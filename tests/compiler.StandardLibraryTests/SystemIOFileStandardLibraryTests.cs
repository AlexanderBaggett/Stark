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

        Assert.Contains("const LinuxOpenExclusiveFlag = 128;", linuxSource, StringComparison.Ordinal);
        Assert.Contains("internal enum FileOpenResult", linuxSource, StringComparison.Ordinal);
        Assert.Contains("internal unsafe fn FileOpenResult OpenFileCreateNewResult(ascii path)", linuxSource, StringComparison.Ordinal);
        Assert.Contains("return FileOpenResult.Err((i32[min max])syscallResult);", linuxSource, StringComparison.Ordinal);
        Assert.Contains("LinuxOpenExclusiveFlag | LinuxOpenCloseOnExecFlag", linuxSource, StringComparison.Ordinal);

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
