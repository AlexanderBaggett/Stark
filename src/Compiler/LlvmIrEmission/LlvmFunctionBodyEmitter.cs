using System.Numerics;
using System.Globalization;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionBodyEmitter
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
    private const string UnreachableTrapHelperName = "__stark_unreachable_trap";
    private const int AggregateScalarizationThresholdBytes = 16;
    private const int AggregateScalarizationMaxLeafCount = 4;
    private const int AggregateMemcpyThresholdBytes = 32;
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
    private readonly DebugFunctionContext? _debugFunction;
    private readonly bool _isStrictFp;
    private readonly HashSet<string> _referencedValueNames;
    private readonly HashSet<string> _addressTakenParameterNames;
    private readonly IReadOnlyDictionary<string, SsaRValue> _valueDefinitions;
    private readonly IReadOnlyDictionary<string, SsaValueFacts> _valueFacts;
    private readonly IReadOnlyDictionary<int, SsaBasicBlock> _blocksById;
    private readonly IReadOnlyDictionary<int, int> _blockOrderById;
    private readonly IReadOnlyDictionary<int, int> _predecessorCounts;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<LlvmAssumeFact>> _assumptionsByBlock;
    private readonly HashSet<string> _tbaaUnsafeAddressRoots;
    private readonly HashSet<string> _scopedNoAliasUnsafeAddressRoots;
    private readonly ScopedNoAliasMetadataModel? _scopedNoAliasMetadata;
    private readonly IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? _parameterEffects;
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
    private int _nextAbiTempId;
    private int _nextAssumeTempId;

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
        bool enableOptimizedRawPointerLoopIntrinsics = false)
    {
        _builder = builder;
        _function = function;
        _abiFunction = abiFunction;
        _resolveCallAbi = resolveCallAbi;
        _ssaFunction = ssaFunction;
        _context = context;
        _debugFunction = debugFunction;
        _isStrictFp = isStrictFp;
        _enableOptimizedRawPointerLoopIntrinsics = enableOptimizedRawPointerLoopIntrinsics;
        _referencedValueNames = CollectReferencedValueNames(ssaFunction);
        _addressTakenParameterNames = CollectAddressTakenParameterNames(ssaFunction);
        _valueDefinitions = CollectValueDefinitions(ssaFunction);
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
        _scopedNoAliasMetadata = BuildScopedNoAliasMetadata(parameterEffects);
        _localStorageClasses = CollectLocalStorageClasses(ssaFunction);
        _singleStoreLocalValues = CollectSingleStoreLocalValues();
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

            foreach (var instruction in block.Instructions)
            {
                _currentDebugLocation = GetInstructionLocation(instruction) ?? _ssaFunction.Location;
                EmitInstruction(instruction);
            }

            _currentDebugLocation = block.Terminator.Location ?? _ssaFunction.Location;
            if (_embeddedOptimizedRawPointerLoopPlansByPreheader.TryGetValue(block.Id, out var embeddedPlan))
            {
                _currentDebugLocation = embeddedPlan.Location ?? block.Terminator.Location ?? _ssaFunction.Location;
                if (!TryEmitOptimizedRawPointerLoopIntrinsicCall(embeddedPlan)
                    || embeddedPlan.ExitBlockId is not int exitBlockId)
                {
                    throw new UnsupportedBodyEmissionException("Failed to emit optimized raw pointer loop intrinsic.");
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

        FlushEntryStaticAllocas();
    }

    private void InitializeEmbeddedOptimizedRawPointerLoopIntrinsics()
    {
        _embeddedOptimizedRawPointerLoopPlansByPreheader = new Dictionary<int, RawPointerLoopIntrinsicPlan>();
        _embeddedOptimizedRawPointerLoopSkippedBlockIds = new HashSet<int>();
        _embeddedOptimizedRawPointerLoopExitBlockIds = new HashSet<int>();
        if (!_enableOptimizedRawPointerLoopIntrinsics)
        {
            return;
        }

        var plans = CollectEmbeddedOptimizedRawPointerLoopIntrinsics(
            _ssaFunction,
            TryGetConcreteTypeLayout,
            _parameterEffects);
        if (plans.Count == 0)
        {
            return;
        }

        _embeddedOptimizedRawPointerLoopPlansByPreheader =
            plans.ToDictionary(static plan => plan.PreheaderBlockId!.Value);
        _embeddedOptimizedRawPointerLoopSkippedBlockIds = plans
            .SelectMany(static plan => plan.SkippedBlockIds ?? [])
            .ToHashSet();
        _embeddedOptimizedRawPointerLoopExitBlockIds = plans
            .Where(static plan => plan.ExitBlockId is not null)
            .Select(static plan => plan.ExitBlockId!.Value)
            .ToHashSet();
    }

    private bool TryEmitWholeFunctionOptimizedRawPointerLoopIntrinsic()
    {
        if (!TryMatchOptimizedRawPointerLoop(
                _ssaFunction,
                TryGetConcreteTypeLayout,
                requiredKind: null,
                _parameterEffects,
                out var plan))
        {
            return false;
        }

        var entryBlock = _blocksById[_ssaFunction.EntryBlockId];
        _currentBlock = entryBlock;
        _currentDebugLocation = _ssaFunction.Location;
        AppendLine($"{FormatBlockLabel(entryBlock.Id)}:");
        _entryStaticAllocaInsertionIndex = _builder.Length;
        EmitEntryParameterMaterialization();
        EmitEntryParameterSlots();
        EmitEntryParameterDebugInfo();

        _currentDebugLocation = plan.Location ?? _ssaFunction.Location;
        if (!TryEmitOptimizedRawPointerLoopIntrinsicCall(plan))
        {
            _currentBlock = null;
            return false;
        }

        AppendLine("  ret void");
        AppendLine(string.Empty);
        _currentBlock = null;
        FlushEntryStaticAllocas();
        return true;
    }

    private bool TryEmitOptimizedRawPointerLoopIntrinsicCall(RawPointerLoopIntrinsicPlan plan)
    {
        return plan.Kind switch
        {
            RawPointerLoopIntrinsicKind.Memcpy => TryEmitOptimizedRawPointerMemcpy(plan),
            RawPointerLoopIntrinsicKind.Memmove => TryEmitOptimizedRawPointerMemmove(plan),
            RawPointerLoopIntrinsicKind.Memset => TryEmitOptimizedRawPointerMemset(plan),
            _ => false
        };
    }

    private bool TryEmitOptimizedRawPointerMemcpy(RawPointerLoopIntrinsicPlan plan)
    {
        if (plan.SourceBase is null
            || !TryEmitRawPointerLoopByteLength(plan, out var byteLength, out var isZeroLength))
        {
            return false;
        }

        if (isZeroLength)
        {
            return true;
        }

        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(plan.DestinationBase, plan.ElementType);
        var sourceAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(plan.SourceBase, plan.ElementType);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase, plan.SourceBase);
        AppendLine(
            $"  call void @llvm.memcpy.p0.p0.i64(ptr{destinationAlignment} {FormatValue(plan.DestinationBase)}, ptr{sourceAlignment} {FormatValue(plan.SourceBase)}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
        return true;
    }

    private bool TryEmitOptimizedRawPointerMemmove(RawPointerLoopIntrinsicPlan plan)
    {
        if (plan.SourceBase is null
            || !TryEmitRawPointerLoopByteLength(plan, out var byteLength, out var isZeroLength))
        {
            return false;
        }

        if (isZeroLength)
        {
            return true;
        }

        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(plan.DestinationBase, plan.ElementType);
        var sourceAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(plan.SourceBase, plan.ElementType);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase, plan.SourceBase);
        AppendLine(
            $"  call void @llvm.memmove.p0.p0.i64(ptr{destinationAlignment} {FormatValue(plan.DestinationBase)}, ptr{sourceAlignment} {FormatValue(plan.SourceBase)}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
        return true;
    }

    private bool TryEmitOptimizedRawPointerMemset(RawPointerLoopIntrinsicPlan plan)
    {
        if (plan.FillValue is null
            || !TryEmitRawPointerLoopByteLength(plan, out var byteLength, out var isZeroLength))
        {
            return false;
        }

        if (isZeroLength)
        {
            return true;
        }

        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(plan.DestinationBase, plan.ElementType);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase);
        AppendLine(
            $"  call void @llvm.memset.p0.i64(ptr{destinationAlignment} {FormatValue(plan.DestinationBase)}, i8 {FormatValue(plan.FillValue)}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
        return true;
    }

    private bool TryEmitRawPointerLoopByteLength(
        RawPointerLoopIntrinsicPlan plan,
        out string byteLength,
        out bool isZeroLength)
    {
        byteLength = string.Empty;
        isZeroLength = false;

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(plan.ElementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            return false;
        }

        if (plan.Count is SsaIntegerConstant constantCount)
        {
            if (constantCount.Value < BigInteger.Zero)
            {
                return false;
            }

            var constantByteLength = constantCount.Value * elementLayout.SizeBytes;
            if (constantByteLength > long.MaxValue)
            {
                return false;
            }

            isZeroLength = constantByteLength.IsZero;
            byteLength = constantByteLength.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (!TryGetIntegerValueRange(plan.Count, new HashSet<string>(StringComparer.Ordinal), out var minCount, out var maxCount)
            || minCount < BigInteger.Zero
            || maxCount * elementLayout.SizeBytes > long.MaxValue
            || plan.Count.Type.Kind != StarkTypeKind.Integer
            || plan.Count.Type.BitWidth is not int countBitWidth
            || countBitWidth is <= 0 or > 64)
        {
            return false;
        }

        string count64;
        if (countBitWidth == 64)
        {
            count64 = FormatValue(plan.Count);
        }
        else
        {
            count64 = $"%{EscapeIdentifier(CreateAbiTempName("rawptr_loop_count"))}";
            AppendLine($"  {count64} = zext {MapType(plan.Count.Type)} {FormatValue(plan.Count)} to i64");
        }

        if (elementLayout.SizeBytes == 1)
        {
            byteLength = count64;
            return true;
        }

        byteLength = $"%{EscapeIdentifier(CreateAbiTempName("rawptr_loop_bytes"))}";
        AppendLine($"  {byteLength} = mul i64 {count64}, {elementLayout.SizeBytes}");
        return true;
    }

    private string GetOptimizedRawPointerLoopArgumentAlignmentFragment(SsaValue pointer, StarkTypeSymbol elementType)
    {
        var alignmentBytes = GetKnownPointerAlignmentBytes(pointer, elementType)
            ?? GetBoundedRawPointerParameterAlignmentBytes(pointer, elementType);
        return GetArgumentAlignmentFragment(alignmentBytes);
    }

    private int? GetBoundedRawPointerParameterAlignmentBytes(SsaValue pointer, StarkTypeSymbol elementType)
    {
        if (pointer is SsaValueReference reference
            && _valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return definition switch
            {
                SsaUseRValue use => GetBoundedRawPointerParameterAlignmentBytes(use.Value, elementType),
                SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer =>
                    GetBoundedRawPointerParameterAlignmentBytes(convert.Operand, elementType),
                _ => null
            };
        }

        if (pointer is not SsaValueReference parameterReference)
        {
            return null;
        }

        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => candidate.Kind == AbiParameterKind.Direct
                && candidate.SourceType.Kind == StarkTypeKind.RawPointer
                && candidate.SourceType.ElementType is not null
                && !string.IsNullOrWhiteSpace(candidate.RawPointerElementCountExpression)
                && (string.Equals(candidate.LlvmName, parameterReference.Name, StringComparison.Ordinal)
                    || string.Equals(candidate.SourceName, parameterReference.Name, StringComparison.Ordinal)));
        if (parameter?.SourceType.ElementType is not { } parameterElementType
            || NormalizeAggregateType(parameterElementType) != NormalizeAggregateType(elementType)
            || TryGetConcreteTypeLayout(NormalizeAggregateType(parameterElementType)) is not { } elementLayout
            || elementLayout.AlignmentBytes <= 1)
        {
            return null;
        }

        return elementLayout.AlignmentBytes;
    }

    private static bool TryMatchOptimizedRawPointerLoop(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        RawPointerLoopIntrinsicKind? requiredKind,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if ((requiredKind is null || requiredKind == RawPointerLoopIntrinsicKind.Memmove)
            && TryMatchOverlapSafeRawPointerMemmoveLoop(function, tryGetConcreteTypeLayout, out plan))
        {
            return true;
        }

        if (!TryMatchCanonicalIndependentRawPointerLoop(function, out var loop))
        {
            return false;
        }

        if ((requiredKind is null || requiredKind == RawPointerLoopIntrinsicKind.Memcpy)
            && TryMatchRawPointerMemcpyLoop(loop, tryGetConcreteTypeLayout, parameterEffects, out plan))
        {
            return true;
        }

        if ((requiredKind is null || requiredKind == RawPointerLoopIntrinsicKind.Memset)
            && TryMatchRawPointerMemsetLoop(loop, tryGetConcreteTypeLayout, out plan))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<RawPointerLoopIntrinsicPlan> CollectEmbeddedOptimizedRawPointerLoopIntrinsics(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var plans = new List<RawPointerLoopIntrinsicPlan>();
        if (!function.HasBody || function.ReturnType.Kind != StarkTypeKind.Void)
        {
            return plans;
        }

        var skippedBlockIds = new HashSet<int>();
        foreach (var preheader in function.Blocks)
        {
            if (skippedBlockIds.Contains(preheader.Id)
                || !TryMatchEmbeddedOptimizedRawPointerLoopFromPreheader(
                    function,
                    preheader,
                    tryGetConcreteTypeLayout,
                    parameterEffects,
                    out var plan))
            {
                continue;
            }

            plans.Add(plan);
            foreach (var skippedBlockId in plan.SkippedBlockIds ?? [])
            {
                skippedBlockIds.Add(skippedBlockId);
            }
        }

        return plans;
    }

    private static bool TryMatchEmbeddedOptimizedRawPointerLoopFromPreheader(
        SsaFunction function,
        SsaBasicBlock preheader,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        var definitions = CollectValueDefinitions(function);
        var nonEntryValueNames = CollectNonEntrySsaValueNames(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var predecessorCounts = CountPredecessors(function);
        if (TryMatchEmbeddedOptimizedRawPointerMemmoveFromPreheader(
                function,
                preheader,
                tryGetConcreteTypeLayout,
                blocksById,
                definitions,
                nonEntryValueNames,
                predecessorCounts,
                out plan))
        {
            return true;
        }

        if (!TryMatchCanonicalRawPointerLoopFromPreheader(
                function,
                preheader,
                blocksById,
                definitions,
                nonEntryValueNames,
                out var loop,
                out var exit)
            || !CanReplaceEmbeddedRawPointerLoop(function, loop, exit, predecessorCounts))
        {
            return false;
        }

        if (TryMatchRawPointerMemcpyLoop(loop, tryGetConcreteTypeLayout, parameterEffects, out plan)
            || TryMatchRawPointerMemsetLoop(loop, tryGetConcreteTypeLayout, out plan))
        {
            plan = plan with
            {
                PreheaderBlockId = preheader.Id,
                ExitBlockId = exit.Id,
                SkippedBlockIds = [loop.ConditionBlockId, loop.Body.Id]
            };
            return true;
        }

        return false;
    }

    private static bool TryMatchEmbeddedOptimizedRawPointerMemmoveFromPreheader(
        SsaFunction function,
        SsaBasicBlock preheader,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        IReadOnlyDictionary<int, int> predecessorCounts,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!TryMatchOverlapSafeTemporaryEntry(
                preheader,
                out var temporaryLocalName,
                out var temporaryType,
                out var temporaryElementType,
                out var temporaryFixedLength)
            || !TryMatchCanonicalRawPointerLoopFromPreheader(
                function,
                preheader,
                blocksById,
                definitions,
                nonEntryValueNames,
                out var sourceToTemporaryLoop,
                out var middle)
            || middle.Phis.Count != 0
            || middle.Instructions.Count != 0
            || !TryMatchCanonicalRawPointerLoopFromPreheader(
                function,
                middle,
                blocksById,
                definitions,
                nonEntryValueNames,
                out var temporaryToDestinationLoop,
                out var exit)
            || !TryMatchEmbeddedOverlapSafeTemporaryExit(exit, temporaryLocalName, temporaryType, definitions)
            || !CanReplaceEmbeddedRawPointerMemmove(
                function,
                sourceToTemporaryLoop,
                middle,
                temporaryToDestinationLoop,
                exit,
                predecessorCounts,
                out var skippedBlockIds)
            || !TryResolveIntegerConstant(
                sourceToTemporaryLoop.Count,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var sourceCount)
            || !TryResolveIntegerConstant(
                temporaryToDestinationLoop.Count,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var destinationCount)
            || sourceCount != temporaryFixedLength
            || destinationCount != temporaryFixedLength
            || !AreEquivalentEntryValues(
                sourceToTemporaryLoop.Count,
                temporaryToDestinationLoop.Count,
                definitions,
                nonEntryValueNames)
            || !TryMatchRawPointerToTemporaryCopyLoop(
                sourceToTemporaryLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var sourceBase,
                out var sourceElementType)
            || !TryMatchTemporaryToRawPointerCopyLoop(
                temporaryToDestinationLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var destinationBase,
                out var destinationElementType,
                out var location)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(temporaryElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(temporaryElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(
                new SsaIntegerConstant(sourceCount, StarkTypeSymbols.Integer(64, BigInteger.Zero, sourceCount)),
                elementLayout,
                definitions))
        {
            return false;
        }

        plan = new RawPointerLoopIntrinsicPlan(
            RawPointerLoopIntrinsicKind.Memmove,
            destinationBase,
            sourceBase,
            null,
            temporaryElementType,
            new SsaIntegerConstant(sourceCount, StarkTypeSymbols.Integer(64, BigInteger.Zero, sourceCount)),
            location,
            preheader.Id,
            exit.Id,
            skippedBlockIds);
        return true;
    }

    private static bool CanReplaceEmbeddedRawPointerMemmove(
        SsaFunction function,
        CanonicalRawPointerLoop sourceToTemporaryLoop,
        SsaBasicBlock middle,
        CanonicalRawPointerLoop temporaryToDestinationLoop,
        SsaBasicBlock exit,
        IReadOnlyDictionary<int, int> predecessorCounts,
        out IReadOnlyList<int> skippedBlockIds)
    {
        var skippedSet = new HashSet<int>
        {
            sourceToTemporaryLoop.ConditionBlockId,
            sourceToTemporaryLoop.Body.Id,
            middle.Id,
            temporaryToDestinationLoop.ConditionBlockId,
            temporaryToDestinationLoop.Body.Id
        };
        skippedBlockIds = skippedSet.ToArray();

        return skippedSet.Count == 5
            && exit.Phis.Count == 0
            && predecessorCounts.TryGetValue(sourceToTemporaryLoop.ConditionBlockId, out var sourceConditionPredecessorCount)
            && sourceConditionPredecessorCount == 2
            && predecessorCounts.TryGetValue(sourceToTemporaryLoop.Body.Id, out var sourceBodyPredecessorCount)
            && sourceBodyPredecessorCount == 1
            && predecessorCounts.TryGetValue(middle.Id, out var middlePredecessorCount)
            && middlePredecessorCount == 1
            && predecessorCounts.TryGetValue(temporaryToDestinationLoop.ConditionBlockId, out var destinationConditionPredecessorCount)
            && destinationConditionPredecessorCount == 2
            && predecessorCounts.TryGetValue(temporaryToDestinationLoop.Body.Id, out var destinationBodyPredecessorCount)
            && destinationBodyPredecessorCount == 1
            && predecessorCounts.TryGetValue(exit.Id, out var exitPredecessorCount)
            && exitPredecessorCount == 1
            && SkippedBlocksDefineNoValuesUsedOutside(function, skippedSet);
    }

    private static bool CanReplaceEmbeddedRawPointerLoop(
        SsaFunction function,
        CanonicalRawPointerLoop loop,
        SsaBasicBlock exit,
        IReadOnlyDictionary<int, int> predecessorCounts)
    {
        return exit.Phis.Count == 0
            && predecessorCounts.TryGetValue(loop.ConditionBlockId, out var conditionPredecessorCount)
            && conditionPredecessorCount == 2
            && predecessorCounts.TryGetValue(loop.Body.Id, out var bodyPredecessorCount)
            && bodyPredecessorCount == 1
            && predecessorCounts.TryGetValue(exit.Id, out var exitPredecessorCount)
            && exitPredecessorCount == 1
            && SkippedBlocksDefineNoValuesUsedOutside(
                function,
                new HashSet<int> { loop.ConditionBlockId, loop.Body.Id });
    }

    private static bool SkippedBlocksDefineNoValuesUsedOutside(
        SsaFunction function,
        ISet<int> skippedBlockIds)
    {
        var skippedDefinitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in function.Blocks.Where(block => skippedBlockIds.Contains(block.Id)))
        {
            foreach (var phi in block.Phis)
            {
                skippedDefinitions.Add(phi.ResultName);
            }

            foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                skippedDefinitions.Add(instruction.ResultName);
            }
        }

        if (skippedDefinitions.Count == 0)
        {
            return true;
        }

        foreach (var block in function.Blocks.Where(block => !skippedBlockIds.Contains(block.Id)))
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    if (ReferencesSkippedDefinition(incoming.Value))
                    {
                        return false;
                    }
                }
            }

            foreach (var instruction in block.Instructions)
            {
                if (InstructionReferencesSkippedDefinition(instruction))
                {
                    return false;
                }
            }

            if (ReferencesSkippedDefinition(block.Terminator.Condition)
                || ReferencesSkippedDefinition(block.Terminator.Value))
            {
                return false;
            }

            if (block.Terminator.SwitchCases is not null
                && block.Terminator.SwitchCases.Any(switchCase => ReferencesSkippedDefinition(switchCase.MatchValue)))
            {
                return false;
            }
        }

        return true;

        bool InstructionReferencesSkippedDefinition(SsaInstruction instruction)
        {
            return instruction switch
            {
                SsaValueInstruction valueInstruction => RValueReferencesSkippedDefinition(valueInstruction.Value),
                SsaStoreLocalInstruction storeLocal => ReferencesSkippedDefinition(storeLocal.Value),
                SsaStoreIndirectInstruction storeIndirect =>
                    ReferencesSkippedDefinition(storeIndirect.Address)
                    || ReferencesSkippedDefinition(storeIndirect.Value),
                SsaCopyMemoryInstruction copyMemory =>
                    ReferencesSkippedDefinition(copyMemory.DestinationAddress)
                    || ReferencesSkippedDefinition(copyMemory.SourceAddress),
                SsaStoreGlobalInstruction storeGlobal => ReferencesSkippedDefinition(storeGlobal.Value),
                _ => false
            };
        }

        bool RValueReferencesSkippedDefinition(SsaRValue value)
        {
            return value switch
            {
                SsaUseRValue use => ReferencesSkippedDefinition(use.Value),
                SsaUnaryRValue unary => ReferencesSkippedDefinition(unary.Operand),
                SsaBinaryRValue binary =>
                    ReferencesSkippedDefinition(binary.Left)
                    || ReferencesSkippedDefinition(binary.Right),
                SsaSelectRValue select =>
                    ReferencesSkippedDefinition(select.Condition)
                    || ReferencesSkippedDefinition(select.WhenTrue)
                    || ReferencesSkippedDefinition(select.WhenFalse),
                SsaCallRValue call =>
                    call.Arguments.Any(ReferencesSkippedDefinition)
                    || (call.IndirectArgumentAddresses?.OfType<SsaValue>().Any(ReferencesSkippedDefinition) == true),
                SsaConvertRValue convert => ReferencesSkippedDefinition(convert.Operand),
                SsaExtractFieldRValue extractField => ReferencesSkippedDefinition(extractField.Target),
                SsaInsertFieldRValue insertField =>
                    ReferencesSkippedDefinition(insertField.Target)
                    || ReferencesSkippedDefinition(insertField.Value),
                SsaExtractIndexRValue extractIndex => ReferencesSkippedDefinition(extractIndex.Target),
                SsaInsertIndexRValue insertIndex =>
                    ReferencesSkippedDefinition(insertIndex.Target)
                    || ReferencesSkippedDefinition(insertIndex.Value),
                SsaMakeSliceFromPointerRValue makeSlice =>
                    ReferencesSkippedDefinition(makeSlice.Pointer)
                    || ReferencesSkippedDefinition(makeSlice.Length),
                SsaLoadSliceElementRValue loadSlice =>
                    ReferencesSkippedDefinition(loadSlice.Slice)
                    || ReferencesSkippedDefinition(loadSlice.Index),
                SsaTextSliceRValue textSlice =>
                    ReferencesSkippedDefinition(textSlice.TextValue)
                    || ReferencesSkippedDefinition(textSlice.Start)
                    || ReferencesSkippedDefinition(textSlice.Length),
                SsaFieldAddressRValue fieldAddress => ReferencesSkippedDefinition(fieldAddress.Address),
                SsaElementAddressRValue elementAddress =>
                    ReferencesSkippedDefinition(elementAddress.Address)
                    || ReferencesSkippedDefinition(elementAddress.Index),
                SsaSliceElementAddressRValue sliceElementAddress =>
                    ReferencesSkippedDefinition(sliceElementAddress.Slice)
                    || ReferencesSkippedDefinition(sliceElementAddress.Index),
                SsaLoadIndirectRValue loadIndirect => ReferencesSkippedDefinition(loadIndirect.Address),
                _ => false
            };
        }

        bool ReferencesSkippedDefinition(SsaValue? value)
        {
            return value is SsaValueReference reference
                && skippedDefinitions.Contains(reference.Name);
        }
    }

    private static bool TryMatchOverlapSafeRawPointerMemmoveLoop(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!function.HasBody
            || function.ReturnType.Kind != StarkTypeKind.Void
            || function.Blocks.Count != 7)
        {
            return false;
        }

        var definitions = CollectValueDefinitions(function);
        var nonEntryValueNames = CollectNonEntrySsaValueNames(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        if (!blocksById.TryGetValue(function.EntryBlockId, out var entry)
            || !TryMatchOverlapSafeTemporaryEntry(
                entry,
                out var temporaryLocalName,
                out var temporaryType,
                out var temporaryElementType,
                out var temporaryFixedLength)
            || !TryMatchCanonicalRawPointerLoopFromPreheader(
                function,
                entry,
                blocksById,
                definitions,
                nonEntryValueNames,
                out var sourceToTemporaryLoop,
                out var middle)
            || middle.Phis.Count != 0
            || middle.Instructions.Count != 0
            || !TryMatchCanonicalRawPointerLoopFromPreheader(
                function,
                middle,
                blocksById,
                definitions,
                nonEntryValueNames,
                out var temporaryToDestinationLoop,
                out var exit)
            || !TryMatchOverlapSafeTemporaryExit(exit, temporaryLocalName, temporaryType)
            || !TryResolveIntegerConstant(
                sourceToTemporaryLoop.Count,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var sourceCount)
            || !TryResolveIntegerConstant(
                temporaryToDestinationLoop.Count,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var destinationCount)
            || sourceCount != temporaryFixedLength
            || destinationCount != temporaryFixedLength
            || !AreEquivalentEntryValues(
                sourceToTemporaryLoop.Count,
                temporaryToDestinationLoop.Count,
                definitions,
                nonEntryValueNames)
            || !TryMatchRawPointerToTemporaryCopyLoop(
                sourceToTemporaryLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var sourceBase,
                out var sourceElementType)
            || !TryMatchTemporaryToRawPointerCopyLoop(
                temporaryToDestinationLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var destinationBase,
                out var destinationElementType,
                out var location)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(temporaryElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(temporaryElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(
                new SsaIntegerConstant(sourceCount, StarkTypeSymbols.Integer(64, BigInteger.Zero, sourceCount)),
                elementLayout,
                definitions))
        {
            return false;
        }

        plan = new RawPointerLoopIntrinsicPlan(
            RawPointerLoopIntrinsicKind.Memmove,
            destinationBase,
            sourceBase,
            null,
            temporaryElementType,
            new SsaIntegerConstant(sourceCount, StarkTypeSymbols.Integer(64, BigInteger.Zero, sourceCount)),
            location);
        return true;
    }

    private static bool TryMatchOverlapSafeTemporaryEntry(
        SsaBasicBlock entry,
        out string temporaryLocalName,
        out StarkTypeSymbol temporaryType,
        out StarkTypeSymbol temporaryElementType,
        out int temporaryFixedLength)
    {
        temporaryLocalName = string.Empty;
        temporaryType = StarkTypeSymbols.Error;
        temporaryElementType = StarkTypeSymbols.Error;
        temporaryFixedLength = 0;

        if (entry.Phis.Count != 0
            || entry.Terminator.Kind != SsaTerminatorKind.Goto
            || entry.Terminator.Targets.Count != 1)
        {
            return false;
        }

        var sawAllocate = false;
        var sawLifetimeStart = false;
        var sawZeroStore = false;
        foreach (var instruction in entry.Instructions)
        {
            switch (instruction)
            {
                case SsaAllocateLocalInstruction allocate:
                    if (sawAllocate
                        || !string.Equals(allocate.StorageClass, "stack", StringComparison.Ordinal)
                        || allocate.LocalType.Kind != StarkTypeKind.FixedArray
                        || allocate.LocalType.ElementType is not { } elementType
                        || allocate.LocalType.FixedLength is not int fixedLength
                        || fixedLength < 0)
                    {
                        return false;
                    }

                    sawAllocate = true;
                    temporaryLocalName = allocate.LocalName;
                    temporaryType = allocate.LocalType;
                    temporaryElementType = elementType;
                    temporaryFixedLength = fixedLength;
                    break;
                case SsaLifetimeStartInstruction lifetimeStart:
                    if (!sawAllocate
                        || sawLifetimeStart
                        || !string.Equals(lifetimeStart.LocalName, temporaryLocalName, StringComparison.Ordinal)
                        || NormalizeAggregateType(lifetimeStart.LocalType) != NormalizeAggregateType(temporaryType))
                    {
                        return false;
                    }

                    sawLifetimeStart = true;
                    break;
                case SsaStoreLocalInstruction storeLocal:
                    if (!sawAllocate
                        || sawZeroStore
                        || !string.Equals(storeLocal.LocalName, temporaryLocalName, StringComparison.Ordinal)
                        || NormalizeAggregateType(storeLocal.LocalType) != NormalizeAggregateType(temporaryType)
                        || storeLocal.Value is not SsaZeroInitializerValue zero
                        || NormalizeAggregateType(zero.Type) != NormalizeAggregateType(temporaryType))
                    {
                        return false;
                    }

                    sawZeroStore = true;
                    break;
                default:
                    return false;
            }
        }

        return sawAllocate && sawLifetimeStart && sawZeroStore;
    }

    private static bool TryMatchOverlapSafeTemporaryExit(
        SsaBasicBlock exit,
        string temporaryLocalName,
        StarkTypeSymbol temporaryType)
    {
        if (exit.Phis.Count != 0
            || exit.Terminator.Kind != SsaTerminatorKind.Return
            || exit.Terminator.Value is not null)
        {
            return false;
        }

        var sawLifetimeEnd = false;
        foreach (var instruction in exit.Instructions)
        {
            if (instruction is not SsaLifetimeEndInstruction lifetimeEnd
                || sawLifetimeEnd
                || !string.Equals(lifetimeEnd.LocalName, temporaryLocalName, StringComparison.Ordinal)
                || NormalizeAggregateType(lifetimeEnd.LocalType) != NormalizeAggregateType(temporaryType))
            {
                return false;
            }

            sawLifetimeEnd = true;
        }

        return sawLifetimeEnd;
    }

    private static bool TryMatchEmbeddedOverlapSafeTemporaryExit(
        SsaBasicBlock exit,
        string temporaryLocalName,
        StarkTypeSymbol temporaryType,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (exit.Phis.Count != 0)
        {
            return false;
        }

        var sawLifetimeEnd = false;
        foreach (var instruction in exit.Instructions)
        {
            if (instruction is SsaLifetimeEndInstruction lifetimeEnd)
            {
                if (sawLifetimeEnd
                    || !string.Equals(lifetimeEnd.LocalName, temporaryLocalName, StringComparison.Ordinal)
                    || NormalizeAggregateType(lifetimeEnd.LocalType) != NormalizeAggregateType(temporaryType))
                {
                    return false;
                }

                sawLifetimeEnd = true;
                continue;
            }

            if (InstructionReferencesLocal(instruction, temporaryLocalName, definitions))
            {
                return false;
            }
        }

        return sawLifetimeEnd;
    }

    private static bool InstructionReferencesLocal(
        SsaInstruction instruction,
        string localName,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction =>
                RValueReferencesLocal(valueInstruction.Value, localName, definitions, new HashSet<string>(StringComparer.Ordinal)),
            SsaAllocateLocalInstruction allocate => string.Equals(allocate.LocalName, localName, StringComparison.Ordinal),
            SsaLifetimeStartInstruction lifetimeStart => string.Equals(lifetimeStart.LocalName, localName, StringComparison.Ordinal),
            SsaLifetimeEndInstruction lifetimeEnd => string.Equals(lifetimeEnd.LocalName, localName, StringComparison.Ordinal),
            SsaDeallocateLocalInstruction deallocate => string.Equals(deallocate.LocalName, localName, StringComparison.Ordinal),
            SsaStoreLocalInstruction storeLocal =>
                string.Equals(storeLocal.LocalName, localName, StringComparison.Ordinal)
                || ValueReferencesLocal(storeLocal.Value, localName, definitions, new HashSet<string>(StringComparer.Ordinal)),
            SsaStoreIndirectInstruction storeIndirect =>
                ValueReferencesLocal(storeIndirect.Address, localName, definitions, new HashSet<string>(StringComparer.Ordinal))
                || ValueReferencesLocal(storeIndirect.Value, localName, definitions, new HashSet<string>(StringComparer.Ordinal)),
            SsaCopyMemoryInstruction copyMemory =>
                ValueReferencesLocal(copyMemory.DestinationAddress, localName, definitions, new HashSet<string>(StringComparer.Ordinal))
                || ValueReferencesLocal(copyMemory.SourceAddress, localName, definitions, new HashSet<string>(StringComparer.Ordinal)),
            SsaStoreGlobalInstruction storeGlobal =>
                ValueReferencesLocal(storeGlobal.Value, localName, definitions, new HashSet<string>(StringComparer.Ordinal)),
            _ => false
        };
    }

    private static bool RValueReferencesLocal(
        SsaRValue value,
        string localName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaUseRValue use => ValueReferencesLocal(use.Value, localName, definitions, visitedValueNames),
            SsaUnaryRValue unary => ValueReferencesLocal(unary.Operand, localName, definitions, visitedValueNames),
            SsaBinaryRValue binary =>
                ValueReferencesLocal(binary.Left, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(binary.Right, localName, definitions, visitedValueNames),
            SsaSelectRValue select =>
                ValueReferencesLocal(select.Condition, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(select.WhenTrue, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(select.WhenFalse, localName, definitions, visitedValueNames),
            SsaCallRValue call =>
                call.Arguments.Any(argument => ValueReferencesLocal(argument, localName, definitions, visitedValueNames))
                || (call.IndirectArgumentAddresses?.Any(address => address is not null && ValueReferencesLocal(address, localName, definitions, visitedValueNames)) == true),
            SsaIndirectCallRValue indirectCall =>
                ValueReferencesLocal(indirectCall.Target, localName, definitions, visitedValueNames)
                || indirectCall.Arguments.Any(argument => ValueReferencesLocal(argument, localName, definitions, visitedValueNames)),
            SsaConvertRValue convert => ValueReferencesLocal(convert.Operand, localName, definitions, visitedValueNames),
            SsaExtractFieldRValue extractField => ValueReferencesLocal(extractField.Target, localName, definitions, visitedValueNames),
            SsaInsertFieldRValue insertField =>
                ValueReferencesLocal(insertField.Target, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(insertField.Value, localName, definitions, visitedValueNames),
            SsaExtractIndexRValue extractIndex => ValueReferencesLocal(extractIndex.Target, localName, definitions, visitedValueNames),
            SsaInsertIndexRValue insertIndex =>
                ValueReferencesLocal(insertIndex.Target, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(insertIndex.Value, localName, definitions, visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice => string.Equals(makeSlice.LocalName, localName, StringComparison.Ordinal),
            SsaMakeSliceFromPointerRValue makeSlice =>
                ValueReferencesLocal(makeSlice.Pointer, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(makeSlice.Length, localName, definitions, visitedValueNames),
            SsaLoadSliceElementRValue loadSlice =>
                ValueReferencesLocal(loadSlice.Slice, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(loadSlice.Index, localName, definitions, visitedValueNames),
            SsaTextSliceRValue textSlice =>
                ValueReferencesLocal(textSlice.TextValue, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(textSlice.Start, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(textSlice.Length, localName, definitions, visitedValueNames),
            SsaAddressOfLocalRValue addressOfLocal => string.Equals(addressOfLocal.LocalName, localName, StringComparison.Ordinal),
            SsaAddressOfParameterRValue => false,
            SsaFieldAddressRValue fieldAddress => ValueReferencesLocal(fieldAddress.Address, localName, definitions, visitedValueNames),
            SsaElementAddressRValue elementAddress =>
                ValueReferencesLocal(elementAddress.Address, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(elementAddress.Index, localName, definitions, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress =>
                ValueReferencesLocal(sliceElementAddress.Slice, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(sliceElementAddress.Index, localName, definitions, visitedValueNames),
            SsaLoadIndirectRValue loadIndirect => ValueReferencesLocal(loadIndirect.Address, localName, definitions, visitedValueNames),
            SsaLoadGlobalRValue => false,
            SsaLoadLocalRValue loadLocal => string.Equals(loadLocal.LocalName, localName, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool ValueReferencesLocal(
        SsaValue? value,
        string localName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return RValueReferencesLocal(definition, localName, definitions, visitedValueNames);
    }

    private static bool TryMatchCanonicalRawPointerLoopFromPreheader(
        SsaFunction function,
        SsaBasicBlock preheader,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        out CanonicalRawPointerLoop loop,
        out SsaBasicBlock exit)
    {
        loop = null!;
        exit = null!;
        if (preheader.Terminator.Kind != SsaTerminatorKind.Goto
            || preheader.Terminator.Targets.Count != 1
            || !blocksById.TryGetValue(preheader.Terminator.Targets[0], out var condition)
            || condition.Phis.Count != 1
            || condition.Terminator.Kind != SsaTerminatorKind.Branch
            || condition.Terminator.Condition is null
            || condition.Terminator.Targets.Count != 2)
        {
            return false;
        }

        var bodyId = condition.Terminator.Targets[0];
        var exitId = condition.Terminator.Targets[1];
        if (!blocksById.TryGetValue(bodyId, out var body)
            || !blocksById.TryGetValue(exitId, out var exitBlock)
            || body.Phis.Count != 0
            || body.Terminator.Kind != SsaTerminatorKind.Goto
            || body.Terminator.Targets.Count != 1
            || body.Terminator.Targets[0] != condition.Id)
        {
            return false;
        }

        exit = exitBlock;
        var induction = condition.Phis[0];
        if (!TryGetPhiIncoming(induction, preheader.Id, out var initialValue)
            || !IsZeroIntegerConstant(initialValue)
            || !TryGetPhiIncoming(induction, body.Id, out var updateValue)
            || updateValue is not SsaValueReference updateReference
            || !definitions.TryGetValue(updateReference.Name, out var updateDefinition)
            || !IsIncrementByOne(updateDefinition, induction.ResultName, definitions))
        {
            return false;
        }

        if (!TryResolveComparisonCondition(condition.Terminator.Condition, definitions, out var comparison)
            || comparison.Operator != SsaBinaryOperator.LessThan
            || !IsInductionValue(comparison.Left, induction.ResultName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryResolveEntryAvailableValue(comparison.Right, definitions, nonEntryValueNames, out var count)
            || !CanRepresentRawPointerLoopByteLength(count, elementLayout: null, definitions))
        {
            return false;
        }

        loop = new CanonicalRawPointerLoop(
            function,
            preheader.Id,
            condition.Id,
            body,
            exitBlock.Id,
            induction.ResultName,
            updateReference.Name,
            count,
            definitions,
            nonEntryValueNames);
        return true;
    }

    private static bool TryMatchRawPointerToTemporaryCopyLoop(
        CanonicalRawPointerLoop loop,
        string temporaryLocalName,
        StarkTypeSymbol temporaryElementType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out SsaValue sourceBase,
        out StarkTypeSymbol sourceElementType)
    {
        sourceBase = null!;
        sourceElementType = StarkTypeSymbols.Error;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || loadDefinition is not SsaLoadIndirectRValue load
            || !TryMatchFixedArrayLocalIndexedElementAddress(
                store.Address,
                temporaryLocalName,
                loop.InductionValueName,
                loop.ValueDefinitions,
                out var storedElementType,
                out var temporaryAddressName,
                out var temporaryRootAddressName)
            || !TryMatchRawPointerIndexedElementAddress(
                load.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                requireMutablePointer: false,
                out sourceBase,
                out sourceElementType,
                out var sourceAddressName)
            || NormalizeAggregateType(storedElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(load.Type) != NormalizeAggregateType(temporaryElementType)
            || !CanUseRawPointerMemcpyElement(temporaryElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(temporaryElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            temporaryAddressName,
            sourceAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!string.IsNullOrEmpty(temporaryRootAddressName))
        {
            allowedValueNames.Add(temporaryRootAddressName);
        }

        return BodyContainsOnlyAllowedInstructions(loop.Body, allowedValueNames);
    }

    private static bool TryMatchTemporaryToRawPointerCopyLoop(
        CanonicalRawPointerLoop loop,
        string temporaryLocalName,
        StarkTypeSymbol temporaryElementType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out SsaValue destinationBase,
        out StarkTypeSymbol destinationElementType,
        out SourceLocation? location)
    {
        destinationBase = null!;
        destinationElementType = StarkTypeSymbols.Error;
        location = null;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || loadDefinition is not SsaLoadIndirectRValue load
            || !TryMatchRawPointerIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                requireMutablePointer: true,
                out destinationBase,
                out destinationElementType,
                out var destinationAddressName)
            || !TryMatchFixedArrayLocalIndexedElementAddress(
                load.Address,
                temporaryLocalName,
                loop.InductionValueName,
                loop.ValueDefinitions,
                out var loadedElementType,
                out var temporaryAddressName,
                out var temporaryRootAddressName)
            || NormalizeAggregateType(destinationElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(loadedElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(load.Type) != NormalizeAggregateType(temporaryElementType)
            || !CanUseRawPointerMemcpyElement(temporaryElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(temporaryElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            destinationAddressName,
            temporaryAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!string.IsNullOrEmpty(temporaryRootAddressName))
        {
            allowedValueNames.Add(temporaryRootAddressName);
        }

        if (!BodyContainsOnlyAllowedInstructions(loop.Body, allowedValueNames))
        {
            return false;
        }

        location = store.Location;
        return true;
    }

    private static bool TryMatchFixedArrayLocalIndexedElementAddress(
        SsaValue address,
        string localName,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out StarkTypeSymbol elementType,
        out string addressResultName,
        out string localAddressResultName)
    {
        elementType = StarkTypeSymbols.Error;
        addressResultName = string.Empty;
        localAddressResultName = string.Empty;

        if (address is not SsaValueReference addressReference
            || !definitions.TryGetValue(addressReference.Name, out var definition)
            || definition is not SsaElementAddressRValue elementAddress
            || elementAddress.AggregateType.Kind != StarkTypeKind.FixedArray
            || elementAddress.AggregateType.ElementType is not { } localElementType
            || elementAddress.Index is null
            || elementAddress.ConstantIndex is not null
            || !IsInductionValue(elementAddress.Index, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryMatchAddressOfLocal(
                elementAddress.Address,
                localName,
                elementAddress.AggregateType,
                definitions,
                out localAddressResultName))
        {
            return false;
        }

        elementType = localElementType;
        addressResultName = addressReference.Name;
        return true;
    }

    private static bool TryMatchAddressOfLocal(
        SsaValue address,
        string localName,
        StarkTypeSymbol localType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localAddressResultName)
    {
        localAddressResultName = string.Empty;
        if (address is not SsaValueReference addressReference
            || !definitions.TryGetValue(addressReference.Name, out var definition)
            || definition is not SsaAddressOfLocalRValue addressOfLocal
            || !string.Equals(addressOfLocal.LocalName, localName, StringComparison.Ordinal)
            || NormalizeAggregateType(addressOfLocal.PointeeType) != NormalizeAggregateType(localType))
        {
            return false;
        }

        localAddressResultName = addressReference.Name;
        return true;
    }

    private static bool TryMatchCanonicalIndependentRawPointerLoop(
        SsaFunction function,
        out CanonicalRawPointerLoop loop)
    {
        loop = null!;
        if (!function.HasBody
            || function.ReturnType.Kind != StarkTypeKind.Void
            || function.Blocks.Count != 4)
        {
            return false;
        }

        var definitions = CollectValueDefinitions(function);
        var nonEntryValueNames = CollectNonEntrySsaValueNames(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        if (!blocksById.TryGetValue(function.EntryBlockId, out var entry)
            || entry.Phis.Count != 0
            || entry.Instructions.Count != 0
            || entry.Terminator.Kind != SsaTerminatorKind.Goto
            || entry.Terminator.Targets.Count != 1
            || !blocksById.TryGetValue(entry.Terminator.Targets[0], out var condition)
            || condition.Phis.Count != 1
            || condition.Terminator.Kind != SsaTerminatorKind.Branch
            || condition.Terminator.Condition is null
            || condition.Terminator.Targets.Count != 2)
        {
            return false;
        }

        var bodyId = condition.Terminator.Targets[0];
        var exitId = condition.Terminator.Targets[1];
        if (!blocksById.TryGetValue(bodyId, out var body)
            || !blocksById.TryGetValue(exitId, out var exit)
            || body.Phis.Count != 0
            || body.Terminator.Kind != SsaTerminatorKind.Goto
            || body.Terminator.Targets.Count != 1
            || body.Terminator.Targets[0] != condition.Id
            || exit.Phis.Count != 0
            || exit.Instructions.Count != 0
            || exit.Terminator.Kind != SsaTerminatorKind.Return
            || exit.Terminator.Value is not null)
        {
            return false;
        }

        var induction = condition.Phis[0];
        if (!TryGetPhiIncoming(induction, entry.Id, out var initialValue)
            || !IsZeroIntegerConstant(initialValue)
            || !TryGetPhiIncoming(induction, body.Id, out var updateValue)
            || updateValue is not SsaValueReference updateReference
            || !definitions.TryGetValue(updateReference.Name, out var updateDefinition)
            || !IsIncrementByOne(updateDefinition, induction.ResultName, definitions))
        {
            return false;
        }

        if (!TryResolveComparisonCondition(condition.Terminator.Condition, definitions, out var comparison)
            || comparison.Operator != SsaBinaryOperator.LessThan
            || !IsInductionValue(comparison.Left, induction.ResultName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryResolveEntryAvailableValue(comparison.Right, definitions, nonEntryValueNames, out var count)
            || !CanRepresentRawPointerLoopByteLength(count, elementLayout: null, definitions))
        {
            return false;
        }

        loop = new CanonicalRawPointerLoop(
            function,
            entry.Id,
            condition.Id,
            body,
            exit.Id,
            induction.ResultName,
            updateReference.Name,
            count,
            definitions,
            nonEntryValueNames);
        return true;
    }

    private static bool TryMatchRawPointerMemcpyLoop(
        CanonicalRawPointerLoop loop,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || loadDefinition is not SsaLoadIndirectRValue load
            || !TryMatchRawPointerIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                requireMutablePointer: true,
                out var destinationBase,
                out var destinationElementType,
                out var destinationAddressName)
            || !TryMatchRawPointerIndexedElementAddress(
                load.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                requireMutablePointer: false,
                out var sourceBase,
                out var sourceElementType,
                out var sourceAddressName)
            || NormalizeAggregateType(destinationElementType) != NormalizeAggregateType(sourceElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(load.Type) != NormalizeAggregateType(destinationElementType)
            || AreEquivalentEntryValues(destinationBase, sourceBase, loop.ValueDefinitions, loop.NonEntryValueNames)
            || !HaveNoAliasProofForMemcpy(loop.Function, destinationBase, sourceBase, parameterEffects)
            || !CanUseRawPointerMemcpyElement(destinationElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(destinationElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            destinationAddressName,
            sourceAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!BodyContainsOnlyAllowedInstructions(loop.Body, allowedValueNames))
        {
            return false;
        }

        plan = new RawPointerLoopIntrinsicPlan(
            RawPointerLoopIntrinsicKind.Memcpy,
            destinationBase,
            sourceBase,
            null,
            destinationElementType,
            loop.Count,
            store.Location);
        return true;
    }

    private static bool TryMatchRawPointerMemsetLoop(
        CanonicalRawPointerLoop loop,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!TryGetSingleStore(loop.Body, out var store)
            || !TryMatchRawPointerIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                requireMutablePointer: true,
                out var destinationBase,
                out var destinationElementType,
                out var destinationAddressName)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(store.Value.Type) != NormalizeAggregateType(destinationElementType)
            || !TryResolveEntryAvailableValue(store.Value, loop.ValueDefinitions, loop.NonEntryValueNames, out var fillValue)
            || !CanUseRawPointerMemsetElement(destinationElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(destinationElementType)) is not { } elementLayout
            || elementLayout.SizeBytes != 1
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            destinationAddressName,
            loop.UpdateValueName
        };
        if (!BodyContainsOnlyAllowedInstructions(loop.Body, allowedValueNames))
        {
            return false;
        }

        plan = new RawPointerLoopIntrinsicPlan(
            RawPointerLoopIntrinsicKind.Memset,
            destinationBase,
            null,
            fillValue,
            destinationElementType,
            loop.Count,
            store.Location);
        return true;
    }

    private static bool TryGetSingleStore(SsaBasicBlock body, out SsaStoreIndirectInstruction store)
    {
        store = null!;
        foreach (var instruction in body.Instructions)
        {
            if (instruction is not SsaStoreIndirectInstruction candidate)
            {
                continue;
            }

            if (store is not null)
            {
                store = null!;
                return false;
            }

            store = candidate;
        }

        return store is not null;
    }

    private static bool TryMatchRawPointerIndexedElementAddress(
        SsaValue address,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        bool requireMutablePointer,
        out SsaValue basePointer,
        out StarkTypeSymbol elementType,
        out string addressResultName)
    {
        basePointer = null!;
        elementType = StarkTypeSymbols.Error;
        addressResultName = string.Empty;

        if (address is not SsaValueReference addressReference
            || !definitions.TryGetValue(addressReference.Name, out var definition)
            || definition is not SsaElementAddressRValue elementAddress
            || elementAddress.Address.Type.Kind != StarkTypeKind.RawPointer
            || elementAddress.Address.Type.ElementType is not { } rawElementType
            || requireMutablePointer && !elementAddress.Address.Type.IsMutablePointer
            || NormalizeAggregateType(elementAddress.AggregateType) != NormalizeAggregateType(rawElementType)
            || elementAddress.Index is null
            || elementAddress.ConstantIndex is not null
            || !IsInductionValue(elementAddress.Index, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryResolveEntryAvailableValue(elementAddress.Address, definitions, nonEntryValueNames, out var resolvedBasePointer))
        {
            return false;
        }

        basePointer = resolvedBasePointer;
        elementType = rawElementType;
        addressResultName = addressReference.Name;
        return true;
    }

    private static bool BodyContainsOnlyAllowedInstructions(SsaBasicBlock body, ISet<string> allowedValueNames)
    {
        foreach (var instruction in body.Instructions)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    if (!allowedValueNames.Contains(valueInstruction.ResultName))
                    {
                        return false;
                    }

                    break;
                case SsaStoreIndirectInstruction:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryGetPhiIncoming(SsaPhi phi, int predecessorBlockId, out SsaValue value)
    {
        foreach (var incoming in phi.Incomings)
        {
            if (incoming.PredecessorBlockId == predecessorBlockId)
            {
                value = incoming.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static bool IsZeroIntegerConstant(SsaValue value)
    {
        return value is SsaIntegerConstant { Value.IsZero: true };
    }

    private static bool IsOneIntegerConstant(SsaValue value)
    {
        return value is SsaIntegerConstant { Value.IsOne: true };
    }

    private static bool IsIncrementByOne(
        SsaRValue definition,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return definition is SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Add or SsaBinaryOperator.WrappingAdd
            } binary
            && (IsInductionValue(binary.Left, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
                    && IsOneIntegerConstant(binary.Right)
                || IsInductionValue(binary.Right, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
                    && IsOneIntegerConstant(binary.Left));
    }

    private static bool IsInductionValue(
        SsaValue value,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        if (string.Equals(reference.Name, inductionValueName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => IsInductionValue(use.Value, inductionValueName, definitions, visitedValueNames),
            SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert) =>
                IsInductionValue(convert.Operand, inductionValueName, definitions, visitedValueNames),
            SsaBinaryRValue binary when IsAddZero(binary, definitions) =>
                IsInductionValue(
                    IsZeroIntegerValue(binary.Left, definitions, new HashSet<string>(StringComparer.Ordinal))
                        ? binary.Right
                        : binary.Left,
                    inductionValueName,
                    definitions,
                    visitedValueNames),
            _ => false
        };
    }

    private static bool TryResolveEntryAvailableValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        out SsaValue resolved)
    {
        return TryResolveEntryAvailableValue(
            value,
            definitions,
            nonEntryValueNames,
            new HashSet<string>(StringComparer.Ordinal),
            out resolved);
    }

    private static bool TryResolveEntryAvailableValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        ISet<string> visitedValueNames,
        out SsaValue resolved)
    {
        switch (value)
        {
            case SsaIntegerConstant
                or SsaFloatConstant
                or SsaStringConstant
                or SsaBoolConstant
                or SsaNullConstant
                or SsaGlobalAddressValue
                or SsaFunctionAddressValue:
                resolved = value;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    resolved = null!;
                    return false;
                }

                if (definitions.TryGetValue(reference.Name, out var definition))
                {
                    if (definition is SsaUseRValue use)
                    {
                        return TryResolveEntryAvailableValue(use.Value, definitions, nonEntryValueNames, visitedValueNames, out resolved);
                    }

                    if (definition is SsaBinaryRValue binary && IsAddZero(binary, definitions))
                    {
                        return TryResolveEntryAvailableValue(
                            IsZeroIntegerValue(binary.Left, definitions, new HashSet<string>(StringComparer.Ordinal))
                                ? binary.Right
                                : binary.Left,
                            definitions,
                            nonEntryValueNames,
                            visitedValueNames,
                            out resolved);
                    }

                    resolved = null!;
                    return false;
                }

                if (nonEntryValueNames.Contains(reference.Name))
                {
                    resolved = null!;
                    return false;
                }

                resolved = value;
                return true;
            default:
                resolved = null!;
                return false;
        }
    }

    private static bool AreEquivalentEntryValues(
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames)
    {
        return TryResolveEntryAvailableValue(left, definitions, nonEntryValueNames, out var resolvedLeft)
            && TryResolveEntryAvailableValue(right, definitions, nonEntryValueNames, out var resolvedRight)
            && Equals(resolvedLeft, resolvedRight);
    }

    private static bool IsAddZero(
        SsaBinaryRValue binary,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return binary.Operator is SsaBinaryOperator.Add or SsaBinaryOperator.WrappingAdd
            && (IsZeroIntegerValue(binary.Left, definitions, new HashSet<string>(StringComparer.Ordinal))
                || IsZeroIntegerValue(binary.Right, definitions, new HashSet<string>(StringComparer.Ordinal)));
    }

    private static bool IsZeroIntegerValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaIntegerConstant { Value.IsZero: true } => true,
            SsaValueReference reference when visitedValueNames.Add(reference.Name)
                                             && definitions.TryGetValue(reference.Name, out var definition) =>
                definition switch
                {
                    SsaUseRValue use => IsZeroIntegerValue(use.Value, definitions, visitedValueNames),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Integer =>
                        IsZeroIntegerValue(convert.Operand, definitions, visitedValueNames),
                    _ => false
                },
            _ => false
        };
    }

    private static bool HaveNoAliasProofForMemcpy(
        SsaFunction function,
        SsaValue destinationBase,
        SsaValue sourceBase,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        return TryResolveParameterName(function, destinationBase, out var destinationParameter)
            && TryResolveParameterName(function, sourceBase, out var sourceParameter)
            && !string.Equals(destinationParameter, sourceParameter, StringComparison.Ordinal)
            && ParameterHasNoAliasProof(function, destinationParameter, parameterEffects)
            && ParameterHasNoAliasProof(function, sourceParameter, parameterEffects);
    }

    private static bool TryResolveParameterName(
        SsaFunction function,
        SsaValue value,
        out string parameterName)
    {
        if (value is SsaValueReference reference)
        {
            foreach (var parameter in function.Parameters)
            {
                if (string.Equals(reference.Name, parameter.Name, StringComparison.Ordinal)
                    || string.Equals(reference.Name, $"arg_{parameter.Name}", StringComparison.Ordinal))
                {
                    parameterName = parameter.Name;
                    return true;
                }
            }
        }

        parameterName = string.Empty;
        return false;
    }

    private static bool ParameterHasNoAliasProof(
        SsaFunction function,
        string parameterName,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (function.Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, parameterName, StringComparison.Ordinal))
                is { IsDisjoint: true })
        {
            return true;
        }

        return parameterEffects is not null
            && parameterEffects.TryGetValue(parameterName, out var effects)
            && effects.GuaranteedNoAlias;
    }

    private static bool CanUseRawPointerMemcpyElement(StarkTypeSymbol elementType)
    {
        return NormalizeAggregateType(elementType).Kind is
            StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static bool CanUseRawPointerMemsetElement(StarkTypeSymbol elementType)
    {
        var normalizedType = NormalizeAggregateType(elementType);
        return normalizedType.Kind == StarkTypeKind.Integer
            && normalizedType.BitWidth == 8;
    }

    private static bool CanRepresentRawPointerLoopByteLength(
        SsaValue count,
        ConcreteTypeLayout? elementLayout,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (elementLayout is null)
        {
            if (TryResolveIntegerConstant(count, definitions, new HashSet<string>(StringComparer.Ordinal), out var constant))
            {
                return constant >= BigInteger.Zero;
            }

            return TryGetIntegerTypeRange(count.Type, out var nonSizedMinCount, out _)
                && nonSizedMinCount >= BigInteger.Zero;
        }

        if (elementLayout.SizeBytes <= 0)
        {
            return false;
        }

        if (TryResolveIntegerConstant(count, definitions, new HashSet<string>(StringComparer.Ordinal), out var constantCount))
        {
            return constantCount >= BigInteger.Zero
                && constantCount * elementLayout.SizeBytes <= long.MaxValue;
        }

        return count.Type.Kind == StarkTypeKind.Integer
            && count.Type.BitWidth is > 0 and <= 64
            && TryGetIntegerTypeRange(count.Type, out var minCount, out var maxCount)
            && minCount >= BigInteger.Zero
            && maxCount * elementLayout.SizeBytes <= long.MaxValue;
    }

    private static ISet<string> CollectNonEntrySsaValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                names.Add(phi.ResultName);
            }

            foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                names.Add(instruction.ResultName);
            }
        }

        return names;
    }

    private enum RawPointerLoopIntrinsicKind
    {
        Memcpy,
        Memmove,
        Memset
    }

    private sealed record RawPointerLoopIntrinsicPlan(
        RawPointerLoopIntrinsicKind Kind,
        SsaValue DestinationBase,
        SsaValue? SourceBase,
        SsaValue? FillValue,
        StarkTypeSymbol ElementType,
        SsaValue Count,
        SourceLocation? Location,
        int? PreheaderBlockId = null,
        int? ExitBlockId = null,
        IReadOnlyList<int>? SkippedBlockIds = null);

    private sealed record CanonicalRawPointerLoop(
        SsaFunction Function,
        int PreheaderBlockId,
        int ConditionBlockId,
        SsaBasicBlock Body,
        int ExitBlockId,
        string InductionValueName,
        string UpdateValueName,
        SsaValue Count,
        IReadOnlyDictionary<string, SsaRValue> ValueDefinitions,
        ISet<string> NonEntryValueNames);

    private void EmitPhi(SsaPhi phi)
    {
        var incoming = string.Join(
            ", ",
            phi.Incomings.Select(entry => $"[ {FormatValue(entry.Value)}, %{FormatPhiIncomingBlockLabel(entry.PredecessorBlockId)} ]"));
        AppendLine($"  %{EscapeIdentifier(phi.ResultName)} = phi{GetFastMathSuffix(phi.Type)} {MapType(phi.Type)} {incoming}");
    }

    private void EmitAssumptionsForBlock(int blockId)
    {
        if (!_assumptionsByBlock.TryGetValue(blockId, out var assumptions))
        {
            return;
        }

        foreach (var assumption in assumptions)
        {
            var condition = assumption.Condition is null
                ? "true"
                : FormatValue(assumption.Condition);
            if (assumption.NegateCondition)
            {
                var negated = $"%{EscapeIdentifier(CreateAbiTempName($"assume_not_{_nextAssumeTempId++}"))}";
                AppendLine($"  {negated} = xor i1 {condition}, true");
                condition = negated;
            }

            var operandBundleSuffix = assumption.OperandBundles.Count == 0
                ? string.Empty
                : " [" + string.Join(", ", assumption.OperandBundles.Select(RenderAssumeOperandBundle)) + "]";
            AppendLine($"  call void @llvm.assume(i1 {condition}){operandBundleSuffix}");
        }
    }

    private string RenderAssumeOperandBundle(LlvmAssumeOperandBundle bundle)
    {
        return bundle.Kind switch
        {
            LlvmAssumeOperandBundleKind.NonNull => $"\"nonnull\"(ptr {FormatValue(bundle.Pointer)})",
            LlvmAssumeOperandBundleKind.Align when bundle.AlignmentBytes is int alignmentBytes =>
                $"\"align\"(ptr {FormatValue(bundle.Pointer)}, i64 {alignmentBytes})",
            _ => throw new UnsupportedBodyEmissionException("Unsupported llvm.assume operand bundle.")
        };
    }

    private IReadOnlyDictionary<int, IReadOnlyList<LlvmAssumeFact>> BuildAssumptionsByBlock()
    {
        var assumptionsByBlock = new Dictionary<int, List<LlvmAssumeFact>>();

        foreach (var block in _ssaFunction.Blocks)
        {
            if (block.Terminator.Kind != SsaTerminatorKind.Branch
                || block.Terminator.Condition is null
                || block.Terminator.Targets.Count != 2)
            {
                continue;
            }

            AddAssumptionsForTarget(block, targetIndex: 0, assumeConditionTrue: true);
            AddAssumptionsForTarget(block, targetIndex: 1, assumeConditionTrue: false);
        }

        return assumptionsByBlock.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<LlvmAssumeFact>)entry.Value,
            EqualityComparer<int>.Default);

        void AddAssumptionsForTarget(SsaBasicBlock sourceBlock, int targetIndex, bool assumeConditionTrue)
        {
            var targetBlockId = sourceBlock.Terminator.Targets[targetIndex];
            if (!CanEmitAssumeInSuccessor(_ssaFunction.EntryBlockId, sourceBlock.Id, targetBlockId, _blockOrderById, _predecessorCounts))
            {
                return;
            }

            var assumptions = BuildAssumeFacts(sourceBlock.Terminator.Condition!, assumeConditionTrue);
            if (assumptions.Count == 0)
            {
                return;
            }

            if (!assumptionsByBlock.TryGetValue(targetBlockId, out var targetAssumptions))
            {
                targetAssumptions = [];
                assumptionsByBlock[targetBlockId] = targetAssumptions;
            }

            targetAssumptions.AddRange(assumptions);
        }
    }

    private IReadOnlyList<LlvmAssumeFact> BuildAssumeFacts(SsaValue condition, bool assumeConditionTrue)
    {
        if (!TryResolveComparisonCondition(condition, _valueDefinitions, out var comparison))
        {
            return [];
        }

        var assumptions = new List<LlvmAssumeFact>();
        AddPointerAssumeFacts(comparison, assumeConditionTrue, assumptions);

        if (IsIntegerValueRangeNarrowingComparison(comparison, _valueDefinitions))
        {
            assumptions.Add(new LlvmAssumeFact(condition, !assumeConditionTrue, []));
        }

        return assumptions;
    }

    private void AddPointerAssumeFacts(
        SsaBinaryRValue comparison,
        bool assumeConditionTrue,
        List<LlvmAssumeFact> assumptions)
    {
        var pointerFacts = new Dictionary<string, List<LlvmAssumeOperandBundle>>(StringComparer.Ordinal);

        if (TryGetNullComparedPointer(comparison, out var nullCheckedPointer, out var nonNullWhenConditionTrue)
            && assumeConditionTrue == nonNullWhenConditionTrue)
        {
            AddPointerBundle(nullCheckedPointer, LlvmAssumeOperandBundleKind.NonNull);
        }

        if (GetAssumedComparisonOperator(comparison.Operator, assumeConditionTrue) == SsaBinaryOperator.Equal
            && comparison.Left.Type.Kind == StarkTypeKind.RawPointer
            && comparison.Right.Type.Kind == StarkTypeKind.RawPointer)
        {
            AddEqualityDerivedPointerFacts(comparison.Left, comparison.Right);
            AddEqualityDerivedPointerFacts(comparison.Right, comparison.Left);
        }

        foreach (var bundles in pointerFacts.Values)
        {
            assumptions.Add(new LlvmAssumeFact(null, false, bundles));
        }

        void AddEqualityDerivedPointerFacts(SsaValue factSource, SsaValue factTarget)
        {
            if (!IsKnownNonNullPointerValue(factSource, new HashSet<string>(StringComparer.Ordinal)))
            {
                return;
            }

            AddPointerBundle(factTarget, LlvmAssumeOperandBundleKind.NonNull);
            if (TryGetPointerElementType(factTarget, out var pointeeType)
                && TryGetKnownPointerAlignmentBytes(factSource, pointeeType, out var alignmentBytes))
            {
                AddPointerBundle(factTarget, LlvmAssumeOperandBundleKind.Align, alignmentBytes);
            }
        }

        void AddPointerBundle(
            SsaValue pointer,
            LlvmAssumeOperandBundleKind kind,
            int? alignmentBytes = null)
        {
            if (pointer.Type.Kind != StarkTypeKind.RawPointer)
            {
                return;
            }

            if (kind == LlvmAssumeOperandBundleKind.Align && alignmentBytes is not > 1)
            {
                return;
            }

            var key = FormatAssumePointerKey(pointer);
            if (!pointerFacts.TryGetValue(key, out var bundles))
            {
                bundles = [];
                pointerFacts[key] = bundles;
            }

            if (bundles.Any(existing => existing.Kind == kind && existing.AlignmentBytes == alignmentBytes))
            {
                return;
            }

            bundles.Add(new LlvmAssumeOperandBundle(kind, pointer, alignmentBytes));
        }
    }

    private bool IsKnownNonNullPointerValue(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is SsaNullConstant)
        {
            return false;
        }

        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaFunctionAddressValue => true,
            SsaValueReference reference when reference.Type.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerReference(reference, visitedValueNames),
            _ => value.Type.Kind == StarkTypeKind.RawPointer
                && TryGetValueDefinition(value, visitedValueNames, out var definition)
                && IsKnownNonNullPointerDefinition(definition, visitedValueNames)
        };
    }

    private bool IsKnownNonNullPointerReference(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return IsKnownNonNullPointerDefinition(definition, visitedValueNames);
        }

        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
        return parameter is not null
            && parameter.LlvmType.Kind == StarkTypeKind.RawPointer
            && (parameter.SourceType.BorrowKind != StarkBorrowKind.None
                || parameter.SourceType.InitializationKind != StarkInitializationKind.None);
    }

    private bool IsKnownNonNullPointerDefinition(SsaRValue definition, ISet<string> visitedValueNames)
    {
        return definition switch
        {
            SsaUseRValue use => IsKnownNonNullPointerValue(use.Value, visitedValueNames),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerValue(convert.Operand, visitedValueNames),
            SsaSelectRValue select when select.Type.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerValue(select.WhenTrue, new HashSet<string>(visitedValueNames, StringComparer.Ordinal))
                && IsKnownNonNullPointerValue(select.WhenFalse, new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            SsaAddressOfLocalRValue => true,
            SsaAddressOfParameterRValue => true,
            SsaFieldAddressRValue fieldAddress => IsKnownNonNullPointerValue(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => IsKnownNonNullPointerValue(elementAddress.Address, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => IsKnownNonNullPointerValue(sliceElementAddress.Slice, visitedValueNames),
            _ => false
        };
    }

    private bool TryGetValueDefinition(
        SsaValue value,
        ISet<string> visitedValueNames,
        out SsaRValue definition)
    {
        if (value is SsaValueReference reference
            && visitedValueNames.Add(reference.Name)
            && _valueDefinitions.TryGetValue(reference.Name, out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    private static string FormatAssumePointerKey(SsaValue pointer)
    {
        return pointer switch
        {
            SsaValueReference reference => $"ref:{reference.Name}",
            SsaGlobalAddressValue global => $"global:{global.GlobalName}",
            _ => $"value:{pointer.Text}:{pointer.Type.DisplayName}"
        };
    }

    private static bool TryGetPointerElementType(SsaValue pointer, out StarkTypeSymbol pointeeType)
    {
        if (pointer.Type.Kind == StarkTypeKind.RawPointer && pointer.Type.ElementType is { } elementType)
        {
            pointeeType = elementType;
            return true;
        }

        pointeeType = StarkTypeSymbols.Error;
        return false;
    }

    private void EmitInstruction(SsaInstruction instruction)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                EmitValueInstruction(valueInstruction);
                return;
            case SsaAllocateLocalInstruction allocateLocal:
                EmitAllocateLocal(allocateLocal);
                return;
            case SsaLifetimeStartInstruction lifetimeStart:
                EmitLifetimeStart(lifetimeStart);
                return;
            case SsaLifetimeEndInstruction lifetimeEnd:
                EmitLifetimeEnd(lifetimeEnd);
                return;
            case SsaDeallocateLocalInstruction deallocateLocal:
                EmitDeallocateLocal(deallocateLocal);
                return;
            case SsaStoreLocalInstruction storeLocal:
                EmitStoreLocal(storeLocal);
                return;
            case SsaCopyMemoryInstruction copyMemory:
                EmitCopyMemory(copyMemory);
                return;
            case SsaStoreIndirectInstruction storeIndirect:
                EmitStoreIndirect(storeIndirect);
                return;
            case SsaStoreGlobalInstruction storeGlobal:
                AppendLine(
                    $"  store {MapType(storeGlobal.GlobalType)} {FormatValue(storeGlobal.Value)}, ptr @{EscapeIdentifier(ResolveGlobalSymbolName(storeGlobal.GlobalName))}{GetGlobalObjectAlignmentSuffix(storeGlobal.GlobalName, storeGlobal.GlobalType)}{GetDirectTbaaMetadataSuffix(CreateTbaaGlobalRootKey(storeGlobal.GlobalName), storeGlobal.GlobalType)}");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA instruction '{instruction.GetType().Name}'.");
        }
    }

    private void EmitValueInstruction(SsaValueInstruction instruction)
    {
        var result = $"%{EscapeIdentifier(instruction.ResultName)}";
        switch (instruction.Value)
        {
            case SsaUseRValue use:
                AppendLine($"  {result} = add {MapType(use.Type)} {FormatValue(use.Value)}, 0");
                return;
            case SsaLoadGlobalRValue load:
                AppendLine(
                    $"  {result} = load {MapType(load.Type)}, ptr @{EscapeIdentifier(ResolveGlobalSymbolName(load.GlobalName))}{GetGlobalObjectAlignmentSuffix(load.GlobalName, load.Type)}{GetInvariantLoadMetadataSuffix(load.GlobalName)}{GetValueRangeMetadataSuffix(instruction.ResultName, load.Type)}{GetDirectTbaaMetadataSuffix(CreateTbaaGlobalRootKey(load.GlobalName), load.Type)}");
                return;
            case SsaLoadLocalRValue loadLocal:
                EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                AppendLine($"  {result} = load {MapType(loadLocal.Type)}, ptr %{EscapeIdentifier($"slot_{loadLocal.LocalName}")}{GetLocalObjectAlignmentSuffix(loadLocal.LocalName, loadLocal.Type)}{GetInvariantLocalLoadMetadataSuffix(loadLocal.LocalName)}{GetValueRangeMetadataSuffix(instruction.ResultName, loadLocal.Type)}{GetDirectTbaaMetadataSuffix(CreateTbaaLocalRootKey(loadLocal.LocalName), loadLocal.Type)}");
                return;
            case SsaConvertRValue convert:
                EmitConvert(instruction.ResultName, result, convert);
                return;
            case SsaExtractFieldRValue extract:
                if (TryEmitAggregateElementLoad(result, extract.Target, extract.FieldIndex, extract.Type, "extract_field_load"))
                {
                    return;
                }

                AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {extract.FieldIndex}");
                return;
            case SsaInsertFieldRValue insert:
                AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatValue(insert.Target)}, {MapType(insert.Value.Type)} {FormatValue(insert.Value)}, {insert.FieldIndex}");
                return;
            case SsaExtractIndexRValue extractIndex:
                if (TryEmitAggregateElementLoad(result, extractIndex.Target, extractIndex.ElementIndex, extractIndex.Type, "extract_index_load"))
                {
                    return;
                }

                AppendLine($"  {result} = extractvalue {MapType(extractIndex.Target.Type)} {FormatValue(extractIndex.Target)}, {extractIndex.ElementIndex}");
                return;
            case SsaInsertIndexRValue insertIndex:
                AppendLine($"  {result} = insertvalue {MapType(insertIndex.Target.Type)} {FormatValue(insertIndex.Target)}, {MapType(insertIndex.Value.Type)} {FormatValue(insertIndex.Value)}, {insertIndex.ElementIndex}");
                return;
            case SsaMakeSliceFromLocalRValue makeSlice:
                EmitMakeSliceFromLocal(result, makeSlice);
                return;
            case SsaMakeSliceFromPointerRValue makeSlice:
                EmitMakeSliceFromPointer(result, makeSlice);
                return;
            case SsaLoadSliceElementRValue loadSlice:
                EmitLoadSliceElement(result, loadSlice, instruction.ScopedNoAliasGroups, instruction.LoopAccessGroups);
                return;
            case SsaTextSliceRValue textSlice:
                EmitTextSlice(result, textSlice);
                return;
            case SsaAddressOfLocalRValue addressOfLocal:
                EmitAddressOfLocal(result, addressOfLocal);
                return;
            case SsaAddressOfParameterRValue addressOfParameter:
                EmitAddressOfParameter(result, addressOfParameter);
                return;
            case SsaFieldAddressRValue fieldAddress:
                EmitFieldAddress(result, fieldAddress);
                return;
            case SsaElementAddressRValue elementAddress:
                EmitElementAddress(result, elementAddress);
                return;
            case SsaSliceElementAddressRValue sliceElementAddress:
                EmitSliceElementAddress(result, sliceElementAddress);
                return;
            case SsaLoadIndirectRValue loadIndirect:
                AppendLine(
                    $"  {result} = load {MapType(loadIndirect.Type)}, ptr {FormatValue(loadIndirect.Address)}{GetKnownPointerAlignmentSuffix(loadIndirect.Address, loadIndirect.Type)}{GetInvariantLoadMetadataSuffix(loadIndirect.Address)}{GetValueRangeMetadataSuffix(instruction.ResultName, loadIndirect.Type)}{GetTbaaMetadataSuffix(loadIndirect.Address, loadIndirect.Type)}{GetScopedNoAliasMetadataSuffix(loadIndirect.Address, instruction.ScopedNoAliasGroups)}{GetLoopAccessGroupMetadataSuffix(instruction.LoopAccessGroups)}");
                return;
            case SsaUnaryRValue unary:
                EmitUnary(result, unary);
                return;
            case SsaBinaryRValue binary:
                EmitBinary(result, binary);
                return;
            case SsaSelectRValue select:
                EmitSelect(result, select);
                return;
            case SsaCallRValue call:
                EmitCall(instruction.ResultName, result, call);
                return;
            case SsaIndirectCallRValue indirectCall:
                EmitIndirectCall(instruction.ResultName, result, indirectCall);
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA rvalue '{instruction.Value.GetType().Name}'.");
        }
    }

    private void EmitConvert(string resultName, string result, SsaConvertRValue convert)
    {
        var sourceType = convert.Operand.Type;
        var targetType = convert.TargetType;

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
        {
            if (sourceType.BitWidth == targetType.BitWidth)
            {
                AppendLine($"  {result} = add {MapType(targetType)} {FormatValue(convert.Operand)}, 0");
                return;
            }

            var opcode = sourceType.BitWidth < targetType.BitWidth
                ? (HasUnsignedIntegerSemantics(sourceType) ? "zext" : "sext")
                : "trunc";
            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
        {
            var opcode = HasUnsignedIntegerSemantics(sourceType) ? "uitofp" : "sitofp";
            if (_isStrictFp)
            {
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedIntegerToFloatIntrinsicName(sourceType, targetType, opcode)}({MapType(sourceType)} {FormatValue(convert.Operand)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
        {
            var opcode = HasUnsignedIntegerSemantics(targetType) ? "fptoui" : "fptosi";
            if (_isStrictFp)
            {
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedFloatToIntegerIntrinsicName(sourceType, targetType, opcode)}({MapType(sourceType)} {FormatValue(convert.Operand)}, metadata !\"fpexcept.strict\") strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
        {
            if (sourceType.BitWidth == targetType.BitWidth)
            {
                if (_isStrictFp)
                {
                    AppendLine($"  {result} = select i1 true, {MapType(targetType)} {FormatValue(convert.Operand)}, {MapType(targetType)} {FormatValue(convert.Operand)}");
                    return;
                }

                AppendLine($"  {result} = fadd{GetFastMathSuffix()} {MapType(targetType)} {FormatValue(convert.Operand)}, 0.0");
                return;
            }

            var opcode = sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc";
            if (_isStrictFp)
            {
                var roundingAndExceptionMetadata = opcode == "fptrunc"
                    ? ", metadata !\"round.dynamic\", metadata !\"fpexcept.strict\""
                    : ", metadata !\"fpexcept.strict\"";
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedFloatConversionIntrinsicName(sourceType, targetType)}({MapType(sourceType)} {FormatValue(convert.Operand)}{roundingAndExceptionMetadata}) strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode}{GetFastMathSuffix()} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
        {
            AppendLine($"  {result} = inttoptr {MapType(sourceType)} {FormatValue(convert.Operand)} to ptr");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.RawPointer)
        {
            AppendLine($"  {result} = select i1 true, ptr {FormatValue(convert.Operand)}, ptr {FormatValue(convert.Operand)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.Integer)
        {
            AppendLine($"  {result} = ptrtoint ptr {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported SSA conversion from '{sourceType.DisplayName}' to '{targetType.DisplayName}'.");
    }

    private void EmitUnary(string result, SsaUnaryRValue unary)
    {
        switch (unary.Operator)
        {
            case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Integer:
                AppendLine($"  {result} = sub {MapType(unary.Type)} 0, {FormatValue(unary.Operand)}");
                return;
            case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Float:
                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call {MapType(unary.Type)} @{GetConstrainedUnaryIntrinsicName("fneg", unary.Type)}({MapType(unary.Type)} {FormatValue(unary.Operand)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                    return;
                }

                AppendLine($"  {result} = fneg{GetFastMathSuffix()} {MapType(unary.Type)} {FormatValue(unary.Operand)}");
                return;
            case SsaUnaryOperator.LogicalNot:
                AppendLine($"  {result} = xor i1 {FormatValue(unary.Operand)}, true");
                return;
            case SsaUnaryOperator.BitwiseNot:
                AppendLine($"  {result} = xor {MapType(unary.Type)} {FormatValue(unary.Operand)}, -1");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA unary operator '{unary.Operator}'.");
        }
    }

    private void EmitSelect(string result, SsaSelectRValue select)
    {
        AppendLine(
            $"  {result} = select i1 {FormatValue(select.Condition)}, {MapType(select.Type)} {FormatValue(select.WhenTrue)}, {MapType(select.Type)} {FormatValue(select.WhenFalse)}");
    }

    private void EmitBinary(string result, SsaBinaryRValue binary)
    {
        if (binary.Type.Kind == StarkTypeKind.Integer)
        {
            if (binary.Operator is SsaBinaryOperator.SaturatingAdd or SsaBinaryOperator.SaturatingSubtract or SsaBinaryOperator.SaturatingMultiply)
            {
                EmitSaturatingIntegerBinary(result, binary);
                return;
            }

            var opcode = binary.Operator switch
            {
                SsaBinaryOperator.Add => "add",
                SsaBinaryOperator.Subtract => "sub",
                SsaBinaryOperator.Multiply => "mul",
                SsaBinaryOperator.WrappingAdd => "add",
                SsaBinaryOperator.WrappingSubtract => "sub",
                SsaBinaryOperator.WrappingMultiply => "mul",
                SsaBinaryOperator.Divide => CanUseUnsignedIntegerDivisionSemantics(binary) ? "udiv" : "sdiv",
                SsaBinaryOperator.Modulo => CanUseUnsignedIntegerDivisionSemantics(binary) ? "urem" : "srem",
                SsaBinaryOperator.BitwiseAnd => "and",
                SsaBinaryOperator.BitwiseXor => "xor",
                SsaBinaryOperator.BitwiseOr => "or",
                SsaBinaryOperator.ShiftLeft => "shl",
                SsaBinaryOperator.ShiftRight => HasUnsignedIntegerSemantics(binary.Left.Type) ? "lshr" : "ashr",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(opcode))
            {
                AppendLine($"  {result} = {opcode}{GetIntegerInstructionFlags(binary)} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Operator == SsaBinaryOperator.Exponent)
        {
            if (binary.Type.Kind == StarkTypeKind.Float)
            {
                EmitFloatExponent(result, binary);
                return;
            }

            if (binary.Type.Kind == StarkTypeKind.Integer)
            {
                EmitIntegerExponent(result, binary);
                return;
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported exponent operator type '{binary.Type.DisplayName}'.");
        }

        if (binary.Type.Kind == StarkTypeKind.Float)
        {
            if (TryEmitFusedMultiplyAdd(result, binary))
            {
                return;
            }

            var opcode = binary.Operator switch
            {
                SsaBinaryOperator.Add => "fadd",
                SsaBinaryOperator.Subtract => "fsub",
                SsaBinaryOperator.Multiply => "fmul",
                SsaBinaryOperator.Divide => "fdiv",
                SsaBinaryOperator.Modulo => "frem",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(opcode))
            {
                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call {MapType(binary.Type)} @{GetConstrainedBinaryIntrinsicName(opcode, binary.Type)}({MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {MapType(binary.Right.Type)} {FormatValue(binary.Right)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                    return;
                }

                AppendLine($"  {result} = {opcode}{GetFastMathSuffix()} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Type.Kind == StarkTypeKind.Bool)
        {
            if (binary.Left.Type.Kind == StarkTypeKind.Bool && binary.Right.Type.Kind == StarkTypeKind.Bool)
            {
                var booleanOpcode = binary.Operator switch
                {
                    SsaBinaryOperator.BitwiseAnd => "and",
                    SsaBinaryOperator.BitwiseOr => "or",
                    SsaBinaryOperator.BitwiseXor => "xor",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(booleanOpcode))
                {
                    AppendLine($"  {result} = {booleanOpcode} i1 {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    SsaBinaryOperator.LessThan => ShouldUseUnsignedIntegerComparison(binary) ? "ult" : "slt",
                    SsaBinaryOperator.LessThanOrEqual => ShouldUseUnsignedIntegerComparison(binary) ? "ule" : "sle",
                    SsaBinaryOperator.GreaterThan => ShouldUseUnsignedIntegerComparison(binary) ? "ugt" : "sgt",
                    SsaBinaryOperator.GreaterThanOrEqual => ShouldUseUnsignedIntegerComparison(binary) ? "uge" : "sge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    AppendLine($"  {result} = icmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.Float)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "oeq",
                    SsaBinaryOperator.NotEqual => "one",
                    SsaBinaryOperator.LessThan => "olt",
                    SsaBinaryOperator.LessThanOrEqual => "ole",
                    SsaBinaryOperator.GreaterThan => "ogt",
                    SsaBinaryOperator.GreaterThanOrEqual => "oge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    if (_isStrictFp)
                    {
                        AppendLine(
                            $"  {result} = call i1 @{GetConstrainedFloatCompareIntrinsicName(binary.Left.Type)}({MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {MapType(binary.Right.Type)} {FormatValue(binary.Right)}, metadata !\"{predicate}\", metadata !\"fpexcept.strict\") strictfp");
                        return;
                    }

                    AppendLine($"  {result} = fcmp{GetFastMathSuffix()} {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.RawPointer)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    SsaBinaryOperator.LessThan => "ult",
                    SsaBinaryOperator.LessThanOrEqual => "ule",
                    SsaBinaryOperator.GreaterThan => "ugt",
                    SsaBinaryOperator.GreaterThanOrEqual => "uge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    AppendLine($"  {result} = icmp {predicate} ptr {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (TryEmitTextEquality(result, binary))
            {
                return;
            }

            if (TryEmitTextOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitFixedArrayOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitScalarizedNamedAggregateOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitSliceEquality(
                    result,
                    binary.Operator,
                    binary.Left.Type,
                    FormatValue(binary.Left),
                    FormatValue(binary.Right)))
            {
                return;
            }

            if (TryEmitScalarizedAggregateEquality(result, binary))
            {
                return;
            }
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported SSA binary operator '{binary.Operator}' for '{binary.Left.Type.DisplayName}'.");
    }

    private bool TryEmitFusedMultiplyAdd(string result, SsaBinaryRValue binary)
    {
        if (_isStrictFp
            || binary.Type.Kind != StarkTypeKind.Float
            || binary.Operator is not (SsaBinaryOperator.Add or SsaBinaryOperator.Subtract))
        {
            return false;
        }

        if (binary.Operator == SsaBinaryOperator.Add)
        {
            if (TryResolveFloatingMultiply(binary.Left, binary.Type, out var leftMultiply))
            {
                EmitFusedMultiplyAdd(
                    result,
                    binary.Type,
                    FormatValue(leftMultiply.Left),
                    FormatValue(leftMultiply.Right),
                    FormatValue(binary.Right));
                return true;
            }

            if (TryResolveFloatingMultiply(binary.Right, binary.Type, out var rightMultiply))
            {
                EmitFusedMultiplyAdd(
                    result,
                    binary.Type,
                    FormatValue(rightMultiply.Left),
                    FormatValue(rightMultiply.Right),
                    FormatValue(binary.Left));
                return true;
            }

            return false;
        }

        if (TryResolveFloatingMultiply(binary.Left, binary.Type, out var minuendMultiply))
        {
            var negatedSubtrahend = EmitFastFloatNegation(binary.Type, binary.Right);
            EmitFusedMultiplyAdd(
                result,
                binary.Type,
                FormatValue(minuendMultiply.Left),
                FormatValue(minuendMultiply.Right),
                negatedSubtrahend);
            return true;
        }

        if (TryResolveFloatingMultiply(binary.Right, binary.Type, out var subtrahendMultiply))
        {
            var negatedFactor = EmitFastFloatNegation(binary.Type, subtrahendMultiply.Left);
            EmitFusedMultiplyAdd(
                result,
                binary.Type,
                negatedFactor,
                FormatValue(subtrahendMultiply.Right),
                FormatValue(binary.Left));
            return true;
        }

        return false;
    }

    private bool TryResolveFloatingMultiply(
        SsaValue value,
        StarkTypeSymbol expectedType,
        out SsaBinaryRValue multiply)
    {
        multiply = null!;

        if (value is not SsaValueReference reference
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Multiply,
                Type.Kind: StarkTypeKind.Float
            } candidate
            || candidate.Type != expectedType)
        {
            return false;
        }

        multiply = candidate;
        return true;
    }

    private void EmitFusedMultiplyAdd(
        string result,
        StarkTypeSymbol type,
        string multiplicand,
        string multiplier,
        string addend)
    {
        var llvmType = MapType(type);
        AppendLine(
            $"  {result} = call{GetFastMathSuffix()} {llvmType} @{GetFusedMultiplyAddIntrinsicName(type)}({llvmType} {multiplicand}, {llvmType} {multiplier}, {llvmType} {addend})");
    }

    private string EmitFastFloatNegation(StarkTypeSymbol type, SsaValue value)
    {
        var result = $"%{EscapeIdentifier(CreateAbiTempName("fmuladd_neg"))}";
        AppendLine($"  {result} = fneg{GetFastMathSuffix()} {MapType(type)} {FormatValue(value)}");
        return result;
    }

    private bool TryEmitScalarizedAggregateEquality(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var rootType = NormalizeAggregateType(binary.Left.Type);
        if (!SupportsScalarizedAggregateEquality(rootType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                rootType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: true,
                out var leaves))
        {
            return false;
        }

        if (leaves.Count == 1)
        {
            return TryEmitScalarizedAggregateLeafComparison(
                result,
                binary.Operator,
                binary.Left,
                binary.Right,
                rootType,
                leaves[0],
                out _);
        }

        string accumulator;
        if (!TryEmitScalarizedAggregateLeafComparison(
                $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                binary.Operator,
                binary.Left,
                binary.Right,
                rootType,
                leaves[0],
                out accumulator))
        {
            return false;
        }

        for (var index = 1; index < leaves.Count; index++)
        {
            if (!TryEmitScalarizedAggregateLeafComparison(
                    $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                    binary.Operator,
                    binary.Left,
                    binary.Right,
                    rootType,
                    leaves[index],
                    out var leafComparison))
            {
                return false;
            }

            var merged = index == leaves.Count - 1
                ? result
                : $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_merge"))}";
            var opcode = binary.Operator == SsaBinaryOperator.Equal ? "and" : "or";
            AppendLine($"  {merged} = {opcode} i1 {accumulator}, {leafComparison}");
            accumulator = merged;
        }

        return true;
    }

    private bool TryEmitTextEquality(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var operandType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || rightType.Kind != operandType.Kind)
        {
            return false;
        }

        return TryEmitTextEqualityHelperCall(
            result,
            binary.Operator,
            operandType,
            FormatValue(binary.Left),
            FormatValue(binary.Right));
    }

    private bool TryEmitTextOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var operandType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || rightType.Kind != operandType.Kind)
        {
            return false;
        }

        return TryEmitTextOrderedComparisonHelperCall(
            result,
            binary.Operator,
            operandType,
            FormatValue(binary.Left),
            FormatValue(binary.Right));
    }

    private bool TryEmitFixedArrayOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var leftType = binary.Left.Type;
        var rightType = binary.Right.Type;
        if (leftType.Kind != StarkTypeKind.FixedArray
            || rightType.Kind != StarkTypeKind.FixedArray
            || leftType.ElementType is null
            || rightType.ElementType is null
            || leftType.FixedLength != rightType.FixedLength)
        {
            return false;
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(leftType);
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("fixedcmp_root"))}";
        var predicate = binary.Operator switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool TryEmitScalarizedNamedAggregateOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var leftType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (leftType.Kind != StarkTypeKind.Named
            || rightType.Kind != StarkTypeKind.Named
            || leftType.NamedType != rightType.NamedType
            || !SupportsScalarizedAggregateOrderedComparison(leftType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                leftType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out _))
        {
            return false;
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(leftType);
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("namedcmp_root"))}";
        var predicate = binary.Operator switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool SupportsScalarizedAggregateEquality(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && _context.EnumLayouts.ContainsKey(namedType.Name))),
            _ => false
        };
    }

    private bool SupportsScalarizedAggregateOrderedComparison(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && _context.EnumLayouts.ContainsKey(namedType.Name))),
            _ => false
        };
    }

    private bool TryEmitScalarizedAggregateLeafComparison(
        string result,
        SsaBinaryOperator operatorKind,
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol rootType,
        AggregateScalarLeaf leaf,
        out string emittedResult)
    {
        var leftValue = EmitScalarizedAggregateLeafValue(left, rootType, leaf.Indices, leaf.Type);
        var rightValue = EmitScalarizedAggregateLeafValue(right, rootType, leaf.Indices, leaf.Type);
        emittedResult = result;
        return TryEmitLeafEqualityComparison(result, operatorKind, leaf.Type, leftValue, rightValue);
    }

    private bool TryEmitLeafEqualityComparison(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        switch (operandType.Kind)
        {
            case StarkTypeKind.Integer:
            case StarkTypeKind.Bool:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                AppendLine($"  {result} = icmp {predicate} {MapType(operandType)} {left}, {right}");
                return true;
            }
            case StarkTypeKind.Float:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "oeq",
                    SsaBinaryOperator.NotEqual => "one",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call i1 @{GetConstrainedFloatCompareIntrinsicName(operandType)}({MapType(operandType)} {left}, {MapType(operandType)} {right}, metadata !\"{predicate}\", metadata !\"fpexcept.strict\") strictfp");
                    return true;
                }

                AppendLine($"  {result} = fcmp{GetFastMathSuffix()} {predicate} {MapType(operandType)} {left}, {right}");
                return true;
            }
            case StarkTypeKind.RawPointer:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                AppendLine($"  {result} = icmp {predicate} ptr {left}, {right}");
                return true;
            }
            case StarkTypeKind.Ascii:
            case StarkTypeKind.Unicode:
                return TryEmitTextEqualityHelperCall(result, operatorKind, operandType, left, right);
            case StarkTypeKind.Slice:
                return TryEmitSliceEquality(result, operatorKind, operandType, left, right);
            default:
                return false;
        }
    }

    private bool TryEmitTextEqualityHelperCall(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var helperName = operandType.Kind == StarkTypeKind.Ascii
            ? AsciiEqualityHelperName
            : UnicodeEqualityHelperName;
        var equalityResult = operatorKind == SsaBinaryOperator.Equal
            ? result
            : $"%{EscapeIdentifier(CreateAbiTempName("textcmp_eq"))}";

        AppendLine(
            $"  {equalityResult} = call i1 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");

        if (operatorKind == SsaBinaryOperator.NotEqual)
        {
            AppendLine($"  {result} = xor i1 {equalityResult}, true");
        }

        return true;
    }

    private bool TryEmitTextOrderedComparisonHelperCall(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || operatorKind is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var helperName = operandType.Kind == StarkTypeKind.Ascii
            ? AsciiCompareHelperName
            : UnicodeCompareHelperName;
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("textcmp_order"))}";
        var predicate = operatorKind switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool TryEmitSliceEquality(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind != StarkTypeKind.Slice
            || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var sliceType = MapType(operandType);
        var predicate = operatorKind == SsaBinaryOperator.Equal ? "eq" : "ne";
        var mergeOpcode = operatorKind == SsaBinaryOperator.Equal ? "and" : "or";
        var leftPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_ptr"))}";
        var rightPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_ptr"))}";
        var leftLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_len"))}";
        var rightLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_len"))}";
        var pointerComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_ptr"))}";
        var lengthComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_len"))}";

        AppendLine($"  {leftPointer} = extractvalue {sliceType} {left}, 0");
        AppendLine($"  {rightPointer} = extractvalue {sliceType} {right}, 0");
        AppendLine($"  {leftLength} = extractvalue {sliceType} {left}, 1");
        AppendLine($"  {rightLength} = extractvalue {sliceType} {right}, 1");
        AppendLine($"  {pointerComparison} = icmp {predicate} ptr {leftPointer}, {rightPointer}");
        AppendLine($"  {lengthComparison} = icmp {predicate} i64 {leftLength}, {rightLength}");
        AppendLine($"  {result} = {mergeOpcode} i1 {pointerComparison}, {lengthComparison}");
        return true;
    }

    private void EmitSaturatingIntegerBinary(string result, SsaBinaryRValue binary)
    {
        if (binary.Type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            throw new UnsupportedBodyEmissionException($"Saturating integer operator '{binary.Operator}' requires a concrete integer bit width.");
        }

        var narrowType = MapType(binary.Type);
        var wideTypeSymbol = StarkTypeSymbols.Integer(bitWidth * 2);
        var wideType = MapType(wideTypeSymbol);
        var wideOpcode = binary.Operator switch
        {
            SsaBinaryOperator.SaturatingAdd => "add",
            SsaBinaryOperator.SaturatingSubtract => "sub",
            SsaBinaryOperator.SaturatingMultiply => "mul",
            _ => throw new UnsupportedBodyEmissionException($"Unsupported saturating integer operator '{binary.Operator}'.")
        };

        var leftWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_left"))}";
        var rightWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_right"))}";
        var valueWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_value"))}";
        var aboveMax = $"%{EscapeIdentifier(CreateAbiTempName("sat_above"))}";
        var belowMin = $"%{EscapeIdentifier(CreateAbiTempName("sat_below"))}";
        var clampHigh = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamp_high"))}";
        var clamped = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamped"))}";

        GetSignedIntegerBounds(bitWidth, out var minValue, out var maxValue);

        AppendLine($"  {leftWide} = sext {narrowType} {FormatValue(binary.Left)} to {wideType}");
        AppendLine($"  {rightWide} = sext {narrowType} {FormatValue(binary.Right)} to {wideType}");
        AppendLine($"  {valueWide} = {wideOpcode} {wideType} {leftWide}, {rightWide}");
        AppendLine($"  {aboveMax} = icmp sgt {wideType} {valueWide}, {maxValue}");
        AppendLine($"  {belowMin} = icmp slt {wideType} {valueWide}, {minValue}");
        AppendLine($"  {clampHigh} = select i1 {aboveMax}, {wideType} {maxValue}, {wideType} {valueWide}");
        AppendLine($"  {clamped} = select i1 {belowMin}, {wideType} {minValue}, {wideType} {clampHigh}");
        AppendLine($"  {result} = trunc {wideType} {clamped} to {narrowType}");
    }

    private string GetIntegerInstructionFlags(SsaBinaryRValue binary)
    {
        return binary.Operator switch
        {
            SsaBinaryOperator.Add or SsaBinaryOperator.Subtract or SsaBinaryOperator.Multiply
                => GetOrdinaryIntegerArithmeticNoWrapFlags(binary),
            SsaBinaryOperator.ShiftLeft => GetShiftLeftNoWrapFlags(binary),
            SsaBinaryOperator.Divide => CanUseExactSignedDivision(binary) ? " exact" : string.Empty,
            SsaBinaryOperator.ShiftRight => CanUseExactArithmeticShiftRight(binary) ? " exact" : string.Empty,
            _ => string.Empty
        };
    }

    private string GetOrdinaryIntegerArithmeticNoWrapFlags(SsaBinaryRValue binary)
    {
        var canUseUnsignedNoWrap = CanUseUnsignedNoWrap(binary);
        var canUseSignedNoWrap = CanUseSignedNoWrap(binary);
        return (canUseUnsignedNoWrap, canUseSignedNoWrap) switch
        {
            (true, true) => " nuw nsw",
            (true, false) => " nuw",
            (false, true) => " nsw",
            _ => string.Empty
        };
    }

    private bool CanUseUnsignedIntegerDivisionSemantics(SsaBinaryRValue binary)
    {
        if (HasUnsignedIntegerSemantics(binary.Type))
        {
            return true;
        }

        return binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo
            && CanProveNonNegativeInteger(binary.Left)
            && CanProveNonNegativeInteger(binary.Right);
    }

    private bool CanProveNonNegativeInteger(SsaValue value)
    {
        return TryGetIntegerValueRange(value, new HashSet<string>(StringComparer.Ordinal), out var min, out _)
                && min >= BigInteger.Zero
            || TryGetKnownZeroSignBit(value);
    }

    private bool TryGetKnownZeroSignBit(SsaValue value)
    {
        var normalizedType = NormalizeAggregateType(value.Type);
        if (normalizedType.Kind != StarkTypeKind.Integer
            || normalizedType.BitWidth is not int bitWidth
            || bitWidth <= 0
            || value is not SsaValueReference reference
            || !_valueFacts.TryGetValue(reference.Name, out var facts)
            || facts.KnownBitsKind != SsaFactLatticeKind.Known
            || facts.KnownBits is not { } knownBits)
        {
            return false;
        }

        var signBit = BigInteger.One << (bitWidth - 1);
        return (knownBits.KnownZeroBits & signBit) != BigInteger.Zero;
    }

    private bool CanUseUnsignedNoWrap(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax)
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var rightMin, out var rightMax))
        {
            return false;
        }

        var domainSize = BigInteger.One << bitWidth;
        if (leftMin < BigInteger.Zero
            || rightMin < BigInteger.Zero
            || leftMax >= domainSize
            || rightMax >= domainSize)
        {
            return false;
        }

        return binary.Operator switch
        {
            SsaBinaryOperator.Add => leftMax + rightMax < domainSize,
            SsaBinaryOperator.Subtract => leftMin >= rightMax,
            SsaBinaryOperator.Multiply => leftMax * rightMax < domainSize,
            _ => false
        };
    }

    private bool CanUseSignedNoWrap(SsaBinaryRValue binary)
    {
        var type = NormalizeAggregateType(binary.Left.Type);
        if (type.Kind != StarkTypeKind.Integer || type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            return false;
        }

        if (!type.IsUnsigned)
        {
            return true;
        }

        if (!TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax)
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var rightMin, out var rightMax))
        {
            return false;
        }

        GetSignedIntegerBounds(bitWidth, out var signedMin, out var signedMax);
        if (leftMin < signedMin || leftMax > signedMax || rightMin < signedMin || rightMax > signedMax)
        {
            return false;
        }

        var resultRange = binary.Operator switch
        {
            SsaBinaryOperator.Add => new SsaIntegerRangeFact(leftMin + rightMin, leftMax + rightMax),
            SsaBinaryOperator.Subtract => new SsaIntegerRangeFact(leftMin - rightMax, leftMax - rightMin),
            SsaBinaryOperator.Multiply => MultiplyRanges(leftMin, leftMax, rightMin, rightMax),
            _ => null
        };

        return resultRange is { } range
            && range.Min >= signedMin
            && range.Max <= signedMax;
    }

    private static SsaIntegerRangeFact MultiplyRanges(
        BigInteger leftMin,
        BigInteger leftMax,
        BigInteger rightMin,
        BigInteger rightMax)
    {
        var candidates = new[]
        {
            leftMin * rightMin,
            leftMin * rightMax,
            leftMax * rightMin,
            leftMax * rightMax
        };
        return new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
    }

    private string GetShiftLeftNoWrapFlags(SsaBinaryRValue binary)
    {
        var canUseUnsignedNoWrap = CanUseUnsignedNoWrapShiftLeft(binary);
        var canUseSignedNoWrap = CanUseSignedNoWrapShiftLeft(binary);
        return (canUseUnsignedNoWrap, canUseSignedNoWrap) switch
        {
            (true, true) => " nuw nsw",
            (true, false) => " nuw",
            (false, true) => " nsw",
            _ => string.Empty
        };
    }

    private bool CanUseUnsignedNoWrapShiftLeft(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetShiftAmountRange(binary, out _, out var maxShift)
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax))
        {
            return false;
        }

        if (maxShift == 0)
        {
            return true;
        }

        if (leftMin < BigInteger.Zero)
        {
            return false;
        }

        var domainSize = BigInteger.One << bitWidth;
        return leftMax * (BigInteger.One << maxShift) < domainSize;
    }

    private bool CanUseSignedNoWrapShiftLeft(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetShiftAmountRange(binary, out var minShift, out var maxShift)
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax))
        {
            return false;
        }

        if (maxShift == 0)
        {
            return true;
        }

        GetSignedIntegerBounds(bitWidth, out var signedMin, out var signedMax);
        var minFactor = BigInteger.One << minShift;
        var maxFactor = BigInteger.One << maxShift;
        var minResult = Min(Min(leftMin * minFactor, leftMin * maxFactor), Min(leftMax * minFactor, leftMax * maxFactor));
        var maxResult = Max(Max(leftMin * minFactor, leftMin * maxFactor), Max(leftMax * minFactor, leftMax * maxFactor));
        return minResult >= signedMin && maxResult <= signedMax;
    }

    private bool CanUseExactSignedDivision(SsaBinaryRValue binary)
    {
        return TryGetIntegerSingletonValue(binary.Right, out var divisor)
            && !divisor.IsZero
            && CanProveMultipleOf(binary.Left, BigInteger.Abs(divisor), new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanUseExactArithmeticShiftRight(SsaBinaryRValue binary)
    {
        return TryGetShiftAmountRange(binary, out _, out var maxShift)
            && CanProveMultipleOfPowerOfTwo(binary.Left, maxShift, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryGetShiftAmountRange(SsaBinaryRValue binary, out int minShift, out int maxShift)
    {
        minShift = default;
        maxShift = default;
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var minValue, out var maxValue)
            || minValue < BigInteger.Zero
            || maxValue < minValue
            || maxValue >= bitWidth
            || maxValue > int.MaxValue)
        {
            return false;
        }

        minShift = (int)minValue;
        maxShift = (int)maxValue;
        return true;
    }

    private bool CanProveMultipleOfPowerOfTwo(
        SsaValue value,
        int exponent,
        HashSet<string> visitedReferences)
    {
        return exponent <= 0
            || CanProveMultipleOf(value, BigInteger.One << exponent, visitedReferences);
    }

    private bool CanProveMultipleOf(
        SsaValue value,
        BigInteger factor,
        HashSet<string> visitedReferences)
    {
        factor = BigInteger.Abs(factor);
        if (factor <= BigInteger.One)
        {
            return true;
        }

        if (TryGetIntegerSingletonValue(value, out var singleton))
        {
            return IsDivisibleBy(singleton, factor);
        }

        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => CanProveMultipleOf(use.Value, factor, visitedReferences),
            SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert)
                => CanProveMultipleOf(convert.Operand, factor, visitedReferences),
            SsaBinaryRValue binary => CanProveBinaryMultipleOf(binary, factor, visitedReferences),
            _ => false
        };
    }

    private bool CanProveBinaryMultipleOf(
        SsaBinaryRValue binary,
        BigInteger factor,
        HashSet<string> visitedReferences)
    {
        if (binary.Operator == SsaBinaryOperator.ShiftLeft
            && TryGetPowerOfTwoExponent(factor, out var exponent)
            && TryGetShiftAmountRange(binary, out var minShift, out _))
        {
            return minShift >= exponent;
        }

        if (binary.Operator != SsaBinaryOperator.Multiply)
        {
            return false;
        }

        return TryGetIntegerSingletonValue(binary.Left, out var leftConstant) && IsDivisibleBy(leftConstant, factor)
            || TryGetIntegerSingletonValue(binary.Right, out var rightConstant) && IsDivisibleBy(rightConstant, factor)
            || CanProveMultipleOf(binary.Left, factor, CloneVisitedReferences(visitedReferences))
            || CanProveMultipleOf(binary.Right, factor, CloneVisitedReferences(visitedReferences));
    }

    private bool TryGetIntegerSingletonValue(SsaValue value, out BigInteger singleton)
    {
        if (TryGetIntegerValueRange(value, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min == max)
        {
            singleton = min;
            return true;
        }

        singleton = default;
        return false;
    }

    private static bool IsDivisibleBy(BigInteger value, BigInteger factor)
    {
        factor = BigInteger.Abs(factor);
        return factor > BigInteger.Zero && value % factor == BigInteger.Zero;
    }

    private static bool TryGetPowerOfTwoExponent(BigInteger value, out int exponent)
    {
        value = BigInteger.Abs(value);
        exponent = 0;
        if (value <= BigInteger.Zero)
        {
            return false;
        }

        while (!value.IsOne)
        {
            if (!value.IsEven)
            {
                exponent = default;
                return false;
            }

            value >>= 1;
            exponent++;
        }

        return true;
    }

    private static HashSet<string> CloneVisitedReferences(HashSet<string> visitedReferences)
    {
        return new HashSet<string>(visitedReferences, StringComparer.Ordinal);
    }

    private bool TryGetIntegerValueRange(
        SsaValue value,
        HashSet<string> visitedReferences,
        out BigInteger min,
        out BigInteger max)
    {
        if (value is SsaIntegerConstant integer)
        {
            min = integer.Value;
            max = integer.Value;
            return true;
        }

        if (value is SsaValueReference reference
            && TryGetIntegerValueFact(reference.Name, out min, out max))
        {
            return true;
        }

        if (value is SsaValueReference referenceValue
            && visitedReferences.Add(referenceValue.Name)
            && _valueDefinitions.TryGetValue(referenceValue.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return TryGetIntegerValueRange(use.Value, visitedReferences, out min, out max);
                case SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert):
                    return TryGetIntegerValueRange(convert.Operand, visitedReferences, out min, out max);
            }
        }

        return TryGetIntegerTypeRange(value.Type, out min, out max);
    }

    private bool TryGetIntegerValueFact(string valueName, out BigInteger min, out BigInteger max)
    {
        if (_valueFacts.TryGetValue(valueName, out var facts)
            && facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } range)
        {
            min = range.Min;
            max = range.Max;
            return true;
        }

        min = default;
        max = default;
        return false;
    }

    private static bool CanPreserveIntegerRangeThroughConversion(SsaConvertRValue convert)
    {
        var sourceType = NormalizeAggregateType(convert.Operand.Type);
        var targetType = NormalizeAggregateType(convert.TargetType);
        if (sourceType.Kind != StarkTypeKind.Integer
            || targetType.Kind != StarkTypeKind.Integer
            || sourceType.BitWidth is not int sourceBitWidth
            || targetType.BitWidth is not int targetBitWidth)
        {
            return false;
        }

        if (sourceBitWidth <= targetBitWidth)
        {
            return true;
        }

        if (!TryGetIntegerTypeRange(sourceType, out var sourceMin, out var sourceMax))
        {
            return false;
        }

        GetSignedIntegerBounds(targetBitWidth, out var targetMin, out var targetMax);
        return sourceMin >= targetMin && sourceMax <= targetMax;
    }

    private static bool TryGetIntegerTypeRange(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        var normalizedType = NormalizeAggregateType(type);
        if (normalizedType.Kind != StarkTypeKind.Integer || normalizedType.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            min = default;
            max = default;
            return false;
        }

        if (normalizedType.RangeMin is not null && normalizedType.RangeMax is not null)
        {
            min = normalizedType.RangeMin.Value;
            max = normalizedType.RangeMax.Value;
            return true;
        }

        if (normalizedType.IsUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << bitWidth) - BigInteger.One;
            return true;
        }

        GetSignedIntegerBounds(bitWidth, out min, out max);
        return true;
    }

    private static bool HasUnsignedIntegerSemantics(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        if (normalizedType.Kind != StarkTypeKind.Integer
            || normalizedType.BitWidth is not int bitWidth
            || !TryGetIntegerTypeRange(normalizedType, out var min, out var max))
        {
            return false;
        }

        if (normalizedType.IsUnsigned)
        {
            return true;
        }

        GetSignedIntegerBounds(bitWidth, out _, out var signedMax);
        return min >= BigInteger.Zero && max > signedMax;
    }

    private static bool ShouldUseUnsignedIntegerComparison(StarkTypeSymbol type)
        => type.Kind == StarkTypeKind.Bool || HasUnsignedIntegerSemantics(type);

    private bool ShouldUseUnsignedIntegerComparison(SsaBinaryRValue binary)
    {
        var isOrderedComparison = binary.Operator is SsaBinaryOperator.LessThan
            or SsaBinaryOperator.LessThanOrEqual
            or SsaBinaryOperator.GreaterThan
            or SsaBinaryOperator.GreaterThanOrEqual;
        return ShouldUseUnsignedIntegerComparison(binary.Left.Type)
            || isOrderedComparison
                && CanProveNonNegativeInteger(binary.Left)
                && CanProveNonNegativeInteger(binary.Right);
    }

    private string GetFixedArrayIndexGepFlags(SsaValue? index, StarkTypeSymbol aggregateType)
    {
        if (index is null || aggregateType.FixedLength is not int fixedLength)
        {
            return string.Empty;
        }

        if (IsKnownZeroIndex(index))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < fixedLength
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private string GetUnboundedPointerIndexGepFlags(SsaValue index)
    {
        return IsKnownZeroIndex(index) ? GetZeroOffsetGepFlags() : string.Empty;
    }

    private string GetSliceElementGepFlags(SsaValue slice, SsaValue index)
    {
        if (IsKnownZeroIndex(index))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetKnownSliceElementCountLowerBound(slice, new HashSet<string>(StringComparer.Ordinal), out var elementCountLowerBound)
            && IsIndexRangeWithinExclusiveBound(index, elementCountLowerBound)
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private string GetTextSliceGepFlags(SsaValue textValue, SsaValue start)
    {
        if (IsKnownZeroIndex(start))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetKnownTextUnitCountLowerBound(textValue, new HashSet<string>(StringComparer.Ordinal), out var unitCountLowerBound)
            && IsIndexRangeWithinInclusiveBound(start, unitCountLowerBound)
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private bool IsKnownZeroIndex(SsaValue index)
    {
        return TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min.IsZero
            && max.IsZero;
    }

    private bool IsIndexRangeWithinExclusiveBound(SsaValue index, BigInteger exclusiveBound)
    {
        return exclusiveBound > BigInteger.Zero
            && TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < exclusiveBound;
    }

    private bool IsIndexRangeWithinInclusiveBound(SsaValue index, BigInteger inclusiveBound)
    {
        return inclusiveBound >= BigInteger.Zero
            && TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max <= inclusiveBound;
    }

    private bool TryGetKnownSliceElementCountLowerBound(
        SsaValue slice,
        HashSet<string> visitedReferences,
        out BigInteger elementCountLowerBound)
    {
        if (slice is SsaValueReference reference
            && visitedReferences.Add(reference.Name))
        {
            if (_valueFacts.TryGetValue(reference.Name, out var facts)
                && facts.LengthKind == SsaFactLatticeKind.Known
                && facts.LengthRange is { } lengthRange
                && lengthRange.Min >= BigInteger.Zero)
            {
                elementCountLowerBound = lengthRange.Min;
                return true;
            }

            if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                switch (definition)
                {
                    case SsaUseRValue use:
                        return TryGetKnownSliceElementCountLowerBound(use.Value, visitedReferences, out elementCountLowerBound);
                    case SsaMakeSliceFromLocalRValue makeSlice when makeSlice.SourceType.FixedLength is int fixedLength:
                        elementCountLowerBound = fixedLength;
                        return true;
                    case SsaMakeSliceFromPointerRValue makeSlice:
                        return TryResolveIntegerConstant(
                            makeSlice.Length,
                            _valueDefinitions,
                            new HashSet<string>(StringComparer.Ordinal),
                            out elementCountLowerBound);
                }
            }
        }

        elementCountLowerBound = default;
        return false;
    }

    private bool TryGetKnownTextUnitCountLowerBound(
        SsaValue textValue,
        HashSet<string> visitedReferences,
        out BigInteger unitCountLowerBound)
    {
        switch (textValue)
        {
            case SsaStringConstant text:
                unitCountLowerBound = ResolveStringConstant(text.LiteralText, text.Type).DataLength;
                return true;
            case SsaValueReference reference
                when visitedReferences.Add(reference.Name):
                if (_valueFacts.TryGetValue(reference.Name, out var facts)
                    && facts.LengthKind == SsaFactLatticeKind.Known
                    && facts.LengthRange is { } lengthRange
                    && lengthRange.Min >= BigInteger.Zero)
                {
                    unitCountLowerBound = lengthRange.Min;
                    return true;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    switch (definition)
                    {
                        case SsaUseRValue use:
                            return TryGetKnownTextUnitCountLowerBound(use.Value, visitedReferences, out unitCountLowerBound);
                        case SsaTextSliceRValue textSlice
                            when TryGetIntegerValueRange(textSlice.Length, new HashSet<string>(StringComparer.Ordinal), out var min, out _)
                                 && min >= BigInteger.Zero:
                            unitCountLowerBound = min;
                            return true;
                    }
                }

                break;
        }

        unitCountLowerBound = default;
        return false;
    }

    private static string GetZeroOffsetGepFlags() => " inbounds nuw";

    // LLVM's `inbounds` GEP contract includes the signed no-wrap (`nusw`) facts; `nuw`
    // is added only where Stark range/object facts prove unsigned address arithmetic too.
    private static string GetProvenInObjectGepFlags() => " inbounds nuw";

    private static BigInteger Min(BigInteger left, BigInteger right) => left <= right ? left : right;

    private static BigInteger Max(BigInteger left, BigInteger right) => left >= right ? left : right;

    private void EmitFloatExponent(string result, SsaBinaryRValue binary)
    {
        var llvmType = MapType(binary.Left.Type);
        if (_isStrictFp)
        {
            AppendLine(
                $"  {result} = call {llvmType} @{GetConstrainedBinaryIntrinsicName("pow", binary.Left.Type)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
            return;
        }

        var intrinsicName = $"@llvm.pow.{GetFloatIntrinsicSuffix(binary.Left.Type)}";
        AppendLine($"  {result} = call{GetFastMathSuffix()} {llvmType} {intrinsicName}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private void EmitIntegerExponent(string result, SsaBinaryRValue binary)
    {
        var bitWidth = binary.Type.BitWidth ?? throw new UnsupportedBodyEmissionException(
            $"Integer exponent operator '{binary.Type.DisplayName}' is missing a bit width.");
        var llvmType = MapType(binary.Type);
        var helperName = GetIntegerExponentHelperName(bitWidth);
        AppendLine(
            $"  {result} = call {llvmType} @{EscapeIdentifier(helperName)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private static void GetSignedIntegerBounds(int bitWidth, out BigInteger minValue, out BigInteger maxValue)
    {
        minValue = -(BigInteger.One << (bitWidth - 1));
        maxValue = (BigInteger.One << (bitWidth - 1)) - 1;
    }

    private void EmitCall(string resultName, string result, SsaCallRValue call)
    {
        var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
        if (abiCallee is null)
        {
            throw new UnsupportedBodyEmissionException($"Missing ABI lowering for call target '{call.FunctionName}'.");
        }

        var sourceReturnType = call.SourceReturnType ?? call.Type;
        if (IsStringType(sourceReturnType) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
        {
            throw new UnsupportedBodyEmissionException(
                $"FFI string returns are not yet supported for '{call.FunctionName}'.");
        }

        if (TryEmitAsciiToUnicodeLiteralCallSiteSpecialization(resultName, result, call, abiCallee))
        {
            return;
        }

        if (TryEmitDictionaryKeyCallSiteSpecialization(result, call, abiCallee))
        {
            return;
        }

        var arguments = new List<string>();
        string? indirectReturnSlot = null;

        if (abiCallee.ReturnsIndirect)
        {
            indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("callret_slot"))}";
            QueueStaticAlloca(indirectReturnSlot, sourceReturnType);
            arguments.Add(RenderSRetArgumentPointer(sourceReturnType, indirectReturnSlot));
        }

        var userParameters = abiCallee.UserParameters;
        if (abiCallee.IsVarargs)
        {
            if (call.Arguments.Count < userParameters.Count)
            {
                throw new UnsupportedBodyEmissionException(
                    $"ABI parameter count mismatch for '{call.FunctionName}': expected at least {userParameters.Count}, got {call.Arguments.Count}.");
            }
        }
        else if (userParameters.Count != call.Arguments.Count)
        {
            throw new UnsupportedBodyEmissionException(
                $"ABI parameter count mismatch for '{call.FunctionName}': expected {userParameters.Count}, got {call.Arguments.Count}.");
        }

        for (var index = 0; index < userParameters.Count; index++)
        {
            var parameter = userParameters[index];
            var argument = call.Arguments[index];

            if (parameter.Kind == AbiParameterKind.Direct)
            {
                arguments.Add(RenderDirectArgument(parameter, argument));
                continue;
            }

            var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && index < call.IndirectArgumentAddresses.Count
                ? call.IndirectArgumentAddresses[index]
                : null;
            if (indirectArgumentAddress is not null)
            {
                arguments.Add(RenderIndirectArgumentPointer(parameter, FormatValue(indirectArgumentAddress)));
                continue;
            }

            var promotedLocal = call.IndirectArgumentLocalNames is not null && index < call.IndirectArgumentLocalNames.Count
                ? call.IndirectArgumentLocalNames[index]
                : null;
            if (!string.IsNullOrWhiteSpace(promotedLocal))
            {
                var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
                if (promotedParameter is not null)
                {
                    if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                    {
                        arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier(promotedParameter.LlvmName)}"));
                    }
                    else
                    {
                        EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                        arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}"));
                    }

                    continue;
                }

                EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_{promotedLocal}")}"));
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && TryResolveAggregateSourceAddress(argument, parameter.SourceType, out var forwardedSourceAddress))
            {
                arguments.Add(RenderIndirectArgumentPointer(parameter, forwardedSourceAddress));
                continue;
            }

            var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
            QueueStaticAlloca(tempSlot, parameter.SourceType);
            EmitValueToAddress(tempSlot, parameter.SourceType, argument, GetStackObjectAlignmentBytes(parameter.SourceType));

            arguments.Add(RenderIndirectArgumentPointer(parameter, tempSlot));
        }

        if (abiCallee.IsVarargs)
        {
            for (var index = userParameters.Count; index < call.Arguments.Count; index++)
            {
                arguments.Add(RenderDirectVarargArgument(call.Arguments[index]));
            }
        }

        var renderedArguments = string.Join(", ", arguments);
        var callTarget = RenderCallTarget(abiCallee);
        var callPrefixSegments = new List<string>();
        if (ShouldEmitTailCallMarker(resultName))
        {
            callPrefixSegments.Add("tail");
        }

        callPrefixSegments.Add("call");
        if (ShouldUseFastMathFlags(call.Type))
        {
            callPrefixSegments.Add("fast");
        }

        if (abiCallee.UsesFastCallingConvention)
        {
            callPrefixSegments.Add("fastcc");
        }

        var callPrefix = string.Join(" ", callPrefixSegments);
        var strictFpCallSuffix = GetStrictFpCallSuffix();

        if (abiCallee.ReturnsIndirect)
        {
            AppendLine($"  {callPrefix} void {callTarget}({renderedArguments}){strictFpCallSuffix}");
            _indirectAggregateValueSlots[resultName] = indirectReturnSlot!;
            if (RequiresAggregateValueMaterialization(resultName, sourceReturnType))
            {
                AppendLine($"  {result} = load {MapType(sourceReturnType)}, ptr {indirectReturnSlot}{GetStackObjectAlignmentSuffix(sourceReturnType)}{GetValueRangeMetadataSuffix(sourceReturnType)}{GetScopedNoAliasMetadataSuffix(CreateScopedAliasFreshResultRootKey(resultName))}");
            }
            return;
        }

        if (call.Type.Kind == StarkTypeKind.Void)
        {
            AppendLine($"  {callPrefix} void {callTarget}({renderedArguments}){strictFpCallSuffix}");
            return;
        }

        var callRangeMetadataSuffix = abiCallee.IsFfi ? string.Empty : GetValueRangeMetadataSuffix(resultName, call.Type);
        AppendLine($"  {result} = {callPrefix} {MapType(abiCallee.LlvmReturnType)} {callTarget}({renderedArguments}){strictFpCallSuffix}{callRangeMetadataSuffix}");
    }

    private enum DictionaryKeyCallSiteOperation
    {
        Hash,
        Equals
    }

    private bool TryEmitDictionaryKeyCallSiteSpecialization(
        string result,
        SsaCallRValue call,
        AbiFunctionSignature abiCallee)
    {
        if (abiCallee.IsFfi
            || TryResolveDictionaryKeyCallSiteOperation(call, abiCallee) is not { } operation)
        {
            return false;
        }

        var expectedParameterCount = operation == DictionaryKeyCallSiteOperation.Hash ? 1 : 2;
        if (!TryResolveDictionaryKeyCallSiteType(call, abiCallee, expectedParameterCount, out var keyType))
        {
            return false;
        }

        if (operation == DictionaryKeyCallSiteOperation.Hash)
        {
            if (call.Type.Kind != StarkTypeKind.Integer || call.Type.BitWidth != 64)
            {
                return false;
            }

            if (!TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[0], 0, keyType, out var value))
            {
                return false;
            }

            EmitDictionaryKeyHashValue(result, keyType, value);
            return true;
        }

        if (call.Type.Kind != StarkTypeKind.Bool
            || !TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[0], 0, keyType, out var left)
            || !TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[1], 1, keyType, out var right))
        {
            return false;
        }

        AppendLine($"  {result} = icmp eq {MapType(keyType)} {left}, {right}");
        return true;
    }

    private static DictionaryKeyCallSiteOperation? TryResolveDictionaryKeyCallSiteOperation(
        SsaCallRValue call,
        AbiFunctionSignature abiCallee)
    {
        foreach (var candidate in new[]
                 {
                     abiCallee.SourceName,
                     abiCallee.DisplaySourceName,
                     abiCallee.Name,
                     abiCallee.SymbolName,
                     call.FunctionName
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate is "System.Collections.DictionaryKey.Hash"
                or "DictionaryKey.Hash"
                or "System_Collections_DictionaryKey_Hash")
            {
                return DictionaryKeyCallSiteOperation.Hash;
            }

            if (candidate is "System.Collections.DictionaryKey.Equals"
                or "DictionaryKey.Equals"
                or "System_Collections_DictionaryKey_Equals")
            {
                return DictionaryKeyCallSiteOperation.Equals;
            }

            if (candidate.StartsWith("__stark_mono_fn_System_Collections__", StringComparison.Ordinal))
            {
                if (candidate.Contains("System_Collections_DictionaryKey_Hash__", StringComparison.Ordinal))
                {
                    return DictionaryKeyCallSiteOperation.Hash;
                }

                if (candidate.Contains("System_Collections_DictionaryKey_Equals__", StringComparison.Ordinal))
                {
                    return DictionaryKeyCallSiteOperation.Equals;
                }
            }
        }

        return null;
    }

    private static bool TryResolveDictionaryKeyCallSiteType(
        SsaCallRValue call,
        AbiFunctionSignature abiCallee,
        int expectedParameterCount,
        out StarkTypeSymbol keyType)
    {
        keyType = StarkTypeSymbols.Error;
        var userParameters = abiCallee.UserParameters;
        if (call.Arguments.Count != expectedParameterCount
            || userParameters.Count != expectedParameterCount)
        {
            return false;
        }

        var firstParameterType = NormalizeDictionaryKeyType(userParameters[0].SourceType);
        if (userParameters[0].SourceType.BorrowKind != StarkBorrowKind.None
            && IsSupportedDictionaryKeyScalarType(firstParameterType))
        {
            keyType = firstParameterType;
        }
        else if (!TryResolveDictionaryKeyArgumentType(call, 0, out keyType))
        {
            return false;
        }

        for (var index = 1; index < userParameters.Count; index++)
        {
            var parameterType = NormalizeDictionaryKeyType(userParameters[index].SourceType);
            if (userParameters[index].SourceType.BorrowKind != StarkBorrowKind.None
                && IsSupportedDictionaryKeyScalarType(parameterType))
            {
                if (parameterType != keyType)
                {
                    return false;
                }

                continue;
            }

            if (!TryResolveDictionaryKeyArgumentType(call, index, out var argumentType)
                || argumentType != keyType)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveDictionaryKeyArgumentType(
        SsaCallRValue call,
        int argumentIndex,
        out StarkTypeSymbol keyType)
    {
        keyType = NormalizeDictionaryKeyType(call.Arguments[argumentIndex].Type);
        if (IsSupportedDictionaryKeyScalarType(keyType))
        {
            return true;
        }

        var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && argumentIndex < call.IndirectArgumentAddresses.Count
            ? call.IndirectArgumentAddresses[argumentIndex]
            : null;
        if (indirectArgumentAddress?.Type is { Kind: StarkTypeKind.RawPointer, ElementType: not null } pointerType)
        {
            keyType = NormalizeDictionaryKeyType(pointerType.ElementType);
            return IsSupportedDictionaryKeyScalarType(keyType);
        }

        keyType = StarkTypeSymbols.Error;
        return false;
    }

    private bool TryMaterializeDictionaryKeyScalarArgument(
        SsaCallRValue call,
        AbiParameterSymbol parameter,
        int argumentIndex,
        StarkTypeSymbol keyType,
        out string value)
    {
        value = string.Empty;
        var argument = call.Arguments[argumentIndex];
        if (parameter.Kind == AbiParameterKind.Direct)
        {
            value = FormatValue(argument);
            return true;
        }

        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            return false;
        }

        var llvmType = MapType(keyType);
        var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && argumentIndex < call.IndirectArgumentAddresses.Count
            ? call.IndirectArgumentAddresses[argumentIndex]
            : null;
        if (indirectArgumentAddress is not null)
        {
            value = LoadDictionaryKeyScalarValue(
                llvmType,
                keyType,
                FormatValue(indirectArgumentAddress),
                GetKnownPointerAlignmentSuffix(indirectArgumentAddress, keyType),
                $"dict_key_arg_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        var promotedLocal = call.IndirectArgumentLocalNames is not null && argumentIndex < call.IndirectArgumentLocalNames.Count
            ? call.IndirectArgumentLocalNames[argumentIndex]
            : null;
        if (!string.IsNullOrWhiteSpace(promotedLocal))
        {
            var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
            if (promotedParameter is not null)
            {
                if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                {
                    value = LoadDictionaryKeyScalarValue(
                        llvmType,
                        keyType,
                        $"%{EscapeIdentifier(promotedParameter.LlvmName)}",
                        GetTypeAlignmentSuffix(keyType),
                        $"dict_key_param_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
                    return true;
                }

                EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                value = LoadDictionaryKeyScalarValue(
                    llvmType,
                    keyType,
                    $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}",
                    GetStackObjectAlignmentSuffix(keyType),
                    $"dict_key_param_slot_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
                return true;
            }

            EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
            value = LoadDictionaryKeyScalarValue(
                llvmType,
                keyType,
                $"%{EscapeIdentifier($"slot_{promotedLocal}")}",
                GetStackObjectAlignmentSuffix(keyType),
                $"dict_key_local_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        if (argument.Type.BorrowKind == StarkBorrowKind.None
            && NormalizeDictionaryKeyType(argument.Type) == keyType)
        {
            value = FormatValue(argument);
            return true;
        }

        return false;
    }

    private string LoadDictionaryKeyScalarValue(
        string llvmType,
        StarkTypeSymbol keyType,
        string address,
        string alignmentSuffix,
        string tempPrefix)
    {
        var loaded = $"%{EscapeIdentifier(CreateAbiTempName(tempPrefix))}";
        AppendLine($"  {loaded} = load {llvmType}, ptr {address}{alignmentSuffix}{GetValueRangeMetadataSuffix(keyType)}");
        return loaded;
    }

    private void EmitDictionaryKeyHashValue(string result, StarkTypeSymbol keyType, string value)
    {
        var llvmType = MapType(keyType);
        if (keyType.Kind == StarkTypeKind.Bool)
        {
            AppendLine($"  {result} = zext i1 {value} to i64");
            return;
        }

        var bitWidth = keyType.BitWidth ?? 64;
        if (bitWidth == 64)
        {
            AppendLine($"  {result} = add i64 {value}, 0");
            return;
        }

        var opcode = bitWidth < 64 ? "zext" : "trunc";
        AppendLine($"  {result} = {opcode} {llvmType} {value} to i64");
    }

    private static StarkTypeSymbol NormalizeDictionaryKeyType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private static bool IsSupportedDictionaryKeyScalarType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool or StarkTypeKind.Integer;
    }

    private bool TryEmitAsciiToUnicodeLiteralCallSiteSpecialization(
        string resultName,
        string result,
        SsaCallRValue call,
        AbiFunctionSignature abiCallee)
    {
        if (!IsTryConvertAsciiToUnicodeAbiTarget(abiCallee)
            || call.Type.Kind != StarkTypeKind.Bool
            || call.Arguments.Count != 2
            || call.Arguments[0].Type is not { Kind: StarkTypeKind.RawPointer, ElementType: not null } destinationPointerType
            || !TryGetKnownAsciiLiteralPayload(call.Arguments[1], out var sourceBytes, out var sourceLiteralText)
            || !CanSplitCurrentBlockForCallSiteControlFlow())
        {
            return false;
        }

        var destinationStructType = destinationPointerType.ElementType;
        var destination = FormatValue(call.Arguments[0]);
        var destinationLlvmType = MapType(destinationStructType);
        var pointerAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: true));
        var lengthAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.Integer(64));
        var unitAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.Integer(32));

        var nullDestinationLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_null_destination"));
        var checkCapacityLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_check_capacity"));
        var checkStorageLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_check_storage"));
        var storeLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_store"));
        var failNonnullLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_fail_nonnull"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_done"));

        var destinationIsNull = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_destination_is_null"))}";
        AppendLine($"  {destinationIsNull} = icmp eq ptr {destination}, null");
        AppendLine($"  br i1 {destinationIsNull}, label %{nullDestinationLabel}, label %{checkCapacityLabel}");

        AppendLine($"{checkCapacityLabel}:");
        var dataAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data_addr"))}";
        var lengthAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_length_addr"))}";
        var capacityAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity_addr"))}";
        var capacity = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity"))}";
        var capacityTooSmall = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity_too_small"))}";
        AppendLine($"  {dataAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 0");
        AppendLine($"  {lengthAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 1");
        AppendLine($"  {capacityAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 2");
        AppendLine($"  {capacity} = load i64, ptr {capacityAddress}{lengthAlignment}");
        AppendLine($"  {capacityTooSmall} = icmp slt i64 {capacity}, {sourceBytes.Length.ToString(CultureInfo.InvariantCulture)}");
        AppendLine($"  br i1 {capacityTooSmall}, label %{failNonnullLabel}, label %{checkStorageLabel}");

        string? data = null;
        AppendLine($"{checkStorageLabel}:");
        if (sourceBytes.Length == 0)
        {
            AppendLine($"  br label %{storeLabel}");
        }
        else
        {
            data = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data"))}";
            var dataIsNull = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data_is_null"))}";
            AppendLine($"  {data} = load ptr, ptr {dataAddress}{pointerAlignment}");
            AppendLine($"  {dataIsNull} = icmp eq ptr {data}, null");
            AppendLine($"  br i1 {dataIsNull}, label %{failNonnullLabel}, label %{storeLabel}");
        }

        AppendLine($"{storeLabel}:");
        if (sourceLiteralText is not null
            && sourceBytes.Length >= LlvmTextOptimizationConstants.AsciiToUnicodeLiteralMemcpyThresholdCodeUnits)
        {
            var unicodeConstant = ResolveStringConstant(sourceLiteralText, StarkTypeSymbols.Unicode);
            var copyByteLength = checked(sourceBytes.Length * 4);
            AppendLine($"  call void @llvm.memcpy.p0.p0.i64(ptr align 4 {data}, ptr align {unicodeConstant.AlignmentBytes} @{unicodeConstant.SymbolName}, i64 {copyByteLength.ToString(CultureInfo.InvariantCulture)}, i1 false)");
        }
        else
        {
            for (var index = 0; index < sourceBytes.Length; index++)
            {
                var destinationUnit = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_unit"))}";
                AppendLine($"  {destinationUnit} = getelementptr{GetProvenInObjectGepFlags()} i32, ptr {data}, i64 {index.ToString(CultureInfo.InvariantCulture)}");
                AppendLine($"  store i32 {sourceBytes[index].ToString(CultureInfo.InvariantCulture)}, ptr {destinationUnit}{unitAlignment}");
            }
        }

        AppendLine($"  store i64 {sourceBytes.Length.ToString(CultureInfo.InvariantCulture)}, ptr {lengthAddress}{lengthAlignment}");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{failNonnullLabel}:");
        AppendLine($"  store i64 0, ptr {lengthAddress}{lengthAlignment}");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{nullDestinationLabel}:");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{doneLabel}:");
        AppendLine($"  {result} = phi i1 [ true, %{storeLabel} ], [ false, %{failNonnullLabel} ], [ false, %{nullDestinationLabel} ]");
        if (_currentBlock is not null)
        {
            _blockExitLabels[_currentBlock.Id] = doneLabel;
        }

        return true;
    }

    private bool TryGetKnownAsciiLiteralPayload(
        SsaValue value,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        if (value is SsaStringConstant { Type.Kind: StarkTypeKind.Ascii } source)
        {
            if (!TextLiteralDecoder.TryDecode(
                    source.LiteralText,
                    source.LiteralText.StartsWith("'", StringComparison.Ordinal)
                        ? TextLiteralKind.Character
                        : TextLiteralKind.String,
                    out var decoded,
                    out _)
                || !decoded.IsAscii)
            {
                return false;
            }

            sourceBytes = decoded.Utf8Bytes;
            literalText = source.LiteralText;
            return true;
        }

        if (value is SsaValueReference reference
            && _valueFacts.TryGetValue(reference.Name, out var facts)
            && facts.Type.Kind == StarkTypeKind.Ascii
            && facts.TextLiteralPayloadKind == SsaFactLatticeKind.Known
            && facts.TextLiteralPayload is { IsAsciiOnly: true } payload)
        {
            try
            {
                sourceBytes = Convert.FromHexString(payload.Utf8PayloadHex);
                return sourceBytes.Length == payload.Utf8Length;
            }
            catch (FormatException)
            {
                sourceBytes = [];
                return false;
            }
        }

        return false;
    }

    private bool CanSplitCurrentBlockForCallSiteControlFlow()
    {
        if (_currentBlock is null)
        {
            return false;
        }

        // Splitting the emitted LLVM block changes the predecessor label seen by successors.
        // Forward successors can read the recorded exit label when their phis are emitted.
        // Backedges target already-emitted phis, so keep those on the ordinary call path
        // until the rewrite moves to SSA blocks.
        foreach (var targetId in EnumerateTerminatorTargets(_currentBlock.Terminator))
        {
            if (_blocksById.TryGetValue(targetId, out var targetBlock)
                && targetBlock.Phis.Count != 0
                && (!_blockOrderById.TryGetValue(targetId, out var targetOrder)
                    || !_blockOrderById.TryGetValue(_currentBlock.Id, out var currentOrder)
                    || targetOrder <= currentOrder))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsTryConvertAsciiToUnicodeAbiTarget(AbiFunctionSignature abiCallee)
    {
        if (string.Equals(abiCallee.SymbolName, "System_Text_TryConvertAsciiToUnicode", StringComparison.Ordinal)
            || string.Equals(abiCallee.Name, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal)
            || string.Equals(abiCallee.DisplaySourceName, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(_context.ModuleName, "System.Text", StringComparison.Ordinal)
            && string.Equals(abiCallee.SymbolName, "TryConvertAsciiToUnicode", StringComparison.Ordinal);
    }

    private string RenderCallTarget(AbiFunctionSignature abiCallee)
    {
        var escapedName = $"@{EscapeIdentifier(abiCallee.SymbolName)}";
        if (!abiCallee.IsVarargs)
        {
            return escapedName;
        }

        var parameterTypes = abiCallee.Parameters
            .Select(parameter => MapType(parameter.LlvmType))
            .Append("...");
        return $"({string.Join(", ", parameterTypes)}) {escapedName}";
    }

    private void EmitIndirectCall(string resultName, string result, SsaIndirectCallRValue call)
    {
        if (call.Target.Type.FunctionPointerParameterTypes is not { } parameterTypes)
        {
            throw new UnsupportedBodyEmissionException("Indirect call target is missing function-pointer parameter type metadata.");
        }

        if (parameterTypes.Count != call.Arguments.Count)
        {
            throw new UnsupportedBodyEmissionException(
                $"Indirect call argument count mismatch: expected {parameterTypes.Count}, got {call.Arguments.Count}.");
        }

        var arguments = new List<string>(call.Arguments.Count);
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            arguments.Add($"{MapType(parameterTypes[index])} {FormatValue(call.Arguments[index])}");
        }

        var callPrefixSegments = new List<string> { "call" };
        if (ShouldUseFastMathFlags(call.Type))
        {
            callPrefixSegments.Add("fast");
        }

        callPrefixSegments.Add("fastcc");
        var callPrefix = string.Join(" ", callPrefixSegments);
        var renderedArguments = string.Join(", ", arguments);
        var strictFpCallSuffix = GetStrictFpCallSuffix();

        if (call.Type.Kind == StarkTypeKind.Void)
        {
            AppendLine($"  {callPrefix} void {FormatValue(call.Target)}({renderedArguments}){strictFpCallSuffix}");
            return;
        }

        AppendLine($"  {result} = {callPrefix} {MapType(call.Type)} {FormatValue(call.Target)}({renderedArguments}){strictFpCallSuffix}{GetValueRangeMetadataSuffix(resultName, call.Type)}");
    }

    private bool ShouldEmitTailCallMarker(string resultName)
    {
        return _tailCallResultNames.Contains(resultName);
    }

    private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
    {
        EnsureLocalSlotExists(allocateLocal.LocalName, allocateLocal.LocalType);
        EmitLocalDebugDeclare(
            $"%{EscapeIdentifier($"slot_{allocateLocal.LocalName}")}",
            allocateLocal.LocalName,
            allocateLocal.LocalType,
            allocateLocal.Location);
    }

    private void EmitLifetimeStart(SsaLifetimeStartInstruction lifetimeStart)
    {
        EmitLifetimeMarker("start", lifetimeStart.LocalName, lifetimeStart.LocalType);
    }

    private void EmitLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd)
    {
        EmitLifetimeMarker("end", lifetimeEnd.LocalName, lifetimeEnd.LocalType);
    }

    private void EmitDeallocateLocal(SsaDeallocateLocalInstruction deallocateLocal)
    {
        if (deallocateLocal.StorageClass != "heap")
        {
            throw new UnsupportedBodyEmissionException(
                $"Local storage class '{deallocateLocal.StorageClass}' is not yet supported for LLVM deallocation.");
        }

        var slotName = $"%{EscapeIdentifier($"slot_{deallocateLocal.LocalName}")}";
        AppendLine($"  call void @{HeapFreeHelperName}(ptr {slotName})");
    }

    private void EmitLifetimeMarker(string phase, string localName, StarkTypeSymbol localType)
    {
        if (TryGetConcreteTypeLayout(localType) is not { } layout)
        {
            return;
        }

        EnsureLocalSlotExists(localName, localType);
        AppendLine($"  call void @llvm.lifetime.{phase}.p0(i64 {layout.SizeBytes}, ptr %{EscapeIdentifier($"slot_{localName}")})");
    }

    private bool ShouldUseFastMathFlags(StarkTypeSymbol type)
    {
        return !_isStrictFp && type.Kind == StarkTypeKind.Float;
    }

    private string GetFastMathSuffix(StarkTypeSymbol type)
    {
        return ShouldUseFastMathFlags(type) ? " fast" : string.Empty;
    }

    private string GetFastMathSuffix()
    {
        return _isStrictFp ? string.Empty : " fast";
    }

    private string GetStrictFpCallSuffix()
    {
        return _isStrictFp ? " strictfp" : string.Empty;
    }

    private static string GetFusedMultiplyAddIntrinsicName(StarkTypeSymbol type)
    {
        return $"llvm.fmuladd.{GetFloatIntrinsicSuffix(type)}";
    }

    private static string GetConstrainedBinaryIntrinsicName(string operation, StarkTypeSymbol type)
    {
        return $"llvm.experimental.constrained.{operation}.{GetFloatIntrinsicSuffix(type)}";
    }

    private static string GetConstrainedUnaryIntrinsicName(string operation, StarkTypeSymbol type)
    {
        return $"llvm.experimental.constrained.{operation}.{GetFloatIntrinsicSuffix(type)}";
    }

    private static string GetConstrainedFloatCompareIntrinsicName(StarkTypeSymbol type)
    {
        return $"llvm.experimental.constrained.fcmp.{GetFloatIntrinsicSuffix(type)}";
    }

    private static string GetConstrainedFloatConversionIntrinsicName(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType)
    {
        return $"llvm.experimental.constrained.{(sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc")}.{GetFloatIntrinsicSuffix(targetType)}.{GetFloatIntrinsicSuffix(sourceType)}";
    }

    private static string GetConstrainedIntegerToFloatIntrinsicName(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType,
        string opcode)
    {
        return $"llvm.experimental.constrained.{opcode}.{GetFloatIntrinsicSuffix(targetType)}.i{sourceType.BitWidth}";
    }

    private static string GetConstrainedFloatToIntegerIntrinsicName(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType,
        string opcode)
    {
        return $"llvm.experimental.constrained.{opcode}.i{targetType.BitWidth}.{GetFloatIntrinsicSuffix(sourceType)}";
    }

    private string GetTypeAlignmentSuffix(StarkTypeSymbol type)
    {
        return GetAlignmentSuffix(GetTypeAlignmentBytes(type));
    }

    private int? GetTypeAlignmentBytes(StarkTypeSymbol type)
    {
        return TryGetConcreteTypeLayout(NormalizeAggregateType(type)) is { AlignmentBytes: > 1 } layout
            ? layout.AlignmentBytes
            : null;
    }

    private string GetStackObjectAlignmentSuffix(StarkTypeSymbol type)
    {
        return GetAlignmentSuffix(GetStackObjectAlignmentBytes(type));
    }

    private int? GetStackObjectAlignmentBytes(StarkTypeSymbol type)
    {
        return GetOwnedObjectAlignmentBytes(type);
    }

    private int? GetHeapObjectAlignmentBytes(StarkTypeSymbol type)
    {
        return GetOwnedObjectAlignmentBytes(type);
    }

    private int? GetOwnedObjectAlignmentBytes(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        if (TryGetConcreteTypeLayout(normalizedType) is not { } layout)
        {
            return null;
        }

        var alignmentBytes = layout.AlignmentBytes;
        if (LlvmAggregateEmissionSupport.TryGetVectorizationFriendlyScalarArrayAlignmentBytes(
                normalizedType,
                layout) is int vectorFriendlyAlignmentBytes)
        {
            alignmentBytes = Math.Max(alignmentBytes, vectorFriendlyAlignmentBytes);
        }

        return alignmentBytes > 1 ? alignmentBytes : null;
    }

    private bool IsVectorizationFriendlyScalarArrayType(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return LlvmAggregateEmissionSupport.TryGetVectorizationFriendlyScalarArrayAlignmentBytes(
            normalizedType,
            TryGetConcreteTypeLayout(normalizedType)) is not null;
    }

    private string GetLocalObjectAlignmentSuffix(string localName, StarkTypeSymbol type)
    {
        return GetAlignmentSuffix(GetLocalObjectAlignmentBytes(localName, type));
    }

    private int? GetLocalObjectAlignmentBytes(string localName, StarkTypeSymbol type)
    {
        return GetLocalStorageClass(localName) switch
        {
            "stack" or "heap" => GetOwnedObjectAlignmentBytes(type),
            _ => GetTypeAlignmentBytes(type)
        };
    }

    private string GetGlobalObjectAlignmentSuffix(string globalName, StarkTypeSymbol type)
    {
        return GetAlignmentSuffix(GetGlobalObjectAlignmentBytes(globalName, type));
    }

    private int? GetGlobalObjectAlignmentBytes(string globalName, StarkTypeSymbol type)
    {
        var layout = TryGetConcreteTypeLayout(NormalizeAggregateType(type));
        var alignmentBytes = GetTypeAlignmentBytes(type) ?? 1;
        if (IsImmutableGlobalName(globalName)
            && LlvmAggregateEmissionSupport.TryGetReadonlyVectorizationFriendlyAlignmentBytes(type, layout) is int readonlyAlignmentBytes)
        {
            alignmentBytes = Math.Max(alignmentBytes, readonlyAlignmentBytes);
        }

        return alignmentBytes > 1 ? alignmentBytes : null;
    }

    private string GetKnownPointerAlignmentSuffix(SsaValue address, StarkTypeSymbol pointeeType)
    {
        return GetAlignmentSuffix(GetKnownPointerAlignmentBytes(address, pointeeType));
    }

    private string GetKnownPointerArgumentAlignmentFragment(SsaValue address, StarkTypeSymbol pointeeType)
    {
        return GetArgumentAlignmentFragment(GetKnownPointerAlignmentBytes(address, pointeeType));
    }

    private int? GetKnownPointerAlignmentBytes(SsaValue address, StarkTypeSymbol pointeeType)
    {
        return TryGetKnownPointerAlignmentBytes(address, pointeeType, out var alignmentBytes)
            ? alignmentBytes
            : null;
    }

    private static string GetAlignmentSuffix(int? alignmentBytes)
    {
        return alignmentBytes is > 1 ? $", align {alignmentBytes.Value}" : string.Empty;
    }

    private static string GetArgumentAlignmentFragment(int? alignmentBytes)
    {
        return alignmentBytes is > 1 ? $" align {alignmentBytes.Value}" : string.Empty;
    }

    private int? GetLeafAlignmentBytes(int? baseAlignmentBytes, StarkTypeSymbol leafType)
    {
        if (baseAlignmentBytes is null)
        {
            return null;
        }

        var leafAlignmentBytes = GetTypeAlignmentBytes(leafType);
        if (leafAlignmentBytes is null)
        {
            return null;
        }

        return Math.Min(baseAlignmentBytes.Value, leafAlignmentBytes.Value);
    }

    private bool TryGetKnownPointerAlignmentBytes(SsaValue address, StarkTypeSymbol pointeeType, out int alignmentBytes)
    {
        return TryGetKnownPointerAlignmentBytesCore(
            address,
            NormalizeAggregateType(pointeeType),
            new HashSet<string>(StringComparer.Ordinal),
            out alignmentBytes);
    }

    private bool TryGetKnownPointerAlignmentBytesCore(
        object address,
        StarkTypeSymbol pointeeType,
        ISet<string> visitedValueNames,
        out int alignmentBytes)
    {
        alignmentBytes = 1;

        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition)
                    && TryGetKnownPointerAlignmentBytesCore(definition, pointeeType, visitedValueNames, out alignmentBytes))
                {
                    return true;
                }

                if (_valueFacts.TryGetValue(reference.Name, out var facts)
                    && facts.PointerAlignmentKind == SsaFactLatticeKind.Known
                    && facts.PointerAlignmentBytes is > 1)
                {
                    alignmentBytes = facts.PointerAlignmentBytes.Value;
                    return true;
                }

                var indirectParameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => candidate.Kind == AbiParameterKind.IndirectIn
                        && (string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                            || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal)));
                if (indirectParameter is not null
                    && AbiLoweringHeuristics.IsByValueIndirectParameter(indirectParameter)
                    && GetTypeAlignmentBytes(indirectParameter.SourceType) is { } parameterAlignmentBytes)
                {
                    alignmentBytes = parameterAlignmentBytes;
                    return alignmentBytes > 1;
                }

                return false;
            case SsaGlobalAddressValue globalAddress:
                alignmentBytes = GetGlobalObjectAlignmentBytes(globalAddress.GlobalName, globalAddress.PointeeType) ?? 1;
                return alignmentBytes > 1;
            case SsaAddressOfLocalRValue addressOfLocal:
                alignmentBytes = GetLocalObjectAlignmentBytes(addressOfLocal.LocalName, addressOfLocal.PointeeType) ?? 1;
                return alignmentBytes > 1;
            case SsaAddressOfParameterRValue addressOfParameter:
            {
                var sourceParameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
                if (sourceParameter is null)
                {
                    return false;
                }

                if (sourceParameter.Kind == AbiParameterKind.IndirectIn)
                {
                    if (AbiLoweringHeuristics.IsByValueIndirectParameter(sourceParameter)
                        && GetTypeAlignmentBytes(sourceParameter.SourceType) is { } byvalAlignmentBytes)
                    {
                        alignmentBytes = byvalAlignmentBytes;
                        return alignmentBytes > 1;
                    }

                    return false;
                }

                alignmentBytes = GetStackObjectAlignmentBytes(addressOfParameter.PointeeType) ?? 1;
                return alignmentBytes > 1;
            }
            case SsaFieldAddressRValue fieldAddress:
            {
                if (!TryGetKnownPointerAlignmentBytesCore(fieldAddress.Address, fieldAddress.AggregateType, visitedValueNames, out var baseAlignmentBytes))
                {
                    return false;
                }

                var fieldType = GetPointeeType(fieldAddress.Type);
                if (fieldType is null || GetTypeAlignmentBytes(fieldType) is not { } fieldAlignmentBytes)
                {
                    return false;
                }

                alignmentBytes = Math.Min(baseAlignmentBytes, fieldAlignmentBytes);
                return alignmentBytes > 1;
            }
            case SsaElementAddressRValue elementAddress:
            {
                if (!TryGetKnownPointerAlignmentBytesCore(elementAddress.Address, elementAddress.AggregateType, visitedValueNames, out var baseAlignmentBytes))
                {
                    return false;
                }

                var elementType = GetPointeeType(elementAddress.Type)
                    ?? elementAddress.AggregateType.ElementType;
                if (elementType is null || GetTypeAlignmentBytes(elementType) is not { } elementAlignmentBytes)
                {
                    return false;
                }

                alignmentBytes = Math.Min(baseAlignmentBytes, elementAlignmentBytes);
                return alignmentBytes > 1;
            }
            case SsaSliceElementAddressRValue sliceElementAddress:
            {
                if (!TryGetKnownSliceDataAlignmentBytes(sliceElementAddress.Slice, visitedValueNames, out var sliceAlignmentBytes))
                {
                    return false;
                }

                var elementType = GetPointeeType(sliceElementAddress.Type);
                if (elementType is null || GetTypeAlignmentBytes(elementType) is not { } elementAlignmentBytes)
                {
                    return false;
                }

                alignmentBytes = Math.Min(sliceAlignmentBytes, elementAlignmentBytes);
                return alignmentBytes > 1;
            }
            case SsaUseRValue use:
                return TryGetKnownPointerAlignmentBytesCore(use.Value, pointeeType, visitedValueNames, out alignmentBytes);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryGetKnownPointerAlignmentBytesCore(convert.Operand, pointeeType, visitedValueNames, out alignmentBytes);
            default:
                return false;
        }
    }

    private bool TryGetKnownSliceDataAlignmentBytes(object slice, ISet<string> visitedValueNames, out int alignmentBytes)
    {
        alignmentBytes = 1;

        switch (slice)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryGetKnownSliceDataAlignmentBytes(definition, visitedValueNames, out alignmentBytes);
            case SsaUseRValue use:
                return TryGetKnownSliceDataAlignmentBytes(use.Value, visitedValueNames, out alignmentBytes);
            case SsaMakeSliceFromLocalRValue makeSlice when makeSlice.SourceType.Kind == StarkTypeKind.FixedArray
                                                           && makeSlice.SourceType.ElementType is not null:
                alignmentBytes = GetLocalObjectAlignmentBytes(makeSlice.LocalName, makeSlice.SourceType)
                    ?? GetTypeAlignmentBytes(makeSlice.SourceType.ElementType)
                    ?? 1;
                return alignmentBytes > 1;
            case SsaMakeSliceFromPointerRValue makeSlice
                when makeSlice.Pointer.Type.ElementType is { } elementType
                     && TryGetKnownPointerAlignmentBytesCore(makeSlice.Pointer, elementType, visitedValueNames, out var pointerAlignmentBytes):
                alignmentBytes = pointerAlignmentBytes;
                return alignmentBytes > 1;
            case SsaLoadLocalRValue loadLocal
                when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue):
                return TryGetKnownSliceDataAlignmentBytes(storedValue, visitedValueNames, out alignmentBytes);
            case SsaTextSliceRValue textSlice:
            {
                var unitType = GetTextUnitType(textSlice.TextValue.Type);
                alignmentBytes = GetTypeAlignmentBytes(unitType) ?? 1;
                return alignmentBytes > 1;
            }
            default:
                return false;
        }
    }

    private static StarkTypeSymbol? GetPointeeType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.RawPointer
            ? type.ElementType
            : null;
    }

    private void EmitStoreLocal(SsaStoreLocalInstruction storeLocal)
    {
        EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
        var slot = $"%{EscapeIdentifier($"slot_{storeLocal.LocalName}")}";
        EmitValueToAddress(
            slot,
            storeLocal.LocalType,
            storeLocal.Value,
            GetLocalObjectAlignmentBytes(storeLocal.LocalName, storeLocal.LocalType),
            GetDirectTbaaMetadataSuffix(CreateTbaaLocalRootKey(storeLocal.LocalName), storeLocal.LocalType));
        EmitInvariantStartForLocalIfNeeded(storeLocal.LocalName, storeLocal.LocalType);
    }

    private void EmitCopyMemory(SsaCopyMemoryInstruction copyMemory)
    {
        var invariantDestinationLocal = TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var localName)
            && _invariantLocalNames.Contains(localName)
                ? localName
                : null;

        if (TryEmitScalarizedAggregateCopy(copyMemory.DestinationAddress, copyMemory.SourceAddress, copyMemory.CopyType))
        {
            EmitInvariantStartForLocalIfNeeded(invariantDestinationLocal, copyMemory.CopyType);
            return;
        }

        if (TryGetConcreteTypeLayout(copyMemory.CopyType) is { } layout
            && layout.SizeBytes > AggregateMemcpyThresholdBytes)
        {
            AppendLine(
                $"  call void @llvm.memcpy.inline.p0.p0.i64(ptr{GetKnownPointerArgumentAlignmentFragment(copyMemory.DestinationAddress, copyMemory.CopyType)} {FormatValue(copyMemory.DestinationAddress)}, ptr{GetKnownPointerArgumentAlignmentFragment(copyMemory.SourceAddress, copyMemory.CopyType)} {FormatValue(copyMemory.SourceAddress)}, i64 {layout.SizeBytes}, i1 false)");
            EmitInvariantStartForLocalIfNeeded(invariantDestinationLocal, copyMemory.CopyType);
            return;
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
        AppendLine(
            $"  {loadedValue} = load {MapType(copyMemory.CopyType)}, ptr {FormatValue(copyMemory.SourceAddress)}{GetKnownPointerAlignmentSuffix(copyMemory.SourceAddress, copyMemory.CopyType)}{GetInvariantLoadMetadataSuffix(copyMemory.SourceAddress)}{GetValueRangeMetadataSuffix(copyMemory.CopyType)}{GetTbaaMetadataSuffix(copyMemory.SourceAddress, copyMemory.CopyType)}{GetScopedNoAliasMetadataSuffix(copyMemory.SourceAddress, copyMemory.ScopedNoAliasGroups)}{GetLoopAccessGroupMetadataSuffix(copyMemory.LoopAccessGroups)}");
        AppendLine($"  store {MapType(copyMemory.CopyType)} {loadedValue}, ptr {FormatValue(copyMemory.DestinationAddress)}{GetKnownPointerAlignmentSuffix(copyMemory.DestinationAddress, copyMemory.CopyType)}{GetTbaaMetadataSuffix(copyMemory.DestinationAddress, copyMemory.CopyType)}{GetScopedNoAliasMetadataSuffix(copyMemory.DestinationAddress, copyMemory.ScopedNoAliasGroups)}{GetLoopAccessGroupMetadataSuffix(copyMemory.LoopAccessGroups)}");
        EmitInvariantStartForLocalIfNeeded(invariantDestinationLocal, copyMemory.CopyType);
    }

    private void EmitStoreIndirect(SsaStoreIndirectInstruction storeIndirect)
    {
        EmitValueToAddress(
            FormatValue(storeIndirect.Address),
            storeIndirect.ValueType,
            storeIndirect.Value,
            GetKnownPointerAlignmentBytes(storeIndirect.Address, storeIndirect.ValueType),
            GetTbaaMetadataSuffix(storeIndirect.Address, storeIndirect.ValueType),
            GetScopedNoAliasMetadataSuffix(storeIndirect.Address, storeIndirect.ScopedNoAliasGroups)
                + GetLoopAccessGroupMetadataSuffix(storeIndirect.LoopAccessGroups));
    }

    private void EmitValueToAddress(SsaValue destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        EmitValueToAddress(
            FormatValue(destinationAddress),
            valueType,
            value,
            GetKnownPointerAlignmentBytes(destinationAddress, valueType),
            GetTbaaMetadataSuffix(destinationAddress, valueType),
            GetScopedNoAliasMetadataSuffix(destinationAddress));
    }

    private void EmitValueToAddress(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        int? alignmentBytes,
        string tbaaMetadataSuffix = "",
        string scopedNoAliasMetadataSuffix = "")
    {
        if (TryEmitInlineAggregateZeroFill(destinationAddress, valueType, value, alignmentBytes))
        {
            return;
        }

        if (ShouldPreferAddressBasedAggregateLowering(valueType))
        {
            if (TryEmitAggregateAddressCopy(destinationAddress, valueType, value, alignmentBytes))
            {
                return;
            }

            if (TryEmitStructuredAggregateStore(destinationAddress, valueType, value, alignmentBytes))
            {
                return;
            }
        }

        if (TryEmitScalarizedAggregateStore(destinationAddress, valueType, value, alignmentBytes, tbaaMetadataSuffix, scopedNoAliasMetadataSuffix))
        {
            return;
        }

        AppendLine($"  store {MapType(valueType)} {FormatValue(value)}, ptr {destinationAddress}{GetAlignmentSuffix(alignmentBytes)}{tbaaMetadataSuffix}{scopedNoAliasMetadataSuffix}");
    }

    private bool TryEmitInlineAggregateZeroFill(string destinationAddress, StarkTypeSymbol valueType, SsaValue value, int? alignmentBytes)
    {
        if (value is not SsaZeroInitializerValue
            || !ShouldEmitInlineAggregateZeroFill(valueType)
            || TryGetConcreteTypeLayout(valueType) is not { } layout)
        {
            return false;
        }

        AppendLine($"  call void @llvm.memset.inline.p0.i64(ptr{GetArgumentAlignmentFragment(alignmentBytes)} {destinationAddress}, i8 0, i64 {layout.SizeBytes}, i1 false)");
        return true;
    }

    private bool ShouldEmitInlineAggregateZeroFill(StarkTypeSymbol valueType)
    {
        if (TryGetConcreteTypeLayout(NormalizeAggregateType(valueType)) is not { } layout
            || layout.SizeBytes <= AggregateScalarizationThresholdBytes)
        {
            return false;
        }

        return valueType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
    }

    private bool ShouldPreferAddressBasedAggregateLowering(StarkTypeSymbol valueType)
    {
        return ShouldEmitInlineAggregateZeroFill(valueType);
    }

    private bool TryEmitScalarizedAggregateCopy(SsaValue destinationAddress, SsaValue sourceAddress, StarkTypeSymbol copyType)
    {
        return TryEmitScalarizedAggregateCopy(
            FormatValue(destinationAddress),
            FormatValue(sourceAddress),
            copyType,
            GetKnownPointerAlignmentBytes(destinationAddress, copyType),
            GetKnownPointerAlignmentBytes(sourceAddress, copyType),
            GetInvariantLoadMetadataSuffix(sourceAddress));
    }

    private bool TryEmitScalarizedAggregateCopy(
        string destinationAddress,
        string sourceAddress,
        StarkTypeSymbol copyType,
        int? destinationAlignmentBytes,
        int? sourceAlignmentBytes,
        string invariantLoadMetadataSuffix)
    {
        if (IsVectorizationFriendlyScalarArrayType(copyType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                copyType,
                requireRepresentationPreserving: true,
                ignoreScalarizationThresholds: false,
                allowTextLeaves: false,
                allowSliceLeaves: false,
                out var leaves))
        {
            return false;
        }

        foreach (var leaf in leaves)
        {
            var sourceLeafAddress = EmitScalarizedAggregateLeafAddress(sourceAddress, copyType, leaf.Indices, "copy_src");
            var loadedLeaf = $"%{EscapeIdentifier(CreateAbiTempName("copy_scalar_load"))}";
            var sourceLeafAlignmentBytes = GetLeafAlignmentBytes(sourceAlignmentBytes, leaf.Type);
            AppendLine(
                $"  {loadedLeaf} = load {MapType(leaf.Type)}, ptr {sourceLeafAddress}{GetAlignmentSuffix(sourceLeafAlignmentBytes)}{invariantLoadMetadataSuffix}{GetValueRangeMetadataSuffix(leaf.Type)}");
            var destinationLeafAddress = EmitScalarizedAggregateLeafAddress(destinationAddress, copyType, leaf.Indices, "copy_dest");
            var destinationLeafAlignmentBytes = GetLeafAlignmentBytes(destinationAlignmentBytes, leaf.Type);
            AppendLine($"  store {MapType(leaf.Type)} {loadedLeaf}, ptr {destinationLeafAddress}{GetAlignmentSuffix(destinationLeafAlignmentBytes)}");
        }

        return true;
    }

    private bool TryEmitAggregateAddressCopy(string destinationAddress, StarkTypeSymbol valueType, SsaValue value, int? destinationAlignmentBytes)
    {
        if (!TryResolveAggregateSourceAddress(value, valueType, out var sourceAddress))
        {
            return false;
        }

        EmitAggregateAddressCopy(
            destinationAddress,
            sourceAddress,
            valueType,
            destinationAlignmentBytes,
            GetKnownPointerAlignmentBytes(value, valueType),
            GetInvariantLoadMetadataSuffixForAggregateSource(value));
        return true;
    }

    private void EmitAggregateAddressCopy(
        string destinationAddress,
        string sourceAddress,
        StarkTypeSymbol copyType,
        int? destinationAlignmentBytes,
        int? sourceAlignmentBytes,
        string invariantLoadMetadataSuffix = "")
    {
        if (TryEmitScalarizedAggregateCopy(destinationAddress, sourceAddress, copyType, destinationAlignmentBytes, sourceAlignmentBytes, invariantLoadMetadataSuffix))
        {
            return;
        }

        if (TryGetConcreteTypeLayout(copyType) is { } layout
            && layout.SizeBytes > AggregateScalarizationThresholdBytes)
        {
            AppendLine(
                $"  call void @llvm.memcpy.inline.p0.p0.i64(ptr{GetArgumentAlignmentFragment(destinationAlignmentBytes)} {destinationAddress}, ptr{GetArgumentAlignmentFragment(sourceAlignmentBytes)} {sourceAddress}, i64 {layout.SizeBytes}, i1 false)");
            return;
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
        AppendLine($"  {loadedValue} = load {MapType(copyType)}, ptr {sourceAddress}{GetAlignmentSuffix(sourceAlignmentBytes)}{invariantLoadMetadataSuffix}{GetValueRangeMetadataSuffix(copyType)}");
        AppendLine($"  store {MapType(copyType)} {loadedValue}, ptr {destinationAddress}{GetAlignmentSuffix(destinationAlignmentBytes)}");
    }

    private bool TryResolveAggregateSourceAddress(SsaValue value, StarkTypeSymbol expectedType, out string sourceAddress)
    {
        return TryResolveAggregateSourceAddress(
            value,
            expectedType,
            new HashSet<string>(StringComparer.Ordinal),
            out sourceAddress);
    }

    private bool TryResolveAggregateSourceAddress(
        SsaValue value,
        StarkTypeSymbol expectedType,
        ISet<string> visitedValueNames,
        out string sourceAddress)
    {
        var normalizedExpectedType = NormalizeAggregateType(expectedType);

        switch (value)
        {
            case SsaValueReference reference:
                if (_indirectAggregateValueSlots.TryGetValue(reference.Name, out var indirectSlot))
                {
                    sourceAddress = indirectSlot;
                    return true;
                }

                var indirectParameter = _abiFunction.UserParameters.FirstOrDefault(
                    parameter => parameter.Kind == AbiParameterKind.IndirectIn
                        && NormalizeAggregateType(parameter.SourceType) == normalizedExpectedType
                        && (string.Equals(parameter.LlvmName, reference.Name, StringComparison.Ordinal)
                            || string.Equals(parameter.SourceName, reference.Name, StringComparison.Ordinal)));
                if (indirectParameter is not null)
                {
                    sourceAddress = $"%{EscapeIdentifier(indirectParameter.LlvmName)}";
                    return true;
                }

                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    sourceAddress = string.Empty;
                    return false;
                }

                switch (definition)
                {
                    case SsaUseRValue use when NormalizeAggregateType(use.Type) == normalizedExpectedType:
                        return TryResolveAggregateSourceAddress(use.Value, expectedType, visitedValueNames, out sourceAddress);
                    case SsaLoadLocalRValue loadLocal when NormalizeAggregateType(loadLocal.Type) == normalizedExpectedType:
                        EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                        sourceAddress = $"%{EscapeIdentifier($"slot_{loadLocal.LocalName}")}";
                        return true;
                    case SsaLoadGlobalRValue loadGlobal when NormalizeAggregateType(loadGlobal.Type) == normalizedExpectedType:
                        sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(loadGlobal.GlobalName))}";
                        return true;
                    case SsaLoadIndirectRValue loadIndirect when NormalizeAggregateType(loadIndirect.Type) == normalizedExpectedType:
                        sourceAddress = FormatValue(loadIndirect.Address);
                        return true;
                    default:
                        sourceAddress = string.Empty;
                        return false;
                }
            case SsaGlobalAddressValue globalAddress when NormalizeAggregateType(globalAddress.PointeeType) == normalizedExpectedType:
                sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}";
                return true;
            default:
                sourceAddress = string.Empty;
                return false;
        }
    }

    private bool TryEmitAggregateElementLoad(
        string result,
        SsaValue target,
        int elementIndex,
        StarkTypeSymbol elementType,
        string purpose)
    {
        if (!CanExtractAggregateElementFromAddress(target.Type, elementIndex, elementType)
            || !TryResolveAggregateSourceAddress(target, target.Type, out var sourceAddress))
        {
            return false;
        }

        var elementAddress = EmitScalarizedAggregateLeafAddress(sourceAddress, target.Type, [elementIndex], purpose);
        var alignmentBytes = GetLeafAlignmentBytes(GetTypeAlignmentBytes(target.Type), elementType);
        AppendLine($"  {result} = load {MapType(elementType)}, ptr {elementAddress}{GetAlignmentSuffix(alignmentBytes)}{GetValueRangeMetadataSuffix(elementType)}");
        return true;
    }

    private bool TryEmitStructuredAggregateStore(string destinationAddress, StarkTypeSymbol valueType, SsaValue value, int? destinationAlignmentBytes = null)
    {
        return TryEmitStructuredAggregateStore(
            destinationAddress,
            valueType,
            value,
            destinationAlignmentBytes,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryEmitStructuredAggregateStore(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        int? destinationAlignmentBytes,
        ISet<string> visitedValueNames)
    {
        switch (value)
        {
            case SsaZeroInitializerValue:
                if (!TryEmitInlineAggregateZeroFill(destinationAddress, valueType, value, destinationAlignmentBytes))
                {
                    AppendLine($"  store {MapType(valueType)} zeroinitializer, ptr {destinationAddress}{GetAlignmentSuffix(destinationAlignmentBytes)}");
                }

                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                switch (definition)
                {
                    case SsaUseRValue use when NormalizeAggregateType(use.Type) == NormalizeAggregateType(valueType):
                        return TryEmitStructuredAggregateStore(destinationAddress, valueType, use.Value, destinationAlignmentBytes, visitedValueNames);
                    case SsaInsertFieldRValue insertField when NormalizeAggregateType(insertField.Type) == NormalizeAggregateType(valueType):
                    {
                        var fieldType = GetAggregateElementType(valueType, insertField.FieldIndex);
                        if (fieldType is null
                            || !TryEmitStructuredAggregateStore(destinationAddress, valueType, insertField.Target, destinationAlignmentBytes, visitedValueNames))
                        {
                            return false;
                        }

                        var fieldAddress = EmitScalarizedAggregateLeafAddress(
                            destinationAddress,
                            valueType,
                            [insertField.FieldIndex],
                            "insert_field_store");
                        EmitValueToAddress(fieldAddress, fieldType, insertField.Value, GetLeafAlignmentBytes(destinationAlignmentBytes, fieldType));
                        return true;
                    }
                    case SsaInsertIndexRValue insertIndex when NormalizeAggregateType(insertIndex.Type) == NormalizeAggregateType(valueType):
                    {
                        var elementType = GetAggregateElementType(valueType, insertIndex.ElementIndex);
                        if (elementType is null
                            || !TryEmitStructuredAggregateStore(destinationAddress, valueType, insertIndex.Target, destinationAlignmentBytes, visitedValueNames))
                        {
                            return false;
                        }

                        var elementAddress = EmitScalarizedAggregateLeafAddress(
                            destinationAddress,
                            valueType,
                            [insertIndex.ElementIndex],
                            "insert_index_store");
                        EmitValueToAddress(elementAddress, elementType, insertIndex.Value, GetLeafAlignmentBytes(destinationAlignmentBytes, elementType));
                        return true;
                    }
                    default:
                        return false;
                }
            default:
                return false;
        }
    }

    private bool RequiresAggregateValueMaterialization(string valueName, StarkTypeSymbol valueType)
    {
        if (_aggregateValueMaterializationRequirements.TryGetValue(valueName, out var cached))
        {
            return cached;
        }

        var required = RequiresAggregateValueMaterialization(
            valueName,
            valueType,
            new HashSet<string>(StringComparer.Ordinal));
        _aggregateValueMaterializationRequirements[valueName] = required;
        return required;
    }

    private bool RequiresAggregateValueMaterialization(
        string valueName,
        StarkTypeSymbol valueType,
        ISet<string> visitingValueNames)
    {
        if (_aggregateValueMaterializationRequirements.TryGetValue(valueName, out var cached))
        {
            return cached;
        }

        if (!visitingValueNames.Add(valueName))
        {
            return true;
        }

        try
        {
            foreach (var block in _ssaFunction.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    if (phi.Incomings.Any(incoming => IsNamedReference(incoming.Value, valueName)))
                    {
                        return true;
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    if (InstructionRequiresAggregateValueMaterialization(
                            instruction,
                            valueName,
                            valueType,
                            visitingValueNames))
                    {
                        return true;
                    }
                }

                if (TerminatorRequiresAggregateValueMaterialization(
                        block.Terminator,
                        valueName,
                        valueType,
                        visitingValueNames))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visitingValueNames.Remove(valueName);
        }
    }

    private bool InstructionRequiresAggregateValueMaterialization(
        SsaInstruction instruction,
        string valueName,
        StarkTypeSymbol valueType,
        ISet<string> visitingValueNames)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                return RValueRequiresAggregateValueMaterialization(
                    valueInstruction,
                    valueName,
                    valueType,
                    visitingValueNames);
            case SsaStoreLocalInstruction storeLocal when IsNamedReference(storeLocal.Value, valueName):
                return !CanForwardAggregateValueToAddress(storeLocal.LocalType, valueType);
            case SsaStoreIndirectInstruction storeIndirect when IsNamedReference(storeIndirect.Value, valueName):
                return !CanForwardAggregateValueToAddress(storeIndirect.ValueType, valueType);
            case SsaStoreGlobalInstruction storeGlobal when IsNamedReference(storeGlobal.Value, valueName):
                return !CanForwardAggregateValueToAddress(storeGlobal.GlobalType, valueType);
            default:
                return false;
        }
    }

    private bool RValueRequiresAggregateValueMaterialization(
        SsaValueInstruction valueInstruction,
        string valueName,
        StarkTypeSymbol valueType,
        ISet<string> visitingValueNames)
    {
        switch (valueInstruction.Value)
        {
            case SsaUseRValue use when IsNamedReference(use.Value, valueName):
                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
            case SsaInsertFieldRValue insertField when IsNamedReference(insertField.Target, valueName):
                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
            case SsaInsertIndexRValue insertIndex when IsNamedReference(insertIndex.Target, valueName):
                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
            case SsaExtractFieldRValue extractField when IsNamedReference(extractField.Target, valueName):
                return !CanExtractAggregateElementFromAddress(valueType, extractField.FieldIndex, extractField.Type);
            case SsaExtractIndexRValue extractIndex when IsNamedReference(extractIndex.Target, valueName):
                return !CanExtractAggregateElementFromAddress(valueType, extractIndex.ElementIndex, extractIndex.Type);
            case SsaCallRValue call:
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    if (!IsNamedReference(call.Arguments[index], valueName))
                    {
                        continue;
                    }

                    if (!CanForwardAggregateValueToIndirectCallParameter(call, index, valueType))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return RValueContainsNamedReference(valueInstruction.Value, valueName);
        }
    }

    private bool TerminatorRequiresAggregateValueMaterialization(
        SsaTerminator terminator,
        string valueName,
        StarkTypeSymbol valueType,
        ISet<string> visitingValueNames)
    {
        if (IsNamedReference(terminator.Condition, valueName))
        {
            return true;
        }

        if (terminator.SwitchCases is not null
            && terminator.SwitchCases.Any(switchCase => IsNamedReference(switchCase.MatchValue, valueName)))
        {
            return true;
        }

        if (terminator.Kind != SsaTerminatorKind.Return
            || !IsNamedReference(terminator.Value, valueName))
        {
            return false;
        }

        return !_abiFunction.ReturnsIndirect
            || !CanForwardAggregateValueToAddress(_function.ReturnType, valueType);
    }

    private bool CanForwardAggregateValueToIndirectCallParameter(SsaCallRValue call, int argumentIndex, StarkTypeSymbol valueType)
    {
        var calleeAbi = _resolveCallAbi(_function.Name, call.FunctionName);
        if (calleeAbi is null || argumentIndex >= calleeAbi.UserParameters.Count)
        {
            return false;
        }

        var parameter = calleeAbi.UserParameters[argumentIndex];
        return AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
            && NormalizeAggregateType(parameter.SourceType) == NormalizeAggregateType(valueType);
    }

    private bool CanForwardAggregateValueToAddress(StarkTypeSymbol destinationType, StarkTypeSymbol valueType)
    {
        return ShouldPreferAddressBasedAggregateLowering(destinationType)
            && NormalizeAggregateType(destinationType) == NormalizeAggregateType(valueType);
    }

    private bool CanExtractAggregateElementFromAddress(
        StarkTypeSymbol aggregateType,
        int elementIndex,
        StarkTypeSymbol elementType)
    {
        return GetAggregateElementType(aggregateType, elementIndex) is { } resolvedElementType
            && NormalizeAggregateType(resolvedElementType) == NormalizeAggregateType(elementType);
    }

    private static bool IsNamedReference(SsaValue? value, string valueName)
    {
        return value is SsaValueReference reference
            && string.Equals(reference.Name, valueName, StringComparison.Ordinal);
    }

    private static bool RValueContainsNamedReference(SsaRValue value, string valueName)
    {
        return value switch
        {
            SsaUseRValue use => IsNamedReference(use.Value, valueName),
            SsaUnaryRValue unary => IsNamedReference(unary.Operand, valueName),
            SsaBinaryRValue binary => IsNamedReference(binary.Left, valueName) || IsNamedReference(binary.Right, valueName),
            SsaSelectRValue select => IsNamedReference(select.Condition, valueName)
                || IsNamedReference(select.WhenTrue, valueName)
                || IsNamedReference(select.WhenFalse, valueName),
            SsaCallRValue call => call.Arguments.Any(argument => IsNamedReference(argument, valueName))
                || (call.IndirectArgumentAddresses?.OfType<SsaValue>().Any(address => IsNamedReference(address, valueName)) ?? false),
            SsaConvertRValue convert => IsNamedReference(convert.Operand, valueName),
            SsaExtractFieldRValue extractField => IsNamedReference(extractField.Target, valueName),
            SsaInsertFieldRValue insertField => IsNamedReference(insertField.Target, valueName) || IsNamedReference(insertField.Value, valueName),
            SsaExtractIndexRValue extractIndex => IsNamedReference(extractIndex.Target, valueName),
            SsaInsertIndexRValue insertIndex => IsNamedReference(insertIndex.Target, valueName) || IsNamedReference(insertIndex.Value, valueName),
            SsaMakeSliceFromPointerRValue makeSlice => IsNamedReference(makeSlice.Pointer, valueName) || IsNamedReference(makeSlice.Length, valueName),
            SsaLoadSliceElementRValue loadSlice => IsNamedReference(loadSlice.Slice, valueName) || IsNamedReference(loadSlice.Index, valueName),
            SsaTextSliceRValue textSlice => IsNamedReference(textSlice.TextValue, valueName) || IsNamedReference(textSlice.Start, valueName) || IsNamedReference(textSlice.Length, valueName),
            SsaFieldAddressRValue fieldAddress => IsNamedReference(fieldAddress.Address, valueName),
            SsaElementAddressRValue elementAddress => IsNamedReference(elementAddress.Address, valueName) || IsNamedReference(elementAddress.Index, valueName),
            SsaSliceElementAddressRValue sliceElementAddress => IsNamedReference(sliceElementAddress.Slice, valueName) || IsNamedReference(sliceElementAddress.Index, valueName),
            SsaLoadIndirectRValue loadIndirect => IsNamedReference(loadIndirect.Address, valueName),
            _ => false
        };
    }

    private bool TryEmitScalarizedAggregateStore(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        int? destinationAlignmentBytes,
        string tbaaMetadataSuffix = "",
        string scopedNoAliasMetadataSuffix = "")
    {
        if (IsVectorizationFriendlyScalarArrayType(valueType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                valueType,
                requireRepresentationPreserving: true,
                ignoreScalarizationThresholds: false,
                allowTextLeaves: false,
                allowSliceLeaves: false,
                out var leaves))
        {
            return false;
        }

        foreach (var leaf in leaves)
        {
            var leafValue = EmitScalarizedAggregateLeafValue(value, valueType, leaf.Indices, leaf.Type);
            var leafAddress = EmitScalarizedAggregateLeafAddress(destinationAddress, valueType, leaf.Indices, "store_dest");
            var leafTbaaMetadataSuffix = leaf.Indices.Count == 0 ? tbaaMetadataSuffix : string.Empty;
            AppendLine($"  store {MapType(leaf.Type)} {leafValue}, ptr {leafAddress}{GetAlignmentSuffix(GetLeafAlignmentBytes(destinationAlignmentBytes, leaf.Type))}{leafTbaaMetadataSuffix}{scopedNoAliasMetadataSuffix}");
        }

        return true;
    }

    private bool TryGetScalarizableAggregateLeaves(
        StarkTypeSymbol type,
        bool requireRepresentationPreserving,
        bool ignoreScalarizationThresholds,
        bool allowTextLeaves,
        bool allowSliceLeaves,
        out IReadOnlyList<AggregateScalarLeaf> leaves)
    {
        leaves = Array.Empty<AggregateScalarLeaf>();

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(type)) is not { } layout
            || layout.SizeBytes <= 0
            || (!ignoreScalarizationThresholds && layout.SizeBytes > AggregateScalarizationThresholdBytes))
        {
            return false;
        }

        var collectedLeaves = new List<AggregateScalarLeaf>();
        if (!TryCollectScalarizableAggregateLeaves(
                NormalizeAggregateType(type),
                requireRepresentationPreserving,
                allowTextLeaves,
                allowSliceLeaves,
                [],
                collectedLeaves))
        {
            return false;
        }

        if (collectedLeaves.Count == 0
            || (!ignoreScalarizationThresholds && collectedLeaves.Count > AggregateScalarizationMaxLeafCount))
        {
            return false;
        }

        leaves = collectedLeaves;
        return true;
    }

    private bool TryCollectScalarizableAggregateLeaves(
        StarkTypeSymbol type,
        bool requireRepresentationPreserving,
        bool allowTextLeaves,
        bool allowSliceLeaves,
        List<int> path,
        List<AggregateScalarLeaf> leaves)
    {
        var normalizedType = NormalizeAggregateType(type);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.Bool:
            case StarkTypeKind.Integer:
            case StarkTypeKind.Float:
            case StarkTypeKind.RawPointer:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.Ascii when allowTextLeaves:
            case StarkTypeKind.Unicode when allowTextLeaves:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.Slice when allowSliceLeaves:
                leaves.Add(new AggregateScalarLeaf([.. path], normalizedType));
                return true;
            case StarkTypeKind.FixedArray when normalizedType.ElementType is not null && normalizedType.FixedLength is int fixedLength:
                for (var index = 0; index < fixedLength; index++)
                {
                    path.Add(index);
                    if (!TryCollectScalarizableAggregateLeaves(
                            normalizedType.ElementType,
                            requireRepresentationPreserving,
                            allowTextLeaves,
                            allowSliceLeaves,
                            path,
                            leaves))
                    {
                        path.RemoveAt(path.Count - 1);
                        return false;
                    }

                    path.RemoveAt(path.Count - 1);
                }

                return true;
            case StarkTypeKind.Named:
            {
                var namedType = ResolveNamedTypeSymbol(normalizedType);
                if (namedType is null
                    || !TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields))
                {
                    return false;
                }

                var sizeBytes = 0;
                var alignmentBytes = 1;
                for (var index = 0; index < orderedFields.Count; index++)
                {
                    var field = orderedFields[index];
                    var fieldLayout = TryGetConcreteTypeLayout(NormalizeAggregateType(field.Type));
                    if (fieldLayout is null)
                    {
                        return false;
                    }

                    var alignedOffset = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                    if (requireRepresentationPreserving && alignedOffset != sizeBytes)
                    {
                        return false;
                    }

                    path.Add(index);
                    if (!TryCollectScalarizableAggregateLeaves(
                            field.Type,
                            requireRepresentationPreserving,
                            allowTextLeaves,
                            allowSliceLeaves,
                            path,
                            leaves))
                    {
                        path.RemoveAt(path.Count - 1);
                        return false;
                    }

                    path.RemoveAt(path.Count - 1);
                    sizeBytes = checked(alignedOffset + fieldLayout.SizeBytes);
                    alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
                }

                if (requireRepresentationPreserving && AlignTo(sizeBytes, alignmentBytes) != sizeBytes)
                {
                    return false;
                }

                return true;
            }
            default:
                return false;
        }
    }

    private string EmitScalarizedAggregateLeafValue(
        SsaValue value,
        StarkTypeSymbol rootType,
        IReadOnlyList<int> indices,
        StarkTypeSymbol leafType)
    {
        if (value is SsaZeroInitializerValue)
        {
            return FormatZeroInitializer(leafType);
        }

        if (value is SsaUndefValue)
        {
            return "undef";
        }

        var currentValue = FormatValue(value);
        var currentType = NormalizeAggregateType(rootType);

        foreach (var index in indices)
        {
            var nextType = GetAggregateElementType(currentType, index)
                ?? throw new UnsupportedBodyEmissionException(
                    $"Cannot scalarize aggregate leaf '{value.Text}' for '{rootType.DisplayName}'.");
            var extracted = $"%{EscapeIdentifier(CreateAbiTempName("scalar_extract"))}";
            AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
            currentValue = extracted;
            currentType = NormalizeAggregateType(nextType);
        }

        return currentValue;
    }

    private string EmitAggregateLeafValueExtraction(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        string rootValue,
        IReadOnlyList<int> indices,
        string purpose)
    {
        if (indices.Count == 0)
        {
            return rootValue;
        }

        var currentValue = rootValue;
        var currentType = NormalizeAggregateType(rootType);

        foreach (var index in indices)
        {
            var nextType = GetAggregateElementType(currentType, index)
                ?? throw new UnsupportedBodyEmissionException(
                    $"Cannot extract aggregate leaf for '{rootType.DisplayName}'.");
            var extracted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
            builder.AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
            currentValue = extracted;
            currentType = NormalizeAggregateType(nextType);
        }

        return currentValue;
    }

    private string EmitScalarizedAggregateLeafAddress(
        SsaValue baseAddress,
        StarkTypeSymbol rootType,
        IReadOnlyList<int> indices,
        string purpose)
    {
        return EmitScalarizedAggregateLeafAddress(FormatValue(baseAddress), rootType, indices, purpose);
    }

    private string EmitScalarizedAggregateLeafAddress(
        string baseAddress,
        StarkTypeSymbol rootType,
        IReadOnlyList<int> indices,
        string purpose)
    {
        if (indices.Count == 0)
        {
            return baseAddress;
        }

        var leafAddress = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        var gepIndices = string.Join(", ", indices.Select(static index => $"i32 {index}"));
        AppendLine($"  {leafAddress} = getelementptr{GetProvenInObjectGepFlags()} {MapType(rootType)}, ptr {baseAddress}, i32 0, {gepIndices}");
        return leafAddress;
    }

    private StarkTypeSymbol? GetAggregateElementType(StarkTypeSymbol type, int index)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null => normalizedType.ElementType,
            StarkTypeKind.Named when ResolveNamedTypeSymbol(normalizedType) is { } namedType
                                       && TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields)
                                       && index >= 0
                                       && index < orderedFields.Count
                => orderedFields[index].Type,
            _ => null
        };
    }

    private bool TryGetScalarizableNamedAggregateFields(
        NamedTypeSymbol namedType,
        out IReadOnlyList<FieldSymbol> orderedFields)
    {
        if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            orderedFields = namedType.OrderedFields;
            return true;
        }

        if (namedType.Kind == DeclarationKind.Enum
            && _context.EnumLayouts.TryGetValue(namedType.Name, out var enumLayout))
        {
            orderedFields = enumLayout.OrderedFields;
            return true;
        }

        orderedFields = Array.Empty<FieldSymbol>();
        return false;
    }

    private static StarkTypeSymbol NormalizeAggregateType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static string FormatZeroInitializer(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Integer => "0",
            StarkTypeKind.Float => "0.0",
            StarkTypeKind.Bool => "false",
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer => "null",
            _ => "zeroinitializer"
        };
    }

    private void EmitMakeSliceFromLocal(string result, SsaMakeSliceFromLocalRValue makeSlice)
    {
        EnsureLocalSlotExists(makeSlice.LocalName, makeSlice.SourceType);

        if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray
            || makeSlice.SourceType.ElementType is null
            || makeSlice.SourceType.FixedLength is not int fixedLength)
        {
            throw new UnsupportedBodyEmissionException($"Slice creation from '{makeSlice.SourceType.DisplayName}' is not supported.");
        }

        var slotName = $"%{EscapeIdentifier($"slot_{makeSlice.LocalName}")}";
        var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";

        AppendLine($"  {elementPointer} = getelementptr{GetZeroOffsetGepFlags()} {MapType(makeSlice.SourceType)}, ptr {slotName}, i32 0, i32 0");
        AppendLine($"  {withPointer} = insertvalue {MapType(makeSlice.Type)} zeroinitializer, ptr {elementPointer}, 0");
        AppendLine($"  {result} = insertvalue {MapType(makeSlice.Type)} {withPointer}, i64 {fixedLength}, 1");
    }

    private void EmitMakeSliceFromPointer(string result, SsaMakeSliceFromPointerRValue makeSlice)
    {
        var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";
        AppendLine($"  {withPointer} = insertvalue {MapType(makeSlice.Type)} zeroinitializer, ptr {FormatValue(makeSlice.Pointer)}, 0");
        AppendLine($"  {result} = insertvalue {MapType(makeSlice.Type)} {withPointer}, {MapType(makeSlice.Length.Type)} {FormatValue(makeSlice.Length)}, 1");
    }

    private void EmitLoadSliceElement(
        string result,
        SsaLoadSliceElementRValue loadSlice,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        IReadOnlyList<string>? loopAccessGroups)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";
        var alignmentBytes = TryGetKnownSliceDataAlignmentBytes(
            loadSlice.Slice,
            new HashSet<string>(StringComparer.Ordinal),
            out var sliceAlignmentBytes)
            ? GetLeafAlignmentBytes(sliceAlignmentBytes, loadSlice.Type)
            : null;

        AppendLine($"  {dataPointer} = extractvalue {MapType(loadSlice.Slice.Type)} {FormatValue(loadSlice.Slice)}, 0");
        AppendLine($"  {elementPointer} = getelementptr{GetSliceElementGepFlags(loadSlice.Slice, loadSlice.Index)} {MapType(loadSlice.Type)}, ptr {dataPointer}, {MapType(loadSlice.Index.Type)} {FormatValue(loadSlice.Index)}");
        AppendLine($"  {result} = load {MapType(loadSlice.Type)}, ptr {elementPointer}{GetAlignmentSuffix(alignmentBytes)}{GetInvariantLoadMetadataSuffix(loadSlice.Slice)}{GetValueRangeMetadataSuffix(loadSlice.Type)}{GetSliceElementTbaaMetadataSuffix(loadSlice.Slice, loadSlice.Type)}{GetScopedNoAliasMetadataSuffixForSlice(loadSlice.Slice, scopedNoAliasGroups)}{GetLoopAccessGroupMetadataSuffix(loopAccessGroups)}");
    }

    private void EmitTextSlice(string result, SsaTextSliceRValue textSlice)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var slicedPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";
        var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";
        var unitType = GetTextUnitType(textSlice.TextValue.Type);

        AppendLine($"  {dataPointer} = extractvalue {MapType(textSlice.TextValue.Type)} {FormatValue(textSlice.TextValue)}, 0");
        AppendLine($"  {slicedPointer} = getelementptr{GetTextSliceGepFlags(textSlice.TextValue, textSlice.Start)} {MapType(unitType)}, ptr {dataPointer}, {MapType(textSlice.Start.Type)} {FormatValue(textSlice.Start)}");
        AppendLine($"  {withPointer} = insertvalue {MapType(textSlice.Type)} zeroinitializer, ptr {slicedPointer}, 0");
        AppendLine($"  {result} = insertvalue {MapType(textSlice.Type)} {withPointer}, {MapType(textSlice.Length.Type)} {FormatValue(textSlice.Length)}, 1");
    }

    private void EmitAddressOfLocal(string result, SsaAddressOfLocalRValue addressOfLocal)
    {
        EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
        AppendLine($"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(addressOfLocal.PointeeType)}, ptr %{EscapeIdentifier($"slot_{addressOfLocal.LocalName}")}, i32 0");
    }

    private void EmitAddressOfParameter(string result, SsaAddressOfParameterRValue addressOfParameter)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
        if (parameter is null)
        {
            throw new UnsupportedBodyEmissionException($"Unknown SSA parameter '{addressOfParameter.ParameterName}' for address emission.");
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            AppendLine(
                $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}, i32 0");
            return;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        AppendLine(
            $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier($"slot_param_{parameter.SourceName}")}, i32 0");
    }

    private void EmitFieldAddress(string result, SsaFieldAddressRValue fieldAddress)
    {
        AppendLine($"  {result} = getelementptr{GetProvenInObjectGepFlags()} {MapType(fieldAddress.AggregateType)}, ptr {FormatValue(fieldAddress.Address)}, i32 0, i32 {fieldAddress.FieldIndex}");
    }

    private void EmitElementAddress(string result, SsaElementAddressRValue elementAddress)
    {
        if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray)
        {
            var indexValue = elementAddress.ConstantIndex is int constantIndex
                ? constantIndex.ToString()
                : $"{MapType(elementAddress.Index!.Type)} {FormatValue(elementAddress.Index)}";

            if (elementAddress.ConstantIndex is int fixedArrayConstantIndex)
            {
                AppendLine($"  {result} = getelementptr{GetProvenInObjectGepFlags()} {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, i32 {fixedArrayConstantIndex}");
            }
            else
            {
                var flags = GetFixedArrayIndexGepFlags(elementAddress.Index, elementAddress.AggregateType);
                AppendLine($"  {result} = getelementptr{flags} {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, {indexValue}");
            }

            return;
        }

        if (elementAddress.ConstantIndex is int scalarConstant)
        {
            var flags = scalarConstant == 0 ? GetZeroOffsetGepFlags() : string.Empty;
            AppendLine($"  {result} = getelementptr{flags} {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 {scalarConstant}");
            return;
        }

        if (elementAddress.Index is null)
        {
            throw new UnsupportedBodyEmissionException("Element address is missing its dynamic index.");
        }

        AppendLine($"  {result} = getelementptr{GetUnboundedPointerIndexGepFlags(elementAddress.Index)} {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, {MapType(elementAddress.Index.Type)} {FormatValue(elementAddress.Index)}");
    }

    private void EmitSliceElementAddress(string result, SsaSliceElementAddressRValue sliceElementAddress)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var elementType = sliceElementAddress.Type.ElementType ?? throw new UnsupportedBodyEmissionException("Slice element address requires a raw pointer element type.");

        AppendLine($"  {dataPointer} = extractvalue {MapType(sliceElementAddress.Slice.Type)} {FormatValue(sliceElementAddress.Slice)}, 0");
        AppendLine($"  {result} = getelementptr{GetSliceElementGepFlags(sliceElementAddress.Slice, sliceElementAddress.Index)} {MapType(elementType)}, ptr {dataPointer}, {MapType(sliceElementAddress.Index.Type)} {FormatValue(sliceElementAddress.Index)}");
    }

    private void EmitTerminator(SsaTerminator terminator)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Goto:
                AppendLine($"  br label %{FormatBlockLabel(terminator.Targets[0])}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Branch:
                if (terminator.Condition is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA branch is missing a condition.");
                }

                AppendLine(
                    $"  br i1 {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.Targets[0])}, label %{FormatBlockLabel(terminator.Targets[1])}{GetBranchPredictionMetadataSuffix(terminator)}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Switch:
                if (terminator.Condition is null || terminator.DefaultTarget is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA switch is missing its condition or default target.");
                }

                if (terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                {
                    AppendLine($"  br label %{FormatBlockLabel(terminator.DefaultTarget.Value)}");
                    return;
                }

                var switchCases = string.Join(
                    " ",
                    terminator.SwitchCases.Select(
                        switchCase => $"{MapType(switchCase.MatchValue.Type)} {FormatValue(switchCase.MatchValue)}, label %{FormatBlockLabel(switchCase.TargetBlockId)}"));

                AppendLine(
                    $"  switch {MapType(terminator.Condition.Type)} {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.DefaultTarget.Value)} [ {switchCases} ]{GetBranchPredictionMetadataSuffix(terminator)}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Return:
                if (_abiFunction.ReturnsIndirect)
                {
                    if (terminator.Value is null || _abiFunction.ReturnBufferParameter is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA aggregate return is missing its value or sret parameter.");
                    }

                    EmitValueToAddress(
                        $"%{EscapeIdentifier(_abiFunction.ReturnBufferParameter.LlvmName)}",
                        _function.ReturnType,
                        terminator.Value,
                        GetTypeAlignmentBytes(_function.ReturnType),
                        scopedNoAliasMetadataSuffix: GetScopedNoAliasMetadataSuffix(CreateScopedAliasParameterRootKey(_abiFunction.ReturnBufferParameter.SourceName)));
                    AppendLine("  ret void");
                    return;
                }

                if (_function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    AppendLine("  ret void");
                    return;
                }

                if (terminator.Value is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA return is missing a return value.");
                }

                AppendLine($"  ret {MapType(_abiFunction.LlvmReturnType)} {FormatValue(terminator.Value)}");
                return;
            case SsaTerminatorKind.Unreachable:
                AppendLine($"  call coldcc void @{UnreachableTrapHelperName}()");
                AppendLine("  unreachable");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA terminator '{terminator.Kind}'.");
        }
    }

    private string GetLoopMetadataSuffix(SsaTerminator terminator)
    {
        if (terminator.LoopContracts is not { Count: > 0 } contracts
            || !contracts.Contains("independent", StringComparer.Ordinal))
        {
            return string.Empty;
        }

        var mustProgressRef = _context.GetMetadataTupleRef(["!\"llvm.loop.mustprogress\""]);
        var loopMetadataItems = new List<string> { string.Empty, mustProgressRef };
        if (terminator.LoopAccessGroups is { Count: > 0 } loopAccessGroups)
        {
            var parallelAccessItems = loopAccessGroups
                .Select(GetLoopAccessGroupRef)
                .Prepend("!\"llvm.loop.parallel_accesses\"")
                .ToArray();
            loopMetadataItems.Add(_context.GetMetadataTupleRef(parallelAccessItems));
        }

        var currentBlockId = _currentBlock?.Id.ToString(CultureInfo.InvariantCulture) ?? "?";
        var key = string.Join(
            "|",
            _abiFunction.SymbolName,
            currentBlockId,
            string.Join(",", terminator.Targets),
            string.Join(",", contracts.Order(StringComparer.Ordinal)),
            string.Join(",", terminator.LoopAccessGroups ?? []));
        var loopRef = _context.GetSelfReferentialMetadataRef(
            $"loop:{key}",
            selfRef =>
            {
                loopMetadataItems[0] = selfRef;
                return $"distinct !{{{string.Join(", ", loopMetadataItems)}}}";
            });
        return $", !llvm.loop {loopRef}";
    }

    private string GetLoopAccessGroupMetadataSuffix(IReadOnlyList<string>? loopAccessGroups)
    {
        if (loopAccessGroups is not { Count: > 0 })
        {
            return string.Empty;
        }

        var accessGroupRefs = loopAccessGroups
            .Select(GetLoopAccessGroupRef)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var accessGroupRef = accessGroupRefs.Length == 1
            ? accessGroupRefs[0]
            : _context.GetMetadataTupleRef(accessGroupRefs);
        return $", !llvm.access.group {accessGroupRef}";
    }

    private string GetLoopAccessGroupRef(string accessGroupId)
    {
        return _context.GetSelfReferentialMetadataRef(
            $"loop-access-group:{_abiFunction.SymbolName}:{accessGroupId}",
            _ => "distinct !{}");
    }

    private string GetBranchPredictionMetadataSuffix(SsaTerminator terminator)
    {
        var weights = GetBranchPredictionWeights(terminator);
        if (weights is null || weights.Count < 2)
        {
            return string.Empty;
        }

        var items = weights
            .Select(static weight => $"i32 {Math.Max(1, weight)}")
            .Prepend("!\"branch_weights\"")
            .ToArray();
        return $", !prof {_context.GetMetadataTupleRef(items)}";
    }

    private IReadOnlyList<int>? GetBranchPredictionWeights(SsaTerminator terminator)
    {
        return terminator.Kind switch
        {
            SsaTerminatorKind.Branch => GetBranchWeights(terminator),
            SsaTerminatorKind.Switch => GetSwitchWeights(terminator),
            _ => null
        };
    }

    private IReadOnlyList<int>? GetBranchWeights(SsaTerminator terminator)
    {
        if (terminator.BranchWeights is { Count: 2 } explicitWeights)
        {
            return explicitWeights;
        }

        if (terminator.Targets.Count != 2)
        {
            return null;
        }

        var trueTargetIsCold = IsColdBranchTarget(terminator.Targets[0]);
        var falseTargetIsCold = IsColdBranchTarget(terminator.Targets[1]);
        if (trueTargetIsCold == falseTargetIsCold)
        {
            var trueTargetIsHot = IsHotBranchTarget(terminator.Targets[0]);
            var falseTargetIsHot = IsHotBranchTarget(terminator.Targets[1]);
            if (trueTargetIsHot == falseTargetIsHot)
            {
                return null;
            }

            return trueTargetIsHot
                ? [HotIntentLikelyWeight, ExplicitIntentNeutralWeight]
                : [ExplicitIntentNeutralWeight, HotIntentLikelyWeight];
        }

        return trueTargetIsCold
            ? [TrapEdgeUnlikelyWeight, NormalEdgeLikelyWeight]
            : [NormalEdgeLikelyWeight, TrapEdgeUnlikelyWeight];
    }

    private IReadOnlyList<int>? GetSwitchWeights(SsaTerminator terminator)
    {
        if (terminator.SwitchCases is not { Count: > 0 } switchCases || terminator.DefaultTarget is null)
        {
            return null;
        }

        var expectedWeightCount = switchCases.Count + 1;
        if (terminator.BranchWeights is { } explicitWeights && explicitWeights.Count == expectedWeightCount)
        {
            return explicitWeights;
        }

        var weights = new int[expectedWeightCount];
        var sawNonNeutralWeight = false;

        AssignInferredWeight(0, terminator.DefaultTarget.Value);
        for (var index = 0; index < switchCases.Count; index++)
        {
            AssignInferredWeight(index + 1, switchCases[index].TargetBlockId);
        }

        return sawNonNeutralWeight ? weights : null;

        void AssignInferredWeight(int index, int targetBlockId)
        {
            weights[index] = IsColdBranchTarget(targetBlockId)
                ? TrapEdgeUnlikelyWeight
                : IsHotBranchTarget(targetBlockId)
                    ? HotIntentLikelyWeight
                    : ExplicitIntentNeutralWeight;
            sawNonNeutralWeight |= weights[index] != ExplicitIntentNeutralWeight;
        }
    }

    private bool IsColdBranchTarget(int targetBlockId)
    {
        return IsColdBranchTarget(targetBlockId, new HashSet<int>());
    }

    private bool IsColdBranchTarget(int targetBlockId, HashSet<int> visited)
    {
        if (!_blocksById.TryGetValue(targetBlockId, out var block) || !visited.Add(targetBlockId))
        {
            return false;
        }

        if (block.Terminator.Kind == SsaTerminatorKind.Unreachable)
        {
            return true;
        }

        if (ContainsCallWithTemperature(block, isCold: true))
        {
            return true;
        }

        if (block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Goto
            && block.Terminator.Targets.Count == 1)
        {
            return IsColdBranchTarget(block.Terminator.Targets[0], visited);
        }

        return false;
    }

    private bool IsHotBranchTarget(int targetBlockId)
    {
        return IsHotBranchTarget(targetBlockId, new HashSet<int>());
    }

    private bool IsHotBranchTarget(int targetBlockId, HashSet<int> visited)
    {
        if (!_blocksById.TryGetValue(targetBlockId, out var block) || !visited.Add(targetBlockId))
        {
            return false;
        }

        if (ContainsCallWithTemperature(block, isCold: false))
        {
            return true;
        }

        if (block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Goto
            && block.Terminator.Targets.Count == 1)
        {
            return IsHotBranchTarget(block.Terminator.Targets[0], visited);
        }

        return false;
    }

    private bool ContainsCallWithTemperature(SsaBasicBlock block, bool isCold)
    {
        foreach (var instruction in block.Instructions)
        {
            if (instruction is not SsaValueInstruction { Value: SsaCallRValue call })
            {
                continue;
            }

            var effects = _context.TryGetFunctionEffects(call.FunctionName);
            if (effects is null)
            {
                continue;
            }

            if (isCold ? effects.IsCold : effects.IsHot)
            {
                return true;
            }
        }

        return false;
    }

    private void EmitFallbackTerminal()
    {
        if (_abiFunction.ReturnsIndirect || _function.ReturnType.Kind == StarkTypeKind.Void)
        {
            AppendLine("  ret void");
            return;
        }

        throw new UnsupportedBodyEmissionException("SSA function body has no blocks.");
    }

    private static string FormatBlockLabel(int blockId) => $"bb{blockId}";

    private string FormatPhiIncomingBlockLabel(int blockId)
    {
        return _blockExitLabels.TryGetValue(blockId, out var label)
            ? label
            : FormatBlockLabel(blockId);
    }

    private string FormatValue(SsaValue value)
    {
        return value switch
        {
            SsaValueReference reference => FormatValueReference(reference),
            SsaIntegerConstant integer => integer.Value.ToString(),
            SsaFloatConstant floating => FormatFloatLiteral(floating),
            SsaStringConstant text => FormatStringConstantValue(text),
            SsaBoolConstant boolean => boolean.Value ? "true" : "false",
            SsaNullConstant => "null",
            SsaGlobalAddressValue globalAddress => $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}",
            SsaFunctionAddressValue functionAddress => $"@{EscapeIdentifier(functionAddress.FunctionName)}",
            SsaZeroInitializerValue => "zeroinitializer",
            SsaUndefValue => "undef",
            _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
        };
    }

    private string GetInvariantLoadMetadataSuffix(string globalName)
    {
        return IsImmutableGlobalName(globalName)
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLoadMetadataSuffixForAggregateSource(SsaValue value)
    {
        return IsImmutableAggregateSource(value, new HashSet<string>(StringComparer.Ordinal))
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLoadMetadataSuffix(SsaValue address)
    {
        return IsImmutableMemoryReference(address, new HashSet<string>(StringComparer.Ordinal))
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLocalLoadMetadataSuffix(string localName)
    {
        return _invariantLocalNames.Contains(localName)
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetValueRangeMetadataSuffix(StarkTypeSymbol type)
    {
        return _context.GetValueRangeMetadataRef(type) is { } rangeMetadataRef
            ? $", !range {rangeMetadataRef}"
            : string.Empty;
    }

    private string GetValueRangeMetadataSuffix(string valueName, StarkTypeSymbol type)
    {
        if (_valueFacts.TryGetValue(valueName, out var facts)
            && facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } range
            && _context.GetValueRangeMetadataRef(type, range) is { } factRangeMetadataRef)
        {
            return $", !range {factRangeMetadataRef}";
        }

        return GetValueRangeMetadataSuffix(type);
    }

    private ScopedNoAliasMetadataModel? BuildScopedNoAliasMetadata(
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        var roots = new Dictionary<string, ScopedNoAliasRoot>(StringComparer.Ordinal);
        foreach (var parameter in _abiFunction.Parameters)
        {
            if (TryCreateScopedNoAliasParameterRoot(parameter, parameterEffects, out var root))
            {
                roots.TryAdd(root.Key, root);
            }
        }

        foreach (var root in CollectFreshResultScopedNoAliasRoots())
        {
            roots.TryAdd(root.Key, root);
        }

        if (roots.Count == 0)
        {
            return null;
        }

        var domainKey = $"function:{_abiFunction.SymbolName}";
        var domainRef = _context.GetAliasScopeDomainRef(domainKey, $"stark.noalias.{_abiFunction.SymbolName}");
        var scopeRefs = roots.Values.ToDictionary(
            static root => root.Key,
            root => _context.GetAliasScopeRef(
                $"{domainKey}:{root.Key}",
                domainRef,
                $"stark.noalias.{_abiFunction.SymbolName}.{root.DisplayName}"),
            StringComparer.Ordinal);

        var accessMetadata = new Dictionary<string, ScopedNoAliasAccessMetadata>(StringComparer.Ordinal);
        foreach (var root in roots.Values)
        {
            var aliasScopeListRef = _context.GetMetadataTupleRef([scopeRefs[root.Key]]);
            var noAliasScopeRefs = scopeRefs
                .Where(scope => !string.Equals(scope.Key, root.Key, StringComparison.Ordinal))
                .Select(static scope => scope.Value)
                .ToArray();
            var noAliasListRef = noAliasScopeRefs.Length == 0
                ? null
                : _context.GetMetadataTupleRef(noAliasScopeRefs);
            accessMetadata[root.Key] = new ScopedNoAliasAccessMetadata(aliasScopeListRef, noAliasListRef);
        }

        return new ScopedNoAliasMetadataModel(accessMetadata, scopeRefs);
    }

    private bool TryCreateScopedNoAliasParameterRoot(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out ScopedNoAliasRoot root)
    {
        root = default;

        var rootKey = CreateScopedAliasParameterRootKey(parameter.SourceName);
        if (_scopedNoAliasUnsafeAddressRoots.Contains(rootKey)
            || !IsScopedNoAliasParameter(parameter, parameterEffects))
        {
            return false;
        }

        var displayName = parameter.Kind == AbiParameterKind.SRet
            ? "sret"
            : $"param.{parameter.SourceName}";
        root = new ScopedNoAliasRoot(rootKey, displayName);
        return true;
    }

    private bool IsScopedNoAliasParameter(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (parameter.Kind == AbiParameterKind.SRet)
        {
            return true;
        }

        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            return TryGetParameterEffect(parameter, parameterEffects, out var directEffects)
                && directEffects.IsMemoryBacked
                && directEffects.GuaranteedNoAlias;
        }

        return parameter.SourceType.InitializationKind != StarkInitializationKind.None
            || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && parameter.SourceType.IsMutableView)
            || AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
            || (TryGetParameterEffect(parameter, parameterEffects, out var indirectEffects)
                && indirectEffects.IsMemoryBacked
                && indirectEffects.GuaranteedNoAlias);
    }

    private static bool TryGetParameterEffect(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out ParameterMemoryEffectSummary effects)
    {
        if (parameterEffects is not null
            && (parameterEffects.TryGetValue(parameter.SourceName, out effects!)
                || parameterEffects.TryGetValue(parameter.LlvmName, out effects!)))
        {
            return true;
        }

        effects = default!;
        return false;
    }

    private IReadOnlyList<ScopedNoAliasRoot> CollectFreshResultScopedNoAliasRoots()
    {
        var roots = new List<ScopedNoAliasRoot>();
        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not SsaValueInstruction { Value: SsaCallRValue call } valueInstruction)
                {
                    continue;
                }

                var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
                if (abiCallee?.ReturnsIndirect == true)
                {
                    roots.Add(new ScopedNoAliasRoot(
                        CreateScopedAliasFreshResultRootKey(valueInstruction.ResultName),
                        $"fresh.{valueInstruction.ResultName}"));
                }
            }
        }

        return roots;
    }

    private string GetScopedNoAliasMetadataSuffix(
        SsaValue address,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups = null)
    {
        if (TryResolveScopedNoAliasRoot(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var runtimeRootKey,
                BuildScopedNoAliasRootSet(scopedNoAliasGroups))
            && GetRuntimeScopedNoAliasMetadataSuffix(runtimeRootKey, scopedNoAliasGroups) is { Length: > 0 } runtimeSuffix)
        {
            return runtimeSuffix;
        }

        return TryResolveScopedNoAliasRoot(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var rootKey)
            ? GetScopedNoAliasMetadataSuffix(rootKey)
            : string.Empty;
    }

    private string GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(params SsaValue?[] accessedRoots)
    {
        if (_scopedNoAliasMetadata is null)
        {
            return string.Empty;
        }

        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in accessedRoots)
        {
            if (root is not null
                && TryResolveScopedNoAliasRoot(
                    root,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var rootKey))
            {
                rootKeys.Add(rootKey);
            }
        }

        if (rootKeys.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeRefs = rootKeys
            .Select(rootKey => _scopedNoAliasMetadata.ScopeRefs.TryGetValue(rootKey, out var scopeRef) ? scopeRef : null)
            .Where(static scopeRef => scopeRef is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (aliasScopeRefs.Length == 0)
        {
            return string.Empty;
        }

        var suffix = $", !alias.scope {_context.GetMetadataTupleRef(aliasScopeRefs)}";
        var noAliasScopeRefs = _scopedNoAliasMetadata.ScopeRefs
            .Where(scope => !rootKeys.Contains(scope.Key))
            .Select(static scope => scope.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (noAliasScopeRefs.Length != 0)
        {
            suffix += $", !noalias {_context.GetMetadataTupleRef(noAliasScopeRefs)}";
        }

        return suffix;
    }

    private string GetScopedNoAliasMetadataSuffixForSlice(
        SsaValue slice,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups = null)
    {
        if (TryResolveScopedNoAliasSliceRoot(
                slice,
                new HashSet<string>(StringComparer.Ordinal),
                out var runtimeRootKey,
                BuildScopedNoAliasRootSet(scopedNoAliasGroups))
            && GetRuntimeScopedNoAliasMetadataSuffix(runtimeRootKey, scopedNoAliasGroups) is { Length: > 0 } runtimeSuffix)
        {
            return runtimeSuffix;
        }

        return TryResolveScopedNoAliasSliceRoot(
                slice,
                new HashSet<string>(StringComparer.Ordinal),
                out var rootKey)
            ? GetScopedNoAliasMetadataSuffix(rootKey)
            : string.Empty;
    }

    private string GetScopedNoAliasMetadataSuffix(string rootKey)
    {
        if (_scopedNoAliasMetadata is null
            || !_scopedNoAliasMetadata.Accesses.TryGetValue(rootKey, out var metadata)
            || metadata.NoAliasListRef is null)
        {
            return string.Empty;
        }

        return $", !alias.scope {metadata.AliasScopeListRef}, !noalias {metadata.NoAliasListRef}";
    }

    private string GetRuntimeScopedNoAliasMetadataSuffix(
        string rootKey,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups)
    {
        if (scopedNoAliasGroups is null || scopedNoAliasGroups.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeRefs = new List<string>();
        var noAliasScopeRefs = new List<string>();
        foreach (var group in scopedNoAliasGroups)
        {
            var rootKeys = group.RootKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rootKeys.Length < 2 || !rootKeys.Contains(rootKey, StringComparer.Ordinal))
            {
                continue;
            }

            var domainKey = $"runtime-disjoint:{_abiFunction.SymbolName}:{group.ScopeId}";
            var domainRef = _context.GetAliasScopeDomainRef(
                domainKey,
                $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}");
            foreach (var candidateRootKey in rootKeys)
            {
                var scopeRef = _context.GetAliasScopeRef(
                    $"{domainKey}:{candidateRootKey}",
                    domainRef,
                    $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}.{FormatScopedNoAliasRootDisplayName(candidateRootKey)}");
                if (string.Equals(candidateRootKey, rootKey, StringComparison.Ordinal))
                {
                    aliasScopeRefs.Add(scopeRef);
                }
                else
                {
                    noAliasScopeRefs.Add(scopeRef);
                }
            }
        }

        if (aliasScopeRefs.Count == 0 || noAliasScopeRefs.Count == 0)
        {
            return string.Empty;
        }

        var aliasScopeListRef = _context.GetMetadataTupleRef(aliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray());
        var noAliasListRef = _context.GetMetadataTupleRef(noAliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray());
        return $", !alias.scope {aliasScopeListRef}, !noalias {noAliasListRef}";
    }

    private static ISet<string>? BuildScopedNoAliasRootSet(IReadOnlyList<ScopedNoAliasGroup>? groups)
    {
        return groups is null || groups.Count == 0
            ? null
            : groups
                .SelectMany(static group => group.RootKeys)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
    }

    private static string FormatScopedNoAliasRootDisplayName(string rootKey)
    {
        return rootKey.Replace(':', '.');
    }

    private bool TryResolveScopedNoAliasRoot(
        SsaValue address,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedNoAliasRoot(definition, visitedValueNames, out rootKey, allowedRootKeys);
                }

                return TryResolveScopedNoAliasParameterReference(reference.Name, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasRoot(
        SsaRValue address,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveScopedNoAliasRoot(use.Value, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaAddressOfParameterRValue addressOfParameter:
                return TryUseScopedNoAliasRoot(
                    CreateScopedAliasParameterRootKey(addressOfParameter.ParameterName),
                    out rootKey,
                    allowedRootKeys);
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveScopedNoAliasRoot(fieldAddress.Address, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaElementAddressRValue elementAddress:
                return TryResolveScopedNoAliasRoot(elementAddress.Address, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryResolveScopedNoAliasSliceRoot(sliceElementAddress.Slice, visitedValueNames, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasSliceRoot(
        SsaValue slice,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (slice)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    rootKey = string.Empty;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveScopedNoAliasSliceRoot(definition, visitedValueNames, out rootKey, allowedRootKeys);
                }

                return TryResolveScopedNoAliasParameterReference(reference.Name, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasSliceRoot(
        SsaRValue slice,
        ISet<string> visitedValueNames,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        switch (slice)
        {
            case SsaUseRValue use:
                return TryResolveScopedNoAliasSliceRoot(use.Value, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaMakeSliceFromPointerRValue makeSlice:
                return TryResolveScopedNoAliasRoot(makeSlice.Pointer, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaLoadLocalRValue loadLocal
                when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue):
                return TryResolveScopedNoAliasSliceRoot(storedValue, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaTextSliceRValue textSlice:
                return TryResolveScopedNoAliasSliceRoot(textSlice.TextValue, visitedValueNames, out rootKey, allowedRootKeys);
            case SsaLoadIndirectRValue loadIndirect:
                return TryResolveScopedNoAliasRoot(loadIndirect.Address, visitedValueNames, out rootKey, allowedRootKeys);
            default:
                rootKey = string.Empty;
                return false;
        }
    }

    private bool TryResolveScopedNoAliasParameterReference(
        string parameterName,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        var parameter = _abiFunction.Parameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, parameterName, StringComparison.Ordinal)
                || string.Equals(candidate.LlvmName, parameterName, StringComparison.Ordinal));
        if (parameter is not null)
        {
            return TryUseScopedNoAliasRoot(CreateScopedAliasParameterRootKey(parameter.SourceName), out rootKey, allowedRootKeys);
        }

        if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
        {
            return TryUseScopedNoAliasRoot(CreateScopedAliasParameterRootKey(parameterName[4..]), out rootKey, allowedRootKeys);
        }

        rootKey = string.Empty;
        return false;
    }

    private bool TryUseScopedNoAliasRoot(
        string candidateRootKey,
        out string rootKey,
        ISet<string>? allowedRootKeys = null)
    {
        if (allowedRootKeys?.Contains(candidateRootKey) == true
            || _scopedNoAliasMetadata is not null
                && _scopedNoAliasMetadata.Accesses.ContainsKey(candidateRootKey))
        {
            rootKey = candidateRootKey;
            return true;
        }

        rootKey = string.Empty;
        return false;
    }

    private string GetDirectTbaaMetadataSuffix(string rootKey, StarkTypeSymbol accessType)
    {
        if (_tbaaUnsafeAddressRoots.Contains(rootKey) || !CanUseTbaaAsAccessType(accessType))
        {
            return string.Empty;
        }

        return $", !tbaa {GetSimpleTbaaAccessTagRef(accessType)}";
    }

    private string GetTbaaMetadataSuffix(SsaValue address, StarkTypeSymbol accessType)
    {
        if (!CanUseTbaaAsAccessType(accessType)
            || !TryResolveTbaaAddressAccess(
                address,
                new HashSet<string>(StringComparer.Ordinal),
                out var access))
        {
            return string.Empty;
        }

        var tagRef = access.UseStructPath
            ? GetStructPathTbaaAccessTagRef(access.RootType, accessType, access.OffsetBytes)
            : GetSimpleTbaaAccessTagRef(accessType);
        return $", !tbaa {tagRef}";
    }

    private string GetSliceElementTbaaMetadataSuffix(SsaValue slice, StarkTypeSymbol elementType)
    {
        if (!CanUseTbaaAsAccessType(elementType)
            || !IsTbaaSafeSliceValue(slice, new HashSet<string>(StringComparer.Ordinal)))
        {
            return string.Empty;
        }

        return $", !tbaa {GetSimpleTbaaAccessTagRef(elementType)}";
    }

    private string GetSimpleTbaaAccessTagRef(StarkTypeSymbol accessType)
    {
        var accessDescriptorRef = GetTbaaTypeDescriptorRef(accessType);
        return _context.GetTbaaAccessTagRef(accessDescriptorRef, accessDescriptorRef, 0);
    }

    private string GetStructPathTbaaAccessTagRef(StarkTypeSymbol rootType, StarkTypeSymbol accessType, long offsetBytes)
    {
        var rootDescriptorRef = GetTbaaTypeDescriptorRef(rootType);
        var accessDescriptorRef = GetTbaaTypeDescriptorRef(accessType);
        return _context.GetTbaaAccessTagRef(rootDescriptorRef, accessDescriptorRef, offsetBytes);
    }

    private string GetTbaaTypeDescriptorRef(StarkTypeSymbol type)
    {
        return GetTbaaTypeDescriptorRef(type, new HashSet<string>(StringComparer.Ordinal));
    }

    private string GetTbaaTypeDescriptorRef(StarkTypeSymbol type, ISet<string> activeTypeKeys)
    {
        var normalizedType = NormalizeAggregateType(type);
        var key = GetTbaaTypeKey(normalizedType);
        var displayName = GetTbaaTypeDisplayName(normalizedType);

        if (!activeTypeKeys.Add(key))
        {
            return _context.GetTbaaTypeDescriptorRef(key, displayName);
        }

        try
        {
            switch (normalizedType.Kind)
            {
                case StarkTypeKind.FixedArray
                    when normalizedType.ElementType is not null
                         && normalizedType.FixedLength is int fixedLength
                         && fixedLength is > 0 and <= TbaaFixedArrayFieldLimit
                         && TryGetConcreteTypeLayout(normalizedType.ElementType) is { } elementLayout:
                {
                    var elementDescriptorRef = GetTbaaTypeDescriptorRef(normalizedType.ElementType, activeTypeKeys);
                    var fields = Enumerable.Range(0, fixedLength)
                        .Select(index => (elementDescriptorRef, OffsetBytes: (long)index * elementLayout.SizeBytes))
                        .ToArray();
                    return _context.GetTbaaStructTypeDescriptorRef(key, displayName, fields);
                }
                case StarkTypeKind.Slice:
                case StarkTypeKind.Ascii:
                case StarkTypeKind.Unicode:
                    if (TryBuildViewTbaaFields(normalizedType, activeTypeKeys, out var viewFields))
                    {
                        return _context.GetTbaaStructTypeDescriptorRef(key, displayName, viewFields);
                    }

                    break;
                case StarkTypeKind.Named:
                    if (TryBuildNamedAggregateTbaaFields(normalizedType, activeTypeKeys, out var aggregateFields))
                    {
                        return _context.GetTbaaStructTypeDescriptorRef(key, displayName, aggregateFields);
                    }

                    break;
            }

            return _context.GetTbaaTypeDescriptorRef(key, displayName);
        }
        finally
        {
            activeTypeKeys.Remove(key);
        }
    }

    private bool TryBuildViewTbaaFields(
        StarkTypeSymbol viewType,
        ISet<string> activeTypeKeys,
        out IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)> fields)
    {
        fields = Array.Empty<(string TypeDescriptorRef, long OffsetBytes)>();

        var elementType = viewType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => viewType.ElementType
        };
        if (elementType is null)
        {
            return false;
        }

        var pointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: false);
        var lengthType = StarkTypeSymbols.Integer(64);
        var pointerLayout = TryGetConcreteTypeLayout(pointerType);
        var lengthLayout = TryGetConcreteTypeLayout(lengthType);
        if (pointerLayout is null || lengthLayout is null)
        {
            return false;
        }

        var lengthOffset = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        fields =
        [
            (GetTbaaTypeDescriptorRef(pointerType, activeTypeKeys), 0L),
            (GetTbaaTypeDescriptorRef(lengthType, activeTypeKeys), (long)lengthOffset)
        ];
        return true;
    }

    private bool TryBuildNamedAggregateTbaaFields(
        StarkTypeSymbol aggregateType,
        ISet<string> activeTypeKeys,
        out IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)> fields)
    {
        fields = Array.Empty<(string TypeDescriptorRef, long OffsetBytes)>();

        var namedType = ResolveNamedTypeSymbol(aggregateType);
        if (namedType is null || !TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields))
        {
            return false;
        }

        var sizeBytes = 0;
        var collectedFields = new List<(string TypeDescriptorRef, long OffsetBytes)>(orderedFields.Count);
        foreach (var field in orderedFields)
        {
            var fieldLayout = TryGetConcreteTypeLayout(field.Type);
            if (fieldLayout is null)
            {
                return false;
            }

            var offsetBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
            collectedFields.Add((GetTbaaTypeDescriptorRef(field.Type, activeTypeKeys), offsetBytes));
            sizeBytes = checked(offsetBytes + fieldLayout.SizeBytes);
        }

        fields = collectedFields;
        return true;
    }

    private bool TryResolveTbaaAddressAccess(
        SsaValue address,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    access = default;
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveTbaaAddressAccess(definition, visitedValueNames, out access);
                }

                return TryResolveTbaaParameterReference(reference, out access);
            case SsaGlobalAddressValue globalAddress:
                return TryCreateTbaaRootAccess(
                    CreateTbaaGlobalRootKey(globalAddress.GlobalName),
                    globalAddress.PointeeType,
                    out access);
            default:
                access = default;
                return false;
        }
    }

    private bool TryResolveTbaaAddressAccess(
        SsaRValue address,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveTbaaAddressAccess(use.Value, visitedValueNames, out access);
            case SsaAddressOfLocalRValue addressOfLocal:
                return TryCreateTbaaRootAccess(
                    CreateTbaaLocalRootKey(addressOfLocal.LocalName),
                    addressOfLocal.PointeeType,
                    out access);
            case SsaAddressOfParameterRValue addressOfParameter:
                return TryCreateTbaaRootAccess(
                    CreateTbaaParameterRootKey(addressOfParameter.ParameterName),
                    addressOfParameter.PointeeType,
                    out access);
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveTbaaAggregateElementAccess(
                    fieldAddress.Address,
                    fieldAddress.AggregateType,
                    fieldAddress.FieldIndex,
                    visitedValueNames,
                    out access);
            case SsaElementAddressRValue { AggregateType.Kind: StarkTypeKind.FixedArray } elementAddress
                when elementAddress.ConstantIndex is int constantIndex:
                return TryResolveTbaaAggregateElementAccess(
                    elementAddress.Address,
                    elementAddress.AggregateType,
                    constantIndex,
                    visitedValueNames,
                    out access);
            case SsaElementAddressRValue { AggregateType.Kind: StarkTypeKind.FixedArray } elementAddress:
            {
                if (!TryResolveTbaaAddressAccess(elementAddress.Address, visitedValueNames, out var baseAccess)
                    || elementAddress.AggregateType.ElementType is null
                    || !CanEmitTbaaForType(elementAddress.AggregateType.ElementType))
                {
                    access = default;
                    return false;
                }

                access = new TbaaAddressAccess(elementAddress.AggregateType.ElementType, 0, UseStructPath: false);
                return true;
            }
            case SsaSliceElementAddressRValue sliceElementAddress:
            {
                if (!IsTbaaSafeSliceValue(sliceElementAddress.Slice, visitedValueNames)
                    || sliceElementAddress.Type.ElementType is not { } elementType
                    || !CanEmitTbaaForType(elementType))
                {
                    access = default;
                    return false;
                }

                access = new TbaaAddressAccess(elementType, 0, UseStructPath: false);
                return true;
            }
            default:
                access = default;
                return false;
        }
    }

    private bool TryResolveTbaaAggregateElementAccess(
        SsaValue aggregateAddress,
        StarkTypeSymbol aggregateType,
        int elementIndex,
        ISet<string> visitedValueNames,
        out TbaaAddressAccess access)
    {
        if (!TryResolveTbaaAddressAccess(aggregateAddress, visitedValueNames, out var baseAccess)
            || !TryGetAggregateElementOffsetBytes(aggregateType, elementIndex, out var elementType, out var elementOffsetBytes))
        {
            access = default;
            return false;
        }

        if (!CanUseStructPathTbaaForAggregateElement(aggregateType))
        {
            access = new TbaaAddressAccess(elementType, 0, UseStructPath: false);
            return true;
        }

        access = new TbaaAddressAccess(
            baseAccess.RootType,
            checked(baseAccess.OffsetBytes + elementOffsetBytes),
            UseStructPath: true);
        return true;
    }

    private bool TryResolveTbaaParameterReference(SsaValueReference reference, out TbaaAddressAccess access)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
        if (parameter is null
            || parameter.Kind != AbiParameterKind.IndirectIn
            || parameter.SourceType.Kind == StarkTypeKind.RawPointer)
        {
            access = default;
            return false;
        }

        return TryCreateTbaaRootAccess(
            CreateTbaaParameterRootKey(parameter.SourceName),
            parameter.SourceType,
            out access);
    }

    private bool TryCreateTbaaRootAccess(string rootKey, StarkTypeSymbol rootType, out TbaaAddressAccess access)
    {
        if (_tbaaUnsafeAddressRoots.Contains(rootKey) || !CanEmitTbaaForType(rootType))
        {
            access = default;
            return false;
        }

        access = new TbaaAddressAccess(rootType, 0, UseStructPath: false);
        return true;
    }

    private bool TryGetAggregateElementOffsetBytes(
        StarkTypeSymbol aggregateType,
        int elementIndex,
        out StarkTypeSymbol elementType,
        out long offsetBytes)
    {
        elementType = StarkTypeSymbols.Error;
        offsetBytes = 0;

        var normalizedType = NormalizeAggregateType(aggregateType);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.FixedArray
                when elementIndex >= 0
                     && normalizedType.ElementType is not null
                     && normalizedType.FixedLength is int fixedLength
                     && elementIndex < fixedLength
                     && TryGetConcreteTypeLayout(normalizedType.ElementType) is { } elementLayout:
                elementType = normalizedType.ElementType;
                offsetBytes = (long)elementIndex * elementLayout.SizeBytes;
                return true;
            case StarkTypeKind.Named
                when ResolveNamedTypeSymbol(normalizedType) is { } namedType
                     && TryGetScalarizableNamedAggregateFields(namedType, out var orderedFields)
                     && elementIndex >= 0
                     && elementIndex < orderedFields.Count:
            {
                var sizeBytes = 0;
                for (var index = 0; index <= elementIndex; index++)
                {
                    var field = orderedFields[index];
                    var fieldLayout = TryGetConcreteTypeLayout(field.Type);
                    if (fieldLayout is null)
                    {
                        return false;
                    }

                    var fieldOffsetBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                    if (index == elementIndex)
                    {
                        elementType = field.Type;
                        offsetBytes = fieldOffsetBytes;
                        return true;
                    }

                    sizeBytes = checked(fieldOffsetBytes + fieldLayout.SizeBytes);
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static bool CanUseStructPathTbaaForAggregateElement(StarkTypeSymbol aggregateType)
    {
        var normalizedType = NormalizeAggregateType(aggregateType);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray => normalizedType.FixedLength is >= 0 and <= TbaaFixedArrayFieldLimit,
            StarkTypeKind.Named => true,
            _ => false
        };
    }

    private bool IsTbaaSafeSliceValue(SsaValue slice, ISet<string> visitedValueNames)
    {
        switch (slice)
        {
            case SsaStringConstant:
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name))
                {
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return IsTbaaSafeSliceValue(definition, visitedValueNames);
                }

                var parameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                        || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
                return parameter is { SourceType.Kind: StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode }
                    && !_tbaaUnsafeAddressRoots.Contains(CreateTbaaParameterRootKey(parameter.SourceName));
            default:
                return false;
        }
    }

    private bool IsTbaaSafeSliceValue(SsaRValue slice, ISet<string> visitedValueNames)
    {
        return slice switch
        {
            SsaUseRValue use => IsTbaaSafeSliceValue(use.Value, visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice
                => !_tbaaUnsafeAddressRoots.Contains(CreateTbaaLocalRootKey(makeSlice.LocalName)),
            SsaMakeSliceFromPointerRValue => false,
            SsaTextSliceRValue textSlice => IsTbaaSafeSliceValue(textSlice.TextValue, visitedValueNames),
            _ => false
        };
    }

    private static bool CanEmitTbaaForType(StarkTypeSymbol type)
    {
        return NormalizeAggregateType(type).Kind is
            StarkTypeKind.Bool
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.FixedArray
            or StarkTypeKind.Slice
            or StarkTypeKind.Named;
    }

    private static bool CanUseTbaaAsAccessType(StarkTypeSymbol type)
    {
        return NormalizeAggregateType(type).Kind is
            StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static string GetTbaaTypeKey(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => "bool",
            StarkTypeKind.Integer => $"integer:{normalizedType.BitWidth}",
            StarkTypeKind.Float => $"float:{normalizedType.BitWidth}",
            StarkTypeKind.RawPointer => "rawptr",
            StarkTypeKind.FunctionPointer => "fnptr",
            StarkTypeKind.Ascii => "text:ascii",
            StarkTypeKind.Unicode => "text:unicode",
            StarkTypeKind.FixedArray => $"array:{normalizedType.FixedLength}:{GetTbaaTypeKey(normalizedType.ElementType ?? StarkTypeSymbols.Error)}",
            StarkTypeKind.Slice => $"slice:{GetTbaaTypeKey(normalizedType.ElementType ?? StarkTypeSymbols.Error)}",
            StarkTypeKind.Named => $"named:{normalizedType.NamedType ?? normalizedType.DisplayName}",
            _ => normalizedType.DisplayName
        };
    }

    private static string GetTbaaTypeDisplayName(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => "stark.bool",
            StarkTypeKind.Integer => $"stark.i{normalizedType.BitWidth}",
            StarkTypeKind.Float => $"stark.f{normalizedType.BitWidth}",
            StarkTypeKind.RawPointer => "stark.ptr",
            StarkTypeKind.FunctionPointer => "stark.fnptr",
            StarkTypeKind.Ascii => "stark.text.ascii",
            StarkTypeKind.Unicode => "stark.text.unicode",
            StarkTypeKind.FixedArray => $"stark.array.{normalizedType.DisplayName}",
            StarkTypeKind.Slice => $"stark.slice.{normalizedType.DisplayName}",
            StarkTypeKind.Named => $"stark.{normalizedType.NamedType ?? normalizedType.DisplayName}",
            _ => $"stark.{normalizedType.DisplayName}"
        };
    }

    private static string CreateTbaaLocalRootKey(string localName) => $"local:{localName}";

    private static string CreateTbaaParameterRootKey(string parameterName) => $"param:{parameterName}";

    private static string CreateTbaaGlobalRootKey(string globalName) => $"global:{globalName}";

    private static string CreateScopedAliasParameterRootKey(string parameterName) => $"param:{parameterName}";

    private static string CreateScopedAliasFreshResultRootKey(string resultName) => $"fresh-result:{resultName}";

    private bool IsImmutableMemoryReference(SsaValue value, ISet<string> visitedValueNames)
    {
        if (HasConstMemoryProvenance(value, new HashSet<string>(StringComparer.Ordinal)))
        {
            return true;
        }

        return value switch
        {
            SsaGlobalAddressValue globalAddress => IsImmutableGlobalName(globalAddress.GlobalName),
            SsaValueReference reference => ResolveImmutableMemoryReference(reference, visitedValueNames),
            _ => false
        };
    }

    private bool ResolveImmutableMemoryReference(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return IsConstParameterValueReference(reference);
        }

        return definition switch
        {
            SsaUseRValue use => IsImmutableMemoryReference(use.Value, visitedValueNames),
            SsaAddressOfLocalRValue addressOfLocal => _constProvenanceLocalNames.Contains(addressOfLocal.LocalName),
            SsaAddressOfParameterRValue addressOfParameter => IsConstParameter(addressOfParameter.ParameterName),
            SsaFieldAddressRValue fieldAddress => IsImmutableMemoryReference(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => IsImmutableMemoryReference(elementAddress.Address, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => IsImmutableMemoryReference(sliceElementAddress.Slice, visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice => _constProvenanceLocalNames.Contains(makeSlice.LocalName),
            SsaMakeSliceFromPointerRValue makeSlice => IsImmutableMemoryReference(makeSlice.Pointer, visitedValueNames),
            SsaLoadLocalRValue loadLocal when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue)
                => IsImmutableMemoryReference(storedValue, visitedValueNames),
            SsaTextSliceRValue textSlice => IsImmutableMemoryReference(textSlice.TextValue, visitedValueNames),
            SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                        && convert.TargetType.Kind == StarkTypeKind.RawPointer
                => IsImmutableMemoryReference(convert.Operand, visitedValueNames),
            _ => false
        };
    }

    private bool HasConstMemoryProvenance(SsaValue value, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue globalAddress => IsImmutableGlobalName(globalAddress.GlobalName),
            SsaValueReference reference => HasConstMemoryProvenance(reference, visitedValueNames),
            _ => false
        };
    }

    private bool HasConstMemoryProvenance(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (IsConstParameterValueReference(reference))
        {
            return true;
        }

        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => HasConstMemoryProvenance(use.Value, visitedValueNames),
            SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                        && convert.TargetType.Kind == StarkTypeKind.RawPointer
                => HasConstMemoryProvenance(convert.Operand, visitedValueNames),
            SsaAddressOfParameterRValue addressOfParameter => IsConstParameter(addressOfParameter.ParameterName),
            SsaAddressOfLocalRValue addressOfLocal => _invariantLocalNames.Contains(addressOfLocal.LocalName),
            SsaFieldAddressRValue fieldAddress => HasConstMemoryProvenance(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => HasConstMemoryProvenance(elementAddress.Address, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => HasConstMemoryProvenance(sliceElementAddress.Slice, visitedValueNames),
            SsaMakeSliceFromPointerRValue makeSlice => HasConstMemoryProvenance(makeSlice.Pointer, visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice => _invariantLocalNames.Contains(makeSlice.LocalName),
            SsaTextSliceRValue textSlice => HasConstMemoryProvenance(textSlice.TextValue, visitedValueNames),
            SsaExtractFieldRValue extractField => HasConstMemoryProvenance(extractField.Target, visitedValueNames),
            SsaExtractIndexRValue extractIndex => HasConstMemoryProvenance(extractIndex.Target, visitedValueNames),
            SsaLoadGlobalRValue loadGlobal => IsImmutableGlobalName(loadGlobal.GlobalName),
            SsaLoadLocalRValue loadLocal => _constProvenanceLocalNames.Contains(loadLocal.LocalName)
                || (TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue)
                    && HasConstMemoryProvenance(storedValue, visitedValueNames)),
            SsaLoadIndirectRValue loadIndirect => HasConstMemoryProvenance(loadIndirect.Address, visitedValueNames),
            _ => false
        };
    }

    private bool IsConstParameterValueReference(SsaValueReference reference)
    {
        const string prefix = "arg_";
        return reference.Name.StartsWith(prefix, StringComparison.Ordinal)
            && IsConstParameter(reference.Name[prefix.Length..]);
    }

    private bool IsConstParameter(string parameterName)
    {
        return _function.Parameters.Any(parameter =>
            string.Equals(parameter.Name, parameterName, StringComparison.Ordinal)
            && parameter.IsConst);
    }

    private bool IsImmutableAggregateSource(SsaValue value, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue globalAddress => IsImmutableGlobalName(globalAddress.GlobalName),
            SsaValueReference reference when visitedValueNames.Add(reference.Name)
                                           && _valueDefinitions.TryGetValue(reference.Name, out var definition)
                => definition switch
                {
                    SsaUseRValue use => IsImmutableAggregateSource(use.Value, visitedValueNames),
                    SsaLoadGlobalRValue loadGlobal => IsImmutableGlobalName(loadGlobal.GlobalName),
                    SsaLoadLocalRValue loadLocal => _invariantLocalNames.Contains(loadLocal.LocalName),
                    SsaLoadIndirectRValue loadIndirect => IsImmutableMemoryReference(loadIndirect.Address, visitedValueNames),
                    _ => false
                },
            _ => IsImmutableMemoryReference(value, visitedValueNames)
        };
    }

    private static bool IsFrozenReadonlyPointer(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.RawPointer
            && !type.IsMutablePointer
            && type.ElementType is { } pointeeType
            && IsFrozenReadonlyType(pointeeType);
    }

    private static bool IsFrozenReadonlyView(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Slice
            && !type.IsMutableView
            && (type.AccessKind == StarkAccessKind.Frozen
                || type.ElementType is { AccessKind: StarkAccessKind.Frozen });
    }

    private static bool IsFrozenReadonlyType(StarkTypeSymbol type)
    {
        return type.AccessKind == StarkAccessKind.Frozen
            || IsFrozenReadonlyPointer(type)
            || IsFrozenReadonlyView(type);
    }

    private void EmitInvariantStartForLocalIfNeeded(string? localName, StarkTypeSymbol localType)
    {
        if (localName is null
            || !_invariantLocalNames.Contains(localName)
            || TryGetConcreteTypeLayout(localType) is not { } layout)
        {
            return;
        }

        var tokenName = EscapeIdentifier(CreateAbiTempName("invariant"));
        AppendLine($"  %{tokenName} = call ptr @llvm.invariant.start.p0(i64 {layout.SizeBytes}, ptr %{EscapeIdentifier($"slot_{localName}")})");
    }

    private bool TryResolveLocalAddressRoot(SsaValue address, out string localName)
    {
        return TryResolveLocalAddressRoot(address, new HashSet<string>(StringComparer.Ordinal), out localName);
    }

    private bool TryResolveLocalAddressRoot(
        SsaValue address,
        ISet<string> visitedValueNames,
        out string localName)
    {
        switch (address)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    localName = string.Empty;
                    return false;
                }

                return definition switch
                {
                    SsaUseRValue use => TryResolveLocalAddressRoot(use.Value, visitedValueNames, out localName),
                    SsaAddressOfLocalRValue addressOfLocal => ReturnLocalName(addressOfLocal.LocalName, out localName),
                    SsaFieldAddressRValue fieldAddress => TryResolveLocalAddressRoot(fieldAddress.Address, visitedValueNames, out localName),
                    SsaElementAddressRValue elementAddress => TryResolveLocalAddressRoot(elementAddress.Address, visitedValueNames, out localName),
                    SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                                && convert.TargetType.Kind == StarkTypeKind.RawPointer
                        => TryResolveLocalAddressRoot(convert.Operand, visitedValueNames, out localName),
                    _ => ReturnNoLocalName(out localName)
                };
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool ReturnLocalName(string value, out string localName)
    {
        localName = value;
        return true;
    }

    private static bool ReturnNoLocalName(out string localName)
    {
        localName = string.Empty;
        return false;
    }

    private static string FormatFloatLiteral(SsaFloatConstant floating)
    {
        if (!double.TryParse(
                CompileTimeExpressionEvaluator.StripFloatSuffix(floating.LiteralText),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new UnsupportedBodyEmissionException(
                $"Unable to parse floating-point literal '{floating.LiteralText}' for LLVM emission.");
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            var bits = floating.Type.BitWidth == 32
                ? BitConverter.DoubleToUInt64Bits((double)(float)parsed)
                : BitConverter.DoubleToUInt64Bits(parsed);
            return $"0x{bits:X16}";
        }

        var rendered = floating.Type.BitWidth == 32
            ? ((double)(float)parsed).ToString("R", CultureInfo.InvariantCulture)
            : parsed.ToString("R", CultureInfo.InvariantCulture);

        return rendered.Contains('.', StringComparison.Ordinal)
            || rendered.Contains('E', StringComparison.Ordinal)
            || rendered.Contains('e', StringComparison.Ordinal)
            ? rendered
            : rendered + ".0";
    }

    private string RenderDirectArgument(AbiParameterSymbol parameter, SsaValue argument)
    {
        if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
        {
            return $"ptr {ExtractStringDataPointer(argument)}";
        }

        var segments = new List<string> { MapType(parameter.LlvmType) };
        if (LlvmValueRangeFacts.TryBuildRangeAttribute(parameter.SourceType, out var rangeAttribute))
        {
            segments.Add(rangeAttribute);
        }

        segments.Add(FormatValue(argument));
        return string.Join(" ", segments);
    }

    private string RenderDirectVarargArgument(SsaValue argument)
    {
        var sourceType = StarkTypeSymbols.WithQualifiers(
            argument.Type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var llvmType = LowerCVarargArgumentType(sourceType);

        if (llvmType.Kind == StarkTypeKind.RawPointer && IsStringType(sourceType))
        {
            return $"ptr {ExtractStringDataPointer(argument)}";
        }

        return $"{MapType(llvmType)} {FormatValue(argument)}";
    }

    private static StarkTypeSymbol LowerCVarargArgumentType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            StarkTypeKind.Unicode => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false),
            _ => type
        };
    }

    private string RenderIndirectArgumentPointer(AbiParameterSymbol parameter, string pointerValue)
    {
        var segments = new List<string> { "ptr" };

        if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
        {
            segments.Add($"byval({MapType(parameter.SourceType)})");
            if (TryGetConcreteTypeLayout(parameter.SourceType) is { AlignmentBytes: > 1 } layout)
            {
                segments.Add($"align {layout.AlignmentBytes}");
            }
        }

        segments.Add(pointerValue);
        return string.Join(" ", segments);
    }

    private string RenderSRetArgumentPointer(StarkTypeSymbol returnType, string pointerValue)
    {
        var segments = new List<string> { "ptr", $"sret({MapType(returnType)})" };
        if (GetStackObjectAlignmentBytes(returnType) is { } alignmentBytes)
        {
            segments.Add($"align {alignmentBytes}");
        }

        segments.Add(pointerValue);
        return string.Join(" ", segments);
    }

    private string FormatStringConstantValue(SsaStringConstant text)
    {
        var pointer = FormatStringDataPointer(text.LiteralText, text.Type);
        var constant = ResolveStringConstant(text.LiteralText, text.Type);
        return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
    }

    private string ExtractStringDataPointer(SsaValue value)
    {
        if (!IsStringType(value.Type))
        {
            throw new UnsupportedBodyEmissionException($"Value '{value.Text}' is not a lowered string.");
        }

        if (value is SsaStringConstant stringConstant)
        {
            return FormatStringDataPointer(stringConstant.LiteralText, stringConstant.Type);
        }

        var tempName = $"%{EscapeIdentifier(CreateAbiTempName("str_data"))}";
        AppendLine($"  {tempName} = extractvalue {MapType(value.Type)} {FormatValue(value)}, 0");
        return tempName;
    }

    private string FormatStringDataPointer(string literalText, StarkTypeSymbol type)
    {
        var constant = ResolveStringConstant(literalText, type);
        return $"getelementptr{GetProvenInObjectGepFlags()} ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
    }

    private void EnsureLocalSlotExists(string localName, StarkTypeSymbol localType)
    {
        var slotName = EscapeIdentifier($"slot_{localName}");
        if (!_allocatedLocalSlots.Add(slotName))
        {
            return;
        }

        switch (GetLocalStorageClass(localName))
        {
            case "stack":
                QueueStaticAlloca($"%{slotName}", localType);
                return;
            case "heap":
                EmitHeapAllocateLocalSlot(slotName, localType);
                return;
            default:
                throw new UnsupportedBodyEmissionException(
                    $"Local storage class '{GetLocalStorageClass(localName)}' is not yet supported for LLVM body emission.");
        }
    }

    private void EmitHeapAllocateLocalSlot(string slotName, StarkTypeSymbol localType)
    {
        var sizePointer = $"%{EscapeIdentifier(CreateAbiTempName("heap_size_ptr"))}";
        var sizeValue = $"%{EscapeIdentifier(CreateAbiTempName("heap_size"))}";
        var alignmentBytes = GetHeapObjectAlignmentBytes(localType) ?? 1;
        AppendLine($"  {sizePointer} = getelementptr {MapType(localType)}, ptr null, i32 1");
        AppendLine($"  {sizeValue} = ptrtoint ptr {sizePointer} to {AllocatorSizeType}");
        AppendLine(
            $"  %{slotName} = call {BuildFreshAllocationResultAttributes(localType)} ptr @{HeapAllocateHelperName}({AllocatorSizeType} noundef {sizeValue}, {AllocatorSizeType} noundef {alignmentBytes})");
    }

    private string BuildFreshAllocationResultAttributes(StarkTypeSymbol allocatedType)
    {
        var attributes = new List<string>
        {
            "noalias",
            "nonnull",
            "noundef"
        };

        if (TryGetConcreteTypeLayout(allocatedType) is { } layout)
        {
            var alignmentBytes = GetHeapObjectAlignmentBytes(allocatedType) ?? layout.AlignmentBytes;
            if (alignmentBytes > 1)
            {
                attributes.Add($"align {alignmentBytes}");
            }

            if (layout.SizeBytes > 0)
            {
                attributes.Add($"dereferenceable({layout.SizeBytes})");
            }
        }

        return string.Join(" ", attributes);
    }

    private string GetLocalStorageClass(string localName)
    {
        return _localStorageClasses.TryGetValue(localName, out var storageClass)
            ? storageClass
            : "stack";
    }

    private void EnsureParameterSlotExists(AbiParameterSymbol parameter, StarkTypeSymbol parameterType)
    {
        var slotName = EscapeIdentifier($"slot_param_{parameter.SourceName}");
        if (_allocatedLocalSlots.Add(slotName))
        {
            QueueStaticAlloca($"%{slotName}", parameterType);

            var incomingValue = _materializedParameters.TryGetValue(parameter.LlvmName, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(parameter.LlvmName)}";
            AppendLine($"  store {MapType(parameterType)} {incomingValue}, ptr %{slotName}{GetStackObjectAlignmentSuffix(parameterType)}{GetDirectTbaaMetadataSuffix(CreateTbaaParameterRootKey(parameter.SourceName), parameterType)}");
        }
    }

    private void EmitEntryParameterMaterialization()
    {
        foreach (var parameter in _abiFunction.UserParameters)
        {
            if (parameter.Kind != AbiParameterKind.IndirectIn)
            {
                continue;
            }

            if (!_referencedValueNames.Contains(parameter.LlvmName)
                && !_referencedValueNames.Contains(parameter.SourceName))
            {
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && !RequiresAggregateValueMaterialization(parameter.LlvmName, parameter.SourceType))
            {
                continue;
            }

            var materializedName = $"%{EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"))}";
            AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}{GetTypeAlignmentSuffix(parameter.SourceType)}{GetValueRangeMetadataSuffix(parameter.SourceType)}{GetDirectTbaaMetadataSuffix(CreateTbaaParameterRootKey(parameter.SourceName), parameter.SourceType)}{GetScopedNoAliasMetadataSuffix(CreateScopedAliasParameterRootKey(parameter.SourceName))}");
            _materializedParameters[parameter.LlvmName] = materializedName;
            _materializedParameters[parameter.SourceName] = materializedName;
        }
    }

    private void EmitEntryParameterSlots()
    {
        foreach (var parameter in _abiFunction.UserParameters)
        {
            if (parameter.Kind == AbiParameterKind.IndirectIn)
            {
                continue;
            }

            if (!_addressTakenParameterNames.Contains(parameter.SourceName)
                && !_addressTakenParameterNames.Contains(parameter.LlvmName))
            {
                continue;
            }

            EnsureParameterSlotExists(parameter, parameter.SourceType);
        }
    }

    private void EmitEntryParameterDebugInfo()
    {
        if (_debugFunction is null)
        {
            return;
        }

        for (var index = 0; index < _abiFunction.UserParameters.Count; index++)
        {
            var parameter = _abiFunction.UserParameters[index];
            var variableRef = _debugFunction.GetParameterVariableRef(parameter.SourceName, parameter.SourceType, index + 1);

            if (parameter.Kind == AbiParameterKind.IndirectIn)
            {
                AppendLine($"  call void @llvm.dbg.declare(metadata ptr %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
                continue;
            }

            AppendLine(
                $"  call void @llvm.dbg.value(metadata {MapType(parameter.LlvmType)} %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
        }
    }

    private void EmitLocalDebugDeclare(string slotName, string localName, StarkTypeSymbol localType, SourceLocation? location)
    {
        if (_debugFunction is null)
        {
            return;
        }

        var variableRef = _debugFunction.GetLocalVariableRef(localName, localType, location ?? _ssaFunction.Location);
        AppendLine($"  call void @llvm.dbg.declare(metadata ptr {slotName}, metadata {variableRef}, metadata !DIExpression())");
    }

    private void QueueStaticAlloca(string slotName, StarkTypeSymbol slotType)
    {
        _entryStaticAllocas.Add($"  {slotName} = alloca {MapType(slotType)}{GetStackObjectAlignmentSuffix(slotType)}");
    }

    private void FlushEntryStaticAllocas()
    {
        if (_entryStaticAllocas.Count == 0 || _entryStaticAllocaInsertionIndex is not int insertionIndex)
        {
            return;
        }

        _builder.Insert(insertionIndex, string.Join(Environment.NewLine, _entryStaticAllocas) + Environment.NewLine);
    }

    private static HashSet<string> CollectReferencedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    VisitValue(incoming.Value);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitValue(block.Terminator.Condition);
            VisitValue(block.Terminator.Value);

            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitValue(switchCase.MatchValue);
                }
            }
        }

        return names;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    VisitRValue(valueInstruction.Value);
                    break;
                case SsaStoreLocalInstruction storeLocal:
                    VisitValue(storeLocal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitValue(storeIndirect.Address);
                    VisitValue(storeIndirect.Value);
                    break;
                case SsaCopyMemoryInstruction copyMemory:
                    VisitValue(copyMemory.DestinationAddress);
                    VisitValue(copyMemory.SourceAddress);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitValue(storeGlobal.Value);
                    break;
            }
        }

        void VisitRValue(SsaRValue value)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    VisitValue(use.Value);
                    break;
                case SsaUnaryRValue unary:
                    VisitValue(unary.Operand);
                    break;
                case SsaBinaryRValue binary:
                    VisitValue(binary.Left);
                    VisitValue(binary.Right);
                    break;
                case SsaSelectRValue select:
                    VisitValue(select.Condition);
                    VisitValue(select.WhenTrue);
                    VisitValue(select.WhenFalse);
                    break;
                case SsaCallRValue call:
                    foreach (var argument in call.Arguments)
                    {
                        VisitValue(argument);
                    }

                    foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                    {
                        VisitValue(address);
                    }

                    break;
                case SsaConvertRValue convert:
                    VisitValue(convert.Operand);
                    break;
                case SsaExtractFieldRValue extractField:
                    VisitValue(extractField.Target);
                    break;
                case SsaInsertFieldRValue insertField:
                    VisitValue(insertField.Target);
                    VisitValue(insertField.Value);
                    break;
                case SsaExtractIndexRValue extractIndex:
                    VisitValue(extractIndex.Target);
                    break;
                case SsaInsertIndexRValue insertIndex:
                    VisitValue(insertIndex.Target);
                    VisitValue(insertIndex.Value);
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    VisitValue(makeSlice.Pointer);
                    VisitValue(makeSlice.Length);
                    break;
                case SsaLoadSliceElementRValue loadSlice:
                    VisitValue(loadSlice.Slice);
                    VisitValue(loadSlice.Index);
                    break;
                case SsaTextSliceRValue textSlice:
                    VisitValue(textSlice.TextValue);
                    VisitValue(textSlice.Start);
                    VisitValue(textSlice.Length);
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    VisitValue(fieldAddress.Address);
                    break;
                case SsaElementAddressRValue elementAddress:
                    VisitValue(elementAddress.Address);
                    VisitValue(elementAddress.Index);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    VisitValue(sliceElementAddress.Slice);
                    VisitValue(sliceElementAddress.Index);
                    break;
                case SsaLoadIndirectRValue loadIndirect:
                    VisitValue(loadIndirect.Address);
                    break;
            }
        }

        void VisitValue(SsaValue? value)
        {
            if (value is SsaValueReference reference)
            {
                names.Add(reference.Name);
            }
        }
    }

    private static HashSet<string> CollectAddressTakenParameterNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction { Value: SsaAddressOfParameterRValue addressOfParameter })
                {
                    names.Add(addressOfParameter.ParameterName);
                }
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction valueInstruction)
                {
                    definitions[valueInstruction.ResultName] = valueInstruction.Value;
                }
            }
        }

        return definitions;
    }

    private static IReadOnlyDictionary<int, int> CollectBlockOrder(SsaFunction function)
    {
        return function.Blocks
            .Select(static (block, index) => (BlockId: block.Id, Index: index))
            .ToDictionary(static item => item.BlockId, static item => item.Index);
    }

    private static IReadOnlyDictionary<int, int> CountPredecessors(SsaFunction function)
    {
        var predecessorSets = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<int>(),
            EqualityComparer<int>.Default);

        foreach (var block in function.Blocks)
        {
            foreach (var target in EnumerateTerminatorTargets(block.Terminator).Distinct())
            {
                if (predecessorSets.TryGetValue(target, out var predecessors))
                {
                    predecessors.Add(block.Id);
                }
            }
        }

        return predecessorSets.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value.Count,
            EqualityComparer<int>.Default);
    }

    private static IEnumerable<int> EnumerateTerminatorTargets(SsaTerminator terminator)
    {
        foreach (var target in terminator.Targets)
        {
            yield return target;
        }

        if (terminator.DefaultTarget is int defaultTarget)
        {
            yield return defaultTarget;
        }

        if (terminator.SwitchCases is null)
        {
            yield break;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            yield return switchCase.TargetBlockId;
        }
    }

    private static bool CanEmitAssumeInSuccessor(
        int entryBlockId,
        int sourceBlockId,
        int targetBlockId,
        IReadOnlyDictionary<int, int> blockOrderById,
        IReadOnlyDictionary<int, int> predecessorCounts)
    {
        if (targetBlockId == entryBlockId
            || !predecessorCounts.TryGetValue(targetBlockId, out var predecessorCount)
            || predecessorCount != 1
            || !blockOrderById.TryGetValue(sourceBlockId, out var sourceOrder)
            || !blockOrderById.TryGetValue(targetBlockId, out var targetOrder))
        {
            return false;
        }

        // Avoid emitting a non-PHI instruction in a block that appears before
        // the branch condition definition in textual LLVM order.
        return sourceOrder < targetOrder;
    }

    private static bool IsPotentialAssumableCondition(
        SsaValue condition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        return TryResolveComparisonCondition(condition, valueDefinitions, out var comparison)
            && (IsIntegerValueRangeNarrowingComparison(comparison, valueDefinitions)
                || IsPointerComparisonCandidate(comparison, valueDefinitions));
    }

    private static bool TryResolveComparisonCondition(
        SsaValue condition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        out SsaBinaryRValue comparison)
    {
        if (condition is SsaValueReference reference
            && valueDefinitions.TryGetValue(reference.Name, out var definition)
            && definition is SsaBinaryRValue binary
            && binary.Type.Kind == StarkTypeKind.Bool
            && IsComparisonOperator(binary.Operator))
        {
            comparison = binary;
            return true;
        }

        comparison = null!;
        return false;
    }

    private static bool IsComparisonOperator(SsaBinaryOperator operation)
    {
        return operation is SsaBinaryOperator.Equal
            or SsaBinaryOperator.NotEqual
            or SsaBinaryOperator.LessThan
            or SsaBinaryOperator.LessThanOrEqual
            or SsaBinaryOperator.GreaterThan
            or SsaBinaryOperator.GreaterThanOrEqual;
    }

    private static bool IsIntegerValueRangeNarrowingComparison(
        SsaBinaryRValue comparison,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        return comparison.Left.Type.Kind == StarkTypeKind.Integer
            && comparison.Right.Type.Kind == StarkTypeKind.Integer
            && (TryResolveIntegerConstant(comparison.Left, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out _)
                || TryResolveIntegerConstant(comparison.Right, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out _));
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out BigInteger constant)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    constant = default;
                    return false;
                }

                return definition switch
                {
                    SsaUseRValue use => TryResolveIntegerConstant(use.Value, valueDefinitions, visitedValueNames, out constant),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Integer =>
                        TryResolveIntegerConstant(convert.Operand, valueDefinitions, visitedValueNames, out constant),
                    _ => Fail(out constant)
                };
            default:
                constant = default;
                return false;
        }

        static bool Fail(out BigInteger value)
        {
            value = default;
            return false;
        }
    }

    private static bool IsPointerComparisonCandidate(
        SsaBinaryRValue comparison,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        if (comparison.Left.Type.Kind != StarkTypeKind.RawPointer
            || comparison.Right.Type.Kind != StarkTypeKind.RawPointer
            || comparison.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        if (comparison.Left is SsaNullConstant || comparison.Right is SsaNullConstant)
        {
            return true;
        }

        return IsPotentialKnownNonNullPointerValue(comparison.Left, valueDefinitions, new HashSet<string>(StringComparer.Ordinal))
            || IsPotentialKnownNonNullPointerValue(comparison.Right, valueDefinitions, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsPotentialKnownNonNullPointerValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaValueReference reference when reference.Type.Kind == StarkTypeKind.RawPointer
                && visitedValueNames.Add(reference.Name)
                && valueDefinitions.TryGetValue(reference.Name, out var definition) =>
                IsPotentialKnownNonNullPointerDefinition(definition, valueDefinitions, visitedValueNames),
            _ => false
        };
    }

    private static bool IsPotentialKnownNonNullPointerDefinition(
        SsaRValue definition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames)
    {
        return definition switch
        {
            SsaUseRValue use => IsPotentialKnownNonNullPointerValue(use.Value, valueDefinitions, visitedValueNames),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer =>
                IsPotentialKnownNonNullPointerValue(convert.Operand, valueDefinitions, visitedValueNames),
            SsaSelectRValue select when select.Type.Kind == StarkTypeKind.RawPointer =>
                IsPotentialKnownNonNullPointerValue(
                    select.WhenTrue,
                    valueDefinitions,
                    new HashSet<string>(visitedValueNames, StringComparer.Ordinal))
                && IsPotentialKnownNonNullPointerValue(
                    select.WhenFalse,
                    valueDefinitions,
                    new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            SsaAddressOfLocalRValue => true,
            SsaAddressOfParameterRValue => true,
            SsaFieldAddressRValue fieldAddress =>
                IsPotentialKnownNonNullPointerValue(fieldAddress.Address, valueDefinitions, visitedValueNames),
            SsaElementAddressRValue elementAddress =>
                IsPotentialKnownNonNullPointerValue(elementAddress.Address, valueDefinitions, visitedValueNames),
            _ => false
        };
    }

    private static bool TryGetNullComparedPointer(
        SsaBinaryRValue comparison,
        out SsaValue pointer,
        out bool nonNullWhenConditionTrue)
    {
        if (comparison.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            pointer = null!;
            nonNullWhenConditionTrue = false;
            return false;
        }

        if (comparison.Left is SsaNullConstant && comparison.Right.Type.Kind == StarkTypeKind.RawPointer)
        {
            pointer = comparison.Right;
            nonNullWhenConditionTrue = comparison.Operator == SsaBinaryOperator.NotEqual;
            return true;
        }

        if (comparison.Right is SsaNullConstant && comparison.Left.Type.Kind == StarkTypeKind.RawPointer)
        {
            pointer = comparison.Left;
            nonNullWhenConditionTrue = comparison.Operator == SsaBinaryOperator.NotEqual;
            return true;
        }

        pointer = null!;
        nonNullWhenConditionTrue = false;
        return false;
    }

    private static SsaBinaryOperator? GetAssumedComparisonOperator(SsaBinaryOperator operation, bool assumeConditionTrue)
    {
        if (assumeConditionTrue)
        {
            return operation;
        }

        return operation switch
        {
            SsaBinaryOperator.Equal => SsaBinaryOperator.NotEqual,
            SsaBinaryOperator.NotEqual => SsaBinaryOperator.Equal,
            SsaBinaryOperator.LessThan => SsaBinaryOperator.GreaterThanOrEqual,
            SsaBinaryOperator.LessThanOrEqual => SsaBinaryOperator.GreaterThan,
            SsaBinaryOperator.GreaterThan => SsaBinaryOperator.LessThanOrEqual,
            SsaBinaryOperator.GreaterThanOrEqual => SsaBinaryOperator.LessThan,
            _ => null
        };
    }

    private static HashSet<string> CollectTailCallResultNames(
        SsaFunction function,
        AbiFunctionSignature callerAbi,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName,
        LlvmEmissionContext context,
        bool isStrictFp)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (isStrictFp
            || callerAbi.ReturnsIndirect
            || callerAbi.LlvmReturnType.Kind == StarkTypeKind.Void
            || callerAbi.IsFfi
            || !callerAbi.UsesFastCallingConvention)
        {
            return names;
        }

        foreach (var block in function.Blocks)
        {
            if (block.Terminator.Kind != SsaTerminatorKind.Return
                || block.Terminator.Value is not SsaValueReference returnedValue
                || block.Instructions.Count == 0
                || block.Instructions[^1] is not SsaValueInstruction
                {
                    ResultName: var resultName,
                    Value: SsaCallRValue call
                }
                || !string.Equals(resultName, returnedValue.Name, StringComparison.Ordinal)
                || !CanEmitTailCallMarker(callerAbi, call, resolveCallAbi, currentFunctionName, context))
            {
                continue;
            }

            names.Add(resultName);
        }

        return names;
    }

    private static bool CanEmitTailCallMarker(
        AbiFunctionSignature callerAbi,
        SsaCallRValue call,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName,
        LlvmEmissionContext context)
    {
        var calleeAbi = resolveCallAbi(currentFunctionName, call.FunctionName);
        return calleeAbi is not null
            && !calleeAbi.ReturnsIndirect
            && !calleeAbi.IsFfi
            && calleeAbi.UsesFastCallingConvention
            && calleeAbi.LlvmReturnType.Kind != StarkTypeKind.Void
            && string.Equals(context.MapType(callerAbi.LlvmReturnType), context.MapType(calleeAbi.LlvmReturnType), StringComparison.Ordinal)
            && calleeAbi.UserParameters.All(static parameter => parameter.Kind == AbiParameterKind.Direct)
            && call.Arguments.All(static argument => !MayContainPointerStorage(argument.Type));
    }

    private static bool MayContainPointerStorage(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.Named => true,
            StarkTypeKind.FixedArray when type.ElementType is not null => MayContainPointerStorage(type.ElementType),
            _ => false
        };
    }

    private static HashSet<string> CollectTbaaUnsafeAddressRoots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    if (incoming.Value.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddAddressValueRootsFresh(incoming.Value);
                    }
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitEscapingValue(block.Terminator.Value);
            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitEscapingValue(switchCase.MatchValue);
                }
            }
        }

        return roots;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal:
                    VisitEscapingValue(storeLocal.Value);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitEscapingValue(storeGlobal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitEscapingValue(storeIndirect.Value);
                    break;
                case SsaValueInstruction { Value: SsaCallRValue call }:
                    foreach (var argument in call.Arguments)
                    {
                        VisitEscapingValue(argument);
                    }

                    foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                    {
                        VisitEscapingValue(address);
                    }

                    break;
                case SsaValueInstruction { Value: SsaConvertRValue convert }
                    when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                         || convert.TargetType.Kind == StarkTypeKind.RawPointer:
                    AddAddressValueRootsFresh(convert.Operand);
                    break;
            }
        }

        void VisitEscapingValue(SsaValue? value)
        {
            if (value?.Type.Kind == StarkTypeKind.RawPointer)
            {
                AddAddressValueRootsFresh(value);
            }
        }

        void AddAddressValueRootsFresh(SsaValue value)
        {
            AddAddressValueRoots(value, new HashSet<string>(StringComparer.Ordinal));
        }

        void AddAddressValueRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else if (reference.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                case SsaGlobalAddressValue globalAddress:
                    roots.Add(CreateTbaaGlobalRootKey(globalAddress.GlobalName));
                    break;
            }
        }

        void AddAddressRValueRoots(SsaRValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    AddAddressValueRoots(use.Value, visitedValueNames);
                    break;
                case SsaAddressOfLocalRValue addressOfLocal:
                    roots.Add(CreateTbaaLocalRootKey(addressOfLocal.LocalName));
                    break;
                case SsaAddressOfParameterRValue addressOfParameter:
                    roots.Add(CreateTbaaParameterRootKey(addressOfParameter.ParameterName));
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    AddAddressValueRoots(fieldAddress.Address, visitedValueNames);
                    break;
                case SsaElementAddressRValue elementAddress:
                    AddAddressValueRoots(elementAddress.Address, visitedValueNames);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    AddSliceRoots(sliceElementAddress.Slice, visitedValueNames);
                    break;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    roots.Add(CreateTbaaLocalRootKey(makeSlice.LocalName));
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    AddAddressValueRoots(makeSlice.Pointer, visitedValueNames);
                    break;
                case SsaTextSliceRValue textSlice:
                    AddSliceRoots(textSlice.TextValue, visitedValueNames);
                    break;
                case SsaConvertRValue convert:
                    AddAddressValueRoots(convert.Operand, visitedValueNames);
                    break;
                case SsaSelectRValue select:
                    AddAddressValueRoots(select.WhenTrue, visitedValueNames);
                    AddAddressValueRoots(select.WhenFalse, visitedValueNames);
                    break;
            }
        }

        void AddSliceRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                case SsaStringConstant:
                    break;
                default:
                    AddAddressValueRoots(value, visitedValueNames);
                    break;
            }
        }

        void AddParameterRoot(string parameterName)
        {
            roots.Add(CreateTbaaParameterRootKey(parameterName));
            if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
            {
                roots.Add(CreateTbaaParameterRootKey(parameterName[4..]));
            }
        }
    }

    private static HashSet<string> CollectScopedNoAliasUnsafeAddressRoots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    if (incoming.Value.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddAddressValueRootsFresh(incoming.Value);
                    }
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitEscapingValue(block.Terminator.Value);
            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitEscapingValue(switchCase.MatchValue);
                }
            }
        }

        return roots;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal:
                    VisitEscapingValue(storeLocal.Value);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitEscapingValue(storeGlobal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitEscapingValue(storeIndirect.Value);
                    break;
                case SsaValueInstruction { Value: SsaCallRValue call }:
                    VisitCallArguments(call);
                    break;
                case SsaValueInstruction { Value: SsaConvertRValue convert }
                    when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                         || convert.TargetType.Kind == StarkTypeKind.RawPointer:
                    AddAddressValueRootsFresh(convert.Operand);
                    break;
            }
        }

        void VisitCallArguments(SsaCallRValue call)
        {
            var calleeAbi = resolveCallAbi(currentFunctionName, call.FunctionName);
            if (calleeAbi is null || calleeAbi.IsFfi)
            {
                foreach (var argument in call.Arguments)
                {
                    VisitEscapingValue(argument);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    VisitEscapingValue(address);
                }

                return;
            }

            var userParameters = calleeAbi.UserParameters;
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index];
                if (argument.Type.Kind != StarkTypeKind.RawPointer)
                {
                    continue;
                }

                if (index >= userParameters.Count || userParameters[index].SourceType.Kind == StarkTypeKind.RawPointer)
                {
                    AddAddressValueRootsFresh(argument);
                }
            }
        }

        void VisitEscapingValue(SsaValue? value)
        {
            if (value?.Type.Kind == StarkTypeKind.RawPointer)
            {
                AddAddressValueRootsFresh(value);
            }
        }

        void AddAddressValueRootsFresh(SsaValue value)
        {
            AddAddressValueRoots(value, new HashSet<string>(StringComparer.Ordinal));
        }

        void AddAddressValueRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else if (reference.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
            }
        }

        void AddAddressRValueRoots(SsaRValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    AddAddressValueRoots(use.Value, visitedValueNames);
                    break;
                case SsaAddressOfParameterRValue addressOfParameter:
                    roots.Add(CreateScopedAliasParameterRootKey(addressOfParameter.ParameterName));
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    AddAddressValueRoots(fieldAddress.Address, visitedValueNames);
                    break;
                case SsaElementAddressRValue elementAddress:
                    AddAddressValueRoots(elementAddress.Address, visitedValueNames);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    AddSliceRoots(sliceElementAddress.Slice, visitedValueNames);
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    AddAddressValueRoots(makeSlice.Pointer, visitedValueNames);
                    break;
                case SsaTextSliceRValue textSlice:
                    AddSliceRoots(textSlice.TextValue, visitedValueNames);
                    break;
                case SsaConvertRValue convert:
                    AddAddressValueRoots(convert.Operand, visitedValueNames);
                    break;
                case SsaSelectRValue select:
                    AddAddressValueRoots(select.WhenTrue, visitedValueNames);
                    AddAddressValueRoots(select.WhenFalse, visitedValueNames);
                    break;
            }
        }

        void AddSliceRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                default:
                    AddAddressValueRoots(value, visitedValueNames);
                    break;
            }
        }

        void AddParameterRoot(string parameterName)
        {
            roots.Add(CreateScopedAliasParameterRootKey(parameterName));
            if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
            {
                roots.Add(CreateScopedAliasParameterRootKey(parameterName[4..]));
            }
        }
    }

    private static Dictionary<string, string> CollectLocalStorageClasses(SsaFunction function)
    {
        var storageClasses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaAllocateLocalInstruction allocateLocal)
                {
                    storageClasses[allocateLocal.LocalName] = allocateLocal.StorageClass;
                }
            }
        }

        return storageClasses;
    }

    private IReadOnlyDictionary<string, SsaValue> CollectSingleStoreLocalValues()
    {
        var values = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var writeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaStoreLocalInstruction storeLocal:
                        writeCounts.TryGetValue(storeLocal.LocalName, out var writeCount);
                        writeCounts[storeLocal.LocalName] = writeCount + 1;
                        if (writeCount == 0)
                        {
                            values[storeLocal.LocalName] = storeLocal.Value;
                        }
                        else
                        {
                            values.Remove(storeLocal.LocalName);
                        }

                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        if (TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var copyDestinationLocal))
                        {
                            blocked.Add(copyDestinationLocal);
                        }

                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        if (TryResolveLocalAddressRoot(storeIndirect.Address, out var indirectDestinationLocal))
                        {
                            blocked.Add(indirectDestinationLocal);
                        }

                        break;
                    case SsaValueInstruction { Value: SsaCallRValue call }:
                        foreach (var argument in call.Arguments)
                        {
                            if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                            {
                                blocked.Add(escapedLocal);
                            }
                        }

                        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                        {
                            if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                            {
                                blocked.Add(escapedLocal);
                            }
                        }

                        break;
                }
            }
        }

        foreach (var localName in blocked)
        {
            values.Remove(localName);
        }

        foreach (var (localName, writeCount) in writeCounts)
        {
            if (writeCount != 1)
            {
                values.Remove(localName);
            }
        }

        return values;
    }

    private bool TryResolveSingleStoreLocalValue(string localName, out SsaValue value)
    {
        return _singleStoreLocalValues.TryGetValue(localName, out value!);
    }

    private HashSet<string> CollectConstProvenanceLocalNames()
    {
        return _ssaFunction.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaAllocateLocalInstruction>()
            .Where(static allocateLocal => allocateLocal.HasConstProvenance)
            .Select(static allocateLocal => allocateLocal.LocalName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private HashSet<string> CollectInvariantLocalNames()
    {
        var candidates = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var writeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaAllocateLocalInstruction { IsImmutable: true, StorageClass: "stack" } allocateLocal:
                        if (TryGetConcreteTypeLayout(allocateLocal.LocalType) is not null)
                        {
                            candidates[allocateLocal.LocalName] = allocateLocal.LocalType;
                        }

                        break;
                    case SsaStoreLocalInstruction storeLocal:
                        CountLocalWrite(storeLocal.LocalName);
                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        if (TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var copyDestinationLocal))
                        {
                            CountLocalWrite(copyDestinationLocal);
                        }

                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        if (TryResolveLocalAddressRoot(storeIndirect.Address, out var indirectDestinationLocal))
                        {
                            blocked.Add(indirectDestinationLocal);
                        }

                        break;
                    case SsaValueInstruction { Value: SsaCallRValue call }:
                        foreach (var argument in call.Arguments)
                        {
                            if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                            {
                                blocked.Add(escapedLocal);
                            }
                        }

                        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                        {
                            if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                            {
                                blocked.Add(escapedLocal);
                            }
                        }

                        break;
                }
            }
        }

        return candidates.Keys
            .Where(localName => !blocked.Contains(localName)
                                && writeCounts.TryGetValue(localName, out var writeCount)
                                && writeCount == 1)
            .ToHashSet(StringComparer.Ordinal);

        void CountLocalWrite(string localName)
        {
            writeCounts[localName] = writeCounts.TryGetValue(localName, out var count)
                ? count + 1
                : 1;
        }
    }

    private string FormatValueReference(SsaValueReference reference)
    {
        if (_valueAliases.TryGetValue(reference.Name, out var alias))
        {
            return alias;
        }

        return _materializedParameters.TryGetValue(reference.Name, out var materialized)
            ? materialized
            : $"%{EscapeIdentifier(reference.Name)}";
    }

    private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

    private string AllocatorSizeType => _context.AllocatorSizeType;

    private string EmptyMetadataRef => _context.EmptyTupleMetadataRef;

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _context.ResolveNamedTypeSymbol(type);

    private EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type) =>
        _context.ResolveStringConstant(literalText, type);

    private string ResolveGlobalSymbolName(string globalName) => _context.ResolveGlobalSymbolName(globalName);

    private bool IsImmutableGlobalName(string globalName) => _context.IsImmutableGlobalName(globalName);

    private static string GetFloatIntrinsicSuffix(StarkTypeSymbol type)
    {
        return type.BitWidth switch
        {
            16 => "f16",
            32 => "f32",
            64 => "f64",
            80 => "f80",
            128 => "f128",
            _ => throw new InvalidOperationException($"Unsupported float intrinsic width '{type.BitWidth}'.")
        };
    }

    private static string GetIntegerExponentHelperName(int bitWidth)
    {
        return $"{IntegerExponentHelperNamePrefix}{bitWidth}";
    }

    private static string GetFixedArrayOrderedComparisonHelperName(StarkTypeSymbol fixedArrayType)
    {
        return $"{FixedArrayCompareHelperNamePrefix}{EscapeIdentifier(fixedArrayType.DisplayName)}";
    }

    private static string GetScalarizedAggregateOrderedComparisonHelperName(StarkTypeSymbol aggregateType)
    {
        return $"{ScalarizedAggregateCompareHelperNamePrefix}{EscapeIdentifier(aggregateType.DisplayName)}";
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static SourceLocation? GetInstructionLocation(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction.Location,
            SsaAllocateLocalInstruction allocateLocal => allocateLocal.Location,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart.Location,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd.Location,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal.Location,
            SsaStoreLocalInstruction storeLocal => storeLocal.Location,
            SsaCopyMemoryInstruction copyMemory => copyMemory.Location,
            SsaStoreIndirectInstruction storeIndirect => storeIndirect.Location,
            SsaStoreGlobalInstruction storeGlobal => storeGlobal.Location,
            _ => null
        };
    }

    private readonly record struct TbaaAddressAccess(
        StarkTypeSymbol RootType,
        long OffsetBytes,
        bool UseStructPath);

    private readonly record struct ScopedNoAliasRoot(
        string Key,
        string DisplayName);

    private sealed record ScopedNoAliasMetadataModel(
        IReadOnlyDictionary<string, ScopedNoAliasAccessMetadata> Accesses,
        IReadOnlyDictionary<string, string> ScopeRefs);

    private sealed record ScopedNoAliasAccessMetadata(
        string AliasScopeListRef,
        string? NoAliasListRef);

    private static bool IsStringType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private enum LlvmAssumeOperandBundleKind
    {
        NonNull,
        Align
    }

    private sealed record LlvmAssumeOperandBundle(
        LlvmAssumeOperandBundleKind Kind,
        SsaValue Pointer,
        int? AlignmentBytes = null);

    private sealed record LlvmAssumeFact(
        SsaValue? Condition,
        bool NegateCondition,
        IReadOnlyList<LlvmAssumeOperandBundle> OperandBundles);

    private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
    {
        return textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new UnsupportedBodyEmissionException($"Text operations require an ascii/unicode value, but found '{textType.DisplayName}'.")
        };
    }

    private void AppendLine(string text)
    {
        if (_debugFunction is not null
            && _currentDebugLocation is not null
            && ShouldAttachDebugLocation(text))
        {
            text = $"{text}, !dbg {_debugFunction.GetLocationRef(_currentDebugLocation)}";
        }

        _builder.AppendLine(text);
    }

    private static bool ShouldAttachDebugLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !text.StartsWith("  ", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return !trimmed.StartsWith(';')
            && !trimmed.StartsWith('}');
    }
}
