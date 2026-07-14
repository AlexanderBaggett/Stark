using Stark.Compiler;

namespace compiler.Tests;

public sealed class SdkCrossStageGoldenTests
{
    [Fact]
    public void SharedManifestFixtureMatchesStage0NormalizedSummary()
    {
        var fixtureDirectory = FixtureDirectory();
        var manifestPath = Path.Combine(fixtureDirectory, "valid.sdk.json");
        var result = SdkManifestLoader.Parse(
            File.ReadAllText(manifestPath),
            fixtureDirectory,
            manifestPath);

        Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics));
        Assert.NotNull(result.Manifest);
        Assert.Equal(
            File.ReadAllText(Path.Combine(fixtureDirectory, "valid.summary.txt")),
            SdkManifestNormalization.RenderSummary(result.Manifest!));
    }

    [Theory]
    [InlineData("malformed.sdk.json", "STK7401")]
    [InlineData("schema-v2.sdk.json", "STK7402")]
    public void SharedManifestFailuresMatchStage0DiagnosticIdentifiers(
        string fixtureName,
        string expectedCode)
    {
        var fixtureDirectory = FixtureDirectory();
        var manifestPath = Path.Combine(fixtureDirectory, fixtureName);
        var result = SdkManifestLoader.Parse(
            File.ReadAllText(manifestPath),
            fixtureDirectory,
            manifestPath);

        Assert.Null(result.Manifest);
        Assert.Equal(expectedCode, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MissingSharedManifestMatchesStage0DiagnosticIdentifier()
    {
        var result = SdkManifestLoader.Load(FixtureDirectory());

        Assert.Null(result.Manifest);
        Assert.Equal("STK7400", Assert.Single(result.Diagnostics).Code);
    }

    private static string FixtureDirectory() =>
        Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "sdk-cross-stage");

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

        throw new DirectoryNotFoundException("Could not find the Stark repository root.");
    }

    private static string FormatDiagnostics(IEnumerable<SdkDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
