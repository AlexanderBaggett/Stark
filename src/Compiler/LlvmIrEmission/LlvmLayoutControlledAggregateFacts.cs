namespace Stark.Compiler.LlvmIrEmission;

internal sealed record LlvmPhysicalAggregateElement(
    int? SourceFieldIndex,
    string? SourceFieldName,
    StarkTypeSymbol? FieldType,
    int OffsetBytes,
    int SizeBytes);

internal static class LlvmLayoutControlledAggregateFacts
{
    public static bool RequiresPhysicalLayout(NamedTypeSymbol namedType)
    {
        return namedType.Layout?.Kind == StructLayoutKind.Explicit
            || namedType.Layout?.PackBytes is not null;
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
