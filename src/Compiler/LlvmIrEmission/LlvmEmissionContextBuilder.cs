using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal static class LlvmEmissionContextBuilder
{
    public static LlvmEmissionContext Build(
        string moduleName,
        string asciiStringTypeName,
        string unicodeStringTypeName,
        ParseResult parseResult,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
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
        return new LlvmEmissionContext(
            moduleName,
            asciiStringTypeName,
            unicodeStringTypeName,
            parseResult,
            loadedModules,
            typeModel,
            enumLayoutModel.Layouts,
            stringConstants,
            targetInfo,
            mapType,
            tryGetConcreteTypeLayout,
            resolveNamedTypeSymbol,
            getScalarizableNamedAggregateFields,
            resolveStringConstant,
            tryGetGlobalAlignmentBytes,
            resolveGlobalSymbolName,
            isConstGlobalName,
            shouldInternalize,
            tryUnwrapSimplePrimaryExpression,
            resolveObjectCreationConstructor,
            getAllocatorSizeType,
            isDebugInfoEnabled,
            getEmptyTupleMetadataRef);
    }
}
