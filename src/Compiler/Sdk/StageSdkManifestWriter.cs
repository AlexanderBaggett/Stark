using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

/// <summary>
/// Assembles the binary package images produced for one bootstrap stage into
/// an explicit SDK contract. Directory enumeration happens only while writing
/// the manifest; normal compiler resolution consumes the resulting module and
/// package index and never infers ownership from the stage directory layout.
/// </summary>
internal static class StageSdkManifestWriter
{
    public const string CommandOption = "--write-stage-sdk-manifest";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static bool TryWrite(
        string sdkRoot,
        string stageName,
        out string manifestPath,
        out string error)
    {
        manifestPath = string.Empty;
        error = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(stageName))
            {
                error = "Stage name must not be empty.";
                return false;
            }

            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
            Directory.CreateDirectory(canonicalRoot);
            var packageImagePaths = EnumeratePackageImages(canonicalRoot);
            if (packageImagePaths.Count == 0)
            {
                error = $"No binary package images were found below '{Path.Combine(canonicalRoot, "stdlib")}' or '{Path.Combine(canonicalRoot, "vendor")}'.";
                return false;
            }

            SdkTargetDescriptor? target = null;
            uint? packageFormatVersion = null;
            string? packageProfile = null;
            var packages = new List<StagePackageDocument>(packageImagePaths.Count);
            var modules = new List<StageModuleDocument>();
            var packageIds = new HashSet<string>(StringComparer.Ordinal);
            var moduleNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var imagePath in packageImagePaths)
            {
                if (!PackageImageLoader.TryLoadManifest(
                        imagePath,
                        out var packageManifest,
                        out var loadDiagnostics,
                        out var observedFormatVersion))
                {
                    error = $"Stage package image '{imagePath}' is invalid: {FormatDiagnostics(loadDiagnostics)}";
                    return false;
                }

                var validationDiagnostics = PackageImageLoader.ValidateManifest(packageManifest, imagePath);
                if (validationDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    error = $"Stage package image '{imagePath}' is invalid: {FormatDiagnostics(validationDiagnostics)}";
                    return false;
                }

                if (observedFormatVersion is null)
                {
                    error = $"Stage package image '{imagePath}' does not contain a binary package format version.";
                    return false;
                }

                if (packageFormatVersion is null)
                {
                    packageFormatVersion = observedFormatVersion;
                }
                else if (packageFormatVersion != observedFormatVersion)
                {
                    error = $"Stage package image '{imagePath}' uses format version {observedFormatVersion}, but the stage already contains format version {packageFormatVersion}.";
                    return false;
                }

                if (packageManifest.Identity is not { } identity)
                {
                    error = $"Stage package image '{imagePath}' does not contain package identity and dependency hashes.";
                    return false;
                }

                if (!packageIds.Add(identity.PackageId))
                {
                    error = $"Stage package ID '{identity.PackageId}' is declared by more than one package image.";
                    return false;
                }

                if (packageManifest.BuildProfile?.Name is not { Length: > 0 } profile
                    || profile is not ("dev" or "release"))
                {
                    error = $"Stage package '{identity.PackageId}' does not contain a supported dev/release build profile.";
                    return false;
                }

                if (packageProfile is null)
                {
                    packageProfile = profile;
                }
                else if (!string.Equals(packageProfile, profile, StringComparison.Ordinal))
                {
                    error = $"Stage package '{identity.PackageId}' uses profile '{profile}', but the stage already contains profile '{packageProfile}'.";
                    return false;
                }

                if (target is null)
                {
                    if (!SdkTargetCompatibility.TryCreateDescriptorFromPackageTarget(
                            CreateTargetId(stageName, packageManifest.Target),
                            packageManifest.Target,
                            out target,
                            out error))
                    {
                        error = $"Could not derive the stage SDK target from '{imagePath}': {error}";
                        return false;
                    }
                }

                var compatibility = SdkTargetCompatibility.ValidatePackageTarget(
                    target,
                    packageManifest.Target,
                    identity.PackageId,
                    imagePath);
                if (!compatibility.IsCompatible)
                {
                    error = $"Stage package '{identity.PackageId}' is incompatible with the stage target: {string.Join("; ", compatibility.Diagnostics.Select(static diagnostic => diagnostic.Message))}";
                    return false;
                }

                var packageDirectory = Path.GetDirectoryName(imagePath) ?? canonicalRoot;
                if (!TryResolveContainedPath(
                        canonicalRoot,
                        packageDirectory,
                        packageManifest.LibraryFileName,
                        requireDirectory: false,
                        out var libraryPath,
                        out error))
                {
                    error = $"Stage package '{identity.PackageId}' library is invalid: {error}";
                    return false;
                }

                if (!TryResolveNativeFiles(
                        canonicalRoot,
                        packageDirectory,
                        packageManifest.NativeDependencies?.Sources,
                        "source",
                        out var nativeArtifacts,
                        out error)
                    || !TryResolveNativeDirectories(
                        canonicalRoot,
                        packageDirectory,
                        packageManifest.NativeDependencies?.IncludeDirectories,
                        "include directory",
                        out var includeDirectories,
                        out error)
                    || !TryResolveNativeDirectories(
                        canonicalRoot,
                        packageDirectory,
                        packageManifest.NativeDependencies?.LibraryDirectories,
                        "library directory",
                        out var libraryDirectories,
                        out error))
                {
                    error = $"Stage package '{identity.PackageId}' native metadata is invalid: {error}";
                    return false;
                }

                foreach (var module in packageManifest.Modules.OrderBy(static module => module.ModuleName, StringComparer.Ordinal))
                {
                    if (!moduleNames.Add(module.ModuleName))
                    {
                        error = $"Stage module '{module.ModuleName}' is owned by more than one package image.";
                        return false;
                    }

                    modules.Add(new StageModuleDocument(module.ModuleName, identity.PackageId));
                }

                packages.Add(new StagePackageDocument(
                    identity.PackageId,
                    stageName.Trim(),
                    profile,
                    ToRelativePath(canonicalRoot, imagePath),
                    ToRelativePath(canonicalRoot, libraryPath),
                    identity.ApiHash,
                    identity.ContentHash,
                    Sha256(imagePath),
                    Sha256(libraryPath),
                    identity.Dependencies
                        .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal)
                        .Select(static dependency => new StageDependencyDocument(
                            dependency.PackageId,
                            dependency.ApiHash,
                            dependency.ContentHash))
                        .ToArray(),
                    new StageNativeDocument(
                        Artifacts: nativeArtifacts,
                        IncludeDirectories: includeDirectories,
                        LibraryDirectories: libraryDirectories,
                        RuntimeFiles: [],
                        LicenseFiles: [],
                        FileChecksums: nativeArtifacts
                            .Select(path => new StageNativeChecksumDocument(
                                path,
                                Sha256(Path.Combine(
                                    canonicalRoot,
                                    path.Replace('/', Path.DirectorySeparatorChar)))))
                            .ToArray(),
                        Libraries: NormalizeStrings(packageManifest.NativeDependencies?.Libraries),
                        LinkArguments: NormalizeStrings(packageManifest.NativeDependencies?.LinkArguments))));
            }

            var document = new StageSdkDocument(
                SdkManifestLoader.SupportedSchemaVersion,
                "stage",
                stageName.Trim(),
                SdkCompilerCompatibility.SupportedLine,
                checked((int)packageFormatVersion!.Value),
                ToDocument(target!),
                modules.OrderBy(static module => module.Name, StringComparer.Ordinal).ToArray(),
                packages.OrderBy(static package => package.Id, StringComparer.Ordinal).ToArray(),
                DevelopmentSourceRoots: []);
            var json = JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
            var parsed = SdkManifestLoader.Parse(json, canonicalRoot);
            if (!parsed.Succeeded || parsed.Manifest?.Kind != SdkDistributionKind.Stage)
            {
                error = $"Generated stage SDK manifest is invalid: {string.Join("; ", parsed.Diagnostics.Select(static diagnostic => diagnostic.Message))}";
                return false;
            }

            manifestPath = Path.Combine(canonicalRoot, SdkRootResolver.ManifestFileName);
            if (File.Exists(manifestPath)
                && string.Equals(File.ReadAllText(manifestPath), json, StringComparison.Ordinal))
            {
                return true;
            }

            var temporaryPath = $"{manifestPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(
                temporaryPath,
                json,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, manifestPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> EnumeratePackageImages(string sdkRoot)
    {
        return new[] { "stdlib", "vendor" }
            .Select(root => Path.Combine(sdkRoot, root))
            .Where(Directory.Exists)
            .SelectMany(static root => Directory.EnumerateFiles(
                root,
                $"*{PackageImageBinaryFormat.FileExtension}",
                SearchOption.AllDirectories))
            .Where(static path => PackageImageBinaryFormat.HasBinaryFileName(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryResolveNativeDirectories(
        string sdkRoot,
        string packageDirectory,
        IReadOnlyList<string>? values,
        string label,
        out IReadOnlyList<string> resolved,
        out string error)
    {
        var paths = new List<string>();
        foreach (var value in values ?? [])
        {
            if (!TryResolveContainedPath(
                    sdkRoot,
                    packageDirectory,
                    value,
                    requireDirectory: true,
                    out var path,
                    out error))
            {
                resolved = [];
                error = $"{label} '{value}': {error}";
                return false;
            }

            paths.Add(ToRelativePath(sdkRoot, path));
        }

        // Native search-directory order is a linker/compiler input. Preserve
        // it byte-for-byte rather than normalizing it as set-like metadata.
        resolved = paths.ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryResolveNativeFiles(
        string sdkRoot,
        string packageDirectory,
        IReadOnlyList<string>? values,
        string label,
        out IReadOnlyList<string> resolved,
        out string error)
    {
        var paths = new List<string>();
        foreach (var value in values ?? [])
        {
            if (!TryResolveContainedPath(
                    sdkRoot,
                    packageDirectory,
                    value,
                    requireDirectory: false,
                    out var path,
                    out error))
            {
                resolved = [];
                error = $"{label} '{value}': {error}";
                return false;
            }

            paths.Add(ToRelativePath(sdkRoot, path));
        }

        resolved = paths.ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryResolveContainedPath(
        string sdkRoot,
        string packageDirectory,
        string value,
        bool requireDirectory,
        out string path,
        out string error)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "path must not be empty.";
            return false;
        }

        path = Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(packageDirectory, value));
        var relative = Path.GetRelativePath(sdkRoot, path);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            error = $"resolved path '{path}' is outside stage SDK root '{sdkRoot}'.";
            return false;
        }

        var exists = requireDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists)
        {
            error = $"resolved {(requireDirectory ? "directory" : "file")} '{path}' does not exist.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string CreateTargetId(string stageName, StarkPackageTargetManifest? target)
    {
        var normalizedTriple = string.IsNullOrWhiteSpace(target?.Triple)
            ? "unresolved"
            : new string(target.Triple
                .Trim()
                .ToLowerInvariant()
                .Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-')
                .ToArray());
        return $"{stageName.Trim()}-{normalizedTriple}";
    }

    private static StageTargetDocument ToDocument(SdkTargetDescriptor target) => new(
        target.Id,
        target.LlvmTriple,
        target.Architecture,
        target.OperatingSystem,
        target.Abi,
        target.PointerBitWidth,
        target.Endianness == SdkEndianness.Little ? "little" : "big",
        target.DataLayout,
        target.BaselineCpu,
        target.BaselineFeatures,
        target.RelocationModel,
        target.CodeModel,
        target.CDataModel,
        target.MinimumOperatingSystemVersion);

    private static IReadOnlyList<string> NormalizeStrings(IReadOnlyList<string>? values) =>
        (values ?? [])
            .Select(static value => value.Trim())
            .ToArray();

    private static string ToRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FormatDiagnostics(IReadOnlyList<CompilerDiagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "the package image could not be decoded"
            : string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Message));

    private sealed record StageSdkDocument(
        int SchemaVersion,
        string Kind,
        string SdkVersion,
        string CompilerCompatibility,
        int PackageFormatVersion,
        StageTargetDocument Target,
        IReadOnlyList<StageModuleDocument> Modules,
        IReadOnlyList<StagePackageDocument> Packages,
        IReadOnlyList<string> DevelopmentSourceRoots);

    private sealed record StageTargetDocument(
        string Id,
        string LlvmTriple,
        string Architecture,
        string OperatingSystem,
        string Abi,
        int PointerBitWidth,
        string Endianness,
        string? DataLayout,
        string? BaselineCpu,
        IReadOnlyList<string> BaselineFeatures,
        string RelocationModel,
        string? CodeModel,
        string? CDataModel,
        string? MinimumOperatingSystemVersion);

    private sealed record StageModuleDocument(string Name, string Package);

    private sealed record StagePackageDocument(
        string Id,
        string Version,
        string Profile,
        string Image,
        string Library,
        string ApiHash,
        string ContentHash,
        string ImageSha256,
        string LibrarySha256,
        IReadOnlyList<StageDependencyDocument> Dependencies,
        StageNativeDocument Native);

    private sealed record StageDependencyDocument(
        string Id,
        string ApiHash,
        string ContentHash);

    private sealed record StageNativeDocument(
        IReadOnlyList<string> Artifacts,
        IReadOnlyList<string> IncludeDirectories,
        IReadOnlyList<string> LibraryDirectories,
        IReadOnlyList<string> RuntimeFiles,
        IReadOnlyList<string> LicenseFiles,
        IReadOnlyList<StageNativeChecksumDocument> FileChecksums,
        IReadOnlyList<string> Libraries,
        IReadOnlyList<string> LinkArguments);

    private sealed record StageNativeChecksumDocument(string Path, string Sha256);
}
