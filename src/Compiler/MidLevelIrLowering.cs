using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class MidLevelIrLowerer
{
    private readonly ParseResult _parseResult;
    private readonly TypeCheckModel _typeModel;
    private readonly Dictionary<string, StarkParser.FunctionDeclarationContext> _functionsByName;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<LiteralKey, StarkTypeSymbol> _literalTypes;

    public MidLevelIrLowerer(
        CompilerPassContext context,
        ParseResult parseResult,
        ModuleGraph moduleGraph,
        TypeCheckModel typeModel)
    {
        _parseResult = parseResult;
        _typeModel = typeModel;
        _functionsByName = parseResult.Root.topLevelDeclaration()
            .Select(static declaration => declaration.functionDeclaration())
            .Where(static declaration => declaration is not null)!
            .ToDictionary(static declaration => declaration.Identifier().GetText(), StringComparer.Ordinal);
        _typeResolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, typeModel.NamedTypes);
        _literalTypes = typeModel.Literals
            .GroupBy(static literal => new LiteralKey(literal.LiteralText, literal.Location.Line, literal.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Type);
    }

    public MidLevelIrModule Lower(HighLevelIrModule hir)
    {
        var functions = hir.Functions
            .Select(LowerFunction)
            .ToArray();

        return new MidLevelIrModule(hir.ModuleName, functions);
    }

    private MidLevelIrFunction LowerFunction(HighLevelIrFunction function)
    {
        if (!_functionsByName.TryGetValue(function.Name, out var declaration)
            || declaration.functionBody().block() is not { } body)
        {
            return new MidLevelIrFunction(
                function.Name,
                BuildSignature(function.Signature),
                function.Signature.ReturnType,
                function.Signature.Parameters,
                function.HasBody,
                SupportsDirectCodeGeneration: false,
                EntryBlockId: 0,
                Locals: [],
                Blocks: []);
        }

        var builder = new FunctionMirBuilder(function, _typeModel, _typeResolver, _literalTypes);
        builder.Lower(body);

        return new MidLevelIrFunction(
            function.Name,
            BuildSignature(function.Signature),
            function.Signature.ReturnType,
            function.Signature.Parameters,
            function.HasBody,
            builder.SupportsDirectCodeGeneration,
            builder.EntryBlockId,
            builder.Locals,
            builder.Blocks);
    }

    private static string BuildSignature(TypedFunctionSignature function)
    {
        return $"{function.ReturnType.DisplayName} {function.Name}({string.Join(", ", function.Parameters.Select(static parameter => $"{parameter.Type.DisplayName} {parameter.Name}"))})";
    }

    private readonly record struct LiteralKey(string Text, int Line, int Column);

    private sealed class FunctionMirBuilder
    {
        private sealed record LowerableSwitchLabel(
            string LabelText,
            StarkParser.LiteralContext? Literal,
            StarkParser.ExpressionContext? GuardExpression,
            bool IsDefault,
            bool IsMatchAll,
            string? CaptureName);

        private sealed record LowerableSwitchSection(
            StarkParser.SwitchSectionContext Section,
            IReadOnlyList<LowerableSwitchLabel> Labels);

        private enum PlacePathKind
        {
            Field,
            ConstantArrayIndex,
            DynamicArrayIndex,
            SliceIndex
        }

        private sealed record PlacePathSegment(
            PlacePathKind Kind,
            string? FieldName,
            int? ConstantIndex,
            MidLevelIrOperand? IndexOperand,
            StarkTypeSymbol ParentType,
            StarkTypeSymbol SegmentType);

        private sealed record PlaceTarget(
            string RootName,
            StarkTypeSymbol RootType,
            StarkTypeSymbol Type,
            IReadOnlyList<PlacePathSegment> Path,
            bool UsesAddressModel);

        private sealed record LoweredAssignment(
            string Text,
            string? TargetName,
            StarkTypeSymbol TargetType,
            MidLevelIrRValue? DirectValue,
            MidLevelIrOperand ResultValue,
            MidLevelIrOperand? Address);

        private readonly HighLevelIrFunction _function;
        private readonly TypeCheckModel _typeModel;
        private readonly StarkTypeResolver _typeResolver;
        private readonly IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> _literalTypes;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private int _nextBlockId;
        private int _nextTempId;

        public FunctionMirBuilder(
            HighLevelIrFunction function,
            TypeCheckModel typeModel,
            StarkTypeResolver typeResolver,
            IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> literalTypes)
        {
            _function = function;
            _typeModel = typeModel;
            _typeResolver = typeResolver;
            _literalTypes = literalTypes;
            _parametersByName = function.Signature.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
            CurrentBlock = CreateBlock("entry");
        }

        public bool SupportsDirectCodeGeneration { get; private set; } = true;

        public int EntryBlockId => 0;

        public IReadOnlyList<MidLevelIrLocal> Locals => _locals;

        public IReadOnlyList<MidLevelIrBasicBlock> Blocks => _blocks
            .Select(static block => block.Build())
            .ToArray();

        private BasicBlockBuilder CurrentBlock { get; set; }

        public void Lower(StarkParser.BlockContext body)
        {
            LowerBlock(body);

            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = _function.Signature.ReturnType.Kind == StarkTypeKind.Void
                    ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [])
                    : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: []);
            }
        }

        private void LowerBlock(StarkParser.BlockContext block)
        {
            foreach (var statement in block.statement())
            {
                LowerStatement(statement);
            }
        }

        private void LowerStatement(StarkParser.StatementContext statement)
        {
            if (CurrentBlock.HasTerminator)
            {
                CurrentBlock = CreateBlock("dead");
            }

            if (statement.block() is { } block)
            {
                LowerBlock(block);
                return;
            }

            if (statement.localConstantDeclaration() is { } localConstant)
            {
                LowerConstantDeclaration(localConstant);
                return;
            }

            if (statement.localVariableDeclaration() is { } localVariable)
            {
                LowerVariableDeclaration(localVariable);
                return;
            }

            if (statement.ifStatement() is { } ifStatement)
            {
                LowerIf(ifStatement);
                return;
            }

            if (statement.switchStatement() is { } switchStatement)
            {
                LowerSwitch(switchStatement);
                return;
            }

            if (statement.whileStatement() is { } whileStatement)
            {
                LowerWhile(whileStatement);
                return;
            }

            if (statement.forStatement() is { } forStatement)
            {
                LowerFor(forStatement);
                return;
            }

            if (statement.returnStatement() is { } returnStatement)
            {
                LowerReturn(returnStatement);
                return;
            }

            if (statement.breakStatement() is not null)
            {
                var loop = _loops.Peek();
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.BreakTarget]);
                return;
            }

            if (statement.continueStatement() is not null)
            {
                var loop = _loops.Peek();
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.ContinueTarget]);
                return;
            }

            if (statement.expressionStatement() is { } expressionStatement)
            {
                LowerExpressionStatement(expressionStatement.expression());
            }
        }

        private void LowerConstantDeclaration(StarkParser.LocalConstantDeclarationContext declaration)
        {
            var declaredType = _typeResolver.ResolveType(declaration.type_());
            foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                EmitAssignmentFromExpression(name, declaredType, declarator.expression(), declarator.expression().GetText());
            }
        }

        private void LowerVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
        {
            var declaredType = _typeResolver.ResolveType(declaration.type_());
            var storageClass = declaration.storageClass().GetText();

            foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass, declaration.MUT() is not null, isConstant: false);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);

                if (declarator.variableInitializer() is { } initializer)
                {
                    LowerVariableInitializer(name, declaredType, initializer);
                }
            }
        }

        private void LowerVariableInitializer(string name, StarkTypeSymbol declaredType, StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is { } expression)
            {
                EmitAssignmentFromExpression(name, declaredType, expression, expression.GetText());
                return;
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                var value = LowerObjectInitializer(declaredType, objectInitializer);
                if (value is null)
                {
                    MarkUnsupported();
                    Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
                    return;
                }

                Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType, new MidLevelIrUseRValue(value));
                return;
            }

            if (initializer.arrayInitializer() is { } arrayInitializer)
            {
                var value = LowerArrayInitializer(declaredType, arrayInitializer);
                if (value is null)
                {
                    MarkUnsupported();
                    Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
                    return;
                }

                Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType, new MidLevelIrUseRValue(value));
                return;
            }

            MarkUnsupported();
            Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
        }

        private void LowerReturn(StarkParser.ReturnStatementContext returnStatement)
        {
            if (returnStatement.expression() is null)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: [],
                    ValueText: null,
                    Value: null);
                return;
            }

            var operand = LowerExpressionToOperand(returnStatement.expression(), _function.Signature.ReturnType);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: returnStatement.expression().GetText(),
                Value: operand);
        }

        private void LowerExpressionStatement(StarkParser.ExpressionContext expression)
        {
            if (TryLowerAssignmentExpression(expression.assignmentExpression(), out var assignment))
            {
                EmitAssignment(assignment);
                return;
            }

            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
                return;
            }

            if (LowerExpressionToOperand(expression) is { } operand)
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: new MidLevelIrUseRValue(operand));
                return;
            }

            MarkUnsupported();
            Emit(MidLevelIrStatementKind.Evaluate, expression.GetText());
        }

        private bool TryLowerAssignmentExpression(
            StarkParser.AssignmentExpressionContext expression,
            out LoweredAssignment assignment)
        {
            assignment = default!;

            if (expression.assignmentOperator() is null
                || !TryResolveAssignmentTarget(expression.unaryExpression(), out var target))
            {
                return false;
            }

            var assignmentText = $"{expression.unaryExpression().GetText()} {expression.assignmentOperator().GetText()} {expression.assignmentExpression().GetText()}";

            if (expression.assignmentOperator().GetText() == "=")
            {
                var assignedValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), target.Type);
                if (assignedValue is null)
                {
                    MarkUnsupported();
                    return true;
                }

                assignment = BuildAssignment(target, assignedValue, assignmentText);
                return true;
            }

            var currentValue = ReadPlace(target);
            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                MarkUnsupported();
                return true;
            }

            var @operator = expression.assignmentOperator().GetText() switch
            {
                "+=" => MidLevelIrBinaryOperator.Add,
                "-=" => MidLevelIrBinaryOperator.Subtract,
                "*=" => MidLevelIrBinaryOperator.Multiply,
                "/=" => MidLevelIrBinaryOperator.Divide,
                "%=" => MidLevelIrBinaryOperator.Modulo,
                "&=" => MidLevelIrBinaryOperator.BitwiseAnd,
                "^=" => MidLevelIrBinaryOperator.BitwiseXor,
                "|=" => MidLevelIrBinaryOperator.BitwiseOr,
                _ => throw new InvalidOperationException($"Unsupported assignment operator '{expression.assignmentOperator().GetText()}'.")
            };

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                MarkUnsupported();
                return true;
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");

            assignment = temp is null
                ? default!
                : BuildAssignment(target, CoerceOperand(temp, target.Type) ?? temp, assignmentText);
            if (temp is null)
            {
                MarkUnsupported();
            }

            return true;
        }

        private void LowerIf(StarkParser.IfStatementContext ifStatement)
        {
            var thenBlock = CreateBlock("if_then");
            var elseBlock = ifStatement.statement().Length > 1 ? CreateBlock("if_else") : null;
            var joinBlock = CreateBlock("if_join");
            var condition = LowerExpressionToOperand(ifStatement.expression(), StarkTypeSymbols.Bool);

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                elseBlock is null ? [thenBlock.Id, joinBlock.Id] : [thenBlock.Id, elseBlock.Id],
                ConditionText: ifStatement.expression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            LowerStatement(ifStatement.statement(0));
            EnsureGoto(joinBlock.Id);

            if (elseBlock is not null)
            {
                CurrentBlock = elseBlock;
                LowerStatement(ifStatement.statement(1));
                EnsureGoto(joinBlock.Id);
            }

            CurrentBlock = joinBlock;
        }

        private void LowerSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            if (TryLowerNativeSwitch(switchStatement) || TryLowerGuardedSwitch(switchStatement))
            {
                return;
            }

            MarkUnsupported();

            var exitBlock = CreateBlock("switch_exit");
            var sectionBlocks = switchStatement.switchSection()
                .Select((section, index) => (Section: section, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();

            var cases = new List<MidLevelIrSwitchCase>();
            foreach (var (section, block) in sectionBlocks)
            {
                foreach (var label in section.switchLabel())
                {
                    var labelText = label.DEFAULT() is not null ? "default" : label.GetText();
                    cases.Add(new MidLevelIrSwitchCase(labelText, block.Id));
                }
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                sectionBlocks.Select(static item => item.Block.Id).Append(exitBlock.Id).ToArray(),
                ConditionText: switchStatement.expression().GetText(),
                SwitchCases: cases);

            foreach (var (section, block) in sectionBlocks)
            {
                CurrentBlock = block;
                foreach (var nested in section.statement())
                {
                    LowerStatement(nested);
                }

                EnsureGoto(exitBlock.Id);
            }

            CurrentBlock = exitBlock;
        }

        private bool TryLowerNativeSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            var switchValue = LowerExpressionToOperand(switchStatement.expression());
            if (switchValue is null || !CanUseNativeSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var allLabels = parsedSections
                .SelectMany(static section => section.Labels)
                .ToArray();

            if (allLabels.Any(static label => label.IsMatchAll && !label.IsDefault))
            {
                return false;
            }

            var nativeLabels = allLabels
                .Where(static label => !label.IsMatchAll)
                .ToArray();

            if (nativeLabels.Length == 0
                || nativeLabels.Any(static label => label.GuardExpression is not null || label.Literal is null || label.CaptureName is not null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var switchCases = new List<MidLevelIrSwitchCase>();
            int? defaultTarget = null;

            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault)
                    {
                        defaultTarget ??= section.Block.Id;
                        continue;
                    }

                    if (label.GuardExpression is not null || label.Literal is null)
                    {
                        return false;
                    }

                    var matchValue = LowerSwitchCaseLiteral(label.Literal, switchValue.Type);
                    if (matchValue is null || !CanUseNativeSwitchCase(matchValue.Type, switchValue.Type))
                    {
                        return false;
                    }

                    switchCases.Add(new MidLevelIrSwitchCase(label.LabelText, section.Block.Id, matchValue));
                }
            }

            var resolvedDefaultTarget = defaultTarget ?? exitBlock.Id;
            if (switchCases.Count == 0)
            {
                return false;
            }

            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(resolvedDefaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: switchStatement.expression().GetText(),
                Condition: switchValue,
                SwitchCases: switchCases,
                DefaultTarget: resolvedDefaultTarget);

            foreach (var section in sections)
            {
                CurrentBlock = section.Block;
                foreach (var nested in section.Section.statement())
                {
                    LowerStatement(nested);
                }

                EnsureGoto(exitBlock.Id);
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerGuardedSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            var switchValue = LowerExpressionToOperand(switchStatement.expression());
            if (switchValue is null || !CanLowerSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (
                    section.Section,
                    section.Labels,
                    EntryBlock: CreateBlock($"switch_test_{index}"),
                    BodyBlock: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault && label.GuardExpression is null && label.CaptureName is null))
                .Select(static section => section.BodyBlock.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryRegisterSwitchCaptureLocals(sections, switchValue.Type))
            {
                return false;
            }

            if (sections.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Goto,
                    [sections[0].EntryBlock.Id]);

                for (var index = 0; index < sections.Length; index++)
                {
                    CurrentBlock = sections[index].EntryBlock;
                    var nextSectionTarget = index + 1 < sections.Length ? sections[index + 1].EntryBlock.Id : defaultTarget;

                    if (!EmitSwitchSectionDecision(
                        sections[index].Labels,
                        switchValue,
                        sections[index].BodyBlock.Id,
                        nextSectionTarget,
                        switchStatement.expression().GetText(),
                        index))
                    {
                        return false;
                    }
                }
            }

            foreach (var section in sections)
            {
                CurrentBlock = section.BodyBlock;
                foreach (var nested in section.Section.statement())
                {
                    LowerStatement(nested);
                }

                EnsureGoto(exitBlock.Id);
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool EmitSwitchSectionDecision(
            IReadOnlyList<LowerableSwitchLabel> labels,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextSectionTarget,
            string switchText,
            int sectionIndex)
        {
            if (labels.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextSectionTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[labels.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < labels.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_test_{sectionIndex}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : nextSectionTarget;

                if (label.IsMatchAll)
                {
                    if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                    {
                        return false;
                    }

                    continue;
                }

                var literalOperand = LowerSwitchCaseLiteral(label.Literal!, switchValue.Type);
                if (literalOperand is null)
                {
                    return false;
                }

                var condition = EmitEqualityComparison(
                    switchValue,
                    literalOperand,
                    $"switch {switchText} == {label.LabelText}");
                if (condition is null)
                {
                    return false;
                }

                if (label.GuardExpression is null && label.CaptureName is null)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: label.LabelText,
                        Condition: condition);
                    continue;
                }

                var matchBlock = CreateBlock($"switch_match_{sectionIndex}_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [matchBlock.Id, nextTarget],
                    ConditionText: label.LabelText,
                    Condition: condition);

                CurrentBlock = matchBlock;
                if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryParseLowerableSwitchSections(
            StarkParser.SwitchStatementContext switchStatement,
            out List<LowerableSwitchSection> sections,
            out int defaultSectionCount)
        {
            sections = [];
            defaultSectionCount = 0;

            foreach (var section in switchStatement.switchSection())
            {
                var labels = new List<LowerableSwitchLabel>();

                foreach (var label in section.switchLabel())
                {
                    if (label.DEFAULT() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel("default", null, null, IsDefault: true, IsMatchAll: true, CaptureName: null));
                        defaultSectionCount++;
                        continue;
                    }

                    var pattern = label.pattern();
                    if (pattern is null)
                    {
                        return false;
                    }

                    if (pattern.DISCARD() is not null)
                    {
                        if (label.whenClause() is null)
                        {
                            labels.Add(new LowerableSwitchLabel(pattern.GetText(), null, null, IsDefault: true, IsMatchAll: true, CaptureName: null));
                            defaultSectionCount++;
                            continue;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: null));
                        continue;
                    }

                    if (pattern.VAR() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: pattern.Identifier()?.GetText()));
                        continue;
                    }

                    if (pattern.literal() is not { } literal)
                    {
                        return false;
                    }

                    labels.Add(new LowerableSwitchLabel(
                        literal.GetText(),
                        literal,
                        label.whenClause()?.expression(),
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null));
                }

                sections.Add(new LowerableSwitchSection(section, labels));
            }

            return true;
        }

        private bool TryRegisterSwitchCaptureLocals(
            IEnumerable<(StarkParser.SwitchSectionContext Section, IReadOnlyList<LowerableSwitchLabel> Labels, BasicBlockBuilder EntryBlock, BasicBlockBuilder BodyBlock)> sections,
            StarkTypeSymbol switchType)
        {
            foreach (var section in sections)
            {
                var captureLabels = section.Labels.Where(static label => label.CaptureName is not null).ToArray();
                if (captureLabels.Length == 0)
                {
                    continue;
                }

                if (captureLabels.Length != 1 || section.Labels.Count != 1)
                {
                    return false;
                }

                var captureName = captureLabels[0].CaptureName!;
                if (_localsByName.ContainsKey(captureName) || _parametersByName.ContainsKey(captureName))
                {
                    return false;
                }

                RegisterLocal(captureName, switchType, storageClass: "match", isMutable: false, isConstant: false);
            }

            return true;
        }

        private bool EmitSwitchMatchTransition(LowerableSwitchLabel label, MidLevelIrOperand switchValue, int targetBlockId, int nextTarget)
        {
            if (label.GuardExpression is null)
            {
                if (label.CaptureName is not null)
                {
                    var capture = new MidLevelIrLocalOperand(label.CaptureName, switchValue.Type);
                    EmitOperandAssignment(capture, switchValue, switchValue.Text);
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            var guard = LowerExpressionToOperand(label.GuardExpression, StarkTypeSymbols.Bool);
            if (guard is null)
            {
                return false;
            }

            if (label.CaptureName is not null)
            {
                var captureBlock = CreateBlock("switch_bind");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [captureBlock.Id, nextTarget],
                    ConditionText: label.GuardExpression.GetText(),
                    Condition: guard);

                CurrentBlock = captureBlock;
                var capture = new MidLevelIrLocalOperand(label.CaptureName, switchValue.Type);
                EmitOperandAssignment(capture, switchValue, switchValue.Text);
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [targetBlockId, nextTarget],
                ConditionText: label.GuardExpression.GetText(),
                Condition: guard);
            return true;
        }

        private void LowerWhile(StarkParser.WhileStatementContext whileStatement)
        {
            var conditionBlock = CreateBlock($"while_{whileStatement.loopBehavior().GetText()}_cond");
            var bodyBlock = CreateBlock("while_body");
            var exitBlock = CreateBlock("while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: whileStatement.expression().GetText(),
                Condition: LowerExpressionToOperand(whileStatement.expression(), StarkTypeSymbols.Bool));

            _loops.Push(new LoopTargets(conditionBlock.Id, exitBlock.Id));
            CurrentBlock = bodyBlock;
            LowerStatement(whileStatement.statement());
            _loops.Pop();
            EnsureGoto(conditionBlock.Id);

            CurrentBlock = exitBlock;
        }

        private void LowerFor(StarkParser.ForStatementContext forStatement)
        {
            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
            {
                var declaredType = _typeResolver.ResolveType(localForVariableDeclaration.type_());
                var storageClass = localForVariableDeclaration.storageClass().GetText();

                foreach (var declarator in localForVariableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    RegisterLocal(name, declaredType, storageClass, localForVariableDeclaration.MUT() is not null, isConstant: false);
                    Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                    if (declarator.variableInitializer() is { } initializer)
                    {
                        LowerVariableInitializer(name, declaredType, initializer);
                    }
                }
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    LowerExpressionStatement(expression);
                }
            }

            var conditionBlock = CreateBlock($"for_{forStatement.loopBehavior().GetText()}_cond");
            var bodyBlock = CreateBlock("for_body");
            var iteratorBlock = CreateBlock("for_iter");
            var exitBlock = CreateBlock("for_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            if (forStatement.forCondition() is { } condition)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [bodyBlock.Id, exitBlock.Id],
                    ConditionText: condition.expression().GetText(),
                    Condition: LowerExpressionToOperand(condition.expression(), StarkTypeSymbols.Bool));
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bodyBlock.Id]);
            }

            _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id));
            CurrentBlock = bodyBlock;
            LowerStatement(forStatement.statement());
            _loops.Pop();
            EnsureGoto(iteratorBlock.Id);

            CurrentBlock = iteratorBlock;
            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    LowerExpressionStatement(expression);
                }
            }

            EnsureGoto(conditionBlock.Id);
            CurrentBlock = exitBlock;
        }

        private MidLevelIrOperand? LowerExpressionToOperand(StarkParser.ExpressionContext expression, StarkTypeSymbol? expectedType = null)
        {
            var operand = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), expectedType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private MidLevelIrOperand? LowerAssignmentExpressionToOperand(
            StarkParser.AssignmentExpressionContext expression,
            StarkTypeSymbol? expectedType = null)
        {
            if (expression.conditionalExpression() is { } conditionalExpression)
            {
                return LowerConditionalExpression(conditionalExpression, expectedType);
            }

            if (!TryLowerAssignmentExpression(expression, out var assignment))
            {
                MarkUnsupported();
                return null;
            }

            EmitAssignment(assignment);
            return assignment.ResultValue;
        }

        private MidLevelIrOperand? LowerConditionalExpression(
            StarkParser.ConditionalExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.expression().Length == 0)
            {
                return LowerLogicalOrExpression(expression.logicalOrExpression(), expectedType);
            }

            if (expression.expression().Length != 2)
            {
                MarkUnsupported();
                return null;
            }

            var condition = LowerLogicalOrExpression(expression.logicalOrExpression(), StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return null;
            }

            var thenBlock = CreateBlock("cond_true");
            var elseBlock = CreateBlock("cond_false");
            var joinBlock = CreateBlock("cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: expression.logicalOrExpression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            var trueValue = LowerExpressionToOperand(expression.expression(0), expectedType);
            var trueBlock = CurrentBlock;
            if (trueValue is null)
            {
                return null;
            }

            CurrentBlock = elseBlock;
            var falseValue = LowerExpressionToOperand(expression.expression(1), expectedType);
            var falseBlock = CurrentBlock;
            if (falseValue is null)
            {
                return null;
            }

            var resultType = expectedType ?? FindCommonType(trueValue.Type, falseValue.Type);
            if (resultType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported();
                return null;
            }

            var result = CreateTemporaryLocal(resultType, "cond");

            CurrentBlock = trueBlock;
            var coercedTrue = CoerceOperand(trueValue, resultType);
            if (coercedTrue is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedTrue, expression.expression(0).GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = falseBlock;
            var coercedFalse = CoerceOperand(falseValue, resultType);
            if (coercedFalse is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedFalse, expression.expression(1).GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? LowerLogicalOrExpression(
            StarkParser.LogicalOrExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.logicalAndExpression().Length == 1)
            {
                return LowerLogicalAndExpression(expression.logicalAndExpression(0), expectedType);
            }

            return LowerShortCircuitBooleanChain(
                expression.logicalAndExpression(),
                item => LowerLogicalAndExpression(item, StarkTypeSymbols.Bool),
                shortCircuitOnTrue: true,
                resultHint: "or");
        }

        private MidLevelIrOperand? LowerLogicalAndExpression(
            StarkParser.LogicalAndExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.bitwiseOrExpression().Length == 1)
            {
                return LowerBitwiseOrExpression(expression.bitwiseOrExpression(0), expectedType);
            }

            return LowerShortCircuitBooleanChain(
                expression.bitwiseOrExpression(),
                item => LowerBitwiseOrExpression(item, StarkTypeSymbols.Bool),
                shortCircuitOnTrue: false,
                resultHint: "and");
        }

        private MidLevelIrOperand? LowerBitwiseOrExpression(
            StarkParser.BitwiseOrExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.bitwiseXorExpression();
            var operators = ExtractOperators<StarkParser.BitwiseXorExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerBitwiseXorExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerBitwiseXorExpression(
            StarkParser.BitwiseXorExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.bitwiseAndExpression();
            var operators = ExtractOperators<StarkParser.BitwiseAndExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerBitwiseAndExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerBitwiseAndExpression(
            StarkParser.BitwiseAndExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.equalityExpression();
            var operators = ExtractOperators<StarkParser.EqualityExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerEqualityExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerEqualityExpression(
            StarkParser.EqualityExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.relationalExpression();
            var operators = ExtractOperators<StarkParser.RelationalExpressionContext>(expression);
            return LowerComparisonChain(
                operands,
                operators,
                item => LowerRelationalExpression(item, expectedType));
        }

        private MidLevelIrOperand? LowerRelationalExpression(
            StarkParser.RelationalExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.shiftExpression();
            var operators = ExtractOperators<StarkParser.ShiftExpressionContext>(expression);
            return LowerComparisonChain(
                operands,
                operators,
                item => LowerShiftExpression(item, expectedType));
        }

        private MidLevelIrOperand? LowerShiftExpression(
            StarkParser.ShiftExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.additiveExpression();
            var operators = ExtractOperators<StarkParser.AdditiveExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerAdditiveExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: true,
                expectedType);
        }

        private MidLevelIrOperand? LowerAdditiveExpression(
            StarkParser.AdditiveExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.multiplicativeExpression();
            var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerMultiplicativeExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: false,
                expectedType);
        }

        private MidLevelIrOperand? LowerMultiplicativeExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.unaryExpression();
            var operators = ExtractOperators<StarkParser.UnaryExpressionContext>(expression);
            return LowerBinaryChain(
                operands,
                operators,
                item => LowerUnaryExpression(item, expectedType),
                MapBinaryOperator,
                requireInteger: false,
                expectedType);
        }

        private MidLevelIrOperand? LowerUnaryExpression(StarkParser.UnaryExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (expression.powerExpression() is { } powerExpression)
            {
                return LowerPowerExpression(powerExpression, expectedType);
            }

            var operand = LowerUnaryExpression(expression.unaryExpression(), expectedType);
            if (operand is null)
            {
                return null;
            }

            var op = expression.GetChild(0).GetText();
            return op switch
            {
                "+" => operand,
                "-" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.Negate, operand, operand.Type, expression.GetText()),
                    "neg"),
                "!" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.LogicalNot, CoerceOperand(operand, StarkTypeSymbols.Bool) ?? operand, StarkTypeSymbols.Bool, expression.GetText()),
                    "not"),
                "~" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.BitwiseNot, operand, operand.Type, expression.GetText()),
                    "bitnot"),
                _ => UnsupportedOperand()
            };
        }

        private MidLevelIrOperand? LowerPowerExpression(StarkParser.PowerExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            var left = LowerPostfixExpression(expression.postfixExpression(), expectedType: null);
            if (left is null)
            {
                return null;
            }

            if (expression.unaryExpression() is not { } rightExpression)
            {
                return expectedType is null ? left : CoerceOperand(left, expectedType);
            }

            var right = LowerUnaryExpression(rightExpression, expectedType: null);
            if (right is null)
            {
                return null;
            }

            var resultType = FindCommonType(left.Type, right.Type);
            if (resultType.Kind != StarkTypeKind.Float)
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, resultType);
            var coercedRight = CoerceOperand(right, resultType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            var result = EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Exponent,
                    coercedLeft,
                    coercedRight,
                    resultType,
                    expression.GetText()),
                "pow");

            if (result is null)
            {
                return null;
            }

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerPostfixExpression(StarkParser.PostfixExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (TryLowerCallExpression(expression, out var call))
            {
                if (call.Type.Kind == StarkTypeKind.Void)
                {
                    MarkUnsupported();
                    return null;
                }

                return EmitTemporary(call, "call");
            }

            var current = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (current is null)
            {
                return null;
            }

            foreach (var postfixPart in expression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    MarkUnsupported();
                    return null;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    current = LowerIndexAccess(current, expressionList);
                }
                else
                {
                    current = LowerFieldAccess(current, postfixPart.Identifier().GetText());
                }

                if (current is null)
                {
                    return null;
                }
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private MidLevelIrOperand? LowerPrimaryExpression(StarkParser.PrimaryExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (expression.literal() is { } literal)
            {
                return LowerLiteral(literal, expectedType);
            }

            if (expression.Identifier() is { } identifier)
            {
                return ResolveNamedOperand(identifier.GetText());
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                return ResolveNamedOperand(qualifiedName.GetText());
            }

            if (expression.objectCreationExpression() is { } objectCreationExpression)
            {
                return LowerObjectCreationExpression(objectCreationExpression, expectedType);
            }

            return LowerExpressionToOperand(expression.expression(), expectedType);
        }

        private MidLevelIrOperand? LowerObjectCreationExpression(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.argumentList() is { } argumentList && argumentList.argument().Length != 0)
            {
                MarkUnsupported();
                return null;
            }

            var createdType = _typeResolver.ResolveType(expression.type_());
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            if (expression.objectInitializer() is { } objectInitializer)
            {
                var initialized = LowerObjectInitializer(createdType, objectInitializer);
                if (initialized is null)
                {
                    return null;
                }

                current = initialized;
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private MidLevelIrOperand? LowerObjectInitializer(StarkTypeSymbol targetType, StarkParser.ObjectInitializerContext objectInitializer)
        {
            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType))
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);

            foreach (var initializer in objectInitializer.memberInitializer())
            {
                if (!namedType.TryGetField(initializer.Identifier().GetText(), out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var value = LowerExpressionToOperand(initializer.expression(), field.Type);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        value,
                        targetType,
                        $"{current.Text}.{field.Name} = {initializer.expression().GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerArrayInitializer(StarkTypeSymbol targetType, StarkParser.ArrayInitializerContext arrayInitializer)
        {
            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);
            var elementCount = Math.Min(fixedLength, arrayInitializer.expression().Length);

            for (var index = 0; index < elementCount; index++)
            {
                var value = LowerExpressionToOperand(arrayInitializer.expression(index), targetType.ElementType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        current,
                        index,
                        value,
                        targetType,
                        $"{current.Text}[{index}] = {arrayInitializer.expression(index).GetText()}"),
                    "insertindex");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerFieldAccess(MidLevelIrOperand target, string memberName)
        {
            if (!TryResolveField(target.Type, memberName, out var field, out var fieldIndex))
            {
                MarkUnsupported();
                return null;
            }

            return EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    field.Name,
                    fieldIndex,
                    field.Type,
                    $"{target.Text}.{field.Name}"),
                "field");
        }

        private MidLevelIrOperand? LowerIndexAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var current = target;

            foreach (var indexExpression in indexes.expression())
            {
                if (current.Type.Kind == StarkTypeKind.FixedArray)
                {
                    if (TryResolveConstantArrayIndex(current.Type, indexExpression, out var constantIndex, out var elementType))
                    {
                        var extracted = EmitTemporary(
                            new MidLevelIrExtractIndexRValue(
                                current,
                                constantIndex,
                                elementType,
                                $"{current.Text}[{constantIndex}]"),
                            "index");
                        if (extracted is null)
                        {
                            return null;
                        }

                        current = extracted;
                        continue;
                    }

                    if (current is not MidLevelIrLocalOperand local || !IsAddressableLocal(local.Name) || current.Type.ElementType is null)
                    {
                        MarkUnsupported();
                        return null;
                    }

                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported();
                        return null;
                    }

                    var baseAddress = EmitTemporary(
                        new MidLevelIrAddressOfLocalRValue(local.Name, local.Type, AddressType(local.Type), $"&{local.Name}"),
                        "addr");
                    if (baseAddress is null)
                    {
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            baseAddress,
                            current.Type,
                            index,
                            ConstantIndex: null,
                            AddressType(current.Type.ElementType),
                            $"{local.Name}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            current.Type.ElementType,
                            $"{local.Name}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.Slice && current.Type.ElementType is not null)
                {
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported();
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrSliceElementAddressRValue(
                            current,
                            index,
                            AddressType(current.Type.ElementType),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            current.Type.ElementType,
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                MarkUnsupported();
                return null;
            }

            return current;
        }

        private bool TryLowerCallExpression(StarkParser.PostfixExpressionContext expression, out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.postfixPart().Length == 0
                || expression.postfixPart()[^1].argumentList() is not { } arguments)
            {
                return false;
            }

            string? functionName = null;
            if (expression.primaryExpression().Identifier() is { } identifier)
            {
                functionName = identifier.GetText();
            }
            else if (expression.primaryExpression().qualifiedName() is { } qualifiedName)
            {
                functionName = qualifiedName.GetText();
            }

            if (functionName is null)
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length - 1; index++)
            {
                var postfixPart = expression.postfixPart()[index];
                if (postfixPart.argumentList() is not null || postfixPart.expressionList() is not null || postfixPart.Identifier() is null)
                {
                    return false;
                }

                functionName = $"{functionName}.{postfixPart.Identifier().GetText()}";
            }

            if (functionName is null || !_typeModel.Functions.TryGetValue(functionName, out var signature))
            {
                MarkUnsupported();
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>();
            for (var index = 0; index < Math.Min(arguments.argument().Length, signature.Parameters.Count); index++)
            {
                var parameterType = signature.Parameters[index].Type;
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), parameterType);
                if (argument is null)
                {
                    MarkUnsupported();
                    return false;
                }

                loweredArguments.Add(argument);
            }

            if (arguments.argument().Length != signature.Parameters.Count)
            {
                MarkUnsupported();
                return false;
            }

            call = new MidLevelIrCallRValue(functionName, loweredArguments, signature.ReturnType, expression.GetText());
            return true;
        }

        private MidLevelIrOperand? LowerLiteral(StarkParser.LiteralContext literal, StarkTypeSymbol? expectedType)
        {
            var literalType = LookupLiteralType(literal);
            if (literal.CharacterLiteral() is not null)
            {
                var characterOperand = new MidLevelIrStringConstantOperand(literal.GetText(), literalType);
                return expectedType is null ? characterOperand : CoerceOperand(characterOperand, expectedType);
            }

            var operand = CreateLiteralOperand(literal, literalType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private StarkTypeSymbol LookupLiteralType(StarkParser.LiteralContext literal)
        {
            var key = new LiteralKey(literal.GetText(), literal.Start.Line, literal.Start.Column + 1);
            return _literalTypes.TryGetValue(key, out var type)
                ? type
                : literal.TRUE() is not null || literal.FALSE() is not null
                    ? StarkTypeSymbols.Bool
                    : literal.NULL() is not null
                        ? StarkTypeSymbols.Null
                        : literal.FloatLiteral() is not null
                            ? StarkTypeSymbols.Float(32)
                            : InferIntegerLiteralType(ParseIntegerLiteral(literal.signedIntegerLiteral()!));
        }

        private static MidLevelIrOperand CreateLiteralOperand(StarkParser.LiteralContext literal, StarkTypeSymbol type)
        {
            if (literal.signedIntegerLiteral() is { } integerLiteral)
            {
                return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteral(integerLiteral), type);
            }

            if (literal.FloatLiteral() is { } floatLiteral)
            {
                return new MidLevelIrFloatConstantOperand(floatLiteral.GetText(), type);
            }

            if (literal.StringLiteral() is { } stringLiteral)
            {
                return new MidLevelIrStringConstantOperand(stringLiteral.GetText(), type);
            }

            if (literal.TRUE() is not null)
            {
                return new MidLevelIrBoolConstantOperand(true);
            }

            if (literal.FALSE() is not null)
            {
                return new MidLevelIrBoolConstantOperand(false);
            }

            if (literal.NULL() is not null)
            {
                return new MidLevelIrNullOperand(type);
            }

            throw new InvalidOperationException($"Unsupported literal '{literal.GetText()}'.");
        }

        private MidLevelIrOperand? ResolveNamedOperand(string name)
        {
            if (_localsByName.TryGetValue(name, out var local))
            {
                return new MidLevelIrLocalOperand(local.Name, local.Type);
            }

            if (_parametersByName.TryGetValue(name, out var parameter))
            {
                return new MidLevelIrParameterOperand(parameter.Name, parameter.Type);
            }

            if (_typeModel.Globals.TryGetValue(name, out var global))
            {
                return new MidLevelIrGlobalOperand(name, global);
            }

            if (_typeModel.Functions.ContainsKey(name))
            {
                MarkUnsupported();
                return null;
            }

            MarkUnsupported();
            return null;
        }

        private bool TryResolveAssignmentTarget(StarkParser.UnaryExpressionContext expression, out PlaceTarget target)
        {
            target = default!;

            if (expression.powerExpression() is not { } powerExpression
                || powerExpression.unaryExpression() is not null
                || powerExpression.postfixExpression() is not { } postfixExpression)
            {
                return false;
            }

            MidLevelIrOperand? root;
            if (postfixExpression.primaryExpression().Identifier() is { } identifier)
            {
                root = ResolveNamedOperand(identifier.GetText());
            }
            else if (postfixExpression.primaryExpression().qualifiedName() is { } qualifiedName)
            {
                root = ResolveNamedOperand(qualifiedName.GetText());
            }
            else
            {
                return false;
            }

            if (root is null)
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var currentType = root.Type;
            var supportsAddressModel = root is MidLevelIrLocalOperand localRoot && IsAddressableLocal(localRoot.Name);
            var usesAddressModel = false;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && supportsAddressModel)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer || currentType.ElementType is null)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                if (!TryResolveField(currentType, postfixPart.Identifier().GetText(), out var field, out var fieldIndex))
                {
                    return false;
                }

                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    postfixPart.Identifier().GetText(),
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: field.Type));
                currentType = field.Type;
                supportsAddressModel = supportsAddressModel || usesAddressModel;
            }

            target = new PlaceTarget(root.Text, root.Type, currentType, path, usesAddressModel);
            return true;
        }

        private MidLevelIrOperand ReadPlace(PlaceTarget target)
        {
            if (target.UsesAddressModel)
            {
                var address = BuildAddress(target);
                if (address is null)
                {
                    MarkUnsupported();
                    return ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
                }

                return EmitTemporary(
                           new MidLevelIrLoadIndirectRValue(address, target.Type, $"{target.RootName}:load"),
                           "load")
                       ?? address;
            }

            var current = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            foreach (var segment in target.Path)
            {
                var extracted = segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? LowerConstantIndexAccess(current, segment.ConstantIndex!.Value, segment.SegmentType)
                    : LowerFieldAccess(current, segment.FieldName!);
                if (extracted is null)
                {
                    MarkUnsupported();
                    return current;
                }

                current = extracted;
            }

            return current;
        }

        private LoweredAssignment BuildAssignment(PlaceTarget target, MidLevelIrOperand value, string text)
        {
            var assignedValue = CoerceOperand(value, target.Type) ?? value;
            if (target.UsesAddressModel)
            {
                var address = BuildAddress(target);
                return new LoweredAssignment(
                    text,
                    TargetName: null,
                    target.Type,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: address);
            }

            if (target.Path.Count == 0)
            {
                return new LoweredAssignment(
                    text,
                    target.RootName,
                    target.RootType,
                    new MidLevelIrUseRValue(assignedValue),
                    assignedValue,
                    Address: null);
            }

            var root = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            var updatedRoot = ApplyAggregatePathUpdate(root, target.Path, 0, assignedValue, text);
            return new LoweredAssignment(
                text,
                target.RootName,
                target.RootType,
                updatedRoot is null ? null : new MidLevelIrUseRValue(updatedRoot),
                assignedValue,
                Address: null);
        }

        private MidLevelIrOperand? ApplyAggregatePathUpdate(
            MidLevelIrOperand aggregate,
            IReadOnlyList<PlacePathSegment> path,
            int depth,
            MidLevelIrOperand value,
            string text)
        {
            var segment = path[depth];
            if (depth == path.Count - 1)
            {
                var coercedValue = CoerceOperand(value, segment.SegmentType);
                if (coercedValue is null)
                {
                    return null;
                }

                return segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? EmitTemporary(
                        new MidLevelIrInsertIndexRValue(
                            aggregate,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setindex")
                    : EmitTemporary(
                        new MidLevelIrInsertFieldRValue(
                            aggregate,
                            segment.FieldName!,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setfield");
            }

            var nested = segment.Kind == PlacePathKind.ConstantArrayIndex
                ? LowerConstantIndexAccess(aggregate, segment.ConstantIndex!.Value, segment.SegmentType)
                : LowerFieldAccess(aggregate, segment.FieldName!);
            if (nested is null)
            {
                return null;
            }

            var updatedNested = ApplyAggregatePathUpdate(nested, path, depth + 1, value, text);
            if (updatedNested is null)
            {
                return null;
            }

            return segment.Kind == PlacePathKind.ConstantArrayIndex
                ? EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        aggregate,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setindex")
                : EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        aggregate,
                        segment.FieldName!,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setfield");
        }

        private MidLevelIrOperand? BuildAddress(PlaceTarget target)
        {
            MidLevelIrOperand? currentValue = ResolveNamedOperand(target.RootName);
            MidLevelIrOperand? currentAddress = currentValue is MidLevelIrLocalOperand local && IsAddressableLocal(local.Name)
                ? EmitTemporary(
                    new MidLevelIrAddressOfLocalRValue(local.Name, local.Type, AddressType(local.Type), $"&{local.Name}"),
                    "addr")
                : null;
            var currentType = target.RootType;

            foreach (var segment in target.Path)
            {
                switch (segment.Kind)
                {
                    case PlacePathKind.Field:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        currentAddress = EmitTemporary(
                            new MidLevelIrFieldAddressRValue(
                                currentAddress,
                                currentType,
                                segment.FieldName!,
                                segment.ConstantIndex!.Value,
                                AddressType(segment.SegmentType),
                                $"{currentAddress.Text}.{segment.FieldName}"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                    case PlacePathKind.ConstantArrayIndex:
                    case PlacePathKind.DynamicArrayIndex:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                currentAddress,
                                currentType,
                                segment.IndexOperand,
                                segment.ConstantIndex,
                                AddressType(segment.SegmentType),
                                $"{currentAddress.Text}[{segment.ConstantIndex?.ToString() ?? segment.IndexOperand?.Text ?? "?"}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                    case PlacePathKind.SliceIndex:
                        var sliceValue = currentValue;
                        if (sliceValue is null && currentAddress is not null)
                        {
                            sliceValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (sliceValue is null || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        currentAddress = EmitTemporary(
                            new MidLevelIrSliceElementAddressRValue(
                                sliceValue,
                                segment.IndexOperand,
                                AddressType(segment.SegmentType),
                                $"{sliceValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                }

                if (currentAddress is null)
                {
                    return null;
                }
            }

            return currentAddress;
        }

        private bool TryResolveField(StarkTypeSymbol targetType, string memberName, out FieldSymbol field, out int fieldIndex)
        {
            field = default!;
            fieldIndex = -1;

            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType))
            {
                return false;
            }

            return namedType.TryGetField(memberName, out field, out fieldIndex);
        }

        private MidLevelIrOperand? LowerConstantIndexAccess(MidLevelIrOperand target, int constantIndex, StarkTypeSymbol elementType)
        {
            return EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    constantIndex,
                    elementType,
                    $"{target.Text}[{constantIndex}]"),
                "index");
        }

        private bool TryResolveConstantArrayIndex(
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            out int constantIndex,
            out StarkTypeSymbol elementType)
        {
            constantIndex = -1;
            elementType = StarkTypeSymbols.Error;

            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                return false;
            }

            var literal = TryGetSimpleLiteral(expression);
            if (literal?.signedIntegerLiteral() is not { } integerLiteral)
            {
                return false;
            }

            var parsed = ParseIntegerLiteral(integerLiteral);
            if (parsed < 0 || parsed > int.MaxValue)
            {
                return false;
            }

            constantIndex = (int)parsed;
            if (constantIndex >= fixedLength)
            {
                return false;
            }

            elementType = targetType.ElementType;
            return true;
        }

        private static StarkParser.LiteralContext? TryGetSimpleLiteral(StarkParser.ExpressionContext expression)
        {
            if (TryGetSimplePostfixExpression(expression) is not { } postfix || postfix.postfixPart().Length != 0)
            {
                return null;
            }

            return postfix.primaryExpression().literal();
        }

        private MidLevelIrOperand? LowerBinaryChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            IReadOnlyList<string> operators,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand,
            Func<string, MidLevelIrBinaryOperator> mapOperator,
            bool requireInteger,
            StarkTypeSymbol? expectedType)
            where TOperandContext : ParserRuleContext
        {
            var current = lowerOperand(operands[0]);
            if (current is null)
            {
                return null;
            }

            if (operators.Count == 0)
            {
                return expectedType is null ? current : CoerceOperand(current, expectedType);
            }

            for (var index = 1; index < operands.Count; index++)
            {
                var next = lowerOperand(operands[index]);
                if (next is null)
                {
                    return null;
                }

                var resultType = FindCommonType(current.Type, next.Type);
                if (requireInteger && resultType.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported();
                    return null;
                }

                var left = CoerceOperand(current, resultType);
                var right = CoerceOperand(next, resultType);
                if (left is null || right is null)
                {
                    return null;
                }

                current = EmitTemporary(
                    new MidLevelIrBinaryRValue(mapOperator(operators[index - 1]), left, right, resultType, operators[index - 1]),
                    "bin");

                if (current is null)
                {
                    return null;
                }
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private MidLevelIrOperand? LowerComparisonChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            IReadOnlyList<string> operators,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand)
            where TOperandContext : ParserRuleContext
        {
            var left = lowerOperand(operands[0]);
            if (left is null)
            {
                return null;
            }

            if (operators.Count == 0)
            {
                return left;
            }

            if (operators.Count != 1 || operands.Count != 2)
            {
                MarkUnsupported();
                return null;
            }

            var right = lowerOperand(operands[1]);
            if (right is null)
            {
                return null;
            }

            var operandType = FindCommonType(left.Type, right.Type);
            if (operandType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, operandType);
            var coercedRight = CoerceOperand(right, operandType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MapBinaryOperator(operators[0]),
                    coercedLeft,
                    coercedRight,
                    StarkTypeSymbols.Bool,
                    operators[0]),
                "cmp");
        }

        private MidLevelIrOperand? CoerceOperand(MidLevelIrOperand? operand, StarkTypeSymbol targetType)
        {
            if (operand is null || targetType.Kind == StarkTypeKind.Error || operand.Type.Kind == StarkTypeKind.Error)
            {
                return operand;
            }

            if (HasSameStorageType(operand.Type, targetType))
            {
                return operand;
            }

            if (operand.Type.Kind == StarkTypeKind.Null && targetType.Kind == StarkTypeKind.RawPointer)
            {
                return new MidLevelIrNullOperand(targetType);
            }

            if (operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "numcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "floatcast");
            }

            if (operand.Type.Kind == StarkTypeKind.FixedArray
                && targetType.Kind == StarkTypeKind.Slice
                && operand is MidLevelIrLocalOperand localOperand
                && IsAddressableLocal(localOperand.Name))
            {
                return EmitTemporary(
                    new MidLevelIrMakeSliceFromLocalRValue(
                        localOperand.Name,
                        operand.Type,
                        targetType,
                        $"{localOperand.Name}:slice"),
                    "slice");
            }

            if (targetType.Kind == StarkTypeKind.Bool && operand.Type.Kind == StarkTypeKind.Bool)
            {
                return operand;
            }

            return operand;
        }

        private MidLevelIrOperand? LowerShortCircuitBooleanChain<TOperandContext>(
            IReadOnlyList<TOperandContext> operands,
            Func<TOperandContext, MidLevelIrOperand?> lowerOperand,
            bool shortCircuitOnTrue,
            string resultHint)
            where TOperandContext : ParserRuleContext
        {
            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, resultHint);
            var joinBlock = CreateBlock($"{resultHint}_join");

            for (var index = 0; index < operands.Count - 1; index++)
            {
                var operand = CoerceOperand(lowerOperand(operands[index]), StarkTypeSymbols.Bool);
                if (operand is null)
                {
                    return null;
                }

                var shortCircuitBlock = CreateBlock($"{resultHint}_short_{index}");
                var nextBlock = CreateBlock($"{resultHint}_rhs_{index + 1}");

                CurrentBlock.Terminator = shortCircuitOnTrue
                    ? new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [shortCircuitBlock.Id, nextBlock.Id],
                        ConditionText: operands[index].GetText(),
                        Condition: operand)
                    : new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [nextBlock.Id, shortCircuitBlock.Id],
                        ConditionText: operands[index].GetText(),
                        Condition: operand);

                CurrentBlock = shortCircuitBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(shortCircuitOnTrue), shortCircuitOnTrue ? "true" : "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
            }

            var lastOperand = CoerceOperand(lowerOperand(operands[^1]), StarkTypeSymbols.Bool);
            if (lastOperand is null)
            {
                return null;
            }

            EmitOperandAssignment(result, lastOperand, operands[^1].GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? EmitEqualityComparison(MidLevelIrOperand left, MidLevelIrOperand right, string text)
        {
            var compareType = FindCommonType(left.Type, right.Type);
            if (compareType.Kind is not (StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.Bool or StarkTypeKind.RawPointer))
            {
                MarkUnsupported();
                return null;
            }

            var coercedLeft = CoerceOperand(left, compareType);
            var coercedRight = CoerceOperand(right, compareType);
            if (coercedLeft is null || coercedRight is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Equal,
                    coercedLeft,
                    coercedRight,
                    StarkTypeSymbols.Bool,
                    text),
                "cmp");
        }

        private MidLevelIrOperand? LowerSwitchCaseLiteral(StarkParser.LiteralContext literal, StarkTypeSymbol switchType)
        {
            if (switchType.Kind == StarkTypeKind.Integer && literal.signedIntegerLiteral() is { } integerLiteral)
            {
                return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteral(integerLiteral), switchType);
            }

            if (switchType.Kind == StarkTypeKind.Bool)
            {
                if (literal.TRUE() is not null)
                {
                    return new MidLevelIrBoolConstantOperand(true);
                }

                if (literal.FALSE() is not null)
                {
                    return new MidLevelIrBoolConstantOperand(false);
                }
            }

            if (switchType.Kind == StarkTypeKind.Float && literal.FloatLiteral() is { } floatLiteral)
            {
                return new MidLevelIrFloatConstantOperand(floatLiteral.GetText(), switchType);
            }

            if (switchType.Kind == StarkTypeKind.RawPointer && literal.NULL() is not null)
            {
                return new MidLevelIrNullOperand(switchType);
            }

            return LowerLiteral(literal, switchType);
        }

        private MidLevelIrOperand? EmitTemporary(MidLevelIrRValue value, string hint)
        {
            var name = AllocateTemporaryName(hint);
            RegisterLocal(name, value.Type, storageClass: "temp", isMutable: false, isConstant: false);
            Emit(MidLevelIrStatementKind.Assign, $"{name} = {value.Text}", name, value.Type, value);
            return new MidLevelIrLocalOperand(name, value.Type);
        }

        private MidLevelIrLocalOperand CreateTemporaryLocal(StarkTypeSymbol type, string hint)
        {
            var name = AllocateTemporaryName(hint);
            RegisterLocal(name, type, storageClass: "temp", isMutable: false, isConstant: false);
            return new MidLevelIrLocalOperand(name, type);
        }

        private void EmitOperandAssignment(MidLevelIrLocalOperand target, MidLevelIrOperand value, string text)
        {
            Emit(
                MidLevelIrStatementKind.Assign,
                $"{target.Name} = {text}",
                target.Name,
                target.Type,
                new MidLevelIrUseRValue(value));
        }

        private bool TryLowerExpressionAsRValue(StarkParser.ExpressionContext expression, out MidLevelIrRValue value)
        {
            value = default!;
            if (TryGetSimplePostfixExpression(expression) is { } postfix
                && TryLowerCallExpression(postfix, out var call))
            {
                value = call;
                return true;
            }

            return false;
        }

        private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
        {
            var assignment = expression.assignmentExpression();
            if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
            {
                return null;
            }

            if (conditional.expression().Length != 0)
            {
                return null;
            }

            var logicalOr = conditional.logicalOrExpression();
            if (logicalOr.logicalAndExpression().Length != 1)
            {
                return null;
            }

            var logicalAnd = logicalOr.logicalAndExpression(0);
            if (logicalAnd.bitwiseOrExpression().Length != 1)
            {
                return null;
            }

            var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
            if (bitwiseOr.bitwiseXorExpression().Length != 1)
            {
                return null;
            }

            var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
            if (bitwiseXor.bitwiseAndExpression().Length != 1)
            {
                return null;
            }

            var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
            if (bitwiseAnd.equalityExpression().Length != 1)
            {
                return null;
            }

            var equality = bitwiseAnd.equalityExpression(0);
            if (equality.relationalExpression().Length != 1)
            {
                return null;
            }

            var relational = equality.relationalExpression(0);
            if (relational.shiftExpression().Length != 1)
            {
                return null;
            }

            var shift = relational.shiftExpression(0);
            if (shift.additiveExpression().Length != 1)
            {
                return null;
            }

            var additive = shift.additiveExpression(0);
            if (additive.multiplicativeExpression().Length != 1)
            {
                return null;
            }

            var multiplicative = additive.multiplicativeExpression(0);
            if (multiplicative.unaryExpression().Length != 1)
            {
                return null;
            }

            return TryGetSimplePostfixExpression(multiplicative.unaryExpression(0));
        }

        private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.UnaryExpressionContext expression)
        {
            if (expression.powerExpression() is not { } powerExpression
                || powerExpression.unaryExpression() is not null)
            {
                return null;
            }

            return powerExpression.postfixExpression();
        }

        private void EmitAssignmentFromExpression(
            string targetName,
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            string text)
        {
            var operand = LowerExpressionToOperand(expression, targetType);
            if (operand is null)
            {
                MarkUnsupported();
                Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType, new MidLevelIrUseRValue(operand));
        }

        private void RegisterLocal(string name, StarkTypeSymbol type, string storageClass, bool isMutable, bool isConstant)
        {
            if (_localsByName.ContainsKey(name))
            {
                return;
            }

            var local = new MidLevelIrLocal(name, type, storageClass, isMutable, isConstant, IsAddressable: ShouldAddressLocal(type, storageClass));
            _locals.Add(local);
            _localsByName[name] = local;
        }

        private void EmitAssignment(LoweredAssignment assignment)
        {
            if (assignment.Address is not null)
            {
                Emit(
                    MidLevelIrStatementKind.StoreIndirect,
                    assignment.Text,
                    targetType: assignment.TargetType,
                    value: new MidLevelIrUseRValue(assignment.ResultValue),
                    address: assignment.Address);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, assignment.Text, assignment.TargetName, assignment.TargetType, value: assignment.DirectValue);
        }

        private void Emit(
            MidLevelIrStatementKind kind,
            string text,
            string? targetName = null,
            StarkTypeSymbol? targetType = null,
            MidLevelIrRValue? value = null,
            MidLevelIrOperand? address = null)
        {
            CurrentBlock.Statements.Add(new MidLevelIrStatement(kind, text, targetName, targetType, address, value));
        }

        private void EnsureGoto(int targetBlockId)
        {
            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
            }
        }

        private string AllocateTemporaryName(string hint)
        {
            var name = $"$tmp{_nextTempId}_{hint}";
            _nextTempId++;
            return name;
        }

        private BasicBlockBuilder CreateBlock(string label)
        {
            var block = new BasicBlockBuilder(_nextBlockId, $"bb{_nextBlockId}_{label}");
            _nextBlockId++;
            _blocks.Add(block);
            return block;
        }

        private void MarkUnsupported()
        {
            SupportsDirectCodeGeneration = false;
        }

        private MidLevelIrOperand? UnsupportedOperand()
        {
            MarkUnsupported();
            return null;
        }

        private static string FormatInitializer(StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is { } expression)
            {
                return expression.GetText();
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                return objectInitializer.GetText();
            }

            return initializer.arrayInitializer()?.GetText() ?? "<init>";
        }

        private static IReadOnlyList<string> ExtractOperators<TOperand>(ParserRuleContext context)
            where TOperand : ParserRuleContext
        {
            var operators = new List<string>();
            var builder = new System.Text.StringBuilder();

            for (var index = 0; index < context.ChildCount; index++)
            {
                var child = context.GetChild(index);
                if (child is TOperand)
                {
                    if (builder.Length > 0)
                    {
                        operators.Add(builder.ToString());
                        builder.Clear();
                    }

                    continue;
                }

                builder.Append(child.GetText());
            }

            return operators;
        }

        private static MidLevelIrBinaryOperator MapBinaryOperator(string text)
        {
            return text switch
            {
                "+" => MidLevelIrBinaryOperator.Add,
                "-" => MidLevelIrBinaryOperator.Subtract,
                "*" => MidLevelIrBinaryOperator.Multiply,
                "/" => MidLevelIrBinaryOperator.Divide,
                "%" => MidLevelIrBinaryOperator.Modulo,
                "&" => MidLevelIrBinaryOperator.BitwiseAnd,
                "^" => MidLevelIrBinaryOperator.BitwiseXor,
                "|" => MidLevelIrBinaryOperator.BitwiseOr,
                "<<" => MidLevelIrBinaryOperator.ShiftLeft,
                ">>" => MidLevelIrBinaryOperator.ShiftRight,
                "==" => MidLevelIrBinaryOperator.Equal,
                "!=" => MidLevelIrBinaryOperator.NotEqual,
                "<" => MidLevelIrBinaryOperator.LessThan,
                "<=" => MidLevelIrBinaryOperator.LessThanOrEqual,
                ">" => MidLevelIrBinaryOperator.GreaterThan,
                ">=" => MidLevelIrBinaryOperator.GreaterThanOrEqual,
                _ => throw new InvalidOperationException($"Unsupported binary operator '{text}'.")
            };
        }

        private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
        {
            if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
            }

            if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Integer)
            {
                return StarkTypeSymbols.Integer(Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0));
            }

            if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Float)
            {
                return StarkTypeSymbols.Float(Math.Max(left.BitWidth ?? 32, right.BitWidth ?? 32));
            }

            if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Integer)
            {
                return left;
            }

            if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Float)
            {
                return right;
            }

            if (left.Kind == StarkTypeKind.Bool && right.Kind == StarkTypeKind.Bool)
            {
                return StarkTypeSymbols.Bool;
            }

            if (left.Kind == StarkTypeKind.RawPointer && right.Kind == StarkTypeKind.Null)
            {
                return left;
            }

            if (left.Kind == StarkTypeKind.Null && right.Kind == StarkTypeKind.RawPointer)
            {
                return right;
            }

            return left.DisplayName == right.DisplayName
                ? left
                : StarkTypeSymbols.Error;
        }

        private static bool HasSameStorageType(StarkTypeSymbol left, StarkTypeSymbol right)
        {
            if (left.Kind != right.Kind)
            {
                return false;
            }

            return left.Kind switch
            {
                StarkTypeKind.Integer => left.BitWidth == right.BitWidth,
                StarkTypeKind.Float => left.BitWidth == right.BitWidth,
                StarkTypeKind.RawPointer => true,
                _ => left.DisplayName == right.DisplayName
            };
        }

        private bool IsAddressableLocal(string name)
        {
            return _localsByName.TryGetValue(name, out var local) && local.IsAddressable;
        }

        private static bool ShouldAddressLocal(StarkTypeSymbol type, string storageClass)
        {
            return storageClass != "temp" && type.Kind == StarkTypeKind.FixedArray;
        }

        private static StarkTypeSymbol AddressType(StarkTypeSymbol pointeeType)
        {
            return StarkTypeSymbols.RawPointer(pointeeType, isMutable: true);
        }

        private static bool CanLowerSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.Bool or StarkTypeKind.RawPointer;
        }

        private static bool CanUseNativeSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool;
        }

        private static bool CanUseNativeSwitchCase(StarkTypeSymbol caseType, StarkTypeSymbol switchType)
        {
            return CanUseNativeSwitchType(caseType) && HasSameStorageType(caseType, switchType);
        }

        private static BigInteger ParseIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
        {
            var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
            return literal.MINUS() is null ? value : -value;
        }

        private static StarkTypeSymbol InferIntegerLiteralType(BigInteger value)
        {
            var widths = new[] { 8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 };
            foreach (var width in widths)
            {
                var min = -(BigInteger.One << (width - 1));
                var max = (BigInteger.One << (width - 1)) - BigInteger.One;
                if (value >= min && value <= max)
                {
                    return StarkTypeSymbols.Integer(width, value, value);
                }
            }

            return StarkTypeSymbols.Integer(widths[^1], value, value);
        }

        private sealed class BasicBlockBuilder
        {
            public BasicBlockBuilder(int id, string label)
            {
                Id = id;
                Label = label;
            }

            public int Id { get; }

            public string Label { get; }

            public List<MidLevelIrStatement> Statements { get; } = [];

            public MidLevelIrTerminator? Terminator { get; set; }

            public bool HasTerminator => Terminator is not null;

            public MidLevelIrBasicBlock Build()
            {
                return new MidLevelIrBasicBlock(
                    Id,
                    Label,
                    Statements.ToArray(),
                    Terminator ?? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: []));
            }
        }

        private readonly record struct LoopTargets(int ContinueTarget, int BreakTarget);
    }
}
