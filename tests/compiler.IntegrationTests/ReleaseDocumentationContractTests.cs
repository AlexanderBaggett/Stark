namespace compiler.IntegrationTests;

using System.Text.Json;

public sealed class ReleaseDocumentationContractTests
{
    [Fact]
    public void ShippedLanguageInternalsIncludesItsAssemblyContractTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var content = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "archive-content.json")));
        var internals = content.RootElement.GetProperty("repositoryContent").GetProperty("trees")
            .EnumerateArray()
            .Single(static tree => tree.GetProperty("id").GetString() == "documentation-linked-internals");
        var files = internals.GetProperty("includeFiles").EnumerateArray()
            .Select(static file => file.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("LanguageInternals.md", files);
        Assert.Contains("ASMFunctionApproach.md", files);
        Assert.Contains("(ASMFunctionApproach.md)", File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "Internals", "LanguageInternals.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void BookStructureGateSupportsTheSystemBashShippedByMacOS()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "check-book-structure.sh"));

        Assert.DoesNotContain("mapfile", script, StringComparison.Ordinal);
        Assert.Contains("while IFS= read -r step", script, StringComparison.Ordinal);
        Assert.Contains("steps+=(\"${step}\")", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Stark repository root.");
    }
}
