namespace Stark.Compiler;

internal sealed partial class SsaValueFactAnalyzer
{
    private static IReadOnlyDictionary<string, SsaFunction> CollectSimpleIntegerBodies(SsaIrModule module) =>
        module.Functions.Where(f => f.HasBody && f.SupportsDirectCodeGeneration
            && f.ReturnType.Kind == StarkTypeKind.Integer
            && f.Parameters.All(p => p.Type.Kind == StarkTypeKind.Integer)
            && f.Blocks is [{ Phis.Count: 0, Terminator.Kind: SsaTerminatorKind.Return,
                Terminator.Value.Type.Kind: StarkTypeKind.Integer } block]
            && block.Id == f.EntryBlockId && block.Instructions.Count <= 128
            && block.Instructions.All(i => i is SsaValueInstruction value
                && value.Value.Type.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool
                && value.Value is SsaUseRValue or SsaUnaryRValue or SsaBinaryRValue or SsaConvertRValue or SsaSelectRValue))
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

    private static bool TryAnalyzeSimpleIntegerCall(
        string valueName,
        SsaCallRValue call,
        IReadOnlyDictionary<string, SsaValueFacts> callerValues,
        IReadOnlyDictionary<string, SsaFunction>? integerBodies,
        out SsaValueFacts facts,
        Func<SsaValue, bool>? provesNonNegative = null)
    {
        facts = null!;
        if (integerBodies is null || call.Type.Kind != StarkTypeKind.Integer
            || !integerBodies.TryGetValue(call.FunctionName, out var function)
            || call.Type != function.ReturnType || call.Arguments.Count != function.Parameters.Count
            || call.IndirectArgumentLocalNames?.Any(n => n is not null) == true
            || call.IndirectArgumentAddresses?.Any(a => a is not null) == true)
            return false;

        var values = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        for (var index = 0; index < function.Parameters.Count; index++)
        {
            var parameter = function.Parameters[index];
            var argument = call.Arguments[index];
            if (argument.Type.Kind != StarkTypeKind.Integer
                || argument.Type.BitWidth != parameter.Type.BitWidth
                || argument.Type.IsUnsigned != parameter.Type.IsUnsigned
                || !TryGetIntegerTypeRange(parameter.Type, out var parameterRange)) return false;
            var name = $"arg_{parameter.Name}";
            var argumentFacts = AnalyzeValue(name, argument, callerValues);
            if (provesNonNegative?.Invoke(argument) == true)
                argumentFacts = ApplyNonNegativeProof(argumentFacts);
            if (argumentFacts.IntegerRangeKind != SsaFactLatticeKind.Known
                || argumentFacts.IntegerRange is not { } range
                || range.Min < parameterRange.Min || range.Max > parameterRange.Max) return false;
            values.Add(name, RenameFacts(name, argumentFacts, parameter.Type));
        }

        // The admitted body is an acyclic sequence of scalar expressions with
        // no calls or memory operations. Reuse the authoritative transfers;
        // contextual arguments never change the callee's global signature.
        var block = function.Blocks[0];
        var reachable = new HashSet<int> { block.Id };
        foreach (var instruction in block.Instructions.Cast<SsaValueInstruction>())
        {
            if (instruction.Value is SsaBinaryRValue binary
                && binary.Operator is SsaBinaryOperator.Add or SsaBinaryOperator.Subtract or SsaBinaryOperator.Multiply
                    or SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo or SsaBinaryOperator.ShiftLeft
                && (!TryAnalyzeIntegerBinary(binary, AnalyzeValue(string.Empty, binary.Left, values),
                        AnalyzeValue(string.Empty, binary.Right, values), out var mathematical)
                    || !TryGetIntegerTypeRange(binary.Type, out var target)
                    || mathematical.Min < target.Min || mathematical.Max > target.Max)) return false;
            if (instruction.Value is SsaUnaryRValue { Operator: SsaUnaryOperator.Negate } unary
                && (AnalyzeValue(string.Empty, unary.Operand, values).IntegerRange is not { } operand
                    || !TryGetIntegerTypeRange(unary.Type, out var unaryTarget)
                    || -operand.Max < unaryTarget.Min || -operand.Min > unaryTarget.Max)) return false;
            values[instruction.ResultName] = AnalyzeRValue(instruction.ResultName, instruction.Value,
                values, string.Empty, function, reachable);
        }
        var returned = AnalyzeConvert(valueName,
            new SsaConvertRValue(block.Terminator.Value!, call.Type, "contextual integer return"), values);
        if (returned.IntegerRangeKind != SsaFactLatticeKind.Known) return false;
        facts = returned;
        return true;
    }
}
