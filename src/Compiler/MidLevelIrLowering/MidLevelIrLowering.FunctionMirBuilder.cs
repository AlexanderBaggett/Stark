using System.Numerics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder : IDisposable
    {
        private static readonly StarkTypeSymbol ByteType = StarkTypeSymbols.Integer(8);
        private static readonly StarkTypeSymbol BytePointerType = StarkTypeSymbols.RawPointer(ByteType, isMutable: false);
        private static readonly StarkTypeSymbol I64Type = StarkTypeSymbols.Integer(64);
        private static readonly StarkTypeSymbol NonNegativeI64Type = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - 1);
        private const int LargeAggregateProjectionAddressThresholdBytes = 128;
        private const string ClosureCaptureDropStatePrefix = "$closure_capture$";

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
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression,
            string? CaptureStorageName = null);

        private sealed record LowerableAggregatePattern(
            string TypeName,
            string? EnumVariantName,
            IReadOnlyList<LowerableAggregateFieldPattern> FieldPatterns,
            string? WholeCaptureName,
            string? WholeCaptureStorageName = null);

        private sealed record PendingSwitchBinding(
            string SourceName,
            string StorageName,
            MidLevelIrOperand Source,
            MidLevelIrOperand? RuntimeMoveSource = null);

        private sealed record LowerableSwitchLabel(
            string LabelText,
            StarkParser.LiteralContext? Literal,
            StarkParser.ExpressionContext? GuardExpression,
            bool IsDefault,
            bool IsMatchAll,
            string? CaptureName,
            LowerableAggregatePattern? AggregatePattern,
            ImportedTemplateTypedBodyExpressionSummary? ImportedLiteralExpression = null,
            ImportedTemplateTypedBodyExpressionSummary? ImportedGuardExpression = null,
            string? CaptureStorageName = null);

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
            DynamicStorageIndex,
            RawPointerIndex,
            SliceIndex
        }

        private enum DynamicStorageOperationKind
        {
            Reserve,
            TryReserve,
            TryReserveCapacity,
            MoveLast,
            MoveAt
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
            bool IsAddressMutable,
            string? ClosureCaptureName = null);

        private sealed record LoweredAssignment(
            string Text,
            string? TargetName,
            StarkTypeSymbol TargetType,
            MidLevelIrRValue? DirectValue,
            MidLevelIrOperand ResultValue,
            MidLevelIrOperand? Address,
            bool ReplacesWholeValue,
            DynamicStorageLengthUpdate? DynamicLengthUpdate = null,
            MemoryWriteKind WriteKind = MemoryWriteKind.Replacement);

        private sealed record DynamicStorageLengthUpdate(
            MidLevelIrOperand StorageAddress,
            StarkTypeSymbol StorageType,
            MidLevelIrOperand InitializedIndex);

        private sealed record DynamicInitSliceProvenance(
            MidLevelIrOperand StorageAddress,
            StarkTypeSymbol StorageType,
            MidLevelIrOperand StartIndex);

        private sealed record MemoryRangeOperand(MidLevelIrOperand Start, MidLevelIrOperand End);

        private sealed class ScopeFrame
        {
            public List<(string Name, StarkTypeSymbol Type)> Locals { get; } = [];
            public List<(string AliasName, string? PreviousAlias, bool HadAlias)> NameAliases { get; } = [];
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
        private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
        private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _fallbackFunctions;
        private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _fallbackGlobals;
        private readonly IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> _literalTypes;
        private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
        private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;
        private readonly IReadOnlyDictionary<string, EnumLayoutSymbol> _publishedEnumLayouts;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundDirectCallOperation> _boundDirectCalls;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundMemberCallOperation> _boundMemberCalls;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundFunctionPointerCallOperation> _boundFunctionPointerCalls;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundClosureCallOperation> _boundClosureCalls;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundIndexAccessOperation> _boundIndexAccesses;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundObjectCreationOperation> _boundObjectCreations;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundEnumConstructionOperation> _boundEnumConstructions;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundEnumCallOperation> _boundEnumCalls;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundEnumValueOperation> _boundEnumValues;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundDynamicStorageOperation> _boundDynamicStorageOperations;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundTextInterpolationOperation> _boundTextInterpolations;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundTextBuildOperation> _boundTextBuilds;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundLayoutQueryOperation> _boundLayoutQueries;
        private readonly IReadOnlyDictionary<BoundOperationKey, BoundSwitchDispatchOperation> _boundSwitchDispatches;
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
        private readonly IReadOnlyDictionary<int, ImportedTemplateFunctionAddressSummary> _importedTemplateFunctionAddresses;
        private readonly IReadOnlyDictionary<int, BoundDynamicStorageOperation> _importedTemplateDynamicStorageOperations;
        private readonly IReadOnlyDictionary<string, string> _materializedSpecializationSymbols;
        private readonly ISet<string>? _genericParameterNames;
        private readonly IReadOnlyDictionary<string, StarkTypeSymbol>? _genericTypeSubstitution;
        private IReadOnlyDictionary<string, StarkTypeSymbol>? _activeGenericTypeSubstitution;
        private readonly IDisposable _logScope;
        private readonly List<MidLevelIrLocal> _locals = [];
        private readonly Dictionary<string, MidLevelIrLocal> _localsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DynamicInitSliceProvenance> _dynamicInitSliceProvenanceByLocal = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TypedParameterSymbol> _parametersByName;
        private readonly Dictionary<string, bool> _runtimeDropStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _closureCaptureMoveSourcesByTempName = new(StringComparer.Ordinal);
        private readonly List<string> _parameterDropOrder = [];
        private readonly Dictionary<string, string> _nameAliases = new(StringComparer.Ordinal);
        private readonly Stack<ConstructorReturnTarget> _constructorReturnTargets = [];
        private readonly List<BasicBlockBuilder> _blocks = [];
        private readonly Stack<LoopTargets> _loops = [];
        private readonly Stack<BreakTargets> _breakTargets = [];
        private readonly Stack<ScopeFrame> _scopes = [];
        private readonly Stack<ScopedNoAliasGroup> _activeScopedNoAliasGroups = [];
        private readonly Stack<string> _activeLoopAccessGroups = [];
        private readonly CompileTimeEvaluator _compileTimeEvaluator;
        private readonly CompileTimeEvaluator.CompileTimeEvaluationState _compileTimeConstantState = new();
        private readonly ImportedTemplateLowerer _importedTemplateLowerer;
        private readonly PlaceLowerer _placeLowerer;
        private readonly RuntimeDropLowerer _runtimeDropLowerer;
        private readonly SwitchPatternLowerer _switchPatternLowerer;
        private readonly ClosureLambdaTypingRecord? _currentClosureLambda;
        private readonly IReadOnlyDictionary<string, ClosureCaptureFieldSymbol> _closureCaptureFieldsByName;
        private readonly StarkTypeSymbol? _closureEnvironmentType;
        private string? _moduleNameOverride;
        private SourceLocation? _currentStatementLocation;
        private MidLevelIrOperand? _closureEnvironmentAddress;
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
        private int _nextScopedLocalId;
        private int _nextRuntimeDisjointScopeId;
        private int _nextLoopAccessGroupId;
        private string? _lastCallBuildFailureReason;

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
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, TypedFunctionSignature> fallbackFunctions,
            IReadOnlyDictionary<string, TypedGlobalSymbol> fallbackGlobals,
            IReadOnlyDictionary<LiteralKey, StarkTypeSymbol> literalTypes,
            IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> objectCreationConstructors,
            IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
            IReadOnlyDictionary<string, EnumLayoutSymbol> publishedEnumLayouts,
            BoundOperationIndex boundOperations,
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
            _namedTypes = namedTypes;
            _fallbackFunctions = fallbackFunctions;
            _fallbackGlobals = fallbackGlobals;
            _literalTypes = literalTypes;
            _objectCreationConstructors = objectCreationConstructors;
            _publishedConcreteLayouts = publishedConcreteLayouts;
            _publishedEnumLayouts = publishedEnumLayouts;
            _boundDirectCalls = boundOperations.DirectCalls;
            _boundMemberCalls = boundOperations.MemberCalls;
            _boundFunctionPointerCalls = boundOperations.FunctionPointerCalls;
            _boundClosureCalls = boundOperations.ClosureCalls;
            _boundIndexAccesses = boundOperations.IndexAccesses;
            _boundObjectCreations = boundOperations.ObjectCreations;
            _boundEnumConstructions = boundOperations.EnumConstructions;
            _boundEnumCalls = boundOperations.EnumCalls;
            _boundEnumValues = boundOperations.EnumValues;
            _boundDynamicStorageOperations = boundOperations.DynamicStorageOperations;
            _boundTextInterpolations = boundOperations.TextInterpolations;
            _boundTextBuilds = boundOperations.TextBuilds;
            _boundLayoutQueries = boundOperations.LayoutQueries;
            _boundSwitchDispatches = boundOperations.SwitchDispatches;
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
            _importedTemplateFunctionAddresses = importedTemplateSummary?.FunctionAddresses.ToDictionary(
                static functionAddress => functionAddress.Ordinal,
                static functionAddress => functionAddress)
                ?? new Dictionary<int, ImportedTemplateFunctionAddressSummary>();
            _importedTemplateDynamicStorageOperations = importedTemplateSummary?.BoundOperations
                .Where(static operation => operation.Ordinal is not null
                    && operation.Operation is BoundDynamicStorageOperation)
                .ToDictionary(
                    static operation => operation.Ordinal!.Value,
                    static operation => (BoundDynamicStorageOperation)operation.Operation)
                ?? new Dictionary<int, BoundDynamicStorageOperation>();
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
            _currentClosureLambda = typeModel.ClosureLambdas.FirstOrDefault(lambda =>
                string.Equals(lambda.FunctionName, function.Name, StringComparison.Ordinal)
                || string.Equals(lambda.FunctionName, function.Signature.Name, StringComparison.Ordinal));
            _closureCaptureFieldsByName = _currentClosureLambda?.CaptureFields.ToDictionary(
                static capture => capture.Name,
                StringComparer.Ordinal)
                ?? new Dictionary<string, ClosureCaptureFieldSymbol>(StringComparer.Ordinal);
            _closureEnvironmentType = _currentClosureLambda?.EnvironmentTypeName is { Length: > 0 } environmentTypeName
                ? StarkTypeSymbols.Named(environmentTypeName)
                : null;
            InitializeOnceClosureCaptureDropStates();
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

        private static string BuildClosureCaptureDropStateKey(string captureName) =>
            $"{ClosureCaptureDropStatePrefix}{captureName}";

        private bool IsCurrentOnceHeapClosureInvoke() =>
            _currentClosureLambda?.ClosureType is
            {
                ClosureStorageKind: StarkClosureStorageKind.Heap,
                ClosureCallCapability: StarkClosureCallCapability.Once
            };

        private static bool IsOwnedClosureCaptureFieldForDrop(ClosureCaptureFieldSymbol capture) =>
            capture.StorageKind == ClosureCaptureStorageKind.Value
            && string.Equals(capture.Mode, "move", StringComparison.Ordinal);

        private void InitializeOnceClosureCaptureDropStates()
        {
            if (!IsCurrentOnceHeapClosureInvoke() || _currentClosureLambda is null)
            {
                return;
            }

            foreach (var capture in _currentClosureLambda.CaptureFields)
            {
                if (!IsOwnedClosureCaptureFieldForDrop(capture)
                    || !RequiresRuntimeDrop(capture.FieldType))
                {
                    continue;
                }

                _runtimeDropStates[BuildClosureCaptureDropStateKey(capture.Name)] = true;
            }
        }

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
            _importedDirectCallOrdinals = _importedTemplateSummary is { DirectCalls.Count: > 0 } directCallTemplateSummary
                ? CollectTemplateDirectCallOrdinals(body, directCallTemplateSummary.DirectCalls)
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
                        EmitOnceClosureEnvironmentCleanup();
                        CurrentBlock.Terminator = new MidLevelIrTerminator(
                            MidLevelIrTerminatorKind.Return,
                            Targets: [],
                            Location: CreateSourceLocation(expression.Start) ?? _functionLocation);
                    }
                }
                else
                {
                    var operand = LowerReturnExpressionToRequiredOperand(bodyExpression, _function.Signature.ReturnType);
                    if (_function.Signature.ReturnType.BorrowKind == StarkBorrowKind.None)
                    {
                        RecordMoveFromOperand(operand, _function.Signature.ReturnType);
                    }

                    operand = MaterializeReturnOperandBeforeStorageCleanup(operand);
                    EmitStorageDeadBeyondDepth(0);
                    EmitOnceClosureEnvironmentCleanup();
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
                throw LoweringInvariantViolation(expression, "Lambda body must be an expression or block.");
            }

            if (!CurrentBlock.HasTerminator)
            {
                CompleteOpenFunctionTerminator();
            }
        }

        public void LowerEmptyClosureDrop()
        {
            CompleteOpenFunctionTerminator();
        }

        public void LowerClosureEnvironmentDrop(ClosureLambdaTypingRecord lambda)
        {
            if (lambda.EnvironmentTypeName is not { Length: > 0 } environmentTypeName)
            {
                throw LoweringInvariantViolation(null, $"Heap closure drop function '{_function.Name}' has no environment type.");
            }

            var environmentType = StarkTypeSymbols.Named(environmentTypeName);
            var environmentParameter = new MidLevelIrParameterOperand(
                CallableValueFacts.ClosureEnvironmentParameterName,
                CallableValueFacts.BuildClosureDropEnvironmentPointerType());
            var typedEnvironmentPointer = EmitRequiredTemporary(
                new MidLevelIrConvertRValue(
                    environmentParameter,
                    AddressType(environmentType, isMutable: true),
                    $"{environmentParameter.Text}:typed-env"),
                "closure_env");

            for (var index = lambda.CaptureFields.Count - 1; index >= 0; index--)
            {
                var capture = lambda.CaptureFields[index];
                if (!IsOwnedClosureCaptureFieldForDrop(capture)
                    || !RequiresRuntimeDrop(capture.FieldType))
                {
                    continue;
                }

                if (!TryResolveField(environmentType, capture.FieldName, out _, out var fieldIndex))
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Heap closure drop function '{_function.Name}' could not resolve environment field '{capture.FieldName}'.");
                }

                var fieldAddress = EmitRequiredTemporary(
                    new MidLevelIrFieldAddressRValue(
                        typedEnvironmentPointer,
                        environmentType,
                        capture.FieldName,
                        fieldIndex,
                        AddressType(capture.FieldType, isMutable: true),
                        $"{typedEnvironmentPointer.Text}.{capture.FieldName}"),
                    "closure_field");
                var fieldValue = EmitRequiredTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        fieldAddress,
                        capture.FieldType,
                        $"{fieldAddress.Text}:load"),
                    "closure_field");
                EmitRuntimeDropFromOperandCore(fieldValue, capture.FieldType);
            }

            Emit(
                MidLevelIrStatementKind.Evaluate,
                $"free {environmentParameter.Text}",
                value: new MidLevelIrHeapStorageFreeRValue(environmentParameter, $"free {environmentParameter.Text}"));
            CompleteOpenFunctionTerminator();
        }

        private void CompleteOpenFunctionTerminator()
        {
            if (_function.Signature.ReturnType.Kind == StarkTypeKind.Void)
            {
                EmitOnceClosureEnvironmentCleanup();
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: [],
                    Location: _functionLocation);
                return;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Unreachable,
                Targets: [],
                Location: _functionLocation);
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
            RestoreScopedNameAliases(scope);
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

                if (statement.unsafeStatement() is { } unsafeStatement)
                {
                    if (unsafeStatement.block() is { } unsafeBlock)
                    {
                        LowerBlock(unsafeBlock);
                    }
                    else if (unsafeStatement.assumeStatement() is { } unsafeAssumeStatement)
                    {
                        LowerAssumeStatement(unsafeAssumeStatement);
                    }

                    return;
                }

                if (statement.assumeStatement() is { } assumeStatement)
                {
                    LowerAssumeStatement(assumeStatement);
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
                        throw LoweringInvariantViolation(statement.breakStatement(), "'break' requires an enclosing loop or switch.");
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
                        throw LoweringInvariantViolation(statement.continueStatement(), "'continue' requires an enclosing loop.");
                    }

                    var loop = _loops.Peek();
                    EmitStorageDeadBeyondDepth(loop.ScopeDepth);
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Goto,
                        [loop.ContinueTarget],
                        LoopBehavior: loop.ContinueLoopBehavior,
                        LoopContracts: loop.ContinueLoopContracts,
                        LoopAccessGroups: loop.ContinueLoopAccessGroups);
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
                var sourceName = declarator.Identifier().GetText();
                var localName = DeclareLocal(sourceName, declaredType, storageClass: "local", isMutable: false, isConstant: true);
                Emit(MidLevelIrStatementKind.StorageLive, localName, localName, declaredType);
                TrackCompileTimeConstant(sourceName, declaredType, declarator.variableInitializer());
                if (LowerVariableInitializer(localName, declaredType, declarator.variableInitializer()))
                {
                    MarkLocalHasConstProvenance(localName);
                }

                InitializeRuntimeDropState(localName, declaredType, isActive: true);
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
                var sourceName = declarator.Identifier().GetText();
                if (TryGetFixedTextStorageCapacity(declarator, out var fixedTextCapacity))
                {
                    LowerFixedTextStorageVariableDeclaration(
                        sourceName,
                        declaredType,
                        storageClass,
                        declaration.MUT() is not null,
                        fixedTextCapacity,
                        declarator.variableInitializer());
                    continue;
                }

                var isMutable = declaration.MUT() is not null;
                var localName = DeclareLocal(sourceName, declaredType, storageClass, isMutable, isConstant: false);
                Emit(MidLevelIrStatementKind.StorageLive, localName, localName, declaredType);
                InitializeRuntimeDropState(localName, declaredType, isActive: false);

                if (declarator.variableInitializer() is { } initializer)
                {
                    var initializerHasConstProvenance = LowerVariableInitializer(localName, declaredType, initializer);
                    if (!isMutable && initializerHasConstProvenance)
                    {
                        MarkLocalHasConstProvenance(localName);
                    }

                    SetRuntimeDropState(localName, isActive: true);
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
                var declaredLocalName = DeclareLocal(name, declaredType, storageClass, isMutable, isConstant: false);
                Emit(MidLevelIrStatementKind.StorageLive, declaredLocalName, declaredLocalName, declaredType);
                InitializeRuntimeDropState(declaredLocalName, declaredType, isActive: false);
                if (initializer is not null)
                {
                    LowerVariableInitializer(declaredLocalName, declaredType, initializer);
                    SetRuntimeDropState(declaredLocalName, isActive: true);
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

            var localName = DeclareLocal(name, declaredType, storageClass, isMutable, isConstant: false);
            Emit(MidLevelIrStatementKind.StorageLive, localName, localName, declaredType);
            InitializeRuntimeDropState(localName, declaredType, isActive: false);

            var emptyText = BuildFixedTextStorageValue(storageName, storageType, declaredType, capacity);
            if (emptyText is null)
            {
                throw LoweringInvariantViolation(initializer, "Fixed text storage value could not be initialized.");
            }

            Emit(MidLevelIrStatementKind.Assign, $"{name}[{capacity}]", localName, declaredType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(localName, isActive: true);

            if (initializer is null)
            {
                return;
            }

            if (initializer.expression() is not { } expression)
            {
                throw LoweringInvariantViolation(initializer, "Fixed text storage initializer requires a text-building expression.");
            }

            if (TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral)
            {
                if (!LowerFixedTextStorageInterpolatedInitializer(localName, declaredType, interpolatedLiteral))
                {
                    throw LoweringInvariantViolation(initializer, "Fixed text storage interpolation could not be lowered.");
                }

                return;
            }

            if (TryGetStandaloneAdditiveExpression(expression) is not { } additive)
            {
                throw LoweringInvariantViolation(initializer, "Fixed text storage initializer requires a text-building expression.");
            }

            if (!LowerFixedTextStorageConcatInitializer(localName, declaredType, additive))
            {
                throw LoweringInvariantViolation(initializer, "Fixed text storage concatenation could not be lowered.");
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
            if (!TryResolveBoundTextBuild(additive, out var boundTextBuild))
            {
                throw LoweringInvariantViolation(additive, "Fixed text storage concatenation reached MIR without a bound text-build operation.");
            }

            var operands = additive.multiplicativeExpression();
            var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additive);
            if (operands.Length < 2 || operators.Any(static item => item != "+"))
            {
                return false;
            }

            ValidateBoundTextBuildOperation(
                boundTextBuild,
                additive,
                buildKind: "concat",
                usesFixedStorage: true,
                resultType: destinationType,
                operandCount: operands.Length);

            var viewType = GetFixedTextStorageViewType(destinationType);
            var current = LowerFixedTextConcatOperandToView(operands[0], viewType);
            if (current is null)
            {
                throw LoweringInvariantViolation(operands[0], "Fixed text storage concatenation could not lower the left operand to a text view.");
            }

            for (var index = 1; index < operands.Length; index++)
            {
                var next = LowerFixedTextConcatOperandToView(operands[index], viewType);
                if (next is null)
                {
                    throw LoweringInvariantViolation(operands[index], "Fixed text storage concatenation could not lower the right operand to a text view.");
                }

                var destinationAddress = CreateMutableAddressOfLocalForInitialization(destinationName, destinationType);
                if (destinationAddress is null)
                {
                    throw LoweringInvariantViolation(additive, "Fixed text storage concatenation could not address the destination buffer.");
                }

                if (!TryBuildFixedTextConcatCall(destinationAddress, current, next, $"{current.Text} + {next.Text}", out var call))
                {
                    throw LoweringInvariantViolation(additive, "Fixed text storage concatenation could not resolve the System.Text concat helper.");
                }

                var success = EmitTemporary(call, "textconcat");
                if (success is null)
                {
                    throw LoweringInvariantViolation(additive, "Fixed text storage concatenation could not materialize the concat result.");
                }

                EmitTrapOnFalse(success, "textconcat_overflow");
                current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
                if (current is null)
                {
                    throw LoweringInvariantViolation(additive, "Fixed text storage concatenation could not view the destination after concat.");
                }
            }

            return true;
        }

        private bool LowerFixedTextStorageInterpolatedInitializer(
            string destinationName,
            StarkTypeSymbol destinationType,
            StarkParser.LiteralContext literal)
        {
            if (!TryResolveBoundTextInterpolation(literal, out var boundInterpolation))
            {
                throw LoweringInvariantViolation(literal, "Fixed text storage interpolation reached MIR without a bound text-interpolation operation.");
            }

            if (literal.StringLiteral() is not { } interpolatedString
                || !InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
            {
                return false;
            }

            ValidateBoundTextInterpolationOperation(
                boundInterpolation,
                literal,
                usesFixedStorage: true,
                resultType: destinationType,
                segmentCount: segments.Count,
                holeCount: segments.OfType<InterpolatedTextHoleSegment>().Count());

            var viewType = GetFixedTextStorageViewType(destinationType);
            var current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
            if (current is null)
            {
                throw LoweringInvariantViolation(literal, "Fixed text storage interpolation could not view the destination buffer.");
            }

            foreach (var segment in segments)
            {
                var next = LowerInterpolatedTextSegmentToView(segment, destinationType, viewType);
                if (next is null)
                {
                    throw LoweringInvariantViolation(literal, "Fixed text storage interpolation could not lower one of its parts.");
                }

                if (!AppendFixedTextStorageSegment(destinationName, destinationType, current, next, literal))
                {
                    return false;
                }

                current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
                if (current is null)
                {
                    throw LoweringInvariantViolation(literal, "Fixed text storage interpolation could not view the destination after appending text.");
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
                throw LoweringInvariantViolation(hole.Expression, "Interpolated text hole did not lower to a value.");
            }

            if (CanUseFixedTextConcatSource(destinationType, value.Type))
            {
                return BuildTextBufferView(value, viewType);
            }

            if (!TextFormattingFacts.TryGetFixedBufferFormatInfo(destinationType, value.Type, out var formatInfo))
            {
                throw LoweringInvariantViolation(hole.Expression, $"Interpolated text does not have a formatter for '{value.Type.DisplayName}'.");
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
                throw LoweringInvariantViolation(context, "Interpolated text formatter storage could not be initialized.");
            }

            Emit(MidLevelIrStatementKind.Assign, $"{textName}[{formatInfo.Capacity}]", textName, destinationType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(textName, isActive: true);

            var destinationAddress = CreateMutableAddressOfLocalForInitialization(textName, destinationType);
            if (destinationAddress is null)
            {
                throw LoweringInvariantViolation(context, "Interpolated text formatter storage could not be addressed.");
            }

            if (!TryBuildFixedTextFormatCall(destinationAddress, value, formatInfo.FunctionName, context.GetText(), out var call))
            {
                throw LoweringInvariantViolation(context, $"Interpolated text could not call '{formatInfo.FunctionName}'.");
            }

            var success = EmitTemporary(call, "textformat");
            if (success is null)
            {
                throw LoweringInvariantViolation(context, "Interpolated text formatter result could not be materialized.");
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
                throw LoweringInvariantViolation(context, "Fixed text storage interpolation could not address the destination buffer.");
            }

            if (!TryBuildFixedTextConcatCall(destinationAddress, current, next, $"{current.Text} + {next.Text}", out var call))
            {
                throw LoweringInvariantViolation(context, "Fixed text storage interpolation could not resolve the System.Text concat helper.");
            }

            var success = EmitTemporary(call, "textconcat");
            if (success is null)
            {
                throw LoweringInvariantViolation(context, "Fixed text storage interpolation could not materialize the concat result.");
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
                throw LoweringInvariantViolation(expression, "Fixed text storage operand did not lower to a value.");
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
                throw LoweringInvariantViolation(null, $"Could not find '{functionName}' while lowering fixed text storage concatenation.");
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [operand.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded
                || !TryBuildCall(resolution.Match!.Name, resolution.Match, receiver: null, receiverPlace: null, operand.Text, out var call, [operand]))
            {
                throw LoweringInvariantViolation(null, $"Could not call '{functionName}' for operand type '{operand.Type.DisplayName}'.");
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
                throw LoweringInvariantViolation(null, $"Could not find '{functionName}' while lowering fixed text storage concatenation.");
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [destinationAddress.Type, left.Type, right.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                throw LoweringInvariantViolation(null, $"Could not match '{functionName}' for '{destinationAddress.Type.DisplayName}', '{left.Type.DisplayName}', and '{right.Type.DisplayName}'.");
            }

            if (!TryBuildCall(resolution.Match!.Name, resolution.Match, receiver: null, receiverPlace: null, text, out call, [destinationAddress, left, right]))
            {
                throw LoweringInvariantViolation(null, $"Could not build call to '{functionName}' for fixed text storage concatenation.");
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
                throw LoweringInvariantViolation(null, $"Could not find '{sourceName}' while lowering fixed text storage interpolation.");
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                receiverType: null,
                [destinationAddress.Type, value.Type],
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                throw LoweringInvariantViolation(null, $"Could not match '{sourceName}' for '{destinationAddress.Type.DisplayName}' and '{value.Type.DisplayName}'.");
            }

            if (!TryBuildCall(resolution.Match!.Name, resolution.Match, receiver: null, receiverPlace: null, text, out call, [destinationAddress, value]))
            {
                throw LoweringInvariantViolation(null, $"Could not build call to '{sourceName}' for fixed text storage interpolation.");
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
                && (_namedTypes.TryGetValue(typeName, out namedType!)
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

        private bool LowerVariableInitializer(string name, StarkTypeSymbol declaredType, StarkParser.VariableInitializerContext initializer)
        {
            if (initializer.expression() is { } expression)
            {
                var hasConstProvenance = EmitAssignmentFromExpression(name, declaredType, expression, expression.GetText());
                TryRecordDynamicInitSliceProvenance(name, declaredType, expression);
                return hasConstProvenance;
            }

            if (initializer.objectInitializer() is { } objectInitializer)
            {
                var value = LowerObjectInitializer(declaredType, objectInitializer);
                if (value is null)
                {
                    throw LoweringInvariantViolation(
                        initializer,
                        $"Object initializer for local '{name}' did not materialize a MIR value.");
                }

                Emit(
                    MidLevelIrStatementKind.Assign,
                    $"{name} = {FormatInitializer(initializer)}",
                    name,
                    declaredType,
                    new MidLevelIrUseRValue(value),
                    writeKind: MemoryWriteKind.Initialization);
                return OperandHasConstProvenance(value);
            }

            if (initializer.arrayInitializer() is { } arrayInitializer)
            {
                var value = LowerArrayInitializer(declaredType, arrayInitializer);
                if (value is null)
                {
                    throw LoweringInvariantViolation(
                        initializer,
                        $"Array initializer for local '{name}' did not materialize a MIR value.");
                }

                Emit(
                    MidLevelIrStatementKind.Assign,
                    $"{name} = {FormatInitializer(initializer)}",
                    name,
                    declaredType,
                    new MidLevelIrUseRValue(value),
                    writeKind: MemoryWriteKind.Initialization);
                return OperandHasConstProvenance(value);
            }

            throw LoweringInvariantViolation(
                initializer,
                $"Variable initializer for local '{name}' has no lowerable expression, object, or array shape.");
        }

        private bool TryRecordDynamicInitSliceProvenance(
            string localName,
            StarkTypeSymbol declaredType,
            StarkParser.ExpressionContext expression)
        {
            if (declaredType.Kind != StarkTypeKind.Slice
                || declaredType.InitializationKind != StarkInitializationKind.Init
                || !TryExtractSimpleUnaryExpression(expression, out var initUnary)
                || initUnary.INIT() is null
                || initUnary.unaryOperator() is not null
                || TryGetSimplePostfixExpression(initUnary.unaryExpression()) is not { } postfix
                || postfix.postfixPart().Length == 0
                || postfix.postfixPart()[^1].expressionList() is not { } range
                || range.expression().Length != 2
                || !IsSimpleSideEffectFreeExpression(range.expression(0)))
            {
                return false;
            }

            var postfixParts = postfix.postfixPart();
            if (!TryInitializePostfixState(postfix.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            PlaceTarget? currentPlace = currentValue is null ? null : CreateRootPlaceTarget(currentValue);
            for (var index = 0; index < postfixParts.Length - 1; index++)
            {
                var postfixPart = postfixParts[index];
                if (postfixPart.argumentList() is not null || postfixPart.expressionList() is not null)
                {
                    return false;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null)
                {
                    currentPlace = currentPlace is not null && TryAppendFieldPlaceTarget(currentPlace, memberName, out var fieldPlace)
                        ? fieldPlace
                        : null;
                    currentValue = currentPlace is { UsesAddressModel: true }
                        ? ReadPlace(currentPlace)
                        : TryLowerPublishedFieldAccess(currentValue, postfixPart, out var publishedFieldAccess)
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
                    currentPlace = CreateRootPlaceTarget(currentValue);
                }
                else
                {
                    currentName = qualifiedName;
                }
            }

            if (currentValue?.Type.Kind != StarkTypeKind.Dynamic || currentPlace is null)
            {
                return false;
            }

            var storageAddress = BuildAddress(currentPlace);
            var start = LowerExpressionToOperand(range.expression(0), NonNegativeI64Type);
            if (storageAddress is null || start is null || start.Type.Kind != StarkTypeKind.Integer)
            {
                return false;
            }

            _dynamicInitSliceProvenanceByLocal[localName] = new DynamicInitSliceProvenance(
                storageAddress,
                currentValue.Type,
                CoerceOperand(start, NonNegativeI64Type) ?? start);
            return true;
        }

        private static bool IsSimpleSideEffectFreeExpression(StarkParser.ExpressionContext expression)
        {
            return TryGetSimplePostfixExpression(expression) is { } postfix
                && postfix.postfixPart().All(static part => part.argumentList() is null);
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

            throw LoweringInvariantViolation(
                initializer,
                $"Variable initializer for '{targetType.DisplayName}' has no lowerable expression, object, or array shape.");
        }

        private void LowerReturn(StarkParser.ReturnStatementContext returnStatement)
        {
            if (_constructorReturnTargets.Count > 0)
            {
                var constructorReturn = _constructorReturnTargets.Peek();
                if (returnStatement.expression() is not null)
                {
                    throw LoweringInvariantViolation(returnStatement, "Constructor bodies cannot return a value.");
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
                EmitOnceClosureEnvironmentCleanup();
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: [],
                    ValueText: null,
                    Value: null);
                return;
            }

            var operand = LowerReturnExpressionToRequiredOperand(returnStatement.expression(), _function.Signature.ReturnType);
            if (_function.Signature.ReturnType.BorrowKind == StarkBorrowKind.None)
            {
                RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            }

            operand = MaterializeReturnOperandBeforeStorageCleanup(operand);
            EmitStorageDeadBeyondDepth(0);
            EmitOnceClosureEnvironmentCleanup();
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: returnStatement.expression().GetText(),
                Value: operand);
        }

        private MidLevelIrOperand LowerReturnExpressionToRequiredOperand(StarkParser.ExpressionContext expression, StarkTypeSymbol returnType)
        {
            return RequireLoweredOperand(
                expression,
                LowerReturnExpressionToOperand(expression, returnType),
                "Return expression was accepted but did not lower to a MIR operand.");
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

        private MidLevelIrOperand MaterializeReturnOperandBeforeStorageCleanup(MidLevelIrOperand operand)
        {
            if (!ReturnOperandNeedsPreCleanupMaterialization(operand))
            {
                return operand;
            }

            return EmitRequiredTemporary(new MidLevelIrUseRValue(operand), "return");
        }

        private bool ReturnOperandNeedsPreCleanupMaterialization(MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrLocalOperand local => IsAddressableLocal(local.Name),
                MidLevelIrParameterOperand parameter => parameter.Type.BorrowKind != StarkBorrowKind.None
                    || parameter.Type.InitializationKind != StarkInitializationKind.None,
                _ => false
            };
        }

        private void LowerExpressionStatement(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionStatementCore(expression))
            {
                return;
            }

            throw LoweringInvariantViolation(
                expression,
                "Expression statement could not be lowered to an assignment, call, or value operand.");
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

            if (TryLowerExpressionAsCallStatement(expression, out var call))
            {
                EmitEvaluateCallStatement(expression, call);
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
                throw LoweringInvariantViolation(
                    conditionalExpression.logicalOrExpression(),
                    "Conditional call statement condition did not lower to a bool operand.");
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
                throw LoweringInvariantViolation(
                    conditionalExpression.expression(0),
                    "Conditional call statement true branch was accepted but did not lower.");
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerConditionalCallStatementBranch(conditionalExpression.expression(1)))
            {
                throw LoweringInvariantViolation(
                    conditionalExpression.expression(1),
                    "Conditional call statement false branch was accepted but did not lower.");
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerConditionalCallStatementBranch(StarkParser.ExpressionContext expression)
        {
            if (TryLowerExpressionAsCallStatement(expression, out var call))
            {
                EmitEvaluateCallStatement(expression, call);
                return true;
            }

            if (TryLowerExpressionAsRValue(expression, out var value))
            {
                EmitEvaluateExpressionStatement(expression, value);
                return true;
            }

            if (TryLowerConditionalCallStatement(expression))
            {
                return true;
            }

            if (CanLowerConditionalCallStatementBranch(expression))
            {
                throw LoweringInvariantViolation(
                    expression,
                    "Conditional call statement branch was classified as lowerable but produced no MIR operation.");
            }

            return false;
        }

        private bool TryLowerExpressionAsCallStatement(
            StarkParser.ExpressionContext expression,
            out MidLevelIrCallStatementOperation call)
        {
            call = default!;
            if (TryGetSimplePostfixExpression(expression) is not { } postfix)
            {
                return false;
            }

            if (TryLowerDirectCallExpressionAsStatement(postfix, out var directCall))
            {
                call = directCall;
                return true;
            }

            if (TryLowerIndirectCallExpressionAsStatement(postfix, out var indirectCall))
            {
                call = indirectCall;
                return true;
            }

            if (TryLowerClosureCallExpressionAsStatement(postfix, out var closureCall))
            {
                call = closureCall;
                return true;
            }

            return false;
        }

        private static MidLevelIrDirectCallStatementOperation ToStatementCall(MidLevelIrCallRValue call)
        {
            return new MidLevelIrDirectCallStatementOperation(
                call.FunctionName,
                call.Arguments,
                call.Type,
                call.Text,
                call.IndirectArgumentLocalNames,
                call.SourceReturnType,
                call.IndirectArgumentAddresses,
                call.PostCallDynamicLengthCommits);
        }

        private static bool TryCreateValueCall(
            LoweredDirectCallParts parts,
            out MidLevelIrCallRValue call)
        {
            call = default!;
            if (parts.ReturnType.Kind == StarkTypeKind.Void)
            {
                return false;
            }

            call = new MidLevelIrCallRValue(
                parts.FunctionName,
                parts.Arguments,
                parts.ReturnType,
                parts.Text,
                parts.IndirectArgumentLocalNames,
                parts.SourceReturnType,
                parts.IndirectArgumentAddresses,
                parts.PostCallDynamicLengthCommits);
            return true;
        }

        private static MidLevelIrDirectCallStatementOperation ToStatementCall(LoweredDirectCallParts parts)
        {
            return new MidLevelIrDirectCallStatementOperation(
                parts.FunctionName,
                parts.Arguments,
                parts.ReturnType,
                parts.Text,
                parts.IndirectArgumentLocalNames,
                parts.SourceReturnType,
                parts.IndirectArgumentAddresses,
                parts.PostCallDynamicLengthCommits);
        }

        private static MidLevelIrIndirectCallStatementOperation ToStatementCall(MidLevelIrIndirectCallRValue call)
        {
            return new MidLevelIrIndirectCallStatementOperation(
                call.Target,
                call.Arguments,
                call.Type,
                call.Text,
                call.SourceReturnType,
                call.IndirectArgumentLocalNames,
                call.IndirectArgumentAddresses,
                call.MayFree);
        }

        private static bool TryCreateValueCall(
            LoweredIndirectCallParts parts,
            out MidLevelIrIndirectCallRValue call)
        {
            call = default!;
            if (parts.ReturnType.Kind == StarkTypeKind.Void)
            {
                return false;
            }

            call = new MidLevelIrIndirectCallRValue(
                parts.Target,
                parts.Arguments,
                parts.ReturnType,
                parts.Text,
                parts.SourceReturnType,
                parts.IndirectArgumentLocalNames,
                parts.IndirectArgumentAddresses,
                parts.MayFree);
            return true;
        }

        private static MidLevelIrIndirectCallStatementOperation ToStatementCall(LoweredIndirectCallParts parts)
        {
            return new MidLevelIrIndirectCallStatementOperation(
                parts.Target,
                parts.Arguments,
                parts.ReturnType,
                parts.Text,
                parts.SourceReturnType,
                parts.IndirectArgumentLocalNames,
                parts.IndirectArgumentAddresses,
                parts.MayFree);
        }

        private sealed record LoweredDirectCallParts(
            string FunctionName,
            IReadOnlyList<MidLevelIrOperand> Arguments,
            StarkTypeSymbol ReturnType,
            string Text,
            IReadOnlyList<string?> IndirectArgumentLocalNames,
            StarkTypeSymbol SourceReturnType,
            IReadOnlyList<MidLevelIrOperand?> IndirectArgumentAddresses,
            IReadOnlyList<MidLevelIrDynamicStorageLengthCommit>? PostCallDynamicLengthCommits);

        private sealed record LoweredIndirectCallParts(
            MidLevelIrOperand Target,
            IReadOnlyList<MidLevelIrOperand> Arguments,
            StarkTypeSymbol ReturnType,
            string Text,
            StarkTypeSymbol SourceReturnType,
            IReadOnlyList<string?> IndirectArgumentLocalNames,
            IReadOnlyList<MidLevelIrOperand?> IndirectArgumentAddresses,
            bool MayFree);

        private void EmitEvaluateCallStatement(
            StarkParser.ExpressionContext expression,
            MidLevelIrCallStatementOperation call)
        {
            EmitEvaluateCallStatement(expression.GetText(), call);
            if (call is MidLevelIrDirectCallStatementOperation directCall
                && IsKnownNoReturnCall(directCall.FunctionName))
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Unreachable,
                    Targets: [],
                    Location: CreateSourceLocation(expression.Start) ?? _currentStatementLocation ?? _functionLocation);
            }
        }

        private void EmitEvaluateCallStatement(
            string text,
            MidLevelIrCallStatementOperation call)
        {
            Emit(MidLevelIrStatementKind.Evaluate, text, call: call);
            if (call is MidLevelIrDirectCallStatementOperation directCall)
            {
                EmitPostCallDynamicLengthCommits(directCall);
            }
        }

        private void EmitEvaluateExpressionStatement(StarkParser.ExpressionContext expression, MidLevelIrRValue value)
        {
            Emit(MidLevelIrStatementKind.Evaluate, expression.GetText(), value: value);
            if (value is MidLevelIrCallRValue evaluatedCall)
            {
                EmitPostCallDynamicLengthCommits(evaluatedCall);
            }

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
                || string.Equals(functionName, "System.Runtime.Platform.MacOS.ExitProcess", StringComparison.Ordinal)
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

            if (expression.INIT() is not null
                && expression.ASSIGN() is not null
                && expression.assignmentOperator() is null)
            {
                var initAssignmentText = $"init {expression.unaryExpression().GetText()} = {expression.assignmentExpression().GetText()}";
                if (TryResolveIndirectPointerAssignmentTarget(expression.unaryExpression(), out var initPointerAddress, out var initPointeeType))
                {
                    var assignedValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), initPointeeType);
                    if (assignedValue is null)
                    {
                        throw LoweringInvariantViolation(
                            expression.assignmentExpression(),
                            $"Init pointer assignment '{initAssignmentText}' did not lower its right-hand side.");
                    }

                    assignment = new LoweredAssignment(
                        initAssignmentText,
                        TargetName: null,
                        initPointeeType,
                        DirectValue: null,
                        ResultValue: assignedValue,
                        Address: initPointerAddress,
                        ReplacesWholeValue: false,
                        WriteKind: MemoryWriteKind.Initialization);
                    return true;
                }

                if (!TryResolveAssignmentTarget(expression.unaryExpression(), out var initTarget))
                {
                    return false;
                }

                var initValue = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), initTarget.Type);
                if (initValue is null)
                {
                    throw LoweringInvariantViolation(
                        expression.assignmentExpression(),
                        $"Init assignment '{initAssignmentText}' did not lower its right-hand side.");
                }

                assignment = BuildAssignment(initTarget, initValue, initAssignmentText) with
                {
                    WriteKind = MemoryWriteKind.Initialization
                };
                if (TryBuildDynamicStorageLengthUpdate(initTarget, out var dynamicLengthUpdate))
                {
                    assignment = assignment with { DynamicLengthUpdate = dynamicLengthUpdate };
                }

                return true;
            }

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
                    throw LoweringInvariantViolation(
                        expression.assignmentExpression(),
                        $"Assignment '{assignmentText}' did not lower its right-hand side.");
                }

                assignment = BuildAssignment(target, assignedValue, assignmentText);
                return true;
            }

            var currentValue = ReadPlace(target);
            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                throw LoweringInvariantViolation(
                    expression.assignmentExpression(),
                    $"Compound assignment '{assignmentText}' did not lower its right-hand side.");
            }

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Compound assignment '{assignmentText}' could not coerce operands to '{commonType.DisplayName}'.");
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");

            assignment = temp is null
                ? default!
                : BuildAssignment(target, CoerceOperand(temp, target.Type) ?? temp, assignmentText);
            if (temp is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Compound assignment '{assignmentText}' did not materialize its computed value.");
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
                    throw LoweringInvariantViolation(
                        expression.assignmentExpression(),
                        $"Pointer assignment '{assignmentText}' did not lower its right-hand side.");
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
                throw LoweringInvariantViolation(
                    expression,
                    $"Pointer compound assignment '{assignmentText}' could not load the pointee.");
            }

            var right = LowerAssignmentExpressionToOperand(expression.assignmentExpression(), currentValue.Type);
            if (right is null)
            {
                throw LoweringInvariantViolation(
                    expression.assignmentExpression(),
                    $"Pointer compound assignment '{assignmentText}' did not lower its right-hand side.");
            }

            var @operator = MapAssignmentOperator(expression.assignmentOperator().GetText());

            var commonType = FindCommonType(currentValue.Type, right.Type);
            var leftValue = CoerceOperand(currentValue, commonType);
            var rightValue = CoerceOperand(right, commonType);
            if (leftValue is null || rightValue is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Pointer compound assignment '{assignmentText}' could not coerce operands to '{commonType.DisplayName}'.");
            }

            var temp = EmitTemporary(
                new MidLevelIrBinaryRValue(@operator, leftValue, rightValue, commonType, assignmentText),
                "compound");
            if (temp is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Pointer compound assignment '{assignmentText}' did not materialize its computed value.");
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
            var conditionExpression = ifStatement.expression();
            ScopedNoAliasGroup? trueBranchScopedNoAliasGroup = null;
            var condition = conditionExpression is not null
                ? LowerExpressionToOperand(conditionExpression, StarkTypeSymbols.Bool)
                : LowerDisjointRuntimeCondition(ifStatement.disjointRuntimeCondition(), out trueBranchScopedNoAliasGroup);
            var branchWeights = CreateConditionalBranchWeights(ifStatement.weightSpecifier());

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                elseBlock is null ? [thenBlock.Id, joinBlock.Id] : [thenBlock.Id, elseBlock.Id],
                ConditionText: conditionExpression?.GetText() ?? ifStatement.disjointRuntimeCondition()?.GetText() ?? "false",
                Condition: condition,
                BranchWeights: branchWeights);

            var branchEntryDropStates = SnapshotRuntimeDropStates();

            CurrentBlock = thenBlock;
            if (trueBranchScopedNoAliasGroup is null)
            {
                LowerStatement(ifStatement.statement(0));
            }
            else
            {
                _activeScopedNoAliasGroups.Push(trueBranchScopedNoAliasGroup);
                try
                {
                    LowerStatement(ifStatement.statement(0));
                }
                finally
                {
                    _activeScopedNoAliasGroups.Pop();
                }
            }

            var thenFallsThrough = !CurrentBlock.HasTerminator;
            var thenDropStates = SnapshotRuntimeDropStates();
            EnsureGoto(joinBlock.Id);

            Dictionary<string, bool>? elseDropStates;
            bool elseFallsThrough;
            if (elseBlock is not null)
            {
                RestoreRuntimeDropStates(branchEntryDropStates);
                CurrentBlock = elseBlock;
                LowerStatement(ifStatement.statement(1));
                elseFallsThrough = !CurrentBlock.HasTerminator;
                elseDropStates = SnapshotRuntimeDropStates();
                EnsureGoto(joinBlock.Id);
            }
            else
            {
                elseFallsThrough = true;
                elseDropStates = branchEntryDropStates;
            }

            RestoreRuntimeDropStates(MergeRuntimeDropStates(
                thenFallsThrough ? thenDropStates : null,
                elseFallsThrough ? elseDropStates : null,
                branchEntryDropStates));
            CurrentBlock = joinBlock;
        }

        private void LowerAssumeStatement(StarkParser.AssumeStatementContext assumeStatement)
        {
            var scopedNoAliasGroup = TryCreateRuntimeDisjointScopedNoAliasGroup(
                assumeStatement.disjointRuntimeCondition().expressionList().expression(),
                "unsafe-assume-disjoint");
            if (scopedNoAliasGroup is null)
            {
                LowerStatement(assumeStatement.statement());
                return;
            }

            _activeScopedNoAliasGroups.Push(scopedNoAliasGroup);
            try
            {
                LowerStatement(assumeStatement.statement());
            }
            finally
            {
                _activeScopedNoAliasGroups.Pop();
            }
        }

        private Dictionary<string, bool> SnapshotRuntimeDropStates()
        {
            return new Dictionary<string, bool>(_runtimeDropStates, StringComparer.Ordinal);
        }

        private void RestoreRuntimeDropStates(IReadOnlyDictionary<string, bool> snapshot)
        {
            _runtimeDropStates.Clear();
            foreach (var (name, isActive) in snapshot)
            {
                _runtimeDropStates[name] = isActive;
            }
        }

        private static Dictionary<string, bool> MergeRuntimeDropStates(
            IReadOnlyDictionary<string, bool>? first,
            IReadOnlyDictionary<string, bool>? second,
            IReadOnlyDictionary<string, bool> fallback)
        {
            if (first is null && second is null)
            {
                return new Dictionary<string, bool>(fallback, StringComparer.Ordinal);
            }

            if (first is null)
            {
                return new Dictionary<string, bool>(second!, StringComparer.Ordinal);
            }

            if (second is null)
            {
                return new Dictionary<string, bool>(first, StringComparer.Ordinal);
            }

            var merged = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var name in first.Keys.Concat(second.Keys).Distinct(StringComparer.Ordinal))
            {
                var firstActive = first.TryGetValue(name, out var activeInFirst) && activeInFirst;
                var secondActive = second.TryGetValue(name, out var activeInSecond) && activeInSecond;
                merged[name] = firstActive && secondActive;
            }

            return merged;
        }

        private static Dictionary<string, bool> MergeRuntimeDropStates(
            IReadOnlyList<IReadOnlyDictionary<string, bool>> states,
            IReadOnlyDictionary<string, bool> fallback)
        {
            if (states.Count == 0)
            {
                return new Dictionary<string, bool>(fallback, StringComparer.Ordinal);
            }

            var merged = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var name in states.SelectMany(static state => state.Keys).Distinct(StringComparer.Ordinal))
            {
                var activeOnEveryPath = true;
                foreach (var state in states)
                {
                    if (!state.TryGetValue(name, out var isActive) || !isActive)
                    {
                        activeOnEveryPath = false;
                        break;
                    }
                }

                merged[name] = activeOnEveryPath;
            }

            return merged;
        }

        private MidLevelIrOperand LowerDisjointRuntimeCondition(
            StarkParser.DisjointRuntimeConditionContext? condition,
            out ScopedNoAliasGroup? scopedNoAliasGroup)
        {
            scopedNoAliasGroup = null;
            if (condition is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Runtime disjoint checks require a parsed disjoint(...) condition.");
            }

            var expressions = condition.expressionList().expression();
            if (expressions.Length < 2)
            {
                throw LoweringInvariantViolation(
                    condition,
                    "Runtime disjoint checks require at least two operands.");
            }

            var ranges = new List<MemoryRangeOperand>(expressions.Length);
            foreach (var expression in expressions)
            {
                if (!TryLowerMemoryRange(expression, out var range))
                {
                    throw LoweringInvariantViolation(
                        expression,
                        $"Runtime disjoint operand '{expression.GetText()}' did not lower to a memory range.");
                }

                ranges.Add(range);
            }

            scopedNoAliasGroup = TryCreateRuntimeDisjointScopedNoAliasGroup(expressions);

            MidLevelIrOperand? combined = null;
            for (var leftIndex = 0; leftIndex < ranges.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < ranges.Count; rightIndex++)
                {
                    var pairwiseDisjoint = EmitRangeDisjointComparison(
                        ranges[leftIndex],
                        ranges[rightIndex],
                        $"{expressions[leftIndex].GetText()} disjoint {expressions[rightIndex].GetText()}");
                    combined = combined is null
                        ? pairwiseDisjoint
                        : EmitBooleanBinary(
                            MidLevelIrBinaryOperator.BitwiseAnd,
                            combined,
                            pairwiseDisjoint,
                            $"{combined.Text} && {pairwiseDisjoint.Text}",
                            "disjoint_all");
                }
            }

            return combined ?? new MidLevelIrBoolConstantOperand(true);
        }

        private ScopedNoAliasGroup? TryCreateRuntimeDisjointScopedNoAliasGroup(
            IReadOnlyList<StarkParser.ExpressionContext> expressions,
            string scopePrefix = "runtime-disjoint")
        {
            var rootKeys = new List<string>(expressions.Count);
            foreach (var expression in expressions)
            {
                if (TryResolveRuntimeDisjointRootKey(expression, out var rootKey))
                {
                    rootKeys.Add(rootKey);
                }
            }

            var distinctRootKeys = rootKeys
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctRootKeys.Length < 2)
            {
                return null;
            }

            return new ScopedNoAliasGroup(
                $"{scopePrefix}-{_nextRuntimeDisjointScopeId++}",
                distinctRootKeys);
        }

        private bool TryResolveRuntimeDisjointRootKey(
            StarkParser.ExpressionContext expression,
            out string rootKey)
        {
            rootKey = string.Empty;

            if (TryGetRawPointerRegionExpression(expression, out var regionRootName, out var regionStart, out var regionLength)
                && _parametersByName.ContainsKey(regionRootName))
            {
                rootKey = TryCreateScopedNoAliasParameterRegionRootKey(
                    regionRootName,
                    regionStart,
                    regionLength,
                    out var regionRootKey)
                        ? regionRootKey
                        : CreateScopedNoAliasParameterRootKey(regionRootName);
                return true;
            }

            if (!TryExtractSimpleUnaryExpression(expression, out var unaryExpression)
                || !TryResolveAssignmentTarget(unaryExpression, out var target)
                || target.RootName is not { } rootName
                || !_parametersByName.ContainsKey(rootName))
            {
                return false;
            }

            rootKey = CreateScopedNoAliasParameterRootKey(rootName);
            return true;
        }

        private bool TryLowerMemoryRange(StarkParser.ExpressionContext expression, out MemoryRangeOperand range)
        {
            range = default!;

            if (TryLowerRawPointerRegionMemoryRange(expression, out range))
            {
                return true;
            }

            if (TryExtractSimpleUnaryExpression(expression, out var unaryExpression)
                && TryResolveAssignmentTarget(unaryExpression, out var target)
                && IsPointerBackedContractRangeType(target.Type))
            {
                return TryBuildPlaceMemoryRange(target, expression, out range);
            }

            var operand = LowerExpressionToOperand(expression);
            if (operand is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    "Runtime disjoint operand could not be lowered.");
            }

            if (operand.Type.Kind == StarkTypeKind.RawPointer)
            {
                return TryBuildRawPointerMemoryRange(operand, expression, out range);
            }

            if (TryGetContiguousViewElementType(operand.Type, out var elementType))
            {
                return TryBuildViewMemoryRange(operand, elementType, expression, out range);
            }

            throw LoweringInvariantViolation(
                expression,
                $"Runtime disjoint operand '{expression.GetText()}' does not lower to a contiguous memory range.");
        }

        private bool TryLowerRawPointerRegionMemoryRange(
            StarkParser.ExpressionContext expression,
            out MemoryRangeOperand range)
        {
            range = default!;
            if (!TryGetRawPointerRegionExpression(expression, out var rootName, out var startExpression, out var lengthExpression))
            {
                return false;
            }

            var pointer = ResolveNamedOperand(rootName);
            if (pointer is null
                || pointer.Type.Kind != StarkTypeKind.RawPointer
                || pointer.Type.ElementType is not { } elementType)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Runtime disjoint raw pointer region '{expression.GetText()}' requires a raw pointer root.");
            }

            var startIndex = LowerExpressionToOperand(startExpression);
            var elementCount = LowerExpressionToOperand(lengthExpression);
            if (startIndex is null
                || startIndex.Type.Kind != StarkTypeKind.Integer
                || elementCount is null
                || elementCount.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Runtime disjoint raw pointer region '{expression.GetText()}' requires integer start and count operands.");
            }

            var regionStart = EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    pointer,
                    elementType,
                    startIndex,
                    ConstantIndex: null,
                    StarkTypeSymbols.RawPointer(elementType, pointer.Type.IsMutablePointer),
                $"{rootName}[{startExpression.GetText()}]"),
                "range_ptr");
            if (regionStart is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Runtime disjoint raw pointer region '{expression.GetText()}' could not materialize its start pointer.");
            }

            var byteStart = CoerceOperand(regionStart, BytePointerType);
            var byteLength = BuildByteLength(elementCount, elementType, expression);
            if (byteStart is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Runtime disjoint raw pointer region '{expression.GetText()}' could not convert its start pointer to a byte pointer.");
            }

            var end = BuildByteRangeEnd(byteStart, byteLength, $"{expression.GetText()}:end");

            range = new MemoryRangeOperand(byteStart, end);
            return true;
        }

        private bool TryBuildPlaceMemoryRange(
            PlaceTarget target,
            ParserRuleContext syntax,
            out MemoryRangeOperand range)
        {
            range = default!;

            var valueType = StarkTypeSymbols.BorrowReturnValueType(target.Type);
            if (!TryGetMemoryRangeLayout(valueType, syntax, out var layout))
            {
                return false;
            }

            var address = BuildAddress(target);
            if (address is null)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint operand '{syntax.GetText()}' is not addressable.");
            }

            var start = CoerceOperand(address, BytePointerType);
            if (start is null)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint operand '{syntax.GetText()}' could not convert its address to a byte pointer.");
            }

            var byteLength = new MidLevelIrIntegerConstantOperand(layout.SizeBytes, I64Type);
            var end = BuildByteRangeEnd(start, byteLength, $"{syntax.GetText()}:end");

            range = new MemoryRangeOperand(start, end);
            return true;
        }

        private bool TryBuildRawPointerMemoryRange(
            MidLevelIrOperand pointer,
            ParserRuleContext syntax,
            out MemoryRangeOperand range)
        {
            range = default!;

            if (pointer.Type.ElementType is not { } elementType)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint raw pointer operand '{syntax.GetText()}' requires a concrete element type.");
            }

            if (!TryGetMemoryRangeLayout(elementType, syntax, out var layout))
            {
                return false;
            }

            var start = CoerceOperand(pointer, BytePointerType);
            if (start is null)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint raw pointer operand '{syntax.GetText()}' could not convert to a byte pointer.");
            }

            var byteLength = new MidLevelIrIntegerConstantOperand(layout.SizeBytes, I64Type);
            var end = BuildByteRangeEnd(start, byteLength, $"{pointer.Text}:end");

            range = new MemoryRangeOperand(start, end);
            return true;
        }

        private bool TryBuildViewMemoryRange(
            MidLevelIrOperand view,
            StarkTypeSymbol elementType,
            ParserRuleContext syntax,
            out MemoryRangeOperand range)
        {
            range = default!;

            var dataPointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    view,
                    0,
                    IndexedElementOperationFamily.ViewComponent,
                    AddressType(elementType, view.Type.IsMutableView),
                    $"{view.Text}:data"),
                "range_data");
            var elementCount = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    view,
                    1,
                    IndexedElementOperationFamily.ViewComponent,
                    I64Type,
                    $"{view.Text}:len"),
                "range_len");
            if (dataPointer is null || elementCount is null)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint view operand '{syntax.GetText()}' could not expose data pointer and length.");
            }

            var start = CoerceOperand(dataPointer, BytePointerType);
            var byteLength = BuildByteLength(elementCount, elementType, syntax);
            if (start is null)
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint view operand '{syntax.GetText()}' could not convert its data pointer to a byte pointer.");
            }

            var end = BuildByteRangeEnd(start, byteLength, $"{view.Text}:end");

            range = new MemoryRangeOperand(start, end);
            return true;
        }

        private MidLevelIrOperand BuildByteLength(
            MidLevelIrOperand elementCount,
            StarkTypeSymbol elementType,
            ParserRuleContext syntax)
        {
            if (!TryGetMemoryRangeLayout(elementType, syntax, out var elementLayout))
            {
                throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint operand '{syntax.GetText()}' has no concrete layout for '{elementType.DisplayName}'.");
            }

            if (elementLayout.SizeBytes == 1)
            {
                return CoerceOperand(elementCount, I64Type)
                    ?? throw LoweringInvariantViolation(
                        syntax,
                        $"Runtime disjoint element count '{elementCount.Text}' could not be converted to i64.");
            }

            var byteCount = new MidLevelIrIntegerConstantOperand(elementLayout.SizeBytes, I64Type);
            var coercedCount = CoerceOperand(elementCount, I64Type)
                ?? throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint element count '{elementCount.Text}' could not be converted to i64.");
            var result = EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Multiply,
                    coercedCount,
                    byteCount,
                    I64Type,
                    $"{elementCount.Text} * {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}"),
                "range_bytes");
            return result
                ?? throw LoweringInvariantViolation(
                    syntax,
                    $"Runtime disjoint byte length for '{syntax.GetText()}' could not be materialized.");
        }

        private MidLevelIrOperand BuildByteRangeEnd(
            MidLevelIrOperand start,
            MidLevelIrOperand byteLength,
            string text)
        {
            return EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    start,
                    ByteType,
                    byteLength,
                    null,
                    BytePointerType,
                    text),
                "range_end")
                ?? throw LoweringInvariantViolation(
                    null,
                    $"Runtime disjoint byte range end '{text}' could not be materialized.");
        }

        private MidLevelIrOperand EmitRangeDisjointComparison(
            MemoryRangeOperand left,
            MemoryRangeOperand right,
            string text)
        {
            var leftBeforeRight = EmitPointerComparison(
                MidLevelIrBinaryOperator.LessThanOrEqual,
                left.End,
                right.Start,
                $"{left.End.Text} <= {right.Start.Text}");
            var rightBeforeLeft = EmitPointerComparison(
                MidLevelIrBinaryOperator.LessThanOrEqual,
                right.End,
                left.Start,
                $"{right.End.Text} <= {left.Start.Text}");
            return EmitBooleanBinary(
                MidLevelIrBinaryOperator.BitwiseOr,
                leftBeforeRight,
                rightBeforeLeft,
                text,
                "disjoint_pair");
        }

        private MidLevelIrOperand EmitPointerComparison(
            MidLevelIrBinaryOperator comparison,
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string text)
        {
            return EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    comparison,
                    left,
                    right,
                    StarkTypeSymbols.Bool,
                    text),
                "ptrcmp");
        }

        private MidLevelIrOperand EmitBooleanBinary(
            MidLevelIrBinaryOperator operatorKind,
            MidLevelIrOperand left,
            MidLevelIrOperand right,
            string text,
            string hint)
        {
            return EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    operatorKind,
                    CoerceOperand(left, StarkTypeSymbols.Bool)
                        ?? throw LoweringInvariantViolation(null, $"Boolean operand '{left.Text}' could not be converted to bool."),
                    CoerceOperand(right, StarkTypeSymbols.Bool)
                        ?? throw LoweringInvariantViolation(null, $"Boolean operand '{right.Text}' could not be converted to bool."),
                    StarkTypeSymbols.Bool,
                    text),
                hint);
        }

        private bool TryGetMemoryRangeLayout(
            StarkTypeSymbol type,
            ParserRuleContext syntax,
            out ConcreteTypeLayout layout)
        {
            if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                    type,
                    _typeModel.NamedTypes,
                    _enumLayoutModel.Layouts) is { } resolvedLayout)
            {
                layout = resolvedLayout;
                return true;
            }

            throw LoweringInvariantViolation(
                syntax,
                $"Runtime disjoint operand '{syntax.GetText()}' has no concrete layout for '{type.DisplayName}'.");
        }

        private static bool IsPointerBackedContractRangeType(StarkTypeSymbol type)
        {
            return type.Kind is not (StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                && (type.BorrowKind != StarkBorrowKind.None
                    || type.InitializationKind != StarkInitializationKind.None);
        }

        private static bool TryGetContiguousViewElementType(StarkTypeSymbol type, out StarkTypeSymbol elementType)
        {
            var valueType = StarkTypeSymbols.BorrowReturnValueType(type);
            switch (valueType.Kind)
            {
                case StarkTypeKind.Slice when valueType.ElementType is not null:
                    elementType = valueType.ElementType;
                    return true;
                case StarkTypeKind.Ascii:
                    elementType = ByteType;
                    return true;
                case StarkTypeKind.Unicode:
                    elementType = StarkTypeSymbols.Integer(32);
                    return true;
                default:
                    elementType = StarkTypeSymbols.Error;
                    return false;
            }
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
            var loopBehavior = whileStatement.loopBehavior().GetText();
            var loopContracts = GetLoopContractNames(whileStatement.loopContract());
            var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);
            var conditionBlock = CreateBlock($"while_{loopBehavior}_cond");
            var bodyBlock = CreateBlock("while_body");
            var exitBlock = CreateBlock("while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: whileStatement.expression().GetText(),
                Condition: LowerExpressionToOperand(whileStatement.expression(), StarkTypeSymbols.Bool));

            _loops.Push(new LoopTargets(
                conditionBlock.Id,
                exitBlock.Id,
                _scopes.Count,
                loopBehavior,
                loopContracts,
                loopAccessGroups));
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                    {
                        _activeLoopAccessGroups.Push(loopAccessGroup);
                    }
                }

                LowerStatement(whileStatement.statement());
            }
            finally
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    for (var index = 0; index < loopAccessGroups.Count; index++)
                    {
                        _activeLoopAccessGroups.Pop();
                    }
                }

                _breakTargets.Pop();
                _loops.Pop();
            }
            EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);

            CurrentBlock = exitBlock;
        }

        private void LowerFor(StarkParser.ForStatementContext forStatement)
        {
            _scopes.Push(new ScopeFrame());
            _compileTimeConstantState.PushScope();

            try
            {
                if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
                {
                    var declaredType = TryResolveLocalDeclarationType(TemplateLocalDeclarationFacts.ForVariableKind, localForVariableDeclaration, out var publishedType)
                        ? publishedType
                        : ResolveTypeWithGenericSubstitution(localForVariableDeclaration.type_(), CurrentModuleName);
                    var storageClass = localForVariableDeclaration.storageClass().GetText();

                    foreach (var declarator in localForVariableDeclaration.variableDeclarators().variableDeclarator())
                    {
                        var sourceName = declarator.Identifier().GetText();
                        var isMutable = localForVariableDeclaration.MUT() is not null;
                        var localName = DeclareLocal(sourceName, declaredType, storageClass, isMutable, isConstant: false);
                        Emit(MidLevelIrStatementKind.StorageLive, localName, localName, declaredType);
                        InitializeRuntimeDropState(localName, declaredType, isActive: false);
                        if (declarator.variableInitializer() is { } initializer)
                        {
                            var initializerHasConstProvenance = LowerVariableInitializer(localName, declaredType, initializer);
                            if (!isMutable && initializerHasConstProvenance)
                            {
                                MarkLocalHasConstProvenance(localName);
                            }

                            SetRuntimeDropState(localName, isActive: true);
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

                var loopBehavior = forStatement.loopBehavior().GetText();
                var conditionBlock = CreateBlock($"for_{loopBehavior}_cond");
                var bodyBlock = CreateBlock("for_body");
                var iteratorBlock = CreateBlock("for_iter");
                var exitBlock = CreateBlock("for_exit");
                var loopContracts = GetLoopContractNames(forStatement.loopContract());
                var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);

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

                _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id, _scopes.Count, null, null, null));
                _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
                CurrentBlock = bodyBlock;
                try
                {
                    if (loopAccessGroups is { Count: > 0 })
                    {
                        foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                        {
                            _activeLoopAccessGroups.Push(loopAccessGroup);
                        }
                    }

                    LowerStatement(forStatement.statement());
                }
                finally
                {
                    if (loopAccessGroups is { Count: > 0 })
                    {
                        for (var index = 0; index < loopAccessGroups.Count; index++)
                        {
                            _activeLoopAccessGroups.Pop();
                        }
                    }

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

                EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);
                CurrentBlock = exitBlock;
            }
            finally
            {
                _compileTimeConstantState.PopScope();
                var scope = _scopes.Pop();
                EmitStorageDead(scope);
                RestoreScopedNameAliases(scope);
            }
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

        private MidLevelIrOperand LowerExpressionToRequiredOperand(
            StarkParser.ExpressionContext expression,
            StarkTypeSymbol? expectedType,
            string reason)
        {
            return RequireLoweredOperand(expression, LowerExpressionToOperand(expression, expectedType), reason);
        }

        private MidLevelIrOperand RequireLoweredOperand(
            ParserRuleContext? context,
            MidLevelIrOperand? operand,
            string reason)
        {
            return operand ?? throw LoweringInvariantViolation(context, reason);
        }

        private bool TryLowerCompileTimeIntegerExpression(
            ParserRuleContext expression,
            StarkTypeSymbol? expectedType,
            out MidLevelIrOperand? operand)
        {
            operand = null;
            if (!_compileTimeEvaluator.TryEvaluateExpressionNode(
                    expression,
                    CurrentModuleName,
                    _compileTimeConstantState,
                    activeCalls: null,
                    out var constant)
                || constant.Kind != CompileTimeConstantKind.Integer)
            {
                return false;
            }

            if (expectedType is not null
                && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
            {
                constant = coerced;
            }

            operand = CreateCompileTimeOperand(constant);
            return true;
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
                throw LoweringInvariantViolation(
                    expression,
                    "Assignment expression could not be lowered as a conditional value or assignment.");
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
                throw LoweringInvariantViolation(
                    expression,
                    "Conditional expression must have exactly two branch expressions.");
            }

            var condition = LowerLogicalOrExpression(expression.logicalOrExpression(), StarkTypeSymbols.Bool);
            if (condition is null)
            {
                throw LoweringInvariantViolation(
                    expression.logicalOrExpression(),
                    "Conditional expression condition did not lower to a value.");
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
                throw LoweringInvariantViolation(
                    expression.expression(0),
                    "Conditional true branch did not lower to a value.");
            }

            CurrentBlock = elseBlock;
            var falseValue = LowerExpressionToOperand(expression.expression(1), expectedType);
            var falseBlock = CurrentBlock;
            if (falseValue is null)
            {
                throw LoweringInvariantViolation(
                    expression.expression(1),
                    "Conditional false branch did not lower to a value.");
            }

            var resultType = expectedType ?? FindCommonType(trueValue.Type, falseValue.Type);
            if (resultType.Kind == StarkTypeKind.Error)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Conditional expression branches '{trueValue.Type.DisplayName}' and '{falseValue.Type.DisplayName}' have no common result type.");
            }

            var resultHasConstProvenance = OperandHasConstProvenance(trueValue)
                && OperandHasConstProvenance(falseValue);
            var result = CreateTemporaryLocal(resultType, "cond", resultHasConstProvenance);

            CurrentBlock = trueBlock;
            var coercedTrue = CoerceOperand(trueValue, resultType);
            if (coercedTrue is null)
            {
                throw LoweringInvariantViolation(
                    expression.expression(0),
                    $"Conditional true branch could not coerce to '{resultType.DisplayName}'.");
            }

            EmitOperandAssignment(result, coercedTrue, expression.expression(0).GetText());
            EnsureGoto(joinBlock.Id);

            CurrentBlock = falseBlock;
            var coercedFalse = CoerceOperand(falseValue, resultType);
            if (coercedFalse is null)
            {
                throw LoweringInvariantViolation(
                    expression.expression(1),
                    $"Conditional false branch could not coerce to '{resultType.DisplayName}'.");
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
            if (operators.Count > 0
                && TryLowerCompileTimeIntegerExpression(expression, expectedType, out var constant))
            {
                return constant;
            }

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

            if (TryLowerCompileTimeIntegerExpression(expression, expectedType, out var constant))
            {
                return constant;
            }

            return LowerBinaryChain(
                operands,
                operators,
                item => LowerMultiplicativeExpression(item, expectedType: null),
                MapBinaryOperator,
                requireInteger: false,
                expectedType,
                textBuildContext: expression);
        }

        private MidLevelIrOperand? LowerMultiplicativeExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var operands = expression.unaryExpression();
            var operators = ExtractOperators<StarkParser.UnaryExpressionContext>(expression);
            if (operators.Count > 0
                && TryLowerCompileTimeIntegerExpression(expression, expectedType, out var constant))
            {
                return constant;
            }

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

            if (op == "init")
            {
                var initializationView = LowerUnaryExpression(expression.unaryExpression(), expectedType: null);
                return expectedType is null ? initializationView : CoerceOperand(initializationView, expectedType);
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
                _ => throw LoweringInvariantViolation(expression, $"Unsupported unary operator '{op}'.")
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
                throw LoweringInvariantViolation(
                    operandExpression,
                    "Address-of expression requires a resolved addressable target.");
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? LowerDereferenceUnary(StarkParser.UnaryExpressionContext expression, MidLevelIrOperand operand)
        {
            if (operand.Type.Kind == StarkTypeKind.RawPointer
                && UsesFrozenProjectionSemantics(operand))
            {
                operand = CoerceOperand(operand, StarkTypeSymbols.FreezeReachableView(operand.Type)) ?? operand;
            }

            if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Dereference expression requires a raw pointer operand, but found '{operand.Type.DisplayName}'.");
            }

            var resultType = operand.Type.ElementType.AccessKind == StarkAccessKind.Frozen
                ? StarkTypeSymbols.FreezeReachableView(operand.Type.ElementType)
                : operand.Type.ElementType;

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    operand,
                    resultType,
                    expression.GetText()),
                "load");
        }

        private MidLevelIrOperand? LowerPowerExpression(StarkParser.PowerExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (expression.unaryExpression() is not { } rightExpression)
            {
                return LowerPostfixExpression(expression.postfixExpression(), expectedType);
            }

            if (TryLowerCompileTimeIntegerExpression(expression, expectedType, out var constant))
            {
                return constant;
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
                throw LoweringInvariantViolation(
                    expression,
                    $"Power expression requires numeric operands, but found '{left.Type.DisplayName}' and '{right.Type.DisplayName}'.");
            }

            var coercedLeft = CoerceOperand(left, resultType);
            var coercedRight = CoerceOperand(right, resultType);
            if (coercedLeft is null || coercedRight is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Power expression operands could not coerce to '{resultType.DisplayName}'.");
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
                throw LoweringInvariantViolation(
                    expression,
                    "Power expression did not materialize a computed value.");
            }

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerPostfixExpression(StarkParser.PostfixExpressionContext expression, StarkTypeSymbol? expectedType)
        {
            if (TryLowerRawSliceConstruction(expression, out var rawSlice))
            {
                return expectedType is null ? rawSlice : CoerceOperand(rawSlice, expectedType);
            }

            if (expression.postfixPart().Length == 0)
            {
                return LowerPrimaryExpression(expression.primaryExpression(), expectedType);
            }

            if (TryLowerDynamicStorageReserveExpression(expression, out var reserve))
            {
                if (reserve.Type.Kind == StarkTypeKind.Void)
                {
                    throw LoweringInvariantViolation(expression, "Dynamic storage Reserve does not produce a value.");
                }

                var reserved = EmitRequiredTemporary(reserve, "dynamic_reserve");
                return expectedType is null ? reserved : CoerceOperand(reserved, expectedType);
            }

            if (TryLowerDynamicStorageMoveLastExpression(expression, out var moveLast))
            {
                var moved = EmitRequiredTemporary(moveLast, "dynamic_move");
                return expectedType is null ? moved : CoerceOperand(moved, expectedType);
            }

            if (TryLowerDynamicStorageMoveAtExpression(expression, out var moveAt))
            {
                var moved = EmitRequiredTemporary(moveAt, "dynamic_move");
                return expectedType is null ? moved : CoerceOperand(moved, expectedType);
            }

            if (TryLowerCallExpression(expression, out var call))
            {
                if (call.Type.Kind == StarkTypeKind.Void)
                {
                    throw LoweringInvariantViolation(expression, "Void direct calls cannot be lowered as value expressions.");
                }

                var callResult = EmitRequiredTemporary(call, "call");

                if (call.SourceReturnType is { } sourceReturnType
                    && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
                {
                    if (expectedType is not null
                        && expectedType.BorrowKind != StarkBorrowKind.None
                        && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
                    {
                        return callResult;
                    }

                    var valueType = GetPointerBackedBorrowLoadType(sourceReturnType);
                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            callResult,
                            valueType,
                            $"{callResult.Text}:load"),
                        "load");
                    return loaded is null
                        ? throw LoweringInvariantViolation(expression, "Pointer-backed direct call return could not be loaded.")
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
                    throw LoweringInvariantViolation(expression, "Void indirect calls cannot be lowered as value expressions.");
                }

                var callResult = EmitRequiredTemporary(indirectCall, "call");

                if (indirectCall.SourceReturnType is { } sourceReturnType
                    && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
                {
                    if (expectedType is not null
                        && expectedType.BorrowKind != StarkBorrowKind.None
                        && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
                    {
                        return callResult;
                    }

                    var valueType = GetPointerBackedBorrowLoadType(sourceReturnType);
                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            callResult,
                            valueType,
                            $"{callResult.Text}:load"),
                        "load");
                    return loaded is null
                        ? throw LoweringInvariantViolation(expression, "Pointer-backed indirect call return could not be loaded.")
                        : expectedType is null
                            ? loaded
                            : CoerceOperand(loaded, expectedType);
                }

                return expectedType is null ? callResult : CoerceOperand(callResult, expectedType);
            }

            if (TryLowerClosureCallExpression(expression, out var closureCall))
            {
                if (closureCall.Type.Kind == StarkTypeKind.Void)
                {
                    throw LoweringInvariantViolation(expression, "Void closure calls cannot be lowered as value expressions.");
                }

                var callResult = EmitRequiredTemporary(closureCall, "call");

                if (closureCall.SourceReturnType is { } sourceReturnType
                    && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
                {
                    if (expectedType is not null
                        && expectedType.BorrowKind != StarkBorrowKind.None
                        && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
                    {
                        return callResult;
                    }

                    var valueType = GetPointerBackedBorrowLoadType(sourceReturnType);
                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            callResult,
                            valueType,
                            $"{callResult.Text}:load"),
                        "load");
                    return loaded is null
                        ? throw LoweringInvariantViolation(expression, "Pointer-backed closure call return could not be loaded.")
                        : expectedType is null
                            ? loaded
                            : CoerceOperand(loaded, expectedType);
                }

                return expectedType is null ? callResult : CoerceOperand(callResult, expectedType);
            }

            if (!TryLowerPostfixOperand(expression, expectedType, out var current))
            {
                return null;
            }

            return expectedType is null ? current : CoerceOperand(current, expectedType);
        }

        private bool TryLowerRawSliceConstruction(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrOperand? result)
        {
            if (!TryLowerRawSliceConstructionPrefix(expression, out result, out var firstUnhandledPostfixIndex))
            {
                return false;
            }

            if (firstUnhandledPostfixIndex != expression.postfixPart().Length)
            {
                result = null;
                return false;
            }

            return true;
        }

        private bool TryLowerRawSliceConstructionPrefix(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrOperand? result,
            out int firstUnhandledPostfixIndex)
        {
            result = null;
            firstUnhandledPostfixIndex = 0;
            if (!string.Equals(expression.primaryExpression().Identifier()?.GetText(), "slice", StringComparison.Ordinal)
                || expression.postfixPart().Length == 0
                || expression.postfixPart()[0] is not { } callPart
                || callPart.argumentList() is not { } argumentList)
            {
                return false;
            }

            firstUnhandledPostfixIndex = 1;
            var arguments = argumentList.argument();
            if (arguments.Length != 2)
            {
                throw LoweringInvariantViolation(argumentList, "Raw slice construction requires pointer and count operands.");
            }

            var pointer = LowerExpressionToOperand(arguments[0].expression());
            var length = LowerExpressionToOperand(arguments[1].expression());
            if (pointer is null
                || length is null
                || length.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(expression, "Raw slice construction requires a raw pointer and integer count.");
            }

            var hasFrozenSliceProvenance = UsesFrozenProjectionSemantics(pointer);
            if (pointer.Type.Kind == StarkTypeKind.RawPointer
                && hasFrozenSliceProvenance)
            {
                pointer = CoerceOperand(pointer, StarkTypeSymbols.FreezeReachableView(pointer.Type)) ?? pointer;
            }

            if (pointer.Type.Kind != StarkTypeKind.RawPointer
                || pointer.Type.ElementType is not { } elementType)
            {
                throw LoweringInvariantViolation(expression, "Raw slice construction requires a raw pointer and integer count.");
            }

            hasFrozenSliceProvenance |= elementType.AccessKind == StarkAccessKind.Frozen;
            var sliceElementType = hasFrozenSliceProvenance
                ? StarkTypeSymbols.WithQualifiers(elementType, accessKind: StarkAccessKind.None, isMutableView: false)
                : elementType;
            var sliceType = StarkTypeSymbols.ApplyQualifiers(
                StarkTypeSymbols.Slice(sliceElementType),
                isMutableView: pointer.Type.IsMutablePointer);
            if (hasFrozenSliceProvenance)
            {
                sliceType = StarkTypeSymbols.FreezeReachableView(sliceType);
            }

            result = EmitTemporary(
                new MidLevelIrMakeSliceFromPointerRValue(
                    pointer,
                    CoerceOperand(length, I64Type) ?? length,
                    sliceType,
                    $"{expression.primaryExpression().GetText()}{callPart.GetText()}"),
                "slice");
            return true;
        }

        private bool TryLowerPostfixOperand(
            StarkParser.PostfixExpressionContext expression,
            StarkTypeSymbol? expectedType,
            out MidLevelIrOperand? result)
        {
            result = null;

            var firstUnhandledPostfixIndex = 0;
            MidLevelIrOperand? currentValue;
            string? currentName;
            if (TryLowerRawSliceConstructionPrefix(expression, out var rawSlice, out firstUnhandledPostfixIndex))
            {
                currentValue = rawSlice;
                currentName = null;
                if (currentValue is null)
                {
                    return false;
                }
            }
            else if (!TryInitializePostfixState(expression.primaryExpression(), out currentValue, out currentName))
            {
                return false;
            }

            PlaceTarget? currentPlace = currentValue is null ? null : CreateRootPlaceTarget(currentValue);
            for (var index = firstUnhandledPostfixIndex; index < expression.postfixPart().Length; index++)
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
                        throw LoweringInvariantViolation(argumentList, "Void direct calls cannot be lowered as value expressions.");
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
                        currentPlace = currentPlace is not null && TryAppendIndexPlaceTarget(currentPlace, expressionList, out var indexedPlace)
                            ? indexedPlace
                            : null;
                        currentValue = currentPlace is { UsesAddressModel: true }
                            ? ReadPlace(currentPlace)
                            : LowerIndexAccess(currentValue, expressionList);
                        if (currentValue is null)
                        {
                            return false;
                        }
                    }
                    else if (currentValue.Type.Kind is not StarkTypeKind.Ascii and not StarkTypeKind.Unicode)
                    {
                        throw LoweringInvariantViolation(postfixPart, "Index access requires at least one index expression.");
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
                        throw LoweringInvariantViolation(
                            memberArguments,
                            $"Member call '{memberName}' could not be resolved for receiver type '{currentValue.Type.DisplayName}'.");
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        throw LoweringInvariantViolation(memberArguments, "Void member calls cannot be lowered as value expressions.");
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
                    currentValue = currentPlace is { UsesAddressModel: true }
                        ? ReadPlace(currentPlace)
                        : TryLowerPublishedFieldAccess(currentValue, postfixPart, out var publishedFieldAccess)
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

                currentValue = ResolveNamedOperand(currentName, expectedType, expression);
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

            if (TryLowerBoundEnumValue(expression, out currentValue))
            {
                currentName = null;
                return currentValue is not null;
            }

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
                    currentName = genericEnumCaseReference.GetText();
                    return true;
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
                return ResolveNamedOperand(identifier.GetText(), expectedType, expression);
            }

            if (expression.lambdaExpression() is { } lambdaExpression)
            {
                return LowerLambdaExpression(lambdaExpression, expectedType);
            }

            if (expression.enumConstructorExpression() is { } enumConstructorExpression)
            {
                return LowerEnumConstructorExpression(enumConstructorExpression, expectedType);
            }

            if (TryLowerBoundEnumValue(expression, out var boundEnumValue))
            {
                return boundEnumValue is null || expectedType is null ? boundEnumValue : CoerceOperand(boundEnumValue, expectedType);
            }

            if (TryLowerPublishedEnumValue(expression, out var publishedEnumValue))
            {
                return publishedEnumValue is null || expectedType is null ? publishedEnumValue : CoerceOperand(publishedEnumValue, expectedType);
            }

            if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
            {
                return !TryBuildGenericEnumCaseName(genericEnumCaseReference, out var genericEnumCaseName)
                    ? null
                    : ResolveNamedOperand(genericEnumCaseName, expectedType, expression);
            }

            if (expression.qualifiedName() is { } qualifiedName)
            {
                return ResolveNamedOperand(qualifiedName.GetText(), expectedType, expression);
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
            if (expectedType?.Kind is not (StarkTypeKind.FunctionPointer or StarkTypeKind.Closure))
            {
                throw LoweringInvariantViolation(expression, "Lambda expressions require an explicit function-pointer or closure target type during MIR lowering.");
            }

            var line = expression.Start.Line;
            var column = expression.Start.Column + 1;
            if (expectedType.Kind == StarkTypeKind.FunctionPointer)
            {
                var lambda = _typeModel.Lambdas.LastOrDefault(lambda =>
                    lambda.Location.Line == line
                    && lambda.Location.Column == column);
                if (lambda is null)
                {
                    throw LoweringInvariantViolation(expression, "No type-checked non-capturing lambda record was found for MIR lowering.");
                }

                return new MidLevelIrFunctionAddressOperand(lambda.FunctionName, expectedType);
            }

            var closureLambda = _typeModel.ClosureLambdas.LastOrDefault(lambda =>
                lambda.Location.Line == line
                && lambda.Location.Column == column);
            if (closureLambda is null)
            {
                throw LoweringInvariantViolation(expression, "No type-checked closure lambda record was found for MIR lowering.");
            }

            if (!closureLambda.HasCaptures)
            {
                return new MidLevelIrClosureValueOperand(closureLambda.FunctionName, expectedType);
            }

            return LowerCapturingClosureValue(expression, closureLambda, expectedType);
        }

        private MidLevelIrOperand? LowerCapturingClosureValue(
            StarkParser.LambdaExpressionContext expression,
            ClosureLambdaTypingRecord closureLambda,
            StarkTypeSymbol expectedType)
        {
            if (closureLambda.EnvironmentTypeName is not { Length: > 0 } environmentTypeName)
            {
                throw LoweringInvariantViolation(expression, "A capturing closure lambda has no environment type name.");
            }

            var environmentType = StarkTypeSymbols.Named(environmentTypeName);
            var environmentLocalName = AllocateTemporaryName("closure_env");
            var environmentStorageClass = closureLambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap
                ? "heap"
                : "stack";
            RegisterLocal(environmentLocalName, environmentType, environmentStorageClass, isMutable: true, isConstant: false);
            if (environmentStorageClass == "stack")
            {
                TrackDeclaredLocal(environmentLocalName, environmentType);
            }

            Emit(MidLevelIrStatementKind.StorageLive, environmentLocalName, environmentLocalName, environmentType);

            var environmentLocal = new MidLevelIrLocalOperand(environmentLocalName, environmentType);
            foreach (var capture in closureLambda.CaptureFields)
            {
                InitializeClosureEnvironmentField(environmentLocal, environmentType, capture, expression);
            }

            var environmentAddress = CreateAddressOfLocal(environmentLocalName, environmentType);
            if (environmentAddress is null)
            {
                throw LoweringInvariantViolation(expression, $"Could not take the address of closure environment '{environmentLocalName}'.");
            }

            var erasedEnvironmentAddress = CoerceOperand(environmentAddress, closureLambda.EnvironmentParameterType)
                ?? environmentAddress;
            var invokePointerType = CallableValueFacts.BuildClosureInvokeFunctionPointerType(expectedType);
            var withInvoke = EmitTemporary(
                new MidLevelIrInsertIndexRValue(
                    new MidLevelIrZeroInitializerOperand(expectedType),
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    new MidLevelIrFunctionAddressOperand(closureLambda.FunctionName, invokePointerType),
                    expectedType,
                    $"closure<{closureLambda.FunctionName}>.invoke"),
                "closure");
            if (withInvoke is null)
            {
                throw LoweringInvariantViolation(expression, $"Could not materialize closure invoke pointer for '{closureLambda.FunctionName}'.");
            }

            var withEnvironment = EmitTemporary(
                new MidLevelIrInsertIndexRValue(
                    withInvoke,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    erasedEnvironmentAddress,
                    expectedType,
                    $"closure<{closureLambda.FunctionName}>.env"),
                "closure");
            if (withEnvironment is null)
            {
                throw LoweringInvariantViolation(expression, $"Could not materialize closure environment pointer for '{closureLambda.FunctionName}'.");
            }

            if (closureLambda.ClosureType.ClosureStorageKind != StarkClosureStorageKind.Heap)
            {
                return withEnvironment;
            }

            return EmitTemporary(
                new MidLevelIrInsertIndexRValue(
                    withEnvironment,
                    ElementIndex: 2,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    new MidLevelIrFunctionAddressOperand(
                        CallableValueFacts.BuildClosureDropFunctionName(closureLambda.FunctionName),
                        CallableValueFacts.BuildClosureDropFunctionPointerType()),
                    expectedType,
                    $"closure<{closureLambda.FunctionName}>.drop"),
                "closure");
        }

        private void InitializeClosureEnvironmentField(
            MidLevelIrLocalOperand environmentLocal,
            StarkTypeSymbol environmentType,
            ClosureCaptureFieldSymbol capture,
            StarkParser.LambdaExpressionContext expression)
        {
            if (!TryCreateEnvironmentFieldPlace(environmentLocal, environmentType, capture, out var fieldTarget))
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Could not resolve environment field '{capture.FieldName}' for captured variable '{capture.Name}'.");
            }

            var value = capture.StorageKind == ClosureCaptureStorageKind.Address
                ? LowerClosureCaptureSourceAddress(capture, expression)
                : LowerClosureCaptureSourceValue(capture, expression);
            var coercedValue = CoerceOperand(value, capture.FieldType) ?? value;
            EmitAssignment(BuildAssignment(fieldTarget, coercedValue, $"{environmentLocal.Name}.{capture.FieldName} = {capture.Name}") with
            {
                WriteKind = MemoryWriteKind.Initialization
            });
        }

        private MidLevelIrOperand LowerClosureCaptureSourceValue(
            ClosureCaptureFieldSymbol capture,
            StarkParser.LambdaExpressionContext expression)
        {
            if (string.Equals(capture.Mode, "addr", StringComparison.Ordinal))
            {
                return LowerClosureCaptureSourceAddress(capture, expression);
            }

            var value = ResolveNamedOperand(capture.Name, capture.SourceType, expression);
            if (value is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Could not resolve captured variable '{capture.Name}' for closure environment initialization.");
            }

            return CoerceOperand(value, capture.FieldType) ?? value;
        }

        private MidLevelIrOperand LowerClosureCaptureSourceAddress(
            ClosureCaptureFieldSymbol capture,
            StarkParser.LambdaExpressionContext expression)
        {
            var source = TryResolveNamedValueOperand(capture.Name);
            if (source is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Could not resolve captured variable '{capture.Name}' for closure environment address capture.");
            }

            var sourcePlace = CreateRootPlaceTarget(source);
            if (sourcePlace is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Captured variable '{capture.Name}' cannot form an addressable closure environment field.");
            }

            var address = BuildAddress(sourcePlace);
            if (address is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Could not take the address of captured variable '{capture.Name}'.");
            }

            return address;
        }

        private bool TryCreateEnvironmentFieldPlace(
            MidLevelIrLocalOperand environmentLocal,
            StarkTypeSymbol environmentType,
            ClosureCaptureFieldSymbol capture,
            out PlaceTarget target)
        {
            target = default!;
            if (!TryResolveField(environmentType, capture.FieldName, out _, out var fieldIndex))
            {
                return false;
            }

            target = new PlaceTarget(
                environmentLocal.Name,
                RootAddress: null,
                RootValue: null,
                RootType: environmentType,
                capture.FieldType,
                [
                    new PlacePathSegment(
                        PlacePathKind.Field,
                        capture.FieldName,
                        fieldIndex,
                        IndexOperand: null,
                        ParentType: environmentType,
                        SegmentType: capture.FieldType)
                ],
                UsesAddressModel: true,
                IsAddressMutable: true);
            return true;
        }

        private MidLevelIrOperand? LowerTypeLayoutExpression(
            StarkParser.PrimaryExpressionContext expression,
            StarkTypeSymbol? expectedType)
        {
            var hasBoundLayout = TryResolveBoundLayoutQuery(expression, out var boundLayout);
            var targetType = hasBoundLayout
                ? ApplyGenericSubstitution(boundLayout.TargetType)
                : ResolveTypeWithGenericSubstitution(expression.type_(), CurrentModuleName);
            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                targetType,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts);
            if (layout is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Type layout expression requires a concrete runtime layout for '{targetType.DisplayName}'.");
            }

            var queryKind = hasBoundLayout
                ? boundLayout.QueryKind
                : expression.ALIGNOF() is not null
                    ? BoundLayoutQueryKind.AlignOf
                    : BoundLayoutQueryKind.SizeOf;
            var value = queryKind == BoundLayoutQueryKind.AlignOf
                ? layout.AlignmentBytes
                : layout.SizeBytes;
            var resultType = hasBoundLayout
                ? ApplyGenericSubstitution(boundLayout.ResultType)
                : queryKind == BoundLayoutQueryKind.AlignOf
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
            var hasBoundObjectCreation = TryResolveBoundObjectCreation(expression, out var boundObjectCreation);
            TryGetPublishedObjectCreationSummary(expression, out var publishedObjectCreation);
            var createdType = hasBoundObjectCreation
                ? ApplyGenericSubstitution(boundObjectCreation.CreatedType)
                : publishedObjectCreation is not null
                ? ApplyGenericSubstitution(publishedObjectCreation.CreatedType)
                : expression.type_() is { } explicitType
                    ? ResolveTypeWithGenericSubstitution(explicitType, CurrentModuleName)
                    : expectedType;
            if (createdType is null || createdType.Kind == StarkTypeKind.Error)
            {
                throw LoweringInvariantViolation(
                    expression,
                    "Target-typed object creation reached MIR without a lowering target type.");
            }

            if (createdType.Kind == StarkTypeKind.Dynamic)
            {
                return LowerDynamicStorageCreation(expression, createdType, expectedType);
            }

            var constructor = hasBoundObjectCreation
                ? boundObjectCreation.Constructor is null
                    ? null
                    : ApplyGenericSubstitution(boundObjectCreation.Constructor)
                : TryGetMatchedObjectCreationConstructor(expression, out var recordedConstructor)
                    ? recordedConstructor
                    : null;
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            var initializerMembers = expression.objectInitializer() is { } sourceInitializer
                ? new List<ObjectInitializerMemberTypingRecord>(sourceInitializer.memberInitializer().Length)
                : new List<ObjectInitializerMemberTypingRecord>();
            if (constructor is not null)
            {
                var initializedFromConstructor = LowerConstructorObjectCreation(expression, createdType, expression.argumentList(), constructor);
                if (initializedFromConstructor is null)
                {
                    throw LoweringInvariantViolation(
                        expression,
                        $"Object creation for '{createdType.DisplayName}' did not lower its constructor result.");
                }

                current = initializedFromConstructor;
            }
            else if (expression.argumentList() is { } argumentList && argumentList.argument().Length != 0)
            {
                throw LoweringInvariantViolation(
                    expression,
                    "Object creation with arguments reached MIR without a resolved constructor shape.");
            }

            if (expression.objectInitializer() is { } objectInitializer)
            {
                var initialized = LowerObjectInitializer(
                    createdType,
                    current,
                    objectInitializer,
                    hasBoundObjectCreation ? boundObjectCreation.Members : null,
                    publishedObjectCreation?.InitializerMembers,
                    initializerMembers);
                if (initialized is null)
                {
                    throw LoweringInvariantViolation(
                        objectInitializer,
                        $"Object initializer for '{createdType.DisplayName}' did not lower to a value.");
                }

                current = initialized;
            }

            if (createdType.Kind != StarkTypeKind.Named)
            {
                return expectedType is null ? current : CoerceOperand(current, expectedType);
            }

            var constructed = WrapObjectConstruction(
                createdType,
                current,
                constructor,
                initializerMembers,
                expression.objectInitializer() is not null,
                expression.GetText());
            return expectedType is null ? constructed : CoerceOperand(constructed, expectedType);
        }

        private MidLevelIrOperand? LowerDynamicStorageCreation(
            StarkParser.ObjectCreationExpressionContext expression,
            StarkTypeSymbol createdType,
            StarkTypeSymbol? expectedType)
        {
            if (createdType.ElementType is null)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage creation requires an element type.");
            }

            if (expression.objectInitializer() is not null)
            {
                throw LoweringInvariantViolation(expression.objectInitializer(), "Dynamic storage creation does not support object initializers.");
            }

            var arguments = expression.argumentList()?.argument() ?? [];
            if (arguments.Length == 0)
            {
                var empty = new MidLevelIrZeroInitializerOperand(createdType);
                return expectedType is null ? empty : CoerceOperand(empty, expectedType);
            }

            if (arguments.Length != 1)
            {
                throw LoweringInvariantViolation(expression.argumentList(), "Dynamic storage creation expects zero arguments or one capacity argument.");
            }

            var capacity = LowerExpressionToOperand(arguments[0].expression());
            if (capacity is null || capacity.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(arguments[0].expression(), "Dynamic storage capacity requires an integer operand.");
            }

            capacity = CoerceOperand(capacity, NonNegativeI64Type) ?? capacity;
            if (capacity is MidLevelIrIntegerConstantOperand { Value.Sign: 0 })
            {
                var empty = new MidLevelIrZeroInitializerOperand(createdType);
                return expectedType is null ? empty : CoerceOperand(empty, expectedType);
            }

            var allocation = EmitTemporary(
                new MidLevelIrDynamicStorageAllocationRValue(
                    capacity,
                    createdType,
                    expression.GetText()),
                "dynamic");
            if (allocation is null)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage allocation did not materialize a MIR operand.");
            }

            return expectedType is null ? allocation : CoerceOperand(allocation, expectedType);
        }

        private MidLevelIrOperand? LowerObjectInitializer(StarkTypeSymbol targetType, StarkParser.ObjectInitializerContext objectInitializer)
        {
            var initializerMembers = new List<ObjectInitializerMemberTypingRecord>(objectInitializer.memberInitializer().Length);
            var initialized = LowerObjectInitializer(
                targetType,
                new MidLevelIrZeroInitializerOperand(targetType),
                objectInitializer,
                boundInitializerMembers: null,
                publishedInitializerMembers: null,
                initializerMembers);
            if (initialized is null)
            {
                return null;
            }

            return WrapObjectConstruction(
                targetType,
                initialized,
                constructor: null,
                initializerMembers,
                hasInitializer: true,
                objectInitializer.GetText());
        }

        private MidLevelIrOperand? LowerObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            StarkParser.ObjectInitializerContext objectInitializer,
            IReadOnlyList<ObjectInitializerMemberTypingRecord>? boundInitializerMembers,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedInitializerMembers,
            List<ObjectInitializerMemberTypingRecord>? resolvedInitializerMembers = null)
        {
            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null)
            {
                throw LoweringInvariantViolation(
                    objectInitializer,
                    $"Object initializer requires a named target type, but lowering received '{targetType.DisplayName}'.");
            }

            _typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType);
            var current = seed;

            for (var index = 0; index < objectInitializer.memberInitializer().Length; index++)
            {
                var initializer = objectInitializer.memberInitializer(index);
                var fieldName = initializer.Identifier().GetText();
                var fieldType = StarkTypeSymbols.Error;
                var fieldIndex = -1;

                if (boundInitializerMembers is { Count: > 0 } && index < boundInitializerMembers.Count)
                {
                    var boundMember = boundInitializerMembers[index];
                    fieldName = boundMember.FieldName;
                    fieldIndex = boundMember.FieldIndex;
                    fieldType = ApplyGenericSubstitution(boundMember.FieldType);
                }
                else if (publishedInitializerMembers is { Count: > 0 } && index < publishedInitializerMembers.Count)
                {
                    var publishedMember = publishedInitializerMembers[index];
                    fieldName = publishedMember.FieldName;
                    fieldIndex = publishedMember.FieldIndex;
                    fieldType = ApplyGenericSubstitution(publishedMember.FieldType);
                }
                else if (namedType is null
                         || !namedType.TryGetField(fieldName, out var field, out fieldIndex))
                {
                    throw LoweringInvariantViolation(
                        initializer,
                        $"Object initializer member '{fieldName}' was accepted without a matching field on '{targetType.DisplayName}'.");
                }
                else
                {
                    fieldType = ApplyGenericSubstitution(field.Type);
                }

                resolvedInitializerMembers?.Add(new ObjectInitializerMemberTypingRecord(fieldName, fieldIndex, fieldType));

                var memberInitializer = initializer.variableInitializer();
                var value = LowerInitializerToOperand(memberInitializer, fieldType);
                if (value is null)
                {
                    throw LoweringInvariantViolation(
                        memberInitializer,
                        $"Object initializer member '{fieldName}' did not lower to a MIR operand.");
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
                    throw LoweringInvariantViolation(
                        initializer,
                        $"Object initializer member '{fieldName}' could not be inserted.");
                }

                current = updated;
                RecordMoveFromOperand(value, fieldType);
            }

            return current;
        }

        private MidLevelIrObjectConstructionOperand WrapObjectConstruction(
            StarkTypeSymbol createdType,
            MidLevelIrOperand value,
            TypedConstructorShape? constructor,
            IReadOnlyList<ObjectInitializerMemberTypingRecord> initializerMembers,
            bool hasInitializer,
            string text)
        {
            if (createdType.Kind != StarkTypeKind.Named || createdType.NamedType is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Object construction for '{text}' requires a named concrete target type, but lowering received '{createdType.DisplayName}'.");
            }

            if (!HasSameStorageType(value.Type, createdType))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Object construction for '{createdType.DisplayName}' produced '{value.Type.DisplayName}'.");
            }

            if (constructor is { IsPrimaryShape: false, BodyKey: null })
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Explicit constructor construction for '{createdType.DisplayName}' reached MIR without a constructor body key.");
            }

            var kind = (constructor, hasInitializer) switch
            {
                (null, false) => MidLevelIrObjectConstructionKind.Empty,
                (null, true) => MidLevelIrObjectConstructionKind.Initializer,
                ({ IsPrimaryShape: true }, false) => MidLevelIrObjectConstructionKind.PrimaryConstructor,
                ({ IsPrimaryShape: false }, false) => MidLevelIrObjectConstructionKind.ExplicitConstructor,
                _ => MidLevelIrObjectConstructionKind.ConstructorAndInitializer
            };
            var facts = new MidLevelIrObjectConstructionFacts(
                createdType,
                createdType.NamedType,
                kind,
                constructor,
                initializerMembers.ToArray(),
                constructor is { IsPrimaryShape: false } ? constructor.BodyKey : null);
            return new MidLevelIrObjectConstructionOperand(value, facts);
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
                throw LoweringInvariantViolation(
                    expression,
                    $"Resolved constructor for '{createdType.DisplayName}' expects {constructor.Parameters.Count} argument(s), but object creation supplied {argumentCount}.");
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
                throw LoweringInvariantViolation(
                    argumentList,
                    $"Primary constructor lowering requires a named type, primary shape, and matching argument count for '{createdType.DisplayName}'.");
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);

            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    throw LoweringInvariantViolation(
                        argumentList,
                        $"Primary constructor parameter '{parameter.Name}' was accepted without a matching field on '{createdType.DisplayName}'.");
                }

                var loweredArgument = LowerExpressionToOperand(argumentList!.argument(index).expression(), parameter.Type);
                if (loweredArgument is null)
                {
                    throw LoweringInvariantViolation(
                        argumentList!.argument(index).expression(),
                        $"Primary constructor argument {index + 1} did not lower to a MIR operand.");
                }

                var fieldType = ApplyGenericSubstitution(field.Type);
                var fieldValue = CoerceOperand(loweredArgument, fieldType);
                if (fieldValue is null)
                {
                    throw LoweringInvariantViolation(
                        argumentList!.argument(index).expression(),
                        $"Primary constructor argument {index + 1} could not coerce to field '{field.Name}'.");
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
                    throw LoweringInvariantViolation(
                        argumentList!.argument(index),
                        $"Primary constructor field '{field.Name}' could not be inserted.");
                }

                current = updated;
                RecordMoveFromOperand(fieldValue, fieldType);
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
                throw LoweringInvariantViolation(
                    expression,
                    $"Constructor body for '{createdType.DisplayName}' is not available to MIR lowering.");
            }

            var loweredArguments = new MidLevelIrOperand[constructor.Parameters.Count];
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                var loweredArgument = LowerExpressionToOperand(argumentList!.argument(index).expression(), parameter.Type);
                if (loweredArgument is null)
                {
                    throw LoweringInvariantViolation(
                        argumentList!.argument(index).expression(),
                        $"Explicit constructor argument {index + 1} did not lower to a MIR operand.");
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

                return EmitRequiredTemporary(new MidLevelIrUseRValue(selfLocal), "ctor");
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
            var hasBoundEnumConstruction = TryResolveBoundEnumConstruction(expression, out var boundEnumConstruction);

            if (hasBoundEnumConstruction)
            {
                enumType = ApplyGenericSubstitution(boundEnumConstruction.EnumType);
                constructorName = $"{enumType.DisplayName}.{boundEnumConstruction.VariantName}";
                if (!TryGetEnumLayout(enumType, out layout)
                    || !layout.TryGetVariant(boundEnumConstruction.VariantName, out variant))
                {
                    throw LoweringInvariantViolation(
                        expression,
                        $"Bound enum constructor '{constructorName}' reached MIR without matching enum layout facts.");
                }
            }
            else if (TryGetPublishedEnumConstructorSummary(expression, out var publishedSummary)
                && publishedSummary is not null)
            {
                publishedEnumConstructor = publishedSummary;
                enumType = ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
                constructorName = $"{enumType.DisplayName}.{publishedEnumConstructor.VariantName}";

                if (!TryGetEnumLayout(enumType, out layout)
                    || !layout.TryGetVariant(publishedEnumConstructor.VariantName, out variant))
                {
                    throw LoweringInvariantViolation(
                        expression,
                        $"Published enum constructor '{constructorName}' reached MIR without matching enum layout facts.");
                }
            }
            else
            {
                constructorName = expression.enumCaseTarget().GetText();
                if (!TryResolveEnumCaseTarget(expression.enumCaseTarget(), out _, out enumType, out layout, out variant))
                {
                    throw LoweringInvariantViolation(
                        expression.enumCaseTarget(),
                        $"Enum constructor target '{constructorName}' was accepted without resolved enum layout facts.");
                }
            }

            if (!variant.UsesNamedFields)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Named-field enum constructor syntax reached MIR for non-named-field enum case '{constructorName}'.");
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
                else if (hasBoundEnumConstruction && memberOrdinal < boundEnumConstruction.Members.Count)
                {
                    var boundMember = boundEnumConstruction.Members[memberOrdinal];
                    memberName = boundMember.FieldName;
                    fieldIndex = boundMember.FieldIndex;
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
                    throw LoweringInvariantViolation(
                        member,
                        $"Enum constructor member '{memberName}' was accepted without a matching field on '{constructorName}'.");
                }

                var value = LowerExpressionToOperand(member.expression(), layoutField.Type);
                if (value is null)
                {
                    throw LoweringInvariantViolation(
                        member.expression(),
                        $"Enum constructor member '{memberName}' did not lower to a MIR operand.");
                }

                var coerced = CoerceOperand(value, layoutField.Type);
                if (coerced is null)
                {
                    throw LoweringInvariantViolation(
                        member.expression(),
                        $"Enum constructor member '{memberName}' could not coerce to payload field type '{layoutField.Type.DisplayName}'.");
                }

                memberValues[fieldIndex] = coerced;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                if (!memberValues.TryGetValue(index, out var value))
                {
                    throw LoweringInvariantViolation(
                        expression,
                        $"Enum constructor '{constructorName}' reached MIR without value for payload field {index}.");
                }

                orderedValues[index] = value;
            }

            var lowered = LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, expression.GetText());
            if (lowered is null)
            {
                throw LoweringInvariantViolation(expression, $"Enum constructor '{constructorName}' did not lower to a MIR operand.");
            }

            return expectedType is null ? lowered : CoerceOperand(lowered, expectedType);
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
                throw LoweringInvariantViolation(
                    arguments,
                    $"Published positional enum constructor '{publishedCaseName}' reached MIR without positional enum layout facts.");
            }

            if (variant.Fields.Count != arguments.argument().Length)
            {
                throw LoweringInvariantViolation(
                    arguments,
                    $"Published enum constructor '{publishedCaseName}' expects {variant.Fields.Count} argument(s), but call supplied {arguments.argument().Length}.");
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), field.Type);
                if (argument is null)
                {
                    throw LoweringInvariantViolation(
                        arguments.argument(index).expression(),
                        $"Published enum constructor argument {index + 1} did not lower to a MIR operand.");
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    throw LoweringInvariantViolation(
                        arguments.argument(index).expression(),
                        $"Published enum constructor argument {index + 1} could not coerce to payload field type '{field.Type.DisplayName}'.");
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, $"{publishedCaseName}{arguments.GetText()}");
            if (value is null)
            {
                throw LoweringInvariantViolation(arguments, $"Published enum constructor '{publishedCaseName}' did not lower to a MIR operand.");
            }

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
                throw LoweringInvariantViolation(
                    expression,
                    $"Published unit enum value '{publishedCaseName}' reached MIR without unit enum layout facts.");
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, [], publishedCaseName);
            return true;
        }

        private bool TryLowerBoundEnumValue(
            StarkParser.PrimaryExpressionContext expression,
            out MidLevelIrOperand? value)
        {
            value = null;
            if (!TryResolveBoundEnumValue(expression, out var boundEnumValue))
            {
                return false;
            }

            var enumType = ApplyGenericSubstitution(boundEnumValue.EnumType);
            var caseName = $"{enumType.DisplayName}.{boundEnumValue.VariantName}";
            if (!TryGetEnumLayout(enumType, out var layout)
                || !layout.TryGetVariant(boundEnumValue.VariantName, out var variant)
                || variant.Fields.Count != 0)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Bound unit enum value '{caseName}' reached MIR without unit enum layout facts.");
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, [], caseName);
            return true;
        }

        private bool TryLowerEnumConstructorCall(
            string constructorName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrOperand? value)
        {
            value = null;
            StarkTypeSymbol enumType;
            EnumLayoutSymbol layout;
            EnumVariantLayoutSymbol variant;

            if (TryResolveBoundEnumCall(arguments, out var boundEnumCall))
            {
                enumType = ApplyGenericSubstitution(boundEnumCall.EnumType);
                if (!TryGetEnumLayout(enumType, out layout)
                    || !layout.TryGetVariant(boundEnumCall.VariantName, out variant)
                    || variant.UsesNamedFields)
                {
                    throw LoweringInvariantViolation(
                        arguments,
                        $"Bound enum constructor '{enumType.DisplayName}.{boundEnumCall.VariantName}' reached MIR without positional enum layout facts.");
                }
            }
            else if (!TryResolveEnumCaseReference(constructorName, out enumType, out layout, out variant)
                || variant.UsesNamedFields)
            {
                return false;
            }

            if (variant.Fields.Count != arguments.argument().Length)
            {
                throw LoweringInvariantViolation(
                    arguments,
                    $"Enum constructor '{constructorName}' expects {variant.Fields.Count} argument(s), but call supplied {arguments.argument().Length}.");
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerExpressionToOperand(arguments.argument(index).expression(), field.Type);
                if (argument is null)
                {
                    throw LoweringInvariantViolation(
                        arguments.argument(index).expression(),
                        $"Enum constructor argument {index + 1} did not lower to a MIR operand.");
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    throw LoweringInvariantViolation(
                        arguments.argument(index).expression(),
                        $"Enum constructor argument {index + 1} could not coerce to payload field type '{field.Type.DisplayName}'.");
                }

                loweredArguments[index] = coerced;
            }

            value = LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, text);
            if (value is null)
            {
                throw LoweringInvariantViolation(arguments, $"Enum constructor '{constructorName}' did not lower to a MIR operand.");
            }

            return true;
        }

        private MidLevelIrOperand? LowerDirectTagEnumConstructor(
            StarkTypeSymbol enumType,
            EnumLayoutSymbol layout,
            EnumVariantLayoutSymbol variant,
            IReadOnlyList<MidLevelIrOperand> payloadValues,
            string text)
        {
            if (layout.Kind != EnumLayoutKind.DirectTag)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Enum constructor '{text}' reached MIR with unsupported enum layout '{layout.Kind}'.");
            }

            if (payloadValues.Count != variant.Fields.Count)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Enum constructor '{text}' supplied {payloadValues.Count} payload value(s), but variant '{variant.Name}' requires {variant.Fields.Count}.");
            }

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
                throw LoweringInvariantViolation(null, $"Enum constructor '{text}' could not materialize its tag field.");
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
                    throw LoweringInvariantViolation(null, $"Enum constructor '{text}' could not materialize payload field {index}.");
                }

                current = updated;
                RecordMoveFromOperand(payloadValues[index], field.Type);
            }

            var payloadFacts = new MidLevelIrEnumPayloadFieldConstructionFacts[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                payloadFacts[index] = new MidLevelIrEnumPayloadFieldConstructionFacts(
                    field.SourceFieldName ?? field.StorageFieldName,
                    index,
                    field.Type,
                    field.StorageFieldName,
                    field.StorageFieldIndex,
                    field.Type);
            }

            return new MidLevelIrEnumConstructionOperand(
                current,
                new MidLevelIrEnumConstructionFacts(
                    enumType,
                    layout,
                    variant,
                    payloadFacts));
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
                throw LoweringInvariantViolation(
                    arrayInitializer,
                    $"Array initializer requires a fixed-size array target, but lowering received '{targetType.DisplayName}'.");
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);
            var elementCount = Math.Min(fixedLength, arrayInitializer.variableInitializer().Length);

            for (var index = 0; index < elementCount; index++)
            {
                var elementInitializer = arrayInitializer.variableInitializer(index);
                var value = LowerInitializerToOperand(elementInitializer, targetType.ElementType);
                if (value is null)
                {
                    throw LoweringInvariantViolation(
                        elementInitializer,
                        $"Array initializer element {index} did not lower to a MIR operand.");
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        current,
                        index,
                        IndexedElementOperationFamily.FixedArrayElement,
                        value,
                        targetType,
                        $"{current.Text}[{index}] = {elementInitializer.GetText()}"),
                    "insertindex");
                if (updated is null)
                {
                    throw LoweringInvariantViolation(
                        elementInitializer,
                        $"Array initializer element {index} could not be inserted.");
                }

                current = updated;
                RecordMoveFromOperand(value, targetType.ElementType);
            }

            return current;
        }

        private MidLevelIrOperand? LowerFieldAccess(MidLevelIrOperand target, string memberName)
        {
            if (TryResolveDynamicStorageField(target.Type, memberName, out var dynamicFieldType, out var dynamicFieldIndex))
            {
                return EmitTemporary(
                    new MidLevelIrExtractFieldRValue(
                        target,
                        memberName,
                        dynamicFieldIndex,
                        dynamicFieldType,
                        $"{target.Text}.{memberName}"),
                    "field");
            }

            if (!TryResolveField(target.Type, memberName, out var field, out var fieldIndex))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Field '{memberName}' could not be resolved on type '{target.Type.DisplayName}' (named type '{target.Type.NamedType ?? "<none>"}').");
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

        private static bool TryResolveDynamicStorageField(
            StarkTypeSymbol type,
            string fieldName,
            out StarkTypeSymbol fieldType,
            out int fieldIndex)
        {
            if (type.Kind == StarkTypeKind.Dynamic && type.ElementType is not null)
            {
                if (string.Equals(fieldName, "Data", StringComparison.Ordinal))
                {
                    fieldType = StarkTypeSymbols.RawPointer(type.ElementType, isMutable: true);
                    fieldIndex = 0;
                    return true;
                }

                if (string.Equals(fieldName, "Capacity", StringComparison.Ordinal))
                {
                    fieldType = NonNegativeI64Type;
                    fieldIndex = 2;
                    return true;
                }

                if (string.Equals(fieldName, "Length", StringComparison.Ordinal))
                {
                    fieldType = NonNegativeI64Type;
                    fieldIndex = 1;
                    return true;
                }
            }

            fieldType = StarkTypeSymbols.Error;
            fieldIndex = -1;
            return false;
        }

        private MidLevelIrOperand? LowerIndexAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var hasBoundIndexAccess = TryResolveBoundIndexAccess(indexes, out var boundIndexAccess);
            if (hasBoundIndexAccess)
            {
                var boundSourceType = ApplyGenericSubstitution(boundIndexAccess.SourceType);
                if (!HasSameStorageType(boundSourceType, target.Type))
                {
                    throw LoweringInvariantViolation(
                        indexes,
                        $"Bound index operation source type '{boundSourceType.DisplayName}' does not match lowered target type '{target.Type.DisplayName}'.");
                }

                if (boundIndexAccess.IndexCount != indexes.expression().Length)
                {
                    throw LoweringInvariantViolation(
                        indexes,
                        $"Bound index operation records {boundIndexAccess.IndexCount} index operand(s), but source has {indexes.expression().Length}.");
                }

                var boundResult = boundIndexAccess.AccessKind switch
                {
                    BoundIndexAccessKind.TextElement or BoundIndexAccessKind.TextSlice =>
                        LowerTextAccess(target, indexes),
                    BoundIndexAccessKind.DynamicElement or BoundIndexAccessKind.DynamicSlice =>
                        LowerDynamicStorageAccess(target, indexes),
                    BoundIndexAccessKind.Element
                        or BoundIndexAccessKind.Slice
                        or BoundIndexAccessKind.RawPointerRegion =>
                        LowerSequentialIndexAccess(),
                    _ => throw LoweringInvariantViolation(
                        indexes,
                        $"Bound index operation kind '{boundIndexAccess.AccessKind}' has no MIR lowering case.")
                };
                return ValidateBoundIndexResult(boundResult, boundIndexAccess, hasBoundIndexAccess: true, indexes);
            }

            if (CanUsePartitionedTextSwitchType(target.Type))
            {
                return LowerTextAccess(target, indexes);
            }

            if (target.Type.Kind == StarkTypeKind.Dynamic && target.Type.ElementType is not null)
            {
                return LowerDynamicStorageAccess(target, indexes);
            }

            return LowerSequentialIndexAccess();

            MidLevelIrOperand? LowerSequentialIndexAccess()
            {
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
                            var extracted = EmitRequiredTemporary(
                                new MidLevelIrExtractIndexRValue(
                                    current,
                                    constantIndex,
                                    IndexedElementOperationFamily.FixedArrayElement,
                                    elementType,
                                    $"{current.Text}[{constantIndex}]"),
                                "index");

                            current = extracted;
                            currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                            continue;
                        }

                        if (current.Type.ElementType is null)
                        {
                            throw LoweringInvariantViolation(indexes, "Dynamic fixed-array indexing requires a fixed-array element type.");
                        }

                        var projectedElementType = currentUsesFrozenProjectionSemantics
                            ? StarkTypeSymbols.FreezeReachableView(current.Type.ElementType)
                            : ProjectFrozenView(current.Type, current.Type.ElementType);
                        var index = LowerExpressionToOperand(indexExpression);
                        if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                        {
                            throw LoweringInvariantViolation(indexExpression, "Dynamic fixed-array indexing requires an integer index operand.");
                        }

                        var baseAddress = TryCreateDynamicFixedArrayBaseAddress(current);
                        if (baseAddress is null)
                        {
                            throw LoweringInvariantViolation(indexes, "Dynamic fixed-array indexing requires an addressable fixed-array source.");
                        }

                        var elementAddress = EmitRequiredTemporary(
                            new MidLevelIrElementAddressRValue(
                                baseAddress,
                                current.Type,
                                index,
                                ConstantIndex: null,
                                AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "addr");

                        var loaded = EmitRequiredTemporary(
                            new MidLevelIrLoadIndirectRValue(
                                elementAddress,
                                projectedElementType,
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "load");

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
                            throw LoweringInvariantViolation(indexExpression, "Slice indexing requires an integer index operand.");
                        }

                        var elementAddress = EmitRequiredTemporary(
                            new MidLevelIrSliceElementAddressRValue(
                                current,
                                index,
                                AddressType(elementType, current.Type.IsMutableView && CanMutateThroughType(current.Type)),
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "addr");

                        var loaded = EmitRequiredTemporary(
                            new MidLevelIrLoadIndirectRValue(
                                elementAddress,
                                elementType,
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "load");

                        current = loaded;
                        currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                        continue;
                    }

                    if (current.Type.Kind == StarkTypeKind.RawPointer && current.Type.ElementType is not null)
                    {
                        var addressSource = currentUsesFrozenProjectionSemantics
                            ? CoerceOperand(current, StarkTypeSymbols.FreezeReachableView(current.Type)) ?? current
                            : current;
                        var elementType = currentUsesFrozenProjectionSemantics
                            ? StarkTypeSymbols.FreezeReachableView(current.Type.ElementType)
                            : current.Type.ElementType;
                        var index = LowerExpressionToOperand(indexExpression);
                        if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                        {
                            throw LoweringInvariantViolation(indexExpression, "Raw pointer indexing requires an integer index operand.");
                        }

                        var elementAddress = EmitRequiredTemporary(
                            new MidLevelIrElementAddressRValue(
                                addressSource,
                                elementType,
                                index,
                                ConstantIndex: null,
                                AddressType(elementType, addressSource.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "addr");

                        var loaded = EmitRequiredTemporary(
                            new MidLevelIrLoadIndirectRValue(
                                elementAddress,
                                elementType,
                                $"{current.Text}[{indexExpression.GetText()}]"),
                            "load");

                        current = loaded;
                        currentUsesFrozenProjectionSemantics = current.Type.AccessKind == StarkAccessKind.Frozen;
                        continue;
                    }

                    throw LoweringInvariantViolation(indexes, "Indexing is only supported for fixed arrays, raw pointers, slices, ascii, unicode, and dynamic storage values.");
                }

                return current;
            }
        }

        private MidLevelIrOperand? ValidateBoundIndexResult(
            MidLevelIrOperand? result,
            BoundIndexAccessOperation boundIndexAccess,
            bool hasBoundIndexAccess,
            ParserRuleContext context)
        {
            if (!hasBoundIndexAccess)
            {
                return result;
            }

            if (result is null)
            {
                throw LoweringInvariantViolation(
                    context,
                    $"Bound index operation '{boundIndexAccess.AccessKind}' did not lower to a MIR operand.");
            }

            var expectedResultType = ApplyGenericSubstitution(boundIndexAccess.ResultType);
            if (!HasSameStorageType(expectedResultType, result.Type))
            {
                throw LoweringInvariantViolation(
                    context,
                    $"Bound index operation result type '{expectedResultType.DisplayName}' does not match lowered result type '{result.Type.DisplayName}'.");
            }

            return result;
        }

        private MidLevelIrOperand? LowerDynamicStorageAccess(MidLevelIrOperand target, StarkParser.ExpressionListContext indexes)
        {
            var indexExpressions = indexes.expression();
            if (indexExpressions.Length is not (1 or 2))
            {
                throw LoweringInvariantViolation(indexes, "Dynamic storage indexing requires one integer index or a start/count pair.");
            }

            var start = LowerExpressionToOperand(indexExpressions[0]);
            if (start is null || start.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(indexExpressions[0], "Dynamic storage indexing requires an integer start/index operand.");
            }

            var dataPointerType = StarkTypeSymbols.RawPointer(target.Type.ElementType!, isMutable: true);
            var dataPointer = LowerKnownFieldAccess(target, "Data", 0, dataPointerType, "Data");
            var elementType = UsesFrozenProjectionSemantics(target)
                ? StarkTypeSymbols.FreezeReachableView(target.Type.ElementType!)
                : ProjectFrozenView(target.Type, target.Type.ElementType!);
            var elementAddress = EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    dataPointer,
                    elementType,
                    start,
                    ConstantIndex: null,
                    AddressType(elementType, dataPointer.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                    $"{target.Text}[{indexExpressions[0].GetText()}]"),
                "addr");
            if (elementAddress is null)
            {
                return null;
            }

            if (indexExpressions.Length == 2)
            {
                var length = LowerExpressionToOperand(indexExpressions[1]);
                if (length is null || length.Type.Kind != StarkTypeKind.Integer)
                {
                    throw LoweringInvariantViolation(indexExpressions[1], "Dynamic storage slicing requires an integer count operand.");
                }

                var sliceType = StarkTypeSymbols.ApplyQualifiers(
                    StarkTypeSymbols.Slice(elementType),
                    isMutableView: dataPointer.Type.IsMutablePointer && CanMutateThroughType(elementType));
                return EmitTemporary(
                    new MidLevelIrMakeSliceFromPointerRValue(
                        elementAddress,
                        length,
                        sliceType,
                        $"{target.Text}[{indexExpressions[0].GetText()}, {indexExpressions[1].GetText()}]"),
                    "slice");
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    elementAddress,
                    elementType,
                    $"{target.Text}[{indexExpressions[0].GetText()}]"),
                "load");
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
                    throw LoweringInvariantViolation(indexExpressions[0], "Text indexing requires an integer index operand.");
                }

                return LowerTextSlice(
                    target,
                    start,
                    new MidLevelIrIntegerConstantOperand(BigInteger.One, StarkTypeSymbols.Integer(64)),
                    $"{target.Text}[{indexExpressions[0].GetText()}]");
            }

            if (indexExpressions.Length != 2)
            {
                throw LoweringInvariantViolation(indexes, "Text indexing requires exactly one integer index or two integer indices.");
            }

            var sliceStart = LowerExpressionToOperand(indexExpressions[0]);
            var sliceLength = LowerExpressionToOperand(indexExpressions[1]);
            if (sliceStart is null
                || sliceLength is null
                || sliceStart.Type.Kind != StarkTypeKind.Integer
                || sliceLength.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(indexes, "Text slicing requires integer start and length operands.");
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
                throw LoweringInvariantViolation(null, $"Text slice '{text}' could not coerce start/count operands to i64.");
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

        private bool TryLowerDirectCallExpressionAsStatement(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (expression.postfixPart().Length == 0
                || expression.postfixPart()[^1].argumentList() is not { })
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
                    if (currentName is null)
                    {
                        return false;
                    }

                    var text = $"{currentName}{argumentList.GetText()}";
                    if (index == expression.postfixPart().Length - 1)
                    {
                        return TryBuildCallStatement(currentName, argumentList, text, out call);
                    }

                    if (!TryBuildCall(currentName, argumentList, text, out var directCall)
                        || directCall.Type.Kind == StarkTypeKind.Void)
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
                    var text = $"{currentValue.Text}.{memberName}{memberArguments.GetText()}";
                    if (index + 1 == expression.postfixPart().Length - 1)
                    {
                        return TryBuildMemberCallStatement(currentValue, currentPlace, memberName, memberArguments, text, out call);
                    }

                    if (!TryBuildMemberCall(currentValue, currentPlace, memberName, memberArguments, text, out var memberCall)
                        || memberCall.Type.Kind == StarkTypeKind.Void)
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

            var hasBoundOperation = TryResolveBoundFunctionPointerCallOperation(arguments, out var boundOperation);
            if (IsEnumCaseCallTarget(expression.primaryExpression()))
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound function-pointer call operation was attached to an enum constructor target.");
                }

                return false;
            }

            var target = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (target is null)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound function-pointer call target did not lower to an operand.");
                }

                return false;
            }

            if (target.Type.Kind != StarkTypeKind.FunctionPointer)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(
                        arguments,
                        $"Bound function-pointer call target lowered to '{target.Type.DisplayName}' instead of a function pointer.");
                }

                return false;
            }

            if (hasBoundOperation)
            {
                ValidateBoundFunctionPointerCallOperation(boundOperation, target.Type, arguments);
            }

            if (TryBuildIndirectCall(target, arguments, $"{target.Text}{arguments.GetText()}", out call))
            {
                return true;
            }

            if (hasBoundOperation)
            {
                throw LoweringInvariantViolation(arguments, $"Bound function-pointer call could not bind lowered arguments. {_lastCallBuildFailureReason}");
            }

            return false;
        }

        private bool TryLowerIndirectCallExpressionAsStatement(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrIndirectCallStatementOperation call)
        {
            call = default!;

            if (!TryLowerIndirectCallExpressionParts(expression, out var parts))
            {
                return false;
            }

            call = ToStatementCall(parts);
            return true;
        }

        private bool TryLowerIndirectCallExpressionParts(
            StarkParser.PostfixExpressionContext expression,
            out LoweredIndirectCallParts call)
        {
            call = default!;

            if (expression.postfixPart().Length != 1
                || expression.postfixPart()[0].argumentList() is not { } arguments)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundFunctionPointerCallOperation(arguments, out var boundOperation);
            if (IsEnumCaseCallTarget(expression.primaryExpression()))
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound function-pointer call operation was attached to an enum constructor target.");
                }

                return false;
            }

            var target = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (target is null)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound function-pointer call target did not lower to an operand.");
                }

                return false;
            }

            if (target.Type.Kind != StarkTypeKind.FunctionPointer)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(
                        arguments,
                        $"Bound function-pointer call target lowered to '{target.Type.DisplayName}' instead of a function pointer.");
                }

                return false;
            }

            if (hasBoundOperation)
            {
                ValidateBoundFunctionPointerCallOperation(boundOperation, target.Type, arguments);
            }

            if (TryBuildIndirectCallParts(target, arguments, $"{target.Text}{arguments.GetText()}", out call))
            {
                return true;
            }

            if (hasBoundOperation)
            {
                throw LoweringInvariantViolation(arguments, $"Bound function-pointer call could not bind lowered arguments. {_lastCallBuildFailureReason}");
            }

            return false;
        }

        private bool TryLowerClosureCallExpression(StarkParser.PostfixExpressionContext expression, out MidLevelIrIndirectCallRValue call)
        {
            call = default!;

            if (expression.postfixPart().Length != 1
                || expression.postfixPart()[0].argumentList() is not { } arguments)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundClosureCallOperation(arguments, out var boundOperation);
            if (IsEnumCaseCallTarget(expression.primaryExpression()))
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound closure call operation was attached to an enum constructor target.");
                }

                return false;
            }

            var target = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (target is null)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound closure call target did not lower to an operand.");
                }

                return false;
            }

            if (target.Type.Kind != StarkTypeKind.Closure)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(
                        arguments,
                        $"Bound closure call target lowered to '{target.Type.DisplayName}' instead of a closure.");
                }

                return false;
            }

            if (hasBoundOperation)
            {
                ValidateBoundClosureCallOperation(boundOperation, target.Type, arguments);
            }

            if (TryBuildClosureCall(target, arguments, $"{target.Text}{arguments.GetText()}", out call))
            {
                return true;
            }

            if (hasBoundOperation)
            {
                throw LoweringInvariantViolation(arguments, $"Bound closure call could not bind lowered arguments. {_lastCallBuildFailureReason}");
            }

            return false;
        }

        private bool TryLowerClosureCallExpressionAsStatement(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrIndirectCallStatementOperation call)
        {
            call = default!;

            if (!TryLowerClosureCallExpressionParts(expression, out var parts))
            {
                return false;
            }

            call = ToStatementCall(parts);
            return true;
        }

        private bool TryLowerClosureCallExpressionParts(
            StarkParser.PostfixExpressionContext expression,
            out LoweredIndirectCallParts call)
        {
            call = default!;

            if (expression.postfixPart().Length != 1
                || expression.postfixPart()[0].argumentList() is not { } arguments)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundClosureCallOperation(arguments, out var boundOperation);
            if (IsEnumCaseCallTarget(expression.primaryExpression()))
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound closure call operation was attached to an enum constructor target.");
                }

                return false;
            }

            var target = LowerPrimaryExpression(expression.primaryExpression(), expectedType: null);
            if (target is null)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(arguments, "Bound closure call target did not lower to an operand.");
                }

                return false;
            }

            if (target.Type.Kind != StarkTypeKind.Closure)
            {
                if (hasBoundOperation)
                {
                    throw LoweringInvariantViolation(
                        arguments,
                        $"Bound closure call target lowered to '{target.Type.DisplayName}' instead of a closure.");
                }

                return false;
            }

            if (hasBoundOperation)
            {
                ValidateBoundClosureCallOperation(boundOperation, target.Type, arguments);
            }

            if (TryBuildClosureCallParts(target, arguments, $"{target.Text}{arguments.GetText()}", out call))
            {
                return true;
            }

            if (hasBoundOperation)
            {
                throw LoweringInvariantViolation(arguments, $"Bound closure call could not bind lowered arguments. {_lastCallBuildFailureReason}");
            }

            return false;
        }

        private void ValidateBoundFunctionPointerCallOperation(
            BoundFunctionPointerCallOperation operation,
            StarkTypeSymbol targetType,
            StarkParser.ArgumentListContext arguments)
        {
            var boundTargetType = ApplyGenericSubstitution(operation.FunctionPointerType);
            if (!HasSameStorageType(boundTargetType, targetType)
                || operation.Arguments.Count != arguments.argument().Length)
            {
                throw LoweringInvariantViolation(
                    arguments,
                    $"Bound function-pointer call records target type '{boundTargetType.DisplayName}' with {operation.Arguments.Count} argument(s), but lowering found '{targetType.DisplayName}' with {arguments.argument().Length}.");
            }
        }

        private void ValidateBoundClosureCallOperation(
            BoundClosureCallOperation operation,
            StarkTypeSymbol targetType,
            StarkParser.ArgumentListContext arguments)
        {
            var boundTargetType = ApplyGenericSubstitution(operation.ClosureType);
            if (!HasSameStorageType(boundTargetType, targetType)
                || operation.Arguments.Count != arguments.argument().Length)
            {
                throw LoweringInvariantViolation(
                    arguments,
                    $"Bound closure call records target type '{boundTargetType.DisplayName}' with {operation.Arguments.Count} argument(s), but lowering found '{targetType.DisplayName}' with {arguments.argument().Length}.");
            }
        }

        private bool TryBuildClosureCall(
            MidLevelIrOperand target,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrIndirectCallRValue call)
        {
            if (!TryBuildClosureCallParts(target, arguments, text, out var parts))
            {
                call = default!;
                return false;
            }

            return TryCreateValueCall(parts, out call);
        }

        private bool TryBuildClosureCallParts(
            MidLevelIrOperand target,
            StarkParser.ArgumentListContext arguments,
            string text,
            out LoweredIndirectCallParts call)
        {
            call = default!;

            if (target.Type.ClosureReturnType is not { } returnType
                || target.Type.ClosureParameterTypes is not { } parameterTypes
                || parameterTypes.Count != arguments.argument().Length)
            {
                return false;
            }

            var invokePointerType = CallableValueFacts.BuildClosureInvokeFunctionPointerType(target.Type);
            var environmentPointerType = CallableValueFacts.BuildClosureEnvironmentPointerType(target.Type);
            var invokePointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    invokePointerType,
                    $"{target.Text}.invoke"),
                "closure_invoke");
            var environmentPointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    environmentPointerType,
                    $"{target.Text}.env"),
                "closure_env");
            if (invokePointer is null || environmentPointer is null)
            {
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>(arguments.argument().Length + 1)
            {
                environmentPointer
            };
            var indirectArgumentLocals = new List<string?>(arguments.argument().Length + 1)
            {
                null
            };
            var indirectArgumentAddresses = new List<MidLevelIrOperand?>(arguments.argument().Length + 1)
            {
                null
            };

            for (var index = 0; index < arguments.argument().Length; index++)
            {
                var parameterType = parameterTypes[index];
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
                var indirectArgumentAddress = ResolveIndirectArgumentAddress(
                    parameterType,
                    arguments.argument(index).expression());
                indirectArgumentLocals.Add(indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(parameterType, lowered)
                        ?? ResolveIndirectArgumentLocal(parameterType, argument)
                    : null);
                indirectArgumentAddresses.Add(indirectArgumentAddress);
                RecordMoveFromOperand(argument, parameterType);
            }

            call = new LoweredIndirectCallParts(
                invokePointer,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(returnType),
                text,
                returnType,
                indirectArgumentLocals,
                indirectArgumentAddresses,
                MayFree: target.Type.ClosureStorageKind == StarkClosureStorageKind.Heap
                    && target.Type.ClosureCallCapability == StarkClosureCallCapability.Once);
            if (target.Type.ClosureCallCapability == StarkClosureCallCapability.Once)
            {
                RecordMoveFromOperand(target, target.Type);
            }

            return true;
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
            if (!TryBuildIndirectCallParts(target, arguments, text, out var parts))
            {
                call = default!;
                return false;
            }

            return TryCreateValueCall(parts, out call);
        }

        private bool TryBuildIndirectCallParts(
            MidLevelIrOperand target,
            StarkParser.ArgumentListContext arguments,
            string text,
            out LoweredIndirectCallParts call)
        {
            call = default!;

            if (target.Type.FunctionPointerReturnType is not { } returnType
                || target.Type.FunctionPointerParameterTypes is not { } parameterTypes
                || parameterTypes.Count != arguments.argument().Length)
            {
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>(arguments.argument().Length);
            var indirectArgumentLocals = new List<string?>(arguments.argument().Length);
            var indirectArgumentAddresses = new List<MidLevelIrOperand?>(arguments.argument().Length);
            for (var index = 0; index < arguments.argument().Length; index++)
            {
                var parameterType = parameterTypes[index];
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
                var indirectArgumentAddress = ResolveIndirectArgumentAddress(
                    parameterType,
                    arguments.argument(index).expression());
                indirectArgumentLocals.Add(indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(parameterType, lowered)
                        ?? ResolveIndirectArgumentLocal(parameterType, argument)
                    : null);
                indirectArgumentAddresses.Add(indirectArgumentAddress);
                RecordMoveFromOperand(argument, parameterType);
            }

            call = new LoweredIndirectCallParts(
                target,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(returnType),
                text,
                returnType,
                indirectArgumentLocals,
                indirectArgumentAddresses,
                MayFree: false);
            return true;
        }

        private bool TryBuildCall(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (TryResolveRecordedDirectCallSignature(functionName, arguments, out var recordedSignature))
            {
                if (TryBuildCall(recordedSignature.Name, recordedSignature, receiver: null, receiverPlace: null, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Recorded call '{functionName}' could not bind its arguments to '{recordedSignature.Name}'.");
            }

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
                    if (TryBuildCall(overloads[0].Name, overloads[0], receiver: null, receiverPlace: null, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Call '{functionName}' could not bind its arguments to '{overloads[0].Name}'. {_lastCallBuildFailureReason}");
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

            if (TryBuildCall(signature.Name, signature, receiver: null, receiverPlace: null, arguments, text, out call))
            {
                return true;
            }

            throw LoweringInvariantViolation(arguments, $"Call '{functionName}' could not bind its arguments to '{signature.Name}'. {_lastCallBuildFailureReason}");
        }

        private bool TryBuildCallStatement(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (TryResolveRecordedDirectCallSignature(functionName, arguments, out var recordedSignature))
            {
                if (TryBuildCallStatement(recordedSignature.Name, recordedSignature, receiver: null, receiverPlace: null, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Recorded call '{functionName}' could not bind its arguments to '{recordedSignature.Name}'.");
            }

            if (TryResolvePublishedDirectCallSignature(functionName, arguments, out var publishedSignature)
                && TryBuildCallStatement(publishedSignature.Name, publishedSignature, receiver: null, receiverPlace: null, arguments, text, out call))
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
                    if (TryBuildCallStatement(overloads[0].Name, overloads[0], receiver: null, receiverPlace: null, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Call '{functionName}' could not bind its arguments to '{overloads[0].Name}'. {_lastCallBuildFailureReason}");
                }

                return TryBuildOverloadedCallStatement(overloads, receiver: null, receiverPlace: null, arguments, text, out call);
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

            if (TryBuildCallStatement(signature.Name, signature, receiver: null, receiverPlace: null, arguments, text, out call))
            {
                return true;
            }

            throw LoweringInvariantViolation(arguments, $"Call '{functionName}' could not bind its arguments to '{signature.Name}'. {_lastCallBuildFailureReason}");
        }

        // True when the signature's containing type is a trait, i.e. the recorded
        // target is an abstract trait method that must be rebound to a concrete
        // implementation at the (now concrete) receiver type.
        private bool IsTraitMethodTarget(TypedFunctionSignature signature)
        {
            var name = signature.SourceName ?? signature.Name;
            var lastDot = name.LastIndexOf('.');
            if (lastDot <= 0)
            {
                return false;
            }

            var containingTypeName = name[..lastDot];
            return _namedTypes.TryGetValue(containingTypeName, out var symbol)
                && symbol.Kind == DeclarationKind.Trait;
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

            // CG06: a recorded member call whose target is a trait method comes from a
            // `where T: Trait` generic body. After specialization the receiver has a
            // concrete type, so fall through to resolve the concrete implementation as a
            // direct call rather than binding the abstract trait method.
            if (TryResolveRecordedMemberCallSignature(memberName, arguments, out var recordedSignature)
                && !IsTraitMethodTarget(recordedSignature))
            {
                if (TryBuildCall(recordedSignature.Name, recordedSignature, receiver, receiverPlace, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Recorded member call '{memberName}' could not bind its arguments to '{recordedSignature.Name}'.");
            }

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
                        if (TryBuildCall(candidates[0].Name, candidates[0], receiver, receiverPlace, arguments, text, out call))
                        {
                            return true;
                        }

                        throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{candidates[0].Name}'. {_lastCallBuildFailureReason}");
                    }

                    if (TryBuildOverloadedCall(candidates, receiver, receiverPlace, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind to an overload for receiver type '{receiver.Type.DisplayName}'. {_lastCallBuildFailureReason}");
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
                    if (TryBuildCall(overloads[0].Name, overloads[0], receiver, receiverPlace, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{overloads[0].Name}'. {_lastCallBuildFailureReason}");
                }

                if (TryBuildOverloadedCall(overloads, receiver, receiverPlace, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind to an overload for receiver type '{receiver.Type.DisplayName}'. {_lastCallBuildFailureReason}");
            }

            if (!TryResolveFunctionSignature(sourceName, out var signature)
                || signature.IsStatic
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            if (TryBuildCall(signature.Name, signature, receiver, receiverPlace, arguments, text, out call))
            {
                return true;
            }

            throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{signature.Name}'. {_lastCallBuildFailureReason}");
        }

        private bool TryBuildMemberCallStatement(
            MidLevelIrOperand receiver,
            PlaceTarget? receiverPlace,
            string memberName,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (TryResolveRecordedMemberCallSignature(memberName, arguments, out var recordedSignature))
            {
                if (TryBuildCallStatement(recordedSignature.Name, recordedSignature, receiver, receiverPlace, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Recorded member call '{memberName}' could not bind its arguments to '{recordedSignature.Name}'.");
            }

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
                        if (TryBuildCallStatement(candidates[0].Name, candidates[0], receiver, receiverPlace, arguments, text, out call))
                        {
                            return true;
                        }

                        throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{candidates[0].Name}'. {_lastCallBuildFailureReason}");
                    }

                    if (TryBuildOverloadedCallStatement(candidates, receiver, receiverPlace, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind to an overload for receiver type '{receiver.Type.DisplayName}'. {_lastCallBuildFailureReason}");
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
                    if (TryBuildCallStatement(overloads[0].Name, overloads[0], receiver, receiverPlace, arguments, text, out call))
                    {
                        return true;
                    }

                    throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{overloads[0].Name}'. {_lastCallBuildFailureReason}");
                }

                if (TryBuildOverloadedCallStatement(overloads, receiver, receiverPlace, arguments, text, out call))
                {
                    return true;
                }

                throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind to an overload for receiver type '{receiver.Type.DisplayName}'. {_lastCallBuildFailureReason}");
            }

            if (!TryResolveFunctionSignature(sourceName, out var signature)
                || signature.IsStatic
                || signature.Parameters.Count == 0)
            {
                return false;
            }

            if (TryBuildCallStatement(signature.Name, signature, receiver, receiverPlace, arguments, text, out call))
            {
                return true;
            }

            throw LoweringInvariantViolation(arguments, $"Member call '{memberName}' could not bind its arguments to '{signature.Name}'. {_lastCallBuildFailureReason}");
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

            if (receiver is null
                && TryResolveRecordedDirectCallSignature(
                    overloads[0].DisplaySourceName,
                    arguments,
                    out var recordedSignature)
                && TryBuildCall(recordedSignature.Name, recordedSignature, receiver, receiverPlace, arguments, text, out call))
            {
                return true;
            }

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

        private bool TryBuildOverloadedCallStatement(
            IReadOnlyList<TypedFunctionSignature> overloads,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (receiver is null
                && TryResolveRecordedDirectCallSignature(
                    overloads[0].DisplaySourceName,
                    arguments,
                    out var recordedSignature)
                && TryBuildCallStatement(recordedSignature.Name, recordedSignature, receiver, receiverPlace, arguments, text, out call))
            {
                return true;
            }

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

            return TryBuildCallStatement(
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
                        _lastCallBuildFailureReason = $"Argument {index + 1} '{arguments.argument(index).expression().GetText()}' could not be lowered with expected type '{expectedArgumentType?.DisplayName ?? "<none>"}'.";
                        return false;
                    }

                    explicitArguments.Add(lowered);
                }
            }

            return TryBuildCall(functionName, signature, receiver, receiverPlace, text, out call, explicitArguments, arguments);
        }

        private bool TryBuildCallStatement(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
            StarkParser.ArgumentListContext arguments,
            string text,
            out MidLevelIrDirectCallStatementOperation call,
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
                        _lastCallBuildFailureReason = $"Argument {index + 1} '{arguments.argument(index).expression().GetText()}' could not be lowered with expected type '{expectedArgumentType?.DisplayName ?? "<none>"}'.";
                        return false;
                    }

                    explicitArguments.Add(lowered);
                }
            }

            return TryBuildCallStatement(functionName, signature, receiver, receiverPlace, text, out call, explicitArguments, arguments);
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
            if (!TryBuildCallParts(
                functionName,
                signature,
                receiver,
                receiverPlace,
                text,
                out var parts,
                loweredExplicitArguments,
                syntaxArguments))
            {
                call = default!;
                return false;
            }

            return TryCreateValueCall(parts, out call);
        }

        private bool TryBuildCallStatement(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
            string text,
            out MidLevelIrDirectCallStatementOperation call,
            IReadOnlyList<MidLevelIrOperand> loweredExplicitArguments,
            StarkParser.ArgumentListContext? syntaxArguments = null)
        {
            if (!TryBuildCallParts(
                functionName,
                signature,
                receiver,
                receiverPlace,
                text,
                out var parts,
                loweredExplicitArguments,
                syntaxArguments))
            {
                call = default!;
                return false;
            }

            call = ToStatementCall(parts);
            return true;
        }

        private bool TryBuildCallParts(
            string functionName,
            TypedFunctionSignature signature,
            MidLevelIrOperand? receiver,
            PlaceTarget? receiverPlace,
            string text,
            out LoweredDirectCallParts call,
            IReadOnlyList<MidLevelIrOperand> loweredExplicitArguments,
            StarkParser.ArgumentListContext? syntaxArguments = null)
        {
            call = default!;
            _lastCallBuildFailureReason = null;

            if (signature.IsGeneric && !signature.IsGenericInstantiation)
            {
                var resolution = FunctionOverloadFacts.Resolve(
                    [signature],
                    receiver?.Type,
                    loweredExplicitArguments.Select(static argument => argument.Type).ToArray(),
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    _lastCallBuildFailureReason = $"Generic call resolution failed for {FunctionOverloadFacts.FormatSignature(signature)} with argument types ({string.Join(", ", loweredExplicitArguments.Select(static argument => argument.Type.DisplayName))}).";
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
                    _lastCallBuildFailureReason = $"Receiver '{receiver.Text}' of type '{receiver.Type.DisplayName}' could not coerce to '{receiverParameterType.DisplayName}'.";
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
                    _lastCallBuildFailureReason = $"Argument {index + 1} '{sourceArgument.Text}' of type '{sourceArgument.Type.DisplayName}' could not coerce to '{parameterType.DisplayName}'.";
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
                    _lastCallBuildFailureReason = $"Call expected at least {explicitParameterCount} explicit argument(s) for varargs signature {FunctionOverloadFacts.FormatSignature(signature)}, but got {loweredExplicitArguments.Count}.";
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
                _lastCallBuildFailureReason = $"Call expected {explicitParameterCount} explicit argument(s) for signature {FunctionOverloadFacts.FormatSignature(signature)}, but got {loweredExplicitArguments.Count}.";
                return false;
            }

            var loweredFunctionName = ResolveCallTargetName(functionName, signature);
            if (string.Equals(loweredFunctionName, functionName, StringComparison.Ordinal)
                && TryResolveDictionaryKeyBuiltinCallTarget(functionName, signature, loweredArguments, out var dictionaryKeySpecialization))
            {
                loweredFunctionName = dictionaryKeySpecialization;
            }

            var postCallDynamicLengthCommits = BuildPostCallDynamicLengthCommits(
                signature,
                loweredArguments,
                indirectArgumentLocals);

            call = new LoweredDirectCallParts(
                loweredFunctionName,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(signature.ReturnType),
                text,
                indirectArgumentLocals,
                signature.ReturnType,
                indirectArgumentAddresses,
                postCallDynamicLengthCommits);
            return true;
        }

        private IReadOnlyList<MidLevelIrDynamicStorageLengthCommit>? BuildPostCallDynamicLengthCommits(
            TypedFunctionSignature signature,
            IReadOnlyList<MidLevelIrOperand> arguments,
            IReadOnlyList<string?> indirectArgumentLocals)
        {
            if (!TryGetFullInitSliceCommitShape(signature, out var destinationIndex, out var countIndex)
                || destinationIndex >= arguments.Count
                || countIndex >= arguments.Count
                || destinationIndex >= indirectArgumentLocals.Count
                || countIndex >= signature.Parameters.Count
                || destinationIndex >= signature.Parameters.Count)
            {
                return null;
            }

            var destinationParameterType = signature.Parameters[destinationIndex].Type;
            if (destinationParameterType.Kind != StarkTypeKind.Slice
                || destinationParameterType.InitializationKind != StarkInitializationKind.Init)
            {
                return null;
            }

            var destinationLocal = indirectArgumentLocals[destinationIndex];
            if (destinationLocal is null
                && arguments[destinationIndex] is MidLevelIrLocalOperand localDestination)
            {
                destinationLocal = localDestination.Name;
            }

            if (destinationLocal is null
                || !_dynamicInitSliceProvenanceByLocal.TryGetValue(destinationLocal, out var provenance)
                || arguments[countIndex].Type.Kind != StarkTypeKind.Integer)
            {
                return null;
            }

            var start = CoerceOperand(provenance.StartIndex, NonNegativeI64Type) ?? provenance.StartIndex;
            var count = CoerceOperand(arguments[countIndex], NonNegativeI64Type) ?? arguments[countIndex];
            var initializedLength = EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Add,
                    start,
                    count,
                    NonNegativeI64Type,
                    $"{provenance.StartIndex.Text} + {arguments[countIndex].Text}"),
                "dynamic_len");
            if (initializedLength is null)
            {
                return null;
            }

            return
            [
                new MidLevelIrDynamicStorageLengthCommit(
                    provenance.StorageAddress,
                    provenance.StorageType,
                    initializedLength)
            ];
        }

        private bool TryGetFullInitSliceCommitShape(
            TypedFunctionSignature signature,
            out int destinationIndex,
            out int countIndex)
        {
            destinationIndex = -1;
            countIndex = -1;

            if (IsMemoryFullInitSliceHelper(
                    signature,
                    "InitializeBytesDisjoint",
                    "InitializeBytes",
                    "InitializeCodePointsDisjoint",
                    "InitializeCodePoints"))
            {
                destinationIndex = 1;
                countIndex = 2;
                return true;
            }

            if (IsMemoryFullInitSliceHelper(
                    signature,
                    "InitializeBytesFromPointerDisjoint",
                    "InitializeCodePointsFromPointerDisjoint"))
            {
                destinationIndex = 2;
                countIndex = 0;
                return true;
            }

            if (IsMemoryFullInitSliceHelper(signature, "FillBytes", "FillCodePoints"))
            {
                destinationIndex = 0;
                countIndex = 2;
                return true;
            }

            return false;
        }

        private bool IsMemoryFullInitSliceHelper(
            TypedFunctionSignature signature,
            params string[] helperNames)
        {
            foreach (var candidate in EnumerateFunctionIdentityNames(signature))
            {
                foreach (var helperName in helperNames)
                {
                    if (IsMemoryFullInitSliceHelperName(candidate, helperName)
                        || (IsMemoryModule(CurrentModuleName)
                            && string.Equals(candidate, helperName, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsMemoryFullInitSliceHelperName(string candidate, string helperName)
        {
            return string.Equals(candidate, $"System.Memory.{helperName}", StringComparison.Ordinal);
        }

        private static bool IsMemoryModule(string moduleName)
        {
            return string.Equals(moduleName, "System.Memory", StringComparison.Ordinal);
        }

        private static IEnumerable<string> EnumerateFunctionIdentityNames(TypedFunctionSignature signature)
        {
            yield return signature.Name;
            if (signature.SourceName is not null)
            {
                yield return signature.SourceName;
            }

            if (signature.TemplateName is not null)
            {
                yield return signature.TemplateName;
            }
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
                Path = path,
                UsesAddressModel = target.UsesAddressModel
                    || ShouldUseProjectionAddressModel(target.RootName, path)
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

        private void ValidateBoundTextInterpolationOperation(
            BoundTextInterpolationOperation operation,
            ParserRuleContext context,
            bool usesFixedStorage,
            StarkTypeSymbol resultType,
            int segmentCount,
            int holeCount)
        {
            var boundResultType = ApplyGenericSubstitution(operation.ResultType);
            if (operation.UsesFixedStorage != usesFixedStorage
                || operation.SegmentCount != segmentCount
                || operation.HoleCount != holeCount
                || !HasSameStorageType(boundResultType, resultType))
            {
                throw LoweringInvariantViolation(
                    context,
                    $"Bound text-interpolation operation records '{boundResultType.DisplayName}' with {operation.SegmentCount} segment(s) and {operation.HoleCount} hole(s), but lowering expected '{resultType.DisplayName}' with {segmentCount} segment(s) and {holeCount} hole(s).");
            }
        }

        private void ValidateBoundTextBuildOperation(
            BoundTextBuildOperation operation,
            ParserRuleContext context,
            string buildKind,
            bool usesFixedStorage,
            StarkTypeSymbol resultType,
            int operandCount)
        {
            var boundResultType = ApplyGenericSubstitution(operation.ResultType);
            if (!string.Equals(operation.BuildKind, buildKind, StringComparison.Ordinal)
                || operation.UsesFixedStorage != usesFixedStorage
                || operation.OperandCount != operandCount
                || !HasSameStorageType(boundResultType, resultType))
            {
                throw LoweringInvariantViolation(
                    context,
                    $"Bound text-build operation records '{operation.BuildKind}' producing '{boundResultType.DisplayName}' from {operation.OperandCount} operand(s), but lowering expected '{buildKind}' producing '{resultType.DisplayName}' from {operandCount} operand(s).");
            }
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
                    throw LoweringInvariantViolation(literal, "Interpolated text literals must fold before MIR lowering.");
                }

                var inferredFoldedType = TextLiteralDecoder.CanUseUtf8Storage(foldedLiteral, TextLiteralKind.String)
                    ? StarkTypeSymbols.Ascii
                    : StarkTypeSymbols.Unicode;
                var foldedType = inferredFoldedType;
                if (TryResolveBoundTextInterpolation(literal, out var boundInterpolation))
                {
                    if (boundInterpolation.UsesFixedStorage)
                    {
                        throw LoweringInvariantViolation(literal, "Fixed text storage interpolation reached scalar literal lowering.");
                    }

                    if (!InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
                    {
                        throw LoweringInvariantViolation(literal, "Bound interpolated text literal could not be reparsed during MIR lowering.");
                    }

                    foldedType = ApplyGenericSubstitution(boundInterpolation.ResultType);
                    ValidateBoundTextInterpolationOperation(
                        boundInterpolation,
                        literal,
                        usesFixedStorage: false,
                        resultType: inferredFoldedType,
                        segmentCount: segments.Count,
                        holeCount: segments.OfType<InterpolatedTextHoleSegment>().Count());
                }

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

        private MidLevelIrOperand? ResolveNamedOperand(
            string name,
            StarkTypeSymbol? expectedType = null,
            ParserRuleContext? syntax = null)
        {
            var operand = TryResolveNamedValueOperand(name);
            if (operand is not null)
            {
                return expectedType is null ? operand : CoerceOperand(operand, expectedType);
            }

            if (TryResolveRecordedFunctionPointerPromotion(name, syntax, expectedType, out var recordedFunctionAddress))
            {
                return recordedFunctionAddress;
            }

            if (TryResolveRecordedClosureFunctionPromotion(name, syntax, expectedType, out var recordedClosureValue))
            {
                return recordedClosureValue;
            }

            if (expectedType?.Kind == StarkTypeKind.FunctionPointer
                && TryResolveFunctionAddressOperand(name, expectedType, syntax, out var functionAddress))
            {
                return functionAddress;
            }

            if (TryResolveFunctionSignature(name, out _))
            {
                throw LoweringInvariantViolation(null, $"Function '{name}' cannot be used as a value without a function-pointer target type.");
            }

            throw LoweringInvariantViolation(null, $"Named operand '{name}' could not be resolved.");
        }

        private bool TryResolveFunctionAddressOperand(
            string name,
            StarkTypeSymbol targetType,
            ParserRuleContext? syntax,
            out MidLevelIrFunctionAddressOperand operand)
        {
            operand = default!;

            if (TryResolveRecordedFunctionPointerPromotion(name, syntax, targetType, out operand))
            {
                return true;
            }

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
                .Where(function => TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(
                    targetType,
                    TypeCompatibilityFacts.FunctionPointerTypeForSignature(function)))
                .ToArray();

            if (candidates.Length != 1)
            {
                return false;
            }

            var function = candidates[0];
            operand = new MidLevelIrFunctionAddressOperand(ResolveCallTargetName(function.Name, function), targetType);
            return true;
        }

        private bool TryResolveRecordedFunctionPointerPromotion(
            string name,
            ParserRuleContext? syntax,
            StarkTypeSymbol? targetType,
            out MidLevelIrFunctionAddressOperand operand)
        {
            operand = default!;
            var location = CreateSourceLocation(syntax?.Start);
            var compatiblePromotions = _typeModel.FunctionPointerPromotions
                .Where(promotion => IsCurrentFunctionRecord(promotion.EnclosingFunctionName)
                    && (targetType?.Kind != StarkTypeKind.FunctionPointer
                        || TypeCompatibilityFacts.AreFunctionPointerTypesAssignable(targetType, promotion.TargetType)))
                .ToArray();
            var matches = Array.Empty<FunctionPointerPromotionTypingRecord>();
            if (location is not null)
            {
                matches = compatiblePromotions
                    .Where(promotion => promotion.Location.Line == location.Line
                        && promotion.Location.Column == location.Column)
                    .ToArray();
            }
            if (matches.Length == 0)
            {
                matches = compatiblePromotions
                    .Where(promotion => FunctionPointerPromotionMatchesName(promotion.Signature, name))
                    .ToArray();
            }

            if (matches.Length != 1)
            {
                return false;
            }

            var match = matches[0];
            var signature = match.Signature;
            var operandType = targetType?.Kind == StarkTypeKind.FunctionPointer ? targetType : match.TargetType;
            operand = new MidLevelIrFunctionAddressOperand(ResolveCallTargetName(signature.Name, signature), operandType);
            return true;
        }

        private static bool FunctionPointerPromotionMatchesName(TypedFunctionSignature signature, string name)
        {
            return string.Equals(signature.Name, name, StringComparison.Ordinal)
                || string.Equals(signature.DisplaySourceName, name, StringComparison.Ordinal)
                || string.Equals(signature.SourceName, name, StringComparison.Ordinal)
                || signature.Name.StartsWith($"{name}#", StringComparison.Ordinal)
                || name.EndsWith($".{signature.DisplaySourceName}", StringComparison.Ordinal)
                || signature.SourceName is not null
                    && name.EndsWith($".{signature.SourceName}", StringComparison.Ordinal);
        }

        private bool TryResolveRecordedClosureFunctionPromotion(
            string name,
            ParserRuleContext? syntax,
            StarkTypeSymbol? targetType,
            out MidLevelIrClosureValueOperand operand)
        {
            operand = default!;
            var location = CreateSourceLocation(syntax?.Start);
            var compatiblePromotions = _typeModel.ClosureFunctionPromotions
                .Where(promotion => IsCurrentFunctionRecord(promotion.EnclosingFunctionName)
                    && (targetType?.Kind != StarkTypeKind.Closure
                        || TypeCompatibilityFacts.AreClosureTypesAssignable(targetType, promotion.ClosureType)))
                .ToArray();
            var matches = Array.Empty<ClosureFunctionPromotionTypingRecord>();
            if (location is not null)
            {
                matches = compatiblePromotions
                    .Where(promotion => promotion.Location.Line == location.Line
                        && promotion.Location.Column == location.Column)
                    .ToArray();
            }

            if (matches.Length == 0)
            {
                matches = compatiblePromotions
                    .Where(promotion => FunctionPointerPromotionMatchesName(promotion.Signature, name))
                    .ToArray();
            }

            if (matches.Length != 1)
            {
                return false;
            }

            var match = matches[0];
            var operandType = targetType?.Kind == StarkTypeKind.Closure ? targetType : match.ClosureType;
            operand = new MidLevelIrClosureValueOperand(match.AdapterFunctionName, operandType);
            return true;
        }

        private bool IsCurrentFunctionRecord(string? enclosingFunctionName)
        {
            if (enclosingFunctionName is null
                || string.Equals(enclosingFunctionName, _function.Name, StringComparison.Ordinal)
                || string.Equals(enclosingFunctionName, _function.Signature.Name, StringComparison.Ordinal))
            {
                return true;
            }

            return enclosingFunctionName.EndsWith($".{_function.Name}", StringComparison.Ordinal)
                || enclosingFunctionName.EndsWith($".{_function.Signature.Name}", StringComparison.Ordinal);
        }

        private bool TryCreateClosureCapturePlaceTarget(string name, out PlaceTarget target)
        {
            target = default!;
            if (!_closureCaptureFieldsByName.TryGetValue(name, out var capture)
                || _closureEnvironmentType is null)
            {
                return false;
            }

            var environmentAddress = GetTypedClosureEnvironmentAddress();
            if (environmentAddress is null
                || !TryResolveField(_closureEnvironmentType, capture.FieldName, out _, out var fieldIndex))
            {
                return false;
            }

            var fieldTarget = new PlaceTarget(
                RootName: null,
                RootAddress: environmentAddress,
                RootValue: null,
                RootType: _closureEnvironmentType,
                Type: capture.FieldType,
                Path:
                [
                    new PlacePathSegment(
                        PlacePathKind.Field,
                        capture.FieldName,
                        fieldIndex,
                        IndexOperand: null,
                        ParentType: _closureEnvironmentType,
                        SegmentType: capture.FieldType)
                ],
                UsesAddressModel: true,
                IsAddressMutable: true);

            if (capture.StorageKind == ClosureCaptureStorageKind.Value)
            {
                var fieldAddress = BuildAddress(fieldTarget);
                if (fieldAddress is null)
                {
                    return false;
                }

                target = new PlaceTarget(
                    RootName: null,
                    RootAddress: fieldAddress,
                    RootValue: null,
                    RootType: capture.BodyType,
                    Type: capture.BodyType,
                    Path: [],
                    UsesAddressModel: true,
                    IsAddressMutable: CallableValueFacts.LambdaCaptureModeExposesWritableBinding(capture.Mode),
                    ClosureCaptureName: IsOwnedClosureCaptureFieldForDrop(capture) ? capture.Name : null);
                return true;
            }

            var capturedAddress = ReadPlace(fieldTarget);
            target = new PlaceTarget(
                RootName: null,
                RootAddress: capturedAddress,
                RootValue: null,
                RootType: capture.BodyType,
                Type: capture.BodyType,
                Path: [],
                UsesAddressModel: true,
                IsAddressMutable: capturedAddress.Type.IsMutablePointer && CallableValueFacts.LambdaCaptureModeExposesWritableBinding(capture.Mode));
            return true;
        }

        private MidLevelIrOperand? GetTypedClosureEnvironmentAddress()
        {
            if (_closureEnvironmentAddress is not null)
            {
                return _closureEnvironmentAddress;
            }

            if (_currentClosureLambda is null
                || _closureEnvironmentType is null
                || !_parametersByName.TryGetValue(CallableValueFacts.ClosureEnvironmentParameterName, out var environmentParameter))
            {
                return null;
            }

            var parameter = new MidLevelIrParameterOperand(environmentParameter.Name, environmentParameter.Type);
            var typedEnvironmentPointerType = StarkTypeSymbols.RawPointer(
                _closureEnvironmentType,
                isMutable: environmentParameter.Type.IsMutablePointer);
            _closureEnvironmentAddress = CoerceOperand(parameter, typedEnvironmentPointerType) ?? parameter;
            return _closureEnvironmentAddress;
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

            if (TryCreateClosureCapturePlaceTarget(name, out var captureTarget))
            {
                return ReadPlace(captureTarget);
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

                var suffix = $".{sourceName}";
                var uniqueSuffixMatches = _typeModel.Overloads
                    .Where(candidate => candidate.Key.EndsWith(suffix, StringComparison.Ordinal))
                    .SelectMany(static candidate => candidate.Value)
                    .ToArray();
                if (uniqueSuffixMatches.Length > 0)
                {
                    var uniqueOwnerNames = uniqueSuffixMatches
                        .Select(static candidate => candidate.Name)
                        .Distinct(StringComparer.Ordinal)
                        .Take(2)
                        .ToArray();
                    if (uniqueOwnerNames.Length == 1)
                    {
                        overloads = uniqueSuffixMatches;
                        return true;
                    }
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
            var location = CreateSourceLocation(declarationContext.Start);
            var typedDeclaration = _typeModel.LocalDeclarations.LastOrDefault(record =>
                IsCurrentFunctionRecord(record.EnclosingFunctionName)
                && TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location) == key
                && (location is null || SourceLocationStartsAt(record.Location, location)));
            typedDeclaration ??= _typeModel.LocalDeclarations
                .Where(record => TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location) == key
                    && (location is null || SourceLocationStartsAt(record.Location, location)))
                .Take(2)
                .ToArray() is [var uniqueDeclaration]
                    ? uniqueDeclaration
                    : null;
            if (typedDeclaration is not null)
            {
                type = ApplyGenericSubstitution(typedDeclaration.Type);
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
                var substitutedSignature = ApplyGenericSubstitution(publishedSignature);
                if (PublishedDirectCallNameMatches(functionName, substitutedSignature))
                {
                    signature = substitutedSignature;
                    return true;
                }
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

        private bool TryResolveRecordedDirectCallSignature(
            string functionName,
            StarkParser.ArgumentListContext arguments,
            out TypedFunctionSignature signature)
        {
            signature = null!;
            if (TryResolveBoundDirectCallOperation(functionName, arguments, out var boundOperation))
            {
                signature = ApplyGenericSubstitution(boundOperation.Signature);
                return true;
            }

            var location = CreateSourceLocation(arguments.Start);
            if (location is null)
            {
                return false;
            }

            var locationMatches = _typeModel.DirectCalls
                .Where(call => SourceLocationStartsAt(call.Location, location))
                .Select(call => ApplyGenericSubstitution(call.Signature))
                .Take(2)
                .ToArray();
            var matches = locationMatches.Length == 1
                ? locationMatches
                : locationMatches
                    .Where(call => PublishedDirectCallNameMatches(functionName, call))
                    .Take(2)
                    .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            signature = matches[0];
            return true;
        }

        private bool TryResolveRecordedMemberCallSignature(
            string memberName,
            StarkParser.ArgumentListContext arguments,
            out TypedFunctionSignature signature)
        {
            signature = null!;
            if (TryResolveBoundMemberCallOperation(memberName, arguments, out var boundOperation))
            {
                signature = ApplyGenericSubstitution(boundOperation.Signature);
                return true;
            }

            var location = CreateSourceLocation(arguments.Start);
            if (location is null)
            {
                return false;
            }

            var locationMatches = _typeModel.MemberCalls
                .Where(call => SourceLocationStartsAt(call.Location, location))
                .Select(call => ApplyGenericSubstitution(call.Signature))
                .Take(2)
                .ToArray();
            var matches = locationMatches.Length == 1
                ? locationMatches
                : locationMatches
                    .Where(call => RecordedMemberCallNameMatches(memberName, call))
                    .Take(2)
                    .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            signature = matches[0];
            return true;
        }

        private bool TryResolveBoundDirectCallOperation(
            string functionName,
            ParserRuleContext context,
            out BoundDirectCallOperation operation)
        {
            if (TryGetBoundOperation(_boundDirectCalls, context, out operation))
            {
                var signature = ApplyGenericSubstitution(operation.Signature);
                if (!PublishedDirectCallNameMatches(functionName, signature))
                {
                    throw LoweringInvariantViolation(
                        context,
                        $"Bound direct-call operation records '{signature.Name}', but lowering is resolving source call '{functionName}'.");
                }

                return true;
            }

            return false;
        }

        private bool TryResolveBoundMemberCallOperation(
            string memberName,
            ParserRuleContext context,
            out BoundMemberCallOperation operation)
        {
            if (TryGetBoundOperation(_boundMemberCalls, context, out operation))
            {
                var signature = ApplyGenericSubstitution(operation.Signature);
                if (!RecordedMemberCallNameMatches(memberName, signature))
                {
                    throw LoweringInvariantViolation(
                        context,
                        $"Bound member-call operation records '{signature.Name}', but lowering is resolving source member '{memberName}'.");
                }

                return true;
            }

            return false;
        }

        private bool TryResolveBoundFunctionPointerCallOperation(
            ParserRuleContext context,
            out BoundFunctionPointerCallOperation operation)
        {
            return TryGetBoundOperation(_boundFunctionPointerCalls, context, out operation);
        }

        private bool TryResolveBoundClosureCallOperation(
            ParserRuleContext context,
            out BoundClosureCallOperation operation)
        {
            return TryGetBoundOperation(_boundClosureCalls, context, out operation);
        }

        private bool TryResolveBoundIndexAccess(
            ParserRuleContext context,
            out BoundIndexAccessOperation operation)
        {
            return TryGetBoundOperation(_boundIndexAccesses, context, out operation);
        }

        private bool TryResolveBoundObjectCreation(
            ParserRuleContext context,
            out BoundObjectCreationOperation operation)
        {
            return TryGetBoundOperation(_boundObjectCreations, context, out operation);
        }

        private bool TryResolveBoundEnumConstruction(
            ParserRuleContext context,
            out BoundEnumConstructionOperation operation)
        {
            return TryGetBoundOperation(_boundEnumConstructions, context, out operation);
        }

        private bool TryResolveBoundEnumCall(
            ParserRuleContext context,
            out BoundEnumCallOperation operation)
        {
            return TryGetBoundOperation(_boundEnumCalls, context, out operation);
        }

        private bool TryResolveBoundEnumValue(
            ParserRuleContext context,
            out BoundEnumValueOperation operation)
        {
            return TryGetBoundOperation(_boundEnumValues, context, out operation);
        }

        private bool TryResolveBoundDynamicStorageOperation(
            ParserRuleContext context,
            out BoundDynamicStorageOperation operation)
        {
            return TryGetBoundOperation(_boundDynamicStorageOperations, context, out operation);
        }

        private bool TryResolveBoundTextInterpolation(
            ParserRuleContext context,
            out BoundTextInterpolationOperation operation)
        {
            return TryGetBoundOperation(_boundTextInterpolations, context, out operation);
        }

        private bool TryResolveBoundTextBuild(
            ParserRuleContext context,
            out BoundTextBuildOperation operation)
        {
            return TryGetBoundOperation(_boundTextBuilds, context, out operation);
        }

        private bool TryResolveBoundLayoutQuery(
            ParserRuleContext context,
            out BoundLayoutQueryOperation operation)
        {
            return TryGetBoundOperation(_boundLayoutQueries, context, out operation);
        }

        private bool TryResolveBoundSwitchDispatch(
            ParserRuleContext context,
            out BoundSwitchDispatchOperation operation)
        {
            return TryGetBoundOperation(_boundSwitchDispatches, context, out operation);
        }

        private bool TryGetBoundOperation<TOperation>(
            IReadOnlyDictionary<BoundOperationKey, TOperation> operations,
            ParserRuleContext context,
            out TOperation operation)
            where TOperation : BoundOperation
        {
            var line = context.Start.Line;
            var column = context.Start.Column + 1;
            foreach (var key in BoundOperationLookupKeys(line, column))
            {
                if (operations.TryGetValue(key, out operation!))
                {
                    return true;
                }
            }

            operation = null!;
            return false;
        }

        private IEnumerable<BoundOperationKey> BoundOperationLookupKeys(int line, int column)
        {
            foreach (var functionName in BoundOperationFunctionNames())
            {
                yield return new BoundOperationKey(functionName, _moduleFilePath, line, column);
            }

            if (!string.IsNullOrWhiteSpace(_moduleFilePath))
            {
                foreach (var functionName in BoundOperationFunctionNames())
                {
                    yield return new BoundOperationKey(functionName, null, line, column);
                }
            }
        }

        private IEnumerable<string> BoundOperationFunctionNames()
        {
            yield return _function.Name;
            if (!string.Equals(_function.Signature.Name, _function.Name, StringComparison.Ordinal))
            {
                yield return _function.Signature.Name;
            }
        }

        private static bool RecordedMemberCallNameMatches(string memberName, TypedFunctionSignature signature)
        {
            return FunctionIdentityNameMatchesMember(signature.Name, memberName)
                || signature.SourceName is not null && FunctionIdentityNameMatchesMember(signature.SourceName, memberName)
                || signature.TemplateName is not null && FunctionIdentityNameMatchesMember(signature.TemplateName, memberName)
                || FunctionIdentityNameMatchesMember(signature.DisplaySourceName, memberName);
        }

        private static bool FunctionIdentityNameMatchesMember(string functionName, string memberName)
        {
            var overloadMarker = functionName.IndexOf("#(", StringComparison.Ordinal);
            if (overloadMarker >= 0)
            {
                functionName = functionName[..overloadMarker];
            }

            return string.Equals(functionName, memberName, StringComparison.Ordinal)
                || functionName.EndsWith($".{memberName}", StringComparison.Ordinal);
        }

        private static bool SourceLocationStartsAt(SourceLocation left, SourceLocation right)
        {
            return left.Line == right.Line
                && left.Column == right.Column
                && (string.IsNullOrEmpty(left.FilePath)
                    || string.IsNullOrEmpty(right.FilePath)
                    || string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal));
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
                    && possibleNames.Any(possibleName => FunctionIdentityNameMatchesDirect(candidate, possibleName)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FunctionIdentityNameMatchesDirect(string functionName, string sourceName)
        {
            var overloadMarker = functionName.IndexOf("#(", StringComparison.Ordinal);
            if (overloadMarker >= 0)
            {
                functionName = functionName[..overloadMarker];
            }

            return string.Equals(functionName, sourceName, StringComparison.Ordinal)
                || !sourceName.Contains('.', StringComparison.Ordinal)
                    && functionName.EndsWith($".{sourceName}", StringComparison.Ordinal);
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
                        ApplyGenericSubstitution(parameter.Type),
                        parameter.IsDisjoint,
                        parameter.IsConst,
                        parameter.RawPointerElementCountExpression))
                    .ToArray(),
                TypeArguments = signature.TypeArguments is { Count: > 0 }
                    ? signature.TypeArguments.Select(ApplyGenericSubstitution).ToArray()
                    : null
            };
        }

        private TypedConstructorShape ApplyGenericSubstitution(TypedConstructorShape constructor)
        {
            if (_activeGenericTypeSubstitution is not { Count: > 0 })
            {
                return constructor;
            }

            return constructor with
            {
                Parameters = constructor.Parameters
                    .Select(parameter => new TypedParameterSymbol(
                        parameter.Name,
                        ApplyGenericSubstitution(parameter.Type),
                        parameter.IsDisjoint,
                        parameter.IsConst,
                        parameter.RawPointerElementCountExpression))
                    .ToArray()
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
                || !TryGetEnumLayoutCore(StarkTypeSymbols.Named(namedType.Name), out var resolvedLayout)
                || !resolvedLayout.TryGetVariant(variantName, out var resolvedVariant))
            {
                layout = null!;
                variant = null!;
                return false;
            }

            layout = resolvedLayout;
            variant = resolvedVariant;
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
                && _namedTypes.TryGetValue($"{CurrentModuleName}.{typeName}", out namedType!))
            {
                return true;
            }

            if (_namedTypes.TryGetValue(typeName, out namedType!))
            {
                return true;
            }

            if (!typeName.Contains('.', StringComparison.Ordinal)
                && _moduleGraph.EnumerateAccessibleModuleQualifiedNames(CurrentModuleName, typeName)
                    .Where(_namedTypes.ContainsKey)
                    .ToArray() is { Length: 1 } importedMatches)
            {
                namedType = _namedTypes[importedMatches[0]];
                return true;
            }

            if (!typeName.Contains('.', StringComparison.Ordinal))
            {
                var suffix = $".{typeName}";
                var uniqueSuffixMatches = _namedTypes
                    .Where(candidate => candidate.Key.EndsWith(suffix, StringComparison.Ordinal))
                    .Select(static candidate => candidate.Value)
                    .Take(2)
                    .ToArray();
                if (uniqueSuffixMatches.Length == 1)
                {
                    namedType = uniqueSuffixMatches[0];
                    return true;
                }
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
            StarkTypeSymbol? expectedType,
            ParserRuleContext? textBuildContext = null)
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
                    && TryBuildRuntimeTextConcatenation(
                        current,
                        next,
                        $"{operands[index - 1].GetText()} + {operands[index].GetText()}",
                        textBuildContext,
                        operands.Count,
                        out var runtimeConcat))
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
                    ? GetShiftResultType(current.Type)
                    : FindCommonType(current.Type, next.Type);
                if (requireInteger && resultType.Kind != StarkTypeKind.Integer)
                {
                    throw LoweringInvariantViolation(
                        operands[index],
                        $"Integer-only binary operator '{operatorText}' reached MIR with result type '{resultType.DisplayName}'.");
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
            ParserRuleContext? textBuildContext,
            int operandCount,
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

            BoundTextBuildOperation? boundTextBuild = null;
            if (textBuildContext is not null
                && TryResolveBoundTextBuild(textBuildContext, out var candidateTextBuild))
            {
                boundTextBuild = candidateTextBuild;
                ValidateBoundTextBuildOperation(
                    candidateTextBuild,
                    textBuildContext,
                    buildKind: "runtime-concat",
                    usesFixedStorage: false,
                    resultType: ApplyGenericSubstitution(candidateTextBuild.ResultType),
                    operandCount);
            }

            if (left is not MidLevelIrStringConstantOperand literalLeft
                || !TryGetTextLiteralLength(literalLeft.LiteralText, left.Type, out var leftLength))
            {
                return false;
            }

            var leftLengthOperand = new MidLevelIrIntegerConstantOperand(leftLength, NonNegativeI64Type);
            TypedFunctionSignature? signature = null;
            if (textBuildContext is not null
                && TryResolveBoundDirectCallOperation(sourceName, textBuildContext, out var boundCall))
            {
                signature = ApplyGenericSubstitution(boundCall.Signature);
            }

            if (signature is null)
            {
                var resolution = FunctionOverloadFacts.Resolve(
                    overloads,
                    receiverType: null,
                    [left.Type, leftLengthOperand.Type, right.Type],
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    return false;
                }

                signature = resolution.Match!;
            }

            if (boundTextBuild is not null
                && !HasSameStorageType(ApplyGenericSubstitution(boundTextBuild.ResultType), signature.ReturnType))
            {
                throw LoweringInvariantViolation(
                    textBuildContext,
                    $"Bound runtime text-build result type '{boundTextBuild.ResultType.DisplayName}' does not match helper return type '{signature.ReturnType.DisplayName}'.");
            }

            return TryBuildCall(
                signature.Name,
                signature,
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
                throw LoweringInvariantViolation(
                    null,
                    $"Comparison '{text}' reached MIR without a common operand type for '{left.Type.DisplayName}' and '{right.Type.DisplayName}'.");
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
                if (operand is MidLevelIrIntegerConstantOperand integerConstant)
                {
                    return TryMaterializeIntegerConstant(
                            integerConstant.Value,
                            integerConstant.Type,
                            targetType,
                            out var convertedConstant)
                        ? new MidLevelIrIntegerConstantOperand(convertedConstant, targetType)
                        : null;
                }

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

            if (TryCoerceFixedArrayToSlice(operand, targetType, out var fixedArraySlice))
            {
                return fixedArraySlice;
            }

            if (HasSameStorageType(operand.Type, targetType))
            {
                if (RequiresNoOpStorageRetype(operand.Type, targetType))
                {
                    return EmitTemporary(
                        new MidLevelIrConvertRValue(operand, targetType, $"{operand.Text}:{targetType.DisplayName}"),
                        "retype");
                }

                return operand;
            }

            if (targetType.Kind == StarkTypeKind.Bool && operand.Type.Kind == StarkTypeKind.Bool)
            {
                return operand;
            }

            return operand;
        }

        private bool TryCoerceFixedArrayToSlice(
            MidLevelIrOperand operand,
            StarkTypeSymbol targetType,
            out MidLevelIrOperand slice)
        {
            slice = default!;

            if (operand.Type.Kind != StarkTypeKind.FixedArray
                || targetType.Kind != StarkTypeKind.Slice
                || operand.Type.ElementType is null
                || operand.Type.FixedLength is not int fixedLength
                || targetType.ElementType is null)
            {
                return false;
            }

            if (operand is MidLevelIrLocalOperand localOperand)
            {
                EnsureAddressableLocal(localOperand.Name);
                slice = EmitTemporary(
                    new MidLevelIrMakeSliceFromLocalRValue(
                        localOperand.Name,
                        operand.Type,
                        targetType,
                        $"{localOperand.Name}:slice"),
                    "slice")!;
                return slice is not null;
            }

            var arrayAddress = TryCreateFixedArrayAddress(operand);
            if (arrayAddress is null)
            {
                return false;
            }

            var elementAddress = EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    arrayAddress,
                    operand.Type,
                    Index: null,
                    ConstantIndex: 0,
                    AddressType(targetType.ElementType, arrayAddress.Type.IsMutablePointer && targetType.IsMutableView),
                    $"{operand.Text}:slice.data"),
                "addr");
            if (elementAddress is null)
            {
                return false;
            }

            slice = EmitTemporary(
                new MidLevelIrMakeSliceFromPointerRValue(
                    elementAddress,
                    new MidLevelIrIntegerConstantOperand(fixedLength, I64Type),
                    targetType,
                    $"{operand.Text}:slice"),
                "slice")!;
            return slice is not null;
        }

        private MidLevelIrOperand? TryCreateFixedArrayAddress(MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, operand.Type),
                MidLevelIrParameterOperand parameter => CreateAddressOfParameter(parameter.Name, operand.Type),
                MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, operand.Type),
                MidLevelIrGlobalAddressOperand globalAddress when globalAddress.PointeeType.Kind == StarkTypeKind.FixedArray
                    => globalAddress,
                _ => null
            };
        }

        private static StarkTypeSymbol GetPointerBackedBorrowLoadType(StarkTypeSymbol sourceReturnType)
        {
            var valueType = StarkTypeSymbols.BorrowReturnValueType(sourceReturnType);
            return valueType.AccessKind == StarkAccessKind.Frozen
                ? StarkTypeSymbols.FreezeReachableView(valueType)
                : valueType;
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
                throw LoweringInvariantViolation(
                    null,
                    $"Equality comparison '{text}' reached MIR with unsupported comparison type '{compareType.DisplayName}'.");
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
                    throw LoweringInvariantViolation(
                        literal,
                        $"Text switch literal '{literal.GetText()}' did not lower to a string constant.");
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
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported text switch literal '{text}' did not lower to a string constant.");
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
                throw LoweringInvariantViolation(
                    null,
                    $"Partitioned text switch component extraction requires ascii/unicode, but got '{switchValue.Type.DisplayName}'.");
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
            RegisterLocal(
                name,
                value.Type,
                storageClass: "temp",
                isMutable: false,
                isConstant: false,
                hasConstProvenance: RValueHasConstProvenance(value));
            Emit(MidLevelIrStatementKind.Assign, $"{name} = {value.Text}", name, value.Type, value);
            if (value is MidLevelIrCallRValue call)
            {
                EmitPostCallDynamicLengthCommits(call);
            }

            return new MidLevelIrLocalOperand(name, value.Type);
        }

        private void EmitPostCallDynamicLengthCommits(MidLevelIrCallRValue call)
        {
            foreach (var commit in call.PostCallDynamicLengthCommits ?? [])
            {
                EmitDynamicStorageLengthCommitCore(commit, $"{call.Text}: dynamic length");
            }
        }

        private void EmitPostCallDynamicLengthCommits(MidLevelIrDirectCallStatementOperation call)
        {
            foreach (var commit in call.PostCallDynamicLengthCommits ?? [])
            {
                EmitDynamicStorageLengthCommitCore(commit, $"{call.Text}: dynamic length");
            }
        }

        private MidLevelIrOperand EmitRequiredTemporary(MidLevelIrRValue value, string hint)
        {
            return EmitTemporary(value, hint)!;
        }

        private MidLevelIrLocalOperand CreateTemporaryLocal(
            StarkTypeSymbol type,
            string hint,
            bool hasConstProvenance = false)
        {
            var name = AllocateTemporaryName(hint);
            RegisterLocal(
                name,
                type,
                storageClass: "temp",
                isMutable: false,
                isConstant: false,
                hasConstProvenance: hasConstProvenance,
                constProvenance: ConstProvenanceFacts.FromPermanentConst(hasConstProvenance));
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
            if (TryGetSimplePostfixExpression(expression) is { } dynamicReservePostfix
                && TryLowerDynamicStorageReserveExpression(dynamicReservePostfix, out var reserve))
            {
                value = reserve;
                return true;
            }

            if (TryGetSimplePostfixExpression(expression) is { } dynamicMoveLastPostfix
                && TryLowerDynamicStorageMoveLastExpression(dynamicMoveLastPostfix, out var moveLast))
            {
                value = moveLast;
                return true;
            }

            if (TryGetSimplePostfixExpression(expression) is { } dynamicMoveAtPostfix
                && TryLowerDynamicStorageMoveAtExpression(dynamicMoveAtPostfix, out var moveAt))
            {
                value = moveAt;
                return true;
            }

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

            if (TryGetSimplePostfixExpression(expression) is { } closurePostfix
                && TryLowerClosureCallExpression(closurePostfix, out var closureCall))
            {
                value = closureCall;
                return true;
            }

            return false;
        }

        private bool TryLowerDynamicStorageReserveExpression(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrRValue reserve)
        {
            reserve = default!;
            var postfixParts = expression.postfixPart();
            if (postfixParts.Length < 2
                || postfixParts[^1].argumentList() is not { } arguments
                || postfixParts[^2].Identifier()?.GetText() is not { } memberName)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundDynamicStorageOperation(arguments, out var boundOperation);
            if (!TryGetDynamicStorageOperationKind(memberName, hasBoundOperation, boundOperation, arguments, out var operationKind)
                || operationKind is not DynamicStorageOperationKind.Reserve
                    and not DynamicStorageOperationKind.TryReserve
                    and not DynamicStorageOperationKind.TryReserveCapacity)
            {
                return false;
            }

            var operationName = DynamicStorageOperationName(operationKind);
            if (hasBoundOperation && !string.Equals(operationName, memberName, StringComparison.Ordinal))
            {
                throw LoweringInvariantViolation(arguments, $"Bound dynamic-storage operation '{operationName}' does not match source member '{memberName}'.");
            }

            if (!TryResolveDynamicStorageOperationTarget(expression, postfixParts.Length - 2, out var currentValue, out var currentPlace))
            {
                return false;
            }

            var resultType = operationKind == DynamicStorageOperationKind.Reserve
                ? StarkTypeSymbols.Void
                : StarkTypeSymbols.Bool;
            ValidateBoundDynamicStorageOperation(boundOperation, hasBoundOperation, currentValue.Type, resultType, arguments);
            if (!currentPlace.IsAddressMutable)
            {
                throw LoweringInvariantViolation(expression, $"Dynamic storage {operationName} requires a mutable addressable dynamic owner.");
            }

            if (arguments.argument().Length != 1)
            {
                var argumentName = operationKind == DynamicStorageOperationKind.TryReserveCapacity ? "target-capacity" : "additional-capacity";
                throw LoweringInvariantViolation(arguments, $"Dynamic storage {operationName} expects one {argumentName} argument.");
            }

            var capacityOperand = LowerExpressionToOperand(arguments.argument(0).expression(), NonNegativeI64Type);
            if (capacityOperand is null || capacityOperand.Type.Kind != StarkTypeKind.Integer)
            {
                var argumentName = operationKind == DynamicStorageOperationKind.TryReserveCapacity ? "target-capacity" : "additional-capacity";
                throw LoweringInvariantViolation(arguments.argument(0).expression(), $"Dynamic storage {operationName} requires an integer {argumentName} operand.");
            }

            capacityOperand = CoerceOperand(capacityOperand, NonNegativeI64Type) ?? capacityOperand;
            var storageAddress = BuildAddress(currentPlace);
            if (storageAddress is null)
            {
                throw LoweringInvariantViolation(expression, $"Dynamic storage {memberName} requires an addressable dynamic owner.");
            }

            reserve = operationKind switch
            {
                DynamicStorageOperationKind.TryReserve => new MidLevelIrDynamicStorageTryReserveRValue(
                    storageAddress,
                    currentValue.Type,
                    capacityOperand,
                    expression.GetText()),
                DynamicStorageOperationKind.TryReserveCapacity => new MidLevelIrDynamicStorageTryReserveCapacityRValue(
                    storageAddress,
                    currentValue.Type,
                    capacityOperand,
                    expression.GetText()),
                DynamicStorageOperationKind.Reserve => new MidLevelIrDynamicStorageReserveRValue(
                    storageAddress,
                    currentValue.Type,
                    capacityOperand,
                    expression.GetText()),
                _ => throw LoweringInvariantViolation(arguments, $"Dynamic storage operation '{operationName}' is not a reserve-family operation.")
            };
            return true;
        }

        private bool TryLowerDynamicStorageMoveLastExpression(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrDynamicStorageMoveLastRValue moveLast)
        {
            moveLast = default!;
            var postfixParts = expression.postfixPart();
            if (postfixParts.Length < 2
                || postfixParts[^1].argumentList() is not { } arguments
                || postfixParts[^2].Identifier()?.GetText() is not { } memberName)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundDynamicStorageOperation(arguments, out var boundOperation);
            if (!TryGetDynamicStorageOperationKind(memberName, hasBoundOperation, boundOperation, arguments, out var operationKind)
                || operationKind is not DynamicStorageOperationKind.MoveLast)
            {
                return false;
            }

            var operationName = DynamicStorageOperationName(operationKind);
            if (hasBoundOperation && !string.Equals(operationName, memberName, StringComparison.Ordinal))
            {
                throw LoweringInvariantViolation(arguments, $"Bound dynamic-storage operation '{operationName}' does not match source member '{memberName}'.");
            }

            if (!TryResolveDynamicStorageOperationTarget(expression, postfixParts.Length - 2, out var currentValue, out var currentPlace))
            {
                return false;
            }

            var resultType = currentValue.Type.ElementType ?? StarkTypeSymbols.Error;
            ValidateBoundDynamicStorageOperation(boundOperation, hasBoundOperation, currentValue.Type, resultType, arguments);
            if (!currentPlace.IsAddressMutable)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage MoveLast requires a mutable addressable dynamic owner.");
            }

            if (arguments.argument().Length != 0)
            {
                throw LoweringInvariantViolation(arguments, "Dynamic storage MoveLast expects no arguments.");
            }

            var storageAddress = BuildAddress(currentPlace);
            if (storageAddress is null)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage MoveLast requires an addressable dynamic owner.");
            }

            moveLast = new MidLevelIrDynamicStorageMoveLastRValue(
                storageAddress,
                currentValue.Type,
                resultType,
                expression.GetText());
            return true;
        }

        private bool TryLowerDynamicStorageMoveAtExpression(
            StarkParser.PostfixExpressionContext expression,
            out MidLevelIrDynamicStorageMoveAtRValue moveAt)
        {
            moveAt = default!;
            var postfixParts = expression.postfixPart();
            if (postfixParts.Length < 2
                || postfixParts[^1].argumentList() is not { } arguments
                || postfixParts[^2].Identifier()?.GetText() is not { } memberName)
            {
                return false;
            }

            var hasBoundOperation = TryResolveBoundDynamicStorageOperation(arguments, out var boundOperation);
            if (!TryGetDynamicStorageOperationKind(memberName, hasBoundOperation, boundOperation, arguments, out var operationKind)
                || operationKind is not DynamicStorageOperationKind.MoveAt)
            {
                return false;
            }

            var operationName = DynamicStorageOperationName(operationKind);
            if (hasBoundOperation && !string.Equals(operationName, memberName, StringComparison.Ordinal))
            {
                throw LoweringInvariantViolation(arguments, $"Bound dynamic-storage operation '{operationName}' does not match source member '{memberName}'.");
            }

            if (!TryResolveDynamicStorageOperationTarget(expression, postfixParts.Length - 2, out var currentValue, out var currentPlace))
            {
                return false;
            }

            var resultType = currentValue.Type.ElementType ?? StarkTypeSymbols.Error;
            ValidateBoundDynamicStorageOperation(boundOperation, hasBoundOperation, currentValue.Type, resultType, arguments);
            if (!currentPlace.IsAddressMutable)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage MoveAt requires a mutable addressable dynamic owner.");
            }

            if (arguments.argument().Length != 1)
            {
                throw LoweringInvariantViolation(arguments, "Dynamic storage MoveAt expects one index argument.");
            }

            var index = LowerExpressionToOperand(arguments.argument(0).expression(), NonNegativeI64Type);
            if (index is null || index.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(arguments.argument(0).expression(), "Dynamic storage MoveAt requires an integer index operand.");
            }

            index = CoerceOperand(index, NonNegativeI64Type) ?? index;
            var storageAddress = BuildAddress(currentPlace);
            if (storageAddress is null)
            {
                throw LoweringInvariantViolation(expression, "Dynamic storage MoveAt requires an addressable dynamic owner.");
            }

            moveAt = new MidLevelIrDynamicStorageMoveAtRValue(
                storageAddress,
                currentValue.Type,
                index,
                resultType,
                expression.GetText());
            return true;
        }

        private bool TryGetDynamicStorageOperationKind(
            string memberName,
            bool hasBoundOperation,
            BoundDynamicStorageOperation boundOperation,
            ParserRuleContext context,
            out DynamicStorageOperationKind operationKind)
        {
            if (hasBoundOperation)
            {
                if (TryClassifyDynamicStorageOperationName(boundOperation.OperationName, out operationKind))
                {
                    return true;
                }

                throw LoweringInvariantViolation(
                    context,
                    $"Bound dynamic-storage operation '{boundOperation.OperationName}' has no MIR lowering case.");
            }

            return TryClassifyDynamicStorageOperationName(memberName, out operationKind);
        }

        private static bool TryClassifyDynamicStorageOperationName(
            string operationName,
            out DynamicStorageOperationKind operationKind)
        {
            switch (operationName)
            {
                case "Reserve":
                    operationKind = DynamicStorageOperationKind.Reserve;
                    return true;
                case "TryReserve":
                    operationKind = DynamicStorageOperationKind.TryReserve;
                    return true;
                case "TryReserveCapacity":
                    operationKind = DynamicStorageOperationKind.TryReserveCapacity;
                    return true;
                case "MoveLast":
                    operationKind = DynamicStorageOperationKind.MoveLast;
                    return true;
                case "MoveAt":
                    operationKind = DynamicStorageOperationKind.MoveAt;
                    return true;
                default:
                    operationKind = default;
                    return false;
            }
        }

        private static string DynamicStorageOperationName(DynamicStorageOperationKind operationKind)
        {
            return operationKind switch
            {
                DynamicStorageOperationKind.Reserve => "Reserve",
                DynamicStorageOperationKind.TryReserve => "TryReserve",
                DynamicStorageOperationKind.TryReserveCapacity => "TryReserveCapacity",
                DynamicStorageOperationKind.MoveLast => "MoveLast",
                DynamicStorageOperationKind.MoveAt => "MoveAt",
                _ => throw new InvalidOperationException($"Unsupported dynamic-storage operation kind '{operationKind}'.")
            };
        }

        private void ValidateBoundDynamicStorageOperation(
            BoundDynamicStorageOperation operation,
            bool hasBoundOperation,
            StarkTypeSymbol receiverType,
            StarkTypeSymbol resultType,
            StarkParser.ArgumentListContext arguments)
        {
            if (!hasBoundOperation)
            {
                return;
            }

            if (!HasSameStorageType(ApplyGenericSubstitution(operation.ReceiverType), receiverType)
                || !HasSameStorageType(ApplyGenericSubstitution(operation.ResultType), resultType)
                || operation.ArgumentCount != arguments.argument().Length)
            {
                throw LoweringInvariantViolation(
                    arguments,
                    $"Bound dynamic-storage operation '{operation.OperationName}' does not match lowered receiver type '{receiverType.DisplayName}', result type '{resultType.DisplayName}', or source arity.");
            }
        }

        private bool TryResolveDynamicStorageOperationTarget(
            StarkParser.PostfixExpressionContext expression,
            int operationMemberPartIndex,
            out MidLevelIrOperand currentValue,
            out PlaceTarget currentPlace)
        {
            currentValue = default!;
            currentPlace = default!;
            var postfixParts = expression.postfixPart();
            if (!TryInitializePostfixState(expression.primaryExpression(), out var current, out var currentName))
            {
                return false;
            }

            PlaceTarget? place = current is null ? null : CreateRootPlaceTarget(current);
            for (var index = 0; index < operationMemberPartIndex; index++)
            {
                var postfixPart = postfixParts[index];
                if (postfixPart.argumentList() is not null || postfixPart.GetChild(0).GetText() == "[")
                {
                    return false;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (current is not null)
                {
                    place = place is not null && TryAppendFieldPlaceTarget(place, memberName, out var fieldPlace)
                        ? fieldPlace
                        : null;
                    current = place is { UsesAddressModel: true }
                        ? ReadPlace(place)
                        : TryLowerPublishedFieldAccess(current, postfixPart, out var publishedFieldAccess)
                            ? publishedFieldAccess
                            : LowerFieldAccess(current, memberName);
                    if (current is null)
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
                current = TryResolveNamedValueOperand(qualifiedName);
                if (current is not null)
                {
                    currentName = null;
                    place = CreateRootPlaceTarget(current);
                }
                else
                {
                    currentName = qualifiedName;
                }
            }

            if (current?.Type.Kind != StarkTypeKind.Dynamic || place is null)
            {
                return false;
            }

            currentValue = current;
            currentPlace = place;
            return true;
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

        private static bool TryGetRawPointerRegionExpression(
            StarkParser.ExpressionContext expression,
            out string rootName,
            out StarkParser.ExpressionContext startExpression,
            out StarkParser.ExpressionContext lengthExpression)
        {
            rootName = string.Empty;
            startExpression = null!;
            lengthExpression = null!;

            if (TryGetSimplePostfixExpression(expression) is not { } postfix
                || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier
                || postfix.postfixPart() is not [var indexPart]
                || indexPart.LBRACK() is null
                || indexPart.expressionList()?.expression() is not [var start, var length])
            {
                return false;
            }

            rootName = identifier;
            startExpression = start;
            lengthExpression = length;
            return true;
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

        private bool EmitAssignmentFromExpression(
            string targetName,
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            string text)
        {
            var operand = LowerExpressionToOperand(expression, targetType);
            if (operand is null)
            {
                throw LoweringInvariantViolation(
                    expression,
                    $"Variable initializer '{text}' for local '{targetName}' did not lower to a MIR operand.");
            }

            Emit(
                MidLevelIrStatementKind.Assign,
                $"{targetName} = {text}",
                targetName,
                targetType,
                new MidLevelIrUseRValue(operand),
                writeKind: MemoryWriteKind.Initialization);
            RecordMoveFromOperand(operand, targetType);
            return OperandHasConstProvenance(operand);
        }

        private string DeclareLocal(string sourceName, StarkTypeSymbol type, string storageClass, bool isMutable, bool isConstant)
        {
            var localName = AllocateLocalStorageName(sourceName);
            RegisterLocal(localName, type, storageClass, isMutable, isConstant);
            TrackDeclaredLocal(localName, type);
            if (!string.Equals(localName, sourceName, StringComparison.Ordinal))
            {
                PushScopedNameAlias(sourceName, localName);
            }

            return localName;
        }

        private string AllocateLocalStorageName(string sourceName)
        {
            if (!_localsByName.ContainsKey(sourceName)
                && !_parametersByName.ContainsKey(sourceName))
            {
                return sourceName;
            }

            var sanitized = SanitizeLocalNameHint(sourceName);
            string candidate;
            do
            {
                candidate = $"$local{_nextScopedLocalId}_{sanitized}";
                _nextScopedLocalId++;
            }
            while (_localsByName.ContainsKey(candidate) || _parametersByName.ContainsKey(candidate));

            return candidate;
        }

        private static string SanitizeLocalNameHint(string sourceName)
        {
            var chars = sourceName
                .Select(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_')
                .ToArray();
            return chars.Length == 0 ? "local" : new string(chars);
        }

        private void PushScopedNameAlias(string sourceName, string localName)
        {
            if (_scopes.Count == 0)
            {
                _nameAliases[sourceName] = localName;
                return;
            }

            var scope = _scopes.Peek();
            if (!scope.NameAliases.Any(alias => string.Equals(alias.AliasName, sourceName, StringComparison.Ordinal)))
            {
                scope.NameAliases.Add((
                    sourceName,
                    _nameAliases.TryGetValue(sourceName, out var previousAlias) ? previousAlias : null,
                    _nameAliases.ContainsKey(sourceName)));
            }

            _nameAliases[sourceName] = localName;
        }

        private void RestoreScopedNameAliases(ScopeFrame scope)
        {
            for (var index = scope.NameAliases.Count - 1; index >= 0; index--)
            {
                var (aliasName, previousAlias, hadAlias) = scope.NameAliases[index];
                if (hadAlias)
                {
                    _nameAliases[aliasName] = previousAlias!;
                }
                else
                {
                    _nameAliases.Remove(aliasName);
                }
            }
        }

        private void RegisterLocal(
            string name,
            StarkTypeSymbol type,
            string storageClass,
            bool isMutable,
            bool isConstant,
            bool hasConstProvenance = false,
            ConstProvenanceKind constProvenance = ConstProvenanceKind.None)
        {
            if (_localsByName.ContainsKey(name))
            {
                return;
            }

            constProvenance = ConstProvenanceFacts.Normalize(hasConstProvenance, constProvenance);
            var local = new MidLevelIrLocal(
                name,
                type,
                storageClass,
                isMutable,
                isConstant,
                IsAddressable: ShouldAddressLocal(type, storageClass),
                Location: _currentStatementLocation ?? _functionLocation,
                HasConstProvenance: ConstProvenanceFacts.HasPermanentConstProvenance(constProvenance),
                ConstProvenance: constProvenance);
            _locals.Add(local);
            _localsByName[name] = local;
        }

        private void MarkLocalHasConstProvenance(string name)
        {
            if (!_localsByName.TryGetValue(name, out var local) || local.HasConstProvenance)
            {
                return;
            }

            var updated = local with
            {
                HasConstProvenance = true,
                ConstProvenance = ConstProvenanceKind.PermanentConst
            };
            _localsByName[name] = updated;
            for (var index = 0; index < _locals.Count; index++)
            {
                if (string.Equals(_locals[index].Name, name, StringComparison.Ordinal))
                {
                    _locals[index] = updated;
                    return;
                }
            }
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
            if (!CanUseOperandAsIndirectArgumentSource(parameterType, argument.Type))
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
            if (target is null
                || !CanUseOperandAsIndirectArgumentSource(parameterType, target.Type))
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
                || parameterType.Kind == StarkTypeKind.Closure
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

        private static bool CanUseOperandAsIndirectArgumentSource(StarkTypeSymbol parameterType, StarkTypeSymbol operandType)
        {
            if (!RequiresIndirectArgument(parameterType))
            {
                return false;
            }

            var parameterStorageType = StarkTypeSymbols.WithQualifiers(
                parameterType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            var operandStorageType = StarkTypeSymbols.WithQualifiers(
                operandType,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);

            return HasSameStorageType(parameterStorageType, operandStorageType);
        }

        private void Emit(
            MidLevelIrStatementKind kind,
            string text,
            string? targetName = null,
            StarkTypeSymbol? targetType = null,
            MidLevelIrRValue? value = null,
            MidLevelIrOperand? address = null,
            MemoryWriteKind writeKind = MemoryWriteKind.Replacement,
            MidLevelIrCallStatementOperation? call = null)
        {
            ValidateStatementShape(kind, targetName, targetType, value, address, call);
            CurrentBlock.Statements.Add(new MidLevelIrStatement(
                kind,
                text,
                targetName,
                targetType,
                address,
                value,
                _currentStatementLocation ?? _functionLocation,
                CurrentScopedNoAliasGroups(),
                CurrentLoopAccessGroups(),
                writeKind,
                call));
        }

        private void ValidateStatementShape(
            MidLevelIrStatementKind kind,
            string? targetName,
            StarkTypeSymbol? targetType,
            MidLevelIrRValue? value,
            MidLevelIrOperand? address,
            MidLevelIrCallStatementOperation? call)
        {
            switch (kind)
            {
                case MidLevelIrStatementKind.StorageLive:
                case MidLevelIrStatementKind.StorageDead:
                    if (targetName is null || targetType is null || value is not null || address is not null || call is not null)
                    {
                        throw LoweringInvariantViolation(
                            null,
                            $"Storage statement '{kind}' requires a target name/type and no value, call, or address.");
                    }

                    return;

                case MidLevelIrStatementKind.Assign:
                    if (targetName is null || targetType is null || value is null || address is not null || call is not null)
                    {
                        throw LoweringInvariantViolation(
                            null,
                            "MIR assignment requires a target name/type, a value, and no indirect address or statement call.");
                    }

                    return;

                case MidLevelIrStatementKind.StoreIndirect:
                    if (targetType is null || value is null || address is null || targetName is not null || call is not null)
                    {
                        throw LoweringInvariantViolation(
                            null,
                            "MIR indirect store requires a pointee type, address, value, and no direct target name or statement call.");
                    }

                    return;

                case MidLevelIrStatementKind.Evaluate:
                    if ((value is null) == (call is null)
                        || targetName is not null
                        || targetType is not null
                        || address is not null)
                    {
                        throw LoweringInvariantViolation(
                            null,
                            "MIR evaluate statement requires exactly one value or statement call and no assignment target.");
                    }

                    if (value is MidLevelIrCallRValue { Type.Kind: StarkTypeKind.Void }
                        || value is MidLevelIrIndirectCallRValue { Type.Kind: StarkTypeKind.Void })
                    {
                        throw LoweringInvariantViolation(
                            null,
                            "MIR void calls must use the statement-only call operation slot.");
                    }

                    return;

                default:
                    throw LoweringInvariantViolation(null, $"MIR statement kind '{kind}' has no validation case.");
            }
        }

        private IReadOnlyList<ScopedNoAliasGroup>? CurrentScopedNoAliasGroups()
        {
            return _activeScopedNoAliasGroups.Count == 0
                ? null
                : _activeScopedNoAliasGroups.Reverse().ToArray();
        }

        private IReadOnlyList<string>? CurrentLoopAccessGroups()
        {
            return _activeLoopAccessGroups.Count == 0
                ? null
                : _activeLoopAccessGroups.Reverse().ToArray();
        }

        private void EnsureGoto(
            int targetBlockId,
            IReadOnlyList<string>? loopContracts = null,
            IReadOnlyList<string>? loopAccessGroups = null,
            string? loopBehavior = null)
        {
            if (!CurrentBlock.HasTerminator)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Goto,
                    [targetBlockId],
                    LoopBehavior: loopBehavior,
                    LoopContracts: loopContracts is { Count: > 0 } ? loopContracts : null,
                    LoopAccessGroups: loopAccessGroups is { Count: > 0 } ? loopAccessGroups : null);
            }
        }

        private IReadOnlyList<string>? CreateIndependentLoopAccessGroups(IReadOnlyList<string>? loopContracts)
        {
            return loopContracts is { Count: > 0 }
                && loopContracts.Contains("independent", StringComparer.Ordinal)
                ? [$"independent-loop-{_nextLoopAccessGroupId++}"]
                : null;
        }

        private static IReadOnlyList<string>? GetLoopContractNames(IEnumerable<StarkParser.LoopContractContext> contracts)
        {
            var names = contracts
                .Select(static contract => contract.GetText())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return names.Length == 0 ? null : names;
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

        private InvalidOperationException LoweringInvariantViolation(
            ParserRuleContext? syntax,
            string reason,
            [CallerMemberName] string caller = "")
        {
            var location = CreateSourceLocation(syntax?.Start) ?? _functionLocation;
            var locationText = location.FilePath is null
                ? $"{location.Line}:{location.Column}"
                : $"{location.FilePath}:{location.Line}:{location.Column}";
            return new InvalidOperationException(
                $"MIR lowering invariant violated in '{caller}' for '{_function.Name}' at {locationText}: {reason}");
        }

        private SourceLocation? CreateSourceLocation(IToken? token)
        {
            return token is null
                ? null
                : new SourceLocation(_moduleFilePath, token.Line, token.Column + 1);
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
                if (StarkTypeSymbols.IsCompileTimeInteger(left)
                    || StarkTypeSymbols.IsCompileTimeInteger(right))
                {
                    return StarkTypeSymbols.CompileTimeInteger;
                }

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

        private static StarkTypeSymbol GetShiftResultType(StarkTypeSymbol left)
        {
            return left.Kind == StarkTypeKind.Integer && left.BitWidth is int bitWidth && bitWidth > 0
                ? StarkTypeSymbols.Integer(bitWidth, isUnsigned: left.IsUnsigned)
                : left;
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
                StarkTypeKind.Slice => left.ElementType is not null
                    && right.ElementType is not null
                    && HasSameStorageType(left.ElementType, right.ElementType),
                StarkTypeKind.FixedArray => left.FixedLength == right.FixedLength
                    && left.ElementType is not null
                    && right.ElementType is not null
                    && HasSameStorageType(left.ElementType, right.ElementType),
                StarkTypeKind.Dynamic => left.ElementType is not null
                    && right.ElementType is not null
                    && HasSameStorageType(left.ElementType, right.ElementType),
                _ => left.DisplayName == right.DisplayName
            };
        }

        private static bool RequiresNoOpStorageRetype(StarkTypeSymbol sourceType, StarkTypeSymbol targetType)
        {
            return sourceType.Kind == StarkTypeKind.Slice
                && targetType.Kind == StarkTypeKind.Slice
                && sourceType != targetType;
        }

        private bool IsAddressableLocal(string name)
        {
            return _localsByName.TryGetValue(name, out var local) && local.IsAddressable;
        }

        private bool ShouldUseHeapProjectionAddressModel(MidLevelIrOperand? root, IReadOnlyList<PlacePathSegment> path)
        {
            return path.Count > 0
                && root is MidLevelIrLocalOperand local
                && IsHeapLocal(local.Name);
        }

        private bool ShouldUseHeapProjectionAddressModel(string? rootName, IReadOnlyList<PlacePathSegment> path)
        {
            return path.Count > 0 && IsHeapLocal(rootName);
        }

        private bool ShouldUseProjectionAddressModel(MidLevelIrOperand? root, IReadOnlyList<PlacePathSegment> path)
        {
            return ShouldUseHeapProjectionAddressModel(root, path)
                || path.Count > 0
                    && (root is MidLevelIrGlobalAddressOperand
                        || root is MidLevelIrGlobalOperand global && IsMutableGlobalRoot(global.Name))
                || IsLargeAggregateProjectionAddressRoot(root, path);
        }

        private bool ShouldUseProjectionAddressModel(string? rootName, IReadOnlyList<PlacePathSegment> path)
        {
            return ShouldUseHeapProjectionAddressModel(rootName, path)
                || path.Count > 0 && rootName is not null && IsMutableGlobalRoot(rootName)
                || IsLargeAggregateProjectionAddressRoot(rootName, path);
        }

        private bool IsLargeAggregateProjectionAddressRoot(MidLevelIrOperand? root, IReadOnlyList<PlacePathSegment> path)
        {
            return path.Count > 0
                && root is MidLevelIrLocalOperand or MidLevelIrParameterOperand
                && IsLargeAggregateProjectionAddressType(ProjectRootType(root));
        }

        private bool IsLargeAggregateProjectionAddressRoot(string? rootName, IReadOnlyList<PlacePathSegment> path)
        {
            if (path.Count == 0 || rootName is null)
            {
                return false;
            }

            StarkTypeSymbol rootType;
            if (_localsByName.TryGetValue(rootName, out var local))
            {
                rootType = local.Type;
            }
            else if (_parametersByName.TryGetValue(rootName, out var parameter))
            {
                rootType = parameter.Type;
            }
            else
            {
                return false;
            }

            return IsLargeAggregateProjectionAddressType(rootType);
        }

        private bool IsLargeAggregateProjectionAddressType(StarkTypeSymbol type)
        {
            if (type.Kind is not (StarkTypeKind.Named or StarkTypeKind.FixedArray))
            {
                return false;
            }

            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                type,
                _namedTypes,
                _enumLayoutModel.Layouts);
            return layout is { SizeBytes: >= LargeAggregateProjectionAddressThresholdBytes };
        }

        private bool IsMutableGlobalRoot(string name)
        {
            return _typeModel.Globals.TryGetValue(name, out var global) && global.IsMutable;
        }

        private bool IsHeapLocal(string? name)
        {
            return name is not null
                && _localsByName.TryGetValue(name, out var local)
                && string.Equals(local.StorageClass, "heap", StringComparison.Ordinal);
        }

        private static bool SupportsAddressModel(MidLevelIrOperand? operand)
        {
            return operand switch
            {
                MidLevelIrObjectConstructionOperand construction => SupportsAddressModel(construction.Value),
                MidLevelIrEnumConstructionOperand construction => SupportsAddressModel(construction.Value),
                MidLevelIrLocalOperand or MidLevelIrParameterOperand or MidLevelIrGlobalOperand or MidLevelIrGlobalAddressOperand => true,
                _ => false
            };
        }

        private bool IsBorrowParameterRoot(MidLevelIrOperand? operand)
        {
            return operand switch
            {
                MidLevelIrObjectConstructionOperand construction => IsBorrowParameterRoot(construction.Value),
                MidLevelIrEnumConstructionOperand construction => IsBorrowParameterRoot(construction.Value),
                MidLevelIrParameterOperand parameter =>
                    _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                    && RequiresIndirectArgument(parameterBinding.Type),
                _ => false
            };
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
                ? CanFormMutableAddressFromParameter(parameter)
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
            return operand is MidLevelIrObjectConstructionOperand objectConstruction
                    ? UsesFrozenProjectionSemantics(objectConstruction.Value)
                : operand is MidLevelIrEnumConstructionOperand enumConstruction
                    ? UsesFrozenProjectionSemantics(enumConstruction.Value)
                : operand.Type.AccessKind == StarkAccessKind.Frozen
                || operand is MidLevelIrLocalOperand local
                    && _localsByName.TryGetValue(local.Name, out var localBinding)
                    && localBinding.HasConstProvenance
                || operand is MidLevelIrParameterOperand parameter
                    && _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                    && parameterBinding.IsConst
                || operand is MidLevelIrGlobalOperand global
                    && TryResolveGlobal(global.Name, out var binding)
                    && binding.IsConst;
        }

        private bool OperandHasConstProvenance(MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrParameterOperand parameter =>
                    _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                    && parameterBinding.IsConst,
                MidLevelIrGlobalOperand global =>
                    TryResolveGlobal(global.Name, out var globalBinding)
                    && globalBinding.IsConst,
                MidLevelIrGlobalAddressOperand globalAddress =>
                    TryResolveGlobal(globalAddress.Name, out var globalBinding)
                    && globalBinding.IsConst,
                MidLevelIrLocalOperand local =>
                    _localsByName.TryGetValue(local.Name, out var localBinding)
                    && localBinding.HasConstProvenance,
                MidLevelIrObjectConstructionOperand construction =>
                    OperandHasConstProvenance(construction.Value),
                MidLevelIrEnumConstructionOperand construction =>
                    OperandHasConstProvenance(construction.Value),
                _ => false
            };
        }

        private bool RValueHasConstProvenance(MidLevelIrRValue value)
        {
            return value switch
            {
                MidLevelIrUseRValue use => OperandHasConstProvenance(use.Operand),
                MidLevelIrConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                                && convert.TargetType.Kind == StarkTypeKind.RawPointer
                    => OperandHasConstProvenance(convert.Operand),
                MidLevelIrExtractFieldRValue extractField => OperandHasConstProvenance(extractField.Target),
                MidLevelIrExtractIndexRValue extractIndex => OperandHasConstProvenance(extractIndex.Target),
                MidLevelIrMakeSliceFromLocalRValue makeSlice =>
                    _localsByName.TryGetValue(makeSlice.LocalName, out var localBinding)
                    && localBinding.HasConstProvenance,
                MidLevelIrMakeSliceFromPointerRValue makeSlice => OperandHasConstProvenance(makeSlice.Pointer),
                MidLevelIrLoadSliceElementRValue loadSlice => OperandHasConstProvenance(loadSlice.Slice),
                MidLevelIrTextSliceRValue textSlice => OperandHasConstProvenance(textSlice.TextValue),
                MidLevelIrAddressOfLocalRValue addressOfLocal =>
                    _localsByName.TryGetValue(addressOfLocal.LocalName, out var localBinding)
                    && localBinding.HasConstProvenance,
                MidLevelIrAddressOfParameterRValue addressOfParameter =>
                    _parametersByName.TryGetValue(addressOfParameter.ParameterName, out var parameterBinding)
                    && parameterBinding.IsConst,
                MidLevelIrFieldAddressRValue fieldAddress => OperandHasConstProvenance(fieldAddress.Address),
                MidLevelIrElementAddressRValue elementAddress => OperandHasConstProvenance(elementAddress.Address),
                MidLevelIrSliceElementAddressRValue sliceElementAddress => OperandHasConstProvenance(sliceElementAddress.Slice),
                MidLevelIrLoadIndirectRValue loadIndirect => OperandHasConstProvenance(loadIndirect.Address),
                _ => false
            };
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

        private static string CreateScopedNoAliasParameterRootKey(string parameterName) => $"param:{parameterName}";

        private bool TryCreateScopedNoAliasParameterRegionRootKey(
            string parameterName,
            StarkParser.ExpressionContext startExpression,
            StarkParser.ExpressionContext lengthExpression,
            out string rootKey)
        {
            rootKey = string.Empty;
            if (!TryResolveParameterIntegerRange(startExpression, out var startMin, out var startMax)
                || !TryResolveParameterIntegerRange(lengthExpression, out _, out var lengthMax)
                || startMin < BigInteger.Zero
                || lengthMax <= BigInteger.Zero)
            {
                return false;
            }

            var rangeMax = startMax + lengthMax - BigInteger.One;
            if (rangeMax < startMin)
            {
                return false;
            }

            rootKey = $"{CreateScopedNoAliasParameterRootKey(parameterName)}[{startMin.ToString(CultureInfo.InvariantCulture)}..{rangeMax.ToString(CultureInfo.InvariantCulture)}]";
            return true;
        }

        private bool TryResolveParameterIntegerRange(
            StarkParser.ExpressionContext expression,
            out BigInteger min,
            out BigInteger max)
        {
            var text = expression.GetText();
            if (BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var literal))
            {
                min = literal;
                max = literal;
                return true;
            }

            if (_parametersByName.TryGetValue(text, out var parameter)
                && parameter.Type.Kind == StarkTypeKind.Integer
                && parameter.Type.RangeMin is { } rangeMin
                && parameter.Type.RangeMax is { } rangeMax)
            {
                min = rangeMin;
                max = rangeMax;
                return true;
            }

            min = default;
            max = default;
            return false;
        }

        private bool GetAddressMutability(MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrObjectConstructionOperand construction => GetAddressMutability(construction.Value),
                MidLevelIrEnumConstructionOperand construction => GetAddressMutability(construction.Value),
                MidLevelIrLocalOperand local => _localsByName.TryGetValue(local.Name, out var localBinding)
                ? CanFormMutableAddressFromLocal(localBinding)
                : true,
                MidLevelIrGlobalOperand global => _typeModel.Globals.TryGetValue(global.Name, out var globalBinding)
                    ? globalBinding.IsMutable && CanMutateThroughType(globalBinding.Type)
                    : true,
                MidLevelIrParameterOperand parameter => _parametersByName.TryGetValue(parameter.Name, out var parameterBinding)
                    ? CanFormMutableAddressFromParameter(parameterBinding)
                    : CanFormMutableAddressFromParameter(parameter.Type),
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
                && !local.HasConstProvenance
                && local.Type.AccessKind != StarkAccessKind.Frozen
                && (local.IsMutable || local.Type.IsMutableView || local.Type.InitializationKind != StarkInitializationKind.None);
        }

        private static bool CanFormMutableAddressFromParameter(TypedParameterSymbol parameter)
        {
            return !parameter.IsConst
                && CanFormMutableAddressFromParameter(parameter.Type);
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

            return StarkTypeSymbols.CompileTimeInteger;
        }

        private static bool TryMaterializeIntegerConstant(
            BigInteger value,
            StarkTypeSymbol sourceType,
            StarkTypeSymbol targetType,
            out BigInteger converted)
        {
            converted = value;
            if (StarkTypeSymbols.IsCompileTimeInteger(targetType))
            {
                return true;
            }

            if (StarkTypeSymbols.IntegerValueFitsEffectiveRange(value, targetType))
            {
                return true;
            }

            if (StarkTypeSymbols.IsCompileTimeInteger(sourceType)
                || targetType.BitWidth is not int bitWidth
                || bitWidth <= 0)
            {
                return false;
            }

            var modulus = BigInteger.One << bitWidth;
            var normalized = ((value % modulus) + modulus) % modulus;
            converted = targetType.IsUnsigned
                ? normalized
                : FromTwosComplement(normalized, bitWidth);
            return StarkTypeSymbols.IntegerValueFitsEffectiveRange(converted, targetType);
        }

        private static BigInteger FromTwosComplement(BigInteger value, int bitWidth)
        {
            var signBit = BigInteger.One << (bitWidth - 1);
            return (value & signBit) != 0
                ? value - (BigInteger.One << bitWidth)
                : value;
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

        private readonly record struct LoopTargets(
            int ContinueTarget,
            int BreakTarget,
            int ScopeDepth,
            string? ContinueLoopBehavior,
            IReadOnlyList<string>? ContinueLoopContracts,
            IReadOnlyList<string>? ContinueLoopAccessGroups);
        private readonly record struct BreakTargets(int Target, int ScopeDepth);
        private readonly record struct ConstructorReturnTarget(int ExitBlockId, int ScopeDepth);
    }
}
