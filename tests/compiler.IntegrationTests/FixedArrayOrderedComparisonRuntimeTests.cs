using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class FixedArrayOrderedComparisonRuntimeTests
{
    [Fact]
    public async Task FixedArrayOrderedComparisonsAreLexicographicAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            fn bool Less(i32[min max][3] left, i32[min max][3] right) {
                return left < right;
            }

            fn bool LessOrEqual(i32[min max][3] left, i32[min max][3] right) {
                return left <= right;
            }

            fn bool Greater(i32[min max][3] left, i32[min max][3] right) {
                return left > right;
            }

            fn bool GreaterOrEqual(i32[min max][3] left, i32[min max][3] right) {
                return left >= right;
            }

            export unsafe fn i32[min max] main() {
                stack i32[min max][3] lessLeft = { 1, 2, 3 };
                stack i32[min max][3] lessRight = { 1, 2, 4 };
                stack i32[min max][3] lessOrEqualLeft = { 1, 2, 3 };
                stack i32[min max][3] lessOrEqualRight = { 1, 2, 3 };
                stack i32[min max][3] greaterLeft = { 1, 2, 4 };
                stack i32[min max][3] greaterRight = { 1, 2, 3 };
                stack i32[min max][3] greaterOrEqualLeft = { 1, 2, 3 };
                stack i32[min max][3] greaterOrEqualRight = { 1, 2, 3 };
                stack i32[min max][3] topLeft = { 2, 0, 0 };
                stack i32[min max][3] topRight = { 1, 9, 9 };

                if (Less(lessLeft, lessRight)
                    && LessOrEqual(lessOrEqualLeft, lessOrEqualRight)
                    && Greater(greaterLeft, greaterRight)
                    && GreaterOrEqual(greaterOrEqualLeft, greaterOrEqualRight)
                    && GreaterOrEqual(topLeft, topRight)) {
                    return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-fixed-array-order-runtime-");
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
            var standardOutput = await process!.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(string.Empty, standardOutput);
            Assert.Equal(string.Empty, standardError);
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
