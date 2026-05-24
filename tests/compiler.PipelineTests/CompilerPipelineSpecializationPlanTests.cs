using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineSpecializationPlanTests
{
    [Fact]
    public void RootGenericInstantiationsPreferOwnedConcreteBodiesInSpecializationPlan()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value)
                {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value)
                {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);
        Assert.Equal(
            new[] { FunctionSpecializationStrategy.OwnedConcreteBody },
            function.SelectionOrder);
        Assert.Equal(FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody, function.CodeGenerationMode);
    }


    [Fact]
    public void SourceBackedImportedLawGenericsPreferCallerCloneBeforeOwnedBodyInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-specialization-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers
                {
                    finite law T Identity<T>(T value)
                    {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value)
                    {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal("__stark_mono_fn_Math__Math_Numbers_Identity__i32", function.SymbolName);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.LawCallerSpecializedClone,
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);
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
    public void SourceBackedImportedTopLevelLawGenericsPreferCallerCloneBeforeOwnedBodyInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-top-level-generic-specialization-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public finite law T Identity<T>(T value)
                {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value)
                    {
                        return Math.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal("__stark_mono_fn_Math__Math_Identity__i32", function.SymbolName);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.LawCallerSpecializedClone,
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);
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
    public void ColdImportedLawGenericsPreferAbiFallbackOverCallerCloneInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cold-source-generic-specialization-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers
                {
                    cold finite law T Identity<T>(T value)
                    {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value)
                    {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.AbiBoundaryOnly, function.CodeGenerationMode);
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
    public void ManifestBackedImportedLawGenericsPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-law-generic-template-semantics-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libMath.starkpkg.json");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Math

                public doctrine Numbers
                {
                    law i32[min max] AddTag<T>(i32[min max] left, i32[min max] right, T tag)
                    {
                        return left + right;
                    }
                }
                """,
                mathPath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Math.lib" : "libMath.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Math"
                        ? WithEffectiveLegacyCompilerSectionCopies(module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                }
                            }
                        })
                        : module)
                    .ToArray()
            };
            Assert.Empty(Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Math").EffectiveCompilerFacts?.FunctionSemantics ?? []);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(mathPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    law i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Math.Numbers.AddTag(left, right, left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Math", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Math.Numbers.AddTag", out var importedSemantics));
            Assert.Empty(importedSemantics.CalledFunctions);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.LawCallerSpecializedClone,
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);
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
    public void ManifestBackedImportedPlainFnGenericsThatStrengthenToLawPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-plain-fn-generic-template-semantics-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libMath.starkpkg.json");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Math

                public fn i32[min max] AddTag<T>(i32[min max] left, i32[min max] right, T tag)
                {
                    return left + right;
                }
                """,
                mathPath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Math.lib" : "libMath.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Math"
                        ? WithEffectiveLegacyCompilerSectionCopies(module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                }
                            }
                        })
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(mathPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    law i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Math.AddTag(left, right, left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Math", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Math.AddTag", out var importedSemantics));
            Assert.Equal(StarkFunctionKind.Fn, importedSemantics.DeclaredKind);
            Assert.Equal(StarkFunctionKind.FiniteLaw, importedSemantics.EffectiveKind);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.LawCallerSpecializedClone,
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
            Assert.NotNull(effectModel);
            var effects = effectModel.Functions["Math.AddTag"];
            Assert.Equal(StarkFunctionKind.FiniteLaw, effects.Kind);
            Assert.True(effects.IsPure);
            Assert.True(effects.NoSync);
            Assert.True(effects.NoFree);
            Assert.True(effects.WillReturn);
            Assert.True(effects.MustProgress);
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
    public void ManifestBackedRecursiveImportedLawGenericsDoNotPreferCallerCloneWhenTemplateSemanticsSurviveWithoutFunctionSemantics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-recursive-law-generic-template-semantics-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libMath.starkpkg.json");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Math

                public doctrine Numbers
                {
                    law i32[min max] CountDown<T>(i32[min max] value, T tag)
                    {
                        if (value == 0)
                        {
                            return 0;
                        }

                        return Numbers.CountDown(value - 1, tag);
                    }
                }
                """,
                mathPath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Math.lib" : "libMath.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Math"
                        ? WithEffectiveLegacyCompilerSectionCopies(module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                }
                            }
                        })
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(mathPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    law i32[min max] Run(i32[min max] value)
                    {
                        return Math.Numbers.CountDown(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Math", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Math.Numbers.CountDown", out var importedSemantics));
            Assert.Contains("Math.Numbers.CountDown", importedSemantics.CalledFunctions);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.DoesNotContain(FunctionSpecializationStrategy.LawCallerSpecializedClone, function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody, function.CodeGenerationMode);
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
    public void DeclarationOnlyImportedGenericInstantiationsFallBackToAbiOnlySpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-specialization-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[] { FunctionSpecializationStrategy.DirectAbiBoundaryFallback },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.AbiBoundaryOnly, function.CodeGenerationMode);
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
    public void ManifestBackedImportedGenericsWithoutPublishedAbiFactsPreferOwnedBodyOnlyInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-specialization-abi-facts-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

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
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.NotNull(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions);

            var abiStrippedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    AbiFunctions = []
                                }
                            }
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, abiStrippedManifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[] { FunctionSpecializationStrategy.LawCallerSpecializedClone, FunctionSpecializationStrategy.OwnedConcreteBody },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);
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
    public void ConflictingSpecializationSymbolPlansReportAmbiguityDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-specialization-plan-ambiguity-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers
                {
                    finite law T Identity<T>(T value)
                    {
                        return value;
                    }
                }

                public finite law T Numbers_Identity<T>(T value)
                {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] left = Math.Numbers_Identity(value);
                        return Math.Numbers.Identity(left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.False(result.Succeeded);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "STK4115"
                    && diagnostic.Stage == "specialization-plan"
                    && diagnostic.Message.Contains("__stark_mono_fn_Math__Math_Numbers_Identity__i32", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("Math.Numbers.Identity<i32>", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("Math.Numbers_Identity<i32>", StringComparison.Ordinal));
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "STK4116"
                    && diagnostic.Stage == "specialization-plan"
                    && diagnostic.Message.Contains("Math.Numbers.Identity<i32>", StringComparison.Ordinal));
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
