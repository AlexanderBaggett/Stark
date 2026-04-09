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
    public void ManifestBackedGenericMethodsAndNestedGenericTypesLowerThroughImportedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-method-nested-generic-integration-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public record Box(i32 Dummy) {
                    fn Pair<T> MakePair<T>(borrow Box self, T value) {
                        stack Pair<T> pair = new Pair<T>(value);
                        return pair;
                    }
                }

                public fn Pair<T> Relay<T>(Box box, T value) {
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

                    fn i32 Run(Facade.Box box, i32 value) {
                        stack Facade.Pair<i32> pair = Facade.Relay(box, value);
                        return value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.True(relay.SupportsDirectCodeGeneration);
            Assert.Contains(
                relay.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue call
                    && call.FunctionName.Contains("Facade_Box_MakePair", StringComparison.Ordinal));

            var makePair = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_Box_MakePair", StringComparison.Ordinal));
            Assert.True(makePair.SupportsDirectCodeGeneration);
            Assert.Contains(
                makePair.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedGenericMethodsLoadDirectlyFromStructuredPackageImageFactsEvenWhenBodyTextIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-structured-generic-loading-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public record Box(i32 Dummy) {
                    fn Pair<T> MakePair<T>(borrow Box self, T value) {
                        stack Pair<T> pair = new Pair<T>(value);
                        return pair;
                    }
                }

                public fn Pair<T> Relay<T>(Box box, T value) {
                    return box.MakePair(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var corruptedManifest = BuildTypedOnlyFacadeManifest(
                manifest,
                template => template with
                {
                    BodyText = "{ return this is not valid Stark; }"
                });

            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Box box, i32 value) {
                        stack Facade.Pair<i32> pair = Facade.Relay(box, value);
                        return value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.DoesNotContain("this is not valid Stark", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn Pair<T> Relay<T>(Box box, T value);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return box.MakePair(value);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return pair;", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Contains(
                mir.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Relay__i32");
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
    public void ManifestBackedLoopControlGenericMethodsLoadDirectlyFromStructuredPackageImageFactsEvenWhenBodyTextIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-structured-loop-control-generic-loading-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumWhileControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    stack mut i32 index = 0;
                    while willexit (index < count) {
                        index = index + 1;
                        if (index < 2) {
                            continue;
                        }
                        if (index == stopAt) {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }

                public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
                        if (index < 2) {
                            continue;
                        }
                        if (index == stopAt) {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var corruptedManifest = BuildTypedOnlyFacadeManifest(
                manifest,
                template => template with
                {
                    BodyText = "{ return this is not valid Stark; }"
                });

            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 count, i32 stopAt, i32 tag) {
                        return Facade.SumWhileControl(count, stopAt, tag) + Facade.SumForControl(count, stopAt, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.DoesNotContain("this is not valid Stark", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn i32 SumWhileControl<T>(i32 count, i32 stopAt, T tag);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("while willexit (index < count)", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("continue;", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("break;", importedModule.ParseResult.SourceText, StringComparison.Ordinal);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SumWhileControl__i32");
            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SumForControl__i32");
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
    public async Task ManifestBackedTypedInterfaceModifiersWithoutCompilerFactsStillSpecializeAndRun()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-typed-interface-modifiers-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public cold noinline fn T Choose<T>(T left, T right, bool takeRight) {
                    stack T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
                }
                """);

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [facadeSourcePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Contains("Emitted static library:", emitStdout.ToString());
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            Assert.NotNull(manifest);

            var typedOnlyManifest = BuildTypedOnlyFacadeManifest(
                manifest!,
                static template => template,
                omitCompilerFacts: true);

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export ffi fn i32 main() {
                    stack i32 left = 3;
                    stack i32 right = 7;
                    return Facade.Choose(left, right, true);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    demoSourcePath,
                    "--emit-exe",
                    "-o",
                    outputPath,
                    "-I",
                    tempDirectory.FullName
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, compileExitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processOutput = await process!.StandardOutput.ReadToEndAsync();
            var processError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, processOutput);
            Assert.Equal(string.Empty, processError);
            Assert.Equal(7, process.ExitCode);
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
    public void ManifestBackedBridgeGenericBodiesLoadWithoutSourceSurfaceFunctionEntriesWhenTypedInterfaceCarriesOverloadKeys()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-bridge-generic-loading-without-source-surface-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public alias BufferView = i32[];

                public fn BufferView Relay<T>(BufferView view, T tag) {
                    return view;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var bridgeOnlyManifest = BuildTypedOnlyFacadeManifest(
                manifest,
                template => template.QualifiedResolvedName == "Facade.Relay"
                    ? template with { TypedBody = null }
                    : template);

            File.WriteAllText(manifestPath, bridgeOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32[] values) {
                        stack i32[] copy = Facade.Relay(values, 0);
                        return copy[0];
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.Contains("public alias BufferView = i32[];", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("Relay<T>", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("return", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            var specializedRelay = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Relay__", StringComparison.Ordinal));
            Assert.True(specializedRelay.HasBody);
            Assert.True(specializedRelay.SupportsDirectCodeGeneration);
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
        Func<StarkPackageFunctionTemplateManifest, StarkPackageFunctionTemplateManifest> rewriteTemplate,
        bool omitCompilerFacts = false)
    {
        var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
        Assert.NotNull(facadeModule.GenericTemplates);
        var compilerFacts = omitCompilerFacts ? null : facadeModule.CompilerFacts;

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
                        CompilerFacts = compilerFacts,
                        GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates),
                        CompilerSections = new StarkPackageCompilerSectionsManifest(
                            TypedInterface: facadeModule.TypedInterface,
                            CompilerFacts: compilerFacts,
                            GenericTemplates: new StarkPackageGenericTemplateSection(rewrittenTemplates)),
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
