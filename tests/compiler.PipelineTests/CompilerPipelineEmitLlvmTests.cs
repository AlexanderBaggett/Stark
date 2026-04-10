using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineEmitLlvmTests
{
    [Fact]
    public void ManifestBackedColdNoInlineGenericInstantiationsPreserveTypedInterfaceModifiersWithoutCompilerFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-typed-interface-modifiers-generic-codegen-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public cold noinline fn T Choose<T>(T left, T right, bool takeRight) {
                    stack T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
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
                            CompilerFacts = null,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: null,
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
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("public cold noinline fn T Choose<T>(T left, T right, bool takeRight);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right, bool takeRight) {
                        return Facade.Choose(left, right, takeRight);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.Contains("public cold noinline fn T Choose<T>(T left, T right, bool takeRight);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Choose", out var importedTemplate));
            Assert.NotNull(importedTemplate.TypedBody);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Choose__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*cold[^\r\n]*noinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported specialization to keep cold/noinline attributes in emitted LLVM.");
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
    public void ManifestBackedImportedGenericSpecializationsUseTemplateSemanticAttributesWhenFunctionSemanticsAreMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-template-semantics-llvm-pipeline-");
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

                public fn void Touch<T>(borrow Box box, T tag) {
                    stack i32 copy = box.Value;
                    return;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run(borrow Facade.Box box, i32 value) {
                        Facade.Touch(box, value);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Touch", out var importedSemantics));
            Assert.NotNull(importedSemantics.MemoryEffects);
            Assert.True(importedSemantics.MemoryEffects!.ReadsArgumentMemory);
            Assert.True(Assert.Single(importedSemantics.Parameters!, static parameter => parameter.Name == "box").GuaranteedReadOnly);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var definition = System.Text.RegularExpressions.Regex.Match(
                llvmModule.Text,
                $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*\)[^\r\n]*",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            Assert.True(definition.Success, $"Expected a concrete LLVM definition for imported specialization '{function.SymbolName}'.");
            Assert.Contains("ptr nonnull noalias readonly nocapture", definition.Value, StringComparison.Ordinal);
            Assert.Contains("dereferenceable(4)", definition.Value, StringComparison.Ordinal);
            Assert.Contains("align 4", definition.Value, StringComparison.Ordinal);
            Assert.Contains("memory(argmem: read)", definition.Value, StringComparison.Ordinal);
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
    public void ManifestBackedImportedPlainFnGenericsThatStrengthenToLawEmitLawClonesWhenTemplateSemanticsSurviveWithoutFunctionSemantics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-plain-fn-generic-template-semantics-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libMath.starkpkg.json");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Math

                public fn i32 AddTag<T>(i32 left, i32 right, T tag) {
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

                    law i32 Run(i32 left, i32 right) {
                        return Math.AddTag(left, right, left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
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

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
            Assert.NotNull(effectModel);
            Assert.Equal(StarkFunctionKind.FiniteLaw, effectModel.Functions["Math.AddTag"].Kind);
            Assert.True(effectModel.Functions["Math.AddTag"].IsPure);
            Assert.True(effectModel.Functions["Math.AddTag"].NoSync);
            Assert.True(effectModel.Functions["Math.AddTag"].NoFree);
            Assert.True(effectModel.Functions["Math.AddTag"].WillReturn);
            Assert.True(effectModel.Functions["Math.AddTag"].MustProgress);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var cloneSymbol = $"__stark_law_clone_{function.SymbolName}";
            Assert.Contains($"define internal fastcc i32 @{cloneSymbol}", llvmModule.Text, StringComparison.Ordinal);
            Assert.Contains($"call fastcc i32 @{cloneSymbol}", llvmModule.Text, StringComparison.Ordinal);
            Assert.Contains($"define internal fastcc i32 @{function.SymbolName}", llvmModule.Text, StringComparison.Ordinal);
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
    public void ManifestBackedMemberCallForwarderGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-member-forwarder-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32 Value) {
                    fn i32 Bump(borrow Box self, i32 delta) {
                        return self.Value + delta;
                    }
                }

                public fn i32 Forward<T>(borrow Box box, i32 delta, T tag) {
                    return box.Bump(delta);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnMemberCallForwarder);
            Assert.Equal(0, publishedTemplate.Semantics.Optimization.DirectCallCount);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.MemberCallCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow Facade.Box box, i32 delta) {
                        return Facade.Forward(box, delta, delta);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Forward", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnMemberCallForwarder);
            Assert.Equal(1, importedSemantics.OptimizationSummary.MemberCallCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Forward");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Forward__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported member-call forwarder specialization to emit with alwaysinline.");
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
    public void ManifestBackedFieldAccessWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-field-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Inner(i32 Value) { }
                public record Box(Inner Inner) { }

                public fn i32 Read<T>(borrow Box box, T tag) {
                    return box.Inner.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Read");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnFieldAccessWrapper);
            Assert.Equal(2, publishedTemplate.Semantics.Optimization.FieldAccessCount);
            Assert.Equal(0, publishedTemplate.Semantics.Optimization.IndexAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow Facade.Box box) {
                        return Facade.Read(box, box.Inner.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Read", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnFieldAccessWrapper);
            Assert.Equal(2, importedSemantics.OptimizationSummary.FieldAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Read");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Read__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported field-access wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedIndexAccessWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-index-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32 Value) { }

                public fn i32 Read<T>(Box[2] boxes, i32 index, T tag) {
                    return boxes[index].Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Read");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnIndexAccessWrapper);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.FieldAccessCount);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.IndexAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Box[2] boxes, i32 index) {
                        return Facade.Read(boxes, index, index);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Read", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnIndexAccessWrapper);
            Assert.Equal(1, importedSemantics.OptimizationSummary.FieldAccessCount);
            Assert.Equal(1, importedSemantics.OptimizationSummary.IndexAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Read");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Read__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported index-access wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedConversionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-conversion-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Inner(i32 Value) { }
                public record Box(Inner Inner) { }

                public fn i64 Read<T>(borrow Box box, T tag) {
                    return (i64)box.Inner.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Read");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnConversionWrapper);
            Assert.Equal(2, publishedTemplate.Semantics.Optimization.FieldAccessCount);
            Assert.Equal(0, publishedTemplate.Semantics.Optimization.IndexAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i64 Run(borrow Facade.Box box) {
                        return Facade.Read(box, box.Inner.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Read", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnConversionWrapper);
            Assert.Equal(2, importedSemantics.OptimizationSummary.FieldAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Read");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported conversion wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedAddressOfWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-address-of-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[2] Values) { }

                public fn rawptr<i32> Pin<T>(borrow Buffer buffer, i32 index, T tag) {
                    return &buffer.Values[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Pin");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnAddressOfWrapper);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.FieldAccessCount);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.IndexAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn rawptr<i32> Run(borrow Facade.Buffer buffer, i32 index) {
                        return Facade.Pin(buffer, index, index);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Pin", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnAddressOfWrapper);
            Assert.Equal(1, importedSemantics.OptimizationSummary.FieldAccessCount);
            Assert.Equal(1, importedSemantics.OptimizationSummary.IndexAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Pin");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported address-of wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedBinaryOperatorWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-binary-operator-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Inner(i32 Value) { }
                public record Box(Inner Inner) { }

                public fn i32 AddDelta<T>(borrow Box box, i32 delta, T tag) {
                    return box.Inner.Value + delta;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.AddDelta");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnBinaryOperatorWrapper);
            Assert.False(publishedTemplate.Semantics.Optimization.IsSingleReturnComparisonWrapper);
            Assert.Equal(2, publishedTemplate.Semantics.Optimization.FieldAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow Facade.Box box, i32 delta) {
                        return Facade.AddDelta(box, delta, delta);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.AddDelta", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnBinaryOperatorWrapper);
            Assert.False(importedSemantics.OptimizationSummary.IsSingleReturnComparisonWrapper);
            Assert.Equal(2, importedSemantics.OptimizationSummary.FieldAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.AddDelta");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_AddDelta__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported binary-operator wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedComparisonWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-comparison-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Inner(i32 Value) { }
                public record Box(Inner Inner) { }

                public fn bool IsBelow<T>(borrow Box box, i32 limit, T tag) {
                    return box.Inner.Value < limit;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.IsBelow");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnComparisonWrapper);
            Assert.False(publishedTemplate.Semantics.Optimization.IsSingleReturnBinaryOperatorWrapper);
            Assert.Equal(2, publishedTemplate.Semantics.Optimization.FieldAccessCount);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn bool Run(borrow Facade.Box box, i32 limit) {
                        return Facade.IsBelow(box, limit, limit);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.IsBelow", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnComparisonWrapper);
            Assert.False(importedSemantics.OptimizationSummary.IsSingleReturnBinaryOperatorWrapper);
            Assert.Equal(2, importedSemantics.OptimizationSummary.FieldAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.IsBelow");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_IsBelow__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported comparison wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedTerminalIfSelectionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-terminal-if-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 ChooseBranch<T>(bool takeLeft, bool takeMiddle, i32 left, i32 middle, i32 right, T tag) {
                    if (takeLeft) {
                        return left;
                    } else if (takeMiddle) {
                        return middle;
                    } else {
                        return right;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.ChooseBranch");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsTerminalSelectionWrapper);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool takeLeft, bool takeMiddle, i32 left, i32 middle, i32 right) {
                        return Facade.ChooseBranch(takeLeft, takeMiddle, left, middle, right, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.ChooseBranch", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsTerminalSelectionWrapper);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.ChooseBranch");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_ChooseBranch__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported terminal-if selection wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedTerminalSwitchSelectionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-terminal-switch-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 ChooseSwitch<T>(i32 selector, i32 left, i32 middle, i32 right, T tag) {
                    switch (selector) {
                        case 0:
                            return left;
                        case 1:
                            return middle;
                        default:
                            return right;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var publishedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.ChooseSwitch");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsTerminalSelectionWrapper);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
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
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 selector, i32 left, i32 middle, i32 right) {
                        return Facade.ChooseSwitch(selector, left, middle, right, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.ChooseSwitch", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsTerminalSelectionWrapper);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.ChooseSwitch");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_ChooseSwitch__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported terminal-switch selection wrapper specialization to emit with alwaysinline.");
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
