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

                fn i32[min max] Worker()
                {
                    return 7;
                }

                struct WorkerPayload
                {
                    i32[min max] ExitCode;
                }

                fn i32[min max] PayloadWorker(WorkerPayload payload)
                {
                    return payload.ExitCode;
                }

                fn bool JoinResultIs(System.Threading.ThreadJoinResult result, i32[min max] expected)
                {
                    switch (result)
                    {
                        case System.Threading.ThreadJoinResult.Ok(var value):
                            return value == expected;
                        case System.Threading.ThreadJoinResult.Err(var error):
                            return false;
                    }
                }

                fn bool StatusOk(System.Threading.ThreadStatus status)
                {
                    switch (status)
                    {
                        case System.Threading.ThreadStatus.Ok:
                            return true;
                        case System.Threading.ThreadStatus.Err(var error):
                            return false;
                    }
                }

                export fn i32[min max] main()
                {
                    stack ThreadEntry entry = Worker;
                    Thread.Yield();
                    Thread.SleepMilliseconds(0);
                    if (entry() != 7)
                    {
                        return 1;
                    }

                    stack ThreadEntry lambdaEntry = () => 11;
                    if (lambdaEntry() != 11)
                    {
                        return 7;
                    }

                    stack mut Thread worker = new(Worker);
                    if (!worker.IsJoinable())
                    {
                        return 2;
                    }

                    stack ThreadJoinResult joined = worker.Join();
                    if (!JoinResultIs(joined, 7))
                    {
                        return 3;
                    }

                    if (worker.IsJoinable())
                    {
                        return 4;
                    }

                    stack mut Thread detached = new(Worker);
                    stack ThreadStatus detachedStatus = detached.Detach();
                    if (!StatusOk(detachedStatus))
                    {
                        return 5;
                    }

                    if (detached.IsJoinable())
                    {
                        return 6;
                    }

                    stack mut Thread lambdaWorker = new(() => 13);
                    stack ThreadJoinResult lambdaJoined = lambdaWorker.Join();
                    if (!JoinResultIs(lambdaJoined, 13))
                    {
                        return 8;
                    }

                    stack WorkerPayload payload = new WorkerPayload()
                    {
                        ExitCode = 17
                    };
                    stack mut Thread payloadWorker = Thread.Start<WorkerPayload>(PayloadWorker, payload);
                    if (!payloadWorker.IsJoinable())
                    {
                        return 9;
                    }

                    stack ThreadJoinResult payloadJoined = payloadWorker.Join();
                    if (!JoinResultIs(payloadJoined, 17))
                    {
                        return 10;
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

                fn i32[min max] Worker()
                {
                    return 7;
                }

                fn i32[min max] Run()
                {
                    stack System.Threading.ThreadEntry entry = Worker;
                    stack System.Threading.ThreadEntry lambdaEntry = () => 11;
                    System.Threading.Thread.Yield();
                    System.Threading.Thread.SleepMilliseconds(0);
                    stack mut System.Threading.Thread worker = new(Worker);
                    if (!worker.IsJoinable())
                    {
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

        Assert.Equal(3, typeCheckModel.FunctionPointerPromotions.Count(promotion => promotion.Signature.Name == "Worker"));
        Assert.Contains(
            typeCheckModel.FunctionPointerPromotions,
            static promotion => promotion.Signature.Name.EndsWith("ThreadPayloadThunk", StringComparison.Ordinal)
                && promotion.TargetType.DisplayName == "fnptr<unsafe fn i32(rawmutptr<i8>)>");

        Assert.Equal(2, typeCheckModel.Lambdas.Count);
        Assert.All(
            typeCheckModel.Lambdas,
            lambda => Assert.Equal("fnptr<fn i32()>", lambda.FunctionPointerType.DisplayName));

        Assert.Equal(2, typeCheckModel.IndirectCalls.Count(indirectCall => indirectCall.FunctionPointerType.DisplayName == "fnptr<fn i32()>"));
        Assert.Contains(
            typeCheckModel.IndirectCalls,
            static indirectCall => indirectCall.FunctionPointerType.DisplayName.StartsWith("fnptr<fn i32(", StringComparison.Ordinal)
                && indirectCall.FunctionPointerType.DisplayName != "fnptr<fn i32()>");
    }

    [Fact]
    public void StdLibSourceThreadingSurfaceSupportsSynchronizedGuardedState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingSynchronizedSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct Counter
                {
                    i32[min max] Value;
                }

                fn i32[min max] Run()
                {
                    stack mut System.Threading.Synchronized<Counter> guarded =
                        new System.Threading.Synchronized<Counter>(new Counter()
                        {
                            Value = 1
                        });

                    stack mut System.Threading.Locked<Counter> guard = guarded.Lock();
                    guard.Value().Value += 4;
                    return guard.Value().Value;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Contains(typeCheckModel.NamedTypes.Keys, static name => name.StartsWith("System.Threading.Synchronized<", StringComparison.Ordinal));
        Assert.Contains(typeCheckModel.NamedTypes.Keys, static name => name.StartsWith("System.Threading.Locked<", StringComparison.Ordinal));
    }

    [Fact]
    public void StdLibSourceThreadingSynchronizedIsShareableWhenPayloadIsTransferable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingSynchronizedShareable.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct Counter
                {
                    i32[min max] Value;
                }

                fn void RequireShareable<T>(borrow T value) where Shareable(T)
                {
                    return;
                }

                fn void Run()
                {
                    stack mut System.Threading.Synchronized<Counter> guarded =
                        new System.Threading.Synchronized<Counter>(new Counter()
                        {
                            Value = 0
                        });
                    RequireShareable(guarded);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceThreadingSynchronizedShareabilityRequiresTransferablePayload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingSynchronizedNotShareable.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct RawHolder
                {
                    rawptr<i32[min max]> Pointer;
                }

                fn void RequireShareable<T>(borrow T value) where Shareable(T)
                {
                    return;
                }

                fn void Run()
                {
                    stack mut RawHolder raw = new()
                    {
                        Pointer = null
                    };
                    stack mut System.Threading.Synchronized<RawHolder> guarded =
                        new System.Threading.Synchronized<RawHolder>(raw);
                    RequireShareable(guarded);
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
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("requires where Shareable(System.Threading.Synchronized<RawHolder>)", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Responsible field chain: System.Threading.Synchronized<RawHolder>.Value.Pointer", StringComparison.Ordinal));
    }

    [Fact]
    public void StdLibSourceThreadingSurfaceRejectsProtectedBorrowEscapingLockedGuard()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingSynchronizedEscape.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct Counter
                {
                    i32[min max] Value;
                }

                static mut System.Threading.Synchronized<Counter> Shared =
                    new System.Threading.Synchronized<Counter>(new Counter()
                    {
                        Value = 0
                    });

                fn retborrow mut Counter Bad()
                {
                    stack mut System.Threading.Locked<Counter> guard = Shared.Lock();
                    return guard.Value();
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4202"
                && diagnostic.Message.Contains("Lifetime error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemThreadingSynchronizedGuardsSharedMutableStateAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-threading-sync-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Threading
                module App

                struct Counter
                {
                    i64[min max] Value;
                }

                static mut System.Threading.Synchronized<Counter> Shared =
                    new System.Threading.Synchronized<Counter>(new Counter()
                    {
                        Value = 0
                    });

                fn i32[min max] Worker()
                {
                    stack mut i32[min max] index = 0;
                    while willexit (index < 20000)
                    {
                        stack mut System.Threading.Locked<Counter> guard = Shared.Lock();
                        guard.Value().Value += 1;
                        index += 1;
                    }

                    return 0;
                }

                export fn i32[min max] main()
                {
                    stack mut System.Threading.Thread a = new(Worker);
                    stack mut System.Threading.Thread b = new(Worker);
                    a.Join();
                    b.Join();

                    stack mut System.Threading.Locked<Counter> guard = Shared.Lock();
                    if (guard.Value().Value == 40000)
                    {
                        return 0;
                    }

                    return 1;
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
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
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
    public void StdLibSourceThreadingSurfaceSupportsMpscChannels()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingChannelSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                fn bool StatusOk(System.Threading.ChannelStatus status)
                {
                    switch (status)
                    {
                        case System.Threading.ChannelStatus.Ok:
                            return true;
                        case System.Threading.ChannelStatus.Err(var error):
                            return false;
                    }
                }

                fn bool ReceiveValue(System.Threading.ChannelReceiveResult<i32[min max]> result, i32[min max] expected)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Item(var value):
                            return value == expected;
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Err(var error):
                            return false;
                    }
                }

                fn bool ReceiveError(System.Threading.ChannelReceiveResult<i32[min max]> result, System.Threading.ChannelError expected)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Item(var value):
                            return false;
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Err(var error):
                            return error == expected;
                    }
                }

                fn bool SenderRejected(System.Threading.ChannelSenderResult<i32[min max]> result, System.Threading.ChannelError expected)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var sender):
                            return false;
                        case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                            return error == expected;
                    }
                }

                fn i32[min max] Run()
                {
                    stack mut System.Threading.Channel<i32[min max]> channel = new();

                    switch (channel.CreateReceiver())
                    {
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Err(var error):
                            return 1;
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Ok(var receiverValue):
                            stack mut System.Threading.Receiver<i32[min max]> receiver = receiverValue;
                            switch (channel.CreateSender())
                            {
                                case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                                    return 2;
                                case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var firstValue):
                                    stack mut System.Threading.Sender<i32[min max]> first = firstValue;
                                    switch (channel.CreateSender())
                                    {
                                        case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                                            return 3;
                                        case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var secondValue):
                                            stack mut System.Threading.Sender<i32[min max]> second = secondValue;
                                            if (!StatusOk(first.Send(10)) || !StatusOk(second.Send(20)))
                                            {
                                                return 4;
                                            }

                                            if (channel.PendingCount() != 2)
                                            {
                                                return 5;
                                            }

                                            if (!ReceiveValue(receiver.Receive(), 10))
                                            {
                                                return 6;
                                            }

                                            if (!ReceiveValue(receiver.Receive(), 20))
                                            {
                                                return 7;
                                            }

                                            if (!ReceiveError(receiver.Receive(), System.Threading.ChannelError.Empty))
                                            {
                                                return 8;
                                            }

                                            first.Close();
                                            second.Close();
                                            if (!ReceiveError(receiver.Receive(), System.Threading.ChannelError.Closed))
                                            {
                                                return 9;
                                            }

                                            receiver.Close();
                                            if (!SenderRejected(channel.CreateSender(), System.Threading.ChannelError.Closed))
                                            {
                                                return 10;
                                            }

                                            return 0;
                                    }
                            }
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Contains(typeCheckModel.NamedTypes.Keys, static name => name.StartsWith("System.Threading.Channel<", StringComparison.Ordinal));
        Assert.Contains(typeCheckModel.NamedTypes.Keys, static name => name.StartsWith("System.Threading.Sender<", StringComparison.Ordinal));
        Assert.Contains(typeCheckModel.NamedTypes.Keys, static name => name.StartsWith("System.Threading.Receiver<", StringComparison.Ordinal));
    }

    [Fact]
    public void StdLibSourceThreadingChannelHandlesCarryThreadSafetyLawFacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingChannelLawFacts.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                fn void RequireShareable<T>(borrow T value) where Shareable(T)
                {
                    return;
                }

                fn void RequireTransfer<T>(T value) where Transferable(T)
                {
                    return;
                }

                fn void Run()
                {
                    stack mut System.Threading.Channel<i32[min max]> channel = new();
                    RequireShareable(channel);

                    switch (channel.CreateSender())
                    {
                        case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                            return;
                        case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var sender):
                            RequireTransfer(sender);
                            return;
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceThreadingChannelSendRequiresTransferablePayload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingChannelTransferable.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct RawHolder
                {
                    rawptr<i32[min max]> Pointer;
                }

                fn void Run()
                {
                    stack mut System.Threading.Channel<RawHolder> channel = new();
                    switch (channel.CreateSender())
                    {
                        case System.Threading.ChannelSenderResult<RawHolder>.Err(var error):
                            return;
                        case System.Threading.ChannelSenderResult<RawHolder>.Ok(var senderValue):
                            stack mut System.Threading.Sender<RawHolder> sender = senderValue;
                            stack RawHolder payload = new RawHolder()
                            {
                                Pointer = null
                            };
                            sender.Send(payload);
                            return;
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("requires where Transferable(RawHolder)", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Responsible field chain: RawHolder.Pointer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemThreadingChannelMovesMessagesAndObservesCloseAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-threading-channel-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Threading
                module App

                fn bool StatusOk(System.Threading.ChannelStatus status)
                {
                    switch (status)
                    {
                        case System.Threading.ChannelStatus.Ok:
                            return true;
                        case System.Threading.ChannelStatus.Err(var error):
                            return false;
                    }
                }

                fn bool ReceiveValue(System.Threading.ChannelReceiveResult<i32[min max]> result, i32[min max] expected)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Item(var value):
                            return value == expected;
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Err(var error):
                            return false;
                    }
                }

                fn bool ReceiveError(System.Threading.ChannelReceiveResult<i32[min max]> result, System.Threading.ChannelError expected)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Item(var value):
                            return false;
                        case System.Threading.ChannelReceiveResult<i32[min max]>.Err(var error):
                            return error == expected;
                    }
                }

                fn bool SendError(System.Threading.ChannelStatus status, System.Threading.ChannelError expected)
                {
                    switch (status)
                    {
                        case System.Threading.ChannelStatus.Ok:
                            return false;
                        case System.Threading.ChannelStatus.Err(var error):
                            return error == expected;
                    }
                }

                fn void DropReceiver(System.Threading.Receiver<i32[min max]> receiver)
                {
                    return;
                }

                fn i32[min max] ExerciseReceiverDrop()
                {
                    stack mut System.Threading.Channel<i32[min max]> channel = new();
                    switch (channel.CreateReceiver())
                    {
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Err(var error):
                            return 20;
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Ok(var receiverValue):
                            stack mut System.Threading.Receiver<i32[min max]> receiver = receiverValue;
                            switch (channel.CreateSender())
                            {
                                case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                                    return 21;
                                case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var senderValue):
                                    stack mut System.Threading.Sender<i32[min max]> sender = senderValue;
                                    DropReceiver(receiver);
                                    if (!SendError(sender.Send(99), System.Threading.ChannelError.Closed))
                                    {
                                        return 22;
                                    }

                                    sender.Close();
                                    return 0;
                            }
                    }
                }

                export fn i32[min max] main()
                {
                    stack mut System.Threading.Channel<i32[min max]> channel = new();

                    switch (channel.CreateReceiver())
                    {
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Err(var error):
                            return 1;
                        case System.Threading.ChannelReceiverResult<i32[min max]>.Ok(var receiverValue):
                            stack mut System.Threading.Receiver<i32[min max]> receiver = receiverValue;
                            switch (channel.CreateSender())
                            {
                                case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                                    return 2;
                                case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var firstValue):
                                    stack mut System.Threading.Sender<i32[min max]> first = firstValue;
                                    switch (channel.CreateSender())
                                    {
                                        case System.Threading.ChannelSenderResult<i32[min max]>.Err(var error):
                                            return 3;
                                        case System.Threading.ChannelSenderResult<i32[min max]>.Ok(var secondValue):
                                            stack mut System.Threading.Sender<i32[min max]> second = secondValue;
                                            if (!StatusOk(first.Send(31)) || !StatusOk(second.Send(11)))
                                            {
                                                return 4;
                                            }

                                            if (!ReceiveValue(receiver.Receive(), 31))
                                            {
                                                return 5;
                                            }

                                            if (!ReceiveValue(receiver.Receive(), 11))
                                            {
                                                return 6;
                                            }

                                            first.Close();
                                            second.Close();
                                            if (!ReceiveError(receiver.Receive(), System.Threading.ChannelError.Closed))
                                            {
                                                return 7;
                                            }

                                            return ExerciseReceiverDrop();
                                    }
                            }
                    }
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
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
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
    public async Task SystemThreadingChannelHandlesContendedProducersAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-threading-channel-contention-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Threading
                module App

                struct ProducerPayload
                {
                    System.Threading.Sender<i64[min max]> Sender;
                    i64[min max] First;
                    i64[min max] Count;
                }

                fn bool StatusOk(System.Threading.ChannelStatus status)
                {
                    switch (status)
                    {
                        case System.Threading.ChannelStatus.Ok:
                            return true;
                        case System.Threading.ChannelStatus.Err(var error):
                            return false;
                    }
                }

                fn bool JoinOk(System.Threading.ThreadJoinResult result)
                {
                    switch (result)
                    {
                        case System.Threading.ThreadJoinResult.Ok(var exitCode):
                            return exitCode == 0;
                        case System.Threading.ThreadJoinResult.Err(var error):
                            return false;
                    }
                }

                fn bool ReceiveClosed(System.Threading.ChannelReceiveResult<i64[min max]> result)
                {
                    switch (result)
                    {
                        case System.Threading.ChannelReceiveResult<i64[min max]>.Item(var value):
                            return false;
                        case System.Threading.ChannelReceiveResult<i64[min max]>.Err(var error):
                            return error == System.Threading.ChannelError.Closed;
                    }
                }

                fn i32[min max] Producer(ProducerPayload payload)
                {
                    stack i64[min max] first = payload.First;
                    stack i64[min max] count = payload.Count;
                    stack mut System.Threading.Sender<i64[min max]> sender = payload.Sender;
                    stack mut i64[min max] offset = 0;

                    while willexit (offset < count)
                    {
                        if (!StatusOk(sender.Send(first + offset)))
                        {
                            return 1;
                        }

                        offset += 1;
                    }

                    sender.Close();
                    return 0;
                }

                export fn i32[min max] main()
                {
                    stack mut System.Threading.Channel<i64[min max]> channel = new();

                    switch (channel.CreateReceiver())
                    {
                        case System.Threading.ChannelReceiverResult<i64[min max]>.Err(var error):
                            return 1;
                        case System.Threading.ChannelReceiverResult<i64[min max]>.Ok(var receiverValue):
                            stack mut System.Threading.Receiver<i64[min max]> receiver = receiverValue;
                            switch (channel.CreateSender())
                            {
                                case System.Threading.ChannelSenderResult<i64[min max]>.Err(var error):
                                    return 2;
                                case System.Threading.ChannelSenderResult<i64[min max]>.Ok(var firstSenderValue):
                                    stack mut System.Threading.Sender<i64[min max]> firstSender = firstSenderValue;
                                    switch (channel.CreateSender())
                                    {
                                        case System.Threading.ChannelSenderResult<i64[min max]>.Err(var error):
                                            return 3;
                                        case System.Threading.ChannelSenderResult<i64[min max]>.Ok(var secondSenderValue):
                                            stack mut System.Threading.Sender<i64[min max]> secondSender = secondSenderValue;
                                            stack ProducerPayload firstPayload = new ProducerPayload()
                                            {
                                                Sender = firstSender,
                                                First = 1,
                                                Count = 128
                                            };
                                            stack ProducerPayload secondPayload = new ProducerPayload()
                                            {
                                                Sender = secondSender,
                                                First = 1001,
                                                Count = 128
                                            };

                                            stack mut System.Threading.Thread firstThread =
                                                System.Threading.Thread.Start<ProducerPayload>(Producer, firstPayload);
                                            stack mut System.Threading.Thread secondThread =
                                                System.Threading.Thread.Start<ProducerPayload>(Producer, secondPayload);
                                            stack mut i64[min max] received = 0;
                                            stack mut i64[min max] sum = 0;

                                            while willexit (received < 256)
                                            {
                                                switch (receiver.Receive())
                                                {
                                                    case System.Threading.ChannelReceiveResult<i64[min max]>.Item(var value):
                                                        sum += value;
                                                        received += 1;
                                                    case System.Threading.ChannelReceiveResult<i64[min max]>.Err(var error):
                                                        if (error == System.Threading.ChannelError.Empty)
                                                        {
                                                            System.Threading.Thread.Yield();
                                                        }
                                                        else
                                                        {
                                                            return 4;
                                                        }
                                                }
                                            }

                                            if (!JoinOk(firstThread.Join()) || !JoinOk(secondThread.Join()))
                                            {
                                                return 5;
                                            }

                                            if (!ReceiveClosed(receiver.Receive()))
                                            {
                                                return 6;
                                            }

                                            if (sum != 144512)
                                            {
                                                return 7;
                                            }

                                            return 0;
                                    }
                            }
                    }
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
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
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
    public void StdLibSourceThreadingSurfaceSupportsPayloadThreadStarts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingPayloadEntry.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct WorkerPayload
                {
                    i32[min max] ExitCode;
                }

                fn i32[min max] Worker(WorkerPayload payload)
                {
                    return payload.ExitCode;
                }

                fn i32[min max] Run()
                {
                    stack WorkerPayload payload = new WorkerPayload()
                    {
                        ExitCode = 9
                    };
                    stack System.Threading.ThreadPayloadEntry<WorkerPayload> entry = Worker;
                    stack mut System.Threading.Thread worker = System.Threading.Thread.Start<WorkerPayload>(entry, payload);
                    worker.Detach();
                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.Contains(
            typeCheckModel.FunctionPointerPromotions,
            static promotion => promotion.Signature.Name == "Worker"
                && promotion.TargetType.DisplayName == "fnptr<fn i32(WorkerPayload)>");
    }

    [Fact]
    public void StdLibSourceThreadPayloadStartRequiresTransferablePayload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibThreadingPayloadTransferable.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Threading
                module Demo

                struct RawPayload
                {
                    rawptr<i32[min max]> Pointer;
                }

                fn i32[min max] Worker(RawPayload payload)
                {
                    return 0;
                }

                fn void Run()
                {
                    stack RawPayload payload = new RawPayload()
                    {
                        Pointer = null
                    };
                    stack mut System.Threading.Thread worker = System.Threading.Thread.Start<RawPayload>(Worker, payload);
                    worker.Detach();
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
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("requires where Transferable(RawPayload)", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Responsible field chain: RawPayload.Pointer", StringComparison.Ordinal));
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
        Assert.Contains("define fastcc noundef ptr @StartThreadWithContext(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @JoinThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DetachThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @LinuxThreadEntryThunk(", llvm, StringComparison.Ordinal);
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

        Assert.Contains("declare win64cc ptr @CreateThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WaitForSingleObject(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetExitCodeThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @SwitchToThread()", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @Sleep(i32)", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WaitOnAddress(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @WakeByAddressSingle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @WakeByAddressAll(", llvm, StringComparison.Ordinal);
        Assert.Contains("define win64cc i32 @ThreadEntryThunk(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @StartThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @StartThreadWithContext(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @JoinThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DetachThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @YieldThread(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @SleepThreadMilliseconds(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWait(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FutexWake(", llvm, StringComparison.Ordinal);
        Assert.Contains("call win64cc ptr @CreateThread(", llvm, StringComparison.Ordinal);
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
