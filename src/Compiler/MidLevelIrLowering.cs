using System.Numerics;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class MidLevelIrLowerer(
    CompilerPassContext context,
    LoadedModuleSet loadedModules,
    ModuleGraph moduleGraph,
    TypeCheckModel typeModel,
    EnumLayoutModel enumLayoutModel)
{
    private readonly TypeCheckModel _typeModel = typeModel;
    private readonly EnumLayoutModel _enumLayoutModel = enumLayoutModel;
    private readonly Dictionary<string, FunctionLoweringContext> _functionsByName = CollectFunctionsByQualifiedName(loadedModules);
    private readonly StarkTypeResolver _typeResolver = new(context, "lower-mir", moduleGraph, typeModel.NamedTypes);
    private readonly Dictionary<string, TypedFunctionSignature> _fallbackFunctions = CollectFallbackFunctionSignatures(context, moduleGraph, typeModel.NamedTypes, loadedModules);
    private readonly Dictionary<string, TypedGlobalSymbol> _fallbackGlobals = CollectFallbackGlobals(context, moduleGraph, typeModel.NamedTypes, loadedModules);
    private readonly Dictionary<LiteralKey, StarkTypeSymbol> _literalTypes = typeModel.Literals
            .GroupBy(static literal => new LiteralKey(literal.LiteralText, literal.Location.Line, literal.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Type);
    private readonly Dictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);

    public MidLevelIrModule Lower(HighLevelIrModule hir)
    {
        var functions = hir.Functions
            .Select(LowerFunction)
            .ToArray();

        return new MidLevelIrModule(hir.ModuleName, functions);
    }

    private MidLevelIrFunction LowerFunction(HighLevelIrFunction function)
    {
        if (!_functionsByName.TryGetValue(function.Name, out var loweringContext)
            || loweringContext.Declaration.Body.block() is not { } body)
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

        var builder = new FunctionMirBuilder(
            function,
            loweringContext.ModuleName,
            _typeModel,
            _enumLayoutModel,
            _typeResolver,
            _fallbackFunctions,
            _fallbackGlobals,
            _literalTypes,
            _objectCreationConstructors);
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

    private static Dictionary<string, FunctionLoweringContext> CollectFunctionsByQualifiedName(LoadedModuleSet loadedModules)
    {
        var functions = new Dictionary<string, FunctionLoweringContext>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult))
            {
                var qualifiedName = module.Reference.IsRoot
                    ? declaration.Name
                    : $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                functions[qualifiedName] = new FunctionLoweringContext(module.SyntaxModel.ModuleName, declaration);
            }
        }

        return functions;
    }

    private static Dictionary<string, TypedFunctionSignature> CollectFallbackFunctionSignatures(
        CompilerPassContext context,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        LoadedModuleSet loadedModules)
    {
        var resolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, namedTypes);
        var functions = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult))
            {
                var genericParameters = resolver.GetGenericParameterNames(declaration.TypeParameters);
                var qualifiedName = QualifyName(module, declaration.Name);
                var parameters = declaration.ParameterList.parameter()
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Identifier().GetText(),
                        resolver.ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName)))
                    .ToArray();
                functions[qualifiedName] = new TypedFunctionSignature(
                    qualifiedName,
                    resolver.ResolveReturnType(declaration.ReturnType, genericParameters, module.SyntaxModel.ModuleName),
                    parameters);
            }
        }

        return functions;
    }

    private static Dictionary<string, TypedGlobalSymbol> CollectFallbackGlobals(
        CompilerPassContext context,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        LoadedModuleSet loadedModules)
    {
        var resolver = new StarkTypeResolver(context, "lower-mir", moduleGraph, namedTypes);
        var globals = new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    var declaredType = resolver.ResolveType(constantDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        var qualifiedName = QualifyName(module, declarator.Identifier().GetText());
                        globals[qualifiedName] = new TypedGlobalSymbol(qualifiedName, declaredType, GlobalBindingKind.Const);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                var declaredVariableType = resolver.ResolveType(variableDeclaration.type_(), currentModuleName: module.SyntaxModel.ModuleName);
                var bindingKind = variableDeclaration.MUT() is not null
                    ? GlobalBindingKind.Mutable
                    : GlobalBindingKind.Immutable;

                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var qualifiedName = QualifyName(module, declarator.Identifier().GetText());
                    globals[qualifiedName] = new TypedGlobalSymbol(qualifiedName, declaredVariableType, bindingKind);
                }
            }
        }

        return globals;
    }

    private static string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }

    private sealed record FunctionLoweringContext(string ModuleName, DeclaredFunctionSyntax Declaration);

    private readonly record struct LiteralKey(string Text, int Line, int Column);
    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

    private sealed class FunctionMirBuilder
    {
        private enum AggregatePatternFieldKind
        {
            Discard,
            Literal,
            Capture,
            Nested
        }

        private sealed record LowerableAggregateFieldPattern(
            string FieldName,
            string StorageFieldName,
            int FieldIndex,
            StarkTypeSymbol FieldType,
            AggregatePatternFieldKind Kind,
            string Text,
            StarkParser.LiteralContext? Literal,
            string? CaptureName,
            LowerableAggregatePattern? NestedPattern);

        private sealed record LowerableAggregatePattern(
            string TypeName,
            string? EnumVariantName,
            IReadOnlyList<LowerableAggregateFieldPattern> FieldPatterns,
            string? WholeCaptureName);

        private sealed record PendingSwitchBinding(string Name, MidLevelIrOperand Source);

        private sealed record LowerableSwitchLabel(
            string LabelText,
            StarkParser.LiteralContext? Literal,
            StarkParser.ExpressionContext? GuardExpression,
            bool IsDefault,
            bool IsMatchAll,
            string? CaptureName,
            LowerableAggregatePattern? AggregatePattern);

        private sealed record LowerableSwitchSection(
            StarkParser.SwitchSectionContext Section,
            IReadOnlyList<LowerableSwitchLabel> Labels);

        private sealed record PartitionedTextSwitchLabel(
            LowerableSwitchLabel Label,
            int TargetBlockId,
            byte[] Bytes,
            int Order);

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
            bool UsesAddressModel,
            bool IsAddressMutable);

        private sealed record LoweredAssignment(
            string Text,
            string? TargetName,
            StarkTypeSymbol TargetType,
            MidLevelIrRValue? DirectValue,
            MidLevelIrOperand ResultValue,
            MidLevelIrOperand? Address);

        private sealed class ScopeFrame
        {
            public List<(string Name, StarkTypeSymbol Type)> Locals { get; } = [];
        }

        private readonly HighLevelIrFunction _function;
        private readonly string _currentModuleName;
        private readonly TypeCheckModel _typeModel;
        private readonly EnumLayoutModel _enumLayoutModel;
        private readonly StarkTypeResolver _typeResolver;
        private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _fallbackFunctions;
        private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _fallbackGlobals;
        private readonly IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> _literalTypes;
        private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private readonly Stack<ScopeFrame> _scopes = [];
        private int _nextBlockId;
        private int _nextTempId;

        public FunctionMirBuilder(
            HighLevelIrFunction function,
            string currentModuleName,
            TypeCheckModel typeModel,
            EnumLayoutModel enumLayoutModel,
            StarkTypeResolver typeResolver,
            IReadOnlyDictionary<string, TypedFunctionSignature> fallbackFunctions,
            IReadOnlyDictionary<string, TypedGlobalSymbol> fallbackGlobals,
            IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> literalTypes,
            IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> objectCreationConstructors)
        {
            _function = function;
            _currentModuleName = currentModuleName;
            _typeModel = typeModel;
            _enumLayoutModel = enumLayoutModel;
            _typeResolver = typeResolver;
            _fallbackFunctions = fallbackFunctions;
            _fallbackGlobals = fallbackGlobals;
            _literalTypes = literalTypes;
            _objectCreationConstructors = objectCreationConstructors;
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
            _scopes.Push(new ScopeFrame());

            foreach (var statement in block.statement())
            {
                LowerStatement(statement);
            }

            var scope = _scopes.Pop();
            EmitStorageDead(scope);
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
                EmitStorageDeadBeyondDepth(loop.ScopeDepth);
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.BreakTarget]);
                return;
            }

            if (statement.continueStatement() is not null)
            {
                var loop = _loops.Peek();
                EmitStorageDeadBeyondDepth(loop.ScopeDepth);
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
            var declaredType = _typeResolver.ResolveType(declaration.type_(), currentModuleName: _currentModuleName);
            foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                LowerVariableInitializer(name, declaredType, declarator.variableInitializer());
            }
        }

        private void LowerVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
        {
            var declaredType = _typeResolver.ResolveType(declaration.type_(), currentModuleName: _currentModuleName);
            var storageClass = declaration.storageClass().GetText();

            foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass, declaration.MUT() is not null, isConstant: false);
                TrackDeclaredLocal(name, declaredType);
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

        private MidLevelIrOperand? LowerInitializerToOperand(StarkParser.VariableInitializerContext initializer, StarkTypeSymbol targetType)
        {
            if (initializer.expression() is { } expression)
            {
                return LowerExpressionToOperand(expression, targetType);
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                return LowerObjectInitializer(targetType, objectInitializer);
            }

            if (initializer.arrayInitializer() is { } arrayInitializer)
            {
                return LowerArrayInitializer(targetType, arrayInitializer);
            }

            MarkUnsupported();
            return null;
        }

        private void LowerReturn(StarkParser.ReturnStatementContext returnStatement)
        {
            if (returnStatement.expression() is null)
            {
                EmitStorageDeadBeyondDepth(0);
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: [],
                    ValueText: null,
                    Value: null);
                return;
            }

            var operand = LowerExpressionToOperand(returnStatement.expression(), _function.Signature.ReturnType);
            EmitStorageDeadBeyondDepth(0);
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

            if (expression.assignmentOperator() is null)
            {
                return false;
            }

            if (TryResolveIndirectPointerAssignmentTarget(expression.unaryExpression(), out var pointerAddress, out var pointeeType))
            {
                assignment = LowerIndirectPointerAssignment(expression, pointerAddress, pointeeType);
                return true;
            }

            if (!TryResolveAssignmentTarget(expression.unaryExpression(), out var target))
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

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

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

        private LoweredAssignment LowerIndirectPointerAssignment(
            StarkParser.AssignmentExpressionContext expression,
            MidLevelIrOperand address,
            StarkTypeSymbol pointeeType)
        {
            var assignmentText = $"{expression.unaryExpression().GetText()} {expression.assignmentOperator().GetText()} {expression.assignmentExpression().GetText()}";

            if (expression.assignmentOperator().GetText() == "=")
            {
                var assignedValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), pointeeType);
                if (assignedValue is null)
                {
                    MarkUnsupported();
                    return default;
                }

                return new LoweredAssignment(
                    assignmentText,
                    TargetName: null,
                    pointeeType,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: address);
            }

            var currentValue = EmitTemporary(
                new MidLevelIrLoadIndirectRValue(address, pointeeType, $"{address.Text}:load"),
                "load");
            if (currentValue is null)
            {
                MarkUnsupported();
                return default;
            }

            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                MarkUnsupported();
                return default;
            }

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                MarkUnsupported();
                return default;
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");
            if (temp is null)
            {
                MarkUnsupported();
                return default;
            }

            return new LoweredAssignment(
                assignmentText,
                TargetName: null,
                pointeeType,
                DirectValue: null,
                ResultValue: CoerceOperand(temp, pointeeType) ?? temp,
                Address: address);
        }

        private bool TryResolveIndirectPointerAssignmentTarget(
            StarkParser.UnaryExpressionContext expression,
            out MidLevelIrOperand address,
            out StarkTypeSymbol pointeeType)
        {
            address = default!;
            pointeeType = StarkTypeSymbols.Error;

            if (expression.conversionType() is not null || expression.powerExpression() is not null)
            {
                return false;
            }

            if (!string.Equals(expression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return false;
            }

            var loweredAddress = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (loweredAddress is null
                || loweredAddress.Type.Kind != StarkTypeKind.RawPointer
                || loweredAddress.Type.ElementType is null)
            {
                return false;
            }

            address = loweredAddress;
            pointeeType = loweredAddress.Type.ElementType;
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
            if (TryLowerNativeSwitch(switchStatement)
                || TryLowerPartitionedTextSwitch(switchStatement)
                || TryLowerGuardedSwitch(switchStatement))
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

        private bool TryLowerPartitionedTextSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            var switchValue = LowerExpressionToOperand(switchStatement.expression());
            if (switchValue is null
                || !CanUsePartitionedTextSwitchType(switchValue.Type)
                || defaultSectionCount > 1)
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

            var textLabels = allLabels
                .Where(static label => !label.IsDefault)
                .ToArray();
            if (textLabels.Length == 0
                || textLabels.Any(static label => label.GuardExpression is not null || label.CaptureName is not null || label.Literal is null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault))
                .Select(static section => section.Block.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return false;
            }

            var flattenedLabels = new List<PartitionedTextSwitchLabel>();
            var order = 0;
            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault || label.Literal is null)
                    {
                        continue;
                    }

                    flattenedLabels.Add(new PartitionedTextSwitchLabel(
                        label,
                        section.Block.Id,
                        DecodeTextLiteral(label.Literal.GetText()),
                        order++));
                }
            }

            if (flattenedLabels.Count == 0)
            {
                return false;
            }

            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthGroups = flattenedLabels
                .GroupBy(static label => label.Bytes.Length)
                .OrderBy(static group => group.Key)
                .Select(group => (
                    Length: group.Key,
                    Labels: group.OrderBy(static label => label.Order).ToArray(),
                    Block: CreateBlock($"switch_len_{group.Key}")))
                .ToArray();

            var switchCases = lengthGroups
                .Select(group => new MidLevelIrSwitchCase(
                    group.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    group.Block.Id,
                    new MidLevelIrIntegerConstantOperand(new BigInteger(group.Length), lengthType)))
                .ToList();
            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(defaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: $"{switchStatement.expression().GetText()}.length",
                Condition: length,
                SwitchCases: switchCases,
                DefaultTarget: defaultTarget);

            foreach (var group in lengthGroups)
            {
                CurrentBlock = group.Block;
                if (!EmitPartitionedTextLengthDecision(dataPointer, group.Labels, defaultTarget, switchStatement.expression().GetText()))
                {
                    return false;
                }
            }

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

                if (label.AggregatePattern is { } aggregatePattern)
                {
                    if (!EmitAggregateSwitchPatternTransition(label, aggregatePattern, switchValue, targetBlockId, nextTarget, sectionIndex, index))
                    {
                        return false;
                    }

                    continue;
                }

                var condition = EmitSwitchLiteralComparison(
                    switchValue,
                    label.Literal!,
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

        private bool TryParseAggregatePattern(StarkParser.AggregatePatternContext aggregatePattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var patternName = aggregatePattern.simpleType().GetText();
            if (TryResolveEnumCaseReference(patternName, out var enumType, out _, out var enumVariant))
            {
                if (enumVariant.UsesNamedFields)
                {
                    return false;
                }

                var enumSuffix = aggregatePattern.aggregatePatternSuffix();
                if (enumVariant.Fields.Count == 0)
                {
                    if (enumSuffix is not null)
                    {
                        return false;
                    }

                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                    return true;
                }

                if (enumSuffix is null)
                {
                    return false;
                }

                if (enumSuffix.Identifier() is { } enumCapture)
                {
                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                    return true;
                }

                var enumFieldPatterns = enumSuffix.pattern();
                if (enumFieldPatterns.Length != enumVariant.Fields.Count)
                {
                    return false;
                }

                var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
                for (var index = 0; index < enumFieldPatterns.Length; index++)
                {
                    var field = enumVariant.Fields[index];
                    if (!TryParseStructuredFieldPattern(
                            enumFieldPatterns[index],
                            field.SourceFieldName ?? field.SourcePosition.ToString(),
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            field.Type,
                            out var parsedFieldPattern))
                    {
                        return false;
                    }

                    parsedEnumFieldPatterns[index] = parsedFieldPattern;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
                return true;
            }

            var patternType = _typeResolver.ResolveSimpleType(aggregatePattern.simpleType(), currentModuleName: _currentModuleName);
            if (patternType.Kind != StarkTypeKind.Named
                || patternType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(patternType.NamedType, out var namedType))
            {
                return false;
            }

            var suffix = aggregatePattern.aggregatePatternSuffix();
            if (suffix is null)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType, EnumVariantName: null, [], WholeCaptureName: null);
                return true;
            }

            if (suffix.Identifier() is { } capture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType, EnumVariantName: null, [], capture.GetText());
                return true;
            }

            var fieldPatterns = suffix.pattern();
            if (fieldPatterns.Length != namedType.OrderedFields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[fieldPatterns.Length];
            for (var index = 0; index < fieldPatterns.Length; index++)
            {
                var field = namedType.OrderedFields[index];
                if (!TryParseStructuredFieldPattern(fieldPatterns[index], field.Name, field.Name, index, field.Type, out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType, EnumVariantName: null, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseEnumNamedFieldPattern(StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var caseName = enumNamedFieldPattern.dottedName().GetText();
            if (!TryResolveEnumCaseReference(caseName, out var enumType, out _, out var enumVariant)
                || !enumVariant.UsesNamedFields)
            {
                return false;
            }

            var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
            if (members.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[enumVariant.Fields.Count];
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var memberName = member.Identifier().GetText();
                var field = enumVariant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                if (field is null
                    || field.SourceFieldName is null
                    || !seenMembers.Add(memberName)
                    || !TryParseStructuredFieldPattern(
                        member.pattern(),
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[field.SourcePosition] = parsedFieldPattern;
            }

            if (seenMembers.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseStructuredFieldPattern(
            StarkParser.PatternContext pattern,
            string fieldName,
            string storageFieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            out LowerableAggregateFieldPattern parsedFieldPattern)
        {
            if (pattern.DISCARD() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Discard,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null);
                return true;
            }

            if (pattern.VAR() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Capture,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: pattern.Identifier()?.GetText(),
                    NestedPattern: null);
                return true;
            }

            if (pattern.literal() is { } literal)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Literal,
                    literal.GetText(),
                    literal,
                    CaptureName: null,
                    NestedPattern: null);
                return true;
            }

            if (pattern.enumNamedFieldPattern() is { } nestedEnumNamedFieldPattern)
            {
                if (!TryParseEnumNamedFieldPattern(nestedEnumNamedFieldPattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null
                    || parsedNestedPattern.WholeCaptureName is not null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedEnumNamedFieldPattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern);
                return true;
            }

            if (pattern.aggregatePattern() is { } nestedAggregatePattern)
            {
                if (!TryParseAggregatePattern(nestedAggregatePattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null
                    || parsedNestedPattern.WholeCaptureName is not null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedAggregatePattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern);
                return true;
            }

            parsedFieldPattern = default!;
            return false;
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
                        labels.Add(new LowerableSwitchLabel("default", null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
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
                            labels.Add(new LowerableSwitchLabel(pattern.GetText(), null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
                            defaultSectionCount++;
                            continue;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: null,
                            AggregatePattern: null));
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
                            CaptureName: pattern.Identifier()?.GetText(),
                            AggregatePattern: null));
                        continue;
                    }

                    if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
                    {
                        if (!TryParseEnumNamedFieldPattern(enumNamedFieldPattern, out var parsedEnumNamedFieldPattern)
                            || parsedEnumNamedFieldPattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            enumNamedFieldPattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedEnumNamedFieldPattern));
                        continue;
                    }

                    if (pattern.aggregatePattern() is { } aggregatePattern)
                    {
                        if (!TryParseAggregatePattern(aggregatePattern, out var parsedAggregatePattern)
                            || parsedAggregatePattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            aggregatePattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedAggregatePattern));
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
                        CaptureName: null,
                        AggregatePattern: null));
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
                var aggregateLabels = section.Labels.Where(static label => label.AggregatePattern is not null).ToArray();
                if (aggregateLabels.Length != 0)
                {
                    if (aggregateLabels.Length != 1 || section.Labels.Count != 1)
                    {
                        return false;
                    }

                    var aggregatePattern = aggregateLabels[0].AggregatePattern!;
                    if (aggregatePattern.WholeCaptureName is not null)
                    {
                        return false;
                    }

                    if (!TryRegisterAggregatePatternCaptureLocals(aggregatePattern))
                    {
                        return false;
                    }

                    continue;
                }

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

        private bool TryRegisterAggregatePatternCaptureLocals(LowerableAggregatePattern aggregatePattern)
        {
            foreach (var fieldPattern in aggregatePattern.FieldPatterns)
            {
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    if (fieldPattern.CaptureName is null
                        || _localsByName.ContainsKey(fieldPattern.CaptureName)
                        || _parametersByName.ContainsKey(fieldPattern.CaptureName))
                    {
                        return false;
                    }

                    RegisterLocal(fieldPattern.CaptureName, fieldPattern.FieldType, storageClass: "match", isMutable: false, isConstant: false);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || fieldPattern.NestedPattern.WholeCaptureName is not null
                        || !TryRegisterAggregatePatternCaptureLocals(fieldPattern.NestedPattern))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool EmitSwitchMatchTransition(LowerableSwitchLabel label, MidLevelIrOperand switchValue, int targetBlockId, int nextTarget)
        {
            IReadOnlyList<PendingSwitchBinding> bindings = label.CaptureName is null
                ? []
                : [new PendingSwitchBinding(label.CaptureName, switchValue)];

            return EmitSwitchBindingsAndGuard(label.GuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregateSwitchPatternTransition(
            LowerableSwitchLabel label,
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextTarget,
            int sectionIndex,
            int labelIndex)
        {
            if (aggregatePattern.WholeCaptureName is not null)
            {
                return false;
            }

            if (switchValue.Type.Kind != StarkTypeKind.Named
                || switchValue.Type.NamedType is null
                || !string.Equals(switchValue.Type.NamedType, aggregatePattern.TypeName, StringComparison.Ordinal))
            {
                return false;
            }

            var bindings = new List<PendingSwitchBinding>();
            var matchBlock = CreateBlock($"switch_agg_match_{sectionIndex}_{labelIndex}");
            if (!EmitAggregatePatternDecision(
                aggregatePattern,
                switchValue,
                matchBlock.Id,
                nextTarget,
                bindings,
                $"{sectionIndex}_{labelIndex}"))
            {
                return false;
            }

            CurrentBlock = matchBlock;
            return EmitSwitchBindingsAndGuard(label.GuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregatePatternDecision(
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int successTarget,
            int failureTarget,
            List<PendingSwitchBinding> bindings,
            string pathTag)
        {
            var fieldPatterns = aggregatePattern.FieldPatterns;
            if (aggregatePattern.EnumVariantName is { } enumVariantName)
            {
                if (!_enumLayoutModel.Layouts.TryGetValue(aggregatePattern.TypeName, out var enumLayout)
                    || !enumLayout.TryGetVariant(enumVariantName, out var enumVariant))
                {
                    return false;
                }

                BasicBlockBuilder? payloadEntryBlock = null;
                var successAfterTag = successTarget;
                if (fieldPatterns.Count != 0)
                {
                    payloadEntryBlock = CreateBlock($"switch_enum_match_{pathTag}");
                    successAfterTag = payloadEntryBlock.Id;
                }

                var tagValue = LowerKnownFieldAccess(switchValue, enumLayout.TagField.Name, fieldIndex: 0, enumLayout.TagField.Type, "$tag");
                if (tagValue is null)
                {
                    return false;
                }

                var expectedTag = new MidLevelIrIntegerConstantOperand(new BigInteger(enumVariant.TagValue), enumLayout.TagField.Type);
                var condition = EmitEqualityComparison(tagValue, expectedTag, $"switch {switchValue.Text} is {aggregatePattern.TypeName}.{enumVariantName}");
                if (condition is null)
                {
                    return false;
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [successAfterTag, failureTarget],
                    ConditionText: $"{aggregatePattern.TypeName}.{enumVariantName}",
                    Condition: condition);

                if (fieldPatterns.Count == 0)
                {
                    return true;
                }

                CurrentBlock = payloadEntryBlock!;
            }

            if (fieldPatterns.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [successTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[fieldPatterns.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < fieldPatterns.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_agg_test_{pathTag}_{index}");
            }

            for (var index = 0; index < fieldPatterns.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var fieldPattern = fieldPatterns[index];
                var nextTarget = index + 1 < fieldPatterns.Count ? decisionBlocks[index + 1].Id : successTarget;

                if (fieldPattern.Kind == AggregatePatternFieldKind.Discard)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(switchValue, fieldPattern.StorageFieldName, fieldPattern.FieldIndex, fieldPattern.FieldType, fieldPattern.FieldName);
                if (fieldValue is null)
                {
                    return false;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    bindings.Add(new PendingSwitchBinding(fieldPattern.CaptureName!, fieldValue));
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || !EmitAggregatePatternDecision(
                            fieldPattern.NestedPattern,
                            fieldValue,
                            nextTarget,
                            failureTarget,
                            bindings,
                            $"{pathTag}_{index}"))
                    {
                        return false;
                    }

                    continue;
                }

                var condition = EmitSwitchLiteralComparison(
                    fieldValue,
                    fieldPattern.Literal!,
                    $"switch {switchValue.Text}.{fieldPattern.FieldName} == {fieldPattern.Text}");
                if (condition is null)
                {
                    return false;
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextTarget, failureTarget],
                    ConditionText: fieldPattern.Text,
                    Condition: condition);
            }

            return true;
        }

        private bool EmitSwitchBindingsAndGuard(
            StarkParser.ExpressionContext? guardExpression,
            IReadOnlyList<PendingSwitchBinding> bindings,
            int targetBlockId,
            int nextTarget)
        {
            if (bindings.Count != 0 && guardExpression is not null)
            {
                var bindBlock = CreateBlock("switch_bind");
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bindBlock.Id]);
                CurrentBlock = bindBlock;
            }

            foreach (var binding in bindings)
            {
                var capture = new MidLevelIrLocalOperand(binding.Name, binding.Source.Type);
                EmitOperandAssignment(capture, binding.Source, binding.Source.Text);
            }

            if (guardExpression is null)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            var guard = LowerExpressionToOperand(guardExpression, StarkTypeSymbols.Bool);
            if (guard is null)
            {
                return false;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [targetBlockId, nextTarget],
                ConditionText: guardExpression.GetText(),
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

            _loops.Push(new LoopTargets(conditionBlock.Id, exitBlock.Id, _scopes.Count));
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
                var declaredType = _typeResolver.ResolveType(localForVariableDeclaration.type_(), currentModuleName: _currentModuleName);
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

            _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id, _scopes.Count));
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

            if (expression.conversionType() is { } conversionType)
            {
                var targetType = _typeResolver.ResolveConversionType(conversionType);
                var convertedOperand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
                if (convertedOperand is null)
                {
                    return null;
                }

                var converted = CoerceOperand(convertedOperand, targetType);
                return expectedType is null ? converted : CoerceOperand(converted, expectedType);
            }

            var operand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (operand is null)
            {
                return null;
            }

            var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();
            var result = op switch
            {
                "+" => operand,
                "-" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.Negate, operand, operand.Type, expression.GetText()),
                    "neg"),
                "-%" => EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MidLevelIrBinaryOperator.WrappingSubtract,
                        new MidLevelIrIntegerConstantOperand(BigInteger.Zero, operand.Type),
                        operand,
                        operand.Type,
                        expression.GetText()),
                    "wrapneg"),
                "!" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.LogicalNot, CoerceOperand(operand, StarkTypeSymbols.Bool) ?? operand, StarkTypeSymbols.Bool, expression.GetText()),
                    "not"),
                "~" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.BitwiseNot, operand, operand.Type, expression.GetText()),
                    "bitnot"),
                "&" => LowerAddressOfUnary(expression.unaryExpression(), operand),
                "*" => LowerDereferenceUnary(expression, operand),
                _ => UnsupportedOperand()
            };

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerAddressOfUnary(StarkParser.UnaryExpressionContext operandExpression, MidLevelIrOperand operand)
        {
            if (operandExpression.conversionType() is null
                && operandExpression.powerExpression() is null
                && string.Equals(operandExpression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return operand;
            }

            if (!TryResolveAssignmentTarget(operandExpression, out var target))
            {
                MarkUnsupported();
                return null;
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? LowerDereferenceUnary(StarkParser.UnaryExpressionContext expression, MidLevelIrOperand operand)
        {
            if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
            {
                MarkUnsupported();
                return null;
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    operand,
                    operand.Type.ElementType,
                    expression.GetText()),
                "load");
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

            if (!TryLowerPostfixOperand(expression, out var current))
            {
                return null;
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private bool TryLowerPostfixOperand(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrOperand? result)
        {
            result = null;

            if (!TryInitializePostfixState(expression.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length; index++)
            {
                var postfixPart = expression.postfixPart()[index];

                if (postfixPart.argumentList() is { } argumentList)
                {
                    if (currentName is null)
                    {
                        return false;
                    }

                    if (TryLowerEnumConstructorCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var enumConstructorValue))
                    {
                        currentValue = enumConstructorValue;
                        currentName = null;
                        if (currentValue is null)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!TryBuildCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var directCall))
                    {
                        return false;
                    }

                    if (directCall.Type.Kind == StarkTypeKind.Void)
                    {
                        MarkUnsupported();
                        return false;
                    }

                    currentValue = EmitTemporary(directCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    if (currentValue is null)
                    {
                        if (currentName is null)
                        {
                            return false;
                        }

                        currentValue = ResolveNamedOperand(currentName);
                        currentName = null;
                        if (currentValue is null)
                        {
                            return false;
                        }
                    }

                    currentValue = LowerIndexAccess(currentValue, expressionList);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null
                    && index + 1 < expression.postfixPart().Length
                    && expression.postfixPart()[index + 1].argumentList() is { } memberArguments)
                {
                    if (!TryBuildMemberCall(currentValue, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall))
                    {
                        return false;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        MarkUnsupported();
                        return false;
                    }

                    currentValue = EmitTemporary(memberCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentValue = LowerFieldAccess(currentValue, memberName);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (currentName is null)
                {
                    return false;
                }

                var qualifiedName = $"{currentName}.{memberName}";
                currentValue = TryResolveNamedValueOperand(qualifiedName);
                if (currentValue is not null)
                {
                    currentName = null;
                }
                else
                {
                    currentName = qualifiedName;
                }
            }

            if (currentValue is null)
            {
                if (currentName is null)
                {
                    return false;
                }

                currentValue = ResolveNamedOperand(currentName);
                if (currentValue is null)
                {
                    return false;
                }
            }

            result = currentValue;
            return true;
        }

        private bool TryInitializePostfixState(
            StarkParser.PrimaryExpressionContext expression,
            out MidLevelIrOperand? currentValue,
            out string? currentName)
        {
            currentValue = null;
            currentName = null;

            if (expression.Identifier() is { } identifier)
            {
                currentValue = TryResolveNamedValueOperand(identifier.GetText());
                currentName = currentValue is null ? identifier.GetText() : null;
                return true;
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                currentValue = TryResolveNamedValueOperand(qualifiedName.GetText());
                currentName = currentValue is null ? qualifiedName.GetText() : null;
                return true;
            }

            currentValue = LowerPrimaryExpression(expression, expectedType: null);
            return currentValue is not null;
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

            if (expression.enumConstructorExpression() is { } enumConstructorExpression)
            {
                return LowerEnumConstructorExpression(enumConstructorExpression, expectedType);
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
            var createdType = _typeResolver.ResolveType(expression.type_(), currentModuleName: _currentModuleName);
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            if (expression.argumentList() is { } argumentList && argumentList.argument().Length != 0)
            {
                var initializedFromConstructor = LowerPrimaryConstructorObjectCreation(expression, createdType, argumentList);
                if (initializedFromConstructor is null)
                {
                    return null;
                }

                current = initializedFromConstructor;
            }

            if (expression.objectInitializer() is { } objectInitializer)
            {
                var initialized = LowerObjectInitializer(createdType, current, objectInitializer);
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
            return LowerObjectInitializer(targetType, new MidLevelIrZeroInitializerOperand(targetType), objectInitializer);
        }

        private MidLevelIrOperand? LowerObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            StarkParser.ObjectInitializerContext objectInitializer)
        {
            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType))
            {
                MarkUnsupported();
                return null;
            }

            var current = seed;

            foreach (var initializer in objectInitializer.memberInitializer())
            {
                if (!namedType.TryGetField(initializer.Identifier().GetText(), out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var memberInitializer = initializer.variableInitializer();
                var value = LowerInitializerToOperand(memberInitializer, field.Type);
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
                        $"{current.Text}.{field.Name} = {memberInitializer.GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerPrimaryConstructorObjectCreation(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol createdType,
            StarkParser.ArgumentListContext argumentList)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || !TryGetMatchedObjectCreationConstructor(expression, out var constructor)
                || constructor is null
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != argumentList.argument().Length)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var loweredArgument = LowerExpressionToOperand(argumentList.argument(index).expression(), parameter.Type);
                if (loweredArgument is null)
                {
                    return null;
                }

                var fieldValue = CoerceOperand(loweredArgument, field.Type);
                if (fieldValue is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        fieldValue,
                        createdType,
                        $"{current.Text}.{field.Name} = {argumentList.argument(index).GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerEnumConstructorExpression(
            StarkParser.EnumConstructorExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var constructorName = expression.dottedName().GetText();
            if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var layout, out var variant)
                || !variant.UsesNamedFields)
            {
                MarkUnsupported();
                return null;
            }

            var memberValues = new Dictionary<string, MidLevelIrOperand>(StringComparer.Ordinal);
            foreach (var member in expression.enumConstructorInitializer().enumConstructorMember())
            {
                var memberName = member.Identifier().GetText();
                var layoutField = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                if (layoutField is null)
                {
                    MarkUnsupported();
                    return null;
                }

                var value = LowerExpressionToOperand(member.expression(), layoutField.Type);
                if (value is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(value, layoutField.Type);
                if (coerced is null)
                {
                    return null;
                }

                memberValues[memberName] = coerced;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                if (field.SourceFieldName is null
                    || !memberValues.TryGetValue(field.SourceFieldName, out var value))
                {
                    MarkUnsupported();
                    return null;
                }

                orderedValues[index] = value;
            }

            var lowered = LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, expression.GetText());
            return lowered is null || expectedType is null ? lowered : CoerceOperand(lowered, expectedType);
        }

        private bool TryLowerEnumConstructorCall(
            string constructorName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields)
            {
                return false;
            }

            if (variant.Fields.Count != arguments.argument().Length)
            {
                MarkUnsupported();
                return true;
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), field.Type);
                if (argument is null)
                {
                    value = null;
                    return true;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    value = null;
                    return true;
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, text);
            return true;
        }

        private MidLevelIrOperand? LowerDirectTagEnumConstructor(
            StarkTypeSymbol enumType,
            EnumLayoutSymbol layout,
            EnumVariantLayoutSymbol variant,
            IReadOnlyList<MidLevelIrOperand> payloadValues,
            string text)
        {
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(enumType);
            var tagValue = new MidLevelIrIntegerConstantOperand(new BigInteger(variant.TagValue), layout.TagField.Type);

            var withTag = EmitTemporary(
                new MidLevelIrInsertFieldRValue(
                    current,
                    layout.TagField.Name,
                    0,
                    tagValue,
                    enumType,
                    $"{text}.$tag = {variant.TagValue}"),
                "enumtag");
            if (withTag is null)
            {
                return null;
            }

            current = withTag;

            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        payloadValues[index],
                        enumType,
                        field.SourceFieldName is null
                            ? $"{text}[{index}] = {payloadValues[index].Text}"
                            : $"{text}.{field.SourceFieldName} = {payloadValues[index].Text}"),
                    "enumfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private bool TryGetMatchedObjectCreationConstructor(
            StarkParser.ObjectCreationExpressionContext expression,
            out TypedConstructorShape? constructor)
        {
            return _objectCreationConstructors.TryGetValue(
                new ObjectCreationKey(
                    expression.GetText(),
                    expression.Start.Line,
                    expression.Start.Column + 1),
                out constructor);
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
            var elementCount = Math.Min(fixedLength, arrayInitializer.variableInitializer().Length);

            for (var index = 0; index < elementCount; index++)
            {
                var elementInitializer = arrayInitializer.variableInitializer(index);
                var value = LowerInitializerToOperand(elementInitializer, targetType.ElementType);
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
                        $"{current.Text}[{index}] = {elementInitializer.GetText()}"),
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

            var projectedType = ProjectFrozenView(target.Type, field.Type);

            return EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    field.Name,
                    fieldIndex,
                    projectedType,
                    $"{target.Text}.{field.Name}"),
                "field");
        }

        private MidLevelIrOperand? LowerKnownFieldAccess(
            MidLevelIrOperand target,
            string fieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            string displayFieldName)
        {
            var projectedType = ProjectFrozenView(target.Type, fieldType);
            return EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    fieldName,
                    fieldIndex,
                    projectedType,
                    $"{target.Text}.{displayFieldName}"),
                "field");
        }

        private MidLevelIrOperand? LowerIndexAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var current = target;

            foreach (var indexExpression in indexes.expression())
            {
                if (current.Type.Kind == StarkTypeKind.FixedArray && current.Type.ElementType is not null)
                {
                    if (TryResolveConstantArrayIndex(current.Type, indexExpression, out var constantIndex, out var resolvedElementType))
                    {
                        var elementType = ProjectFrozenView(current.Type, resolvedElementType);
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

                    if (current is not MidLevelIrLocalOperand local || current.Type.ElementType is null)
                    {
                        MarkUnsupported();
                        return null;
                    }

                    var projectedElementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    EnsureAddressableLocal(local.Name);

                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported();
                        return null;
                    }

                    var baseAddress = CreateAddressOfLocal(local.Name, local.Type);
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
                            AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                            $"{local.Name}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            projectedElementType,
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
                    var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
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
                            AddressType(elementType, current.Type.IsMutableView && CanMutateThroughType(current.Type)),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
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

            if (!TryInitializePostfixState(expression.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            for (var index = 0; index < expression.postfixPart().Length; index++)
            {
                var postfixPart = expression.postfixPart()[index];

                if (postfixPart.argumentList() is { } argumentList)
                {
                    if (currentName is null
                        || !TryBuildCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out var directCall))
                    {
                        return false;
                    }

                    if (index == expression.postfixPart().Length - 1)
                    {
                        call = directCall;
                        return true;
                    }

                    if (directCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return false;
                    }

                    currentValue = EmitTemporary(directCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    if (currentValue is null)
                    {
                        return false;
                    }

                    currentValue = LowerIndexAccess(currentValue, expressionList);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null
                    && index + 1 < expression.postfixPart().Length
                    && expression.postfixPart()[index + 1].argumentList() is { } memberArguments)
                {
                    if (!TryBuildMemberCall(currentValue, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall))
                    {
                        return false;
                    }

                    if (index + 1 == expression.postfixPart().Length - 1)
                    {
                        call = memberCall;
                        return true;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return false;
                    }

                    currentValue = EmitTemporary(memberCall, "call");
                    currentName = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentValue = LowerFieldAccess(currentValue, memberName);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                if (currentName is null)
                {
                    return false;
                }

                currentName = $"{currentName}.{memberName}";
            }

            return false;
        }

        private bool TryBuildCall(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (!TryResolveFunctionSignature(functionName, out var signature))
            {
                return false;
            }

            return TryBuildCall(functionName, signature, receiver: null, arguments, text, out call);
        }

        private bool TryBuildMemberCall(
            MidLevelIrOperand receiver,
            string memberName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (receiver.Type.NamedType is not { } namedTypeName
                || !TryResolveFunctionSignature($"{namedTypeName}.{memberName}", out var signature)
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            return TryBuildCall(signature.Name, signature, receiver, arguments, text, out call);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            var loweredArguments = new List<MidLevelIrOperand>();
            var indirectArgumentLocals = new List<string?>();
            var receiverOffset = receiver is null ? 0 : 1;
            var explicitParameterCount = Math.Max(0, signature.Parameters.Count - receiverOffset);

            if (receiver is not null)
            {
                var receiverOperand = CoerceOperand(receiver, signature.Parameters[0].Type);
                if (receiverOperand is null)
                {
                    return false;
                }

                loweredArguments.Add(receiverOperand);
                indirectArgumentLocals.Add(ResolveIndirectArgumentLocal(signature.Parameters[0].Type, receiverOperand));
            }

            for (var index = 0; index < Math.Min(arguments.argument().Length, explicitParameterCount); index++)
            {
                var parameterType = signature.Parameters[index + receiverOffset].Type;
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                indirectArgumentLocals.Add(ResolveIndirectArgumentLocal(parameterType, argument));
            }

            if (arguments.argument().Length != explicitParameterCount)
            {
                return false;
            }

            call = new MidLevelIrCallRValue(
                signature.Name,
                loweredArguments,
                signature.ReturnType,
                text,
                indirectArgumentLocals);
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
                            : literal.StringLiteral() is not null
                                ? InferTextLiteralType(literal.GetText(), TextLiteralKind.String)
                                : literal.CharacterLiteral() is not null
                                    ? InferTextLiteralType(literal.GetText(), TextLiteralKind.Character)
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

        private static StarkTypeSymbol InferTextLiteralType(string text, TextLiteralKind kind)
        {
            return TextLiteralDecoder.IsAsciiLiteral(text, kind)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        private MidLevelIrOperand? ResolveNamedOperand(string name)
        {
            var operand = TryResolveNamedValueOperand(name);
            if (operand is not null)
            {
                return operand;
            }

            if (TryResolveFunctionSignature(name, out _))
            {
                MarkUnsupported();
                return null;
            }

            MarkUnsupported();
            return null;
        }

        private MidLevelIrOperand? TryResolveNamedValueOperand(string name)
        {
            if (_localsByName.TryGetValue(name, out var local))
            {
                return new MidLevelIrLocalOperand(local.Name, local.Type);
            }

            if (_parametersByName.TryGetValue(name, out var parameter))
            {
                return new MidLevelIrParameterOperand(parameter.Name, parameter.Type);
            }

            if (TryResolveGlobal(name, out var global))
            {
                return new MidLevelIrGlobalOperand(global.Name, global.Type);
            }

            if (TryResolveEnumCaseReference(name, out var enumType, out var enumLayout, out var variant) && variant.Fields.Count == 0)
            {
                return LowerDirectTagEnumConstructor(enumType, enumLayout, variant, [], name);
            }

            return null;
        }

        private bool TryResolveFunctionSignature(string name, out TypedFunctionSignature signature)
        {
            if (_typeModel.Functions.TryGetValue(name, out signature!))
            {
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackFunctions.TryGetValue($"{_currentModuleName}.{name}", out signature!))
            {
                return true;
            }

            return _fallbackFunctions.TryGetValue(name, out signature!);
        }

        private bool TryResolveGlobal(string name, out TypedGlobalSymbol global)
        {
            if (_typeModel.Globals.TryGetValue(name, out global!))
            {
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackGlobals.TryGetValue($"{_currentModuleName}.{name}", out global!))
            {
                return true;
            }

            return _fallbackGlobals.TryGetValue(name, out global!);
        }

        private bool TryResolveEnumCaseReference(
            string name,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            var separator = name.LastIndexOf('.');
            if (separator <= 0)
            {
                return false;
            }

            var enumTypeName = name[..separator];
            var variantName = name[(separator + 1)..];
            if (!TryResolveNamedTypeBySourceName(enumTypeName, out var namedType)
                || namedType.Kind != DeclarationKind.Enum
                || !_enumLayoutModel.Layouts.TryGetValue(namedType.Name, out layout)
                || !layout.TryGetVariant(variantName, out variant))
            {
                layout = null!;
                variant = null!;
                return false;
            }

            enumType = StarkTypeSymbols.Named(namedType.Name);
            return true;
        }

        private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
        {
            if (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!))
            {
                return true;
            }

            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _typeModel.NamedTypes.TryGetValue($"{_currentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            namedType = null!;
            return false;
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

            if (postfixExpression.primaryExpression().expression() is { } groupedExpression
                && TryExtractSimpleUnaryExpression(groupedExpression, out var groupedUnary))
            {
                return TryResolveAssignmentTarget(groupedUnary, out target);
            }

            if (!TryInitializePostfixState(postfixExpression.primaryExpression(), out var root, out var currentName))
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var currentType = root?.Type;
            var supportsAddressModel = root is MidLevelIrLocalOperand;
            var usesAddressModel = false;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (currentType is null)
                {
                    var memberName = postfixPart.Identifier()?.GetText();
                    if (currentName is null || memberName is null)
                    {
                        return false;
                    }

                    var qualifiedName = $"{currentName}.{memberName}";
                    root = TryResolveNamedValueOperand(qualifiedName);
                    if (root is null)
                    {
                        currentName = qualifiedName;
                        continue;
                    }

                    currentName = null;
                    currentType = root.Type;
                    supportsAddressModel = root is MidLevelIrLocalOperand;
                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectFrozenView(currentType, elementType);
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
                            if (root is not MidLevelIrLocalOperand localRoot || currentType.ElementType is null)
                            {
                                return false;
                            }

                            EnsureAddressableLocal(localRoot.Name);
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
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

                            var sliceElementType = ProjectFrozenView(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
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

                var projectedType = ProjectFrozenView(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    postfixPart.Identifier().GetText(),
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
                supportsAddressModel = supportsAddressModel || usesAddressModel;
            }

            if (root is null)
            {
                if (currentName is null)
                {
                    return false;
                }

                root = ResolveNamedOperand(currentName);
                if (root is null)
                {
                    return false;
                }

                currentType = root.Type;
            }

            var targetType = currentType ?? root.Type;
            target = new PlaceTarget(root.Text, root.Type, targetType, path, usesAddressModel, GetAddressMutability(root));
            return true;
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;

            if (expression.assignmentExpression() is not { } assignmentExpression
                || assignmentExpression.unaryExpression() is not null
                || assignmentExpression.assignmentOperator() is not null
                || assignmentExpression.conditionalExpression() is not { } conditionalExpression
                || conditionalExpression.expression().Length != 0)
            {
                return false;
            }

            return TryExtractSimpleUnaryExpression(conditionalExpression.logicalOrExpression(), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.logicalAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.logicalAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseOrExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseOrExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseXorExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseXorExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseXorExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.equalityExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.equalityExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.EqualityExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.relationalExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.relationalExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.RelationalExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.shiftExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.shiftExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ShiftExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.additiveExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.additiveExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.AdditiveExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.multiplicativeExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.multiplicativeExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.unaryExpression().Length == 1
                && (unaryExpression = expression.unaryExpression(0)) is not null;
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
            var currentAddressIsMutable = target.IsAddressMutable;
            MidLevelIrOperand? currentAddress = currentValue switch
            {
                MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, local.Type),
                MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, global.Type),
                _ => null
            };
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

                        var fieldAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrFieldAddressRValue(
                                currentAddress,
                                currentType,
                                segment.FieldName!,
                                segment.ConstantIndex!.Value,
                                AddressType(segment.SegmentType, fieldAddressIsMutable),
                                $"{currentAddress.Text}.{segment.FieldName}"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = fieldAddressIsMutable;
                        currentValue = null;
                        break;
                    case PlacePathKind.ConstantArrayIndex:
                    case PlacePathKind.DynamicArrayIndex:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        var elementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                currentAddress,
                                currentType,
                                segment.IndexOperand,
                                segment.ConstantIndex,
                                AddressType(segment.SegmentType, elementAddressIsMutable),
                                $"{currentAddress.Text}[{segment.ConstantIndex?.ToString() ?? segment.IndexOperand?.Text ?? "?"}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = elementAddressIsMutable;
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

                        var sliceElementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrSliceElementAddressRValue(
                                sliceValue,
                                segment.IndexOperand,
                                AddressType(segment.SegmentType, sliceElementAddressIsMutable),
                                $"{sliceValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = sliceElementAddressIsMutable;
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

            var currentLeft = left;
            if (operators.Count == 1 && operands.Count == 2)
            {
                var right = lowerOperand(operands[1]);
                return right is null
                    ? null
                    : EmitPairComparison(currentLeft, right, operators[0], $"{operands[0].GetText()} {operators[0]} {operands[1].GetText()}");
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "cmpchain");
            var joinBlock = CreateBlock("cmpchain_join");

            for (var index = 0; index < operators.Count; index++)
            {
                var right = lowerOperand(operands[index + 1]);
                if (right is null)
                {
                    return null;
                }

                var comparison = EmitPairComparison(
                    currentLeft,
                    right,
                    operators[index],
                    $"{operands[index].GetText()} {operators[index]} {operands[index + 1].GetText()}");
                if (comparison is null)
                {
                    return null;
                }

                if (index == operators.Count - 1)
                {
                    EmitOperandAssignment(result, comparison, comparison.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextBlock = CreateBlock($"cmpchain_next_{index + 1}");
                var falseBlock = CreateBlock($"cmpchain_false_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextBlock.Id, falseBlock.Id],
                    ConditionText: comparison.Text,
                    Condition: comparison);

                CurrentBlock = falseBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
                currentLeft = right;
            }

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? EmitPairComparison(
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string operatorText,
            string text)
        {
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
                    MapBinaryOperator(operatorText),
                    coercedLeft,
                    coercedRight,
                    StarkTypeSymbols.Bool,
                    text),
                "cmp");
        }

        private MidLevelIrOperand? CoerceOperand(MidLevelIrOperand? operand, StarkTypeSymbol targetType)
        {
            if (operand is null || targetType.Kind == StarkTypeKind.Error || operand.Type.Kind == StarkTypeKind.Error)
            {
                return operand;
            }

            if (operand.Type == targetType)
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

            if ((operand.Type.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
                || (operand.Type.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.Integer))
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    targetType.Kind == StarkTypeKind.RawPointer ? "ptrcast" : "intcast");
            }

            if (operand.Type.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.RawPointer)
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "ptrcast");
            }

            if ((operand.Type.Kind == StarkTypeKind.Ascii && targetType.Kind == StarkTypeKind.Unicode)
                || (operand.Type.Kind == StarkTypeKind.Unicode && targetType.Kind == StarkTypeKind.Ascii))
            {
                return EmitTemporary(
                    new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                    "textcast");
            }

            if (operand.Type.Kind == StarkTypeKind.FixedArray
                && targetType.Kind == StarkTypeKind.Slice
                && operand is MidLevelIrLocalOperand localOperand)
            {
                EnsureAddressableLocal(localOperand.Name);
                return EmitTemporary(
                    new MidLevelIrMakeSliceFromLocalRValue(
                        localOperand.Name,
                        operand.Type,
                        targetType,
                        $"{localOperand.Name}:slice"),
                    "slice");
            }

            if (HasSameStorageType(operand.Type, targetType))
            {
                return operand;
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

        private MidLevelIrOperand? EmitSwitchLiteralComparison(
            MidLevelIrOperand switchValue,
            StarkParser.LiteralContext literal,
            string text)
        {
            var literalOperand = LowerSwitchCaseLiteral(literal, switchValue.Type);
            if (literalOperand is null)
            {
                return null;
            }

            if (switchValue.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (literalOperand is not MidLevelIrStringConstantOperand stringLiteral)
                {
                    MarkUnsupported();
                    return null;
                }

                return EmitTextLiteralComparison(switchValue, stringLiteral, text);
            }

            return EmitEqualityComparison(switchValue, literalOperand, text);
        }

        private bool EmitPartitionedTextLengthDecision(
            MidLevelIrOperand dataPointer,
            IReadOnlyList<PartitionedTextSwitchLabel> labels,
            int defaultTarget,
            string switchText)
        {
            if (labels.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[labels.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < labels.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"textcmp_len_{labels[0].Bytes.Length}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : defaultTarget;

                if (!EmitTextLiteralMatchTransition(
                    dataPointer,
                    label.Bytes,
                    label.TargetBlockId,
                    nextTarget,
                    $"switch {switchText} == {label.Label.LabelText}"))
                {
                    return false;
                }
            }

            return true;
        }

        private MidLevelIrOperand? EmitTextLiteralComparison(
            MidLevelIrOperand switchValue,
            MidLevelIrStringConstantOperand literal,
            string text)
        {
            var bytes = DecodeTextLiteral(literal.LiteralText);
            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return null;
            }

            var byteType = StarkTypeSymbols.Integer(8);
            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthMatches = EmitPairComparison(
                length,
                new MidLevelIrIntegerConstantOperand(new BigInteger(bytes.Length), lengthType),
                "==",
                $"{text}:length");
            if (lengthMatches is null || bytes.Length == 0)
            {
                return lengthMatches;
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "textcmp");
            var compareBlock = CreateBlock("textcmp_byte_0");
            var falseBlock = CreateBlock("textcmp_false");
            var joinBlock = CreateBlock("textcmp_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [compareBlock.Id, falseBlock.Id],
                ConditionText: lengthMatches.Text,
                Condition: lengthMatches);

            CurrentBlock = falseBlock;
            EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
            EnsureGoto(joinBlock.Id);

            CurrentBlock = compareBlock;

            for (var index = 0; index < bytes.Length; index++)
            {
                var byteAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        byteType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(byteType, isMutable: false),
                        $"{switchValue.Text}.data[{index}]"),
                    "addr");
                if (byteAddress is null)
                {
                    return null;
                }

                var loadedByte = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        byteAddress,
                        byteType,
                        $"{switchValue.Text}.data[{index}]"),
                    "load");
                if (loadedByte is null)
                {
                    return null;
                }

                var expectedByte = new MidLevelIrIntegerConstantOperand(ToSignedByteValue(bytes[index]), byteType);
                var byteMatches = EmitPairComparison(
                    loadedByte,
                    expectedByte,
                    "==",
                    $"{text}:byte{index}");
                if (byteMatches is null)
                {
                    return null;
                }

                if (index == bytes.Length - 1)
                {
                    EmitOperandAssignment(result, byteMatches, byteMatches.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, falseBlock.Id],
                    ConditionText: byteMatches.Text,
                    Condition: byteMatches);
                CurrentBlock = nextByteBlock;
            }

            CurrentBlock = joinBlock;
            return result;
        }

        private bool TryExtractTextSwitchComponents(
            MidLevelIrOperand switchValue,
            out MidLevelIrOperand dataPointer,
            out MidLevelIrOperand length)
        {
            dataPointer = null!;
            length = null!;

            if (!CanUsePartitionedTextSwitchType(switchValue.Type))
            {
                MarkUnsupported();
                return false;
            }

            var byteType = StarkTypeSymbols.Integer(8);
            var dataPointerType = StarkTypeSymbols.RawPointer(byteType, isMutable: false);
            var lengthType = StarkTypeSymbols.Integer(64);

            var extractedDataPointer = EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    switchValue,
                    "data",
                    0,
                    dataPointerType,
                    $"{switchValue.Text}.data"),
                "strdata");
            var extractedLength = EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    switchValue,
                    "length",
                    1,
                    lengthType,
                    $"{switchValue.Text}.length"),
                "strlen");
            if (extractedDataPointer is null || extractedLength is null)
            {
                return false;
            }

            dataPointer = extractedDataPointer;
            length = extractedLength;
            return true;
        }

        private bool EmitTextLiteralMatchTransition(
            MidLevelIrOperand dataPointer,
            byte[] bytes,
            int targetBlockId,
            int nextTarget,
            string text)
        {
            if (bytes.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            var byteType = StarkTypeSymbols.Integer(8);
            for (var index = 0; index < bytes.Length; index++)
            {
                var byteAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        byteType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(byteType, isMutable: false),
                        $"{dataPointer.Text}[{index}]"),
                    "addr");
                if (byteAddress is null)
                {
                    return false;
                }

                var loadedByte = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        byteAddress,
                        byteType,
                        $"{dataPointer.Text}[{index}]"),
                    "load");
                if (loadedByte is null)
                {
                    return false;
                }

                var byteMatches = EmitPairComparison(
                    loadedByte,
                    new MidLevelIrIntegerConstantOperand(ToSignedByteValue(bytes[index]), byteType),
                    "==",
                    $"{text}:byte{index}");
                if (byteMatches is null)
                {
                    return false;
                }

                if (index == bytes.Length - 1)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: byteMatches.Text,
                        Condition: byteMatches);
                    return true;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, nextTarget],
                    ConditionText: byteMatches.Text,
                    Condition: byteMatches);
                CurrentBlock = nextByteBlock;
            }

            return true;
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

            if (switchType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                && (literal.StringLiteral() is not null || literal.CharacterLiteral() is not null))
            {
                return new MidLevelIrStringConstantOperand(literal.GetText(), switchType);
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

        private void TrackDeclaredLocal(string name, StarkTypeSymbol type)
        {
            if (_scopes.Count == 0)
            {
                return;
            }

            _scopes.Peek().Locals.Add((name, type));
        }

        private void EmitStorageDead(ScopeFrame scope)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            for (var index = scope.Locals.Count - 1; index >= 0; index--)
            {
                var (name, type) = scope.Locals[index];
                Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
            }
        }

        private void EmitStorageDeadBeyondDepth(int depth)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            var currentDepth = _scopes.Count;
            foreach (var scope in _scopes)
            {
                if (currentDepth <= depth)
                {
                    break;
                }

                for (var index = scope.Locals.Count - 1; index >= 0; index--)
                {
                    var (name, type) = scope.Locals[index];
                    Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
                }

                currentDepth--;
            }
        }

        private string? ResolveIndirectArgumentLocal(StarkTypeSymbol parameterType, MidLevelIrOperand argument)
        {
            if (!RequiresIndirectArgument(parameterType)
                || argument is not MidLevelIrLocalOperand localOperand)
            {
                return null;
            }

            EnsureAddressableLocal(localOperand.Name);
            return localOperand.Name;
        }

        private static bool RequiresIndirectArgument(StarkTypeSymbol type)
        {
            return type.BorrowKind != StarkBorrowKind.None
                || type.InitializationKind != StarkInitializationKind.None;
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
                "+%" => MidLevelIrBinaryOperator.WrappingAdd,
                "-%" => MidLevelIrBinaryOperator.WrappingSubtract,
                "*%" => MidLevelIrBinaryOperator.WrappingMultiply,
                "+|" => MidLevelIrBinaryOperator.SaturatingAdd,
                "-|" => MidLevelIrBinaryOperator.SaturatingSubtract,
                "*|" => MidLevelIrBinaryOperator.SaturatingMultiply,
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

        private static MidLevelIrBinaryOperator MapAssignmentOperator(string text)
        {
            return text switch
            {
                "+=" => MidLevelIrBinaryOperator.Add,
                "-=" => MidLevelIrBinaryOperator.Subtract,
                "*=" => MidLevelIrBinaryOperator.Multiply,
                "+%=" => MidLevelIrBinaryOperator.WrappingAdd,
                "-%=" => MidLevelIrBinaryOperator.WrappingSubtract,
                "*%=" => MidLevelIrBinaryOperator.WrappingMultiply,
                "+|=" => MidLevelIrBinaryOperator.SaturatingAdd,
                "-|=" => MidLevelIrBinaryOperator.SaturatingSubtract,
                "*|=" => MidLevelIrBinaryOperator.SaturatingMultiply,
                "/=" => MidLevelIrBinaryOperator.Divide,
                "%=" => MidLevelIrBinaryOperator.Modulo,
                "&=" => MidLevelIrBinaryOperator.BitwiseAnd,
                "^=" => MidLevelIrBinaryOperator.BitwiseXor,
                "|=" => MidLevelIrBinaryOperator.BitwiseOr,
                _ => throw new InvalidOperationException($"Unsupported assignment operator '{text}'.")
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

        private void EnsureAddressableLocal(string name)
        {
            if (!_localsByName.TryGetValue(name, out var local) || local.IsAddressable)
            {
                return;
            }

            var addressableLocal = local with { IsAddressable = true };
            _localsByName[name] = addressableLocal;

            for (var index = 0; index < _locals.Count; index++)
            {
                if (string.Equals(_locals[index].Name, name, StringComparison.Ordinal))
                {
                    _locals[index] = addressableLocal;
                    break;
                }
            }
        }

        private MidLevelIrOperand? CreateAddressOfLocal(string name, StarkTypeSymbol type)
        {
            EnsureAddressableLocal(name);
            var isMutable = _localsByName.TryGetValue(name, out var local)
                ? !local.IsConstant && CanMutateThroughType(local.Type)
                : true;
            return EmitTemporary(
                new MidLevelIrAddressOfLocalRValue(name, type, AddressType(type, isMutable), $"&{name}"),
                "addr");
        }

        private MidLevelIrOperand CreateAddressOfGlobal(string name, StarkTypeSymbol type)
        {
            var isMutable = _typeModel.Globals.TryGetValue(name, out var global)
                ? global.IsMutable && CanMutateThroughType(global.Type)
                : true;
            return new MidLevelIrGlobalAddressOperand(name, type, AddressType(type, isMutable));
        }

        private static bool ShouldAddressLocal(StarkTypeSymbol type, string storageClass)
        {
            return storageClass is "heap" or "arena" or "static"
                && type.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
        }

        private static StarkTypeSymbol AddressType(StarkTypeSymbol pointeeType, bool isMutable)
        {
            return StarkTypeSymbols.RawPointer(pointeeType, isMutable);
        }

        private bool GetAddressMutability(MidLevelIrOperand operand)
        {
            return operand switch
                {
                    MidLevelIrLocalOperand local => _localsByName.TryGetValue(local.Name, out var localBinding)
                    ? !localBinding.IsConstant && CanMutateThroughType(localBinding.Type)
                    : true,
                MidLevelIrGlobalOperand global => _typeModel.Globals.TryGetValue(global.Name, out var globalBinding)
                    ? globalBinding.IsMutable && CanMutateThroughType(globalBinding.Type)
                    : true,
                MidLevelIrParameterOperand parameter => CanMutateThroughType(parameter.Type),
                MidLevelIrGlobalAddressOperand globalAddress => globalAddress.Type.IsMutablePointer,
                _ => true
            };
        }

        private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
        {
            return sourceType.AccessKind == StarkAccessKind.Frozen
                ? StarkTypeSymbols.FreezeReachableView(projectedType)
                : projectedType;
        }

        private static bool CanMutateThroughType(StarkTypeSymbol type) => type.AccessKind != StarkAccessKind.Frozen;

        private static bool CanLowerSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Integer
                or StarkTypeKind.Float
                or StarkTypeKind.Bool
                or StarkTypeKind.RawPointer
                or StarkTypeKind.Ascii
                or StarkTypeKind.Unicode
                or StarkTypeKind.Named;
        }

        private static bool CanUsePartitionedTextSwitchType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
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

        private static BigInteger ToSignedByteValue(byte value)
        {
            return value <= sbyte.MaxValue
                ? new BigInteger(value)
                : new BigInteger(unchecked((sbyte)value));
        }

        private static byte[] DecodeTextLiteral(string literalText)
        {
            var kind = literalText.StartsWith('\'')
                ? TextLiteralKind.Character
                : TextLiteralKind.String;
            return TextLiteralDecoder.DecodeUtf8BytesOrFallback(literalText, kind);
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

        private readonly record struct LoopTargets(int ContinueTarget, int BreakTarget, int ScopeDepth);
    }
}
