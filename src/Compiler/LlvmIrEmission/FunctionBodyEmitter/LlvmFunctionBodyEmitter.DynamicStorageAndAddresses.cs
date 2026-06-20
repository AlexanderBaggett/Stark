using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitMakeSliceFromLocal(string result, SsaMakeSliceFromLocalRValue makeSlice)
    {
        EnsureLocalSlotExists(makeSlice.LocalName, makeSlice.SourceType);

        if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray
            || makeSlice.SourceType.ElementType is null
            || makeSlice.SourceType.FixedLength is not int fixedLength)
        {
            throw new UnsupportedBodyEmissionException($"Slice creation from '{makeSlice.SourceType.DisplayName}' is not supported.");
        }

        var slotName = GetLocalSlotPointer(makeSlice.LocalName);
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

    private void EmitDynamicStorageAllocation(string result, SsaDynamicStorageAllocationRValue allocation)
    {
        if (allocation.Type.Kind != StarkTypeKind.Dynamic
            || allocation.Type.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage allocation requires a dynamic type, but found '{allocation.Type.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage allocation requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var resultType = MapType(allocation.Type);
        var capacityI64 = EmitUnsignedIntegerAsI64(allocation.Capacity, "dynamic_capacity");
        var maxCount = GetMaximumDynamicStorageElementCount(elementLayout.SizeBytes);
        var isArenaAllocation = allocation.AllocationKind == DynamicStorageAllocationKind.Arena;
        if (TryGetIntegerValueRange(allocation.Capacity, out var capacityRange)
            && capacityRange.Min > BigInteger.Zero
            && capacityRange.Max <= new BigInteger(maxCount))
        {
            EmitDynamicStorageAllocationValue(result, resultType, capacityI64, elementLayout, isArenaAllocation);
            return;
        }

        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            if (isArenaAllocation)
            {
                AppendLine(
                    $"  {result} = call {resultType} @{ArenaDynamicStorageAllocateHelperName}(ptr nonnull {ArenaFrameSlotName}, i64 noundef {capacityI64}, {AllocatorSizeType} noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes).ToString(CultureInfo.InvariantCulture)}, i64 noundef {FormatUnsignedI64Constant(maxCount)})");
                return;
            }

            AppendLine(
                $"  {result} = call {resultType} @{DynamicStorageAllocateHelperName}(i64 noundef {capacityI64}, {AllocatorSizeType} noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes).ToString(CultureInfo.InvariantCulture)}, i64 noundef {FormatUnsignedI64Constant(maxCount)})");
            return;
        }

        var zeroLabel = EscapeIdentifier(CreateAbiTempName("dynamic_alloc_zero"));
        var checkLabel = EscapeIdentifier(CreateAbiTempName("dynamic_alloc_check"));
        var trapLabel = EscapeIdentifier(CreateAbiTempName("dynamic_alloc_overflow"));
        var allocateLabel = EscapeIdentifier(CreateAbiTempName("dynamic_alloc_allocate"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("dynamic_alloc_done"));
        var capacityIsZero = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_is_zero"))}";
        var allocatedValue = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_value"))}";
        var overflowProfile = _context.GetMetadataTupleRef([
            "!\"branch_weights\"",
            $"i32 {TrapEdgeUnlikelyWeight}",
            $"i32 {NormalEdgeLikelyWeight}"
        ]);

        AppendLine($"  {capacityIsZero} = icmp eq i64 {capacityI64}, 0");
        AppendLine($"  br i1 {capacityIsZero}, label %{zeroLabel}, label %{checkLabel}");
        AppendLine(string.Empty);
        AppendLine($"{zeroLabel}:");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{checkLabel}:");

        if (maxCount < long.MaxValue)
        {
            var tooLarge = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_too_large"))}";
            AppendLine($"  {tooLarge} = icmp ugt i64 {capacityI64}, {maxCount.ToString(CultureInfo.InvariantCulture)}");
            AppendLine($"  br i1 {tooLarge}, label %{trapLabel}, label %{allocateLabel}, !prof {overflowProfile}");
            AppendLine(string.Empty);
            AppendLine($"{trapLabel}:");
            AppendLine("  call void @llvm.trap()");
            AppendLine("  unreachable");
            AppendLine(string.Empty);
            AppendLine($"{allocateLabel}:");
        }
        else
        {
            AppendLine($"  br label %{allocateLabel}");
            AppendLine(string.Empty);
            AppendLine($"{allocateLabel}:");
        }

        EmitDynamicStorageAllocationValue(allocatedValue, resultType, capacityI64, elementLayout, isArenaAllocation);
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{doneLabel}:");
        AppendLine($"  {result} = phi {resultType} [ zeroinitializer, %{zeroLabel} ], [ {allocatedValue}, %{allocateLabel} ]");
        RecordCurrentBlockExitLabel(doneLabel);
    }

    private void EmitDynamicStorageRuntimeAllocationValue(
        string result,
        string resultType,
        string capacityI64,
        ConcreteTypeLayout elementLayout)
    {
        EmitDynamicStorageAllocationValue(result, resultType, capacityI64, elementLayout, useArena: false);
    }

    private void EmitDynamicStorageAllocationValue(
        string result,
        string resultType,
        string capacityI64,
        ConcreteTypeLayout elementLayout,
        bool useArena)
    {
        var allocatedPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_ptr"))}";
        var withPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_with_ptr"))}";
        var withLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_with_length"))}";
        var allocatorCount = EmitI64AsAllocatorSize(capacityI64, "dynamic_alloc_count");
        var byteLength = allocatorCount;
        if (elementLayout.SizeBytes != 1)
        {
            byteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_alloc_bytes"))}";
            AppendLine($"  {byteLength} = mul {AllocatorSizeType} {allocatorCount}, {elementLayout.SizeBytes}");
        }

        var alignmentBytes = Math.Max(1, elementLayout.AlignmentBytes);
        var allocationAttributes = alignmentBytes > 1
            ? $"noalias nonnull noundef align {alignmentBytes}"
            : "noalias nonnull noundef";
        var allocationArguments = useArena
            ? $"ptr nonnull {ArenaFrameSlotName}, {AllocatorSizeType} noundef {byteLength}, {AllocatorSizeType} noundef {alignmentBytes}"
            : $"{AllocatorSizeType} noundef {byteLength}, {AllocatorSizeType} noundef {alignmentBytes}";
        var allocatorName = useArena ? ArenaAllocateHelperName : RuntimeAllocateHelperName;
        AppendLine(
            $"  {allocatedPointer} = call {allocationAttributes} ptr @{allocatorName}({allocationArguments})");
        AppendLine($"  {withPointer} = insertvalue {resultType} zeroinitializer, ptr {allocatedPointer}, 0");
        AppendLine($"  {withLength} = insertvalue {resultType} {withPointer}, i64 0, 1");
        AppendLine($"  {result} = insertvalue {resultType} {withLength}, i64 {capacityI64}, 2");
    }

    private void EmitDynamicStorageFree(SsaDynamicStorageFreeRValue free)
    {
        if (free.Storage.Type.Kind != StarkTypeKind.Dynamic)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage free requires a dynamic value, but found '{free.Storage.Type.DisplayName}'.");
        }

        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_free_ptr"))}";
        AppendLine($"  {pointer} = extractvalue {MapType(NormalizeAggregateType(free.Storage.Type))} {FormatValue(free.Storage)}, 0");
        AppendLine($"  call void @{RuntimeFreeHelperName}(ptr {pointer})");
    }

    private void EmitHeapStorageFree(SsaHeapStorageFreeRValue free)
    {
        if (free.Pointer.Type.Kind != StarkTypeKind.RawPointer)
        {
            throw new UnsupportedBodyEmissionException(
                $"Heap storage free requires a raw pointer, but found '{free.Pointer.Type.DisplayName}'.");
        }

        AppendLine($"  call void @{HeapFreeHelperName}(ptr {FormatValue(free.Pointer)})");
    }

    private void EmitDynamicStorageReserve(SsaDynamicStorageReserveRValue reserve)
    {
        if (reserve.AllocationKind == DynamicStorageAllocationKind.Arena)
        {
            throw new UnsupportedBodyEmissionException("Arena-backed dynamic storage Reserve requires arena grow-copy lowering, which is not implemented yet.");
        }

        if (reserve.StorageType.Kind != StarkTypeKind.Dynamic
            || reserve.StorageType.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage reserve requires a dynamic type, but found '{reserve.StorageType.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage reserve requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var storageType = MapType(NormalizeAggregateType(reserve.StorageType));
        var storageAddress = FormatValue(reserve.StorageAddress);
        var additionalI64 = EmitUnsignedIntegerAsI64(reserve.AdditionalCapacity, "dynamic_reserve_additional");
        var maxCount = GetMaximumDynamicStorageElementCount(elementLayout.SizeBytes);
        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            AppendLine(
                $"  call void @{DynamicStorageReserveHelperName}(ptr {storageAddress}, i64 noundef {additionalI64}, {AllocatorSizeType} noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes).ToString(CultureInfo.InvariantCulture)}, i64 noundef {FormatUnsignedI64Constant(maxCount)})");
            return;
        }

        var current = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_current"))}";
        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_ptr"))}";
        var length = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_length"))}";
        var capacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_capacity"))}";
        var needed = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_needed"))}";
        var neededOverflow = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_needed_overflow"))}";
        var enoughCapacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_enough"))}";
        var doubled = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_doubled"))}";
        var canDouble = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_can_double"))}";
        var doubledOrMax = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_doubled_or_max"))}";
        var capacitySmall = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_capacity_small"))}";
        var minimumGrowth = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_minimum"))}";
        var neededLarger = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_needed_larger"))}";
        var newCapacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_new_capacity"))}";
        var checkLabel = EscapeIdentifier(CreateAbiTempName("dynamic_reserve_check"));
        var trapLabel = EscapeIdentifier(CreateAbiTempName("dynamic_reserve_overflow"));
        var growLabel = EscapeIdentifier(CreateAbiTempName("dynamic_reserve_grow"));
        var reallocLabel = EscapeIdentifier(CreateAbiTempName("dynamic_reserve_realloc"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("dynamic_reserve_done"));
        var overflowProfile = _context.GetMetadataTupleRef([
            "!\"branch_weights\"",
            $"i32 {TrapEdgeUnlikelyWeight}",
            $"i32 {NormalEdgeLikelyWeight}"
        ]);

        AppendLine($"  {current} = load {storageType}, ptr {storageAddress}");
        AppendLine($"  {pointer} = extractvalue {storageType} {current}, 0");
        AppendLine($"  {length} = extractvalue {storageType} {current}, 1");
        AppendLine($"  {capacity} = extractvalue {storageType} {current}, 2");
        AppendLine($"  {needed} = add i64 {length}, {additionalI64}");
        AppendLine($"  {neededOverflow} = icmp ult i64 {needed}, {length}");
        AppendLine($"  br i1 {neededOverflow}, label %{trapLabel}, label %{checkLabel}, !prof {overflowProfile}");
        AppendLine(string.Empty);
        AppendLine($"{trapLabel}:");
        AppendLine("  call void @llvm.trap()");
        AppendLine("  unreachable");
        AppendLine(string.Empty);
        AppendLine($"{checkLabel}:");
        AppendLine($"  {enoughCapacity} = icmp ule i64 {needed}, {capacity}");
        AppendLine($"  br i1 {enoughCapacity}, label %{doneLabel}, label %{growLabel}");
        AppendLine(string.Empty);
        AppendLine($"{growLabel}:");
        AppendLine($"  {doubled} = shl i64 {capacity}, 1");
        AppendLine($"  {canDouble} = icmp ule i64 {capacity}, 9223372036854775807");
        AppendLine($"  {doubledOrMax} = select i1 {canDouble}, i64 {doubled}, i64 -1");
        AppendLine($"  {capacitySmall} = icmp ult i64 {capacity}, 2");
        AppendLine($"  {minimumGrowth} = select i1 {capacitySmall}, i64 4, i64 {doubledOrMax}");
        AppendLine($"  {neededLarger} = icmp ugt i64 {needed}, {minimumGrowth}");
        AppendLine($"  {newCapacity} = select i1 {neededLarger}, i64 {needed}, i64 {minimumGrowth}");

        if (maxCount < ulong.MaxValue)
        {
            var tooLarge = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_too_large"))}";
            AppendLine($"  {tooLarge} = icmp ugt i64 {newCapacity}, {maxCount.ToString(CultureInfo.InvariantCulture)}");
            AppendLine($"  br i1 {tooLarge}, label %{trapLabel}, label %{reallocLabel}, !prof {overflowProfile}");
            AppendLine(string.Empty);
            AppendLine($"{reallocLabel}:");
        }

        var oldAllocatorCount = EmitI64AsAllocatorSize(capacity, "dynamic_reserve_old_count");
        var newAllocatorCount = EmitI64AsAllocatorSize(newCapacity, "dynamic_reserve_new_count");
        var oldByteLength = oldAllocatorCount;
        var newByteLength = newAllocatorCount;
        if (elementLayout.SizeBytes != 1)
        {
            oldByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_old_bytes"))}";
            newByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_new_bytes"))}";
            AppendLine($"  {oldByteLength} = mul {AllocatorSizeType} {oldAllocatorCount}, {elementLayout.SizeBytes}");
            AppendLine($"  {newByteLength} = mul {AllocatorSizeType} {newAllocatorCount}, {elementLayout.SizeBytes}");
        }

        var newPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_new_ptr"))}";
        var withPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_with_ptr"))}";
        var updated = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_reserve_updated"))}";
        AppendLine(
            $"  {newPointer} = call nonnull noundef ptr @{RuntimeReallocateHelperName}(ptr {pointer}, {AllocatorSizeType} noundef {oldByteLength}, {AllocatorSizeType} noundef {newByteLength}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes)})");
        AppendLine($"  {withPointer} = insertvalue {storageType} {current}, ptr {newPointer}, 0");
        AppendLine($"  {updated} = insertvalue {storageType} {withPointer}, i64 {newCapacity}, 2");
        AppendLine($"  store {storageType} {updated}, ptr {storageAddress}");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{doneLabel}:");
        RecordCurrentBlockExitLabel(doneLabel);
    }

    private void EmitDynamicStorageTryReserve(string result, SsaDynamicStorageTryReserveRValue reserve)
    {
        if (reserve.AllocationKind == DynamicStorageAllocationKind.Arena)
        {
            throw new UnsupportedBodyEmissionException("Arena-backed dynamic storage TryReserve requires fallible arena grow-copy lowering, which is not implemented yet.");
        }

        if (reserve.StorageType.Kind != StarkTypeKind.Dynamic
            || reserve.StorageType.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage TryReserve requires a dynamic type, but found '{reserve.StorageType.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage TryReserve requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var storageType = MapType(NormalizeAggregateType(reserve.StorageType));
        var storageAddress = FormatValue(reserve.StorageAddress);
        var additionalI64 = EmitUnsignedIntegerAsI64(reserve.AdditionalCapacity, "dynamic_try_reserve_additional");
        var maxCount = GetMaximumDynamicStorageElementCount(elementLayout.SizeBytes);
        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            AppendLine(
                $"  {result} = call i1 @{DynamicStorageTryReserveHelperName}(ptr {storageAddress}, i64 noundef {additionalI64}, {AllocatorSizeType} noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes).ToString(CultureInfo.InvariantCulture)}, i64 noundef {FormatUnsignedI64Constant(maxCount)})");
            return;
        }

        var current = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_current"))}";
        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_ptr"))}";
        var length = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_length"))}";
        var capacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity"))}";
        var needed = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_needed"))}";
        var neededOverflow = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_needed_overflow"))}";
        var enoughCapacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_enough"))}";
        var doubled = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_doubled"))}";
        var canDouble = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_can_double"))}";
        var doubledOrMax = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_doubled_or_max"))}";
        var capacitySmall = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_small"))}";
        var minimumGrowth = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_minimum"))}";
        var neededLarger = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_needed_larger"))}";
        var newCapacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_new_capacity"))}";
        var nullPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_null"))}";
        var checkLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_check"));
        var failLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_failed"));
        var growLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_grow"));
        var reallocLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_realloc"));
        var updateLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_update"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_done"));
        var failureProfile = _context.GetMetadataTupleRef([
            "!\"branch_weights\"",
            $"i32 {TrapEdgeUnlikelyWeight}",
            $"i32 {NormalEdgeLikelyWeight}"
        ]);

        AppendLine($"  {current} = load {storageType}, ptr {storageAddress}");
        AppendLine($"  {pointer} = extractvalue {storageType} {current}, 0");
        AppendLine($"  {length} = extractvalue {storageType} {current}, 1");
        AppendLine($"  {capacity} = extractvalue {storageType} {current}, 2");
        AppendLine($"  {needed} = add i64 {length}, {additionalI64}");
        AppendLine($"  {neededOverflow} = icmp ult i64 {needed}, {length}");
        AppendLine($"  br i1 {neededOverflow}, label %{failLabel}, label %{checkLabel}, !prof {failureProfile}");
        AppendLine(string.Empty);
        AppendLine($"{checkLabel}:");
        AppendLine($"  {enoughCapacity} = icmp ule i64 {needed}, {capacity}");
        AppendLine($"  br i1 {enoughCapacity}, label %{doneLabel}, label %{growLabel}");
        AppendLine(string.Empty);
        AppendLine($"{growLabel}:");
        AppendLine($"  {doubled} = shl i64 {capacity}, 1");
        AppendLine($"  {canDouble} = icmp ule i64 {capacity}, 9223372036854775807");
        AppendLine($"  {doubledOrMax} = select i1 {canDouble}, i64 {doubled}, i64 -1");
        AppendLine($"  {capacitySmall} = icmp ult i64 {capacity}, 2");
        AppendLine($"  {minimumGrowth} = select i1 {capacitySmall}, i64 4, i64 {doubledOrMax}");
        AppendLine($"  {neededLarger} = icmp ugt i64 {needed}, {minimumGrowth}");
        AppendLine($"  {newCapacity} = select i1 {neededLarger}, i64 {needed}, i64 {minimumGrowth}");

        if (maxCount < ulong.MaxValue)
        {
            var tooLarge = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_too_large"))}";
            AppendLine($"  {tooLarge} = icmp ugt i64 {newCapacity}, {maxCount.ToString(CultureInfo.InvariantCulture)}");
            AppendLine($"  br i1 {tooLarge}, label %{failLabel}, label %{reallocLabel}, !prof {failureProfile}");
            AppendLine(string.Empty);
            AppendLine($"{reallocLabel}:");
        }

        var oldAllocatorCount = EmitI64AsAllocatorSize(capacity, "dynamic_try_reserve_old_count");
        var newAllocatorCount = EmitI64AsAllocatorSize(newCapacity, "dynamic_try_reserve_new_count");
        var oldByteLength = oldAllocatorCount;
        var newByteLength = newAllocatorCount;
        if (elementLayout.SizeBytes != 1)
        {
            oldByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_old_bytes"))}";
            newByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_new_bytes"))}";
            AppendLine($"  {oldByteLength} = mul {AllocatorSizeType} {oldAllocatorCount}, {elementLayout.SizeBytes}");
            AppendLine($"  {newByteLength} = mul {AllocatorSizeType} {newAllocatorCount}, {elementLayout.SizeBytes}");
        }

        var newPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_new_ptr"))}";
        var withPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_with_ptr"))}";
        var updated = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_updated"))}";
        AppendLine(
            $"  {newPointer} = call ptr @{RuntimeTryReallocateHelperName}(ptr {pointer}, {AllocatorSizeType} noundef {oldByteLength}, {AllocatorSizeType} noundef {newByteLength}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes)})");
        AppendLine($"  {nullPointer} = icmp eq ptr {newPointer}, null");
        AppendLine($"  br i1 {nullPointer}, label %{failLabel}, label %{updateLabel}, !prof {failureProfile}");
        AppendLine(string.Empty);
        AppendLine($"{updateLabel}:");
        AppendLine($"  {withPointer} = insertvalue {storageType} {current}, ptr {newPointer}, 0");
        AppendLine($"  {updated} = insertvalue {storageType} {withPointer}, i64 {newCapacity}, 2");
        AppendLine($"  store {storageType} {updated}, ptr {storageAddress}");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{failLabel}:");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{doneLabel}:");
        AppendLine($"  {result} = phi i1 [ true, %{checkLabel} ], [ true, %{updateLabel} ], [ false, %{failLabel} ]");
        RecordCurrentBlockExitLabel(doneLabel);
    }

    private void EmitDynamicStorageTryReserveCapacity(string result, SsaDynamicStorageTryReserveCapacityRValue reserve)
    {
        if (reserve.AllocationKind == DynamicStorageAllocationKind.Arena)
        {
            throw new UnsupportedBodyEmissionException("Arena-backed dynamic storage TryReserveCapacity requires fallible arena grow-copy lowering, which is not implemented yet.");
        }

        if (reserve.StorageType.Kind != StarkTypeKind.Dynamic
            || reserve.StorageType.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage TryReserveCapacity requires a dynamic type, but found '{reserve.StorageType.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage TryReserveCapacity requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var storageType = MapType(NormalizeAggregateType(reserve.StorageType));
        var storageAddress = FormatValue(reserve.StorageAddress);
        var targetCapacityI64 = EmitUnsignedIntegerAsI64(reserve.TargetCapacity, "dynamic_try_reserve_capacity_target");
        var maxCount = GetMaximumDynamicStorageElementCount(elementLayout.SizeBytes);
        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            AppendLine(
                $"  {result} = call i1 @{DynamicStorageTryReserveCapacityHelperName}(ptr {storageAddress}, i64 noundef {targetCapacityI64}, {AllocatorSizeType} noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes).ToString(CultureInfo.InvariantCulture)}, i64 noundef {FormatUnsignedI64Constant(maxCount)})");
            return;
        }

        var current = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_current"))}";
        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_ptr"))}";
        var capacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_capacity"))}";
        var enoughCapacity = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_enough"))}";
        var nullPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_null"))}";
        var checkLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_check"));
        var failLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_failed"));
        var reallocLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_realloc"));
        var updateLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_update"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_done"));
        var failureProfile = _context.GetMetadataTupleRef([
            "!\"branch_weights\"",
            $"i32 {TrapEdgeUnlikelyWeight}",
            $"i32 {NormalEdgeLikelyWeight}"
        ]);

        AppendLine($"  {current} = load {storageType}, ptr {storageAddress}");
        AppendLine($"  {pointer} = extractvalue {storageType} {current}, 0");
        AppendLine($"  {capacity} = extractvalue {storageType} {current}, 2");
        AppendLine($"  br label %{checkLabel}");
        AppendLine(string.Empty);
        AppendLine($"{checkLabel}:");
        AppendLine($"  {enoughCapacity} = icmp ule i64 {targetCapacityI64}, {capacity}");
        AppendLine($"  br i1 {enoughCapacity}, label %{doneLabel}, label %{reallocLabel}");
        AppendLine(string.Empty);
        AppendLine($"{reallocLabel}:");

        if (maxCount < ulong.MaxValue)
        {
            var tooLarge = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_too_large"))}";
            AppendLine($"  {tooLarge} = icmp ugt i64 {targetCapacityI64}, {maxCount.ToString(CultureInfo.InvariantCulture)}");
            var growLabel = EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_grow"));
            AppendLine($"  br i1 {tooLarge}, label %{failLabel}, label %{growLabel}, !prof {failureProfile}");
            AppendLine(string.Empty);
            AppendLine($"{growLabel}:");
        }

        var oldAllocatorCount = EmitI64AsAllocatorSize(capacity, "dynamic_try_reserve_capacity_old_count");
        var newAllocatorCount = EmitI64AsAllocatorSize(targetCapacityI64, "dynamic_try_reserve_capacity_new_count");
        var oldByteLength = oldAllocatorCount;
        var newByteLength = newAllocatorCount;
        if (elementLayout.SizeBytes != 1)
        {
            oldByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_old_bytes"))}";
            newByteLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_new_bytes"))}";
            AppendLine($"  {oldByteLength} = mul {AllocatorSizeType} {oldAllocatorCount}, {elementLayout.SizeBytes}");
            AppendLine($"  {newByteLength} = mul {AllocatorSizeType} {newAllocatorCount}, {elementLayout.SizeBytes}");
        }

        var newPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_new_ptr"))}";
        var withPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_with_ptr"))}";
        var updated = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_try_reserve_capacity_updated"))}";
        AppendLine(
            $"  {newPointer} = call ptr @{RuntimeTryReallocateHelperName}(ptr {pointer}, {AllocatorSizeType} noundef {oldByteLength}, {AllocatorSizeType} noundef {newByteLength}, {AllocatorSizeType} noundef {Math.Max(1, elementLayout.AlignmentBytes)})");
        AppendLine($"  {nullPointer} = icmp eq ptr {newPointer}, null");
        AppendLine($"  br i1 {nullPointer}, label %{failLabel}, label %{updateLabel}, !prof {failureProfile}");
        AppendLine(string.Empty);
        AppendLine($"{updateLabel}:");
        AppendLine($"  {withPointer} = insertvalue {storageType} {current}, ptr {newPointer}, 0");
        AppendLine($"  {updated} = insertvalue {storageType} {withPointer}, i64 {targetCapacityI64}, 2");
        AppendLine($"  store {storageType} {updated}, ptr {storageAddress}");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{failLabel}:");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{doneLabel}:");
        AppendLine($"  {result} = phi i1 [ true, %{checkLabel} ], [ true, %{updateLabel} ], [ false, %{failLabel} ]");
        RecordCurrentBlockExitLabel(doneLabel);
    }

    private void EmitDynamicStorageMoveLast(string resultName, string result, SsaDynamicStorageMoveLastRValue moveLast)
    {
        if (moveLast.StorageType.Kind != StarkTypeKind.Dynamic
            || moveLast.StorageType.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveLast requires a dynamic type, but found '{moveLast.StorageType.DisplayName}'.");
        }

        if (NormalizeAggregateType(elementType) != NormalizeAggregateType(moveLast.Type))
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveLast result type '{moveLast.Type.DisplayName}' does not match element type '{elementType.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveLast requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var storageType = MapType(NormalizeAggregateType(moveLast.StorageType));
        var elementLlvmType = MapType(moveLast.Type);
        var storageAddress = FormatValue(moveLast.StorageAddress);
        var elementAlignment = Math.Max(1, elementLayout.AlignmentBytes);
        var useAddressBackedResult = TryPrepareLargeDynamicMoveResultSlot(
            resultName,
            moveLast.Type,
            elementLayout,
            "dynamic_move_result_slot",
            out var resultSlot,
            out var resultAlignmentBytes);
        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            var elementPointerFallback = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_ptr_fallback"))}";
            AppendLine(
                $"  {elementPointerFallback} = call nonnull ptr @{DynamicStorageMoveLastPointerHelperName}(ptr {storageAddress}, i64 noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)})");
            if (useAddressBackedResult)
            {
                EmitAggregateAddressCopy(
                    resultSlot,
                    elementPointerFallback,
                    moveLast.Type,
                    resultAlignmentBytes,
                    elementAlignment);
                return;
            }

            AppendLine($"  {result} = load {elementLlvmType}, ptr {elementPointerFallback}, align {elementAlignment}");
            return;
        }

        var current = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_current"))}";
        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_ptr"))}";
        var length = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_length"))}";
        var isEmpty = moveLast.IsKnownNonEmpty
            ? string.Empty
            : $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_is_empty"))}";
        var newLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_new_length"))}";
        var elementPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_element_ptr"))}";
        var movedValue = useAddressBackedResult
            ? string.Empty
            : moveLast.IsKnownNonEmpty
                ? result
                : $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_value"))}";
        var updated = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_updated"))}";
        var trapLabel = moveLast.IsKnownNonEmpty ? string.Empty : EscapeIdentifier(CreateAbiTempName("dynamic_move_empty"));
        var moveLabel = moveLast.IsKnownNonEmpty ? string.Empty : EscapeIdentifier(CreateAbiTempName("dynamic_move_do"));
        var doneLabel = moveLast.IsKnownNonEmpty ? string.Empty : EscapeIdentifier(CreateAbiTempName("dynamic_move_done"));
        var emptyProfile = moveLast.IsKnownNonEmpty
            ? string.Empty
            : _context.GetMetadataTupleRef([
                "!\"branch_weights\"",
                $"i32 {TrapEdgeUnlikelyWeight}",
                $"i32 {NormalEdgeLikelyWeight}"
            ]);

        AppendLine($"  {current} = load {storageType}, ptr {storageAddress}");
        AppendLine($"  {length} = extractvalue {storageType} {current}, 1");
        if (!moveLast.IsKnownNonEmpty)
        {
            AppendLine($"  {isEmpty} = icmp eq i64 {length}, 0");
            AppendLine($"  br i1 {isEmpty}, label %{trapLabel}, label %{moveLabel}, !prof {emptyProfile}");
            AppendLine(string.Empty);
            AppendLine($"{trapLabel}:");
            AppendLine("  call void @llvm.trap()");
            AppendLine("  unreachable");
            AppendLine(string.Empty);
            AppendLine($"{moveLabel}:");
        }

        AppendLine($"  {newLength} = sub i64 {length}, 1");
        AppendLine($"  {pointer} = extractvalue {storageType} {current}, 0");
        AppendLine($"  {elementPointer} = getelementptr{GetProvenInObjectGepFlags()} {elementLlvmType}, ptr {pointer}, i64 {newLength}");
        if (useAddressBackedResult)
        {
            EmitAggregateAddressCopy(
                resultSlot,
                elementPointer,
                moveLast.Type,
                resultAlignmentBytes,
                elementAlignment);
        }
        else
        {
            AppendLine($"  {movedValue} = load {elementLlvmType}, ptr {elementPointer}, align {elementAlignment}");
        }

        AppendLine($"  {updated} = insertvalue {storageType} {current}, i64 {newLength}, 1");
        AppendLine($"  store {storageType} {updated}, ptr {storageAddress}");
        if (!moveLast.IsKnownNonEmpty)
        {
            AppendLine($"  br label %{doneLabel}");
            AppendLine(string.Empty);
            AppendLine($"{doneLabel}:");
            if (!useAddressBackedResult)
            {
                AppendLine($"  {result} = phi {elementLlvmType} [ {movedValue}, %{moveLabel} ]");
            }

            RecordCurrentBlockExitLabel(doneLabel);
        }
    }

    private void EmitDynamicStorageMoveAt(string resultName, string result, SsaDynamicStorageMoveAtRValue moveAt)
    {
        if (moveAt.StorageType.Kind != StarkTypeKind.Dynamic
            || moveAt.StorageType.ElementType is not { } elementType)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveAt requires a dynamic type, but found '{moveAt.StorageType.DisplayName}'.");
        }

        if (NormalizeAggregateType(elementType) != NormalizeAggregateType(moveAt.Type))
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveAt result type '{moveAt.Type.DisplayName}' does not match element type '{elementType.DisplayName}'.");
        }

        if (TryGetConcreteTypeLayout(NormalizeAggregateType(elementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage MoveAt requires a concrete element layout for '{elementType.DisplayName}'.");
        }

        var storageType = MapType(NormalizeAggregateType(moveAt.StorageType));
        var elementLlvmType = MapType(moveAt.Type);
        var storageAddress = FormatValue(moveAt.StorageAddress);
        var indexI64 = EmitUnsignedIntegerAsI64(moveAt.Index, "dynamic_move_at_index");
        var elementAlignment = Math.Max(1, elementLayout.AlignmentBytes);
        var useAddressBackedResult = TryPrepareLargeDynamicMoveResultSlot(
            resultName,
            moveAt.Type,
            elementLayout,
            "dynamic_move_at_result_slot",
            out var resultSlot,
            out var resultAlignmentBytes);
        if (!CanSplitCurrentBlockForCallSiteControlFlow())
        {
            var outSlot = resultSlot;
            if (!useAddressBackedResult)
            {
                outSlot = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_slot"))}";
                _entryStaticAllocas.Add($"  {outSlot} = alloca {elementLlvmType}, align {elementAlignment}");
            }

            AppendLine(
                $"  call void @{DynamicStorageMoveAtToOutHelperName}(ptr {storageAddress}, i64 noundef {indexI64}, ptr {outSlot}, i64 noundef {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)})");
            if (!useAddressBackedResult)
            {
                AppendLine($"  {result} = load {elementLlvmType}, ptr {outSlot}, align {elementAlignment}");
            }

            return;
        }

        var current = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_current"))}";
        var pointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_ptr"))}";
        var length = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_length"))}";
        var inBounds = moveAt.IsKnownInBounds
            ? string.Empty
            : $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_in_bounds"))}";
        var newLength = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_new_length"))}";
        var elementPointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_element_ptr"))}";
        var movedValue = useAddressBackedResult
            ? string.Empty
            : $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_value"))}";
        var hasTail = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_has_tail"))}";
        var nextIndex = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_next_index"))}";
        var sourcePointer = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_source_ptr"))}";
        var tailCount = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_tail_count"))}";
        var tailBytes = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_tail_bytes"))}";
        var updated = $"%{EscapeIdentifier(CreateAbiTempName("dynamic_move_at_updated"))}";
        var trapLabel = moveAt.IsKnownInBounds ? string.Empty : EscapeIdentifier(CreateAbiTempName("dynamic_move_at_bad_index"));
        var moveLabel = moveAt.IsKnownInBounds ? string.Empty : EscapeIdentifier(CreateAbiTempName("dynamic_move_at_do"));
        var shiftLabel = EscapeIdentifier(CreateAbiTempName("dynamic_move_at_shift"));
        var updateLabel = EscapeIdentifier(CreateAbiTempName("dynamic_move_at_update"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("dynamic_move_at_done"));
        var boundsProfile = moveAt.IsKnownInBounds
            ? string.Empty
            : _context.GetMetadataTupleRef([
                "!\"branch_weights\"",
                $"i32 {NormalEdgeLikelyWeight}",
                $"i32 {TrapEdgeUnlikelyWeight}"
            ]);

        AppendLine($"  {current} = load {storageType}, ptr {storageAddress}");
        AppendLine($"  {length} = extractvalue {storageType} {current}, 1");
        if (!moveAt.IsKnownInBounds)
        {
            AppendLine($"  {inBounds} = icmp ult i64 {indexI64}, {length}");
            AppendLine($"  br i1 {inBounds}, label %{moveLabel}, label %{trapLabel}, !prof {boundsProfile}");
            AppendLine(string.Empty);
            AppendLine($"{trapLabel}:");
            AppendLine("  call void @llvm.trap()");
            AppendLine("  unreachable");
            AppendLine(string.Empty);
            AppendLine($"{moveLabel}:");
        }

        AppendLine($"  {newLength} = sub i64 {length}, 1");
        AppendLine($"  {pointer} = extractvalue {storageType} {current}, 0");
        AppendLine($"  {elementPointer} = getelementptr{GetProvenInObjectGepFlags()} {elementLlvmType}, ptr {pointer}, i64 {indexI64}");
        if (useAddressBackedResult)
        {
            EmitAggregateAddressCopy(
                resultSlot,
                elementPointer,
                moveAt.Type,
                resultAlignmentBytes,
                elementAlignment);
        }
        else
        {
            AppendLine($"  {movedValue} = load {elementLlvmType}, ptr {elementPointer}, align {elementAlignment}");
        }

        AppendLine($"  {hasTail} = icmp ult i64 {indexI64}, {newLength}");
        AppendLine($"  br i1 {hasTail}, label %{shiftLabel}, label %{updateLabel}");
        AppendLine(string.Empty);
        AppendLine($"{shiftLabel}:");
        AppendLine($"  {nextIndex} = add i64 {indexI64}, 1");
        AppendLine($"  {sourcePointer} = getelementptr{GetProvenInObjectGepFlags()} {elementLlvmType}, ptr {pointer}, i64 {nextIndex}");
        AppendLine($"  {tailCount} = sub i64 {newLength}, {indexI64}");
        AppendLine($"  {tailBytes} = mul i64 {tailCount}, {elementLayout.SizeBytes.ToString(CultureInfo.InvariantCulture)}");
        AppendLine($"  call void @llvm.memmove.p0.p0.i64(ptr align {elementAlignment} {elementPointer}, ptr align {elementAlignment} {sourcePointer}, i64 {tailBytes}, i1 false)");
        AppendLine($"  br label %{updateLabel}");
        AppendLine(string.Empty);
        AppendLine($"{updateLabel}:");
        AppendLine($"  {updated} = insertvalue {storageType} {current}, i64 {newLength}, 1");
        AppendLine($"  store {storageType} {updated}, ptr {storageAddress}");
        AppendLine($"  br label %{doneLabel}");
        AppendLine(string.Empty);
        AppendLine($"{doneLabel}:");
        if (!useAddressBackedResult)
        {
            AppendLine($"  {result} = phi {elementLlvmType} [ {movedValue}, %{updateLabel} ]");
        }

        RecordCurrentBlockExitLabel(doneLabel);
    }

    private bool TryPrepareLargeDynamicMoveResultSlot(
        string resultName,
        StarkTypeSymbol resultType,
        ConcreteTypeLayout resultLayout,
        string slotPurpose,
        out string resultSlot,
        out int resultAlignmentBytes)
    {
        resultAlignmentBytes = Math.Max(1, resultLayout.AlignmentBytes);
        resultSlot = string.Empty;

        if (!ShouldPreferAddressBasedAggregateLowering(resultType)
            || resultLayout.SizeBytes <= AggregateMemcpyThresholdBytes)
        {
            return false;
        }

        if (TryGetCurrentReturnBufferForwardingAddress(resultName, resultType, out resultSlot))
        {
            resultAlignmentBytes = GetStackObjectAlignmentBytes(resultType) ?? resultAlignmentBytes;
        }
        else if (TryGetImmediateAggregateForwardingAddress(
                     resultName,
                     resultType,
                     out resultSlot,
                     out var localAlignmentBytes))
        {
            resultAlignmentBytes = localAlignmentBytes ?? resultAlignmentBytes;
        }
        else
        {
            if (RequiresAggregateValueMaterialization(resultName, resultType))
            {
                return false;
            }

            resultSlot = $"%{EscapeIdentifier(CreateAbiTempName(slotPurpose))}";
            QueueStaticAlloca(resultSlot, resultType);
        }

        _indirectAggregateValueSlots[resultName] = resultSlot;
        return true;
    }

    private string EmitUnsignedIntegerAsI64(SsaValue value, string purpose)
    {
        if (value.Type.Kind != StarkTypeKind.Integer || value.Type.BitWidth is not int bitWidth)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage capacity must be an integer, but found '{value.Type.DisplayName}'.");
        }

        if (bitWidth == 64)
        {
            return FormatValue(value);
        }

        if (bitWidth is <= 0 or > 64)
        {
            throw new UnsupportedBodyEmissionException(
                $"Dynamic storage capacity width '{bitWidth}' is not supported.");
        }

        var converted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        AppendLine($"  {converted} = zext {MapType(value.Type)} {FormatValue(value)} to i64");
        return converted;
    }

    private string EmitI64AsAllocatorSize(string value, string purpose)
    {
        return AllocatorSizeType switch
        {
            "i64" => value,
            "i32" => EmitTruncatedAllocatorSize(value, purpose),
            _ => throw new UnsupportedBodyEmissionException(
                $"Allocator size type '{AllocatorSizeType}' is not supported for dynamic storage.")
        };
    }

    private string EmitTruncatedAllocatorSize(string value, string purpose)
    {
        var converted = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        AppendLine($"  {converted} = trunc i64 {value} to {AllocatorSizeType}");
        return converted;
    }

    private ulong GetMaximumDynamicStorageElementCount(int elementSizeBytes)
    {
        var maxAllocatorSize = AllocatorSizeType switch
        {
            "i64" => ulong.MaxValue,
            "i32" => uint.MaxValue,
            _ => throw new UnsupportedBodyEmissionException(
                $"Allocator size type '{AllocatorSizeType}' is not supported for dynamic storage.")
        };

        return maxAllocatorSize / (ulong)elementSizeBytes;
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
        AppendLine($"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfLocal.PointeeType))}, ptr {GetLocalSlotPointer(addressOfLocal.LocalName)}, i32 0");
    }

    private bool TryAliasAddressOfLocal(string resultName, SsaAddressOfLocalRValue addressOfLocal)
    {
        EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
        _valueAliases[resultName] = GetLocalSlotPointer(addressOfLocal.LocalName);
        return true;
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
                $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfParameter.PointeeType))}, ptr %{EscapeIdentifier(parameter.LlvmName)}, i32 0");
            return;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        AppendLine(
            $"  {result} = getelementptr{GetZeroOffsetGepFlags()} {MapType(NormalizeAggregateType(addressOfParameter.PointeeType))}, ptr %{EscapeIdentifier($"slot_param_{parameter.SourceName}")}, i32 0");
    }

    private bool TryAliasAddressOfParameter(string resultName, SsaAddressOfParameterRValue addressOfParameter)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
        if (parameter is null)
        {
            return false;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            _valueAliases[resultName] = $"%{EscapeIdentifier(parameter.LlvmName)}";
            return true;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        _valueAliases[resultName] = $"%{EscapeIdentifier($"slot_param_{parameter.SourceName}")}";
        return true;
    }

    private void EmitFieldAddress(string result, SsaFieldAddressRValue fieldAddress)
    {
        var aggregateType = NormalizeAggregateType(
            StarkTypeSymbols.IsPointerBackedBorrowType(fieldAddress.AggregateType)
                ? StarkTypeSymbols.BorrowReturnValueType(fieldAddress.AggregateType)
                : fieldAddress.AggregateType);

        if (TryGetLayoutControlledFieldOffsetBytes(
                aggregateType,
                fieldAddress.FieldIndex,
                out var fieldOffsetBytes))
        {
            AppendLine($"  {result} = getelementptr{GetProvenInObjectGepFlags()} i8, ptr {FormatValue(fieldAddress.Address)}, i64 {fieldOffsetBytes}");
            return;
        }

        AppendLine($"  {result} = getelementptr{GetProvenInObjectGepFlags()} {MapType(aggregateType)}, ptr {FormatValue(fieldAddress.Address)}, i32 0, i32 {fieldAddress.FieldIndex}");
    }

    private bool TryGetLayoutControlledFieldOffsetBytes(
        StarkTypeSymbol aggregateType,
        int fieldIndex,
        out int fieldOffsetBytes)
    {
        fieldOffsetBytes = 0;
        var normalizedType = NormalizeAggregateType(aggregateType);
        if (normalizedType.Kind != StarkTypeKind.Named
            || ResolveNamedTypeSymbol(normalizedType) is not { } namedType
            || !LlvmLayoutControlledAggregateFacts.RequiresPhysicalLayout(namedType)
            || TryGetConcreteTypeLayout(normalizedType) is not { } layout
            || fieldIndex < 0
            || fieldIndex >= namedType.OrderedFields.Count)
        {
            return false;
        }

        var fieldName = namedType.OrderedFields[fieldIndex].Name;
        if (!layout.TryGetField(fieldName, out var fieldLayout))
        {
            return false;
        }

        fieldOffsetBytes = fieldLayout.OffsetBytes;
        return true;
    }

    private void EmitElementAddress(string result, SsaElementAddressRValue elementAddress)
    {
        if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray
            && !ElementAddressTargetsWholeAggregate(elementAddress))
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

        AppendLine($"  {result} = getelementptr{GetUnboundedPointerIndexGepFlags(elementAddress.Address, elementAddress.Index)} {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, {MapType(elementAddress.Index.Type)} {FormatValue(elementAddress.Index)}");
    }

    /// <summary>
    /// True when the element address yields a pointer to the whole aggregate type
    /// rather than into it: addressing an element of dynamic or raw storage whose
    /// element type is itself a fixed array. Such addresses must use the
    /// pointer-element GEP form (stride = whole array), not the into-array form.
    /// </summary>
    private bool ElementAddressTargetsWholeAggregate(SsaElementAddressRValue elementAddress)
    {
        return elementAddress.Type.Kind == StarkTypeKind.RawPointer
            && elementAddress.Type.ElementType is { } pointeeType
            && string.Equals(MapType(pointeeType), MapType(elementAddress.AggregateType), StringComparison.Ordinal);
    }

    private bool TryAliasZeroElementAddress(string resultName, SsaElementAddressRValue elementAddress)
    {
        if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray
            || elementAddress.ConstantIndex != 0)
        {
            return false;
        }

        _valueAliases[resultName] = FormatValue(elementAddress.Address);
        return true;
    }

    private void EmitSliceElementAddress(string result, SsaSliceElementAddressRValue sliceElementAddress)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var elementType = sliceElementAddress.Type.ElementType ?? throw new UnsupportedBodyEmissionException("Slice element address requires a raw pointer element type.");

        AppendLine($"  {dataPointer} = extractvalue {MapType(sliceElementAddress.Slice.Type)} {FormatValue(sliceElementAddress.Slice)}, 0");
        AppendLine($"  {result} = getelementptr{GetSliceElementGepFlags(sliceElementAddress.Slice, sliceElementAddress.Index)} {MapType(elementType)}, ptr {dataPointer}, {MapType(sliceElementAddress.Index.Type)} {FormatValue(sliceElementAddress.Index)}");
    }
}
