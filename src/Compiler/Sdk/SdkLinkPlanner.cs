namespace Stark.Compiler;

internal sealed record SdkLinkPlan(
    IReadOnlyList<string> PackageLibraries,
    IReadOnlyList<string> PackageImages,
    IReadOnlyList<string> RuntimeFiles,
    IReadOnlyList<SdkDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.Count == 0;
}

internal sealed record SdkRuntimeFilePlan(
    IReadOnlyList<string> RuntimeFiles,
    IReadOnlyList<SdkDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.Count == 0;
}

/// <summary>
/// Turns the SDK packages reached by a compilation into a deterministic static
/// link plan. Static linkers consume archives left-to-right, so dependents must
/// precede their SDK dependencies (for example Vendor.Raylib before System).
/// Ordinary package archives remain ahead of SDK archives so their references
/// to official modules can be satisfied as well.
/// </summary>
internal static class SdkLinkPlanner
{
    public static SdkLinkPlan Create(
        ActiveSdkResolution activeSdk,
        CompilationResult compilation,
        IReadOnlyList<string> discoveredPackageLibraries)
    {
        ArgumentNullException.ThrowIfNull(activeSdk);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(discoveredPackageLibraries);

        if (!activeSdk.IsActive
            || activeSdk.Manifest?.PackageIndex is not { } packageIndex
            || activeSdk.PackageResolver is null
            || !compilation.Artifacts.TryGet(
                CompilerArtifactKeys.LoadedModules,
                out LoadedModuleSet? loadedModules)
            || loadedModules is null)
        {
            return new SdkLinkPlan(
                discoveredPackageLibraries.ToArray(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<SdkDiagnostic>());
        }

        var selectionDiagnostics = new List<SdkDiagnostic>();
        var selectedPackageIds = SelectSdkPackageIds(
            packageIndex,
            loadedModules,
            discoveredPackageLibraries,
            selectionDiagnostics);

        if (selectionDiagnostics.Count != 0)
        {
            return new SdkLinkPlan(
                discoveredPackageLibraries.ToArray(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                selectionDiagnostics);
        }

        if (selectedPackageIds.Count == 0)
        {
            return new SdkLinkPlan(
                discoveredPackageLibraries.ToArray(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<SdkDiagnostic>());
        }

        var orderedPackages = BuildDependentBeforeDependencyOrder(packageIndex, selectedPackageIds);
        var pathComparer = GetPathComparer();
        var diagnostics = new List<SdkDiagnostic>();
        var sdkLibraries = new List<string>();
        var sdkLibrarySet = new HashSet<string>(pathComparer);
        var sdkImages = new List<string>();
        var sdkImageSet = new HashSet<string>(pathComparer);

        foreach (var package in orderedPackages)
        {
            if (!activeSdk.PackageResolver.TryGetValidatedPackageLibraryPath(
                    package.Id,
                    out var libraryPath,
                    out var packageDiagnostics))
            {
                diagnostics.AddRange(packageDiagnostics);
                continue;
            }

            libraryPath = Path.GetFullPath(libraryPath);
            if (sdkLibrarySet.Add(libraryPath))
            {
                sdkLibraries.Add(libraryPath);
            }

            if (!SdkManifestPathValidator.TryResolvePath(
                    packageIndex.SdkRoot,
                    package.ImagePath,
                    out var imagePath,
                    out var imageResolutionError))
            {
                diagnostics.Add(new SdkDiagnostic(
                    "STK7469",
                    $"SDK package '{package.Id}' image path '{package.ImagePath}' could not be resolved safely while building the link plan: {imageResolutionError}",
                    packageIndex.SdkRoot));
                continue;
            }

            imagePath = Path.GetFullPath(imagePath);
            if (sdkImageSet.Add(imagePath))
            {
                sdkImages.Add(imagePath);
            }
        }

        if (diagnostics.Count != 0)
        {
            return new SdkLinkPlan(
                discoveredPackageLibraries.ToArray(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                diagnostics.ToArray());
        }

        var runtimeFilePlan = BuildRuntimeFilePlan(
            packageIndex,
            orderedPackages,
            activeSdk.Manifest.Manifest!.Target);
        if (!runtimeFilePlan.Succeeded)
        {
            return new SdkLinkPlan(
                discoveredPackageLibraries.ToArray(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                runtimeFilePlan.Diagnostics);
        }

        var ordinaryLibraries = discoveredPackageLibraries
            .Select(Path.GetFullPath)
            .Where(path => !sdkLibrarySet.Contains(path))
            .Distinct(pathComparer)
            .ToArray();
        return new SdkLinkPlan(
            ordinaryLibraries.Concat(sdkLibraries).ToArray(),
            sdkImages,
            runtimeFilePlan.RuntimeFiles,
            Array.Empty<SdkDiagnostic>());
    }

    internal static SdkRuntimeFilePlan BuildRuntimeFilePlan(
        SdkPackageIndex packageIndex,
        IReadOnlyList<SdkPackageDescriptor> orderedPackages,
        SdkTargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(packageIndex);
        ArgumentNullException.ThrowIfNull(orderedPackages);
        ArgumentNullException.ThrowIfNull(target);

        var sourcePathComparer = GetPathComparer();
        var destinationNameComparer = string.Equals(
            target.OperatingSystem,
            "windows",
            StringComparison.OrdinalIgnoreCase)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var runtimeFiles = new List<string>();
        var destinations = new Dictionary<string, RuntimeFileCandidate>(destinationNameComparer);
        var diagnostics = new List<SdkDiagnostic>();

        foreach (var package in orderedPackages)
        {
            var declaredChecksums = package.Native.FileChecksums.ToDictionary(
                static checksum => checksum.Path,
                static checksum => checksum.Sha256,
                StringComparer.Ordinal);
            foreach (var relativeRuntimePath in package.Native.RuntimeFiles)
            {
                if (!SdkManifestPathValidator.TryResolvePath(
                        packageIndex.SdkRoot,
                        relativeRuntimePath,
                        out var sourcePath,
                        out var resolutionError))
                {
                    diagnostics.Add(new SdkDiagnostic(
                        "STK7477",
                        $"SDK package '{package.Id}' runtime file path '{relativeRuntimePath}' could not be resolved safely while building the link plan: {resolutionError}",
                        packageIndex.SdkRoot));
                    continue;
                }

                var destinationName = Path.GetFileName(sourcePath);
                declaredChecksums.TryGetValue(relativeRuntimePath, out var declaredSha256);
                var candidate = new RuntimeFileCandidate(
                    package.Id,
                    relativeRuntimePath,
                    sourcePath,
                    declaredSha256);

                if (!destinations.TryGetValue(destinationName, out var existing))
                {
                    destinations.Add(destinationName, candidate);
                    runtimeFiles.Add(sourcePath);
                    continue;
                }

                if (sourcePathComparer.Equals(existing.SourcePath, sourcePath)
                    || (existing.DeclaredSha256 is not null
                        && declaredSha256 is not null
                        && string.Equals(
                            existing.DeclaredSha256,
                            declaredSha256,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    // Both candidates stage to the same destination bytes. Keep
                    // the first package-order occurrence so output is stable.
                    continue;
                }

                diagnostics.Add(new SdkDiagnostic(
                    "STK7476",
                    $"SDK packages '{existing.PackageId}' and '{package.Id}' declare distinct runtime files "
                    + $"that both stage as '{destinationName}', but they do not declare the same SHA-256 "
                    + $"('{existing.RelativePath}' = {FormatChecksum(existing.DeclaredSha256)}; "
                    + $"'{relativeRuntimePath}' = {FormatChecksum(declaredSha256)}). Runtime files are "
                    + "staged into one executable directory and may not overwrite different bytes.",
                    sourcePath));
            }
        }

        return new SdkRuntimeFilePlan(runtimeFiles, diagnostics);
    }

    internal static IReadOnlySet<string> SelectSdkPackageIds(
        SdkPackageIndex packageIndex,
        LoadedModuleSet loadedModules,
        IReadOnlyList<string> discoveredPackageLibraries)
    {
        return SelectSdkPackageIds(
            packageIndex,
            loadedModules,
            discoveredPackageLibraries,
            diagnostics: null);
    }

    private static IReadOnlySet<string> SelectSdkPackageIds(
        SdkPackageIndex packageIndex,
        LoadedModuleSet loadedModules,
        IReadOnlyList<string> discoveredPackageLibraries,
        List<SdkDiagnostic>? diagnostics)
    {
        var selectedPackageIds = new HashSet<string>(StringComparer.Ordinal);
        var pathComparer = GetPathComparer();

        // A module name alone is not provenance. In particular, a development
        // SDK may deliberately compile an official module from source, and the
        // source root must not accidentally pull the installed archive for the
        // same name into its link. Select package-backed documents only when
        // their manifest is the exact SDK-indexed image.
        foreach (var module in loadedModules.Modules.Values)
        {
            if (!module.IsPackageImageImport
                || module.Reference.ManifestPath is null
                || !packageIndex.TryGetPackageForModule(
                    module.SyntaxModel.ModuleName,
                    out var package,
                    out _))
            {
                continue;
            }

            if (!SdkManifestPathValidator.TryResolvePath(
                    packageIndex.SdkRoot,
                    package.ImagePath,
                    out var indexedImagePath,
                    out var imageResolutionError))
            {
                diagnostics?.Add(new SdkDiagnostic(
                    "STK7469",
                    $"SDK package '{package.Id}' image path '{package.ImagePath}' could not be resolved safely while building the link plan: {imageResolutionError}",
                    packageIndex.SdkRoot));
                continue;
            }

            if (pathComparer.Equals(
                    Path.GetFullPath(module.Reference.ManifestPath),
                    Path.GetFullPath(indexedImagePath)))
            {
                selectedPackageIds.Add(package.Id);
            }
        }

        // A package archive may be discovered before all of its module
        // documents are materialized. Treat an exact SDK library path as an
        // additional selection signal without decoding unrelated packages.
        var discoveredLibrarySet = discoveredPackageLibraries
            .Select(Path.GetFullPath)
            .ToHashSet(pathComparer);
        foreach (var package in packageIndex.Packages)
        {
            if (package.LibraryPath is null)
            {
                continue;
            }

            // Preserve lazy package validation. Comparing an SDK declaration
            // against the libraries already reached by compilation does not
            // itself use the artifact, so first perform the lexical comparison
            // guaranteed safe by manifest validation. Re-run the hardened path
            // resolution only for an exact selected candidate.
            var declaredLibraryPath = Path.GetFullPath(Path.Combine(
                packageIndex.SdkRoot,
                package.LibraryPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!discoveredLibrarySet.Contains(declaredLibraryPath))
            {
                continue;
            }

            if (!SdkManifestPathValidator.TryResolvePath(
                    packageIndex.SdkRoot,
                    package.LibraryPath,
                    out _,
                    out var libraryResolutionError))
            {
                diagnostics?.Add(new SdkDiagnostic(
                    "STK7464",
                    $"SDK package '{package.Id}' library path '{package.LibraryPath}' could not be resolved safely while building the link plan: {libraryResolutionError}",
                    packageIndex.SdkRoot));
                continue;
            }

            selectedPackageIds.Add(package.Id);
        }

        return selectedPackageIds;
    }

    internal static IReadOnlyList<SdkPackageDescriptor> BuildDependentBeforeDependencyOrder(
        SdkPackageIndex packageIndex,
        IEnumerable<string> selectedPackageIds)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(selectedPackageIds.Order(StringComparer.Ordinal));
        while (pending.Count != 0)
        {
            var packageId = pending.Dequeue();
            if (!closure.Add(packageId)
                || !packageIndex.TryGetPackage(packageId, out var package))
            {
                continue;
            }

            foreach (var dependency in package.Dependencies.OrderBy(
                         static dependency => dependency.PackageId,
                         StringComparer.Ordinal))
            {
                pending.Enqueue(dependency.PackageId);
            }
        }

        var incomingEdges = closure.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        foreach (var packageId in closure)
        {
            if (!packageIndex.TryGetPackage(packageId, out var package))
            {
                continue;
            }

            foreach (var dependency in package.Dependencies)
            {
                if (closure.Contains(dependency.PackageId))
                {
                    incomingEdges[dependency.PackageId]++;
                }
            }
        }

        var ready = new SortedSet<string>(
            incomingEdges.Where(static entry => entry.Value == 0).Select(static entry => entry.Key),
            StringComparer.Ordinal);
        var ordered = new List<SdkPackageDescriptor>(closure.Count);
        while (ready.Count != 0)
        {
            var packageId = ready.Min!;
            ready.Remove(packageId);
            if (!packageIndex.TryGetPackage(packageId, out var package))
            {
                continue;
            }

            ordered.Add(package);
            foreach (var dependency in package.Dependencies.OrderBy(
                         static dependency => dependency.PackageId,
                         StringComparer.Ordinal))
            {
                if (!incomingEdges.TryGetValue(dependency.PackageId, out var incoming))
                {
                    continue;
                }

                incomingEdges[dependency.PackageId] = --incoming;
                if (incoming == 0)
                {
                    ready.Add(dependency.PackageId);
                }
            }
        }

        if (ordered.Count != closure.Count)
        {
            throw new InvalidOperationException(
                "SDK package dependency graph contains a cycle after manifest validation.");
        }

        return ordered;
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FormatChecksum(string? checksum) =>
        checksum is null ? "<missing>" : checksum;

    private sealed record RuntimeFileCandidate(
        string PackageId,
        string RelativePath,
        string SourcePath,
        string? DeclaredSha256);
}
