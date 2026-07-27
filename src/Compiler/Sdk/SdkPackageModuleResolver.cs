namespace Stark.Compiler;

internal sealed record SdkPackageModuleResolverLoadResult(
    SdkPackageModuleResolver? Resolver,
    IReadOnlyList<SdkDiagnostic> Diagnostics)
{
    public bool Succeeded => Resolver is not null && Diagnostics.Count == 0;
}

internal sealed class SdkPackageModuleResolver :
    IModuleSourceResolver,
    IModuleDocumentResolver,
    IModuleResolutionDiagnosticProvider
{
    private readonly SdkTargetDescriptor? _sdkTarget;
    private readonly IReadOnlyDictionary<string, Lazy<PackageLoadResult>> _packageLoads;

    private SdkPackageModuleResolver(
        SdkPackageIndex packageIndex,
        SdkTargetDescriptor? sdkTarget,
        IReadOnlyList<string> searchDirectories)
    {
        PackageIndex = packageIndex;
        _sdkTarget = sdkTarget;
        SearchDirectories = searchDirectories;
        _packageLoads = packageIndex.Packages.ToDictionary(
            static package => package.Id,
            package => new Lazy<PackageLoadResult>(
                () => LoadPackage(packageIndex, package, _sdkTarget),
                LazyThreadSafetyMode.ExecutionAndPublication),
            StringComparer.Ordinal);
    }

    public SdkPackageIndex PackageIndex { get; }

    public IReadOnlyList<string> SearchDirectories { get; }

    internal int MaterializedPackageCount => _packageLoads.Values.Count(static load => load.IsValueCreated);

    public static SdkPackageModuleResolverLoadResult Load(SdkManifestLoadResult manifestLoadResult)
    {
        var result = CreateLazy(manifestLoadResult);
        return ValidateAll(result);
    }

    /// <summary>
    /// Creates an indexed resolver without touching package images or native
    /// payloads. Package integrity is checked atomically the first time one of
    /// its advertised modules is requested. This keeps an unused optional
    /// vendor package from preventing an otherwise independent compilation.
    /// </summary>
    public static SdkPackageModuleResolverLoadResult CreateLazy(SdkManifestLoadResult manifestLoadResult)
    {
        ArgumentNullException.ThrowIfNull(manifestLoadResult);
        if (!manifestLoadResult.Succeeded || manifestLoadResult.PackageIndex is null)
        {
            return new SdkPackageModuleResolverLoadResult(
                Resolver: null,
                manifestLoadResult.Diagnostics.ToArray());
        }

        return CreateLazy(
            manifestLoadResult.PackageIndex,
            manifestLoadResult.Manifest?.Target);
    }

    public static SdkPackageModuleResolverLoadResult Load(SdkPackageIndex packageIndex)
    {
        var result = CreateLazy(packageIndex, sdkTarget: null);
        return ValidateAll(result);
    }

    private static SdkPackageModuleResolverLoadResult CreateLazy(
        SdkPackageIndex packageIndex,
        SdkTargetDescriptor? sdkTarget)
    {
        ArgumentNullException.ThrowIfNull(packageIndex);

        var searchDirectories = new SortedSet<string>(GetPathComparer());
        var diagnostics = new List<SdkDiagnostic>();
        foreach (var package in packageIndex.Packages)
        {
            if (!SdkManifestPathValidator.TryResolvePath(
                    packageIndex.SdkRoot,
                    package.ImagePath,
                    out var imagePath,
                    out var resolutionError))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7469",
                    $"SDK package '{package.Id}' image path '{package.ImagePath}' could not be resolved safely while activating the SDK: {resolutionError}",
                    packageIndex.SdkRoot));
                continue;
            }

            searchDirectories.Add(Path.GetDirectoryName(imagePath) ?? packageIndex.SdkRoot);
        }

        if (diagnostics.Count != 0)
        {
            return new SdkPackageModuleResolverLoadResult(null, diagnostics);
        }

        return new SdkPackageModuleResolverLoadResult(
            new SdkPackageModuleResolver(
                packageIndex,
                sdkTarget,
                searchDirectories.ToArray()),
            Array.Empty<SdkDiagnostic>());
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        if (TryGetIndexedPackageModule(moduleName, out var packageModule))
        {
            module = CreateReference(packageModule);
            return true;
        }

        module = default!;
        return false;
    }

    /// <summary>
    /// Forces every package in stable package-ID order. Doctor/release
    /// validation uses this path so distribution defects cannot remain hidden
    /// merely because a smoke input did not import the affected package.
    /// </summary>
    public IReadOnlyList<SdkDiagnostic> ValidateAllPackages()
    {
        var diagnostics = new List<SdkDiagnostic>();
        foreach (var package in PackageIndex.Packages)
        {
            diagnostics.AddRange(ValidatePackage(package.Id));
        }

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Forces one advertised package and returns its complete validation
    /// result. Doctor uses this package-scoped entry point so machine-readable
    /// reports can attribute every image, archive, target, and native-payload
    /// failure without parsing diagnostic prose.
    /// </summary>
    public IReadOnlyList<SdkDiagnostic> ValidatePackage(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (_packageLoads.TryGetValue(packageId, out var load))
        {
            return load.Value.Diagnostics;
        }

        return
        [
            new SdkDiagnostic(
                "STK7451",
                $"SDK package '{packageId}' is not present in the active package index.")
        ];
    }

    public bool TryGetValidatedPackageLibraryPath(
        string packageId,
        out string libraryPath,
        out IReadOnlyList<SdkDiagnostic> diagnostics)
    {
        if (!_packageLoads.TryGetValue(packageId, out var load))
        {
            libraryPath = string.Empty;
            diagnostics =
            [
                new SdkDiagnostic(
                    "STK7451",
                    $"SDK link plan refers to unknown package '{packageId}'.")
            ];
            return false;
        }

        var result = load.Value;
        diagnostics = result.Diagnostics;
        if (diagnostics.Count != 0 || result.Modules.Count == 0)
        {
            libraryPath = string.Empty;
            return false;
        }

        libraryPath = result.Modules.Values.First().LibraryPath;
        return true;
    }

    public bool TryGetUnresolvedModuleDiagnostic(
        string moduleName,
        out string code,
        out string message)
    {
        if (!PackageIndex.TryGetPackageForModule(moduleName, out var package, out _))
        {
            code = string.Empty;
            message = string.Empty;
            return false;
        }

        var diagnostics = _packageLoads[package.Id].Value.Diagnostics;
        if (diagnostics.Count == 0)
        {
            code = string.Empty;
            message = string.Empty;
            return false;
        }

        code = diagnostics[0].Code;
        message = FormatResolutionDiagnostic(diagnostics);
        return true;
    }

    public bool TryLoadModuleSource(
        ResolvedModuleReference module,
        out string sourceText,
        out string? filePath)
    {
        if (TryGetPackageModule(module, out var packageModule))
        {
            if (PackageImageLoader.TryBuildStructuredModuleDocument(packageModule, out var structuredDocument))
            {
                sourceText = structuredDocument.ParseResult.SourceText;
                filePath = packageModule.ManifestPath;
                return true;
            }

            if (PackageImageLoader.TryBuildModuleSource(packageModule, out sourceText))
            {
                filePath = packageModule.ManifestPath;
                return true;
            }
        }

        sourceText = string.Empty;
        filePath = module.ManifestPath;
        return false;
    }

    public bool TryLoadModuleDocument(
        ResolvedModuleReference module,
        LlvmTargetInfo? targetInfo,
        out LoadedModuleDocument document)
    {
        if (TryGetPackageModule(module, out var packageModule)
            && (PackageImageLoader.TryBuildStructuredModuleDocument(packageModule, out var packageDocument)
                || PackageImageLoader.TryBuildModuleDocument(packageModule, out packageDocument)))
        {
            document = packageDocument with
            {
                Reference = CreateReference(packageModule),
                TargetInfo = targetInfo
            };
            return true;
        }

        document = default!;
        return false;
    }

    private bool TryGetPackageModule(
        ResolvedModuleReference reference,
        out ResolvedPackageModule packageModule)
    {
        if (!TryGetIndexedPackageModule(reference.ModuleName, out packageModule))
        {
            return false;
        }

        if (reference.ManifestPath is null)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(reference.ManifestPath),
            packageModule.ManifestPath,
            comparison);
    }

    private bool TryGetIndexedPackageModule(
        string moduleName,
        out ResolvedPackageModule packageModule)
    {
        if (PackageIndex.TryGetPackageForModule(moduleName, out var package, out _)
            && _packageLoads[package.Id].Value.Modules.TryGetValue(moduleName, out packageModule!))
        {
            return true;
        }

        packageModule = default!;
        return false;
    }

    private static SdkPackageModuleResolverLoadResult ValidateAll(
        SdkPackageModuleResolverLoadResult result)
    {
        if (result.Resolver is null || result.Diagnostics.Count != 0)
        {
            return result;
        }

        var diagnostics = result.Resolver.ValidateAllPackages();
        return diagnostics.Count == 0
            ? result
            : new SdkPackageModuleResolverLoadResult(null, diagnostics);
    }

    private static PackageLoadResult LoadPackage(
        SdkPackageIndex packageIndex,
        SdkPackageDescriptor package,
        SdkTargetDescriptor? sdkTarget)
    {
        var diagnostics = new List<SdkDiagnostic>();
        SdkIntegrityValidator.ValidateNativePaths(packageIndex, package, diagnostics);
        if (!SdkManifestPathValidator.TryResolvePath(
                packageIndex.SdkRoot,
                package.ImagePath,
                out var imagePath,
                out var imageResolutionError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7469",
                $"SDK package '{package.Id}' image path '{package.ImagePath}' could not be resolved safely: {imageResolutionError}",
                packageIndex.SdkRoot));
            return PackageLoadResult.Failed(diagnostics);
        }

        if (!File.Exists(imagePath))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7460",
                $"SDK package '{package.Id}' image is missing: '{imagePath}'.",
                imagePath));
            return PackageLoadResult.Failed(diagnostics);
        }

        if (!SdkIntegrityValidator.ValidateFileChecksum(
                package.Id,
                "image",
                imagePath,
                package.ImageSha256,
                "STK7465",
                diagnostics))
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        var packageLoaded = PackageImageLoader.TryLoadManifest(
            imagePath,
            out var packageManifest,
            out var packageDiagnostics,
            out var binaryFormatVersion);
        if (binaryFormatVersion is { } observedFormatVersion
            && observedFormatVersion != (uint)packageIndex.PackageFormatVersion)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7456",
                $"SDK package '{package.Id}' image format version is {observedFormatVersion}, but sdk.json declares packageFormatVersion {packageIndex.PackageFormatVersion}.",
                imagePath));
            return PackageLoadResult.Failed(diagnostics);
        }

        if (!packageLoaded)
        {
            var detail = packageDiagnostics.Count == 0
                ? "the package image could not be decoded"
                : string.Join("; ", packageDiagnostics.Select(static diagnostic => diagnostic.Message));
            diagnostics.Add(new SdkDiagnostic(
                "STK7461",
                $"SDK package '{package.Id}' image is invalid: {detail}.",
                imagePath));
            return PackageLoadResult.Failed(diagnostics);
        }

        // Indexed resolution only needs the identity integrity check here. The package
        // loader validates each materialized module as it is consumed; rebuilding all
        // typed/compiler facts at package-selection time would defeat lazy SDK startup.
        var manifestDiagnostics = PackageImageLoader.ValidateManifestIdentity(packageManifest, imagePath);
        if (manifestDiagnostics.Count != 0)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7461",
                $"SDK package '{package.Id}' image manifest is invalid: {string.Join("; ", manifestDiagnostics.Select(static diagnostic => diagnostic.Message))}.",
                imagePath));
            return PackageLoadResult.Failed(diagnostics);
        }

        ValidatePackageIdentity(
            packageIndex,
            package,
            packageManifest,
            binaryFormatVersion,
            imagePath,
            diagnostics);
        if (diagnostics.Count != 0)
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        if (sdkTarget is not null)
        {
            var compatibility = SdkTargetCompatibility.ValidatePackageTarget(
                sdkTarget,
                packageManifest.Target,
                package.Id,
                imagePath);
            if (!compatibility.IsCompatible)
            {
                diagnostics.AddRange(compatibility.Diagnostics);
                return PackageLoadResult.Failed(diagnostics);
            }
        }

        var indexedModules = packageIndex.Modules
            .Where(ownership => string.Equals(ownership.PackageId, package.Id, StringComparison.Ordinal))
            .Select(static ownership => ownership.ModuleName)
            .ToHashSet(StringComparer.Ordinal);
        var packageModules = packageManifest.Modules
            .Select(static module => module.ModuleName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var missingModule in indexedModules.Except(packageModules, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7462",
                $"SDK module index assigns '{missingModule}' to package '{package.Id}', but the package image does not contain that module.",
                imagePath));
        }

        foreach (var unindexedModule in packageModules.Except(indexedModules, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7463",
                $"SDK package '{package.Id}' contains module '{unindexedModule}', but sdk.json does not assign that module to the package.",
                imagePath));
        }

        if (!indexedModules.SetEquals(packageModules))
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        var libraryPath = ResolveLibraryPath(packageIndex, package, imagePath, packageManifest, diagnostics);
        if (libraryPath is null)
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        if (!File.Exists(libraryPath))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7466",
                $"SDK package '{package.Id}' library is missing or is not a file: '{libraryPath}'.",
                libraryPath));
            return PackageLoadResult.Failed(diagnostics);
        }

        if (!SdkIntegrityValidator.ValidateFileChecksum(
                package.Id,
                "library",
                libraryPath,
                package.LibrarySha256,
                "STK7467",
                diagnostics))
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        if (diagnostics.Count != 0)
        {
            return PackageLoadResult.Failed(diagnostics);
        }

        var modules = packageManifest.Modules
            .OrderBy(static module => module.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                static module => module.ModuleName,
                module => new ResolvedPackageModule(imagePath, libraryPath, packageManifest, module),
                StringComparer.Ordinal);
        return new PackageLoadResult(modules, Array.Empty<SdkDiagnostic>());
    }

    private static void ValidatePackageIdentity(
        SdkPackageIndex packageIndex,
        SdkPackageDescriptor package,
        StarkPackageManifest packageManifest,
        uint? binaryFormatVersion,
        string imagePath,
        List<SdkDiagnostic> diagnostics)
    {
        if (binaryFormatVersion is null)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7456",
                $"SDK package '{package.Id}' image is not a binary package image, so its format version cannot be verified against sdk.json packageFormatVersion {packageIndex.PackageFormatVersion}.",
                imagePath));
        }
        if (!string.Equals(package.Id, packageManifest.RootModule, StringComparison.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7454",
                $"SDK package descriptor ID '{package.Id}' does not match package image root module identity '{packageManifest.RootModule}'.",
                imagePath));
        }


        var descriptorCarriesIdentity = package.ApiHash is not null
            || package.ContentHash is not null
            || package.Dependencies.Any(static dependency =>
                dependency.ApiHash is not null || dependency.ContentHash is not null);
        if (descriptorCarriesIdentity)
        {
            if (packageManifest.Identity is not { } imageIdentity)
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7458",
                    $"SDK package '{package.Id}' descriptor contains API/content identity facts, but its package image does not.",
                    imagePath));
            }
            else
            {
                if (!string.Equals(package.Id, imageIdentity.PackageId, StringComparison.Ordinal)
                    || !string.Equals(package.ApiHash, imageIdentity.ApiHash, StringComparison.Ordinal)
                    || !string.Equals(package.ContentHash, imageIdentity.ContentHash, StringComparison.Ordinal))
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7458",
                        $"SDK package '{package.Id}' descriptor API/content identity does not match its package image.",
                        imagePath));
                }

                var descriptorDependencies = package.Dependencies
                    .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal)
                    .ToArray();
                var imageDependencies = (imageIdentity.Dependencies ?? [])
                    .OrderBy(static dependency => dependency.PackageId, StringComparer.Ordinal)
                    .ToArray();
                if (descriptorDependencies.Length != imageDependencies.Length
                    || descriptorDependencies.Where((dependency, index) =>
                        !string.Equals(dependency.PackageId, imageDependencies[index].PackageId, StringComparison.Ordinal)
                        || !string.Equals(dependency.ApiHash, imageDependencies[index].ApiHash, StringComparison.Ordinal)
                        || !string.Equals(dependency.ContentHash, imageDependencies[index].ContentHash, StringComparison.Ordinal)).Any())
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7459",
                        $"SDK package '{package.Id}' dependency identity graph does not match its package image.",
                        imagePath));
                }
            }
        }

        var imageProfile = packageManifest.BuildProfile?.Name;
        if (string.IsNullOrWhiteSpace(imageProfile))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7455",
                $"SDK package '{package.Id}' descriptor declares profile '{package.Profile}', but its package image does not contain build-profile facts.",
                imagePath));
        }
        else if (!string.Equals(package.Profile, imageProfile, StringComparison.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7455",
                $"SDK package '{package.Id}' descriptor profile '{package.Profile}' does not match package image build profile '{imageProfile}'.",
                imagePath));
        }

        if (package.LibraryPath is { } descriptorLibraryPath)
        {
            var descriptorLibraryFileName = descriptorLibraryPath[
                (descriptorLibraryPath.LastIndexOf('/') + 1)..];
            if (!string.Equals(
                    descriptorLibraryFileName,
                    packageManifest.LibraryFileName,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7457",
                    $"SDK package '{package.Id}' descriptor library path names '{descriptorLibraryFileName}', but its package image identifies library file '{packageManifest.LibraryFileName}'. The directory may be relocated, but the library file identity must match.",
                    imagePath));
            }
        }
    }

    private static string FormatResolutionDiagnostic(IReadOnlyList<SdkDiagnostic> diagnostics)
    {
        static string Format(SdkDiagnostic diagnostic)
        {
            var path = string.IsNullOrWhiteSpace(diagnostic.Path)
                ? string.Empty
                : $" ({diagnostic.Path})";
            return $"{diagnostic.Message}{path}";
        }

        if (diagnostics.Count == 1)
        {
            return Format(diagnostics[0]);
        }

        return Format(diagnostics[0])
            + " Additional SDK package validation failures: "
            + string.Join(
                "; ",
                diagnostics.Skip(1).Select(diagnostic => $"{diagnostic.Code}: {Format(diagnostic)}"));
    }

    private static ResolvedModuleReference CreateReference(ResolvedPackageModule module)
    {
        return new ResolvedModuleReference(
            module.Module.ModuleName,
            FilePath: module.ManifestPath,
            IsExternal: false,
            IsRoot: false,
            ManifestPath: module.ManifestPath,
            LibraryPath: module.LibraryPath,
            IsSdkPackage: true);
    }

    private static string? ResolveLibraryPath(
        SdkPackageIndex packageIndex,
        SdkPackageDescriptor package,
        string imagePath,
        StarkPackageManifest packageManifest,
        List<SdkDiagnostic> diagnostics)
    {
        try
        {
            if (package.LibraryPath is not null)
            {
                return packageIndex.GetPackageLibraryPath(package);
            }

            var imageRelativeDirectory = Path.GetRelativePath(
                packageIndex.SdkRoot,
                Path.GetDirectoryName(imagePath) ?? packageIndex.SdkRoot)
                .Replace(Path.DirectorySeparatorChar, '/');
            var derivedRelativePath = string.Equals(imageRelativeDirectory, ".", StringComparison.Ordinal)
                ? packageManifest.LibraryFileName.Replace(Path.DirectorySeparatorChar, '/')
                : $"{imageRelativeDirectory}/{packageManifest.LibraryFileName.Replace(Path.DirectorySeparatorChar, '/')}";
            return SdkManifestPathValidator.ResolvePath(packageIndex.SdkRoot, derivedRelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7464",
                $"SDK package '{package.Id}' library path from its package image is invalid: {exception.Message}",
                imagePath));
            return null;
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PackageLoadResult(
        IReadOnlyDictionary<string, ResolvedPackageModule> Modules,
        IReadOnlyList<SdkDiagnostic> Diagnostics)
    {
        public static PackageLoadResult Failed(IReadOnlyList<SdkDiagnostic> diagnostics) =>
            new(
                new Dictionary<string, ResolvedPackageModule>(StringComparer.Ordinal),
                diagnostics.ToArray());
    }
}
