namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalBackendBoundaryAuditTests : StandardLibraryTestSuite
{
    [Fact]
    public void ExperimentalModulesDoNotUseBackendOpaqueBoundariesWithoutBenchmarkProof()
    {
        var repositoryRoot = FindRepositoryRoot();
        var experimentalRoot = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Experimental");

        var violations = Directory.EnumerateFiles(experimentalRoot, "*.stark", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new
                {
                    Path = path,
                    Line = index + 1,
                    Text = line.Trim()
                }))
            .Where(item => item.Text.Contains("[Backend(Opaque)]", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(repositoryRoot, item.Path)}:{item.Line}: {item.Text}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Experimental standard-library modules should stay transparent to whole-program optimization unless benchmark evidence justifies an opaque backend boundary."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }
}
