using Stark.Compiler;
using System.Text.RegularExpressions;

namespace compiler.Tests;

public sealed class BenchmarkSourceTests
{
    [Fact]
    public void BenchmarkSourcesCompile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks");
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var benchmarkSources = Directory.GetFiles(benchmarkRoot, "*.stark", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(benchmarkSources);

        foreach (var benchmarkSource in benchmarkSources)
        {
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(benchmarkSource), benchmarkSource),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: targetInfo,
                    ModuleResolver: new TargetAwareStdLibModuleResolver(
                        new FileSystemModuleResolver(sourceRoot),
                        [sourceRoot],
                        targetInfo)));

            Assert.True(
                result.Succeeded,
                $"{benchmarkSource}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");
            FallbackLogAssertions.AssertNoFallbackLogs(result, "Normal benchmark builds", benchmarkSource);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.DoesNotContain("@malloc(", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@realloc(", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@free(", llvmModule.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MemoryCopyFillHotLoopUsesInfallibleHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkSource = Path.Combine(repositoryRoot, "benchmarks", "allocator", "MemoryCopyFill.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkSource), benchmarkSource),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(
            result.Succeeded,
            $"{benchmarkSource}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var mainBody = ExtractDefinedFunctionText(
            llvm,
            "define i32 @main(",
            "Expected benchmark main definition to be emitted.");

        Assert.Contains("@__stark_inline_clone_System_Memory_CopyBytesDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_FillInitializedBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_CopyCodePointsDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_FillInitializedCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_MoveBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Memory_MoveCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_CopyBytesDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_FillInitializedBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_CopyCodePointsDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_FillInitializedCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_ReserveBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_ReserveCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_MoveBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_MoveCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_CopyBytesDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_FillInitializedBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_CopyCodePointsDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_FillInitializedCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_MoveBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Memory_MoveCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.Contains("; closed-world imported inline body: System.Memory.CopyBytesDisjointInfallible", llvm, StringComparison.Ordinal);
        Assert.Contains("; closed-world imported inline body: System.Memory.MoveBytesInfallible", llvm, StringComparison.Ordinal);

        var copyCodePointsClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Memory_CopyCodePointsDisjointInfallible(",
            "Expected code-point copy inline clone to be emitted.");
        var moveBytesClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Memory_MoveBytesInfallible(",
            "Expected byte move inline clone to be emitted.");
        var moveCodePointsClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Memory_MoveCodePointsInfallible(",
            "Expected code-point move inline clone to be emitted.");

        Assert.Contains("@llvm.memcpy.p0.p0.i64", copyCodePointsClone, StringComparison.Ordinal);
        Assert.Contains("mul i64 %arg_count, 4", copyCodePointsClone, StringComparison.Ordinal);
        Assert.Contains("@llvm.memmove.p0.p0.i64", moveBytesClone, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ult ptr", moveBytesClone, StringComparison.Ordinal);
        Assert.Contains("@llvm.memmove.p0.p0.i64", moveCodePointsClone, StringComparison.Ordinal);
        Assert.Contains("mul i64 %arg_count, 4", moveCodePointsClone, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ult ptr", moveCodePointsClone, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkTcpBenchmarksUseImportedInlineSocketClonesThroughLocalHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var loopback = CompileBenchmarkLlvm(repositoryRoot, "network/TcpLoopbackThroughput.stark", targetInfo);
        var scatterGather = CompileBenchmarkLlvm(repositoryRoot, "network/TcpScatterGatherLoopback.stark", targetInfo);

        var loopbackMain = ExtractDefinedFunctionText(
            loopback,
            "define i32 @main(",
            "Expected TcpLoopbackThroughput main definition to be emitted.");
        Assert.Contains(
            "@__stark_inline_clone_System_Net_Tcp_TcpClient_Write__mutborrowTcpClient_borrowSystem_Runtime_Buffer_FixedByteBuffer4096_(",
            loopbackMain,
            StringComparison.Ordinal);
        Assert.Contains(
            "@__stark_inline_clone_System_Net_Tcp_TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_FixedByteBuffer4096_(",
            loopbackMain,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call fastcc void @System_Net_Tcp_TcpClient_Write__mutborrowTcpClient_borrowSystem_Runtime_Buffer_FixedByteBuffer4096_(",
            loopbackMain,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call fastcc void @System_Net_Tcp_TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_FixedByteBuffer4096_(",
            loopbackMain,
            StringComparison.Ordinal);

        var loopbackSliceWriteClone = ExtractDefinedFunctionText(
            loopback,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Net_Tcp_TcpClient_Write__mutborrowTcpClient_borrowi8_minmax____(",
            "Expected TcpClient slice write inline clone to be emitted.");
        AssertDirectLinuxX64WriteSyscall(loopbackSliceWriteClone);
        Assert.DoesNotContain("call i64 @LinuxSyscall", loopbackSliceWriteClone, StringComparison.Ordinal);
        Assert.DoesNotContain("call i64 @System_Runtime_Platform_Linux_LinuxSyscall", loopbackSliceWriteClone, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_WriteSocket(", loopbackSliceWriteClone, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_Linux_WriteSocket(", loopbackSliceWriteClone, StringComparison.Ordinal);

        Assert.Contains(
            "call fastcc void @__stark_inline_clone_System_Net_Tcp_TcpClient_WriteVectored(",
            scatterGather,
            StringComparison.Ordinal);
        Assert.Contains(
            "call fastcc void @__stark_inline_clone_System_Net_Tcp_TcpClient_ReadVectored(",
            scatterGather,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call fastcc void @System_Net_Tcp_TcpClient_WriteVectored(",
            scatterGather,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call fastcc void @System_Net_Tcp_TcpClient_ReadVectored(",
            scatterGather,
            StringComparison.Ordinal);

        var scatterReadVectorClone = ExtractDefinedFunctionText(
            scatterGather,
            "define internal dso_local fastcc noundef i64 @__stark_inline_clone_System_Runtime_Platform_Linux_ReadSocketVector2(",
            "Expected Linux read-vector inline clone to be emitted.");
        var scatterWriteVectorClone = ExtractDefinedFunctionText(
            scatterGather,
            "define internal dso_local fastcc noundef i64 @__stark_inline_clone_System_Runtime_Platform_Linux_WriteSocketVector2(",
            "Expected Linux write-vector inline clone to be emitted.");
        Assert.Contains("%System_Runtime_Platform_Linux_LinuxIovec = type { ptr, i64 }", scatterGather, StringComparison.Ordinal);
        // LinuxReadvSyscallNumber / LinuxWritevSyscallNumber are comptime `const`s in
        // System.Runtime.Platform.Linux. Now that imported source-module bodies are
        // type-checked in the consuming build (cross-module monomorphization fix), the
        // consts fold to their literal syscall numbers (readv = 19, writev = 20) here
        // instead of an external global load. Preserve both the folded values and their
        // direct inline-assembly call sites, including the ABI clobber set.
        AssertDirectLinuxX64Syscall(scatterReadVectorClone, 19);
        Assert.Contains("[2 x %System_Runtime_Platform_Linux_LinuxIovec]", scatterReadVectorClone, StringComparison.Ordinal);
        Assert.Contains("i64 2", scatterReadVectorClone, StringComparison.Ordinal);
        AssertDirectLinuxX64Syscall(scatterWriteVectorClone, 20);
        Assert.Contains("[2 x %System_Runtime_Platform_Linux_LinuxIovec]", scatterWriteVectorClone, StringComparison.Ordinal);
        Assert.Contains("i64 2", scatterWriteVectorClone, StringComparison.Ordinal);
        Assert.DoesNotContain("call i64 @LinuxSyscall", scatterGather, StringComparison.Ordinal);
        Assert.DoesNotContain("call i64 @System_Runtime_Platform_Linux_LinuxSyscall", scatterGather, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsAllocatorBenchmarksUseHeapReAllocFastPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = new LlvmTargetInfo("x86_64-pc-windows-msvc", null);
        var relativePaths = new[]
        {
            "allocator/MemoryDynamicReserveGrowth.stark",
            "allocator/SystemMemoryFallbackReallocate.stark",
            "allocator/SystemMemoryBucketReallocate.stark"
        };

        foreach (var relativePath in relativePaths)
        {
            var llvm = CompileBenchmarkLlvm(repositoryRoot, relativePath, targetInfo);

            Assert.Contains("declare noundef ptr @HeapReAlloc(ptr, i32, ptr, i64 noundef) allocsize(3) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
            Assert.Contains("define internal dso_local noundef ptr @__stark_os_reallocate(ptr %ptr, i64 noundef %size) unnamed_addr allocsize(1) allockind(\"realloc\") \"alloc-family\"=\"__stark_os_allocate\" nounwind", llvm, StringComparison.Ordinal);
            Assert.Contains("call noundef ptr @HeapReAlloc(ptr %heap, i32 0, ptr %ptr, i64 noundef %size)", llvm, StringComparison.Ordinal);
            Assert.Contains("br i1 %realloc_old_is_bucket, label %try_bucket_reuse, label %os_realloc_check", llvm, StringComparison.Ordinal);
            Assert.Contains("br i1 %realloc_bucket_can_reuse, label %reuse_old, label %fallback", llvm, StringComparison.Ordinal);
            Assert.Contains("os_realloc_check:", llvm, StringComparison.Ordinal);
            Assert.Contains("%realloc_header_is_base = icmp eq ptr %realloc_base, %realloc_header", llvm, StringComparison.Ordinal);
            Assert.Contains("%realloc_os_alignment_ok = icmp ule i64 %realloc_effective_alignment, 8", llvm, StringComparison.Ordinal);
            Assert.Contains("br i1 %realloc_can_os_realloc, label %try_os_reallocate, label %fallback", llvm, StringComparison.Ordinal);
            Assert.Contains("br i1 %os_realloc_failed, label %fallback, label %os_reallocated", llvm, StringComparison.Ordinal);
            Assert.Contains("call void @llvm.memcpy.p0.p0.i64(ptr align 8 %new_ptr, ptr align 8 %old_ptr, i64 %copy_length, i1 false)", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("@realloc(", llvm, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowsDirectoryEnumerationUsesAllocationFreeInfoPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = new LlvmTargetInfo("x86_64-pc-windows-msvc", null);
        var llvm = CompileBenchmarkLlvm(repositoryRoot, "io/DirectoryEnumeration.stark", targetInfo);
        var enumerateOnceBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i64 @EnumerateOnce()",
            "Expected DirectoryEnumeration EnumerateOnce definition to be emitted.");

        Assert.Contains("ReadRemainingNameLengthChecksumRaw", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_FileSystem_Directory_ReadNextInfoRaw(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_FileSystem_Directory_ReadNextInfo(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_FileSystem_Directory_ReadNext(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Buffer_FixedByteBuffer8192_WritePointer(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Buffer_FixedByteBuffer8192_Capacity(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("System_FileSystem_DirectoryReadInfoResult", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_alloc", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_free", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("i64 8192, i1 false", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("i64 8240, i1 false", enumerateOnceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryEnumerationDoesNotExposeLargeDirectoryPayloadAsSsaValue()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = NativeToolchain.TryDetectDefaultTargetInfo(out var detected)
            ? detected
            : new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var llvm = CompileBenchmarkLlvm(repositoryRoot, "io/DirectoryEnumeration.stark", targetInfo);
        var enumerateOnceBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i64 @EnumerateOnce()",
            "Expected DirectoryEnumeration EnumerateOnce definition to be emitted.");

        Assert.DoesNotMatch(
            @"load %System_FileSystem_Directory, ptr %abi_extract_field_load_",
            enumerateOnceBody);
        Assert.DoesNotMatch(
            @"store %System_FileSystem_Directory %v\d+, ptr %slot_directory",
            enumerateOnceBody);
        Assert.DoesNotContain("%slot_directory = alloca", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"llvm\.memcpy[^\n]+%slot_directory",
            enumerateOnceBody);
        Assert.DoesNotContain("load %System_FileSystem_Directory", enumerateOnceBody, StringComparison.Ordinal);
        Assert.Contains("ReadRemainingNameLengthChecksumRaw", enumerateOnceBody, StringComparison.Ordinal);
        Assert.Contains("ReadDirectoryNameLengthChecksum", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_FileSystem_Directory_ReadNextInfoRaw(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_FileSystem_Directory_ReadNextInfo(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@EntryLength(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@IsEnd(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Buffer_FixedByteBuffer8192_WritePointer(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Buffer_FixedByteBuffer8192_Capacity(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectoryEntryKindFromLinuxType", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadDirectoryEntryInfo", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("System_FileSystem_DirectoryReadInfoResult", enumerateOnceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@IOOk(", enumerateOnceBody, StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(enumerateOnceBody, @"\bfca(?:\.[A-Za-z0-9_]+)+", RegexOptions.CultureInvariant).Count < 16,
            "Expected DirectoryEnumeration EnumerateOnce to avoid per-byte fca field extraction for the inline directory buffer.");
        Assert.DoesNotMatch(
            @"llvm\.memcpy[^\n]+ptr [^,]+%slot_[^,]*drop[^,]*, ptr [^,]+%slot_directory, i64 8248",
            enumerateOnceBody);
    }

    [Fact]
    public void FileOpenDoesNotExposeLargeFilePayloadAsSsaValue()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = NativeToolchain.TryDetectDefaultTargetInfo(out var detected)
            ? detected
            : new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var llvm = CompileSourceLlvm(
            repositoryRoot,
            """
            import System.IO
            import System.IO.File
            module Demo

            unsafe fn i32[min max] OpenAndClose()
            {
                stack System.IO.IOResult<System.IO.File.File> opened =
                    System.IO.File.Open("large-file-payload-test.tmp", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.None);
                switch (opened)
                {
                    case System.IO.IOResult<System.IO.File.File>.Err(var openError):
                        return 1;
                    case System.IO.IOResult<System.IO.File.File>.Ok(var fileValue):
                        stack mut System.IO.File.File file = fileValue;
                        switch (file.Close())
                        {
                            case System.IO.IOStatus.Ok:
                                return 0;
                            case System.IO.IOStatus.Err(var closeError):
                                return 2;
                        }
                }
            }
            """,
            "/virtual/FileOpenPayloadRegression.stark",
            targetInfo);
        var openAndCloseBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i32 @OpenAndClose()",
            "Expected OpenAndClose definition to be emitted.");

        Assert.DoesNotMatch(
            @"load %System_IO_File_File, ptr %abi_extract_field_load_",
            openAndCloseBody);
        Assert.DoesNotMatch(
            @"store %System_IO_File_File %v\d+, ptr %slot_file",
            openAndCloseBody);
        Assert.DoesNotContain("%slot_file = alloca", openAndCloseBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"llvm\.memcpy[^\n]+%slot_file",
            openAndCloseBody);
        Assert.DoesNotContain("load %System_IO_File_File", openAndCloseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@llvm.memcpy.inline.p0.p0.i64", openAndCloseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DictionaryInsertDropFieldUpdatesDoNotStoreDeferredAggregateValues()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hasNativeToolchain = NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo);
        var targetInfo = hasNativeToolchain
            ? detectedTargetInfo
            : new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var llvm = CompileBenchmarkLlvm(repositoryRoot, "collections/DictionaryInsert.stark", targetInfo);
        var mainBody = ExtractDefinedFunctionText(
            llvm,
            "define i32 @main(",
            "Expected DictionaryInsert benchmark main definition to be emitted.");

        Assert.DoesNotMatch(
            @"store %System_Collections_Dictionary[^,\n]* %v\d+, ptr %slot_values",
            mainBody);
        Assert.DoesNotContain("insertvalue %System_Collections_Dictionary", mainBody, StringComparison.Ordinal);
        Assert.Contains("store ptr null, ptr %abi_insert_field_store_", mainBody, StringComparison.Ordinal);
        Assert.Matches(@"store i64 0, ptr %(abi_insert_field_store_|v)\d+", mainBody);

        if (!hasNativeToolchain)
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-dictionary-llvm-");
        try
        {
            var objectPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "DictionaryInsert.obj" : "DictionaryInsert.o");
            var objectResult = NativeToolchain.EmitObject(llvm, objectPath, targetInfo: targetInfo);

            Assert.True(
                objectResult.Succeeded,
                objectResult.StandardOutput + Environment.NewLine + objectResult.StandardError);
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
    public void TextFormattingBenchmarksSpecializeConstantIntegerFormatting()
    {
        var repositoryRoot = FindRepositoryRoot();
        var integerMain = CompileBenchmarkMainLlvm(repositoryRoot, "text/ConstantIntegerFormatting.stark");
        var unicodeMain = CompileBenchmarkMainLlvm(repositoryRoot, "text/UnicodeFormatting.stark");

        Assert.DoesNotContain("TryFormatI64Ascii", integerMain, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFormatU64Ascii", integerMain, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFormatI1024Ascii", integerMain, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFormatU1024Ascii", integerMain, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFormatI1024Unicode", unicodeMain, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFormatU1024Unicode", unicodeMain, StringComparison.Ordinal);
        Assert.Contains("llvm.memcpy", integerMain, StringComparison.Ordinal);
        Assert.Contains("llvm.memcpy", unicodeMain, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardLibraryOptimizationBenchmarkGatesHaveExpectedSourceMatrix()
    {
        var repositoryRoot = FindRepositoryRoot();
        var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks");
        var gates = new[]
        {
            new BenchmarkGate("allocator", "MemoryDynamicReserveGrowth"),
            new BenchmarkGate("allocator", "SystemMemoryFallbackReallocate"),
            new BenchmarkGate("allocator", "SystemMemoryBucketReallocate"),
            new BenchmarkGate("runtime", "RuntimeBufferFixed"),
            new BenchmarkGate("runtime", "RuntimeBufferDynamic"),
            new BenchmarkGate("network", "TcpLoopbackThroughput"),
            new BenchmarkGate("network", "TcpScatterGatherLoopback"),
            new BenchmarkGate("console", "ConsoleWrites"),
            new BenchmarkGate("collections", "LinkedListPush"),
            new BenchmarkGate("collections", "LinkedListChurn"),
            new BenchmarkGate("collections", "QueueDequeue"),
            new BenchmarkGate("collections", "QueueChurn"),
            new BenchmarkGate("collections", "DictionaryMixed"),
            new BenchmarkGate("text", "TextConcatCopy"),
            new BenchmarkGate("text", "PathJoin"),
            new BenchmarkGate("text", "PathNormalize"),
            new BenchmarkGate("text", "PathQueries"),
            new BenchmarkGate("text", "UnicodeFormatting"),
            new BenchmarkGate("io", "DirectoryEnumeration")
        };

        Assert.Empty(Directory.GetFiles(benchmarkRoot, "Experimental*.stark", SearchOption.AllDirectories));

        foreach (var gate in gates)
        {
            var directory = Path.Combine(benchmarkRoot, gate.Directory);
            Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.c")), $"Missing C benchmark for {gate.Directory}/{gate.Name}.");
            Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.rs")), $"Missing Rust benchmark for {gate.Directory}/{gate.Name}.");
            Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.stark")), $"Missing Stark benchmark for {gate.Directory}/{gate.Name}.");
        }
    }

    [Fact]
    public void BenchmarkHarnessReportsCanonicalStarkOnly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-benchmarks.ps1"));
        var bashScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-benchmarks.sh"));
        var codegenScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "analyze-benchmark-codegen.ps1"));

        Assert.Contains("Emit-Row \"benchmark,language", script, StringComparison.Ordinal);
        Assert.Contains("llvm_object_us,link_us,toolchain_us,binary_bytes", script, StringComparison.Ordinal);
        Assert.Contains("median_us,avg_us", script, StringComparison.Ordinal);
        Assert.Contains("c_median_ratio", script, StringComparison.Ordinal);
        Assert.Contains("STARK_BENCH_SUBSET", script, StringComparison.Ordinal);
        Assert.Contains("STARK_BENCH_RUNTIME_ONLY", script, StringComparison.Ordinal);
        Assert.Contains("benchmark-stderr", bashScript, StringComparison.Ordinal);
        Assert.Contains("Captured benchmark stderr:", bashScript, StringComparison.Ordinal);
        Assert.DoesNotContain("benchmark,language,collection", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stark-experimental", script, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWith(\"Experimental\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stable-stark", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic-stark", script, StringComparison.Ordinal);
        Assert.DoesNotContain("experimental-ring-stark", script, StringComparison.Ordinal);

        foreach (var harness in new[] { bashScript, codegenScript })
        {
            Assert.DoesNotContain("stark-experimental", harness, StringComparison.Ordinal);
            Assert.DoesNotContain("StartsWith(\"Experimental\"", harness, StringComparison.Ordinal);
            Assert.DoesNotContain("Get-ExperimentalBenchmarkId", harness, StringComparison.Ordinal);
        }
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for benchmark source tests.");
    }

    private static void AssertDirectLinuxX64Syscall(string text, long syscallNumber)
    {
        var firstArgument = $"(i64 {syscallNumber}";
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
            {
                if (!line.Contains("call i64 asm sideeffect \"syscall\"", StringComparison.Ordinal))
                {
                    return false;
                }

                var argumentIndex = line.IndexOf(firstArgument, StringComparison.Ordinal);
                if (argumentIndex < 0)
                {
                    return false;
                }

                var delimiterIndex = argumentIndex + firstArgument.Length;
                return delimiterIndex < line.Length
                    && line[delimiterIndex] is ',' or ')';
            });

        Assert.NotNull(callLine);
        Assert.Contains(
            "~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}",
            callLine,
            StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    private static void AssertDirectLinuxX64WriteSyscall(string text)
    {
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
                line.Contains(
                    "call i64 asm sideeffect \"movq $$1, %rax\\0Asyscall\"",
                    StringComparison.Ordinal));

        Assert.NotNull(callLine);
        Assert.Contains(
            "~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}",
            callLine,
            StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix, string missingMessage)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, missingMessage);

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }

    private static string CompileBenchmarkMainLlvm(string repositoryRoot, string relativePath)
    {
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var llvm = CompileBenchmarkLlvm(repositoryRoot, relativePath, targetInfo);
        return ExtractDefinedFunctionText(
            llvm,
            "define i32 @main(",
            $"Expected benchmark main definition for {relativePath} to be emitted.");
    }

    private static string CompileBenchmarkLlvm(
        string repositoryRoot,
        string relativePath,
        LlvmTargetInfo targetInfo)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkSource = Path.Combine(repositoryRoot, "benchmarks", relativePath);
        return CompileSourceLlvm(
            repositoryRoot,
            File.ReadAllText(benchmarkSource),
            benchmarkSource,
            targetInfo);
    }

    private static string CompileSourceLlvm(
        string repositoryRoot,
        string source,
        string sourcePath,
        LlvmTargetInfo targetInfo)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source, sourcePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(
            result.Succeeded,
            $"{sourcePath}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");

        return result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
    }

    private static string NormalizeLlvmForMaterialComparison(string llvm)
    {
        var normalized = llvm.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @", !dbg !\d+", string.Empty, RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"!dbg !\d+", string.Empty, RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant);
        return normalized.Trim();
    }

    private readonly record struct BenchmarkGate(string Directory, string Name);
}
