using System.Numerics;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private static readonly StarkTypeSymbol SeparateStoragePointerType =
        StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);

    private sealed record FreshDynamicStoragePointer(
        string LocalName,
        SsaValueReference Pointer,
        int BlockId,
        int InstructionIndex);

    private void TrackFreshDynamicLocalStorageAfterStore(SsaStoreLocalInstruction storeLocal)
    {
        if (storeLocal.LocalType.Kind != StarkTypeKind.Dynamic)
        {
            return;
        }

        _freshDynamicLocalStoragePointers.Remove(storeLocal.LocalName);
        if (_currentBlock is null
            || _currentInstructionIndex < 0
            || !TryGetPositiveFreshDynamicAllocation(
                storeLocal.Value,
                _valueDefinitions,
                _valueFacts,
                out _))
        {
            return;
        }

        var current = new FreshDynamicStoragePointer(
            storeLocal.LocalName,
            EmitDynamicStorageDataPointerForSeparateStorage(storeLocal),
            _currentBlock.Id,
            _currentInstructionIndex);

        foreach (var prior in _freshDynamicLocalStoragePointers.Values.ToArray())
        {
            if (!string.Equals(prior.LocalName, current.LocalName, StringComparison.Ordinal)
                && FreshDynamicPointerDominatesCurrent(prior))
            {
                EmitSeparateStorageAssume(prior, current);
            }
        }

        _freshDynamicLocalStoragePointers[storeLocal.LocalName] = current;
    }

    private void ForgetFreshDynamicLocalStorage(string localName, StarkTypeSymbol localType)
    {
        if (localType.Kind == StarkTypeKind.Dynamic)
        {
            _freshDynamicLocalStoragePointers.Remove(localName);
        }
    }

    private SsaValueReference EmitDynamicStorageDataPointerForSeparateStorage(SsaStoreLocalInstruction storeLocal)
    {
        var pointerName = CreateAbiTempName($"separate_storage_{storeLocal.LocalName}_data");
        var pointer = $"%{EscapeIdentifier(pointerName)}";
        AppendLine($"  {pointer} = extractvalue {MapType(storeLocal.LocalType)} {FormatValue(storeLocal.Value)}, 0");
        return new SsaValueReference(pointerName, SeparateStoragePointerType);
    }

    private bool FreshDynamicPointerDominatesCurrent(FreshDynamicStoragePointer pointer)
    {
        if (_currentBlock is null)
        {
            return false;
        }

        return pointer.BlockId == _currentBlock.Id
            ? pointer.InstructionIndex < _currentInstructionIndex
            : GetBlockDominance().Dominates(pointer.BlockId, _currentBlock.Id);
    }

    private BlockDominanceIndex GetBlockDominance()
    {
        return _blockDominance ??= BlockDominanceIndex.Build(_ssaFunction);
    }

    private void EmitSeparateStorageAssume(FreshDynamicStoragePointer left, FreshDynamicStoragePointer right)
    {
        var pairKey = BuildParameterPairKey(left.Pointer.Name, right.Pointer.Name);
        if (!_emittedSeparateStoragePairs.Add(pairKey))
        {
            return;
        }

        var bundle = new LlvmAssumeOperandBundle(
            LlvmAssumeOperandBundleKind.SeparateStorage,
            left.Pointer,
            OtherPointer: right.Pointer);
        AppendLine($"  call void @llvm.assume(i1 true) [{RenderAssumeOperandBundle(bundle)}]");
    }

    private static bool MayEmitSeparateStorageAssume(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlyDictionary<string, SsaValueFacts>? valueFacts = null)
    {
        BlockDominanceIndex? dominance = null;
        var freshStores = new List<(string LocalName, int BlockId, int InstructionIndex)>();
        foreach (var block in function.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (block.Instructions[instructionIndex] is not SsaStoreLocalInstruction { LocalType.Kind: StarkTypeKind.Dynamic } storeLocal)
                {
                    continue;
                }

                if (!TryGetPositiveFreshDynamicAllocation(
                    storeLocal.Value,
                    valueDefinitions,
                    valueFacts,
                    out _))
                {
                    freshStores.RemoveAll(entry => string.Equals(entry.LocalName, storeLocal.LocalName, StringComparison.Ordinal));
                    continue;
                }

                foreach (var prior in freshStores)
                {
                    if (string.Equals(prior.LocalName, storeLocal.LocalName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var priorDominatesStore = prior.BlockId == block.Id
                        ? prior.InstructionIndex < instructionIndex
                        : (dominance ??= BlockDominanceIndex.Build(function)).Dominates(prior.BlockId, block.Id);
                    if (priorDominatesStore)
                    {
                        return true;
                    }
                }

                freshStores.RemoveAll(entry => string.Equals(entry.LocalName, storeLocal.LocalName, StringComparison.Ordinal));
                freshStores.Add((storeLocal.LocalName, block.Id, instructionIndex));
            }
        }

        return false;
    }

    private static bool TryGetPositiveFreshDynamicAllocation(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlyDictionary<string, SsaValueFacts>? valueFacts,
        out SsaDynamicStorageAllocationRValue allocation)
    {
        return TryGetPositiveFreshDynamicAllocation(
            value,
            valueDefinitions,
            valueFacts,
            new HashSet<string>(StringComparer.Ordinal),
            out allocation);
    }

    private static bool TryGetPositiveFreshDynamicAllocation(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlyDictionary<string, SsaValueFacts>? valueFacts,
        ISet<string> visitedValueNames,
        out SsaDynamicStorageAllocationRValue allocation)
    {
        allocation = default!;
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name)
            || !valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaDynamicStorageAllocationRValue dynamicAllocation
                when dynamicAllocation.Type.Kind == StarkTypeKind.Dynamic
                     && IsProvenPositiveIntegerValue(
                         dynamicAllocation.Capacity,
                         valueDefinitions,
                         valueFacts,
                         visitedValueNames):
                allocation = dynamicAllocation;
                return true;
            case SsaUseRValue use:
                return TryGetPositiveFreshDynamicAllocation(
                    use.Value,
                    valueDefinitions,
                    valueFacts,
                    visitedValueNames,
                    out allocation);
            default:
                return false;
        }
    }

    private static bool IsProvenPositiveIntegerValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlyDictionary<string, SsaValueFacts>? valueFacts,
        ISet<string> visitedValueNames)
    {
        return TryGetIntegerValueRange(value, valueDefinitions, valueFacts, visitedValueNames, out var range)
            && range.Min > BigInteger.Zero;
    }

    private bool TryGetIntegerValueRange(SsaValue value, out SsaIntegerRangeFact range)
    {
        return TryGetIntegerValueRange(
            value,
            _valueDefinitions,
            _valueFacts,
            new HashSet<string>(StringComparer.Ordinal),
            out range);
    }

    private static bool TryGetIntegerValueRange(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlyDictionary<string, SsaValueFacts>? valueFacts,
        ISet<string> visitedValueNames,
        out SsaIntegerRangeFact range)
    {
        if (value is SsaIntegerConstant constant)
        {
            range = new SsaIntegerRangeFact(constant.Value, constant.Value);
            return true;
        }

        if (TryGetIntegerTypeRange(value.Type, out range))
        {
            return true;
        }

        if (value is not SsaValueReference reference)
        {
            range = default!;
            return false;
        }

        if (valueFacts is not null
            && valueFacts.TryGetValue(reference.Name, out var facts)
            && facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } integerRange)
        {
            range = integerRange;
            return true;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            range = default!;
            return false;
        }

        range = default!;
        return definition switch
        {
            SsaUseRValue use => TryGetIntegerValueRange(use.Value, valueDefinitions, valueFacts, visitedValueNames, out range),
            SsaConvertRValue { TargetType: { } targetType } => TryGetIntegerTypeRange(targetType, out range),
            _ => false
        };
    }

    private static bool TryGetIntegerTypeRange(StarkTypeSymbol type, out SsaIntegerRangeFact range)
    {
        if (type.Kind == StarkTypeKind.Integer
            && type.RangeMin is { } min
            && type.RangeMax is { } max)
        {
            range = new SsaIntegerRangeFact(min, max);
            return true;
        }

        range = default!;
        return false;
    }
}
