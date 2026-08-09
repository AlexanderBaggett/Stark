using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmEmissionContext
{
    private readonly IReadOnlyDictionary<string, AsmFunctionModel> _asmFunctions;
    private readonly HashSet<OpaqueAsmSymbolUse> _opaqueAsmSymbolUses = [];
    private readonly Func<StarkTypeSymbol, string> _mapType;
    private readonly Func<StarkTypeSymbol, ConcreteTypeLayout?> _tryGetConcreteTypeLayout;
    private readonly Func<StarkTypeSymbol, NamedTypeSymbol?> _resolveNamedTypeSymbol;
    private readonly Func<NamedTypeSymbol, IReadOnlyList<FieldSymbol>?> _getScalarizableNamedAggregateFields;
    private readonly Func<string, StarkTypeSymbol, EmittedStringConstant> _resolveStringConstant;
    private readonly Func<StarkTypeSymbol, int?> _tryGetGlobalAlignmentBytes;
    private readonly Func<string, string> _resolveGlobalSymbolName;
    private readonly Func<string, bool> _isImmutableGlobalName;
    private readonly Func<StarkVisibility, bool> _shouldInternalize;
    private readonly Func<string, FunctionEffectProfile?> _tryGetFunctionEffects;
    private readonly Func<StarkParser.ExpressionContext, StarkParser.PrimaryExpressionContext?> _tryUnwrapSimplePrimaryExpression;
    private readonly Func<StarkParser.ObjectCreationExpressionContext, TypedConstructorShape?> _resolveObjectCreationConstructor;
    private readonly Func<string> _getAllocatorSizeType;
    private readonly Func<bool> _isDebugInfoEnabled;
    private readonly Func<string> _getEmptyTupleMetadataRef;
    private readonly Func<StarkTypeSymbol, string?> _getValueRangeMetadataRef;
    private readonly Func<StarkTypeSymbol, SsaIntegerRangeFact, string?> _getValueRangeFactMetadataRef;
    private readonly Func<string, string, string> _getTbaaTypeDescriptorRef;
    private readonly Func<string, string, IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)>, string> _getTbaaStructTypeDescriptorRef;
    private readonly Func<string, string, long, string> _getTbaaAccessTagRef;
    private readonly Func<string, string, string> _getAliasScopeDomainRef;
    private readonly Func<string, string, string, string> _getAliasScopeRef;
    private readonly Func<IReadOnlyList<string>, string> _getMetadataTupleRef;
    private readonly Func<string, Func<string, string>, string> _getSelfReferentialMetadataRef;

    public LlvmEmissionContext(
        string moduleName,
        string asciiStringTypeName,
        string unicodeStringTypeName,
        ParseResult parseResult,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IEnumerable<EmittedStringConstant> stringConstants,
        LlvmTargetInfo? targetInfo,
        Func<StarkTypeSymbol, string> mapType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedTypeSymbol,
        Func<NamedTypeSymbol, IReadOnlyList<FieldSymbol>?> getScalarizableNamedAggregateFields,
        Func<string, StarkTypeSymbol, EmittedStringConstant> resolveStringConstant,
        Func<StarkTypeSymbol, int?> tryGetGlobalAlignmentBytes,
        Func<string, string> resolveGlobalSymbolName,
        Func<string, bool> isImmutableGlobalName,
        Func<StarkVisibility, bool> shouldInternalize,
        Func<string, FunctionEffectProfile?> tryGetFunctionEffects,
        Func<StarkParser.ExpressionContext, StarkParser.PrimaryExpressionContext?> tryUnwrapSimplePrimaryExpression,
        Func<StarkParser.ObjectCreationExpressionContext, TypedConstructorShape?> resolveObjectCreationConstructor,
        Func<string> getAllocatorSizeType,
        Func<bool> isDebugInfoEnabled,
        Func<string> getEmptyTupleMetadataRef,
        Func<StarkTypeSymbol, string?> getValueRangeMetadataRef,
        Func<StarkTypeSymbol, SsaIntegerRangeFact, string?> getValueRangeFactMetadataRef,
        Func<string, string, string> getTbaaTypeDescriptorRef,
        Func<string, string, IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)>, string> getTbaaStructTypeDescriptorRef,
        Func<string, string, long, string> getTbaaAccessTagRef,
        Func<string, string, string> getAliasScopeDomainRef,
        Func<string, string, string, string> getAliasScopeRef,
        Func<IReadOnlyList<string>, string> getMetadataTupleRef,
        Func<string, Func<string, string>, string> getSelfReferentialMetadataRef)
    {
        ModuleName = moduleName;
        AsciiStringTypeName = asciiStringTypeName;
        UnicodeStringTypeName = unicodeStringTypeName;
        ParseResult = parseResult;
        LoadedModules = loadedModules;
        TypeModel = typeModel;
        EnumLayouts = enumLayouts;
        StringConstants = stringConstants.ToArray();
        TargetInfo = targetInfo;
        _asmFunctions = BuildAsmFunctionIndex(loadedModules);
        _mapType = mapType;
        _tryGetConcreteTypeLayout = tryGetConcreteTypeLayout;
        _resolveNamedTypeSymbol = resolveNamedTypeSymbol;
        _getScalarizableNamedAggregateFields = getScalarizableNamedAggregateFields;
        _resolveStringConstant = resolveStringConstant;
        _tryGetGlobalAlignmentBytes = tryGetGlobalAlignmentBytes;
        _resolveGlobalSymbolName = resolveGlobalSymbolName;
        _isImmutableGlobalName = isImmutableGlobalName;
        _shouldInternalize = shouldInternalize;
        _tryGetFunctionEffects = tryGetFunctionEffects;
        _tryUnwrapSimplePrimaryExpression = tryUnwrapSimplePrimaryExpression;
        _resolveObjectCreationConstructor = resolveObjectCreationConstructor;
        _getAllocatorSizeType = getAllocatorSizeType;
        _isDebugInfoEnabled = isDebugInfoEnabled;
        _getEmptyTupleMetadataRef = getEmptyTupleMetadataRef;
        _getValueRangeMetadataRef = getValueRangeMetadataRef;
        _getValueRangeFactMetadataRef = getValueRangeFactMetadataRef;
        _getTbaaTypeDescriptorRef = getTbaaTypeDescriptorRef;
        _getTbaaStructTypeDescriptorRef = getTbaaStructTypeDescriptorRef;
        _getTbaaAccessTagRef = getTbaaAccessTagRef;
        _getAliasScopeDomainRef = getAliasScopeDomainRef;
        _getAliasScopeRef = getAliasScopeRef;
        _getMetadataTupleRef = getMetadataTupleRef;
        _getSelfReferentialMetadataRef = getSelfReferentialMetadataRef;
    }

    public string ModuleName { get; }

    public string AsciiStringTypeName { get; }

    public string UnicodeStringTypeName { get; }

    public ParseResult ParseResult { get; }

    public LoadedModuleSet LoadedModules { get; }

    public TypeCheckModel TypeModel { get; }

    public IReadOnlyDictionary<string, EnumLayoutSymbol> EnumLayouts { get; }

    public IReadOnlyList<EmittedStringConstant> StringConstants { get; }

    public LlvmTargetInfo? TargetInfo { get; }

    // Mach-O has no COMDAT support; linkonce_odr definitions must be emitted without
    // comdat groups on Apple targets.
    public bool TargetSupportsComdat
    {
        get
        {
            var triple = TargetInfo?.Triple;
            if (string.IsNullOrWhiteSpace(triple))
            {
                return true;
            }

            return !triple.Contains("darwin", StringComparison.OrdinalIgnoreCase)
                && !triple.Contains("macos", StringComparison.OrdinalIgnoreCase)
                && !triple.Contains("apple", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string AllocatorSizeType => _getAllocatorSizeType();

    public bool DebugInfoEnabled => _isDebugInfoEnabled();

    public string EmptyTupleMetadataRef => _getEmptyTupleMetadataRef();

    public string? GetValueRangeMetadataRef(StarkTypeSymbol type) => _getValueRangeMetadataRef(type);

    public string? GetValueRangeMetadataRef(StarkTypeSymbol type, SsaIntegerRangeFact range) =>
        _getValueRangeFactMetadataRef(type, range);

    public string GetTbaaTypeDescriptorRef(string key, string displayName) =>
        _getTbaaTypeDescriptorRef(key, displayName);

    public string GetTbaaStructTypeDescriptorRef(
        string key,
        string displayName,
        IReadOnlyList<(string TypeDescriptorRef, long OffsetBytes)> fields) =>
        _getTbaaStructTypeDescriptorRef(key, displayName, fields);

    public string GetTbaaAccessTagRef(string baseTypeDescriptorRef, string accessTypeDescriptorRef, long offsetBytes) =>
        _getTbaaAccessTagRef(baseTypeDescriptorRef, accessTypeDescriptorRef, offsetBytes);

    public string GetAliasScopeDomainRef(string key, string displayName) =>
        _getAliasScopeDomainRef(key, displayName);

    public string GetAliasScopeRef(string key, string domainRef, string displayName) =>
        _getAliasScopeRef(key, domainRef, displayName);

    public string GetMetadataTupleRef(IReadOnlyList<string> items) => _getMetadataTupleRef(items);

    public string GetSelfReferentialMetadataRef(string key, Func<string, string> buildBody) =>
        _getSelfReferentialMetadataRef(key, buildBody);

    public string MapType(StarkTypeSymbol type) => _mapType(type);

    public ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _tryGetConcreteTypeLayout(type);

    public NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _resolveNamedTypeSymbol(type);

    public IReadOnlyList<FieldSymbol>? GetScalarizableNamedAggregateFields(NamedTypeSymbol namedType) =>
        _getScalarizableNamedAggregateFields(namedType);

    public EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type) =>
        _resolveStringConstant(literalText, type);

    public int? TryGetGlobalAlignmentBytes(StarkTypeSymbol type) => _tryGetGlobalAlignmentBytes(type);

    public string ResolveGlobalSymbolName(string globalName) => _resolveGlobalSymbolName(globalName);

    public bool IsImmutableGlobalName(string globalName) => _isImmutableGlobalName(globalName);

    public bool ShouldInternalize(StarkVisibility visibility) => _shouldInternalize(visibility);

    public FunctionEffectProfile? TryGetFunctionEffects(string functionName) => _tryGetFunctionEffects(functionName);

    public bool TryGetAsmFunction(string functionName, out AsmFunctionModel asmFunction) =>
        _asmFunctions.TryGetValue(functionName, out asmFunction!);

    public IReadOnlyCollection<OpaqueAsmSymbolUse> OpaqueAsmSymbolUses => _opaqueAsmSymbolUses;

    public void RegisterOpaqueAsmSymbolUses(
        string asmFunctionName,
        IReadOnlyList<AsmSymbolReferenceModel> symbols)
    {
        foreach (var symbol in symbols)
        {
            _opaqueAsmSymbolUses.Add(new OpaqueAsmSymbolUse(asmFunctionName, symbol.SourceName));
        }
    }

    public StarkParser.PrimaryExpressionContext? TryUnwrapSimplePrimaryExpression(StarkParser.ExpressionContext expression) =>
        _tryUnwrapSimplePrimaryExpression(expression);

    public TypedConstructorShape? ResolveObjectCreationConstructor(StarkParser.ObjectCreationExpressionContext objectCreation) =>
        _resolveObjectCreationConstructor(objectCreation);

    private static IReadOnlyDictionary<string, AsmFunctionModel> BuildAsmFunctionIndex(LoadedModuleSet loadedModules)
    {
        var functions = new Dictionary<string, AsmFunctionModel>(StringComparer.Ordinal);
        foreach (var module in loadedModules.Modules.Values)
        {
            foreach (var declaration in module.SyntaxModel.Declarations)
            {
                if (declaration.Function?.Asm is not { } asmFunction)
                {
                    continue;
                }

                var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
                functions[FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName)] = asmFunction;
            }
        }

        return functions;
    }
}

internal sealed record OpaqueAsmSymbolUse(
    string AsmFunctionName,
    string SourceName);
