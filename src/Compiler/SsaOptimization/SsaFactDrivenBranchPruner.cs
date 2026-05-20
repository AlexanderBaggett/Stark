using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaFactDrivenBranchPruner
{
    public SsaIrModule Optimize(
        SsaIrModule module,
        SsaValueFactModel facts)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = facts.Functions.TryGetValue(function.Name, out var functionFacts)
                    ? OptimizeFunction(function, functionFacts)
                    : function;
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private static SsaFunction OptimizeFunction(
        SsaFunction function,
        SsaFunctionFactModel facts)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var changed = false;
        var current = function;
        var usedNames = CollectDefinedValueNames(function);
        var definitions = BuildValueDefinitions(current);
        var threaded = ThreadJumpEdges(current, facts, definitions);
        if (!ReferenceEquals(threaded, current))
        {
            changed = true;
            current = threaded;
            definitions = BuildValueDefinitions(current);
        }

        var blocks = current.Blocks
            .Select(block =>
            {
                var optimized = PruneBlock(block, CreateScopedFacts(facts, block.Id), definitions, usedNames);
                if (ReferenceEquals(optimized, block))
                {
                    return block;
                }

                changed = true;
                return optimized;
            })
            .ToArray();

        return changed
            ? RemoveStalePhiIncomings(current with { Blocks = blocks })
            : current;
    }

    private static IReadOnlyDictionary<string, SsaRValue> BuildValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static SsaFunction ThreadJumpEdges(
        SsaFunction function,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                if (block.Terminator.Kind != SsaTerminatorKind.Branch
                    || block.Terminator.Targets.Count != 2
                    || block.Terminator.Targets[0] == block.Terminator.Targets[1]
                    || block.Terminator.BranchWeights is { Count: > 0 })
                {
                    return block;
                }

                var targets = block.Terminator.Targets.ToArray();
                for (var index = 0; index < targets.Length; index++)
                {
                    if (TryThreadJumpEdge(block, targets[index], byId, facts, definitions, out var threadedTarget))
                    {
                        targets[index] = threadedTarget;
                    }
                }

                if (targets.SequenceEqual(block.Terminator.Targets))
                {
                    return block;
                }

                changed = true;
                return block with
                {
                    Terminator = block.Terminator with { Targets = targets }
                };
            })
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private static bool TryThreadJumpEdge(
        SsaBasicBlock predecessor,
        int targetBlockId,
        IReadOnlyDictionary<int, SsaBasicBlock> byId,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out int threadedTarget)
    {
        threadedTarget = targetBlockId;
        if (!byId.TryGetValue(targetBlockId, out var targetBlock)
            || !CanThreadThroughBlock(targetBlock)
            || targetBlock.Terminator.Kind != SsaTerminatorKind.Branch
            || targetBlock.Terminator.Condition is null
            || targetBlock.Terminator.Targets.Count != 2
            || targetBlock.Terminator.Targets[0] == targetBlock.Terminator.Targets[1]
            || targetBlock.Terminator.BranchWeights is { Count: > 0 })
        {
            return false;
        }

        var edgeFacts = SsaValueFactAnalyzer.InferEdgeFacts(
            predecessor.Terminator,
            targetBlockId,
            definitions,
            facts.Values);
        if (edgeFacts.Count == 0)
        {
            return false;
        }

        var scopedFacts = CreateEdgeScopedFacts(facts, edgeFacts);
        if (!TryGetBooleanFact(targetBlock.Terminator.Condition, scopedFacts, definitions, out var condition))
        {
            return false;
        }

        var candidate = targetBlock.Terminator.Targets[condition ? 0 : 1];
        if (candidate == targetBlockId
            || candidate == predecessor.Id
            || !byId.TryGetValue(candidate, out var candidateBlock)
            || candidateBlock.Phis.Count != 0)
        {
            return false;
        }

        threadedTarget = candidate;
        return true;
    }

    private static bool CanThreadThroughBlock(SsaBasicBlock block)
    {
        return block.Phis.Count == 0
            && block.Instructions.All(static instruction =>
                instruction is SsaValueInstruction valueInstruction
                && IsJumpThreadingSafeRValue(valueInstruction.Value));
    }

    private static bool IsJumpThreadingSafeRValue(SsaRValue value)
    {
        return value switch
        {
            SsaUseRValue => true,
            SsaUnaryRValue { Type.Kind: StarkTypeKind.Bool } => true,
            SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool } => true,
            SsaConvertRValue { Type.Kind: StarkTypeKind.Bool } => true,
            _ => false
        };
    }

    private static SsaFunctionFactModel CreateEdgeScopedFacts(
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaValueFacts> edgeFacts)
    {
        var values = new Dictionary<string, SsaValueFacts>(facts.Values, StringComparer.Ordinal);
        foreach (var (name, valueFacts) in edgeFacts)
        {
            values[name] = valueFacts;
        }

        return facts with { Values = values };
    }

    private static SsaFunctionFactModel CreateScopedFacts(
        SsaFunctionFactModel facts,
        int blockId)
    {
        if (facts.BlockEntryValueFacts is not { } blockEntries
            || !blockEntries.TryGetValue(blockId, out var entryFacts)
            || entryFacts.Count == 0)
        {
            return facts;
        }

        var values = new Dictionary<string, SsaValueFacts>(facts.Values, StringComparer.Ordinal);
        foreach (var (name, valueFacts) in entryFacts)
        {
            values[name] = valueFacts;
        }

        return facts with { Values = values };
    }

    private static SsaBasicBlock PruneBlock(
        SsaBasicBlock block,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> usedNames)
    {
        var terminator = PruneTerminator(
            block.Id,
            block.Terminator,
            facts,
            definitions,
            usedNames,
            out var appendedInstructions);

        if (appendedInstructions.Count == 0
            && EqualityComparer<SsaTerminator>.Default.Equals(terminator, block.Terminator))
        {
            return block;
        }

        return block with
        {
            Instructions = appendedInstructions.Count == 0
                ? block.Instructions
                : block.Instructions.Concat(appendedInstructions).ToArray(),
            Terminator = terminator
        };
    }

    private static SsaTerminator PruneTerminator(
        int blockId,
        SsaTerminator terminator,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> usedNames,
        out IReadOnlyList<SsaInstruction> appendedInstructions)
    {
        appendedInstructions = [];

        if (terminator.Kind == SsaTerminatorKind.Branch
            && terminator.Targets.Count == 2
            && terminator.Condition is not null
            && TryGetBooleanFact(terminator.Condition, facts, definitions, out var condition))
        {
            return PreserveLoopMetadata(
                terminator,
                new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    [terminator.Targets[condition ? 0 : 1]],
                    Location: terminator.Location));
        }

        if (terminator.Kind == SsaTerminatorKind.Switch
            && terminator.Condition is not null
            && terminator.DefaultTarget is int defaultTarget
            && terminator.SwitchCases is { Count: > 0 } switchCases)
        {
            if (TryGetSwitchSingleton(terminator.Condition, facts, out var singleton)
                && TryResolveSwitchTarget(singleton, switchCases, defaultTarget, out var targetBlockId))
            {
                return PreserveLoopMetadata(
                    terminator,
                    new SsaTerminator(
                        SsaTerminatorKind.Goto,
                        [targetBlockId],
                        Location: terminator.Location));
            }

            if (TryGetIntegerRangeFact(terminator.Condition, facts, out var range))
            {
                var filteredCases = switchCases
                    .Where(switchCase => !IsIntegerCaseOutsideRange(switchCase, range))
                    .ToArray();

                if (filteredCases.Length == 0)
                {
                    return PreserveLoopMetadata(
                        terminator,
                        new SsaTerminator(
                            SsaTerminatorKind.Goto,
                            [defaultTarget],
                            Location: terminator.Location));
                }

                if (filteredCases.Length != switchCases.Count)
                {
                    if (filteredCases.Length == 1)
                    {
                        return BuildSingleCaseSwitchBranch(
                            blockId,
                            terminator,
                            filteredCases[0],
                            defaultTarget,
                            usedNames,
                            out appendedInstructions);
                    }

                    return PreserveLoopMetadata(
                        terminator,
                        new SsaTerminator(
                            SsaTerminatorKind.Switch,
                            filteredCases
                                .Select(static switchCase => switchCase.TargetBlockId)
                                .Distinct()
                                .ToArray(),
                            Condition: terminator.Condition,
                            SwitchCases: filteredCases,
                            DefaultTarget: defaultTarget,
                            Location: terminator.Location));
                }
            }
        }

        return terminator;
    }

    private static SsaTerminator BuildSingleCaseSwitchBranch(
        int blockId,
        SsaTerminator terminator,
        SsaSwitchCase switchCase,
        int defaultTarget,
        ISet<string> usedNames,
        out IReadOnlyList<SsaInstruction> appendedInstructions)
    {
        var conditionName = CreateUniqueValueName(usedNames, $"switch_case_live_{blockId}");
        appendedInstructions =
        [
            new SsaValueInstruction(
                conditionName,
                new SsaBinaryRValue(
                    SsaBinaryOperator.Equal,
                    terminator.Condition!,
                    switchCase.MatchValue,
                    StarkTypeSymbols.Bool,
                    "=="),
                terminator.Location)
        ];

        return PreserveLoopMetadata(
            terminator,
            new SsaTerminator(
                SsaTerminatorKind.Branch,
                [switchCase.TargetBlockId, defaultTarget],
                Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
                Location: terminator.Location));
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

    private static bool TryGetBooleanFact(
        SsaValue value,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out bool boolean)
    {
        if (value is SsaBoolConstant constant)
        {
            boolean = constant.Value;
            return true;
        }

        if (value is SsaValueReference reference
            && facts.Values.TryGetValue(reference.Name, out var valueFacts)
            && valueFacts.BooleanKind == SsaFactLatticeKind.Known
            && valueFacts.BooleanConstant is bool known)
        {
            boolean = known;
            return true;
        }

        if (value is SsaValueReference comparisonReference
            && definitions.TryGetValue(comparisonReference.Name, out var definition)
            && definition is SsaBinaryRValue { Type.Kind: StarkTypeKind.Bool } comparison
            && TryGetValueFacts(comparison.Left, facts, out var leftFacts)
            && TryGetValueFacts(comparison.Right, facts, out var rightFacts)
            && SsaValueFactAnalyzer.TryEvaluateComparison(
                comparison.Operator,
                leftFacts,
                rightFacts,
                out var comparisonValue))
        {
            boolean = comparisonValue;
            return true;
        }

        boolean = false;
        return false;
    }

    private static bool TryGetValueFacts(
        SsaValue value,
        SsaFunctionFactModel facts,
        out SsaValueFacts valueFacts)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                valueFacts = new SsaValueFacts(
                    integer.Text,
                    integer.Type,
                    IntegerRangeKind: SsaFactLatticeKind.Known,
                    IntegerRange: new SsaIntegerRangeFact(integer.Value, integer.Value));
                return true;
            case SsaBoolConstant boolean:
                valueFacts = new SsaValueFacts(
                    boolean.Text,
                    StarkTypeSymbols.Bool,
                    BooleanKind: SsaFactLatticeKind.Known,
                    BooleanConstant: boolean.Value);
                return true;
            case SsaNullConstant nullValue:
                valueFacts = new SsaValueFacts(
                    nullValue.Text,
                    nullValue.Type,
                    Nullability: SsaNullabilityFactKind.Null);
                return true;
            case SsaGlobalAddressValue globalAddress:
                valueFacts = new SsaValueFacts(
                    globalAddress.Text,
                    globalAddress.Type,
                    Nullability: SsaNullabilityFactKind.NonNull);
                return true;
            case SsaFunctionAddressValue functionAddress:
                valueFacts = new SsaValueFacts(
                    functionAddress.Text,
                    functionAddress.Type,
                    Nullability: SsaNullabilityFactKind.NonNull);
                return true;
            case SsaClosureValue closure:
                valueFacts = new SsaValueFacts(
                    closure.Text,
                    closure.Type);
                return true;
            case SsaValueReference reference when facts.Values.TryGetValue(reference.Name, out var knownFacts):
                valueFacts = knownFacts;
                return true;
            default:
                valueFacts = default!;
                return false;
        }
    }

    private static bool TryGetSwitchSingleton(
        SsaValue value,
        SsaFunctionFactModel facts,
        out SsaValue singleton)
    {
        switch (value)
        {
            case SsaBoolConstant:
            case SsaIntegerConstant:
                singleton = value;
                return true;
            case SsaValueReference reference
                when facts.Values.TryGetValue(reference.Name, out var valueFacts)
                     && valueFacts.BooleanKind == SsaFactLatticeKind.Known
                     && valueFacts.BooleanConstant is bool boolean:
                singleton = new SsaBoolConstant(boolean);
                return true;
            case SsaValueReference reference
                when facts.Values.TryGetValue(reference.Name, out var valueFacts)
                     && valueFacts.IntegerRangeKind == SsaFactLatticeKind.Known
                     && valueFacts.IntegerRange is { } range
                     && range.Min == range.Max:
                singleton = new SsaIntegerConstant(range.Min, value.Type);
                return true;
            default:
                singleton = default!;
                return false;
        }
    }

    private static bool TryGetIntegerRangeFact(
        SsaValue value,
        SsaFunctionFactModel facts,
        out SsaIntegerRangeFact range)
    {
        if (value is SsaIntegerConstant integer)
        {
            range = new SsaIntegerRangeFact(integer.Value, integer.Value);
            return true;
        }

        if (value is SsaValueReference reference
            && facts.Values.TryGetValue(reference.Name, out var valueFacts)
            && valueFacts.IntegerRangeKind == SsaFactLatticeKind.Known
            && valueFacts.IntegerRange is { } knownRange)
        {
            range = knownRange;
            return true;
        }

        range = default!;
        return false;
    }

    private static bool TryResolveSwitchTarget(
        SsaValue singleton,
        IReadOnlyList<SsaSwitchCase> switchCases,
        int defaultTarget,
        out int targetBlockId)
    {
        foreach (var switchCase in switchCases)
        {
            if (SwitchValuesEqual(singleton, switchCase.MatchValue))
            {
                targetBlockId = switchCase.TargetBlockId;
                return true;
            }
        }

        targetBlockId = defaultTarget;
        return true;
    }

    private static bool SwitchValuesEqual(SsaValue left, SsaValue right)
    {
        return (left, right) switch
        {
            (SsaBoolConstant leftBool, SsaBoolConstant rightBool) => leftBool.Value == rightBool.Value,
            (SsaIntegerConstant leftInteger, SsaIntegerConstant rightInteger) => leftInteger.Value == rightInteger.Value,
            _ => false
        };
    }

    private static bool IsIntegerCaseOutsideRange(
        SsaSwitchCase switchCase,
        SsaIntegerRangeFact range)
    {
        return switchCase.MatchValue is SsaIntegerConstant match
            && (match.Value < range.Min || match.Value > range.Max);
    }

    private static SsaFunction RemoveStalePhiIncomings(SsaFunction function)
    {
        var liveEdges = CollectLiveEdges(function.Blocks);
        var blocks = function.Blocks
            .Select(block => block with
            {
                Phis = block.Phis
                    .Select(phi => phi with
                    {
                        Incomings = CoalescePhiIncomings(
                            phi.Incomings
                                .Where(incoming => liveEdges.Contains((incoming.PredecessorBlockId, block.Id)))
                                .ToArray())
                    })
                    .ToArray()
            })
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static HashSet<(int PredecessorBlockId, int SuccessorBlockId)> CollectLiveEdges(
        IReadOnlyList<SsaBasicBlock> blocks)
    {
        var liveEdges = new HashSet<(int, int)>();
        foreach (var block in blocks)
        {
            foreach (var successor in GetSuccessors(block.Terminator))
            {
                liveEdges.Add((block.Id, successor));
            }
        }

        return liveEdges;
    }

    private static HashSet<string> CollectDefinedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            names.Add($"arg_{parameter.Name}");
        }

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                names.Add(phi.ResultName);
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                names.Add(valueInstruction.ResultName);
            }
        }

        return names;
    }

    private static string CreateUniqueValueName(ISet<string> usedNames, string baseName)
    {
        var candidate = baseName;
        var suffix = 0;
        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = $"{baseName}_{suffix}";
        }

        return candidate;
    }

    private static IEnumerable<int> GetSuccessors(SsaTerminator terminator)
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

    private static IReadOnlyList<SsaPhiIncoming> CoalescePhiIncomings(IReadOnlyList<SsaPhiIncoming> incomings)
    {
        var result = new List<SsaPhiIncoming>(incomings.Count);
        var byPredecessor = new Dictionary<int, SsaValue>();

        foreach (var incoming in incomings)
        {
            if (!byPredecessor.TryGetValue(incoming.PredecessorBlockId, out var existing))
            {
                byPredecessor[incoming.PredecessorBlockId] = incoming.Value;
                result.Add(incoming);
                continue;
            }

            if (EqualityComparer<SsaValue>.Default.Equals(existing, incoming.Value))
            {
                continue;
            }

            result.Add(incoming);
        }

        return result;
    }
}

