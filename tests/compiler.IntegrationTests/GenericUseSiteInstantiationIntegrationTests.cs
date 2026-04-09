using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class GenericUseSiteInstantiationIntegrationTests
{
    [Fact]
    public void ManifestBackedNestedGenericTypePlanningDiscoversNestedLayoutsFromImportedUseSites()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-nested-generic-layout-integration-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Wrapper<T>(T Value) { }
                public record Envelope<T>(Wrapper<T> Wrapped) { }
                public record Crate<T>(Envelope<T> Envelope) { }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Crate<i32> crate) {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Facade_Crate__i32");
            Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Facade_Envelope__i32");
            Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Facade_Wrapper__i32");
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
    public void ManifestBackedRecursiveGenericPlanningFallsBackToPublishedCallSummariesWithoutDeferredFunctionTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-call-summary-function-fallback-integration-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }

                public fn T Relay<T>(T value) {
                    return Forward(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var typedOnlyManifest = BuildTypedOnlyFacadeManifest(
                manifest,
                template => StripDeferredInstantiations(template) with
                {
                    BodyText = template.QualifiedResolvedName switch
                    {
                        "Facade.Forward" => "{\n    return value;\n}",
                        "Facade.Relay" => "{\n    return value;\n}",
                        _ => template.BodyText
                    }
                });

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.Relay(left) + Facade.Relay(right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Equal(3, plan.Functions.Count);
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Identity__i32");
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
    public void ManifestBackedGenericTypePlanningFallsBackToPublishedTemplateFactsWithoutDeferredTypeTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-template-facts-type-fallback-integration-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<A, B>(A First, B Second) { }

                public fn Pair<T, bool> MakePair<T>(T value, bool flag) {
                    stack Pair<T, bool> pair = new Pair<T, bool>(value, flag);
                    return pair;
                }

                public fn i32 Relay<T>(T value, bool flag) {
                    stack Pair<T, bool> pair = MakePair(value, flag);
                    return pair.Second ? 1 : 0;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var typedOnlyManifest = BuildTypedOnlyFacadeManifest(
                manifest,
                template => StripDeferredInstantiations(template) with
                {
                    BodyText = template.QualifiedResolvedName == "Facade.Relay"
                        ? "{\n    return flag ? 1 : 0;\n}"
                        : template.BodyText
                });

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value, bool flag) {
                        return Facade.Relay(value, flag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_MakePair__i32");
            Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Facade_Pair__i32__bool");
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

    private static StarkPackageManifest BuildTypedOnlyFacadeManifest(
        StarkPackageManifest manifest,
        Func<StarkPackageFunctionTemplateManifest, StarkPackageFunctionTemplateManifest> rewriteTemplate)
    {
        var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
        Assert.NotNull(facadeModule.GenericTemplates);

        var rewrittenTemplates = facadeModule.GenericTemplates!.Functions
            .Select(rewriteTemplate)
            .ToArray();

        return manifest with
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
                        GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates)
                    }
                    : module)
                .ToArray()
        };
    }

    private static StarkPackageFunctionTemplateManifest StripDeferredInstantiations(StarkPackageFunctionTemplateManifest template)
    {
        return template with
        {
            DeferredFunctionInstantiations = [],
            DeferredTypeInstantiations = []
        };
    }

    private static StarkPackageModuleManifest WithEffectiveLegacyCompilerSectionCopies(StarkPackageModuleManifest module)
    {
        return module with
        {
            TypedInterface = module.EffectiveTypedInterface,
            CompilerFacts = module.EffectiveCompilerFacts,
            GenericTemplates = module.EffectiveGenericTemplates
        };
    }
}
