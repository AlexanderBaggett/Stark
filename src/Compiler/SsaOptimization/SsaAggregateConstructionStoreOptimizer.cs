namespace Stark.Compiler;

internal sealed class SsaAggregateConstructionStoreOptimizer
{
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;

    public SsaAggregateConstructionStoreOptimizer(IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        _namedTypes = namedTypes;
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
            || function.Blocks.Count == 0
            || function.Ownership is null)
        {
            return function;
        }

        var directConstructionLocals = function.Ownership.Roots
            .Where(static root => root.RootKind == OwnershipStorageRootKind.Local
                                  && !root.IsAddressTaken
                                  && !root.HasRawPointerEscape)
            .Select(static root => root.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (directConstructionLocals.Count == 0)
        {
            return function;
        }

        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
        var usedNames = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.ResultName)
            .Concat(function.Blocks.SelectMany(static block => block.Phis).Select(static phi => phi.ResultName))
            .ToHashSet(StringComparer.Ordinal);

        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            blocks.Add(OptimizeBlock(block, definitions, directConstructionLocals, usedNames, ref changed));
        }

        return changed
            ? function with { Blocks = blocks.ToArray() }
            : function;
    }

    private SsaBasicBlock OptimizeBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> directConstructionLocals,
        HashSet<string> usedNames,
        ref bool changed)
    {
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        var blockChanged = false;

        foreach (var instruction in block.Instructions)
        {
            if (instruction is SsaStoreLocalInstruction store
                && directConstructionLocals.Contains(store.LocalName)
                && TryCollectFullConstruction(store.Value, store.LocalType, definitions, out var inserts))
            {
                LowerConstructionStoreToFieldStores(store, inserts, usedNames, instructions);
                blockChanged = true;
                continue;
            }

            instructions.Add(instruction);
        }

        if (!blockChanged)
        {
            return block;
        }

        changed = true;
        return block with { Instructions = instructions.ToArray() };
    }

    private void LowerConstructionStoreToFieldStores(
        SsaStoreLocalInstruction store,
        IReadOnlyList<SsaInsertFieldRValue> inserts,
        HashSet<string> usedNames,
        List<SsaInstruction> instructions)
    {
        var addressType = StarkTypeSymbols.RawPointer(store.LocalType, isMutable: true);
        var localAddressName = AllocateName(usedNames, $"{store.LocalName}_construct_addr");
        var localAddress = new SsaValueReference(localAddressName, addressType);
        instructions.Add(new SsaValueInstruction(
            localAddressName,
            new SsaAddressOfLocalRValue(
                store.LocalName,
                store.LocalType,
                addressType,
                $"&{store.LocalName}"),
            store.Location));

        foreach (var insert in inserts.OrderBy(static insert => insert.FieldIndex))
        {
            var fieldAddressName = AllocateName(usedNames, $"{store.LocalName}_{insert.FieldName}_addr");
            var fieldAddressType = StarkTypeSymbols.RawPointer(insert.Value.Type, isMutable: true);
            var fieldAddress = new SsaValueReference(fieldAddressName, fieldAddressType);
            instructions.Add(new SsaValueInstruction(
                fieldAddressName,
                new SsaFieldAddressRValue(
                    localAddress,
                    store.LocalType,
                    insert.FieldName,
                    insert.FieldIndex,
                    fieldAddressType,
                    $"&{store.LocalName}.{insert.FieldName}"),
                store.Location));
            instructions.Add(new SsaStoreIndirectInstruction(
                fieldAddress,
                insert.Value.Type,
                insert.Value,
                store.Location,
                WriteKind: store.WriteKind));
        }
    }

    private bool TryCollectFullConstruction(
        SsaValue value,
        StarkTypeSymbol aggregateType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out IReadOnlyList<SsaInsertFieldRValue> inserts)
    {
        inserts = [];
        if (!TryGetNamedType(aggregateType, out var namedType)
            || namedType.OrderedFields.Count == 0
            || !TryCollectConstructionChain(value, aggregateType, definitions, [], out var collected)
            || collected.Count != namedType.OrderedFields.Count)
        {
            return false;
        }

        var seen = new bool[namedType.OrderedFields.Count];
        foreach (var insert in collected)
        {
            if (insert.FieldIndex < 0
                || insert.FieldIndex >= seen.Length
                || seen[insert.FieldIndex]
                || !string.Equals(namedType.OrderedFields[insert.FieldIndex].Name, insert.FieldName, StringComparison.Ordinal))
            {
                return false;
            }

            seen[insert.FieldIndex] = true;
        }

        inserts = collected;
        return true;
    }

    private static bool TryCollectConstructionChain(
        SsaValue value,
        StarkTypeSymbol aggregateType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        HashSet<string> visited,
        out List<SsaInsertFieldRValue> inserts)
    {
        inserts = [];
        if (value is SsaZeroInitializerValue directZero && directZero.Type == aggregateType)
        {
            return true;
        }

        if (value is not SsaValueReference reference
            || !visited.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaUseRValue { Value: SsaZeroInitializerValue zero } when zero.Type == aggregateType:
                return true;
            case SsaInsertFieldRValue insert when insert.Type == aggregateType:
                if (!TryCollectConstructionChain(insert.Target, aggregateType, definitions, visited, out inserts))
                {
                    return false;
                }

                inserts.Add(insert);
                return true;
            default:
                return false;
        }
    }

    private bool TryGetNamedType(StarkTypeSymbol type, out NamedTypeSymbol namedType)
    {
        namedType = default!;
        if (type.Kind != StarkTypeKind.Named || type.NamedType is null)
        {
            return false;
        }

        if (_namedTypes.TryGetValue(type.NamedType, out namedType!))
        {
            return true;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
        return _namedTypes.TryGetValue(baseName, out namedType!);
    }

    private static string AllocateName(HashSet<string> usedNames, string hint)
    {
        var candidate = $"$ssa_{hint}";
        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        for (var index = 1; ; index++)
        {
            candidate = $"$ssa_{hint}_{index}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
