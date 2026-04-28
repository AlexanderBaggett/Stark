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
        Assert.Contains("declare i32 @GetConsoleMode(", llvm, StringComparison.Ordinal);
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
        Assert.Contains("declare i32 @SetFilePointerEx(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileRead(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileWrite(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileAppend(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileReadWrite(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @FlushFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @ReadFile__", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @WriteFile__", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @SeekFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @CreateFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @CloseHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @FlushFileBuffers(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WriteFile(", llvm, StringComparison.Ordinal);
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
        Assert.Contains("declare i32 @CreateDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @RemoveDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @FindFirstFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @FindNextFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @FindClose(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @PathExists(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @FileExists(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CreateDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DeleteDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @ReadDirectoryEntry(", llvm, StringComparison.Ordinal);
        Assert.Contains("@DirectoryEntryKindFromWindowsAttributes(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetFileAttributesW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @CreateDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @RemoveDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @FindFirstFileW(", llvm, StringComparison.Ordinal);
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
        Assert.Contains("define fastcc noundef i1 @HasLongPathPrefix(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDriveAbsoluteWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsUncWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryDecodeUtf8PathToWide(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildAbsoluteWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildLongWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryBuildWindowsWidePath(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @DirectorySeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @AlternateDirectorySeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef %stark_ascii @PathSeparator()", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsDirectorySeparator(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @TryCurrentDirectory(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetCurrentDirectoryW(", llvm, StringComparison.Ordinal);
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

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
                    if (!allocator.IsDefault()) {
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
}
