namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalRawBoundaryAuditTests : StandardLibraryTestSuite
{
    [Fact]
    public void HigherLevelExperimentalPublicApisDoNotExposeRawPointers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var experimentalRoot = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Experimental");
        var allowedLowLevelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(experimentalRoot, "Text.stark")
        };

        var violations = Directory.EnumerateFiles(experimentalRoot, "*.stark", SearchOption.AllDirectories)
            .Where(path => !allowedLowLevelFiles.Contains(path))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new
                {
                    Path = path,
                    Line = index + 1,
                    Text = line.Trim()
                }))
            .Where(item =>
                item.Text.StartsWith("public ", StringComparison.Ordinal)
                && (item.Text.Contains("rawptr", StringComparison.Ordinal)
                    || item.Text.Contains("rawmutptr", StringComparison.Ordinal)))
            .Select(item => $"{Path.GetRelativePath(repositoryRoot, item.Path)}:{item.Line}: {item.Text}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Higher-level experimental public APIs must not expose raw pointers."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }
}
