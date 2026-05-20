using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private void EmitPhi(SsaPhi phi)
    {
        var incoming = string.Join(
            ", ",
            phi.Incomings.Select(entry => $"[ {FormatValue(entry.Value)}, %{FormatPhiIncomingBlockLabel(entry.PredecessorBlockId)} ]"));
        AppendLine($"  %{EscapeIdentifier(phi.ResultName)} = phi{GetFastMathSuffix(phi.Type)} {MapType(phi.Type)} {incoming}");
    }

    private void EmitAssumptionsForBlock(int blockId)
    {
        if (!_assumptionsByBlock.TryGetValue(blockId, out var assumptions))
        {
            return;
        }

        foreach (var assumption in assumptions)
        {
            var condition = assumption.Condition is null
                ? "true"
                : FormatValue(assumption.Condition);
            if (assumption.NegateCondition)
            {
                var negated = $"%{EscapeIdentifier(CreateAbiTempName($"assume_not_{_nextAssumeTempId++}"))}";
                AppendLine($"  {negated} = xor i1 {condition}, true");
                condition = negated;
            }

            var operandBundleSuffix = assumption.OperandBundles.Count == 0
                ? string.Empty
                : " [" + string.Join(", ", assumption.OperandBundles.Select(RenderAssumeOperandBundle)) + "]";
            AppendLine($"  call void @llvm.assume(i1 {condition}){operandBundleSuffix}");
        }
    }

    private void EmitEntrySameParameterAssumptions()
    {
        if (_function.SameGroups.Count == 0)
        {
            return;
        }

        var emittedPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _function.SameGroups)
        {
            var names = group.ParameterNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var leftIndex = 0; leftIndex < names.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < names.Length; rightIndex++)
                {
                    if (emittedPairs.Add(BuildParameterPairKey(names[leftIndex], names[rightIndex])))
                    {
                        EmitEntrySameParameterAssumptions(names[leftIndex], names[rightIndex]);
                    }
                }
            }
        }
    }

    private void EmitEntrySameParameterAssumptions(string leftName, string rightName)
    {
        if (!TryGetAbiUserParameter(leftName, out var left)
            || !TryGetAbiUserParameter(rightName, out var right))
        {
            return;
        }

        var leftView = TryGetSameParameterViewComponents(left, "same_left", out var leftData, out var leftLength);
        var rightView = TryGetSameParameterViewComponents(right, "same_right", out var rightData, out var rightLength);
        if (leftView && rightView)
        {
            EmitPointerEqualityAssume(leftData, rightData, "same_data");
            EmitI64EqualityAssume(leftLength, rightLength, "same_len");
            return;
        }

        var leftPointer = leftView
            ? leftData
            : TryGetSameParameterPointerOperand(left, out var directLeftPointer)
                ? directLeftPointer
                : null;
        var rightPointer = rightView
            ? rightData
            : TryGetSameParameterPointerOperand(right, out var directRightPointer)
                ? directRightPointer
                : null;
        if (leftPointer is not null && rightPointer is not null)
        {
            EmitPointerEqualityAssume(leftPointer, rightPointer, "same_ptr");
        }
    }

    private bool TryGetAbiUserParameter(string sourceName, out AbiParameterSymbol parameter)
    {
        var found = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.SourceName, sourceName, StringComparison.Ordinal));
        if (found is null)
        {
            parameter = default!;
            return false;
        }

        parameter = found;
        return true;
    }

    private bool TryGetSameParameterViewComponents(
        AbiParameterSymbol parameter,
        string purpose,
        out string dataPointer,
        out string length)
    {
        dataPointer = string.Empty;
        length = string.Empty;
        if (parameter.Kind != AbiParameterKind.Direct || !IsSliceLikeMemoryView(parameter.SourceType))
        {
            return false;
        }

        var aggregate = $"%{EscapeIdentifier(parameter.LlvmName)}";
        var llvmType = MapType(parameter.LlvmType);
        dataPointer = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_data"))}";
        length = $"%{EscapeIdentifier(CreateAbiTempName($"{purpose}_len"))}";
        AppendLine($"  {dataPointer} = extractvalue {llvmType} {aggregate}, 0");
        AppendLine($"  {length} = extractvalue {llvmType} {aggregate}, 1");
        return true;
    }

    private static bool TryGetSameParameterPointerOperand(AbiParameterSymbol parameter, out string pointer)
    {
        if (parameter.Kind == AbiParameterKind.Direct && parameter.SourceType.Kind != StarkTypeKind.RawPointer)
        {
            pointer = string.Empty;
            return false;
        }

        pointer = $"%{EscapeIdentifier(parameter.LlvmName)}";
        return (parameter.Kind is AbiParameterKind.Direct or AbiParameterKind.IndirectIn)
            && parameter.LlvmType.Kind == StarkTypeKind.RawPointer;
    }

    private void EmitPointerEqualityAssume(string leftPointer, string rightPointer, string purpose)
    {
        var comparison = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        AppendLine($"  {comparison} = icmp eq ptr {leftPointer}, {rightPointer}");
        AppendLine($"  call void @llvm.assume(i1 {comparison})");
    }

    private void EmitI64EqualityAssume(string leftValue, string rightValue, string purpose)
    {
        var comparison = $"%{EscapeIdentifier(CreateAbiTempName(purpose))}";
        AppendLine($"  {comparison} = icmp eq i64 {leftValue}, {rightValue}");
        AppendLine($"  call void @llvm.assume(i1 {comparison})");
    }

    private string RenderAssumeOperandBundle(LlvmAssumeOperandBundle bundle)
    {
        return bundle.Kind switch
        {
            LlvmAssumeOperandBundleKind.NonNull => $"\"nonnull\"(ptr {FormatValue(bundle.Pointer)})",
            LlvmAssumeOperandBundleKind.Align when bundle.AlignmentBytes is int alignmentBytes =>
                $"\"align\"(ptr {FormatValue(bundle.Pointer)}, i64 {alignmentBytes})",
            _ => throw new UnsupportedBodyEmissionException("Unsupported llvm.assume operand bundle.")
        };
    }

    private IReadOnlyDictionary<int, IReadOnlyList<LlvmAssumeFact>> BuildAssumptionsByBlock()
    {
        var assumptionsByBlock = new Dictionary<int, List<LlvmAssumeFact>>();

        foreach (var block in _ssaFunction.Blocks)
        {
            if (block.Terminator.Kind != SsaTerminatorKind.Branch
                || block.Terminator.Condition is null
                || block.Terminator.Targets.Count != 2)
            {
                continue;
            }

            AddAssumptionsForTarget(block, targetIndex: 0, assumeConditionTrue: true);
            AddAssumptionsForTarget(block, targetIndex: 1, assumeConditionTrue: false);
        }

        return assumptionsByBlock.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<LlvmAssumeFact>)entry.Value,
            EqualityComparer<int>.Default);

        void AddAssumptionsForTarget(SsaBasicBlock sourceBlock, int targetIndex, bool assumeConditionTrue)
        {
            var targetBlockId = sourceBlock.Terminator.Targets[targetIndex];
            if (!CanEmitAssumeInSuccessor(_ssaFunction.EntryBlockId, sourceBlock.Id, targetBlockId, _blockOrderById, _predecessorCounts))
            {
                return;
            }

            var assumptions = BuildAssumeFacts(sourceBlock.Terminator.Condition!, assumeConditionTrue);
            if (assumptions.Count == 0)
            {
                return;
            }

            if (!assumptionsByBlock.TryGetValue(targetBlockId, out var targetAssumptions))
            {
                targetAssumptions = [];
                assumptionsByBlock[targetBlockId] = targetAssumptions;
            }

            targetAssumptions.AddRange(assumptions);
        }
    }

    private IReadOnlyList<LlvmAssumeFact> BuildAssumeFacts(SsaValue condition, bool assumeConditionTrue)
    {
        if (!TryResolveComparisonCondition(condition, _valueDefinitions, out var comparison))
        {
            return [];
        }

        var assumptions = new List<LlvmAssumeFact>();
        AddPointerAssumeFacts(comparison, assumeConditionTrue, assumptions);

        if (IsIntegerValueRangeNarrowingComparison(comparison, _valueDefinitions))
        {
            assumptions.Add(new LlvmAssumeFact(condition, !assumeConditionTrue, []));
        }

        return assumptions;
    }

    private void AddPointerAssumeFacts(
        SsaBinaryRValue comparison,
        bool assumeConditionTrue,
        List<LlvmAssumeFact> assumptions)
    {
        var pointerFacts = new Dictionary<string, List<LlvmAssumeOperandBundle>>(StringComparer.Ordinal);

        if (TryGetNullComparedPointer(comparison, out var nullCheckedPointer, out var nonNullWhenConditionTrue)
            && assumeConditionTrue == nonNullWhenConditionTrue)
        {
            AddPointerBundle(nullCheckedPointer, LlvmAssumeOperandBundleKind.NonNull);
        }

        if (GetAssumedComparisonOperator(comparison.Operator, assumeConditionTrue) == SsaBinaryOperator.Equal
            && comparison.Left.Type.Kind == StarkTypeKind.RawPointer
            && comparison.Right.Type.Kind == StarkTypeKind.RawPointer)
        {
            AddEqualityDerivedPointerFacts(comparison.Left, comparison.Right);
            AddEqualityDerivedPointerFacts(comparison.Right, comparison.Left);
        }

        foreach (var bundles in pointerFacts.Values)
        {
            assumptions.Add(new LlvmAssumeFact(null, false, bundles));
        }

        void AddEqualityDerivedPointerFacts(SsaValue factSource, SsaValue factTarget)
        {
            if (!IsKnownNonNullPointerValue(factSource, new HashSet<string>(StringComparer.Ordinal)))
            {
                return;
            }

            AddPointerBundle(factTarget, LlvmAssumeOperandBundleKind.NonNull);
            if (TryGetPointerElementType(factTarget, out var pointeeType)
                && TryGetKnownPointerAlignmentBytes(factSource, pointeeType, out var alignmentBytes))
            {
                AddPointerBundle(factTarget, LlvmAssumeOperandBundleKind.Align, alignmentBytes);
            }
        }

        void AddPointerBundle(
            SsaValue pointer,
            LlvmAssumeOperandBundleKind kind,
            int? alignmentBytes = null)
        {
            if (pointer.Type.Kind != StarkTypeKind.RawPointer)
            {
                return;
            }

            if (kind == LlvmAssumeOperandBundleKind.Align && alignmentBytes is not > 1)
            {
                return;
            }

            var key = FormatAssumePointerKey(pointer);
            if (!pointerFacts.TryGetValue(key, out var bundles))
            {
                bundles = [];
                pointerFacts[key] = bundles;
            }

            if (bundles.Any(existing => existing.Kind == kind && existing.AlignmentBytes == alignmentBytes))
            {
                return;
            }

            bundles.Add(new LlvmAssumeOperandBundle(kind, pointer, alignmentBytes));
        }
    }

    private bool IsKnownNonNullPointerValue(SsaValue value, ISet<string> visitedValueNames)
    {
        if (value is SsaNullConstant)
        {
            return false;
        }

        return value switch
        {
            SsaGlobalAddressValue => true,
            SsaFunctionAddressValue => true,
            SsaValueReference reference when reference.Type.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerReference(reference, visitedValueNames),
            _ => value.Type.Kind == StarkTypeKind.RawPointer
                && TryGetValueDefinition(value, visitedValueNames, out var definition)
                && IsKnownNonNullPointerDefinition(definition, visitedValueNames)
        };
    }

    private bool IsKnownNonNullPointerReference(SsaValueReference reference, ISet<string> visitedValueNames)
    {
        if (!visitedValueNames.Add(reference.Name))
        {
            return false;
        }

        if (_valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return IsKnownNonNullPointerDefinition(definition, visitedValueNames);
        }

        var parameter = _abiFunction.UserParameters.FirstOrDefault(
            candidate => string.Equals(candidate.LlvmName, reference.Name, StringComparison.Ordinal)
                || string.Equals(candidate.SourceName, reference.Name, StringComparison.Ordinal));
        return parameter is not null
            && parameter.LlvmType.Kind == StarkTypeKind.RawPointer
            && (parameter.SourceType.BorrowKind != StarkBorrowKind.None
                || parameter.SourceType.InitializationKind != StarkInitializationKind.None);
    }

    private bool IsKnownNonNullPointerDefinition(SsaRValue definition, ISet<string> visitedValueNames)
    {
        return definition switch
        {
            SsaUseRValue use => IsKnownNonNullPointerValue(use.Value, visitedValueNames),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerValue(convert.Operand, visitedValueNames),
            SsaSelectRValue select when select.Type.Kind == StarkTypeKind.RawPointer =>
                IsKnownNonNullPointerValue(select.WhenTrue, new HashSet<string>(visitedValueNames, StringComparer.Ordinal))
                && IsKnownNonNullPointerValue(select.WhenFalse, new HashSet<string>(visitedValueNames, StringComparer.Ordinal)),
            SsaAddressOfLocalRValue => true,
            SsaAddressOfParameterRValue => true,
            SsaFieldAddressRValue fieldAddress => IsKnownNonNullPointerValue(fieldAddress.Address, visitedValueNames),
            SsaElementAddressRValue elementAddress => IsKnownNonNullPointerValue(elementAddress.Address, visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => IsKnownNonNullPointerValue(sliceElementAddress.Slice, visitedValueNames),
            _ => false
        };
    }

    private bool TryGetValueDefinition(
        SsaValue value,
        ISet<string> visitedValueNames,
        out SsaRValue definition)
    {
        if (value is SsaValueReference reference
            && visitedValueNames.Add(reference.Name)
            && _valueDefinitions.TryGetValue(reference.Name, out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    private static string FormatAssumePointerKey(SsaValue pointer)
    {
        return pointer switch
        {
            SsaValueReference reference => $"ref:{reference.Name}",
            SsaGlobalAddressValue global => $"global:{global.GlobalName}",
            _ => $"value:{pointer.Text}:{pointer.Type.DisplayName}"
        };
    }

    private static bool TryGetPointerElementType(SsaValue pointer, out StarkTypeSymbol pointeeType)
    {
        if (pointer.Type.Kind == StarkTypeKind.RawPointer && pointer.Type.ElementType is { } elementType)
        {
            pointeeType = elementType;
            return true;
        }

        pointeeType = StarkTypeSymbols.Error;
        return false;
    }
}
