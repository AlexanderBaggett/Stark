using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemNetTcpStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceNetTcpClosedHandleLifecycleTypeChecks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibNetTcpClosedLifecycle.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Net.Tcp
                module Demo

                fn bool StatusOk(System.Net.NetStatus status)
                {
                    switch (status)
                    {
                        case System.Net.NetStatus.Ok:
                            return true;
                        case System.Net.NetStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[min max] Run()
                {
                    stack System.Net.IPv4Endpoint endpoint = new System.Net.IPv4Endpoint()
                    {
                        Address = new System.Net.IPv4Address()
                        {
                            A = 127,
                            B = 0,
                            C = 0,
                            D = 1
                        },
                        Port = 80
                    };
                    stack System.Net.NetResult<System.Net.Tcp.TcpClient> connected =
                        System.Net.Tcp.TcpClient.Connect(endpoint);
                    switch (connected)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Ok(var connectedClient):
                            if (!connectedClient.IsOpen())
                            {
                                return 7;
                            }
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Err(var error):
                    }

                    stack System.Net.NetResult<System.Net.Tcp.TcpListener> listening =
                        System.Net.Tcp.TcpListener.Listen(endpoint);
                    switch (listening)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpListener>.Ok(var listeningSocket):
                            if (!listeningSocket.IsOpen())
                            {
                                return 9;
                            }
                        case System.Net.NetResult<System.Net.Tcp.TcpListener>.Err(var error):
                    }

                    stack mut System.Net.Tcp.TcpClient client = new();
                    if (client.IsOpen())
                    {
                        return 1;
                    }

                    if (StatusOk(client.Shutdown(System.Net.Tcp.TcpShutdown.Both)))
                    {
                        return 8;
                    }

                    stack mut i8[min max][4] buffer =
                    {
                        1, 2, 3, 4
                    };
                    stack System.Net.NetResult<u64[0 2 ** 63 - 1]> readResult = client.Read(buffer);
                    switch (readResult)
                    {
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                            return 10;
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
                    }

                    stack System.Net.NetResult<u64[0 2 ** 63 - 1]> writeResult = client.Write(buffer);
                    switch (writeResult)
                    {
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                            return 11;
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
                    }

                    if (!StatusOk(client.Close()))
                    {
                        return 2;
                    }

                    stack mut System.Net.Tcp.TcpListener listener = new();
                    if (listener.IsOpen())
                    {
                        return 3;
                    }

                    stack System.Net.NetResult<System.Net.Tcp.TcpClient> closedAccept = listener.Accept();
                    switch (closedAccept)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Ok(var acceptedClient):
                            return 13;
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Err(var error):
                    }

                    if (!StatusOk(listener.Close()))
                    {
                        return 4;
                    }

                    stack System.Net.Tcp.TcpShutdown shutdown = System.Net.Tcp.TcpShutdown.Both;
                    switch (shutdown)
                    {
                        case System.Net.Tcp.TcpShutdown.Receive:
                            return 5;
                        case System.Net.Tcp.TcpShutdown.Send:
                            return 6;
                        case System.Net.Tcp.TcpShutdown.Both:
                            return 0;
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "enum-layout"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);

        var shutdown = enumLayoutModel.Layouts["System.Net.Tcp.TcpShutdown"];
        AssertCompactTag(shutdown, bitWidth: 8, maxTagValue: 2);
        Assert.Equal(["$tag"], shutdown.OrderedFields.Select(static field => field.Name).ToArray());
    }

    [Fact]
    public async Task PackagedStdLibNetTcpClosedHandleLifecycleWorksWithoutSource()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-net-tcp-");
        var sharedPackage = await SharedStdlibPackage.GetAsync();
        var packageDirectory = sharedPackage.DirectoryPath;

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn bool StatusOk(System.Net.NetStatus status)
                {
                    switch (status)
                    {
                        case System.Net.NetStatus.Ok:
                            return true;
                        case System.Net.NetStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[min max] Run()
                {
                    stack System.Net.IPv4Endpoint endpoint = new System.Net.IPv4Endpoint()
                    {
                        Address = new System.Net.IPv4Address()
                        {
                            A = 127,
                            B = 0,
                            C = 0,
                            D = 1
                        },
                        Port = 80
                    };
                    stack System.Net.NetResult<System.Net.Tcp.TcpClient> connected =
                        System.Net.Tcp.TcpClient.Connect(endpoint);
                    switch (connected)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Ok(var connectedClient):
                            if (!connectedClient.IsOpen())
                            {
                                return 7;
                            }
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Err(var error):
                    }

                    stack System.Net.NetResult<System.Net.Tcp.TcpListener> listening =
                        System.Net.Tcp.TcpListener.Listen(endpoint);
                    switch (listening)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpListener>.Ok(var listeningSocket):
                            if (!listeningSocket.IsOpen())
                            {
                                return 9;
                            }
                        case System.Net.NetResult<System.Net.Tcp.TcpListener>.Err(var error):
                    }

                    stack mut System.Net.Tcp.TcpClient client = new();
                    if (client.IsOpen())
                    {
                        return 1;
                    }

                    if (StatusOk(client.Shutdown(System.Net.Tcp.TcpShutdown.Both)))
                    {
                        return 8;
                    }

                    stack mut i8[min max][4] buffer =
                    {
                        1, 2, 3, 4
                    };
                    stack System.Net.NetResult<u64[0 2 ** 63 - 1]> readResult = client.Read(buffer);
                    switch (readResult)
                    {
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                            return 10;
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
                    }

                    stack System.Net.NetResult<u64[0 2 ** 63 - 1]> writeResult = client.Write(buffer);
                    switch (writeResult)
                    {
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                            return 11;
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
                    }

                    if (!StatusOk(client.Close()))
                    {
                        return 2;
                    }

                    stack mut System.Net.Tcp.TcpListener listener = new();
                    if (listener.IsOpen())
                    {
                        return 3;
                    }

                    stack System.Net.NetResult<System.Net.Tcp.TcpClient> closedAccept = listener.Accept();
                    switch (closedAccept)
                    {
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Ok(var acceptedClient):
                            return 13;
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Err(var error):
                    }

                    if (!StatusOk(listener.Close()))
                    {
                        return 4;
                    }

                    return 0;
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    TargetInfo: sharedPackage.TargetInfo,
                    StopAfterPassId: "enum-layout"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);

            var shutdown = enumLayoutModel.Layouts["System.Net.Tcp.TcpShutdown"];
            AssertCompactTag(shutdown, bitWidth: 8, maxTagValue: 2);
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
    public void StdLibSourceNetTcpCloseRoutesOpenHandlesThroughPlatformSocketClose()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tcpPath = Path.Combine(sourceRoot, "System", "Net", "Tcp.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(tcpPath),
                tcpPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("@TcpClient_Read__", llvm, StringComparison.Ordinal);
        Assert.Contains("@TcpClient_Write__", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %System_Net_NetStatus @TcpClient_Shutdown(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %System_Net_NetStatus @TcpClient_Close(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %System_Net_NetStatus @TcpListener_Close(", llvm, StringComparison.Ordinal);
        Assert.Contains("@TcpClient_Connect(", llvm, StringComparison.Ordinal);
        Assert.Contains("@TcpListener_Listen(", llvm, StringComparison.Ordinal);
        Assert.Contains("@TcpListener_Accept(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc ptr @__stark_inline_clone_System_Runtime_Platform_Linux_ConnectTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc ptr @__stark_inline_clone_System_Runtime_Platform_Linux_ListenTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_Linux_LinuxReadSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_Linux_LinuxWriteSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("GetMutableByteSliceParts", llvm, StringComparison.Ordinal);
        Assert.Contains("GetByteSliceParts", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_Linux_LinuxShutdownSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_Linux_LinuxCloseSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %System_Net_NetStatus @StatusFromPlatformResult(", llvm, StringComparison.Ordinal);
        Assert.Contains("@ByteCountFromPlatformResult(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc %System_Net_NetworkError @NetworkErrorFromPlatformResult(", llvm, StringComparison.Ordinal);

        var clientCloseBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Net_NetStatus @TcpClient_Close(",
            "Expected TcpClient.Close definition.");
        var listenerCloseBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Net_NetStatus @TcpListener_Close(",
            "Expected TcpListener.Close definition.");
        AssertCloseBodyRoutesOpenHandleThroughPlatformClose(clientCloseBody);
        AssertCloseBodyRoutesOpenHandleThroughPlatformClose(listenerCloseBody);
    }

    [Fact]
    public void StdLibSourceLinuxSocketCloseUsesCloseSyscallPath()
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

        Assert.Contains("define fastcc noundef i32 @CloseSocket(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 @CloseFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Handle(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@closesocket(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTcpConnectUsesSocketAndConnectSyscallPath()
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

        Assert.Contains("define fastcc noundef ptr @ConnectTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3Integers(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc void @WriteTcpIPv4Sockaddr(", llvm, StringComparison.Ordinal);
        var connectBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef ptr @ConnectTcpIPv4(",
            "Expected Linux ConnectTcpIPv4 definition.");
        // LinuxCloseSyscallNumber (3) is a comptime constant that folds into the call site.
        Assert.Contains("call i64 @LinuxSyscall1Handle(i64 range(i64 3, 4) 3, ptr readonly", connectBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 @CloseFile(", connectBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTcpShutdownUsesShutdownSyscallPath()
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

        Assert.Contains("define fastcc noundef i32 @ShutdownSocket(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall2HandleInteger(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxShutdownSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@shutdown(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTcpReadWriteUseReadAndWriteSyscallPaths()
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

        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @ReadSocket\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @WriteSocket\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @ReadSocketVector2\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @WriteSocketVector2\(", llvm);
        Assert.Contains("%LinuxIovec = type { ptr, i64 }", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxReadSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxWriteSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxReadvSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxWritevSyscallNumber", llvm, StringComparison.Ordinal);

        var readVectorBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i64 @ReadSocketVector2(",
            "Expected Linux ReadSocketVector2 definition.");
        var writeVectorBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i64 @WriteSocketVector2(",
            "Expected Linux WriteSocketVector2 definition.");
        Assert.Contains("[2 x %LinuxIovec]", readVectorBody, StringComparison.Ordinal);
        // LinuxReadvSyscallNumber (19) / LinuxWritevSyscallNumber (20) are comptime
        // constants that fold into the syscall wrapper call sites.
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(i64 range(i64 19, 20) 19, ptr readonly", readVectorBody, StringComparison.Ordinal);
        Assert.Contains("i64 range(i64 2, 3) 2)", readVectorBody, StringComparison.Ordinal);
        Assert.Contains("[2 x %LinuxIovec]", writeVectorBody, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(i64 range(i64 20, 21) 20, ptr readonly", writeVectorBody, StringComparison.Ordinal);
        Assert.Contains("i64 range(i64 2, 3) 2)", writeVectorBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@recv(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@send(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@readv(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@writev(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTcpListenUsesSocketBindAndListenSyscallPath()
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

        Assert.Contains("define fastcc noundef ptr @ListenTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3Integers(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall2HandleInteger(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxBindSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxListenSyscallNumber", llvm, StringComparison.Ordinal);
        var listenBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef ptr @ListenTcpIPv4(",
            "Expected Linux ListenTcpIPv4 definition.");
        // LinuxCloseSyscallNumber (3) is a comptime constant that folds into the call site.
        Assert.Contains("call i64 @LinuxSyscall1Handle(i64 range(i64 3, 4) 3, ptr readonly", listenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 @CloseFile(", listenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@bind(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@listen(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTcpAcceptUsesAccept4SyscallPath()
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

        Assert.Contains("define fastcc noundef ptr @AcceptSocket(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4HandlePointersInteger(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxAccept4SyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxSocketCloseOnExecFlag", llvm, StringComparison.Ordinal);
        var acceptBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef ptr @AcceptSocket(",
            "Expected Linux AcceptSocket definition.");
        Assert.DoesNotContain("@LinuxCloseSyscallNumber", acceptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call i64 @LinuxSyscall1Handle(", acceptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@accept(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@accept4(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsTcpUsesWinsockPath()
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

        Assert.Contains("declare i32 @WSAStartup(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WSAGetLastError(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare win64cc i32 @InitOnceExecuteOnce(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @WSASocketW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @connect(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @bind(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @listen(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @accept(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @recv(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @send(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WSARecv(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WSASend(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @shutdown(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @closesocket(", llvm, StringComparison.Ordinal);

        Assert.Contains("%WinSocketBuffer = type { i32, ptr }", llvm, StringComparison.Ordinal);
        Assert.Contains("%WinInitOnce = type { ptr }", llvm, StringComparison.Ordinal);
        Assert.Contains("@WinSockInitOnce = global %WinInitOnce { ptr null }", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @ConnectTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @ListenTcpIPv4(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @AcceptSocket(", llvm, StringComparison.Ordinal);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @ReadSocket\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @WriteSocket\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @ReadSocketVector2\(", llvm);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @WriteSocketVector2\(", llvm);
        Assert.Contains("define fastcc noundef i32 @ShutdownSocket(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseSocket(", llvm, StringComparison.Ordinal);

        Assert.Contains("call i32 @WSAStartup(", llvm, StringComparison.Ordinal);
        Assert.Contains("call win64cc i32 @InitOnceExecuteOnce(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @WSASocketW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @connect(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @bind(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @listen(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @accept(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @recv(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @send(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WSARecv(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WSASend(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @shutdown(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @closesocket(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
    }

    private static void AssertCloseBodyRoutesOpenHandleThroughPlatformClose(string body)
    {
        Assert.Contains("call i64 @LinuxSyscall1Handle(i64 range(i64 3, 4) 3, ptr readonly", body, StringComparison.Ordinal);
        Assert.True(
            body.Contains("call fastcc %System_Net_NetStatus @StatusFromPlatformResult(", StringComparison.Ordinal)
            || (body.Contains("icmp slt i32", StringComparison.Ordinal)
                && body.Contains("call fastcc %System_Net_NetworkError @NetworkErrorFromPlatformResult(", StringComparison.Ordinal)
                && body.Contains("insertvalue %System_Net_NetStatus", StringComparison.Ordinal)),
            body);
    }

    private static void AssertCompactTag(EnumLayoutSymbol layout, int bitWidth, int maxTagValue)
    {
        Assert.Equal("$tag", layout.TagField.Name);
        Assert.Equal(StarkTypeKind.Integer, layout.TagField.Type.Kind);
        Assert.Equal(bitWidth, layout.TagField.Type.BitWidth);
        Assert.Equal(System.Numerics.BigInteger.Zero, layout.TagField.Type.RangeMin);
        Assert.Equal(new System.Numerics.BigInteger(maxTagValue), layout.TagField.Type.RangeMax);
    }
}
