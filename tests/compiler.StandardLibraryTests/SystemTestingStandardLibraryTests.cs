using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTestingStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceTestingHelpersCompileWithExplicitFactRunner()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "SystemTestingCompile.stark");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Testing
                module DemoTests

                [Fact]
                fn bool AdditionWorks() {
                    return System.Testing.Equal(4, 2 + 2);
                }

                export unsafe ffi fn i32[min max] main() {
                    stack mut u8[0 1] failed = 0;
                    if (System.Testing.RunFact("AdditionWorks", AdditionWorks()) != 0) {
                        failed = 1;
                    }

                    return System.Testing.ExitCode(failed);
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibTestingModuleStaysRawPointerFreeAndExplicit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testingSource = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System", "Testing.stark"));
        var systemSource = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System.stark"));

        Assert.DoesNotContain("rawptr<", testingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("rawmutptr<", testingSource, StringComparison.Ordinal);
        Assert.Contains("import System.Testing", systemSource, StringComparison.Ordinal);
        Assert.DoesNotContain("export import System.Testing", systemSource, StringComparison.Ordinal);
    }
}
