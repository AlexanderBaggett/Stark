using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseLocalBuildScriptTests
{
    [Fact]
    public void LocalDriverReusesReleaseManifestsAndWorkflowScripts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-release.ps1"));
        var qualityRunner = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "run-release-quality-gate.ps1"));
        var configurationIdentityScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "get-release-configuration-identity.ps1"));
        var releaseContract = script + "\n" + qualityRunner + "\n" + configurationIdentityScript;

        foreach (var expected in new[]
        {
            "eng/release/targets.json",
            "eng/release/dependencies.json",
            "eng/release/managed-license-evidence.json",
            "eng/release/NuGet.config",
            "eng/release/vendor-packages.json",
            "eng/release/build-tools.json",
            "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj",
            "scripts/prepare-release-public-assets.ps1",
            "scripts/resolve-release-tools.ps1",
            "scripts/run-release-quality-gate.ps1",
            "regenerationScript",
            "scripts/acquire-llvm-toolchain.ps1",
            "scripts/llvm-source-build.ps1",
            "scripts/assemble-sdk-manifest.ps1",
            "scripts/get-release-configuration-identity.ps1",
            "scripts/prepare-vendor-release-input.ps1",
            "scripts/package-release.ps1",
            "scripts/generate-release-docs.ps1",
            "scripts/release-documentation-contract.ps1",
            "scripts/stage-release-installers.ps1",
            "scripts/audit-release-native-dependencies.ps1",
            "scripts/smoke-release-archive.ps1",
            "scripts/smoke-release-install.ps1",
            "scripts/audit-public-repository.ps1",
            "scripts/check-book-structure.sh",
            "scripts/check-book-samples.sh",
            "tests/release-installers/test-installers.sh",
            "ReleaseInstallerContractTests",
        })
        {
            Assert.Contains(expected, releaseContract.Replace('\\', '/'), StringComparison.Ordinal);
        }

        Assert.Contains("configurationDigest", script, StringComparison.Ordinal);
        Assert.Contains("outputIdentitySha256", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"-BuildIdentity\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-BuildConfigurationSha256\", $configurationDigest", script, StringComparison.Ordinal);
        Assert.Contains("\"-BuildPlanSha256\", $basePlanSha256", script, StringComparison.Ordinal);
        Assert.Contains("\"--restore-only\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$configurationPaths =", script, StringComparison.Ordinal);
        Assert.Contains("src/compiler.csproj", script.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("explicit -CMakePath and -NinjaPath", script, StringComparison.Ordinal);
        Assert.Contains("$requiresLlvmSourceBuildTools", script, StringComparison.Ordinal);
        Assert.Contains("$selectedPhases -contains \"Acquire\"", script, StringComparison.Ordinal);
        Assert.Contains("Release execution requires a clean checkout", script, StringComparison.Ordinal);
        Assert.Contains("publicationAction = $false", script, StringComparison.Ordinal);
        Assert.Contains("[string] $ReleaseToolsPath", script, StringComparison.Ordinal);
        Assert.Contains("releaseToolsAssembly", script, StringComparison.Ordinal);
        var releaseToolsResolver = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "resolve-release-tools.ps1"));
        Assert.Contains("[Console]::Error.WriteLine([string]$line)", releaseToolsResolver, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $line", releaseToolsResolver, StringComparison.Ordinal);
        Assert.DoesNotContain("python", releaseContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linux-x64\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-x64\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("macos-arm64\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanPhaseEmitsTheFullCommandGraphWithoutExecuting()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var testRoot = Path.Combine(repositoryRoot, "artifacts", "local-release-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var document = await InvokePlanAsync(
                powershell,
                repositoryRoot,
                commit,
                Path.Combine(testRoot, "cache"),
                Path.Combine(testRoot, "output"),
                ["Plan"]);

            Assert.False(document.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.True(document.RootElement.GetProperty("planOnly").GetBoolean());
            Assert.False(document.RootElement.GetProperty("willExecute").GetBoolean());
            Assert.Equal(
                ["Quality", "Acquire", "Build", "Package", "Validate", "Smoke"],
                document.RootElement.GetProperty("phases").EnumerateArray().Select(static value => value.GetString()));
            Assert.NotEmpty(document.RootElement.GetProperty("commands").EnumerateArray());
            Assert.False(Directory.Exists(testRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DryRunPlansEveryPhaseWithoutCreatingCacheOrOutputRoots()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var testRoot = Path.Combine(repositoryRoot, "artifacts", "local-release-tests", Guid.NewGuid().ToString("N"));
        var cacheBase = Path.Combine(testRoot, "cache");
        var outputBase = Path.Combine(testRoot, "output");
        try
        {
            using var document = await InvokePlanAsync(
                powershell,
                repositoryRoot,
                commit,
                cacheBase,
                outputBase,
                ["All", "-DryRun"]);
            var root = document.RootElement;

            Assert.Equal("stark-local-release", root.GetProperty("planKind").GetString());
            Assert.True(root.GetProperty("dryRun").GetBoolean());
            Assert.False(root.GetProperty("publishingCandidate").GetBoolean());
            Assert.False(root.GetProperty("publicationAction").GetBoolean());
            Assert.Equal(["macos-arm64"], root.GetProperty("targets").EnumerateArray().Select(static value => value.GetString()));
            Assert.Equal(
                ["Quality", "Acquire", "Build", "Package", "Validate", "Smoke"],
                root.GetProperty("phases").EnumerateArray().Select(static value => value.GetString()));
            var externalBuildTools = root.GetProperty("configuration").GetProperty("externalBuildTools").EnumerateArray().ToArray();
            Assert.Equal(["cmake", "ninja"], externalBuildTools.Select(static tool => tool.GetProperty("name").GetString()));
            Assert.All(externalBuildTools, static tool => Assert.Matches("^[0-9a-f]{64}$", tool.GetProperty("sha256").GetString()!));

            var commands = root.GetProperty("commands").EnumerateArray().ToArray();
            var qualityCommand = Assert.Single(commands, static command => command.GetProperty("label").GetString() == "Run mandatory repository quality gate");
            Assert.Equal("Quality", qualityCommand.GetProperty("phase").GetString());
            Assert.Equal("repository", qualityCommand.GetProperty("targetId").GetString());
            Assert.Equal("Run mandatory repository quality gate", commands[0].GetProperty("label").GetString());
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Acquire compiler-private LLVM backend");
            var acquireCommand = Assert.Single(commands, static command => command.GetProperty("label").GetString() == "Acquire compiler-private LLVM backend");
            var acquireArguments = acquireCommand.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).ToArray();
            Assert.Contains("-CMakePath", acquireArguments);
            Assert.Contains("-NinjaPath", acquireArguments);
            var restoreCommand = Assert.Single(commands, static command => command.GetProperty("label").GetString() == "Restore exact managed dependency graph");
            var restoreArguments = restoreCommand.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).ToArray();
            Assert.Contains("--locked-mode", restoreArguments);
            Assert.Contains("-p:DisableImplicitLibraryPacksFolder=true", restoreArguments);
            Assert.Contains("-p:DisableTransitiveFrameworkReferenceDownloads=true", restoreArguments);
            Assert.Contains(
                restoreCommand.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()!.Replace('\\', '/')),
                static value => value.EndsWith("/src/packages.osx-arm64.lock.json", StringComparison.Ordinal));
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Validate exact managed dependency graph");
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Prepare official Vendor package images");
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Package release archive");
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Validate staged SDK completeness");
            Assert.Contains(commands, static command => command.GetProperty("label").GetString() == "Qualify archive-local installer lifecycle");
            var vendorCommand = Assert.Single(commands, static command => command.GetProperty("label").GetString() == "Prepare official Vendor package images");
            var vendorArguments = vendorCommand.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).ToArray();
            Assert.Contains("-CMakePath", vendorArguments);
            Assert.Contains("-NinjaPath", vendorArguments);
            var packageCommand = Assert.Single(commands, static command => command.GetProperty("label").GetString() == "Package release archive");
            var packageArguments = packageCommand.GetProperty("arguments").EnumerateArray().Select(static value => value.GetString()).ToArray();
            var buildConfigurationIndex = Array.IndexOf(packageArguments, "-BuildConfigurationSha256");
            var buildPlanIndex = Array.IndexOf(packageArguments, "-BuildPlanSha256");
            Assert.InRange(buildConfigurationIndex, 0, packageArguments.Length - 2);
            Assert.InRange(buildPlanIndex, 0, packageArguments.Length - 2);
            Assert.Matches("^[0-9a-f]{64}$", packageArguments[buildConfigurationIndex + 1]!);
            Assert.Matches("^[0-9a-f]{64}$", packageArguments[buildPlanIndex + 1]!);

            var roots = root.GetProperty("roots");
            Assert.Matches("^[0-9a-f]{64}$", roots.GetProperty("outputIdentitySha256").GetString()!);
            Assert.Equal(root.GetProperty("configuration").GetProperty("sha256").GetString(), packageArguments[buildConfigurationIndex + 1]);
            Assert.Equal(root.GetProperty("workflowSemantics").GetProperty("releasePlanSha256").GetString(), packageArguments[buildPlanIndex + 1]);
            Assert.Equal("stark-release-configuration", root.GetProperty("configuration").GetProperty("identityKind").GetString());
            Assert.Equal("sha256-ordinal-path-size-content-v1", root.GetProperty("configuration").GetProperty("algorithm").GetString());
            Assert.Equal("10.0.302", root.GetProperty("workflowSemantics").GetProperty("requiredDotnetVersion").GetString());
            Assert.Equal("10.0.10", root.GetProperty("workflowSemantics").GetProperty("requiredDotnetRuntimeVersion").GetString());
            var releaseTools = root.GetProperty("releaseTools");
            Assert.Equal("Stark.ReleaseTools", releaseTools.GetProperty("implementation").GetString());
            Assert.Equal("net10.0", releaseTools.GetProperty("targetFramework").GetString());
            Assert.Equal("10.0.302", releaseTools.GetProperty("dotnetSdkVersion").GetString());
            Assert.Equal("10.0.10", releaseTools.GetProperty("dotnetRuntimeVersion").GetString());
            Assert.Matches("^[0-9a-f]{64}$", releaseTools.GetProperty("assemblySha256").GetString()!);
            Assert.Matches("^[0-9a-f]{64}$", releaseTools.GetProperty("projectSha256").GetString()!);
            var releaseToolsPathIndex = Array.IndexOf(packageArguments, "-ReleaseToolsPath");
            Assert.InRange(releaseToolsPathIndex, 0, packageArguments.Length - 2);
            Assert.Equal(releaseTools.GetProperty("assembly").GetString(), packageArguments[releaseToolsPathIndex + 1]);
            Assert.Matches("^[0-9a-f]{64}$", roots.GetProperty("outputIdentitySha256").GetString()!);
            Assert.Equal(
                root.GetProperty("configuration").GetProperty("sha256").GetString(),
                Path.GetFileName(roots.GetProperty("cache").GetString()));
            Assert.Equal(
                roots.GetProperty("outputIdentitySha256").GetString(),
                Path.GetFileName(roots.GetProperty("output").GetString()));
            var cacheRoot = roots.GetProperty("cache").GetString()!;
            var dotnetCommands = commands.Where(static command => Path.GetFileNameWithoutExtension(command.GetProperty("executable").GetString()) == "dotnet").ToArray();
            Assert.NotEmpty(dotnetCommands);
            Assert.All(dotnetCommands, command =>
            {
                var environment = command.GetProperty("environment");
                Assert.Equal(Path.Combine(cacheRoot, "nuget-packages"), environment.GetProperty("NUGET_PACKAGES").GetString());
                Assert.Equal(Path.Combine(cacheRoot, "nuget-http"), environment.GetProperty("NUGET_HTTP_CACHE_PATH").GetString());
            });
            Assert.False(Directory.Exists(cacheBase));
            Assert.False(Directory.Exists(outputBase));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SeparatePhasesShareChecksumAddressedRoots()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var testRoot = Path.Combine(repositoryRoot, "artifacts", "local-release-tests", Guid.NewGuid().ToString("N"));
        var cacheBase = Path.Combine(testRoot, "cache");
        var outputBase = Path.Combine(testRoot, "output");
        try
        {
            using var acquire = await InvokePlanAsync(powershell, repositoryRoot, commit, cacheBase, outputBase, ["Acquire", "-DryRun"]);
            using var build = await InvokePlanAsync(powershell, repositoryRoot, commit, cacheBase, outputBase, ["Build", "-DryRun"]);

            Assert.Equal(
                acquire.RootElement.GetProperty("roots").GetProperty("cache").GetString(),
                build.RootElement.GetProperty("roots").GetProperty("cache").GetString());
            Assert.Equal(
                acquire.RootElement.GetProperty("roots").GetProperty("output").GetString(),
                build.RootElement.GetProperty("roots").GetProperty("output").GetString());
            Assert.All(acquire.RootElement.GetProperty("commands").EnumerateArray(), static command => Assert.Equal("Acquire", command.GetProperty("phase").GetString()));
            Assert.All(build.RootElement.GetProperty("commands").EnumerateArray(), static command => Assert.Equal("Build", command.GetProperty("phase").GetString()));
            Assert.False(Directory.Exists(testRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PublishingEquivalentPlanRejectsPartialPhases()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var script = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        var result = await RunProcessAsync(
            powershell,
            [
                "-NoProfile", "-NonInteractive", "-File", script,
                "-Version", "v0.0.0-local-test",
                "-Commit", commit,
                "-Ref", commit,
                "-Targets", "all",
                "-Phase", "Package",
                "-DryRun",
                "-PublishingCandidate",
            ],
            repositoryRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("publishing-equivalent local candidate must select the complete", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QualityPhaseIsRepositoryScopedAndDoesNotRequireTargetBuildTools()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var script = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        var result = await RunProcessAsync(
            powershell,
            [
                "-NoProfile", "-NonInteractive", "-File", script,
                "-Version", "v0.0.0-local-test",
                "-Commit", commit,
                "-Ref", commit,
                "-Targets", "all",
                "-Phase", "Quality",
                "-DryRun",
            ],
            repositoryRoot);

        Assert.True(
            result.ExitCode == 0,
            $"Quality dry-run failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(
            ["Quality"],
            document.RootElement.GetProperty("phases").EnumerateArray().Select(static value => value.GetString()));
        var command = Assert.Single(document.RootElement.GetProperty("commands").EnumerateArray());
        Assert.Equal("repository", command.GetProperty("targetId").GetString());
        Assert.Equal("Run mandatory repository quality gate", command.GetProperty("label").GetString());
    }

    [Fact]
    public async Task PublishingEquivalentPlanRejectsPartialTargetSet()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var script = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        var result = await RunProcessAsync(
            powershell,
            [
                "-NoProfile", "-NonInteractive", "-File", script,
                "-Version", "v0.0.0-local-test",
                "-Commit", commit,
                "-Ref", commit,
                "-Targets", "macos-arm64",
                "-Phase", "Plan",
                "-PublishingCandidate",
            ],
            repositoryRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("release-enabled target", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveSdlContributorRequiresExplicitBuildToolPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var commit = (await RunProcessAsync("git", ["rev-parse", "HEAD"], repositoryRoot)).Stdout.Trim();
        var script = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        var result = await RunProcessAsync(
            powershell,
            [
                "-NoProfile", "-NonInteractive", "-File", script,
                "-Version", "v0.0.0-local-test",
                "-Commit", commit,
                "-Ref", commit,
                "-Targets", "macos-arm64",
                "-Phase", "Plan",
            ],
            repositoryRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ambient build-tool", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discovery is forbidden", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDriverParsesWhenPowerShellIsAvailable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        const string parserCommand = """
            & {
                param([string] $ScriptPath)
                $tokens = $null
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) {
                    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
                    exit 1
                }
            }
            """;
        foreach (var relativePath in new[]
        {
            Path.Combine("scripts", "build-release.ps1"),
            Path.Combine("scripts", "run-release-quality-gate.ps1"),
        })
        {
            var script = Path.Combine(repositoryRoot, relativePath);
            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", parserCommand, script],
                repositoryRoot);
            Assert.True(result.ExitCode == 0, $"PowerShell parser rejected {relativePath}.{Environment.NewLine}{result.Stderr}");
        }
    }

    private static async Task<JsonDocument> InvokePlanAsync(
        string powershell,
        string repositoryRoot,
        string commit,
        string cacheBase,
        string outputBase,
        IReadOnlyList<string> phaseArguments)
    {
        var script = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-File", script,
            "-Version", "v0.0.0-local-test",
            "-Commit", commit,
            "-Ref", commit,
            "-Targets", "macos-arm64",
            "-Phase",
        };
        arguments.AddRange(phaseArguments);
        arguments.AddRange(
        [
            "-CacheBase", cacheBase,
            "-OutputBase", outputBase,
            "-CMakePath", Environment.ProcessPath!,
            "-NinjaPath", Environment.ProcessPath!,
        ]);
        var result = await RunProcessAsync(powershell, arguments, repositoryRoot);
        Assert.True(
            result.ExitCode == 0,
            $"Local release dry-run failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        return JsonDocument.Parse(result.Stdout);
    }

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : new[] { "pwsh" })
        {
            try
            {
                var result = await RunProcessAsync(candidate, ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"], workingDirectory);
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
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
