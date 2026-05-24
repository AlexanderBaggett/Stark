using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class TextOrderedComparisonRuntimeTests
{
    [Fact]
    public async Task TextOrderedComparisonsAreLexicographicAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            export unsafe fn i32[min max] main()
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                stack rawptr<i32[min max]> p0 = (rawptr<i32[min max]>)(&values[0]);
                stack rawptr<i32[min max]> p1 = (rawptr<i32[min max]>)(&values[1]);

                if ("ab" < "aba"
                    && "aba" > "ab"
                    && "abc" <= "abc"
                    && "abd" >= "abd"
                    && false < true
                    && true >= true
                    && p0 < p1
                    && p1 > p0
                    && p0 <= p0
                    && p1 >= p1
                    && ((unicode)"cafe") < ((unicode)"caf\u00E9")
                    && ((unicode)"caf\u00E9") > ((unicode)"cafe")
                    && ((unicode)"cafe")[0, 3] <= ((unicode)"cafg")[0, 3]
                    && ((unicode)"cafg")[0, 3] >= ((unicode)"cafe")[0, 3])
                    {
                        return 7;
                }

                return 0;
            }
            """);

        Assert.Equal(7, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-text-order-runtime-");
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
