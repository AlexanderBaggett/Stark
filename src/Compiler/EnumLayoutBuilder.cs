using System.Numerics;

namespace Stark.Compiler;

internal static class EnumLayoutBuilder
{
    public static EnumLayoutModel Build(TypeCheckModel typeModel)
    {
        return Build(typeModel.ModuleName, typeModel.NamedTypes);
    }

    public static EnumLayoutModel Build(
        string moduleName,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var layouts = namedTypes.Values
            .Where(static namedType => namedType.Kind == DeclarationKind.Enum)
            .OrderBy(static namedType => namedType.Name, StringComparer.Ordinal)
            .ToDictionary(
                static namedType => namedType.Name,
                static namedType => BuildDirectTagLayout(namedType),
                StringComparer.Ordinal);

        return new EnumLayoutModel(moduleName, layouts);
    }

    private static EnumLayoutSymbol BuildDirectTagLayout(NamedTypeSymbol enumType)
    {
        var variants = new Dictionary<string, EnumVariantLayoutSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();
        var tagField = new FieldSymbol("$tag", CreateTagType(enumType.Variants.Count));
        orderedFields.Add(tagField);

        var tagOrderedVariants = GetTagOrderedVariants(enumType);
        for (var variantIndex = 0; variantIndex < tagOrderedVariants.Count; variantIndex++)
        {
            var variant = tagOrderedVariants[variantIndex];
            var layoutFields = new List<EnumVariantLayoutFieldSymbol>();

            foreach (var field in variant.Fields)
            {
                var storageFieldName = BuildStorageFieldName(variant, field);
                var storageFieldIndex = orderedFields.Count;
                orderedFields.Add(new FieldSymbol(storageFieldName, field.Type));
                layoutFields.Add(new EnumVariantLayoutFieldSymbol(
                    field.Position,
                    field.Name,
                    storageFieldName,
                    storageFieldIndex,
                    field.Type));
            }

            variants[variant.Name] = new EnumVariantLayoutSymbol(
                variant.Name,
                variantIndex,
                variant.UsesNamedFields,
                layoutFields);
        }

        return new EnumLayoutSymbol(
            enumType.Name,
            EnumLayoutKind.DirectTag,
            tagField,
            orderedFields,
            variants);
    }

    private static StarkTypeSymbol CreateTagType(int variantCount)
    {
        var maxTagValue = BigInteger.Max(BigInteger.Zero, variantCount - 1);
        var bitWidth = variantCount <= 0x100
            ? 8
            : variantCount <= 0x1_0000
                ? 16
                : 32;

        return StarkTypeSymbols.Integer(bitWidth, BigInteger.Zero, maxTagValue);
    }

    private static IReadOnlyList<EnumVariantSymbol> GetTagOrderedVariants(NamedTypeSymbol enumType)
    {
        if (enumType.Variants.Count == 0)
        {
            return [];
        }

        var indexedVariants = enumType.Variants
            .Select(static (variant, index) => new { Variant = variant, Index = index })
            .ToArray();
        var zeroTagVariant = indexedVariants.FirstOrDefault(static variant => variant.Variant.IsUnit);
        if (zeroTagVariant is null || zeroTagVariant.Index == 0)
        {
            return enumType.Variants;
        }

        return indexedVariants
            .OrderBy(variant => variant.Index == zeroTagVariant.Index ? 0 : 1)
            .ThenBy(static variant => variant.Index)
            .Select(static variant => variant.Variant)
            .ToArray();
    }

    private static string BuildStorageFieldName(EnumVariantSymbol variant, EnumVariantFieldSymbol field)
    {
        var suffix = field.Name is null
            ? field.Position.ToString()
            : field.Name;
        return $"${variant.Name}_{suffix}";
    }
}
