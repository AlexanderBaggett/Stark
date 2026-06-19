using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
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
        EmitEntrySameParameterAssumptions();

        _currentDebugLocation = plan.Location ?? _ssaFunction.Location;
        EmitOptimizedRawPointerLoopIntrinsicCall(plan);

        AppendLine("  ret void");
        AppendLine(string.Empty);
        _currentBlock = null;
        _currentInstructionIndex = -1;
        FlushEntryStaticAllocas();
        return true;
    }

    private void EmitOptimizedRawPointerLoopIntrinsicCall(RawPointerLoopIntrinsicPlan plan)
    {
        ValidateOptimizedRawPointerLoopIntrinsicPlan(plan);
        switch (plan.Kind)
        {
            case RawPointerLoopIntrinsicKind.Memcpy:
                EmitOptimizedRawPointerMemcpy(plan);
                break;
            case RawPointerLoopIntrinsicKind.Memmove:
                EmitOptimizedRawPointerMemmove(plan);
                break;
            case RawPointerLoopIntrinsicKind.Memset:
                EmitOptimizedRawPointerMemset(plan);
                break;
            default:
                throw CreateOptimizedRawPointerLoopInvariantException(plan, $"unsupported intrinsic kind '{plan.Kind}'.");
        }

        EmitOptimizedRawPointerLoopDynamicLengthCommit(plan);
    }

    private void ValidateOptimizedRawPointerLoopIntrinsicPlan(RawPointerLoopIntrinsicPlan plan)
    {
        if (plan.DestinationBase is null)
        {
            throw CreateOptimizedRawPointerLoopInvariantException(plan, "destination base is missing.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(plan.ElementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw CreateOptimizedRawPointerLoopInvariantException(
                plan,
                $"element type '{plan.ElementType.DisplayName}' does not have a positive concrete layout.");
        }

        if (!CanRepresentRawPointerLoopByteLength(plan.Count, elementLayout, _valueDefinitions))
        {
            throw CreateOptimizedRawPointerLoopInvariantException(
                plan,
                $"byte length for count '{plan.Count.Text}' and element type '{plan.ElementType.DisplayName}' is not representable as i64.");
        }

        switch (plan.Kind)
        {
            case RawPointerLoopIntrinsicKind.Memcpy:
            case RawPointerLoopIntrinsicKind.Memmove:
                if (plan.SourceBase is null)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(plan, "source base is missing.");
                }

                if (plan.FillValue is not null)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(plan, "copy/move plan unexpectedly carries a fill value.");
                }

                break;
            case RawPointerLoopIntrinsicKind.Memset:
                if (plan.SourceBase is not null)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(plan, "memset plan unexpectedly carries a source base.");
                }

                if (plan.FillValue is null)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(plan, "fill value is missing.");
                }

                if (NormalizeAggregateType(plan.FillValue.Type).Kind != StarkTypeKind.Integer
                    || NormalizeAggregateType(plan.FillValue.Type).BitWidth != 8)
                {
                    throw CreateOptimizedRawPointerLoopInvariantException(
                        plan,
                        $"memset fill value must be i8-shaped, but found '{plan.FillValue.Type.DisplayName}'.");
                }

                break;
            default:
                throw CreateOptimizedRawPointerLoopInvariantException(plan, $"unsupported intrinsic kind '{plan.Kind}'.");
        }
    }

    private InvalidOperationException CreateOptimizedRawPointerLoopInvariantException(
        RawPointerLoopIntrinsicPlan plan,
        string reason)
    {
        return new InvalidOperationException(
            $"Invalid optimized raw pointer loop intrinsic plan in '{_ssaFunction.Name}' ({plan.Kind}): {reason}");
    }

    private void EmitOptimizedRawPointerMemcpy(RawPointerLoopIntrinsicPlan plan)
    {
        var (byteLength, isZeroLength) = EmitRawPointerLoopByteLength(plan);

        if (isZeroLength)
        {
            return;
        }

        var destinationPointer = EmitOptimizedRawPointerLoopBasePointer(
            plan.DestinationBase,
            plan.DestinationBaseIsSlice,
            "rawptr_loop_destination");
        var sourcePointer = EmitOptimizedRawPointerLoopBasePointer(
            plan.SourceBase!,
            plan.SourceBaseIsSlice,
            "rawptr_loop_source");
        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(
            plan.DestinationBase,
            plan.ElementType,
            plan.DestinationBaseIsSlice);
        var sourceAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(
            plan.SourceBase,
            plan.ElementType,
            plan.SourceBaseIsSlice);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase, plan.SourceBase);
        AppendLine(
            $"  call void @llvm.memcpy.p0.p0.i64(ptr{destinationAlignment} {destinationPointer}, ptr{sourceAlignment} {sourcePointer}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
    }

    private void EmitOptimizedRawPointerMemmove(RawPointerLoopIntrinsicPlan plan)
    {
        var (byteLength, isZeroLength) = EmitRawPointerLoopByteLength(plan);

        if (isZeroLength)
        {
            return;
        }

        var destinationPointer = EmitOptimizedRawPointerLoopBasePointer(
            plan.DestinationBase,
            plan.DestinationBaseIsSlice,
            "rawptr_loop_destination");
        var sourcePointer = EmitOptimizedRawPointerLoopBasePointer(
            plan.SourceBase!,
            plan.SourceBaseIsSlice,
            "rawptr_loop_source");
        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(
            plan.DestinationBase,
            plan.ElementType,
            plan.DestinationBaseIsSlice);
        var sourceAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(
            plan.SourceBase,
            plan.ElementType,
            plan.SourceBaseIsSlice);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase, plan.SourceBase);
        AppendLine(
            $"  call void @llvm.memmove.p0.p0.i64(ptr{destinationAlignment} {destinationPointer}, ptr{sourceAlignment} {sourcePointer}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
    }

    private void EmitOptimizedRawPointerMemset(RawPointerLoopIntrinsicPlan plan)
    {
        var (byteLength, isZeroLength) = EmitRawPointerLoopByteLength(plan);

        if (isZeroLength)
        {
            return;
        }

        var destinationPointer = EmitOptimizedRawPointerLoopBasePointer(
            plan.DestinationBase,
            plan.DestinationBaseIsSlice,
            "rawptr_loop_destination");
        var destinationAlignment = GetOptimizedRawPointerLoopArgumentAlignmentFragment(
            plan.DestinationBase,
            plan.ElementType,
            plan.DestinationBaseIsSlice);
        var scopedNoAliasMetadata = GetOptimizedRawPointerLoopScopedNoAliasMetadataSuffix(plan.DestinationBase);
        AppendLine(
            $"  call void @llvm.memset.p0.i64(ptr{destinationAlignment} {destinationPointer}, i8 {FormatValue(plan.FillValue!)}, i64 {byteLength}, i1 false){scopedNoAliasMetadata}");
    }

    private (string ByteLength, bool IsZeroLength) EmitRawPointerLoopByteLength(RawPointerLoopIntrinsicPlan plan)
    {
        if (TryGetConcreteTypeLayout(NormalizeAggregateType(plan.ElementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw CreateOptimizedRawPointerLoopInvariantException(
                plan,
                $"element type '{plan.ElementType.DisplayName}' does not have a positive concrete layout.");
        }

        if (plan.Count is SsaIntegerConstant constantCount)
        {
            if (constantCount.Value < BigInteger.Zero)
            {
                throw CreateOptimizedRawPointerLoopInvariantException(plan, "constant count is negative.");
            }

            var constantByteLength = constantCount.Value * elementLayout.SizeBytes;
            if (constantByteLength > long.MaxValue)
            {
                throw CreateOptimizedRawPointerLoopInvariantException(plan, "constant byte length exceeds i64.");
            }

            return (constantByteLength.ToString(CultureInfo.InvariantCulture), constantByteLength.IsZero);
        }

        if (!TryGetIntegerValueRange(plan.Count, new HashSet<string>(StringComparer.Ordinal), out var minCount, out var maxCount)
            || minCount < BigInteger.Zero
            || maxCount * elementLayout.SizeBytes > long.MaxValue
            || plan.Count.Type.Kind != StarkTypeKind.Integer
            || plan.Count.Type.BitWidth is not int countBitWidth
            || countBitWidth is <= 0 or > 64)
        {
            throw CreateOptimizedRawPointerLoopInvariantException(
                plan,
                $"dynamic count '{plan.Count.Text}' cannot produce a non-negative i64 byte length.");
        }

        string count64;
        if (countBitWidth == 64)
        {
            count64 = EmitOptimizedRawPointerLoopIntegerValue(plan.Count, plan.Count.Type, "rawptr_loop_count");
        }
        else
        {
            var countValue = EmitOptimizedRawPointerLoopIntegerValue(plan.Count, plan.Count.Type, "rawptr_loop_count");
            count64 = $"%{EscapeIdentifier(CreateAbiTempName("rawptr_loop_count"))}";
            AppendLine($"  {count64} = zext {MapType(plan.Count.Type)} {countValue} to i64");
        }

        if (elementLayout.SizeBytes == 1)
        {
            return (count64, false);
        }

        var byteLength = $"%{EscapeIdentifier(CreateAbiTempName("rawptr_loop_bytes"))}";
        AppendLine($"  {byteLength} = mul i64 {count64}, {elementLayout.SizeBytes}");
        return (byteLength, false);
    }

    private string EmitOptimizedRawPointerLoopBasePointer(SsaValue baseValue, bool isSlice, string purpose)
    {
        if (!isSlice)
        {
            return FormatValue(baseValue);
        }

        var sliceValue = EmitOptimizedRawPointerLoopSliceValue(baseValue, purpose);
        var dataPointer = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_data"))}";
        AppendLine($"  {dataPointer} = extractvalue {MapType(baseValue.Type)} {sliceValue}, 0");
        return dataPointer;
    }

    private string EmitOptimizedRawPointerLoopSliceValue(SsaValue slice, string purpose)
    {
        if (slice is SsaValueReference reference
            && _valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return EmitOptimizedRawPointerLoopSliceValue(use.Value, purpose);
                case SsaLoadIndirectRValue load:
                {
                    var address = EmitOptimizedRawPointerLoopAddress(load.Address, $"{purpose}_slice");
                    var loaded = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_slice"))}";
                    AppendLine($"  {loaded} = load {MapType(load.Type)}, ptr {address}{GetKnownPointerAlignmentSuffix(load.Address, load.Type)}");
                    return loaded;
                }
                case SsaLoadLocalRValue loadLocal:
                {
                    EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                    var loaded = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_slice"))}";
                    AppendLine($"  {loaded} = load {MapType(loadLocal.Type)}, ptr {GetLocalSlotPointer(loadLocal.LocalName)}{GetLocalSlotAlignmentSuffix(loadLocal.LocalName, loadLocal.Type)}");
                    return loaded;
                }
            }
        }

        return FormatValue(slice);
    }

    private string EmitOptimizedRawPointerLoopAddress(SsaValue address, string purpose)
    {
        if (address is SsaValueReference reference
            && _valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return EmitOptimizedRawPointerLoopAddress(use.Value, purpose);
                case SsaAddressOfParameterRValue addressOfParameter:
                    return EmitOptimizedRawPointerLoopAddressOfParameter(addressOfParameter, purpose);
                case SsaAddressOfLocalRValue addressOfLocal:
                    return EmitOptimizedRawPointerLoopAddressOfLocal(addressOfLocal, purpose);
                case SsaFieldAddressRValue fieldAddress:
                {
                    var baseAddress = EmitOptimizedRawPointerLoopAddress(fieldAddress.Address, $"{purpose}_base");
                    var fieldPointer = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_field"))}";
                    var aggregateType = NormalizeAggregateType(
                        StarkTypeSymbols.IsPointerBackedBorrowType(fieldAddress.AggregateType)
                            ? StarkTypeSymbols.BorrowReturnValueType(fieldAddress.AggregateType)
                            : fieldAddress.AggregateType);
                    if (TryGetLayoutControlledFieldOffsetBytes(
                            aggregateType,
                            fieldAddress.FieldIndex,
                            out var fieldOffsetBytes))
                    {
                        AppendLine($"  {fieldPointer} = getelementptr{GetProvenInObjectGepFlags()} i8, ptr {baseAddress}, i64 {fieldOffsetBytes}");
                    }
                    else
                    {
                        AppendLine($"  {fieldPointer} = getelementptr{GetProvenInObjectGepFlags()} {MapType(aggregateType)}, ptr {baseAddress}, i32 0, i32 {fieldAddress.FieldIndex}");
                    }

                    return fieldPointer;
                }
            }
        }

        return FormatValue(address);
    }

    private void EmitOptimizedRawPointerLoopDynamicLengthCommit(RawPointerLoopIntrinsicPlan plan)
    {
        if (plan.DynamicLengthCommit is not { } commit)
        {
            return;
        }

        var address = EmitOptimizedRawPointerLoopAddress(commit.LengthAddress, "dynamic_length_commit");
        var count = EmitIntegerForOptimizedRawPointerLoopLengthCommit(plan.Count, commit.LengthType, "dynamic_length_count");
        var start = EmitIntegerForOptimizedRawPointerLoopLengthCommit(commit.StartLength, commit.LengthType, "dynamic_length_start");
        var finalLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_length_final"))}";
        AppendLine($"  {finalLength} = add {MapType(commit.LengthType)} {start}, {count}");
        AppendLine($"  store {MapType(commit.LengthType)} {finalLength}, ptr {address}{GetKnownPointerAlignmentSuffix(commit.LengthAddress, commit.LengthType)}");
    }

    private string EmitIntegerForOptimizedRawPointerLoopLengthCommit(
        SsaValue value,
        StarkTypeSymbol targetType,
        string purpose)
    {
        if (value.Type.Kind != StarkTypeKind.Integer
            || targetType.Kind != StarkTypeKind.Integer
            || value.Type.BitWidth is not { } sourceWidth
            || targetType.BitWidth is not { } targetWidth)
        {
            return FormatValue(value);
        }

        if (value is SsaValueReference reference
            && _valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return EmitIntegerForOptimizedRawPointerLoopLengthCommit(use.Value, targetType, purpose);
                case SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert):
                    return EmitIntegerForOptimizedRawPointerLoopLengthCommit(convert.Operand, targetType, purpose);
                case SsaBinaryRValue binary when IsAddZero(binary, _valueDefinitions):
                    return EmitIntegerForOptimizedRawPointerLoopLengthCommit(
                        IsZeroIntegerValue(binary.Left, _valueDefinitions, new HashSet<string>(StringComparer.Ordinal))
                            ? binary.Right
                            : binary.Left,
                        targetType,
                        purpose);
            }
        }

        if (sourceWidth == targetWidth)
        {
            return FormatValue(value);
        }

        var converted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        var op = sourceWidth < targetWidth ? "zext" : "trunc";
        AppendLine($"  {converted} = {op} {MapType(value.Type)} {FormatValue(value)} to {MapType(targetType)}");
        return converted;
    }

    private string EmitOptimizedRawPointerLoopIntegerValue(
        SsaValue value,
        StarkTypeSymbol targetType,
        string purpose)
    {
        if (value.Type.Kind != StarkTypeKind.Integer
            || targetType.Kind != StarkTypeKind.Integer
            || value.Type.BitWidth is not { } sourceWidth
            || targetType.BitWidth is not { } targetWidth)
        {
            return FormatValue(value);
        }

        if (value is SsaValueReference reference
            && _valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return EmitOptimizedRawPointerLoopIntegerValue(use.Value, targetType, purpose);
                case SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert):
                    return EmitOptimizedRawPointerLoopIntegerValue(convert.Operand, targetType, purpose);
                case SsaBinaryRValue binary when IsAddZero(binary, _valueDefinitions):
                    return EmitOptimizedRawPointerLoopIntegerValue(
                        IsZeroIntegerValue(binary.Left, _valueDefinitions, new HashSet<string>(StringComparer.Ordinal))
                            ? binary.Right
                            : binary.Left,
                        targetType,
                        purpose);
            }
        }

        if (sourceWidth == targetWidth)
        {
            return FormatValue(value);
        }

        var converted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        var op = sourceWidth < targetWidth ? "zext" : "trunc";
        AppendLine($"  {converted} = {op} {MapType(value.Type)} {FormatValue(value)} to {MapType(targetType)}");
        return converted;
    }

    private string EmitOptimizedRawPointerLoopAddressOfParameter(SsaAddressOfParameterRValue addressOfParameter, string purpose)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
        if (parameter is null)
        {
            throw new InvalidOperationException(
                $"Invalid optimized raw pointer loop address in '{_ssaFunction.Name}': unknown ABI parameter '{addressOfParameter.ParameterName}'.");
        }

        var result = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_addr"))}";
        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            AppendLine(
                $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfParameter.PointeeType))}, ptr %{EscapeIdentifier(parameter.LlvmName)}, i32 0");
            return result;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        AppendLine(
            $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfParameter.PointeeType))}, ptr %{EscapeIdentifier($"slot_param_{parameter.SourceName}")}, i32 0");
        return result;
    }

    private string EmitOptimizedRawPointerLoopAddressOfLocal(SsaAddressOfLocalRValue addressOfLocal, string purpose)
    {
        EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
        var result = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_addr"))}";
        AppendLine($"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfLocal.PointeeType))}, ptr {GetLocalSlotPointer(addressOfLocal.LocalName)}, i32 0");
        return result;
    }

    private string GetOptimizedRawPointerLoopArgumentAlignmentFragment(
        SsaValue? pointer,
        StarkTypeSymbol elementType,
        bool isSlice = false)
    {
        if (pointer is null)
        {
            return string.Empty;
        }

        if (isSlice)
        {
            var sliceAlignment = TryGetKnownSliceDataAlignmentBytes(
                    pointer,
                    new HashSet<string>(StringComparer.Ordinal),
                    out var sliceAlignmentBytes)
                ? GetLeafAlignmentBytes(sliceAlignmentBytes, elementType)
                : GetTypeAlignmentBytes(elementType);
            return GetArgumentAlignmentFragment(sliceAlignment);
        }

        var alignmentBytes = GetKnownPointerAlignmentBytes(pointer, elementType)
            ?? GetBoundedRawPointerRegionAlignmentBytes(pointer, elementType)
            ?? GetBoundedRawPointerParameterAlignmentBytes(pointer, elementType);
        return GetArgumentAlignmentFragment(alignmentBytes);
    }

    private int? GetBoundedRawPointerRegionAlignmentBytes(SsaValue pointer, StarkTypeSymbol elementType)
    {
        if (!TryGetBoundedRawPointerRegionFact(
                pointer,
                new HashSet<string>(StringComparer.Ordinal),
                out var boundedRegion)
            || boundedRegion.ElementAlignmentBytes is not > 1
            || pointer.Type.ElementType is not { } pointerElementType
            || NormalizeAggregateType(pointerElementType) != NormalizeAggregateType(elementType))
        {
            return null;
        }

        return boundedRegion.ElementAlignmentBytes.Value;
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
            && TryMatchForwardBackwardRawPointerMemmoveLoop(function, tryGetConcreteTypeLayout, out plan))
        {
            return true;
        }

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
        if (!function.HasBody)
        {
            return plans;
        }

        // These per-function facts are loop-invariant; computing them per candidate
        // preheader made this scan quadratic in block count on large functions
        // (dominator dataflow alone is O(blocks^2) per computation).
        var definitions = CollectValueDefinitions(function);
        var nonEntryValueNames = CollectNonEntrySsaValueNames(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var availableValueNamesByBlock = BuildAvailableValueNamesByBlock(function);
        var predecessorCounts = CountPredecessors(function);

        var skippedBlockIds = new HashSet<int>();
        foreach (var preheader in function.Blocks)
        {
            if (skippedBlockIds.Contains(preheader.Id)
                || !TryMatchEmbeddedOptimizedRawPointerLoopFromPreheader(
                    function,
                    preheader,
                    tryGetConcreteTypeLayout,
                    parameterEffects,
                    definitions,
                    nonEntryValueNames,
                    blocksById,
                    availableValueNamesByBlock,
                    predecessorCounts,
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
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<int, ISet<string>> availableValueNamesByBlock,
        IReadOnlyDictionary<int, int> predecessorCounts,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (TryMatchEmbeddedOptimizedRawPointerMemmoveFromPreheader(
                function,
                preheader,
                tryGetConcreteTypeLayout,
                blocksById,
                definitions,
                nonEntryValueNames,
                availableValueNamesByBlock,
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
                availableValueNamesByBlock,
                out var loop,
                out var exit))
        {
            return false;
        }

        if (!CanReplaceEmbeddedRawPointerLoop(function, loop, exit, predecessorCounts))
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
        IReadOnlyDictionary<int, ISet<string>> availableValueNamesByBlock,
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
                availableValueNamesByBlock,
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
                availableValueNamesByBlock,
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
                out var sourceElementType,
                out var sourceBaseIsSlice)
            || !TryMatchTemporaryToRawPointerCopyLoop(
                temporaryToDestinationLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var destinationBase,
                out var destinationElementType,
                out var destinationBaseIsSlice,
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
            DestinationBaseIsSlice: destinationBaseIsSlice,
            SourceBaseIsSlice: sourceBaseIsSlice,
            PreheaderBlockId: preheader.Id,
            ExitBlockId: exit.Id,
            SkippedBlockIds: skippedBlockIds);
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
                SsaIndirectCallRValue indirectCall =>
                    ReferencesSkippedDefinition(indirectCall.Target)
                    || indirectCall.Arguments.Any(ReferencesSkippedDefinition)
                    || (indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>().Any(ReferencesSkippedDefinition) == true),
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
                SsaDynamicStorageAllocationRValue allocation => ReferencesSkippedDefinition(allocation.Capacity),
                SsaDynamicStorageFreeRValue free => ReferencesSkippedDefinition(free.Storage),
                SsaHeapStorageFreeRValue free => ReferencesSkippedDefinition(free.Pointer),
                SsaDynamicStorageReserveRValue reserve =>
                    ReferencesSkippedDefinition(reserve.StorageAddress)
                    || ReferencesSkippedDefinition(reserve.AdditionalCapacity),
                SsaDynamicStorageTryReserveRValue reserve =>
                    ReferencesSkippedDefinition(reserve.StorageAddress)
                    || ReferencesSkippedDefinition(reserve.AdditionalCapacity),
                SsaDynamicStorageTryReserveCapacityRValue reserve =>
                    ReferencesSkippedDefinition(reserve.StorageAddress)
                    || ReferencesSkippedDefinition(reserve.TargetCapacity),
                SsaDynamicStorageMoveLastRValue moveLast => ReferencesSkippedDefinition(moveLast.StorageAddress),
                SsaDynamicStorageMoveAtRValue moveAt =>
                    ReferencesSkippedDefinition(moveAt.StorageAddress)
                    || ReferencesSkippedDefinition(moveAt.Index),
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

    private static bool TryMatchForwardBackwardRawPointerMemmoveLoop(
        SsaFunction function,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!function.HasBody
            || function.ReturnType.Kind != StarkTypeKind.Void)
        {
            return false;
        }

        var definitions = CollectValueDefinitions(function);
        var nonEntryValueNames = CollectNonEntrySsaValueNames(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var availableValueNamesByBlock = BuildAvailableValueNamesByBlock(function);
        if (!blocksById.TryGetValue(function.EntryBlockId, out var entry)
            || !TryMatchOptionalZeroCountGuard(
                entry,
                blocksById,
                definitions,
                out var directionBlock,
                out var guardedCount)
            || directionBlock.Terminator.Kind != SsaTerminatorKind.Branch
            || directionBlock.Terminator.Condition is null
            || directionBlock.Terminator.Targets.Count != 2
            || !TryResolveComparisonCondition(directionBlock.Terminator.Condition, definitions, out var directionComparison))
        {
            return false;
        }

        if (TryMatchForwardBackwardCandidate(
                directionBlock.Terminator.Targets[0],
                directionBlock.Terminator.Targets[1],
                conditionTrueSelectsForward: true,
                out plan))
        {
            return true;
        }

        return TryMatchForwardBackwardCandidate(
            directionBlock.Terminator.Targets[1],
            directionBlock.Terminator.Targets[0],
            conditionTrueSelectsForward: false,
            out plan);

        bool TryMatchForwardBackwardCandidate(
            int forwardPreheaderId,
            int backwardPreheaderId,
            bool conditionTrueSelectsForward,
            out RawPointerLoopIntrinsicPlan candidatePlan)
        {
            candidatePlan = null!;
            if (!blocksById.TryGetValue(forwardPreheaderId, out var forwardPreheader)
                || !blocksById.TryGetValue(backwardPreheaderId, out var backwardPreheader))
            {
                return false;
            }

            if (!TryMatchCanonicalRawPointerLoopFromPreheader(
                    function,
                    forwardPreheader,
                    blocksById,
                    definitions,
                    nonEntryValueNames,
                    availableValueNamesByBlock,
                    out var forwardLoop,
                    out var forwardExit)
                || !TryMatchBackwardRawPointerLoopFromPreheader(
                    function,
                    backwardPreheader,
                    blocksById,
                    definitions,
                    nonEntryValueNames,
                    availableValueNamesByBlock,
                    out var backwardLoop,
                    out var backwardExit)
                || !IsPlainVoidReturnBlock(forwardExit)
                || !IsPlainVoidReturnBlock(backwardExit))
            {
                return false;
            }

            if (guardedCount is not null
                && !AreEquivalentFunctionEntryValues(function, guardedCount, forwardLoop.Count, definitions, nonEntryValueNames))
            {
                return false;
            }

            if (!AreEquivalentFunctionEntryValues(function, forwardLoop.Count, backwardLoop.Count, definitions, nonEntryValueNames))
            {
                return false;
            }

            if (!TryMatchRawPointerMemmoveCopyLoop(
                    forwardLoop,
                    tryGetConcreteTypeLayout,
                    out var forwardCopy)
                || !TryMatchRawPointerMemmoveCopyLoop(
                    backwardLoop,
                    tryGetConcreteTypeLayout,
                    out var backwardCopy)
                || NormalizeAggregateType(forwardCopy.ElementType) != NormalizeAggregateType(backwardCopy.ElementType))
            {
                return false;
            }

            if (!AreEquivalentMemoryBases(
                    function,
                    forwardCopy.SourceBase,
                    backwardCopy.SourceBase,
                    definitions,
                    nonEntryValueNames)
                || !AreEquivalentMemoryBases(
                    function,
                    forwardCopy.DestinationBase,
                    backwardCopy.DestinationBase,
                    definitions,
                    nonEntryValueNames))
            {
                return false;
            }

            if (!TryMatchMemmoveDirectionComparison(
                    function,
                    directionComparison,
                    forwardCopy,
                    conditionTrueSelectsForward,
                    definitions))
            {
                return false;
            }

            candidatePlan = new RawPointerLoopIntrinsicPlan(
                RawPointerLoopIntrinsicKind.Memmove,
                forwardCopy.DestinationBase,
                forwardCopy.SourceBase,
                null,
                forwardCopy.ElementType,
                forwardLoop.Count,
                forwardCopy.Location,
                DestinationBaseIsSlice: forwardCopy.DestinationBaseIsSlice,
                SourceBaseIsSlice: forwardCopy.SourceBaseIsSlice);
            return true;
        }
    }

    private static bool TryMatchOptionalZeroCountGuard(
        SsaBasicBlock entry,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaBasicBlock directionBlock,
        out SsaValue? guardedCount)
    {
        directionBlock = entry;
        guardedCount = null;
        if (entry.Terminator.Kind != SsaTerminatorKind.Branch
            || entry.Terminator.Condition is null
            || entry.Terminator.Targets.Count != 2
            || !TryResolveComparisonCondition(entry.Terminator.Condition, definitions, out var comparison)
            || !TryMatchZeroCountComparison(comparison, definitions, out guardedCount))
        {
            return true;
        }

        var trueTargetId = entry.Terminator.Targets[0];
        var falseTargetId = entry.Terminator.Targets[1];
        if (comparison.Operator == SsaBinaryOperator.Equal
            && blocksById.TryGetValue(trueTargetId, out var zeroExit)
            && IsPlainVoidReturnBlock(zeroExit)
            && blocksById.TryGetValue(falseTargetId, out var nonZeroBlock))
        {
            directionBlock = nonZeroBlock;
            return true;
        }

        if (comparison.Operator == SsaBinaryOperator.NotEqual
            && blocksById.TryGetValue(falseTargetId, out zeroExit)
            && IsPlainVoidReturnBlock(zeroExit)
            && blocksById.TryGetValue(trueTargetId, out nonZeroBlock))
        {
            directionBlock = nonZeroBlock;
            return true;
        }

        directionBlock = null!;
        guardedCount = null;
        return false;
    }

    private static bool TryMatchZeroCountComparison(
        SsaBinaryRValue comparison,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue count)
    {
        count = null!;
        if (comparison.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        if (IsZeroIntegerValue(comparison.Left, definitions, new HashSet<string>(StringComparer.Ordinal)))
        {
            count = comparison.Right;
            return true;
        }

        if (IsZeroIntegerValue(comparison.Right, definitions, new HashSet<string>(StringComparer.Ordinal)))
        {
            count = comparison.Left;
            return true;
        }

        return false;
    }

    private static bool TryMatchBackwardRawPointerLoopFromPreheader(
        SsaFunction function,
        SsaBasicBlock preheader,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        IReadOnlyDictionary<int, ISet<string>> availableValueNamesByBlock,
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
            || !TryResolveEntryAvailableValue(initialValue, definitions, nonEntryValueNames, out var count)
            || !TryGetPhiIncoming(induction, body.Id, out var updateValue)
            || updateValue is not SsaValueReference updateReference
            || !definitions.TryGetValue(updateReference.Name, out var updateDefinition)
            || !IsDecrementByOne(updateDefinition, induction.ResultName, definitions)
            || !CanRepresentRawPointerLoopByteLength(count, elementLayout: null, definitions))
        {
            return false;
        }

        if (!TryResolveComparisonCondition(condition.Terminator.Condition, definitions, out var comparison)
            || !IsPositiveInductionCondition(comparison, induction.ResultName, definitions))
        {
            return false;
        }

        loop = new CanonicalRawPointerLoop(
            function,
            preheader.Id,
            condition.Id,
            body,
            exitBlock.Id,
            updateReference.Name,
            updateReference.Name,
            count,
            definitions,
            nonEntryValueNames,
            availableValueNamesByBlock.TryGetValue(preheader.Id, out var availableValueNames)
                ? availableValueNames
                : CollectBlockValueNames(preheader));
        return true;
    }

    private static bool IsPositiveInductionCondition(
        SsaBinaryRValue comparison,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return comparison.Operator == SsaBinaryOperator.GreaterThan
                && IsInductionValue(comparison.Left, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
                && IsZeroIntegerValue(comparison.Right, definitions, new HashSet<string>(StringComparer.Ordinal))
            || comparison.Operator == SsaBinaryOperator.LessThan
                && IsZeroIntegerValue(comparison.Left, definitions, new HashSet<string>(StringComparer.Ordinal))
                && IsInductionValue(comparison.Right, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool TryMatchRawPointerMemmoveCopyLoop(
        CanonicalRawPointerLoop loop,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out RawPointerCopyLoopMatch match)
    {
        match = null!;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || !TryMatchMemoryLoopIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                requireMutablePointer: true,
                out var destinationBase,
                out var destinationElementType,
                out var destinationAddressName,
                out var destinationBaseIsSlice,
                out var destinationSupportValueNames)
            || !TryMatchMemoryLoopLoad(
                loadDefinition,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                out var sourceBase,
                out var sourceElementType,
                out var sourceAddressName,
                out var sourceBaseIsSlice,
                out var sourceSupportValueNames)
            || NormalizeAggregateType(destinationElementType) != NormalizeAggregateType(sourceElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(store.Value.Type) != NormalizeAggregateType(destinationElementType)
            || !CanUseRawPointerMemcpyElement(destinationElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(destinationElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            destinationAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!string.IsNullOrEmpty(sourceAddressName))
        {
            allowedValueNames.Add(sourceAddressName);
        }

        foreach (var valueName in destinationSupportValueNames.Concat(sourceSupportValueNames))
        {
            allowedValueNames.Add(valueName);
        }

        if (!BodyContainsOnlyAllowedInstructions(loop.Body, allowedValueNames))
        {
            return false;
        }

        match = new RawPointerCopyLoopMatch(
            destinationBase,
            sourceBase,
            destinationElementType,
            store.Location,
            destinationBaseIsSlice,
            sourceBaseIsSlice);
        return true;
    }

    private static bool AreEquivalentMemoryBases(
        SsaFunction function,
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames)
    {
        if (AreEquivalentEntryValues(left, right, definitions, nonEntryValueNames))
        {
            return true;
        }

        return TryResolveParameterName(function, left, definitions, out var leftParameter)
            && TryResolveParameterName(function, right, definitions, out var rightParameter)
            && string.Equals(leftParameter, rightParameter, StringComparison.Ordinal);
    }

    private static bool AreEquivalentFunctionEntryValues(
        SsaFunction function,
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames)
    {
        if (AreEquivalentEntryValues(left, right, definitions, nonEntryValueNames))
        {
            return true;
        }

        return TryResolveParameterName(function, left, definitions, out var leftParameter)
            && TryResolveParameterName(function, right, definitions, out var rightParameter)
            && string.Equals(leftParameter, rightParameter, StringComparison.Ordinal);
    }

    private static bool TryMatchMemmoveDirectionComparison(
        SsaFunction function,
        SsaBinaryRValue comparison,
        RawPointerCopyLoopMatch copy,
        bool conditionTrueSelectsForward,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (!TryResolveParameterName(function, copy.DestinationBase, definitions, out var destinationParameter)
            || !TryResolveParameterName(function, copy.SourceBase, definitions, out var sourceParameter)
            || string.Equals(destinationParameter, sourceParameter, StringComparison.Ordinal)
            || !TryResolveZeroOffsetPointerParameterName(function, comparison.Left, definitions, out var leftParameter)
            || !TryResolveZeroOffsetPointerParameterName(function, comparison.Right, definitions, out var rightParameter))
        {
            return false;
        }

        bool destinationBeforeSourceWhenTrue;
        if (comparison.Operator == SsaBinaryOperator.LessThan
            && string.Equals(leftParameter, destinationParameter, StringComparison.Ordinal)
            && string.Equals(rightParameter, sourceParameter, StringComparison.Ordinal)
            || comparison.Operator == SsaBinaryOperator.GreaterThan
            && string.Equals(leftParameter, sourceParameter, StringComparison.Ordinal)
            && string.Equals(rightParameter, destinationParameter, StringComparison.Ordinal))
        {
            destinationBeforeSourceWhenTrue = true;
        }
        else if (comparison.Operator == SsaBinaryOperator.LessThan
                 && string.Equals(leftParameter, sourceParameter, StringComparison.Ordinal)
                 && string.Equals(rightParameter, destinationParameter, StringComparison.Ordinal)
                 || comparison.Operator == SsaBinaryOperator.GreaterThan
                 && string.Equals(leftParameter, destinationParameter, StringComparison.Ordinal)
                 && string.Equals(rightParameter, sourceParameter, StringComparison.Ordinal))
        {
            destinationBeforeSourceWhenTrue = false;
        }
        else
        {
            return false;
        }

        return destinationBeforeSourceWhenTrue == conditionTrueSelectsForward;
    }

    private static bool TryResolveZeroOffsetPointerParameterName(
        SsaFunction function,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string parameterName)
    {
        switch (value)
        {
            case SsaValueReference reference when definitions.TryGetValue(reference.Name, out var definition):
                return TryResolveZeroOffsetPointerParameterName(function, definition, definitions, out parameterName);
            default:
                return TryResolveParameterName(function, value, definitions, out parameterName);
        }
    }

    private static bool TryResolveZeroOffsetPointerParameterName(
        SsaFunction function,
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string parameterName)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveZeroOffsetPointerParameterName(function, use.Value, definitions, out parameterName);
            case SsaConvertRValue convert:
                return TryResolveZeroOffsetPointerParameterName(function, convert.Operand, definitions, out parameterName);
            case SsaSliceElementAddressRValue sliceElementAddress
                when IsZeroIntegerValue(sliceElementAddress.Index, definitions, new HashSet<string>(StringComparer.Ordinal)):
                return TryResolveParameterName(function, sliceElementAddress.Slice, definitions, out parameterName);
            case SsaElementAddressRValue elementAddress
                when elementAddress.ConstantIndex == 0
                     || elementAddress.Index is not null
                     && IsZeroIntegerValue(elementAddress.Index, definitions, new HashSet<string>(StringComparer.Ordinal)):
                return TryResolveParameterName(function, elementAddress.Address, definitions, out parameterName);
            default:
                parameterName = string.Empty;
                return false;
        }
    }

    private static bool IsPlainVoidReturnBlock(SsaBasicBlock block)
    {
        return block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Return
            && block.Terminator.Value is null;
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
        var availableValueNamesByBlock = BuildAvailableValueNamesByBlock(function);
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
                availableValueNamesByBlock,
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
                availableValueNamesByBlock,
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
                out var sourceElementType,
                out var sourceBaseIsSlice)
            || !TryMatchTemporaryToRawPointerCopyLoop(
                temporaryToDestinationLoop,
                temporaryLocalName,
                temporaryElementType,
                tryGetConcreteTypeLayout,
                out var destinationBase,
                out var destinationElementType,
                out var destinationBaseIsSlice,
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
            DestinationBaseIsSlice: destinationBaseIsSlice,
            SourceBaseIsSlice: sourceBaseIsSlice);
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
                || indirectCall.Arguments.Any(argument => ValueReferencesLocal(argument, localName, definitions, visitedValueNames))
                || (indirectCall.IndirectArgumentAddresses?.Any(address => address is not null && ValueReferencesLocal(address, localName, definitions, visitedValueNames)) == true),
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
            SsaDynamicStorageAllocationRValue allocation =>
                ValueReferencesLocal(allocation.Capacity, localName, definitions, visitedValueNames),
            SsaDynamicStorageFreeRValue free =>
                ValueReferencesLocal(free.Storage, localName, definitions, visitedValueNames),
            SsaHeapStorageFreeRValue free =>
                ValueReferencesLocal(free.Pointer, localName, definitions, visitedValueNames),
            SsaDynamicStorageReserveRValue reserve =>
                ValueReferencesLocal(reserve.StorageAddress, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(reserve.AdditionalCapacity, localName, definitions, visitedValueNames),
            SsaDynamicStorageTryReserveRValue reserve =>
                ValueReferencesLocal(reserve.StorageAddress, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(reserve.AdditionalCapacity, localName, definitions, visitedValueNames),
            SsaDynamicStorageTryReserveCapacityRValue reserve =>
                ValueReferencesLocal(reserve.StorageAddress, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(reserve.TargetCapacity, localName, definitions, visitedValueNames),
            SsaDynamicStorageMoveLastRValue moveLast =>
                ValueReferencesLocal(moveLast.StorageAddress, localName, definitions, visitedValueNames),
            SsaDynamicStorageMoveAtRValue moveAt =>
                ValueReferencesLocal(moveAt.StorageAddress, localName, definitions, visitedValueNames)
                || ValueReferencesLocal(moveAt.Index, localName, definitions, visitedValueNames),
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
        IReadOnlyDictionary<int, ISet<string>> availableValueNamesByBlock,
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

        var loopLocalValueNames = CollectBlockValueNames(condition)
            .Concat(CollectBlockValueNames(body))
            .ToHashSet(StringComparer.Ordinal);
        if (!TryResolveComparisonCondition(condition.Terminator.Condition, definitions, out var comparison)
            || comparison.Operator != SsaBinaryOperator.LessThan
            || !IsInductionValue(comparison.Left, induction.ResultName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryResolvePreheaderAvailableValue(comparison.Right, definitions, loopLocalValueNames, out var count)
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
            nonEntryValueNames,
            availableValueNamesByBlock.TryGetValue(preheader.Id, out var availableValueNames)
                ? availableValueNames
                : CollectBlockValueNames(preheader));
        return true;
    }

    private static bool TryMatchRawPointerToTemporaryCopyLoop(
        CanonicalRawPointerLoop loop,
        string temporaryLocalName,
        StarkTypeSymbol temporaryElementType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out SsaValue sourceBase,
        out StarkTypeSymbol sourceElementType,
        out bool sourceBaseIsSlice)
    {
        sourceBase = null!;
        sourceElementType = StarkTypeSymbols.Error;
        sourceBaseIsSlice = false;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || !TryMatchFixedArrayLocalIndexedElementAddress(
                store.Address,
                temporaryLocalName,
                loop.InductionValueName,
                loop.ValueDefinitions,
                out var storedElementType,
                out var temporaryAddressName,
                out var temporaryRootAddressName)
            || !TryMatchMemoryLoopLoad(
                loadDefinition,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                out sourceBase,
                out sourceElementType,
                out var sourceAddressName,
                out sourceBaseIsSlice,
                out var sourceSupportValueNames)
            || NormalizeAggregateType(storedElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(sourceElementType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(temporaryElementType)
            || NormalizeAggregateType(store.Value.Type) != NormalizeAggregateType(temporaryElementType)
            || !CanUseRawPointerMemcpyElement(temporaryElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(temporaryElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            temporaryAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!string.IsNullOrEmpty(sourceAddressName))
        {
            allowedValueNames.Add(sourceAddressName);
        }

        foreach (var valueName in sourceSupportValueNames)
        {
            allowedValueNames.Add(valueName);
        }

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
        out bool destinationBaseIsSlice,
        out SourceLocation? location)
    {
        destinationBase = null!;
        destinationElementType = StarkTypeSymbols.Error;
        destinationBaseIsSlice = false;
        location = null;
        if (!TryGetSingleStore(loop.Body, out var store)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || loadDefinition is not SsaLoadIndirectRValue load
            || !TryMatchMemoryLoopIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                requireMutablePointer: true,
                out destinationBase,
                out destinationElementType,
                out var destinationAddressName,
                out destinationBaseIsSlice,
                out var destinationSupportValueNames)
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
        foreach (var valueName in destinationSupportValueNames)
        {
            allowedValueNames.Add(valueName);
        }

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
            nonEntryValueNames,
            CollectBlockValueNames(entry));
        return true;
    }

    private static bool TryMatchRawPointerMemcpyLoop(
        CanonicalRawPointerLoop loop,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!TryGetDataStoreAndOptionalDynamicLengthCommit(loop, out var store, out var dynamicLengthCommit)
            || store.Value is not SsaValueReference loadReference
            || !loop.ValueDefinitions.TryGetValue(loadReference.Name, out var loadDefinition)
            || !TryMatchMemoryLoopIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                requireMutablePointer: true,
                out var destinationBase,
                out var destinationElementType,
                out var destinationAddressName,
                out var destinationBaseIsSlice,
                out var destinationSupportValueNames)
            || !TryMatchMemoryLoopLoad(
                loadDefinition,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                out var sourceBase,
                out var sourceElementType,
                out var sourceAddressName,
                out var sourceBaseIsSlice,
                out var sourceSupportValueNames)
            || NormalizeAggregateType(destinationElementType) != NormalizeAggregateType(sourceElementType)
            || NormalizeAggregateType(store.ValueType) != NormalizeAggregateType(destinationElementType)
            || NormalizeAggregateType(store.Value.Type) != NormalizeAggregateType(destinationElementType)
            || AreEquivalentEntryValues(destinationBase, sourceBase, loop.ValueDefinitions, loop.NonEntryValueNames)
            || !HaveNoAliasProofForMemcpy(loop.Function, destinationBase, sourceBase, loop.ValueDefinitions, parameterEffects)
            || !CanUseRawPointerMemcpyElement(destinationElementType)
            || tryGetConcreteTypeLayout(NormalizeAggregateType(destinationElementType)) is not { } elementLayout
            || !CanRepresentRawPointerLoopByteLength(loop.Count, elementLayout, loop.ValueDefinitions))
        {
            return false;
        }

        var allowedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            destinationAddressName,
            loadReference.Name,
            loop.UpdateValueName
        };
        if (!string.IsNullOrEmpty(sourceAddressName))
        {
            allowedValueNames.Add(sourceAddressName);
        }

        foreach (var valueName in destinationSupportValueNames.Concat(sourceSupportValueNames))
        {
            allowedValueNames.Add(valueName);
        }

        foreach (var valueName in dynamicLengthCommit?.SupportValueNames ?? [])
        {
            allowedValueNames.Add(valueName);
        }

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
            store.Location,
            DestinationBaseIsSlice: destinationBaseIsSlice,
            SourceBaseIsSlice: sourceBaseIsSlice,
            DynamicLengthCommit: dynamicLengthCommit);
        return true;
    }

    private static bool TryMatchRawPointerMemsetLoop(
        CanonicalRawPointerLoop loop,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        out RawPointerLoopIntrinsicPlan plan)
    {
        plan = null!;
        if (!TryGetDataStoreAndOptionalDynamicLengthCommit(loop, out var store, out var dynamicLengthCommit)
            || !TryMatchMemoryLoopIndexedElementAddress(
                store.Address,
                loop.InductionValueName,
                loop.ValueDefinitions,
                loop.NonEntryValueNames,
                loop.AvailableValueNames,
                requireMutablePointer: true,
                out var destinationBase,
                out var destinationElementType,
                out var destinationAddressName,
                out var destinationBaseIsSlice,
                out var destinationSupportValueNames)
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
        foreach (var valueName in destinationSupportValueNames)
        {
            allowedValueNames.Add(valueName);
        }

        foreach (var valueName in dynamicLengthCommit?.SupportValueNames ?? [])
        {
            allowedValueNames.Add(valueName);
        }

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
            store.Location,
            DestinationBaseIsSlice: destinationBaseIsSlice,
            DynamicLengthCommit: dynamicLengthCommit);
        return true;
    }

    private static bool TryGetDataStoreAndOptionalDynamicLengthCommit(
        CanonicalRawPointerLoop loop,
        out SsaStoreIndirectInstruction dataStore,
        out DynamicLengthCommitPlan? dynamicLengthCommit)
    {
        dataStore = null!;
        dynamicLengthCommit = null;
        var stores = loop.Body.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();
        if (stores.Length == 1)
        {
            dataStore = stores[0];
            return true;
        }

        if (stores.Length != 2)
        {
            return false;
        }

        if (TryMatchDynamicLengthCommitStore(loop, stores[0], out dynamicLengthCommit))
        {
            dataStore = stores[1];
            return true;
        }

        if (TryMatchDynamicLengthCommitStore(loop, stores[1], out dynamicLengthCommit))
        {
            dataStore = stores[0];
            return true;
        }

        return false;
    }

    private static bool TryMatchDynamicLengthCommitStore(
        CanonicalRawPointerLoop loop,
        SsaStoreIndirectInstruction store,
        out DynamicLengthCommitPlan commit)
    {
        commit = null!;
        if (store.Address is not SsaValueReference addressReference
            || !loop.ValueDefinitions.TryGetValue(addressReference.Name, out var addressDefinition)
            || addressDefinition is not SsaFieldAddressRValue
            {
                FieldName: "Length",
                AggregateType.Kind: StarkTypeKind.Dynamic
            }
            || store.ValueType.Kind != StarkTypeKind.Integer
            || !TryMatchDynamicLengthCommitValue(
                loop,
                store.Value,
                out var startLength,
                out var supportValueNames)
            || ValueReferencesBodyDefinition(startLength, loop.Body))
        {
            return false;
        }

        var support = supportValueNames.ToHashSet(StringComparer.Ordinal);
        support.Add(addressReference.Name);
        commit = new DynamicLengthCommitPlan(
            store.Address,
            startLength,
            store.ValueType,
            support.ToArray());
        return true;
    }

    private static bool TryMatchDynamicLengthCommitValue(
        CanonicalRawPointerLoop loop,
        SsaValue value,
        out SsaValue startLength,
        out IReadOnlyList<string> supportValueNames)
    {
        startLength = null!;
        var support = new HashSet<string>(StringComparer.Ordinal);
        if (!TryMatchAddOne(value, loop.ValueDefinitions, support, out var beforeIncrement)
            || !TryMatchAddWithInduction(
                beforeIncrement,
                loop.InductionValueName,
                loop.ValueDefinitions,
                support,
                out startLength))
        {
            supportValueNames = [];
            return false;
        }

        supportValueNames = support.ToArray();
        return true;
    }

    private static bool TryMatchAddOne(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> supportValueNames,
        out SsaValue other)
    {
        other = null!;
        if (value is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Add or SsaBinaryOperator.WrappingAdd
            } binary)
        {
            return false;
        }

        if (IsOneIntegerConstant(binary.Left))
        {
            supportValueNames.Add(reference.Name);
            other = binary.Right;
            return true;
        }

        if (IsOneIntegerConstant(binary.Right))
        {
            supportValueNames.Add(reference.Name);
            other = binary.Left;
            return true;
        }

        return false;
    }

    private static bool TryMatchAddWithInduction(
        SsaValue value,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> supportValueNames,
        out SsaValue other)
    {
        other = null!;
        if (value is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Add or SsaBinaryOperator.WrappingAdd
            } binary)
        {
            return false;
        }

        if (IsInductionValue(binary.Left, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal)))
        {
            supportValueNames.Add(reference.Name);
            AddInductionSupportValueNames(binary.Left, inductionValueName, definitions, supportValueNames);
            other = binary.Right;
            return true;
        }

        if (IsInductionValue(binary.Right, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal)))
        {
            supportValueNames.Add(reference.Name);
            AddInductionSupportValueNames(binary.Right, inductionValueName, definitions, supportValueNames);
            other = binary.Left;
            return true;
        }

        return false;
    }

    private static void AddInductionSupportValueNames(
        SsaValue value,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> supportValueNames)
    {
        if (value is not SsaValueReference reference
            || string.Equals(reference.Name, inductionValueName, StringComparison.Ordinal)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return;
        }

        supportValueNames.Add(reference.Name);
        switch (definition)
        {
            case SsaUseRValue use:
                AddInductionSupportValueNames(use.Value, inductionValueName, definitions, supportValueNames);
                break;
            case SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert):
                AddInductionSupportValueNames(convert.Operand, inductionValueName, definitions, supportValueNames);
                break;
            case SsaBinaryRValue binary when IsAddZero(binary, definitions):
                AddInductionSupportValueNames(binary.Left, inductionValueName, definitions, supportValueNames);
                AddInductionSupportValueNames(binary.Right, inductionValueName, definitions, supportValueNames);
                break;
        }
    }

    private static bool ValueReferencesBodyDefinition(SsaValue value, SsaBasicBlock body)
    {
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        return body.Phis.Any(phi => string.Equals(phi.ResultName, reference.Name, StringComparison.Ordinal))
            || body.Instructions.OfType<SsaValueInstruction>()
                .Any(instruction => string.Equals(instruction.ResultName, reference.Name, StringComparison.Ordinal));
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
        ISet<string> availableValueNames,
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
            || !TryResolveLoopAvailableBaseValue(elementAddress.Address, definitions, nonEntryValueNames, availableValueNames, out var resolvedBasePointer))
        {
            return false;
        }

        basePointer = resolvedBasePointer;
        elementType = rawElementType;
        addressResultName = addressReference.Name;
        return true;
    }

    private static bool TryMatchMemoryLoopIndexedElementAddress(
        SsaValue address,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        ISet<string> availableValueNames,
        bool requireMutablePointer,
        out SsaValue baseValue,
        out StarkTypeSymbol elementType,
        out string addressResultName,
        out bool baseIsSlice,
        out IReadOnlyList<string> supportValueNames)
    {
        if (TryMatchRawPointerIndexedElementAddress(
                address,
                inductionValueName,
                definitions,
                nonEntryValueNames,
                availableValueNames,
                requireMutablePointer,
                out baseValue,
                out elementType,
                out addressResultName))
        {
            baseIsSlice = false;
            supportValueNames = [];
            return true;
        }

        return TryMatchSliceIndexedElementAddress(
            address,
            inductionValueName,
            definitions,
            requireMutablePointer,
            out baseValue,
            out elementType,
            out addressResultName,
            out baseIsSlice,
            out supportValueNames);
    }

    private static bool TryMatchMemoryLoopLoad(
        SsaRValue loadDefinition,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        ISet<string> availableValueNames,
        out SsaValue sourceBase,
        out StarkTypeSymbol sourceElementType,
        out string sourceAddressName,
        out bool sourceBaseIsSlice,
        out IReadOnlyList<string> sourceSupportValueNames)
    {
        switch (loadDefinition)
        {
            case SsaLoadIndirectRValue load:
                return TryMatchMemoryLoopIndexedElementAddress(
                    load.Address,
                    inductionValueName,
                    definitions,
                    nonEntryValueNames,
                    availableValueNames,
                    requireMutablePointer: false,
                    out sourceBase,
                    out sourceElementType,
                    out sourceAddressName,
                    out sourceBaseIsSlice,
                    out sourceSupportValueNames);
            case SsaLoadSliceElementRValue loadSlice:
                return TryMatchSliceIndexedElement(
                    loadSlice.Slice,
                    loadSlice.Index,
                    inductionValueName,
                    definitions,
                    requireMutablePointer: false,
                    out sourceBase,
                    out sourceElementType,
                    out sourceBaseIsSlice,
                    out sourceSupportValueNames,
                    out sourceAddressName);
            default:
                sourceBase = null!;
                sourceElementType = StarkTypeSymbols.Error;
                sourceAddressName = string.Empty;
                sourceBaseIsSlice = false;
                sourceSupportValueNames = [];
                return false;
        }
    }

    private static bool TryMatchSliceIndexedElementAddress(
        SsaValue address,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        bool requireMutablePointer,
        out SsaValue baseSlice,
        out StarkTypeSymbol elementType,
        out string addressResultName,
        out bool baseIsSlice,
        out IReadOnlyList<string> supportValueNames)
    {
        baseSlice = null!;
        elementType = StarkTypeSymbols.Error;
        addressResultName = string.Empty;
        baseIsSlice = false;
        supportValueNames = [];

        if (address is not SsaValueReference addressReference
            || !definitions.TryGetValue(addressReference.Name, out var definition)
            || definition is not SsaSliceElementAddressRValue sliceElementAddress
            || !TryMatchSliceIndexedElement(
                sliceElementAddress.Slice,
                sliceElementAddress.Index,
                inductionValueName,
                definitions,
                requireMutablePointer,
                out baseSlice,
                out elementType,
                out baseIsSlice,
                out supportValueNames,
                out _))
        {
            return false;
        }

        addressResultName = addressReference.Name;
        return true;
    }

    private static bool TryMatchSliceIndexedElement(
        SsaValue slice,
        SsaValue index,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        bool requireMutablePointer,
        out SsaValue baseSlice,
        out StarkTypeSymbol elementType,
        out bool baseIsSlice,
        out IReadOnlyList<string> supportValueNames,
        out string addressResultName)
    {
        baseSlice = null!;
        elementType = StarkTypeSymbols.Error;
        baseIsSlice = false;
        supportValueNames = [];
        addressResultName = string.Empty;

        if (slice.Type.Kind != StarkTypeKind.Slice
            || slice.Type.ElementType is not { } sliceElementType
            || requireMutablePointer
                && !slice.Type.IsMutableView
                && slice.Type.InitializationKind == StarkInitializationKind.None
            || !IsInductionValue(index, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
            || !TryResolveLoopInvariantSliceBase(slice, definitions, out baseSlice, out supportValueNames))
        {
            return false;
        }

        elementType = sliceElementType;
        baseIsSlice = true;
        return true;
    }

    private static bool TryResolveLoopInvariantSliceBase(
        SsaValue slice,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue baseSlice,
        out IReadOnlyList<string> supportValueNames)
    {
        baseSlice = null!;
        supportValueNames = [];
        if (slice is not SsaValueReference sliceReference
            || !definitions.TryGetValue(sliceReference.Name, out var definition)
            || !TryMatchLoopInvariantSliceLoad(definition, definitions, out var addressSupportValueNames))
        {
            return false;
        }

        var supportNames = new List<string>(addressSupportValueNames.Count + 1) { sliceReference.Name };
        supportNames.AddRange(addressSupportValueNames);
        baseSlice = slice;
        supportValueNames = supportNames;
        return true;
    }

    private static bool TryMatchLoopInvariantSliceLoad(
        SsaRValue definition,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out IReadOnlyList<string> supportValueNames)
    {
        supportValueNames = [];
        switch (definition)
        {
            case SsaLoadIndirectRValue load
                when load.Type.Kind == StarkTypeKind.Slice
                     && TryMatchLoopInvariantSliceHeaderAddress(load.Address, definitions, out var addressSupportValueNames):
                supportValueNames = addressSupportValueNames;
                return true;
            case SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Slice }:
                return true;
            default:
                return false;
        }
    }

    private static bool TryMatchLoopInvariantSliceHeaderAddress(
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out IReadOnlyList<string> supportValueNames)
    {
        supportValueNames = [];
        if (address is not SsaValueReference addressReference
            || !definitions.TryGetValue(addressReference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaUseRValue use
                when TryMatchLoopInvariantSliceHeaderAddress(use.Value, definitions, out var nestedSupport):
            {
                var names = new List<string>(nestedSupport.Count + 1) { addressReference.Name };
                names.AddRange(nestedSupport);
                supportValueNames = names;
                return true;
            }
            case SsaAddressOfParameterRValue:
            case SsaAddressOfLocalRValue:
                supportValueNames = [addressReference.Name];
                return true;
            default:
                return false;
        }
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

    private static IReadOnlyDictionary<int, ISet<string>> BuildAvailableValueNamesByBlock(SsaFunction function)
    {
        var dominance = BlockDominanceIndex.Build(function);

        // A value is "available" at a block exactly when the block that defines it
        // dominates that block. Map each SSA value name to its defining block so per-block
        // availability can be served as an O(1)-query dominance view rather than a
        // materialized set. Materializing every block's set is O(blocks^2) time and memory
        // on large functions (e.g. a synthesized test-runner entry point with thousands of
        // blocks); the view keeps this analysis near-linear so it can always run.
        var valueDefinitionBlockId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var name in CollectBlockValueNames(block))
            {
                // SSA value names are unique, so first-definition-wins is exact.
                valueDefinitionBlockId.TryAdd(name, block.Id);
            }
        }

        var availableByBlock = new Dictionary<int, ISet<string>>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            availableByBlock[block.Id] = new DominatorAvailableValueNames(block.Id, valueDefinitionBlockId, dominance);
        }

        return availableByBlock;
    }

    /// <summary>
    /// Immediate-dominator index for a function's control-flow graph. Dominators are
    /// computed with the Cooper-Harvey-Kennedy "A Simple, Fast Dominance Algorithm"
    /// (near-linear in practice), then flattened into enter/exit timestamps from a walk of
    /// the dominator tree so that "does A dominate B" is an O(1) ancestor test. This
    /// replaces an earlier set-based iterative dataflow that was O(blocks^2)-O(blocks^3)
    /// and could hang on functions with thousands of basic blocks.
    /// </summary>
    private sealed class BlockDominanceIndex
    {
        private readonly IReadOnlyDictionary<int, int> _enter;
        private readonly IReadOnlyDictionary<int, int> _exit;

        private BlockDominanceIndex(IReadOnlyDictionary<int, int> enter, IReadOnlyDictionary<int, int> exit)
        {
            _enter = enter;
            _exit = exit;
        }

        public bool Dominates(int dominator, int block)
        {
            if (!_enter.TryGetValue(block, out var blockEnter))
            {
                // A block unreachable from entry is dominated only by itself, matching the
                // degenerate result the prior dataflow produced for isolated blocks.
                return dominator == block;
            }

            if (!_enter.TryGetValue(dominator, out var dominatorEnter))
            {
                // An unreachable block cannot dominate a reachable one.
                return false;
            }

            // In the dominator-tree Euler tour, A is an ancestor-or-self of B (i.e. A
            // dominates B) iff B's [enter, exit] interval nests inside A's.
            return dominatorEnter <= blockEnter && _exit[block] <= _exit[dominator];
        }

        public static BlockDominanceIndex Build(SsaFunction function)
        {
            var blocks = function.Blocks;
            var successors = new Dictionary<int, int[]>(blocks.Count);
            var predecessors = new Dictionary<int, List<int>>(blocks.Count);
            foreach (var block in blocks)
            {
                successors[block.Id] = block.Terminator.Targets as int[] ?? block.Terminator.Targets.ToArray();
                predecessors[block.Id] = new List<int>();
            }

            foreach (var block in blocks)
            {
                foreach (var target in successors[block.Id])
                {
                    if (predecessors.TryGetValue(target, out var targetPredecessors))
                    {
                        targetPredecessors.Add(block.Id);
                    }
                }
            }

            var entryId = function.EntryBlockId;

            // Postorder of the blocks reachable from entry, via iterative DFS so the very
            // deep block chains these large functions produce cannot overflow the stack.
            var postIndex = new Dictionary<int, int>(blocks.Count);
            var reversePostorder = new List<int>(blocks.Count);
            if (successors.ContainsKey(entryId))
            {
                var visited = new HashSet<int> { entryId };
                var dfs = new Stack<(int Block, int NextSuccessor)>();
                dfs.Push((entryId, 0));
                while (dfs.Count > 0)
                {
                    var (block, nextSuccessor) = dfs.Pop();
                    var blockSuccessors = successors.TryGetValue(block, out var found) ? found : [];
                    if (nextSuccessor < blockSuccessors.Length)
                    {
                        dfs.Push((block, nextSuccessor + 1));
                        var successor = blockSuccessors[nextSuccessor];
                        if (successors.ContainsKey(successor) && visited.Add(successor))
                        {
                            dfs.Push((successor, 0));
                        }
                    }
                    else
                    {
                        postIndex[block] = reversePostorder.Count;
                        reversePostorder.Add(block);
                    }
                }

                reversePostorder.Reverse();
            }

            // Cooper-Harvey-Kennedy immediate-dominator fixpoint over reverse postorder.
            var idom = new Dictionary<int, int> { [entryId] = entryId };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in reversePostorder)
                {
                    if (block == entryId)
                    {
                        continue;
                    }

                    var newImmediateDominator = -1;
                    foreach (var predecessor in predecessors[block])
                    {
                        if (!idom.ContainsKey(predecessor))
                        {
                            continue;
                        }

                        newImmediateDominator = newImmediateDominator == -1
                            ? predecessor
                            : Intersect(predecessor, newImmediateDominator, idom, postIndex);
                    }

                    if (newImmediateDominator != -1
                        && (!idom.TryGetValue(block, out var current) || current != newImmediateDominator))
                    {
                        idom[block] = newImmediateDominator;
                        changed = true;
                    }
                }
            }

            // Materialize the dominator tree's children, then assign enter/exit timestamps
            // with an iterative preorder walk so dominance becomes an O(1) interval test.
            var children = new Dictionary<int, List<int>>();
            foreach (var block in reversePostorder)
            {
                if (block == entryId || !idom.TryGetValue(block, out var parent))
                {
                    continue;
                }

                if (!children.TryGetValue(parent, out var siblings))
                {
                    siblings = new List<int>();
                    children[parent] = siblings;
                }

                siblings.Add(block);
            }

            var enter = new Dictionary<int, int>(reversePostorder.Count);
            var exit = new Dictionary<int, int>(reversePostorder.Count);
            if (reversePostorder.Count > 0)
            {
                var timer = 0;
                var tour = new Stack<(int Block, bool Exiting)>();
                tour.Push((entryId, false));
                while (tour.Count > 0)
                {
                    var (block, exiting) = tour.Pop();
                    if (exiting)
                    {
                        exit[block] = timer++;
                        continue;
                    }

                    enter[block] = timer++;
                    tour.Push((block, true));
                    if (children.TryGetValue(block, out var blockChildren))
                    {
                        foreach (var child in blockChildren)
                        {
                            tour.Push((child, false));
                        }
                    }
                }
            }

            return new BlockDominanceIndex(enter, exit);
        }

        private static int Intersect(
            int left,
            int right,
            IReadOnlyDictionary<int, int> idom,
            IReadOnlyDictionary<int, int> postIndex)
        {
            // Walk the two fingers up the dominator tree (lower postorder index == deeper)
            // until they meet at the common dominator.
            while (left != right)
            {
                while (postIndex[left] < postIndex[right])
                {
                    left = idom[left];
                }

                while (postIndex[right] < postIndex[left])
                {
                    right = idom[right];
                }
            }

            return left;
        }
    }

    /// <summary>
    /// Read-only view of the SSA value names available at one block: a name is available
    /// exactly when its defining block dominates this block. The loop matchers only ever
    /// call <see cref="Contains"/>, so the set is never materialized; the remaining
    /// <see cref="ISet{T}"/> members are intentionally unsupported and fail fast if a
    /// future caller depends on them.
    /// </summary>
    private sealed class DominatorAvailableValueNames : ISet<string>
    {
        private readonly int _blockId;
        private readonly IReadOnlyDictionary<string, int> _valueDefinitionBlockId;
        private readonly BlockDominanceIndex _dominance;

        public DominatorAvailableValueNames(
            int blockId,
            IReadOnlyDictionary<string, int> valueDefinitionBlockId,
            BlockDominanceIndex dominance)
        {
            _blockId = blockId;
            _valueDefinitionBlockId = valueDefinitionBlockId;
            _dominance = dominance;
        }

        public bool Contains(string item) =>
            _valueDefinitionBlockId.TryGetValue(item, out var definitionBlockId)
            && _dominance.Dominates(definitionBlockId, _blockId);

        public int Count => throw Unsupported();
        public bool IsReadOnly => true;
        bool ISet<string>.Add(string item) => throw Unsupported();
        void ICollection<string>.Add(string item) => throw Unsupported();
        public void Clear() => throw Unsupported();
        public void CopyTo(string[] array, int arrayIndex) => throw Unsupported();
        public bool Remove(string item) => throw Unsupported();
        public void ExceptWith(IEnumerable<string> other) => throw Unsupported();
        public void IntersectWith(IEnumerable<string> other) => throw Unsupported();
        public bool IsProperSubsetOf(IEnumerable<string> other) => throw Unsupported();
        public bool IsProperSupersetOf(IEnumerable<string> other) => throw Unsupported();
        public bool IsSubsetOf(IEnumerable<string> other) => throw Unsupported();
        public bool IsSupersetOf(IEnumerable<string> other) => throw Unsupported();
        public bool Overlaps(IEnumerable<string> other) => throw Unsupported();
        public bool SetEquals(IEnumerable<string> other) => throw Unsupported();
        public void SymmetricExceptWith(IEnumerable<string> other) => throw Unsupported();
        public void UnionWith(IEnumerable<string> other) => throw Unsupported();
        public IEnumerator<string> GetEnumerator() => throw Unsupported();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw Unsupported();

        private static NotSupportedException Unsupported() =>
            new("Dominance-backed availability view supports only Contains(name).");
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

    private static bool IsDecrementByOne(
        SsaRValue definition,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return definition is SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Subtract or SsaBinaryOperator.WrappingSubtract
            } binary
            && IsInductionValue(binary.Left, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
            && IsOneIntegerConstant(binary.Right);
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

                    if (definition is SsaConvertRValue convert
                        && CanPreserveIntegerRangeThroughConversion(convert)
                        && TryResolveEntryAvailableValue(convert.Operand, definitions, nonEntryValueNames, visitedValueNames, out _))
                    {
                        resolved = value;
                        return true;
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

    private static bool TryResolvePreheaderAvailableValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> loopLocalValueNames,
        out SsaValue resolved)
    {
        return TryResolvePreheaderAvailableValue(
            value,
            definitions,
            loopLocalValueNames,
            new HashSet<string>(StringComparer.Ordinal),
            out resolved);
    }

    private static bool TryResolvePreheaderAvailableValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> loopLocalValueNames,
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
                if (!visitedValueNames.Add(reference.Name)
                    || loopLocalValueNames.Contains(reference.Name))
                {
                    resolved = null!;
                    return false;
                }

                if (definitions.TryGetValue(reference.Name, out var definition))
                {
                    if (definition is SsaUseRValue use)
                    {
                        return TryResolvePreheaderAvailableValue(
                            use.Value,
                            definitions,
                            loopLocalValueNames,
                            visitedValueNames,
                            out resolved);
                    }

                    if (definition is SsaConvertRValue convert
                        && CanPreserveIntegerRangeThroughConversion(convert)
                        && TryResolvePreheaderAvailableValue(
                            convert.Operand,
                            definitions,
                            loopLocalValueNames,
                            visitedValueNames,
                            out _))
                    {
                        resolved = value;
                        return true;
                    }

                    if (definition is SsaBinaryRValue binary && IsAddZero(binary, definitions))
                    {
                        return TryResolvePreheaderAvailableValue(
                            IsZeroIntegerValue(binary.Left, definitions, new HashSet<string>(StringComparer.Ordinal))
                                ? binary.Right
                                : binary.Left,
                            definitions,
                            loopLocalValueNames,
                            visitedValueNames,
                            out resolved);
                    }
                }

                resolved = value;
                return true;
            default:
                resolved = null!;
                return false;
        }
    }

    private static bool TryResolveLoopAvailableBaseValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> nonEntryValueNames,
        ISet<string> availableValueNames,
        out SsaValue resolved)
    {
        if (value is SsaValueReference reference && availableValueNames.Contains(reference.Name))
        {
            resolved = value;
            return true;
        }

        return TryResolveEntryAvailableValue(value, definitions, nonEntryValueNames, out resolved);
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
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        return TryResolveParameterName(function, destinationBase, definitions, out var destinationParameter)
            && TryResolveParameterName(function, sourceBase, definitions, out var sourceParameter)
            && !string.Equals(destinationParameter, sourceParameter, StringComparison.Ordinal)
            && (ParametersHavePairwiseNoAliasProof(function, destinationParameter, sourceParameter)
                || (ParameterHasNoAliasProof(function, destinationParameter, parameterEffects)
                    && ParameterHasNoAliasProof(function, sourceParameter, parameterEffects)));
    }

    private static bool ParametersHavePairwiseNoAliasProof(
        SsaFunction function,
        string leftParameterName,
        string rightParameterName)
    {
        foreach (var group in function.DisjointParameterGroups ?? [])
        {
            if (group.HasSubregions)
            {
                continue;
            }

            var containsLeft = false;
            var containsRight = false;
            foreach (var parameterName in group.ParameterNames)
            {
                containsLeft |= string.Equals(parameterName, leftParameterName, StringComparison.Ordinal);
                containsRight |= string.Equals(parameterName, rightParameterName, StringComparison.Ordinal);
                if (containsLeft && containsRight)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolveParameterName(
        SsaFunction function,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
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

            if (definitions.TryGetValue(reference.Name, out var definition))
            {
                return TryResolveParameterName(function, definition, definitions, out parameterName);
            }
        }

        parameterName = string.Empty;
        return false;
    }

    private static bool TryResolveParameterName(
        SsaFunction function,
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string parameterName)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveParameterName(function, use.Value, definitions, out parameterName);
            case SsaConvertRValue convert:
                return TryResolveParameterName(function, convert.Operand, definitions, out parameterName);
            case SsaLoadLocalRValue loadLocal
                when TryResolveSingleStoredLocalValue(function, loadLocal.LocalName, out var storedValue):
                return TryResolveParameterName(function, storedValue, definitions, out parameterName);
            case SsaLoadIndirectRValue load:
                return TryResolveParameterNameFromAddress(function, load.Address, definitions, out parameterName);
            case SsaMakeSliceFromPointerRValue makeSlice:
                return TryResolveParameterName(function, makeSlice.Pointer, definitions, out parameterName);
            case SsaCallRValue { Arguments.Count: 1 } call
                when IsTextDataPointerFunctionName(call.FunctionName):
                return TryResolveParameterName(function, call.Arguments[0], definitions, out parameterName);
            case SsaElementAddressRValue elementAddress:
                return TryResolveParameterName(function, elementAddress.Address, definitions, out parameterName);
            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryResolveParameterName(function, sliceElementAddress.Slice, definitions, out parameterName);
            case SsaExtractFieldRValue extractField:
                return TryResolveParameterName(function, extractField.Target, definitions, out parameterName);
            default:
                parameterName = string.Empty;
                return false;
        }
    }

    private static bool IsTextDataPointerFunctionName(string functionName)
    {
        return string.Equals(functionName, "AsciiData", StringComparison.Ordinal)
            || string.Equals(functionName, "UnicodeData", StringComparison.Ordinal)
            || string.Equals(functionName, "System.Text.AsciiData", StringComparison.Ordinal)
            || string.Equals(functionName, "System.Text.UnicodeData", StringComparison.Ordinal)
            || string.Equals(functionName, "System_Text_AsciiData", StringComparison.Ordinal)
            || string.Equals(functionName, "System_Text_UnicodeData", StringComparison.Ordinal)
            || functionName.EndsWith(".AsciiData", StringComparison.Ordinal)
            || functionName.EndsWith(".UnicodeData", StringComparison.Ordinal)
            || functionName.EndsWith("_AsciiData", StringComparison.Ordinal)
            || functionName.EndsWith("_UnicodeData", StringComparison.Ordinal);
    }

    private static bool TryResolveParameterNameFromAddress(
        SsaFunction function,
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string parameterName)
    {
        switch (address)
        {
            case SsaValueReference reference when definitions.TryGetValue(reference.Name, out var definition):
                return TryResolveParameterNameFromAddress(function, definition, definitions, out parameterName);
            default:
                parameterName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveParameterNameFromAddress(
        SsaFunction function,
        SsaRValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string parameterName)
    {
        switch (address)
        {
            case SsaUseRValue use:
                return TryResolveParameterNameFromAddress(function, use.Value, definitions, out parameterName);
            case SsaAddressOfParameterRValue addressOfParameter:
                parameterName = addressOfParameter.ParameterName;
                return true;
            case SsaAddressOfLocalRValue addressOfLocal
                when TryResolveSingleStoredLocalValue(function, addressOfLocal.LocalName, out var storedValue):
                return TryResolveParameterName(function, storedValue, definitions, out parameterName);
            case SsaFieldAddressRValue fieldAddress:
                return TryResolveParameterNameFromAddress(function, fieldAddress.Address, definitions, out parameterName);
            case SsaElementAddressRValue elementAddress:
                return TryResolveParameterName(function, elementAddress.Address, definitions, out parameterName);
            default:
                parameterName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveSingleStoredLocalValue(
        SsaFunction function,
        string localName,
        out SsaValue value)
    {
        value = null!;
        var found = false;
        foreach (var store in function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>())
        {
            if (!string.Equals(store.LocalName, localName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                value = null!;
                return false;
            }

            value = store.Value;
            found = true;
        }

        return found;
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

    private static ISet<string> CollectBlockValueNames(SsaBasicBlock block)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phi in block.Phis)
        {
            names.Add(phi.ResultName);
        }

        foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
        {
            names.Add(instruction.ResultName);
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
        bool DestinationBaseIsSlice = false,
        bool SourceBaseIsSlice = false,
        int? PreheaderBlockId = null,
        int? ExitBlockId = null,
        IReadOnlyList<int>? SkippedBlockIds = null,
        DynamicLengthCommitPlan? DynamicLengthCommit = null);

    private sealed record RawPointerCopyLoopMatch(
        SsaValue DestinationBase,
        SsaValue SourceBase,
        StarkTypeSymbol ElementType,
        SourceLocation? Location,
        bool DestinationBaseIsSlice,
        bool SourceBaseIsSlice);

    private sealed record DynamicLengthCommitPlan(
        SsaValue LengthAddress,
        SsaValue StartLength,
        StarkTypeSymbol LengthType,
        IReadOnlyList<string> SupportValueNames);

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
        ISet<string> NonEntryValueNames,
        ISet<string> AvailableValueNames);
}
