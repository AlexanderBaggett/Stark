using System.Numerics;

namespace Stark.Compiler;

internal static class EnumLayoutBuilder
{
    public static EnumLayoutModel Build(TypeCheckModel typeModel)
    {
        var layouts = typeModel.NamedTypes.Values
            .Where(static namedType => namedType.Kind == DeclarationKind.Enum)
            .OrderBy(static namedType => namedType.Name, StringComparer.Ordinal)
            .ToDictionary(
                static namedType => namedType.Name,
                static namedType => BuildDirectTagLayout(namedType),
                StringComparer.Ordinal);

        return new EnumLayoutModel(typeModel.ModuleName, layouts);
    }

    private static EnumLayoutSymbol BuildDirectTagLayout(NamedTypeSymbol enumType)
    {
        var variants = new Dictionary<string, EnumVariantLayoutSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>();
        var tagField = new FieldSymbol("$tag", CreateTagType(enumType.Variants.Count));
        orderedFields.Add(tagField);

        for (var variantIndex = 0; variantIndex < enumType.Variants.Count; variantIndex++)
        {
            var variant = enumType.Variants[variantIndex];
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

    private static string BuildStorageFieldName(EnumVariantSymbol variant, EnumVariantFieldSymbol field)
    {
        var suffix = field.Name is null
            ? field.Position.ToString()
            : field.Name;
        return $"${variant.Name}_{suffix}";
    }
}
