using System.Numerics;

namespace Stark.Compiler;

// Select native storage for a bounded SSA induction. Source-width values remain
// explicit conversions at observable uses. Proven division/remainder users can
// compute at native width without changing their source-width results.
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
        if (!function.HasBody || !function.SupportsDirectCodeGeneration) return function;
        var current = function;
        while (TryWiden(current, out var next)) current = next;
        return current;
    }

    private static bool TryWiden(SsaFunction function, out SsaFunction result)
    {
        result = function;
        var blocks = function.Blocks.ToDictionary(block => block.Id);
        var definitions = function.Blocks.SelectMany(block => block.Instructions.OfType<SsaValueInstruction>()
            .Select(instruction => (Block: block.Id, Instruction: instruction)))
            .ToDictionary(pair => pair.Instruction.ResultName, StringComparer.Ordinal);
        var names = definitions.Keys.Concat(function.Blocks.SelectMany(block => block.Phis.Select(phi => phi.ResultName)))
            .ToHashSet(StringComparer.Ordinal);
        string Fresh(string prefix)
        {
            var name = prefix;
            for (var suffix = 1; !names.Add(name); suffix++) name = prefix + suffix;
            return name;
        }

        foreach (var header in function.Blocks)
        {
            if (header.Terminator is not { Kind: SsaTerminatorKind.Branch, Condition: SsaValueReference condition, Targets.Count: 2 }
                || !definitions.TryGetValue(condition.Name, out var compareDefinition)
                || compareDefinition.Block != header.Id
                || compareDefinition.Instruction.Value is not SsaBinaryRValue
                { Operator: SsaBinaryOperator.LessThan, Left: SsaValueReference converted, Right: SsaIntegerConstant limit } comparison
                || limit.Value <= 0 || limit.Value >= (1 << 23)
                || !definitions.TryGetValue(converted.Name, out var convertDefinition)
                || convertDefinition.Block != header.Id
                || convertDefinition.Instruction.Value is not SsaConvertRValue
                { Operand: SsaValueReference counter, TargetType.BitWidth: 24, TargetType.IsUnsigned: false } conversion)
                continue;
            var phi = header.Phis.SingleOrDefault(candidate => candidate.ResultName == counter.Name);
            if (phi is not { Type.BitWidth: 24, Type.IsUnsigned: true, Incomings.Count: 2 }
                || phi.Type.RangeMin != 0 || phi.Type.RangeMax is null || phi.Type.RangeMax < limit.Value
                || function.Blocks.Count(block => block.Terminator.Targets.Contains(header.Id)) != 2)
                continue;

            var initialEdges = phi.Incomings.Where(incoming => incoming.Value is SsaIntegerConstant { Value.IsZero: true }).ToArray();
            if (initialEdges.Length != 1 || initialEdges[0].PredecessorBlockId == header.Id) continue;
            var initial = initialEdges[0];
            var back = phi.Incomings.Single(incoming => !ReferenceEquals(incoming, initial));
            if (back.Value is not SsaValueReference update
                || !definitions.TryGetValue(update.Name, out var updateDefinition)
                || updateDefinition.Block == header.Id
                || !blocks.TryGetValue(back.PredecessorBlockId, out var latch)
                || !latch.Terminator.Targets.Contains(header.Id)
                || updateDefinition.Instruction.Value is not SsaBinaryRValue
                { Operator: SsaBinaryOperator.Add, Left: SsaValueReference updateCounter, Right: SsaIntegerConstant step } increment
                || updateCounter.Name != counter.Name || step.Value != 1
                // Every increment must be guarded by counter < limit. Exits may
                // contain cleanup or nested control flow, but cannot reach it
                // again without passing through this header's true edge.
                || !CanReach(header.Terminator.Targets[0], updateDefinition.Block, header.Id, blocks)
                || CanReach(header.Terminator.Targets[1], updateDefinition.Block, header.Id, blocks))
                continue;

            var wideCounter = StarkTypeSymbols.Integer(32, 0, limit.Value, isUnsigned: true);
            var wideCompare = StarkTypeSymbols.Integer(32, conversion.TargetType.RangeMin, conversion.TargetType.RangeMax);
            var counterName = Fresh(counter.Name + "_native");
            var nextName = Fresh(update.Name + "_native");
            var compareName = Fresh(converted.Name + "_native");
            var counterValue = new SsaValueReference(counterName, wideCounter);
            var nextValue = new SsaValueReference(nextName, wideCounter);
            var remainderValues = new Dictionary<BigInteger, SsaValueReference>();
            var remainderPhis = new List<SsaPhi>();
            var remainderUpdates = new List<SsaInstruction>();

            SsaValue Remainder(BigInteger divisor, StarkTypeSymbol type, SourceLocation? location)
            {
                if (divisor == 1) return new SsaIntegerConstant(0, type);
                if (divisor > limit.Value) return counterValue;
                if (remainderValues.TryGetValue(divisor, out var existing)) return existing;

                // The primary induction starts at zero and advances once per
                // backedge. Thus (i + 1) % d is r + 1, resetting at d. These
                // bounds also prove that the added update cannot overflow.
                var name = Fresh(counter.Name + "_remainder");
                var value = new SsaValueReference(name, type);
                var incrementType = StarkTypeSymbols.Integer(32, 1, divisor, isUnsigned: true);
                var incrementName = Fresh(name + "_increment");
                var incrementValue = new SsaValueReference(incrementName, incrementType);
                var conditionName = Fresh(name + "_end");
                var nextRemainderName = Fresh(name + "_next");
                remainderPhis.Add(new SsaPhi(name, name, type,
                    [new(initial.PredecessorBlockId, new SsaIntegerConstant(0, type)),
                     new(back.PredecessorBlockId, new SsaValueReference(nextRemainderName, type))], location));
                remainderUpdates.Add(new SsaValueInstruction(incrementName, new SsaBinaryRValue(
                    SsaBinaryOperator.Add, value, new SsaIntegerConstant(1, wideCounter), incrementType,
                    "remainder induction increment"), location));
                remainderUpdates.Add(new SsaValueInstruction(conditionName, new SsaBinaryRValue(
                    SsaBinaryOperator.Equal, incrementValue, new SsaIntegerConstant(divisor, incrementType),
                    StarkTypeSymbols.Bool, "remainder induction boundary"), location));
                remainderUpdates.Add(new SsaValueInstruction(nextRemainderName, new SsaSelectRValue(
                    new SsaValueReference(conditionName, StarkTypeSymbols.Bool), new SsaIntegerConstant(0, type),
                    incrementValue, type, "bounded remainder induction"), location));
                remainderValues.Add(divisor, value);
                return value;
            }

            bool IsCounterAlias(SsaValue value)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (value is SsaValueReference reference && visited.Add(reference.Name)
                    && StarkTypeSymbols.TryGetEffectiveIntegerBounds(value.Type, out var min, out var max)
                    && min <= 0 && max >= limit.Value)
                {
                    if (reference.Name == counter.Name) return true;
                    if (!definitions.TryGetValue(reference.Name, out var definition)) return false;
                    switch (definition.Instruction.Value)
                    {
                        case SsaUseRValue use: value = use.Value; break;
                        case SsaConvertRValue convert: value = convert.Operand; break;
                        default: return false;
                    }
                }
                return false;
            }

            bool TryWidenArithmetic(SsaValueInstruction instruction, int blockId, List<SsaInstruction> instructions)
            {
                if (instruction.Value is not SsaBinaryRValue
                    { Operator: SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo,
                      Type.Kind: StarkTypeKind.Integer, Type.BitWidth: 24,
                      Left.Type.Kind: StarkTypeKind.Integer, Left.Type.BitWidth: 24,
                      Right: SsaIntegerConstant { Type.Kind: StarkTypeKind.Integer, Type.BitWidth: 24 } divisor } arithmetic
                    || divisor.Value <= 0
                    || !StarkTypeSymbols.IntegerValueFitsEffectiveRange(divisor.Value, divisor.Type)
                    || !StarkTypeSymbols.IntegerValueFitsEffectiveRange(divisor.Value,
                        StarkTypeSymbols.Integer(24, isUnsigned: arithmetic.Type.IsUnsigned))
                    || !IsCounterAlias(arithmetic.Left)
                    || !StarkTypeSymbols.TryGetEffectiveIntegerBounds(arithmetic.Type, out var min, out var max))
                    return false;
                var resultMax = arithmetic.Operator == SsaBinaryOperator.Divide
                    ? limit.Value / divisor.Value : BigInteger.Min(limit.Value, divisor.Value - 1);
                if (min > 0 || resultMax > max) return false;

                // The checked induction is nonnegative and fits both source
                // interpretations. A positive divisor gives the same value at
                // either width; retain the original result at every boundary.
                var nativeType = StarkTypeSymbols.Integer(32, 0, resultMax, isUnsigned: true);
                var nativeName = Fresh(instruction.ResultName + "_native");
                // Do not add per-iteration state for conditional uses or a
                // power-of-two remainder that can already use one native mask.
                var alwaysExecuted = blockId == header.Id
                    || !CanReach(header.Terminator.Targets[0], back.PredecessorBlockId, blockId, blocks);
                var simpleRemainder = divisor.Value == 1 || divisor.Value > limit.Value;
                var useInduction = alwaysExecuted && (divisor.Value & (divisor.Value - 1)) != 0;
                SsaRValue native = arithmetic.Operator == SsaBinaryOperator.Modulo && (simpleRemainder || useInduction)
                    ? new SsaUseRValue(Remainder(divisor.Value, nativeType, instruction.Location))
                    : arithmetic with
                    {
                        Left = counterValue,
                        Right = new SsaIntegerConstant(divisor.Value, StarkTypeSymbols.Integer(32, isUnsigned: true)),
                        Type = nativeType
                    };
                instructions.Add(instruction with { ResultName = nativeName, Value = native });
                instructions.Add(instruction with { Value = new SsaConvertRValue(
                    new SsaValueReference(nativeName, nativeType), arithmetic.Type, "source-width arithmetic result") });
                return true;
            }

            var arithmeticReplacements = new Dictionary<string, List<SsaInstruction>>(StringComparer.Ordinal);
            foreach (var definition in definitions.Values)
            {
                var replacement = new List<SsaInstruction>();
                if (TryWidenArithmetic(definition.Instruction, definition.Block, replacement))
                    arithmeticReplacements.Add(definition.Instruction.ResultName, replacement);
            }

            var newPhi = phi with { ResultName = counterName, Type = wideCounter, Incomings =
            [new(initial.PredecessorBlockId, new SsaIntegerConstant(0, wideCounter)), new(back.PredecessorBlockId, nextValue)] };
            result = function with { Blocks = function.Blocks.Select(block =>
            {
                var instructions = new List<SsaInstruction>();
                if (block.Id == header.Id)
                    instructions.Add(new SsaValueInstruction(counter.Name,
                        new SsaConvertRValue(counterValue, phi.Type, "source-width induction value"), phi.Location));
                foreach (var instruction in block.Instructions)
                {
                    if (ReferenceEquals(instruction, compareDefinition.Instruction))
                    {
                        instructions.Add(convertDefinition.Instruction with { ResultName = compareName,
                            Value = new SsaConvertRValue(counterValue, wideCompare, conversion.Text) });
                        instructions.Add(compareDefinition.Instruction with { Value = comparison with
                        { Left = new SsaValueReference(compareName, wideCompare), Right = new SsaIntegerConstant(limit.Value, wideCompare) } });
                    }
                    else if (ReferenceEquals(instruction, updateDefinition.Instruction))
                    {
                        instructions.Add(updateDefinition.Instruction with { ResultName = nextName,
                            Value = increment with { Left = counterValue, Right = new SsaIntegerConstant(1, wideCounter), Type = wideCounter } });
                        instructions.Add(updateDefinition.Instruction with
                        { Value = new SsaConvertRValue(nextValue, increment.Type, "source-width increment value") });
                    }
                    else if (instruction is SsaValueInstruction value
                        && arithmeticReplacements.TryGetValue(value.ResultName, out var replacement))
                        instructions.AddRange(replacement);
                    else instructions.Add(instruction);
                }
                if (block.Id == back.PredecessorBlockId) instructions.AddRange(remainderUpdates);
                return block with { Phis = (block.Id == header.Id ? remainderPhis : [])
                        .Concat(block.Phis.Select(candidate => ReferenceEquals(candidate, phi) ? newPhi : candidate)).ToArray(),
                    Instructions = instructions.ToArray() };
            }).ToArray() };
            return true;
        }
        return false;
    }

    private static bool CanReach(int start, int target, int barrier, IReadOnlyDictionary<int, SsaBasicBlock> blocks)
    {
        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        pending.Push(start);
        while (pending.TryPop(out var id))
        {
            if (id == barrier || !visited.Add(id)) continue;
            if (id == target) return true;
            if (blocks.TryGetValue(id, out var block))
                foreach (var successor in block.Terminator.Targets) pending.Push(successor);
        }
        return false;
    }
}
