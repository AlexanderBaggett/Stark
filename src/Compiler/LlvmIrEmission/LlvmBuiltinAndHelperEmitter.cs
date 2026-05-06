using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmBuiltinAndHelperEmitter
{
    private const string AsciiEqualityHelperName = "__stark_ascii_equal";
    private const string UnicodeEqualityHelperName = "__stark_unicode_equal";
    private const string AsciiCompareHelperName = "__stark_ascii_compare";
    private const string UnicodeCompareHelperName = "__stark_unicode_compare";
    private const string FixedArrayCompareHelperNamePrefix = "__stark_fixed_array_compare_";
    private const string ScalarizedAggregateCompareHelperNamePrefix = "__stark_named_compare_";
    private const string IntegerExponentHelperNamePrefix = "__stark_int_pow_i";
    private const string HeapAllocateHelperName = "__stark_heap_alloc";
    private const string HeapFreeHelperName = "__stark_heap_free";
    private const string RuntimeAllocateHelperName = "__stark_runtime_alloc";
    private const string RuntimeTryAllocateHelperName = "__stark_runtime_try_alloc";
    private const string RuntimeReallocateHelperName = "__stark_runtime_realloc";
    private const string RuntimeTryReallocateHelperName = "__stark_runtime_try_realloc";
    private const string RuntimeFreeHelperName = "__stark_runtime_free";
    private const string DynamicStorageAllocateHelperName = "__stark_dynamic_alloc";
    private const string DynamicStorageReserveHelperName = "__stark_dynamic_reserve";
    private const string DynamicStorageTryReserveHelperName = "__stark_dynamic_try_reserve";
    private const string DynamicStorageTryReserveCapacityHelperName = "__stark_dynamic_try_reserve_capacity";
    private const string DynamicStorageMoveLastPointerHelperName = "__stark_dynamic_move_last_ptr";
    private const string DynamicStorageMoveAtToOutHelperName = "__stark_dynamic_move_at_to_out";
    private const string OsAllocateHelperName = "__stark_os_allocate";
    private const string OsReallocateHelperName = "__stark_os_reallocate";
    private const string OsFreeHelperName = "__stark_os_free";
    private const string HeapAllocatorFamilyAttribute = "\"alloc-family\"=\"__stark_heap_alloc\"";
    private const string RuntimeAllocatorFamilyAttribute = "\"alloc-family\"=\"__stark_runtime_alloc\"";
    private const string OsAllocatorFamilyAttribute = "\"alloc-family\"=\"__stark_os_allocate\"";
    private const string RuntimeAllocatorLockName = "__stark_alloc_lock";
    private const string RuntimeAllocatorLockAcquireHelperName = "__stark_alloc_lock_acquire";
    private const string RuntimeAllocatorLockReleaseHelperName = "__stark_alloc_lock_release";
    private const string WindowsI128DivideHelperName = "__divti3";
    private const string WindowsI128ModuloHelperName = "__modti3";
    private const string WindowsI128UnsignedDivRemHelperName = "__stark_u128_divrem";
    private const string OutOfMemoryTrapHelperName = "__stark_oom_trap";
    private const string UnreachableTrapHelperName = "__stark_unreachable_trap";
    private const int AggregateScalarizationThresholdBytes = 16;
    private const int AggregateScalarizationMaxLeafCount = 4;
    private const int RuntimeAllocatorBucketAlignmentBytes = 16;
    private const int RuntimeAllocatorSlabTargetBytes = 4096;
    private const int RuntimeAllocatorMinimumSlabBlockCount = 2;
    private static readonly int[] RuntimeAllocatorBucketSizes = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

    private readonly LlvmEmissionContext _context;
    private readonly Func<bool, TypedFunctionSignature, AbiFunctionSignature, FunctionEffectProfile, FunctionMemoryEffectSummary?, IReadOnlyDictionary<string, ParameterMemoryEffectSummary>?, string> _buildDefinitionSignature;
    private readonly Func<IEnumerable<SsaBinaryRValue>> _enumerateBinaryOperations;
    private readonly Func<IEnumerable<SsaFunction>> _enumerateSsaFunctions;
    private readonly Func<string, string> _escapeInlineAsmString;
    private readonly Func<bool> _usesLifetimeMarkers;
    private readonly Func<bool> _usesInvariantStartIntrinsic;
    private readonly Func<bool> _usesHeapAllocator;
    private readonly Func<bool> _usesUnreachableTrapHelper;
    private readonly Func<bool> _usesAssumeIntrinsic;
    private readonly Func<bool> _usesMemcpyIntrinsic;
    private readonly Func<bool> _usesMemmoveIntrinsic;
    private readonly Func<bool> _usesMemcpyInlineIntrinsic;
    private readonly Func<bool> _usesMemsetIntrinsic;
    private readonly Func<bool> _usesMemsetInlineIntrinsic;

    public LlvmBuiltinAndHelperEmitter(
        LlvmEmissionContext context,
        Func<bool, TypedFunctionSignature, AbiFunctionSignature, FunctionEffectProfile, FunctionMemoryEffectSummary?, IReadOnlyDictionary<string, ParameterMemoryEffectSummary>?, string> buildDefinitionSignature,
        Func<IEnumerable<SsaBinaryRValue>> enumerateBinaryOperations,
        Func<IEnumerable<SsaFunction>> enumerateSsaFunctions,
        Func<string, string> escapeInlineAsmString,
        Func<bool> usesLifetimeMarkers,
        Func<bool> usesInvariantStartIntrinsic,
        Func<bool> usesHeapAllocator,
        Func<bool> usesUnreachableTrapHelper,
        Func<bool> usesAssumeIntrinsic,
        Func<bool> usesMemcpyIntrinsic,
        Func<bool> usesMemmoveIntrinsic,
        Func<bool> usesMemcpyInlineIntrinsic,
        Func<bool> usesMemsetIntrinsic,
        Func<bool> usesMemsetInlineIntrinsic)
    {
        _context = context;
        _buildDefinitionSignature = buildDefinitionSignature;
        _enumerateBinaryOperations = enumerateBinaryOperations;
        _enumerateSsaFunctions = enumerateSsaFunctions;
        _escapeInlineAsmString = escapeInlineAsmString;
        _usesLifetimeMarkers = usesLifetimeMarkers;
        _usesInvariantStartIntrinsic = usesInvariantStartIntrinsic;
        _usesHeapAllocator = usesHeapAllocator;
        _usesUnreachableTrapHelper = usesUnreachableTrapHelper;
        _usesAssumeIntrinsic = usesAssumeIntrinsic;
        _usesMemcpyIntrinsic = usesMemcpyIntrinsic;
        _usesMemmoveIntrinsic = usesMemmoveIntrinsic;
        _usesMemcpyInlineIntrinsic = usesMemcpyInlineIntrinsic;
        _usesMemsetIntrinsic = usesMemsetIntrinsic;
        _usesMemsetInlineIntrinsic = usesMemsetInlineIntrinsic;
    }

    private string CurrentModuleName => _context.ModuleName;

    private LlvmTargetInfo? TargetInfo => _context.TargetInfo;

    private string AllocatorSizeType => _context.AllocatorSizeType;

    private bool DebugInfoEnabled => _context.DebugInfoEnabled;

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _context.ResolveNamedTypeSymbol(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    private static string GetProvenInObjectGepFlags() => " inbounds nuw";

    private IReadOnlyList<FieldSymbol>? GetScalarizableNamedAggregateFields(NamedTypeSymbol namedType) =>
        _context.GetScalarizableNamedAggregateFields(namedType);

    public void EmitIntrinsicDeclarations(StringBuilder builder, IEnumerable<TypedFunctionSignature> signatures)
    {
        var declarations = new SortedSet<string>(StringComparer.Ordinal);
        var systemMemoryBuiltins = CollectSystemMemoryAllocatorBuiltins(signatures);
        var usesSystemMemoryAllocate = systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Allocate);
        var usesSystemMemoryReallocate = systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Reallocate);
        var usesSystemMemoryFree = systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Free);
        var usesSystemMemoryTrap = usesSystemMemoryAllocate || usesSystemMemoryReallocate;
        var usesDynamicStorageAllocator = UsesDynamicStorageAllocator();
        var usesRuntimeAllocator = _usesHeapAllocator()
            || usesDynamicStorageAllocator
            || string.Equals(CurrentModuleName, "System.Memory", StringComparison.Ordinal)
            || usesSystemMemoryAllocate
            || usesSystemMemoryReallocate
            || usesSystemMemoryFree;
        var usesTextConcatBuiltin = UsesSystemTextConcatBuiltin(signatures);

        foreach (var binary in _enumerateBinaryOperations()
                     .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Float))
        {
            var llvmType = MapType(binary.Type);
            var suffix = GetFloatIntrinsicSuffix(binary.Type);
            declarations.Add($"declare {llvmType} @llvm.pow.{suffix}({llvmType}, {llvmType})");
        }

        foreach (var type in CollectFusedMultiplyAddTypes())
        {
            var llvmType = MapType(type);
            declarations.Add($"declare {llvmType} @{GetFusedMultiplyAddIntrinsicName(type)}({llvmType}, {llvmType}, {llvmType})");
        }

        foreach (var declaration in EnumerateSystemMathIntrinsicDeclarations(signatures))
        {
            declarations.Add(declaration);
        }

        foreach (var declaration in EnumerateConstrainedFloatingPointIntrinsicDeclarations())
        {
            declarations.Add(declaration);
        }

        foreach (var declaration in EnumerateSystemBitOperationsIntrinsicDeclarations(signatures))
        {
            declarations.Add(declaration);
        }

        if (_usesLifetimeMarkers())
        {
            declarations.Add("declare void @llvm.lifetime.start.p0(i64 immarg, ptr nocapture)");
            declarations.Add("declare void @llvm.lifetime.end.p0(i64 immarg, ptr nocapture)");
        }

        if (_usesInvariantStartIntrinsic())
        {
            declarations.Add("declare ptr @llvm.invariant.start.p0(i64 immarg, ptr nocapture)");
        }

        if (_usesAssumeIntrinsic())
        {
            declarations.Add("declare void @llvm.assume(i1 noundef)");
        }

        if (usesRuntimeAllocator || _usesUnreachableTrapHelper() || usesSystemMemoryTrap)
        {
            declarations.Add("declare void @llvm.trap() cold noreturn nounwind");
        }

        if (usesRuntimeAllocator && IsWindowsTarget())
        {
            declarations.Add("declare ptr @GetProcessHeap() nounwind");
            declarations.Add($"declare noalias noundef ptr @HeapAlloc(ptr, i32, {AllocatorSizeType} noundef) allocsize(2) allockind(\"alloc,uninitialized\") {OsAllocatorFamilyAttribute} nounwind");
            declarations.Add($"declare noundef ptr @HeapReAlloc(ptr, i32, ptr, {AllocatorSizeType} noundef) allocsize(3) allockind(\"realloc\") {OsAllocatorFamilyAttribute} nounwind");
            declarations.Add($"declare i32 @HeapFree(ptr, i32, ptr) allockind(\"free\") {OsAllocatorFamilyAttribute} nounwind");
        }

        if (usesRuntimeAllocator
            || usesTextConcatBuiltin
            || _usesMemcpyIntrinsic()
            || UsesAsciiToUnicodeLiteralMemcpySpecialization())
        {
            declarations.Add("declare void @llvm.memcpy.p0.p0.i64(ptr nocapture writeonly, ptr nocapture readonly, i64, i1 immarg)");
        }

        if (_usesMemsetIntrinsic())
        {
            declarations.Add("declare void @llvm.memset.p0.i64(ptr nocapture writeonly, i8, i64, i1 immarg)");
        }

        if (usesRuntimeAllocator || _usesMemmoveIntrinsic())
        {
            declarations.Add("declare void @llvm.memmove.p0.p0.i64(ptr nocapture writeonly, ptr nocapture readonly, i64, i1 immarg)");
        }

        if (_usesMemcpyInlineIntrinsic())
        {
            declarations.Add("declare void @llvm.memcpy.inline.p0.p0.i64(ptr nocapture writeonly, ptr nocapture readonly, i64 immarg, i1 immarg)");
        }

        if (_usesMemsetInlineIntrinsic())
        {
            declarations.Add("declare void @llvm.memset.inline.p0.i64(ptr nocapture writeonly, i8, i64 immarg, i1 immarg)");
        }

        if (DebugInfoEnabled)
        {
            declarations.Add("declare void @llvm.dbg.declare(metadata, metadata, metadata)");
            declarations.Add("declare void @llvm.dbg.value(metadata, metadata, metadata)");
        }

        foreach (var declaration in declarations)
        {
            builder.AppendLine(declaration);
        }

        if (declarations.Count != 0)
        {
            builder.AppendLine();
        }
    }

    public void EmitInternalHelperDefinitions(StringBuilder builder, IEnumerable<TypedFunctionSignature> signatures)
    {
        var systemMemoryBuiltins = CollectSystemMemoryAllocatorBuiltins(signatures);
        var usesSystemMemoryAllocator = systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Allocate)
            || systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Reallocate)
            || systemMemoryBuiltins.Contains(SystemMemoryBuiltinKind.Free);
        var usesDynamicStorageAllocator = UsesDynamicStorageAllocator();

        foreach (var textType in CollectTextEqualityTypes())
        {
            EmitTextEqualityHelperDefinition(
                builder,
                textType,
                textType.Kind == StarkTypeKind.Ascii ? AsciiEqualityHelperName : UnicodeEqualityHelperName);
            builder.AppendLine();
        }

        foreach (var textType in CollectTextOrderedComparisonTypes())
        {
            EmitTextComparisonHelperDefinition(
                builder,
                textType,
                textType.Kind == StarkTypeKind.Ascii ? AsciiCompareHelperName : UnicodeCompareHelperName);
            builder.AppendLine();
        }

        foreach (var fixedArrayType in CollectFixedArrayOrderedComparisonTypes())
        {
            EmitFixedArrayOrderedComparisonHelperDefinition(builder, fixedArrayType);
            builder.AppendLine();
        }

        foreach (var namedAggregateType in CollectScalarizedNamedAggregateOrderedComparisonTypes())
        {
            EmitScalarizedNamedAggregateOrderedComparisonHelperDefinition(builder, namedAggregateType);
            builder.AppendLine();
        }

        foreach (var bitWidth in CollectIntegerExponentBitWidths())
        {
            EmitIntegerExponentHelperDefinition(builder, bitWidth);
            builder.AppendLine();
        }

        if (UsesWindowsI128DivisionLibcall())
        {
            EmitWindowsI128DivisionLibcallDefinitions(builder);
            builder.AppendLine();
        }

        if (_usesUnreachableTrapHelper())
        {
            EmitTrapHelperDefinition(builder, UnreachableTrapHelperName);
            builder.AppendLine();
        }

        if (_usesHeapAllocator())
        {
            EmitTrapHelperDefinition(builder, OutOfMemoryTrapHelperName);
            builder.AppendLine();
            EmitRuntimeAllocatorHelperDefinitions(builder);
            builder.AppendLine();
            EmitHeapAllocateHelperDefinition(builder);
            builder.AppendLine();
            EmitHeapFreeHelperDefinition(builder);
            builder.AppendLine();
        }
        else if (usesDynamicStorageAllocator || CurrentModuleName == "System.Memory" || usesSystemMemoryAllocator)
        {
            EmitTrapHelperDefinition(builder, OutOfMemoryTrapHelperName);
            builder.AppendLine();
            EmitRuntimeAllocatorHelperDefinitions(builder);
            builder.AppendLine();
        }
    }

    private bool UsesWindowsI128DivisionLibcall()
    {
        return IsWindowsTarget()
            && _enumerateBinaryOperations().Any(static binary =>
                binary.Type.Kind == StarkTypeKind.Integer
                && binary.Type.BitWidth == 128
                && binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo);
    }

    private static void EmitWindowsI128DivisionLibcallDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"${WindowsI128DivideHelperName} = comdat any");
        builder.AppendLine($"${WindowsI128ModuloHelperName} = comdat any");
        builder.AppendLine();
        builder.AppendLine($"define internal dso_local {{ i128, i128 }} @{WindowsI128UnsignedDivRemHelperName}(i128 %numerator, i128 %denominator) unnamed_addr nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  br label %loop");
        builder.AppendLine();
        builder.AppendLine("loop:");
        builder.AppendLine("  %index = phi i32 [ 127, %entry ], [ %next_index, %continue ]");
        builder.AppendLine("  %quotient = phi i128 [ 0, %entry ], [ %next_quotient, %continue ]");
        builder.AppendLine("  %remainder = phi i128 [ 0, %entry ], [ %next_remainder, %continue ]");
        builder.AppendLine("  %shift = zext i32 %index to i128");
        builder.AppendLine("  %numerator_shifted = lshr i128 %numerator, %shift");
        builder.AppendLine("  %next_bit = and i128 %numerator_shifted, 1");
        builder.AppendLine("  %remainder_shifted = shl i128 %remainder, 1");
        builder.AppendLine("  %candidate_remainder = or i128 %remainder_shifted, %next_bit");
        builder.AppendLine("  %can_subtract = icmp uge i128 %candidate_remainder, %denominator");
        builder.AppendLine("  %subtracted_remainder = sub i128 %candidate_remainder, %denominator");
        builder.AppendLine("  %next_remainder = select i1 %can_subtract, i128 %subtracted_remainder, i128 %candidate_remainder");
        builder.AppendLine("  %quotient_bit = shl i128 1, %shift");
        builder.AppendLine("  %quotient_with_bit = or i128 %quotient, %quotient_bit");
        builder.AppendLine("  %next_quotient = select i1 %can_subtract, i128 %quotient_with_bit, i128 %quotient");
        builder.AppendLine("  %done = icmp eq i32 %index, 0");
        builder.AppendLine("  br i1 %done, label %exit, label %continue");
        builder.AppendLine();
        builder.AppendLine("continue:");
        builder.AppendLine("  %next_index = add i32 %index, -1");
        builder.AppendLine("  br label %loop");
        builder.AppendLine();
        builder.AppendLine("exit:");
        builder.AppendLine("  %with_quotient = insertvalue { i128, i128 } undef, i128 %next_quotient, 0");
        builder.AppendLine("  %with_remainder = insertvalue { i128, i128 } %with_quotient, i128 %next_remainder, 1");
        builder.AppendLine("  ret { i128, i128 } %with_remainder");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"define linkonce_odr dso_local <2 x i64> @{WindowsI128DivideHelperName}(ptr nocapture readonly %left, ptr nocapture readonly %right) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %left_value = load i128, ptr %left, align 8");
        builder.AppendLine("  %right_value = load i128, ptr %right, align 8");
        builder.AppendLine("  %left_negative = icmp slt i128 %left_value, 0");
        builder.AppendLine("  %right_negative = icmp slt i128 %right_value, 0");
        builder.AppendLine("  %negated_left = sub i128 0, %left_value");
        builder.AppendLine("  %negated_right = sub i128 0, %right_value");
        builder.AppendLine("  %abs_left = select i1 %left_negative, i128 %negated_left, i128 %left_value");
        builder.AppendLine("  %abs_right = select i1 %right_negative, i128 %negated_right, i128 %right_value");
        builder.AppendLine($"  %divrem = call {{ i128, i128 }} @{WindowsI128UnsignedDivRemHelperName}(i128 %abs_left, i128 %abs_right)");
        builder.AppendLine("  %unsigned_quotient = extractvalue { i128, i128 } %divrem, 0");
        builder.AppendLine("  %quotient_negative = xor i1 %left_negative, %right_negative");
        builder.AppendLine("  %negated_quotient = sub i128 0, %unsigned_quotient");
        builder.AppendLine("  %quotient = select i1 %quotient_negative, i128 %negated_quotient, i128 %unsigned_quotient");
        EmitI128VectorReturn(builder, "%quotient");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"define linkonce_odr dso_local <2 x i64> @{WindowsI128ModuloHelperName}(ptr nocapture readonly %left, ptr nocapture readonly %right) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %left_value = load i128, ptr %left, align 8");
        builder.AppendLine("  %right_value = load i128, ptr %right, align 8");
        builder.AppendLine("  %left_negative = icmp slt i128 %left_value, 0");
        builder.AppendLine("  %right_negative = icmp slt i128 %right_value, 0");
        builder.AppendLine("  %negated_left = sub i128 0, %left_value");
        builder.AppendLine("  %negated_right = sub i128 0, %right_value");
        builder.AppendLine("  %abs_left = select i1 %left_negative, i128 %negated_left, i128 %left_value");
        builder.AppendLine("  %abs_right = select i1 %right_negative, i128 %negated_right, i128 %right_value");
        builder.AppendLine($"  %divrem = call {{ i128, i128 }} @{WindowsI128UnsignedDivRemHelperName}(i128 %abs_left, i128 %abs_right)");
        builder.AppendLine("  %unsigned_remainder = extractvalue { i128, i128 } %divrem, 1");
        builder.AppendLine("  %negated_remainder = sub i128 0, %unsigned_remainder");
        builder.AppendLine("  %remainder = select i1 %left_negative, i128 %negated_remainder, i128 %unsigned_remainder");
        EmitI128VectorReturn(builder, "%remainder");
        builder.AppendLine("}");
    }

    private static void EmitI128VectorReturn(StringBuilder builder, string valueName)
    {
        builder.AppendLine($"  %return_low = trunc i128 {valueName} to i64");
        builder.AppendLine($"  %return_high_i128 = lshr i128 {valueName}, 64");
        builder.AppendLine("  %return_high = trunc i128 %return_high_i128 to i64");
        builder.AppendLine("  %return_low_vector = insertelement <2 x i64> undef, i64 %return_low, i32 0");
        builder.AppendLine("  %return_vector = insertelement <2 x i64> %return_low_vector, i64 %return_high, i32 1");
        builder.AppendLine("  ret <2 x i64> %return_vector");
    }

    private static void EmitTrapHelperDefinition(StringBuilder builder, string helperName)
    {
        builder.AppendLine(
            $"define internal dso_local coldcc void @{helperName}() unnamed_addr cold noreturn nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
        builder.AppendLine("}");
    }

    private void EmitHeapAllocateHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine(
            $"define internal dso_local noalias nonnull noundef ptr @{HeapAllocateHelperName}({AllocatorSizeType} noundef %size, {AllocatorSizeType} noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") {HeapAllocatorFamilyAttribute} nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %raw = call noalias nonnull noundef ptr @{RuntimeAllocateHelperName}({AllocatorSizeType} noundef %size, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  ret ptr %raw");
        builder.AppendLine("}");
    }

    private void EmitHeapFreeHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine($"define internal dso_local void @{HeapFreeHelperName}(ptr %ptr) unnamed_addr allockind(\"free\") {HeapAllocatorFamilyAttribute} nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  call void @{RuntimeFreeHelperName}(ptr %ptr)");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private void EmitRuntimeAllocatorHelperDefinitions(StringBuilder builder)
    {
        EmitRuntimeAllocatorComdatDefinitions(builder);
        builder.AppendLine();
        EmitRuntimeAllocatorGlobalDefinitions(builder);
        builder.AppendLine();
        EmitRuntimeAllocatorLockHelperDefinitions(builder);
        builder.AppendLine();
        EmitRuntimeAllocateHelperDefinition(builder);
        builder.AppendLine();
        EmitRuntimeTryAllocateHelperDefinition(builder);
        builder.AppendLine();
        EmitRuntimeReallocateHelperDefinition(builder);
        builder.AppendLine();
        EmitRuntimeTryReallocateHelperDefinition(builder);
        builder.AppendLine();
        EmitRuntimeFreeHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageHelperDefinitions(builder);
        builder.AppendLine();
        EmitOsAllocateHelperDefinition(builder);
        builder.AppendLine();
        if (IsWindowsTarget())
        {
            EmitOsReallocateHelperDefinition(builder);
            builder.AppendLine();
        }

        EmitOsFreeHelperDefinition(builder);
    }

    private void EmitRuntimeAllocatorComdatDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"${RuntimeAllocatorLockName} = comdat any");
        foreach (var bucketSize in RuntimeAllocatorBucketSizes)
        {
            builder.AppendLine($"${GetRuntimeAllocatorBucketGlobalName(bucketSize)} = comdat any");
        }

        builder.AppendLine($"${RuntimeAllocatorLockAcquireHelperName} = comdat any");
        builder.AppendLine($"${RuntimeAllocatorLockReleaseHelperName} = comdat any");
        builder.AppendLine($"${RuntimeAllocateHelperName} = comdat any");
        builder.AppendLine($"${RuntimeTryAllocateHelperName} = comdat any");
        builder.AppendLine($"${RuntimeReallocateHelperName} = comdat any");
        builder.AppendLine($"${RuntimeTryReallocateHelperName} = comdat any");
        builder.AppendLine($"${RuntimeFreeHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageAllocateHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageReserveHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageTryReserveHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageTryReserveCapacityHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageMoveLastPointerHelperName} = comdat any");
        builder.AppendLine($"${DynamicStorageMoveAtToOutHelperName} = comdat any");
    }

    private void EmitDynamicStorageHelperDefinitions(StringBuilder builder)
    {
        EmitDynamicStorageAllocateHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageReserveHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageTryReserveHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageTryReserveCapacityHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageMoveLastPointerHelperDefinition(builder);
        builder.AppendLine();
        EmitDynamicStorageMoveAtToOutHelperDefinition(builder);
    }

    private void EmitDynamicStorageAllocateHelperDefinition(StringBuilder builder)
    {
        var capacityAllocator = AllocatorSizeType == "i64"
            ? "%capacity"
            : "%capacity_size";

        builder.AppendLine(
            $"define linkonce_odr hidden {{ ptr, i64, i64 }} @{DynamicStorageAllocateHelperName}(i64 noundef %capacity, {AllocatorSizeType} noundef %element_size, {AllocatorSizeType} noundef allocalign %alignment, i64 noundef %max_count) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %is_zero = icmp eq i64 %capacity, 0");
        builder.AppendLine("  br i1 %is_zero, label %zero, label %check");
        builder.AppendLine();
        builder.AppendLine("zero:");
        builder.AppendLine("  br label %done");
        builder.AppendLine();
        builder.AppendLine("check:");
        builder.AppendLine("  %too_large = icmp ugt i64 %capacity, %max_count");
        builder.AppendLine("  br i1 %too_large, label %overflow, label %allocate");
        builder.AppendLine();
        builder.AppendLine("overflow:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
        builder.AppendLine();
        builder.AppendLine("allocate:");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {capacityAllocator} = trunc i64 %capacity to {AllocatorSizeType}");
        }

        builder.AppendLine($"  %byte_length = mul {AllocatorSizeType} {capacityAllocator}, %element_size");
        builder.AppendLine($"  %ptr = call noalias nonnull noundef ptr @{RuntimeAllocateHelperName}({AllocatorSizeType} noundef %byte_length, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  %with_ptr = insertvalue { ptr, i64, i64 } zeroinitializer, ptr %ptr, 0");
        builder.AppendLine("  %with_length = insertvalue { ptr, i64, i64 } %with_ptr, i64 0, 1");
        builder.AppendLine("  %value = insertvalue { ptr, i64, i64 } %with_length, i64 %capacity, 2");
        builder.AppendLine("  br label %done");
        builder.AppendLine();
        builder.AppendLine("done:");
        builder.AppendLine("  %result = phi { ptr, i64, i64 } [ zeroinitializer, %zero ], [ %value, %allocate ]");
        builder.AppendLine("  ret { ptr, i64, i64 } %result");
        builder.AppendLine("}");
    }

    private void EmitDynamicStorageReserveHelperDefinition(StringBuilder builder)
    {
        var oldAllocatorCount = AllocatorSizeType == "i64"
            ? "%capacity"
            : "%old_count";
        var newAllocatorCount = AllocatorSizeType == "i64"
            ? "%new_capacity"
            : "%new_count";

        builder.AppendLine(
            $"define linkonce_odr hidden void @{DynamicStorageReserveHelperName}(ptr nocapture %storage, i64 noundef %additional, {AllocatorSizeType} noundef %element_size, {AllocatorSizeType} noundef allocalign %alignment, i64 noundef %max_count) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %current = load { ptr, i64, i64 }, ptr %storage");
        builder.AppendLine("  %ptr = extractvalue { ptr, i64, i64 } %current, 0");
        builder.AppendLine("  %length = extractvalue { ptr, i64, i64 } %current, 1");
        builder.AppendLine("  %capacity = extractvalue { ptr, i64, i64 } %current, 2");
        builder.AppendLine("  %needed = add i64 %length, %additional");
        builder.AppendLine("  %needed_overflow = icmp ult i64 %needed, %length");
        builder.AppendLine("  br i1 %needed_overflow, label %overflow, label %check");
        builder.AppendLine();
        builder.AppendLine("overflow:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
        builder.AppendLine();
        builder.AppendLine("check:");
        builder.AppendLine("  %enough = icmp ule i64 %needed, %capacity");
        builder.AppendLine("  br i1 %enough, label %done, label %grow");
        builder.AppendLine();
        builder.AppendLine("grow:");
        builder.AppendLine("  %doubled = shl i64 %capacity, 1");
        builder.AppendLine("  %can_double = icmp ule i64 %capacity, 9223372036854775807");
        builder.AppendLine("  %doubled_or_max = select i1 %can_double, i64 %doubled, i64 -1");
        builder.AppendLine("  %capacity_small = icmp ult i64 %capacity, 2");
        builder.AppendLine("  %minimum = select i1 %capacity_small, i64 4, i64 %doubled_or_max");
        builder.AppendLine("  %needed_larger = icmp ugt i64 %needed, %minimum");
        builder.AppendLine("  %new_capacity = select i1 %needed_larger, i64 %needed, i64 %minimum");
        builder.AppendLine("  %too_large = icmp ugt i64 %new_capacity, %max_count");
        builder.AppendLine("  br i1 %too_large, label %overflow, label %realloc");
        builder.AppendLine();
        builder.AppendLine("realloc:");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {oldAllocatorCount} = trunc i64 %capacity to {AllocatorSizeType}");
            builder.AppendLine($"  {newAllocatorCount} = trunc i64 %new_capacity to {AllocatorSizeType}");
        }

        builder.AppendLine($"  %old_bytes = mul {AllocatorSizeType} {oldAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_bytes = mul {AllocatorSizeType} {newAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_ptr = call nonnull noundef ptr @{RuntimeReallocateHelperName}(ptr %ptr, {AllocatorSizeType} noundef %old_bytes, {AllocatorSizeType} noundef %new_bytes, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  %with_ptr = insertvalue { ptr, i64, i64 } %current, ptr %new_ptr, 0");
        builder.AppendLine("  %updated = insertvalue { ptr, i64, i64 } %with_ptr, i64 %new_capacity, 2");
        builder.AppendLine("  store { ptr, i64, i64 } %updated, ptr %storage");
        builder.AppendLine("  br label %done");
        builder.AppendLine();
        builder.AppendLine("done:");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private void EmitDynamicStorageTryReserveHelperDefinition(StringBuilder builder)
    {
        var oldAllocatorCount = AllocatorSizeType == "i64"
            ? "%capacity"
            : "%old_count";
        var newAllocatorCount = AllocatorSizeType == "i64"
            ? "%new_capacity"
            : "%new_count";

        builder.AppendLine(
            $"define linkonce_odr hidden i1 @{DynamicStorageTryReserveHelperName}(ptr nocapture %storage, i64 noundef %additional, {AllocatorSizeType} noundef %element_size, {AllocatorSizeType} noundef allocalign %alignment, i64 noundef %max_count) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %current = load { ptr, i64, i64 }, ptr %storage");
        builder.AppendLine("  %ptr = extractvalue { ptr, i64, i64 } %current, 0");
        builder.AppendLine("  %length = extractvalue { ptr, i64, i64 } %current, 1");
        builder.AppendLine("  %capacity = extractvalue { ptr, i64, i64 } %current, 2");
        builder.AppendLine("  %needed = add i64 %length, %additional");
        builder.AppendLine("  %needed_overflow = icmp ult i64 %needed, %length");
        builder.AppendLine("  br i1 %needed_overflow, label %failed, label %check");
        builder.AppendLine();
        builder.AppendLine("check:");
        builder.AppendLine("  %enough = icmp ule i64 %needed, %capacity");
        builder.AppendLine("  br i1 %enough, label %succeeded, label %grow");
        builder.AppendLine();
        builder.AppendLine("grow:");
        builder.AppendLine("  %doubled = shl i64 %capacity, 1");
        builder.AppendLine("  %can_double = icmp ule i64 %capacity, 9223372036854775807");
        builder.AppendLine("  %doubled_or_max = select i1 %can_double, i64 %doubled, i64 -1");
        builder.AppendLine("  %capacity_small = icmp ult i64 %capacity, 2");
        builder.AppendLine("  %minimum = select i1 %capacity_small, i64 4, i64 %doubled_or_max");
        builder.AppendLine("  %needed_larger = icmp ugt i64 %needed, %minimum");
        builder.AppendLine("  %new_capacity = select i1 %needed_larger, i64 %needed, i64 %minimum");
        builder.AppendLine("  %too_large = icmp ugt i64 %new_capacity, %max_count");
        builder.AppendLine("  br i1 %too_large, label %failed, label %realloc");
        builder.AppendLine();
        builder.AppendLine("realloc:");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {oldAllocatorCount} = trunc i64 %capacity to {AllocatorSizeType}");
            builder.AppendLine($"  {newAllocatorCount} = trunc i64 %new_capacity to {AllocatorSizeType}");
        }

        builder.AppendLine($"  %old_bytes = mul {AllocatorSizeType} {oldAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_bytes = mul {AllocatorSizeType} {newAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_ptr = call ptr @{RuntimeTryReallocateHelperName}(ptr %ptr, {AllocatorSizeType} noundef %old_bytes, {AllocatorSizeType} noundef %new_bytes, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  %new_ptr_is_null = icmp eq ptr %new_ptr, null");
        builder.AppendLine("  br i1 %new_ptr_is_null, label %failed, label %update");
        builder.AppendLine();
        builder.AppendLine("update:");
        builder.AppendLine("  %with_ptr = insertvalue { ptr, i64, i64 } %current, ptr %new_ptr, 0");
        builder.AppendLine("  %updated = insertvalue { ptr, i64, i64 } %with_ptr, i64 %new_capacity, 2");
        builder.AppendLine("  store { ptr, i64, i64 } %updated, ptr %storage");
        builder.AppendLine("  br label %succeeded");
        builder.AppendLine();
        builder.AppendLine("failed:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine();
        builder.AppendLine("succeeded:");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("}");
    }

    private void EmitDynamicStorageTryReserveCapacityHelperDefinition(StringBuilder builder)
    {
        var oldAllocatorCount = AllocatorSizeType == "i64"
            ? "%capacity"
            : "%old_count";
        var targetAllocatorCount = AllocatorSizeType == "i64"
            ? "%target_capacity"
            : "%target_count";

        builder.AppendLine(
            $"define linkonce_odr hidden i1 @{DynamicStorageTryReserveCapacityHelperName}(ptr nocapture %storage, i64 noundef %target_capacity, {AllocatorSizeType} noundef %element_size, {AllocatorSizeType} noundef allocalign %alignment, i64 noundef %max_count) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %current = load { ptr, i64, i64 }, ptr %storage");
        builder.AppendLine("  %ptr = extractvalue { ptr, i64, i64 } %current, 0");
        builder.AppendLine("  %capacity = extractvalue { ptr, i64, i64 } %current, 2");
        builder.AppendLine("  %enough = icmp ule i64 %target_capacity, %capacity");
        builder.AppendLine("  br i1 %enough, label %succeeded, label %grow");
        builder.AppendLine();
        builder.AppendLine("grow:");
        builder.AppendLine("  %too_large = icmp ugt i64 %target_capacity, %max_count");
        builder.AppendLine("  br i1 %too_large, label %failed, label %realloc");
        builder.AppendLine();
        builder.AppendLine("realloc:");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {oldAllocatorCount} = trunc i64 %capacity to {AllocatorSizeType}");
            builder.AppendLine($"  {targetAllocatorCount} = trunc i64 %target_capacity to {AllocatorSizeType}");
        }

        builder.AppendLine($"  %old_bytes = mul {AllocatorSizeType} {oldAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_bytes = mul {AllocatorSizeType} {targetAllocatorCount}, %element_size");
        builder.AppendLine($"  %new_ptr = call ptr @{RuntimeTryReallocateHelperName}(ptr %ptr, {AllocatorSizeType} noundef %old_bytes, {AllocatorSizeType} noundef %new_bytes, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  %new_ptr_is_null = icmp eq ptr %new_ptr, null");
        builder.AppendLine("  br i1 %new_ptr_is_null, label %failed, label %update");
        builder.AppendLine();
        builder.AppendLine("update:");
        builder.AppendLine("  %with_ptr = insertvalue { ptr, i64, i64 } %current, ptr %new_ptr, 0");
        builder.AppendLine("  %updated = insertvalue { ptr, i64, i64 } %with_ptr, i64 %target_capacity, 2");
        builder.AppendLine("  store { ptr, i64, i64 } %updated, ptr %storage");
        builder.AppendLine("  br label %succeeded");
        builder.AppendLine();
        builder.AppendLine("failed:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine();
        builder.AppendLine("succeeded:");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("}");
    }

    private void EmitDynamicStorageMoveLastPointerHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine(
            $"define linkonce_odr hidden nonnull ptr @{DynamicStorageMoveLastPointerHelperName}(ptr nocapture %storage, i64 noundef %element_size) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %current = load { ptr, i64, i64 }, ptr %storage");
        builder.AppendLine("  %length = extractvalue { ptr, i64, i64 } %current, 1");
        builder.AppendLine("  %is_empty = icmp eq i64 %length, 0");
        builder.AppendLine("  br i1 %is_empty, label %empty, label %move");
        builder.AppendLine();
        builder.AppendLine("empty:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
        builder.AppendLine();
        builder.AppendLine("move:");
        builder.AppendLine("  %new_length = sub i64 %length, 1");
        builder.AppendLine("  %ptr = extractvalue { ptr, i64, i64 } %current, 0");
        builder.AppendLine("  %byte_offset = mul i64 %new_length, %element_size");
        builder.AppendLine("  %element_ptr = getelementptr i8, ptr %ptr, i64 %byte_offset");
        builder.AppendLine("  %updated = insertvalue { ptr, i64, i64 } %current, i64 %new_length, 1");
        builder.AppendLine("  store { ptr, i64, i64 } %updated, ptr %storage");
        builder.AppendLine("  ret ptr %element_ptr");
        builder.AppendLine("}");
    }

    private void EmitDynamicStorageMoveAtToOutHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine(
            $"define linkonce_odr hidden void @{DynamicStorageMoveAtToOutHelperName}(ptr nocapture %storage, i64 noundef %index, ptr nocapture writeonly %out, i64 noundef %element_size) unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %current = load { ptr, i64, i64 }, ptr %storage");
        builder.AppendLine("  %length = extractvalue { ptr, i64, i64 } %current, 1");
        builder.AppendLine("  %in_bounds = icmp ult i64 %index, %length");
        builder.AppendLine("  br i1 %in_bounds, label %move, label %bad_index");
        builder.AppendLine();
        builder.AppendLine("bad_index:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
        builder.AppendLine();
        builder.AppendLine("move:");
        builder.AppendLine("  %ptr = extractvalue { ptr, i64, i64 } %current, 0");
        builder.AppendLine("  %byte_offset = mul i64 %index, %element_size");
        builder.AppendLine("  %element_ptr = getelementptr i8, ptr %ptr, i64 %byte_offset");
        builder.AppendLine("  call void @llvm.memcpy.p0.p0.i64(ptr %out, ptr %element_ptr, i64 %element_size, i1 false)");
        builder.AppendLine("  %new_length = sub i64 %length, 1");
        builder.AppendLine("  %has_tail = icmp ult i64 %index, %new_length");
        builder.AppendLine("  br i1 %has_tail, label %shift, label %update");
        builder.AppendLine();
        builder.AppendLine("shift:");
        builder.AppendLine("  %next_index = add i64 %index, 1");
        builder.AppendLine("  %source_offset = mul i64 %next_index, %element_size");
        builder.AppendLine("  %source_ptr = getelementptr i8, ptr %ptr, i64 %source_offset");
        builder.AppendLine("  %tail_count = sub i64 %new_length, %index");
        builder.AppendLine("  %tail_bytes = mul i64 %tail_count, %element_size");
        builder.AppendLine("  call void @llvm.memmove.p0.p0.i64(ptr %element_ptr, ptr %source_ptr, i64 %tail_bytes, i1 false)");
        builder.AppendLine("  br label %update");
        builder.AppendLine();
        builder.AppendLine("update:");
        builder.AppendLine("  %updated = insertvalue { ptr, i64, i64 } %current, i64 %new_length, 1");
        builder.AppendLine("  store { ptr, i64, i64 } %updated, ptr %storage");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private bool UsesDynamicStorageAllocator()
    {
        foreach (var function in _enumerateSsaFunctions())
        {
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is SsaValueInstruction { Value: SsaDynamicStorageAllocationRValue or SsaDynamicStorageFreeRValue or SsaDynamicStorageReserveRValue or SsaDynamicStorageTryReserveRValue or SsaDynamicStorageTryReserveCapacityRValue or SsaDynamicStorageMoveLastRValue or SsaDynamicStorageMoveAtRValue })
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void EmitRuntimeAllocatorGlobalDefinitions(StringBuilder builder)
    {
        var pointerSizeBytes = GetTargetPointerSizeBytes();
        var bucketThreadLocalStorage = IsWindowsTarget()
            ? "thread_local"
            : "thread_local(localexec)";

        builder.AppendLine($"@{RuntimeAllocatorLockName} = linkonce_odr hidden global i32 0, comdat, align 4");
        foreach (var bucketSize in RuntimeAllocatorBucketSizes)
        {
            builder.AppendLine($"@{GetRuntimeAllocatorBucketGlobalName(bucketSize)} = linkonce_odr hidden {bucketThreadLocalStorage} global ptr null, comdat, align {pointerSizeBytes}");
        }
    }

    private static void EmitRuntimeAllocatorLockHelperDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"define linkonce_odr hidden void @{RuntimeAllocatorLockAcquireHelperName}() unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  br label %try_lock");
        builder.AppendLine();
        builder.AppendLine("try_lock:");
        builder.AppendLine($"  %previous = atomicrmw xchg ptr @{RuntimeAllocatorLockName}, i32 1 acquire, align 4");
        builder.AppendLine("  %acquired = icmp eq i32 %previous, 0");
        builder.AppendLine("  br i1 %acquired, label %done, label %try_lock");
        builder.AppendLine();
        builder.AppendLine("done:");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"define linkonce_odr hidden void @{RuntimeAllocatorLockReleaseHelperName}() unnamed_addr nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  store atomic i32 0, ptr @{RuntimeAllocatorLockName} release, align 4");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private void EmitRuntimeAllocateHelperDefinition(StringBuilder builder)
    {
        EmitRuntimeAllocateHelperDefinition(builder, RuntimeAllocateHelperName, trapsOnFailure: true);
    }

    private void EmitRuntimeTryAllocateHelperDefinition(StringBuilder builder)
    {
        EmitRuntimeAllocateHelperDefinition(builder, RuntimeTryAllocateHelperName, trapsOnFailure: false);
    }

    private void EmitRuntimeAllocateHelperDefinition(
        StringBuilder builder,
        string helperName,
        bool trapsOnFailure)
    {
        var pointerSizeBytes = GetTargetPointerSizeBytes();
        var bucketAlignmentBytes = GetRuntimeAllocatorBucketAlignmentBytes(pointerSizeBytes);
        var headerBytes = GetRuntimeAllocationHeaderBytes(pointerSizeBytes);
        var bucketSizeSlotOffset = pointerSizeBytes + GetAllocatorSizeBytes();
        var largestBucketSize = RuntimeAllocatorBucketSizes[^1];
        var allocationFailureProfile = _context.GetMetadataTupleRef(["!\"branch_weights\"", "i32 1", "i32 2000"]);
        var returnAttributes = trapsOnFailure
            ? "noalias nonnull noundef ptr"
            : "noalias noundef ptr";

        builder.AppendLine(
            $"define linkonce_odr hidden {returnAttributes} @{helperName}({AllocatorSizeType} noundef %size, {AllocatorSizeType} noundef allocalign %alignment) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized,aligned\") {RuntimeAllocatorFamilyAttribute} nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %size_is_zero = icmp eq {AllocatorSizeType} %size, 0");
        builder.AppendLine($"  %requested_size = select i1 %size_is_zero, {AllocatorSizeType} 1, {AllocatorSizeType} %size");
        builder.AppendLine($"  %alignment_too_small = icmp ult {AllocatorSizeType} %alignment, {pointerSizeBytes}");
        builder.AppendLine($"  %effective_alignment = select i1 %alignment_too_small, {AllocatorSizeType} {pointerSizeBytes}, {AllocatorSizeType} %alignment");
        builder.AppendLine($"  %alignment_minus_one = sub {AllocatorSizeType} %effective_alignment, 1");
        builder.AppendLine($"  %alignment_power_check = and {AllocatorSizeType} %effective_alignment, %alignment_minus_one");
        builder.AppendLine($"  %alignment_not_power_of_two = icmp ne {AllocatorSizeType} %alignment_power_check, 0");
        builder.AppendLine("  br i1 %alignment_not_power_of_two, label %oom, label %classify");
        builder.AppendLine();
        builder.AppendLine("classify:");
        builder.AppendLine($"  %bucket_alignment_ok = icmp ule {AllocatorSizeType} %effective_alignment, {bucketAlignmentBytes}");
        builder.AppendLine($"  %bucket_size_ok = icmp ule {AllocatorSizeType} %requested_size, {largestBucketSize}");
        builder.AppendLine("  %can_bucket = and i1 %bucket_alignment_ok, %bucket_size_ok");
        builder.AppendLine("  br i1 %can_bucket, label %bucket_select_16, label %large_allocate");
        builder.AppendLine();

        for (var index = 0; index < RuntimeAllocatorBucketSizes.Length; index++)
        {
            var bucketSize = RuntimeAllocatorBucketSizes[index];
            var nextLabel = index + 1 < RuntimeAllocatorBucketSizes.Length
                ? $"bucket_select_{RuntimeAllocatorBucketSizes[index + 1]}"
                : $"bucket_{bucketSize}";
            builder.AppendLine($"bucket_select_{bucketSize}:");
            if (index + 1 < RuntimeAllocatorBucketSizes.Length)
            {
                builder.AppendLine($"  %fits_bucket_{bucketSize} = icmp ule {AllocatorSizeType} %requested_size, {bucketSize}");
                builder.AppendLine($"  br i1 %fits_bucket_{bucketSize}, label %bucket_{bucketSize}, label %{nextLabel}");
            }
            else
            {
                builder.AppendLine($"  br label %bucket_{bucketSize}");
            }

            builder.AppendLine();
        }

        foreach (var bucketSize in RuntimeAllocatorBucketSizes)
        {
            var bucketGlobalName = GetRuntimeAllocatorBucketGlobalName(bucketSize);
            builder.AppendLine($"bucket_{bucketSize}:");
            builder.AppendLine($"  %bucket_head_{bucketSize} = load ptr, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
            builder.AppendLine($"  %bucket_has_node_{bucketSize} = icmp ne ptr %bucket_head_{bucketSize}, null");
            builder.AppendLine($"  br i1 %bucket_has_node_{bucketSize}, label %bucket_{bucketSize}_pop, label %bucket_{bucketSize}_empty");
            builder.AppendLine();
            builder.AppendLine($"bucket_{bucketSize}_pop:");
            builder.AppendLine($"  %bucket_next_{bucketSize} = load ptr, ptr %bucket_head_{bucketSize}, align {bucketAlignmentBytes}");
            builder.AppendLine($"  store ptr %bucket_next_{bucketSize}, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
            builder.AppendLine($"  ret ptr %bucket_head_{bucketSize}");
            builder.AppendLine();
            builder.AppendLine($"bucket_{bucketSize}_empty:");
            builder.AppendLine($"  br label %bucket_{bucketSize}_refill");
            builder.AppendLine();
        }

        foreach (var bucketSize in RuntimeAllocatorBucketSizes)
        {
            EmitRuntimeAllocatorBucketRefillBlock(
                builder,
                bucketSize,
                pointerSizeBytes,
                bucketAlignmentBytes,
                headerBytes,
                bucketSizeSlotOffset,
                allocationFailureProfile);
            builder.AppendLine();
        }

        builder.AppendLine("large_allocate:");
        builder.AppendLine("  br label %os_allocate");
        builder.AppendLine();
        builder.AppendLine("os_allocate:");
        builder.AppendLine($"  %with_header = add {AllocatorSizeType} %requested_size, {headerBytes}");
        builder.AppendLine($"  %overflow_header = icmp ult {AllocatorSizeType} %with_header, %requested_size");
        builder.AppendLine($"  %total = add {AllocatorSizeType} %with_header, %effective_alignment");
        builder.AppendLine($"  %overflow_alignment = icmp ult {AllocatorSizeType} %total, %with_header");
        builder.AppendLine("  %size_overflow = or i1 %overflow_header, %overflow_alignment");
        builder.AppendLine("  br i1 %size_overflow, label %oom, label %allocate_os");
        builder.AppendLine();
        builder.AppendLine("allocate_os:");
        builder.AppendLine($"  %base = call noalias noundef ptr @{OsAllocateHelperName}({AllocatorSizeType} noundef %total)");
        builder.AppendLine("  %is_null = icmp eq ptr %base, null");
        builder.AppendLine($"  br i1 %is_null, label %oom, label %ok, !prof {allocationFailureProfile}");
        builder.AppendLine();
        builder.AppendLine("oom:");
        if (trapsOnFailure)
        {
            builder.AppendLine($"  call coldcc void @{OutOfMemoryTrapHelperName}()");
            builder.AppendLine("  unreachable");
        }
        else
        {
            builder.AppendLine("  ret ptr null");
        }

        builder.AppendLine();
        builder.AppendLine("ok:");
        builder.AppendLine($"  %base_int = ptrtoint ptr %base to {AllocatorSizeType}");
        builder.AppendLine($"  %data_start = add {AllocatorSizeType} %base_int, {headerBytes}");
        builder.AppendLine($"  %block_alignment_minus_one = sub {AllocatorSizeType} %effective_alignment, 1");
        builder.AppendLine($"  %data_with_mask = add {AllocatorSizeType} %data_start, %block_alignment_minus_one");
        builder.AppendLine($"  %negative_alignment = sub {AllocatorSizeType} 0, %effective_alignment");
        builder.AppendLine($"  %aligned_int = and {AllocatorSizeType} %data_with_mask, %negative_alignment");
        builder.AppendLine($"  %header_int = sub {AllocatorSizeType} %aligned_int, {headerBytes}");
        builder.AppendLine("  %result = inttoptr " + AllocatorSizeType + " %aligned_int to ptr");
        builder.AppendLine("  %header = inttoptr " + AllocatorSizeType + " %header_int to ptr");
        builder.AppendLine($"  store ptr %base, ptr %header, align {pointerSizeBytes}");
        builder.AppendLine($"  %length_slot = getelementptr i8, ptr %header, i64 {pointerSizeBytes}");
        builder.AppendLine($"  store {AllocatorSizeType} %total, ptr %length_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %bucket_size_slot = getelementptr i8, ptr %header, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  store {AllocatorSizeType} 0, ptr %bucket_size_slot, align {pointerSizeBytes}");
        builder.AppendLine("  ret ptr %result");
        builder.AppendLine("}");
    }

    private void EmitRuntimeAllocatorBucketRefillBlock(
        StringBuilder builder,
        int bucketSize,
        int pointerSizeBytes,
        int bucketAlignmentBytes,
        int headerBytes,
        int bucketSizeSlotOffset,
        string allocationFailureProfile)
    {
        var bucketGlobalName = GetRuntimeAllocatorBucketGlobalName(bucketSize);
        var strideBytes = GetRuntimeAllocatorBucketStrideBytes(bucketSize, headerBytes, bucketAlignmentBytes);
        var slabBlockCount = GetRuntimeAllocatorSlabBlockCount(bucketSize, headerBytes, bucketAlignmentBytes, strideBytes);
        var slabTotalBytes = GetRuntimeAllocatorSlabTotalBytes(bucketSize, headerBytes, bucketAlignmentBytes, strideBytes, slabBlockCount);
        var alignmentMask = bucketAlignmentBytes - 1;
        var negativeAlignment = -bucketAlignmentBytes;

        builder.AppendLine($"bucket_{bucketSize}_refill:");
        builder.AppendLine($"  %bucket_{bucketSize}_slab_base = call noalias noundef ptr @{OsAllocateHelperName}({AllocatorSizeType} noundef {slabTotalBytes})");
        builder.AppendLine($"  %bucket_{bucketSize}_slab_is_null = icmp eq ptr %bucket_{bucketSize}_slab_base, null");
        builder.AppendLine($"  br i1 %bucket_{bucketSize}_slab_is_null, label %oom, label %bucket_{bucketSize}_slab_ok, !prof {allocationFailureProfile}");
        builder.AppendLine();
        builder.AppendLine($"bucket_{bucketSize}_slab_ok:");
        builder.AppendLine($"  %bucket_{bucketSize}_base_int = ptrtoint ptr %bucket_{bucketSize}_slab_base to {AllocatorSizeType}");
        builder.AppendLine($"  %bucket_{bucketSize}_data_start = add {AllocatorSizeType} %bucket_{bucketSize}_base_int, {headerBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_data_with_mask = add {AllocatorSizeType} %bucket_{bucketSize}_data_start, {alignmentMask}");
        builder.AppendLine($"  %bucket_{bucketSize}_first_int = and {AllocatorSizeType} %bucket_{bucketSize}_data_with_mask, {negativeAlignment}");
        builder.AppendLine($"  %bucket_{bucketSize}_first = inttoptr {AllocatorSizeType} %bucket_{bucketSize}_first_int to ptr");
        EmitRuntimeAllocatorBucketHeaderStores(
            builder,
            bucketSize,
            "first",
            $"%bucket_{bucketSize}_slab_base",
            $"%bucket_{bucketSize}_first_int",
            slabTotalBytes,
            pointerSizeBytes,
            headerBytes,
            bucketSizeSlotOffset);
        builder.AppendLine($"  br label %bucket_{bucketSize}_refill_loop");
        builder.AppendLine();
        builder.AppendLine($"bucket_{bucketSize}_refill_loop:");
        builder.AppendLine($"  %bucket_{bucketSize}_refill_index = phi {AllocatorSizeType} [1, %bucket_{bucketSize}_slab_ok], [%bucket_{bucketSize}_refill_next, %bucket_{bucketSize}_refill_body]");
        builder.AppendLine($"  %bucket_{bucketSize}_refill_done = icmp eq {AllocatorSizeType} %bucket_{bucketSize}_refill_index, {slabBlockCount}");
        builder.AppendLine($"  br i1 %bucket_{bucketSize}_refill_done, label %bucket_{bucketSize}_refill_done_block, label %bucket_{bucketSize}_refill_body");
        builder.AppendLine();
        builder.AppendLine($"bucket_{bucketSize}_refill_body:");
        builder.AppendLine($"  %bucket_{bucketSize}_refill_offset = mul {AllocatorSizeType} %bucket_{bucketSize}_refill_index, {strideBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_block_int = add {AllocatorSizeType} %bucket_{bucketSize}_first_int, %bucket_{bucketSize}_refill_offset");
        builder.AppendLine($"  %bucket_{bucketSize}_block = inttoptr {AllocatorSizeType} %bucket_{bucketSize}_block_int to ptr");
        EmitRuntimeAllocatorBucketHeaderStores(
            builder,
            bucketSize,
            "block",
            $"%bucket_{bucketSize}_slab_base",
            $"%bucket_{bucketSize}_block_int",
            slabTotalBytes,
            pointerSizeBytes,
            headerBytes,
            bucketSizeSlotOffset);
        builder.AppendLine($"  %bucket_{bucketSize}_old_head = load ptr, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
        builder.AppendLine($"  store ptr %bucket_{bucketSize}_old_head, ptr %bucket_{bucketSize}_block, align {bucketAlignmentBytes}");
        builder.AppendLine($"  store ptr %bucket_{bucketSize}_block, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_refill_next = add {AllocatorSizeType} %bucket_{bucketSize}_refill_index, 1");
        builder.AppendLine($"  br label %bucket_{bucketSize}_refill_loop");
        builder.AppendLine();
        builder.AppendLine($"bucket_{bucketSize}_refill_done_block:");
        builder.AppendLine($"  ret ptr %bucket_{bucketSize}_first");
    }

    private void EmitRuntimeAllocatorBucketHeaderStores(
        StringBuilder builder,
        int bucketSize,
        string localPrefix,
        string slabBase,
        string blockDataInt,
        int slabTotalBytes,
        int pointerSizeBytes,
        int headerBytes,
        int bucketSizeSlotOffset)
    {
        builder.AppendLine($"  %bucket_{bucketSize}_{localPrefix}_header_int = sub {AllocatorSizeType} {blockDataInt}, {headerBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_{localPrefix}_header = inttoptr {AllocatorSizeType} %bucket_{bucketSize}_{localPrefix}_header_int to ptr");
        builder.AppendLine($"  store ptr {slabBase}, ptr %bucket_{bucketSize}_{localPrefix}_header, align {pointerSizeBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_{localPrefix}_length_slot = getelementptr i8, ptr %bucket_{bucketSize}_{localPrefix}_header, i64 {pointerSizeBytes}");
        builder.AppendLine($"  store {AllocatorSizeType} {slabTotalBytes}, ptr %bucket_{bucketSize}_{localPrefix}_length_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %bucket_{bucketSize}_{localPrefix}_bucket_size_slot = getelementptr i8, ptr %bucket_{bucketSize}_{localPrefix}_header, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  store {AllocatorSizeType} {bucketSize}, ptr %bucket_{bucketSize}_{localPrefix}_bucket_size_slot, align {pointerSizeBytes}");
    }

    private void EmitRuntimeReallocateHelperDefinition(StringBuilder builder)
    {
        var pointerSizeBytes = GetTargetPointerSizeBytes();
        var bucketAlignmentBytes = GetRuntimeAllocatorBucketAlignmentBytes(pointerSizeBytes);
        var headerBytes = GetRuntimeAllocationHeaderBytes(pointerSizeBytes);
        var bucketSizeSlotOffset = pointerSizeBytes + GetAllocatorSizeBytes();
        var copyLength = $"%copy_length";
        var copyLengthI64 = AllocatorSizeType == "i64"
            ? copyLength
            : "%copy_length_i64";
        var allocationFailureProfile = _context.GetMetadataTupleRef(["!\"branch_weights\"", "i32 1", "i32 2000"]);
        var nonBucketLabel = IsWindowsTarget()
            ? "os_realloc_check"
            : "fallback";

        builder.AppendLine(
            $"define linkonce_odr hidden nonnull noundef ptr @{RuntimeReallocateHelperName}(ptr %old_ptr, {AllocatorSizeType} noundef %old_size, {AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef allocalign %alignment) unnamed_addr allocsize(2) allockind(\"realloc,aligned\") {RuntimeAllocatorFamilyAttribute} nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %old_is_null = icmp eq ptr %old_ptr, null");
        builder.AppendLine("  br i1 %old_is_null, label %allocate_only, label %check_alignment");
        builder.AppendLine();
        builder.AppendLine("allocate_only:");
        builder.AppendLine($"  %allocated = call noalias nonnull noundef ptr @{RuntimeAllocateHelperName}({AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  ret ptr %allocated");
        builder.AppendLine();
        builder.AppendLine("check_alignment:");
        builder.AppendLine($"  %realloc_alignment_too_small = icmp ult {AllocatorSizeType} %alignment, {pointerSizeBytes}");
        builder.AppendLine($"  %realloc_effective_alignment = select i1 %realloc_alignment_too_small, {AllocatorSizeType} {pointerSizeBytes}, {AllocatorSizeType} %alignment");
        builder.AppendLine($"  %realloc_alignment_minus_one = sub {AllocatorSizeType} %realloc_effective_alignment, 1");
        builder.AppendLine($"  %realloc_alignment_power_check = and {AllocatorSizeType} %realloc_effective_alignment, %realloc_alignment_minus_one");
        builder.AppendLine($"  %realloc_alignment_not_power_of_two = icmp ne {AllocatorSizeType} %realloc_alignment_power_check, 0");
        builder.AppendLine("  br i1 %realloc_alignment_not_power_of_two, label %oom, label %classify_old");
        builder.AppendLine();
        builder.AppendLine("classify_old:");
        builder.AppendLine($"  %realloc_header = getelementptr i8, ptr %old_ptr, i64 -{headerBytes}");
        builder.AppendLine($"  %realloc_bucket_size_slot = getelementptr i8, ptr %realloc_header, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  %realloc_bucket_size = load {AllocatorSizeType}, ptr %realloc_bucket_size_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %realloc_old_is_bucket = icmp ne {AllocatorSizeType} %realloc_bucket_size, 0");
        builder.AppendLine($"  br i1 %realloc_old_is_bucket, label %try_bucket_reuse, label %{nonBucketLabel}");
        builder.AppendLine();
        builder.AppendLine("try_bucket_reuse:");
        builder.AppendLine($"  %realloc_bucket_size_fits = icmp ule {AllocatorSizeType} %new_size, %realloc_bucket_size");
        builder.AppendLine($"  %realloc_bucket_alignment_fits = icmp ule {AllocatorSizeType} %realloc_effective_alignment, {bucketAlignmentBytes}");
        builder.AppendLine("  %realloc_bucket_can_reuse = and i1 %realloc_bucket_size_fits, %realloc_bucket_alignment_fits");
        builder.AppendLine("  br i1 %realloc_bucket_can_reuse, label %reuse_old, label %fallback");
        builder.AppendLine();
        builder.AppendLine("reuse_old:");
        builder.AppendLine("  ret ptr %old_ptr");
        builder.AppendLine();
        if (IsWindowsTarget())
        {
            EmitRuntimeWindowsOsReallocateFastPath(
                builder,
                pointerSizeBytes,
                headerBytes,
                bucketSizeSlotOffset,
                allocationFailureProfile);
            builder.AppendLine();
        }

        builder.AppendLine("fallback:");
        builder.AppendLine($"  %new_ptr = call noalias nonnull noundef ptr @{RuntimeAllocateHelperName}({AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef %realloc_effective_alignment)");
        builder.AppendLine($"  %copy_uses_old = icmp ult {AllocatorSizeType} %old_size, %new_size");
        builder.AppendLine($"  {copyLength} = select i1 %copy_uses_old, {AllocatorSizeType} %old_size, {AllocatorSizeType} %new_size");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {copyLengthI64} = zext {AllocatorSizeType} {copyLength} to i64");
        }

        builder.AppendLine($"  call void @llvm.memcpy.p0.p0.i64(ptr align {pointerSizeBytes} %new_ptr, ptr align {pointerSizeBytes} %old_ptr, i64 {copyLengthI64}, i1 false)");
        builder.AppendLine($"  call void @{RuntimeFreeHelperName}(ptr %old_ptr)");
        builder.AppendLine("  ret ptr %new_ptr");
        builder.AppendLine();
        builder.AppendLine("oom:");
        builder.AppendLine($"  call coldcc void @{OutOfMemoryTrapHelperName}()");
        builder.AppendLine("  unreachable");
        builder.AppendLine("}");
    }

    private void EmitRuntimeTryReallocateHelperDefinition(StringBuilder builder)
    {
        var pointerSizeBytes = GetTargetPointerSizeBytes();
        var bucketAlignmentBytes = GetRuntimeAllocatorBucketAlignmentBytes(pointerSizeBytes);
        var headerBytes = GetRuntimeAllocationHeaderBytes(pointerSizeBytes);
        var bucketSizeSlotOffset = pointerSizeBytes + GetAllocatorSizeBytes();
        var copyLength = "%copy_length";
        var copyLengthI64 = AllocatorSizeType == "i64"
            ? copyLength
            : "%copy_length_i64";
        var allocationFailureProfile = _context.GetMetadataTupleRef(["!\"branch_weights\"", "i32 1", "i32 2000"]);
        var nonBucketLabel = IsWindowsTarget()
            ? "os_realloc_check"
            : "fallback";

        builder.AppendLine(
            $"define linkonce_odr hidden ptr @{RuntimeTryReallocateHelperName}(ptr %old_ptr, {AllocatorSizeType} noundef %old_size, {AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef allocalign %alignment) unnamed_addr allocsize(2) allockind(\"realloc,aligned\") {RuntimeAllocatorFamilyAttribute} nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %old_is_null = icmp eq ptr %old_ptr, null");
        builder.AppendLine("  br i1 %old_is_null, label %allocate_only, label %check_alignment");
        builder.AppendLine();
        builder.AppendLine("allocate_only:");
        builder.AppendLine($"  %allocated = call noalias noundef ptr @{RuntimeTryAllocateHelperName}({AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef %alignment)");
        builder.AppendLine("  ret ptr %allocated");
        builder.AppendLine();
        builder.AppendLine("check_alignment:");
        builder.AppendLine($"  %realloc_alignment_too_small = icmp ult {AllocatorSizeType} %alignment, {pointerSizeBytes}");
        builder.AppendLine($"  %realloc_effective_alignment = select i1 %realloc_alignment_too_small, {AllocatorSizeType} {pointerSizeBytes}, {AllocatorSizeType} %alignment");
        builder.AppendLine($"  %realloc_alignment_minus_one = sub {AllocatorSizeType} %realloc_effective_alignment, 1");
        builder.AppendLine($"  %realloc_alignment_power_check = and {AllocatorSizeType} %realloc_effective_alignment, %realloc_alignment_minus_one");
        builder.AppendLine($"  %realloc_alignment_not_power_of_two = icmp ne {AllocatorSizeType} %realloc_alignment_power_check, 0");
        builder.AppendLine("  br i1 %realloc_alignment_not_power_of_two, label %failed, label %classify_old");
        builder.AppendLine();
        builder.AppendLine("classify_old:");
        builder.AppendLine($"  %realloc_header = getelementptr i8, ptr %old_ptr, i64 -{headerBytes}");
        builder.AppendLine($"  %realloc_bucket_size_slot = getelementptr i8, ptr %realloc_header, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  %realloc_bucket_size = load {AllocatorSizeType}, ptr %realloc_bucket_size_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %realloc_old_is_bucket = icmp ne {AllocatorSizeType} %realloc_bucket_size, 0");
        builder.AppendLine($"  br i1 %realloc_old_is_bucket, label %try_bucket_reuse, label %{nonBucketLabel}");
        builder.AppendLine();
        builder.AppendLine("try_bucket_reuse:");
        builder.AppendLine($"  %realloc_bucket_size_fits = icmp ule {AllocatorSizeType} %new_size, %realloc_bucket_size");
        builder.AppendLine($"  %realloc_bucket_alignment_fits = icmp ule {AllocatorSizeType} %realloc_effective_alignment, {bucketAlignmentBytes}");
        builder.AppendLine("  %realloc_bucket_can_reuse = and i1 %realloc_bucket_size_fits, %realloc_bucket_alignment_fits");
        builder.AppendLine("  br i1 %realloc_bucket_can_reuse, label %reuse_old, label %fallback");
        builder.AppendLine();
        builder.AppendLine("reuse_old:");
        builder.AppendLine("  ret ptr %old_ptr");
        builder.AppendLine();
        if (IsWindowsTarget())
        {
            EmitRuntimeWindowsOsReallocateFastPath(
                builder,
                pointerSizeBytes,
                headerBytes,
                bucketSizeSlotOffset,
                allocationFailureProfile);
            builder.AppendLine();
        }

        builder.AppendLine("fallback:");
        builder.AppendLine($"  %new_ptr = call noalias noundef ptr @{RuntimeTryAllocateHelperName}({AllocatorSizeType} noundef %new_size, {AllocatorSizeType} noundef %realloc_effective_alignment)");
        builder.AppendLine("  %new_ptr_is_null = icmp eq ptr %new_ptr, null");
        builder.AppendLine("  br i1 %new_ptr_is_null, label %failed, label %copy");
        builder.AppendLine();
        builder.AppendLine("copy:");
        builder.AppendLine($"  %copy_uses_old = icmp ult {AllocatorSizeType} %old_size, %new_size");
        builder.AppendLine($"  {copyLength} = select i1 %copy_uses_old, {AllocatorSizeType} %old_size, {AllocatorSizeType} %new_size");
        if (AllocatorSizeType != "i64")
        {
            builder.AppendLine($"  {copyLengthI64} = zext {AllocatorSizeType} {copyLength} to i64");
        }

        builder.AppendLine($"  call void @llvm.memcpy.p0.p0.i64(ptr align {pointerSizeBytes} %new_ptr, ptr align {pointerSizeBytes} %old_ptr, i64 {copyLengthI64}, i1 false)");
        builder.AppendLine($"  call void @{RuntimeFreeHelperName}(ptr %old_ptr)");
        builder.AppendLine("  ret ptr %new_ptr");
        builder.AppendLine();
        builder.AppendLine("failed:");
        builder.AppendLine("  ret ptr null");
        builder.AppendLine("}");
    }

    private void EmitRuntimeWindowsOsReallocateFastPath(
        StringBuilder builder,
        int pointerSizeBytes,
        int headerBytes,
        int bucketSizeSlotOffset,
        string allocationFailureProfile)
    {
        builder.AppendLine("os_realloc_check:");
        builder.AppendLine($"  %realloc_base = load ptr, ptr %realloc_header, align {pointerSizeBytes}");
        builder.AppendLine("  %realloc_header_is_base = icmp eq ptr %realloc_base, %realloc_header");
        builder.AppendLine($"  %realloc_os_alignment_ok = icmp ule {AllocatorSizeType} %realloc_effective_alignment, {pointerSizeBytes}");
        builder.AppendLine("  %realloc_can_os_realloc = and i1 %realloc_header_is_base, %realloc_os_alignment_ok");
        builder.AppendLine("  br i1 %realloc_can_os_realloc, label %try_os_reallocate, label %fallback");
        builder.AppendLine();
        builder.AppendLine("try_os_reallocate:");
        builder.AppendLine($"  %os_realloc_size_is_zero = icmp eq {AllocatorSizeType} %new_size, 0");
        builder.AppendLine($"  %os_realloc_requested_size = select i1 %os_realloc_size_is_zero, {AllocatorSizeType} 1, {AllocatorSizeType} %new_size");
        builder.AppendLine($"  %os_with_header = add {AllocatorSizeType} %os_realloc_requested_size, {headerBytes}");
        builder.AppendLine($"  %os_overflow_header = icmp ult {AllocatorSizeType} %os_with_header, %os_realloc_requested_size");
        builder.AppendLine($"  %os_total = add {AllocatorSizeType} %os_with_header, %realloc_effective_alignment");
        builder.AppendLine($"  %os_overflow_alignment = icmp ult {AllocatorSizeType} %os_total, %os_with_header");
        builder.AppendLine("  %os_size_overflow = or i1 %os_overflow_header, %os_overflow_alignment");
        builder.AppendLine("  br i1 %os_size_overflow, label %fallback, label %call_os_reallocate");
        builder.AppendLine();
        builder.AppendLine("call_os_reallocate:");
        builder.AppendLine($"  %os_realloc_base = call noundef ptr @{OsReallocateHelperName}(ptr %realloc_base, {AllocatorSizeType} noundef %os_total)");
        builder.AppendLine("  %os_realloc_failed = icmp eq ptr %os_realloc_base, null");
        builder.AppendLine($"  br i1 %os_realloc_failed, label %fallback, label %os_reallocated, !prof {allocationFailureProfile}");
        builder.AppendLine();
        builder.AppendLine("os_reallocated:");
        builder.AppendLine($"  store ptr %os_realloc_base, ptr %os_realloc_base, align {pointerSizeBytes}");
        builder.AppendLine($"  %os_length_slot = getelementptr i8, ptr %os_realloc_base, i64 {pointerSizeBytes}");
        builder.AppendLine($"  store {AllocatorSizeType} %os_total, ptr %os_length_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %os_bucket_size_slot = getelementptr i8, ptr %os_realloc_base, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  store {AllocatorSizeType} 0, ptr %os_bucket_size_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %os_realloc_ptr = getelementptr i8, ptr %os_realloc_base, i64 {headerBytes}");
        builder.AppendLine("  ret ptr %os_realloc_ptr");
    }

    private void EmitRuntimeFreeHelperDefinition(StringBuilder builder)
    {
        var pointerSizeBytes = GetTargetPointerSizeBytes();
        var bucketAlignmentBytes = GetRuntimeAllocatorBucketAlignmentBytes(pointerSizeBytes);
        var headerBytes = GetRuntimeAllocationHeaderBytes(pointerSizeBytes);
        var bucketSizeSlotOffset = pointerSizeBytes + GetAllocatorSizeBytes();

        builder.AppendLine($"define linkonce_odr hidden void @{RuntimeFreeHelperName}(ptr %ptr) unnamed_addr allockind(\"free\") {RuntimeAllocatorFamilyAttribute} nounwind comdat {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %is_null = icmp eq ptr %ptr, null");
        builder.AppendLine("  br i1 %is_null, label %done, label %free");
        builder.AppendLine();
        builder.AppendLine("free:");
        builder.AppendLine($"  %header = getelementptr i8, ptr %ptr, i64 -{headerBytes}");
        builder.AppendLine($"  %bucket_size_slot = getelementptr i8, ptr %header, i64 {bucketSizeSlotOffset}");
        builder.AppendLine($"  %bucket_size = load {AllocatorSizeType}, ptr %bucket_size_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  %is_bucket = icmp ne {AllocatorSizeType} %bucket_size, 0");
        builder.AppendLine("  br i1 %is_bucket, label %bucket_free_select_16, label %free_os");
        builder.AppendLine();

        for (var index = 0; index < RuntimeAllocatorBucketSizes.Length; index++)
        {
            var bucketSize = RuntimeAllocatorBucketSizes[index];
            var nextLabel = index + 1 < RuntimeAllocatorBucketSizes.Length
                ? $"bucket_free_select_{RuntimeAllocatorBucketSizes[index + 1]}"
                : "free_os";
            builder.AppendLine($"bucket_free_select_{bucketSize}:");
            builder.AppendLine($"  %bucket_is_{bucketSize} = icmp eq {AllocatorSizeType} %bucket_size, {bucketSize}");
            builder.AppendLine($"  br i1 %bucket_is_{bucketSize}, label %bucket_{bucketSize}_push, label %{nextLabel}");
            builder.AppendLine();
        }

        foreach (var bucketSize in RuntimeAllocatorBucketSizes)
        {
            var bucketGlobalName = GetRuntimeAllocatorBucketGlobalName(bucketSize);
            builder.AppendLine($"bucket_{bucketSize}_push:");
            builder.AppendLine($"  %bucket_head_{bucketSize} = load ptr, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
            builder.AppendLine($"  store ptr %bucket_head_{bucketSize}, ptr %ptr, align {bucketAlignmentBytes}");
            builder.AppendLine($"  store ptr %ptr, ptr @{bucketGlobalName}, align {pointerSizeBytes}");
            builder.AppendLine("  br label %done");
            builder.AppendLine();
        }

        builder.AppendLine("free_os:");
        builder.AppendLine($"  %base = load ptr, ptr %header, align {pointerSizeBytes}");
        builder.AppendLine($"  %length_slot = getelementptr i8, ptr %header, i64 {pointerSizeBytes}");
        builder.AppendLine($"  %total = load {AllocatorSizeType}, ptr %length_slot, align {pointerSizeBytes}");
        builder.AppendLine($"  call void @{OsFreeHelperName}(ptr %base, {AllocatorSizeType} noundef %total)");
        builder.AppendLine("  br label %done");
        builder.AppendLine();
        builder.AppendLine("done:");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private void EmitOsAllocateHelperDefinition(StringBuilder builder)
    {
        if (IsWindowsTarget())
        {
            builder.AppendLine($"define internal dso_local noalias noundef ptr @{OsAllocateHelperName}({AllocatorSizeType} noundef %size) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized\") {OsAllocatorFamilyAttribute} nounwind {{");
            builder.AppendLine("entry:");
            builder.AppendLine("  %heap = call ptr @GetProcessHeap()");
            builder.AppendLine($"  %ptr = call noalias noundef ptr @HeapAlloc(ptr %heap, i32 0, {AllocatorSizeType} noundef %size)");
            builder.AppendLine("  ret ptr %ptr");
            builder.AppendLine("}");
            return;
        }

        if (IsLinuxTarget())
        {
            EmitLinuxOsAllocateHelperDefinition(builder);
            return;
        }

        EmitUnsupportedOsAllocateHelperDefinition(builder);
    }

    private void EmitOsReallocateHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine($"define internal dso_local noundef ptr @{OsReallocateHelperName}(ptr %ptr, {AllocatorSizeType} noundef %size) unnamed_addr allocsize(1) allockind(\"realloc\") {OsAllocatorFamilyAttribute} nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  %heap = call ptr @GetProcessHeap()");
        builder.AppendLine($"  %next = call noundef ptr @HeapReAlloc(ptr %heap, i32 0, ptr %ptr, {AllocatorSizeType} noundef %size)");
        builder.AppendLine("  ret ptr %next");
        builder.AppendLine("}");
    }

    private void EmitOsFreeHelperDefinition(StringBuilder builder)
    {
        if (IsWindowsTarget())
        {
            builder.AppendLine($"define internal dso_local void @{OsFreeHelperName}(ptr %ptr, {AllocatorSizeType} noundef %size) unnamed_addr allockind(\"free\") {OsAllocatorFamilyAttribute} nounwind {{");
            builder.AppendLine("entry:");
            builder.AppendLine("  %heap = call ptr @GetProcessHeap()");
            builder.AppendLine("  %ignored = call i32 @HeapFree(ptr %heap, i32 0, ptr %ptr)");
            builder.AppendLine("  ret void");
            builder.AppendLine("}");
            return;
        }

        if (IsLinuxTarget())
        {
            EmitLinuxOsFreeHelperDefinition(builder);
            return;
        }

        EmitUnsupportedOsFreeHelperDefinition(builder);
    }

    private void EmitLinuxOsAllocateHelperDefinition(StringBuilder builder)
    {
        if (!TryGetLinuxAllocatorSyscallSpec(out var syscallSpec))
        {
            EmitUnsupportedOsAllocateHelperDefinition(builder);
            return;
        }

        builder.AppendLine($"define internal dso_local noalias noundef ptr @{OsAllocateHelperName}({AllocatorSizeType} noundef %size) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized\") {OsAllocatorFamilyAttribute} nounwind {{");
        builder.AppendLine("entry:");
        var sizeValue = syscallSpec.ValueBitWidth == 64
            ? MaterializeAllocatorSizeAsI64(builder, "%size", "size64")
            : MaterializeAllocatorSizeAsI32WithBounds(builder, "%size", "size32", "too_large", "syscall", returnVoidOnTooLarge: false);
        EmitLinuxSyscall6(
            builder,
            "%mmap_result",
            syscallSpec,
            syscallSpec.MmapNumber,
            "0",
            sizeValue,
            "3",
            "34",
            "-1",
            "0");
        builder.AppendLine($"  %is_error = icmp uge {syscallSpec.ValueType} %mmap_result, -4095");
        builder.AppendLine($"  %base = inttoptr {syscallSpec.ValueType} %mmap_result to ptr");
        builder.AppendLine("  %result = select i1 %is_error, ptr null, ptr %base");
        builder.AppendLine("  ret ptr %result");
        builder.AppendLine("}");
    }

    private void EmitLinuxOsFreeHelperDefinition(StringBuilder builder)
    {
        if (!TryGetLinuxAllocatorSyscallSpec(out var syscallSpec))
        {
            EmitUnsupportedOsFreeHelperDefinition(builder);
            return;
        }

        builder.AppendLine($"define internal dso_local void @{OsFreeHelperName}(ptr %ptr, {AllocatorSizeType} noundef %size) unnamed_addr allockind(\"free\") {OsAllocatorFamilyAttribute} nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine($"  %ptr_int = ptrtoint ptr %ptr to {syscallSpec.ValueType}");
        var sizeValue = syscallSpec.ValueBitWidth == 64
            ? MaterializeAllocatorSizeAsI64(builder, "%size", "size64")
            : MaterializeAllocatorSizeAsI32WithBounds(builder, "%size", "size32", "too_large", "syscall", returnVoidOnTooLarge: true);
        EmitLinuxSyscall2(
            builder,
            "%munmap_result",
            syscallSpec,
            syscallSpec.MunmapNumber,
            "%ptr_int",
            sizeValue);
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private void EmitUnsupportedOsAllocateHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine($"define internal dso_local ptr @{OsAllocateHelperName}({AllocatorSizeType} noundef %size) unnamed_addr nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  ret ptr null");
        builder.AppendLine("}");
    }

    private void EmitUnsupportedOsFreeHelperDefinition(StringBuilder builder)
    {
        builder.AppendLine($"define internal dso_local void @{OsFreeHelperName}(ptr %ptr, {AllocatorSizeType} noundef %size) unnamed_addr nounwind {{");
        builder.AppendLine("entry:");
        builder.AppendLine("  ret void");
        builder.AppendLine("}");
    }

    private string MaterializeAllocatorSizeAsI64(StringBuilder builder, string value, string localName)
    {
        if (AllocatorSizeType == "i64")
        {
            return value;
        }

        var converted = $"%{localName}";
        builder.AppendLine($"  {converted} = zext {AllocatorSizeType} {value} to i64");
        return converted;
    }

    private string MaterializeAllocatorSizeAsI32WithBounds(
        StringBuilder builder,
        string value,
        string localName,
        string tooLargeLabel,
        string okLabel,
        bool returnVoidOnTooLarge)
    {
        if (AllocatorSizeType == "i32")
        {
            return value;
        }

        builder.AppendLine($"  %allocator_size_too_large = icmp ugt {AllocatorSizeType} {value}, 4294967295");
        builder.AppendLine($"  br i1 %allocator_size_too_large, label %{tooLargeLabel}, label %{okLabel}");
        builder.AppendLine();
        builder.AppendLine($"{tooLargeLabel}:");
        builder.AppendLine(returnVoidOnTooLarge ? "  ret void" : "  ret ptr null");
        builder.AppendLine();
        builder.AppendLine($"{okLabel}:");
        var converted = $"%{localName}";
        builder.AppendLine($"  {converted} = trunc {AllocatorSizeType} {value} to i32");
        return converted;
    }

    private static void EmitLinuxSyscall2(
        StringBuilder builder,
        string resultName,
        LinuxAllocatorSyscallSpec syscallSpec,
        long syscallNumber,
        string arg1,
        string arg2)
    {
        EmitLinuxSyscall6(builder, resultName, syscallSpec, syscallNumber, arg1, arg2, "0", "0", "0", "0");
    }

    private static void EmitLinuxSyscall6(
        StringBuilder builder,
        string resultName,
        LinuxAllocatorSyscallSpec syscallSpec,
        long syscallNumber,
        string arg1,
        string arg2,
        string arg3,
        string arg4,
        string arg5,
        string arg6)
    {
        var valueType = syscallSpec.ValueType;
        builder.AppendLine(
            $"  {resultName} = call {valueType} asm sideeffect \"{syscallSpec.Template}\", \"{syscallSpec.Constraints}\"({valueType} {syscallNumber}, {valueType} {arg1}, {valueType} {arg2}, {valueType} {arg3}, {valueType} {arg4}, {valueType} {arg5}, {valueType} {arg6})");
    }

    private bool TryGetLinuxAllocatorSyscallSpec(out LinuxAllocatorSyscallSpec syscallSpec)
    {
        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(TargetInfo);
        syscallSpec = architecture switch
        {
            StarkAsmArchitecture.X86_64 => new LinuxAllocatorSyscallSpec(
                MmapNumber: 9,
                MunmapNumber: 11,
                ValueBitWidth: 64,
                Template: "syscall",
                Constraints: "={rax},0,{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}"),
            StarkAsmArchitecture.AArch64 => new LinuxAllocatorSyscallSpec(
                MmapNumber: 222,
                MunmapNumber: 215,
                ValueBitWidth: 64,
                Template: "svc #0",
                Constraints: "={x0},{x8},0,{x1},{x2},{x3},{x4},{x5},~{memory}"),
            StarkAsmArchitecture.RiscV64 => new LinuxAllocatorSyscallSpec(
                MmapNumber: 222,
                MunmapNumber: 215,
                ValueBitWidth: 64,
                Template: "ecall",
                Constraints: "={a0},{a7},0,{a1},{a2},{a3},{a4},{a5},~{memory}"),
            StarkAsmArchitecture.X86 => new LinuxAllocatorSyscallSpec(
                MmapNumber: 192,
                MunmapNumber: 91,
                ValueBitWidth: 32,
                Template: "int $$0x80",
                Constraints: "={eax},0,{ebx},{ecx},{edx},{esi},{edi},{ebp},~{memory},~{dirflag},~{fpsr},~{flags}"),
            StarkAsmArchitecture.Arm32 => new LinuxAllocatorSyscallSpec(
                MmapNumber: 192,
                MunmapNumber: 91,
                ValueBitWidth: 32,
                Template: "svc #0",
                Constraints: "={r0},{r7},0,{r1},{r2},{r3},{r4},{r5},~{memory}"),
            _ => default
        };

        return syscallSpec.Template is not null;
    }

    private int GetTargetPointerSizeBytes()
    {
        if (TryGetConcreteTypeLayout(StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false)) is { SizeBytes: > 0 } layout)
        {
            return layout.SizeBytes;
        }

        if (TryGetPointerSizeBytesFromDataLayout(TargetInfo, out var pointerSizeBytes))
        {
            return pointerSizeBytes;
        }

        return StarkAsmArchitectureFacts.ResolveActiveArchitecture(TargetInfo) switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 8,
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 4,
            _ => IntPtr.Size
        };
    }

    private int GetRuntimeAllocationHeaderBytes(int pointerSizeBytes)
    {
        return checked(pointerSizeBytes + (GetAllocatorSizeBytes() * 2));
    }

    private static int GetRuntimeAllocatorBucketAlignmentBytes(int pointerSizeBytes)
    {
        return Math.Max(pointerSizeBytes, RuntimeAllocatorBucketAlignmentBytes);
    }

    private static int GetRuntimeAllocatorBucketStrideBytes(int bucketSize, int headerBytes, int bucketAlignmentBytes)
    {
        return AlignUp(checked(headerBytes + bucketSize), bucketAlignmentBytes);
    }

    private static int GetRuntimeAllocatorSlabBlockCount(
        int bucketSize,
        int headerBytes,
        int bucketAlignmentBytes,
        int strideBytes)
    {
        var fixedBytes = checked(headerBytes + (bucketAlignmentBytes - 1) + bucketSize);
        if (fixedBytes >= RuntimeAllocatorSlabTargetBytes)
        {
            return RuntimeAllocatorMinimumSlabBlockCount;
        }

        var pageFitBlockCount = 1 + ((RuntimeAllocatorSlabTargetBytes - fixedBytes) / strideBytes);
        return Math.Max(RuntimeAllocatorMinimumSlabBlockCount, pageFitBlockCount);
    }

    private static int GetRuntimeAllocatorSlabTotalBytes(
        int bucketSize,
        int headerBytes,
        int bucketAlignmentBytes,
        int strideBytes,
        int slabBlockCount)
    {
        return checked(headerBytes
            + (bucketAlignmentBytes - 1)
            + ((slabBlockCount - 1) * strideBytes)
            + bucketSize);
    }

    private static int AlignUp(int value, int alignment)
    {
        return checked(((value + alignment - 1) / alignment) * alignment);
    }

    private static string GetRuntimeAllocatorBucketGlobalName(int bucketSize)
    {
        return $"__stark_alloc_bucket_{bucketSize}";
    }

    private int GetAllocatorSizeBytes()
    {
        if (AllocatorSizeType.Length > 1
            && AllocatorSizeType[0] == 'i'
            && int.TryParse(AllocatorSizeType[1..], out var bitWidth)
            && bitWidth > 0)
        {
            return (bitWidth + 7) / 8;
        }

        throw new InvalidOperationException($"Unsupported allocator size type '{AllocatorSizeType}'.");
    }

    private static bool TryGetPointerSizeBytesFromDataLayout(LlvmTargetInfo? targetInfo, out int pointerSizeBytes)
    {
        pointerSizeBytes = 0;
        var dataLayout = targetInfo?.DataLayout;
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return false;
        }

        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("p:", StringComparison.Ordinal)
                && !token.StartsWith("p0:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var sizeBits))
            {
                continue;
            }

            pointerSizeBytes = (sizeBits + 7) / 8;
            return pointerSizeBytes > 0;
        }

        return false;
    }

    private bool IsLinuxTarget()
    {
        var triple = TargetInfo?.Triple;
        if (!string.IsNullOrWhiteSpace(triple))
        {
            return triple.Contains("linux", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsLinux();
    }

    private bool IsWindowsTarget()
    {
        var triple = TargetInfo?.Triple;
        if (!string.IsNullOrWhiteSpace(triple))
        {
            return triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("msvc", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsWindows();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectTextEqualityTypes()
    {
        return _enumerateBinaryOperations()
            .Where(static binary => binary.Operator is SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual)
            .Select(static binary => binary.Left.Type)
            .Select(NormalizeAggregateType)
            .Where(static type => type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            .Distinct()
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectTextOrderedComparisonTypes()
    {
        return _enumerateBinaryOperations()
            .Where(static binary => binary.Operator is
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual)
            .Select(static binary => binary.Left.Type)
            .Select(NormalizeAggregateType)
            .Where(static type => type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            .Distinct()
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<int> CollectIntegerExponentBitWidths()
    {
        return _enumerateBinaryOperations()
            .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Integer && binary.Type.BitWidth is int)
            .Select(static binary => binary.Type.BitWidth!.Value)
            .Distinct()
            .OrderBy(static bitWidth => bitWidth)
            .ToArray();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectFixedArrayOrderedComparisonTypes()
    {
        var collected = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var binary in _enumerateBinaryOperations())
        {
            if (binary.Type.Kind != StarkTypeKind.Bool)
            {
                continue;
            }

            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                continue;
            }

            CollectFixedArrayOrderedComparisonTypes(binary.Left.Type, collected);
        }

        return collected.Values
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<StarkTypeSymbol> CollectScalarizedNamedAggregateOrderedComparisonTypes()
    {
        var collected = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var binary in _enumerateBinaryOperations())
        {
            if (binary.Type.Kind != StarkTypeKind.Bool)
            {
                continue;
            }

            if (binary.Operator is not (
                    SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual))
            {
                continue;
            }

            CollectScalarizedNamedAggregateOrderedComparisonTypes(binary.Left.Type, collected);
        }

        return collected.Values
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private void CollectFixedArrayOrderedComparisonTypes(
        StarkTypeSymbol type,
        Dictionary<string, StarkTypeSymbol> collected)
    {
        if (type.Kind != StarkTypeKind.FixedArray
            || type.ElementType is null
            || type.FixedLength is not int)
        {
            return;
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(type);
        if (!collected.TryAdd(helperName, type))
        {
            return;
        }

        CollectFixedArrayOrderedComparisonTypes(type.ElementType, collected);
    }

    private void CollectScalarizedNamedAggregateOrderedComparisonTypes(
        StarkTypeSymbol type,
        Dictionary<string, StarkTypeSymbol> collected)
    {
        if (type.Kind != StarkTypeKind.Named
            || !SupportsScalarizedAggregateOrderedComparison(type)
            || !TryGetScalarizableAggregateLeaves(
                type,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out _))
        {
            return;
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(type);
        collected.TryAdd(helperName, type);
    }

    private bool SupportsScalarizedAggregateOrderedComparison(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && namedType.EnumVariants is { Count: > 0 })),
            _ => false
        };
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

                    var sizeBytes = 0;
                    var alignmentBytes = 1;
                    for (var index = 0; index < orderedFields.Count; index++)
                    {
                        var field = orderedFields[index];
                        var fieldLayout = TryGetConcreteTypeLayout(NormalizeAggregateType(field.Type));
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
        var step = 0;

        foreach (var index in indices)
        {
            var nextType = GetAggregateElementType(currentType, index)
                ?? throw new UnsupportedBodyEmissionException(
                    $"Cannot extract aggregate leaf for '{rootType.DisplayName}'.");
            var extracted = $"%{EscapeIdentifier($"{purpose}_{step++}")}";
            builder.AppendLine($"  {extracted} = extractvalue {MapType(currentType)} {currentValue}, {index}");
            currentValue = extracted;
            currentType = NormalizeAggregateType(nextType);
        }

        return currentValue;
    }

    private void EmitTextEqualityHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol textType,
        string helperName)
    {
        var textLlvmType = MapType(textType);
        var unitType = textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new InvalidOperationException($"Text equality helper requires an ascii/unicode type, but found '{textType.DisplayName}'.")
        };
        var unitLlvmType = MapType(unitType);

        builder.AppendLine(BuildInternalAddressInsensitiveHelperSignature(
            "i1",
            helperName,
            $"{textLlvmType} %left, {textLlvmType} %right"));
        builder.AppendLine("entry:");
        builder.AppendLine($"  %left_data = extractvalue {textLlvmType} %left, 0");
        builder.AppendLine($"  %left_length = extractvalue {textLlvmType} %left, 1");
        builder.AppendLine($"  %right_data = extractvalue {textLlvmType} %right, 0");
        builder.AppendLine($"  %right_length = extractvalue {textLlvmType} %right, 1");
        builder.AppendLine("  %length_equal = icmp eq i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_equal, label %loop_header, label %return_false");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine("  %textcmp_index = phi i64 [ 0, %entry ], [ %textcmp_next, %loop_continue ]");
        builder.AppendLine("  %textcmp_done = icmp eq i64 %textcmp_index, %left_length");
        builder.AppendLine("  br i1 %textcmp_done, label %return_true, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %left_unit_ptr = getelementptr{GetProvenInObjectGepFlags()} {unitLlvmType}, ptr %left_data, i64 %textcmp_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr{GetProvenInObjectGepFlags()} {unitLlvmType}, ptr %right_data, i64 %textcmp_index");
        builder.AppendLine($"  %left_unit = load {unitLlvmType}, ptr %left_unit_ptr");
        builder.AppendLine($"  %right_unit = load {unitLlvmType}, ptr %right_unit_ptr");
        builder.AppendLine($"  %unit_equal = icmp eq {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_equal, label %loop_continue, label %return_false");
        builder.AppendLine();
        builder.AppendLine("loop_continue:");
        builder.AppendLine("  %textcmp_next = add i64 %textcmp_index, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("return_false:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine();
        builder.AppendLine("return_true:");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("}");
    }

    private void EmitTextComparisonHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol textType,
        string helperName)
    {
        var textLlvmType = MapType(textType);
        var unitType = textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new InvalidOperationException($"Text comparison helper requires an ascii/unicode type, but found '{textType.DisplayName}'.")
        };
        var unitLlvmType = MapType(unitType);

        builder.AppendLine(BuildInternalAddressInsensitiveHelperSignature(
            "i32",
            helperName,
            $"{textLlvmType} %left, {textLlvmType} %right"));
        builder.AppendLine("entry:");
        builder.AppendLine($"  %left_data = extractvalue {textLlvmType} %left, 0");
        builder.AppendLine($"  %left_length = extractvalue {textLlvmType} %left, 1");
        builder.AppendLine($"  %right_data = extractvalue {textLlvmType} %right, 0");
        builder.AppendLine($"  %right_length = extractvalue {textLlvmType} %right, 1");
        builder.AppendLine("  %left_shorter = icmp ult i64 %left_length, %right_length");
        builder.AppendLine("  %min_length = select i1 %left_shorter, i64 %left_length, i64 %right_length");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine("  %textord_index = phi i64 [ 0, %entry ], [ %textord_next, %loop_continue ]");
        builder.AppendLine("  %textord_done = icmp eq i64 %textord_index, %min_length");
        builder.AppendLine("  br i1 %textord_done, label %length_compare, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %left_unit_ptr = getelementptr{GetProvenInObjectGepFlags()} {unitLlvmType}, ptr %left_data, i64 %textord_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr{GetProvenInObjectGepFlags()} {unitLlvmType}, ptr %right_data, i64 %textord_index");
        builder.AppendLine($"  %left_unit = load {unitLlvmType}, ptr %left_unit_ptr");
        builder.AppendLine($"  %right_unit = load {unitLlvmType}, ptr %right_unit_ptr");
        builder.AppendLine($"  %unit_less = icmp ult {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_less, label %return_less, label %check_greater");
        builder.AppendLine();
        builder.AppendLine("check_greater:");
        builder.AppendLine($"  %unit_greater = icmp ugt {unitLlvmType} %left_unit, %right_unit");
        builder.AppendLine("  br i1 %unit_greater, label %return_greater, label %loop_continue");
        builder.AppendLine();
        builder.AppendLine("loop_continue:");
        builder.AppendLine("  %textord_next = add i64 %textord_index, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("length_compare:");
        builder.AppendLine("  %length_equal = icmp eq i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_equal, label %return_equal, label %length_decide");
        builder.AppendLine();
        builder.AppendLine("length_decide:");
        builder.AppendLine("  %length_less = icmp ult i64 %left_length, %right_length");
        builder.AppendLine("  br i1 %length_less, label %return_less, label %return_greater");
        builder.AppendLine();
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine();
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine();
        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("}");
    }

    private void EmitFixedArrayOrderedComparisonHelperDefinition(StringBuilder builder, StarkTypeSymbol fixedArrayType)
    {
        if (fixedArrayType.Kind != StarkTypeKind.FixedArray
            || fixedArrayType.ElementType is null
            || fixedArrayType.FixedLength is not int fixedLength)
        {
            throw new InvalidOperationException($"Fixed-array ordered comparison helper requires a fixed array type, but found '{fixedArrayType.DisplayName}'.");
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(fixedArrayType);
        var arrayLlvmType = MapType(fixedArrayType);

        builder.AppendLine(BuildInternalAddressInsensitiveHelperSignature(
            "i32",
            helperName,
            $"{arrayLlvmType} %left, {arrayLlvmType} %right"));
        builder.AppendLine("entry:");
        if (fixedLength == 0)
        {
            builder.AppendLine("  ret i32 0");
            builder.AppendLine("}");
            return;
        }

        builder.AppendLine("  br label %compare_0");

        for (var index = 0; index < fixedLength; index++)
        {
            EmitFixedArrayOrderedComparisonElement(
                builder,
                fixedArrayType,
                index,
                index == fixedLength - 1);
        }

        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine("}");
    }

    private void EmitScalarizedNamedAggregateOrderedComparisonHelperDefinition(
        StringBuilder builder,
        StarkTypeSymbol aggregateType)
    {
        if (aggregateType.Kind != StarkTypeKind.Named
            || !SupportsScalarizedAggregateOrderedComparison(aggregateType))
        {
            throw new InvalidOperationException(
                $"Named aggregate ordered comparison helper requires a scalarizable named aggregate type, but found '{aggregateType.DisplayName}'.");
        }

        if (!TryGetScalarizableAggregateLeaves(
                aggregateType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out var leaves))
        {
            throw new InvalidOperationException(
                $"Named aggregate ordered comparison helper requires a scalarizable aggregate shape for '{aggregateType.DisplayName}'.");
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(aggregateType);
        var aggregateLlvmType = MapType(aggregateType);

        builder.AppendLine(BuildInternalAddressInsensitiveHelperSignature(
            "i32",
            helperName,
            $"{aggregateLlvmType} %left, {aggregateLlvmType} %right"));
        builder.AppendLine("entry:");
        if (leaves.Count == 0)
        {
            builder.AppendLine("  ret i32 0");
            builder.AppendLine("}");
            return;
        }

        builder.AppendLine("  br label %compare_0");

        for (var index = 0; index < leaves.Count; index++)
        {
            EmitScalarizedNamedAggregateOrderedComparisonLeaf(
                builder,
                aggregateType,
                leaves[index],
                index,
                index == leaves.Count - 1);
        }

        builder.AppendLine("return_equal:");
        builder.AppendLine("  ret i32 0");
        builder.AppendLine("return_less:");
        builder.AppendLine("  ret i32 -1");
        builder.AppendLine("return_greater:");
        builder.AppendLine("  ret i32 1");
        builder.AppendLine("}");
    }

    private void EmitScalarizedNamedAggregateOrderedComparisonLeaf(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        AggregateScalarLeaf leaf,
        int index,
        bool isLastElement)
    {
        var compareBlock = $"compare_{index}";
        var checkGreaterBlock = $"check_greater_{index}";
        var nextBlock = isLastElement ? "return_equal" : $"compare_{index + 1}";

        builder.AppendLine();
        builder.AppendLine($"{compareBlock}:");
        var leftValue = EmitAggregateLeafValueExtraction(builder, rootType, "%left", leaf.Indices, $"namedcmp_left_{index}");
        var rightValue = EmitAggregateLeafValueExtraction(builder, rootType, "%right", leaf.Indices, $"namedcmp_right_{index}");

        if (TryEmitOrderedComparisonValue(
                builder,
                leaf.Type,
                leftValue,
                rightValue,
                index,
                checkGreaterBlock,
                nextBlock))
        {
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported ordered comparison leaf type '{leaf.Type.DisplayName}' in named aggregate helper.");
    }

    private void EmitFixedArrayOrderedComparisonElement(
        StringBuilder builder,
        StarkTypeSymbol rootType,
        int index,
        bool isLastElement)
    {
        var elementType = rootType.ElementType
            ?? throw new InvalidOperationException($"Fixed-array ordered comparison helper requires a comparable element at index {index} for '{rootType.DisplayName}'.");
        var compareBlock = $"compare_{index}";
        var checkGreaterBlock = $"check_greater_{index}";
        var nextBlock = isLastElement ? "return_equal" : $"compare_{index + 1}";

        builder.AppendLine();
        builder.AppendLine($"{compareBlock}:");
        builder.AppendLine($"  %fixedcmp_left_{index} = extractvalue {MapType(rootType)} %left, {index}");
        builder.AppendLine($"  %fixedcmp_right_{index} = extractvalue {MapType(rootType)} %right, {index}");

        if (TryEmitOrderedComparisonValue(
                builder,
                elementType,
                $"%fixedcmp_left_{index}",
                $"%fixedcmp_right_{index}",
                index,
                checkGreaterBlock,
                nextBlock))
        {
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported ordered comparison element type '{elementType.DisplayName}' in fixed-array helper.");
    }

    private bool TryEmitOrderedComparisonValue(
        StringBuilder builder,
        StarkTypeSymbol operandType,
        string left,
        string right,
        int index,
        string checkGreaterBlock,
        string nextBlock)
    {
        switch (operandType.Kind)
        {
            case StarkTypeKind.Integer when operandType.BitWidth is not null:
                {
                    builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt {MapType(operandType)} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt {MapType(operandType)} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                    return true;
                }
            case StarkTypeKind.Float:
                {
                    builder.AppendLine($"  %fixedcmp_less_{index} = fcmp fast olt {MapType(operandType)} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = fcmp fast ogt {MapType(operandType)} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                    return true;
                }
            case StarkTypeKind.Bool:
            case StarkTypeKind.RawPointer:
                {
                    var compareType = operandType.Kind == StarkTypeKind.RawPointer ? "ptr" : MapType(operandType);
                    builder.AppendLine($"  %fixedcmp_less_{index} = icmp ult {compareType} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = icmp ugt {compareType} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                    return true;
                }
            case StarkTypeKind.Ascii:
            case StarkTypeKind.Unicode:
                {
                    var helperName = operandType.Kind == StarkTypeKind.Ascii
                        ? AsciiCompareHelperName
                        : UnicodeCompareHelperName;
                    var compareResult = $"%fixedcmp_text_{index}";
                    builder.AppendLine($"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
                    builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt i32 {compareResult}, 0");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt i32 {compareResult}, 0");
                    builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                    return true;
                }
            case StarkTypeKind.FixedArray when operandType.ElementType is not null && operandType.FixedLength is int:
                {
                    var helperName = GetFixedArrayOrderedComparisonHelperName(operandType);
                    var compareResult = $"%fixedcmp_nested_{index}";
                    builder.AppendLine($"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
                    builder.AppendLine($"  %fixedcmp_less_{index} = icmp slt i32 {compareResult}, 0");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = icmp sgt i32 {compareResult}, 0");
                    builder.AppendLine($"  br i1 %fixedcmp_greater_{index}, label %return_greater, label %{nextBlock}");
                    return true;
                }
            default:
                return false;
        }
    }

    private void EmitIntegerExponentHelperDefinition(StringBuilder builder, int bitWidth)
    {
        var integerType = StarkTypeSymbols.Integer(bitWidth);
        var llvmType = MapType(integerType);
        var helperName = GetIntegerExponentHelperName(bitWidth);

        builder.AppendLine(BuildInternalAddressInsensitiveHelperSignature(
            llvmType,
            helperName,
            $"{llvmType} %base, {llvmType} %exponent"));
        builder.AppendLine("entry:");
        builder.AppendLine($"  %negative = icmp slt {llvmType} %exponent, 0");
        builder.AppendLine("  br i1 %negative, label %return_zero, label %loop_header");
        builder.AppendLine();
        builder.AppendLine("loop_header:");
        builder.AppendLine($"  %pow_result = phi {llvmType} [ 1, %entry ], [ %pow_next, %loop_body ]");
        builder.AppendLine($"  %pow_exp = phi {llvmType} [ %exponent, %entry ], [ %pow_exp_next, %loop_body ]");
        builder.AppendLine($"  %pow_done = icmp eq {llvmType} %pow_exp, 0");
        builder.AppendLine("  br i1 %pow_done, label %return_result, label %loop_body");
        builder.AppendLine();
        builder.AppendLine("loop_body:");
        builder.AppendLine($"  %pow_next = mul {llvmType} %pow_result, %base");
        builder.AppendLine($"  %pow_exp_next = sub {llvmType} %pow_exp, 1");
        builder.AppendLine("  br label %loop_header");
        builder.AppendLine();
        builder.AppendLine("return_zero:");
        builder.AppendLine($"  ret {llvmType} 0");
        builder.AppendLine();
        builder.AppendLine("return_result:");
        builder.AppendLine($"  ret {llvmType} %pow_result");
        builder.AppendLine("}");
    }

    private static string BuildInternalAddressInsensitiveHelperSignature(
        string returnType,
        string helperName,
        string parameters)
    {
        return $"define internal dso_local {returnType} @{EscapeIdentifier(helperName)}({parameters}) unnamed_addr {{";
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
                => StarkTypeSymbols.Integer(64),
            StarkTypeKind.Dynamic when index == 2
                => StarkTypeSymbols.Integer(64),
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
        if (GetScalarizableNamedAggregateFields(namedType) is { } fields)
        {
            orderedFields = fields;
            return true;
        }

        orderedFields = Array.Empty<FieldSymbol>();
        return false;
    }

    private static StarkTypeSymbol NormalizeAggregateType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private static int AlignTo(int value, int alignment)
    {
        return alignment <= 1
            ? value
            : ((value + alignment - 1) / alignment) * alignment;
    }

    private static string GetIntegerExponentHelperName(int bitWidth)
    {
        return $"{IntegerExponentHelperNamePrefix}{bitWidth}";
    }

    private static string GetFixedArrayOrderedComparisonHelperName(StarkTypeSymbol fixedArrayType)
    {
        return $"{FixedArrayCompareHelperNamePrefix}{EscapeIdentifier(fixedArrayType.DisplayName)}";
    }

    private static string GetScalarizedAggregateOrderedComparisonHelperName(StarkTypeSymbol aggregateType)
    {
        return $"{ScalarizedAggregateCompareHelperNamePrefix}{EscapeIdentifier(aggregateType.DisplayName)}";
    }

    public IEnumerable<string> EnumerateSystemMathIntrinsicDeclarations(IEnumerable<TypedFunctionSignature> signatures)
    {
        foreach (var signature in signatures)
        {
            if (!TryResolveSystemMathBuiltin(CurrentModuleName, signature, out var builtinKind))
            {
                continue;
            }

            if (!IsLlvmIntrinsicSystemMathBuiltin(builtinKind))
            {
                continue;
            }

            yield return BuildSystemMathIntrinsicDeclaration(signature, builtinKind);
        }
    }

    private IEnumerable<string> EnumerateConstrainedFloatingPointIntrinsicDeclarations()
    {
        var declarations = new SortedSet<string>(StringComparer.Ordinal);
        var hasStrictFpFunction = false;

        foreach (var function in _enumerateSsaFunctions())
        {
            if (_context.TryGetFunctionEffects(function.Name)?.IsStrictFp != true)
            {
                continue;
            }

            hasStrictFpFunction = true;
            foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
            {
                if (instruction is not SsaValueInstruction valueInstruction)
                {
                    continue;
                }

                AddConstrainedFloatingPointDeclarations(valueInstruction.Value, declarations);
            }
        }

        if (hasStrictFpFunction)
        {
            foreach (var floatType in EnumerateSupportedFloatTypes())
            {
                declarations.Add(BuildConstrainedFloatCompareDeclaration(floatType));
            }
        }

        return declarations;
    }

    private IEnumerable<StarkTypeSymbol> CollectFusedMultiplyAddTypes()
    {
        var types = new HashSet<StarkTypeSymbol>();

        foreach (var function in _enumerateSsaFunctions())
        {
            if (_context.TryGetFunctionEffects(function.Name)?.IsStrictFp == true)
            {
                continue;
            }

            var valueDefinitions = CollectValueDefinitions(function);
            foreach (var binary in function.Blocks
                         .SelectMany(static block => block.Instructions)
                         .OfType<SsaValueInstruction>()
                         .Select(static instruction => instruction.Value)
                         .OfType<SsaBinaryRValue>())
            {
                if (HasFusedMultiplyAddCandidate(binary, valueDefinitions))
                {
                    types.Add(binary.Type);
                }
            }
        }

        return types
            .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(
                static instruction => instruction.ResultName,
                static instruction => instruction.Value,
                StringComparer.Ordinal);
    }

    private static bool HasFusedMultiplyAddCandidate(
        SsaBinaryRValue binary,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        if (binary.Type.Kind != StarkTypeKind.Float
            || binary.Operator is not (SsaBinaryOperator.Add or SsaBinaryOperator.Subtract))
        {
            return false;
        }

        return IsFloatingMultiplyReference(binary.Left, binary.Type, valueDefinitions)
            || IsFloatingMultiplyReference(binary.Right, binary.Type, valueDefinitions);
    }

    private static bool IsFloatingMultiplyReference(
        SsaValue value,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        return value is SsaValueReference reference
            && valueDefinitions.TryGetValue(reference.Name, out var definition)
            && definition is SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Multiply,
                Type.Kind: StarkTypeKind.Float
            } multiply
            && multiply.Type == expectedType;
    }

    private void AddConstrainedFloatingPointDeclarations(SsaRValue value, ISet<string> declarations)
    {
        switch (value)
        {
            case SsaUnaryRValue { Operator: SsaUnaryOperator.Negate, Type.Kind: StarkTypeKind.Float } unary:
                declarations.Add(BuildConstrainedUnaryDeclaration("fneg", unary.Type));
                break;
            case SsaBinaryRValue { Operator: SsaBinaryOperator.Exponent, Type.Kind: StarkTypeKind.Float } binary:
                declarations.Add(BuildConstrainedBinaryDeclaration("pow", binary.Type));
                break;
            case SsaBinaryRValue { Type.Kind: StarkTypeKind.Float } binary:
                var operation = binary.Operator switch
                {
                    SsaBinaryOperator.Add => "fadd",
                    SsaBinaryOperator.Subtract => "fsub",
                    SsaBinaryOperator.Multiply => "fmul",
                    SsaBinaryOperator.Divide => "fdiv",
                    SsaBinaryOperator.Modulo => "frem",
                    _ => null
                };
                if (operation is not null)
                {
                    declarations.Add(BuildConstrainedBinaryDeclaration(operation, binary.Type));
                }

                break;
            case SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool, Left.Type.Kind: StarkTypeKind.Float } binary:
                declarations.Add(BuildConstrainedFloatCompareDeclaration(binary.Left.Type));
                break;
            case SsaConvertRValue convert:
                AddConstrainedConversionDeclaration(convert, declarations);
                break;
        }
    }

    private void AddConstrainedConversionDeclaration(SsaConvertRValue convert, ISet<string> declarations)
    {
        var sourceType = convert.Operand.Type;
        var targetType = convert.TargetType;

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
        {
            declarations.Add(
                $"declare {MapType(targetType)} @{GetConstrainedIntegerToFloatIntrinsicName(sourceType, targetType)}({MapType(sourceType)}, metadata, metadata)");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
        {
            declarations.Add(
                $"declare {MapType(targetType)} @{GetConstrainedFloatToIntegerIntrinsicName(sourceType, targetType)}({MapType(sourceType)}, metadata)");
            return;
        }

        if (sourceType.Kind != StarkTypeKind.Float
            || targetType.Kind != StarkTypeKind.Float
            || sourceType.BitWidth == targetType.BitWidth)
        {
            return;
        }

        var metadataParameters = sourceType.BitWidth < targetType.BitWidth
            ? "metadata"
            : "metadata, metadata";
        declarations.Add(
            $"declare {MapType(targetType)} @{GetConstrainedFloatConversionIntrinsicName(sourceType, targetType)}({MapType(sourceType)}, {metadataParameters})");
    }

    private static IEnumerable<StarkTypeSymbol> EnumerateSupportedFloatTypes()
    {
        yield return StarkTypeSymbols.Float(16);
        yield return StarkTypeSymbols.Float(32);
        yield return StarkTypeSymbols.Float(64);
    }

    private string BuildConstrainedBinaryDeclaration(string operation, StarkTypeSymbol type)
    {
        var llvmType = MapType(type);
        return $"declare {llvmType} @{GetConstrainedBinaryIntrinsicName(operation, type)}({llvmType}, {llvmType}, metadata, metadata)";
    }

    private string BuildConstrainedUnaryDeclaration(string operation, StarkTypeSymbol type)
    {
        var llvmType = MapType(type);
        return $"declare {llvmType} @{GetConstrainedUnaryIntrinsicName(operation, type)}({llvmType}, metadata, metadata)";
    }

    private string BuildConstrainedFloatCompareDeclaration(StarkTypeSymbol type)
    {
        var llvmType = MapType(type);
        return $"declare i1 @{GetConstrainedFloatCompareIntrinsicName(type)}({llvmType}, {llvmType}, metadata, metadata)";
    }

    public IEnumerable<string> EnumerateSystemBitOperationsIntrinsicDeclarations(IEnumerable<TypedFunctionSignature> signatures)
    {
        foreach (var signature in signatures)
        {
            if (!TryResolveSystemBitOperationsBuiltin(CurrentModuleName, signature, out var builtinKind))
            {
                continue;
            }

            yield return BuildSystemBitOperationsIntrinsicDeclaration(signature, builtinKind);
        }
    }

    private string BuildSystemMathIntrinsicDeclaration(
        TypedFunctionSignature function,
        SystemMathBuiltinKind builtinKind)
    {
        var arity = GetSystemMathIntrinsicArity(builtinKind);
        var scalarType = ValidateSystemMathBuiltinSignature(function, builtinKind, arity);
        var intrinsicName = $"@llvm.{GetSystemMathIntrinsicBaseName(builtinKind)}.{GetFloatIntrinsicSuffix(scalarType)}";
        var llvmType = MapType(scalarType);

        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            var pairType = $"{{ {llvmType}, {llvmType} }}";
            return $"declare {pairType} {intrinsicName}({llvmType})";
        }

        return $"declare {llvmType} {intrinsicName}({string.Join(", ", Enumerable.Repeat(llvmType, arity))})";
    }

    private string BuildSystemBitOperationsIntrinsicDeclaration(
        TypedFunctionSignature function,
        SystemBitOperationsBuiltinKind builtinKind)
    {
        var surfaceArity = GetSystemBitOperationsSurfaceArity(builtinKind);
        var scalarType = ValidateSystemBitOperationsBuiltinSignature(function, builtinKind, surfaceArity);
        var intrinsicName = $"@llvm.{GetSystemBitOperationsIntrinsicBaseName(builtinKind)}.i{scalarType.BitWidth}";
        var llvmType = MapType(scalarType);

        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.LeadingZeroCount or SystemBitOperationsBuiltinKind.TrailingZeroCount
                => $"declare {llvmType} {intrinsicName}({llvmType}, i1 immarg)",
            SystemBitOperationsBuiltinKind.PopCount
                => $"declare {llvmType} {intrinsicName}({llvmType})",
            SystemBitOperationsBuiltinKind.RotateLeft or SystemBitOperationsBuiltinKind.RotateRight
                => $"declare {llvmType} {intrinsicName}({llvmType}, {llvmType}, {llvmType})",
            _ => throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.")
        };
    }

    private IReadOnlySet<SystemMemoryBuiltinKind> CollectSystemMemoryAllocatorBuiltins(IEnumerable<TypedFunctionSignature> signatures)
    {
        var builtins = new HashSet<SystemMemoryBuiltinKind>();

        foreach (var signature in signatures)
        {
            if (TryResolveSystemMemoryBuiltin(CurrentModuleName, signature, out var builtinKind))
            {
                builtins.Add(builtinKind);
            }
        }

        return builtins;
    }

    public bool TryEmitBuiltinFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        string moduleName,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects)
    {
        if (TryResolveSystemMathBuiltin(moduleName, function, out var systemMathBuiltinKind))
        {
            builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemMathBuiltin(builder, function, abiFunction, systemMathBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (TryResolveSystemBitOperationsBuiltin(moduleName, function, out var systemBitOperationsBuiltinKind))
        {
            builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemBitOperationsBuiltin(builder, function, abiFunction, systemBitOperationsBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (TryResolveSystemMemoryBuiltin(moduleName, function, out var systemMemoryBuiltinKind))
        {
            var effectiveMemoryEffects = GetSystemMemoryBuiltinMemoryEffects(systemMemoryBuiltinKind);
            builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, effectiveMemoryEffects, parameterEffects) + " {");
            EmitSystemMemoryBuiltin(builder, function, abiFunction, systemMemoryBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (TryResolveSystemCollectionsBuiltin(moduleName, function, out var systemCollectionsBuiltinKind))
        {
            builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemCollectionsBuiltin(builder, function, abiFunction, systemCollectionsBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (TryResolveSystemRuntimeBuiltin(moduleName, function, out var systemRuntimeBuiltinKind))
        {
            builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
            EmitSystemRuntimeBuiltin(builder, abiFunction, systemRuntimeBuiltinKind);
            builder.AppendLine("}");
            return true;
        }

        if (!TryGetSystemTextBuiltin(moduleName, function.Name, out var builtinKind))
        {
            return false;
        }

        if (!IsSystemTextBuiltinHostModule(CurrentModuleName)
            && builtinKind is SystemTextBuiltinKind.TryConcatAscii
                or SystemTextBuiltinKind.TryConcatUnicode
                or SystemTextBuiltinKind.TryConvertAsciiToUnicode)
        {
            return false;
        }

        builder.AppendLine(_buildDefinitionSignature(internalize, function, abiFunction, effects, memoryEffects, parameterEffects) + " {");
        switch (builtinKind)
        {
            case SystemTextBuiltinKind.AsciiView:
                EmitOwnedTextViewBuiltin(builder, abiFunction, StarkTypeSymbols.Ascii);
                break;
            case SystemTextBuiltinKind.UnicodeView:
                EmitOwnedTextViewBuiltin(builder, abiFunction, StarkTypeSymbols.Unicode);
                break;
            case SystemTextBuiltinKind.AsciiData:
            case SystemTextBuiltinKind.UnicodeData:
                EmitTextViewDataBuiltin(builder, abiFunction);
                break;
            case SystemTextBuiltinKind.AsciiLength:
            case SystemTextBuiltinKind.UnicodeLength:
                EmitTextViewLengthBuiltin(builder, abiFunction);
                break;
            case SystemTextBuiltinKind.TryConcatAscii:
                EmitOwnedTextConcatBuiltin(builder, abiFunction, StarkTypeSymbols.Integer(8), StarkTypeSymbols.Ascii);
                break;
            case SystemTextBuiltinKind.TryConcatUnicode:
                EmitOwnedTextConcatBuiltin(builder, abiFunction, StarkTypeSymbols.Integer(32), StarkTypeSymbols.Unicode);
                break;
            case SystemTextBuiltinKind.TryConvertAsciiToUnicode:
                EmitAsciiToUnicodeConversionBuiltin(builder, abiFunction);
                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Text builtin '{builtinKind}'.");
        }

        builder.AppendLine("}");
        return true;
    }

    private void EmitSystemMathBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemMathBuiltinKind builtinKind)
    {
        var arity = GetSystemMathIntrinsicArity(builtinKind);
        var scalarType = ValidateSystemMathBuiltinSignature(function, builtinKind, arity);

        if (abiFunction.UserParameters.Count != arity)
        {
            throw new InvalidOperationException($"System.Math builtin '{abiFunction.Name}' expects exactly {arity} user parameter(s).");
        }

        if (IsHardwareAsmSystemMathBuiltin(builtinKind))
        {
            EmitSystemMathHardwareBuiltin(builder, function, abiFunction, builtinKind, scalarType);
            return;
        }

        var llvmType = MapType(scalarType);
        var intrinsicName = $"@llvm.{GetSystemMathIntrinsicBaseName(builtinKind)}.{GetFloatIntrinsicSuffix(scalarType)}";

        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            EmitSystemMathSinCosBuiltin(builder, function, abiFunction, intrinsicName, scalarType);
            return;
        }

        builder.AppendLine("entry:");
        var fastMath = GetBuiltinFastMathCallModifier(function);
        if (arity == 1)
        {
            var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
            builder.AppendLine($"  %math_result = call{fastMath} {llvmType} {intrinsicName}({llvmType} {value})");
            builder.AppendLine($"  ret {llvmType} %math_result");
            return;
        }

        var left = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
        var right = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
        builder.AppendLine($"  %math_result = call{fastMath} {llvmType} {intrinsicName}({llvmType} {left}, {llvmType} {right})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private void EmitSystemBitOperationsBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemBitOperationsBuiltinKind builtinKind)
    {
        var surfaceArity = GetSystemBitOperationsSurfaceArity(builtinKind);
        var scalarType = ValidateSystemBitOperationsBuiltinSignature(function, builtinKind, surfaceArity);
        if (abiFunction.UserParameters.Count != surfaceArity)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{abiFunction.Name}' expects exactly {surfaceArity} user parameter(s).");
        }

        var llvmType = MapType(scalarType);
        var intrinsicName = $"@llvm.{GetSystemBitOperationsIntrinsicBaseName(builtinKind)}.i{scalarType.BitWidth}";
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        switch (builtinKind)
        {
            case SystemBitOperationsBuiltinKind.LeadingZeroCount:
            case SystemBitOperationsBuiltinKind.TrailingZeroCount:
                builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value}, i1 false)");
                break;
            case SystemBitOperationsBuiltinKind.PopCount:
                builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value})");
                break;
            case SystemBitOperationsBuiltinKind.RotateLeft:
            case SystemBitOperationsBuiltinKind.RotateRight:
                {
                    var amount = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
                    builder.AppendLine($"  %bit_result = call {llvmType} {intrinsicName}({llvmType} {value}, {llvmType} {value}, {llvmType} {amount})");
                    break;
                }
            default:
                throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.");
        }

        builder.AppendLine($"  ret {llvmType} %bit_result");
    }

    private void EmitSystemMemoryBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemMemoryBuiltinKind builtinKind)
    {
        ValidateSystemMemoryBuiltinSignature(function, builtinKind);

        switch (builtinKind)
        {
            case SystemMemoryBuiltinKind.Allocate:
                EmitSystemMemoryAllocateBuiltin(builder, abiFunction);
                break;
            case SystemMemoryBuiltinKind.Reallocate:
                EmitSystemMemoryReallocateBuiltin(builder, abiFunction);
                break;
            case SystemMemoryBuiltinKind.Free:
                EmitSystemMemoryFreeBuiltin(builder, abiFunction);
                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Memory builtin '{builtinKind}'.");
        }
    }

    private void EmitSystemCollectionsBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemCollectionsBuiltinKind builtinKind)
    {
        switch (builtinKind)
        {
            case SystemCollectionsBuiltinKind.ListAsSlice:
            case SystemCollectionsBuiltinKind.ListAsMutableSlice:
                var listShape = ValidateSystemCollectionsListSliceSignature(function, builtinKind);
                EmitListSliceViewBuiltin(builder, function, abiFunction, listShape);
                break;
            case SystemCollectionsBuiltinKind.DictionaryKeyEquals:
                EmitDictionaryKeyEqualsBuiltin(builder, function, abiFunction);
                break;
            case SystemCollectionsBuiltinKind.DictionaryKeyHash:
                EmitDictionaryKeyHashBuiltin(builder, function, abiFunction);
                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Collections builtin '{builtinKind}'.");
        }
    }

    private void EmitSystemRuntimeBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        SystemRuntimeBuiltinKind builtinKind)
    {
        switch (builtinKind)
        {
            case SystemRuntimeBuiltinKind.GetByteSliceParts:
            case SystemRuntimeBuiltinKind.GetMutableByteSliceParts:
                EmitByteSlicePartsBuiltin(builder, abiFunction);
                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Runtime builtin '{builtinKind}'.");
        }
    }

    private void EmitByteSlicePartsBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Runtime byte slice parts builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var sourceType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.SourceReturnType);

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "byte_slice_source");
        builder.AppendLine($"  %byte_slice_data = extractvalue {sourceType} {sourceValue}, 0");
        builder.AppendLine($"  %byte_slice_length = extractvalue {sourceType} {sourceValue}, 1");
        builder.AppendLine($"  %byte_slice_parts_with_data = insertvalue {resultType} zeroinitializer, ptr %byte_slice_data, 0");
        builder.AppendLine($"  %byte_slice_parts = insertvalue {resultType} %byte_slice_parts_with_data, i64 %byte_slice_length, 1");
        EmitAggregateBuiltinReturn(builder, abiFunction, resultType, "%byte_slice_parts");
    }

    private void EmitListSliceViewBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemCollectionsListShape listShape)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' expects exactly one receiver parameter.");
        }

        var receiver = abiFunction.UserParameters[0];
        var listType = MapType(receiver.SourceType);
        var resultType = MapType(function.ReturnType);
        var receiverPointer = $"%{EscapeIdentifier(receiver.LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %list_data_addr = getelementptr{GetProvenInObjectGepFlags()} {listType}, ptr {receiverPointer}, i32 0, i32 {listShape.DataFieldIndex}");
        builder.AppendLine($"  %list_length_addr = getelementptr{GetProvenInObjectGepFlags()} {listType}, ptr {receiverPointer}, i32 0, i32 {listShape.LengthFieldIndex}");
        builder.AppendLine("  %list_data = load ptr, ptr %list_data_addr");
        builder.AppendLine("  %list_length = load i64, ptr %list_length_addr");
        builder.AppendLine($"  %slice_with_ptr = insertvalue {resultType} zeroinitializer, ptr %list_data, 0");
        builder.AppendLine($"  %slice_result = insertvalue {resultType} %slice_with_ptr, i64 %list_length, 1");
        builder.AppendLine($"  ret {resultType} %slice_result");
    }

    private void EmitDictionaryKeyEqualsBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction)
    {
        var keyType = ValidateSystemCollectionsDictionaryKeySignature(function, expectedParameterCount: 2);
        if (abiFunction.UserParameters.Count != 2)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' expects exactly two key parameters.");
        }

        var llvmType = MapType(keyType);
        builder.AppendLine("entry:");
        var left = EmitDictionaryKeyParameterLoad(builder, abiFunction.UserParameters[0], keyType, "dict_key_left");
        var right = EmitDictionaryKeyParameterLoad(builder, abiFunction.UserParameters[1], keyType, "dict_key_right");
        builder.AppendLine($"  %dict_key_equal = icmp eq {llvmType} {left}, {right}");
        builder.AppendLine("  ret i1 %dict_key_equal");
    }

    private void EmitDictionaryKeyHashBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction)
    {
        var keyType = ValidateSystemCollectionsDictionaryKeySignature(function, expectedParameterCount: 1);
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' expects exactly one key parameter.");
        }

        var llvmType = MapType(keyType);
        builder.AppendLine("entry:");
        var value = EmitDictionaryKeyParameterLoad(builder, abiFunction.UserParameters[0], keyType, "dict_key_value");
        var hashValue = keyType.Kind switch
        {
            StarkTypeKind.Bool => EmitIntegerHashConversion(builder, "i1", value, "dict_key_hash"),
            StarkTypeKind.Integer when keyType.BitWidth is int bitWidth => EmitIntegerHashConversion(builder, llvmType, value, "dict_key_hash", bitWidth),
            _ => throw new InvalidOperationException($"System.Collections DictionaryKey.Hash does not support key type '{keyType.DisplayName}'.")
        };
        builder.AppendLine($"  ret i64 {hashValue}");
    }

    private string EmitDictionaryKeyParameterLoad(
        StringBuilder builder,
        AbiParameterSymbol parameter,
        StarkTypeSymbol keyType,
        string localName)
    {
        var llvmType = MapType(keyType);
        var parameterValue = $"%{EscapeIdentifier(parameter.LlvmName)}";
        if (parameter.Kind == AbiParameterKind.Direct)
        {
            return parameterValue;
        }

        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            throw new InvalidOperationException($"System.Collections dictionary key parameter '{parameter.SourceName}' must lower directly or as an indirect input.");
        }

        var loaded = $"%{localName}";
        builder.AppendLine($"  {loaded} = load {llvmType}, ptr {parameterValue}");
        return loaded;
    }

    private static string EmitIntegerHashConversion(
        StringBuilder builder,
        string llvmType,
        string value,
        string localName,
        int bitWidth = 1)
    {
        if (bitWidth == 64)
        {
            return value;
        }

        var converted = $"%{localName}";
        var opcode = bitWidth < 64 ? "zext" : "trunc";
        builder.AppendLine($"  {converted} = {opcode} {llvmType} {value} to i64");
        return converted;
    }

    private void EmitSystemMemoryAllocateBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Memory builtin '{abiFunction.Name}' expects exactly three user parameters.");
        }

        var allocatorParameter = abiFunction.UserParameters[0];
        var byteLengthParameter = abiFunction.UserParameters[1];
        var alignmentParameter = abiFunction.UserParameters[2];
        var allocationShape = GetSystemMemoryAllocationShape(abiFunction.SourceReturnType);
        var byteLengthValue = GetBuiltinParameterValue(byteLengthParameter);
        var alignmentValue = GetBuiltinParameterValue(alignmentParameter);

        builder.AppendLine("entry:");
        var allocatorValue = MaterializeAggregateBuiltinParameterValue(builder, allocatorParameter, "memory_allocator");
        builder.AppendLine($"  %memory_is_zero = icmp eq {allocationShape.ByteLengthLlvmType} {byteLengthValue}, 0");
        builder.AppendLine("  br i1 %memory_is_zero, label %memory_zero, label %memory_allocate");
        builder.AppendLine();
        builder.AppendLine("memory_zero:");
        var zeroResult = EmitSystemMemoryAllocationValue(
            builder,
            allocationShape,
            "null",
            byteLengthValue,
            alignmentValue,
            allocatorValue,
            "memory_zero");
        EmitAggregateBuiltinReturn(builder, abiFunction, allocationShape.LlvmType, zeroResult);
        builder.AppendLine();
        builder.AppendLine("memory_allocate:");
        var needsRuntimeSizeConversion = NeedsSystemMemoryRuntimeSizeConversion(allocationShape);
        if (needsRuntimeSizeConversion)
        {
            EmitSystemMemoryRuntimeSizeBoundsCheck(
                builder,
                [
                    (allocationShape.ByteLengthLlvmType, byteLengthValue, "memory_byte_length"),
                    (allocationShape.AlignmentLlvmType, alignmentValue, "memory_alignment")
                ],
                "memory_runtime_size_ok",
                "memory_oom");
        }

        var runtimeByteLengthValue = EmitSystemMemoryRuntimeSizeConversion(
            builder,
            allocationShape.ByteLengthLlvmType,
            byteLengthValue,
            "memory_byte_length_runtime");
        var runtimeAlignmentValue = EmitSystemMemoryRuntimeSizeConversion(
            builder,
            allocationShape.AlignmentLlvmType,
            alignmentValue,
            "memory_alignment_runtime");
        builder.AppendLine($"  %memory_ptr = call noalias nonnull noundef ptr @{RuntimeAllocateHelperName}({AllocatorSizeType} noundef {runtimeByteLengthValue}, {AllocatorSizeType} noundef {runtimeAlignmentValue})");
        var result = EmitSystemMemoryAllocationValue(
            builder,
            allocationShape,
            "%memory_ptr",
            byteLengthValue,
            alignmentValue,
            allocatorValue,
            "memory");
        EmitAggregateBuiltinReturn(builder, abiFunction, allocationShape.LlvmType, result);
        if (needsRuntimeSizeConversion)
        {
            builder.AppendLine();
            EmitSystemMemoryTrapBlock(builder, "memory_oom");
        }
    }

    private void EmitSystemMemoryReallocateBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Memory builtin '{abiFunction.Name}' expects exactly three user parameters.");
        }

        var allocationParameter = abiFunction.UserParameters[0];
        var byteLengthParameter = abiFunction.UserParameters[1];
        var alignmentParameter = abiFunction.UserParameters[2];
        var allocationShape = GetSystemMemoryAllocationShape(abiFunction.SourceReturnType);
        var byteLengthValue = GetBuiltinParameterValue(byteLengthParameter);
        var alignmentValue = GetBuiltinParameterValue(alignmentParameter);

        builder.AppendLine("entry:");
        var allocationValue = MaterializeAggregateBuiltinParameterValue(builder, allocationParameter, "memory_allocation");
        builder.AppendLine($"  %memory_old_ptr = extractvalue {allocationShape.LlvmType} {allocationValue}, 0");
        builder.AppendLine($"  %memory_old_byte_length = extractvalue {allocationShape.LlvmType} {allocationValue}, 1");
        builder.AppendLine($"  %memory_allocator = extractvalue {allocationShape.LlvmType} {allocationValue}, 3");
        builder.AppendLine($"  %memory_is_zero = icmp eq {allocationShape.ByteLengthLlvmType} {byteLengthValue}, 0");
        builder.AppendLine("  br i1 %memory_is_zero, label %memory_free_zero, label %memory_reallocate");
        builder.AppendLine();
        builder.AppendLine("memory_free_zero:");
        builder.AppendLine($"  call void @{RuntimeFreeHelperName}(ptr %memory_old_ptr)");
        var zeroResult = EmitSystemMemoryAllocationValue(
            builder,
            allocationShape,
            "null",
            byteLengthValue,
            alignmentValue,
            "%memory_allocator",
            "memory_zero");
        EmitAggregateBuiltinReturn(builder, abiFunction, allocationShape.LlvmType, zeroResult);
        builder.AppendLine();
        builder.AppendLine("memory_reallocate:");
        var needsRuntimeSizeConversion = NeedsSystemMemoryRuntimeSizeConversion(allocationShape);
        if (needsRuntimeSizeConversion)
        {
            EmitSystemMemoryRuntimeSizeBoundsCheck(
                builder,
                [
                    (allocationShape.ByteLengthLlvmType, "%memory_old_byte_length", "memory_old_byte_length"),
                    (allocationShape.ByteLengthLlvmType, byteLengthValue, "memory_byte_length"),
                    (allocationShape.AlignmentLlvmType, alignmentValue, "memory_alignment")
                ],
                "memory_runtime_size_ok",
                "memory_oom");
        }

        var runtimeOldByteLengthValue = EmitSystemMemoryRuntimeSizeConversion(
            builder,
            allocationShape.ByteLengthLlvmType,
            "%memory_old_byte_length",
            "memory_old_byte_length_runtime");
        var runtimeByteLengthValue = EmitSystemMemoryRuntimeSizeConversion(
            builder,
            allocationShape.ByteLengthLlvmType,
            byteLengthValue,
            "memory_byte_length_runtime");
        var runtimeAlignmentValue = EmitSystemMemoryRuntimeSizeConversion(
            builder,
            allocationShape.AlignmentLlvmType,
            alignmentValue,
            "memory_alignment_runtime");
        builder.AppendLine($"  %memory_ptr = call nonnull noundef ptr @{RuntimeReallocateHelperName}(ptr %memory_old_ptr, {AllocatorSizeType} noundef {runtimeOldByteLengthValue}, {AllocatorSizeType} noundef {runtimeByteLengthValue}, {AllocatorSizeType} noundef {runtimeAlignmentValue})");
        var result = EmitSystemMemoryAllocationValue(
            builder,
            allocationShape,
            "%memory_ptr",
            byteLengthValue,
            alignmentValue,
            "%memory_allocator",
            "memory");
        EmitAggregateBuiltinReturn(builder, abiFunction, allocationShape.LlvmType, result);
        if (needsRuntimeSizeConversion)
        {
            builder.AppendLine();
            EmitSystemMemoryTrapBlock(builder, "memory_oom");
        }
    }

    private void EmitSystemMemoryFreeBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Memory builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var allocationParameter = abiFunction.UserParameters[0];
        var allocationShape = GetSystemMemoryAllocationShape(allocationParameter.SourceType);

        builder.AppendLine("entry:");
        var allocationValue = MaterializeAggregateBuiltinParameterValue(builder, allocationParameter, "memory_allocation");
        builder.AppendLine($"  %memory_ptr = extractvalue {allocationShape.LlvmType} {allocationValue}, 0");
        builder.AppendLine($"  call void @{RuntimeFreeHelperName}(ptr %memory_ptr)");
        builder.AppendLine("  ret void");
    }

    private static string GetBuiltinParameterValue(AbiParameterSymbol parameter)
    {
        if (parameter.Kind != AbiParameterKind.Direct)
        {
            throw new InvalidOperationException($"System.Memory scalar parameter '{parameter.SourceName}' must lower directly.");
        }

        return $"%{EscapeIdentifier(parameter.LlvmName)}";
    }

    private bool NeedsSystemMemoryRuntimeSizeConversion(SystemMemoryAllocationShape allocationShape)
    {
        return allocationShape.ByteLengthLlvmType != AllocatorSizeType
            || allocationShape.AlignmentLlvmType != AllocatorSizeType;
    }

    private void EmitSystemMemoryRuntimeSizeBoundsCheck(
        StringBuilder builder,
        IReadOnlyList<(string LlvmType, string Value, string LocalName)> values,
        string okLabel,
        string trapLabel)
    {
        if (AllocatorSizeType != "i32")
        {
            throw new InvalidOperationException(
                $"System.Memory runtime size conversion from i64 to '{AllocatorSizeType}' is not supported.");
        }

        string? combined = null;
        foreach (var (llvmType, value, localName) in values)
        {
            if (llvmType == AllocatorSizeType)
            {
                continue;
            }

            if (llvmType != "i64")
            {
                throw new InvalidOperationException(
                    $"System.Memory runtime size conversion from '{llvmType}' to '{AllocatorSizeType}' is not supported.");
            }

            var tooLarge = $"%{localName}_too_large";
            builder.AppendLine($"  {tooLarge} = icmp ugt {llvmType} {value}, 4294967295");
            if (combined is null)
            {
                combined = tooLarge;
            }
            else
            {
                var nextCombined = $"%{localName}_or_previous_too_large";
                builder.AppendLine($"  {nextCombined} = or i1 {combined}, {tooLarge}");
                combined = nextCombined;
            }
        }

        if (combined is null)
        {
            return;
        }

        builder.AppendLine($"  br i1 {combined}, label %{trapLabel}, label %{okLabel}");
        builder.AppendLine();
        builder.AppendLine($"{okLabel}:");
    }

    private string EmitSystemMemoryRuntimeSizeConversion(
        StringBuilder builder,
        string sourceLlvmType,
        string sourceValue,
        string localName)
    {
        if (sourceLlvmType == AllocatorSizeType)
        {
            return sourceValue;
        }

        if (sourceLlvmType == "i64" && AllocatorSizeType == "i32")
        {
            var converted = $"%{localName}";
            builder.AppendLine($"  {converted} = trunc i64 {sourceValue} to i32");
            return converted;
        }

        throw new InvalidOperationException(
            $"System.Memory runtime size conversion from '{sourceLlvmType}' to '{AllocatorSizeType}' is not supported.");
    }

    private string EmitSystemMemoryAllocationValue(
        StringBuilder builder,
        SystemMemoryAllocationShape allocationShape,
        string pointerValue,
        string byteLengthValue,
        string alignmentValue,
        string allocatorValue,
        string localPrefix)
    {
        var withPointer = $"%{EscapeIdentifier($"{localPrefix}_with_ptr")}";
        var withByteLength = $"%{EscapeIdentifier($"{localPrefix}_with_len")}";
        var withAlignment = $"%{EscapeIdentifier($"{localPrefix}_with_align")}";
        var result = $"%{EscapeIdentifier($"{localPrefix}_result")}";

        builder.AppendLine($"  {withPointer} = insertvalue {allocationShape.LlvmType} zeroinitializer, ptr {pointerValue}, 0");
        builder.AppendLine($"  {withByteLength} = insertvalue {allocationShape.LlvmType} {withPointer}, {allocationShape.ByteLengthLlvmType} {byteLengthValue}, 1");
        builder.AppendLine($"  {withAlignment} = insertvalue {allocationShape.LlvmType} {withByteLength}, {allocationShape.AlignmentLlvmType} {alignmentValue}, 2");
        builder.AppendLine($"  {result} = insertvalue {allocationShape.LlvmType} {withAlignment}, {allocationShape.AllocatorLlvmType} {allocatorValue}, 3");
        return result;
    }

    private void EmitAggregateBuiltinReturn(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        string valueType,
        string value)
    {
        if (abiFunction.ReturnsIndirect)
        {
            if (abiFunction.ReturnBufferParameter is null)
            {
                throw new InvalidOperationException($"System.Memory aggregate builtin '{abiFunction.Name}' is missing its sret parameter.");
            }

            builder.AppendLine($"  store {valueType} {value}, ptr %{EscapeIdentifier(abiFunction.ReturnBufferParameter.LlvmName)}");
            builder.AppendLine("  ret void");
            return;
        }

        builder.AppendLine($"  ret {valueType} {value}");
    }

    private static void EmitSystemMemoryTrapBlock(StringBuilder builder, string label)
    {
        builder.AppendLine($"{label}:");
        builder.AppendLine("  call void @llvm.trap()");
        builder.AppendLine("  unreachable");
    }

    private void EmitSystemMathHardwareBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        if (builtinKind == SystemMathBuiltinKind.FusedMultiplyAdd)
        {
            EmitSystemMathFusedMultiplyAddHardwareBuiltin(builder, function, abiFunction, scalarType);
            return;
        }

        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 1 user parameter.");
        }

        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(TargetInfo);
        var template = GetSystemMathHardwareAsmTemplate(builtinKind, scalarType, architecture);
        var constraints = GetSystemMathHardwareAsmConstraints(scalarType, architecture);
        var llvmType = MapType(scalarType);
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine(
            $"  %math_result = call{GetBuiltinFastMathCallModifier(function)} {llvmType} asm \"{_escapeInlineAsmString(template)}\", \"{_escapeInlineAsmString(constraints)}\"({llvmType} {value})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private void EmitSystemMathFusedMultiplyAddHardwareBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol scalarType)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 3 user parameters.");
        }

        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(TargetInfo);
        var template = GetSystemMathFusedMultiplyAddAsmTemplate(scalarType, architecture);
        var constraints = GetSystemMathFusedMultiplyAddAsmConstraints(scalarType, architecture);
        var llvmType = MapType(scalarType);
        var left = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
        var right = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
        var addend = $"%{EscapeIdentifier(abiFunction.UserParameters[2].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine(
            $"  %math_result = call{GetBuiltinFastMathCallModifier(function)} {llvmType} asm \"{_escapeInlineAsmString(template)}\", \"{_escapeInlineAsmString(constraints)}\"({llvmType} {left}, {llvmType} {right}, {llvmType} {addend})");
        builder.AppendLine($"  ret {llvmType} %math_result");
    }

    private static string GetSystemMathFusedMultiplyAddAsmTemplate(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => scalarType.BitWidth switch
            {
                32 => "vfmadd213ss %xmm2, %xmm1, %xmm0",
                64 => "vfmadd213sd %xmm2, %xmm1, %xmm0",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "fmadd s0, s0, s1, s2",
                64 => "fmadd d0, d0, d1, d2",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{SystemMathBuiltinKind.FusedMultiplyAdd}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathFusedMultiplyAddAsmConstraints(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => "={xmm0},0,{xmm1},{xmm2}",
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "={s0},0,{s1},{s2}",
                64 => "={d0},0,{d1},{d2}",
                _ => throw new InvalidOperationException(
                    $"System.Math FusedMultiplyAdd single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{SystemMathBuiltinKind.FusedMultiplyAdd}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => GetX86SystemMathHardwareAsmTemplate(builtinKind, scalarType),
            StarkAsmArchitecture.AArch64 => GetAArch64SystemMathHardwareAsmTemplate(builtinKind, scalarType),
            _ => throw new InvalidOperationException(
                $"System.Math builtin '{builtinKind}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetSystemMathHardwareAsmConstraints(
        StarkTypeSymbol scalarType,
        StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 => "={xmm0},0",
            StarkAsmArchitecture.AArch64 => scalarType.BitWidth switch
            {
                32 => "={s0},0",
                64 => "={d0},0",
                _ => throw new InvalidOperationException(
                    $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.")
        };
    }

    private static string GetX86SystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        return scalarType.BitWidth switch
        {
            32 => builtinKind switch
            {
                SystemMathBuiltinKind.Sqrt => "sqrtss %xmm0, %xmm0",
                SystemMathBuiltinKind.ReciprocalEstimate => "rcpss %xmm0, %xmm0",
                SystemMathBuiltinKind.ReciprocalSqrtEstimate => "rsqrtss %xmm0, %xmm0",
                SystemMathBuiltinKind.Ceiling => "roundss $$2, %xmm0, %xmm0",
                SystemMathBuiltinKind.Floor => "roundss $$1, %xmm0, %xmm0",
                SystemMathBuiltinKind.Truncate => "roundss $$3, %xmm0, %xmm0",
                SystemMathBuiltinKind.Round => "roundss $$0, %xmm0, %xmm0",
                _ => throw new InvalidOperationException($"Unsupported x86/x64 hardware System.Math builtin '{builtinKind}'.")
            },
            64 => builtinKind switch
            {
                SystemMathBuiltinKind.Sqrt => "sqrtsd %xmm0, %xmm0",
                SystemMathBuiltinKind.Ceiling => "roundsd $$2, %xmm0, %xmm0",
                SystemMathBuiltinKind.Floor => "roundsd $$1, %xmm0, %xmm0",
                SystemMathBuiltinKind.Truncate => "roundsd $$3, %xmm0, %xmm0",
                SystemMathBuiltinKind.Round => "roundsd $$0, %xmm0, %xmm0",
                _ => throw new InvalidOperationException($"Unsupported x86/x64 hardware System.Math builtin '{builtinKind}'.")
            },
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
        };
    }

    private static string GetAArch64SystemMathHardwareAsmTemplate(
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        var register = scalarType.BitWidth switch
        {
            32 => "s0",
            64 => "d0",
            _ => throw new InvalidOperationException(
                $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.")
        };

        var opcode = builtinKind switch
        {
            SystemMathBuiltinKind.Sqrt => "fsqrt",
            SystemMathBuiltinKind.ReciprocalEstimate => "frecpe",
            SystemMathBuiltinKind.ReciprocalSqrtEstimate => "frsqrte",
            SystemMathBuiltinKind.Ceiling => "frintp",
            SystemMathBuiltinKind.Floor => "frintm",
            SystemMathBuiltinKind.Truncate => "frintz",
            SystemMathBuiltinKind.Round => "frintn",
            _ => throw new InvalidOperationException($"Unsupported aarch64 hardware System.Math builtin '{builtinKind}'.")
        };

        return $"{opcode} {register}, {register}";
    }

    private void EmitSystemMathSinCosBuiltin(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        string intrinsicName,
        StarkTypeSymbol scalarType)
    {
        var signature = ValidateSystemMathSinCosBuiltinSignature(function);
        var scalarLlvmType = MapType(scalarType);
        var pairType = $"{{ {scalarLlvmType}, {scalarLlvmType} }}";
        var resultType = MapType(function.ReturnType);
        var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";

        builder.AppendLine("entry:");
        builder.AppendLine($"  %math_pair = call{GetBuiltinFastMathCallModifier(function)} {pairType} {intrinsicName}({scalarLlvmType} {value})");
        builder.AppendLine($"  %math_sin = extractvalue {pairType} %math_pair, 0");
        builder.AppendLine($"  %math_cos = extractvalue {pairType} %math_pair, 1");
        builder.AppendLine($"  %math_with_sin = insertvalue {resultType} zeroinitializer, {scalarLlvmType} %math_sin, {signature.SinFieldIndex}");
        builder.AppendLine($"  %math_result = insertvalue {resultType} %math_with_sin, {scalarLlvmType} %math_cos, {signature.CosFieldIndex}");
        builder.AppendLine($"  ret {resultType} %math_result");
    }

    private string GetBuiltinFastMathCallModifier(TypedFunctionSignature function)
    {
        return _context.TryGetFunctionEffects(function.Name)?.IsStrictFp == true ? string.Empty : " fast";
    }

    private void EmitOwnedTextViewBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol viewType)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text view builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(viewType);

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "view_source");
        builder.AppendLine($"  %view_data = extractvalue {aggregateType} {sourceValue}, 0");
        builder.AppendLine($"  %view_length = extractvalue {aggregateType} {sourceValue}, 1");
        builder.AppendLine($"  %view_with_ptr = insertvalue {resultType} zeroinitializer, ptr %view_data, 0");
        builder.AppendLine($"  %view_result = insertvalue {resultType} %view_with_ptr, i64 %view_length, 1");
        builder.AppendLine($"  ret {resultType} %view_result");
    }

    private void EmitTextViewDataBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        EmitSliceViewDataBuiltin(builder, abiFunction);
    }

    private void EmitTextViewLengthBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        EmitSliceViewLengthBuiltin(builder, abiFunction);
    }

    private void EmitSliceViewDataBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"Slice data builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.LlvmReturnType);

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "view_source");
        builder.AppendLine($"  %view_data = extractvalue {aggregateType} {sourceValue}, 0");
        builder.AppendLine($"  ret {resultType} %view_data");
    }

    private void EmitSliceViewLengthBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"Slice length builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.LlvmReturnType);

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "view_source");
        builder.AppendLine($"  %view_length = extractvalue {aggregateType} {sourceValue}, 1");
        builder.AppendLine($"  ret {resultType} %view_length");
    }

    private void EmitOwnedTextConcatBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction,
        StarkTypeSymbol unitType,
        StarkTypeSymbol viewType)
    {
        if (abiFunction.UserParameters.Count != 3)
        {
            throw new InvalidOperationException($"System.Text concat builtin '{abiFunction.Name}' expects exactly three user parameters.");
        }

        var destinationParameter = abiFunction.UserParameters[0];
        var leftParameter = abiFunction.UserParameters[1];
        var rightParameter = abiFunction.UserParameters[2];
        var aggregateType = destinationParameter.SourceType.ElementType is not null
            ? MapType(destinationParameter.SourceType.ElementType)
            : throw new InvalidOperationException($"System.Text concat builtin '{abiFunction.Name}' requires a raw pointer destination to an owning text aggregate.");
        var viewLlvmType = MapType(viewType);
        var unitLlvmType = MapType(unitType);
        var destinationPointer = $"%{EscapeIdentifier(destinationParameter.LlvmName)}";

        builder.AppendLine("entry:");
        var leftValue = MaterializeAggregateBuiltinParameterValue(builder, leftParameter, "concat_left_view");
        var rightValue = MaterializeAggregateBuiltinParameterValue(builder, rightParameter, "concat_right_view");
        builder.AppendLine($"  %concat_data_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 0");
        builder.AppendLine($"  %concat_length_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 1");
        builder.AppendLine($"  %concat_capacity_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 2");
        builder.AppendLine("  %concat_data = load ptr, ptr %concat_data_addr");
        builder.AppendLine("  %concat_capacity = load i64, ptr %concat_capacity_addr");
        builder.AppendLine($"  %concat_left_data = extractvalue {viewLlvmType} {leftValue}, 0");
        builder.AppendLine($"  %concat_left_length = extractvalue {viewLlvmType} {leftValue}, 1");
        builder.AppendLine($"  %concat_right_data = extractvalue {viewLlvmType} {rightValue}, 0");
        builder.AppendLine($"  %concat_right_length = extractvalue {viewLlvmType} {rightValue}, 1");
        builder.AppendLine("  %concat_left_nonnegative = icmp sge i64 %concat_left_length, 0");
        builder.AppendLine("  %concat_right_nonnegative = icmp sge i64 %concat_right_length, 0");
        builder.AppendLine("  %concat_capacity_nonnegative = icmp sge i64 %concat_capacity, 0");
        builder.AppendLine("  %concat_lengths_nonnegative = and i1 %concat_left_nonnegative, %concat_right_nonnegative");
        builder.AppendLine("  %concat_nonnegative_inputs = and i1 %concat_lengths_nonnegative, %concat_capacity_nonnegative");
        builder.AppendLine("  %concat_max_after_left = sub i64 9223372036854775807, %concat_left_length");
        builder.AppendLine("  %concat_no_length_overflow = icmp sle i64 %concat_right_length, %concat_max_after_left");
        builder.AppendLine("  %concat_required = add i64 %concat_left_length, %concat_right_length");
        builder.AppendLine("  %concat_valid_lengths = and i1 %concat_nonnegative_inputs, %concat_no_length_overflow");
        builder.AppendLine("  %concat_has_capacity = icmp sle i64 %concat_required, %concat_capacity");
        builder.AppendLine("  %concat_needs_storage = icmp ne i64 %concat_required, 0");
        builder.AppendLine("  %concat_has_data = icmp ne ptr %concat_data, null");
        builder.AppendLine("  %concat_storage_ready = select i1 %concat_needs_storage, i1 %concat_has_data, i1 true");
        builder.AppendLine("  %concat_size_ok = and i1 %concat_valid_lengths, %concat_has_capacity");
        builder.AppendLine("  %concat_success = and i1 %concat_size_ok, %concat_storage_ready");
        builder.AppendLine("  br i1 %concat_success, label %concat_copy_left_check, label %concat_fail");
        builder.AppendLine("concat_fail:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine("concat_copy_left_check:");
        builder.AppendLine("  %concat_left_nonempty = icmp ne i64 %concat_left_length, 0");
        builder.AppendLine("  br i1 %concat_left_nonempty, label %concat_copy_left, label %concat_after_left");
        builder.AppendLine("concat_copy_left:");
        if (unitType.BitWidth == 8)
        {
            builder.AppendLine("  %concat_left_bytes = add i64 %concat_left_length, 0");
        }
        else
        {
            builder.AppendLine($"  %concat_left_bytes = mul i64 %concat_left_length, {unitType.BitWidth / 8}");
        }
        builder.AppendLine("  call void @llvm.memcpy.p0.p0.i64(ptr %concat_data, ptr %concat_left_data, i64 %concat_left_bytes, i1 false)");
        builder.AppendLine("  br label %concat_after_left");
        builder.AppendLine("concat_after_left:");
        builder.AppendLine("  %concat_right_nonempty = icmp ne i64 %concat_right_length, 0");
        builder.AppendLine("  br i1 %concat_right_nonempty, label %concat_copy_right, label %concat_finish");
        builder.AppendLine("concat_copy_right:");
        builder.AppendLine($"  %concat_right_dest = getelementptr{GetProvenInObjectGepFlags()} {unitLlvmType}, ptr %concat_data, i64 %concat_left_length");
        if (unitType.BitWidth == 8)
        {
            builder.AppendLine("  %concat_right_bytes = add i64 %concat_right_length, 0");
        }
        else
        {
            builder.AppendLine($"  %concat_right_bytes = mul i64 %concat_right_length, {unitType.BitWidth / 8}");
        }
        builder.AppendLine("  call void @llvm.memcpy.p0.p0.i64(ptr %concat_right_dest, ptr %concat_right_data, i64 %concat_right_bytes, i1 false)");
        builder.AppendLine("  br label %concat_finish");
        builder.AppendLine("concat_finish:");
        builder.AppendLine("  store i64 %concat_required, ptr %concat_length_addr");
        builder.AppendLine("  ret i1 true");
    }

    private void EmitAsciiToUnicodeConversionBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 2)
        {
            throw new InvalidOperationException($"System.Text ASCII-to-Unicode conversion builtin '{abiFunction.Name}' expects exactly two user parameters.");
        }

        var destinationParameter = abiFunction.UserParameters[0];
        var sourceParameter = abiFunction.UserParameters[1];
        var aggregateType = destinationParameter.SourceType.ElementType is not null
            ? MapType(destinationParameter.SourceType.ElementType)
            : throw new InvalidOperationException($"System.Text ASCII-to-Unicode conversion builtin '{abiFunction.Name}' requires a raw pointer destination to a Unicode aggregate.");
        var viewLlvmType = MapType(sourceParameter.SourceType);
        var destinationPointer = $"%{EscapeIdentifier(destinationParameter.LlvmName)}";
        var fastLoopAccessGroupRef = _context.GetSelfReferentialMetadataRef(
            $"loop-access-group:{abiFunction.SymbolName}:ascii-to-unicode-fast",
            _ => "distinct !{}");
        var fastLoopAccessGroupSuffix = $", !llvm.access.group {fastLoopAccessGroupRef}";
        var mustProgressRef = _context.GetMetadataTupleRef(["!\"llvm.loop.mustprogress\""]);
        var parallelAccessRef = _context.GetMetadataTupleRef(["!\"llvm.loop.parallel_accesses\"", fastLoopAccessGroupRef]);
        var fastLoopRef = _context.GetSelfReferentialMetadataRef(
            $"loop:{abiFunction.SymbolName}:ascii-to-unicode-fast",
            selfRef => $"distinct !{{{selfRef}, {mustProgressRef}, {parallelAccessRef}}}");
        var fastLoopMetadataSuffix = $", !llvm.loop {fastLoopRef}";

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "convert_source_view");
        builder.AppendLine($"  %convert_destination_is_null = icmp eq ptr {destinationPointer}, null");
        builder.AppendLine("  br i1 %convert_destination_is_null, label %convert_destination_null, label %convert_destination_ready");
        builder.AppendLine("convert_destination_null:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine("convert_destination_ready:");
        builder.AppendLine($"  %convert_data_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 0");
        builder.AppendLine($"  %convert_length_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 1");
        builder.AppendLine($"  %convert_capacity_addr = getelementptr{GetProvenInObjectGepFlags()} {aggregateType}, ptr {destinationPointer}, i32 0, i32 2");
        builder.AppendLine("  %convert_data = load ptr, ptr %convert_data_addr");
        builder.AppendLine("  %convert_capacity = load i64, ptr %convert_capacity_addr");
        builder.AppendLine($"  %convert_source_data = extractvalue {viewLlvmType} {sourceValue}, 0");
        builder.AppendLine($"  %convert_source_length = extractvalue {viewLlvmType} {sourceValue}, 1");
        builder.AppendLine("  %convert_capacity_nonnegative = icmp sge i64 %convert_capacity, 0");
        builder.AppendLine("  %convert_source_length_nonnegative = icmp sge i64 %convert_source_length, 0");
        builder.AppendLine("  %convert_lengths_valid = and i1 %convert_capacity_nonnegative, %convert_source_length_nonnegative");
        builder.AppendLine("  br i1 %convert_lengths_valid, label %convert_check_storage, label %convert_fail");
        builder.AppendLine("convert_check_storage:");
        builder.AppendLine("  %convert_source_nonempty = icmp sgt i64 %convert_source_length, 0");
        builder.AppendLine("  %convert_has_destination_data = icmp ne ptr %convert_data, null");
        builder.AppendLine("  %convert_has_source_data = icmp ne ptr %convert_source_data, null");
        builder.AppendLine("  %convert_has_both_data = and i1 %convert_has_destination_data, %convert_has_source_data");
        builder.AppendLine("  %convert_storage_ready = select i1 %convert_source_nonempty, i1 %convert_has_both_data, i1 true");
        builder.AppendLine("  br i1 %convert_storage_ready, label %convert_fast_entry, label %convert_fail");
        builder.AppendLine("convert_fail:");
        builder.AppendLine("  store i64 0, ptr %convert_length_addr");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine("convert_fast_entry:");
        builder.AppendLine("  %convert_fits_as_unicode = icmp sle i64 %convert_source_length, %convert_capacity");
        builder.AppendLine("  br i1 %convert_fits_as_unicode, label %convert_fast_disjoint_check, label %convert_fallback_entry");
        builder.AppendLine("convert_fast_disjoint_check:");
        builder.AppendLine("  %convert_fast_source_empty = icmp eq i64 %convert_source_length, 0");
        builder.AppendLine("  br i1 %convert_fast_source_empty, label %convert_fast_loop, label %convert_fast_disjoint_nonempty");
        builder.AppendLine("convert_fast_disjoint_nonempty:");
        builder.AppendLine("  %convert_fast_source_end = getelementptr i8, ptr %convert_source_data, i64 %convert_source_length");
        builder.AppendLine("  %convert_fast_dest_end = getelementptr i32, ptr %convert_data, i64 %convert_source_length");
        builder.AppendLine("  %convert_fast_source_before_dest = icmp ule ptr %convert_fast_source_end, %convert_data");
        builder.AppendLine("  %convert_fast_dest_before_source = icmp ule ptr %convert_fast_dest_end, %convert_source_data");
        builder.AppendLine("  %convert_fast_disjoint = or i1 %convert_fast_source_before_dest, %convert_fast_dest_before_source");
        builder.AppendLine("  br i1 %convert_fast_disjoint, label %convert_fast_loop, label %convert_fail");
        builder.AppendLine("convert_fast_loop:");
        builder.AppendLine("  %convert_fast_index = phi i64 [ 0, %convert_fast_disjoint_check ], [ 0, %convert_fast_disjoint_nonempty ], [ %convert_fast_next, %convert_fast_store ]");
        builder.AppendLine("  %convert_fast_done = icmp eq i64 %convert_fast_index, %convert_source_length");
        builder.AppendLine("  br i1 %convert_fast_done, label %convert_success_source_length, label %convert_fast_load");
        builder.AppendLine("convert_fast_load:");
        builder.AppendLine("  %convert_fast_source_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_fast_index");
        builder.AppendLine($"  %convert_fast_unit = load i8, ptr %convert_fast_source_ptr{fastLoopAccessGroupSuffix}");
        builder.AppendLine("  %convert_fast_non_ascii = icmp slt i8 %convert_fast_unit, 0");
        builder.AppendLine("  br i1 %convert_fast_non_ascii, label %convert_fallback_entry, label %convert_fast_store");
        builder.AppendLine("convert_fast_store:");
        builder.AppendLine("  %convert_fast_dest_ptr = getelementptr i32, ptr %convert_data, i64 %convert_fast_index");
        builder.AppendLine("  %convert_fast_wide = zext i8 %convert_fast_unit to i32");
        builder.AppendLine($"  store i32 %convert_fast_wide, ptr %convert_fast_dest_ptr{fastLoopAccessGroupSuffix}");
        builder.AppendLine("  %convert_fast_next = add i64 %convert_fast_index, 1");
        builder.AppendLine($"  br label %convert_fast_loop{fastLoopMetadataSuffix}");
        builder.AppendLine("convert_success_source_length:");
        builder.AppendLine("  store i64 %convert_source_length, ptr %convert_length_addr");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("convert_fallback_entry:");
        builder.AppendLine("  br label %convert_fallback_loop");
        builder.AppendLine("convert_fallback_loop:");
        builder.AppendLine("  %convert_read_index = phi i64 [ 0, %convert_fallback_entry ], [ %convert_next_read, %convert_store_decoded ]");
        builder.AppendLine("  %convert_write_index = phi i64 [ 0, %convert_fallback_entry ], [ %convert_next_write, %convert_store_decoded ]");
        builder.AppendLine("  %convert_fallback_has_input = icmp slt i64 %convert_read_index, %convert_source_length");
        builder.AppendLine("  br i1 %convert_fallback_has_input, label %convert_fallback_capacity, label %convert_fallback_success");
        builder.AppendLine("convert_fallback_capacity:");
        builder.AppendLine("  %convert_fallback_has_capacity = icmp slt i64 %convert_write_index, %convert_capacity");
        builder.AppendLine("  br i1 %convert_fallback_has_capacity, label %convert_decode_first, label %convert_fail");
        builder.AppendLine("convert_fallback_success:");
        builder.AppendLine("  store i64 %convert_write_index, ptr %convert_length_addr");
        builder.AppendLine("  ret i1 true");
        builder.AppendLine("convert_decode_first:");
        builder.AppendLine("  %convert_remaining = sub i64 %convert_source_length, %convert_read_index");
        builder.AppendLine("  %convert_first_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_read_index");
        builder.AppendLine("  %convert_first_raw = load i8, ptr %convert_first_ptr");
        builder.AppendLine("  %convert_first = zext i8 %convert_first_raw to i32");
        builder.AppendLine("  %convert_first_is_ascii = icmp ule i32 %convert_first, 127");
        builder.AppendLine("  br i1 %convert_first_is_ascii, label %convert_decoded_ascii, label %convert_decode_two_check");
        builder.AppendLine("convert_decoded_ascii:");
        builder.AppendLine("  %convert_ascii_next_read = add i64 %convert_read_index, 1");
        builder.AppendLine("  br label %convert_store_decoded");
        builder.AppendLine("convert_decode_two_check:");
        builder.AppendLine("  %convert_first_ge_194 = icmp uge i32 %convert_first, 194");
        builder.AppendLine("  %convert_first_le_223 = icmp ule i32 %convert_first, 223");
        builder.AppendLine("  %convert_is_two_byte = and i1 %convert_first_ge_194, %convert_first_le_223");
        builder.AppendLine("  br i1 %convert_is_two_byte, label %convert_decode_two_length, label %convert_decode_three_check");
        builder.AppendLine("convert_decode_two_length:");
        builder.AppendLine("  %convert_has_two_bytes = icmp sge i64 %convert_remaining, 2");
        builder.AppendLine("  br i1 %convert_has_two_bytes, label %convert_decode_two_continuation, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_two_continuation:");
        builder.AppendLine("  %convert_two_index_1 = add i64 %convert_read_index, 1");
        builder.AppendLine("  %convert_second_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_two_index_1");
        builder.AppendLine("  %convert_second_raw = load i8, ptr %convert_second_ptr");
        builder.AppendLine("  %convert_second = zext i8 %convert_second_raw to i32");
        builder.AppendLine("  %convert_second_ge_128 = icmp uge i32 %convert_second, 128");
        builder.AppendLine("  %convert_second_le_191 = icmp ule i32 %convert_second, 191");
        builder.AppendLine("  %convert_second_valid = and i1 %convert_second_ge_128, %convert_second_le_191");
        builder.AppendLine("  br i1 %convert_second_valid, label %convert_decode_two_accept, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_two_accept:");
        builder.AppendLine("  %convert_two_first_bits = and i32 %convert_first, 31");
        builder.AppendLine("  %convert_two_first_shifted = shl i32 %convert_two_first_bits, 6");
        builder.AppendLine("  %convert_two_second_bits = and i32 %convert_second, 63");
        builder.AppendLine("  %convert_two_code_point = or i32 %convert_two_first_shifted, %convert_two_second_bits");
        builder.AppendLine("  %convert_two_next_read = add i64 %convert_read_index, 2");
        builder.AppendLine("  br label %convert_store_decoded");
        builder.AppendLine("convert_decode_three_check:");
        builder.AppendLine("  %convert_first_ge_224 = icmp uge i32 %convert_first, 224");
        builder.AppendLine("  %convert_first_le_239 = icmp ule i32 %convert_first, 239");
        builder.AppendLine("  %convert_is_three_byte = and i1 %convert_first_ge_224, %convert_first_le_239");
        builder.AppendLine("  br i1 %convert_is_three_byte, label %convert_decode_three_length, label %convert_decode_four_check");
        builder.AppendLine("convert_decode_three_length:");
        builder.AppendLine("  %convert_has_three_bytes = icmp sge i64 %convert_remaining, 3");
        builder.AppendLine("  br i1 %convert_has_three_bytes, label %convert_decode_three_continuations, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_three_continuations:");
        builder.AppendLine("  %convert_three_index_1 = add i64 %convert_read_index, 1");
        builder.AppendLine("  %convert_three_index_2 = add i64 %convert_read_index, 2");
        builder.AppendLine("  %convert_three_second_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_three_index_1");
        builder.AppendLine("  %convert_three_third_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_three_index_2");
        builder.AppendLine("  %convert_three_second_raw = load i8, ptr %convert_three_second_ptr");
        builder.AppendLine("  %convert_three_third_raw = load i8, ptr %convert_three_third_ptr");
        builder.AppendLine("  %convert_three_second = zext i8 %convert_three_second_raw to i32");
        builder.AppendLine("  %convert_three_third = zext i8 %convert_three_third_raw to i32");
        builder.AppendLine("  %convert_three_second_ge_128 = icmp uge i32 %convert_three_second, 128");
        builder.AppendLine("  %convert_three_second_le_191 = icmp ule i32 %convert_three_second, 191");
        builder.AppendLine("  %convert_three_second_valid = and i1 %convert_three_second_ge_128, %convert_three_second_le_191");
        builder.AppendLine("  %convert_three_third_ge_128 = icmp uge i32 %convert_three_third, 128");
        builder.AppendLine("  %convert_three_third_le_191 = icmp ule i32 %convert_three_third, 191");
        builder.AppendLine("  %convert_three_third_valid = and i1 %convert_three_third_ge_128, %convert_three_third_le_191");
        builder.AppendLine("  %convert_three_continuations_valid = and i1 %convert_three_second_valid, %convert_three_third_valid");
        builder.AppendLine("  %convert_three_is_e0 = icmp eq i32 %convert_first, 224");
        builder.AppendLine("  %convert_three_second_under_160 = icmp ult i32 %convert_three_second, 160");
        builder.AppendLine("  %convert_three_overlong = and i1 %convert_three_is_e0, %convert_three_second_under_160");
        builder.AppendLine("  %convert_three_is_ed = icmp eq i32 %convert_first, 237");
        builder.AppendLine("  %convert_three_second_at_or_after_160 = icmp uge i32 %convert_three_second, 160");
        builder.AppendLine("  %convert_three_surrogate = and i1 %convert_three_is_ed, %convert_three_second_at_or_after_160");
        builder.AppendLine("  %convert_three_range_invalid = or i1 %convert_three_overlong, %convert_three_surrogate");
        builder.AppendLine("  %convert_three_range_valid = xor i1 %convert_three_range_invalid, true");
        builder.AppendLine("  %convert_three_valid = and i1 %convert_three_continuations_valid, %convert_three_range_valid");
        builder.AppendLine("  br i1 %convert_three_valid, label %convert_decode_three_accept, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_three_accept:");
        builder.AppendLine("  %convert_three_first_bits = and i32 %convert_first, 15");
        builder.AppendLine("  %convert_three_first_shifted = shl i32 %convert_three_first_bits, 12");
        builder.AppendLine("  %convert_three_second_bits = and i32 %convert_three_second, 63");
        builder.AppendLine("  %convert_three_second_shifted = shl i32 %convert_three_second_bits, 6");
        builder.AppendLine("  %convert_three_partial = or i32 %convert_three_first_shifted, %convert_three_second_shifted");
        builder.AppendLine("  %convert_three_third_bits = and i32 %convert_three_third, 63");
        builder.AppendLine("  %convert_three_code_point = or i32 %convert_three_partial, %convert_three_third_bits");
        builder.AppendLine("  %convert_three_next_read = add i64 %convert_read_index, 3");
        builder.AppendLine("  br label %convert_store_decoded");
        builder.AppendLine("convert_decode_four_check:");
        builder.AppendLine("  %convert_first_ge_240 = icmp uge i32 %convert_first, 240");
        builder.AppendLine("  %convert_first_le_244 = icmp ule i32 %convert_first, 244");
        builder.AppendLine("  %convert_is_four_byte = and i1 %convert_first_ge_240, %convert_first_le_244");
        builder.AppendLine("  br i1 %convert_is_four_byte, label %convert_decode_four_length, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_four_length:");
        builder.AppendLine("  %convert_has_four_bytes = icmp sge i64 %convert_remaining, 4");
        builder.AppendLine("  br i1 %convert_has_four_bytes, label %convert_decode_four_continuations, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_four_continuations:");
        builder.AppendLine("  %convert_four_index_1 = add i64 %convert_read_index, 1");
        builder.AppendLine("  %convert_four_index_2 = add i64 %convert_read_index, 2");
        builder.AppendLine("  %convert_four_index_3 = add i64 %convert_read_index, 3");
        builder.AppendLine("  %convert_four_second_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_four_index_1");
        builder.AppendLine("  %convert_four_third_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_four_index_2");
        builder.AppendLine("  %convert_four_fourth_ptr = getelementptr i8, ptr %convert_source_data, i64 %convert_four_index_3");
        builder.AppendLine("  %convert_four_second_raw = load i8, ptr %convert_four_second_ptr");
        builder.AppendLine("  %convert_four_third_raw = load i8, ptr %convert_four_third_ptr");
        builder.AppendLine("  %convert_four_fourth_raw = load i8, ptr %convert_four_fourth_ptr");
        builder.AppendLine("  %convert_four_second = zext i8 %convert_four_second_raw to i32");
        builder.AppendLine("  %convert_four_third = zext i8 %convert_four_third_raw to i32");
        builder.AppendLine("  %convert_four_fourth = zext i8 %convert_four_fourth_raw to i32");
        builder.AppendLine("  %convert_four_second_ge_128 = icmp uge i32 %convert_four_second, 128");
        builder.AppendLine("  %convert_four_second_le_191 = icmp ule i32 %convert_four_second, 191");
        builder.AppendLine("  %convert_four_second_valid = and i1 %convert_four_second_ge_128, %convert_four_second_le_191");
        builder.AppendLine("  %convert_four_third_ge_128 = icmp uge i32 %convert_four_third, 128");
        builder.AppendLine("  %convert_four_third_le_191 = icmp ule i32 %convert_four_third, 191");
        builder.AppendLine("  %convert_four_third_valid = and i1 %convert_four_third_ge_128, %convert_four_third_le_191");
        builder.AppendLine("  %convert_four_fourth_ge_128 = icmp uge i32 %convert_four_fourth, 128");
        builder.AppendLine("  %convert_four_fourth_le_191 = icmp ule i32 %convert_four_fourth, 191");
        builder.AppendLine("  %convert_four_fourth_valid = and i1 %convert_four_fourth_ge_128, %convert_four_fourth_le_191");
        builder.AppendLine("  %convert_four_first_pair_valid = and i1 %convert_four_second_valid, %convert_four_third_valid");
        builder.AppendLine("  %convert_four_continuations_valid = and i1 %convert_four_first_pair_valid, %convert_four_fourth_valid");
        builder.AppendLine("  %convert_four_is_f0 = icmp eq i32 %convert_first, 240");
        builder.AppendLine("  %convert_four_second_under_144 = icmp ult i32 %convert_four_second, 144");
        builder.AppendLine("  %convert_four_overlong = and i1 %convert_four_is_f0, %convert_four_second_under_144");
        builder.AppendLine("  %convert_four_is_f4 = icmp eq i32 %convert_first, 244");
        builder.AppendLine("  %convert_four_second_after_143 = icmp ugt i32 %convert_four_second, 143");
        builder.AppendLine("  %convert_four_too_large = and i1 %convert_four_is_f4, %convert_four_second_after_143");
        builder.AppendLine("  %convert_four_range_invalid = or i1 %convert_four_overlong, %convert_four_too_large");
        builder.AppendLine("  %convert_four_range_valid = xor i1 %convert_four_range_invalid, true");
        builder.AppendLine("  %convert_four_valid = and i1 %convert_four_continuations_valid, %convert_four_range_valid");
        builder.AppendLine("  br i1 %convert_four_valid, label %convert_decode_four_accept, label %convert_decode_invalid");
        builder.AppendLine("convert_decode_four_accept:");
        builder.AppendLine("  %convert_four_first_bits = and i32 %convert_first, 7");
        builder.AppendLine("  %convert_four_first_shifted = shl i32 %convert_four_first_bits, 18");
        builder.AppendLine("  %convert_four_second_bits = and i32 %convert_four_second, 63");
        builder.AppendLine("  %convert_four_second_shifted = shl i32 %convert_four_second_bits, 12");
        builder.AppendLine("  %convert_four_third_bits = and i32 %convert_four_third, 63");
        builder.AppendLine("  %convert_four_third_shifted = shl i32 %convert_four_third_bits, 6");
        builder.AppendLine("  %convert_four_partial_0 = or i32 %convert_four_first_shifted, %convert_four_second_shifted");
        builder.AppendLine("  %convert_four_partial_1 = or i32 %convert_four_partial_0, %convert_four_third_shifted");
        builder.AppendLine("  %convert_four_fourth_bits = and i32 %convert_four_fourth, 63");
        builder.AppendLine("  %convert_four_code_point = or i32 %convert_four_partial_1, %convert_four_fourth_bits");
        builder.AppendLine("  %convert_four_next_read = add i64 %convert_read_index, 4");
        builder.AppendLine("  br label %convert_store_decoded");
        builder.AppendLine("convert_decode_invalid:");
        builder.AppendLine("  %convert_invalid_next_read = add i64 %convert_read_index, 1");
        builder.AppendLine("  br label %convert_store_decoded");
        builder.AppendLine("convert_store_decoded:");
        builder.AppendLine("  %convert_code_point = phi i32 [ %convert_first, %convert_decoded_ascii ], [ %convert_two_code_point, %convert_decode_two_accept ], [ %convert_three_code_point, %convert_decode_three_accept ], [ %convert_four_code_point, %convert_decode_four_accept ], [ 65533, %convert_decode_invalid ]");
        builder.AppendLine("  %convert_next_read = phi i64 [ %convert_ascii_next_read, %convert_decoded_ascii ], [ %convert_two_next_read, %convert_decode_two_accept ], [ %convert_three_next_read, %convert_decode_three_accept ], [ %convert_four_next_read, %convert_decode_four_accept ], [ %convert_invalid_next_read, %convert_decode_invalid ]");
        builder.AppendLine("  %convert_fallback_dest_ptr = getelementptr i32, ptr %convert_data, i64 %convert_write_index");
        builder.AppendLine("  store i32 %convert_code_point, ptr %convert_fallback_dest_ptr");
        builder.AppendLine("  %convert_next_write = add i64 %convert_write_index, 1");
        builder.AppendLine("  br label %convert_fallback_loop");
    }

    private bool UsesSystemTextConcatBuiltin(IEnumerable<TypedFunctionSignature> signatures)
    {
        return IsSystemTextBuiltinHostModule(CurrentModuleName)
            && signatures.Any(static signature =>
                string.Equals(signature.Name, "TryConcatAscii", StringComparison.Ordinal)
                || string.Equals(signature.Name, "TryConcatUnicode", StringComparison.Ordinal));
    }

    private bool UsesAsciiToUnicodeLiteralMemcpySpecialization()
    {
        foreach (var function in _enumerateSsaFunctions())
        {
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is not SsaValueInstruction
                        {
                            Value: SsaCallRValue
                            {
                                Arguments:
                                [
                                    _,
                                    SsaStringConstant
                                    {
                                        Type.Kind: StarkTypeKind.Ascii
                                    } source
                                ]
                            } call
                        }
                        || !IsPotentialTryConvertAsciiToUnicodeCall(call.FunctionName)
                        || !TextLiteralDecoder.TryDecode(
                            source.LiteralText,
                            source.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                            out var decoded,
                            out _)
                        || !decoded.IsAscii
                        || decoded.Utf8Bytes.Length < LlvmTextOptimizationConstants.AsciiToUnicodeLiteralMemcpyThresholdCodeUnits)
                    {
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPotentialTryConvertAsciiToUnicodeCall(string functionName)
    {
        return string.Equals(functionName, "TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || string.Equals(functionName, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || functionName.EndsWith(".TryConvertAsciiToUnicode", StringComparison.Ordinal);
    }

    private string MaterializeAggregateBuiltinParameterValue(
        StringBuilder builder,
        AbiParameterSymbol parameter,
        string localName)
    {
        var incomingValue = $"%{EscapeIdentifier(parameter.LlvmName)}";
        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            return incomingValue;
        }

        var loadedValue = $"%{EscapeIdentifier(localName)}";
        builder.AppendLine($"  {loadedValue} = load {MapType(parameter.SourceType)}, ptr {incomingValue}");
        return loadedValue;
    }

    public IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? GetBuiltinParameterEffects(
        string moduleName,
        string functionName,
        TypedFunctionSignature function)
    {
        if (TryGetSystemCollectionsBuiltin(moduleName, function.TemplateName ?? function.DisplaySourceName, out var collectionsBuiltinKind)
            || TryGetSystemCollectionsBuiltin(moduleName, functionName, out collectionsBuiltinKind))
        {
            return collectionsBuiltinKind switch
            {
                SystemCollectionsBuiltinKind.ListAsSlice or SystemCollectionsBuiltinKind.ListAsMutableSlice
                    => function.Parameters.ToDictionary(
                        static parameter => parameter.Name,
                        static parameter => new ParameterMemoryEffectSummary(
                            parameter.Name,
                            parameter.Type.DisplayName,
                            IsMemoryBacked: true,
                            GuaranteedNonNull: true,
                            GuaranteedReadOnly: !parameter.Type.IsMutableView,
                            GuaranteedWriteOnly: false,
                            GuaranteedNoAlias: parameter.Type.IsMutableView,
                            DereferenceableBytes: null,
                            AlignmentBytes: null,
                            Reads: true,
                            Writes: false,
                            CaptureKind: ParameterCaptureKind.Return),
                        StringComparer.Ordinal),
                SystemCollectionsBuiltinKind.DictionaryKeyEquals or SystemCollectionsBuiltinKind.DictionaryKeyHash
                    => function.Parameters.ToDictionary(
                        static parameter => parameter.Name,
                        static parameter => new ParameterMemoryEffectSummary(
                            parameter.Name,
                            parameter.Type.DisplayName,
                            IsMemoryBacked: true,
                            GuaranteedNonNull: true,
                            GuaranteedReadOnly: true,
                            GuaranteedWriteOnly: false,
                            GuaranteedNoAlias: false,
                            DereferenceableBytes: null,
                            AlignmentBytes: null,
                            Reads: true,
                            Writes: false,
                            CaptureKind: ParameterCaptureKind.None),
                        StringComparer.Ordinal),
                _ => null
            };
        }

        if (TryResolveSystemRuntimeBuiltin(moduleName, function, out var runtimeBuiltinKind)
            || TryGetSystemRuntimeBuiltin(moduleName, functionName, out runtimeBuiltinKind))
        {
            return runtimeBuiltinKind switch
            {
                SystemRuntimeBuiltinKind.GetByteSliceParts
                    => function.Parameters.ToDictionary(
                        static parameter => parameter.Name,
                        static parameter => new ParameterMemoryEffectSummary(
                            parameter.Name,
                            parameter.Type.DisplayName,
                            IsMemoryBacked: true,
                            GuaranteedNonNull: false,
                            GuaranteedReadOnly: true,
                            GuaranteedWriteOnly: false,
                            GuaranteedNoAlias: false,
                            DereferenceableBytes: null,
                            AlignmentBytes: null,
                            Reads: true,
                            Writes: false,
                            CaptureKind: ParameterCaptureKind.Return),
                        StringComparer.Ordinal),
                SystemRuntimeBuiltinKind.GetMutableByteSliceParts
                    => function.Parameters.ToDictionary(
                        static parameter => parameter.Name,
                        static parameter => new ParameterMemoryEffectSummary(
                            parameter.Name,
                            parameter.Type.DisplayName,
                            IsMemoryBacked: true,
                            GuaranteedNonNull: false,
                            GuaranteedReadOnly: false,
                            GuaranteedWriteOnly: false,
                            GuaranteedNoAlias: parameter.Type.IsMutableView,
                            DereferenceableBytes: null,
                            AlignmentBytes: null,
                            Reads: true,
                            Writes: false,
                            CaptureKind: ParameterCaptureKind.Return),
                        StringComparer.Ordinal),
                _ => null
            };
        }

        if (!TryGetSystemTextBuiltin(moduleName, functionName, out var builtinKind))
        {
            return null;
        }

        return builtinKind switch
        {
            SystemTextBuiltinKind.AsciiView or SystemTextBuiltinKind.UnicodeView
                or SystemTextBuiltinKind.AsciiData or SystemTextBuiltinKind.UnicodeData
                or SystemTextBuiltinKind.AsciiLength or SystemTextBuiltinKind.UnicodeLength
                => function.Parameters.ToDictionary(
                    static parameter => parameter.Name,
                    static parameter => new ParameterMemoryEffectSummary(
                        parameter.Name,
                        parameter.Type.DisplayName,
                        IsMemoryBacked: true,
                        GuaranteedNonNull: true,
                        GuaranteedReadOnly: true,
                        GuaranteedWriteOnly: false,
                        GuaranteedNoAlias: true,
                        DereferenceableBytes: null,
                        AlignmentBytes: null,
                        Reads: true,
                        Writes: false,
                        CaptureKind: ParameterCaptureKind.None),
                    StringComparer.Ordinal),
            SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode
                or SystemTextBuiltinKind.TryConvertAsciiToUnicode
                => function.Parameters.ToDictionary(
                    static parameter => parameter.Name,
                    static parameter => new ParameterMemoryEffectSummary(
                        parameter.Name,
                        parameter.Type.DisplayName,
                        IsMemoryBacked: parameter.Name == "destination",
                        GuaranteedNonNull: false,
                        GuaranteedReadOnly: false,
                        GuaranteedWriteOnly: false,
                        GuaranteedNoAlias: false,
                        DereferenceableBytes: null,
                        AlignmentBytes: null,
                        Reads: parameter.Name == "destination",
                        Writes: parameter.Name == "destination",
                        CaptureKind: ParameterCaptureKind.None),
                    StringComparer.Ordinal),
            _ => null
        };
    }

    private static bool TryGetSystemRuntimeBuiltin(
        string moduleName,
        string functionName,
        out SystemRuntimeBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Runtime.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Runtime", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "GetByteSliceParts" => SystemRuntimeBuiltinKind.GetByteSliceParts,
            "GetMutableByteSliceParts" => SystemRuntimeBuiltinKind.GetMutableByteSliceParts,
            _ => default
        };

        return sourceName is "GetByteSliceParts" or "GetMutableByteSliceParts";
    }

    private static bool TryResolveSystemRuntimeBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemRuntimeBuiltinKind builtinKind)
    {
        return TryGetSystemRuntimeBuiltin(moduleName, function.TemplateName ?? function.DisplaySourceName, out builtinKind)
            || TryGetSystemRuntimeBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemRuntimeBuiltin(moduleName: string.Empty, function.TemplateName ?? function.Name, out builtinKind);
    }

    private static bool TryGetSystemTextBuiltin(
        string moduleName,
        string functionName,
        out SystemTextBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string systemTextPrefix = "System.Text.";
            if (functionName.StartsWith(systemTextPrefix, StringComparison.Ordinal))
            {
                sourceName = functionName[systemTextPrefix.Length..];
            }
            else
            {
                return false;
            }
        }
        else
        {
            if (!IsSystemTextBuiltinHostModule(moduleName))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "AsciiView" => SystemTextBuiltinKind.AsciiView,
            "UnicodeView" => SystemTextBuiltinKind.UnicodeView,
            "AsciiData" => SystemTextBuiltinKind.AsciiData,
            "UnicodeData" => SystemTextBuiltinKind.UnicodeData,
            "AsciiLength" => SystemTextBuiltinKind.AsciiLength,
            "UnicodeLength" => SystemTextBuiltinKind.UnicodeLength,
            "TryConcatAscii" => SystemTextBuiltinKind.TryConcatAscii,
            "TryConcatUnicode" => SystemTextBuiltinKind.TryConcatUnicode,
            "TryConvertAsciiToUnicode" => SystemTextBuiltinKind.TryConvertAsciiToUnicode,
            _ => default
        };

        return sourceName is
            "AsciiView" or "UnicodeView"
            or "AsciiData" or "UnicodeData"
            or "AsciiLength" or "UnicodeLength"
            or "TryConcatAscii" or "TryConcatUnicode"
            or "TryConvertAsciiToUnicode";
    }

    private static bool IsSystemTextBuiltinHostModule(string moduleName)
    {
        return string.Equals(moduleName, "System.Text", StringComparison.Ordinal)
            || string.Equals(moduleName, "System.Runtime.Platform.Linux", StringComparison.Ordinal)
            || string.Equals(moduleName, "System.Runtime.Platform.Windows", StringComparison.Ordinal);
    }

    private static bool TryGetSystemMathBuiltin(
        string moduleName,
        string functionName,
        out SystemMathBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Math.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Math", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "Sin" => SystemMathBuiltinKind.Sin,
            "Cos" => SystemMathBuiltinKind.Cos,
            "Tan" => SystemMathBuiltinKind.Tan,
            "Exp" => SystemMathBuiltinKind.Exp,
            "Exp2" => SystemMathBuiltinKind.Exp2,
            "Log" => SystemMathBuiltinKind.Log,
            "Log2" => SystemMathBuiltinKind.Log2,
            "Log10" => SystemMathBuiltinKind.Log10,
            "Asin" => SystemMathBuiltinKind.Asin,
            "Acos" => SystemMathBuiltinKind.Acos,
            "Atan" => SystemMathBuiltinKind.Atan,
            "Atan2" => SystemMathBuiltinKind.Atan2,
            "Pow" => SystemMathBuiltinKind.Pow,
            "Sinh" => SystemMathBuiltinKind.Sinh,
            "Cosh" => SystemMathBuiltinKind.Cosh,
            "Tanh" => SystemMathBuiltinKind.Tanh,
            "SinCos" => SystemMathBuiltinKind.SinCos,
            "Sqrt" => SystemMathBuiltinKind.Sqrt,
            "FusedMultiplyAdd" => SystemMathBuiltinKind.FusedMultiplyAdd,
            "ReciprocalEstimate" => SystemMathBuiltinKind.ReciprocalEstimate,
            "ReciprocalSqrtEstimate" => SystemMathBuiltinKind.ReciprocalSqrtEstimate,
            "Ceiling" => SystemMathBuiltinKind.Ceiling,
            "Floor" => SystemMathBuiltinKind.Floor,
            "Truncate" => SystemMathBuiltinKind.Truncate,
            "Round" => SystemMathBuiltinKind.Round,
            "Min" => SystemMathBuiltinKind.Min,
            "Max" => SystemMathBuiltinKind.Max,
            _ => default
        };

        return sourceName is
            "Sin" or "Cos" or "Tan"
            or "Exp" or "Exp2"
            or "Log" or "Log2" or "Log10"
            or "Asin" or "Acos" or "Atan" or "Atan2"
            or "Pow"
            or "Sinh" or "Cosh" or "Tanh"
            or "SinCos"
            or "Sqrt" or "FusedMultiplyAdd" or "ReciprocalEstimate" or "ReciprocalSqrtEstimate"
            or "Ceiling" or "Floor" or "Truncate" or "Round"
            or "Min" or "Max";
    }

    private static bool TryResolveSystemMathBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemMathBuiltinKind builtinKind)
    {
        return TryGetSystemMathBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemMathBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private static int GetSystemMathIntrinsicArity(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMathBuiltinKind.Atan2 or SystemMathBuiltinKind.Pow => 2,
            SystemMathBuiltinKind.FusedMultiplyAdd => 3,
            SystemMathBuiltinKind.Min or SystemMathBuiltinKind.Max => 2,
            _ => 1
        };
    }

    private static bool IsLlvmIntrinsicSystemMathBuiltin(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind is
            SystemMathBuiltinKind.Sin
            or SystemMathBuiltinKind.Cos
            or SystemMathBuiltinKind.Tan
            or SystemMathBuiltinKind.Exp
            or SystemMathBuiltinKind.Exp2
            or SystemMathBuiltinKind.Log
            or SystemMathBuiltinKind.Log2
            or SystemMathBuiltinKind.Log10
            or SystemMathBuiltinKind.Asin
            or SystemMathBuiltinKind.Acos
            or SystemMathBuiltinKind.Atan
            or SystemMathBuiltinKind.Atan2
            or SystemMathBuiltinKind.Pow
            or SystemMathBuiltinKind.Sinh
            or SystemMathBuiltinKind.Cosh
            or SystemMathBuiltinKind.Tanh
            or SystemMathBuiltinKind.SinCos
            or SystemMathBuiltinKind.Min
            or SystemMathBuiltinKind.Max;
    }

    private static bool IsHardwareAsmSystemMathBuiltin(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind is
            SystemMathBuiltinKind.Sqrt
            or SystemMathBuiltinKind.FusedMultiplyAdd
            or SystemMathBuiltinKind.ReciprocalEstimate
            or SystemMathBuiltinKind.ReciprocalSqrtEstimate
            or SystemMathBuiltinKind.Ceiling
            or SystemMathBuiltinKind.Floor
            or SystemMathBuiltinKind.Truncate
            or SystemMathBuiltinKind.Round;
    }

    private static string GetSystemMathIntrinsicBaseName(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMathBuiltinKind.Sin => "sin",
            SystemMathBuiltinKind.Cos => "cos",
            SystemMathBuiltinKind.Tan => "tan",
            SystemMathBuiltinKind.Exp => "exp",
            SystemMathBuiltinKind.Exp2 => "exp2",
            SystemMathBuiltinKind.Log => "log",
            SystemMathBuiltinKind.Log2 => "log2",
            SystemMathBuiltinKind.Log10 => "log10",
            SystemMathBuiltinKind.Asin => "asin",
            SystemMathBuiltinKind.Acos => "acos",
            SystemMathBuiltinKind.Atan => "atan",
            SystemMathBuiltinKind.Atan2 => "atan2",
            SystemMathBuiltinKind.Pow => "pow",
            SystemMathBuiltinKind.Sinh => "sinh",
            SystemMathBuiltinKind.Cosh => "cosh",
            SystemMathBuiltinKind.Tanh => "tanh",
            SystemMathBuiltinKind.SinCos => "sincos",
            SystemMathBuiltinKind.Min => "minnum",
            SystemMathBuiltinKind.Max => "maxnum",
            _ => throw new InvalidOperationException($"Unsupported System.Math builtin '{builtinKind}'.")
        };
    }

    private static bool TryGetSystemBitOperationsBuiltin(
        string moduleName,
        string functionName,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.BitOperations.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.BitOperations", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "LeadingZeroCount" => SystemBitOperationsBuiltinKind.LeadingZeroCount,
            "TrailingZeroCount" => SystemBitOperationsBuiltinKind.TrailingZeroCount,
            "PopCount" => SystemBitOperationsBuiltinKind.PopCount,
            "RotateLeft" => SystemBitOperationsBuiltinKind.RotateLeft,
            "RotateRight" => SystemBitOperationsBuiltinKind.RotateRight,
            _ => default
        };

        return sourceName is
            "LeadingZeroCount"
            or "TrailingZeroCount"
            or "PopCount"
            or "RotateLeft"
            or "RotateRight";
    }

    private static bool TryResolveSystemBitOperationsBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        return TryGetSystemBitOperationsBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemBitOperationsBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private static bool TryGetSystemMemoryBuiltin(
        string moduleName,
        string functionName,
        out SystemMemoryBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Memory.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Memory", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "Allocate" => SystemMemoryBuiltinKind.Allocate,
            "Reallocate" => SystemMemoryBuiltinKind.Reallocate,
            "Free" => SystemMemoryBuiltinKind.Free,
            _ => default
        };

        return sourceName is "Allocate" or "Reallocate" or "Free";
    }

    private static bool TryResolveSystemMemoryBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemMemoryBuiltinKind builtinKind)
    {
        return TryGetSystemMemoryBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemMemoryBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private static bool TryGetSystemCollectionsBuiltin(
        string moduleName,
        string functionName,
        out SystemCollectionsBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Collections.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Collections", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "List.AsSlice" => SystemCollectionsBuiltinKind.ListAsSlice,
            "List.AsMutableSlice" => SystemCollectionsBuiltinKind.ListAsMutableSlice,
            "DictionaryKey.Equals" => SystemCollectionsBuiltinKind.DictionaryKeyEquals,
            "DictionaryKey.Hash" => SystemCollectionsBuiltinKind.DictionaryKeyHash,
            _ => default
        };

        return sourceName is "List.AsSlice" or "List.AsMutableSlice" or "DictionaryKey.Equals" or "DictionaryKey.Hash";
    }

    private static bool TryResolveSystemCollectionsBuiltin(
        string moduleName,
        TypedFunctionSignature function,
        out SystemCollectionsBuiltinKind builtinKind)
    {
        if (TryGetSystemCollectionsBuiltin(moduleName, function.TemplateName ?? function.DisplaySourceName, out builtinKind)
            || TryGetSystemCollectionsBuiltin(moduleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemCollectionsBuiltin(moduleName: string.Empty, function.TemplateName ?? function.Name, out builtinKind))
        {
            return builtinKind is not (SystemCollectionsBuiltinKind.DictionaryKeyEquals or SystemCollectionsBuiltinKind.DictionaryKeyHash)
                || !function.IsGeneric
                || function.IsGenericInstantiation;
        }

        return false;
    }

    private static int GetSystemBitOperationsSurfaceArity(SystemBitOperationsBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.RotateLeft or SystemBitOperationsBuiltinKind.RotateRight => 2,
            _ => 1
        };
    }

    private static string GetSystemBitOperationsIntrinsicBaseName(SystemBitOperationsBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.LeadingZeroCount => "ctlz",
            SystemBitOperationsBuiltinKind.TrailingZeroCount => "cttz",
            SystemBitOperationsBuiltinKind.PopCount => "ctpop",
            SystemBitOperationsBuiltinKind.RotateLeft => "fshl",
            SystemBitOperationsBuiltinKind.RotateRight => "fshr",
            _ => throw new InvalidOperationException($"Unsupported System.BitOperations builtin '{builtinKind}'.")
        };
    }

    private static string DescribeAsmArchitecture(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x86_64",
            StarkAsmArchitecture.AArch64 => "aarch64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "unknown"
        };
    }

    private StarkTypeSymbol ValidateSystemMathBuiltinSignature(
        TypedFunctionSignature function,
        SystemMathBuiltinKind builtinKind,
        int arity)
    {
        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            return ValidateSystemMathSinCosBuiltinSignature(function).ScalarType;
        }

        if (function.ReturnType.Kind != StarkTypeKind.Float)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' requires a floating-point return type.");
        }

        if (function.Parameters.Count != arity)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly {arity} parameter(s).");
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Type.Kind != StarkTypeKind.Float
                || parameter.Type.BitWidth != function.ReturnType.BitWidth)
            {
                throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' requires all parameters to match the floating-point return type '{function.ReturnType.DisplayName}'.");
            }
        }

        if ((builtinKind is SystemMathBuiltinKind.ReciprocalEstimate or SystemMathBuiltinKind.ReciprocalSqrtEstimate)
            && function.ReturnType.BitWidth != 32)
        {
            throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' currently supports only 'f32' because the shared single-instruction surface is single-precision.");
        }

        return function.ReturnType;
    }

    private StarkTypeSymbol ValidateSystemBitOperationsBuiltinSignature(
        TypedFunctionSignature function,
        SystemBitOperationsBuiltinKind builtinKind,
        int arity)
    {
        if (function.ReturnType.Kind != StarkTypeKind.Integer)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{function.Name}' requires an integer return type.");
        }

        if (function.ReturnType.BitWidth is not (32 or 64))
        {
            throw new InvalidOperationException(
                $"System.BitOperations builtin '{function.Name}' currently supports only 'i32' and 'i64', but found '{function.ReturnType.DisplayName}'.");
        }

        if (function.Parameters.Count != arity)
        {
            throw new InvalidOperationException($"System.BitOperations builtin '{function.Name}' expects exactly {arity} parameter(s).");
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Type.Kind != StarkTypeKind.Integer
                || parameter.Type.BitWidth != function.ReturnType.BitWidth)
            {
                throw new InvalidOperationException(
                    $"System.BitOperations builtin '{function.Name}' requires all parameters to match the integer return type '{function.ReturnType.DisplayName}'.");
            }
        }

        return function.ReturnType;
    }

    private void ValidateSystemMemoryBuiltinSignature(
        TypedFunctionSignature function,
        SystemMemoryBuiltinKind builtinKind)
    {
        switch (builtinKind)
        {
            case SystemMemoryBuiltinKind.Allocate:
                if (!IsSystemMemoryNamedType(function.ReturnType, "Allocation")
                    || function.Parameters.Count != 3
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocator")
                    || !IsAllocatorSizeInteger(function.Parameters[1].Type)
                    || !IsAllocatorSizeInteger(function.Parameters[2].Type))
                {
                    throw new InvalidOperationException(
                        $"System.Memory builtin '{function.Name}' must have signature 'Allocation Allocate(Allocator allocator, i64 byteLength, i64 alignment)'.");
                }

                break;
            case SystemMemoryBuiltinKind.Reallocate:
                if (!IsSystemMemoryNamedType(function.ReturnType, "Allocation")
                    || function.Parameters.Count != 3
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocation")
                    || !IsAllocatorSizeInteger(function.Parameters[1].Type)
                    || !IsAllocatorSizeInteger(function.Parameters[2].Type))
                {
                    throw new InvalidOperationException(
                        $"System.Memory builtin '{function.Name}' must have signature 'Allocation Reallocate(Allocation allocation, i64 byteLength, i64 alignment)'.");
                }

                break;
            case SystemMemoryBuiltinKind.Free:
                if (function.ReturnType.Kind != StarkTypeKind.Void
                    || function.Parameters.Count != 1
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocation"))
                {
                    throw new InvalidOperationException(
                        $"System.Memory builtin '{function.Name}' must have signature 'void Free(Allocation allocation)'.");
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported System.Memory builtin '{builtinKind}'.");
        }
    }

    private SystemCollectionsListShape ValidateSystemCollectionsListSliceSignature(
        TypedFunctionSignature function,
        SystemCollectionsBuiltinKind builtinKind)
    {
        if (function.Parameters.Count != 1
            || function.Parameters[0].Type.Kind != StarkTypeKind.Named
            || function.Parameters[0].Type.BorrowKind != StarkBorrowKind.Borrow
            || ResolveNamedTypeSymbol(function.Parameters[0].Type) is not { } listType
            || !string.Equals(
                StarkTypeSymbols.GetGenericBaseName(function.Parameters[0].Type.NamedType ?? string.Empty),
                "System.Collections.List",
                StringComparison.Ordinal)
            || function.ReturnType.Kind != StarkTypeKind.Slice
            || function.ReturnType.BorrowKind != StarkBorrowKind.RetBorrow)
        {
            throw new InvalidOperationException(
                $"System.Collections builtin '{function.Name}' must have signature 'retborrow T[] List.AsSlice(borrow List<T> self)' or 'retborrow mut T[] List.AsMutableSlice(mut borrow List<T> self)'.");
        }

        if (builtinKind == SystemCollectionsBuiltinKind.ListAsMutableSlice
            && (!function.Parameters[0].Type.IsMutableView || !function.ReturnType.IsMutableView))
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' must use mutable receiver and return a mutable retborrow slice.");
        }

        if (!listType.TryGetField("Data", out var dataField, out var dataFieldIndex)
            || dataField.Type.Kind != StarkTypeKind.RawPointer
            || !listType.TryGetField("Length", out var lengthField, out var lengthFieldIndex)
            || lengthField.Type.Kind != StarkTypeKind.Integer)
        {
            throw new InvalidOperationException("System.Collections List<T> must contain Data and Length fields for slice-view builtins.");
        }

        return new SystemCollectionsListShape(dataFieldIndex, lengthFieldIndex);
    }

    private static StarkTypeSymbol ValidateSystemCollectionsDictionaryKeySignature(
        TypedFunctionSignature function,
        int expectedParameterCount)
    {
        if (function.Parameters.Count != expectedParameterCount)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' expects {expectedParameterCount} key parameter(s).");
        }

        if (expectedParameterCount == 1)
        {
            if (function.ReturnType.Kind != StarkTypeKind.Integer || function.ReturnType.BitWidth != 64)
            {
                throw new InvalidOperationException($"System.Collections builtin '{function.Name}' must return 'u64[0 max]'.");
            }
        }
        else if (function.ReturnType.Kind != StarkTypeKind.Bool)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' must return 'bool'.");
        }

        var keyType = StarkTypeSymbols.WithQualifiers(
            function.Parameters[0].Type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (function.Parameters[0].Type.BorrowKind == StarkBorrowKind.None)
        {
            throw new InvalidOperationException($"System.Collections builtin '{function.Name}' key parameters must use 'borrow'.");
        }

        for (var index = 1; index < function.Parameters.Count; index++)
        {
            var parameterType = StarkTypeSymbols.WithQualifiers(
                function.Parameters[index].Type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);
            if (function.Parameters[index].Type.BorrowKind == StarkBorrowKind.None
                || parameterType != keyType)
            {
                throw new InvalidOperationException($"System.Collections builtin '{function.Name}' key parameters must all borrow the same key type.");
            }
        }

        if (keyType.Kind is not (StarkTypeKind.Bool or StarkTypeKind.Integer))
        {
            throw new InvalidOperationException($"System.Collections DictionaryKey builtin '{function.Name}' does not support key type '{keyType.DisplayName}'.");
        }

        return keyType;
    }

    private static FunctionMemoryEffectSummary GetSystemMemoryBuiltinMemoryEffects(SystemMemoryBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMemoryBuiltinKind.Allocate => new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: false,
                WritesArgumentMemory: false,
                CapturesArgumentMemory: false,
                ReadsOtherMemory: true,
                WritesOtherMemory: true),
            SystemMemoryBuiltinKind.Reallocate or SystemMemoryBuiltinKind.Free => new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: true,
                WritesArgumentMemory: false,
                CapturesArgumentMemory: false,
                ReadsOtherMemory: true,
                WritesOtherMemory: true),
            _ => throw new InvalidOperationException($"Unsupported System.Memory builtin '{builtinKind}'.")
        };
    }

    private SystemMemoryAllocationShape GetSystemMemoryAllocationShape(StarkTypeSymbol allocationType)
    {
        var namedType = ResolveNamedTypeSymbol(allocationType);
        if (namedType is null
            || !IsSystemMemoryNamedType(allocationType, "Allocation")
            || namedType.OrderedFields.Count < 4
            || !string.Equals(namedType.OrderedFields[0].Name, "Pointer", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[1].Name, "ByteLength", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[2].Name, "Alignment", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[3].Name, "Allocator", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("System.Memory Allocation must contain Pointer, ByteLength, Alignment, and Allocator fields in that order.");
        }

        return new SystemMemoryAllocationShape(
            LlvmType: MapType(allocationType),
            ByteLengthLlvmType: MapType(namedType.OrderedFields[1].Type),
            AlignmentLlvmType: MapType(namedType.OrderedFields[2].Type),
            AllocatorLlvmType: MapType(namedType.OrderedFields[3].Type));
    }

    private static bool IsSystemMemoryNamedType(StarkTypeSymbol type, string localName)
    {
        if (type.Kind != StarkTypeKind.Named)
        {
            return false;
        }

        var name = type.NamedType ?? type.DisplayName;
        return string.Equals(name, localName, StringComparison.Ordinal)
            || name.EndsWith($".{localName}", StringComparison.Ordinal)
            || string.Equals(type.DisplayName, localName, StringComparison.Ordinal)
            || type.DisplayName.EndsWith($".{localName}", StringComparison.Ordinal);
    }

    private static bool IsAllocatorSizeInteger(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Integer && type.BitWidth == 64;
    }

    private SystemMathSinCosSignature ValidateSystemMathSinCosBuiltinSignature(TypedFunctionSignature function)
    {
        if (function.Parameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' expects exactly 1 parameter.");
        }

        var scalarType = function.Parameters[0].Type;
        if (scalarType.Kind != StarkTypeKind.Float)
        {
            throw new InvalidOperationException($"System.Math builtin '{function.Name}' requires a floating-point input parameter.");
        }

        var namedType = ResolveNamedTypeSymbol(function.ReturnType);
        if (namedType is null
            || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
            || namedType.OrderedFields.Count != 2
            || !namedType.TryGetField("Sin", out var sinField, out var sinFieldIndex)
            || !namedType.TryGetField("Cos", out var cosField, out var cosFieldIndex)
            || sinField.Type.Kind != StarkTypeKind.Float
            || cosField.Type.Kind != StarkTypeKind.Float
            || sinField.Type.BitWidth != scalarType.BitWidth
            || cosField.Type.BitWidth != scalarType.BitWidth)
        {
            throw new InvalidOperationException(
                $"System.Math builtin '{function.Name}' requires a two-field struct/record return type with 'Sin' and 'Cos' fields matching the floating-point parameter type '{scalarType.DisplayName}'.");
        }

        return new SystemMathSinCosSignature(scalarType, sinFieldIndex, cosFieldIndex);
    }

    private enum SystemTextBuiltinKind
    {
        AsciiView,
        UnicodeView,
        AsciiData,
        UnicodeData,
        AsciiLength,
        UnicodeLength,
        TryConcatAscii,
        TryConcatUnicode,
        TryConvertAsciiToUnicode
    }

    private enum SystemMathBuiltinKind
    {
        Sin,
        Cos,
        Tan,
        Exp,
        Exp2,
        Log,
        Log2,
        Log10,
        Asin,
        Acos,
        Atan,
        Atan2,
        Pow,
        Sinh,
        Cosh,
        Tanh,
        SinCos,
        Sqrt,
        FusedMultiplyAdd,
        ReciprocalEstimate,
        ReciprocalSqrtEstimate,
        Ceiling,
        Floor,
        Truncate,
        Round,
        Min,
        Max
    }

    private enum SystemBitOperationsBuiltinKind
    {
        LeadingZeroCount,
        TrailingZeroCount,
        PopCount,
        RotateLeft,
        RotateRight
    }

    private enum SystemMemoryBuiltinKind
    {
        Allocate,
        Reallocate,
        Free
    }

    private enum SystemRuntimeBuiltinKind
    {
        GetByteSliceParts,
        GetMutableByteSliceParts
    }

    private enum SystemCollectionsBuiltinKind
    {
        ListAsSlice,
        ListAsMutableSlice,
        DictionaryKeyEquals,
        DictionaryKeyHash
    }

    private readonly record struct SystemCollectionsListShape(
        int DataFieldIndex,
        int LengthFieldIndex);

    private readonly record struct SystemMathSinCosSignature(
        StarkTypeSymbol ScalarType,
        int SinFieldIndex,
        int CosFieldIndex);

    private readonly record struct SystemMemoryAllocationShape(
        string LlvmType,
        string ByteLengthLlvmType,
        string AlignmentLlvmType,
        string AllocatorLlvmType);

    private readonly record struct LinuxAllocatorSyscallSpec(
        long MmapNumber,
        long MunmapNumber,
        int ValueBitWidth,
        string? Template,
        string? Constraints)
    {
        public string ValueType => ValueBitWidth == 32 ? "i32" : "i64";
    }

    private static string GetFloatIntrinsicSuffix(StarkTypeSymbol type)
    {
        return type.BitWidth switch
        {
            16 => "f16",
            32 => "f32",
            64 => "f64",
            80 => "f80",
            128 => "f128",
            _ => throw new InvalidOperationException($"Unsupported float intrinsic width '{type.BitWidth}'.")
        };
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
        StarkTypeSymbol targetType)
    {
        return $"llvm.experimental.constrained.sitofp.{GetFloatIntrinsicSuffix(targetType)}.i{sourceType.BitWidth}";
    }

    private static string GetConstrainedFloatToIntegerIntrinsicName(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType)
    {
        return $"llvm.experimental.constrained.fptosi.i{targetType.BitWidth}.{GetFloatIntrinsicSuffix(sourceType)}";
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
