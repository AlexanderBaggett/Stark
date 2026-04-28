using Stark.Compiler;
using Stark.Parsing;

namespace compiler.Tests;

public sealed class PackageImageArchitectureTests
{
    [Fact]
    public void PackageImagePreservesBackendOpaqueModuleBoundary()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-backend-opaque-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    [Backend(Opaque)]
                    module Facade

                    public fn i32[-2147483648 2147483647] Identity(i32[-2147483648 2147483647] value) {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Equal("opaque", facadeModule.CompilerSections?.CompilerFacts?.BackendOptimizationMode);
            Assert.Contains("\"BackendOptimizationMode\": \"opaque\"", manifest.ToJson(), StringComparison.Ordinal);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("[Backend(Opaque)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, syntaxModel.BackendOptimizationMode);
            var attribute = Assert.Single(syntaxModel.ModuleAttributes ?? []);
            Assert.Equal("Backend", attribute.Name);
            Assert.Equal(["Opaque"], attribute.Arguments);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, facts.BackendOptimizationMode);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedModule, out var importedDocument));
            Assert.False(CompilerCli.ShouldEnableDependencyLto(importedDocument));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesFineGrainedBackendOpaqueBoundaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-fine-backend-opaque-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    [Backend(Opaque)]
                    public finite law i32[-2147483648 2147483647] Identity(i32[-2147483648 2147483647] value) {
                        return value;
                    }

                    [Backend(Opaque)]
                    public finite law T Echo<T>(T value) {
                        return value;
                    }

                    [Backend(Opaque)]
                    public struct Box {
                        i32[-2147483648 2147483647] Value;

                        public finite law i32[-2147483648 2147483647] Read(borrow Box self) {
                            return self.Value;
                        }
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            var compilerFacts = facadeModule.CompilerSections?.CompilerFacts;
            Assert.NotNull(typedInterface);
            Assert.NotNull(compilerFacts);

            var identity = Assert.Single(typedInterface!.Functions, static function => function.Name == "Identity");
            Assert.Equal("opaque", identity.BackendOptimizationMode);
            var echo = Assert.Single(typedInterface.Functions, static function => function.Name == "Echo");
            Assert.Equal("opaque", echo.BackendOptimizationMode);

            var box = Assert.Single(typedInterface.Types, static type => type.Name == "Box");
            Assert.Equal("opaque", box.BackendOptimizationMode);
            var read = Assert.Single(box.Methods ?? [], static method => method.Name == "Read");
            Assert.Equal("opaque", read.BackendOptimizationMode);
            var identityEffects = Assert.Single(
                compilerFacts!.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Identity");
            Assert.Equal("opaque", identityEffects.BackendOptimizationMode);
            var echoEffects = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Echo");
            Assert.Equal("opaque", echoEffects.BackendOptimizationMode);
            var readEffects = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Box.Read");
            Assert.Equal("opaque", readEffects.BackendOptimizationMode);
            var echoTemplate = Assert.Single(
                facadeModule.CompilerSections?.GenericTemplates?.Functions ?? [],
                static function => function.QualifiedResolvedName == "Facade.Echo");
            Assert.Equal("opaque", echoTemplate.BackendOptimizationMode);

            Assert.Contains("\"BackendOptimizationMode\": \"opaque\"", manifest.ToJson(), StringComparison.Ordinal);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("[Backend(Opaque)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
            var importedIdentity = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Identity");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedIdentity.Function!.BackendOptimizationMode);
            var importedEcho = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Echo");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedEcho.Function!.BackendOptimizationMode);
            var importedBox = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedBox.BackendOptimizationMode);
            var importedRead = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box.Read");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedRead.Function!.BackendOptimizationMode);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(
                ModuleBackendOptimizationMode.Opaque,
                facts.FunctionTemplates["Facade.Echo"].BackendOptimizationMode);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void NonOpaqueSourceDependencyCanParticipateInLto()
    {
        var parseResult = StarkSyntax.ParseCompilationUnit("module Helpers");
        var syntaxModel = SyntaxModelFactory.Create(parseResult);
        var document = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "Helpers",
                "/virtual/Helpers.stark",
                IsExternal: false,
                IsRoot: false),
            parseResult,
            syntaxModel);

        Assert.True(CompilerCli.ShouldEnableDependencyLto(document));
    }

    [Fact]
    public void SystemCollectionsSourceUsesBackendOpaqueInsteadOfCompilerNameGate()
    {
        var repositoryRoot = FindRepositoryRoot();
        var collectionsPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Collections.stark");
        var parseResult = StarkSyntax.ParseCompilationUnit(File.ReadAllText(collectionsPath));
        var syntaxModel = SyntaxModelFactory.Create(parseResult);

        Assert.Equal("System.Collections", syntaxModel.ModuleName);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, syntaxModel.BackendOptimizationMode);

        var nonOpaqueCollections = StarkSyntax.ParseCompilationUnit("module System.Collections");
        var nonOpaqueSyntaxModel = SyntaxModelFactory.Create(nonOpaqueCollections);
        var nonOpaqueDocument = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "System.Collections",
                "/virtual/System/Collections.stark",
                IsExternal: false,
                IsRoot: false),
            nonOpaqueCollections,
            nonOpaqueSyntaxModel);

        Assert.True(CompilerCli.ShouldEnableDependencyLto(nonOpaqueDocument));
    }

    [Fact]
    public void PackageImagePreservesFfiVarargsFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-varargs-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public ffi varargs fn i32[min max] printf(ascii format);
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var function = Assert.Single(facadeModule.CompilerSections?.TypedInterface?.Functions ?? []);
            var effect = Assert.Single(facadeModule.CompilerSections?.CompilerFacts?.FunctionEffects ?? []);
            var abiFunction = Assert.Single(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions ?? []);

            Assert.True(function.IsVarargs);
            Assert.True(effect.IsVarargs);
            Assert.True(abiFunction.IsVarargs);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("public ffi varargs fn i32[", sourceText, StringComparison.Ordinal);
            Assert.Contains("printf(ascii format);", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesUnsignedIntegerFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-unsigned-integers-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Bytes.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Bytes

                    public fn u8[0 127] Keep(u8[min 127] value) {
                        return value;
                    }

                    public fn u32[0 max] Keep32(u32[0 max] value) {
                        return value;
                    }

                    public fn u96[0 max] Keep96(u96[0 max] value) {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Bytes.lib" : "libBytes.a"));
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Bytes");
            var functions = module.CompilerSections?.TypedInterface?.Functions ?? [];
            var function = Assert.Single(functions, static item => item.Name == "Keep");
            var function32 = Assert.Single(functions, static item => item.Name == "Keep32");
            var function96 = Assert.Single(functions, static item => item.Name == "Keep96");

            Assert.True(function.ReturnType.IsUnsigned);
            Assert.True(function.Parameters[0].Type.IsUnsigned);
            Assert.True(function32.ReturnType.IsUnsigned);
            Assert.True(function32.Parameters[0].Type.IsUnsigned);
            Assert.True(function96.ReturnType.IsUnsigned);
            Assert.True(function96.Parameters[0].Type.IsUnsigned);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("public fn u8[0 127] Keep(u8[0 127] value)", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn u32[0 4294967295] Keep32(u32[0 4294967295] value)", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn u96[0 79228162514264337593543950335] Keep96(u96[0 79228162514264337593543950335] value)", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBuilderPublishesTypedInterfaceImportsAsStructuredDependencySurface()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-imports-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Bits.stark"),
                """
                module Bits

                public record Token(i32[-2147483648 2147483647] value) {
                }
                """);
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public fn i32[-2147483648 2147483647] Id(i32[-2147483648 2147483647] value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    export import Bits
                    module Facade

                    public fn Bits.Token Forward(Bits.Token value) {
                        return value;
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterfaceImports = facadeModule.CompilerSections?.TypedInterface?.Imports;

            Assert.NotNull(typedInterfaceImports);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Math" && !import.IsExported);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Bits" && import.IsExported);
            Assert.Null(facadeModule.Imports);
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
    public void PackageImageBuilderPublishesInternalDependencyImportsNeededByImportedBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-internal-imports-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Runtime.stark"),
                """
                module Runtime

                internal fn i32[-2147483648 2147483647] Hidden() {
                    return 7;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Runtime
                    module Facade

                    public fn i32[-2147483648 2147483647] Run() {
                        return Runtime.Hidden();
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterfaceImports = facadeModule.CompilerSections?.TypedInterface?.Imports;

            Assert.NotNull(typedInterfaceImports);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Runtime" && !import.IsExported);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                    Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                    manifest,
                    facadeModule),
                out var sourceText));
            Assert.Contains("import Runtime", sourceText, StringComparison.Ordinal);
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
    public void PackageImageBuilderPublishesLinkageMetadataForModuleObjectSelection()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-linkage-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Runtime.stark"),
                """
                module Runtime

                internal fn i32[-2147483648 2147483647] Hidden() {
                    return 7;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Runtime
                    module Facade

                    public fn i32[-2147483648 2147483647] Run() {
                        return Runtime.Hidden();
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var runtimeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Runtime");
            var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";

            var facadeLinkage = facadeModule.CompilerSections?.CompilerFacts?.Linkage;
            Assert.NotNull(facadeLinkage);
            Assert.Equal($"root{objectExtension}", facadeLinkage!.ObjectFileName);
            Assert.Contains("Facade_Run", facadeLinkage.DefinedSymbols);
            Assert.Contains("Runtime_Hidden", facadeLinkage.ReferencedSymbols ?? []);

            var runtimeLinkage = runtimeModule.CompilerSections?.CompilerFacts?.Linkage;
            Assert.NotNull(runtimeLinkage);
            Assert.Equal($"Runtime{objectExtension}", runtimeLinkage!.ObjectFileName);
            Assert.Contains("Runtime_Hidden", runtimeLinkage.DefinedSymbols);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(
                new ResolvedPackageModule(
                    Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                    libraryPath,
                    manifest,
                    facadeModule),
                out var facadeFacts));
            Assert.Equal($"root{objectExtension}", facadeFacts.Linkage?.ObjectFileName);
            Assert.Contains("Runtime_Hidden", facadeFacts.Linkage?.ReferencedSymbols ?? new HashSet<string>(StringComparer.Ordinal));
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
    public void PackageImagePreservesConstNumericStorageWithoutReconstructingScalarRanges()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-const-numeric-storage-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public const Small = 80;
                    public const Big = 2**16;
                    public const Float64 = 80.0;
                    public const Float32 = 80.0f;
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedGlobals = module.EffectiveTypedInterface!.Globals;

            AssertConstIntegerType(typedGlobals, "Small", 8, "80", "80");
            AssertConstIntegerType(typedGlobals, "Big", 24, "65536", "65536");
            AssertConstFloatType(typedGlobals, "Float64", 64);
            AssertConstFloatType(typedGlobals, "Float32", 32);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));

            Assert.Contains("public const i8 Small = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const i24 Big = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const f64 Float64 = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const f32 Float32 = 0;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("const i8[80 80]", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("const i24[65536 65536]", sourceText, StringComparison.Ordinal);
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
    public void StructuredPackageImageSourceIgnoresCorruptedBodyTextWhenTypedBodyFactsExist()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-corrupt-body-text-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public fn T Identity<T>(T value) {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var templates = module.EffectiveGenericTemplates!.Functions
                .Select(static template => template.QualifiedResolvedName == "Facade.Identity"
                    ? template with { BodyText = "{ return this is not valid Stark; }" }
                    : template)
                .ToArray();
            var corruptedTemplates = new StarkPackageGenericTemplateSection(templates);
            var corruptedModule = module with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = module.CompilerSections is null
                    ? null
                    : module.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            var identityTemplate = Assert.Single(
                corruptedModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Identity");
            Assert.NotNull(identityTemplate.TypedBody);

            Assert.True(PackageImageLoader.TryBuildStructuredModuleDocument(
                new ResolvedPackageModule(manifestPath, libraryPath, corruptedManifest, corruptedModule),
                out var document));

            Assert.Contains("public fn T Identity<T>(T value);", document.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("this is not valid Stark", document.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value", document.ParseResult.SourceText, StringComparison.Ordinal);
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
    public void PackageImageLoaderPrefersTypedInterfaceImportsOverExplicitSourceSurfaceImports()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: [],
                Imports:
                [
                    new StarkPackageImportManifest("TypedDep", IsExported: false)
                ]),
            GenericTemplates: new StarkPackageGenericTemplateSection(
                [
                    new StarkPackageFunctionTemplateManifest(
                        QualifiedResolvedName: "Facade.Identity#(i32)",
                        QualifiedName: "Facade.Identity",
                        OverloadKey: "(i32)",
                        BodyText: "{ return value; }")
                ]),
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Imports:
                [
                    new StarkPackageImportManifest("LegacyDep", IsExported: false)
                ],
                ReExports: [],
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: []));

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));

        var typedImport = Assert.Single(syntaxModel.Imports);
        Assert.Equal("TypedDep", typedImport.ModuleName);
        Assert.False(typedImport.IsReExport);
        Assert.Contains("import TypedDep", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("import LegacyDep", sourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImageLoaderFallsBackToLegacyFlatImportsWhenTypedInterfaceImportsAreMissing()
    {
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
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
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
                Globals: []),
            Imports:
            [
                new StarkPackageImportManifest("LegacyMath", IsExported: false)
            ],
            SourceSurface: null);

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
        Assert.Contains(syntaxModel.Imports, static import => import.ModuleName == "LegacyMath" && !import.IsReExport);
        Assert.Contains("import LegacyMath", sourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImageLoaderLegacyFlatImportsDoNotHideLegacyReExports()
    {
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
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: []),
            Imports: [],
            SourceSurface: null);

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
        Assert.Contains(syntaxModel.Imports, static import => import.ModuleName == "Bits" && import.IsReExport);
        Assert.Contains("export import Bits", sourceText, StringComparison.Ordinal);
    }

    private static ResolvedPackageModule CreateResolvedPackageModule(StarkPackageModuleManifest module)
    {
        return new ResolvedPackageModule(
            $"/virtual/{module.ModuleName}.starkpkg.json",
            $"/virtual/lib{module.ModuleName}.a",
            new StarkPackageManifest(module.ModuleName, $"lib{module.ModuleName}.a", [module]),
            module);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root.");
    }

    private static void AssertConstIntegerType(
        IReadOnlyList<StarkPackageTypedGlobalManifest> globals,
        string name,
        int bitWidth,
        string rangeMin,
        string rangeMax)
    {
        var global = Assert.Single(globals, item => item.Name == name);

        Assert.Equal("globalconstant", global.Kind);
        Assert.Equal("integer", global.Type.Kind);
        Assert.Equal(bitWidth, global.Type.BitWidth);
        Assert.Equal(rangeMin, global.Type.RangeMin);
        Assert.Equal(rangeMax, global.Type.RangeMax);
    }

    private static void AssertConstFloatType(
        IReadOnlyList<StarkPackageTypedGlobalManifest> globals,
        string name,
        int bitWidth)
    {
        var global = Assert.Single(globals, item => item.Name == name);

        Assert.Equal("globalconstant", global.Kind);
        Assert.Equal("float", global.Type.Kind);
        Assert.Equal(bitWidth, global.Type.BitWidth);
    }
}
