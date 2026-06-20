using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private static string FormatFloatLiteral(SsaFloatConstant floating)
    {
        if (!double.TryParse(
                CompileTimeExpressionEvaluator.StripFloatSuffix(floating.LiteralText),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new UnsupportedBodyEmissionException(
                $"Unable to parse floating-point literal '{floating.LiteralText}' for LLVM emission.");
        }

        // Emit every value (integral, scientific, subnormal, inf, nan) as a
        // bit-exact hex float. Decimal "R" formatting drops the fractional
        // point for integral values and emits bare scientific notation
        // (1E+17) for large magnitudes, both of which LLVM rejects.
        return LlvmFloatLiteral.Render(parsed, floating.Type.BitWidth ?? 64);
    }

    private string RenderDirectArgument(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter,
        SsaValue argument,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        bool includeContractAttributes)
    {
        if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
        {
            return $"ptr {ExtractStringDataPointer(argument)}";
        }

        var segments = new List<string> { MapType(parameter.LlvmType) };
        if (TryBuildDirectArgumentRangeAttribute(parameter, argument, out var rangeAttribute))
        {
            segments.Add(rangeAttribute);
        }

        if (includeContractAttributes)
        {
            var attributes = _attributeBuilder.GetAbiParameterAttributes(parameter, parameterEffects, abiFunction).ToList();
            AddBoundedRawPointerArgumentAttributes(parameter, argument, attributes);
            segments.AddRange(attributes);
        }

        segments.Add(FormatDirectAbiArgumentValue(parameter, argument));
        return string.Join(" ", segments);
    }

    private bool TryBuildDirectArgumentRangeAttribute(
        AbiParameterSymbol parameter,
        SsaValue argument,
        out string rangeAttribute)
    {
        if (MapType(argument.Type) == MapType(parameter.LlvmType)
            && TryGetIntegerValueRange(argument, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && LlvmValueRangeFacts.TryBuildRangeAttribute(
                parameter.SourceType,
                new SsaIntegerRangeFact(min, max),
                out rangeAttribute))
        {
            return true;
        }

        return LlvmValueRangeFacts.TryBuildRangeAttribute(parameter.SourceType, out rangeAttribute);
    }

    private string FormatDirectAbiArgumentValue(AbiParameterSymbol parameter, SsaValue argument)
    {
        if (!CAbiAggregateClassifier.IsCarrierType(parameter.SourceType, parameter.LlvmType))
        {
            return FormatValue(argument);
        }

        return MaterializeCAbiCarrierFromSourceValue(
            parameter.SourceType,
            parameter.LlvmType,
            argument,
            $"ffi_arg_{parameter.SourceName}");
    }

    private string MaterializeCAbiCarrierFromSourceValue(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol carrierType,
        SsaValue sourceValue,
        string tempPrefix)
    {
        if (TryGetConcreteTypeLayout(sourceType) is not { } sourceLayout
            || TryGetConcreteTypeLayout(carrierType) is not { } carrierLayout)
        {
            return FormatValue(sourceValue);
        }

        string sourceAddress;
        int? sourceAlignmentBytes;
        if (!TryResolveAggregateSourceAddress(sourceValue, sourceType, out sourceAddress, out sourceAlignmentBytes))
        {
            sourceAddress = $"%{EscapeIdentifier(CreateAbiTempName($"{tempPrefix}_source"))}";
            QueueStaticAlloca(sourceAddress, sourceType);
            sourceAlignmentBytes = GetStackObjectAlignmentBytes(sourceType);
            EmitValueToAddress(sourceAddress, sourceType, sourceValue, sourceAlignmentBytes);
        }

        var carrierAddress = $"%{EscapeIdentifier(CreateAbiTempName($"{tempPrefix}_carrier"))}";
        QueueStaticAlloca(carrierAddress, carrierType);
        var carrierAlignmentBytes = GetStackObjectAlignmentBytes(carrierType);
        if (carrierLayout.SizeBytes > sourceLayout.SizeBytes)
        {
            AppendLine($"  call void @llvm.memset.inline.p0.i64(ptr{GetArgumentAlignmentFragment(carrierAlignmentBytes)} {carrierAddress}, i8 0, i64 {carrierLayout.SizeBytes}, i1 false)");
        }

        EmitAggregateMemcpy(
            carrierAddress,
            sourceAddress,
            sourceLayout.SizeBytes,
            GetArgumentAlignmentFragment(carrierAlignmentBytes),
            GetArgumentAlignmentFragment(sourceAlignmentBytes));

        var carrierValue = $"%{EscapeIdentifier(CreateAbiTempName($"{tempPrefix}_value"))}";
        AppendLine($"  {carrierValue} = load {MapType(carrierType)}, ptr {carrierAddress}{GetAlignmentSuffix(carrierAlignmentBytes)}");
        return carrierValue;
    }

    private string MaterializeSourceValueFromCAbiCarrier(
        string carrierValue,
        StarkTypeSymbol carrierType,
        StarkTypeSymbol sourceType,
        string resultName,
        string tempPrefix,
        string? materializedValueName = null)
    {
        if (TryGetConcreteTypeLayout(sourceType) is not { } sourceLayout
            || TryGetConcreteTypeLayout(carrierType) is not { } carrierLayout)
        {
            return carrierValue;
        }

        var carrierAddress = $"%{EscapeIdentifier(CreateAbiTempName($"{tempPrefix}_carrier"))}";
        QueueStaticAlloca(carrierAddress, carrierType);
        var carrierAlignmentBytes = GetStackObjectAlignmentBytes(carrierType);
        AppendLine($"  store {MapType(carrierType)} {carrierValue}, ptr {carrierAddress}{GetAlignmentSuffix(carrierAlignmentBytes)}");

        var sourceAddress = $"%{EscapeIdentifier(CreateAbiTempName($"{tempPrefix}_source"))}";
        QueueStaticAlloca(sourceAddress, sourceType);
        var sourceAlignmentBytes = GetStackObjectAlignmentBytes(sourceType);
        EmitAggregateMemcpy(
            sourceAddress,
            carrierAddress,
            Math.Min(sourceLayout.SizeBytes, carrierLayout.SizeBytes),
            GetArgumentAlignmentFragment(sourceAlignmentBytes),
            GetArgumentAlignmentFragment(carrierAlignmentBytes));

        _indirectAggregateValueSlots[resultName] = sourceAddress;
        var sourceValue = $"%{EscapeIdentifier(materializedValueName ?? resultName)}";
        AppendLine($"  {sourceValue} = load {MapType(sourceType)}, ptr {sourceAddress}{GetAlignmentSuffix(sourceAlignmentBytes)}{GetValueRangeMetadataSuffix(sourceType)}");
        return sourceValue;
    }

    private void AddBoundedRawPointerArgumentAttributes(
        AbiParameterSymbol parameter,
        SsaValue argument,
        List<string> attributes)
    {
        if (parameter.Kind != AbiParameterKind.Direct
            || parameter.SourceType.Kind != StarkTypeKind.RawPointer
            || parameter.SourceType.ElementType is not { } parameterElementType
            || string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            || !TryGetBoundedRawPointerRegionFact(
                argument,
                new HashSet<string>(StringComparer.Ordinal),
                out var boundedRegion)
            || boundedRegion.ElementCountRange is not { } countRange
            || countRange.Min <= BigInteger.Zero
            || !RawPointerRegionElementTypesMatch(argument.Type, parameter.SourceType)
            || TryGetConcreteTypeLayout(NormalizeAggregateType(parameterElementType)) is not { } elementLayout
            || elementLayout.SizeBytes <= 0)
        {
            return;
        }

        AddUniqueAttribute(attributes, "nonnull", insertionIndex: 0);

        var minimumByteCount = countRange.Min * elementLayout.SizeBytes;
        if (minimumByteCount <= long.MaxValue)
        {
            AddOrStrengthenDereferenceableAttribute(attributes, minimumByteCount);
        }

        var alignmentBytes = boundedRegion.ElementAlignmentBytes is > 1
            ? Math.Max(boundedRegion.ElementAlignmentBytes.Value, elementLayout.AlignmentBytes)
            : elementLayout.AlignmentBytes;
        if (alignmentBytes > 1)
        {
            AddOrStrengthenAlignAttribute(attributes, alignmentBytes);
        }
    }

    private static bool RawPointerRegionElementTypesMatch(StarkTypeSymbol argumentType, StarkTypeSymbol parameterType)
    {
        return argumentType.Kind == StarkTypeKind.RawPointer
            && parameterType.Kind == StarkTypeKind.RawPointer
            && argumentType.ElementType is { } argumentElementType
            && parameterType.ElementType is { } parameterElementType
            && NormalizeAggregateType(argumentElementType) == NormalizeAggregateType(parameterElementType);
    }

    private static void AddUniqueAttribute(List<string> attributes, string attribute, int? insertionIndex = null)
    {
        if (!attributes.Contains(attribute, StringComparer.Ordinal))
        {
            attributes.Insert(Math.Clamp(insertionIndex ?? attributes.Count, 0, attributes.Count), attribute);
        }
    }

    private static void AddOrStrengthenDereferenceableAttribute(List<string> attributes, BigInteger byteCount)
    {
        var replacement = $"dereferenceable({byteCount.ToString(CultureInfo.InvariantCulture)})";
        for (var index = 0; index < attributes.Count; index++)
        {
            if (!TryParseDereferenceableAttribute(attributes[index], out var existingByteCount))
            {
                continue;
            }

            if (byteCount > existingByteCount)
            {
                attributes[index] = replacement;
            }

            return;
        }

        attributes.Insert(GetPointerExtentAttributeInsertionIndex(attributes), replacement);
    }

    private static bool TryParseDereferenceableAttribute(string attribute, out BigInteger byteCount)
    {
        const string prefix = "dereferenceable(";
        byteCount = default;
        return attribute.StartsWith(prefix, StringComparison.Ordinal)
            && attribute.EndsWith(')')
            && BigInteger.TryParse(
                attribute[prefix.Length..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out byteCount);
    }

    private static void AddOrStrengthenAlignAttribute(List<string> attributes, int alignmentBytes)
    {
        var replacement = $"align {alignmentBytes.ToString(CultureInfo.InvariantCulture)}";
        for (var index = 0; index < attributes.Count; index++)
        {
            if (!attributes[index].StartsWith("align ", StringComparison.Ordinal)
                || !int.TryParse(attributes[index][6..], NumberStyles.None, CultureInfo.InvariantCulture, out var existingAlignmentBytes))
            {
                continue;
            }

            if (alignmentBytes > existingAlignmentBytes)
            {
                attributes[index] = replacement;
            }

            return;
        }

        attributes.Insert(GetPointerExtentAttributeInsertionIndex(attributes), replacement);
    }

    private static int GetPointerExtentAttributeInsertionIndex(List<string> attributes)
    {
        for (var index = attributes.Count - 1; index >= 0; index--)
        {
            var attribute = attributes[index];
            if (string.Equals(attribute, "nonnull", StringComparison.Ordinal)
                || attribute.StartsWith("dereferenceable(", StringComparison.Ordinal)
                || attribute.StartsWith("align ", StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private string RenderDirectVarargArgument(SsaValue argument)
    {
        var sourceType = StarkTypeSymbols.WithQualifiers(
            argument.Type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var llvmType = LowerCVarargArgumentType(sourceType);

        if (llvmType.Kind == StarkTypeKind.RawPointer && IsStringType(sourceType))
        {
            return $"ptr {ExtractStringDataPointer(argument)}";
        }

        return $"{MapType(llvmType)} {FormatValue(argument)}";
    }

    private static StarkTypeSymbol LowerCVarargArgumentType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            StarkTypeKind.Unicode => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false),
            _ => type
        };
    }

    private string RenderIndirectArgumentPointer(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter,
        string pointerValue,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        bool includeContractAttributes)
    {
        var segments = new List<string> { "ptr" };
        if (includeContractAttributes)
        {
            segments.AddRange(_attributeBuilder.GetAbiParameterAttributes(parameter, parameterEffects, abiFunction));
        }
        else if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
        {
            segments.Add($"byval({MapType(parameter.SourceType)})");
            if (TryGetConcreteTypeLayout(parameter.SourceType) is { AlignmentBytes: > 1 } layout)
            {
                segments.Add($"align {layout.AlignmentBytes}");
            }
        }

        segments.Add(pointerValue);
        return string.Join(" ", segments);
    }

    private string RenderSRetArgumentPointer(
        AbiFunctionSignature abiFunction,
        AbiParameterSymbol parameter,
        string pointerValue,
        bool includeContractAttributes)
    {
        var segments = new List<string> { "ptr" };
        if (includeContractAttributes)
        {
            segments.AddRange(_attributeBuilder.GetAbiParameterAttributes(parameter, parameterEffects: null, abiFunction));
        }
        else
        {
            segments.Add($"sret({MapType(parameter.SourceType)})");
            if (GetStackObjectAlignmentBytes(parameter.SourceType) is { } alignmentBytes)
            {
                segments.Add($"align {alignmentBytes}");
            }
        }

        segments.Add(pointerValue);
        return string.Join(" ", segments);
    }

    private string FormatStringConstantValue(SsaStringConstant text)
    {
        var pointer = FormatStringDataPointer(text.LiteralText, text.Type);
        var constant = ResolveStringConstant(text.LiteralText, text.Type);
        return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
    }

    private string ExtractStringDataPointer(SsaValue value)
    {
        if (!IsStringType(value.Type))
        {
            throw new UnsupportedBodyEmissionException($"Value '{value.Text}' is not a lowered string.");
        }

        if (value is SsaStringConstant stringConstant)
        {
            return FormatStringDataPointer(stringConstant.LiteralText, stringConstant.Type);
        }

        var tempName = $"%{EscapeIdentifier(CreateAbiTempName("str_data"))}";
        AppendLine($"  {tempName} = extractvalue {MapType(value.Type)} {FormatValue(value)}, 0");
        return tempName;
    }

    private string FormatStringDataPointer(string literalText, StarkTypeSymbol type)
    {
        var constant = ResolveStringConstant(literalText, type);
        return $"getelementptr{GetProvenInObjectGepFlags()} ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
    }

    private void EnsureLocalSlotExists(string localName, StarkTypeSymbol localType)
    {
        if (_localSlotAliases.ContainsKey(localName))
        {
            return;
        }

        MaterializeDeferredAliasCandidateLocal(localName, localType);

        var slotName = EscapeIdentifier($"slot_{localName}");
        if (!_allocatedLocalSlots.Add(slotName))
        {
            return;
        }

        switch (GetLocalStorageClass(localName))
        {
            case "stack":
            case "match":
                QueueStaticAlloca($"%{slotName}", localType);
                break;
            case "heap":
                EmitHeapAllocateLocalSlot(slotName, localType);
                break;
            default:
                throw new UnsupportedBodyEmissionException(
                    $"Local storage class '{GetLocalStorageClass(localName)}' is invalid for LLVM body emission.");
        }

        EmitDeferredAliasLocalSetup(localName, localType, $"%{slotName}");
    }

    private string GetLocalSlotPointer(string localName)
    {
        return _localSlotAliases.TryGetValue(localName, out var alias)
            ? alias.Pointer
            : $"%{EscapeIdentifier($"slot_{localName}")}";
    }

    private int? GetLocalSlotAlignmentBytes(string localName, StarkTypeSymbol localType)
    {
        return _localSlotAliases.TryGetValue(localName, out var alias)
            ? alias.AlignmentBytes ?? GetTypeAlignmentBytes(alias.Type)
            : GetLocalObjectAlignmentBytes(localName, localType);
    }

    private string GetLocalSlotAlignmentSuffix(string localName, StarkTypeSymbol localType)
    {
        return GetAlignmentSuffix(GetLocalSlotAlignmentBytes(localName, localType));
    }

    private bool IsDirectAggregateAliasCandidateLocal(string localName)
    {
        return _directAggregateAliasCandidateLocalNames.Contains(localName)
            && !_materializedAliasCandidateLocalNames.Contains(localName)
            && !_localSlotAliases.ContainsKey(localName);
    }

    private void MaterializeDeferredAliasCandidateLocal(string localName, StarkTypeSymbol localType)
    {
        if (!_directAggregateAliasCandidateLocalNames.Contains(localName)
            || _materializedAliasCandidateLocalNames.Contains(localName)
            || _localSlotAliases.ContainsKey(localName))
        {
            return;
        }

        _materializedAliasCandidateLocalNames.Add(localName);
    }

    private void EmitDeferredAliasLocalSetup(string localName, StarkTypeSymbol localType, string slotPointer)
    {
        if (_deferredAliasLocalAllocations.Remove(localName, out var allocateLocal))
        {
            EmitLocalDebugDeclare(
                slotPointer,
                allocateLocal.LocalName,
                allocateLocal.LocalType,
                allocateLocal.Location);
        }

        if (_deferredAliasLifetimeStarts.Remove(localName, out _)
            && TryGetConcreteTypeLayout(localType) is { } layout)
        {
            AppendLine($"  call void @llvm.lifetime.start.p0(i64 {layout.SizeBytes}, ptr {slotPointer})");
        }
    }

    private void EmitHeapAllocateLocalSlot(string slotName, StarkTypeSymbol localType)
    {
        var sizePointer = $"%{EscapeIdentifier(CreateAbiTempName("heap_size_ptr"))}";
        var sizeValue = $"%{EscapeIdentifier(CreateAbiTempName("heap_size"))}";
        var alignmentBytes = GetHeapObjectAlignmentBytes(localType) ?? 1;
        AppendLine($"  {sizePointer} = getelementptr {MapType(localType)}, ptr null, i32 1");
        AppendLine($"  {sizeValue} = ptrtoint ptr {sizePointer} to {AllocatorSizeType}");
        AppendLine(
            $"  %{slotName} = call {BuildFreshAllocationResultAttributes(localType)} ptr @{HeapAllocateHelperName}({AllocatorSizeType} noundef {sizeValue}, {AllocatorSizeType} noundef {alignmentBytes})");
    }

    private string BuildFreshAllocationResultAttributes(StarkTypeSymbol allocatedType)
    {
        var attributes = new List<string>
        {
            "noalias",
            "nonnull",
            "noundef"
        };

        if (TryGetConcreteTypeLayout(allocatedType) is { } layout)
        {
            var alignmentBytes = GetHeapObjectAlignmentBytes(allocatedType) ?? layout.AlignmentBytes;
            if (alignmentBytes > 1)
            {
                attributes.Add($"align {alignmentBytes}");
            }

            if (layout.SizeBytes > 0)
            {
                attributes.Add($"dereferenceable({layout.SizeBytes})");
            }
        }

        return string.Join(" ", attributes);
    }

    private string GetLocalStorageClass(string localName)
    {
        return _localStorageClasses.TryGetValue(localName, out var storageClass)
            ? storageClass
            : "stack";
    }

    private void EnsureParameterSlotExists(AbiParameterSymbol parameter, StarkTypeSymbol parameterType)
    {
        var slotName = EscapeIdentifier($"slot_param_{parameter.SourceName}");
        if (_allocatedLocalSlots.Add(slotName))
        {
            QueueStaticAlloca($"%{slotName}", parameterType);

            var incomingValue = _materializedParameters.TryGetValue(parameter.LlvmName, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(parameter.LlvmName)}";
            AppendLine($"  store {MapType(parameterType)} {incomingValue}, ptr %{slotName}{GetStackObjectAlignmentSuffix(parameterType)}{GetDirectTbaaMetadataSuffix(CreateTbaaParameterRootKey(parameter.SourceName), parameterType)}");
        }
    }

    private void EmitEntryParameterMaterialization()
    {
        foreach (var parameter in _abiFunction.UserParameters)
        {
            if (parameter.Kind == AbiParameterKind.Direct
                && CAbiAggregateClassifier.IsCarrierType(parameter.SourceType, parameter.LlvmType)
                && (_referencedValueNames.Contains(parameter.LlvmName)
                    || _referencedValueNames.Contains(parameter.SourceName)
                    || _addressTakenParameterNames.Contains(parameter.LlvmName)
                    || _addressTakenParameterNames.Contains(parameter.SourceName)))
            {
                var carrierMaterializedName = EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"));
                var materializedValue = MaterializeSourceValueFromCAbiCarrier(
                    $"%{EscapeIdentifier(parameter.LlvmName)}",
                    parameter.LlvmType,
                    parameter.SourceType,
                    parameter.LlvmName,
                    $"arg_{parameter.SourceName}",
                    carrierMaterializedName);
                _materializedParameters[parameter.LlvmName] = materializedValue;
                _materializedParameters[parameter.SourceName] = materializedValue;
                if (_indirectAggregateValueSlots.TryGetValue(parameter.LlvmName, out var sourceAddress))
                {
                    _indirectAggregateValueSlots[parameter.SourceName] = sourceAddress;
                }

                continue;
            }

            if (parameter.Kind != AbiParameterKind.IndirectIn)
            {
                continue;
            }

            if (!_referencedValueNames.Contains(parameter.LlvmName)
                && !_referencedValueNames.Contains(parameter.SourceName))
            {
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && !RequiresAggregateValueMaterialization(parameter.LlvmName, parameter.SourceType))
            {
                continue;
            }

            var materializedName = $"%{EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"))}";
            AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}{GetTypeAlignmentSuffix(parameter.SourceType)}{GetValueRangeMetadataSuffix(parameter.SourceType)}{GetDirectTbaaMetadataSuffix(CreateTbaaParameterRootKey(parameter.SourceName), parameter.SourceType)}{GetScopedNoAliasMetadataSuffix(CreateScopedAliasParameterRootKey(parameter.SourceName))}");
            _materializedParameters[parameter.LlvmName] = materializedName;
            _materializedParameters[parameter.SourceName] = materializedName;
        }
    }

    private void EmitEntryParameterSlots()
    {
        foreach (var parameter in _abiFunction.UserParameters)
        {
            if (parameter.Kind == AbiParameterKind.IndirectIn)
            {
                continue;
            }

            if (!_addressTakenParameterNames.Contains(parameter.SourceName)
                && !_addressTakenParameterNames.Contains(parameter.LlvmName))
            {
                continue;
            }

            EnsureParameterSlotExists(parameter, parameter.SourceType);
        }
    }

    private void EmitEntryParameterDebugInfo()
    {
        if (_debugFunction is null)
        {
            return;
        }

        for (var index = 0; index < _abiFunction.UserParameters.Count; index++)
        {
            var parameter = _abiFunction.UserParameters[index];
            var variableRef = _debugFunction.GetParameterVariableRef(parameter.SourceName, parameter.SourceType, index + 1);

            if (parameter.Kind == AbiParameterKind.IndirectIn)
            {
                AppendLine($"  call void @llvm.dbg.declare(metadata ptr %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
                continue;
            }

            AppendLine(
                $"  call void @llvm.dbg.value(metadata {MapType(parameter.LlvmType)} %{EscapeIdentifier(parameter.LlvmName)}, metadata {variableRef}, metadata !DIExpression())");
        }
    }

    private void EmitLocalDebugDeclare(string slotName, string localName, StarkTypeSymbol localType, SourceLocation? location)
    {
        if (_debugFunction is null)
        {
            return;
        }

        var variableRef = _debugFunction.GetLocalVariableRef(localName, localType, location ?? _ssaFunction.Location);
        AppendLine($"  call void @llvm.dbg.declare(metadata ptr {slotName}, metadata {variableRef}, metadata !DIExpression())");
    }

    private void QueueStaticAlloca(string slotName, StarkTypeSymbol slotType)
    {
        _entryStaticAllocas.Add($"  {slotName} = alloca {MapType(slotType)}{GetStackObjectAlignmentSuffix(slotType)}");
    }

    private void FlushEntryStaticAllocas()
    {
        if (_entryStaticAllocas.Count == 0 || _entryStaticAllocaInsertionIndex is not int insertionIndex)
        {
            return;
        }

        _builder.Insert(insertionIndex, string.Join(Environment.NewLine, _entryStaticAllocas) + Environment.NewLine);
    }
}
