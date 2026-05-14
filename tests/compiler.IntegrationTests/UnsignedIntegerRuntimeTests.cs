using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class UnsignedIntegerRuntimeTests
{
    [Fact]
    public async Task UnsignedSmallMediumAndWideIntegerOperationsRunCorrectly()
    {
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            const u96 WideHigh = 2 ** 95;
            const u96 WideLow = 2 ** 94;

            fn i32[min max] Small() {
                stack u8[0 max] maxValue = (u8[0 max])(2 ** 8 - 1);
                stack u8[0 max] divisor = 2;
                stack u8[0 max] threshold = 200;
                if (maxValue > threshold && maxValue / divisor == (u8[0 max])(2 ** 7 - 1) && maxValue >> 4 == 15) {
                    return 0;
                }

                return 1;
            }

            fn i32[min max] Medium() {
                stack u32[0 max] maxValue = (u32[0 max])(2 ** 32 - 1);
                stack u32[0 max] high = 4000000000;
                stack u32[0 max] divisor = (u32[0 max])(2 ** 16);
                if (maxValue > high && maxValue / divisor == (u32[0 max])(2 ** 16 - 1)) {
                    return 0;
                }

                return 2;
            }

            fn i32[min max] Wide() {
                stack u96[0 max] high = WideHigh;
                stack u96[0 max] low = WideLow;
                if (high > low) {
                    return 0;
                }

                return 3;
            }

            export unsafe fn i32[min max] main() {
                stack i32[min max] small = Small();
                if (small != 0) {
                    return small;
                }

                stack i32[min max] medium = Medium();
                if (medium != 0) {
                    return medium;
                }

                return Wide();
            }
            """);

        Assert.Equal(0, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-unsigned-runtime-");
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
