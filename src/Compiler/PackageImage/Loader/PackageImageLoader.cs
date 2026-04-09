using Stark.Parsing;

namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    public static bool TryLoadManifest(string manifestPath, out StarkPackageManifest manifest)
    {
        manifest = default!;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var parsed = StarkPackageManifest.FromJson(json);
            if (parsed is null)
            {
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseManifestJson(
        string json,
        string? manifestPath,
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
                        Message: "Package image JSON could not be parsed into a Stark package manifest.",
                        Stage: "package-image",
                        Location: new SourceLocation(manifestPath, 1, 1))
                ];
                return false;
            }

            manifest = parsed;
            diagnostics = ValidateManifest(parsed, manifestPath);
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
