using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class JsonIO
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    public static JsonNode Load(string path, string label)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            RejectDuplicateProperties(bytes, label);
            return JsonNode.Parse(bytes) ?? throw new ReleaseToolException($"{label} is empty.");
        }
        catch (ReleaseToolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ReleaseToolException($"Could not read {label} '{path}': {exception.Message}", exception);
        }
    }

    public static JsonObject LoadObject(string path, string label)
        => Load(path, label) as JsonObject ?? throw new ReleaseToolException($"{label} must be a JSON object.");

    public static JsonObject ParseObject(ReadOnlySpan<byte> bytes, string label)
        => Parse(bytes, label) as JsonObject ?? throw new ReleaseToolException($"{label} must be a JSON object.");

    public static JsonNode Parse(ReadOnlySpan<byte> bytes, string label)
    {
        RejectDuplicateProperties(bytes, label);
        try
        {
            return JsonNode.Parse(bytes) ?? throw new ReleaseToolException($"{label} is empty.");
        }
        catch (JsonException exception)
        {
            throw new ReleaseToolException($"{label} is not valid JSON: {exception.Message}", exception);
        }
    }

    public static byte[] CanonicalBytes(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, node);
        }

        return stream.ToArray();
    }

    public static string Compact(JsonNode node) => Encoding.UTF8.GetString(CanonicalBytes(node));

    public static void Write(string path, JsonNode node, bool indented = true)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = node.ToJsonString(indented ? IndentedOptions : CompactOptions);
            File.WriteAllText(temporary, json + (indented ? "\n" : string.Empty), new UTF8Encoding(false));
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string RequiredString(this JsonObject value, string name, string context)
    {
        if (!value.TryGetPropertyValue(name, out var node) || node is null || node.GetValueKind() != JsonValueKind.String)
        {
            throw new ReleaseToolException($"{context}.{name} must be a string.");
        }

        var result = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ReleaseToolException($"{context}.{name} must not be empty.");
        }

        return result;
    }

    public static int RequiredInt(this JsonObject value, string name, string context)
    {
        if (!value.TryGetPropertyValue(name, out var node) || node is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out var result))
        {
            throw new ReleaseToolException($"{context}.{name} must be an integer.");
        }

        return result;
    }

    public static bool RequiredBool(this JsonObject value, string name, string context)
    {
        if (!value.TryGetPropertyValue(name, out var node) || node is not JsonValue jsonValue || !jsonValue.TryGetValue<bool>(out var result))
        {
            throw new ReleaseToolException($"{context}.{name} must be a boolean.");
        }

        return result;
    }

    public static JsonObject RequiredObject(this JsonObject value, string name, string context)
        => value[name] as JsonObject ?? throw new ReleaseToolException($"{context}.{name} must be an object.");

    public static JsonArray RequiredArray(this JsonObject value, string name, string context)
        => value[name] as JsonArray ?? throw new ReleaseToolException($"{context}.{name} must be an array.");

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes, string label)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        var stack = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    stack.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var properties = stack.Peek() ?? throw new ReleaseToolException($"{label} has a property outside an object.");
                    var name = reader.GetString()!;
                    if (!properties.Add(name))
                    {
                        throw new ReleaseToolException($"{label} contains duplicate JSON property '{name}'.");
                    }

                    break;
            }
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var property in value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var item in value)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
