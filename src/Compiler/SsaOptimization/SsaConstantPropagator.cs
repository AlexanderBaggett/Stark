using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaConstantPropagator
{
    private readonly SsaCleanupOptimizer _cleanupOptimizer = new(enableSelectPredication: false);
    private static readonly IReadOnlyDictionary<string, ConstantState> EmptyConstantStates =
        new Dictionary<string, ConstantState>(StringComparer.Ordinal);
    private const int PropagationPassCount = 2;

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var optimized = new SsaIrModule(
            module.ModuleName,
            module.Functions.Select(function => OptimizeFunction(function, module.ModuleName)).ToArray(),
            module.AddressTakenFunctions);

        return SsaAddressTakenFunctionPruner.Prune(optimized);
    }

    public SsaFunction OptimizeFunction(SsaFunction function)
    {
        return OptimizeFunction(function, moduleName: string.Empty);
    }

    private SsaFunction OptimizeFunction(SsaFunction function, string moduleName)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = function;

        for (var iteration = 0; iteration < PropagationPassCount; iteration++)
        {
            current = OptimizeFunctionCore(current, moduleName);
        }

        return current;
    }

    private SsaFunction OptimizeFunctionCore(SsaFunction function, string moduleName)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var states = new Dictionary<string, ConstantState>(StringComparer.Ordinal);

        foreach (var parameter in function.Parameters)
        {
            states[$"arg_{parameter.Name}"] = ConstantState.Overdefined;
        }

        HashSet<int> reachable;

        while (true)
        {
            reachable = ComputeReachableBlocks(function, byId, states);
            var newStates = new Dictionary<string, ConstantState>(StringComparer.Ordinal);

            foreach (var parameter in function.Parameters)
            {
                newStates[$"arg_{parameter.Name}"] = ConstantState.Overdefined;
            }

            var changed = true;
            while (changed)
            {
                changed = false;

                foreach (var block in function.Blocks)
                {
                    if (!reachable.Contains(block.Id))
                    {
                        continue;
                    }

                    foreach (var phi in block.Phis)
                    {
                        var state = EvaluatePhi(phi, newStates, reachable);
                        if (UpdateState(newStates, phi.ResultName, state))
                        {
                            changed = true;
                        }
                    }

                    foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
                    {
                        var state = EvaluateRValue(instruction.Value, newStates, moduleName);
                        if (UpdateState(newStates, instruction.ResultName, state))
                        {
                            changed = true;
                        }
                    }
                }
            }

            if (StatesEqual(states, newStates))
            {
                states = newStates;
                break;
            }

            states = newStates;
        }

        var replacements = states
            .Where(static pair => pair.Value.Kind == ConstantStateKind.Constant && pair.Value.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Value!, StringComparer.Ordinal);

        var blocks = new List<SsaBasicBlock>();
        foreach (var block in function.Blocks)
        {
            if (!reachable.Contains(block.Id))
            {
                continue;
            }

            var phis = block.Phis
                .Where(phi => !replacements.ContainsKey(phi.ResultName))
                .Select(phi => new SsaPhi(
                    phi.ResultName,
                    phi.VariableName,
                    phi.Type,
                    phi.Incomings
                        .Where(incoming => reachable.Contains(incoming.PredecessorBlockId))
                        .Select(incoming => new SsaPhiIncoming(
                            incoming.PredecessorBlockId,
                            RewriteValue(incoming.Value, replacements)))
                        .ToArray()))
                .ToArray();

            var instructions = block.Instructions
                .Select(instruction => RewriteInstruction(instruction, replacements))
                .Where(instruction => instruction is not SsaValueInstruction valueInstruction
                                      || !replacements.ContainsKey(valueInstruction.ResultName))
                .ToArray();

            blocks.Add(new SsaBasicBlock(
                block.Id,
                block.Label,
                phis,
                instructions,
                FoldTerminator(block.Terminator, replacements)));
        }

        var propagated = function with { Blocks = blocks.ToArray() };
        propagated = _cleanupOptimizer.PruneUnreachableBlocks(propagated);
        propagated = _cleanupOptimizer.OptimizeFunction(propagated);
        return propagated;
    }

    private static HashSet<int> ComputeReachableBlocks(
        SsaFunction function,
        IReadOnlyDictionary<int, SsaBasicBlock> blocks,
        IReadOnlyDictionary<string, ConstantState> states)
    {
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(function.EntryBlockId);

        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current) || !blocks.TryGetValue(current, out var block))
            {
                continue;
            }

            foreach (var successor in GetReachableSuccessors(block.Terminator, states))
            {
                if (blocks.ContainsKey(successor))
                {
                    pending.Push(successor);
                }
            }
        }

        return reachable;
    }

    private static IEnumerable<int> GetReachableSuccessors(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, ConstantState> states)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Goto:
                return terminator.Targets;
            case SsaTerminatorKind.Branch:
                if (terminator.Condition is not null
                    && TryGetBoolConstant(ResolveConstantState(terminator.Condition, states), out var branchCondition))
                {
                    return [terminator.Targets[branchCondition ? 0 : 1]];
                }

                return terminator.Targets;
            case SsaTerminatorKind.Switch:
                if (terminator.Condition is not null
                    && TryMatchConstantSwitchTarget(ResolveConstantState(terminator.Condition, states), terminator, out var matchedTarget))
                {
                    return [matchedTarget];
                }

                return terminator.DefaultTarget is { } defaultTarget
                    ? terminator.Targets.Concat([defaultTarget]).Distinct().ToArray()
                    : terminator.Targets;
            default:
                return [];
        }
    }

    private static bool TryMatchConstantSwitchTarget(
        ConstantState conditionState,
        SsaTerminator terminator,
        out int targetBlockId)
    {
        targetBlockId = terminator.DefaultTarget ?? terminator.Targets.LastOrDefault();

        if (conditionState.Kind != ConstantStateKind.Constant || conditionState.Value is null)
        {
            return false;
        }

        if (terminator.SwitchCases is not null)
        {
            foreach (var switchCase in terminator.SwitchCases)
            {
                var caseState = ResolveConstantState(switchCase.MatchValue, EmptyConstantStates);
                if (caseState.Kind == ConstantStateKind.Constant
                    && caseState.Value is not null
                    && ConstantValuesEqual(conditionState.Value, caseState.Value))
                {
                    targetBlockId = switchCase.TargetBlockId;
                    return true;
                }
            }
        }

        return true;
    }

    private static ConstantState EvaluatePhi(
        SsaPhi phi,
        IReadOnlyDictionary<string, ConstantState> states,
        IReadOnlySet<int> reachable)
    {
        var incomingStates = phi.Incomings
            .Where(incoming => reachable.Contains(incoming.PredecessorBlockId))
            .Select(incoming => ResolveConstantState(incoming.Value, states))
            .ToArray();

        return JoinStates(incomingStates);
    }

    private static ConstantState EvaluateRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, ConstantState> states,
        string moduleName)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return ResolveConstantState(use.Value, states);
            case SsaUnaryRValue unary:
                {
                    var operand = ResolveConstantState(unary.Operand, states);
                    if (operand.Kind != ConstantStateKind.Constant || operand.Value is null)
                    {
                        return operand.Kind == ConstantStateKind.Unknown ? ConstantState.Unknown : ConstantState.Overdefined;
                    }

                    return TryFoldUnary(unary, operand.Value, out var foldedUnary)
                        ? ConstantState.FromValue(foldedUnary)
                        : ConstantState.Overdefined;
                }
            case SsaBinaryRValue binary:
                {
                    var left = ResolveConstantState(binary.Left, states);
                    var right = ResolveConstantState(binary.Right, states);
                    if (left.Kind == ConstantStateKind.Unknown || right.Kind == ConstantStateKind.Unknown)
                    {
                        return ConstantState.Unknown;
                    }

                    if (left.Kind != ConstantStateKind.Constant || right.Kind != ConstantStateKind.Constant
                        || left.Value is null || right.Value is null)
                    {
                        return ConstantState.Overdefined;
                    }

                    return TryFoldBinary(binary, left.Value, right.Value, out var foldedBinary)
                        ? ConstantState.FromValue(foldedBinary)
                        : ConstantState.Overdefined;
                }
            case SsaSelectRValue select:
                {
                    var condition = ResolveConstantState(select.Condition, states);
                    if (condition.Kind == ConstantStateKind.Unknown)
                    {
                        return ConstantState.Unknown;
                    }

                    if (condition.Kind != ConstantStateKind.Constant
                        || condition.Value is not SsaBoolConstant boolean)
                    {
                        return ConstantState.Overdefined;
                    }

                    return ResolveConstantState(boolean.Value ? select.WhenTrue : select.WhenFalse, states);
                }
            case SsaConvertRValue convert:
                {
                    var operand = ResolveConstantState(convert.Operand, states);
                    if (operand.Kind != ConstantStateKind.Constant || operand.Value is null)
                    {
                        return operand.Kind == ConstantStateKind.Unknown ? ConstantState.Unknown : ConstantState.Overdefined;
                    }

                    return TryFoldConvert(convert, operand.Value, out var foldedConvert)
                        ? ConstantState.FromValue(foldedConvert)
                        : ConstantState.Overdefined;
                }
            case SsaCallRValue call:
                return TryFoldTextLengthCall(call, states, moduleName, out var foldedCall)
                    ? ConstantState.FromValue(foldedCall)
                    : ConstantState.Overdefined;
            default:
                return ConstantState.Overdefined;
        }
    }

    private static ConstantState ResolveConstantState(
        SsaValue value,
        IReadOnlyDictionary<string, ConstantState> states)
    {
        return value switch
        {
            SsaIntegerConstant integer => ConstantState.FromValue(integer),
            SsaFloatConstant floating => ConstantState.FromValue(floating),
            SsaBoolConstant boolean => ConstantState.FromValue(boolean),
            SsaStringConstant text => ConstantState.FromValue(text),
            SsaNullConstant nullValue => ConstantState.FromValue(nullValue),
            SsaFunctionAddressValue functionAddress => ConstantState.FromValue(functionAddress),
            SsaClosureValue closure => ConstantState.FromValue(closure),
            SsaZeroInitializerValue zero when TryExpandScalarZero(zero.Type, out var zeroValue) => ConstantState.FromValue(zeroValue),
            SsaValueReference reference when states.TryGetValue(reference.Name, out var state) => state,
            SsaValueReference => ConstantState.Unknown,
            _ => ConstantState.Overdefined
        };
    }

    private static ConstantState JoinStates(IReadOnlyList<ConstantState> states)
    {
        if (states.Count == 0)
        {
            return ConstantState.Unknown;
        }

        var aggregate = states[0];
        for (var index = 1; index < states.Count; index++)
        {
            aggregate = JoinStates(aggregate, states[index]);
            if (aggregate.Kind == ConstantStateKind.Overdefined)
            {
                break;
            }
        }

        return aggregate;
    }

    private static ConstantState JoinStates(ConstantState left, ConstantState right)
    {
        if (left.Kind == ConstantStateKind.Unknown || right.Kind == ConstantStateKind.Unknown)
        {
            return ConstantState.Unknown;
        }

        if (left.Kind == ConstantStateKind.Overdefined || right.Kind == ConstantStateKind.Overdefined)
        {
            return ConstantState.Overdefined;
        }

        if (left.Value is not null && right.Value is not null && ConstantValuesEqual(left.Value, right.Value))
        {
            return left;
        }

        return ConstantState.Overdefined;
    }

    private static bool UpdateState(
        IDictionary<string, ConstantState> states,
        string name,
        ConstantState value)
    {
        if (states.TryGetValue(name, out var existing) && existing.Equals(value))
        {
            return false;
        }

        states[name] = value;
        return true;
    }

    private static bool StatesEqual(
        IReadOnlyDictionary<string, ConstantState> left,
        IReadOnlyDictionary<string, ConstantState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) || !pair.Value.Equals(other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFoldUnary(SsaUnaryRValue unary, SsaValue operand, out SsaValue folded)
    {
        folded = operand;

        return unary.Operator switch
        {
            SsaUnaryOperator.LogicalNot when operand is SsaBoolConstant boolean => FoldLogicalNot(boolean, out folded),
            SsaUnaryOperator.Negate when operand is SsaIntegerConstant integer => FoldIntegerNegate(integer, out folded),
            SsaUnaryOperator.Negate when operand is SsaFloatConstant floating => FoldFloatNegate(floating, out folded),
            SsaUnaryOperator.BitwiseNot when operand is SsaIntegerConstant integer => FoldBitwiseNot(integer, out folded),
            _ => false
        };
    }

    private static bool TryFoldBinary(SsaBinaryRValue binary, SsaValue left, SsaValue right, out SsaValue folded)
    {
        folded = left;

        if (left is SsaIntegerConstant leftInteger && right is SsaIntegerConstant rightInteger)
        {
            return FoldIntegerBinary(binary, leftInteger, rightInteger, out folded);
        }

        if (left is SsaFloatConstant leftFloat && right is SsaFloatConstant rightFloat)
        {
            return FoldFloatBinary(binary, leftFloat, rightFloat, out folded);
        }

        if (left is SsaBoolConstant leftBool && right is SsaBoolConstant rightBool)
        {
            return FoldBooleanBinary(binary, leftBool, rightBool, out folded);
        }

        if (binary.Operator is SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual
            && ConstantValuesEqual(left, right))
        {
            folded = new SsaBoolConstant(binary.Operator == SsaBinaryOperator.Equal);
            return true;
        }

        return false;
    }

    private static bool TryFoldConvert(SsaConvertRValue convert, SsaValue operand, out SsaValue folded)
    {
        folded = operand;

        if (operand.Type == convert.TargetType)
        {
            return true;
        }

        if (operand is SsaIntegerConstant integer && convert.TargetType.Kind == StarkTypeKind.Integer)
        {
            return TryFoldSignedInteger(convert.TargetType, integer.Value, out folded);
        }

        if (operand is SsaBoolConstant boolean && convert.TargetType.Kind == StarkTypeKind.Bool)
        {
            folded = boolean;
            return true;
        }

        if (operand is SsaFloatConstant floating && convert.TargetType.Kind == StarkTypeKind.Float)
        {
            if (!TryParseFloat(floating, out var value))
            {
                return false;
            }

            folded = new SsaFloatConstant(FormatFloat(value, convert.TargetType), convert.TargetType);
            return true;
        }

        return false;
    }

    private static bool TryFoldTextLengthCall(
        SsaCallRValue call,
        IReadOnlyDictionary<string, ConstantState> states,
        string moduleName,
        out SsaValue folded)
    {
        folded = default!;
        if (call.Type.Kind != StarkTypeKind.Integer
            || call.Arguments.Count != 1
            || !SsaValueFactAnalyzer.TryGetSystemTextLengthFunction(call.FunctionName, moduleName, out var textKind)
            || call.Arguments[0].Type.Kind != textKind
            || ResolveConstantState(call.Arguments[0], states) is not
            {
                Kind: ConstantStateKind.Constant,
                Value: SsaStringConstant source
            }
            || !TextLiteralDecoder.TryDecode(
                source.LiteralText,
                source.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                out var decoded,
                out _))
        {
            return false;
        }

        var length = textKind == StarkTypeKind.Unicode
            ? decoded.Utf32CodeUnits.Length
            : decoded.Utf8Bytes.Length;
        folded = new SsaIntegerConstant(length, call.Type);
        return true;
    }

    private static bool FoldLogicalNot(SsaBoolConstant boolean, out SsaValue folded)
    {
        folded = new SsaBoolConstant(!boolean.Value);
        return true;
    }

    private static bool FoldIntegerNegate(SsaIntegerConstant integer, out SsaValue folded)
    {
        if (!TryFitInteger(-integer.Value, integer.Type, out var fitted))
        {
            folded = integer;
            return false;
        }

        folded = new SsaIntegerConstant(fitted, integer.Type);
        return true;
    }

    private static bool FoldBitwiseNot(SsaIntegerConstant integer, out SsaValue folded)
    {
        var bitWidth = integer.Type.BitWidth ?? 0;
        if (bitWidth <= 0)
        {
            folded = integer;
            return false;
        }

        var mask = (BigInteger.One << bitWidth) - 1;
        var twosComplement = integer.Value & mask;
        var inverted = (~twosComplement) & mask;
        var foldedValue = integer.Type.IsUnsigned ? inverted : FromTwosComplement(inverted, bitWidth);
        if (!StarkTypeSymbols.IntegerValueFitsEffectiveRange(foldedValue, integer.Type))
        {
            folded = integer;
            return false;
        }

        folded = new SsaIntegerConstant(foldedValue, integer.Type);
        return true;
    }

    private static bool FoldFloatNegate(SsaFloatConstant floating, out SsaValue folded)
    {
        if (!TryParseFloat(floating, out var value))
        {
            folded = floating;
            return false;
        }

        folded = new SsaFloatConstant(FormatFloat(-value, floating.Type), floating.Type);
        return true;
    }

    private static bool FoldIntegerBinary(
        SsaBinaryRValue binary,
        SsaIntegerConstant left,
        SsaIntegerConstant right,
        out SsaValue folded)
    {
        folded = left;
        var bitWidth = left.Type.BitWidth ?? 0;
        if (bitWidth <= 0)
        {
            return false;
        }

        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
                return TryFoldSignedInteger(left.Type, left.Value + right.Value, out folded);
            case SsaBinaryOperator.Subtract:
                return TryFoldSignedInteger(left.Type, left.Value - right.Value, out folded);
            case SsaBinaryOperator.Multiply:
                return TryFoldSignedInteger(left.Type, left.Value * right.Value, out folded);
            case SsaBinaryOperator.WrappingAdd:
                return TryWrapSignedInteger(left.Type, left.Value + right.Value, out folded);
            case SsaBinaryOperator.WrappingSubtract:
                return TryWrapSignedInteger(left.Type, left.Value - right.Value, out folded);
            case SsaBinaryOperator.WrappingMultiply:
                return TryWrapSignedInteger(left.Type, left.Value * right.Value, out folded);
            case SsaBinaryOperator.SaturatingAdd:
                return TryClampSignedInteger(left.Type, left.Value + right.Value, out folded);
            case SsaBinaryOperator.SaturatingSubtract:
                return TryClampSignedInteger(left.Type, left.Value - right.Value, out folded);
            case SsaBinaryOperator.SaturatingMultiply:
                return TryClampSignedInteger(left.Type, left.Value * right.Value, out folded);
            case SsaBinaryOperator.Divide:
                if (right.Value.IsZero)
                {
                    return false;
                }

                return TryFoldSignedInteger(left.Type, left.Value / right.Value, out folded);
            case SsaBinaryOperator.Modulo:
                if (right.Value.IsZero)
                {
                    return false;
                }

                return TryFoldSignedInteger(left.Type, left.Value % right.Value, out folded);
            case SsaBinaryOperator.BitwiseAnd:
                return TryFoldSignedInteger(left.Type, left.Value & right.Value, out folded);
            case SsaBinaryOperator.BitwiseXor:
                return TryFoldSignedInteger(left.Type, left.Value ^ right.Value, out folded);
            case SsaBinaryOperator.BitwiseOr:
                return TryFoldSignedInteger(left.Type, left.Value | right.Value, out folded);
            case SsaBinaryOperator.ShiftLeft:
                if (!TryGetValidShiftAmount(right.Value, bitWidth, out var leftShift))
                {
                    return false;
                }

                return TryFoldSignedInteger(left.Type, left.Value << leftShift, out folded);
            case SsaBinaryOperator.ShiftRight:
                if (!TryGetValidShiftAmount(right.Value, bitWidth, out var rightShift))
                {
                    return false;
                }

                return TryFoldSignedInteger(left.Type, left.Value >> rightShift, out folded);
            case SsaBinaryOperator.Equal:
                folded = new SsaBoolConstant(left.Value == right.Value);
                return true;
            case SsaBinaryOperator.NotEqual:
                folded = new SsaBoolConstant(left.Value != right.Value);
                return true;
            case SsaBinaryOperator.LessThan:
                folded = new SsaBoolConstant(left.Value < right.Value);
                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                folded = new SsaBoolConstant(left.Value <= right.Value);
                return true;
            case SsaBinaryOperator.GreaterThan:
                folded = new SsaBoolConstant(left.Value > right.Value);
                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                folded = new SsaBoolConstant(left.Value >= right.Value);
                return true;
            default:
                return false;
        }
    }

    private static bool FoldFloatBinary(
        SsaBinaryRValue binary,
        SsaFloatConstant left,
        SsaFloatConstant right,
        out SsaValue folded)
    {
        folded = left;

        if (!TryParseFloat(left, out var leftValue)
            || !TryParseFloat(right, out var rightValue))
        {
            return false;
        }

        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
                folded = new SsaFloatConstant(FormatFloat(leftValue + rightValue, left.Type), left.Type);
                return true;
            case SsaBinaryOperator.Subtract:
                folded = new SsaFloatConstant(FormatFloat(leftValue - rightValue, left.Type), left.Type);
                return true;
            case SsaBinaryOperator.Multiply:
                folded = new SsaFloatConstant(FormatFloat(leftValue * rightValue, left.Type), left.Type);
                return true;
            case SsaBinaryOperator.Divide:
                folded = new SsaFloatConstant(FormatFloat(leftValue / rightValue, left.Type), left.Type);
                return true;
            case SsaBinaryOperator.Exponent:
                folded = new SsaFloatConstant(FormatFloat(Math.Pow(leftValue, rightValue), left.Type), left.Type);
                return true;
            case SsaBinaryOperator.Equal:
                folded = new SsaBoolConstant(leftValue == rightValue);
                return true;
            case SsaBinaryOperator.NotEqual:
                folded = new SsaBoolConstant(leftValue != rightValue);
                return true;
            case SsaBinaryOperator.LessThan:
                folded = new SsaBoolConstant(leftValue < rightValue);
                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                folded = new SsaBoolConstant(leftValue <= rightValue);
                return true;
            case SsaBinaryOperator.GreaterThan:
                folded = new SsaBoolConstant(leftValue > rightValue);
                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                folded = new SsaBoolConstant(leftValue >= rightValue);
                return true;
            default:
                return false;
        }
    }

    private static bool FoldBooleanBinary(
        SsaBinaryRValue binary,
        SsaBoolConstant left,
        SsaBoolConstant right,
        out SsaValue folded)
    {
        folded = binary.Operator switch
        {
            SsaBinaryOperator.Equal => new SsaBoolConstant(left.Value == right.Value),
            SsaBinaryOperator.NotEqual => new SsaBoolConstant(left.Value != right.Value),
            _ => left
        };

        return binary.Operator is SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual;
    }

    private static bool TryFoldSignedInteger(StarkTypeSymbol type, BigInteger value, out SsaValue folded)
    {
        if (TryFitInteger(value, type, out var fitted))
        {
            folded = new SsaIntegerConstant(fitted, type);
            return true;
        }

        folded = new SsaIntegerConstant(value, type);
        return false;
    }

    private static bool TryWrapSignedInteger(StarkTypeSymbol type, BigInteger value, out SsaValue folded)
    {
        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            folded = new SsaIntegerConstant(value, type);
            return false;
        }

        var modulus = BigInteger.One << bitWidth;
        var normalized = ((value % modulus) + modulus) % modulus;
        var wrapped = type.IsUnsigned ? normalized : FromTwosComplement(normalized, bitWidth);
        if (!StarkTypeSymbols.IntegerValueFitsEffectiveRange(wrapped, type))
        {
            folded = new SsaIntegerConstant(value, type);
            return false;
        }

        folded = new SsaIntegerConstant(wrapped, type);
        return true;
    }

    private static bool TryClampSignedInteger(StarkTypeSymbol type, BigInteger value, out SsaValue folded)
    {
        if (!TryGetIntegerBounds(type, out var min, out var max))
        {
            folded = new SsaIntegerConstant(value, type);
            return false;
        }

        var clamped = value < min ? min : value > max ? max : value;
        folded = new SsaIntegerConstant(clamped, type);
        return true;
    }

    private static bool TryFitInteger(BigInteger value, StarkTypeSymbol type, out BigInteger fitted)
    {
        fitted = value;
        if (!TryGetIntegerBounds(type, out var min, out var max))
        {
            return false;
        }

        if (value < min || value > max)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetIntegerBounds(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out min, out max);
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

    private static bool TryParseFloat(SsaFloatConstant floating, out double value)
    {
        return double.TryParse(
            floating.LiteralText,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string FormatFloat(double value, StarkTypeSymbol type)
    {
        var format = type.BitWidth == 32 ? "R" : "R";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static bool TryGetBoolConstant(ConstantState state, out bool value)
    {
        if (state.Kind == ConstantStateKind.Constant && state.Value is SsaBoolConstant boolean)
        {
            value = boolean.Value;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryExpandScalarZero(StarkTypeSymbol type, out SsaValue value)
    {
        switch (type.Kind)
        {
            case StarkTypeKind.Bool:
                value = new SsaBoolConstant(false);
                return true;
            case StarkTypeKind.Integer:
                value = new SsaIntegerConstant(BigInteger.Zero, type);
                return true;
            case StarkTypeKind.Float:
                value = new SsaFloatConstant(FormatFloat(0, type), type);
                return true;
            default:
                value = new SsaZeroInitializerValue(type);
                return false;
        }
    }

    private static bool ConstantValuesEqual(SsaValue left, SsaValue right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        return (left, right) switch
        {
            (SsaIntegerConstant a, SsaIntegerConstant b) => a.Value == b.Value,
            (SsaFloatConstant a, SsaFloatConstant b) => string.Equals(a.LiteralText, b.LiteralText, StringComparison.Ordinal),
            (SsaBoolConstant a, SsaBoolConstant b) => a.Value == b.Value,
            (SsaStringConstant a, SsaStringConstant b) => string.Equals(a.LiteralText, b.LiteralText, StringComparison.Ordinal),
            (SsaNullConstant, SsaNullConstant) => true,
            _ => EqualityComparer<SsaValue>.Default.Equals(left, right)
        };
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => new SsaValueInstruction(
                valueInstruction.ResultName,
                RewriteRValue(valueInstruction.Value, replacements),
                valueInstruction.Location,
                valueInstruction.ScopedNoAliasGroups,
                valueInstruction.LoopAccessGroups),
            SsaCallInstruction call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaIndirectCallInstruction call => call with
            {
                Target = RewriteValue(call.Target, replacements),
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaAllocateLocalInstruction allocateLocal => allocateLocal,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
            SsaStoreLocalInstruction storeLocal => new SsaStoreLocalInstruction(
                storeLocal.LocalName,
                storeLocal.LocalType,
                RewriteValue(storeLocal.Value, replacements),
                storeLocal.Location,
                storeLocal.WriteKind),
            SsaCopyMemoryInstruction copyMemory => new SsaCopyMemoryInstruction(
                RewriteValue(copyMemory.DestinationAddress, replacements),
                RewriteValue(copyMemory.SourceAddress, replacements),
                copyMemory.CopyType,
                copyMemory.TransferKind,
                copyMemory.Location,
                copyMemory.ScopedNoAliasGroups,
                copyMemory.LoopAccessGroups,
                copyMemory.WriteKind),
            SsaStoreIndirectInstruction storeIndirect => new SsaStoreIndirectInstruction(
                RewriteValue(storeIndirect.Address, replacements),
                storeIndirect.ValueType,
                RewriteValue(storeIndirect.Value, replacements),
                storeIndirect.Location,
                storeIndirect.ScopedNoAliasGroups,
                storeIndirect.LoopAccessGroups,
                storeIndirect.WriteKind),
            SsaStoreGlobalInstruction storeGlobal => new SsaStoreGlobalInstruction(
                storeGlobal.GlobalName,
                storeGlobal.GlobalType,
                RewriteValue(storeGlobal.Value, replacements)),
            _ => instruction
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return value switch
        {
            SsaUseRValue use => new SsaUseRValue(RewriteValue(use.Value, replacements)),
            SsaUnaryRValue unary => new SsaUnaryRValue(
                unary.Operator,
                RewriteValue(unary.Operand, replacements),
                unary.Type,
                unary.Text),
            SsaBinaryRValue binary => new SsaBinaryRValue(
                binary.Operator,
                RewriteValue(binary.Left, replacements),
                RewriteValue(binary.Right, replacements),
                binary.Type,
                binary.Text),
            SsaSelectRValue select => new SsaSelectRValue(
                RewriteValue(select.Condition, replacements),
                RewriteValue(select.WhenTrue, replacements),
                RewriteValue(select.WhenFalse, replacements),
                select.Type,
                select.Text),
            SsaCallRValue call => new SsaCallRValue(
                call.FunctionName,
                call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray(),
                call.Type,
                call.Text,
                call.IndirectArgumentLocalNames,
                call.SourceReturnType,
                call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()),
            SsaIndirectCallRValue indirectCall => RewriteIndirectCallRValue(indirectCall, replacements),
            SsaConvertRValue convert => new SsaConvertRValue(
                RewriteValue(convert.Operand, replacements),
                convert.TargetType,
                convert.Text),
            SsaExtractFieldRValue extractField => new SsaExtractFieldRValue(
                RewriteValue(extractField.Target, replacements),
                extractField.FieldName,
                extractField.FieldIndex,
                extractField.Type,
                extractField.Text),
            SsaInsertFieldRValue insertField => new SsaInsertFieldRValue(
                RewriteValue(insertField.Target, replacements),
                insertField.FieldName,
                insertField.FieldIndex,
                RewriteValue(insertField.Value, replacements),
                insertField.Type,
                insertField.Text),
            SsaExtractIndexRValue extractIndex => new SsaExtractIndexRValue(
                RewriteValue(extractIndex.Target, replacements),
                extractIndex.ElementIndex,
                extractIndex.OperationFamily,
                extractIndex.Type,
                extractIndex.Text),
            SsaInsertIndexRValue insertIndex => new SsaInsertIndexRValue(
                RewriteValue(insertIndex.Target, replacements),
                insertIndex.ElementIndex,
                insertIndex.OperationFamily,
                RewriteValue(insertIndex.Value, replacements),
                insertIndex.Type,
                insertIndex.Text),
            SsaMakeSliceFromLocalRValue makeSlice => makeSlice,
            SsaMakeSliceFromPointerRValue makeSlice => new SsaMakeSliceFromPointerRValue(
                RewriteValue(makeSlice.Pointer, replacements),
                RewriteValue(makeSlice.Length, replacements),
                makeSlice.Type,
                makeSlice.Text),
            SsaDynamicStorageAllocationRValue allocation => new SsaDynamicStorageAllocationRValue(
                RewriteValue(allocation.Capacity, replacements),
                allocation.Type,
                allocation.AllocationKind,
                allocation.Text),
            SsaDynamicStorageFreeRValue free => new SsaDynamicStorageFreeRValue(
                RewriteValue(free.Storage, replacements),
                free.Text),
            SsaHeapStorageFreeRValue free => new SsaHeapStorageFreeRValue(
                RewriteValue(free.Pointer, replacements),
                free.Text),
            SsaDynamicStorageReserveRValue reserve => new SsaDynamicStorageReserveRValue(
                RewriteValue(reserve.StorageAddress, replacements),
                reserve.StorageType,
                RewriteValue(reserve.AdditionalCapacity, replacements),
                reserve.AllocationKind,
                reserve.Text),
            SsaDynamicStorageTryReserveRValue reserve => new SsaDynamicStorageTryReserveRValue(
                RewriteValue(reserve.StorageAddress, replacements),
                reserve.StorageType,
                RewriteValue(reserve.AdditionalCapacity, replacements),
                reserve.AllocationKind,
                reserve.Text),
            SsaDynamicStorageTryReserveCapacityRValue reserve => new SsaDynamicStorageTryReserveCapacityRValue(
                RewriteValue(reserve.StorageAddress, replacements),
                reserve.StorageType,
                RewriteValue(reserve.TargetCapacity, replacements),
                reserve.AllocationKind,
                reserve.Text),
            SsaDynamicStorageMoveLastRValue moveLast => new SsaDynamicStorageMoveLastRValue(
                RewriteValue(moveLast.StorageAddress, replacements),
                moveLast.StorageType,
                moveLast.Type,
                moveLast.Text,
                moveLast.IsKnownNonEmpty),
            SsaDynamicStorageMoveAtRValue moveAt => new SsaDynamicStorageMoveAtRValue(
                RewriteValue(moveAt.StorageAddress, replacements),
                moveAt.StorageType,
                RewriteValue(moveAt.Index, replacements),
                moveAt.Type,
                moveAt.Text,
                moveAt.IsKnownInBounds),
            SsaLoadSliceElementRValue loadSlice => new SsaLoadSliceElementRValue(
                RewriteValue(loadSlice.Slice, replacements),
                RewriteValue(loadSlice.Index, replacements),
                loadSlice.Type,
                loadSlice.Text),
            SsaTextSliceRValue textSlice => new SsaTextSliceRValue(
                RewriteValue(textSlice.TextValue, replacements),
                RewriteValue(textSlice.Start, replacements),
                RewriteValue(textSlice.Length, replacements),
                textSlice.Type,
                textSlice.Text),
            SsaAddressOfLocalRValue addressOfLocal => addressOfLocal,
            SsaAddressOfParameterRValue addressOfParameter => addressOfParameter,
            SsaFieldAddressRValue fieldAddress => new SsaFieldAddressRValue(
                RewriteValue(fieldAddress.Address, replacements),
                fieldAddress.AggregateType,
                fieldAddress.FieldName,
                fieldAddress.FieldIndex,
                fieldAddress.Type,
                fieldAddress.Text),
            SsaElementAddressRValue elementAddress => new SsaElementAddressRValue(
                RewriteValue(elementAddress.Address, replacements),
                elementAddress.AggregateType,
                elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements),
                elementAddress.ConstantIndex,
                elementAddress.Type,
                elementAddress.Text),
            SsaSliceElementAddressRValue sliceElementAddress => new SsaSliceElementAddressRValue(
                RewriteValue(sliceElementAddress.Slice, replacements),
                RewriteValue(sliceElementAddress.Index, replacements),
                sliceElementAddress.Type,
                sliceElementAddress.Text),
            SsaLoadIndirectRValue loadIndirect => new SsaLoadIndirectRValue(
                RewriteValue(loadIndirect.Address, replacements),
                loadIndirect.Type,
                loadIndirect.Text),
            SsaLoadGlobalRValue loadGlobal => loadGlobal,
            SsaLoadLocalRValue loadLocal => loadLocal,
            _ => value
        };
    }

    private static SsaRValue RewriteIndirectCallRValue(
        SsaIndirectCallRValue indirectCall,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var target = RewriteValue(indirectCall.Target, replacements);
        var arguments = indirectCall.Arguments
            .Select(argument => RewriteValue(argument, replacements))
            .ToArray();

        return new SsaIndirectCallRValue(
            target,
            arguments,
            indirectCall.Type,
            indirectCall.Text,
            indirectCall.SourceReturnType,
            indirectCall.IndirectArgumentLocalNames,
            indirectCall.IndirectArgumentAddresses?
                .Select(address => address is null ? null : RewriteValue(address, replacements))
                .ToArray(),
            indirectCall.MayFree);
    }

    private static SsaValue RewriteValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (value is SsaValueReference reference
            && replacements.TryGetValue(reference.Name, out var replacement)
            && seen.Add(reference.Name))
        {
            value = replacement;
        }

        return value;
    }

    private static SsaTerminator FoldTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var rewrittenCondition = terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements);
        var rewrittenValue = terminator.Value is null ? null : RewriteValue(terminator.Value, replacements);
        var rewrittenCases = terminator.SwitchCases?.Select(switchCase => new SsaSwitchCase(
                switchCase.Label,
                switchCase.TargetBlockId,
                RewriteValue(switchCase.MatchValue, replacements)))
            .ToArray();

        if (terminator.Kind == SsaTerminatorKind.Branch
            && rewrittenCondition is not null
            && TryGetBoolConstant(ResolveConstantState(rewrittenCondition, EmptyConstantStates), out var branchCondition))
        {
            return PreserveLoopMetadata(
                terminator,
                new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    [terminator.Targets[branchCondition ? 0 : 1]]));
        }

        if (terminator.Kind == SsaTerminatorKind.Switch
            && rewrittenCondition is not null
            && TryMatchConstantSwitchTarget(
                ResolveConstantState(rewrittenCondition, EmptyConstantStates),
                new SsaTerminator(
                    terminator.Kind,
                    terminator.Targets,
                    Condition: rewrittenCondition,
                    SwitchCases: rewrittenCases,
                    DefaultTarget: terminator.DefaultTarget,
                    Location: terminator.Location,
                    BranchWeights: terminator.BranchWeights),
                out var targetBlockId))
        {
            return PreserveLoopMetadata(
                terminator,
                new SsaTerminator(SsaTerminatorKind.Goto, [targetBlockId]));
        }

        return new SsaTerminator(
            terminator.Kind,
            terminator.Targets,
            Condition: rewrittenCondition,
            Value: rewrittenValue,
            TailDirectCall: RewriteTailDirectCall(terminator.TailDirectCall, replacements),
            TailIndirectCall: RewriteTailIndirectCall(terminator.TailIndirectCall, replacements),
            SwitchCases: rewrittenCases,
            DefaultTarget: terminator.DefaultTarget,
            Location: terminator.Location,
            BranchWeights: terminator.BranchWeights,
            LoopBehavior: terminator.LoopBehavior,
            LoopContracts: terminator.LoopContracts,
            LoopAccessGroups: terminator.LoopAccessGroups);
    }

    private static ISsaDirectCallOperation? RewriteTailDirectCall(
        ISsaDirectCallOperation? call,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        if (call is null)
        {
            return null;
        }

        var arguments = call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray();
        var indirectArgumentAddresses = call.IndirectArgumentAddresses?
            .Select(address => address is null ? null : RewriteValue(address, replacements))
            .ToArray();

        return call switch
        {
            SsaCallInstruction instruction => instruction with
            {
                Arguments = arguments,
                IndirectArgumentAddresses = indirectArgumentAddresses
            },
            SsaCallRValue rValue => rValue with
            {
                Arguments = arguments,
                IndirectArgumentAddresses = indirectArgumentAddresses
            },
            _ => call
        };
    }

    private static ISsaIndirectCallOperation? RewriteTailIndirectCall(
        ISsaIndirectCallOperation? call,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        if (call is null)
        {
            return null;
        }

        var target = RewriteValue(call.Target, replacements);
        var arguments = call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray();
        var indirectArgumentAddresses = call.IndirectArgumentAddresses?
            .Select(address => address is null ? null : RewriteValue(address, replacements))
            .ToArray();

        return call switch
        {
            SsaIndirectCallInstruction instruction => instruction with
            {
                Target = target,
                Arguments = arguments,
                IndirectArgumentAddresses = indirectArgumentAddresses
            },
            SsaIndirectCallRValue rValue => rValue with
            {
                Target = target,
                Arguments = arguments,
                IndirectArgumentAddresses = indirectArgumentAddresses
            },
            _ => call
        };
    }

    private static SsaTerminator PreserveLoopMetadata(SsaTerminator source, SsaTerminator replacement)
    {
        return replacement with
        {
            LoopBehavior = source.LoopBehavior,
            LoopContracts = source.LoopContracts,
            LoopAccessGroups = source.LoopAccessGroups
        };
    }

    private enum ConstantStateKind
    {
        Unknown,
        Constant,
        Overdefined
    }

    private readonly record struct ConstantState(ConstantStateKind Kind, SsaValue? Value)
    {
        public static ConstantState Unknown => new(ConstantStateKind.Unknown, null);

        public static ConstantState Overdefined => new(ConstantStateKind.Overdefined, null);

        public static ConstantState FromValue(SsaValue value) => new(ConstantStateKind.Constant, value);
    }
}
