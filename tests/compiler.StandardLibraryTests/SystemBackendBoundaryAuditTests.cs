using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemBackendBoundaryAuditTests : StandardLibraryTestSuite
{
    [Fact]
    public void OptimizedStandardLibraryBenchmarksDoNotEmitOpaqueStandardLibraryFunctions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var benchmarkPaths = new[]
        {
            Path.Combine(repositoryRoot, "benchmarks", "allocator", "MemoryCopyFill.stark"),
            Path.Combine(repositoryRoot, "benchmarks", "collections", "DictionaryLookup.stark")
        };

        foreach (var benchmarkPath in benchmarkPaths)
        {
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: targetInfo,
                    ModuleResolver: new TargetAwareStdLibModuleResolver(
                        new FileSystemModuleResolver(sourceRoot),
                        [sourceRoot],
                        targetInfo)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            var violations = llvm.Split('\n')
                .Select(static (line, index) => new
                {
                    Line = index + 1,
                    Text = line.TrimEnd('\r')
                })
                .Where(static item =>
                    (item.Text.Contains("@System_", StringComparison.Ordinal)
                        || item.Text.Contains("@__stark_mono_fn_System_", StringComparison.Ordinal)
                        || item.Text.Contains("@System_Memory_", StringComparison.Ordinal)
                        || item.Text.Contains("@__stark_mono_fn_System_Memory_", StringComparison.Ordinal))
                    && (item.Text.Contains("optnone", StringComparison.Ordinal)
                        || item.Text.Contains("noinline", StringComparison.Ordinal)))
                .Select(item => $"{Path.GetRelativePath(repositoryRoot, benchmarkPath)}:{item.Line}: {item.Text}")
                .ToArray();

            Assert.True(
                violations.Length == 0,
                "Optimized standard-library functions should stay transparent to LLVM unless an explicit benchmark-backed opaque boundary is added."
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
        }
    }
}
