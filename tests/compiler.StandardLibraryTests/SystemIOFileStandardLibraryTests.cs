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
    public async Task SourceStdLibFileSeekRoundTripsOnLinux()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
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

            Assert.True(exitCode == 0, stderr.ToString());
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
    public async Task PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-owned-file-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

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

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-buffering-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

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

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-file-encodings-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

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

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-move-delete-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

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

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-unicode-io-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

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
