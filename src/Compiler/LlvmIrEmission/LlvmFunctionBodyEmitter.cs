using System.Numerics;
using System.Globalization;
using System.Text;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmFunctionBodyEmitter
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
    private const int AggregateMemcpyThresholdBytes = 32;

    private readonly StringBuilder _builder;
    private readonly TypedFunctionSignature _function;
    private readonly AbiFunctionSignature _abiFunction;
    private readonly Func<string, string, AbiFunctionSignature?> _resolveCallAbi;
    private readonly SsaFunction _ssaFunction;
    private readonly LlvmEmissionContext _context;
    private readonly DebugFunctionContext? _debugFunction;
    private readonly HashSet<string> _referencedValueNames;
    private readonly IReadOnlyDictionary<string, SsaRValue> _valueDefinitions;
    private readonly HashSet<string> _allocatedLocalSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _localStorageClasses;
    private readonly Dictionary<string, bool> _aggregateValueMaterializationRequirements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _indirectAggregateValueSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _materializedParameters = new(StringComparer.Ordinal);
    private SourceLocation? _currentDebugLocation;
    private int _nextAbiTempId;

    public LlvmFunctionBodyEmitter(
        StringBuilder builder,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        SsaFunction ssaFunction,
        LlvmEmissionContext context,
        DebugFunctionContext? debugFunction)
    {
        _builder = builder;
        _function = function;
        _abiFunction = abiFunction;
        _resolveCallAbi = resolveCallAbi;
        _ssaFunction = ssaFunction;
        _context = context;
        _debugFunction = debugFunction;
        _referencedValueNames = CollectReferencedValueNames(ssaFunction);
        _valueDefinitions = CollectValueDefinitions(ssaFunction);
        _localStorageClasses = CollectLocalStorageClasses(ssaFunction);
    }

    public void Emit()
    {
        if (_ssaFunction.Blocks.Count == 0)
        {
            _currentDebugLocation = _ssaFunction.Location;
            EmitFallbackTerminal();
            return;
        }

        foreach (var block in _ssaFunction.Blocks)
        {
            AppendLine($"{FormatBlockLabel(block.Id)}:");

            if (block.Id == _ssaFunction.EntryBlockId)
            {
                _currentDebugLocation = _ssaFunction.Location;
                EmitEntryParameterMaterialization();
                EmitEntryParameterSlots();
                EmitEntryParameterDebugInfo();
            }

            foreach (var phi in block.Phis)
            {
                _currentDebugLocation = phi.Location ?? _ssaFunction.Location;
                EmitPhi(phi);
            }

            foreach (var instruction in block.Instructions)
            {
                _currentDebugLocation = GetInstructionLocation(instruction) ?? _ssaFunction.Location;
                EmitInstruction(instruction);
            }

            _currentDebugLocation = block.Terminator.Location ?? _ssaFunction.Location;
            EmitTerminator(block.Terminator);
            AppendLine(string.Empty);
        }
    }

    private void EmitPhi(SsaPhi phi)
    {
        var incoming = string.Join(
            ", ",
            phi.Incomings.Select(entry => $"[ {FormatValue(entry.Value)}, %{FormatBlockLabel(entry.PredecessorBlockId)} ]"));
        AppendLine($"  %{EscapeIdentifier(phi.ResultName)} = phi {MapType(phi.Type)} {incoming}");
    }

    private void EmitInstruction(SsaInstruction instruction)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                EmitValueInstruction(valueInstruction);
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
                AppendLine(
                    $"  store {MapType(storeGlobal.GlobalType)} {FormatValue(storeGlobal.Value)}, ptr @{EscapeIdentifier(ResolveGlobalSymbolName(storeGlobal.GlobalName))}");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA instruction '{instruction.GetType().Name}'.");
        }
    }

    private void EmitValueInstruction(SsaValueInstruction instruction)
    {
        var result = $"%{EscapeIdentifier(instruction.ResultName)}";
        switch (instruction.Value)
        {
            case SsaUseRValue use:
                AppendLine($"  {result} = add {MapType(use.Type)} {FormatValue(use.Value)}, 0");
                return;
            case SsaLoadGlobalRValue load:
                AppendLine(
                    $"  {result} = load {MapType(load.Type)}, ptr @{EscapeIdentifier(ResolveGlobalSymbolName(load.GlobalName))}{GetInvariantLoadMetadataSuffix(load.GlobalName)}");
                return;
            case SsaLoadLocalRValue loadLocal:
                EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                AppendLine($"  {result} = load {MapType(loadLocal.Type)}, ptr %{EscapeIdentifier($"slot_{loadLocal.LocalName}")}");
                return;
            case SsaConvertRValue convert:
                EmitConvert(result, convert);
                return;
            case SsaExtractFieldRValue extract:
                AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {extract.FieldIndex}");
                return;
            case SsaInsertFieldRValue insert:
                AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatValue(insert.Target)}, {MapType(insert.Value.Type)} {FormatValue(insert.Value)}, {insert.FieldIndex}");
                return;
            case SsaExtractIndexRValue extractIndex:
                AppendLine($"  {result} = extractvalue {MapType(extractIndex.Target.Type)} {FormatValue(extractIndex.Target)}, {extractIndex.ElementIndex}");
                return;
            case SsaInsertIndexRValue insertIndex:
                AppendLine($"  {result} = insertvalue {MapType(insertIndex.Target.Type)} {FormatValue(insertIndex.Target)}, {MapType(insertIndex.Value.Type)} {FormatValue(insertIndex.Value)}, {insertIndex.ElementIndex}");
                return;
            case SsaMakeSliceFromLocalRValue makeSlice:
                EmitMakeSliceFromLocal(result, makeSlice);
                return;
            case SsaLoadSliceElementRValue loadSlice:
                EmitLoadSliceElement(result, loadSlice);
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
                    $"  {result} = load {MapType(loadIndirect.Type)}, ptr {FormatValue(loadIndirect.Address)}{GetInvariantLoadMetadataSuffix(loadIndirect.Address)}");
                return;
            case SsaUnaryRValue unary:
                EmitUnary(result, unary);
                return;
            case SsaBinaryRValue binary:
                EmitBinary(result, binary);
                return;
            case SsaCallRValue call:
                EmitCall(instruction.ResultName, result, call);
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA rvalue '{instruction.Value.GetType().Name}'.");
        }
    }

    private void EmitConvert(string result, SsaConvertRValue convert)
    {
        var sourceType = convert.Operand.Type;
        var targetType = convert.TargetType;

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
        {
            if (sourceType.BitWidth == targetType.BitWidth)
            {
                AppendLine($"  {result} = add {MapType(targetType)} {FormatValue(convert.Operand)}, 0");
                return;
            }

            var opcode = sourceType.BitWidth < targetType.BitWidth ? "sext" : "trunc";
            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
        {
            AppendLine($"  {result} = sitofp {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Integer)
        {
            AppendLine($"  {result} = fptosi {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
        {
            if (sourceType.BitWidth == targetType.BitWidth)
            {
                AppendLine($"  {result} = fadd {MapType(targetType)} {FormatValue(convert.Operand)}, 0.0");
                return;
            }

            var opcode = sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc";
            AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.RawPointer)
        {
            AppendLine($"  {result} = inttoptr {MapType(sourceType)} {FormatValue(convert.Operand)} to ptr");
            return;
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer && targetType.Kind == StarkTypeKind.RawPointer)
        {
            AppendLine($"  {result} = getelementptr inbounds i8, ptr {FormatValue(convert.Operand)}, i64 0");
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
                AppendLine($"  {result} = fneg {MapType(unary.Type)} {FormatValue(unary.Operand)}");
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
                SsaBinaryOperator.Divide => "sdiv",
                SsaBinaryOperator.Modulo => "srem",
                SsaBinaryOperator.BitwiseAnd => "and",
                SsaBinaryOperator.BitwiseXor => "xor",
                SsaBinaryOperator.BitwiseOr => "or",
                SsaBinaryOperator.ShiftLeft => "shl",
                SsaBinaryOperator.ShiftRight => "ashr",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(opcode))
            {
                AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Operator == SsaBinaryOperator.Exponent)
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
                AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                return;
            }
        }

        if (binary.Type.Kind == StarkTypeKind.Bool)
        {
            if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
            {
                var predicate = binary.Operator switch
                {
                    SsaBinaryOperator.Equal => "eq",
                    SsaBinaryOperator.NotEqual => "ne",
                    SsaBinaryOperator.LessThan => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ult" : "slt",
                    SsaBinaryOperator.LessThanOrEqual => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ule" : "sle",
                    SsaBinaryOperator.GreaterThan => binary.Left.Type.Kind == StarkTypeKind.Bool ? "ugt" : "sgt",
                    SsaBinaryOperator.GreaterThanOrEqual => binary.Left.Type.Kind == StarkTypeKind.Bool ? "uge" : "sge",
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
                    AppendLine($"  {result} = fcmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
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

                AppendLine($"  {result} = fcmp {predicate} {MapType(operandType)} {left}, {right}");
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

    private void EmitFloatExponent(string result, SsaBinaryRValue binary)
    {
        var llvmType = MapType(binary.Left.Type);
        var intrinsicName = $"@llvm.pow.{GetFloatIntrinsicSuffix(binary.Left.Type)}";
        AppendLine($"  {result} = call {llvmType} {intrinsicName}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private void EmitIntegerExponent(string result, SsaBinaryRValue binary)
    {
        var bitWidth = binary.Type.BitWidth ?? throw new UnsupportedBodyEmissionException(
            $"Integer exponent operator '{binary.Type.DisplayName}' is missing a bit width.");
        var llvmType = MapType(binary.Type);
        var helperName = GetIntegerExponentHelperName(bitWidth);
        AppendLine(
            $"  {result} = call {llvmType} @{EscapeIdentifier(helperName)}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
    }

    private static void GetSignedIntegerBounds(int bitWidth, out BigInteger minValue, out BigInteger maxValue)
    {
        minValue = -(BigInteger.One << (bitWidth - 1));
        maxValue = (BigInteger.One << (bitWidth - 1)) - 1;
    }

    private void EmitCall(string resultName, string result, SsaCallRValue call)
    {
        var abiCallee = _resolveCallAbi(_function.Name, call.FunctionName);
        if (abiCallee is null)
        {
            throw new UnsupportedBodyEmissionException($"Missing ABI lowering for call target '{call.FunctionName}'.");
        }

        if (IsStringType(call.Type) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
        {
            throw new UnsupportedBodyEmissionException(
                $"FFI string returns are not yet supported for '{call.FunctionName}'.");
        }

        var arguments = new List<string>();
        string? indirectReturnSlot = null;

        if (abiCallee.ReturnsIndirect)
        {
            indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("callret_slot"))}";
            AppendLine($"  {indirectReturnSlot} = alloca {MapType(call.Type)}");
            arguments.Add(RenderSRetArgumentPointer(call.Type, indirectReturnSlot));
        }

        var userParameters = abiCallee.UserParameters;
        if (userParameters.Count != call.Arguments.Count)
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
                arguments.Add(RenderDirectArgument(parameter, argument));
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
                        arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier(promotedParameter.LlvmName)}"));
                    }
                    else
                    {
                        EnsureParameterSlotExists(promotedParameter, promotedParameter.SourceType);
                        arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_param_{promotedParameter.SourceName}")}"));
                    }

                    continue;
                }

                EnsureLocalSlotExists(promotedLocal!, parameter.SourceType);
                arguments.Add(RenderIndirectArgumentPointer(parameter, $"%{EscapeIdentifier($"slot_{promotedLocal}")}"));
                continue;
            }

            if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter)
                && TryResolveAggregateSourceAddress(argument, parameter.SourceType, out var forwardedSourceAddress))
            {
                arguments.Add(RenderIndirectArgumentPointer(parameter, forwardedSourceAddress));
                continue;
            }

            var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
            AppendLine($"  {tempSlot} = alloca {MapType(parameter.SourceType)}");
            EmitValueToAddress(tempSlot, parameter.SourceType, argument);

            arguments.Add(RenderIndirectArgumentPointer(parameter, tempSlot));
        }

        var renderedArguments = string.Join(", ", arguments);
        var callPrefix = abiCallee.UsesFastCallingConvention ? "call fastcc" : "call";

        if (abiCallee.ReturnsIndirect)
        {
            AppendLine($"  {callPrefix} void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
            _indirectAggregateValueSlots[resultName] = indirectReturnSlot!;
            if (RequiresAggregateValueMaterialization(resultName, call.Type))
            {
                AppendLine($"  {result} = load {MapType(call.Type)}, ptr {indirectReturnSlot}");
            }
            return;
        }

        if (call.Type.Kind == StarkTypeKind.Void)
        {
            AppendLine($"  {callPrefix} void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
            return;
        }

        AppendLine($"  {result} = {callPrefix} {MapType(abiCallee.LlvmReturnType)} @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
    }

    private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
    {
        EnsureLocalSlotExists(allocateLocal.LocalName, allocateLocal.LocalType);
        EmitLocalDebugDeclare(
            $"%{EscapeIdentifier($"slot_{allocateLocal.LocalName}")}",
            allocateLocal.LocalName,
            allocateLocal.LocalType,
            allocateLocal.Location);
    }

    private void EmitLifetimeStart(SsaLifetimeStartInstruction lifetimeStart)
    {
        EmitLifetimeMarker("start", lifetimeStart.LocalName, lifetimeStart.LocalType);
    }

    private void EmitLifetimeEnd(SsaLifetimeEndInstruction lifetimeEnd)
    {
        EmitLifetimeMarker("end", lifetimeEnd.LocalName, lifetimeEnd.LocalType);
    }

    private void EmitDeallocateLocal(SsaDeallocateLocalInstruction deallocateLocal)
    {
        if (deallocateLocal.StorageClass != "heap")
        {
            throw new UnsupportedBodyEmissionException(
                $"Local storage class '{deallocateLocal.StorageClass}' is not yet supported for LLVM deallocation.");
        }

        var slotName = $"%{EscapeIdentifier($"slot_{deallocateLocal.LocalName}")}";
        AppendLine($"  call void @free(ptr {slotName})");
    }

    private void EmitLifetimeMarker(string phase, string localName, StarkTypeSymbol localType)
    {
        if (TryGetConcreteTypeLayout(localType) is not { } layout)
        {
            return;
        }

        EnsureLocalSlotExists(localName, localType);
        AppendLine($"  call void @llvm.lifetime.{phase}.p0(i64 {layout.SizeBytes}, ptr %{EscapeIdentifier($"slot_{localName}")})");
    }

    private void EmitStoreLocal(SsaStoreLocalInstruction storeLocal)
    {
        EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
        var slot = $"%{EscapeIdentifier($"slot_{storeLocal.LocalName}")}";
        EmitValueToAddress(slot, storeLocal.LocalType, storeLocal.Value);
    }

    private void EmitCopyMemory(SsaCopyMemoryInstruction copyMemory)
    {
        if (TryEmitScalarizedAggregateCopy(copyMemory.DestinationAddress, copyMemory.SourceAddress, copyMemory.CopyType))
        {
            return;
        }

        if (TryGetConcreteTypeLayout(copyMemory.CopyType) is { } layout
            && layout.SizeBytes > AggregateMemcpyThresholdBytes)
        {
            AppendLine(
                $"  call void @llvm.memcpy.inline.p0.p0.i64(ptr {FormatValue(copyMemory.DestinationAddress)}, ptr {FormatValue(copyMemory.SourceAddress)}, i64 {layout.SizeBytes}, i1 false)");
            return;
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
        AppendLine(
            $"  {loadedValue} = load {MapType(copyMemory.CopyType)}, ptr {FormatValue(copyMemory.SourceAddress)}{GetInvariantLoadMetadataSuffix(copyMemory.SourceAddress)}");
        AppendLine($"  store {MapType(copyMemory.CopyType)} {loadedValue}, ptr {FormatValue(copyMemory.DestinationAddress)}");
    }

    private void EmitStoreIndirect(SsaStoreIndirectInstruction storeIndirect)
    {
        EmitValueToAddress(FormatValue(storeIndirect.Address), storeIndirect.ValueType, storeIndirect.Value);
    }

    private void EmitValueToAddress(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        if (TryEmitInlineAggregateZeroFill(destinationAddress, valueType, value))
        {
            return;
        }

        if (ShouldPreferAddressBasedAggregateLowering(valueType))
        {
            if (TryEmitAggregateAddressCopy(destinationAddress, valueType, value))
            {
                return;
            }

            if (TryEmitStructuredAggregateStore(destinationAddress, valueType, value))
            {
                return;
            }
        }

        if (TryEmitScalarizedAggregateStore(destinationAddress, valueType, value))
        {
            return;
        }

        AppendLine($"  store {MapType(valueType)} {FormatValue(value)}, ptr {destinationAddress}");
    }

    private bool TryEmitInlineAggregateZeroFill(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        if (value is not SsaZeroInitializerValue
            || !ShouldEmitInlineAggregateZeroFill(valueType)
            || TryGetConcreteTypeLayout(valueType) is not { } layout)
        {
            return false;
        }

        AppendLine($"  call void @llvm.memset.inline.p0.i64(ptr {destinationAddress}, i8 0, i64 {layout.SizeBytes}, i1 false)");
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
            GetInvariantLoadMetadataSuffix(sourceAddress));
    }

    private bool TryEmitScalarizedAggregateCopy(
        string destinationAddress,
        string sourceAddress,
        StarkTypeSymbol copyType,
        string invariantLoadMetadataSuffix)
    {
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
            AppendLine(
                $"  {loadedLeaf} = load {MapType(leaf.Type)}, ptr {sourceLeafAddress}{invariantLoadMetadataSuffix}");
            var destinationLeafAddress = EmitScalarizedAggregateLeafAddress(destinationAddress, copyType, leaf.Indices, "copy_dest");
            AppendLine($"  store {MapType(leaf.Type)} {loadedLeaf}, ptr {destinationLeafAddress}");
        }

        return true;
    }

    private bool TryEmitAggregateAddressCopy(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        if (!TryResolveAggregateSourceAddress(value, valueType, out var sourceAddress))
        {
            return false;
        }

        EmitAggregateAddressCopy(destinationAddress, sourceAddress, valueType);
        return true;
    }

    private void EmitAggregateAddressCopy(string destinationAddress, string sourceAddress, StarkTypeSymbol copyType)
    {
        if (TryEmitScalarizedAggregateCopy(destinationAddress, sourceAddress, copyType, string.Empty))
        {
            return;
        }

        if (TryGetConcreteTypeLayout(copyType) is { } layout
            && layout.SizeBytes > AggregateScalarizationThresholdBytes)
        {
            AppendLine(
                $"  call void @llvm.memcpy.inline.p0.p0.i64(ptr {destinationAddress}, ptr {sourceAddress}, i64 {layout.SizeBytes}, i1 false)");
            return;
        }

        var loadedValue = $"%{EscapeIdentifier(CreateAbiTempName("copy_load"))}";
        AppendLine($"  {loadedValue} = load {MapType(copyType)}, ptr {sourceAddress}");
        AppendLine($"  store {MapType(copyType)} {loadedValue}, ptr {destinationAddress}");
    }

    private bool TryResolveAggregateSourceAddress(SsaValue value, StarkTypeSymbol expectedType, out string sourceAddress)
    {
        return TryResolveAggregateSourceAddress(
            value,
            expectedType,
            new HashSet<string>(StringComparer.Ordinal),
            out sourceAddress);
    }

    private bool TryResolveAggregateSourceAddress(
        SsaValue value,
        StarkTypeSymbol expectedType,
        ISet<string> visitedValueNames,
        out string sourceAddress)
    {
        var normalizedExpectedType = NormalizeAggregateType(expectedType);

        switch (value)
        {
            case SsaValueReference reference:
                if (_indirectAggregateValueSlots.TryGetValue(reference.Name, out var indirectSlot))
                {
                    sourceAddress = indirectSlot;
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
                    return true;
                }

                if (!visitedValueNames.Add(reference.Name)
                    || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    sourceAddress = string.Empty;
                    return false;
                }

                switch (definition)
                {
                    case SsaUseRValue use when NormalizeAggregateType(use.Type) == normalizedExpectedType:
                        return TryResolveAggregateSourceAddress(use.Value, expectedType, visitedValueNames, out sourceAddress);
                    case SsaLoadLocalRValue loadLocal when NormalizeAggregateType(loadLocal.Type) == normalizedExpectedType:
                        EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                        sourceAddress = $"%{EscapeIdentifier($"slot_{loadLocal.LocalName}")}";
                        return true;
                    case SsaLoadGlobalRValue loadGlobal when NormalizeAggregateType(loadGlobal.Type) == normalizedExpectedType:
                        sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(loadGlobal.GlobalName))}";
                        return true;
                    case SsaLoadIndirectRValue loadIndirect when NormalizeAggregateType(loadIndirect.Type) == normalizedExpectedType:
                        sourceAddress = FormatValue(loadIndirect.Address);
                        return true;
                    default:
                        sourceAddress = string.Empty;
                        return false;
                }
            case SsaGlobalAddressValue globalAddress when NormalizeAggregateType(globalAddress.PointeeType) == normalizedExpectedType:
                sourceAddress = $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}";
                return true;
            default:
                sourceAddress = string.Empty;
                return false;
        }
    }

    private bool TryEmitStructuredAggregateStore(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
        return TryEmitStructuredAggregateStore(
            destinationAddress,
            valueType,
            value,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryEmitStructuredAggregateStore(
        string destinationAddress,
        StarkTypeSymbol valueType,
        SsaValue value,
        ISet<string> visitedValueNames)
    {
        switch (value)
        {
            case SsaZeroInitializerValue:
                if (!TryEmitInlineAggregateZeroFill(destinationAddress, valueType, value))
                {
                    AppendLine($"  store {MapType(valueType)} zeroinitializer, ptr {destinationAddress}");
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
                        return TryEmitStructuredAggregateStore(destinationAddress, valueType, use.Value, visitedValueNames);
                    case SsaInsertFieldRValue insertField when NormalizeAggregateType(insertField.Type) == NormalizeAggregateType(valueType):
                    {
                        var fieldType = GetAggregateElementType(valueType, insertField.FieldIndex);
                        if (fieldType is null
                            || !TryEmitStructuredAggregateStore(destinationAddress, valueType, insertField.Target, visitedValueNames))
                        {
                            return false;
                        }

                        var fieldAddress = EmitScalarizedAggregateLeafAddress(
                            destinationAddress,
                            valueType,
                            [insertField.FieldIndex],
                            "insert_field_store");
                        EmitValueToAddress(fieldAddress, fieldType, insertField.Value);
                        return true;
                    }
                    case SsaInsertIndexRValue insertIndex when NormalizeAggregateType(insertIndex.Type) == NormalizeAggregateType(valueType):
                    {
                        var elementType = GetAggregateElementType(valueType, insertIndex.ElementIndex);
                        if (elementType is null
                            || !TryEmitStructuredAggregateStore(destinationAddress, valueType, insertIndex.Target, visitedValueNames))
                        {
                            return false;
                        }

                        var elementAddress = EmitScalarizedAggregateLeafAddress(
                            destinationAddress,
                            valueType,
                            [insertIndex.ElementIndex],
                            "insert_index_store");
                        EmitValueToAddress(elementAddress, elementType, insertIndex.Value);
                        return true;
                    }
                    default:
                        return false;
                }
            default:
                return false;
        }
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
            case SsaInsertIndexRValue insertIndex when IsNamedReference(insertIndex.Target, valueName):
                return RequiresAggregateValueMaterialization(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type,
                    visitingValueNames);
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

    private bool CanForwardAggregateValueToIndirectCallParameter(SsaCallRValue call, int argumentIndex, StarkTypeSymbol valueType)
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
            SsaCallRValue call => call.Arguments.Any(argument => IsNamedReference(argument, valueName)),
            SsaConvertRValue convert => IsNamedReference(convert.Operand, valueName),
            SsaExtractFieldRValue extractField => IsNamedReference(extractField.Target, valueName),
            SsaInsertFieldRValue insertField => IsNamedReference(insertField.Target, valueName) || IsNamedReference(insertField.Value, valueName),
            SsaExtractIndexRValue extractIndex => IsNamedReference(extractIndex.Target, valueName),
            SsaInsertIndexRValue insertIndex => IsNamedReference(insertIndex.Target, valueName) || IsNamedReference(insertIndex.Value, valueName),
            SsaLoadSliceElementRValue loadSlice => IsNamedReference(loadSlice.Slice, valueName) || IsNamedReference(loadSlice.Index, valueName),
            SsaTextSliceRValue textSlice => IsNamedReference(textSlice.TextValue, valueName) || IsNamedReference(textSlice.Start, valueName) || IsNamedReference(textSlice.Length, valueName),
            SsaFieldAddressRValue fieldAddress => IsNamedReference(fieldAddress.Address, valueName),
            SsaElementAddressRValue elementAddress => IsNamedReference(elementAddress.Address, valueName) || IsNamedReference(elementAddress.Index, valueName),
            SsaSliceElementAddressRValue sliceElementAddress => IsNamedReference(sliceElementAddress.Slice, valueName) || IsNamedReference(sliceElementAddress.Index, valueName),
            SsaLoadIndirectRValue loadIndirect => IsNamedReference(loadIndirect.Address, valueName),
            _ => false
        };
    }

    private bool TryEmitScalarizedAggregateStore(string destinationAddress, StarkTypeSymbol valueType, SsaValue value)
    {
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
            AppendLine($"  store {MapType(leaf.Type)} {leafValue}, ptr {leafAddress}");
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

        var leafAddress = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        var gepIndices = string.Join(", ", indices.Select(static index => $"i32 {index}"));
        AppendLine($"  {leafAddress} = getelementptr inbounds {MapType(rootType)}, ptr {baseAddress}, i32 0, {gepIndices}");
        return leafAddress;
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
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static string FormatZeroInitializer(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeAggregateType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Integer => "0",
            StarkTypeKind.Float => "0.0",
            StarkTypeKind.Bool => "false",
            StarkTypeKind.RawPointer => "null",
            _ => "zeroinitializer"
        };
    }

    private void EmitMakeSliceFromLocal(string result, SsaMakeSliceFromLocalRValue makeSlice)
    {
        EnsureLocalSlotExists(makeSlice.LocalName, makeSlice.SourceType);

        if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray
            || makeSlice.SourceType.ElementType is null
            || makeSlice.SourceType.FixedLength is not int fixedLength)
        {
            throw new UnsupportedBodyEmissionException($"Slice creation from '{makeSlice.SourceType.DisplayName}' is not supported.");
        }

        var slotName = $"%{EscapeIdentifier($"slot_{makeSlice.LocalName}")}";
        var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";

        AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(makeSlice.SourceType)}, ptr {slotName}, i32 0, i32 0");
        AppendLine($"  {withPointer} = insertvalue {MapType(makeSlice.Type)} zeroinitializer, ptr {elementPointer}, 0");
        AppendLine($"  {result} = insertvalue {MapType(makeSlice.Type)} {withPointer}, i64 {fixedLength}, 1");
    }

    private void EmitLoadSliceElement(string result, SsaLoadSliceElementRValue loadSlice)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";

        AppendLine($"  {dataPointer} = extractvalue {MapType(loadSlice.Slice.Type)} {FormatValue(loadSlice.Slice)}, 0");
        AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(loadSlice.Type)}, ptr {dataPointer}, {MapType(loadSlice.Index.Type)} {FormatValue(loadSlice.Index)}");
        AppendLine($"  {result} = load {MapType(loadSlice.Type)}, ptr {elementPointer}");
    }

    private void EmitTextSlice(string result, SsaTextSliceRValue textSlice)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var slicedPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";
        var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";
        var unitType = GetTextUnitType(textSlice.TextValue.Type);

        AppendLine($"  {dataPointer} = extractvalue {MapType(textSlice.TextValue.Type)} {FormatValue(textSlice.TextValue)}, 0");
        AppendLine($"  {slicedPointer} = getelementptr inbounds {MapType(unitType)}, ptr {dataPointer}, {MapType(textSlice.Start.Type)} {FormatValue(textSlice.Start)}");
        AppendLine($"  {withPointer} = insertvalue {MapType(textSlice.Type)} zeroinitializer, ptr {slicedPointer}, 0");
        AppendLine($"  {result} = insertvalue {MapType(textSlice.Type)} {withPointer}, {MapType(textSlice.Length.Type)} {FormatValue(textSlice.Length)}, 1");
    }

    private void EmitAddressOfLocal(string result, SsaAddressOfLocalRValue addressOfLocal)
    {
        EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
        AppendLine($"  {result} = getelementptr inbounds {MapType(addressOfLocal.PointeeType)}, ptr %{EscapeIdentifier($"slot_{addressOfLocal.LocalName}")}, i32 0");
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
                $"  {result} = getelementptr inbounds {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}, i32 0");
            return;
        }

        EnsureParameterSlotExists(parameter, addressOfParameter.PointeeType);
        AppendLine(
            $"  {result} = getelementptr inbounds {MapType(addressOfParameter.PointeeType)}, ptr %{EscapeIdentifier($"slot_param_{parameter.SourceName}")}, i32 0");
    }

    private void EmitFieldAddress(string result, SsaFieldAddressRValue fieldAddress)
    {
        AppendLine($"  {result} = getelementptr inbounds {MapType(fieldAddress.AggregateType)}, ptr {FormatValue(fieldAddress.Address)}, i32 0, i32 {fieldAddress.FieldIndex}");
    }

    private void EmitElementAddress(string result, SsaElementAddressRValue elementAddress)
    {
        if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray)
        {
            var indexValue = elementAddress.ConstantIndex is int constantIndex
                ? constantIndex.ToString()
                : $"{MapType(elementAddress.Index!.Type)} {FormatValue(elementAddress.Index)}";

            if (elementAddress.ConstantIndex is int fixedArrayConstantIndex)
            {
                AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, i32 {fixedArrayConstantIndex}");
            }
            else
            {
                AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, {indexValue}");
            }

            return;
        }

        if (elementAddress.ConstantIndex is int scalarConstant)
        {
            AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 {scalarConstant}");
            return;
        }

        if (elementAddress.Index is null)
        {
            throw new UnsupportedBodyEmissionException("Element address is missing its dynamic index.");
        }

        AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, {MapType(elementAddress.Index.Type)} {FormatValue(elementAddress.Index)}");
    }

    private void EmitSliceElementAddress(string result, SsaSliceElementAddressRValue sliceElementAddress)
    {
        var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
        var elementType = sliceElementAddress.Type.ElementType ?? throw new UnsupportedBodyEmissionException("Slice element address requires a raw pointer element type.");

        AppendLine($"  {dataPointer} = extractvalue {MapType(sliceElementAddress.Slice.Type)} {FormatValue(sliceElementAddress.Slice)}, 0");
        AppendLine($"  {result} = getelementptr inbounds {MapType(elementType)}, ptr {dataPointer}, {MapType(sliceElementAddress.Index.Type)} {FormatValue(sliceElementAddress.Index)}");
    }

    private void EmitTerminator(SsaTerminator terminator)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Goto:
                AppendLine($"  br label %{FormatBlockLabel(terminator.Targets[0])}");
                return;
            case SsaTerminatorKind.Branch:
                if (terminator.Condition is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA branch is missing a condition.");
                }

                AppendLine(
                    $"  br i1 {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.Targets[0])}, label %{FormatBlockLabel(terminator.Targets[1])}");
                return;
            case SsaTerminatorKind.Switch:
                if (terminator.Condition is null || terminator.DefaultTarget is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA switch is missing its condition or default target.");
                }

                if (terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                {
                    AppendLine($"  br label %{FormatBlockLabel(terminator.DefaultTarget.Value)}");
                    return;
                }

                var switchCases = string.Join(
                    " ",
                    terminator.SwitchCases.Select(
                        switchCase => $"{MapType(switchCase.MatchValue.Type)} {FormatValue(switchCase.MatchValue)}, label %{FormatBlockLabel(switchCase.TargetBlockId)}"));

                AppendLine(
                    $"  switch {MapType(terminator.Condition.Type)} {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.DefaultTarget.Value)} [ {switchCases} ]");
                return;
            case SsaTerminatorKind.Return:
                if (_abiFunction.ReturnsIndirect)
                {
                    if (terminator.Value is null || _abiFunction.ReturnBufferParameter is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA aggregate return is missing its value or sret parameter.");
                    }

                    EmitValueToAddress(
                        $"%{EscapeIdentifier(_abiFunction.ReturnBufferParameter.LlvmName)}",
                        _function.ReturnType,
                        terminator.Value);
                    AppendLine("  ret void");
                    return;
                }

                if (_function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    AppendLine("  ret void");
                    return;
                }

                if (terminator.Value is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA return is missing a return value.");
                }

                AppendLine($"  ret {MapType(_function.ReturnType)} {FormatValue(terminator.Value)}");
                return;
            case SsaTerminatorKind.Unreachable:
                AppendLine("  unreachable");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA terminator '{terminator.Kind}'.");
        }
    }

    private void EmitFallbackTerminal()
    {
        if (_abiFunction.ReturnsIndirect || _function.ReturnType.Kind == StarkTypeKind.Void)
        {
            AppendLine("  ret void");
            return;
        }

        throw new UnsupportedBodyEmissionException("SSA function body has no blocks.");
    }

    private static string FormatBlockLabel(int blockId) => $"bb{blockId}";

    private string FormatValue(SsaValue value)
    {
        return value switch
        {
            SsaValueReference reference => FormatValueReference(reference),
            SsaIntegerConstant integer => integer.Value.ToString(),
            SsaFloatConstant floating => FormatFloatLiteral(floating),
            SsaStringConstant text => FormatStringConstantValue(text),
            SsaBoolConstant boolean => boolean.Value ? "true" : "false",
            SsaNullConstant => "null",
            SsaGlobalAddressValue globalAddress => $"@{EscapeIdentifier(ResolveGlobalSymbolName(globalAddress.GlobalName))}",
            SsaZeroInitializerValue => "zeroinitializer",
            SsaUndefValue => "undef",
            _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
        };
    }

    private string GetInvariantLoadMetadataSuffix(string globalName)
    {
        return IsConstGlobalName(globalName)
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private string GetInvariantLoadMetadataSuffix(SsaValue address)
    {
        return IsConstGlobalAddress(address)
            ? $", !invariant.load {EmptyMetadataRef}"
            : string.Empty;
    }

    private bool IsConstGlobalAddress(SsaValue value)
    {
        return IsConstGlobalAddress(value, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool IsConstGlobalAddress(SsaValue value, ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue globalAddress => IsConstGlobalName(globalAddress.GlobalName),
            SsaValueReference reference => ResolveConstGlobalAddress(reference, visitedValueNames),
            _ => false
        };
    }

    private bool ResolveConstGlobalAddress(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (!visitedValueNames.Add(reference.Name)
            || !_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => IsConstGlobalAddress(use.Value, visitedValueNames),
            SsaFieldAddressRValue fieldAddress => IsConstGlobalAddress(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => IsConstGlobalAddress(elementAddress.Address, visitedValueNames),
            SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                        && convert.TargetType.Kind == StarkTypeKind.RawPointer
                => IsConstGlobalAddress(convert.Operand, visitedValueNames),
            _ => false
        };
    }

    private static string FormatFloatLiteral(SsaFloatConstant floating)
    {
        if (!double.TryParse(
                floating.LiteralText,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new UnsupportedBodyEmissionException(
                $"Unable to parse floating-point literal '{floating.LiteralText}' for LLVM emission.");
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            var bits = floating.Type.BitWidth == 32
                ? BitConverter.DoubleToUInt64Bits((double)(float)parsed)
                : BitConverter.DoubleToUInt64Bits(parsed);
            return $"0x{bits:X16}";
        }

        var rendered = floating.Type.BitWidth == 32
            ? ((double)(float)parsed).ToString("R", CultureInfo.InvariantCulture)
            : parsed.ToString("R", CultureInfo.InvariantCulture);

        return rendered.Contains('.', StringComparison.Ordinal)
            || rendered.Contains('E', StringComparison.Ordinal)
            || rendered.Contains('e', StringComparison.Ordinal)
            ? rendered
            : rendered + ".0";
    }

    private string RenderDirectArgument(AbiParameterSymbol parameter, SsaValue argument)
    {
        if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
        {
            return $"ptr {ExtractStringDataPointer(argument)}";
        }

        return $"{MapType(parameter.LlvmType)} {FormatValue(argument)}";
    }

    private string RenderIndirectArgumentPointer(AbiParameterSymbol parameter, string pointerValue)
    {
        var segments = new List<string> { "ptr" };

        if (AbiLoweringHeuristics.IsByValueIndirectParameter(parameter))
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

    private string RenderSRetArgumentPointer(StarkTypeSymbol returnType, string pointerValue)
    {
        var segments = new List<string> { "ptr", $"sret({MapType(returnType)})" };
        if (TryGetConcreteTypeLayout(returnType) is { AlignmentBytes: > 1 } layout)
        {
            segments.Add($"align {layout.AlignmentBytes}");
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
        return $"getelementptr inbounds ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
    }

    private void EnsureLocalSlotExists(string localName, StarkTypeSymbol localType)
    {
        var slotName = EscapeIdentifier($"slot_{localName}");
        if (!_allocatedLocalSlots.Add(slotName))
        {
            return;
        }

        switch (GetLocalStorageClass(localName))
        {
            case "stack":
                AppendLine($"  %{slotName} = alloca {MapType(localType)}");
                return;
            case "heap":
                EmitHeapAllocateLocalSlot(slotName, localType);
                return;
            default:
                throw new UnsupportedBodyEmissionException(
                    $"Local storage class '{GetLocalStorageClass(localName)}' is not yet supported for LLVM body emission.");
        }
    }

    private void EmitHeapAllocateLocalSlot(string slotName, StarkTypeSymbol localType)
    {
        var sizePointer = $"%{EscapeIdentifier(CreateAbiTempName("heap_size_ptr"))}";
        var sizeValue = $"%{EscapeIdentifier(CreateAbiTempName("heap_size"))}";
        AppendLine($"  {sizePointer} = getelementptr {MapType(localType)}, ptr null, i32 1");
        AppendLine($"  {sizeValue} = ptrtoint ptr {sizePointer} to {AllocatorSizeType}");
        AppendLine($"  %{slotName} = call ptr @malloc({AllocatorSizeType} {sizeValue})");
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
            AppendLine($"  %{slotName} = alloca {MapType(parameterType)}");

            var incomingValue = _materializedParameters.TryGetValue(parameter.LlvmName, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(parameter.LlvmName)}";
            AppendLine($"  store {MapType(parameterType)} {incomingValue}, ptr %{slotName}");
        }
    }

    private void EmitEntryParameterMaterialization()
    {
        foreach (var parameter in _abiFunction.UserParameters)
        {
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
            AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}");
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

    private static HashSet<string> CollectReferencedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    VisitValue(incoming.Value);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitValue(block.Terminator.Condition);
            VisitValue(block.Terminator.Value);

            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitValue(switchCase.MatchValue);
                }
            }
        }

        return names;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    VisitRValue(valueInstruction.Value);
                    break;
                case SsaStoreLocalInstruction storeLocal:
                    VisitValue(storeLocal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitValue(storeIndirect.Address);
                    VisitValue(storeIndirect.Value);
                    break;
                case SsaCopyMemoryInstruction copyMemory:
                    VisitValue(copyMemory.DestinationAddress);
                    VisitValue(copyMemory.SourceAddress);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitValue(storeGlobal.Value);
                    break;
            }
        }

        void VisitRValue(SsaRValue value)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    VisitValue(use.Value);
                    break;
                case SsaUnaryRValue unary:
                    VisitValue(unary.Operand);
                    break;
                case SsaBinaryRValue binary:
                    VisitValue(binary.Left);
                    VisitValue(binary.Right);
                    break;
                case SsaCallRValue call:
                    foreach (var argument in call.Arguments)
                    {
                        VisitValue(argument);
                    }

                    break;
                case SsaConvertRValue convert:
                    VisitValue(convert.Operand);
                    break;
                case SsaExtractFieldRValue extractField:
                    VisitValue(extractField.Target);
                    break;
                case SsaInsertFieldRValue insertField:
                    VisitValue(insertField.Target);
                    VisitValue(insertField.Value);
                    break;
                case SsaExtractIndexRValue extractIndex:
                    VisitValue(extractIndex.Target);
                    break;
                case SsaInsertIndexRValue insertIndex:
                    VisitValue(insertIndex.Target);
                    VisitValue(insertIndex.Value);
                    break;
                case SsaLoadSliceElementRValue loadSlice:
                    VisitValue(loadSlice.Slice);
                    VisitValue(loadSlice.Index);
                    break;
                case SsaTextSliceRValue textSlice:
                    VisitValue(textSlice.TextValue);
                    VisitValue(textSlice.Start);
                    VisitValue(textSlice.Length);
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    VisitValue(fieldAddress.Address);
                    break;
                case SsaElementAddressRValue elementAddress:
                    VisitValue(elementAddress.Address);
                    VisitValue(elementAddress.Index);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    VisitValue(sliceElementAddress.Slice);
                    VisitValue(sliceElementAddress.Index);
                    break;
                case SsaLoadIndirectRValue loadIndirect:
                    VisitValue(loadIndirect.Address);
                    break;
            }
        }

        void VisitValue(SsaValue? value)
        {
            if (value is SsaValueReference reference)
            {
                names.Add(reference.Name);
            }
        }
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction valueInstruction)
                {
                    definitions[valueInstruction.ResultName] = valueInstruction.Value;
                }
            }
        }

        return definitions;
    }

    private static Dictionary<string, string> CollectLocalStorageClasses(SsaFunction function)
    {
        var storageClasses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaAllocateLocalInstruction allocateLocal)
                {
                    storageClasses[allocateLocal.LocalName] = allocateLocal.StorageClass;
                }
            }
        }

        return storageClasses;
    }

    private string FormatValueReference(SsaValueReference reference)
    {
        return _materializedParameters.TryGetValue(reference.Name, out var materialized)
            ? materialized
            : $"%{EscapeIdentifier(reference.Name)}";
    }

    private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

    private string AllocatorSizeType => _context.AllocatorSizeType;

    private string EmptyMetadataRef => _context.EmptyTupleMetadataRef;

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _context.ResolveNamedTypeSymbol(type);

    private EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type) =>
        _context.ResolveStringConstant(literalText, type);

    private string ResolveGlobalSymbolName(string globalName) => _context.ResolveGlobalSymbolName(globalName);

    private bool IsConstGlobalName(string globalName) => _context.IsConstGlobalName(globalName);

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

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static SourceLocation? GetInstructionLocation(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction.Location,
            SsaAllocateLocalInstruction allocateLocal => allocateLocal.Location,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart.Location,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd.Location,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal.Location,
            SsaStoreLocalInstruction storeLocal => storeLocal.Location,
            SsaCopyMemoryInstruction copyMemory => copyMemory.Location,
            SsaStoreIndirectInstruction storeIndirect => storeIndirect.Location,
            SsaStoreGlobalInstruction storeGlobal => storeGlobal.Location,
            _ => null
        };
    }

    private static bool IsStringType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
    {
        return textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new UnsupportedBodyEmissionException($"Text operations require an ascii/unicode value, but found '{textType.DisplayName}'.")
        };
    }

    private void AppendLine(string text)
    {
        if (_debugFunction is not null
            && _currentDebugLocation is not null
            && ShouldAttachDebugLocation(text))
        {
            text = $"{text}, !dbg {_debugFunction.GetLocationRef(_currentDebugLocation)}";
        }

        _builder.AppendLine(text);
    }

    private static bool ShouldAttachDebugLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !text.StartsWith("  ", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return !trimmed.StartsWith(';')
            && !trimmed.StartsWith('}');
    }
}
