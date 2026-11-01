namespace Stark.Compiler.LlvmIrEmission;

internal sealed record LlvmPhysicalAggregateElement(
    int? SourceFieldIndex,
    string? SourceFieldName,
    StarkTypeSymbol? FieldType,
    int OffsetBytes,
    int SizeBytes);

internal static class LlvmLayoutControlledAggregateFacts
{
    public static NamedTypeSymbol WithStorageFields(
        NamedTypeSymbol namedType,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        return namedType.Kind == DeclarationKind.Enum
            && enumLayouts.TryGetValue(namedType.Name, out var layout)
            ? namedType with { OrderedFields = layout.OrderedFields }
            : namedType;
    }

    public static bool RequiresPhysicalLayout(
        NamedTypeSymbol namedType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
    {
        return RequiresPhysicalLayout(namedType, new HashSet<string>(StringComparer.Ordinal));

        bool RequiresPhysicalLayout(NamedTypeSymbol current, ISet<string> active)
        {
            if (current.Layout?.Kind == StructLayoutKind.Explicit
                || current.Layout?.PackBytes is not null
                || current.Layout?.AlignBytes is not null)
            {
                return true;
            }
            if (!active.Add(current.Name)) return false;
            try
            {
                // LLVM named types do not carry a declaration's explicit alignment.
                // Containing structs and enum payloads must retain that layout too.
                foreach (var field in WithStorageFields(current, enumLayouts).OrderedFields)
                {
                    var type = field.Type;
                    if (type.BorrowKind != StarkBorrowKind.None) continue;
                    while (type.Kind == StarkTypeKind.FixedArray && type.ElementType is { } element)
                        type = element;
                    if (type.Kind == StarkTypeKind.Named
                        && LlvmAggregateEmissionSupport.ResolveNamedTypeSymbol(type, namedTypes) is { } nested
                        && RequiresPhysicalLayout(nested, active))
                        return true;
                }
                return false;
            }
            finally { active.Remove(current.Name); }
        }
    }

    public static bool TryBuildPhysicalElements(
        NamedTypeSymbol namedType,
        ConcreteTypeLayout layout,
        out IReadOnlyList<LlvmPhysicalAggregateElement> elements,
        out bool hasOverlappingFields)
    {
        var physicalElements = new List<LlvmPhysicalAggregateElement>();
        var cursor = 0;
        hasOverlappingFields = false;

        var fieldLayoutsByName = layout.Fields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        var orderedFields = namedType.OrderedFields
            .Select((field, sourceIndex) => (Field: field, SourceIndex: sourceIndex))
            .Where(field => fieldLayoutsByName.ContainsKey(field.Field.Name))
            .OrderBy(field => fieldLayoutsByName[field.Field.Name].OffsetBytes)
            .ThenBy(field => field.SourceIndex)
            .ToArray();

        foreach (var (field, sourceIndex) in orderedFields)
        {
            var fieldLayout = fieldLayoutsByName[field.Name];
            if (fieldLayout.OffsetBytes < cursor)
            {
                hasOverlappingFields = true;
                elements = [];
                return false;
            }

            if (fieldLayout.OffsetBytes > cursor)
            {
                physicalElements.Add(new LlvmPhysicalAggregateElement(
                    SourceFieldIndex: null,
                    SourceFieldName: null,
                    FieldType: null,
                    OffsetBytes: cursor,
                    SizeBytes: fieldLayout.OffsetBytes - cursor));
                cursor = fieldLayout.OffsetBytes;
            }

            physicalElements.Add(new LlvmPhysicalAggregateElement(
                sourceIndex,
                field.Name,
                field.Type,
                fieldLayout.OffsetBytes,
                fieldLayout.SizeBytes));
            cursor = checked(fieldLayout.OffsetBytes + fieldLayout.SizeBytes);
        }

        if (layout.SizeBytes > cursor)
        {
            physicalElements.Add(new LlvmPhysicalAggregateElement(
                SourceFieldIndex: null,
                SourceFieldName: null,
                FieldType: null,
                OffsetBytes: cursor,
                SizeBytes: layout.SizeBytes - cursor));
        }

        elements = physicalElements;
        return true;
    }

    public static bool TryGetStorageElementIndex(
        NamedTypeSymbol namedType,
        ConcreteTypeLayout layout,
        int sourceFieldIndex,
        out int storageElementIndex)
    {
        storageElementIndex = -1;
        if (!TryBuildPhysicalElements(namedType, layout, out var elements, out _))
        {
            return false;
        }

        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].SourceFieldIndex == sourceFieldIndex)
            {
                storageElementIndex = index;
                return true;
            }
        }

        return false;
    }
}
