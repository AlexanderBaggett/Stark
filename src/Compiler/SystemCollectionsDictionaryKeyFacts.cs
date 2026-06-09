namespace Stark.Compiler;

internal enum SystemCollectionsDictionaryKeyContractKind
{
    CompilerKnownScalar,
    CompilerKnownText,
    ExplicitStaticMethods
}

internal sealed record SystemCollectionsDictionaryKeyContract(
    SystemCollectionsDictionaryKeyContractKind Kind,
    StarkTypeSymbol KeyType,
    TypedFunctionSignature? HashFunction = null,
    TypedFunctionSignature? EqualsFunction = null)
{
    public bool UsesExplicitStaticMethods => Kind == SystemCollectionsDictionaryKeyContractKind.ExplicitStaticMethods;

    public bool UsesCompilerKnownScalar => Kind == SystemCollectionsDictionaryKeyContractKind.CompilerKnownScalar;

    public bool UsesCompilerKnownText => Kind == SystemCollectionsDictionaryKeyContractKind.CompilerKnownText;
}

internal static class SystemCollectionsDictionaryKeyFacts
{
    public const string DictionaryTypeName = "System.Collections.Dictionary";
    public const string HashSetTypeName = "System.Collections.HashSet";
    public const string DictionaryKeyDoctrineName = "System.Collections.DictionaryKey";
    public const string HashMemberName = "Hash";
    public const string EqualsMemberName = "Equals";

    public static bool TryGetDictionaryKeyType(StarkTypeSymbol type, out StarkTypeSymbol keyType)
    {
        keyType = StarkTypeSymbols.Error;
        var coreType = NormalizeType(type);

        if (!StarkTypeSymbols.IsGenericInstantiation(coreType)
            || coreType.NamedType is null
            || coreType.TypeArguments is not { } typeArguments)
        {
            return false;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(coreType.NamedType);
        if (baseName is DictionaryTypeName && typeArguments.Count == 2)
        {
            keyType = NormalizeType(typeArguments[0]);
            return true;
        }

        if (baseName is HashSetTypeName && typeArguments.Count == 1)
        {
            keyType = NormalizeType(typeArguments[0]);
            return true;
        }

        return false;
    }

    public static bool IsCompilerKnownKey(StarkTypeSymbol keyType)
    {
        var normalized = NormalizeType(keyType);
        return normalized.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode;
    }

    public static bool TryResolveContract(
        StarkTypeSymbol keyType,
        IReadOnlyDictionary<string, IReadOnlyList<TypedFunctionSignature>> functionOverloads,
        out SystemCollectionsDictionaryKeyContract contract,
        out string diagnostic)
    {
        return TryResolveContract(
            keyType,
            sourceName => functionOverloads.TryGetValue(sourceName, out var candidates) ? candidates : null,
            out contract,
            out diagnostic);
    }

    public static bool TryResolveContract(
        StarkTypeSymbol keyType,
        Func<string, IReadOnlyList<TypedFunctionSignature>?> resolveFunctionOverloads,
        out SystemCollectionsDictionaryKeyContract contract,
        out string diagnostic)
    {
        keyType = NormalizeType(keyType);
        diagnostic = string.Empty;

        if (IsCompilerKnownScalarKey(keyType))
        {
            contract = new SystemCollectionsDictionaryKeyContract(
                SystemCollectionsDictionaryKeyContractKind.CompilerKnownScalar,
                keyType);
            return true;
        }

        if (IsCompilerKnownTextKey(keyType))
        {
            contract = new SystemCollectionsDictionaryKeyContract(
                SystemCollectionsDictionaryKeyContractKind.CompilerKnownText,
                keyType);
            return true;
        }

        if (keyType.Kind != StarkTypeKind.Named || keyType.NamedType is null)
        {
            contract = default!;
            diagnostic = FormatMissingContractDiagnostic(keyType);
            return false;
        }

        var borrowedKeyType = StarkTypeSymbols.WithQualifiers(
            keyType,
            borrowKind: StarkBorrowKind.Borrow,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

        if (!TryResolveStaticMember(
                keyType,
                HashMemberName,
                [borrowedKeyType],
                resolveFunctionOverloads,
                FormatExpectedHashSignature(keyType),
                out var hashFunction,
                out diagnostic))
        {
            contract = default!;
            return false;
        }

        if (!ValidateHashFunction(keyType, hashFunction, out diagnostic))
        {
            contract = default!;
            return false;
        }

        if (!TryResolveStaticMember(
                keyType,
                EqualsMemberName,
                [borrowedKeyType, borrowedKeyType],
                resolveFunctionOverloads,
                FormatExpectedEqualsSignature(keyType),
                out var equalsFunction,
                out diagnostic))
        {
            contract = default!;
            return false;
        }

        if (!ValidateEqualsFunction(keyType, equalsFunction, out diagnostic))
        {
            contract = default!;
            return false;
        }

        contract = new SystemCollectionsDictionaryKeyContract(
            SystemCollectionsDictionaryKeyContractKind.ExplicitStaticMethods,
            keyType,
            hashFunction,
            equalsFunction);
        return true;
    }

    public static StarkTypeSymbol NormalizeType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    public static string FormatMissingContractDiagnostic(StarkTypeSymbol keyType)
    {
        return $"{FormatContractIntro(keyType)} Built-in dictionary key contracts are available for 'bool', Stark integer key types, 'ascii', and 'unicode'; otherwise declare '{FormatExpectedHashSignature(keyType)}' and '{FormatExpectedEqualsSignature(keyType)}' on the key type.";
    }

    private static bool IsCompilerKnownScalarKey(StarkTypeSymbol keyType)
    {
        return NormalizeType(keyType).Kind is StarkTypeKind.Bool or StarkTypeKind.Integer;
    }

    private static bool IsCompilerKnownTextKey(StarkTypeSymbol keyType)
    {
        return NormalizeType(keyType).Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static bool TryResolveStaticMember(
        StarkTypeSymbol keyType,
        string memberName,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<string, IReadOnlyList<TypedFunctionSignature>?> resolveFunctionOverloads,
        string expectedSignature,
        out TypedFunctionSignature function,
        out string diagnostic)
    {
        var candidates = GetStaticMemberCandidates(keyType, memberName, resolveFunctionOverloads).ToArray();
        if (candidates.Length == 0)
        {
            function = default!;
            diagnostic = FormatMissingContractMethodDiagnostic(keyType, memberName, expectedSignature);
            return false;
        }

        var resolution = FunctionOverloadFacts.Resolve(
            candidates,
            receiverType: null,
            argumentTypes,
            TypeCompatibilityFacts.CanAssign);
        if (!resolution.Succeeded || resolution.Match is null)
        {
            function = default!;
            diagnostic = FormatIncompatibleContractOverloadDiagnostic(
                keyType,
                memberName,
                expectedSignature,
                resolution.Candidates.Count == 0 ? candidates : resolution.Candidates,
                resolution.Failure);
            return false;
        }

        function = resolution.Match;
        diagnostic = string.Empty;
        return true;
    }

    private static IEnumerable<TypedFunctionSignature> GetStaticMemberCandidates(
        StarkTypeSymbol keyType,
        string memberName,
        Func<string, IReadOnlyList<TypedFunctionSignature>?> resolveFunctionOverloads)
    {
        if (keyType.NamedType is null)
        {
            yield break;
        }

        var sourceName = $"{StarkTypeSymbols.GetGenericBaseName(keyType.NamedType)}.{memberName}";
        var candidates = resolveFunctionOverloads(sourceName);
        if (candidates is null)
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsStatic)
            {
                yield return candidate;
            }
        }
    }

    private static bool ValidateHashFunction(
        StarkTypeSymbol keyType,
        TypedFunctionSignature function,
        out string diagnostic)
    {
        if (!TypeCompatibilityFacts.FunctionKindSatisfies(function.Kind, StarkFunctionKind.FiniteLaw)
            || !IsU64(function.ReturnType)
            || function.Parameters.Count != 1
            || !IsBorrowedKeyParameter(function.Parameters[0], keyType))
        {
            diagnostic = FormatInvalidHashFunctionDiagnostic(keyType, function);
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateEqualsFunction(
        StarkTypeSymbol keyType,
        TypedFunctionSignature function,
        out string diagnostic)
    {
        if (!TypeCompatibilityFacts.FunctionKindSatisfies(function.Kind, StarkFunctionKind.FiniteLaw)
            || function.ReturnType.Kind != StarkTypeKind.Bool
            || function.Parameters.Count != 2
            || !IsBorrowedKeyParameter(function.Parameters[0], keyType)
            || !IsBorrowedKeyParameter(function.Parameters[1], keyType)
            || !AllowsParameterOverlap(function, function.Parameters[0].Name, function.Parameters[1].Name))
        {
            diagnostic = FormatInvalidEqualsFunctionDiagnostic(keyType, function);
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool IsBorrowedKeyParameter(TypedParameterSymbol parameter, StarkTypeSymbol keyType)
    {
        return parameter.Type.BorrowKind != StarkBorrowKind.None
            && NormalizeType(parameter.Type) == keyType;
    }

    private static bool IsU64(StarkTypeSymbol type)
    {
        var normalized = NormalizeType(type);
        return normalized.Kind == StarkTypeKind.Integer
            && normalized.BitWidth == 64
            && normalized.IsUnsigned;
    }

    private static bool AllowsParameterOverlap(
        TypedFunctionSignature function,
        string leftParameterName,
        string rightParameterName)
    {
        return function.OverlapGroups.Any(group =>
            group.ParameterNames.Contains(leftParameterName, StringComparer.Ordinal)
            && group.ParameterNames.Contains(rightParameterName, StringComparer.Ordinal));
    }

    private static string FormatContractIntro(StarkTypeSymbol keyType)
    {
        return $"Dictionary key type '{keyType.DisplayName}' must satisfy '{DictionaryKeyDoctrineName}<{keyType.DisplayName}>'.";
    }

    private static string FormatExpectedHashSignature(StarkTypeSymbol keyType)
    {
        return $"static finite law u64[0 max] Hash(borrow {keyType.DisplayName} value)";
    }

    private static string FormatExpectedEqualsSignature(StarkTypeSymbol keyType)
    {
        return $"static finite law bool Equals(borrow {keyType.DisplayName} left, borrow {keyType.DisplayName} right) where overlap(left, right)";
    }

    private static string FormatMissingContractMethodDiagnostic(
        StarkTypeSymbol keyType,
        string memberName,
        string expectedSignature)
    {
        return $"{FormatContractIntro(keyType)} Missing required {memberName} contract method. Declare '{expectedSignature}' on the key type.";
    }

    private static string FormatIncompatibleContractOverloadDiagnostic(
        StarkTypeSymbol keyType,
        string memberName,
        string expectedSignature,
        IReadOnlyList<TypedFunctionSignature> candidates,
        OverloadResolutionFailureKind failureKind)
    {
        var failureText = failureKind == OverloadResolutionFailureKind.Ambiguous
            ? "the matching overloads are ambiguous"
            : "no overload accepts the required borrowed key parameter shape";
        return $"{FormatContractIntro(keyType)} Incompatible {memberName} contract method: expected '{expectedSignature}', but {failureText}. Candidate(s): {FormatCandidateList(candidates)}.";
    }

    private static string FormatInvalidHashFunctionDiagnostic(
        StarkTypeSymbol keyType,
        TypedFunctionSignature function)
    {
        var reason = !TypeCompatibilityFacts.FunctionKindSatisfies(function.Kind, StarkFunctionKind.FiniteLaw)
            ? "must be a 'finite law'"
            : !IsU64(function.ReturnType)
                ? "must return 'u64[0 max]'"
                : function.Parameters.Count != 1
                    ? "must take exactly one key parameter"
                    : !IsBorrowedKeyParameter(function.Parameters[0], keyType)
                        ? $"parameter 1 must be 'borrow {keyType.DisplayName}'"
                        : "does not match the required Hash contract shape";

        return FormatInvalidContractFunctionDiagnostic(
            keyType,
            HashMemberName,
            FormatExpectedHashSignature(keyType),
            function,
            reason);
    }

    private static string FormatInvalidEqualsFunctionDiagnostic(
        StarkTypeSymbol keyType,
        TypedFunctionSignature function)
    {
        var reason = !TypeCompatibilityFacts.FunctionKindSatisfies(function.Kind, StarkFunctionKind.FiniteLaw)
            ? "must be a 'finite law'"
            : function.ReturnType.Kind != StarkTypeKind.Bool
                ? "must return 'bool'"
                : function.Parameters.Count != 2
                    ? "must take exactly two key parameters"
                    : !IsBorrowedKeyParameter(function.Parameters[0], keyType)
                        ? $"parameter 1 must be 'borrow {keyType.DisplayName}'"
                        : !IsBorrowedKeyParameter(function.Parameters[1], keyType)
                            ? $"parameter 2 must be 'borrow {keyType.DisplayName}'"
                            : !AllowsParameterOverlap(function, function.Parameters[0].Name, function.Parameters[1].Name)
                                ? "must include 'where overlap(left, right)' for the two borrowed key parameters"
                                : "does not match the required Equals contract shape";

        return FormatInvalidContractFunctionDiagnostic(
            keyType,
            EqualsMemberName,
            FormatExpectedEqualsSignature(keyType),
            function,
            reason);
    }

    private static string FormatInvalidContractFunctionDiagnostic(
        StarkTypeSymbol keyType,
        string memberName,
        string expectedSignature,
        TypedFunctionSignature function,
        string reason)
    {
        return $"{FormatContractIntro(keyType)} Incompatible {memberName} contract method: expected '{expectedSignature}', but '{FunctionOverloadFacts.FormatSignature(function)}' {reason}.";
    }

    private static string FormatCandidateList(IReadOnlyList<TypedFunctionSignature> candidates)
    {
        return candidates.Count == 0
            ? "<none>"
            : string.Join(", ", candidates.Select(FunctionOverloadFacts.FormatSignature));
    }
}
