using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private static HashSet<string> CollectReferencedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    VisitValue(incoming.Value);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitValue(block.Terminator.Condition);
            VisitValue(block.Terminator.Value);

            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitValue(switchCase.MatchValue);
                }
            }
        }

        return names;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    VisitRValue(valueInstruction.Value);
                    break;
                case SsaCallInstruction call:
                    VisitDirectCall(call);
                    break;
                case SsaIndirectCallInstruction call:
                    VisitIndirectCall(call);
                    break;
                case SsaStoreLocalInstruction storeLocal:
                    VisitValue(storeLocal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitValue(storeIndirect.Address);
                    VisitValue(storeIndirect.Value);
                    break;
                case SsaCopyMemoryInstruction copyMemory:
                    VisitValue(copyMemory.DestinationAddress);
                    VisitValue(copyMemory.SourceAddress);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitValue(storeGlobal.Value);
                    break;
            }
        }

        void VisitRValue(SsaRValue value)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    VisitValue(use.Value);
                    break;
                case SsaUnaryRValue unary:
                    VisitValue(unary.Operand);
                    break;
                case SsaBinaryRValue binary:
                    VisitValue(binary.Left);
                    VisitValue(binary.Right);
                    break;
                case SsaSelectRValue select:
                    VisitValue(select.Condition);
                    VisitValue(select.WhenTrue);
                    VisitValue(select.WhenFalse);
                    break;
                case SsaCallRValue call:
                    VisitDirectCall(call);
                    break;
                case SsaIndirectCallRValue indirectCall:
                    VisitIndirectCall(indirectCall);
                    break;
                case SsaConvertRValue convert:
                    VisitValue(convert.Operand);
                    break;
                case SsaExtractFieldRValue extractField:
                    VisitValue(extractField.Target);
                    break;
                case SsaInsertFieldRValue insertField:
                    VisitValue(insertField.Target);
                    VisitValue(insertField.Value);
                    break;
                case SsaExtractIndexRValue extractIndex:
                    VisitValue(extractIndex.Target);
                    break;
                case SsaInsertIndexRValue insertIndex:
                    VisitValue(insertIndex.Target);
                    VisitValue(insertIndex.Value);
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    VisitValue(makeSlice.Pointer);
                    VisitValue(makeSlice.Length);
                    break;
                case SsaDynamicStorageAllocationRValue allocation:
                    VisitValue(allocation.Capacity);
                    break;
                case SsaDynamicStorageFreeRValue free:
                    VisitValue(free.Storage);
                    break;
                case SsaHeapStorageFreeRValue free:
                    VisitValue(free.Pointer);
                    break;
                case SsaDynamicStorageReserveRValue reserve:
                    VisitValue(reserve.StorageAddress);
                    VisitValue(reserve.AdditionalCapacity);
                    break;
                case SsaDynamicStorageTryReserveRValue reserve:
                    VisitValue(reserve.StorageAddress);
                    VisitValue(reserve.AdditionalCapacity);
                    break;
                case SsaDynamicStorageTryReserveCapacityRValue reserve:
                    VisitValue(reserve.StorageAddress);
                    VisitValue(reserve.TargetCapacity);
                    break;
                case SsaDynamicStorageMoveLastRValue moveLast:
                    VisitValue(moveLast.StorageAddress);
                    break;
                case SsaDynamicStorageMoveAtRValue moveAt:
                    VisitValue(moveAt.StorageAddress);
                    VisitValue(moveAt.Index);
                    break;
                case SsaLoadSliceElementRValue loadSlice:
                    VisitValue(loadSlice.Slice);
                    VisitValue(loadSlice.Index);
                    break;
                case SsaTextSliceRValue textSlice:
                    VisitValue(textSlice.TextValue);
                    VisitValue(textSlice.Start);
                    VisitValue(textSlice.Length);
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    VisitValue(fieldAddress.Address);
                    break;
                case SsaElementAddressRValue elementAddress:
                    VisitValue(elementAddress.Address);
                    VisitValue(elementAddress.Index);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    VisitValue(sliceElementAddress.Slice);
                    VisitValue(sliceElementAddress.Index);
                    break;
                case SsaLoadIndirectRValue loadIndirect:
                    VisitValue(loadIndirect.Address);
                    break;
            }
        }

        void VisitDirectCall(ISsaDirectCallOperation call)
        {
            VisitCallArguments(call.Arguments, call.IndirectArgumentLocalNames, call.IndirectArgumentAddresses);
        }

        void VisitIndirectCall(ISsaIndirectCallOperation call)
        {
            VisitValue(call.Target);
            VisitCallArguments(call.Arguments, call.IndirectArgumentLocalNames, call.IndirectArgumentAddresses);
        }

        void VisitCallArguments(
            IReadOnlyList<SsaValue> arguments,
            IReadOnlyList<string?>? indirectArgumentLocalNames,
            IReadOnlyList<SsaValue?>? indirectArgumentAddresses)
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                var hasIndirectAddress = indirectArgumentAddresses is not null
                    && index < indirectArgumentAddresses.Count
                    && indirectArgumentAddresses[index] is not null;
                var hasPromotedLocal = indirectArgumentLocalNames is not null
                    && index < indirectArgumentLocalNames.Count
                    && !string.IsNullOrWhiteSpace(indirectArgumentLocalNames[index]);
                if (hasIndirectAddress || hasPromotedLocal)
                {
                    continue;
                }

                VisitValue(arguments[index]);
            }

            foreach (var address in indirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                VisitValue(address);
            }
        }

        void VisitValue(SsaValue? value)
        {
            if (value is SsaValueReference reference)
            {
                names.Add(reference.Name);
            }
        }
    }

    private static HashSet<string> CollectAddressTakenParameterNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction { Value: SsaAddressOfParameterRValue addressOfParameter })
                {
                    names.Add(addressOfParameter.ParameterName);
                }
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction valueInstruction)
                {
                    definitions[valueInstruction.ResultName] = valueInstruction.Value;
                }
            }
        }

        return definitions;
    }

    private static IReadOnlyDictionary<string, SsaPhi> CollectPhisByResultName(SsaFunction function)
    {
        var phis = new Dictionary<string, SsaPhi>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                phis[phi.ResultName] = phi;
            }
        }

        return phis;
    }

    private static IReadOnlyDictionary<string, SsaValue> CollectTrivialValueAliases(SsaFunction function)
    {
        var aliases = new Dictionary<string, SsaValue>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction valueInstruction
                    && TryGetTrivialValueAlias(valueInstruction.Value, out var aliasValue))
                {
                    aliases[valueInstruction.ResultName] = aliasValue;
                }
            }
        }

        return aliases;
    }

    private static bool TryGetTrivialValueAlias(SsaRValue value, out SsaValue aliasValue)
    {
        switch (value)
        {
            case SsaUseRValue use:
                aliasValue = use.Value;
                return true;
            case SsaConvertRValue convert when IsNoOpConversion(convert.Operand.Type, convert.TargetType):
                aliasValue = convert.Operand;
                return true;
            default:
                aliasValue = new SsaUndefValue(value.Type);
                return false;
        }
    }

    private static bool IsNoOpConversion(StarkTypeSymbol sourceType, StarkTypeSymbol targetType)
    {
        if (IsPointerBackedBorrowRuntimePointerConversion(sourceType, targetType))
        {
            return true;
        }

        var source = NormalizeAggregateType(sourceType);
        var target = NormalizeAggregateType(targetType);
        if (HaveSameNoOpValueShape(source, target))
        {
            return true;
        }

        return (source.Kind, target.Kind) switch
        {
            (StarkTypeKind.Integer, StarkTypeKind.Integer) => source.BitWidth == target.BitWidth,
            (StarkTypeKind.Float, StarkTypeKind.Float) => source.BitWidth == target.BitWidth,
            (StarkTypeKind.RawPointer, StarkTypeKind.RawPointer) => true,
            (StarkTypeKind.FunctionPointer, StarkTypeKind.FunctionPointer) => true,
            _ => false
        };
    }

    private static bool IsPointerBackedBorrowRuntimePointerConversion(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType)
    {
        if (StarkTypeSymbols.IsPointerBackedBorrowType(sourceType)
            && targetType.Kind == StarkTypeKind.RawPointer
            && targetType.ElementType is { } targetElementType)
        {
            return HaveSameNoOpValueShape(StarkTypeSymbols.BorrowReturnValueType(sourceType), targetElementType);
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer
            && sourceType.ElementType is { } sourceElementType
            && StarkTypeSymbols.IsPointerBackedBorrowType(targetType))
        {
            return HaveSameNoOpValueShape(sourceElementType, StarkTypeSymbols.BorrowReturnValueType(targetType));
        }

        return false;
    }

    private static bool HaveSameNoOpValueShape(StarkTypeSymbol sourceType, StarkTypeSymbol targetType)
    {
        var source = NormalizeAggregateType(sourceType);
        var target = NormalizeAggregateType(targetType);
        if (source.Kind != target.Kind)
        {
            return false;
        }

        return source.Kind switch
        {
            StarkTypeKind.Void or StarkTypeKind.Bool or StarkTypeKind.Ascii or StarkTypeKind.Unicode => true,
            StarkTypeKind.Integer or StarkTypeKind.Float => source.BitWidth == target.BitWidth,
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer or StarkTypeKind.Null => true,
            StarkTypeKind.FixedArray => source.FixedLength == target.FixedLength
                && source.ElementType is not null
                && target.ElementType is not null
                && HaveSameNoOpValueShape(source.ElementType, target.ElementType),
            StarkTypeKind.Slice or StarkTypeKind.Dynamic => source.ElementType is not null
                && target.ElementType is not null
                && HaveSameNoOpValueShape(source.ElementType, target.ElementType),
            StarkTypeKind.Closure => true,
            StarkTypeKind.Named => string.Equals(source.NamedType, target.NamedType, StringComparison.Ordinal),
            _ => source == target
        };
    }

    private static IReadOnlyDictionary<string, SsaInstructionPosition> CollectValueDefinitionPositions(SsaFunction function)
    {
        var positions = new Dictionary<string, SsaInstructionPosition>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (block.Instructions[instructionIndex] is SsaValueInstruction valueInstruction)
                {
                    positions[valueInstruction.ResultName] = new SsaInstructionPosition(block.Id, instructionIndex);
                }
            }
        }

        return positions;
    }

    private static IReadOnlyDictionary<int, int> CollectBlockOrder(SsaFunction function)
    {
        return function.Blocks
            .Select(static (block, index) => (BlockId: block.Id, Index: index))
            .ToDictionary(static item => item.BlockId, static item => item.Index);
    }

    private static IReadOnlyDictionary<int, int> CountPredecessors(SsaFunction function)
    {
        var predecessorSets = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<int>(),
            EqualityComparer<int>.Default);

        foreach (var block in function.Blocks)
        {
            foreach (var target in EnumerateTerminatorTargets(block.Terminator).Distinct())
            {
                if (predecessorSets.TryGetValue(target, out var predecessors))
                {
                    predecessors.Add(block.Id);
                }
            }
        }

        return predecessorSets.ToDictionary(
            static entry => entry.Key,
            static entry => entry.Value.Count,
            EqualityComparer<int>.Default);
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

        if (terminator.SwitchCases is null)
        {
            yield break;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            yield return switchCase.TargetBlockId;
        }
    }

    private static bool CanEmitAssumeInSuccessor(
        int entryBlockId,
        int sourceBlockId,
        int targetBlockId,
        IReadOnlyDictionary<int, int> blockOrderById,
        IReadOnlyDictionary<int, int> predecessorCounts)
    {
        if (targetBlockId == entryBlockId
            || !predecessorCounts.TryGetValue(targetBlockId, out var predecessorCount)
            || predecessorCount != 1
            || !blockOrderById.TryGetValue(sourceBlockId, out var sourceOrder)
            || !blockOrderById.TryGetValue(targetBlockId, out var targetOrder))
        {
            return false;
        }

        // Avoid emitting a non-PHI instruction in a block that appears before
        // the branch condition definition in textual LLVM order.
        return sourceOrder < targetOrder;
    }

    private static bool IsPotentialAssumableCondition(
        SsaValue condition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        return TryResolveComparisonCondition(condition, valueDefinitions, out var comparison)
            && (IsIntegerValueRangeNarrowingComparison(comparison, valueDefinitions)
                || IsPointerComparisonCandidate(comparison, valueDefinitions));
    }

    private static bool TryResolveComparisonCondition(
        SsaValue condition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        out SsaBinaryRValue comparison)
    {
        if (condition is SsaValueReference reference
            && valueDefinitions.TryGetValue(reference.Name, out var definition)
            && definition is SsaBinaryRValue binary
            && binary.Type.Kind == StarkTypeKind.Bool
            && IsComparisonOperator(binary.Operator))
        {
            comparison = binary;
            return true;
        }

        comparison = null!;
        return false;
    }

    private static bool IsComparisonOperator(SsaBinaryOperator operation)
    {
        return operation is SsaBinaryOperator.Equal
            or SsaBinaryOperator.NotEqual
            or SsaBinaryOperator.LessThan
            or SsaBinaryOperator.LessThanOrEqual
            or SsaBinaryOperator.GreaterThan
            or SsaBinaryOperator.GreaterThanOrEqual;
    }

    private static bool IsIntegerValueRangeNarrowingComparison(
        SsaBinaryRValue comparison,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        return comparison.Left.Type.Kind == StarkTypeKind.Integer
            && comparison.Right.Type.Kind == StarkTypeKind.Integer
            && (TryResolveIntegerConstant(comparison.Left, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out _)
                || TryResolveIntegerConstant(comparison.Right, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out _));
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames,
        out BigInteger constant)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !valueDefinitions.TryGetValue(reference.Name, out var definition))
                {
                    constant = default;
                    return false;
                }

                return definition switch
                {
                    SsaUseRValue use => TryResolveIntegerConstant(use.Value, valueDefinitions, visitedValueNames, out constant),
                    SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Integer =>
                        TryResolveIntegerConstant(convert.Operand, valueDefinitions, visitedValueNames, out constant),
                    _ => Fail(out constant)
                };
            default:
                constant = default;
                return false;
        }

        static bool Fail(out BigInteger value)
        {
            value = default;
            return false;
        }
    }

    private static bool IsPointerComparisonCandidate(
        SsaBinaryRValue comparison,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        if (comparison.Left.Type.Kind != StarkTypeKind.RawPointer
            || comparison.Right.Type.Kind != StarkTypeKind.RawPointer
            || comparison.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            return false;
        }

        if (comparison.Left is SsaNullConstant || comparison.Right is SsaNullConstant)
        {
            return true;
        }

        return IsPotentialKnownNonNullPointerValue(comparison.Left, valueDefinitions, new HashSet<string>(StringComparer.Ordinal))
            || IsPotentialKnownNonNullPointerValue(comparison.Right, valueDefinitions, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsPotentialKnownNonNullPointerValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaValueReference reference when reference.Type.Kind == StarkTypeKind.RawPointer
                && visitedValueNames.Add(reference.Name)
                && valueDefinitions.TryGetValue(reference.Name, out var definition) =>
                IsPotentialKnownNonNullPointerDefinition(definition, valueDefinitions, visitedValueNames),
            _ => false
        };
    }

    private static bool IsPotentialKnownNonNullPointerDefinition(
        SsaRValue definition,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedValueNames)
    {
        return definition switch
        {
            SsaUseRValue use => IsPotentialKnownNonNullPointerValue(use.Value, valueDefinitions, visitedValueNames),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer =>
                IsPotentialKnownNonNullPointerValue(convert.Operand, valueDefinitions, visitedValueNames),
            SsaSelectRValue select when select.Type.Kind == StarkTypeKind.RawPointer =>
                IsPotentialKnownNonNullPointerValue(
                    select.WhenTrue,
                    valueDefinitions,
                    new HashSet<string>(visitedValueNames, StringComparer.Ordinal))
                && IsPotentialKnownNonNullPointerValue(
                    select.WhenFalse,
                    valueDefinitions,
                    new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            SsaAddressOfLocalRValue => true,
            SsaAddressOfParameterRValue => true,
            SsaFieldAddressRValue fieldAddress =>
                IsPotentialKnownNonNullPointerValue(fieldAddress.Address, valueDefinitions, visitedValueNames),
            SsaElementAddressRValue elementAddress =>
                IsPotentialKnownNonNullPointerValue(elementAddress.Address, valueDefinitions, visitedValueNames),
            _ => false
        };
    }

    private static bool TryGetNullComparedPointer(
        SsaBinaryRValue comparison,
        out SsaValue pointer,
        out bool nonNullWhenConditionTrue)
    {
        if (comparison.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual))
        {
            pointer = null!;
            nonNullWhenConditionTrue = false;
            return false;
        }

        if (comparison.Left is SsaNullConstant && comparison.Right.Type.Kind == StarkTypeKind.RawPointer)
        {
            pointer = comparison.Right;
            nonNullWhenConditionTrue = comparison.Operator == SsaBinaryOperator.NotEqual;
            return true;
        }

        if (comparison.Right is SsaNullConstant && comparison.Left.Type.Kind == StarkTypeKind.RawPointer)
        {
            pointer = comparison.Left;
            nonNullWhenConditionTrue = comparison.Operator == SsaBinaryOperator.NotEqual;
            return true;
        }

        pointer = null!;
        nonNullWhenConditionTrue = false;
        return false;
    }

    private static SsaBinaryOperator? GetAssumedComparisonOperator(SsaBinaryOperator operation, bool assumeConditionTrue)
    {
        if (assumeConditionTrue)
        {
            return operation;
        }

        return operation switch
        {
            SsaBinaryOperator.Equal => SsaBinaryOperator.NotEqual,
            SsaBinaryOperator.NotEqual => SsaBinaryOperator.Equal,
            SsaBinaryOperator.LessThan => SsaBinaryOperator.GreaterThanOrEqual,
            SsaBinaryOperator.LessThanOrEqual => SsaBinaryOperator.GreaterThan,
            SsaBinaryOperator.GreaterThan => SsaBinaryOperator.LessThanOrEqual,
            SsaBinaryOperator.GreaterThanOrEqual => SsaBinaryOperator.LessThan,
            _ => null
        };
    }

    private static HashSet<string> CollectTailCallResultNames(
        SsaFunction function,
        AbiFunctionSignature callerAbi,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName,
        LlvmEmissionContext context,
        bool isStrictFp)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (isStrictFp
            || callerAbi.ReturnsIndirect
            || callerAbi.LlvmReturnType.Kind == StarkTypeKind.Void
            || callerAbi.IsFfi
            || !callerAbi.UsesFastCallingConvention)
        {
            return names;
        }

        foreach (var block in function.Blocks)
        {
            if (block.Terminator.Kind != SsaTerminatorKind.Return
                || block.Terminator.Value is not SsaValueReference returnedValue
                || block.Instructions.Count == 0
                || block.Instructions[^1] is not SsaValueInstruction
                {
                    ResultName: var resultName,
                    Value: SsaCallRValue call
                }
                || !string.Equals(resultName, returnedValue.Name, StringComparison.Ordinal)
                || !CanEmitTailCallMarker(callerAbi, call, resolveCallAbi, currentFunctionName, context))
            {
                continue;
            }

            names.Add(resultName);
        }

        return names;
    }

    private static bool CanEmitTailCallMarker(
        AbiFunctionSignature callerAbi,
        SsaCallRValue call,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName,
        LlvmEmissionContext context)
    {
        var calleeAbi = resolveCallAbi(currentFunctionName, call.FunctionName);
        return calleeAbi is not null
            && !calleeAbi.ReturnsIndirect
            && !calleeAbi.IsFfi
            && calleeAbi.UsesFastCallingConvention
            && calleeAbi.LlvmReturnType.Kind != StarkTypeKind.Void
            && string.Equals(context.MapType(callerAbi.LlvmReturnType), context.MapType(calleeAbi.LlvmReturnType), StringComparison.Ordinal)
            && calleeAbi.UserParameters.All(static parameter => parameter.Kind == AbiParameterKind.Direct)
            && call.Arguments.All(static argument => !MayContainPointerStorage(argument.Type));
    }

    private static bool MayContainPointerStorage(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.Named => true,
            StarkTypeKind.FixedArray when type.ElementType is not null => MayContainPointerStorage(type.ElementType),
            _ => false
        };
    }

    private static HashSet<string> CollectTbaaUnsafeAddressRoots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    if (incoming.Value.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddAddressValueRootsFresh(incoming.Value);
                    }
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitEscapingValue(block.Terminator.Value);
            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitEscapingValue(switchCase.MatchValue);
                }
            }
        }

        return roots;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal:
                    VisitEscapingValue(storeLocal.Value);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitEscapingValue(storeGlobal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitEscapingValue(storeIndirect.Value);
                    break;
                case SsaValueInstruction { Value: SsaCallRValue call }:
                    foreach (var argument in call.Arguments)
                    {
                        VisitEscapingValue(argument);
                    }

                    foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                    {
                        VisitEscapingValue(address);
                    }

                    break;
                case SsaCallInstruction call:
                    foreach (var argument in call.Arguments)
                    {
                        VisitEscapingValue(argument);
                    }

                    foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                    {
                        VisitEscapingValue(address);
                    }

                    break;
                case SsaValueInstruction { Value: SsaConvertRValue convert }
                    when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                         || convert.TargetType.Kind == StarkTypeKind.RawPointer:
                    AddAddressValueRootsFresh(convert.Operand);
                    break;
            }
        }

        void VisitEscapingValue(SsaValue? value)
        {
            if (value?.Type.Kind == StarkTypeKind.RawPointer)
            {
                AddAddressValueRootsFresh(value);
            }
        }

        void AddAddressValueRootsFresh(SsaValue value)
        {
            AddAddressValueRoots(value, new HashSet<string>(StringComparer.Ordinal));
        }

        void AddAddressValueRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else if (reference.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                case SsaGlobalAddressValue globalAddress:
                    roots.Add(CreateTbaaGlobalRootKey(globalAddress.GlobalName));
                    break;
            }
        }

        void AddAddressRValueRoots(SsaRValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    AddAddressValueRoots(use.Value, visitedValueNames);
                    break;
                case SsaAddressOfLocalRValue addressOfLocal:
                    roots.Add(CreateTbaaLocalRootKey(addressOfLocal.LocalName));
                    break;
                case SsaAddressOfParameterRValue addressOfParameter:
                    roots.Add(CreateTbaaParameterRootKey(addressOfParameter.ParameterName));
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    AddAddressValueRoots(fieldAddress.Address, visitedValueNames);
                    break;
                case SsaElementAddressRValue elementAddress:
                    AddAddressValueRoots(elementAddress.Address, visitedValueNames);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    AddSliceRoots(sliceElementAddress.Slice, visitedValueNames);
                    break;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    roots.Add(CreateTbaaLocalRootKey(makeSlice.LocalName));
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    AddAddressValueRoots(makeSlice.Pointer, visitedValueNames);
                    break;
                case SsaTextSliceRValue textSlice:
                    AddSliceRoots(textSlice.TextValue, visitedValueNames);
                    break;
                case SsaConvertRValue convert:
                    AddAddressValueRoots(convert.Operand, visitedValueNames);
                    break;
                case SsaSelectRValue select:
                    AddAddressValueRoots(select.WhenTrue, visitedValueNames);
                    AddAddressValueRoots(select.WhenFalse, visitedValueNames);
                    break;
            }
        }

        void AddSliceRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                case SsaStringConstant:
                case SsaTextDataAddressValue:
                    break;
                default:
                    AddAddressValueRoots(value, visitedValueNames);
                    break;
            }
        }

        void AddParameterRoot(string parameterName)
        {
            roots.Add(CreateTbaaParameterRootKey(parameterName));
            if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
            {
                roots.Add(CreateTbaaParameterRootKey(parameterName[4..]));
            }
        }
    }

    private static HashSet<string> CollectScopedNoAliasUnsafeAddressRoots(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        string currentFunctionName)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    if (incoming.Value.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddAddressValueRootsFresh(incoming.Value);
                    }
                }
            }

            foreach (var instruction in block.Instructions)
            {
                VisitInstruction(instruction);
            }

            VisitEscapingValue(block.Terminator.Value);
            if (block.Terminator.SwitchCases is not null)
            {
                foreach (var switchCase in block.Terminator.SwitchCases)
                {
                    VisitEscapingValue(switchCase.MatchValue);
                }
            }
        }

        return roots;

        void VisitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaStoreLocalInstruction storeLocal:
                    VisitEscapingValue(storeLocal.Value);
                    break;
                case SsaStoreGlobalInstruction storeGlobal:
                    VisitEscapingValue(storeGlobal.Value);
                    break;
                case SsaStoreIndirectInstruction storeIndirect:
                    VisitEscapingValue(storeIndirect.Value);
                    break;
                case SsaValueInstruction { Value: SsaCallRValue call }:
                    VisitCallArguments(call);
                    break;
                case SsaCallInstruction call:
                    VisitCallArguments(call);
                    break;
                case SsaValueInstruction { Value: SsaConvertRValue convert }
                    when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                         || convert.TargetType.Kind == StarkTypeKind.RawPointer:
                    AddAddressValueRootsFresh(convert.Operand);
                    break;
            }
        }

        void VisitCallArguments(ISsaDirectCallOperation call)
        {
            var calleeAbi = resolveCallAbi(currentFunctionName, call.FunctionName);
            if (calleeAbi is null || calleeAbi.IsFfi)
            {
                foreach (var argument in call.Arguments)
                {
                    VisitEscapingValue(argument);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    VisitEscapingValue(address);
                }

                return;
            }

            var userParameters = calleeAbi.UserParameters;
            for (var index = 0; index < call.Arguments.Count; index++)
            {
                var argument = call.Arguments[index];
                if (argument.Type.Kind != StarkTypeKind.RawPointer)
                {
                    continue;
                }

                if (index >= userParameters.Count || userParameters[index].SourceType.Kind == StarkTypeKind.RawPointer)
                {
                    AddAddressValueRootsFresh(argument);
                }
            }
        }

        void VisitEscapingValue(SsaValue? value)
        {
            if (value?.Type.Kind == StarkTypeKind.RawPointer)
            {
                AddAddressValueRootsFresh(value);
            }
        }

        void AddAddressValueRootsFresh(SsaValue value)
        {
            AddAddressValueRoots(value, new HashSet<string>(StringComparer.Ordinal));
        }

        void AddAddressValueRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else if (reference.Type.Kind == StarkTypeKind.RawPointer)
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
            }
        }

        void AddAddressRValueRoots(SsaRValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    AddAddressValueRoots(use.Value, visitedValueNames);
                    break;
                case SsaExtractFieldRValue { FieldName: "Data", Target.Type.Kind: StarkTypeKind.Dynamic } extractField:
                    AddDynamicValueRoot(extractField.Target, visitedValueNames);
                    break;
                case SsaAddressOfParameterRValue addressOfParameter:
                    roots.Add(CreateScopedAliasParameterRootKey(addressOfParameter.ParameterName));
                    break;
                case SsaFieldAddressRValue fieldAddress:
                    AddAddressValueRoots(fieldAddress.Address, visitedValueNames);
                    break;
                case SsaElementAddressRValue elementAddress:
                    AddAddressValueRoots(elementAddress.Address, visitedValueNames);
                    break;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    AddSliceRoots(sliceElementAddress.Slice, visitedValueNames);
                    break;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    AddAddressValueRoots(makeSlice.Pointer, visitedValueNames);
                    break;
                case SsaTextSliceRValue textSlice:
                    AddSliceRoots(textSlice.TextValue, visitedValueNames);
                    break;
                case SsaConvertRValue convert:
                    AddAddressValueRoots(convert.Operand, visitedValueNames);
                    break;
                case SsaSelectRValue select:
                    AddAddressValueRoots(select.WhenTrue, visitedValueNames);
                    AddAddressValueRoots(select.WhenFalse, visitedValueNames);
                    break;
            }
        }

        void AddSliceRoots(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddAddressRValueRoots(definition, visitedValueNames);
                    }
                    else
                    {
                        AddParameterRoot(reference.Name);
                    }

                    break;
                default:
                    AddAddressValueRoots(value, visitedValueNames);
                    break;
            }
        }

        void AddDynamicValueRoot(SsaValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaValueReference reference:
                    if (!visitedValueNames.Add(reference.Name))
                    {
                        return;
                    }

                    if (valueDefinitions.TryGetValue(reference.Name, out var definition))
                    {
                        AddDynamicRValueRoot(definition, visitedValueNames);
                    }

                    break;
            }
        }

        void AddDynamicRValueRoot(SsaRValue value, ISet<string> visitedValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    AddDynamicValueRoot(use.Value, visitedValueNames);
                    break;
                case SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Dynamic } loadLocal:
                    roots.Add(CreateScopedAliasDynamicLocalRootKey(loadLocal.LocalName));
                    break;
            }
        }

        void AddParameterRoot(string parameterName)
        {
            roots.Add(CreateScopedAliasParameterRootKey(parameterName));
            if (parameterName.StartsWith("arg_", StringComparison.Ordinal) && parameterName.Length > 4)
            {
                roots.Add(CreateScopedAliasParameterRootKey(parameterName[4..]));
            }
        }
    }

    private static Dictionary<string, string> CollectLocalStorageClasses(SsaFunction function)
    {
        var storageClasses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaAllocateLocalInstruction allocateLocal)
                {
                    storageClasses[allocateLocal.LocalName] = allocateLocal.StorageClass;
                }
            }
        }

        return storageClasses;
    }

    private IReadOnlyDictionary<string, SsaValue> CollectSingleStoreLocalValues()
    {
        var values = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var writeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaStoreLocalInstruction storeLocal:
                        writeCounts.TryGetValue(storeLocal.LocalName, out var writeCount);
                        writeCounts[storeLocal.LocalName] = writeCount + 1;
                        if (writeCount == 0)
                        {
                            values[storeLocal.LocalName] = storeLocal.Value;
                        }
                        else
                        {
                            values.Remove(storeLocal.LocalName);
                        }

                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        if (TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var copyDestinationLocal))
                        {
                            blocked.Add(copyDestinationLocal);
                        }

                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        if (TryResolveLocalAddressRoot(storeIndirect.Address, out var indirectDestinationLocal))
                        {
                            blocked.Add(indirectDestinationLocal);
                        }

                        break;
                    case SsaValueInstruction { Value: SsaCallRValue call }:
                        BlockDirectCallLocalRoots(call);
                        break;
                    case SsaCallInstruction call:
                        BlockDirectCallLocalRoots(call);
                        break;
                    case SsaIndirectCallInstruction call:
                        BlockIndirectCallLocalRoots(call);
                        break;
                }
            }
        }

        foreach (var localName in blocked)
        {
            values.Remove(localName);
        }

        foreach (var (localName, writeCount) in writeCounts)
        {
            if (writeCount != 1)
            {
                values.Remove(localName);
            }
        }

        return values;

        void BlockDirectCallLocalRoots(ISsaDirectCallOperation call)
        {
            foreach (var argument in call.Arguments)
            {
                if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }

            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }
        }

        void BlockIndirectCallLocalRoots(ISsaIndirectCallOperation call)
        {
            if (TryResolveLocalAddressRoot(call.Target, out var targetLocal))
            {
                blocked.Add(targetLocal);
            }

            foreach (var argument in call.Arguments)
            {
                if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }

            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }
        }
    }

    private HashSet<string> CollectDirectAggregateAliasCandidateLocalNames()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var allocateLocal in block.Instructions.OfType<SsaAllocateLocalInstruction>())
            {
                if (allocateLocal.StorageClass != "stack"
                    || !_singleStoreLocalValues.TryGetValue(allocateLocal.LocalName, out var storedValue)
                    || !CanAliasLocalToFreshIndirectAggregateSource(storedValue, allocateLocal.LocalType))
                {
                    continue;
                }

                candidates.Add(allocateLocal.LocalName);
            }
        }

        return candidates;
    }

    private bool TryResolveSingleStoreLocalValue(string localName, out SsaValue value)
    {
        return _singleStoreLocalValues.TryGetValue(localName, out value!);
    }

    private HashSet<string> CollectConstProvenanceLocalNames()
    {
        return _ssaFunction.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaAllocateLocalInstruction>()
            .Where(static allocateLocal =>
                allocateLocal.HasConstProvenance
                || ConstProvenanceFacts.HasPermanentConstProvenance(allocateLocal.ConstProvenance))
            .Select(static allocateLocal => allocateLocal.LocalName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private HashSet<string> CollectInvariantLocalNames()
    {
        var candidates = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var writeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _ssaFunction.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case SsaAllocateLocalInstruction { IsImmutable: true, StorageClass: "stack" } allocateLocal:
                        if (TryGetConcreteTypeLayout(allocateLocal.LocalType) is not null)
                        {
                            candidates[allocateLocal.LocalName] = allocateLocal.LocalType;
                        }

                        break;
                    case SsaStoreLocalInstruction storeLocal:
                        CountLocalWrite(storeLocal.LocalName);
                        break;
                    case SsaCopyMemoryInstruction copyMemory:
                        if (TryResolveLocalAddressRoot(copyMemory.DestinationAddress, out var copyDestinationLocal))
                        {
                            CountLocalWrite(copyDestinationLocal);
                        }

                        break;
                    case SsaStoreIndirectInstruction storeIndirect:
                        if (TryResolveLocalAddressRoot(storeIndirect.Address, out var indirectDestinationLocal))
                        {
                            blocked.Add(indirectDestinationLocal);
                        }

                        break;
                    case SsaValueInstruction { Value: SsaCallRValue call }:
                        BlockDirectCallLocalRoots(call);
                        break;
                    case SsaCallInstruction call:
                        BlockDirectCallLocalRoots(call);
                        break;
                    case SsaValueInstruction { Value: SsaIndirectCallRValue call }:
                        BlockIndirectCallLocalRoots(call);
                        break;
                    case SsaIndirectCallInstruction call:
                        BlockIndirectCallLocalRoots(call);
                        break;
                }
            }
        }

        return candidates.Keys
            .Where(localName => !blocked.Contains(localName)
                                && writeCounts.TryGetValue(localName, out var writeCount)
                                && writeCount == 1)
            .ToHashSet(StringComparer.Ordinal);

        void CountLocalWrite(string localName)
        {
            writeCounts[localName] = writeCounts.TryGetValue(localName, out var count)
                ? count + 1
                : 1;
        }

        void BlockDirectCallLocalRoots(ISsaDirectCallOperation call)
        {
            foreach (var argument in call.Arguments)
            {
                if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }

            foreach (var localName in call.IndirectArgumentLocalNames ?? [])
            {
                if (!string.IsNullOrWhiteSpace(localName))
                {
                    blocked.Add(localName!);
                }
            }

            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }
        }

        void BlockIndirectCallLocalRoots(ISsaIndirectCallOperation call)
        {
            if (TryResolveLocalAddressRoot(call.Target, out var targetLocal))
            {
                blocked.Add(targetLocal);
            }

            foreach (var argument in call.Arguments)
            {
                if (TryResolveLocalAddressRoot(argument, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }

            foreach (var localName in call.IndirectArgumentLocalNames ?? [])
            {
                if (!string.IsNullOrWhiteSpace(localName))
                {
                    blocked.Add(localName!);
                }
            }

            foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
            {
                if (TryResolveLocalAddressRoot(address, out var escapedLocal))
                {
                    blocked.Add(escapedLocal);
                }
            }
        }
    }

    private string FormatValueReference(SsaValueReference reference)
    {
        if (_valueAliases.TryGetValue(reference.Name, out var alias))
        {
            return alias;
        }

        if (_trivialValueAliases.TryGetValue(reference.Name, out var aliasValue))
        {
            return FormatTrivialValueAlias(aliasValue, new HashSet<string>(StringComparer.Ordinal) { reference.Name });
        }

        return FormatNonAliasedValueReference(reference);
    }

    private string FormatTrivialValueAlias(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is SsaValueReference reference)
        {
            if (_valueAliases.TryGetValue(reference.Name, out var alias))
            {
                return alias;
            }

            if (_trivialValueAliases.TryGetValue(reference.Name, out var aliasValue)
                && visitedValueNames.Add(reference.Name))
            {
                return FormatTrivialValueAlias(aliasValue, visitedValueNames);
            }

            return FormatNonAliasedValueReference(reference);
        }

        return FormatValue(value);
    }

    private string FormatNonAliasedValueReference(SsaValueReference reference)
    {
        return _materializedParameters.TryGetValue(reference.Name, out var materialized)
            ? materialized
            : $"%{EscapeIdentifier(reference.Name)}";
    }
}
