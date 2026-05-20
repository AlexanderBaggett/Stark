namespace Stark.Compiler;

internal sealed class SsaConstLookupTableOptimizer
{
    private readonly TypeCheckModel _typeModel;

    public SsaConstLookupTableOptimizer(TypeCheckModel typeModel)
    {
        _typeModel = typeModel;
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

    private SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var definitions = CollectValueDefinitions(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block => RewriteBlock(block, definitions, ref changed))
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private SsaBasicBlock RewriteBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ref bool changed)
    {
        var blockChanged = false;
        var instructions = new SsaInstruction[block.Instructions.Count];
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (instruction is SsaValueInstruction
                {
                    Value: SsaExtractIndexRValue
                    {
                        OperationFamily: IndexedElementOperationFamily.FixedArrayElement
                    } extractIndex
                } valueInstruction
                && TryFoldConstLookup(extractIndex, definitions, out var folded))
            {
                instructions[index] = valueInstruction with
                {
                    Value = new SsaUseRValue(folded)
                };
                changed = true;
                blockChanged = true;
                continue;
            }

            if (instruction is SsaValueInstruction
                {
                    Value: SsaLoadIndirectRValue loadIndirect
                } loadInstruction
                && TryFoldConstLookupLoad(loadIndirect, definitions, out folded))
            {
                instructions[index] = loadInstruction with
                {
                    Value = new SsaUseRValue(folded)
                };
                changed = true;
                blockChanged = true;
                continue;
            }

            instructions[index] = instruction;
        }

        return blockChanged
            ? block with { Instructions = instructions }
            : block;
    }

    private bool TryFoldConstLookup(
        SsaExtractIndexRValue extractIndex,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue folded)
    {
        folded = default!;

        if (!TryResolveLoadedGlobal(
                extractIndex.Target,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var globalName)
            || !_typeModel.Globals.TryGetValue(globalName, out var global)
            || global.BindingKind != GlobalBindingKind.Const
            || global.ConstantInitializer is not
            {
                Kind: TypedConstantInitializerKind.FixedArray
            } initializer
            || extractIndex.ElementIndex < 0
            || extractIndex.ElementIndex >= initializer.ElementValues.Count
            || !TryCreateSsaValue(initializer.ElementValues[extractIndex.ElementIndex], extractIndex.Type, out folded))
        {
            return false;
        }

        return true;
    }

    private bool TryFoldConstLookupLoad(
        SsaLoadIndirectRValue loadIndirect,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out SsaValue folded)
    {
        folded = default!;

        if (!TryResolveConstFixedArrayElementAddress(
                loadIndirect.Address,
                definitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var globalName,
                out var elementIndex)
            || !_typeModel.Globals.TryGetValue(globalName, out var global)
            || global.BindingKind != GlobalBindingKind.Const
            || global.ConstantInitializer is not
            {
                Kind: TypedConstantInitializerKind.FixedArray
            } initializer
            || elementIndex < 0
            || elementIndex >= initializer.ElementValues.Count
            || !TryCreateSsaValue(initializer.ElementValues[elementIndex], loadIndirect.Type, out folded))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolveLoadedGlobal(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName)
    {
        globalName = string.Empty;
        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveLoadedGlobal(definition, definitions, visitedValueNames, out globalName);
            default:
                return false;
        }
    }

    private static bool TryResolveLoadedGlobal(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName)
    {
        globalName = string.Empty;
        return value switch
        {
            SsaLoadGlobalRValue loadGlobal => Assign(loadGlobal.GlobalName, out globalName),
            SsaUseRValue use => TryResolveLoadedGlobal(use.Value, definitions, visitedValueNames, out globalName),
            _ => false
        };
    }

    private static bool TryResolveConstFixedArrayElementAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName,
        out int elementIndex)
    {
        globalName = string.Empty;
        elementIndex = -1;

        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveConstFixedArrayElementAddress(
                    definition,
                    definitions,
                    visitedValueNames,
                    out globalName,
                    out elementIndex);
            default:
                return false;
        }
    }

    private static bool TryResolveConstFixedArrayElementAddress(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName,
        out int elementIndex)
    {
        globalName = string.Empty;
        elementIndex = -1;

        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveConstFixedArrayElementAddress(
                    use.Value,
                    definitions,
                    visitedValueNames,
                    out globalName,
                    out elementIndex);

            case SsaElementAddressRValue elementAddress
                when elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray:
                if (!TryResolveGlobalAddress(
                        elementAddress.Address,
                        definitions,
                        visitedValueNames,
                        out globalName)
                    || !TryResolveElementIndex(elementAddress, definitions, visitedValueNames, out elementIndex))
                {
                    return false;
                }

                return true;

            case SsaSliceElementAddressRValue sliceElementAddress:
                if (!TryResolveIntegerConstant(
                        sliceElementAddress.Index,
                        definitions,
                        visitedValueNames,
                        out var relativeIndex)
                    || relativeIndex < 0
                    || relativeIndex > int.MaxValue
                    || !TryResolveConstFixedArraySlice(
                        sliceElementAddress.Slice,
                        definitions,
                        visitedValueNames,
                        out globalName,
                        out var startIndex,
                        out var length)
                    || relativeIndex >= length
                    || startIndex > int.MaxValue - (int)relativeIndex)
                {
                    return false;
                }

                elementIndex = startIndex + (int)relativeIndex;
                return true;

            default:
                return false;
        }
    }

    private static bool TryResolveConstFixedArraySlice(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName,
        out int startIndex,
        out int length)
    {
        globalName = string.Empty;
        startIndex = -1;
        length = -1;

        switch (value)
        {
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveConstFixedArraySlice(
                    definition,
                    definitions,
                    visitedValueNames,
                    out globalName,
                    out startIndex,
                    out length);
            default:
                return false;
        }
    }

    private static bool TryResolveConstFixedArraySlice(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName,
        out int startIndex,
        out int length)
    {
        globalName = string.Empty;
        startIndex = -1;
        length = -1;

        switch (value)
        {
            case SsaUseRValue use:
                return TryResolveConstFixedArraySlice(
                    use.Value,
                    definitions,
                    visitedValueNames,
                    out globalName,
                    out startIndex,
                    out length);

            case SsaMakeSliceFromPointerRValue makeSlice:
                if (!TryResolveConstFixedArrayElementAddress(
                        makeSlice.Pointer,
                        definitions,
                        visitedValueNames,
                        out globalName,
                        out startIndex)
                    || !TryResolveIntegerConstant(
                        makeSlice.Length,
                        definitions,
                        visitedValueNames,
                        out var resolvedLength)
                    || resolvedLength < 0
                    || resolvedLength > int.MaxValue)
                {
                    return false;
                }

                length = (int)resolvedLength;
                return true;

            default:
                return false;
        }
    }

    private static bool TryResolveGlobalAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName)
    {
        globalName = string.Empty;

        switch (value)
        {
            case SsaGlobalAddressValue globalAddress:
                globalName = globalAddress.GlobalName;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveGlobalAddress(definition, definitions, visitedValueNames, out globalName);
            default:
                return false;
        }
    }

    private static bool TryResolveGlobalAddress(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out string globalName)
    {
        globalName = string.Empty;
        return value switch
        {
            SsaUseRValue use => TryResolveGlobalAddress(use.Value, definitions, visitedValueNames, out globalName),
            SsaConvertRValue convert => TryResolveGlobalAddress(convert.Operand, definitions, visitedValueNames, out globalName),
            _ => false
        };
    }

    private static bool TryResolveElementIndex(
        SsaElementAddressRValue elementAddress,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out int elementIndex)
    {
        elementIndex = -1;

        if (elementAddress.ConstantIndex is int constantIndex)
        {
            elementIndex = constantIndex;
            return true;
        }

        if (elementAddress.Index is null
            || !TryResolveIntegerConstant(elementAddress.Index, definitions, visitedValueNames, out var resolved)
            || resolved < 0
            || resolved > int.MaxValue)
        {
            return false;
        }

        elementIndex = (int)resolved;
        return true;
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out System.Numerics.BigInteger constant)
    {
        constant = default;

        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference:
                if (!visitedValueNames.Add(reference.Name)
                    || !definitions.TryGetValue(reference.Name, out var definition))
                {
                    return false;
                }

                return TryResolveIntegerConstant(definition, definitions, visitedValueNames, out constant);
            default:
                return false;
        }
    }

    private static bool TryResolveIntegerConstant(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        ISet<string> visitedValueNames,
        out System.Numerics.BigInteger constant)
    {
        constant = default;
        return value switch
        {
            SsaUseRValue use => TryResolveIntegerConstant(use.Value, definitions, visitedValueNames, out constant),
            SsaConvertRValue convert => TryResolveIntegerConstant(convert.Operand, definitions, visitedValueNames, out constant),
            _ => false
        };
    }

    private static bool TryCreateSsaValue(
        TypedConstantInitializer initializer,
        StarkTypeSymbol expectedType,
        out SsaValue value)
    {
        value = initializer.Kind switch
        {
            TypedConstantInitializerKind.Integer when initializer.IntegerValue is { } integerValue
                => new SsaIntegerConstant(integerValue, expectedType),
            TypedConstantInitializerKind.Float when initializer.FloatLiteralText is not null
                => new SsaFloatConstant(initializer.FloatLiteralText, expectedType),
            TypedConstantInitializerKind.Bool when expectedType.Kind == StarkTypeKind.Bool
                                        && initializer.BoolValue is { } boolValue
                => new SsaBoolConstant(boolValue),
            TypedConstantInitializerKind.Text when initializer.TextLiteralText is not null
                => new SsaStringConstant(initializer.TextLiteralText, expectedType),
            TypedConstantInitializerKind.Null => new SsaNullConstant(expectedType),
            _ => default!
        };

        return value is not null;
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

    private static bool Assign(string source, out string destination)
    {
        destination = source;
        return true;
    }
}
