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

        Assert.Contains("@System_Experimental_Memory_CopyBytesDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Memory_FillInitializedBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Memory_CopyCodePointsDisjointInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Memory_FillInitializedCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Memory_MoveBytesInfallible(", mainBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Memory_MoveCodePointsInfallible(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyBytesDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_CopyCodePointsDisjoint(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_FillInitializedCodePoints(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveBytes(", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Memory_MoveCodePoints(", mainBody, StringComparison.Ordinal);
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
    public void ExperimentalStandardLibraryOptimizationBenchmarkGatesHaveExpectedSourceMatrix()
    {
        var repositoryRoot = FindRepositoryRoot();
        var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks");
        var gates = new[]
        {
            new BenchmarkGate("runtime", "RuntimeBufferFixed", HasStableStark: false),
            new BenchmarkGate("runtime", "RuntimeBufferDynamic", HasStableStark: false),
            new BenchmarkGate("network", "TcpLoopbackThroughput"),
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
            new BenchmarkGate("text", "UnicodeFormatting")
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
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkSource = Path.Combine(repositoryRoot, "benchmarks", relativePath);
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
        return ExtractDefinedFunctionText(
            llvm,
            "define i32 @main(",
            $"Expected benchmark main definition for {relativePath} to be emitted.");
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
