using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaScalarReplacementOptimizer
{
    private readonly FunctionEffectModel? _effectModel;

    private readonly record struct FieldKey(
        string LocalName,
        string FieldPath,
        StarkTypeSymbol Type);

    private readonly record struct LocalMemoryReadSet(
        IReadOnlySet<string> LocalRoots,
        IReadOnlySet<FieldKey> ExactFields);

    public SsaScalarReplacementOptimizer(FunctionEffectModel? effectModel = null)
    {
        _effectModel = effectModel;
    }

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

    private SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
        var phiDefinitions = function.Blocks
            .SelectMany(static block => block.Phis)
            .ToDictionary(static phi => phi.ResultName, static phi => phi, StringComparer.Ordinal);

        var eligibleAggregateRoots = CollectEligibleStackAggregateLocals(function, definitions, phiDefinitions);
        if (eligibleAggregateRoots.Count == 0)
        {
            return function;
        }

        var changed = false;
        var optimizedFunction = function;
        var copiedAggregateRoots = CollectCopiedAggregateDestinationRoots(
            function,
            definitions,
            eligibleAggregateRoots);
        if (copiedAggregateRoots.Count > 0)
        {
            optimizedFunction = EliminateDeadAggregateCopies(
                optimizedFunction,
                definitions,
                copiedAggregateRoots,
                out var aggregateCopyChanged);
            changed |= aggregateCopyChanged;
        }

        var storedFields = CollectStoredScalarFields(
            optimizedFunction,
            definitions,
            eligibleAggregateRoots);
        if (storedFields.Count == 0)
        {
            return changed ? optimizedFunction : function;
        }

        var liveOutByBlock = ComputeFieldLiveOut(optimizedFunction, definitions, storedFields);
        var blocks = new List<SsaBasicBlock>(optimizedFunction.Blocks.Count);
        foreach (var block in optimizedFunction.Blocks)
        {
            var liveOut = liveOutByBlock.TryGetValue(block.Id, out var blockLiveOut)
                ? blockLiveOut
                : new HashSet<FieldKey>();
            blocks.Add(EliminateDeadStoresInBlock(
                block,
                definitions,
                storedFields,
                liveOut,
                ref changed));
        }

        return changed
            ? optimizedFunction with { Blocks = blocks.ToArray() }
            : function;
    }

    private static IReadOnlySet<string> CollectEligibleStackAggregateLocals(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var allocateLocal in function.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaAllocateLocalInstruction>())
        {
            if (string.Equals(allocateLocal.StorageClass, "stack", StringComparison.Ordinal)
                && IsSroaAggregateType(allocateLocal.LocalType))
            {
                candidates.Add(allocateLocal.LocalName);
            }
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var escaped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                AddEscapedAggregateRoots(instruction, definitions, phiDefinitions, candidates, escaped);
            }

            foreach (var value in EnumerateTerminatorOperands(block.Terminator))
            {
                AddEscapedAggregateRoots(value, definitions, phiDefinitions, candidates, escaped);
            }
        }

        candidates.ExceptWith(escaped);
        return candidates;
    }

    private static void AddEscapedAggregateRoots(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        IReadOnlySet<string> candidates,
        ISet<string> escaped)
    {
        switch (instruction)
        {
            case SsaValueInstruction { Value: SsaCallRValue call }:
                AddEscapedAggregateCallRoots(call, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaCallInstruction call:
                AddEscapedAggregateCallRoots(call, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaValueInstruction { Value: SsaIndirectCallRValue indirectCall }:
                AddEscapedAggregateIndirectCallRoots(indirectCall, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaIndirectCallInstruction indirectCall:
                AddEscapedAggregateIndirectCallRoots(indirectCall, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaValueInstruction { Value: SsaMakeSliceFromLocalRValue makeSlice }:
                if (candidates.Contains(makeSlice.LocalName))
                {
                    escaped.Add(makeSlice.LocalName);
                }

                break;

            case SsaStoreLocalInstruction storeLocal:
                AddEscapedAggregateRoots(storeLocal.Value, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaStoreIndirectInstruction storeIndirect:
                AddEscapedAggregateRoots(storeIndirect.Value, definitions, phiDefinitions, candidates, escaped);
                break;

            case SsaStoreGlobalInstruction storeGlobal:
                AddEscapedAggregateRoots(storeGlobal.Value, definitions, phiDefinitions, candidates, escaped);
                break;
        }
    }

    private static void AddEscapedAggregateCallRoots(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        IReadOnlySet<string> candidates,
        ISet<string> escaped)
    {
        foreach (var argument in call.Arguments)
        {
            AddEscapedAggregateRoots(argument, definitions, phiDefinitions, candidates, escaped);
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            AddEscapedAggregateRoots(address, definitions, phiDefinitions, candidates, escaped);
        }
    }

    private static void AddEscapedAggregateIndirectCallRoots(
        ISsaIndirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        IReadOnlySet<string> candidates,
        ISet<string> escaped)
    {
        AddEscapedAggregateRoots(call.Target, definitions, phiDefinitions, candidates, escaped);
        foreach (var argument in call.Arguments)
        {
            AddEscapedAggregateRoots(argument, definitions, phiDefinitions, candidates, escaped);
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            AddEscapedAggregateRoots(address, definitions, phiDefinitions, candidates, escaped);
        }
    }

    private static void AddEscapedAggregateRoots(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        IReadOnlySet<string> candidates,
        ISet<string> escaped)
    {
        if (!IsPotentialMemoryReferenceType(value.Type))
        {
            return;
        }

        var roots = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalRoots(
            value,
            definitions,
            phiDefinitions,
            new HashSet<string>(StringComparer.Ordinal),
            roots);
        roots.IntersectWith(candidates);
        escaped.UnionWith(roots);
    }

    private static void CollectLocalRoots(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> visitedValueNames,
        ISet<string> roots)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name))
        {
            return;
        }

        if (definitions.TryGetValue(reference.Name, out var definition))
        {
            CollectLocalRoots(definition, definitions, phiDefinitions, visitedValueNames, roots);
            return;
        }

        if (!phiDefinitions.TryGetValue(reference.Name, out var phi))
        {
            return;
        }

        foreach (var incoming in phi.Incomings)
        {
            CollectLocalRoots(incoming.Value, definitions, phiDefinitions, visitedValueNames, roots);
        }
    }

    private static void CollectLocalRoots(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> visitedValueNames,
        ISet<string> roots)
    {
        switch (value)
        {
            case SsaAddressOfLocalRValue addressOfLocal:
                roots.Add(addressOfLocal.LocalName);
                break;

            case SsaMakeSliceFromLocalRValue makeSlice:
                roots.Add(makeSlice.LocalName);
                break;

            case SsaUseRValue use:
                CollectLocalRoots(use.Value, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaSelectRValue select:
                CollectLocalRoots(select.WhenTrue, definitions, phiDefinitions, visitedValueNames, roots);
                CollectLocalRoots(select.WhenFalse, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaConvertRValue convert:
                CollectLocalRoots(convert.Operand, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaFieldAddressRValue fieldAddress:
                CollectLocalRoots(fieldAddress.Address, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaElementAddressRValue elementAddress:
                CollectLocalRoots(elementAddress.Address, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaSliceElementAddressRValue sliceElementAddress:
                CollectLocalRoots(sliceElementAddress.Slice, definitions, phiDefinitions, visitedValueNames, roots);
                break;

            case SsaTextSliceRValue textSlice:
                CollectLocalRoots(textSlice.TextValue, definitions, phiDefinitions, visitedValueNames, roots);
                break;
        }
    }

    private static IReadOnlySet<string> CollectCopiedAggregateDestinationRoots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> eligibleAggregateRoots)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in function.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaCopyMemoryInstruction>())
        {
            if (copy.TransferKind == SsaMemoryTransferKind.Copy
                && !IsForwardableScalarMemoryType(copy.CopyType)
                && TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var localName))
            {
                if (eligibleAggregateRoots.Contains(localName))
                {
                    roots.Add(localName);
                }
            }
        }

        return roots;
    }

    private SsaFunction EliminateDeadAggregateCopies(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        out bool changed)
    {
        var liveOutByBlock = ComputeAggregateRootLiveOut(function, definitions, copiedAggregateRoots);
        changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            var liveOut = liveOutByBlock.TryGetValue(block.Id, out var blockLiveOut)
                ? blockLiveOut
                : new HashSet<string>(StringComparer.Ordinal);
            blocks.Add(EliminateDeadAggregateCopiesInBlock(
                block,
                definitions,
                copiedAggregateRoots,
                liveOut,
                ref changed));
        }

        return changed
            ? function with { Blocks = blocks.ToArray() }
            : function;
    }

    private IReadOnlyDictionary<int, IReadOnlySet<string>> ComputeAggregateRootLiveOut(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots)
    {
        var successors = function.Blocks.ToDictionary(
            static block => block.Id,
            block => GetSuccessorBlockIds(block.Terminator).ToArray());
        var useByBlock = new Dictionary<int, HashSet<string>>();
        var defByBlock = new Dictionary<int, HashSet<string>>();
        var liveInByBlock = new Dictionary<int, HashSet<string>>();
        var liveOutByBlock = new Dictionary<int, HashSet<string>>();

        foreach (var block in function.Blocks)
        {
            CollectAggregateRootUseDef(
                block,
                definitions,
                copiedAggregateRoots,
                out var use,
                out var def);
            useByBlock[block.Id] = use;
            defByBlock[block.Id] = def;
            liveInByBlock[block.Id] = new HashSet<string>(StringComparer.Ordinal);
            liveOutByBlock[block.Id] = new HashSet<string>(StringComparer.Ordinal);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = function.Blocks.Count - 1; index >= 0; index--)
            {
                var block = function.Blocks[index];
                var newLiveOut = new HashSet<string>(StringComparer.Ordinal);
                if (successors.TryGetValue(block.Id, out var blockSuccessors))
                {
                    foreach (var successor in blockSuccessors)
                    {
                        if (liveInByBlock.TryGetValue(successor, out var successorLiveIn))
                        {
                            newLiveOut.UnionWith(successorLiveIn);
                        }
                    }
                }

                var newLiveIn = new HashSet<string>(newLiveOut, StringComparer.Ordinal);
                newLiveIn.ExceptWith(defByBlock[block.Id]);
                newLiveIn.UnionWith(useByBlock[block.Id]);

                if (!liveOutByBlock[block.Id].SetEquals(newLiveOut))
                {
                    liveOutByBlock[block.Id] = newLiveOut;
                    changed = true;
                }

                if (!liveInByBlock[block.Id].SetEquals(newLiveIn))
                {
                    liveInByBlock[block.Id] = newLiveIn;
                    changed = true;
                }
            }
        }

        return liveOutByBlock.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<string>)pair.Value);
    }

    private void CollectAggregateRootUseDef(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        out HashSet<string> use,
        out HashSet<string> def)
    {
        use = new HashSet<string>(StringComparer.Ordinal);
        def = new HashSet<string>(StringComparer.Ordinal);
        var definedInBlock = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }:
                    AddAggregateRootUse(loadLocal.LocalName, copiedAggregateRoots, definedInBlock, use);
                    break;

                case SsaValueInstruction { Value: SsaLoadIndirectRValue load }:
                    AddAggregateRootRead(load.Address, definitions, copiedAggregateRoots, definedInBlock, use);
                    break;

                case SsaStoreLocalInstruction storeLocal:
                    AddAggregateRootDef(storeLocal.LocalName, copiedAggregateRoots, definedInBlock, def);
                    break;

                case SsaStoreIndirectInstruction storeIndirect:
                    AddAggregateRootWrite(storeIndirect.Address, definitions, copiedAggregateRoots, definedInBlock, use, def);
                    break;

                case SsaCopyMemoryInstruction copy:
                    AddAggregateCopyUseDef(copy, definitions, copiedAggregateRoots, definedInBlock, use, def);
                    break;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    AddAggregateCallUses(call, definitions, copiedAggregateRoots, definedInBlock, use);
                    break;
                case SsaCallInstruction call:
                    AddAggregateCallUses(call, definitions, copiedAggregateRoots, definedInBlock, use);
                    break;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                case SsaIndirectCallInstruction:
                    AddAllAggregateRootUses(copiedAggregateRoots, definedInBlock, use);
                    break;

                case SsaLifetimeEndInstruction lifetimeEnd:
                    AddAggregateRootDef(lifetimeEnd.LocalName, copiedAggregateRoots, definedInBlock, def);
                    break;

                case SsaDeallocateLocalInstruction deallocateLocal:
                    AddAggregateRootDef(deallocateLocal.LocalName, copiedAggregateRoots, definedInBlock, def);
                    break;
            }
        }
    }

    private SsaBasicBlock EliminateDeadAggregateCopiesInBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        IReadOnlySet<string> liveOut,
        ref bool changed)
    {
        var live = new HashSet<string>(liveOut, StringComparer.Ordinal);
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        var blockChanged = false;

        for (var index = block.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = block.Instructions[index];
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }:
                    AddAggregateRootLive(loadLocal.LocalName, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaLoadIndirectRValue load }:
                    AddAggregateRootReadLiveness(load.Address, definitions, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreLocalInstruction storeLocal:
                    RemoveAggregateRootLive(storeLocal.LocalName, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreIndirectInstruction storeIndirect:
                    AddAggregateRootWriteLiveness(storeIndirect.Address, definitions, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaCopyMemoryInstruction copy:
                    if (copy.TransferKind == SsaMemoryTransferKind.Copy
                        && !IsForwardableScalarMemoryType(copy.CopyType)
                        && TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var destinationLocalName)
                        && copiedAggregateRoots.Contains(destinationLocalName)
                        && !live.Contains(destinationLocalName))
                    {
                        blockChanged = true;
                        continue;
                    }

                    AddAggregateCopyLiveness(copy, definitions, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    AddAggregateCallLiveness(call, definitions, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;
                case SsaCallInstruction call:
                    AddAggregateCallLiveness(call, definitions, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                case SsaIndirectCallInstruction:
                    AddAllAggregateRootsLive(copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaLifetimeEndInstruction lifetimeEnd:
                    RemoveAggregateRootLive(lifetimeEnd.LocalName, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                case SsaDeallocateLocalInstruction deallocateLocal:
                    RemoveAggregateRootLive(deallocateLocal.LocalName, copiedAggregateRoots, live);
                    instructions.Add(instruction);
                    continue;

                default:
                    instructions.Add(instruction);
                    continue;
            }
        }

        if (!blockChanged)
        {
            return block;
        }

        instructions.Reverse();
        changed = true;
        return block with { Instructions = instructions.ToArray() };
    }

    private static void AddAggregateRootRead(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use)
    {
        if (TryResolveLocalRoot(address, definitions, out var localName))
        {
            AddAggregateRootUse(localName, copiedAggregateRoots, definedInBlock, use);
            return;
        }

        if (IsPotentialMemoryReferenceType(address.Type))
        {
            AddAllAggregateRootUses(copiedAggregateRoots, definedInBlock, use);
        }
    }

    private static void AddAggregateRootWrite(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use,
        ISet<string> def)
    {
        if (TryResolveDirectLocalAddress(address, definitions, out var localName))
        {
            AddAggregateRootDef(localName, copiedAggregateRoots, definedInBlock, def);
            return;
        }

        if (!TryResolveLocalRoot(address, definitions, out _))
        {
            AddAllAggregateRootUses(copiedAggregateRoots, definedInBlock, use);
        }
    }

    private static void AddAggregateCopyUseDef(
        SsaCopyMemoryInstruction copy,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use,
        ISet<string> def)
    {
        AddAggregateRootRead(copy.SourceAddress, definitions, copiedAggregateRoots, definedInBlock, use);
        if (!IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var destinationLocalName))
        {
            AddAggregateRootDef(destinationLocalName, copiedAggregateRoots, definedInBlock, def);
            return;
        }

        AddAggregateRootWrite(copy.DestinationAddress, definitions, copiedAggregateRoots, definedInBlock, use, def);
    }

    private void AddAggregateCallUses(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use)
    {
        if (!TryGetReadLocalMemorySet(call, definitions, out var readSet))
        {
            AddAllAggregateRootUses(copiedAggregateRoots, definedInBlock, use);
            return;
        }

        foreach (var field in readSet.ExactFields)
        {
            AddAggregateRootUse(field.LocalName, copiedAggregateRoots, definedInBlock, use);
        }

        foreach (var localName in readSet.LocalRoots)
        {
            AddAggregateRootUse(localName, copiedAggregateRoots, definedInBlock, use);
        }
    }

    private void AddAggregateCallLiveness(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (!TryGetReadLocalMemorySet(call, definitions, out var readSet))
        {
            AddAllAggregateRootsLive(copiedAggregateRoots, live);
            return;
        }

        foreach (var field in readSet.ExactFields)
        {
            AddAggregateRootLive(field.LocalName, copiedAggregateRoots, live);
        }

        foreach (var localName in readSet.LocalRoots)
        {
            AddAggregateRootLive(localName, copiedAggregateRoots, live);
        }
    }

    private static void AddAggregateRootReadLiveness(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (TryResolveLocalRoot(address, definitions, out var localName))
        {
            AddAggregateRootLive(localName, copiedAggregateRoots, live);
            return;
        }

        if (IsPotentialMemoryReferenceType(address.Type))
        {
            AddAllAggregateRootsLive(copiedAggregateRoots, live);
        }
    }

    private static void AddAggregateRootWriteLiveness(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (TryResolveDirectLocalAddress(address, definitions, out var localName))
        {
            RemoveAggregateRootLive(localName, copiedAggregateRoots, live);
            return;
        }

        if (!TryResolveLocalRoot(address, definitions, out _))
        {
            AddAllAggregateRootsLive(copiedAggregateRoots, live);
        }
    }

    private static void AddAggregateCopyLiveness(
        SsaCopyMemoryInstruction copy,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (!IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveDirectLocalAddress(copy.DestinationAddress, definitions, out var destinationLocalName))
        {
            RemoveAggregateRootLive(destinationLocalName, copiedAggregateRoots, live);
        }
        else
        {
            AddAggregateRootWriteLiveness(copy.DestinationAddress, definitions, copiedAggregateRoots, live);
        }

        AddAggregateRootReadLiveness(copy.SourceAddress, definitions, copiedAggregateRoots, live);
    }

    private static bool TryResolveDirectLocalAddress(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        localName = string.Empty;
        return address is SsaValueReference reference
               && definitions.TryGetValue(reference.Name, out var definition)
               && definition is SsaAddressOfLocalRValue addressOfLocal
               && ReturnLocalName(addressOfLocal.LocalName, out localName);
    }

    private static bool TryResolveLocalRoot(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveLocalRoot(
            address,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out localName);
    }

    private static bool TryResolveLocalRoot(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string localName)
    {
        localName = string.Empty;
        return address is SsaValueReference reference
               && visitedValueNames.Add(reference.Name)
               && definitions.TryGetValue(reference.Name, out var definition)
               && definition switch
               {
                   SsaAddressOfLocalRValue addressOfLocal => ReturnLocalName(addressOfLocal.LocalName, out localName),
                   SsaMakeSliceFromLocalRValue makeSlice => ReturnLocalName(makeSlice.LocalName, out localName),
                   SsaFieldAddressRValue fieldAddress => TryResolveLocalRoot(
                       fieldAddress.Address,
                       definitions,
                       visitedValueNames,
                       out localName),
                   SsaElementAddressRValue elementAddress => TryResolveLocalRoot(
                       elementAddress.Address,
                       definitions,
                       visitedValueNames,
                       out localName),
                   SsaSliceElementAddressRValue sliceElementAddress => TryResolveLocalRoot(
                       sliceElementAddress.Slice,
                       definitions,
                       visitedValueNames,
                       out localName),
                   SsaTextSliceRValue textSlice => TryResolveLocalRoot(
                       textSlice.TextValue,
                       definitions,
                       visitedValueNames,
                       out localName),
                   SsaUseRValue use => TryResolveLocalRoot(
                       use.Value,
                       definitions,
                       visitedValueNames,
                       out localName),
                   SsaConvertRValue convert => TryResolveLocalRoot(
                       convert.Operand,
                       definitions,
                       visitedValueNames,
                       out localName),
                   _ => false
               };
    }

    private static void AddAggregateRootUse(
        string localName,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use)
    {
        if (copiedAggregateRoots.Contains(localName)
            && !definedInBlock.Contains(localName))
        {
            use.Add(localName);
        }
    }

    private static void AddAllAggregateRootUses(
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> use)
    {
        foreach (var localName in copiedAggregateRoots)
        {
            AddAggregateRootUse(localName, copiedAggregateRoots, definedInBlock, use);
        }
    }

    private static void AddAggregateRootDef(
        string localName,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> definedInBlock,
        ISet<string> def)
    {
        if (!copiedAggregateRoots.Contains(localName))
        {
            return;
        }

        definedInBlock.Add(localName);
        def.Add(localName);
    }

    private static void AddAggregateRootLive(
        string localName,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (copiedAggregateRoots.Contains(localName))
        {
            live.Add(localName);
        }
    }

    private static void RemoveAggregateRootLive(
        string localName,
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        if (copiedAggregateRoots.Contains(localName))
        {
            live.Remove(localName);
        }
    }

    private static void AddAllAggregateRootsLive(
        IReadOnlySet<string> copiedAggregateRoots,
        ISet<string> live)
    {
        foreach (var localName in copiedAggregateRoots)
        {
            live.Add(localName);
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

    private static IReadOnlySet<FieldKey> CollectStoredScalarFields(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> eligibleAggregateRoots)
    {
        var fields = new HashSet<FieldKey>();
        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            switch (instruction)
            {
                case SsaStoreIndirectInstruction store
                    when IsForwardableScalarMemoryType(store.ValueType)
                         && TryResolveFieldKey(store.Address, definitions, out var storeKey)
                         && storeKey.Type == store.ValueType
                         && eligibleAggregateRoots.Contains(storeKey.LocalName):
                    fields.Add(storeKey);
                    break;

                case SsaCopyMemoryInstruction copy
                    when IsForwardableScalarMemoryType(copy.CopyType)
                         && TryResolveFieldKey(copy.DestinationAddress, definitions, out var destinationKey)
                         && destinationKey.Type == copy.CopyType
                         && eligibleAggregateRoots.Contains(destinationKey.LocalName):
                    fields.Add(destinationKey);
                    break;
            }
        }

        return fields;
    }

    private IReadOnlyDictionary<int, IReadOnlySet<FieldKey>> ComputeFieldLiveOut(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields)
    {
        var successors = function.Blocks.ToDictionary(
            static block => block.Id,
            block => GetSuccessorBlockIds(block.Terminator).ToArray());
        var useByBlock = new Dictionary<int, HashSet<FieldKey>>();
        var defByBlock = new Dictionary<int, HashSet<FieldKey>>();
        var liveInByBlock = new Dictionary<int, HashSet<FieldKey>>();
        var liveOutByBlock = new Dictionary<int, HashSet<FieldKey>>();

        foreach (var block in function.Blocks)
        {
            CollectUseDef(block, definitions, storedFields, out var use, out var def);
            useByBlock[block.Id] = use;
            defByBlock[block.Id] = def;
            liveInByBlock[block.Id] = [];
            liveOutByBlock[block.Id] = [];
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = function.Blocks.Count - 1; index >= 0; index--)
            {
                var block = function.Blocks[index];
                var newLiveOut = new HashSet<FieldKey>();
                if (successors.TryGetValue(block.Id, out var blockSuccessors))
                {
                    foreach (var successor in blockSuccessors)
                    {
                        if (liveInByBlock.TryGetValue(successor, out var successorLiveIn))
                        {
                            newLiveOut.UnionWith(successorLiveIn);
                        }
                    }
                }

                var newLiveIn = new HashSet<FieldKey>(newLiveOut);
                newLiveIn.ExceptWith(defByBlock[block.Id]);
                newLiveIn.UnionWith(useByBlock[block.Id]);

                if (!liveOutByBlock[block.Id].SetEquals(newLiveOut))
                {
                    liveOutByBlock[block.Id] = newLiveOut;
                    changed = true;
                }

                if (!liveInByBlock[block.Id].SetEquals(newLiveIn))
                {
                    liveInByBlock[block.Id] = newLiveIn;
                    changed = true;
                }
            }
        }

        return liveOutByBlock.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<FieldKey>)pair.Value);
    }

    private void CollectUseDef(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        out HashSet<FieldKey> use,
        out HashSet<FieldKey> def)
    {
        use = [];
        def = [];
        var definedInBlock = new HashSet<FieldKey>();

        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadIndirectRValue load }
                    when IsForwardableScalarMemoryType(load.Type)
                         && TryResolveFieldKey(load.Address, definitions, out var loadedKey)
                         && loadedKey.Type == load.Type:
                    if (storedFields.Contains(loadedKey))
                    {
                        AddFieldUse(loadedKey, definedInBlock, use);
                    }

                    break;

                case SsaValueInstruction { Value: SsaLoadIndirectRValue }:
                    AddAllFieldUses(storedFields, definedInBlock, use);
                    break;

                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }:
                    AddLocalFieldUses(loadLocal.LocalName, storedFields, definedInBlock, use);
                    break;

                case SsaStoreIndirectInstruction store
                    when IsForwardableScalarMemoryType(store.ValueType)
                         && TryResolveFieldKey(store.Address, definitions, out var storedKey)
                         && storedKey.Type == store.ValueType:
                    if (storedFields.Contains(storedKey))
                    {
                        definedInBlock.Add(storedKey);
                        def.Add(storedKey);
                    }

                    break;

                case SsaStoreIndirectInstruction:
                    AddAllFieldUses(storedFields, definedInBlock, use);
                    break;

                case SsaStoreLocalInstruction storeLocal:
                    AddLocalFieldUses(storeLocal.LocalName, storedFields, definedInBlock, use);
                    break;

                case SsaCopyMemoryInstruction copy:
                    AddCopyUsesAndDefs(copy, definitions, storedFields, definedInBlock, use, def);
                    break;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    AddCallUses(call, definitions, storedFields, definedInBlock, use);
                    break;
                case SsaCallInstruction call:
                    AddCallUses(call, definitions, storedFields, definedInBlock, use);
                    break;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                case SsaIndirectCallInstruction:
                    AddAllFieldUses(storedFields, definedInBlock, use);
                    break;
            }
        }
    }

    private SsaBasicBlock EliminateDeadStoresInBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        IReadOnlySet<FieldKey> liveOut,
        ref bool changed)
    {
        var live = new HashSet<FieldKey>(liveOut);
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        var blockChanged = false;

        for (var index = block.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = block.Instructions[index];
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadIndirectRValue load }
                    when IsForwardableScalarMemoryType(load.Type)
                         && TryResolveFieldKey(load.Address, definitions, out var loadedKey)
                         && loadedKey.Type == load.Type:
                    if (storedFields.Contains(loadedKey))
                    {
                        live.Add(loadedKey);
                    }

                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaLoadIndirectRValue }:
                    AddAllLive(live, storedFields);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }:
                    AddLocalFieldsLive(loadLocal.LocalName, storedFields, live);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreIndirectInstruction store
                    when IsForwardableScalarMemoryType(store.ValueType)
                         && TryResolveFieldKey(store.Address, definitions, out var storedKey)
                         && storedKey.Type == store.ValueType:
                    if (!storedFields.Contains(storedKey))
                    {
                        instructions.Add(instruction);
                        continue;
                    }

                    if (!live.Contains(storedKey))
                    {
                        blockChanged = true;
                        continue;
                    }

                    live.Remove(storedKey);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreIndirectInstruction:
                    AddAllLive(live, storedFields);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreLocalInstruction storeLocal:
                    AddLocalFieldsLive(storeLocal.LocalName, storedFields, live);
                    instructions.Add(instruction);
                    continue;

                case SsaCopyMemoryInstruction copy:
                    if (TryResolveExactScalarFieldCopy(copy, definitions, out var sourceKey, out var destinationKey)
                        && storedFields.Contains(destinationKey)
                        && !live.Contains(destinationKey))
                    {
                        blockChanged = true;
                        continue;
                    }

                    AddCopyLiveness(copy, definitions, storedFields, live);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    AddCallLiveness(call, definitions, storedFields, live);
                    instructions.Add(instruction);
                    continue;
                case SsaCallInstruction call:
                    AddCallLiveness(call, definitions, storedFields, live);
                    instructions.Add(instruction);
                    continue;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                case SsaIndirectCallInstruction:
                    AddAllLive(live, storedFields);
                    instructions.Add(instruction);
                    continue;

                default:
                    instructions.Add(instruction);
                    continue;
            }
        }

        if (!blockChanged)
        {
            return block;
        }

        instructions.Reverse();
        changed = true;
        return block with { Instructions = instructions.ToArray() };
    }

    private static void AddCopyUsesAndDefs(
        SsaCopyMemoryInstruction copy,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> definedInBlock,
        ISet<FieldKey> use,
        ISet<FieldKey> def)
    {
        if (IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveFieldKey(copy.SourceAddress, definitions, out var sourceKey)
            && sourceKey.Type == copy.CopyType)
        {
            if (storedFields.Contains(sourceKey))
            {
                AddFieldUse(sourceKey, definedInBlock, use);
            }
        }
        else
        {
            AddAllFieldUses(storedFields, definedInBlock, use);
        }

        if (IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveFieldKey(copy.DestinationAddress, definitions, out var destinationKey)
            && destinationKey.Type == copy.CopyType)
        {
            if (storedFields.Contains(destinationKey))
            {
                definedInBlock.Add(destinationKey);
                def.Add(destinationKey);
            }
        }
        else
        {
            AddAllFieldUses(storedFields, definedInBlock, use);
        }
    }

    private static bool TryResolveExactScalarFieldCopy(
        SsaCopyMemoryInstruction copy,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldKey sourceKey,
        out FieldKey destinationKey)
    {
        sourceKey = default;
        destinationKey = default;
        return IsForwardableScalarMemoryType(copy.CopyType)
               && TryResolveFieldKey(copy.SourceAddress, definitions, out sourceKey)
               && TryResolveFieldKey(copy.DestinationAddress, definitions, out destinationKey)
               && sourceKey.Type == copy.CopyType
               && destinationKey.Type == copy.CopyType;
    }

    private static void AddCopyLiveness(
        SsaCopyMemoryInstruction copy,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> live)
    {
        if (IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveFieldKey(copy.DestinationAddress, definitions, out var destinationKey)
            && destinationKey.Type == copy.CopyType)
        {
            if (storedFields.Contains(destinationKey))
            {
                live.Remove(destinationKey);
            }
        }
        else
        {
            AddAllLive(live, storedFields);
        }

        if (IsForwardableScalarMemoryType(copy.CopyType)
            && TryResolveFieldKey(copy.SourceAddress, definitions, out var sourceKey)
            && sourceKey.Type == copy.CopyType)
        {
            if (storedFields.Contains(sourceKey))
            {
                live.Add(sourceKey);
            }
        }
        else
        {
            AddAllLive(live, storedFields);
        }
    }

    private void AddCallUses(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> definedInBlock,
        ISet<FieldKey> use)
    {
        if (!TryGetReadLocalMemorySet(call, definitions, out var readSet))
        {
            AddAllFieldUses(storedFields, definedInBlock, use);
            return;
        }

        foreach (var field in readSet.ExactFields)
        {
            AddFieldUse(field, definedInBlock, use);
        }

        foreach (var localName in readSet.LocalRoots)
        {
            foreach (var field in storedFields.Where(field => string.Equals(field.LocalName, localName, StringComparison.Ordinal)))
            {
                AddFieldUse(field, definedInBlock, use);
            }
        }
    }

    private void AddCallLiveness(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> live)
    {
        if (!TryGetReadLocalMemorySet(call, definitions, out var readSet))
        {
            AddAllLive(live, storedFields);
            return;
        }

        foreach (var field in readSet.ExactFields)
        {
            live.Add(field);
        }

        foreach (var localName in readSet.LocalRoots)
        {
            foreach (var field in storedFields.Where(field => string.Equals(field.LocalName, localName, StringComparison.Ordinal)))
            {
                live.Add(field);
            }
        }
    }

    private bool TryGetReadLocalMemorySet(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out LocalMemoryReadSet readSet)
    {
        return TryGetReadLocalMemorySet(_effectModel, call, definitions, out readSet);
    }

    private static bool TryGetReadLocalMemorySet(
        FunctionEffectModel? effectModel,
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out LocalMemoryReadSet readSet)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var exactFields = new HashSet<FieldKey>();
        readSet = new LocalMemoryReadSet(roots, exactFields);

        if (effectModel is not { } model
            || !model.Functions.TryGetValue(call.FunctionName, out var effects)
            || !effects.IsPure
            || !effects.NoSync)
        {
            return false;
        }

        if (effects.ReadsOtherMemory)
        {
            // The callee reads memory beyond its own arguments -- e.g. the object
            // behind a `dyn` trait object's data pointer, or memory reached through a
            // global. It can only reach a local through a pointer to that local, so
            // the locals it may read are exactly those whose address has escaped (had
            // its address taken anywhere in this function). Locals whose address is
            // never taken cannot be observed by such a callee, so their field stores
            // remain eligible for elimination.
            CollectAddressEscapedLocals(definitions, roots);
            return true;
        }

        if (!effects.ReadsArgumentMemory)
        {
            return true;
        }

        foreach (var argument in EnumerateCallMemoryArguments(call))
        {
            if (!TryCollectLocalMemoryReads(
                    argument,
                    definitions,
                    new HashSet<string>(StringComparer.Ordinal),
                    roots,
                    exactFields))
            {
                return false;
            }
        }

        return true;
    }

    // Locals whose address is taken anywhere in the function (directly, or via a
    // field/element/slice address). Only these can be observed by a callee that
    // reads memory beyond its arguments, so they bound such a callee's local reads.
    private static void CollectAddressEscapedLocals(
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> escapedLocals)
    {
        foreach (var rvalue in definitions.Values)
        {
            switch (rvalue)
            {
                case SsaAddressOfLocalRValue addressOfLocal:
                    escapedLocals.Add(addressOfLocal.LocalName);
                    break;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    escapedLocals.Add(makeSlice.LocalName);
                    break;
                case SsaFieldAddressRValue fieldAddress
                    when TryResolveFieldAddressRoot(fieldAddress.Address, definitions, new HashSet<string>(StringComparer.Ordinal), out var fieldRoot, out _):
                    escapedLocals.Add(fieldRoot);
                    break;
                case SsaElementAddressRValue elementAddress
                    when TryResolveFieldAddressRoot(elementAddress.Address, definitions, new HashSet<string>(StringComparer.Ordinal), out var elementRoot, out _):
                    escapedLocals.Add(elementRoot);
                    break;
            }
        }
    }

    private static bool TryCollectLocalMemoryReads(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        ISet<string> localNames,
        ISet<FieldKey> exactFields)
    {
        return value switch
        {
            SsaValueReference reference
                when visitedValueNames.Add(reference.Name)
                     && definitions.TryGetValue(reference.Name, out var definition) =>
                TryCollectLocalMemoryReads(definition, definitions, visitedValueNames, localNames, exactFields),
            SsaValueReference reference when IsPotentialMemoryReferenceType(reference.Type) => false,
            _ => true
        };
    }

    private static bool TryCollectLocalMemoryReads(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        ISet<string> localNames,
        ISet<FieldKey> exactFields)
    {
        switch (value)
        {
            case SsaAddressOfLocalRValue addressOfLocal:
                localNames.Add(addressOfLocal.LocalName);
                return true;

            case SsaMakeSliceFromLocalRValue makeSlice:
                localNames.Add(makeSlice.LocalName);
                return true;

            case SsaUseRValue use:
                return TryCollectLocalMemoryReads(use.Value, definitions, visitedValueNames, localNames, exactFields);

            case SsaSelectRValue select:
                return TryCollectLocalMemoryReads(select.WhenTrue, definitions, visitedValueNames, localNames, exactFields)
                       && TryCollectLocalMemoryReads(select.WhenFalse, definitions, visitedValueNames, localNames, exactFields);

            case SsaConvertRValue convert:
                return TryCollectLocalMemoryReads(convert.Operand, definitions, visitedValueNames, localNames, exactFields);

            case SsaFieldAddressRValue fieldAddress:
                if (TryCreateFieldKey(fieldAddress, definitions, out var fieldKey))
                {
                    exactFields.Add(fieldKey);
                    return true;
                }

                if (!TryResolveFieldAddressRoot(
                        fieldAddress.Address,
                        definitions,
                        visitedValueNames,
                        out var fieldLocalName,
                        out _))
                {
                    return false;
                }

                localNames.Add(fieldLocalName);
                return true;

            case SsaElementAddressRValue elementAddress:
                if (TryCreateElementKey(elementAddress, definitions, out var elementKey))
                {
                    exactFields.Add(elementKey);
                    return true;
                }

                if (!TryResolveFieldAddressRoot(
                        elementAddress.Address,
                        definitions,
                        visitedValueNames,
                        out var elementLocalName,
                        out _))
                {
                    return false;
                }

                localNames.Add(elementLocalName);
                return true;

            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryCollectLocalMemoryReads(
                    sliceElementAddress.Slice,
                    definitions,
                    visitedValueNames,
                    localNames,
                    exactFields);

            case SsaTextSliceRValue textSlice:
                return TryCollectLocalMemoryReads(
                    textSlice.TextValue,
                    definitions,
                    visitedValueNames,
                    localNames,
                    exactFields);

            default:
                return !IsPotentialMemoryReferenceType(value.Type);
        }
    }

    private static bool TryResolveFieldKey(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldKey key)
    {
        key = default;
        return address is SsaValueReference reference
               && definitions.TryGetValue(reference.Name, out var definition)
               && definition switch
               {
                   SsaFieldAddressRValue fieldAddress => TryCreateFieldKey(fieldAddress, definitions, out key),
                   SsaElementAddressRValue elementAddress => TryCreateElementKey(elementAddress, definitions, out key),
                   _ => false
               };
    }

    private static bool TryCreateFieldKey(
        SsaFieldAddressRValue fieldAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldKey key)
    {
        key = default;
        if (fieldAddress.Type.ElementType is not { } fieldType
            || !IsForwardableScalarMemoryType(fieldType)
            || !TryResolveFieldAddressRoot(
                fieldAddress.Address,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var localName,
                out var parentFieldPath))
        {
            return false;
        }

        key = new FieldKey(
            localName,
            AppendFieldPath(parentFieldPath, fieldAddress.FieldIndex, fieldAddress.FieldName),
            fieldType);
        return true;
    }

    private static bool TryCreateElementKey(
        SsaElementAddressRValue elementAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldKey key)
    {
        key = default;
        if (elementAddress.AggregateType.Kind != StarkTypeKind.FixedArray
            || elementAddress.ConstantIndex is not int constantIndex
            || elementAddress.Type.ElementType is not { } elementType
            || !IsForwardableScalarMemoryType(elementType)
            || !TryResolveFieldAddressRoot(
                elementAddress.Address,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var localName,
                out var parentFieldPath))
        {
            return false;
        }

        key = new FieldKey(
            localName,
            AppendElementPath(parentFieldPath, constantIndex),
            elementType);
        return true;
    }

    private static bool TryResolveFieldAddressRoot(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string localName,
        out string fieldPath)
    {
        localName = string.Empty;
        fieldPath = string.Empty;
        return address is SsaValueReference reference
               && visitedValueNames.Add(reference.Name)
               && definitions.TryGetValue(reference.Name, out var definition)
               && definition switch
               {
                   SsaAddressOfLocalRValue addressOfLocal => ReturnFieldAddressRoot(
                       addressOfLocal.LocalName,
                       string.Empty,
                       out localName,
                       out fieldPath),
                   SsaFieldAddressRValue parentFieldAddress => TryResolveParentFieldAddressRoot(
                       parentFieldAddress,
                       definitions,
                       visitedValueNames,
                       out localName,
                       out fieldPath),
                   SsaElementAddressRValue parentElementAddress => TryResolveParentElementAddressRoot(
                       parentElementAddress,
                       definitions,
                       visitedValueNames,
                       out localName,
                       out fieldPath),
                   SsaUseRValue use => TryResolveFieldAddressRoot(
                       use.Value,
                       definitions,
                       visitedValueNames,
                       out localName,
                       out fieldPath),
                   SsaConvertRValue convert => TryResolveFieldAddressRoot(
                       convert.Operand,
                       definitions,
                       visitedValueNames,
                       out localName,
                       out fieldPath),
                   _ => false
               };
    }

    private static bool TryResolveParentFieldAddressRoot(
        SsaFieldAddressRValue fieldAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string localName,
        out string fieldPath)
    {
        if (!TryResolveFieldAddressRoot(
                fieldAddress.Address,
                definitions,
                visitedValueNames,
                out localName,
                out var parentFieldPath))
        {
            fieldPath = string.Empty;
            return false;
        }

        fieldPath = AppendFieldPath(parentFieldPath, fieldAddress.FieldIndex, fieldAddress.FieldName);
        return true;
    }

    private static bool TryResolveParentElementAddressRoot(
        SsaElementAddressRValue elementAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string localName,
        out string fieldPath)
    {
        if (elementAddress.AggregateType.Kind != StarkTypeKind.FixedArray
            || elementAddress.ConstantIndex is not int constantIndex
            || !TryResolveFieldAddressRoot(
                elementAddress.Address,
                definitions,
                visitedValueNames,
                out localName,
                out var parentFieldPath))
        {
            localName = string.Empty;
            fieldPath = string.Empty;
            return false;
        }

        fieldPath = AppendElementPath(parentFieldPath, constantIndex);
        return true;
    }

    private static void AddFieldUse(
        FieldKey field,
        ISet<FieldKey> definedInBlock,
        ISet<FieldKey> use)
    {
        if (!definedInBlock.Contains(field))
        {
            use.Add(field);
        }
    }

    private static void AddAllFieldUses(
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> definedInBlock,
        ISet<FieldKey> use)
    {
        foreach (var field in storedFields)
        {
            AddFieldUse(field, definedInBlock, use);
        }
    }

    private static void AddLocalFieldUses(
        string localName,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> definedInBlock,
        ISet<FieldKey> use)
    {
        foreach (var field in storedFields.Where(field => string.Equals(field.LocalName, localName, StringComparison.Ordinal)))
        {
            AddFieldUse(field, definedInBlock, use);
        }
    }

    private static void AddLocalFieldsLive(
        string localName,
        IReadOnlySet<FieldKey> storedFields,
        ISet<FieldKey> live)
    {
        foreach (var field in storedFields.Where(field => string.Equals(field.LocalName, localName, StringComparison.Ordinal)))
        {
            live.Add(field);
        }
    }

    private static void AddAllLive(ISet<FieldKey> live, IEnumerable<FieldKey> storedFields)
    {
        foreach (var field in storedFields)
        {
            live.Add(field);
        }
    }

    private static IEnumerable<int> GetSuccessorBlockIds(SsaTerminator terminator)
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

    private static IEnumerable<SsaValue> EnumerateTerminatorOperands(SsaTerminator terminator)
    {
        if (terminator.Condition is not null)
        {
            yield return terminator.Condition;
        }

        if (terminator.Value is not null)
        {
            yield return terminator.Value;
        }

        if (terminator.SwitchCases is not null)
        {
            foreach (var switchCase in terminator.SwitchCases)
            {
                yield return switchCase.MatchValue;
            }
        }
    }

    private static IEnumerable<SsaValue> EnumerateCallMemoryArguments(ISsaDirectCallOperation call)
    {
        foreach (var argument in call.Arguments)
        {
            yield return argument;
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            yield return address;
        }
    }

    private static bool IsPotentialMemoryReferenceType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.RawPointer
            or StarkTypeKind.Slice
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    private static bool IsForwardableScalarMemoryType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static bool IsSroaAggregateType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
    }

    private static string AppendFieldPath(string parentFieldPath, int fieldIndex, string fieldName)
    {
        var segment = string.Create(
            CultureInfo.InvariantCulture,
            $"{fieldIndex}:{fieldName}");
        return string.IsNullOrEmpty(parentFieldPath)
            ? segment
            : string.Concat(parentFieldPath, "/", segment);
    }

    private static string AppendElementPath(string parentFieldPath, int constantIndex)
    {
        var segment = string.Create(
            CultureInfo.InvariantCulture,
            $"element:{constantIndex}");
        return string.IsNullOrEmpty(parentFieldPath)
            ? segment
            : string.Concat(parentFieldPath, "/", segment);
    }

    private static bool ReturnFieldAddressRoot(
        string value,
        string fieldPathValue,
        out string localName,
        out string fieldPath)
    {
        localName = value;
        fieldPath = fieldPathValue;
        return true;
    }
}
