using System.Globalization;
using System.Numerics;

namespace Stark.Compiler;

internal sealed class SsaCleanupOptimizer
{
    public SsaIrModule Optimize(SsaIrModule module)
    {
        return new SsaIrModule(
            module.ModuleName,
            module.Functions.Select(OptimizeFunction).ToArray());
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
            SsaCallRValue call => call.Arguments,
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
            SsaCallRValue call => new SsaCallRValue(
                call.FunctionName,
                call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray(),
                call.Type,
                call.Text,
                call.IndirectArgumentLocalNames,
                call.SourceReturnType),
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

internal sealed class SsaConstantPropagator
{
    private readonly SsaCleanupOptimizer _cleanupOptimizer = new();
    private static readonly IReadOnlyDictionary<string, ConstantState> EmptyConstantStates =
        new Dictionary<string, ConstantState>(StringComparer.Ordinal);
    private const int PropagationPassCount = 2;

    public SsaIrModule Optimize(SsaIrModule module)
    {
        return new SsaIrModule(
            module.ModuleName,
            module.Functions.Select(OptimizeFunction).ToArray());
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
        if (!TryFitSignedInteger(-integer.Value, integer.Type.BitWidth ?? 0, out var fitted))
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
        var signed = FromTwosComplement(inverted, bitWidth);
        folded = new SsaIntegerConstant(signed, integer.Type);
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
        if (TryFitSignedInteger(value, type.BitWidth ?? 0, out var fitted))
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
        var wrapped = FromTwosComplement(normalized, bitWidth);
        folded = new SsaIntegerConstant(wrapped, type);
        return true;
    }

    private static bool TryClampSignedInteger(StarkTypeSymbol type, BigInteger value, out SsaValue folded)
    {
        if (!TryGetSignedIntegerBounds(type.BitWidth ?? 0, out var min, out var max))
        {
            folded = new SsaIntegerConstant(value, type);
            return false;
        }

        var clamped = value < min ? min : value > max ? max : value;
        folded = new SsaIntegerConstant(clamped, type);
        return true;
    }

    private static bool TryFitSignedInteger(BigInteger value, int bitWidth, out BigInteger fitted)
    {
        fitted = value;
        if (!TryGetSignedIntegerBounds(bitWidth, out var min, out var max))
        {
            return false;
        }

        if (value < min || value > max)
        {
            return false;
        }

        return true;
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
            SsaCallRValue call => new SsaCallRValue(
                call.FunctionName,
                call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray(),
                call.Type,
                call.Text,
                call.IndirectArgumentLocalNames,
                call.SourceReturnType),
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
