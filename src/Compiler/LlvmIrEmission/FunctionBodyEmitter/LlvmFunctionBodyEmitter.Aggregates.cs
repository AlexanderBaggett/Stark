using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitStoreLocal(SsaStoreLocalInstruction storeLocal)
    {
        if (storeLocal.Value is SsaUndefValue && ShouldSuppressMovedFromUndefStore(storeLocal))
        {
            return;
        }

        if (StarkTypeSymbols.IsPointerBackedBorrowType(storeLocal.LocalType))
        {
            EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
            var borrowSlot = GetLocalSlotPointer(storeLocal.LocalName);
            EmitValueToAddress(
                borrowSlot,
                storeLocal.LocalType,
                storeLocal.Value,
                GetLocalSlotAlignmentBytes(storeLocal.LocalName, storeLocal.LocalType),
                GetDirectTbaaMetadataSuffix(CreateTbaaLocalRootKey(storeLocal.LocalName), storeLocal.LocalType));
            EmitInvariantStartForLocalIfNeeded(storeLocal.LocalName, storeLocal.LocalType);
            TrackFreshDynamicLocalStorageAfterStore(storeLocal);
            return;
        }

        if (TryEmitDirectAggregateAliasStoreLocal(storeLocal))
        {
            TrackFreshDynamicLocalStorageAfterStore(storeLocal);
            return;
        }

        EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
        var slot = GetLocalSlotPointer(storeLocal.LocalName);
        EmitValueToAddress(
            slot,
            storeLocal.LocalType,
            storeLocal.Value,
            GetLocalSlotAlignmentBytes(storeLocal.LocalName, storeLocal.LocalType),
            GetDirectTbaaMetadataSuffix(CreateTbaaLocalRootKey(storeLocal.LocalName), storeLocal.LocalType));
        EmitInvariantStartForLocalIfNeeded(storeLocal.LocalName, storeLocal.LocalType);
        TrackFreshDynamicLocalStorageAfterStore(storeLocal);
    }

    private bool TryEmitDirectAggregateAliasStoreLocal(SsaStoreLocalInstruction storeLocal)
    {
        if (!IsDirectAggregateAliasCandidateLocal(storeLocal.LocalName)
            || !CanAliasLocalToFreshIndirectAggregateSource(storeLocal.Value, storeLocal.LocalType)
            || !TryResolveAggregateSourceAddress(
                storeLocal.Value,
                storeLocal.LocalType,
                out var sourceAddress,
                out var sourceAlignmentBytes))
        {
            return false;
        }

        _localSlotAliases[storeLocal.LocalName] = new LocalSlotAlias(
            sourceAddress,
            sourceAlignmentBytes ?? GetLocalObjectAlignmentBytes(storeLocal.LocalName, storeLocal.LocalType),
            storeLocal.LocalType);

        if (_deferredAliasLocalAllocations.Remove(storeLocal.LocalName, out var allocateLocal))
        {
            EmitLocalDebugDeclare(
                sourceAddress,
                allocateLocal.LocalName,
                allocateLocal.LocalType,
                allocateLocal.Location);
        }

        _deferredAliasLifetimeStarts.Remove(storeLocal.LocalName);

        EmitInvariantStartForLocalIfNeeded(storeLocal.LocalName, storeLocal.LocalType);
        return true;
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
            EmitAggregateMemcpy(
                FormatValue(copyMemory.DestinationAddress),
                FormatValue(copyMemory.SourceAddress),
                layout.SizeBytes,
                GetKnownPointerArgumentAlignmentFragment(copyMemory.DestinationAddress, copyMemory.CopyType),
                GetKnownPointerArgumentAlignmentFragment(copyMemory.SourceAddress, copyMemory.CopyType));
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
        var alignmentBytes = GetKnownPointerAlignmentBytes(storeIndirect.Address, storeIndirect.ValueType);
        EmitValueToAddress(
            FormatValue(storeIndirect.Address),
            storeIndirect.ValueType,
            storeIndirect.Value,
            alignmentBytes,
            GetTbaaMetadataSuffix(storeIndirect.Address, storeIndirect.ValueType),
            GetScopedNoAliasMetadataSuffix(storeIndirect.Address, storeIndirect.ScopedNoAliasGroups)
                + GetLoopAccessGroupMetadataSuffix(storeIndirect.LoopAccessGroups),
            ShouldEmitExplicitLowAlignment(storeIndirect.Address, storeIndirect.ValueType, alignmentBytes));
    }

    private void EmitStoreGlobal(SsaStoreGlobalInstruction storeGlobal)
    {
        EmitValueToAddress(
            $"@{EscapeIdentifier(ResolveGlobalSymbolName(storeGlobal.GlobalName))}",
            storeGlobal.GlobalType,
            storeGlobal.Value,
            GetGlobalObjectAlignmentBytes(storeGlobal.GlobalName, storeGlobal.GlobalType),
            GetDirectTbaaMetadataSuffix(CreateTbaaGlobalRootKey(storeGlobal.GlobalName), storeGlobal.GlobalType));
    }

    private void EmitValueToAddress(SsaValue destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        var alignmentBytes = GetKnownPointerAlignmentBytes(destinationAddress, valueType);
        EmitValueToAddress(
            FormatValue(destinationAddress),
            valueType,
            value,
            alignmentBytes,
            GetTbaaMetadataSuffix(destinationAddress, valueType),
            GetScopedNoAliasMetadataSuffix(destinationAddress),
            ShouldEmitExplicitLowAlignment(destinationAddress, valueType, alignmentBytes));
    }

    private void EmitValueToAddress(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        int? alignmentBytes,
        string tbaaMetadataSuffix = "",
        string scopedNoAliasMetadataSuffix = "",
        bool includeByteAlignment = false)
    {
        if (TryEmitPointerBackedBorrowStore(destinationAddress, valueType, value, alignmentBytes, tbaaMetadataSuffix, scopedNoAliasMetadataSuffix))
        {
            return;
        }

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

        AppendLine($"  store {MapType(valueType)} {FormatValue(value)}, ptr {destinationAddress}{GetAlignmentSuffix(alignmentBytes, includeByteAlignment)}{tbaaMetadataSuffix}{scopedNoAliasMetadataSuffix}");
    }

    private bool TryEmitPointerBackedBorrowStore(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        int? alignmentBytes,
        string tbaaMetadataSuffix,
        string scopedNoAliasMetadataSuffix)
    {
        if (!StarkTypeSymbols.IsPointerBackedBorrowType(valueType))
        {
            return false;
        }

        var pointerValue = value switch
        {
            SsaNullConstant or SsaZeroInitializerValue => "null",
            _ => TryResolveAggregateSourceAddress(value, valueType, out var sourceAddress, out _)
                ? sourceAddress
                : FormatValue(value)
        };
        AppendLine($"  store ptr {pointerValue}, ptr {destinationAddress}{GetAlignmentSuffix(alignmentBytes)}{tbaaMetadataSuffix}{scopedNoAliasMetadataSuffix}");
        return true;
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
        if (!TryResolveAggregateSourceAddress(value, valueType, out var sourceAddress, out var sourceAlignmentBytes))
        {
            return false;
        }

        if (string.Equals(destinationAddress, sourceAddress, StringComparison.Ordinal))
        {
            return true;
        }

        EmitAggregateAddressCopy(
            destinationAddress,
            sourceAddress,
            valueType,
            destinationAlignmentBytes,
            sourceAlignmentBytes,
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
            EmitAggregateMemcpy(
                destinationAddress,
                sourceAddress,
                layout.SizeBytes,
                GetArgumentAlignmentFragment(destinationAlignmentBytes),
                GetArgumentAlignmentFragment(sourceAlignmentBytes));
            return;
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
        AppendLine($"  {loadedValue} = load {MapType(copyType)}, ptr {sourceAddress}{GetAlignmentSuffix(sourceAlignmentBytes)}{invariantLoadMetadataSuffix}{GetValueRangeMetadataSuffix(copyType)}");
        AppendLine($"  store {MapType(copyType)} {loadedValue}, ptr {destinationAddress}{GetAlignmentSuffix(destinationAlignmentBytes)}");
    }

    private void EmitAggregateMemcpy(
        string destinationAddress,
        string sourceAddress,
        long sizeBytes,
        string destinationAlignmentFragment,
        string sourceAlignmentFragment)
    {
        var intrinsic = sizeBytes <= AggregateInlineMemcpyThresholdBytes
            ? "llvm.memcpy.inline.p0.p0.i64"
            : "llvm.memcpy.p0.p0.i64";
        AppendLine(
            $"  call void @{intrinsic}(ptr{destinationAlignmentFragment} {destinationAddress}, ptr{sourceAlignmentFragment} {sourceAddress}, i64 {sizeBytes}, i1 false)");
    }

    private bool TryResolveAggregateSourceAddress(SsaValue value, StarkTypeSymbol expectedType, out string sourceAddress)
    {
        return TryResolveAggregateSourceAddress(
            value,
            expectedType,
            out sourceAddress,
            out _);
    }

    private bool TryResolveAggregateSourceAddress(
        SsaValue value,
        StarkTypeSymbol expectedType,
        out string sourceAddress,
        out int? sourceAlignmentBytes)
    {
        return TryResolveAggregateSourceAddress(
            value,
            expectedType,
            new HashSet<string>(StringComparer.Ordinal),
            out sourceAddress,
            out sourceAlignmentBytes);
    }

    private bool TryResolveAggregateSourceAddress(
        SsaValue value,
        StarkTypeSymbol expectedType,
        ISet<string> visitedValueNames,
        out string sourceAddress,
        out int? sourceAlignmentBytes)
    {
        var normalizedExpectedType = NormalizeAggregateType(expectedType);

        if (StarkTypeSymbols.IsPointerBackedBorrowType(expectedType)
            && TryResolvePointerBackedBorrowSourceAddress(
                value,
                expectedType,
                visitedValueNames,
                out sourceAddress,
                out sourceAlignmentBytes))
        {
            return true;
        }

        if (StarkTypeSymbols.IsPointerBackedBorrowType(value.Type)
            && NormalizeAggregateType(StarkTypeSymbols.BorrowReturnValueType(value.Type)) == normalizedExpectedType
            && TryResolvePointerBackedBorrowSourceAddress(
                value,
                value.Type,
                visitedValueNames,
                out sourceAddress,
                out sourceAlignmentBytes))
        {
            return true;
        }

        switch (value)
        {
            case SsaValueReference reference:
                if (_indirectAggregateValueSlots.TryGetValue(reference.Name, out var indirectSlot))
                {
                    sourceAddress = indirectSlot;
                    sourceAlignmentBytes = GetTypeAlignmentBytes(expectedType);
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
                    sourceAlignmentBytes = GetTypeAlignmentBytes(indirectParameter.SourceType);
                    return true;
                }

                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    sourceAddress = string.Empty;
                    sourceAlignmentBytes = null;
                    return false;
                }

                switch (definition)
                {
                    case SsaUseRValue use when NormalizeAggregateType(use.Type) == normalizedExpectedType:
                        return TryResolveAggregateSourceAddress(
                            use.Value,
                            expectedType,
                            visitedValueNames,
                            out sourceAddress,
                            out sourceAlignmentBytes);
                    case SsaLoadLocalRValue loadLocal when NormalizeAggregateType(loadLocal.Type) == normalizedExpectedType:
                        if (IsLocalLifetimeEndedBeforeCurrentInstruction(loadLocal.LocalName)
                            || !CanForwardLocalLoadStorageAtCurrentInstruction(reference.Name, loadLocal.LocalName))
                        {
                            sourceAddress = string.Empty;
                            sourceAlignmentBytes = null;
                            return false;
                        }

                        EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                        sourceAddress = GetLocalSlotPointer(loadLocal.LocalName);
                        sourceAlignmentBytes = GetLocalSlotAlignmentBytes(loadLocal.LocalName, loadLocal.Type)
                            ?? GetTypeAlignmentBytes(loadLocal.Type);
                        return true;
                    case SsaLoadGlobalRValue loadGlobal
                        when NormalizeAggregateType(loadGlobal.Type) == normalizedExpectedType
                             && IsImmutableGlobalName(loadGlobal.GlobalName):
                        sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(loadGlobal.GlobalName))}";
                        sourceAlignmentBytes = GetGlobalObjectAlignmentBytes(loadGlobal.GlobalName, loadGlobal.Type);
                        return true;
                    case SsaExtractFieldRValue extractField
                        when NormalizeAggregateType(extractField.Type) == normalizedExpectedType
                             && GetAggregateElementType(extractField.Target.Type, extractField.FieldIndex) is { } fieldType
                             && NormalizeAggregateType(fieldType) == normalizedExpectedType
                             && TryResolveAggregateSourceAddress(
                                 extractField.Target,
                                 extractField.Target.Type,
                                 visitedValueNames,
                                 out var aggregateSourceAddress,
                                 out var aggregateAlignmentBytes):
                        sourceAddress = EmitScalarizedAggregateLeafAddress(
                            aggregateSourceAddress,
                            extractField.Target.Type,
                            [extractField.FieldIndex],
                            "aggregate_source_field");
                        sourceAlignmentBytes = GetLeafAlignmentBytes(aggregateAlignmentBytes, fieldType)
                            ?? GetTypeAlignmentBytes(fieldType);
                        return true;
                    case SsaExtractIndexRValue extractIndex
                        when NormalizeAggregateType(extractIndex.Type) == normalizedExpectedType
                             && GetAggregateElementType(extractIndex.Target.Type, extractIndex.ElementIndex) is { } elementType
                             && NormalizeAggregateType(elementType) == normalizedExpectedType
                             && TryResolveAggregateSourceAddress(
                                 extractIndex.Target,
                                 extractIndex.Target.Type,
                                 visitedValueNames,
                                 out var aggregateSourceAddress,
                                 out var aggregateAlignmentBytes):
                        sourceAddress = EmitScalarizedAggregateLeafAddress(
                            aggregateSourceAddress,
                            extractIndex.Target.Type,
                            [extractIndex.ElementIndex],
                            "aggregate_source_index");
                        sourceAlignmentBytes = GetLeafAlignmentBytes(aggregateAlignmentBytes, elementType)
                            ?? GetTypeAlignmentBytes(elementType);
                        return true;
                    default:
                        sourceAddress = string.Empty;
                        sourceAlignmentBytes = null;
                        return false;
                }
            case SsaGlobalAddressValue globalAddress when NormalizeAggregateType(globalAddress.PointeeType) == normalizedExpectedType:
                sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}";
                sourceAlignmentBytes = GetGlobalObjectAlignmentBytes(globalAddress.GlobalName, globalAddress.PointeeType);
                return true;
            default:
                sourceAddress = string.Empty;
                sourceAlignmentBytes = null;
                return false;
        }
    }

    private bool TryResolvePointerBackedBorrowSourceAddress(
        SsaValue value,
        StarkTypeSymbol borrowType,
        ISet<string> visitedValueNames,
        out string sourceAddress,
        out int? sourceAlignmentBytes)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name))
        {
            sourceAddress = string.Empty;
            sourceAlignmentBytes = null;
            return false;
        }

        var borrowedValueType = StarkTypeSymbols.BorrowReturnValueType(borrowType);
        if (TryResolvePointerBackedBorrowParameterValue(reference, borrowedValueType, out sourceAddress, out sourceAlignmentBytes))
        {
            return true;
        }

        if (_trivialValueAliases.TryGetValue(reference.Name, out var alias))
        {
            return TryResolvePointerBackedBorrowSourceAddress(
                alias,
                borrowType,
                visitedValueNames,
                out sourceAddress,
                out sourceAlignmentBytes);
        }

        if (StarkTypeSymbols.IsPointerBackedBorrowType(reference.Type)
            && NormalizeAggregateType(StarkTypeSymbols.BorrowReturnValueType(reference.Type)) == NormalizeAggregateType(borrowedValueType))
        {
            sourceAddress = FormatValue(reference);
            sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
            return true;
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            sourceAddress = string.Empty;
            sourceAlignmentBytes = null;
            return false;
        }

        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolvePointerBackedBorrowSourceAddress(
                    use.Value,
                    borrowType,
                    visitedValueNames,
                    out sourceAddress,
                    out sourceAlignmentBytes);
            case SsaConvertRValue convert
                when StarkTypeSymbols.IsPointerBackedBorrowType(convert.TargetType)
                     && convert.Operand.Type.Kind == StarkTypeKind.RawPointer:
                sourceAddress = FormatValue(convert.Operand);
                sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
                return true;
            case SsaLoadIndirectRValue loadIndirect
                when StarkTypeSymbols.IsPointerBackedBorrowType(loadIndirect.Type):
                if (TryResolveDirectPointerBackedBorrowPointeeAddress(
                        loadIndirect.Address,
                        borrowType,
                        out sourceAddress,
                        out sourceAlignmentBytes,
                        out _))
                {
                    return true;
                }

                if (IsAddressOfPointerBackedBorrowParameter(loadIndirect.Address))
                {
                    sourceAddress = FormatValue(loadIndirect.Address);
                    sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
                    return true;
                }

                sourceAddress = EmitPointerBackedBorrowSlotLoad(
                    FormatValue(loadIndirect.Address),
                    GetKnownPointerAlignmentBytes(loadIndirect.Address, loadIndirect.Type));
                sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
                return true;
            case SsaLoadLocalRValue loadLocal
                when StarkTypeSymbols.IsPointerBackedBorrowType(loadLocal.Type):
                if (TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue)
                    && StarkTypeSymbols.IsPointerBackedBorrowType(storedValue.Type)
                    && TryResolvePointerBackedBorrowSourceAddress(
                        storedValue,
                        borrowType,
                        visitedValueNames,
                        out sourceAddress,
                        out sourceAlignmentBytes))
                {
                    return true;
                }

                EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                sourceAddress = EmitPointerBackedBorrowSlotLoad(
                    GetLocalSlotPointer(loadLocal.LocalName),
                    GetLocalSlotAlignmentBytes(loadLocal.LocalName, loadLocal.Type));
                sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
                return true;
            case SsaExtractFieldRValue extractField
                when StarkTypeSymbols.IsPointerBackedBorrowType(extractField.Type)
                     && TryResolveAggregateSourceAddress(
                         extractField.Target,
                         extractField.Target.Type,
                         visitedValueNames,
                         out var aggregateSourceAddress,
                         out var aggregateAlignmentBytes):
                var fieldType = GetAggregateElementType(extractField.Target.Type, extractField.FieldIndex);
                var slotAddress = EmitScalarizedAggregateLeafAddress(
                    aggregateSourceAddress,
                    extractField.Target.Type,
                    [extractField.FieldIndex],
                    "borrow_source_field");
                sourceAddress = EmitPointerBackedBorrowSlotLoad(
                    slotAddress,
                    GetLeafAlignmentBytes(aggregateAlignmentBytes, fieldType ?? extractField.Type));
                sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
                return true;
            default:
                sourceAddress = string.Empty;
                sourceAlignmentBytes = null;
                return false;
        }
    }

    private bool TryResolveDirectPointerBackedBorrowPointeeAddress(
        SsaValue address,
        StarkTypeSymbol borrowType,
        out string sourceAddress,
        out int? sourceAlignmentBytes,
        out StarkTypeSymbol pointeeType)
    {
        sourceAddress = string.Empty;
        sourceAlignmentBytes = null;
        pointeeType = StarkTypeSymbols.Error;

        if (!TryGetDirectPointerBackedBorrowPointeeType(address.Type, borrowType, out pointeeType))
        {
            return false;
        }

        sourceAddress = FormatValue(address);
        sourceAlignmentBytes = GetKnownPointerAlignmentBytes(address, pointeeType)
            ?? GetTypeAlignmentBytes(pointeeType);
        return true;
    }

    private static bool TryGetDirectPointerBackedBorrowPointeeType(
        StarkTypeSymbol addressType,
        StarkTypeSymbol borrowType,
        out StarkTypeSymbol pointeeType)
    {
        pointeeType = StarkTypeSymbols.Error;
        if (!StarkTypeSymbols.IsPointerBackedBorrowType(borrowType)
            || addressType.Kind != StarkTypeKind.RawPointer
            || addressType.ElementType is not { } addressPointeeType
            || StarkTypeSymbols.IsPointerBackedBorrowType(addressPointeeType))
        {
            return false;
        }

        var borrowedValueType = StarkTypeSymbols.WithQualifiers(
            StarkTypeSymbols.BorrowReturnValueType(borrowType),
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (NormalizeAggregateType(addressPointeeType) != NormalizeAggregateType(borrowedValueType))
        {
            return false;
        }

        pointeeType = addressPointeeType;
        return true;
    }

    private bool TryResolvePointerBackedBorrowParameterValue(
        SsaValueReference reference,
        StarkTypeSymbol borrowedValueType,
        out string sourceAddress,
        out int? sourceAlignmentBytes)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(parameter =>
            StarkTypeSymbols.IsPointerBackedBorrowType(parameter.SourceType)
            && (string.Equals(parameter.LlvmName, reference.Name, StringComparison.Ordinal)
                || string.Equals(parameter.SourceName, reference.Name, StringComparison.Ordinal)));
        if (parameter is null)
        {
            sourceAddress = string.Empty;
            sourceAlignmentBytes = null;
            return false;
        }

        sourceAddress = $"%{EscapeIdentifier(parameter.LlvmName)}";
        sourceAlignmentBytes = GetTypeAlignmentBytes(borrowedValueType);
        return true;
    }

    private bool IsAddressOfPointerBackedBorrowParameter(SsaValue address)
    {
        if (address is not SsaValueReference reference
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaAddressOfParameterRValue addressOfParameter
            || !StarkTypeSymbols.IsPointerBackedBorrowType(addressOfParameter.PointeeType))
        {
            return false;
        }

        return _abiFunction.UserParameters.Any(parameter =>
            string.Equals(parameter.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal)
            && StarkTypeSymbols.IsPointerBackedBorrowType(parameter.SourceType));
    }

    private string EmitPointerBackedBorrowSlotLoad(string slotAddress, int? slotAlignmentBytes)
    {
        var loadedPointer = $"%{EscapeIdentifier(CreateAbiTempName("borrow_ptr_load"))}";
        AppendLine($"  {loadedPointer} = load ptr, ptr {slotAddress}{GetAlignmentSuffix(slotAlignmentBytes)}");
        return loadedPointer;
    }

    private bool TryEmitPointerBackedBorrowExtractFieldLoad(string result, SsaExtractFieldRValue extract)
    {
        if (!StarkTypeSymbols.IsPointerBackedBorrowType(extract.Target.Type))
        {
            return false;
        }

        var aggregateType = StarkTypeSymbols.BorrowReturnValueType(extract.Target.Type);
        var fieldAddress = EmitScalarizedAggregateLeafAddress(
            FormatValue(extract.Target),
            aggregateType,
            [extract.FieldIndex],
            "borrow_extract_field");
        AppendLine(
            $"  {result} = load {MapType(extract.Type)}, ptr {fieldAddress}{GetAlignmentSuffix(GetTypeAlignmentBytes(extract.Type))}{GetValueRangeMetadataSuffix(extract.Type)}");
        return true;
    }

    private bool TryEmitPointerBackedBorrowExtractIndexLoad(string result, SsaExtractIndexRValue extract)
    {
        if (!StarkTypeSymbols.IsPointerBackedBorrowType(extract.Target.Type))
        {
            return false;
        }

        var aggregateType = StarkTypeSymbols.BorrowReturnValueType(extract.Target.Type);
        var elementAddress = EmitScalarizedAggregateLeafAddress(
            FormatValue(extract.Target),
            aggregateType,
            [extract.ElementIndex],
            "borrow_extract_index");
        AppendLine(
            $"  {result} = load {MapType(extract.Type)}, ptr {elementAddress}{GetAlignmentSuffix(GetTypeAlignmentBytes(extract.Type))}{GetValueRangeMetadataSuffix(extract.Type)}");
        return true;
    }

    private bool IsLocalLifetimeEndedBeforeCurrentInstruction(string localName)
    {
        if (_currentBlock is null || _currentInstructionIndex <= 0)
        {
            return false;
        }

        var isEnded = false;
        for (var index = 0; index < _currentInstructionIndex && index < _currentBlock.Instructions.Count; index++)
        {
            switch (_currentBlock.Instructions[index])
            {
                case SsaLifetimeStartInstruction lifetimeStart
                    when string.Equals(lifetimeStart.LocalName, localName, StringComparison.Ordinal):
                    isEnded = false;
                    break;
                case SsaLifetimeEndInstruction lifetimeEnd
                    when string.Equals(lifetimeEnd.LocalName, localName, StringComparison.Ordinal)
                         && !ShouldSuppressLifetimeEnd(lifetimeEnd, index):
                    isEnded = true;
                    break;
            }
        }

        return isEnded;
    }

    private bool CanForwardLocalLoadStorageAtCurrentInstruction(string valueName, string localName)
    {
        if (_currentBlock is null
            || !_valueDefinitionPositions.TryGetValue(valueName, out var definitionPosition)
            || definitionPosition.BlockId != _currentBlock.Id
            || definitionPosition.InstructionIndex >= _currentInstructionIndex)
        {
            return false;
        }

        for (var index = definitionPosition.InstructionIndex + 1;
             index < _currentInstructionIndex && index < _currentBlock.Instructions.Count;
             index++)
        {
            if (InstructionMayOverwriteLocalStorage(_currentBlock.Instructions[index], localName, index))
            {
                return false;
            }
        }

        return true;
    }

    private bool ShouldSuppressMovedFromUndefStore(SsaStoreLocalInstruction storeLocal)
    {
        return ShouldSuppressMovedFromUndefStore(storeLocal, _currentInstructionIndex);
    }

    private bool ShouldSuppressMovedFromUndefStore(SsaStoreLocalInstruction storeLocal, int storeInstructionIndex)
    {
        if (_currentBlock is null || storeInstructionIndex < 0)
        {
            return false;
        }

        for (var index = storeInstructionIndex + 1; index < _currentBlock.Instructions.Count; index++)
        {
            var instruction = _currentBlock.Instructions[index];
            switch (instruction)
            {
                case SsaAllocateLocalInstruction allocateLocal
                    when string.Equals(allocateLocal.LocalName, storeLocal.LocalName, StringComparison.Ordinal):
                case SsaLifetimeStartInstruction lifetimeStart
                    when string.Equals(lifetimeStart.LocalName, storeLocal.LocalName, StringComparison.Ordinal):
                case SsaStoreLocalInstruction nextStore
                    when string.Equals(nextStore.LocalName, storeLocal.LocalName, StringComparison.Ordinal):
                    return false;
            }

            if (InstructionTransitivelyUsesLocalStorage(instruction, storeLocal.LocalName))
            {
                return true;
            }
        }

        return TerminatorTransitivelyUsesLocalStorage(_currentBlock.Terminator, storeLocal.LocalName);
    }

    private bool InstructionMayOverwriteLocalStorage(SsaInstruction instruction, string localName, int instructionIndex)
    {
        switch (instruction)
        {
            case SsaStoreLocalInstruction storeLocal
                when string.Equals(storeLocal.LocalName, localName, StringComparison.Ordinal):
                return storeLocal.Value is not SsaUndefValue
                    || !ShouldSuppressMovedFromUndefStore(storeLocal, instructionIndex);
            case SsaAllocateLocalInstruction allocateLocal
                when string.Equals(allocateLocal.LocalName, localName, StringComparison.Ordinal):
            case SsaLifetimeEndInstruction lifetimeEnd
                when string.Equals(lifetimeEnd.LocalName, localName, StringComparison.Ordinal)
                     && !ShouldSuppressLifetimeEnd(lifetimeEnd, instructionIndex):
            case SsaDeallocateLocalInstruction deallocateLocal
                when string.Equals(deallocateLocal.LocalName, localName, StringComparison.Ordinal):
                return true;
            case SsaCopyMemoryInstruction copyMemory:
                return TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var copyDestinationLocal)
                    && string.Equals(copyDestinationLocal, localName, StringComparison.Ordinal);
            case SsaStoreIndirectInstruction storeIndirect:
                return TryResolveLocalAddressRoot(storeIndirect.Address, out var indirectDestinationLocal)
                    && string.Equals(indirectDestinationLocal, localName, StringComparison.Ordinal);
            case SsaValueInstruction { Value: SsaCallRValue call }:
                return CallMayOverwriteLocalStorage(call, localName);
            case SsaCallInstruction call:
                return CallMayOverwriteLocalStorage(call, localName);
            case SsaValueInstruction valueInstruction:
                return RValueMayOverwriteLocalStorage(valueInstruction.Value, localName);
            default:
                return false;
        }
    }

    private bool CallMayOverwriteLocalStorage(ISsaDirectCallOperation call, string localName)
    {
        foreach (var argument in call.Arguments)
        {
            if (ValueMayReferenceLocalStorage(argument, localName))
            {
                return true;
            }
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            if (ValueMayReferenceLocalStorage(address, localName))
            {
                return true;
            }
        }

        return false;
    }

    private bool RValueMayOverwriteLocalStorage(SsaRValue value, string localName)
    {
        return value switch
        {
            SsaDynamicStorageFreeRValue free => ValueMayReferenceLocalStorage(free.Storage, localName),
            SsaHeapStorageFreeRValue free => ValueMayReferenceLocalStorage(free.Pointer, localName),
            SsaDynamicStorageReserveRValue reserve => ValueMayReferenceLocalStorage(reserve.StorageAddress, localName),
            SsaDynamicStorageTryReserveRValue reserve => ValueMayReferenceLocalStorage(reserve.StorageAddress, localName),
            SsaDynamicStorageTryReserveCapacityRValue reserve => ValueMayReferenceLocalStorage(reserve.StorageAddress, localName),
            SsaDynamicStorageMoveLastRValue moveLast => ValueMayReferenceLocalStorage(moveLast.StorageAddress, localName),
            SsaDynamicStorageMoveAtRValue moveAt => ValueMayReferenceLocalStorage(moveAt.StorageAddress, localName),
            _ => false
        };
    }

    private bool ValueMayReferenceLocalStorage(SsaValue value, string localName)
    {
        return TryResolveLocalAddressRoot(value, out var referencedLocal)
            && string.Equals(referencedLocal, localName, StringComparison.Ordinal);
    }

    private bool CanDeferAddressForwardedAggregateValueInstruction(SsaValueInstruction instruction)
    {
        return CanDeferAddressForwardedAggregateValueInstruction(instruction.ResultName, instruction.Value);
    }

    private bool CanDeferAddressForwardedAggregateValueInstruction(string resultName, SsaRValue value)
    {
        if (!ShouldPreferAddressBasedAggregateLowering(value.Type)
            || TryGetConcreteTypeLayout(NormalizeAggregateType(value.Type)) is not { SizeBytes: > AggregateMemcpyThresholdBytes }
            || RequiresAggregateValueMaterialization(resultName, value.Type))
        {
            return false;
        }

        return value switch
        {
            SsaUseRValue => true,
            SsaExtractFieldRValue extractField => IsFreshIndirectAggregateValueReference(extractField.Target),
            SsaExtractIndexRValue extractIndex => IsFreshIndirectAggregateValueReference(extractIndex.Target),
            SsaInsertFieldRValue => true,
            SsaInsertIndexRValue => true,
            _ => false
        };
    }

    private string FormatAggregateValueUse(SsaValue value, StarkTypeSymbol valueType, string purpose)
    {
        if (!ShouldPreferAddressBasedAggregateLowering(valueType)
            || ValueHasDirectLlvmRepresentation(value, new HashSet<string>(StringComparer.Ordinal)))
        {
            return FormatValue(value);
        }

        if (!TryResolveAggregateSourceAddress(value, valueType, out var sourceAddress, out var sourceAlignmentBytes))
        {
            return FormatValue(value);
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        AppendLine($"  {loadedValue} = load {MapType(valueType)}, ptr {sourceAddress}{GetAlignmentSuffix(sourceAlignmentBytes)}{GetValueRangeMetadataSuffix(valueType)}");
        return loadedValue;
    }

    private bool ValueHasDirectLlvmRepresentation(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference)
        {
            return true;
        }

        if (_materializedParameters.ContainsKey(reference.Name)
            || _phisByResultName.ContainsKey(reference.Name))
        {
            return true;
        }

        if (IsUnmaterializedIndirectParameterReference(reference))
        {
            return false;
        }

        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (_trivialValueAliases.TryGetValue(reference.Name, out var alias))
        {
            return ValueHasDirectLlvmRepresentation(alias, visitedValueNames);
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return true;
        }

        if (definition is SsaUseRValue use)
        {
            return ValueHasDirectLlvmRepresentation(use.Value, visitedValueNames);
        }

        if (definition is SsaConvertRValue convert && IsNoOpConversion(convert.Operand.Type, convert.TargetType))
        {
            return ValueHasDirectLlvmRepresentation(convert.Operand, visitedValueNames);
        }

        if (definition is SsaCallRValue call
            && _resolveCallAbi(_function.Name, call.FunctionName)?.ReturnsIndirect == true)
        {
            return RequiresAggregateValueMaterialization(reference.Name, call.SourceReturnType ?? call.Type);
        }

        if (definition is SsaIndirectCallRValue indirectCall
            && BuildIndirectCallAbi(indirectCall).ReturnsIndirect)
        {
            return RequiresAggregateValueMaterialization(reference.Name, indirectCall.SourceReturnType ?? indirectCall.Type);
        }

        return !CanDeferAddressForwardedAggregateValueInstruction(reference.Name, definition);
    }

    private bool IsUnmaterializedIndirectParameterReference(SsaValueReference reference)
    {
        return !_materializedParameters.ContainsKey(reference.Name)
            && _abiFunction.UserParameters.Any(parameter =>
                parameter.Kind == AbiParameterKind.IndirectIn
                && NormalizeAggregateType(parameter.SourceType) == NormalizeAggregateType(reference.Type)
                && (string.Equals(parameter.LlvmName, reference.Name, StringComparison.Ordinal)
                    || string.Equals(parameter.SourceName, reference.Name, StringComparison.Ordinal)));
    }

    private bool CanAliasLocalToFreshIndirectAggregateSource(SsaValue value, StarkTypeSymbol localType)
    {
        if (!ShouldPreferAddressBasedAggregateLowering(localType)
            || TryGetConcreteTypeLayout(NormalizeAggregateType(localType)) is not { SizeBytes: > AggregateMemcpyThresholdBytes }
            || NormalizeAggregateType(value.Type) != NormalizeAggregateType(localType))
        {
            return false;
        }

        return IsFreshIndirectAggregatePayloadProjection(
            value,
            NormalizeAggregateType(localType),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private bool IsFreshIndirectAggregatePayloadProjection(
        SsaValue value,
        StarkTypeSymbol expectedType,
        ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name)
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use when NormalizeAggregateType(use.Type) == expectedType =>
                IsFreshIndirectAggregatePayloadProjection(use.Value, expectedType, visitedValueNames),
            SsaExtractFieldRValue extractField
                when NormalizeAggregateType(extractField.Type) == expectedType
                     && GetAggregateElementType(extractField.Target.Type, extractField.FieldIndex) is { } fieldType
                     && NormalizeAggregateType(fieldType) == expectedType =>
                IsFreshIndirectAggregateRootReference(extractField.Target, new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            SsaExtractIndexRValue extractIndex
                when NormalizeAggregateType(extractIndex.Type) == expectedType
                     && GetAggregateElementType(extractIndex.Target.Type, extractIndex.ElementIndex) is { } elementType
                     && NormalizeAggregateType(elementType) == expectedType =>
                IsFreshIndirectAggregateRootReference(extractIndex.Target, new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            _ => false
        };
    }

    private bool IsFreshIndirectAggregateRootReference(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (_indirectAggregateValueSlots.ContainsKey(reference.Name))
        {
            return true;
        }

        if (!_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => IsFreshIndirectAggregateRootReference(use.Value, visitedValueNames),
            SsaCallRValue call => _resolveCallAbi(_function.Name, call.FunctionName)?.ReturnsIndirect == true,
            _ => false
        };
    }

    private bool IsFreshIndirectAggregateValueReference(SsaValue value)
    {
        return IsFreshIndirectAggregateValueReference(
            value,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private bool IsFreshIndirectAggregateValueReference(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference
            || !visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (_indirectAggregateValueSlots.ContainsKey(reference.Name))
        {
            return true;
        }

        return _valueDefinitions.TryGetValue(reference.Name, out var definition)
            && definition is SsaUseRValue use
            && IsFreshIndirectAggregateValueReference(use.Value, visitedValueNames);
    }

    private bool TryEmitAggregateElementLoad(
        string result,
        SsaValue target,
        int elementIndex,
        StarkTypeSymbol elementType,
        string purpose)
    {
        if (!CanExtractAggregateElementFromAddress(target.Type, elementIndex, elementType))
        {
            return false;
        }

        if (!TryResolveAggregateSourceAddress(target, target.Type, out var sourceAddress))
        {
            if (!StarkTypeSymbols.IsPointerBackedBorrowType(target.Type))
            {
                return false;
            }

            sourceAddress = FormatValue(target);
        }

        var elementAddress = EmitScalarizedAggregateLeafAddress(sourceAddress, target.Type, [elementIndex], purpose);
        var alignmentBytes = GetLeafAlignmentBytes(GetTypeAlignmentBytes(target.Type), elementType);
        AppendLine($"  {result} = load {MapType(elementType)}, ptr {elementAddress}{GetAlignmentSuffix(alignmentBytes)}{GetValueRangeMetadataSuffix(elementType)}");
        return true;
    }

    private bool TryEmitLayoutControlledExtractField(string result, SsaExtractFieldRValue extract)
    {
        if (!TryResolveLayoutControlledField(
                extract.Target.Type,
                extract.FieldIndex,
                out var namedType,
                out var layout,
                out _,
                out var fieldLayout))
        {
            return false;
        }

        if (LlvmLayoutControlledAggregateFacts.TryGetStorageElementIndex(
                namedType,
                layout,
                extract.FieldIndex,
                out var storageElementIndex))
        {
            AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {storageElementIndex}");
            return true;
        }

        var slotName = $"%{EscapeIdentifier(CreateAbiTempName("layout_extract_slot"))}";
        QueueStaticAlloca(slotName, extract.Target.Type);
        var aggregateValue = FormatAggregateValueUse(extract.Target, extract.Target.Type, "layout_extract_target");
        AppendLine($"  store {MapType(extract.Target.Type)} {aggregateValue}, ptr {slotName}{GetStackObjectAlignmentSuffix(extract.Target.Type)}");
        var fieldAddress = $"%{EscapeIdentifier(CreateAbiTempName("layout_extract_field"))}";
        AppendLine($"  {fieldAddress} = getelementptr{GetProvenInObjectGepFlags()} i8, ptr {slotName}, i64 {fieldLayout.OffsetBytes}");
        var alignmentBytes = Math.Min(
            fieldLayout.NaturalAlignmentBytes,
            GetAlignmentAtOffset(layout.AlignmentBytes, fieldLayout.OffsetBytes));
        AppendLine($"  {result} = load {MapType(extract.Type)}, ptr {fieldAddress}{GetAlignmentSuffix(alignmentBytes)}{GetValueRangeMetadataSuffix(extract.Type)}");
        return true;
    }

    private bool TryEmitLayoutControlledInsertField(string result, SsaInsertFieldRValue insert)
    {
        if (!TryResolveLayoutControlledField(
                insert.Target.Type,
                insert.FieldIndex,
                out var namedType,
                out var layout,
                out _,
                out var fieldLayout))
        {
            return false;
        }

        if (LlvmLayoutControlledAggregateFacts.TryGetStorageElementIndex(
                namedType,
                layout,
                insert.FieldIndex,
                out var storageElementIndex))
        {
            AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatAggregateValueUse(insert.Target, insert.Target.Type, "insert_field_target")}, {MapType(insert.Value.Type)} {FormatAggregateValueUse(insert.Value, insert.Value.Type, "insert_field_value")}, {storageElementIndex}");
            return true;
        }

        var slotName = $"%{EscapeIdentifier(CreateAbiTempName("layout_insert_slot"))}";
        QueueStaticAlloca(slotName, insert.Target.Type);
        var aggregateValue = FormatAggregateValueUse(insert.Target, insert.Target.Type, "layout_insert_target");
        AppendLine($"  store {MapType(insert.Target.Type)} {aggregateValue}, ptr {slotName}{GetStackObjectAlignmentSuffix(insert.Target.Type)}");
        var fieldAddress = $"%{EscapeIdentifier(CreateAbiTempName("layout_insert_field"))}";
        AppendLine($"  {fieldAddress} = getelementptr{GetProvenInObjectGepFlags()} i8, ptr {slotName}, i64 {fieldLayout.OffsetBytes}");
        var fieldAlignmentBytes = Math.Min(
            fieldLayout.NaturalAlignmentBytes,
            GetAlignmentAtOffset(layout.AlignmentBytes, fieldLayout.OffsetBytes));
        AppendLine($"  store {MapType(insert.Value.Type)} {FormatAggregateValueUse(insert.Value, insert.Value.Type, "insert_field_value")}, ptr {fieldAddress}{GetAlignmentSuffix(fieldAlignmentBytes)}");
        AppendLine($"  {result} = load {MapType(insert.Target.Type)}, ptr {slotName}{GetStackObjectAlignmentSuffix(insert.Target.Type)}{GetValueRangeMetadataSuffix(insert.Target.Type)}");
        return true;
    }

    private bool TryResolveLayoutControlledField(
        StarkTypeSymbol aggregateType,
        int fieldIndex,
        out NamedTypeSymbol namedType,
        out ConcreteTypeLayout layout,
        out FieldSymbol field,
        out ConcreteFieldLayout fieldLayout)
    {
        namedType = null!;
        layout = null!;
        field = null!;
        fieldLayout = null!;

        var normalizedType = NormalizeAggregateType(aggregateType);
        if (normalizedType.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(normalizedType) is not { } resolvedNamedType
            || !LlvmLayoutControlledAggregateFacts.RequiresPhysicalLayout(resolvedNamedType)
            || TryGetConcreteTypeLayout(normalizedType) is not { } resolvedLayout
            || fieldIndex < 0
            || fieldIndex >= resolvedNamedType.OrderedFields.Count)
        {
            return false;
        }

        var resolvedField = resolvedNamedType.OrderedFields[fieldIndex];
        if (!resolvedLayout.TryGetField(resolvedField.Name, out var resolvedFieldLayout))
        {
            return false;
        }

        namedType = resolvedNamedType;
        layout = resolvedLayout;
        field = resolvedField;
        fieldLayout = resolvedFieldLayout;
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
        // Unoptimized SSA keeps aggregate build chains uncollapsed, so following them can
        // otherwise recurse past the thread stack; the caller's generic store path is safe.
        if (visitedValueNames.Count > 256)
        {
            return false;
        }

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
                    case SsaLoadLocalRValue loadLocal when NormalizeAggregateType(loadLocal.Type) == NormalizeAggregateType(valueType):
                    {
                        if (IsLocalLifetimeEndedBeforeCurrentInstruction(loadLocal.LocalName)
                            || !CanForwardLocalLoadStorageAtCurrentInstruction(reference.Name, loadLocal.LocalName))
                        {
                            return false;
                        }

                        EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                        return TryEmitStructuredAggregateBaseStore(
                            destinationAddress,
                            valueType,
                            GetLocalSlotPointer(loadLocal.LocalName),
                            destinationAlignmentBytes,
                            GetLocalSlotAlignmentBytes(loadLocal.LocalName, loadLocal.Type),
                            GetInvariantLocalLoadMetadataSuffix(loadLocal.LocalName));
                    }
                    case SsaLoadIndirectRValue loadIndirect when NormalizeAggregateType(loadIndirect.Type) == NormalizeAggregateType(valueType):
                        return TryEmitStructuredAggregateBaseStore(
                            destinationAddress,
                            valueType,
                            FormatValue(loadIndirect.Address),
                            destinationAlignmentBytes,
                            GetKnownPointerAlignmentBytes(loadIndirect.Address, loadIndirect.Type),
                            GetInvariantLoadMetadataSuffix(loadIndirect.Address));
                    case SsaLoadGlobalRValue loadGlobal when NormalizeAggregateType(loadGlobal.Type) == NormalizeAggregateType(valueType):
                        return TryEmitStructuredAggregateBaseStore(
                            destinationAddress,
                            valueType,
                            $"@{EscapeIdentifier(ResolveGlobalSymbolName(loadGlobal.GlobalName))}",
                            destinationAlignmentBytes,
                            GetGlobalObjectAlignmentBytes(loadGlobal.GlobalName, loadGlobal.Type),
                            GetInvariantLoadMetadataSuffix(loadGlobal.GlobalName));
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

    private bool TryEmitStructuredAggregateBaseStore(
        string destinationAddress,
        StarkTypeSymbol valueType,
        string sourceAddress,
        int? destinationAlignmentBytes,
        int? sourceAlignmentBytes,
        string invariantLoadMetadataSuffix)
    {
        if (string.Equals(destinationAddress, sourceAddress, StringComparison.Ordinal))
        {
            return true;
        }

        EmitAggregateAddressCopy(
            destinationAddress,
            sourceAddress,
            valueType,
            destinationAlignmentBytes,
            sourceAlignmentBytes,
            invariantLoadMetadataSuffix);
        return true;
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

        // Unoptimized SSA keeps use-def chains uncollapsed, so this walk can otherwise
        // recurse past the thread stack on large functions; materializing is always safe.
        if (visitingValueNames.Count > 256)
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
            case SsaCallInstruction call:
                return CallRequiresAggregateValueMaterialization(call, valueName, valueType);
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
            case SsaInsertFieldRValue insertField when IsNamedReference(insertField.Value, valueName):
                if (GetAggregateElementType(insertField.Type, insertField.FieldIndex) is not { } fieldType
                    || !CanForwardAggregateValueToAddress(fieldType, valueType))
                {
                    return true;
                }

                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
            case SsaInsertIndexRValue insertIndex when IsNamedReference(insertIndex.Target, valueName):
                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
            case SsaInsertIndexRValue insertIndex when IsNamedReference(insertIndex.Value, valueName):
                if (GetAggregateElementType(insertIndex.Type, insertIndex.ElementIndex) is not { } elementType
                    || !CanForwardAggregateValueToAddress(elementType, valueType))
                {
                    return true;
                }

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

    private bool CallRequiresAggregateValueMaterialization(
        ISsaDirectCallOperation call,
        string valueName,
        StarkTypeSymbol valueType)
    {
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

    private bool CanForwardAggregateValueToIndirectCallParameter(ISsaDirectCallOperation call, int argumentIndex, StarkTypeSymbol valueType)
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
            SsaIndirectCallRValue indirectCall => IsNamedReference(indirectCall.Target, valueName)
                || indirectCall.Arguments.Any(argument => IsNamedReference(argument, valueName))
                || (indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>().Any(address => IsNamedReference(address, valueName)) ?? false),
            SsaConvertRValue convert => IsNamedReference(convert.Operand, valueName),
            SsaExtractFieldRValue extractField => IsNamedReference(extractField.Target, valueName),
            SsaInsertFieldRValue insertField => IsNamedReference(insertField.Target, valueName) || IsNamedReference(insertField.Value, valueName),
            SsaExtractIndexRValue extractIndex => IsNamedReference(extractIndex.Target, valueName),
            SsaInsertIndexRValue insertIndex => IsNamedReference(insertIndex.Target, valueName) || IsNamedReference(insertIndex.Value, valueName),
            SsaMakeSliceFromPointerRValue makeSlice => IsNamedReference(makeSlice.Pointer, valueName) || IsNamedReference(makeSlice.Length, valueName),
            SsaDynamicStorageAllocationRValue allocation => IsNamedReference(allocation.Capacity, valueName),
            SsaDynamicStorageFreeRValue free => IsNamedReference(free.Storage, valueName),
            SsaHeapStorageFreeRValue free => IsNamedReference(free.Pointer, valueName),
            SsaDynamicStorageReserveRValue reserve => IsNamedReference(reserve.StorageAddress, valueName) || IsNamedReference(reserve.AdditionalCapacity, valueName),
            SsaDynamicStorageTryReserveRValue reserve => IsNamedReference(reserve.StorageAddress, valueName) || IsNamedReference(reserve.AdditionalCapacity, valueName),
            SsaDynamicStorageTryReserveCapacityRValue reserve => IsNamedReference(reserve.StorageAddress, valueName) || IsNamedReference(reserve.TargetCapacity, valueName),
            SsaDynamicStorageMoveLastRValue moveLast => IsNamedReference(moveLast.StorageAddress, valueName),
            SsaDynamicStorageMoveAtRValue moveAt => IsNamedReference(moveAt.StorageAddress, valueName) || IsNamedReference(moveAt.Index, valueName),
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
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            leaves.Add(new AggregateScalarLeaf([.. path], type));
            return true;
        }

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

                if (LlvmLayoutControlledAggregateFacts.RequiresPhysicalLayout(namedType))
                {
                    return false;
                }

                var sizeBytes = 0;
                var alignmentBytes = 1;
                for (var index = 0; index < orderedFields.Count; index++)
                {
                    var field = orderedFields[index];
                    var fieldLayout = TryGetConcreteTypeLayout(field.Type);
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

        var aggregateRootType = NormalizeAggregateType(
            StarkTypeSymbols.IsPointerBackedBorrowType(rootType)
                ? StarkTypeSymbols.BorrowReturnValueType(rootType)
                : rootType);

        if (indices.Count == 1
            && TryGetLayoutControlledFieldOffsetBytes(aggregateRootType, indices[0], out var fieldOffsetBytes))
        {
            var fieldAddress = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
            AppendLine($"  {fieldAddress} = getelementptr{GetProvenInObjectGepFlags()} i8, ptr {baseAddress}, i64 {fieldOffsetBytes}");
            return fieldAddress;
        }

        var leafAddress = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        var gepIndices = string.Join(", ", indices.Select(static index => $"i32 {index}"));
        AppendLine($"  {leafAddress} = getelementptr{GetProvenInObjectGepFlags()} {MapType(aggregateRootType)}, ptr {baseAddress}, i32 0, {gepIndices}");
        return leafAddress;
    }

    private StarkTypeSymbol? GetAggregateElementType(StarkTypeSymbol type, int index)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null => normalizedType.ElementType,
            StarkTypeKind.Dynamic when normalizedType.ElementType is not null && index == 0
                => StarkTypeSymbols.RawPointer(normalizedType.ElementType, isMutable: true),
            StarkTypeKind.Dynamic when index == 1
                => StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue)),
            StarkTypeKind.Dynamic when index == 2
                => StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue)),
            StarkTypeKind.Closure when index == 0
                => CallableValueFacts.BuildClosureInvokeFunctionPointerType(normalizedType),
            StarkTypeKind.Closure when index == 1
                => CallableValueFacts.BuildClosureEnvironmentPointerType(normalizedType),
            StarkTypeKind.Closure when index == 2 && normalizedType.ClosureStorageKind == StarkClosureStorageKind.Heap
                => CallableValueFacts.BuildClosureDropFunctionPointerType(),
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
        var normalized = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        return normalized.Kind == StarkTypeKind.Named
               && normalized.NamedType is not null
               && normalized.TypeArguments is { Count: > 0 }
            ? normalized with { TypeArguments = null }
            : normalized;
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
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            return "null";
        }

        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Integer => "0",
            StarkTypeKind.Float => "0.0",
            StarkTypeKind.Bool => "false",
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer => "null",
            StarkTypeKind.Closure => "zeroinitializer",
            _ => "zeroinitializer"
        };
    }
}
