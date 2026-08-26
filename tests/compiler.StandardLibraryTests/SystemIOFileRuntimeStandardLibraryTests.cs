using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOFileRuntimeStandardLibraryTests : StandardLibraryTestSuite
{
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
        AssemblyLoweringAssertions.ContainsDirectLinuxX64Syscall(llvm, 8);
        AssemblyLoweringAssertions.DoesNotContainLinuxSyscallBridgeCalls(llvm);
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
        AssemblyLoweringAssertions.ContainsDirectLinuxX64Syscall(llvm, 74);
        AssemblyLoweringAssertions.DoesNotContainLinuxSyscallBridgeCalls(llvm);
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
                                case System.IO.IOError.NotADirectory:
                                    return false;
                                case System.IO.IOError.IsADirectory:
                                    return false;
                                case System.IO.IOError.DirectoryNotEmpty:
                                    return false;
                                case System.IO.IOError.TooManyLinks:
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
}
