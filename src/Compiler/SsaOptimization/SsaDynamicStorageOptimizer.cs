using System.Numerics;

namespace Stark.Compiler;

internal sealed class SsaDynamicStorageOptimizer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> _directCallParameterEffects;

    private sealed record DynamicStorageLocalMutation(
        string LocalName,
        SsaValueFacts Before,
        SsaValueFacts OnSuccess);

    private sealed record DynamicStorageBlockTransfer(
        IReadOnlyDictionary<string, SsaValueFacts> ExitState,
        IReadOnlyDictionary<string, DynamicStorageLocalMutation> ConditionalMutations);

    public SsaDynamicStorageOptimizer(
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>? directCallParameterEffects = null)
    {
        _directCallParameterEffects = directCallParameterEffects
                                      ?? new Dictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>(StringComparer.Ordinal);
    }

    public SsaIrModule Optimize(SsaIrModule module, SsaValueFactModel facts)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = facts.Functions.TryGetValue(function.Name, out var functionFacts)
                    ? OptimizeFunction(function, functionFacts)
                    : function;
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private SsaFunction OptimizeFunction(
        SsaFunction function,
        SsaFunctionFactModel facts)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var definitions = CollectValueDefinitions(function);
        var entryStates = AnalyzeDynamicStorageEntryStates(function, definitions, facts.Values, _directCallParameterEffects);
        if (entryStates.Count == 0)
        {
            return function;
        }

        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var entryState = entryStates.TryGetValue(block.Id, out var state)
                    ? state
                    : EmptyState;
                var optimized = OptimizeBlock(block, entryState, definitions, facts.Values, _directCallParameterEffects);
                changed |= !ReferenceEquals(optimized, block);
                return optimized;
            })
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private static readonly IReadOnlyDictionary<string, SsaValueFacts> EmptyState =
        new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> AnalyzeDynamicStorageEntryStates(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        var reachableBlockIds = FindReachableBlockIds(function);
        var reachableBlocks = function.Blocks
            .Where(block => reachableBlockIds.Contains(block.Id))
            .ToArray();
        if (reachableBlocks.Length == 0)
        {
            return new Dictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>();
        }

        var entryStates = new Dictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>();
        var initializedEntries = new HashSet<int>();
        entryStates[function.EntryBlockId] = EmptyState;
        initializedEntries.Add(function.EntryBlockId);

        var changed = true;
        var iterationLimit = Math.Max(4, reachableBlocks.Length * 4);
        for (var iteration = 0; changed && iteration < iterationLimit; iteration++)
        {
            changed = false;
            foreach (var block in reachableBlocks)
            {
                if (!initializedEntries.Contains(block.Id)
                    || !entryStates.TryGetValue(block.Id, out var entryState))
                {
                    continue;
                }

                var transfer = AnalyzeBlockTransfer(block, entryState, definitions, values, directCallParameterEffects);
                foreach (var target in EnumerateTerminatorTargets(block.Terminator))
                {
                    if (!reachableBlockIds.Contains(target))
                    {
                        continue;
                    }

                    if (!TryApplyDynamicStorageEdgeTransfer(
                        block.Terminator,
                        target,
                        transfer.ExitState,
                        transfer.ConditionalMutations,
                        definitions,
                        values,
                        out var edgeState))
                    {
                        continue;
                    }

                    if (MergeEntryState(target, edgeState, entryStates, initializedEntries))
                    {
                        changed = true;
                    }
                }
            }
        }

        return entryStates;
    }

    private static HashSet<int> FindReachableBlockIds(SsaFunction function)
    {
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(function.EntryBlockId);

        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current)
                || !blocksById.TryGetValue(current, out var block))
            {
                continue;
            }

            foreach (var target in EnumerateTerminatorTargets(block.Terminator))
            {
                if (!reachable.Contains(target))
                {
                    pending.Push(target);
                }
            }
        }

        return reachable;
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
    }

    private static DynamicStorageBlockTransfer AnalyzeBlockTransfer(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaValueFacts> entryState,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        var state = CloneState(entryState);
        var conditionalMutations = new Dictionary<string, DynamicStorageLocalMutation>(StringComparer.Ordinal);
        var dynamicIntegerRanges = new Dictionary<string, SsaIntegerRangeFact>(StringComparer.Ordinal);
        foreach (var instruction in block.Instructions)
        {
            ApplyInstructionTransfer(
                instruction,
                state,
                conditionalMutations,
                definitions,
                values,
                dynamicIntegerRanges,
                directCallParameterEffects);
        }

        return new DynamicStorageBlockTransfer(state, conditionalMutations);
    }

    private static SsaBasicBlock OptimizeBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaValueFacts> entryState,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        var state = CloneState(entryState);
        var conditionalMutations = new Dictionary<string, DynamicStorageLocalMutation>(StringComparer.Ordinal);
        var dynamicIntegerRanges = new Dictionary<string, SsaIntegerRangeFact>(StringComparer.Ordinal);
        var changed = false;
        var instructions = new List<SsaInstruction>(block.Instructions.Count);

        foreach (var instruction in block.Instructions)
        {
            if (TryOptimizeInstruction(instruction, state, definitions, values, out var optimized))
            {
                changed = true;
                if (optimized is not null)
                {
                    instructions.Add(optimized);
                    ApplyInstructionTransfer(
                        optimized,
                        state,
                        conditionalMutations,
                        definitions,
                        values,
                        dynamicIntegerRanges,
                        directCallParameterEffects);
                }

                continue;
            }

            instructions.Add(instruction);
            ApplyInstructionTransfer(
                instruction,
                state,
                conditionalMutations,
                definitions,
                values,
                dynamicIntegerRanges,
                directCallParameterEffects);
        }

        return changed
            ? block with { Instructions = instructions.ToArray() }
            : block;
    }

    private static bool TryOptimizeInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out SsaInstruction? optimized)
    {
        optimized = null;
        if (instruction is not SsaValueInstruction valueInstruction)
        {
            return false;
        }

        switch (valueInstruction.Value)
        {
            case SsaDynamicStorageReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName)
                     && state.TryGetValue(localName, out var current)
                     && CanProveReserveAdditionalNoop(current, reserve.AdditionalCapacity, values):
                optimized = null;
                return true;

            case SsaDynamicStorageTryReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName)
                     && state.TryGetValue(localName, out var current)
                     && CanProveReserveAdditionalNoop(current, reserve.AdditionalCapacity, values):
                optimized = valueInstruction with
                {
                    Value = new SsaUseRValue(new SsaBoolConstant(true))
                };
                return true;

            case SsaDynamicStorageTryReserveCapacityRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName)
                     && state.TryGetValue(localName, out var current)
                     && CanProveReserveCapacityNoop(current, reserve.TargetCapacity, values):
                optimized = valueInstruction with
                {
                    Value = new SsaUseRValue(new SsaBoolConstant(true))
                };
                return true;

            case SsaDynamicStorageFreeRValue free
                when CanProveDynamicStorageFreeNoop(free.Storage, values):
                optimized = null;
                return true;

            case SsaDynamicStorageMoveLastRValue moveLast
                when !moveLast.IsKnownNonEmpty
                     && TryResolveLocalAddressRoot(moveLast.StorageAddress, definitions, out var localName)
                     && state.TryGetValue(localName, out var current)
                     && CanProveMoveLastNonEmpty(current):
                optimized = valueInstruction with
                {
                    Value = moveLast with { IsKnownNonEmpty = true }
                };
                return true;

            case SsaDynamicStorageMoveAtRValue moveAt
                when !moveAt.IsKnownInBounds
                     && TryResolveLocalAddressRoot(moveAt.StorageAddress, definitions, out var localName)
                     && state.TryGetValue(localName, out var current)
                     && CanProveMoveAtInBounds(current, moveAt.Index, values):
                optimized = valueInstruction with
                {
                    Value = moveAt with { IsKnownInBounds = true }
                };
                return true;

            default:
                return false;
        }
    }

    private static void ApplyInstructionTransfer(
        SsaInstruction instruction,
        Dictionary<string, SsaValueFacts> state,
        Dictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        Dictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        if (instruction is SsaValueInstruction valueForRange)
        {
            RecordDynamicIntegerRange(valueForRange, state, definitions, values, dynamicIntegerRanges);
        }

        switch (instruction)
        {
            case SsaStoreLocalInstruction storeLocal:
                ApplyStoreLocalTransfer(storeLocal, state, definitions, values);
                return;

            case SsaStoreGlobalInstruction storeGlobal:
                InvalidateEscapedRawPointer(state, storeGlobal.Value, definitions, values);
                return;

            case SsaStoreIndirectInstruction storeIndirect:
                if (TryApplyDynamicStorageLengthStore(storeIndirect, state, definitions, values, dynamicIntegerRanges))
                {
                    return;
                }

                InvalidateDynamicStorageLocalAddress(state, storeIndirect.Address, definitions);
                InvalidateEscapedRawPointer(state, storeIndirect.Value, definitions, values);
                return;

            case SsaCopyMemoryInstruction copyMemory:
                InvalidateDynamicStorageLocalAddress(state, copyMemory.DestinationAddress, definitions);
                return;

            case SsaCallInstruction call:
                InvalidateDirectCallDynamicStorageFacts(state, call, definitions, values, directCallParameterEffects);
                return;

            case SsaIndirectCallInstruction call:
                InvalidateIndirectCallDynamicStorageFacts(state, call, definitions, values);
                return;

            case SsaValueInstruction valueInstruction:
                ApplyValueTransfer(valueInstruction, state, conditionalMutations, definitions, values, directCallParameterEffects);
                return;
        }
    }

    private static void ApplyStoreLocalTransfer(
        SsaStoreLocalInstruction storeLocal,
        Dictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (storeLocal.LocalType.Kind == StarkTypeKind.Dynamic)
        {
            if (TryGetValueFacts(storeLocal.Value, values, out var storedFacts)
                && HasDynamicStoragePayload(storedFacts))
            {
                state[storeLocal.LocalName] = WithDynamicStorageOwnerRoot(
                    RenameFacts(storeLocal.LocalName, storedFacts, storeLocal.LocalType),
                    storeLocal.LocalName);
            }
            else
            {
                state.Remove(storeLocal.LocalName);
            }

            return;
        }

        InvalidateEscapedRawPointer(state, storeLocal.Value, definitions, values);
    }

    private static bool TryApplyDynamicStorageLengthStore(
        SsaStoreIndirectInstruction store,
        Dictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges)
    {
        if (!TryResolveDynamicStorageFieldAddress(
                store.Address,
                definitions,
                out var localName,
                out var storageType,
                out var fieldName,
                out var fieldIndex)
            || fieldIndex != 1
            || !string.Equals(fieldName, "Length", StringComparison.Ordinal)
            || !TryGetDynamicAwareIntegerRange(store.Value, values, dynamicIntegerRanges, out var lengthRange))
        {
            return false;
        }

        var before = state.TryGetValue(localName, out var current)
            ? current
            : new SsaValueFacts(localName, storageType);
        var updated = NormalizeDynamicStorageFacts(WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName) with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = ClampDynamicStorageCountRange(lengthRange),
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = ClampDynamicStorageCountRange(lengthRange)
        });
        state[localName] = updated;
        return true;
    }

    private static void ApplyValueTransfer(
        SsaValueInstruction valueInstruction,
        Dictionary<string, SsaValueFacts> state,
        Dictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        switch (valueInstruction.Value)
        {
            case SsaDynamicStorageReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName):
                ApplyUnconditionalMutation(
                    state,
                    localName,
                    reserve.StorageType,
                    current => ApplyReserveAdditionalFacts(current, reserve.AdditionalCapacity, values));
                return;

            case SsaDynamicStorageTryReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName):
                ApplyConditionalMutation(
                    state,
                    conditionalMutations,
                    valueInstruction.ResultName,
                    localName,
                    reserve.StorageType,
                    current => ApplyReserveAdditionalFacts(current, reserve.AdditionalCapacity, values));
                return;

            case SsaDynamicStorageTryReserveCapacityRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var localName):
                ApplyConditionalMutation(
                    state,
                    conditionalMutations,
                    valueInstruction.ResultName,
                    localName,
                    reserve.StorageType,
                    current => ApplyReserveCapacityFacts(current, reserve.TargetCapacity, values));
                return;

            case SsaDynamicStorageMoveLastRValue moveLast
                when TryResolveLocalAddressRoot(moveLast.StorageAddress, definitions, out var localName):
                ApplyUnconditionalMutation(state, localName, moveLast.StorageType, ApplyMoveOneFacts);
                return;

            case SsaDynamicStorageMoveAtRValue moveAt
                when TryResolveLocalAddressRoot(moveAt.StorageAddress, definitions, out var localName):
                ApplyUnconditionalMutation(state, localName, moveAt.StorageType, ApplyMoveOneFacts);
                return;

            case SsaDynamicStorageFreeRValue free:
                InvalidateFreedDynamicStorageOwner(state, free.Storage, values);
                return;

            case SsaCallRValue call:
                InvalidateDirectCallDynamicStorageFacts(state, call, definitions, values, directCallParameterEffects);
                return;

            case SsaIndirectCallRValue call:
                InvalidateIndirectCallDynamicStorageFacts(state, call, definitions, values);
                return;
        }
    }

    private static void InvalidateDirectCallDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            if (SsaDynamicStorageCallFactPolicy.ShouldPreserveDirectCallArgumentDynamicFacts(
                    call,
                    index,
                    directCallParameterEffects))
            {
                continue;
            }

            var argument = call.Arguments[index];
            InvalidateEscapedRawPointer(state, argument, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, argument, definitions);
        }

        for (var index = 0; index < (call.IndirectArgumentAddresses?.Count ?? 0); index++)
        {
            if (SsaDynamicStorageCallFactPolicy.ShouldPreserveDirectCallArgumentDynamicFacts(
                    call,
                    index,
                    directCallParameterEffects))
            {
                continue;
            }

            var address = call.IndirectArgumentAddresses![index];
            if (address is null)
            {
                continue;
            }

            InvalidateEscapedRawPointer(state, address, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, address, definitions);
        }
    }

    private static void InvalidateIndirectCallDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        ISsaIndirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        foreach (var argument in call.Arguments)
        {
            InvalidateEscapedRawPointer(state, argument, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, argument, definitions);
        }

        foreach (var address in call.IndirectArgumentAddresses ?? [])
        {
            if (address is null)
            {
                continue;
            }

            InvalidateEscapedRawPointer(state, address, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, address, definitions);
        }
    }

    private static void InvalidateEscapedRawPointer(
        Dictionary<string, SsaValueFacts> state,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (value.Type.Kind == StarkTypeKind.RawPointer
            && TryResolveLocalAddressRoot(value, definitions, out var localName))
        {
            RemoveOverlappingDynamicStorageFacts(state, localName);
        }

        if (TryResolveDynamicStorageViewOwnerRoot(
                value,
                definitions,
                values,
                new HashSet<string>(StringComparer.Ordinal),
                out var ownerRootName))
        {
            RemoveOverlappingDynamicStorageFacts(state, ownerRootName);
        }
    }

    private static void InvalidateDynamicStorageLocalAddress(
        Dictionary<string, SsaValueFacts> state,
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (TryResolveLocalAddressRoot(address, definitions, out var localName))
        {
            RemoveOverlappingDynamicStorageFacts(state, localName);
        }
    }

    private static bool TryResolveDynamicStorageViewOwnerRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        ownerRootName = string.Empty;

        switch (value)
        {
            case SsaValueReference reference:
                if (values.TryGetValue(reference.Name, out var facts)
                    && facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
                    && facts.DynamicStorageRegion?.OwnerRootName is { Length: > 0 } directOwnerRoot)
                {
                    ownerRootName = directOwnerRoot;
                    return true;
                }

                if (!visitedReferences.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveDynamicStorageViewOwnerRoot(
                    definition,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            default:
                return false;
        }
    }

    private static bool TryResolveDynamicStorageViewOwnerRoot(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveDynamicStorageViewOwnerRoot(
                    use.Value,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryResolveDynamicStorageViewOwnerRoot(
                    convert.Operand,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaExtractFieldRValue extractField
                when extractField.Target.Type.Kind == StarkTypeKind.Dynamic
                     && extractField.Type.Kind == StarkTypeKind.RawPointer
                     && IsDynamicDataField(extractField):
                return TryResolveDynamicStorageOwnerRoot(
                    extractField.Target,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaElementAddressRValue elementAddress:
                return TryResolveDynamicStorageViewOwnerRoot(
                    elementAddress.Address,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryResolveDynamicStorageViewOwnerRoot(
                    sliceElementAddress.Slice,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaMakeSliceFromPointerRValue makeSlice:
                return TryResolveDynamicStorageViewOwnerRoot(
                    makeSlice.Pointer,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            default:
                ownerRootName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageOwnerRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        ownerRootName = string.Empty;
        if (value is SsaValueReference reference)
        {
            if (values.TryGetValue(reference.Name, out var facts)
                && facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
                && facts.DynamicStorageRegion?.OwnerRootName is { Length: > 0 } ownerRoot)
            {
                ownerRootName = ownerRoot;
                return true;
            }

            if (visitedReferences.Add(reference.Name)
                && definitions.TryGetValue(reference.Name, out var definition))
            {
                return definition switch
                {
                    SsaUseRValue use => TryResolveDynamicStorageOwnerRoot(
                        use.Value,
                        definitions,
                        values,
                        visitedReferences,
                        out ownerRootName),
                    SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal =>
                        ReturnLocalName(loadLocal.LocalName, out ownerRootName),
                    _ => false
                };
            }
        }

        return false;
    }

    private static void ApplyUnconditionalMutation(
        Dictionary<string, SsaValueFacts> state,
        string localName,
        StarkTypeSymbol storageType,
        Func<SsaValueFacts, SsaValueFacts> mutate)
    {
        var before = state.TryGetValue(localName, out var current)
            ? current
            : new SsaValueFacts(localName, storageType);
        before = WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName);
        var after = WithDynamicStorageOwnerRoot(RenameFacts(localName, mutate(before), storageType), localName);
        if (HasDynamicStoragePayload(after))
        {
            state[localName] = after;
        }
        else
        {
            state.Remove(localName);
        }
    }

    private static void ApplyConditionalMutation(
        Dictionary<string, SsaValueFacts> state,
        Dictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        string resultName,
        string localName,
        StarkTypeSymbol storageType,
        Func<SsaValueFacts, SsaValueFacts> mutate)
    {
        var before = state.TryGetValue(localName, out var current)
            ? current
            : new SsaValueFacts(localName, storageType);
        before = WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName);
        var onSuccess = WithDynamicStorageOwnerRoot(RenameFacts(localName, mutate(before), storageType), localName);
        conditionalMutations[resultName] = new DynamicStorageLocalMutation(localName, before, onSuccess);

        var joined = JoinDynamicStorageFacts(localName, storageType, before, onSuccess);
        if (HasDynamicStoragePayload(joined))
        {
            state[localName] = joined;
        }
        else
        {
            state.Remove(localName);
        }
    }

    private static bool TryApplyDynamicStorageEdgeTransfer(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaValueFacts> exitState,
        IReadOnlyDictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> transferredState)
    {
        transferredState = exitState;
        Dictionary<string, SsaValueFacts>? edgeState = null;
        var currentState = exitState;

        if (conditionalMutations.Count != 0
            && TryResolveBranchConditionValue(terminator, target, definitions, out var conditionName, out var branchWhenConditionTrue)
            && conditionalMutations.TryGetValue(conditionName, out var mutation))
        {
            edgeState = CloneState(exitState);
            edgeState[mutation.LocalName] = branchWhenConditionTrue
                ? mutation.OnSuccess
                : mutation.Before;
            currentState = edgeState;
        }

        if (TryInferDynamicStorageLengthEdgeFacts(
                terminator,
                target,
                definitions,
                values,
                currentState,
                out var localName,
                out var refinedFacts,
                out var edgeIsReachable))
        {
            if (!edgeIsReachable)
            {
                return false;
            }

            edgeState ??= CloneState(exitState);
            edgeState[localName] = refinedFacts;
        }

        transferredState = edgeState ?? exitState;
        return true;
    }

    private static bool TryInferDynamicStorageLengthEdgeFacts(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        out string localName,
        out SsaValueFacts refinedFacts,
        out bool edgeIsReachable)
    {
        localName = string.Empty;
        refinedFacts = default!;
        edgeIsReachable = true;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchWhenTrue;
        if (target == terminator.Targets[0])
        {
            branchWhenTrue = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchWhenTrue = false;
        }
        else
        {
            return false;
        }

        if (!TryResolveBranchComparison(
                terminator,
                target,
                definitions,
                out var comparison,
                out branchWhenTrue))
        {
            return false;
        }

        return TryInferDynamicStorageLengthComparisonFacts(
                   comparison.Left,
                   comparison.Operator,
                   comparison.Right,
                   branchWhenTrue,
                   definitions,
                   values,
                   state,
                   out localName,
                   out refinedFacts,
                   out edgeIsReachable)
               || TryMirrorComparisonOperator(comparison.Operator, out var mirroredOperator)
               && TryInferDynamicStorageLengthComparisonFacts(
                   comparison.Right,
                   mirroredOperator,
                   comparison.Left,
                   branchWhenTrue,
                   definitions,
                   values,
                   state,
                   out localName,
                   out refinedFacts,
                   out edgeIsReachable);
    }

    private static bool TryResolveBranchComparison(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaBinaryRValue comparison,
        out bool branchWhenComparisonTrue)
    {
        comparison = default!;
        branchWhenComparisonTrue = false;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchTakesCondition;
        if (target == terminator.Targets[0])
        {
            branchTakesCondition = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchTakesCondition = false;
        }
        else
        {
            return false;
        }

        if (!TryResolveBooleanSource(
                terminator.Condition,
                branchTakesCondition,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var conditionName,
                out branchWhenComparisonTrue)
            || !definitions.TryGetValue(conditionName, out var definition)
            || definition is not SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool } resolvedComparison)
        {
            return false;
        }

        comparison = resolvedComparison;
        return true;
    }

    private static bool TryInferDynamicStorageLengthComparisonFacts(
        SsaValue lengthValue,
        SsaBinaryOperator comparisonOperator,
        SsaValue constant,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        out string localName,
        out SsaValueFacts refinedFacts,
        out bool edgeIsReachable)
    {
        localName = string.Empty;
        refinedFacts = default!;
        edgeIsReachable = true;
        if (!TryResolveDynamicStorageLengthValueRoot(lengthValue, definitions, out localName)
            || !TryGetIntegerSingleton(constant, values, out var constantValue)
            || !TryBuildComparisonRangeConstraint(
                comparisonOperator,
                constantValue,
                branchWhenTrue,
                out var min,
                out var max)
            || !state.TryGetValue(localName, out var current)
            || current.LengthKind != SsaFactLatticeKind.Known
            || current.LengthRange is not { } currentLength)
        {
            return false;
        }

        var refinedLength = new SsaIntegerRangeFact(
            min is { } lowerBound ? Max(currentLength.Min, lowerBound) : currentLength.Min,
            max is { } upperBound ? Min(currentLength.Max, upperBound) : currentLength.Max);
        if (refinedLength.Min > refinedLength.Max)
        {
            edgeIsReachable = false;
            return true;
        }

        refinedLength = ClampDynamicStorageCountRange(refinedLength);
        if (refinedLength.Min > refinedLength.Max)
        {
            edgeIsReachable = false;
            return true;
        }

        refinedFacts = NormalizeDynamicStorageFacts(current with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = refinedLength,
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = refinedLength
        });
        return true;
    }

    private static bool TryResolveDynamicStorageLengthValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        localName = string.Empty;
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        var visitedReferences = new HashSet<string>(StringComparer.Ordinal);
        var current = reference;
        while (visitedReferences.Add(current.Name)
               && definitions.TryGetValue(current.Name, out var definition))
        {
            if (TryResolveDynamicStorageLengthReadRoot(definition, definitions, out localName))
            {
                return true;
            }

            switch (definition)
            {
                case SsaUseRValue { Value: SsaValueReference next }:
                    current = next;
                    continue;
                case SsaConvertRValue { Operand: SsaValueReference next }:
                    current = next;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryGetIntegerSingleton(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out BigInteger singleton)
    {
        if (TryGetIntegerRange(value, values, out var range)
            && range.Min == range.Max)
        {
            singleton = range.Min;
            return true;
        }

        singleton = default;
        return false;
    }

    private static bool TryBuildComparisonRangeConstraint(
        SsaBinaryOperator comparisonOperator,
        BigInteger constant,
        bool branchWhenTrue,
        out BigInteger? min,
        out BigInteger? max)
    {
        min = null;
        max = null;

        switch (comparisonOperator)
        {
            case SsaBinaryOperator.LessThan:
                if (branchWhenTrue)
                {
                    max = constant - BigInteger.One;
                }
                else
                {
                    min = constant;
                }

                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                if (branchWhenTrue)
                {
                    max = constant;
                }
                else
                {
                    min = constant + BigInteger.One;
                }

                return true;
            case SsaBinaryOperator.GreaterThan:
                if (branchWhenTrue)
                {
                    min = constant + BigInteger.One;
                }
                else
                {
                    max = constant;
                }

                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                if (branchWhenTrue)
                {
                    min = constant;
                }
                else
                {
                    max = constant - BigInteger.One;
                }

                return true;
            case SsaBinaryOperator.Equal when branchWhenTrue:
            case SsaBinaryOperator.NotEqual when !branchWhenTrue:
                min = constant;
                max = constant;
                return true;
            default:
                return false;
        }
    }

    private static bool TryMirrorComparisonOperator(
        SsaBinaryOperator comparisonOperator,
        out SsaBinaryOperator mirroredOperator)
    {
        switch (comparisonOperator)
        {
            case SsaBinaryOperator.LessThan:
                mirroredOperator = SsaBinaryOperator.GreaterThan;
                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                mirroredOperator = SsaBinaryOperator.GreaterThanOrEqual;
                return true;
            case SsaBinaryOperator.GreaterThan:
                mirroredOperator = SsaBinaryOperator.LessThan;
                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                mirroredOperator = SsaBinaryOperator.LessThanOrEqual;
                return true;
            case SsaBinaryOperator.Equal:
            case SsaBinaryOperator.NotEqual:
                mirroredOperator = comparisonOperator;
                return true;
            default:
                mirroredOperator = default;
                return false;
        }
    }

    private static bool TryResolveBranchConditionValue(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string conditionName,
        out bool branchWhenConditionTrue)
    {
        conditionName = string.Empty;
        branchWhenConditionTrue = false;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchTakesCondition;
        if (target == terminator.Targets[0])
        {
            branchTakesCondition = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchTakesCondition = false;
        }
        else
        {
            return false;
        }

        return TryResolveBooleanSource(
            terminator.Condition,
            branchTakesCondition,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out conditionName,
            out branchWhenConditionTrue);
    }

    private static bool TryResolveBooleanSource(
        SsaValue value,
        bool branchTakesValue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string valueName,
        out bool branchWhenValueTrue)
    {
        valueName = string.Empty;
        branchWhenValueTrue = branchTakesValue;
        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name))
        {
            return false;
        }

        if (!definitions.TryGetValue(reference.Name, out var definition))
        {
            valueName = reference.Name;
            branchWhenValueTrue = branchTakesValue;
            return true;
        }

        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveBooleanSource(use.Value, branchTakesValue, definitions, visitedReferences, out valueName, out branchWhenValueTrue);
            case SsaUnaryRValue { Operator: SsaUnaryOperator.LogicalNot } unary:
                return TryResolveBooleanSource(unary.Operand, !branchTakesValue, definitions, visitedReferences, out valueName, out branchWhenValueTrue);
            default:
                valueName = reference.Name;
                branchWhenValueTrue = branchTakesValue;
                return true;
        }
    }

    private static bool MergeEntryState(
        int targetBlockId,
        IReadOnlyDictionary<string, SsaValueFacts> edgeState,
        Dictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> entryStates,
        HashSet<int> initializedEntries)
    {
        if (!initializedEntries.Add(targetBlockId))
        {
            var existingState = entryStates[targetBlockId];
            var joinedState = JoinStates(existingState, edgeState);
            if (StatesEqual(existingState, joinedState))
            {
                return false;
            }

            entryStates[targetBlockId] = joinedState;
            return true;
        }

        entryStates[targetBlockId] = CloneState(edgeState);
        return edgeState.Count != 0;
    }

    private static Dictionary<string, SsaValueFacts> JoinStates(
        IReadOnlyDictionary<string, SsaValueFacts> left,
        IReadOnlyDictionary<string, SsaValueFacts> right)
    {
        var joined = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        foreach (var (localName, leftFacts) in left)
        {
            if (!right.TryGetValue(localName, out var rightFacts)
                || leftFacts.Type.Kind != StarkTypeKind.Dynamic
                || rightFacts.Type != leftFacts.Type)
            {
                continue;
            }

            var localFacts = JoinDynamicStorageFacts(localName, leftFacts.Type, leftFacts, rightFacts);
            if (HasDynamicStoragePayload(localFacts))
            {
                joined[localName] = localFacts;
            }
        }

        return joined;
    }

    private static bool StatesEqual(
        IReadOnlyDictionary<string, SsaValueFacts> left,
        IReadOnlyDictionary<string, SsaValueFacts> right)
    {
        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var rightFacts)
                                   && EqualityComparer<SsaValueFacts>.Default.Equals(pair.Value, rightFacts));
    }

    private static Dictionary<string, SsaValueFacts> CloneState(IReadOnlyDictionary<string, SsaValueFacts> state)
    {
        return new Dictionary<string, SsaValueFacts>(state, StringComparer.Ordinal);
    }

    private static bool CanProveReserveAdditionalNoop(
        SsaValueFacts current,
        SsaValue additionalCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (current.LengthKind != SsaFactLatticeKind.Known
            || current.LengthRange is not { } lengthRange
            || current.CapacityKind != SsaFactLatticeKind.Known
            || current.CapacityRange is not { } capacityRange
            || !TryGetIntegerRange(additionalCapacity, values, out var additionalRange))
        {
            return false;
        }

        var requiredCapacity = Max(BigInteger.Zero, lengthRange.Max) + Max(BigInteger.Zero, additionalRange.Max);
        return capacityRange.Min >= requiredCapacity;
    }

    private static bool CanProveReserveCapacityNoop(
        SsaValueFacts current,
        SsaValue targetCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        return current.CapacityKind == SsaFactLatticeKind.Known
               && current.CapacityRange is { } capacityRange
               && TryGetIntegerRange(targetCapacity, values, out var targetRange)
               && capacityRange.Min >= Max(BigInteger.Zero, targetRange.Max);
    }

    private static bool CanProveDynamicStorageFreeNoop(
        SsaValue storage,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        return TryGetValueFacts(storage, values, out var facts)
               && facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
               && facts.DynamicStorageRegion is
               {
                   BackingAllocationKind: SsaDynamicStorageBackingAllocationKind.None
               };
    }

    private static bool CanProveMoveLastNonEmpty(SsaValueFacts current)
    {
        return current.LengthKind == SsaFactLatticeKind.Known
               && current.LengthRange is { } lengthRange
               && lengthRange.Min > BigInteger.Zero;
    }

    private static bool CanProveMoveAtInBounds(
        SsaValueFacts current,
        SsaValue index,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        return current.LengthKind == SsaFactLatticeKind.Known
               && current.LengthRange is { } lengthRange
               && lengthRange.Min > BigInteger.Zero
               && TryGetIntegerRange(index, values, out var indexRange)
               && Max(BigInteger.Zero, indexRange.Max) < lengthRange.Min;
    }

    private static SsaValueFacts ApplyReserveAdditionalFacts(
        SsaValueFacts current,
        SsaValue additionalCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (!TryGetIntegerRange(additionalCapacity, values, out var additionalRange))
        {
            return WithDynamicStorageBackingIdentityUnknown(current);
        }

        var provenNoop = current.LengthKind == SsaFactLatticeKind.Known
                         && current.LengthRange is { } currentLengthRange
                         && current.CapacityKind == SsaFactLatticeKind.Known
                         && current.CapacityRange is { } currentCapacityRange
                         && currentCapacityRange.Min >= Max(BigInteger.Zero, currentLengthRange.Max)
                            + Max(BigInteger.Zero, additionalRange.Max);
        BigInteger? requiredLowerBound = null;
        if (current.LengthKind == SsaFactLatticeKind.Known
            && current.LengthRange is { } lengthRange)
        {
            requiredLowerBound = Max(BigInteger.Zero, lengthRange.Min) + Max(BigInteger.Zero, additionalRange.Min);
        }

        var updated = NormalizeDynamicStorageFacts(RaiseCapacityLowerBound(current, requiredLowerBound));
        return provenNoop
            ? updated
            : WithDynamicStorageBackingIdentityUnknown(updated);
    }

    private static SsaValueFacts ApplyReserveCapacityFacts(
        SsaValueFacts current,
        SsaValue targetCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (!TryGetIntegerRange(targetCapacity, values, out var targetRange))
        {
            return WithDynamicStorageBackingIdentityUnknown(current);
        }

        var provenNoop = current.CapacityKind == SsaFactLatticeKind.Known
                         && current.CapacityRange is { } capacityRange
                         && capacityRange.Min >= Max(BigInteger.Zero, targetRange.Max);
        var updated = NormalizeDynamicStorageFacts(RaiseCapacityLowerBound(current, Max(BigInteger.Zero, targetRange.Min)));
        return provenNoop
            ? updated
            : WithDynamicStorageBackingIdentityUnknown(updated);
    }

    private static SsaValueFacts RaiseCapacityLowerBound(SsaValueFacts current, BigInteger? requiredLowerBound)
    {
        if (requiredLowerBound is null
            && current.CapacityKind != SsaFactLatticeKind.Known)
        {
            return current;
        }

        var upperBound = DynamicStorageCountUpperBound;
        var lowerBound = BigInteger.Zero;
        if (current.CapacityKind == SsaFactLatticeKind.Known
            && current.CapacityRange is { } capacityRange)
        {
            lowerBound = Max(BigInteger.Zero, capacityRange.Min);
        }

        if (requiredLowerBound is { } required)
        {
            lowerBound = Max(lowerBound, required);
        }

        lowerBound = Min(lowerBound, upperBound);
        return current with
        {
            CapacityKind = SsaFactLatticeKind.Known,
            CapacityRange = new SsaIntegerRangeFact(lowerBound, upperBound)
        };
    }

    private static SsaValueFacts ApplyMoveOneFacts(SsaValueFacts current)
    {
        if (current.LengthKind != SsaFactLatticeKind.Known
            || current.LengthRange is not { } lengthRange)
        {
            return current;
        }

        var newLength = new SsaIntegerRangeFact(
            lengthRange.Min > BigInteger.Zero ? lengthRange.Min - BigInteger.One : BigInteger.Zero,
            lengthRange.Max > BigInteger.Zero ? lengthRange.Max - BigInteger.One : BigInteger.Zero);

        return NormalizeDynamicStorageFacts(current with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = ClampDynamicStorageCountRange(newLength),
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = ClampDynamicStorageCountRange(newLength)
        });
    }

    private static void InvalidateFreedDynamicStorageOwner(
        Dictionary<string, SsaValueFacts> state,
        SsaValue storage,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (!TryGetValueFacts(storage, values, out var facts)
            || facts.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || facts.DynamicStorageRegion?.OwnerRootName is not { Length: > 0 } ownerRootName)
        {
            return;
        }

        RemoveOverlappingDynamicStorageFacts(state, ownerRootName);
    }

    private static SsaValueFacts NormalizeDynamicStorageFacts(SsaValueFacts facts)
    {
        if (facts.Type.Kind != StarkTypeKind.Dynamic)
        {
            return facts;
        }

        var normalized = facts;
        if (normalized.LengthKind == SsaFactLatticeKind.Known
            && normalized.LengthRange is { } lengthRange)
        {
            var clampedLength = ClampDynamicStorageCountRange(lengthRange);
            normalized = normalized with { LengthRange = clampedLength };
            if (normalized.InitializedPrefixKind != SsaFactLatticeKind.Known
                || normalized.InitializedPrefixRange is null)
            {
                normalized = normalized with
                {
                    InitializedPrefixKind = SsaFactLatticeKind.Known,
                    InitializedPrefixRange = clampedLength
                };
            }

            if (normalized.CapacityKind == SsaFactLatticeKind.Known
                && normalized.CapacityRange is { } capacityRange)
            {
                var clampedCapacity = ClampDynamicStorageCountRange(capacityRange);
                var capacityMin = Max(clampedCapacity.Min, clampedLength.Min);
                normalized = normalized with
                {
                    CapacityRange = new SsaIntegerRangeFact(capacityMin, Max(capacityMin, clampedCapacity.Max))
                };
            }
            else
            {
                normalized = normalized with
                {
                    CapacityKind = SsaFactLatticeKind.Known,
                    CapacityRange = new SsaIntegerRangeFact(clampedLength.Min, DynamicStorageCountUpperBound)
                };
            }
        }

        if (normalized.InitializedPrefixKind == SsaFactLatticeKind.Known
            && normalized.InitializedPrefixRange is { } initializedRange)
        {
            normalized = normalized with
            {
                InitializedPrefixRange = ClampDynamicStorageCountRange(initializedRange)
            };
        }

        return WithDynamicStorageRegionFromCurrentFacts(normalized);
    }

    private static SsaValueFacts WithDynamicStorageRegionFromCurrentFacts(SsaValueFacts facts)
    {
        if (facts.Type.Kind != StarkTypeKind.Dynamic
            || facts.Type.ElementType is not { } elementType)
        {
            return facts with
            {
                DynamicStorageRegionKind = SsaFactLatticeKind.Unknown,
                DynamicStorageRegion = null
            };
        }

        var existingRegion = facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
            ? facts.DynamicStorageRegion
            : null;
        var lengthRange = facts.LengthKind == SsaFactLatticeKind.Known
            ? facts.LengthRange
            : existingRegion?.InitializedLengthRange;
        var capacityRange = facts.CapacityKind == SsaFactLatticeKind.Known
            ? facts.CapacityRange
            : existingRegion?.CapacityRange;
        var initializedPrefixRange = facts.InitializedPrefixKind == SsaFactLatticeKind.Known
            ? facts.InitializedPrefixRange
            : existingRegion?.InitializedPrefixRange;

        if (existingRegion is null
            && lengthRange is null
            && capacityRange is null
            && initializedPrefixRange is null)
        {
            return facts;
        }

        var elementAlignmentBytes = TryGetTypeAlignmentBytes(elementType, out var alignmentBytes)
            ? alignmentBytes
            : existingRegion?.ElementAlignmentBytes;
        var backingKind = InferDynamicStorageBackingAllocationKind(
            existingRegion?.BackingAllocationKind ?? SsaDynamicStorageBackingAllocationKind.Unknown,
            capacityRange);
        var backingAllocationId = backingKind is SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
                or SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            ? existingRegion?.BackingAllocationId
            : null;

        return facts with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = new SsaDynamicStorageRegionFact(
                existingRegion?.OwnerRootName,
                backingKind,
                backingAllocationId,
                elementType,
                capacityRange,
                lengthRange,
                initializedPrefixRange,
                TryCreateDynamicStorageSpareCapacityRange(lengthRange, capacityRange),
                elementAlignmentBytes,
                existingRegion?.AllocatorProvenance ?? SsaDynamicStorageAllocatorProvenanceKind.Unknown)
        };
    }

    private static SsaValueFacts WithDynamicStorageOwnerRoot(SsaValueFacts facts, string ownerRootName)
    {
        var normalized = NormalizeDynamicStorageFacts(facts);
        if (normalized.Type.Kind != StarkTypeKind.Dynamic
            || normalized.Type.ElementType is not { } elementType)
        {
            return normalized;
        }

        var region = normalized.DynamicStorageRegionKind == SsaFactLatticeKind.Known
            ? normalized.DynamicStorageRegion
            : null;
        region ??= new SsaDynamicStorageRegionFact(
            ownerRootName,
            SsaDynamicStorageBackingAllocationKind.Unknown,
            null,
            elementType,
            null,
            null,
            null,
            null,
            TryGetTypeAlignmentBytes(elementType, out var alignmentBytes) ? alignmentBytes : null,
            SsaDynamicStorageAllocatorProvenanceKind.Unknown);

        return normalized with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = region with { OwnerRootName = ownerRootName }
        };
    }

    private static SsaValueFacts WithDynamicStorageBackingIdentityUnknown(SsaValueFacts facts)
    {
        var normalized = NormalizeDynamicStorageFacts(facts);
        if (normalized.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || normalized.DynamicStorageRegion is not { } region)
        {
            return normalized;
        }

        var backingKind = InferDynamicStorageBackingAllocationKind(
            SsaDynamicStorageBackingAllocationKind.Unknown,
            region.CapacityRange);
        return normalized with
        {
            DynamicStorageRegion = region with
            {
                BackingAllocationKind = backingKind,
                BackingAllocationId = null
            }
        };
    }

    private static SsaDynamicStorageBackingAllocationKind InferDynamicStorageBackingAllocationKind(
        SsaDynamicStorageBackingAllocationKind existingKind,
        SsaIntegerRangeFact? capacityRange)
    {
        if (capacityRange is null)
        {
            return existingKind;
        }

        if (capacityRange.Max <= BigInteger.Zero)
        {
            return SsaDynamicStorageBackingAllocationKind.None;
        }

        if (capacityRange.Min <= BigInteger.Zero)
        {
            return SsaDynamicStorageBackingAllocationKind.Unknown;
        }

        return existingKind == SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            ? SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            : SsaDynamicStorageBackingAllocationKind.RuntimeAllocation;
    }

    private static SsaIntegerRangeFact? TryCreateDynamicStorageSpareCapacityRange(
        SsaIntegerRangeFact? lengthRange,
        SsaIntegerRangeFact? capacityRange)
    {
        if (lengthRange is null || capacityRange is null)
        {
            return null;
        }

        var min = Max(BigInteger.Zero, capacityRange.Min - lengthRange.Max);
        var max = Max(BigInteger.Zero, capacityRange.Max - lengthRange.Min);
        return new SsaIntegerRangeFact(min, Max(min, max));
    }

    private static SsaValueFacts JoinDynamicStorageFacts(
        string valueName,
        StarkTypeSymbol type,
        SsaValueFacts left,
        SsaValueFacts right)
    {
        var result = new SsaValueFacts(valueName, type);
        if (left.LengthKind == SsaFactLatticeKind.Known
            && left.LengthRange is { } leftLength
            && right.LengthKind == SsaFactLatticeKind.Known
            && right.LengthRange is { } rightLength)
        {
            result = result with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = ClampDynamicStorageCountRange(new SsaIntegerRangeFact(
                    Min(leftLength.Min, rightLength.Min),
                    Max(leftLength.Max, rightLength.Max)))
            };
        }

        if (left.CapacityKind == SsaFactLatticeKind.Known
            && left.CapacityRange is { } leftCapacity
            && right.CapacityKind == SsaFactLatticeKind.Known
            && right.CapacityRange is { } rightCapacity)
        {
            result = result with
            {
                CapacityKind = SsaFactLatticeKind.Known,
                CapacityRange = ClampDynamicStorageCountRange(new SsaIntegerRangeFact(
                    Min(leftCapacity.Min, rightCapacity.Min),
                    Max(leftCapacity.Max, rightCapacity.Max)))
            };
        }

        if (left.InitializedPrefixKind == SsaFactLatticeKind.Known
            && left.InitializedPrefixRange is { } leftPrefix
            && right.InitializedPrefixKind == SsaFactLatticeKind.Known
            && right.InitializedPrefixRange is { } rightPrefix)
        {
            result = result with
            {
                InitializedPrefixKind = SsaFactLatticeKind.Known,
                InitializedPrefixRange = ClampDynamicStorageCountRange(new SsaIntegerRangeFact(
                    Min(leftPrefix.Min, rightPrefix.Min),
                    Max(leftPrefix.Max, rightPrefix.Max)))
            };
        }

        result = NormalizeDynamicStorageFacts(result);
        if (left.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || left.DynamicStorageRegion is not { } leftRegion
            || right.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || right.DynamicStorageRegion is not { } rightRegion
            || type.ElementType is not { } elementType)
        {
            return result;
        }

        var currentRegion = result.DynamicStorageRegion;
        var sameOwnerRoot = string.Equals(leftRegion.OwnerRootName, rightRegion.OwnerRootName, StringComparison.Ordinal);
        var sameAllocator = leftRegion.AllocatorProvenance == rightRegion.AllocatorProvenance;
        var sameBackingIdentity = leftRegion.BackingAllocationKind == rightRegion.BackingAllocationKind
                                  && string.Equals(leftRegion.BackingAllocationId, rightRegion.BackingAllocationId, StringComparison.Ordinal);
        var backingKind = sameBackingIdentity
            ? leftRegion.BackingAllocationKind
            : InferDynamicStorageBackingAllocationKind(
                SsaDynamicStorageBackingAllocationKind.Unknown,
                result.CapacityKind == SsaFactLatticeKind.Known ? result.CapacityRange : null);

        return result with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = new SsaDynamicStorageRegionFact(
                sameOwnerRoot ? leftRegion.OwnerRootName : null,
                backingKind,
                sameBackingIdentity && backingKind is SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
                    or SsaDynamicStorageBackingAllocationKind.ArenaAllocation
                    ? leftRegion.BackingAllocationId
                    : null,
                elementType,
                currentRegion?.CapacityRange,
                currentRegion?.InitializedLengthRange,
                currentRegion?.InitializedPrefixRange,
                currentRegion?.SpareCapacityRange,
                TryGetTypeAlignmentBytes(elementType, out var alignmentBytes)
                    ? alignmentBytes
                    : currentRegion?.ElementAlignmentBytes,
                sameAllocator ? leftRegion.AllocatorProvenance : SsaDynamicStorageAllocatorProvenanceKind.Unknown)
        };
    }

    private static bool TryGetValueFacts(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out SsaValueFacts facts)
    {
        if (value is SsaValueReference reference
            && values.TryGetValue(reference.Name, out facts!))
        {
            return true;
        }

        if (value is SsaZeroInitializerValue { Type.Kind: StarkTypeKind.Dynamic })
        {
            facts = CreateEmptyDynamicStorageFacts(value.Text, value.Type);
            return true;
        }

        facts = new SsaValueFacts(value.Text, value.Type);
        return false;
    }

    private static void RecordDynamicIntegerRange(
        SsaValueInstruction instruction,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        Dictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges)
    {
        if (TryEvaluateDynamicStorageIntegerRange(
                instruction.Value,
                state,
                definitions,
                values,
                dynamicIntegerRanges,
                out var range))
        {
            dynamicIntegerRanges[instruction.ResultName] = ClampDynamicStorageCountRange(range);
            return;
        }

        dynamicIntegerRanges.Remove(instruction.ResultName);
    }

    private static bool TryEvaluateDynamicStorageIntegerRange(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        out SsaIntegerRangeFact range)
    {
        if (TryResolveDynamicStorageLengthReadRoot(value, definitions, out var localName)
            && state.TryGetValue(localName, out var current)
            && current.LengthKind == SsaFactLatticeKind.Known
            && current.LengthRange is { } lengthRange)
        {
            range = ClampDynamicStorageCountRange(lengthRange);
            return true;
        }

        switch (value)
        {
            case SsaUseRValue use:
                return TryGetDynamicAwareIntegerRange(use.Value, values, dynamicIntegerRanges, out range);
            case SsaConvertRValue convert
                when convert.TargetType.Kind == StarkTypeKind.Integer
                     && convert.Operand.Type == convert.TargetType:
                return TryGetDynamicAwareIntegerRange(convert.Operand, values, dynamicIntegerRanges, out range);
            case SsaBinaryRValue binary
                when binary.Type.Kind == StarkTypeKind.Integer
                     && TryGetDynamicAwareIntegerRange(binary.Left, values, dynamicIntegerRanges, out var left)
                     && TryGetDynamicAwareIntegerRange(binary.Right, values, dynamicIntegerRanges, out var right):
                return TryEvaluateIntegerBinaryRange(binary.Operator, left, right, out range);
            default:
                range = default!;
                return false;
        }
    }

    private static bool TryEvaluateIntegerBinaryRange(
        SsaBinaryOperator operation,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        switch (operation)
        {
            case SsaBinaryOperator.Add:
                range = new SsaIntegerRangeFact(left.Min + right.Min, left.Max + right.Max);
                return true;
            case SsaBinaryOperator.Subtract:
                range = new SsaIntegerRangeFact(left.Min - right.Max, left.Max - right.Min);
                return true;
            default:
                range = default!;
                return false;
        }
    }

    private static bool TryGetDynamicAwareIntegerRange(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        out SsaIntegerRangeFact range)
    {
        if (value is SsaValueReference reference
            && dynamicIntegerRanges.TryGetValue(reference.Name, out range!))
        {
            return true;
        }

        return TryGetIntegerRange(value, values, out range);
    }

    private static bool TryResolveDynamicStorageLengthReadRoot(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        switch (value)
        {
            case SsaExtractFieldRValue extractField
                when IsDynamicLengthField(extractField)
                     && TryResolveDynamicStorageValueRoot(extractField.Target, definitions, out localName):
                return true;
            case SsaLoadIndirectRValue loadIndirect
                when TryResolveDynamicStorageFieldAddress(
                         loadIndirect.Address,
                         definitions,
                         out localName,
                         out _,
                         out var fieldName,
                         out var fieldIndex)
                     && IsDynamicLengthField(fieldName, fieldIndex):
                return true;
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveDynamicStorageValueRoot(
            value,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out localName);
    }

    private static bool TryResolveDynamicStorageValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName)
    {
        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            localName = string.Empty;
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => TryResolveDynamicStorageValueRoot(
                use.Value,
                definitions,
                visitedReferences,
                out localName),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal =>
                ReturnLocalName(loadLocal.LocalName, out localName),
            SsaLoadIndirectRValue { Type.Kind: StarkTypeKind.Dynamic } loadIndirect =>
                TryResolveLocalAddressRoot(loadIndirect.Address, definitions, visitedReferences, out localName),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Dynamic =>
                TryResolveDynamicStorageValueRoot(
                    convert.Operand,
                    definitions,
                    visitedReferences,
                    out localName),
            _ => ReturnNoLocalName(out localName)
        };
    }

    private static bool TryGetIntegerRange(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out SsaIntegerRangeFact range)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                range = new SsaIntegerRangeFact(integer.Value, integer.Value);
                return true;
            case SsaValueReference reference
                when values.TryGetValue(reference.Name, out var facts)
                     && facts.IntegerRangeKind == SsaFactLatticeKind.Known
                     && facts.IntegerRange is { } knownRange:
                range = knownRange;
                return true;
            default:
                range = default!;
                return false;
        }
    }

    private static bool TryResolveLocalAddressRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveLocalAddressRoot(value, definitions, new HashSet<string>(StringComparer.Ordinal), out localName);
    }

    private static bool TryResolveLocalAddressRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName)
    {
        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedReferences.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    localName = string.Empty;
                    return false;
                }

                return definition switch
                {
                    SsaAddressOfLocalRValue addressOfLocal => ReturnLocalName(addressOfLocal.LocalName, out localName),
                    SsaAddressOfParameterRValue addressOfParameter => ReturnLocalName(addressOfParameter.ParameterName, out localName),
                    SsaUseRValue use => TryResolveLocalAddressRoot(use.Value, definitions, visitedReferences, out localName),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer
                        => TryResolveLocalAddressRoot(convert.Operand, definitions, visitedReferences, out localName),
                    SsaLoadIndirectRValue { Type.Kind: StarkTypeKind.Dynamic } loadIndirect
                        => TryResolveLocalAddressRoot(loadIndirect.Address, definitions, visitedReferences, out localName),
                    SsaFieldAddressRValue fieldAddress
                        when TryResolveLocalAddressRoot(fieldAddress.Address, definitions, visitedReferences, out var parentRoot) =>
                        ReturnLocalName($"{parentRoot}.{fieldAddress.FieldName}", out localName),
                    SsaElementAddressRValue elementAddress
                        when TryResolveLocalAddressRoot(elementAddress.Address, definitions, visitedReferences, out var parentRoot) =>
                        ReturnLocalName($"{parentRoot}[*]", out localName),
                    _ => ReturnNoLocalName(out localName)
                };
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageFieldAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName,
        out StarkTypeSymbol storageType,
        out string fieldName,
        out int fieldIndex)
    {
        return TryResolveDynamicStorageFieldAddress(
            value,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out localName,
            out storageType,
            out fieldName,
            out fieldIndex);
    }

    private static bool TryResolveDynamicStorageFieldAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName,
        out StarkTypeSymbol storageType,
        out string fieldName,
        out int fieldIndex)
    {
        localName = string.Empty;
        storageType = StarkTypeSymbols.Error;
        fieldName = string.Empty;
        fieldIndex = -1;

        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveDynamicStorageFieldAddress(
                    use.Value,
                    definitions,
                    visitedReferences,
                    out localName,
                    out storageType,
                    out fieldName,
                    out fieldIndex);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryResolveDynamicStorageFieldAddress(
                    convert.Operand,
                    definitions,
                    visitedReferences,
                    out localName,
                    out storageType,
                    out fieldName,
                    out fieldIndex);
            case SsaFieldAddressRValue fieldAddress
                when fieldAddress.AggregateType.Kind == StarkTypeKind.Dynamic
                     && TryResolveLocalAddressRoot(fieldAddress.Address, definitions, out localName):
                storageType = fieldAddress.AggregateType;
                fieldName = fieldAddress.FieldName;
                fieldIndex = fieldAddress.FieldIndex;
                return true;
            default:
                return false;
        }
    }

    private static SsaValueFacts RenameFacts(string valueName, SsaValueFacts facts, StarkTypeSymbol type)
    {
        return NormalizeDynamicStorageFacts(facts with
        {
            ValueName = valueName,
            Type = type
        });
    }

    private static bool HasDynamicStoragePayload(SsaValueFacts facts)
    {
        return facts.Type.Kind == StarkTypeKind.Dynamic
               && (facts.LengthKind == SsaFactLatticeKind.Known
                   || facts.CapacityKind == SsaFactLatticeKind.Known
                   || facts.InitializedPrefixKind == SsaFactLatticeKind.Known
                   || facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known);
    }

    private static SsaValueFacts CreateEmptyDynamicStorageFacts(string valueName, StarkTypeSymbol type)
    {
        var zero = new SsaIntegerRangeFact(BigInteger.Zero, BigInteger.Zero);
        return NormalizeDynamicStorageFacts(new SsaValueFacts(
            valueName,
            type,
            LengthKind: SsaFactLatticeKind.Known,
            LengthRange: zero,
            CapacityKind: SsaFactLatticeKind.Known,
            CapacityRange: zero,
            InitializedPrefixKind: SsaFactLatticeKind.Known,
            InitializedPrefixRange: zero,
            DynamicStorageRegionKind: SsaFactLatticeKind.Known,
            DynamicStorageRegion: type.ElementType is { } elementType
                ? new SsaDynamicStorageRegionFact(
                    null,
                    SsaDynamicStorageBackingAllocationKind.None,
                    null,
                    elementType,
                    zero,
                    zero,
                    zero,
                    zero,
                    TryGetTypeAlignmentBytes(elementType, out var alignmentBytes) ? alignmentBytes : null,
                    SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault)
                : null));
    }

    private static bool TryGetTypeAlignmentBytes(StarkTypeSymbol type, out int alignmentBytes)
    {
        var concreteType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        switch (concreteType.Kind)
        {
            case StarkTypeKind.Integer or StarkTypeKind.Float when concreteType.BitWidth is int bitWidth:
                alignmentBytes = GetPortableScalarAlignmentBytes(bitWidth);
                return alignmentBytes > 1;
            case StarkTypeKind.FixedArray when concreteType.ElementType is not null:
                return TryGetTypeAlignmentBytes(concreteType.ElementType, out alignmentBytes);
            default:
                alignmentBytes = 1;
                return false;
        }
    }

    private static int GetPortableScalarAlignmentBytes(int bitWidth)
    {
        if (bitWidth <= 8)
        {
            return 1;
        }

        if (bitWidth <= 16)
        {
            return 2;
        }

        return 4;
    }

    private static SsaIntegerRangeFact ClampDynamicStorageCountRange(SsaIntegerRangeFact range)
    {
        var lower = Max(BigInteger.Zero, range.Min);
        var upper = Min(DynamicStorageCountUpperBound, Max(lower, range.Max));
        return new SsaIntegerRangeFact(lower, upper);
    }

    private static BigInteger DynamicStorageCountUpperBound => (BigInteger.One << 63) - BigInteger.One;

    private static BigInteger Min(BigInteger left, BigInteger right) => left <= right ? left : right;

    private static BigInteger Max(BigInteger left, BigInteger right) => left >= right ? left : right;

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

    private static void RemoveOverlappingDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        string escapedRoot)
    {
        foreach (var ownerRoot in state.Keys
                     .Where(ownerRoot => DynamicStorageOwnerRootsMayOverlap(ownerRoot, escapedRoot))
                     .ToArray())
        {
            state.Remove(ownerRoot);
        }
    }

    private static bool DynamicStorageOwnerRootsMayOverlap(string ownerRoot, string escapedRoot)
    {
        return string.Equals(ownerRoot, escapedRoot, StringComparison.Ordinal)
               || ownerRoot.StartsWith($"{escapedRoot}.", StringComparison.Ordinal)
               || escapedRoot.StartsWith($"{ownerRoot}.", StringComparison.Ordinal)
               || ownerRoot.StartsWith($"{escapedRoot}[*]", StringComparison.Ordinal)
               || escapedRoot.StartsWith($"{ownerRoot}[*]", StringComparison.Ordinal);
    }

    private static bool IsDynamicDataField(SsaExtractFieldRValue extractField)
    {
        return extractField.FieldIndex == 0
               || string.Equals(extractField.FieldName, "Data", StringComparison.Ordinal)
               || string.Equals(extractField.FieldName, "data", StringComparison.Ordinal);
    }

    private static bool IsDynamicLengthField(SsaExtractFieldRValue extractField)
    {
        return IsDynamicLengthField(extractField.FieldName, extractField.FieldIndex);
    }

    private static bool IsDynamicLengthField(string fieldName, int fieldIndex)
    {
        return fieldIndex == 1
               || string.Equals(fieldName, "Length", StringComparison.Ordinal)
               || string.Equals(fieldName, "length", StringComparison.Ordinal);
    }
}
