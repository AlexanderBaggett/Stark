using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stark.ReleaseTools;

internal static partial class CandidateEvidenceBinder
{
    private const long MaximumMetadataBytes = 64L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> RuntimeIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["linux-x64"] = "linux-x64",
        ["linux-arm64"] = "linux-arm64",
        ["macos-x64"] = "osx-x64",
        ["macos-arm64"] = "osx-arm64",
        ["windows-x64"] = "win-x64",
        ["windows-arm64"] = "win-arm64",
    };

    public static JsonObject Run(CommandLine command)
    {
        if (command.HasFlag("--bind"))
        {
            command.RejectUnknown("--bind", "--archive", "--sdk-root", "--managed-report", "--native-report", "--stage-report");
            return Bind(command.Required("--archive"), command.Required("--sdk-root"), command.Required("--managed-report"), command.Required("--native-report"), command.Required("--stage-report"));
        }

        command.RejectUnknown("--archive");
        return InspectArchive(command.Required("--archive")).Binding;
    }

    private sealed record ArchiveBinding(JsonObject Binding, byte[] ReleaseJson, byte[] Manifest);

    public static JsonObject Bind(string archive, string sdkRoot, string managedReport, string nativeReport, string stageReport)
    {
        var sdk = new DirectoryInfo(Path.GetFullPath(sdkRoot));
        Validation.Require(sdk.Exists && sdk.LinkTarget is null, $"Staged SDK root must be a real directory: {sdkRoot}");
        var inspected = InspectArchive(archive);
        var staged = inspected.Binding.RequiredObject("stagedSdk", "candidate binding");
        var rootName = staged.RequiredString("root", "candidate binding stagedSdk");
        Validation.Require(sdk.Name == rootName, "Staged SDK root and archived SDK root disagree.");
        var stagedRelease = CandidateIdentity.ReadStableFile(Path.Combine(sdk.FullName, "release.json"), "staged release.json");
        var stagedManifest = CandidateIdentity.ReadStableFile(Path.Combine(sdk.FullName, "release-files.sha256"), "staged release-files.sha256");
        Validation.Require(stagedRelease.AsSpan().SequenceEqual(inspected.ReleaseJson) && stagedManifest.AsSpan().SequenceEqual(inspected.Manifest), "Staged and archived release metadata bytes disagree.");
        var release = inspected.Binding.RequiredObject("release", "candidate binding");
        var target = release.RequiredString("targetId", "candidate binding release");
        var rid = release.RequiredString("runtimeIdentifier", "candidate binding release");
        var validated = CandidateIdentity.Inspect(sdk.FullName, target, rid);
        Validation.Require(
            validated.RequiredObject("releaseJson", "validated candidate").RequiredString("sha256", "validated candidate releaseJson") == staged.RequiredString("releaseJsonSha256", "candidate binding stagedSdk") &&
            validated.RequiredObject("releaseFiles", "validated candidate").RequiredString("sha256", "validated candidate releaseFiles") == staged.RequiredString("releaseFilesSha256", "candidate binding stagedSdk") &&
            validated.RequiredString("root", "validated candidate") == rootName &&
            validated.RequiredObject("release", "validated candidate").RequiredString("sourceCommit", "validated candidate release") == inspected.Binding.RequiredString("sourceCommit", "candidate binding") &&
            validated.RequiredObject("release", "validated candidate").RequiredString("configurationSha256", "validated candidate release") == inspected.Binding.RequiredObject("configuration", "candidate binding").RequiredString("sha256", "candidate binding configuration") &&
            validated.RequiredObject("release", "validated candidate").RequiredString("planSha256", "validated candidate release") == inspected.Binding.RequiredObject("plan", "candidate binding").RequiredString("sha256", "candidate binding plan"),
            "Staged validation subject and archived candidate binding disagree.");

        var specifications = new[]
        {
            (Path: Path.GetFullPath(managedReport), Role: "managed-dependency", Name: $"managed-dependencies-{target}.json"),
            (Path: Path.GetFullPath(nativeReport), Role: "native-dependency", Name: $"native-dependencies-{target}.json"),
            (Path: Path.GetFullPath(stageReport), Role: "stage-validation", Name: $"stage-validation-{target}.json"),
        };
        Validation.Require(specifications.Select(item => item.Path).Distinct(PathComparer).Count() == 3, "Candidate evidence report paths must be distinct.");
        var updates = new List<(string Path, byte[] Original, byte[] Updated)>();
        foreach (var specification in specifications)
        {
            Validation.Require(Path.GetFileName(specification.Path) == specification.Name, $"{specification.Role} report name must be '{specification.Name}'.");
            var original = CandidateIdentity.ReadStableFile(specification.Path, $"{specification.Role} report");
            var report = JsonIO.ParseObject(original, $"{specification.Role} report");
            ValidateReport(report, specification.Role, target, rid, rootName, validated);
            report["candidateBinding"] = inspected.Binding.DeepClone();
            var updated = Encoding.UTF8.GetBytes(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            updates.Add((specification.Path, original, updated));
        }

        ReplaceReports(updates);
        return inspected.Binding;
    }

    private static ArchiveBinding InspectArchive(string archivePath)
    {
        var path = Path.GetFullPath(archivePath);
        var info = new FileInfo(path);
        Validation.Require(info.Exists && info.LinkTarget is null && info.Length > 0, $"Release archive must be a nonempty regular file: {path}");
        var beforeLength = info.Length;
        var beforeWrite = info.LastWriteTimeUtc;
        var archiveSha = JsonIO.Sha256File(path);
        info.Refresh();
        Validation.Require(info.Length == beforeLength && info.LastWriteTimeUtc == beforeWrite, "Release archive changed while being hashed.");
        var nameMatch = ArchiveName().Match(info.Name);
        Validation.Require(nameMatch.Success, $"Release archive name is not canonical: '{info.Name}'.");
        var version = nameMatch.Groups["version"].Value;
        var target = nameMatch.Groups["target"].Value;
        var extension = nameMatch.Groups["extension"].Value;
        var rootName = $"stark-{version}-{target}";
        var releaseMember = $"{rootName}/release.json";
        var manifestMember = $"{rootName}/release-files.sha256";
        var metadata = extension == "zip" ? ReadZipMetadata(path, [releaseMember, manifestMember]) : ReadTarMetadata(path, [releaseMember, manifestMember]);
        info.Refresh();
        Validation.Require(info.Length == beforeLength && info.LastWriteTimeUtc == beforeWrite, "Release archive changed while embedded identity was inspected.");
        var releaseBytes = metadata[releaseMember];
        var manifestBytes = metadata[manifestMember];
        var releaseSha = JsonIO.Sha256(releaseBytes);
        var manifestSha = JsonIO.Sha256(manifestBytes);
        _ = CandidateIdentity.ValidateManifest(manifestBytes, releaseSha, "archived");
        var release = JsonIO.ParseObject(releaseBytes, "archived release.json");
        Validation.Require(release.RequiredInt("schemaVersion", "archived release.json") == 2, "Archived release.json schemaVersion must be 2.");
        var releaseVersion = release.RequiredString("releaseVersion", "archived release.json");
        Validation.Require(PortableArgument().IsMatch(releaseVersion) && releaseVersion == version && release.RequiredString("starkVersion", "archived release.json") == version, "Archive name and release version disagree.");
        var targetId = release.RequiredString("targetId", "archived release.json");
        var assetSuffix = release.RequiredString("assetSuffix", "archived release.json");
        Validation.Require(targetId == target && assetSuffix == target, "Archive name and release target disagree.");
        var rid = release.RequiredString("runtimeIdentifier", "archived release.json");
        Validation.Require(RuntimeIdentifiers[target] == rid, "Release runtime identifier does not match its target.");
        var triple = release.RequiredString("defaultTargetTriple", "archived release.json");
        Validation.Require(PortableArgument().IsMatch(triple), "Release target triple is invalid.");
        var archiveKind = release.RequiredString("archiveKind", "archived release.json");
        Validation.Require(archiveKind == (extension == "zip" ? "zip" : "targz"), "Release archive kind does not match its extension.");
        var source = release.RequiredObject("source", "archived release.json");
        var commit = source.RequiredString("commit", "archived release source");
        Validation.Require(SourceCommit().IsMatch(commit) && release.RequiredString("gitCommit", "archived release.json") == commit, "Release source identity is invalid.");
        var build = release.RequiredObject("buildIdentity", "archived release.json");
        Validation.Require(build.RequiredString("kind", "release build identity") == "content-addressed-release-build" && JsonNode.DeepEquals(release["workflowIdentity"], build), "Release workflow/build identities disagree.");
        var identity = build.RequiredString("identity", "release build identity");
        Validation.Require(ContentIdentity().IsMatch(identity), "Release build identity is not content addressed.");
        var configuration = release.RequiredObject("configuration", "archived release.json");
        var configurationSha = configuration.RequiredString("sha256", "release configuration");
        Validation.Require(configuration.RequiredString("identityKind", "release configuration") == "stark-release-configuration" && configuration.RequiredString("algorithm", "release configuration") == "sha256-ordinal-path-size-content-v1" && Validation.IsSha256(configurationSha), "Release configuration identity is invalid.");
        var planSha = build.RequiredString("releasePlanSha256", "release build identity");
        Validation.Require(build.RequiredString("configurationSha256", "release build identity") == configurationSha && Validation.IsSha256(planSha), "Release build/configuration/plan identities disagree.");
        var archiveTool = release.RequiredObject("buildOptions", "release build options").RequiredObject("archiveContainerTool", "release build options");
        ValidateReleaseTool(archiveTool);
        var expectedFacts = new JsonObject
        {
            ["archiveTool"] = archiveTool.DeepClone(),
            ["commit"] = commit,
            ["configurationSha256"] = configurationSha,
            ["releasePlanSha256"] = planSha,
            ["releaseVersion"] = releaseVersion,
            ["schemaVersion"] = 1,
            ["targetId"] = targetId,
        };
        Validation.Require(JsonNode.DeepEquals(build["identityFacts"], expectedFacts), "Release content-addressed identity facts disagree.");
        var computedIdentity = ComputeBuildIdentity(commit, releaseVersion, targetId, configurationSha, planSha, archiveTool);
        Validation.Require(identity == computedIdentity, "Release content-addressed identity digest is invalid.");

        var checksumPath = path + ".sha256";
        var checksum = Encoding.ASCII.GetString(CandidateIdentity.ReadStableFile(checksumPath, "release archive checksum")).Trim();
        var checksumMatch = ChecksumLine().Match(checksum);
        Validation.Require(checksumMatch.Success && checksumMatch.Groups[1].Value == archiveSha && checksumMatch.Groups[2].Value == info.Name, "Release archive checksum does not identify the exact archive.");
        var binding = new JsonObject
        {
            ["archive"] = new JsonObject { ["bytes"] = beforeLength, ["name"] = info.Name, ["sha256"] = archiveSha },
            ["configuration"] = new JsonObject { ["algorithm"] = "sha256-ordinal-path-size-content-v1", ["identityKind"] = "stark-release-configuration", ["sha256"] = configurationSha },
            ["kind"] = "stark-release-candidate-binding",
            ["plan"] = new JsonObject { ["algorithm"] = "sha256", ["sha256"] = planSha },
            ["release"] = new JsonObject
            {
                ["archiveKind"] = archiveKind,
                ["assetSuffix"] = assetSuffix,
                ["identity"] = new JsonObject { ["kind"] = "content-addressed-release-build", ["value"] = identity },
                ["runtimeIdentifier"] = rid,
                ["targetId"] = targetId,
                ["targetTriple"] = triple,
                ["version"] = releaseVersion,
            },
            ["schemaVersion"] = 1,
            ["sourceCommit"] = commit,
            ["stagedSdk"] = new JsonObject { ["releaseFilesSha256"] = manifestSha, ["releaseJsonSha256"] = releaseSha, ["root"] = rootName },
        };
        return new ArchiveBinding(binding, releaseBytes, manifestBytes);
    }

    public static string ComputeBuildIdentity(string commit, string version, string target, string configurationSha, string planSha, JsonObject tool)
    {
        var manifest = tool.RequiredObject("manifest", "release tool");
        var assembly = tool.RequiredObject("assembly", "release tool");
        var lines = new[]
        {
            "stark-content-addressed-release-build-v2", $"commit={commit}", $"releaseVersion={version}", $"targetId={target}",
            $"configurationSha256={configurationSha}", $"releasePlanSha256={planSha}",
            $"releaseToolManifestSha256={manifest.RequiredString("sha256", "release tool manifest")}",
            $"releaseToolImplementation={tool.RequiredString("implementation", "release tool")}",
            $"releaseToolTargetFramework={tool.RequiredString("targetFramework", "release tool")}",
            $"dotnetSdkVersion={tool.RequiredString("dotnetSdkVersion", "release tool")}",
            $"dotnetRuntimeVersion={tool.RequiredString("dotnetRuntimeVersion", "release tool")}",
            $"releaseToolAssemblySha256={assembly.RequiredString("sha256", "release tool assembly")}",
        };
        return $"sha256:{JsonIO.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"))}";
    }

    private static void ValidateReleaseTool(JsonObject tool)
    {
        Validation.Require(tool.RequiredString("implementation", "release tool") == ReleaseToolIdentity.Implementation && tool.RequiredString("targetFramework", "release tool") == ReleaseToolIdentity.TargetFramework && tool.RequiredString("dotnetSdkVersion", "release tool") == ReleaseToolIdentity.DotNetSdkVersion && tool.RequiredString("dotnetRuntimeVersion", "release tool") == ReleaseToolIdentity.DotNetRuntimeVersion, "Release archive-tool identity is unsupported.");
        var manifest = tool.RequiredObject("manifest", "release tool");
        Validation.Require(manifest.RequiredString("path", "release tool manifest") == "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj" && manifest.RequiredInt("schemaVersion", "release tool manifest") == 1 && Validation.IsSha256(manifest.RequiredString("sha256", "release tool manifest")), "Release archive-tool manifest identity is invalid.");
        var assembly = tool.RequiredObject("assembly", "release tool");
        Validation.Require(assembly["bytes"]!.GetValue<long>() > 0 && Validation.IsSha256(assembly.RequiredString("sha256", "release tool assembly")), "Release archive-tool assembly identity is invalid.");
    }

    private static Dictionary<string, byte[]> ReadZipMetadata(string path, HashSet<string> expected)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var selected = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var candidate = entry.FullName.TrimEnd('/');
            PortablePaths.Validate(candidate, "release ZIP entry");
            if (expected.Contains(entry.FullName)) Validation.Require(selected.TryAdd(entry.FullName, entry), $"Archive must contain exactly one '{entry.FullName}'.");
        }

        Validation.Require(selected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "Archive release metadata set is incomplete.");
        return selected.ToDictionary(item => item.Key, item => ReadLimited(item.Value.Open(), item.Value.Length, item.Key), StringComparer.Ordinal);
    }

    private static Dictionary<string, byte[]> ReadTarMetadata(string path, HashSet<string> expected)
    {
        using var stream = File.OpenRead(path);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = archive.GetNextEntry(copyData: false)) is not null)
        {
            PortablePaths.Validate(entry.Name.TrimEnd('/'), "release TAR entry");
            if (!expected.Contains(entry.Name)) continue;
            Validation.Require(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is not null && result.TryAdd(entry.Name, ReadLimited(entry.DataStream, entry.Length, entry.Name)), $"Archive must contain exactly one regular '{entry.Name}'.");
        }

        Validation.Require(result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "Archive release metadata set is incomplete.");
        return result;
    }

    private static byte[] ReadLimited(Stream stream, long declaredLength, string label)
    {
        Validation.Require(declaredLength is >= 0 and <= MaximumMetadataBytes, $"Archive metadata '{label}' is invalid or too large.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        Validation.Require(output.Length == declaredLength, $"Archive metadata '{label}' was truncated.");
        return output.ToArray();
    }

    private static void ValidateReport(JsonObject report, string role, string target, string rid, string rootName, JsonObject validated)
    {
        Validation.Require(report.RequiredInt("schemaVersion", role) == 1 && report["candidateBinding"] is null && report.RequiredString("validationScope", role) == "release-candidate" && JsonNode.DeepEquals(report["validatedCandidate"], validated), $"{role} report did not validate this exact staged release candidate.");
        switch (role)
        {
            case "managed-dependency":
                Validation.Require(report.RequiredString("status", role) == "ready" && report.RequiredString("targetId", role) == target && report.RequiredString("runtimeIdentifier", role) == rid, "Managed-dependency report identity is invalid.");
                foreach (var name in new[] { "nugetConfig", "lockFile" }) Validation.SafeRelativePath(report.RequiredString(name, role), $"{role} {name}");
                break;
            case "native-dependency":
                Validation.Require(report.RequiredString("status", role) == "ok" && report.RequiredString("assetSuffix", role) == target && report.RequiredString("sdkRoot", role) == rootName, "Native-dependency report identity is invalid.");
                break;
            case "stage-validation":
                Validation.Require(report.RequiredString("status", role) == "ok" && report.RequiredString("targetId", role) == target && report.RequiredString("sdkRoot", role) == rootName, "Stage-validation report identity is invalid.");
                break;
        }
    }

    private static void ReplaceReports(List<(string Path, byte[] Original, byte[] Updated)> updates)
    {
        var temporaries = new List<(string Path, string Temporary, byte[] Original)>();
        var replaced = new List<(string Path, byte[] Original)>();
        try
        {
            foreach (var update in updates)
            {
                Validation.Require(CandidateIdentity.ReadStableFile(update.Path, "evidence report").AsSpan().SequenceEqual(update.Original), $"Evidence report changed before binding: {update.Path}");
                var temporary = Path.Combine(Path.GetDirectoryName(update.Path)!, $".{Path.GetFileName(update.Path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temporary, update.Updated);
                temporaries.Add((update.Path, temporary, update.Original));
            }

            foreach (var item in temporaries)
            {
                File.Move(item.Temporary, item.Path, true);
                replaced.Add((item.Path, item.Original));
            }
        }
        catch
        {
            foreach (var item in replaced.AsEnumerable().Reverse()) File.WriteAllBytes(item.Path, item.Original);
            throw;
        }
        finally
        {
            foreach (var item in temporaries) if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^stark-(?<version>[A-Za-z0-9][A-Za-z0-9._+\-]*?)-(?<target>(?:linux|macos|windows)-(?:x64|arm64))\.(?<extension>tar\.gz|zip)$")]
    private static partial Regex ArchiveName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+\\-]*$")]
    private static partial Regex PortableArgument();
    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex SourceCommit();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ContentIdentity();
    [GeneratedRegex("^([0-9a-f]{64})  ([^/\\\\]+)$")]
    private static partial Regex ChecksumLine();
}
