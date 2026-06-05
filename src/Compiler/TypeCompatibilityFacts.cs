using System.Numerics;

namespace Stark.Compiler;

internal static class TypeCompatibilityFacts
{
    private enum FunctionPointerParameterMemoryRelation
    {
        None,
        Disjoint,
        Overlap,
        Same
    }

    public static bool CanAssign(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.Error || source.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (!AreQualifiersAssignable(target, source))
        {
            return false;
        }

        if (Equals(target, source))
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Kind == StarkTypeKind.Integer)
        {
            if (target.BitWidth is null || source.BitWidth is null)
            {
                return false;
            }

            if (!TryGetEffectiveIntegerRange(source, out var sourceMin, out var sourceMax)
                || !TryGetEffectiveIntegerRange(target, out var targetMin, out var targetMax))
            {
                return false;
            }

            return IsRangeContained(sourceMin, sourceMax, targetMin, targetMax);
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Float)
        {
            return source.BitWidth <= target.BitWidth;
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Integer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            if (target.ElementType is null || source.ElementType is null)
            {
                return target.IsMutablePointer == source.IsMutablePointer;
            }

            if (target.IsMutablePointer && !source.IsMutablePointer)
            {
                return false;
            }

            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.Null)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.FunctionPointer && source.Kind == StarkTypeKind.FunctionPointer)
        {
            return AreFunctionPointerTypesAssignable(target, source);
        }

        if (target.Kind == StarkTypeKind.Closure && source.Kind == StarkTypeKind.Closure)
        {
            return AreClosureTypesAssignable(target, source);
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.FixedArray && target.ElementType is not null && source.ElementType is not null)
        {
            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.FixedArray && source.Kind == StarkTypeKind.FixedArray)
        {
            return target.FixedLength == source.FixedLength
                && target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.Slice)
        {
            return target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.Dynamic && source.Kind == StarkTypeKind.Dynamic)
        {
            return target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType)
                && CanAssign(source.ElementType, target.ElementType);
        }

        if (target.Kind == StarkTypeKind.AssociatedType && source.Kind == StarkTypeKind.AssociatedType)
        {
            return string.Equals(target.AssociatedTypeName, source.AssociatedTypeName, StringComparison.Ordinal)
                && target.AssociatedTypeOwner is not null
                && source.AssociatedTypeOwner is not null
                && CanAssign(target.AssociatedTypeOwner, source.AssociatedTypeOwner);
        }

        return target.Kind == StarkTypeKind.Named
            && source.Kind == StarkTypeKind.Named
            && string.Equals(target.NamedType, source.NamedType, StringComparison.Ordinal);
    }

    public static bool AreFunctionPointerTypesAssignable(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.FunctionPointerKind is not { } targetKind
            || source.FunctionPointerKind is not { } sourceKind
            || target.FunctionPointerReturnType is not { } targetReturn
            || source.FunctionPointerReturnType is not { } sourceReturn
            || target.FunctionPointerParameterTypes is not { } targetParameters
            || source.FunctionPointerParameterTypes is not { } sourceParameters
            || targetParameters.Count != sourceParameters.Count)
        {
            return false;
        }

        if (!FunctionKindSatisfies(sourceKind, targetKind)
            || source.FunctionPointerAbi != target.FunctionPointerAbi
            || !Equals(targetReturn, sourceReturn))
        {
            return false;
        }

        for (var index = 0; index < targetParameters.Count; index++)
        {
            if (!Equals(sourceParameters[index], targetParameters[index]))
            {
                return false;
            }

            if (!string.Equals(
                    GetFunctionPointerParameterRawPointerElementCountExpression(source, index),
                    GetFunctionPointerParameterRawPointerElementCountExpression(target, index),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return AreFunctionPointerMemoryContractsAssignable(target, source, targetParameters);
    }

    public static bool AreClosureTypesAssignable(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.ClosureFunctionKind is not { } targetKind
            || source.ClosureFunctionKind is not { } sourceKind
            || target.ClosureReturnType is not { } targetReturn
            || source.ClosureReturnType is not { } sourceReturn
            || target.ClosureParameterTypes is not { } targetParameters
            || source.ClosureParameterTypes is not { } sourceParameters
            || targetParameters.Count != sourceParameters.Count)
        {
            return false;
        }

        if (target.ClosureStorageKind != source.ClosureStorageKind
            || target.ClosureCallCapability != source.ClosureCallCapability
            || !FunctionKindSatisfies(sourceKind, targetKind)
            || !Equals(targetReturn, sourceReturn))
        {
            return false;
        }

        for (var index = 0; index < targetParameters.Count; index++)
        {
            if (!Equals(sourceParameters[index], targetParameters[index]))
            {
                return false;
            }

            if (!string.Equals(
                    StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(source, index),
                    StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(target, index),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return AreClosureMemoryContractsAssignable(target, source, targetParameters);
    }

    public static StarkTypeSymbol FunctionPointerTypeForSignature(TypedFunctionSignature function)
    {
        var parameterNameMap = function.Parameters
            .Select((parameter, index) => new
            {
                parameter.Name,
                SyntheticName = $"arg{index}"
            })
            .ToDictionary(static pair => pair.Name, static pair => pair.SyntheticName, StringComparer.Ordinal);

        return StarkTypeSymbols.FunctionPointer(
            function.Kind,
            function.ReturnType,
            function.Parameters.Select(static parameter => parameter.Type).ToArray(),
            MapDisjointGroups(function.DisjointGroups, parameterNameMap),
            MapOverlapGroups(function.OverlapGroups, parameterNameMap),
            MapSameGroups(function.SameGroups, parameterNameMap),
            function.Parameters
                .Select(parameter => MapRawPointerElementCountExpression(
                    parameter.RawPointerElementCountExpression,
                    parameterNameMap))
                .ToArray(),
            function.FfiAbi);
    }

    public static StarkTypeSymbol ClosureTypeForSignature(
        TypedFunctionSignature function,
        StarkClosureStorageKind storageKind,
        StarkClosureCallCapability callCapability)
    {
        var parameterNameMap = function.Parameters
            .Select((parameter, index) => new
            {
                parameter.Name,
                SyntheticName = $"arg{index}"
            })
            .ToDictionary(static pair => pair.Name, static pair => pair.SyntheticName, StringComparer.Ordinal);

        return StarkTypeSymbols.Closure(
            storageKind,
            callCapability,
            function.Kind,
            function.ReturnType,
            function.Parameters.Select(static parameter => parameter.Type).ToArray(),
            MapDisjointGroups(function.DisjointGroups, parameterNameMap),
            MapOverlapGroups(function.OverlapGroups, parameterNameMap),
            MapSameGroups(function.SameGroups, parameterNameMap),
            function.Parameters
                .Select(parameter => MapRawPointerElementCountExpression(
                    parameter.RawPointerElementCountExpression,
                    parameterNameMap))
                .ToArray());
    }

    private static string? GetFunctionPointerParameterRawPointerElementCountExpression(
        StarkTypeSymbol functionPointerType,
        int parameterIndex)
    {
        return StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
            functionPointerType,
            parameterIndex);
    }

    private static string? MapRawPointerElementCountExpression(
        string? expression,
        IReadOnlyDictionary<string, string> parameterNameMap)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        return parameterNameMap.TryGetValue(expression, out var syntheticName)
            ? syntheticName
            : expression;
    }

    public static bool FunctionKindSatisfies(StarkFunctionKind source, StarkFunctionKind target)
    {
        return (!FunctionKindFacts.IsLaw(target) || FunctionKindFacts.IsLaw(source))
            && (!FunctionKindFacts.IsFinite(target) || FunctionKindFacts.IsFinite(source));
    }

    public static bool AreQualifiersAssignable(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (!IsBorrowAssignable(target.BorrowKind, source.BorrowKind))
        {
            return false;
        }

        if (!IsAccessAssignable(target.AccessKind, source.AccessKind))
        {
            return false;
        }

        if (target.InitializationKind != source.InitializationKind)
        {
            return false;
        }

        if (target.IsMutableView && !source.IsMutableView)
        {
            return false;
        }

        return true;
    }

    private static bool AreFunctionPointerMemoryContractsAssignable(
        StarkTypeSymbol target,
        StarkTypeSymbol source,
        IReadOnlyList<StarkTypeSymbol> parameterTypes)
    {
        for (var leftIndex = 0; leftIndex < parameterTypes.Count; leftIndex++)
        {
            if (!ParameterMemoryContractFacts.IsMemoryBacked(parameterTypes[leftIndex]))
            {
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < parameterTypes.Count; rightIndex++)
            {
                if (!ParameterMemoryContractFacts.IsMemoryBacked(parameterTypes[rightIndex]))
                {
                    continue;
                }

                var leftName = $"arg{leftIndex}";
                var rightName = $"arg{rightIndex}";
                var targetRelation = GetFunctionPointerParameterMemoryRelation(target, leftName, rightName);
                var sourceRelation = GetFunctionPointerParameterMemoryRelation(source, leftName, rightName);
                if (!SourceMemoryRelationSatisfiesTarget(sourceRelation, targetRelation))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AreClosureMemoryContractsAssignable(
        StarkTypeSymbol target,
        StarkTypeSymbol source,
        IReadOnlyList<StarkTypeSymbol> parameterTypes)
    {
        for (var leftIndex = 0; leftIndex < parameterTypes.Count; leftIndex++)
        {
            if (!ParameterMemoryContractFacts.IsMemoryBacked(parameterTypes[leftIndex]))
            {
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < parameterTypes.Count; rightIndex++)
            {
                if (!ParameterMemoryContractFacts.IsMemoryBacked(parameterTypes[rightIndex]))
                {
                    continue;
                }

                var leftName = $"arg{leftIndex}";
                var rightName = $"arg{rightIndex}";
                var targetRelation = GetClosureParameterMemoryRelation(target, leftName, rightName);
                var sourceRelation = GetClosureParameterMemoryRelation(source, leftName, rightName);
                if (!SourceMemoryRelationSatisfiesTarget(sourceRelation, targetRelation))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SourceMemoryRelationSatisfiesTarget(
        FunctionPointerParameterMemoryRelation source,
        FunctionPointerParameterMemoryRelation target)
    {
        return target switch
        {
            FunctionPointerParameterMemoryRelation.Same => source is FunctionPointerParameterMemoryRelation.Same
                or FunctionPointerParameterMemoryRelation.Overlap,
            FunctionPointerParameterMemoryRelation.Overlap or FunctionPointerParameterMemoryRelation.None => source == FunctionPointerParameterMemoryRelation.Overlap,
            FunctionPointerParameterMemoryRelation.Disjoint => source is FunctionPointerParameterMemoryRelation.Disjoint
                or FunctionPointerParameterMemoryRelation.Overlap,
            _ => false
        };
    }

    private static FunctionPointerParameterMemoryRelation GetFunctionPointerParameterMemoryRelation(
        StarkTypeSymbol type,
        string leftName,
        string rightName)
    {
        if (ContainsParameterPair(type.FunctionPointerSameParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Same;
        }

        if (ContainsParameterPair(type.FunctionPointerOverlapParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Overlap;
        }

        if (ContainsParameterPair(type.FunctionPointerDisjointParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Disjoint;
        }

        return FunctionPointerParameterMemoryRelation.None;
    }

    private static FunctionPointerParameterMemoryRelation GetClosureParameterMemoryRelation(
        StarkTypeSymbol type,
        string leftName,
        string rightName)
    {
        if (ContainsParameterPair(type.ClosureSameParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Same;
        }

        if (ContainsParameterPair(type.ClosureOverlapParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Overlap;
        }

        if (ContainsParameterPair(type.ClosureDisjointParameterGroups, leftName, rightName))
        {
            return FunctionPointerParameterMemoryRelation.Disjoint;
        }

        return FunctionPointerParameterMemoryRelation.None;
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterDisjointGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => !group.HasSubregions && GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterOverlapGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterSameGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool GroupContainsParameterPair(
        IReadOnlyList<string> parameterNames,
        string leftName,
        string rightName)
    {
        var containsLeft = false;
        var containsRight = false;
        foreach (var parameterName in parameterNames)
        {
            containsLeft |= string.Equals(parameterName, leftName, StringComparison.Ordinal);
            containsRight |= string.Equals(parameterName, rightName, StringComparison.Ordinal);
            if (containsLeft && containsRight)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ParameterDisjointGroup> MapDisjointGroups(
        IReadOnlyList<ParameterDisjointGroup> groups,
        IReadOnlyDictionary<string, string> parameterNameMap)
    {
        return groups
            .Select(group =>
            {
                if (group.HasSubregions)
                {
                    var regions = group.MemoryRegions
                        .Select(region => parameterNameMap.TryGetValue(region.ParameterName, out var mappedName)
                            ? region with { ParameterName = mappedName }
                            : null)
                        .Where(static region => region is not null)
                        .Select(static region => region!)
                        .ToArray();
                    var names = regions
                        .Select(static region => region.ParameterName)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return regions.Length >= 2
                        ? new ParameterDisjointGroup(names, regions)
                        : null;
                }

                var mappedNames = MapGroup(group.ParameterNames, parameterNameMap);
                return mappedNames.Count >= 2 ? new ParameterDisjointGroup(mappedNames) : null;
            })
            .Where(static group => group is not null)
            .Cast<ParameterDisjointGroup>()
            .ToArray();
    }

    private static IReadOnlyList<ParameterOverlapGroup> MapOverlapGroups(
        IReadOnlyList<ParameterOverlapGroup> groups,
        IReadOnlyDictionary<string, string> parameterNameMap)
    {
        return groups
            .Select(group => MapGroup(group.ParameterNames, parameterNameMap))
            .Where(static names => names.Count >= 2)
            .Select(static names => new ParameterOverlapGroup(names))
            .ToArray();
    }

    private static IReadOnlyList<ParameterSameGroup> MapSameGroups(
        IReadOnlyList<ParameterSameGroup> groups,
        IReadOnlyDictionary<string, string> parameterNameMap)
    {
        return groups
            .Select(group => MapGroup(group.ParameterNames, parameterNameMap))
            .Where(static names => names.Count >= 2)
            .Select(static names => new ParameterSameGroup(names))
            .ToArray();
    }

    private static IReadOnlyList<string> MapGroup(
        IReadOnlyList<string> parameterNames,
        IReadOnlyDictionary<string, string> parameterNameMap)
    {
        var names = new List<string>(parameterNames.Count);
        foreach (var parameterName in parameterNames)
        {
            if (parameterNameMap.TryGetValue(parameterName, out var syntheticName))
            {
                names.Add(syntheticName);
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool WouldEraseFrozenProvenance(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (source.AccessKind == StarkAccessKind.Frozen && target.AccessKind != StarkAccessKind.Frozen)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            if (target.ElementType is not null && source.ElementType is not null)
            {
                return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
            }
        }

        if ((target.Kind == StarkTypeKind.FixedArray || target.Kind == StarkTypeKind.Slice)
            && source.Kind == target.Kind
            && target.ElementType is not null
            && source.ElementType is not null)
        {
            return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
        }

        return false;
    }

    public static bool IsRangeContained(BigInteger? sourceMin, BigInteger? sourceMax, BigInteger? targetMin, BigInteger? targetMax)
    {
        if (targetMin is null || targetMax is null)
        {
            return true;
        }

        if (sourceMin is null || sourceMax is null)
        {
            return false;
        }

        return sourceMin >= targetMin && sourceMax <= targetMax;
    }

    private static bool TryGetEffectiveIntegerRange(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out min, out max);
    }

    private static bool IsBorrowAssignable(StarkBorrowKind target, StarkBorrowKind source)
    {
        if (target == StarkBorrowKind.None || source == StarkBorrowKind.None)
        {
            return target == source;
        }

        return BorrowRank(source) >= BorrowRank(target);
    }

    private static bool IsAccessAssignable(StarkAccessKind target, StarkAccessKind source)
    {
        if (target == source)
        {
            return true;
        }

        if (source == StarkAccessKind.Frozen || target == StarkAccessKind.Frozen)
        {
            return false;
        }

        return AccessRank(source) >= AccessRank(target);
    }

    private static int BorrowRank(StarkBorrowKind kind)
    {
        return kind switch
        {
            StarkBorrowKind.Borrow => 1,
            StarkBorrowKind.RetBorrow => 2,
            StarkBorrowKind.StoreBorrow => 3,
            _ => 0
        };
    }

    private static int AccessRank(StarkAccessKind kind)
    {
        return kind switch
        {
            StarkAccessKind.Shared => 1,
            StarkAccessKind.Frozen => 2,
            _ => 0
        };
    }
}
