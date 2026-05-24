using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemProcessStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceProcessHelpersTypeCheckAndLower()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibProcess.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Process
                module Demo

                fn i32[min max] ReadCurrentProcess()
                {
                    return System.Process.CurrentId();
                }

                fn void Stop(i32[min max] code)
                {
                    System.Process.Exit(code);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc noundef i32 @ReadCurrentProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @Stop(", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_CurrentId()", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_Exit(", llvm, StringComparison.Ordinal);

        var stopBody = ExtractDefinedFunctionText(llvm, "define fastcc void @Stop(", "Expected Stop to lower as a defined function.");
        Assert.Contains("call fastcc void @System_Process_Exit(", stopBody, StringComparison.Ordinal);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", stopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ret void", stopBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceProcessExitTerminatesWithRequestedExitCode()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-public-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Process
                module App

                export fn i32[min max] main()
                {
                    if (System.Process.CurrentId() <= 0)
                    {
                        return 3;
                    }

                    System.Process.Exit(23);
                    return 4;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(23, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
    public async Task PackagedStdLibProcessHelpersWorkWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-package-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-pkg", "--package-library-file", libraryFileName, "-o", manifestPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted package image:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn i32[min max] Run()
                {
                    if (System.Process.CurrentId() < 0)
                    {
                        return 1;
                    }

                    return 0;
                }

                fn void Stop()
                {
                    System.Process.Exit(5);
                    return;
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            var stopBody = ExtractDefinedFunctionText(llvm, "define fastcc void @Stop(", "Expected packaged System.Process.Exit caller to lower as a defined function.");
            Assert.Contains("call fastcc void @System_Process_Exit(", stopBody, StringComparison.Ordinal);
            Assert.Contains("call coldcc void @__stark_unreachable_trap()", stopBody, StringComparison.Ordinal);
            Assert.DoesNotContain("ret void", stopBody, StringComparison.Ordinal);
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

