using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed class CoreVendorReleaseInputScriptTests
{
    private static readonly string[] CorePackageIds =
    [
        "Vendor.STB.Image",
        "Vendor.Miniaudio",
        "Vendor.Cgltf"
    ];

    [Fact]
    public void CoreVendorCatalogPinsEverySourceInputAndUsesTheReleaseContributor()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));

        var packages = catalog.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(package => CorePackageIds.Contains(package.GetProperty("id").GetString(), StringComparer.Ordinal))
            .ToDictionary(package => package.GetProperty("id").GetString()!, StringComparer.Ordinal);

        Assert.Equal(CorePackageIds.Length, packages.Count);
        foreach (var packageId in CorePackageIds)
        {
            var package = packages[packageId];
            Assert.Equal("scripts/prepare-core-vendor-release-input.ps1", package.GetProperty("buildRecipe").GetString());

            foreach (var targetId in new[] { "linux-x64", "windows-x64", "macos-arm64" })
            {
                Assert.Equal("required-source-build", package.GetProperty("targetSupport").GetProperty(targetId).GetString());
            }

            var sources = package.GetProperty("sourceFiles").EnumerateArray().ToArray();
            Assert.NotEmpty(sources);
            foreach (var source in sources)
            {
                var relativePath = source.GetProperty("path").GetString()!;
                var expectedHash = source.GetProperty("sha256").GetString()!;
                var actualHash = Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath))));
                Assert.Matches("^[0-9a-f]{64}$", expectedHash);
                Assert.Equal(expectedHash, actualHash);
            }

            Assert.NotEmpty(package.GetProperty("licenseEvidencePaths").EnumerateArray());
        }
    }

    [Fact]
    public void CoreVendorCatalogCarriesTheReviewedPerPlatformSystemLinkFacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));
        var packages = catalog.RootElement.GetProperty("packages")
            .EnumerateArray()
            .ToDictionary(package => package.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var packageId in new[] { "Vendor.STB.Image", "Vendor.Cgltf" })
        {
            var facts = packages[packageId].GetProperty("systemLinkFacts");
            Assert.Empty(facts.GetProperty("linux").EnumerateArray());
            Assert.Empty(facts.GetProperty("windows").EnumerateArray());
            Assert.Empty(facts.GetProperty("macos").EnumerateArray());
        }

        var miniaudio = packages["Vendor.Miniaudio"].GetProperty("systemLinkFacts");
        Assert.Equal(["dl", "m", "pthread"], Strings(miniaudio.GetProperty("linux")));
        Assert.Equal(["ole32", "user32", "advapi32"], Strings(miniaudio.GetProperty("windows")));
        Assert.Equal(["AudioToolbox", "CoreAudio", "CoreFoundation"], Strings(miniaudio.GetProperty("macos")));
    }

    [Fact]
    public void ContributorIsHermeticAdditiveAndEmitsTheOrchestratorContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "prepare-core-vendor-release-input.ps1"));

        foreach (var parameter in new[]
        {
            "$AssetSuffix",
            "$TargetTriple",
            "$OutputVendorRoot",
            "$StdlibPackageDir",
            "$ToolchainDir",
            "$CompilerProject",
            "$ContributionManifestPath"
        })
        {
            Assert.Contains(parameter, script, StringComparison.Ordinal);
        }

        Assert.Contains("Assert-Sha256", script, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredProperty -Object $targetSupport -Name $AssetSuffix", script, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredProperty -Object $systemLinkFacts -Name $targetOperatingSystem", script, StringComparison.Ordinal);
        Assert.Contains("Assert-MatchingHost", script, StringComparison.Ordinal);
        Assert.Contains("Assert-SafeOutputRoot -Path $outputRoot", script, StringComparison.Ordinal);
        Assert.Contains("Output Vendor root '$candidate' cannot be a filesystem root", script, StringComparison.Ordinal);
        Assert.Contains("must be a child of '$artifactsRoot'", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $repositoryRoot \"vendor/src\"", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointPath", script, StringComparison.Ordinal);
        Assert.Contains("must be outside shared OutputVendorRoot", script, StringComparison.Ordinal);
        Assert.Contains("[Guid]::NewGuid().ToString(\"N\")", script, StringComparison.Ordinal);
        Assert.Contains("} finally {", script, StringComparison.Ordinal);
        Assert.Contains("artifacts/core-vendor-work", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $workRoot -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean unexpected core Vendor work root", script, StringComparison.Ordinal);
        Assert.Contains("\"--emit-lib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-stark-path\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--package-profile\", \"release\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--target\", $TargetTriple", script, StringComparison.Ordinal);
        Assert.Contains("\"--toolchain-dir\", $toolchainPath", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-source\", $stagedImplementation", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-include-dir\", $stagedNativeRoot", script, StringComparison.Ordinal);
        Assert.Contains("@($declaredSystemLinks)", script, StringComparison.Ordinal);
        Assert.Contains("@(-framework", script.Replace("\"", string.Empty), StringComparison.Ordinal);
        Assert.Contains("Generated release package '$PackageId' must not depend on pkg-config", script, StringComparison.Ordinal);

        Assert.Contains("schemaVersion = 1", script, StringComparison.Ordinal);
        Assert.Contains("targetId = $AssetSuffix", script, StringComparison.Ordinal);
        Assert.Contains("packages = [object[]]", script, StringComparison.Ordinal);
        Assert.Contains("nativePayload = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("licenseFiles = [object[]]", script, StringComparison.Ordinal);
        Assert.Contains("provenance = $provenanceDescriptor", script, StringComparison.Ordinal);
        Assert.Contains("Kind = \"native-source\"", script, StringComparison.Ordinal);
        Assert.Contains("Kind = \"header\"", script, StringComparison.Ordinal);
        Assert.Contains("Kind = \"documentation\"", script, StringComparison.Ordinal);
        Assert.Contains("Kind = \"license\"", script, StringComparison.Ordinal);
        Assert.Contains("Kind = \"provenance\"", script, StringComparison.Ordinal);
        Assert.Contains("New-PlainFileDescriptor -Root $outputRoot -Path $_", script, StringComparison.Ordinal);
        Assert.Contains("New-PlainFileDescriptor -Root $outputRoot -Path $provenancePath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("native-input", script, StringComparison.Ordinal);
        Assert.DoesNotContain("license-evidence", script, StringComparison.Ordinal);
        Assert.Contains("imageSha256", script, StringComparison.Ordinal);
        Assert.Contains("librarySha256", script, StringComparison.Ordinal);

        var artifactKinds = Regex.Matches(script, "Kind = \\\"([^\\\"]+)\\\"")
            .Select(static match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(artifactKinds.SetEquals(["native-source", "header", "documentation", "license", "provenance"]));

        var plainDescriptorStart = script.IndexOf("function New-PlainFileDescriptor", StringComparison.Ordinal);
        var plainDescriptorEnd = script.IndexOf("function Copy-RequiredFile", plainDescriptorStart, StringComparison.Ordinal);
        Assert.True(plainDescriptorStart >= 0 && plainDescriptorEnd > plainDescriptorStart);
        var plainDescriptor = script[plainDescriptorStart..plainDescriptorEnd];
        Assert.DoesNotContain("kind =", plainDescriptor, StringComparison.Ordinal);
        Assert.Contains("path =", plainDescriptor, StringComparison.Ordinal);
        Assert.Contains("bytes =", plainDescriptor, StringComparison.Ordinal);
        Assert.Contains("sha256 =", plainDescriptor, StringComparison.Ordinal);

        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--native-pkg-config", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stdlib/src", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $outputRoot -Recurse", script, StringComparison.Ordinal);
        Assert.Contains("A contributor must never replace the shared output Vendor root", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorUsesTargetCorrectArchiveNamesAndPackageOwnedRelativeNativePaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "prepare-core-vendor-release-input.ps1"));

        Assert.Contains("\"$($definition.LibraryStem).lib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"lib$($definition.LibraryStem).a\"", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::ChangeExtension($libraryPath, \".starkpkg\")", script, StringComparison.Ordinal);
        Assert.Contains("-ExpectedSource $definition.ImplementationFile", script, StringComparison.Ordinal);
        Assert.Contains("-ExpectedIncludeDirectory (\"native/\" + $definition.NativeSlug)", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PortablePackagePath", script, StringComparison.Ordinal);
        Assert.Contains("$dependencyIds -join \"`n\") -cne \"System\"", script, StringComparison.Ordinal);
        Assert.Contains("$moduleNames -join \"`n\") -cne $PackageId", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributionPathSorterUsesOrdinalOrderingForMixedCasePaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "prepare-core-vendor-release-input.ps1"));
        Assert.Contains("Sort-ObjectsOrdinalByProperty", script, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::Ordinal.Compare", script, StringComparison.Ordinal);
        Assert.Contains("$left -is [System.Collections.IDictionary]", script, StringComparison.Ordinal);
        Assert.Contains("$left[$PropertyName]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object { $_.path }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$packageContributions | Sort-Object", script, StringComparison.Ordinal);

        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var helperStart = script.IndexOf("function Sort-ObjectsOrdinalByProperty", StringComparison.Ordinal);
        var helperEnd = script.IndexOf("function Write-DeterministicJson", helperStart, StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart);
        var fixtureScript = string.Concat(
            script[helperStart..helperEnd],
            "\n$values = @(\n",
            "    [ordered]@{ path = 'z/native.c' },\n",
            "    [ordered]@{ path = 'a/header.h' },\n",
            "    [ordered]@{ path = 'B/VERSION.md' },\n",
            "    [ordered]@{ path = 'A/LICENSE' },\n",
            "    [pscustomobject]@{ path = 'C/NOTICE' }\n",
            ")\n",
            "$sorted = @(Sort-ObjectsOrdinalByProperty -Values $values -PropertyName 'path')\n",
            "[Console]::Out.Write(($sorted.path -join \"`n\"))\n");

        var fixturePath = Path.Combine(Path.GetTempPath(), $"stark-core-vendor-ordinal-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(fixturePath, fixtureScript);
        try
        {
            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-File", fixturePath],
                repositoryRoot);
            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.Equal("A/LICENSE\nB/VERSION.md\nC/NOTICE\na/header.h\nz/native.c", result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    private static string[] Strings(JsonElement value)
        => value.EnumerateArray().Select(static item => item.GetString()!).ToArray();

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : new[] { "pwsh" })
        {
            try
            {
                var result = await RunProcessAsync(
                    candidate,
                    ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"],
                    workingDirectory);
                if (result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
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
