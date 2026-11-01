using System.Numerics;

namespace Stark.Compiler;

// Reduce a private cyclic array's affine updates and their sum. The proof is
// pointwise over cells and bounds every source intermediate/checksum prefix;
// it does not rely on positive inputs, native integer widths or dead bytes
// being interchangeable with an escaping array.
internal sealed class SsaCyclicArraySumOptimizer
{
    public SsaIrModule Optimize(SsaIrModule module, SsaValueFactModel facts)
    {
        var changed = false;
        var functions = module.Functions.Select(function =>
        {
            if (!function.HasBody || !function.SupportsDirectCodeGeneration) return function;
            facts.Functions.TryGetValue(function.Name, out var functionFacts);
            var current = function;
            while (TryFold(current, functionFacts, out var next)) current = next;
            changed |= !ReferenceEquals(current, function);
            return current;
        }).ToArray();
        return changed ? module with { Functions = functions } : module;
    }

    private static bool TryFold(SsaFunction function, SsaFunctionFactModel? facts, out SsaFunction result)
    {
        result = function;
        var definitions = function.Blocks.SelectMany(b => b.Instructions.OfType<SsaValueInstruction>()
            .Select(i => (Block: b.Id, Instruction: i))).ToDictionary(p => p.Instruction.ResultName, StringComparer.Ordinal);
        foreach (var header in function.Blocks)
        {
            if (header.Phis.Count != 2
                || header.Terminator is not { Kind: SsaTerminatorKind.Branch, Targets.Count: 2,
                    Condition: SsaValueReference condition }
                || !definitions.TryGetValue(condition.Name, out var comparison)
                || comparison.Block != header.Id
                || comparison.Instruction.Value is not SsaBinaryRValue
                    { Operator: SsaBinaryOperator.LessThan, Right: SsaIntegerConstant { Value.Sign: > 0 } limit } guard
                || !header.Instructions.All(i => i is SsaValueInstruction v && IsPureInteger(v.Value))) continue;
            foreach (var counter in header.Phis)
            {
                if (!SsaBoundedLoopAnalysis.IsCounterAlias(guard.Left, counter, limit.Value, definitions)
                    || !SsaBoundedLoopAnalysis.TryGetUnitStep(function, header, counter, limit.Value, definitions,
                        out var initial, out var back, out _, out _)) continue;
                var body = function.Blocks.Single(b => b.Id == back.PredecessorBlockId);
                if (header.Terminator.Targets[0] != body.Id
                    || body.Phis.Count != 0
                    || body.Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 }
                    || body.Terminator.Targets[0] != header.Id
                    || function.Blocks.Count(b => b.Terminator.Targets.Contains(body.Id)) != 1) continue;
                var stores = body.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();
                if (stores.Length != 1 || stores[0].Address is not SsaValueReference address
                    || !definitions.TryGetValue(address.Name, out var addressDefinition)
                    || addressDefinition.Instruction.Value is not SsaElementAddressRValue element
                    || element.AggregateType is not { Kind: StarkTypeKind.FixedArray, FixedLength: > 0 and <= 256,
                        ElementType.Kind: StarkTypeKind.Integer } array
                    || element.Address is not SsaValueReference root
                    || !definitions.TryGetValue(root.Name, out var rootDefinition)
                    || rootDefinition.Instruction.Value is not SsaAddressOfLocalRValue local
                    || local.PointeeType != array) continue;
                var type = array.ElementType!;
                var sum = header.Phis.Single(p => !ReferenceEquals(p, counter));
                if (element.Type.Kind != StarkTypeKind.RawPointer || element.Type.ElementType != type
                    || sum.Type != type || stores[0].ValueType != type || stores[0].Value.Type != type
                    || sum.Incomings.Count != 2) continue;
                var sumEntry = sum.Incomings.SingleOrDefault(i => i.PredecessorBlockId == initial.PredecessorBlockId);
                var sumBack = sum.Incomings.SingleOrDefault(i => i.PredecessorBlockId == body.Id);
                if (sumEntry is null || sumBack?.Value is not SsaValueReference sumNext
                    || !TryConstant(sumEntry.Value, facts, out var initialSum)
                    || !definitions.TryGetValue(sumNext.Name, out var sumDefinition)
                    || sumDefinition.Block != body.Id
                    || sumDefinition.Instruction.Value is not SsaBinaryRValue
                        { Operator: SsaBinaryOperator.Add } addSum) continue;
                var store = stores[0];
                var storePosition = body.Instructions.ToList().IndexOf(store);
                bool Same(SsaValue a, SsaValue b) => Unwrap(a) == Unwrap(b);
                SsaValue Unwrap(SsaValue value)
                {
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (value is SsaValueReference r && visited.Add(r.Name)
                        && definitions.TryGetValue(r.Name, out var d) && d.Instruction.Value is SsaUseRValue use)
                        value = use.Value;
                    return value;
                }
                bool IsUpdated(SsaValue value)
                {
                    value = Unwrap(value);
                    return Same(value, store.Value)
                        || value is SsaValueReference r && definitions.TryGetValue(r.Name, out var d)
                        && d.Block == body.Id && d.Instruction.Value is SsaLoadIndirectRValue load
                        && load.Type == type && Same(load.Address, store.Address)
                        && body.Instructions.ToList().IndexOf(d.Instruction) > storePosition;
                }
                var sumValue = new SsaValueReference(sum.ResultName, sum.Type);
                if (!(Same(addSum.Left, sumValue) && IsUpdated(addSum.Right)
                        || Same(addSum.Right, sumValue) && IsUpdated(addSum.Left))
                    || body.Instructions.ToList().IndexOf(sumDefinition.Instruction) <= storePosition) continue;
                var oldLoads = body.Instructions.Take(storePosition).OfType<SsaValueInstruction>()
                    .Where(i => i.Value is SsaLoadIndirectRValue load && load.Type == type
                        && Same(load.Address, store.Address)).ToArray();
                if (oldLoads.Length != 1) continue;
                var oldValue = new SsaValueReference(oldLoads[0].ResultName, type);
                var index = element.ConstantIndex is int c ? new SsaIntegerConstant(c, type) : element.Index;
                if (index is null || !TryPeriod(index, out var period) || period > array.FixedLength) continue;
                bool TryPeriod(SsaValue value, out int periodValue)
                {
                    var conversions = new List<StarkTypeSymbol>();
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (value is SsaValueReference r && visited.Add(r.Name)
                        && definitions.TryGetValue(r.Name, out var d))
                    {
                        conversions.Add(value.Type);
                        if (d.Instruction.Value is SsaUseRValue use) { value = use.Value; continue; }
                        if (d.Instruction.Value is SsaConvertRValue convert)
                        { value = convert.Operand; continue; }
                        break;
                    }
                    periodValue = 0;
                    if (value is SsaIntegerConstant { Value.IsZero: true }) periodValue = 1;
                    else if (SsaBoundedLoopAnalysis.IsCounterAlias(value, counter, limit.Value - 1, definitions)
                        && limit.Value <= array.FixedLength) periodValue = array.FixedLength!.Value;
                    else if (value is SsaValueReference r && definitions.TryGetValue(r.Name, out var d)
                        && d.Instruction.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Modulo,
                            Right: SsaIntegerConstant { Value.Sign: > 0 } divisor } modulo
                        && divisor.Value <= array.FixedLength
                        && SsaBoundedLoopAnalysis.IsCounterAlias(modulo.Left, counter, limit.Value - 1, definitions))
                        periodValue = (int)divisor.Value;
                    var max = BigInteger.Min(periodValue - 1, limit.Value - 1);
                    return periodValue > 0 && SsaBoundedLoopAnalysis.Fits(value.Type, 0, max)
                        && conversions.All(t => SsaBoundedLoopAnalysis.Fits(t, 0, max));
                }
                bool IsIndex(SsaValue value) => TryPeriod(value, out var p) && p == period;
                var invariantInstructions = new List<SsaValueInstruction>();
                // Source-width seed conversions may be inside the body. Move
                // only immutable integer copies/conversions of invariant inputs.
                bool Invariant(SsaValue value, HashSet<string> active)
                {
                    if (value.Type.Kind != StarkTypeKind.Integer) return false;
                    if (value is SsaIntegerConstant) return true;
                    if (value is not SsaValueReference r || !active.Add(r.Name)) return false;
                    if (function.Parameters.Any(p => r.Name == "arg_" + p.Name)) return true;
                    if (!definitions.TryGetValue(r.Name, out var d)) return false;
                    if (d.Block != header.Id && d.Block != body.Id) return true;
                    var operand = d.Instruction.Value switch {
                        SsaUseRValue use => use.Value,
                        SsaConvertRValue convert => convert.Operand,
                        _ => null
                    };
                    if (operand is null || !Invariant(operand, active)) return false;
                    if (!invariantInstructions.Contains(d.Instruction)) invariantInstructions.Add(d.Instruction);
                    return true;
                }
                var oldCount = 0;
                var indexCount = 0;
                var seeds = new List<SsaValue>();
                bool Collect(SsaValue value, HashSet<string> active)
                {
                    value = Unwrap(value);
                    if (Same(value, oldValue)) { oldCount++; return true; }
                    if (IsIndex(value)) { indexCount++; return true; }
                    if (value.Type == type && Invariant(value, new(StringComparer.Ordinal)))
                    { seeds.Add(value); return true; }
                    if (value is not SsaValueReference r || !active.Add(r.Name)
                        || !definitions.TryGetValue(r.Name, out var d)
                        || d.Instruction.Value is not SsaBinaryRValue { Operator: SsaBinaryOperator.Add } add
                        || add.Type != type) return false;
                    var ok = Collect(add.Left, active) && Collect(add.Right, active);
                    active.Remove(r.Name);
                    return ok;
                }
                if (!Collect(store.Value, new(StringComparer.Ordinal)) || oldCount != 1 || indexCount > 1
                    || seeds.Count > 1) continue;
                var seed = seeds.Count == 1 ? seeds[0] : new SsaIntegerConstant(0, type);
                if (!TryRange(seed, facts, out var seedRange)
                    || !TryPrivateArray(function, body, initial.PredecessorBlockId, local.LocalName, array,
                        facts, definitions, out var cells)) continue;
                if (!body.Instructions.All(i => i == store || i is SsaValueInstruction v && (IsPureInteger(v.Value)
                    || v == rootDefinition.Instruction || v == addressDefinition.Instruction
                    || v.Value is SsaLoadIndirectRValue load && load.Type == type && Same(load.Address, store.Address)))) continue;

                var coefficient = BigInteger.Zero;
                var constant = initialSum;
                var prefixMin = initialSum;
                var prefixMax = initialSum;
                var safe = true;
                for (var j = 0; j < period && safe; j++)
                {
                    // Cell j is visited k times, contributing k*initial[j]
                    // + (seed+indexCount*j)*k*(k+1)/2, including a partial cycle.
                    var count = limit.Value / period + (j < limit.Value % period ? 1 : 0);
                    if (count.IsZero) continue;
                    var offset = indexCount * j;
                    var deltaMin = seedRange.Min + offset;
                    var deltaMax = seedRange.Max + offset;
                    var oldRange = new SsaIntegerRangeFact(
                        BigInteger.Min(cells[j], cells[j] + (count - 1) * deltaMin),
                        BigInteger.Max(cells[j], cells[j] + (count - 1) * deltaMax));
                    SsaIntegerRangeFact? Evaluate(SsaValue value, HashSet<string> active)
                    {
                        value = Unwrap(value);
                        if (Same(value, oldValue)) return oldRange;
                        if (IsIndex(value)) return new(j, j);
                        if (Same(value, seed)) return seedRange;
                        if (value is not SsaValueReference r || !active.Add(r.Name)
                            || !definitions.TryGetValue(r.Name, out var d)
                            || d.Instruction.Value is not SsaBinaryRValue { Operator: SsaBinaryOperator.Add } add)
                            return null;
                        var left = Evaluate(add.Left, active);
                        var right = Evaluate(add.Right, active);
                        active.Remove(r.Name);
                        if (left is null || right is null) return null;
                        var range = new SsaIntegerRangeFact(left.Min + right.Min, left.Max + right.Max);
                        return SsaBoundedLoopAnalysis.Fits(add.Type, range.Min, range.Max) ? range : null;
                    }
                    var updated = Evaluate(store.Value, new(StringComparer.Ordinal));
                    safe = SsaBoundedLoopAnalysis.Fits(type, oldRange.Min, oldRange.Max)
                        && updated is not null && SsaBoundedLoopAnalysis.Fits(type, updated.Min, updated.Max);
                    if (!safe) break;
                    // Any prefix lies between all possible negative terms and
                    // all possible positive terms; a fitting final sum alone
                    // does not establish the original checksum's safety.
                    prefixMin += count * BigInteger.Min(0, updated!.Min);
                    prefixMax += count * BigInteger.Max(0, updated.Max);
                    var triangular = count * (count + 1) / 2;
                    coefficient += triangular;
                    constant += count * cells[j] + offset * triangular;
                }
                if (!safe || !SsaBoundedLoopAnalysis.Fits(type, prefixMin, prefixMax)
                    || !SsaBoundedLoopAnalysis.Fits(addSum.Type, prefixMin, prefixMax)) continue;
                var names = definitions.Keys.Concat(function.Blocks.SelectMany(b => b.Phis.Select(p => p.ResultName)))
                    .Concat(function.Parameters.Select(p => "arg_" + p.Name))
                    .ToHashSet(StringComparer.Ordinal);
                string Fresh(string name)
                {
                    var candidate = name;
                    for (var suffix = 1; !names.Add(candidate); suffix++) candidate = name + suffix;
                    return candidate;
                }
                var emitted = invariantInstructions.Cast<SsaInstruction>().ToList();
                SsaValue final;
                if (seedRange.Min == seedRange.Max)
                {
                    var value = constant + coefficient * seedRange.Min;
                    if (!SsaBoundedLoopAnalysis.Fits(type, value, value)) continue;
                    final = new SsaIntegerConstant(value, type);
                }
                else
                {
                    if (!SsaBoundedLoopAnalysis.Fits(type, coefficient, coefficient)
                        || !SsaBoundedLoopAnalysis.Fits(type, constant, constant)
                        || !SsaBoundedLoopAnalysis.Fits(type, coefficient * seedRange.Min, coefficient * seedRange.Max)
                        || !SsaBoundedLoopAnalysis.Fits(type, constant + coefficient * seedRange.Min,
                            constant + coefficient * seedRange.Max)) continue;
                    var productName = Fresh(sum.ResultName + "_cyclic_product");
                    var totalName = Fresh(sum.ResultName + "_cyclic_total");
                    emitted.Add(new SsaValueInstruction(productName, new SsaBinaryRValue(SsaBinaryOperator.Multiply,
                        seed, new SsaIntegerConstant(coefficient, type), type, "proved cyclic array sum"), sum.Location));
                    emitted.Add(new SsaValueInstruction(totalName, new SsaBinaryRValue(SsaBinaryOperator.Add,
                        new SsaValueReference(productName, type), new SsaIntegerConstant(constant, type),
                        type, "proved cyclic array sum"), sum.Location));
                    final = new SsaValueReference(totalName, type);
                }
                emitted.Add(new SsaValueInstruction(counter.ResultName,
                    new SsaUseRValue(new SsaIntegerConstant(limit.Value, counter.Type)), counter.Location));
                emitted.Add(new SsaValueInstruction(sum.ResultName, new SsaUseRValue(final), sum.Location));
                result = function with { Blocks = function.Blocks.Where(b => b.Id != body.Id).Select(b => b.Id == header.Id
                    ? b with { Phis = [], Instructions = emitted.Concat(b.Instructions.Where(i =>
                            i is not SsaValueInstruction v || !invariantInstructions.Contains(v))).ToArray(),
                        Terminator = new SsaTerminator(SsaTerminatorKind.Goto, [header.Terminator.Targets[1]],
                            Location: header.Terminator.Location) } : b).ToArray() };
                return true;
            }
        }
        return false;
    }

    private static bool IsPureInteger(SsaRValue value) => value switch
    {
        SsaUseRValue use => use.Value.Type.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool,
        SsaConvertRValue convert => convert.Operand.Type.Kind == StarkTypeKind.Integer
            && convert.TargetType.Kind == StarkTypeKind.Integer,
        SsaBinaryRValue binary => binary.Type.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool
            && binary.Operator is SsaBinaryOperator.Add or SsaBinaryOperator.Modulo
                or SsaBinaryOperator.LessThan or SsaBinaryOperator.BitwiseAnd,
        _ => false
    };

    private static bool TryRange(SsaValue value, SsaFunctionFactModel? facts, out SsaIntegerRangeFact range)
    {
        if (value is SsaIntegerConstant constant)
            range = new(constant.Value, constant.Value);
        else if (value is SsaValueReference r && facts?.Values.TryGetValue(r.Name, out var fact) == true
            && fact.IntegerRangeKind == SsaFactLatticeKind.Known && fact.IntegerRange is { } known)
            range = known;
        else if (StarkTypeSymbols.TryGetEffectiveIntegerBounds(value.Type, out var min, out var max))
            range = new(min, max);
        else { range = null!; return false; }
        return SsaBoundedLoopAnalysis.Fits(value.Type, range.Min, range.Max);
    }

    private static bool TryConstant(SsaValue value, SsaFunctionFactModel? facts, out BigInteger constant)
    {
        constant = 0;
        if (!TryRange(value, facts, out var range) || range.Min != range.Max) return false;
        constant = range.Min;
        return true;
    }

    private static bool TryPrivateArray(SsaFunction function, SsaBasicBlock body, int entry,
        string name, StarkTypeSymbol type, SsaFunctionFactModel? facts,
        IReadOnlyDictionary<string, (int Block, SsaValueInstruction Instruction)> definitions,
        out BigInteger[] cells)
    {
        cells = new BigInteger[type.FixedLength!.Value];
        var instructions = function.Blocks.SelectMany(b => b.Instructions.Select((i, p) => (Block: b.Id, Position: p, Value: i))).ToArray();
        var allocations = instructions.Where(i => i.Value is SsaAllocateLocalInstruction a && a.LocalName == name).ToArray();
        var initializations = instructions.Where(i => i.Value is SsaStoreLocalInstruction s && s.LocalName == name).ToArray();
        if (allocations.Length != 1 || initializations.Length != 1
            || allocations[0].Value is not SsaAllocateLocalInstruction { StorageClass: "stack", LocalType: var allocated }
            || allocated != type || initializations[0].Block != entry
            || allocations[0].Block != entry || allocations[0].Position >= initializations[0].Position) return false;
        var initialization = initializations[0];
        foreach (var instruction in instructions)
        {
            if (instruction.Block == body.Id) continue;
            if (instruction.Value is SsaValueInstruction v)
            {
                if (v.Value is SsaAddressOfLocalRValue a && a.LocalName == name
                    || v.Value is SsaLoadLocalRValue l && l.LocalName == name) return false;
                if (v.Value is ISsaDirectCallOperation or ISsaIndirectCallOperation
                    && (instruction.Block != entry || instruction.Position >= initialization.Position)) return false;
            }
            else if (instruction.Value is not (SsaAllocateLocalInstruction or SsaStoreLocalInstruction
                or SsaLifetimeStartInstruction or SsaLifetimeEndInstruction)) return false;
        }
        if (function.Blocks.Any(b => b.Terminator.TailDirectCall is not null || b.Terminator.TailIndirectCall is not null)) return false;
        var writes = new List<SsaInsertIndexRValue>();
        var value = ((SsaStoreLocalInstruction)initialization.Value).Value;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (value is SsaValueReference reference && visited.Add(reference.Name)
            && definitions.TryGetValue(reference.Name, out var definition))
        {
            if (definition.Instruction.Value is SsaUseRValue use) value = use.Value;
            else if (definition.Instruction.Value is SsaInsertIndexRValue insert && insert.Type == type
                && insert.ElementIndex >= 0 && insert.ElementIndex < cells.Length)
            { writes.Add(insert); value = insert.Target; }
            else return false;
        }
        if (value is not SsaZeroInitializerValue || value.Type != type) return false;
        foreach (var write in writes.AsEnumerable().Reverse())
        {
            if (!TryConstant(write.Value, facts, out var constant)) return false;
            cells[write.ElementIndex] = constant;
        }
        return cells.All(c => SsaBoundedLoopAnalysis.Fits(type.ElementType!, c, c));
    }
}
