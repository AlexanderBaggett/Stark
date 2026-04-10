using System.Globalization;
using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed record TypeAliasResolutionSource(
    string LookupName,
    string ModuleName,
    StarkVisibility Visibility,
    bool IsExternal,
    IReadOnlyList<string> GenericParameters,
    StarkParser.Type_Context TargetType,
    IToken NameToken);

internal sealed class StarkTypeResolver
{
    private readonly CompilerPassContext _context;
    private readonly string _stage;
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
    private readonly IReadOnlyDictionary<string, TypeAliasSymbol> _typeAliases;
    private readonly IReadOnlyDictionary<string, TypeAliasResolutionSource> _typeAliasSources;
    private readonly Dictionary<string, TypeAliasSymbol>? _mutableTypeAliases;
    private readonly HashSet<string> _resolvingTypeAliases = new(StringComparer.Ordinal);

    public StarkTypeResolver(
        CompilerPassContext context,
        string stage,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        : this(context, stage, moduleGraph, namedTypes, typeAliases: null)
    {
    }

    public StarkTypeResolver(
        CompilerPassContext context,
        string stage,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, TypeAliasSymbol>? typeAliases)
        : this(context, stage, moduleGraph, namedTypes, typeAliases, typeAliasSources: null)
    {
    }

    internal StarkTypeResolver(
        CompilerPassContext context,
        string stage,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        Dictionary<string, TypeAliasSymbol> typeAliases,
        IReadOnlyDictionary<string, TypeAliasResolutionSource> typeAliasSources)
    {
        _context = context;
        _stage = stage;
        _namedTypes = namedTypes;
        _typeAliases = typeAliases;
        _typeAliasSources = typeAliasSources;
        _mutableTypeAliases = typeAliases;
    }

    private StarkTypeResolver(
        CompilerPassContext context,
        string stage,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, TypeAliasSymbol>? typeAliases,
        IReadOnlyDictionary<string, TypeAliasResolutionSource>? typeAliasSources)
    {
        _context = context;
        _stage = stage;
        _namedTypes = namedTypes;
        _typeAliases = typeAliases ?? EmptyTypeAliases;
        _typeAliasSources = typeAliasSources ?? EmptyTypeAliasSources;
        _mutableTypeAliases = typeAliases as Dictionary<string, TypeAliasSymbol>;
    }

    private static IReadOnlyDictionary<string, TypeAliasSymbol> EmptyTypeAliases { get; } =
        new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, TypeAliasResolutionSource> EmptyTypeAliasSources { get; } =
        new Dictionary<string, TypeAliasResolutionSource>(StringComparer.Ordinal);

    public StarkTypeSymbol ResolveReturnType(StarkParser.ReturnTypeContext returnType, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        return returnType.VOID() is not null
            ? StarkTypeSymbols.Void
            : ResolveType(returnType.type_(), genericParameters, currentModuleName);
    }

    public StarkTypeSymbol ResolveType(StarkParser.Type_Context type, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        var result = ResolveNonArrayType(type.nonArrayType(), genericParameters, currentModuleName);

        foreach (var suffix in type.arraySuffix())
        {
            if (suffix.expression() is null)
            {
                result = StarkTypeSymbols.Slice(result);
                continue;
            }

            var length = TryEvaluateConstantInteger(suffix.expression());
            if (length is null)
            {
                ReportError("STK3014", "Fixed array lengths must currently be constant integer expressions.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            if (length < 0 || length > int.MaxValue)
            {
                ReportError("STK3014", $"Fixed array length '{length}' is out of range.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            result = StarkTypeSymbols.FixedArray(result, (int)length.Value);
        }

        return ApplyQualifiers(result, type.typeQualifier());
    }

    public StarkTypeSymbol ResolveConversionType(StarkParser.ConversionTypeContext type, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        var result = ResolveConversionNonArrayType(type.conversionNonArrayType(), genericParameters, currentModuleName);

        foreach (var suffix in type.arraySuffix())
        {
            if (suffix.expression() is null)
            {
                result = StarkTypeSymbols.Slice(result);
                continue;
            }

            var length = TryEvaluateConstantInteger(suffix.expression());
            if (length is null)
            {
                ReportError("STK3014", "Fixed array lengths must currently be constant integer expressions.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            if (length < 0 || length > int.MaxValue)
            {
                ReportError("STK3014", $"Fixed array length '{length}' is out of range.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            result = StarkTypeSymbols.FixedArray(result, (int)length.Value);
        }

        return ApplyQualifiers(result, type.typeQualifier());
    }

    public HashSet<string>? GetGenericParameterNames(StarkParser.TypeParameterListContext? typeParameterList)
    {
        if (typeParameterList is null)
        {
            return null;
        }

        return typeParameterList.typeParameter()
            .Select(static parameter => parameter.GetText())
            .ToHashSet(StringComparer.Ordinal);
    }

    public StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        if (genericParameters?.Contains(qualifiedName) == true)
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (_namedTypes.ContainsKey(qualifiedName))
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (!string.IsNullOrWhiteSpace(currentModuleName)
            && !qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            var moduleQualifiedName = $"{currentModuleName}.{qualifiedName}";
            if (_namedTypes.ContainsKey(moduleQualifiedName))
            {
                return StarkTypeSymbols.Named(moduleQualifiedName);
            }
        }

        if (TryResolveTypeAlias(qualifiedName, currentModuleName, token, typeArguments: null, out var aliasType))
        {
            return aliasType;
        }

        if (!qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            ReportError("STK3004", $"Unknown type '{qualifiedName}'.", token);
            return StarkTypeSymbols.Error;
        }

        ReportError("STK3004", $"Unknown type '{qualifiedName}'.", token);
        return StarkTypeSymbols.Error;
    }

    internal bool TryResolveDeclaredTypeAlias(string lookupName, string? currentModuleName, out TypeAliasSymbol alias)
    {
        return TryResolveTypeAliasSymbol(lookupName, currentModuleName, out alias);
    }

    private StarkTypeSymbol ResolveNonArrayType(StarkParser.NonArrayTypeContext type, ISet<string>? genericParameters, string? currentModuleName)
    {
        if (type.rawPointerType() is { } rawPointerType)
        {
            var elementType = ResolveType(rawPointerType.type_(), genericParameters, currentModuleName);
            return StarkTypeSymbols.RawPointer(elementType, rawPointerType.RAWMUTPTR() is not null);
        }

        if (type.integerType() is { } integerType)
        {
            return ResolveIntegerType(integerType);
        }

        return ResolveSimpleType(type.simpleType(), genericParameters, currentModuleName);
    }

    private StarkTypeSymbol ResolveConversionNonArrayType(StarkParser.ConversionNonArrayTypeContext type, ISet<string>? genericParameters, string? currentModuleName)
    {
        if (type.rawPointerType() is { } rawPointerType)
        {
            var elementType = ResolveType(rawPointerType.type_(), genericParameters, currentModuleName);
            return StarkTypeSymbols.RawPointer(elementType, rawPointerType.RAWMUTPTR() is not null);
        }

        if (type.integerType() is { } integerType)
        {
            return ResolveIntegerType(integerType);
        }

        return ResolveBuiltinType(type.builtinType());
    }

    public StarkTypeSymbol ResolveSimpleType(StarkParser.SimpleTypeContext simpleType, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        if (simpleType.builtinType() is { } builtinType)
        {
            return ResolveBuiltinType(builtinType);
        }

        var qualifiedName = simpleType.qualifiedName().GetText();

        if (simpleType.typeArgumentList() is { } typeArgList)
        {
            var typeArgs = typeArgList.type_()
                .Select(typeArg => ResolveType(typeArg, genericParameters, currentModuleName))
                .ToArray();

            if (typeArgs.Any(static t => t.Kind == StarkTypeKind.Error))
            {
                return StarkTypeSymbols.Error;
            }

            if (TryResolveTypeAlias(qualifiedName, currentModuleName, simpleType.Start, typeArgs, out var aliasType))
            {
                return aliasType;
            }

            var baseType = ResolveQualifiedType(qualifiedName, genericParameters: null, simpleType.Start, currentModuleName);
            if (baseType.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
            }

            return StarkTypeSymbols.GenericInstantiation(baseType.NamedType ?? qualifiedName, typeArgs);
        }

        return ResolveQualifiedType(qualifiedName, genericParameters, simpleType.Start, currentModuleName);
    }

    private static StarkTypeSymbol ResolveIntegerType(StarkParser.IntegerTypeContext integerType)
    {
        var width = int.Parse(integerType.INTEGER_TYPE().GetText()[1..], CultureInfo.InvariantCulture);
        var rangeConstraint = integerType.rangeConstraint();
        var min = ParseSignedIntegerLiteral(rangeConstraint.signedIntegerLiteral(0));
        var max = ParseSignedIntegerLiteral(rangeConstraint.signedIntegerLiteral(1));
        return StarkTypeSymbols.Integer(width, min, max);
    }

    private bool TryResolveTypeAlias(
        string qualifiedName,
        string? currentModuleName,
        IToken token,
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        out StarkTypeSymbol aliasType)
    {
        foreach (var candidate in EnumerateAliasLookupNames(qualifiedName, currentModuleName))
        {
            if (!TryResolveTypeAliasSymbol(candidate, currentModuleName, out var alias))
            {
                continue;
            }

            aliasType = InstantiateTypeAlias(alias, qualifiedName, typeArguments, token);
            return true;
        }

        aliasType = StarkTypeSymbols.Error;
        return false;
    }

    private bool TryResolveTypeAliasSymbol(string lookupName, string? currentModuleName, out TypeAliasSymbol alias)
    {
        if (_typeAliases.TryGetValue(lookupName, out alias!)
            && IsTypeAliasAccessible(alias, currentModuleName))
        {
            return true;
        }

        if (!_typeAliasSources.TryGetValue(lookupName, out var source)
            || !IsTypeAliasAccessible(source, currentModuleName))
        {
            alias = null!;
            return false;
        }

        alias = ResolveTypeAliasSymbol(source);
        return IsTypeAliasAccessible(alias, currentModuleName);
    }

    private TypeAliasSymbol ResolveTypeAliasSymbol(TypeAliasResolutionSource source)
    {
        if (_typeAliases.TryGetValue(source.LookupName, out var existing))
        {
            return existing;
        }

        if (!_resolvingTypeAliases.Add(source.LookupName))
        {
            ReportError(
                "STK3023",
                $"Type alias '{source.LookupName}' participates in a cycle and cannot be resolved.",
                source.NameToken);
            return CacheResolvedTypeAlias(source, StarkTypeSymbols.Error);
        }

        try
        {
            var genericParameters = source.GenericParameters.Count == 0
                ? null
                : source.GenericParameters.ToHashSet(StringComparer.Ordinal);
            var targetType = ResolveType(source.TargetType, genericParameters, source.ModuleName);
            return CacheResolvedTypeAlias(source, targetType);
        }
        finally
        {
            _resolvingTypeAliases.Remove(source.LookupName);
        }
    }

    private TypeAliasSymbol CacheResolvedTypeAlias(TypeAliasResolutionSource source, StarkTypeSymbol targetType)
    {
        var alias = new TypeAliasSymbol(
            source.LookupName,
            source.ModuleName,
            source.Visibility,
            targetType,
            source.GenericParameters.Count == 0 ? null : source.GenericParameters.ToArray(),
            source.IsExternal);
        _mutableTypeAliases?[source.LookupName] = alias;
        return alias;
    }

    private StarkTypeSymbol InstantiateTypeAlias(
        TypeAliasSymbol alias,
        string diagnosticName,
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        IToken token)
    {
        if (!alias.IsGeneric)
        {
            if (typeArguments is not null)
            {
                ReportError("STK3019", $"Type alias '{diagnosticName}' is not generic and does not accept type arguments.", token);
                return StarkTypeSymbols.Error;
            }

            return alias.TargetType;
        }

        if (typeArguments is null)
        {
            ReportError(
                "STK3019",
                $"Generic type alias '{diagnosticName}' expects {alias.GenericParams.Count} type argument(s) but 0 were provided.",
                token);
            return StarkTypeSymbols.Error;
        }

        if (alias.GenericParams.Count != typeArguments.Count)
        {
            ReportError(
                "STK3019",
                $"Generic type alias '{diagnosticName}' expects {alias.GenericParams.Count} type argument(s) but {typeArguments.Count} were provided.",
                token);
            return StarkTypeSymbols.Error;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < alias.GenericParams.Count; index++)
        {
            substitution[alias.GenericParams[index]] = typeArguments[index];
        }

        return SubstituteType(alias.TargetType, substitution);
    }

    private static StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
    {
        var coreType = StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        StarkTypeSymbol substitutedCore;

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (substitution.TryGetValue(name, out var substituted))
            {
                substitutedCore = StarkTypeSymbols.WithQualifiers(
                    substituted,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
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

    private static IEnumerable<string> EnumerateAliasLookupNames(string qualifiedName, string? currentModuleName)
    {
        yield return qualifiedName;

        if (!string.IsNullOrWhiteSpace(currentModuleName)
            && !qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            yield return $"{currentModuleName}.{qualifiedName}";
        }
    }

    private static bool IsTypeAliasAccessible(TypeAliasSymbol alias, string? currentModuleName)
    {
        if (string.Equals(alias.ModuleName, currentModuleName, StringComparison.Ordinal))
        {
            return true;
        }

        return alias.Visibility switch
        {
            StarkVisibility.Module => false,
            StarkVisibility.Internal => !alias.IsExternal,
            StarkVisibility.Public => true,
            StarkVisibility.Export => true,
            _ => false
        };
    }

    private static bool IsTypeAliasAccessible(TypeAliasResolutionSource alias, string? currentModuleName)
    {
        if (string.Equals(alias.ModuleName, currentModuleName, StringComparison.Ordinal))
        {
            return true;
        }

        return alias.Visibility switch
        {
            StarkVisibility.Module => false,
            StarkVisibility.Internal => !alias.IsExternal,
            StarkVisibility.Public => true,
            StarkVisibility.Export => true,
            _ => false
        };
    }

    private static StarkTypeSymbol ResolveBuiltinType(StarkParser.BuiltinTypeContext builtinType)
    {
        var text = builtinType.GetText();
        return text switch
        {
            "void" => StarkTypeSymbols.Void,
            "bool" => StarkTypeSymbols.Bool,
            "ascii" => StarkTypeSymbols.Ascii,
            "unicode" => StarkTypeSymbols.Unicode,
            "Ascii" => StarkTypeSymbols.OwnedAscii,
            "Unicode" => StarkTypeSymbols.OwnedUnicode,
            _ when text.StartsWith("f", StringComparison.Ordinal) => StarkTypeSymbols.Float(int.Parse(text[1..], CultureInfo.InvariantCulture)),
            _ => StarkTypeSymbols.Error
        };
    }

    private StarkTypeSymbol ApplyQualifiers(StarkTypeSymbol type, IReadOnlyList<StarkParser.TypeQualifierContext> qualifiers)
    {
        if (qualifiers.Count == 0 || type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        StarkBorrowKind borrowKind = StarkBorrowKind.None;
        StarkAccessKind accessKind = StarkAccessKind.None;
        StarkInitializationKind initializationKind = StarkInitializationKind.None;
        var isMutableView = false;

        foreach (var qualifier in qualifiers)
        {
            var text = qualifier.GetText();
            switch (text)
            {
                case "borrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.Borrow, qualifier);
                    break;
                case "retborrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.RetBorrow, qualifier);
                    break;
                case "storeborrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.StoreBorrow, qualifier);
                    break;
                case "shared":
                    accessKind = ApplyAccessQualifier(accessKind, StarkAccessKind.Shared, qualifier);
                    break;
                case "frozen":
                    accessKind = ApplyAccessQualifier(accessKind, StarkAccessKind.Frozen, qualifier);
                    break;
                case "out":
                    initializationKind = ApplyInitializationQualifier(initializationKind, StarkInitializationKind.Out, qualifier);
                    break;
                case "init":
                    initializationKind = ApplyInitializationQualifier(initializationKind, StarkInitializationKind.Init, qualifier);
                    break;
                case "mut":
                    isMutableView = true;
                    break;
            }
        }

        if (type.Kind == StarkTypeKind.RawPointer
            && (borrowKind != StarkBorrowKind.None || accessKind != StarkAccessKind.None || initializationKind != StarkInitializationKind.None))
        {
            ReportError("STK3018", "Raw pointers cannot be wrapped in safe borrow, access, or initialization qualifiers.", qualifiers[0]);
        }

        return StarkTypeSymbols.ApplyQualifiers(type, borrowKind, accessKind, initializationKind, isMutableView);
    }

    private StarkBorrowKind ApplyBorrowQualifier(
        StarkBorrowKind current,
        StarkBorrowKind next,
        ParserRuleContext context)
    {
        if (current != StarkBorrowKind.None && current != next)
        {
            ReportError("STK3015", "A type may not combine multiple borrow escape qualifiers.", context);
            return current;
        }

        return next;
    }

    private StarkAccessKind ApplyAccessQualifier(
        StarkAccessKind current,
        StarkAccessKind next,
        ParserRuleContext context)
    {
        if (current != StarkAccessKind.None && current != next)
        {
            ReportError("STK3016", "A type may not combine both 'shared' and 'frozen'.", context);
            return current;
        }

        return next;
    }

    private StarkInitializationKind ApplyInitializationQualifier(
        StarkInitializationKind current,
        StarkInitializationKind next,
        ParserRuleContext context)
    {
        if (current != StarkInitializationKind.None && current != next)
        {
            ReportError("STK3017", "A type may not combine both 'out' and 'init'.", context);
            return current;
        }

        return next;
    }

    private void ReportError(string code, string message, ParserRuleContext context)
    {
        _context.Diagnostics.Error(code, message, _stage, Location(context.Start));
    }

    private void ReportError(string code, string message, IToken token)
    {
        _context.Diagnostics.Error(code, message, _stage, Location(token));
    }

    private SourceLocation Location(IToken token)
    {
        var tokenText = token.Text;
        if (string.IsNullOrEmpty(tokenText))
        {
            return new SourceLocation(_context.Input.FilePath, token.Line, token.Column + 1);
        }

        var normalizedText = tokenText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        if (lines.Length == 1)
        {
            return new SourceLocation(
                _context.Input.FilePath,
                token.Line,
                token.Column + 1,
                token.Line,
                token.Column + Math.Max(lines[0].Length, 1));
        }

        return new SourceLocation(
            _context.Input.FilePath,
            token.Line,
            token.Column + 1,
            token.Line + lines.Length - 1,
            Math.Max(lines[^1].Length, 1));
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private BigInteger? TryEvaluateConstantInteger(StarkParser.ExpressionContext expression)
    {
        return CompileTimeExpressionEvaluator.TryEvaluateInteger(expression, out var value)
            ? value
            : null;
    }
}
