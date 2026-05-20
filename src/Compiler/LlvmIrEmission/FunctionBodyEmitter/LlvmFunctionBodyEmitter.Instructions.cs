using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitInstruction(SsaInstruction instruction)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                EmitValueInstruction(valueInstruction);
                return;
            case SsaCallInstruction call:
                EmitCallInstruction(call);
                return;
            case SsaIndirectCallInstruction call:
                EmitIndirectCallInstruction(call);
                return;
            case SsaAllocateLocalInstruction allocateLocal:
                EmitAllocateLocal(allocateLocal);
                return;
            case SsaLifetimeStartInstruction lifetimeStart:
                EmitLifetimeStart(lifetimeStart);
                return;
            case SsaLifetimeEndInstruction lifetimeEnd:
                EmitLifetimeEnd(lifetimeEnd);
                return;
            case SsaDeallocateLocalInstruction deallocateLocal:
                EmitDeallocateLocal(deallocateLocal);
                return;
            case SsaStoreLocalInstruction storeLocal:
                EmitStoreLocal(storeLocal);
                return;
            case SsaCopyMemoryInstruction copyMemory:
                EmitCopyMemory(copyMemory);
                return;
            case SsaStoreIndirectInstruction storeIndirect:
                EmitStoreIndirect(storeIndirect);
                return;
            case SsaStoreGlobalInstruction storeGlobal:
                EmitStoreGlobal(storeGlobal);
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA instruction '{instruction.GetType().Name}'.");
        }
    }

    private void EmitValueInstruction(SsaValueInstruction instruction)
    {
        if (!_referencedValueNames.Contains(instruction.ResultName)
            && instruction.Value is SsaLoadLocalRValue)
        {
            return;
        }

        if (CanDeferAddressForwardedAggregateValueInstruction(instruction))
        {
            return;
        }

        var result = $"%{EscapeIdentifier(instruction.ResultName)}";
        switch (instruction.Value)
        {
            case SsaUseRValue:
                return;
            case SsaLoadGlobalRValue load:
                AppendLine(
                    $"  {result} = load {MapType(load.Type)}, ptr @{EscapeIdentifier(ResolveGlobalSymbolName(load.GlobalName))}{GetGlobalObjectAlignmentSuffix(load.GlobalName, load.Type)}{GetInvariantLoadMetadataSuffix(load.GlobalName)}{GetValueRangeMetadataSuffix(instruction.ResultName, load.Type)}{GetDirectTbaaMetadataSuffix(CreateTbaaGlobalRootKey(load.GlobalName), load.Type)}");
                return;
            case SsaLoadLocalRValue loadLocal:
                EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                AppendLine($"  {result} = load {MapType(loadLocal.Type)}, ptr {GetLocalSlotPointer(loadLocal.LocalName)}{GetLocalSlotAlignmentSuffix(loadLocal.LocalName, loadLocal.Type)}{GetInvariantLocalLoadMetadataSuffix(loadLocal.LocalName)}{GetValueRangeMetadataSuffix(instruction.ResultName, loadLocal.Type)}{GetDirectTbaaMetadataSuffix(CreateTbaaLocalRootKey(loadLocal.LocalName), loadLocal.Type)}");
                return;
            case SsaConvertRValue convert:
                EmitConvert(instruction.ResultName, result, convert);
                return;
            case SsaExtractFieldRValue extract:
                if (TryEmitAggregateElementLoad(result, extract.Target, extract.FieldIndex, extract.Type, "extract_field_load"))
                {
                    return;
                }

                AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {extract.FieldIndex}");
                return;
            case SsaInsertFieldRValue insert:
                AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatValue(insert.Target)}, {MapType(insert.Value.Type)} {FormatValue(insert.Value)}, {insert.FieldIndex}");
                return;
            case SsaExtractIndexRValue extractIndex:
                if (TryEmitAggregateElementLoad(result, extractIndex.Target, extractIndex.ElementIndex, extractIndex.Type, "extract_index_load"))
                {
                    return;
                }

                AppendLine($"  {result} = extractvalue {MapType(extractIndex.Target.Type)} {FormatValue(extractIndex.Target)}, {extractIndex.ElementIndex}");
                return;
            case SsaInsertIndexRValue insertIndex:
                AppendLine($"  {result} = insertvalue {MapType(insertIndex.Target.Type)} {FormatValue(insertIndex.Target)}, {MapType(insertIndex.Value.Type)} {FormatValue(insertIndex.Value)}, {insertIndex.ElementIndex}");
                return;
            case SsaMakeSliceFromLocalRValue makeSlice:
                EmitMakeSliceFromLocal(result, makeSlice);
                return;
            case SsaMakeSliceFromPointerRValue makeSlice:
                EmitMakeSliceFromPointer(result, makeSlice);
                return;
            case SsaDynamicStorageAllocationRValue allocation:
                EmitDynamicStorageAllocation(result, allocation);
                return;
            case SsaDynamicStorageFreeRValue free:
                EmitDynamicStorageFree(free);
                return;
            case SsaHeapStorageFreeRValue free:
                EmitHeapStorageFree(free);
                return;
            case SsaDynamicStorageReserveRValue reserve:
                EmitDynamicStorageReserve(reserve);
                return;
            case SsaDynamicStorageTryReserveRValue reserve:
                EmitDynamicStorageTryReserve(result, reserve);
                return;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                EmitDynamicStorageTryReserveCapacity(result, reserve);
                return;
            case SsaDynamicStorageMoveLastRValue moveLast:
                EmitDynamicStorageMoveLast(instruction.ResultName, result, moveLast);
                return;
            case SsaDynamicStorageMoveAtRValue moveAt:
                EmitDynamicStorageMoveAt(instruction.ResultName, result, moveAt);
                return;
            case SsaLoadSliceElementRValue loadSlice:
                EmitLoadSliceElement(result, loadSlice, instruction.ScopedNoAliasGroups, instruction.LoopAccessGroups);
                return;
            case SsaTextSliceRValue textSlice:
                EmitTextSlice(result, textSlice);
                return;
            case SsaAddressOfLocalRValue addressOfLocal:
                EmitAddressOfLocal(result, addressOfLocal);
                return;
            case SsaAddressOfParameterRValue addressOfParameter:
                EmitAddressOfParameter(result, addressOfParameter);
                return;
            case SsaFieldAddressRValue fieldAddress:
                EmitFieldAddress(result, fieldAddress);
                return;
            case SsaElementAddressRValue elementAddress:
                EmitElementAddress(result, elementAddress);
                return;
            case SsaSliceElementAddressRValue sliceElementAddress:
                EmitSliceElementAddress(result, sliceElementAddress);
                return;
            case SsaLoadIndirectRValue loadIndirect:
                AppendLine(
                    $"  {result} = load {MapType(loadIndirect.Type)}, ptr {FormatValue(loadIndirect.Address)}{GetKnownPointerAlignmentSuffix(loadIndirect.Address, loadIndirect.Type)}{GetInvariantLoadMetadataSuffix(loadIndirect.Address)}{GetValueRangeMetadataSuffix(instruction.ResultName, loadIndirect.Type)}{GetTbaaMetadataSuffix(loadIndirect.Address, loadIndirect.Type)}{GetScopedNoAliasMetadataSuffix(loadIndirect.Address, instruction.ScopedNoAliasGroups)}{GetLoopAccessGroupMetadataSuffix(instruction.LoopAccessGroups)}");
                return;
            case SsaUnaryRValue unary:
                EmitUnary(result, unary);
                return;
            case SsaBinaryRValue binary:
                EmitBinary(result, binary);
                return;
            case SsaSelectRValue select:
                EmitSelect(result, select);
                return;
            case SsaCallRValue call:
                EmitCall(
                    instruction.ResultName,
                    result,
                    call,
                    instruction.ScopedNoAliasGroups,
                    instruction.LoopAccessGroups);
                return;
            case SsaIndirectCallRValue indirectCall:
                EmitIndirectCall(
                    instruction.ResultName,
                    result,
                    indirectCall,
                    instruction.ScopedNoAliasGroups,
                    instruction.LoopAccessGroups);
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA rvalue '{instruction.Value.GetType().Name}'.");
        }
    }

    private void EmitConvert(string resultName, string result, SsaConvertRValue convert)
    {
        var sourceType = convert.Operand.Type;
        var targetType = convert.TargetType;

        if (IsNoOpConversion(sourceType, targetType))
        {
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
        {
            var opcode = sourceType.BitWidth < targetType.BitWidth
                ? (HasUnsignedIntegerSemantics(sourceType) ? "zext" : "sext")
                : "trunc";
            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
        {
            var opcode = HasUnsignedIntegerSemantics(sourceType) ? "uitofp" : "sitofp";
            if (_isStrictFp)
            {
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedIntegerToFloatIntrinsicName(sourceType, targetType, opcode)}({MapType(sourceType)} {FormatValue(convert.Operand)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
        {
            var opcode = HasUnsignedIntegerSemantics(targetType) ? "fptoui" : "fptosi";
            if (_isStrictFp)
            {
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedFloatToIntegerIntrinsicName(sourceType, targetType, opcode)}({MapType(sourceType)} {FormatValue(convert.Operand)}, metadata !\"fpexcept.strict\") strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
        {
            var opcode = sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc";
            if (_isStrictFp)
            {
                var roundingAndExceptionMetadata = opcode == "fptrunc"
                    ? ", metadata !\"round.dynamic\", metadata !\"fpexcept.strict\""
                    : ", metadata !\"fpexcept.strict\"";
                AppendLine(
                    $"  {result} = call {MapType(targetType)} @{GetConstrainedFloatConversionIntrinsicName(sourceType, targetType)}({MapType(sourceType)} {FormatValue(convert.Operand)}{roundingAndExceptionMetadata}) strictfp");
                return;
            }

            AppendLine($"  {result} = {opcode}{GetFastMathSuffix()} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
        {
            AppendLine($"  {result} = inttoptr {MapType(sourceType)} {FormatValue(convert.Operand)} to ptr");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.Integer)
        {
            AppendLine($"  {result} = ptrtoint ptr {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported SSA conversion from '{sourceType.DisplayName}' to '{targetType.DisplayName}'.");
    }

    private void EmitUnary(string result, SsaUnaryRValue unary)
    {
        switch (unary.Operator)
        {
            case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Integer:
                AppendLine($"  {result} = sub {MapType(unary.Type)} 0, {FormatValue(unary.Operand)}");
                return;
            case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Float:
                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call {MapType(unary.Type)} @{GetConstrainedUnaryIntrinsicName("fneg", unary.Type)}({MapType(unary.Type)} {FormatValue(unary.Operand)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                    return;
                }

                AppendLine($"  {result} = fneg{GetFastMathSuffix()} {MapType(unary.Type)} {FormatValue(unary.Operand)}");
                return;
            case SsaUnaryOperator.LogicalNot:
                AppendLine($"  {result} = xor i1 {FormatValue(unary.Operand)}, true");
                return;
            case SsaUnaryOperator.BitwiseNot:
                AppendLine($"  {result} = xor {MapType(unary.Type)} {FormatValue(unary.Operand)}, -1");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA unary operator '{unary.Operator}'.");
        }
    }

    private void EmitSelect(string result, SsaSelectRValue select)
    {
        AppendLine(
            $"  {result} = select i1 {FormatValue(select.Condition)}, {MapType(select.Type)} {FormatValue(select.WhenTrue)}, {MapType(select.Type)} {FormatValue(select.WhenFalse)}");
    }

    private void EmitBinary(string result, SsaBinaryRValue binary)
    {
        if (binary.Type.Kind == StarkTypeKind.Integer)
        {
            if (binary.Operator is SsaBinaryOperator.SaturatingAdd or SsaBinaryOperator.SaturatingSubtract or SsaBinaryOperator.SaturatingMultiply)
            {
                EmitSaturatingIntegerBinary(result, binary);
                return;
            }

            var opcode = binary.Operator switch
            {
                SsaBinaryOperator.Add => "add",
                SsaBinaryOperator.Subtract => "sub",
                SsaBinaryOperator.Multiply => "mul",
                SsaBinaryOperator.WrappingAdd => "add",
                SsaBinaryOperator.WrappingSubtract => "sub",
                SsaBinaryOperator.WrappingMultiply => "mul",
                SsaBinaryOperator.Divide => CanUseUnsignedIntegerDivisionSemantics(binary) ? "udiv" : "sdiv",
                SsaBinaryOperator.Modulo => CanUseUnsignedIntegerDivisionSemantics(binary) ? "urem" : "srem",
                SsaBinaryOperator.BitwiseAnd => "and",
                SsaBinaryOperator.BitwiseXor => "xor",
                SsaBinaryOperator.BitwiseOr => "or",
                SsaBinaryOperator.ShiftLeft => "shl",
                SsaBinaryOperator.ShiftRight => HasUnsignedIntegerSemantics(binary.Left.Type) ? "lshr" : "ashr",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(opcode))
            {
                AppendLine($"  {result} = {opcode}{GetIntegerInstructionFlags(binary)} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Operator is SsaBinaryOperator.Exponent or SsaBinaryOperator.WrappingExponent)
        {
            if (binary.Type.Kind == StarkTypeKind.Float)
            {
                EmitFloatExponent(result, binary);
                return;
            }

            if (binary.Type.Kind == StarkTypeKind.Integer)
            {
                EmitIntegerExponent(result, binary);
                return;
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported exponent operator type '{binary.Type.DisplayName}'.");
        }

        if (binary.Type.Kind == StarkTypeKind.Float)
        {
            if (TryEmitFusedMultiplyAdd(result, binary))
            {
                return;
            }

            var opcode = binary.Operator switch
            {
                SsaBinaryOperator.Add => "fadd",
                SsaBinaryOperator.Subtract => "fsub",
                SsaBinaryOperator.Multiply => "fmul",
                SsaBinaryOperator.Divide => "fdiv",
                SsaBinaryOperator.Modulo => "frem",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(opcode))
            {
                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call {MapType(binary.Type)} @{GetConstrainedBinaryIntrinsicName(opcode, binary.Type)}({MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {MapType(binary.Right.Type)} {FormatValue(binary.Right)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
                    return;
                }

                AppendLine($"  {result} = {opcode}{GetFastMathSuffix()} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Type.Kind == StarkTypeKind.Bool)
        {
            if (binary.Left.Type.Kind == StarkTypeKind.Bool && binary.Right.Type.Kind == StarkTypeKind.Bool)
            {
                var booleanOpcode = binary.Operator switch
                {
                    SsaBinaryOperator.BitwiseAnd => "and",
                    SsaBinaryOperator.BitwiseOr => "or",
                    SsaBinaryOperator.BitwiseXor => "xor",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(booleanOpcode))
                {
                    AppendLine($"  {result} = {booleanOpcode} i1 {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    SsaBinaryOperator.LessThan => ShouldUseUnsignedIntegerComparison(binary) ? "ult" : "slt",
                    SsaBinaryOperator.LessThanOrEqual => ShouldUseUnsignedIntegerComparison(binary) ? "ule" : "sle",
                    SsaBinaryOperator.GreaterThan => ShouldUseUnsignedIntegerComparison(binary) ? "ugt" : "sgt",
                    SsaBinaryOperator.GreaterThanOrEqual => ShouldUseUnsignedIntegerComparison(binary) ? "uge" : "sge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    AppendLine($"  {result} = icmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.Float)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "oeq",
                    SsaBinaryOperator.NotEqual => "one",
                    SsaBinaryOperator.LessThan => "olt",
                    SsaBinaryOperator.LessThanOrEqual => "ole",
                    SsaBinaryOperator.GreaterThan => "ogt",
                    SsaBinaryOperator.GreaterThanOrEqual => "oge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    if (_isStrictFp)
                    {
                        AppendLine(
                            $"  {result} = call i1 @{GetConstrainedFloatCompareIntrinsicName(binary.Left.Type)}({MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {MapType(binary.Right.Type)} {FormatValue(binary.Right)}, metadata !\"{predicate}\", metadata !\"fpexcept.strict\") strictfp");
                        return;
                    }

                    AppendLine($"  {result} = fcmp{GetFastMathSuffix()} {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Left.Type.Kind == StarkTypeKind.RawPointer)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    SsaBinaryOperator.LessThan => "ult",
                    SsaBinaryOperator.LessThanOrEqual => "ule",
                    SsaBinaryOperator.GreaterThan => "ugt",
                    SsaBinaryOperator.GreaterThanOrEqual => "uge",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(predicate))
                {
                    AppendLine($"  {result} = icmp {predicate} ptr {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (TryEmitTextEquality(result, binary))
            {
                return;
            }

            if (TryEmitTextOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitFixedArrayOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitScalarizedNamedAggregateOrderedComparison(result, binary))
            {
                return;
            }

            if (TryEmitSliceEquality(
                    result,
                    binary.Operator,
                    binary.Left.Type,
                    FormatValue(binary.Left),
                    FormatValue(binary.Right)))
            {
                return;
            }

            if (TryEmitScalarizedAggregateEquality(result, binary))
            {
                return;
            }
        }

        throw new UnsupportedBodyEmissionException(
            $"Unsupported SSA binary operator '{binary.Operator}' for '{binary.Left.Type.DisplayName}'.");
    }

    private bool TryEmitFusedMultiplyAdd(string result, SsaBinaryRValue binary)
    {
        if (_isStrictFp
            || binary.Type.Kind != StarkTypeKind.Float
            || binary.Operator is not (SsaBinaryOperator.Add or SsaBinaryOperator.Subtract))
        {
            return false;
        }

        if (binary.Operator == SsaBinaryOperator.Add)
        {
            if (TryResolveFloatingMultiply(binary.Left, binary.Type, out var leftMultiply))
            {
                EmitFusedMultiplyAdd(
                    result,
                    binary.Type,
                    FormatValue(leftMultiply.Left),
                    FormatValue(leftMultiply.Right),
                    FormatValue(binary.Right));
                return true;
            }

            if (TryResolveFloatingMultiply(binary.Right, binary.Type, out var rightMultiply))
            {
                EmitFusedMultiplyAdd(
                    result,
                    binary.Type,
                    FormatValue(rightMultiply.Left),
                    FormatValue(rightMultiply.Right),
                    FormatValue(binary.Left));
                return true;
            }

            return false;
        }

        if (TryResolveFloatingMultiply(binary.Left, binary.Type, out var minuendMultiply))
        {
            var negatedSubtrahend = EmitFastFloatNegation(binary.Type, binary.Right);
            EmitFusedMultiplyAdd(
                result,
                binary.Type,
                FormatValue(minuendMultiply.Left),
                FormatValue(minuendMultiply.Right),
                negatedSubtrahend);
            return true;
        }

        if (TryResolveFloatingMultiply(binary.Right, binary.Type, out var subtrahendMultiply))
        {
            var negatedFactor = EmitFastFloatNegation(binary.Type, subtrahendMultiply.Left);
            EmitFusedMultiplyAdd(
                result,
                binary.Type,
                negatedFactor,
                FormatValue(subtrahendMultiply.Right),
                FormatValue(binary.Left));
            return true;
        }

        return false;
    }

    private bool TryResolveFloatingMultiply(
        SsaValue value,
        StarkTypeSymbol expectedType,
        out SsaBinaryRValue multiply)
    {
        multiply = null!;

        if (value is not SsaValueReference reference
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Multiply,
                Type.Kind: StarkTypeKind.Float
            } candidate
            || candidate.Type != expectedType)
        {
            return false;
        }

        multiply = candidate;
        return true;
    }

    private void EmitFusedMultiplyAdd(
        string result,
        StarkTypeSymbol type,
        string multiplicand,
        string multiplier,
        string addend)
    {
        var llvmType = MapType(type);
        AppendLine(
            $"  {result} = call{GetFastMathSuffix()} {llvmType} @{GetFusedMultiplyAddIntrinsicName(type)}({llvmType} {multiplicand}, {llvmType} {multiplier}, {llvmType} {addend})");
    }

    private string EmitFastFloatNegation(StarkTypeSymbol type, SsaValue value)
    {
        var result = $"%{EscapeIdentifier(CreateAbiTempName("fmuladd_neg"))}";
        AppendLine($"  {result} = fneg{GetFastMathSuffix()} {MapType(type)} {FormatValue(value)}");
        return result;
    }

    private bool TryEmitScalarizedAggregateEquality(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var rootType = NormalizeAggregateType(binary.Left.Type);
        if (!SupportsScalarizedAggregateEquality(rootType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                rootType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: true,
                out var leaves))
        {
            return false;
        }

        if (leaves.Count == 1)
        {
            return TryEmitScalarizedAggregateLeafComparison(
                result,
                binary.Operator,
                binary.Left,
                binary.Right,
                rootType,
                leaves[0],
                out _);
        }

        string accumulator;
        if (!TryEmitScalarizedAggregateLeafComparison(
                $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                binary.Operator,
                binary.Left,
                binary.Right,
                rootType,
                leaves[0],
                out accumulator))
        {
            return false;
        }

        for (var index = 1; index < leaves.Count; index++)
        {
            if (!TryEmitScalarizedAggregateLeafComparison(
                    $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_leaf"))}",
                    binary.Operator,
                    binary.Left,
                    binary.Right,
                    rootType,
                    leaves[index],
                    out var leafComparison))
            {
                return false;
            }

            var merged = index == leaves.Count - 1
                ? result
                : $"%{EscapeIdentifier(CreateAbiTempName("aggcmp_merge"))}";
            var opcode = binary.Operator == SsaBinaryOperator.Equal ? "and" : "or";
            AppendLine($"  {merged} = {opcode} i1 {accumulator}, {leafComparison}");
            accumulator = merged;
        }

        return true;
    }

    private bool TryEmitTextEquality(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var operandType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || rightType.Kind != operandType.Kind)
        {
            return false;
        }

        return TryEmitTextEqualityHelperCall(
            result,
            binary.Operator,
            operandType,
            FormatValue(binary.Left),
            FormatValue(binary.Right));
    }

    private bool TryEmitTextOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var operandType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || rightType.Kind != operandType.Kind)
        {
            return false;
        }

        return TryEmitTextOrderedComparisonHelperCall(
            result,
            binary.Operator,
            operandType,
            FormatValue(binary.Left),
            FormatValue(binary.Right));
    }

    private bool TryEmitFixedArrayOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var leftType = binary.Left.Type;
        var rightType = binary.Right.Type;
        if (leftType.Kind != StarkTypeKind.FixedArray
            || rightType.Kind != StarkTypeKind.FixedArray
            || leftType.ElementType is null
            || rightType.ElementType is null
            || leftType.FixedLength != rightType.FixedLength)
        {
            return false;
        }

        var helperName = GetFixedArrayOrderedComparisonHelperName(leftType);
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("fixedcmp_root"))}";
        var predicate = binary.Operator switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool TryEmitScalarizedNamedAggregateOrderedComparison(string result, SsaBinaryRValue binary)
    {
        if (binary.Operator is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var leftType = NormalizeAggregateType(binary.Left.Type);
        var rightType = NormalizeAggregateType(binary.Right.Type);
        if (leftType.Kind != StarkTypeKind.Named
            || rightType.Kind != StarkTypeKind.Named
            || leftType.NamedType != rightType.NamedType
            || !SupportsScalarizedAggregateOrderedComparison(leftType))
        {
            return false;
        }

        if (!TryGetScalarizableAggregateLeaves(
                leftType,
                requireRepresentationPreserving: false,
                ignoreScalarizationThresholds: true,
                allowTextLeaves: true,
                allowSliceLeaves: false,
                out _))
        {
            return false;
        }

        var helperName = GetScalarizedAggregateOrderedComparisonHelperName(leftType);
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("namedcmp_root"))}";
        var predicate = binary.Operator switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(leftType)} {FormatValue(binary.Left)}, {MapType(rightType)} {FormatValue(binary.Right)})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool SupportsScalarizedAggregateEquality(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && _context.EnumLayouts.ContainsKey(namedType.Name))),
            _ => false
        };
    }

    private bool SupportsScalarizedAggregateOrderedComparison(StarkTypeSymbol rootType)
    {
        return rootType.Kind switch
        {
            StarkTypeKind.Named => ResolveNamedTypeSymbol(rootType) is { } namedType
                && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                    || (namedType.Kind == DeclarationKind.Enum && _context.EnumLayouts.ContainsKey(namedType.Name))),
            _ => false
        };
    }

    private bool TryEmitScalarizedAggregateLeafComparison(
        string result,
        SsaBinaryOperator operatorKind,
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol rootType,
        AggregateScalarLeaf leaf,
        out string emittedResult)
    {
        var leftValue = EmitScalarizedAggregateLeafValue(left, rootType, leaf.Indices, leaf.Type);
        var rightValue = EmitScalarizedAggregateLeafValue(right, rootType, leaf.Indices, leaf.Type);
        emittedResult = result;
        return TryEmitLeafEqualityComparison(result, operatorKind, leaf.Type, leftValue, rightValue);
    }

    private bool TryEmitLeafEqualityComparison(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        switch (operandType.Kind)
        {
            case StarkTypeKind.Integer:
            case StarkTypeKind.Bool:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                AppendLine($"  {result} = icmp {predicate} {MapType(operandType)} {left}, {right}");
                return true;
            }
            case StarkTypeKind.Float:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "oeq",
                    SsaBinaryOperator.NotEqual => "one",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                if (_isStrictFp)
                {
                    AppendLine(
                        $"  {result} = call i1 @{GetConstrainedFloatCompareIntrinsicName(operandType)}({MapType(operandType)} {left}, {MapType(operandType)} {right}, metadata !\"{predicate}\", metadata !\"fpexcept.strict\") strictfp");
                    return true;
                }

                AppendLine($"  {result} = fcmp{GetFastMathSuffix()} {predicate} {MapType(operandType)} {left}, {right}");
                return true;
            }
            case StarkTypeKind.RawPointer:
            {
                var predicate = operatorKind switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    _ => string.Empty
                };

                if (predicate.Length == 0)
                {
                    return false;
                }

                AppendLine($"  {result} = icmp {predicate} ptr {left}, {right}");
                return true;
            }
            case StarkTypeKind.Ascii:
            case StarkTypeKind.Unicode:
                return TryEmitTextEqualityHelperCall(result, operatorKind, operandType, left, right);
            case StarkTypeKind.Slice:
                return TryEmitSliceEquality(result, operatorKind, operandType, left, right);
            default:
                return false;
        }
    }

    private bool TryEmitTextEqualityHelperCall(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var helperName = operandType.Kind == StarkTypeKind.Ascii
            ? AsciiEqualityHelperName
            : UnicodeEqualityHelperName;
        var equalityResult = operatorKind == SsaBinaryOperator.Equal
            ? result
            : $"%{EscapeIdentifier(CreateAbiTempName("textcmp_eq"))}";

        AppendLine(
            $"  {equalityResult} = call i1 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");

        if (operatorKind == SsaBinaryOperator.NotEqual)
        {
            AppendLine($"  {result} = xor i1 {equalityResult}, true");
        }

        return true;
    }

    private bool TryEmitTextOrderedComparisonHelperCall(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || operatorKind is not (
                SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual))
        {
            return false;
        }

        var helperName = operandType.Kind == StarkTypeKind.Ascii
            ? AsciiCompareHelperName
            : UnicodeCompareHelperName;
        var compareResult = $"%{EscapeIdentifier(CreateAbiTempName("textcmp_order"))}";
        var predicate = operatorKind switch
        {
            SsaBinaryOperator.LessThan => "slt",
            SsaBinaryOperator.LessThanOrEqual => "sle",
            SsaBinaryOperator.GreaterThan => "sgt",
            SsaBinaryOperator.GreaterThanOrEqual => "sge",
            _ => string.Empty
        };

        if (predicate.Length == 0)
        {
            return false;
        }

        AppendLine(
            $"  {compareResult} = call i32 @{EscapeIdentifier(helperName)}({MapType(operandType)} {left}, {MapType(operandType)} {right})");
        AppendLine($"  {result} = icmp {predicate} i32 {compareResult}, 0");
        return true;
    }

    private bool TryEmitSliceEquality(
        string result,
        SsaBinaryOperator operatorKind,
        StarkTypeSymbol operandType,
        string left,
        string right)
    {
        operandType = NormalizeAggregateType(operandType);
        if (operandType.Kind != StarkTypeKind.Slice
            || operatorKind is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        var sliceType = MapType(operandType);
        var predicate = operatorKind == SsaBinaryOperator.Equal ? "eq" : "ne";
        var mergeOpcode = operatorKind == SsaBinaryOperator.Equal ? "and" : "or";
        var leftPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_ptr"))}";
        var rightPointer = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_ptr"))}";
        var leftLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_left_len"))}";
        var rightLength = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_right_len"))}";
        var pointerComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_ptr"))}";
        var lengthComparison = $"%{EscapeIdentifier(CreateAbiTempName("slicecmp_len"))}";

        AppendLine($"  {leftPointer} = extractvalue {sliceType} {left}, 0");
        AppendLine($"  {rightPointer} = extractvalue {sliceType} {right}, 0");
        AppendLine($"  {leftLength} = extractvalue {sliceType} {left}, 1");
        AppendLine($"  {rightLength} = extractvalue {sliceType} {right}, 1");
        AppendLine($"  {pointerComparison} = icmp {predicate} ptr {leftPointer}, {rightPointer}");
        AppendLine($"  {lengthComparison} = icmp {predicate} i64 {leftLength}, {rightLength}");
        AppendLine($"  {result} = {mergeOpcode} i1 {pointerComparison}, {lengthComparison}");
        return true;
    }

    private void EmitSaturatingIntegerBinary(string result, SsaBinaryRValue binary)
    {
        if (binary.Type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            throw new UnsupportedBodyEmissionException($"Saturating integer operator '{binary.Operator}' requires a concrete integer bit width.");
        }

        var narrowType = MapType(binary.Type);
        var wideTypeSymbol = StarkTypeSymbols.Integer(bitWidth * 2);
        var wideType = MapType(wideTypeSymbol);
        var wideOpcode = binary.Operator switch
        {
            SsaBinaryOperator.SaturatingAdd => "add",
            SsaBinaryOperator.SaturatingSubtract => "sub",
            SsaBinaryOperator.SaturatingMultiply => "mul",
            _ => throw new UnsupportedBodyEmissionException($"Unsupported saturating integer operator '{binary.Operator}'.")
        };

        var leftWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_left"))}";
        var rightWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_right"))}";
        var valueWide = $"%{EscapeIdentifier(CreateAbiTempName("sat_value"))}";
        var aboveMax = $"%{EscapeIdentifier(CreateAbiTempName("sat_above"))}";
        var belowMin = $"%{EscapeIdentifier(CreateAbiTempName("sat_below"))}";
        var clampHigh = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamp_high"))}";
        var clamped = $"%{EscapeIdentifier(CreateAbiTempName("sat_clamped"))}";

        GetSignedIntegerBounds(bitWidth, out var minValue, out var maxValue);

        AppendLine($"  {leftWide} = sext {narrowType} {FormatValue(binary.Left)} to {wideType}");
        AppendLine($"  {rightWide} = sext {narrowType} {FormatValue(binary.Right)} to {wideType}");
        AppendLine($"  {valueWide} = {wideOpcode} {wideType} {leftWide}, {rightWide}");
        AppendLine($"  {aboveMax} = icmp sgt {wideType} {valueWide}, {maxValue}");
        AppendLine($"  {belowMin} = icmp slt {wideType} {valueWide}, {minValue}");
        AppendLine($"  {clampHigh} = select i1 {aboveMax}, {wideType} {maxValue}, {wideType} {valueWide}");
        AppendLine($"  {clamped} = select i1 {belowMin}, {wideType} {minValue}, {wideType} {clampHigh}");
        AppendLine($"  {result} = trunc {wideType} {clamped} to {narrowType}");
    }

    private string GetIntegerInstructionFlags(SsaBinaryRValue binary)
    {
        return binary.Operator switch
        {
            SsaBinaryOperator.Add or SsaBinaryOperator.Subtract or SsaBinaryOperator.Multiply
                => GetOrdinaryIntegerArithmeticNoWrapFlags(binary),
            SsaBinaryOperator.ShiftLeft => GetShiftLeftNoWrapFlags(binary),
            SsaBinaryOperator.Divide => CanUseExactSignedDivision(binary) ? " exact" : string.Empty,
            SsaBinaryOperator.ShiftRight => CanUseExactArithmeticShiftRight(binary) ? " exact" : string.Empty,
            _ => string.Empty
        };
    }

    private string GetOrdinaryIntegerArithmeticNoWrapFlags(SsaBinaryRValue binary)
    {
        var canUseUnsignedNoWrap = CanUseUnsignedNoWrapByContract(binary);
        var canUseSignedNoWrap = CanUseSignedNoWrap(binary);
        return (canUseUnsignedNoWrap, canUseSignedNoWrap) switch
        {
            (true, true) => " nuw nsw",
            (true, false) => " nuw",
            (false, true) => " nsw",
            _ => string.Empty
        };
    }

    private bool CanUseUnsignedNoWrapByContract(SsaBinaryRValue binary)
    {
        var type = NormalizeAggregateType(binary.Left.Type);
        return type.Kind == StarkTypeKind.Integer && type.IsUnsigned
            || CanUseUnsignedNoWrap(binary);
    }

    private bool CanUseUnsignedIntegerDivisionSemantics(SsaBinaryRValue binary)
    {
        if (HasUnsignedIntegerSemantics(binary.Type))
        {
            return true;
        }

        return binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo
            && CanProveNonNegativeInteger(binary.Left)
            && CanProveNonNegativeInteger(binary.Right);
    }

    private bool CanProveNonNegativeInteger(SsaValue value)
    {
        return TryGetIntegerValueRange(value, new HashSet<string>(StringComparer.Ordinal), out var min, out _)
                && min >= BigInteger.Zero
            || TryGetKnownZeroSignBit(value);
    }

    private bool TryGetKnownZeroSignBit(SsaValue value)
    {
        var normalizedType = NormalizeAggregateType(value.Type);
        if (normalizedType.Kind != StarkTypeKind.Integer
            || normalizedType.BitWidth is not int bitWidth
            || bitWidth <= 0
            || value is not SsaValueReference reference
            || !_valueFacts.TryGetValue(reference.Name, out var facts)
            || facts.KnownBitsKind != SsaFactLatticeKind.Known
            || facts.KnownBits is not { } knownBits)
        {
            return false;
        }

        var signBit = BigInteger.One << (bitWidth - 1);
        return (knownBits.KnownZeroBits & signBit) != BigInteger.Zero;
    }

    private bool CanUseUnsignedNoWrap(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax)
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var rightMin, out var rightMax))
        {
            return false;
        }

        var domainSize = BigInteger.One << bitWidth;
        if (leftMin < BigInteger.Zero
            || rightMin < BigInteger.Zero
            || leftMax >= domainSize
            || rightMax >= domainSize)
        {
            return false;
        }

        return binary.Operator switch
        {
            SsaBinaryOperator.Add => leftMax + rightMax < domainSize,
            SsaBinaryOperator.Subtract => leftMin >= rightMax,
            SsaBinaryOperator.Multiply => leftMax * rightMax < domainSize,
            _ => false
        };
    }

    private bool CanUseSignedNoWrap(SsaBinaryRValue binary)
    {
        var type = NormalizeAggregateType(binary.Left.Type);
        if (type.Kind != StarkTypeKind.Integer || type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            return false;
        }

        if (!type.IsUnsigned)
        {
            return true;
        }

        if (!TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax)
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var rightMin, out var rightMax))
        {
            return false;
        }

        GetSignedIntegerBounds(bitWidth, out var signedMin, out var signedMax);
        if (leftMin < signedMin || leftMax > signedMax || rightMin < signedMin || rightMax > signedMax)
        {
            return false;
        }

        var resultRange = binary.Operator switch
        {
            SsaBinaryOperator.Add => new SsaIntegerRangeFact(leftMin + rightMin, leftMax + rightMax),
            SsaBinaryOperator.Subtract => new SsaIntegerRangeFact(leftMin - rightMax, leftMax - rightMin),
            SsaBinaryOperator.Multiply => MultiplyRanges(leftMin, leftMax, rightMin, rightMax),
            _ => null
        };

        return resultRange is { } range
            && range.Min >= signedMin
            && range.Max <= signedMax;
    }

    private static SsaIntegerRangeFact MultiplyRanges(
        BigInteger leftMin,
        BigInteger leftMax,
        BigInteger rightMin,
        BigInteger rightMax)
    {
        var candidates = new[]
        {
            leftMin * rightMin,
            leftMin * rightMax,
            leftMax * rightMin,
            leftMax * rightMax
        };
        return new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
    }

    private string GetShiftLeftNoWrapFlags(SsaBinaryRValue binary)
    {
        var canUseUnsignedNoWrap = CanUseUnsignedNoWrapShiftLeft(binary);
        var canUseSignedNoWrap = CanUseSignedNoWrapShiftLeft(binary);
        return (canUseUnsignedNoWrap, canUseSignedNoWrap) switch
        {
            (true, true) => " nuw nsw",
            (true, false) => " nuw",
            (false, true) => " nsw",
            _ => string.Empty
        };
    }

    private bool CanUseUnsignedNoWrapShiftLeft(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetShiftAmountRange(binary, out _, out var maxShift)
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax))
        {
            return false;
        }

        if (maxShift == 0)
        {
            return true;
        }

        if (leftMin < BigInteger.Zero)
        {
            return false;
        }

        var domainSize = BigInteger.One << bitWidth;
        return leftMax * (BigInteger.One << maxShift) < domainSize;
    }

    private bool CanUseSignedNoWrapShiftLeft(SsaBinaryRValue binary)
    {
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetShiftAmountRange(binary, out var minShift, out var maxShift)
            || !TryGetIntegerValueRange(binary.Left, new HashSet<string>(StringComparer.Ordinal), out var leftMin, out var leftMax))
        {
            return false;
        }

        if (maxShift == 0)
        {
            return true;
        }

        GetSignedIntegerBounds(bitWidth, out var signedMin, out var signedMax);
        var minFactor = BigInteger.One << minShift;
        var maxFactor = BigInteger.One << maxShift;
        var minResult = Min(Min(leftMin * minFactor, leftMin * maxFactor), Min(leftMax * minFactor, leftMax * maxFactor));
        var maxResult = Max(Max(leftMin * minFactor, leftMin * maxFactor), Max(leftMax * minFactor, leftMax * maxFactor));
        return minResult >= signedMin && maxResult <= signedMax;
    }

    private bool CanUseExactSignedDivision(SsaBinaryRValue binary)
    {
        return TryGetIntegerSingletonValue(binary.Right, out var divisor)
            && !divisor.IsZero
            && CanProveMultipleOf(binary.Left, BigInteger.Abs(divisor), new HashSet<string>(StringComparer.Ordinal));
    }

    private bool CanUseExactArithmeticShiftRight(SsaBinaryRValue binary)
    {
        return TryGetShiftAmountRange(binary, out _, out var maxShift)
            && CanProveMultipleOfPowerOfTwo(binary.Left, maxShift, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryGetShiftAmountRange(SsaBinaryRValue binary, out int minShift, out int maxShift)
    {
        minShift = default;
        maxShift = default;
        if (binary.Left.Type.BitWidth is not int bitWidth || bitWidth <= 0
            || !TryGetIntegerValueRange(binary.Right, new HashSet<string>(StringComparer.Ordinal), out var minValue, out var maxValue)
            || minValue < BigInteger.Zero
            || maxValue < minValue
            || maxValue >= bitWidth
            || maxValue > int.MaxValue)
        {
            return false;
        }

        minShift = (int)minValue;
        maxShift = (int)maxValue;
        return true;
    }

    private bool CanProveMultipleOfPowerOfTwo(
        SsaValue value,
        int exponent,
        HashSet<string> visitedReferences)
    {
        return exponent <= 0
            || CanProveMultipleOf(value, BigInteger.One << exponent, visitedReferences);
    }

    private bool CanProveMultipleOf(
        SsaValue value,
        BigInteger factor,
        HashSet<string> visitedReferences)
    {
        factor = BigInteger.Abs(factor);
        if (factor <= BigInteger.One)
        {
            return true;
        }

        if (TryGetIntegerSingletonValue(value, out var singleton))
        {
            return IsDivisibleBy(singleton, factor);
        }

        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => CanProveMultipleOf(use.Value, factor, visitedReferences),
            SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert)
                => CanProveMultipleOf(convert.Operand, factor, visitedReferences),
            SsaBinaryRValue binary => CanProveBinaryMultipleOf(binary, factor, visitedReferences),
            _ => false
        };
    }

    private bool CanProveBinaryMultipleOf(
        SsaBinaryRValue binary,
        BigInteger factor,
        HashSet<string> visitedReferences)
    {
        if (binary.Operator == SsaBinaryOperator.ShiftLeft
            && TryGetPowerOfTwoExponent(factor, out var exponent)
            && TryGetShiftAmountRange(binary, out var minShift, out _))
        {
            return minShift >= exponent;
        }

        if (binary.Operator != SsaBinaryOperator.Multiply)
        {
            return false;
        }

        return TryGetIntegerSingletonValue(binary.Left, out var leftConstant) && IsDivisibleBy(leftConstant, factor)
            || TryGetIntegerSingletonValue(binary.Right, out var rightConstant) && IsDivisibleBy(rightConstant, factor)
            || CanProveMultipleOf(binary.Left, factor, CloneVisitedReferences(visitedReferences))
            || CanProveMultipleOf(binary.Right, factor, CloneVisitedReferences(visitedReferences));
    }

    private bool TryGetIntegerSingletonValue(SsaValue value, out BigInteger singleton)
    {
        if (TryGetIntegerValueRange(value, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min == max)
        {
            singleton = min;
            return true;
        }

        singleton = default;
        return false;
    }

    private static bool IsDivisibleBy(BigInteger value, BigInteger factor)
    {
        factor = BigInteger.Abs(factor);
        return factor > BigInteger.Zero && value % factor == BigInteger.Zero;
    }

    private static bool TryGetPowerOfTwoExponent(BigInteger value, out int exponent)
    {
        value = BigInteger.Abs(value);
        exponent = 0;
        if (value <= BigInteger.Zero)
        {
            return false;
        }

        while (!value.IsOne)
        {
            if (!value.IsEven)
            {
                exponent = default;
                return false;
            }

            value >>= 1;
            exponent++;
        }

        return true;
    }

    private static HashSet<string> CloneVisitedReferences(HashSet<string> visitedReferences)
    {
        return new HashSet<string>(visitedReferences, StringComparer.Ordinal);
    }

    private bool TryGetIntegerValueRange(
        SsaValue value,
        HashSet<string> visitedReferences,
        out BigInteger min,
        out BigInteger max)
    {
        if (value is SsaIntegerConstant integer)
        {
            min = integer.Value;
            max = integer.Value;
            return true;
        }

        if (value is SsaValueReference reference
            && TryGetIntegerValueFact(reference.Name, out min, out max))
        {
            return true;
        }

        if (value is SsaValueReference referenceValue
            && visitedReferences.Add(referenceValue.Name)
            && _valueDefinitions.TryGetValue(referenceValue.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return TryGetIntegerValueRange(use.Value, visitedReferences, out min, out max);
                case SsaConvertRValue convert when CanPreserveIntegerRangeThroughConversion(convert):
                    return TryGetIntegerValueRange(convert.Operand, visitedReferences, out min, out max);
            }
        }

        return TryGetIntegerTypeRange(value.Type, out min, out max);
    }

    private bool TryGetIntegerValueFact(string valueName, out BigInteger min, out BigInteger max)
    {
        if (_valueFacts.TryGetValue(valueName, out var facts)
            && facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } range)
        {
            min = range.Min;
            max = range.Max;
            return true;
        }

        min = default;
        max = default;
        return false;
    }

    private static bool CanPreserveIntegerRangeThroughConversion(SsaConvertRValue convert)
    {
        var sourceType = NormalizeAggregateType(convert.Operand.Type);
        var targetType = NormalizeAggregateType(convert.TargetType);
        if (sourceType.Kind != StarkTypeKind.Integer
            || targetType.Kind != StarkTypeKind.Integer
            || sourceType.BitWidth is not int sourceBitWidth
            || targetType.BitWidth is not int targetBitWidth)
        {
            return false;
        }

        if (sourceType.IsUnsigned != targetType.IsUnsigned)
        {
            return false;
        }

        if (sourceBitWidth <= targetBitWidth)
        {
            return true;
        }

        if (!TryGetIntegerTypeRange(sourceType, out var sourceMin, out var sourceMax))
        {
            return false;
        }

        GetSignedIntegerBounds(targetBitWidth, out var targetMin, out var targetMax);
        return sourceMin >= targetMin && sourceMax <= targetMax;
    }

    private static bool TryGetIntegerTypeRange(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        var normalizedType = NormalizeAggregateType(type);
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(normalizedType, out min, out max);
    }

    private static bool HasUnsignedIntegerSemantics(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        if (normalizedType.Kind != StarkTypeKind.Integer
            || normalizedType.BitWidth is not int bitWidth
            || !TryGetIntegerTypeRange(normalizedType, out var min, out var max))
        {
            return false;
        }

        if (normalizedType.IsUnsigned)
        {
            return true;
        }

        GetSignedIntegerBounds(bitWidth, out _, out var signedMax);
        return min >= BigInteger.Zero && max > signedMax;
    }

    private static bool ShouldUseUnsignedIntegerComparison(StarkTypeSymbol type)
        => type.Kind == StarkTypeKind.Bool || HasUnsignedIntegerSemantics(type);

    private bool ShouldUseUnsignedIntegerComparison(SsaBinaryRValue binary)
    {
        var isOrderedComparison = binary.Operator is SsaBinaryOperator.LessThan
            or SsaBinaryOperator.LessThanOrEqual
            or SsaBinaryOperator.GreaterThan
            or SsaBinaryOperator.GreaterThanOrEqual;
        return ShouldUseUnsignedIntegerComparison(binary.Left.Type)
            || isOrderedComparison
                && CanProveNonNegativeInteger(binary.Left)
                && CanProveNonNegativeInteger(binary.Right);
    }

    private string GetFixedArrayIndexGepFlags(SsaValue? index, StarkTypeSymbol aggregateType)
    {
        if (index is null || aggregateType.FixedLength is not int fixedLength)
        {
            return string.Empty;
        }

        if (IsKnownZeroIndex(index))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < fixedLength
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private string GetUnboundedPointerIndexGepFlags(SsaValue pointer, SsaValue index)
    {
        if (IsKnownZeroIndex(index))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetBoundedRawPointerElementCountLowerBound(
            pointer,
            new HashSet<string>(StringComparer.Ordinal),
            out var elementCountLowerBound)
            && IsIndexRangeWithinExclusiveBound(index, elementCountLowerBound)
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private string GetSliceElementGepFlags(SsaValue slice, SsaValue index)
    {
        if (IsKnownZeroIndex(index))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetKnownSliceElementCountLowerBound(slice, new HashSet<string>(StringComparer.Ordinal), out var elementCountLowerBound)
            && IsIndexRangeWithinExclusiveBound(index, elementCountLowerBound)
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private string GetTextSliceGepFlags(SsaValue textValue, SsaValue start)
    {
        if (IsKnownZeroIndex(start))
        {
            return GetZeroOffsetGepFlags();
        }

        return TryGetKnownTextUnitCountLowerBound(textValue, new HashSet<string>(StringComparer.Ordinal), out var unitCountLowerBound)
            && IsIndexRangeWithinInclusiveBound(start, unitCountLowerBound)
                ? GetProvenInObjectGepFlags()
                : string.Empty;
    }

    private bool IsKnownZeroIndex(SsaValue index)
    {
        return TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min.IsZero
            && max.IsZero;
    }

    private bool IsIndexRangeWithinExclusiveBound(SsaValue index, BigInteger exclusiveBound)
    {
        return exclusiveBound > BigInteger.Zero
            && TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < exclusiveBound;
    }

    private bool IsIndexRangeWithinInclusiveBound(SsaValue index, BigInteger inclusiveBound)
    {
        return inclusiveBound >= BigInteger.Zero
            && TryGetIntegerValueRange(index, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max <= inclusiveBound;
    }

    private bool TryGetKnownSliceElementCountLowerBound(
        SsaValue slice,
        HashSet<string> visitedReferences,
        out BigInteger elementCountLowerBound)
    {
        if (slice is SsaValueReference reference
            && visitedReferences.Add(reference.Name))
        {
            if (_valueFacts.TryGetValue(reference.Name, out var facts)
                && facts.LengthKind == SsaFactLatticeKind.Known
                && facts.LengthRange is { } lengthRange
                && lengthRange.Min >= BigInteger.Zero)
            {
                elementCountLowerBound = lengthRange.Min;
                return true;
            }

            if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                switch (definition)
                {
                    case SsaUseRValue use:
                        return TryGetKnownSliceElementCountLowerBound(use.Value, visitedReferences, out elementCountLowerBound);
                    case SsaLoadLocalRValue loadLocal:
                        return TryGetStoredLocalSliceElementCountLowerBound(
                            loadLocal.LocalName,
                            visitedReferences,
                            out elementCountLowerBound);
                    case SsaMakeSliceFromLocalRValue makeSlice when makeSlice.SourceType.FixedLength is int fixedLength:
                        elementCountLowerBound = fixedLength;
                        return true;
                    case SsaMakeSliceFromPointerRValue makeSlice:
                        return TryResolveIntegerConstant(
                            makeSlice.Length,
                            _valueDefinitions,
                            new HashSet<string>(StringComparer.Ordinal),
                            out elementCountLowerBound);
                }
            }
        }

        elementCountLowerBound = default;
        return false;
    }

    private bool TryGetBoundedRawPointerElementCountLowerBound(
        SsaValue pointer,
        HashSet<string> visitedReferences,
        out BigInteger elementCountLowerBound)
    {
        if (TryGetBoundedRawPointerRegionFact(pointer, visitedReferences, out var boundedRegion)
            && boundedRegion.ElementCountRange is { } elementCountRange
            && elementCountRange.Min >= BigInteger.Zero)
        {
            elementCountLowerBound = elementCountRange.Min;
            return true;
        }

        elementCountLowerBound = default;
        return false;
    }

    private bool TryGetBoundedRawPointerRegionFact(
        SsaValue value,
        ISet<string> visitedReferences,
        out SsaBoundedRawPointerRegionFact boundedRegion)
    {
        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedReferences.Add(reference.Name))
                {
                    boundedRegion = default!;
                    return false;
                }

                if (_valueFacts.TryGetValue(reference.Name, out var facts)
                    && facts.BoundedRawPointerRegionKind == SsaFactLatticeKind.Known
                    && facts.BoundedRawPointerRegion is { } knownRegion)
                {
                    boundedRegion = knownRegion;
                    return true;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryGetBoundedRawPointerRegionFact(definition, visitedReferences, out boundedRegion);
                }

                break;
        }

        boundedRegion = default!;
        return false;
    }

    private bool TryGetBoundedRawPointerRegionFact(
        SsaRValue value,
        ISet<string> visitedReferences,
        out SsaBoundedRawPointerRegionFact boundedRegion)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryGetBoundedRawPointerRegionFact(use.Value, visitedReferences, out boundedRegion);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryGetBoundedRawPointerRegionFact(convert.Operand, visitedReferences, out boundedRegion);
            case SsaMakeSliceFromPointerRValue makeSlice:
            {
                SsaIntegerRangeFact? lengthRange = null;
                if (makeSlice.Length is SsaValueReference lengthReference
                    && _valueFacts.TryGetValue(lengthReference.Name, out var lengthFacts)
                    && lengthFacts.IntegerRangeKind == SsaFactLatticeKind.Known)
                {
                    lengthRange = lengthFacts.IntegerRange;
                }
                else if (makeSlice.Length is SsaIntegerConstant lengthConstant)
                {
                    lengthRange = new SsaIntegerRangeFact(lengthConstant.Value, lengthConstant.Value);
                }

                var alignmentBytes = TryGetBoundedRawPointerRegionFact(
                        makeSlice.Pointer,
                        visitedReferences,
                        out var pointerRegion)
                    ? pointerRegion.ElementAlignmentBytes
                    : null;
                boundedRegion = new SsaBoundedRawPointerRegionFact(
                    makeSlice.Length,
                    lengthRange,
                    alignmentBytes);
                return true;
            }
            case SsaLoadLocalRValue loadLocal
                when TryResolveSingleStoreLocalValue(loadLocal.LocalName, out var storedValue):
                return TryGetBoundedRawPointerRegionFact(storedValue, visitedReferences, out boundedRegion);
            default:
                boundedRegion = default!;
                return false;
        }
    }

    private bool TryGetStoredLocalSliceElementCountLowerBound(
        string localName,
        HashSet<string> visitedReferences,
        out BigInteger elementCountLowerBound)
    {
        if (!visitedReferences.Add($"local:{localName}"))
        {
            elementCountLowerBound = default;
            return false;
        }

        var sawStore = false;
        var lowerBound = BigInteger.Zero;
        foreach (var store in _ssaFunction.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaStoreLocalInstruction>()
                     .Where(store => string.Equals(store.LocalName, localName, StringComparison.Ordinal)))
        {
            var storeVisited = new HashSet<string>(visitedReferences, StringComparer.Ordinal);
            if (!TryGetKnownSliceElementCountLowerBound(store.Value, storeVisited, out var storedLowerBound))
            {
                elementCountLowerBound = default;
                return false;
            }

            lowerBound = sawStore ? Min(lowerBound, storedLowerBound) : storedLowerBound;
            sawStore = true;
        }

        elementCountLowerBound = lowerBound;
        return sawStore;
    }

    private bool TryGetKnownTextUnitCountLowerBound(
        SsaValue textValue,
        HashSet<string> visitedReferences,
        out BigInteger unitCountLowerBound)
    {
        switch (textValue)
        {
            case SsaStringConstant text:
                unitCountLowerBound = ResolveStringConstant(text.LiteralText, text.Type).DataLength;
                return true;
            case SsaValueReference reference
                when visitedReferences.Add(reference.Name):
                if (_valueFacts.TryGetValue(reference.Name, out var facts)
                    && facts.LengthKind == SsaFactLatticeKind.Known
                    && facts.LengthRange is { } lengthRange
                    && lengthRange.Min >= BigInteger.Zero)
                {
                    unitCountLowerBound = lengthRange.Min;
                    return true;
                }

                if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    switch (definition)
                    {
                        case SsaUseRValue use:
                            return TryGetKnownTextUnitCountLowerBound(use.Value, visitedReferences, out unitCountLowerBound);
                        case SsaTextSliceRValue textSlice
                            when TryGetIntegerValueRange(textSlice.Length, new HashSet<string>(StringComparer.Ordinal), out var min, out _)
                                 && min >= BigInteger.Zero:
                            unitCountLowerBound = min;
                            return true;
                    }
                }

                break;
        }

        unitCountLowerBound = default;
        return false;
    }

    private static string GetZeroOffsetGepFlags() => " inbounds nuw";

    // LLVM's `inbounds` GEP contract includes the signed no-wrap (`nusw`) facts; `nuw`
    // is added only where Stark range/object facts prove unsigned address arithmetic too.
    private static string GetProvenInObjectGepFlags() => " inbounds nuw";

    private static BigInteger Min(BigInteger left, BigInteger right) => left <= right ? left : right;

    private static BigInteger Max(BigInteger left, BigInteger right) => left >= right ? left : right;

    private void EmitFloatExponent(string result, SsaBinaryRValue binary)
    {
        var llvmType = MapType(binary.Left.Type);
        if (_isStrictFp)
        {
            AppendLine(
                $"  {result} = call {llvmType} @{GetConstrainedBinaryIntrinsicName("pow", binary.Left.Type)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)}, metadata !\"round.dynamic\", metadata !\"fpexcept.strict\") strictfp");
            return;
        }

        var intrinsicName = $"@llvm.pow.{GetFloatIntrinsicSuffix(binary.Left.Type)}";
        AppendLine($"  {result} = call{GetFastMathSuffix()} {llvmType} {intrinsicName}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private void EmitIntegerExponent(string result, SsaBinaryRValue binary)
    {
        var bitWidth = binary.Type.BitWidth ?? throw new UnsupportedBodyEmissionException(
            $"Integer exponent operator '{binary.Type.DisplayName}' is missing a bit width.");
        var llvmType = MapType(binary.Type);
        if (binary.Right is SsaIntegerConstant exponent
            && exponent.Value >= BigInteger.Zero
            && exponent.Value <= new BigInteger(8))
        {
            EmitSmallConstantIntegerExponent(result, binary, llvmType, (int)exponent.Value);
            return;
        }

        var helperName = GetIntegerExponentHelperName(bitWidth);
        AppendLine(
            $"  {result} = call {llvmType} @{EscapeIdentifier(helperName)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private void EmitSmallConstantIntegerExponent(
        string result,
        SsaBinaryRValue binary,
        string llvmType,
        int exponent)
    {
        if (exponent == 0)
        {
            AppendLine($"  {result} = add {llvmType} 0, 1");
            return;
        }

        var baseValue = FormatValue(binary.Left);
        if (exponent == 1)
        {
            AppendLine($"  {result} = add {llvmType} 0, {baseValue}");
            return;
        }

        var accumulator = baseValue;
        for (var factorIndex = 2; factorIndex <= exponent; factorIndex++)
        {
            var target = factorIndex == exponent
                ? result
                : $"%{EscapeIdentifier(CreateAbiTempName("int_pow"))}";
            AppendLine($"  {target} = mul {llvmType} {accumulator}, {baseValue}");
            accumulator = target;
        }
    }

    private static void GetSignedIntegerBounds(int bitWidth, out BigInteger minValue, out BigInteger maxValue)
    {
        minValue = -(BigInteger.One << (bitWidth - 1));
        maxValue = (BigInteger.One << (bitWidth - 1)) - 1;
    }
}
