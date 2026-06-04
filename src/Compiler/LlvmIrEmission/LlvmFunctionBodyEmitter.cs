using System.Numerics;
using System.Globalization;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private const string AsciiEqualityHelperName = "__stark_ascii_equal";
    private const string UnicodeEqualityHelperName = "__stark_unicode_equal";
    private const string AsciiCompareHelperName = "__stark_ascii_compare";
    private const string UnicodeCompareHelperName = "__stark_unicode_compare";
    private const string FixedArrayCompareHelperNamePrefix = "__stark_fixed_array_compare_";
    private const string ScalarizedAggregateCompareHelperNamePrefix = "__stark_named_compare_";
    private const string IntegerExponentHelperNamePrefix = "__stark_int_pow_i";
    private const string HeapAllocateHelperName = "__stark_heap_alloc";
    private const string HeapFreeHelperName = "__stark_heap_free";
    private const string RuntimeAllocateHelperName = "__stark_runtime_alloc";
    private const string RuntimeReallocateHelperName = "__stark_runtime_realloc";
    private const string RuntimeTryReallocateHelperName = "__stark_runtime_try_realloc";
    private const string RuntimeFreeHelperName = "__stark_runtime_free";
    private const string DynamicStorageAllocateHelperName = "__stark_dynamic_alloc";
    private const string DynamicStorageReserveHelperName = "__stark_dynamic_reserve";
    private const string DynamicStorageTryReserveHelperName = "__stark_dynamic_try_reserve";
    private const string DynamicStorageTryReserveCapacityHelperName = "__stark_dynamic_try_reserve_capacity";
    private const string DynamicStorageMoveLastPointerHelperName = "__stark_dynamic_move_last_ptr";
    private const string DynamicStorageMoveAtToOutHelperName = "__stark_dynamic_move_at_to_out";
    private const string UnreachableTrapHelperName = "__stark_unreachable_trap";
    private const int AggregateScalarizationThresholdBytes = 16;
    private const int AggregateScalarizationMaxLeafCount = 4;
    private const int AggregateMemcpyThresholdBytes = 32;
    private const int AggregateInlineMemcpyThresholdBytes = 256;
    private const int TbaaFixedArrayFieldLimit = 64;
    private const int TrapEdgeUnlikelyWeight = 1;
    private const int NormalEdgeLikelyWeight = 2000;
    private const int HotIntentLikelyWeight = 2000;
    private const int ExplicitIntentNeutralWeight = 100;

    private readonly StringBuilder _builder;
    private readonly TypedFunctionSignature _function;
    private readonly AbiFunctionSignature _abiFunction;
    private readonly Func<string, string, AbiFunctionSignature?> _resolveCallAbi;
    private readonly SsaFunction _ssaFunction;
    private readonly LlvmEmissionContext _context;
    private readonly LlvmFunctionAttributeBuilder _attributeBuilder;
    private readonly DebugFunctionContext? _debugFunction;
    private readonly bool _isStrictFp;
    private readonly ResolveParameterEffectsDelegate? _resolveParameterEffects;
    private readonly ResolveFunctionMemoryEffectsDelegate? _resolveFunctionMemoryEffects;
    private readonly HashSet<string> _referencedValueNames;
    private readonly HashSet<string> _addressTakenParameterNames;
    private readonly IReadOnlyDictionary<string, SsaRValue> _valueDefinitions;
    private readonly IReadOnlyDictionary<string, SsaPhi> _phisByResultName;
    private readonly IReadOnlyDictionary<string, SsaValue> _trivialValueAliases;
    private readonly IReadOnlyDictionary<string, SsaInstructionPosition> _valueDefinitionPositions;
    private readonly IReadOnlyDictionary<string, SsaValueFacts> _valueFacts;
    private readonly IReadOnlyDictionary<int, SsaBasicBlock> _blocksById;
    private readonly IReadOnlyDictionary<int, int> _blockOrderById;
    private readonly IReadOnlyDictionary<int, int> _predecessorCounts;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<LlvmAssumeFact>> _assumptionsByBlock;
    private readonly HashSet<string> _tbaaUnsafeAddressRoots;
    private readonly HashSet<string> _scopedNoAliasUnsafeAddressRoots;
    private readonly IReadOnlyDictionary<string, string> _sameParameterCanonicalRootKeys;
    private readonly ScopedNoAliasMetadataModel? _scopedNoAliasMetadata;
    private readonly IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? _parameterEffects;
    private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;
    private readonly bool _enableOptimizedRawPointerLoopIntrinsics;
    private readonly HashSet<string> _allocatedLocalSlots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _constProvenanceLocalNames;
    private readonly HashSet<string> _invariantLocalNames;
    private readonly IReadOnlyDictionary<string, SsaValue> _singleStoreLocalValues;
    private readonly HashSet<string> _tailCallResultNames;
    private readonly List<string> _entryStaticAllocas = [];
    private readonly Dictionary<string, string> _localStorageClasses;
    private readonly Dictionary<string, bool> _aggregateValueMaterializationRequirements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _indirectAggregateValueSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalSlotAlias> _localSlotAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directAggregateAliasCandidateLocalNames;
    private readonly Dictionary<string, SsaAllocateLocalInstruction> _deferredAliasLocalAllocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SsaLifetimeStartInstruction> _deferredAliasLifetimeStarts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _materializedAliasCandidateLocalNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _materializedParameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _valueAliases = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<int, RawPointerLoopIntrinsicPlan> _embeddedOptimizedRawPointerLoopPlansByPreheader =
        new Dictionary<int, RawPointerLoopIntrinsicPlan>();
    private HashSet<int> _embeddedOptimizedRawPointerLoopSkippedBlockIds = new();
    private HashSet<int> _embeddedOptimizedRawPointerLoopExitBlockIds = new();
    private readonly Dictionary<int, string> _blockExitLabels = [];
    private SourceLocation? _currentDebugLocation;
    private SsaBasicBlock? _currentBlock;
    private int? _entryStaticAllocaInsertionIndex;
    private int _currentInstructionIndex = -1;
    private int _nextAbiTempId;
    private int _nextAssumeTempId;

    private sealed record LocalSlotAlias(string Pointer, int? AlignmentBytes, StarkTypeSymbol Type);
    private readonly record struct SsaInstructionPosition(int BlockId, int InstructionIndex);

    public LlvmFunctionBodyEmitter(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        SsaFunction ssaFunction,
        LlvmEmissionContext context,
        DebugFunctionContext? debugFunction,
        SsaFunctionFactModel? valueFacts,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        bool isStrictFp,
        ResolveParameterEffectsDelegate? resolveParameterEffects = null,
        ResolveFunctionMemoryEffectsDelegate? resolveFunctionMemoryEffects = null,
        bool enableOptimizedRawPointerLoopIntrinsics = false)
    {
        _builder = builder;
        _function = function;
        _abiFunction = abiFunction;
        _resolveCallAbi = resolveCallAbi;
        _ssaFunction = ssaFunction;
        _context = context;
        _attributeBuilder = new LlvmFunctionAttributeBuilder(context);
        _debugFunction = debugFunction;
        _isStrictFp = isStrictFp;
        _resolveParameterEffects = resolveParameterEffects;
        _resolveFunctionMemoryEffects = resolveFunctionMemoryEffects;
        _publishedConcreteLayouts = LlvmSpecializationEmissionPlanner.BuildPublishedConcreteLayouts(context.LoadedModules);
        _enableOptimizedRawPointerLoopIntrinsics = enableOptimizedRawPointerLoopIntrinsics;
        _referencedValueNames = CollectReferencedValueNames(ssaFunction);
        _addressTakenParameterNames = CollectAddressTakenParameterNames(ssaFunction);
        _valueDefinitions = CollectValueDefinitions(ssaFunction);
        _phisByResultName = CollectPhisByResultName(ssaFunction);
        _trivialValueAliases = CollectTrivialValueAliases(ssaFunction);
        _valueDefinitionPositions = CollectValueDefinitionPositions(ssaFunction);
        _valueFacts = valueFacts?.Values ?? new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        _blocksById = ssaFunction.Blocks.ToDictionary(static block => block.Id);
        _blockOrderById = CollectBlockOrder(ssaFunction);
        _predecessorCounts = CountPredecessors(ssaFunction);
        _tbaaUnsafeAddressRoots = CollectTbaaUnsafeAddressRoots(ssaFunction, _valueDefinitions);
        _scopedNoAliasUnsafeAddressRoots = CollectScopedNoAliasUnsafeAddressRoots(
            ssaFunction,
            _valueDefinitions,
            resolveCallAbi,
            function.Name);
        _parameterEffects = parameterEffects;
        _sameParameterCanonicalRootKeys = BuildSameParameterCanonicalRootKeys();
        _scopedNoAliasMetadata = BuildScopedNoAliasMetadata(parameterEffects);
        _localStorageClasses = CollectLocalStorageClasses(ssaFunction);
        _singleStoreLocalValues = CollectSingleStoreLocalValues();
        _directAggregateAliasCandidateLocalNames = CollectDirectAggregateAliasCandidateLocalNames();
        _constProvenanceLocalNames = CollectConstProvenanceLocalNames();
        _invariantLocalNames = CollectInvariantLocalNames();
        _tailCallResultNames = CollectTailCallResultNames(
            ssaFunction,
            abiFunction,
            resolveCallAbi,
            function.Name,
            context,
            isStrictFp);
        _assumptionsByBlock = BuildAssumptionsByBlock();
    }

    public static bool MayEmitAssumeIntrinsic(SsaFunction function)
    {
        if (function.SameGroups.Any(static group =>
                group.ParameterNames.Distinct(StringComparer.Ordinal).Count() >= 2))
        {
            return true;
        }

        var valueDefinitions = CollectValueDefinitions(function);
        var blockOrderById = CollectBlockOrder(function);
        var predecessorCounts = CountPredecessors(function);

        foreach (var block in function.Blocks)
        {
            if (block.Terminator.Kind != SsaTerminatorKind.Branch
                || block.Terminator.Condition is null
                || block.Terminator.Targets.Count != 2
                || !IsPotentialAssumableCondition(block.Terminator.Condition, valueDefinitions))
            {
                continue;
            }

            if (CanEmitAssumeInSuccessor(
                    function.EntryBlockId,
                    block.Id,
                    block.Terminator.Targets[0],
                    blockOrderById,
                    predecessorCounts)
                || CanEmitAssumeInSuccessor(
                    function.EntryBlockId,
                    block.Id,
                    block.Terminator.Targets[1],
                    blockOrderById,
                    predecessorCounts))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MayEmitOptimizedRawPointerMemcpyIntrinsic(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects = null)
    {
        return TryMatchOptimizedRawPointerLoop(
                function,
                tryGetConcreteTypeLayout,
                RawPointerLoopIntrinsicKind.Memcpy,
                parameterEffects,
                out _)
            || CollectEmbeddedOptimizedRawPointerLoopIntrinsics(
                    function,
                    tryGetConcreteTypeLayout,
                    parameterEffects)
                .Any(static plan => plan.Kind == RawPointerLoopIntrinsicKind.Memcpy);
    }

    public static bool MayEmitOptimizedRawPointerMemmoveIntrinsic(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout)
    {
        return TryMatchOptimizedRawPointerLoop(
                function,
                tryGetConcreteTypeLayout,
                RawPointerLoopIntrinsicKind.Memmove,
                parameterEffects: null,
                out _)
            || CollectEmbeddedOptimizedRawPointerLoopIntrinsics(
                    function,
                    tryGetConcreteTypeLayout,
                    parameterEffects: null)
                .Any(static plan => plan.Kind == RawPointerLoopIntrinsicKind.Memmove);
    }

    public static bool MayEmitOptimizedRawPointerMemsetIntrinsic(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects = null)
    {
        return TryMatchOptimizedRawPointerLoop(
                function,
                tryGetConcreteTypeLayout,
                RawPointerLoopIntrinsicKind.Memset,
                parameterEffects,
                out _)
            || CollectEmbeddedOptimizedRawPointerLoopIntrinsics(
                    function,
                    tryGetConcreteTypeLayout,
                    parameterEffects)
                .Any(static plan => plan.Kind == RawPointerLoopIntrinsicKind.Memset);
    }

    public void Emit()
    {
        if (_ssaFunction.Blocks.Count == 0)
        {
            _currentDebugLocation = _ssaFunction.Location;
            EmitFallbackTerminal();
            return;
        }

        if (_enableOptimizedRawPointerLoopIntrinsics && TryEmitWholeFunctionOptimizedRawPointerLoopIntrinsic())
        {
            return;
        }

        InitializeEmbeddedOptimizedRawPointerLoopIntrinsics();
        foreach (var block in _ssaFunction.Blocks)
        {
            if (_embeddedOptimizedRawPointerLoopSkippedBlockIds.Contains(block.Id))
            {
                continue;
            }

            _currentBlock = block;
            AppendLine($"{FormatBlockLabel(block.Id)}:");

            if (block.Id == _ssaFunction.EntryBlockId)
            {
                _entryStaticAllocaInsertionIndex = _builder.Length;
                _currentDebugLocation = _ssaFunction.Location;
                EmitEntryParameterMaterialization();
                EmitEntryParameterSlots();
                EmitEntryParameterDebugInfo();
                EmitEntrySameParameterAssumptions();
            }

            foreach (var phi in block.Phis)
            {
                _currentDebugLocation = phi.Location ?? _ssaFunction.Location;
                EmitPhi(phi);
            }

            _currentDebugLocation = block.Terminator.Location ?? _ssaFunction.Location;
            if (!_embeddedOptimizedRawPointerLoopExitBlockIds.Contains(block.Id))
            {
                EmitAssumptionsForBlock(block.Id);
            }

            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                _currentInstructionIndex = instructionIndex;
                _currentDebugLocation = GetInstructionLocation(instruction) ?? _ssaFunction.Location;
                EmitInstruction(instruction);
            }

            _currentInstructionIndex = block.Instructions.Count;
            _currentDebugLocation = block.Terminator.Location ?? _ssaFunction.Location;
            if (_embeddedOptimizedRawPointerLoopPlansByPreheader.TryGetValue(block.Id, out var embeddedPlan))
            {
                _currentDebugLocation = embeddedPlan.Location ?? block.Terminator.Location ?? _ssaFunction.Location;
                EmitOptimizedRawPointerLoopIntrinsicCall(embeddedPlan);
                if (embeddedPlan.ExitBlockId is not int exitBlockId)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(embeddedPlan, "embedded plan is missing its exit block.");
                }

                AppendLine($"  br label %{FormatBlockLabel(exitBlockId)}");
            }
            else
            {
                EmitTerminator(block.Terminator);
            }

            AppendLine(string.Empty);
        }

        _currentBlock = null;
        _currentInstructionIndex = -1;

        FlushEntryStaticAllocas();
    }


}
