using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemThreadingStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task PackagedStdLibThreadingEntrySchedulerAndThreadLifecycleWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var threadingPath = Path.Combine(sourceRoot, "System", "Threading.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-threading-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.Threading.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [threadingPath, "--emit-lib", "-I", sourceRoot, "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.True(
                buildExitCode == 0,
                buildStdout + Environment.NewLine + buildStderr);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Threading
                module App

                fn i32[-2147483648 2147483647] Worker() {
                    return 7;
                }

                fn bool JoinResultIs(System.Threading.ThreadJoinResult result, i32[-2147483648 2147483647] expected) {
                    switch (result) {
                        case System.Threading.ThreadJoinResult.Ok(var value):
                            return value == expected;
                        case System.Threading.ThreadJoinResult.Err(var error):
                            return false;
                    }
                }

                fn bool StatusOk(System.Threading.ThreadStatus status) {
                    switch (status) {
                        case System.Threading.ThreadStatus.Ok:
                            return true;
                        case System.Threading.ThreadStatus.Err(var error):
                            return false;
                    }
                }

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack ThreadEntry entry = Worker;
                    Thread.Yield();
                    Thread.SleepMilliseconds(0);
                    if (entry() != 7) {
                        return 1;
                    }

                    stack ThreadEntry lambdaEntry = () => 11;
                    if (lambdaEntry() != 11) {
                        return 7;
                    }

                    stack mut Thread worker = new(Worker);
                    if (!worker.IsJoinable()) {
                        return 2;
                    }

                    stack ThreadJoinResult joined = worker.Join();
                    if (!JoinResultIs(joined, 7)) {
                        return 3;
                    }

                    if (worker.IsJoinable()) {
                        return 4;
                    }

                    stack mut Thread detached = new(Worker);
                    stack ThreadStatus detachedStatus = detached.Detach();
                    if (!StatusOk(detachedStatus)) {
                        return 5;
                    }

                    if (detached.IsJoinable()) {
                        return 6;
                    }

                    stack mut Thread lambdaWorker = new(() => 13);
                    stack ThreadJoinResult lambdaJoined = lambdaWorker.Join();
                    if (!JoinResultIs(lambdaJoined, 13)) {
                        return 8;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, appDirectory, string.Empty);
            Assert.Equal(0, execution.ExitCode);
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
    public void StdLibSourceThreadingSurfaceSupportsThreadEntryAndSchedulerCalls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                fn i32[-2147483648 2147483647] Worker() {
                    return 7;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack System.Threading.ThreadEntry entry = Worker;
                    stack System.Threading.ThreadEntry lambdaEntry = () => 11;
                    System.Threading.Thread.Yield();
                    System.Threading.Thread.SleepMilliseconds(0);
                    stack mut System.Threading.Thread worker = new(Worker);
                    if (!worker.IsJoinable()) {
                        return 1;
                    }

                    worker.Join();
                    stack mut System.Threading.Thread detached = new(Worker);
                    detached.Detach();
                    stack mut System.Threading.Thread lambdaWorker = new(() => 13);
                    lambdaWorker.Join();
                    return entry() + lambdaEntry();
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.Equal(3, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(
            typeCheckModel.FunctionPointerPromotions,
            promotion =>
            {
                Assert.Equal("Worker", promotion.Signature.Name);
                Assert.Equal("fnptr<fn i32()>", promotion.TargetType.DisplayName);
            });

        Assert.Equal(2, typeCheckModel.Lambdas.Count);
        Assert.All(
            typeCheckModel.Lambdas,
            lambda => Assert.Equal("fnptr<fn i32()>", lambda.FunctionPointerType.DisplayName));

        Assert.Equal(2, typeCheckModel.IndirectCalls.Count);
        Assert.All(
            typeCheckModel.IndirectCalls,
            indirectCall => Assert.Equal("fnptr<fn i32()>", indirectCall.FunctionPointerType.DisplayName));
    }

    [Fact]
    public void StdLibSourceThreadingSurfaceRejectsCapturedLambdaThreadEntriesUntilCaptureLoweringExists()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingCapturedEntry.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                fn void Run() {
                    stack i32[-2147483648 2147483647] exitCode = 7;
                    stack System.Threading.ThreadEntry entry = capture(copy exitCode) () => exitCode;
                    stack mut System.Threading.Thread worker = new(capture(copy exitCode) () => exitCode);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Capturing lambdas", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.Equal(2, typeCheckModel.LambdaCaptures.Count);
        Assert.All(
            typeCheckModel.LambdaCaptures,
            capture =>
            {
                Assert.Equal("exitCode", capture.Name);
                Assert.Equal("copy", capture.Mode);
                Assert.False(capture.IsUnsafe);
                Assert.Equal("Run", capture.EnclosingFunctionName);
            });
    }

    [Fact]
    public void StdLibSourceThreadingErrorEnumsUseCompactLayouts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var threadingPath = Path.Combine(sourceRoot, "System", "Threading.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(threadingPath),
                threadingPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "enum-layout"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);

        Assert.Equal(8, enumLayoutModel.Layouts["ThreadError"].TagField.Type.BitWidth);
        Assert.Equal(8, enumLayoutModel.Layouts["ThreadStatus"].TagField.Type.BitWidth);
        Assert.Equal(8, enumLayoutModel.Layouts["ThreadJoinResult"].TagField.Type.BitWidth);
    }

    [Fact]
    public void StdLibSourceLinuxThreadingUsesRawCloneLifecycleAndSyscallBackedScheduler()
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

        Assert.Contains("define i64 @LinuxMapThreadRegion(", llvm, StringComparison.Ordinal);
        Assert.Contains("define i32 @LinuxReleaseThreadReference(", llvm, StringComparison.Ordinal);
        Assert.Contains("define i64 @LinuxCloneThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @StartThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @JoinThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DetachThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWait(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ReleaseThreadReference(", llvm, StringComparison.Ordinal);
        Assert.Contains("define i64 @LinuxSyscall2Pointers(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @YieldThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @SleepThreadMilliseconds(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxMapThreadRegion(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxCloneThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @LinuxReleaseThreadReference(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxMunmapSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxFutexSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Handle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall2Pointers(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_create(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_join(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_detach(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@ThreadEntryThunk(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@sched_yield(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@nanosleep(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsThreadingUsesWin32LifecycleAndSchedulerCalls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var windowsPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Windows.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(windowsPath),
                windowsPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare ptr @CreateThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WaitForSingleObject(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetExitCodeThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @SwitchToThread()", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @Sleep(i32)", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WaitOnAddress(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @WakeByAddressSingle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @WakeByAddressAll(", llvm, StringComparison.Ordinal);
        Assert.Contains("define i32 @ThreadEntryThunk(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @StartThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @JoinThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DetachThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @YieldThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @SleepThreadMilliseconds(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWait(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWake(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @CreateThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WaitForSingleObject(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetExitCodeThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @SwitchToThread()", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @Sleep(i32", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WaitOnAddress(", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @WakeByAddressSingle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @WakeByAddressAll(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_create(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_join(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@pthread_detach(", llvm, StringComparison.Ordinal);
    }
}

