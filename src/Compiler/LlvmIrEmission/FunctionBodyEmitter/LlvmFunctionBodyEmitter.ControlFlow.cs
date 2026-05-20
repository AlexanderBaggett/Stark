using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitTerminator(SsaTerminator terminator)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Goto:
                AppendLine($"  br label %{FormatBlockLabel(terminator.Targets[0])}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Branch:
                if (terminator.Condition is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA branch is missing a condition.");
                }

                AppendLine(
                    $"  br i1 {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.Targets[0])}, label %{FormatBlockLabel(terminator.Targets[1])}{GetBranchPredictionMetadataSuffix(terminator)}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Switch:
                if (terminator.Condition is null || terminator.DefaultTarget is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA switch is missing its condition or default target.");
                }

                if (terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                {
                    AppendLine($"  br label %{FormatBlockLabel(terminator.DefaultTarget.Value)}");
                    return;
                }

                var switchCases = string.Join(
                    " ",
                    terminator.SwitchCases.Select(
                        switchCase => $"{MapType(switchCase.MatchValue.Type)} {FormatValue(switchCase.MatchValue)}, label %{FormatBlockLabel(switchCase.TargetBlockId)}"));

                AppendLine(
                    $"  switch {MapType(terminator.Condition.Type)} {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.DefaultTarget.Value)} [ {switchCases} ]{GetBranchPredictionMetadataSuffix(terminator)}{GetLoopMetadataSuffix(terminator)}");
                return;
            case SsaTerminatorKind.Return:
                if (_abiFunction.ReturnsIndirect)
                {
                    if (terminator.Value is null || _abiFunction.ReturnBufferParameter is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA aggregate return is missing its value or sret parameter.");
                    }

                    EmitValueToAddress(
                        $"%{EscapeIdentifier(_abiFunction.ReturnBufferParameter.LlvmName)}",
                        _function.ReturnType,
                        terminator.Value,
                        GetTypeAlignmentBytes(_function.ReturnType),
                        scopedNoAliasMetadataSuffix: GetScopedNoAliasMetadataSuffix(CreateScopedAliasParameterRootKey(_abiFunction.ReturnBufferParameter.SourceName)));
                    AppendLine("  ret void");
                    return;
                }

                if (_function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    AppendLine("  ret void");
                    return;
                }

                if (terminator.Value is null)
                {
                    throw new UnsupportedBodyEmissionException("SSA return is missing a return value.");
                }

                AppendLine($"  ret {MapType(_abiFunction.LlvmReturnType)} {FormatValue(terminator.Value)}");
                return;
            case SsaTerminatorKind.Unreachable:
                AppendLine($"  call {TrapCallingConventionPrefix()}void @{UnreachableTrapHelperName}()");
                AppendLine("  unreachable");
                return;
            default:
                throw new UnsupportedBodyEmissionException($"Unsupported SSA terminator '{terminator.Kind}'.");
        }
    }

    private string GetLoopMetadataSuffix(SsaTerminator terminator)
    {
        var hasMustProgress = string.Equals(terminator.LoopBehavior, "willexit", StringComparison.Ordinal);
        var loopAccessGroups = terminator.LoopAccessGroups;
        var hasParallelAccesses = loopAccessGroups is { Count: > 0 };
        if (!hasMustProgress && !hasParallelAccesses)
        {
            return string.Empty;
        }

        var loopMetadataItems = new List<string> { string.Empty };
        if (hasMustProgress)
        {
            loopMetadataItems.Add(_context.GetMetadataTupleRef(["!\"llvm.loop.mustprogress\""]));
        }

        if (loopAccessGroups is { Count: > 0 })
        {
            var parallelAccessItems = loopAccessGroups
                .Select(GetLoopAccessGroupRef)
                .Prepend("!\"llvm.loop.parallel_accesses\"")
                .ToArray();
            loopMetadataItems.Add(_context.GetMetadataTupleRef(parallelAccessItems));
        }

        var currentBlockId = _currentBlock?.Id.ToString(CultureInfo.InvariantCulture) ?? "?";
        var key = string.Join(
            "|",
            _abiFunction.SymbolName,
            currentBlockId,
            string.Join(",", terminator.Targets),
            terminator.LoopBehavior ?? string.Empty,
            string.Join(",", terminator.LoopContracts is null
                ? Array.Empty<string>()
                : terminator.LoopContracts.Order(StringComparer.Ordinal)),
            string.Join(",", terminator.LoopAccessGroups ?? []));
        var loopRef = _context.GetSelfReferentialMetadataRef(
            $"loop:{key}",
            selfRef =>
            {
                loopMetadataItems[0] = selfRef;
                return $"distinct !{{{string.Join(", ", loopMetadataItems)}}}";
            });
        return $", !llvm.loop {loopRef}";
    }

    private string GetLoopAccessGroupMetadataSuffix(IReadOnlyList<string>? loopAccessGroups)
    {
        if (loopAccessGroups is not { Count: > 0 })
        {
            return string.Empty;
        }

        var accessGroupRefs = loopAccessGroups
            .Select(GetLoopAccessGroupRef)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var accessGroupRef = accessGroupRefs.Length == 1
            ? accessGroupRefs[0]
            : _context.GetMetadataTupleRef(accessGroupRefs);
        return $", !llvm.access.group {accessGroupRef}";
    }

    private string GetLoopAccessGroupRef(string accessGroupId)
    {
        return _context.GetSelfReferentialMetadataRef(
            $"loop-access-group:{_abiFunction.SymbolName}:{accessGroupId}",
            _ => "distinct !{}");
    }

    private string GetBranchPredictionMetadataSuffix(SsaTerminator terminator)
    {
        var weights = GetBranchPredictionWeights(terminator);
        if (weights is null || weights.Count < 2)
        {
            return string.Empty;
        }

        var items = weights
            .Select(static weight => $"i32 {Math.Max(1, weight)}")
            .Prepend("!\"branch_weights\"")
            .ToArray();
        return $", !prof {_context.GetMetadataTupleRef(items)}";
    }

    private IReadOnlyList<int>? GetBranchPredictionWeights(SsaTerminator terminator)
    {
        return terminator.Kind switch
        {
            SsaTerminatorKind.Branch => GetBranchWeights(terminator),
            SsaTerminatorKind.Switch => GetSwitchWeights(terminator),
            _ => null
        };
    }

    private IReadOnlyList<int>? GetBranchWeights(SsaTerminator terminator)
    {
        if (terminator.BranchWeights is { Count: 2 } explicitWeights)
        {
            return explicitWeights;
        }

        if (terminator.Targets.Count != 2)
        {
            return null;
        }

        var trueTargetIsCold = IsColdBranchTarget(terminator.Targets[0]);
        var falseTargetIsCold = IsColdBranchTarget(terminator.Targets[1]);
        if (trueTargetIsCold == falseTargetIsCold)
        {
            var trueTargetIsHot = IsHotBranchTarget(terminator.Targets[0]);
            var falseTargetIsHot = IsHotBranchTarget(terminator.Targets[1]);
            if (trueTargetIsHot == falseTargetIsHot)
            {
                return null;
            }

            return trueTargetIsHot
                ? [HotIntentLikelyWeight, ExplicitIntentNeutralWeight]
                : [ExplicitIntentNeutralWeight, HotIntentLikelyWeight];
        }

        return trueTargetIsCold
            ? [TrapEdgeUnlikelyWeight, NormalEdgeLikelyWeight]
            : [NormalEdgeLikelyWeight, TrapEdgeUnlikelyWeight];
    }

    private IReadOnlyList<int>? GetSwitchWeights(SsaTerminator terminator)
    {
        if (terminator.SwitchCases is not { Count: > 0 } switchCases || terminator.DefaultTarget is null)
        {
            return null;
        }

        var expectedWeightCount = switchCases.Count + 1;
        if (terminator.BranchWeights is { } explicitWeights && explicitWeights.Count == expectedWeightCount)
        {
            return explicitWeights;
        }

        var weights = new int[expectedWeightCount];
        var sawNonNeutralWeight = false;

        AssignInferredWeight(0, terminator.DefaultTarget.Value);
        for (var index = 0; index < switchCases.Count; index++)
        {
            AssignInferredWeight(index + 1, switchCases[index].TargetBlockId);
        }

        return sawNonNeutralWeight ? weights : null;

        void AssignInferredWeight(int index, int targetBlockId)
        {
            weights[index] = IsColdBranchTarget(targetBlockId)
                ? TrapEdgeUnlikelyWeight
                : IsHotBranchTarget(targetBlockId)
                    ? HotIntentLikelyWeight
                    : ExplicitIntentNeutralWeight;
            sawNonNeutralWeight |= weights[index] != ExplicitIntentNeutralWeight;
        }
    }

    private bool IsColdBranchTarget(int targetBlockId)
    {
        return IsColdBranchTarget(targetBlockId, new HashSet<int>());
    }

    private bool IsColdBranchTarget(int targetBlockId, HashSet<int> visited)
    {
        if (!_blocksById.TryGetValue(targetBlockId, out var block) || !visited.Add(targetBlockId))
        {
            return false;
        }

        if (block.Terminator.Kind == SsaTerminatorKind.Unreachable)
        {
            return true;
        }

        if (ContainsCallWithTemperature(block, isCold: true))
        {
            return true;
        }

        if (block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Goto
            && block.Terminator.Targets.Count == 1)
        {
            return IsColdBranchTarget(block.Terminator.Targets[0], visited);
        }

        return false;
    }

    private bool IsHotBranchTarget(int targetBlockId)
    {
        return IsHotBranchTarget(targetBlockId, new HashSet<int>());
    }

    private bool IsHotBranchTarget(int targetBlockId, HashSet<int> visited)
    {
        if (!_blocksById.TryGetValue(targetBlockId, out var block) || !visited.Add(targetBlockId))
        {
            return false;
        }

        if (ContainsCallWithTemperature(block, isCold: false))
        {
            return true;
        }

        if (block.Phis.Count == 0
            && block.Instructions.Count == 0
            && block.Terminator.Kind == SsaTerminatorKind.Goto
            && block.Terminator.Targets.Count == 1)
        {
            return IsHotBranchTarget(block.Terminator.Targets[0], visited);
        }

        return false;
    }

    private bool ContainsCallWithTemperature(SsaBasicBlock block, bool isCold)
    {
        foreach (var instruction in block.Instructions)
        {
            var functionName = instruction switch
            {
                SsaValueInstruction { Value: SsaCallRValue call } => call.FunctionName,
                SsaCallInstruction call => call.FunctionName,
                _ => null
            };

            if (functionName is null)
            {
                continue;
            }

            var effects = _context.TryGetFunctionEffects(functionName);
            if (effects is null)
            {
                continue;
            }

            if (isCold ? effects.IsCold : effects.IsHot)
            {
                return true;
            }
        }

        return false;
    }

    private void EmitFallbackTerminal()
    {
        if (_abiFunction.ReturnsIndirect || _function.ReturnType.Kind == StarkTypeKind.Void)
        {
            AppendLine("  ret void");
            return;
        }

        throw new UnsupportedBodyEmissionException("SSA function body has no blocks.");
    }

    private static string FormatBlockLabel(int blockId) => $"bb{blockId}";

    private string FormatPhiIncomingBlockLabel(int blockId)
    {
        return _blockExitLabels.TryGetValue(blockId, out var label)
            ? label
            : FormatBlockLabel(blockId);
    }

    private void RecordCurrentBlockExitLabel(string label)
    {
        if (_currentBlock is not null)
        {
            _blockExitLabels[_currentBlock.Id] = label;
        }
    }
}
