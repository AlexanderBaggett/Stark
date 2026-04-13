using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class PackageImageOptimizationSummaryWrapperIntegrationTests
{
    [Fact]
    public async Task ManifestBackedAggregateConstructionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-aggregate-construction-wrapper-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public struct Inner<T> {
                    T Value;
                }

                public struct Outer<T> {
                    Inner<T> Item;
                    i32[-2147483648 2147483647] Count;
                }

                public enum Boxed<T> {
                    None,
                    Value { Data: T, Tag: i32[-2147483648 2147483647] },
                }

                public fn Outer<T> WrapObject<T>(T value, i32[-2147483648 2147483647] count, T tag) {
                    return new Outer<T>() {
                        Item = { Value = value },
                        Count = count
                    };
                }

                public fn Boxed<T> WrapEnum<T>(T value, i32[-2147483648 2147483647] tag, T marker) {
                    return Boxed<T>.Value { Data: value, Tag: tag };
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

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest!.Modules, static module => module.ModuleName == "Facade"));
            var objectTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.WrapObject");
            Assert.Null(objectTemplate.BodyText);
            Assert.NotNull(objectTemplate.TypedBody);
            Assert.NotNull(objectTemplate.Semantics);
            Assert.NotNull(objectTemplate.Semantics!.Optimization);
            Assert.True(objectTemplate.Semantics.Optimization!.IsSingleReturnAggregateConstructionWrapper);

            var enumTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.WrapEnum");
            Assert.Null(enumTemplate.BodyText);
            Assert.NotNull(enumTemplate.TypedBody);
            Assert.NotNull(enumTemplate.Semantics);
            Assert.NotNull(enumTemplate.Semantics!.Optimization);
            Assert.True(enumTemplate.Semantics.Optimization!.IsSingleReturnAggregateConstructionWrapper);

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
                            CompilerFacts = facadeModule.CompilerFacts! with
                            {
                                FunctionSemantics = []
                            },
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                },
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

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack i32[-2147483648 2147483647] seed = 40;
                    stack Facade.Outer<i32[-2147483648 2147483647]> wrapped = Facade.WrapObject(seed, 1, seed);
                    stack Facade.Boxed<i32[-2147483648 2147483647]> boxed = Facade.WrapEnum(wrapped.Item.Value, wrapped.Count, seed);

                    switch (boxed) {
                        case Facade.Boxed<i32[-2147483648 2147483647]>.Value { Data: var data, Tag: var tag }:
                            return data + tag;
                        default:
                            return 1;
                    }
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

            Assert.True(compileExitCode == 0, stderr.ToString());
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
            Assert.Equal(41, process.ExitCode);
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
    public async Task ManifestBackedLocalUpdateWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-local-update-wrapper-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public record Inner(i32[-2147483648 2147483647] Value) { }
                public record Box(Inner Inner) { }

                public fn i32[-2147483648 2147483647] Bump<T>(Box box, i32[-2147483648 2147483647] delta, T tag) {
                    stack mut i32[-2147483648 2147483647] current;
                    current = box.Inner.Value;
                    current += delta;
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

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest!.Modules, static module => module.ModuleName == "Facade"));
            var bumpTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Bump");
            Assert.Null(bumpTemplate.BodyText);
            Assert.NotNull(bumpTemplate.TypedBody);
            Assert.NotNull(bumpTemplate.Semantics);
            Assert.NotNull(bumpTemplate.Semantics!.Optimization);
            Assert.True(bumpTemplate.Semantics.Optimization!.IsSimpleLocalUpdateWrapper);

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
                            CompilerFacts = facadeModule.CompilerFacts! with
                            {
                                FunctionSemantics = []
                            },
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                },
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

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                fn i32[-2147483648 2147483647] Run(Facade.Box box, i32[-2147483648 2147483647] delta, i32[-2147483648 2147483647] tag) {
                    return Facade.Bump(box, delta, tag);
                }

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack Facade.Box box = new Facade.Box(new Facade.Inner(40));
                    stack i32[-2147483648 2147483647] delta = 2;
                    stack i32[-2147483648 2147483647] tag = 0;
                    return Run(box, delta, tag);
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

            Assert.True(compileExitCode == 0, stderr.ToString());
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
            Assert.Equal(42, process.ExitCode);
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
    public async Task ManifestBackedTerminalSelectionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-terminal-selection-wrapper-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public fn i32[-2147483648 2147483647] ChooseBranch<T>(bool takeLeft, bool takeMiddle, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right, T tag) {
                    if (takeLeft) {
                        return left;
                    } else if (takeMiddle) {
                        return middle;
                    } else {
                        return right;
                    }
                }

                public fn i32[-2147483648 2147483647] ChooseSwitch<T>(i32[-2147483648 2147483647] selector, i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] middle, i32[-2147483648 2147483647] right, T tag) {
                    switch (selector) {
                        case 0:
                            return left;
                        case 1:
                            return middle;
                        default:
                            return right;
                    }
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

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest!.Modules, static module => module.ModuleName == "Facade"));

            var branchTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.ChooseBranch");
            Assert.Null(branchTemplate.BodyText);
            Assert.NotNull(branchTemplate.TypedBody);
            Assert.NotNull(branchTemplate.Semantics);
            Assert.NotNull(branchTemplate.Semantics!.Optimization);
            Assert.True(branchTemplate.Semantics.Optimization!.IsTerminalSelectionWrapper);

            var switchTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.ChooseSwitch");
            Assert.Null(switchTemplate.BodyText);
            Assert.NotNull(switchTemplate.TypedBody);
            Assert.NotNull(switchTemplate.Semantics);
            Assert.NotNull(switchTemplate.Semantics!.Optimization);
            Assert.True(switchTemplate.Semantics.Optimization!.IsTerminalSelectionWrapper);

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
                            CompilerFacts = facadeModule.CompilerFacts! with
                            {
                                FunctionSemantics = []
                            },
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                },
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

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack i32[-2147483648 2147483647] tag = 0;
                    stack i32[-2147483648 2147483647] first = Facade.ChooseBranch(false, true, 1, 40, 9, tag);
                    stack i32[-2147483648 2147483647] second = Facade.ChooseSwitch(0, 2, 7, 9, tag);
                    return first + second;
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

            Assert.True(compileExitCode == 0, stderr.ToString());
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
            Assert.Equal(42, process.ExitCode);
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
    public async Task ManifestBackedBinaryAndComparisonWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-operator-wrapper-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public record Inner(i32[-2147483648 2147483647] Value) { }
                public record Box(Inner Inner) { }

                public fn i32[-2147483648 2147483647] AddDelta<T>(Box box, i32[-2147483648 2147483647] delta, T tag) {
                    return box.Inner.Value + delta;
                }

                public fn bool IsBelow<T>(Box box, i32[-2147483648 2147483647] limit, T tag) {
                    return box.Inner.Value < limit;
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

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest!.Modules, static module => module.ModuleName == "Facade"));
            var addDeltaTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.AddDelta");
            Assert.Null(addDeltaTemplate.BodyText);
            Assert.NotNull(addDeltaTemplate.TypedBody);
            Assert.NotNull(addDeltaTemplate.Semantics);
            Assert.NotNull(addDeltaTemplate.Semantics!.Optimization);
            Assert.True(addDeltaTemplate.Semantics.Optimization!.IsSingleReturnBinaryOperatorWrapper);
            Assert.False(addDeltaTemplate.Semantics.Optimization.IsSingleReturnComparisonWrapper);

            var isBelowTemplate = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.IsBelow");
            Assert.Null(isBelowTemplate.BodyText);
            Assert.NotNull(isBelowTemplate.TypedBody);
            Assert.NotNull(isBelowTemplate.Semantics);
            Assert.NotNull(isBelowTemplate.Semantics!.Optimization);
            Assert.True(isBelowTemplate.Semantics.Optimization!.IsSingleReturnComparisonWrapper);
            Assert.False(isBelowTemplate.Semantics.Optimization.IsSingleReturnBinaryOperatorWrapper);

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
                            CompilerFacts = facadeModule.CompilerFacts! with
                            {
                                FunctionSemantics = []
                            },
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                },
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

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack i32[-2147483648 2147483647] delta = 2;
                    stack i32[-2147483648 2147483647] limit = 50;
                    stack i32[-2147483648 2147483647] result = Facade.AddDelta(new Facade.Box(new Facade.Inner(40)), delta, delta);
                    if (Facade.IsBelow(new Facade.Box(new Facade.Inner(result - delta)), limit, delta)) {
                        return result;
                    }

                    return 1;
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

            Assert.True(compileExitCode == 0, stderr.ToString());
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
            Assert.Equal(42, process.ExitCode);
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
    public async Task ManifestBackedConversionWrapperBodiesCompileAndRunWithoutTopLevelFunctionSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-optimization-summary-wrapper-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public record Inner(i32[-2147483648 2147483647] Value) { }
                public record Box(Inner Inner) { }

                public fn i64[-9223372036854775808 9223372036854775807] Read<T>(Box box, T tag) {
                    return (i64[-9223372036854775808 9223372036854775807])box.Inner.Value;
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

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest!.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(
                facadeModule.GenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Read");
            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.NotNull(template.Semantics);
            Assert.NotNull(template.Semantics!.Optimization);
            Assert.True(template.Semantics.Optimization!.IsSingleReturnConversionWrapper);

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
                            CompilerFacts = facadeModule.CompilerFacts! with
                            {
                                FunctionSemantics = []
                            },
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts! with
                                {
                                    FunctionSemantics = []
                                },
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

            await File.WriteAllTextAsync(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadeSourcePath);

            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack Facade.Box box = new Facade.Box(new Facade.Inner(41));
                    stack i32[-2147483648 2147483647] tag = box.Inner.Value;
                    return (i32[-2147483648 2147483647])Facade.Read(box, tag) + 1;
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

            Assert.True(compileExitCode == 0, stderr.ToString());
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
            Assert.Equal(42, process.ExitCode);
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
