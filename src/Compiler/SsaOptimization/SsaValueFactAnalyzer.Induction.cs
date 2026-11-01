using System.Numerics;

namespace Stark.Compiler;

internal sealed partial class SsaValueFactAnalyzer
{
    private static IReadOnlySet<string> RefineInductiveNonNegativeFacts(
        SsaFunction function,
        Dictionary<string, SsaValueFacts> values,
        ISet<int> reachableBlocks)
    {
        var candidates = function.Blocks.Where(b => reachableBlocks.Contains(b.Id))
            .SelectMany(b => b.Phis)
            .Where(p => p.Type.Kind == StarkTypeKind.Integer
                && values[p.ResultName].IntegerRangeKind == SsaFactLatticeKind.Known
                && values[p.ResultName].IntegerRange is { Min.Sign: < 0, Max.Sign: >= 0 })
            .ToArray();
        var proven = new HashSet<string>(StringComparer.Ordinal);
        var instructions = function.Blocks.Where(b => reachableBlocks.Contains(b.Id))
            .SelectMany(b => b.Instructions).OfType<SsaValueInstruction>().ToArray();
        if (candidates.Length == 0 && !instructions.Any(i => i.Value is SsaBinaryRValue
                { Operator: SsaBinaryOperator.Subtract } && values[i.ResultName].IntegerRangeKind == SsaFactLatticeKind.Known
                && values[i.ResultName].IntegerRange is { Min.Sign: < 0, Max.Sign: >= 0 })) return proven;
        var phis = function.Blocks.Where(b => reachableBlocks.Contains(b.Id)).SelectMany(b => b.Phis)
            .ToDictionary(p => p.ResultName, p => p.Incomings.Where(i => reachableBlocks.Contains(i.PredecessorBlockId))
                .Select(i => i.Value).ToArray(), StringComparer.Ordinal);
        var definitions = instructions.ToDictionary(i => i.ResultName, i => i.Value, StringComparer.Ordinal);
        var unconditional = new NonNegativeProof(values, definitions, phis);
        foreach (var phi in candidates)
        {
            var incoming = phi.Incomings.Where(i => reachableBlocks.Contains(i.PredecessorBlockId)).ToArray();
            // A reachable SSA cycle needs an independently proved entry, and
            // every incoming value must preserve the hypothesis. Merge phis
            // must prove all their edges without another cyclic hypothesis.
            var induction = new NonNegativeProof(values, definitions, phis, phi.ResultName);
            if (!incoming.Any(i => unconditional.Proves(i.Value))
                || !incoming.All(i => induction.Proves(i.Value))) continue;
            values[phi.ResultName] = ApplyNonNegativeProof(values[phi.ResultName]);
            proven.Add(phi.ResultName);
        }

        // Relational facts must be recovered even if an earlier rewrite has
        // already preserved the phi's nonnegative range in its source type.
        foreach (var instruction in instructions)
        {
            var facts = values[instruction.ResultName];
            if (facts.IntegerRangeKind == SsaFactLatticeKind.Known
                && facts.IntegerRange is { Min.Sign: < 0, Max.Sign: >= 0 }
                && unconditional.Proves(new SsaValueReference(instruction.ResultName, instruction.Value.Type)))
            {
                values[instruction.ResultName] = ApplyNonNegativeProof(facts);
                proven.Add(instruction.ResultName);
            }
        }
        return proven;
    }

    private static SsaValueFacts ApplyNonNegativeProof(SsaValueFacts facts)
    {
        if (facts.IntegerRangeKind != SsaFactLatticeKind.Known
            || facts.IntegerRange is not { Min.Sign: < 0, Max.Sign: >= 0 } range) return facts;
        var refined = facts with { IntegerRange = new SsaIntegerRangeFact(BigInteger.Zero, range.Max) };
        if (!TryCreateKnownBitsForRange(facts.Type, refined.IntegerRange, out var bits)) return refined;
        if (facts.KnownBitsKind == SsaFactLatticeKind.Known && facts.KnownBits is { } existing)
        {
            bits = new SsaKnownBitsFact(bits.KnownZeroBits | existing.KnownZeroBits,
                bits.KnownOneBits | existing.KnownOneBits);
        }
        return ApplyKnownBits(refined, bits);
    }

    /// <summary>
    /// Proves signs and simple orders of immutable SSA expressions. Interval
    /// transfers bound every arithmetic operation before relational reasoning;
    /// no wrap, overflow or opaque cyclic relation is assumed away.
    /// </summary>
    private sealed class NonNegativeProof(
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValue[]> phis,
        string? hypothesis = null)
    {
        private readonly HashSet<string> _activeValues = new(StringComparer.Ordinal);
        private readonly HashSet<(SsaValue, SsaValue)> _activeOrders = [];
        private readonly Dictionary<string, bool> _signs = new(StringComparer.Ordinal);
        private readonly Dictionary<(SsaValue, SsaValue), bool> _orders = [];

        public bool Proves(SsaValue value)
        {
            // Facts can strengthen between separate proof requests. Memoize
            // only within one request, including repeated shared expressions.
            _signs.Clear();
            _orders.Clear();
            return ProvesCore(value);
        }

        private bool ProvesCore(SsaValue value)
        {
            if (_activeValues.Count + _activeOrders.Count >= 128) return false;
            if (Range(value) is { Min.Sign: >= 0 }) return true;
            if (value is not SsaValueReference reference) return false;
            if (reference.Name == hypothesis) return true;
            if (_signs.TryGetValue(reference.Name, out var known)) return known;
            if (!_activeValues.Add(reference.Name)) return false;
            try
            {
                if (!definitions.TryGetValue(reference.Name, out var definition))
                {
                    var merged = phis.TryGetValue(reference.Name, out var incoming) && incoming.Length != 0
                        && incoming.All(ProvesCore);
                    _signs[reference.Name] = merged;
                    return merged;
                }
                var proven = definition switch
                {
                    SsaUseRValue use => ProvesCore(use.Value),
                    SsaConvertRValue convert when IsLossless(convert) => ProvesCore(convert.Operand),
                    SsaSelectRValue select => ProvesCore(select.WhenTrue) && ProvesCore(select.WhenFalse),
                    SsaBinaryRValue binary when Fits(binary) => binary.Operator switch
                    {
                        SsaBinaryOperator.Add or SsaBinaryOperator.Multiply => ProvesCore(binary.Left) && ProvesCore(binary.Right),
                        SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo =>
                            Range(binary.Right) is { Min.Sign: > 0 } && ProvesCore(binary.Left),
                        SsaBinaryOperator.Subtract => LessOrEqual(binary.Right, binary.Left),
                        _ => false
                    },
                    _ => false
                };
                _signs[reference.Name] = proven;
                return proven;
            }
            finally { _activeValues.Remove(reference.Name); }
        }

        private bool LessOrEqual(SsaValue left, SsaValue right)
        {
            if (_activeValues.Count + _activeOrders.Count >= 128) return false;
            left = Unwrap(left);
            right = Unwrap(right);
            if (left.Equals(right)) return true;
            if (Range(left) is { } leftRange && Range(right) is { } rightRange
                && leftRange.Max <= rightRange.Min) return true;
            var pair = (left, right);
            if (_orders.TryGetValue(pair, out var known)) return known;
            if (!_activeOrders.Add(pair)) return false;
            bool Remember(bool result) { _orders[pair] = result; return result; }
            try
            {
                if (Definition(right) is SsaBinaryRValue greater && Fits(greater))
                {
                    if (greater.Operator == SsaBinaryOperator.Add
                        && (ProvesCore(greater.Left) && LessOrEqual(left, greater.Right)
                            || ProvesCore(greater.Right) && LessOrEqual(left, greater.Left))) return Remember(true);
                    if (greater.Operator == SsaBinaryOperator.Multiply
                        && (Range(greater.Left) is { Min.Sign: > 0 } && ProvesCore(greater.Right) && LessOrEqual(left, greater.Right)
                            || Range(greater.Right) is { Min.Sign: > 0 } && ProvesCore(greater.Left) && LessOrEqual(left, greater.Left))) return Remember(true);
                }
                // For a nonnegative integer and a divisor >= 1, x/d <= x.
                return Remember(Definition(left) is SsaBinaryRValue { Operator: SsaBinaryOperator.Divide } lesser
                    && Fits(lesser) && Range(lesser.Right) is { Min.Sign: > 0 } && ProvesCore(lesser.Left)
                    && LessOrEqual(lesser.Left, right));
            }
            finally { _activeOrders.Remove(pair); }
        }

        private SsaRValue? Definition(SsaValue value) => value is SsaValueReference reference
            && definitions.TryGetValue(reference.Name, out var definition) ? definition : null;

        private SsaIntegerRangeFact? Range(SsaValue value)
        {
            var facts = AnalyzeValue(string.Empty, value, values);
            return facts.IntegerRangeKind == SsaFactLatticeKind.Known ? facts.IntegerRange : null;
        }

        private bool Fits(SsaBinaryRValue binary) =>
            binary.Operator is SsaBinaryOperator.Add or SsaBinaryOperator.Subtract or SsaBinaryOperator.Multiply
                or SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo
            && TryAnalyzeIntegerBinary(binary, AnalyzeValue(string.Empty, binary.Left, values),
                AnalyzeValue(string.Empty, binary.Right, values), out var mathematical)
            && TryGetIntegerTypeRange(binary.Type, out var typeRange)
            && mathematical.Min >= typeRange.Min && mathematical.Max <= typeRange.Max;

        private bool IsLossless(SsaConvertRValue convert) => Range(convert.Operand) is { } source
            && TryGetIntegerTypeRange(convert.TargetType, out var target)
            && source.Min >= target.Min && source.Max <= target.Max;

        private SsaValue Unwrap(SsaValue value)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (value is SsaValueReference reference && seen.Add(reference.Name))
            {
                switch (Definition(value))
                {
                    case SsaUseRValue use: value = use.Value; break;
                    case SsaConvertRValue convert when IsLossless(convert): value = convert.Operand; break;
                    default: return value;
                }
            }
            return value;
        }
    }
}
