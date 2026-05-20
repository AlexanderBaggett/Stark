using System.Globalization;
using System.Numerics;
using System.Text;

namespace Stark.Compiler;

internal sealed class SsaIntegerArithmeticFolder
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
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var definitions = BuildDefinitionMap(function);
        if (definitions.Count == 0)
        {
            return function;
        }

        var usedNames = CollectExistingValueNames(function);
        var temporaryCounter = 0;
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var optimized = OptimizeBlock(block, definitions, usedNames, ref temporaryCounter);
                changed |= !ReferenceEquals(optimized, block);
                return optimized;
            })
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private static IReadOnlyDictionary<string, SsaRValue> BuildDefinitionMap(SsaFunction function)
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

    private static HashSet<string> CollectExistingValueNames(SsaFunction function)
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

    private static SsaBasicBlock OptimizeBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var changed = false;
        var instructions = new List<SsaInstruction>(block.Instructions.Count);

        foreach (var instruction in block.Instructions)
        {
            if (instruction is SsaValueInstruction valueInstruction
                && TryFoldLinearIntegerInstruction(
                    valueInstruction,
                    definitions,
                    usedNames,
                    ref temporaryCounter,
                    out var emittedInstructions))
            {
                instructions.AddRange(emittedInstructions);
                changed = true;
                continue;
            }

            if (instruction is SsaValueInstruction productInstruction
                && TryFoldProductIntegerInstruction(
                    productInstruction,
                    definitions,
                    usedNames,
                    ref temporaryCounter,
                    out var productInstructions))
            {
                instructions.AddRange(productInstructions);
                changed = true;
                continue;
            }

            instructions.Add(instruction);
        }

        return changed
            ? block with { Instructions = instructions }
            : block;
    }

    private static bool TryFoldLinearIntegerInstruction(
        SsaValueInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        HashSet<string> usedNames,
        ref int temporaryCounter,
        out IReadOnlyList<SsaInstruction> instructions)
    {
        instructions = [];

        if (instruction.Value is not SsaBinaryRValue binary
            || !TryGetLinearFamily(binary.Operator, out var family)
            || binary.Type.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        var terms = new List<LinearTerm>();
        FlattenLinearTerms(
            new SsaValueReference(instruction.ResultName, binary.Type),
            binary,
            family,
            coefficient: BigInteger.One,
            expectedType: binary.Type,
            definitions,
            terms,
            new HashSet<string>(StringComparer.Ordinal));

        if (terms.Count < 2)
        {
            return false;
        }

        var compactedTerms = CompactLinearTerms(terms, family, binary.Type, definitions, out var changed);
        if (!changed)
        {
            return false;
        }

        instructions = EmitFoldedInstruction(
            instruction,
            family,
            compactedTerms,
            usedNames,
            ref temporaryCounter);
        return true;
    }

    private static bool TryFoldProductIntegerInstruction(
        SsaValueInstruction instruction,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        HashSet<string> usedNames,
        ref int temporaryCounter,
        out IReadOnlyList<SsaInstruction> instructions)
    {
        instructions = [];

        if (instruction.Value is not SsaBinaryRValue binary
            || !TryGetProductFamily(binary.Operator, out var family)
            || binary.Type.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        var factors = new List<SsaValue>();
        FlattenProductFactors(
            new SsaValueReference(instruction.ResultName, binary.Type),
            binary,
            family,
            binary.Type,
            definitions,
            factors,
            new HashSet<string>(StringComparer.Ordinal));

        if (factors.Count < 2)
        {
            return false;
        }

        var compactedFactors = CompactProductFactors(factors, binary.Type, out var changed);
        if (!changed)
        {
            return false;
        }

        instructions = EmitFoldedProductInstruction(
            instruction,
            family,
            compactedFactors,
            usedNames,
            ref temporaryCounter);
        return true;
    }

    private static void FlattenProductFactors(
        SsaValue rootValue,
        SsaRValue value,
        ProductArithmeticFamily family,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        List<SsaValue> factors,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaUseRValue use:
                FlattenProductValue(use.Value, family, expectedType, definitions, factors, visitingValueNames);
                return;
            case SsaBinaryRValue binary
                when binary.Type == expectedType
                     && TryGetProductFamily(binary.Operator, out var binaryFamily)
                     && binaryFamily == family:
                FlattenProductValue(binary.Left, family, expectedType, definitions, factors, visitingValueNames);
                FlattenProductValue(binary.Right, family, expectedType, definitions, factors, visitingValueNames);
                return;
        }

        factors.Add(rootValue);
    }

    private static void FlattenProductValue(
        SsaValue value,
        ProductArithmeticFamily family,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        List<SsaValue> factors,
        ISet<string> visitingValueNames)
    {
        if (value.Type != expectedType)
        {
            factors.Add(value);
            return;
        }

        if (value is SsaValueReference reference
            && definitions.TryGetValue(reference.Name, out var definition))
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                throw new InvalidOperationException(
                    $"Malformed SSA contains a cyclic value definition involving '{reference.Name}'.");
            }

            try
            {
                FlattenProductFactors(value, definition, family, expectedType, definitions, factors, visitingValueNames);
                return;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        factors.Add(value);
    }

    private static IReadOnlyList<ProductFactor> CompactProductFactors(
        IReadOnlyList<SsaValue> factors,
        StarkTypeSymbol resultType,
        out bool changed)
    {
        changed = false;
        var compacted = new List<ProductFactor>(factors.Count);
        var index = 0;

        while (index < factors.Count)
        {
            var current = factors[index];
            if (current is SsaIntegerConstant)
            {
                compacted.Add(new ProductFactor(current, Exponent: 1));
                index++;
                continue;
            }

            var runLength = 1;
            while (index + runLength < factors.Count
                   && factors[index + runLength] is not SsaIntegerConstant
                   && string.Equals(ValueKey(factors[index + runLength]), ValueKey(current), StringComparison.Ordinal))
            {
                runLength++;
            }

            if (runLength >= 2
                && CreateOrdinaryIntegerConstant(runLength, resultType, out _))
            {
                compacted.Add(new ProductFactor(current, runLength));
                changed = true;
                index += runLength;
                continue;
            }

            compacted.Add(new ProductFactor(current, Exponent: 1));
            index++;
        }

        return compacted;
    }

    private static void FlattenLinearTerms(
        SsaValue rootValue,
        SsaRValue value,
        LinearArithmeticFamily family,
        BigInteger coefficient,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        List<LinearTerm> terms,
        ISet<string> visitingValueNames)
    {
        switch (value)
        {
            case SsaUseRValue use:
                FlattenLinearValue(use.Value, family, coefficient, expectedType, definitions, terms, visitingValueNames);
                return;
            case SsaUnaryRValue { Operator: SsaUnaryOperator.Negate, Type: var type, Operand: var operand }
                when type == expectedType
                     && operand.Type == expectedType
                     && expectedType.Kind == StarkTypeKind.Integer:
                AddTerm(terms, operand, -coefficient, rootValue, coefficient);
                return;
            case SsaBinaryRValue binary
                when binary.Type == expectedType
                     && TryGetLinearFamily(binary.Operator, out var binaryFamily)
                     && binaryFamily == family:
                switch (binary.Operator)
                {
                    case SsaBinaryOperator.Add:
                    case SsaBinaryOperator.WrappingAdd:
                        FlattenLinearValue(binary.Left, family, coefficient, expectedType, definitions, terms, visitingValueNames);
                        FlattenLinearValue(binary.Right, family, coefficient, expectedType, definitions, terms, visitingValueNames);
                        return;
                    case SsaBinaryOperator.Subtract:
                    case SsaBinaryOperator.WrappingSubtract:
                        FlattenLinearValue(binary.Left, family, coefficient, expectedType, definitions, terms, visitingValueNames);
                        FlattenLinearValue(binary.Right, family, -coefficient, expectedType, definitions, terms, visitingValueNames);
                        return;
                }

                break;
            case SsaBinaryRValue binary
                when binary.Type == expectedType
                     && TryGetConstantCoefficientTerm(
                         rootValue,
                         binary,
                         family,
                         coefficient,
                         expectedType,
                         definitions,
                         out var term):
                terms.Add(term);
                return;
        }

        AddTerm(terms, rootValue, coefficient, rootValue, coefficient);
    }

    private static void FlattenLinearValue(
        SsaValue value,
        LinearArithmeticFamily family,
        BigInteger coefficient,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        List<LinearTerm> terms,
        ISet<string> visitingValueNames)
    {
        if (value.Type != expectedType)
        {
            AddTerm(terms, value, coefficient, value, coefficient);
            return;
        }

        if (value is SsaValueReference reference
            && definitions.TryGetValue(reference.Name, out var definition))
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                throw new InvalidOperationException(
                    $"Malformed SSA contains a cyclic value definition involving '{reference.Name}'.");
            }

            try
            {
                FlattenLinearTerms(value, definition, family, coefficient, expectedType, definitions, terms, visitingValueNames);
                return;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        AddTerm(terms, value, coefficient, value, coefficient);
    }

    private static void AddTerm(
        List<LinearTerm> terms,
        SsaValue value,
        BigInteger coefficient,
        SsaValue originalValue,
        BigInteger originalCoefficient)
    {
        if (!coefficient.IsZero)
        {
            terms.Add(new LinearTerm(value, coefficient, originalValue, originalCoefficient));
        }
    }

    private static bool TryGetConstantCoefficientTerm(
        SsaValue originalValue,
        SsaBinaryRValue binary,
        LinearArithmeticFamily family,
        BigInteger coefficient,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out LinearTerm term)
    {
        term = default;

        if (TryGetMultiplyOperatorFamily(binary.Operator, out var multiplyFamily)
            && multiplyFamily == family
            && TryGetConstantMultiplyOperand(binary, definitions, expectedType, out var baseValue, out var multiplier)
            && !multiplier.IsZero
            && CanUseConstantCoefficientTerm(baseValue, multiplier, family, definitions))
        {
            term = new LinearTerm(
                baseValue,
                coefficient * multiplier,
                originalValue,
                coefficient);
            return true;
        }

        if (family == LinearArithmeticFamily.Ordinary
            && binary.Operator == SsaBinaryOperator.ShiftLeft
            && binary.Left.Type == expectedType
            && TryResolveIntegerConstant(binary.Right, definitions, out var shiftValue)
            && TryGetShiftCoefficient(shiftValue, expectedType, out var shiftCoefficient)
            && CanProveScaledValueFits(binary.Left, shiftCoefficient, definitions))
        {
            term = new LinearTerm(
                binary.Left,
                coefficient * shiftCoefficient,
                originalValue,
                coefficient);
            return true;
        }

        return false;
    }

    private static bool TryGetMultiplyOperatorFamily(
        SsaBinaryOperator operatorKind,
        out LinearArithmeticFamily family)
    {
        switch (operatorKind)
        {
            case SsaBinaryOperator.Multiply:
                family = LinearArithmeticFamily.Ordinary;
                return true;
            case SsaBinaryOperator.WrappingMultiply:
                family = LinearArithmeticFamily.Wrapping;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static bool TryGetConstantMultiplyOperand(
        SsaBinaryRValue binary,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        StarkTypeSymbol expectedType,
        out SsaValue baseValue,
        out BigInteger multiplier)
    {
        if (binary.Left.Type == expectedType
            && TryResolveIntegerConstant(binary.Right, definitions, out multiplier))
        {
            baseValue = binary.Left;
            return true;
        }

        if (binary.Right.Type == expectedType
            && TryResolveIntegerConstant(binary.Left, definitions, out multiplier))
        {
            baseValue = binary.Right;
            return true;
        }

        baseValue = default!;
        multiplier = BigInteger.Zero;
        return false;
    }

    private static bool TryGetShiftCoefficient(
        BigInteger shiftValue,
        StarkTypeSymbol type,
        out BigInteger coefficient)
    {
        coefficient = BigInteger.Zero;
        if (shiftValue < BigInteger.Zero
            || type.BitWidth is not int bitWidth
            || bitWidth <= 0
            || shiftValue >= bitWidth
            || shiftValue > int.MaxValue)
        {
            return false;
        }

        coefficient = BigInteger.One << (int)shiftValue;
        return true;
    }

    private static bool CanUseConstantCoefficientTerm(
        SsaValue value,
        BigInteger coefficient,
        LinearArithmeticFamily family,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return family == LinearArithmeticFamily.Wrapping
               || CanProveScaledValueFits(value, coefficient, definitions);
    }

    private static IReadOnlyList<LinearTerm> CompactLinearTerms(
        IReadOnlyList<LinearTerm> terms,
        LinearArithmeticFamily family,
        StarkTypeSymbol resultType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out bool changed)
    {
        changed = false;
        var compacted = new List<LinearTerm>(terms.Count);
        var index = 0;

        while (index < terms.Count)
        {
            var current = terms[index];
            if (current.Value is SsaIntegerConstant)
            {
                var start = index;
                index++;
                while (index < terms.Count && terms[index].Value is SsaIntegerConstant)
                {
                    index++;
                }

                if (TryCompactConstantRun(terms, start, index, family, resultType, out var constantTerm))
                {
                    if (constantTerm is { } compactedConstant)
                    {
                        compacted.Add(compactedConstant);
                    }

                    changed = true;
                }
                else
                {
                    for (var termIndex = start; termIndex < index; termIndex++)
                    {
                        compacted.Add(RestoreOriginalTerm(terms[termIndex]));
                    }
                }

                continue;
            }

            var key = ValueKey(current.Value);
            var groupStart = index;
            index++;
            while (index < terms.Count
                   && terms[index].Value is not SsaIntegerConstant
                   && string.Equals(ValueKey(terms[index].Value), key, StringComparison.Ordinal))
            {
                index++;
            }

            if (TryCompactValueGroup(terms, groupStart, index, family, resultType, definitions, out var compactedTerm))
            {
                if (compactedTerm is { } term)
                {
                    compacted.Add(term);
                }

                changed = true;
            }
            else
            {
                for (var termIndex = groupStart; termIndex < index; termIndex++)
                {
                    compacted.Add(RestoreOriginalTerm(terms[termIndex]));
                }
            }
        }

        return compacted;
    }

    private static bool TryCompactConstantRun(
        IReadOnlyList<LinearTerm> terms,
        int start,
        int end,
        LinearArithmeticFamily family,
        StarkTypeSymbol resultType,
        out LinearTerm? compacted)
    {
        compacted = null;
        if (end - start < 2)
        {
            return false;
        }

        var sum = BigInteger.Zero;
        var hasPositiveContribution = false;
        var hasNegativeContribution = false;
        for (var index = start; index < end; index++)
        {
            var integer = (SsaIntegerConstant)terms[index].Value;
            var contribution = integer.Value * terms[index].Coefficient;
            sum += contribution;
            hasPositiveContribution |= contribution.Sign > 0;
            hasNegativeContribution |= contribution.Sign < 0;
        }

        if (family == LinearArithmeticFamily.Ordinary
            && hasPositiveContribution
            && hasNegativeContribution)
        {
            return false;
        }

        var constant = family == LinearArithmeticFamily.Wrapping
            ? CreateWrappingIntegerConstant(sum, resultType)
            : CreateOrdinaryIntegerConstant(sum, resultType, out var ordinaryConstant)
                ? ordinaryConstant
                : null;
        if (constant is null)
        {
            return false;
        }

        if (constant.Value.IsZero)
        {
            compacted = null;
            return true;
        }

        compacted = new LinearTerm(constant, BigInteger.One, constant, BigInteger.One);
        return true;
    }

    private static bool TryCompactValueGroup(
        IReadOnlyList<LinearTerm> terms,
        int start,
        int end,
        LinearArithmeticFamily family,
        StarkTypeSymbol resultType,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out LinearTerm? compacted)
    {
        compacted = null;
        if (end - start < 2)
        {
            return false;
        }

        var value = terms[start].Value;
        if (value.Type != resultType)
        {
            return false;
        }

        var coefficientSum = BigInteger.Zero;
        var allPositive = true;
        var allNegative = true;
        for (var index = start; index < end; index++)
        {
            var coefficient = terms[index].Coefficient;
            coefficientSum += coefficient;
            allPositive &= coefficient.Sign > 0;
            allNegative &= coefficient.Sign < 0;
        }

        if (!CanCompactCoefficientGroup(
                value,
                terms,
                start,
                end,
                coefficientSum,
                allPositive,
                allNegative,
                family,
                resultType,
                definitions))
        {
            return false;
        }

        if (coefficientSum.IsZero)
        {
            compacted = null;
            return true;
        }

        compacted = new LinearTerm(value, coefficientSum, value, coefficientSum);
        return true;
    }

    private static bool CanCompactCoefficientGroup(
        SsaValue value,
        IReadOnlyList<LinearTerm> terms,
        int start,
        int end,
        BigInteger coefficientSum,
        bool allPositive,
        bool allNegative,
        LinearArithmeticFamily family,
        StarkTypeSymbol resultType,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (family == LinearArithmeticFamily.Wrapping)
        {
            return true;
        }

        var magnitude = BigInteger.Abs(coefficientSum);
        if (!magnitude.IsZero
            && !CreateOrdinaryIntegerConstant(magnitude, resultType, out _))
        {
            return false;
        }

        if (allPositive)
        {
            return true;
        }

        if (allNegative)
        {
            return CanProveScaledValueFits(value, magnitude, definitions);
        }

        return CanProveCoefficientPrefixesFit(value, terms, start, end, definitions);
    }

    private static bool CanProveCoefficientPrefixesFit(
        SsaValue value,
        IReadOnlyList<LinearTerm> terms,
        int start,
        int end,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var coefficient = BigInteger.Zero;
        for (var index = start; index < end; index++)
        {
            coefficient += terms[index].Coefficient;
            if (!CanProveScaledValueFits(value, coefficient, definitions))
            {
                return false;
            }
        }

        return true;
    }

    private static LinearTerm RestoreOriginalTerm(LinearTerm term)
    {
        return new LinearTerm(
            term.OriginalValue,
            term.OriginalCoefficient,
            term.OriginalValue,
            term.OriginalCoefficient);
    }

    private static bool CanProveScaledValueFits(
        SsaValue value,
        BigInteger coefficient,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        if (coefficient.IsZero)
        {
            return true;
        }

        if (!TryGetStaticIntegerRange(value, definitions, new HashSet<string>(StringComparer.Ordinal), out var min, out var max)
            || value.Type.BitWidth is not int bitWidth
            || bitWidth <= 0)
        {
            return false;
        }

        var candidates = new[]
        {
            min * coefficient,
            max * coefficient
        };
        var productMin = candidates.Min();
        var productMax = candidates.Max();

        if (value.Type.IsUnsigned)
        {
            return productMin >= BigInteger.Zero
                && productMax < (BigInteger.One << bitWidth);
        }

        var signedMin = -(BigInteger.One << (bitWidth - 1));
        var signedMax = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
        return productMin >= signedMin && productMax <= signedMax;
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
            case SsaValueReference reference
                when visited.Add(reference.Name)
                     && definitions.TryGetValue(reference.Name, out var definition):
                switch (definition)
                {
                    case SsaUseRValue use:
                        return TryGetStaticIntegerRange(use.Value, definitions, visited, out min, out max);
                    case SsaConvertRValue convert when IsSameWidthIntegerConversion(convert):
                        return TryGetStaticIntegerRange(convert.Operand, definitions, visited, out min, out max);
                }

                break;
        }

        if (StarkTypeSymbols.TryGetEffectiveIntegerBounds(value.Type, out var rangeMin, out var rangeMax))
        {
            min = rangeMin;
            max = rangeMax;
            return true;
        }

        min = BigInteger.Zero;
        max = BigInteger.Zero;
        return false;
    }

    private static bool IsSameWidthIntegerConversion(SsaConvertRValue convert)
    {
        return convert.Operand.Type.Kind == StarkTypeKind.Integer
            && convert.TargetType.Kind == StarkTypeKind.Integer
            && convert.Operand.Type.BitWidth == convert.TargetType.BitWidth;
    }

    private static IReadOnlyList<SsaInstruction> EmitFoldedInstruction(
        SsaValueInstruction originalInstruction,
        LinearArithmeticFamily family,
        IReadOnlyList<LinearTerm> terms,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var instructions = new List<SsaInstruction>();
        var materializedTerms = new LinearTerm[terms.Count];
        for (var index = 0; index < terms.Count; index++)
        {
            materializedTerms[index] = MaterializeTerm(
                originalInstruction,
                terms[index],
                family,
                instructions,
                usedNames,
                ref temporaryCounter);
        }

        if (materializedTerms.Length == 0)
        {
            instructions.Add(originalInstruction);
            return instructions;
        }

        var rootValue = BuildFoldedValue(
            originalInstruction,
            family,
            materializedTerms,
            instructions,
            usedNames,
            ref temporaryCounter);
        instructions.Add(originalInstruction with { Value = rootValue });
        return instructions;
    }

    private static LinearTerm MaterializeTerm(
        SsaValueInstruction rootInstruction,
        LinearTerm term,
        LinearArithmeticFamily family,
        List<SsaInstruction> instructions,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var magnitude = BigInteger.Abs(term.Coefficient);
        if (magnitude <= BigInteger.One)
        {
            return term;
        }

        var coefficient = family == LinearArithmeticFamily.Wrapping
            ? CreateWrappingIntegerConstant(magnitude, term.Value.Type)
            : CreateOrdinaryIntegerConstant(magnitude, term.Value.Type, out var ordinaryCoefficient)
                ? ordinaryCoefficient
                : throw new InvalidOperationException(
                    $"Integer arithmetic folding selected multiplier '{magnitude}' that does not fit '{term.Value.Type.DisplayName}'.");
        var temporaryName = AllocateTemporaryName(rootInstruction.ResultName, "mul", usedNames, ref temporaryCounter);
        var temporaryValue = new SsaValueReference(temporaryName, term.Value.Type);
        var multiplyOperator = family == LinearArithmeticFamily.Wrapping
            ? SsaBinaryOperator.WrappingMultiply
            : SsaBinaryOperator.Multiply;
        instructions.Add(new SsaValueInstruction(
            temporaryName,
            new SsaBinaryRValue(
                multiplyOperator,
                term.Value,
                coefficient,
                term.Value.Type,
                $"{term.Value.Text} {OperatorText(multiplyOperator)} {coefficient.Text}"),
            rootInstruction.Location));

        return term with { Value = temporaryValue, Coefficient = new BigInteger(term.Coefficient.Sign) };
    }

    private static SsaRValue BuildFoldedValue(
        SsaValueInstruction rootInstruction,
        LinearArithmeticFamily family,
        IReadOnlyList<LinearTerm> terms,
        List<SsaInstruction> instructions,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var zero = new SsaIntegerConstant(BigInteger.Zero, rootInstruction.Value.Type);
        if (terms.Count == 0)
        {
            return new SsaUseRValue(zero);
        }

        SsaValue accumulator;
        var index = 0;

        if (terms[0].Coefficient.Sign > 0)
        {
            accumulator = terms[0].Value;
            index = 1;
        }
        else
        {
            accumulator = zero;
        }

        for (; index < terms.Count; index++)
        {
            var term = terms[index];
            var isLast = index == terms.Count - 1;
            var operatorKind = term.Coefficient.Sign > 0
                ? AddOperator(family)
                : SubtractOperator(family);
            var binary = new SsaBinaryRValue(
                operatorKind,
                accumulator,
                term.Value,
                rootInstruction.Value.Type,
                $"{accumulator.Text} {OperatorText(operatorKind)} {term.Value.Text}");

            if (isLast)
            {
                return binary;
            }

            var temporaryName = AllocateTemporaryName(rootInstruction.ResultName, "lin", usedNames, ref temporaryCounter);
            instructions.Add(new SsaValueInstruction(
                temporaryName,
                binary,
                rootInstruction.Location));
            accumulator = new SsaValueReference(temporaryName, rootInstruction.Value.Type);
        }

        return new SsaUseRValue(accumulator);
    }

    private static IReadOnlyList<SsaInstruction> EmitFoldedProductInstruction(
        SsaValueInstruction originalInstruction,
        ProductArithmeticFamily family,
        IReadOnlyList<ProductFactor> factors,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var instructions = new List<SsaInstruction>();
        var materializedFactors = new SsaValue[factors.Count];
        for (var index = 0; index < factors.Count; index++)
        {
            materializedFactors[index] = MaterializeProductFactor(
                originalInstruction,
                family,
                factors[index],
                instructions,
                usedNames,
                ref temporaryCounter);
        }

        var rootValue = BuildFoldedProductValue(
            originalInstruction,
            family,
            materializedFactors,
            instructions,
            usedNames,
            ref temporaryCounter);
        instructions.Add(originalInstruction with { Value = rootValue });
        return instructions;
    }

    private static SsaValue MaterializeProductFactor(
        SsaValueInstruction rootInstruction,
        ProductArithmeticFamily family,
        ProductFactor factor,
        List<SsaInstruction> instructions,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        if (factor.Exponent <= 1)
        {
            return factor.Value;
        }

        var exponent = CreateOrdinaryIntegerConstant(factor.Exponent, factor.Value.Type, out var exponentConstant)
            ? exponentConstant
            : throw new InvalidOperationException(
                $"Integer product folding selected exponent '{factor.Exponent}' that does not fit '{factor.Value.Type.DisplayName}'.");
        var temporaryName = AllocateTemporaryName(rootInstruction.ResultName, "pow", usedNames, ref temporaryCounter);
        instructions.Add(new SsaValueInstruction(
            temporaryName,
            new SsaBinaryRValue(
                family == ProductArithmeticFamily.Wrapping
                    ? SsaBinaryOperator.WrappingExponent
                    : SsaBinaryOperator.Exponent,
                factor.Value,
                exponent,
                factor.Value.Type,
                $"{factor.Value.Text} ** {exponent.Text}"),
            rootInstruction.Location));
        return new SsaValueReference(temporaryName, factor.Value.Type);
    }

    private static SsaRValue BuildFoldedProductValue(
        SsaValueInstruction rootInstruction,
        ProductArithmeticFamily family,
        IReadOnlyList<SsaValue> factors,
        List<SsaInstruction> instructions,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        if (factors.Count == 0)
        {
            var one = new SsaIntegerConstant(BigInteger.One, rootInstruction.Value.Type);
            return new SsaUseRValue(one);
        }

        var accumulator = factors[0];
        var multiplyOperator = family == ProductArithmeticFamily.Wrapping
            ? SsaBinaryOperator.WrappingMultiply
            : SsaBinaryOperator.Multiply;
        for (var index = 1; index < factors.Count; index++)
        {
            var factor = factors[index];
            var isLast = index == factors.Count - 1;
            var binary = new SsaBinaryRValue(
                multiplyOperator,
                accumulator,
                factor,
                rootInstruction.Value.Type,
                $"{accumulator.Text} {OperatorText(multiplyOperator)} {factor.Text}");

            if (isLast)
            {
                return binary;
            }

            var temporaryName = AllocateTemporaryName(rootInstruction.ResultName, "prod", usedNames, ref temporaryCounter);
            instructions.Add(new SsaValueInstruction(
                temporaryName,
                binary,
                rootInstruction.Location));
            accumulator = new SsaValueReference(temporaryName, rootInstruction.Value.Type);
        }

        return new SsaUseRValue(accumulator);
    }

    private static string AllocateTemporaryName(
        string rootName,
        string suffix,
        HashSet<string> usedNames,
        ref int temporaryCounter)
    {
        var sanitizedRoot = SanitizeName(rootName);
        while (true)
        {
            var candidate = $"{sanitizedRoot}_{suffix}_{temporaryCounter.ToString(CultureInfo.InvariantCulture)}";
            temporaryCounter++;
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        foreach (var character in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        if (builder.Length == 0 || char.IsDigit(builder[0]))
        {
            builder.Insert(0, "ssa_");
        }

        return builder.ToString();
    }

    private static bool TryGetLinearFamily(
        SsaBinaryOperator operatorKind,
        out LinearArithmeticFamily family)
    {
        switch (operatorKind)
        {
            case SsaBinaryOperator.Add:
            case SsaBinaryOperator.Subtract:
                family = LinearArithmeticFamily.Ordinary;
                return true;
            case SsaBinaryOperator.WrappingAdd:
            case SsaBinaryOperator.WrappingSubtract:
                family = LinearArithmeticFamily.Wrapping;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static bool TryGetProductFamily(
        SsaBinaryOperator operatorKind,
        out ProductArithmeticFamily family)
    {
        switch (operatorKind)
        {
            case SsaBinaryOperator.Multiply:
                family = ProductArithmeticFamily.Ordinary;
                return true;
            case SsaBinaryOperator.WrappingMultiply:
                family = ProductArithmeticFamily.Wrapping;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static SsaBinaryOperator AddOperator(LinearArithmeticFamily family)
    {
        return family == LinearArithmeticFamily.Wrapping
            ? SsaBinaryOperator.WrappingAdd
            : SsaBinaryOperator.Add;
    }

    private static SsaBinaryOperator SubtractOperator(LinearArithmeticFamily family)
    {
        return family == LinearArithmeticFamily.Wrapping
            ? SsaBinaryOperator.WrappingSubtract
            : SsaBinaryOperator.Subtract;
    }

    private static string OperatorText(SsaBinaryOperator operatorKind)
    {
        return operatorKind switch
        {
            SsaBinaryOperator.Add => "+",
            SsaBinaryOperator.Subtract => "-",
            SsaBinaryOperator.Multiply => "*",
            SsaBinaryOperator.WrappingAdd => "+%",
            SsaBinaryOperator.WrappingSubtract => "-%",
            SsaBinaryOperator.WrappingMultiply => "*%",
            SsaBinaryOperator.Exponent => "**",
            SsaBinaryOperator.WrappingExponent => "**%",
            _ => operatorKind.ToString()
        };
    }

    private static bool CreateOrdinaryIntegerConstant(
        BigInteger value,
        StarkTypeSymbol type,
        out SsaIntegerConstant constant)
    {
        if (StarkTypeSymbols.IntegerValueFitsEffectiveRange(value, type))
        {
            constant = new SsaIntegerConstant(value, type);
            return true;
        }

        constant = new SsaIntegerConstant(BigInteger.Zero, type);
        return false;
    }

    private static SsaIntegerConstant CreateWrappingIntegerConstant(
        BigInteger value,
        StarkTypeSymbol type)
    {
        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            return new SsaIntegerConstant(value, type);
        }

        var modulus = BigInteger.One << bitWidth;
        var wrapped = value % modulus;
        if (wrapped < BigInteger.Zero)
        {
            wrapped += modulus;
        }

        if (!type.IsUnsigned)
        {
            var signBit = BigInteger.One << (bitWidth - 1);
            if ((wrapped & signBit) != BigInteger.Zero)
            {
                wrapped -= modulus;
            }
        }

        return new SsaIntegerConstant(wrapped, type);
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out BigInteger constant)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference
                when definitions.TryGetValue(reference.Name, out var definition):
                switch (definition)
                {
                    case SsaUseRValue use:
                        return TryResolveIntegerConstant(use.Value, definitions, out constant);
                    case SsaConvertRValue convert when IsSameWidthIntegerConversion(convert):
                        return TryResolveIntegerConstant(convert.Operand, definitions, out constant);
                }

                break;
        }

        constant = BigInteger.Zero;
        return false;
    }

    private static string ValueKey(SsaValue value)
    {
        return value switch
        {
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

    private enum LinearArithmeticFamily
    {
        Ordinary,
        Wrapping
    }

    private enum ProductArithmeticFamily
    {
        Ordinary,
        Wrapping
    }

    private readonly record struct LinearTerm(
        SsaValue Value,
        BigInteger Coefficient,
        SsaValue OriginalValue,
        BigInteger OriginalCoefficient);

    private readonly record struct ProductFactor(
        SsaValue Value,
        int Exponent);
}
