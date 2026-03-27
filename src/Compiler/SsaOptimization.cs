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
        current = ReuseIdenticalMaterializedValues(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveUnusedPureInstructions(current);
        current = CollapseTrampolineBlocks(current);
        current = PruneUnreachableBlocks(current);
        current = RewriteTrivialCopiesAndIdentityPhis(current);
        current = RemoveUnusedPureInstructions(current);
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
                Condition: logicalNot.Operand);
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
                Condition: branchCondition);
            return true;
        }

        return false;
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
                        memoryVersion++;
                        break;
                }
            }
        }

        return replacements.Count == 0
            ? function
            : ApplyReplacements(function, replacements);
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
            case SsaAddressOfLocalRValue addressOfLocal:
                key = $"address-of-local|{addressOfLocal.LocalName}|{TypeKey(addressOfLocal.PointeeType)}|{TypeKey(addressOfLocal.Type)}";
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
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var block in blocks)
            {
                foreach (var phi in block.Phis)
                {
                    if (replacements.ContainsKey(phi.ResultName))
                    {
                        continue;
                    }

                    var rewrittenIncomings = phi.Incomings
                        .Select(incoming => RewriteValue(incoming.Value, replacements))
                        .ToArray();

                    if (TryFindIdentityValue(phi.ResultName, rewrittenIncomings, out var identityValue))
                    {
                        replacements[phi.ResultName] = identityValue!;
                        changed = true;
                    }
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
                    }
                }
            }
        }

        return replacements;
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

        var first = values[0];
        if (values.Any(value => !EqualityComparer<SsaValue>.Default.Equals(value, first)))
        {
            return false;
        }

        if (first is SsaValueReference reference
            && string.Equals(reference.Name, resultName, StringComparison.Ordinal))
        {
            return false;
        }

        identityValue = first;
        return true;
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
                call.IndirectArgumentLocalNames),
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
            SsaAddressOfLocalRValue addressOfLocal => addressOfLocal,
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
                : resolveTarget(terminator.DefaultTarget.Value));
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

    private static bool TryFitSignedInteger(BigInteger value, int bitWidth, out BigInteger fitted)
    {
        fitted = value;
        if (bitWidth <= 0)
        {
            return false;
        }

        var min = -(BigInteger.One << (bitWidth - 1));
        var max = (BigInteger.One << (bitWidth - 1)) - 1;
        if (value < min || value > max)
        {
            return false;
        }

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
                call.IndirectArgumentLocalNames),
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
            SsaAddressOfLocalRValue addressOfLocal => addressOfLocal,
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
                    DefaultTarget: terminator.DefaultTarget),
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
            DefaultTarget: terminator.DefaultTarget);
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
