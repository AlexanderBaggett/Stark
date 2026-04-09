using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineLoadModulesTests
{
    [Fact]
    public void ManifestBackedModulesPreservePublishedSemanticFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-semantics-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32 Value;
                }

                public fn retborrow Box Echo(retborrow Box value) {
                    return value;
                }

                public fn void Reset(borrow mut Box value) {
                    value.Value = 0;
                    return;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
                        }
                        : module)
                    .ToArray()
            };
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("Echo(", sourceText, StringComparison.Ordinal);
            Assert.Contains("Reset(", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Box value) {
                        return value.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "load-modules"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Echo", out var echo));
            Assert.NotNull(echo.MemoryEffects);
            Assert.True(echo.MemoryEffects!.CapturesArgumentMemory);
            Assert.Equal(ParameterCaptureKind.Return, Assert.Single(echo.Parameters!).CaptureKind);

            Assert.True(importedModule.PackageImageFacts.FunctionSemantics.TryGetValue("Facade.Reset", out var reset));
            Assert.NotNull(reset.MemoryEffects);
            Assert.True(reset.MemoryEffects!.WritesArgumentMemory);
            Assert.True(Assert.Single(reset.Parameters!).Writes);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageImageDocumentResolversLoadStructuredImportsWithoutAnySourceText()
    {
        var bitsModule = new StarkPackageModuleManifest(
            "Bits",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions: [],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Token",
                        QualifiedName: "Bits.Token",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ])
                ],
                Globals: [],
                TypeAliases: []));
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports:
            [
                new StarkPackageReExportManifest("Bits")
            ],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("named", Name: "Bits.Token"),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("named", Name: "Bits.Token"))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []));

        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Bits.starkpkg.json",
                    "/virtual/libBits.a",
                    new StarkPackageManifest("Bits", "libBits.a", [bitsModule]),
                    bitsModule),
                out var bitsDocument));
        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var facadeDocument));

        var resolver = new DocumentOnlyModuleResolver(bitsDocument, facadeDocument);
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn void Run() {
                    return;
                }
                """),
            new CompilerOptions(
                ModuleResolver: resolver,
                StopAfterPassId: "load-modules"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.Equal(0, resolver.SourceLoadAttempts);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Facade"));
        Assert.True(moduleGraph.HasModule("Bits"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
        Assert.NotNull(importedFacade);
        Assert.True(loadedModules.TryGet("Bits", out var importedBits));
        Assert.NotNull(importedBits);
        Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Bits" && import.IsReExport);
        Assert.Contains(importedBits.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Record && declaration.Name == "Token");
        Assert.Contains(importedFacade.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Identity");
    }


    [Fact]
    public void PackageImageDocumentResolversLoadNonReExportImportsWithoutAnySourceText()
    {
        var mathModule = new StarkPackageModuleManifest(
            "Math",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Math.Identity",
                        Visibility: "public",
                        SymbolName: "Math.Identity",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []));
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Forward",
                        QualifiedName: "Facade.Forward",
                        Visibility: "public",
                        SymbolName: "Facade.Forward",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []),
            Imports:
            [
                new StarkPackageImportManifest("Math", IsExported: false)
            ]);

        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Math.starkpkg.json",
                    "/virtual/libMath.a",
                    new StarkPackageManifest("Math", "libMath.a", [mathModule]),
                    mathModule),
                out var mathDocument));
        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule, mathModule]),
                    facadeModule),
                out var facadeDocument));

        var resolver = new DocumentOnlyModuleResolver(mathDocument, facadeDocument);
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn void Run() {
                    return;
                }
                """),
            new CompilerOptions(
                ModuleResolver: resolver,
                StopAfterPassId: "load-modules"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.Equal(0, resolver.SourceLoadAttempts);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Facade"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
        Assert.NotNull(importedFacade);
        Assert.True(loadedModules.TryGet("Math", out var importedMath));
        Assert.NotNull(importedMath);
        Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Math" && !import.IsReExport);
        Assert.Contains(importedFacade.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Forward");
    }
}
