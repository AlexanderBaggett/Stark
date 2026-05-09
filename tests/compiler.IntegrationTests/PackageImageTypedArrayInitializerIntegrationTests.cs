using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class PackageImageTypedArrayInitializerIntegrationTests
{
    [Fact]
    public async Task ManifestBackedTypedArrayInitializerBodiesCompileAndRunWithoutSyntheticSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-array-runtime-");
        var facadeSourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var demoSourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                facadeSourcePath,
                """
                module Facade

                public fn i32[min max] SumArray<T>(i32[min max] left, i32[min max] right, T tag) {
                    stack i32[min max][2] values = { left, right };
                    return values[0] + values[1];
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
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json")));
            File.Delete(facadeSourcePath);
            await File.WriteAllTextAsync(
                demoSourcePath,
                """
                import Facade
                module Demo

                export unsafe ffi fn i32[min max] main() {
                    return Facade.SumArray(4, 7, 0);
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
}
