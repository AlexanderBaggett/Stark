using Stark.Compiler;

namespace compiler.Tests;

public sealed class BenchmarkSourceTests
{
    [Fact]
    public void AllocatorBenchmarkSourcesCompile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks", "allocator");
        var benchmarkSources = Directory.GetFiles(benchmarkRoot, "*.stark").Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(benchmarkSources);

        foreach (var benchmarkSource in benchmarkSources)
        {
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(benchmarkSource), benchmarkSource),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

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
}
