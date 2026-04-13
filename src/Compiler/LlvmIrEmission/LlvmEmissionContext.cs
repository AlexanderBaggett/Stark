using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmEmissionContext
{
    private readonly Func<StarkTypeSymbol, string> _mapType;
    private readonly Func<StarkTypeSymbol, ConcreteTypeLayout?> _tryGetConcreteTypeLayout;
    private readonly Func<StarkTypeSymbol, NamedTypeSymbol?> _resolveNamedTypeSymbol;
    private readonly Func<NamedTypeSymbol, IReadOnlyList<FieldSymbol>?> _getScalarizableNamedAggregateFields;
    private readonly Func<string, StarkTypeSymbol, EmittedStringConstant> _resolveStringConstant;
    private readonly Func<StarkTypeSymbol, int?> _tryGetGlobalAlignmentBytes;
    private readonly Func<string, string> _resolveGlobalSymbolName;
    private readonly Func<string, bool> _isConstGlobalName;
    private readonly Func<StarkVisibility, bool> _shouldInternalize;
    private readonly Func<StarkParser.ExpressionContext, StarkParser.PrimaryExpressionContext?> _tryUnwrapSimplePrimaryExpression;
    private readonly Func<StarkParser.ObjectCreationExpressionContext, TypedConstructorShape?> _resolveObjectCreationConstructor;
    private readonly Func<string> _getAllocatorSizeType;
    private readonly Func<bool> _isDebugInfoEnabled;
    private readonly Func<string> _getEmptyTupleMetadataRef;

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
        Func<string, bool> isConstGlobalName,
        Func<StarkVisibility, bool> shouldInternalize,
        Func<StarkParser.ExpressionContext, StarkParser.PrimaryExpressionContext?> tryUnwrapSimplePrimaryExpression,
        Func<StarkParser.ObjectCreationExpressionContext, TypedConstructorShape?> resolveObjectCreationConstructor,
        Func<string> getAllocatorSizeType,
        Func<bool> isDebugInfoEnabled,
        Func<string> getEmptyTupleMetadataRef)
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
        _mapType = mapType;
        _tryGetConcreteTypeLayout = tryGetConcreteTypeLayout;
        _resolveNamedTypeSymbol = resolveNamedTypeSymbol;
        _getScalarizableNamedAggregateFields = getScalarizableNamedAggregateFields;
        _resolveStringConstant = resolveStringConstant;
        _tryGetGlobalAlignmentBytes = tryGetGlobalAlignmentBytes;
        _resolveGlobalSymbolName = resolveGlobalSymbolName;
        _isConstGlobalName = isConstGlobalName;
        _shouldInternalize = shouldInternalize;
        _tryUnwrapSimplePrimaryExpression = tryUnwrapSimplePrimaryExpression;
        _resolveObjectCreationConstructor = resolveObjectCreationConstructor;
        _getAllocatorSizeType = getAllocatorSizeType;
        _isDebugInfoEnabled = isDebugInfoEnabled;
        _getEmptyTupleMetadataRef = getEmptyTupleMetadataRef;
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

    public string AllocatorSizeType => _getAllocatorSizeType();

    public bool DebugInfoEnabled => _isDebugInfoEnabled();

    public string EmptyTupleMetadataRef => _getEmptyTupleMetadataRef();

    public string MapType(StarkTypeSymbol type) => _mapType(type);

    public ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _tryGetConcreteTypeLayout(type);

    public NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _resolveNamedTypeSymbol(type);

    public IReadOnlyList<FieldSymbol>? GetScalarizableNamedAggregateFields(NamedTypeSymbol namedType) =>
        _getScalarizableNamedAggregateFields(namedType);

    public EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type) =>
        _resolveStringConstant(literalText, type);

    public int? TryGetGlobalAlignmentBytes(StarkTypeSymbol type) => _tryGetGlobalAlignmentBytes(type);

    public string ResolveGlobalSymbolName(string globalName) => _resolveGlobalSymbolName(globalName);

    public bool IsConstGlobalName(string globalName) => _isConstGlobalName(globalName);

    public bool ShouldInternalize(StarkVisibility visibility) => _shouldInternalize(visibility);

    public StarkParser.PrimaryExpressionContext? TryUnwrapSimplePrimaryExpression(StarkParser.ExpressionContext expression) =>
        _tryUnwrapSimplePrimaryExpression(expression);

    public TypedConstructorShape? ResolveObjectCreationConstructor(StarkParser.ObjectCreationExpressionContext objectCreation) =>
        _resolveObjectCreationConstructor(objectCreation);
}
