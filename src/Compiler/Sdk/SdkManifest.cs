namespace Stark.Compiler;

internal enum SdkDistributionKind
{
    Release,
    Development,
    Stage
}

internal sealed record SdkManifest(
    int SchemaVersion,
    SdkDistributionKind Kind,
    string SdkVersion,
    string CompilerCompatibility,
    int PackageFormatVersion,
    SdkTargetDescriptor Target,
    IReadOnlyList<SdkModuleOwnership> Modules,
    IReadOnlyList<SdkPackageDescriptor> Packages,
    IReadOnlyList<string> DevelopmentSourceRoots);

internal sealed record SdkModuleOwnership(
    string ModuleName,
    string PackageId);

internal sealed record SdkPackageDescriptor(
    string Id,
    string Version,
    string Profile,
    string ImagePath,
    string? LibraryPath,
    string? ApiHash,
    string? ContentHash,
    string? ImageSha256,
    string? LibrarySha256,
    IReadOnlyList<SdkPackageDependency> Dependencies,
    SdkNativePackageDescriptor Native);

internal sealed record SdkPackageDependency(
    string PackageId,
    string? ApiHash,
    string? ContentHash);

internal sealed record SdkNativePackageDescriptor(
    IReadOnlyList<string> ArtifactPaths,
    IReadOnlyList<string> IncludeDirectories,
    IReadOnlyList<string> LibraryDirectories,
    IReadOnlyList<string> RuntimeFiles,
    IReadOnlyList<string> LicenseFiles,
    IReadOnlyList<SdkNativeFileChecksum> FileChecksums,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<string> LinkArguments);

internal sealed record SdkNativeFileChecksum(
    string Path,
    string Sha256);

internal sealed record SdkDiagnostic(
    string Code,
    string Message,
    string? Path = null);

internal sealed record SdkManifestLoadResult(
    string SdkRoot,
    string ManifestPath,
    SdkManifest? Manifest,
    SdkPackageIndex? PackageIndex,
    IReadOnlyList<SdkDiagnostic> Diagnostics)
{
    public bool Succeeded => Manifest is not null
        && PackageIndex is not null
        && Diagnostics.Count == 0;
}
