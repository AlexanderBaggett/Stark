using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseArchiveMetadataTests
{
    [Fact]
    public void MetadataTemplateRequiresTheExhaustiveSchemaTwoArchiveContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "release-metadata.template.json")));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("staticValues").GetProperty("releaseSchemaVersion").GetInt32());
        Assert.Equal("64-bit-only", root.GetProperty("staticValues").GetProperty("architecturePolicy").GetString());

        var bindings = root.GetProperty("bindings")
            .EnumerateArray()
            .ToDictionary(static binding => binding.GetProperty("output").GetString()!, StringComparer.Ordinal);
        foreach (var output in new[]
        {
            "gitCommit",
            "source",
            "workflowIdentity",
            "buildIdentity",
            "buildOptions",
            "configuration",
            "schemas",
            "targetFacts",
            "contentIdentities",
            "sdk",
            "packages",
            "packageSchemaFacts",
            "dependencies",
            "vendorCatalog",
        })
        {
            Assert.True(bindings.TryGetValue(output, out var binding), $"Missing required release metadata binding '{output}'.");
            Assert.True(binding.GetProperty("required").GetBoolean());
        }

        Assert.Equal("source.commit", bindings["gitCommit"].GetProperty("source").GetString());
    }

    [Fact]
    public void PackagerFailsClosedAndPreservesReproducibilityFacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));

        foreach (var expected in new[]
        {
            "[string] $BuildConfigurationSha256",
            "[string] $BuildPlanSha256",
            "[string] $ReleaseToolsPath",
            "Release packaging requires Git to prove",
            "status --porcelain --untracked-files=all",
            "Release packaging requires a clean tracked source tree",
            "Release packaging requires a compiler-private backend closure",
            "content-addressed-release-build",
            "stark-content-addressed-release-build-v2",
            "archiveContainerTool = $archiveToolMetadata",
            "releaseToolAssemblySha256=",
            "Get-PackageImageFormatVersion",
            "packageSchemaFacts = [object[]]$PackageSchemaFacts",
            "packagingInputsSha256 = $packagingConfigurationSha256",
            "releasePlanSha256 = $explicitBuildPlanSha256",
            "get-release-configuration-identity.ps1",
            "declarationSha256 = Get-ObjectSha256 -Value $dependency",
            "selectionSha256 = Get-ObjectSha256 -Value $selections[0]",
            "license = [string](Get-RequiredJsonPropertyValue -Object $dependency -Name \"license\")",
            "managedLicenseInventory = [ordered]@{",
            "Managed license evidence must inventory exactly three target license/notice files",
            "releaseContributionSha256 = Get-ObjectSha256 -Value $releaseContributions[0]",
            "selectedPackages = [object[]]$vendorPackageFacts",
            "contentIdentities = $ContentIdentities",
            "Write-DeterministicJson -Path $Path -Value $releaseJson",
        })
        {
            Assert.Contains(expected, script, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("GITHUB_RUN_ID", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_RUN_ATTEMPT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_REF", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_REPOSITORY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_SERVER_URL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("powerShellVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Date", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagerUsesTheRepositoryOwnedDeterministicArchiveWriter()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));
        var archiveWriter = File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "Stark.ReleaseTools", "ArchiveCreator.cs"));
        var identity = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "get-release-configuration-identity.ps1"));
        var localDriver = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-release.ps1"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("create-archive", script, StringComparison.Ordinal);
        Assert.Contains("--source-root $stageRoot", script, StringComparison.Ordinal);
        Assert.Contains("--kind $ArchiveKind", script, StringComparison.Ordinal);
        Assert.Contains("[string] $ReleaseToolsPath", script, StringComparison.Ordinal);
        Assert.Contains("eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Compress-Archive", script, StringComparison.Ordinal);
        Assert.DoesNotContain("tar -czf", script, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UnixEpoch", archiveWriter, StringComparison.Ordinal);
        Assert.Contains("ZipEpoch", archiveWriter, StringComparison.Ordinal);
        Assert.Contains("Windows ZIP release archives cannot contain symbolic links", archiveWriter, StringComparison.Ordinal);
        Assert.Contains("eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj", identity, StringComparison.Ordinal);
        Assert.Contains("eng/release/Stark.ReleaseTools", identity, StringComparison.Ordinal);
        Assert.Contains("-Filter \"*.cs\"", identity, StringComparison.Ordinal);
        Assert.Contains("[string] $ReleaseToolsPath", localDriver, StringComparison.Ordinal);
        Assert.Contains("\"-ReleaseToolsPath\", $releaseToolsAssembly", localDriver, StringComparison.Ordinal);
        Assert.Contains("RELEASE_TOOLS_DLL", workflow, StringComparison.Ordinal);
        Assert.Contains("Stark.ReleaseTools/Stark.ReleaseTools.csproj", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("python", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actions/setup-python@", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationIdentityCapturesEveryDotSourcedReleaseHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptsRoot = Path.Combine(repositoryRoot, "scripts");
        var identityScript = File.ReadAllText(Path.Combine(scriptsRoot, "get-release-configuration-identity.ps1"));

        var captured = 0;
        foreach (var scriptPath in Directory.EnumerateFiles(scriptsRoot, "*.ps1", SearchOption.TopDirectoryOnly))
        {
            var script = File.ReadAllText(scriptPath);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                script,
                "(?m)^\\s*\\.\\s+\\(Join-Path\\s+\\$PSScriptRoot\\s+\"([^\"]+\\.ps1)\"\\)"))
            {
                var helper = match.Groups[1].Value;
                Assert.Contains($"\"scripts/{helper}\"", identityScript, StringComparison.Ordinal);
                captured++;
            }
        }

        Assert.True(captured > 0, "Expected at least one dot-sourced release helper contract.");
        Assert.Contains("\".gitattributes\"", identityScript, StringComparison.Ordinal);
        Assert.Contains("scripts/release-repository-audit-allowlist.json", identityScript, StringComparison.Ordinal);
        Assert.Contains("sha256-ordinal-path-size-content-v1", identityScript, StringComparison.Ordinal);
    }

    [Fact]
    public void StageValidatorRecomputesEveryCriticalArchiveIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var validator = File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "Stark.ReleaseTools", "ReleaseStageValidator.cs"));

        foreach (var expected in new[]
        {
            "RequiredInt(\"schemaVersion\"",
            "source identity contains missing or environment-dependent facts",
            "RequiredBool(\"trackedWorkingTreeDirty\"",
            "release configuration hashes disagree",
            "content-addressed build identity digest mismatch",
            "Release tool is not bound",
            "release package image hash mismatch",
            "release dependency hashes are invalid",
            "selected Vendor package identity is invalid",
            "release staged Vendor catalog hash differs",
            "ValidateContentIdentity(root, identities",
        })
        {
            Assert.Contains(expected, validator, StringComparison.Ordinal);
        }
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
