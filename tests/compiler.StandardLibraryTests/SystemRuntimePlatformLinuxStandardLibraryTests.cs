using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimePlatformLinuxStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath() => _suite.StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath() => _suite.StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath() => _suite.StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceLinuxFileOperationsUseSyscallBackedPath() => _suite.StdLibSourceLinuxFileOperationsUseSyscallBackedPath();

    [Fact]
    public void StdLibSourceLinuxFileExistsUsesStatSyscallPath() => _suite.StdLibSourceLinuxFileExistsUsesStatSyscallPath();

    [Fact]
    public void StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath() => _suite.StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath();

    [Fact]
    public void SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets() => _suite.SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets();

    [Fact]
    public Task SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal() => _suite.SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal();

    [Fact]
    public void StdLibSourceLinuxProcessHelpersUseSyscallBackedPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc void @ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ProcessId(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxExitGroupSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxGetPidSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Integer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@exit(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@getpid(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxEventWaitingUsesEpollSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc void @WriteEpollEvent(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WaitForEvents(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WaitReadable(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WaitWritable(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxEpollCreate1SyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxEpollCtlSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxEpollWaitSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxEpollReadableEvent", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxEpollWritableEvent", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Integer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4HandlePointersInteger(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@poll(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@epoll_wait(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceRuntimePlatformWaitWritableUsesLinuxEpoll()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-poll-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                export unsafe ffi fn i32[min max] main() {
                    if (System.Runtime.Platform.WaitWritable((rawptr<i8[min max]>)1, 0) <= 0) {
                        return 1;
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

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
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
    public void StdLibSourceLinuxFutexSynchronizationUsesSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc noundef i32 @FutexWait(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWake(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxFutexSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxFutexWaitPrivateOperation", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxFutexWakePrivateOperation", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4HandlePointersInteger(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@futex(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceRuntimePlatformFutexWaitWakeUseLinuxSyscall()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-futex-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                export unsafe ffi fn i32[min max] main() {
                    stack mut i32[min max] state = 1;
                    if (System.Runtime.Platform.FutexWait(&state, 2) != -11) {
                        return 1;
                    }

                    if (System.Runtime.Platform.FutexWake(&state, 1) < 0) {
                        return 2;
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

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
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
    public async Task SourceRuntimePlatformProcessExitUsesLinuxExitGroup()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                export unsafe ffi fn i32[min max] main() {
                    if (System.Runtime.Platform.ProcessId() <= 0) {
                        return 3;
                    }

                    System.Runtime.Platform.ExitProcess(23);
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

            Assert.Equal(23, process.ExitCode);
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

