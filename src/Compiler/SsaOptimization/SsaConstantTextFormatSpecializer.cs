using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaConstantTextFormatSpecializer
{
    private const int CopyRewriteThresholdCodeUnits = 16;

    private static readonly Regex IntegerFormatFunctionPattern = new(
        @"(?:^|\.)(TryFormat)(?<kind>[IU])(?<width>8|16|24|32|48|64|96|128|192|256|384|512|768|1024)(?<text>Ascii|Unicode)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function, module.ModuleName);
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
        string moduleName)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = function;
        while (TryRewriteFirstEligibleCall(current, moduleName, out var rewritten))
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
                        valueDefinitions,
                        valueInstruction,
                        call,
                        out var plan))
                {
                    continue;
                }

                rewritten = RewriteCall(
                    function,
                    blockIndex,
                    instructionIndex,
                    valueInstruction,
                    call,
                    plan,
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
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        SsaValueInstruction valueInstruction,
        SsaCallRValue call,
        out TextFormatRewritePlan plan)
    {
        plan = default!;

        if (call.Type.Kind != StarkTypeKind.Bool
            || valueInstruction.Value.Type.Kind != StarkTypeKind.Bool
            || call.Arguments.Count != 2
            || !TryParseIntegerFormatCall(call.FunctionName, moduleName, out var signed, out var width, out var useUnicode)
            || call.Arguments[0].Type is not { Kind: StarkTypeKind.RawPointer, ElementType: not null } destinationPointerType
            || !destinationPointerType.IsMutablePointer
            || !TryGetOwnedTextKind(destinationPointerType.ElementType, out var destinationUsesUnicode)
            || destinationUsesUnicode != useUnicode
            || !TryResolveIntegerConstant(
                call.Arguments[1],
                valueDefinitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var value)
            || !IntegerFitsFormatWidth(value, signed, width))
        {
            return false;
        }

        var text = value.ToString(CultureInfo.InvariantCulture);
        var unitType = useUnicode
            ? StarkTypeSymbols.Integer(32)
            : StarkTypeSymbols.Integer(8);
        var sourceUnits = text.Select(character => new BigInteger(character)).ToArray();
        var literalText = TextLiteralDecoder.EncodeStringLiteral(text);
        plan = new TextFormatRewritePlan(
            destinationPointerType.ElementType,
            useUnicode ? StarkTypeSymbols.Unicode : StarkTypeSymbols.Ascii,
            unitType,
            sourceUnits,
            literalText);
        return true;
    }

    private static bool TryParseIntegerFormatCall(
        string functionName,
        string moduleName,
        out bool signed,
        out int width,
        out bool useUnicode)
    {
        signed = false;
        width = 0;
        useUnicode = false;

        var match = IntegerFormatFunctionPattern.Match(functionName);
        if (!match.Success)
        {
            return false;
        }

        if (!functionName.Contains('.', StringComparison.Ordinal)
            && !string.Equals(moduleName, "System.Text", StringComparison.Ordinal))
        {
            return false;
        }

        signed = string.Equals(match.Groups["kind"].Value, "I", StringComparison.Ordinal);
        width = int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture);
        useUnicode = string.Equals(match.Groups["text"].Value, "Unicode", StringComparison.Ordinal);
        return true;
    }

    private static bool TryGetOwnedTextKind(StarkTypeSymbol type, out bool useUnicode)
    {
        if (type.Kind == StarkTypeKind.Named
            && string.Equals(type.NamedType, StarkTypeSymbols.OwnedAsciiName, StringComparison.Ordinal))
        {
            useUnicode = false;
            return true;
        }

        if (type.Kind == StarkTypeKind.Named
            && string.Equals(type.NamedType, StarkTypeSymbols.OwnedUnicodeName, StringComparison.Ordinal))
        {
            useUnicode = true;
            return true;
        }

        useUnicode = false;
        return false;
    }

    private static bool IntegerFitsFormatWidth(BigInteger value, bool signed, int width)
    {
        var min = signed
            ? -(BigInteger.One << (width - 1))
            : BigInteger.Zero;
        var max = signed
            ? (BigInteger.One << (width - 1)) - 1
            : (BigInteger.One << width) - 1;
        return value >= min && value <= max;
    }

    private static SsaFunction RewriteCall(
        SsaFunction function,
        int blockIndex,
        int instructionIndex,
        SsaValueInstruction callInstruction,
        SsaCallRValue call,
        TextFormatRewritePlan plan,
        ISet<string> usedValueNames,
        int nextBlockId)
    {
        var originalBlock = function.Blocks[blockIndex];
        var prefixInstructions = originalBlock.Instructions.Take(instructionIndex).ToArray();
        var suffixInstructions = originalBlock.Instructions.Skip(instructionIndex + 1).ToArray();
        var location = callInstruction.Location;
        var destination = call.Arguments[0];
        var sourceLength = plan.SourceUnits.Count;

        var nullDestinationBlockId = nextBlockId++;
        var checkCapacityBlockId = nextBlockId++;
        var checkStorageBlockId = nextBlockId++;
        var storeBlockId = nextBlockId++;
        var failNonnullBlockId = nextBlockId++;
        var doneBlockId = nextBlockId;

        var labelBase = $"{originalBlock.Label}_format_const_{callInstruction.ResultName}";
        var lengthType = StarkTypeSymbols.Integer(64);
        var dataPointerType = StarkTypeSymbols.RawPointer(plan.UnitType, isMutable: true);
        var readonlyDataPointerType = StarkTypeSymbols.RawPointer(plan.UnitType, isMutable: false);
        var dataFieldAddressType = StarkTypeSymbols.RawPointer(dataPointerType, isMutable: true);
        var lengthFieldAddressType = StarkTypeSymbols.RawPointer(lengthType, isMutable: true);
        var unitAddressType = StarkTypeSymbols.RawPointer(plan.UnitType, isMutable: true);

        var destinationIsNullName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_destination_is_null");
        var capacityAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_capacity_addr");
        var capacityName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_capacity");
        var capacityTooSmallName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_capacity_too_small");

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

        var capacityAddressReference = new SsaValueReference(capacityAddressName, lengthFieldAddressType);
        var capacityReference = new SsaValueReference(capacityName, lengthType);

        var checkCapacityBlock = new SsaBasicBlock(
            checkCapacityBlockId,
            $"{labelBase}_check_capacity",
            [],
            [
                new SsaValueInstruction(
                    capacityAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        plan.DestinationStructType,
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

        var checkDataAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_check_data_addr");
        var dataName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_data");
        var dataIsNullName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_data_is_null");
        var checkDataAddressReference = new SsaValueReference(checkDataAddressName, dataFieldAddressType);
        var dataReference = new SsaValueReference(dataName, dataPointerType);
        var checkStorageBlock = new SsaBasicBlock(
            checkStorageBlockId,
            $"{labelBase}_check_storage",
            [],
            [
                new SsaValueInstruction(
                    checkDataAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        plan.DestinationStructType,
                        "Data",
                        0,
                        dataFieldAddressType,
                        $"{destination.Text}.Data"),
                    location),
                new SsaValueInstruction(
                    dataName,
                    new SsaLoadIndirectRValue(
                        checkDataAddressReference,
                        dataPointerType,
                        $"{destination.Text}.Data"),
                    location),
                new SsaValueInstruction(
                    dataIsNullName,
                    new SsaBinaryRValue(
                        SsaBinaryOperator.Equal,
                        dataReference,
                        new SsaNullConstant(dataPointerType),
                        StarkTypeSymbols.Bool,
                        $"{destination.Text}.Data == null"),
                    location)
            ],
            new SsaTerminator(
                SsaTerminatorKind.Branch,
                [failNonnullBlockId, storeBlockId],
                Condition: new SsaValueReference(dataIsNullName, StarkTypeSymbols.Bool),
                Location: originalBlock.Terminator.Location));

        var storeInstructions = new List<SsaInstruction>();
        var storeDataAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_store_data_addr");
        var storeDataName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_store_data");
        var storeLengthAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_store_length_addr");
        var storeDataAddressReference = new SsaValueReference(storeDataAddressName, dataFieldAddressType);
        var storeDataReference = new SsaValueReference(storeDataName, dataPointerType);
        var storeLengthAddressReference = new SsaValueReference(storeLengthAddressName, lengthFieldAddressType);
        storeInstructions.Add(new SsaValueInstruction(
            storeDataAddressName,
            new SsaFieldAddressRValue(
                destination,
                plan.DestinationStructType,
                "Data",
                0,
                dataFieldAddressType,
                $"{destination.Text}.Data"),
            location));
        storeInstructions.Add(new SsaValueInstruction(
            storeDataName,
            new SsaLoadIndirectRValue(
                storeDataAddressReference,
                dataPointerType,
                $"{destination.Text}.Data"),
            location));
        storeInstructions.Add(new SsaValueInstruction(
            storeLengthAddressName,
            new SsaFieldAddressRValue(
                destination,
                plan.DestinationStructType,
                "Length",
                1,
                lengthFieldAddressType,
                $"{destination.Text}.Length"),
            location));
        if (plan.SourceUnits.Count >= CopyRewriteThresholdCodeUnits)
        {
            storeInstructions.Add(new SsaCopyMemoryInstruction(
                storeDataReference,
                new SsaTextDataAddressValue(
                    plan.LiteralText,
                    plan.TextType,
                    readonlyDataPointerType),
                StarkTypeSymbols.FixedArray(plan.UnitType, plan.SourceUnits.Count),
                Location: location));
        }
        else
        {
            for (var index = 0; index < plan.SourceUnits.Count; index++)
            {
                var unitAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_unit");
                var unitAddressReference = new SsaValueReference(unitAddressName, unitAddressType);
                storeInstructions.Add(new SsaValueInstruction(
                    unitAddressName,
                    new SsaElementAddressRValue(
                        storeDataReference,
                        plan.UnitType,
                        Index: null,
                        ConstantIndex: index,
                        unitAddressType,
                        $"{destination.Text}.Data[{index.ToString(CultureInfo.InvariantCulture)}]"),
                    location));
                storeInstructions.Add(new SsaStoreIndirectInstruction(
                    unitAddressReference,
                    plan.UnitType,
                    new SsaIntegerConstant(plan.SourceUnits[index], plan.UnitType),
                    location));
            }
        }

        storeInstructions.Add(new SsaStoreIndirectInstruction(
            storeLengthAddressReference,
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

        var failLengthAddressName = CreateUniqueValueName(usedValueNames, $"{callInstruction.ResultName}_format_const_fail_length_addr");
        var failLengthAddressReference = new SsaValueReference(failLengthAddressName, lengthFieldAddressType);
        var failNonnullBlock = new SsaBasicBlock(
            failNonnullBlockId,
            $"{labelBase}_fail_nonnull",
            [],
            [
                new SsaValueInstruction(
                    failLengthAddressName,
                    new SsaFieldAddressRValue(
                        destination,
                        plan.DestinationStructType,
                        "Length",
                        1,
                        lengthFieldAddressType,
                        $"{destination.Text}.Length"),
                    location),
                new SsaStoreIndirectInstruction(
                    failLengthAddressReference,
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

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out BigInteger constant)
    {
        if (TryResolveIntegerConstantCore(value, valueDefinitions, visitedValueNames, out var resolved))
        {
            constant = resolved.Value;
            return true;
        }

        constant = default;
        return false;
    }

    private static bool TryResolveIntegerConstantCore(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out ResolvedIntegerConstant resolved)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                return TryNormalizeIntegerConstant(integer, out resolved);
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    resolved = default;
                    return false;
                }

                return TryResolveIntegerConstantCore(definition, valueDefinitions, visitedValueNames, out resolved);
            default:
                resolved = default;
                return false;
        }
    }

    private static bool TryResolveIntegerConstantCore(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out ResolvedIntegerConstant resolved)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveIntegerConstantCore(use.Value, valueDefinitions, visitedValueNames, out resolved);
            case SsaConvertRValue { TargetType.Kind: StarkTypeKind.Integer } convert:
                if (!TryResolveIntegerConstantCore(convert.Operand, valueDefinitions, visitedValueNames, out var converted))
                {
                    resolved = default;
                    return false;
                }

                if (TryNormalizeIntegerToType(converted.Value, convert.TargetType, out var convertedValue))
                {
                    resolved = new ResolvedIntegerConstant(convertedValue);
                    return true;
                }

                resolved = default;
                return false;
            case SsaUnaryRValue { Operator: SsaUnaryOperator.Negate } unary
                when TryResolveIntegerConstantCore(unary.Operand, valueDefinitions, visitedValueNames, out var operand):
                return TryResolveCheckedIntegerResult(-operand.Value, unary.Type, out resolved);
            case SsaUnaryRValue { Operator: SsaUnaryOperator.BitwiseNot } unary
                when TryResolveIntegerConstantCore(unary.Operand, valueDefinitions, visitedValueNames, out var operand):
                return TryResolveCheckedIntegerResult(~operand.Value, unary.Type, out resolved);
            case SsaBinaryRValue binary
                when TryResolveIntegerConstantCore(binary.Left, valueDefinitions, visitedValueNames, out var left)
                     && TryResolveIntegerConstantCore(binary.Right, valueDefinitions, visitedValueNames, out var right)
                     && TryEvaluateIntegerBinary(binary, left, right, out var result):
                resolved = result;
                return true;
            default:
                resolved = default;
                return false;
        }
    }

    private static bool TryEvaluateIntegerBinary(
        SsaBinaryRValue binary,
        ResolvedIntegerConstant left,
        ResolvedIntegerConstant right,
        out ResolvedIntegerConstant result)
    {
        result = default;
        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
                return TryResolveCheckedIntegerResult(left.Value + right.Value, binary.Type, out result);
            case SsaBinaryOperator.Subtract:
                return TryResolveCheckedIntegerResult(left.Value - right.Value, binary.Type, out result);
            case SsaBinaryOperator.Multiply:
                return TryResolveCheckedIntegerResult(left.Value * right.Value, binary.Type, out result);
            case SsaBinaryOperator.WrappingAdd:
                return TryResolveWrappingIntegerResult(left.Value + right.Value, binary.Type, out result);
            case SsaBinaryOperator.WrappingSubtract:
                return TryResolveWrappingIntegerResult(left.Value - right.Value, binary.Type, out result);
            case SsaBinaryOperator.WrappingMultiply:
                return TryResolveWrappingIntegerResult(left.Value * right.Value, binary.Type, out result);
            case SsaBinaryOperator.SaturatingAdd:
                return TryResolveSaturatingIntegerResult(left.Value + right.Value, binary.Type, out result);
            case SsaBinaryOperator.SaturatingSubtract:
                return TryResolveSaturatingIntegerResult(left.Value - right.Value, binary.Type, out result);
            case SsaBinaryOperator.SaturatingMultiply:
                return TryResolveSaturatingIntegerResult(left.Value * right.Value, binary.Type, out result);
            case SsaBinaryOperator.Divide when right.Value != BigInteger.Zero:
                return TryResolveCheckedIntegerResult(left.Value / right.Value, binary.Type, out result);
            case SsaBinaryOperator.Modulo when right.Value != BigInteger.Zero:
                return TryResolveCheckedIntegerResult(left.Value % right.Value, binary.Type, out result);
            case SsaBinaryOperator.BitwiseAnd:
                return TryResolveCheckedIntegerResult(left.Value & right.Value, binary.Type, out result);
            case SsaBinaryOperator.BitwiseXor:
                return TryResolveCheckedIntegerResult(left.Value ^ right.Value, binary.Type, out result);
            case SsaBinaryOperator.BitwiseOr:
                return TryResolveCheckedIntegerResult(left.Value | right.Value, binary.Type, out result);
            case SsaBinaryOperator.Exponent when right.Value >= BigInteger.Zero && right.Value <= 1024:
                return TryResolveCheckedIntegerResult(BigInteger.Pow(left.Value, (int)right.Value), binary.Type, out result);
            case SsaBinaryOperator.ShiftLeft
                when TryGetConcreteIntegerBitWidth(binary.Type, out var leftShiftWidth)
                     && TryGetValidShiftAmount(right.Value, leftShiftWidth, out var leftShift):
                return TryResolveCheckedIntegerResult(left.Value << leftShift, binary.Type, out result);
            case SsaBinaryOperator.ShiftRight
                when TryGetConcreteIntegerBitWidth(binary.Type, out var rightShiftWidth)
                     && TryGetValidShiftAmount(right.Value, rightShiftWidth, out var rightShift):
                return TryResolveCheckedIntegerResult(left.Value >> rightShift, binary.Type, out result);
            default:
                return false;
        }
    }

    private static bool TryNormalizeIntegerConstant(SsaIntegerConstant integer, out ResolvedIntegerConstant resolved)
    {
        if (TryFitsEffectiveIntegerRange(integer.Value, integer.Type))
        {
            resolved = new ResolvedIntegerConstant(integer.Value);
            return true;
        }

        resolved = default;
        return false;
    }

    private static bool TryResolveCheckedIntegerResult(
        BigInteger value,
        StarkTypeSymbol type,
        out ResolvedIntegerConstant resolved)
    {
        if (TryFitsEffectiveIntegerRange(value, type))
        {
            resolved = new ResolvedIntegerConstant(value);
            return true;
        }

        resolved = default;
        return false;
    }

    private static bool TryResolveWrappingIntegerResult(
        BigInteger value,
        StarkTypeSymbol type,
        out ResolvedIntegerConstant resolved)
    {
        if (TryWrapIntegerToStorage(value, type, out var wrapped)
            && TryFitsEffectiveIntegerRange(wrapped, type))
        {
            resolved = new ResolvedIntegerConstant(wrapped);
            return true;
        }

        resolved = default;
        return false;
    }

    private static bool TryResolveSaturatingIntegerResult(
        BigInteger value,
        StarkTypeSymbol type,
        out ResolvedIntegerConstant resolved)
    {
        if (!TryGetEffectiveIntegerBounds(type, out var min, out var max))
        {
            resolved = default;
            return false;
        }

        var clamped = value < min
            ? min
            : value > max
                ? max
                : value;
        resolved = new ResolvedIntegerConstant(clamped);
        return true;
    }

    private static bool TryNormalizeIntegerToType(BigInteger value, StarkTypeSymbol type, out BigInteger normalized)
    {
        if (TryFitsEffectiveIntegerRange(value, type))
        {
            normalized = value;
            return true;
        }

        if (!TryWrapIntegerToStorage(value, type, out normalized))
        {
            return false;
        }

        return TryFitsEffectiveIntegerRange(normalized, type);
    }

    private static bool TryFitsEffectiveIntegerRange(BigInteger value, StarkTypeSymbol type)
    {
        return StarkTypeSymbols.IntegerValueFitsEffectiveRange(value, type);
    }

    private static bool TryGetEffectiveIntegerBounds(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out min, out max);
    }

    private static bool TryWrapIntegerToStorage(BigInteger value, StarkTypeSymbol type, out BigInteger wrapped)
    {
        wrapped = value;
        if (!TryGetConcreteIntegerBitWidth(type, out var bitWidth))
        {
            return false;
        }

        var modulus = BigInteger.One << bitWidth;
        var normalized = ((value % modulus) + modulus) % modulus;
        wrapped = type.IsUnsigned
            ? normalized
            : FromTwosComplement(normalized, bitWidth);
        return true;
    }

    private static bool TryGetConcreteIntegerBitWidth(StarkTypeSymbol type, out int bitWidth)
    {
        bitWidth = type.BitWidth ?? 0;
        return type.Kind == StarkTypeKind.Integer && bitWidth > 0;
    }

    private static bool TryGetValidShiftAmount(BigInteger value, int bitWidth, out int shift)
    {
        shift = 0;
        if (value < 0 || value >= bitWidth || value > int.MaxValue)
        {
            return false;
        }

        shift = (int)value;
        return true;
    }

    private static BigInteger FromTwosComplement(BigInteger value, int bitWidth)
    {
        var signBit = BigInteger.One << (bitWidth - 1);
        return (value & signBit) != 0
            ? value - (BigInteger.One << bitWidth)
            : value;
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

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
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

    private sealed record TextFormatRewritePlan(
        StarkTypeSymbol DestinationStructType,
        StarkTypeSymbol TextType,
        StarkTypeSymbol UnitType,
        IReadOnlyList<BigInteger> SourceUnits,
        string LiteralText);

    private readonly record struct ResolvedIntegerConstant(BigInteger Value);
}
