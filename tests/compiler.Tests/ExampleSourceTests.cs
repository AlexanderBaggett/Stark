using Stark.Compiler;

namespace compiler.Tests;

public sealed class ExampleSourceTests
{
    [Fact]
    public void RepresentativeExampleSourcesEmitLlvmWithoutFallbackLogs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplesRoot = Path.Combine(repositoryRoot, "examples");
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var exampleSources = new[]
        {
            "hello.stark",
            "arithmetic/Arithmetic.stark",
            "basic-syntax/BasicSyntax.stark",
            "control-flow/ControlFlow.stark",
            "multi-module/App.stark",
            "modules/App.stark",
            "borrowing/Borrowing.stark",
            "borrowing/OwnershipMoves.stark",
            "type-system/TypeSystem.stark",
            "data-model/DataModel.stark"
        };

        foreach (var relativePath in exampleSources)
        {
            var sourcePath = Path.Combine(examplesRoot, relativePath);
            var sourceDirectory = Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException($"Unable to resolve example directory for '{sourcePath}'.");
            var searchDirectories = new[] { sourceDirectory, stdlibRoot };
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(sourcePath), sourcePath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: targetInfo,
                    ModuleResolver: new TargetAwareStdLibModuleResolver(
                        new FileSystemModuleResolver(searchDirectories),
                        searchDirectories,
                        targetInfo)));

            Assert.True(
                result.Succeeded,
                $"{sourcePath}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()))}");
            FallbackLogAssertions.AssertNoFallbackLogs(result, "Representative example builds", sourcePath);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for example source tests.");
    }
}
