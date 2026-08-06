using System.Text;
using System.Text.Json.Nodes;
using Stark.ReleaseTools;

namespace compiler.IntegrationTests;

public sealed class PrivateBackendQualifierTests
{
    [Fact]
    public void StaticQualificationAcceptsAnExactHashedPinnedSourceBuildClosure()
    {
        using var fixture = PrivateBackendFixture.Create();

        var result = PrivateBackendQualifier.ValidateManifest(
            fixture.RepositoryRoot,
            "macos-x64",
            fixture.ToolchainRoot);

        Assert.Equal("macos-x64", result.Target.Id);
        Assert.Equal("pinned-source-build", result.Manifest.RequiredString("acquisitionKind", "manifest"));
        Assert.Contains("bin/clang", result.RequiredTools);
    }

    [Fact]
    public void StaticQualificationRejectsTrackedByteDrift()
    {
        using var fixture = PrivateBackendFixture.Create();
        File.AppendAllText(Path.Combine(fixture.ToolchainRoot, "bin", "clang"), "drift", new UTF8Encoding(false));

        var error = Assert.Throws<ReleaseToolException>(() => PrivateBackendQualifier.ValidateManifest(
            fixture.RepositoryRoot,
            "macos-x64",
            fixture.ToolchainRoot));

        Assert.Contains("bytes; expected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticQualificationRejectsUntrackedDevelopmentFiles()
    {
        using var fixture = PrivateBackendFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.ToolchainRoot, "include"));
        File.WriteAllText(Path.Combine(fixture.ToolchainRoot, "include", "untracked.h"), "development", new UTF8Encoding(false));

        var error = Assert.Throws<ReleaseToolException>(() => PrivateBackendQualifier.ValidateManifest(
            fixture.RepositoryRoot,
            "macos-x64",
            fixture.ToolchainRoot));

        Assert.Contains("untracked files", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticQualificationRejectsOptimizationRecipeDrift()
    {
        using var fixture = PrivateBackendFixture.Create();
        var manifestPath = Path.Combine(fixture.ToolchainRoot, "manifest.json");
        var manifest = JsonIO.LoadObject(manifestPath, "test manifest");
        manifest.RequiredObject("sourceBuild", "test manifest")["lto"] = "Off";
        JsonIO.Write(manifestPath, manifest);

        var error = Assert.Throws<ReleaseToolException>(() => PrivateBackendQualifier.ValidateManifest(
            fixture.RepositoryRoot,
            "macos-x64",
            fixture.ToolchainRoot));

        Assert.Contains("source-build lto differs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticQualificationRejectsAppleToolchainIdentityDrift()
    {
        using var fixture = PrivateBackendFixture.Create();
        var manifestPath = Path.Combine(fixture.ToolchainRoot, "manifest.json");
        var manifest = JsonIO.LoadObject(manifestPath, "test manifest");
        manifest.RequiredObject("sourceBuild", "test manifest")
            .RequiredObject("appleToolchain", "source-build evidence")["sdkVersion"] = "99.0";
        JsonIO.Write(manifestPath, manifest);

        var error = Assert.Throws<ReleaseToolException>(() => PrivateBackendQualifier.ValidateManifest(
            fixture.RepositoryRoot,
            "macos-x64",
            fixture.ToolchainRoot));

        Assert.Contains("SDK identity differs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericLldVersionProbeSelectsTheMacOsDriverFlavor()
    {
        Assert.Equal(
            ["-flavor", "darwin", "--version"],
            PrivateBackendQualifier.GetVersionProbeArguments("bin/lld"));
    }

    [Theory]
    [InlineData("bin/ld.lld")]
    [InlineData("bin/ld64.lld")]
    [InlineData("bin/clang")]
    public void NamedBackendToolsUseTheirNativeVersionProbe(string relativePath)
    {
        Assert.Equal(
            ["--version"],
            PrivateBackendQualifier.GetVersionProbeArguments(relativePath));
    }

    private sealed class PrivateBackendFixture : IDisposable
    {
        private PrivateBackendFixture(string repositoryRoot, string temporaryRoot, string toolchainRoot)
        {
            RepositoryRoot = repositoryRoot;
            TemporaryRoot = temporaryRoot;
            ToolchainRoot = toolchainRoot;
        }

        public string RepositoryRoot { get; }
        public string TemporaryRoot { get; }
        public string ToolchainRoot { get; }

        public static PrivateBackendFixture Create()
        {
            var repositoryRoot = FindRepositoryRoot();
            var temporaryRoot = Directory.CreateTempSubdirectory("stark-private-backend-fixture-").FullName;
            var toolchainRoot = Path.Combine(temporaryRoot, "toolchain");
            Directory.CreateDirectory(toolchainRoot);

            var acquisition = JsonIO.LoadObject(
                Path.Combine(repositoryRoot, "scripts", "llvm-22.1.8-assets.json"),
                "LLVM acquisition manifest");
            var platform = acquisition.RequiredObject("platforms", "LLVM acquisition manifest")
                .RequiredObject("macos-x64", "LLVM acquisition platforms");
            var recipe = platform.RequiredObject("sourceBuild", "LLVM/macos-x64");
            var requiredTools = Validation.Strings(platform["requiredTools"], "requiredTools", nonEmpty: true);

            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".stark-llvm-toolchain-owner.json"] = "{\"schemaVersion\":1,\"kind\":\"stark-llvm-output\"}\n",
                ["lib/clang/22/include/stddef.h"] = "/* compiler resource */\n",
                ["licenses/source/LICENSE.TXT"] = "Apache License 2.0 with LLVM exception\n",
            };
            foreach (var requiredTool in requiredTools)
            {
                files[requiredTool] = "synthetic executable\n";
            }

            var sourceArchive = acquisition.RequiredObject("sourceArchive", "LLVM acquisition manifest");
            foreach (var kind in new[] { "signature", "attestation" })
            {
                var evidenceAsset = sourceArchive.RequiredObject(kind, "LLVM source archive");
                files[$"provenance/{evidenceAsset.RequiredString("name", $"LLVM source {kind}")}"] = $"{kind}\n";
            }

            foreach (var (relativePath, content) in files)
            {
                var path = Path.Combine(toolchainRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }

            var evidence = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["recipeKind"] = "pinned-source-build",
                ["hostOperatingSystem"] = recipe["hostOperatingSystem"]!.DeepClone(),
                ["hostArchitecture"] = recipe["hostArchitecture"]!.DeepClone(),
                ["minimumDeploymentTarget"] = recipe["minimumDeploymentTarget"]!.DeepClone(),
                ["configuration"] = recipe["configuration"]!.DeepClone(),
                ["optimization"] = recipe["optimization"]!.DeepClone(),
                ["lto"] = recipe["lto"]!.DeepClone(),
                ["generator"] = recipe["generator"]!.DeepClone(),
                ["sourceSubdirectory"] = recipe["sourceSubdirectory"]!.DeepClone(),
                ["projects"] = recipe["projects"]!.DeepClone(),
                ["targetsToBuild"] = recipe["targetsToBuild"]!.DeepClone(),
                ["buildTarget"] = recipe["buildTarget"]!.DeepClone(),
                ["cmakeOptions"] = recipe["cmakeOptions"]!.DeepClone(),
                ["sourceDateEpoch"] = recipe["sourceDateEpoch"]!.DeepClone(),
                ["compileJobs"] = 1,
                ["parallelLinkJobs"] = recipe["parallelLinkJobs"]!.DeepClone(),
                ["buildTools"] = new JsonObject
                {
                    ["cmake"] = new JsonObject
                    {
                        ["version"] = recipe["cmakeVersion"]!.DeepClone(),
                        ["sha256"] = new string('1', 64),
                    },
                    ["ninja"] = new JsonObject
                    {
                        ["version"] = recipe["ninjaVersion"]!.DeepClone(),
                        ["sha256"] = new string('2', 64),
                    },
                },
                ["appleToolchain"] = new JsonObject
                {
                    ["xcodeVersion"] = recipe.RequiredObject("qualifiedAppleToolchain", "source-build recipe")["xcodeVersion"]!.DeepClone(),
                    ["sdkVersion"] = recipe.RequiredObject("qualifiedAppleToolchain", "source-build recipe")["sdkVersion"]!.DeepClone(),
                    ["clangVersion"] = recipe.RequiredObject("qualifiedAppleToolchain", "source-build recipe")["clangVersionLine"]!.DeepClone(),
                    ["clangSha256"] = recipe.RequiredObject("qualifiedAppleToolchain", "source-build recipe")["clangSha256"]!.DeepClone(),
                    ["clangxxSha256"] = recipe.RequiredObject("qualifiedAppleToolchain", "source-build recipe")["clangxxSha256"]!.DeepClone(),
                },
            };

            var closureFiles = new JsonArray();
            long logicalBytes = 0;
            foreach (var path in Directory.EnumerateFiles(toolchainRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var file = new FileInfo(path);
                var relativePath = Path.GetRelativePath(toolchainRoot, path).Replace('\\', '/');
                logicalBytes += file.Length;
                closureFiles.Add(new JsonObject
                {
                    ["path"] = relativePath,
                    ["bytes"] = file.Length,
                    ["sha256"] = JsonIO.Sha256File(path),
                });
            }

            var manifest = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["payloadKind"] = "stark-compiler-private-backend",
                ["llvmVersion"] = acquisition["llvmVersion"]!.DeepClone(),
                ["releaseTag"] = acquisition["releaseTag"]!.DeepClone(),
                ["releaseUrl"] = acquisition["releaseUrl"]!.DeepClone(),
                ["assetSuffix"] = "macos-x64",
                ["runtimeIdentifier"] = "osx-x64",
                ["acquisitionKind"] = "pinned-source-build",
                ["binaryArchive"] = null,
                ["sourceArchive"] = sourceArchive.DeepClone(),
                ["sourceBuild"] = evidence,
                ["compilerResourceRoots"] = new JsonArray("lib/clang"),
                ["requiredTools"] = platform["requiredTools"]!.DeepClone(),
                ["requiredPatternMatches"] = new JsonArray(),
                ["excludedDevelopmentPatterns"] = new JsonArray(),
                ["hardlinkAliases"] = platform["hardlinkAliases"]!.DeepClone(),
                ["licenseFiles"] = new JsonArray("source/LICENSE.TXT"),
                ["runtimeClosure"] = new JsonObject
                {
                    ["fileCount"] = closureFiles.Count,
                    ["logicalBytes"] = logicalBytes,
                    ["files"] = closureFiles,
                },
            };
            JsonIO.Write(Path.Combine(toolchainRoot, "manifest.json"), manifest);
            return new PrivateBackendFixture(repositoryRoot, temporaryRoot, toolchainRoot);
        }

        public void Dispose() => Directory.Delete(TemporaryRoot, recursive: true);

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
}
