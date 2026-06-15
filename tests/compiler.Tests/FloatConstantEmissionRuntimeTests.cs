using Stark.Compiler;
using System.Diagnostics;

namespace compiler.Tests;

/// <summary>
/// End-to-end regression for the float-constant LLVM IR emission bug
/// (BUGS.md "f64/f32 constant emission renders invalid LLVM IR"). An f64
/// fixed-array constant with integral elements used to emit `double 1` and
/// scientific elements `double 1E+17`, and a scalar `1e17` return emitted
/// `ret double 1E+17` — all rejected by the LLVM backend with
/// "integer constant must have integer type". This drives the program all the
/// way through `--emit-exe` (the real LLVM toolchain that parses the IR) so a
/// regression fails at build time, not just in an IR-text assertion.
/// </summary>
public sealed class FloatConstantEmissionRuntimeTests
{
    [Fact]
    public async Task IntegralAndScientificFloatConstantsBuildAndRunThroughLlvmBackend()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"stark-float-const-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var appPath = Path.Combine(tempDirectory, "FloatConstEmission.stark");
            var outputPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "FloatConstEmission.exe" : "FloatConstEmission");
            await File.WriteAllTextAsync(
                appPath,
                """
                module Demo

                // Powers of ten 10**0 .. 10**22: integral elements (1e0..1e16)
                // and scientific-form elements (1e17..1e22). Every element must
                // emit as a valid LLVM float literal or the backend rejects the
                // module.
                const f64[23] Pow10 =
                {
                    1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
                    1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
                };

                // Scalar f64 literal in scientific range: previously emitted
                // `ret double 1E+17`.
                unsafe finite f64 ScalarScientific()
                {
                    return 1e17;
                }

                export unsafe fn i32[min max] main()
                {
                    // Integral element round-trips to 1.0.
                    if (Pow10[0] != 1.0)
                    {
                        return 1;
                    }

                    // Integral element 10**1.
                    if (Pow10[1] != 10.0)
                    {
                        return 2;
                    }

                    // Scientific-range array element equals the scalar literal.
                    if (Pow10[17] != ScalarScientific())
                    {
                        return 3;
                    }

                    // Largest table entry is finite and positive.
                    if (Pow10[22] <= 0.0)
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
