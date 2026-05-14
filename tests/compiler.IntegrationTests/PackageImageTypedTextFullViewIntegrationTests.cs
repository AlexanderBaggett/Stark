using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class PackageImageTypedTextFullViewIntegrationTests
{
    [Fact]
    public async Task ManifestBackedTypedFullViewTextBodiesCompileAndRunWithoutSyntheticSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-text-full-view-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public fn ascii WholeAscii<T>(ascii text, T tag) {
                    return text[];
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
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.NotNull(facadeModule.EffectiveGenericTemplates);
            var effectiveTemplates = facadeModule.EffectiveGenericTemplates!;
            var wholeAscii = Assert.Single(effectiveTemplates.Functions, static template => template.QualifiedResolvedName == "Facade.WholeAscii");
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
                            TypedInterface = facadeModule.EffectiveTypedInterface,
                            CompilerFacts = facadeModule.EffectiveCompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                effectiveTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == wholeAscii.QualifiedResolvedName
                                        ? template with { BodyText = "{ return this is not valid Stark; }" }
                                        : template)
                                    .ToArray())
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

                export fn i32[min max] main() {
                    stack ascii text = Facade.WholeAscii("hello", 0);
                    if (text != "hello") {
                        return 1;
                    }
                    if (text[1] != 'e') {
                        return 2;
                    }
                    return 0;
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
            Assert.Equal(0, process.ExitCode);
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
