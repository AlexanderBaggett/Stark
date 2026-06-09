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
    IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParameters,
    StarkParser.Type_Context TargetType,
    IToken NameToken);

internal sealed class StarkTypeResolver
{
    private readonly CompilerPassContext _context;
    private readonly string _stage;
    private readonly ModuleGraph _moduleGraph;
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
    private readonly IReadOnlyDictionary<string, TypeAliasSymbol> _typeAliases;
    private readonly IReadOnlyDictionary<string, TypeAliasResolutionSource> _typeAliasSources;
    private readonly Dictionary<string, TypeAliasSymbol>? _mutableTypeAliases;
    private readonly HashSet<string> _resolvingTypeAliases = new(StringComparer.Ordinal);
    private const int MaximumCompileTimeIntegerEndpointBitWidth = 1024;
    private static readonly BigInteger MinimumCompileTimeIntegerEndpoint =
        -(BigInteger.One << (MaximumCompileTimeIntegerEndpointBitWidth - 1));
    private static readonly BigInteger MaximumCompileTimeIntegerEndpoint =
        (BigInteger.One << MaximumCompileTimeIntegerEndpointBitWidth) - BigInteger.One;

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
        _moduleGraph = moduleGraph;
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
        _moduleGraph = moduleGraph;
        _namedTypes = namedTypes;
        _typeAliases = typeAliases ?? EmptyTypeAliases;
        _typeAliasSources = typeAliasSources ?? EmptyTypeAliasSources;
        _mutableTypeAliases = typeAliases as Dictionary<string, TypeAliasSymbol>;
    }

    private static IReadOnlyDictionary<string, TypeAliasSymbol> EmptyTypeAliases { get; } =
        new Dictionary<string, TypeAliasSymbol>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, TypeAliasResolutionSource> EmptyTypeAliasSources { get; } =
        new Dictionary<string, TypeAliasResolutionSource>(StringComparer.Ordinal);

    public StarkTypeSymbol ResolveReturnType(
        StarkParser.ReturnTypeContext returnType,
        ISet<string>? genericParameters = null,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        return returnType.VOID() is not null
            ? StarkTypeSymbols.Void
            : ResolveType(returnType.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
    }

    public StarkTypeSymbol ResolveType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters = null,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        var result = ResolveNonArrayType(type.nonArrayType(), genericParameters, currentModuleName, comptimeGenericParameters);

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
                if (TryResolveComptimeArrayLengthParameter(suffix.expression(), comptimeGenericParameters, out var parameterName))
                {
                    result = StarkTypeSymbols.FixedArray(result, fixedLength: null, parameterName);
                    continue;
                }

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

    public StarkTypeSymbol ResolveParameterType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out string? rawPointerElementCountExpression,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        rawPointerElementCountExpression = null;

        if (TryResolveBoundedRawPointerParameterType(
                type,
                genericParameters,
                currentModuleName,
                out var rawPointerType,
                out rawPointerElementCountExpression,
                comptimeGenericParameters))
        {
            return rawPointerType;
        }

        return ResolveType(type, genericParameters, currentModuleName, comptimeGenericParameters);
    }

    public StarkTypeSymbol ResolveConversionType(
        StarkParser.ConversionTypeContext type,
        ISet<string>? genericParameters = null,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        var result = ResolveConversionNonArrayType(type.conversionNonArrayType(), genericParameters, currentModuleName, comptimeGenericParameters);

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
                if (TryResolveComptimeArrayLengthParameter(suffix.expression(), comptimeGenericParameters, out var parameterName))
                {
                    result = StarkTypeSymbols.FixedArray(result, fixedLength: null, parameterName);
                    continue;
                }

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
            .Where(static parameter => parameter.COMPTIME() is null)
            .Select(static parameter => parameter.Identifier().GetText())
            .ToHashSet(StringComparer.Ordinal);
    }

    public StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        if (genericParameters?.Contains(qualifiedName) == true)
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (!qualifiedName.Contains('.', StringComparison.Ordinal)
            && string.Equals(currentModuleName, _moduleGraph.RootModuleName, StringComparison.Ordinal)
            && _namedTypes.ContainsKey(qualifiedName))
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

            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, qualifiedName)
                .Where(_namedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                return StarkTypeSymbols.Named(importedMatches[0]);
            }

            if (importedMatches.Length > 1)
            {
                ReportError(
                    "STK3004",
                    $"Imported type name '{qualifiedName}' is ambiguous between {string.Join(", ", importedMatches)}. Use a fully qualified name.",
                    token);
                return StarkTypeSymbols.Error;
            }
        }

        if (_namedTypes.ContainsKey(qualifiedName))
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (TryResolveTypeAlias(
                qualifiedName,
                currentModuleName,
                token,
                typeArguments: null,
                comptimeValueArguments: null,
                out var aliasType))
        {
            return aliasType;
        }

        if (TryResolveDynTraitVtableType(
                qualifiedName,
                typeArgumentList: null,
                genericParameters,
                currentModuleName,
                comptimeGenericParameters: null,
                token,
                out var dynTraitVtableType))
        {
            return dynTraitVtableType;
        }

        if (TryResolveAssociatedTypeName(qualifiedName, genericParameters, currentModuleName, out var associatedType))
        {
            return associatedType;
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

    private StarkTypeSymbol ResolveNonArrayType(
        StarkParser.NonArrayTypeContext type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        if (type.dynamicType() is { } dynamicType)
        {
            var elementType = ResolveType(dynamicType.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
            return StarkTypeSymbols.Dynamic(elementType);
        }

        if (type.rawPointerType() is { } rawPointerType)
        {
            var elementType = ResolveType(rawPointerType.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
            return StarkTypeSymbols.RawPointer(elementType, rawPointerType.RAWMUTPTR() is not null);
        }

        if (type.functionPointerType() is { } functionPointerType)
        {
            return ResolveFunctionPointerType(functionPointerType, genericParameters, currentModuleName, comptimeGenericParameters);
        }

        if (type.closureType() is { } closureType)
        {
            return ResolveClosureType(closureType, genericParameters, currentModuleName, comptimeGenericParameters);
        }

        if (type.dynTraitType() is { } dynTraitType)
        {
            return ResolveDynTraitType(dynTraitType, genericParameters, currentModuleName, comptimeGenericParameters);
        }

        if (type.integerType() is { } integerType)
        {
            return ResolveIntegerType(integerType);
        }

        return ResolveSimpleType(type.simpleType(), genericParameters, currentModuleName, comptimeGenericParameters);
    }

    // Resolves a `dyn Trait` trait-object type (optionally `heap dyn Trait`).
    // The borrow/mut-borrow distinction is supplied by the outer type qualifier
    // and applied later by ApplyQualifiers, exactly like `borrow closure<...>`.
    // Object safety of the trait's individual methods is validated separately at
    // the trait declaration; here we require that the trait opted into dynamic
    // dispatch with `dyn trait`.
    private StarkTypeSymbol ResolveDynTraitType(
        StarkParser.DynTraitTypeContext type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        var traitType = ResolveSimpleType(type.simpleType(), genericParameters, currentModuleName, comptimeGenericParameters);
        if (traitType.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        var traitName = traitType.NamedType ?? type.simpleType().GetText();
        var simpleName = traitName.LastIndexOf('.') is var dot && dot >= 0 ? traitName[(dot + 1)..] : traitName;

        if (_namedTypes.TryGetValue(traitName, out var traitSymbol))
        {
            if (traitSymbol.Kind != DeclarationKind.Trait)
            {
                ReportError(
                    "STK3035",
                    $"'dyn' requires a trait, but '{simpleName}' is not a trait. A trait object can only be formed over a 'dyn trait'.",
                    type);
                return StarkTypeSymbols.Error;
            }

            if (!traitSymbol.IsDynTrait)
            {
                ReportError(
                    "STK3035",
                    $"Trait '{simpleName}' is static-only and cannot form a trait object. Declare it as 'dyn trait {simpleName}' to opt into dynamic dispatch, or use an enum for a closed set of cases.",
                    type);
                return StarkTypeSymbols.Error;
            }
        }

        var storageKind = type.dynStoragePrefix() is not null
            ? StarkDynTraitStorageKind.Heap
            : StarkDynTraitStorageKind.View;
        return StarkTypeSymbols.DynTrait(traitName, storageKind, traitType.TypeArguments);
    }

    private bool TryResolveBoundedRawPointerParameterType(
        StarkParser.Type_Context type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out StarkTypeSymbol rawPointerType,
        out string? elementCountExpression,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        rawPointerType = StarkTypeSymbols.Error;
        elementCountExpression = null;

        if (type.nonArrayType().rawPointerType() is not { } rawPointerSyntax
            || type.arraySuffix() is not [var suffix]
            || suffix.expression() is not { } countExpression)
        {
            return false;
        }

        var elementType = ResolveType(rawPointerSyntax.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
        rawPointerType = ApplyQualifiers(
            StarkTypeSymbols.RawPointer(elementType, rawPointerSyntax.RAWMUTPTR() is not null),
            type.typeQualifier());
        elementCountExpression = TryEvaluateConstantInteger(countExpression) is { } constantElementCount
            ? constantElementCount.ToString(CultureInfo.InvariantCulture)
            : countExpression.GetText();
        return true;
    }

    private StarkTypeSymbol ResolveConversionNonArrayType(
        StarkParser.ConversionNonArrayTypeContext type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        if (type.rawPointerType() is { } rawPointerType)
        {
            var elementType = ResolveType(rawPointerType.type_(), genericParameters, currentModuleName, comptimeGenericParameters);
            return StarkTypeSymbols.RawPointer(elementType, rawPointerType.RAWMUTPTR() is not null);
        }

        if (type.integerType() is { } integerType)
        {
            return ResolveIntegerType(integerType);
        }

        return ResolveBuiltinType(type.builtinType());
    }

    private StarkTypeSymbol ResolveFunctionPointerType(
        StarkParser.FunctionPointerTypeContext type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        var signature = type.functionPointerSignature();
        var isUnsafe = signature.UNSAFE() is not null;
        StarkFfiAbi? ffiAbi = null;
        if (signature.functionPointerAbiModifier() is { } abiModifier)
        {
            if (abiModifier.ffiAbiSpecifier() is null)
            {
                ffiAbi = StarkFfiAbi.C;
            }
            else if (FfiAbiSyntaxFacts.TryResolveFfiAbi(
                    abiModifier.ffiAbiSpecifier().ffiAbi(),
                    _context.Options.TargetInfo,
                    out var resolvedAbi,
                    out var errorMessage,
                    out var errorContext))
            {
                ffiAbi = resolvedAbi;
            }
            else
            {
                ReportError("STK3046", errorMessage, errorContext);
            }
        }

        var returnType = ResolveReturnType(signature.returnType(), genericParameters, currentModuleName, comptimeGenericParameters);
        var parameterTypeSyntaxes = signature.functionPointerParameterList().type_();
        var parameterTypes = new List<StarkTypeSymbol>(parameterTypeSyntaxes.Length);
        var rawPointerElementCountExpressions = new List<string?>(parameterTypeSyntaxes.Length);
        foreach (var parameterTypeSyntax in parameterTypeSyntaxes)
        {
            parameterTypes.Add(ResolveParameterType(
                parameterTypeSyntax,
                genericParameters,
                currentModuleName,
                out var rawPointerElementCountExpression,
                comptimeGenericParameters));
            rawPointerElementCountExpressions.Add(rawPointerElementCountExpression);
        }

        var parameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameterType,
                RawPointerElementCountExpression: rawPointerElementCountExpressions[index]))
            .ToArray();
        ValidateFunctionPointerBoundedRawPointerParameterCounts("function-pointer", parameterTypeSyntaxes, parameters);
        ValidateUnsupportedFunctionPointerDisjointClauses(signature);
        ValidateFunctionPointerRelationConflicts(signature);
        var overlapGroups = CreateFunctionPointerOverlapGroups(signature, parameters);
        var sameGroups = CreateFunctionPointerSameGroups(signature, parameters);
        var disjointGroups = ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
            parameters,
            explicitDisjointGroups: [],
            overlapGroups,
            sameGroups,
            applyDefaultNonOverlap: true);
        return StarkTypeSymbols.FunctionPointer(
            ParseFunctionKind(signature.functionKind()),
            returnType,
            parameterTypes,
            disjointGroups,
            overlapGroups,
            sameGroups,
            rawPointerElementCountExpressions,
            ffiAbi,
            isUnsafe);
    }

    private StarkTypeSymbol ResolveClosureType(
        StarkParser.ClosureTypeContext type,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        var signature = type.closureSignature();
        var returnType = ResolveReturnType(signature.returnType(), genericParameters, currentModuleName, comptimeGenericParameters);
        var parameterTypeSyntaxes = signature.functionPointerParameterList().type_();
        var parameterTypes = new List<StarkTypeSymbol>(parameterTypeSyntaxes.Length);
        var rawPointerElementCountExpressions = new List<string?>(parameterTypeSyntaxes.Length);
        foreach (var parameterTypeSyntax in parameterTypeSyntaxes)
        {
            parameterTypes.Add(ResolveParameterType(
                parameterTypeSyntax,
                genericParameters,
                currentModuleName,
                out var rawPointerElementCountExpression,
                comptimeGenericParameters));
            rawPointerElementCountExpressions.Add(rawPointerElementCountExpression);
        }

        var parameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameterType,
                RawPointerElementCountExpression: rawPointerElementCountExpressions[index]))
            .ToArray();
        ValidateFunctionPointerBoundedRawPointerParameterCounts("closure", parameterTypeSyntaxes, parameters);
        ValidateUnsupportedClosureDisjointClauses(signature);
        ValidateClosureRelationConflicts(signature);
        var overlapGroups = CreateClosureOverlapGroups(signature, parameters);
        var sameGroups = CreateClosureSameGroups(signature, parameters);
        var disjointGroups = ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
            parameters,
            explicitDisjointGroups: [],
            overlapGroups,
            sameGroups,
            applyDefaultNonOverlap: true);
        var functionKind = ParseFunctionKind(signature.functionKind());
        var callCapability = ParseClosureCallCapability(signature.closureCallCapability());
        ValidateClosureCallCapability(callCapability, functionKind, signature);
        return StarkTypeSymbols.Closure(
            ParseClosureStorageKind(type.closureStoragePrefix()),
            callCapability,
            functionKind,
            returnType,
            parameterTypes,
            disjointGroups,
            overlapGroups,
            sameGroups,
            rawPointerElementCountExpressions);
    }

    private void ValidateFunctionPointerBoundedRawPointerParameterCounts(
        string callableKind,
        IReadOnlyList<StarkParser.Type_Context> parameterTypeSyntaxes,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        var parameterSymbols = parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        for (var index = 0; index < parameterTypeSyntaxes.Count && index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (parameter.RawPointerElementCountExpression is null
                || !TryGetBoundedRawPointerElementCountExpression(parameterTypeSyntaxes[index], out var countExpression))
            {
                continue;
            }

            ValidateFunctionPointerBoundedRawPointerCountExpression(callableKind, parameter.Name, countExpression, parameterSymbols);
        }
    }

    private bool ValidateFunctionPointerBoundedRawPointerCountExpression(
        string callableKind,
        string parameterName,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterSymbols)
    {
        if (TryGetFunctionPointerContractParameterName(expression, out var boundName))
        {
            if (!parameterSymbols.TryGetValue(boundName, out var boundParameter))
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer {callableKind} parameter '{parameterName}' references unknown count parameter '{boundName}'.",
                    expression);
                return false;
            }

            if (boundParameter.Type.Kind != StarkTypeKind.Integer)
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer {callableKind} parameter '{parameterName}' count '{boundName}' must be an integer parameter, but found '{boundParameter.Type.DisplayName}'.",
                    expression);
                return false;
            }

            if (!IsProvablyNonNegativeIntegerType(boundParameter.Type))
            {
                ReportError(
                    "STK3014",
                    $"Bounded raw pointer {callableKind} parameter '{parameterName}' count '{boundName}' must be provably non-negative.",
                    expression);
                return false;
            }

            return true;
        }

        if (TryEvaluateConstantInteger(expression) is { } constant)
        {
            if (constant >= BigInteger.Zero)
            {
                return true;
            }

            ReportError(
                "STK3014",
                $"Bounded raw pointer {callableKind} parameter '{parameterName}' count '{expression.GetText()}' must be non-negative.",
                expression);
            return false;
        }

        ReportError(
            "STK3014",
            $"Bounded raw pointer {callableKind} parameter '{parameterName}' count must be a non-negative integer parameter of the form 'arg0', 'arg1', and so on, or a compile-time integer constant.",
            expression);
        return false;
    }

    private static bool TryGetBoundedRawPointerElementCountExpression(
        StarkParser.Type_Context type,
        out StarkParser.ExpressionContext countExpression)
    {
        countExpression = null!;
        if (type.nonArrayType().rawPointerType() is null
            || type.arraySuffix() is not [var suffix]
            || suffix.expression() is not { } expression)
        {
            return false;
        }

        countExpression = expression;
        return true;
    }

    private IReadOnlyList<ParameterOverlapGroup> CreateFunctionPointerOverlapGroups(
        StarkParser.FunctionPointerSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return CreateFunctionPointerRelationGroups(
                signature,
                parameters,
                relationName: "overlap",
                static contract => contract.overlapContract()?.expressionList())
            .Select(static group => new ParameterOverlapGroup(group))
            .ToArray();
    }

    private IReadOnlyList<ParameterSameGroup> CreateFunctionPointerSameGroups(
        StarkParser.FunctionPointerSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return CreateFunctionPointerRelationGroups(
                signature,
                parameters,
                relationName: "same",
                static contract => contract.sameContract()?.expressionList())
            .Select(static group => new ParameterSameGroup(group))
            .ToArray();
    }

    private IReadOnlyList<ParameterOverlapGroup> CreateClosureOverlapGroups(
        StarkParser.ClosureSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return CreateClosureRelationGroups(
                signature,
                parameters,
                relationName: "overlap",
                static contract => contract.overlapContract()?.expressionList())
            .Select(static group => new ParameterOverlapGroup(group))
            .ToArray();
    }

    private IReadOnlyList<ParameterSameGroup> CreateClosureSameGroups(
        StarkParser.ClosureSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        return CreateClosureRelationGroups(
                signature,
                parameters,
                relationName: "same",
                static contract => contract.sameContract()?.expressionList())
            .Select(static group => new ParameterSameGroup(group))
            .ToArray();
    }

    private IReadOnlyList<IReadOnlyList<string>> CreateClosureRelationGroups(
        StarkParser.ClosureSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters,
        string relationName,
        Func<StarkParser.ParameterMemoryContractContext, StarkParser.ExpressionListContext?> selectExpressionList)
    {
        var parameterByName = parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var expressionList = selectExpressionList(contract);
                if (expressionList is null)
                {
                    continue;
                }

                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var expression in expressionList.expression())
                {
                    if (!TryGetFunctionPointerContractParameterName(expression, out var name))
                    {
                        ReportError(
                            "STK3029",
                            $"Closure '{relationName}' contracts must use synthetic parameter names of the form 'arg0', 'arg1', and so on.",
                            expression);
                        continue;
                    }

                    if (!parameterByName.TryGetValue(name, out var parameter))
                    {
                        ReportError(
                            "STK3029",
                            $"Closure '{relationName}' contract references unknown parameter '{name}'.",
                            expression);
                    }
                    else if (!ParameterMemoryContractFacts.IsMemoryBacked(parameter.Type))
                    {
                        ReportError(
                            "STK3029",
                            $"Closure '{relationName}' contract references parameter '{name}' with non-memory-backed type '{parameter.Type.DisplayName}'. Memory contracts require memory-backed parameters such as slices, text views, borrows, initialization views, or raw pointers.",
                            expression);
                    }
                    else if (!seen.Add(name))
                    {
                        ReportError(
                            "STK3029",
                            $"Closure '{relationName}' contract repeats parameter '{name}'.",
                            expression);
                    }
                    else
                    {
                        names.Add(name);
                    }
                }

                if (names.Count < 2)
                {
                    ReportError(
                        "STK3029",
                        $"Closure 'where {relationName}(...)' contracts require at least two parameter operands.",
                        contract);
                    continue;
                }

                groups.Add(names.ToArray());
            }
        }

        return groups;
    }

    private IReadOnlyList<IReadOnlyList<string>> CreateFunctionPointerRelationGroups(
        StarkParser.FunctionPointerSignatureContext signature,
        IReadOnlyList<TypedParameterSymbol> parameters,
        string relationName,
        Func<StarkParser.ParameterMemoryContractContext, StarkParser.ExpressionListContext?> selectExpressionList)
    {
        var parameterByName = parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var expressionList = selectExpressionList(contract);
                if (expressionList is null)
                {
                    continue;
                }

                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var expression in expressionList.expression())
                {
                    if (!TryGetFunctionPointerContractParameterName(expression, out var name))
                    {
                        ReportError(
                            "STK3029",
                            $"Function pointer '{relationName}' contracts must use synthetic parameter names of the form 'arg0', 'arg1', and so on.",
                            expression);
                        continue;
                    }

                    if (!parameterByName.TryGetValue(name, out var parameter))
                    {
                        ReportError(
                            "STK3029",
                            $"Function pointer '{relationName}' contract references unknown parameter '{name}'.",
                            expression);
                    }
                    else if (!ParameterMemoryContractFacts.IsMemoryBacked(parameter.Type))
                    {
                        ReportError(
                            "STK3029",
                            $"Function pointer '{relationName}' contract references parameter '{name}' with non-memory-backed type '{parameter.Type.DisplayName}'. Memory contracts require memory-backed parameters such as slices, text views, borrows, initialization views, or raw pointers.",
                            expression);
                    }
                    else if (!seen.Add(name))
                    {
                        ReportError(
                            "STK3029",
                            $"Function pointer '{relationName}' contract repeats parameter '{name}'.",
                            expression);
                    }
                    else
                    {
                        names.Add(name);
                    }
                }

                if (names.Count < 2)
                {
                    ReportError(
                        "STK3029",
                        $"Function pointer 'where {relationName}(...)' contracts require at least two parameter operands.",
                        contract);
                    continue;
                }

                groups.Add(names.ToArray());
            }
        }

        return groups;
    }

    private void ValidateUnsupportedFunctionPointerDisjointClauses(StarkParser.FunctionPointerSignatureContext signature)
    {
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                if (contract.disjointContract() is null)
                {
                    continue;
                }

                ReportError(
                    "STK3029",
                    "Function pointer whole-parameter 'where disjoint(...)' is redundant because memory-backed function pointer parameters are non-overlapping by default. Use 'where overlap(...)' for intentional overlap or 'where same(...)' for identical storage.",
                    contract);
            }
        }
    }

    private void ValidateUnsupportedClosureDisjointClauses(StarkParser.ClosureSignatureContext signature)
    {
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                if (contract.disjointContract() is null)
                {
                    continue;
                }

                ReportError(
                    "STK3029",
                    "Closure whole-parameter 'where disjoint(...)' is redundant because memory-backed closure parameters are non-overlapping by default. Use 'where overlap(...)' for intentional overlap or 'where same(...)' for identical storage.",
                    contract);
            }
        }
    }

    private void ValidateFunctionPointerRelationConflicts(StarkParser.FunctionPointerSignatureContext signature)
    {
        var overlapPairs = CollectFunctionPointerRelationPairs(signature, static contract => contract.overlapContract()?.expressionList());
        var samePairs = CollectFunctionPointerRelationPairs(signature, static contract => contract.sameContract()?.expressionList());
        foreach (var pair in overlapPairs)
        {
            if (!samePairs.Contains(pair))
            {
                continue;
            }

            ReportError(
                "STK3029",
                $"Function pointer memory contract for parameters '{pair.Replace("|", "' and '", StringComparison.Ordinal)}' cannot be both overlap and same-memory. Use 'same' when identical storage is required.",
                signature);
        }
    }

    private void ValidateClosureRelationConflicts(StarkParser.ClosureSignatureContext signature)
    {
        var overlapPairs = CollectClosureRelationPairs(signature, static contract => contract.overlapContract()?.expressionList());
        var samePairs = CollectClosureRelationPairs(signature, static contract => contract.sameContract()?.expressionList());
        foreach (var pair in overlapPairs)
        {
            if (!samePairs.Contains(pair))
            {
                continue;
            }

            ReportError(
                "STK3029",
                $"Closure memory contract for parameters '{pair.Replace("|", "' and '", StringComparison.Ordinal)}' cannot be both overlap and same-memory. Use 'same' when identical storage is required.",
                signature);
        }
    }

    private static HashSet<string> CollectFunctionPointerRelationPairs(
        StarkParser.FunctionPointerSignatureContext signature,
        Func<StarkParser.ParameterMemoryContractContext, StarkParser.ExpressionListContext?> selectExpressionList)
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var names = selectExpressionList(contract)?.expression()
                    .Select(static expression => TryGetFunctionPointerContractParameterName(expression, out var name) ? name : null)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (names is null)
                {
                    continue;
                }

                for (var leftIndex = 0; leftIndex < names.Length; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < names.Length; rightIndex++)
                    {
                        pairs.Add(BuildNamePairKey(names[leftIndex], names[rightIndex]));
                    }
                }
            }
        }

        return pairs;
    }

    private static HashSet<string> CollectClosureRelationPairs(
        StarkParser.ClosureSignatureContext signature,
        Func<StarkParser.ParameterMemoryContractContext, StarkParser.ExpressionListContext?> selectExpressionList)
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clause in signature.parameterMemoryContractClause())
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var names = selectExpressionList(contract)?.expression()
                    .Select(static expression => TryGetFunctionPointerContractParameterName(expression, out var name) ? name : null)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (names is null)
                {
                    continue;
                }

                for (var leftIndex = 0; leftIndex < names.Length; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < names.Length; rightIndex++)
                    {
                        pairs.Add(BuildNamePairKey(names[leftIndex], names[rightIndex]));
                    }
                }
            }
        }

        return pairs;
    }

    private static bool TryGetFunctionPointerContractParameterName(
        StarkParser.ExpressionContext expression,
        out string name)
    {
        name = expression.GetText();
        if (name.Length <= 3
            || !name.StartsWith("arg", StringComparison.Ordinal)
            || !int.TryParse(name[3..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            name = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsProvablyNonNegativeIntegerType(StarkTypeSymbol type)
    {
        if (type.Kind != StarkTypeKind.Integer || type.BitWidth is not { } bitWidth)
        {
            return false;
        }

        if (type.RangeMin is { } rangeMin)
        {
            return rangeMin >= BigInteger.Zero;
        }

        return type.IsUnsigned
            && bitWidth > 0;
    }

    private static string BuildNamePairKey(string left, string right)
    {
        return string.CompareOrdinal(left, right) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }

    private static StarkFunctionKind ParseFunctionKind(StarkParser.FunctionKindContext functionKind)
    {
        return functionKind.GetText() switch
        {
            "finite" => StarkFunctionKind.Finite,
            "law" => StarkFunctionKind.Law,
            "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => StarkFunctionKind.Fn
        };
    }

    private static StarkClosureStorageKind ParseClosureStorageKind(StarkParser.ClosureStoragePrefixContext? storagePrefix)
    {
        return storagePrefix?.GetText() switch
        {
            "inline" => StarkClosureStorageKind.Inline,
            "heap" => StarkClosureStorageKind.Heap,
            _ => StarkClosureStorageKind.Unspecified
        };
    }

    private static StarkClosureCallCapability ParseClosureCallCapability(StarkParser.ClosureCallCapabilityContext? callCapability)
    {
        return callCapability?.GetText() switch
        {
            "mut" => StarkClosureCallCapability.Mut,
            "once" => StarkClosureCallCapability.Once,
            _ => StarkClosureCallCapability.None
        };
    }

    private void ValidateClosureCallCapability(
        StarkClosureCallCapability callCapability,
        StarkFunctionKind functionKind,
        StarkParser.ClosureSignatureContext signature)
    {
        if (callCapability is StarkClosureCallCapability.None
            || !FunctionKindFacts.IsLaw(functionKind))
        {
            return;
        }

        ReportError(
            "STK3014",
            "Law and finite law closure signatures cannot use 'mut' or 'once' call capability because law closures must not hide environment mutation or consumption.",
            signature.closureCallCapability() ?? (ParserRuleContext)signature);
    }

    public StarkTypeSymbol ResolveSimpleType(
        StarkParser.SimpleTypeContext simpleType,
        ISet<string>? genericParameters = null,
        string? currentModuleName = null,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters = null)
    {
        if (simpleType.builtinType() is { } builtinType)
        {
            return ResolveBuiltinType(builtinType);
        }

        var qualifiedName = simpleType.qualifiedName().GetText();

        if (simpleType.typeArgumentList() is { } typeArgList)
        {
            if (TryResolveTypeAlias(
                    qualifiedName,
                    currentModuleName,
                    simpleType.Start,
                    typeArgList,
                    genericParameters,
                    comptimeGenericParameters,
                    out var aliasType))
            {
                return aliasType;
            }

            if (TryResolveDynTraitVtableType(
                    qualifiedName,
                    typeArgList,
                    genericParameters,
                    currentModuleName,
                    comptimeGenericParameters,
                    simpleType.Start,
                    out var dynTraitVtableType))
            {
                return dynTraitVtableType;
            }

            if (TryResolveAssociatedTypeName(qualifiedName, genericParameters, currentModuleName, out _))
            {
                ReportError("STK3019", $"Associated type '{qualifiedName}' is not generic and does not accept type arguments.", simpleType.Start);
                return StarkTypeSymbols.Error;
            }

            var baseType = ResolveQualifiedType(qualifiedName, genericParameters: null, simpleType.Start, currentModuleName);
            if (baseType.Kind == StarkTypeKind.Error)
            {
                return StarkTypeSymbols.Error;
            }

            var resolvedBaseName = baseType.NamedType ?? qualifiedName;
            if (!_namedTypes.TryGetValue(resolvedBaseName, out var namedType))
            {
                ReportError("STK3004", $"Unknown generic type '{qualifiedName}'.", simpleType.Start);
                return StarkTypeSymbols.Error;
            }

            var genericArguments = GenericArgumentSyntaxFacts.Resolve(
                typeArgList,
                namedType.GenericParams,
                namedType.ComptimeGenericParams,
                typeArg => ResolveType(typeArg, genericParameters, currentModuleName, comptimeGenericParameters),
                ReportError,
                visibleComptimeParameters: comptimeGenericParameters);
            if (genericArguments.TypeArguments.Any(static t => t.Kind == StarkTypeKind.Error))
            {
                return StarkTypeSymbols.Error;
            }

            return StarkTypeSymbols.GenericInstantiation(
                resolvedBaseName,
                genericArguments.TypeArguments,
                genericArguments.ComptimeValueArguments);
        }

        return ResolveQualifiedType(qualifiedName, genericParameters, simpleType.Start, currentModuleName);
    }

    private bool TryResolveDynTraitVtableType(
        string qualifiedName,
        StarkParser.TypeArgumentListContext? typeArgumentList,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters,
        IToken token,
        out StarkTypeSymbol vtableType)
    {
        vtableType = StarkTypeSymbols.Error;
        var dot = qualifiedName.LastIndexOf('.');
        if (dot <= 0 || dot == qualifiedName.Length - 1)
        {
            return false;
        }

        var memberName = qualifiedName[(dot + 1)..];
        if (!string.Equals(memberName, StarkTypeSymbols.DynTraitVtableMemberName, StringComparison.Ordinal))
        {
            return false;
        }

        var ownerName = qualifiedName[..dot];
        if (!TryResolveAssociatedTypeOwner(ownerName, genericParameters, currentModuleName, out var ownerType)
            || ownerType.Kind != StarkTypeKind.Named
            || ownerType.NamedType is not { } ownerNamedType)
        {
            return false;
        }

        var ownerBaseName = StarkTypeSymbols.GetGenericBaseName(ownerNamedType);
        if (!_namedTypes.TryGetValue(ownerBaseName, out var ownerSymbol)
            || ownerSymbol.Kind != DeclarationKind.Trait)
        {
            return false;
        }

        if (!ownerSymbol.IsDynTrait)
        {
            ReportError(
                "STK3035",
                $"Trait '{ownerBaseName}' is static-only and has no vtable type. Declare it as 'dyn trait {LastSegment(ownerBaseName)}' before using '{qualifiedName}'.",
                token);
            return true;
        }

        if (typeArgumentList is null)
        {
            if (ownerSymbol.GenericParams.Count != 0 || ownerSymbol.ComptimeGenericParams.Count != 0)
            {
                ReportError(
                    "STK3019",
                    $"Vtable type '{qualifiedName}' requires {ownerSymbol.GenericParams.Count} type argument(s) and {ownerSymbol.ComptimeGenericParams.Count} comptime value argument(s).",
                    token);
                return true;
            }

            vtableType = StarkTypeSymbols.DynTraitVtable(ownerBaseName);
            return true;
        }

        var genericArguments = GenericArgumentSyntaxFacts.Resolve(
            typeArgumentList,
            ownerSymbol.GenericParams,
            ownerSymbol.ComptimeGenericParams,
            typeArg => ResolveType(typeArg, genericParameters, currentModuleName, comptimeGenericParameters),
            ReportError,
            visibleComptimeParameters: comptimeGenericParameters);
        if (genericArguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
        {
            return true;
        }

        vtableType = StarkTypeSymbols.DynTraitVtable(
            ownerBaseName,
            genericArguments.TypeArguments,
            genericArguments.ComptimeValueArguments);
        return true;
    }

    private bool TryResolveAssociatedTypeName(
        string qualifiedName,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out StarkTypeSymbol associatedType)
    {
        associatedType = StarkTypeSymbols.Error;
        var dot = qualifiedName.LastIndexOf('.');
        if (dot <= 0 || dot == qualifiedName.Length - 1)
        {
            return false;
        }

        var ownerName = qualifiedName[..dot];
        var associatedTypeName = qualifiedName[(dot + 1)..];
        if (!TryResolveAssociatedTypeOwner(ownerName, genericParameters, currentModuleName, out var ownerType))
        {
            return false;
        }

        if (AssociatedTypeFacts.TryResolveAssociatedType(ownerType, associatedTypeName, _namedTypes, out var targetType))
        {
            associatedType = targetType;
            return true;
        }

        if (ownerType.Kind == StarkTypeKind.Named
            && ownerType.NamedType is { } ownerNamedType
            && genericParameters?.Contains(ownerNamedType) == true)
        {
            associatedType = StarkTypeSymbols.AssociatedType(ownerType, associatedTypeName);
            return true;
        }

        if (ownerType.Kind == StarkTypeKind.Named
            && ownerType.NamedType is { } namedTypeName
            && _namedTypes.TryGetValue(StarkTypeSymbols.GetGenericBaseName(namedTypeName), out var namedType)
            && namedType.AssociatedTypes.TryGetValue(associatedTypeName, out var associatedMember)
            && associatedMember.IsRequired)
        {
            associatedType = StarkTypeSymbols.AssociatedType(ownerType, associatedTypeName);
            return true;
        }

        return false;
    }

    private bool TryResolveAssociatedTypeOwner(
        string ownerName,
        ISet<string>? genericParameters,
        string? currentModuleName,
        out StarkTypeSymbol ownerType)
    {
        if (genericParameters?.Contains(ownerName) == true)
        {
            ownerType = StarkTypeSymbols.Named(ownerName);
            return true;
        }

        if (!ownerName.Contains('.', StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(currentModuleName))
        {
            var moduleQualifiedName = $"{currentModuleName}.{ownerName}";
            if (_namedTypes.ContainsKey(moduleQualifiedName))
            {
                ownerType = StarkTypeSymbols.Named(moduleQualifiedName);
                return true;
            }

            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, ownerName)
                .Where(_namedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                ownerType = StarkTypeSymbols.Named(importedMatches[0]);
                return true;
            }
        }

        if (_namedTypes.ContainsKey(ownerName))
        {
            ownerType = StarkTypeSymbols.Named(ownerName);
            return true;
        }

        ownerType = StarkTypeSymbols.Error;
        return false;
    }

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private StarkTypeSymbol ResolveIntegerType(StarkParser.IntegerTypeContext integerType)
    {
        var integerTypeText = integerType.INTEGER_TYPE().GetText();
        var isUnsigned = integerTypeText[0] == 'u';
        var width = int.Parse(integerTypeText[1..], CultureInfo.InvariantCulture);
        IntegerRangeStorageFacts.GetIntegerTypeBounds(width, isUnsigned, out var typeMin, out var typeMax);

        var rangeConstraint = integerType.rangeConstraint();
        var endpointTokens = rangeConstraint.rangeEndpointToken()
            .Select(static endpointToken => endpointToken.Start)
            .ToArray();
        if (!TryFindIntegerRangeEndpointSplit(endpointTokens, out var upperEndpointStart))
        {
            ReportError(
                "STK3014",
                "Integer range constraints must contain exactly two compile-time integer endpoint expressions.",
                rangeConstraint);
            return StarkTypeSymbols.Integer(width, isUnsigned: isUnsigned);
        }

        var lower = ResolveIntegerRangeEndpoint(endpointTokens, 0, upperEndpointStart, typeMin, typeMax, rangeConstraint);
        var upper = ResolveIntegerRangeEndpoint(endpointTokens, upperEndpointStart, endpointTokens.Length, typeMin, typeMax, rangeConstraint);

        if (lower is null || upper is null)
        {
            return StarkTypeSymbols.Integer(width, isUnsigned: isUnsigned);
        }

        var styleIsValid = ValidateIntegerRangeEndpointStyle(
            endpointTokens,
            0,
            upperEndpointStart,
            lower.Value,
            typeMin,
            typeMax,
            integerTypeText,
            isUnsigned)
            & ValidateIntegerRangeEndpointStyle(
                endpointTokens,
                upperEndpointStart,
                endpointTokens.Length,
                upper.Value,
                typeMin,
                typeMax,
                integerTypeText,
                isUnsigned);
        if (!styleIsValid)
        {
            return StarkTypeSymbols.Integer(width, isUnsigned: isUnsigned);
        }

        if ((isUnsigned || _context.Options.EnforceIntegerRangeStorageRules)
            && (lower.Value < typeMin || lower.Value > typeMax || upper.Value < typeMin || upper.Value > typeMax))
        {
            var suggestion = IntegerRangeStorageFacts.TryGetSmallestTypeForRange(lower.Value, upper.Value, out var suggestedType)
                ? $" Use `{suggestedType.DisplayName}` if that is the intended range."
                : string.Empty;
            ReportError(
                "STK3014",
                $"Integer range endpoints for {integerTypeText} must be between {typeMin} and {typeMax}.{suggestion}",
                rangeConstraint);
            return StarkTypeSymbols.Integer(width, isUnsigned: isUnsigned);
        }

        if (lower.Value > upper.Value)
        {
            ReportError(
                "STK3014",
                $"Integer range lower bound '{lower.Value}' cannot exceed upper bound '{upper.Value}'.",
                rangeConstraint);
            return StarkTypeSymbols.Integer(width, isUnsigned: isUnsigned);
        }

        return StarkTypeSymbols.Integer(width, lower.Value, upper.Value, isUnsigned);
    }

    private bool ValidateIntegerRangeEndpointStyle(
        IReadOnlyList<IToken> tokens,
        int start,
        int end,
        BigInteger value,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        string integerTypeText,
        bool isUnsigned)
    {
        if (value == containingTypeMax && !IsSingleIdentifierEndpoint(tokens, start, end, "max"))
        {
            ReportError(
                "STK3014",
                $"Integer range endpoint '{FormatIntegerRangeEndpoint(tokens, start, end)}' spells the maximum value for {integerTypeText}; use the `max` shorthand instead.",
                start < end ? tokens[start] : tokens[Math.Max(0, end - 1)]);
            return false;
        }

        if (!isUnsigned
            && value == containingTypeMin
            && !IsSingleIdentifierEndpoint(tokens, start, end, "min"))
        {
            ReportError(
                "STK3014",
                $"Integer range endpoint '{FormatIntegerRangeEndpoint(tokens, start, end)}' spells the minimum value for {integerTypeText}; use the `min` shorthand instead.",
                start < end ? tokens[start] : tokens[Math.Max(0, end - 1)]);
            return false;
        }

        return true;
    }

    private static bool IsSingleIdentifierEndpoint(
        IReadOnlyList<IToken> tokens,
        int start,
        int end,
        string expectedName)
    {
        return end == start + 1
            && tokens[start].Type == StarkParser.Identifier
            && string.Equals(tokens[start].Text, expectedName, StringComparison.Ordinal);
    }

    private BigInteger? ResolveIntegerRangeEndpoint(
        IReadOnlyList<IToken> tokens,
        int start,
        int end,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        ParserRuleContext fallbackContext)
    {
        var diagnosticCount = _context.Diagnostics.Count;
        if (TryParseIntegerRangeEndpointExpression(
            tokens,
            start,
            end,
            evaluate: true,
            containingTypeMin,
            containingTypeMax,
            out var value))
        {
            return value;
        }

        if (_context.Diagnostics.Count == diagnosticCount)
        {
            ReportError(
                "STK3014",
                $"Integer range endpoint '{FormatIntegerRangeEndpoint(tokens, start, end)}' must be a compile-time integer expression.",
                start < end ? tokens[start] : fallbackContext.Start);
        }

        return null;
    }

    private bool TryFindIntegerRangeEndpointSplit(IReadOnlyList<IToken> tokens, out int upperEndpointStart)
    {
        upperEndpointStart = -1;

        for (var split = 1; split < tokens.Count; split++)
        {
            if (TryParseIntegerRangeEndpointExpression(
                    tokens,
                    0,
                    split,
                    evaluate: false,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    out _)
                && TryParseIntegerRangeEndpointExpression(
                    tokens,
                    split,
                    tokens.Count,
                    evaluate: false,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    out _))
            {
                upperEndpointStart = split;
            }
        }

        return upperEndpointStart > 0;
    }

    private bool TryParseIntegerRangeEndpointExpression(
        IReadOnlyList<IToken> tokens,
        int start,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        var position = start;
        return start < end
            && TryParseIntegerRangeEndpointAdditive(
                tokens,
                ref position,
                end,
                evaluate,
                containingTypeMin,
                containingTypeMax,
                out value)
            && position == end;
    }

    private bool TryParseIntegerRangeEndpointAdditive(
        IReadOnlyList<IToken> tokens,
        ref int position,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        if (!TryParseIntegerRangeEndpointMultiplicative(
                tokens,
                ref position,
                end,
                evaluate,
                containingTypeMin,
                containingTypeMax,
                out value))
        {
            return false;
        }

        while (position < end && tokens[position].Type is StarkParser.PLUS or StarkParser.MINUS)
        {
            var operatorToken = tokens[position++];
            if (!TryParseIntegerRangeEndpointMultiplicative(
                    tokens,
                    ref position,
                    end,
                    evaluate,
                    containingTypeMin,
                    containingTypeMax,
                    out var right))
            {
                return false;
            }

            if (!evaluate)
            {
                continue;
            }

            var result = operatorToken.Type == StarkParser.PLUS
                ? value + right
                : value - right;
            if (!TryValidateIntegerRangeEndpointValue(result, operatorToken))
            {
                return false;
            }

            value = result;
        }

        return true;
    }

    private bool TryParseIntegerRangeEndpointMultiplicative(
        IReadOnlyList<IToken> tokens,
        ref int position,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        if (!TryParseIntegerRangeEndpointPower(
                tokens,
                ref position,
                end,
                evaluate,
                containingTypeMin,
                containingTypeMax,
                out value))
        {
            return false;
        }

        while (position < end && tokens[position].Type is StarkParser.STAR or StarkParser.DIV or StarkParser.MOD)
        {
            var operatorToken = tokens[position++];
            if (!TryParseIntegerRangeEndpointPower(
                    tokens,
                    ref position,
                    end,
                    evaluate,
                    containingTypeMin,
                    containingTypeMax,
                    out var right))
            {
                return false;
            }

            if (!evaluate)
            {
                continue;
            }

            if ((operatorToken.Type == StarkParser.DIV || operatorToken.Type == StarkParser.MOD)
                && right.IsZero)
            {
                ReportError("STK3014", "Integer range endpoint evaluation cannot divide by zero.", operatorToken);
                return false;
            }

            var result = operatorToken.Type switch
            {
                StarkParser.STAR => value * right,
                StarkParser.DIV => value / right,
                StarkParser.MOD => value % right,
                _ => value
            };
            if (!TryValidateIntegerRangeEndpointValue(result, operatorToken))
            {
                return false;
            }

            value = result;
        }

        return true;
    }

    private bool TryParseIntegerRangeEndpointPower(
        IReadOnlyList<IToken> tokens,
        ref int position,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        if (!TryParseIntegerRangeEndpointUnary(
                tokens,
                ref position,
                end,
                evaluate,
                containingTypeMin,
                containingTypeMax,
                out value))
        {
            return false;
        }

        if (position >= end || tokens[position].Type != StarkParser.POW)
        {
            return true;
        }

        var operatorToken = tokens[position++];
        if (!TryParseIntegerRangeEndpointPower(
                tokens,
                ref position,
                end,
                evaluate,
                containingTypeMin,
                containingTypeMax,
                out var exponent))
        {
            return false;
        }

        return !evaluate || TryEvaluateIntegerRangeEndpointPower(value, exponent, operatorToken, out value);
    }

    private bool TryParseIntegerRangeEndpointUnary(
        IReadOnlyList<IToken> tokens,
        ref int position,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        if (position < end && tokens[position].Type == StarkParser.MINUS)
        {
            var operatorToken = tokens[position++];
            if (!TryParseIntegerRangeEndpointUnary(
                    tokens,
                    ref position,
                    end,
                    evaluate,
                    containingTypeMin,
                    containingTypeMax,
                    out value))
            {
                return false;
            }

            if (!evaluate)
            {
                return true;
            }

            value = -value;
            return TryValidateIntegerRangeEndpointValue(value, operatorToken);
        }

        return TryParseIntegerRangeEndpointPrimary(
            tokens,
            ref position,
            end,
            evaluate,
            containingTypeMin,
            containingTypeMax,
            out value);
    }

    private bool TryParseIntegerRangeEndpointPrimary(
        IReadOnlyList<IToken> tokens,
        ref int position,
        int end,
        bool evaluate,
        BigInteger containingTypeMin,
        BigInteger containingTypeMax,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        if (position >= end)
        {
            return false;
        }

        var token = tokens[position++];
        switch (token.Type)
        {
            case StarkParser.IntegerLiteral:
                if (!evaluate)
                {
                    return true;
                }

                value = BigInteger.Parse(token.Text, CultureInfo.InvariantCulture);
                return TryValidateIntegerRangeEndpointValue(value, token);

            case StarkParser.Identifier:
                if (!evaluate)
                {
                    return true;
                }

                return token.Text switch
                {
                    "min" => SetRangeEndpointValue(containingTypeMin, out value),
                    "max" => SetRangeEndpointValue(containingTypeMax, out value),
                    _ => ReportUnsupportedIntegerRangeEndpoint(token, token.Text)
                };

            case StarkParser.LPAREN:
                if (!TryParseIntegerRangeEndpointAdditive(
                        tokens,
                        ref position,
                        end,
                        evaluate,
                        containingTypeMin,
                        containingTypeMax,
                        out value)
                    || position >= end
                    || tokens[position].Type != StarkParser.RPAREN)
                {
                    return false;
                }

                position++;
                return true;

            default:
                return false;
        }
    }

    private bool TryEvaluateIntegerRangeEndpointPower(
        BigInteger baseValue,
        BigInteger exponent,
        IToken operatorToken,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        if (exponent.Sign < 0)
        {
            ReportError("STK3014", "Integer range endpoint exponentiation requires a non-negative exponent.", operatorToken);
            return false;
        }

        if (exponent.IsZero)
        {
            value = BigInteger.One;
            return TryValidateIntegerRangeEndpointValue(value, operatorToken);
        }

        if (baseValue.IsZero)
        {
            value = BigInteger.Zero;
            return true;
        }

        if (baseValue == BigInteger.One)
        {
            value = BigInteger.One;
            return true;
        }

        if (baseValue == -BigInteger.One)
        {
            value = exponent.IsEven ? BigInteger.One : -BigInteger.One;
            return true;
        }

        if (exponent > MaximumCompileTimeIntegerEndpointBitWidth)
        {
            ReportIntegerRangeEndpointOverflow(operatorToken);
            return false;
        }

        value = BigInteger.Pow(baseValue, (int)exponent);
        return TryValidateIntegerRangeEndpointValue(value, operatorToken);
    }

    private static bool SetRangeEndpointValue(BigInteger source, out BigInteger value)
    {
        value = source;
        return true;
    }

    private bool ReportUnsupportedIntegerRangeEndpoint(IToken token, string endpointText)
    {
        ReportError(
            "STK3014",
            $"Integer range endpoint '{endpointText}' is not supported here; use an integer literal, 'min', 'max', or compile-time integer arithmetic over those values.",
            token);
        return false;
    }

    private bool TryValidateIntegerRangeEndpointValue(BigInteger value, IToken token)
    {
        if (value >= MinimumCompileTimeIntegerEndpoint && value <= MaximumCompileTimeIntegerEndpoint)
        {
            return true;
        }

        ReportIntegerRangeEndpointOverflow(token);
        return false;
    }

    private void ReportIntegerRangeEndpointOverflow(IToken token)
    {
        ReportError(
            "STK3014",
            $"Integer range endpoint constant evaluation overflowed the supported compile-time endpoint range of {MaximumCompileTimeIntegerEndpointBitWidth} bits.",
            token);
    }

    private static string FormatIntegerRangeEndpoint(IReadOnlyList<IToken> tokens, int start, int end)
    {
        return string.Concat(tokens.Skip(start).Take(end - start).Select(static token => token.Text));
    }

    private bool TryResolveTypeAlias(
        string qualifiedName,
        string? currentModuleName,
        IToken token,
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? comptimeValueArguments,
        out StarkTypeSymbol aliasType)
    {
        foreach (var candidate in EnumerateLocalAliasLookupNames(qualifiedName, currentModuleName))
        {
            if (TryResolveCompilerKnownCTypeAlias(
                    candidate,
                    qualifiedName,
                    typeArguments,
                    comptimeValueArguments,
                    token,
                    out aliasType))
            {
                return true;
            }

            if (!TryResolveTypeAliasSymbol(candidate, currentModuleName, out var alias))
            {
                continue;
            }

            aliasType = InstantiateTypeAlias(alias, qualifiedName, typeArguments, comptimeValueArguments, token);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentModuleName)
            && !qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            var importedAliases = new List<TypeAliasSymbol>();
            var importedCompilerKnownAliases = new List<(string Name, StarkTypeSymbol Type)>();
            foreach (var candidate in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, qualifiedName))
            {
                if (TryResolveCompilerKnownCTypeAlias(
                        candidate,
                        qualifiedName,
                        typeArguments,
                        comptimeValueArguments,
                        token,
                        out var compilerKnownAliasType))
                {
                    importedCompilerKnownAliases.Add((candidate, compilerKnownAliasType));
                    continue;
                }

                if (TryResolveTypeAliasSymbol(candidate, currentModuleName, out var alias))
                {
                    importedAliases.Add(alias);
                }
            }

            if (importedCompilerKnownAliases.Count == 1 && importedAliases.Count == 0)
            {
                aliasType = importedCompilerKnownAliases[0].Type;
                return true;
            }

            if (importedCompilerKnownAliases.Count + importedAliases.Count > 1)
            {
                var aliasNames = importedCompilerKnownAliases
                    .Select(static alias => alias.Name)
                    .Concat(importedAliases.Select(static alias => alias.Name));
                ReportError(
                    "STK3004",
                    $"Imported type alias '{qualifiedName}' is ambiguous between {string.Join(", ", aliasNames)}. Use a fully qualified name.",
                    token);
                aliasType = StarkTypeSymbols.Error;
                return true;
            }

            if (importedAliases.Count == 1)
            {
                aliasType = InstantiateTypeAlias(importedAliases[0], qualifiedName, typeArguments, comptimeValueArguments, token);
                return true;
            }

            if (importedAliases.Count > 1)
            {
                ReportError(
                    "STK3004",
                    $"Imported type alias '{qualifiedName}' is ambiguous between {string.Join(", ", importedAliases.Select(static alias => alias.Name))}. Use a fully qualified name.",
                    token);
                aliasType = StarkTypeSymbols.Error;
                return true;
            }
        }

        aliasType = StarkTypeSymbols.Error;
        return false;
    }

    private bool TryResolveTypeAlias(
        string qualifiedName,
        string? currentModuleName,
        IToken token,
        StarkParser.TypeArgumentListContext typeArgumentList,
        ISet<string>? genericParameters,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters,
        out StarkTypeSymbol aliasType)
    {
        foreach (var candidate in EnumerateLocalAliasLookupNames(qualifiedName, currentModuleName))
        {
            if (StarkCDataModelFacts.TryResolveAlias(
                    candidate,
                    _context.Options.TargetInfo,
                    out _,
                    out _))
            {
                ReportError("STK3019", $"Type alias '{qualifiedName}' is not generic and does not accept generic arguments.", token);
                aliasType = StarkTypeSymbols.Error;
                return true;
            }

            if (!TryResolveTypeAliasSymbol(candidate, currentModuleName, out var alias))
            {
                continue;
            }

            var genericArguments = ResolveTypeAliasGenericArguments(
                alias,
                typeArgumentList,
                genericParameters,
                currentModuleName,
                comptimeGenericParameters);
            aliasType = InstantiateTypeAlias(
                alias,
                qualifiedName,
                genericArguments.TypeArguments,
                genericArguments.ComptimeValueArguments,
                token);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentModuleName)
            && !qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            var importedAliases = new List<TypeAliasSymbol>();
            var importedCompilerKnownAliases = new List<string>();
            foreach (var candidate in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(currentModuleName, qualifiedName))
            {
                if (StarkCDataModelFacts.TryResolveAlias(
                        candidate,
                        _context.Options.TargetInfo,
                        out _,
                        out _))
                {
                    importedCompilerKnownAliases.Add(candidate);
                    continue;
                }

                if (TryResolveTypeAliasSymbol(candidate, currentModuleName, out var alias))
                {
                    importedAliases.Add(alias);
                }
            }

            if (importedCompilerKnownAliases.Count + importedAliases.Count > 1)
            {
                var aliasNames = importedCompilerKnownAliases.Concat(importedAliases.Select(static alias => alias.Name));
                ReportError(
                    "STK3004",
                    $"Imported type alias '{qualifiedName}' is ambiguous between {string.Join(", ", aliasNames)}. Use a fully qualified name.",
                    token);
                aliasType = StarkTypeSymbols.Error;
                return true;
            }

            if (importedCompilerKnownAliases.Count == 1)
            {
                ReportError("STK3019", $"Type alias '{qualifiedName}' is not generic and does not accept generic arguments.", token);
                aliasType = StarkTypeSymbols.Error;
                return true;
            }

            if (importedAliases.Count == 1)
            {
                var alias = importedAliases[0];
                var genericArguments = ResolveTypeAliasGenericArguments(
                    alias,
                    typeArgumentList,
                    genericParameters,
                    currentModuleName,
                    comptimeGenericParameters);
                aliasType = InstantiateTypeAlias(
                    alias,
                    qualifiedName,
                    genericArguments.TypeArguments,
                    genericArguments.ComptimeValueArguments,
                    token);
                return true;
            }
        }

        aliasType = StarkTypeSymbols.Error;
        return false;
    }

    private ResolvedGenericArgumentList ResolveTypeAliasGenericArguments(
        TypeAliasSymbol alias,
        StarkParser.TypeArgumentListContext typeArgumentList,
        ISet<string>? genericParameters,
        string? currentModuleName,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters)
    {
        return GenericArgumentSyntaxFacts.Resolve(
            typeArgumentList,
            alias.GenericParams,
            alias.ComptimeGenericParams,
            typeArg => ResolveType(typeArg, genericParameters, currentModuleName, comptimeGenericParameters),
            ReportError,
            visibleComptimeParameters: comptimeGenericParameters);
    }

    private bool TryResolveCompilerKnownCTypeAlias(
        string lookupName,
        string diagnosticName,
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? comptimeValueArguments,
        IToken token,
        out StarkTypeSymbol aliasType)
    {
        if (!StarkCDataModelFacts.TryResolveAlias(
                lookupName,
                _context.Options.TargetInfo,
                out aliasType,
                out var diagnostic))
        {
            return false;
        }

        if (typeArguments is { Count: > 0 }
            || comptimeValueArguments is { Count: > 0 })
        {
            ReportError("STK3019", $"Type alias '{diagnosticName}' is not generic and does not accept generic arguments.", token);
            aliasType = StarkTypeSymbols.Error;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            ReportError("STK3050", diagnostic, token);
            aliasType = StarkTypeSymbols.Error;
        }

        return true;
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
            var comptimeGenericParameters = source.ComptimeGenericParameters.Count == 0
                ? null
                : source.ComptimeGenericParameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
            var targetType = ResolveType(source.TargetType, genericParameters, source.ModuleName, comptimeGenericParameters);
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
            source.ComptimeGenericParameters.Count == 0 ? null : source.ComptimeGenericParameters.ToArray(),
            IsExternal: source.IsExternal);
        _mutableTypeAliases?[source.LookupName] = alias;
        return alias;
    }

    private StarkTypeSymbol InstantiateTypeAlias(
        TypeAliasSymbol alias,
        string diagnosticName,
        IReadOnlyList<StarkTypeSymbol>? typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol>? comptimeValueArguments,
        IToken token)
    {
        var providedTypeArguments = typeArguments ?? [];
        var providedComptimeValueArguments = comptimeValueArguments ?? [];
        if (!alias.IsGeneric)
        {
            if (providedTypeArguments.Count > 0 || providedComptimeValueArguments.Count > 0)
            {
                ReportError("STK3019", $"Type alias '{diagnosticName}' is not generic and does not accept generic arguments.", token);
                return StarkTypeSymbols.Error;
            }

            return alias.TargetType;
        }

        if (alias.GenericParams.Count != providedTypeArguments.Count
            || alias.ComptimeGenericParams.Count != providedComptimeValueArguments.Count)
        {
            ReportError(
                "STK3019",
                $"Generic type alias '{diagnosticName}' expects {alias.GenericParams.Count} type argument(s) and {alias.ComptimeGenericParams.Count} comptime value argument(s), but received {providedTypeArguments.Count} type argument(s) and {providedComptimeValueArguments.Count} comptime value argument(s).",
                token);
            return StarkTypeSymbols.Error;
        }

        var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < alias.GenericParams.Count; index++)
        {
            substitution[alias.GenericParams[index]] = providedTypeArguments[index];
        }

        var comptimeValueSubstitution = new Dictionary<string, ComptimeValueArgumentSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < alias.ComptimeGenericParams.Count; index++)
        {
            comptimeValueSubstitution[alias.ComptimeGenericParams[index].Name] = providedComptimeValueArguments[index];
        }

        return SubstituteType(alias.TargetType, substitution, comptimeValueSubstitution);
    }

    private static StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, ComptimeValueArgumentSymbol>? comptimeValueSubstitution)
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
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType))
            {
                var substitutedArguments = (coreType.TypeArguments ?? [])
                    .Select(argument => SubstituteType(argument, substitution, comptimeValueSubstitution))
                    .ToArray();
                var substitutedValueArguments = SubstituteComptimeValueArguments(
                    coreType.ComptimeValueArguments,
                    comptimeValueSubstitution);
                substitutedCore = StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(name),
                    substitutedArguments,
                    substitutedValueArguments);
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
            substitutedCore = StarkTypeSymbols.AssociatedType(
                SubstituteType(coreType.AssociatedTypeOwner, substitution, comptimeValueSubstitution),
                coreType.AssociatedTypeName);
        }
        else if (coreType.ElementType is not null)
        {
            var substitutedElement = SubstituteType(coreType.ElementType, substitution, comptimeValueSubstitution);
            var fixedLength = coreType.FixedLength;
            var fixedLengthParameterName = coreType.FixedLengthParameterName;
            if (fixedLengthParameterName is not null
                && comptimeValueSubstitution is not null
                && comptimeValueSubstitution.TryGetValue(fixedLengthParameterName, out var substitutedLength))
            {
                if (substitutedLength.IsSymbolic)
                {
                    fixedLength = null;
                    fixedLengthParameterName = substitutedLength.SourceName;
                }
                else if (substitutedLength.IntegerValue >= 0 && substitutedLength.IntegerValue <= int.MaxValue)
                {
                    fixedLength = (int)substitutedLength.IntegerValue;
                    fixedLengthParameterName = null;
                }
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
                SubstituteType(returnType, substitution, comptimeValueSubstitution),
                parameterTypes.Select(parameter => SubstituteType(parameter, substitution, comptimeValueSubstitution)).ToArray(),
                coreType.FunctionPointerDisjointParameterGroups,
                coreType.FunctionPointerOverlapParameterGroups,
                coreType.FunctionPointerSameParameterGroups,
                coreType.FunctionPointerParameterRawPointerElementCountExpressions,
                coreType.FunctionPointerAbi,
                coreType.FunctionPointerIsUnsafe);
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
                SubstituteType(closureReturnType, substitution, comptimeValueSubstitution),
                closureParameterTypes.Select(parameter => SubstituteType(parameter, substitution, comptimeValueSubstitution)).ToArray(),
                coreType.ClosureDisjointParameterGroups,
                coreType.ClosureOverlapParameterGroups,
                coreType.ClosureSameParameterGroups,
                coreType.ClosureParameterRawPointerElementCountExpressions);
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

    private static IReadOnlyList<ComptimeValueArgumentSymbol>? SubstituteComptimeValueArguments(
        IReadOnlyList<ComptimeValueArgumentSymbol>? values,
        IReadOnlyDictionary<string, ComptimeValueArgumentSymbol>? comptimeValueSubstitution)
    {
        if (values is not { Count: > 0 } || comptimeValueSubstitution is not { Count: > 0 })
        {
            return values;
        }

        var changed = false;
        var substituted = new ComptimeValueArgumentSymbol[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!comptimeValueSubstitution.TryGetValue(value.SourceName, out var replacement))
            {
                substituted[index] = value;
                continue;
            }

            substituted[index] = value with
            {
                IntegerValue = replacement.IntegerValue,
                IsSymbolic = replacement.IsSymbolic,
                SymbolicSourceName = replacement.IsSymbolic ? replacement.SourceName : null
            };
            changed = true;
        }

        return changed ? substituted : values;
    }

    private static IEnumerable<string> EnumerateLocalAliasLookupNames(string qualifiedName, string? currentModuleName)
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

    private static bool TryResolveComptimeArrayLengthParameter(
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters,
        out string parameterName)
    {
        parameterName = expression.GetText();
        if (comptimeGenericParameters is null
            || string.IsNullOrWhiteSpace(parameterName)
            || !comptimeGenericParameters.TryGetValue(parameterName, out var parameter)
            || parameter.Type.Kind != StarkTypeKind.Integer)
        {
            parameterName = string.Empty;
            return false;
        }

        return true;
    }
}
