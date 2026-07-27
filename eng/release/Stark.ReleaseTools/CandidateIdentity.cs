using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stark.ReleaseTools;

internal static partial class CandidateIdentity
{
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
        command.RejectUnknown("--sdk-root", "--target-id", "--runtime-identifier");
        return Inspect(command.Required("--sdk-root"), command.OptionalNullable("--target-id"), command.OptionalNullable("--runtime-identifier"));
    }

    public static JsonObject Inspect(string sdkRoot, string? expectedTargetId = null, string? expectedRuntimeIdentifier = null)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(sdkRoot));
        Validation.Require(directory.Exists && directory.LinkTarget is null, $"Staged SDK root must be a real directory: {sdkRoot}");
        var releaseBytes = ReadStableFile(Path.Combine(directory.FullName, "release.json"), "staged release.json");
        var manifestBytes = ReadStableFile(Path.Combine(directory.FullName, "release-files.sha256"), "staged release-files.sha256");
        var releaseSha = JsonIO.Sha256(releaseBytes);
        var manifestSha = JsonIO.Sha256(manifestBytes);
        var manifestEntries = ValidateManifest(manifestBytes, releaseSha, "staged");
        var release = JsonIO.ParseObject(releaseBytes, "staged release.json");
        Validation.Require(release.RequiredInt("schemaVersion", "staged release.json") == 2, "Staged release.json schemaVersion must be 2.");
        var version = release.RequiredString("releaseVersion", "staged release.json");
        Validation.Require(release.RequiredString("starkVersion", "staged release.json") == version, "Staged release versions disagree.");
        var targetId = release.RequiredString("targetId", "staged release.json");
        Validation.Require(Target().IsMatch(targetId) && release.RequiredString("assetSuffix", "staged release.json") == targetId, "Staged release target identity is invalid.");
        Validation.Require(directory.Name == $"stark-{version}-{targetId}", "Staged SDK root does not match its release identity.");
        if (expectedTargetId is not null) Validation.Require(targetId == expectedTargetId, "Staged release target does not match the validator target.");
        var rid = release.RequiredString("runtimeIdentifier", "staged release.json");
        Validation.Require(RuntimeIdentifiers[targetId] == rid, "Staged release runtime identifier is invalid.");
        if (expectedRuntimeIdentifier is not null) Validation.Require(rid == expectedRuntimeIdentifier, "Staged release runtime identifier does not match the validated managed restore.");
        var source = release.RequiredObject("source", "staged release.json");
        var commit = source.RequiredString("commit", "staged release source");
        Validation.Require(SourceCommit().IsMatch(commit) && release.RequiredString("gitCommit", "staged release.json") == commit, "Staged release source identity is invalid.");
        var configuration = release.RequiredObject("configuration", "staged release.json");
        var configurationSha = configuration.RequiredString("sha256", "staged release configuration");
        Validation.Require(Validation.IsSha256(configurationSha), "Staged release configuration SHA-256 is invalid.");
        var build = release.RequiredObject("buildIdentity", "staged release.json");
        Validation.Require(build.RequiredString("kind", "staged build identity") == "content-addressed-release-build", "Staged release build identity kind is invalid.");
        var identity = build.RequiredString("identity", "staged build identity");
        Validation.Require(ContentIdentity().IsMatch(identity), "Staged release build identity is not content addressed.");
        Validation.Require(JsonNode.DeepEquals(release["workflowIdentity"], build), "Staged release workflow/build identities disagree.");
        Validation.Require(build.RequiredString("configurationSha256", "staged build identity") == configurationSha, "Staged release configuration identities disagree.");
        var planSha = build.RequiredString("releasePlanSha256", "staged build identity");
        Validation.Require(Validation.IsSha256(planSha), "Staged release plan SHA-256 is invalid.");
        return new JsonObject
        {
            ["kind"] = "stark-staged-release-validation-subject",
            ["release"] = new JsonObject
            {
                ["buildIdentity"] = identity,
                ["configurationSha256"] = configurationSha,
                ["planSha256"] = planSha,
                ["runtimeIdentifier"] = rid,
                ["sourceCommit"] = commit,
                ["targetId"] = targetId,
                ["version"] = version,
            },
            ["releaseFiles"] = new JsonObject { ["bytes"] = manifestBytes.Length, ["entries"] = manifestEntries, ["sha256"] = manifestSha },
            ["releaseJson"] = new JsonObject { ["bytes"] = releaseBytes.Length, ["sha256"] = releaseSha },
            ["root"] = directory.Name,
            ["schemaVersion"] = 1,
        };
    }

    public static byte[] ReadStableFile(string path, string label)
    {
        var before = new FileInfo(path);
        Validation.Require(before.Exists && before.LinkTarget is null, $"{label} must be a real regular file: {path}");
        var length = before.Length;
        var writeTime = before.LastWriteTimeUtc;
        var bytes = File.ReadAllBytes(path);
        var after = new FileInfo(path);
        Validation.Require(after.Exists && after.LinkTarget is null && after.Length == length && after.LastWriteTimeUtc == writeTime && bytes.LongLength == length, $"{label} changed while it was read: {path}");
        return bytes;
    }

    public static int ValidateManifest(byte[] data, string releaseJsonSha256, string label)
    {
        string text;
        try { text = Encoding.ASCII.GetString(data); }
        catch (Exception exception) { throw new ReleaseToolException($"{label} release-files.sha256 is not ASCII.", exception); }
        Validation.Require(data.All(value => value <= 0x7f), $"{label} release-files.sha256 is not ASCII.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var releaseEntries = new List<string>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            lineNumber++;
            var normalized = line.EndsWith('\r') ? line[..^1] : line;
            var match = ManifestLine().Match(normalized);
            Validation.Require(match.Success, $"{label} release-files.sha256 line {lineNumber} is malformed.");
            var relative = match.Groups[2].Value;
            Validation.SafeRelativePath(relative, $"{label} release-files.sha256 line {lineNumber}");
            Validation.Require(seen.Add(relative) && relative != "release-files.sha256", $"{label} release-files.sha256 contains a duplicate or self checksum.");
            if (relative == "release.json") releaseEntries.Add(match.Groups[1].Value);
        }

        Validation.Require(seen.Count != 0 && releaseEntries.SequenceEqual([releaseJsonSha256]), $"{label} release-files.sha256 does not bind the exact release.json.");
        return seen.Count;
    }

    [GeneratedRegex("^(?:linux|macos|windows)-(?:x64|arm64)$")]
    private static partial Regex Target();

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex SourceCommit();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ContentIdentity();

    [GeneratedRegex("^([0-9a-f]{64})  (.+)$")]
    private static partial Regex ManifestLine();
}
