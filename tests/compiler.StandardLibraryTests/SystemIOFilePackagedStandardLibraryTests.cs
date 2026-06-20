using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOFilePackagedStandardLibraryTests : StandardLibraryTestSuite
{
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
}
