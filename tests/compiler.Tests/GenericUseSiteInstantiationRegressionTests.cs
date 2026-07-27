using Stark.Compiler;

namespace compiler.Tests;

public sealed class GenericUseSiteInstantiationRegressionTests
{
    [Fact]
    public void NestedGenericTypeLayoutsAreDiscoveredFromSourceUseSites()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Wrapper<T>(T Value)
                {
                }
                record Envelope<T>(Wrapper<T> Wrapped)
                {
                }
                record Crate<T>(Envelope<T> Envelope)
                {
                }

                fn i32[min max] Run(Crate<i32[min max]> crate)
                {
                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Crate__i32");
        Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Envelope__i32");
        Assert.Contains(plan.Types, static type => type.SymbolName == "__stark_mono_ty_Demo__Wrapper__i32");
    }

    [Fact]
    public void ImportedSourceOnlyInstantiationsMaterializeFromBodyUseSites()
    {
        // Regression: instantiations referenced only inside imported source module
        // bodies (never named by the root) must still be materialized and planned;
        // type checking skips imported non-generic bodies, so these come from the
        // imported-source instantiation walk (TypeChecking.MaterializeImportedSourceInstantiations).
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-only-instantiation-");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "Demo"));
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Demo", "Lib.stark"),
                """
                module Demo.Lib

                public enum InnerError
                {
                    Bad,
                }

                public enum GenericResult<T>
                {
                    [Ok] Ok(T),
                    [Err] Err(InnerError),
                }

                public record Pair<T>(T First, T Second)
                {
                }

                fn GenericResult<i32[min max]> ProduceGeneric(i32[min max] value)
                {
                    if (value < 0)
                    {
                        return GenericResult<i32[min max]>.Err(InnerError.Bad);
                    }

                    return GenericResult<i32[min max]>.Ok(value);
                }

                fn i64[min max] SumPair(i32[min max] value)
                {
                    stack Pair<i64[min max]> pair = new Pair<i64[min max]>((i64[min max])value, 2);
                    return pair.First + pair.Second;
                }

                public fn i32[min max] Consume(i32[min max] value)
                {
                    switch (ProduceGeneric(value))
                    {
                        case GenericResult<i32[min max]>.Err(var error):
                            return -1;
                        case GenericResult<i32[min max]>.Ok(var ok):
                            return ok + (i32[min max])SumPair(0);
                    }
                }
                """);
            var appSource =
                """
                import Demo.Lib
                module Demo.App

                export fn i32[min max] main()
                {
                    return Demo.Lib.Consume(3) - 5;
                }
                """;
            var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
            File.WriteAllText(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Contains(plan!.Types, static type => type.InstantiatedTypeName == "Demo.Lib.GenericResult<i32>");
            Assert.Contains(plan.Types, static type => type.InstantiatedTypeName == "Demo.Lib.Pair<i64>");

            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.Contains("@Demo_Lib_Consume(", llvmModule!.Text, StringComparison.Ordinal);
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
    public void ManifestBackedGenericMethodsAndNestedGenericTypesMaterializeFromPublishedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-method-nested-generic-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value)
                {
                }

                public record Box(i32[min max] Dummy)
                {
                    fn Pair<T> MakePair<T>(borrow Box self, T value)
                    {
                        stack Pair<T> pair = new Pair<T>(value);
                        return pair;
                    }
                }

                public fn Pair<T> Relay<T>(Box box, T value)
                {
                    return box.MakePair(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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
                            GenericTemplates = facadeModule.GenericTemplates
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

                    fn Facade.Pair<i32[min max]> Run(Facade.Box box, i32[min max] value)
                    {
                        return Facade.Relay(box, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            Assert.Contains(
                plan.Functions,
                static function => function.TemplateName == "Facade.Box.MakePair"
                    && function.TypeArguments.Select(static type => type.DisplayName).SequenceEqual(["i32"])
                    && function.OwnerModuleName == "Demo");

            Assert.Contains(
                plan.Functions,
                static function => function.TemplateName == "Facade.Relay"
                    && function.TypeArguments.Select(static type => type.DisplayName).SequenceEqual(["i32"])
                    && function.OwnerModuleName == "Demo");

            Assert.Contains(
                plan.Types,
                static type => type.TemplateName == "Facade.Pair"
                    && type.InstantiatedTypeName == "Facade.Pair<i32>"
                    && type.SymbolName == "__stark_mono_ty_Demo__Facade_Pair__i32");
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
    public void ManifestBackedRecursiveGenericPlanningFallsBackToPublishedCallSummariesWhenDeferredFunctionTriggersAreMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-call-summary-generic-function-fallback-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    return value;
                }

                public fn T Forward<T>(T value)
                {
                    return Identity(value);
                }

                public fn T Relay<T>(T value)
                {
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

                    fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
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
    public void ManifestBackedGenericTypePlanningFallsBackToPublishedTemplateFactsWhenDeferredTypeTriggersAreMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-template-facts-generic-type-fallback-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<A, B>(A First, B Second)
                {
                }

                public fn Pair<T, bool> MakePair<T>(T value, bool flag)
                {
                    stack Pair<T, bool> pair = new Pair<T, bool>(value, flag);
                    return pair;
                }

                public fn i32[min max] Relay<T>(T value, bool flag)
                {
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

                    fn i32[min max] Run(i32[min max] value, bool flag)
                    {
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

    [Fact]
    public void ManifestBackedDictionaryKeyConstraintRejectsUnprovenKeyTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-dictionary-key-constraint-");
        var systemDirectory = Path.Combine(tempDirectory.FullName, "System");
        var collectionsPath = Path.Combine(systemDirectory, "Collections.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libSystemCollections.starkpkg.json");

        try
        {
            Directory.CreateDirectory(systemDirectory);

            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module System.Collections

                public struct Dictionary<K, V>
                {
                    K Key;
                    V Value;
                }
                """,
                collectionsPath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "SystemCollections.lib" : "libSystemCollections.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(collectionsPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import System.Collections
                    module Demo

                    struct Box
                    {
                        u32[0 2 ** 31 - 1] Value;
                    }

                    fn void Use(Dictionary<Box, u32[0 2 ** 31 - 1]> boxes)
                    {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(consumerResult.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
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
