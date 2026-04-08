namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private static StarkPackageEnumLayoutManifest BuildEnumLayoutManifest(
        LoadedModuleDocument module,
        string qualifiedTypeName,
        EnumLayoutSymbol enumLayout)
    {
        return new StarkPackageEnumLayoutManifest(
            qualifiedTypeName,
            enumLayout.Kind.ToString().ToLowerInvariant(),
            new StarkPackageEnumLayoutFieldManifest(
                enumLayout.TagField.Name,
                BuildPublishedAbiTypeReference(enumLayout.TagField.Type, module)),
            enumLayout.OrderedFields
                .Select(field => new StarkPackageEnumLayoutFieldManifest(
                    field.Name,
                    BuildPublishedAbiTypeReference(field.Type, module)))
                .ToArray(),
            enumLayout.Variants.Values
                .OrderBy(static variant => variant.TagValue)
                .Select(variant => new StarkPackageEnumVariantLayoutManifest(
                    variant.Name,
                    variant.TagValue,
                    variant.UsesNamedFields,
                    variant.Fields
                        .Select(field => new StarkPackageEnumVariantLayoutFieldManifest(
                            field.SourcePosition,
                            field.SourceFieldName,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            BuildPublishedAbiTypeReference(field.Type, module)))
                        .ToArray()))
                .ToArray());
    }
}

internal static partial class PackageImageLoader
{
    private static bool TryBuildEnumLayoutSymbol(
        StarkPackageEnumLayoutManifest enumLayout,
        out EnumLayoutSymbol layout)
    {
        layout = default!;

        if (!TryParseEnumLayoutKind(enumLayout.Kind, out var kind))
        {
            return false;
        }

        var tagField = new FieldSymbol(
            enumLayout.TagField.Name,
            BuildTypeSymbol(enumLayout.TagField.Type));
        var orderedFields = enumLayout.OrderedFields
            .Select(field => new FieldSymbol(field.Name, BuildTypeSymbol(field.Type)))
            .ToArray();
        var variants = new Dictionary<string, EnumVariantLayoutSymbol>(StringComparer.Ordinal);

        foreach (var variant in enumLayout.Variants)
        {
            variants[variant.Name] = new EnumVariantLayoutSymbol(
                variant.Name,
                variant.TagValue,
                variant.UsesNamedFields,
                variant.Fields
                    .Select(field => new EnumVariantLayoutFieldSymbol(
                        field.SourcePosition,
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        BuildTypeSymbol(field.Type)))
                    .ToArray());
        }

        layout = new EnumLayoutSymbol(
            enumLayout.QualifiedTypeName,
            kind,
            tagField,
            orderedFields,
            variants);
        return true;
    }
}
