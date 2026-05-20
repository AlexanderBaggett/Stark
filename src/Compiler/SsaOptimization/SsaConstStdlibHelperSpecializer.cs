using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaConstStdlibHelperSpecializer
{
    private readonly TypeCheckModel _typeModel;
    private readonly LlvmTargetInfo? _targetInfo;

    public SsaConstStdlibHelperSpecializer(
        TypeCheckModel typeModel,
        LlvmTargetInfo? targetInfo)
    {
        _typeModel = typeModel;
        _targetInfo = targetInfo;
    }

    public SsaIrModule Optimize(
        SsaIrModule module,
        SsaValueFactModel facts)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = facts.Functions.TryGetValue(function.Name, out var functionFacts)
                    ? OptimizeFunction(function, module.ModuleName, functionFacts)
                    : function;
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
        string moduleName,
        SsaFunctionFactModel facts)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var valueDefinitions = CollectValueDefinitions(function);
        var constProvenanceLocalNames = CollectConstProvenanceLocalNames(function);
        var usedNames = CollectDefinedValueNames(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block => RewriteBlock(
                function,
                moduleName,
                facts,
                valueDefinitions,
                constProvenanceLocalNames,
                usedNames,
                block,
                ref changed))
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private SsaBasicBlock RewriteBlock(
        SsaFunction function,
        string moduleName,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        ISet<string> usedNames,
        SsaBasicBlock block,
        ref bool changed)
    {
        var blockChanged = false;
        var instructions = new List<SsaInstruction>(block.Instructions.Count);
        foreach (var instruction in block.Instructions)
        {
            if (instruction is SsaValueInstruction
                {
                    Value: SsaCallRValue call
                } valueInstruction
                && TryRewriteCall(
                    function,
                    moduleName,
                    facts,
                    valueDefinitions,
                    constProvenanceLocalNames,
                    usedNames,
                    valueInstruction,
                    call,
                    out var replacements))
            {
                instructions.AddRange(replacements);
                changed = true;
                blockChanged = true;
                continue;
            }

            instructions.Add(instruction);
        }

        return blockChanged
            ? block with { Instructions = instructions }
            : block;
    }

    private bool TryRewriteCall(
        SsaFunction function,
        string moduleName,
        SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        ISet<string> usedNames,
        SsaValueInstruction valueInstruction,
        SsaCallRValue call,
        out IReadOnlyList<SsaInstruction> replacements)
    {
        replacements = [];

        if (TryRetargetConstTextVariant(
            function,
            moduleName,
            valueDefinitions,
            constProvenanceLocalNames,
            valueInstruction,
            call,
            out var textRetargeted))
        {
            replacements = [textRetargeted];
            return true;
        }

        if (TryRetargetConstPathVariant(
            function,
            moduleName,
            valueDefinitions,
            constProvenanceLocalNames,
            valueInstruction,
            call,
            out var retargeted))
        {
            replacements = [retargeted];
            return true;
        }

        var isPathFactsCall = IsPathFactsCall(call.FunctionName, moduleName);
        var isPathProjectionCall = call.Type.Kind == StarkTypeKind.Ascii
                                   && IsAnyPathProjectionCall(call.FunctionName, moduleName);
        if (!isPathFactsCall && !isPathProjectionCall)
        {
            return false;
        }

        if (call.Arguments.Count != 1
            || !TryGetKnownAsciiLiteralPayload(
                call.Arguments[0],
                facts,
                function,
                valueDefinitions,
                new HashSet<string>(StringComparer.Ordinal),
                out var pathBytes,
                out var pathLiteralText))
        {
            return false;
        }

        if (isPathFactsCall
            && TryResolvePathFactsType(call.Type, out var pathFactsType)
            && TryComputePathFacts(pathBytes, pathLiteralText, out var pathFacts))
        {
            replacements = RewritePathFactsCall(valueInstruction, call, pathFactsType, pathFacts, usedNames);
            return true;
        }

        if (isPathProjectionCall
            && TryComputePathFacts(pathBytes, pathLiteralText, out pathFacts)
            && TryResolvePathProjection(call.FunctionName, moduleName, pathBytes, pathFacts, out var projection))
        {
            replacements =
            [
                valueInstruction with
                {
                    Value = new SsaUseRValue(new SsaStringConstant(projection.LiteralText, call.Type))
                }
            ];
            return true;
        }

        return false;
    }

    private bool TryRetargetConstTextVariant(
        SsaFunction function,
        string moduleName,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        SsaValueInstruction valueInstruction,
        SsaCallRValue call,
        out SsaValueInstruction retargeted)
    {
        retargeted = valueInstruction;
        if (!TryResolveConstTextVariant(call.FunctionName, moduleName, out var constVariant)
            || call.Arguments.Count <= constVariant.SourceArgumentIndexes.Max()
            || !AllArgumentsHaveConstMemoryProvenance(
                function,
                call.Arguments,
                constVariant.SourceArgumentIndexes,
                valueDefinitions,
                constProvenanceLocalNames)
            || !TryResolveRetargetSignature(
                call.FunctionName,
                moduleName,
                "System.Text",
                constVariant.TargetSimpleName,
                call,
                constVariant.SourceArgumentIndexes,
                out var signature))
        {
            return false;
        }

        retargeted = valueInstruction with
        {
            Value = call with
            {
                FunctionName = signature.Name,
                Text = RetargetCallText(call.Text, constVariant.TargetSimpleName)
            }
        };
        return true;
    }

    private bool TryRetargetConstPathVariant(
        SsaFunction function,
        string moduleName,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        SsaValueInstruction valueInstruction,
        SsaCallRValue call,
        out SsaValueInstruction retargeted)
    {
        retargeted = valueInstruction;
        if (!TryResolveConstPathVariant(call.FunctionName, moduleName, out var constVariant, out var sourceArgumentIndexes)
            || call.Arguments.Count <= sourceArgumentIndexes.Max()
            || !AllArgumentsHaveConstMemoryProvenance(
                function,
                call.Arguments,
                sourceArgumentIndexes,
                valueDefinitions,
                constProvenanceLocalNames)
            || !TryResolveRetargetSignature(
                call.FunctionName,
                moduleName,
                "System.IO.Path",
                constVariant.SimpleName,
                call,
                sourceArgumentIndexes,
                out var signature))
        {
            return false;
        }

        retargeted = valueInstruction with
        {
            Value = call with
            {
                FunctionName = signature.Name,
                Text = RetargetCallText(call.Text, constVariant.SimpleName)
            }
        };
        return true;
    }

    private bool AllArgumentsHaveConstMemoryProvenance(
        SsaFunction function,
        IReadOnlyList<SsaValue> arguments,
        IReadOnlyList<int> argumentIndexes,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames)
    {
        foreach (var argumentIndex in argumentIndexes)
        {
            if (argumentIndex < 0
                || argumentIndex >= arguments.Count
                || !HasConstMemoryProvenance(
                    function,
                    arguments[argumentIndex],
                    valueDefinitions,
                    constProvenanceLocalNames,
                    new HashSet<string>(StringComparer.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasConstMemoryProvenance(
        SsaFunction function,
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
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
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            _ => false
        };
    }

    private bool HasConstMemoryProvenance(
        SsaFunction function,
        SsaValueReference reference,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        IReadOnlySet<string> constProvenanceLocalNames,
        ISet<string> visitedValueNames)
    {
        if (IsConstParameterValueReference(function, reference))
        {
            return true;
        }

        if (!visitedValueNames.Add(reference.Name)
            || !valueDefinitions.TryGetValue(reference.Name, out var definition))
        {
            return false;
        }

        return definition switch
        {
            SsaUseRValue use => HasConstMemoryProvenance(
                function,
                use.Value,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaConvertRValue convert when CanPreserveConstProvenanceThroughConvert(convert)
                => HasConstMemoryProvenance(
                    function,
                    convert.Operand,
                    valueDefinitions,
                    constProvenanceLocalNames,
                    visitedValueNames),
            SsaAddressOfParameterRValue addressOfParameter => IsConstParameter(function, addressOfParameter.ParameterName),
            SsaAddressOfLocalRValue addressOfLocal => constProvenanceLocalNames.Contains(addressOfLocal.LocalName),
            SsaFieldAddressRValue fieldAddress => HasConstMemoryProvenance(
                function,
                fieldAddress.Address,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaElementAddressRValue elementAddress => HasConstMemoryProvenance(
                function,
                elementAddress.Address,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaSliceElementAddressRValue sliceElementAddress => HasConstMemoryProvenance(
                function,
                sliceElementAddress.Slice,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaMakeSliceFromPointerRValue makeSlice => HasConstMemoryProvenance(
                function,
                makeSlice.Pointer,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaMakeSliceFromLocalRValue makeSlice => constProvenanceLocalNames.Contains(makeSlice.LocalName),
            SsaTextSliceRValue textSlice => HasConstMemoryProvenance(
                function,
                textSlice.TextValue,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaExtractFieldRValue extractField => HasConstMemoryProvenance(
                function,
                extractField.Target,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaExtractIndexRValue extractIndex => HasConstMemoryProvenance(
                function,
                extractIndex.Target,
                valueDefinitions,
                constProvenanceLocalNames,
                visitedValueNames),
            SsaLoadGlobalRValue loadGlobal => IsPermanentConstGlobalName(loadGlobal.GlobalName),
            SsaLoadLocalRValue loadLocal => constProvenanceLocalNames.Contains(loadLocal.LocalName),
            SsaLoadIndirectRValue loadIndirect => HasConstMemoryProvenance(
                function,
                loadIndirect.Address,
                valueDefinitions,
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

    private static bool CanPreserveConstProvenanceThroughConvert(SsaConvertRValue convert)
    {
        return (convert.Operand.Type.Kind == StarkTypeKind.RawPointer
                && convert.TargetType.Kind == StarkTypeKind.RawPointer)
               || (convert.Operand.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                   && convert.TargetType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode);
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

    private bool TryResolveRetargetSignature(
        string sourceFunctionName,
        string moduleName,
        string qualifiedModuleName,
        string targetSimpleName,
        SsaCallRValue call,
        IReadOnlyList<int> sourceArgumentIndexes,
        out TypedFunctionSignature signature)
    {
        signature = default!;
        var matches = new List<TypedFunctionSignature>();
        foreach (var candidate in EnumerateRetargetNameCandidates(
                     sourceFunctionName,
                     moduleName,
                     qualifiedModuleName,
                     targetSimpleName))
        {
            if (_typeModel.Functions.TryGetValue(candidate, out var directSignature)
                && IsRetargetSignatureCompatible(directSignature, call, sourceArgumentIndexes))
            {
                matches.Add(directSignature);
            }

            if (_typeModel.Overloads.TryGetValue(candidate, out var overloads))
            {
                matches.AddRange(overloads.Where(overload =>
                    IsRetargetSignatureCompatible(overload, call, sourceArgumentIndexes)));
            }
        }

        matches = matches
            .DistinctBy(static match => match.Name)
            .ToList();
        if (matches.Count != 1)
        {
            return false;
        }

        signature = matches[0];
        return true;
    }

    private static bool IsRetargetSignatureCompatible(
        TypedFunctionSignature signature,
        SsaCallRValue call,
        IReadOnlyList<int> sourceArgumentIndexes)
    {
        return signature.Parameters.Count == call.Arguments.Count
               && HaveSameType(signature.ReturnType, call.Type)
               && sourceArgumentIndexes.All(index =>
                   index >= 0
                   && index < signature.Parameters.Count
                   && signature.Parameters[index].IsConst);
    }

    private static IEnumerable<string> EnumerateRetargetNameCandidates(
        string sourceFunctionName,
        string moduleName,
        string qualifiedModuleName,
        string targetSimpleName)
    {
        if (TryReplaceLastNameSegment(sourceFunctionName, targetSimpleName, out var replaced))
        {
            yield return replaced;
        }

        if (string.Equals(moduleName, qualifiedModuleName, StringComparison.Ordinal))
        {
            yield return targetSimpleName;
        }

        yield return $"{qualifiedModuleName}.{targetSimpleName}";
    }

    private static bool TryReplaceLastNameSegment(
        string sourceFunctionName,
        string targetSimpleName,
        out string replaced)
    {
        replaced = string.Empty;
        var separatorIndex = sourceFunctionName.LastIndexOf('.');
        if (separatorIndex < 0)
        {
            return false;
        }

        replaced = $"{sourceFunctionName[..(separatorIndex + 1)]}{targetSimpleName}";
        return true;
    }

    private static bool TryResolveConstPathVariant(
        string functionName,
        string moduleName,
        out ConstPathVariant variant,
        out IReadOnlyList<int> sourceArgumentIndexes)
    {
        variant = default;
        sourceArgumentIndexes = [];

        if (IsPathFunction(functionName, moduleName, "TryJoin"))
        {
            variant = new ConstPathVariant("TryJoinConst");
            sourceArgumentIndexes = [1, 2];
            return true;
        }

        if (IsPathFunction(functionName, moduleName, "Join"))
        {
            variant = new ConstPathVariant("JoinConst");
            sourceArgumentIndexes = [0, 1];
            return true;
        }

        if (IsPathFunction(functionName, moduleName, "TryNormalizeSeparators"))
        {
            variant = new ConstPathVariant("TryNormalizeSeparatorsConst");
            sourceArgumentIndexes = [1];
            return true;
        }

        if (IsPathFunction(functionName, moduleName, "NormalizeSeparators"))
        {
            variant = new ConstPathVariant("NormalizeSeparatorsConst");
            sourceArgumentIndexes = [0];
            return true;
        }

        return false;
    }

    private static bool TryResolveConstTextVariant(
        string functionName,
        string moduleName,
        out ConstTextVariant variant)
    {
        variant = default;
        if (IsTextFunction(functionName, moduleName, "AppendAscii")
            || IsTextFunction(functionName, moduleName, "AppendAsciiDisjoint")
            || IsTextFunction(functionName, moduleName, "AppendConstAscii"))
        {
            variant = new ConstTextVariant(
                "AppendConstAsciiDisjoint",
                StarkTypeKind.Ascii,
                [1]);
            return true;
        }

        if (IsTextFunction(functionName, moduleName, "AppendUnicode")
            || IsTextFunction(functionName, moduleName, "AppendUnicodeDisjoint")
            || IsTextFunction(functionName, moduleName, "AppendConstUnicode"))
        {
            variant = new ConstTextVariant(
                "AppendConstUnicodeDisjoint",
                StarkTypeKind.Unicode,
                [1]);
            return true;
        }

        if (IsTextFunction(functionName, moduleName, "FromAscii"))
        {
            variant = new ConstTextVariant(
                "FromConstAscii",
                StarkTypeKind.Ascii,
                [1]);
            return true;
        }

        if (IsTextFunction(functionName, moduleName, "FromUnicode"))
        {
            variant = new ConstTextVariant(
                "FromConstUnicode",
                StarkTypeKind.Unicode,
                [1]);
            return true;
        }

        if (IsTextFunction(functionName, moduleName, "FromAsciiToUnicode"))
        {
            variant = new ConstTextVariant(
                "FromConstAsciiToUnicode",
                StarkTypeKind.Ascii,
                [1]);
            return true;
        }

        return false;
    }

    private static string RetargetCallText(string text, string targetSimpleName)
    {
        var openParenIndex = text.IndexOf('(', StringComparison.Ordinal);
        return openParenIndex > 0
            ? $"{targetSimpleName}{text[openParenIndex..]}"
            : targetSimpleName;
    }

    private static bool HaveSameType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return left == right
               || left.Kind == right.Kind
               && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
               && string.Equals(left.NamedType, right.NamedType, StringComparison.Ordinal)
               && left.BitWidth == right.BitWidth;
    }

    private static IReadOnlyList<SsaInstruction> RewritePathFactsCall(
        SsaValueInstruction original,
        SsaCallRValue call,
        NamedTypeSymbol pathFactsType,
        PathFactsValue facts,
        ISet<string> usedNames)
    {
        var resultType = call.Type;
        var location = original.Location;
        var instructions = new List<SsaInstruction>(pathFactsType.OrderedFields.Count + 1);
        var seedName = CreateUniqueValueName(usedNames, $"{original.ResultName}_pathfacts_seed");
        instructions.Add(new SsaValueInstruction(
            seedName,
            new SsaUseRValue(new SsaZeroInitializerValue(resultType)),
            location,
            original.ScopedNoAliasGroups,
            original.LoopAccessGroups));

        var current = new SsaValueReference(seedName, resultType);
        for (var fieldIndex = 0; fieldIndex < pathFactsType.OrderedFields.Count; fieldIndex++)
        {
            var field = pathFactsType.OrderedFields[fieldIndex];
            var value = CreatePathFactsFieldValue(field, facts);
            var resultName = fieldIndex == pathFactsType.OrderedFields.Count - 1
                ? original.ResultName
                : CreateUniqueValueName(
                    usedNames,
                    $"{original.ResultName}_pathfacts_{field.Name.ToLowerInvariant()}");

            instructions.Add(new SsaValueInstruction(
                resultName,
                new SsaInsertFieldRValue(
                    current,
                    field.Name,
                    fieldIndex,
                    value,
                    resultType,
                    $"{current.Text} with {field.Name}"),
                location,
                original.ScopedNoAliasGroups,
                original.LoopAccessGroups));
            current = new SsaValueReference(resultName, resultType);
        }

        return instructions;
    }

    private static SsaValue CreatePathFactsFieldValue(FieldSymbol field, PathFactsValue facts)
    {
        return field.Name switch
        {
            "Path" => new SsaStringConstant(facts.PathLiteralText, field.Type),
            "Length" => new SsaIntegerConstant(facts.Length, field.Type),
            "End" => new SsaIntegerConstant(facts.End, field.Type),
            "SegmentStart" => new SsaIntegerConstant(facts.SegmentStart, field.Type),
            "ExtensionStart" => new SsaIntegerConstant(facts.ExtensionStart, field.Type),
            "DirectoryLength" => new SsaIntegerConstant(facts.DirectoryLength, field.Type),
            "HasExtension" => new SsaBoolConstant(facts.HasExtension),
            _ => throw new InvalidOperationException(
                $"Unsupported System.IO.Path.PathFacts field '{field.Name}' in const stdlib helper specialization.")
        };
    }

    private bool TryResolvePathFactsType(
        StarkTypeSymbol type,
        out NamedTypeSymbol pathFactsType)
    {
        pathFactsType = default!;
        if (type.Kind != StarkTypeKind.Named
            || string.IsNullOrWhiteSpace(type.NamedType)
            || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            || !IsPathFactsTypeName(namedType.Name)
            || namedType.OrderedFields.Count != 7)
        {
            return false;
        }

        var fields = namedType.OrderedFields;
        if (!IsAsciiField(fields[0], "Path")
            || !IsIntegerField(fields[1], "Length", 64)
            || !IsIntegerField(fields[2], "End", 64)
            || !IsIntegerField(fields[3], "SegmentStart", 64)
            || !IsIntegerField(fields[4], "ExtensionStart", 64)
            || !IsIntegerField(fields[5], "DirectoryLength", 64)
            || !IsBoolField(fields[6], "HasExtension"))
        {
            return false;
        }

        pathFactsType = namedType;
        return true;
    }

    private bool TryComputePathFacts(
        IReadOnlyList<byte> pathBytes,
        string pathLiteralText,
        out PathFactsValue facts)
    {
        facts = default;

        if (pathBytes.Count > int.MaxValue)
        {
            return false;
        }

        var separators = ResolvePathSeparators(_targetInfo);
        var end = pathBytes.Count;
        var length = end;
        while (end > 1 && IsDirectorySeparator(pathBytes[end - 1], separators))
        {
            end--;
        }

        if (end <= 0)
        {
            facts = PathFactsValue.Empty();
            return true;
        }

        var separator = end - 1;
        while (separator >= 0)
        {
            if (IsDirectorySeparator(pathBytes[separator], separators))
            {
                break;
            }

            separator--;
        }

        var segmentStart = separator + 1;
        var directoryLength = separator == 0
            ? 1
            : separator > 0
                ? separator
                : 0;

        var extensionStart = end - 1;
        var hasExtension = false;
        while (extensionStart > segmentStart)
        {
            if (pathBytes[extensionStart] == (byte)'.')
            {
                hasExtension = true;
                break;
            }

            extensionStart--;
        }

        facts = new PathFactsValue(
            pathLiteralText,
            length,
            end,
            segmentStart,
            extensionStart,
            directoryLength,
            hasExtension);
        return true;
    }

    private static bool TryResolvePathProjection(
        string functionName,
        string moduleName,
        IReadOnlyList<byte> pathBytes,
        PathFactsValue facts,
        out PathProjectionValue projection)
    {
        projection = default;
        if (IsPathProjectionCall(functionName, moduleName, "Extension", "ExtensionConst"))
        {
            if (!facts.HasExtension)
            {
                projection = PathProjectionValue.Empty;
                return true;
            }

            return TryCreateProjection(pathBytes, facts.ExtensionStart, facts.End - facts.ExtensionStart, out projection);
        }

        if (IsPathProjectionCall(functionName, moduleName, "BaseName", "BaseNameConst"))
        {
            if (facts.SegmentStart >= facts.End)
            {
                projection = PathProjectionValue.Empty;
                return true;
            }

            var length = facts.HasExtension
                ? facts.ExtensionStart - facts.SegmentStart
                : facts.End - facts.SegmentStart;
            return TryCreateProjection(pathBytes, facts.SegmentStart, length, out projection);
        }

        if (IsPathProjectionCall(functionName, moduleName, "DirectoryName", "DirectoryNameConst"))
        {
            if (facts.DirectoryLength <= 0)
            {
                projection = PathProjectionValue.Empty;
                return true;
            }

            return TryCreateProjection(pathBytes, 0, facts.DirectoryLength, out projection);
        }

        return false;
    }

    private static bool TryCreateProjection(
        IReadOnlyList<byte> pathBytes,
        BigInteger start,
        BigInteger length,
        out PathProjectionValue projection)
    {
        projection = default;
        var end = start + length;
        if (start < BigInteger.Zero
            || length < BigInteger.Zero
            || start > int.MaxValue
            || length > int.MaxValue
            || end > pathBytes.Count)
        {
            return false;
        }

        var sourceText = Encoding.UTF8.GetString(
            pathBytes
                .Skip((int)start)
                .Take((int)length)
                .ToArray());
        projection = new PathProjectionValue(TextLiteralDecoder.EncodeStringLiteral(sourceText));
        return true;
    }

    private static bool TryGetKnownAsciiLiteralPayload(
        SsaValue value,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (value is SsaStringConstant { Type.Kind: StarkTypeKind.Ascii } source)
        {
            if (!TextLiteralDecoder.TryDecode(
                    source.LiteralText,
                    source.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                    out var decoded,
                    out _)
                || !decoded.IsAscii)
            {
                return false;
            }

            sourceBytes = decoded.Utf8Bytes;
            literalText = source.LiteralText;
            return true;
        }

        if (value is SsaValueReference reference)
        {
            if (facts.Values.TryGetValue(reference.Name, out var valueFacts)
                && valueFacts.Type.Kind == StarkTypeKind.Ascii
                && valueFacts.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                && valueFacts.TextLiteralPayload is { IsAsciiOnly: true } payload
                && TryDecodeAsciiPayloadFact(payload, out sourceBytes, out literalText))
            {
                return true;
            }

            if (visitedNames.Add($"value:{reference.Name}")
                && valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryGetKnownAsciiLiteralPayload(
                    definition,
                    facts,
                    function,
                    valueDefinitions,
                    visitedNames,
                    out sourceBytes,
                    out literalText);
            }
        }

        return false;
    }

    private static bool TryGetKnownTextLiteralPayload(
        SsaValue value,
        StarkTypeKind textKind,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames)
    {
        if (textKind == StarkTypeKind.Ascii)
        {
            return TryGetKnownAsciiLiteralPayload(
                value,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out _,
                out _);
        }

        if (textKind != StarkTypeKind.Unicode
            || value.Type.Kind != StarkTypeKind.Unicode)
        {
            return false;
        }

        if (value is SsaStringConstant { Type.Kind: StarkTypeKind.Unicode } source)
        {
            return TextLiteralDecoder.TryDecode(
                source.LiteralText,
                source.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                out _,
                out _);
        }

        if (value is SsaValueReference reference)
        {
            if (facts.Values.TryGetValue(reference.Name, out var valueFacts)
                && valueFacts.Type.Kind == StarkTypeKind.Unicode
                && valueFacts.TextLiteralPayloadKind == SsaFactLatticeKind.Known
                && valueFacts.TextLiteralPayload is not null)
            {
                return true;
            }

            if (visitedNames.Add($"value:{reference.Name}")
                && valueDefinitions.TryGetValue(reference.Name, out var definition))
            {
                return TryGetKnownTextLiteralPayload(
                    definition,
                    textKind,
                    facts,
                    function,
                    valueDefinitions,
                    visitedNames);
            }
        }

        return false;
    }

    private static bool TryGetKnownTextLiteralPayload(
        SsaRValue value,
        StarkTypeKind textKind,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames)
    {
        if (textKind == StarkTypeKind.Ascii)
        {
            return TryGetKnownAsciiLiteralPayload(
                value,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out _,
                out _);
        }

        if (textKind != StarkTypeKind.Unicode)
        {
            return false;
        }

        return value switch
        {
            SsaUseRValue use => TryGetKnownTextLiteralPayload(
                use.Value,
                textKind,
                facts,
                function,
                valueDefinitions,
                visitedNames),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Unicode } loadLocal => TryGetKnownTextLiteralPayloadFromLocal(
                loadLocal,
                textKind,
                facts,
                function,
                valueDefinitions,
                visitedNames),
            SsaConvertRValue { TargetType.Kind: StarkTypeKind.Unicode } convert => TryGetKnownTextLiteralPayload(
                convert.Operand,
                convert.Operand.Type.Kind,
                facts,
                function,
                valueDefinitions,
                visitedNames),
            SsaTextSliceRValue { Type.Kind: StarkTypeKind.Unicode } textSlice => TryGetKnownUnicodeLiteralSlicePayload(
                textSlice,
                facts,
                function,
                valueDefinitions,
                visitedNames),
            _ => false
        };
    }

    private static bool TryGetKnownUnicodeLiteralSlicePayload(
        SsaTextSliceRValue textSlice,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames)
    {
        return TryGetKnownTextLiteralPayload(
                   textSlice.TextValue,
                   StarkTypeKind.Unicode,
                   facts,
                   function,
                   valueDefinitions,
                   visitedNames)
               && TryResolveExactNonNegativeInteger(textSlice.Start, valueDefinitions, out _)
               && TryResolveExactNonNegativeInteger(textSlice.Length, valueDefinitions, out _);
    }

    private static bool TryGetKnownTextLiteralPayloadFromLocal(
        SsaLoadLocalRValue loadLocal,
        StarkTypeKind textKind,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames)
    {
        if (!visitedNames.Add($"local:{loadLocal.LocalName}")
            || loadLocal.Type.Kind != textKind
            || LocalAddressMayBeObserved(function, loadLocal.LocalName))
        {
            return false;
        }

        var stores = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaStoreLocalInstruction>()
            .Where(store => string.Equals(store.LocalName, loadLocal.LocalName, StringComparison.Ordinal)
                            && store.LocalType == loadLocal.Type)
            .ToArray();
        return stores.Length == 1
               && TryGetKnownTextLiteralPayload(
                   stores[0].Value,
                   textKind,
                   facts,
                   function,
                   valueDefinitions,
                   visitedNames);
    }

    private static bool TryGetKnownAsciiLiteralPayload(
        SsaRValue value,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        return value switch
        {
            SsaUseRValue use => TryGetKnownAsciiLiteralPayload(
                use.Value,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaTextSliceRValue { Type.Kind: StarkTypeKind.Ascii } textSlice => TryGetKnownAsciiLiteralSlicePayload(
                textSlice,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaLoadLocalRValue { Type.Kind: StarkTypeKind.Ascii } loadLocal => TryGetKnownAsciiLiteralPayloadFromLocal(
                loadLocal,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            SsaConvertRValue convert when convert.TargetType.Kind == StarkTypeKind.Ascii => TryGetKnownAsciiLiteralPayload(
                convert.Operand,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out sourceBytes,
                out literalText),
            _ => false
        };
    }

    private static bool TryGetKnownAsciiLiteralSlicePayload(
        SsaTextSliceRValue textSlice,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (!TryGetKnownAsciiLiteralPayload(
                textSlice.TextValue,
                facts,
                function,
                valueDefinitions,
                visitedNames,
                out var sourcePayload,
                out _)
            || !TryResolveExactNonNegativeInteger(textSlice.Start, valueDefinitions, out var start)
            || !TryResolveExactNonNegativeInteger(textSlice.Length, valueDefinitions, out var length)
            || !TrySliceAsciiPayload(sourcePayload, start, length, out sourceBytes))
        {
            return false;
        }

        literalText = TextLiteralDecoder.EncodeStringLiteral(Encoding.UTF8.GetString(sourceBytes));
        return true;
    }

    private static bool TryGetKnownAsciiLiteralPayloadFromLocal(
        SsaLoadLocalRValue loadLocal,
        SsaFunctionFactModel facts,
        SsaFunction function,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        ISet<string> visitedNames,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        if (!visitedNames.Add($"local:{loadLocal.LocalName}")
            || LocalAddressMayBeObserved(function, loadLocal.LocalName))
        {
            return false;
        }

        var stores = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaStoreLocalInstruction>()
            .Where(store => string.Equals(store.LocalName, loadLocal.LocalName, StringComparison.Ordinal)
                            && store.LocalType == loadLocal.Type)
            .ToArray();
        if (stores.Length != 1)
        {
            return false;
        }

        return TryGetKnownAsciiLiteralPayload(
            stores[0].Value,
            facts,
            function,
            valueDefinitions,
            visitedNames,
            out sourceBytes,
            out literalText);
    }

    private static bool LocalAddressMayBeObserved(SsaFunction function, string localName)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Any(instruction => SsaValueFactAnalyzer.RValueTakesLocalAddress(instruction.Value, localName));
    }

    private static bool TryDecodeAsciiPayloadFact(
        SsaTextLiteralPayloadFact payload,
        out byte[] sourceBytes,
        out string literalText)
    {
        sourceBytes = [];
        literalText = string.Empty;

        try
        {
            sourceBytes = Convert.FromHexString(payload.Utf8PayloadHex);
            if (sourceBytes.Length != payload.Utf8Length)
            {
                return false;
            }

            literalText = TextLiteralDecoder.EncodeStringLiteral(payload.DecodedText);
            return true;
        }
        catch (FormatException)
        {
            sourceBytes = [];
            literalText = string.Empty;
            return false;
        }
    }

    private static bool TryResolveExactNonNegativeInteger(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> valueDefinitions,
        out BigInteger exact)
    {
        if (TryResolveIntegerConstant(value, valueDefinitions, new HashSet<string>(StringComparer.Ordinal), out exact)
            && exact >= BigInteger.Zero)
        {
            return true;
        }

        exact = default;
        return false;
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

    private static bool TrySliceAsciiPayload(
        IReadOnlyList<byte> sourceBytes,
        BigInteger start,
        BigInteger length,
        out byte[] slicedBytes)
    {
        slicedBytes = [];
        var end = start + length;
        if (start < BigInteger.Zero
            || length < BigInteger.Zero
            || start > int.MaxValue
            || length > int.MaxValue
            || end > sourceBytes.Count)
        {
            return false;
        }

        slicedBytes = sourceBytes
            .Skip((int)start)
            .Take((int)length)
            .ToArray();
        return true;
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectValueDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
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
            candidate = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static PathSeparators ResolvePathSeparators(LlvmTargetInfo? targetInfo)
    {
        return IsWindowsTarget(targetInfo)
            ? new PathSeparators((byte)'\\', (byte)'/')
            : new PathSeparators((byte)'/', null);
    }

    private static bool IsWindowsTarget(LlvmTargetInfo? targetInfo)
    {
        var triple = targetInfo?.Triple;
        return !string.IsNullOrWhiteSpace(triple)
               && (triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                   || triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                   || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase)
                   || triple.Contains("msvc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDirectorySeparator(byte value, PathSeparators separators)
    {
        return value == separators.Primary
               || separators.Alternate is byte alternate && value == alternate;
    }

    private static bool IsPathFactsCall(string functionName, string moduleName)
    {
        return IsPathFunction(functionName, moduleName, "GetFacts")
               || IsPathFunction(functionName, moduleName, "GetConstFacts");
    }

    private static bool IsPathProjectionCall(
        string functionName,
        string moduleName,
        string runtimeName,
        string constName)
    {
        return IsPathFunction(functionName, moduleName, runtimeName)
               || IsPathFunction(functionName, moduleName, constName);
    }

    private static bool IsAnyPathProjectionCall(string functionName, string moduleName)
    {
        return IsPathProjectionCall(functionName, moduleName, "Extension", "ExtensionConst")
               || IsPathProjectionCall(functionName, moduleName, "BaseName", "BaseNameConst")
               || IsPathProjectionCall(functionName, moduleName, "DirectoryName", "DirectoryNameConst");
    }

    private static bool IsPathFunction(string functionName, string moduleName, string simpleName)
    {
        return string.Equals(functionName, $"System.IO.Path.{simpleName}", StringComparison.Ordinal)
               || string.Equals(moduleName, "System.IO.Path", StringComparison.Ordinal)
               && string.Equals(functionName, simpleName, StringComparison.Ordinal);
    }

    private static bool IsTextFunction(string functionName, string moduleName, string simpleName)
    {
        return string.Equals(functionName, $"System.Text.{simpleName}", StringComparison.Ordinal)
               || functionName.StartsWith("System.Text.", StringComparison.Ordinal)
               && functionName.EndsWith($".{simpleName}", StringComparison.Ordinal)
               || string.Equals(moduleName, "System.Text", StringComparison.Ordinal)
               && (string.Equals(functionName, simpleName, StringComparison.Ordinal)
                   || functionName.EndsWith($".{simpleName}", StringComparison.Ordinal));
    }

    private static bool IsPathFactsTypeName(string typeName)
    {
        return string.Equals(typeName, "System.IO.Path.PathFacts", StringComparison.Ordinal)
               || string.Equals(typeName, "PathFacts", StringComparison.Ordinal);
    }

    private static bool IsAsciiField(FieldSymbol field, string name)
    {
        return string.Equals(field.Name, name, StringComparison.Ordinal)
               && field.Type.Kind == StarkTypeKind.Ascii;
    }

    private static bool IsIntegerField(FieldSymbol field, string name, int bitWidth)
    {
        return string.Equals(field.Name, name, StringComparison.Ordinal)
               && field.Type.Kind == StarkTypeKind.Integer
               && field.Type.BitWidth == bitWidth;
    }

    private static bool IsBoolField(FieldSymbol field, string name)
    {
        return string.Equals(field.Name, name, StringComparison.Ordinal)
               && field.Type.Kind == StarkTypeKind.Bool;
    }

    private readonly record struct PathFactsValue(
        string PathLiteralText,
        BigInteger Length,
        BigInteger End,
        BigInteger SegmentStart,
        BigInteger ExtensionStart,
        BigInteger DirectoryLength,
        bool HasExtension)
    {
        public static PathFactsValue Empty() => new(
            TextLiteralDecoder.EncodeStringLiteral(string.Empty),
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            false);
    }

    private readonly record struct PathProjectionValue(string LiteralText)
    {
        public static PathProjectionValue Empty { get; } = new(TextLiteralDecoder.EncodeStringLiteral(string.Empty));
    }

    private readonly record struct PathSeparators(byte Primary, byte? Alternate);

    private readonly record struct ConstPathVariant(string SimpleName);

    private readonly record struct ConstTextVariant(
        string TargetSimpleName,
        StarkTypeKind SourceTextKind,
        IReadOnlyList<int> SourceArgumentIndexes);
}
