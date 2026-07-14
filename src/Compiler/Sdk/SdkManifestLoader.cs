using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

internal static class SdkManifestLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static SdkManifestLoadResult Load(SdkRootResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (!SdkManifestPathValidator.TryResolvePath(
                resolution.RootPath,
                SdkRootResolver.ManifestFileName,
                out var manifestPath,
                out var manifestPathError))
        {
            return Failure(
                resolution.RootPath,
                resolution.ManifestPath,
                new SdkDiagnostic(
                    "STK7400",
                    $"SDK manifest path could not be resolved safely: {manifestPathError}",
                    resolution.ManifestPath));
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return Parse(json, resolution.RootPath, manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                resolution.RootPath,
                manifestPath,
                new SdkDiagnostic(
                    "STK7400",
                    $"SDK manifest could not be read: {exception.Message}",
                    manifestPath));
        }
    }

    public static SdkManifestLoadResult Load(string sdkRoot)
    {
        var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
        return Load(new SdkRootResolution(
            canonicalRoot,
            Path.Combine(canonicalRoot, SdkRootResolver.ManifestFileName),
            SdkRootOrigin.Explicit,
            ExecutablePath: null));
    }

    public static SdkManifestLoadResult Parse(
        string json,
        string sdkRoot,
        string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
        manifestPath ??= Path.Combine(canonicalRoot, SdkRootResolver.ManifestFileName);
        manifestPath = Path.GetFullPath(manifestPath);

        SdkManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SdkManifestDocument>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return Failure(
                canonicalRoot,
                manifestPath,
                new SdkDiagnostic(
                    "STK7401",
                    $"SDK manifest JSON is malformed: {exception.Message}",
                    manifestPath));
        }

        if (document is null)
        {
            return Failure(
                canonicalRoot,
                manifestPath,
                new SdkDiagnostic("STK7401", "SDK manifest JSON must contain an object.", manifestPath));
        }

        var diagnostics = new List<SdkDiagnostic>();
        ValidateHeader(document, manifestPath, diagnostics, out var kind);
        var target = BuildTarget(document.Target, manifestPath, diagnostics);
        var packages = BuildPackages(document.Packages, kind, canonicalRoot, manifestPath, diagnostics);
        var modules = BuildModules(document.Modules, kind, manifestPath, diagnostics);
        var developmentSourceRoots = BuildDevelopmentSourceRoots(
            document.DevelopmentSourceRoots,
            kind,
            canonicalRoot,
            manifestPath,
            diagnostics);

        ValidateReferences(packages, modules, manifestPath, diagnostics);
        if (diagnostics.Count != 0 || target is null || kind is null)
        {
            return new SdkManifestLoadResult(
                canonicalRoot,
                manifestPath,
                Manifest: null,
                PackageIndex: null,
                diagnostics.ToArray());
        }

        var manifest = new SdkManifest(
            document.SchemaVersion!.Value,
            kind.Value,
            document.SdkVersion!.Trim(),
            document.CompilerCompatibility!.Trim(),
            document.PackageFormatVersion!.Value,
            target,
            modules,
            packages,
            developmentSourceRoots);
        var packageIndex = new SdkPackageIndex(
            canonicalRoot,
            packages,
            modules,
            manifest.PackageFormatVersion);
        return new SdkManifestLoadResult(
            canonicalRoot,
            manifestPath,
            manifest,
            packageIndex,
            Array.Empty<SdkDiagnostic>());
    }

    private static void ValidateHeader(
        SdkManifestDocument document,
        string manifestPath,
        List<SdkDiagnostic> diagnostics,
        out SdkDistributionKind? kind)
    {
        kind = null;
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7402",
                $"Unsupported SDK manifest schema version {document.SchemaVersion?.ToString() ?? "<missing>"}; expected {SupportedSchemaVersion}.",
                manifestPath));
        }

        kind = document.Kind?.Trim() switch
        {
            "release" => SdkDistributionKind.Release,
            "development" => SdkDistributionKind.Development,
            "stage" => SdkDistributionKind.Stage,
            _ => null
        };
        if (kind is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7403",
                "SDK distribution kind must be 'release', 'development', or 'stage'.",
                manifestPath));
        }

        ValidateRequiredText(document.SdkVersion, "SDK version", "STK7404", manifestPath, diagnostics);
        ValidateCompilerCompatibility(document.CompilerCompatibility, manifestPath, diagnostics);
        if (document.PackageFormatVersion is null or <= 0)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7406",
                "SDK package format version must be a positive integer.",
                manifestPath));
        }
        else if (!PackageImageBinaryFormat.IsSupportedFormatVersion(document.PackageFormatVersion.Value))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7409",
                $"SDK package format version {document.PackageFormatVersion.Value} is not supported; expected {PackageImageBinaryFormat.LegacyFormatVersion} or {PackageImageBinaryFormat.CurrentFormatVersion}.",
                manifestPath));
        }
    }

    private static void ValidateCompilerCompatibility(
        string? compilerCompatibility,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(compilerCompatibility))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7405",
                $"SDK compiler compatibility is required; expected exact compatibility line '{SdkCompilerCompatibility.SupportedLine}'.",
                manifestPath));
            return;
        }

        if (!string.Equals(
                compilerCompatibility,
                SdkCompilerCompatibility.SupportedLine,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7405",
                $"SDK compiler compatibility '{compilerCompatibility}' is not supported by this compiler; expected exact compatibility line '{SdkCompilerCompatibility.SupportedLine}'.",
                manifestPath));
        }
    }

    private static SdkTargetDescriptor? BuildTarget(
        SdkTargetDocument? target,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (target is null)
        {
            diagnostics.Add(new SdkDiagnostic("STK7407", "SDK target descriptor is required.", manifestPath));
            return null;
        }

        ValidateRequiredText(target.Id, "target ID", "STK7408", manifestPath, diagnostics);
        ValidateRequiredText(target.LlvmTriple, "target LLVM triple", "STK7408", manifestPath, diagnostics);
        ValidateRequiredText(target.Architecture, "target architecture", "STK7408", manifestPath, diagnostics);
        ValidateRequiredText(target.OperatingSystem, "target operating system", "STK7408", manifestPath, diagnostics);
        ValidateRequiredText(target.Abi, "target ABI", "STK7408", manifestPath, diagnostics);
        ValidateRequiredText(target.RelocationModel, "target relocation model", "STK7408", manifestPath, diagnostics);
        if (target.PointerBitWidth is null or <= 0)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7408",
                "SDK target pointer bit width must be a positive integer.",
                manifestPath));
        }

        var endianness = target.Endianness?.Trim() switch
        {
            "little" => SdkEndianness.Little,
            "big" => SdkEndianness.Big,
            _ => (SdkEndianness?)null
        };
        if (endianness is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7408",
                "SDK target endianness must be 'little' or 'big'.",
                manifestPath));
        }

        if (!TargetFeatureFacts.TryNormalizeDistinct(
                target.BaselineFeatures,
                out var baselineFeatures,
                out var featureError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7408",
                $"SDK target baseline features are invalid: {featureError}.",
                manifestPath));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Code == "STK7408"))
        {
            return null;
        }

        return new SdkTargetDescriptor(
            target.Id!.Trim(),
            target.LlvmTriple!.Trim(),
            target.Architecture!.Trim(),
            target.OperatingSystem!.Trim(),
            target.Abi!.Trim(),
            target.PointerBitWidth!.Value,
            endianness!.Value,
            NormalizeOptionalText(target.DataLayout),
            NormalizeOptionalText(target.BaselineCpu),
            baselineFeatures,
            target.RelocationModel!.Trim(),
            NormalizeOptionalText(target.CodeModel),
            NormalizeOptionalText(target.CDataModel),
            NormalizeOptionalText(target.MinimumOperatingSystemVersion));
    }

    private static IReadOnlyList<SdkPackageDescriptor> BuildPackages(
        IReadOnlyList<SdkPackageDocument>? packageDocuments,
        SdkDistributionKind? kind,
        string sdkRoot,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (packageDocuments is not { Count: > 0 })
        {
            // A repository development SDK can be source-only. Release and
            // stage SDKs remain binary contracts and must advertise packages.
            if (kind != SdkDistributionKind.Development)
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7420",
                    "SDK manifest must declare at least one package.",
                    manifestPath));
            }

            return Array.Empty<SdkPackageDescriptor>();
        }

        var packages = new List<SdkPackageDescriptor>(packageDocuments.Count);
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in packageDocuments)
        {
            var id = document.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                diagnostics.Add(new SdkDiagnostic("STK7421", "SDK package ID must not be empty.", manifestPath));
                continue;
            }

            if (!packageIds.Add(id))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7422",
                    $"SDK package '{id}' is declared more than once.",
                    manifestPath));
                continue;
            }

            ValidateRequiredText(document.Version, $"package '{id}' version", "STK7421", manifestPath, diagnostics);
            ValidateRequiredText(document.Profile, $"package '{id}' profile", "STK7421", manifestPath, diagnostics);
            var profile = document.Profile?.Trim();
            if (!string.IsNullOrWhiteSpace(profile)
                && !string.Equals(profile, "dev", StringComparison.Ordinal)
                && !string.Equals(profile, "release", StringComparison.Ordinal))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7429",
                    $"SDK package '{id}' profile '{profile}' is not supported. Expected dev or release.",
                    manifestPath));
            }
            else if (kind == SdkDistributionKind.Release
                     && !string.IsNullOrWhiteSpace(profile)
                     && !string.Equals(profile, "release", StringComparison.Ordinal))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7429",
                    $"Release SDK package '{id}' must declare profile 'release', not '{profile}'.",
                    manifestPath));
            }
            SdkManifestPathValidator.TryValidate(
                sdkRoot,
                document.Image,
                $"package '{id}' image",
                diagnostics,
                manifestPath);
            if (document.Library is not null)
            {
                SdkManifestPathValidator.TryValidate(
                    sdkRoot,
                    document.Library,
                    $"package '{id}' library",
                    diagnostics,
                    manifestPath);
            }

            var imageSha256 = NormalizeSha256(
                document.ImageSha256,
                $"package '{id}' image",
                manifestPath,
                diagnostics);
            var librarySha256 = NormalizeSha256(
                document.LibrarySha256,
                $"package '{id}' library",
                manifestPath,
                diagnostics);
            if (kind == SdkDistributionKind.Release)
            {
                RequireReleaseChecksum(imageSha256, $"package '{id}' image", manifestPath, diagnostics);
                RequireReleaseChecksum(librarySha256, $"package '{id}' library", manifestPath, diagnostics);
            }

            var apiHash = NormalizeSha256(
                document.ApiHash,
                $"package '{id}' API identity",
                manifestPath,
                diagnostics);
            var contentHash = NormalizeSha256(
                document.ContentHash,
                $"package '{id}' content identity",
                manifestPath,
                diagnostics);
            if ((apiHash is null) != (contentHash is null))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7426",
                    $"SDK package '{id}' must declare API and content identity hashes together.",
                    manifestPath));
            }

            var dependencies = BuildDependencies(document.Dependencies, id, manifestPath, diagnostics);
            var native = BuildNative(document.Native, id, kind, sdkRoot, manifestPath, diagnostics);
            packages.Add(new SdkPackageDescriptor(
                id,
                document.Version?.Trim() ?? string.Empty,
                profile ?? string.Empty,
                document.Image?.Trim() ?? string.Empty,
                NormalizeOptionalText(document.Library),
                apiHash,
                contentHash,
                imageSha256,
                librarySha256,
                dependencies,
                native));
        }

        return packages.OrderBy(static package => package.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<SdkPackageDependency> BuildDependencies(
        IReadOnlyList<SdkPackageDependencyDocument>? documents,
        string ownerId,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var dependencies = new List<SdkPackageDependency>();
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents ?? [])
        {
            var id = document.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7423",
                    $"SDK package '{ownerId}' has a dependency with an empty package ID.",
                    manifestPath));
                continue;
            }

            if (!dependencyIds.Add(id))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7424",
                    $"SDK package '{ownerId}' declares dependency '{id}' more than once.",
                    manifestPath));
                continue;
            }

            var apiHash = NormalizeSha256(
                document.ApiHash,
                $"package '{ownerId}' dependency '{id}' API identity",
                manifestPath,
                diagnostics);
            var contentHash = NormalizeSha256(
                document.ContentHash,
                $"package '{ownerId}' dependency '{id}' content identity",
                manifestPath,
                diagnostics);
            if ((apiHash is null) != (contentHash is null))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7426",
                    $"SDK package '{ownerId}' dependency '{id}' must declare API and content identity hashes together.",
                    manifestPath));
            }

            dependencies.Add(new SdkPackageDependency(id, apiHash, contentHash));
        }

        return dependencies.OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal).ToArray();
    }

    private static SdkNativePackageDescriptor BuildNative(
        SdkNativePackageDocument? document,
        string packageId,
        SdkDistributionKind? kind,
        string sdkRoot,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        document ??= new SdkNativePackageDocument();
        var artifacts = NormalizeAndValidatePaths(document.Artifacts, "native artifact", packageId, sdkRoot, manifestPath, diagnostics);
        var includes = NormalizeAndValidatePaths(document.IncludeDirectories, "native include directory", packageId, sdkRoot, manifestPath, diagnostics);
        var libraryDirectories = NormalizeAndValidatePaths(document.LibraryDirectories, "native library directory", packageId, sdkRoot, manifestPath, diagnostics);
        var runtimeFiles = NormalizeAndValidatePaths(document.RuntimeFiles, "native runtime file", packageId, sdkRoot, manifestPath, diagnostics);
        var licenseFiles = NormalizeAndValidatePaths(document.LicenseFiles, "native license file", packageId, sdkRoot, manifestPath, diagnostics);
        var checksumPaths = artifacts
            .Concat(runtimeFiles)
            .Concat(licenseFiles)
            .ToHashSet(StringComparer.Ordinal);
        var fileChecksums = BuildNativeFileChecksums(
            document.FileChecksums,
            checksumPaths,
            packageId,
            kind,
            sdkRoot,
            manifestPath,
            diagnostics);
        return new SdkNativePackageDescriptor(
            artifacts,
            includes,
            libraryDirectories,
            runtimeFiles,
            licenseFiles,
            fileChecksums,
            NormalizeNonEmptyStrings(document.Libraries, $"package '{packageId}' native library", manifestPath, diagnostics),
            NormalizeNonEmptyStrings(document.LinkArguments, $"package '{packageId}' native link argument", manifestPath, diagnostics));
    }

    private static IReadOnlyList<SdkNativeFileChecksum> BuildNativeFileChecksums(
        IReadOnlyList<SdkNativeFileChecksumDocument>? documents,
        IReadOnlySet<string> checksumPaths,
        string packageId,
        SdkDistributionKind? kind,
        string sdkRoot,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var checksums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents ?? [])
        {
            if (!SdkManifestPathValidator.TryValidate(
                    sdkRoot,
                    document.Path,
                    $"package '{packageId}' native checksum",
                    diagnostics,
                    manifestPath))
            {
                continue;
            }

            var path = document.Path!.Trim();
            var isDuplicate = !declaredPaths.Add(path);
            if (isDuplicate)
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7428",
                    $"SDK package '{packageId}' declares more than one checksum for native file '{path}'.",
                    manifestPath));
            }

            if (document.Sha256 is null)
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7426",
                    $"SDK package '{packageId}' native file '{path}' SHA-256 must contain exactly 64 hexadecimal characters.",
                    manifestPath));
                continue;
            }

            var sha256 = NormalizeSha256(
                document.Sha256,
                $"package '{packageId}' native file '{path}'",
                manifestPath,
                diagnostics);
            if (sha256 is null)
            {
                continue;
            }

            if (!isDuplicate)
            {
                checksums.Add(path, sha256);
            }
        }

        foreach (var path in checksums.Keys.Where(path => !checksumPaths.Contains(path)).ToArray())
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7428",
                $"SDK package '{packageId}' native checksum path '{path}' is not declared as an artifact, runtime file, or license file.",
                manifestPath));
        }

        if (kind == SdkDistributionKind.Release)
        {
            foreach (var path in checksumPaths.Where(path => !checksums.ContainsKey(path)).Order(StringComparer.Ordinal))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7427",
                    $"Release SDK package '{packageId}' native file '{path}' requires a SHA-256 checksum.",
                    manifestPath));
            }
        }

        return checksums
            .Select(static pair => new SdkNativeFileChecksum(pair.Key, pair.Value))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeAndValidatePaths(
        IReadOnlyList<string>? paths,
        string label,
        string packageId,
        string sdkRoot,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var normalized = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (SdkManifestPathValidator.TryValidate(
                    sdkRoot,
                    path,
                    $"package '{packageId}' {label}",
                    diagnostics,
                    manifestPath))
            {
                normalized.Add(path.Trim());
            }
        }

        return normalized.ToArray();
    }

    private static IReadOnlyList<SdkModuleOwnership> BuildModules(
        IReadOnlyList<SdkModuleDocument>? moduleDocuments,
        SdkDistributionKind? kind,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (moduleDocuments is not { Count: > 0 })
        {
            // Development manifests may delegate all official modules to
            // explicitly declared source roots. Binary SDK kinds may not.
            if (kind != SdkDistributionKind.Development)
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7430",
                    "SDK manifest must declare at least one module owner.",
                    manifestPath));
            }

            return Array.Empty<SdkModuleOwnership>();
        }

        var modules = new List<SdkModuleOwnership>(moduleDocuments.Count);
        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in moduleDocuments)
        {
            var moduleName = document.Name?.Trim();
            var packageId = document.Package?.Trim();
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(packageId))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7431",
                    "SDK module entries require non-empty 'name' and 'package' values.",
                    manifestPath));
                continue;
            }

            if (!moduleNames.Add(moduleName))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7432",
                    $"SDK module '{moduleName}' has more than one owner.",
                    manifestPath));
                continue;
            }

            modules.Add(new SdkModuleOwnership(moduleName, packageId));
        }

        return modules.OrderBy(static module => module.ModuleName, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> BuildDevelopmentSourceRoots(
        IReadOnlyList<string>? roots,
        SdkDistributionKind? kind,
        string sdkRoot,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (roots is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        if (kind != SdkDistributionKind.Development)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7440",
                "Only a development SDK may declare development source roots.",
                manifestPath));
        }

        var normalized = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            if (SdkManifestPathValidator.TryValidate(
                    sdkRoot,
                    root,
                    "development source root",
                    diagnostics,
                    manifestPath))
            {
                normalized.Add(root.Trim());
            }
        }

        return normalized.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateReferences(
        IReadOnlyList<SdkPackageDescriptor> packages,
        IReadOnlyList<SdkModuleOwnership> modules,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var packagesById = packages.ToDictionary(static package => package.Id, StringComparer.Ordinal);
        var packageIds = packagesById.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!packageIds.Contains(module.PackageId))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7450",
                    $"SDK module '{module.ModuleName}' refers to unknown package '{module.PackageId}'.",
                    manifestPath));
            }
        }

        foreach (var package in packages)
        {
            foreach (var dependency in package.Dependencies)
            {
                if (!packageIds.Contains(dependency.PackageId))
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7451",
                        $"SDK package '{package.Id}' refers to unknown dependency '{dependency.PackageId}'.",
                        manifestPath));
                }
                else if (string.Equals(package.Id, dependency.PackageId, StringComparison.Ordinal))
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7452",
                        $"SDK package '{package.Id}' cannot depend on itself.",
                        manifestPath));
                }
                else
                {
                    var referencedPackage = packagesById[dependency.PackageId];
                    if (dependency.ApiHash is not null
                        && (!string.Equals(dependency.ApiHash, referencedPackage.ApiHash, StringComparison.Ordinal)
                            || !string.Equals(dependency.ContentHash, referencedPackage.ContentHash, StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new SdkDiagnostic(
                            "STK7458",
                            $"SDK package '{package.Id}' dependency '{dependency.PackageId}' API/content identity does not match the referenced package descriptor.",
                            manifestPath));
                    }
                }
            }
        }

        ValidateDependencyCycles(packages, packageIds, manifestPath, diagnostics);
    }

    private static void ValidateDependencyCycles(
        IReadOnlyList<SdkPackageDescriptor> packages,
        IReadOnlySet<string> packageIds,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var packagesById = packages.ToDictionary(static package => package.Id, StringComparer.Ordinal);
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        var stack = new List<string>();
        var stackPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var package in packages.OrderBy(static package => package.Id, StringComparer.Ordinal))
        {
            if (!states.ContainsKey(package.Id))
            {
                Visit(package.Id);
            }
        }

        void Visit(string packageId)
        {
            states[packageId] = 1;
            stackPositions[packageId] = stack.Count;
            stack.Add(packageId);

            foreach (var dependencyId in packagesById[packageId].Dependencies
                         .Select(static dependency => dependency.PackageId)
                         .Where(dependencyId => packageIds.Contains(dependencyId)
                             && !string.Equals(packageId, dependencyId, StringComparison.Ordinal))
                         .Order(StringComparer.Ordinal))
            {
                if (!states.TryGetValue(dependencyId, out var state))
                {
                    Visit(dependencyId);
                    continue;
                }

                if (state != 1 || !stackPositions.TryGetValue(dependencyId, out var cycleStart))
                {
                    continue;
                }

                var canonicalCycle = CanonicalizeCycle(stack.Skip(cycleStart).ToArray());
                var cycleKey = string.Join("\0", canonicalCycle);
                if (reportedCycles.Add(cycleKey))
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7453",
                        $"SDK package dependency cycle detected: {string.Join(" -> ", canonicalCycle)} -> {canonicalCycle[0]}.",
                        manifestPath));
                }
            }

            stack.RemoveAt(stack.Count - 1);
            stackPositions.Remove(packageId);
            states[packageId] = 2;
        }
    }

    private static IReadOnlyList<string> CanonicalizeCycle(IReadOnlyList<string> cycle)
    {
        var firstIndex = 0;
        for (var index = 1; index < cycle.Count; index++)
        {
            if (string.CompareOrdinal(cycle[index], cycle[firstIndex]) < 0)
            {
                firstIndex = index;
            }
        }

        var canonical = new string[cycle.Count];
        for (var index = 0; index < cycle.Count; index++)
        {
            canonical[index] = cycle[(firstIndex + index) % cycle.Count];
        }

        return canonical;
    }

    private static IReadOnlyList<string> NormalizeNonEmptyStrings(
        IReadOnlyList<string>? values,
        string label,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        var normalized = new List<string>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostics.Add(new SdkDiagnostic("STK7425", $"SDK {label} must not be empty.", manifestPath));
                continue;
            }

            normalized.Add(value.Trim());
        }

        return normalized.ToArray();
    }

    private static void ValidateRequiredText(
        string? value,
        string label,
        string code,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(new SdkDiagnostic(code, $"SDK {label} must not be empty.", manifestPath));
        }
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSha256(
        string? value,
        string label,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();

        if (normalized.Length != 64 || normalized.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7426",
                $"SDK {label} SHA-256 must contain exactly 64 hexadecimal characters.",
                manifestPath));
        }

        return normalized.ToLowerInvariant();
    }

    private static void RequireReleaseChecksum(
        string? checksum,
        string label,
        string manifestPath,
        List<SdkDiagnostic> diagnostics)
    {
        if (checksum is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7427",
                $"Release SDK {label} requires a SHA-256 checksum.",
                manifestPath));
        }
    }

    private static SdkManifestLoadResult Failure(
        string sdkRoot,
        string manifestPath,
        params SdkDiagnostic[] diagnostics)
    {
        return new SdkManifestLoadResult(
            sdkRoot,
            manifestPath,
            Manifest: null,
            PackageIndex: null,
            diagnostics);
    }

    private sealed class SdkManifestDocument
    {
        public int? SchemaVersion { get; init; }
        public string? Kind { get; init; }
        public string? SdkVersion { get; init; }
        public string? CompilerCompatibility { get; init; }
        public int? PackageFormatVersion { get; init; }
        public SdkTargetDocument? Target { get; init; }
        public IReadOnlyList<SdkModuleDocument>? Modules { get; init; }
        public IReadOnlyList<SdkPackageDocument>? Packages { get; init; }
        public IReadOnlyList<string>? DevelopmentSourceRoots { get; init; }
    }

    private sealed class SdkTargetDocument
    {
        public string? Id { get; init; }
        public string? LlvmTriple { get; init; }
        public string? Architecture { get; init; }
        public string? OperatingSystem { get; init; }
        public string? Abi { get; init; }
        public int? PointerBitWidth { get; init; }
        public string? Endianness { get; init; }
        public string? DataLayout { get; init; }
        public string? BaselineCpu { get; init; }
        public IReadOnlyList<string>? BaselineFeatures { get; init; }
        public string? RelocationModel { get; init; }
        public string? CodeModel { get; init; }
        public string? CDataModel { get; init; }
        public string? MinimumOperatingSystemVersion { get; init; }
    }

    private sealed class SdkModuleDocument
    {
        public string? Name { get; init; }
        public string? Package { get; init; }
    }

    private sealed class SdkPackageDocument
    {
        public string? Id { get; init; }
        public string? Version { get; init; }
        public string? Profile { get; init; }
        public string? Image { get; init; }
        public string? Library { get; init; }
        public string? ApiHash { get; init; }
        public string? ContentHash { get; init; }
        public string? ImageSha256 { get; init; }
        public string? LibrarySha256 { get; init; }
        public IReadOnlyList<SdkPackageDependencyDocument>? Dependencies { get; init; }
        public SdkNativePackageDocument? Native { get; init; }
    }

    private sealed class SdkPackageDependencyDocument
    {
        public string? Id { get; init; }
        public string? ApiHash { get; init; }
        public string? ContentHash { get; init; }
    }

    private sealed class SdkNativePackageDocument
    {
        public IReadOnlyList<string>? Artifacts { get; init; }
        public IReadOnlyList<string>? IncludeDirectories { get; init; }
        public IReadOnlyList<string>? LibraryDirectories { get; init; }
        public IReadOnlyList<string>? RuntimeFiles { get; init; }
        public IReadOnlyList<string>? LicenseFiles { get; init; }
        public IReadOnlyList<SdkNativeFileChecksumDocument>? FileChecksums { get; init; }
        public IReadOnlyList<string>? Libraries { get; init; }
        public IReadOnlyList<string>? LinkArguments { get; init; }
    }

    private sealed class SdkNativeFileChecksumDocument
    {
        public string? Path { get; init; }
        public string? Sha256 { get; init; }
    }
}
