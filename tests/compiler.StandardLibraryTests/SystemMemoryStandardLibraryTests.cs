using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemMemoryStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceMemoryModuleSupportsDefaultAllocatorSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibMemorySurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn bool UseDefaultAllocator() {
                    stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
                    return allocator.IsDefault();
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceMemoryModuleUsesWindowsHeapAllocatorForWindowsTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var memoryPath = Path.Combine(sourceRoot, "System", "Memory.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(memoryPath),
                memoryPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare ptr @GetProcessHeap() nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @HeapAlloc(ptr, i32, i64) nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @HeapFree(ptr, i32, ptr) nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @HeapAlloc(ptr %heap, i32 0, i64 %size)", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @HeapFree(ptr %heap, i32 0, ptr %ptr)", llvm, StringComparison.Ordinal);
        Assert.Contains("@__stark_alloc_bucket_16 = weak_odr hidden thread_local global ptr null, align 8", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_local(localexec)", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("asm sideeffect \"syscall\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceImportedStdLibAllocatorExecutableHasNoExplicitCAllocatorSymbolReferences()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var nmPath = FindFirstAvailableTool(OperatingSystem.IsWindows() ? "llvm-nm" : "nm", OperatingSystem.IsWindows() ? "nm" : "llvm-nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-source-alloc-symbols-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await WriteAllocatorAuditAppAsync(appPath);

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

            await AssertBinaryHasNoExplicitCAllocatorSymbolReferencesAsync(nmPath, outputPath);
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
    public async Task PackagedStdLibAllocatorExecutableHasNoExplicitCAllocatorSymbolReferences()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var nmPath = FindFirstAvailableTool(OperatingSystem.IsWindows() ? "llvm-nm" : "nm", OperatingSystem.IsWindows() ? "nm" : "llvm-nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-package-alloc-symbols-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await WriteAllocatorAuditAppAsync(appPath);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            await AssertBinaryHasNoExplicitCAllocatorSymbolReferencesAsync(nmPath, outputPath);
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
    public async Task PackagedImportSystemConsoleExecutableDoesNotPullUnusedAllocatorCSymbols()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var nmPath = FindFirstAvailableTool(OperatingSystem.IsWindows() ? "llvm-nm" : "nm", OperatingSystem.IsWindows() ? "nm" : "llvm-nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var systemPath = Path.Combine(sourceRoot, "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-console-no-alloc-symbols-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var packagedAppDirectory = Path.Combine(tempDirectory.FullName, "packaged-app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(packagedAppDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var packagedAppPath = Path.Combine(packagedAppDirectory, "App.stark");
        var packagedOutputPath = Path.Combine(packagedAppDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-O0", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await WriteImportSystemConsoleAppAsync(packagedAppPath);

            var packagedStdout = new StringWriter();
            var packagedStderr = new StringWriter();
            var packagedExitCode = await CompilerCli.RunAsync(
                [packagedAppPath, "--emit-exe", "-O0", "-I", packageDirectory, "-o", packagedOutputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                packagedStdout,
                packagedStderr);

            Assert.Equal(0, packagedExitCode);
            Assert.Contains("Emitted executable:", packagedStdout.ToString());
            AssertCompilerLogsEmitted(packagedStderr.ToString());
            Assert.True(File.Exists(packagedOutputPath));
            await AssertBinaryHasNoExplicitCAllocatorSymbolReferencesAsync(nmPath, packagedOutputPath);
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
    public async Task SourceImportedImportSystemConsoleExecutableDoesNotEmitUnusedMemoryObjects()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-source-console-no-memory-objects-");
        var tempsDirectory = Path.Combine(tempDirectory.FullName, "temps");
        Directory.CreateDirectory(tempsDirectory);
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
        var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";

        try
        {
            await WriteImportSystemConsoleAppAsync(appPath);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-O0", "-I", sourceRoot, "--save-temps", tempsDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var emittedFiles = Directory.EnumerateFiles(tempsDirectory)
                .Select(Path.GetFileName)
                .OrderBy(static fileName => fileName, StringComparer.Ordinal)
                .ToArray();
            var emittedFileList = string.Join(Environment.NewLine, emittedFiles);
            Assert.Contains($"System_Console{objectExtension}", emittedFiles);
            Assert.DoesNotContain($"System_Memory{objectExtension}", emittedFiles);
            Assert.DoesNotContain($"System_FileSystem{objectExtension}", emittedFiles);
            Assert.DoesNotContain($"System_Memory{objectExtension}", emittedFileList);
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

    private static Task WriteImportSystemConsoleAppAsync(string path)
    {
        return File.WriteAllTextAsync(
            path,
            """
            import System
            module App

            export ffi fn i32[-2147483648 2147483647] main() {
                System.Console.WriteLine("allocator stays unused");
                return 0;
            }
            """);
    }
}
