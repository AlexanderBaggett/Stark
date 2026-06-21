using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaAliasAwareMemoryOptimizer
{
    private readonly FunctionEffectModel? _effectModel;

    private readonly record struct FunctionBodyGlobalMemoryEffects(
        bool ReadsGlobalMemory,
        bool WritesGlobalMemory);

    private readonly record struct FieldMemoryKey(
        string LocalName,
        string FieldPath,
        StarkTypeSymbol Type);

    private readonly record struct LocalMemoryReadSet(
        IReadOnlySet<string> LocalRoots,
        IReadOnlySet<FieldMemoryKey> ExactFields);

    public SsaAliasAwareMemoryOptimizer(FunctionEffectModel? effectModel = null)
    {
        _effectModel = effectModel;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var bodyGlobalMemoryEffects = AnalyzeFunctionBodyGlobalMemoryEffects(module);
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function, bodyGlobalMemoryEffects);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    public SsaFunction OptimizeFunction(SsaFunction function)
    {
        return OptimizeFunction(function, EmptyBodyGlobalMemoryEffects);
    }

    private SsaFunction OptimizeFunction(
        SsaFunction function,
        IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> bodyGlobalMemoryEffects)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var eligibleLocals = CollectEligibleStackScalarLocals(function);
        var closureEnvironmentLocals = CollectClosureEnvironmentLocals(function);
        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
        var valueUseBlockIds = CollectValueUseBlockIds(function);

        var predecessors = BuildPredecessorMap(function);
        var exitKnownLocalsByBlock = new Dictionary<int, IReadOnlyDictionary<string, SsaValue>>();
        var exitKnownGlobalsByBlock =
            new Dictionary<int, IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>>();
        var exitKnownFieldsByBlock = new Dictionary<int, IReadOnlyDictionary<FieldMemoryKey, SsaValue>>();
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            var entryKnownLocals = TryGetSinglePredecessorExitKnownLocals(
                block,
                predecessors,
                exitKnownLocalsByBlock);
            var entryKnownGlobals = TryGetSinglePredecessorExitKnownGlobals(
                block,
                predecessors,
                exitKnownGlobalsByBlock);
            var entryKnownFields = TryGetSinglePredecessorExitKnownFields(
                block,
                predecessors,
                exitKnownFieldsByBlock);
            blocks.Add(OptimizeBlock(
                block,
                eligibleLocals,
                closureEnvironmentLocals,
                definitions,
                valueUseBlockIds,
                entryKnownLocals,
                entryKnownGlobals,
                entryKnownFields,
                bodyGlobalMemoryEffects,
                ref changed,
                out var exitKnownLocals,
                out var exitKnownGlobals,
                out var exitKnownFields));
            exitKnownLocalsByBlock[block.Id] = exitKnownLocals;
            exitKnownGlobalsByBlock[block.Id] = exitKnownGlobals;
            exitKnownFieldsByBlock[block.Id] = exitKnownFields;
        }

        var forwardedFunction = changed
            ? function with { Blocks = blocks.ToArray() }
            : function;
        var deadStoreOptimizedFunction = EliminateDeadStackScalarStores(
            forwardedFunction,
            eligibleLocals,
            out var deadStoreChanged);

        return deadStoreChanged ? deadStoreOptimizedFunction : forwardedFunction;
    }

    private SsaBasicBlock OptimizeBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        IReadOnlySet<string> closureEnvironmentLocals,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, IReadOnlySet<int>> valueUseBlockIds,
        IReadOnlyDictionary<string, SsaValue> entryKnownLocals,
        IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> entryKnownGlobals,
        IReadOnlyDictionary<FieldMemoryKey, SsaValue> entryKnownFields,
        IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> bodyGlobalMemoryEffects,
        ref bool changed,
        out IReadOnlyDictionary<string, SsaValue> exitKnownLocals,
        out IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> exitKnownGlobals,
        out IReadOnlyDictionary<FieldMemoryKey, SsaValue> exitKnownFields)
    {
        var blockChanged = false;
        var knownLocals = new Dictionary<string, SsaValue>(entryKnownLocals, StringComparer.Ordinal);
        var knownGlobals = new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>(entryKnownGlobals);
        var knownFields = new Dictionary<FieldMemoryKey, SsaValue>(entryKnownFields);
        var fieldAddressKeys = new Dictionary<string, FieldMemoryKey>(StringComparer.Ordinal);
        var pendingStoreInstructionIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingGlobalStoreInstructionIndexes =
            new Dictionary<(string GlobalName, StarkTypeSymbol Type), int>();
        var pendingFieldStoreInstructionIndexes = new Dictionary<FieldMemoryKey, int>();
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var instructions = new List<SsaInstruction?>(block.Instructions.Count);

        foreach (var instruction in block.Instructions)
        {
            var rewritten = RewriteInstruction(instruction, replacements);
            if (!EqualityComparer<SsaInstruction>.Default.Equals(rewritten, instruction))
            {
                blockChanged = true;
            }

            switch (rewritten)
            {
                case SsaValueInstruction
                {
                    Value: SsaLoadLocalRValue loadLocal
                } valueInstruction
                    when eligibleLocals.TryGetValue(loadLocal.LocalName, out var localType)
                         && localType == loadLocal.Type
                         && knownLocals.TryGetValue(loadLocal.LocalName, out var knownValue)
                         && knownValue.Type == loadLocal.Type
                         && ValueUsesAreConfinedToBlock(valueInstruction.ResultName, block.Id, valueUseBlockIds):
                    replacements[valueInstruction.ResultName] = RewriteValue(knownValue, replacements);
                    blockChanged = true;
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadGlobalRValue loadGlobal
                } valueInstruction
                    when IsForwardableScalarMemoryType(loadGlobal.Type)
                         && knownGlobals.TryGetValue((loadGlobal.GlobalName, loadGlobal.Type), out var knownValue)
                         && knownValue.Type == loadGlobal.Type
                         && ValueUsesAreConfinedToBlock(valueInstruction.ResultName, block.Id, valueUseBlockIds):
                    replacements[valueInstruction.ResultName] = RewriteValue(knownValue, replacements);
                    blockChanged = true;
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadGlobalRValue loadGlobal
                } valueInstruction:
                    if (IsForwardableScalarMemoryType(loadGlobal.Type))
                    {
                        knownGlobals[(loadGlobal.GlobalName, loadGlobal.Type)] =
                            new SsaValueReference(valueInstruction.ResultName, loadGlobal.Type);
                    }
                    else
                    {
                        RemoveKnownGlobal(knownGlobals, loadGlobal.GlobalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadIndirectRValue loadIndirect
                } valueInstruction
                    when IsForwardableScalarMemoryType(loadIndirect.Type)
                         && TryResolveFieldMemoryKey(loadIndirect.Address, definitions, fieldAddressKeys, out var loadedFieldKey)
                         && loadedFieldKey.Type == loadIndirect.Type
                         && knownFields.TryGetValue(loadedFieldKey, out var knownValue)
                         && knownValue.Type == loadIndirect.Type
                         && ValueUsesAreConfinedToBlock(valueInstruction.ResultName, block.Id, valueUseBlockIds):
                    // The confinement check matches the load-local and
                    // load-global forwarding cases above: replacements are
                    // per-block, so a result consumed in another block (e.g.
                    // an inlined call's continuation phi) must keep its
                    // defining load.
                    replacements[valueInstruction.ResultName] = RewriteValue(knownValue, replacements);
                    blockChanged = true;
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadIndirectRValue loadIndirect
                } valueInstruction
                    when IsForwardableScalarMemoryType(loadIndirect.Type)
                         && TryResolveFieldMemoryKey(loadIndirect.Address, definitions, fieldAddressKeys, out var loadedFieldKey):
                    if (loadedFieldKey.Type == loadIndirect.Type)
                    {
                        pendingFieldStoreInstructionIndexes.Remove(loadedFieldKey);
                        knownFields[loadedFieldKey] = new SsaValueReference(valueInstruction.ResultName, loadIndirect.Type);
                    }
                    else
                    {
                        pendingFieldStoreInstructionIndexes.Remove(loadedFieldKey);
                        knownFields.Remove(loadedFieldKey);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaFieldAddressRValue fieldAddress
                } valueInstruction:
                    if (TryCreateFieldMemoryKey(fieldAddress, definitions, out var createdFieldKey))
                    {
                        fieldAddressKeys[valueInstruction.ResultName] = createdFieldKey;
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaElementAddressRValue elementAddress
                } valueInstruction:
                    if (TryCreateElementMemoryKey(elementAddress, definitions, out var createdElementKey))
                    {
                        fieldAddressKeys[valueInstruction.ResultName] = createdElementKey;
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadLocalRValue loadLocal
                }:
                    pendingStoreInstructionIndexes.Remove(loadLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    HandleDirectCall(call);
                    instructions.Add(rewritten);
                    continue;

                case SsaCallInstruction call:
                    HandleDirectCall(call);
                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                case SsaIndirectCallInstruction:
                    knownGlobals.Clear();
                    pendingGlobalStoreInstructionIndexes.Clear();
                    knownFields.Clear();
                    pendingFieldStoreInstructionIndexes.Clear();
                    instructions.Add(rewritten);
                    continue;

                case SsaStoreLocalInstruction storeLocal:
                    RemoveKnownFieldLocal(knownFields, storeLocal.LocalName);
                    RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, storeLocal.LocalName);
                    if (eligibleLocals.TryGetValue(storeLocal.LocalName, out var storeLocalType)
                        && storeLocalType == storeLocal.LocalType
                        && storeLocal.Value.Type == storeLocal.LocalType)
                    {
                        knownLocals[storeLocal.LocalName] = RewriteValue(storeLocal.Value, replacements);
                        if (pendingStoreInstructionIndexes.TryGetValue(storeLocal.LocalName, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingStoreInstructionIndexes[storeLocal.LocalName] = instructions.Count;
                    }
                    else
                    {
                        knownLocals.Remove(storeLocal.LocalName);
                        pendingStoreInstructionIndexes.Remove(storeLocal.LocalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaStoreIndirectInstruction storeIndirect:
                    if (IsForwardableScalarMemoryType(storeIndirect.ValueType)
                        && TryResolveFieldMemoryKey(storeIndirect.Address, definitions, fieldAddressKeys, out var storedFieldKey)
                        && storedFieldKey.Type == storeIndirect.ValueType
                        && storeIndirect.Value.Type == storeIndirect.ValueType)
                    {
                        knownFields[storedFieldKey] = RewriteValue(storeIndirect.Value, replacements);
                        if (pendingFieldStoreInstructionIndexes.TryGetValue(storedFieldKey, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingFieldStoreInstructionIndexes[storedFieldKey] = instructions.Count;
                    }
                    else
                    {
                        if (TryResolveFieldAddressRoot(
                                storeIndirect.Address,
                                definitions,
                                new HashSet<string>(StringComparer.Ordinal),
                                out var writtenLocalName,
                                out _))
                        {
                            RemoveKnownFieldLocal(knownFields, writtenLocalName);
                            RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, writtenLocalName);
                        }
                        else
                        {
                            knownGlobals.Clear();
                            pendingGlobalStoreInstructionIndexes.Clear();
                            knownFields.Clear();
                            pendingFieldStoreInstructionIndexes.Clear();
                        }
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaStoreGlobalInstruction storeGlobal:
                    if (IsForwardableScalarMemoryType(storeGlobal.GlobalType)
                        && storeGlobal.Value.Type == storeGlobal.GlobalType)
                    {
                        var globalKey = (storeGlobal.GlobalName, storeGlobal.GlobalType);
                        knownGlobals[globalKey] =
                            RewriteValue(storeGlobal.Value, replacements);
                        if (pendingGlobalStoreInstructionIndexes.TryGetValue(globalKey, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingGlobalStoreInstructionIndexes[globalKey] = instructions.Count;
                    }
                    else
                    {
                        RemoveKnownGlobal(knownGlobals, storeGlobal.GlobalName);
                        RemovePendingGlobalStore(pendingGlobalStoreInstructionIndexes, storeGlobal.GlobalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaCopyMemoryInstruction copyMemory:
                    var sourceIsExactField = TryResolveFieldMemoryKey(
                                                 copyMemory.SourceAddress,
                                                 definitions,
                                                 fieldAddressKeys,
                                                 out var sourceFieldKey)
                                             && sourceFieldKey.Type == copyMemory.CopyType;
                    var destinationIsExactField = TryResolveFieldMemoryKey(
                                                      copyMemory.DestinationAddress,
                                                      definitions,
                                                      fieldAddressKeys,
                                                      out var destinationFieldKey)
                                                  && destinationFieldKey.Type == copyMemory.CopyType;

                    SsaValue? copiedFieldValue = null;
                    if (sourceIsExactField)
                    {
                        pendingFieldStoreInstructionIndexes.Remove(sourceFieldKey);
                        if (destinationIsExactField
                            && sourceFieldKey.Type == destinationFieldKey.Type
                            && knownFields.TryGetValue(sourceFieldKey, out var knownSourceFieldValue))
                        {
                            copiedFieldValue = RewriteValue(knownSourceFieldValue, replacements);
                        }
                    }
                    else
                    {
                        pendingGlobalStoreInstructionIndexes.Clear();
                        pendingFieldStoreInstructionIndexes.Clear();
                    }

                    if (destinationIsExactField)
                    {
                        knownFields.Remove(destinationFieldKey);
                        if (pendingFieldStoreInstructionIndexes.TryGetValue(destinationFieldKey, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingFieldStoreInstructionIndexes.Remove(destinationFieldKey);
                        if (copiedFieldValue is not null)
                        {
                            knownFields[destinationFieldKey] = copiedFieldValue;
                        }
                    }
                    else
                    {
                        knownGlobals.Clear();
                        pendingGlobalStoreInstructionIndexes.Clear();
                        knownFields.Clear();
                        pendingFieldStoreInstructionIndexes.Clear();
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaAllocateLocalInstruction allocateLocal:
                    knownLocals.Remove(allocateLocal.LocalName);
                    pendingStoreInstructionIndexes.Remove(allocateLocal.LocalName);
                    RemoveKnownFieldLocal(knownFields, allocateLocal.LocalName);
                    RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, allocateLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaLifetimeEndInstruction lifetimeEnd:
                    knownLocals.Remove(lifetimeEnd.LocalName);
                    pendingStoreInstructionIndexes.Remove(lifetimeEnd.LocalName);
                    RemoveKnownFieldLocal(knownFields, lifetimeEnd.LocalName);
                    if (closureEnvironmentLocals.Contains(lifetimeEnd.LocalName))
                    {
                        RemovePendingFieldLocalStores(
                            pendingFieldStoreInstructionIndexes,
                            instructions,
                            lifetimeEnd.LocalName,
                            ref blockChanged);
                    }

                    RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, lifetimeEnd.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaDeallocateLocalInstruction deallocateLocal:
                    knownLocals.Remove(deallocateLocal.LocalName);
                    pendingStoreInstructionIndexes.Remove(deallocateLocal.LocalName);
                    RemoveKnownFieldLocal(knownFields, deallocateLocal.LocalName);
                    RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, deallocateLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                default:
                    instructions.Add(rewritten);
                    continue;
            }
        }

        void HandleDirectCall(ISsaDirectCallOperation call)
        {
            if (MayWriteGlobalMemory(call, bodyGlobalMemoryEffects))
            {
                knownGlobals.Clear();
                pendingGlobalStoreInstructionIndexes.Clear();
                knownFields.Clear();
                pendingFieldStoreInstructionIndexes.Clear();
            }
            else
            {
                if (MayReadGlobalMemory(call, definitions, bodyGlobalMemoryEffects))
                {
                    pendingGlobalStoreInstructionIndexes.Clear();
                }

                if (TryGetReadLocalMemorySet(call, definitions, out var readLocalMemory))
                {
                    foreach (var field in readLocalMemory.ExactFields)
                    {
                        pendingFieldStoreInstructionIndexes.Remove(field);
                    }

                    foreach (var localName in readLocalMemory.LocalRoots)
                    {
                        RemovePendingFieldLocal(pendingFieldStoreInstructionIndexes, localName);
                    }
                }
                else
                {
                    pendingFieldStoreInstructionIndexes.Clear();
                }
            }
        }

        var rewrittenTerminator = RewriteTerminator(block.Terminator, replacements);
        if (!EqualityComparer<SsaTerminator>.Default.Equals(rewrittenTerminator, block.Terminator))
        {
            blockChanged = true;
        }

        exitKnownLocals = new Dictionary<string, SsaValue>(knownLocals, StringComparer.Ordinal);
        exitKnownGlobals = new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>(knownGlobals);
        exitKnownFields = new Dictionary<FieldMemoryKey, SsaValue>(knownFields);
        if (!blockChanged)
        {
            return block;
        }

        changed = true;
        return block with
        {
            Instructions = instructions
                .Where(static instruction => instruction is not null)
                .Cast<SsaInstruction>()
                .ToArray(),
            Terminator = rewrittenTerminator
        };
    }

    private static SsaFunction EliminateDeadStackScalarStores(
        SsaFunction function,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        out bool changed)
    {
        var liveOutByBlock = ComputeEligibleLocalLiveOut(function, eligibleLocals);
        changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            var liveOut = liveOutByBlock.TryGetValue(block.Id, out var blockLiveOut)
                ? blockLiveOut
                : new HashSet<string>(StringComparer.Ordinal);
            blocks.Add(EliminateDeadStackScalarStoresInBlock(
                block,
                eligibleLocals,
                liveOut,
                ref changed));
        }

        return changed ? function with { Blocks = blocks.ToArray() } : function;
    }

    private static SsaBasicBlock EliminateDeadStackScalarStoresInBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
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
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }
                    when eligibleLocals.ContainsKey(loadLocal.LocalName):
                    live.Add(loadLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreLocalInstruction storeLocal
                    when IsEligibleStackScalarStore(storeLocal, eligibleLocals):
                    if (!live.Contains(storeLocal.LocalName))
                    {
                        blockChanged = true;
                        continue;
                    }

                    live.Remove(storeLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaAllocateLocalInstruction allocateLocal
                    when IsEligibleStackScalarLocal(allocateLocal.LocalName, allocateLocal.LocalType, eligibleLocals):
                    live.Remove(allocateLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaLifetimeEndInstruction lifetimeEnd
                    when eligibleLocals.ContainsKey(lifetimeEnd.LocalName):
                    live.Remove(lifetimeEnd.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaDeallocateLocalInstruction deallocateLocal
                    when eligibleLocals.ContainsKey(deallocateLocal.LocalName):
                    live.Remove(deallocateLocal.LocalName);
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

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> ComputeEligibleLocalLiveOut(
        SsaFunction function,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
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
            CollectEligibleLocalUseDef(block, eligibleLocals, out var use, out var def);
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

    private static void CollectEligibleLocalUseDef(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
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
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }
                    when eligibleLocals.ContainsKey(loadLocal.LocalName):
                    if (!definedInBlock.Contains(loadLocal.LocalName))
                    {
                        use.Add(loadLocal.LocalName);
                    }

                    break;

                case SsaStoreLocalInstruction storeLocal
                    when IsEligibleStackScalarStore(storeLocal, eligibleLocals):
                    definedInBlock.Add(storeLocal.LocalName);
                    def.Add(storeLocal.LocalName);
                    break;

                case SsaAllocateLocalInstruction allocateLocal
                    when IsEligibleStackScalarLocal(allocateLocal.LocalName, allocateLocal.LocalType, eligibleLocals):
                    definedInBlock.Add(allocateLocal.LocalName);
                    def.Add(allocateLocal.LocalName);
                    break;

                case SsaLifetimeEndInstruction lifetimeEnd
                    when eligibleLocals.ContainsKey(lifetimeEnd.LocalName):
                    definedInBlock.Add(lifetimeEnd.LocalName);
                    def.Add(lifetimeEnd.LocalName);
                    break;

                case SsaDeallocateLocalInstruction deallocateLocal
                    when eligibleLocals.ContainsKey(deallocateLocal.LocalName):
                    definedInBlock.Add(deallocateLocal.LocalName);
                    def.Add(deallocateLocal.LocalName);
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, SsaValue> TryGetSinglePredecessorExitKnownLocals(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValue>> exitKnownLocalsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitKnownLocalsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitKnownLocals)
            ? predecessorExitKnownLocals
            : new Dictionary<string, SsaValue>(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>
        TryGetSinglePredecessorExitKnownGlobals(
            SsaBasicBlock block,
            IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
            IReadOnlyDictionary<int, IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>>
                exitKnownGlobalsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitKnownGlobalsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitKnownGlobals)
            ? predecessorExitKnownGlobals
            : new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>();
    }

    private static IReadOnlyDictionary<FieldMemoryKey, SsaValue> TryGetSinglePredecessorExitKnownFields(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
        IReadOnlyDictionary<int, IReadOnlyDictionary<FieldMemoryKey, SsaValue>> exitKnownFieldsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitKnownFieldsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitKnownFields)
            ? predecessorExitKnownFields
            : new Dictionary<FieldMemoryKey, SsaValue>();
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> BuildPredecessorMap(SsaFunction function)
    {
        var predecessors = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new List<int>(),
            EqualityComparer<int>.Default);

        foreach (var block in function.Blocks)
        {
            foreach (var successor in GetSuccessorBlockIds(block.Terminator))
            {
                if (predecessors.TryGetValue(successor, out var successorPredecessors))
                {
                    successorPredecessors.Add(block.Id);
                }
            }
        }

        return predecessors.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<int>)pair.Value.ToArray());
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> CollectValueUseBlockIds(SsaFunction function)
    {
        var uses = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    AddValueUse(incoming.Value, block.Id, uses);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaValueInstruction valueInstruction:
                        AddRValueUses(valueInstruction.Value, block.Id, uses);
                        break;
                    case SsaStoreLocalInstruction storeLocal:
                        AddValueUse(storeLocal.Value, block.Id, uses);
                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        AddValueUse(copyMemory.DestinationAddress, block.Id, uses);
                        AddValueUse(copyMemory.SourceAddress, block.Id, uses);
                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        AddValueUse(storeIndirect.Address, block.Id, uses);
                        AddValueUse(storeIndirect.Value, block.Id, uses);
                        break;
                    case SsaStoreGlobalInstruction storeGlobal:
                        AddValueUse(storeGlobal.Value, block.Id, uses);
                        break;
                }
            }

            if (block.Terminator.Condition is not null)
            {
                AddValueUse(block.Terminator.Condition, block.Id, uses);
            }

            if (block.Terminator.Value is not null)
            {
                AddValueUse(block.Terminator.Value, block.Id, uses);
            }

            if (block.Terminator.TailDirectCall is not null)
            {
                foreach (var argument in block.Terminator.TailDirectCall.Arguments)
                {
                    AddValueUse(argument, block.Id, uses);
                }

                foreach (var address in block.Terminator.TailDirectCall.IndirectArgumentAddresses ?? [])
                {
                    if (address is not null)
                    {
                        AddValueUse(address, block.Id, uses);
                    }
                }
            }

            if (block.Terminator.TailIndirectCall is not null)
            {
                AddValueUse(block.Terminator.TailIndirectCall.Target, block.Id, uses);
                foreach (var argument in block.Terminator.TailIndirectCall.Arguments)
                {
                    AddValueUse(argument, block.Id, uses);
                }

                foreach (var address in block.Terminator.TailIndirectCall.IndirectArgumentAddresses ?? [])
                {
                    if (address is not null)
                    {
                        AddValueUse(address, block.Id, uses);
                    }
                }
            }

            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    AddValueUse(switchCase.MatchValue, block.Id, uses);
                }
            }
        }

        return uses.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<int>)pair.Value);
    }

    private static bool ValueUsesAreConfinedToBlock(
        string valueName,
        int blockId,
        IReadOnlyDictionary<string, IReadOnlySet<int>> valueUseBlockIds)
    {
        return !valueUseBlockIds.TryGetValue(valueName, out var useBlocks)
               || useBlocks.Count == 0
               || useBlocks.All(useBlock => useBlock == blockId);
    }

    private static void AddValueUse(
        SsaValue value,
        int blockId,
        IDictionary<string, HashSet<int>> uses)
    {
        if (value is not SsaValueReference reference)
        {
            return;
        }

        if (!uses.TryGetValue(reference.Name, out var blocks))
        {
            blocks = new HashSet<int>();
            uses[reference.Name] = blocks;
        }

        blocks.Add(blockId);
    }

    private static void AddRValueUses(
        SsaRValue value,
        int blockId,
        IDictionary<string, HashSet<int>> uses)
    {
        switch (value)
        {
            case SsaUseRValue use:
                AddValueUse(use.Value, blockId, uses);
                break;
            case SsaUnaryRValue unary:
                AddValueUse(unary.Operand, blockId, uses);
                break;
            case SsaBinaryRValue binary:
                AddValueUse(binary.Left, blockId, uses);
                AddValueUse(binary.Right, blockId, uses);
                break;
            case SsaSelectRValue select:
                AddValueUse(select.Condition, blockId, uses);
                AddValueUse(select.WhenTrue, blockId, uses);
                AddValueUse(select.WhenFalse, blockId, uses);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddValueUse(argument, blockId, uses);
                }

                AddOptionalValueUses(call.IndirectArgumentAddresses, blockId, uses);
                break;
            case SsaIndirectCallRValue indirectCall:
                AddValueUse(indirectCall.Target, blockId, uses);
                foreach (var argument in indirectCall.Arguments)
                {
                    AddValueUse(argument, blockId, uses);
                }

                AddOptionalValueUses(indirectCall.IndirectArgumentAddresses, blockId, uses);
                break;
            case SsaConvertRValue convert:
                AddValueUse(convert.Operand, blockId, uses);
                break;
            case SsaExtractFieldRValue extractField:
                AddValueUse(extractField.Target, blockId, uses);
                break;
            case SsaInsertFieldRValue insertField:
                AddValueUse(insertField.Target, blockId, uses);
                AddValueUse(insertField.Value, blockId, uses);
                break;
            case SsaExtractIndexRValue extractIndex:
                AddValueUse(extractIndex.Target, blockId, uses);
                break;
            case SsaInsertIndexRValue insertIndex:
                AddValueUse(insertIndex.Target, blockId, uses);
                AddValueUse(insertIndex.Value, blockId, uses);
                break;
            case SsaMakeSliceFromPointerRValue makeSlice:
                AddValueUse(makeSlice.Pointer, blockId, uses);
                AddValueUse(makeSlice.Length, blockId, uses);
                break;
            case SsaDynamicStorageAllocationRValue allocation:
                AddValueUse(allocation.Capacity, blockId, uses);
                break;
            case SsaDynamicStorageFreeRValue free:
                AddValueUse(free.Storage, blockId, uses);
                break;
            case SsaHeapStorageFreeRValue free:
                AddValueUse(free.Pointer, blockId, uses);
                break;
            case SsaDynamicStorageReserveRValue reserve:
                AddValueUse(reserve.StorageAddress, blockId, uses);
                AddValueUse(reserve.AdditionalCapacity, blockId, uses);
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                AddValueUse(reserve.StorageAddress, blockId, uses);
                AddValueUse(reserve.AdditionalCapacity, blockId, uses);
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                AddValueUse(reserve.StorageAddress, blockId, uses);
                AddValueUse(reserve.TargetCapacity, blockId, uses);
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                AddValueUse(moveLast.StorageAddress, blockId, uses);
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                AddValueUse(moveAt.StorageAddress, blockId, uses);
                AddValueUse(moveAt.Index, blockId, uses);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                AddValueUse(loadSlice.Slice, blockId, uses);
                AddValueUse(loadSlice.Index, blockId, uses);
                break;
            case SsaTextSliceRValue textSlice:
                AddValueUse(textSlice.TextValue, blockId, uses);
                AddValueUse(textSlice.Start, blockId, uses);
                AddValueUse(textSlice.Length, blockId, uses);
                break;
            case SsaFieldAddressRValue fieldAddress:
                AddValueUse(fieldAddress.Address, blockId, uses);
                break;
            case SsaElementAddressRValue elementAddress:
                AddValueUse(elementAddress.Address, blockId, uses);
                if (elementAddress.Index is not null)
                {
                    AddValueUse(elementAddress.Index, blockId, uses);
                }

                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                AddValueUse(sliceElementAddress.Slice, blockId, uses);
                AddValueUse(sliceElementAddress.Index, blockId, uses);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                AddValueUse(loadIndirect.Address, blockId, uses);
                break;
        }
    }

    private static void AddOptionalValueUses(
        IReadOnlyList<SsaValue?>? values,
        int blockId,
        IDictionary<string, HashSet<int>> uses)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (value is not null)
            {
                AddValueUse(value, blockId, uses);
            }
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

    private static IReadOnlyDictionary<string, StarkTypeSymbol> CollectEligibleStackScalarLocals(SsaFunction function)
    {
        var escapedLocals = CollectEscapedLocalNames(function);
        var candidates = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            if (instruction is not SsaAllocateLocalInstruction allocateLocal)
            {
                continue;
            }

            if (string.Equals(allocateLocal.StorageClass, "stack", StringComparison.Ordinal)
                && !escapedLocals.Contains(allocateLocal.LocalName)
                && IsForwardableScalarMemoryType(allocateLocal.LocalType))
            {
                candidates[allocateLocal.LocalName] = allocateLocal.LocalType;
                continue;
            }

            candidates.Remove(allocateLocal.LocalName);
        }

        return candidates;
    }

    private static HashSet<string> CollectEscapedLocalNames(SsaFunction function)
    {
        var escaped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    AddEscapedLocalNames(valueInstruction.Value, escaped);
                    break;
                case SsaCallInstruction call:
                    AddEscapedIndirectArgumentLocalNames(call.IndirectArgumentLocalNames, escaped);
                    break;
                case SsaIndirectCallInstruction call:
                    AddEscapedIndirectArgumentLocalNames(call.IndirectArgumentLocalNames, escaped);
                    break;
            }
        }

        return escaped;
    }

    private static void AddEscapedLocalNames(SsaRValue value, ISet<string> escaped)
    {
        switch (value)
        {
            case SsaAddressOfLocalRValue addressOfLocal:
                escaped.Add(addressOfLocal.LocalName);
                break;

            case SsaMakeSliceFromLocalRValue makeSlice:
                escaped.Add(makeSlice.LocalName);
                break;

            case SsaCallRValue call:
                AddEscapedIndirectArgumentLocalNames(call.IndirectArgumentLocalNames, escaped);
                break;

            case SsaIndirectCallRValue call:
                AddEscapedIndirectArgumentLocalNames(call.IndirectArgumentLocalNames, escaped);
                break;
        }
    }

    private static void AddEscapedIndirectArgumentLocalNames(
        IReadOnlyList<string?>? indirectLocals,
        ISet<string> escaped)
    {
        foreach (var localName in indirectLocals ?? [])
        {
            if (localName is not null)
            {
                escaped.Add(localName);
            }
        }
    }

    private static bool IsForwardableScalarMemoryType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> EmptyBodyGlobalMemoryEffects { get; } =
        new Dictionary<string, FunctionBodyGlobalMemoryEffects>(StringComparer.Ordinal);

    private static IReadOnlySet<string> CollectClosureEnvironmentLocals(SsaFunction function)
    {
        var locals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var allocateLocal in function.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaAllocateLocalInstruction>())
        {
            if (allocateLocal.StorageClass == "stack"
                && allocateLocal.LocalType.DisplayName.EndsWith(".__env", StringComparison.Ordinal))
            {
                locals.Add(allocateLocal.LocalName);
            }
        }

        return locals;
    }

    private static void RemovePendingFieldLocalStores(
        IDictionary<FieldMemoryKey, int> pendingFieldStoreInstructionIndexes,
        IList<SsaInstruction?> instructions,
        string localName,
        ref bool blockChanged)
    {
        foreach (var pair in pendingFieldStoreInstructionIndexes.ToArray())
        {
            if (!string.Equals(pair.Key.LocalName, localName, StringComparison.Ordinal))
            {
                continue;
            }

            instructions[pair.Value] = null;
            pendingFieldStoreInstructionIndexes.Remove(pair.Key);
            blockChanged = true;
        }
    }

    private IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> AnalyzeFunctionBodyGlobalMemoryEffects(
        SsaIrModule module)
    {
        var functions = module.Functions
            .Where(static function => function.HasBody && function.SupportsDirectCodeGeneration)
            .ToArray();
        if (functions.Length == 0)
        {
            return EmptyBodyGlobalMemoryEffects;
        }

        var effects = functions.ToDictionary(
            static function => function.Name,
            static _ => new FunctionBodyGlobalMemoryEffects(false, false),
            StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var function in functions)
            {
                var analyzed = AnalyzeFunctionBodyGlobalMemoryEffects(function, effects);
                if (!EqualityComparer<FunctionBodyGlobalMemoryEffects>.Default.Equals(effects[function.Name], analyzed))
                {
                    effects[function.Name] = analyzed;
                    changed = true;
                }
            }
        }

        return effects;
    }

    private FunctionBodyGlobalMemoryEffects AnalyzeFunctionBodyGlobalMemoryEffects(
        SsaFunction function,
        IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> knownBodyEffects)
    {
        var readsGlobalMemory = false;
        var writesGlobalMemory = false;
        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaValueInstruction { Value: SsaLoadGlobalRValue }:
                        readsGlobalMemory = true;
                        break;

                    case SsaStoreGlobalInstruction:
                        writesGlobalMemory = true;
                        break;

                    case SsaValueInstruction { Value: SsaCallRValue call }:
                        AnalyzeDirectCall(call);
                        break;

                    case SsaCallInstruction call:
                        AnalyzeDirectCall(call);
                        break;

                    case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                    case SsaIndirectCallInstruction:
                        readsGlobalMemory = true;
                        writesGlobalMemory = true;
                        break;
                }
            }
        }

        void AnalyzeDirectCall(ISsaDirectCallOperation call)
        {
            if (MayReadGlobalMemoryFromEffectModel(call, definitions)
                || knownBodyEffects.TryGetValue(call.FunctionName, out var readEffects)
                && readEffects.ReadsGlobalMemory)
            {
                readsGlobalMemory = true;
            }

            if (MayWriteGlobalMemoryFromEffectModel(call)
                || knownBodyEffects.TryGetValue(call.FunctionName, out var writeEffects)
                && writeEffects.WritesGlobalMemory)
            {
                writesGlobalMemory = true;
            }
        }

        return new FunctionBodyGlobalMemoryEffects(readsGlobalMemory, writesGlobalMemory);
    }

    private bool MayWriteGlobalMemory(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> bodyGlobalMemoryEffects)
    {
        return MayWriteGlobalMemoryFromEffectModel(call)
               || bodyGlobalMemoryEffects.TryGetValue(call.FunctionName, out var bodyEffects)
               && bodyEffects.WritesGlobalMemory;
    }

    private bool MayWriteGlobalMemoryFromEffectModel(ISsaDirectCallOperation call)
    {
        return _effectModel is not { } effectModel
               || !effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
               || !effects.IsPure
               || !effects.NoSync
               // A callee that writes memory beyond its arguments may write globals.
               || effects.WritesOtherMemory;
    }

    private bool MayReadGlobalMemory(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, FunctionBodyGlobalMemoryEffects> bodyGlobalMemoryEffects)
    {
        return MayReadGlobalMemoryFromEffectModel(call, definitions)
               || bodyGlobalMemoryEffects.TryGetValue(call.FunctionName, out var bodyEffects)
               && bodyEffects.ReadsGlobalMemory;
    }

    private bool MayReadGlobalMemoryFromEffectModel(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return _effectModel is not { } effectModel
               || !effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
               || !effects.IsPure
               || !effects.NoSync
               // A callee that reads memory beyond its arguments (e.g. through a
               // dyn trait object's data pointer) may read global memory.
               || effects.ReadsOtherMemory
               || effects.ReadsArgumentMemory && EnumerateCallMemoryArguments(call).Any(argument =>
                   ValueMayReferenceGlobalMemory(
                       argument,
                       definitions,
                       new HashSet<string>(StringComparer.Ordinal)));
    }

    private bool TryGetReadLocalMemorySet(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out LocalMemoryReadSet readSet)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var exactFields = new HashSet<FieldMemoryKey>();
        readSet = new LocalMemoryReadSet(roots, exactFields);

        if (_effectModel is not { } effectModel
            || !effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
            || !effects.IsPure
            || !effects.NoSync)
        {
            return false;
        }

        if (effects.ReadsOtherMemory)
        {
            // The callee reads memory beyond its arguments (e.g. the object behind a
            // dyn trait object's data pointer, or memory reached through a global). It
            // can only reach a local through a pointer to that local, so the locals it
            // may read are exactly those whose address has escaped in this function.
            // Locals whose address is never taken cannot be observed by such a callee.
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

    private static bool ValueMayReferenceGlobalMemory(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaValueReference reference
                when visitedValueNames.Add(reference.Name)
                     && definitions.TryGetValue(reference.Name, out var definition) =>
                RValueMayReferenceGlobalMemory(definition, definitions, visitedValueNames),
            SsaValueReference reference when IsPotentialMemoryReferenceType(reference.Type) => true,
            _ => false
        };
    }

    private static bool RValueMayReferenceGlobalMemory(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaUseRValue use => ValueMayReferenceGlobalMemory(use.Value, definitions, visitedValueNames),
            SsaSelectRValue select => ValueMayReferenceGlobalMemory(select.WhenTrue, definitions, visitedValueNames)
                                      || ValueMayReferenceGlobalMemory(select.WhenFalse, definitions, visitedValueNames),
            SsaConvertRValue convert => ValueMayReferenceGlobalMemory(convert.Operand, definitions, visitedValueNames),
            SsaFieldAddressRValue fieldAddress => ValueMayReferenceGlobalMemory(fieldAddress.Address, definitions, visitedValueNames),
            SsaElementAddressRValue elementAddress => ValueMayReferenceGlobalMemory(elementAddress.Address, definitions, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => ValueMayReferenceGlobalMemory(sliceElementAddress.Slice, definitions, visitedValueNames),
            SsaTextSliceRValue textSlice => ValueMayReferenceGlobalMemory(textSlice.TextValue, definitions, visitedValueNames),
            _ => false
        };
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
        ISet<FieldMemoryKey> exactFields)
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
        ISet<FieldMemoryKey> exactFields)
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
                if (TryCreateFieldMemoryKey(fieldAddress, definitions, out var fieldKey))
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
                if (TryCreateElementMemoryKey(elementAddress, definitions, out var elementKey))
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

    private static bool TryResolveFieldMemoryKey(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, FieldMemoryKey> fieldAddressKeys,
        out FieldMemoryKey key)
    {
        key = default!;

        switch (address)
        {
            case SsaValueReference reference:
                if (fieldAddressKeys.TryGetValue(reference.Name, out key))
                {
                    return true;
                }

                if (!definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return definition switch
                {
                    SsaFieldAddressRValue fieldAddress => TryCreateFieldMemoryKey(fieldAddress, definitions, out key),
                    SsaElementAddressRValue elementAddress => TryCreateElementMemoryKey(elementAddress, definitions, out key),
                    _ => false
                };

            default:
                return false;
        }
    }

    private static bool TryCreateFieldMemoryKey(
        SsaFieldAddressRValue fieldAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldMemoryKey key)
    {
        key = default!;
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

        key = new FieldMemoryKey(
            localName,
            AppendFieldPath(parentFieldPath, fieldAddress.FieldIndex, fieldAddress.FieldName),
            fieldType);
        return true;
    }

    private static bool TryCreateElementMemoryKey(
        SsaElementAddressRValue elementAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out FieldMemoryKey key)
    {
        key = default!;
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

        key = new FieldMemoryKey(
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
        switch (address)
        {
            case SsaValueReference reference
                when visitedValueNames.Add(reference.Name)
                     && definitions.TryGetValue(reference.Name, out var definition):
                return definition switch
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

            default:
                return false;
        }
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

    private static void RemoveKnownFieldLocal(
        IDictionary<FieldMemoryKey, SsaValue> knownFields,
        string localName)
    {
        foreach (var key in knownFields.Keys
            .Where(key => string.Equals(key.LocalName, localName, StringComparison.Ordinal))
            .ToArray())
        {
            knownFields.Remove(key);
        }
    }

    private static void RemovePendingFieldLocal(
        IDictionary<FieldMemoryKey, int> pendingFieldStoreInstructionIndexes,
        string localName)
    {
        foreach (var key in pendingFieldStoreInstructionIndexes.Keys
            .Where(key => string.Equals(key.LocalName, localName, StringComparison.Ordinal))
            .ToArray())
        {
            pendingFieldStoreInstructionIndexes.Remove(key);
        }
    }

    private static void RemoveKnownGlobal(
        IDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> knownGlobals,
        string globalName)
    {
        foreach (var key in knownGlobals.Keys
            .Where(key => string.Equals(key.GlobalName, globalName, StringComparison.Ordinal))
            .ToArray())
        {
            knownGlobals.Remove(key);
        }
    }

    private static void RemovePendingGlobalStore(
        IDictionary<(string GlobalName, StarkTypeSymbol Type), int> pendingGlobalStoreInstructionIndexes,
        string globalName)
    {
        foreach (var key in pendingGlobalStoreInstructionIndexes.Keys
            .Where(key => string.Equals(key.GlobalName, globalName, StringComparison.Ordinal))
            .ToArray())
        {
            pendingGlobalStoreInstructionIndexes.Remove(key);
        }
    }

    private static bool IsEligibleStackScalarStore(
        SsaStoreLocalInstruction storeLocal,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
    {
        return IsEligibleStackScalarLocal(storeLocal.LocalName, storeLocal.LocalType, eligibleLocals)
               && storeLocal.Value.Type == storeLocal.LocalType;
    }

    private static bool IsEligibleStackScalarLocal(
        string localName,
        StarkTypeSymbol localType,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
    {
        return eligibleLocals.TryGetValue(localName, out var eligibleType)
               && eligibleType == localType;
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction with
            {
                Value = RewriteRValue(valueInstruction.Value, replacements)
            },
            SsaCallInstruction call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaIndirectCallInstruction call => call with
            {
                Target = RewriteValue(call.Target, replacements),
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaStoreLocalInstruction storeLocal => storeLocal with
            {
                Value = RewriteValue(storeLocal.Value, replacements)
            },
            SsaCopyMemoryInstruction copyMemory => copyMemory with
            {
                DestinationAddress = RewriteValue(copyMemory.DestinationAddress, replacements),
                SourceAddress = RewriteValue(copyMemory.SourceAddress, replacements)
            },
            SsaStoreIndirectInstruction storeIndirect => storeIndirect with
            {
                Address = RewriteValue(storeIndirect.Address, replacements),
                Value = RewriteValue(storeIndirect.Value, replacements)
            },
            SsaStoreGlobalInstruction storeGlobal => storeGlobal with
            {
                Value = RewriteValue(storeGlobal.Value, replacements)
            },
            _ => instruction
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return value switch
        {
            SsaUseRValue use => use with
            {
                Value = RewriteValue(use.Value, replacements)
            },
            SsaUnaryRValue unary => unary with
            {
                Operand = RewriteValue(unary.Operand, replacements)
            },
            SsaBinaryRValue binary => binary with
            {
                Left = RewriteValue(binary.Left, replacements),
                Right = RewriteValue(binary.Right, replacements)
            },
            SsaSelectRValue select => select with
            {
                Condition = RewriteValue(select.Condition, replacements),
                WhenTrue = RewriteValue(select.WhenTrue, replacements),
                WhenFalse = RewriteValue(select.WhenFalse, replacements)
            },
            SsaCallRValue call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaIndirectCallRValue indirectCall => indirectCall with
            {
                Target = RewriteValue(indirectCall.Target, replacements),
                Arguments = indirectCall.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = indirectCall.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaConvertRValue convert => convert with
            {
                Operand = RewriteValue(convert.Operand, replacements)
            },
            SsaExtractFieldRValue extractField => extractField with
            {
                Target = RewriteValue(extractField.Target, replacements)
            },
            SsaInsertFieldRValue insertField => insertField with
            {
                Target = RewriteValue(insertField.Target, replacements),
                Value = RewriteValue(insertField.Value, replacements)
            },
            SsaExtractIndexRValue extractIndex => extractIndex with
            {
                Target = RewriteValue(extractIndex.Target, replacements)
            },
            SsaInsertIndexRValue insertIndex => insertIndex with
            {
                Target = RewriteValue(insertIndex.Target, replacements),
                Value = RewriteValue(insertIndex.Value, replacements)
            },
            SsaMakeSliceFromPointerRValue makeSlice => makeSlice with
            {
                Pointer = RewriteValue(makeSlice.Pointer, replacements),
                Length = RewriteValue(makeSlice.Length, replacements)
            },
            SsaDynamicStorageAllocationRValue allocation => allocation with
            {
                Capacity = RewriteValue(allocation.Capacity, replacements)
            },
            SsaDynamicStorageFreeRValue free => free with
            {
                Storage = RewriteValue(free.Storage, replacements)
            },
            SsaHeapStorageFreeRValue free => free with
            {
                Pointer = RewriteValue(free.Pointer, replacements)
            },
            SsaDynamicStorageReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveCapacityRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                TargetCapacity = RewriteValue(reserve.TargetCapacity, replacements)
            },
            SsaDynamicStorageMoveLastRValue moveLast => moveLast with
            {
                StorageAddress = RewriteValue(moveLast.StorageAddress, replacements)
            },
            SsaDynamicStorageMoveAtRValue moveAt => moveAt with
            {
                StorageAddress = RewriteValue(moveAt.StorageAddress, replacements),
                Index = RewriteValue(moveAt.Index, replacements)
            },
            SsaLoadSliceElementRValue loadSlice => loadSlice with
            {
                Slice = RewriteValue(loadSlice.Slice, replacements),
                Index = RewriteValue(loadSlice.Index, replacements)
            },
            SsaTextSliceRValue textSlice => textSlice with
            {
                TextValue = RewriteValue(textSlice.TextValue, replacements),
                Start = RewriteValue(textSlice.Start, replacements),
                Length = RewriteValue(textSlice.Length, replacements)
            },
            SsaFieldAddressRValue fieldAddress => fieldAddress with
            {
                Address = RewriteValue(fieldAddress.Address, replacements)
            },
            SsaElementAddressRValue elementAddress => elementAddress with
            {
                Address = RewriteValue(elementAddress.Address, replacements),
                Index = elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements)
            },
            SsaSliceElementAddressRValue sliceElementAddress => sliceElementAddress with
            {
                Slice = RewriteValue(sliceElementAddress.Slice, replacements),
                Index = RewriteValue(sliceElementAddress.Index, replacements)
            },
            SsaLoadIndirectRValue loadIndirect => loadIndirect with
            {
                Address = RewriteValue(loadIndirect.Address, replacements)
            },
            _ => value
        };
    }

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return terminator with
        {
            Condition = terminator.Condition is null
                ? null
                : RewriteValue(terminator.Condition, replacements),
            Value = terminator.Value is null
                ? null
                : RewriteValue(terminator.Value, replacements),
            SwitchCases = terminator.SwitchCases?
                .Select(switchCase => switchCase with
                {
                    MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                })
                .ToArray()
        };
    }

    private static SsaValue RewriteValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (value is SsaValueReference reference
            && replacements.TryGetValue(reference.Name, out var replacement)
            && seen.Add(reference.Name))
        {
            value = replacement;
        }

        return value;
    }
}
