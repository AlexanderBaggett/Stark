namespace Stark.Compiler;

internal static class AbiLoweringHeuristics
{
    private const int AggregateIndirectAbiThresholdBytes = 16;

    public static bool RequiresIndirectParameterAbi(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        return type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None
            || IsLargeByValueAggregate(type, namedTypes, enumLayouts);
    }

    public static bool RequiresIndirectReturnAbi(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        if (type.BorrowKind != StarkBorrowKind.None)
        {
            return false;
        }

        return IsLargeByValueAggregate(type, namedTypes, enumLayouts);
    }

    public static bool IsByValueIndirectParameter(AbiParameterSymbol parameter)
    {
        return parameter.Kind == AbiParameterKind.IndirectIn
            && parameter.SourceType.BorrowKind == StarkBorrowKind.None
            && parameter.SourceType.InitializationKind == StarkInitializationKind.None;
    }

    private static bool IsLargeByValueAggregate(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        if (!IsAggregateAbiCandidate(type))
        {
            return false;
        }

        var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(type, namedTypes, enumLayouts);
        return layout is { SizeBytes: > AggregateIndirectAbiThresholdBytes };
    }

    private static bool IsAggregateAbiCandidate(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Named;
    }
}
