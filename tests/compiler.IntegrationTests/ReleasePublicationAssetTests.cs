using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stark.ReleaseTools;

namespace compiler.IntegrationTests;

public sealed class ReleasePublicationAssetTests
{
    private const string ExpectedVersion = "v0.1.0";
    private const string ExpectedSourceCommit = "1111111111111111111111111111111111111111";
    private const string ExpectedConfigurationSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ExpectedPlanSha256 = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public async Task PublicationAssetsAreCompleteValidatedAndExactlyChecksummed()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-public-assets-");
        try
        {
            var inputRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "downloaded"));
            var outputRoot = Path.Combine(testRoot.FullName, "public");
            CreateValidTargetAssets(inputRoot.FullName);

            var result = await RunScriptAsync(powershell, repositoryRoot, inputRoot.FullName, outputRoot);
            Assert.True(
                result.ExitCode == 0,
                $"Public asset preparation failed.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");

            var actualNames = Directory.EnumerateFiles(outputRoot)
                .Select(static path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                [
                    "SHA256SUMS.txt",
                    "managed-dependencies-macos-arm64.json",
                    "native-dependencies-macos-arm64.json",
                    "stage-validation-macos-arm64.json",
                    "stark-v0.1.0-macos-arm64.tar.gz",
                    "stark-v0.1.0-macos-arm64.tar.gz.sha256",
                ],
                actualNames);

            var manifestPath = Path.Combine(outputRoot, "SHA256SUMS.txt");
            var manifestLines = File.ReadAllLines(manifestPath);
            Assert.Equal(actualNames.Length - 1, manifestLines.Length);
            Assert.Equal(
                actualNames.Where(static name => name != "SHA256SUMS.txt"),
                manifestLines.Select(static line => line[66..]));
            foreach (var line in manifestLines)
            {
                Assert.Matches("^[0-9a-f]{64}  [^/\\\\]+$", line);
                var expectedHash = line[..64];
                var name = line[66..];
                Assert.Equal(expectedHash, Sha256(Path.Combine(outputRoot, name)));
            }

            Assert.False(File.Exists(Path.Combine(outputRoot, "native-dependencies-macos-arm64.log")));
            Assert.False(File.Exists(Path.Combine(outputRoot, "stage-validation-macos-arm64.log")));
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("tampered-checksum")]
    [InlineData("unknown-file")]
    [InlineData("failed-evidence")]
    [InlineData("absolute-evidence-path")]
    [InlineData("wrong-sdk-root")]
    [InlineData("stale-archive-binding")]
    [InlineData("forged-report-binding")]
    [InlineData("coordinated-forged-binding")]
    [InlineData("wrong-expected-version")]
    [InlineData("wrong-expected-source")]
    [InlineData("wrong-expected-configuration")]
    [InlineData("wrong-expected-plan")]
    [InlineData("wrong-expected-targets")]
    [InlineData("missing-expected-target")]
    public async Task PublicationFailsClosedForInvalidOrUnreviewedAssets(string fault)
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-public-assets-reject-");
        try
        {
            var inputRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "downloaded"));
            var outputRoot = Path.Combine(testRoot.FullName, "public");
            CreateValidTargetAssets(inputRoot.FullName);
            var expectedVersion = ExpectedVersion;
            var expectedSourceCommit = ExpectedSourceCommit;
            var expectedConfigurationSha256 = ExpectedConfigurationSha256;
            var expectedPlanSha256 = ExpectedPlanSha256;
            var expectedTargets = "macos-arm64";
            switch (fault)
            {
                case "tampered-checksum":
                    File.WriteAllText(
                        Path.Combine(inputRoot.FullName, "release", "stark-v0.1.0-macos-arm64.tar.gz.sha256"),
                        $"{new string('0', 64)}  stark-v0.1.0-macos-arm64.tar.gz\n",
                        Encoding.ASCII);
                    break;
                case "unknown-file":
                    File.WriteAllText(Path.Combine(inputRoot.FullName, "unreviewed.txt"), "unexpected\n");
                    break;
                case "failed-evidence":
                    File.WriteAllText(
                        Path.Combine(inputRoot.FullName, "release", "stage-validation-macos-arm64.json"),
                        "{\"schemaVersion\":1,\"status\":\"violations\",\"targetId\":\"macos-arm64\"}\n");
                    break;
                case "absolute-evidence-path":
                    File.WriteAllText(
                        Path.Combine(inputRoot.FullName, "managed-dependencies-macos-arm64.json"),
                        "{\"schemaVersion\":1,\"status\":\"ready\",\"runtimeIdentifier\":\"osx-arm64\",\"nugetConfig\":\"/runner/work/NuGet.config\",\"lockFile\":\"src/packages.osx-arm64.lock.json\"}\n");
                    break;
                case "wrong-sdk-root":
                    File.WriteAllText(
                        Path.Combine(inputRoot.FullName, "release", "native-dependencies-macos-arm64.json"),
                        "{\"schemaVersion\":1,\"status\":\"ok\",\"assetSuffix\":\"macos-arm64\",\"sdkRoot\":\"/runner/work/stark-sdk\"}\n");
                    break;
                case "stale-archive-binding":
                {
                    var archivePath = Path.Combine(
                        inputRoot.FullName,
                        "release",
                        "stark-v0.1.0-macos-arm64.tar.gz");
                    var archiveBytes = File.ReadAllBytes(archivePath);
                    archiveBytes[4] ^= 1;
                    File.WriteAllBytes(archivePath, archiveBytes);
                    File.WriteAllText(
                        archivePath + ".sha256",
                        $"{Sha256(archivePath)}  {Path.GetFileName(archivePath)}\n",
                        Encoding.ASCII);
                    break;
                }
                case "forged-report-binding":
                    RewriteCandidateBinding(
                        Path.Combine(inputRoot.FullName, "managed-dependencies-macos-arm64.json"),
                        new string('f', 40));
                    break;
                case "coordinated-forged-binding":
                    foreach (var reportPath in new[]
                    {
                        Path.Combine(inputRoot.FullName, "managed-dependencies-macos-arm64.json"),
                        Path.Combine(inputRoot.FullName, "release", "native-dependencies-macos-arm64.json"),
                        Path.Combine(inputRoot.FullName, "release", "stage-validation-macos-arm64.json"),
                    })
                    {
                        RewriteCandidateBinding(reportPath, new string('f', 40));
                    }
                    break;
                case "wrong-expected-version":
                    expectedVersion = "v0.1.1";
                    break;
                case "wrong-expected-source":
                    expectedSourceCommit = new string('e', 40);
                    break;
                case "wrong-expected-configuration":
                    expectedConfigurationSha256 = new string('e', 64);
                    break;
                case "wrong-expected-plan":
                    expectedPlanSha256 = new string('e', 64);
                    break;
                case "wrong-expected-targets":
                    expectedTargets = "linux-x64";
                    break;
                case "missing-expected-target":
                    expectedTargets = "macos-arm64,linux-x64";
                    break;
                default:
                    throw new InvalidOperationException($"Unknown test fault '{fault}'.");
            }

            var result = await RunScriptAsync(
                powershell,
                repositoryRoot,
                inputRoot.FullName,
                outputRoot,
                expectedTargets,
                expectedVersion,
                expectedSourceCommit,
                expectedConfigurationSha256,
                expectedPlanSha256);
            Assert.NotEqual(0, result.ExitCode);
            if (fault is "stale-archive-binding" or "forged-report-binding" or "coordinated-forged-binding")
            {
                Assert.Contains("candidateBinding", result.Stderr, StringComparison.Ordinal);
            }
            Assert.Empty(Directory.EnumerateFiles(outputRoot));
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("linux-x64", "linux-x64", "tar.gz")]
    [InlineData("linux-arm64", "linux-arm64", "tar.gz")]
    [InlineData("macos-x64", "osx-x64", "tar.gz")]
    [InlineData("macos-arm64", "osx-arm64", "tar.gz")]
    [InlineData("windows-x64", "win-x64", "zip")]
    [InlineData("windows-arm64", "win-arm64", "zip")]
    public async Task PublicationAcceptsEverySupported64BitTargetIdentity(
        string target,
        string runtimeIdentifier,
        string extension)
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Directory.CreateTempSubdirectory("stark-public-assets-target-");
        try
        {
            var inputRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "downloaded"));
            var outputRoot = Path.Combine(testRoot.FullName, "public");
            CreateTargetAssets(inputRoot.FullName, target, runtimeIdentifier, extension);

            var result = await RunScriptAsync(
                powershell,
                repositoryRoot,
                inputRoot.FullName,
                outputRoot,
                target);
            Assert.True(
                result.ExitCode == 0,
                $"Public asset preparation failed for {target}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(File.Exists(Path.Combine(outputRoot, $"stark-v0.1.0-{target}.{extension}")));
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void CandidateEvidenceBinderBindsTheExactArchiveAndStagedSdk()
    {
        var testRoot = Directory.CreateTempSubdirectory("stark-candidate-binding-");
        try
        {
            var inputRoot = Directory.CreateDirectory(Path.Combine(testRoot.FullName, "downloaded"));
            CreateValidTargetAssets(inputRoot.FullName);
            var releaseRoot = Path.Combine(inputRoot.FullName, "release");
            var archivePath = Path.Combine(releaseRoot, "stark-v0.1.0-macos-arm64.tar.gz");
            var extractionRoot = Path.Combine(testRoot.FullName, "extracted");
            ArchiveExtractor.Extract(CommandLine.Parse([
                "extract-archive", "--archive", archivePath, "--kind", "targz", "--destination", extractionRoot,
                "--required-root", "stark-v0.1.0-macos-arm64", "--label", "candidate archive"]));
            var sdkRoot = Path.Combine(extractionRoot, "stark-v0.1.0-macos-arm64");
            var reports = new[]
            {
                Path.Combine(inputRoot.FullName, "managed-dependencies-macos-arm64.json"),
                Path.Combine(releaseRoot, "native-dependencies-macos-arm64.json"),
                Path.Combine(releaseRoot, "stage-validation-macos-arm64.json"),
            };
            foreach (var report in reports)
            {
                var document = JsonNode.Parse(File.ReadAllText(report))!.AsObject();
                document.Remove("candidateBinding");
                File.WriteAllText(report, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            }

            var binding = CandidateEvidenceBinder.Bind(archivePath, sdkRoot, reports[0], reports[1], reports[2]);

            Assert.Equal(ExpectedSourceCommit, binding["sourceCommit"]!.GetValue<string>());
            foreach (var report in reports)
            {
                var rebound = JsonNode.Parse(File.ReadAllText(report))!.AsObject();
                Assert.True(JsonNode.DeepEquals(binding, rebound["candidateBinding"]));
            }
        }
        finally
        {
            testRoot.Delete(recursive: true);
        }
    }

    private static void CreateValidTargetAssets(string inputRoot) =>
        CreateTargetAssets(inputRoot, "macos-arm64", "osx-arm64", "tar.gz");

    private static void CreateTargetAssets(
        string inputRoot,
        string target,
        string runtimeIdentifier,
        string extension)
    {
        var releaseRoot = Directory.CreateDirectory(Path.Combine(inputRoot, "release"));
        var archiveName = $"stark-v0.1.0-{target}.{extension}";
        var archivePath = Path.Combine(releaseRoot.FullName, archiveName);
        var targetTriples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-x64"] = "x86_64-unknown-linux-gnu",
            ["linux-arm64"] = "aarch64-unknown-linux-gnu",
            ["macos-x64"] = "x86_64-apple-macosx11.0.0",
            ["macos-arm64"] = "arm64-apple-macosx11.0.0",
            ["windows-x64"] = "x86_64-pc-windows-msvc",
            ["windows-arm64"] = "aarch64-pc-windows-msvc",
        };
        const string sourceCommit = "1111111111111111111111111111111111111111";
        const string configurationSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
        const string planSha256 = "3333333333333333333333333333333333333333333333333333333333333333";
        const string archiveManifestSha256 = "5555555555555555555555555555555555555555555555555555555555555555";
        const string releaseToolsAssemblySha256 = "9999999999999999999999999999999999999999999999999999999999999999";
        var archiveTool = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["implementation"] = "Stark.ReleaseTools",
            ["manifest"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj",
                ["schemaVersion"] = 1,
                ["sha256"] = archiveManifestSha256,
            },
            ["targetFramework"] = "net10.0",
            ["dotnetSdkVersion"] = "10.0.302",
            ["dotnetRuntimeVersion"] = "10.0.10",
            ["assembly"] = new Dictionary<string, object?>
            {
                ["bytes"] = 12345,
                ["sha256"] = releaseToolsAssemblySha256,
            },
        };
        var identityText = string.Join('\n',
            "stark-content-addressed-release-build-v2",
            $"commit={sourceCommit}",
            "releaseVersion=v0.1.0",
            $"targetId={target}",
            $"configurationSha256={configurationSha256}",
            $"releasePlanSha256={planSha256}",
            $"releaseToolManifestSha256={archiveManifestSha256}",
            "releaseToolImplementation=Stark.ReleaseTools",
            "releaseToolTargetFramework=net10.0",
            "dotnetSdkVersion=10.0.302",
            "dotnetRuntimeVersion=10.0.10",
            $"releaseToolAssemblySha256={releaseToolsAssemblySha256}") + "\n";
        var buildIdentity = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["configurationSha256"] = configurationSha256,
            ["identity"] = $"sha256:{Sha256(Encoding.UTF8.GetBytes(identityText))}",
            ["identityFacts"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["archiveTool"] = archiveTool,
                ["commit"] = sourceCommit,
                ["configurationSha256"] = configurationSha256,
                ["releasePlanSha256"] = planSha256,
                ["releaseVersion"] = "v0.1.0",
                ["schemaVersion"] = 1,
                ["targetId"] = target,
            },
            ["kind"] = "content-addressed-release-build",
            ["releasePlanSha256"] = planSha256,
        };
        var archiveKind = extension == "zip" ? "zip" : "targz";
        var releaseDocument = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["archiveKind"] = archiveKind,
            ["assetSuffix"] = target,
            ["buildIdentity"] = buildIdentity,
            ["buildOptions"] = new Dictionary<string, object?> { ["archiveContainerTool"] = archiveTool },
            ["configuration"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "sha256-ordinal-path-size-content-v1",
                ["identityKind"] = "stark-release-configuration",
                ["sha256"] = configurationSha256,
            },
            ["defaultTargetTriple"] = targetTriples[target],
            ["gitCommit"] = sourceCommit,
            ["releaseVersion"] = "v0.1.0",
            ["runtimeIdentifier"] = runtimeIdentifier,
            ["schemaVersion"] = 2,
            ["source"] = new Dictionary<string, object?> { ["commit"] = sourceCommit },
            ["starkVersion"] = "v0.1.0",
            ["targetId"] = target,
            ["workflowIdentity"] = buildIdentity,
        };
        var releaseJson = JsonSerializer.SerializeToUtf8Bytes(
            releaseDocument,
            new JsonSerializerOptions { WriteIndented = true });
        var releaseFiles = Encoding.ASCII.GetBytes($"{Sha256(releaseJson)}  release.json\n");
        WriteCandidateArchive(archivePath, extension, $"stark-v0.1.0-{target}", releaseJson, releaseFiles);
        File.WriteAllText(
            archivePath + ".sha256",
            $"{Sha256(archivePath)}  {archiveName}\n",
            Encoding.ASCII);
        var binding = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["archive"] = new Dictionary<string, object?>
            {
                ["bytes"] = new FileInfo(archivePath).Length,
                ["name"] = archiveName,
                ["sha256"] = Sha256(archivePath),
            },
            ["configuration"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "sha256-ordinal-path-size-content-v1",
                ["identityKind"] = "stark-release-configuration",
                ["sha256"] = configurationSha256,
            },
            ["kind"] = "stark-release-candidate-binding",
            ["plan"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "sha256",
                ["sha256"] = planSha256,
            },
            ["release"] = new Dictionary<string, object?>
            {
                ["archiveKind"] = archiveKind,
                ["assetSuffix"] = target,
                ["identity"] = new Dictionary<string, object?>
                {
                    ["kind"] = "content-addressed-release-build",
                    ["value"] = buildIdentity["identity"],
                },
                ["runtimeIdentifier"] = runtimeIdentifier,
                ["targetId"] = target,
                ["targetTriple"] = targetTriples[target],
                ["version"] = "v0.1.0",
            },
            ["schemaVersion"] = 1,
            ["sourceCommit"] = sourceCommit,
            ["stagedSdk"] = new Dictionary<string, object?>
            {
                ["releaseFilesSha256"] = Sha256(releaseFiles),
                ["releaseJsonSha256"] = Sha256(releaseJson),
                ["root"] = $"stark-v0.1.0-{target}",
            },
        };
        var validatedCandidate = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "stark-staged-release-validation-subject",
            ["release"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["buildIdentity"] = buildIdentity["identity"],
                ["configurationSha256"] = configurationSha256,
                ["planSha256"] = planSha256,
                ["runtimeIdentifier"] = runtimeIdentifier,
                ["sourceCommit"] = sourceCommit,
                ["targetId"] = target,
                ["version"] = "v0.1.0",
            },
            ["releaseFiles"] = new Dictionary<string, object?>
            {
                ["bytes"] = releaseFiles.Length,
                ["entries"] = 1,
                ["sha256"] = Sha256(releaseFiles),
            },
            ["releaseJson"] = new Dictionary<string, object?>
            {
                ["bytes"] = releaseJson.Length,
                ["sha256"] = Sha256(releaseJson),
            },
            ["root"] = $"stark-v0.1.0-{target}",
            ["schemaVersion"] = 1,
        };
        WriteJson(
            Path.Combine(inputRoot, $"managed-dependencies-{target}.json"),
            new Dictionary<string, object?>
            {
                ["candidateBinding"] = binding,
                ["lockFile"] = $"src/packages.{runtimeIdentifier}.lock.json",
                ["nugetConfig"] = "eng/release/NuGet.config",
                ["runtimeIdentifier"] = runtimeIdentifier,
                ["schemaVersion"] = 1,
                ["status"] = "ready",
                ["targetId"] = target,
                ["validatedCandidate"] = validatedCandidate,
                ["validationScope"] = "release-candidate",
            });
        WriteJson(
            Path.Combine(releaseRoot.FullName, $"native-dependencies-{target}.json"),
            new Dictionary<string, object?>
            {
                ["assetSuffix"] = target,
                ["candidateBinding"] = binding,
                ["schemaVersion"] = 1,
                ["sdkRoot"] = $"stark-v0.1.0-{target}",
                ["status"] = "ok",
                ["validatedCandidate"] = validatedCandidate,
                ["validationScope"] = "release-candidate",
            });
        WriteJson(
            Path.Combine(releaseRoot.FullName, $"stage-validation-{target}.json"),
            new Dictionary<string, object?>
            {
                ["candidateBinding"] = binding,
                ["schemaVersion"] = 1,
                ["sdkRoot"] = $"stark-v0.1.0-{target}",
                ["status"] = "ok",
                ["targetId"] = target,
                ["validatedCandidate"] = validatedCandidate,
                ["validationScope"] = "release-candidate",
            });
        File.WriteAllText(Path.Combine(releaseRoot.FullName, $"native-dependencies-{target}.log"), "internal\n");
        File.WriteAllText(Path.Combine(releaseRoot.FullName, $"stage-validation-{target}.log"), "internal\n");
    }

    private static void WriteCandidateArchive(
        string archivePath,
        string extension,
        string rootName,
        byte[] releaseJson,
        byte[] releaseFiles)
    {
        if (extension == "zip")
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            WriteZipEntry(archive, $"{rootName}/release.json", releaseJson);
            WriteZipEntry(archive, $"{rootName}/release-files.sha256", releaseFiles);
            return;
        }

        using var output = File.Create(archivePath);
        using var compressed = new GZipStream(output, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(compressed, TarEntryFormat.Gnu);
        WriteTarEntry(writer, $"{rootName}/release.json", releaseJson);
        WriteTarEntry(writer, $"{rootName}/release-files.sha256", releaseFiles);
    }

    private static void WriteZipEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var output = entry.Open();
        output.Write(content);
    }

    private static void WriteTarEntry(TarWriter writer, string name, byte[] content)
    {
        using var data = new MemoryStream(content, writable: false);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = data };
        writer.WriteEntry(entry);
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static void RewriteCandidateBinding(string path, string sourceCommit)
    {
        var report = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        report["candidateBinding"]!["sourceCommit"] = sourceCommit;
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunScriptAsync(
        string powershell,
        string repositoryRoot,
        string inputRoot,
        string outputRoot,
        string expectedTargets = "macos-arm64",
        string expectedVersion = ExpectedVersion,
        string expectedSourceCommit = ExpectedSourceCommit,
        string expectedConfigurationSha256 = ExpectedConfigurationSha256,
        string expectedPlanSha256 = ExpectedPlanSha256) =>
        RunProcessAsync(
            powershell,
            [
                "-NoProfile", "-NonInteractive", "-File",
                Path.Combine(repositoryRoot, "scripts", "prepare-release-public-assets.ps1"),
                "-InputDirectory", inputRoot,
                "-OutputDirectory", outputRoot,
                "-ExpectedVersion", expectedVersion,
                "-ExpectedSourceCommit", expectedSourceCommit,
                "-ExpectedConfigurationSha256", expectedConfigurationSha256,
                "-ExpectedReleasePlanSha256", expectedPlanSha256,
                "-ExpectedTargets", expectedTargets,
            ],
            repositoryRoot);

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
            },
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
}
