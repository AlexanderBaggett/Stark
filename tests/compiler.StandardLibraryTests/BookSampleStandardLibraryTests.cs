using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class BookSampleStandardLibraryTests : StandardLibraryTestSuite
{
    public static IEnumerable<object[]> CompileCheckedBookSamples()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sampleRoots = new[]
        {
            Path.Combine(repositoryRoot, "site", "assets", "book", "samples"),
            Path.Combine(repositoryRoot, "site", "assets", "book", "stdlib-samples")
        };

        foreach (var sampleRoot in sampleRoots)
        {
            foreach (var path in Directory.EnumerateFiles(sampleRoot, "*.stark").Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return [Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/')];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CompileCheckedBookSamples))]
    public void PositiveBookSamplesCompileWithStrictIntegerRanges(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(path), path),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EnforceIntegerRangeStorageRules: true));

        Assert.True(
            result.Succeeded,
            relativePath
            + Environment.NewLine
            + string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
