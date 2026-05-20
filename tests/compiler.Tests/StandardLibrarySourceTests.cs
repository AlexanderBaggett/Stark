using Stark.Compiler;

namespace compiler.Tests;

public sealed class StandardLibrarySourceTests
{
    [Fact]
    public void StandardLibraryRootEmitsLlvmWithoutFallbackLogs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var sourcePath = Path.Combine(sourceRoot, "System.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(sourcePath), sourcePath),
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
            $"{sourcePath}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");
        FallbackLogAssertions.AssertNoFallbackLogs(result, "Standard-library builds", sourcePath);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for standard-library source tests.");
    }
}
