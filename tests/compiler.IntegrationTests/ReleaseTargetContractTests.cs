using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseTargetContractTests
{
    private static readonly string[] TargetIds =
    [
        "linux-x64",
        "linux-arm64",
        "windows-x64",
        "windows-arm64",
        "macos-x64",
        "macos-arm64",
    ];

    [Fact]
    public void FastWorkflowRunsTheRepositoryContractOnEveryNativeTargetRunner()
    {
        var repositoryRoot = FindRepositoryRoot();
        var contractWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release-contract.yml"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        using var targets = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "targets.json")));

        Assert.StartsWith("name: Release contract", contractWorkflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", contractWorkflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", contractWorkflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/test-release-target-contract.ps1", contractWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("acquire-llvm-toolchain.ps1 -", contractWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("run-release-quality-gate.ps1", contractWorkflow, StringComparison.Ordinal);

        var manifestTargets = targets.RootElement.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(TargetIds, manifestTargets.Select(static target => target.GetProperty("id").GetString()));
        foreach (var target in manifestTargets)
        {
            var id = target.GetProperty("id").GetString()!;
            var runner = target.GetProperty("githubRunner").GetString()!;
            Assert.Contains($"target_id: {id}", contractWorkflow, StringComparison.Ordinal);
            Assert.Contains($"os: {runner}", contractWorkflow, StringComparison.Ordinal);
        }

        var contractJob = releaseWorkflow.IndexOf("  contract:", StringComparison.Ordinal);
        var qualityJob = releaseWorkflow.IndexOf("  quality:", StringComparison.Ordinal);
        Assert.True(contractJob >= 0 && qualityJob > contractJob, "Fast target contracts must precede expensive release qualification.");
        Assert.Contains("name: Contract ${{ matrix.asset_suffix }}", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("needs: [prepare, contract]", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractScriptCoversEveryConfiguredTargetAndCriticalProducerConsumerBoundary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test-release-target-contract.ps1"));

        foreach (var targetId in TargetIds)
        {
            Assert.Contains(targetId, script, StringComparison.Ordinal);
        }
        Assert.Contains("ProcessArchitecture", script, StringComparison.Ordinal);
        Assert.Contains("Parser]::ParseFile", script, StringComparison.Ordinal);
        Assert.Contains("required-binary", script, StringComparison.Ordinal);
        Assert.Contains("glfw-3.4.bin.MACOS.zip", script, StringComparison.Ordinal);
        Assert.Contains("lib/libllvm*", script, StringComparison.Ordinal);
        Assert.Contains("--package-image-output", script, StringComparison.Ordinal);
        Assert.Contains("SQLite native-object validation", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateBackendManifestNeverSelectsDevelopmentArchivesAsRuntimePatterns()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "llvm-22.1.8-assets.json")));
        var platforms = manifest.RootElement.GetProperty("platforms");

        foreach (var targetId in TargetIds)
        {
            var patterns = platforms.GetProperty(targetId).GetProperty("requiredPatterns")
                .EnumerateArray()
                .Select(static pattern => pattern.GetString()!)
                .ToArray();
            Assert.DoesNotContain(patterns, static pattern => pattern.EndsWith(".a", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(patterns, static pattern => pattern.EndsWith(".lib", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("lib/libLLVM*", patterns, StringComparer.Ordinal);
        }

        var acquisition = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "acquire-llvm-toolchain.ps1"));
        Assert.Contains("runtime pattern selected development library", acquisition, StringComparison.Ordinal);
        Assert.Contains("Static/import libraries must not enter a Stark release", acquisition, StringComparison.Ordinal);
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
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
