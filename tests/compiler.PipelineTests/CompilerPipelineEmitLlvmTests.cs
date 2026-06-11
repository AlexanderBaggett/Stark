using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineEmitLlvmTests
{
    [Fact]
    public void SourceBackedImportedInlineFunctionsEmitInternalLlvmBodyClones()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-imported-inline-body-llvm-pipeline-");
        var libPath = Path.Combine(tempDirectory.FullName, "Lib.stark");
        var demoPath = Path.Combine(tempDirectory.FullName, "Demo.stark");

        try
        {
            File.WriteAllText(
                libPath,
                """
                module Lib

                export inline fn u8[0 102] SelectOrAdd(u8[0 100] value, bool add)
                {
                    stack mut u8[0 102] result = value;
                    stack mut i32[min max] step = 0;
                    while willexit (step < 2)
                    {
                        if (add)
                        {
                            result = (result / 2) + 1;
                            result = (result / 3) + (result / 5);
                            result = (result / 2) + 1;
                        }
                        else
                        {
                            result = (result / 2) + 2;
                            result = (result / 3) + (result / 7);
                            result = (result / 2) + 2;
                        }

                        step = step + 1;
                    }

                    return result;
                }
                """);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Lib
                    module Demo

                    fn u8[0 102] Run(u8[0 100] value, bool add)
                    {
                        return Lib.SelectOrAdd(value, add);
                    }
                    """,
                    demoPath),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.Contains("; closed-world imported inline body: Lib.SelectOrAdd", llvm, StringComparison.Ordinal);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"define internal[^\r\n]*@__stark_inline_clone_Lib_SelectOrAdd\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected source-backed imported inline function to be emitted as an internal clone definition.");
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"call fastcc[^\r\n]*@__stark_inline_clone_Lib_SelectOrAdd\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected root call to target the imported inline body clone.");
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"call fastcc[^\r\n]*@Lib_SelectOrAdd\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected root call to avoid the imported ABI declaration when an inline body clone exists.");
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
    public void SourceBackedImportedInlineFunctionsDeclareModulePrivateConstsUsedByClone()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-imported-inline-const-llvm-pipeline-");
        var libPath = Path.Combine(tempDirectory.FullName, "Lib.stark");
        var demoPath = Path.Combine(tempDirectory.FullName, "Demo.stark");

        try
        {
            File.WriteAllText(
                libPath,
                """
                module Lib

                const Offset = 7;

                export inline fn i32[min max] AddOffset(i32[min max] value)
                {
                    stack mut i32[min max] total = value;
                    stack mut i32[min max] step = 0;
                    while willexit (step < 3)
                    {
                        total = total + Offset;
                        total = total + (total / 3);
                        total = total - (total / 5);
                        total = total + (total / 7);
                        total = total - (total / 11);
                        step = step + 1;
                    }

                    return total;
                }
                """);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Lib
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Lib.AddOffset(value);
                    }
                    """,
                    demoPath),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.Contains("; closed-world imported inline body: Lib.AddOffset", llvm, StringComparison.Ordinal);
            Assert.Contains("; imported inline const definition: Lib.Offset", llvm, StringComparison.Ordinal);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"@Lib_Offset = internal (?:unnamed_addr )?constant i\d+ 7",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported inline clone to bring in the module-private const definition it references.");
            Assert.Contains("ptr @Lib_Offset", llvm, StringComparison.Ordinal);
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
    public void SourceBackedImportedInlineFunctionsCloneModulePrivateCalleeDependencies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-imported-inline-private-callee-llvm-pipeline-");
        var libPath = Path.Combine(tempDirectory.FullName, "Lib.stark");
        var demoPath = Path.Combine(tempDirectory.FullName, "Demo.stark");

        try
        {
            File.WriteAllText(
                libPath,
                """
                module Lib

                const Offset = 7;

                fn u8[0 100] ApplyOffset(u8[0 93] value)
                {
                    stack mut u8[0 100] total = value;
                    stack mut i32[min max] step = 0;
                    while willexit (step < 2)
                    {
                        total = (total / 2) + Offset;
                        total = (total / 3) + Offset;
                        total = (total / 2) + (total / 5);
                        total = (total / 3) + (total / 7);
                        step = step + 1;
                    }

                    return total;
                }

                export inline fn u8[0 100] AddOffset(u8[0 93] value)
                {
                    return ApplyOffset(value);
                }
                """);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Lib
                    module Demo

                    fn u8[0 100] Run(u8[0 93] value)
                    {
                        return Lib.AddOffset(value);
                    }
                    """,
                    demoPath),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.Contains("; closed-world imported inline body: Lib.ApplyOffset", llvm, StringComparison.Ordinal);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"call fastcc[^\r\n]*@__stark_inline_clone_Lib_ApplyOffset\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported inline clone to call an internal clone for its module-private helper.");
            Assert.DoesNotContain("call fastcc i8 @Lib_ApplyOffset(", llvm, StringComparison.Ordinal);
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
    public void SourceBackedImportedInlineCloneSeedsRestrictEmissionToReachableRootPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-imported-inline-seed-llvm-pipeline-");
        var basePath = Path.Combine(tempDirectory.FullName, "Base.stark");
        var wrapperPath = Path.Combine(tempDirectory.FullName, "Wrapper.stark");

        try
        {
            File.WriteAllText(
                basePath,
                """
                module Base

                export inline fn i32[min max] Used(i32[min max] value, bool add)
                {
                    stack mut i32[min max] total = value;
                    stack mut i32[min max] step = 0;
                    while willexit (step < 2)
                    {
                        if (add)
                        {
                            total = total + 1 + (total / 3);
                            total = total - (total / 5);
                        }
                        else
                        {
                            total = total + 3 + (total / 7);
                            total = total - (total / 11);
                        }

                        step = step + 1;
                    }

                    return total;
                }

                export inline fn i32[min max] Unused(i32[min max] value, bool add)
                {
                    stack mut i32[min max] total = value;
                    stack mut i32[min max] step = 0;
                    while willexit (step < 2)
                    {
                        if (add)
                        {
                            total = total + 2 + (total / 3);
                            total = total - (total / 5);
                        }
                        else
                        {
                            total = total + 4 + (total / 7);
                            total = total - (total / 11);
                        }

                        step = step + 1;
                    }

                    return total;
                }
                """);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Base
                    module Wrapper

                    export fn i32[min max] UsedEntry(i32[min max] value, bool add)
                    {
                        return Base.Used(value, add);
                    }

                    export fn i32[min max] UnusedEntry(i32[min max] value, bool add)
                    {
                        return Base.Unused(value, add);
                    }
                    """,
                    wrapperPath),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    ImportedInlineCloneSeedFunctions: new HashSet<string>(StringComparer.Ordinal)
                    {
                        "UsedEntry"
                    }));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.Contains("; closed-world imported inline body: Base.Used", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("; closed-world imported inline body: Base.Unused", llvm, StringComparison.Ordinal);
            Assert.Contains("@__stark_inline_clone_Base_Used(", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("@__stark_inline_clone_Base_Unused(", llvm, StringComparison.Ordinal);
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
    public void SourceBackedImportedNoInlineFunctionsStayAbiDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-imported-noinline-body-llvm-pipeline-");
        var libPath = Path.Combine(tempDirectory.FullName, "Lib.stark");
        var demoPath = Path.Combine(tempDirectory.FullName, "Demo.stark");

        try
        {
            File.WriteAllText(
                libPath,
                """
                module Lib

                public noinline finite u8[0 102] SelectOrAdd(u8[0 100] value, bool add)
                {
                    if (add)
                    {
                        return value + 1;
                    }

                    return value + 2;
                }
                """);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Lib
                    module Demo

                    fn u8[0 102] Run(u8[0 100] value, bool add)
                    {
                        return Lib.SelectOrAdd(value, add);
                    }
                    """,
                    demoPath),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.DoesNotContain("closed-world imported inline body: Lib.SelectOrAdd", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("@__stark_inline_clone_Lib_SelectOrAdd", llvm, StringComparison.Ordinal);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvm,
                    @"call fastcc[^\r\n]*@Lib_SelectOrAdd\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected noinline imports to remain ABI calls.");
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

                public cold noinline fn T Choose<T>(T left, T right, bool takeRight)
                {
                    stack mut T current = left;
                    if (takeRight)
                    {
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

                    fn i32[min max] Run(i32[min max] left, i32[min max] right, bool takeRight)
                    {
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
    public void PackageBackedImportedGenericInlineBodiesEmitReachableInlineClonesFromTypedFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-imported-generic-inline-clone-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public inline fn i32[min max] Blend<T>(T tag, i32[min max] value)
                {
                    stack i32[min max] a = value + (value / 3);
                    stack i32[min max] b = a - (a / 5);
                    stack i32[min max] c = b + (b / 7);
                    stack i32[min max] d = c - (c / 11);
                    stack i32[min max] e = d + (d / 13);
                    stack i32[min max] f = e - (e / 17);
                    stack i32[min max] g = f + (f / 19);
                    stack i32[min max] h = g - (g / 23);
                    stack i32[min max] i = h + (h / 29);
                    stack i32[min max] j = i - (i / 31);
                    stack i32[min max] k = j + (j / 37);
                    stack i32[min max] l = k - (k / 41);
                    stack i32[min max] m = l + (l / 43);
                    return m;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates!.Functions
                    .Select(static template => template.QualifiedResolvedName == "Facade.Blend"
                        ? template with { BodyText = "{ return this is not valid Stark; }" }
                        : template)
                    .ToArray());
            var corruptedModule = facadeModule with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = facadeModule.CompilerSections is null
                    ? null
                    : facadeModule.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            File.WriteAllText(
                manifestPath,
                (manifest with
                {
                    Modules = manifest.Modules
                        .Select(module => module.ModuleName == "Facade" ? corruptedModule : module)
                        .ToArray()
                }).ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Facade.Blend(left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Blend");
            Assert.Equal("__stark_mono_fn_Demo__Facade_Blend__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.DoesNotContain("this is not valid Stark", llvmModule!.Text, StringComparison.Ordinal);
            Assert.Contains($"; closed-world imported inline body: {function.SymbolName}", llvmModule.Text, StringComparison.Ordinal);
            Assert.Contains($"@__stark_inline_clone_{function.SymbolName}(", llvmModule.Text, StringComparison.Ordinal);
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

                public struct Box
                {
                    i32[min max] Value;
                }

                public fn void Touch<T>(borrow Box box, T tag)
                {
                    stack i32[min max] copy = box.Value;
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

                    fn void Run(borrow Facade.Box box, i32[min max] value)
                    {
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
                $@"define internal[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*\)[^\r\n]*",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            Assert.True(definition.Success, $"Expected an internal LLVM body for imported specialization '{function.SymbolName}'.");
            Assert.Contains("ptr noundef nonnull noalias readonly nocapture", definition.Value, StringComparison.Ordinal);
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
    public void ManifestBackedImportedGenericSpecializationsPreserveRegionFactsWithoutSourceBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-region-facts-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn i32[min max] CopyFirst<T>(
                    rawmutptr<i32[min max]> left,
                    rawmutptr<i32[min max]> right,
                    T tag)
                    {
                        *left = 7;
                    return *right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.EffectiveTypedInterface);
            Assert.NotNull(facadeModule.EffectiveCompilerFacts);
            Assert.NotNull(facadeModule.EffectiveGenericTemplates);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? WithEffectiveLegacyCompilerSectionCopies(module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.EffectiveTypedInterface,
                            CompilerFacts = facadeModule.EffectiveCompilerFacts,
                            GenericTemplates = facadeModule.EffectiveGenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.EffectiveTypedInterface,
                                CompilerFacts: facadeModule.EffectiveCompilerFacts,
                                GenericTemplates: facadeModule.EffectiveGenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        })
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
            Assert.Contains("public unsafe fn i32[min max] CopyFirst<T>", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("*left = 7;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return *right;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run()
                    {
                        stack mut i32[min max] left = 0;
                        stack mut i32[min max] right = 5;
                        return Facade.CopyFirst(&left, &right, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "emit-llvm",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.CopyFirst");

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var definition = System.Text.RegularExpressions.Regex.Match(
                llvmModule.Text,
                $@"define internal[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*\)[\s\S]*?^}}",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            Assert.True(definition.Success, $"Expected an internal LLVM body for imported specialization '{function.SymbolName}'.");
            Assert.Contains("ptr noundef noalias nocapture %arg_left", definition.Value, StringComparison.Ordinal);
            Assert.Contains("ptr noundef noalias nocapture %arg_right", definition.Value, StringComparison.Ordinal);
            Assert.Contains($"stark.noalias.{function.SymbolName}.param.left", llvmModule.Text, StringComparison.Ordinal);
            Assert.Contains($"stark.noalias.{function.SymbolName}.param.right", llvmModule.Text, StringComparison.Ordinal);
            Assert.Matches(@"store i32 .* !alias\.scope !\d+, !noalias !\d+", definition.Value);
            Assert.Matches(@"load i32, ptr .* !alias\.scope !\d+, !noalias !\d+", definition.Value);
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
    public void ManifestBackedImportedPlainFnGenericsThatStrengthenToLawInlineWhenTemplateSemanticsSurviveWithoutFunctionSemantics()
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
            Assert.DoesNotContain($"__stark_law_clone_{function.SymbolName}", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("call fastcc", llvmModule.Text, StringComparison.Ordinal);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define internal[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                $"Expected an internal LLVM body for imported specialization '{function.SymbolName}'.");
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define internal[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*\)[^\r\n]*memory\(none\)[^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                $"Expected imported specialization '{function.SymbolName}' to retain finite-law LLVM attributes.");
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

                public record Box(i32[min max] Value)
                {
                    fn i32[min max] Bump(borrow Box self, i32[min max] delta)
                    {
                        return self.Value + delta;
                    }
                }

                public fn i32[min max] Forward<T>(borrow Box box, i32[min max] delta, T tag)
                {
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

                    fn i32[min max] Run(borrow Facade.Box box, i32[min max] delta)
                    {
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

                public record Inner(i32[min max] Value)
                {
                }
                public record Box(Inner Inner)
                {
                }

                public fn i32[min max] Read<T>(borrow Box box, T tag)
                {
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

                    fn i32[min max] Run(borrow Facade.Box box)
                    {
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

                public record Box(i32[min max] Value)
                {
                }

                public fn i32[min max] Read<T>(Box[2] boxes, i32[min max] index, T tag)
                {
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

                    fn i32[min max] Run(Facade.Box[2] boxes, i32[min max] index)
                    {
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

                public record Inner(i32[min max] Value)
                {
                }
                public record Box(Inner Inner)
                {
                }

                public fn i64[min max] Read<T>(borrow Box box, T tag)
                {
                    return (i64[min max])box.Inner.Value;
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

                    fn i64[min max] Run(borrow Facade.Box box)
                    {
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

                public record Buffer(i32[min max][2] Values)
                {
                }

                public unsafe fn rawptr<i32[min max]> Pin<T>(borrow Buffer buffer, i32[min max] index, T tag)
                {
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

                    unsafe fn rawptr<i32[min max]> Run(borrow Facade.Buffer buffer, i32[min max] index)
                    {
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

            var wrapperMatch = System.Text.RegularExpressions.Regex.Match(
                llvmModule.Text,
                $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*\)[^\r\n]*\r?\n\{{(?<body>.*?)^\}}",
                System.Text.RegularExpressions.RegexOptions.Singleline
                | System.Text.RegularExpressions.RegexOptions.Multiline
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            Assert.True(wrapperMatch.Success, "Expected a concrete LLVM definition for the imported address-of wrapper specialization.");
            var wrapperBody = wrapperMatch.Groups["body"].Value;
            Assert.Contains("getelementptr", wrapperBody);
            Assert.DoesNotContain("ptrtoint", wrapperBody);
            Assert.DoesNotContain("inttoptr", wrapperBody);
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

                public record Inner(i32[min max] Value)
                {
                }
                public record Box(Inner Inner)
                {
                }

                public fn i32[min max] AddDelta<T>(borrow Box box, i32[min max] delta, T tag)
                {
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

                    fn i32[min max] Run(borrow Facade.Box box, i32[min max] delta)
                    {
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

                public record Inner(i32[min max] Value)
                {
                }
                public record Box(Inner Inner)
                {
                }

                public fn bool IsBelow<T>(borrow Box box, i32[min max] limit, T tag)
                {
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

                    fn bool Run(borrow Facade.Box box, i32[min max] limit)
                    {
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

                public fn i32[min max] ChooseBranch<T>(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right, T tag)
                {
                    if (takeLeft)
                    {
                        return left;
                    }
                    else if (takeMiddle)
                    {
                        return middle;
                    }
                    else
                    {
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

                    fn i32[min max] Run(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right)
                    {
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

                public fn i32[min max] ChooseSwitch<T>(i32[min max] selector, i32[min max] left, i32[min max] middle, i32[min max] right, T tag)
                {
                    switch (selector)
                    {
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

                    fn i32[min max] Run(i32[min max] selector, i32[min max] left, i32[min max] middle, i32[min max] right)
                    {
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

    [Fact]
    public void ManifestBackedObjectConstructionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-object-construction-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Inner<T>
                {
                    T Value;
                }

                public struct Outer<T>
                {
                    Inner<T> Item;
                    i32[min max] Count;
                }

                public fn Outer<T> Wrap<T>(T value, i32[min max] count, T tag)
                {
                    return new Outer<T>()
                    {
                        Item =
                        {
                            Value = value
                        },
                        Count = count
                    };
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
                static template => template.QualifiedResolvedName == "Facade.Wrap");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnAggregateConstructionWrapper);
            Assert.Equal(1, publishedTemplate.Semantics.Optimization.ObjectCreationCount);

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

                    fn Facade.Outer<i32[min max]> Run(i32[min max] value, i32[min max] count)
                    {
                        return Facade.Wrap(value, count, value);
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
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Wrap", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnAggregateConstructionWrapper);
            Assert.Equal(1, importedSemantics.OptimizationSummary.ObjectCreationCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Wrap");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Wrap__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported object-construction wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedEnumConstructionWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-enum-construction-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T>
                {
                    None,
                    Value
                    {
                        Data: T, Tag: i32[min max]
                    },
                }

                public fn Boxed<T> Wrap<T>(T value, i32[min max] tag, T marker)
                {
                    return Boxed<T>.Value
                    {
                        Data: value, Tag: tag
                    };
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
                static template => template.QualifiedResolvedName == "Facade.Wrap");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSingleReturnAggregateConstructionWrapper);

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

                    fn Facade.Boxed<i32[min max]> Run(i32[min max] value, i32[min max] tag)
                    {
                        return Facade.Wrap(value, tag, value);
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
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Wrap", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSingleReturnAggregateConstructionWrapper);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Wrap");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Wrap__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported enum-construction wrapper specialization to emit with alwaysinline.");
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
    public void ManifestBackedLocalUpdateWrapperGenericsUseOptimizationSummaryForPlanningAndCodegen()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-local-update-wrapper-generic-llvm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Inner(i32[min max] Value)
                {
                }
                public record Box(Inner Inner)
                {
                }

                public fn i32[min max] Bump<T>(borrow Box box, i32[min max] delta, T tag)
                {
                    stack mut i32[min max] current = box.Inner.Value;
                    current += delta;
                    return current;
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
                static template => template.QualifiedResolvedName == "Facade.Bump");
            Assert.NotNull(publishedTemplate.Semantics);
            Assert.NotNull(publishedTemplate.Semantics!.Optimization);
            Assert.True(publishedTemplate.Semantics.Optimization!.IsSimpleLocalUpdateWrapper);
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

                    fn i32[min max] Run(borrow Facade.Box box, i32[min max] delta)
                    {
                        return Facade.Bump(box, delta, delta);
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
            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Bump", out var importedSemantics));
            Assert.NotNull(importedSemantics.OptimizationSummary);
            Assert.True(importedSemantics.OptimizationSummary!.IsSimpleLocalUpdateWrapper);
            Assert.Equal(2, importedSemantics.OptimizationSummary.FieldAccessCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            var function = Assert.Single(plan.Functions, static function => function.TemplateName == "Facade.Bump");
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Bump__i32", function.SymbolName);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    llvmModule.Text,
                    $@"define[^\r\n]*@{System.Text.RegularExpressions.Regex.Escape(function.SymbolName)}\([^\r\n]*alwaysinline",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Expected the imported local-update wrapper specialization to emit with alwaysinline.");
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
    public void ConstGlobalDerivedLoadsEmitInvariantLoadMetadataWithoutTaggingLocalLoads()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;
                }

                const Box Current = new Box()
                {
                    Value = 5
                };

                unsafe fn i32[min max] Run()
                {
                    stack mut i32[min max] local = 3;
                    stack rawptr<i32[min max]> localPtr = &local;
                    stack rawptr<frozen i32[min max]> constPtr = &(Current.Value);
                    return (*localPtr) + (*constPtr);
                }
                """,
                Path.Combine(Path.GetTempPath(), "ConstInvariantLoad.stark")),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);

        var match = System.Text.RegularExpressions.Regex.Match(
            llvmModule.Text,
            @"define[^\r\n]*@Run\([^\r\n]*\)[^\r\n]*\r?\n\{(?<body>.*?)^\}",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Expected a concrete LLVM definition for Run.");

        var body = match.Groups["body"].Value;
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(
                body,
                @"load i32, ptr %(?:v\d+|slot_\w+)(?:, align \d+)?",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count == 2,
            "Expected exactly two i32 loads in Run: one local and one const-derived.");
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(
                body,
                @"load i32, ptr %(?:v\d+|slot_\w+)(?:, align \d+)?, !invariant\.load !\d+",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count == 1,
            "Expected exactly one invariant-marked load in Run.");
    }
}
