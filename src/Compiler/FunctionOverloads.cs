using System.Numerics;
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

        var overloadKey = string.IsNullOrEmpty(declaration.Function.PublishedOverloadKey)
            ? BuildOverloadKey(declaration.Function.Parameters)
            : declaration.Function.PublishedOverloadKey;
        return GetResolvedLocalName(syntaxModel, declaration.Name, overloadKey);
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
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver = null)
    {
        var matches = new List<(TypedFunctionSignature Signature, int ExactMatches, int ConversionCost, int GenericPenalty)>();

        foreach (var candidate in candidates)
        {
            if (TryResolveCandidate(candidate, receiverType, argumentTypes, canAssign, associatedTypeResolver, out var resolvedCandidate, out var exactMatches, out var conversionCost, out var genericPenalty))
            {
                matches.Add((resolvedCandidate, exactMatches, conversionCost, genericPenalty));
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
        var bestConversionCost = matches
            .Where(match => match.ExactMatches == bestExactMatchCount)
            .Min(static match => match.ConversionCost);
        var bestGenericPenalty = matches
            .Where(match => match.ExactMatches == bestExactMatchCount && match.ConversionCost == bestConversionCost)
            .Min(static match => match.GenericPenalty);
        var bestMatches = matches
            .Where(match => match.ExactMatches == bestExactMatchCount
                && match.ConversionCost == bestConversionCost
                && match.GenericPenalty == bestGenericPenalty)
            .Select(static match => match.Signature)
            .ToArray();

        return bestMatches.Length == 1
            ? new OverloadResolutionResult(bestMatches[0], OverloadResolutionFailureKind.None, bestMatches)
            : new OverloadResolutionResult(null, OverloadResolutionFailureKind.Ambiguous, bestMatches);
    }

    public static string FormatSignature(TypedFunctionSignature signature)
    {
        var genericParts = signature.GenericParams
            .Concat(signature.ComptimeGenericParams.Select(static parameter => $"comptime {parameter.Type.DisplayName} {parameter.Name}"))
            .ToArray();
        var instantiationParts = (signature.TypeArguments ?? [])
            .Select(static argument => argument.DisplayName)
            .Concat(signature.ComptimeValues.Select(static argument => argument.DisplayName))
            .ToArray();
        var genericSuffix = signature.IsGeneric
            ? $"<{string.Join(", ", genericParts)}>"
            : signature.IsGenericInstantiation && instantiationParts.Length > 0
                ? $"<{string.Join(", ", instantiationParts)}>"
                : string.Empty;
        return $"{signature.DisplaySourceName}{genericSuffix}({string.Join(", ", signature.Parameters.Select(FormatParameter))})";
    }

    private static string FormatParameter(TypedParameterSymbol parameter)
    {
        return parameter.IsConst
            ? $"const {parameter.Type.DisplayName}"
            : parameter.Type.DisplayName;
    }

    public static string BuildTypeArgumentKey(IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        return string.Join(",", typeArguments.Select(BuildCanonicalTypeKey));
    }

    public static string BuildComptimeValueArgumentKey(IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments)
    {
        return valueArguments is { Count: > 0 }
            ? string.Join(",", valueArguments.Select(static argument => argument.IsSymbolic
                ? argument.DisplayName
                : $"{argument.ParameterName}={argument.IntegerValue}"))
            : string.Empty;
    }

    public static string BuildInstantiationArgumentKey(
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments)
    {
        var typeKey = typeArguments is { Count: > 0 }
            ? BuildTypeArgumentKey(typeArguments)
            : string.Empty;
        var valueKey = BuildComptimeValueArgumentKey(valueArguments);
        return string.IsNullOrEmpty(valueKey)
            ? typeKey
            : string.IsNullOrEmpty(typeKey)
                ? valueKey
                : $"{typeKey};{valueKey}";
    }

    public static string BuildCanonicalTypeKey(StarkTypeSymbol type)
    {
        var coreType = StripQualifiers(type);

        return coreType.Kind switch
        {
            StarkTypeKind.Named when coreType.NamedType is not null
                => StarkTypeSymbols.IsGenericInstantiation(coreType)
                    ? $"{StarkTypeSymbols.GetGenericBaseName(coreType.NamedType)}<{BuildInstantiationArgumentKey(coreType.TypeArguments, coreType.ComptimeValueArguments)}>"
                    : coreType.NamedType,
            StarkTypeKind.Integer when coreType.BitWidth is int bitWidth
                                        && coreType.IsUnsigned
                                        && StarkTypeSymbols.IsFullUnsignedIntegerRange(bitWidth, coreType.RangeMin, coreType.RangeMax)
                => $"u{bitWidth}",
            StarkTypeKind.Integer when coreType.BitWidth is int bitWidth
                                        && StarkTypeSymbols.IsFullSignedIntegerRange(bitWidth, coreType.RangeMin, coreType.RangeMax)
                => $"i{bitWidth}",
            StarkTypeKind.RawPointer when coreType.ElementType is not null
                => $"{(coreType.IsMutablePointer ? "rawmutptr" : "rawptr")}<{BuildCanonicalTypeKey(coreType.ElementType)}>",
            StarkTypeKind.FixedArray when coreType.ElementType is not null
                => $"{BuildCanonicalTypeKey(coreType.ElementType)}[{(coreType.FixedLength is { } fixedLength ? fixedLength.ToString() : coreType.FixedLengthParameterName ?? "?")}]",
            StarkTypeKind.Slice when coreType.ElementType is not null
                => $"{BuildCanonicalTypeKey(coreType.ElementType)}[]",
            StarkTypeKind.AssociatedType when coreType.AssociatedTypeOwner is not null
                                              && coreType.AssociatedTypeName is not null
                => $"{BuildCanonicalTypeKey(coreType.AssociatedTypeOwner)}.{coreType.AssociatedTypeName}",
            _ => coreType.DisplayName
        };
    }

    public static IReadOnlyDictionary<string, StarkTypeSymbol> BuildGenericSubstitution(
        TypedFunctionSignature template,
        IReadOnlyList<StarkTypeSymbol> typeArguments)
    {
        var genericParameters = GetEffectiveGenericParameters(template, typeArguments.Count);
        if (genericParameters.Count == 0)
        {
            return new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        }

        if (genericParameters.Count != typeArguments.Count)
        {
            throw new InvalidOperationException(
                $"Generic function '{template.Name}' expects {genericParameters.Count} type argument(s) but {typeArguments.Count} were provided.");
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < genericParameters.Count; index++)
        {
            substitution[genericParameters[index]] = typeArguments[index];
        }

        return substitution;
    }

    public static IReadOnlyDictionary<string, BigInteger> BuildComptimeValueSubstitution(
        TypedFunctionSignature template,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments)
    {
        if (template.ComptimeGenericParams.Count == 0)
        {
            return new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        }

        valueArguments ??= [];
        if (template.ComptimeGenericParams.Count != valueArguments.Count)
        {
            throw new InvalidOperationException(
                $"Generic function '{template.Name}' expects {template.ComptimeGenericParams.Count} comptime value argument(s) but {valueArguments.Count} were provided.");
        }

        var substitution = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        for (var index = 0; index < template.ComptimeGenericParams.Count; index++)
        {
            var parameter = template.ComptimeGenericParams[index];
            var argument = valueArguments[index];
            if (argument.IsSymbolic)
            {
                continue;
            }

            substitution[parameter.Name] = argument.IntegerValue;
        }

        return substitution;
    }

    public static TypedFunctionSignature InstantiateSignature(
        TypedFunctionSignature template,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        string materializedName,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver = null,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments = null)
    {
        var substitution = BuildGenericSubstitution(template, typeArguments);
        var valueSubstitution = BuildComptimeValueSubstitution(template, valueArguments);
        return template with
        {
            Name = materializedName,
            ReturnType = SubstituteType(template.ReturnType, substitution, associatedTypeResolver, valueSubstitution),
            Parameters = template.Parameters
                .Select(parameter => new TypedParameterSymbol(
                    parameter.Name,
                    SubstituteType(parameter.Type, substitution, associatedTypeResolver, valueSubstitution),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            GenericParameterNames = null,
            ComptimeGenericParameterNames = null,
            TemplateName = template.TemplateName ?? template.Name,
            TypeArguments = typeArguments.ToArray(),
            ComptimeValueArguments = valueArguments?.ToArray(),
            ThreadSafetyLawPredicates = SubstituteThreadSafetyLawPredicates(
                template.ThreadSafetyLaws,
                substitution,
                associatedTypeResolver,
                valueSubstitution)
        };
    }

    private static IReadOnlyList<string> GetEffectiveGenericParameters(
        TypedFunctionSignature template,
        int typeArgumentCount)
    {
        if (template.GenericParams.Count > 0 || typeArgumentCount == 0)
        {
            return template.GenericParams;
        }

        var inferred = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(template.ReturnType);
        foreach (var parameter in template.Parameters)
        {
            Visit(parameter.Type);
        }

        return inferred.Count == typeArgumentCount ? inferred : [];

        void Visit(StarkTypeSymbol type)
        {
            var coreType = StripQualifiers(type);
            if (coreType.Kind == StarkTypeKind.Named
                && coreType.NamedType is { } name
                && !name.Contains('.', StringComparison.Ordinal)
                && coreType.TypeArguments is not { Count: > 0 }
                && seen.Add(name))
            {
                inferred.Add(name);
            }

            if (coreType.TypeArguments is { Count: > 0 })
            {
                foreach (var argument in coreType.TypeArguments)
                {
                    Visit(argument);
                }
            }

            if (coreType.ElementType is not null)
            {
                Visit(coreType.ElementType);
            }
        }
    }

    private static bool TryResolveCandidate(
        TypedFunctionSignature candidate,
        StarkTypeSymbol? receiverType,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver,
        out TypedFunctionSignature resolvedCandidate,
        out int exactMatches,
        out int conversionCost,
        out int genericPenalty)
    {
        resolvedCandidate = candidate;
        exactMatches = 0;
        conversionCost = 0;
        genericPenalty = 0;

        var receiverOffset = receiverType is null ? 0 : 1;
        if (candidate.Parameters.Count < receiverOffset)
        {
            return false;
        }

        var explicitParameterCount = candidate.Parameters.Count - receiverOffset;
        if (candidate.IsVarargs)
        {
            if (argumentTypes.Count < explicitParameterCount)
            {
                return false;
            }
        }
        else if (explicitParameterCount != argumentTypes.Count)
        {
            return false;
        }

        if (candidate.IsGeneric)
        {
            if (!TryInstantiateGenericCandidate(candidate, receiverType, argumentTypes, associatedTypeResolver, out resolvedCandidate))
            {
                return false;
            }

            genericPenalty = candidate.GenericParams.Count + candidate.ComptimeGenericParams.Count;
        }

        if (receiverType is not null)
        {
            var receiverParameterType = GetOverloadArgumentParameterType(resolvedCandidate.Parameters[0]);
            if (!CanBindReceiver(receiverParameterType, receiverType, canAssign))
            {
                return false;
            }

            conversionCost += GetBindingCost(receiverParameterType, receiverType);
            if (IsExactOverloadTypeMatch(receiverParameterType, receiverType))
            {
                exactMatches++;
            }
        }

        for (var index = 0; index < explicitParameterCount; index++)
        {
            var parameterType = GetOverloadArgumentParameterType(resolvedCandidate.Parameters[index + receiverOffset]);
            var argumentType = argumentTypes[index];
            if (!CanBindParameter(parameterType, argumentType, canAssign))
            {
                return false;
            }

            conversionCost += GetBindingCost(parameterType, argumentType);
            if (IsExactOverloadTypeMatch(parameterType, argumentType))
            {
                exactMatches++;
            }
        }

        return true;
    }

    private static int GetBindingCost(StarkTypeSymbol targetType, StarkTypeSymbol sourceType)
    {
        var target = StripQualifiers(targetType);
        var source = StripQualifiers(sourceType);

        if (IsExactOverloadTypeMatch(target, source))
        {
            return 0;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Kind == StarkTypeKind.Integer)
        {
            var widthCost = target.BitWidth is int targetWidth && source.BitWidth is int sourceWidth
                ? Math.Max(0, targetWidth - sourceWidth)
                : 128;
            var signednessCost = target.IsUnsigned == source.IsUnsigned ? 0 : 64;
            return 10 + widthCost + signednessCost;
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Float)
        {
            var widthCost = target.BitWidth is int targetWidth && source.BitWidth is int sourceWidth
                ? Math.Max(0, targetWidth - sourceWidth)
                : 128;
            return 100 + widthCost;
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Integer)
        {
            var widthCost = source.BitWidth ?? 128;
            return 1_000 + widthCost;
        }

        return 10_000;
    }

    private static bool IsExactOverloadTypeMatch(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        return string.Equals(
            BuildCanonicalTypeKey(left),
            BuildCanonicalTypeKey(right),
            StringComparison.Ordinal);
    }

    private static bool TryInstantiateGenericCandidate(
        TypedFunctionSignature candidate,
        StarkTypeSymbol? receiverType,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver,
        out TypedFunctionSignature instantiated)
    {
        instantiated = candidate;

        if (!candidate.IsGeneric)
        {
            return true;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        var genericParameters = candidate.GenericParams.ToHashSet(StringComparer.Ordinal);
        var valueSubstitution = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        var comptimeGenericParameters = candidate.ComptimeGenericParams
            .ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        var comptimeGenericParameterNames = comptimeGenericParameters.Keys.ToHashSet(StringComparer.Ordinal);
        var receiverOffset = receiverType is null ? 0 : 1;

        if (receiverType is not null)
        {
            var receiverParameterType = SubstituteType(
                GetOverloadArgumentParameterType(candidate.Parameters[0]),
                substitution,
                associatedTypeResolver,
                valueSubstitution);
            if ((ContainsGenericParameter(receiverParameterType, genericParameters)
                    || ContainsComptimeValueParameter(receiverParameterType, comptimeGenericParameterNames))
                && !TryInferTypeArguments(
                    receiverParameterType,
                    receiverType,
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution))
            {
                return false;
            }
        }

        for (var index = 0; index < argumentTypes.Count; index++)
        {
            var parameter = candidate.Parameters[index + receiverOffset];
            var parameterType = SubstituteType(
                GetOverloadArgumentParameterType(parameter),
                substitution,
                associatedTypeResolver,
                valueSubstitution);
            if ((ContainsGenericParameter(parameterType, genericParameters)
                    || ContainsComptimeValueParameter(parameterType, comptimeGenericParameterNames))
                && !TryInferTypeArguments(
                    parameterType,
                    argumentTypes[index],
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution))
            {
                return false;
            }
        }

        if (candidate.GenericParams.Any(parameter => !substitution.ContainsKey(parameter))
            || candidate.ComptimeGenericParams.Any(parameter => !valueSubstitution.ContainsKey(parameter.Name)))
        {
            return false;
        }

        var valueArguments = candidate.ComptimeGenericParams
            .Select(parameter => new ComptimeValueArgumentSymbol(
                parameter.Name,
                valueSubstitution[parameter.Name],
                parameter.Type))
            .ToArray();
        instantiated = candidate with
        {
            ReturnType = SubstituteType(candidate.ReturnType, substitution, associatedTypeResolver, valueSubstitution),
            Parameters = candidate.Parameters
                .Select(parameter => new TypedParameterSymbol(
                    parameter.Name,
                    SubstituteType(parameter.Type, substitution, associatedTypeResolver, valueSubstitution),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            GenericParameterNames = null,
            ComptimeGenericParameterNames = null,
            TemplateName = candidate.TemplateName ?? candidate.Name,
            TypeArguments = candidate.GenericParams.Select(parameter => substitution[parameter]).ToArray(),
            ComptimeValueArguments = valueArguments,
            ThreadSafetyLawPredicates = SubstituteThreadSafetyLawPredicates(
                candidate.ThreadSafetyLaws,
                substitution,
                associatedTypeResolver,
                valueSubstitution)
        };
        return true;
    }

    private static IReadOnlyList<ThreadSafetyLawPredicateSymbol>? SubstituteThreadSafetyLawPredicates(
        IReadOnlyList<ThreadSafetyLawPredicateSymbol> predicates,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver,
        IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution)
    {
        if (predicates.Count == 0)
        {
            return null;
        }

        return predicates
            .Select(predicate => predicate with
            {
                Type = SubstituteType(predicate.Type, substitution, associatedTypeResolver, comptimeValueSubstitution)
            })
            .ToArray();
    }

    private static bool CanBindParameter(
        StarkTypeSymbol parameterType,
        StarkTypeSymbol argumentType,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool> canAssign)
    {
        if (canAssign(parameterType, argumentType))
        {
            return true;
        }

        if (parameterType.AccessKind == StarkAccessKind.Frozen
            && canAssign(parameterType, StarkTypeSymbols.FreezeReachableView(argumentType)))
        {
            return true;
        }

        if (parameterType.BorrowKind != StarkBorrowKind.None)
        {
            var strippedParameterType = StripQualifiers(parameterType);
            var strippedArgumentType = StripQualifiers(argumentType);
            if (canAssign(strippedParameterType, strippedArgumentType))
            {
                return !parameterType.IsMutableView || argumentType.AccessKind != StarkAccessKind.Frozen;
            }
        }

        if (parameterType.InitializationKind == StarkInitializationKind.None)
        {
            return false;
        }

        var parameterStorageType = StarkTypeSymbols.WithQualifiers(
            parameterType,
            initializationKind: StarkInitializationKind.None);
        var argumentStorageType = argumentType.InitializationKind == StarkInitializationKind.None
            ? argumentType
            : StarkTypeSymbols.WithQualifiers(argumentType, initializationKind: StarkInitializationKind.None);
        return canAssign(parameterStorageType, argumentStorageType);
    }

    private static StarkTypeSymbol GetOverloadArgumentParameterType(TypedParameterSymbol parameter)
    {
        return parameter.IsConst
            ? StarkTypeSymbols.FreezeReachableView(parameter.Type)
            : parameter.Type;
    }

    private static bool TryInferTypeArguments(
        StarkTypeSymbol parameterType,
        StarkTypeSymbol argumentType,
        ISet<string> genericParameters,
        IDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol> comptimeGenericParameters,
        IDictionary<string, BigInteger> valueSubstitution)
    {
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
                substitution,
                comptimeGenericParameters,
                valueSubstitution);
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
                    || (strippedParameterType.TypeArguments ?? []).Count != (strippedArgumentType.TypeArguments ?? []).Count
                    || (strippedParameterType.ComptimeValueArguments ?? []).Count != (strippedArgumentType.ComptimeValueArguments ?? []).Count)
                {
                    return false;
                }

                var parameterTypeArguments = strippedParameterType.TypeArguments ?? [];
                var argumentTypeArguments = strippedArgumentType.TypeArguments ?? [];
                for (var index = 0; index < parameterTypeArguments.Count; index++)
                {
                    if (!TryInferTypeArguments(
                            parameterTypeArguments[index],
                            argumentTypeArguments[index],
                            genericParameters,
                            substitution,
                            comptimeGenericParameters,
                            valueSubstitution))
                    {
                        return false;
                    }
                }

                var parameterValueArguments = strippedParameterType.ComptimeValueArguments ?? [];
                var argumentValueArguments = strippedArgumentType.ComptimeValueArguments ?? [];
                for (var index = 0; index < parameterValueArguments.Count; index++)
                {
                    var parameterValue = parameterValueArguments[index];
                    var argumentValue = argumentValueArguments[index];
                    if (!string.Equals(parameterValue.ParameterName, argumentValue.ParameterName, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (!TryBindComptimeValueParameter(
                            parameterValue,
                            argumentValue.IntegerValue,
                            comptimeGenericParameters,
                            valueSubstitution))
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
            return TryInferFixedArrayLength(
                    strippedParameterType,
                    strippedArgumentType,
                    comptimeGenericParameters,
                    valueSubstitution)
                && strippedParameterType.ElementType is not null
                && strippedArgumentType.ElementType is not null
                && TryInferTypeArguments(
                    strippedParameterType.ElementType,
                    strippedArgumentType.ElementType,
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution);
        }

        if (strippedParameterType.Kind == StarkTypeKind.Slice)
        {
            return strippedParameterType.ElementType is not null
                && strippedArgumentType.ElementType is not null
                && TryInferTypeArguments(
                    strippedParameterType.ElementType,
                    strippedArgumentType.ElementType,
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution);
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
                            substitution,
                            comptimeGenericParameters,
                            valueSubstitution)));
        }

        if (strippedParameterType.Kind == StarkTypeKind.FunctionPointer)
        {
            if (strippedParameterType.FunctionPointerKind != strippedArgumentType.FunctionPointerKind
                || strippedParameterType.FunctionPointerAbi != strippedArgumentType.FunctionPointerAbi
                || strippedParameterType.FunctionPointerIsUnsafe != strippedArgumentType.FunctionPointerIsUnsafe
                || strippedParameterType.FunctionPointerReturnType is null
                || strippedArgumentType.FunctionPointerReturnType is null
                || strippedParameterType.FunctionPointerParameterTypes is not { } parameterTypes
                || strippedArgumentType.FunctionPointerParameterTypes is not { } argumentParameterTypes
                || parameterTypes.Count != argumentParameterTypes.Count
                || !TryInferTypeArguments(
                    strippedParameterType.FunctionPointerReturnType,
                    strippedArgumentType.FunctionPointerReturnType,
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution))
            {
                return false;
            }

            for (var index = 0; index < parameterTypes.Count; index++)
            {
                if (!TryInferTypeArguments(
                        parameterTypes[index],
                        argumentParameterTypes[index],
                        genericParameters,
                        substitution,
                        comptimeGenericParameters,
                        valueSubstitution))
                {
                    return false;
                }
            }

            return true;
        }

        if (strippedParameterType.Kind == StarkTypeKind.Closure)
        {
            if (strippedArgumentType.Kind != StarkTypeKind.Closure
                || strippedParameterType.ClosureStorageKind != strippedArgumentType.ClosureStorageKind
                || strippedParameterType.ClosureCallCapability != strippedArgumentType.ClosureCallCapability
                || strippedParameterType.ClosureFunctionKind != strippedArgumentType.ClosureFunctionKind
                || strippedParameterType.ClosureReturnType is null
                || strippedArgumentType.ClosureReturnType is null
                || strippedParameterType.ClosureParameterTypes is not { } parameterTypes
                || strippedArgumentType.ClosureParameterTypes is not { } argumentParameterTypes
                || parameterTypes.Count != argumentParameterTypes.Count
                || !TryInferTypeArguments(
                    strippedParameterType.ClosureReturnType,
                    strippedArgumentType.ClosureReturnType,
                    genericParameters,
                    substitution,
                    comptimeGenericParameters,
                    valueSubstitution))
            {
                return false;
            }

            for (var index = 0; index < parameterTypes.Count; index++)
            {
                if (!TryInferTypeArguments(
                        parameterTypes[index],
                        argumentParameterTypes[index],
                        genericParameters,
                        substitution,
                        comptimeGenericParameters,
                        valueSubstitution))
                {
                    return false;
                }
            }

            return true;
        }

        return strippedParameterType == strippedArgumentType;
    }

    private static bool TryInferFixedArrayLength(
        StarkTypeSymbol parameterType,
        StarkTypeSymbol argumentType,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol> comptimeGenericParameters,
        IDictionary<string, BigInteger> valueSubstitution)
    {
        if (!string.IsNullOrWhiteSpace(parameterType.FixedLengthParameterName))
        {
            if (argumentType.FixedLength is not int argumentLength
                || !comptimeGenericParameters.TryGetValue(parameterType.FixedLengthParameterName, out var parameter))
            {
                return false;
            }

            return TryBindComptimeValueParameter(parameter, argumentLength, valueSubstitution);
        }

        return parameterType.FixedLength == argumentType.FixedLength;
    }

    private static bool TryBindComptimeValueParameter(
        ComptimeValueArgumentSymbol parameterValue,
        BigInteger inferredValue,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol> comptimeGenericParameters,
        IDictionary<string, BigInteger> valueSubstitution)
    {
        var parameterName = parameterValue.IsSymbolic
            ? parameterValue.SourceName
            : parameterValue.ParameterName;
        if (!comptimeGenericParameters.TryGetValue(parameterName, out var parameter))
        {
            return parameterValue.IntegerValue == inferredValue;
        }

        if (!parameterValue.IsSymbolic
            && parameterValue.IntegerValue != inferredValue)
        {
            return false;
        }

        return TryBindComptimeValueParameter(parameter, inferredValue, valueSubstitution);
    }

    private static bool TryBindComptimeValueParameter(
        ComptimeGenericParameterSymbol parameter,
        BigInteger inferredValue,
        IDictionary<string, BigInteger> valueSubstitution)
    {
        if (parameter.Type.Kind != StarkTypeKind.Integer
            || !StarkTypeSymbols.IntegerValueFitsEffectiveRange(inferredValue, parameter.Type))
        {
            return false;
        }

        if (valueSubstitution.TryGetValue(parameter.Name, out var existing))
        {
            return existing == inferredValue;
        }

        valueSubstitution[parameter.Name] = inferredValue;
        return true;
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

        if (strippedType.Kind == StarkTypeKind.FunctionPointer)
        {
            return strippedType.FunctionPointerReturnType is not null
                   && ContainsGenericParameter(strippedType.FunctionPointerReturnType, genericParameters)
                   || strippedType.FunctionPointerParameterTypes is { Count: > 0 }
                   && strippedType.FunctionPointerParameterTypes.Any(parameter => ContainsGenericParameter(parameter, genericParameters));
        }

        if (strippedType.Kind == StarkTypeKind.Closure)
        {
            return strippedType.ClosureReturnType is not null
                   && ContainsGenericParameter(strippedType.ClosureReturnType, genericParameters)
                   || strippedType.ClosureParameterTypes is { Count: > 0 }
                   && strippedType.ClosureParameterTypes.Any(parameter => ContainsGenericParameter(parameter, genericParameters));
        }

        if (strippedType.Kind == StarkTypeKind.AssociatedType)
        {
            return strippedType.AssociatedTypeOwner is not null
                && ContainsGenericParameter(strippedType.AssociatedTypeOwner, genericParameters);
        }

        return strippedType.ElementType is not null
            && ContainsGenericParameter(strippedType.ElementType, genericParameters);
    }

    public static bool ContainsComptimeValueParameter(StarkTypeSymbol type, IEnumerable<string> parameterNames)
    {
        var parameterSet = parameterNames as ISet<string> ?? parameterNames.ToHashSet(StringComparer.Ordinal);
        return ContainsComptimeValueParameterCore(type, parameterSet);
    }

    private static bool ContainsComptimeValueParameterCore(StarkTypeSymbol type, ISet<string> parameterNames)
    {
        var strippedType = StripQualifiers(type);
        if (!string.IsNullOrWhiteSpace(strippedType.FixedLengthParameterName)
            && parameterNames.Contains(strippedType.FixedLengthParameterName))
        {
            return true;
        }

        if (strippedType.TypeArguments is { Count: > 0 }
            && strippedType.TypeArguments.Any(argument => ContainsComptimeValueParameterCore(argument, parameterNames)))
        {
            return true;
        }

        if (strippedType.ComptimeValueArguments is { Count: > 0 }
            && strippedType.ComptimeValueArguments.Any(argument => argument.IsSymbolic && parameterNames.Contains(argument.SourceName)))
        {
            return true;
        }

        if (strippedType.Kind == StarkTypeKind.FunctionPointer)
        {
            return strippedType.FunctionPointerReturnType is not null
                   && ContainsComptimeValueParameterCore(strippedType.FunctionPointerReturnType, parameterNames)
                   || strippedType.FunctionPointerParameterTypes is { Count: > 0 }
                   && strippedType.FunctionPointerParameterTypes.Any(parameter => ContainsComptimeValueParameterCore(parameter, parameterNames));
        }

        if (strippedType.Kind == StarkTypeKind.Closure)
        {
            return strippedType.ClosureReturnType is not null
                   && ContainsComptimeValueParameterCore(strippedType.ClosureReturnType, parameterNames)
                   || strippedType.ClosureParameterTypes is { Count: > 0 }
                   && strippedType.ClosureParameterTypes.Any(parameter => ContainsComptimeValueParameterCore(parameter, parameterNames));
        }

        if (strippedType.Kind == StarkTypeKind.AssociatedType)
        {
            return strippedType.AssociatedTypeOwner is not null
                && ContainsComptimeValueParameterCore(strippedType.AssociatedTypeOwner, parameterNames);
        }

        return strippedType.ElementType is not null
            && ContainsComptimeValueParameterCore(strippedType.ElementType, parameterNames);
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
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        Func<StarkTypeSymbol, string, StarkTypeSymbol?>? associatedTypeResolver = null,
        IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution = null)
    {
        var coreType = StripTopLevelQualifiers(type);
        StarkTypeSymbol substitutedCore;

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (substitution.TryGetValue(name, out var substituted))
            {
                substitutedCore = StripTopLevelQualifiers(substituted);
            }
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType))
            {
                var substitutedArguments = (coreType.TypeArguments ?? [])
                    .Select(argument => SubstituteType(argument, substitution, associatedTypeResolver, comptimeValueSubstitution))
                    .ToArray();
                var substitutedValues = SubstituteComptimeValues(coreType.ComptimeValueArguments, comptimeValueSubstitution);
                substitutedCore = StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(name),
                    substitutedArguments,
                    substitutedValues);
            }
            else
            {
                substitutedCore = coreType;
            }
        }
        else if (coreType.Kind == StarkTypeKind.AssociatedType
            && coreType.AssociatedTypeOwner is not null
            && coreType.AssociatedTypeName is not null)
        {
            var substitutedOwner = SubstituteType(coreType.AssociatedTypeOwner, substitution, associatedTypeResolver, comptimeValueSubstitution);
            var resolvedAssociated = associatedTypeResolver?.Invoke(substitutedOwner, coreType.AssociatedTypeName);
            substitutedCore = resolvedAssociated is not null
                ? StripTopLevelQualifiers(resolvedAssociated)
                : StarkTypeSymbols.AssociatedType(substitutedOwner, coreType.AssociatedTypeName);
        }
        else if (coreType.ElementType is not null)
        {
            var substitutedElement = SubstituteType(coreType.ElementType, substitution, associatedTypeResolver, comptimeValueSubstitution);
            var fixedLength = coreType.FixedLength;
            var fixedLengthParameterName = coreType.FixedLengthParameterName;
            if (fixedLengthParameterName is not null
                && comptimeValueSubstitution is not null
                && comptimeValueSubstitution.TryGetValue(fixedLengthParameterName, out var substitutedLength)
                && substitutedLength >= 0
                && substitutedLength <= int.MaxValue)
            {
                fixedLength = (int)substitutedLength;
                fixedLengthParameterName = null;
            }

            substitutedCore = coreType.Kind switch
            {
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(substitutedElement, fixedLength, fixedLengthParameterName),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(substitutedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(substitutedElement, coreType.IsMutablePointer),
                StarkTypeKind.Dynamic => StarkTypeSymbols.Dynamic(substitutedElement),
                _ => coreType
            };
        }
        else if (coreType.Kind == StarkTypeKind.FunctionPointer
            && coreType.FunctionPointerKind is { } functionKind
            && coreType.FunctionPointerReturnType is { } returnType
            && coreType.FunctionPointerParameterTypes is { } parameterTypes)
        {
            substitutedCore = StarkTypeSymbols.FunctionPointer(
                functionKind,
                SubstituteType(returnType, substitution, associatedTypeResolver, comptimeValueSubstitution),
                parameterTypes.Select(parameter => SubstituteType(parameter, substitution, associatedTypeResolver, comptimeValueSubstitution)).ToArray(),
                coreType.FunctionPointerDisjointParameterGroups,
                coreType.FunctionPointerOverlapParameterGroups,
                coreType.FunctionPointerSameParameterGroups,
                coreType.FunctionPointerParameterRawPointerElementCountExpressions,
                coreType.FunctionPointerAbi,
                coreType.FunctionPointerIsUnsafe,
                coreType.FunctionPointerIsTailCallable,
                coreType.FunctionPointerPointeeDeadOnReturnParameterNames);
        }
        else if (coreType.Kind == StarkTypeKind.Closure
            && coreType.ClosureFunctionKind is { } closureFunctionKind
            && coreType.ClosureReturnType is { } closureReturnType
            && coreType.ClosureParameterTypes is { } closureParameterTypes)
        {
            substitutedCore = StarkTypeSymbols.Closure(
                coreType.ClosureStorageKind,
                coreType.ClosureCallCapability,
                closureFunctionKind,
                SubstituteType(closureReturnType, substitution, associatedTypeResolver, comptimeValueSubstitution),
                closureParameterTypes.Select(parameter => SubstituteType(parameter, substitution, associatedTypeResolver, comptimeValueSubstitution)).ToArray(),
                coreType.ClosureDisjointParameterGroups,
                coreType.ClosureOverlapParameterGroups,
                coreType.ClosureSameParameterGroups,
                coreType.ClosureParameterRawPointerElementCountExpressions,
                coreType.ClosureIsTailCallable,
                coreType.ClosurePointeeDeadOnReturnParameterNames);
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

    public static IReadOnlyList<ComptimeValueArgumentSymbol>? SubstituteComptimeValues(
        IReadOnlyList<ComptimeValueArgumentSymbol>? values,
        IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution)
    {
        if (values is not { Count: > 0 })
        {
            return values;
        }

        if (comptimeValueSubstitution is not { Count: > 0 })
        {
            return values;
        }

        var changed = false;
        var substituted = new ComptimeValueArgumentSymbol[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (comptimeValueSubstitution.TryGetValue(value.SourceName, out var concreteValue))
            {
                substituted[index] = value with
                {
                    IntegerValue = concreteValue,
                    IsSymbolic = false,
                    SymbolicSourceName = null
                };
                changed = true;
            }
            else
            {
                substituted[index] = value;
            }
        }

        return changed ? substituted : values;
    }

    private static StarkTypeSymbol StripTopLevelQualifiers(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private static StarkTypeSymbol StripQualifiers(StarkTypeSymbol type)
    {
        var strippedCore = type.Kind switch
        {
            StarkTypeKind.RawPointer when type.ElementType is not null
                => StarkTypeSymbols.RawPointer(StripQualifiers(type.ElementType), type.IsMutablePointer),
            StarkTypeKind.FixedArray when type.ElementType is not null
                => StarkTypeSymbols.FixedArray(StripQualifiers(type.ElementType), type.FixedLength, type.FixedLengthParameterName),
            StarkTypeKind.Slice when type.ElementType is not null
                => StarkTypeSymbols.Slice(StripQualifiers(type.ElementType)),
            StarkTypeKind.Dynamic when type.ElementType is not null
                => StarkTypeSymbols.Dynamic(StripQualifiers(type.ElementType)),
            StarkTypeKind.Named when (type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 })
                                     && type.NamedType is not null
                => StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(type.NamedType),
                    (type.TypeArguments ?? []).Select(StripQualifiers).ToArray(),
                    type.ComptimeValueArguments),
            StarkTypeKind.Closure when type.ClosureFunctionKind is { } closureFunctionKind
                                       && type.ClosureReturnType is { } closureReturnType
                                       && type.ClosureParameterTypes is { } closureParameterTypes
                => StarkTypeSymbols.Closure(
                    type.ClosureStorageKind,
                    type.ClosureCallCapability,
                    closureFunctionKind,
                    StripQualifiers(closureReturnType),
                    closureParameterTypes.Select(StripQualifiers).ToArray(),
                    type.ClosureDisjointParameterGroups,
                    type.ClosureOverlapParameterGroups,
                    type.ClosureSameParameterGroups,
                    type.ClosureParameterRawPointerElementCountExpressions,
                    type.ClosureIsTailCallable,
                    type.ClosurePointeeDeadOnReturnParameterNames),
            _ => type
        };

        return StarkTypeSymbols.WithQualifiers(
            strippedCore,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private static int CountFunctionsWithSourceName(SyntaxModel syntaxModel, string sourceName)
    {
        return syntaxModel.Declarations.Count(declaration =>
            declaration.Kind == DeclarationKind.Function
            && string.Equals(declaration.Name, sourceName, StringComparison.Ordinal));
    }
}
