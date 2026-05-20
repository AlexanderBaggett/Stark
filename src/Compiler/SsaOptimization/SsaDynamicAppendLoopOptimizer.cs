namespace Stark.Compiler;

internal sealed class SsaDynamicAppendLoopOptimizer
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

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private static SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var current = function;
        var usedNames = CollectUsedValueNames(current);
        var changed = false;

        while (TryOptimizeOneLoop(current, usedNames, out var optimized))
        {
            current = optimized;
            usedNames = CollectUsedValueNames(current);
            changed = true;
        }

        return changed ? current : function;
    }

    private static bool TryOptimizeOneLoop(
        SsaFunction function,
        HashSet<string> usedNames,
        out SsaFunction optimized)
    {
        optimized = function;
        var definitions = CollectValueDefinitions(function);
        var definingInstructions = CollectValueInstructions(function);
        var blocksById = function.Blocks.ToDictionary(static block => block.Id);
        var predecessorCounts = CountPredecessors(function);

        foreach (var preheader in function.Blocks)
        {
            if (!TryMatchCanonicalAppendLoop(
                    function,
                    preheader,
                    blocksById,
                    definitions,
                    predecessorCounts,
                    out var loop))
            {
                continue;
            }

            var cloneContext = new CloneContext(definitions, definingInstructions, loop.LoopValueNames, usedNames);
            if (!TryBuildRewrite(loop, cloneContext, out var rewrite))
            {
                continue;
            }

            optimized = ApplyRewrite(function, loop, rewrite);
            return true;
        }

        return false;
    }

    private static bool TryMatchCanonicalAppendLoop(
        SsaFunction function,
        SsaBasicBlock preheader,
        IReadOnlyDictionary<int, SsaBasicBlock> blocksById,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlyDictionary<int, int> predecessorCounts,
        out AppendLoopShape loop)
    {
        loop = null!;
        if (preheader.Terminator.Kind != SsaTerminatorKind.Goto
            || preheader.Terminator.Targets.Count != 1
            || !blocksById.TryGetValue(preheader.Terminator.Targets[0], out var condition)
            || condition.Phis.Count != 1
            || condition.Terminator.Kind != SsaTerminatorKind.Branch
            || condition.Terminator.Condition is null
            || condition.Terminator.Targets.Count != 2)
        {
            return false;
        }

        var bodyId = condition.Terminator.Targets[0];
        var exitId = condition.Terminator.Targets[1];
        if (!blocksById.TryGetValue(bodyId, out var body)
            || !blocksById.TryGetValue(exitId, out var exit)
            || body.Phis.Count != 0
            || body.Terminator.Kind != SsaTerminatorKind.Goto
            || body.Terminator.Targets.Count != 1
            || body.Terminator.Targets[0] != condition.Id
            || exit.Phis.Count != 0
            || !predecessorCounts.TryGetValue(condition.Id, out var conditionPredecessors)
            || conditionPredecessors != 2
            || !predecessorCounts.TryGetValue(body.Id, out var bodyPredecessors)
            || bodyPredecessors != 1
            || !predecessorCounts.TryGetValue(exit.Id, out var exitPredecessors)
            || exitPredecessors != 1)
        {
            return false;
        }

        var induction = condition.Phis[0];
        if (!TryGetPhiIncoming(induction, preheader.Id, out var initialValue)
            || initialValue is not SsaIntegerConstant { Value.IsZero: true }
            || !TryGetPhiIncoming(induction, body.Id, out var updateValue)
            || updateValue is not SsaValueReference updateReference
            || !definitions.TryGetValue(updateReference.Name, out var updateDefinition)
            || !IsIncrementByOne(updateDefinition, induction.ResultName, definitions))
        {
            return false;
        }

        if (!TryResolveComparisonCondition(condition.Terminator.Condition, definitions, out var comparison)
            || comparison.Operator != SsaBinaryOperator.LessThan
            || !IsInductionValue(comparison.Left, induction.ResultName, definitions, new HashSet<string>(StringComparer.Ordinal)))
        {
            return false;
        }

        var loopValueNames = CollectBlockValueNames(condition);
        loopValueNames.UnionWith(CollectBlockValueNames(body));
        if (ValueReferencesAnyLoopValue(comparison.Right, definitions, loopValueNames, new HashSet<string>(StringComparer.Ordinal)))
        {
            return false;
        }

        loop = new AppendLoopShape(
            preheader,
            condition,
            body,
            exit,
            induction,
            updateReference.Name,
            comparison.Right,
            loopValueNames,
            definitions);
        return true;
    }

    private static bool TryBuildRewrite(
        AppendLoopShape loop,
        CloneContext cloneContext,
        out AppendLoopRewrite rewrite)
    {
        rewrite = null!;
        var stores = loop.Body.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();
        if (stores.Length != 2)
        {
            return false;
        }

        if (!TryClassifyAppendStores(loop, stores[0], stores[1], out var dataStore, out var lengthStore, out var match)
            && !TryClassifyAppendStores(loop, stores[1], stores[0], out dataStore, out lengthStore, out match))
        {
            return false;
        }

        var valueDependencies = new HashSet<string>(StringComparer.Ordinal);
        CollectLoopValueDependencies(dataStore.Value, cloneContext.Definitions, loop.LoopValueNames, valueDependencies);
        if (valueDependencies.Overlaps(match.RemovedValueNames))
        {
            return false;
        }

        if (!cloneContext.TryCloneValue(match.CurrentLength, out var startLength)
            || !cloneContext.TryCloneValue(match.DataPointer, out var dataPointer)
            || !cloneContext.TryCloneValue(lengthStore.Address, out var lengthAddress)
            || !TryConvertValueForLength(loop.Count, startLength.Type, cloneContext, out var countForLength))
        {
            return false;
        }

        var tailPointerName = AllocateName(cloneContext.UsedNames, "dynamic_append_tail");
        var tailPointer = new SsaValueReference(tailPointerName, match.ElementAddress.Type);
        cloneContext.Instructions.Add(new SsaValueInstruction(
            tailPointerName,
            new SsaElementAddressRValue(
                dataPointer,
                match.ElementAddress.AggregateType,
                startLength,
                ConstantIndex: null,
                match.ElementAddress.Type,
                $"{match.ElementAddress.Text}:tail"),
            dataStore.Location));

        var finalLengthName = AllocateName(cloneContext.UsedNames, "dynamic_append_final_length");
        var finalLength = new SsaValueReference(finalLengthName, startLength.Type);
        var exitInstructions = new List<SsaInstruction>
        {
            new SsaValueInstruction(
                finalLengthName,
                new SsaBinaryRValue(
                    SsaBinaryOperator.Add,
                    startLength,
                    countForLength,
                    startLength.Type,
                    $"{startLength.Text} + {countForLength.Text}"),
                lengthStore.Location),
            new SsaStoreIndirectInstruction(
                lengthAddress,
                lengthStore.ValueType,
                finalLength,
                lengthStore.Location,
                lengthStore.ScopedNoAliasGroups,
                lengthStore.LoopAccessGroups,
                lengthStore.WriteKind)
        };

        var removedNames = new HashSet<string>(cloneContext.ClonedLoopValueNames, StringComparer.Ordinal)
        {
            match.CommitValueName
        };
        removedNames.Remove(match.ElementAddressName);
        if (valueDependencies.Overlaps(removedNames))
        {
            return false;
        }

        var rewrittenElementAddress = match.ElementAddress with
        {
            Address = tailPointer,
            Index = new SsaValueReference(loop.Induction.ResultName, loop.Induction.Type),
            ConstantIndex = null,
            Text = $"{match.ElementAddress.Text}:tail[{loop.Induction.ResultName}]"
        };

        rewrite = new AppendLoopRewrite(
            cloneContext.Instructions.ToArray(),
            exitInstructions.ToArray(),
            dataStore,
            lengthStore,
            match.ElementAddressName,
            rewrittenElementAddress,
            removedNames);
        return !RewriteWouldLeaveRemovedReferences(loop, rewrite);
    }

    private static bool TryConvertValueForLength(
        SsaValue value,
        StarkTypeSymbol lengthType,
        CloneContext cloneContext,
        out SsaValue converted)
    {
        converted = null!;
        if (!cloneContext.TryCloneValue(value, out var cloned))
        {
            return false;
        }

        if (cloned.Type == lengthType
            || cloned.Type.Kind == lengthType.Kind
            && string.Equals(cloned.Type.DisplayName, lengthType.DisplayName, StringComparison.Ordinal))
        {
            converted = cloned;
            return true;
        }

        if (cloned.Type.Kind != StarkTypeKind.Integer || lengthType.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        var convertedName = AllocateName(cloneContext.UsedNames, "dynamic_append_count");
        converted = new SsaValueReference(convertedName, lengthType);
        cloneContext.Instructions.Add(new SsaValueInstruction(
            convertedName,
            new SsaConvertRValue(cloned, lengthType, $"{cloned.Text}:{lengthType.DisplayName}")));
        return true;
    }

    private static bool TryClassifyAppendStores(
        AppendLoopShape loop,
        SsaStoreIndirectInstruction dataStore,
        SsaStoreIndirectInstruction lengthStore,
        out SsaStoreIndirectInstruction matchedDataStore,
        out SsaStoreIndirectInstruction matchedLengthStore,
        out AppendStoreMatch match)
    {
        matchedDataStore = dataStore;
        matchedLengthStore = lengthStore;
        match = null!;

        if (dataStore.WriteKind != MemoryWriteKind.Initialization
            || !TryMatchLengthCommit(lengthStore, loop, out var currentLength, out var commitValueName)
            || dataStore.Address is not SsaValueReference dataAddressReference
            || !loop.ValueDefinitions.TryGetValue(dataAddressReference.Name, out var dataAddressDefinition)
            || dataAddressDefinition is not SsaElementAddressRValue
            {
                Address.Type.Kind: StarkTypeKind.RawPointer,
                Address.Type.IsMutablePointer: true,
                Index: { } elementIndex,
                ConstantIndex: null
            } elementAddress
            || !ValuesAreEquivalent(elementIndex, currentLength, loop.ValueDefinitions)
            || !HaveSameDynamicOwner(elementAddress.Address, currentLength, lengthStore.Address, loop.ValueDefinitions)
            || NormalizeAggregateType(elementAddress.Address.Type.ElementType ?? StarkTypeSymbols.Error)
               != NormalizeAggregateType(elementAddress.AggregateType)
            || NormalizeAggregateType(dataStore.ValueType) != NormalizeAggregateType(elementAddress.AggregateType)
            || NormalizeAggregateType(dataStore.Value.Type) != NormalizeAggregateType(elementAddress.AggregateType))
        {
            return false;
        }

        var removedValueNames = new HashSet<string>(StringComparer.Ordinal)
        {
            dataAddressReference.Name,
            commitValueName
        };
        CollectLoopValueDependencies(currentLength, loop.ValueDefinitions, loop.LoopValueNames, removedValueNames);
        CollectLoopValueDependencies(elementAddress.Address, loop.ValueDefinitions, loop.LoopValueNames, removedValueNames);
        CollectLoopValueDependencies(lengthStore.Address, loop.ValueDefinitions, loop.LoopValueNames, removedValueNames);
        CollectLoopValueDependencies(lengthStore.Value, loop.ValueDefinitions, loop.LoopValueNames, removedValueNames);

        match = new AppendStoreMatch(
            currentLength,
            elementAddress.Address,
            dataAddressReference.Name,
            elementAddress,
            commitValueName,
            removedValueNames);
        return true;
    }

    private static bool TryMatchLengthCommit(
        SsaStoreIndirectInstruction store,
        AppendLoopShape loop,
        out SsaValue currentLength,
        out string commitValueName)
    {
        currentLength = null!;
        commitValueName = string.Empty;
        if (store.Address is not SsaValueReference addressReference
            || !loop.ValueDefinitions.TryGetValue(addressReference.Name, out var addressDefinition)
            || addressDefinition is not SsaFieldAddressRValue
            {
                FieldName: "Length",
                AggregateType.Kind: StarkTypeKind.Dynamic
            }
            || store.Value is not SsaValueReference valueReference
            || !loop.ValueDefinitions.TryGetValue(valueReference.Name, out var valueDefinition)
            || valueDefinition is not SsaBinaryRValue { Operator: SsaBinaryOperator.Add } binary
            || store.ValueType.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        if (binary.Left is SsaIntegerConstant { Value.IsOne: true })
        {
            currentLength = binary.Right;
            commitValueName = valueReference.Name;
            return true;
        }

        if (binary.Right is SsaIntegerConstant { Value.IsOne: true })
        {
            currentLength = binary.Left;
            commitValueName = valueReference.Name;
            return true;
        }

        return false;
    }

    private static bool HaveSameDynamicOwner(
        SsaValue dataPointer,
        SsaValue currentLength,
        SsaValue lengthAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (TryGetExtractedDynamicFieldTarget(dataPointer, "Data", definitions, out var dataTarget)
            && TryGetExtractedDynamicFieldTarget(currentLength, "Length", definitions, out var lengthTarget)
            && ValuesAreEquivalent(dataTarget, lengthTarget, definitions))
        {
            return true;
        }

        if (TryGetLoadedDynamicFieldAddress(dataPointer, "Data", definitions, out var dataFieldAddress)
            && TryGetDynamicFieldAddress(lengthAddress, "Length", definitions, out var lengthFieldAddress)
            && ValuesAreEquivalent(dataFieldAddress.Address, lengthFieldAddress.Address, definitions))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetExtractedDynamicFieldTarget(
        SsaValue value,
        string fieldName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue target)
    {
        target = null!;
        if (value is SsaValueReference reference
            && definitions.TryGetValue(reference.Name, out var definition)
            && definition is SsaExtractFieldRValue
            {
                Target.Type.Kind: StarkTypeKind.Dynamic,
                Target: { } fieldTarget
            } extract
            && string.Equals(extract.FieldName, fieldName, StringComparison.Ordinal))
        {
            target = fieldTarget;
            return true;
        }

        return false;
    }

    private static bool TryGetLoadedDynamicFieldAddress(
        SsaValue value,
        string fieldName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaFieldAddressRValue fieldAddress)
    {
        fieldAddress = null!;
        if (value is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaLoadIndirectRValue { Address: SsaValueReference addressReference }
            || !definitions.TryGetValue(addressReference.Name, out var addressDefinition)
            || addressDefinition is not SsaFieldAddressRValue candidate
            || candidate.AggregateType.Kind != StarkTypeKind.Dynamic
            || !string.Equals(candidate.FieldName, fieldName, StringComparison.Ordinal))
        {
            return false;
        }

        fieldAddress = candidate;
        return true;
    }

    private static bool TryGetDynamicFieldAddress(
        SsaValue value,
        string fieldName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaFieldAddressRValue fieldAddress)
    {
        fieldAddress = null!;
        if (value is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition)
            || definition is not SsaFieldAddressRValue candidate
            || candidate.AggregateType.Kind != StarkTypeKind.Dynamic
            || !string.Equals(candidate.FieldName, fieldName, StringComparison.Ordinal))
        {
            return false;
        }

        fieldAddress = candidate;
        return true;
    }

    private static SsaFunction ApplyRewrite(
        SsaFunction function,
        AppendLoopShape loop,
        AppendLoopRewrite rewrite)
    {
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        foreach (var block in function.Blocks)
        {
            if (block.Id == loop.Preheader.Id)
            {
                var instructions = block.Instructions.Concat(rewrite.PreheaderInstructions).ToArray();
                blocks.Add(block with { Instructions = instructions });
                continue;
            }

            if (block.Id == loop.Body.Id)
            {
                blocks.Add(RewriteBodyBlock(block, rewrite));
                continue;
            }

            if (block.Id == loop.Exit.Id)
            {
                var instructions = rewrite.ExitPrefixInstructions.Concat(block.Instructions).ToArray();
                blocks.Add(block with { Instructions = instructions });
                continue;
            }

            blocks.Add(block);
        }

        return function with { Blocks = blocks.ToArray() };
    }

    private static SsaBasicBlock RewriteBodyBlock(SsaBasicBlock block, AppendLoopRewrite rewrite)
    {
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        foreach (var instruction in block.Instructions)
        {
            if (ReferenceEquals(instruction, rewrite.LengthStore))
            {
                continue;
            }

            if (instruction is SsaValueInstruction valueInstruction)
            {
                if (string.Equals(valueInstruction.ResultName, rewrite.ElementAddressName, StringComparison.Ordinal))
                {
                    instructions.Add(valueInstruction with { Value = rewrite.RewrittenElementAddress });
                    continue;
                }

                if (rewrite.RemovedValueNames.Contains(valueInstruction.ResultName))
                {
                    continue;
                }
            }

            instructions.Add(instruction);
        }

        return block with { Instructions = instructions.ToArray() };
    }

    private static bool RewriteWouldLeaveRemovedReferences(AppendLoopShape loop, AppendLoopRewrite rewrite)
    {
        foreach (var instruction in loop.Body.Instructions)
        {
            if (ReferenceEquals(instruction, rewrite.LengthStore))
            {
                continue;
            }

            if (instruction is SsaValueInstruction valueInstruction
                && rewrite.RemovedValueNames.Contains(valueInstruction.ResultName))
            {
                continue;
            }

            var rewritten = instruction is SsaValueInstruction elementAddressInstruction
                            && string.Equals(elementAddressInstruction.ResultName, rewrite.ElementAddressName, StringComparison.Ordinal)
                ? elementAddressInstruction with { Value = rewrite.RewrittenElementAddress }
                : instruction;

            if (InstructionReferencesAny(rewritten, rewrite.RemovedValueNames))
            {
                return true;
            }
        }

        return loop.Body.Terminator.Condition is not null
               && ValueReferencesAny(loop.Body.Terminator.Condition, rewrite.RemovedValueNames)
               || loop.Body.Terminator.Value is not null
               && ValueReferencesAny(loop.Body.Terminator.Value, rewrite.RemovedValueNames);
    }

    private static bool TryResolveComparisonCondition(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaBinaryRValue comparison)
    {
        comparison = null!;
        if (value is not SsaValueReference reference
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        switch (definition)
        {
            case SsaBinaryRValue binary
                when binary.Operator is SsaBinaryOperator.LessThan
                    or SsaBinaryOperator.LessThanOrEqual
                    or SsaBinaryOperator.GreaterThan
                    or SsaBinaryOperator.GreaterThanOrEqual
                    or SsaBinaryOperator.Equal
                    or SsaBinaryOperator.NotEqual:
                comparison = binary;
                return true;
            case SsaUseRValue use:
                return TryResolveComparisonCondition(use.Value, definitions, out comparison);
            default:
                return false;
        }
    }

    private static bool IsIncrementByOne(
        SsaRValue definition,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return definition is SsaBinaryRValue { Operator: SsaBinaryOperator.Add } binary
               && (IsInductionValue(binary.Left, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
                       && binary.Right is SsaIntegerConstant { Value.IsOne: true }
                   || IsInductionValue(binary.Right, inductionValueName, definitions, new HashSet<string>(StringComparer.Ordinal))
                       && binary.Left is SsaIntegerConstant { Value.IsOne: true });
    }

    private static bool IsInductionValue(
        SsaValue value,
        string inductionValueName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        if (string.Equals(reference.Name, inductionValueName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => IsInductionValue(use.Value, inductionValueName, definitions, visitedValueNames),
            SsaConvertRValue convert => IsInductionValue(convert.Operand, inductionValueName, definitions, visitedValueNames),
            _ => false
        };
    }

    private static bool ValuesAreEquivalent(
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return ValuesAreEquivalent(left, right, definitions, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool ValuesAreEquivalent(
        SsaValue left,
        SsaValue right,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedPairs)
    {
        if (Equals(left, right))
        {
            return true;
        }

        var pairKey = $"{left.Text}|{right.Text}";
        if (!visitedPairs.Add(pairKey))
        {
            return false;
        }

        if (left is SsaValueReference leftReference
            && definitions.TryGetValue(leftReference.Name, out var leftDefinition)
            && leftDefinition is SsaUseRValue leftUse)
        {
            return ValuesAreEquivalent(leftUse.Value, right, definitions, visitedPairs);
        }

        if (right is SsaValueReference rightReference
            && definitions.TryGetValue(rightReference.Name, out var rightDefinition)
            && rightDefinition is SsaUseRValue rightUse)
        {
            return ValuesAreEquivalent(left, rightUse.Value, definitions, visitedPairs);
        }

        return false;
    }

    private static void CollectLoopValueDependencies(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> loopValueNames,
        ISet<string> dependencies)
    {
        if (value is not SsaValueReference reference
            || !loopValueNames.Contains(reference.Name)
            || !dependencies.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return;
        }

        foreach (var child in EnumerateRValueChildren(definition))
        {
            CollectLoopValueDependencies(child, definitions, loopValueNames, dependencies);
        }
    }

    private static bool ValueReferencesAnyLoopValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> loopValueNames,
        ISet<string> visitedValueNames)
    {
        if (value is not SsaValueReference reference)
        {
            return false;
        }

        if (loopValueNames.Contains(reference.Name))
        {
            return true;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !definitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return EnumerateRValueChildren(definition)
            .Any(child => ValueReferencesAnyLoopValue(child, definitions, loopValueNames, visitedValueNames));
    }

    private static bool InstructionReferencesAny(SsaInstruction instruction, IReadOnlySet<string> names)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => RValueReferencesAny(valueInstruction.Value, names),
            SsaCallInstruction call => call.Arguments.Any(argument => ValueReferencesAny(argument, names))
                                       || call.IndirectArgumentAddresses?.Any(address => address is not null && ValueReferencesAny(address, names)) == true,
            SsaIndirectCallInstruction call => ValueReferencesAny(call.Target, names)
                                               || call.Arguments.Any(argument => ValueReferencesAny(argument, names))
                                               || call.IndirectArgumentAddresses?.Any(address => address is not null && ValueReferencesAny(address, names)) == true,
            SsaStoreLocalInstruction store => ValueReferencesAny(store.Value, names),
            SsaStoreIndirectInstruction store => ValueReferencesAny(store.Address, names) || ValueReferencesAny(store.Value, names),
            SsaCopyMemoryInstruction copy => ValueReferencesAny(copy.DestinationAddress, names) || ValueReferencesAny(copy.SourceAddress, names),
            SsaStoreGlobalInstruction store => ValueReferencesAny(store.Value, names),
            _ => false
        };
    }

    private static bool RValueReferencesAny(SsaRValue value, IReadOnlySet<string> names)
    {
        return EnumerateRValueChildren(value).Any(child => ValueReferencesAny(child, names));
    }

    private static bool ValueReferencesAny(SsaValue value, IReadOnlySet<string> names)
    {
        return value is SsaValueReference reference && names.Contains(reference.Name);
    }

    private static IEnumerable<SsaValue> EnumerateRValueChildren(SsaRValue value)
    {
        switch (value)
        {
            case SsaUseRValue use:
                yield return use.Value;
                break;
            case SsaUnaryRValue unary:
                yield return unary.Operand;
                break;
            case SsaBinaryRValue binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case SsaSelectRValue select:
                yield return select.Condition;
                yield return select.WhenTrue;
                yield return select.WhenFalse;
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    yield return argument;
                }

                foreach (var address in call.IndirectArgumentAddresses ?? [])
                {
                    if (address is not null)
                    {
                        yield return address;
                    }
                }

                break;
            case SsaIndirectCallRValue call:
                yield return call.Target;
                foreach (var argument in call.Arguments)
                {
                    yield return argument;
                }

                foreach (var address in call.IndirectArgumentAddresses ?? [])
                {
                    if (address is not null)
                    {
                        yield return address;
                    }
                }

                break;
            case SsaConvertRValue convert:
                yield return convert.Operand;
                break;
            case SsaExtractFieldRValue extract:
                yield return extract.Target;
                break;
            case SsaInsertFieldRValue insert:
                yield return insert.Target;
                yield return insert.Value;
                break;
            case SsaExtractIndexRValue extract:
                yield return extract.Target;
                break;
            case SsaInsertIndexRValue insert:
                yield return insert.Target;
                yield return insert.Value;
                break;
            case SsaMakeSliceFromPointerRValue makeSlice:
                yield return makeSlice.Pointer;
                yield return makeSlice.Length;
                break;
            case SsaDynamicStorageAllocationRValue allocation:
                yield return allocation.Capacity;
                break;
            case SsaDynamicStorageFreeRValue free:
                yield return free.Storage;
                break;
            case SsaHeapStorageFreeRValue free:
                yield return free.Pointer;
                break;
            case SsaDynamicStorageReserveRValue reserve:
                yield return reserve.StorageAddress;
                yield return reserve.AdditionalCapacity;
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                yield return reserve.StorageAddress;
                yield return reserve.AdditionalCapacity;
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                yield return reserve.StorageAddress;
                yield return reserve.TargetCapacity;
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                yield return moveLast.StorageAddress;
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                yield return moveAt.StorageAddress;
                yield return moveAt.Index;
                break;
            case SsaLoadSliceElementRValue loadSlice:
                yield return loadSlice.Slice;
                yield return loadSlice.Index;
                break;
            case SsaTextSliceRValue textSlice:
                yield return textSlice.TextValue;
                yield return textSlice.Start;
                yield return textSlice.Length;
                break;
            case SsaFieldAddressRValue fieldAddress:
                yield return fieldAddress.Address;
                break;
            case SsaElementAddressRValue elementAddress:
                yield return elementAddress.Address;
                if (elementAddress.Index is not null)
                {
                    yield return elementAddress.Index;
                }

                break;
            case SsaSliceElementAddressRValue sliceAddress:
                yield return sliceAddress.Slice;
                yield return sliceAddress.Index;
                break;
            case SsaLoadIndirectRValue load:
                yield return load.Address;
                break;
        }
    }

    private static bool TryGetPhiIncoming(SsaPhi phi, int predecessorBlockId, out SsaValue value)
    {
        foreach (var incoming in phi.Incomings)
        {
            if (incoming.PredecessorBlockId == predecessorBlockId)
            {
                value = incoming.Value;
                return true;
            }
        }

        value = null!;
        return false;
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

    private static Dictionary<string, SsaValueInstruction> CollectValueInstructions(SsaFunction function)
    {
        var instructions = new Dictionary<string, SsaValueInstruction>(StringComparer.Ordinal);
        foreach (var instruction in function.Blocks
                     .SelectMany(static block => block.Instructions)
                     .OfType<SsaValueInstruction>())
        {
            instructions[instruction.ResultName] = instruction;
        }

        return instructions;
    }

    private static HashSet<string> CollectUsedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
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

    private static HashSet<string> CollectBlockValueNames(SsaBasicBlock block)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phi in block.Phis)
        {
            names.Add(phi.ResultName);
        }

        foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
        {
            names.Add(instruction.ResultName);
        }

        return names;
    }

    private static IReadOnlyDictionary<int, int> CountPredecessors(SsaFunction function)
    {
        var counts = new Dictionary<int, int>();
        foreach (var block in function.Blocks)
        {
            foreach (var target in block.Terminator.Targets)
            {
                counts[target] = counts.TryGetValue(target, out var count) ? count + 1 : 1;
            }

            if (block.Terminator.DefaultTarget is int defaultTarget)
            {
                counts[defaultTarget] = counts.TryGetValue(defaultTarget, out var count) ? count + 1 : 1;
            }
        }

        return counts;
    }

    private static string AllocateName(HashSet<string> usedNames, string stem)
    {
        var candidate = stem;
        var suffix = 0;
        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = $"{stem}_{suffix}";
        }

        return candidate;
    }

    private static StarkTypeSymbol NormalizeAggregateType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private sealed class CloneContext
    {
        private readonly IReadOnlyDictionary<string, SsaValueInstruction> _definingInstructions;
        private readonly IReadOnlySet<string> _loopValueNames;
        private readonly Dictionary<string, SsaValueReference> _clonedValues = new(StringComparer.Ordinal);

        public CloneContext(
            IReadOnlyDictionary<string, SsaRValue> definitions,
            IReadOnlyDictionary<string, SsaValueInstruction> definingInstructions,
            IReadOnlySet<string> loopValueNames,
            HashSet<string> usedNames)
        {
            Definitions = definitions;
            _definingInstructions = definingInstructions;
            _loopValueNames = loopValueNames;
            UsedNames = usedNames;
        }

        public IReadOnlyDictionary<string, SsaRValue> Definitions { get; }

        public HashSet<string> UsedNames { get; }

        public List<SsaInstruction> Instructions { get; } = [];

        public HashSet<string> ClonedLoopValueNames { get; } = new(StringComparer.Ordinal);

        public bool TryCloneValue(SsaValue value, out SsaValue cloned)
        {
            cloned = value;
            if (value is not SsaValueReference reference
                || !_loopValueNames.Contains(reference.Name))
            {
                return true;
            }

            if (_clonedValues.TryGetValue(reference.Name, out var existing))
            {
                cloned = existing;
                return true;
            }

            if (!Definitions.TryGetValue(reference.Name, out var definition)
                || !TryCloneRValue(definition, out var clonedDefinition))
            {
                return false;
            }

            var clonedName = AllocateName(UsedNames, $"{reference.Name}_dynamic_append");
            var clonedReference = new SsaValueReference(clonedName, clonedDefinition.Type);
            _clonedValues[reference.Name] = clonedReference;
            ClonedLoopValueNames.Add(reference.Name);
            _definingInstructions.TryGetValue(reference.Name, out var originalInstruction);
            Instructions.Add(new SsaValueInstruction(
                clonedName,
                clonedDefinition,
                originalInstruction?.Location,
                originalInstruction?.ScopedNoAliasGroups,
                originalInstruction?.LoopAccessGroups));
            cloned = clonedReference;
            return true;
        }

        private bool TryCloneRValue(SsaRValue value, out SsaRValue cloned)
        {
            cloned = value;
            switch (value)
            {
                case SsaUseRValue use:
                    if (!TryCloneValue(use.Value, out var useValue))
                    {
                        return false;
                    }

                    cloned = new SsaUseRValue(useValue);
                    return true;
                case SsaUnaryRValue unary:
                    if (!TryCloneValue(unary.Operand, out var operand))
                    {
                        return false;
                    }

                    cloned = unary with { Operand = operand };
                    return true;
                case SsaBinaryRValue binary:
                    if (!TryCloneValue(binary.Left, out var left)
                        || !TryCloneValue(binary.Right, out var right))
                    {
                        return false;
                    }

                    cloned = binary with { Left = left, Right = right };
                    return true;
                case SsaSelectRValue select:
                    if (!TryCloneValue(select.Condition, out var condition)
                        || !TryCloneValue(select.WhenTrue, out var whenTrue)
                        || !TryCloneValue(select.WhenFalse, out var whenFalse))
                    {
                        return false;
                    }

                    cloned = select with
                    {
                        Condition = condition,
                        WhenTrue = whenTrue,
                        WhenFalse = whenFalse
                    };
                    return true;
                case SsaConvertRValue convert:
                    if (!TryCloneValue(convert.Operand, out var convertOperand))
                    {
                        return false;
                    }

                    cloned = convert with { Operand = convertOperand };
                    return true;
                case SsaExtractFieldRValue extract:
                    if (!TryCloneValue(extract.Target, out var extractTarget))
                    {
                        return false;
                    }

                    cloned = extract with { Target = extractTarget };
                    return true;
                case SsaInsertFieldRValue insert:
                    if (!TryCloneValue(insert.Target, out var insertTarget)
                        || !TryCloneValue(insert.Value, out var insertValue))
                    {
                        return false;
                    }

                    cloned = insert with { Target = insertTarget, Value = insertValue };
                    return true;
                case SsaExtractIndexRValue extractIndex:
                    if (!TryCloneValue(extractIndex.Target, out var extractIndexTarget))
                    {
                        return false;
                    }

                    cloned = extractIndex with { Target = extractIndexTarget };
                    return true;
                case SsaInsertIndexRValue insertIndex:
                    if (!TryCloneValue(insertIndex.Target, out var insertIndexTarget)
                        || !TryCloneValue(insertIndex.Value, out var insertIndexValue))
                    {
                        return false;
                    }

                    cloned = insertIndex with { Target = insertIndexTarget, Value = insertIndexValue };
                    return true;
                case SsaMakeSliceFromLocalRValue:
                case SsaAddressOfLocalRValue:
                case SsaAddressOfParameterRValue:
                case SsaLoadGlobalRValue:
                case SsaLoadLocalRValue:
                    return true;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    if (!TryCloneValue(makeSlice.Pointer, out var pointer)
                        || !TryCloneValue(makeSlice.Length, out var length))
                    {
                        return false;
                    }

                    cloned = makeSlice with { Pointer = pointer, Length = length };
                    return true;
                case SsaFieldAddressRValue fieldAddress:
                    if (!TryCloneValue(fieldAddress.Address, out var fieldBase))
                    {
                        return false;
                    }

                    cloned = fieldAddress with { Address = fieldBase };
                    return true;
                case SsaElementAddressRValue elementAddress:
                    if (!TryCloneValue(elementAddress.Address, out var elementBase))
                    {
                        return false;
                    }

                    SsaValue? clonedIndex = null;
                    if (elementAddress.Index is not null
                        && !TryCloneValue(elementAddress.Index, out clonedIndex))
                    {
                        return false;
                    }

                    cloned = elementAddress with { Address = elementBase, Index = clonedIndex };
                    return true;
                case SsaSliceElementAddressRValue sliceAddress:
                    if (!TryCloneValue(sliceAddress.Slice, out var slice)
                        || !TryCloneValue(sliceAddress.Index, out var index))
                    {
                        return false;
                    }

                    cloned = sliceAddress with { Slice = slice, Index = index };
                    return true;
                case SsaLoadIndirectRValue load:
                    if (!TryCloneValue(load.Address, out var address))
                    {
                        return false;
                    }

                    cloned = load with { Address = address };
                    return true;
                default:
                    return false;
            }
        }
    }

    private sealed record AppendLoopShape(
        SsaBasicBlock Preheader,
        SsaBasicBlock Condition,
        SsaBasicBlock Body,
        SsaBasicBlock Exit,
        SsaPhi Induction,
        string UpdateValueName,
        SsaValue Count,
        IReadOnlySet<string> LoopValueNames,
        IReadOnlyDictionary<string, SsaRValue> ValueDefinitions);

    private sealed record AppendStoreMatch(
        SsaValue CurrentLength,
        SsaValue DataPointer,
        string ElementAddressName,
        SsaElementAddressRValue ElementAddress,
        string CommitValueName,
        IReadOnlySet<string> RemovedValueNames);

    private sealed record AppendLoopRewrite(
        IReadOnlyList<SsaInstruction> PreheaderInstructions,
        IReadOnlyList<SsaInstruction> ExitPrefixInstructions,
        SsaStoreIndirectInstruction DataStore,
        SsaStoreIndirectInstruction LengthStore,
        string ElementAddressName,
        SsaElementAddressRValue RewrittenElementAddress,
        IReadOnlySet<string> RemovedValueNames);
}
