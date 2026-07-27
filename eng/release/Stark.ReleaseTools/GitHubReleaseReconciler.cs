using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stark.ReleaseTools;

internal static partial class GitHubReleaseReconciler
{
    private sealed record DesiredAsset(string Name, long Size, string Digest, string Path);
    private sealed record RemoteAsset(long Id, string Name, string? State, long? Size, string? Digest);
    private sealed record Release(long Id, string Tag, bool Draft, bool Prerelease, string? Name, string TargetCommitish);

    public static async Task<JsonObject?> RunAsync(CommandLine command)
    {
        command.RejectUnknown("--mode", "--upload-directory", "--desired-draft", "--prerelease", "--release-name", "--tag", "--expected-commit", "--desired-directory", "--repository", "--api-url", "--output", "--github-output");
        var mode = command.Required("--mode");
        var repository = command.Optional("--repository", Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? string.Empty);
        var output = command.OptionalNullable("--output");
        JsonObject report;
        try
        {
            Validation.Require(repository.Length != 0, "--repository or GITHUB_REPOSITORY is required.");
            var upload = command.OptionalNullable("--upload-directory");
            Validation.Require(mode != "prune" || upload is not null, "--mode prune requires --upload-directory.");
            report = await ReconcileAsync(
                mode,
                command.Required("--tag"),
                command.Required("--expected-commit"),
                command.Required("--desired-directory"),
                repository,
                command.Optional("--api-url", Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com"),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN") ?? string.Empty,
                upload,
                ParseOptionalBool(command.OptionalNullable("--desired-draft"), "--desired-draft"),
                ParseOptionalBool(command.OptionalNullable("--prerelease"), "--prerelease"),
                command.OptionalNullable("--release-name"),
                output,
                handler: null);
        }
        catch (Exception exception)
        {
            report = output is not null && File.Exists(output)
                ? JsonIO.LoadObject(output, "publication reconciliation journal")
                : new JsonObject
                {
                    ["schemaVersion"] = 3,
                    ["status"] = "error",
                    ["mode"] = mode,
                    ["repository"] = repository,
                    ["tag"] = command.Optional("--tag"),
                    ["expectedCommit"] = command.Optional("--expected-commit"),
                };
            report["status"] = "error";
            report["error"] = exception.Message;
            if (output is not null) JsonIO.Write(output, report);
            throw;
        }

        if (output is not null) JsonIO.Write(output, report);
        WriteOutputs(command.OptionalNullable("--github-output") ?? Environment.GetEnvironmentVariable("GITHUB_OUTPUT"), report);
        return output is null ? report : null;
    }

    internal static async Task<JsonObject> ReconcileAsync(string mode, string tag, string expectedCommit, string desiredDirectory, string repository, string apiUrl, string token, string? uploadDirectory, bool? desiredDraft, bool? prerelease, string? releaseName, string? journalPath, HttpMessageHandler? handler = null)
    {
        Validation.Require(mode is "prune" or "verify" or "configure", $"Unsupported reconciliation mode '{mode}'.");
        Validation.Require(Tag().IsMatch(tag) && !tag.Contains("..", StringComparison.Ordinal), $"Unsafe GitHub Release tag '{tag}'.");
        var configurationValues = new object?[] { desiredDraft, prerelease, releaseName };
        Validation.Require(mode == "configure" ? configurationValues.All(value => value is not null) : configurationValues.All(value => value is null), "Release metadata is required only, and completely, in configure mode.");
        Validation.Require(releaseName is null || releaseName.Length != 0, "Desired release name must not be empty.");
        Validation.Require(mode == "prune" || uploadDirectory is null, "Upload subset output is accepted only in prune mode.");
        expectedCommit = RequiredCommit(expectedCommit, "expected source commit");
        var desired = DesiredAssets(desiredDirectory);
        if (uploadDirectory is not null)
        {
            var desiredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(desiredDirectory));
            var uploadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(uploadDirectory));
            Validation.Require(!Overlaps(desiredRoot, uploadRoot), "Desired asset directory and upload subset directory must not overlap.");
        }
        var desiredNames = desired.Select(asset => asset.Name).ToArray();
        using var api = new GitHubApi(apiUrl, repository, token, handler);
        var release = await api.FindReleaseAsync(tag);
        var sourceBinding = await BindSourceAsync(api, release, tag, expectedCommit);
        if (release is null)
        {
            Validation.Require(mode == "prune", $"GitHub Release for tag '{tag}' does not exist.");
            var report = BaseReport("release-absent", mode, repository, tag, expectedCommit, null, "absent", sourceBinding, desired, []);
            report["assetDifferences"] = Differences(desired, []);
            report["uploadAssets"] = new JsonArray(desiredNames.Select(value => (JsonNode?)value).ToArray());
            report["assetUploadRequired"] = true;
            report["releaseActionRequired"] = true;
            report["uploadRequired"] = true;
            MaterializeSubset(desired, uploadDirectory, desiredNames);
            return report;
        }

        var state = release.Draft ? "draft" : "published";
        var assets = await api.ListAssetsAsync(release.Id);
        var differences = Differences(desired, assets);
        var current = BaseReport("reconciling", mode, repository, tag, expectedCommit, release.Id, state, sourceBinding, desired, assets);
        current["releasePrerelease"] = release.Prerelease;
        current["releaseName"] = release.Name;
        current["assetDifferences"] = differences;

        if (mode is "verify" or "configure")
        {
            Validation.Require(!HasDifferences(differences), $"GitHub Release assets differ from the desired byte-exact set: {JsonIO.Compact(differences)}");
            if (mode == "verify")
            {
                current["status"] = $"verified-{state}";
                return current;
            }

            var metadataDifferences = new JsonObject();
            AddDifference("draft", release.Draft, desiredDraft!.Value);
            AddDifference("prerelease", release.Prerelease, prerelease!.Value);
            AddDifference("name", release.Name, releaseName!);
            var configuration = new JsonObject
            {
                ["requested"] = true,
                ["desiredDraft"] = desiredDraft,
                ["desiredPrerelease"] = prerelease,
                ["desiredName"] = releaseName,
                ["metadataDifferences"] = metadataDifferences,
                ["makesPublic"] = !desiredDraft.Value && release.Draft,
                ["performed"] = false,
            };
            current["configuration"] = configuration;
            if (!release.Draft)
            {
                Validation.Require(metadataDifferences.Count == 0, $"Published GitHub Release metadata is immutable and differs: {JsonIO.Compact(metadataDifferences)}");
                current["status"] = "published-exact";
                return current;
            }

            if (metadataDifferences.Count == 0)
            {
                current["status"] = "draft-metadata-exact";
                return current;
            }

            current["status"] = desiredDraft.Value ? "configuring-draft" : "finalizing";
            Journal(journalPath, current);
            var configured = await api.ConfigureReleaseAsync(release.Id, tag, desiredDraft.Value, prerelease.Value, releaseName!);
            configuration["performed"] = true;
            current["releaseState"] = configured.Draft ? "draft" : "published";
            current["releasePrerelease"] = configured.Prerelease;
            current["releaseName"] = configured.Name;
            var postBinding = await BindSourceAsync(api, configured, tag, expectedCommit);
            var postAssets = await api.ListAssetsAsync(release.Id);
            var postDifferences = Differences(desired, postAssets);
            current["postConfigurationSourceBinding"] = postBinding;
            current["postConfigurationAssetDetails"] = RemoteDetails(postAssets);
            current["postConfigurationAssetDifferences"] = postDifferences;
            Validation.Require(!HasDifferences(postDifferences), $"GitHub Release assets changed during configuration: {JsonIO.Compact(postDifferences)}");
            current["status"] = desiredDraft.Value ? "configured-draft" : "finalized";
            Journal(journalPath, current);
            return current;

            void AddDifference(string name, object? actual, object? expected)
            {
                if (!Equals(actual, expected)) metadataDifferences[name] = new JsonObject { ["actual"] = JsonValue.Create(actual), ["expected"] = JsonValue.Create(expected) };
            }
        }

        if (!release.Draft)
        {
            Validation.Require(!HasDifferences(differences), $"Published GitHub Release assets are immutable and differ: {JsonIO.Compact(differences)}");
            current["status"] = "published-exact";
            MaterializeSubset(desired, uploadDirectory, []);
            return current;
        }

        if (!HasDifferences(differences))
        {
            current["status"] = "draft-exact";
            MaterializeSubset(desired, uploadDirectory, []);
            return current;
        }

        var mismatched = differences.RequiredObject("mismatched", "asset differences").Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var deletions = Validation.Strings(differences["unexpected"], "unexpected assets").Concat(mismatched).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var uploads = Validation.Strings(differences["missing"], "missing assets").Concat(mismatched).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        current["plannedDeletions"] = new JsonArray(deletions.Select(value => (JsonNode?)value).ToArray());
        current["uploadAssets"] = new JsonArray(uploads.Select(value => (JsonNode?)value).ToArray());
        current["assetUploadRequired"] = uploads.Length != 0;
        current["releaseActionRequired"] = uploads.Length != 0;
        current["uploadRequired"] = uploads.Length != 0;
        current["status"] = deletions.Length != 0 ? "pruning" : "draft-incomplete";
        MaterializeSubset(desired, uploadDirectory, uploads);
        Journal(journalPath, current);
        var assetsByName = assets.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        var deleted = current.RequiredArray("deletedAssets", "reconciliation report");
        foreach (var name in deletions)
        {
            await api.DeleteAssetAsync(assetsByName[name].Id);
            deleted.Add(name);
            Journal(journalPath, current);
        }

        current["status"] = deletions.Length != 0 ? "pruned" : "draft-incomplete";
        return current;
    }

    private static JsonObject BaseReport(string status, string mode, string repository, string tag, string commit, long? releaseId, string state, JsonObject source, IReadOnlyList<DesiredAsset> desired, IReadOnlyList<RemoteAsset> remote)
        => new()
        {
            ["schemaVersion"] = 3,
            ["status"] = status,
            ["mode"] = mode,
            ["repository"] = repository,
            ["tag"] = tag,
            ["expectedCommit"] = commit,
            ["releaseId"] = releaseId,
            ["releaseState"] = state,
            ["sourceBinding"] = source,
            ["desiredAssets"] = new JsonArray(desired.Select(asset => (JsonNode?)asset.Name).ToArray()),
            ["desiredAssetDetails"] = DesiredDetails(desired),
            ["existingAssets"] = new JsonArray(remote.Select(asset => (JsonNode?)asset.Name).OrderBy(node => node!.GetValue<string>(), StringComparer.Ordinal).ToArray()),
            ["remoteAssetDetails"] = RemoteDetails(remote),
            ["plannedDeletions"] = new JsonArray(),
            ["deletedAssets"] = new JsonArray(),
            ["uploadAssets"] = new JsonArray(),
            ["assetUploadRequired"] = false,
            ["releaseActionRequired"] = false,
            ["uploadRequired"] = false,
        };

    private static List<DesiredAsset> DesiredAssets(string directory)
    {
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        Validation.Require(root.Exists && root.LinkTarget is null, $"Desired asset path is not a directory: {root.FullName}");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DesiredAsset>();
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            PortablePaths.ValidateSegment(entry.Name, "desired release asset", entry.Name);
            Validation.Require(entry is FileInfo && entry.Exists && entry.LinkTarget is null && names.Add(entry.Name), $"Desired asset directory contains a non-regular or case-colliding entry: {entry.Name}");
            var file = (FileInfo)entry;
            file.Refresh();
            var length = file.Length;
            var writeTime = file.LastWriteTimeUtc;
            var digest = $"sha256:{JsonIO.Sha256File(file.FullName)}";
            file.Refresh();
            Validation.Require(file.Exists && file.LinkTarget is null && file.Length == length && file.LastWriteTimeUtc == writeTime, $"Desired release asset changed while it was hashed: {entry.Name}");
            result.Add(new DesiredAsset(file.Name, length, digest, file.FullName));
        }

        Validation.Require(result.Count != 0, "Desired asset directory is empty.");
        return result.OrderBy(asset => asset.Name, StringComparer.Ordinal).ToList();
    }

    private static JsonObject Differences(IReadOnlyList<DesiredAsset> desired, IReadOnlyList<RemoteAsset> remote)
    {
        var desiredByName = desired.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        var remoteByName = new Dictionary<string, RemoteAsset>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in remote)
        {
            Validation.Require(remoteByName.TryAdd(asset.Name, asset) && folded.Add(asset.Name), "Existing GitHub Release contains duplicate or case-colliding assets.");
        }

        var missing = desiredByName.Keys.Except(remoteByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = remoteByName.Keys.Except(desiredByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var mismatched = new JsonObject();
        foreach (var name in desiredByName.Keys.Intersect(remoteByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var local = desiredByName[name];
            var uploaded = remoteByName[name];
            var reasons = new JsonArray();
            if (uploaded.State != "uploaded") reasons.Add($"state={uploaded.State ?? "null"}");
            if (uploaded.Size != local.Size) reasons.Add($"size={uploaded.Size?.ToString() ?? "null"}, expected={local.Size}");
            var digest = uploaded.Digest?.ToLowerInvariant();
            if (digest is null || !Digest().IsMatch(digest)) reasons.Add($"digest={uploaded.Digest ?? "null"}");
            else if (digest != local.Digest) reasons.Add($"digest={digest}, expected={local.Digest}");
            if (reasons.Count != 0) mismatched[name] = reasons;
        }

        return new JsonObject
        {
            ["missing"] = new JsonArray(missing.Select(value => (JsonNode?)value).ToArray()),
            ["unexpected"] = new JsonArray(unexpected.Select(value => (JsonNode?)value).ToArray()),
            ["mismatched"] = mismatched,
        };
    }

    private static bool HasDifferences(JsonObject differences)
        => differences.RequiredArray("missing", "asset differences").Count != 0 || differences.RequiredArray("unexpected", "asset differences").Count != 0 || differences.RequiredObject("mismatched", "asset differences").Count != 0;

    private static void MaterializeSubset(IReadOnlyList<DesiredAsset> desired, string? uploadDirectory, IReadOnlyCollection<string> names)
    {
        if (uploadDirectory is null) return;
        var destination = new DirectoryInfo(Path.GetFullPath(uploadDirectory));
        Validation.Require(destination.LinkTarget is null && (!destination.Exists || !destination.EnumerateFileSystemInfos().Any()), $"Upload subset directory must be absent or empty: {destination.FullName}");
        Directory.CreateDirectory(destination.FullName);
        var byName = desired.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var target = Path.Combine(destination.FullName, name);
            File.Copy(byName[name].Path, target);
            Validation.Require(new FileInfo(target).Length == byName[name].Size && $"sha256:{JsonIO.Sha256File(target)}" == byName[name].Digest, $"Materialized upload asset differs: {name}");
        }
    }

    private static async Task<JsonObject> BindSourceAsync(GitHubApi api, Release? release, string tag, string expectedCommit)
    {
        var tagCommit = await api.ResolveTagCommitAsync(tag);
        Validation.Require(tagCommit is null || tagCommit == expectedCommit, $"Git tag '{tag}' resolves to {tagCommit}, not expected {expectedCommit}.");
        string? targetCommitish = null;
        string? targetCommit = null;
        if (release is not null && release.Draft)
        {
            targetCommitish = release.TargetCommitish;
            Validation.Require(targetCommitish.ToLowerInvariant() == expectedCommit, "Existing draft target_commitish is not the exact expected source commit.");
            targetCommit = await api.ResolveCommitishAsync(targetCommitish);
            Validation.Require(targetCommit == expectedCommit, "Existing draft target_commitish resolves to the wrong commit.");
        }
        else if (release is not null)
        {
            Validation.Require(tagCommit is not null, $"Published GitHub Release tag '{tag}' has no Git tag ref.");
        }

        return new JsonObject { ["expectedCommit"] = expectedCommit, ["tagCommit"] = tagCommit, ["draftTargetCommitish"] = targetCommitish, ["draftTargetCommit"] = targetCommit };
    }

    private static JsonArray DesiredDetails(IReadOnlyList<DesiredAsset> assets)
        => new(assets.Select(asset => (JsonNode?)new JsonObject { ["name"] = asset.Name, ["size"] = asset.Size, ["digest"] = asset.Digest }).ToArray());

    private static JsonArray RemoteDetails(IReadOnlyList<RemoteAsset> assets)
        => new(assets.OrderBy(asset => asset.Name, StringComparer.Ordinal).Select(asset => (JsonNode?)new JsonObject { ["id"] = asset.Id, ["name"] = asset.Name, ["state"] = asset.State, ["size"] = asset.Size, ["digest"] = asset.Digest }).ToArray());

    private static void Journal(string? path, JsonObject report)
    {
        if (path is not null) JsonIO.Write(path, report);
    }

    private static void WriteOutputs(string? path, JsonObject report)
    {
        if (string.IsNullOrEmpty(path)) return;
        var lines = new[]
        {
            $"release_action_required={(report["releaseActionRequired"]?.GetValue<bool>() == true).ToString().ToLowerInvariant()}",
            $"asset_upload_required={(report["assetUploadRequired"]?.GetValue<bool>() == true).ToString().ToLowerInvariant()}",
            $"upload_required={(report["releaseActionRequired"]?.GetValue<bool>() == true).ToString().ToLowerInvariant()}",
            $"release_state={report["releaseState"]?.GetValue<string>() ?? "unknown"}",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    private static bool? ParseOptionalBool(string? value, string label)
    {
        if (value is null) return null;
        Validation.Require(value is "true" or "false", $"{label} must be true or false.");
        return value == "true";
    }

    private static string RequiredCommit(string value, string label)
    {
        var result = value.ToLowerInvariant();
        Validation.Require(Commit().IsMatch(result), $"{label} must be one full 40-character commit SHA.");
        return result;
    }

    private static bool Overlaps(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var leftPrefix = left + Path.DirectorySeparatorChar;
        var rightPrefix = right + Path.DirectorySeparatorChar;
        return left.Equals(right, comparison) || left.StartsWith(rightPrefix, comparison) || right.StartsWith(leftPrefix, comparison);
    }

    private sealed class GitHubApi : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _repository;

        public GitHubApi(string apiUrl, string repository, string token, HttpMessageHandler? handler)
        {
            Validation.Require(Repository().IsMatch(repository), $"Invalid GitHub repository identity: '{repository}'.");
            Validation.Require(token.Length != 0, "GITHUB_TOKEN or GH_TOKEN is required.");
            _repository = repository;
            var baseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            Validation.Require(baseAddress.Scheme == Uri.UriSchemeHttps, "GitHub API URL must use HTTPS.");
            _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("stark-release-asset-reconciler");
            _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        }

        public async Task<Release?> FindReleaseAsync(string tag)
        {
            var response = await SendAsync(HttpMethod.Get, $"repos/{_repository}/releases/tags/{Uri.EscapeDataString(tag)}", allowNotFound: true);
            if (response.Status != HttpStatusCode.NotFound) return ParseRelease(Parse(response.Body, "release lookup"), tag);
            var matches = new List<Release>();
            for (var page = 1; page <= 1000; page++)
            {
                var pageResponse = await SendAsync(HttpMethod.Get, $"repos/{_repository}/releases?per_page=100&page={page}");
                var array = Parse(pageResponse.Body, "release listing") as JsonArray ?? throw new ReleaseToolException("GitHub release listing is not an array.");
                foreach (var item in array.OfType<JsonObject>()) if (item["tag_name"]?.GetValue<string>() == tag) matches.Add(ParseRelease(item, tag));
                if (array.Count < 100) break;
                Validation.Require(page != 1000, "GitHub release pagination exceeded 1000 pages.");
            }

            Validation.Require(matches.Count <= 1, $"GitHub returned multiple releases for tag '{tag}'.");
            return matches.SingleOrDefault();
        }

        public async Task<string?> ResolveTagCommitAsync(string tag)
        {
            var response = await SendAsync(HttpMethod.Get, $"repos/{_repository}/git/ref/tags/{Uri.EscapeDataString(tag)}", allowNotFound: true);
            if (response.Status == HttpStatusCode.NotFound) return null;
            var document = Parse(response.Body, "tag ref") as JsonObject ?? throw new ReleaseToolException("GitHub tag ref is not an object.");
            Validation.Require(document["ref"]?.GetValue<string>() == $"refs/tags/{tag}", "GitHub tag lookup returned the wrong ref.");
            return await ResolveObjectAsync(document.RequiredObject("object", "GitHub tag ref"), []);
        }

        private async Task<string> ResolveObjectAsync(JsonObject value, HashSet<string> seen)
        {
            var type = value.RequiredString("type", "GitHub tag object");
            var sha = RequiredCommit(value.RequiredString("sha", "GitHub tag object"), "tag object SHA");
            if (type == "commit") return sha;
            Validation.Require(type == "tag" && seen.Count < 16 && seen.Add(sha), "GitHub annotated tag chain is cyclic, too deep, or unsupported.");
            var response = await SendAsync(HttpMethod.Get, $"repos/{_repository}/git/tags/{sha}");
            var document = Parse(response.Body, "annotated tag") as JsonObject ?? throw new ReleaseToolException("Annotated tag is not an object.");
            Validation.Require(RequiredCommit(document.RequiredString("sha", "annotated tag"), "annotated tag SHA") == sha, "Annotated tag lookup returned the wrong object.");
            return await ResolveObjectAsync(document.RequiredObject("object", "annotated tag"), seen);
        }

        public async Task<string> ResolveCommitishAsync(string value)
        {
            var response = await SendAsync(HttpMethod.Get, $"repos/{_repository}/commits/{Uri.EscapeDataString(value)}");
            var document = Parse(response.Body, "commitish") as JsonObject ?? throw new ReleaseToolException("GitHub commit lookup is not an object.");
            return RequiredCommit(document.RequiredString("sha", "commitish"), "resolved commit SHA");
        }

        public async Task<List<RemoteAsset>> ListAssetsAsync(long releaseId)
        {
            var result = new List<RemoteAsset>();
            for (var page = 1; page <= 1000; page++)
            {
                var response = await SendAsync(HttpMethod.Get, $"repos/{_repository}/releases/{releaseId}/assets?per_page=100&page={page}");
                var array = Parse(response.Body, "release assets") as JsonArray ?? throw new ReleaseToolException("Release asset response is not an array.");
                foreach (var item in array.OfType<JsonObject>())
                {
                    var id = item["id"]!.GetValue<long>();
                    var name = item.RequiredString("name", "release asset");
                    Validation.Require(id > 0, $"Release asset '{name}' has invalid id.");
                    result.Add(new RemoteAsset(id, name, item["state"]?.GetValue<string>(), item["size"]?.GetValue<long>(), item["digest"]?.GetValue<string>()));
                }

                if (array.Count < 100) break;
                Validation.Require(page != 1000, "GitHub release asset pagination exceeded 1000 pages.");
            }

            return result;
        }

        public async Task DeleteAssetAsync(long id)
        {
            var response = await SendAsync(HttpMethod.Delete, $"repos/{_repository}/releases/assets/{id}");
            Validation.Require(response.Status == HttpStatusCode.NoContent && response.Body.Length == 0, "GitHub release asset deletion returned unexpected content.");
        }

        public async Task<Release> ConfigureReleaseAsync(long id, string tag, bool draft, bool prerelease, string name)
        {
            var body = new JsonObject { ["draft"] = draft, ["prerelease"] = prerelease, ["name"] = name };
            var response = await SendAsync(HttpMethod.Patch, $"repos/{_repository}/releases/{id}", body);
            var release = ParseRelease(Parse(response.Body, "release configuration"), tag);
            Validation.Require(release.Id == id && release.Draft == draft && release.Prerelease == prerelease && release.Name == name, "GitHub release configuration returned the wrong state.");
            return release;
        }

        private async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(HttpMethod method, string path, JsonObject? json = null, bool allowNotFound = false)
        {
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(JsonIO.Compact(json), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode && !(allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                throw new ReleaseToolException($"GitHub API {method} {path} failed with HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes.Take(1000).ToArray())}");
            }

            return (response.StatusCode, bytes);
        }

        private static JsonNode Parse(byte[] body, string label)
        {
            try { return JsonIO.Parse(body, $"GitHub API {label}"); }
            catch (System.Text.Json.JsonException exception) { throw new ReleaseToolException($"GitHub API returned invalid JSON for {label}.", exception); }
        }

        private static Release ParseRelease(JsonNode node, string tag)
        {
            var document = node as JsonObject ?? throw new ReleaseToolException("GitHub release response is not an object.");
            Validation.Require(document.RequiredString("tag_name", "GitHub release") == tag, "GitHub release returned the wrong tag.");
            var id = document["id"]!.GetValue<long>();
            var draft = document["draft"]!.GetValue<bool>();
            var prerelease = document["prerelease"]!.GetValue<bool>();
            var name = document["name"]?.GetValue<string>();
            Validation.Require(id > 0, "GitHub release id is invalid.");
            return new Release(id, tag, draft, prerelease, name, document.RequiredString("target_commitish", "GitHub release"));
        }

        public void Dispose() => _client.Dispose();
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex Digest();
    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex Repository();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")]
    private static partial Regex Tag();
}
