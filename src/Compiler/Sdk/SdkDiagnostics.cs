namespace Stark.Compiler;

internal sealed record SdkDoctorArtifactReport(
    string Status,
    int DeclaredCount,
    IReadOnlyList<string> Paths);

internal sealed record SdkDoctorDiagnosticReport(
    string Severity,
    string Code,
    string Category,
    string Message,
    string? Path,
    string? PackageId);

internal sealed record SdkDoctorTargetReport(
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

internal sealed record SdkDoctorPackageReport(
    string Id,
    string Version,
    string Profile,
    string Status,
    IReadOnlyList<string> Modules,
    SdkDoctorArtifactReport Image,
    SdkDoctorArtifactReport Library,
    SdkDoctorArtifactReport NativeArtifacts,
    SdkDoctorArtifactReport IncludeDirectories,
    SdkDoctorArtifactReport LibraryDirectories,
    SdkDoctorArtifactReport RuntimeFiles,
    SdkDoctorArtifactReport LicenseFiles,
    SdkDoctorArtifactReport Checksums,
    IReadOnlyList<SdkDoctorDiagnosticReport> Diagnostics);

internal sealed record SdkDoctorReport(
    string Status,
    string PackageIntegrityStatus,
    string TargetCompatibilityStatus,
    string? Root,
    string? Origin,
    string? ManifestPath,
    string? Kind,
    int? SchemaVersion,
    string? SdkVersion,
    string? CompilerCompatibility,
    int? PackageFormatVersion,
    SdkDoctorTargetReport? Target,
    IReadOnlyList<SdkDoctorPackageReport> Packages,
    IReadOnlyList<SdkDoctorDiagnosticReport> Diagnostics)
{
    public bool IsValid => string.Equals(Status, "ok", StringComparison.Ordinal);
}

/// <summary>
/// Builds the normalized Stage0 SDK diagnostic model used by both the human
/// doctor view and deterministic automation JSON. Keeping attribution here
/// avoids teaching callers to infer package/artifact identity from prose.
/// </summary>
internal static class SdkDiagnostics
{
    public static SdkDoctorReport BuildDoctorReport(
        ActiveSdkResolution activeSdk,
        LlvmTargetInfo? activeTarget)
    {
        ArgumentNullException.ThrowIfNull(activeSdk);

        var root = activeSdk.Root;
        var activationDiagnostics = activeSdk.Diagnostics
            .Select(static diagnostic => ToReport(diagnostic, packageId: null))
            .ToList();

        if (!activeSdk.IsActive
            || activeSdk.Manifest?.Manifest is not { } manifest
            || activeSdk.PackageResolver is null)
        {
            if (activationDiagnostics.Count == 0)
            {
                var manifestPath = root?.ManifestPath;
                activationDiagnostics.Add(new SdkDoctorDiagnosticReport(
                    "error",
                    "STK7400",
                    "sdk-manifest",
                    manifestPath is null
                        ? "The Stark SDK root could not be resolved."
                        : $"SDK manifest is missing: '{manifestPath}'.",
                    manifestPath,
                    PackageId: null));
            }

            return new SdkDoctorReport(
                root is null ? "unresolved" : "invalid",
                PackageIntegrityStatus: "not-checked",
                TargetCompatibilityStatus: "not-checked",
                root?.RootPath,
                FormatOrigin(root?.Origin),
                root?.ManifestPath,
                Kind: null,
                SchemaVersion: null,
                SdkVersion: null,
                CompilerCompatibility: null,
                PackageFormatVersion: null,
                Target: null,
                Packages: Array.Empty<SdkDoctorPackageReport>(),
                activationDiagnostics.ToArray());
        }

        var packageReports = manifest.Packages
            .OrderBy(static package => package.Id, StringComparer.Ordinal)
            .Select(package => BuildPackageReport(
                manifest,
                activeSdk.PackageResolver,
                package))
            .ToArray();

        var diagnostics = new List<SdkDoctorDiagnosticReport>(activationDiagnostics);
        foreach (var package in packageReports)
        {
            diagnostics.AddRange(package.Diagnostics);
        }

        var targetCompatibilityStatus = "not-checked";
        if (activeTarget is not null)
        {
            var compatibility = SdkTargetCompatibility.ValidateActiveTarget(
                manifest.Target,
                activeTarget,
                activeSdk.Manifest.ManifestPath);
            targetCompatibilityStatus = compatibility.IsCompatible ? "ok" : "incompatible";
            diagnostics.AddRange(compatibility.Diagnostics.Select(
                static diagnostic => ToReport(diagnostic, packageId: null)));
        }

        var packageIntegrityStatus = packageReports.All(static package => package.Status == "ok")
            ? "ok"
            : "invalid";

        return new SdkDoctorReport(
            diagnostics.Count == 0 ? "ok" : "invalid",
            packageIntegrityStatus,
            targetCompatibilityStatus,
            root!.RootPath,
            FormatOrigin(root.Origin),
            root.ManifestPath,
            manifest.Kind.ToString().ToLowerInvariant(),
            manifest.SchemaVersion,
            manifest.SdkVersion,
            manifest.CompilerCompatibility,
            manifest.PackageFormatVersion,
            ToTargetReport(manifest.Target),
            packageReports,
            diagnostics.ToArray());
    }

    public static void WriteText(TextWriter stdout, SdkDoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(report);

        stdout.WriteLine($"  root: {report.Root ?? "<unresolved>"}");
        if (report.Origin is not null)
        {
            stdout.WriteLine($"  origin: {report.Origin}");
        }

        if (report.ManifestPath is not null)
        {
            stdout.WriteLine($"  manifest: {report.ManifestPath}");
        }

        if (report.Kind is null)
        {
            stdout.WriteLine("  status: <missing or invalid>");
            WriteDiagnostics(stdout, report.Diagnostics, indentation: "  ");
            return;
        }

        stdout.WriteLine($"  kind: {report.Kind}");
        stdout.WriteLine($"  schema: {report.SchemaVersion}");
        stdout.WriteLine($"  version: {report.SdkVersion}");
        stdout.WriteLine($"  compiler compatibility: {report.CompilerCompatibility}");
        stdout.WriteLine($"  package format: {report.PackageFormatVersion}");
        stdout.WriteLine($"  target id: {report.Target!.Id}");
        stdout.WriteLine($"  target triple: {report.Target.LlvmTriple}");
        stdout.WriteLine($"  target architecture: {report.Target.Architecture}");
        stdout.WriteLine($"  target operating system: {report.Target.OperatingSystem}");
        stdout.WriteLine($"  target abi: {report.Target.Abi}");
        stdout.WriteLine($"  target pointer width: {report.Target.PointerBitWidth}");
        stdout.WriteLine($"  target endianness: {report.Target.Endianness}");
        stdout.WriteLine($"  target data layout: {report.Target.DataLayout ?? "<unspecified>"}");
        stdout.WriteLine($"  target baseline cpu: {report.Target.BaselineCpu ?? "<unspecified>"}");
        stdout.WriteLine($"  target baseline features: {FormatList(report.Target.BaselineFeatures)}");
        stdout.WriteLine($"  target relocation model: {report.Target.RelocationModel}");
        stdout.WriteLine($"  target code model: {report.Target.CodeModel ?? "<unspecified>"}");
        stdout.WriteLine($"  target c data model: {report.Target.CDataModel ?? "<unspecified>"}");
        stdout.WriteLine($"  target minimum os version: {report.Target.MinimumOperatingSystemVersion ?? "<unspecified>"}");
        stdout.WriteLine($"  packages: {FormatList(report.Packages.Select(static package => package.Id))}");
        stdout.WriteLine("  package status:");

        foreach (var package in report.Packages)
        {
            stdout.WriteLine($"    {package.Id}: {package.Status} (version={package.Version}, profile={package.Profile})");
            WriteArtifact(stdout, "image", package.Image);
            WriteArtifact(stdout, "archive", package.Library);
            WriteArtifact(stdout, "native artifacts", package.NativeArtifacts);
            WriteArtifact(stdout, "include directories", package.IncludeDirectories);
            WriteArtifact(stdout, "library directories", package.LibraryDirectories);
            WriteArtifact(stdout, "runtime files", package.RuntimeFiles);
            WriteArtifact(stdout, "license files", package.LicenseFiles);
            WriteArtifact(stdout, "checksums", package.Checksums);
            WriteDiagnostics(stdout, package.Diagnostics, indentation: "      ");
        }

        stdout.WriteLine($"  package integrity: {report.PackageIntegrityStatus}");
        stdout.WriteLine($"  target compatibility: {report.TargetCompatibilityStatus}");
        if (!report.IsValid)
        {
            var packageDiagnosticKeys = report.Packages
                .SelectMany(static package => package.Diagnostics)
                .Select(static diagnostic => DiagnosticKey(diagnostic))
                .ToHashSet(StringComparer.Ordinal);
            WriteDiagnostics(
                stdout,
                report.Diagnostics.Where(diagnostic => !packageDiagnosticKeys.Contains(DiagnosticKey(diagnostic))),
                indentation: "  ");
        }

        stdout.WriteLine($"  status: {(report.IsValid ? "ok" : "invalid")}");
    }

    private static SdkDoctorPackageReport BuildPackageReport(
        SdkManifest manifest,
        SdkPackageModuleResolver resolver,
        SdkPackageDescriptor package)
    {
        var diagnostics = resolver.ValidatePackage(package.Id)
            .Select(diagnostic => ToReport(diagnostic, package.Id))
            .ToArray();
        var codes = diagnostics.Select(static diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);
        var hasBlockingImageFailure = diagnostics.Any(IsBlockingImageDiagnostic);
        var hasLibraryFailure = diagnostics.Any(IsLibraryDiagnostic);

        var imageStatus = diagnostics.Any(IsImageDiagnostic) ? "invalid" : "ok";
        var libraryStatus = package.LibraryPath is null
            ? "not-required"
            : hasLibraryFailure
                ? "invalid"
                : hasBlockingImageFailure
                    ? "not-checked"
                    : "ok";

        var declaredChecksumCount = (package.ImageSha256 is null ? 0 : 1)
            + (package.LibrarySha256 is null ? 0 : 1)
            + package.Native.FileChecksums.Count;
        var hasUnverifiableChecksum = hasBlockingImageFailure
            || codes.Overlaps(["STK7466", "STK7470", "STK7473", "STK7474"]);
        var checksumStatus = declaredChecksumCount == 0
            ? "not-required"
            : diagnostics.Any(IsChecksumDiagnostic)
                ? "invalid"
                : hasUnverifiableChecksum
                    ? "not-checked"
                    : "ok";

        return new SdkDoctorPackageReport(
            package.Id,
            package.Version,
            package.Profile,
            diagnostics.Length == 0 ? "ok" : "invalid",
            manifest.Modules
                .Where(ownership => string.Equals(ownership.PackageId, package.Id, StringComparison.Ordinal))
                .Select(static ownership => ownership.ModuleName)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            new SdkDoctorArtifactReport(imageStatus, 1, [package.ImagePath]),
            new SdkDoctorArtifactReport(
                libraryStatus,
                package.LibraryPath is null ? 0 : 1,
                package.LibraryPath is null ? Array.Empty<string>() : [package.LibraryPath]),
            BuildDeclaredArtifactReport(
                package.Native.ArtifactPaths,
                codes.Contains("STK7470")
                    || HasChecksumFailureForDeclaredPath(
                        resolver.PackageIndex.SdkRoot,
                        package.Native.ArtifactPaths,
                        diagnostics)),
            BuildDeclaredArtifactReport(package.Native.IncludeDirectories, codes.Contains("STK7471")),
            BuildDeclaredArtifactReport(package.Native.LibraryDirectories, codes.Contains("STK7472")),
            BuildDeclaredArtifactReport(
                package.Native.RuntimeFiles,
                codes.Contains("STK7473")
                    || HasChecksumFailureForDeclaredPath(
                        resolver.PackageIndex.SdkRoot,
                        package.Native.RuntimeFiles,
                        diagnostics)),
            BuildDeclaredArtifactReport(
                package.Native.LicenseFiles,
                codes.Contains("STK7474")
                    || HasChecksumFailureForDeclaredPath(
                        resolver.PackageIndex.SdkRoot,
                        package.Native.LicenseFiles,
                        diagnostics)),
            new SdkDoctorArtifactReport(
                checksumStatus,
                declaredChecksumCount,
                BuildChecksumPaths(package)),
            diagnostics);
    }

    private static SdkDoctorArtifactReport BuildDeclaredArtifactReport(
        IReadOnlyList<string> paths,
        bool invalid) =>
        new(
            paths.Count == 0 ? "not-required" : invalid ? "invalid" : "ok",
            paths.Count,
            paths.Order(StringComparer.Ordinal).ToArray());

    private static bool HasChecksumFailureForDeclaredPath(
        string sdkRoot,
        IReadOnlyList<string> declaredPaths,
        IReadOnlyList<SdkDoctorDiagnosticReport> diagnostics)
    {
        var checksumFailurePaths = diagnostics
            .Where(IsChecksumDiagnostic)
            .Select(static diagnostic => diagnostic.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .ToHashSet(GetPathComparer());
        if (checksumFailurePaths.Count == 0)
        {
            return false;
        }

        foreach (var declaredPath in declaredPaths)
        {
            if (SdkManifestPathValidator.TryResolvePath(sdkRoot, declaredPath, out var resolvedPath, out _)
                && checksumFailurePaths.Contains(Path.GetFullPath(resolvedPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildChecksumPaths(SdkPackageDescriptor package)
    {
        var paths = new List<string>();
        if (package.ImageSha256 is not null)
        {
            paths.Add(package.ImagePath);
        }

        if (package.LibrarySha256 is not null && package.LibraryPath is not null)
        {
            paths.Add(package.LibraryPath);
        }

        paths.AddRange(package.Native.FileChecksums.Select(static checksum => checksum.Path));
        return paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static SdkDoctorTargetReport ToTargetReport(SdkTargetDescriptor target) =>
        new(
            target.Id,
            target.LlvmTriple,
            target.Architecture,
            target.OperatingSystem,
            target.Abi,
            target.PointerBitWidth,
            target.Endianness.ToString().ToLowerInvariant(),
            target.DataLayout,
            target.BaselineCpu,
            target.BaselineFeatures.ToArray(),
            target.RelocationModel,
            target.CodeModel,
            target.CDataModel,
            target.MinimumOperatingSystemVersion);

    private static SdkDoctorDiagnosticReport ToReport(SdkDiagnostic diagnostic, string? packageId) =>
        new(
            "error",
            diagnostic.Code,
            Classify(diagnostic),
            diagnostic.Message,
            diagnostic.Path,
            packageId);

    private static string Classify(SdkDiagnostic diagnostic)
    {
        return diagnostic.Code switch
        {
            "STK7400" => "sdk-root",
            "STK7460" or "STK7461" or "STK7462" or "STK7463" or "STK7465" => "package-image",
            "STK7464" or "STK7466" or "STK7467" => "package-archive",
            "STK7468" when diagnostic.Message.Contains("library", StringComparison.OrdinalIgnoreCase) => "package-archive-checksum",
            "STK7468" when diagnostic.Message.Contains("image", StringComparison.OrdinalIgnoreCase) => "package-image-checksum",
            "STK7468" => "package-checksum",
            "STK7469" => "sdk-path-security",
            "STK7470" => "native-artifact",
            "STK7471" => "native-include-directory",
            "STK7472" => "native-library-directory",
            "STK7473" => "native-runtime-file",
            "STK7474" => "native-license-file",
            "STK7475" => "native-file-checksum",
            "STK7476" or "STK7477" => "native-runtime-staging",
            _ when diagnostic.Code.StartsWith("STK748", StringComparison.Ordinal)
                || diagnostic.Code.StartsWith("STK749", StringComparison.Ordinal) => "target-compatibility",
            _ when diagnostic.Code.StartsWith("STK745", StringComparison.Ordinal) => "package-identity",
            _ when diagnostic.Code.StartsWith("STK744", StringComparison.Ordinal) => "package-dependency",
            _ => "sdk-manifest"
        };
    }

    private static bool IsImageDiagnostic(SdkDoctorDiagnosticReport diagnostic) =>
        diagnostic.Category is "package-image" or "package-image-checksum" or "package-identity";

    private static bool IsBlockingImageDiagnostic(SdkDoctorDiagnosticReport diagnostic) =>
        IsImageDiagnostic(diagnostic) || diagnostic.Category == "target-compatibility";

    private static bool IsLibraryDiagnostic(SdkDoctorDiagnosticReport diagnostic) =>
        diagnostic.Category is "package-archive" or "package-archive-checksum";

    private static bool IsChecksumDiagnostic(SdkDoctorDiagnosticReport diagnostic) =>
        diagnostic.Category.Contains("checksum", StringComparison.Ordinal);

    private static string? FormatOrigin(SdkRootOrigin? origin) =>
        origin?.ToString().ToLowerInvariant();

    private static void WriteArtifact(TextWriter stdout, string label, SdkDoctorArtifactReport artifact)
    {
        var paths = artifact.Paths.Count == 0 ? string.Empty : $" [{string.Join(", ", artifact.Paths)}]";
        stdout.WriteLine($"      {label}: {artifact.Status} (declared={artifact.DeclaredCount}){paths}");
    }

    private static void WriteDiagnostics(
        TextWriter stdout,
        IEnumerable<SdkDoctorDiagnosticReport> diagnostics,
        string indentation)
    {
        foreach (var diagnostic in diagnostics)
        {
            var path = string.IsNullOrWhiteSpace(diagnostic.Path)
                ? string.Empty
                : $" ({diagnostic.Path})";
            stdout.WriteLine($"{indentation}{diagnostic.Code} [{diagnostic.Category}]: {diagnostic.Message}{path}");
        }
    }

    private static string FormatList(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? "<none>" : string.Join(", ", materialized);
    }

    private static string DiagnosticKey(SdkDoctorDiagnosticReport diagnostic) =>
        $"{diagnostic.Code}\0{diagnostic.Message}\0{diagnostic.Path}\0{diagnostic.PackageId}";

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
