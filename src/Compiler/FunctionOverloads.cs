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
            && string.Equals(BuildOverloadKey(candidate.Function.Parameters), overloadKey, StringComparison.Ordinal));

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
        var matches = new List<(TypedFunctionSignature Signature, int ExactMatches)>();

        foreach (var candidate in candidates)
        {
            var receiverOffset = receiverType is null ? 0 : 1;
            if (candidate.Parameters.Count < receiverOffset)
            {
                continue;
            }

            var explicitParameterCount = candidate.Parameters.Count - receiverOffset;
            if (explicitParameterCount != argumentTypes.Count)
            {
                continue;
            }

            var exactMatches = 0;
            var matched = true;

            if (receiverType is not null)
            {
                var receiverParameterType = candidate.Parameters[0].Type;
                if (!CanBindReceiver(receiverParameterType, receiverType, canAssign))
                {
                    matched = false;
                }
                else if (receiverParameterType == receiverType)
                {
                    exactMatches++;
                }
            }

            if (!matched)
            {
                continue;
            }

            for (var index = 0; index < argumentTypes.Count; index++)
            {
                var parameterType = candidate.Parameters[index + receiverOffset].Type;
                var argumentType = argumentTypes[index];
                if (!canAssign(parameterType, argumentType))
                {
                    matched = false;
                    break;
                }

                if (parameterType == argumentType)
                {
                    exactMatches++;
                }
            }

            if (matched)
            {
                matches.Add((candidate, exactMatches));
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
        var bestMatches = matches
            .Where(match => match.ExactMatches == bestExactMatchCount)
            .Select(static match => match.Signature)
            .ToArray();

        return bestMatches.Length == 1
            ? new OverloadResolutionResult(bestMatches[0], OverloadResolutionFailureKind.None, bestMatches)
            : new OverloadResolutionResult(null, OverloadResolutionFailureKind.Ambiguous, bestMatches);
    }

    public static string FormatSignature(TypedFunctionSignature signature)
    {
        return $"{signature.DisplaySourceName}({string.Join(", ", signature.Parameters.Select(static parameter => parameter.Type.DisplayName))})";
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
