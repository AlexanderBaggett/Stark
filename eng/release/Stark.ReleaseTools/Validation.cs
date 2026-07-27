using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static partial class Validation
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ReleaseToolException(message);
        }
    }

    public static string String(JsonNode? node, string context)
    {
        Require(node is JsonValue && node.GetValueKind() == System.Text.Json.JsonValueKind.String, $"{context} must be a non-empty string.");
        var value = node!.GetValue<string>();
        Require(!string.IsNullOrWhiteSpace(value), $"{context} must be a non-empty string.");
        return value;
    }

    public static string[] Strings(JsonNode? node, string context, bool nonEmpty = false)
    {
        Require(node is JsonArray, $"{context} must be an array.");
        var values = ((JsonArray)node!).Select((item, index) => String(item, $"{context}[{index}]")).ToArray();
        Require(!nonEmpty || values.Length != 0, $"{context} must not be empty.");
        Unique(values, context);
        return values;
    }

    public static void Unique(IEnumerable<string> values, string context)
    {
        var materialized = values.ToArray();
        Require(materialized.Length == materialized.Distinct(StringComparer.Ordinal).Count(), $"{context} contains duplicates.");
    }

    public static void SafeRelativePath(string value, string context)
    {
        Require(!string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && !value.Contains('\\') && !value.StartsWith('/') && !value.StartsWith('~'), $"{context} must be a safe relative path: '{value}'.");
        Require(!DrivePath().IsMatch(value), $"{context} must not be drive-qualified: '{value}'.");
        Require(value.Split('/').All(part => part is not ("" or "." or "..")), $"{context} is unsafe: '{value}'.");
    }

    public static bool IsSha256(string value) => Sha256().IsMatch(value);
    public static bool IsSha512(string value) => Sha512().IsMatch(value);
    public static bool IsIdentifier(string value) => Identifier().IsMatch(value);
    public static bool IsDependencyIdentifier(string value) => DependencyIdentifier().IsMatch(value);

    public static void NoPlaceholders(string name, JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value)
                {
                    NoPlaceholders(name, property.Value, string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}");
                }

                break;
            case JsonArray value:
                for (var index = 0; index < value.Count; index++)
                {
                    NoPlaceholders(name, value[index], $"{path}[{index}]");
                }

                break;
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                var text = value.GetValue<string>();
                Require(!Placeholder().IsMatch(text), $"{name}.{path} contains an unresolved placeholder.");
                if (path.EndsWith("sha256", StringComparison.OrdinalIgnoreCase))
                {
                    Require(IsSha256(text), $"{name}.{path} is not a SHA-256 digest.");
                }

                break;
        }
    }

    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex DrivePath();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256();

    [GeneratedRegex("^[0-9a-fA-F]{128}$")]
    private static partial Regex Sha512();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[a-z][a-z0-9.-]*$")]
    private static partial Regex DependencyIdentifier();

    [GeneratedRegex(@"(?:\bTODO\b|\bTBD\b|REPLACE[-_ ]?ME|<[^<>]+>|\$\{[^{}]+\})", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();
}
