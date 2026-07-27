using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stark.ReleaseTools;

internal static partial class ReleasePlanPreparer
{
    public static JsonNode? Run(CommandLine command)
    {
        command.RejectUnknown(
            "--root", "--event-name", "--resolved-commit", "--github-ref", "--github-ref-name", "--github-sha",
            "--input-version", "--input-ref", "--input-commit", "--input-targets", "--input-publish",
            "--input-draft", "--input-prerelease", "--require-release-tool", "--plan-output", "--github-output");
        var root = Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory()));
        var configuration = ReleaseConfiguration.Validate(root);
        var tool = ReleaseToolIdentity.Current();
        if (command.HasFlag("--require-release-tool"))
        {
            Validation.Require(tool.RequiredBool("matchesPolicy", "release tool identity"), "Release execution requires Stark.ReleaseTools under the exact pinned .NET SDK/runtime policy.");
        }

        var eventName = command.Required("--event-name");
        var resolved = ValidateCommit(command.Required("--resolved-commit"), "resolved commit");
        Validation.Require(eventName is "workflow_dispatch" or "push", $"Unsupported release event '{eventName}'.");
        string version;
        string requestedRef;
        string? expected;
        List<string> targetIds;
        bool publish;
        bool draft;
        bool prerelease;
        if (eventName == "workflow_dispatch")
        {
            version = ValidateVersion(command.Optional("--input-version"));
            requestedRef = ValidateRef(command.Optional("--input-ref"));
            targetIds = SelectTargets(command.Optional("--input-targets"), configuration.Targets);
            publish = ParseBool(command.Optional("--input-publish"), "publish input");
            draft = ParseBool(command.Optional("--input-draft"), "draft input");
            prerelease = ParseBool(command.Optional("--input-prerelease"), "prerelease input");
            var requestedCommit = command.Optional("--input-commit").Trim();
            expected = requestedCommit.Length != 0 ? ValidateCommit(requestedCommit, "expected commit") : FullCommit().IsMatch(requestedRef) ? requestedRef.ToLowerInvariant() : null;
            if (publish)
            {
                Validation.Require(expected is not null, "Publishing a manually dispatched release requires an expected commit or a full commit SHA as the requested ref.");
            }
        }
        else
        {
            var githubRef = command.Optional("--github-ref");
            Validation.Require(githubRef.StartsWith("refs/tags/", StringComparison.Ordinal), "Automatic release push must be a tag ref.");
            version = ValidateVersion(command.Optional("--github-ref-name"));
            requestedRef = ValidateRef(githubRef);
            expected = ValidateCommit(command.Optional("--github-sha"), "GitHub event commit");
            targetIds = SelectTargets("all", configuration.Targets);
            publish = true;
            draft = false;
            prerelease = version.Contains('-');
        }

        if (expected is not null)
        {
            Validation.Require(resolved == expected, $"Resolved commit {resolved} does not match expected commit {expected}.");
        }

        var enabled = configuration.Targets.Where(target => target.ReleaseEnabled).Select(target => target.Id).ToArray();
        if (publish)
        {
            Validation.Require(targetIds.SequenceEqual(enabled), "Publication requires every release-enabled target; target subsets are diagnostic builds only.");
        }

        var complete = ReleaseConfiguration.GenerateMatrix(configuration, includePlanned: false).RequiredArray("include", "release matrix");
        var selected = targetIds.ToHashSet(StringComparer.Ordinal);
        var include = new JsonArray(complete.OfType<JsonObject>().Where(entry => selected.Contains(entry.RequiredString("target_id", "release matrix entry"))).Select(entry => entry.DeepClone()).ToArray());
        Validation.Require(include.Count == targetIds.Count && include.Count != 0, "Selected target matrix is empty or incomplete.");
        var targetArray = new JsonArray(targetIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var warningArray = new JsonArray(configuration.Warnings.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var plan = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["eventName"] = eventName,
            ["version"] = version,
            ["requestedRef"] = requestedRef,
            ["expectedCommit"] = expected,
            ["resolvedCommit"] = resolved,
            ["targetIds"] = targetArray,
            ["publish"] = publish,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["releaseTool"] = tool,
            ["matrix"] = new JsonObject { ["include"] = include },
            ["configurationWarnings"] = warningArray,
        };
        var planOutput = command.OptionalNullable("--plan-output");
        if (planOutput is not null) JsonIO.Write(planOutput, plan);
        var githubOutput = command.OptionalNullable("--github-output");
        if (githubOutput is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(githubOutput))!);
            var lines = new[]
            {
                $"matrix={JsonIO.Compact(plan.RequiredObject("matrix", "release plan"))}",
                $"version={version}", $"source_ref={requestedRef}", $"commit_sha={resolved}",
                $"target_ids={string.Join(',', targetIds)}", $"publish={publish.ToString().ToLowerInvariant()}",
                $"draft={draft.ToString().ToLowerInvariant()}", $"prerelease={prerelease.ToString().ToLowerInvariant()}",
            };
            File.AppendAllText(githubOutput, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        }

        Console.Out.WriteLine($"Prepared {version} from {resolved} for {string.Join(", ", targetIds)} (publish={publish.ToString().ToLowerInvariant()}, draft={draft.ToString().ToLowerInvariant()}, prerelease={prerelease.ToString().ToLowerInvariant()}).");
        foreach (var warning in configuration.Warnings) Console.Error.WriteLine($"warning: {warning}");
        return null;
    }

    private static bool ParseBool(string value, string name)
    {
        Validation.Require(value.Trim() is "true" or "false", $"{name} must be exactly true or false.");
        return value.Trim() == "true";
    }

    private static string ValidateVersion(string value)
    {
        var result = value.Trim();
        Validation.Require(PortableVersion().IsMatch(result), $"Release version '{value}' is not a portable path segment.");
        return result;
    }

    private static string ValidateRef(string value)
    {
        var result = value.Trim();
        Validation.Require(SafeGitRef().IsMatch(result) && !result.StartsWith('-') && !result.Contains("..", StringComparison.Ordinal) && !result.Contains("//", StringComparison.Ordinal) && !result.Contains("@{", StringComparison.Ordinal) && !result.EndsWith('/') && !result.EndsWith('.') && !result.EndsWith(".lock", StringComparison.Ordinal), $"Requested ref '{value}' is unsafe.");
        return result;
    }

    private static string ValidateCommit(string value, string name)
    {
        var result = value.Trim().ToLowerInvariant();
        Validation.Require(FullCommit().IsMatch(result), $"{name} must be a full 40-character hexadecimal commit SHA.");
        return result;
    }

    private static List<string> SelectTargets(string selection, IReadOnlyList<ReleaseTarget> targets)
    {
        var requested = selection.Trim();
        Validation.Require(requested.Length != 0, "Target selection must be 'all' or a comma-separated list of enabled target IDs.");
        var enabled = targets.Where(target => target.ReleaseEnabled).Select(target => target.Id).ToArray();
        if (requested == "all") return [.. enabled];
        var pieces = requested.Split(',').Select(piece => piece.Trim()).ToArray();
        Validation.Require(pieces.All(piece => piece.Length != 0) && pieces.Length == pieces.Distinct(StringComparer.Ordinal).Count(), "Target selection contains an empty or duplicate target ID.");
        var all = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        Validation.Require(pieces.All(all.Contains), $"Target selection contains unknown target(s): {string.Join(", ", pieces.Where(piece => !all.Contains(piece)))}");
        Validation.Require(pieces.All(enabled.Contains), $"Target selection contains release-disabled target(s): {string.Join(", ", pieces.Where(piece => !enabled.Contains(piece)))}");
        var selected = pieces.ToHashSet(StringComparer.Ordinal);
        return enabled.Where(selected.Contains).ToList();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")]
    private static partial Regex PortableVersion();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/+-]{0,255}$")]
    private static partial Regex SafeGitRef();

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex FullCommit();
}
