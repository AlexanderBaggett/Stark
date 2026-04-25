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

    [Fact]
    public void ValidateManifestAcceptsRichReExportOnlyModuleWithEmptyFacts()
    {
        var manifest = new StarkPackageManifest(
            RootModule: "Facade",
            LibraryFileName: "libFacade.a",
            Modules:
            [
                new StarkPackageModuleManifest(
                    ModuleName: "Facade",
                    ReExports: [],
                    Functions: [],
                    Types: [],
                    Globals: [],
                    SourceSurface: new StarkPackageSourceSurfaceSection(
                        ReExports: [new StarkPackageReExportManifest("Facade.IO")]),
                    CompilerSections: new StarkPackageCompilerSectionsManifest(
                        TypedInterface: new StarkPackageTypedInterfaceSection([], [], [], Imports: []),
                        CompilerFacts: new StarkPackageCompilerFactsSection([]))),
                CreateRichModule("Facade.IO")
            ]);

        var diagnostics = PackageImageLoader.ValidateManifest(manifest, "/virtual/Facade.starkpkg.json");

        Assert.Empty(diagnostics);
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

    private static StarkPackageModuleManifest CreateRichModule(string moduleName)
    {
        return new StarkPackageModuleManifest(
            ModuleName: moduleName,
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            SourceSurface: new StarkPackageSourceSurfaceSection(),
            CompilerSections: new StarkPackageCompilerSectionsManifest(
                TypedInterface: new StarkPackageTypedInterfaceSection([], [], [], Imports: []),
                CompilerFacts: new StarkPackageCompilerFactsSection([])));
    }
}
