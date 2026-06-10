using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class BookSampleStandardLibraryTests : StandardLibraryTestSuite
{
    public static IEnumerable<object[]> CompileCheckedBookSamples()
    {
        // Positive book samples live per chapter: site/content/book/<chapter>/samples/*.stark
        // (negative ones live in rejected/ and are covered by scripts/check-book-samples.sh).
        var repositoryRoot = FindRepositoryRoot();
        var bookRoot = Path.Combine(repositoryRoot, "site", "content", "book");

        foreach (var chapterDirectory in Directory.EnumerateDirectories(bookRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var sampleRoot = Path.Combine(chapterDirectory, "samples");
            if (!Directory.Exists(sampleRoot))
            {
                continue;
            }

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
                // Samples only assert acceptance; borrow-liveness is the last pass that can
                // reject a program, so the SSA/optimization/emission passes add no coverage.
                StopAfterPassId: "borrow-liveness",
                EnforceIntegerRangeStorageRules: true));

        Assert.True(
            result.Succeeded,
            relativePath
            + Environment.NewLine
            + string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
