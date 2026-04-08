using Stark.Parsing;

namespace Stark.Compiler;

internal enum OverloadResolutionFailureKind
{
    None,
    NoMatch,
    Ambiguous
}

internal readonly record struct OverloadResolutionResult(
    TypedFunctionSignature? Match,
    OverloadResolutionFailureKind Failure,
    IReadOnlyList<TypedFunctionSignature> Candidates)
{
    public bool Succeeded => Match is not null && Failure == OverloadResolutionFailureKind.None;
}

internal static class FunctionOverloadFacts
{
    public static string BuildOverloadKey(IEnumerable<string> parameterTypeTexts)
    {
        return $"({string.Join(",", parameterTypeTexts.Select(static text => CanonicalizeTypeText(text).Replace(" ", string.Empty, StringComparison.Ordinal)))})";
    }

    public static string BuildOverloadKey(IReadOnlyList<ParameterModel> parameters)
    {
        return BuildOverloadKey(parameters.Select(static parameter => parameter.TypeText));
    }

    public static string BuildOverloadKey(StarkParser.ParameterListContext parameterList)
    {
        return BuildOverloadKey(parameterList.parameter().Select(static parameter => parameter.type_().GetText()));
    }

    public static IEnumerable<string> GetDeclaredOverloadKeys(TopLevelDeclarationModel declaration)
    {
        if (declaration.Function is null)
        {
            return [];
        }

        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            BuildOverloadKey(declaration.Function.Parameters)
        };

        if (!string.IsNullOrEmpty(declaration.Function.PublishedOverloadKey))
        {
            keys.Add(declaration.Function.PublishedOverloadKey);
        }

        return keys;
    }

    public static string GetResolvedLocalName(SyntaxModel syntaxModel, TopLevelDeclarationModel declaration)
    {
        if (declaration.Function is null)
        {
            return declaration.Name;
        }

        return GetResolvedLocalName(syntaxModel, declaration.Name, BuildOverloadKey(declaration.Function.Parameters));
    }

    public static string GetResolvedLocalName(
        SyntaxModel syntaxModel,
        string sourceName,
        IEnumerable<string> parameterTypeTexts)
    {
        return GetResolvedLocalName(syntaxModel, sourceName, BuildOverloadKey(parameterTypeTexts));
    }

    public static string GetResolvedLocalName(
        SyntaxModel syntaxModel,
        string sourceName,
        string overloadKey)
    {
        return CountFunctionsWithSourceName(syntaxModel, sourceName) > 1
            ? $"{sourceName}#{overloadKey}"
            : sourceName;
    }

    public static string QualifyResolvedName(LoadedModuleDocument module, string resolvedLocalName)
    {
        return module.Reference.IsRoot
            ? resolvedLocalName
            : $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}";
    }

    public static string QualifySourceName(LoadedModuleDocument module, string sourceLocalName)
    {
        return module.Reference.IsRoot
            ? sourceLocalName
            : $"{module.SyntaxModel.ModuleName}.{sourceLocalName}";
    }

    private static string CanonicalizeTypeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return trimmed;
        }

        var qualifiers = new HashSet<string>(StringComparer.Ordinal);
        var qualifierCount = 0;

        while (qualifierCount < parts.Length && IsTypeQualifier(parts[qualifierCount]))
        {
            qualifiers.Add(parts[qualifierCount]);
            qualifierCount++;
        }

        if (qualifierCount == 0)
        {
            return trimmed;
        }

        var builder = new List<string>(8);
        if (qualifiers.Contains("mut"))
        {
            builder.Add("mut");
        }

        if (qualifiers.Contains("borrow"))
        {
            builder.Add("borrow");
        }

        if (qualifiers.Contains("retborrow"))
        {
            builder.Add("retborrow");
        }

        if (qualifiers.Contains("storeborrow"))
        {
            builder.Add("storeborrow");
        }

        if (qualifiers.Contains("shared"))
        {
            builder.Add("shared");
        }

        if (qualifiers.Contains("frozen"))
        {
            builder.Add("frozen");
        }

        if (qualifiers.Contains("out"))
        {
            builder.Add("out");
        }

        if (qualifiers.Contains("init"))
        {
            builder.Add("init");
        }

        builder.Add(string.Join(" ", parts.Skip(qualifierCount)));
        return string.Join(" ", builder);
    }

    private static bool IsTypeQualifier(string text)
    {
        return text is "mut"
            or "borrow"
            or "retborrow"
            or "storeborrow"
            or "shared"
            or "frozen"
            or "out"
            or "init";
    }

    public static bool TryFindFunctionDeclaration(
        SyntaxModel syntaxModel,
        string sourceName,
        string overloadKey,
        out TopLevelDeclarationModel declaration)
    {
        var match = syntaxModel.Declarations.FirstOrDefault(candidate =>
            candidate.Kind == DeclarationKind.Function
            && string.Equals(candidate.Name, sourceName, StringComparison.Ordinal)
            && candidate.Function is not null
            && GetDeclaredOverloadKeys(candidate).Any(candidateKey => string.Equals(candidateKey, overloadKey, StringComparison.Ordinal)));

        if (match is null)
        {
            declaration = null!;
            return false;
        }

        declaration = match;
        return true;
    }

    public static bool CanBindReceiver(
        StarkTypeSymbol parameterType,
        StarkTypeSymbol receiverType,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign)
    {
        if (canAssign(parameterType, receiverType))
        {
            return true;
        }

        if (parameterType.BorrowKind == StarkBorrowKind.None)
        {
            return false;
        }

        var strippedParameterType = StripQualifiers(parameterType);
        var strippedReceiverType = StripQualifiers(receiverType);

        if (!canAssign(strippedParameterType, strippedReceiverType))
        {
            return false;
        }

        return !parameterType.IsMutableView || receiverType.AccessKind != StarkAccessKind.Frozen;
    }

    public static OverloadResolutionResult Resolve(
        IReadOnlyList<TypedFunctionSignature> candidates,
        StarkTypeSymbol? receiverType,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign)
    {
        var matches = new List<(TypedFunctionSignature Signature, int ExactMatches, int GenericPenalty)>();

        foreach (var candidate in candidates)
        {
            if (TryResolveCandidate(candidate, receiverType, argumentTypes, canAssign, out var resolvedCandidate, out var exactMatches, out var genericPenalty))
            {
                matches.Add((resolvedCandidate, exactMatches, genericPenalty));
            }
        }

        if (matches.Count == 0)
        {
            return new OverloadResolutionResult(
                Match: null,
                OverloadResolutionFailureKind.NoMatch,
                candidates);
        }

        var bestExactMatchCount = matches.Max(static match => match.ExactMatches);
        var bestGenericPenalty = matches
            .Where(match => match.ExactMatches == bestExactMatchCount)
            .Min(static match => match.GenericPenalty);
        var bestMatches = matches
            .Where(match => match.ExactMatches == bestExactMatchCount && match.GenericPenalty == bestGenericPenalty)
            .Select(static match => match.Signature)
            .ToArray();

        return bestMatches.Length == 1
            ? new OverloadResolutionResult(bestMatches[0], OverloadResolutionFailureKind.None, bestMatches)
            : new OverloadResolutionResult(null, OverloadResolutionFailureKind.Ambiguous, bestMatches);
    }

    public static string FormatSignature(TypedFunctionSignature signature)
    {
        var genericSuffix = signature.IsGeneric
            ? $"<{string.Join(", ", signature.GenericParams)}>"
            : signature.IsGenericInstantiation && signature.TypeArguments is { Count: > 0 }
                ? $"<{string.Join(", ", signature.TypeArguments.Select(static argument => argument.DisplayName))}>"
                : string.Empty;
        return $"{signature.DisplaySourceName}{genericSuffix}({string.Join(", ", signature.Parameters.Select(static parameter => parameter.Type.DisplayName))})";
    }

    public static string BuildTypeArgumentKey(IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return string.Join(",", typeArguments.Select(BuildCanonicalTypeKey));
    }

    public static string BuildCanonicalTypeKey(StarkTypeSymbol type)
    {
        var coreType = StripQualifiers(type);

        return coreType.Kind switch
        {
            StarkTypeKind.Named when coreType.NamedType is not null => coreType.NamedType,
            StarkTypeKind.RawPointer when coreType.ElementType is not null
                => $"{(coreType.IsMutablePointer ? "rawmutptr" : "rawptr")}<{BuildCanonicalTypeKey(coreType.ElementType)}>",
            StarkTypeKind.FixedArray when coreType.ElementType is not null
                => $"{BuildCanonicalTypeKey(coreType.ElementType)}[{(coreType.FixedLength is { } fixedLength ? fixedLength.ToString() : "?")}]",
            StarkTypeKind.Slice when coreType.ElementType is not null
                => $"{BuildCanonicalTypeKey(coreType.ElementType)}[]",
            _ => coreType.DisplayName
        };
    }

    public static IReadOnlyDictionary<string, StarkTypeSymbol> BuildGenericSubstitution(
        TypedFunctionSignature template,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        if (!template.IsGeneric)
        {
            return new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        }

        if (template.GenericParams.Count != typeArguments.Count)
        {
            throw new InvalidOperationException(
                $"Generic function '{template.Name}' expects {template.GenericParams.Count} type argument(s) but {typeArguments.Count} were provided.");
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < template.GenericParams.Count; index++)
        {
            substitution[template.GenericParams[index]] = typeArguments[index];
        }

        return substitution;
    }

    public static TypedFunctionSignature InstantiateSignature(
        TypedFunctionSignature template,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        string materializedName)
    {
        var substitution = BuildGenericSubstitution(template, typeArguments);
        return template with
        {
            Name = materializedName,
            ReturnType = SubstituteType(template.ReturnType, substitution),
            Parameters = template.Parameters
                .Select(parameter => new TypedParameterSymbol(parameter.Name, SubstituteType(parameter.Type, substitution)))
                .ToArray(),
            GenericParameterNames = null,
            TemplateName = template.TemplateName ?? template.Name,
            TypeArguments = typeArguments.ToArray()
        };
    }

    private static bool TryResolveCandidate(
        TypedFunctionSignature candidate,
        StarkTypeSymbol? receiverType,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign,
        out TypedFunctionSignature resolvedCandidate,
        out int exactMatches,
        out int genericPenalty)
    {
        resolvedCandidate = candidate;
        exactMatches = 0;
        genericPenalty = 0;

        var receiverOffset = receiverType is null ? 0 : 1;
        if (candidate.Parameters.Count < receiverOffset)
        {
            return false;
        }

        var explicitParameterCount = candidate.Parameters.Count - receiverOffset;
        if (explicitParameterCount != argumentTypes.Count)
        {
            return false;
        }

        if (candidate.IsGeneric)
        {
            if (!TryInstantiateGenericCandidate(candidate, receiverType, argumentTypes, out resolvedCandidate))
            {
                return false;
            }

            genericPenalty = candidate.GenericParams.Count;
        }

        if (receiverType is not null)
        {
            var receiverParameterType = resolvedCandidate.Parameters[0].Type;
            if (!CanBindReceiver(receiverParameterType, receiverType, canAssign))
            {
                return false;
            }

            if (receiverParameterType == receiverType)
            {
                exactMatches++;
            }
        }

        for (var index = 0; index < argumentTypes.Count; index++)
        {
            var parameterType = resolvedCandidate.Parameters[index + receiverOffset].Type;
            var argumentType = argumentTypes[index];
            if (!canAssign(parameterType, argumentType))
            {
                return false;
            }

            if (parameterType == argumentType)
            {
                exactMatches++;
            }
        }

        return true;
    }

    private static bool TryInstantiateGenericCandidate(
        TypedFunctionSignature candidate,
        StarkTypeSymbol? receiverType,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        out TypedFunctionSignature instantiated)
    {
        instantiated = candidate;

        if (!candidate.IsGeneric)
        {
            return true;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var genericParameters = candidate.GenericParams.ToHashSet(StringComparer.Ordinal);
        var receiverOffset = receiverType is null ? 0 : 1;

        if (receiverType is not null
            && ContainsGenericParameter(candidate.Parameters[0].Type, genericParameters)
            && !TryInferTypeArguments(candidate.Parameters[0].Type, receiverType, genericParameters, substitution))
        {
            return false;
        }

        for (var index = 0; index < argumentTypes.Count; index++)
        {
            var parameterType = candidate.Parameters[index + receiverOffset].Type;
            if (ContainsGenericParameter(parameterType, genericParameters)
                && !TryInferTypeArguments(
                    parameterType,
                    argumentTypes[index],
                    genericParameters,
                    substitution))
            {
                return false;
            }
        }

        if (candidate.GenericParams.Any(parameter => !substitution.ContainsKey(parameter)))
        {
            return false;
        }

        instantiated = candidate with
        {
            ReturnType = SubstituteType(candidate.ReturnType, substitution),
            Parameters = candidate.Parameters
                .Select(parameter => new TypedParameterSymbol(parameter.Name, SubstituteType(parameter.Type, substitution)))
                .ToArray(),
            GenericParameterNames = null,
            TemplateName = candidate.TemplateName ?? candidate.Name,
            TypeArguments = candidate.GenericParams.Select(parameter => substitution[parameter]).ToArray()
        };
        return true;
    }

    private static bool TryInferTypeArguments(
        StarkTypeSymbol parameterType,
        StarkTypeSymbol argumentType,
        ISet<string> genericParameters,
        IDictionary<string, StarkTypeSymbol> substitution)
    {
        if (TryGetDirectGenericParameterName(parameterType, genericParameters, out var directGenericParameter))
        {
            return TryBindGenericParameter(directGenericParameter, argumentType, substitution);
        }

        if (!TypeCompatibilityFacts.AreQualifiersAssignable(parameterType, argumentType))
        {
            return false;
        }

        var strippedParameterType = StripQualifiers(parameterType);
        var strippedArgumentType = StripQualifiers(argumentType);
        if (TryGetDirectGenericParameterName(strippedParameterType, genericParameters, out var qualifiedGenericParameter))
        {
            return TryBindGenericParameter(qualifiedGenericParameter, strippedArgumentType, substitution);
        }

        if (strippedParameterType.Kind == StarkTypeKind.Slice
            && strippedArgumentType.Kind == StarkTypeKind.FixedArray
            && strippedParameterType.ElementType is not null
            && strippedArgumentType.ElementType is not null)
        {
            return TryInferTypeArguments(
                strippedParameterType.ElementType,
                strippedArgumentType.ElementType,
                genericParameters,
                substitution);
        }

        if (strippedParameterType.Kind != strippedArgumentType.Kind)
        {
            return false;
        }

        if (strippedParameterType.Kind == StarkTypeKind.Named)
        {
            if (strippedParameterType.NamedType is null || strippedArgumentType.NamedType is null)
            {
                return false;
            }

            if (StarkTypeSymbols.IsGenericInstantiation(strippedParameterType))
            {
                if (!StarkTypeSymbols.IsGenericInstantiation(strippedArgumentType)
                    || !string.Equals(
                        StarkTypeSymbols.GetGenericBaseName(strippedParameterType.NamedType),
                        StarkTypeSymbols.GetGenericBaseName(strippedArgumentType.NamedType),
                        StringComparison.Ordinal)
                    || strippedParameterType.TypeArguments is null
                    || strippedArgumentType.TypeArguments is null
                    || strippedParameterType.TypeArguments.Count != strippedArgumentType.TypeArguments.Count)
                {
                    return false;
                }

                for (var index = 0; index < strippedParameterType.TypeArguments.Count; index++)
                {
                    if (!TryInferTypeArguments(
                            strippedParameterType.TypeArguments[index],
                            strippedArgumentType.TypeArguments[index],
                            genericParameters,
                            substitution))
                    {
                        return false;
                    }
                }

                return true;
            }

            return string.Equals(strippedParameterType.NamedType, strippedArgumentType.NamedType, StringComparison.Ordinal);
        }

        if (strippedParameterType.Kind == StarkTypeKind.FixedArray)
        {
            return strippedParameterType.FixedLength == strippedArgumentType.FixedLength
                && strippedParameterType.ElementType is not null
                && strippedArgumentType.ElementType is not null
                && TryInferTypeArguments(
                    strippedParameterType.ElementType,
                    strippedArgumentType.ElementType,
                    genericParameters,
                    substitution);
        }

        if (strippedParameterType.Kind == StarkTypeKind.Slice)
        {
            return strippedParameterType.ElementType is not null
                && strippedArgumentType.ElementType is not null
                && TryInferTypeArguments(
                    strippedParameterType.ElementType,
                    strippedArgumentType.ElementType,
                    genericParameters,
                    substitution);
        }

        if (strippedParameterType.Kind == StarkTypeKind.RawPointer)
        {
            return (!strippedParameterType.IsMutablePointer || strippedArgumentType.IsMutablePointer)
                && ((strippedParameterType.ElementType is null && strippedArgumentType.ElementType is null)
                    || (strippedParameterType.ElementType is not null
                        && strippedArgumentType.ElementType is not null
                        && TryInferTypeArguments(
                            strippedParameterType.ElementType,
                            strippedArgumentType.ElementType,
                            genericParameters,
                            substitution)));
        }

        return strippedParameterType == strippedArgumentType;
    }

    private static bool TryGetDirectGenericParameterName(
        StarkTypeSymbol type,
        ISet<string> genericParameters,
        out string parameterName)
    {
        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is { } name
            && type.TypeArguments is not { Count: > 0 }
            && genericParameters.Contains(name))
        {
            parameterName = name;
            return true;
        }

        parameterName = string.Empty;
        return false;
    }

    private static bool ContainsGenericParameter(StarkTypeSymbol type, ISet<string> genericParameters)
    {
        if (TryGetDirectGenericParameterName(type, genericParameters, out _))
        {
            return true;
        }

        var strippedType = StripQualifiers(type);
        if (TryGetDirectGenericParameterName(strippedType, genericParameters, out _))
        {
            return true;
        }

        if (strippedType.TypeArguments is { Count: > 0 }
            && strippedType.TypeArguments.Any(argument => ContainsGenericParameter(argument, genericParameters)))
        {
            return true;
        }

        return strippedType.ElementType is not null
            && ContainsGenericParameter(strippedType.ElementType, genericParameters);
    }

    private static bool TryBindGenericParameter(
        string parameterName,
        StarkTypeSymbol inferredType,
        IDictionary<string, StarkTypeSymbol> substitution)
    {
        if (substitution.TryGetValue(parameterName, out var existing))
        {
            return existing == inferredType;
        }

        substitution[parameterName] = inferredType;
        return true;
    }

    public static StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        var coreType = StripQualifiers(type);
        StarkTypeSymbol substitutedCore;

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (substitution.TryGetValue(name, out var substituted))
            {
                substitutedCore = StripQualifiers(substituted);
            }
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType) && coreType.TypeArguments is not null)
            {
                var substitutedArguments = coreType.TypeArguments
                    .Select(argument => SubstituteType(argument, substitution))
                    .ToArray();
                substitutedCore = StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(name),
                    substitutedArguments);
            }
            else
            {
                substitutedCore = coreType;
            }
        }
        else if (coreType.ElementType is not null)
        {
            var substitutedElement = SubstituteType(coreType.ElementType, substitution);
            substitutedCore = coreType.Kind switch
            {
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(substitutedElement, coreType.FixedLength),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(substitutedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(substitutedElement, coreType.IsMutablePointer),
                _ => coreType
            };
        }
        else
        {
            substitutedCore = coreType;
        }

        return StarkTypeSymbols.WithQualifiers(
            substitutedCore,
            borrowKind: type.BorrowKind,
            accessKind: type.AccessKind,
            initializationKind: type.InitializationKind,
            isMutableView: type.IsMutableView);
    }

    private static StarkTypeSymbol StripQualifiers(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false,
            ElementType = type.ElementType is null ? null : StripQualifiers(type.ElementType)
        };
    }

    private static int CountFunctionsWithSourceName(SyntaxModel syntaxModel, string sourceName)
    {
        return syntaxModel.Declarations.Count(declaration =>
            declaration.Kind == DeclarationKind.Function
            && string.Equals(declaration.Name, sourceName, StringComparison.Ordinal));
    }
}
