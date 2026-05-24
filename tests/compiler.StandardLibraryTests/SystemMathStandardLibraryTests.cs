using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemMathStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public Task PackagedStdLibMathIntrinsicsWorkWithoutSource() => _suite.PackagedStdLibMathIntrinsicsWorkWithoutSource();

    [Fact]
    public Task PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt() => _suite.PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt();

    [Fact]
    public async Task SourceStdLibMathXorShift32PseudoRandomExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-math-xorshift32-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Math
                module App

                export fn i32[min max] main()
                {
                    stack mut System.Math.XorShift32 rng = System.Math.XorShift32.Seeded(1);
                    if (rng.CurrentState() != 1)
                    {
                        return 1;
                    }

                    if (rng.NextU32() != 270369)
                    {
                        return 2;
                    }

                    if (rng.NextU32() != 67634689)
                    {
                        return 3;
                    }

                    if (rng.NextI32() != -1647531835)
                    {
                        return 4;
                    }

                    stack f32 unit = rng.NextF32();
                    if (unit <= 0.0 || unit >= 1.0)
                    {
                        return 5;
                    }

                    rng.Reseed(0);
                    if (rng.CurrentState() != 2463534242)
                    {
                        return 6;
                    }

                    if (rng.NextU32() == 0)
                    {
                        return 7;
                    }

                    stack mut System.Math.XorShift32 defaultRng = new System.Math.XorShift32();
                    if (defaultRng.CurrentState() != 2463534242)
                    {
                        return 8;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = tempDirectory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public void SourceStdLibMathXorShift32PseudoRandomEmitsStraightLineBitOps()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibMathXorShift32Ir.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Math
                module Demo

                fn u32[0 max] DrawU32(u32[0 max] seed)
                {
                    stack mut System.Math.XorShift32 rng = System.Math.XorShift32.Seeded(seed);
                    return rng.NextU32();
                }

                fn f32 DrawF32(u32[0 max] seed)
                {
                    stack mut System.Math.XorShift32 rng = System.Math.XorShift32.Seeded(seed);
                    return rng.NextF32();
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                OptimizationLevel: CompilerOptimizationLevel.O0,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("shl i32", llvm, StringComparison.Ordinal);
        Assert.Contains("lshr i32", llvm, StringComparison.Ordinal);
        Assert.Contains("xor i32", llvm, StringComparison.Ordinal);
        Assert.Contains("fmul fast float", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("fdiv float", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@rand", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@random", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_alloc", llvm, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for stdlib integration tests.");
    }

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }
}
