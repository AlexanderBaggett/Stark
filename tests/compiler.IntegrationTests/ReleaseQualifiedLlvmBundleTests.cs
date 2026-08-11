using System.Text.Json;
using System.Text.Json.Nodes;
using Stark.ReleaseTools;

namespace compiler.IntegrationTests;

public sealed class ReleaseQualifiedLlvmBundleTests
{
    private static readonly string[] TargetIds =
    [
        "linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64",
    ];

    [Fact]
    public void BundleLockTracksEveryTargetAndStartsWithoutUnpinnedPublicationFields()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eng", "release", "llvm-toolchain-bundles.json")));
        var entries = document.RootElement.GetProperty("targets").EnumerateArray().ToArray();

        Assert.Equal(TargetIds, entries.Select(entry => entry.GetProperty("target").GetString()));
        Assert.All(entries, entry =>
        {
            Assert.Equal("build-required", entry.GetProperty("status").GetString());
            Assert.False(entry.TryGetProperty("archive", out _));
            Assert.False(entry.TryGetProperty("manifestSha256", out _));
        });
    }

    [Fact]
    public void PreparationWorkflowBuildsAllTargetsAndPublishesOnlyTheCompleteImmutableSet()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "prepare-llvm-toolchains.yml"));

        Assert.StartsWith("name: Prepare qualified LLVM toolchains", workflow, StringComparison.Ordinal);
        Assert.Contains("--include-planned", workflow, StringComparison.Ordinal);
        Assert.Contains("-IgnoreQualifiedBundle", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-private-backend-bundle", workflow, StringComparison.Ordinal);
        Assert.Contains("create-archive", workflow, StringComparison.Ordinal);
        Assert.Contains("extract-archive", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/attest-build-provenance@977bb373ede98d70efdf65b84cb5f73e068dcc2a", workflow, StringComparison.Ordinal);
        Assert.Contains("Publication requires exactly six unique LLVM bundle records", workflow, StringComparison.Ordinal);
        Assert.Contains("ref=main and an explicit full commit SHA", workflow, StringComparison.Ordinal);
        Assert.Contains("refusing to replace them", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--clobber", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedPublishedLockShapePassesTheSameConfigurationContractUsedByRelease()
    {
        var root = FindRepositoryRoot();
        var configuration = ReleaseConfiguration.Validate(root);
        var dependencies = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "dependencies.json"), "dependencies.json");
        var backend = dependencies.RequiredArray("dependencies", "dependencies.json")
            .OfType<JsonObject>()
            .Single(entry => entry.RequiredString("kind", "dependency") == "compiler-private-backend");
        var document = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "llvm-toolchain-bundles.json"), "bundle lock");
        foreach (var entry in document.RequiredArray("targets", "bundle lock").OfType<JsonObject>())
        {
            var assetName = entry.RequiredString("assetName", "bundle target");
            entry["status"] = "published";
            entry["archive"] = new JsonObject
            {
                ["name"] = assetName,
                ["url"] = $"https://github.com/AlexanderBaggett/Stark/releases/download/llvm-22.1.8-stark.1/{assetName}",
                ["sha256"] = new string('1', 64),
                ["size"] = 1024L,
            };
            entry["manifestSha256"] = new string('2', 64);
            entry["qualificationCommit"] = new string('3', 40);
            entry["qualificationWorkflow"] = "https://github.com/AlexanderBaggett/Stark/actions/runs/1";
        }

        var statuses = ReleaseConfiguration.ValidatePrivateBackendBundleDocument(document, backend, configuration.Targets);

        Assert.Equal(TargetIds, statuses.Keys);
        Assert.All(statuses.Values, status => Assert.Equal("published", status));
    }

    [Fact]
    public void ReleaseConsumesPublishedBundlesThroughTheVerifiedExclusivePath()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var acquisition = File.ReadAllText(Path.Combine(root, "scripts", "acquire-llvm-toolchain.ps1"));
        var localRelease = File.ReadAllText(Path.Combine(root, "scripts", "build-release.ps1"));

        Assert.Contains("matrix.llvm_bundle_manifest", workflow, StringComparison.Ordinal);
        Assert.Contains("-ReleaseToolsPath $env:RELEASE_TOOLS_DLL", workflow, StringComparison.Ordinal);
        Assert.Contains("$bundleStatus -eq \"published\"", acquisition, StringComparison.Ordinal);
        Assert.Contains("safe extraction and closure verification", acquisition, StringComparison.Ordinal);
        Assert.Contains("--expected-manifest-sha256", acquisition, StringComparison.Ordinal);
        Assert.Contains("verify-private-backend-bundle", acquisition, StringComparison.Ordinal);
        Assert.Contains("-QualifiedBundleManifestPath", localRelease, StringComparison.Ordinal);
        Assert.Contains("-ReleaseToolsPath", localRelease, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Stark repository root.");
    }
}
