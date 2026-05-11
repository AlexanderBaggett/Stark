using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineLoadModulesTests
{
    [Fact]
    public void LoadModulesReusesSourceParsesDiscoveredByModuleGraph()
    {
        var resolver = new CountingSourceModuleResolver(
            (
                "Facade",
                """
                import Bits
                module Facade

                public fn void Touch() {
                    return;
                }
                """),
            (
                "Bits",
                """
                module Bits

                public fn void Mark() {
                    return;
                }
                """));
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
        Assert.Equal(1, resolver.GetSourceLoadCount("Facade"));
        Assert.Equal(1, resolver.GetSourceLoadCount("Bits"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SourceModuleParseCache, out SourceModuleParseCache? cache));
        Assert.NotNull(cache);
        Assert.True(cache.TryGet("Facade", out _));
        Assert.True(cache.TryGet("Bits", out _));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("Facade", out var facadeModule));
        Assert.NotNull(facadeModule);
        Assert.True(loadedModules.TryGet("Bits", out var bitsModule));
        Assert.NotNull(bitsModule);
        Assert.True(cache.TryGet("Facade", out var cachedFacade));
        Assert.NotNull(cachedFacade);
        Assert.Same(cachedFacade.SyntaxModel, facadeModule.SyntaxModel);
        Assert.True(cache.TryGet("Bits", out var cachedBits));
        Assert.NotNull(cachedBits);
        Assert.Same(cachedBits.SyntaxModel, bitsModule.SyntaxModel);
    }

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
                    i32[min max] Value;
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

                    fn i32[min max] Run(Facade.Box value) {
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
    public void ManifestBackedModulesPreservePublishedSemanticCallFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-semantic-calls-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn void Touch(borrow mut Box box) {
                    box.Value = 1;
                    return;
                }

                public fn void Outer(borrow mut Box box) {
                    Touch(box);
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

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run(borrow mut Facade.Box box) {
                        Facade.Outer(box);
                        return;
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

            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Outer", out var outer));
            Assert.Contains("Facade.Touch", outer.CalledFunctions);
            var call = Assert.Single(outer.Calls);
            Assert.Equal("Facade.Touch", call.CalleeName);
            Assert.True(call.MemoryEffects.WritesArgumentMemory);
            var argument = Assert.Single(call.Arguments);
            Assert.Equal(0, argument.ArgumentIndex);
            Assert.Equal("box", argument.CallerParameterName);
            Assert.Equal("box", argument.CalleeParameterName);
            Assert.True(argument.Writes);
            Assert.Equal(ParameterCaptureKind.None, argument.CaptureKind);
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
    public void ManifestBackedModulesPreservePublishedGenericTemplateSemanticCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-template-semantic-calls-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn void Reset(borrow mut Box box) {
                    box.Value = 0;
                    return;
                }

                public fn void Touch<T>(borrow mut Box box, T tag) {
                    Reset(box);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
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

            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Touch", out var template));
            Assert.Contains("Facade.Reset", template.CalledFunctions);
            var call = Assert.Single(template.Calls);
            Assert.Equal("Facade.Reset", call.CalleeName);
            Assert.True(call.MemoryEffects.WritesArgumentMemory);
            var argument = Assert.Single(call.Arguments);
            Assert.Equal(0, argument.ArgumentIndex);
            Assert.Equal("box", argument.CallerParameterName);
            Assert.Equal("box", argument.CalleeParameterName);
            Assert.True(argument.Writes);
            Assert.Equal(ParameterCaptureKind.None, argument.CaptureKind);
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

    [Fact]
    public void ManifestBackedModulesPreserveOptimizationReadyGenericTemplateFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-template-optimization-facts-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T> {
                    T Value;
                }

                public fn Box<T> Wrap<T>(T value) {
                    stack T copy = value;
                    return new Box<T>() { Value = copy };
                }

                public fn T Read<T>(borrow Box<T> box) {
                    return box.Value;
                }

                public fn T Forward<T>(borrow Box<T> box) {
                    return Read(box);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
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

            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Wrap", out var wrap));
            Assert.NotNull(wrap.TypedBody);
            Assert.Equal(2, wrap.TopLevelStatementCount);
            Assert.True(wrap.EstimatedBodyCost is > 0);
            Assert.NotNull(wrap.OptimizationSummary);
            Assert.Equal(1, wrap.OptimizationSummary!.ObjectCreationCount);
            var local = Assert.Single(wrap.LocalDeclarations);
            Assert.Equal("var", local.Kind);
            Assert.Equal("T", local.Type.NamedType);
            var objectCreation = Assert.Single(wrap.ObjectCreations);
            Assert.Contains("Facade.Box", objectCreation.CreatedType.DisplayName, StringComparison.Ordinal);
            Assert.Null(objectCreation.Constructor);
            var initializerMember = Assert.Single(objectCreation.InitializerMembers);
            Assert.Equal("Value", initializerMember.FieldName);
            Assert.Equal(0, initializerMember.FieldIndex);
            Assert.Equal("T", initializerMember.FieldType.NamedType);

            Assert.True(importedModule.PackageImageFacts.FunctionTemplates.TryGetValue("Facade.Read", out var read));
            Assert.NotNull(read.TypedBody);
            Assert.NotNull(read.OptimizationSummary);
            Assert.True(read.OptimizationSummary!.IsSingleReturnFieldAccessWrapper);
            var fieldAccess = Assert.Single(read.FieldAccesses);
            Assert.Equal("Value", fieldAccess.FieldName);
            Assert.Equal(0, fieldAccess.FieldIndex);
            Assert.Equal("T", fieldAccess.FieldType.NamedType);

            Assert.True(importedModule.PackageImageFacts.FunctionTemplates.TryGetValue("Facade.Forward", out var forward));
            Assert.NotNull(forward.TypedBody);
            Assert.NotNull(forward.OptimizationSummary);
            Assert.True(forward.OptimizationSummary!.IsSingleReturnDirectCallForwarder);
            Assert.Contains("Facade.Read", forward.CalledFunctions);
            var directCall = Assert.Single(forward.DirectCalls);
            Assert.Equal(0, directCall.Ordinal);
            Assert.Equal("Facade.Read", directCall.Signature.Name);
            Assert.Equal("T", directCall.Signature.ReturnType.NamedType);
            var parameter = Assert.Single(directCall.Signature.Parameters);
            Assert.Equal("box", parameter.Name);
            Assert.Contains("Facade.Box", parameter.Type.DisplayName, StringComparison.Ordinal);
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

    private sealed class CountingSourceModuleResolver : IModuleSourceResolver
    {
        private readonly Dictionary<string, (ResolvedModuleReference Reference, string SourceText)> _modules;
        private readonly Dictionary<string, int> _sourceLoadCounts = new(StringComparer.Ordinal);

        public CountingSourceModuleResolver(params (string ModuleName, string SourceText)[] modules)
        {
            _modules = modules.ToDictionary(
                static module => module.ModuleName,
                static module =>
                {
                    var filePath = Path.Combine("/virtual", module.ModuleName.Replace('.', Path.DirectorySeparatorChar) + ".stark");
                    return (
                        new ResolvedModuleReference(module.ModuleName, filePath, IsExternal: false),
                        module.SourceText);
                },
                StringComparer.Ordinal);
        }

        public int GetSourceLoadCount(string moduleName)
        {
            return _sourceLoadCounts.TryGetValue(moduleName, out var count) ? count : 0;
        }

        public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
        {
            if (_modules.TryGetValue(moduleName, out var entry))
            {
                module = entry.Reference;
                return true;
            }

            module = default!;
            return false;
        }

        public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
        {
            if (_modules.TryGetValue(module.ModuleName, out var entry))
            {
                _sourceLoadCounts[module.ModuleName] = GetSourceLoadCount(module.ModuleName) + 1;
                sourceText = entry.SourceText;
                filePath = entry.Reference.FilePath;
                return true;
            }

            sourceText = string.Empty;
            filePath = null;
            return false;
        }
    }
}
