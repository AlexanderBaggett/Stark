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
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.DoesNotContain("@malloc(", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@realloc(", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@free(", llvmModule.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExperimentalMemoryCopyFillHotLoopUsesInfallibleHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkSource = Path.Combine(repositoryRoot, "benchmarks", "allocator", "ExperimentalMemoryCopyFill.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkSource), benchmarkSource),
            new CompilerOptions(
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O3,
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

        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_CopyBytesDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_FillInitializedBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_CopyCodePointsDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_FillInitializedCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_ReserveBytes(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_ReserveCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_MoveBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@__stark_inline_clone_System_Experimental_Memory_MoveCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyBytesDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyCodePointsDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_ReserveBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_ReserveCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyBytesDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyCodePointsDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.Contains("; closed-world imported inline body: System.Experimental.Memory.ReserveBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("; closed-world imported inline body: System.Experimental.Memory.CopyBytesDisjointInfallible", llvm, StringComparison.Ordinal);
        Assert.Contains("; closed-world imported inline body: System.Experimental.Memory.MoveBytesInfallible", llvm, StringComparison.Ordinal);

        var copyCodePointsClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Experimental_Memory_CopyCodePointsDisjointInfallible(",
            "Expected code-point copy inline clone to be emitted.");
        var moveBytesClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Experimental_Memory_MoveBytesInfallible(",
            "Expected byte move inline clone to be emitted.");
        var moveCodePointsClone = ExtractDefinedFunctionText(
            llvm,
            "define internal dso_local fastcc void @__stark_inline_clone_System_Experimental_Memory_MoveCodePointsInfallible(",
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
    public void ImplementationIdenticalAllocatorExperimentalBenchmarksMatchStableLlvm()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pairs = new[]
        {
            ("allocator/HeapLocalBucketReuse.stark", "allocator/ExperimentalHeapLocalBucketReuse.stark"),
            ("allocator/SystemMemoryBucketReallocate.stark", "allocator/ExperimentalSystemMemoryBucketReallocate.stark"),
            ("allocator/SystemMemoryFallbackReallocate.stark", "allocator/ExperimentalSystemMemoryFallbackReallocate.stark")
        };

        foreach (var (stableRelativePath, experimentalRelativePath) in pairs)
        {
            var stableLlvm = CompileBenchmarkMainLlvm(repositoryRoot, stableRelativePath);
            var experimentalLlvm = CompileBenchmarkMainLlvm(repositoryRoot, experimentalRelativePath);

            Assert.Equal(
                NormalizeLlvmForMaterialComparison(stableLlvm),
                NormalizeLlvmForMaterialComparison(experimentalLlvm));
        }
    }

    [Fact]
    public void WindowsAllocatorBenchmarksUseHeapReAllocFastPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var targetInfo = new LlvmTargetInfo("x86_64-pc-windows-msvc", null);
        var relativePaths = new[]
        {
            "allocator/MemoryDynamicReserveGrowth.stark",
            "allocator/ExperimentalMemoryDynamicReserveGrowth.stark",
            "allocator/SystemMemoryFallbackReallocate.stark",
            "allocator/ExperimentalSystemMemoryFallbackReallocate.stark",
            "allocator/SystemMemoryBucketReallocate.stark",
            "allocator/ExperimentalSystemMemoryBucketReallocate.stark"
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
    public void ExperimentalStandardLibraryOptimizationBenchmarkGatesHaveExpectedSourceMatrix()
    {
        var repositoryRoot = FindRepositoryRoot();
        var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks");
        var gates = new[]
        {
            new BenchmarkGate("allocator", "MemoryDynamicReserveGrowth"),
            new BenchmarkGate("allocator", "SystemMemoryFallbackReallocate"),
            new BenchmarkGate("allocator", "SystemMemoryBucketReallocate"),
            new BenchmarkGate("runtime", "RuntimeBufferFixed", HasStableStark: false),
            new BenchmarkGate("runtime", "RuntimeBufferDynamic", HasStableStark: false),
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
            new BenchmarkGate("text", "PathNormalize", HasStableStark: false),
            new BenchmarkGate("text", "PathQueries"),
            new BenchmarkGate("text", "UnicodeFormatting"),
            new BenchmarkGate("io", "DirectoryEnumeration")
        };

        foreach (var gate in gates)
        {
            var directory = Path.Combine(benchmarkRoot, gate.Directory);
            Assert.True(File.Exists(Path.Combine(directory, $"Experimental{gate.Name}.stark")), $"Missing experimental Stark benchmark for {gate.Directory}/{gate.Name}.");
            Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.c")), $"Missing C benchmark for {gate.Directory}/{gate.Name}.");
            Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.rs")), $"Missing Rust benchmark for {gate.Directory}/{gate.Name}.");

            if (gate.HasStableStark)
            {
                Assert.True(File.Exists(Path.Combine(directory, $"{gate.Name}.stark")), $"Missing stable Stark benchmark for {gate.Directory}/{gate.Name}.");
            }
        }
    }

    [Fact]
    public void BenchmarkHarnessReportsExperimentalStarkAsLanguageVariant()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-benchmarks.ps1"));

        Assert.Contains("$stem.StartsWith(\"Experimental\", [StringComparison]::Ordinal)", script, StringComparison.Ordinal);
        Assert.Contains("$languageLabel = \"stark-experimental\"", script, StringComparison.Ordinal);
        Assert.Contains("Emit-Row \"benchmark,language", script, StringComparison.Ordinal);
        Assert.Contains("llvm_object_us,link_us,toolchain_us,binary_bytes", script, StringComparison.Ordinal);
        Assert.Contains("median_us,avg_us", script, StringComparison.Ordinal);
        Assert.Contains("c_median_ratio", script, StringComparison.Ordinal);
        Assert.Contains("STARK_BENCH_SUBSET", script, StringComparison.Ordinal);
        Assert.Contains("STARK_BENCH_RUNTIME_ONLY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("benchmark,language,collection", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stable-stark", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic-stark", script, StringComparison.Ordinal);
        Assert.DoesNotContain("experimental-ring-stark", script, StringComparison.Ordinal);
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
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkSource), benchmarkSource),
            new CompilerOptions(
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O3,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(
            result.Succeeded,
            $"{benchmarkSource}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");

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

    private readonly record struct BenchmarkGate(string Directory, string Name, bool HasStableStark = true);
}
