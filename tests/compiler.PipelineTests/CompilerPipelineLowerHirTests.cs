using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineLowerHirTests
{
    [Fact]
    public void ManifestBackedGenericFunctionsMaterializeConcreteBodiesFromPackageImageTemplates()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.TypedInterface);
            Assert.NotNull(facadeModule.CompilerFacts);
            Assert.NotNull(facadeModule.GenericTemplates);

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
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-hir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            var importedIdentity = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Identity");
            Assert.True(importedIdentity.Function!.HasBody);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);

            var monomorphized = Assert.Single(
                hir.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(monomorphized.HasBody);
            Assert.Equal(FunctionBodyLoweringKind.StarkCfg, monomorphized.BodyLoweringKind);
            Assert.Equal("Facade.Identity", monomorphized.Signature.TemplateName);
            Assert.Equal("Facade.Identity", monomorphized.BodyTemplateName);
            Assert.NotNull(monomorphized.GenericTypeSubstitution);
            Assert.True(monomorphized.GenericTypeSubstitution!.TryGetValue("T", out var substitutedType));
            Assert.Equal("i32", substitutedType.DisplayName);
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
    public void NestedGenericCallsMaterializeTransitiveSpecializationsIntoHighLevelIr()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn T Forward<T>(T value) {
                    return Identity(value);
                }

                fn i32 Run(i32 value) {
                    return Forward(value);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(StopAfterPassId: "lower-hir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);

        Assert.Contains(hir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Forward__i32");
        Assert.Contains(hir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
    }


    [Fact]
    public void LowerHirMaterializesSourceBackedMonomorphizedGenericFunctions()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-hir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);

        var monomorphized = Assert.Single(
            hir.Functions,
            static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
        Assert.True(monomorphized.HasBody);
        Assert.Equal(FunctionBodyLoweringKind.StarkCfg, monomorphized.BodyLoweringKind);
        Assert.Equal("Identity", monomorphized.Signature.TemplateName);
        Assert.Equal("Identity", monomorphized.BodyTemplateName);
        Assert.Equal("i32", monomorphized.Signature.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(monomorphized.Signature.Parameters).Type.DisplayName);
        Assert.NotNull(monomorphized.GenericTypeSubstitution);
        Assert.True(monomorphized.GenericTypeSubstitution!.TryGetValue("T", out var substitutedType));
        Assert.Equal("i32", substitutedType.DisplayName);
    }


    [Fact]
    public void LowerMirUsesPublishedTemplateObjectCreationFactsForImportedGenericBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-template-object-creations-lower-mir-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libDemo.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Demo.lib" : "libDemo.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var moduleSource =
                """
                module Demo

                public record Counter(i32 Value) { }

                public fn i32 MakeFlag<T>(T value) {
                    stack Counter counter = new Counter(0);
                    return 1;
                }

                fn i32 Run(i32 value) {
                    return MakeFlag(value);
                }
                """;
            var manifestResult = pipeline.Run(new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")));
            Assert.True(manifestResult.Succeeded, string.Join(", ", manifestResult.Diagnostics.Select(static d => d.ToString())));
            var manifest = PackageImageBuilder.Create(manifestResult, libraryPath);
            var demoModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Demo");

            Assert.True(
                PackageImageLoader.TryBuildLoadedPackageImageFacts(
                    new ResolvedPackageModule(manifestPath, libraryPath, manifest, demoModule),
                    out var packageImageFacts));

            var result = pipeline.Run(
                new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(StopAfterPassId: "lower-hir"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));

            var remappedFunctionTemplates = packageImageFacts.FunctionTemplates.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            if (packageImageFacts.FunctionTemplates.TryGetValue("Demo.MakeFlag", out var makeFlagTemplate))
            {
                remappedFunctionTemplates["MakeFlag"] = makeFlagTemplate with
                {
                    ObjectCreationSummaries = makeFlagTemplate.ObjectCreations
                        .Select(static objectCreation => objectCreation with
                        {
                            CreatedType = string.Equals(objectCreation.CreatedType.NamedType, "Demo.Counter", StringComparison.Ordinal)
                                ? StarkTypeSymbols.Named("Counter")
                                : objectCreation.CreatedType
                        })
                        .ToArray()
                };
            }

            var remappedPackageImageFacts = packageImageFacts with { FunctionTemplates = remappedFunctionTemplates };
            Assert.True(remappedPackageImageFacts.FunctionTemplates.TryGetValue("MakeFlag", out var importedTemplate));
            Assert.Single(importedTemplate.Constructors);

            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
            Assert.NotNull(moduleGraph);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);

            var prunedTypeModel = typeModel with
            {
                ObjectCreations = typeModel.ObjectCreations
                    .Where(static record => !string.Equals(record.EnclosingFunctionName, "MakeFlag", StringComparison.Ordinal))
                    .ToArray()
            };

            Assert.DoesNotContain(
                prunedTypeModel.ObjectCreations,
                static record => string.Equals(record.EnclosingFunctionName, "MakeFlag", StringComparison.Ordinal));

            var rootModule = Assert.Single(loadedModules.Modules.Values, static module => module.Reference.IsRoot);
            var modulesWithPackageImage = loadedModules.Modules.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            modulesWithPackageImage["PkgFacts"] = rootModule with
            {
                Reference = new ResolvedModuleReference(
                    "PkgFacts",
                    rootModule.Reference.FilePath,
                    IsExternal: false,
                    IsRoot: false),
                PackageImageFacts = remappedPackageImageFacts
            };
            var loadedModulesWithPackageImage = loadedModules with { Modules = modulesWithPackageImage };

            var state = new CompilationState(
                new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(StopAfterPassId: "lower-hir"));
            var lowerer = new MidLevelIrLowerer(new CompilerPassContext(state), loadedModulesWithPackageImage, moduleGraph, prunedTypeModel, enumLayoutModel);
            var mir = lowerer.Lower(hir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__MakeFlag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue);
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
}
