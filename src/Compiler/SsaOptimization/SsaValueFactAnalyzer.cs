using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaValueFactAnalyzer
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> _directCallParameterEffects;

    public SsaValueFactAnalyzer(
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>? directCallParameterEffects = null)
    {
        _directCallParameterEffects = directCallParameterEffects
                                      ?? new Dictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>(StringComparer.Ordinal);
    }

    public SsaValueFactModel Analyze(SsaIrModule module)
    {
        var functions = module.Functions
            .Where(static function => function.HasBody && function.SupportsDirectCodeGeneration)
            .Select(function => AnalyzeFunction(module.ModuleName, function))
            .ToDictionary(static function => function.FunctionName, StringComparer.Ordinal);

        return new SsaValueFactModel(module.ModuleName, functions);
    }

    private SsaFunctionFactModel AnalyzeFunction(string moduleName, SsaFunction function)
    {
        var values = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        var reachableBlockIds = FindReachableBlockIds(function);

        foreach (var parameter in function.Parameters)
        {
            values[$"arg_{parameter.Name}"] = CreateParameterFacts($"arg_{parameter.Name}", parameter, function.Parameters);
        }

        foreach (var block in function.Blocks)
        {
            if (!reachableBlockIds.Contains(block.Id))
            {
                continue;
            }

            foreach (var phi in block.Phis)
            {
                values[phi.ResultName] = CreateTypeFacts(phi.ResultName, phi.Type);
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                values[valueInstruction.ResultName] = CreateTypeFacts(
                    valueInstruction.ResultName,
                    valueInstruction.Value.Type);
            }
        }

        RefineFacts(moduleName, function, values, reachableBlockIds, _directCallParameterEffects);
        var blockEntryFacts = AnalyzeBlockEntryFacts(function, values, reachableBlockIds);
        var blockExitFacts = AnalyzeBlockExitFacts(function, values, blockEntryFacts, reachableBlockIds);
        return new SsaFunctionFactModel(function.Name, values, blockEntryFacts, blockExitFacts);
    }

    private static HashSet<int> FindReachableBlockIds(SsaFunction function)
    {
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var reachable = new HashSet<int>();
        var worklist = new Stack<int>();
        worklist.Push(function.EntryBlockId);

        while (worklist.Count != 0)
        {
            var blockId = worklist.Pop();
            if (!reachable.Add(blockId)
                || !blocksById.TryGetValue(blockId, out var block))
            {
                continue;
            }

            foreach (var target in EnumerateTerminatorTargets(block.Terminator))
            {
                if (!reachable.Contains(target))
                {
                    worklist.Push(target);
                }
            }
        }

        return reachable;
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> AnalyzeBlockEntryFacts(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<int> reachableBlockIds)
    {
        var definitions = CollectValueDefinitions(function);
        var incomingFacts = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new List<IReadOnlyDictionary<string, SsaValueFacts>>());

        foreach (var block in function.Blocks)
        {
            if (!reachableBlockIds.Contains(block.Id))
            {
                continue;
            }

            foreach (var target in EnumerateTerminatorTargets(block.Terminator))
            {
                if (!reachableBlockIds.Contains(target)
                    || !incomingFacts.TryGetValue(target, out var edges))
                {
                    continue;
                }

                edges.Add(InferEdgeFacts(block.Terminator, target, definitions, values));
            }
        }

        var result = new Dictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>();
        foreach (var (blockId, edgeFacts) in incomingFacts)
        {
            if (edgeFacts.Count == 0)
            {
                continue;
            }

            var joined = JoinEdgeFacts(edgeFacts, values);
            if (joined.Count != 0)
            {
                result[blockId] = joined;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> AnalyzeBlockExitFacts(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> blockEntryFacts,
        ISet<int> reachableBlockIds)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>();
        foreach (var block in function.Blocks)
        {
            if (!reachableBlockIds.Contains(block.Id))
            {
                continue;
            }

            var exitFacts = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
            if (blockEntryFacts.TryGetValue(block.Id, out var entryFacts))
            {
                foreach (var (valueName, facts) in entryFacts)
                {
                    if (HasValueFactPayload(facts))
                    {
                        exitFacts[valueName] = facts;
                    }
                }
            }

            foreach (var phi in block.Phis)
            {
                AddBlockLocalValueFact(phi.ResultName);
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                AddBlockLocalValueFact(valueInstruction.ResultName);
            }

            if (exitFacts.Count != 0)
            {
                result[block.Id] = exitFacts;
            }

            void AddBlockLocalValueFact(string valueName)
            {
                if (values.TryGetValue(valueName, out var facts)
                    && HasValueFactPayload(facts))
                {
                    exitFacts[valueName] = facts;
                }
            }
        }

        return result;
    }

    private static bool HasValueFactPayload(SsaValueFacts facts)
    {
        return facts.IntegerRangeKind != SsaFactLatticeKind.Unknown
            || facts.KnownBitsKind != SsaFactLatticeKind.Unknown
            || facts.BooleanKind != SsaFactLatticeKind.Unknown
            || facts.Nullability != SsaNullabilityFactKind.Unknown
            || facts.PointerAlignmentKind != SsaFactLatticeKind.Unknown
            || facts.LengthKind != SsaFactLatticeKind.Unknown
            || facts.CapacityKind != SsaFactLatticeKind.Unknown
            || facts.InitializedPrefixKind != SsaFactLatticeKind.Unknown
            || facts.TextLiteralPayloadKind != SsaFactLatticeKind.Unknown
            || facts.BoundedRawPointerRegionKind != SsaFactLatticeKind.Unknown
            || facts.DynamicStorageRegionKind != SsaFactLatticeKind.Unknown;
    }

    private static Dictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);

        foreach (var instruction in function.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaValueInstruction>())
        {
            definitions[instruction.ResultName] = instruction.Value;
        }

        return definitions;
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

    internal static IReadOnlyDictionary<string, SsaValueFacts> InferEdgeFacts(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        }

        bool branchWhenTrue;
        if (target == terminator.Targets[0])
        {
            branchWhenTrue = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchWhenTrue = false;
        }
        else
        {
            return new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        }

        return TryInferComparisonFacts(
            terminator.Condition,
            branchWhenTrue,
            definitions,
            values,
            out var facts)
            ? facts
            : new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
    }

    private static bool TryInferComparisonFacts(
        SsaValue condition,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> facts)
    {
        facts = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        if (condition is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool } comparison)
        {
            return false;
        }

        if (TryInferReferenceConstantComparisonFacts(
                comparison.Left,
                comparison.Operator,
                comparison.Right,
                branchWhenTrue,
                definitions,
                values,
                out facts)
            || TryMirrorComparisonOperator(comparison.Operator, out var mirroredOperator)
            && TryInferReferenceConstantComparisonFacts(
                comparison.Right,
                mirroredOperator,
                comparison.Left,
                branchWhenTrue,
                definitions,
                values,
                out facts))
        {
            return true;
        }

        if (TryInferReferenceNullComparisonFacts(
                comparison.Left,
                comparison.Operator,
                comparison.Right,
                branchWhenTrue,
                definitions,
                values,
                out facts)
            || TryMirrorComparisonOperator(comparison.Operator, out mirroredOperator)
            && TryInferReferenceNullComparisonFacts(
                comparison.Right,
                mirroredOperator,
                comparison.Left,
                branchWhenTrue,
                definitions,
                values,
                out facts))
        {
            return true;
        }

        if (TryInferReferenceKnownNonNullComparisonFacts(
                comparison.Left,
                comparison.Operator,
                comparison.Right,
                branchWhenTrue,
                definitions,
                values,
                out facts)
            || TryMirrorComparisonOperator(comparison.Operator, out mirroredOperator)
            && TryInferReferenceKnownNonNullComparisonFacts(
                comparison.Right,
                mirroredOperator,
                comparison.Left,
                branchWhenTrue,
                definitions,
                values,
                out facts))
        {
            return true;
        }

        return false;
    }

    private static bool TryInferReferenceConstantComparisonFacts(
        SsaValue variable,
        SsaBinaryOperator comparisonOperator,
        SsaValue constant,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> facts)
    {
        facts = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);

        if (variable is not SsaValueReference variableReference
            || variable.Type.Kind != StarkTypeKind.Integer
            || !TryGetIntegerSingleton(constant, values, out var constantValue)
            || !TryBuildComparisonRangeConstraint(
                comparisonOperator,
                constantValue,
                branchWhenTrue,
                out var min,
                out var max))
        {
            return false;
        }

        var inferred = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        foreach (var reference in ResolveReferenceAliases(variableReference, definitions))
        {
            if (TryRefineIntegerRange(reference.Name, reference.Type, min, max, values, out var valueFacts))
            {
                inferred[reference.Name] = valueFacts;
            }
        }

        if (inferred.Count == 0)
        {
            return false;
        }

        facts = inferred;
        return true;
    }

    private static bool TryInferReferenceNullComparisonFacts(
        SsaValue variable,
        SsaBinaryOperator comparisonOperator,
        SsaValue nullCandidate,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> facts)
    {
        facts = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);

        if (variable is not SsaValueReference variableReference
            || variable.Type.Kind != StarkTypeKind.RawPointer
            || !TryGetNullSingleton(nullCandidate, values)
            || !TryBuildNullabilityConstraint(
                comparisonOperator,
                branchWhenTrue,
                out var nullability))
        {
            return false;
        }

        var inferred = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        foreach (var reference in ResolveReferenceAliases(variableReference, definitions))
        {
            if (TryRefineNullability(reference.Name, reference.Type, nullability, values, out var valueFacts))
            {
                inferred[reference.Name] = valueFacts;
            }
        }

        if (inferred.Count == 0)
        {
            return false;
        }

        facts = inferred;
        return true;
    }

    private static bool TryInferReferenceKnownNonNullComparisonFacts(
        SsaValue variable,
        SsaBinaryOperator comparisonOperator,
        SsaValue nonNullCandidate,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> facts)
    {
        facts = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);

        if (variable is not SsaValueReference variableReference
            || variable.Type.Kind != StarkTypeKind.RawPointer
            || !IsEqualityEdge(comparisonOperator, branchWhenTrue)
            || !TryGetKnownNonNullPointer(nonNullCandidate, values))
        {
            return false;
        }

        _ = TryGetKnownPointerAlignment(nonNullCandidate, values, out var alignmentBytes);
        var inferred = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        foreach (var reference in ResolveReferenceAliases(variableReference, definitions))
        {
            if (TryRefineNullability(reference.Name, reference.Type, SsaNullabilityFactKind.NonNull, values, out var valueFacts))
            {
                if (alignmentBytes is > 1
                    && TryNormalizePointerAlignment(reference.Type, alignmentBytes, out var normalizedAlignmentBytes))
                {
                    valueFacts = WithPointerAlignment(valueFacts, normalizedAlignmentBytes);
                }

                inferred[reference.Name] = valueFacts;
            }
        }

        if (inferred.Count == 0)
        {
            return false;
        }

        facts = inferred;
        return true;
    }

    private static IReadOnlyList<SsaValueReference> ResolveReferenceAliases(
        SsaValueReference reference,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var aliases = new List<SsaValueReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = reference;

        while (seen.Add(current.Name))
        {
            aliases.Add(current);
            if (!definitions.TryGetValue(current.Name, out var definition)
                || definition is not SsaUseRValue { Value: SsaValueReference next })
            {
                break;
            }

            current = next;
        }

        return aliases;
    }

    private static bool TryRefineIntegerRange(
        string valueName,
        StarkTypeSymbol type,
        BigInteger? min,
        BigInteger? max,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out SsaValueFacts valueFacts)
    {
        valueFacts = default!;
        var currentFacts = values.TryGetValue(valueName, out var knownFacts)
            ? knownFacts
            : CreateTypeFacts(valueName, type);
        if (currentFacts.IntegerRangeKind != SsaFactLatticeKind.Known
            || currentFacts.IntegerRange is not { } currentRange)
        {
            return false;
        }

        var refinedRange = new SsaIntegerRangeFact(
            min is { } lowerBound ? Max(currentRange.Min, lowerBound) : currentRange.Min,
            max is { } upperBound ? Min(currentRange.Max, upperBound) : currentRange.Max);
        refinedRange = ClampToTypeRange(refinedRange, type);
        if (refinedRange.Min > refinedRange.Max)
        {
            return false;
        }

        valueFacts = currentFacts with
        {
            IntegerRangeKind = SsaFactLatticeKind.Known,
            IntegerRange = refinedRange
        };
        return true;
    }

    private static bool TryGetIntegerSingleton(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out BigInteger singleton)
    {
        switch (value)
        {
            case SsaIntegerConstant integer
                when StarkTypeSymbols.IntegerValueFitsEffectiveRange(integer.Value, integer.Type):
                singleton = integer.Value;
                return true;
            case SsaValueReference reference
                when values.TryGetValue(reference.Name, out var facts)
                     && facts.IntegerRangeKind == SsaFactLatticeKind.Known
                     && facts.IntegerRange is { } range
                     && range.Min == range.Max:
                singleton = range.Min;
                return true;
            default:
                singleton = default;
                return false;
        }
    }

    private static bool TryGetNullSingleton(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        return value is SsaNullConstant
            || value is SsaValueReference reference
            && values.TryGetValue(reference.Name, out var facts)
            && facts.Nullability == SsaNullabilityFactKind.Null;
    }

    private static bool TryGetKnownNonNullPointer(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        return value is SsaGlobalAddressValue
               || value is SsaFunctionAddressValue
               || value is SsaValueReference reference
               && values.TryGetValue(reference.Name, out var facts)
               && facts.Nullability == SsaNullabilityFactKind.NonNull;
    }

    private static bool TryGetKnownPointerAlignment(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out int alignmentBytes)
    {
        switch (value)
        {
            case SsaGlobalAddressValue globalAddress
                when TryGetTypeAlignmentBytes(globalAddress.PointeeType, out alignmentBytes):
                return true;
            case SsaValueReference reference
                when values.TryGetValue(reference.Name, out var facts)
                     && facts.PointerAlignmentKind == SsaFactLatticeKind.Known
                     && facts.PointerAlignmentBytes is > 1:
                alignmentBytes = facts.PointerAlignmentBytes.Value;
                return true;
            default:
                alignmentBytes = 1;
                return false;
        }
    }

    private static bool TryBuildComparisonRangeConstraint(
        SsaBinaryOperator comparisonOperator,
        BigInteger constant,
        bool branchWhenTrue,
        out BigInteger? min,
        out BigInteger? max)
    {
        min = null;
        max = null;

        switch (comparisonOperator)
        {
            case SsaBinaryOperator.LessThan:
                if (branchWhenTrue)
                {
                    max = constant - BigInteger.One;
                }
                else
                {
                    min = constant;
                }

                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                if (branchWhenTrue)
                {
                    max = constant;
                }
                else
                {
                    min = constant + BigInteger.One;
                }

                return true;
            case SsaBinaryOperator.GreaterThan:
                if (branchWhenTrue)
                {
                    min = constant + BigInteger.One;
                }
                else
                {
                    max = constant;
                }

                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                if (branchWhenTrue)
                {
                    min = constant;
                }
                else
                {
                    max = constant - BigInteger.One;
                }

                return true;
            case SsaBinaryOperator.Equal when branchWhenTrue:
            case SsaBinaryOperator.NotEqual when !branchWhenTrue:
                min = constant;
                max = constant;
                return true;
            default:
                return false;
        }
    }

    private static bool TryBuildNullabilityConstraint(
        SsaBinaryOperator comparisonOperator,
        bool branchWhenTrue,
        out SsaNullabilityFactKind nullability)
    {
        switch (comparisonOperator)
        {
            case SsaBinaryOperator.Equal:
                nullability = branchWhenTrue
                    ? SsaNullabilityFactKind.Null
                    : SsaNullabilityFactKind.NonNull;
                return true;
            case SsaBinaryOperator.NotEqual:
                nullability = branchWhenTrue
                    ? SsaNullabilityFactKind.NonNull
                    : SsaNullabilityFactKind.Null;
                return true;
            default:
                nullability = SsaNullabilityFactKind.Unknown;
                return false;
        }
    }

    private static bool IsEqualityEdge(
        SsaBinaryOperator comparisonOperator,
        bool branchWhenTrue)
    {
        return comparisonOperator is SsaBinaryOperator.Equal && branchWhenTrue
               || comparisonOperator is SsaBinaryOperator.NotEqual && !branchWhenTrue;
    }

    private static bool TryMirrorComparisonOperator(
        SsaBinaryOperator comparisonOperator,
        out SsaBinaryOperator mirroredOperator)
    {
        switch (comparisonOperator)
        {
            case SsaBinaryOperator.LessThan:
                mirroredOperator = SsaBinaryOperator.GreaterThan;
                return true;
            case SsaBinaryOperator.LessThanOrEqual:
                mirroredOperator = SsaBinaryOperator.GreaterThanOrEqual;
                return true;
            case SsaBinaryOperator.GreaterThan:
                mirroredOperator = SsaBinaryOperator.LessThan;
                return true;
            case SsaBinaryOperator.GreaterThanOrEqual:
                mirroredOperator = SsaBinaryOperator.LessThanOrEqual;
                return true;
            case SsaBinaryOperator.Equal:
            case SsaBinaryOperator.NotEqual:
                mirroredOperator = comparisonOperator;
                return true;
            default:
                mirroredOperator = default;
                return false;
        }
    }

    private static IReadOnlyDictionary<string, SsaValueFacts> JoinEdgeFacts(
        IReadOnlyList<IReadOnlyDictionary<string, SsaValueFacts>> edgeFacts,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        var result = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        var names = edgeFacts
            .SelectMany(static facts => facts.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var name in names)
        {
            if (!values.TryGetValue(name, out var baseFacts))
            {
                continue;
            }

            var incomingFacts = edgeFacts
                .Select(facts => facts.TryGetValue(name, out var edgeFact) ? edgeFact : baseFacts)
                .ToArray();
            var joined = JoinFacts(name, baseFacts.Type, incomingFacts);
            if (!EqualityComparer<SsaValueFacts>.Default.Equals(baseFacts, joined))
            {
                result[name] = joined;
            }
        }

        return result;
    }

    private static void RefineFacts(
        string moduleName,
        SsaFunction function,
        Dictionary<string, SsaValueFacts> values,
        ISet<int> reachableBlockIds,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            foreach (var phi in function.Blocks
                         .Where(block => reachableBlockIds.Contains(block.Id))
                         .SelectMany(static block => block.Phis))
            {
                var incomingFacts = phi.Incomings
                    .Where(incoming => reachableBlockIds.Contains(incoming.PredecessorBlockId))
                    .Select(incoming => AnalyzeValue(phi.ResultName, incoming.Value, values))
                    .ToArray();
                if (incomingFacts.Length == 0)
                {
                    continue;
                }

                var joined = JoinFacts(phi.ResultName, phi.Type, incomingFacts);
                if (!EqualityComparer<SsaValueFacts>.Default.Equals(values[phi.ResultName], joined))
                {
                    values[phi.ResultName] = joined;
                    changed = true;
                }
            }

            foreach (var valueInstruction in function.Blocks
                         .Where(block => reachableBlockIds.Contains(block.Id))
                         .SelectMany(static block => block.Instructions)
                         .OfType<SsaValueInstruction>())
            {
                var analyzed = AnalyzeRValue(
                    valueInstruction.ResultName,
                    valueInstruction.Value,
                    values,
                    moduleName,
                    function,
                    reachableBlockIds);
                if (!EqualityComparer<SsaValueFacts>.Default.Equals(values[valueInstruction.ResultName], analyzed))
                {
                    values[valueInstruction.ResultName] = analyzed;
                    changed = true;
                }
            }

            if (RefineDynamicStorageLocalFacts(function, values, reachableBlockIds, directCallParameterEffects))
            {
                changed = true;
            }

            if (!changed)
            {
                return;
            }
        }
    }

    private static SsaValueFacts AnalyzeRValue(
        string valueName,
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues,
        string moduleName,
        SsaFunction function,
        ISet<int> reachableBlockIds)
    {
        return value switch
        {
            SsaUseRValue use => RenameFacts(valueName, AnalyzeValue(valueName, use.Value, knownValues), value.Type),
            SsaUnaryRValue unary => AnalyzeUnary(valueName, unary, knownValues),
            SsaBinaryRValue binary => AnalyzeBinary(valueName, binary, knownValues),
            SsaSelectRValue select => AnalyzeSelect(valueName, select, knownValues),
            SsaConvertRValue convert => AnalyzeConvert(valueName, convert, knownValues),
            SsaExtractFieldRValue extractField => AnalyzeExtractField(valueName, extractField, knownValues),
            SsaInsertFieldRValue insertField => AnalyzeInsertField(valueName, insertField, knownValues),
            SsaCallRValue call => AnalyzeCall(valueName, call, knownValues, moduleName),
            SsaAddressOfLocalRValue addressOfLocal => CreateAddressFacts(valueName, addressOfLocal.Type, addressOfLocal.PointeeType),
            SsaAddressOfParameterRValue addressOfParameter => CreateAddressFacts(valueName, addressOfParameter.Type, addressOfParameter.PointeeType),
            SsaFieldAddressRValue fieldAddress => AnalyzeDerivedPointerAddress(valueName, fieldAddress.Type, fieldAddress.Address, knownValues),
            SsaElementAddressRValue elementAddress => AnalyzeDerivedPointerAddress(valueName, elementAddress.Type, elementAddress.Address, knownValues),
            SsaSliceElementAddressRValue sliceElementAddress => AnalyzeSliceElementAddress(valueName, sliceElementAddress, knownValues),
            SsaMakeSliceFromLocalRValue makeSlice => AnalyzeMakeSlice(valueName, makeSlice),
            SsaMakeSliceFromPointerRValue makeSlice => AnalyzeMakeSliceFromPointer(valueName, makeSlice, knownValues),
            SsaLoadLocalRValue loadLocal => AnalyzeLoadLocal(valueName, loadLocal, function, reachableBlockIds, knownValues),
            SsaTextSliceRValue textSlice => AnalyzeTextSlice(valueName, textSlice, knownValues),
            SsaDynamicStorageAllocationRValue allocation => AnalyzeDynamicStorageAllocation(valueName, allocation, knownValues),
            _ => CreateTypeFacts(valueName, value.Type)
        };
    }

    private static SsaValueFacts AnalyzeValue(
        string valueName,
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        return value switch
        {
            SsaValueReference reference when knownValues.TryGetValue(reference.Name, out var facts)
                => RenameFacts(valueName, facts, value.Type),
            SsaIntegerConstant integer
                when StarkTypeSymbols.IntegerValueFitsEffectiveRange(integer.Value, integer.Type)
                => CreateIntegerConstantFacts(valueName, integer.Type, integer.Value),
            SsaIntegerConstant integer => CreateTypeFacts(valueName, integer.Type),
            SsaBoolConstant boolean => CreateBooleanConstantFacts(valueName, boolean.Value),
            SsaNullConstant nullConstant => CreateNullFacts(valueName, nullConstant.Type),
            SsaStringConstant text => CreateTextConstantFacts(valueName, text.Type, text.LiteralText),
            SsaGlobalAddressValue globalAddress => CreateAddressFacts(valueName, globalAddress.Type, globalAddress.PointeeType),
            SsaFunctionAddressValue functionAddress => CreateNonNullFacts(valueName, functionAddress.Type),
            SsaZeroInitializerValue { Type.Kind: StarkTypeKind.Dynamic } dynamicZero
                => CreateEmptyDynamicStorageFacts(valueName, dynamicZero.Type),
            _ => CreateTypeFacts(valueName, value.Type)
        };
    }

    private static SsaValueFacts AnalyzeUnary(
        string valueName,
        SsaUnaryRValue unary,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var operand = AnalyzeValue(valueName, unary.Operand, knownValues);
        if (unary.Operator == SsaUnaryOperator.LogicalNot
            && operand.BooleanKind == SsaFactLatticeKind.Known
            && operand.BooleanConstant is bool boolean)
        {
            return CreateBooleanConstantFacts(valueName, !boolean);
        }

        if (unary.Operator == SsaUnaryOperator.BitwiseNot
            && unary.Type.Kind == StarkTypeKind.Integer
            && operand.KnownBitsKind == SsaFactLatticeKind.Known
            && operand.KnownBits is { } operandKnownBits
            && TryGetIntegerBitDomain(unary.Type, out _, out var mask, out _))
        {
            return ApplyKnownBits(
                CreateTypeFacts(valueName, unary.Type),
                new SsaKnownBitsFact(
                    operandKnownBits.KnownOneBits & mask,
                    operandKnownBits.KnownZeroBits & mask));
        }

        return CreateTypeFacts(valueName, unary.Type);
    }

    private static SsaValueFacts AnalyzeBinary(
        string valueName,
        SsaBinaryRValue binary,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var left = AnalyzeValue(valueName, binary.Left, knownValues);
        var right = AnalyzeValue(valueName, binary.Right, knownValues);

        if (binary.Type.Kind == StarkTypeKind.Bool
            && TryEvaluateComparison(binary.Operator, left, right, out var comparison))
        {
            return CreateBooleanConstantFacts(valueName, comparison);
        }

        if (binary.Type.Kind == StarkTypeKind.Integer)
        {
            var facts = TryAnalyzeIntegerBinary(binary, left, right, out var range)
                ? CreateIntegerRangeFacts(valueName, binary.Type, ClampToTypeRange(range, binary.Type))
                : CreateTypeFacts(valueName, binary.Type);

            return TryAnalyzeIntegerKnownBits(binary, left, right, out var knownBits)
                ? ApplyKnownBits(facts, knownBits)
                : facts;
        }

        return CreateTypeFacts(valueName, binary.Type);
    }

    private static SsaValueFacts AnalyzeSelect(
        string valueName,
        SsaSelectRValue select,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var condition = AnalyzeValue(valueName, select.Condition, knownValues);
        if (condition.BooleanKind == SsaFactLatticeKind.Known
            && condition.BooleanConstant is bool conditionValue)
        {
            return RenameFacts(
                valueName,
                AnalyzeValue(valueName, conditionValue ? select.WhenTrue : select.WhenFalse, knownValues),
                select.Type);
        }

        return JoinFacts(
            valueName,
            select.Type,
            [
                AnalyzeValue(valueName, select.WhenTrue, knownValues),
                AnalyzeValue(valueName, select.WhenFalse, knownValues)
            ]);
    }

    private static bool TryAnalyzeIntegerBinary(
        SsaBinaryRValue binary,
        SsaValueFacts left,
        SsaValueFacts right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (left.IntegerRangeKind != SsaFactLatticeKind.Known
            || right.IntegerRangeKind != SsaFactLatticeKind.Known
            || left.IntegerRange is not { } leftRange
            || right.IntegerRange is not { } rightRange)
        {
            return false;
        }

        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
                range = new SsaIntegerRangeFact(
                    leftRange.Min + rightRange.Min,
                    leftRange.Max + rightRange.Max);
                return true;
            case SsaBinaryOperator.WrappingAdd:
                return TryAnalyzeWrappingRange(
                    binary.Type,
                    new SsaIntegerRangeFact(
                        leftRange.Min + rightRange.Min,
                        leftRange.Max + rightRange.Max),
                    out range);
            case SsaBinaryOperator.SaturatingAdd:
                return TryAnalyzeSaturatingAddRange(binary.Type, leftRange, rightRange, out range);
            case SsaBinaryOperator.Subtract:
                range = new SsaIntegerRangeFact(
                    leftRange.Min - rightRange.Max,
                    leftRange.Max - rightRange.Min);
                return true;
            case SsaBinaryOperator.WrappingSubtract:
                return TryAnalyzeWrappingRange(
                    binary.Type,
                    new SsaIntegerRangeFact(
                        leftRange.Min - rightRange.Max,
                        leftRange.Max - rightRange.Min),
                    out range);
            case SsaBinaryOperator.SaturatingSubtract:
                return TryAnalyzeSaturatingSubtractRange(binary.Type, leftRange, rightRange, out range);
            case SsaBinaryOperator.Multiply:
                range = MultiplyRanges(leftRange, rightRange);
                return true;
            case SsaBinaryOperator.WrappingMultiply:
                return TryAnalyzeWrappingRange(binary.Type, MultiplyRanges(leftRange, rightRange), out range);
            case SsaBinaryOperator.SaturatingMultiply:
                return TryAnalyzeSaturatingMultiplyRange(binary.Type, leftRange, rightRange, out range);
            case SsaBinaryOperator.Divide:
                return TryAnalyzeDivideRange(leftRange, rightRange, out range);
            case SsaBinaryOperator.Modulo:
                return TryAnalyzeModuloRange(leftRange, rightRange, out range);
            case SsaBinaryOperator.BitwiseAnd:
            case SsaBinaryOperator.BitwiseOr:
            case SsaBinaryOperator.BitwiseXor:
                return TryAnalyzeBitwiseRange(binary.Operator, leftRange, rightRange, out range);
            case SsaBinaryOperator.ShiftLeft:
                return TryAnalyzeShiftLeftRange(binary, leftRange, rightRange, out range);
            case SsaBinaryOperator.ShiftRight:
                return TryAnalyzeShiftRightRange(binary, leftRange, rightRange, out range);
            default:
                return false;
        }
    }

    private static bool TryAnalyzeIntegerKnownBits(
        SsaBinaryRValue binary,
        SsaValueFacts left,
        SsaValueFacts right,
        out SsaKnownBitsFact knownBits)
    {
        knownBits = default!;
        if (!TryGetIntegerBitDomain(binary.Type, out var bitWidth, out var mask, out _))
        {
            return false;
        }

        var leftBits = GetKnownBitsOrNone(left);
        var rightBits = GetKnownBitsOrNone(right);
        var leftKnownZero = leftBits.KnownZeroBits & mask;
        var leftKnownOne = leftBits.KnownOneBits & mask;
        var rightKnownZero = rightBits.KnownZeroBits & mask;
        var rightKnownOne = rightBits.KnownOneBits & mask;

        knownBits = binary.Operator switch
        {
            SsaBinaryOperator.BitwiseAnd => new SsaKnownBitsFact(
                (leftKnownZero | rightKnownZero) & mask,
                (leftKnownOne & rightKnownOne) & mask),
            SsaBinaryOperator.BitwiseOr => new SsaKnownBitsFact(
                (leftKnownZero & rightKnownZero) & mask,
                (leftKnownOne | rightKnownOne) & mask),
            SsaBinaryOperator.BitwiseXor => new SsaKnownBitsFact(
                ((leftKnownZero & rightKnownZero) | (leftKnownOne & rightKnownOne)) & mask,
                ((leftKnownOne & rightKnownZero) | (leftKnownZero & rightKnownOne)) & mask),
            SsaBinaryOperator.ShiftLeft when TryGetKnownShiftAmount(right, bitWidth, out var leftShift) =>
                CreateShiftLeftKnownBits(leftKnownZero, leftKnownOne, leftShift, mask),
            SsaBinaryOperator.ShiftRight when TryGetKnownShiftAmount(right, bitWidth, out var rightShift) =>
                CreateShiftRightKnownBits(binary.Type, leftKnownZero, leftKnownOne, rightShift, bitWidth, mask),
            _ => default!
        };

        return knownBits is not null
            && (knownBits.KnownZeroBits != BigInteger.Zero || knownBits.KnownOneBits != BigInteger.Zero);

        static SsaKnownBitsFact GetKnownBitsOrNone(SsaValueFacts facts)
            => facts.KnownBitsKind == SsaFactLatticeKind.Known && facts.KnownBits is { } knownBits
                ? knownBits
                : new SsaKnownBitsFact(BigInteger.Zero, BigInteger.Zero);
    }

    private static bool TryGetKnownShiftAmount(SsaValueFacts facts, int bitWidth, out int shift)
    {
        shift = default;
        if (facts.IntegerRangeKind != SsaFactLatticeKind.Known
            || facts.IntegerRange is not { Min: var min, Max: var max }
            || min != max
            || min < BigInteger.Zero
            || min >= bitWidth
            || min > int.MaxValue)
        {
            return false;
        }

        shift = (int)min;
        return true;
    }

    private static SsaKnownBitsFact CreateShiftLeftKnownBits(
        BigInteger knownZero,
        BigInteger knownOne,
        int shift,
        BigInteger mask)
    {
        var lowZeroBits = shift == 0
            ? BigInteger.Zero
            : (BigInteger.One << shift) - BigInteger.One;
        return new SsaKnownBitsFact(
            ((knownZero << shift) | lowZeroBits) & mask,
            (knownOne << shift) & mask);
    }

    private static SsaKnownBitsFact CreateShiftRightKnownBits(
        StarkTypeSymbol type,
        BigInteger knownZero,
        BigInteger knownOne,
        int shift,
        int bitWidth,
        BigInteger mask)
    {
        var shiftedZero = (knownZero >> shift) & mask;
        var shiftedOne = (knownOne >> shift) & mask;
        if (shift == 0)
        {
            return new SsaKnownBitsFact(shiftedZero, shiftedOne);
        }

        var highBits = mask ^ ((BigInteger.One << (bitWidth - shift)) - BigInteger.One);
        var signBit = BigInteger.One << (bitWidth - 1);
        if (type.IsUnsigned || (knownZero & signBit) != BigInteger.Zero)
        {
            shiftedZero |= highBits;
        }
        else if ((knownOne & signBit) != BigInteger.Zero)
        {
            shiftedOne |= highBits;
        }

        return new SsaKnownBitsFact(shiftedZero & mask, shiftedOne & mask);
    }

    private static bool TryAnalyzeWrappingRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact mathematicalRange,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetIntegerTypeRange(type, out var typeRange)
            || mathematicalRange.Min < typeRange.Min
            || mathematicalRange.Max > typeRange.Max)
        {
            return false;
        }

        range = mathematicalRange;
        return true;
    }

    private static bool TryAnalyzeSaturatingAddRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetIntegerTypeRange(type, out var typeRange))
        {
            return false;
        }

        range = new SsaIntegerRangeFact(
            SaturateInteger(left.Min + right.Min, typeRange),
            SaturateInteger(left.Max + right.Max, typeRange));
        return true;
    }

    private static bool TryAnalyzeSaturatingSubtractRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetIntegerTypeRange(type, out var typeRange))
        {
            return false;
        }

        range = new SsaIntegerRangeFact(
            SaturateInteger(left.Min - right.Max, typeRange),
            SaturateInteger(left.Max - right.Min, typeRange));
        return true;
    }

    private static bool TryAnalyzeSaturatingMultiplyRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetIntegerTypeRange(type, out var typeRange))
        {
            return false;
        }

        var candidates = new[]
        {
            SaturateInteger(left.Min * right.Min, typeRange),
            SaturateInteger(left.Min * right.Max, typeRange),
            SaturateInteger(left.Max * right.Min, typeRange),
            SaturateInteger(left.Max * right.Max, typeRange)
        };
        range = new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
        return true;
    }

    private static bool TryAnalyzeBitwiseRange(
        SsaBinaryOperator op,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (left.Min < BigInteger.Zero || right.Min < BigInteger.Zero)
        {
            return false;
        }

        if (op == SsaBinaryOperator.BitwiseAnd)
        {
            range = new SsaIntegerRangeFact(BigInteger.Zero, Min(left.Max, right.Max));
            return true;
        }

        var upper = CreateNonNegativeBitMask(left.Max) | CreateNonNegativeBitMask(right.Max);
        range = new SsaIntegerRangeFact(BigInteger.Zero, upper);
        return true;
    }

    private static bool TryAnalyzeDivideRange(
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (right.Min <= BigInteger.Zero && right.Max >= BigInteger.Zero)
        {
            return false;
        }

        var candidates = new[]
        {
            left.Min / right.Min,
            left.Min / right.Max,
            left.Max / right.Min,
            left.Max / right.Max
        };
        range = new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
        return true;
    }

    private static bool TryAnalyzeModuloRange(
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (right.Min <= BigInteger.Zero)
        {
            return false;
        }

        var maxMagnitude = right.Max - BigInteger.One;
        if (left.Min >= BigInteger.Zero)
        {
            range = new SsaIntegerRangeFact(BigInteger.Zero, Min(left.Max, maxMagnitude));
            return true;
        }

        if (left.Max <= BigInteger.Zero)
        {
            range = new SsaIntegerRangeFact(Max(left.Min, -maxMagnitude), BigInteger.Zero);
            return true;
        }

        range = new SsaIntegerRangeFact(-maxMagnitude, maxMagnitude);
        return true;
    }

    private static bool TryAnalyzeShiftLeftRange(
        SsaBinaryRValue binary,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetShiftBounds(binary, right, out var minShift, out var maxShift))
        {
            return false;
        }

        var minFactor = BigInteger.One << minShift;
        var maxFactor = BigInteger.One << maxShift;
        var candidates = new[]
        {
            left.Min * minFactor,
            left.Min * maxFactor,
            left.Max * minFactor,
            left.Max * maxFactor
        };
        range = new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
        return true;
    }

    private static bool TryAnalyzeShiftRightRange(
        SsaBinaryRValue binary,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetShiftBounds(binary, right, out var minShift, out var maxShift))
        {
            return false;
        }

        var candidates = new[]
        {
            left.Min >> minShift,
            left.Min >> maxShift,
            left.Max >> minShift,
            left.Max >> maxShift
        };
        range = new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
        return true;
    }

    private static bool TryGetShiftBounds(
        SsaBinaryRValue binary,
        SsaIntegerRangeFact shiftRange,
        out int minShift,
        out int maxShift)
    {
        minShift = default;
        maxShift = default;
        if (binary.Left.Type.BitWidth is not int bitWidth
            || bitWidth <= 0
            || shiftRange.Min < BigInteger.Zero
            || shiftRange.Max < shiftRange.Min
            || shiftRange.Max >= bitWidth
            || shiftRange.Max > int.MaxValue)
        {
            return false;
        }

        minShift = (int)shiftRange.Min;
        maxShift = (int)shiftRange.Max;
        return true;
    }

    private static SsaValueFacts AnalyzeConvert(
        string valueName,
        SsaConvertRValue convert,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var operand = AnalyzeValue(valueName, convert.Operand, knownValues);
        if (convert.TargetType.Kind == StarkTypeKind.Integer
            && operand.IntegerRangeKind == SsaFactLatticeKind.Known
            && operand.IntegerRange is { } range
            && TryGetIntegerTypeRange(convert.TargetType, out var targetRange))
        {
            var convertedRange = TranslateIntegerConvertRange(convert.Operand.Type, convert.TargetType, range, targetRange);
            var facts = CreateIntegerRangeFacts(valueName, convert.TargetType, convertedRange);
            return TryTranslateIntegerConvertKnownBits(convert.Operand.Type, convert.TargetType, operand, out var knownBits)
                ? ApplyKnownBits(facts, knownBits)
                : facts;
        }

        if (convert.TargetType.Kind == StarkTypeKind.Bool
            && operand.BooleanKind == SsaFactLatticeKind.Known
            && operand.BooleanConstant is bool boolean)
        {
            return CreateBooleanConstantFacts(valueName, boolean);
        }

        if (convert.TargetType.Kind == StarkTypeKind.RawPointer
            && convert.Operand.Type.Kind == StarkTypeKind.RawPointer)
        {
            var facts = CreateTypeFacts(valueName, convert.TargetType) with
            {
                Nullability = operand.Nullability
            };
            if (TryCanPreserveBoundedRawPointerRegionThroughConvert(
                    convert.Operand.Type,
                    convert.TargetType,
                    operand,
                    out var boundedRegion))
            {
                facts = WithBoundedRawPointerRegion(facts, boundedRegion);
            }

            return TryNormalizePointerAlignment(convert.TargetType, operand.PointerAlignmentBytes, out var alignmentBytes)
                ? WithPointerAlignment(facts, alignmentBytes)
                : facts;
        }

        if (convert.TargetType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            && convert.Operand.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            && operand.TextLiteralPayloadKind == SsaFactLatticeKind.Known
            && operand.TextLiteralPayload is { } payload
            && (convert.TargetType.Kind != StarkTypeKind.Ascii || payload.IsAsciiOnly))
        {
            return CreateTextLiteralPayloadFacts(valueName, convert.TargetType, payload);
        }

        return CreateTypeFacts(valueName, convert.TargetType);
    }

    private static SsaIntegerRangeFact TranslateIntegerConvertRange(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType,
        SsaIntegerRangeFact sourceRange,
        SsaIntegerRangeFact targetRange)
    {
        if (sourceType.Kind == StarkTypeKind.Integer
            && targetType.Kind == StarkTypeKind.Integer
            && sourceType.BitWidth == targetType.BitWidth
            && sourceType.IsUnsigned != targetType.IsUnsigned
            && targetType.BitWidth is int bitWidth
            && bitWidth > 0)
        {
            return TranslateSameWidthIntegerReinterpretRange(targetType, sourceRange, targetRange, bitWidth);
        }

        return ClampRange(sourceRange, targetRange);
    }

    private static SsaIntegerRangeFact TranslateSameWidthIntegerReinterpretRange(
        StarkTypeSymbol targetType,
        SsaIntegerRangeFact sourceRange,
        SsaIntegerRangeFact targetRange,
        int bitWidth)
    {
        var domainSize = BigInteger.One << bitWidth;
        var valueCount = sourceRange.Max - sourceRange.Min + BigInteger.One;
        if (valueCount >= domainSize)
        {
            return targetRange;
        }

        var normalizedMin = NormalizeIntegerBits(sourceRange.Min, domainSize);
        var normalizedMax = NormalizeIntegerBits(sourceRange.Max, domainSize);
        if (normalizedMin > normalizedMax)
        {
            return targetRange;
        }

        if (targetType.IsUnsigned)
        {
            return new SsaIntegerRangeFact(normalizedMin, normalizedMax);
        }

        var signBit = BigInteger.One << (bitWidth - 1);
        if (normalizedMax < signBit)
        {
            return new SsaIntegerRangeFact(normalizedMin, normalizedMax);
        }

        if (normalizedMin >= signBit)
        {
            return new SsaIntegerRangeFact(normalizedMin - domainSize, normalizedMax - domainSize);
        }

        return targetRange;
    }

    private static SsaValueFacts AnalyzeDerivedPointerAddress(
        string valueName,
        StarkTypeSymbol pointerType,
        SsaValue baseAddress,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var facts = CreateNonNullFacts(valueName, pointerType);
        var baseFacts = AnalyzeValue(valueName, baseAddress, knownValues);
        if (baseFacts.PointerAlignmentKind != SsaFactLatticeKind.Known
            || baseFacts.PointerAlignmentBytes is not int baseAlignmentBytes
            || !TryNormalizePointerAlignment(pointerType, baseAlignmentBytes, out var alignmentBytes))
        {
            return facts;
        }

        return WithPointerAlignment(facts, alignmentBytes);
    }

    private static SsaValueFacts AnalyzeSliceElementAddress(
        string valueName,
        SsaSliceElementAddressRValue sliceElementAddress,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var facts = CreateNonNullFacts(valueName, sliceElementAddress.Type);
        var sliceFacts = AnalyzeValue(valueName, sliceElementAddress.Slice, knownValues);
        if (sliceFacts.BoundedRawPointerRegionKind == SsaFactLatticeKind.Known
            && sliceFacts.BoundedRawPointerRegion is { ElementAlignmentBytes: > 1 } boundedRegion
            && TryNormalizePointerAlignment(
                sliceElementAddress.Type,
                boundedRegion.ElementAlignmentBytes,
                out var alignmentBytes))
        {
            return WithPointerAlignment(facts, alignmentBytes);
        }

        return NormalizeDynamicStorageFacts(facts);
    }

    private static SsaValueFacts AnalyzeCall(
        string valueName,
        SsaCallRValue call,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues,
        string moduleName)
    {
        if (call.Type.Kind == StarkTypeKind.Integer
            && call.Arguments.Count == 1
            && TryGetSystemTextLengthFunction(call.FunctionName, moduleName, out var textKind)
            && call.Arguments[0].Type.Kind == textKind
            && AnalyzeValue(valueName, call.Arguments[0], knownValues) is
            {
                LengthKind: SsaFactLatticeKind.Known,
                LengthRange: { } lengthRange
            })
        {
            return CreateIntegerRangeFacts(
                valueName,
                call.Type,
                ClampToTypeRange(lengthRange, call.Type));
        }

        return CreateTypeFacts(valueName, call.Type);
    }

    private static SsaValueFacts AnalyzeExtractField(
        string valueName,
        SsaExtractFieldRValue extractField,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        if (extractField.Target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            && extractField.Type.Kind == StarkTypeKind.Integer
            && IsTextLengthField(extractField)
            && AnalyzeValue(valueName, extractField.Target, knownValues) is
            {
                LengthKind: SsaFactLatticeKind.Known,
                LengthRange: { } lengthRange
            })
        {
            return CreateIntegerRangeFacts(
                valueName,
                extractField.Type,
                ClampToTypeRange(lengthRange, extractField.Type));
        }

        if (extractField.Target.Type.Kind == StarkTypeKind.Dynamic
            && extractField.Type.Kind == StarkTypeKind.Integer)
        {
            var targetFacts = AnalyzeValue(valueName, extractField.Target, knownValues);
            if (IsDynamicLengthField(extractField)
                && targetFacts.LengthKind == SsaFactLatticeKind.Known
                && targetFacts.LengthRange is { } dynamicLengthRange)
            {
                return CreateIntegerRangeFacts(
                    valueName,
                    extractField.Type,
                    ClampToTypeRange(dynamicLengthRange, extractField.Type));
            }

            if (IsDynamicCapacityField(extractField)
                && targetFacts.CapacityKind == SsaFactLatticeKind.Known
                && targetFacts.CapacityRange is { } dynamicCapacityRange)
            {
                return CreateIntegerRangeFacts(
                    valueName,
                    extractField.Type,
                    ClampToTypeRange(dynamicCapacityRange, extractField.Type));
            }
        }

        if (extractField.Target.Type.Kind == StarkTypeKind.Dynamic
            && extractField.Type.Kind == StarkTypeKind.RawPointer
            && IsDynamicDataField(extractField))
        {
            var facts = CreateTypeFacts(valueName, extractField.Type);
            var targetFacts = AnalyzeValue(valueName, extractField.Target, knownValues);
            if (targetFacts.CapacityKind == SsaFactLatticeKind.Known
                && targetFacts.CapacityRange is { } dataCapacityRange
                && dataCapacityRange.Min > BigInteger.Zero
                && extractField.Target.Type.ElementType is { } elementType)
            {
                facts = CreateNonNullFacts(valueName, extractField.Type);
                if (TryGetTypeAlignmentBytes(elementType, out var alignmentBytes))
                {
                    facts = WithPointerAlignment(facts, alignmentBytes);
                }
            }

            return facts;
        }

        return CreateTypeFacts(valueName, extractField.Type);
    }

    private static SsaValueFacts AnalyzeInsertField(
        string valueName,
        SsaInsertFieldRValue insertField,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        if (insertField.Type.Kind != StarkTypeKind.Dynamic)
        {
            return CreateTypeFacts(valueName, insertField.Type);
        }

        var facts = RenameFacts(valueName, AnalyzeValue(valueName, insertField.Target, knownValues), insertField.Type);
        if (insertField.Value.Type.Kind == StarkTypeKind.Integer)
        {
            var valueFacts = AnalyzeValue(valueName, insertField.Value, knownValues);
            if (valueFacts.IntegerRangeKind == SsaFactLatticeKind.Known
                && valueFacts.IntegerRange is { } insertedRange)
            {
                if (IsDynamicLengthField(insertField.FieldName, insertField.FieldIndex))
                {
                    var lengthRange = ClampDynamicStorageCountRange(insertedRange);
                    facts = facts with
                    {
                        LengthKind = SsaFactLatticeKind.Known,
                        LengthRange = lengthRange,
                        InitializedPrefixKind = SsaFactLatticeKind.Known,
                        InitializedPrefixRange = lengthRange
                    };
                }
                else if (IsDynamicCapacityField(insertField.FieldName, insertField.FieldIndex))
                {
                    facts = facts with
                    {
                        CapacityKind = SsaFactLatticeKind.Known,
                        CapacityRange = ClampDynamicStorageCountRange(insertedRange)
                    };
                }
            }
        }

        return NormalizeDynamicStorageFacts(facts);
    }

    private static SsaValueFacts AnalyzeMakeSlice(
        string valueName,
        SsaMakeSliceFromLocalRValue makeSlice)
    {
        var facts = CreateTypeFacts(valueName, makeSlice.Type);
        return makeSlice.SourceType.Kind == StarkTypeKind.FixedArray
               && makeSlice.SourceType.FixedLength is int fixedLength
            ? facts with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = new SsaIntegerRangeFact(fixedLength, fixedLength)
            }
            : facts;
    }

    private static SsaValueFacts AnalyzeMakeSliceFromPointer(
        string valueName,
        SsaMakeSliceFromPointerRValue makeSlice,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var facts = CreateTypeFacts(valueName, makeSlice.Type);
        var lengthFacts = AnalyzeValue(valueName, makeSlice.Length, knownValues);
        if (lengthFacts.IntegerRangeKind == SsaFactLatticeKind.Known
            && lengthFacts.IntegerRange is { } lengthRange)
        {
            facts = facts with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = lengthRange
            };
        }

        var pointerFacts = AnalyzeValue(valueName, makeSlice.Pointer, knownValues);
        var alignmentBytes = pointerFacts.PointerAlignmentBytes
            ?? pointerFacts.BoundedRawPointerRegion?.ElementAlignmentBytes;
        var boundedRegion = new SsaBoundedRawPointerRegionFact(
            makeSlice.Length,
            lengthFacts.IntegerRangeKind == SsaFactLatticeKind.Known ? lengthFacts.IntegerRange : null,
            alignmentBytes);

        return WithBoundedRawPointerRegion(facts, boundedRegion);
    }

    private static SsaValueFacts AnalyzeLoadLocal(
        string valueName,
        SsaLoadLocalRValue loadLocal,
        SsaFunction function,
        ISet<int> reachableBlockIds,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var facts = CreateTypeFacts(valueName, loadLocal.Type);
        if (loadLocal.Type.Kind is not (StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.Dynamic))
        {
            return facts;
        }

        if (loadLocal.Type.Kind == StarkTypeKind.Dynamic
            && knownValues.TryGetValue(valueName, out var existingFacts)
            && HasDynamicStorageFactPayload(existingFacts))
        {
            return existingFacts;
        }

        if (IsLocalAddressTaken(function, reachableBlockIds, loadLocal.LocalName))
        {
            return facts;
        }

        var storedFacts = function.Blocks
            .Where(block => reachableBlockIds.Contains(block.Id))
            .SelectMany(static block => block.Instructions)
            .OfType<SsaStoreLocalInstruction>()
            .Where(store => string.Equals(store.LocalName, loadLocal.LocalName, StringComparison.Ordinal)
                            && store.LocalType == loadLocal.Type)
            .Select(store => AnalyzeValue(valueName, store.Value, knownValues))
            .ToArray();
        if (storedFacts.Length == 0)
        {
            return facts;
        }

        return JoinFacts(valueName, loadLocal.Type, storedFacts);
    }

    private static SsaValueFacts AnalyzeDynamicStorageAllocation(
        string valueName,
        SsaDynamicStorageAllocationRValue allocation,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var facts = CreateTypeFacts(valueName, allocation.Type);
        var zero = new SsaIntegerRangeFact(BigInteger.Zero, BigInteger.Zero);
        facts = facts with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = zero,
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = zero
        };

        var capacityFacts = AnalyzeValue(valueName, allocation.Capacity, knownValues);
        if (capacityFacts.IntegerRangeKind == SsaFactLatticeKind.Known
            && capacityFacts.IntegerRange is { } capacityRange)
        {
            facts = facts with
            {
                CapacityKind = SsaFactLatticeKind.Known,
                CapacityRange = ClampDynamicStorageCountRange(capacityRange)
            };
        }

        var allocatorProvenance = allocation.AllocationKind == DynamicStorageAllocationKind.Arena
            ? SsaDynamicStorageAllocatorProvenanceKind.ArenaFrame
            : SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault;

        return WithDynamicStorageAllocationIdentity(
            NormalizeDynamicStorageFacts(facts),
            valueName,
            allocatorProvenance);
    }

    private sealed record DynamicStorageLocalMutation(
        string LocalName,
        SsaValueFacts Before,
        SsaValueFacts OnSuccess);

    private sealed record DynamicStorageBlockTransfer(
        IReadOnlyDictionary<string, SsaValueFacts> ExitState,
        IReadOnlyDictionary<string, DynamicStorageLocalMutation> ConditionalMutations);

    private static bool RefineDynamicStorageLocalFacts(
        SsaFunction function,
        Dictionary<string, SsaValueFacts> values,
        ISet<int> reachableBlockIds,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        var definitions = CollectValueDefinitions(function);
        var reachableBlocks = function.Blocks
            .Where(block => reachableBlockIds.Contains(block.Id))
            .ToArray();
        if (reachableBlocks.Length == 0)
        {
            return false;
        }

        var entryStates = new Dictionary<int, Dictionary<string, SsaValueFacts>>();
        var initializedEntries = new HashSet<int>();
        entryStates[function.EntryBlockId] = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        initializedEntries.Add(function.EntryBlockId);

        // Run the entry-state fixpoint against a scratch copy of the value
        // facts: recording refined per-value facts DURING iteration is
        // order-dependent — a loop body's first pass records ranges from the
        // not-yet-joined entry state (e.g. a dynamic's zero-init length before
        // the mutating back edge merges in) and nothing retracts them once the
        // states converge. Facts are recorded once, after convergence, from
        // the joined entry states.
        var scratchValues = new Dictionary<string, SsaValueFacts>(values, StringComparer.Ordinal);
        var scratchChangedFacts = false;
        var changedStates = true;
        var iterationLimit = Math.Max(4, reachableBlocks.Length * 4);
        for (var iteration = 0; changedStates && iteration < iterationLimit; iteration++)
        {
            changedStates = false;
            foreach (var block in reachableBlocks)
            {
                if (!initializedEntries.Contains(block.Id)
                    || !entryStates.TryGetValue(block.Id, out var entryState))
                {
                    continue;
                }

                var transfer = AnalyzeDynamicStorageBlockTransfer(
                    block,
                    entryState,
                    definitions,
                    scratchValues,
                    directCallParameterEffects,
                    ref scratchChangedFacts);
                foreach (var target in EnumerateTerminatorTargets(block.Terminator))
                {
                    if (!reachableBlockIds.Contains(target))
                    {
                        continue;
                    }

                    if (!TryApplyDynamicStorageEdgeTransfer(
                        block.Terminator,
                        target,
                        transfer.ExitState,
                        transfer.ConditionalMutations,
                        definitions,
                        scratchValues,
                        out var edgeState))
                    {
                        continue;
                    }

                    if (MergeDynamicStorageEntryState(target, edgeState, entryStates, initializedEntries))
                    {
                        changedStates = true;
                    }
                }
            }
        }

        var changedFacts = false;
        foreach (var block in reachableBlocks)
        {
            if (!initializedEntries.Contains(block.Id)
                || !entryStates.TryGetValue(block.Id, out var entryState))
            {
                continue;
            }

            AnalyzeDynamicStorageBlockTransfer(
                block,
                entryState,
                definitions,
                values,
                directCallParameterEffects,
                ref changedFacts);
        }

        return changedFacts;
    }

    private static DynamicStorageBlockTransfer AnalyzeDynamicStorageBlockTransfer(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaValueFacts> entryState,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        Dictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects,
        ref bool changedFacts)
    {
        var state = CloneDynamicStorageState(entryState);
        var conditionalMutations = new Dictionary<string, DynamicStorageLocalMutation>(StringComparer.Ordinal);
        var dynamicIntegerRanges = new Dictionary<string, SsaIntegerRangeFact>(StringComparer.Ordinal);

        foreach (var instruction in block.Instructions)
        {
            if (instruction is SsaValueInstruction valueForRange)
            {
                RecordDynamicIntegerRange(
                    valueForRange,
                    state,
                    definitions,
                    values,
                    dynamicIntegerRanges,
                    ref changedFacts);
            }

            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal when storeLocal.LocalType.Kind == StarkTypeKind.Dynamic:
                {
                    var storedFacts = AnalyzeValue(storeLocal.LocalName, storeLocal.Value, values);
                    if (HasDynamicStorageFactPayload(storedFacts))
                    {
                        state[storeLocal.LocalName] = WithDynamicStorageOwnerRoot(
                            RenameFacts(storeLocal.LocalName, storedFacts, storeLocal.LocalType),
                            storeLocal.LocalName);
                    }
                    else
                    {
                        state.Remove(storeLocal.LocalName);
                    }

                    break;
                }
                case SsaStoreLocalInstruction storeLocal:
                    InvalidateEscapedRawPointer(state, storeLocal.Value, definitions, values);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    InvalidateEscapedRawPointer(state, storeGlobal.Value, definitions, values);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    if (TryApplyDynamicStorageLengthStore(
                            storeIndirect,
                            state,
                            definitions,
                            values,
                            dynamicIntegerRanges))
                    {
                        break;
                    }

                    InvalidateDynamicStorageLocalAddress(state, storeIndirect.Address, definitions);
                    InvalidateEscapedRawPointer(state, storeIndirect.Value, definitions, values);
                    break;
                case SsaCopyMemoryInstruction copyMemory:
                    InvalidateDynamicStorageLocalAddress(state, copyMemory.DestinationAddress, definitions);
                    break;
                case SsaCallInstruction call:
                    InvalidateDirectCallDynamicStorageFacts(state, call, definitions, values, directCallParameterEffects);
                    break;
                case SsaIndirectCallInstruction call:
                    InvalidateIndirectCallDynamicStorageFacts(state, call, definitions, values);
                    break;
                case SsaValueInstruction valueInstruction:
                    AnalyzeDynamicStorageValueTransfer(
                        valueInstruction,
                        state,
                        conditionalMutations,
                        definitions,
                        values,
                        directCallParameterEffects,
                        ref changedFacts);
                    break;
            }
        }

        return new DynamicStorageBlockTransfer(state, conditionalMutations);
    }

    private static void AnalyzeDynamicStorageValueTransfer(
        SsaValueInstruction valueInstruction,
        Dictionary<string, SsaValueFacts> state,
        Dictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        Dictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects,
        ref bool changedFacts)
    {
        switch (valueInstruction.Value)
        {
            case SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal:
                if (state.TryGetValue(loadLocal.LocalName, out var localFacts))
                {
                    var renamed = RenameFacts(valueInstruction.ResultName, localFacts, loadLocal.Type);
                    if (!EqualityComparer<SsaValueFacts>.Default.Equals(values[valueInstruction.ResultName], renamed))
                    {
                        values[valueInstruction.ResultName] = renamed;
                        changedFacts = true;
                    }
                }

                return;
            case SsaDynamicStorageReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var reserveLocalName):
                ApplyUnconditionalDynamicStorageMutation(
                    state,
                    reserveLocalName,
                    reserve.StorageType,
                    current => ApplyDynamicStorageReserveAdditionalFacts(current, reserve.AdditionalCapacity, values));
                return;
            case SsaDynamicStorageTryReserveRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var reserveLocalName):
                ApplyConditionalDynamicStorageMutation(
                    state,
                    conditionalMutations,
                    valueInstruction.ResultName,
                    reserveLocalName,
                    reserve.StorageType,
                    current => ApplyDynamicStorageReserveAdditionalFacts(current, reserve.AdditionalCapacity, values));
                return;
            case SsaDynamicStorageTryReserveCapacityRValue reserve
                when TryResolveLocalAddressRoot(reserve.StorageAddress, definitions, out var reserveLocalName):
                ApplyConditionalDynamicStorageMutation(
                    state,
                    conditionalMutations,
                    valueInstruction.ResultName,
                    reserveLocalName,
                    reserve.StorageType,
                    current => ApplyDynamicStorageReserveCapacityFacts(current, reserve.TargetCapacity, values));
                return;
            case SsaDynamicStorageMoveLastRValue moveLast
                when TryResolveLocalAddressRoot(moveLast.StorageAddress, definitions, out var moveLastLocalName):
                ApplyUnconditionalDynamicStorageMutation(
                    state,
                    moveLastLocalName,
                    moveLast.StorageType,
                    ApplyDynamicStorageMoveOneFacts);
                return;
            case SsaDynamicStorageMoveAtRValue moveAt
                when TryResolveLocalAddressRoot(moveAt.StorageAddress, definitions, out var moveAtLocalName):
                ApplyUnconditionalDynamicStorageMutation(
                    state,
                    moveAtLocalName,
                    moveAt.StorageType,
                    ApplyDynamicStorageMoveOneFacts);
                return;
            case SsaDynamicStorageFreeRValue free:
                InvalidateFreedDynamicStorageOwner(state, free.Storage, values);
                return;
            case SsaCallRValue call:
                InvalidateDirectCallDynamicStorageFacts(state, call, definitions, values, directCallParameterEffects);
                return;
            case SsaIndirectCallRValue call:
                InvalidateIndirectCallDynamicStorageFacts(state, call, definitions, values);
                return;
        }
    }

    private static void RecordDynamicIntegerRange(
        SsaValueInstruction instruction,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        Dictionary<string, SsaValueFacts> values,
        Dictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        ref bool changedFacts)
    {
        if (!TryEvaluateDynamicStorageIntegerRange(
                instruction.Value,
                state,
                definitions,
                values,
                dynamicIntegerRanges,
                out var range))
        {
            dynamicIntegerRanges.Remove(instruction.ResultName);
            return;
        }

        var clamped = ClampToTypeRange(range, instruction.Value.Type);
        dynamicIntegerRanges[instruction.ResultName] = clamped;
        var updated = CreateIntegerRangeFacts(instruction.ResultName, instruction.Value.Type, clamped);
        if (!EqualityComparer<SsaValueFacts>.Default.Equals(values[instruction.ResultName], updated))
        {
            values[instruction.ResultName] = updated;
            changedFacts = true;
        }
    }

    private static bool TryEvaluateDynamicStorageIntegerRange(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        out SsaIntegerRangeFact range)
    {
        if (TryResolveDynamicStorageLengthReadRoot(value, definitions, out var localName)
            && state.TryGetValue(localName, out var current)
            && current.LengthKind == SsaFactLatticeKind.Known
            && current.LengthRange is { } lengthRange)
        {
            range = ClampDynamicStorageCountRange(lengthRange);
            return true;
        }

        switch (value)
        {
            case SsaUseRValue use:
                return TryGetDynamicAwareIntegerRange(use.Value, values, dynamicIntegerRanges, out range);
            case SsaConvertRValue convert
                when convert.TargetType.Kind == StarkTypeKind.Integer
                     && convert.Operand.Type == convert.TargetType:
                return TryGetDynamicAwareIntegerRange(convert.Operand, values, dynamicIntegerRanges, out range);
            case SsaBinaryRValue binary
                when binary.Type.Kind == StarkTypeKind.Integer
                     && TryGetDynamicAwareIntegerRange(binary.Left, values, dynamicIntegerRanges, out var left)
                     && TryGetDynamicAwareIntegerRange(binary.Right, values, dynamicIntegerRanges, out var right):
                return TryEvaluateDynamicIntegerBinaryRange(binary.Operator, left, right, out range);
            default:
                range = default!;
                return false;
        }
    }

    private static bool TryEvaluateDynamicIntegerBinaryRange(
        SsaBinaryOperator operation,
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right,
        out SsaIntegerRangeFact range)
    {
        switch (operation)
        {
            case SsaBinaryOperator.Add:
                range = new SsaIntegerRangeFact(left.Min + right.Min, left.Max + right.Max);
                return true;
            case SsaBinaryOperator.Subtract:
                range = new SsaIntegerRangeFact(left.Min - right.Max, left.Max - right.Min);
                return true;
            default:
                range = default!;
                return false;
        }
    }

    private static bool TryGetDynamicAwareIntegerRange(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges,
        out SsaIntegerRangeFact range)
    {
        if (value is SsaValueReference reference
            && dynamicIntegerRanges.TryGetValue(reference.Name, out range!))
        {
            return true;
        }

        var facts = AnalyzeValue("dynamic.integer", value, values);
        if (facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } knownRange)
        {
            range = knownRange;
            return true;
        }

        range = default!;
        return false;
    }

    private static bool TryResolveDynamicStorageLengthReadRoot(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        switch (value)
        {
            case SsaExtractFieldRValue extractField
                when IsDynamicLengthField(extractField)
                     && TryResolveDynamicStorageValueRoot(extractField.Target, definitions, out localName):
                return true;
            case SsaLoadIndirectRValue loadIndirect
                when TryResolveDynamicStorageFieldAddress(
                         loadIndirect.Address,
                         definitions,
                         out localName,
                         out _,
                         out var fieldName,
                         out var fieldIndex)
                     && IsDynamicLengthField(fieldName, fieldIndex):
                return true;
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveDynamicStorageValueRoot(
            value,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out localName);
    }

    private static bool TryResolveDynamicStorageValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName)
    {
        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            localName = string.Empty;
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => TryResolveDynamicStorageValueRoot(
                use.Value,
                definitions,
                visitedReferences,
                out localName),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal =>
                ReturnLocalName(loadLocal.LocalName, out localName),
            SsaLoadIndirectRValue { Type.Kind: StarkTypeKind.Dynamic } loadIndirect =>
                TryResolveLocalAddressRoot(loadIndirect.Address, definitions, visitedReferences, out localName),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Dynamic =>
                TryResolveDynamicStorageValueRoot(
                    convert.Operand,
                    definitions,
                    visitedReferences,
                    out localName),
            _ => ReturnNoLocalName(out localName)
        };
    }

    private static bool TryApplyDynamicStorageLengthStore(
        SsaStoreIndirectInstruction store,
        Dictionary<string, SsaValueFacts> state,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaIntegerRangeFact> dynamicIntegerRanges)
    {
        if (!TryResolveDynamicStorageFieldAddress(
                store.Address,
                definitions,
                out var localName,
                out var storageType,
                out var fieldName,
                out var fieldIndex)
            || fieldIndex != 1
            || !string.Equals(fieldName, "Length", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetDynamicAwareIntegerRange(
                store.Value,
                values,
                dynamicIntegerRanges,
                out var lengthRange))
        {
            return false;
        }

        var before = state.TryGetValue(localName, out var current)
            ? current
            : new SsaValueFacts(localName, storageType);
        var updated = NormalizeDynamicStorageFacts(WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName) with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = ClampDynamicStorageCountRange(lengthRange),
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = ClampDynamicStorageCountRange(lengthRange)
        });
        state[localName] = updated;
        return true;
    }

    private static void ApplyUnconditionalDynamicStorageMutation(
        Dictionary<string, SsaValueFacts> state,
        string localName,
        StarkTypeSymbol storageType,
        Func<SsaValueFacts, SsaValueFacts> mutate)
    {
        var before = state.TryGetValue(localName, out var known)
            ? known
            : CreateTypeFacts(localName, storageType);
        before = WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName);
        var after = mutate(before);
        if (HasDynamicStorageFactPayload(after))
        {
            state[localName] = WithDynamicStorageOwnerRoot(RenameFacts(localName, after, storageType), localName);
        }
        else
        {
            state.Remove(localName);
        }
    }

    private static void ApplyConditionalDynamicStorageMutation(
        Dictionary<string, SsaValueFacts> state,
        Dictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        string resultName,
        string localName,
        StarkTypeSymbol storageType,
        Func<SsaValueFacts, SsaValueFacts> mutate)
    {
        var before = state.TryGetValue(localName, out var known)
            ? known
            : CreateTypeFacts(localName, storageType);
        before = WithDynamicStorageOwnerRoot(RenameFacts(localName, before, storageType), localName);
        var onSuccess = WithDynamicStorageOwnerRoot(RenameFacts(localName, mutate(before), storageType), localName);
        conditionalMutations[resultName] = new DynamicStorageLocalMutation(localName, before, onSuccess);

        var joined = JoinFacts(localName, storageType, [before, onSuccess]);
        if (HasDynamicStorageFactPayload(joined))
        {
            state[localName] = joined;
        }
        else
        {
            state.Remove(localName);
        }
    }

    private static bool TryApplyDynamicStorageEdgeTransfer(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaValueFacts> exitState,
        IReadOnlyDictionary<string, DynamicStorageLocalMutation> conditionalMutations,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out IReadOnlyDictionary<string, SsaValueFacts> transferredState)
    {
        transferredState = exitState;
        Dictionary<string, SsaValueFacts>? edgeState = null;
        var currentState = exitState;

        if (conditionalMutations.Count != 0
            && TryResolveBranchConditionValue(
                    terminator,
                    target,
                    definitions,
                    out var conditionName,
                    out var branchWhenConditionTrue)
            && conditionalMutations.TryGetValue(conditionName, out var mutation))
        {
            edgeState = CloneDynamicStorageState(exitState);
            edgeState[mutation.LocalName] = branchWhenConditionTrue
                ? mutation.OnSuccess
                : mutation.Before;
            currentState = edgeState;
        }

        if (TryInferDynamicStorageLengthEdgeFacts(
                terminator,
                target,
                definitions,
                values,
                currentState,
                out var localName,
                out var refinedFacts,
                out var edgeIsReachable))
        {
            if (!edgeIsReachable)
            {
                return false;
            }

            edgeState ??= CloneDynamicStorageState(exitState);
            edgeState[localName] = refinedFacts;
        }

        transferredState = edgeState ?? exitState;
        return true;
    }

    private static bool TryInferDynamicStorageLengthEdgeFacts(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        out string localName,
        out SsaValueFacts refinedFacts,
        out bool edgeIsReachable)
    {
        localName = string.Empty;
        refinedFacts = default!;
        edgeIsReachable = true;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchWhenTrue;
        if (target == terminator.Targets[0])
        {
            branchWhenTrue = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchWhenTrue = false;
        }
        else
        {
            return false;
        }

        if (!TryResolveBranchComparison(
                terminator,
                target,
                definitions,
                out var comparison,
                out branchWhenTrue))
        {
            return false;
        }

        return TryInferDynamicStorageLengthComparisonFacts(
                   comparison.Left,
                   comparison.Operator,
                   comparison.Right,
                   branchWhenTrue,
                   definitions,
                   values,
                   state,
                   out localName,
                   out refinedFacts,
                   out edgeIsReachable)
               || TryMirrorComparisonOperator(comparison.Operator, out var mirroredOperator)
               && TryInferDynamicStorageLengthComparisonFacts(
                   comparison.Right,
                   mirroredOperator,
                   comparison.Left,
                   branchWhenTrue,
                   definitions,
                   values,
                   state,
                   out localName,
                   out refinedFacts,
                   out edgeIsReachable);
    }

    private static bool TryResolveBranchComparison(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaBinaryRValue comparison,
        out bool branchWhenComparisonTrue)
    {
        comparison = default!;
        branchWhenComparisonTrue = false;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchTakesCondition;
        if (target == terminator.Targets[0])
        {
            branchTakesCondition = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchTakesCondition = false;
        }
        else
        {
            return false;
        }

        if (!TryResolveBooleanSource(
                terminator.Condition,
                branchTakesCondition,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var conditionName,
                out branchWhenComparisonTrue)
            || !definitions.TryGetValue(conditionName, out var definition)
            || definition is not SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool } resolvedComparison)
        {
            return false;
        }

        comparison = resolvedComparison;
        return true;
    }

    private static bool TryInferDynamicStorageLengthComparisonFacts(
        SsaValue lengthValue,
        SsaBinaryOperator comparisonOperator,
        SsaValue constant,
        bool branchWhenTrue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, SsaValueFacts> state,
        out string localName,
        out SsaValueFacts refinedFacts,
        out bool edgeIsReachable)
    {
        localName = string.Empty;
        refinedFacts = default!;
        edgeIsReachable = true;
        if (!TryResolveDynamicStorageLengthValueRoot(lengthValue, definitions, out localName)
            || !TryGetIntegerSingleton(constant, values, out var constantValue)
            || !TryBuildComparisonRangeConstraint(
                comparisonOperator,
                constantValue,
                branchWhenTrue,
                out var min,
                out var max)
            || !state.TryGetValue(localName, out var current)
            || current.LengthKind != SsaFactLatticeKind.Known
            || current.LengthRange is not { } currentLength)
        {
            return false;
        }

        var refinedLength = new SsaIntegerRangeFact(
            min is { } lowerBound ? Max(currentLength.Min, lowerBound) : currentLength.Min,
            max is { } upperBound ? Min(currentLength.Max, upperBound) : currentLength.Max);
        if (refinedLength.Min > refinedLength.Max)
        {
            edgeIsReachable = false;
            return true;
        }

        refinedLength = ClampDynamicStorageCountRange(refinedLength);
        if (refinedLength.Min > refinedLength.Max)
        {
            edgeIsReachable = false;
            return true;
        }

        refinedFacts = NormalizeDynamicStorageFacts(current with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = refinedLength,
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = refinedLength
        });
        return true;
    }

    private static bool TryResolveDynamicStorageLengthValueRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        localName = string.Empty;
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        var visitedReferences = new HashSet<string>(StringComparer.Ordinal);
        var current = reference;
        while (visitedReferences.Add(current.Name)
               && definitions.TryGetValue(current.Name, out var definition))
        {
            if (TryResolveDynamicStorageLengthReadRoot(definition, definitions, out localName))
            {
                return true;
            }

            switch (definition)
            {
                case SsaUseRValue { Value: SsaValueReference next }:
                    current = next;
                    continue;
                case SsaConvertRValue { Operand: SsaValueReference next }:
                    current = next;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryResolveBranchConditionValue(
        SsaTerminator terminator,
        int target,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string conditionName,
        out bool branchWhenConditionTrue)
    {
        conditionName = string.Empty;
        branchWhenConditionTrue = false;
        if (terminator.Kind != SsaTerminatorKind.Branch
            || terminator.Targets.Count != 2
            || terminator.Condition is null
            || terminator.Targets[0] == terminator.Targets[1])
        {
            return false;
        }

        bool branchTakesCondition;
        if (target == terminator.Targets[0])
        {
            branchTakesCondition = true;
        }
        else if (target == terminator.Targets[1])
        {
            branchTakesCondition = false;
        }
        else
        {
            return false;
        }

        return TryResolveBooleanSource(
            terminator.Condition,
            branchTakesCondition,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out conditionName,
            out branchWhenConditionTrue);
    }

    private static bool TryResolveBooleanSource(
        SsaValue value,
        bool branchTakesValue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string valueName,
        out bool branchWhenValueTrue)
    {
        valueName = string.Empty;
        branchWhenValueTrue = branchTakesValue;
        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name))
        {
            return false;
        }

        if (!definitions.TryGetValue(reference.Name, out var definition))
        {
            valueName = reference.Name;
            branchWhenValueTrue = branchTakesValue;
            return true;
        }

        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveBooleanSource(use.Value, branchTakesValue, definitions, visitedReferences, out valueName, out branchWhenValueTrue);
            case SsaUnaryRValue { Operator: SsaUnaryOperator.LogicalNot } unary:
                return TryResolveBooleanSource(unary.Operand, !branchTakesValue, definitions, visitedReferences, out valueName, out branchWhenValueTrue);
            default:
                valueName = reference.Name;
                branchWhenValueTrue = branchTakesValue;
                return true;
        }
    }

    private static bool MergeDynamicStorageEntryState(
        int targetBlockId,
        IReadOnlyDictionary<string, SsaValueFacts> edgeState,
        Dictionary<int, Dictionary<string, SsaValueFacts>> entryStates,
        HashSet<int> initializedEntries)
    {
        if (!initializedEntries.Add(targetBlockId))
        {
            var existingState = entryStates[targetBlockId];
            var joinedState = JoinDynamicStorageStates(existingState, edgeState);
            if (DynamicStorageStatesEqual(existingState, joinedState))
            {
                return false;
            }

            entryStates[targetBlockId] = joinedState;
            return true;
        }

        entryStates[targetBlockId] = CloneDynamicStorageState(edgeState);
        return edgeState.Count != 0;
    }

    private static Dictionary<string, SsaValueFacts> JoinDynamicStorageStates(
        IReadOnlyDictionary<string, SsaValueFacts> left,
        IReadOnlyDictionary<string, SsaValueFacts> right)
    {
        var joined = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);
        foreach (var (localName, leftFacts) in left)
        {
            if (!right.TryGetValue(localName, out var rightFacts)
                || leftFacts.Type.Kind != StarkTypeKind.Dynamic
                || rightFacts.Type != leftFacts.Type)
            {
                continue;
            }

            var localFacts = JoinFacts(localName, leftFacts.Type, [leftFacts, rightFacts]);
            if (HasDynamicStorageFactPayload(localFacts))
            {
                joined[localName] = localFacts;
            }
        }

        return joined;
    }

    private static Dictionary<string, SsaValueFacts> CloneDynamicStorageState(
        IReadOnlyDictionary<string, SsaValueFacts> state)
    {
        return new Dictionary<string, SsaValueFacts>(state, StringComparer.Ordinal);
    }

    private static bool DynamicStorageStatesEqual(
        IReadOnlyDictionary<string, SsaValueFacts> left,
        IReadOnlyDictionary<string, SsaValueFacts> right)
    {
        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var rightFacts)
                                   && EqualityComparer<SsaValueFacts>.Default.Equals(pair.Value, rightFacts));
    }

    private static void InvalidateDynamicStorageLocalAddress(
        Dictionary<string, SsaValueFacts> state,
        SsaValue address,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (TryResolveLocalAddressRoot(address, definitions, out var localName))
        {
            RemoveOverlappingDynamicStorageFacts(state, localName);
        }
    }

    private static void InvalidateDirectCallDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            if (SsaDynamicStorageCallFactPolicy.ShouldPreserveDirectCallArgumentDynamicFacts(
                    call,
                    index,
                    directCallParameterEffects))
            {
                continue;
            }

            var argument = call.Arguments[index];
            InvalidateEscapedRawPointer(state, argument, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, argument, definitions);
        }

        for (var index = 0; index < (call.IndirectArgumentAddresses?.Count ?? 0); index++)
        {
            if (SsaDynamicStorageCallFactPolicy.ShouldPreserveDirectCallArgumentDynamicFacts(
                    call,
                    index,
                    directCallParameterEffects))
            {
                continue;
            }

            var address = call.IndirectArgumentAddresses![index];
            if (address is null)
            {
                continue;
            }

            InvalidateEscapedRawPointer(state, address, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, address, definitions);
        }
    }

    private static void InvalidateIndirectCallDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        ISsaIndirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        foreach (var argument in call.Arguments)
        {
            InvalidateEscapedRawPointer(state, argument, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, argument, definitions);
        }

        foreach (var address in call.IndirectArgumentAddresses ?? [])
        {
            if (address is null)
            {
                continue;
            }

            InvalidateEscapedRawPointer(state, address, definitions, values);
            InvalidateDynamicStorageLocalAddress(state, address, definitions);
        }
    }

    private static void InvalidateEscapedRawPointer(
        Dictionary<string, SsaValueFacts> state,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        if (value.Type.Kind == StarkTypeKind.RawPointer
            && TryResolveLocalAddressRoot(value, definitions, out var localName))
        {
            RemoveOverlappingDynamicStorageFacts(state, localName);
        }

        if (TryResolveDynamicStorageViewOwnerRoot(
                value,
                definitions,
                values,
                new HashSet<string>(StringComparer.Ordinal),
                out var ownerRootName))
        {
            RemoveOverlappingDynamicStorageFacts(state, ownerRootName);
        }
    }

    private static bool TryResolveDynamicStorageViewOwnerRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        ownerRootName = string.Empty;

        switch (value)
        {
            case SsaValueReference reference:
                if (values.TryGetValue(reference.Name, out var facts)
                    && facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
                    && facts.DynamicStorageRegion?.OwnerRootName is { Length: > 0 } directOwnerRoot)
                {
                    ownerRootName = directOwnerRoot;
                    return true;
                }

                if (!visitedReferences.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveDynamicStorageViewOwnerRoot(
                    definition,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            default:
                return false;
        }
    }

    private static bool TryResolveDynamicStorageViewOwnerRoot(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveDynamicStorageViewOwnerRoot(
                    use.Value,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryResolveDynamicStorageViewOwnerRoot(
                    convert.Operand,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaExtractFieldRValue extractField
                when extractField.Target.Type.Kind == StarkTypeKind.Dynamic
                     && extractField.Type.Kind == StarkTypeKind.RawPointer
                     && IsDynamicDataField(extractField):
                return TryResolveDynamicStorageOwnerRoot(
                    extractField.Target,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaElementAddressRValue elementAddress:
                return TryResolveDynamicStorageViewOwnerRoot(
                    elementAddress.Address,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaSliceElementAddressRValue sliceElementAddress:
                return TryResolveDynamicStorageViewOwnerRoot(
                    sliceElementAddress.Slice,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            case SsaMakeSliceFromPointerRValue makeSlice:
                return TryResolveDynamicStorageViewOwnerRoot(
                    makeSlice.Pointer,
                    definitions,
                    values,
                    visitedReferences,
                    out ownerRootName);
            default:
                ownerRootName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageOwnerRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        ISet<string> visitedReferences,
        out string ownerRootName)
    {
        ownerRootName = string.Empty;
        if (value is SsaValueReference reference)
        {
            if (values.TryGetValue(reference.Name, out var facts)
                && facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
                && facts.DynamicStorageRegion?.OwnerRootName is { Length: > 0 } ownerRoot)
            {
                ownerRootName = ownerRoot;
                return true;
            }

            if (visitedReferences.Add(reference.Name)
                && definitions.TryGetValue(reference.Name, out var definition))
            {
                return definition switch
                {
                    SsaUseRValue use => TryResolveDynamicStorageOwnerRoot(
                        use.Value,
                        definitions,
                        values,
                        visitedReferences,
                        out ownerRootName),
                    SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal =>
                        ReturnLocalName(loadLocal.LocalName, out ownerRootName),
                    SsaLoadIndirectRValue { Type.Kind: StarkTypeKind.Dynamic } loadIndirect =>
                        TryResolveLocalAddressRoot(loadIndirect.Address, definitions, visitedReferences, out ownerRootName),
                    _ => false
                };
            }
        }

        return false;
    }

    private static bool TryResolveLocalAddressRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        return TryResolveLocalAddressRoot(value, definitions, new HashSet<string>(StringComparer.Ordinal), out localName);
    }

    private static bool TryResolveLocalAddressRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName)
    {
        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedReferences.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    localName = string.Empty;
                    return false;
                }

                return definition switch
                {
                    SsaAddressOfLocalRValue addressOfLocal => ReturnLocalName(addressOfLocal.LocalName, out localName),
                    SsaAddressOfParameterRValue addressOfParameter => ReturnLocalName(addressOfParameter.ParameterName, out localName),
                    SsaUseRValue use => TryResolveLocalAddressRoot(use.Value, definitions, visitedReferences, out localName),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer
                        => TryResolveLocalAddressRoot(convert.Operand, definitions, visitedReferences, out localName),
                    SsaLoadIndirectRValue { Type.Kind: StarkTypeKind.Dynamic } loadIndirect
                        => TryResolveLocalAddressRoot(loadIndirect.Address, definitions, visitedReferences, out localName),
                    SsaFieldAddressRValue fieldAddress
                        when TryResolveLocalAddressRoot(fieldAddress.Address, definitions, visitedReferences, out var parentRoot) =>
                        ReturnLocalName($"{parentRoot}.{fieldAddress.FieldName}", out localName),
                    SsaElementAddressRValue elementAddress
                        when TryResolveLocalAddressRoot(elementAddress.Address, definitions, visitedReferences, out var parentRoot) =>
                        ReturnLocalName($"{parentRoot}[*]", out localName),
                    _ => ReturnNoLocalName(out localName)
                };
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static bool TryResolveDynamicStorageFieldAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName,
        out StarkTypeSymbol storageType,
        out string fieldName,
        out int fieldIndex)
    {
        return TryResolveDynamicStorageFieldAddress(
            value,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out localName,
            out storageType,
            out fieldName,
            out fieldIndex);
    }

    private static bool TryResolveDynamicStorageFieldAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedReferences,
        out string localName,
        out StarkTypeSymbol storageType,
        out string fieldName,
        out int fieldIndex)
    {
        localName = string.Empty;
        storageType = StarkTypeSymbols.Error;
        fieldName = string.Empty;
        fieldIndex = -1;

        if (value is not SsaValueReference reference
            || !visitedReferences.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveDynamicStorageFieldAddress(
                    use.Value,
                    definitions,
                    visitedReferences,
                    out localName,
                    out storageType,
                    out fieldName,
                    out fieldIndex);
            case SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer:
                return TryResolveDynamicStorageFieldAddress(
                    convert.Operand,
                    definitions,
                    visitedReferences,
                    out localName,
                    out storageType,
                    out fieldName,
                    out fieldIndex);
            case SsaFieldAddressRValue fieldAddress
                when fieldAddress.AggregateType.Kind == StarkTypeKind.Dynamic
                     && TryResolveLocalAddressRoot(fieldAddress.Address, definitions, out localName):
                storageType = fieldAddress.AggregateType;
                fieldName = fieldAddress.FieldName;
                fieldIndex = fieldAddress.FieldIndex;
                return true;
            default:
                return false;
        }
    }

    private static bool ReturnLocalName(string value, out string localName)
    {
        localName = value;
        return true;
    }

    private static bool ReturnNoLocalName(out string localName)
    {
        localName = string.Empty;
        return false;
    }

    private static void RemoveOverlappingDynamicStorageFacts(
        Dictionary<string, SsaValueFacts> state,
        string escapedRoot)
    {
        foreach (var ownerRoot in state.Keys
                     .Where(ownerRoot => DynamicStorageOwnerRootsMayOverlap(ownerRoot, escapedRoot))
                     .ToArray())
        {
            state.Remove(ownerRoot);
        }
    }

    private static bool DynamicStorageOwnerRootsMayOverlap(string ownerRoot, string escapedRoot)
    {
        return string.Equals(ownerRoot, escapedRoot, StringComparison.Ordinal)
               || ownerRoot.StartsWith($"{escapedRoot}.", StringComparison.Ordinal)
               || escapedRoot.StartsWith($"{ownerRoot}.", StringComparison.Ordinal)
               || ownerRoot.StartsWith($"{escapedRoot}[*]", StringComparison.Ordinal)
               || escapedRoot.StartsWith($"{ownerRoot}[*]", StringComparison.Ordinal);
    }

    private static bool HasDynamicStorageFactPayload(SsaValueFacts facts)
    {
        return facts.Type.Kind == StarkTypeKind.Dynamic
               && (facts.LengthKind == SsaFactLatticeKind.Known
                   || facts.CapacityKind == SsaFactLatticeKind.Known
                   || facts.InitializedPrefixKind == SsaFactLatticeKind.Known
                   || facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known);
    }

    private static SsaValueFacts ApplyDynamicStorageReserveAdditionalFacts(
        SsaValueFacts current,
        SsaValue additionalCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        var additionalFacts = AnalyzeValue("dynamic.reserve.additional", additionalCapacity, values);
        if (additionalFacts.IntegerRangeKind != SsaFactLatticeKind.Known
            || additionalFacts.IntegerRange is not { } additionalRange)
        {
            return WithDynamicStorageBackingIdentityUnknown(current);
        }

        var provenNoop = current.LengthKind == SsaFactLatticeKind.Known
                         && current.LengthRange is { } currentLengthRange
                         && current.CapacityKind == SsaFactLatticeKind.Known
                         && current.CapacityRange is { } currentCapacityRange
                         && currentCapacityRange.Min >= Max(BigInteger.Zero, currentLengthRange.Max)
                            + Max(BigInteger.Zero, additionalRange.Max);
        BigInteger? requiredLowerBound = null;
        if (current.LengthKind == SsaFactLatticeKind.Known
            && current.LengthRange is { } lengthRange)
        {
            requiredLowerBound = Max(BigInteger.Zero, lengthRange.Min) + Max(BigInteger.Zero, additionalRange.Min);
        }

        var updated = NormalizeDynamicStorageFacts(RaiseDynamicStorageCapacityLowerBound(current, requiredLowerBound));
        return provenNoop
            ? updated
            : WithDynamicStorageBackingIdentityUnknown(updated);
    }

    private static SsaValueFacts ApplyDynamicStorageReserveCapacityFacts(
        SsaValueFacts current,
        SsaValue targetCapacity,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        var targetFacts = AnalyzeValue("dynamic.reserve.capacity", targetCapacity, values);
        if (targetFacts.IntegerRangeKind != SsaFactLatticeKind.Known
            || targetFacts.IntegerRange is not { } targetRange)
        {
            return WithDynamicStorageBackingIdentityUnknown(current);
        }

        var provenNoop = current.CapacityKind == SsaFactLatticeKind.Known
                         && current.CapacityRange is { } capacityRange
                         && capacityRange.Min >= Max(BigInteger.Zero, targetRange.Max);
        var updated = NormalizeDynamicStorageFacts(RaiseDynamicStorageCapacityLowerBound(current, Max(BigInteger.Zero, targetRange.Min)));
        return provenNoop
            ? updated
            : WithDynamicStorageBackingIdentityUnknown(updated);
    }

    private static SsaValueFacts RaiseDynamicStorageCapacityLowerBound(
        SsaValueFacts current,
        BigInteger? requiredLowerBound)
    {
        if (requiredLowerBound is null
            && current.CapacityKind != SsaFactLatticeKind.Known)
        {
            return current;
        }

        var upperBound = GetDynamicStorageCountUpperBound();
        var lowerBound = BigInteger.Zero;
        if (current.CapacityKind == SsaFactLatticeKind.Known
            && current.CapacityRange is { } capacityRange)
        {
            lowerBound = Max(BigInteger.Zero, capacityRange.Min);
        }

        if (requiredLowerBound is { } required)
        {
            lowerBound = Max(lowerBound, required);
        }

        lowerBound = Min(lowerBound, upperBound);
        return current with
        {
            CapacityKind = SsaFactLatticeKind.Known,
            CapacityRange = new SsaIntegerRangeFact(lowerBound, upperBound)
        };
    }

    private static SsaValueFacts NormalizeDynamicStorageFacts(SsaValueFacts facts)
    {
        if (facts.Type.Kind != StarkTypeKind.Dynamic)
        {
            return facts;
        }

        var normalized = facts;
        if (normalized.LengthKind == SsaFactLatticeKind.Known
            && normalized.LengthRange is { } lengthRange)
        {
            var clampedLength = ClampDynamicStorageCountRange(lengthRange);
            normalized = normalized with
            {
                LengthRange = clampedLength
            };

            if (normalized.InitializedPrefixKind != SsaFactLatticeKind.Known
                || normalized.InitializedPrefixRange is null)
            {
                normalized = normalized with
                {
                    InitializedPrefixKind = SsaFactLatticeKind.Known,
                    InitializedPrefixRange = clampedLength
                };
            }

            var upperBound = GetDynamicStorageCountUpperBound();
            if (normalized.CapacityKind == SsaFactLatticeKind.Known
                && normalized.CapacityRange is { } capacityRange)
            {
                var clampedCapacity = ClampDynamicStorageCountRange(capacityRange);
                var capacityMin = Max(clampedCapacity.Min, clampedLength.Min);
                normalized = normalized with
                {
                    CapacityRange = new SsaIntegerRangeFact(
                        capacityMin,
                        Max(capacityMin, clampedCapacity.Max))
                };
            }
            else
            {
                normalized = normalized with
                {
                    CapacityKind = SsaFactLatticeKind.Known,
                    CapacityRange = new SsaIntegerRangeFact(clampedLength.Min, upperBound)
                };
            }
        }

        if (normalized.InitializedPrefixKind == SsaFactLatticeKind.Known
            && normalized.InitializedPrefixRange is { } initializedRange)
        {
            normalized = normalized with
            {
                InitializedPrefixRange = ClampDynamicStorageCountRange(initializedRange)
            };
        }

        return WithDynamicStorageRegionFromCurrentFacts(normalized);
    }

    private static SsaValueFacts WithDynamicStorageRegionFromCurrentFacts(SsaValueFacts facts)
    {
        if (facts.Type.Kind != StarkTypeKind.Dynamic
            || facts.Type.ElementType is not { } elementType)
        {
            return facts with
            {
                DynamicStorageRegionKind = SsaFactLatticeKind.Unknown,
                DynamicStorageRegion = null
            };
        }

        var existingRegion = facts.DynamicStorageRegionKind == SsaFactLatticeKind.Known
            ? facts.DynamicStorageRegion
            : null;
        var lengthRange = facts.LengthKind == SsaFactLatticeKind.Known
            ? facts.LengthRange
            : existingRegion?.InitializedLengthRange;
        var capacityRange = facts.CapacityKind == SsaFactLatticeKind.Known
            ? facts.CapacityRange
            : existingRegion?.CapacityRange;
        var initializedPrefixRange = facts.InitializedPrefixKind == SsaFactLatticeKind.Known
            ? facts.InitializedPrefixRange
            : existingRegion?.InitializedPrefixRange;

        if (existingRegion is null
            && lengthRange is null
            && capacityRange is null
            && initializedPrefixRange is null)
        {
            return facts;
        }

        var elementAlignmentBytes = TryGetTypeAlignmentBytes(elementType, out var alignmentBytes)
            ? alignmentBytes
            : existingRegion?.ElementAlignmentBytes;
        var backingKind = InferDynamicStorageBackingAllocationKind(
            existingRegion?.BackingAllocationKind ?? SsaDynamicStorageBackingAllocationKind.Unknown,
            capacityRange);
        var backingAllocationId = backingKind is SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
                or SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            ? existingRegion?.BackingAllocationId
            : null;

        return facts with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = new SsaDynamicStorageRegionFact(
                existingRegion?.OwnerRootName,
                backingKind,
                backingAllocationId,
                elementType,
                capacityRange,
                lengthRange,
                initializedPrefixRange,
                TryCreateDynamicStorageSpareCapacityRange(lengthRange, capacityRange),
                elementAlignmentBytes,
                existingRegion?.AllocatorProvenance ?? SsaDynamicStorageAllocatorProvenanceKind.Unknown)
        };
    }

    private static SsaValueFacts WithDynamicStorageOwnerRoot(SsaValueFacts facts, string ownerRootName)
    {
        var normalized = NormalizeDynamicStorageFacts(facts);
        if (normalized.Type.Kind != StarkTypeKind.Dynamic
            || normalized.Type.ElementType is not { } elementType)
        {
            return normalized;
        }

        var region = normalized.DynamicStorageRegionKind == SsaFactLatticeKind.Known
            ? normalized.DynamicStorageRegion
            : null;
        region ??= new SsaDynamicStorageRegionFact(
            ownerRootName,
            SsaDynamicStorageBackingAllocationKind.Unknown,
            null,
            elementType,
            null,
            null,
            null,
            null,
            TryGetTypeAlignmentBytes(elementType, out var alignmentBytes) ? alignmentBytes : null,
            SsaDynamicStorageAllocatorProvenanceKind.Unknown);

        return normalized with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = region with { OwnerRootName = ownerRootName }
        };
    }

    private static SsaValueFacts WithDynamicStorageAllocationIdentity(
        SsaValueFacts facts,
        string allocationId,
        SsaDynamicStorageAllocatorProvenanceKind allocatorProvenance)
    {
        var normalized = NormalizeDynamicStorageFacts(facts);
        if (normalized.Type.Kind != StarkTypeKind.Dynamic
            || normalized.Type.ElementType is not { } elementType)
        {
            return normalized;
        }

        var region = normalized.DynamicStorageRegionKind == SsaFactLatticeKind.Known
            ? normalized.DynamicStorageRegion
            : null;
        var backingKind = InferDynamicStorageBackingAllocationKind(
            region?.BackingAllocationKind ?? SsaDynamicStorageBackingAllocationKind.Unknown,
            normalized.CapacityKind == SsaFactLatticeKind.Known ? normalized.CapacityRange : region?.CapacityRange);
        if (backingKind == SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
            && allocatorProvenance == SsaDynamicStorageAllocatorProvenanceKind.ArenaFrame)
        {
            backingKind = SsaDynamicStorageBackingAllocationKind.ArenaAllocation;
        }

        var backingId = backingKind is SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
                or SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            ? allocationId
            : null;

        region ??= new SsaDynamicStorageRegionFact(
            null,
            backingKind,
            backingId,
            elementType,
            normalized.CapacityRange,
            normalized.LengthRange,
            normalized.InitializedPrefixRange,
            TryCreateDynamicStorageSpareCapacityRange(normalized.LengthRange, normalized.CapacityRange),
            TryGetTypeAlignmentBytes(elementType, out var alignmentBytes) ? alignmentBytes : null,
            allocatorProvenance);

        return normalized with
        {
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = region with
            {
                BackingAllocationKind = backingKind,
                BackingAllocationId = backingId,
                AllocatorProvenance = allocatorProvenance
            }
        };
    }

    private static SsaValueFacts WithDynamicStorageBackingIdentityUnknown(SsaValueFacts facts)
    {
        var normalized = NormalizeDynamicStorageFacts(facts);
        if (normalized.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || normalized.DynamicStorageRegion is not { } region)
        {
            return normalized;
        }

        var backingKind = InferDynamicStorageBackingAllocationKind(
            SsaDynamicStorageBackingAllocationKind.Unknown,
            region.CapacityRange);
        return normalized with
        {
            DynamicStorageRegion = region with
            {
                BackingAllocationKind = backingKind,
                BackingAllocationId = null
            }
        };
    }

    private static SsaValueFacts JoinDynamicStorageRegionFacts(
        string valueName,
        StarkTypeSymbol type,
        SsaValueFacts joined,
        IReadOnlyList<SsaValueFacts> inputs)
    {
        if (type.ElementType is not { } elementType)
        {
            return joined;
        }

        var regions = inputs
            .Where(static fact => fact.DynamicStorageRegionKind == SsaFactLatticeKind.Known
                                  && fact.DynamicStorageRegion is not null)
            .Select(static fact => fact.DynamicStorageRegion!)
            .ToArray();
        if (regions.Length != inputs.Count)
        {
            return NormalizeDynamicStorageFacts(joined);
        }

        var ownerRootNames = regions
            .Select(static region => region.OwnerRootName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allocatorProvenances = regions
            .Select(static region => region.AllocatorProvenance)
            .Distinct()
            .ToArray();
        var backingIds = regions
            .Select(static region => region.BackingAllocationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sameBackingIdentity = backingIds.Length == 1
                                  && regions.Select(static region => region.BackingAllocationKind).Distinct().Count() == 1;
        var normalized = NormalizeDynamicStorageFacts(joined);
        var currentRegion = normalized.DynamicStorageRegion;
        var backingKind = sameBackingIdentity
            ? regions[0].BackingAllocationKind
            : InferDynamicStorageBackingAllocationKind(
                SsaDynamicStorageBackingAllocationKind.Unknown,
                normalized.CapacityKind == SsaFactLatticeKind.Known ? normalized.CapacityRange : null);

        return normalized with
        {
            ValueName = valueName,
            DynamicStorageRegionKind = SsaFactLatticeKind.Known,
            DynamicStorageRegion = new SsaDynamicStorageRegionFact(
                ownerRootNames.Length == 1 ? ownerRootNames[0] : null,
                backingKind,
                sameBackingIdentity && backingKind is SsaDynamicStorageBackingAllocationKind.RuntimeAllocation
                    or SsaDynamicStorageBackingAllocationKind.ArenaAllocation
                    ? backingIds[0]
                    : null,
                elementType,
                currentRegion?.CapacityRange,
                currentRegion?.InitializedLengthRange,
                currentRegion?.InitializedPrefixRange,
                currentRegion?.SpareCapacityRange,
                TryGetTypeAlignmentBytes(elementType, out var alignmentBytes)
                    ? alignmentBytes
                    : currentRegion?.ElementAlignmentBytes,
                allocatorProvenances.Length == 1
                    ? allocatorProvenances[0]
                    : SsaDynamicStorageAllocatorProvenanceKind.Unknown)
        };
    }

    private static SsaValueFacts CreateEmptyDynamicStorageFacts(string valueName, StarkTypeSymbol type)
    {
        var zero = new SsaIntegerRangeFact(BigInteger.Zero, BigInteger.Zero);
        return NormalizeDynamicStorageFacts(new SsaValueFacts(
            valueName,
            type,
            LengthKind: SsaFactLatticeKind.Known,
            LengthRange: zero,
            CapacityKind: SsaFactLatticeKind.Known,
            CapacityRange: zero,
            InitializedPrefixKind: SsaFactLatticeKind.Known,
            InitializedPrefixRange: zero,
            DynamicStorageRegionKind: SsaFactLatticeKind.Known,
            DynamicStorageRegion: type.ElementType is { } elementType
                ? new SsaDynamicStorageRegionFact(
                    null,
                    SsaDynamicStorageBackingAllocationKind.None,
                    null,
                    elementType,
                    zero,
                    zero,
                    zero,
                    zero,
                    TryGetTypeAlignmentBytes(elementType, out var alignmentBytes) ? alignmentBytes : null,
                    SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault)
                : null));
    }

    private static SsaDynamicStorageBackingAllocationKind InferDynamicStorageBackingAllocationKind(
        SsaDynamicStorageBackingAllocationKind existingKind,
        SsaIntegerRangeFact? capacityRange)
    {
        if (capacityRange is null)
        {
            return existingKind;
        }

        if (capacityRange.Max <= BigInteger.Zero)
        {
            return SsaDynamicStorageBackingAllocationKind.None;
        }

        if (capacityRange.Min <= BigInteger.Zero)
        {
            return SsaDynamicStorageBackingAllocationKind.Unknown;
        }

        return existingKind == SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            ? SsaDynamicStorageBackingAllocationKind.ArenaAllocation
            : SsaDynamicStorageBackingAllocationKind.RuntimeAllocation;
    }

    private static SsaIntegerRangeFact? TryCreateDynamicStorageSpareCapacityRange(
        SsaIntegerRangeFact? lengthRange,
        SsaIntegerRangeFact? capacityRange)
    {
        if (lengthRange is null || capacityRange is null)
        {
            return null;
        }

        var min = Max(BigInteger.Zero, capacityRange.Min - lengthRange.Max);
        var max = Max(BigInteger.Zero, capacityRange.Max - lengthRange.Min);
        return new SsaIntegerRangeFact(min, Max(min, max));
    }

    private static SsaValueFacts ApplyDynamicStorageMoveOneFacts(SsaValueFacts current)
    {
        if (current.LengthKind != SsaFactLatticeKind.Known
            || current.LengthRange is not { } lengthRange)
        {
            return current;
        }

        var newLength = new SsaIntegerRangeFact(
            lengthRange.Min > BigInteger.Zero ? lengthRange.Min - BigInteger.One : BigInteger.Zero,
            lengthRange.Max > BigInteger.Zero ? lengthRange.Max - BigInteger.One : BigInteger.Zero);

        return NormalizeDynamicStorageFacts(current with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = ClampDynamicStorageCountRange(newLength),
            InitializedPrefixKind = SsaFactLatticeKind.Known,
            InitializedPrefixRange = ClampDynamicStorageCountRange(newLength)
        });
    }

    private static void InvalidateFreedDynamicStorageOwner(
        Dictionary<string, SsaValueFacts> state,
        SsaValue storage,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        var facts = AnalyzeValue("dynamic.free.storage", storage, values);
        if (facts.DynamicStorageRegionKind != SsaFactLatticeKind.Known
            || facts.DynamicStorageRegion?.OwnerRootName is not { Length: > 0 } ownerRootName)
        {
            return;
        }

        RemoveOverlappingDynamicStorageFacts(state, ownerRootName);
    }

    private static bool IsLocalAddressTaken(
        SsaFunction function,
        ISet<int> reachableBlockIds,
        string localName)
    {
        foreach (var valueInstruction in function.Blocks
                     .Where(block => reachableBlockIds.Contains(block.Id))
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaValueInstruction>())
        {
            if (RValueTakesLocalAddress(valueInstruction.Value, localName))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool RValueTakesLocalAddress(SsaRValue value, string localName)
    {
        return value is SsaAddressOfLocalRValue addressOfLocal
               && string.Equals(addressOfLocal.LocalName, localName, StringComparison.Ordinal);
    }

    private static SsaValueFacts AnalyzeTextSlice(
        string valueName,
        SsaTextSliceRValue textSlice,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var textValue = AnalyzeValue(valueName, textSlice.TextValue, knownValues);
        var start = AnalyzeValue(valueName, textSlice.Start, knownValues);
        var length = AnalyzeValue(valueName, textSlice.Length, knownValues);
        if (textValue.TextLiteralPayloadKind == SsaFactLatticeKind.Known
            && textValue.TextLiteralPayload is { } payload
            && TryGetExactNonNegativeInteger(start, out var exactStart)
            && TryGetExactNonNegativeInteger(length, out var exactLength)
            && TrySliceTextLiteralPayload(textSlice.Type, payload, exactStart, exactLength, out var slicedPayload))
        {
            return CreateTextLiteralPayloadFacts(valueName, textSlice.Type, slicedPayload);
        }

        if (length.IntegerRangeKind == SsaFactLatticeKind.Known
            && length.IntegerRange is { } lengthRange)
        {
            return CreateTypeFacts(valueName, textSlice.Type) with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = lengthRange
            };
        }

        return CreateTypeFacts(valueName, textSlice.Type);
    }

    private static bool TryGetExactNonNegativeInteger(SsaValueFacts facts, out BigInteger value)
    {
        if (facts.IntegerRangeKind == SsaFactLatticeKind.Known
            && facts.IntegerRange is { } range
            && range.Min == range.Max
            && range.Min >= BigInteger.Zero)
        {
            value = range.Min;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TrySliceTextLiteralPayload(
        StarkTypeSymbol type,
        SsaTextLiteralPayloadFact payload,
        BigInteger start,
        BigInteger length,
        out SsaTextLiteralPayloadFact slicedPayload)
    {
        slicedPayload = null!;
        var sourceLength = type.Kind == StarkTypeKind.Unicode
            ? payload.Utf32Length
            : payload.Utf8Length;
        var end = start + length;
        if (start < BigInteger.Zero
            || length < BigInteger.Zero
            || end > sourceLength
            || start > int.MaxValue
            || length > int.MaxValue)
        {
            return false;
        }

        var slicedText = payload.DecodedText.Substring((int)start, (int)length);
        slicedPayload = CreateTextLiteralPayload(new DecodedTextLiteral(slicedText));
        return type.Kind != StarkTypeKind.Ascii || slicedPayload.IsAsciiOnly;
    }

    internal static bool TryEvaluateComparison(
        SsaBinaryOperator op,
        SsaValueFacts left,
        SsaValueFacts right,
        out bool value)
    {
        value = false;
        if (TryEvaluateNullComparison(op, left.Nullability, right.Nullability, out value))
        {
            return true;
        }

        if (TryEvaluateKnownBitsEquality(op, left, right, out value))
        {
            return true;
        }

        if (left.IntegerRangeKind != SsaFactLatticeKind.Known
            || right.IntegerRangeKind != SsaFactLatticeKind.Known
            || left.IntegerRange is not { } leftRange
            || right.IntegerRange is not { } rightRange)
        {
            return false;
        }

        switch (op)
        {
            case SsaBinaryOperator.Equal:
                if (leftRange.Min == leftRange.Max
                    && rightRange.Min == rightRange.Max
                    && leftRange.Min == rightRange.Min)
                {
                    value = true;
                    return true;
                }

                if (leftRange.Max < rightRange.Min || rightRange.Max < leftRange.Min)
                {
                    value = false;
                    return true;
                }

                return false;
            case SsaBinaryOperator.NotEqual:
                if (leftRange.Min == leftRange.Max
                    && rightRange.Min == rightRange.Max
                    && leftRange.Min == rightRange.Min)
                {
                    value = false;
                    return true;
                }

                if (leftRange.Max < rightRange.Min || rightRange.Max < leftRange.Min)
                {
                    value = true;
                    return true;
                }

                return false;
            case SsaBinaryOperator.LessThan:
                return TryProveOrderedComparison(
                    leftRange.Max < rightRange.Min,
                    leftRange.Min >= rightRange.Max,
                    out value);
            case SsaBinaryOperator.LessThanOrEqual:
                return TryProveOrderedComparison(
                    leftRange.Max <= rightRange.Min,
                    leftRange.Min > rightRange.Max,
                    out value);
            case SsaBinaryOperator.GreaterThan:
                return TryProveOrderedComparison(
                    leftRange.Min > rightRange.Max,
                    leftRange.Max <= rightRange.Min,
                    out value);
            case SsaBinaryOperator.GreaterThanOrEqual:
                return TryProveOrderedComparison(
                    leftRange.Min >= rightRange.Max,
                    leftRange.Max < rightRange.Min,
                    out value);
            default:
                return false;
        }
    }

    private static bool TryEvaluateKnownBitsEquality(
        SsaBinaryOperator op,
        SsaValueFacts left,
        SsaValueFacts right,
        out bool value)
    {
        value = false;
        if (op is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual)
            || left.KnownBitsKind != SsaFactLatticeKind.Known
            || right.KnownBitsKind != SsaFactLatticeKind.Known
            || left.KnownBits is not { } leftBits
            || right.KnownBits is not { } rightBits
            || !TryGetIntegerBitDomain(left.Type, out var leftBitWidth, out var leftMask, out _)
            || !TryGetIntegerBitDomain(right.Type, out var rightBitWidth, out var rightMask, out _)
            || leftBitWidth != rightBitWidth
            || left.Type.IsUnsigned != right.Type.IsUnsigned)
        {
            return false;
        }

        var mask = leftMask & rightMask;
        var leftKnownZero = leftBits.KnownZeroBits & mask;
        var leftKnownOne = leftBits.KnownOneBits & mask;
        var rightKnownZero = rightBits.KnownZeroBits & mask;
        var rightKnownOne = rightBits.KnownOneBits & mask;
        var conflictingBits = (leftKnownOne & rightKnownZero) | (rightKnownOne & leftKnownZero);
        if (conflictingBits != BigInteger.Zero)
        {
            value = op == SsaBinaryOperator.NotEqual;
            return true;
        }

        var leftFullyKnown = (leftKnownZero | leftKnownOne) == mask;
        var rightFullyKnown = (rightKnownZero | rightKnownOne) == mask;
        if (leftFullyKnown && rightFullyKnown)
        {
            var equal = leftKnownOne == rightKnownOne;
            value = op == SsaBinaryOperator.Equal ? equal : !equal;
            return true;
        }

        return false;
    }

    private static bool TryEvaluateNullComparison(
        SsaBinaryOperator op,
        SsaNullabilityFactKind left,
        SsaNullabilityFactKind right,
        out bool value)
    {
        value = false;
        if (op is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        bool? equal = (left, right) switch
        {
            (SsaNullabilityFactKind.Null, SsaNullabilityFactKind.Null) => true,
            (SsaNullabilityFactKind.Null, SsaNullabilityFactKind.NonNull) => false,
            (SsaNullabilityFactKind.NonNull, SsaNullabilityFactKind.Null) => false,
            _ => null
        };

        if (equal is not bool equality)
        {
            return false;
        }

        value = op == SsaBinaryOperator.Equal ? equality : !equality;
        return true;
    }

    private static bool TryProveOrderedComparison(bool provenTrue, bool provenFalse, out bool value)
    {
        value = false;
        if (provenTrue)
        {
            value = true;
            return true;
        }

        if (provenFalse)
        {
            return true;
        }

        return false;
    }

    private static SsaValueFacts JoinFacts(
        string valueName,
        StarkTypeSymbol type,
        IReadOnlyList<SsaValueFacts> facts)
    {
        var joined = CreateTypeFacts(valueName, type);

        var ranges = facts
            .Where(static fact => fact.IntegerRangeKind == SsaFactLatticeKind.Known && fact.IntegerRange is not null)
            .Select(static fact => fact.IntegerRange!)
            .ToArray();
        if (ranges.Length == facts.Count)
        {
            joined = CreateIntegerRangeFacts(
                valueName,
                type,
                ClampToTypeRange(
                    new SsaIntegerRangeFact(
                        ranges.Min(static range => range.Min),
                        ranges.Max(static range => range.Max)),
                    type));
        }

        var knownBooleanFacts = facts
            .Where(static fact => fact.BooleanKind == SsaFactLatticeKind.Known && fact.BooleanConstant is not null)
            .Select(static fact => fact.BooleanConstant!.Value)
            .ToArray();
        var distinctBooleanFacts = knownBooleanFacts.Distinct().ToArray();
        if (knownBooleanFacts.Length == facts.Count && distinctBooleanFacts.Length == 1)
        {
            joined = joined with
            {
                BooleanKind = SsaFactLatticeKind.Known,
                BooleanConstant = distinctBooleanFacts[0]
            };
        }

        var lengthRanges = facts
            .Where(static fact => fact.LengthKind == SsaFactLatticeKind.Known && fact.LengthRange is not null)
            .Select(static fact => fact.LengthRange!)
            .ToArray();
        if (lengthRanges.Length == facts.Count)
        {
            joined = joined with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = new SsaIntegerRangeFact(
                    lengthRanges.Min(static range => range.Min),
                    lengthRanges.Max(static range => range.Max))
            };
        }

        var capacityRanges = facts
            .Where(static fact => fact.CapacityKind == SsaFactLatticeKind.Known && fact.CapacityRange is not null)
            .Select(static fact => fact.CapacityRange!)
            .ToArray();
        if (capacityRanges.Length == facts.Count)
        {
            joined = joined with
            {
                CapacityKind = SsaFactLatticeKind.Known,
                CapacityRange = ClampDynamicStorageCountRange(
                    new SsaIntegerRangeFact(
                        capacityRanges.Min(static range => range.Min),
                        capacityRanges.Max(static range => range.Max)))
            };
        }

        var initializedPrefixRanges = facts
            .Where(static fact => fact.InitializedPrefixKind == SsaFactLatticeKind.Known && fact.InitializedPrefixRange is not null)
            .Select(static fact => fact.InitializedPrefixRange!)
            .ToArray();
        if (initializedPrefixRanges.Length == facts.Count)
        {
            joined = joined with
            {
                InitializedPrefixKind = SsaFactLatticeKind.Known,
                InitializedPrefixRange = ClampDynamicStorageCountRange(
                    new SsaIntegerRangeFact(
                        initializedPrefixRanges.Min(static range => range.Min),
                        initializedPrefixRanges.Max(static range => range.Max)))
            };
        }

        var textPayloads = facts
            .Where(static fact => fact.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                                  && fact.TextLiteralPayload is not null)
            .Select(static fact => fact.TextLiteralPayload!)
            .Distinct()
            .ToArray();
        if (textPayloads.Length == 1
            && facts.All(static fact => fact.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                                        && fact.TextLiteralPayload is not null))
        {
            joined = CreateTextLiteralPayloadFacts(valueName, type, textPayloads[0]) with
            {
                IntegerRangeKind = joined.IntegerRangeKind,
                IntegerRange = joined.IntegerRange,
                KnownBitsKind = joined.KnownBitsKind,
                KnownBits = joined.KnownBits,
                BooleanKind = joined.BooleanKind,
                BooleanConstant = joined.BooleanConstant,
                Nullability = joined.Nullability,
                PointerAlignmentKind = joined.PointerAlignmentKind,
                PointerAlignmentBytes = joined.PointerAlignmentBytes,
                CapacityKind = joined.CapacityKind,
                CapacityRange = joined.CapacityRange,
                InitializedPrefixKind = joined.InitializedPrefixKind,
                InitializedPrefixRange = joined.InitializedPrefixRange,
                BoundedRawPointerRegionKind = joined.BoundedRawPointerRegionKind,
                BoundedRawPointerRegion = joined.BoundedRawPointerRegion
            };
        }

        var knownNullabilityFacts = facts
            .Select(static fact => fact.Nullability)
            .Where(static nullability => nullability is SsaNullabilityFactKind.Null or SsaNullabilityFactKind.NonNull)
            .ToArray();
        var distinctNullabilityFacts = knownNullabilityFacts.Distinct().ToArray();
        if (knownNullabilityFacts.Length == facts.Count && distinctNullabilityFacts.Length == 1)
        {
            joined = joined with
            {
                Nullability = distinctNullabilityFacts[0]
            };
        }

        var pointerAlignments = facts
            .Where(static fact => fact.PointerAlignmentKind == SsaFactLatticeKind.Known
                                  && fact.PointerAlignmentBytes is > 1)
            .Select(static fact => fact.PointerAlignmentBytes!.Value)
            .ToArray();
        if (pointerAlignments.Length == facts.Count)
        {
            var alignmentBytes = pointerAlignments.Aggregate(GreatestCommonDivisor);
            if (TryNormalizePointerAlignment(type, alignmentBytes, out var normalizedAlignmentBytes))
            {
                joined = WithPointerAlignment(joined, normalizedAlignmentBytes);
            }
        }

        var boundedRegions = facts
            .Where(static fact => fact.BoundedRawPointerRegionKind == SsaFactLatticeKind.Known
                                  && fact.BoundedRawPointerRegion is not null)
            .Select(static fact => fact.BoundedRawPointerRegion!)
            .Distinct()
            .ToArray();
        if (boundedRegions.Length == 1
            && facts.All(static fact => fact.BoundedRawPointerRegionKind == SsaFactLatticeKind.Known
                                        && fact.BoundedRawPointerRegion is not null))
        {
            joined = WithBoundedRawPointerRegion(joined, boundedRegions[0]);
        }

        return type.Kind == StarkTypeKind.Dynamic
            ? JoinDynamicStorageRegionFacts(valueName, type, joined, facts)
            : joined;
    }

    private static SsaValueFacts RenameFacts(string valueName, SsaValueFacts facts, StarkTypeSymbol type)
    {
        return NormalizeDynamicStorageFacts(facts with
        {
            ValueName = valueName,
            Type = type
        });
    }

    private static SsaValueFacts CreateParameterFacts(
        string valueName,
        TypedParameterSymbol parameter,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        var facts = CreateTypeFacts(valueName, parameter.Type);
        if (parameter.Type.Kind == StarkTypeKind.Dynamic)
        {
            facts = WithDynamicStorageOwnerRoot(facts, parameter.Name);
        }

        if (!TryCreateBoundedRawPointerParameterRegionFact(parameter, parameters, out var boundedRegion))
        {
            return facts;
        }

        facts = WithBoundedRawPointerRegion(facts, boundedRegion);
        if (boundedRegion.ElementCountRange is { } elementCountRange
            && elementCountRange.Min > BigInteger.Zero)
        {
            facts = facts with
            {
                Nullability = SsaNullabilityFactKind.NonNull
            };

            if (boundedRegion.ElementAlignmentBytes is > 1)
            {
                facts = WithPointerAlignment(facts, boundedRegion.ElementAlignmentBytes.Value);
            }
        }

        return facts;
    }

    private static SsaValueFacts CreateTypeFacts(string valueName, StarkTypeSymbol type)
    {
        var facts = new SsaValueFacts(valueName, type);

        if (type.Kind == StarkTypeKind.Integer && TryGetIntegerTypeRange(type, out var range))
        {
            facts = CreateIntegerRangeFacts(valueName, type, range);
        }

        if (type.Kind == StarkTypeKind.FixedArray && type.FixedLength is int fixedLength)
        {
            facts = facts with
            {
                LengthKind = SsaFactLatticeKind.Known,
                LengthRange = new SsaIntegerRangeFact(fixedLength, fixedLength)
            };
        }

        return facts;
    }

    private static SsaIntegerRangeFact ClampDynamicStorageCountRange(SsaIntegerRangeFact range)
    {
        var lower = Max(BigInteger.Zero, range.Min);
        var upper = Min(GetDynamicStorageCountUpperBound(), Max(lower, range.Max));
        return new SsaIntegerRangeFact(lower, upper);
    }

    private static BigInteger GetDynamicStorageCountUpperBound()
    {
        return (BigInteger.One << 63) - BigInteger.One;
    }

    private static bool TryCreateBoundedRawPointerParameterRegionFact(
        TypedParameterSymbol parameter,
        IReadOnlyList<TypedParameterSymbol> parameters,
        out SsaBoundedRawPointerRegionFact boundedRegion)
    {
        boundedRegion = default!;
        if (parameter.Type.Kind != StarkTypeKind.RawPointer
            || parameter.Type.ElementType is not { } elementType
            || string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            || !TryResolveBoundedRawPointerElementCount(
                parameter.RawPointerElementCountExpression,
                parameters,
                out var elementCount,
                out var elementCountRange))
        {
            return false;
        }

        _ = TryGetTypeAlignmentBytes(elementType, out var alignmentBytes);
        boundedRegion = new SsaBoundedRawPointerRegionFact(
            elementCount,
            elementCountRange,
            alignmentBytes > 1 ? alignmentBytes : null);
        return true;
    }

    private static bool TryResolveBoundedRawPointerElementCount(
        string countExpression,
        IReadOnlyList<TypedParameterSymbol> parameters,
        out SsaValue elementCount,
        out SsaIntegerRangeFact? elementCountRange)
    {
        var normalizedExpression = countExpression.Trim();
        if (BigInteger.TryParse(normalizedExpression, NumberStyles.None, CultureInfo.InvariantCulture, out var constantCount))
        {
            var constantType = StarkTypeSymbols.Integer(64);
            elementCount = new SsaIntegerConstant(constantCount, constantType);
            elementCountRange = new SsaIntegerRangeFact(constantCount, constantCount);
            return true;
        }

        var countParameter = parameters.FirstOrDefault(
            candidate => string.Equals(candidate.Name, normalizedExpression, StringComparison.Ordinal));
        if (countParameter is null)
        {
            elementCount = default!;
            elementCountRange = null;
            return false;
        }

        elementCount = new SsaValueReference($"arg_{countParameter.Name}", countParameter.Type);
        var countFacts = CreateTypeFacts($"arg_{countParameter.Name}", countParameter.Type);
        elementCountRange = countFacts.IntegerRangeKind == SsaFactLatticeKind.Known
            ? countFacts.IntegerRange
            : null;
        return true;
    }

    private static SsaValueFacts CreateIntegerConstantFacts(
        string valueName,
        StarkTypeSymbol type,
        BigInteger value)
    {
        var facts = CreateIntegerRangeFacts(valueName, type, new SsaIntegerRangeFact(value, value));

        return TryCreateKnownBitsForConstant(type, value, out var knownBits)
            ? ApplyKnownBits(facts, knownBits)
            : facts;
    }

    private static SsaValueFacts CreateIntegerRangeFacts(
        string valueName,
        StarkTypeSymbol type,
        SsaIntegerRangeFact range)
    {
        var facts = new SsaValueFacts(
            valueName,
            type,
            IntegerRangeKind: SsaFactLatticeKind.Known,
            IntegerRange: range);

        if (TryCreateKnownBitsForRange(type, range, out var knownBits))
        {
            facts = ApplyKnownBits(facts, knownBits);
        }

        return facts;
    }

    private static bool TryCreateKnownBitsForConstant(
        StarkTypeSymbol type,
        BigInteger value,
        out SsaKnownBitsFact knownBits)
    {
        knownBits = default!;
        if (!TryGetIntegerBitDomain(type, out _, out var mask, out var modulus))
        {
            return false;
        }

        var normalized = NormalizeIntegerBits(value, modulus);
        knownBits = new SsaKnownBitsFact(mask ^ normalized, normalized);
        return true;
    }

    private static bool TryCreateKnownBitsForRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact range,
        out SsaKnownBitsFact knownBits)
    {
        knownBits = default!;
        if (range.Min == range.Max)
        {
            return TryCreateKnownBitsForConstant(type, range.Min, out knownBits);
        }

        if (range.Min < BigInteger.Zero
            || !TryGetIntegerBitDomain(type, out _, out var mask, out _))
        {
            return false;
        }

        var possibleOneBits = CreateNonNegativeBitMask(range.Max) & mask;
        knownBits = new SsaKnownBitsFact(mask ^ possibleOneBits, BigInteger.Zero);
        return true;
    }

    private static bool TryTranslateIntegerConvertKnownBits(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType,
        SsaValueFacts operand,
        out SsaKnownBitsFact knownBits)
    {
        knownBits = default!;
        if (operand.KnownBitsKind != SsaFactLatticeKind.Known
            || operand.KnownBits is not { } operandKnownBits
            || !TryGetIntegerBitDomain(sourceType, out var sourceBitWidth, out _, out _)
            || !TryGetIntegerBitDomain(targetType, out var targetBitWidth, out var targetMask, out _)
            || targetBitWidth > sourceBitWidth)
        {
            return false;
        }

        knownBits = new SsaKnownBitsFact(
            operandKnownBits.KnownZeroBits & targetMask,
            operandKnownBits.KnownOneBits & targetMask);
        return true;
    }

    private static SsaValueFacts ApplyKnownBits(SsaValueFacts facts, SsaKnownBitsFact knownBits)
    {
        if (!TryGetIntegerBitDomain(facts.Type, out _, out var mask, out _))
        {
            return facts;
        }

        var normalizedKnownZero = knownBits.KnownZeroBits & mask;
        var normalizedKnownOne = knownBits.KnownOneBits & mask;
        var updated = facts with
        {
            KnownBitsKind = SsaFactLatticeKind.Known,
            KnownBits = new SsaKnownBitsFact(normalizedKnownZero, normalizedKnownOne)
        };

        if ((normalizedKnownZero | normalizedKnownOne) == mask)
        {
            var value = DenormalizeIntegerBits(normalizedKnownOne, facts.Type);
            if (facts.IntegerRangeKind == SsaFactLatticeKind.Known
                && facts.IntegerRange is { } existingRange)
            {
                var normalizedValue = normalizedKnownOne & mask;
                var matchingValues = new[]
                    {
                        value,
                        normalizedValue
                    }
                    .Distinct()
                    .Where(candidate => existingRange.Min <= candidate && candidate <= existingRange.Max)
                    .ToArray();

                if (matchingValues.Length == 1)
                {
                    updated = updated with
                    {
                        IntegerRange = new SsaIntegerRangeFact(matchingValues[0], matchingValues[0])
                    };
                }
            }
            else
            {
                updated = updated with
                {
                    IntegerRangeKind = SsaFactLatticeKind.Known,
                    IntegerRange = new SsaIntegerRangeFact(value, value)
                };
            }
        }
        else if (TryCreateNonNegativeRangeFromKnownBits(
                     facts.Type,
                     new SsaKnownBitsFact(normalizedKnownZero, normalizedKnownOne),
                     out var knownBitsRange))
        {
            updated = IntersectIntegerRange(updated, knownBitsRange);
        }

        return updated;
    }

    private static bool TryCreateNonNegativeRangeFromKnownBits(
        StarkTypeSymbol type,
        SsaKnownBitsFact knownBits,
        out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!TryGetIntegerBitDomain(type, out var bitWidth, out var mask, out _))
        {
            return false;
        }

        if (!type.IsUnsigned)
        {
            var signBit = BigInteger.One << (bitWidth - 1);
            if ((knownBits.KnownZeroBits & signBit) == BigInteger.Zero)
            {
                return false;
            }
        }

        var min = knownBits.KnownOneBits & mask;
        var max = mask ^ (knownBits.KnownZeroBits & mask);
        if (min > max)
        {
            return false;
        }

        range = new SsaIntegerRangeFact(min, max);
        return true;
    }

    private static SsaValueFacts IntersectIntegerRange(SsaValueFacts facts, SsaIntegerRangeFact range)
    {
        if (facts.IntegerRangeKind != SsaFactLatticeKind.Known
            || facts.IntegerRange is not { } existingRange)
        {
            return facts with
            {
                IntegerRangeKind = SsaFactLatticeKind.Known,
                IntegerRange = range
            };
        }

        var min = Max(existingRange.Min, range.Min);
        var max = Min(existingRange.Max, range.Max);
        return min <= max
            ? facts with
            {
                IntegerRange = new SsaIntegerRangeFact(min, max)
            }
            : facts;
    }

    private static bool TryGetIntegerBitDomain(
        StarkTypeSymbol type,
        out int bitWidth,
        out BigInteger mask,
        out BigInteger modulus)
    {
        bitWidth = 0;
        mask = BigInteger.Zero;
        modulus = BigInteger.Zero;
        if (type.Kind != StarkTypeKind.Integer
            || type.BitWidth is not int width
            || width <= 0)
        {
            return false;
        }

        bitWidth = width;
        modulus = BigInteger.One << bitWidth;
        mask = modulus - BigInteger.One;
        return true;
    }

    private static BigInteger NormalizeIntegerBits(BigInteger value, BigInteger modulus)
    {
        return ((value % modulus) + modulus) % modulus;
    }

    private static BigInteger DenormalizeIntegerBits(BigInteger normalized, StarkTypeSymbol type)
    {
        if (!TryGetIntegerBitDomain(type, out var bitWidth, out _, out var modulus)
            || type.IsUnsigned)
        {
            return normalized;
        }

        var signBit = BigInteger.One << (bitWidth - 1);
        return (normalized & signBit) != BigInteger.Zero
            ? normalized - modulus
            : normalized;
    }

    private static SsaValueFacts CreateBooleanConstantFacts(string valueName, bool value)
    {
        return new SsaValueFacts(
            valueName,
            StarkTypeSymbols.Bool,
            BooleanKind: SsaFactLatticeKind.Known,
            BooleanConstant: value);
    }

    private static SsaValueFacts CreateTextConstantFacts(
        string valueName,
        StarkTypeSymbol type,
        string literalText)
    {
        var facts = CreateTypeFacts(valueName, type);
        if (type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || !TextLiteralDecoder.TryDecode(
                literalText,
                literalText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                out var decoded,
                out _))
        {
            return facts;
        }

        var payload = CreateTextLiteralPayload(decoded);

        return CreateTextLiteralPayloadFacts(valueName, type, payload);
    }

    private static SsaTextLiteralPayloadFact CreateTextLiteralPayload(DecodedTextLiteral decoded)
    {
        var utf8Bytes = decoded.Utf8Bytes;
        var utf32CodeUnits = decoded.Utf32CodeUnits;

        return new SsaTextLiteralPayloadFact(
            decoded.Value,
            Convert.ToHexString(utf8Bytes),
            string.Join(
                ",",
                utf32CodeUnits.Select(static unit => unit.ToString("X8", CultureInfo.InvariantCulture))),
            decoded.IsAscii,
            utf8Bytes.Length,
            utf32CodeUnits.Length);
    }

    private static SsaValueFacts CreateTextLiteralPayloadFacts(
        string valueName,
        StarkTypeSymbol type,
        SsaTextLiteralPayloadFact payload)
    {
        var facts = CreateTypeFacts(valueName, type);
        var length = type.Kind == StarkTypeKind.Unicode
            ? payload.Utf32Length
            : payload.Utf8Length;

        return facts with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = new SsaIntegerRangeFact(length, length),
            TextLiteralPayloadKind = SsaFactLatticeKind.Known,
            TextLiteralPayload = payload
        };
    }

    private static SsaValueFacts CreateNullFacts(string valueName, StarkTypeSymbol type)
    {
        return CreateTypeFacts(valueName, type) with
        {
            Nullability = SsaNullabilityFactKind.Null
        };
    }

    private static SsaValueFacts CreateNonNullFacts(string valueName, StarkTypeSymbol type)
    {
        return CreateTypeFacts(valueName, type) with
        {
            Nullability = SsaNullabilityFactKind.NonNull
        };
    }

    private static SsaValueFacts CreateAddressFacts(
        string valueName,
        StarkTypeSymbol pointerType,
        StarkTypeSymbol pointeeType)
    {
        var facts = CreateNonNullFacts(valueName, pointerType);
        return TryGetTypeAlignmentBytes(pointeeType, out var alignmentBytes)
            ? WithPointerAlignment(facts, alignmentBytes)
            : facts;
    }

    private static SsaValueFacts WithPointerAlignment(SsaValueFacts facts, int alignmentBytes)
    {
        return facts.Type.Kind == StarkTypeKind.RawPointer && alignmentBytes > 1
            ? facts with
            {
                PointerAlignmentKind = SsaFactLatticeKind.Known,
                PointerAlignmentBytes = alignmentBytes
            }
            : facts;
    }

    private static SsaValueFacts WithBoundedRawPointerRegion(
        SsaValueFacts facts,
        SsaBoundedRawPointerRegionFact boundedRegion)
    {
        return facts.Type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice
            ? facts with
            {
                BoundedRawPointerRegionKind = SsaFactLatticeKind.Known,
                BoundedRawPointerRegion = boundedRegion
            }
            : facts;
    }

    private static bool TryCanPreserveBoundedRawPointerRegionThroughConvert(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType,
        SsaValueFacts operand,
        out SsaBoundedRawPointerRegionFact boundedRegion)
    {
        boundedRegion = default!;
        if (operand.BoundedRawPointerRegionKind != SsaFactLatticeKind.Known
            || operand.BoundedRawPointerRegion is not { } operandRegion
            || sourceType.Kind != StarkTypeKind.RawPointer
            || targetType.Kind != StarkTypeKind.RawPointer
            || sourceType.ElementType is not { } sourceElementType
            || targetType.ElementType is not { } targetElementType
            || NormalizeComparableElementType(sourceElementType) != NormalizeComparableElementType(targetElementType))
        {
            return false;
        }

        boundedRegion = operandRegion;
        return true;
    }

    private static StarkTypeSymbol NormalizeComparableElementType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private static bool TryNormalizePointerAlignment(
        StarkTypeSymbol pointerType,
        int? baseAlignmentBytes,
        out int alignmentBytes)
    {
        alignmentBytes = 1;
        if (pointerType.Kind != StarkTypeKind.RawPointer
            || pointerType.ElementType is not { } pointeeType
            || baseAlignmentBytes is not > 1
            || !TryGetTypeAlignmentBytes(pointeeType, out var pointeeAlignmentBytes))
        {
            return false;
        }

        alignmentBytes = Math.Min(baseAlignmentBytes.Value, pointeeAlignmentBytes);
        return alignmentBytes > 1;
    }

    private static bool TryGetTypeAlignmentBytes(StarkTypeSymbol type, out int alignmentBytes)
    {
        // SSA facts are target-independent, so keep only minimum scalar alignments that are safe
        // across the supported 32-bit and 64-bit targets. The LLVM emitter can recover stronger
        // target-aware alignment from direct address definitions.
        var concreteType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        switch (concreteType.Kind)
        {
            case StarkTypeKind.Integer or StarkTypeKind.Float when concreteType.BitWidth is int bitWidth:
                alignmentBytes = GetPortableScalarAlignmentBytes(bitWidth);
                return alignmentBytes > 1;
            case StarkTypeKind.FixedArray when concreteType.ElementType is not null:
                return TryGetTypeAlignmentBytes(concreteType.ElementType, out alignmentBytes);
            default:
                alignmentBytes = 1;
                return false;
        }
    }

    private static int GetPortableScalarAlignmentBytes(int bitWidth)
    {
        if (bitWidth <= 8)
        {
            return 1;
        }

        if (bitWidth <= 16)
        {
            return 2;
        }

        return 4;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            var next = left % right;
            left = right;
            right = next;
        }

        return left;
    }

    private static bool IsTextLengthField(SsaExtractFieldRValue extractField)
    {
        return extractField.FieldIndex == 1
               || string.Equals(extractField.FieldName, "Length", StringComparison.Ordinal)
               || string.Equals(extractField.FieldName, "length", StringComparison.Ordinal);
    }

    private static bool IsDynamicLengthField(SsaExtractFieldRValue extractField)
    {
        return IsDynamicLengthField(extractField.FieldName, extractField.FieldIndex);
    }

    private static bool IsDynamicCapacityField(SsaExtractFieldRValue extractField)
    {
        return IsDynamicCapacityField(extractField.FieldName, extractField.FieldIndex);
    }

    private static bool IsDynamicDataField(SsaExtractFieldRValue extractField)
    {
        return extractField.FieldIndex == 0
               || string.Equals(extractField.FieldName, "Data", StringComparison.Ordinal)
               || string.Equals(extractField.FieldName, "data", StringComparison.Ordinal);
    }

    private static bool IsDynamicLengthField(string fieldName, int fieldIndex)
    {
        return fieldIndex == 1
               || string.Equals(fieldName, "Length", StringComparison.Ordinal)
               || string.Equals(fieldName, "length", StringComparison.Ordinal);
    }

    private static bool IsDynamicCapacityField(string fieldName, int fieldIndex)
    {
        return fieldIndex == 2
               || string.Equals(fieldName, "Capacity", StringComparison.Ordinal)
               || string.Equals(fieldName, "capacity", StringComparison.Ordinal);
    }

    internal static bool TryGetSystemTextLengthFunction(
        string functionName,
        string moduleName,
        out StarkTypeKind textKind)
    {
        switch (functionName)
        {
            case "System.Text.AsciiLength":
                textKind = StarkTypeKind.Ascii;
                return true;
            case "System.Text.UnicodeLength":
                textKind = StarkTypeKind.Unicode;
                return true;
            case "AsciiLength" when IsSystemTextLengthHostModule(moduleName):
                textKind = StarkTypeKind.Ascii;
                return true;
            case "UnicodeLength" when IsSystemTextLengthHostModule(moduleName):
                textKind = StarkTypeKind.Unicode;
                return true;
            default:
                textKind = default;
                return false;
        }
    }

    private static bool IsSystemTextLengthHostModule(string moduleName)
    {
        return string.Equals(moduleName, "System.Text", StringComparison.Ordinal)
            || string.Equals(moduleName, "System.Runtime.Platform.Linux", StringComparison.Ordinal)
            || string.Equals(moduleName, "System.Runtime.Platform.MacOS", StringComparison.Ordinal)
            || string.Equals(moduleName, "System.Runtime.Platform.Windows", StringComparison.Ordinal);
    }

    private static bool TryRefineNullability(
        string valueName,
        StarkTypeSymbol type,
        SsaNullabilityFactKind nullability,
        IReadOnlyDictionary<string, SsaValueFacts> values,
        out SsaValueFacts valueFacts)
    {
        valueFacts = default!;
        if (type.Kind != StarkTypeKind.RawPointer
            || nullability is not (SsaNullabilityFactKind.Null or SsaNullabilityFactKind.NonNull))
        {
            return false;
        }

        var currentFacts = values.TryGetValue(valueName, out var knownFacts)
            ? knownFacts
            : CreateTypeFacts(valueName, type);
        if (currentFacts.Nullability is SsaNullabilityFactKind.Overdefined)
        {
            return false;
        }

        if (currentFacts.Nullability is SsaNullabilityFactKind.Null or SsaNullabilityFactKind.NonNull
            && currentFacts.Nullability != nullability)
        {
            return false;
        }

        valueFacts = currentFacts with
        {
            Nullability = nullability
        };
        return true;
    }

    private static SsaIntegerRangeFact MultiplyRanges(
        SsaIntegerRangeFact left,
        SsaIntegerRangeFact right)
    {
        var candidates = new[]
        {
            left.Min * right.Min,
            left.Min * right.Max,
            left.Max * right.Min,
            left.Max * right.Max
        };

        return new SsaIntegerRangeFact(candidates.Min(), candidates.Max());
    }

    private static BigInteger SaturateInteger(BigInteger value, SsaIntegerRangeFact bounds)
    {
        if (value < bounds.Min)
        {
            return bounds.Min;
        }

        return value > bounds.Max ? bounds.Max : value;
    }

    private static BigInteger CreateNonNegativeBitMask(BigInteger maxValue)
    {
        var mask = BigInteger.Zero;
        var value = maxValue;
        while (value > BigInteger.Zero)
        {
            mask = (mask << 1) | BigInteger.One;
            value >>= 1;
        }

        return mask;
    }

    private static SsaIntegerRangeFact ClampToTypeRange(SsaIntegerRangeFact range, StarkTypeSymbol type)
    {
        return TryGetIntegerTypeRange(type, out var typeRange)
            ? ClampRange(range, typeRange)
            : range;
    }

    private static SsaIntegerRangeFact ClampRange(
        SsaIntegerRangeFact range,
        SsaIntegerRangeFact bounds)
    {
        var min = Max(range.Min, bounds.Min);
        var max = Min(range.Max, bounds.Max);
        return min <= max
            ? new SsaIntegerRangeFact(min, max)
            : bounds;
    }

    private static bool TryGetIntegerTypeRange(StarkTypeSymbol type, out SsaIntegerRangeFact range)
    {
        range = default!;
        if (!StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out var min, out var max))
        {
            return false;
        }

        range = new SsaIntegerRangeFact(min, max);
        return true;
    }

    private static BigInteger Min(BigInteger left, BigInteger right) => left <= right ? left : right;

    private static BigInteger Max(BigInteger left, BigInteger right) => left >= right ? left : right;
}
