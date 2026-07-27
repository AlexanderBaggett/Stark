using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseNativeDependencyAuditTests
{
    [Fact]
    public void AuditScriptCoversEveryReleaseBinaryFamilyAndHostFormat()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "audit-release-native-dependencies.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("Get-ChildItem -LiteralPath $Root -File -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("Get-ApprovedNativeRoots", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsApprovedNativePayloadPath", script, StringComparison.Ordinal);
        Assert.Contains("Get-NativeFileFormat", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsManagedPortableExecutable", script, StringComparison.Ordinal);
        Assert.Contains("Inspect-MacBinary", script, StringComparison.Ordinal);
        Assert.Contains("otool", script, StringComparison.Ordinal);
        Assert.Contains("LC_RPATH", script, StringComparison.Ordinal);
        Assert.Contains("-Token \"@loader_path\" -AllowExact $true", script, StringComparison.Ordinal);
        Assert.Contains("-Token \"@executable_path\" -AllowExact $true", script, StringComparison.Ordinal);
        Assert.Contains("Test-SafeLoaderTokenPath", script, StringComparison.Ordinal);
        Assert.Contains("-OriginDirectory $LoaderOriginDirectory -Root $Root -AllowParentTraversal", script, StringComparison.Ordinal);
        Assert.Contains("-OriginDirectory $ExecutableOriginDirectory -Root $Root -AllowParentTraversal", script, StringComparison.Ordinal);
        Assert.Contains("$Path.IndexOf([char]0) -ge 0", script, StringComparison.Ordinal);
        Assert.Contains("-not $AllowParentTraversal -and $segment -in @(\".\", \"..\")", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsInsideRoot -Path $candidate -Root $Root", script, StringComparison.Ordinal);
        Assert.Contains("fail closed for dylibs", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::GetFullPath($Path)", script, StringComparison.Ordinal);
        Assert.Contains("Inspect-LinuxBinary", script, StringComparison.Ordinal);
        Assert.Contains("readelf", script, StringComparison.Ordinal);
        Assert.Contains("RPATH|RUNPATH", script, StringComparison.Ordinal);
        Assert.Contains("-Token '$ORIGIN' -AllowExact $true", script, StringComparison.Ordinal);
        Assert.Contains("-Token '${ORIGIN}' -AllowExact $true", script, StringComparison.Ordinal);
        Assert.Contains("-OriginDirectory $File.DirectoryName", script, StringComparison.Ordinal);
        Assert.Contains("contains an empty current-directory search entry", script, StringComparison.Ordinal);
        Assert.Contains("points inside the SDK but the file is missing", script, StringComparison.Ordinal);
        Assert.Contains("Get-BundledNativeLibraryNames", script, StringComparison.Ordinal);
        Assert.Contains("SDK-relative but no matching library is bundled", script, StringComparison.Ordinal);
        Assert.Contains("neither bundled nor in the approved base Linux runtime allowlist", script, StringComparison.Ordinal);
        Assert.Contains("Inspect-WindowsBinary", script, StringComparison.Ordinal);
        Assert.Contains("dumpbin", script, StringComparison.Ordinal);
        Assert.Contains("llvm-objdump", script, StringComparison.Ordinal);
        Assert.Contains("BundledDlls", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditScriptRejectsNonPolicyPathsAndProducesMachineReadableEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "audit-release-native-dependencies.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("release.json", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsInsideRoot", script, StringComparison.Ordinal);
        Assert.Contains("approved macOS system roots", script, StringComparison.Ordinal);
        Assert.Contains("neither SDK-relative nor an approved system library root", script, StringComparison.Ordinal);
        Assert.Contains("neither bundled in the SDK nor in the approved Windows system allowlist", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 1", script, StringComparison.Ordinal);
        Assert.Contains("status = if ($violations.Count -eq 0)", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Depth 8", script, StringComparison.Ordinal);
        Assert.Contains("throw \"Release native dependency audit found", script, StringComparison.Ordinal);

        Assert.DoesNotContain("/opt/homebrew", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/usr/local/lib", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("$ORIGIN/../lib", true)]
    [InlineData("${ORIGIN}/../lib", true)]
    [InlineData("$ORIGIN/../../../outside", false)]
    [InlineData("/lib/../tmp", false)]
    [InlineData("$ORIGIN::/usr/lib", false)]
    public async Task LinuxOriginSearchPathsAreNormalizedBeforePolicyChecks(string runPath, bool expectedToPass)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-native-audit-linux-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "stark-v0.1.0-linux-x64"));
            var binRoot = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "bin"));
            Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "lib"));
            File.WriteAllBytes(Path.Combine(binRoot.FullName, "probe"), [0x7f, 0x45, 0x4c, 0x46, 0, 0, 0, 0]);
            WriteCandidateIdentity(
                sdkRoot.FullName,
                "linux-x64",
                "linux-x64",
                "x86_64-unknown-linux-gnu");

            var toolRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "tools"));
            var readElfTool = Path.Combine(toolRoot.FullName, "readelf");
            File.WriteAllText(
                readElfTool,
                "#!/bin/sh\nprintf ' 0x000000000000001d (RUNPATH) Library runpath: [%s]\\n' \"$STARK_FAKE_RUNPATH\"\n");
            var executableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(readElfTool, executableMode);

            var reportPath = Path.Combine(testRoot.FullName, "report.json");
            var result = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File",
                    Path.Combine(repositoryRoot, "scripts", "audit-release-native-dependencies.ps1"),
                    "-SdkRoot", sdkRoot.FullName,
                    "-OutputPath", reportPath,
                ],
                repositoryRoot,
                new Dictionary<string, string>
                {
                    ["PATH"] = toolRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["STARK_FAKE_RUNPATH"] = runPath,
                });

            Assert.Equal(expectedToPass, result.ExitCode == 0);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(expectedToPass ? "ok" : "violations", report.RootElement.GetProperty("status").GetString());
            if (expectedToPass)
            {
                Assert.Equal(
                    "stark-staged-release-validation-subject",
                    report.RootElement.GetProperty("validatedCandidate").GetProperty("kind").GetString());
            }
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("examples/foreign-elf", "elf")]
    [InlineData("docs/native-payload.md", "mach-o")]
    public async Task AuditRejectsNativeArtifactsAnywhereOutsideApprovedPayloadRoots(
        string relativePath,
        string format)
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-native-audit-whole-stage-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "sdk"));
            File.WriteAllText(
                Path.Combine(sdkRoot.FullName, "release.json"),
                "{\"assetSuffix\":\"macos-arm64\",\"defaultTargetTriple\":\"arm64-apple-macosx11.0.0\"}");
            var payload = Path.Combine(sdkRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            File.WriteAllBytes(
                payload,
                format == "elf"
                    ? [0x7f, 0x45, 0x4c, 0x46, 0, 0, 0, 0]
                    : [0xcf, 0xfa, 0xed, 0xfe, 0, 0, 0, 0]);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    payload,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var reportPath = Path.Combine(testRoot.FullName, "report.json");
            var result = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File",
                    Path.Combine(repositoryRoot, "scripts", "audit-release-native-dependencies.ps1"),
                    "-SdkRoot", sdkRoot.FullName,
                    "-OutputPath", reportPath,
                ],
                repositoryRoot);

            Assert.NotEqual(0, result.ExitCode);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("violations", report.RootElement.GetProperty("status").GetString());
            var file = Assert.Single(report.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal(relativePath, file.GetProperty("Path").GetString());
            Assert.Equal(format, file.GetProperty("Format").GetString());
            Assert.Contains(
                file.GetProperty("Violations").EnumerateArray(),
                violation => violation.GetString()!.Contains("outside approved payload roots", StringComparison.Ordinal));
            if (format == "elf")
            {
                Assert.Contains(
                    file.GetProperty("Violations").EnumerateArray(),
                    violation => violation.GetString()!.Contains("not release platform 'macos'", StringComparison.Ordinal));
            }
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RepositoryContentStagingUsesTheManifestAndExcludesGeneratedOrNativeArtifacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-release-content-");
        try
        {
            var stageRoot = Path.Combine(testRoot.FullName, "stage");
            var result = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File",
                    Path.Combine(repositoryRoot, "scripts", "stage-release-repository-content.ps1"),
                    "-RepositoryRoot", repositoryRoot,
                    "-StageRoot", stageRoot,
                    "-ManifestPath", Path.Combine(repositoryRoot, "eng", "release", "archive-content.json"),
                ],
                repositoryRoot);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(stageRoot, "docs", "Userfacing", "LanguageReference.md")));
            Assert.True(File.Exists(Path.Combine(stageRoot, "docs", "StandardLibrary", "System.md")));
            Assert.True(File.Exists(Path.Combine(stageRoot, "docs", "Internals", "LanguageInternals.md")));
            Assert.True(File.Exists(Path.Combine(stageRoot, "docs", "Internals", "SdkLayoutAndResolution.md")));
            Assert.True(File.Exists(Path.Combine(stageRoot, "examples", "README.md")));
            Assert.True(File.Exists(Path.Combine(stageRoot, "examples", "hello.stark")));

            Assert.False(File.Exists(Path.Combine(stageRoot, "docs", "Internals", "ReleasePrep.md")));
            Assert.False(File.Exists(Path.Combine(stageRoot, "docs", "Self-host-Prep", "TASKS.md")));
            Assert.False(File.Exists(Path.Combine(stageRoot, "examples", "breakout", "breakout-raylib")));
            Assert.False(File.Exists(Path.Combine(stageRoot, "examples", "raylib", "dist", "libRaylibStark.starkpkg")));
            Assert.Empty(Directory.EnumerateFiles(stageRoot, "*.starkpkg", SearchOption.AllDirectories));
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void ArchiveSmokeRequiresOneIdentityMatchedTopLevelDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-archive.ps1"));

        Assert.Contains("Release archive must contain exactly one top-level SDK directory", script, StringComparison.Ordinal);
        Assert.Contains("$entries.Count -ne 1", script, StringComparison.Ordinal);
        Assert.Contains("$expectedRootName = \"stark-$releaseVersion-$releaseAssetSuffix\"", script, StringComparison.Ordinal);
        Assert.Contains("does not match release.json identity", script, StringComparison.Ordinal);
        Assert.Contains("must contain sdk.json and bin/stark[.exe]", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseFileChecksums -PackageRoot $packageRoot", script, StringComparison.Ordinal);
        Assert.Contains("Release archive contains untracked file", script, StringComparison.Ordinal);
        Assert.Contains("failed SHA-256 verification", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagingStagesExamplesAndThePlatformInstallerPair()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));
        var installerStageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "stage-release-installers.ps1"));

        Assert.Contains("stage-release-repository-content.ps1", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-OptionalTree -Source (Join-Path $repositoryRoot \"examples\")", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-OptionalTree -Source (Join-Path $repositoryRoot \"docs\")", packageScript, StringComparison.Ordinal);

        using var content = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "archive-content.json")));
        var roots = content.RootElement.GetProperty("nativeBinaryPolicy").GetProperty("approvedRoots")
            .EnumerateArray().Select(static value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.True(roots.SetEquals(["bin", "stdlib", "toolchain", "vendor"]));
        var forbidden = content.RootElement.GetProperty("repositoryContent").GetProperty("forbiddenExtensions")
            .EnumerateArray().Select(static value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(".starkpkg", forbidden);
        Assert.Contains(".exe", forbidden);
        Assert.Contains("stage-release-installers.ps1", packageScript, StringComparison.Ordinal);
        Assert.Contains("-StageRoot $stageRoot", packageScript, StringComparison.Ordinal);
        Assert.Contains("-AssetSuffix $AssetSuffix", packageScript, StringComparison.Ordinal);
        Assert.Contains("contentChecksumManifest = \"release-files.sha256\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("-Files (Get-FileManifest -Root $stageRoot)", packageScript, StringComparison.Ordinal);
        Assert.Contains("Write-ReleaseFileChecksums", packageScript, StringComparison.Ordinal);
        Assert.Contains("@(\"install.ps1\", \"uninstall.ps1\")", installerStageScript, StringComparison.Ordinal);
        Assert.Contains("@(\"install.sh\", \"uninstall.sh\")", installerStageScript, StringComparison.Ordinal);
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

    private static void WriteCandidateIdentity(
        string sdkRoot,
        string targetId,
        string runtimeIdentifier,
        string targetTriple)
    {
        const string configurationSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
        const string planSha256 = "3333333333333333333333333333333333333333333333333333333333333333";
        const string sourceCommit = "1111111111111111111111111111111111111111";
        var buildIdentity = new Dictionary<string, object?>
        {
            ["configurationSha256"] = configurationSha256,
            ["identity"] = $"sha256:{new string('4', 64)}",
            ["kind"] = "content-addressed-release-build",
            ["releasePlanSha256"] = planSha256,
        };
        var release = new Dictionary<string, object?>
        {
            ["assetSuffix"] = targetId,
            ["buildIdentity"] = buildIdentity,
            ["configuration"] = new Dictionary<string, object?> { ["sha256"] = configurationSha256 },
            ["defaultTargetTriple"] = targetTriple,
            ["gitCommit"] = sourceCommit,
            ["releaseVersion"] = "v0.1.0",
            ["runtimeIdentifier"] = runtimeIdentifier,
            ["schemaVersion"] = 2,
            ["source"] = new Dictionary<string, object?> { ["commit"] = sourceCommit },
            ["starkVersion"] = "v0.1.0",
            ["targetId"] = targetId,
            ["workflowIdentity"] = buildIdentity,
        };
        var releaseJson = JsonSerializer.SerializeToUtf8Bytes(
            release,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(Path.Combine(sdkRoot, "release.json"), releaseJson);
        var releaseHash = Convert.ToHexString(SHA256.HashData(releaseJson)).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(sdkRoot, "release-files.sha256"),
            $"{releaseHash}  release.json\n",
            Encoding.ASCII);
    }

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("STARK_TEST_POWERSHELL"),
                     "pwsh",
                 }.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var result = await RunProcessAsync(
                    candidate!,
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
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
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
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
