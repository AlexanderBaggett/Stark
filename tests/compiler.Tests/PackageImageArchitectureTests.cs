using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageArchitectureTests
{
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
