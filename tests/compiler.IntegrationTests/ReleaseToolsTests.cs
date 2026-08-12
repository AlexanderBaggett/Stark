using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Stark.ReleaseTools;

namespace compiler.IntegrationTests;

public sealed class ReleaseToolsTests
{
    [Theory]
    [InlineData(493, "0755")]
    [InlineData(420, "0644")]
    [InlineData(0, "0000")]
    public void UnixModesUseExplicitOctalFormatting(int mode, string expected)
    {
        Assert.Equal(expected, ReleaseStageValidator.FormatUnixMode(mode));
    }

    [Fact]
    public void RepositoryCheckoutPreservesReleaseCriticalLfBytes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var attributes = File.ReadAllText(Path.Combine(repositoryRoot, ".gitattributes")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("* text=auto eol=lf\n", attributes, StringComparison.Ordinal);

        var license = File.ReadAllBytes(Path.Combine(repositoryRoot, "eng", "release", "licenses", "Antlr4.Runtime.Standard-4.13.1-LICENSE.txt"));
        Assert.Equal(1_480, license.Length);
        Assert.DoesNotContain((byte)'\r', license);
        Assert.Equal("3db1fb3ee79a4b4f9918fc4d0f6133bf18a3cf787f126cd22f8aa9b862281c0c", JsonIO.Sha256(license));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("targz")]
    public void ArchiveCreationIsDeterministicAndRoundTrips(string kind)
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.Path, "source"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
        File.WriteAllText(Path.Combine(source.FullName, "nested", "data.txt"), "stark\n", new UTF8Encoding(false));
        var extension = kind == "zip" ? "zip" : "tar.gz";
        var first = Path.Combine(temporary.Path, $"first.{extension}");
        var second = Path.Combine(temporary.Path, $"second.{extension}");

        ArchiveCreator.Run(CommandLine.Parse(["create-archive", "--source-root", source.FullName, "--output", first, "--kind", kind]));
        File.SetLastWriteTimeUtc(Path.Combine(source.FullName, "nested", "data.txt"), DateTime.UtcNow.AddYears(-7));
        ArchiveCreator.Run(CommandLine.Parse(["create-archive", "--source-root", source.FullName, "--output", second, "--kind", kind]));

        Assert.Equal(JsonIO.Sha256File(first), JsonIO.Sha256File(second));
        var extracted = Path.Combine(temporary.Path, "extracted");
        ArchiveExtractor.Extract(CommandLine.Parse(["extract-archive", "--archive", first, "--kind", kind, "--destination", extracted, "--required-root", source.Name]));
        Assert.Equal("stark\n", File.ReadAllText(Path.Combine(extracted, source.Name, "nested", "data.txt")));
    }

    [Fact]
    public void ArchiveExtractionRejectsTraversalBeforeWritingDestination()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "malicious.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("escape");
        }

        var destination = Path.Combine(temporary.Path, "output");
        Assert.Throws<ReleaseToolException>(() => ArchiveExtractor.Extract(CommandLine.Parse([
            "extract-archive", "--archive", archivePath, "--kind", "zip", "--destination", destination, "--label", "test archive"])));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escape.txt")));
    }

    [Fact]
    public void ArchiveExtractionRejectsDuplicateAndPortableCollidingEntries()
    {
        using var temporary = new TemporaryDirectory();
        foreach (var names in new[] { new[] { "root/value.txt", "root/value.txt" }, new[] { "root/Value.txt", "root/value.txt" } })
        {
            var archivePath = Path.Combine(temporary.Path, $"malicious-{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (var name in names)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                    writer.Write(name);
                }
            }

            var destination = Path.Combine(temporary.Path, $"output-{Guid.NewGuid():N}");
            Assert.Throws<ReleaseToolException>(() => ArchiveExtractor.Extract(CommandLine.Parse([
                "extract-archive", "--archive", archivePath, "--kind", "zip", "--destination", destination, "--required-root", "root"])));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public void ArchiveCreationRejectsNonportableInputAndOutputInsideStage()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.Path, "root"));
        File.WriteAllText(Path.Combine(source.FullName, "café.txt"), "value", new UTF8Encoding(false));
        Assert.Throws<ReleaseToolException>(() => ArchiveCreator.Create(source.FullName, Path.Combine(temporary.Path, "release.zip"), "zip"));

        File.Delete(Path.Combine(source.FullName, "café.txt"));
        File.WriteAllText(Path.Combine(source.FullName, "value.txt"), "value", new UTF8Encoding(false));
        Assert.Throws<ReleaseToolException>(() => ArchiveCreator.Create(source.FullName, Path.Combine(source.FullName, "release.zip"), "zip"));
    }

    [Fact]
    public void TarCreationPreservesSafeLinksAndZipFailsClosed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.Path, "root"));
        File.WriteAllText(Path.Combine(source.FullName, "target.txt"), "value", new UTF8Encoding(false));
        File.CreateSymbolicLink(Path.Combine(source.FullName, "link.txt"), "target.txt");
        var tar = Path.Combine(temporary.Path, "release.tar.gz");
        ArchiveCreator.Create(source.FullName, tar, "targz");
        var extracted = Path.Combine(temporary.Path, "extracted");
        ArchiveExtractor.Extract(CommandLine.Parse(["extract-archive", "--archive", tar, "--kind", "targz", "--destination", extracted, "--required-root", "root"]));
        Assert.Equal("target.txt", new FileInfo(Path.Combine(extracted, "root", "link.txt")).LinkTarget);
        Assert.Throws<ReleaseToolException>(() => ArchiveCreator.Create(source.FullName, Path.Combine(temporary.Path, "release.zip"), "zip"));
    }

    [Fact]
    public void TarExtractionSupportsSafeLinksThroughImplicitDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "implicit-root.tar.gz");
        using (var output = File.Create(archivePath))
        using (var compressed = new GZipStream(output, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(compressed, TarEntryFormat.Pax))
        {
            using var content = new MemoryStream("tool\n"u8.ToArray(), writable: false);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "root/Versions/5/tool") { DataStream = content });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "root/Versions/Current") { LinkName = "5" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "root/tool") { LinkName = "Versions/Current/tool" });
        }

        var destination = Path.Combine(temporary.Path, "output");
        ArchiveExtractor.Extract(CommandLine.Parse([
            "extract-archive", "--archive", archivePath, "--kind", "targz",
            "--destination", destination, "--required-root", "root", "--label", "implicit-root archive"]));
        Assert.Equal("tool\n", File.ReadAllText(Path.Combine(destination, "root", "tool")));
    }

    [Fact]
    public void TarExtractionRestoresPaxUstarPrefixesAndEmptyFiles()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "pax-prefix.tar.gz");
        var directory = "root/" + string.Join('/', Enumerable.Repeat("long-directory-name", 6));
        var entryPath = $"{directory}/empty.txt";
        using (var output = File.Create(archivePath))
        using (var compressed = new GZipStream(output, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(compressed, TarEntryFormat.Pax))
        {
            var attributes = new[] { KeyValuePair.Create("stark.test", "ustar-prefix") };
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryPath, attributes) { DataStream = Stream.Null });
        }

        var destination = Path.Combine(temporary.Path, "output");
        ArchiveExtractor.Extract(CommandLine.Parse([
            "extract-archive", "--archive", archivePath, "--kind", "targz",
            "--destination", destination, "--required-root", "root", "--label", "PAX prefix archive"]));
        var extracted = new FileInfo(Path.Combine(destination, entryPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(extracted.Exists);
        Assert.Equal(0, extracted.Length);
    }

    [Fact]
    public void ArchiveExtractionRejectsCaseCollidingImplicitDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "implicit-collision.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(archive.CreateEntry("Root/one/value.txt").Open(), new UTF8Encoding(false)))
            {
                first.Write("one");
            }
            using var second = new StreamWriter(archive.CreateEntry("root/two/value.txt").Open(), new UTF8Encoding(false));
            second.Write("two");
        }

        var destination = Path.Combine(temporary.Path, "output");
        var error = Assert.Throws<ReleaseToolException>(() => ArchiveExtractor.Extract(CommandLine.Parse([
            "extract-archive", "--archive", archivePath, "--kind", "zip",
            "--destination", destination, "--label", "implicit-collision archive"])));
        Assert.Contains("case-colliding explicit or implicit paths", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void CandidateComparisonReportsContentAndMetadataIndependently()
    {
        using var temporary = new TemporaryDirectory();
        var left = Directory.CreateDirectory(Path.Combine(temporary.Path, "left"));
        var right = Directory.CreateDirectory(Path.Combine(temporary.Path, "right"));
        File.WriteAllText(Path.Combine(left.FullName, "value.txt"), "left", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(right.FullName, "value.txt"), "right", new UTF8Encoding(false));
        var timestamp = DateTime.UtcNow.AddYears(-1);
        File.SetLastWriteTimeUtc(Path.Combine(left.FullName, "value.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(right.FullName, "value.txt"), timestamp);

        var report = CandidateComparer.Compare(CandidateComparer.Inventory(left.FullName, "left"), CandidateComparer.Inventory(right.FullName, "right"));
        Assert.False(report.RequiredObject("result", "comparison").RequiredBool("deterministicEqual", "comparison result"));
        Assert.False(report.RequiredObject("result", "comparison").RequiredBool("payloadContentEqual", "comparison result"));
        Assert.Single(report.RequiredObject("differences", "comparison").RequiredArray("entryContent", "comparison differences"));
    }

    [Fact]
    public void CandidateComparisonReportsMetadataOnlyDrift()
    {
        using var temporary = new TemporaryDirectory();
        var left = Directory.CreateDirectory(Path.Combine(temporary.Path, "left"));
        var right = Directory.CreateDirectory(Path.Combine(temporary.Path, "right"));
        var leftPath = Path.Combine(left.FullName, "value.txt");
        var rightPath = Path.Combine(right.FullName, "value.txt");
        File.WriteAllText(leftPath, "same", new UTF8Encoding(false));
        File.WriteAllText(rightPath, "same", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(leftPath, DateTime.UtcNow.AddYears(-2));
        File.SetLastWriteTimeUtc(rightPath, DateTime.UtcNow.AddYears(-1));

        var report = CandidateComparer.Compare(CandidateComparer.Inventory(left.FullName, "left"), CandidateComparer.Inventory(right.FullName, "right"));
        Assert.True(report.RequiredObject("result", "comparison").RequiredBool("payloadContentEqual", "comparison result"));
        Assert.False(report.RequiredObject("result", "comparison").RequiredBool("entryMetadataEqual", "comparison result"));
        Assert.Single(report.RequiredObject("differences", "comparison").RequiredArray("entryMetadata", "comparison differences"));
    }

    [Fact]
    public void JsonReaderRejectsDuplicateSecurityProperties()
    {
        Assert.Throws<ReleaseToolException>(() => JsonIO.ParseObject("{\"sha256\":\"a\",\"sha256\":\"b\"}"u8, "test input"));
    }

    [Theory]
    [InlineData("--root", "one", "--root", "two")]
    [InlineData("--bind", "--bind", "true", "")]
    public void CommandLineRejectsEveryDuplicateOption(string first, string second, string third, string fourth)
    {
        var arguments = new[] { "validate-config", first, second, third, fourth }
            .Where(value => value.Length != 0)
            .ToArray();
        Assert.Throws<ReleaseToolException>(() => CommandLine.Parse(arguments));
    }

    [Fact]
    public void ReleaseConfigurationAndWorkflowHaveNoPythonRuntimeDependency()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = ReleaseConfiguration.Validate(repositoryRoot);
        Assert.NotEmpty(configuration.Targets);
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var scripts = string.Join('\n', Directory.EnumerateFiles(Path.Combine(repositoryRoot, "scripts"), "*.ps1").Select(File.ReadAllText));
        Assert.DoesNotContain("python", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("python", scripts, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "scripts"), "*.py", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "eng", "release"), "*.py", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReleaseNuGetConfigurationFailsClosedOnTrustAndMappingDrift()
    {
        var repositoryRoot = FindRepositoryRoot();
        const string relativePath = "eng/release/NuGet.config";
        ReleaseConfiguration.ValidateNuGetConfiguration(repositoryRoot, relativePath);
        var original = XDocument.Load(Path.Combine(repositoryRoot, relativePath));
        using var temporary = new TemporaryDirectory();

        void RequireRejection(Action<XElement> mutate, string expectedMessage)
        {
            var changed = XDocument.Parse(original.ToString(SaveOptions.DisableFormatting));
            mutate(changed.Root!);
            const string fileName = "NuGet.config";
            changed.Save(Path.Combine(temporary.Path, fileName));
            var error = Assert.Throws<ReleaseToolException>(() => ReleaseConfiguration.ValidateNuGetConfiguration(temporary.Path, fileName));
            Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        }

        RequireRejection(root => root.Descendants("package")
            .Single(package => package.Attribute("pattern")?.Value == "Microsoft.NETCore.App.Runtime.*")
            .Remove(), "package-source mapping");
        RequireRejection(root => root.Descendants("certificate").First()
            .SetAttributeValue("fingerprint", new string('0', 64)), "trusted repository certificates");
    }

    [Fact]
    public void ReleaseConfigurationEmitsTheReviewedEnabledAndPlannedMatrices()
    {
        var configuration = ReleaseConfiguration.Validate(FindRepositoryRoot());
        Assert.Equal(6, configuration.Targets.Count);
        Assert.Equal(3, configuration.Targets.Count(target => target.ReleaseEnabled));
        var enabled = ReleaseConfiguration.GenerateMatrix(configuration, includePlanned: false).RequiredArray("include", "matrix").OfType<JsonObject>().ToArray();
        var complete = ReleaseConfiguration.GenerateMatrix(configuration, includePlanned: true).RequiredArray("include", "matrix").OfType<JsonObject>().ToArray();
        Assert.Equal(3, enabled.Length);
        Assert.Equal(6, complete.Length);
        Assert.All(enabled, entry => Assert.True(entry.RequiredBool("release_enabled", "matrix entry")));
        var macOsX64 = complete.Single(entry => entry.RequiredString("target_id", "matrix entry") == "macos-x64");
        Assert.False(macOsX64.RequiredBool("release_enabled", "matrix entry"));
        Assert.Equal("pinned-source-build", macOsX64.RequiredString("private_backend_acquisition", "matrix entry"));
    }

    [Fact]
    public void ReleaseBuildToolManifestIsTargetCompleteAndFailsClosedOnInputDrift()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = ReleaseConfiguration.Validate(repositoryRoot);
        var buildTools = JsonIO.LoadObject(Path.Combine(repositoryRoot, "eng", "release", "build-tools.json"), "build-tools.json");
        ReleaseConfiguration.ValidateBuildTools(buildTools, configuration.Targets);

        var cmake = buildTools.RequiredObject("tools", "build-tools.json").RequiredObject("cmake", "build-tools.json tools");
        var firstAsset = cmake.RequiredArray("assets", "CMake").OfType<JsonObject>().First();
        firstAsset["url"] = "https://example.invalid/unreviewed-build-tool.tar.gz";
        var error = Assert.Throws<ReleaseToolException>(() => ReleaseConfiguration.ValidateBuildTools(buildTools, configuration.Targets));
        Assert.Contains("immutable release identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseToolIdentityDeclaresTheExactPinnedRuntimePolicy()
    {
        var identity = ReleaseToolIdentity.Current();
        var policy = identity.RequiredObject("policy", "release tool identity");

        Assert.Equal("10.0.302", policy.RequiredString("dotnetSdkVersion", "release tool policy"));
        Assert.Equal("10.0.10", policy.RequiredString("dotnetRuntimeVersion", "release tool policy"));
    }

    [Fact]
    public void StandaloneReleaseToolDisablesRuntimePatchRollForward()
    {
        var assemblyPath = typeof(ReleaseToolIdentity).Assembly.Location;
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        Assert.True(File.Exists(runtimeConfigPath), $"Release-tool runtime config is missing: {runtimeConfigPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        var runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
        var framework = runtimeOptions.GetProperty("framework");
        Assert.Equal("Disable", runtimeOptions.GetProperty("rollForward").GetString());
        Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());
        Assert.Equal("10.0.10", framework.GetProperty("version").GetString());
    }

    [Fact]
    public void ReleasePlanningRejectsPartialPublicationBeforeWritingAPlan()
    {
        using var temporary = new TemporaryDirectory();
        var plan = Path.Combine(temporary.Path, "plan.json");
        Assert.Throws<ReleaseToolException>(() => ReleasePlanPreparer.Run(CommandLine.Parse([
            "prepare-release", "--root", FindRepositoryRoot(), "--event-name", "workflow_dispatch",
            "--resolved-commit", new string('1', 40), "--input-version", "v0.1.0", "--input-ref", new string('1', 40),
            "--input-targets", "macos-arm64", "--input-publish", "true",
            "--input-draft", "true", "--input-prerelease", "false", "--plan-output", plan])));
        Assert.False(File.Exists(plan));
    }

    [Fact]
    public void ReleasePlanningAllowsManualPublicationFromABranchRef()
    {
        using var temporary = new TemporaryDirectory();
        var planPath = Path.Combine(temporary.Path, "plan.json");
        var commit = new string('1', 40);
        ReleasePlanPreparer.Run(CommandLine.Parse([
            "prepare-release", "--root", FindRepositoryRoot(), "--event-name", "workflow_dispatch",
            "--resolved-commit", commit, "--input-version", "v0.1.0", "--input-ref", "main",
            "--input-targets", "all", "--input-publish", "true",
            "--input-draft", "true", "--input-prerelease", "true", "--input-include-planned", "false",
            "--plan-output", planPath]));

        var plan = JsonIO.LoadObject(planPath, "test release plan");
        Assert.Equal(commit, plan.RequiredString("resolvedCommit", "test release plan"));
        Assert.True(plan.RequiredBool("publish", "test release plan"));
    }

    [Fact]
    public void ReleasePlanningAllowsAllPlannedTargetsOnlyForDiagnostics()
    {
        using var temporary = new TemporaryDirectory();
        var planPath = Path.Combine(temporary.Path, "plan.json");
        ReleasePlanPreparer.Run(CommandLine.Parse([
            "prepare-release", "--root", FindRepositoryRoot(), "--event-name", "workflow_dispatch",
            "--resolved-commit", new string('1', 40), "--input-version", "v0.1.0", "--input-ref", new string('1', 40),
            "--input-targets", "all", "--input-publish", "false",
            "--input-draft", "true", "--input-prerelease", "true", "--input-include-planned", "true",
            "--plan-output", planPath]));

        var plan = JsonIO.LoadObject(planPath, "test release plan");
        Assert.True(plan.RequiredBool("includePlanned", "test release plan"));
        Assert.False(plan.RequiredBool("publish", "test release plan"));
        Assert.Equal(6, plan.RequiredArray("targetIds", "test release plan").Count);
        Assert.Equal(6, plan.RequiredObject("matrix", "test release plan").RequiredArray("include", "test release matrix").Count);
    }

    [Fact]
    public void ReleasePlanningRejectsPlannedTargetsWithoutDiagnosticOptIn()
    {
        using var temporary = new TemporaryDirectory();
        var planPath = Path.Combine(temporary.Path, "plan.json");
        Assert.Throws<ReleaseToolException>(() => ReleasePlanPreparer.Run(CommandLine.Parse([
            "prepare-release", "--root", FindRepositoryRoot(), "--event-name", "workflow_dispatch",
            "--resolved-commit", new string('1', 40), "--input-version", "v0.1.0", "--input-ref", new string('1', 40),
            "--input-targets", "macos-x64", "--input-publish", "false",
            "--input-draft", "true", "--input-prerelease", "true", "--input-include-planned", "false",
            "--plan-output", planPath])));
        Assert.False(File.Exists(planPath));
    }

    [Fact]
    public void ReleasePlanningRejectsPlannedTargetsInPublicationMode()
    {
        using var temporary = new TemporaryDirectory();
        var planPath = Path.Combine(temporary.Path, "plan.json");
        var error = Assert.Throws<ReleaseToolException>(() => ReleasePlanPreparer.Run(CommandLine.Parse([
            "prepare-release", "--root", FindRepositoryRoot(), "--event-name", "workflow_dispatch",
            "--resolved-commit", new string('1', 40), "--input-version", "v0.1.0", "--input-ref", new string('1', 40),
            "--input-targets", "all", "--input-publish", "true",
            "--input-draft", "true", "--input-prerelease", "false", "--input-include-planned", "true",
            "--plan-output", planPath])));
        Assert.Contains("diagnostic-only", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(planPath));
    }

    [Fact]
    public async Task PublicationReconciliationRejectsUnsafeTagsBeforeNetworkAccess()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "asset.zip"), "value", new UTF8Encoding(false));
        await Assert.ThrowsAsync<ReleaseToolException>(() => GitHubReleaseReconciler.RunAsync(CommandLine.Parse([
            "reconcile-github-release", "--mode", "verify", "--tag", "../unsafe", "--expected-commit", new string('1', 40),
            "--desired-directory", temporary.Path, "--repository", "owner/repository"])));
    }

    [Fact]
    public async Task PublicationReconciliationRejectsOverlappingAssetDirectoriesBeforeNetworkAccess()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "asset.zip"), "value", new UTF8Encoding(false));
        var uploadDirectory = Path.Combine(temporary.Path, "upload");

        await Assert.ThrowsAsync<ReleaseToolException>(() => GitHubReleaseReconciler.RunAsync(CommandLine.Parse([
            "reconcile-github-release", "--mode", "prune", "--tag", "v0.1.0", "--expected-commit", new string('1', 40),
            "--desired-directory", temporary.Path, "--upload-directory", uploadDirectory, "--repository", "owner/repository"])));
        Assert.False(Directory.Exists(uploadDirectory));
    }

    [Fact]
    public async Task PublicationReconciliationVerifiesAnExactDraftWithoutMutation()
    {
        using var temporary = new TemporaryDirectory();
        var assetPath = Path.Combine(temporary.Path, "asset.zip");
        File.WriteAllText(assetPath, "value", new UTF8Encoding(false));
        var commit = new string('1', 40);
        var digest = $"sha256:{JsonIO.Sha256File(assetPath)}";
        var handler = new ScriptedHttpHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            Assert.Equal(HttpMethod.Get, request.Method);
            return path switch
            {
                "/repos/owner/repository/releases/tags/v0.1.0" => JsonResponse(new JsonObject
                {
                    ["id"] = 7,
                    ["tag_name"] = "v0.1.0",
                    ["draft"] = true,
                    ["prerelease"] = true,
                    ["name"] = "Stark v0.1.0",
                    ["target_commitish"] = commit,
                }),
                "/repos/owner/repository/git/ref/tags/v0.1.0" => JsonResponse(new JsonObject
                {
                    ["ref"] = "refs/tags/v0.1.0",
                    ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = commit },
                }),
                var value when value == $"/repos/owner/repository/commits/{commit}" => JsonResponse(new JsonObject { ["sha"] = commit }),
                "/repos/owner/repository/releases/7/assets?per_page=100&page=1" => JsonResponse(new JsonArray
                {
                    new JsonObject { ["id"] = 9, ["name"] = "asset.zip", ["state"] = "uploaded", ["size"] = 5, ["digest"] = digest },
                }),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected GitHub request: {request.Method} {path}"),
            };
        });

        var report = await GitHubReleaseReconciler.ReconcileAsync(
            "verify", "v0.1.0", commit, temporary.Path, "owner/repository", "https://api.github.test", "token",
            uploadDirectory: null, desiredDraft: null, prerelease: null, releaseName: null, journalPath: null, handler: handler);

        Assert.Equal("verified-draft", report["status"]!.GetValue<string>());
        Assert.Equal(4, handler.RequestCount);
        Assert.False(report["releaseActionRequired"]!.GetValue<bool>());
    }

    private static HttpResponseMessage JsonResponse(JsonNode document, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonIO.Compact(document), Encoding.UTF8, "application/json"),
        };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Stark repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("stark-release-tools-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
