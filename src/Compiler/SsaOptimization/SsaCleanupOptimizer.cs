using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaCleanupOptimizer
{
    private readonly bool _enableSelectPredication;

    public SsaCleanupOptimizer(bool enableSelectPredication = true)
    {
        _enableSelectPredication = enableSelectPredication;
    }

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

        var current = RemoveStalePhiIncomings(function);
        current = CanonicalizeCompareAndBranchShapes(current);
        current = SimplifyTrivialTerminators(current);
        current = RemoveStalePhiIncomings(current);
        current = SimplifySingleCaseSwitches(current);
        current = NormalizeSwitchLoweringStructures(current);
        current = ReuseIdenticalMaterializedValues(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveStoresToWriteOnlyLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = RemoveUnusedLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = CollapseTrampolineBlocks(current);
        current = RemoveStalePhiIncomings(current);
        current = MergeLinearBlocks(current);
        current = RemoveStalePhiIncomings(current);
        current = CanonicalizeEarlyReturnDiamonds(current);
        if (_enableSelectPredication)
        {
            current = PredicatizeSimpleReturnDiamonds(current);
        }

        current = PruneUnreachableBlocks(current);
        current = RemoveStalePhiIncomings(current);
        current = SimplifyTrivialTerminators(current);
        current = RemoveStalePhiIncomings(current);
        current = NormalizeSwitchLoweringStructures(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveStoresToWriteOnlyLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = RemoveUnusedLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = CollapseTrampolineBlocks(current);
        current = RemoveStalePhiIncomings(current);
        current = MergeLinearBlocks(current);
        current = RemoveStalePhiIncomings(current);
        current = CanonicalizeEarlyReturnDiamonds(current);
        if (_enableSelectPredication)
        {
            current = PredicatizeSimpleReturnDiamonds(current);
            current = RewriteTrivialCopiesAndIdentityPhis(current);
            current = RemoveUnusedPureInstructions(current);
        }

        current = RemoveUnusedArenaFrameInstructions(current);
        return RemoveStalePhiIncomings(PruneUnreachableBlocks(current));
    }

    private static SsaFunction CanonicalizeCompareAndBranchShapes(SsaFunction function)
    {
        var blocks = function.Blocks
            .Select(CanonicalizeCompareAndBranchShape)
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static SsaBasicBlock CanonicalizeCompareAndBranchShape(SsaBasicBlock block)
    {
        if (block.Terminator.Kind != SsaTerminatorKind.Branch
            || block.Terminator.Condition is not SsaValueReference conditionReference)
        {
            return block;
        }

        var definingInstruction = block.Instructions
            .OfType<SsaValueInstruction>()
            .FirstOrDefault(instruction => string.Equals(instruction.ResultName, conditionReference.Name, StringComparison.Ordinal));

        if (definingInstruction is null)
        {
            return block;
        }

        if (!TryCanonicalizeBranchCondition(definingInstruction.Value, block.Terminator, out var canonicalTerminator))
        {
            return block;
        }

        return block with { Terminator = canonicalTerminator };
    }

    private static bool TryCanonicalizeBranchCondition(
        SsaRValue value,
        SsaTerminator terminator,
        out SsaTerminator canonicalTerminator)
    {
        canonicalTerminator = terminator;

        if (terminator.Targets.Count != 2)
        {
            return false;
        }

        if (value is SsaUnaryRValue { Operator: SsaUnaryOperator.LogicalNot } logicalNot
            && logicalNot.Operand.Type.Kind == StarkTypeKind.Bool)
        {
            canonicalTerminator = PreserveLoopMetadata(
                terminator,
                new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    [terminator.Targets[1], terminator.Targets[0]],
                    Condition: logicalNot.Operand,
                    Location: terminator.Location,
                    BranchWeights: ReverseBranchWeights(terminator)));
            return true;
        }

        if (value is not SsaBinaryRValue binary
            || binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        if (TryMatchBooleanComparison(binary.Left, binary.Right, out var branchCondition, out var swapTargets)
            || TryMatchBooleanComparison(binary.Right, binary.Left, out branchCondition, out swapTargets))
        {
            if (binary.Operator == SsaBinaryOperator.NotEqual)
            {
                swapTargets = !swapTargets;
            }

            var targets = swapTargets
                ? new[] { terminator.Targets[1], terminator.Targets[0] }
                : terminator.Targets.ToArray();

            canonicalTerminator = PreserveLoopMetadata(
                terminator,
                new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    targets,
                    Condition: branchCondition,
                    Location: terminator.Location,
                    BranchWeights: swapTargets ? ReverseBranchWeights(terminator) : terminator.BranchWeights));
            return true;
        }

        return false;
    }

    private static IReadOnlyList<int>? ReverseBranchWeights(SsaTerminator terminator)
    {
        return terminator.BranchWeights is { Count: 2 } weights
            ? [weights[1], weights[0]]
            : null;
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

    private static bool TryMatchBooleanComparison(
        SsaValue constantCandidate,
        SsaValue other,
        out SsaValue branchCondition,
        out bool swapTargets)
    {
        branchCondition = other;
        swapTargets = false;

        if (constantCandidate is not SsaBoolConstant constant
            || other.Type.Kind != StarkTypeKind.Bool)
        {
            return false;
        }

        swapTargets = !constant.Value;
        return true;
    }

    private static SsaFunction ReuseIdenticalMaterializedValues(SsaFunction function)
    {
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            var available = new Dictionary<string, SsaValueReference>(StringComparer.Ordinal);
            var memoryVersion = 0;

            foreach (var instruction in block.Instructions.Select(instruction => RewriteInstruction(instruction, replacements)))
            {
                switch (instruction)
                {
                    case SsaValueInstruction valueInstruction:
                        if (TryGetReusableInstructionKey(valueInstruction.Value, memoryVersion, out var key))
                        {
                            if (available.TryGetValue(key, out var existing))
                            {
                                replacements[valueInstruction.ResultName] = existing;
                                break;
                            }

                            available[key] = new SsaValueReference(valueInstruction.ResultName, valueInstruction.Value.Type);
                        }

                        if (IsMemoryBarrier(valueInstruction.Value))
                        {
                            memoryVersion++;
                        }

                        break;

                    case SsaStoreLocalInstruction:
                    case SsaCallInstruction:
                    case SsaIndirectCallInstruction:
                    case SsaCopyMemoryInstruction:
                    case SsaStoreIndirectInstruction:
                    case SsaStoreGlobalInstruction:
                    case SsaLifetimeStartInstruction:
                    case SsaLifetimeEndInstruction:
                    case SsaDeallocateLocalInstruction:
                        memoryVersion++;
                        break;
                }
            }
        }

        return replacements.Count == 0
            ? function
            : ApplyReplacements(function, replacements);
    }

    private static SsaFunction SimplifyTrivialTerminators(SsaFunction function)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var simplified = SimplifyTrivialTerminator(block.Id, block.Terminator, byId);
                if (!EqualityComparer<SsaTerminator>.Default.Equals(simplified, block.Terminator))
                {
                    changed = true;
                    return block with { Terminator = simplified };
                }

                return block;
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static SsaFunction SimplifySingleCaseSwitches(SsaFunction function)
    {
        var usedNames = CollectDefinedValueNames(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                if (block.Terminator.Kind != SsaTerminatorKind.Switch
                    || block.Terminator.Condition is null
                    || block.Terminator.DefaultTarget is null
                    || block.Terminator.SwitchCases is not { Count: 1 } switchCases)
                {
                    return block;
                }

                var switchCase = switchCases[0];
                if (TryBuildSingleCaseSwitchBranch(block.Terminator, switchCase, out var branchTerminator))
                {
                    changed = true;
                    return block with { Terminator = branchTerminator };
                }

                var conditionName = CreateUniqueValueName(usedNames, $"switch_case_match_{block.Id}");
                var compareInstruction = new SsaValueInstruction(
                    conditionName,
                    new SsaBinaryRValue(
                        SsaBinaryOperator.Equal,
                        block.Terminator.Condition,
                        switchCase.MatchValue,
                        StarkTypeSymbols.Bool,
                        "=="));

                changed = true;
                return new SsaBasicBlock(
                    block.Id,
                    block.Label,
                    block.Phis,
                    block.Instructions.Concat([compareInstruction]).ToArray(),
                    PreserveLoopMetadata(
                        block.Terminator,
                        new SsaTerminator(
                            SsaTerminatorKind.Branch,
                            [switchCase.TargetBlockId, block.Terminator.DefaultTarget.Value],
                            Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
                            Location: block.Terminator.Location,
                            BranchWeights: TryCreateSwitchCaseBranchWeights(block.Terminator, switchCase, trueTargetIsCase: true))));
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static SsaFunction NormalizeSwitchLoweringStructures(SsaFunction function)
    {
        if (function.Blocks.Count == 0)
        {
            return function;
        }

        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var usedNames = CollectDefinedValueNames(function);
        var nextBlockId = function.Blocks.Max(static block => block.Id) + 1;
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            if (TryNormalizeSwitchBlock(block, byId, usedNames, ref nextBlockId, out var replacementBlocks))
            {
                changed = true;
                blocks.AddRange(replacementBlocks);
                continue;
            }

            blocks.Add(block);
        }

        return changed ? function with { Blocks = blocks.ToArray() } : function;
    }

    private static bool TryNormalizeSwitchBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, SsaBasicBlock> byId,
        ISet<string> usedNames,
        ref int nextBlockId,
        out IReadOnlyList<SsaBasicBlock> replacementBlocks)
    {
        replacementBlocks = [];

        if (block.Terminator.Kind != SsaTerminatorKind.Switch
            || block.Terminator.Condition is null
            || block.Terminator.DefaultTarget is null
            || block.Terminator.SwitchCases is not { Count: > 1 } switchCases)
        {
            return false;
        }

        if (TryBuildExhaustiveBoolSwitchBranch(block.Terminator, byId, out var branchTerminator))
        {
            replacementBlocks = [block with { Terminator = branchTerminator }];
            return true;
        }

        if (!TryGetOrderedIntegerSwitchCases(block.Terminator, out var orderedCases)
            || !ShouldLowerSwitchToCompareChain(orderedCases)
            || !CanRewriteSwitchWithDistinctPhiFreeTargets(orderedCases.Select(static switchCase => switchCase.TargetBlockId)
                .Append(block.Terminator.DefaultTarget.Value), byId))
        {
            return false;
        }

        replacementBlocks = BuildSwitchCompareChain(block, orderedCases, usedNames, ref nextBlockId);
        return true;
    }

    private static bool TryBuildExhaustiveBoolSwitchBranch(
        SsaTerminator terminator,
        IReadOnlyDictionary<int, SsaBasicBlock> byId,
        out SsaTerminator branchTerminator)
    {
        branchTerminator = new SsaTerminator(SsaTerminatorKind.Unreachable, []);

        if (terminator.Condition is not { Type.Kind: StarkTypeKind.Bool }
            || terminator.DefaultTarget is null
            || terminator.SwitchCases is not { Count: 2 } switchCases)
        {
            return false;
        }

        SsaSwitchCase? trueCase = null;
        SsaSwitchCase? falseCase = null;

        foreach (var switchCase in switchCases)
        {
            if (switchCase.MatchValue is not SsaBoolConstant match)
            {
                return false;
            }

            if (match.Value)
            {
                trueCase = switchCase;
            }
            else
            {
                falseCase = switchCase;
            }
        }

        if (trueCase is null
            || falseCase is null
            || !CanRewriteSwitchWithDistinctPhiFreeTargets(
                [trueCase.TargetBlockId, falseCase.TargetBlockId, terminator.DefaultTarget.Value],
                byId))
        {
            return false;
        }

        branchTerminator = PreserveLoopMetadata(
            terminator,
            new SsaTerminator(
                SsaTerminatorKind.Branch,
                [trueCase.TargetBlockId, falseCase.TargetBlockId],
                Condition: terminator.Condition,
                Location: terminator.Location,
                BranchWeights: TryCreateBoolSwitchBranchWeights(terminator, trueCase, falseCase)));
        return true;
    }

    private static bool TryGetOrderedIntegerSwitchCases(
        SsaTerminator terminator,
        out IReadOnlyList<SsaSwitchCase> orderedCases)
    {
        orderedCases = [];

        if (terminator.Condition is not { Type.Kind: StarkTypeKind.Integer }
            || terminator.SwitchCases is null)
        {
            return false;
        }

        var cases = new List<(SsaSwitchCase SwitchCase, BigInteger Value)>(terminator.SwitchCases.Count);
        foreach (var switchCase in terminator.SwitchCases)
        {
            if (switchCase.MatchValue is not SsaIntegerConstant match)
            {
                return false;
            }

            cases.Add((switchCase, match.Value));
        }

        orderedCases = cases
            .OrderBy(static item => item.Value)
            .Select(static item => item.SwitchCase)
            .ToArray();
        return orderedCases.Count != 0;
    }

    private static bool ShouldLowerSwitchToCompareChain(IReadOnlyList<SsaSwitchCase> orderedCases)
    {
        if (orderedCases.Count is < 2 or > 4)
        {
            return false;
        }

        if (orderedCases.Count <= 3)
        {
            return true;
        }

        var values = orderedCases
            .Select(static switchCase => ((SsaIntegerConstant)switchCase.MatchValue).Value)
            .ToArray();
        var span = values[^1] - values[0] + BigInteger.One;
        return span > orderedCases.Count * 2;
    }

    private static bool CanRewriteSwitchWithDistinctPhiFreeTargets(
        IEnumerable<int> targetBlockIds,
        IReadOnlyDictionary<int, SsaBasicBlock> byId)
    {
        var seen = new HashSet<int>();
        foreach (var targetBlockId in targetBlockIds)
        {
            if (!seen.Add(targetBlockId))
            {
                return false;
            }

            if (byId.TryGetValue(targetBlockId, out var targetBlock) && targetBlock.Phis.Count != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<SsaBasicBlock> BuildSwitchCompareChain(
        SsaBasicBlock block,
        IReadOnlyList<SsaSwitchCase> orderedCases,
        ISet<string> usedNames,
        ref int nextBlockId)
    {
        var replacementBlocks = new List<SsaBasicBlock>(orderedCases.Count);
        var currentBlockId = block.Id;
        var currentLabel = block.Label;
        var currentPhis = block.Phis;
        var currentInstructions = block.Instructions;
        var condition = block.Terminator.Condition!;
        var defaultTarget = block.Terminator.DefaultTarget!.Value;
        var remainingWeight = TryGetSwitchCompareChainWeightTotal(block.Terminator, orderedCases);

        for (var index = 0; index < orderedCases.Count; index++)
        {
            var switchCase = orderedCases[index];
            var conditionName = CreateUniqueValueName(usedNames, $"switch_match_{block.Id}_{index}");
            var compareInstruction = new SsaValueInstruction(
                conditionName,
                new SsaBinaryRValue(
                    SsaBinaryOperator.Equal,
                    condition,
                    switchCase.MatchValue,
                    StarkTypeSymbols.Bool,
                    "=="));

            var falseTarget = index == orderedCases.Count - 1
                ? defaultTarget
                : nextBlockId++;
            var branchWeights = TryCreateSwitchCompareChainBranchWeights(block.Terminator, switchCase, ref remainingWeight);

            replacementBlocks.Add(new SsaBasicBlock(
                currentBlockId,
                currentLabel,
                currentPhis,
                currentInstructions.Concat([compareInstruction]).ToArray(),
                PreserveLoopMetadata(
                    block.Terminator,
                    new SsaTerminator(
                        SsaTerminatorKind.Branch,
                        [switchCase.TargetBlockId, falseTarget],
                        Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
                        Location: block.Terminator.Location,
                        BranchWeights: branchWeights))));

            currentBlockId = falseTarget;
            currentLabel = $"{block.Label}_switch_cmp_{index + 1}";
            currentPhis = [];
            currentInstructions = [];
        }

        return replacementBlocks;
    }

    private static int? TryGetSwitchCompareChainWeightTotal(
        SsaTerminator terminator,
        IReadOnlyList<SsaSwitchCase> orderedCases)
    {
        if (terminator.SwitchCases is not { Count: > 0 } switchCases
            || terminator.BranchWeights is not { } weights
            || weights.Count != switchCases.Count + 1)
        {
            return null;
        }

        var total = Math.Max(1, weights[0]);
        foreach (var switchCase in orderedCases)
        {
            if (!TryGetSwitchCaseWeight(terminator, switchCase, out var caseWeight, out _))
            {
                return null;
            }

            total = Math.Min(int.MaxValue, total + Math.Max(1, caseWeight));
        }

        return total;
    }

    private static IReadOnlyList<int>? TryCreateSwitchCompareChainBranchWeights(
        SsaTerminator terminator,
        SsaSwitchCase switchCase,
        ref int? remainingWeight)
    {
        if (remainingWeight is not { } total
            || !TryGetSwitchCaseWeight(terminator, switchCase, out var caseWeight, out _))
        {
            remainingWeight = null;
            return null;
        }

        caseWeight = Math.Max(1, caseWeight);
        var falseWeight = Math.Max(1, total - caseWeight);
        remainingWeight = falseWeight;
        return [caseWeight, falseWeight];
    }

    private static SsaTerminator SimplifyTrivialTerminator(
        int blockId,
        SsaTerminator terminator,
        IReadOnlyDictionary<int, SsaBasicBlock> byId)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Branch when terminator.Targets.Count == 2
                                             && terminator.Targets[0] == terminator.Targets[1]
                                             && CanCollapseMultiEdgeTerminator(blockId, terminator.Targets[0], byId):
                return PreserveLoopMetadata(
                    terminator,
                    new SsaTerminator(SsaTerminatorKind.Goto, [terminator.Targets[0]]));
            case SsaTerminatorKind.Switch:
            {
                terminator = RemoveSwitchCasesThatMatchDefaultTarget(terminator);

                if ((terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                    && terminator.DefaultTarget is { } defaultTarget)
                {
                    return PreserveLoopMetadata(
                        terminator,
                        new SsaTerminator(SsaTerminatorKind.Goto, [defaultTarget]));
                }

                var allTargets = new List<int>(terminator.Targets);
                if (terminator.DefaultTarget is { } fallthroughTarget)
                {
                    allTargets.Add(fallthroughTarget);
                }

                if (allTargets.Count != 0
                    && allTargets.All(target => target == allTargets[0])
                    && CanCollapseMultiEdgeTerminator(blockId, allTargets[0], byId))
                {
                    return PreserveLoopMetadata(
                        terminator,
                        new SsaTerminator(SsaTerminatorKind.Goto, [allTargets[0]]));
                }

                break;
            }
        }

        return terminator;
    }

    private static bool CanCollapseMultiEdgeTerminator(
        int predecessorBlockId,
        int targetBlockId,
        IReadOnlyDictionary<int, SsaBasicBlock> byId)
    {
        if (!byId.TryGetValue(targetBlockId, out var targetBlock))
        {
            return true;
        }

        return targetBlock.Phis.All(phi => phi.Incomings.Count(incoming => incoming.PredecessorBlockId == predecessorBlockId) <= 1);
    }

    private static SsaTerminator RemoveSwitchCasesThatMatchDefaultTarget(SsaTerminator terminator)
    {
        if (terminator.Kind != SsaTerminatorKind.Switch
            || terminator.DefaultTarget is not { } defaultTarget
            || terminator.SwitchCases is not { Count: > 0 } switchCases)
        {
            return terminator;
        }

        var filteredCases = switchCases
            .Where(switchCase => switchCase.TargetBlockId != defaultTarget)
            .ToArray();

        if (filteredCases.Length == switchCases.Count)
        {
            return terminator;
        }

        var branchWeights = TryRemoveDefaultTargetSwitchCaseWeights(terminator, filteredCases);

        return terminator with
        {
            Targets = filteredCases
                .Select(static switchCase => switchCase.TargetBlockId)
                .Distinct()
                .ToArray(),
            SwitchCases = filteredCases,
            BranchWeights = branchWeights
        };
    }

    private static IReadOnlyList<int>? TryRemoveDefaultTargetSwitchCaseWeights(
        SsaTerminator terminator,
        IReadOnlyList<SsaSwitchCase> filteredCases)
    {
        if (terminator.SwitchCases is not { Count: > 0 } switchCases
            || terminator.BranchWeights is not { } weights
            || weights.Count != switchCases.Count + 1)
        {
            return null;
        }

        var keptCases = filteredCases.ToHashSet();
        var adjustedDefaultWeight = Math.Max(1, weights[0]);
        var filteredWeights = new List<int>(filteredCases.Count + 1);
        filteredWeights.Add(0);

        for (var index = 0; index < switchCases.Count; index++)
        {
            var caseWeight = Math.Max(1, weights[index + 1]);
            if (keptCases.Contains(switchCases[index]))
            {
                filteredWeights.Add(caseWeight);
                continue;
            }

            adjustedDefaultWeight = Math.Min(int.MaxValue, adjustedDefaultWeight + caseWeight);
        }

        filteredWeights[0] = adjustedDefaultWeight;
        return filteredWeights;
    }

    private static bool TryBuildSingleCaseSwitchBranch(
        SsaTerminator terminator,
        SsaSwitchCase switchCase,
        out SsaTerminator branchTerminator)
    {
        if (terminator.Condition is { Type.Kind: StarkTypeKind.Bool }
            && switchCase.MatchValue is SsaBoolConstant match)
        {
            var targets = match.Value
                ? new[] { switchCase.TargetBlockId, terminator.DefaultTarget!.Value }
                : new[] { terminator.DefaultTarget!.Value, switchCase.TargetBlockId };

            branchTerminator = PreserveLoopMetadata(
                terminator,
                new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    targets,
                    Condition: terminator.Condition,
                    Location: terminator.Location,
                    BranchWeights: TryCreateSwitchCaseBranchWeights(terminator, switchCase, trueTargetIsCase: match.Value)));
            return true;
        }

        branchTerminator = new SsaTerminator(SsaTerminatorKind.Unreachable, []);
        return false;
    }

    private static IReadOnlyList<int>? TryCreateBoolSwitchBranchWeights(
        SsaTerminator terminator,
        SsaSwitchCase trueCase,
        SsaSwitchCase falseCase)
    {
        return TryGetSwitchCaseWeight(terminator, trueCase, out var trueWeight, out _)
               && TryGetSwitchCaseWeight(terminator, falseCase, out var falseWeight, out _)
            ? [trueWeight, falseWeight]
            : null;
    }

    private static IReadOnlyList<int>? TryCreateSwitchCaseBranchWeights(
        SsaTerminator terminator,
        SsaSwitchCase switchCase,
        bool trueTargetIsCase)
    {
        if (!TryGetSwitchCaseWeight(terminator, switchCase, out var caseWeight, out var defaultWeight))
        {
            return null;
        }

        return trueTargetIsCase
            ? [caseWeight, defaultWeight]
            : [defaultWeight, caseWeight];
    }

    private static bool TryGetSwitchCaseWeight(
        SsaTerminator terminator,
        SsaSwitchCase switchCase,
        out int caseWeight,
        out int defaultWeight)
    {
        caseWeight = 0;
        defaultWeight = 0;

        if (terminator.SwitchCases is not { Count: > 0 } switchCases
            || terminator.BranchWeights is not { } weights
            || weights.Count != switchCases.Count + 1)
        {
            return false;
        }

        for (var index = 0; index < switchCases.Count; index++)
        {
            if (!Equals(switchCases[index], switchCase))
            {
                continue;
            }

            defaultWeight = weights[0];
            caseWeight = weights[index + 1];
            return true;
        }

        return false;
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

            foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                names.Add(instruction.ResultName);
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

    private static bool TryGetReusableInstructionKey(
        SsaRValue value,
        int memoryVersion,
        out string key)
    {
        switch (value)
        {
            case SsaUseRValue use:
                key = $"use|{ValueKey(use.Value)}";
                return true;
            case SsaUnaryRValue unary:
                key = $"unary|{unary.Operator}|{ValueKey(unary.Operand)}|{TypeKey(unary.Type)}";
                return true;
            case SsaBinaryRValue binary:
                var left = ValueKey(binary.Left);
                var right = ValueKey(binary.Right);
                if (IsCommutative(binary.Operator) && string.CompareOrdinal(right, left) < 0)
                {
                    (left, right) = (right, left);
                }

                key = $"binary|{binary.Operator}|{left}|{right}|{TypeKey(binary.Type)}";
                return true;
            case SsaSelectRValue select:
                key = $"select|{ValueKey(select.Condition)}|{ValueKey(select.WhenTrue)}|{ValueKey(select.WhenFalse)}|{TypeKey(select.Type)}";
                return true;
            case SsaConvertRValue convert:
                key = $"convert|{ValueKey(convert.Operand)}|{TypeKey(convert.TargetType)}";
                return true;
            case SsaExtractFieldRValue extractField:
                key = $"extract-field|{ValueKey(extractField.Target)}|{extractField.FieldName}|{extractField.FieldIndex}|{TypeKey(extractField.Type)}";
                return true;
            case SsaInsertFieldRValue insertField:
                key = $"insert-field|{ValueKey(insertField.Target)}|{insertField.FieldName}|{insertField.FieldIndex}|{ValueKey(insertField.Value)}|{TypeKey(insertField.Type)}";
                return true;
            case SsaExtractIndexRValue extractIndex:
                key = $"extract-index|{ValueKey(extractIndex.Target)}|{extractIndex.ElementIndex}|{TypeKey(extractIndex.Type)}";
                return true;
            case SsaInsertIndexRValue insertIndex:
                key = $"insert-index|{ValueKey(insertIndex.Target)}|{insertIndex.ElementIndex}|{ValueKey(insertIndex.Value)}|{TypeKey(insertIndex.Type)}";
                return true;
            case SsaMakeSliceFromLocalRValue makeSlice:
                key = $"make-slice|{makeSlice.LocalName}|{TypeKey(makeSlice.SourceType)}|{TypeKey(makeSlice.Type)}";
                return true;
            case SsaLoadSliceElementRValue loadSlice:
                key = $"load-slice|m{memoryVersion}|{ValueKey(loadSlice.Slice)}|{ValueKey(loadSlice.Index)}|{TypeKey(loadSlice.Type)}";
                return true;
            case SsaTextSliceRValue textSlice:
                key = $"text-slice|{ValueKey(textSlice.TextValue)}|{ValueKey(textSlice.Start)}|{ValueKey(textSlice.Length)}|{TypeKey(textSlice.Type)}";
                return true;
            case SsaAddressOfLocalRValue addressOfLocal:
                key = $"address-of-local|{addressOfLocal.LocalName}|{TypeKey(addressOfLocal.PointeeType)}|{TypeKey(addressOfLocal.Type)}";
                return true;
            case SsaAddressOfParameterRValue addressOfParameter:
                key = $"address-of-parameter|{addressOfParameter.ParameterName}|{TypeKey(addressOfParameter.PointeeType)}|{TypeKey(addressOfParameter.Type)}";
                return true;
            case SsaFieldAddressRValue fieldAddress:
                key = $"field-address|{ValueKey(fieldAddress.Address)}|{TypeKey(fieldAddress.AggregateType)}|{fieldAddress.FieldName}|{fieldAddress.FieldIndex}|{TypeKey(fieldAddress.Type)}";
                return true;
            case SsaElementAddressRValue elementAddress:
                key = $"element-address|{ValueKey(elementAddress.Address)}|{TypeKey(elementAddress.AggregateType)}|{ValueKey(elementAddress.Index)}|{elementAddress.ConstantIndex}|{TypeKey(elementAddress.Type)}";
                return true;
            case SsaSliceElementAddressRValue sliceElementAddress:
                key = $"slice-element-address|{ValueKey(sliceElementAddress.Slice)}|{ValueKey(sliceElementAddress.Index)}|{TypeKey(sliceElementAddress.Type)}";
                return true;
            case SsaLoadIndirectRValue loadIndirect:
                key = $"load-indirect|m{memoryVersion}|{ValueKey(loadIndirect.Address)}|{TypeKey(loadIndirect.Type)}";
                return true;
            case SsaLoadGlobalRValue loadGlobal:
                key = $"load-global|m{memoryVersion}|{loadGlobal.GlobalName}|{TypeKey(loadGlobal.Type)}";
                return true;
            case SsaLoadLocalRValue loadLocal:
                key = $"load-local|m{memoryVersion}|{loadLocal.LocalName}|{TypeKey(loadLocal.Type)}";
                return true;
            default:
                key = string.Empty;
                return false;
        }
    }

    private static bool IsMemoryBarrier(SsaRValue value)
    {
        return value is SsaCallRValue
            or SsaDynamicStorageAllocationRValue
            or SsaDynamicStorageFreeRValue
            or SsaHeapStorageFreeRValue
            or SsaDynamicStorageReserveRValue
            or SsaDynamicStorageTryReserveRValue
            or SsaDynamicStorageTryReserveCapacityRValue
            or SsaDynamicStorageMoveLastRValue
            or SsaDynamicStorageMoveAtRValue;
    }

    private static SsaFunction ApplyReplacements(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var blocks = function.Blocks
            .Select(block => new SsaBasicBlock(
                block.Id,
                block.Label,
                block.Phis
                    .Where(phi => !replacements.ContainsKey(phi.ResultName))
                    .Select(phi => new SsaPhi(
                        phi.ResultName,
                        phi.VariableName,
                        phi.Type,
                        CoalescePhiIncomings(
                            phi.Incomings
                                .Select(incoming => new SsaPhiIncoming(
                                    incoming.PredecessorBlockId,
                                    RewriteValue(incoming.Value, replacements)))
                                .ToArray())))
                    .ToArray(),
                block.Instructions
                    .Select(instruction => RewriteInstruction(instruction, replacements))
                    .Where(instruction => instruction is not SsaValueInstruction valueInstruction
                                          || !replacements.ContainsKey(valueInstruction.ResultName))
                    .ToArray(),
                RewriteTerminator(block.Terminator, replacements)))
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static SsaFunction RewriteTrivialCopiesAndIdentityPhis(SsaFunction function)
    {
        var replacements = ComputeTrivialReplacements(function.Blocks);
        if (replacements.Count == 0)
        {
            return function;
        }

        return ApplyReplacements(function, replacements);
    }

    private static Dictionary<string, SsaValue> ComputeTrivialReplacements(IReadOnlyList<SsaBasicBlock> blocks)
    {
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var definitions = BuildValueDefinitions(blocks);
        var phiDefinitions = BuildPhiDefinitions(blocks);
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var block in blocks)
            {
                var equivalentPhis = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var phi in block.Phis)
                {
                    if (replacements.ContainsKey(phi.ResultName))
                    {
                        continue;
                    }

                    var rewrittenIncomings = CoalescePhiIncomings(
                        phi.Incomings
                            .Select(incoming => new SsaPhiIncoming(
                                incoming.PredecessorBlockId,
                                RewriteValue(incoming.Value, replacements)))
                            .ToArray());

                    if (TryFindIdentityValue(
                            phi.ResultName,
                            rewrittenIncomings.Select(static incoming => incoming.Value).ToArray(),
                            out var identityValue))
                    {
                        replacements[phi.ResultName] = identityValue!;
                        changed = true;
                        continue;
                    }

                    var phiKey = BuildEquivalentPhiKey(phi.Type, rewrittenIncomings);
                    if (equivalentPhis.TryGetValue(phiKey, out var existingPhi))
                    {
                        replacements[phi.ResultName] = new SsaValueReference(existingPhi, phi.Type);
                        changed = true;
                        continue;
                    }

                    equivalentPhis[phiKey] = phi.ResultName;
                }

                foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
                {
                    if (replacements.ContainsKey(instruction.ResultName))
                    {
                        continue;
                    }

                    if (instruction.Value is SsaUseRValue use)
                    {
                        replacements[instruction.ResultName] = RewriteValue(use.Value, replacements);
                        changed = true;
                        continue;
                    }

                    if (TryFindTrivialInstructionReplacement(
                            instruction.Value,
                            replacements,
                            definitions,
                            phiDefinitions,
                            out var trivialReplacement))
                    {
                        replacements[instruction.ResultName] = trivialReplacement;
                        changed = true;
                    }
                }
            }
        }

        return replacements;
    }

    private static IReadOnlyDictionary<string, SsaRValue> BuildValueDefinitions(IReadOnlyList<SsaBasicBlock> blocks)
    {
        return blocks
            .SelectMany(static block => block.Instructions.OfType<SsaValueInstruction>())
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, SsaPhi> BuildPhiDefinitions(IReadOnlyList<SsaBasicBlock> blocks)
    {
        return blocks
            .SelectMany(static block => block.Phis)
            .ToDictionary(static phi => phi.ResultName, static phi => phi, StringComparer.Ordinal);
    }

    private static bool TryFindIdentityValue(
        string resultName,
        IReadOnlyList<SsaValue> values,
        out SsaValue? identityValue)
    {
        identityValue = null;

        if (values.Count == 0)
        {
            return false;
        }

        var nonSelfValues = values
            .Where(value => value is not SsaValueReference reference
                            || !string.Equals(reference.Name, resultName, StringComparison.Ordinal))
            .ToArray();

        if (nonSelfValues.Length == 0)
        {
            return false;
        }

        var first = nonSelfValues[0];
        if (nonSelfValues.Any(value => !EqualityComparer<SsaValue>.Default.Equals(value, first)))
        {
            return false;
        }

        identityValue = first;
        return true;
    }

    private static string BuildEquivalentPhiKey(
        StarkTypeSymbol type,
        IReadOnlyList<SsaPhiIncoming> incomings)
    {
        var ordered = incomings
            .OrderBy(static incoming => incoming.PredecessorBlockId)
            .ThenBy(static incoming => ValueKey(incoming.Value), StringComparer.Ordinal)
            .ToArray();

        return $"{TypeKey(type)}|{string.Join(";", ordered.Select(static incoming => $"{incoming.PredecessorBlockId}={ValueKey(incoming.Value)}"))}";
    }

    private static bool TryFindTrivialInstructionReplacement(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        out SsaValue replacement)
    {
        switch (value)
        {
            case SsaBinaryRValue binary:
                return TryFindAlgebraicIdentityReplacement(
                    binary,
                    replacements,
                    definitions,
                    out replacement);
            case SsaConvertRValue convert:
            {
                var operand = RewriteValue(convert.Operand, replacements);
                if (operand.Type == convert.TargetType)
                {
                    replacement = operand;
                    return true;
                }

                break;
            }
            case SsaSelectRValue select:
            {
                var condition = RewriteValue(select.Condition, replacements);
                if (condition is SsaBoolConstant constant)
                {
                    replacement = RewriteValue(constant.Value ? select.WhenTrue : select.WhenFalse, replacements);
                    return true;
                }

                var whenTrue = RewriteValue(select.WhenTrue, replacements);
                var whenFalse = RewriteValue(select.WhenFalse, replacements);
                if (EqualityComparer<SsaValue>.Default.Equals(whenTrue, whenFalse))
                {
                    replacement = whenTrue;
                    return true;
                }

                if (select.Type.Kind == StarkTypeKind.Bool
                    && condition.Type.Kind == StarkTypeKind.Bool
                    && whenTrue is SsaBoolConstant { Value: true }
                    && whenFalse is SsaBoolConstant { Value: false })
                {
                    replacement = condition;
                    return true;
                }

                break;
            }
            case SsaExtractFieldRValue extractField:
                if (TryResolveAggregateFieldValue(
                        extractField.Target,
                        extractField.FieldName,
                        extractField.FieldIndex,
                        extractField.Type,
                        replacements,
                        definitions,
                        phiDefinitions,
                        out replacement))
                {
                    return true;
                }

                break;
            case SsaExtractIndexRValue extractIndex:
                if (TryResolveAggregateIndexValue(
                        extractIndex.Target,
                        extractIndex.ElementIndex,
                        extractIndex.Type,
                        replacements,
                        definitions,
                        phiDefinitions,
                        out replacement))
                {
                    return true;
                }

                break;
            case SsaInsertFieldRValue insertField:
            {
                var target = RewriteValue(insertField.Target, replacements);
                var insertedValue = RewriteValue(insertField.Value, replacements);
                if (TryResolveAggregateFieldValue(
                        target,
                        insertField.FieldName,
                        insertField.FieldIndex,
                        insertField.Value.Type,
                        replacements,
                        definitions,
                        phiDefinitions,
                        out var existingField)
                    && EqualityComparer<SsaValue>.Default.Equals(existingField, insertedValue))
                {
                    replacement = target;
                    return true;
                }

                break;
            }
            case SsaInsertIndexRValue insertIndex:
            {
                var target = RewriteValue(insertIndex.Target, replacements);
                var insertedValue = RewriteValue(insertIndex.Value, replacements);
                if (TryResolveAggregateIndexValue(
                        target,
                        insertIndex.ElementIndex,
                        insertIndex.Value.Type,
                        replacements,
                        definitions,
                        phiDefinitions,
                        out var existingElement)
                    && EqualityComparer<SsaValue>.Default.Equals(existingElement, insertedValue))
                {
                    replacement = target;
                    return true;
                }

                break;
            }
        }

        replacement = new SsaUndefValue(value.Type);
        return false;
    }

    private static bool TryFindAlgebraicIdentityReplacement(
        SsaBinaryRValue binary,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue replacement)
    {
        var left = ResolveIdentityOperand(binary.Left, replacements, definitions);
        var right = ResolveIdentityOperand(binary.Right, replacements, definitions);

        replacement = new SsaUndefValue(binary.Type);
        if (TryReplaceSameIntegerComparison(binary, left, right, out replacement))
        {
            return true;
        }

        if (binary.Type.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
            case SsaBinaryOperator.WrappingAdd:
            case SsaBinaryOperator.SaturatingAdd:
                return TryReplaceCommutativeIdentity(left, right, IsZeroIntegerConstant, out replacement);
            case SsaBinaryOperator.Subtract:
            case SsaBinaryOperator.WrappingSubtract:
            case SsaBinaryOperator.SaturatingSubtract:
                return TryReplaceRightIdentity(left, right, IsZeroIntegerConstant, out replacement)
                       || TryReplaceSameOperands(left, right, CreateZeroIntegerConstant(binary.Type), out replacement);
            case SsaBinaryOperator.Multiply:
            case SsaBinaryOperator.WrappingMultiply:
            case SsaBinaryOperator.SaturatingMultiply:
                return TryReplaceCommutativeAbsorbingConstant(left, right, binary.Type, IsZeroIntegerConstant, BigInteger.Zero, out replacement)
                       || TryReplaceCommutativeIdentity(left, right, IsOneIntegerConstant, out replacement);
            case SsaBinaryOperator.Divide:
                return TryReplaceRightIdentity(left, right, IsOneIntegerConstant, out replacement)
                       || TryReplaceSameNonZeroOperands(
                           left,
                           right,
                           binary.Type,
                           BigInteger.One,
                           definitions,
                           out replacement)
                       || TryReplaceNonNegativeRangeBelowDivisorWithZero(
                           left,
                           right,
                           binary.Type,
                           definitions,
                           out replacement);
            case SsaBinaryOperator.Modulo:
                return TryReplaceRightAbsorbingConstant(left, right, binary.Type, IsOneIntegerConstant, BigInteger.Zero, out replacement)
                       || TryReplaceSameNonZeroOperands(
                           left,
                           right,
                           binary.Type,
                           BigInteger.Zero,
                           definitions,
                           out replacement)
                       || TryReplaceNonNegativeRangeBelowDivisorModuloWithDividend(
                           left,
                           right,
                           definitions,
                           out replacement);
            case SsaBinaryOperator.BitwiseAnd:
                return TryReplaceSameOperands(left, right, left, out replacement)
                       || TryReplaceCommutativeAbsorbingConstant(left, right, binary.Type, IsZeroIntegerConstant, BigInteger.Zero, out replacement)
                       || TryReplaceCommutativeIdentity(left, right, IsAllOnesIntegerConstant, out replacement);
            case SsaBinaryOperator.BitwiseOr:
                return TryReplaceSameOperands(left, right, left, out replacement)
                       || TryReplaceCommutativeAbsorbingConstant(left, right, binary.Type, IsAllOnesIntegerConstant, CreateAllOnesIntegerConstantValue(binary.Type), out replacement)
                       || TryReplaceCommutativeIdentity(left, right, IsZeroIntegerConstant, out replacement);
            case SsaBinaryOperator.BitwiseXor:
                return TryReplaceSameOperands(left, right, CreateZeroIntegerConstant(binary.Type), out replacement)
                       || TryReplaceCommutativeIdentity(left, right, IsZeroIntegerConstant, out replacement);
            case SsaBinaryOperator.ShiftLeft:
            case SsaBinaryOperator.ShiftRight:
                return TryReplaceRightIdentity(left, right, IsZeroIntegerConstant, out replacement);
            default:
                return false;
        }
    }

    private static bool TryReplaceSameIntegerComparison(
        SsaBinaryRValue binary,
        SsaValue left,
        SsaValue right,
        out SsaValue replacement)
    {
        if (binary.Type.Kind != StarkTypeKind.Bool
            || left.Type.Kind != StarkTypeKind.Integer
            || left is SsaUndefValue
            || right is SsaUndefValue
            || !EqualityComparer<SsaValue>.Default.Equals(left, right))
        {
            replacement = new SsaUndefValue(binary.Type);
            return false;
        }

        var value = binary.Operator switch
        {
            SsaBinaryOperator.Equal => true,
            SsaBinaryOperator.NotEqual => false,
            SsaBinaryOperator.LessThan => false,
            SsaBinaryOperator.LessThanOrEqual => true,
            SsaBinaryOperator.GreaterThan => false,
            SsaBinaryOperator.GreaterThanOrEqual => true,
            _ => (bool?)null
        };

        if (value is not { } constant)
        {
            replacement = new SsaUndefValue(binary.Type);
            return false;
        }

        replacement = new SsaBoolConstant(constant);
        return true;
    }

    private static bool TryReplaceCommutativeIdentity(
        SsaValue left,
        SsaValue right,
        Func<SsaValue, bool> isIdentity,
        out SsaValue replacement)
    {
        if (isIdentity(right))
        {
            replacement = left;
            return true;
        }

        if (isIdentity(left))
        {
            replacement = right;
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceSameOperands(
        SsaValue left,
        SsaValue right,
        SsaValue replacementValue,
        out SsaValue replacement)
    {
        if (EqualityComparer<SsaValue>.Default.Equals(left, right))
        {
            replacement = replacementValue;
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceCommutativeAbsorbingConstant(
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol type,
        Func<SsaValue, bool> isAbsorbing,
        BigInteger absorbingValue,
        out SsaValue replacement)
    {
        if (isAbsorbing(left) || isAbsorbing(right))
        {
            replacement = new SsaIntegerConstant(absorbingValue, type);
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceRightIdentity(
        SsaValue left,
        SsaValue right,
        Func<SsaValue, bool> isIdentity,
        out SsaValue replacement)
    {
        if (isIdentity(right))
        {
            replacement = left;
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceSameNonZeroOperands(
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol type,
        BigInteger replacementValue,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue replacement)
    {
        if (EqualityComparer<SsaValue>.Default.Equals(left, right)
            && TryGetStaticIntegerRange(left, definitions, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && (min > BigInteger.Zero || max < BigInteger.Zero))
        {
            replacement = new SsaIntegerConstant(replacementValue, type);
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceNonNegativeRangeBelowDivisorModuloWithDividend(
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue replacement)
    {
        if (TryGetPositiveIntegerConstant(right, definitions, new HashSet<string>(StringComparer.Ordinal), out var divisor)
            && TryGetStaticIntegerRange(left, definitions, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < divisor)
        {
            replacement = left;
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryReplaceNonNegativeRangeBelowDivisorWithZero(
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue replacement)
    {
        if (TryGetPositiveIntegerConstant(right, definitions, new HashSet<string>(StringComparer.Ordinal), out var divisor)
            && TryGetStaticIntegerRange(left, definitions, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            && min >= BigInteger.Zero
            && max < divisor)
        {
            replacement = new SsaIntegerConstant(BigInteger.Zero, type);
            return true;
        }

        replacement = left;
        return false;
    }

    private static bool TryGetPositiveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visited,
        out BigInteger constant)
    {
        if (value is SsaIntegerConstant integer && integer.Value > BigInteger.Zero)
        {
            constant = integer.Value;
            return true;
        }

        if (value is SsaValueReference reference
            && visited.Add(reference.Name)
            && definitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    return TryGetPositiveIntegerConstant(use.Value, definitions, visited, out constant);
                case SsaConvertRValue convert when IsSameWidthIntegerConversion(convert):
                    return TryGetPositiveIntegerConstant(convert.Operand, definitions, visited, out constant);
            }
        }

        constant = BigInteger.Zero;
        return false;
    }

    private static bool TryGetStaticIntegerRange(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visited,
        out BigInteger min,
        out BigInteger max)
    {
        switch (value)
        {
            case SsaIntegerConstant integer
                when StarkTypeSymbols.IntegerValueFitsEffectiveRange(integer.Value, integer.Type):
                min = integer.Value;
                max = integer.Value;
                return true;
            default:
                if (value is SsaValueReference reference
                    && visited.Add(reference.Name)
                    && definitions.TryGetValue(reference.Name, out var definition))
                {
                    switch (definition)
                    {
                        case SsaUseRValue use:
                            return TryGetStaticIntegerRange(use.Value, definitions, visited, out min, out max);
                        case SsaConvertRValue convert when IsSameWidthIntegerConversion(convert):
                            return TryGetStaticIntegerRange(convert.Operand, definitions, visited, out min, out max);
                    }
                }

                var type = value.Type;
                if (StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out var rangeMin, out var rangeMax))
                {
                    min = rangeMin;
                    max = rangeMax;
                    return true;
                }

                min = BigInteger.Zero;
                max = BigInteger.Zero;
                return false;
        }
    }

    private static bool IsSameWidthIntegerConversion(SsaConvertRValue convert)
    {
        return convert.Operand.Type.Kind == StarkTypeKind.Integer
            && convert.TargetType.Kind == StarkTypeKind.Integer
            && convert.Operand.Type.BitWidth == convert.TargetType.BitWidth;
    }

    private static bool TryReplaceRightAbsorbingConstant(
        SsaValue left,
        SsaValue right,
        StarkTypeSymbol type,
        Func<SsaValue, bool> isAbsorbing,
        BigInteger absorbingValue,
        out SsaValue replacement)
    {
        if (isAbsorbing(right))
        {
            replacement = new SsaIntegerConstant(absorbingValue, type);
            return true;
        }

        replacement = left;
        return false;
    }

    private static SsaIntegerConstant CreateZeroIntegerConstant(StarkTypeSymbol type)
    {
        return new SsaIntegerConstant(BigInteger.Zero, type);
    }

    private static bool IsZeroIntegerConstant(SsaValue value)
    {
        return value is SsaIntegerConstant { Value.IsZero: true };
    }

    private static bool IsOneIntegerConstant(SsaValue value)
    {
        return value is SsaIntegerConstant { Value.IsOne: true };
    }

    private static bool IsAllOnesIntegerConstant(SsaValue value)
    {
        if (value is not SsaIntegerConstant integer
            || integer.Type.Kind != StarkTypeKind.Integer
            || integer.Type.BitWidth is not int bitWidth
            || bitWidth <= 0)
        {
            return false;
        }

        if (integer.Value == -BigInteger.One)
        {
            return true;
        }

        return integer.Type.IsUnsigned
            && integer.Value == (BigInteger.One << bitWidth) - BigInteger.One;
    }

    private static BigInteger CreateAllOnesIntegerConstantValue(StarkTypeSymbol type)
    {
        return type.IsUnsigned && type.BitWidth is int bitWidth && bitWidth > 0
            ? (BigInteger.One << bitWidth) - BigInteger.One
            : -BigInteger.One;
    }

    private static SsaValue ResolveIdentityOperand(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return ResolveIdentityOperand(
            RewriteValue(value, replacements),
            replacements,
            definitions,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static SsaValue ResolveIdentityOperand(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visited)
    {
        while (value is SsaValueReference reference
            && visited.Add(reference.Name)
            && definitions.TryGetValue(reference.Name, out var definition))
        {
            switch (definition)
            {
                case SsaUseRValue use:
                    value = RewriteValue(use.Value, replacements);
                    continue;
                case SsaConvertRValue convert:
                    var operand = ResolveIdentityOperand(
                        RewriteValue(convert.Operand, replacements),
                        replacements,
                        definitions,
                        visited);

                    if (TryRetypeIntegerIdentityConstant(operand, convert.TargetType, out var converted))
                    {
                        return converted;
                    }

                    return value;
                default:
                    return value;
            }
        }

        return value;
    }

    private static bool TryRetypeIntegerIdentityConstant(
        SsaValue value,
        StarkTypeSymbol targetType,
        out SsaValue converted)
    {
        if (value is SsaIntegerConstant integer
            && targetType.Kind == StarkTypeKind.Integer
            && (integer.Value.IsZero
                || integer.Value.IsOne
                || (!targetType.IsUnsigned && integer.Value == -BigInteger.One)))
        {
            converted = new SsaIntegerConstant(integer.Value, targetType);
            return true;
        }

        converted = value;
        return false;
    }

    private static bool TryResolveAggregateFieldValue(
        SsaValue aggregate,
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        out SsaValue replacement)
    {
        return TryResolveAggregateFieldValueCore(
            RewriteValue(aggregate, replacements),
            fieldName,
            fieldIndex,
            fieldType,
            replacements,
            definitions,
            phiDefinitions,
            new HashSet<string>(StringComparer.Ordinal),
            out replacement);
    }

    private static bool TryResolveAggregateFieldValueCore(
        SsaValue aggregate,
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (aggregate)
        {
            case SsaZeroInitializerValue:
                replacement = CreateZeroValue(fieldType);
                return true;
            case SsaValueReference reference:
                if (!seen.Add(reference.Name))
                {
                    replacement = aggregate;
                    return false;
                }

                if (definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveAggregateFieldValueFromDefinition(
                        definition,
                        fieldName,
                        fieldIndex,
                        fieldType,
                        replacements,
                        definitions,
                        phiDefinitions,
                        seen,
                        out replacement);
                }

                if (phiDefinitions.TryGetValue(reference.Name, out var phi))
                {
                    return TryResolveAggregateFieldValueFromPhi(
                        phi,
                        fieldName,
                        fieldIndex,
                        fieldType,
                        replacements,
                        definitions,
                        phiDefinitions,
                        seen,
                        out replacement);
                }

                replacement = aggregate;
                return false;
            default:
                replacement = aggregate;
                return false;
        }
    }

    private static bool TryResolveAggregateFieldValueFromDefinition(
        SsaRValue definition,
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveAggregateFieldValueCore(
                    RewriteValue(use.Value, replacements),
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaSelectRValue select:
                return TryResolveAggregateFieldValueFromSelect(
                    select,
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaInsertFieldRValue insertField when insertField.FieldIndex == fieldIndex
                                                     && string.Equals(insertField.FieldName, fieldName, StringComparison.Ordinal):
                replacement = RewriteValue(insertField.Value, replacements);
                return true;
            case SsaInsertFieldRValue insertField:
                return TryResolveAggregateFieldValueCore(
                    RewriteValue(insertField.Target, replacements),
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaInsertIndexRValue insertIndex:
                return TryResolveAggregateFieldValueCore(
                    RewriteValue(insertIndex.Target, replacements),
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            default:
                replacement = new SsaUndefValue(fieldType);
                return false;
        }
    }

    private static bool TryResolveAggregateFieldValueFromSelect(
        SsaSelectRValue select,
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        if (!TryResolveAggregateFieldValueCore(
                RewriteValue(select.WhenTrue, replacements),
                fieldName,
                fieldIndex,
                fieldType,
                replacements,
                definitions,
                phiDefinitions,
                new HashSet<string>(seen, StringComparer.Ordinal),
                out var whenTrueField)
            || !TryResolveAggregateFieldValueCore(
                RewriteValue(select.WhenFalse, replacements),
                fieldName,
                fieldIndex,
                fieldType,
                replacements,
                definitions,
                phiDefinitions,
                new HashSet<string>(seen, StringComparer.Ordinal),
                out var whenFalseField))
        {
            replacement = new SsaUndefValue(fieldType);
            return false;
        }

        whenTrueField = RewriteValue(whenTrueField, replacements);
        whenFalseField = RewriteValue(whenFalseField, replacements);
        if (EqualityComparer<SsaValue>.Default.Equals(whenTrueField, whenFalseField))
        {
            replacement = whenTrueField;
            return true;
        }

        replacement = new SsaUndefValue(fieldType);
        return false;
    }

    private static bool TryResolveAggregateFieldValueFromPhi(
        SsaPhi phi,
        string fieldName,
        int fieldIndex,
        StarkTypeSymbol fieldType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        SsaValue? commonValue = null;
        foreach (var incoming in phi.Incomings)
        {
            var incomingValue = RewriteValue(incoming.Value, replacements);
            if (incomingValue is SsaValueReference selfReference
                && string.Equals(selfReference.Name, phi.ResultName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryResolveAggregateFieldValueCore(
                    incomingValue,
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    new HashSet<string>(seen, StringComparer.Ordinal),
                    out var incomingFieldValue))
            {
                replacement = new SsaUndefValue(fieldType);
                return false;
            }

            incomingFieldValue = RewriteValue(incomingFieldValue, replacements);
            if (commonValue is null)
            {
                commonValue = incomingFieldValue;
                continue;
            }

            if (!EqualityComparer<SsaValue>.Default.Equals(commonValue, incomingFieldValue))
            {
                replacement = new SsaUndefValue(fieldType);
                return false;
            }
        }

        if (commonValue is not null)
        {
            replacement = commonValue;
            return true;
        }

        replacement = new SsaUndefValue(fieldType);
        return false;
    }

    private static bool TryResolveAggregateIndexValue(
        SsaValue aggregate,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        out SsaValue replacement)
    {
        return TryResolveAggregateIndexValueCore(
            RewriteValue(aggregate, replacements),
            elementIndex,
            elementType,
            replacements,
            definitions,
            phiDefinitions,
            new HashSet<string>(StringComparer.Ordinal),
            out replacement);
    }

    private static bool TryResolveAggregateIndexValueCore(
        SsaValue aggregate,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (aggregate)
        {
            case SsaZeroInitializerValue:
                replacement = CreateZeroValue(elementType);
                return true;
            case SsaValueReference reference:
                if (!seen.Add(reference.Name))
                {
                    replacement = aggregate;
                    return false;
                }

                if (definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryResolveAggregateIndexValueFromDefinition(
                        definition,
                        elementIndex,
                        elementType,
                        replacements,
                        definitions,
                        phiDefinitions,
                        seen,
                        out replacement);
                }

                if (phiDefinitions.TryGetValue(reference.Name, out var phi))
                {
                    return TryResolveAggregateIndexValueFromPhi(
                        phi,
                        elementIndex,
                        elementType,
                        replacements,
                        definitions,
                        phiDefinitions,
                        seen,
                        out replacement);
                }

                replacement = aggregate;
                return false;
            default:
                replacement = aggregate;
                return false;
        }
    }

    private static bool TryResolveAggregateIndexValueFromDefinition(
        SsaRValue definition,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (definition)
        {
            case SsaUseRValue use:
                return TryResolveAggregateIndexValueCore(
                    RewriteValue(use.Value, replacements),
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaSelectRValue select:
                return TryResolveAggregateIndexValueFromSelect(
                    select,
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaInsertIndexRValue insertIndex when insertIndex.ElementIndex == elementIndex:
                replacement = RewriteValue(insertIndex.Value, replacements);
                return true;
            case SsaInsertIndexRValue insertIndex:
                return TryResolveAggregateIndexValueCore(
                    RewriteValue(insertIndex.Target, replacements),
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            case SsaInsertFieldRValue insertField:
                return TryResolveAggregateIndexValueCore(
                    RewriteValue(insertField.Target, replacements),
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    seen,
                    out replacement);
            default:
                replacement = new SsaUndefValue(elementType);
                return false;
        }
    }

    private static bool TryResolveAggregateIndexValueFromSelect(
        SsaSelectRValue select,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        if (!TryResolveAggregateIndexValueCore(
                RewriteValue(select.WhenTrue, replacements),
                elementIndex,
                elementType,
                replacements,
                definitions,
                phiDefinitions,
                new HashSet<string>(seen, StringComparer.Ordinal),
                out var whenTrueElement)
            || !TryResolveAggregateIndexValueCore(
                RewriteValue(select.WhenFalse, replacements),
                elementIndex,
                elementType,
                replacements,
                definitions,
                phiDefinitions,
                new HashSet<string>(seen, StringComparer.Ordinal),
                out var whenFalseElement))
        {
            replacement = new SsaUndefValue(elementType);
            return false;
        }

        whenTrueElement = RewriteValue(whenTrueElement, replacements);
        whenFalseElement = RewriteValue(whenFalseElement, replacements);
        if (EqualityComparer<SsaValue>.Default.Equals(whenTrueElement, whenFalseElement))
        {
            replacement = whenTrueElement;
            return true;
        }

        replacement = new SsaUndefValue(elementType);
        return false;
    }

    private static bool TryResolveAggregateIndexValueFromPhi(
        SsaPhi phi,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaPhi> phiDefinitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        SsaValue? commonValue = null;
        foreach (var incoming in phi.Incomings)
        {
            var incomingValue = RewriteValue(incoming.Value, replacements);
            if (incomingValue is SsaValueReference selfReference
                && string.Equals(selfReference.Name, phi.ResultName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryResolveAggregateIndexValueCore(
                    incomingValue,
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    phiDefinitions,
                    new HashSet<string>(seen, StringComparer.Ordinal),
                    out var incomingElementValue))
            {
                replacement = new SsaUndefValue(elementType);
                return false;
            }

            incomingElementValue = RewriteValue(incomingElementValue, replacements);
            if (commonValue is null)
            {
                commonValue = incomingElementValue;
                continue;
            }

            if (!EqualityComparer<SsaValue>.Default.Equals(commonValue, incomingElementValue))
            {
                replacement = new SsaUndefValue(elementType);
                return false;
            }
        }

        if (commonValue is not null)
        {
            replacement = commonValue;
            return true;
        }

        replacement = new SsaUndefValue(elementType);
        return false;
    }

    private static SsaValue CreateZeroValue(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Bool => new SsaBoolConstant(false),
            StarkTypeKind.Integer => new SsaIntegerConstant(BigInteger.Zero, type),
            StarkTypeKind.Float => new SsaFloatConstant(FormatFloatLiteral(0, type), type),
            _ => new SsaZeroInitializerValue(type)
        };
    }

    private static string FormatFloatLiteral(double value, StarkTypeSymbol type)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static SsaFunction RemoveUnusedPureInstructions(SsaFunction function)
    {
        var current = function;

        while (true)
        {
            var usedNames = CollectUsedValueNames(current);
            var changed = false;
            var blocks = new List<SsaBasicBlock>(current.Blocks.Count);

            foreach (var block in current.Blocks)
            {
                var instructions = block.Instructions
                    .Where(instruction =>
                    {
                        if (instruction is not SsaValueInstruction valueInstruction)
                        {
                            return true;
                        }

                        if (usedNames.Contains(valueInstruction.ResultName))
                        {
                            return true;
                        }

                        if (!IsPureRemovableInstruction(valueInstruction.Value))
                        {
                            return true;
                        }

                        changed = true;
                        return false;
                    })
                    .ToArray();

                blocks.Add(block with { Instructions = instructions });
            }

            if (!changed)
            {
                return current;
            }

            current = current with { Blocks = blocks.ToArray() };
        }
    }

    private static bool IsPureRemovableInstruction(SsaRValue value)
    {
        return value is not SsaCallRValue
            and not SsaIndirectCallRValue
            and not SsaDynamicStorageAllocationRValue
            and not SsaDynamicStorageFreeRValue
            and not SsaHeapStorageFreeRValue
            and not SsaDynamicStorageReserveRValue
            and not SsaDynamicStorageTryReserveRValue
            and not SsaDynamicStorageTryReserveCapacityRValue
            and not SsaDynamicStorageMoveLastRValue
            and not SsaDynamicStorageMoveAtRValue;
    }

    private static SsaFunction RemoveUnusedArenaFrameInstructions(SsaFunction function)
    {
        if (UsesArenaFrameStorage(function))
        {
            return function;
        }

        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var instructions = block.Instructions
                    .Where(instruction =>
                    {
                        if (instruction is not (SsaArenaFrameEnterInstruction or SsaArenaFrameLeaveInstruction))
                        {
                            return true;
                        }

                        changed = true;
                        return false;
                    })
                    .ToArray();

                return changed && instructions.Length != block.Instructions.Count
                    ? block with { Instructions = instructions }
                    : block;
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static bool UsesArenaFrameStorage(SsaFunction function)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaAllocateLocalInstruction { StorageClass: "arena" }:
                    case SsaValueInstruction { Value: SsaDynamicStorageAllocationRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageTryReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageTryReserveCapacityRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                        return true;
                }
            }
        }

        return false;
    }

    private static SsaFunction RemoveStoresToWriteOnlyLocalStorage(SsaFunction function)
    {
        var definitions = BuildValueDefinitions(function);
        var requiredLocals = CollectReadOrEscapedLocalSlots(function, definitions);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var instructions = block.Instructions
                    .Where(instruction =>
                    {
                        if (instruction is not SsaStoreIndirectInstruction storeIndirect
                            || !TryResolveSingleLocalRoot(storeIndirect.Address, definitions, out var localName)
                            || requiredLocals.Contains(localName))
                        {
                            return true;
                        }

                        changed = true;
                        return false;
                    })
                    .ToArray();

                return instructions.Length == block.Instructions.Count
                    ? block
                    : block with { Instructions = instructions };
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static SsaFunction RemoveUnusedLocalStorage(SsaFunction function)
    {
        var requiredLocals = CollectRequiredLocalSlots(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var instructions = block.Instructions
                    .Where(instruction =>
                    {
                        if (!TryGetLocalStorageName(instruction, out var localName)
                            || requiredLocals.Contains(localName))
                        {
                            return true;
                        }

                        changed = true;
                        return false;
                    })
                    .ToArray();

                return instructions.Length == block.Instructions.Count
                    ? block
                    : block with { Instructions = instructions };
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static IReadOnlyDictionary<string, SsaRValue> BuildValueDefinitions(SsaFunction function)
    {
        var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                definitions[instruction.ResultName] = instruction.Value;
            }
        }

        return definitions;
    }

    private static HashSet<string> CollectReadOrEscapedLocalSlots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var requiredLocals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    AddLocalRoots(incoming.Value, definitions, requiredLocals);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                AddReadOrEscapedLocalSlots(instruction, definitions, requiredLocals);
            }

            AddReadOrEscapedLocalSlots(block.Terminator, definitions, requiredLocals);
        }

        return requiredLocals;
    }

    private static void AddReadOrEscapedLocalSlots(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> requiredLocals)
    {
        switch (instruction)
        {
            case SsaValueInstruction { Value: SsaAddressOfLocalRValue }:
            case SsaValueInstruction { Value: SsaFieldAddressRValue }:
            case SsaValueInstruction { Value: SsaElementAddressRValue }:
                break;
            case SsaValueInstruction valueInstruction:
                AddReadOrEscapedLocalSlots(valueInstruction.Value, definitions, requiredLocals);
                break;
            case SsaCallInstruction call:
                AddReadOrEscapedDirectCallLocalSlots(call, definitions, requiredLocals);
                break;
            case SsaIndirectCallInstruction call:
                AddLocalRoots(call.Target, definitions, requiredLocals);
                foreach (var argument in call.Arguments)
                {
                    AddLocalRoots(argument, definitions, requiredLocals);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    AddLocalRoots(address, definitions, requiredLocals);
                }

                foreach (var localName in call.IndirectArgumentLocalNames?.OfType<string>() ?? [])
                {
                    requiredLocals.Add(localName);
                }

                break;
            case SsaStoreLocalInstruction storeLocal:
                requiredLocals.Add(storeLocal.LocalName);
                AddLocalRoots(storeLocal.Value, definitions, requiredLocals);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                AddLocalRoots(storeIndirect.Value, definitions, requiredLocals);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                AddLocalRoots(copyMemory.DestinationAddress, definitions, requiredLocals);
                AddLocalRoots(copyMemory.SourceAddress, definitions, requiredLocals);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                AddLocalRoots(storeGlobal.Value, definitions, requiredLocals);
                break;
        }
    }

    private static void AddReadOrEscapedLocalSlots(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> requiredLocals)
    {
        if (terminator.Condition is not null)
        {
            AddLocalRoots(terminator.Condition, definitions, requiredLocals);
        }

        if (terminator.Value is not null)
        {
            AddLocalRoots(terminator.Value, definitions, requiredLocals);
        }

        if (terminator.TailDirectCall is not null)
        {
            foreach (var argument in terminator.TailDirectCall.Arguments)
            {
                AddLocalRoots(argument, definitions, requiredLocals);
            }

            foreach (var address in terminator.TailDirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                AddLocalRoots(address, definitions, requiredLocals);
            }

            foreach (var localName in terminator.TailDirectCall.IndirectArgumentLocalNames?.OfType<string>() ?? [])
            {
                requiredLocals.Add(localName);
            }
        }

        if (terminator.TailIndirectCall is not null)
        {
            AddLocalRoots(terminator.TailIndirectCall.Target, definitions, requiredLocals);
            foreach (var argument in terminator.TailIndirectCall.Arguments)
            {
                AddLocalRoots(argument, definitions, requiredLocals);
            }

            foreach (var address in terminator.TailIndirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                AddLocalRoots(address, definitions, requiredLocals);
            }

            foreach (var localName in terminator.TailIndirectCall.IndirectArgumentLocalNames?.OfType<string>() ?? [])
            {
                requiredLocals.Add(localName);
            }
        }

        foreach (var switchCase in terminator.SwitchCases ?? [])
        {
            AddLocalRoots(switchCase.MatchValue, definitions, requiredLocals);
        }
    }

    private static void AddReadOrEscapedLocalSlots(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> requiredLocals)
    {
        switch (value)
        {
            case SsaLoadLocalRValue loadLocal:
                requiredLocals.Add(loadLocal.LocalName);
                break;
            case SsaMakeSliceFromLocalRValue makeSlice:
                requiredLocals.Add(makeSlice.LocalName);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                AddLocalRoots(loadIndirect.Address, definitions, requiredLocals);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddLocalRoots(argument, definitions, requiredLocals);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    AddLocalRoots(address, definitions, requiredLocals);
                }

                foreach (var localName in call.IndirectArgumentLocalNames?.OfType<string>() ?? [])
                {
                    requiredLocals.Add(localName);
                }

                break;
            case SsaIndirectCallRValue indirectCall:
                AddLocalRoots(indirectCall.Target, definitions, requiredLocals);
                foreach (var argument in indirectCall.Arguments)
                {
                    AddLocalRoots(argument, definitions, requiredLocals);
                }

                foreach (var address in indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    AddLocalRoots(address, definitions, requiredLocals);
                }

                foreach (var localName in indirectCall.IndirectArgumentLocalNames?.OfType<string>() ?? [])
                {
                    requiredLocals.Add(localName);
                }

                break;
            default:
                foreach (var operand in EnumerateRValueOperands(value))
                {
                    AddLocalRoots(operand, definitions, requiredLocals);
                }

                break;
        }
    }

    private static void AddReadOrEscapedDirectCallLocalSlots(
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> requiredLocals)
    {
        foreach (var argument in call.Arguments)
        {
            AddLocalRoots(argument, definitions, requiredLocals);
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            AddLocalRoots(address, definitions, requiredLocals);
        }

        foreach (var localName in call.IndirectArgumentLocalNames?.OfType<string>() ?? [])
        {
            requiredLocals.Add(localName);
        }
    }

    private static bool TryResolveSingleLocalRoot(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out string localName)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        AddLocalRoots(value, definitions, roots);
        if (roots.Count == 1)
        {
            localName = roots.Single();
            return true;
        }

        localName = string.Empty;
        return false;
    }

    private static void AddLocalRoots(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> roots)
    {
        AddLocalRoots(value, definitions, roots, new HashSet<string>(StringComparer.Ordinal));
    }

    private static void AddLocalRoots(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> roots,
        ISet<string> visiting)
    {
        if (value is not SsaValueReference reference
            || !visiting.Add(reference.Name))
        {
            return;
        }

        try
        {
            if (definitions.TryGetValue(reference.Name, out var definition))
            {
                AddLocalRoots(definition, definitions, roots, visiting);
            }
        }
        finally
        {
            visiting.Remove(reference.Name);
        }
    }

    private static void AddLocalRoots(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> roots,
        ISet<string> visiting)
    {
        switch (value)
        {
            case SsaAddressOfLocalRValue addressOfLocal:
                roots.Add(addressOfLocal.LocalName);
                break;
            case SsaMakeSliceFromLocalRValue makeSlice:
                roots.Add(makeSlice.LocalName);
                break;
            case SsaLoadLocalRValue loadLocal:
                roots.Add(loadLocal.LocalName);
                break;
            default:
                foreach (var operand in EnumerateRValueOperands(value))
                {
                    AddLocalRoots(operand, definitions, roots, visiting);
                }

                break;
        }
    }

    private static HashSet<string> CollectRequiredLocalSlots(SsaFunction function)
    {
        var requiredLocals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction valueInstruction)
                {
                    AddReferencedLocalSlots(valueInstruction.Value, requiredLocals);
                }
                else if (instruction is SsaCallInstruction directCall)
                {
                    AddReferencedDirectCallLocalSlots(directCall, requiredLocals);
                }
                else if (instruction is SsaIndirectCallInstruction indirectCall)
                {
                    AddReferencedIndirectCallLocalSlots(indirectCall, requiredLocals);
                }
            }
        }

        return requiredLocals;
    }

    private static void AddReferencedLocalSlots(SsaRValue value, ISet<string> requiredLocals)
    {
        switch (value)
        {
            case SsaMakeSliceFromLocalRValue makeSlice:
                requiredLocals.Add(makeSlice.LocalName);
                break;
            case SsaAddressOfLocalRValue addressOfLocal:
                requiredLocals.Add(addressOfLocal.LocalName);
                break;
            case SsaLoadLocalRValue loadLocal:
                requiredLocals.Add(loadLocal.LocalName);
                break;
            case SsaCallRValue { IndirectArgumentLocalNames: { } indirectLocals }:
                foreach (var localName in indirectLocals)
                {
                    if (localName is not null)
                    {
                        requiredLocals.Add(localName);
                    }
                }

                break;
            case SsaIndirectCallRValue { IndirectArgumentLocalNames: { } indirectLocals }:
                foreach (var localName in indirectLocals)
                {
                    if (localName is not null)
                    {
                        requiredLocals.Add(localName);
                    }
                }

                break;
        }
    }

    private static void AddReferencedDirectCallLocalSlots(ISsaDirectCallOperation call, ISet<string> requiredLocals)
    {
        foreach (var localName in call.IndirectArgumentLocalNames?.OfType<string>() ?? [])
        {
            requiredLocals.Add(localName);
        }
    }

    private static void AddReferencedIndirectCallLocalSlots(ISsaIndirectCallOperation call, ISet<string> requiredLocals)
    {
        foreach (var localName in call.IndirectArgumentLocalNames?.OfType<string>() ?? [])
        {
            requiredLocals.Add(localName);
        }
    }

    private static bool TryGetLocalStorageName(SsaInstruction instruction, out string localName)
    {
        switch (instruction)
        {
            case SsaAllocateLocalInstruction allocateLocal:
                localName = allocateLocal.LocalName;
                return true;
            case SsaLifetimeStartInstruction lifetimeStart:
                localName = lifetimeStart.LocalName;
                return true;
            case SsaLifetimeEndInstruction lifetimeEnd:
                localName = lifetimeEnd.LocalName;
                return true;
            case SsaDeallocateLocalInstruction deallocateLocal:
                localName = deallocateLocal.LocalName;
                return true;
            case SsaStoreLocalInstruction storeLocal:
                localName = storeLocal.LocalName;
                return true;
            default:
                localName = string.Empty;
                return false;
        }
    }

    private static HashSet<string> CollectUsedValueNames(SsaFunction function)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    AddUsedValueNames(incoming.Value, used);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                foreach (var value in EnumerateInstructionOperands(instruction))
                {
                    AddUsedValueNames(value, used);
                }
            }

            foreach (var value in EnumerateTerminatorOperands(block.Terminator))
            {
                AddUsedValueNames(value, used);
            }
        }

        return used;
    }

    private static void AddUsedValueNames(SsaValue value, ISet<string> usedNames)
    {
        if (value is SsaValueReference reference)
        {
            usedNames.Add(reference.Name);
        }
    }

    private static IEnumerable<SsaValue> EnumerateInstructionOperands(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => EnumerateRValueOperands(valueInstruction.Value),
            SsaCallInstruction call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments,
            SsaIndirectCallInstruction call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Prepend(call.Target).Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments.Prepend(call.Target),
            SsaLifetimeStartInstruction => [],
            SsaLifetimeEndInstruction => [],
            SsaDeallocateLocalInstruction => [],
            SsaStoreLocalInstruction storeLocal => [storeLocal.Value],
            SsaCopyMemoryInstruction copyMemory => [copyMemory.DestinationAddress, copyMemory.SourceAddress],
            SsaStoreIndirectInstruction storeIndirect => [storeIndirect.Address, storeIndirect.Value],
            SsaStoreGlobalInstruction storeGlobal => [storeGlobal.Value],
            _ => []
        };
    }

    private static IEnumerable<SsaValue> EnumerateRValueOperands(SsaRValue value)
    {
        return value switch
        {
            SsaUseRValue use => [use.Value],
            SsaUnaryRValue unary => [unary.Operand],
            SsaBinaryRValue binary => [binary.Left, binary.Right],
            SsaSelectRValue select => [select.Condition, select.WhenTrue, select.WhenFalse],
            SsaCallRValue call => call.IndirectArgumentAddresses is { Count: > 0 }
                ? call.Arguments.Concat(call.IndirectArgumentAddresses.OfType<SsaValue>())
                : call.Arguments,
            SsaIndirectCallRValue indirectCall => indirectCall.IndirectArgumentAddresses is { Count: > 0 }
                ? indirectCall.Arguments.Prepend(indirectCall.Target).Concat(indirectCall.IndirectArgumentAddresses.OfType<SsaValue>())
                : indirectCall.Arguments.Prepend(indirectCall.Target),
            SsaConvertRValue convert => [convert.Operand],
            SsaExtractFieldRValue extractField => [extractField.Target],
            SsaInsertFieldRValue insertField => [insertField.Target, insertField.Value],
            SsaExtractIndexRValue extractIndex => [extractIndex.Target],
            SsaInsertIndexRValue insertIndex => [insertIndex.Target, insertIndex.Value],
            SsaDynVTableSlotRValue vtableSlot => [vtableSlot.VtablePointer],
            SsaMakeSliceFromPointerRValue makeSlice => [makeSlice.Pointer, makeSlice.Length],
            SsaDynamicStorageAllocationRValue allocation => [allocation.Capacity],
            SsaDynamicStorageFreeRValue free => [free.Storage],
            SsaHeapStorageFreeRValue free => [free.Pointer],
            SsaDynamicStorageReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
            SsaDynamicStorageTryReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
            SsaDynamicStorageTryReserveCapacityRValue reserve => [reserve.StorageAddress, reserve.TargetCapacity],
            SsaDynamicStorageMoveLastRValue moveLast => [moveLast.StorageAddress],
            SsaDynamicStorageMoveAtRValue moveAt => [moveAt.StorageAddress, moveAt.Index],
            SsaLoadSliceElementRValue loadSlice => [loadSlice.Slice, loadSlice.Index],
            SsaTextSliceRValue textSlice => [textSlice.TextValue, textSlice.Start, textSlice.Length],
            SsaFieldAddressRValue fieldAddress => [fieldAddress.Address],
            SsaElementAddressRValue elementAddress when elementAddress.Index is not null => [elementAddress.Address, elementAddress.Index],
            SsaElementAddressRValue elementAddress => [elementAddress.Address],
            SsaSliceElementAddressRValue sliceElementAddress => [sliceElementAddress.Slice, sliceElementAddress.Index],
            SsaLoadIndirectRValue loadIndirect => [loadIndirect.Address],
            _ => []
        };
    }

    private static IEnumerable<SsaValue> EnumerateTerminatorOperands(SsaTerminator terminator)
    {
        if (terminator.Condition is not null)
        {
            yield return terminator.Condition;
        }

        if (terminator.Value is not null)
        {
            yield return terminator.Value;
        }

        if (terminator.TailDirectCall is not null)
        {
            foreach (var argument in terminator.TailDirectCall.Arguments)
            {
                yield return argument;
            }

            foreach (var address in terminator.TailDirectCall.IndirectArgumentAddresses ?? [])
            {
                if (address is not null)
                {
                    yield return address;
                }
            }
        }

        if (terminator.TailIndirectCall is not null)
        {
            yield return terminator.TailIndirectCall.Target;

            foreach (var argument in terminator.TailIndirectCall.Arguments)
            {
                yield return argument;
            }

            foreach (var address in terminator.TailIndirectCall.IndirectArgumentAddresses ?? [])
            {
                if (address is not null)
                {
                    yield return address;
                }
            }
        }

        if (terminator.SwitchCases is not null)
        {
            foreach (var switchCase in terminator.SwitchCases)
            {
                yield return switchCase.MatchValue;
            }
        }
    }

    private static bool IsCommutative(SsaBinaryOperator operatorKind)
    {
        return operatorKind switch
        {
            SsaBinaryOperator.Add => true,
            SsaBinaryOperator.Multiply => true,
            SsaBinaryOperator.WrappingAdd => true,
            SsaBinaryOperator.WrappingMultiply => true,
            SsaBinaryOperator.SaturatingAdd => true,
            SsaBinaryOperator.SaturatingMultiply => true,
            SsaBinaryOperator.BitwiseAnd => true,
            SsaBinaryOperator.BitwiseXor => true,
            SsaBinaryOperator.BitwiseOr => true,
            SsaBinaryOperator.Equal => true,
            SsaBinaryOperator.NotEqual => true,
            _ => false
        };
    }

    private static string ValueKey(SsaValue? value)
    {
        return value switch
        {
            null => "<null>",
            SsaValueReference reference => $"ref:{reference.Name}:{TypeKey(reference.Type)}",
            SsaIntegerConstant integer => $"int:{integer.Value}:{TypeKey(integer.Type)}",
            SsaFloatConstant floating => $"float:{floating.LiteralText}:{TypeKey(floating.Type)}",
            SsaStringConstant text => $"string:{text.LiteralText}:{TypeKey(text.Type)}",
            SsaBoolConstant boolean => $"bool:{boolean.Value}",
            SsaNullConstant nullValue => $"null:{TypeKey(nullValue.Type)}",
            SsaUndefValue undef => $"undef:{TypeKey(undef.Type)}",
            SsaZeroInitializerValue zero => $"zero:{TypeKey(zero.Type)}",
            _ => $"{value.GetType().Name}:{value.Text}:{TypeKey(value.Type)}"
        };
    }

    private static string TypeKey(StarkTypeSymbol type)
    {
        return type.ToString();
    }

    private static SsaFunction RemoveStalePhiIncomings(SsaFunction function)
    {
        var liveEdges = CollectLiveEdges(function.Blocks);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                if (block.Phis.Count == 0)
                {
                    return block;
                }

                var phis = block.Phis
                    .Select(phi =>
                    {
                        var incomings = phi.Incomings
                            .Where(incoming => liveEdges.Contains((incoming.PredecessorBlockId, block.Id)))
                            .ToArray();
                        if (incomings.Length != phi.Incomings.Count)
                        {
                            changed = true;
                        }

                        return new SsaPhi(
                            phi.ResultName,
                            phi.VariableName,
                            phi.Type,
                            CoalescePhiIncomings(incomings));
                    })
                    .ToArray();

                return block with { Phis = phis };
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
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

    private static SsaFunction CollapseTrampolineBlocks(SsaFunction function)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var predecessors = BuildPredecessors(function.Blocks);
        var redirects = new Dictionary<int, int>();

        foreach (var block in function.Blocks)
        {
            if (block.Id == function.EntryBlockId)
            {
                continue;
            }

            if (block.Phis.Count != 0 || block.Instructions.Count != 0)
            {
                continue;
            }

            if (block.Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 })
            {
                continue;
            }

            if (predecessors.GetValueOrDefault(block.Id, []).Count != 1)
            {
                continue;
            }

            var target = block.Terminator.Targets[0];
            if (target == block.Id)
            {
                continue;
            }

            if (byId.TryGetValue(target, out var targetBlock) && targetBlock.Phis.Count != 0)
            {
                continue;
            }

            redirects[block.Id] = target;
        }

        if (redirects.Count == 0)
        {
            return function;
        }

        var targetCache = new Dictionary<int, int>();
        var predecessorCache = new Dictionary<int, int>();
        var blocks = function.Blocks
            .Where(block => !redirects.ContainsKey(block.Id))
            .Select(block => new SsaBasicBlock(
                block.Id,
                block.Label,
                block.Phis
                    .Select(phi => new SsaPhi(
                        phi.ResultName,
                        phi.VariableName,
                        phi.Type,
                        CoalescePhiIncomings(
                            phi.Incomings.Select(incoming => new SsaPhiIncoming(
                                    ResolveCollapsedPredecessor(incoming.PredecessorBlockId, redirects, predecessors, predecessorCache),
                                    incoming.Value))
                                .ToArray())))
                    .ToArray(),
                block.Instructions,
                RewriteTerminator(
                    block.Terminator,
                    replacements: null,
                    target => ResolveCollapsedTarget(target, redirects, targetCache))))
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static SsaFunction MergeLinearBlocks(SsaFunction function)
    {
        var current = function;

        while (TryFindLinearMergeCandidate(current, out var predecessorBlockId, out var blockId))
        {
            current = MergeLinearBlocks(current, predecessorBlockId, blockId);
        }

        return current;
    }

    private static bool TryFindLinearMergeCandidate(
        SsaFunction function,
        out int predecessorBlockId,
        out int blockId)
    {
        predecessorBlockId = -1;
        blockId = -1;

        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var predecessors = BuildPredecessors(function.Blocks);

        foreach (var block in function.Blocks)
        {
            if (block.Id == function.EntryBlockId || block.Phis.Count != 0)
            {
                continue;
            }

            var blockPredecessors = predecessors.GetValueOrDefault(block.Id, []);
            if (blockPredecessors.Count != 1)
            {
                continue;
            }

            var predecessor = byId[blockPredecessors[0]];
            if (predecessor.Terminator.Kind != SsaTerminatorKind.Goto
                || predecessor.Terminator.Targets.Count != 1
                || predecessor.Terminator.Targets[0] != block.Id)
            {
                continue;
            }

            predecessorBlockId = predecessor.Id;
            blockId = block.Id;
            return true;
        }

        return false;
    }

    private static SsaFunction MergeLinearBlocks(
        SsaFunction function,
        int predecessorBlockId,
        int blockId)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var predecessor = byId[predecessorBlockId];
        var block = byId[blockId];

        var blocks = function.Blocks
            .Where(candidate => candidate.Id != blockId)
            .Select(candidate =>
            {
                if (candidate.Id == predecessorBlockId)
                {
                    return new SsaBasicBlock(
                        candidate.Id,
                        candidate.Label,
                        candidate.Phis,
                        candidate.Instructions.Concat(block.Instructions).ToArray(),
                        block.Terminator);
                }

                var phis = candidate.Phis
                    .Select(phi => new SsaPhi(
                        phi.ResultName,
                        phi.VariableName,
                        phi.Type,
                        CoalescePhiIncomings(
                            phi.Incomings
                                .Select(incoming => incoming.PredecessorBlockId == blockId
                                    ? new SsaPhiIncoming(predecessorBlockId, incoming.Value)
                                    : incoming)
                                .ToArray())))
                    .ToArray();

                return candidate with { Phis = phis };
            })
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static SsaFunction CanonicalizeEarlyReturnDiamonds(SsaFunction function)
    {
        var predecessors = BuildPredecessors(function.Blocks);
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var replacementsByPredecessor = new Dictionary<int, SsaTerminator>();

        foreach (var block in function.Blocks)
        {
            if (!TryBuildEarlyReturnDiamondReplacements(block, predecessors, byId, out var replacements))
            {
                continue;
            }

            foreach (var replacement in replacements)
            {
                replacementsByPredecessor[replacement.Key] = replacement.Value;
            }
        }

        if (replacementsByPredecessor.Count == 0)
        {
            return function;
        }

        var blocks = function.Blocks
            .Select(block => replacementsByPredecessor.TryGetValue(block.Id, out var replacement)
                ? block with { Terminator = replacement }
                : block)
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static SsaFunction PredicatizeSimpleReturnDiamonds(SsaFunction function)
    {
        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var usedNames = CollectDefinedValueNames(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                if (!TryPredicatizeSimpleReturnDiamond(block, byId, usedNames, out var replacement))
                {
                    return block;
                }

                changed = true;
                return replacement;
            })
            .ToArray();

        return changed ? function with { Blocks = blocks } : function;
    }

    private static bool TryPredicatizeSimpleReturnDiamond(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, SsaBasicBlock> byId,
        ISet<string> usedNames,
        out SsaBasicBlock replacement)
    {
        replacement = block;

        if (block.Terminator is not
            {
                Kind: SsaTerminatorKind.Branch,
                Condition: { Type.Kind: StarkTypeKind.Bool } condition,
                Targets.Count: 2
            }
            || block.Terminator.BranchWeights is { Count: > 0 }
            || block.Terminator.Targets[0] == block.Terminator.Targets[1]
            || !byId.TryGetValue(block.Terminator.Targets[0], out var trueBlock)
            || !byId.TryGetValue(block.Terminator.Targets[1], out var falseBlock)
            || !TryGetSimpleReturnValue(trueBlock, out var whenTrue)
            || !TryGetSimpleReturnValue(falseBlock, out var whenFalse)
            || whenTrue.Type != whenFalse.Type
            || !IsSelectFriendlyType(whenTrue.Type))
        {
            return false;
        }

        var selectName = CreateUniqueValueName(usedNames, $"select_{block.Id}");
        var select = new SsaValueInstruction(
            selectName,
            new SsaSelectRValue(
                condition,
                whenTrue,
                whenFalse,
                whenTrue.Type,
                "select"));

        replacement = block with
        {
            Instructions = block.Instructions.Concat([select]).ToArray(),
            Terminator = new SsaTerminator(
                SsaTerminatorKind.Return,
                [],
                Value: new SsaValueReference(selectName, whenTrue.Type),
                Location: block.Terminator.Location)
        };
        return true;
    }

    private static bool TryGetSimpleReturnValue(SsaBasicBlock block, out SsaValue value)
    {
        if (block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Return
            && block.Terminator.Value is { } returnValue)
        {
            value = returnValue;
            return true;
        }

        value = new SsaUndefValue(StarkTypeSymbols.Void);
        return false;
    }

    private static bool IsSelectFriendlyType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Void
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private static bool TryBuildEarlyReturnDiamondReplacements(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, List<int>> predecessors,
        IReadOnlyDictionary<int, SsaBasicBlock> byId,
        out Dictionary<int, SsaTerminator> replacements)
    {
        replacements = new Dictionary<int, SsaTerminator>();

        if (block.Terminator.Kind != SsaTerminatorKind.Return || block.Instructions.Count != 0)
        {
            return false;
        }

        var blockPredecessors = predecessors.GetValueOrDefault(block.Id, []);
        if (blockPredecessors.Count < 2)
        {
            return false;
        }

        foreach (var predecessorId in blockPredecessors)
        {
            if (!byId.TryGetValue(predecessorId, out var predecessor)
                || predecessor.Terminator.Kind != SsaTerminatorKind.Goto
                || predecessor.Terminator.Targets.Count != 1
                || predecessor.Terminator.Targets[0] != block.Id)
            {
                replacements.Clear();
                return false;
            }
        }

        foreach (var predecessorId in blockPredecessors)
        {
            var phiReplacements = BuildPhiIncomingReplacementMap(block, predecessorId);
            if (phiReplacements is null)
            {
                replacements.Clear();
                return false;
            }

            replacements[predecessorId] = new SsaTerminator(
                SsaTerminatorKind.Return,
                [],
                Value: block.Terminator.Value is null
                    ? null
                    : RewriteValue(block.Terminator.Value, phiReplacements));
        }

        return replacements.Count != 0;
    }

    private static Dictionary<string, SsaValue>? BuildPhiIncomingReplacementMap(
        SsaBasicBlock block,
        int predecessorBlockId)
    {
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);

        foreach (var phi in block.Phis)
        {
            var incoming = phi.Incomings.FirstOrDefault(incoming => incoming.PredecessorBlockId == predecessorBlockId);
            if (incoming is null)
            {
                return null;
            }

            replacements[phi.ResultName] = incoming.Value;
        }

        return replacements;
    }

    private static int ResolveCollapsedTarget(
        int blockId,
        IReadOnlyDictionary<int, int> redirects,
        Dictionary<int, int> cache)
    {
        if (cache.TryGetValue(blockId, out var resolved))
        {
            return resolved;
        }

        if (!redirects.TryGetValue(blockId, out var target))
        {
            cache[blockId] = blockId;
            return blockId;
        }

        resolved = ResolveCollapsedTarget(target, redirects, cache);
        cache[blockId] = resolved;
        return resolved;
    }

    private static int ResolveCollapsedPredecessor(
        int blockId,
        IReadOnlyDictionary<int, int> redirects,
        IReadOnlyDictionary<int, List<int>> predecessors,
        Dictionary<int, int> cache)
    {
        if (cache.TryGetValue(blockId, out var resolved))
        {
            return resolved;
        }

        if (!redirects.ContainsKey(blockId))
        {
            cache[blockId] = blockId;
            return blockId;
        }

        var blockPredecessors = predecessors.GetValueOrDefault(blockId, []);
        if (blockPredecessors.Count != 1)
        {
            cache[blockId] = blockId;
            return blockId;
        }

        resolved = ResolveCollapsedPredecessor(blockPredecessors[0], redirects, predecessors, cache);
        cache[blockId] = resolved;
        return resolved;
    }

    public SsaFunction PruneUnreachableBlocks(SsaFunction function)
    {
        if (!function.HasBody || function.Blocks.Count == 0)
        {
            return function;
        }

        var byId = function.Blocks.ToDictionary(static block => block.Id);
        var reachable = ComputeReachableBlocks(function.EntryBlockId, byId);
        var blocks = function.Blocks
            .Where(block => reachable.Contains(block.Id))
            .Select(block => new SsaBasicBlock(
                block.Id,
                block.Label,
                block.Phis
                    .Select(phi => new SsaPhi(
                        phi.ResultName,
                        phi.VariableName,
                        phi.Type,
                        CoalescePhiIncomings(
                            phi.Incomings
                                .Where(incoming => reachable.Contains(incoming.PredecessorBlockId))
                                .ToArray())))
                    .ToArray(),
                block.Instructions,
                block.Terminator))
            .ToArray();

        return function with { Blocks = blocks };
    }

    private static HashSet<int> ComputeReachableBlocks(
        int entryBlockId,
        IReadOnlyDictionary<int, SsaBasicBlock> blocks)
    {
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(entryBlockId);

        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current) || !blocks.TryGetValue(current, out var block))
            {
                continue;
            }

            foreach (var successor in GetSuccessors(block.Terminator))
            {
                if (blocks.ContainsKey(successor))
                {
                    pending.Push(successor);
                }
            }
        }

        return reachable;
    }

    private static IReadOnlyDictionary<int, List<int>> BuildPredecessors(IReadOnlyList<SsaBasicBlock> blocks)
    {
        var predecessors = blocks.ToDictionary(static block => block.Id, static _ => new List<int>());

        foreach (var block in blocks)
        {
            foreach (var successor in GetSuccessors(block.Terminator))
            {
                if (predecessors.TryGetValue(successor, out var list))
                {
                    list.Add(block.Id);
                }
            }
        }

        return predecessors;
    }

    private static IEnumerable<int> GetSuccessors(SsaTerminator terminator)
    {
        if (terminator.DefaultTarget is { } defaultTarget)
        {
            foreach (var target in terminator.Targets)
            {
                yield return target;
            }

            yield return defaultTarget;
            yield break;
        }

        foreach (var target in terminator.Targets)
        {
            yield return target;
        }
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue>? replacements)
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
        IReadOnlyDictionary<string, SsaValue>? replacements)
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
            SsaDynVTableSlotRValue vtableSlot => new SsaDynVTableSlotRValue(
                RewriteValue(vtableSlot.VtablePointer, replacements),
                vtableSlot.SlotIndex,
                vtableSlot.Type,
                vtableSlot.Text),
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
        IReadOnlyDictionary<string, SsaValue>? replacements)
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

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue>? replacements,
        Func<int, int>? resolveTarget = null)
    {
        resolveTarget ??= static target => target;

        return new SsaTerminator(
            terminator.Kind,
            terminator.Targets.Select(resolveTarget).ToArray(),
            Condition: terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
            Value: terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
            TailDirectCall: RewriteTailDirectCall(terminator.TailDirectCall, replacements),
            TailIndirectCall: RewriteTailIndirectCall(terminator.TailIndirectCall, replacements),
            SwitchCases: terminator.SwitchCases?.Select(switchCase => new SsaSwitchCase(
                switchCase.Label,
                resolveTarget(switchCase.TargetBlockId),
                RewriteValue(switchCase.MatchValue, replacements))).ToArray(),
            DefaultTarget: terminator.DefaultTarget is null
                ? null
                : resolveTarget(terminator.DefaultTarget.Value),
            Location: terminator.Location,
            BranchWeights: terminator.BranchWeights,
            LoopBehavior: terminator.LoopBehavior,
            LoopContracts: terminator.LoopContracts,
            LoopAccessGroups: terminator.LoopAccessGroups);
    }

    private static ISsaDirectCallOperation? RewriteTailDirectCall(
        ISsaDirectCallOperation? call,
        IReadOnlyDictionary<string, SsaValue>? replacements)
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
        IReadOnlyDictionary<string, SsaValue>? replacements)
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

    private static SsaValue RewriteValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue>? replacements)
    {
        if (replacements is null)
        {
            return value;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (value is SsaValueReference reference
            && replacements.TryGetValue(reference.Name, out var replacement)
            && seen.Add(reference.Name))
        {
            value = replacement;
        }

        return value;
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
