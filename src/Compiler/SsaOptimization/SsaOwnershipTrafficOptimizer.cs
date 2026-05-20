namespace Stark.Compiler;

internal sealed class SsaOwnershipTrafficOptimizer
{
    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private static SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var candidateRoots = CollectCandidateRoots(function);
        if (candidateRoots.Count == 0)
        {
            return function;
        }

        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
        var blockTransfers = ComputeBlockTransfers(function, definitions, candidateRoots);
        var liveOut = ComputeLiveOut(function, blockTransfers);

        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            blocks.Add(RemoveDeadOwnershipTraffic(block, definitions, candidateRoots, liveOut[block.Id], ref changed));
        }

        return changed
            ? function with { Blocks = blocks.ToArray() }
            : function;
    }

    private static IReadOnlySet<string> CollectCandidateRoots(SsaFunction function)
    {
        if (function.Ownership is not { } ownership)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return ownership.Roots
            .Where(static root => root.RootKind == OwnershipStorageRootKind.Local
                                  && !root.IsAddressTaken
                                  && !root.HasRawPointerEscape)
            .Select(static root => root.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<int, BlockTransfer> ComputeBlockTransfers(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots)
    {
        var transfers = new Dictionary<int, BlockTransfer>();
        foreach (var block in function.Blocks)
        {
            var use = new HashSet<string>(StringComparer.Ordinal);
            var def = new HashSet<string>(StringComparer.Ordinal);
            var defined = new HashSet<string>(StringComparer.Ordinal);

            foreach (var instruction in block.Instructions)
            {
                AddInstructionUseDef(instruction, definitions, candidateRoots, use, def, defined);
            }

            transfers[block.Id] = new BlockTransfer(use, def);
        }

        return transfers;
    }

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> ComputeLiveOut(
        SsaFunction function,
        IReadOnlyDictionary<int, BlockTransfer> transfers)
    {
        var successors = function.Blocks.ToDictionary(
            static block => block.Id,
            static block => block.Terminator.Targets.Distinct().ToArray());
        var liveIn = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal));
        var liveOut = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal));

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var blockIndex = function.Blocks.Count - 1; blockIndex >= 0; blockIndex--)
            {
                var block = function.Blocks[blockIndex];
                var nextLiveOut = new HashSet<string>(StringComparer.Ordinal);
                foreach (var successor in successors[block.Id])
                {
                    if (liveIn.TryGetValue(successor, out var successorLiveIn))
                    {
                        nextLiveOut.UnionWith(successorLiveIn);
                    }
                }

                if (!nextLiveOut.SetEquals(liveOut[block.Id]))
                {
                    liveOut[block.Id] = nextLiveOut;
                    changed = true;
                }

                var transfer = transfers[block.Id];
                var nextLiveIn = new HashSet<string>(transfer.Use, StringComparer.Ordinal);
                foreach (var root in liveOut[block.Id])
                {
                    if (!transfer.Def.Contains(root))
                    {
                        nextLiveIn.Add(root);
                    }
                }

                if (!nextLiveIn.SetEquals(liveIn[block.Id]))
                {
                    liveIn[block.Id] = nextLiveIn;
                    changed = true;
                }
            }
        }

        return liveOut.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<string>)pair.Value,
            EqualityComparer<int>.Default);
    }

    private static SsaBasicBlock RemoveDeadOwnershipTraffic(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        IReadOnlySet<string> liveOut,
        ref bool changed)
    {
        var live = new HashSet<string>(liveOut, StringComparer.Ordinal);
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        var blockChanged = false;

        for (var index = block.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = block.Instructions[index];
            if (IsDeadMoveInvalidationStore(instruction, candidateRoots, live)
                || IsDeadAggregateCopy(instruction, definitions, candidateRoots, live))
            {
                blockChanged = true;
                continue;
            }

            ApplyReverseTransfer(instruction, definitions, candidateRoots, live);
            instructions.Add(instruction);
        }

        if (!blockChanged)
        {
            return block;
        }

        changed = true;
        instructions.Reverse();
        return block with { Instructions = instructions.ToArray() };
    }

    private static bool IsDeadMoveInvalidationStore(
        SsaInstruction instruction,
        IReadOnlySet<string> candidateRoots,
        IReadOnlySet<string> live)
    {
        return instruction is SsaStoreLocalInstruction { Value: SsaUndefValue } store
               && candidateRoots.Contains(store.LocalName)
               && !live.Contains(store.LocalName);
    }

    private static bool IsDeadAggregateCopy(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        IReadOnlySet<string> live)
    {
        return instruction is SsaCopyMemoryInstruction copy
               && TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var localName)
               && candidateRoots.Contains(localName)
               && !live.Contains(localName);
    }

    private static void AddInstructionUseDef(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        HashSet<string> use,
        HashSet<string> def,
        HashSet<string> defined)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                AddRValueUses(valueInstruction.Value, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                break;

            case SsaStoreLocalInstruction storeLocal:
                AddAddressUse(storeLocal.Value, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                AddWholeLocalDefinition(storeLocal.LocalName, candidateRoots, def, defined);
                break;

            case SsaStoreIndirectInstruction storeIndirect:
                AddAddressUse(storeIndirect.Address, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                AddAddressUse(storeIndirect.Value, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                break;

            case SsaStoreGlobalInstruction storeGlobal:
                AddAddressUse(storeGlobal.Value, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                break;

            case SsaCopyMemoryInstruction copy:
                AddAddressUse(copy.SourceAddress, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                if (TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var destinationLocal))
                {
                    AddWholeLocalDefinition(destinationLocal, candidateRoots, def, defined);
                }
                break;

            case SsaCallInstruction call:
                AddCallUses(call, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                break;

            case SsaIndirectCallInstruction call:
                AddCallUses(call, definitions, candidateRoots, root =>
                {
                    if (!defined.Contains(root))
                    {
                        use.Add(root);
                    }
                });
                break;

            case SsaLifetimeEndInstruction lifetimeEnd:
                AddWholeLocalDefinition(lifetimeEnd.LocalName, candidateRoots, def, defined);
                break;

            case SsaDeallocateLocalInstruction deallocate:
                AddWholeLocalDefinition(deallocate.LocalName, candidateRoots, def, defined);
                break;
        }
    }

    private static void ApplyReverseTransfer(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        HashSet<string> live)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                AddRValueUses(valueInstruction.Value, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaStoreLocalInstruction storeLocal:
                live.Remove(storeLocal.LocalName);
                AddAddressUse(storeLocal.Value, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaStoreIndirectInstruction storeIndirect:
                AddAddressUse(storeIndirect.Address, definitions, candidateRoots, root => live.Add(root));
                AddAddressUse(storeIndirect.Value, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaStoreGlobalInstruction storeGlobal:
                AddAddressUse(storeGlobal.Value, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaCopyMemoryInstruction copy:
                if (TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var destinationLocal))
                {
                    live.Remove(destinationLocal);
                }

                AddAddressUse(copy.SourceAddress, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaCallInstruction call:
                AddCallUses(call, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaIndirectCallInstruction call:
                AddCallUses(call, definitions, candidateRoots, root => live.Add(root));
                break;

            case SsaLifetimeEndInstruction lifetimeEnd:
                live.Remove(lifetimeEnd.LocalName);
                break;

            case SsaDeallocateLocalInstruction deallocate:
                live.Remove(deallocate.LocalName);
                break;
        }
    }

    private static void AddRValueUses(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        Action<string> addUse)
    {
        switch (value)
        {
            case SsaLoadLocalRValue loadLocal:
                AddUse(loadLocal.LocalName, candidateRoots, addUse);
                break;
            case SsaMakeSliceFromLocalRValue slice:
                AddUse(slice.LocalName, candidateRoots, addUse);
                break;
            case SsaLoadIndirectRValue load:
                AddAddressUse(load.Address, definitions, candidateRoots, addUse);
                break;
            case SsaDynamicStorageReserveRValue reserve:
                AddAddressUse(reserve.StorageAddress, definitions, candidateRoots, addUse);
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                AddAddressUse(reserve.StorageAddress, definitions, candidateRoots, addUse);
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                AddAddressUse(reserve.StorageAddress, definitions, candidateRoots, addUse);
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                AddAddressUse(moveLast.StorageAddress, definitions, candidateRoots, addUse);
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                AddAddressUse(moveAt.StorageAddress, definitions, candidateRoots, addUse);
                break;
            case SsaCallRValue call:
                AddCallUses(call, definitions, candidateRoots, addUse);
                break;
            case SsaIndirectCallRValue call:
                AddCallUses(call, definitions, candidateRoots, addUse);
                break;
        }
    }

    private static void AddCallUses(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        Action<string> addUse)
    {
        foreach (var argument in call.Arguments)
        {
            AddAddressUse(argument, definitions, candidateRoots, addUse);
        }

        foreach (var localName in call.IndirectArgumentLocalNames ?? [])
        {
            AddUse(localName, candidateRoots, addUse);
        }

        foreach (var address in call.IndirectArgumentAddresses ?? [])
        {
            if (address is not null)
            {
                AddAddressUse(address, definitions, candidateRoots, addUse);
            }
        }
    }

    private static void AddCallUses(
        ISsaIndirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        Action<string> addUse)
    {
        foreach (var argument in call.Arguments)
        {
            AddAddressUse(argument, definitions, candidateRoots, addUse);
        }

        foreach (var localName in call.IndirectArgumentLocalNames ?? [])
        {
            AddUse(localName, candidateRoots, addUse);
        }

        foreach (var address in call.IndirectArgumentAddresses ?? [])
        {
            if (address is not null)
            {
                AddAddressUse(address, definitions, candidateRoots, addUse);
            }
        }
    }

    private static void AddAddressUse(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> candidateRoots,
        Action<string> addUse)
    {
        if (TryResolveDirectLocalAddress(address, definitions, out var localName))
        {
            AddUse(localName, candidateRoots, addUse);
        }
    }

    private static void AddUse(string? localName, IReadOnlySet<string> candidateRoots, Action<string> addUse)
    {
        if (localName is not null && candidateRoots.Contains(localName))
        {
            addUse(localName);
        }
    }

    private static void AddWholeLocalDefinition(
        string localName,
        IReadOnlySet<string> candidateRoots,
        HashSet<string> def,
        HashSet<string> defined)
    {
        if (!candidateRoots.Contains(localName))
        {
            return;
        }

        defined.Add(localName);
        def.Add(localName);
    }

    private static bool TryResolveDirectLocalAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveDirectLocalAddress(value, definitions, [], out localName);
    }

    private static bool TryResolveDirectLocalAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        HashSet<string> visited,
        out string localName)
    {
        localName = string.Empty;
        if (value is not SsaValueReference reference
            || !visited.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaAddressOfLocalRValue address:
                localName = address.LocalName;
                return true;
            case SsaFieldAddressRValue field:
                return TryResolveDirectLocalAddress(field.Address, definitions, visited, out localName);
            case SsaElementAddressRValue element:
                return TryResolveDirectLocalAddress(element.Address, definitions, visited, out localName);
            case SsaUseRValue use:
                return TryResolveDirectLocalAddress(use.Value, definitions, visited, out localName);
            case SsaConvertRValue convert:
                return TryResolveDirectLocalAddress(convert.Operand, definitions, visited, out localName);
            default:
                return false;
        }
    }

    private sealed record BlockTransfer(
        IReadOnlySet<string> Use,
        IReadOnlySet<string> Def);
}
