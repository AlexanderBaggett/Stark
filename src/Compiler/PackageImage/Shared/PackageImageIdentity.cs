using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

/// <summary>
/// Computes package identities from the compiler facts that consumers observe.
/// The API hash covers typed/compiler/template facts plus target and direct
/// dependency APIs. The content hash additionally covers the complete package
/// payload contract, native metadata, build profile, and dependency content.
/// Neither hash includes its own stored value, so identities are reproducible.
/// </summary>
internal static class PackageImageIdentity
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static StarkPackageIdentityManifest Create(
        StarkPackageManifest manifest,
        IReadOnlyList<StarkPackageDependencyIdentityManifest>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var normalizedDependencies = NormalizeDependencies(
            dependencies ?? manifest.Identity?.Dependencies ?? []);
        var modules = manifest.Modules
            .OrderBy(static module => module.ModuleName, StringComparer.Ordinal)
            .ToArray();
        var apiModules = modules
            .Select(static module => new ApiModuleDocument(
                module.ModuleName,
                module.EffectiveSourceSurface.ReExports,
                module.EffectiveTypedInterface,
                module.EffectiveCompilerFacts,
                module.EffectiveGenericTemplates))
            .ToArray();
        var apiDependencies = normalizedDependencies
            .Select(static dependency => new ApiDependencyDocument(
                dependency.PackageId,
                dependency.ApiHash))
            .ToArray();
        var apiHash = Hash(new ApiHashDocument(
            manifest.RootModule,
            manifest.Target,
            apiModules,
            apiDependencies));
        var contentHash = Hash(new ContentHashDocument(
            manifest.RootModule,
            manifest.LibraryFileName,
            modules,
            manifest.NativeDependencies,
            manifest.Target,
            manifest.BuildProfile,
            normalizedDependencies));

        return new StarkPackageIdentityManifest(
            manifest.RootModule,
            apiHash,
            contentHash,
            normalizedDependencies);
    }

    public static StarkPackageManifest Apply(
        StarkPackageManifest manifest,
        IReadOnlyList<StarkPackageDependencyIdentityManifest>? dependencies = null) =>
        manifest with { Identity = Create(manifest, dependencies) };

    public static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<StarkPackageDependencyIdentityManifest> NormalizeDependencies(
        IReadOnlyList<StarkPackageDependencyIdentityManifest> dependencies) =>
        dependencies
            .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal)
            .ToArray();

    private static string Hash<T>(T document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record ApiHashDocument(
        string PackageId,
        StarkPackageTargetManifest? Target,
        IReadOnlyList<ApiModuleDocument> Modules,
        IReadOnlyList<ApiDependencyDocument> Dependencies);

    private sealed record ApiModuleDocument(
        string ModuleName,
        IReadOnlyList<StarkPackageReExportManifest>? ReExports,
        StarkPackageTypedInterfaceSection? TypedInterface,
        StarkPackageCompilerFactsSection? CompilerFacts,
        StarkPackageGenericTemplateSection? GenericTemplates);

    private sealed record ApiDependencyDocument(
        string PackageId,
        string ApiHash);

    private sealed record ContentHashDocument(
        string PackageId,
        string LibraryFileName,
        IReadOnlyList<StarkPackageModuleManifest> Modules,
        StarkPackageNativeDependencyManifest? NativeDependencies,
        StarkPackageTargetManifest? Target,
        StarkPackageBuildProfileManifest? BuildProfile,
        IReadOnlyList<StarkPackageDependencyIdentityManifest> Dependencies);
}
