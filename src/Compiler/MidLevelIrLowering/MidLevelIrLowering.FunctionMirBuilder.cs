using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder : IDisposable
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
            LowerableAggregatePattern? NestedPattern,
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression);

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
            LowerableAggregatePattern? AggregatePattern,
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression = null,
            ImportedTemplateTypedBodyExpressionSummary? ImportedGuardExpression = null);

        private sealed record LowerableSwitchSection(
            StarkParser.SwitchSectionContext Section,
            IReadOnlyList<LowerableSwitchLabel> Labels);

        private sealed record PartitionedTextSwitchLabel(
            LowerableSwitchLabel Label,
            int TargetBlockId,
            int[] Units,
            int Order);

        private enum PlacePathKind
        {
            Field,
            ConstantArrayIndex,
            DynamicArrayIndex,
            RawPointerIndex,
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
            string? RootName,
            MidLevelIrOperand? RootAddress,
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
            MidLevelIrOperand? Address,
            bool ReplacesWholeValue);

        private sealed class ScopeFrame
        {
            public List<(string Name, StarkTypeSymbol Type)> Locals { get; } = [];
        }

        private sealed class DestructorContext : IDisposable
        {
            private readonly FunctionMirBuilder _builder;
            private readonly string? _previousModuleName;
            private readonly string _aliasName;
            private readonly string? _previousAlias;
            private readonly bool _hadAlias;

            public DestructorContext(
                FunctionMirBuilder builder,
                string? previousModuleName,
                string aliasName,
                string? previousAlias,
                bool hadAlias)
            {
                _builder = builder;
                _previousModuleName = previousModuleName;
                _aliasName = aliasName;
                _previousAlias = previousAlias;
                _hadAlias = hadAlias;
            }

            public void Dispose()
            {
                _builder._moduleNameOverride = _previousModuleName;
                if (_hadAlias)
                {
                    _builder._nameAliases[_aliasName] = _previousAlias!;
                }
                else
                {
                    _builder._nameAliases.Remove(_aliasName);
                }
            }
        }

        private readonly HighLevelIrFunction _function;
        private readonly string _currentModuleName;
        private readonly TypeCheckModel _typeModel;
        private readonly EnumLayoutModel _enumLayoutModel;
        private readonly StarkTypeResolver _typeResolver;
        private readonly IReadOnlyDictionary<string, FunctionLoweringContext> _functionsByName;
        private readonly IReadOnlyDictionary<string, DestructorLoweringContext> _destructorsByTypeName;
        private readonly CompilerLogBag _logs;
        private readonly string? _moduleFilePath;
        private readonly SourceLocation _functionLocation;
        private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _fallbackFunctions;
        private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _fallbackGlobals;
        private readonly IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> _literalTypes;
        private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
        private readonly ImportedFunctionTemplateSummary? _importedTemplateSummary;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumConstructorSummary> _importedTemplateEnumConstructors;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumCallSummary> _importedTemplateEnumCalls;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumValueSummary> _importedTemplateEnumValues;
        private readonly IReadOnlyDictionary<int, ImportedTemplateEnumPatternSummary> _importedTemplateEnumPatterns;
        private readonly IReadOnlyDictionary<int, ImportedTemplateAggregatePatternSummary> _importedTemplateAggregatePatterns;
        private readonly IReadOnlyDictionary<string, StarkTypeSymbol> _importedTemplateLocalDeclarations;
        private readonly IReadOnlyDictionary<int, StarkTypeSymbol> _importedTemplateConversions;
        private readonly IReadOnlyDictionary<int, TypedFunctionSignature> _importedTemplateDirectCalls;
        private readonly IReadOnlyDictionary<int, ImportedTemplateFieldAccessSummary> _importedTemplateFieldAccesses;
        private readonly IReadOnlyDictionary<int, TypedFunctionSignature> _importedTemplateMemberCalls;
        private readonly IReadOnlyDictionary<string, string> _materializedSpecializationSymbols;
        private readonly ISet<string>? _genericParameterNames;
        private readonly IReadOnlyDictionary<string, StarkTypeSymbol>? _genericTypeSubstitution;
        private readonly HashSet<string> _unsupportedLogKeys = new(StringComparer.Ordinal);
        private readonly IDisposable _logScope;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly Dictionary<string, bool> _runtimeDropStates = new(StringComparer.Ordinal);
        private readonly List<string> _parameterDropOrder = [];
        private readonly Dictionary<string, string> _nameAliases = new(StringComparer.Ordinal);
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private readonly Stack<BreakTargets> _breakTargets = [];
        private readonly Stack<ScopeFrame> _scopes = [];
        private readonly CompileTimeEvaluator _compileTimeEvaluator;
        private readonly ImportedTemplateLowerer _importedTemplateLowerer;
        private readonly PlaceLowerer _placeLowerer;
        private readonly RuntimeDropLowerer _runtimeDropLowerer;
        private readonly SwitchPatternLowerer _switchPatternLowerer;
        private string? _moduleNameOverride;
        private SourceLocation? _currentStatementLocation;
        private IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int>? _importedObjectCreationOrdinals;
        private IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int>? _importedEnumConstructorOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedEnumCallOrdinals;
        private IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int>? _importedEnumValueOrdinals;
        private IReadOnlyDictionary<ParserRuleContext, int>? _importedEnumPatternOrdinals;
        private IReadOnlyDictionary<StarkParser.UnaryExpressionContext, int>? _importedConversionOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedDirectCallOrdinals;
        private IReadOnlyDictionary<StarkParser.PostfixPartContext, int>? _importedFieldAccessOrdinals;
        private IReadOnlyDictionary<StarkParser.ArgumentListContext, int>? _importedMemberCallOrdinals;
        private int _nextBlockId;
        private int _nextTempId;

        public FunctionMirBuilder(
            HighLevelIrFunction function,
            string currentModuleName,
            TypeCheckModel typeModel,
            EnumLayoutModel enumLayoutModel,
            StarkTypeResolver typeResolver,
            IReadOnlyDictionary<string, FunctionLoweringContext> functionsByName,
            IReadOnlyDictionary<string, DestructorLoweringContext> destructorsByTypeName,
            CompilerLogBag logs,
            string? moduleFilePath,
            SourceLocation functionLocation,
            IReadOnlyDictionary<string, TypedFunctionSignature> fallbackFunctions,
            IReadOnlyDictionary<string, TypedGlobalSymbol> fallbackGlobals,
            IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> literalTypes,
            IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> objectCreationConstructors,
            ImportedFunctionTemplateSummary? importedTemplateSummary,
            IReadOnlyDictionary<string, string> materializedSpecializationSymbols,
            IReadOnlyDictionary<string, StarkTypeSymbol>? genericTypeSubstitution)
        {
            _function = function;
            _currentModuleName = currentModuleName;
            _typeModel = typeModel;
            _enumLayoutModel = enumLayoutModel;
            _typeResolver = typeResolver;
            _functionsByName = functionsByName;
            _destructorsByTypeName = destructorsByTypeName;
            _logs = logs;
            _moduleFilePath = moduleFilePath;
            _functionLocation = functionLocation;
            _fallbackFunctions = fallbackFunctions;
            _fallbackGlobals = fallbackGlobals;
            _literalTypes = literalTypes;
            _objectCreationConstructors = objectCreationConstructors;
            _importedTemplateSummary = importedTemplateSummary;
            _importedTemplateEnumConstructors = importedTemplateSummary?.EnumConstructors.ToDictionary(
                static enumConstructor => enumConstructor.Ordinal,
                static enumConstructor => enumConstructor)
                ?? new Dictionary<int, ImportedTemplateEnumConstructorSummary>();
            _importedTemplateEnumCalls = importedTemplateSummary?.EnumCalls.ToDictionary(
                static enumCall => enumCall.Ordinal,
                static enumCall => enumCall)
                ?? new Dictionary<int, ImportedTemplateEnumCallSummary>();
            _importedTemplateEnumValues = importedTemplateSummary?.EnumValues.ToDictionary(
                static enumValue => enumValue.Ordinal,
                static enumValue => enumValue)
                ?? new Dictionary<int, ImportedTemplateEnumValueSummary>();
            _importedTemplateEnumPatterns = importedTemplateSummary?.EnumPatterns.ToDictionary(
                static enumPattern => enumPattern.Ordinal,
                static enumPattern => enumPattern)
                ?? new Dictionary<int, ImportedTemplateEnumPatternSummary>();
            _importedTemplateAggregatePatterns = importedTemplateSummary?.AggregatePatterns.ToDictionary(
                static aggregatePattern => aggregatePattern.Ordinal,
                static aggregatePattern => aggregatePattern)
                ?? new Dictionary<int, ImportedTemplateAggregatePatternSummary>();
            _importedTemplateLocalDeclarations = importedTemplateSummary?.LocalDeclarations.ToDictionary(
                static local => TemplateLocalDeclarationFacts.BuildLookupKey(local.Kind, local.Line, local.Column),
                static local => local.Type,
                StringComparer.Ordinal)
                ?? new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            _importedTemplateConversions = importedTemplateSummary?.Conversions.ToDictionary(
                static conversion => conversion.Ordinal,
                static conversion => conversion.TargetType)
                ?? new Dictionary<int, StarkTypeSymbol>();
            _importedTemplateDirectCalls = importedTemplateSummary?.DirectCalls.ToDictionary(
                static call => call.Ordinal,
                static call => call.Signature)
                ?? new Dictionary<int, TypedFunctionSignature>();
            _importedTemplateFieldAccesses = importedTemplateSummary?.FieldAccesses.ToDictionary(
                static access => access.Ordinal,
                static access => access)
                ?? new Dictionary<int, ImportedTemplateFieldAccessSummary>();
            _importedTemplateMemberCalls = importedTemplateSummary?.MemberCalls.ToDictionary(
                static call => call.Ordinal,
                static call => call.Signature)
                ?? new Dictionary<int, TypedFunctionSignature>();
            _materializedSpecializationSymbols = materializedSpecializationSymbols;
            _genericParameterNames = function.Signature.IsGeneric
                ? function.Signature.GenericParams.ToHashSet(StringComparer.Ordinal)
                : genericTypeSubstitution is { Count: > 0 }
                    ? genericTypeSubstitution.Keys.ToHashSet(StringComparer.Ordinal)
                    : null;
            _genericTypeSubstitution = genericTypeSubstitution is { Count: > 0 }
                ? new Dictionary<string, StarkTypeSymbol>(genericTypeSubstitution, StringComparer.Ordinal)
                : null;
            _compileTimeEvaluator = new CompileTimeEvaluator(this);
            _importedTemplateLowerer = new ImportedTemplateLowerer(this);
            _placeLowerer = new PlaceLowerer(this);
            _runtimeDropLowerer = new RuntimeDropLowerer(this);
            _switchPatternLowerer = new SwitchPatternLowerer(this);
            _parametersByName = function.Signature.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
            foreach (var parameter in function.Signature.Parameters)
            {
                if (!RequiresRuntimeDrop(parameter.Type))
                {
                    continue;
                }

                _runtimeDropStates[parameter.Name] = true;
                _parameterDropOrder.Add(parameter.Name);
            }

            _logScope = _logs.PushContext(
                stage: "lower-mir",
                symbolName: function.Name,
                location: functionLocation);
            CurrentBlock = CreateBlock("entry");
        }

        public bool SupportsDirectCodeGeneration { get; private set; } = true;

        public int EntryBlockId => 0;

        public IReadOnlyList<MidLevelIrLocal> Locals => _locals;

        public IReadOnlyList<MidLevelIrBasicBlock> Blocks => _blocks
            .Select(static block => block.Build())
            .ToArray();

        private BasicBlockBuilder CurrentBlock { get; set; }
        private string CurrentModuleName => _moduleNameOverride ?? _currentModuleName;

        public void Dispose()
        {
            _logScope.Dispose();
        }

        public void Lower(StarkParser.BlockContext body)
        {
            _importedObjectCreationOrdinals = _importedTemplateSummary is { ObjectCreations.Count: > 0 }
                ? CollectTrackedObjectCreationOrdinals(body)
                : null;
            _importedEnumConstructorOrdinals = _importedTemplateSummary is { EnumConstructors.Count: > 0 }
                ? CollectTemplateEnumConstructorOrdinals(body)
                : null;
            _importedEnumCallOrdinals = _importedTemplateSummary is { EnumCalls.Count: > 0 }
                ? CollectTemplateDirectCallOrdinals(body)
                : null;
            _importedEnumValueOrdinals = _importedTemplateSummary is { EnumValues.Count: > 0 }
                ? CollectTemplateEnumValueOrdinals(body)
                : null;
            _importedEnumPatternOrdinals = _importedTemplateSummary is { } importedTemplateSummary
                && (importedTemplateSummary.EnumPatterns.Count > 0 || importedTemplateSummary.AggregatePatterns.Count > 0)
                ? CollectTemplateEnumPatternOrdinals(body)
                : null;
            _importedConversionOrdinals = _importedTemplateSummary is { Conversions.Count: > 0 }
                ? CollectTemplateConversionOrdinals(body)
                : null;
            _importedDirectCallOrdinals = _importedTemplateSummary is { DirectCalls.Count: > 0 }
                ? CollectTemplateDirectCallOrdinals(body)
                : null;
            _importedFieldAccessOrdinals = _importedTemplateSummary is { FieldAccesses.Count: > 0 }
                ? CollectTemplateFieldAccessOrdinals(body)
                : null;
            _importedMemberCallOrdinals = _importedTemplateSummary is { MemberCalls.Count: > 0 }
                ? CollectTemplateMemberCallOrdinals(body)
                : null;
            LowerBlock(body);

            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = _function.Signature.ReturnType.Kind == StarkTypeKind.Void
                    ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [], Location: _functionLocation)
                    : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: [], Location: _functionLocation);
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
            var previousStatementLocation = _currentStatementLocation;
            _currentStatementLocation = CreateSourceLocation(statement.Start) ?? _functionLocation;

            try
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
                if (_breakTargets.Count == 0)
                {
                    MarkUnsupported(statement.breakStatement(), "'break' requires an enclosing loop or switch.");
                    return;
                }

                var breakTarget = _breakTargets.Peek();
                EmitStorageDeadBeyondDepth(breakTarget.ScopeDepth);
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [breakTarget.Target]);
                return;
            }

            if (statement.continueStatement() is not null)
            {
                if (_loops.Count == 0)
                {
                    MarkUnsupported(statement.continueStatement(), "'continue' requires an enclosing loop.");
                    return;
                }

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
            finally
            {
                _currentStatementLocation = previousStatementLocation;
            }
        }

        private void LowerConstantDeclaration(StarkParser.LocalConstantDeclarationContext declaration)
        {
            var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, declaration, out var publishedType)
                ? publishedType
                : ResolveTypeWithGenericSubstitution(declaration.type_(), CurrentModuleName);
            foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                LowerVariableInitializer(name, declaredType, declarator.variableInitializer());
                InitializeRuntimeDropState(name, declaredType, isActive: true);
            }
        }

        private void LowerVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
        {
            var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.VariableKind, declaration, out var publishedType)
                ? publishedType
                : ResolveTypeWithGenericSubstitution(declaration.type_(), CurrentModuleName);
            var storageClass = declaration.storageClass().GetText();

            foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass, declaration.MUT() is not null, isConstant: false);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                InitializeRuntimeDropState(name, declaredType, isActive: false);

                if (declarator.variableInitializer() is { } initializer)
                {
                    LowerVariableInitializer(name, declaredType, initializer);
                    SetRuntimeDropState(name, isActive: true);
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
                    MarkUnsupported(initializer, "Object initializer lowered without a materialized MIR value.");
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
                    MarkUnsupported(initializer, "Array initializer lowered without a materialized MIR value.");
                    Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType);
                    return;
                }

                Emit(MidLevelIrStatementKind.Assign, $"{name} = {FormatInitializer(initializer)}", name, declaredType, new MidLevelIrUseRValue(value));
                return;
            }

            MarkUnsupported(initializer, "Unsupported variable initializer shape.");
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
            RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            EmitStorageDeadBeyondDepth(0);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: returnStatement.expression().GetText(),
                Value: operand);
        }

        private void LowerExpressionStatement(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionStatementCore(expression))
            {
                return;
            }

            MarkUnsupported(expression, "Expression statement could not be lowered to an assignment, rvalue, or operand.");
            Emit(MidLevelIrStatementKind.Evaluate, expression.GetText());
        }

        private bool TryLowerExpressionStatementCore(StarkParser.ExpressionContext expression)
        {
            if (TryLowerAssignmentExpression(expression.assignmentExpression(), out var assignment))
            {
                EmitAssignment(assignment);
                return true;
            }

            if (TryLowerConditionalCallStatement(expression))
            {
                return true;
            }

            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
                return true;
            }

            if (LowerExpressionToOperand(expression) is { } operand)
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: new MidLevelIrUseRValue(operand));
                return true;
            }

            return false;
        }

        private bool TryLowerConditionalCallStatement(StarkParser.ExpressionContext expression)
        {
            if (!TryGetTernaryConditionalExpression(expression, out var conditionalExpression)
                || !CanLowerConditionalCallStatementBranch(conditionalExpression.expression(0))
                || !CanLowerConditionalCallStatementBranch(conditionalExpression.expression(1)))
            {
                return false;
            }

            var condition = LowerLogicalOrExpression(conditionalExpression.logicalOrExpression(), StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            var thenBlock = CreateBlock("cond_true");
            var elseBlock = CreateBlock("cond_false");
            var joinBlock = CreateBlock("cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: conditionalExpression.logicalOrExpression().GetText(),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerConditionalCallStatementBranch(conditionalExpression.expression(0)))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerConditionalCallStatementBranch(conditionalExpression.expression(1)))
            {
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerConditionalCallStatementBranch(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
                return true;
            }

            return TryLowerConditionalCallStatement(expression);
        }

        private static bool CanLowerConditionalCallStatementBranch(StarkParser.ExpressionContext expression)
        {
            if (TryGetSimplePostfixExpression(expression) is { } postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[^1].argumentList() is not null)
            {
                return true;
            }

            return TryGetTernaryConditionalExpression(expression, out var conditionalExpression)
                && CanLowerConditionalCallStatementBranch(conditionalExpression.expression(0))
                && CanLowerConditionalCallStatementBranch(conditionalExpression.expression(1));
        }

        private static bool TryGetTernaryConditionalExpression(
            StarkParser.ExpressionContext expression,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StarkParser.ConditionalExpressionContext? conditionalExpression)
        {
            conditionalExpression = null;
            var assignmentExpression = expression.assignmentExpression();
            if (assignmentExpression.assignmentOperator() is not null
                || assignmentExpression.conditionalExpression() is not { } conditional
                || conditional.expression().Length != 2)
            {
                return false;
            }

            conditionalExpression = conditional;
            return true;
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
                    Address: address,
                    ReplacesWholeValue: false);
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
                Address: address,
                ReplacesWholeValue: false);
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
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                LowerStatement(whileStatement.statement());
            }
            finally
            {
                _breakTargets.Pop();
                _loops.Pop();
            }
            EnsureGoto(conditionBlock.Id);

            CurrentBlock = exitBlock;
        }

        private void LowerFor(StarkParser.ForStatementContext forStatement)
        {
            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
            {
                var declaredType = TryResolvePublishedLocalDeclarationType(TemplateLocalDeclarationFacts.ForVariableKind, localForVariableDeclaration, out var publishedType)
                    ? publishedType
                    : ResolveTypeWithGenericSubstitution(localForVariableDeclaration.type_(), CurrentModuleName);
                var storageClass = localForVariableDeclaration.storageClass().GetText();

                foreach (var declarator in localForVariableDeclaration.variableDeclarators().variableDeclarator())
                {
                    var name = declarator.Identifier().GetText();
                    RegisterLocal(name, declaredType, storageClass, localForVariableDeclaration.MUT() is not null, isConstant: false);
                    TrackDeclaredLocal(name, declaredType);
                    Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                    InitializeRuntimeDropState(name, declaredType, isActive: false);
                    if (declarator.variableInitializer() is { } initializer)
                    {
                        LowerVariableInitializer(name, declaredType, initializer);
                        SetRuntimeDropState(name, isActive: true);
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
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                LowerStatement(forStatement.statement());
            }
            finally
            {
                _breakTargets.Pop();
                _loops.Pop();
            }
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
            if (_compileTimeEvaluator.TryEvaluateExpression(expression, CurrentModuleName, state: null, activeCalls: null, out var constant))
            {
                if (expectedType is not null
                    && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
                {
                    return CreateCompileTimeOperand(coerced);
                }

                return expectedType is null
                    ? CreateCompileTimeOperand(constant)
                    : CoerceOperand(CreateCompileTimeOperand(constant), expectedType);
            }

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
                var targetType = TryResolvePublishedConversionType(expression, out var publishedTargetType)
                    ? publishedTargetType
                    : ApplyGenericSubstitution(_typeResolver.ResolveConversionType(conversionType, _genericParameterNames, CurrentModuleName));
                var convertedOperand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
                if (convertedOperand is null)
                {
                    return null;
                }

                var converted = CoerceOperand(convertedOperand, targetType);
                return expectedType is null ? converted : CoerceOperand(converted, expectedType);
            }

            var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();
            if (op == "&")
            {
                var address = LowerAddressOfUnary(expression.unaryExpression());
                return expectedType is null ? address : CoerceOperand(address, expectedType);
            }

            var operand = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (operand is null)
            {
                return null;
            }

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
                "*" => LowerDereferenceUnary(expression, operand),
                _ => UnsupportedOperand()
            };

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerAddressOfUnary(StarkParser.UnaryExpressionContext operandExpression)
        {
            if (operandExpression.conversionType() is null
                && operandExpression.powerExpression() is null
                && string.Equals(operandExpression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return LowerUnaryExpression(operandExpression.unaryExpression(), expectedType: null);
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
            if (resultType.Kind is not (StarkTypeKind.Float or StarkTypeKind.Integer))
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
                    if (TryLowerPublishedEnumCall(argumentList, out var publishedEnumCall))
                    {
                        currentValue = publishedEnumCall;
                        currentName = null;
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

                if (postfixPart.GetChild(0).GetText() == "[")
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

                    if (postfixPart.expressionList() is { } expressionList)
                    {
                        currentValue = LowerIndexAccess(currentValue, expressionList);
                        if (currentValue is null)
                        {
                            return false;
                        }
                    }
                    else if (currentValue.Type.Kind is not StarkTypeKind.Ascii and not StarkTypeKind.Unicode)
                    {
                        MarkUnsupported(reason: "Index access currently requires at least one index expression.");
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
                    if (!(TryBuildPublishedMemberCall(currentValue, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall)
                          || TryBuildMemberCall(currentValue, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out memberCall)))
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
                    currentValue = TryLowerPublishedFieldAccess(currentValue, postfixPart, out var publishedFieldAccess)
                        ? publishedFieldAccess
                        : LowerFieldAccess(currentValue, memberName);
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

            if (TryLowerPublishedEnumValue(expression, out currentValue))
            {
                currentName = null;
                return currentValue is not null;
            }

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

            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                if (!TryBuildGenericEnumCaseName(genericEnumCaseReference, out var genericEnumCaseName))
                {
                    return false;
                }

                currentValue = TryResolveNamedValueOperand(genericEnumCaseName);
                currentName = currentValue is null ? genericEnumCaseName : null;
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

            if (TryLowerPublishedEnumValue(expression, out var publishedEnumValue))
            {
                return publishedEnumValue is null || expectedType is null ? publishedEnumValue : CoerceOperand(publishedEnumValue, expectedType);
            }

            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return !TryBuildGenericEnumCaseName(genericEnumCaseReference, out var genericEnumCaseName)
                    ? null
                    : ResolveNamedOperand(genericEnumCaseName);
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
            TryGetPublishedObjectCreationSummary(expression, out var publishedObjectCreation);
            var createdType = publishedObjectCreation is not null
                ? ApplyGenericSubstitution(publishedObjectCreation.CreatedType)
                : ResolveTypeWithGenericSubstitution(expression.type_(), CurrentModuleName);
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
                var initialized = LowerObjectInitializer(
                    createdType,
                    current,
                    objectInitializer,
                    publishedObjectCreation?.InitializerMembers);
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
            return LowerObjectInitializer(targetType, new MidLevelIrZeroInitializerOperand(targetType), objectInitializer, publishedInitializerMembers: null);
        }

        private MidLevelIrOperand? LowerObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            StarkParser.ObjectInitializerContext objectInitializer,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedInitializerMembers)
        {
            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null)
            {
                MarkUnsupported();
                return null;
            }

            _typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType);
            var current = seed;

            for (var index = 0; index < objectInitializer.memberInitializer().Length; index++)
            {
                var initializer = objectInitializer.memberInitializer(index);
                var fieldName = initializer.Identifier().GetText();
                var fieldType = StarkTypeSymbols.Error;
                var fieldIndex = -1;

                if (publishedInitializerMembers is { Count: > 0 } && index < publishedInitializerMembers.Count)
                {
                    var publishedMember = publishedInitializerMembers[index];
                    fieldName = publishedMember.FieldName;
                    fieldIndex = publishedMember.FieldIndex;
                    fieldType = ApplyGenericSubstitution(publishedMember.FieldType);
                }
                else if (namedType is null
                         || !namedType.TryGetField(fieldName, out var field, out fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }
                else
                {
                    fieldType = field.Type;
                }

                var memberInitializer = initializer.variableInitializer();
                var value = LowerInitializerToOperand(memberInitializer, fieldType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        fieldName,
                        fieldIndex,
                        value,
                        targetType,
                        $"{current.Text}.{fieldName} = {memberInitializer.GetText()}"),
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
            string constructorName;
            StarkTypeSymbol enumType;
            EnumLayoutSymbol layout;
            EnumVariantLayoutSymbol variant;
            ImportedTemplateEnumConstructorSummary? publishedEnumConstructor = null;

            if (TryGetPublishedEnumConstructorSummary(expression, out var publishedSummary)
                && publishedSummary is not null)
            {
                publishedEnumConstructor = publishedSummary;
                enumType = ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
                constructorName = $"{enumType.DisplayName}.{publishedEnumConstructor.VariantName}";

                if (!TryGetEnumLayout(enumType, out layout)
                    || !layout.TryGetVariant(publishedEnumConstructor.VariantName, out variant))
                {
                    MarkUnsupported();
                    return null;
                }
            }
            else
            {
                constructorName = expression.enumCaseTarget().GetText();
                if (!TryResolveEnumCaseTarget(expression.enumCaseTarget(), out _, out enumType, out layout, out variant))
                {
                    MarkUnsupported();
                    return null;
                }
            }

            if (!variant.UsesNamedFields)
            {
                MarkUnsupported();
                return null;
            }

            var memberValues = new Dictionary<int, MidLevelIrOperand>();
            for (var memberOrdinal = 0; memberOrdinal < expression.enumConstructorInitializer().enumConstructorMember().Length; memberOrdinal++)
            {
                var member = expression.enumConstructorInitializer().enumConstructorMember(memberOrdinal);
                var memberName = member.Identifier().GetText();
                EnumVariantLayoutFieldSymbol? layoutField = null;
                var fieldIndex = -1;

                if (publishedEnumConstructor is not null && memberOrdinal < publishedEnumConstructor.Members.Count)
                {
                    var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                    memberName = publishedMember.FieldName;
                    fieldIndex = publishedMember.FieldIndex;
                    if (fieldIndex >= 0 && fieldIndex < variant.Fields.Count)
                    {
                        layoutField = variant.Fields[fieldIndex];
                    }
                }
                else
                {
                    for (var fieldOrdinal = 0; fieldOrdinal < variant.Fields.Count; fieldOrdinal++)
                    {
                        var candidate = variant.Fields[fieldOrdinal];
                        if (!string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        layoutField = candidate;
                        fieldIndex = fieldOrdinal;
                        break;
                    }
                }

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

                memberValues[fieldIndex] = coerced;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                if (!memberValues.TryGetValue(index, out var value))
                {
                    MarkUnsupported();
                    return null;
                }

                orderedValues[index] = value;
            }

            var lowered = LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, expression.GetText());
            return lowered is null || expectedType is null ? lowered : CoerceOperand(lowered, expectedType);
        }

        private bool TryLowerPublishedEnumCall(
            StarkParser.ArgumentListContext arguments,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolvePublishedEnumCallSummary(arguments, out var publishedEnumCall))
            {
                return false;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumCall.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumCall.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields)
            {
                MarkUnsupported();
                return true;
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
                    return true;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    return true;
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, $"{publishedCaseName}{arguments.GetText()}");
            return true;
        }

        private bool TryLowerPublishedEnumValue(
            StarkParser.PrimaryExpressionContext expression,
            out MidLevelIrOperand? value)
        {
            value = null;

            if (!TryResolvePublishedEnumValueSummary(expression, out var publishedEnumValue))
            {
                return false;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumValue.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumValue.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.Fields.Count != 0)
            {
                MarkUnsupported();
                return true;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, [], publishedCaseName);
            return true;
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

        private bool TryGetPublishedEnumConstructorSummary(
            StarkParser.EnumConstructorExpressionContext expression,
            out ImportedTemplateEnumConstructorSummary? summary)
        {
            if (_importedEnumConstructorOrdinals is null
                || !_importedEnumConstructorOrdinals.TryGetValue(expression, out var ordinal)
                || !_importedTemplateEnumConstructors.TryGetValue(ordinal, out var publishedSummary))
            {
                summary = null;
                return false;
            }

            summary = publishedSummary;
            return true;
        }

        private bool TryGetMatchedObjectCreationConstructor(
            StarkParser.ObjectCreationExpressionContext expression,
            out TypedConstructorShape? constructor)
        {
            if (TryGetPublishedObjectCreationSummary(expression, out var importedObjectCreation))
            {
                constructor = importedObjectCreation.Constructor;
                return true;
            }

            return _objectCreationConstructors.TryGetValue(
                new ObjectCreationKey(
                    expression.GetText(),
                    expression.Start.Line,
                    expression.Start.Column + 1),
                out constructor);
        }

        private bool TryGetPublishedObjectCreationSummary(
            StarkParser.ObjectCreationExpressionContext expression,
            out ImportedTemplateObjectCreationSummary importedObjectCreation)
        {
            importedObjectCreation = default!;

            if (_importedTemplateSummary is not { ObjectCreations.Count: > 0 } importedTemplateSummary
                || _importedObjectCreationOrdinals is null
                || !_importedObjectCreationOrdinals.TryGetValue(expression, out var ordinal)
                || ordinal >= importedTemplateSummary.ObjectCreations.Count)
            {
                return false;
            }

            importedObjectCreation = importedTemplateSummary.ObjectCreations[ordinal];
            return true;
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

            var projectedType = ProjectProjectionType(target, field.Type);

            return EmitTemporary(
                new MidLevelIrExtractFieldRValue(
                    target,
                    field.Name,
                    fieldIndex,
                    projectedType,
                    $"{target.Text}.{field.Name}"),
                "field");
        }

        private MidLevelIrOperand LowerKnownFieldAccess(
            MidLevelIrOperand target,
            string fieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            string displayFieldName)
        {
            var projectedType = ProjectProjectionType(target, fieldType);
            return EmitRequiredTemporary(
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
            if (CanUsePartitionedTextSwitchType(target.Type))
            {
                return LowerTextAccess(target, indexes);
            }

            var current = target;
            var currentUsesFrozenProjectionSemantics = UsesFrozenProjectionSemantics(current);

            foreach (var indexExpression in indexes.expression())
            {
                if (current.Type.Kind == StarkTypeKind.FixedArray && current.Type.ElementType is not null)
                {
                    if (TryResolveConstantArrayIndex(current.Type, indexExpression, out var constantIndex, out var resolvedElementType))
                    {
                        var elementType = currentUsesFrozenProjectionSemantics
                            ? StarkTypeSymbols.FreezeReachableView(resolvedElementType)
                            : ProjectFrozenView(current.Type, resolvedElementType);
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
                        currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                        continue;
                    }

                    if (current.Type.ElementType is null)
                    {
                        MarkUnsupported(indexes, "Dynamic fixed-array indexing currently requires an addressable fixed-array source.");
                        return null;
                    }

                    var projectedElementType = currentUsesFrozenProjectionSemantics
                        ? StarkTypeSymbols.FreezeReachableView(current.Type.ElementType)
                        : ProjectFrozenView(current.Type, current.Type.ElementType);
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Dynamic fixed-array indexing requires an integer index operand.");
                        return null;
                    }

                    var baseAddress = TryCreateDynamicFixedArrayBaseAddress(current);
                    if (baseAddress is null)
                    {
                        MarkUnsupported(indexes, "Dynamic fixed-array indexing currently requires an addressable fixed-array source.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            baseAddress,
                            current.Type,
                            index,
                            ConstantIndex: null,
                            AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            projectedElementType,
                            $"{current.Text}[{indexExpression.GetText()}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.Slice && current.Type.ElementType is not null)
                {
                    var elementType = currentUsesFrozenProjectionSemantics
                        ? StarkTypeSymbols.FreezeReachableView(current.Type.ElementType)
                        : ProjectFrozenView(current.Type, current.Type.ElementType);
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Slice indexing requires an integer index operand.");
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
                    currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.RawPointer && current.Type.ElementType is not null)
                {
                    var elementType = current.Type.ElementType;
                    var index = LowerExpressionToOperand(indexExpression);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        MarkUnsupported(indexExpression, "Raw pointer indexing requires an integer index operand.");
                        return null;
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            current,
                            elementType,
                            index,
                            ConstantIndex: null,
                            AddressType(elementType, current.Type.IsMutablePointer && CanMutateThroughType(elementType)),
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
                    currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                    continue;
                }

                MarkUnsupported(indexes, "Indexing is only supported for fixed arrays, raw pointers, slices, ascii, and unicode values.");
                return null;
            }

            return current;
        }

        private MidLevelIrOperand? LowerTextAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var indexExpressions = indexes.expression();
            if (indexExpressions.Length == 0)
            {
                return target;
            }

            if (indexExpressions.Length == 1)
            {
                var start = LowerExpressionToOperand(indexExpressions[0]);
                if (start is null || start.Type.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported(indexes, "Text indexing currently requires an integer index operand.");
                    return null;
                }

                return LowerTextSlice(
                    target,
                    start,
                    new MidLevelIrIntegerConstantOperand(BigInteger.One, StarkTypeSymbols.Integer(64)),
                    $"{target.Text}[{indexExpressions[0].GetText()}]");
            }

            if (indexExpressions.Length != 2)
            {
                MarkUnsupported(indexes, "Text indexing currently requires exactly one integer index or two integer indices.");
                return null;
            }

            var sliceStart = LowerExpressionToOperand(indexExpressions[0]);
            var sliceLength = LowerExpressionToOperand(indexExpressions[1]);
            if (sliceStart is null
                || sliceLength is null
                || sliceStart.Type.Kind != StarkTypeKind.Integer
                || sliceLength.Type.Kind != StarkTypeKind.Integer)
            {
                MarkUnsupported(indexes, "Text slicing currently requires integer start and length operands.");
                return null;
            }

            return LowerTextSlice(
                target,
                sliceStart,
                sliceLength,
                $"{target.Text}[{indexExpressions[0].GetText()}, {indexExpressions[1].GetText()}]");
        }

        private MidLevelIrOperand? LowerTextSlice(
            MidLevelIrOperand target,
            MidLevelIrOperand start,
            MidLevelIrOperand length,
            string text)
        {
            var coercedStart = CoerceOperand(start, StarkTypeSymbols.Integer(64));
            var coercedLength = CoerceOperand(length, StarkTypeSymbols.Integer(64));
            if (coercedStart is null || coercedLength is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrTextSliceRValue(
                    target,
                    coercedStart,
                    coercedLength,
                    target.Type,
                    text),
                "slice");
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

            if (TryGetFunctionOverloads(functionName, out var overloads))
            {
                return TryBuildOverloadedCall(overloads, receiver: null, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(functionName, out var signature))
            {
                if (!TryResolvePublishedDirectCallSignature(arguments, out signature))
                {
                    return false;
                }
            }

            return TryBuildCall(signature.Name, signature, receiver: null, arguments, text, out call);
        }

        private bool TryBuildMemberCall(
            MidLevelIrOperand receiver,
            string memberName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (receiver.Type.NamedType is not { } namedTypeName)
            {
                return false;
            }

            var sourceName = $"{namedTypeName}.{memberName}";
            if (TryGetFunctionOverloads(sourceName, out var overloads))
            {
                return TryBuildOverloadedCall(overloads, receiver, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(sourceName, out var signature)
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            return TryBuildCall(signature.Name, signature, receiver, arguments, text, out call);
        }

        private bool TryBuildPublishedMemberCall(
            MidLevelIrOperand receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (_importedMemberCallOrdinals is not { } memberCallOrdinals
                || !memberCallOrdinals.TryGetValue(arguments, out var memberCallOrdinal)
                || !_importedTemplateMemberCalls.TryGetValue(memberCallOrdinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            return TryBuildCall(signature.Name, signature, receiver, arguments, text, out call);
        }

        private bool TryBuildOverloadedCall(
            IReadOnlyList<TypedFunctionSignature> overloads,
            MidLevelIrOperand? receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            var loweredArguments = new List<MidLevelIrOperand>(arguments.argument().Length);
            foreach (var argument in arguments.argument())
            {
                var lowered = LowerExpressionToOperand(argument.expression(), expectedType: null);
                if (lowered is null)
                {
                    return false;
                }

                loweredArguments.Add(lowered);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiver?.Type,
                loweredArguments.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return false;
            }

            return TryBuildCall(
                resolution.Match!.Name,
                resolution.Match,
                receiver,
                arguments,
                text,
                out call,
                loweredArguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call,
            IReadOnlyList<MidLevelIrOperand>? loweredExplicitArguments = null)
        {
            var explicitArguments = new List<MidLevelIrOperand>(Math.Max(
                loweredExplicitArguments?.Count ?? 0,
                arguments.argument().Length));
            if (loweredExplicitArguments is not null)
            {
                explicitArguments.AddRange(loweredExplicitArguments);
            }
            else
            {
                foreach (var argument in arguments.argument())
                {
                    var lowered = LowerExpressionToOperand(argument.expression(), expectedType: null);
                    if (lowered is null)
                    {
                        call = default!;
                        return false;
                    }

                    explicitArguments.Add(lowered);
                }
            }

            return TryBuildCall(functionName, signature, receiver, text, out call, explicitArguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            string text,
            out MidLevelIrCallRValue call,
            IReadOnlyList<MidLevelIrOperand> loweredExplicitArguments)
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
                RecordMoveFromOperand(receiverOperand, signature.Parameters[0].Type);
            }

            for (var index = 0; index < Math.Min(loweredExplicitArguments.Count, explicitParameterCount); index++)
            {
                var parameterType = signature.Parameters[index + receiverOffset].Type;
                var argument = CoerceOperand(loweredExplicitArguments[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                indirectArgumentLocals.Add(ResolveIndirectArgumentLocal(parameterType, argument));
                RecordMoveFromOperand(argument, parameterType);
            }

            if (loweredExplicitArguments.Count != explicitParameterCount)
            {
                return false;
            }

            var loweredFunctionName = ResolveCallTargetName(functionName, signature);
            call = new MidLevelIrCallRValue(
                loweredFunctionName,
                loweredArguments,
                signature.ReturnType,
                text,
                indirectArgumentLocals);
            return true;
        }

        private string ResolveCallTargetName(string fallbackFunctionName, TypedFunctionSignature signature)
        {
            if (!signature.IsGenericInstantiation
                || signature.TemplateName is not { } templateName
                || signature.TypeArguments is not { Count: > 0 } typeArguments)
            {
                return fallbackFunctionName;
            }

            var specializationKey = MidLevelIrLowerer.BuildMaterializedSpecializationKey(templateName, typeArguments);
            return _materializedSpecializationSymbols.TryGetValue(specializationKey, out var materializedSymbol)
                ? materializedSymbol
                : fallbackFunctionName;
        }

        private MidLevelIrOperand? LowerLiteral(StarkParser.LiteralContext literal, StarkTypeSymbol? expectedType)
        {
            var literalType = LookupLiteralType(literal);
            var operand = CreateLiteralOperand(literal.GetText(), literalType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private static MidLevelIrOperand CreateCompileTimeOperand(CompileTimeConstant constant)
        {
            return constant.Kind switch
            {
                CompileTimeConstantKind.Integer => new MidLevelIrIntegerConstantOperand(constant.IntegerValue, constant.Type),
                CompileTimeConstantKind.Float => new MidLevelIrFloatConstantOperand(CompileTimeExpressionEvaluator.FormatFloatLiteral(constant), constant.Type),
                CompileTimeConstantKind.Bool => new MidLevelIrBoolConstantOperand(constant.BoolValue),
                CompileTimeConstantKind.Null => new MidLevelIrNullOperand(constant.Type),
                CompileTimeConstantKind.Text when constant.TextLiteral is not null => new MidLevelIrStringConstantOperand(constant.TextLiteral, constant.Type),
                _ => throw new InvalidOperationException($"Unsupported compile-time constant kind '{constant.Kind}'.")
            };
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

        private static MidLevelIrOperand CreateLiteralOperand(string literalText, StarkTypeSymbol type)
        {
            if (literalText.Length > 0 && literalText[0] == '\'')
            {
                return new MidLevelIrStringConstantOperand(literalText, type);
            }

            if (literalText.Length > 0 && literalText[0] == '"')
            {
                return new MidLevelIrStringConstantOperand(literalText, type);
            }

            if (string.Equals(literalText, "true", StringComparison.Ordinal))
            {
                return new MidLevelIrBoolConstantOperand(true);
            }

            if (string.Equals(literalText, "false", StringComparison.Ordinal))
            {
                return new MidLevelIrBoolConstantOperand(false);
            }

            if (string.Equals(literalText, "null", StringComparison.Ordinal))
            {
                return new MidLevelIrNullOperand(type);
            }

            if (type.Kind == StarkTypeKind.Float)
            {
                return new MidLevelIrFloatConstantOperand(literalText, type);
            }

            return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteralText(literalText), type);
        }

        private static StarkTypeSymbol InferTextLiteralType(string text, TextLiteralKind kind)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(text, kind)
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
            if (_nameAliases.TryGetValue(name, out var aliasedName))
            {
                name = aliasedName;
            }

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

        private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
        {
            return TryGetFunctionOverloads(sourceName, CurrentModuleName, out overloads);
        }

        private bool TryGetFunctionOverloads(string sourceName, string currentModuleName, out IReadOnlyList<TypedFunctionSignature> overloads)
        {
            if (!sourceName.Contains('.', StringComparison.Ordinal)
                && _typeModel.Overloads.TryGetValue($"{currentModuleName}.{sourceName}", out overloads!))
            {
                return true;
            }

            if (_typeModel.Overloads.TryGetValue(sourceName, out overloads!))
            {
                return true;
            }

            overloads = [];
            return false;
        }

        private bool TryResolveFunctionSignature(string name, out TypedFunctionSignature signature)
        {
            return TryResolveFunctionSignature(name, CurrentModuleName, out signature);
        }

        private bool TryResolveFunctionSignature(string name, string currentModuleName, out TypedFunctionSignature signature)
        {
            if (!name.Contains('.', StringComparison.Ordinal)
                && _typeModel.Functions.TryGetValue($"{currentModuleName}.{name}", out signature!))
            {
                return true;
            }

            if (_typeModel.Functions.TryGetValue(name, out signature!))
            {
                return true;
            }

            if (TryGetFunctionOverloads(name, currentModuleName, out var overloads) && overloads.Count == 1)
            {
                signature = overloads[0];
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackFunctions.TryGetValue($"{currentModuleName}.{name}", out signature!))
            {
                return true;
            }

            return _fallbackFunctions.TryGetValue(name, out signature!);
        }

        private bool TryResolveGlobal(string name, out TypedGlobalSymbol global)
        {
            if (!name.Contains('.', StringComparison.Ordinal)
                && _typeModel.Globals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
            {
                return true;
            }

            if (_typeModel.Globals.TryGetValue(name, out global!))
            {
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackGlobals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
            {
                return true;
            }

            return _fallbackGlobals.TryGetValue(name, out global!);
        }

        private StarkTypeSymbol ResolveTypeWithGenericSubstitution(
            StarkParser.Type_Context type,
            string? moduleName)
        {
            return ApplyGenericSubstitution(
                _typeResolver.ResolveType(type, _genericParameterNames, moduleName));
        }

        private bool TryResolvePublishedLocalDeclarationType(
            string declarationKind,
            ParserRuleContext declarationContext,
            out StarkTypeSymbol type)
        {
            if (_importedTemplateLocalDeclarations.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                        declarationKind,
                        declarationContext.Start.Line,
                        declarationContext.Start.Column + 1),
                    out var publishedType))
            {
                type = ApplyGenericSubstitution(publishedType);
                return true;
            }

            type = StarkTypeSymbols.Error;
            return false;
        }

        private bool TryResolvePublishedDirectCallSignature(
            StarkParser.ArgumentListContext arguments,
            out TypedFunctionSignature signature)
        {
            if (_importedDirectCallOrdinals is { } directCallOrdinals
                && directCallOrdinals.TryGetValue(arguments, out var directCallOrdinal)
                && _importedTemplateDirectCalls.TryGetValue(directCallOrdinal, out var publishedSignature))
            {
                signature = ApplyGenericSubstitution(publishedSignature);
                return true;
            }

            signature = null!;
            return false;
        }

        private bool TryResolvePublishedEnumCallSummary(
            StarkParser.ArgumentListContext arguments,
            out ImportedTemplateEnumCallSummary summary)
        {
            if (_importedEnumCallOrdinals is { } enumCallOrdinals
                && enumCallOrdinals.TryGetValue(arguments, out var enumCallOrdinal)
                && _importedTemplateEnumCalls.TryGetValue(enumCallOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedEnumValueSummary(
            StarkParser.PrimaryExpressionContext expression,
            out ImportedTemplateEnumValueSummary summary)
        {
            if (_importedEnumValueOrdinals is { } enumValueOrdinals
                && enumValueOrdinals.TryGetValue(expression, out var enumValueOrdinal)
                && _importedTemplateEnumValues.TryGetValue(enumValueOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedEnumPatternSummary(
            ParserRuleContext patternContext,
            out ImportedTemplateEnumPatternSummary summary)
        {
            if (_importedEnumPatternOrdinals is { } enumPatternOrdinals
                && enumPatternOrdinals.TryGetValue(patternContext, out var enumPatternOrdinal)
                && _importedTemplateEnumPatterns.TryGetValue(enumPatternOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedAggregatePatternSummary(
            StarkParser.AggregatePatternContext patternContext,
            out ImportedTemplateAggregatePatternSummary summary)
        {
            if (_importedEnumPatternOrdinals is { } patternOrdinals
                && patternOrdinals.TryGetValue(patternContext, out var patternOrdinal)
                && _importedTemplateAggregatePatterns.TryGetValue(patternOrdinal, out var publishedSummary))
            {
                summary = publishedSummary;
                return true;
            }

            summary = null!;
            return false;
        }

        private bool TryResolvePublishedConversionType(
            StarkParser.UnaryExpressionContext expression,
            out StarkTypeSymbol type)
        {
            if (_importedConversionOrdinals is { } conversionOrdinals
                && conversionOrdinals.TryGetValue(expression, out var conversionOrdinal)
                && _importedTemplateConversions.TryGetValue(conversionOrdinal, out var publishedType))
            {
                type = ApplyGenericSubstitution(publishedType);
                return true;
            }

            type = StarkTypeSymbols.Error;
            return false;
        }

        private bool TryLowerPublishedFieldAccess(
            MidLevelIrOperand target,
            StarkParser.PostfixPartContext postfixPart,
            out MidLevelIrOperand? fieldValue)
        {
            fieldValue = null;

            if (_importedFieldAccessOrdinals is not { } fieldAccessOrdinals
                || !fieldAccessOrdinals.TryGetValue(postfixPart, out var fieldAccessOrdinal)
                || !_importedTemplateFieldAccesses.TryGetValue(fieldAccessOrdinal, out var publishedFieldAccess))
            {
                return false;
            }

            fieldValue = LowerKnownFieldAccess(
                target,
                publishedFieldAccess.FieldName,
                publishedFieldAccess.FieldIndex,
                ApplyGenericSubstitution(publishedFieldAccess.FieldType),
                publishedFieldAccess.FieldName);
            return true;
        }

        private StarkTypeSymbol ApplyGenericSubstitution(StarkTypeSymbol type)
        {
            return _genericTypeSubstitution is { Count: > 0 }
                ? FunctionOverloadFacts.SubstituteType(type, _genericTypeSubstitution)
                : type;
        }

        private TypedFunctionSignature ApplyGenericSubstitution(TypedFunctionSignature signature)
        {
            if (_genericTypeSubstitution is not { Count: > 0 })
            {
                return signature;
            }

            return signature with
            {
                ReturnType = ApplyGenericSubstitution(signature.ReturnType),
                Parameters = signature.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        ApplyGenericSubstitution(parameter.Type)))
                    .ToArray(),
                TypeArguments = signature.TypeArguments is { Count: > 0 }
                    ? signature.TypeArguments.Select(ApplyGenericSubstitution).ToArray()
                    : null
            };
        }

        private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
        {
            var baseName = genericQualifiedName.qualifiedName().GetText();
            var baseType = ApplyGenericSubstitution(
                _typeResolver.ResolveQualifiedType(baseName, _genericParameterNames, genericQualifiedName.qualifiedName().Start, CurrentModuleName));
            if (baseType.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
            }

            var typeArguments = genericQualifiedName.typeArgumentList().type_()
                .Select(typeArgument => ResolveTypeWithGenericSubstitution(typeArgument, CurrentModuleName))
                .ToArray();
            if (typeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
            {
                return StarkTypeSymbols.Error;
            }

            return StarkTypeSymbols.GenericInstantiation(baseType.NamedType ?? baseName, typeArguments);
        }

        private bool TryBuildGenericEnumCaseName(
            StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
            out string name)
        {
            name = string.Empty;

            var enumType = ResolveGenericQualifiedName(genericEnumCaseReference.genericQualifiedName());
            if (enumType.Kind != StarkTypeKind.Named || enumType.NamedType is null)
            {
                return false;
            }

            name = $"{enumType.NamedType}.{genericEnumCaseReference.Identifier().GetText()}";
            return true;
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

        private bool TryResolveEnumCaseReference(
            StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            return TryBuildGenericEnumCaseName(genericEnumCaseReference, out var name)
                && TryResolveEnumCaseReference(name, out enumType, out layout, out variant);
        }

        private bool TryResolveEnumCaseTarget(
            StarkParser.EnumCaseTargetContext enumCaseTarget,
            out string caseName,
            out StarkTypeSymbol enumType,
            out EnumLayoutSymbol layout,
            out EnumVariantLayoutSymbol variant)
        {
            caseName = enumCaseTarget.GetText();
            enumType = StarkTypeSymbols.Error;
            layout = null!;
            variant = null!;

            if (enumCaseTarget.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out layout, out variant);
            }

            return TryResolveEnumCaseReference(enumCaseTarget.dottedName().GetText(), out enumType, out layout, out variant);
        }

        private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
        {
            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            if (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!))
            {
                return true;
            }

            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _typeModel.NamedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            namedType = null!;
            return false;
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
                if (TryConvertTextLiteral(operand, targetType, out var convertedTextLiteral))
                {
                    return convertedTextLiteral;
                }

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

        private MidLevelIrOperand EmitResolvedEqualityComparison(MidLevelIrOperand left, MidLevelIrOperand right, string text)
        {
            return EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Equal,
                    left,
                    right,
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

        private MidLevelIrOperand? EmitImportedTypedTemplateSwitchLiteralComparison(
            MidLevelIrOperand switchValue,
            ImportedTemplateTypedBodyExpressionSummary literalExpression,
            string text)
        {
            return _importedTemplateLowerer.EmitSwitchLiteralComparison(switchValue, literalExpression, text);
        }

        private MidLevelIrOperand? EmitImportedTypedTemplateSwitchLiteralComparisonCore(
            MidLevelIrOperand switchValue,
            ImportedTemplateTypedBodyExpressionSummary literalExpression,
            string text)
        {
            var literalOperand = LowerImportedTypedTemplateExpression(literalExpression, switchValue.Type);
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
                decisionBlocks[index] = CreateBlock($"textcmp_len_{labels[0].Units.Length}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : defaultTarget;

                if (!EmitTextLiteralMatchTransition(
                    dataPointer,
                    label.Units,
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
            var units = DecodeTextLiteralUnits(literal.LiteralText, switchValue.Type);
            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return null;
            }

            var unitType = GetTextUnitType(switchValue.Type);
            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthMatches = EmitPairComparison(
                length,
                new MidLevelIrIntegerConstantOperand(new BigInteger(units.Length), lengthType),
                "==",
                $"{text}:length");
            if (lengthMatches is null || units.Length == 0)
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

            for (var index = 0; index < units.Length; index++)
            {
                var unitAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        unitType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(unitType, isMutable: false),
                        $"{switchValue.Text}.data[{index}]"),
                    "addr");
                if (unitAddress is null)
                {
                    return null;
                }

                var loadedUnit = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        unitAddress,
                        unitType,
                        $"{switchValue.Text}.data[{index}]"),
                    "load");
                if (loadedUnit is null)
                {
                    return null;
                }

                var unitMatches = EmitPairComparison(
                    loadedUnit,
                    CreateTextUnitConstant(units[index], unitType),
                    "==",
                    $"{text}:unit{index}");
                if (unitMatches is null)
                {
                    return null;
                }

                if (index == units.Length - 1)
                {
                    EmitOperandAssignment(result, unitMatches, unitMatches.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, falseBlock.Id],
                    ConditionText: unitMatches.Text,
                    Condition: unitMatches);
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

            var unitType = GetTextUnitType(switchValue.Type);
            var dataPointerType = StarkTypeSymbols.RawPointer(unitType, isMutable: false);
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
            int[] units,
            int targetBlockId,
            int nextTarget,
            string text)
        {
            if (units.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            var unitType = dataPointer.Type.ElementType ?? throw new InvalidOperationException("Text switch data pointer requires an element type.");
            for (var index = 0; index < units.Length; index++)
            {
                var unitAddress = EmitTemporary(
                    new MidLevelIrElementAddressRValue(
                        dataPointer,
                        unitType,
                        Index: null,
                        ConstantIndex: index,
                        AddressType(unitType, isMutable: false),
                        $"{dataPointer.Text}[{index}]"),
                    "addr");
                if (unitAddress is null)
                {
                    return false;
                }

                var loadedUnit = EmitTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        unitAddress,
                        unitType,
                        $"{dataPointer.Text}[{index}]"),
                    "load");
                if (loadedUnit is null)
                {
                    return false;
                }

                var unitMatches = EmitPairComparison(
                    loadedUnit,
                    CreateTextUnitConstant(units[index], unitType),
                    "==",
                    $"{text}:unit{index}");
                if (unitMatches is null)
                {
                    return false;
                }

                if (index == units.Length - 1)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: unitMatches.Text,
                        Condition: unitMatches);
                    return true;
                }

                var nextByteBlock = CreateBlock($"textcmp_byte_{index + 1}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextByteBlock.Id, nextTarget],
                    ConditionText: unitMatches.Text,
                    Condition: unitMatches);
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

        private MidLevelIrOperand EmitRequiredTemporary(MidLevelIrRValue value, string hint)
        {
            return EmitTemporary(value, hint)!;
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
                MarkUnsupported(expression, $"Variable initializer '{text}' could not be lowered to a MIR operand.");
                Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{targetName} = {text}", targetName, targetType, new MidLevelIrUseRValue(operand));
            RecordMoveFromOperand(operand, targetType);
        }

        private void RegisterLocal(string name, StarkTypeSymbol type, string storageClass, bool isMutable, bool isConstant)
        {
            if (_localsByName.ContainsKey(name))
            {
                return;
            }

            var local = new MidLevelIrLocal(
                name,
                type,
                storageClass,
                isMutable,
                isConstant,
                IsAddressable: ShouldAddressLocal(type, storageClass),
                Location: _currentStatementLocation ?? _functionLocation);
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

        private string? ResolveIndirectArgumentLocal(StarkTypeSymbol parameterType, MidLevelIrOperand argument)
        {
            if (!RequiresIndirectArgument(parameterType))
            {
                return null;
            }

            switch (argument)
            {
                case MidLevelIrLocalOperand localOperand:
                    EnsureAddressableLocal(localOperand.Name);
                    return localOperand.Name;
                case MidLevelIrParameterOperand parameterOperand when RequiresIndirectArgument(parameterOperand.Type):
                    return parameterOperand.Name;
                default:
                    return null;
            }
        }

        private static bool RequiresIndirectArgument(StarkTypeSymbol type)
        {
            return type.BorrowKind != StarkBorrowKind.None
                || type.InitializationKind != StarkInitializationKind.None;
        }

        private void Emit(
            MidLevelIrStatementKind kind,
            string text,
            string? targetName = null,
            StarkTypeSymbol? targetType = null,
            MidLevelIrRValue? value = null,
            MidLevelIrOperand? address = null)
        {
            CurrentBlock.Statements.Add(new MidLevelIrStatement(
                kind,
                text,
                targetName,
                targetType,
                address,
                value,
                _currentStatementLocation ?? _functionLocation));
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
            var block = new BasicBlockBuilder(
                _nextBlockId,
                $"bb{_nextBlockId}_{label}",
                () => _currentStatementLocation ?? _functionLocation);
            _nextBlockId++;
            _blocks.Add(block);
            return block;
        }

        private void MarkUnsupported(
            ParserRuleContext? syntax = null,
            string? reason = null,
            string? featureTag = null,
            [CallerMemberName] string caller = "")
        {
            SupportsDirectCodeGeneration = false;

            var location = CreateSourceLocation(syntax?.Start) ?? _functionLocation;
            var logKey = string.Join(
                "|",
                caller,
                CurrentBlock.Id.ToString(),
                location.Line.ToString(),
                location.Column.ToString(),
                reason ?? string.Empty);

            if (!_unsupportedLogKeys.Add(logKey))
            {
                return;
            }

            var resolvedFeatureTag = featureTag ?? CreateFeatureTag(caller);
            var message = reason ?? $"Direct MIR lowering stopped in '{caller}'.";

            _logs.GapWarning(
                "lowering",
                "unsupported-lowering",
                message,
                featureTag: resolvedFeatureTag,
                reason: reason,
                operation: caller,
                location: location,
                outcome: CompilerLogOutcome.Unsupported,
                data: CompilerLogData.Create(
                        ("module", CurrentModuleName),
                    ("function", _function.Name),
                    ("bodyLoweringKind", _function.BodyLoweringKind.ToString()),
                    ("blockId", CurrentBlock.Id.ToString()),
                    ("blockLabel", CurrentBlock.Label),
                    ("syntaxText", TruncateForLog(syntax?.GetText()))));
        }

        private MidLevelIrOperand? UnsupportedOperand()
        {
            MarkUnsupported();
            return null;
        }

        private SourceLocation? CreateSourceLocation(IToken? token)
        {
            return token is null
                ? null
                : new SourceLocation(_moduleFilePath, token.Line, token.Column + 1);
        }

        private static string? TruncateForLog(string? text, int maxLength = 120)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            {
                return text;
            }

            return $"{text[..maxLength]}...";
        }

        private static string CreateFeatureTag(string caller)
        {
            if (string.IsNullOrWhiteSpace(caller))
            {
                return "mir-lowering-gap";
            }

            var builder = new StringBuilder();
            for (var index = 0; index < caller.Length; index++)
            {
                var current = caller[index];
                if (char.IsUpper(current) && index > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString();
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
                "**" => MidLevelIrBinaryOperator.Exponent,
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

        private static bool SupportsAddressModel(MidLevelIrOperand? operand)
        {
            return operand is MidLevelIrLocalOperand or MidLevelIrParameterOperand or MidLevelIrGlobalOperand or MidLevelIrGlobalAddressOperand;
        }

        private bool IsBorrowParameterRoot(MidLevelIrOperand? operand)
        {
            return operand is MidLevelIrParameterOperand parameter
                && _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                && parameterBinding.Type.BorrowKind != StarkBorrowKind.None;
        }

        private bool TryInitializePointerPlaceRoot(
            StarkParser.UnaryExpressionContext expression,
            out MidLevelIrOperand address,
            out StarkTypeSymbol rootType,
            out bool isAddressMutable)
        {
            address = default!;
            rootType = StarkTypeSymbols.Error;
            isAddressMutable = false;

            if (expression.conversionType() is not null
                || expression.powerExpression() is not null
                || !string.Equals(expression.unaryOperator()?.GetText(), "*", StringComparison.Ordinal))
            {
                return false;
            }

            var loweredPointer = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
            if (loweredPointer is null
                || loweredPointer.Type.Kind != StarkTypeKind.RawPointer
                || loweredPointer.Type.ElementType is null)
            {
                return false;
            }

            address = loweredPointer;
            rootType = loweredPointer.Type.ElementType;
            isAddressMutable = loweredPointer.Type.IsMutablePointer && CanMutateThroughType(rootType);
            return true;
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

        private MidLevelIrOperand? CreateAddressOfParameter(string name, StarkTypeSymbol type)
        {
            var isMutable = _parametersByName.TryGetValue(name, out var parameter)
                ? CanMutateThroughType(parameter.Type)
                : true;
            return EmitTemporary(
                new MidLevelIrAddressOfParameterRValue(name, type, AddressType(type, isMutable), $"&{name}"),
                "addr");
        }

        private MidLevelIrOperand CreateAddressOfGlobal(string name, StarkTypeSymbol type)
        {
            var isMutable = _typeModel.Globals.TryGetValue(name, out var global)
                ? global.IsMutable && CanMutateThroughType(global.Type)
                : true;
            return new MidLevelIrGlobalAddressOperand(name, type, AddressType(type, isMutable));
        }

        private bool UsesFrozenProjectionSemantics(MidLevelIrOperand operand)
        {
            return operand.Type.AccessKind == StarkAccessKind.Frozen
                || operand is MidLevelIrGlobalOperand global
                    && TryResolveGlobal(global.Name, out var binding)
                    && binding.IsConst;
        }

        private StarkTypeSymbol ProjectRootType(MidLevelIrOperand operand)
        {
            return UsesFrozenProjectionSemantics(operand)
                ? StarkTypeSymbols.FreezeAddressPointeeType(operand.Type)
                : operand.Type;
        }

        private StarkTypeSymbol ProjectProjectionType(MidLevelIrOperand source, StarkTypeSymbol projectedType)
        {
            return UsesFrozenProjectionSemantics(source)
                ? StarkTypeSymbols.FreezeReachableView(projectedType)
                : ProjectFrozenView(source.Type, projectedType);
        }

        private static bool ShouldAddressLocal(StarkTypeSymbol type, string storageClass)
        {
            if (storageClass == "heap")
            {
                return true;
            }

            return storageClass is "arena" or "static"
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

        private static BigInteger ParseIntegerLiteralText(string literalText)
        {
            return BigInteger.Parse(literalText);
        }

        private static BigInteger ToSignedByteValue(byte value)
        {
            return value <= sbyte.MaxValue
                ? new BigInteger(value)
                : new BigInteger(unchecked((sbyte)value));
        }

        private static bool TryConvertTextLiteral(
            MidLevelIrOperand operand,
            StarkTypeSymbol targetType,
            out MidLevelIrOperand converted)
        {
            converted = null!;
            if (operand is not MidLevelIrStringConstantOperand textConstant)
            {
                return false;
            }

            if (targetType.Kind == StarkTypeKind.Unicode && operand.Type.Kind == StarkTypeKind.Ascii)
            {
                converted = new MidLevelIrStringConstantOperand(textConstant.LiteralText, targetType);
                return true;
            }

            if (targetType.Kind == StarkTypeKind.Ascii
                && operand.Type.Kind == StarkTypeKind.Unicode
                && TextLiteralDecoder.CanUseUtf8Storage(
                    textConstant.LiteralText,
                    textConstant.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String))
            {
                converted = new MidLevelIrStringConstantOperand(textConstant.LiteralText, targetType);
                return true;
            }

            return false;
        }

        private static int[] DecodeTextLiteralUnits(string literalText, StarkTypeSymbol textType)
        {
            var kind = literalText.StartsWith('\'')
                ? TextLiteralKind.Character
                : TextLiteralKind.String;

            return textType.Kind switch
            {
                StarkTypeKind.Ascii => TextLiteralDecoder.DecodeUtf8BytesOrFallback(literalText, kind)
                    .Select(static value => (int)value)
                    .ToArray(),
                StarkTypeKind.Unicode => TextLiteralDecoder.DecodeUtf32CodeUnitsOrFallback(literalText, kind),
                _ => throw new InvalidOperationException($"Text literal decoding requires an ascii/unicode target, but found '{textType.DisplayName}'.")
            };
        }

        private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
        {
            return textType.Kind switch
            {
                StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
                StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
                _ => throw new InvalidOperationException($"Text unit type requires an ascii/unicode value, but found '{textType.DisplayName}'.")
            };
        }

        private static MidLevelIrIntegerConstantOperand CreateTextUnitConstant(int value, StarkTypeSymbol unitType)
        {
            return unitType.BitWidth == 8
                ? new MidLevelIrIntegerConstantOperand(ToSignedByteValue((byte)value), unitType)
                : new MidLevelIrIntegerConstantOperand(new BigInteger(value), unitType);
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
            private readonly Func<SourceLocation?> _locationProvider;
            private MidLevelIrTerminator? _terminator;

            public BasicBlockBuilder(int id, string label, Func<SourceLocation?> locationProvider)
            {
                Id = id;
                Label = label;
                _locationProvider = locationProvider;
            }

            public int Id { get; }

            public string Label { get; }

            public List<MidLevelIrStatement> Statements { get; } = [];

            public MidLevelIrTerminator? Terminator
            {
                get => _terminator;
                set => _terminator = value is null || value.Location is not null
                    ? value
                    : value with { Location = _locationProvider() };
            }

            public bool HasTerminator => Terminator is not null;

            public MidLevelIrBasicBlock Build()
            {
                return new MidLevelIrBasicBlock(
                    Id,
                    Label,
                    Statements.ToArray(),
                    Terminator ?? new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Unreachable,
                        Targets: [],
                        Location: _locationProvider()));
            }
        }

        private readonly record struct LoopTargets(int ContinueTarget, int BreakTarget, int ScopeDepth);
        private readonly record struct BreakTargets(int Target, int ScopeDepth);
    }
}
