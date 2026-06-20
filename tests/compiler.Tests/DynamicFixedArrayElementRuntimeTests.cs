using Stark.Compiler;
using System.Diagnostics;

namespace compiler.Tests;

/// <summary>
/// Runtime regression for dynamics whose element type is a fixed array
/// (`dynamic T[N]`). Element writes previously emitted the into-array GEP form
/// (stride = inner element) instead of the pointer-element form (stride = whole
/// array), silently overlapping appended rows, and SSA validation rejected the
/// correct address shape. Covers init writes, single-element reads, and
/// start/count slicing with per-element verification.
/// </summary>
public sealed class DynamicFixedArrayElementRuntimeTests
{
    [Fact]
    public async Task DynamicOfFixedArrayWritesReadsAndSlicesPreserveRows()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"stark-dynamic-fixed-array-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var appPath = Path.Combine(tempDirectory, "DynamicFixedArrayRuntime.stark");
            var outputPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "DynamicFixedArrayRuntime.exe" : "DynamicFixedArrayRuntime");
            await File.WriteAllTextAsync(
                appPath,
                """
                module Demo

                export unsafe fn i32[min max] main()
                {
                    stack mut dynamic i8[min max][2] rows = new();
                    if (!rows.TryReserve(2))
                    {
                        return 1;
                    }

                    stack mut i8[min max][2] first;
                    first[0] = 5;
                    first[1] = 6;
                    init rows[rows.Length] = first;

                    stack mut i8[min max][2] second;
                    second[0] = 7;
                    second[1] = 8;
                    init rows[rows.Length] = second;

                    stack i8[min max][2][] view = rows[0, rows.Length];
                    if (view.Length != 2)
                    {
                        return 2;
                    }

                    if (view[0][0] != 5)
                    {
                        return 10;
                    }

                    if (view[0][1] != 6)
                    {
                        return 11;
                    }

                    if (view[1][0] != 7)
                    {
                        return 12;
                    }

                    if (view[1][1] != 8)
                    {
                        return 13;
                    }

                    stack i8[min max][2] copied = rows[1];
                    if (copied[0] != 7 || copied[1] != 8)
                    {
                        return 4;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);
            Assert.True(compileExitCode == 0, stdout + Environment.NewLine + stderr);

            var execution = await RunProcessAsync(outputPath, tempDirectory);
            Assert.True(
                execution.ExitCode == 0,
                $"Exit code: {execution.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{execution.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{execution.Stderr}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
