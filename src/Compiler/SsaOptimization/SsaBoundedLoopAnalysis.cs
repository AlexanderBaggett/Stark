using System.Numerics;

namespace Stark.Compiler;

internal static class SsaBoundedLoopAnalysis
{
    internal static bool Fits(StarkTypeSymbol type, BigInteger min, BigInteger max) =>
        type.Kind == StarkTypeKind.Integer
        && StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out var typeMin, out var typeMax)
        && min >= typeMin && max <= typeMax;

    internal static bool IsCounterAlias(SsaValue value, SsaPhi counter, BigInteger max,
        IReadOnlyDictionary<string, (int Block, SsaValueInstruction Instruction)> definitions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (value is SsaValueReference reference && visited.Add(reference.Name)
            && Fits(value.Type, 0, max))
        {
            if (reference.Name == counter.ResultName) return true;
            if (!definitions.TryGetValue(reference.Name, out var definition)) return false;
            switch (definition.Instruction.Value)
            {
                case SsaUseRValue use: value = use.Value; break;
                case SsaConvertRValue convert when Fits(convert.Operand.Type, 0, max):
                    value = convert.Operand; break;
                default: return false;
            }
        }
        return false;
    }

    internal static bool TryGetUnitStep(SsaFunction function, SsaBasicBlock header, SsaPhi counter,
        BigInteger limit,
        IReadOnlyDictionary<string, (int Block, SsaValueInstruction Instruction)> definitions,
        out SsaPhiIncoming initial, out SsaPhiIncoming back,
        out (int Block, SsaValueInstruction Instruction) updateDefinition, out SsaBinaryRValue increment)
    {
        initial = back = null!;
        updateDefinition = default;
        increment = null!;
        if (limit <= 0 || counter.Incomings.Count != 2 || !Fits(counter.Type, 0, limit)
            || header.Terminator is not { Kind: SsaTerminatorKind.Branch, Targets.Count: 2 }
            || function.Blocks.Count(b => b.Terminator.Targets.Contains(header.Id)) != 2) return false;
        var entries = counter.Incomings.Where(i => i.Value is SsaIntegerConstant { Value.IsZero: true }).ToArray();
        if (entries.Length != 1 || entries[0].PredecessorBlockId == header.Id) return false;
        var entry = entries[0];
        initial = entry;
        back = counter.Incomings.Single(i => !ReferenceEquals(i, entry));
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        if (back.Value is not SsaValueReference update
            || !definitions.TryGetValue(update.Name, out updateDefinition)
            || updateDefinition.Block == header.Id
            || !blocks.TryGetValue(back.PredecessorBlockId, out var latch)
            || !latch.Terminator.Targets.Contains(header.Id)
            || updateDefinition.Instruction.Value is not SsaBinaryRValue
                { Operator: SsaBinaryOperator.Add, Right: SsaIntegerConstant { Value.IsOne: true } } add
            || !IsCounterAlias(add.Left, counter, limit - 1, definitions)
            || !Fits(add.Type, 1, limit)
            || !CanReach(header.Terminator.Targets[0], updateDefinition.Block, header.Id, blocks)
            || CanReach(header.Terminator.Targets[1], updateDefinition.Block, header.Id, blocks)) return false;
        increment = add;
        return true;
    }

    internal static bool CanReach(int start, int target, int barrier, IReadOnlyDictionary<int, SsaBasicBlock> blocks)
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
