using System.Globalization;
using System.Text;

namespace Stark.Compiler;

internal sealed class SsaConstGraphCallCseOptimizer
{
    private readonly FunctionEffectModel _effectModel;
    private readonly SemanticValidationModel? _semanticValidation;
    private readonly TypeCheckModel _typeModel;

    public SsaConstGraphCallCseOptimizer(
        FunctionEffectModel effectModel,
        SemanticValidationModel? semanticValidation,
        TypeCheckModel typeModel)
    {
        _effectModel = effectModel;
        _semanticValidation = semanticValidation;
        _typeModel = typeModel;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var functionsByName = module.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function, functionsByName);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        return changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;
    }

    private SsaFunction OptimizeFunction(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaFunction> functionsByName)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return function;
        }

        var definitions = BuildValueDefinitions(function);
        var constProvenanceLocalNames = CollectConstProvenanceLocalNames(function);
        var predecessors = BuildPredecessorMap(function);
        var exitAvailableCallsByBlock = new Dictionary<int, IReadOnlyDictionary<string, SsaValueReference>>();
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);

        foreach (var block in function.Blocks)
        {
            var availableCalls = new Dictionary<string, SsaValueReference>(
                TryGetSinglePredecessorAvailableCalls(block, predecessors, exitAvailableCallsByBlock),
                StringComparer.Ordinal);
            var instructions = new List<SsaInstruction>(block.Instructions.Count);

            foreach (var instruction in block.Instructions)
            {
                var rewritten = RewriteInstruction(instruction, replacements);
                if (!EqualityComparer<SsaInstruction>.Default.Equals(rewritten, instruction))
                {
                    changed = true;
                }

                if (rewritten is SsaValueInstruction { Value: SsaCallRValue call } valueInstruction
                    && TryCreateEligibleCallKey(
                        function,
                        call,
                        functionsByName,
                        definitions,
                        constProvenanceLocalNames,
                        replacements,
                        out var key))
                {
                    if (availableCalls.TryGetValue(key, out var existingValue))
                    {
                        replacements[valueInstruction.ResultName] = existingValue;
                        changed = true;
                        continue;
                    }

                    availableCalls[key] = new SsaValueReference(valueInstruction.ResultName, call.Type);
                }

                instructions.Add(rewritten);
            }

            blocks.Add(block with { Instructions = instructions.ToArray() });
            exitAvailableCallsByBlock[block.Id] = availableCalls;
        }

        if (!changed)
        {
            return function;
        }

        var optimized = function with { Blocks = blocks.ToArray() };
        return RewriteFunction(optimized, replacements);
    }

    private bool TryCreateEligibleCallKey(
        SsaFunction caller,
        SsaCallRValue call,
        IReadOnlyDictionary<string, SsaFunction> functionsByName,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        IReadOnlyDictionary<string, SsaValue> replacements,
        out string key)
    {
        key = string.Empty;

        if (!_effectModel.Functions.TryGetValue(call.FunctionName, out var effects)
            || !FunctionKindFacts.IsLaw(effects.Kind)
            || !effects.IsPure
            || !effects.NoSync
            || !effects.NoFree
            || !effects.NoUnwind
            || !effects.WillReturn
            || effects.IsFfi
            || !TryResolveMemoryEffects(call.FunctionName, effects, out var memoryEffects)
            || !memoryEffects.ReadsArgumentMemory
            || memoryEffects.WritesArgumentMemory
            || memoryEffects.CapturesArgumentMemory
            || memoryEffects.ReadsOtherMemory
            || memoryEffects.WritesOtherMemory
            || !TryResolveCalleeParameters(call.FunctionName, functionsByName, out var parameters))
        {
            return false;
        }

        var parameterEffects = TryResolveParameterEffects(call.FunctionName);
        var hasConstGraphRead = false;

        for (var index = 0; index < call.Arguments.Count; index++)
        {
            if (!IsReadMemoryBackedParameter(index, parameters, parameterEffects, memoryEffects))
            {
                continue;
            }

            var argument = RewriteValue(call.Arguments[index], replacements);
            var indirectAddress = call.IndirectArgumentAddresses is not null && index < call.IndirectArgumentAddresses.Count
                ? call.IndirectArgumentAddresses[index]
                : null;
            var indirectLocalName = call.IndirectArgumentLocalNames is not null && index < call.IndirectArgumentLocalNames.Count
                ? call.IndirectArgumentLocalNames[index]
                : null;

            if (indirectAddress is not null)
            {
                indirectAddress = RewriteValue(indirectAddress, replacements);
            }

            var hasConstProvenance = HasConstMemoryProvenance(
                caller,
                argument,
                definitions,
                constProvenanceLocalNames,
                new HashSet<string>(StringComparer.Ordinal))
                || indirectAddress is not null
                && HasConstMemoryProvenance(
                    caller,
                    indirectAddress,
                    definitions,
                    constProvenanceLocalNames,
                    new HashSet<string>(StringComparer.Ordinal))
                || indirectLocalName is not null
                && constProvenanceLocalNames.Contains(indirectLocalName);

            if (!hasConstProvenance)
            {
                return false;
            }

            hasConstGraphRead = true;
        }

        if (!hasConstGraphRead)
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.Append(call.FunctionName);
        builder.Append("|ret:");
        builder.Append(call.Type.DisplayName);
        builder.Append("|args:");
        builder.Append(call.Arguments.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in call.Arguments)
        {
            builder.Append('|');
            if (!TryAppendValueFingerprint(RewriteValue(argument, replacements), builder))
            {
                return false;
            }
        }

        builder.Append("|indirect-locals:");
        AppendNullableStringListFingerprint(call.IndirectArgumentLocalNames, builder);

        builder.Append("|indirect-addresses:");
        if (!TryAppendNullableValueListFingerprint(call.IndirectArgumentAddresses, replacements, builder))
        {
            return false;
        }

        key = builder.ToString();
        return true;
    }

    private bool TryResolveMemoryEffects(
        string functionName,
        FunctionEffectProfile effects,
        out FunctionMemoryEffectSummary memoryEffects)
    {
        if (_semanticValidation is not null
            && _semanticValidation.Functions.TryGetValue(functionName, out var summary)
            && summary.MemoryEffects is not null)
        {
            memoryEffects = summary.MemoryEffects;
            return true;
        }

        if (!effects.IsPure)
        {
            memoryEffects = new FunctionMemoryEffectSummary(
                ReadsArgumentMemory: true,
                WritesArgumentMemory: true,
                CapturesArgumentMemory: true,
                ReadsOtherMemory: true,
                WritesOtherMemory: true);
            return true;
        }

        memoryEffects = new FunctionMemoryEffectSummary(
            ReadsArgumentMemory: effects.ReadsArgumentMemory,
            WritesArgumentMemory: false,
            CapturesArgumentMemory: false);
        return true;
    }

    private IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? TryResolveParameterEffects(string functionName)
    {
        if (_semanticValidation is null
            || !_semanticValidation.Functions.TryGetValue(functionName, out var summary)
            || summary.Parameters is null)
        {
            return null;
        }

        return summary.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
    }

    private bool TryResolveCalleeParameters(
        string functionName,
        IReadOnlyDictionary<string, SsaFunction> functionsByName,
        out IReadOnlyList<TypedParameterSymbol> parameters)
    {
        if (functionsByName.TryGetValue(functionName, out var function))
        {
            parameters = function.Parameters;
            return true;
        }

        if (_typeModel.Functions.TryGetValue(functionName, out var signature))
        {
            parameters = signature.Parameters;
            return true;
        }

        parameters = [];
        return false;
    }

    private static bool IsReadMemoryBackedParameter(
        int index,
        IReadOnlyList<TypedParameterSymbol> parameters,
        IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
        FunctionMemoryEffectSummary memoryEffects)
    {
        if (index >= parameters.Count)
        {
            return memoryEffects.ReadsArgumentMemory;
        }

        var parameter = parameters[index];
        if (parameterEffects is not null
            && parameterEffects.TryGetValue(parameter.Name, out var effect))
        {
            return effect.IsMemoryBacked && effect.Reads;
        }

        return memoryEffects.ReadsArgumentMemory
            && ParameterMemoryContractFacts.IsMemoryBacked(parameter.Type);
    }

    private bool HasConstMemoryProvenance(
        SsaFunction function,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        ISet<string> visitedValueNames)
    {
        return value switch
        {
            SsaStringConstant => true,
            SsaTextDataAddressValue => true,
            SsaGlobalAddressValue globalAddress => IsPermanentConstGlobalName(globalAddress.GlobalName),
            SsaValueReference reference => HasConstMemoryProvenance(
                function,
                reference,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            _ => false
        };
    }

    private bool HasConstMemoryProvenance(
        SsaFunction function,
        SsaValueReference reference,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        ISet<string> visitedValueNames)
    {
        if (IsConstParameterValueReference(function, reference))
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
            SsaUseRValue use => HasConstMemoryProvenance(
                function,
                use.Value,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                                            && convert.TargetType.Kind == StarkTypeKind.RawPointer
                => HasConstMemoryProvenance(
                    function,
                    convert.Operand,
                    definitions,
                    constProvenanceLocalNames,
                    visitedValueNames),
            SsaAddressOfParameterRValue addressOfParameter => IsConstParameter(function, addressOfParameter.ParameterName),
            SsaAddressOfLocalRValue addressOfLocal => constProvenanceLocalNames.Contains(addressOfLocal.LocalName),
            SsaFieldAddressRValue fieldAddress => HasConstMemoryProvenance(
                function,
                fieldAddress.Address,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaElementAddressRValue elementAddress => HasConstMemoryProvenance(
                function,
                elementAddress.Address,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => HasConstMemoryProvenance(
                function,
                sliceElementAddress.Slice,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaMakeSliceFromPointerRValue makeSlice => HasConstMemoryProvenance(
                function,
                makeSlice.Pointer,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice => constProvenanceLocalNames.Contains(makeSlice.LocalName),
            SsaTextSliceRValue textSlice => HasConstMemoryProvenance(
                function,
                textSlice.TextValue,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaExtractFieldRValue extractField => HasConstMemoryProvenance(
                function,
                extractField.Target,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaExtractIndexRValue extractIndex => HasConstMemoryProvenance(
                function,
                extractIndex.Target,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaLoadGlobalRValue loadGlobal => IsPermanentConstGlobalName(loadGlobal.GlobalName),
            SsaLoadLocalRValue loadLocal => constProvenanceLocalNames.Contains(loadLocal.LocalName),
            SsaLoadIndirectRValue loadIndirect => HasConstMemoryProvenance(
                function,
                loadIndirect.Address,
                definitions,
                constProvenanceLocalNames,
                visitedValueNames),
            _ => false
        };
    }

    private bool IsPermanentConstGlobalName(string globalName)
    {
        return _typeModel.Globals.TryGetValue(globalName, out var global)
            && ConstProvenanceFacts.HasPermanentConstProvenance(global.ConstProvenance);
    }

    private static bool IsConstParameterValueReference(SsaFunction function, SsaValueReference reference)
    {
        const string prefix = "arg_";
        return reference.Name.StartsWith(prefix, StringComparison.Ordinal)
            && IsConstParameter(function, reference.Name[prefix.Length..]);
    }

    private static bool IsConstParameter(SsaFunction function, string parameterName)
    {
        return function.Parameters.Any(parameter =>
            string.Equals(parameter.Name, parameterName, StringComparison.Ordinal)
            && parameter.IsConst);
    }

    private static IReadOnlyDictionary<string, SsaRValue> BuildValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static HashSet<string> CollectConstProvenanceLocalNames(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaAllocateLocalInstruction>()
            .Where(static allocateLocal =>
                allocateLocal.HasConstProvenance
                || ConstProvenanceFacts.HasPermanentConstProvenance(allocateLocal.ConstProvenance))
            .Select(static allocateLocal => allocateLocal.LocalName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, SsaValueReference> TryGetSinglePredecessorAvailableCalls(
        SsaBasicBlock block,
        IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueReference>> exitAvailableCallsByBlock)
    {
        return predecessors.TryGetValue(block.Id, out var blockPredecessors)
               && blockPredecessors.Count == 1
               && exitAvailableCallsByBlock.TryGetValue(blockPredecessors[0], out var predecessorExitAvailableCalls)
            ? predecessorExitAvailableCalls
            : new Dictionary<string, SsaValueReference>(StringComparer.Ordinal);
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

        if (terminator.DefaultTarget is { } defaultTarget)
        {
            yield return defaultTarget;
        }
    }

    private static SsaFunction RewriteFunction(
        SsaFunction function,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        if (replacements.Count == 0)
        {
            return function;
        }

        var blocks = function.Blocks
            .Select(block => block with
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
            })
            .ToArray();

        return function with { Blocks = blocks };
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
            SsaCallInstruction call => call with
            {
                Arguments = RewriteValues(call.Arguments, replacements),
                IndirectArgumentAddresses = RewriteNullableValues(call.IndirectArgumentAddresses, replacements)
            },
            SsaIndirectCallInstruction call => call with
            {
                Target = RewriteValue(call.Target, replacements),
                Arguments = RewriteValues(call.Arguments, replacements),
                IndirectArgumentAddresses = RewriteNullableValues(call.IndirectArgumentAddresses, replacements)
            },
            SsaStoreLocalInstruction storeLocal => storeLocal with
            {
                Value = RewriteValue(storeLocal.Value, replacements)
            },
            SsaStoreIndirectInstruction storeIndirect => storeIndirect with
            {
                Address = RewriteValue(storeIndirect.Address, replacements),
                Value = RewriteValue(storeIndirect.Value, replacements)
            },
            SsaCopyMemoryInstruction copyMemory => copyMemory with
            {
                DestinationAddress = RewriteValue(copyMemory.DestinationAddress, replacements),
                SourceAddress = RewriteValue(copyMemory.SourceAddress, replacements)
            },
            SsaStoreGlobalInstruction storeGlobal => storeGlobal with
            {
                Value = RewriteValue(storeGlobal.Value, replacements)
            },
            _ => instruction
        };
    }

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return terminator with
        {
            Condition = terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
            Value = terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
            SwitchCases = terminator.SwitchCases is null
                ? null
                : terminator.SwitchCases
                    .Select(switchCase => switchCase with
                    {
                        MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                    })
                    .ToArray()
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return value switch
        {
            SsaUseRValue use => new SsaUseRValue(RewriteValue(use.Value, replacements)),
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
                Arguments = RewriteValues(call.Arguments, replacements),
                IndirectArgumentAddresses = RewriteNullableValues(call.IndirectArgumentAddresses, replacements)
            },
            SsaIndirectCallRValue call => call with
            {
                Target = RewriteValue(call.Target, replacements),
                Arguments = RewriteValues(call.Arguments, replacements),
                IndirectArgumentAddresses = RewriteNullableValues(call.IndirectArgumentAddresses, replacements)
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
            SsaMakeSliceFromPointerRValue makeSlice => makeSlice with
            {
                Pointer = RewriteValue(makeSlice.Pointer, replacements),
                Length = RewriteValue(makeSlice.Length, replacements)
            },
            SsaDynamicStorageAllocationRValue allocation => allocation with
            {
                Capacity = RewriteValue(allocation.Capacity, replacements)
            },
            SsaDynamicStorageFreeRValue free => free with
            {
                Storage = RewriteValue(free.Storage, replacements)
            },
            SsaHeapStorageFreeRValue free => free with
            {
                Pointer = RewriteValue(free.Pointer, replacements)
            },
            SsaDynamicStorageReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveCapacityRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                TargetCapacity = RewriteValue(reserve.TargetCapacity, replacements)
            },
            SsaDynamicStorageMoveLastRValue moveLast => moveLast with
            {
                StorageAddress = RewriteValue(moveLast.StorageAddress, replacements)
            },
            SsaDynamicStorageMoveAtRValue moveAt => moveAt with
            {
                StorageAddress = RewriteValue(moveAt.StorageAddress, replacements),
                Index = RewriteValue(moveAt.Index, replacements)
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

    private static IReadOnlyList<SsaValue> RewriteValues(
        IReadOnlyList<SsaValue> values,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return values.Select(value => RewriteValue(value, replacements)).ToArray();
    }

    private static IReadOnlyList<SsaValue?>? RewriteNullableValues(
        IReadOnlyList<SsaValue?>? values,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return values?.Select(value => value is null ? null : RewriteValue(value, replacements)).ToArray();
    }

    private static SsaValue RewriteValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        while (value is SsaValueReference reference
               && replacements.TryGetValue(reference.Name, out var replacement))
        {
            value = replacement;
        }

        return value;
    }

    private static bool TryAppendNullableValueListFingerprint(
        IReadOnlyList<SsaValue?>? values,
        IReadOnlyDictionary<string, SsaValue> replacements,
        StringBuilder builder)
    {
        if (values is null)
        {
            builder.Append("null");
            return true;
        }

        builder.Append(values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in values)
        {
            builder.Append('|');
            if (value is null)
            {
                builder.Append("null");
                continue;
            }

            if (!TryAppendValueFingerprint(RewriteValue(value, replacements), builder))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendNullableStringListFingerprint(
        IReadOnlyList<string?>? values,
        StringBuilder builder)
    {
        if (values is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append(values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in values)
        {
            builder.Append('|');
            builder.Append(value ?? "null");
        }
    }

    private static bool TryAppendValueFingerprint(SsaValue value, StringBuilder builder)
    {
        builder.Append(value.GetType().Name);
        builder.Append(':');
        builder.Append(value.Type.DisplayName);
        builder.Append(':');

        switch (value)
        {
            case SsaValueReference reference:
                builder.Append(reference.Name);
                return true;
            case SsaIntegerConstant integer:
                builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                return true;
            case SsaFloatConstant floating:
                builder.Append(floating.LiteralText);
                return true;
            case SsaStringConstant text:
                builder.Append(text.LiteralText);
                return true;
            case SsaTextDataAddressValue textData:
                builder.Append(textData.TextType.DisplayName);
                builder.Append(':');
                builder.Append(textData.LiteralText);
                return true;
            case SsaBoolConstant boolean:
                builder.Append(boolean.Value ? "true" : "false");
                return true;
            case SsaNullConstant:
                builder.Append("null");
                return true;
            case SsaGlobalAddressValue globalAddress:
                builder.Append(globalAddress.GlobalName);
                builder.Append(':');
                builder.Append(globalAddress.PointeeType.DisplayName);
                return true;
            case SsaFunctionAddressValue functionAddress:
                builder.Append(functionAddress.FunctionName);
                return true;
            case SsaClosureValue closure:
                builder.Append(closure.InvokeFunctionName);
                return true;
            case SsaZeroInitializerValue:
                builder.Append("zero");
                return true;
            case SsaUndefValue:
                return false;
            default:
                return false;
        }
    }
}
