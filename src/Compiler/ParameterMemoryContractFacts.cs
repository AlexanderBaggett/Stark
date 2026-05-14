namespace Stark.Compiler;

internal static class ParameterMemoryContractFacts
{
    public static bool IsMemoryBacked(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None;
    }

    public static IReadOnlyList<ParameterDisjointGroup> BuildEffectiveDisjointGroups(
        IReadOnlyList<TypedParameterSymbol> parameters,
        IReadOnlyList<ParameterDisjointGroup> explicitDisjointGroups,
        IReadOnlyList<ParameterOverlapGroup> overlapGroups,
        IReadOnlyList<ParameterSameGroup> sameGroups,
        bool applyDefaultNonOverlap)
    {
        var groups = new List<ParameterDisjointGroup>();
        var suppressedPairs = overlapGroups
            .SelectMany(static group => EnumerateNamePairs(group.ParameterNames))
            .Concat(sameGroups.SelectMany(static group => EnumerateNamePairs(group.ParameterNames)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var group in explicitDisjointGroups)
        {
            var names = group.ParameterNames
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length < 2)
            {
                continue;
            }

            var pairNames = EnumerateNamePairs(names).ToArray();
            if (pairNames.Any(suppressedPairs.Contains))
            {
                continue;
            }

            groups.Add(new ParameterDisjointGroup(names));
        }

        if (applyDefaultNonOverlap)
        {
            var defaultMemoryParameters = parameters
                .Where(static parameter => IsMemoryBacked(parameter.Type))
                .Select(static parameter => parameter.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (var leftIndex = 0; leftIndex < defaultMemoryParameters.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < defaultMemoryParameters.Length; rightIndex++)
                {
                    var left = defaultMemoryParameters[leftIndex];
                    var right = defaultMemoryParameters[rightIndex];
                    if (!suppressedPairs.Contains(BuildNamePairKey(left, right)))
                    {
                        groups.Add(new ParameterDisjointGroup([left, right]));
                    }
                }
            }
        }

        return groups
            .GroupBy(static group => string.Join("|", group.ParameterNames.Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static IEnumerable<string> EnumerateNamePairs(IReadOnlyList<string> names)
    {
        var distinctNames = names
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        for (var leftIndex = 0; leftIndex < distinctNames.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < distinctNames.Length; rightIndex++)
            {
                yield return BuildNamePairKey(distinctNames[leftIndex], distinctNames[rightIndex]);
            }
        }
    }

    private static string BuildNamePairKey(string left, string right)
    {
        return string.CompareOrdinal(left, right) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";
    }
}
