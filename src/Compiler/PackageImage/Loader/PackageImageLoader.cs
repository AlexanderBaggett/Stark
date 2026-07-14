using Stark.Parsing;

namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    public static bool TryLoadManifest(string manifestPath, out StarkPackageManifest manifest)
    {
        return TryLoadManifest(manifestPath, out manifest, out _);
    }

    public static bool TryLoadManifest(
        string manifestPath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        return TryLoadManifest(
            manifestPath,
            out manifest,
            out diagnostics,
            out _);
    }

    internal static bool TryLoadManifest(
        string manifestPath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics,
        out uint? binaryFormatVersion)
    {
        manifest = default!;
        binaryFormatVersion = null;

        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (PackageImageBinaryFormat.HasBinaryMagic(bytes)
                || PackageImageBinaryFormat.HasBinaryFileName(manifestPath))
            {
                if (PackageImageBinaryFormat.TryReadFormatVersion(bytes, out var formatVersion))
                {
                    binaryFormatVersion = formatVersion;
                }

                return PackageImageBinaryFormat.TryDecode(bytes, manifestPath, out manifest, out diagnostics);
            }

            return TryParseManifestJsonDocument(
                System.Text.Encoding.UTF8.GetString(bytes),
                manifestPath,
                validate: false,
                out manifest,
                out diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics =
            [
                new CompilerDiagnostic(
                    Code: "STK7127",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image file could not be read: {exception.Message}",
                    Stage: "package-image",
                    Location: new SourceLocation(manifestPath, 1, 1))
            ];
            return false;
        }
    }

    public static bool TryParseManifestJson(
        string json,
        string? manifestPath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        return TryParseManifestJsonDocument(json, manifestPath, validate: true, out manifest, out diagnostics);
    }

    private static bool TryParseManifestJsonDocument(
        string json,
        string? manifestPath,
        bool validate,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        manifest = default!;
        try
        {
            var parsed = StarkPackageManifest.FromJson(json);
            if (parsed is null)
            {
                diagnostics =
                [
                    new CompilerDiagnostic(
                        Code: "STK7100",
                        Severity: DiagnosticSeverity.Error,
                        Message: "Package image JSON could not be parsed into a Stark package image document.",
                        Stage: "package-image",
                        Location: new SourceLocation(manifestPath, 1, 1))
                ];
                return false;
            }

            manifest = parsed;
            diagnostics = validate ? ValidateManifest(parsed, manifestPath) : [];
            return diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
        }
        catch (Exception exception)
        {
            diagnostics =
            [
                new CompilerDiagnostic(
                    Code: "STK7101",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image JSON is malformed: {exception.Message}",
                    Stage: "package-image",
                    Location: new SourceLocation(manifestPath, 1, 1))
            ];
            return false;
        }
    }

    public static IReadOnlyList<CompilerDiagnostic> ValidateManifest(StarkPackageManifest manifest, string? manifestPath = null)
    {
        var diagnostics = new List<CompilerDiagnostic>();
        var manifestLocation = new SourceLocation(manifestPath, 1, 1);

        if (string.IsNullOrWhiteSpace(manifest.RootModule))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7102",
                Severity: DiagnosticSeverity.Error,
                Message: "Package image root module name must not be empty.",
                Stage: "package-image",
                Location: manifestLocation));
        }

        if (string.IsNullOrWhiteSpace(manifest.LibraryFileName))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7103",
                Severity: DiagnosticSeverity.Error,
                Message: "Package image library file name must not be empty.",
                Stage: "package-image",
                Location: manifestLocation));
        }

        if (manifest.Modules is not { Count: > 0 })
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7104",
                Severity: DiagnosticSeverity.Error,
                Message: "Package image must contain at least one module entry.",
                Stage: "package-image",
                Location: manifestLocation));
            return diagnostics;
        }

        ValidateNativeDependencyList(
            manifest.NativeDependencies?.Sources,
            "native source",
            "STK7110",
            diagnostics,
            manifestLocation);
        ValidateNativeDependencyList(
            manifest.NativeDependencies?.IncludeDirectories,
            "native include directory",
            "STK7111",
            diagnostics,
            manifestLocation);
        ValidateNativeDependencyList(
            manifest.NativeDependencies?.LibraryDirectories,
            "native library directory",
            "STK7112",
            diagnostics,
            manifestLocation);
        ValidateNativeDependencyList(
            manifest.NativeDependencies?.Libraries,
            "native library",
            "STK7113",
            diagnostics,
            manifestLocation);
        ValidateNativeDependencyList(
            manifest.NativeDependencies?.LinkArguments,
            "native link argument",
            "STK7114",
            diagnostics,
            manifestLocation);
        ValidateNativeDependencyList(
            manifest.NativeDependencies?.PkgConfigPackages,
            "native pkg-config package",
            "STK7115",
            diagnostics,
            manifestLocation);
        TargetCompatibilityValidator.ValidateManifestTarget(manifest, manifestPath, diagnostics);
        ValidateManifestBuildProfile(manifest.BuildProfile, diagnostics, manifestLocation);
        diagnostics.AddRange(ValidateManifestIdentity(manifest, manifestPath));

        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in manifest.Modules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleName))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7105",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Package image module entries must have a non-empty module name.",
                    Stage: "package-image",
                    Location: manifestLocation));
                continue;
            }

            if (!moduleNames.Add(module.ModuleName))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7106",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{module.ModuleName}' appears more than once.",
                    Stage: "package-image",
                    Location: manifestLocation));
                continue;
            }

            var resolvedModule = new ResolvedPackageModule(
                ManifestPath: manifestPath ?? "<memory>",
                LibraryPath: manifest.LibraryFileName,
                Manifest: manifest,
                Module: module);

            if (!TryBuildModuleSyntaxModel(resolvedModule, out _))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7107",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{module.ModuleName}' has malformed or unsupported typed interface/source-surface content.",
                    Stage: "package-image",
                    Location: manifestLocation));
            }

            if (!TryBuildLoadedPackageImageFacts(resolvedModule, out _))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7108",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package image module '{module.ModuleName}' has malformed compiler facts or typed template sections.",
                    Stage: "package-image",
                    Location: manifestLocation));
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.RootModule)
            && !moduleNames.Contains(manifest.RootModule))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7109",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image root module '{manifest.RootModule}' is not present in the module table.",
                Stage: "package-image",
                Location: manifestLocation));
        }

        return diagnostics;
    }

    internal static IReadOnlyList<CompilerDiagnostic> ValidateManifestIdentity(
        StarkPackageManifest manifest,
        string? manifestPath = null)
    {
        var diagnostics = new List<CompilerDiagnostic>();
        AppendManifestIdentityDiagnostics(
            manifest,
            diagnostics,
            new SourceLocation(manifestPath, 1, 1));
        return diagnostics;
    }

    private static void AppendManifestIdentityDiagnostics(
        StarkPackageManifest manifest,
        List<CompilerDiagnostic> diagnostics,
        SourceLocation location)
    {
        if (manifest.Identity is not { } identity)
        {
            return;
        }

        if (!string.Equals(identity.PackageId, manifest.RootModule, StringComparison.Ordinal))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7137",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package identity '{identity.PackageId}' does not match root module '{manifest.RootModule}'.",
                Stage: "package-image",
                Location: location));
        }

        if (!PackageImageIdentity.IsSha256(identity.ApiHash)
            || !PackageImageIdentity.IsSha256(identity.ContentHash))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7137",
                Severity: DiagnosticSeverity.Error,
                Message: "Package identity API/content hashes must be lowercase SHA-256 values.",
                Stage: "package-image",
                Location: location));
        }

        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        string? previousDependencyId = null;
        if (identity.Dependencies is null)
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7137",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package '{manifest.RootModule}' identity must contain a dependency array.",
                Stage: "package-image",
                Location: location));
            return;
        }

        foreach (var dependency in identity.Dependencies ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency.PackageId)
                || !string.Equals(dependency.PackageId, dependency.PackageId.Trim(), StringComparison.Ordinal)
                || string.Equals(dependency.PackageId, manifest.RootModule, StringComparison.Ordinal)
                || !dependencyIds.Add(dependency.PackageId)
                || !PackageImageIdentity.IsSha256(dependency.ApiHash)
                || !PackageImageIdentity.IsSha256(dependency.ContentHash))
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7137",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package '{manifest.RootModule}' has an invalid, duplicate, self-referential, or unhashed dependency identity '{dependency.PackageId}'.",
                    Stage: "package-image",
                    Location: location));
                continue;
            }

            if (previousDependencyId is not null
                && string.CompareOrdinal(previousDependencyId, dependency.PackageId) >= 0)
            {
                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7137",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Package '{manifest.RootModule}' dependency identities must be in unique ordinal package-ID order.",
                    Stage: "package-image",
                    Location: location));
            }

            previousDependencyId = dependency.PackageId;
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Code == "STK7137"))
        {
            return;
        }

        var expected = PackageImageIdentity.Create(manifest, identity.Dependencies);
        if (!string.Equals(identity.ApiHash, expected.ApiHash, StringComparison.Ordinal)
            || !string.Equals(identity.ContentHash, expected.ContentHash, StringComparison.Ordinal))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7138",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package '{manifest.RootModule}' API/content identity does not match its typed/compiler/content facts.",
                Stage: "package-image",
                Location: location));
        }
    }

    private static void ValidateManifestBuildProfile(
        StarkPackageBuildProfileManifest? profile,
        List<CompilerDiagnostic> diagnostics,
        SourceLocation location)
    {
        if (profile is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7128",
                Severity: DiagnosticSeverity.Error,
                Message: "Package image build profile name must not be empty when profile facts are present.",
                Stage: "package-image",
                Location: location));
            return;
        }

        if (!string.Equals(profile.Name, "dev", StringComparison.Ordinal)
            && !string.Equals(profile.Name, "release", StringComparison.Ordinal))
        {
            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7129",
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image build profile '{profile.Name}' is not supported. Expected dev or release.",
                Stage: "package-image",
                Location: location));
        }
    }

    private static void ValidateNativeDependencyList(
        IReadOnlyList<string>? values,
        string label,
        string code,
        List<CompilerDiagnostic> diagnostics,
        SourceLocation location)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            diagnostics.Add(new CompilerDiagnostic(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: $"Package image {label} entries must not be empty.",
                Stage: "package-image",
                Location: location));
        }
    }

    public static bool TryBuildModuleDocument(ResolvedPackageModule module, out LoadedModuleDocument document)
    {
        document = default!;

        if (module.Module.EffectiveTypedInterface?.Functions.Any(static function => function.Asm is not null) == true
            || !TryBuildModuleSyntaxModel(module, out var syntaxModel)
            || !TryBuildModuleSource(module, out var sourceText))
        {
            return false;
        }

        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
        document = new LoadedModuleDocument(
            new ResolvedModuleReference(
                module.Module.ModuleName,
                module.ManifestPath,
                IsExternal: false,
                IsRoot: false,
                ManifestPath: module.ManifestPath,
                LibraryPath: module.LibraryPath),
            parseResult,
            syntaxModel,
            TryBuildLoadedPackageImageFacts(module, out var packageImageFacts) ? packageImageFacts : null);
        return true;
    }

    public static bool TryBuildStructuredModuleDocument(ResolvedPackageModule module, out LoadedModuleDocument document)
    {
        document = default!;

        if (module.Module.EffectiveTypedInterface?.Functions.Any(static function => function.Asm is not null) == true
            || !TryBuildModuleSyntaxModel(module, out var syntaxModel)
            || !TryBuildLoadedPackageImageFacts(module, out var packageImageFacts)
            || !TryBuildStructuredModuleSource(module, out var sourceText))
        {
            return false;
        }

        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
        if (parseResult.Diagnostics.Count != 0)
        {
            return false;
        }

        document = new LoadedModuleDocument(
            new ResolvedModuleReference(
                module.Module.ModuleName,
                module.ManifestPath,
                IsExternal: false,
                IsRoot: false,
                ManifestPath: module.ManifestPath,
                LibraryPath: module.LibraryPath),
            parseResult,
            syntaxModel,
            packageImageFacts);
        return true;
    }

    private static bool TryBuildStructuredModuleSource(ResolvedPackageModule module, out string sourceText)
    {
        return TryBuildModuleSource(module, out sourceText);
    }
}
