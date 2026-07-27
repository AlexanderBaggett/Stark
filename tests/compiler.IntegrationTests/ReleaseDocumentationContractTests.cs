namespace compiler.IntegrationTests;

public sealed class ReleaseDocumentationContractTests
{
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
