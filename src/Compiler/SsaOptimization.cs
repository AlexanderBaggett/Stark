using System.Globalization;
using System.Numerics;
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
            module.Functions.Select(OptimizeFunction).ToArray(),
            module.AddressTakenFunctions);

        return SsaAddressTakenFunctionPruner.Prune(optimized);
    }

    public SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = CanonicalizeCompareAndBranchShapes(function);
        current = SimplifyTrivialTerminators(current);
        current = SimplifySingleCaseSwitches(current);
        current = NormalizeSwitchLoweringStructures(current);
        current = ReuseIdenticalMaterializedValues(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveUnusedPureInstructions(current);
        current = RemoveUnusedLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = CollapseTrampolineBlocks(current);
        current = MergeLinearBlocks(current);
        current = CanonicalizeEarlyReturnDiamonds(current);
        if (_enableSelectPredication)
        {
            current = PredicatizeSimpleReturnDiamonds(current);
        }

        current = PruneUnreachableBlocks(current);
        current = SimplifyTrivialTerminators(current);
        current = NormalizeSwitchLoweringStructures(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveUnusedPureInstructions(current);
        current = RemoveUnusedLocalStorage(current);
        current = RemoveUnusedPureInstructions(current);
        current = CollapseTrampolineBlocks(current);
        current = MergeLinearBlocks(current);
        current = CanonicalizeEarlyReturnDiamonds(current);
        if (_enableSelectPredication)
        {
            current = PredicatizeSimpleReturnDiamonds(current);
        }

        return PruneUnreachableBlocks(current);
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
            canonicalTerminator = new SsaTerminator(
                SsaTerminatorKind.Branch,
                [terminator.Targets[1], terminator.Targets[0]],
                Condition: logicalNot.Operand,
                Location: terminator.Location,
                BranchWeights: ReverseBranchWeights(terminator));
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

            canonicalTerminator = new SsaTerminator(
                SsaTerminatorKind.Branch,
                targets,
                Condition: branchCondition,
                Location: terminator.Location,
                BranchWeights: swapTargets ? ReverseBranchWeights(terminator) : terminator.BranchWeights);
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
                    new SsaTerminator(
                        SsaTerminatorKind.Branch,
                        [switchCase.TargetBlockId, block.Terminator.DefaultTarget.Value],
                        Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
                        Location: block.Terminator.Location,
                        BranchWeights: TryCreateSwitchCaseBranchWeights(block.Terminator, switchCase, trueTargetIsCase: true)));
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

        branchTerminator = new SsaTerminator(
            SsaTerminatorKind.Branch,
            [trueCase.TargetBlockId, falseCase.TargetBlockId],
            Condition: terminator.Condition,
            Location: terminator.Location,
            BranchWeights: TryCreateBoolSwitchBranchWeights(terminator, trueCase, falseCase));
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
                new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    [switchCase.TargetBlockId, falseTarget],
                    Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
                    Location: block.Terminator.Location,
                    BranchWeights: branchWeights)));

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
                return new SsaTerminator(SsaTerminatorKind.Goto, [terminator.Targets[0]]);
            case SsaTerminatorKind.Switch:
            {
                terminator = RemoveSwitchCasesThatMatchDefaultTarget(terminator);

                if ((terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                    && terminator.DefaultTarget is { } defaultTarget)
                {
                    return new SsaTerminator(SsaTerminatorKind.Goto, [defaultTarget]);
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
                    return new SsaTerminator(SsaTerminatorKind.Goto, [allTargets[0]]);
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

            branchTerminator = new SsaTerminator(
                SsaTerminatorKind.Branch,
                targets,
                Condition: terminator.Condition,
                Location: terminator.Location,
                BranchWeights: TryCreateSwitchCaseBranchWeights(terminator, switchCase, trueTargetIsCase: match.Value));
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
        return value is SsaCallRValue;
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
                return TryReplaceRightIdentity(left, right, IsOneIntegerConstant, out replacement);
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
        out SsaValue replacement)
    {
        return TryResolveAggregateFieldValueCore(
            RewriteValue(aggregate, replacements),
            fieldName,
            fieldIndex,
            fieldType,
            replacements,
            definitions,
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
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (aggregate)
        {
            case SsaZeroInitializerValue:
                replacement = CreateZeroValue(fieldType);
                return true;
            case SsaValueReference reference when seen.Add(reference.Name) && definitions.TryGetValue(reference.Name, out var definition):
                return TryResolveAggregateFieldValueFromDefinition(
                    definition,
                    fieldName,
                    fieldIndex,
                    fieldType,
                    replacements,
                    definitions,
                    seen,
                    out replacement);
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
                    seen,
                    out replacement);
            default:
                replacement = new SsaUndefValue(fieldType);
                return false;
        }
    }

    private static bool TryResolveAggregateIndexValue(
        SsaValue aggregate,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue replacement)
    {
        return TryResolveAggregateIndexValueCore(
            RewriteValue(aggregate, replacements),
            elementIndex,
            elementType,
            replacements,
            definitions,
            new HashSet<string>(StringComparer.Ordinal),
            out replacement);
    }

    private static bool TryResolveAggregateIndexValueCore(
        SsaValue aggregate,
        int elementIndex,
        StarkTypeSymbol elementType,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> seen,
        out SsaValue replacement)
    {
        switch (aggregate)
        {
            case SsaZeroInitializerValue:
                replacement = CreateZeroValue(elementType);
                return true;
            case SsaValueReference reference when seen.Add(reference.Name) && definitions.TryGetValue(reference.Name, out var definition):
                return TryResolveAggregateIndexValueFromDefinition(
                    definition,
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    seen,
                    out replacement);
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
                    seen,
                    out replacement);
            case SsaInsertFieldRValue insertField:
                return TryResolveAggregateIndexValueCore(
                    RewriteValue(insertField.Target, replacements),
                    elementIndex,
                    elementType,
                    replacements,
                    definitions,
                    seen,
                    out replacement);
            default:
                replacement = new SsaUndefValue(elementType);
                return false;
        }
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
        return value is not SsaCallRValue;
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
            SsaIndirectCallRValue indirectCall => [indirectCall.Target, .. indirectCall.Arguments],
            SsaConvertRValue convert => [convert.Operand],
            SsaExtractFieldRValue extractField => [extractField.Target],
            SsaInsertFieldRValue insertField => [insertField.Target, insertField.Value],
            SsaExtractIndexRValue extractIndex => [extractIndex.Target],
            SsaInsertIndexRValue insertIndex => [insertIndex.Target, insertIndex.Value],
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
                RewriteRValue(valueInstruction.Value, replacements)),
            SsaAllocateLocalInstruction allocateLocal => allocateLocal,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
            SsaStoreLocalInstruction storeLocal => new SsaStoreLocalInstruction(
                storeLocal.LocalName,
                storeLocal.LocalType,
                RewriteValue(storeLocal.Value, replacements)),
            SsaCopyMemoryInstruction copyMemory => new SsaCopyMemoryInstruction(
                RewriteValue(copyMemory.DestinationAddress, replacements),
                RewriteValue(copyMemory.SourceAddress, replacements),
                copyMemory.CopyType,
                copyMemory.TransferKind),
            SsaStoreIndirectInstruction storeIndirect => new SsaStoreIndirectInstruction(
                RewriteValue(storeIndirect.Address, replacements),
                storeIndirect.ValueType,
                RewriteValue(storeIndirect.Value, replacements)),
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
                extractIndex.Type,
                extractIndex.Text),
            SsaInsertIndexRValue insertIndex => new SsaInsertIndexRValue(
                RewriteValue(insertIndex.Target, replacements),
                insertIndex.ElementIndex,
                RewriteValue(insertIndex.Value, replacements),
                insertIndex.Type,
                insertIndex.Text),
            SsaMakeSliceFromLocalRValue makeSlice => makeSlice,
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
            indirectCall.SourceReturnType);
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
            SwitchCases: terminator.SwitchCases?.Select(switchCase => new SsaSwitchCase(
                switchCase.Label,
                resolveTarget(switchCase.TargetBlockId),
                RewriteValue(switchCase.MatchValue, replacements))).ToArray(),
            DefaultTarget: terminator.DefaultTarget is null
                ? null
                : resolveTarget(terminator.DefaultTarget.Value),
            Location: terminator.Location,
            BranchWeights: terminator.BranchWeights);
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

internal sealed class SsaAliasAwareMemoryOptimizer
{
    private readonly FunctionEffectModel? _effectModel;

    public SsaAliasAwareMemoryOptimizer(FunctionEffectModel? effectModel = null)
    {
        _effectModel = effectModel;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    public SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var eligibleLocals = CollectEligibleStackScalarLocals(function);
        var definitions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);

        var predecessors = BuildPredecessorMap(function);
        var exitKnownLocalsByBlock = new Dictionary<int, IReadOnlyDictionary<string, SsaValue>>();
        var exitKnownGlobalsByBlock =
            new Dictionary<int, IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>>();
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            var entryKnownLocals = TryGetSinglePredecessorExitKnownLocals(
                block,
                predecessors,
                exitKnownLocalsByBlock);
            var entryKnownGlobals = TryGetSinglePredecessorExitKnownGlobals(
                block,
                predecessors,
                exitKnownGlobalsByBlock);
            blocks.Add(OptimizeBlock(
                block,
                eligibleLocals,
                definitions,
                entryKnownLocals,
                entryKnownGlobals,
                ref changed,
                out var exitKnownLocals,
                out var exitKnownGlobals));
            exitKnownLocalsByBlock[block.Id] = exitKnownLocals;
            exitKnownGlobalsByBlock[block.Id] = exitKnownGlobals;
        }

        var forwardedFunction = changed
            ? function with { Blocks = blocks.ToArray() }
            : function;
        var deadStoreOptimizedFunction = EliminateDeadStackScalarStores(
            forwardedFunction,
            eligibleLocals,
            out var deadStoreChanged);

        return deadStoreChanged ? deadStoreOptimizedFunction : forwardedFunction;
    }

    private SsaBasicBlock OptimizeBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<string, SsaValue> entryKnownLocals,
        IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> entryKnownGlobals,
        ref bool changed,
        out IReadOnlyDictionary<string, SsaValue> exitKnownLocals,
        out IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> exitKnownGlobals)
    {
        var blockChanged = false;
        var knownLocals = new Dictionary<string, SsaValue>(entryKnownLocals, StringComparer.Ordinal);
        var knownGlobals = new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>(entryKnownGlobals);
        var pendingStoreInstructionIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingGlobalStoreInstructionIndexes =
            new Dictionary<(string GlobalName, StarkTypeSymbol Type), int>();
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var instructions = new List<SsaInstruction?>(block.Instructions.Count);

        foreach (var instruction in block.Instructions)
        {
            var rewritten = RewriteInstruction(instruction, replacements);
            if (!EqualityComparer<SsaInstruction>.Default.Equals(rewritten, instruction))
            {
                blockChanged = true;
            }

            switch (rewritten)
            {
                case SsaValueInstruction
                {
                    Value: SsaLoadLocalRValue loadLocal
                } valueInstruction
                    when eligibleLocals.TryGetValue(loadLocal.LocalName, out var localType)
                         && localType == loadLocal.Type
                         && knownLocals.TryGetValue(loadLocal.LocalName, out var knownValue)
                         && knownValue.Type == loadLocal.Type:
                    replacements[valueInstruction.ResultName] = RewriteValue(knownValue, replacements);
                    blockChanged = true;
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadGlobalRValue loadGlobal
                } valueInstruction
                    when IsForwardableScalarMemoryType(loadGlobal.Type)
                         && knownGlobals.TryGetValue((loadGlobal.GlobalName, loadGlobal.Type), out var knownValue)
                         && knownValue.Type == loadGlobal.Type:
                    replacements[valueInstruction.ResultName] = RewriteValue(knownValue, replacements);
                    blockChanged = true;
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadGlobalRValue loadGlobal
                } valueInstruction:
                    if (IsForwardableScalarMemoryType(loadGlobal.Type))
                    {
                        knownGlobals[(loadGlobal.GlobalName, loadGlobal.Type)] =
                            new SsaValueReference(valueInstruction.ResultName, loadGlobal.Type);
                    }
                    else
                    {
                        RemoveKnownGlobal(knownGlobals, loadGlobal.GlobalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction
                {
                    Value: SsaLoadLocalRValue loadLocal
                }:
                    pendingStoreInstructionIndexes.Remove(loadLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction { Value: SsaCallRValue call }:
                    if (MayWriteGlobalMemory(call))
                    {
                        knownGlobals.Clear();
                        pendingGlobalStoreInstructionIndexes.Clear();
                    }
                    else if (MayReadGlobalMemory(call, definitions))
                    {
                        pendingGlobalStoreInstructionIndexes.Clear();
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaValueInstruction { Value: SsaIndirectCallRValue }:
                    knownGlobals.Clear();
                    pendingGlobalStoreInstructionIndexes.Clear();
                    instructions.Add(rewritten);
                    continue;

                case SsaStoreLocalInstruction storeLocal:
                    if (eligibleLocals.TryGetValue(storeLocal.LocalName, out var storeLocalType)
                        && storeLocalType == storeLocal.LocalType
                        && storeLocal.Value.Type == storeLocal.LocalType)
                    {
                        knownLocals[storeLocal.LocalName] = RewriteValue(storeLocal.Value, replacements);
                        if (pendingStoreInstructionIndexes.TryGetValue(storeLocal.LocalName, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingStoreInstructionIndexes[storeLocal.LocalName] = instructions.Count;
                    }
                    else
                    {
                        knownLocals.Remove(storeLocal.LocalName);
                        pendingStoreInstructionIndexes.Remove(storeLocal.LocalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaStoreGlobalInstruction storeGlobal:
                    if (IsForwardableScalarMemoryType(storeGlobal.GlobalType)
                        && storeGlobal.Value.Type == storeGlobal.GlobalType)
                    {
                        var globalKey = (storeGlobal.GlobalName, storeGlobal.GlobalType);
                        knownGlobals[globalKey] =
                            RewriteValue(storeGlobal.Value, replacements);
                        if (pendingGlobalStoreInstructionIndexes.TryGetValue(globalKey, out var pendingStoreIndex))
                        {
                            instructions[pendingStoreIndex] = null;
                            blockChanged = true;
                        }

                        pendingGlobalStoreInstructionIndexes[globalKey] = instructions.Count;
                    }
                    else
                    {
                        RemoveKnownGlobal(knownGlobals, storeGlobal.GlobalName);
                        RemovePendingGlobalStore(pendingGlobalStoreInstructionIndexes, storeGlobal.GlobalName);
                    }

                    instructions.Add(rewritten);
                    continue;

                case SsaStoreIndirectInstruction:
                case SsaCopyMemoryInstruction:
                    knownGlobals.Clear();
                    pendingGlobalStoreInstructionIndexes.Clear();
                    instructions.Add(rewritten);
                    continue;

                case SsaAllocateLocalInstruction allocateLocal:
                    knownLocals.Remove(allocateLocal.LocalName);
                    pendingStoreInstructionIndexes.Remove(allocateLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaLifetimeEndInstruction lifetimeEnd:
                    knownLocals.Remove(lifetimeEnd.LocalName);
                    pendingStoreInstructionIndexes.Remove(lifetimeEnd.LocalName);
                    instructions.Add(rewritten);
                    continue;

                case SsaDeallocateLocalInstruction deallocateLocal:
                    knownLocals.Remove(deallocateLocal.LocalName);
                    pendingStoreInstructionIndexes.Remove(deallocateLocal.LocalName);
                    instructions.Add(rewritten);
                    continue;

                default:
                    instructions.Add(rewritten);
                    continue;
            }
        }

        var rewrittenTerminator = RewriteTerminator(block.Terminator, replacements);
        if (!EqualityComparer<SsaTerminator>.Default.Equals(rewrittenTerminator, block.Terminator))
        {
            blockChanged = true;
        }

        exitKnownLocals = new Dictionary<string, SsaValue>(knownLocals, StringComparer.Ordinal);
        exitKnownGlobals = new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>(knownGlobals);
        if (!blockChanged)
        {
            return block;
        }

        changed = true;
        return block with
        {
            Instructions = instructions
                .Where(static instruction => instruction is not null)
                .Cast<SsaInstruction>()
                .ToArray(),
            Terminator = rewrittenTerminator
        };
    }

    private static SsaFunction EliminateDeadStackScalarStores(
        SsaFunction function,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        out bool changed)
    {
        var liveOutByBlock = ComputeEligibleLocalLiveOut(function, eligibleLocals);
        changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            var liveOut = liveOutByBlock.TryGetValue(block.Id, out var blockLiveOut)
                ? blockLiveOut
                : new HashSet<string>(StringComparer.Ordinal);
            blocks.Add(EliminateDeadStackScalarStoresInBlock(
                block,
                eligibleLocals,
                liveOut,
                ref changed));
        }

        return changed ? function with { Blocks = blocks.ToArray() } : function;
    }

    private static SsaBasicBlock EliminateDeadStackScalarStoresInBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        IReadOnlySet<string> liveOut,
        ref bool changed)
    {
        var live = new HashSet<string>(liveOut, StringComparer.Ordinal);
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        var blockChanged = false;

        for (var index = block.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = block.Instructions[index];
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }
                    when eligibleLocals.ContainsKey(loadLocal.LocalName):
                    live.Add(loadLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaStoreLocalInstruction storeLocal
                    when IsEligibleStackScalarStore(storeLocal, eligibleLocals):
                    if (!live.Contains(storeLocal.LocalName))
                    {
                        blockChanged = true;
                        continue;
                    }

                    live.Remove(storeLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaAllocateLocalInstruction allocateLocal
                    when IsEligibleStackScalarLocal(allocateLocal.LocalName, allocateLocal.LocalType, eligibleLocals):
                    live.Remove(allocateLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaLifetimeEndInstruction lifetimeEnd
                    when eligibleLocals.ContainsKey(lifetimeEnd.LocalName):
                    live.Remove(lifetimeEnd.LocalName);
                    instructions.Add(instruction);
                    continue;

                case SsaDeallocateLocalInstruction deallocateLocal
                    when eligibleLocals.ContainsKey(deallocateLocal.LocalName):
                    live.Remove(deallocateLocal.LocalName);
                    instructions.Add(instruction);
                    continue;

                default:
                    instructions.Add(instruction);
                    continue;
            }
        }

        if (!blockChanged)
        {
            return block;
        }

        instructions.Reverse();
        changed = true;
        return block with { Instructions = instructions.ToArray() };
    }

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> ComputeEligibleLocalLiveOut(
        SsaFunction function,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
    {
        var successors = function.Blocks.ToDictionary(
            static block => block.Id,
            block => GetSuccessorBlockIds(block.Terminator).ToArray());
        var useByBlock = new Dictionary<int, HashSet<string>>();
        var defByBlock = new Dictionary<int, HashSet<string>>();
        var liveInByBlock = new Dictionary<int, HashSet<string>>();
        var liveOutByBlock = new Dictionary<int, HashSet<string>>();

        foreach (var block in function.Blocks)
        {
            CollectEligibleLocalUseDef(block, eligibleLocals, out var use, out var def);
            useByBlock[block.Id] = use;
            defByBlock[block.Id] = def;
            liveInByBlock[block.Id] = new HashSet<string>(StringComparer.Ordinal);
            liveOutByBlock[block.Id] = new HashSet<string>(StringComparer.Ordinal);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = function.Blocks.Count - 1; index >= 0; index--)
            {
                var block = function.Blocks[index];
                var newLiveOut = new HashSet<string>(StringComparer.Ordinal);
                if (successors.TryGetValue(block.Id, out var blockSuccessors))
                {
                    foreach (var successor in blockSuccessors)
                    {
                        if (liveInByBlock.TryGetValue(successor, out var successorLiveIn))
                        {
                            newLiveOut.UnionWith(successorLiveIn);
                        }
                    }
                }

                var newLiveIn = new HashSet<string>(newLiveOut, StringComparer.Ordinal);
                newLiveIn.ExceptWith(defByBlock[block.Id]);
                newLiveIn.UnionWith(useByBlock[block.Id]);

                if (!liveOutByBlock[block.Id].SetEquals(newLiveOut))
                {
                    liveOutByBlock[block.Id] = newLiveOut;
                    changed = true;
                }

                if (!liveInByBlock[block.Id].SetEquals(newLiveIn))
                {
                    liveInByBlock[block.Id] = newLiveIn;
                    changed = true;
                }
            }
        }

        return liveOutByBlock.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<string>)pair.Value);
    }

    private static void CollectEligibleLocalUseDef(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals,
        out HashSet<string> use,
        out HashSet<string> def)
    {
        use = new HashSet<string>(StringComparer.Ordinal);
        def = new HashSet<string>(StringComparer.Ordinal);
        var definedInBlock = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case SsaValueInstruction { Value: SsaLoadLocalRValue loadLocal }
                    when eligibleLocals.ContainsKey(loadLocal.LocalName):
                    if (!definedInBlock.Contains(loadLocal.LocalName))
                    {
                        use.Add(loadLocal.LocalName);
                    }

                    break;

                case SsaStoreLocalInstruction storeLocal
                    when IsEligibleStackScalarStore(storeLocal, eligibleLocals):
                    definedInBlock.Add(storeLocal.LocalName);
                    def.Add(storeLocal.LocalName);
                    break;

                case SsaAllocateLocalInstruction allocateLocal
                    when IsEligibleStackScalarLocal(allocateLocal.LocalName, allocateLocal.LocalType, eligibleLocals):
                    definedInBlock.Add(allocateLocal.LocalName);
                    def.Add(allocateLocal.LocalName);
                    break;

                case SsaLifetimeEndInstruction lifetimeEnd
                    when eligibleLocals.ContainsKey(lifetimeEnd.LocalName):
                    definedInBlock.Add(lifetimeEnd.LocalName);
                    def.Add(lifetimeEnd.LocalName);
                    break;

                case SsaDeallocateLocalInstruction deallocateLocal
                    when eligibleLocals.ContainsKey(deallocateLocal.LocalName):
                    definedInBlock.Add(deallocateLocal.LocalName);
                    def.Add(deallocateLocal.LocalName);
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, SsaValue> TryGetSinglePredecessorExitKnownLocals(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValue>> exitKnownLocalsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitKnownLocalsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitKnownLocals)
            ? predecessorExitKnownLocals
            : new Dictionary<string, SsaValue>(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>
        TryGetSinglePredecessorExitKnownGlobals(
            SsaBasicBlock block,
            IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
            IReadOnlyDictionary<int, IReadOnlyDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>>
                exitKnownGlobalsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitKnownGlobalsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitKnownGlobals)
            ? predecessorExitKnownGlobals
            : new Dictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue>();
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> BuildPredecessorMap(SsaFunction function)
    {
        var predecessors = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new List<int>(),
            EqualityComparer<int>.Default);

        foreach (var block in function.Blocks)
        {
            foreach (var successor in GetSuccessorBlockIds(block.Terminator))
            {
                if (predecessors.TryGetValue(successor, out var successorPredecessors))
                {
                    successorPredecessors.Add(block.Id);
                }
            }
        }

        return predecessors.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<int>)pair.Value.ToArray());
    }

    private static IEnumerable<int> GetSuccessorBlockIds(SsaTerminator terminator)
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

    private static IReadOnlyDictionary<string, StarkTypeSymbol> CollectEligibleStackScalarLocals(SsaFunction function)
    {
        var escapedLocals = CollectEscapedLocalNames(function);
        var candidates = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            if (instruction is not SsaAllocateLocalInstruction allocateLocal)
            {
                continue;
            }

            if (string.Equals(allocateLocal.StorageClass, "stack", StringComparison.Ordinal)
                && !escapedLocals.Contains(allocateLocal.LocalName)
                && IsForwardableScalarMemoryType(allocateLocal.LocalType))
            {
                candidates[allocateLocal.LocalName] = allocateLocal.LocalType;
                continue;
            }

            candidates.Remove(allocateLocal.LocalName);
        }

        return candidates;
    }

    private static HashSet<string> CollectEscapedLocalNames(SsaFunction function)
    {
        var escaped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    AddEscapedLocalNames(valueInstruction.Value, escaped);
                    break;
            }
        }

        return escaped;
    }

    private static void AddEscapedLocalNames(SsaRValue value, ISet<string> escaped)
    {
        switch (value)
        {
            case SsaAddressOfLocalRValue addressOfLocal:
                escaped.Add(addressOfLocal.LocalName);
                break;

            case SsaMakeSliceFromLocalRValue makeSlice:
                escaped.Add(makeSlice.LocalName);
                break;

            case SsaCallRValue { IndirectArgumentLocalNames: { } indirectLocals }:
                foreach (var localName in indirectLocals)
                {
                    if (localName is not null)
                    {
                        escaped.Add(localName);
                    }
                }

                break;
        }
    }

    private static bool IsForwardableScalarMemoryType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
    }

    private bool MayWriteGlobalMemory(SsaCallRValue call)
    {
        return _effectModel is not { } effectModel
               || !effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
               || !effects.IsPure
               || !effects.NoSync;
    }

    private bool MayReadGlobalMemory(
        SsaCallRValue call,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return _effectModel is not { } effectModel
               || !effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
               || !effects.IsPure
               || !effects.NoSync
               || effects.ReadsArgumentMemory && EnumerateCallMemoryArguments(call).Any(argument =>
                   ValueMayReferenceGlobalMemory(
                       argument,
                       definitions,
                       new HashSet<string>(StringComparer.Ordinal)));
    }

    private static bool ValueMayReferenceGlobalMemory(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaValueReference reference
                when visitedValueNames.Add(reference.Name)
                     && definitions.TryGetValue(reference.Name, out var definition) =>
                RValueMayReferenceGlobalMemory(definition, definitions, visitedValueNames),
            SsaValueReference reference when IsPotentialMemoryReferenceType(reference.Type) => true,
            _ => false
        };
    }

    private static bool RValueMayReferenceGlobalMemory(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaUseRValue use => ValueMayReferenceGlobalMemory(use.Value, definitions, visitedValueNames),
            SsaSelectRValue select => ValueMayReferenceGlobalMemory(select.WhenTrue, definitions, visitedValueNames)
                                      || ValueMayReferenceGlobalMemory(select.WhenFalse, definitions, visitedValueNames),
            SsaConvertRValue convert => ValueMayReferenceGlobalMemory(convert.Operand, definitions, visitedValueNames),
            SsaFieldAddressRValue fieldAddress => ValueMayReferenceGlobalMemory(fieldAddress.Address, definitions, visitedValueNames),
            SsaElementAddressRValue elementAddress => ValueMayReferenceGlobalMemory(elementAddress.Address, definitions, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => ValueMayReferenceGlobalMemory(sliceElementAddress.Slice, definitions, visitedValueNames),
            SsaTextSliceRValue textSlice => ValueMayReferenceGlobalMemory(textSlice.TextValue, definitions, visitedValueNames),
            _ => false
        };
    }

    private static IEnumerable<SsaValue> EnumerateCallMemoryArguments(SsaCallRValue call)
    {
        foreach (var argument in call.Arguments)
        {
            yield return argument;
        }

        foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
        {
            yield return address;
        }
    }

    private static bool IsPotentialMemoryReferenceType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.RawPointer
            or StarkTypeKind.Slice
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    private static void RemoveKnownGlobal(
        IDictionary<(string GlobalName, StarkTypeSymbol Type), SsaValue> knownGlobals,
        string globalName)
    {
        foreach (var key in knownGlobals.Keys
            .Where(key => string.Equals(key.GlobalName, globalName, StringComparison.Ordinal))
            .ToArray())
        {
            knownGlobals.Remove(key);
        }
    }

    private static void RemovePendingGlobalStore(
        IDictionary<(string GlobalName, StarkTypeSymbol Type), int> pendingGlobalStoreInstructionIndexes,
        string globalName)
    {
        foreach (var key in pendingGlobalStoreInstructionIndexes.Keys
            .Where(key => string.Equals(key.GlobalName, globalName, StringComparison.Ordinal))
            .ToArray())
        {
            pendingGlobalStoreInstructionIndexes.Remove(key);
        }
    }

    private static bool IsEligibleStackScalarStore(
        SsaStoreLocalInstruction storeLocal,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
    {
        return IsEligibleStackScalarLocal(storeLocal.LocalName, storeLocal.LocalType, eligibleLocals)
               && storeLocal.Value.Type == storeLocal.LocalType;
    }

    private static bool IsEligibleStackScalarLocal(
        string localName,
        StarkTypeSymbol localType,
        IReadOnlyDictionary<string, StarkTypeSymbol> eligibleLocals)
    {
        return eligibleLocals.TryGetValue(localName, out var eligibleType)
               && eligibleType == localType;
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction with
            {
                Value = RewriteRValue(valueInstruction.Value, replacements)
            },
            SsaStoreLocalInstruction storeLocal => storeLocal with
            {
                Value = RewriteValue(storeLocal.Value, replacements)
            },
            SsaCopyMemoryInstruction copyMemory => copyMemory with
            {
                DestinationAddress = RewriteValue(copyMemory.DestinationAddress, replacements),
                SourceAddress = RewriteValue(copyMemory.SourceAddress, replacements)
            },
            SsaStoreIndirectInstruction storeIndirect => storeIndirect with
            {
                Address = RewriteValue(storeIndirect.Address, replacements),
                Value = RewriteValue(storeIndirect.Value, replacements)
            },
            SsaStoreGlobalInstruction storeGlobal => storeGlobal with
            {
                Value = RewriteValue(storeGlobal.Value, replacements)
            },
            _ => instruction
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return value switch
        {
            SsaUseRValue use => use with
            {
                Value = RewriteValue(use.Value, replacements)
            },
            SsaUnaryRValue unary => unary with
            {
                Operand = RewriteValue(unary.Operand, replacements)
            },
            SsaBinaryRValue binary => binary with
            {
                Left = RewriteValue(binary.Left, replacements),
                Right = RewriteValue(binary.Right, replacements)
            },
            SsaSelectRValue select => select with
            {
                Condition = RewriteValue(select.Condition, replacements),
                WhenTrue = RewriteValue(select.WhenTrue, replacements),
                WhenFalse = RewriteValue(select.WhenFalse, replacements)
            },
            SsaCallRValue call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaIndirectCallRValue indirectCall => indirectCall with
            {
                Target = RewriteValue(indirectCall.Target, replacements),
                Arguments = indirectCall.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray()
            },
            SsaConvertRValue convert => convert with
            {
                Operand = RewriteValue(convert.Operand, replacements)
            },
            SsaExtractFieldRValue extractField => extractField with
            {
                Target = RewriteValue(extractField.Target, replacements)
            },
            SsaInsertFieldRValue insertField => insertField with
            {
                Target = RewriteValue(insertField.Target, replacements),
                Value = RewriteValue(insertField.Value, replacements)
            },
            SsaExtractIndexRValue extractIndex => extractIndex with
            {
                Target = RewriteValue(extractIndex.Target, replacements)
            },
            SsaInsertIndexRValue insertIndex => insertIndex with
            {
                Target = RewriteValue(insertIndex.Target, replacements),
                Value = RewriteValue(insertIndex.Value, replacements)
            },
            SsaLoadSliceElementRValue loadSlice => loadSlice with
            {
                Slice = RewriteValue(loadSlice.Slice, replacements),
                Index = RewriteValue(loadSlice.Index, replacements)
            },
            SsaTextSliceRValue textSlice => textSlice with
            {
                TextValue = RewriteValue(textSlice.TextValue, replacements),
                Start = RewriteValue(textSlice.Start, replacements),
                Length = RewriteValue(textSlice.Length, replacements)
            },
            SsaFieldAddressRValue fieldAddress => fieldAddress with
            {
                Address = RewriteValue(fieldAddress.Address, replacements)
            },
            SsaElementAddressRValue elementAddress => elementAddress with
            {
                Address = RewriteValue(elementAddress.Address, replacements),
                Index = elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements)
            },
            SsaSliceElementAddressRValue sliceElementAddress => sliceElementAddress with
            {
                Slice = RewriteValue(sliceElementAddress.Slice, replacements),
                Index = RewriteValue(sliceElementAddress.Index, replacements)
            },
            SsaLoadIndirectRValue loadIndirect => loadIndirect with
            {
                Address = RewriteValue(loadIndirect.Address, replacements)
            },
            _ => value
        };
    }

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return terminator with
        {
            Condition = terminator.Condition is null
                ? null
                : RewriteValue(terminator.Condition, replacements),
            Value = terminator.Value is null
                ? null
                : RewriteValue(terminator.Value, replacements),
            SwitchCases = terminator.SwitchCases?
                .Select(switchCase => switchCase with
                {
                    MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                })
                .ToArray()
        };
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
}

internal sealed class SsaDirectCallDevirtualizer
{
    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        var optimizedModule = changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;

        return SsaAddressTakenFunctionPruner.Prune(optimizedModule);
    }

    private static SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var instructions = block.Instructions
                    .Select(instruction =>
                    {
                        var optimized = OptimizeInstruction(instruction);
                        changed |= !ReferenceEquals(optimized, instruction);
                        return optimized;
                    })
                    .ToArray();

                return changed
                    ? block with { Instructions = instructions }
                    : block;
            })
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private static SsaInstruction OptimizeInstruction(SsaInstruction instruction)
    {
        return instruction is SsaValueInstruction valueInstruction
               && TryDevirtualizeDirectFunctionAddressCall(valueInstruction.Value, out var directCall)
            ? valueInstruction with { Value = directCall }
            : instruction;
    }

    private static bool TryDevirtualizeDirectFunctionAddressCall(
        SsaRValue value,
        out SsaCallRValue directCall)
    {
        directCall = default!;

        if (value is not SsaIndirectCallRValue
            {
                Target: SsaFunctionAddressValue functionAddress
            } indirectCall)
        {
            return false;
        }

        directCall = new SsaCallRValue(
            functionAddress.FunctionName,
            indirectCall.Arguments,
            indirectCall.Type,
            indirectCall.Text,
            SourceReturnType: indirectCall.SourceReturnType);
        return true;
    }
}

internal sealed class SsaValueFactAnalyzer
{
    public SsaValueFactModel Analyze(SsaIrModule module)
    {
        var functions = module.Functions
            .Where(static function => function.HasBody && function.SupportsDirectCodeGeneration)
            .Select(function => AnalyzeFunction(module.ModuleName, function))
            .ToDictionary(static function => function.FunctionName, StringComparer.Ordinal);

        return new SsaValueFactModel(module.ModuleName, functions);
    }

    private static SsaFunctionFactModel AnalyzeFunction(string moduleName, SsaFunction function)
    {
        var values = new Dictionary<string, SsaValueFacts>(StringComparer.Ordinal);

        foreach (var parameter in function.Parameters)
        {
            values[$"arg_{parameter.Name}"] = CreateTypeFacts($"arg_{parameter.Name}", parameter.Type);
        }

        foreach (var block in function.Blocks)
        {
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

        RefineFacts(moduleName, function, values);
        var blockEntryFacts = AnalyzeBlockEntryFacts(function, values);
        return new SsaFunctionFactModel(function.Name, values, blockEntryFacts);
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>> AnalyzeBlockEntryFacts(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaValueFacts> values)
    {
        var definitions = CollectValueDefinitions(function);
        var incomingFacts = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new List<IReadOnlyDictionary<string, SsaValueFacts>>());

        foreach (var block in function.Blocks)
        {
            foreach (var target in EnumerateTerminatorTargets(block.Terminator))
            {
                if (!incomingFacts.TryGetValue(target, out var edges))
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
            case SsaIntegerConstant integer:
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
        Dictionary<string, SsaValueFacts> values)
    {
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            foreach (var phi in function.Blocks.SelectMany(static block => block.Phis))
            {
                var incomingFacts = phi.Incomings
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
                         .SelectMany(static block => block.Instructions)
                         .OfType<SsaValueInstruction>())
            {
                var analyzed = AnalyzeRValue(
                    valueInstruction.ResultName,
                    valueInstruction.Value,
                    values,
                    moduleName);
                if (!EqualityComparer<SsaValueFacts>.Default.Equals(values[valueInstruction.ResultName], analyzed))
                {
                    values[valueInstruction.ResultName] = analyzed;
                    changed = true;
                }
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
        string moduleName)
    {
        return value switch
        {
            SsaUseRValue use => RenameFacts(valueName, AnalyzeValue(valueName, use.Value, knownValues), value.Type),
            SsaUnaryRValue unary => AnalyzeUnary(valueName, unary, knownValues),
            SsaBinaryRValue binary => AnalyzeBinary(valueName, binary, knownValues),
            SsaSelectRValue select => AnalyzeSelect(valueName, select, knownValues),
            SsaConvertRValue convert => AnalyzeConvert(valueName, convert, knownValues),
            SsaExtractFieldRValue extractField => AnalyzeExtractField(valueName, extractField, knownValues),
            SsaCallRValue call => AnalyzeCall(valueName, call, knownValues, moduleName),
            SsaAddressOfLocalRValue addressOfLocal => CreateAddressFacts(valueName, addressOfLocal.Type, addressOfLocal.PointeeType),
            SsaAddressOfParameterRValue addressOfParameter => CreateAddressFacts(valueName, addressOfParameter.Type, addressOfParameter.PointeeType),
            SsaFieldAddressRValue fieldAddress => AnalyzeDerivedPointerAddress(valueName, fieldAddress.Type, fieldAddress.Address, knownValues),
            SsaElementAddressRValue elementAddress => AnalyzeDerivedPointerAddress(valueName, elementAddress.Type, elementAddress.Address, knownValues),
            SsaSliceElementAddressRValue sliceElementAddress => CreateNonNullFacts(valueName, sliceElementAddress.Type),
            SsaMakeSliceFromLocalRValue makeSlice => AnalyzeMakeSlice(valueName, makeSlice),
            SsaTextSliceRValue textSlice => AnalyzeTextSlice(valueName, textSlice, knownValues),
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
            SsaIntegerConstant integer => CreateIntegerConstantFacts(valueName, integer.Type, integer.Value),
            SsaBoolConstant boolean => CreateBooleanConstantFacts(valueName, boolean.Value),
            SsaNullConstant nullConstant => CreateNullFacts(valueName, nullConstant.Type),
            SsaStringConstant text => CreateTextConstantFacts(valueName, text.Type, text.LiteralText),
            SsaGlobalAddressValue globalAddress => CreateAddressFacts(valueName, globalAddress.Type, globalAddress.PointeeType),
            SsaFunctionAddressValue functionAddress => CreateNonNullFacts(valueName, functionAddress.Type),
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
        if (left.KnownBitsKind != SsaFactLatticeKind.Known
            || right.KnownBitsKind != SsaFactLatticeKind.Known
            || left.KnownBits is not { } leftBits
            || right.KnownBits is not { } rightBits
            || !TryGetIntegerBitDomain(binary.Type, out var bitWidth, out var mask, out _))
        {
            return false;
        }

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

        return knownBits is not null;
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
            var facts = CreateIntegerRangeFacts(valueName, convert.TargetType, ClampRange(range, targetRange));
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
            return TryNormalizePointerAlignment(convert.TargetType, operand.PointerAlignmentBytes, out var alignmentBytes)
                ? WithPointerAlignment(facts, alignmentBytes)
                : facts;
        }

        return CreateTypeFacts(valueName, convert.TargetType);
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

        return CreateTypeFacts(valueName, extractField.Type);
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

    private static SsaValueFacts AnalyzeTextSlice(
        string valueName,
        SsaTextSliceRValue textSlice,
        IReadOnlyDictionary<string, SsaValueFacts> knownValues)
    {
        var length = AnalyzeValue(valueName, textSlice.Length, knownValues);
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

        return joined;
    }

    private static SsaValueFacts RenameFacts(string valueName, SsaValueFacts facts, StarkTypeSymbol type)
    {
        return facts with
        {
            ValueName = valueName,
            Type = type
        };
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
            updated = updated with
            {
                IntegerRangeKind = SsaFactLatticeKind.Known,
                IntegerRange = new SsaIntegerRangeFact(value, value)
            };
        }

        return updated;
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

        var length = type.Kind == StarkTypeKind.Unicode
            ? decoded.Utf32CodeUnits.Length
            : decoded.Utf8Bytes.Length;

        return facts with
        {
            LengthKind = SsaFactLatticeKind.Known,
            LengthRange = new SsaIntegerRangeFact(length, length)
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

    private static bool TryGetSystemTextLengthFunction(
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
        return new SsaIntegerRangeFact(
            Max(range.Min, bounds.Min),
            Min(range.Max, bounds.Max));
    }

    private static bool TryGetIntegerTypeRange(StarkTypeSymbol type, out SsaIntegerRangeFact range)
    {
        range = default!;
        if (type.Kind != StarkTypeKind.Integer || type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            return false;
        }

        if (type.RangeMin is not null && type.RangeMax is not null)
        {
            range = new SsaIntegerRangeFact(type.RangeMin.Value, type.RangeMax.Value);
            return true;
        }

        if (type.IsUnsigned)
        {
            range = new SsaIntegerRangeFact(BigInteger.Zero, (BigInteger.One << bitWidth) - BigInteger.One);
            return true;
        }

        range = new SsaIntegerRangeFact(
            -(BigInteger.One << (bitWidth - 1)),
            (BigInteger.One << (bitWidth - 1)) - BigInteger.One);
        return true;
    }

    private static BigInteger Min(BigInteger left, BigInteger right) => left <= right ? left : right;

    private static BigInteger Max(BigInteger left, BigInteger right) => left >= right ? left : right;
}

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
            return new SsaTerminator(
                SsaTerminatorKind.Goto,
                [terminator.Targets[condition ? 0 : 1]],
                Location: terminator.Location);
        }

        if (terminator.Kind == SsaTerminatorKind.Switch
            && terminator.Condition is not null
            && terminator.DefaultTarget is int defaultTarget
            && terminator.SwitchCases is { Count: > 0 } switchCases)
        {
            if (TryGetSwitchSingleton(terminator.Condition, facts, out var singleton)
                && TryResolveSwitchTarget(singleton, switchCases, defaultTarget, out var targetBlockId))
            {
                return new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    [targetBlockId],
                    Location: terminator.Location);
            }

            if (TryGetIntegerRangeFact(terminator.Condition, facts, out var range))
            {
                var filteredCases = switchCases
                    .Where(switchCase => !IsIntegerCaseOutsideRange(switchCase, range))
                    .ToArray();

                if (filteredCases.Length == 0)
                {
                    return new SsaTerminator(
                        SsaTerminatorKind.Goto,
                        [defaultTarget],
                        Location: terminator.Location);
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

                    return new SsaTerminator(
                        SsaTerminatorKind.Switch,
                        filteredCases
                            .Select(static switchCase => switchCase.TargetBlockId)
                            .Distinct()
                            .ToArray(),
                        Condition: terminator.Condition,
                        SwitchCases: filteredCases,
                        DefaultTarget: defaultTarget,
                        Location: terminator.Location);
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

        return new SsaTerminator(
            SsaTerminatorKind.Branch,
            [switchCase.TargetBlockId, defaultTarget],
            Condition: new SsaValueReference(conditionName, StarkTypeSymbols.Bool),
            Location: terminator.Location);
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

internal sealed class SsaDirectCallInliner
{
    private const int MaxOrdinaryInlineInstructionCount = 12;
    private const int MaxLawInlineInstructionCount = 20;
    private const int MaxInlineSitesPerFunction = 64;
    private const int MaxInlineRounds = 4;

    private readonly FunctionEffectModel _effectModel;
    private readonly IReadOnlySet<string> _modulePrivateFunctionNames;
    private readonly IReadOnlySet<string> _declaredLawFunctionNames;

    public SsaDirectCallInliner(
        FunctionEffectModel effectModel,
        IReadOnlySet<string> modulePrivateFunctionNames,
        IReadOnlySet<string> declaredLawFunctionNames)
    {
        _effectModel = effectModel;
        _modulePrivateFunctionNames = modulePrivateFunctionNames;
        _declaredLawFunctionNames = declaredLawFunctionNames;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var candidates = RemoveRecursiveCandidates(CollectCandidates(module));
        if (candidates.Count == 0)
        {
            return module;
        }

        var current = module;
        var changedAny = false;

        for (var round = 0; round < MaxInlineRounds; round++)
        {
            var changedRound = false;
            var functions = current.Functions
                .Select(function =>
                {
                    var optimized = InlineFunction(function, candidates);
                    changedRound |= !ReferenceEquals(optimized, function);
                    return optimized;
                })
                .ToArray();

            if (!changedRound)
            {
                break;
            }

            changedAny = true;
            current = new SsaIrModule(current.ModuleName, functions, current.AddressTakenFunctionRecords);
        }

        return changedAny
            ? current
            : module;
    }

    private IReadOnlyDictionary<string, InlineCandidate> CollectCandidates(SsaIrModule module)
    {
        var candidates = new Dictionary<string, InlineCandidate>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            if (!TryBuildCandidate(function, out var candidate))
            {
                continue;
            }

            candidates[function.Name] = candidate;
        }

        return candidates;
    }

    private static IReadOnlyDictionary<string, InlineCandidate> RemoveRecursiveCandidates(
        IReadOnlyDictionary<string, InlineCandidate> candidates)
    {
        var result = new Dictionary<string, InlineCandidate>(candidates, StringComparer.Ordinal);

        foreach (var candidate in candidates.Values)
        {
            if (CanReachCandidate(candidate.Function.Name, candidate.Function.Name, candidates, []))
            {
                result.Remove(candidate.Function.Name);
            }
        }

        return result;
    }

    private static bool CanReachCandidate(
        string originFunctionName,
        string currentFunctionName,
        IReadOnlyDictionary<string, InlineCandidate> candidates,
        HashSet<string> visited)
    {
        if (!candidates.TryGetValue(currentFunctionName, out var candidate))
        {
            return false;
        }

        foreach (var callee in candidate.DirectCalls)
        {
            if (string.Equals(callee, originFunctionName, StringComparison.Ordinal))
            {
                return true;
            }

            if (visited.Add(callee)
                && CanReachCandidate(originFunctionName, callee, candidates, visited))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildCandidate(SsaFunction function, out InlineCandidate candidate)
    {
        candidate = default!;

        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || !_effectModel.Functions.TryGetValue(function.Name, out var effects)
            || effects.IsFfi
            || effects.IsCold
            || effects.InlinePreference == InlinePreference.NoInline
            || !IsInlineCandidateByPolicy(function, effects)
            || !IsInlineSafeType(function.ReturnType)
            || function.Parameters.Any(static parameter => !IsInlineSafeType(parameter.Type))
            || function.Blocks.Count != 1)
        {
            return false;
        }

        var block = function.Blocks[0];
        var isDeclaredLaw = _declaredLawFunctionNames.Contains(function.Name)
            || FunctionKindFacts.IsLaw(effects.Kind);
        var maxInlineInstructionCount = GetMaxInlineInstructionCount(isDeclaredLaw);
        if (block.Id != function.EntryBlockId
            || block.Phis.Count != 0
            || block.Terminator.Kind != SsaTerminatorKind.Return
            || block.Terminator.Value is not { } returnValue
            || block.Instructions.Count > maxInlineInstructionCount)
        {
            return false;
        }

        var instructions = new List<SsaValueInstruction>(block.Instructions.Count);
        var directCalls = new List<string>();
        foreach (var instruction in block.Instructions)
        {
            if (instruction is not SsaValueInstruction valueInstruction
                || !IsInlineSafeType(valueInstruction.Value.Type)
                || !IsInlineSafeRValue(valueInstruction.Value, function.Name))
            {
                return false;
            }

            if (valueInstruction.Value is SsaCallRValue call)
            {
                directCalls.Add(call.FunctionName);
            }

            instructions.Add(valueInstruction);
        }

        if (!IsInlineSafeValue(returnValue))
        {
            return false;
        }

        candidate = new InlineCandidate(function, instructions, returnValue, directCalls);
        return true;
    }

    private bool IsInlineCandidateByPolicy(
        SsaFunction function,
        FunctionEffectProfile effects)
    {
        return effects.InlinePreference == InlinePreference.Inline
            || _modulePrivateFunctionNames.Contains(function.Name)
            || _declaredLawFunctionNames.Contains(function.Name);
    }

    private static int GetMaxInlineInstructionCount(bool isDeclaredLaw)
    {
        return isDeclaredLaw
            ? MaxLawInlineInstructionCount
            : MaxOrdinaryInlineInstructionCount;
    }

    private SsaFunction InlineFunction(
        SsaFunction function,
        IReadOnlyDictionary<string, InlineCandidate> candidates)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var usedValueNames = CollectDefinedValueNames(function);
        var inlineSiteIndex = 0;
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            var instructions = new List<SsaInstruction>(block.Instructions.Count);

            foreach (var instruction in block.Instructions)
            {
                var rewrittenInstruction = RewriteInstruction(instruction, replacements);

                if (inlineSiteIndex < MaxInlineSitesPerFunction
                    && rewrittenInstruction is SsaValueInstruction
                    {
                        Value: SsaCallRValue call
                    } valueInstruction
                    && TryInlineCall(
                        function,
                        valueInstruction,
                        call,
                        candidates,
                        inlineSiteIndex,
                        usedValueNames,
                        out var clonedInstructions,
                        out var replacement))
                {
                    instructions.AddRange(clonedInstructions);
                    replacements[valueInstruction.ResultName] = replacement;
                    inlineSiteIndex++;
                    changed = true;
                    continue;
                }

                instructions.Add(rewrittenInstruction);
            }

            blocks.Add(block with { Instructions = instructions });
        }

        if (!changed)
        {
            return function;
        }

        var rewrittenBlocks = blocks
            .Select(block => RewriteBlock(block, replacements))
            .ToArray();

        return function with { Blocks = rewrittenBlocks };
    }

    private static bool TryInlineCall(
        SsaFunction caller,
        SsaValueInstruction callInstruction,
        SsaCallRValue call,
        IReadOnlyDictionary<string, InlineCandidate> candidates,
        int inlineSiteIndex,
        ISet<string> usedValueNames,
        out IReadOnlyList<SsaInstruction> clonedInstructions,
        out SsaValue replacement)
    {
        clonedInstructions = [];
        replacement = default!;

        if (!candidates.TryGetValue(call.FunctionName, out var candidate)
            || string.Equals(candidate.Function.Name, caller.Name, StringComparison.Ordinal)
            || candidate.Function.Parameters.Count != call.Arguments.Count
            || call.IndirectArgumentLocalNames?.Any(static name => name is not null) == true
            || call.IndirectArgumentAddresses?.Any(static address => address is not null) == true)
        {
            return false;
        }

        var localReplacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        for (var index = 0; index < candidate.Function.Parameters.Count; index++)
        {
            var parameter = candidate.Function.Parameters[index];
            localReplacements[$"arg_{parameter.Name}"] = call.Arguments[index];
        }

        var clones = new List<SsaInstruction>(candidate.Instructions.Count);
        foreach (var candidateInstruction in candidate.Instructions)
        {
            var rewrittenValue = RewriteRValue(candidateInstruction.Value, localReplacements);
            var resultName = CreateFreshName(
                $"{candidateInstruction.ResultName}_inl{inlineSiteIndex}",
                usedValueNames);

            clones.Add(new SsaValueInstruction(
                resultName,
                rewrittenValue,
                callInstruction.Location ?? candidateInstruction.Location));

            localReplacements[candidateInstruction.ResultName] = new SsaValueReference(
                resultName,
                rewrittenValue.Type);
        }

        replacement = RewriteValue(candidate.ReturnValue, localReplacements);
        clonedInstructions = clones;
        return true;
    }

    private static HashSet<string> CollectDefinedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

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

    private static string CreateFreshName(string baseName, ISet<string> usedValueNames)
    {
        if (usedValueNames.Add(baseName))
        {
            return baseName;
        }

        var suffix = 1;
        while (true)
        {
            var candidate = $"{baseName}_{suffix}";
            if (usedValueNames.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static SsaBasicBlock RewriteBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return block with
        {
            Phis = block.Phis
                .Select(phi => phi with
                {
                    Incomings = phi.Incomings
                        .Select(incoming => incoming with
                        {
                            Value = RewriteValue(incoming.Value, replacements)
                        })
                        .ToArray()
                })
                .ToArray(),
            Instructions = block.Instructions
                .Select(instruction => RewriteInstruction(instruction, replacements))
                .ToArray(),
            Terminator = RewriteTerminator(block.Terminator, replacements)
        };
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction with
            {
                Value = RewriteRValue(valueInstruction.Value, replacements)
            },
            SsaAllocateLocalInstruction allocateLocal => allocateLocal,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
            SsaStoreLocalInstruction storeLocal => storeLocal with
            {
                Value = RewriteValue(storeLocal.Value, replacements)
            },
            SsaCopyMemoryInstruction copyMemory => copyMemory with
            {
                DestinationAddress = RewriteValue(copyMemory.DestinationAddress, replacements),
                SourceAddress = RewriteValue(copyMemory.SourceAddress, replacements)
            },
            SsaStoreIndirectInstruction storeIndirect => storeIndirect with
            {
                Address = RewriteValue(storeIndirect.Address, replacements),
                Value = RewriteValue(storeIndirect.Value, replacements)
            },
            SsaStoreGlobalInstruction storeGlobal => storeGlobal with
            {
                Value = RewriteValue(storeGlobal.Value, replacements)
            },
            _ => instruction
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return value switch
        {
            SsaUseRValue use => use with
            {
                Value = RewriteValue(use.Value, replacements)
            },
            SsaUnaryRValue unary => unary with
            {
                Operand = RewriteValue(unary.Operand, replacements)
            },
            SsaBinaryRValue binary => binary with
            {
                Left = RewriteValue(binary.Left, replacements),
                Right = RewriteValue(binary.Right, replacements)
            },
            SsaSelectRValue select => select with
            {
                Condition = RewriteValue(select.Condition, replacements),
                WhenTrue = RewriteValue(select.WhenTrue, replacements),
                WhenFalse = RewriteValue(select.WhenFalse, replacements)
            },
            SsaCallRValue call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentAddresses = call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : RewriteValue(address, replacements))
                    .ToArray()
            },
            SsaIndirectCallRValue indirectCall => RewriteIndirectCallRValue(indirectCall, replacements),
            SsaConvertRValue convert => convert with
            {
                Operand = RewriteValue(convert.Operand, replacements)
            },
            SsaExtractFieldRValue extractField => extractField with
            {
                Target = RewriteValue(extractField.Target, replacements)
            },
            SsaInsertFieldRValue insertField => insertField with
            {
                Target = RewriteValue(insertField.Target, replacements),
                Value = RewriteValue(insertField.Value, replacements)
            },
            SsaExtractIndexRValue extractIndex => extractIndex with
            {
                Target = RewriteValue(extractIndex.Target, replacements)
            },
            SsaInsertIndexRValue insertIndex => insertIndex with
            {
                Target = RewriteValue(insertIndex.Target, replacements),
                Value = RewriteValue(insertIndex.Value, replacements)
            },
            SsaLoadSliceElementRValue loadSlice => loadSlice with
            {
                Slice = RewriteValue(loadSlice.Slice, replacements),
                Index = RewriteValue(loadSlice.Index, replacements)
            },
            SsaTextSliceRValue textSlice => textSlice with
            {
                TextValue = RewriteValue(textSlice.TextValue, replacements),
                Start = RewriteValue(textSlice.Start, replacements),
                Length = RewriteValue(textSlice.Length, replacements)
            },
            SsaFieldAddressRValue fieldAddress => fieldAddress with
            {
                Address = RewriteValue(fieldAddress.Address, replacements)
            },
            SsaElementAddressRValue elementAddress => elementAddress with
            {
                Address = RewriteValue(elementAddress.Address, replacements),
                Index = elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements)
            },
            SsaSliceElementAddressRValue sliceElementAddress => sliceElementAddress with
            {
                Slice = RewriteValue(sliceElementAddress.Slice, replacements),
                Index = RewriteValue(sliceElementAddress.Index, replacements)
            },
            SsaLoadIndirectRValue loadIndirect => loadIndirect with
            {
                Address = RewriteValue(loadIndirect.Address, replacements)
            },
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

        return target is SsaFunctionAddressValue functionAddress
            ? new SsaCallRValue(
                functionAddress.FunctionName,
                arguments,
                indirectCall.Type,
                indirectCall.Text,
                SourceReturnType: indirectCall.SourceReturnType)
            : indirectCall with
            {
                Target = target,
                Arguments = arguments
            };
    }

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return terminator with
        {
            Condition = terminator.Condition is null
                ? null
                : RewriteValue(terminator.Condition, replacements),
            Value = terminator.Value is null
                ? null
                : RewriteValue(terminator.Value, replacements),
            SwitchCases = terminator.SwitchCases?
                .Select(switchCase => switchCase with
                {
                    MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                })
                .ToArray()
        };
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

    private static bool IsInlineSafeRValue(SsaRValue value, string ownerFunctionName)
    {
        return value switch
        {
            SsaUseRValue use => IsInlineSafeValue(use.Value),
            SsaUnaryRValue unary => IsInlineSafeValue(unary.Operand),
            SsaBinaryRValue binary => IsInlineSafeValue(binary.Left)
                                      && IsInlineSafeValue(binary.Right),
            SsaSelectRValue select => IsInlineSafeValue(select.Condition)
                                      && IsInlineSafeValue(select.WhenTrue)
                                      && IsInlineSafeValue(select.WhenFalse),
            SsaCallRValue call => !string.Equals(call.FunctionName, ownerFunctionName, StringComparison.Ordinal)
                                  && IsInlineSafeType(call.Type)
                                  && call.Arguments.All(IsInlineSafeValue)
                                  && call.IndirectArgumentLocalNames?.Any(static name => name is not null) != true
                                  && call.IndirectArgumentAddresses?.Any(static address => address is not null) != true,
            SsaConvertRValue convert => IsInlineSafeValue(convert.Operand),
            _ => false
        };
    }

    private static bool IsInlineSafeValue(SsaValue value)
    {
        return value is SsaValueReference
            or SsaIntegerConstant
            or SsaFloatConstant
            or SsaStringConstant
            or SsaBoolConstant
            or SsaNullConstant
            or SsaGlobalAddressValue
            or SsaFunctionAddressValue
            or SsaUndefValue
            or SsaZeroInitializerValue;
    }

    private static bool IsInlineSafeType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer
            or StarkTypeKind.Null;
    }

    private sealed record InlineCandidate(
        SsaFunction Function,
        IReadOnlyList<SsaValueInstruction> Instructions,
        SsaValue ReturnValue,
        IReadOnlyList<string> DirectCalls);
}

internal static class SsaAddressTakenFunctionPruner
{
    public static SsaIrModule Prune(SsaIrModule module)
    {
        if (module.AddressTakenFunctions.Count == 0)
        {
            return module;
        }

        var referencedFunctions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            AddReferencedFunctionAddresses(function, referencedFunctions);
        }

        var prunedFunctions = module.AddressTakenFunctions
            .Where(referencedFunctions.Contains)
            .ToArray();

        return prunedFunctions.Length == module.AddressTakenFunctions.Count
            ? module
            : module with { AddressTakenFunctionRecords = prunedFunctions };
    }

    private static void AddReferencedFunctionAddresses(
        SsaFunction function,
        HashSet<string> referencedFunctions)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    AddReferencedFunctionAddress(incoming.Value, referencedFunctions);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                AddReferencedFunctionAddresses(instruction, referencedFunctions);
            }

            AddReferencedFunctionAddresses(block.Terminator, referencedFunctions);
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaInstruction instruction,
        HashSet<string> referencedFunctions)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                AddReferencedFunctionAddresses(valueInstruction.Value, referencedFunctions);
                break;
            case SsaStoreLocalInstruction storeLocal:
                AddReferencedFunctionAddress(storeLocal.Value, referencedFunctions);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                AddReferencedFunctionAddress(storeIndirect.Address, referencedFunctions);
                AddReferencedFunctionAddress(storeIndirect.Value, referencedFunctions);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                AddReferencedFunctionAddress(copyMemory.DestinationAddress, referencedFunctions);
                AddReferencedFunctionAddress(copyMemory.SourceAddress, referencedFunctions);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                AddReferencedFunctionAddress(storeGlobal.Value, referencedFunctions);
                break;
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaRValue value,
        HashSet<string> referencedFunctions)
    {
        switch (value)
        {
            case SsaUseRValue use:
                AddReferencedFunctionAddress(use.Value, referencedFunctions);
                break;
            case SsaUnaryRValue unary:
                AddReferencedFunctionAddress(unary.Operand, referencedFunctions);
                break;
            case SsaBinaryRValue binary:
                AddReferencedFunctionAddress(binary.Left, referencedFunctions);
                AddReferencedFunctionAddress(binary.Right, referencedFunctions);
                break;
            case SsaSelectRValue select:
                AddReferencedFunctionAddress(select.Condition, referencedFunctions);
                AddReferencedFunctionAddress(select.WhenTrue, referencedFunctions);
                AddReferencedFunctionAddress(select.WhenFalse, referencedFunctions);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddReferencedFunctionAddress(argument, referencedFunctions);
                }

                if (call.IndirectArgumentAddresses is not null)
                {
                    foreach (var address in call.IndirectArgumentAddresses)
                    {
                        if (address is not null)
                        {
                            AddReferencedFunctionAddress(address, referencedFunctions);
                        }
                    }
                }

                break;
            case SsaIndirectCallRValue indirectCall:
                AddReferencedFunctionAddress(indirectCall.Target, referencedFunctions);

                foreach (var argument in indirectCall.Arguments)
                {
                    AddReferencedFunctionAddress(argument, referencedFunctions);
                }

                break;
            case SsaConvertRValue convert:
                AddReferencedFunctionAddress(convert.Operand, referencedFunctions);
                break;
            case SsaExtractFieldRValue extractField:
                AddReferencedFunctionAddress(extractField.Target, referencedFunctions);
                break;
            case SsaInsertFieldRValue insertField:
                AddReferencedFunctionAddress(insertField.Target, referencedFunctions);
                AddReferencedFunctionAddress(insertField.Value, referencedFunctions);
                break;
            case SsaExtractIndexRValue extractIndex:
                AddReferencedFunctionAddress(extractIndex.Target, referencedFunctions);
                break;
            case SsaInsertIndexRValue insertIndex:
                AddReferencedFunctionAddress(insertIndex.Target, referencedFunctions);
                AddReferencedFunctionAddress(insertIndex.Value, referencedFunctions);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                AddReferencedFunctionAddress(loadSlice.Slice, referencedFunctions);
                AddReferencedFunctionAddress(loadSlice.Index, referencedFunctions);
                break;
            case SsaTextSliceRValue textSlice:
                AddReferencedFunctionAddress(textSlice.TextValue, referencedFunctions);
                AddReferencedFunctionAddress(textSlice.Start, referencedFunctions);
                AddReferencedFunctionAddress(textSlice.Length, referencedFunctions);
                break;
            case SsaFieldAddressRValue fieldAddress:
                AddReferencedFunctionAddress(fieldAddress.Address, referencedFunctions);
                break;
            case SsaElementAddressRValue elementAddress:
                AddReferencedFunctionAddress(elementAddress.Address, referencedFunctions);

                if (elementAddress.Index is not null)
                {
                    AddReferencedFunctionAddress(elementAddress.Index, referencedFunctions);
                }

                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                AddReferencedFunctionAddress(sliceElementAddress.Slice, referencedFunctions);
                AddReferencedFunctionAddress(sliceElementAddress.Index, referencedFunctions);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                AddReferencedFunctionAddress(loadIndirect.Address, referencedFunctions);
                break;
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaTerminator terminator,
        HashSet<string> referencedFunctions)
    {
        if (terminator.Condition is not null)
        {
            AddReferencedFunctionAddress(terminator.Condition, referencedFunctions);
        }

        if (terminator.Value is not null)
        {
            AddReferencedFunctionAddress(terminator.Value, referencedFunctions);
        }

        if (terminator.SwitchCases is null)
        {
            return;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            AddReferencedFunctionAddress(switchCase.MatchValue, referencedFunctions);
        }
    }

    private static void AddReferencedFunctionAddress(
        SsaValue value,
        HashSet<string> referencedFunctions)
    {
        if (value is SsaFunctionAddressValue functionAddress)
        {
            referencedFunctions.Add(functionAddress.FunctionName);
        }
    }
}

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
            module.Functions.Select(OptimizeFunction).ToArray(),
            module.AddressTakenFunctions);

        return SsaAddressTakenFunctionPruner.Prune(optimized);
    }

    public SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = function;

        for (var iteration = 0; iteration < PropagationPassCount; iteration++)
        {
            current = OptimizeFunctionCore(current);
        }

        return current;
    }

    private SsaFunction OptimizeFunctionCore(SsaFunction function)
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
                        var state = EvaluateRValue(instruction.Value, newStates);
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
        IReadOnlyDictionary<string, ConstantState> states)
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
        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            min = BigInteger.Zero;
            max = BigInteger.Zero;
            return false;
        }

        if (type.IsUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << bitWidth) - BigInteger.One;
            return true;
        }

        return TryGetSignedIntegerBounds(bitWidth, out min, out max);
    }

    private static bool TryGetSignedIntegerBounds(int bitWidth, out BigInteger min, out BigInteger max)
    {
        min = BigInteger.Zero;
        max = BigInteger.Zero;
        if (bitWidth <= 0)
        {
            return false;
        }

        min = -(BigInteger.One << (bitWidth - 1));
        max = (BigInteger.One << (bitWidth - 1)) - 1;
        return true;
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
                RewriteRValue(valueInstruction.Value, replacements)),
            SsaAllocateLocalInstruction allocateLocal => allocateLocal,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
            SsaStoreLocalInstruction storeLocal => new SsaStoreLocalInstruction(
                storeLocal.LocalName,
                storeLocal.LocalType,
                RewriteValue(storeLocal.Value, replacements)),
            SsaCopyMemoryInstruction copyMemory => new SsaCopyMemoryInstruction(
                RewriteValue(copyMemory.DestinationAddress, replacements),
                RewriteValue(copyMemory.SourceAddress, replacements),
                copyMemory.CopyType,
                copyMemory.TransferKind),
            SsaStoreIndirectInstruction storeIndirect => new SsaStoreIndirectInstruction(
                RewriteValue(storeIndirect.Address, replacements),
                storeIndirect.ValueType,
                RewriteValue(storeIndirect.Value, replacements)),
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
                extractIndex.Type,
                extractIndex.Text),
            SsaInsertIndexRValue insertIndex => new SsaInsertIndexRValue(
                RewriteValue(insertIndex.Target, replacements),
                insertIndex.ElementIndex,
                RewriteValue(insertIndex.Value, replacements),
                insertIndex.Type,
                insertIndex.Text),
            SsaMakeSliceFromLocalRValue makeSlice => makeSlice,
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
            indirectCall.SourceReturnType);
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
            return new SsaTerminator(
                SsaTerminatorKind.Goto,
                [terminator.Targets[branchCondition ? 0 : 1]]);
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
            return new SsaTerminator(SsaTerminatorKind.Goto, [targetBlockId]);
        }

        return new SsaTerminator(
            terminator.Kind,
            terminator.Targets,
            Condition: rewrittenCondition,
            Value: rewrittenValue,
            SwitchCases: rewrittenCases,
            DefaultTarget: terminator.DefaultTarget,
            Location: terminator.Location,
            BranchWeights: terminator.BranchWeights);
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
