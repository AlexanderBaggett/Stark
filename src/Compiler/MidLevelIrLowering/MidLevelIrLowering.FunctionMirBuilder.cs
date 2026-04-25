using System.Numerics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder : IDisposable
    {
        private static readonly StarkTypeSymbol NonNegativeI64Type = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - 1);

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
            MidLevelIrOperand? RootValue,
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
            private readonly IReadOnlyDictionary<string, StarkTypeSymbol>? _previousGenericTypeSubstitution;
            private readonly string _aliasName;
            private readonly string? _previousAlias;
            private readonly bool _hadAlias;

            public DestructorContext(
                FunctionMirBuilder builder,
                string? previousModuleName,
                IReadOnlyDictionary<string, StarkTypeSymbol>? previousGenericTypeSubstitution,
                string aliasName,
                string? previousAlias,
                bool hadAlias)
            {
                _builder = builder;
                _previousModuleName = previousModuleName;
                _previousGenericTypeSubstitution = previousGenericTypeSubstitution;
                _aliasName = aliasName;
                _previousAlias = previousAlias;
                _hadAlias = hadAlias;
            }

            public void Dispose()
            {
                _builder._moduleNameOverride = _previousModuleName;
                _builder._activeGenericTypeSubstitution = _previousGenericTypeSubstitution;
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

        private sealed class ConstructorBodyContext : IDisposable
        {
            private readonly FunctionMirBuilder _builder;
            private readonly string? _previousModuleName;
            private readonly IReadOnlyDictionary<string, StarkTypeSymbol>? _previousGenericTypeSubstitution;
            private readonly List<(string AliasName, string? PreviousAlias, bool HadAlias)> _previousAliases;

            public ConstructorBodyContext(
                FunctionMirBuilder builder,
                string? previousModuleName,
                IReadOnlyDictionary<string, StarkTypeSymbol>? previousGenericTypeSubstitution,
                List<(string AliasName, string? PreviousAlias, bool HadAlias)> previousAliases)
            {
                _builder = builder;
                _previousModuleName = previousModuleName;
                _previousGenericTypeSubstitution = previousGenericTypeSubstitution;
                _previousAliases = previousAliases;
            }

            public void Dispose()
            {
                _builder._moduleNameOverride = _previousModuleName;
                _builder._activeGenericTypeSubstitution = _previousGenericTypeSubstitution;

                foreach (var (aliasName, previousAlias, hadAlias) in _previousAliases)
                {
                    if (hadAlias)
                    {
                        _builder._nameAliases[aliasName] = previousAlias!;
                    }
                    else
                    {
                        _builder._nameAliases.Remove(aliasName);
                    }
                }
            }
        }

        private readonly HighLevelIrFunction _function;
        private readonly string _currentModuleName;
        private readonly TypeCheckModel _typeModel;
        private readonly EnumLayoutModel _enumLayoutModel;
        private readonly ModuleGraph _moduleGraph;
        private readonly StarkTypeResolver _typeResolver;
        private readonly IReadOnlyDictionary<string, FunctionLoweringContext> _functionsByName;
        private readonly IReadOnlyDictionary<string, ConstructorLoweringContext> _constructorsByBodyKey;
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
        private IReadOnlyDictionary<string, StarkTypeSymbol>? _activeGenericTypeSubstitution;
        private readonly HashSet<string> _unsupportedLogKeys = new(StringComparer.Ordinal);
        private readonly IDisposable _logScope;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly Dictionary<string, bool> _runtimeDropStates = new(StringComparer.Ordinal);
        private readonly List<string> _parameterDropOrder = [];
        private readonly Dictionary<string, string> _nameAliases = new(StringComparer.Ordinal);
        private readonly Stack<ConstructorReturnTarget> _constructorReturnTargets = [];
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private readonly Stack<BreakTargets> _breakTargets = [];
        private readonly Stack<ScopeFrame> _scopes = [];
        private readonly CompileTimeEvaluator _compileTimeEvaluator;
        private readonly CompileTimeEvaluator.CompileTimeEvaluationState _compileTimeConstantState = new();
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
            ModuleGraph moduleGraph,
            StarkTypeResolver typeResolver,
            IReadOnlyDictionary<string, FunctionLoweringContext> functionsByName,
            IReadOnlyDictionary<string, ConstructorLoweringContext> constructorsByBodyKey,
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
            IReadOnlyDictionary<string, StarkTypeSymbol>? genericTypeSubstitution,
            bool useImportedTemplateLocalDeclarationFacts)
        {
            _function = function;
            _currentModuleName = currentModuleName;
            _typeModel = typeModel;
            _enumLayoutModel = enumLayoutModel;
            _moduleGraph = moduleGraph;
            _typeResolver = typeResolver;
            _functionsByName = functionsByName;
            _constructorsByBodyKey = constructorsByBodyKey;
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
            _importedTemplateLocalDeclarations = useImportedTemplateLocalDeclarationFacts
                ? importedTemplateSummary?.LocalDeclarations.ToDictionary(
                    static local => TemplateLocalDeclarationFacts.BuildLookupKey(local.Kind, local.Line, local.Column),
                    static local => local.Type,
                    StringComparer.Ordinal)
                    ?? new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal)
                : new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
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
            _activeGenericTypeSubstitution = _genericTypeSubstitution;
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
                CompleteOpenFunctionTerminator();
            }
        }

        public void LowerLambda(StarkParser.LambdaExpressionContext expression)
        {
            if (expression.expression() is { } bodyExpression)
            {
                if (_function.Signature.ReturnType.Kind == StarkTypeKind.Void)
                {
                    LowerExpressionStatement(bodyExpression);
                    if (!CurrentBlock.HasTerminator)
                    {
                        EmitStorageDeadBeyondDepth(0);
                        CurrentBlock.Terminator = new MidLevelIrTerminator(
                            MidLevelIrTerminatorKind.Return,
                            Targets: [],
                            Location: CreateSourceLocation(expression.Start) ?? _functionLocation);
                    }
                }
                else
                {
                    var operand = LowerReturnExpressionToOperand(bodyExpression, _function.Signature.ReturnType);
                    if (_function.Signature.ReturnType.BorrowKind == StarkBorrowKind.None)
                    {
                        RecordMoveFromOperand(operand, _function.Signature.ReturnType);
                    }

                    EmitStorageDeadBeyondDepth(0);
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Return,
                        Targets: [],
                        ValueText: bodyExpression.GetText(),
                        Value: operand,
                        Location: CreateSourceLocation(expression.Start) ?? _functionLocation);
                }
            }
            else if (expression.block() is { } block)
            {
                LowerBlock(block);
            }
            else
            {
                MarkUnsupported(expression, "Unsupported lambda body shape.");
            }

            if (!CurrentBlock.HasTerminator)
            {
                CompleteOpenFunctionTerminator();
            }
        }

        private void CompleteOpenFunctionTerminator()
        {
            CurrentBlock.Terminator = _function.Signature.ReturnType.Kind == StarkTypeKind.Void
                ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [], Location: _functionLocation)
                : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: [], Location: _functionLocation);
        }

        private void LowerBlock(StarkParser.BlockContext block)
        {
            _scopes.Push(new ScopeFrame());
            _compileTimeConstantState.PushScope();

            try
            {
                foreach (var statement in block.statement())
                {
                    LowerStatement(statement);
                    if (_constructorReturnTargets.Count > 0 && CurrentBlock.HasTerminator)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _compileTimeConstantState.PopScope();
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
            var declaredType = TryResolveLocalDeclarationType(TemplateLocalDeclarationFacts.ConstantKind, declaration, out var publishedType)
                ? publishedType
                : declaration.type_() is { } typeContext
                    ? ResolveTypeWithGenericSubstitution(typeContext, CurrentModuleName)
                    : StarkTypeSymbols.Error;
            foreach (var declarator in declaration.constantDeclarators().constantDeclarator())
            {
                var name = declarator.Identifier().GetText();
                RegisterLocal(name, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                TrackCompileTimeConstant(name, declaredType, declarator.variableInitializer());
                LowerVariableInitializer(name, declaredType, declarator.variableInitializer());
                InitializeRuntimeDropState(name, declaredType, isActive: true);
            }
        }

        private void TrackCompileTimeConstant(
            string name,
            StarkTypeSymbol declaredType,
            StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is not { } expression
                || !_compileTimeEvaluator.TryEvaluateExpression(
                    expression,
                    CurrentModuleName,
                    _compileTimeConstantState,
                    activeCalls: null,
                    out var constant)
                || !CompileTimeExpressionEvaluator.TryCoerce(constant, declaredType, out var coerced))
            {
                return;
            }

            _compileTimeConstantState.Declare(name, coerced, isMutable: false);
        }

        private void LowerVariableDeclaration(StarkParser.LocalVariableDeclarationContext declaration)
        {
            var declaredType = TryResolveLocalDeclarationType(TemplateLocalDeclarationFacts.VariableKind, declaration, out var publishedType)
                ? publishedType
                : ResolveTypeWithGenericSubstitution(declaration.type_(), CurrentModuleName);
            var storageClass = declaration.storageClass().GetText();

            foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
            {
                var name = declarator.Identifier().GetText();
                if (TryGetFixedTextStorageCapacity(declarator, out var fixedTextCapacity))
                {
                    LowerFixedTextStorageVariableDeclaration(
                        name,
                        declaredType,
                        storageClass,
                        declaration.MUT() is not null,
                        fixedTextCapacity,
                        declarator.variableInitializer());
                    continue;
                }

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

        private bool TryGetFixedTextStorageCapacity(StarkParser.VariableDeclaratorContext declarator, out int capacity)
        {
            capacity = 0;
            if (declarator.variableStorageCapacity() is not { } capacityContext
                || !_compileTimeEvaluator.TryEvaluateInteger(
                    capacityContext.expression(),
                    CurrentModuleName,
                    _compileTimeConstantState,
                    activeCalls: null,
                    out var capacityValue)
                || capacityValue <= 0
                || capacityValue > int.MaxValue)
            {
                return false;
            }

            capacity = (int)capacityValue;
            return true;
        }

        private void LowerFixedTextStorageVariableDeclaration(
            string name,
            StarkTypeSymbol declaredType,
            string storageClass,
            bool isMutable,
            int capacity,
            StarkParser.VariableInitializerContext? initializer)
        {
            if (!IsTextBufferType(declaredType))
            {
                RegisterLocal(name, declaredType, storageClass, isMutable, isConstant: false);
                TrackDeclaredLocal(name, declaredType);
                Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
                InitializeRuntimeDropState(name, declaredType, isActive: false);
                if (initializer is not null)
                {
                    LowerVariableInitializer(name, declaredType, initializer);
                    SetRuntimeDropState(name, isActive: true);
                }

                return;
            }

            var unitType = GetFixedTextStorageUnitType(declaredType);
            var storageType = StarkTypeSymbols.FixedArray(unitType, capacity);
            var storageName = AllocateTemporaryName($"{name}_text_storage");

            RegisterLocal(storageName, storageType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(storageName, storageType);
            Emit(MidLevelIrStatementKind.StorageLive, storageName, storageName, storageType);
            InitializeRuntimeDropState(storageName, storageType, isActive: false);

            RegisterLocal(name, declaredType, storageClass, isMutable, isConstant: false);
            TrackDeclaredLocal(name, declaredType);
            Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
            InitializeRuntimeDropState(name, declaredType, isActive: false);

            var emptyText = BuildFixedTextStorageValue(storageName, storageType, declaredType, capacity);
            if (emptyText is null)
            {
                MarkUnsupported(initializer, "Fixed text storage value could not be initialized.");
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{name}[{capacity}]", name, declaredType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(name, isActive: true);

            if (initializer is null)
            {
                return;
            }

            if (initializer.expression() is not { } expression)
            {
                MarkUnsupported(initializer, "Fixed text storage initializer requires a text-building expression.");
                return;
            }

            if (TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral)
            {
                if (!LowerFixedTextStorageInterpolatedInitializer(name, declaredType, interpolatedLiteral))
                {
                    MarkUnsupported(initializer, "Fixed text storage interpolation could not be lowered.");
                }

                return;
            }

            if (TryGetStandaloneAdditiveExpression(expression) is not { } additive)
            {
                MarkUnsupported(initializer, "Fixed text storage initializer requires a text-building expression.");
                return;
            }

            if (!LowerFixedTextStorageConcatInitializer(name, declaredType, additive))
            {
                MarkUnsupported(initializer, "Fixed text storage concatenation could not be lowered.");
            }
        }

        private MidLevelIrOperand? BuildFixedTextStorageValue(
            string storageName,
            StarkTypeSymbol storageType,
            StarkTypeSymbol textType,
            int capacity)
        {
            var unitType = GetFixedTextStorageUnitType(textType);
            var storageAddress = CreateAddressOfLocal(storageName, storageType);
            if (storageAddress is null)
            {
                return null;
            }

            var dataPointer = EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    storageAddress,
                    storageType,
                    Index: null,
                    ConstantIndex: 0,
                    AddressType(unitType, isMutable: true),
                    $"&{storageName}[0]"),
                "textdata");
            if (dataPointer is null)
            {
                return null;
            }

            return BuildTextBufferValue(textType, dataPointer, length: BigInteger.Zero, capacity: capacity);
        }

        private MidLevelIrOperand? BuildTextBufferValue(
            StarkTypeSymbol textType,
            MidLevelIrOperand dataPointer,
            BigInteger length,
            BigInteger capacity)
        {
            if (!TryGetTextBufferNamedType(textType, out var namedType))
            {
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(textType);
            var values = new (string FieldName, MidLevelIrOperand Value)[]
            {
                ("Data", dataPointer),
                ("Length", new MidLevelIrIntegerConstantOperand(length, StarkTypeSymbols.Integer(64))),
                ("Capacity", new MidLevelIrIntegerConstantOperand(capacity, StarkTypeSymbols.Integer(64)))
            };

            foreach (var (fieldName, value) in values)
            {
                if (!namedType.TryGetField(fieldName, out var field, out var fieldIndex))
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        value,
                        textType,
                        $"{current.Text}.{field.Name} = {value.Text}"),
                    "textfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private bool LowerFixedTextStorageConcatInitializer(
            string destinationName,
            StarkTypeSymbol destinationType,
            StarkParser.AdditiveExpressionContext additive)
        {
            var operands = additive.multiplicativeExpression();
            var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additive);
            if (operands.Length < 2 || operators.Any(static item => item != "+"))
            {
                return false;
            }

            var viewType = GetFixedTextStorageViewType(destinationType);
            var current = LowerFixedTextConcatOperandToView(operands[0], viewType);
            if (current is null)
            {
                MarkUnsupported(operands[0], "Fixed text storage concatenation could not lower the left operand to a text view.");
                return false;
            }

            for (var index = 1; index < operands.Length; index++)
            {
                var next = LowerFixedTextConcatOperandToView(operands[index], viewType);
                if (next is null)
                {
                    MarkUnsupported(operands[index], "Fixed text storage concatenation could not lower the right operand to a text view.");
                    return false;
                }

                var destinationAddress = CreateMutableAddressOfLocalForInitialization(destinationName, destinationType);
                if (destinationAddress is null)
                {
                    MarkUnsupported(additive, "Fixed text storage concatenation could not address the destination buffer.");
                    return false;
                }

                if (!TryBuildFixedTextConcatCall(destinationAddress, current, next, $"{current.Text} + {next.Text}", out var call))
                {
                    MarkUnsupported(additive, "Fixed text storage concatenation could not resolve the System.Text concat helper.");
                    return false;
                }

                var success = EmitTemporary(call, "textconcat");
                if (success is null)
                {
                    MarkUnsupported(additive, "Fixed text storage concatenation could not materialize the concat result.");
                    return false;
                }

                EmitTrapOnFalse(success, "textconcat_overflow");
                current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
                if (current is null)
                {
                    MarkUnsupported(additive, "Fixed text storage concatenation could not view the destination after concat.");
                    return false;
                }
            }

            return true;
        }

        private bool LowerFixedTextStorageInterpolatedInitializer(
            string destinationName,
            StarkTypeSymbol destinationType,
            StarkParser.LiteralContext literal)
        {
            if (literal.StringLiteral() is not { } interpolatedString
                || !InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
            {
                return false;
            }

            var viewType = GetFixedTextStorageViewType(destinationType);
            var current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
            if (current is null)
            {
                MarkUnsupported(literal, "Fixed text storage interpolation could not view the destination buffer.");
                return false;
            }

            foreach (var segment in segments)
            {
                var next = LowerInterpolatedTextSegmentToView(segment, destinationType, viewType);
                if (next is null)
                {
                    MarkUnsupported(literal, "Fixed text storage interpolation could not lower one of its parts.");
                    return false;
                }

                if (!AppendFixedTextStorageSegment(destinationName, destinationType, current, next, literal))
                {
                    return false;
                }

                current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
                if (current is null)
                {
                    MarkUnsupported(literal, "Fixed text storage interpolation could not view the destination after appending text.");
                    return false;
                }
            }

            return true;
        }

        private MidLevelIrOperand? LowerInterpolatedTextSegmentToView(
            InterpolatedTextSegment segment,
            StarkTypeSymbol destinationType,
            StarkTypeSymbol viewType)
        {
            if (segment is InterpolatedTextRawSegment raw)
            {
                return raw.Value.Length == 0
                    ? BuildTextBufferView(new MidLevelIrStringConstantOperand("\"\"", viewType), viewType)
                    : new MidLevelIrStringConstantOperand(TextLiteralDecoder.EncodeStringLiteral(raw.Value), viewType);
            }

            var hole = (InterpolatedTextHoleSegment)segment;
            var value = LowerExpressionToOperand(hole.Expression, expectedType: null);
            if (value is null)
            {
                MarkUnsupported(hole.Expression, "Interpolated text hole did not lower to a value.");
                return null;
            }

            if (CanUseFixedTextConcatSource(destinationType, value.Type))
            {
                return BuildTextBufferView(value, viewType);
            }

            if (!TextFormattingFacts.TryGetFixedBufferFormatInfo(destinationType, value.Type, out var formatInfo))
            {
                MarkUnsupported(hole.Expression, $"Interpolated text does not have a formatter for '{value.Type.DisplayName}'.");
                return null;
            }

            return LowerFormattedInterpolatedTextHole(value, destinationType, viewType, formatInfo, hole.Expression);
        }

        private MidLevelIrOperand? LowerFormattedInterpolatedTextHole(
            MidLevelIrOperand value,
            StarkTypeSymbol destinationType,
            StarkTypeSymbol viewType,
            FixedTextFormatInfo formatInfo,
            ParserRuleContext context)
        {
            var storageName = AllocateTemporaryName("interpolation_format_storage");
            var textName = AllocateTemporaryName("interpolation_format_text");
            var unitType = GetFixedTextStorageUnitType(destinationType);
            var storageType = StarkTypeSymbols.FixedArray(unitType, formatInfo.Capacity);

            RegisterLocal(storageName, storageType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(storageName, storageType);
            Emit(MidLevelIrStatementKind.StorageLive, storageName, storageName, storageType);
            InitializeRuntimeDropState(storageName, storageType, isActive: false);

            RegisterLocal(textName, destinationType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(textName, destinationType);
            Emit(MidLevelIrStatementKind.StorageLive, textName, textName, destinationType);
            InitializeRuntimeDropState(textName, destinationType, isActive: false);

            var emptyText = BuildFixedTextStorageValue(storageName, storageType, destinationType, formatInfo.Capacity);
            if (emptyText is null)
            {
                MarkUnsupported(context, "Interpolated text formatter storage could not be initialized.");
                return null;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{textName}[{formatInfo.Capacity}]", textName, destinationType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(textName, isActive: true);

            var destinationAddress = CreateMutableAddressOfLocalForInitialization(textName, destinationType);
            if (destinationAddress is null)
            {
                MarkUnsupported(context, "Interpolated text formatter storage could not be addressed.");
                return null;
            }

            if (!TryBuildFixedTextFormatCall(destinationAddress, value, formatInfo.FunctionName, context.GetText(), out var call))
            {
                MarkUnsupported(context, $"Interpolated text could not call '{formatInfo.FunctionName}'.");
                return null;
            }

            var success = EmitTemporary(call, "textformat");
            if (success is null)
            {
                MarkUnsupported(context, "Interpolated text formatter result could not be materialized.");
                return null;
            }

            EmitTrapOnFalse(success, "textformat_overflow");
            return BuildTextBufferView(new MidLevelIrLocalOperand(textName, destinationType), viewType);
        }

        private bool AppendFixedTextStorageSegment(
            string destinationName,
            StarkTypeSymbol destinationType,
            MidLevelIrOperand current,
            MidLevelIrOperand next,
            ParserRuleContext context)
        {
            var destinationAddress = CreateMutableAddressOfLocalForInitialization(destinationName, destinationType);
            if (destinationAddress is null)
            {
                MarkUnsupported(context, "Fixed text storage interpolation could not address the destination buffer.");
                return false;
            }

            if (!TryBuildFixedTextConcatCall(destinationAddress, current, next, $"{current.Text} + {next.Text}", out var call))
            {
                MarkUnsupported(context, "Fixed text storage interpolation could not resolve the System.Text concat helper.");
                return false;
            }

            var success = EmitTemporary(call, "textconcat");
            if (success is null)
            {
                MarkUnsupported(context, "Fixed text storage interpolation could not materialize the concat result.");
                return false;
            }

            EmitTrapOnFalse(success, "textconcat_overflow");
            return true;
        }

        private MidLevelIrOperand? LowerFixedTextConcatOperandToView(
            StarkParser.MultiplicativeExpressionContext expression,
            StarkTypeSymbol viewType)
        {
            var operand = LowerMultiplicativeExpression(expression, expectedType: null);
            if (operand is null)
            {
                MarkUnsupported(expression, "Fixed text storage operand did not lower to a value.");
                return null;
            }

            return BuildTextBufferView(operand, viewType);
        }

        private MidLevelIrOperand? BuildTextBufferView(MidLevelIrOperand operand, StarkTypeSymbol viewType)
        {
            if (operand.Type.Kind == viewType.Kind)
            {
                return CoerceOperand(operand, viewType);
            }

            if (!IsTextBufferType(operand.Type))
            {
                return CoerceOperand(operand, viewType);
            }

            var functionName = GetSystemTextFunctionName(viewType.Kind == StarkTypeKind.Unicode
                ? "UnicodeView"
                : "AsciiView");
            if (!TryGetFunctionOverloads(functionName, out var overloads))
            {
                MarkUnsupported(reason: $"Could not find '{functionName}' while lowering fixed text storage concatenation.");
                return null;
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [operand.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded
                || !TryBuildCall(functionName, resolution.Match!, receiver: null, receiverPlace: null, operand.Text, out var call, [operand]))
            {
                MarkUnsupported(reason: $"Could not call '{functionName}' for operand type '{operand.Type.DisplayName}'.");
                return null;
            }

            return EmitTemporary(call, "textview");
        }

        private bool TryBuildFixedTextConcatCall(
            MidLevelIrOperand destinationAddress,
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;
            var functionName = GetSystemTextFunctionName(left.Type.Kind == StarkTypeKind.Unicode
                ? "TryConcatUnicode"
                : "TryConcatAscii");
            if (!TryGetFunctionOverloads(functionName, out var overloads))
            {
                MarkUnsupported(reason: $"Could not find '{functionName}' while lowering fixed text storage concatenation.");
                return false;
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [destinationAddress.Type, left.Type, right.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                MarkUnsupported(reason: $"Could not match '{functionName}' for '{destinationAddress.Type.DisplayName}', '{left.Type.DisplayName}', and '{right.Type.DisplayName}'.");
                return false;
            }

            if (!TryBuildCall(functionName, resolution.Match!, receiver: null, receiverPlace: null, text, out call, [destinationAddress, left, right]))
            {
                MarkUnsupported(reason: $"Could not build call to '{functionName}' for fixed text storage concatenation.");
                return false;
            }

            return true;
        }

        private bool TryBuildFixedTextFormatCall(
            MidLevelIrOperand destinationAddress,
            MidLevelIrOperand value,
            string functionName,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;
            var sourceName = GetSystemTextFunctionName(functionName);
            if (!TryGetFunctionOverloads(sourceName, out var overloads))
            {
                MarkUnsupported(reason: $"Could not find '{sourceName}' while lowering fixed text storage interpolation.");
                return false;
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [destinationAddress.Type, value.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                MarkUnsupported(reason: $"Could not match '{sourceName}' for '{destinationAddress.Type.DisplayName}' and '{value.Type.DisplayName}'.");
                return false;
            }

            if (!TryBuildCall(sourceName, resolution.Match!, receiver: null, receiverPlace: null, text, out call, [destinationAddress, value]))
            {
                MarkUnsupported(reason: $"Could not build call to '{sourceName}' for fixed text storage interpolation.");
                return false;
            }

            return true;
        }

        private void EmitTrapOnFalse(MidLevelIrOperand condition, string label)
        {
            var continueBlock = CreateBlock($"{label}_ok");
            var failBlock = CreateBlock($"{label}_fail");
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [continueBlock.Id, failBlock.Id],
                ConditionText: condition.Text,
                Condition: condition);

            CurrentBlock = failBlock;
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: []);
            CurrentBlock = continueBlock;
        }

        private MidLevelIrOperand? CreateMutableAddressOfLocalForInitialization(string name, StarkTypeSymbol type)
        {
            EnsureAddressableLocal(name);
            return EmitTemporary(
                new MidLevelIrAddressOfLocalRValue(name, type, AddressType(type, isMutable: true), $"&{name}"),
                "addr");
        }

        private static bool IsTextBufferType(StarkTypeSymbol type)
        {
            return type.Kind == StarkTypeKind.Named
                && type.NamedType is StarkTypeSymbols.OwnedAsciiName or StarkTypeSymbols.OwnedUnicodeName;
        }

        private static bool CanUseFixedTextConcatSource(StarkTypeSymbol destination, StarkTypeSymbol source)
        {
            return destination.NamedType switch
            {
                StarkTypeSymbols.OwnedAsciiName => source.Kind == StarkTypeKind.Ascii
                    || source.Kind == StarkTypeKind.Named && source.NamedType == StarkTypeSymbols.OwnedAsciiName,
                StarkTypeSymbols.OwnedUnicodeName => source.Kind == StarkTypeKind.Unicode
                    || source.Kind == StarkTypeKind.Named && source.NamedType == StarkTypeSymbols.OwnedUnicodeName,
                _ => false
            };
        }

        private bool TryGetTextBufferNamedType(StarkTypeSymbol type, out NamedTypeSymbol namedType)
        {
            if (type.Kind == StarkTypeKind.Named
                && type.NamedType is { } typeName
                && (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!)
                    || StarkTypeSymbols.TryGetBuiltinNamedType(typeName, out namedType!)))
            {
                return true;
            }

            namedType = null!;
            return false;
        }

        private static StarkTypeSymbol GetFixedTextStorageUnitType(StarkTypeSymbol textType)
        {
            return textType.NamedType == StarkTypeSymbols.OwnedUnicodeName
                ? StarkTypeSymbols.Integer(32)
                : StarkTypeSymbols.Integer(8);
        }

        private static StarkTypeSymbol GetFixedTextStorageViewType(StarkTypeSymbol textType)
        {
            return textType.NamedType == StarkTypeSymbols.OwnedUnicodeName
                ? StarkTypeSymbols.Unicode
                : StarkTypeSymbols.Ascii;
        }

        private string GetSystemTextFunctionName(string name)
        {
            return string.Equals(CurrentModuleName, "System.Text", StringComparison.Ordinal)
                ? name
                : $"System.Text.{name}";
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
            if (_constructorReturnTargets.Count > 0)
            {
                var constructorReturn = _constructorReturnTargets.Peek();
                if (returnStatement.expression() is not null)
                {
                    MarkUnsupported(returnStatement, "Constructor bodies cannot return a value.");
                    return;
                }

                EmitStorageDeadBeyondDepth(constructorReturn.ScopeDepth);
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Goto,
                    [constructorReturn.ExitBlockId],
                    Location: CreateSourceLocation(returnStatement.Start) ?? _functionLocation);
                return;
            }

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

            var operand = LowerReturnExpressionToOperand(returnStatement.expression(), _function.Signature.ReturnType);
            if (_function.Signature.ReturnType.BorrowKind == StarkBorrowKind.None)
            {
                RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            }

            EmitStorageDeadBeyondDepth(0);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: returnStatement.expression().GetText(),
                Value: operand);
        }

        private MidLevelIrOperand? LowerReturnExpressionToOperand(StarkParser.ExpressionContext expression, StarkTypeSymbol returnType)
        {
            if (returnType.BorrowKind != StarkBorrowKind.None
                && StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
                && TryExtractSimpleUnaryExpression(expression, out var unaryExpression)
                && TryResolveAssignmentTarget(unaryExpression, out var target)
                && target.Type.BorrowKind == StarkBorrowKind.None)
            {
                return BuildAddress(target);
            }

            if (returnType.BorrowKind != StarkBorrowKind.None
                && !StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType))
            {
                return LowerExpressionToOperand(expression, StarkTypeSymbols.BorrowReturnValueType(returnType));
            }

            return LowerExpressionToOperand(expression, returnType);
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
                if (assignment is not null)
                {
                    EmitAssignment(assignment);
                }

                return true;
            }

            if (TryLowerConditionalCallStatement(expression))
            {
                return true;
            }

            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                EmitEvaluateExpressionStatement(expression, value);
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
                EmitEvaluateExpressionStatement(expression, value);
                return true;
            }

            return TryLowerConditionalCallStatement(expression);
        }

        private void EmitEvaluateExpressionStatement(StarkParser.ExpressionContext expression, MidLevelIrRValue value)
        {
            Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);

            if (value is MidLevelIrCallRValue call && IsKnownNoReturnCall(call.FunctionName))
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Unreachable,
                    Targets: [],
                    Location: CreateSourceLocation(expression.Start) ?? _currentStatementLocation ?? _functionLocation);
            }
        }

        private static bool IsKnownNoReturnCall(string functionName)
        {
            return string.Equals(functionName, "System.Process.Exit", StringComparison.Ordinal)
                || string.Equals(functionName, "System.Runtime.Platform.ExitProcess", StringComparison.Ordinal)
                || string.Equals(functionName, "System.Runtime.Platform.Linux.ExitProcess", StringComparison.Ordinal)
                || string.Equals(functionName, "System.Runtime.Platform.Windows.ExitProcess", StringComparison.Ordinal);
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
            var branchWeights = CreateConditionalBranchWeights(ifStatement.weightSpecifier());

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                elseBlock is null ? [thenBlock.Id, joinBlock.Id] : [thenBlock.Id, elseBlock.Id],
                ConditionText: ifStatement.expression().GetText(),
                Condition: condition,
                BranchWeights: branchWeights);

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

        private static IReadOnlyList<int>? CreateConditionalBranchWeights(StarkParser.WeightSpecifierContext? weightSpecifier)
        {
            if (TryParseBranchWeightPercent(weightSpecifier) is not { } takenPercent)
            {
                return null;
            }

            return [Math.Max(1, takenPercent), Math.Max(1, 100 - takenPercent)];
        }

        private static IReadOnlyList<int>? CreateSwitchBranchWeights(
            StarkParser.WeightSpecifierContext? weightSpecifier,
            int caseCount)
        {
            if (caseCount <= 0 || TryParseBranchWeightPercent(weightSpecifier) is not { } explicitCasePercent)
            {
                return null;
            }

            var weights = new int[caseCount + 1];
            weights[0] = Math.Max(1, 100 - explicitCasePercent);

            var baseCaseWeight = Math.Max(1, explicitCasePercent / caseCount);
            var remainder = explicitCasePercent % caseCount;
            for (var index = 0; index < caseCount; index++)
            {
                weights[index + 1] = baseCaseWeight + (index < remainder ? 1 : 0);
            }

            return weights;
        }

        private static int? TryParseBranchWeightPercent(StarkParser.WeightSpecifierContext? weightSpecifier)
        {
            if (weightSpecifier is null)
            {
                return null;
            }

            var text = weightSpecifier.GetText();
            if (text.Length < 2 || text[0] != 'w')
            {
                return null;
            }

            var digits = text[1..];
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return 100;
            }

            return Math.Clamp(value, 0, 100);
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
                var declaredType = TryResolveLocalDeclarationType(TemplateLocalDeclarationFacts.ForVariableKind, localForVariableDeclaration, out var publishedType)
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
            if (_compileTimeEvaluator.TryEvaluateExpression(expression, CurrentModuleName, _compileTimeConstantState, activeCalls: null, out var constant))
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
            if (operators.Count == 0)
            {
                return LowerMultiplicativeExpression(operands[0], expectedType);
            }

            return LowerBinaryChain(
                operands,
                operators,
                item => LowerMultiplicativeExpression(item, expectedType: null),
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
                    : ApplyGenericSubstitution(_typeResolver.ResolveConversionType(conversionType, ActiveGenericParameterNames(), CurrentModuleName));
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
            if (expression.unaryExpression() is not { } rightExpression)
            {
                return LowerPostfixExpression(expression.postfixExpression(), expectedType);
            }

            var left = LowerPostfixExpression(expression.postfixExpression(), expectedType: null);
            if (left is null)
            {
                return null;
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
            if (expression.postfixPart().Length == 0)
            {
                return LowerPrimaryExpression(expression.primaryExpression(), expectedType);
            }

            if (TryLowerCallExpression(expression, out var call))
            {
                if (call.Type.Kind == StarkTypeKind.Void)
                {
                    MarkUnsupported();
                    return null;
                }

                var callResult = EmitTemporary(call, "call");
                if (callResult is null)
                {
                    return null;
                }

                if (call.SourceReturnType is { } sourceReturnType
                    && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
                {
                    if (expectedType is not null
                        && expectedType.BorrowKind != StarkBorrowKind.None
                        && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
                    {
                        return callResult;
                    }

                    var valueType = StarkTypeSymbols.BorrowReturnValueType(sourceReturnType);
                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            callResult,
                            valueType,
                            $"{callResult.Text}:load"),
                        "load");
                    return loaded is null
                        ? null
                        : expectedType is null
                            ? loaded
                            : CoerceOperand(loaded, expectedType);
                }

                return expectedType is null ? callResult : CoerceOperand(callResult, expectedType);
            }

            if (TryLowerIndirectCallExpression(expression, out var indirectCall))
            {
                if (indirectCall.Type.Kind == StarkTypeKind.Void)
                {
                    MarkUnsupported();
                    return null;
                }

                var callResult = EmitTemporary(indirectCall, "call");
                if (callResult is null)
                {
                    return null;
                }

                if (indirectCall.SourceReturnType is { } sourceReturnType
                    && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
                {
                    if (expectedType is not null
                        && expectedType.BorrowKind != StarkBorrowKind.None
                        && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
                    {
                        return callResult;
                    }

                    var valueType = StarkTypeSymbols.BorrowReturnValueType(sourceReturnType);
                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            callResult,
                            valueType,
                            $"{callResult.Text}:load"),
                        "load");
                    return loaded is null
                        ? null
                        : expectedType is null
                            ? loaded
                            : CoerceOperand(loaded, expectedType);
                }

                return expectedType is null ? callResult : CoerceOperand(callResult, expectedType);
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

            PlaceTarget? currentPlace = currentValue is null ? null : CreateRootPlaceTarget(currentValue);
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
                    currentPlace = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    currentValue = LoadPointerBackedBorrowReturnIfNeeded(directCall, currentValue);
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
                        currentPlace = null;
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
                    if (!(TryBuildPublishedMemberCall(currentValue, currentPlace, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall)
                          || TryBuildMemberCall(currentValue, currentPlace, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out memberCall)))
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
                    currentPlace = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    currentValue = LoadPointerBackedBorrowReturnIfNeeded(memberCall, currentValue);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentPlace = currentPlace is not null && TryAppendFieldPlaceTarget(currentPlace, memberName, out var fieldPlace)
                        ? fieldPlace
                        : null;
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

            if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
            {
                return LowerTypeLayoutExpression(expression, expectedType);
            }

            if (expression.Identifier() is { } identifier)
            {
                return ResolveNamedOperand(identifier.GetText(), expectedType);
            }

            if (expression.lambdaExpression() is { } lambdaExpression)
            {
                return LowerLambdaExpression(lambdaExpression, expectedType);
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
                    : ResolveNamedOperand(genericEnumCaseName, expectedType);
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                return ResolveNamedOperand(qualifiedName.GetText(), expectedType);
            }

            if (expression.objectCreationExpression() is { } objectCreationExpression)
            {
                return LowerObjectCreationExpression(objectCreationExpression, expectedType);
            }

            return LowerExpressionToOperand(expression.expression(), expectedType);
        }

        private MidLevelIrOperand? LowerLambdaExpression(
            StarkParser.LambdaExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            if (expectedType?.Kind != StarkTypeKind.FunctionPointer)
            {
                MarkUnsupported(expression, "Lambda expressions require an explicit function-pointer target type during MIR lowering.");
                return null;
            }

            var line = expression.Start.Line;
            var column = expression.Start.Column + 1;
            var lambda = _typeModel.Lambdas.LastOrDefault(lambda =>
                lambda.Location.Line == line
                && lambda.Location.Column == column);
            if (lambda is null)
            {
                MarkUnsupported(expression, "No type-checked non-capturing lambda record was found for MIR lowering.");
                return null;
            }

            return new MidLevelIrFunctionAddressOperand(lambda.FunctionName, expectedType);
        }

        private MidLevelIrOperand? LowerTypeLayoutExpression(
            StarkParser.PrimaryExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var targetType = ResolveTypeWithGenericSubstitution(expression.type_(), CurrentModuleName);
            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                targetType,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts);
            if (layout is null)
            {
                MarkUnsupported(expression, $"Cannot compute the concrete layout of '{targetType.DisplayName}'.");
                return null;
            }

            var value = expression.ALIGNOF() is not null
                ? layout.AlignmentBytes
                : layout.SizeBytes;
            var resultType = expression.ALIGNOF() is not null
                ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
                : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue));
            var operand = new MidLevelIrIntegerConstantOperand(
                new BigInteger(value),
                resultType);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private MidLevelIrOperand? LowerObjectCreationExpression(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            TryGetPublishedObjectCreationSummary(expression, out var publishedObjectCreation);
            var createdType = publishedObjectCreation is not null
                ? ApplyGenericSubstitution(publishedObjectCreation.CreatedType)
                : expression.type_() is { } explicitType
                    ? ResolveTypeWithGenericSubstitution(explicitType, CurrentModuleName)
                    : expectedType;
            if (createdType is null || createdType.Kind == StarkTypeKind.Error)
            {
                MarkUnsupported(expression, "Target-typed object creation requires a lowering target type.");
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            if (TryGetMatchedObjectCreationConstructor(expression, out var constructor) && constructor is not null)
            {
                var initializedFromConstructor = LowerConstructorObjectCreation(expression, createdType, expression.argumentList(), constructor);
                if (initializedFromConstructor is null)
                {
                    return null;
                }

                current = initializedFromConstructor;
            }
            else if (expression.argumentList() is { } argumentList && argumentList.argument().Length != 0)
            {
                MarkUnsupported(expression, "Object creation arguments require a resolved constructor.");
                return null;
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
                RecordMoveFromOperand(value, fieldType);
            }

            return current;
        }

        private MidLevelIrOperand? LowerConstructorObjectCreation(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol createdType,
            StarkParser.ArgumentListContext? argumentList,
            TypedConstructorShape constructor)
        {
            var argumentCount = argumentList?.argument().Length ?? 0;
            if (constructor.Parameters.Count != argumentCount)
            {
                MarkUnsupported(expression, $"Resolved constructor for '{createdType.DisplayName}' expects {constructor.Parameters.Count} argument(s), but object creation supplied {argumentCount}.");
                return null;
            }

            return constructor.IsPrimaryShape
                ? LowerPrimaryConstructorObjectCreation(createdType, argumentList, constructor)
                : LowerExplicitConstructorObjectCreation(expression, createdType, argumentList, constructor);
        }

        private MidLevelIrOperand? LowerPrimaryConstructorObjectCreation(
            StarkTypeSymbol createdType,
            StarkParser.ArgumentListContext? argumentList,
            TypedConstructorShape constructor)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || constructor is null
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != (argumentList?.argument().Length ?? 0))
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

                var loweredArgument = LowerExpressionToOperand(argumentList!.argument(index).expression(), parameter.Type);
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
                        $"{current.Text}.{field.Name} = {argumentList!.argument(index).GetText()}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
                RecordMoveFromOperand(fieldValue, field.Type);
            }

            return current;
        }

        private MidLevelIrOperand? LowerExplicitConstructorObjectCreation(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol createdType,
            StarkParser.ArgumentListContext? argumentList,
            TypedConstructorShape constructor)
        {
            if (constructor.BodyKey is null
                || !_constructorsByBodyKey.TryGetValue(constructor.BodyKey, out var constructorContext))
            {
                MarkUnsupported(
                    expression,
                    $"Constructor body for '{createdType.DisplayName}' is not available to MIR lowering.");
                return null;
            }

            var loweredArguments = new MidLevelIrOperand[constructor.Parameters.Count];
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                var loweredArgument = LowerExpressionToOperand(argumentList!.argument(index).expression(), parameter.Type);
                if (loweredArgument is null)
                {
                    return null;
                }

                loweredArguments[index] = CoerceOperand(loweredArgument, parameter.Type) ?? loweredArgument;
            }

            _scopes.Push(new ScopeFrame());
            try
            {
                var selfName = AllocateTemporaryName("ctor_self");
                RegisterLocal(selfName, createdType, storageClass: "temp", isMutable: true, isConstant: false);
                TrackDeclaredLocal(selfName, createdType);
                Emit(MidLevelIrStatementKind.StorageLive, selfName, selfName, createdType);

                var selfLocal = new MidLevelIrLocalOperand(selfName, createdType);
                EmitOperandAssignment(selfLocal, new MidLevelIrZeroInitializerOperand(createdType), "zeroinitializer");

                var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["self"] = selfName
                };

                for (var index = 0; index < constructor.Parameters.Count; index++)
                {
                    var parameter = constructor.Parameters[index];
                    var parameterName = AllocateTemporaryName("ctor_param");
                    RegisterLocal(parameterName, parameter.Type, storageClass: "temp", isMutable: false, isConstant: false);
                    TrackDeclaredLocal(parameterName, parameter.Type);
                    Emit(MidLevelIrStatementKind.StorageLive, parameterName, parameterName, parameter.Type);

                    var parameterLocal = new MidLevelIrLocalOperand(parameterName, parameter.Type);
                    InitializeRuntimeDropState(parameterName, parameter.Type, isActive: false);
                    EmitOperandAssignment(parameterLocal, loweredArguments[index], argumentList!.argument(index).GetText());
                    RecordMoveFromOperand(loweredArguments[index], parameter.Type);
                    SetRuntimeDropState(parameterName, isActive: true);
                    aliases[parameter.Name] = parameterName;
                }

                using var constructorBodyContext = EnterConstructorBodyContext(constructorContext, createdType, aliases);
                var exitBlock = CreateBlock("ctor_exit");
                _constructorReturnTargets.Push(new ConstructorReturnTarget(exitBlock.Id, _scopes.Count));
                try
                {
                    LowerBlock(constructorContext.Body);
                }
                finally
                {
                    _constructorReturnTargets.Pop();
                }

                EnsureGoto(exitBlock.Id);
                CurrentBlock = exitBlock;

                return EmitTemporary(new MidLevelIrUseRValue(selfLocal), "ctor");
            }
            finally
            {
                var scope = _scopes.Pop();
                EmitStorageDead(scope);
            }
        }

        private ConstructorBodyContext EnterConstructorBodyContext(
            ConstructorLoweringContext constructorContext,
            StarkTypeSymbol createdType,
            IReadOnlyDictionary<string, string> aliases)
        {
            var previousModuleName = _moduleNameOverride;
            var previousGenericTypeSubstitution = _activeGenericTypeSubstitution;
            var previousAliases = new List<(string AliasName, string? PreviousAlias, bool HadAlias)>(aliases.Count);

            foreach (var (aliasName, targetName) in aliases)
            {
                previousAliases.Add((
                    aliasName,
                    _nameAliases.TryGetValue(aliasName, out var previousAlias) ? previousAlias : null,
                    _nameAliases.ContainsKey(aliasName)));
                _nameAliases[aliasName] = targetName;
            }

            _moduleNameOverride = constructorContext.ModuleName;
            _activeGenericTypeSubstitution = BuildNamedTypeGenericSubstitution(createdType);
            return new ConstructorBodyContext(
                this,
                previousModuleName,
                previousGenericTypeSubstitution,
                previousAliases);
        }

        private IReadOnlyDictionary<string, StarkTypeSymbol>? BuildNamedTypeGenericSubstitution(StarkTypeSymbol createdType)
        {
            Dictionary<string, StarkTypeSymbol>? substitution = _genericTypeSubstitution is { Count: > 0 }
                ? new Dictionary<string, StarkTypeSymbol>(_genericTypeSubstitution, StringComparer.Ordinal)
                : null;

            if (createdType.NamedType is null
                || createdType.TypeArguments is not { Count: > 0 } typeArguments)
            {
                return substitution;
            }

            var baseTypeName = StarkTypeSymbols.GetGenericBaseName(createdType.NamedType);
            if (!_typeModel.NamedTypes.TryGetValue(baseTypeName, out var template)
                || template.GenericParams.Count == 0)
            {
                return substitution;
            }

            substitution ??= new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            for (var index = 0; index < template.GenericParams.Count && index < typeArguments.Count; index++)
            {
                substitution[template.GenericParams[index]] = ApplyGenericSubstitution(typeArguments[index]);
            }

            return substitution;
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
                RecordMoveFromOperand(payloadValues[index], field.Type);
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
                RecordMoveFromOperand(value, targetType.ElementType);
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

            PlaceTarget? currentPlace = currentValue is null ? null : CreateRootPlaceTarget(currentValue);
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
                    currentPlace = null;
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
                    if (!TryBuildMemberCall(currentValue, currentPlace, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out var memberCall))
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
                    currentPlace = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    index++;
                    continue;
                }

                if (currentValue is not null)
                {
                    currentPlace = currentPlace is not null && TryAppendFieldPlaceTarget(currentPlace, memberName, out var fieldPlace)
                        ? fieldPlace
                        : null;
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

        private bool TryLowerIndirectCallExpression(StarkParser.PostfixExpressionContext expression, out MidLevelIrIndirectCallRValue call)
        {
            call = default!;

            if (expression.postfixPart().Length != 1
                || expression.postfixPart()[0].argumentList() is not { } arguments)
            {
                return false;
            }

            if (IsEnumCaseCallTarget(expression.primaryExpression()))
            {
                return false;
            }

            var target = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (target?.Type.Kind != StarkTypeKind.FunctionPointer)
            {
                return false;
            }

            return TryBuildIndirectCall(target, arguments, $"{target.Text}{arguments.GetText()}", out call);
        }

        private bool IsEnumCaseCallTarget(StarkParser.PrimaryExpressionContext expression)
        {
            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return TryResolveEnumCaseReference(genericEnumCaseReference, out _, out _, out _);
            }

            return expression.qualifiedName() is { } qualifiedName
                && TryResolveEnumCaseReference(qualifiedName.GetText(), out _, out _, out _);
        }

        private bool TryBuildIndirectCall(
            MidLevelIrOperand target,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrIndirectCallRValue call)
        {
            call = default!;

            if (target.Type.FunctionPointerReturnType is not { } returnType
                || target.Type.FunctionPointerParameterTypes is not { } parameterTypes
                || parameterTypes.Count != arguments.argument().Length)
            {
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>(arguments.argument().Length);
            for (var index = 0; index < arguments.argument().Length; index++)
            {
                var parameterType = parameterTypes[index];
                if (RequiresIndirectArgument(parameterType))
                {
                    MarkUnsupported(reason: "Indirect function-pointer calls with borrow/out/init parameters require ABI metadata and are not lowered yet.");
                    return false;
                }

                var lowered = LowerExpressionToOperand(arguments.argument(index).expression(), parameterType);
                if (lowered is null)
                {
                    return false;
                }

                var argument = CoerceCallArgument(lowered, parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                RecordMoveFromOperand(argument, parameterType);
            }

            call = new MidLevelIrIndirectCallRValue(
                target,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(returnType),
                text,
                returnType);
            return true;
        }

        private bool TryBuildCall(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (TryResolvePublishedDirectCallSignature(functionName, arguments, out var publishedSignature)
                && TryBuildCall(publishedSignature.Name, publishedSignature, receiver: null, receiverPlace: null, arguments, text, out call))
            {
                return true;
            }

            if (TryGetFunctionOverloads(functionName, out var overloads))
            {
                overloads = FilterDirectCallableTypeMemberFunctions(functionName, overloads);
                if (overloads.Count == 0)
                {
                    return false;
                }

                if (overloads.Count == 1 && !overloads[0].IsGeneric)
                {
                    return TryBuildCall(overloads[0].Name, overloads[0], receiver: null, receiverPlace: null, arguments, text, out call);
                }

                return TryBuildOverloadedCall(overloads, receiver: null, receiverPlace: null, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(functionName, out var signature))
            {
                if (!TryResolvePublishedDirectCallSignature(functionName, arguments, out signature))
                {
                    return false;
                }
            }
            else if (IsStructOrRecordMemberFunctionSourceName(functionName) && !signature.IsStatic)
            {
                return false;
            }

            return TryBuildCall(signature.Name, signature, receiver: null, receiverPlace: null, arguments, text, out call);
        }

        private bool TryBuildMemberCall(
            MidLevelIrOperand receiver,
            PlaceTarget? receiverPlace,
            string memberName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (TryGetValueTextConversionSourceName(memberName, out var valueTextConversionSourceName)
                && TryGetFunctionOverloads(valueTextConversionSourceName, out var valueTextConversionOverloads))
            {
                var candidates = valueTextConversionOverloads
                    .Where(static overload => !overload.IsStatic)
                    .Where(overload => overload.Parameters.Count != 0
                        && FunctionOverloadFacts.CanBindReceiver(overload.Parameters[0].Type, receiver.Type, TypeCompatibilityFacts.CanAssign))
                    .ToArray();
                if (candidates.Length != 0)
                {
                    if (candidates.Length == 1 && !candidates[0].IsGeneric)
                    {
                        return TryBuildCall(candidates[0].Name, candidates[0], receiver, receiverPlace, arguments, text, out call);
                    }

                    return TryBuildOverloadedCall(candidates, receiver, receiverPlace, arguments, text, out call);
                }
            }

            if (receiver.Type.NamedType is not { } namedTypeName)
            {
                return false;
            }

            var sourceName = $"{namedTypeName}.{memberName}";
            if (TryGetFunctionOverloads(sourceName, out var overloads))
            {
                overloads = overloads.Where(static method => !method.IsStatic).ToArray();
                if (overloads.Count == 0)
                {
                    return false;
                }

                if (overloads.Count == 1 && !overloads[0].IsGeneric)
                {
                    return TryBuildCall(overloads[0].Name, overloads[0], receiver, receiverPlace, arguments, text, out call);
                }

                return TryBuildOverloadedCall(overloads, receiver, receiverPlace, arguments, text, out call);
            }

            if (!TryResolveFunctionSignature(sourceName, out var signature)
                || signature.IsStatic
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            return TryBuildCall(signature.Name, signature, receiver, receiverPlace, arguments, text, out call);
        }

        private static bool TryGetValueTextConversionSourceName(string memberName, out string sourceName)
        {
            sourceName = memberName switch
            {
                "ToAscii" => "System.Text.ToAscii",
                "ToUnicode" => "System.Text.ToUnicode",
                _ => string.Empty
            };

            return sourceName.Length != 0;
        }

        private bool TryBuildPublishedMemberCall(
            MidLevelIrOperand receiver,
            PlaceTarget? receiverPlace,
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
            return TryBuildCall(signature.Name, signature, receiver, receiverPlace, arguments, text, out call);
        }

        private bool TryBuildOverloadedCall(
            IReadOnlyList<TypedFunctionSignature> overloads,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
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
                receiverPlace,
                arguments,
                text,
                out call,
                loweredArguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
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
                var receiverOffset = receiver is null ? 0 : 1;
                var explicitParameterCount = Math.Max(0, signature.Parameters.Count - receiverOffset);
                for (var index = 0; index < arguments.argument().Length; index++)
                {
                    var expectedArgumentType = !signature.IsGeneric && index < explicitParameterCount
                        ? signature.Parameters[index + receiverOffset].Type
                        : null;
                    var lowered = LowerExpressionToOperand(arguments.argument(index).expression(), expectedArgumentType);
                    if (lowered is null)
                    {
                        call = default!;
                        return false;
                    }

                    explicitArguments.Add(lowered);
                }
            }

            return TryBuildCall(functionName, signature, receiver, receiverPlace, text, out call, explicitArguments, arguments);
        }

        private bool TryBuildCall(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
            string text,
            out MidLevelIrCallRValue call,
            IReadOnlyList<MidLevelIrOperand> loweredExplicitArguments,
            StarkParser.ArgumentListContext? syntaxArguments = null)
        {
            call = default!;

            if (signature.IsGeneric && !signature.IsGenericInstantiation)
            {
                var resolution = FunctionOverloadFacts.Resolve(
                    [signature],
                    receiver?.Type,
                    loweredExplicitArguments.Select(static argument => argument.Type).ToArray(),
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    return false;
                }

                signature = resolution.Match!;
                functionName = signature.Name;
            }

            var loweredArguments = new List<MidLevelIrOperand>();
            var indirectArgumentLocals = new List<string?>();
            var indirectArgumentAddresses = new List<MidLevelIrOperand?>();
            var receiverOffset = receiver is null ? 0 : 1;
            var explicitParameterCount = Math.Max(0, signature.Parameters.Count - receiverOffset);

            if (receiver is not null)
            {
                var receiverParameterType = signature.Parameters[0].Type;
                var receiverOperand = CoerceCallArgument(receiver, receiverParameterType);
                if (receiverOperand is null)
                {
                    return false;
                }

                loweredArguments.Add(receiverOperand);
                var indirectArgumentAddress = receiverPlace is { Path.Count: > 0 }
                    ? ResolveIndirectArgumentAddress(receiverParameterType, receiverPlace)
                    : null;
                var indirectArgumentLocal = indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(receiverParameterType, receiver)
                        ?? ResolveIndirectArgumentLocal(receiverParameterType, receiverOperand)
                    : null;
                indirectArgumentLocals.Add(indirectArgumentLocal);
                indirectArgumentAddresses.Add(indirectArgumentLocal is null
                    ? indirectArgumentAddress
                    : null);
                RecordMoveFromOperand(receiverOperand, receiverParameterType);
            }

            for (var index = 0; index < Math.Min(loweredExplicitArguments.Count, explicitParameterCount); index++)
            {
                var parameterType = signature.Parameters[index + receiverOffset].Type;
                var sourceArgument = loweredExplicitArguments[index];
                var argument = CoerceCallArgument(sourceArgument, parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                var indirectArgumentAddress = syntaxArguments is not null && index < syntaxArguments.argument().Length
                    ? ResolveIndirectArgumentAddress(parameterType, syntaxArguments.argument(index).expression())
                    : null;
                indirectArgumentLocals.Add(indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(parameterType, sourceArgument)
                        ?? ResolveIndirectArgumentLocal(parameterType, argument)
                    : null);
                indirectArgumentAddresses.Add(indirectArgumentAddress);
                RecordMoveFromOperand(argument, parameterType);
            }

            if (signature.IsVarargs)
            {
                if (loweredExplicitArguments.Count < explicitParameterCount)
                {
                    return false;
                }

                for (var index = explicitParameterCount; index < loweredExplicitArguments.Count; index++)
                {
                    loweredArguments.Add(loweredExplicitArguments[index]);
                    indirectArgumentLocals.Add(null);
                    indirectArgumentAddresses.Add(null);
                }
            }
            else if (loweredExplicitArguments.Count != explicitParameterCount)
            {
                return false;
            }

            var loweredFunctionName = ResolveCallTargetName(functionName, signature);
            if (string.Equals(loweredFunctionName, functionName, StringComparison.Ordinal)
                && TryResolveDictionaryKeyBuiltinCallTarget(functionName, signature, loweredArguments, out var dictionaryKeySpecialization))
            {
                loweredFunctionName = dictionaryKeySpecialization;
            }

            call = new MidLevelIrCallRValue(
                loweredFunctionName,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(signature.ReturnType),
                text,
                indirectArgumentLocals,
                signature.ReturnType,
                indirectArgumentAddresses);
            return true;
        }

        private PlaceTarget? CreateRootPlaceTarget(MidLevelIrOperand root)
        {
            if (!SupportsAddressModel(root))
            {
                return null;
            }

            var rootType = ProjectRootType(root);
            return new PlaceTarget(
                root.Text,
                RootAddress: null,
                RootValue: null,
                rootType,
                rootType,
                Path: [],
                UsesAddressModel: IsBorrowParameterRoot(root),
                IsAddressMutable: GetAddressMutability(root));
        }

        private bool TryAppendFieldPlaceTarget(PlaceTarget target, string memberName, out PlaceTarget updated)
        {
            updated = target;
            if (!TryResolveField(target.Type, memberName, out var field, out var fieldIndex))
            {
                return false;
            }

            var fieldType = ProjectAddressProjectionType(target.Type, field.Type);
            var path = target.Path.ToList();
            path.Add(new PlacePathSegment(
                PlacePathKind.Field,
                field.Name,
                fieldIndex,
                IndexOperand: null,
                ParentType: target.Type,
                SegmentType: fieldType));
            updated = target with
            {
                Type = fieldType,
                Path = path
            };
            return true;
        }

        private MidLevelIrOperand? CoerceCallArgument(MidLevelIrOperand sourceArgument, StarkTypeSymbol parameterType)
        {
            var direct = CoerceOperand(sourceArgument, parameterType);
            if (direct is not null)
            {
                return direct;
            }

            if (parameterType.BorrowKind == StarkBorrowKind.None
                && parameterType.InitializationKind == StarkInitializationKind.None)
            {
                return null;
            }

            var storageType = StarkTypeSymbols.WithQualifiers(
                parameterType,
                borrowKind: StarkBorrowKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            return CoerceOperand(sourceArgument, storageType);
        }

        private bool TryResolveDictionaryKeyBuiltinCallTarget(
            string functionName,
            TypedFunctionSignature signature,
            IReadOnlyList<MidLevelIrOperand> loweredArguments,
            out string symbolName)
        {
            symbolName = string.Empty;

            var templateName = ResolveDictionaryKeyBuiltinTemplateName(functionName, signature);
            if (templateName is null || loweredArguments.Count == 0)
            {
                return false;
            }

            var keyType = StarkTypeSymbols.WithQualifiers(
                loweredArguments[0].Type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            var specializationKey = MidLevelIrLowerer.BuildMaterializedSpecializationKey(templateName, [keyType]);
            return _materializedSpecializationSymbols.TryGetValue(specializationKey, out symbolName!);
        }

        private static string? ResolveDictionaryKeyBuiltinTemplateName(
            string functionName,
            TypedFunctionSignature signature)
        {
            foreach (var candidate in new[]
                     {
                         signature.TemplateName,
                         signature.DisplaySourceName,
                         signature.Name,
                         functionName
                     })
            {
                if (candidate is "System.Collections.DictionaryKey.Hash" or "DictionaryKey.Hash")
                {
                    return "System.Collections.DictionaryKey.Hash";
                }

                if (candidate is "System.Collections.DictionaryKey.Equals" or "DictionaryKey.Equals")
                {
                    return "System.Collections.DictionaryKey.Equals";
                }
            }

            return null;
        }

        private MidLevelIrOperand? LoadPointerBackedBorrowReturnIfNeeded(MidLevelIrCallRValue call, MidLevelIrOperand callResult)
        {
            if (call.SourceReturnType is not { } sourceReturnType
                || !StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
            {
                return callResult;
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    callResult,
                    StarkTypeSymbols.BorrowReturnValueType(sourceReturnType),
                    $"{callResult.Text}:load"),
                "load");
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
            if (literal.DOLLAR() is not null && literal.StringLiteral() is { } interpolatedString)
            {
                if (!InterpolatedText.TryFold(
                        interpolatedString.GetText(),
                        new CompileTimeEvaluationServices(
                            TryResolveIdentifier: _compileTimeConstantState.TryResolve),
                        out var foldedLiteral,
                        out _))
                {
                    MarkUnsupported(literal, "Interpolated text literals must fold before MIR lowering.");
                    return null;
                }

                var foldedType = TextLiteralDecoder.CanUseUtf8Storage(foldedLiteral, TextLiteralKind.String)
                    ? StarkTypeSymbols.Ascii
                    : StarkTypeSymbols.Unicode;
                var foldedOperand = new MidLevelIrStringConstantOperand(foldedLiteral, foldedType);
                return expectedType is null ? foldedOperand : CoerceOperand(foldedOperand, expectedType);
            }

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
                return new MidLevelIrFloatConstantOperand(CompileTimeExpressionEvaluator.StripFloatSuffix(literalText), type);
            }

            return new MidLevelIrIntegerConstantOperand(ParseIntegerLiteralText(literalText), type);
        }

        private static StarkTypeSymbol InferTextLiteralType(string text, TextLiteralKind kind)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(text, kind)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        private MidLevelIrOperand? ResolveNamedOperand(string name, StarkTypeSymbol? expectedType = null)
        {
            var operand = TryResolveNamedValueOperand(name);
            if (operand is not null)
            {
                return expectedType is null ? operand : CoerceOperand(operand, expectedType);
            }

            if (expectedType?.Kind == StarkTypeKind.FunctionPointer
                && TryResolveFunctionAddressOperand(name, expectedType, out var functionAddress))
            {
                return functionAddress;
            }

            if (TryResolveFunctionSignature(name, out _))
            {
                MarkUnsupported();
                return null;
            }

            MarkUnsupported();
            return null;
        }

        private bool TryResolveFunctionAddressOperand(
            string name,
            StarkTypeSymbol targetType,
            out MidLevelIrFunctionAddressOperand operand)
        {
            operand = default!;

            if (!TryGetFunctionOverloads(name, out var overloads))
            {
                if (!TryResolveFunctionSignature(name, out var signature))
                {
                    return false;
                }

                overloads = [signature];
            }

            overloads = FilterDirectCallableTypeMemberFunctions(name, overloads);
            var candidates = overloads
                .Where(static function => !function.IsGeneric)
                .Where(static function => !function.IsUnsafe)
                .Where(function => TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(
                    targetType,
                    StarkTypeSymbols.FunctionPointer(
                        function.Kind,
                        function.ReturnType,
                        function.Parameters.Select(static parameter => parameter.Type).ToArray())))
                .ToArray();

            if (candidates.Length != 1)
            {
                return false;
            }

            var function = candidates[0];
            operand = new MidLevelIrFunctionAddressOperand(ResolveCallTargetName(function.Name, function), targetType);
            return true;
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

            if (TryResolveTypeQualifiedMemberSourceName(sourceName, out var resolvedMemberSourceName)
                && _typeModel.Overloads.TryGetValue(resolvedMemberSourceName, out overloads!))
            {
                return true;
            }

            if (!sourceName.Contains('.', StringComparison.Ordinal))
            {
                var importedCandidates = new List<TypedFunctionSignature>();
                foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, sourceName))
                {
                    if (_typeModel.Overloads.TryGetValue(candidateName, out var candidates))
                    {
                        importedCandidates.AddRange(candidates);
                    }
                }

                if (importedCandidates.Count > 0)
                {
                    overloads = importedCandidates;
                    return true;
                }
            }

            overloads = [];
            return false;
        }

        private bool TryResolveTypeQualifiedMemberSourceName(string sourceName, out string resolvedSourceName)
        {
            resolvedSourceName = string.Empty;
            var separator = sourceName.LastIndexOf('.');
            if (separator <= 0)
            {
                return false;
            }

            var qualifier = sourceName[..separator];
            if (!TryResolveNamedTypeBySourceName(qualifier, out var namedType))
            {
                return false;
            }

            resolvedSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{sourceName[(separator + 1)..]}";
            return !string.Equals(resolvedSourceName, sourceName, StringComparison.Ordinal);
        }

        private IReadOnlyList<TypedFunctionSignature> FilterDirectCallableTypeMemberFunctions(
            string sourceName,
            IReadOnlyList<TypedFunctionSignature> functions)
        {
            return IsStructOrRecordMemberFunctionSourceName(sourceName)
                ? functions.Where(static function => function.IsStatic).ToArray()
                : functions;
        }

        private bool IsStructOrRecordMemberFunctionSourceName(string sourceName)
        {
            var separator = sourceName.LastIndexOf('.');
            if (separator <= 0)
            {
                return false;
            }

            var typeName = sourceName[..separator];
            return TryResolveNamedTypeBySourceName(typeName, out var namedType)
                && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
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

            if (!name.Contains('.', StringComparison.Ordinal))
            {
                var importedFallbackMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, name)
                    .Where(_fallbackFunctions.ContainsKey)
                    .ToArray();
                if (importedFallbackMatches.Length == 1)
                {
                    signature = _fallbackFunctions[importedFallbackMatches[0]];
                    return true;
                }
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

            if (!name.Contains('.', StringComparison.Ordinal))
            {
                var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, name)
                    .Where(_typeModel.Globals.ContainsKey)
                    .ToArray();
                if (importedMatches.Length == 1)
                {
                    global = _typeModel.Globals[importedMatches[0]];
                    return true;
                }
            }

            if (!name.Contains('.', StringComparison.Ordinal)
                && _fallbackGlobals.TryGetValue($"{CurrentModuleName}.{name}", out global!))
            {
                return true;
            }

            if (!name.Contains('.', StringComparison.Ordinal))
            {
                var importedFallbackMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, name)
                    .Where(_fallbackGlobals.ContainsKey)
                    .ToArray();
                if (importedFallbackMatches.Length == 1)
                {
                    global = _fallbackGlobals[importedFallbackMatches[0]];
                    return true;
                }
            }

            return _fallbackGlobals.TryGetValue(name, out global!);
        }

        private StarkTypeSymbol ResolveTypeWithGenericSubstitution(
            StarkParser.Type_Context type,
            string? moduleName)
        {
            return ApplyGenericSubstitution(
                _typeResolver.ResolveType(type, ActiveGenericParameterNames(), moduleName));
        }

        private ISet<string>? ActiveGenericParameterNames()
        {
            if (_activeGenericTypeSubstitution is not { Count: > 0 })
            {
                return _genericParameterNames;
            }

            var names = _genericParameterNames is { Count: > 0 }
                ? new HashSet<string>(_genericParameterNames, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in _activeGenericTypeSubstitution.Keys)
            {
                names.Add(name);
            }

            return names;
        }

        private bool TryResolveLocalDeclarationType(
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

            var key = TemplateLocalDeclarationFacts.BuildLookupKey(
                declarationKind,
                declarationContext.Start.Line,
                declarationContext.Start.Column + 1);
            var typedDeclaration = _typeModel.LocalDeclarations.LastOrDefault(record =>
                string.Equals(record.EnclosingFunctionName, _function.Name, StringComparison.Ordinal)
                && TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location) == key);
            if (typedDeclaration is not null)
            {
                type = typedDeclaration.Type;
                return true;
            }

            type = StarkTypeSymbols.Error;
            return false;
        }

        private bool TryResolvePublishedDirectCallSignature(
            string functionName,
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

            if (_importedTemplateDirectCalls.Count > 0)
            {
                var argumentCount = arguments.argument().Length;
                var matches = _importedTemplateDirectCalls.Values
                    .Select(ApplyGenericSubstitution)
                    .Where(candidate =>
                        candidate.Parameters.Count == argumentCount
                        && PublishedDirectCallNameMatches(functionName, candidate))
                    .ToArray();
                if (matches.Length == 1)
                {
                    signature = matches[0];
                    return true;
                }
            }

            signature = null!;
            return false;
        }

        private bool PublishedDirectCallNameMatches(string functionName, TypedFunctionSignature signature)
        {
            var possibleNames = new List<string> { functionName };
            if (!functionName.Contains('.', StringComparison.Ordinal))
            {
                possibleNames.Add($"{CurrentModuleName}.{functionName}");
            }

            if (TryResolveTypeQualifiedMemberSourceName(functionName, out var resolvedMemberSourceName))
            {
                possibleNames.Add(resolvedMemberSourceName);
            }

            foreach (var candidate in new[]
                     {
                         signature.SourceName,
                         signature.TemplateName,
                         signature.Name
                     })
            {
                if (candidate is not null
                    && possibleNames.Any(possibleName => string.Equals(candidate, possibleName, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

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
            return _activeGenericTypeSubstitution is { Count: > 0 }
                ? FunctionOverloadFacts.SubstituteType(type, _activeGenericTypeSubstitution)
                : type;
        }

        private TypedFunctionSignature ApplyGenericSubstitution(TypedFunctionSignature signature)
        {
            if (_activeGenericTypeSubstitution is not { Count: > 0 })
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
                _typeResolver.ResolveQualifiedType(baseName, ActiveGenericParameterNames(), genericQualifiedName.qualifiedName().Start, CurrentModuleName));
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
                && _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, typeName)
                    .Where(_typeModel.NamedTypes.ContainsKey)
                    .ToArray() is { Length: 1 } importedMatches)
            {
                namedType = _typeModel.NamedTypes[importedMatches[0]];
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

                if (operators[index - 1] == "+"
                    && TryBuildRuntimeTextConcatenation(current, next, $"{operands[index - 1].GetText()} + {operands[index].GetText()}", out var runtimeConcat))
                {
                    current = EmitTemporary(runtimeConcat, "concat");
                    if (current is null)
                    {
                        return null;
                    }

                    continue;
                }

                var operatorText = operators[index - 1];
                var resultType = operatorText is "<<" or ">>"
                    ? current.Type
                    : FindCommonType(current.Type, next.Type);
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

                if (operators[index - 1] == "+"
                    && TryFoldTextConstantConcatenation(left, right, resultType, out var foldedText))
                {
                    current = foldedText;
                    continue;
                }

                current = EmitTemporary(
                    new MidLevelIrBinaryRValue(mapOperator(operatorText), left, right, resultType, operatorText),
                    "bin");

                if (current is null)
                {
                    return null;
                }
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private bool TryBuildRuntimeTextConcatenation(
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            var sourceName = left.Type.Kind switch
            {
                StarkTypeKind.Unicode => "System.Text.ConcatUnicode",
                StarkTypeKind.Ascii => "System.Text.ConcatAscii",
                _ => null
            };
            if (sourceName is null || !TryGetFunctionOverloads(sourceName, out var overloads))
            {
                return false;
            }

            if (left is not MidLevelIrStringConstantOperand literalLeft
                || !TryGetTextLiteralLength(literalLeft.LiteralText, left.Type, out var leftLength))
            {
                return false;
            }

            var leftLengthOperand = new MidLevelIrIntegerConstantOperand(leftLength, NonNegativeI64Type);
            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [left.Type, leftLengthOperand.Type, right.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return false;
            }

            return TryBuildCall(
                resolution.Match!.Name,
                resolution.Match,
                receiver: null,
                receiverPlace: null,
                text,
                out call,
                [left, leftLengthOperand, right]);
        }

        private static bool TryGetTextLiteralLength(string literalText, StarkTypeSymbol type, out BigInteger length)
        {
            var kind = literalText.StartsWith('\'')
                ? TextLiteralKind.Character
                : TextLiteralKind.String;
            if (!TextLiteralDecoder.TryDecode(literalText, kind, out var decoded, out _))
            {
                length = default;
                return false;
            }

            length = type.Kind == StarkTypeKind.Unicode
                ? decoded.Utf32CodeUnits.Length
                : decoded.Utf8Bytes.Length;
            return true;
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

            if (operand.Type.Kind == StarkTypeKind.FunctionPointer
                && targetType.Kind == StarkTypeKind.FunctionPointer
                && TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(targetType, operand.Type))
            {
                return operand;
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
                return new MidLevelIrFloatConstantOperand(CompileTimeExpressionEvaluator.StripFloatSuffix(floatLiteral.GetText()), switchType);
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

            if (TryGetSimplePostfixExpression(expression) is { } indirectPostfix
                && TryLowerIndirectCallExpression(indirectPostfix, out var indirectCall))
            {
                value = indirectCall;
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

        private static StarkParser.AdditiveExpressionContext? TryGetStandaloneAdditiveExpression(StarkParser.ExpressionContext expression)
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
            return shift.additiveExpression().Length == 1
                ? shift.additiveExpression(0)
                : null;
        }

        private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
        {
            var additive = TryGetStandaloneAdditiveExpression(expression);
            if (additive is null || additive.multiplicativeExpression().Length != 1)
            {
                return null;
            }

            var multiplicative = additive.multiplicativeExpression(0);
            if (multiplicative.unaryExpression().Length != 1)
            {
                return null;
            }

            var unary = multiplicative.unaryExpression(0);
            if (unary.powerExpression() is not { } power
                || power.unaryExpression() is not null
                || power.postfixExpression() is not { } postfix
                || postfix.postfixPart().Length != 0)
            {
                return null;
            }

            var literal = postfix.primaryExpression().literal();
            return literal?.DOLLAR() is not null && literal.StringLiteral() is not null
                ? literal
                : null;
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

        private MidLevelIrOperand? ResolveIndirectArgumentAddress(StarkTypeSymbol parameterType, PlaceTarget? target)
        {
            if (!RequiresIndirectArgument(parameterType) || target is null)
            {
                return null;
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? ResolveIndirectArgumentAddress(
            StarkTypeSymbol parameterType,
            StarkParser.ExpressionContext expression)
        {
            if (!RequiresIndirectArgument(parameterType)
                || !TryExtractSimpleUnaryExpression(expression, out var unaryExpression)
                || MayEvaluateNestedExpressionForAddress(unaryExpression)
                || !TryResolveAssignmentTarget(unaryExpression, out var target))
            {
                return null;
            }

            return ResolveIndirectArgumentAddress(parameterType, target);
        }

        private static bool MayEvaluateNestedExpressionForAddress(StarkParser.UnaryExpressionContext expression)
        {
            if (expression.unaryOperator() is not null)
            {
                return true;
            }

            var postfix = expression.powerExpression()?.postfixExpression();
            return postfix is not null && postfix.postfixPart().Any(static part =>
                part.argumentList() is not null || part.expressionList() is not null);
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

            if (left.DisplayName == right.DisplayName)
            {
                return left;
            }

            if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Integer)
            {
                return StarkTypeSymbols.Integer(
                    Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0),
                    isUnsigned: left.IsUnsigned && right.IsUnsigned);
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
                && RequiresIndirectArgument(parameterBinding.Type);
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
                ? CanFormMutableAddressFromLocal(local)
                : true;
            return EmitTemporary(
                new MidLevelIrAddressOfLocalRValue(name, type, AddressType(type, isMutable), $"&{name}"),
                "addr");
        }

        private MidLevelIrOperand? CreateAddressOfParameter(string name, StarkTypeSymbol type)
        {
            var isMutable = _parametersByName.TryGetValue(name, out var parameter)
                ? CanFormMutableAddressFromParameter(parameter.Type)
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
                    ? CanFormMutableAddressFromLocal(localBinding)
                    : true,
                MidLevelIrGlobalOperand global => _typeModel.Globals.TryGetValue(global.Name, out var globalBinding)
                    ? globalBinding.IsMutable && CanMutateThroughType(globalBinding.Type)
                    : true,
                MidLevelIrParameterOperand parameter => CanFormMutableAddressFromParameter(parameter.Type),
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

    private static bool CanFormMutableAddressFromLocal(MidLevelIrLocal local)
    {
        return !local.IsConstant
            && local.Type.AccessKind != StarkAccessKind.Frozen
            && (local.IsMutable || local.Type.IsMutableView || local.Type.InitializationKind != StarkInitializationKind.None);
    }

    private static bool CanFormMutableAddressFromParameter(StarkTypeSymbol type)
    {
        return (type.IsMutableView || type.InitializationKind != StarkInitializationKind.None)
            && CanMutateThroughType(type);
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

        private static bool TryFoldTextConstantConcatenation(
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            StarkTypeSymbol resultType,
            out MidLevelIrOperand folded)
        {
            folded = null!;
            if (left is not MidLevelIrStringConstantOperand leftText
                || right is not MidLevelIrStringConstantOperand rightText
                || resultType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode))
            {
                return false;
            }

            if (!TextLiteralDecoder.TryConcatenateAsStringLiteral(
                    leftText.LiteralText,
                    GetTextLiteralKind(leftText.LiteralText),
                    rightText.LiteralText,
                    GetTextLiteralKind(rightText.LiteralText),
                    out var literalText))
            {
                return false;
            }

            folded = new MidLevelIrStringConstantOperand(literalText, resultType);
            return true;
        }

        private static TextLiteralKind GetTextLiteralKind(string literalText)
        {
            return literalText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String;
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
        private readonly record struct ConstructorReturnTarget(int ExitBlockId, int ScopeDepth);
    }
}
