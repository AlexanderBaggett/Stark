using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class IntegerExponentRuntimeTests
{
    [Fact]
    public async Task IntegerExponentiationRunsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            export ffi fn i32[-2147483648 2147483647] main() {
                stack i32[-2147483648 2147483647] left = 2;
                stack i32[-2147483648 2147483647] right = 5;
                return left ** right;
            }
            """);

        Assert.Equal(32, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-int-exponent-runtime-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-exe", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
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
            await process.WaitForExitAsync();

            var processOutput = await process.StandardOutput.ReadToEndAsync();
            var processError = await process.StandardError.ReadToEndAsync();
            Assert.Equal(string.Empty, processOutput);
            Assert.Equal(string.Empty, processError);
            return process.ExitCode;
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
