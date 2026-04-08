using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageLoaderDiagnosticsTests
{
    [Fact]
    public void TryParseManifestJsonReportsMalformedJson()
    {
        var success = PackageImageLoader.TryParseManifestJson(
            "{",
            "/virtual/Broken.starkpkg.json",
            out _,
            out var diagnostics);

        Assert.False(success);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("STK7101", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("package-image", diagnostic.Stage);
    }

    [Fact]
    public void ValidateManifestReportsDuplicateModuleAndMissingRootModuleEntry()
    {
        var firstModule = CreateLegacyOnlyModule("Demo");
        var duplicateModule = CreateLegacyOnlyModule("Demo");
        var manifest = new StarkPackageManifest(
            RootModule: "MissingRoot",
            LibraryFileName: "libDemo.a",
            Modules: [firstModule, duplicateModule]);

        var diagnostics = PackageImageLoader.ValidateManifest(manifest, "/virtual/Demo.starkpkg.json");

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Code == "STK7106");
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Code == "STK7109");
    }

    [Fact]
    public void ValidateManifestReportsMissingRichSectionsForLegacyOnlyModule()
    {
        var manifest = new StarkPackageManifest(
            RootModule: "Demo",
            LibraryFileName: "libDemo.a",
            Modules: [CreateLegacyOnlyModule("Demo")]);

        var diagnostics = PackageImageLoader.ValidateManifest(manifest, "/virtual/Demo.starkpkg.json");

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Code == "STK7107");
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Code == "STK7108");
    }

    private static StarkPackageModuleManifest CreateLegacyOnlyModule(string moduleName)
    {
        return new StarkPackageModuleManifest(
            ModuleName: moduleName,
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: []);
    }
}
