namespace Stark.Compiler;

// Widen a private, bounded induction counter so the backend need not legalize
// an i24 loop counter on every iteration. All other shapes remain untouched.
internal sealed class SsaLoopCounterWidening
{
    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions.Select(function =>
        {
            var result = OptimizeFunction(function);
            changed |= !ReferenceEquals(result, function);
            return result;
        }).ToArray();
        return changed ? module with { Functions = functions } : module;
    }

    private static SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration
            || function.Blocks.Any(block => block.Instructions.Any(instruction =>
                instruction is not SsaValueInstruction { Value: SsaBinaryRValue or SsaConvertRValue or SsaCallRValue or SsaSelectRValue or SsaUnaryRValue or SsaUseRValue })))
        {
            return function;
        }

        var current = function;
        while (TryWiden(current, out var next))
        {
            current = next;
        }
        return current;
    }

    private static bool TryWiden(SsaFunction function, out SsaFunction result)
    {
        result = function;
        var blocks = function.Blocks.ToDictionary(block => block.Id);
        foreach (var header in function.Blocks)
        {
            if (header.Terminator is not { Kind: SsaTerminatorKind.Branch, Condition: SsaValueReference condition, Targets.Count: 2 }
                || !blocks.TryGetValue(header.Terminator.Targets[0], out var body)
                || body.Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 }
                || body.Terminator.Targets[0] != header.Id
                || function.Blocks.Count(block => block.Terminator.Targets.Contains(header.Id)) != 2)
                continue;

            var comparisonInstruction = header.Instructions.OfType<SsaValueInstruction>().SingleOrDefault(i => i.ResultName == condition.Name);
            if (comparisonInstruction?.Value is not SsaBinaryRValue { Operator: SsaBinaryOperator.LessThan, Left: SsaValueReference converted, Right: SsaIntegerConstant limit } comparison
                || limit.Value <= 0 || limit.Value >= (1 << 23))
                continue;
            var conversionInstruction = header.Instructions.OfType<SsaValueInstruction>().SingleOrDefault(i => i.ResultName == converted.Name);
            if (conversionInstruction?.Value is not SsaConvertRValue { Operand: SsaValueReference counter, TargetType.BitWidth: 24, TargetType.IsUnsigned: false } conversion)
                continue;
            var phi = header.Phis.SingleOrDefault(p => p.ResultName == counter.Name);
            if (phi is not { Type.BitWidth: 24, Type.IsUnsigned: true, Incomings.Count: 2 }
                || phi.Type.RangeMin != 0 || phi.Type.RangeMax is null || phi.Type.RangeMax < limit.Value)
                continue;
            var initial = phi.Incomings.SingleOrDefault(i => i.PredecessorBlockId != body.Id);
            var back = phi.Incomings.SingleOrDefault(i => i.PredecessorBlockId == body.Id);
            if (initial?.Value is not SsaIntegerConstant { Value.IsZero: true }
                || back?.Value is not SsaValueReference update
                || !blocks.TryGetValue(initial.PredecessorBlockId, out var preheader)
                || preheader.Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 }
                || preheader.Terminator.Targets[0] != header.Id)
                continue;
            var updateInstruction = body.Instructions.OfType<SsaValueInstruction>().SingleOrDefault(i => i.ResultName == update.Name);
            if (updateInstruction?.Value is not SsaBinaryRValue { Operator: SsaBinaryOperator.Add, Left: SsaValueReference updateCounter, Right: SsaIntegerConstant step } increment
                || updateCounter.Name != counter.Name || step.Value != 1)
                continue;

            // No other use may observe the changed width, including the exit value.
            var uses = CountUses(function);
            if (uses.GetValueOrDefault(counter.Name) != 2 || uses.GetValueOrDefault(converted.Name) != 1 || uses.GetValueOrDefault(update.Name) != 1)
                continue;

            var wideCounter = StarkTypeSymbols.Integer(32, 0, phi.Type.RangeMax, isUnsigned: true);
            var wideCompare = StarkTypeSymbols.Integer(32);
            var newPhi = phi with { Type = wideCounter, Incomings = phi.Incomings.Select(i => i with { Value = i.Value switch
            {
                SsaIntegerConstant n => new SsaIntegerConstant(n.Value, wideCounter),
                SsaValueReference v => new SsaValueReference(v.Name, wideCounter),
                _ => i.Value
            } }).ToArray() };
            var newConversion = conversionInstruction with { Value = new SsaConvertRValue(new SsaValueReference(counter.Name, wideCounter), wideCompare, conversion.Text) };
            var newComparison = comparisonInstruction with { Value = comparison with { Left = new SsaValueReference(converted.Name, wideCompare), Right = new SsaIntegerConstant(limit.Value, wideCompare) } };
            var newUpdate = updateInstruction with { Value = new SsaBinaryRValue(increment.Operator, new SsaValueReference(counter.Name, wideCounter), new SsaIntegerConstant(1, wideCounter), wideCounter, increment.Text) };
            result = function with { Blocks = function.Blocks.Select(block => block.Id == header.Id
                ? block with { Phis = block.Phis.Select(p => p.ResultName == phi.ResultName ? newPhi : p).ToArray(), Instructions = block.Instructions.Select(i => ReferenceEquals(i, conversionInstruction) ? newConversion : ReferenceEquals(i, comparisonInstruction) ? newComparison : i).ToArray() }
                : block.Id == body.Id
                    ? block with { Instructions = block.Instructions.Select(i => ReferenceEquals(i, updateInstruction) ? newUpdate : i).ToArray() }
                    : block).ToArray() };
            return true;
        }
        return false;
    }

    private static Dictionary<string, int> CountUses(SsaFunction function)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(SsaValue value)
        {
            if (value is SsaValueReference reference)
                uses[reference.Name] = uses.GetValueOrDefault(reference.Name) + 1;
        }
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
                foreach (var incoming in phi.Incomings) Count(incoming.Value);
            foreach (var instruction in block.Instructions)
                foreach (var operand in SsaCleanupOptimizer.EnumerateInstructionOperands(instruction)) Count(operand);
            foreach (var operand in SsaCleanupOptimizer.EnumerateTerminatorOperands(block.Terminator)) Count(operand);
        }
        return uses;
    }
}
