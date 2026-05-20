using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
    {
        if (IsDirectAggregateAliasCandidateLocal(allocateLocal.LocalName))
        {
            _deferredAliasLocalAllocations[allocateLocal.LocalName] = allocateLocal;
            return;
        }

        EnsureLocalSlotExists(allocateLocal.LocalName, allocateLocal.LocalType);
        EmitLocalDebugDeclare(
            GetLocalSlotPointer(allocateLocal.LocalName),
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
        if (ShouldSuppressLifetimeEnd(lifetimeEnd))
        {
            return;
        }

        EmitLifetimeMarker("end", lifetimeEnd.LocalName, lifetimeEnd.LocalType);
    }

    private void EmitDeallocateLocal(SsaDeallocateLocalInstruction deallocateLocal)
    {
        if (deallocateLocal.StorageClass != "heap")
        {
            throw new UnsupportedBodyEmissionException(
                $"Local storage class '{deallocateLocal.StorageClass}' is invalid for LLVM deallocation.");
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

        if (phase == "start" && IsDirectAggregateAliasCandidateLocal(localName))
        {
            _deferredAliasLifetimeStarts[localName] = new SsaLifetimeStartInstruction(localName, localType, null);
            return;
        }

        EnsureLocalSlotExists(localName, localType);
        AppendLine($"  call void @llvm.lifetime.{phase}.p0(i64 {layout.SizeBytes}, ptr {GetLocalSlotPointer(localName)})");
    }

    private bool ShouldSuppressLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd)
    {
        return ShouldSuppressLifetimeEnd(lifetimeEnd, _currentInstructionIndex);
    }

    private bool ShouldSuppressLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd, int lifetimeEndInstructionIndex)
    {
        if (_currentBlock is null || lifetimeEndInstructionIndex < 0)
        {
            return false;
        }

        for (var index = lifetimeEndInstructionIndex + 1; index < _currentBlock.Instructions.Count; index++)
        {
            var instruction = _currentBlock.Instructions[index];
            if (instruction is SsaLifetimeStartInstruction lifetimeStart
                && string.Equals(lifetimeStart.LocalName, lifetimeEnd.LocalName, StringComparison.Ordinal))
            {
                return false;
            }

            if (InstructionTransitivelyUsesLocalStorage(instruction, lifetimeEnd.LocalName))
            {
                return true;
            }
        }

        return TerminatorTransitivelyUsesLocalStorage(_currentBlock.Terminator, lifetimeEnd.LocalName);
    }

    private bool InstructionTransitivelyUsesLocalStorage(SsaInstruction instruction, string localName)
    {
        return instruction switch
        {
            SsaValueInstruction { Value: SsaCallRValue call } => CallMayAddressForwardLocalAfterLifetimeEnd(call, localName),
            SsaCallInstruction call => CallMayAddressForwardLocalAfterLifetimeEnd(call, localName),
            SsaStoreLocalInstruction storeLocal => CanForwardAggregateValueToAddress(storeLocal.LocalType, storeLocal.Value.Type)
                && ValueTransitivelyUsesLocalStorage(storeLocal.Value, localName),
            SsaStoreIndirectInstruction storeIndirect => (CanForwardAggregateValueToAddress(storeIndirect.ValueType, storeIndirect.Value.Type)
                    && ValueTransitivelyUsesLocalStorage(storeIndirect.Value, localName))
                || ValueTransitivelyUsesLocalStorage(storeIndirect.Address, localName),
            SsaStoreGlobalInstruction storeGlobal => CanForwardAggregateValueToAddress(storeGlobal.GlobalType, storeGlobal.Value.Type)
                && ValueTransitivelyUsesLocalStorage(storeGlobal.Value, localName),
            SsaCopyMemoryInstruction copyMemory => ValueTransitivelyUsesLocalStorage(copyMemory.DestinationAddress, localName)
                || ValueTransitivelyUsesLocalStorage(copyMemory.SourceAddress, localName),
            _ => false
        };
    }

    private bool TerminatorTransitivelyUsesLocalStorage(SsaTerminator terminator, string localName)
    {
        return terminator is { Kind: SsaTerminatorKind.Return, Value: { } value }
            && _abiFunction.ReturnsIndirect
            && CanForwardAggregateValueToAddress(_function.ReturnType, value.Type)
            && ValueTransitivelyUsesLocalStorage(value, localName);
    }

    private bool CallMayAddressForwardLocalAfterLifetimeEnd(ISsaDirectCallOperation call, string localName)
    {
        var calleeAbi = _resolveCallAbi(_function.Name, call.FunctionName);
        if (calleeAbi is null)
        {
            return false;
        }

        for (var index = 0; index < call.Arguments.Count && index < calleeAbi.UserParameters.Count; index++)
        {
            var parameter = calleeAbi.UserParameters[index];
            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && CanForwardAggregateValueToAddress(parameter.SourceType, call.Arguments[index].Type)
                && ValueTransitivelyUsesLocalStorage(call.Arguments[index], localName))
            {
                return true;
            }
        }

        return false;
    }

    private bool ValueTransitivelyUsesLocalStorage(SsaValue? value, string localName)
    {
        return ValueTransitivelyUsesLocalStorage(value, localName, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool ValueTransitivelyUsesLocalStorage(SsaValue? value, string localName, ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaLoadLocalRValue loadLocal => string.Equals(loadLocal.LocalName, localName, StringComparison.Ordinal),
            SsaAddressOfLocalRValue addressOfLocal => string.Equals(addressOfLocal.LocalName, localName, StringComparison.Ordinal),
            _ => RValueTransitivelyUsesLocalStorage(definition, localName, visitedValueNames)
        };
    }

    private bool RValueTransitivelyUsesLocalStorage(SsaRValue value, string localName)
    {
        return RValueTransitivelyUsesLocalStorage(value, localName, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool RValueTransitivelyUsesLocalStorage(SsaRValue value, string localName, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaUseRValue use => ValueTransitivelyUsesLocalStorage(use.Value, localName, visitedValueNames),
            SsaUnaryRValue unary => ValueTransitivelyUsesLocalStorage(unary.Operand, localName, visitedValueNames),
            SsaBinaryRValue binary => ValueTransitivelyUsesLocalStorage(binary.Left, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(binary.Right, localName, visitedValueNames),
            SsaSelectRValue select => ValueTransitivelyUsesLocalStorage(select.Condition, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(select.WhenTrue, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(select.WhenFalse, localName, visitedValueNames),
            SsaCallRValue call => call.Arguments.Any(argument => ValueTransitivelyUsesLocalStorage(argument, localName, visitedValueNames))
                || (call.IndirectArgumentAddresses?.OfType<SsaValue>().Any(address => ValueTransitivelyUsesLocalStorage(address, localName, visitedValueNames)) ?? false),
            SsaIndirectCallRValue indirectCall => ValueTransitivelyUsesLocalStorage(indirectCall.Target, localName, visitedValueNames)
                || indirectCall.Arguments.Any(argument => ValueTransitivelyUsesLocalStorage(argument, localName, visitedValueNames))
                || (indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>().Any(address => ValueTransitivelyUsesLocalStorage(address, localName, visitedValueNames)) ?? false),
            SsaConvertRValue convert => ValueTransitivelyUsesLocalStorage(convert.Operand, localName, visitedValueNames),
            SsaExtractFieldRValue extractField => ValueTransitivelyUsesLocalStorage(extractField.Target, localName, visitedValueNames),
            SsaInsertFieldRValue insertField => ValueTransitivelyUsesLocalStorage(insertField.Target, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(insertField.Value, localName, visitedValueNames),
            SsaExtractIndexRValue extractIndex => ValueTransitivelyUsesLocalStorage(extractIndex.Target, localName, visitedValueNames),
            SsaInsertIndexRValue insertIndex => ValueTransitivelyUsesLocalStorage(insertIndex.Target, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(insertIndex.Value, localName, visitedValueNames),
            SsaMakeSliceFromPointerRValue makeSlice => ValueTransitivelyUsesLocalStorage(makeSlice.Pointer, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(makeSlice.Length, localName, visitedValueNames),
            SsaDynamicStorageAllocationRValue allocation => ValueTransitivelyUsesLocalStorage(allocation.Capacity, localName, visitedValueNames),
            SsaDynamicStorageFreeRValue free => ValueTransitivelyUsesLocalStorage(free.Storage, localName, visitedValueNames),
            SsaHeapStorageFreeRValue free => ValueTransitivelyUsesLocalStorage(free.Pointer, localName, visitedValueNames),
            SsaDynamicStorageReserveRValue reserve => ValueTransitivelyUsesLocalStorage(reserve.StorageAddress, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(reserve.AdditionalCapacity, localName, visitedValueNames),
            SsaDynamicStorageTryReserveRValue reserve => ValueTransitivelyUsesLocalStorage(reserve.StorageAddress, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(reserve.AdditionalCapacity, localName, visitedValueNames),
            SsaDynamicStorageTryReserveCapacityRValue reserve => ValueTransitivelyUsesLocalStorage(reserve.StorageAddress, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(reserve.TargetCapacity, localName, visitedValueNames),
            SsaDynamicStorageMoveLastRValue moveLast => ValueTransitivelyUsesLocalStorage(moveLast.StorageAddress, localName, visitedValueNames),
            SsaDynamicStorageMoveAtRValue moveAt => ValueTransitivelyUsesLocalStorage(moveAt.StorageAddress, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(moveAt.Index, localName, visitedValueNames),
            SsaLoadSliceElementRValue loadSlice => ValueTransitivelyUsesLocalStorage(loadSlice.Slice, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(loadSlice.Index, localName, visitedValueNames),
            SsaTextSliceRValue textSlice => ValueTransitivelyUsesLocalStorage(textSlice.TextValue, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(textSlice.Start, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(textSlice.Length, localName, visitedValueNames),
            SsaFieldAddressRValue fieldAddress => ValueTransitivelyUsesLocalStorage(fieldAddress.Address, localName, visitedValueNames),
            SsaElementAddressRValue elementAddress => ValueTransitivelyUsesLocalStorage(elementAddress.Address, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(elementAddress.Index, localName, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => ValueTransitivelyUsesLocalStorage(sliceElementAddress.Slice, localName, visitedValueNames)
                || ValueTransitivelyUsesLocalStorage(sliceElementAddress.Index, localName, visitedValueNames),
            SsaLoadIndirectRValue loadIndirect => ValueTransitivelyUsesLocalStorage(loadIndirect.Address, localName, visitedValueNames),
            _ => false
        };
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
            case SsaTextDataAddressValue textData:
                alignmentBytes = ResolveStringConstant(textData.LiteralText, textData.TextType).AlignmentBytes;
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
            case SsaExtractFieldRValue { FieldIndex: 0, Target.Type.Kind: StarkTypeKind.Dynamic } extractField
                when string.Equals(extractField.FieldName, "Data", StringComparison.Ordinal)
                     && extractField.Target.Type.ElementType is { } dynamicElementType:
            {
                alignmentBytes = GetTypeAlignmentBytes(dynamicElementType) ?? 1;
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
                if (!visitedValueNames.Add(reference.Name))
                {
                    return false;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition)
                    && TryGetKnownSliceDataAlignmentBytes(definition, visitedValueNames, out alignmentBytes))
                {
                    return true;
                }

                if (_valueFacts.TryGetValue(reference.Name, out var facts)
                    && facts.BoundedRawPointerRegionKind == SsaFactLatticeKind.Known
                    && facts.BoundedRawPointerRegion is { ElementAlignmentBytes: > 1 } boundedRegion)
                {
                    alignmentBytes = boundedRegion.ElementAlignmentBytes.Value;
                    return true;
                }

                return false;
            case SsaUseRValue use:
                return TryGetKnownSliceDataAlignmentBytes(use.Value, visitedValueNames, out alignmentBytes);
            case SsaConvertRValue convert when IsNoOpConversion(convert.Operand.Type, convert.TargetType):
                return TryGetKnownSliceDataAlignmentBytes(convert.Operand, visitedValueNames, out alignmentBytes);
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
}
