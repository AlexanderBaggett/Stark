using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitCallInstruction(SsaCallInstruction instruction)
    {
        EmitCall(
            resultName: null,
            result: null,
            instruction,
            instruction.ScopedNoAliasGroups,
            instruction.LoopAccessGroups);
    }

    private void EmitCall(
        string? resultName,
        string? result,
        ISsaDirectCallOperation call,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        IReadOnlyList<string>? loopAccessGroups)
    {
        var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
        if (abiCallee is null)
        {
            throw new UnsupportedBodyEmissionException($"Missing ABI lowering for call target '{call.FunctionName}'.");
        }

        var sourceReturnType = call.SourceReturnType ?? call.Type;
        if (IsStringType(sourceReturnType) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
        {
            throw new UnsupportedBodyEmissionException(
                $"FFI string returns are invalid for '{call.FunctionName}'. Return a raw pointer plus explicit length/status and wrap it in Stark code.");
        }

        if (TryEmitAsciiToUnicodeLiteralCallSiteSpecialization(result, call, abiCallee))
        {
            return;
        }

        if (result is not null
            && call is SsaCallRValue dictionaryCall
            && TryEmitDictionaryKeyCallSiteSpecialization(result, dictionaryCall, abiCallee))
        {
            return;
        }

        var calleeParameterEffects = ResolveCallParameterEffects(call.FunctionName);
        var calleeMemoryEffects = ResolveCallMemoryEffects(call.FunctionName);
        var arguments = new List<string>();
        string? indirectReturnSlot = null;

        if (abiCallee.ReturnsIndirect)
        {
            if (resultName is null
                || !TryGetCurrentReturnBufferForwardingAddress(resultName, sourceReturnType, out indirectReturnSlot))
            {
                indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("callret_slot"))}";
                QueueStaticAlloca(indirectReturnSlot, sourceReturnType);
            }

            arguments.Add(RenderSRetArgumentPointer(abiCallee, abiCallee.ReturnBufferParameter!, indirectReturnSlot, includeContractAttributes: true));
        }

        var userParameters = abiCallee.UserParameters;
        if (abiCallee.IsVarargs)
        {
            if (call.Arguments.Count < userParameters.Count)
            {
                throw new UnsupportedBodyEmissionException(
                    $"ABI parameter count mismatch for '{call.FunctionName}': expected at least {userParameters.Count}, got {call.Arguments.Count}.");
            }
        }
        else if (userParameters.Count != call.Arguments.Count)
        {
            throw new UnsupportedBodyEmissionException(
                $"ABI parameter count mismatch for '{call.FunctionName}': expected {userParameters.Count}, got {call.Arguments.Count}.");
        }

        for (var index = 0; index < userParameters.Count; index++)
        {
            var parameter = userParameters[index];
            var argument = call.Arguments[index];

            if (parameter.Kind == AbiParameterKind.Direct)
            {
                arguments.Add(RenderDirectArgument(abiCallee, parameter, argument, calleeParameterEffects, includeContractAttributes: true));
                continue;
            }

            var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && index < call.IndirectArgumentAddresses.Count
                ? call.IndirectArgumentAddresses[index]
                : null;
            if (indirectArgumentAddress is not null)
            {
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, FormatValue(indirectArgumentAddress), calleeParameterEffects, includeContractAttributes: true));
                continue;
            }

            var promotedLocal = call.IndirectArgumentLocalNames is not null && index < call.IndirectArgumentLocalNames.Count
                ? call.IndirectArgumentLocalNames[index]
                : null;
            if (!string.IsNullOrWhiteSpace(promotedLocal))
            {
                var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
                if (promotedParameter is not null)
                {
                    if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                    {
                        arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, $"%{EscapeIdentifier(promotedParameter.LlvmName)}", calleeParameterEffects, includeContractAttributes: true));
                    }
                    else
                    {
                        EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                        arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}", calleeParameterEffects, includeContractAttributes: true));
                    }

                    continue;
                }

                EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, GetLocalSlotPointer(promotedLocal!), calleeParameterEffects, includeContractAttributes: true));
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && TryResolveAggregateSourceAddress(argument, parameter.SourceType, out var forwardedSourceAddress))
            {
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, forwardedSourceAddress, calleeParameterEffects, includeContractAttributes: true));
                continue;
            }

            var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
            QueueStaticAlloca(tempSlot, parameter.SourceType);
            EmitValueToAddress(tempSlot, parameter.SourceType, argument, GetStackObjectAlignmentBytes(parameter.SourceType));

            arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, tempSlot, calleeParameterEffects, includeContractAttributes: true));
        }

        if (abiCallee.IsVarargs)
        {
            for (var index = userParameters.Count; index < call.Arguments.Count; index++)
            {
                arguments.Add(RenderDirectVarargArgument(call.Arguments[index]));
            }
        }

        var renderedArguments = string.Join(", ", arguments);
        var callTarget = RenderCallTarget(abiCallee);
        var callPrefixSegments = new List<string>();
        if (resultName is not null && ShouldEmitTailCallMarker(resultName))
        {
            callPrefixSegments.Add("tail");
        }

        callPrefixSegments.Add("call");
        if (ShouldUseFastMathFlags(call.Type))
        {
            callPrefixSegments.Add("fast");
        }

        if (abiCallee.UsesFastCallingConvention)
        {
            callPrefixSegments.Add("fastcc");
        }

        var callPrefix = string.Join(" ", callPrefixSegments);
        var strictFpCallSuffix = GetStrictFpCallSuffix();
        var callMetadataSuffix = GetCallInstructionMetadataSuffix(
            abiCallee,
            call.Arguments,
            call.IndirectArgumentAddresses,
            call.IndirectArgumentLocalNames,
            calleeParameterEffects,
            calleeMemoryEffects,
            scopedNoAliasGroups,
            loopAccessGroups);

        if (abiCallee.ReturnsIndirect)
        {
            AppendLine($"  {callPrefix} void {callTarget}({renderedArguments}){strictFpCallSuffix}{callMetadataSuffix}");
            if (resultName is not null)
            {
                _indirectAggregateValueSlots[resultName] = indirectReturnSlot!;
                if (result is not null && RequiresAggregateValueMaterialization(resultName, sourceReturnType))
                {
                    AppendLine($"  {result} = load {MapType(sourceReturnType)}, ptr {indirectReturnSlot}{GetStackObjectAlignmentSuffix(sourceReturnType)}{GetValueRangeMetadataSuffix(sourceReturnType)}{GetScopedNoAliasMetadataSuffix(CreateScopedAliasFreshResultRootKey(resultName))}");
                }
            }

            return;
        }

        if (call.Type.Kind == StarkTypeKind.Void)
        {
            AppendLine($"  {callPrefix} void {callTarget}({renderedArguments}){strictFpCallSuffix}{callMetadataSuffix}");
            return;
        }

        if (resultName is null || result is null)
        {
            AppendLine($"  {callPrefix} {RenderCallResultType(abiCallee)} {callTarget}({renderedArguments}){strictFpCallSuffix}{callMetadataSuffix}");
            return;
        }

        var callRangeMetadataSuffix = abiCallee.IsFfi ? string.Empty : GetValueRangeMetadataSuffix(resultName, call.Type);
        AppendLine($"  {result} = {callPrefix} {RenderCallResultType(abiCallee)} {callTarget}({renderedArguments}){strictFpCallSuffix}{callRangeMetadataSuffix}{callMetadataSuffix}");
    }

    private enum DictionaryKeyCallSiteOperation
    {
        Hash,
        Equals
    }

    private bool TryEmitDictionaryKeyCallSiteSpecialization(
        string result,
        SsaCallRValue call,
        AbiFunctionSignature abiCallee)
    {
        if (abiCallee.IsFfi
            || TryResolveDictionaryKeyCallSiteOperation(call, abiCallee) is not { } operation)
        {
            return false;
        }

        var expectedParameterCount = operation == DictionaryKeyCallSiteOperation.Hash ? 1 : 2;
        if (!TryResolveDictionaryKeyCallSiteType(call, abiCallee, expectedParameterCount, out var keyType))
        {
            return false;
        }

        if (operation == DictionaryKeyCallSiteOperation.Hash)
        {
            if (call.Type.Kind != StarkTypeKind.Integer || call.Type.BitWidth != 64)
            {
                return false;
            }

            if (!TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[0], 0, keyType, out var value))
            {
                return false;
            }

            EmitDictionaryKeyHashValue(result, keyType, value);
            return true;
        }

        if (call.Type.Kind != StarkTypeKind.Bool
            || !TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[0], 0, keyType, out var left)
            || !TryMaterializeDictionaryKeyScalarArgument(call, abiCallee.UserParameters[1], 1, keyType, out var right))
        {
            return false;
        }

        AppendLine($"  {result} = icmp eq {MapType(keyType)} {left}, {right}");
        return true;
    }

    private static DictionaryKeyCallSiteOperation? TryResolveDictionaryKeyCallSiteOperation(
        SsaCallRValue call,
        AbiFunctionSignature abiCallee)
    {
        foreach (var candidate in new[]
                 {
                     abiCallee.SourceName,
                     abiCallee.DisplaySourceName,
                     abiCallee.Name,
                     abiCallee.SymbolName,
                     call.FunctionName
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate is "System.Collections.DictionaryKey.Hash"
                or "DictionaryKey.Hash"
                or "System_Collections_DictionaryKey_Hash")
            {
                return DictionaryKeyCallSiteOperation.Hash;
            }

            if (candidate is "System.Collections.DictionaryKey.Equals"
                or "DictionaryKey.Equals"
                or "System_Collections_DictionaryKey_Equals")
            {
                return DictionaryKeyCallSiteOperation.Equals;
            }

            if (candidate.StartsWith("__stark_mono_fn_System_Collections__", StringComparison.Ordinal))
            {
                if (candidate.Contains("System_Collections_DictionaryKey_Hash__", StringComparison.Ordinal))
                {
                    return DictionaryKeyCallSiteOperation.Hash;
                }

                if (candidate.Contains("System_Collections_DictionaryKey_Equals__", StringComparison.Ordinal))
                {
                    return DictionaryKeyCallSiteOperation.Equals;
                }
            }
        }

        return null;
    }

    private static bool TryResolveDictionaryKeyCallSiteType(
        SsaCallRValue call,
        AbiFunctionSignature abiCallee,
        int expectedParameterCount,
        out StarkTypeSymbol keyType)
    {
        keyType = StarkTypeSymbols.Error;
        var userParameters = abiCallee.UserParameters;
        if (call.Arguments.Count != expectedParameterCount
            || userParameters.Count != expectedParameterCount)
        {
            return false;
        }

        var firstParameterType = NormalizeDictionaryKeyType(userParameters[0].SourceType);
        if (userParameters[0].SourceType.BorrowKind != StarkBorrowKind.None
            && IsSupportedDictionaryKeyScalarType(firstParameterType))
        {
            keyType = firstParameterType;
        }
        else if (!TryResolveDictionaryKeyArgumentType(call, 0, out keyType))
        {
            return false;
        }

        for (var index = 1; index < userParameters.Count; index++)
        {
            var parameterType = NormalizeDictionaryKeyType(userParameters[index].SourceType);
            if (userParameters[index].SourceType.BorrowKind != StarkBorrowKind.None
                && IsSupportedDictionaryKeyScalarType(parameterType))
            {
                if (parameterType != keyType)
                {
                    return false;
                }

                continue;
            }

            if (!TryResolveDictionaryKeyArgumentType(call, index, out var argumentType)
                || argumentType != keyType)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveDictionaryKeyArgumentType(
        SsaCallRValue call,
        int argumentIndex,
        out StarkTypeSymbol keyType)
    {
        keyType = NormalizeDictionaryKeyType(call.Arguments[argumentIndex].Type);
        if (IsSupportedDictionaryKeyScalarType(keyType))
        {
            return true;
        }

        var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && argumentIndex < call.IndirectArgumentAddresses.Count
            ? call.IndirectArgumentAddresses[argumentIndex]
            : null;
        if (indirectArgumentAddress?.Type is { Kind: StarkTypeKind.RawPointer, ElementType: not null } pointerType)
        {
            keyType = NormalizeDictionaryKeyType(pointerType.ElementType);
            return IsSupportedDictionaryKeyScalarType(keyType);
        }

        keyType = StarkTypeSymbols.Error;
        return false;
    }

    private bool TryMaterializeDictionaryKeyScalarArgument(
        SsaCallRValue call,
        AbiParameterSymbol parameter,
        int argumentIndex,
        StarkTypeSymbol keyType,
        out string value)
    {
        value = string.Empty;
        var argument = call.Arguments[argumentIndex];
        if (parameter.Kind == AbiParameterKind.Direct)
        {
            value = FormatValue(argument);
            return true;
        }

        if (parameter.Kind != AbiParameterKind.IndirectIn)
        {
            return false;
        }

        var llvmType = MapType(keyType);
        var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && argumentIndex < call.IndirectArgumentAddresses.Count
            ? call.IndirectArgumentAddresses[argumentIndex]
            : null;
        if (indirectArgumentAddress is not null)
        {
            value = LoadDictionaryKeyScalarValue(
                llvmType,
                keyType,
                FormatValue(indirectArgumentAddress),
                GetKnownPointerAlignmentSuffix(indirectArgumentAddress, keyType),
                $"dict_key_arg_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        var promotedLocal = call.IndirectArgumentLocalNames is not null && argumentIndex < call.IndirectArgumentLocalNames.Count
            ? call.IndirectArgumentLocalNames[argumentIndex]
            : null;
        if (!string.IsNullOrWhiteSpace(promotedLocal))
        {
            var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
            if (promotedParameter is not null)
            {
                if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                {
                    value = LoadDictionaryKeyScalarValue(
                        llvmType,
                        keyType,
                        $"%{EscapeIdentifier(promotedParameter.LlvmName)}",
                        GetTypeAlignmentSuffix(keyType),
                        $"dict_key_param_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
                    return true;
                }

                EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                value = LoadDictionaryKeyScalarValue(
                    llvmType,
                    keyType,
                    $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}",
                    GetStackObjectAlignmentSuffix(keyType),
                    $"dict_key_param_slot_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
                return true;
            }

            EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
            value = LoadDictionaryKeyScalarValue(
                llvmType,
                keyType,
                GetLocalSlotPointer(promotedLocal!),
                GetLocalSlotAlignmentSuffix(promotedLocal!, keyType),
                $"dict_key_local_{argumentIndex.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        if (argument.Type.BorrowKind == StarkBorrowKind.None
            && NormalizeDictionaryKeyType(argument.Type) == keyType)
        {
            value = FormatValue(argument);
            return true;
        }

        return false;
    }

    private string LoadDictionaryKeyScalarValue(
        string llvmType,
        StarkTypeSymbol keyType,
        string address,
        string alignmentSuffix,
        string tempPrefix)
    {
        var loaded = $"%{EscapeIdentifier(CreateAbiTempName(tempPrefix))}";
        AppendLine($"  {loaded} = load {llvmType}, ptr {address}{alignmentSuffix}{GetValueRangeMetadataSuffix(keyType)}");
        return loaded;
    }

    private void EmitDictionaryKeyHashValue(string result, StarkTypeSymbol keyType, string value)
    {
        var llvmType = MapType(keyType);
        if (keyType.Kind == StarkTypeKind.Bool)
        {
            AppendLine($"  {result} = zext i1 {value} to i64");
            return;
        }

        var bitWidth = keyType.BitWidth ?? 64;
        if (bitWidth == 64)
        {
            AppendLine($"  {result} = add i64 {value}, 0");
            return;
        }

        var opcode = bitWidth < 64 ? "zext" : "trunc";
        AppendLine($"  {result} = {opcode} {llvmType} {value} to i64");
    }

    private static StarkTypeSymbol NormalizeDictionaryKeyType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private static bool IsSupportedDictionaryKeyScalarType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool or StarkTypeKind.Integer;
    }

    private bool TryEmitAsciiToUnicodeLiteralCallSiteSpecialization(
        string? result,
        ISsaDirectCallOperation call,
        AbiFunctionSignature abiCallee)
    {
        if (!IsTryConvertAsciiToUnicodeAbiTarget(abiCallee)
            || call.Type.Kind != StarkTypeKind.Bool
            || call.Arguments.Count != 2
            || call.Arguments[0].Type is not { Kind: StarkTypeKind.RawPointer, ElementType: not null } destinationPointerType
            || !TryGetKnownAsciiLiteralPayload(call.Arguments[1], out var sourceBytes, out var sourceLiteralText)
            || !CanSplitCurrentBlockForCallSiteControlFlow())
        {
            return false;
        }

        var destinationStructType = destinationPointerType.ElementType;
        var destination = FormatValue(call.Arguments[0]);
        var destinationLlvmType = MapType(destinationStructType);
        var pointerAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: true));
        var lengthAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.Integer(64));
        var unitAlignment = GetTypeAlignmentSuffix(StarkTypeSymbols.Integer(32));

        var nullDestinationLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_null_destination"));
        var checkCapacityLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_check_capacity"));
        var checkStorageLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_check_storage"));
        var storeLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_store"));
        var failNonnullLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_fail_nonnull"));
        var doneLabel = EscapeIdentifier(CreateAbiTempName("ascii2unicode_done"));

        var destinationIsNull = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_destination_is_null"))}";
        AppendLine($"  {destinationIsNull} = icmp eq ptr {destination}, null");
        AppendLine($"  br i1 {destinationIsNull}, label %{nullDestinationLabel}, label %{checkCapacityLabel}");

        AppendLine($"{checkCapacityLabel}:");
        var dataAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data_addr"))}";
        var lengthAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_length_addr"))}";
        var capacityAddress = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity_addr"))}";
        var capacity = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity"))}";
        var capacityTooSmall = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_capacity_too_small"))}";
        AppendLine($"  {dataAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 0");
        AppendLine($"  {lengthAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 1");
        AppendLine($"  {capacityAddress} = getelementptr{GetProvenInObjectGepFlags()} {destinationLlvmType}, ptr {destination}, i32 0, i32 2");
        AppendLine($"  {capacity} = load i64, ptr {capacityAddress}{lengthAlignment}");
        AppendLine($"  {capacityTooSmall} = icmp slt i64 {capacity}, {sourceBytes.Length.ToString(CultureInfo.InvariantCulture)}");
        AppendLine($"  br i1 {capacityTooSmall}, label %{failNonnullLabel}, label %{checkStorageLabel}");

        string? data = null;
        AppendLine($"{checkStorageLabel}:");
        if (sourceBytes.Length == 0)
        {
            AppendLine($"  br label %{storeLabel}");
        }
        else
        {
            data = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data"))}";
            var dataIsNull = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_data_is_null"))}";
            AppendLine($"  {data} = load ptr, ptr {dataAddress}{pointerAlignment}");
            AppendLine($"  {dataIsNull} = icmp eq ptr {data}, null");
            AppendLine($"  br i1 {dataIsNull}, label %{failNonnullLabel}, label %{storeLabel}");
        }

        AppendLine($"{storeLabel}:");
        if (sourceLiteralText is not null
            && sourceBytes.Length >= LlvmTextOptimizationConstants.AsciiToUnicodeLiteralMemcpyThresholdCodeUnits)
        {
            var unicodeConstant = ResolveStringConstant(sourceLiteralText, StarkTypeSymbols.Unicode);
            var copyByteLength = checked(sourceBytes.Length * 4);
            AppendLine($"  call void @llvm.memcpy.p0.p0.i64(ptr align 4 {data}, ptr align {unicodeConstant.AlignmentBytes} @{unicodeConstant.SymbolName}, i64 {copyByteLength.ToString(CultureInfo.InvariantCulture)}, i1 false)");
        }
        else
        {
            for (var index = 0; index < sourceBytes.Length; index++)
            {
                var destinationUnit = $"%{EscapeIdentifier(CreateAbiTempName("ascii2unicode_unit"))}";
                AppendLine($"  {destinationUnit} = getelementptr{GetProvenInObjectGepFlags()} i32, ptr {data}, i64 {index.ToString(CultureInfo.InvariantCulture)}");
                AppendLine($"  store i32 {sourceBytes[index].ToString(CultureInfo.InvariantCulture)}, ptr {destinationUnit}{unitAlignment}");
            }
        }

        AppendLine($"  store i64 {sourceBytes.Length.ToString(CultureInfo.InvariantCulture)}, ptr {lengthAddress}{lengthAlignment}");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{failNonnullLabel}:");
        AppendLine($"  store i64 0, ptr {lengthAddress}{lengthAlignment}");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{nullDestinationLabel}:");
        AppendLine($"  br label %{doneLabel}");

        AppendLine($"{doneLabel}:");
        if (result is not null)
        {
            AppendLine($"  {result} = phi i1 [ true, %{storeLabel} ], [ false, %{failNonnullLabel} ], [ false, %{nullDestinationLabel} ]");
        }

        if (_currentBlock is not null)
        {
            _blockExitLabels[_currentBlock.Id] = doneLabel;
        }

        return true;
    }

    private bool TryGetKnownAsciiLiteralPayload(
        SsaValue value,
        out byte[] sourceBytes,
        out string? literalText)
    {
        return TryGetKnownAsciiLiteralPayload(
            value,
            new HashSet<string>(StringComparer.Ordinal),
            out sourceBytes,
            out literalText);
    }

    private bool TryGetKnownAsciiLiteralPayload(
        SsaValue value,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        if (value is SsaStringConstant { Type.Kind: StarkTypeKind.Ascii } source)
        {
            if (!TextLiteralDecoder.TryDecode(
                    source.LiteralText,
                    source.LiteralText.StartsWith("'", StringComparison.Ordinal)
                        ? TextLiteralKind.Character
                        : TextLiteralKind.String,
                    out var decoded,
                    out _)
                || !decoded.IsAscii)
            {
                return false;
            }

            sourceBytes = decoded.Utf8Bytes;
            literalText = source.LiteralText;
            return true;
        }

        if (value is SsaValueReference reference)
        {
            if (_valueFacts.TryGetValue(reference.Name, out var facts)
                && facts.Type.Kind == StarkTypeKind.Ascii
                && facts.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                && facts.TextLiteralPayload is { IsAsciiOnly: true } payload
                && TryDecodeAsciiPayloadFact(payload, out sourceBytes, out literalText))
            {
                return true;
            }

            if (visitedNames.Add($"value:{reference.Name}")
                && _valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryGetKnownAsciiLiteralPayload(
                    definition,
                    visitedNames,
                    out sourceBytes,
                    out literalText);
            }
        }

        return false;
    }

    private bool TryGetKnownAsciiLiteralPayload(
        SsaRValue value,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        return value switch
        {
            SsaUseRValue use => TryGetKnownAsciiLiteralPayload(
                use.Value,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaTextSliceRValue { Type.Kind: StarkTypeKind.Ascii } textSlice => TryGetKnownAsciiLiteralSlicePayload(
                textSlice,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Ascii } loadLocal => TryGetKnownAsciiLiteralPayloadFromLocal(
                loadLocal,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Ascii => TryGetKnownAsciiLiteralPayload(
                convert.Operand,
                visitedNames,
                out sourceBytes,
                out literalText),
            _ => false
        };
    }

    private bool TryGetKnownAsciiLiteralPayloadFromLocal(
        SsaLoadLocalRValue loadLocal,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        return visitedNames.Add($"local:{loadLocal.LocalName}")
               && TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue)
               && TryGetKnownAsciiLiteralPayload(
                   storedValue,
                   visitedNames,
                   out sourceBytes,
                   out literalText);
    }

    private bool TryGetKnownAsciiLiteralSlicePayload(
        SsaTextSliceRValue textSlice,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        if (!TryGetKnownAsciiLiteralPayload(
                textSlice.TextValue,
                visitedNames,
                out var sourcePayload,
                out _)
            || !TryGetExactNonNegativeIntegerValue(textSlice.Start, out var start)
            || !TryGetExactNonNegativeIntegerValue(textSlice.Length, out var length)
            || !TrySliceAsciiPayload(sourcePayload, start, length, out sourceBytes))
        {
            return false;
        }

        literalText = TextLiteralDecoder.EncodeStringLiteral(Encoding.UTF8.GetString(sourceBytes));
        return true;
    }

    private static bool TryDecodeAsciiPayloadFact(
        SsaTextLiteralPayloadFact payload,
        out byte[] sourceBytes,
        out string? literalText)
    {
        sourceBytes = [];
        literalText = null;

        try
        {
            sourceBytes = Convert.FromHexString(payload.Utf8PayloadHex);
            if (sourceBytes.Length != payload.Utf8Length)
            {
                sourceBytes = [];
                return false;
            }

            literalText = TextLiteralDecoder.EncodeStringLiteral(payload.DecodedText);
            return true;
        }
        catch (FormatException)
        {
            sourceBytes = [];
            return false;
        }
    }

    private bool TryGetExactNonNegativeIntegerValue(SsaValue value, out BigInteger exact)
    {
        if (TryGetIntegerValueRange(value, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min == max
            && min >= BigInteger.Zero)
        {
            exact = min;
            return true;
        }

        exact = default;
        return false;
    }

    private static bool TrySliceAsciiPayload(
        IReadOnlyList<byte> sourceBytes,
        BigInteger start,
        BigInteger length,
        out byte[] slicedBytes)
    {
        slicedBytes = [];
        var end = start + length;
        if (start < BigInteger.Zero
            || length < BigInteger.Zero
            || start > int.MaxValue
            || length > int.MaxValue
            || end > sourceBytes.Count)
        {
            return false;
        }

        slicedBytes = sourceBytes
            .Skip((int)start)
            .Take((int)length)
            .ToArray();
        return true;
    }

    private bool CanSplitCurrentBlockForCallSiteControlFlow()
    {
        if (_currentBlock is null)
        {
            return false;
        }

        // Splitting the emitted LLVM block changes the predecessor label seen by successors.
        // Forward successors can read the recorded exit label when their phis are emitted.
        // Backedges target already-emitted phis, so keep those on the ordinary call path
        // until the rewrite moves to SSA blocks.
        foreach (var targetId in EnumerateTerminatorTargets(_currentBlock.Terminator))
        {
            if (_blocksById.TryGetValue(targetId, out var targetBlock)
                && targetBlock.Phis.Count != 0
                && (!_blockOrderById.TryGetValue(targetId, out var targetOrder)
                    || !_blockOrderById.TryGetValue(_currentBlock.Id, out var currentOrder)
                    || targetOrder <= currentOrder))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsTryConvertAsciiToUnicodeAbiTarget(AbiFunctionSignature abiCallee)
    {
        if (string.Equals(abiCallee.SymbolName, "System_Text_TryConvertAsciiToUnicode", StringComparison.Ordinal)
            || string.Equals(abiCallee.Name, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal)
            || string.Equals(abiCallee.DisplaySourceName, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(_context.ModuleName, "System.Text", StringComparison.Ordinal)
            && string.Equals(abiCallee.SymbolName, "TryConvertAsciiToUnicode", StringComparison.Ordinal);
    }

    private string RenderCallTarget(AbiFunctionSignature abiCallee)
    {
        var escapedName = $"@{EscapeIdentifier(abiCallee.SymbolName)}";
        if (!abiCallee.IsVarargs)
        {
            return escapedName;
        }

        var parameterTypes = abiCallee.Parameters
            .Select(parameter => MapType(parameter.LlvmType))
            .Append("...");
        return $"({string.Join(", ", parameterTypes)}) {escapedName}";
    }

    private string RenderCallResultType(AbiFunctionSignature abiFunction)
    {
        return ShouldEmitPointerReturnCallAttributes(abiFunction)
            ? _attributeBuilder.RenderAbiReturnType(abiFunction)
            : MapType(abiFunction.LlvmReturnType);
    }

    private static bool ShouldEmitPointerReturnCallAttributes(AbiFunctionSignature abiFunction)
    {
        if (abiFunction.IsFfi || abiFunction.ReturnsIndirect)
        {
            return false;
        }

        return abiFunction.LlvmReturnType.Kind == StarkTypeKind.FunctionPointer
            || (abiFunction.LlvmReturnType.Kind == StarkTypeKind.RawPointer
                && StarkTypeSymbols.IsPointerBackedBorrowReturn(abiFunction.SourceReturnType));
    }

    private void EmitIndirectCallInstruction(SsaIndirectCallInstruction instruction)
    {
        EmitIndirectCall(
            resultName: null,
            result: null,
            instruction,
            instruction.ScopedNoAliasGroups,
            instruction.LoopAccessGroups);
    }

    private void EmitIndirectCall(
        string? resultName,
        string? result,
        ISsaIndirectCallOperation call,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        IReadOnlyList<string>? loopAccessGroups)
    {
        var abiCallee = BuildIndirectCallAbi(call);
        var sourceReturnType = call.SourceReturnType ?? call.Type;
        var arguments = new List<string>();
        string? indirectReturnSlot = null;

        if (abiCallee.ReturnsIndirect)
        {
            if (resultName is null
                || !TryGetCurrentReturnBufferForwardingAddress(resultName, sourceReturnType, out indirectReturnSlot))
            {
                indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("indirect_callret_slot"))}";
                QueueStaticAlloca(indirectReturnSlot, sourceReturnType);
            }

            arguments.Add(RenderSRetArgumentPointer(abiCallee, abiCallee.ReturnBufferParameter!, indirectReturnSlot, includeContractAttributes: true));
        }

        var parameterEffects = BuildIndirectCallParameterEffects(abiCallee, call.Target.Type);

        var userParameters = abiCallee.UserParameters;
        if (userParameters.Count != call.Arguments.Count)
        {
            throw new UnsupportedBodyEmissionException(
                $"Indirect call argument count mismatch: expected {userParameters.Count}, got {call.Arguments.Count}.");
        }

        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var parameter = userParameters[index];
            var argument = call.Arguments[index];

            if (parameter.Kind == AbiParameterKind.Direct)
            {
                arguments.Add(RenderDirectArgument(abiCallee, parameter, argument, parameterEffects, includeContractAttributes: true));
                continue;
            }

            var indirectArgumentAddress = call.IndirectArgumentAddresses is not null && index < call.IndirectArgumentAddresses.Count
                ? call.IndirectArgumentAddresses[index]
                : null;
            if (indirectArgumentAddress is not null)
            {
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, FormatValue(indirectArgumentAddress), parameterEffects, includeContractAttributes: true));
                continue;
            }

            var promotedLocal = call.IndirectArgumentLocalNames is not null && index < call.IndirectArgumentLocalNames.Count
                ? call.IndirectArgumentLocalNames[index]
                : null;
            if (!string.IsNullOrWhiteSpace(promotedLocal))
            {
                var promotedParameter = _abiFunction.UserParameters.FirstOrDefault(
                    candidate => string.Equals(candidate.SourceName, promotedLocal, StringComparison.Ordinal));
                if (promotedParameter is not null)
                {
                    if (promotedParameter.Kind == AbiParameterKind.IndirectIn)
                    {
                        arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, $"%{EscapeIdentifier(promotedParameter.LlvmName)}", parameterEffects, includeContractAttributes: true));
                    }
                    else
                    {
                        EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                        arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}", parameterEffects, includeContractAttributes: true));
                    }

                    continue;
                }

                EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, GetLocalSlotPointer(promotedLocal!), parameterEffects, includeContractAttributes: true));
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && TryResolveAggregateSourceAddress(argument, parameter.SourceType, out var forwardedSourceAddress))
            {
                arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, forwardedSourceAddress, parameterEffects, includeContractAttributes: true));
                continue;
            }

            var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"indirect_callarg_{parameter.SourceName}"))}";
            QueueStaticAlloca(tempSlot, parameter.SourceType);
            EmitValueToAddress(tempSlot, parameter.SourceType, argument, GetStackObjectAlignmentBytes(parameter.SourceType));

            arguments.Add(RenderIndirectArgumentPointer(abiCallee, parameter, tempSlot, parameterEffects, includeContractAttributes: true));
        }

        var callPrefixSegments = new List<string> { "call" };
        if (ShouldUseFastMathFlags(call.Type))
        {
            callPrefixSegments.Add("fast");
        }

        callPrefixSegments.Add("fastcc");
        var callPrefix = string.Join(" ", callPrefixSegments);
        var renderedArguments = string.Join(", ", arguments);
        var strictFpCallSuffix = GetStrictFpCallSuffix();
        var callSiteAttributes = _attributeBuilder.BuildFunctionPointerCallSiteAttributes(
            abiCallee,
            call.Target.Type.FunctionPointerKind ?? StarkFunctionKind.Fn);
        var callSiteAttributeSuffix = string.IsNullOrWhiteSpace(callSiteAttributes)
            ? string.Empty
            : $" {callSiteAttributes}";
        var calleesMetadataSuffix = GetKnownCalleesMetadataSuffix(call.Target);
        var callMetadataSuffix = GetCallInstructionMetadataSuffix(
            abiCallee,
            call.Arguments,
            call.IndirectArgumentAddresses,
            call.IndirectArgumentLocalNames,
            parameterEffects,
            BuildIndirectCallMemoryEffects(parameterEffects, call.Target.Type),
            scopedNoAliasGroups,
            loopAccessGroups);

        if (abiCallee.ReturnsIndirect)
        {
            AppendLine($"  {callPrefix} void {FormatValue(call.Target)}({renderedArguments}){callSiteAttributeSuffix}{strictFpCallSuffix}{calleesMetadataSuffix}{callMetadataSuffix}");
            if (resultName is not null)
            {
                _indirectAggregateValueSlots[resultName] = indirectReturnSlot!;
                if (result is not null && RequiresAggregateValueMaterialization(resultName, sourceReturnType))
                {
                    AppendLine($"  {result} = load {MapType(sourceReturnType)}, ptr {indirectReturnSlot}{GetStackObjectAlignmentSuffix(sourceReturnType)}{GetValueRangeMetadataSuffix(sourceReturnType)}{GetScopedNoAliasMetadataSuffix(CreateScopedAliasFreshResultRootKey(resultName))}");
                }
            }

            return;
        }

        if (call.Type.Kind == StarkTypeKind.Void)
        {
            AppendLine($"  {callPrefix} void {FormatValue(call.Target)}({renderedArguments}){callSiteAttributeSuffix}{strictFpCallSuffix}{calleesMetadataSuffix}{callMetadataSuffix}");
            return;
        }

        if (resultName is null || result is null)
        {
            AppendLine($"  {callPrefix} {RenderCallResultType(abiCallee)} {FormatValue(call.Target)}({renderedArguments}){callSiteAttributeSuffix}{strictFpCallSuffix}{calleesMetadataSuffix}{callMetadataSuffix}");
            return;
        }

        AppendLine($"  {result} = {callPrefix} {RenderCallResultType(abiCallee)} {FormatValue(call.Target)}({renderedArguments}){callSiteAttributeSuffix}{strictFpCallSuffix}{GetValueRangeMetadataSuffix(resultName, call.Type)}{calleesMetadataSuffix}{callMetadataSuffix}");
    }

    private string GetKnownCalleesMetadataSuffix(SsaValue target)
    {
        if (target is SsaFunctionAddressValue)
        {
            return string.Empty;
        }

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        return TryCollectKnownFunctionPointerTargets(target, targets, new HashSet<string>(StringComparer.Ordinal))
            && targets.Count > 1
            ? $", !callees {_context.GetMetadataTupleRef(targets.Select(static name => $"ptr @{EscapeIdentifier(name)}").ToArray())}"
            : string.Empty;
    }

    private bool TryCollectKnownFunctionPointerTargets(
        SsaValue value,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaFunctionAddressValue functionAddress:
                targets.Add(functionAddress.FunctionName);
                return true;
            case SsaValueReference reference:
                return TryCollectKnownFunctionPointerTargets(reference, targets, visitingValueNames);
            default:
                return false;
        }
    }

    private bool TryCollectKnownFunctionPointerTargets(
        SsaValueReference reference,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        if (!visitingValueNames.Add(reference.Name))
        {
            return false;
        }

        try
        {
            if (_trivialValueAliases.TryGetValue(reference.Name, out var alias))
            {
                return TryCollectKnownFunctionPointerTargets(alias, targets, visitingValueNames);
            }

            if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryCollectKnownFunctionPointerTargets(definition, targets, visitingValueNames);
            }

            if (_phisByResultName.TryGetValue(reference.Name, out var phi))
            {
                if (phi.Incomings.Count == 0)
                {
                    return false;
                }

                foreach (var incoming in phi.Incomings)
                {
                    if (!TryCollectKnownFunctionPointerTargets(incoming.Value, targets, visitingValueNames))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
        finally
        {
            visitingValueNames.Remove(reference.Name);
        }
    }

    private bool TryCollectKnownFunctionPointerTargets(
        SsaRValue value,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryCollectKnownFunctionPointerTargets(use.Value, targets, visitingValueNames);
            case SsaSelectRValue select:
                return TryCollectKnownFunctionPointerTargets(select.WhenTrue, targets, visitingValueNames)
                    && TryCollectKnownFunctionPointerTargets(select.WhenFalse, targets, visitingValueNames);
            case SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.FunctionPointer
                                             && convert.TargetType.Kind == StarkTypeKind.FunctionPointer:
                return TryCollectKnownFunctionPointerTargets(convert.Operand, targets, visitingValueNames);
            case SsaExtractIndexRValue { ElementIndex: 0 } extractIndex
                when extractIndex.Target.Type.Kind == StarkTypeKind.Closure:
                return TryCollectKnownClosureInvokeTargets(
                    extractIndex.Target,
                    targets,
                    visitingValueNames);
            default:
                return false;
        }
    }

    private bool TryCollectKnownClosureInvokeTargets(
        SsaValue value,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaClosureValue closure:
                targets.Add(closure.InvokeFunctionName);
                return true;
            case SsaValueReference reference:
                return TryCollectKnownClosureInvokeTargets(reference, targets, visitingValueNames);
            default:
                return false;
        }
    }

    private bool TryCollectKnownClosureInvokeTargets(
        SsaValueReference reference,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        if (!visitingValueNames.Add(reference.Name))
        {
            return false;
        }

        try
        {
            if (_trivialValueAliases.TryGetValue(reference.Name, out var alias))
            {
                return TryCollectKnownClosureInvokeTargets(alias, targets, visitingValueNames);
            }

            if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryCollectKnownClosureInvokeTargets(definition, targets, visitingValueNames);
            }

            if (_phisByResultName.TryGetValue(reference.Name, out var phi))
            {
                if (phi.Incomings.Count == 0)
                {
                    return false;
                }

                foreach (var incoming in phi.Incomings)
                {
                    if (!TryCollectKnownClosureInvokeTargets(incoming.Value, targets, visitingValueNames))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
        finally
        {
            visitingValueNames.Remove(reference.Name);
        }
    }

    private bool TryCollectKnownClosureInvokeTargets(
        SsaRValue value,
        ISet<string> targets,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryCollectKnownClosureInvokeTargets(use.Value, targets, visitingValueNames);
            case SsaSelectRValue select
                when select.Type.Kind == StarkTypeKind.Closure:
                return TryCollectKnownClosureInvokeTargets(select.WhenTrue, targets, visitingValueNames)
                    && TryCollectKnownClosureInvokeTargets(select.WhenFalse, targets, visitingValueNames);
            default:
                return false;
        }
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? ResolveCallParameterEffects(string functionName)
    {
        return _resolveParameterEffects?.Invoke(functionName, hasBody: true)
            ?? _resolveParameterEffects?.Invoke(functionName, hasBody: false);
    }

    private FunctionMemoryEffectSummary? ResolveCallMemoryEffects(string functionName)
    {
        var memoryEffects = _resolveFunctionMemoryEffects?.Invoke(functionName, hasBody: true)
            ?? _resolveFunctionMemoryEffects?.Invoke(functionName, hasBody: false);
        if (memoryEffects is not null)
        {
            return memoryEffects;
        }

        return _context.TryGetFunctionEffects(functionName) is { IsPure: true } effects
            ? new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: effects.ReadsArgumentMemory,
                WritesArgumentMemory: false,
                CapturesArgumentMemory: false)
            : null;
    }

    private static FunctionMemoryEffectSummary? BuildIndirectCallMemoryEffects(
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        StarkTypeSymbol functionPointerType)
    {
        if (!FunctionKindFacts.IsLaw(functionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn))
        {
            return null;
        }

        var parameters = parameterEffects?.Values.ToArray() ?? [];
        return new FunctionMemoryEffectSummary(
            ReadsArgumentMemory: parameters.Any(static parameter => parameter.Reads),
            WritesArgumentMemory: parameters.Any(static parameter => parameter.Writes),
            CapturesArgumentMemory: parameters.Any(static parameter => parameter.CaptureKind != ParameterCaptureKind.None));
    }

    private string GetCallInstructionMetadataSuffix(
        AbiFunctionSignature abiCallee,
        IReadOnlyList<SsaValue> arguments,
        IReadOnlyList<SsaValue?>? indirectArgumentAddresses,
        IReadOnlyList<string?>? indirectArgumentLocalNames,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        FunctionMemoryEffectSummary? memoryEffects,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        IReadOnlyList<string>? loopAccessGroups)
    {
        if (memoryEffects is null
            || !CanAttachCallMemoryMetadata(memoryEffects))
        {
            return string.Empty;
        }

        var suffix = string.Empty;
        if (TryCollectCallAccessRootKeys(
                abiCallee,
                arguments,
                indirectArgumentAddresses,
                indirectArgumentLocalNames,
                parameterEffects,
                memoryEffects,
                scopedNoAliasGroups,
                out var rootKeys))
        {
            suffix += GetCallScopedNoAliasMetadataSuffix(rootKeys, scopedNoAliasGroups);
        }

        suffix += GetLoopAccessGroupMetadataSuffix(loopAccessGroups);
        return suffix;
    }

    private static bool CanAttachCallMemoryMetadata(FunctionMemoryEffectSummary memoryEffects)
    {
        return (memoryEffects.ReadsArgumentMemory || memoryEffects.WritesArgumentMemory)
            && !memoryEffects.ReadsOtherMemory
            && !memoryEffects.WritesOtherMemory
            && !memoryEffects.CapturesArgumentMemory;
    }

    private bool TryCollectCallAccessRootKeys(
        AbiFunctionSignature abiCallee,
        IReadOnlyList<SsaValue> arguments,
        IReadOnlyList<SsaValue?>? indirectArgumentAddresses,
        IReadOnlyList<string?>? indirectArgumentLocalNames,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        FunctionMemoryEffectSummary memoryEffects,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        out IReadOnlySet<string> rootKeys)
    {
        var collected = new HashSet<string>(StringComparer.Ordinal);
        var allowedRootKeys = BuildScopedNoAliasRootSet(scopedNoAliasGroups);
        var userParameters = abiCallee.UserParameters;
        for (var index = 0; index < userParameters.Count; index++)
        {
            var parameter = userParameters[index];
            if (!CallParameterMayAccessMemory(parameter, parameterEffects, memoryEffects))
            {
                continue;
            }

            if (index >= arguments.Count
                || !TryResolveCallArgumentScopedRoot(
                    parameter,
                    arguments[index],
                    indirectArgumentAddresses is not null && index < indirectArgumentAddresses.Count
                        ? indirectArgumentAddresses[index]
                        : null,
                    indirectArgumentLocalNames is not null && index < indirectArgumentLocalNames.Count
                        ? indirectArgumentLocalNames[index]
                        : null,
                    allowedRootKeys,
                    out var rootKey))
            {
                rootKeys = new HashSet<string>(StringComparer.Ordinal);
                return false;
            }

            collected.Add(rootKey);
        }

        rootKeys = collected;
        return collected.Count > 0;
    }

    private static bool CallParameterMayAccessMemory(
        AbiParameterSymbol parameter,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        FunctionMemoryEffectSummary memoryEffects)
    {
        if (parameterEffects is not null
            && parameterEffects.TryGetValue(parameter.SourceName, out var effects))
        {
            return effects.Reads || effects.Writes;
        }

        return (memoryEffects.ReadsArgumentMemory || memoryEffects.WritesArgumentMemory)
            && ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType);
    }

    private bool TryResolveCallArgumentScopedRoot(
        AbiParameterSymbol parameter,
        SsaValue argument,
        SsaValue? indirectArgumentAddress,
        string? indirectArgumentLocalName,
        ISet<string>? allowedRootKeys,
        out string rootKey)
    {
        if (indirectArgumentAddress is not null)
        {
            return TryResolveScopedNoAliasRoot(
                indirectArgumentAddress,
                new HashSet<string>(StringComparer.Ordinal),
                out rootKey,
                allowedRootKeys);
        }

        if (!string.IsNullOrWhiteSpace(indirectArgumentLocalName))
        {
            if (_abiFunction.UserParameters.Any(candidate =>
                    string.Equals(candidate.SourceName, indirectArgumentLocalName, StringComparison.Ordinal)))
            {
                return TryUseScopedNoAliasRoot(
                    CreateScopedAliasParameterRootKey(indirectArgumentLocalName),
                    out rootKey,
                    allowedRootKeys);
            }

            return TryUseScopedNoAliasRoot(
                CreateScopedAliasDynamicLocalRootKey(indirectArgumentLocalName),
                out rootKey,
                allowedRootKeys);
        }

        if (parameter.Kind != AbiParameterKind.Direct)
        {
            rootKey = string.Empty;
            return false;
        }

        if (IsSliceLikeMemoryView(parameter.SourceType))
        {
            return TryResolveScopedNoAliasSliceRoot(
                argument,
                new HashSet<string>(StringComparer.Ordinal),
                out rootKey,
                allowedRootKeys);
        }

        return TryResolveScopedNoAliasRoot(
            argument,
            new HashSet<string>(StringComparer.Ordinal),
            out rootKey,
            allowedRootKeys);
    }

    private static bool IsSliceLikeMemoryView(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private string GetCallScopedNoAliasMetadataSuffix(
        IReadOnlySet<string> rootKeys,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups)
    {
        var aliasScopeRefs = new List<string>();
        var noAliasScopeRefs = new List<string>();
        AddFunctionScopedCallNoAliasMetadataRefs(rootKeys, aliasScopeRefs, noAliasScopeRefs);
        AddRuntimeScopedCallNoAliasMetadataRefs(rootKeys, scopedNoAliasGroups, aliasScopeRefs, noAliasScopeRefs);
        if (aliasScopeRefs.Count == 0)
        {
            return string.Empty;
        }

        var suffix = $", !alias.scope {_context.GetMetadataTupleRef(aliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray())}";
        var distinctNoAliasScopeRefs = noAliasScopeRefs.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctNoAliasScopeRefs.Length != 0)
        {
            suffix += $", !noalias {_context.GetMetadataTupleRef(distinctNoAliasScopeRefs)}";
        }

        return suffix;
    }

    private void AddFunctionScopedCallNoAliasMetadataRefs(
        IReadOnlySet<string> rootKeys,
        ICollection<string> aliasScopeRefs,
        ICollection<string> noAliasScopeRefs)
    {
        if (_scopedNoAliasMetadata is null)
        {
            return;
        }

        var touchedScopeRefs = rootKeys
            .Select(rootKey => _scopedNoAliasMetadata.ScopeRefs.TryGetValue(rootKey, out var scopeRef) ? scopeRef : null)
            .Where(static scopeRef => scopeRef is not null)
            .Cast<string>()
            .ToArray();
        foreach (var scopeRef in touchedScopeRefs)
        {
            aliasScopeRefs.Add(scopeRef);
        }

        if (touchedScopeRefs.Length == 0)
        {
            return;
        }

        foreach (var scopeRef in _scopedNoAliasMetadata.ScopeRefs
                     .Where(scope => !rootKeys.Contains(scope.Key))
                     .Select(static scope => scope.Value))
        {
            noAliasScopeRefs.Add(scopeRef);
        }
    }

    private void AddRuntimeScopedCallNoAliasMetadataRefs(
        IReadOnlySet<string> rootKeys,
        IReadOnlyList<ScopedNoAliasGroup>? scopedNoAliasGroups,
        ICollection<string> aliasScopeRefs,
        ICollection<string> noAliasScopeRefs)
    {
        if (scopedNoAliasGroups is not { Count: > 0 })
        {
            return;
        }

        foreach (var group in scopedNoAliasGroups)
        {
            var groupRootKeys = group.RootKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(CanonicalizeScopedNoAliasRootKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (groupRootKeys.Length < 2)
            {
                continue;
            }

            var touchedRootKeys = groupRootKeys
                .Where(rootKeys.Contains)
                .ToArray();
            if (touchedRootKeys.Length == 0)
            {
                continue;
            }

            var domainKey = $"runtime-disjoint:{_abiFunction.SymbolName}:{group.ScopeId}";
            var domainRef = _context.GetAliasScopeDomainRef(
                domainKey,
                $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}");
            foreach (var candidateRootKey in groupRootKeys)
            {
                var scopeRef = _context.GetAliasScopeRef(
                    $"{domainKey}:{candidateRootKey}",
                    domainRef,
                    $"stark.noalias.{_abiFunction.SymbolName}.{group.ScopeId}.{FormatScopedNoAliasRootDisplayName(candidateRootKey)}");
                if (rootKeys.Contains(candidateRootKey))
                {
                    aliasScopeRefs.Add(scopeRef);
                }
                else
                {
                    noAliasScopeRefs.Add(scopeRef);
                }
            }
        }
    }

    private AbiFunctionSignature BuildIndirectCallAbi(ISsaIndirectCallOperation call)
    {
        if (call.Target.Type.FunctionPointerReturnType is not { } returnType
            || call.Target.Type.FunctionPointerParameterTypes is not { } parameterTypes)
        {
            throw new UnsupportedBodyEmissionException("Indirect call target is missing function-pointer ABI metadata.");
        }

        var signature = new TypedFunctionSignature(
            "$indirect",
            returnType,
            parameterTypes
                .Select((parameterType, index) => new TypedParameterSymbol(
                    $"arg{index.ToString(CultureInfo.InvariantCulture)}",
                    parameterType,
                    RawPointerElementCountExpression: StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
                        call.Target.Type,
                        index)))
                .ToArray(),
            Kind: call.Target.Type.FunctionPointerKind ?? StarkFunctionKind.Fn,
            DisjointParameterGroups: call.Target.Type.FunctionPointerDisjointParameterGroups ?? [],
            OverlapParameterGroups: call.Target.Type.FunctionPointerOverlapParameterGroups ?? [],
            SameParameterGroups: call.Target.Type.FunctionPointerSameParameterGroups ?? []);
        return LlvmSpecializationEmissionPlanner.BuildSyntheticAbiSignature(
            signature,
            "$indirect",
            isFfi: false,
            _context.TypeModel.NamedTypes,
            _context.EnumLayouts);
    }

    private static IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? BuildIndirectCallParameterEffects(
        AbiFunctionSignature abiCallee,
        StarkTypeSymbol functionPointerType)
    {
        var memoryBackedParameters = abiCallee.UserParameters
            .Where(static parameter => ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType))
            .Select(static parameter => parameter.SourceName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (memoryBackedParameters.Length == 0)
        {
            return null;
        }

        return abiCallee.UserParameters
            .Where(static parameter => ParameterMemoryContractFacts.IsMemoryBacked(parameter.SourceType))
            .ToDictionary(
                static parameter => parameter.SourceName,
                parameter =>
                {
                    var guaranteedReadOnly = DeriveIndirectCallParameterReadOnly(parameter.SourceType);
                    var guaranteedWriteOnly = parameter.SourceType.InitializationKind != StarkInitializationKind.None;
                    var reads = !guaranteedWriteOnly;
                    var writes = guaranteedWriteOnly || !guaranteedReadOnly;
                    return new ParameterMemoryEffectSummary(
                        parameter.SourceName,
                        parameter.SourceType.DisplayName,
                        IsMemoryBacked: true,
                        GuaranteedNonNull: parameter.SourceType.BorrowKind != StarkBorrowKind.None
                            || parameter.SourceType.InitializationKind != StarkInitializationKind.None,
                        GuaranteedReadOnly: guaranteedReadOnly,
                        GuaranteedWriteOnly: guaranteedWriteOnly,
                        GuaranteedNoAlias: IsFunctionPointerParameterNoAliasAgainstAll(
                            parameter.SourceName,
                            memoryBackedParameters,
                            functionPointerType.FunctionPointerDisjointParameterGroups ?? []),
                        DereferenceableBytes: null,
                        AlignmentBytes: null,
                        Reads: reads,
                        Writes: writes,
                        CaptureKind: ParameterCaptureKind.Escape);
                },
                StringComparer.Ordinal);
    }

    private static bool IsFunctionPointerParameterNoAliasAgainstAll(
        string parameterName,
        IReadOnlyList<string> memoryBackedParameterNames,
        IReadOnlyList<ParameterDisjointGroup> disjointGroups)
    {
        if (memoryBackedParameterNames.Count <= 1)
        {
            return false;
        }

        foreach (var otherName in memoryBackedParameterNames)
        {
            if (string.Equals(parameterName, otherName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!DisjointGroupsContainPair(disjointGroups, parameterName, otherName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DisjointGroupsContainPair(
        IReadOnlyList<ParameterDisjointGroup> disjointGroups,
        string leftName,
        string rightName)
    {
        foreach (var group in disjointGroups)
        {
            if (group.HasSubregions)
            {
                continue;
            }

            var containsLeft = false;
            var containsRight = false;
            foreach (var parameterName in group.ParameterNames)
            {
                containsLeft |= string.Equals(parameterName, leftName, StringComparison.Ordinal);
                containsRight |= string.Equals(parameterName, rightName, StringComparison.Ordinal);
                if (containsLeft && containsRight)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildParameterPairKey(string left, string right)
    {
        return string.CompareOrdinal(left, right) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }

    private static bool DeriveIndirectCallParameterReadOnly(StarkTypeSymbol type)
    {
        if (type.InitializationKind != StarkInitializationKind.None)
        {
            return false;
        }

        return (type.Kind == StarkTypeKind.RawPointer && !type.IsMutablePointer)
            || type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || (type.BorrowKind != StarkBorrowKind.None && !type.IsMutableView)
            || type.AccessKind is StarkAccessKind.Shared or StarkAccessKind.Frozen;
    }

    private bool TryGetCurrentReturnBufferForwardingAddress(
        string resultName,
        StarkTypeSymbol resultType,
        out string returnBufferAddress)
    {
        returnBufferAddress = string.Empty;
        if (!_abiFunction.ReturnsIndirect
            || _abiFunction.ReturnBufferParameter is not { } returnBufferParameter
            || _currentBlock is not { } currentBlock
            || currentBlock.Terminator.Kind != SsaTerminatorKind.Return
            || !CanForwardAggregateValueToAddress(_function.ReturnType, resultType)
            || !IsNamedReference(currentBlock.Terminator.Value, resultName)
            || RequiresAggregateValueMaterialization(resultName, resultType))
        {
            return false;
        }

        returnBufferAddress = $"%{EscapeIdentifier(returnBufferParameter.LlvmName)}";
        return true;
    }

    private bool TryGetImmediateAggregateForwardingAddress(
        string resultName,
        StarkTypeSymbol resultType,
        out string destinationAddress,
        out int? destinationAlignmentBytes)
    {
        destinationAddress = string.Empty;
        destinationAlignmentBytes = null;

        if (_currentBlock is null)
        {
            return false;
        }

        for (var index = _currentInstructionIndex + 1; index < _currentBlock.Instructions.Count; index++)
        {
            var instruction = _currentBlock.Instructions[index];
            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal
                    when IsNamedReference(storeLocal.Value, resultName)
                         && CanForwardAggregateValueToAddress(storeLocal.LocalType, resultType)
                         && ValueHasOnlyForwardableStoreUse(resultName, storeLocal):
                    EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
                    destinationAddress = GetLocalSlotPointer(storeLocal.LocalName);
                    destinationAlignmentBytes = GetLocalSlotAlignmentBytes(storeLocal.LocalName, storeLocal.LocalType);
                    return true;
                case SsaStoreIndirectInstruction storeIndirect
                    when IsNamedReference(storeIndirect.Value, resultName)
                         && ValueHasOnlyForwardableStoreUse(resultName, storeIndirect)
                         && TryResolveForwardingDestinationAddress(
                             storeIndirect.Address,
                             storeIndirect.ValueType,
                             out destinationAddress,
                             out destinationAlignmentBytes):
                    return true;
                case SsaStoreGlobalInstruction storeGlobal
                    when IsNamedReference(storeGlobal.Value, resultName)
                         && CanForwardAggregateValueToAddress(storeGlobal.GlobalType, resultType)
                         && ValueHasOnlyForwardableStoreUse(resultName, storeGlobal):
                    destinationAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(storeGlobal.GlobalName))}";
                    destinationAlignmentBytes = GetGlobalObjectAlignmentBytes(storeGlobal.GlobalName, storeGlobal.GlobalType);
                    return true;
                case SsaValueInstruction valueInstruction
                    when CanSkipValueInstructionBeforeForwardedAggregateStore(valueInstruction, resultName):
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private bool CanSkipValueInstructionBeforeForwardedAggregateStore(
        SsaValueInstruction instruction,
        string resultName)
    {
        return !RValueContainsNamedReference(instruction.Value, resultName)
            && instruction.Value is SsaUseRValue
                or SsaConvertRValue
                or SsaAddressOfLocalRValue
                or SsaAddressOfParameterRValue
                or SsaFieldAddressRValue
                or SsaElementAddressRValue;
    }

    private bool ValueHasOnlyForwardableStoreUse(string valueName, SsaInstruction allowedStore)
    {
        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                if (phi.Incomings.Any(incoming => IsNamedReference(incoming.Value, valueName)))
                {
                    return false;
                }
            }

            foreach (var instruction in block.Instructions)
            {
                if (ReferenceEquals(instruction, allowedStore))
                {
                    continue;
                }

                if (InstructionDirectlyReferencesValue(instruction, valueName))
                {
                    return false;
                }
            }

            if (TerminatorDirectlyReferencesValue(block.Terminator, valueName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InstructionDirectlyReferencesValue(SsaInstruction instruction, string valueName)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => RValueContainsNamedReference(valueInstruction.Value, valueName),
            SsaStoreLocalInstruction storeLocal => IsNamedReference(storeLocal.Value, valueName),
            SsaStoreIndirectInstruction storeIndirect => IsNamedReference(storeIndirect.Value, valueName)
                || IsNamedReference(storeIndirect.Address, valueName),
            SsaStoreGlobalInstruction storeGlobal => IsNamedReference(storeGlobal.Value, valueName),
            SsaCopyMemoryInstruction copyMemory => IsNamedReference(copyMemory.DestinationAddress, valueName)
                || IsNamedReference(copyMemory.SourceAddress, valueName),
            _ => false
        };
    }

    private static bool TerminatorDirectlyReferencesValue(SsaTerminator terminator, string valueName)
    {
        if (IsNamedReference(terminator.Value, valueName)
            || IsNamedReference(terminator.Condition, valueName))
        {
            return true;
        }

        return terminator.SwitchCases is not null
            && terminator.SwitchCases.Any(switchCase => IsNamedReference(switchCase.MatchValue, valueName));
    }

    private bool TryResolveForwardingDestinationAddress(
        SsaValue addressValue,
        StarkTypeSymbol valueType,
        out string destinationAddress,
        out int? destinationAlignmentBytes)
    {
        return TryResolveForwardingDestinationAddress(
            addressValue,
            valueType,
            new HashSet<string>(StringComparer.Ordinal),
            out destinationAddress,
            out destinationAlignmentBytes);
    }

    private bool TryResolveForwardingDestinationAddress(
        SsaValue addressValue,
        StarkTypeSymbol valueType,
        ISet<string> visitedValueNames,
        out string destinationAddress,
        out int? destinationAlignmentBytes)
    {
        switch (addressValue)
        {
            case SsaValueReference reference:
                if (_valueAliases.TryGetValue(reference.Name, out var alias))
                {
                    destinationAddress = alias;
                    destinationAlignmentBytes = GetKnownPointerAlignmentBytes(addressValue, valueType);
                    return true;
                }

                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    destinationAddress = string.Empty;
                    destinationAlignmentBytes = null;
                    return false;
                }

                if (!TryResolveForwardingDestinationAddress(
                        definition,
                        valueType,
                        visitedValueNames,
                        out destinationAddress,
                        out destinationAlignmentBytes))
                {
                    return false;
                }

                _valueAliases[reference.Name] = destinationAddress;
                return true;
            case SsaGlobalAddressValue globalAddress
                when NormalizeAggregateType(globalAddress.PointeeType) == NormalizeAggregateType(valueType):
                destinationAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}";
                destinationAlignmentBytes = GetGlobalObjectAlignmentBytes(globalAddress.GlobalName, globalAddress.PointeeType);
                return true;
            default:
                destinationAddress = string.Empty;
                destinationAlignmentBytes = null;
                return false;
        }
    }

    private bool TryResolveForwardingDestinationAddress(
        SsaRValue addressValue,
        StarkTypeSymbol valueType,
        ISet<string> visitedValueNames,
        out string destinationAddress,
        out int? destinationAlignmentBytes)
    {
        switch (addressValue)
        {
            case SsaUseRValue use:
                return TryResolveForwardingDestinationAddress(
                    use.Value,
                    valueType,
                    visitedValueNames,
                    out destinationAddress,
                    out destinationAlignmentBytes);
            case SsaConvertRValue convert
                when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                     && convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryResolveForwardingDestinationAddress(
                    convert.Operand,
                    valueType,
                    visitedValueNames,
                    out destinationAddress,
                    out destinationAlignmentBytes);
            case SsaAddressOfLocalRValue addressOfLocal
                when NormalizeAggregateType(addressOfLocal.PointeeType) == NormalizeAggregateType(valueType):
                EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
                destinationAddress = GetLocalSlotPointer(addressOfLocal.LocalName);
                destinationAlignmentBytes = GetLocalSlotAlignmentBytes(addressOfLocal.LocalName, addressOfLocal.PointeeType);
                return true;
            case SsaAddressOfParameterRValue addressOfParameter
                when NormalizeAggregateType(addressOfParameter.PointeeType) == NormalizeAggregateType(valueType):
                return TryResolveForwardingParameterAddress(
                    addressOfParameter,
                    out destinationAddress,
                    out destinationAlignmentBytes);
            case SsaFieldAddressRValue fieldAddress
                when fieldAddress.Type.ElementType is { } fieldType
                     && NormalizeAggregateType(fieldType) == NormalizeAggregateType(valueType)
                     && TryResolveForwardingDestinationAddress(
                         fieldAddress.Address,
                         fieldAddress.AggregateType,
                         visitedValueNames,
                         out var aggregateAddress,
                         out var aggregateAlignmentBytes):
                destinationAddress = EmitScalarizedAggregateLeafAddress(
                    aggregateAddress,
                    fieldAddress.AggregateType,
                    [fieldAddress.FieldIndex],
                    "forward_store_field");
                destinationAlignmentBytes = GetLeafAlignmentBytes(aggregateAlignmentBytes, fieldType)
                    ?? GetTypeAlignmentBytes(fieldType);
                return true;
            case SsaElementAddressRValue elementAddress
                when elementAddress.Type.ElementType is { } elementType
                     && NormalizeAggregateType(elementType) == NormalizeAggregateType(valueType)
                     && TryGetForwardingConstantElementIndex(elementAddress, out var constantIndex)
                     && TryResolveForwardingDestinationAddress(
                         elementAddress.Address,
                         elementAddress.AggregateType,
                         visitedValueNames,
                         out var aggregateAddress,
                         out var aggregateAlignmentBytes):
                destinationAddress = EmitForwardingElementDestinationAddress(
                    aggregateAddress,
                    elementAddress.AggregateType,
                    constantIndex);
                destinationAlignmentBytes = GetLeafAlignmentBytes(aggregateAlignmentBytes, elementType)
                    ?? GetTypeAlignmentBytes(elementType);
                return true;
            default:
                destinationAddress = string.Empty;
                destinationAlignmentBytes = null;
                return false;
        }
    }

    private bool TryResolveForwardingParameterAddress(
        SsaAddressOfParameterRValue addressOfParameter,
        out string destinationAddress,
        out int? destinationAlignmentBytes)
    {
        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
        if (parameter is null)
        {
            destinationAddress = string.Empty;
            destinationAlignmentBytes = null;
            return false;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            destinationAddress = $"%{EscapeIdentifier(parameter.LlvmName)}";
            destinationAlignmentBytes = GetTypeAlignmentBytes(addressOfParameter.PointeeType);
            return true;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        destinationAddress = $"%{EscapeIdentifier($"slot_param_{parameter.SourceName}")}";
        destinationAlignmentBytes = GetStackObjectAlignmentBytes(addressOfParameter.PointeeType);
        return true;
    }

    private static bool TryGetForwardingConstantElementIndex(
        SsaElementAddressRValue elementAddress,
        out int constantIndex)
    {
        if (elementAddress.ConstantIndex is int fixedIndex)
        {
            constantIndex = fixedIndex;
            return true;
        }

        if (elementAddress.Index is SsaIntegerConstant integerIndex
            && integerIndex.Value >= int.MinValue
            && integerIndex.Value <= int.MaxValue)
        {
            constantIndex = (int)integerIndex.Value;
            return true;
        }

        constantIndex = 0;
        return false;
    }

    private string EmitForwardingElementDestinationAddress(
        string aggregateAddress,
        StarkTypeSymbol aggregateType,
        int constantIndex)
    {
        var elementAddress = $"%{EscapeIdentifier(CreateAbiTempName("forward_store_index"))}";
        if (aggregateType.Kind == StarkTypeKind.FixedArray)
        {
            AppendLine($"  {elementAddress} = getelementptr{GetProvenInObjectGepFlags()} {MapType(aggregateType)}, ptr {aggregateAddress}, i32 0, i32 {constantIndex}");
            return elementAddress;
        }

        var flags = constantIndex == 0 ? GetZeroOffsetGepFlags() : string.Empty;
        AppendLine($"  {elementAddress} = getelementptr{flags} {MapType(aggregateType)}, ptr {aggregateAddress}, i32 {constantIndex}");
        return elementAddress;
    }

    private bool ShouldEmitTailCallMarker(string resultName)
    {
        return _tailCallResultNames.Contains(resultName);
    }
}
