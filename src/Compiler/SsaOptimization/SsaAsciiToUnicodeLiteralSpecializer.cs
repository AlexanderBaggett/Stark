using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaAsciiToUnicodeLiteralSpecializer
{
    private const int ScalarRewriteThresholdCodeUnits = 32;

    public SsaIrModule Optimize(
        SsaIrModule module,
        SsaValueFactModel facts)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = facts.Functions.TryGetValue(function.Name, out var functionFacts)
                    ? OptimizeFunction(function, module.ModuleName, functionFacts)
                    : function;
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private static SsaFunction OptimizeFunction(
        SsaFunction function,
        string moduleName,
        SsaFunctionFactModel facts)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = function;
        while (TryRewriteFirstEligibleCall(current, moduleName, facts, out var rewritten))
        {
            current = rewritten;
        }

        return ReferenceEquals(current, function)
            ? function
            : current;
    }

    private static bool TryRewriteFirstEligibleCall(
        SsaFunction function,
        string moduleName,
        SsaFunctionFactModel facts,
        out SsaFunction rewritten)
    {
        var usedValueNames = CollectDefinedValueNames(function);
        var valueDefinitions = CollectValueDefinitions(function);
        var nextBlockId = function.Blocks.Count == 0
            ? 0
            : function.Blocks.Max(static block => block.Id) + 1;

        for (var blockIndex = 0; blockIndex < function.Blocks.Count; blockIndex++)
        {
            var block = function.Blocks[blockIndex];
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (block.Instructions[instructionIndex] is not SsaValueInstruction
                    {
                        Value: SsaCallRValue call
                    } valueInstruction
                    || !TryCreateRewritePlan(
                        moduleName,
                        facts,
                        function,
                        valueDefinitions,
                        valueInstruction,
                        call,
                        out var destinationStructType,
                        out var sourceBytes,
                        out var sourceLiteralText))
                {
                    continue;
                }

                rewritten = RewriteCall(
                    function,
                    blockIndex,
                    instructionIndex,
                    valueInstruction,
                    call,
                    destinationStructType,
                    sourceBytes,
                    sourceLiteralText,
                    usedValueNames,
                    nextBlockId);
                return true;
            }
        }

        rewritten = function;
        return false;
    }

    private static bool TryCreateRewritePlan(
        string moduleName,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        SsaValueInstruction valueInstruction,
        SsaCallRValue call,
        out StarkTypeSymbol destinationStructType,
        out byte[] sourceBytes,
        out string sourceLiteralText)
    {
        destinationStructType = default!;
        sourceBytes = [];
        sourceLiteralText = string.Empty;

        if (!IsTryConvertAsciiToUnicodeCall(call.FunctionName, moduleName)
            || call.Type.Kind != StarkTypeKind.Bool
            || valueInstruction.Value.Type.Kind != StarkTypeKind.Bool
            || call.Arguments.Count != 2
            || call.Arguments[0].Type is not { Kind: StarkTypeKind.RawPointer, ElementType: not null } destinationPointerType
            || !TryGetKnownAsciiLiteralPayload(
                call.Arguments[1],
                facts,
                function,
                valueDefinitions,
                new HashSet<string>(StringComparer.Ordinal),
                out sourceBytes,
                out sourceLiteralText))
        {
            return false;
        }

        destinationStructType = destinationPointerType.ElementType;
        return true;
    }

    private static SsaFunction RewriteCall(
        SsaFunction function,
        int blockIndex,
        int instructionIndex,
        SsaValueInstruction callInstruction,
        SsaCallRValue call,
        StarkTypeSymbol destinationStructType,
        IReadOnlyList<byte> sourceBytes,
        string sourceLiteralText,
        ISet<string> usedValueNames,
        int nextBlockId)
    {
        var originalBlock = function.Blocks[blockIndex];
        var prefixInstructions = originalBlock.Instructions.Take(instructionIndex).ToArray();
        var suffixInstructions = originalBlock.Instructions.Skip(instructionIndex + 1).ToArray();
        var location = callInstruction.Location;
        var destination = call.Arguments[0];
        var sourceLength = sourceBytes.Count;

        var nullDestinationBlockId = nextBlockId++;
        var checkCapacityBlockId = nextBlockId++;
        var checkStorageBlockId = nextBlockId++;
        var storeBlockId = nextBlockId++;
        var failNonnullBlockId = nextBlockId++;
        var doneBlockId = nextBlockId;

        var labelBase = $"{originalBlock.Label}_ascii2unicode_{callInstruction.ResultName}";
        var unitType = StarkTypeSymbols.Integer(32);
        var lengthType = StarkTypeSymbols.Integer(64);
        var dataPointerType = StarkTypeSymbols.RawPointer(unitType, isMutable: true);
        var readonlyDataPointerType = StarkTypeSymbols.RawPointer(unitType, isMutable: false);
        var dataFieldAddressType = StarkTypeSymbols.RawPointer(dataPointerType, isMutable: true);
        var lengthFieldAddressType = StarkTypeSymbols.RawPointer(lengthType, isMutable: true);
        var unitAddressType = StarkTypeSymbols.RawPointer(unitType, isMutable: true);

        var destinationIsNullName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_destination_is_null");
        var dataAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_data_addr");
        var lengthAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_length_addr");
        var capacityAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_capacity_addr");
        var capacityName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_capacity");
        var capacityTooSmallName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_capacity_too_small");

        var originalReplacement = new SsaBasicBlock(
            originalBlock.Id,
            originalBlock.Label,
            originalBlock.Phis,
            prefixInstructions
                .Append(new SsaValueInstruction(
                    destinationIsNullName,
                    new SsaBinaryRValue(
                        SsaBinaryOperator.Equal,
                        destination,
                        new SsaNullConstant(destination.Type),
                        StarkTypeSymbols.Bool,
                        $"{destination.Text} == null"),
                    location))
                .ToArray(),
            new SsaTerminator(
                SsaTerminatorKind.Branch,
                [nullDestinationBlockId, checkCapacityBlockId],
                Condition: new SsaValueReference(destinationIsNullName, StarkTypeSymbols.Bool),
                Location: originalBlock.Terminator.Location));

        var dataAddressReference = new SsaValueReference(dataAddressName, dataFieldAddressType);
        var lengthAddressReference = new SsaValueReference(lengthAddressName, lengthFieldAddressType);
        var capacityAddressReference = new SsaValueReference(capacityAddressName, lengthFieldAddressType);
        var capacityReference = new SsaValueReference(capacityName, lengthType);

        var checkCapacityBlock = new SsaBasicBlock(
            checkCapacityBlockId,
            $"{labelBase}_check_capacity",
            [],
            [
                new SsaValueInstruction(
                    dataAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        destinationStructType,
                        "Data",
                        0,
                        dataFieldAddressType,
                        $"{destination.Text}.Data"),
                    location),
                new SsaValueInstruction(
                    lengthAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        destinationStructType,
                        "Length",
                        1,
                        lengthFieldAddressType,
                        $"{destination.Text}.Length"),
                    location),
                new SsaValueInstruction(
                    capacityAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        destinationStructType,
                        "Capacity",
                        2,
                        lengthFieldAddressType,
                        $"{destination.Text}.Capacity"),
                    location),
                new SsaValueInstruction(
                    capacityName,
                    new SsaLoadIndirectRValue(
                        capacityAddressReference,
                        lengthType,
                        $"{destination.Text}.Capacity")),
                new SsaValueInstruction(
                    capacityTooSmallName,
                    new SsaBinaryRValue(
                        SsaBinaryOperator.LessThan,
                        capacityReference,
                        new SsaIntegerConstant(sourceLength, lengthType),
                        StarkTypeSymbols.Bool,
                        $"{destination.Text}.Capacity < {sourceLength.ToString(CultureInfo.InvariantCulture)}"),
                    location)
            ],
            new SsaTerminator(
                SsaTerminatorKind.Branch,
                [failNonnullBlockId, checkStorageBlockId],
                Condition: new SsaValueReference(capacityTooSmallName, StarkTypeSymbols.Bool),
                Location: originalBlock.Terminator.Location));

        var checkStorageInstructions = new List<SsaInstruction>();
        SsaValueReference? dataReference = null;
        SsaTerminator checkStorageTerminator;
        if (sourceLength == 0)
        {
            checkStorageTerminator = new SsaTerminator(
                SsaTerminatorKind.Goto,
                [storeBlockId],
                Location: originalBlock.Terminator.Location);
        }
        else
        {
            var dataName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_data");
            var dataIsNullName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_data_is_null");
            dataReference = new SsaValueReference(dataName, dataPointerType);
            checkStorageInstructions.Add(new SsaValueInstruction(
                dataName,
                new SsaLoadIndirectRValue(
                    dataAddressReference,
                    dataPointerType,
                    $"{destination.Text}.Data"),
                location));
            checkStorageInstructions.Add(new SsaValueInstruction(
                dataIsNullName,
                new SsaBinaryRValue(
                    SsaBinaryOperator.Equal,
                    dataReference,
                    new SsaNullConstant(dataPointerType),
                    StarkTypeSymbols.Bool,
                    $"{destination.Text}.Data == null"),
                location));
            checkStorageTerminator = new SsaTerminator(
                SsaTerminatorKind.Branch,
                [failNonnullBlockId, storeBlockId],
                Condition: new SsaValueReference(dataIsNullName, StarkTypeSymbols.Bool),
                Location: originalBlock.Terminator.Location);
        }

        var checkStorageBlock = new SsaBasicBlock(
            checkStorageBlockId,
            $"{labelBase}_check_storage",
            [],
            checkStorageInstructions.ToArray(),
            checkStorageTerminator);

        var storeInstructions = new List<SsaInstruction>();
        if (dataReference is not null
            && sourceBytes.Count >= ScalarRewriteThresholdCodeUnits)
        {
            storeInstructions.Add(new SsaCopyMemoryInstruction(
                dataReference,
                new SsaTextDataAddressValue(
                    sourceLiteralText,
                    StarkTypeSymbols.Unicode,
                    readonlyDataPointerType),
                StarkTypeSymbols.FixedArray(unitType, sourceBytes.Count),
                Location: location));
        }
        else if (dataReference is not null)
        {
            for (var index = 0; index < sourceBytes.Count; index++)
            {
                var unitAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_ascii2unicode_unit");
                var unitAddressReference = new SsaValueReference(unitAddressName, unitAddressType);
                storeInstructions.Add(new SsaValueInstruction(
                    unitAddressName,
                    new SsaElementAddressRValue(
                        dataReference,
                        unitType,
                        Index: null,
                        ConstantIndex: index,
                        unitAddressType,
                        $"{destination.Text}.Data[{index.ToString(CultureInfo.InvariantCulture)}]"),
                    location));
                storeInstructions.Add(new SsaStoreIndirectInstruction(
                    unitAddressReference,
                    unitType,
                    new SsaIntegerConstant(sourceBytes[index], unitType),
                    location));
            }
        }

        storeInstructions.Add(new SsaStoreIndirectInstruction(
            lengthAddressReference,
            lengthType,
            new SsaIntegerConstant(sourceLength, lengthType),
            location));

        var storeBlock = new SsaBasicBlock(
            storeBlockId,
            $"{labelBase}_store",
            [],
            storeInstructions.ToArray(),
            new SsaTerminator(
                SsaTerminatorKind.Goto,
                [doneBlockId],
                Location: originalBlock.Terminator.Location));

        var failNonnullBlock = new SsaBasicBlock(
            failNonnullBlockId,
            $"{labelBase}_fail_nonnull",
            [],
            [
                new SsaStoreIndirectInstruction(
                    lengthAddressReference,
                    lengthType,
                    new SsaIntegerConstant(0, lengthType),
                    location)
            ],
            new SsaTerminator(
                SsaTerminatorKind.Goto,
                [doneBlockId],
                Location: originalBlock.Terminator.Location));

        var nullDestinationBlock = new SsaBasicBlock(
            nullDestinationBlockId,
            $"{labelBase}_null_destination",
            [],
            [],
            new SsaTerminator(
                SsaTerminatorKind.Goto,
                [doneBlockId],
                Location: originalBlock.Terminator.Location));

        var doneBlock = new SsaBasicBlock(
            doneBlockId,
            $"{labelBase}_done",
            [
                new SsaPhi(
                    callInstruction.ResultName,
                    callInstruction.ResultName,
                    StarkTypeSymbols.Bool,
                    [
                        new SsaPhiIncoming(storeBlockId, new SsaBoolConstant(true)),
                        new SsaPhiIncoming(failNonnullBlockId, new SsaBoolConstant(false)),
                        new SsaPhiIncoming(nullDestinationBlockId, new SsaBoolConstant(false))
                    ],
                    location)
            ],
            suffixInstructions,
            originalBlock.Terminator);

        var originalSuccessors = EnumerateTerminatorTargets(originalBlock.Terminator)
            .Distinct()
            .ToHashSet();
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count + 6);
        for (var index = 0; index < function.Blocks.Count; index++)
        {
            if (index != blockIndex)
            {
                blocks.Add(UpdatePhiPredecessor(function.Blocks[index], originalBlock.Id, doneBlockId, originalSuccessors));
                continue;
            }

            blocks.Add(UpdatePhiPredecessor(originalReplacement, originalBlock.Id, doneBlockId, originalSuccessors));
            blocks.Add(checkCapacityBlock);
            blocks.Add(checkStorageBlock);
            blocks.Add(storeBlock);
            blocks.Add(failNonnullBlock);
            blocks.Add(nullDestinationBlock);
            blocks.Add(doneBlock);
        }

        return function with { Blocks = blocks.ToArray() };
    }

    private static SsaBasicBlock UpdatePhiPredecessor(
        SsaBasicBlock block,
        int oldPredecessorId,
        int newPredecessorId,
        ISet<int> updatedSuccessors)
    {
        if (block.Phis.Count == 0
            || !updatedSuccessors.Contains(block.Id))
        {
            return block;
        }

        var changed = false;
        var phis = block.Phis
            .Select(phi =>
            {
                var phiChanged = false;
                var incomings = phi.Incomings
                    .Select(incoming =>
                    {
                        if (incoming.PredecessorBlockId != oldPredecessorId)
                        {
                            return incoming;
                        }

                        changed = true;
                        phiChanged = true;
                        return incoming with { PredecessorBlockId = newPredecessorId };
                    })
                    .ToArray();

                return phiChanged
                    ? phi with { Incomings = incomings }
                    : phi;
            })
            .ToArray();

        return changed
            ? block with { Phis = phis }
            : block;
    }

    private static bool TryGetKnownAsciiLiteralPayload(
        SsaValue value,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (value is SsaStringConstant { Type.Kind: StarkTypeKind.Ascii } source)
        {
            if (!TextLiteralDecoder.TryDecode(
                    source.LiteralText,
                    source.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
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
            if (facts.Values.TryGetValue(reference.Name, out var valueFacts)
                && valueFacts.Type.Kind == StarkTypeKind.Ascii
                && valueFacts.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                && valueFacts.TextLiteralPayload is { IsAsciiOnly: true } payload
                && TryDecodeAsciiPayloadFact(payload, out sourceBytes, out literalText))
            {
                return true;
            }

            if (visitedNames.Add($"value:{reference.Name}")
                && valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryGetKnownAsciiLiteralPayload(
                    definition,
                    facts,
                    function,
                    valueDefinitions,
                    visitedNames,
                    out sourceBytes,
                    out literalText);
            }
        }

        return false;
    }

    private static bool TryGetKnownAsciiLiteralPayload(
        SsaRValue value,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        return value switch
        {
            SsaUseRValue use => TryGetKnownAsciiLiteralPayload(
                use.Value,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaTextSliceRValue { Type.Kind: StarkTypeKind.Ascii } textSlice => TryGetKnownAsciiLiteralSlicePayload(
                textSlice,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Ascii } loadLocal => TryGetKnownAsciiLiteralPayloadFromLocal(
                loadLocal,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Ascii => TryGetKnownAsciiLiteralPayload(
                convert.Operand,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            _ => false
        };
    }

    private static bool TryGetKnownAsciiLiteralSlicePayload(
        SsaTextSliceRValue textSlice,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (!TryGetKnownAsciiLiteralPayload(
                textSlice.TextValue,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out var sourcePayload,
                out _)
            || !TryResolveExactNonNegativeInteger(textSlice.Start, valueDefinitions, out var start)
            || !TryResolveExactNonNegativeInteger(textSlice.Length, valueDefinitions, out var length)
            || !TrySliceAsciiPayload(sourcePayload, start, length, out sourceBytes))
        {
            return false;
        }

        literalText = TextLiteralDecoder.EncodeStringLiteral(Encoding.UTF8.GetString(sourceBytes));
        return true;
    }

    private static bool TryGetKnownAsciiLiteralPayloadFromLocal(
        SsaLoadLocalRValue loadLocal,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (!visitedNames.Add($"local:{loadLocal.LocalName}")
            || LocalAddressMayBeObserved(function, loadLocal.LocalName))
        {
            return false;
        }

        var stores = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaStoreLocalInstruction>()
            .Where(store => string.Equals(store.LocalName, loadLocal.LocalName, StringComparison.Ordinal)
                            && store.LocalType == loadLocal.Type)
            .ToArray();
        if (stores.Length != 1)
        {
            return false;
        }

        return TryGetKnownAsciiLiteralPayload(
            stores[0].Value,
            facts,
            function,
            valueDefinitions,
            visitedNames,
            out sourceBytes,
            out literalText);
    }

    private static bool LocalAddressMayBeObserved(SsaFunction function, string localName)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Any(instruction => SsaValueFactAnalyzer.RValueTakesLocalAddress(instruction.Value, localName));
    }

    private static bool TryDecodeAsciiPayloadFact(
        SsaTextLiteralPayloadFact payload,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        try
        {
            sourceBytes = Convert.FromHexString(payload.Utf8PayloadHex);
            if (sourceBytes.Length != payload.Utf8Length)
            {
                return false;
            }

            literalText = TextLiteralDecoder.EncodeStringLiteral(payload.DecodedText);
            return true;
        }
        catch (FormatException)
        {
            sourceBytes = [];
            literalText = string.Empty;
            return false;
        }
    }

    private static bool TryResolveExactNonNegativeInteger(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        out BigInteger exact)
    {
        if (TryResolveIntegerConstant(value, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out exact)
            && exact >= BigInteger.Zero)
        {
            return true;
        }

        exact = default;
        return false;
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out BigInteger constant)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    constant = default;
                    return false;
                }

                return definition switch
                {
                    SsaUseRValue use => TryResolveIntegerConstant(use.Value, valueDefinitions, visitedValueNames, out constant),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Integer =>
                        TryResolveIntegerConstant(convert.Operand, valueDefinitions, visitedValueNames, out constant),
                    _ => Fail(out constant)
                };
            default:
                constant = default;
                return false;
        }

        static bool Fail(out BigInteger value)
        {
            value = default;
            return false;
        }
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

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static bool IsTryConvertAsciiToUnicodeCall(string functionName, string moduleName)
    {
        return string.Equals(functionName, "System.Text.TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || functionName.EndsWith(".TryConvertAsciiToUnicode", StringComparison.Ordinal)
               || string.Equals(moduleName, "System.Text", StringComparison.Ordinal)
               && string.Equals(functionName, "TryConvertAsciiToUnicode", StringComparison.Ordinal);
    }

    private static HashSet<string> CollectDefinedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            names.Add($"arg_{parameter.Name}");
        }

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

    private static string CreateUniqueValueName(ISet<string> usedNames, string baseName)
    {
        var candidate = baseName;
        var suffix = 0;
        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static IEnumerable<int> EnumerateTerminatorTargets(SsaTerminator terminator)
    {
        foreach (var target in terminator.Targets)
        {
            yield return target;
        }

        if (terminator.DefaultTarget is int defaultTarget)
        {
            yield return defaultTarget;
        }
    }
}

