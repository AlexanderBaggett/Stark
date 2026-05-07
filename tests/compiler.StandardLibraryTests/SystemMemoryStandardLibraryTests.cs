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

                fn bool MemoryOk(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UsePromotedDynamicMemory() {
                    stack mut dynamic i8[min max] bytes = new();
                    stack mut i8[min max][4] source = { 1, 2, 3, 4 };
                    stack u64[0 2 ** 63 - 1] four = 4;
                    if (!MemoryOk(System.Memory.ReserveBytes(bytes, four))) {
                        return false;
                    }

                    if (!MemoryOk(System.Memory.AppendBytes(bytes, source, four))) {
                        return false;
                    }

                    if (!MemoryOk(System.Memory.AppendFillBytes(bytes, 9, four))) {
                        return false;
                    }

                    if (!MemoryOk(System.Memory.MoveBytes(bytes[2, four], bytes[0, four], four))) {
                        return false;
                    }

                    return bytes.Length == 8;
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
        Assert.Contains("declare noalias noundef ptr @HeapAlloc(ptr, i32, i64 noundef) allocsize(2) allockind(\"alloc,uninitialized\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("declare noundef ptr @HeapReAlloc(ptr, i32, ptr, i64 noundef) allocsize(3) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @HeapFree(ptr, i32, ptr) allockind(\"free\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local noalias noundef ptr @__stark_os_allocate(i64 noundef %size) unnamed_addr allocsize(0) allockind(\"alloc,uninitialized\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local noundef ptr @__stark_os_reallocate(ptr %ptr, i64 noundef %size) unnamed_addr allocsize(1) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
        Assert.Contains("call noalias noundef ptr @HeapAlloc(ptr %heap, i32 0, i64 noundef %size)", llvm, StringComparison.Ordinal);
        Assert.Contains("call noundef ptr @HeapReAlloc(ptr %heap, i32 0, ptr %ptr, i64 noundef %size)", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @HeapFree(ptr %heap, i32 0, ptr %ptr)", llvm, StringComparison.Ordinal);
        Assert.Contains("os_realloc_check:", llvm, StringComparison.Ordinal);
        Assert.Contains("br i1 %realloc_can_os_realloc, label %try_os_reallocate, label %fallback", llvm, StringComparison.Ordinal);
        Assert.Contains("br i1 %os_realloc_failed, label %fallback, label %os_reallocated", llvm, StringComparison.Ordinal);
        Assert.Contains("@__stark_alloc_bucket_16 = linkonce_odr hidden thread_local global ptr null, comdat, align 8", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_local(localexec)", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("asm sideeffect \"syscall\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceWindowsMemoryReallocateExecutablePreservesContentsAcrossHeapAndFallbackPaths()
    {
        if (!OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-memory-windows-realloc-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "App.exe");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                module System.Memory

                public struct Allocator {
                    u8[0 2 ** 7 - 1] Kind;

                    static finite law Allocator Default() {
                        return new Allocator() {
                            Kind = 0
                        };
                    }
                }

                internal struct Allocation {
                    rawmutptr<i8[min max]> Pointer;
                    u64[0 2 ** 63 - 1] ByteLength;
                    u64[1 2 ** 63 - 1] Alignment;
                    Allocator Allocator;
                }

                internal fn Allocation Allocate(Allocator allocator, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
                internal fn Allocation Reallocate(Allocation allocation, u64[0 2 ** 63 - 1] byteLength, u64[1 2 ** 63 - 1] alignment);
                internal fn void Free(Allocation allocation);

                unsafe fn void Fill(borrow Allocation allocation, u64[0 2 ** 63 - 1] count, i8[min max] value) {
                    stack rawmutptr<i8[min max]> data = allocation.Pointer;
                    for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                        *(&data[index]) = value;
                    }
                }

                unsafe fn bool AllEqual(borrow Allocation allocation, u64[0 2 ** 63 - 1] count, i8[min max] value) {
                    stack rawmutptr<i8[min max]> data = allocation.Pointer;
                    for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                        if (*(&data[index]) != value) {
                            return false;
                        }
                    }

                    return true;
                }

                export unsafe ffi fn i32[min max] main() {
                    stack mut Allocation large = Allocate(Allocator.Default(), 5000, 8);
                    if (large.Pointer == null) {
                        return 1;
                    }

                    Fill(large, 5000, 3);
                    large = Reallocate(large, 9000, 8);
                    if (large.Pointer == null || !AllEqual(large, 5000, 3)) {
                        return 2;
                    }

                    large = Reallocate(large, 4097, 8);
                    if (large.Pointer == null || !AllEqual(large, 4097, 3)) {
                        return 3;
                    }

                    large = Reallocate(large, 4097, 8);
                    if (large.Pointer == null || !AllEqual(large, 4097, 3)) {
                        return 4;
                    }

                    Free(large);

                    stack mut Allocation bucket = Allocate(Allocator.Default(), 16, 8);
                    if (bucket.Pointer == null) {
                        return 5;
                    }

                    Fill(bucket, 16, 11);
                    bucket = Reallocate(bucket, 12, 8);
                    if (bucket.Pointer == null || !AllEqual(bucket, 12, 11)) {
                        return 6;
                    }

                    bucket = Reallocate(bucket, 8, 8);
                    if (bucket.Pointer == null || !AllEqual(bucket, 8, 11)) {
                        return 7;
                    }

                    Free(bucket);

                    stack mut Allocation bucketFallback = Allocate(Allocator.Default(), 16, 8);
                    if (bucketFallback.Pointer == null) {
                        return 8;
                    }

                    Fill(bucketFallback, 16, 21);
                    bucketFallback = Reallocate(bucketFallback, 5000, 8);
                    if (bucketFallback.Pointer == null || !AllEqual(bucketFallback, 16, 21)) {
                        return 9;
                    }

                    Free(bucketFallback);

                    stack mut Allocation overAligned = Allocate(Allocator.Default(), 512, 64);
                    if (overAligned.Pointer == null) {
                        return 10;
                    }

                    Fill(overAligned, 512, 31);
                    overAligned = Reallocate(overAligned, 1024, 64);
                    if (overAligned.Pointer == null || !AllEqual(overAligned, 512, 31)) {
                        return 11;
                    }

                    Free(overAligned);
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-o", outputPath, "--target", targetInfo.Triple],
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

            export unsafe ffi fn i32[min max] main() {
                System.Console.WriteLine("allocator stays unused");
                return 0;
            }
            """);
    }
}

