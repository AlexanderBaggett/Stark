using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseVendorPreparationScriptTests
{
    [Fact]
    public void UnifiedVendorEntryPointOwnsAggregationAndTransactionalReplacement()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "prepare-vendor-release-input.ps1"));

        Assert.Contains("[ValidateSet(\"linux-x64\", \"windows-x64\", \"macos-arm64\")]", script, StringComparison.Ordinal);
        Assert.Contains("[string] $OutputVendorRoot", script, StringComparison.Ordinal);
        Assert.Contains("[string] $StdlibPackageDir", script, StringComparison.Ordinal);
        Assert.Contains("[string] $ToolchainDir", script, StringComparison.Ordinal);
        Assert.Contains("[string] $CMakePath", script, StringComparison.Ordinal);
        Assert.Contains("[string] $NinjaPath", script, StringComparison.Ordinal);
        Assert.Contains("prepare-raylib-release-input.ps1", script, StringComparison.Ordinal);
        Assert.Contains("prepare-core-vendor-release-input.ps1", script, StringComparison.Ordinal);
        Assert.Contains("prepare-glfw-vendor-release-input.ps1", script, StringComparison.Ordinal);
        Assert.Contains("prepare-sdl3-vendor-release-input.ps1", script, StringComparison.Ordinal);
        Assert.Contains("prepare-sqlite-vendor-release-input.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Vendor.SDL3", script, StringComparison.Ordinal);
        Assert.Contains("CMakePath = $cmakeExecutable", script, StringComparison.Ordinal);
        Assert.Contains("NinjaPath = $ninjaExecutable", script, StringComparison.Ordinal);
        Assert.Contains("Contributor contract (schema 1)", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 2", script, StringComparison.Ordinal);
        Assert.Contains("manifestKind = $manifestKind", script, StringComparison.Ordinal);
        Assert.Contains("state = \"ready\"", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $outputRoot -Destination $backupRoot", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $stageRoot -Destination $outputRoot", script, StringComparison.Ordinal);
        Assert.Contains("release-input.json intentionally excludes itself", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedVendorEntryPointFailsClosedOverTargetInputsAndContributorFiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "prepare-vendor-release-input.ps1"));

        Assert.Contains("Assert-MatchingHost", script, StringComparison.Ordinal);
        Assert.Contains("stark-compiler-private-backend", script, StringComparison.Ordinal);
        Assert.Contains(".stark-llvm-toolchain-owner.json", script, StringComparison.Ordinal);
        Assert.Contains("Private compiler backend runtime closure counts", script, StringComparison.Ordinal);
        Assert.Contains("missing or untracked files relative to runtimeClosure.files", script, StringComparison.Ordinal);
        Assert.Contains("$file = Get-Item -LiteralPath $absolutePath -Force", script, StringComparison.Ordinal);
        Assert.Contains("Where-Object { $_ -ne \"manifest.json\" }", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Where-Object { $_ -ne \"manifest.json\" -and $_ -ne \".stark-llvm-toolchain-owner.json\" }",
            script,
            StringComparison.Ordinal);
        Assert.Contains("inspect-pkg $stdlibImages[0].FullName", script, StringComparison.Ordinal);
        Assert.Contains("package image/library file names", script, StringComparison.Ordinal);
        Assert.Contains("modules do not exactly match the official catalog", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PortableArtifact", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("catalog/vendor-packages.json", script, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", script, StringComparison.Ordinal);
        Assert.Contains("expected initial release slice", script, StringComparison.Ordinal);
        Assert.Contains("SortedDictionary[string, object]", script, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::Ordinal", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object path", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibCanContributeWithoutReplacingTheSharedVendorRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "prepare-raylib-release-input.ps1"));

        Assert.Contains("[switch] $ContributorMode", script, StringComparison.Ordinal);
        Assert.Contains("[string] $ContributionManifestPath", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $ContributorMode)", script, StringComparison.Ordinal);
        Assert.Contains("The unified orchestrator owns release-input.json", script, StringComparison.Ordinal);
        Assert.Contains("packages = @($packageEntries)", script, StringComparison.Ordinal);
        Assert.Contains("Contributed pinned Raylib", script, StringComparison.Ordinal);
        Assert.Contains("catalog/vendor-packages.json", script, StringComparison.Ordinal);
        Assert.Contains("$licenseFiles += [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("Get-OrdinalSortedObjects -Values $nativeArtifacts", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object path", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (Test-Path -LiteralPath $outputRoot) {\n    Remove-Item -LiteralPath $outputRoot -Recurse -Force\n}",
            script.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RaylibUpstreamFamilyIsPackagedAsThreeOwnershipCorrectImages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "prepare-raylib-release-input.ps1"));
        var rootSource = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "src", "Vendor", "Raylib.stark"));
        using var catalog = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));

        var packages = catalog.RootElement.GetProperty("packages")
            .EnumerateArray()
            .ToDictionary(static package => package.GetProperty("id").GetString()!, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "Vendor.Raylib",
                "Vendor.Raylib.Audio",
                "Vendor.Raylib.Core",
                "Vendor.Raylib.Models",
                "Vendor.Raylib.Owners",
                "Vendor.Raylib.Shapes",
                "Vendor.Raylib.Text",
                "Vendor.Raylib.Textures",
                "Vendor.Raylib.Types",
            },
            packages["Vendor.Raylib"].GetProperty("modules").EnumerateArray().Select(static value => value.GetString()!).OrderBy(static value => value, StringComparer.Ordinal));
        Assert.Equal(["Vendor.Raymath"], packages["Vendor.Raymath"].GetProperty("modules").EnumerateArray().Select(static value => value.GetString()!));
        Assert.Equal(["Vendor.Rlgl"], packages["Vendor.Rlgl"].GetProperty("modules").EnumerateArray().Select(static value => value.GetString()!));
        Assert.Equal(["Vendor.Raylib"], packages["Vendor.Raymath"].GetProperty("dependencies").EnumerateArray().Select(static value => value.GetString()!));
        Assert.Equal(["Vendor.Raylib"], packages["Vendor.Rlgl"].GetProperty("dependencies").EnumerateArray().Select(static value => value.GetString()!));
        Assert.Equal("Vendor.Raylib", packages["Vendor.Raylib"].GetProperty("nativePayloadOwner").GetString());
        Assert.Equal("Vendor.Raylib", packages["Vendor.Raymath"].GetProperty("nativePayloadOwner").GetString());
        Assert.Equal("Vendor.Raylib", packages["Vendor.Rlgl"].GetProperty("nativePayloadOwner").GetString());

        Assert.Contains("Vendor.Raymath", script, StringComparison.Ordinal);
        Assert.Contains("Vendor.Rlgl", script, StringComparison.Ordinal);
        Assert.Contains("\"-I\", $targetDist", script, StringComparison.Ordinal);
        Assert.Contains("do not exactly match direct imported package identities", script, StringComparison.Ordinal);
        Assert.Contains("\"-I\", (Join-Path $outputRoot \"src\")", script, StringComparison.Ordinal);
        Assert.Contains("artifacts = @()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("import Vendor.Raymath", rootSource, StringComparison.Ordinal);
        Assert.DoesNotContain("import Vendor.Rlgl", rootSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VendorPreparationScriptsParseWhenPowerShellIsAvailable()
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
            "scripts/prepare-vendor-release-input.ps1",
            "scripts/prepare-raylib-release-input.ps1",
            "scripts/prepare-core-vendor-release-input.ps1",
            "scripts/prepare-glfw-vendor-release-input.ps1",
            "scripts/prepare-sdl3-vendor-release-input.ps1",
            "scripts/prepare-sqlite-vendor-release-input.ps1",
        })
        {
            var path = Path.Combine(repositoryRoot, relativePath);
            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", parserCommand, path],
                repositoryRoot);
            Assert.True(result.ExitCode == 0, $"PowerShell parser rejected '{relativePath}'.{Environment.NewLine}{result.Stderr}");
        }
    }

    [Fact]
    public async Task SchemaNormalizerAcceptsAnExecutableCoreContributorFixture()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        // macOS exposes /var as a symlink to /private/var. The production
        // release-input validator intentionally rejects reparse-point paths,
        // so keep this executable fixture under the repository artifact root.
        var fixtureRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "vendor-contribution-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var relativeFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dist/macos-arm64/libVendorCgltf.a"] = "archive fixture",
                ["dist/macos-arm64/libVendorCgltf.starkpkg"] = "package fixture",
                ["dist/macos-arm64/native/cgltf/cgltf.h"] = "header fixture",
                ["dist/macos-arm64/native/cgltf/LICENSE.cgltf.h"] = "license fixture",
                ["dist/macos-arm64/native/cgltf/PROVENANCE.json"] = "{}\n",
            };
            foreach (var (relativePath, contents) in relativeFiles)
            {
                var path = Path.Combine(fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, contents);
            }

            object Descriptor(string path) => new
            {
                path,
                bytes = new FileInfo(Path.Combine(fixtureRoot, path.Replace('/', Path.DirectorySeparatorChar))).Length,
                sha256 = Sha256(Path.Combine(fixtureRoot, path.Replace('/', Path.DirectorySeparatorChar))),
            };
            object Artifact(string kind, string path) => new
            {
                kind,
                path,
                bytes = new FileInfo(Path.Combine(fixtureRoot, path.Replace('/', Path.DirectorySeparatorChar))).Length,
                sha256 = Sha256(Path.Combine(fixtureRoot, path.Replace('/', Path.DirectorySeparatorChar))),
            };

            var contribution = new
            {
                schemaVersion = 1,
                targetId = "macos-arm64",
                targetTriple = "arm64-apple-macosx11.0.0",
                packages = new[]
                {
                    new
                    {
                        id = "Vendor.Cgltf",
                        version = "1.15",
                        sourceIdentity = "commit:360db1a95480fe102ae9c69b27c5d101167ff5ba",
                        target = new { id = "macos-arm64", targetTriple = "arm64-apple-macosx11.0.0" },
                        package = new
                        {
                            rootModule = "Vendor.Cgltf",
                            image = "dist/macos-arm64/libVendorCgltf.starkpkg",
                            imageSha256 = Sha256(Path.Combine(fixtureRoot, "dist/macos-arm64/libVendorCgltf.starkpkg")),
                            library = "dist/macos-arm64/libVendorCgltf.a",
                            librarySha256 = Sha256(Path.Combine(fixtureRoot, "dist/macos-arm64/libVendorCgltf.a")),
                            modules = new[] { "Vendor.Cgltf" },
                        },
                        nativePayload = new
                        {
                            artifacts = new[]
                            {
                                Artifact("header", "dist/macos-arm64/native/cgltf/cgltf.h"),
                                Artifact("license", "dist/macos-arm64/native/cgltf/LICENSE.cgltf.h"),
                                Artifact("provenance", "dist/macos-arm64/native/cgltf/PROVENANCE.json"),
                            },
                            licenseFiles = new[] { Descriptor("dist/macos-arm64/native/cgltf/LICENSE.cgltf.h") },
                        },
                        provenance = Descriptor("dist/macos-arm64/native/cgltf/PROVENANCE.json"),
                    },
                },
            };
            var manifestPath = Path.Combine(fixtureRoot, "core-contribution.json");
            var normalizedPath = Path.Combine(fixtureRoot, "normalized-contribution.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(contribution));

            var script = Path.Combine(repositoryRoot, "scripts", "prepare-vendor-release-input.ps1");
            var result = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File", script,
                    "-AssetSuffix", "macos-arm64",
                    "-TargetTriple", "arm64-apple-macosx11.0.0",
                    "-OutputVendorRoot", "unused",
                    "-StdlibPackageDir", "unused",
                    "-ToolchainDir", "unused",
                    "-CMakePath", "unused",
                    "-NinjaPath", "unused",
                    "-ValidateContributionManifest", manifestPath,
                    "-ValidationRoot", fixtureRoot,
                    "-NormalizedContributionOutput", normalizedPath,
                ],
                repositoryRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Executable Vendor contribution normalization failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("Validated 1 Vendor contribution package(s).", result.Stdout, StringComparison.Ordinal);

            using var normalized = JsonDocument.Parse(await File.ReadAllTextAsync(normalizedPath));
            var artifactPaths = normalized.RootElement.GetProperty("packages")[0]
                .GetProperty("nativePayload")
                .GetProperty("artifacts")
                .EnumerateArray()
                .Select(static value => value.GetProperty("path").GetString())
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "dist/macos-arm64/native/cgltf/LICENSE.cgltf.h",
                    "dist/macos-arm64/native/cgltf/PROVENANCE.json",
                    "dist/macos-arm64/native/cgltf/cgltf.h",
                },
                artifactPaths);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
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

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
