using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class PackageImageTypedComparisonChainIntegrationTests
{
    [Fact]
    public async Task ManifestBackedTypedComparisonChainBodiesCompileAndRunWithoutSyntheticSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-comparison-chain-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public static mut i32[min max] OrderedCalls = 0;
                public static mut i32[min max] EqualityCalls = 0;

                public fn i32[min max] NextOrdered()
                {
                    OrderedCalls += 1;
                    return 1;
                }

                public fn i32[min max] NextEquality()
                {
                    EqualityCalls += 1;
                    return 1;
                }

                public fn bool ObserveOrdered<T>(T tag)
                {
                    return 0 < NextOrdered() < 3;
                }

                public fn bool ObserveEquality<T>(T tag)
                {
                    return 1 == NextEquality() == 1;
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

            Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));

            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var orderedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ObserveOrdered");
            var equalityTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ObserveEquality");
            Assert.Null(orderedTemplate.BodyText);
            Assert.NotNull(orderedTemplate.TypedBody);
            Assert.Null(equalityTemplate.BodyText);
            Assert.NotNull(equalityTemplate.TypedBody);

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
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
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

                export fn i32[min max] main()
                {
                    stack i32[min max] tag = 0;
                    if (!Facade.ObserveOrdered(tag))
                    {
                        return 21;
                    }

                    if (!Facade.ObserveEquality(tag))
                    {
                        return 22;
                    }

                    return Facade.OrderedCalls * 10 + Facade.EqualityCalls;
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
            Assert.Equal(11, process.ExitCode);
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
