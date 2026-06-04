namespace Stark.Compiler;

internal enum SystemCollectionsDictionaryKeyContractKind
{
    CompilerKnownScalar,
    ExplicitStaticMethods
}

internal sealed record SystemCollectionsDictionaryKeyContract(
    SystemCollectionsDictionaryKeyContractKind Kind,
    StarkTypeSymbol KeyType,
    TypedFunctionSignature? HashFunction = null,
    TypedFunctionSignature? EqualsFunction = null)
{
    public bool UsesExplicitStaticMethods => Kind == SystemCollectionsDictionaryKeyContractKind.ExplicitStaticMethods;
}

internal static class SystemCollectionsDictionaryKeyFacts
{
    public const string DictionaryTypeName = "System.Collections.Dictionary";
    public const string DictionaryKeyDoctrineName = "System.Collections.DictionaryKey";
    public const string HashMemberName = "Hash";
    public const string EqualsMemberName = "Equals";

    public static bool TryGetDictionaryKeyType(StarkTypeSymbol type, out StarkTypeSymbol keyType)
    {
        keyType = StarkTypeSymbols.Error;
        var coreType = NormalizeType(type);

        if (!StarkTypeSymbols.IsGenericInstantiation(coreType)
            || coreType.NamedType is null
            || coreType.TypeArguments is not { Count: 2 }
            || StarkTypeSymbols.GetGenericBaseName(coreType.NamedType) is not DictionaryTypeName)
        {
            return false;
        }

        keyType = NormalizeType(coreType.TypeArguments[0]);
        return true;
    }

    public static bool IsCompilerKnownKey(StarkTypeSymbol keyType)
    {
        return NormalizeType(keyType).Kind is StarkTypeKind.Bool or StarkTypeKind.Integer;
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

        if (IsCompilerKnownKey(keyType))
        {
            contract = new SystemCollectionsDictionaryKeyContract(
                SystemCollectionsDictionaryKeyContractKind.CompilerKnownScalar,
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
        return $"Dictionary key type '{keyType.DisplayName}' must satisfy '{DictionaryKeyDoctrineName}<{keyType.DisplayName}>'. Built-in dictionary key contracts are available for 'bool' and Stark integer key types; otherwise declare 'static finite law u64[0 max] Hash(borrow {keyType.DisplayName} value)' and 'static finite law bool Equals(borrow {keyType.DisplayName} left, borrow {keyType.DisplayName} right) where overlap(left, right)' on the key type.";
    }

    private static bool TryResolveStaticMember(
        StarkTypeSymbol keyType,
        string memberName,
        IReadOnlyList<StarkTypeSymbol> argumentTypes,
        Func<string, IReadOnlyList<TypedFunctionSignature>?> resolveFunctionOverloads,
        out TypedFunctionSignature function,
        out string diagnostic)
    {
        var candidates = GetStaticMemberCandidates(keyType, memberName, resolveFunctionOverloads).ToArray();
        if (candidates.Length == 0)
        {
            function = default!;
            diagnostic = FormatMissingContractDiagnostic(keyType);
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
            diagnostic = FormatMissingContractDiagnostic(keyType);
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
            diagnostic = FormatMissingContractDiagnostic(keyType);
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
            diagnostic = FormatMissingContractDiagnostic(keyType);
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
}
