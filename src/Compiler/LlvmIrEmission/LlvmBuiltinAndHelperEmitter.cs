using System.Text;

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
    private const int AggregateScalarizationThresholdBytes = 16;
    private const int AggregateScalarizationMaxLeafCount = 4;

    private readonly LlvmEmissionContext _context;
    private readonly Func<bool, TypedFunctionSignature, AbiFunctionSignature, FunctionEffectProfile, FunctionMemoryEffectSummary?, IReadOnlyDictionary<string, ParameterMemoryEffectSummary>?, string> _buildDefinitionSignature;
    private readonly Func<IEnumerable<SsaBinaryRValue>> _enumerateBinaryOperations;
    private readonly Func<string, string> _escapeInlineAsmString;
    private readonly Func<bool> _usesLifetimeMarkers;
    private readonly Func<bool> _usesHeapAllocator;
    private readonly Func<bool> _usesMemcpyInlineIntrinsic;
    private readonly Func<bool> _usesMemsetInlineIntrinsic;

    public LlvmBuiltinAndHelperEmitter(
        LlvmEmissionContext context,
        Func<bool, TypedFunctionSignature, AbiFunctionSignature, FunctionEffectProfile, FunctionMemoryEffectSummary?, IReadOnlyDictionary<string, ParameterMemoryEffectSummary>?, string> buildDefinitionSignature,
        Func<IEnumerable<SsaBinaryRValue>> enumerateBinaryOperations,
        Func<string, string> escapeInlineAsmString,
        Func<bool> usesLifetimeMarkers,
        Func<bool> usesHeapAllocator,
        Func<bool> usesMemcpyInlineIntrinsic,
        Func<bool> usesMemsetInlineIntrinsic)
    {
        _context = context;
        _buildDefinitionSignature = buildDefinitionSignature;
        _enumerateBinaryOperations = enumerateBinaryOperations;
        _escapeInlineAsmString = escapeInlineAsmString;
        _usesLifetimeMarkers = usesLifetimeMarkers;
        _usesHeapAllocator = usesHeapAllocator;
        _usesMemcpyInlineIntrinsic = usesMemcpyInlineIntrinsic;
        _usesMemsetInlineIntrinsic = usesMemsetInlineIntrinsic;
    }

    private string CurrentModuleName => _context.ModuleName;

    private LlvmTargetInfo? TargetInfo => _context.TargetInfo;

    private string AllocatorSizeType => _context.AllocatorSizeType;

    private bool DebugInfoEnabled => _context.DebugInfoEnabled;

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _context.ResolveNamedTypeSymbol(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    private IReadOnlyList<FieldSymbol>? GetScalarizableNamedAggregateFields(NamedTypeSymbol namedType) =>
        _context.GetScalarizableNamedAggregateFields(namedType);

    public void EmitIntrinsicDeclarations(StringBuilder builder, IEnumerable<TypedFunctionSignature> signatures)
    {
        var declarations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var binary in _enumerateBinaryOperations()
                     .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Float))
        {
            var llvmType = MapType(binary.Type);
            var suffix = GetFloatIntrinsicSuffix(binary.Type);
            declarations.Add($"declare {llvmType} @llvm.pow.{suffix}({llvmType}, {llvmType})");
        }

        foreach (var declaration in EnumerateSystemMathIntrinsicDeclarations(signatures))
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

        if (_usesHeapAllocator())
        {
            declarations.Add($"declare ptr @malloc({AllocatorSizeType})");
            declarations.Add("declare void @free(ptr)");
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

    public void EmitInternalHelperDefinitions(StringBuilder builder)
    {
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

        builder.AppendLine($"define internal i1 @{EscapeIdentifier(helperName)}({textLlvmType} %left, {textLlvmType} %right) {{");
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
        builder.AppendLine($"  %left_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %left_data, i64 %textcmp_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %right_data, i64 %textcmp_index");
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

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({textLlvmType} %left, {textLlvmType} %right) {{");
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
        builder.AppendLine($"  %left_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %left_data, i64 %textord_index");
        builder.AppendLine($"  %right_unit_ptr = getelementptr inbounds {unitLlvmType}, ptr %right_data, i64 %textord_index");
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

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({arrayLlvmType} %left, {arrayLlvmType} %right) {{");
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

        builder.AppendLine($"define internal i32 @{EscapeIdentifier(helperName)}({aggregateLlvmType} %left, {aggregateLlvmType} %right) {{");
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
                    builder.AppendLine($"  %fixedcmp_less_{index} = fcmp olt {MapType(operandType)} {left}, {right}");
                    builder.AppendLine($"  br i1 %fixedcmp_less_{index}, label %return_less, label %{checkGreaterBlock}");
                    builder.AppendLine();
                    builder.AppendLine($"{checkGreaterBlock}:");
                    builder.AppendLine($"  %fixedcmp_greater_{index} = fcmp ogt {MapType(operandType)} {left}, {right}");
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

        builder.AppendLine($"define internal {llvmType} @{EscapeIdentifier(helperName)}({llvmType} %base, {llvmType} %exponent) {{");
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

    private StarkTypeSymbol? GetAggregateElementType(StarkTypeSymbol type, int index)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null => normalizedType.ElementType,
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

        if (!TryGetSystemTextBuiltin(moduleName, function.Name, out var builtinKind))
        {
            return false;
        }

        if (!string.Equals(CurrentModuleName, "System.Text", StringComparison.Ordinal)
            && builtinKind is SystemTextBuiltinKind.TryConcatAscii or SystemTextBuiltinKind.TryConcatUnicode)
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
        if (arity == 1)
        {
            var value = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
            builder.AppendLine($"  %math_result = call {llvmType} {intrinsicName}({llvmType} {value})");
            builder.AppendLine($"  ret {llvmType} %math_result");
            return;
        }

        var left = $"%{EscapeIdentifier(abiFunction.UserParameters[0].LlvmName)}";
        var right = $"%{EscapeIdentifier(abiFunction.UserParameters[1].LlvmName)}";
        builder.AppendLine($"  %math_result = call {llvmType} {intrinsicName}({llvmType} {left}, {llvmType} {right})");
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
            $"  %math_result = call {llvmType} asm \"{_escapeInlineAsmString(template)}\", \"{_escapeInlineAsmString(constraints)}\"({llvmType} {value})");
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
            $"  %math_result = call {llvmType} asm \"{_escapeInlineAsmString(template)}\", \"{_escapeInlineAsmString(constraints)}\"({llvmType} {left}, {llvmType} {right}, {llvmType} {addend})");
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
        builder.AppendLine($"  %math_pair = call {pairType} {intrinsicName}({scalarLlvmType} {value})");
        builder.AppendLine($"  %math_sin = extractvalue {pairType} %math_pair, 0");
        builder.AppendLine($"  %math_cos = extractvalue {pairType} %math_pair, 1");
        builder.AppendLine($"  %math_with_sin = insertvalue {resultType} zeroinitializer, {scalarLlvmType} %math_sin, {signature.SinFieldIndex}");
        builder.AppendLine($"  %math_result = insertvalue {resultType} %math_with_sin, {scalarLlvmType} %math_cos, {signature.CosFieldIndex}");
        builder.AppendLine($"  ret {resultType} %math_result");
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
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text data builtin '{abiFunction.Name}' expects exactly one user parameter.");
        }

        var sourceParameter = abiFunction.UserParameters[0];
        var aggregateType = MapType(sourceParameter.SourceType);
        var resultType = MapType(abiFunction.LlvmReturnType);

        builder.AppendLine("entry:");
        var sourceValue = MaterializeAggregateBuiltinParameterValue(builder, sourceParameter, "view_source");
        builder.AppendLine($"  %view_data = extractvalue {aggregateType} {sourceValue}, 0");
        builder.AppendLine($"  ret {resultType} %view_data");
    }

    private void EmitTextViewLengthBuiltin(
        StringBuilder builder,
        AbiFunctionSignature abiFunction)
    {
        if (abiFunction.UserParameters.Count != 1)
        {
            throw new InvalidOperationException($"System.Text length builtin '{abiFunction.Name}' expects exactly one user parameter.");
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
        builder.AppendLine($"  %concat_data_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 0");
        builder.AppendLine($"  %concat_length_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 1");
        builder.AppendLine($"  %concat_capacity_addr = getelementptr inbounds {aggregateType}, ptr {destinationPointer}, i32 0, i32 2");
        builder.AppendLine("  %concat_data = load ptr, ptr %concat_data_addr");
        builder.AppendLine("  %concat_capacity = load i64, ptr %concat_capacity_addr");
        builder.AppendLine($"  %concat_left_data = extractvalue {viewLlvmType} {leftValue}, 0");
        builder.AppendLine($"  %concat_left_length = extractvalue {viewLlvmType} {leftValue}, 1");
        builder.AppendLine($"  %concat_right_data = extractvalue {viewLlvmType} {rightValue}, 0");
        builder.AppendLine($"  %concat_right_length = extractvalue {viewLlvmType} {rightValue}, 1");
        builder.AppendLine("  %concat_required = add i64 %concat_left_length, %concat_right_length");
        builder.AppendLine("  %concat_has_capacity = icmp ule i64 %concat_required, %concat_capacity");
        builder.AppendLine("  %concat_needs_storage = icmp ne i64 %concat_required, 0");
        builder.AppendLine("  %concat_has_data = icmp ne ptr %concat_data, null");
        builder.AppendLine("  %concat_storage_ready = select i1 %concat_needs_storage, i1 %concat_has_data, i1 true");
        builder.AppendLine("  %concat_success = and i1 %concat_has_capacity, %concat_storage_ready");
        builder.AppendLine("  br i1 %concat_success, label %concat_copy_left_check, label %concat_fail");
        builder.AppendLine("concat_fail:");
        builder.AppendLine("  ret i1 false");
        builder.AppendLine("concat_copy_left_check:");
        builder.AppendLine("  %concat_left_nonempty = icmp ne i64 %concat_left_length, 0");
        builder.AppendLine("  br i1 %concat_left_nonempty, label %concat_copy_left_loop, label %concat_after_left");
        builder.AppendLine("concat_copy_left_loop:");
        builder.AppendLine("  %concat_left_index = phi i64 [ 0, %concat_copy_left_check ], [ %concat_left_next, %concat_copy_left_loop ]");
        builder.AppendLine($"  %concat_left_src = getelementptr inbounds {unitLlvmType}, ptr %concat_left_data, i64 %concat_left_index");
        builder.AppendLine($"  %concat_left_dst = getelementptr inbounds {unitLlvmType}, ptr %concat_data, i64 %concat_left_index");
        builder.AppendLine($"  %concat_left_unit = load {unitLlvmType}, ptr %concat_left_src");
        builder.AppendLine($"  store {unitLlvmType} %concat_left_unit, ptr %concat_left_dst");
        builder.AppendLine("  %concat_left_next = add i64 %concat_left_index, 1");
        builder.AppendLine("  %concat_left_more = icmp ult i64 %concat_left_next, %concat_left_length");
        builder.AppendLine("  br i1 %concat_left_more, label %concat_copy_left_loop, label %concat_after_left");
        builder.AppendLine("concat_after_left:");
        builder.AppendLine("  %concat_right_nonempty = icmp ne i64 %concat_right_length, 0");
        builder.AppendLine("  br i1 %concat_right_nonempty, label %concat_copy_right_prepare, label %concat_finish");
        builder.AppendLine("concat_copy_right_prepare:");
        builder.AppendLine($"  %concat_right_dest = getelementptr inbounds {unitLlvmType}, ptr %concat_data, i64 %concat_left_length");
        builder.AppendLine("  br label %concat_copy_right_loop");
        builder.AppendLine("concat_copy_right_loop:");
        builder.AppendLine("  %concat_right_index = phi i64 [ 0, %concat_copy_right_prepare ], [ %concat_right_next, %concat_copy_right_loop ]");
        builder.AppendLine($"  %concat_right_src = getelementptr inbounds {unitLlvmType}, ptr %concat_right_data, i64 %concat_right_index");
        builder.AppendLine($"  %concat_right_dst = getelementptr inbounds {unitLlvmType}, ptr %concat_right_dest, i64 %concat_right_index");
        builder.AppendLine($"  %concat_right_unit = load {unitLlvmType}, ptr %concat_right_src");
        builder.AppendLine($"  store {unitLlvmType} %concat_right_unit, ptr %concat_right_dst");
        builder.AppendLine("  %concat_right_next = add i64 %concat_right_index, 1");
        builder.AppendLine("  %concat_right_more = icmp ult i64 %concat_right_next, %concat_right_length");
        builder.AppendLine("  br i1 %concat_right_more, label %concat_copy_right_loop, label %concat_finish");
        builder.AppendLine("concat_finish:");
        builder.AppendLine("  store i64 %concat_required, ptr %concat_length_addr");
        builder.AppendLine("  ret i1 true");
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

    private static bool TryGetSystemTextBuiltin(
        string moduleName,
        string functionName,
        out SystemTextBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Text.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Text", StringComparison.Ordinal))
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
            _ => default
        };

        return sourceName is
            "AsciiView" or "UnicodeView"
            or "AsciiData" or "UnicodeData"
            or "AsciiLength" or "UnicodeLength"
            or "TryConcatAscii" or "TryConcatUnicode";
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
        TryConcatUnicode
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

    private readonly record struct SystemMathSinCosSignature(
        StarkTypeSymbol ScalarType,
        int SinFieldIndex,
        int CosFieldIndex);

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
