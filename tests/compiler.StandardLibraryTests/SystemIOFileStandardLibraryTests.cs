using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOFileStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceRawFileHandleHelpersStayInternal()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var filePath = Path.Combine(sourceRoot, "System", "IO", "File.stark");
        var source = File.ReadAllText(filePath);

        Assert.Contains("internal unsafe fn rawptr<i8[min max]> OpenRead", source, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn rawptr<i8[min max]> OpenWrite", source, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn i64[min max] ReadBytes", source, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn void WriteLine(rawptr<i8[min max]> handle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe fn rawptr<i8[min max]> OpenRead", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe fn rawptr<i8[min max]> OpenWrite", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe fn i64[min max] ReadBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public unsafe fn void WriteLine(rawptr<i8[min max]> handle", source, StringComparison.Ordinal);

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                source,
                filePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceWholeFileHelpersUseExplicitStatusAndChunkedBuffers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var filePath = Path.Combine(sourceRoot, "System", "IO", "File.stark");
        var source = File.ReadAllText(filePath);

        Assert.Contains("public fn System.IO.IOStatus ReadAllBytesInto(ascii path, mut borrow System.Runtime.Buffer.DynamicByteBuffer destination)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer> ReadAllBytes(ascii path)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus ReadAllTextInto(ascii path, mut borrow System.Text.OwnedAscii destination)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOResult<System.Text.OwnedAscii> ReadAllText(ascii path)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow i8[min max][] source)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus WriteAllText(ascii path, ascii text)", source, StringComparison.Ordinal);
        Assert.Contains("stack mut i8[min max][8192] storage;", source, StringComparison.Ordinal);
        Assert.Contains("destination.WriteSlice(chunk, count)", source, StringComparison.Ordinal);
        Assert.Contains("destination.AppendSlice(chunk, count)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText(ascii path, mut borrow", source, StringComparison.Ordinal);

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                source,
                filePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceAtomicWholeFileHelpersUseExclusiveCreateSyncAndMove()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var filePath = Path.Combine(sourceRoot, "System", "IO", "File.stark");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var macOSPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "MacOS.stark");
        var windowsPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Windows.stark");
        var source = File.ReadAllText(filePath);
        var linuxSource = File.ReadAllText(linuxPath);
        var macOSSource = File.ReadAllText(macOSPath);
        var windowsSource = File.ReadAllText(windowsPath);

        Assert.Contains("CreateNew,", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow i8[min max][] source)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, ascii text)", source, StringComparison.Ordinal);
        Assert.Contains("public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, unicode text)", source, StringComparison.Ordinal);
        Assert.Contains("Open(tempPath.View(), FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("file.SyncAll()", source, StringComparison.Ordinal);
        Assert.Contains("Move(tempPath, targetPath)", source, StringComparison.Ordinal);
        Assert.Contains("Delete(tempPath)", source, StringComparison.Ordinal);
        Assert.Contains("internal enum OpenHandleResult", source, StringComparison.Ordinal);
        Assert.Contains("System.Runtime.Platform.OpenFileCreateNewResult(path)", source, StringComparison.Ordinal);
        Assert.Contains("case System.Runtime.Platform.FileOpenResult.Err(var error):", source, StringComparison.Ordinal);
        Assert.Contains("return OpenHandleResult.Err(IOErrorFromPlatformResult(error));", source, StringComparison.Ordinal);

        Assert.Contains("const LinuxFsyncSyscallNumber = 74;", linuxSource, StringComparison.Ordinal);
        Assert.Contains("const LinuxOpenExclusiveFlag = 128;", linuxSource, StringComparison.Ordinal);
        Assert.Contains("internal enum FileOpenResult", linuxSource, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn FileOpenResult OpenFileCreateNewResult(ascii path)", linuxSource, StringComparison.Ordinal);
        Assert.Contains("return FileOpenResult.Err((i32[min max])syscallResult);", linuxSource, StringComparison.Ordinal);
        Assert.Contains("LinuxOpenExclusiveFlag | LinuxOpenCloseOnExecFlag", linuxSource, StringComparison.Ordinal);
        Assert.Contains("LinuxSyscall1Handle(LinuxFsyncSyscallNumber, handle)", linuxSource, StringComparison.Ordinal);

        Assert.Contains("const MacOSOpenExclusiveFlag = 2048;", macOSSource, StringComparison.Ordinal);
        Assert.Contains("internal unsafe ffi fn rawmutptr<i32[min max]> __error();", macOSSource, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn FileOpenResult OpenFileCreateNewResult(ascii path)", macOSSource, StringComparison.Ordinal);
        Assert.Contains("return FileOpenResult.Err(NegativeErrno());", macOSSource, StringComparison.Ordinal);
        Assert.Contains("MacOSOpenExclusiveFlag | MacOSOpenCloseOnExecFlag", macOSSource, StringComparison.Ordinal);
        Assert.Contains("fsync(HandleToFd(handle))", macOSSource, StringComparison.Ordinal);

        Assert.Contains("const WinCreateNew = 1;", windowsSource, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn FileOpenResult OpenFileCreateNewResult(ascii path)", windowsSource, StringComparison.Ordinal);
        Assert.Contains("OpenFileWithDispositionResult(path, WinGenericWrite, WinCreateNew)", windowsSource, StringComparison.Ordinal);
        Assert.Contains("return FileOpenResult.Err(NegativeLastError());", windowsSource, StringComparison.Ordinal);
        Assert.Contains("FlushFileBuffers(handle)", windowsSource, StringComparison.Ordinal);

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                source,
                filePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceOwnedFileHandlesSupportAsciiAndUnicodeWriteOverloads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibOwnedFileUnicodeSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }
                unsafe fn void Use()
                {
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("demo.txt", System.IO.File.FileMode.Write));
                    file.WriteText("ascii");
                    file.WriteText((unicode)"ascii");
                    file.WriteLine("line");
                    file.WriteLine((unicode)"line");
                    file.Flush();
                    file.SyncAll();
                    file.Close();
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceFileSeekSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibFileSeekSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }
                unsafe fn void Use()
                {
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("demo.txt", System.IO.File.FileMode.ReadWrite));
                    file.Seek(0, System.IO.File.SeekOrigin.Begin);
                    file.Seek(1, System.IO.File.SeekOrigin.Current);
                    file.Seek(-1, System.IO.File.SeekOrigin.End);
                    file.Close();
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceOwnedFileFlushDrainsOnlyUserBufferAndSyncAllCallsPlatformFlush()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var filePath = Path.Combine(sourceRoot, "System", "IO", "File.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(filePath),
                filePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        foreach (var signaturePrefix in new[]
        {
            "define fastcc noundef %System_IO_IOStatus @File_Close(",
            "define fastcc noundef %System_IO_IOStatus @File_Flush(",
            "define fastcc noundef i64 @File_ReadBytes(",
            "define fastcc noundef i64 @File_WriteBytes(",
            "define fastcc noundef i64 @File_ReadByteRegion(",
            "define fastcc noundef i64 @File_WriteByteRegion(",
            "define fastcc noundef i64 @File_SeekRaw(",
            "define fastcc noundef i32 @Flush("
        })
        {
            var body = ExtractDefinedFunctionText(llvm, signaturePrefix, $"Expected {signaturePrefix} definition in emitted LLVM.");
            Assert.DoesNotContain("@System_Runtime_Platform_FlushFile(", body, StringComparison.Ordinal);
        }

        foreach (var signaturePrefix in new[]
        {
            "define fastcc noundef i32 @File_CloseRaw(",
            "define fastcc noundef i32 @File_FlushRaw("
        })
        {
            var body = ExtractDefinedFunctionText(llvm, signaturePrefix, $"Expected {signaturePrefix} definition in emitted LLVM.");
            Assert.Contains("@File_FlushBufferedWrite(", body, StringComparison.Ordinal);
        }

        foreach (var signaturePrefix in new[]
        {
            "define fastcc noundef i64 @File_ReadBytes(",
            "define fastcc noundef i64 @File_WriteBytes(",
            "define fastcc noundef i64 @File_ReadByteRegion(",
            "define fastcc noundef i64 @File_WriteByteRegion(",
            "define fastcc noundef i64 @File_SeekRaw("
        })
        {
            var body = ExtractDefinedFunctionText(llvm, signaturePrefix, $"Expected {signaturePrefix} definition in emitted LLVM.");
            Assert.Contains("@File_FlushRaw(", body, StringComparison.Ordinal);
        }

        var ownedSyncBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_IO_IOStatus @File_SyncAll(",
            "Expected File.SyncAll definition in emitted LLVM.");
        var ownedRawSyncBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i32 @File_SyncAllRaw(",
            "Expected File.SyncAllRaw definition in emitted LLVM.");
        var rawSyncBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i32 @SyncAll(",
            "Expected raw SyncAll definition in emitted LLVM.");

        Assert.Contains("@File_SyncAllRaw(", ownedSyncBody, StringComparison.Ordinal);
        Assert.Contains("@File_FlushBufferedWrite(", ownedRawSyncBody, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_FlushFile(", ownedRawSyncBody, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_FlushFile(", rawSyncBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceFileBufferedAsciiAppendsUseInlineAsmCopyHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var filePath = Path.Combine(sourceRoot, "System", "IO", "File.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(filePath),
                filePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var appendBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @File_TryAppendBufferedAscii(",
            "Expected File.TryAppendBufferedAscii definition in emitted LLVM.");

        Assert.Contains("define void @CopyAsciiBytes(", llvm, StringComparison.Ordinal);
        Assert.Contains("rep movsb", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @CopyAsciiBytes(", appendBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@llvm.memcpy", appendBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxFileSeekUsesLseekSyscallPath()
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

        Assert.Contains("define fastcc noundef i64 @SeekFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxLseekSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleIntegerInteger(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@lseek(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxFileSyncUsesFsyncSyscallPath()
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

        Assert.Contains("define fastcc noundef i32 @FlushFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("@LinuxFsyncSyscallNumber", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Handle(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibFileSeekRoundTripsOnLinux()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-file-seek-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                fn i64[min max] CountOrNegative(System.IO.IOResult<u64[0 2 ** 63 - 1]> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Ok(var value):
                            return (i64[min max])value;
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Err(var error):
                            return -1;
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    stack mut i8[min max][1] buffer =
                    {
                        0
                    };
                    stack mut System.IO.File.File read = OpenOrEmpty(System.IO.File.Open("seek.txt", System.IO.File.FileMode.Read));
                    if (!read.IsOpen())
                    {
                        return 1;
                    }

                    stack mut System.IO.File.File result = OpenOrEmpty(System.IO.File.Open("seek-result.txt", System.IO.File.FileMode.Write));
                    if (!result.IsOpen())
                    {
                        return 2;
                    }

                    stack mut i8[min max][] byte = slice(&buffer[0], 1);

                    if (CountOrNegative(read.Seek(2, System.IO.File.SeekOrigin.Begin)) != 2)
                    {
                        return 3;
                    }

                    if (CountOrNegative(read.Read(byte)) != 1)
                    {
                        return 4;
                    }

                    if (CountOrNegative(result.Write(byte)) != 1)
                    {
                        return 5;
                    }

                    if (CountOrNegative(read.Seek(-1, System.IO.File.SeekOrigin.Current)) != 2)
                    {
                        return 6;
                    }

                    if (CountOrNegative(read.Read(byte)) != 1)
                    {
                        return 7;
                    }

                    if (CountOrNegative(result.Write(byte)) != 1)
                    {
                        return 8;
                    }

                    if (CountOrNegative(read.Seek(-1, System.IO.File.SeekOrigin.End)) != 4)
                    {
                        return 9;
                    }

                    if (CountOrNegative(read.Read(byte)) != 1)
                    {
                        return 10;
                    }

                    if (CountOrNegative(result.Write(byte)) != 1)
                    {
                        return 11;
                    }

                    if (!StatusOk(read.Close()))
                    {
                        return 12;
                    }

                    if (!StatusOk(result.Close()))
                    {
                        return 13;
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

            await File.WriteAllTextAsync(Path.Combine(tempDirectory.FullName, "seek.txt"), "abcde");

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
            Assert.Equal("abcde", await File.ReadAllTextAsync(Path.Combine(tempDirectory.FullName, "seek.txt")));
            Assert.Equal("cce", await File.ReadAllTextAsync(Path.Combine(tempDirectory.FullName, "seek-result.txt")));
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
    public async Task SourceStdLibWholeFileHelpersRoundTripOnLinux()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-file-whole-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                import System.Text
                import System.Runtime.Buffer
                module App

                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool MemoryStatusOk(System.Memory.MemoryStatus status)
                {
                    switch (status)
                    {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[min max] CheckText(System.IO.IOResult<System.Text.OwnedAscii> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
                            return 10;
                        case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var text):
                            if (text.Length() != 8)
                            {
                                return 11;
                            }

                            stack i8[min max][] bytes = text.AsSlice();
                            if (bytes[0] != (i8[min max])97 || bytes[7] != (i8[min max])116)
                            {
                                return 12;
                            }

                            return 0;
                    }
                }

                fn i32[min max] CheckBytes(System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer>.Err(var error):
                            return 20;
                        case System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer>.Ok(var bytes):
                            if (bytes.Length() != 4 || bytes.Readable() != 4)
                            {
                                return 21;
                            }

                            stack i8[min max][] view = bytes.ReadSlice();
                            if (view[0] != (i8[min max])65 || view[1] != (i8[min max])66 || view[2] != (i8[min max])67 || view[3] != (i8[min max])68)
                            {
                                return 22;
                            }

                            return 0;
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    if (!StatusOk(System.IO.File.WriteAllText("text.txt", "alphabet")))
                    {
                        return 1;
                    }

                    stack i32[min max] textResult = CheckText(System.IO.File.ReadAllText("text.txt"));
                    if (textResult != 0)
                    {
                        return textResult;
                    }

                    stack mut System.Text.OwnedAscii appended = new();
                    if (!MemoryStatusOk(appended.AppendAscii("pre:")))
                    {
                        return 2;
                    }

                    if (!StatusOk(System.IO.File.ReadAllTextInto("text.txt", appended)))
                    {
                        return 3;
                    }

                    if (appended.Length() != 12)
                    {
                        return 4;
                    }

                    stack i8[min max][] appendedView = appended.AsSlice();
                    if (appendedView[0] != (i8[min max])112 || appendedView[3] != (i8[min max])58 || appendedView[4] != (i8[min max])97 || appendedView[11] != (i8[min max])116)
                    {
                        return 5;
                    }

                    stack mut i8[min max][4] raw =
                    {
                        65, 66, 67, 68
                    };
                    stack i8[min max][] rawSlice = slice(&raw[0], 4);
                    if (!StatusOk(System.IO.File.WriteAllBytes("bytes.bin", rawSlice)))
                    {
                        return 6;
                    }

                    stack i32[min max] byteResult = CheckBytes(System.IO.File.ReadAllBytes("bytes.bin"));
                    if (byteResult != 0)
                    {
                        return byteResult;
                    }

                    stack mut System.Runtime.Buffer.DynamicByteBuffer byteDestination = new();
                    if (!StatusOk(System.IO.File.ReadAllBytesInto("bytes.bin", byteDestination)))
                    {
                        return 7;
                    }

                    if (byteDestination.Length() != 4 || byteDestination.Readable() != 4)
                    {
                        return 8;
                    }

                    stack i8[min max][] byteDestinationView = byteDestination.ReadSlice();
                    if (byteDestinationView[0] != (i8[min max])65 || byteDestinationView[3] != (i8[min max])68)
                    {
                        return 9;
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

            Assert.True(exitCode == 0, stderr.ToString());
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
            Assert.Equal("alphabet", await File.ReadAllTextAsync(Path.Combine(tempDirectory.FullName, "text.txt")));
            Assert.Equal(new byte[] { 65, 66, 67, 68 }, await File.ReadAllBytesAsync(Path.Combine(tempDirectory.FullName, "bytes.bin")));
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
    public async Task SourceStdLibAtomicWholeFileHelpersReplaceOnLinux()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-file-atomic-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                import System.Text
                import System.Runtime.Buffer
                module App

                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[min max] CheckText(System.IO.IOResult<System.Text.OwnedAscii> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
                            return 10;
                        case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var text):
                            if (text.Length() != 9)
                            {
                                return 11;
                            }

                            stack i8[min max][] bytes = text.AsSlice();
                            if (bytes[0] != (i8[min max])110 || bytes[8] != (i8[min max])101)
                            {
                                return 12;
                            }

                            return 0;
                    }
                }

                fn i32[min max] CheckBytes(System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer>.Err(var error):
                            return 20;
                        case System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer>.Ok(var bytes):
                            if (bytes.Length() != 3 || bytes.Readable() != 3)
                            {
                                return 21;
                            }

                            stack i8[min max][] view = bytes.ReadSlice();
                            if (view[0] != (i8[min max])80 || view[1] != (i8[min max])81 || view[2] != (i8[min max])82)
                            {
                                return 22;
                            }

                            return 0;
                    }
                }

                fn bool CreateNewFailsWhenTargetExists()
                {
                    stack mut System.IO.File.File file = new();
                    switch (System.IO.File.Open("atomic.txt", System.IO.File.FileMode.CreateNew))
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var opened):
                            file = opened;
                            file.Close();
                            return false;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            switch (error)
                            {
                                case System.IO.IOError.AlreadyExists:
                                    return true;
                                case System.IO.IOError.NotFound:
                                    return false;
                                case System.IO.IOError.PermissionDenied:
                                    return false;
                                case System.IO.IOError.InvalidPath:
                                    return false;
                                case System.IO.IOError.BrokenPipe:
                                    return false;
                                case System.IO.IOError.DiskFull:
                                    return false;
                                case System.IO.IOError.Unknown(var code):
                                    return false;
                            }
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    if (!StatusOk(System.IO.File.WriteAllText("atomic.txt", "old")))
                    {
                        return 1;
                    }

                    if (!StatusOk(System.IO.File.WriteAllTextAtomic("atomic.txt", "new-value")))
                    {
                        return 2;
                    }

                    stack i32[min max] textResult = CheckText(System.IO.File.ReadAllText("atomic.txt"));
                    if (textResult != 0)
                    {
                        return textResult;
                    }

                    if (!CreateNewFailsWhenTargetExists())
                    {
                        return 3;
                    }

                    stack mut i8[min max][3] raw =
                    {
                        80, 81, 82
                    };
                    stack i8[min max][] rawSlice = slice(&raw[0], 3);
                    if (!StatusOk(System.IO.File.WriteAllBytesAtomic("bytes.bin", rawSlice)))
                    {
                        return 4;
                    }

                    stack i32[min max] byteResult = CheckBytes(System.IO.File.ReadAllBytes("bytes.bin"));
                    if (byteResult != 0)
                    {
                        return byteResult;
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

            Assert.True(exitCode == 0, stderr.ToString());
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
            Assert.Equal("new-value", await File.ReadAllTextAsync(Path.Combine(tempDirectory.FullName, "atomic.txt")));
            Assert.Equal(new byte[] { 80, 81, 82 }, await File.ReadAllBytesAsync(Path.Combine(tempDirectory.FullName, "bytes.bin")));
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
    public async Task PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-owned-file-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                fn i64[min max] CountOrNegative(System.IO.IOResult<u64[0 2 ** 63 - 1]> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Ok(var value):
                            return (i64[min max])value;
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Err(var error):
                            return -1;
                    }
                }

                fn void WriteOwned()
                {
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("owned-test.txt", System.IO.File.FileMode.Write));
                    file.WriteLine("Owned");
                    return;
                }

                export unsafe fn i32[min max] main()
                {
                    WriteOwned();

                    if (!BoolOrFalse(System.IO.File.Exists("owned-test.txt")))
                    {
                        return 2;
                    }

                    if (BoolOrFalse(System.IO.File.Exists("missing-test.txt")))
                    {
                        return 3;
                    }

                    stack mut i8[min max][8] buffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("owned-test.txt", System.IO.File.FileMode.Read));
                    stack mut i8[min max][] destination = slice(&buffer[0], 6);
                    stack i64[min max] count = CountOrNegative(file.Read(destination));
                    file.Close();

                    if (count != 6)
                    {
                        return 4;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
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
                WorkingDirectory = appDirectory,
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
            Assert.Equal("Owned\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "owned-test.txt")));
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
    public async Task PackagedStdLibFileBufferingModesBehaveAsExpected()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-buffering-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                fn i64[min max] CountOrNegative(System.IO.IOResult<u64[0 2 ** 63 - 1]> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Ok(var value):
                            return (i64[min max])value;
                        case System.IO.IOResult<u64[0 2 ** 63 - 1]>.Err(var error):
                            return -1;
                    }
                }

                unsafe fn i64[min max] ReadCount(ascii path, u64[0 2 ** 63 - 1] expected)
                {
                    stack mut i8[min max][16] buffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open(path, System.IO.File.FileMode.Read));
                    if (!file.IsOpen())
                    {
                        return -1;
                    }

                    stack mut i8[min max][] destination = slice(&buffer[0], expected);
                    stack i64[min max] count = CountOrNegative(file.Read(destination));
                    file.Close();
                    return count;
                }

                export unsafe fn i32[min max] main()
                {
                    stack mut System.IO.File.File defaulted = OpenOrEmpty(System.IO.File.Open("default.txt", System.IO.File.FileMode.Write));
                    defaulted.WriteLine("Default");
                    if (ReadCount("default.txt", 8) != 0)
                    {
                        return 1;
                    }

                    if (!StatusOk(defaulted.Close()))
                    {
                        return 2;
                    }

                    if (ReadCount("default.txt", 8) != 8)
                    {
                        return 3;
                    }

                    stack mut System.IO.File.File full = OpenOrEmpty(System.IO.File.Open("full.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Full));
                    full.WriteLine("Full");
                    if (ReadCount("full.txt", 5) != 0)
                    {
                        return 4;
                    }

                    if (!StatusOk(full.Flush()))
                    {
                        return 5;
                    }

                    if (ReadCount("full.txt", 5) != 5)
                    {
                        return 6;
                    }

                    if (!StatusOk(full.Close()))
                    {
                        return 7;
                    }

                    stack mut System.IO.File.File line = OpenOrEmpty(System.IO.File.Open("line.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    line.WriteLine("Line");
                    if (ReadCount("line.txt", 5) != 5)
                    {
                        return 8;
                    }

                    if (!StatusOk(line.Close()))
                    {
                        return 9;
                    }

                    stack mut System.IO.File.File none = OpenOrEmpty(System.IO.File.Open("none.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.None));
                    none.WriteText("None");
                    if (ReadCount("none.txt", 4) != 4)
                    {
                        return 10;
                    }

                    if (!StatusOk(none.Close()))
                    {
                        return 11;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
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
                WorkingDirectory = appDirectory,
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
            Assert.Equal("Default\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "default.txt")));
            Assert.Equal("Full\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "full.txt")));
            Assert.Equal("Line\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "line.txt")));
            Assert.Equal("None", await File.ReadAllTextAsync(Path.Combine(appDirectory, "none.txt")));
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
    public async Task PackagedStdLibOwnedFileWritesHonorExplicitTextEncodings()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-file-encodings-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                import System.Text
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }
                export unsafe fn i32[min max] main()
                {
                    stack mut i32[min max][1] gothicBuffer =
                    {
                        66376
                    };
                    stack mut Unicode gothic = new Unicode()
                    {
                        Data = &gothicBuffer[0],
                        Length = 1,
                        Capacity = 1
                    };

                    stack mut System.IO.File.File utf8 = OpenOrEmpty(System.IO.File.Open("utf8.txt", System.IO.File.FileMode.Write, System.Text.Encoding.UTF8));
                    utf8.WriteText("Hi ");
                    utf8.WriteLine((unicode)"Î±");
                    if (!StatusOk(utf8.Close()))
                    {
                        return 1;
                    }

                    stack mut System.IO.File.File utf16 = OpenOrEmpty(System.IO.File.Open("utf16.txt", System.IO.File.FileMode.Write, System.Text.Encoding.UTF16));
                    utf16.WriteText("A");
                    utf16.WriteText(System.Text.UnicodeView(gothic));
                    utf16.WriteLine((unicode)"Î²");
                    if (!StatusOk(utf16.Close()))
                    {
                        return 2;
                    }

                    gothic = new Unicode()
                    {
                        Data = &gothicBuffer[0],
                        Length = 1,
                        Capacity = 1
                    };

                    stack mut System.IO.File.File utf32 = OpenOrEmpty(System.IO.File.Open("utf32.txt", System.IO.File.FileMode.Write, System.Text.Encoding.UTF32));
                    utf32.WriteText("Z");
                    utf32.WriteText(System.Text.UnicodeView(gothic));
                    utf32.WriteLine((unicode)"Î³");
                    if (!StatusOk(utf32.Close()))
                    {
                        return 3;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
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
                WorkingDirectory = appDirectory,
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

            var gothic = char.ConvertFromUtf32(66376);
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes("Hi Î±\n"),
                await File.ReadAllBytesAsync(Path.Combine(appDirectory, "utf8.txt")));
            Assert.Equal(
                System.Text.Encoding.Unicode.GetBytes("A" + gothic + "Î²\n"),
                await File.ReadAllBytesAsync(Path.Combine(appDirectory, "utf16.txt")));
            Assert.Equal(
                System.Text.Encoding.UTF32.GetBytes("Z" + gothic + "Î³\n"),
                await File.ReadAllBytesAsync(Path.Combine(appDirectory, "utf32.txt")));
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
    public async Task PackagedStdLibFileMoveDeleteAndExistsRoundTrip()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-move-delete-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }
                export fn i32[min max] main()
                {
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("before.txt", System.IO.File.FileMode.Write));
                    if (!file.IsOpen())
                    {
                        return 1;
                    }

                    if (!StatusOk(file.WriteLine("Move me")))
                    {
                        return 2;
                    }

                    if (!StatusOk(file.Close()))
                    {
                        return 2;
                    }

                    if (!BoolOrFalse(System.IO.File.Exists("before.txt")))
                    {
                        return 3;
                    }

                    if (!StatusOk(System.IO.File.Move("before.txt", "after.txt")))
                    {
                        return 4;
                    }

                    if (BoolOrFalse(System.IO.File.Exists("before.txt")))
                    {
                        return 5;
                    }

                    if (!BoolOrFalse(System.IO.File.Exists("after.txt")))
                    {
                        return 6;
                    }

                    if (!StatusOk(System.IO.File.Delete("after.txt")))
                    {
                        return 7;
                    }

                    if (BoolOrFalse(System.IO.File.Exists("after.txt")))
                    {
                        return 8;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
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
                WorkingDirectory = appDirectory,
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
            Assert.False(File.Exists(Path.Combine(appDirectory, "before.txt")));
            Assert.False(File.Exists(Path.Combine(appDirectory, "after.txt")));
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
    public async Task PackagedStdLibUnicodeConsoleAndRawFileWritesWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-unicode-io-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                import System.Text
                module App
                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }
                export fn i32[min max] main()
                {
                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open("unicode.txt", System.IO.File.FileMode.Write, System.Text.Encoding.UTF8));
                    if (!file.IsOpen())
                    {
                        return 1;
                    }

                    if (!StatusOk(file.WriteLine((unicode)"File \u03B1")))
                    {
                        return 2;
                    }

                    if (!StatusOk(file.Close()))
                    {
                        return 2;
                    }

                    switch (System.Console.WriteLine((unicode)"Console \u03B1"))
                    {
                        case System.IO.IOStatus.Ok:
                            return 0;
                        case System.IO.IOStatus.Err(var error):
                            return 3;
                    }
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
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
                WorkingDirectory = appDirectory,
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
            Assert.Equal("Console α\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.Equal("File α\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "unicode.txt")));
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
