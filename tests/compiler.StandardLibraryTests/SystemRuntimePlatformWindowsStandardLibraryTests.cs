using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimePlatformWindowsStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis() => _suite.StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis();

    [Fact]
    public void StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper() => _suite.StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper();

    [Fact]
    public void StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule() => _suite.StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule();

    [Fact]
    public void RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath() => _suite.RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath();

    [Fact]
    public void WindowsDispatchTemplateMirrorsLinuxDispatchSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var templatesRoot = Path.Combine(repositoryRoot, "stdlib", "templates");
        var linuxPath = Path.Combine(templatesRoot, "System.Runtime.Platform.LinuxDispatch.stark");
        var windowsPath = Path.Combine(templatesRoot, "System.Runtime.Platform.WindowsDispatch.stark");

        var linuxFunctions = ExtractInternalFunctionNames(File.ReadAllText(linuxPath));
        var windowsFunctions = ExtractInternalFunctionNames(File.ReadAllText(windowsPath));

        Assert.Equal(linuxFunctions, windowsFunctions);
    }

    [Fact]
    public void StdLibSourceWindowsConsoleInputOutputUsesKernel32Apis()
    {
        var result = CompileWindowsPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare ptr @GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WriteConsoleW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetConsoleMode(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @CachedStdout(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @CachedStderr(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WriteStdoutAscii(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WriteStdoutUnicode(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WriteStderrAscii(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @WriteStderrUnicode(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenStdin(", llvm, StringComparison.Ordinal);
        Assert.Matches(@"define fastcc noundef(?: range\([^)]*\))? i64 @ReadStdin\(", llvm);
        Assert.Contains("define fastcc noundef i1 @IsTerminal(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WriteConsoleW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetConsoleMode(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fputs(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fputws(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsFilePrimitivesUseKernel32Apis()
    {
        var result = CompileWindowsPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare ptr @CreateFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @FlushFileBuffers(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @NtReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @NtWriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @SetFilePointerEx(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileRead(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileWrite(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileAppend(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileReadWrite(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FlushFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @ReadFile__", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @WriteFile__", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @ReadFileBytesFast(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @WriteFileBytesFast(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @SeekFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @CreateFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @FlushFileBuffers(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @NtReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @NtWriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @SetFilePointerEx(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fopen(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fclose(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fflush(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsDirectoryAndMetadataUseKernel32Apis()
    {
        var result = CompileWindowsPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare i32 @GetFileAttributesW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetFileAttributesExW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @CreateDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @RemoveDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @FindFirstFileExW(", llvm, StringComparison.Ordinal);
        Assert.Contains("@WinFindFirstExLargeFetch = local_unnamed_addr constant i8 2", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @FindNextFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @FindClose(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @PathExists(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @FileExists(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ReadPathMetadata(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CreateDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DeleteDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectoryWithFlags(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectoryWithFlagsInto(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectoryFast(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectoryFastInto(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ReadDirectoryEntry(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ReadDirectoryEntryInfo(", llvm, StringComparison.Ordinal);
        Assert.Contains("@DirectoryEntryKindFromWindowsAttributes(", llvm, StringComparison.Ordinal);
        Assert.Contains("@WindowsFileTimeToUnixSeconds(", llvm, StringComparison.Ordinal);
        Assert.Contains("@WindowsFileTimeToUnixNanoseconds(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetFileAttributesW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetFileAttributesExW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @CreateDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @RemoveDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @FindFirstFileExW(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("call ptr @FindFirstFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @FindNextFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @FindClose(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@opendir(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@readdir(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@closedir(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@stat(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsPathBehaviorUsesWideNormalizationRules()
    {
        var result = CompileWindowsPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("c\"\\5C\\00\"", llvm, StringComparison.Ordinal);
        Assert.Contains("c\"/\\00\"", llvm, StringComparison.Ordinal);
        Assert.Contains("c\";\\00\"", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetCurrentDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetTempPathW(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @HasLongPathPrefix(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDriveAbsoluteWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsUncWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryDecodeUtf8PathToWide(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @TryCopyWideAscii(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildAbsoluteWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildLongWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @DirectorySeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @AlternateDirectorySeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @PathSeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDirectorySeparator(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryCurrentDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryTempDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetCurrentDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetTempPathW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 @TryCopyWideAscii(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i1 @TryDecodeUtf8PathToWide(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i1 @TryBuildLongWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i1 @TryBuildWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@getcwd(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceWindowsProcessHelpersUseKernel32Apis()
    {
        var result = CompileWindowsPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare void @ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetCurrentProcessId()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ProcessId(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetCurrentProcessId()", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDispatchProcessExitCallsKernelImportWithoutSymbolCollision()
    {
        var result = CompileWindowsDispatchTemplate();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc void @System_Runtime_Platform_ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("define fastcc void @ExitProcess(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagedStdLibWindowsTargetCanBeConsumedWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-package-consume-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var manifestPath = Path.Combine(packageDirectory, "System.starkpkg.json");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var llvmPath = Path.Combine(appDirectory, "App.ll");

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-pkg", "--target", "x86_64-pc-windows-msvc", "--package-library-file", "System.lib", "-o", manifestPath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStdout + Environment.NewLine + emitStderr);
            Assert.Contains("Emitted package image:", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Package library file: System.lib", emitStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(manifestPath));

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                export fn i32[min max] main()
                {
                    stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
                    if (!allocator.IsDefault())
                    {
                        return 1;
                    }

                    System.Threading.Thread.Yield();
                    System.Threading.Thread.SleepMilliseconds(0);
                    System.Console.WriteLine("windows package");
                    return 0;
                }
                """);

            var appStdout = new StringWriter();
            var appStderr = new StringWriter();
            var appExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-llvm", "-I", packageDirectory, "--target", "x86_64-pc-windows-msvc", "-o", llvmPath],
                new StringReader(string.Empty),
                appStdout,
                appStderr);

            Assert.True(appExitCode == 0, appStdout + Environment.NewLine + appStderr);
            Assert.True(File.Exists(llvmPath));

            var llvm = await File.ReadAllTextAsync(llvmPath);
            Assert.Contains("call fastcc %System_Memory_Allocator @System_Memory_Allocator_Default()", llvm, StringComparison.Ordinal);
            Assert.Contains("call fastcc i1 @System_Memory_Allocator_IsDefault(", llvm, StringComparison.Ordinal);
            Assert.Contains("call fastcc void @System_Threading_Thread_Yield()", llvm, StringComparison.Ordinal);
            Assert.Contains("call fastcc void @System_Threading_Thread_SleepMilliseconds(", llvm, StringComparison.Ordinal);
            Assert.Contains("call fastcc %System_IO_IOStatus @System_Console_WriteLine__ascii_(", llvm, StringComparison.Ordinal);
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
    public async Task SourceWindowsConsoleExecutableWritesRedirectedOutputAndErrors()
    {
        if (!OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-windows-console-runtime-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "App.exe");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                import System.Console
                module App

                fn bool IsOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                export fn i32[min max] main()
                {
                    if (!IsOk(System.Console.Write((unicode)"Console")))
                    {
                        return 1;
                    }

                    if (!IsOk(System.Console.WriteLine((unicode)" Status")))
                    {
                        return 2;
                    }

                    if (!IsOk(System.Console.WriteErrorLine("stderr works")))
                    {
                        return 3;
                    }

                    if (!IsOk(System.Console.WriteLine((unicode)"Promoted")))
                    {
                        return 4;
                    }

                    if (!IsOk(System.Console.WriteErrorLine("promoted stderr")))
                    {
                        return 5;
                    }

                    return 0;
                }
                """);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStdout + Environment.NewLine + compileStderr);
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessAsync(outputPath, tempDirectory.FullName);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal("Console Status\nPromoted\n", NormalizeNewlines(execution.Stdout));
            Assert.Equal("stderr works\npromoted stderr\n", NormalizeNewlines(execution.Stderr));
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
    public async Task SourceWindowsDirectoryEnumerationProbeCompilesAsciiUnicodeAndLongNameChecks()
    {
        if (!OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-windows-dir-enum-");
        var appPath = Path.Combine(tempDirectory.FullName, "DirectoryDecodeProbe.stark");
        var llvmPath = Path.Combine(tempDirectory.FullName, "DirectoryDecodeProbe.ll");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.FileSystem
                import System.Text
                import System.IO
                module WindowsDirectoryDecodeProbe

                fn bool IOOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool IsWideName(mut borrow System.FileSystem.FileSystemEntry entry)
                {
                    if (entry.Name.Length() != 11)
                    {
                        return false;
                    }

                    stack i8[min max][] view = entry.Name.AsSlice();
                    return view[0] == 119
                        && view[1] == 105
                        && view[2] == 100
                        && view[3] == 101
                        && view[4] == 45
                        && view[5] == (i8[min max])-61
                        && view[6] == (i8[min max])-87
                        && view[7] == 46
                        && view[8] == 116
                        && view[9] == 120
                        && view[10] == 116;
                }

                fn bool IsLongName(mut borrow System.FileSystem.FileSystemEntry entry)
                {
                    if (entry.Name.Length() != 184)
                    {
                        return false;
                    }

                    stack i8[min max][] view = entry.Name.AsSlice();
                    return view[0] == 97
                        && view[179] == 97
                        && view[180] == 46
                        && view[181] == 116
                        && view[182] == 120
                        && view[183] == 116;
                }

                export unsafe fn i32[min max] main()
                {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened =
                        System.FileSystem.OpenDirectory("scan-root");
                    switch (opened)
                    {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                            return 1;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directoryValue):
                            stack mut System.FileSystem.Directory directory = directoryValue;
                            stack mut bool foundWide = false;
                            stack mut bool foundLong = false;

                            for willexit (stack mut u8[0 8] index = 0; index < 8; index += 1)
                            {
                                switch (directory.ReadNext())
                                {
                                    case System.FileSystem.DirectoryReadResult.Err(var readError):
                                        directory.Close();
                                        return 2;
                                    case System.FileSystem.DirectoryReadResult.End:
                                        if (!IOOk(directory.Close()))
                                        {
                                            return 3;
                                        }

                                        if (!foundWide)
                                        {
                                            return 4;
                                        }

                                        if (!foundLong)
                                        {
                                            return 5;
                                        }

                                        return 0;
                                    case System.FileSystem.DirectoryReadResult.Entry(var entry):
                                        stack mut System.FileSystem.FileSystemEntry mutableEntry = entry;
                                        if (IsWideName(mutableEntry))
                                        {
                                            foundWide = true;
                                        }

                                        if (IsLongName(mutableEntry))
                                        {
                                            foundLong = true;
                                        }
                                }
                            }

                            directory.Close();
                            return 6;
                    }
                }
                """);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-llvm", "-I", sourceRoot, "-o", llvmPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStdout + Environment.NewLine + compileStderr);
            Assert.True(File.Exists(llvmPath));

            var llvm = await File.ReadAllTextAsync(llvmPath);
            Assert.Contains("System_FileSystem_Directory_ReadNext", llvm, StringComparison.Ordinal);
            Assert.Contains("System_FileSystem_OpenDirectory", llvm, StringComparison.Ordinal);
            Assert.Contains("TryCopyWideAscii", llvm, StringComparison.Ordinal);
            Assert.Contains("TryCopyWideUtf8", llvm, StringComparison.Ordinal);
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
    public async Task SourceWindowsPromotedDirectoryEnumerationExecutablePreservesCachedFirstEntry()
    {
        if (!OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-windows-dir-enum-runtime-");
        var appPath = Path.Combine(tempDirectory.FullName, "DirectoryRuntimeProbe.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "DirectoryRuntimeProbe.exe");
        var scanDirectory = Path.Combine(tempDirectory.FullName, "scan-root");
        var emptyDirectory = Path.Combine(tempDirectory.FullName, "empty-root");

        try
        {
            Directory.CreateDirectory(scanDirectory);
            Directory.CreateDirectory(emptyDirectory);
            File.WriteAllText(Path.Combine(scanDirectory, "first-entry.tmp"), string.Empty);
            File.WriteAllText(Path.Combine(scanDirectory, "wide-\u00E9.txt"), string.Empty);
            File.WriteAllText(Path.Combine(scanDirectory, new string('a', 180) + ".txt"), string.Empty);

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.FileSystem
                import System.IO
                module WindowsDirectoryRuntimeProbe

                fn bool IOOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool IsEnd(System.FileSystem.DirectoryReadInfoResult result)
                {
                    switch (result)
                    {
                        case System.FileSystem.DirectoryReadInfoResult.Entry(var entry):
                            return false;
                        case System.FileSystem.DirectoryReadInfoResult.End:
                            return true;
                        case System.FileSystem.DirectoryReadInfoResult.Err(var error):
                            return false;
                    }
                }

                fn bool IsErr(System.FileSystem.DirectoryReadInfoResult result)
                {
                    switch (result)
                    {
                        case System.FileSystem.DirectoryReadInfoResult.Entry(var entry):
                            return false;
                        case System.FileSystem.DirectoryReadInfoResult.End:
                            return false;
                        case System.FileSystem.DirectoryReadInfoResult.Err(var error):
                            return true;
                    }
                }

                fn i32[min max] CheckEmptyDirectory()
                {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened =
                        System.FileSystem.OpenDirectory("empty-root");
                    switch (opened)
                    {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                            return 1;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directoryValue):
                            stack mut System.FileSystem.Directory directory = directoryValue;
                            if (!IsEnd(directory.ReadNextInfo()))
                            {
                                directory.Close();
                                return 2;
                            }

                            if (!IOOk(directory.Close()))
                            {
                                return 3;
                            }

                            return 0;
                    }
                }

                fn i32[min max] CheckEarlyClose()
                {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened =
                        System.FileSystem.OpenDirectory("scan-root");
                    switch (opened)
                    {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                            return 1;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directoryValue):
                            stack mut System.FileSystem.Directory directory = directoryValue;
                            if (!IOOk(directory.Close()))
                            {
                                return 2;
                            }

                            if (!IOOk(directory.Close()))
                            {
                                return 3;
                            }

                            if (!IsErr(directory.ReadNextInfo()))
                            {
                                return 4;
                            }

                            return 0;
                    }
                }

                fn i32[min max] ScanOnce()
                {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened =
                        System.FileSystem.OpenDirectory("scan-root");
                    switch (opened)
                    {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                            return 1;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directoryValue):
                            stack mut System.FileSystem.Directory directory = directoryValue;
                            stack mut bool foundFirst = false;
                            stack mut bool foundWide = false;
                            stack mut bool foundLong = false;
                            stack mut u8[0 3] count = 0;
                            stack mut u8[0 210] checksum = 0;

                            while willexit (true)
                            {
                                switch (directory.ReadNextInfo())
                                {
                                    case System.FileSystem.DirectoryReadInfoResult.Err(var readError):
                                        directory.Close();
                                        return 2;
                                    case System.FileSystem.DirectoryReadInfoResult.End:
                                        if (!IOOk(directory.Close()))
                                        {
                                            return 3;
                                        }

                                        if (count != 3 || checksum != 210)
                                        {
                                            return 4;
                                        }

                                        if (!foundFirst || !foundWide || !foundLong)
                                        {
                                            return 5;
                                        }

                                        return 0;
                                    case System.FileSystem.DirectoryReadInfoResult.Entry(var entry):
                                        stack u8[0 210] length = (u8[0 210])entry.NameLength;
                                        if (length == 15)
                                        {
                                            foundFirst = true;
                                        }

                                        if (length == 11)
                                        {
                                            foundWide = true;
                                        }

                                        if (length == 184)
                                        {
                                            foundLong = true;
                                        }

                                        count += 1;
                                        checksum += length;
                                }
                            }
                    }
                }

                export fn i32[min max] main()
                {
                    stack i32[min max] emptyStatus = CheckEmptyDirectory();
                    if (emptyStatus != 0)
                    {
                        return 10 + emptyStatus;
                    }

                    stack i32[min max] closeStatus = CheckEarlyClose();
                    if (closeStatus != 0)
                    {
                        return 20 + closeStatus;
                    }

                    stack i32[min max] firstScan = ScanOnce();
                    if (firstScan != 0)
                    {
                        return 30 + firstScan;
                    }

                    stack i32[min max] secondScan = ScanOnce();
                    if (secondScan != 0)
                    {
                        return 40 + secondScan;
                    }

                    return 0;
                }
                """);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStdout + Environment.NewLine + compileStderr);
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessAsync(outputPath, tempDirectory.FullName);
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

    private static CompilationResult CompileWindowsPlatformSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var windowsPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Windows.stark");
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(windowsPath),
                windowsPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));
    }

    private static CompilationResult CompileWindowsDispatchTemplate()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var templatePath = Path.Combine(repositoryRoot, "stdlib", "templates", "System.Runtime.Platform.WindowsDispatch.stark");
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(templatePath),
                templatePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));
    }

    private static string[] ExtractInternalFunctionNames(string source)
    {
        var names = new List<string>();
        using var reader = new StringReader(source);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("internal fn ", StringComparison.Ordinal))
            {
                continue;
            }

            var openParen = trimmed.IndexOf('(');
            if (openParen < 0)
            {
                continue;
            }

            var declarationPrefix = trimmed[..openParen];
            var nameStart = declarationPrefix.LastIndexOf(' ');
            if (nameStart < 0 || nameStart + 1 >= declarationPrefix.Length)
            {
                continue;
            }

            names.Add(declarationPrefix[(nameStart + 1)..]);
        }

        return names
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, string workingDirectory)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        var stdout = await process!.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
