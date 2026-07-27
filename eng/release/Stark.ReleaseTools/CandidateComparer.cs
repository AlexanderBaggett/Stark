using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class CandidateComparer
{
    private sealed record Entry(string Path, int Occurrence, string Type, long? Size, string? Sha256, string? LinkTarget, JsonObject Metadata)
    {
        public JsonObject ToJson() => new()
        {
            ["path"] = Path,
            ["occurrence"] = Occurrence,
            ["type"] = Type,
            ["size"] = Size,
            ["sha256"] = Sha256,
            ["linkTarget"] = LinkTarget,
            ["metadata"] = Metadata.DeepClone(),
        };

        public JsonObject ContentJson() => new()
        {
            ["path"] = Path,
            ["occurrence"] = Occurrence,
            ["type"] = Type,
            ["size"] = Size,
            ["sha256"] = Sha256,
            ["linkTarget"] = LinkTarget,
        };
    }

    public static JsonObject? Run(CommandLine command)
    {
        command.RejectUnknown("--candidate-a", "--candidate-b", "--label-a", "--label-b", "--output", "--allow-differences");
        var report = Compare(
            Inventory(command.Required("--candidate-a"), command.Optional("--label-a", "candidate-a")),
            Inventory(command.Required("--candidate-b"), command.Optional("--label-b", "candidate-b")));
        var output = command.OptionalNullable("--output");
        if (output is null)
        {
            Console.Out.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            JsonIO.Write(output, report);
        }

        var equal = report.RequiredObject("result", "comparison report").RequiredBool("deterministicEqual", "comparison result");
        Console.Error.WriteLine(equal
            ? "Release candidates are deterministically equal under the recorded evidence policy."
            : "Release candidates differ; inspect the categorized evidence in the report.");
        if (!equal && !command.HasFlag("--allow-differences"))
        {
            throw new ReleaseToolException("Release candidates are not deterministically equal.");
        }

        return null;
    }

    internal static JsonObject Inventory(string candidatePath, string label)
    {
        var fullPath = Path.GetFullPath(candidatePath);
        List<Entry> entries;
        string kind;
        string? format = null;
        JsonObject? container = null;
        if (Directory.Exists(fullPath) && new DirectoryInfo(fullPath).LinkTarget is null)
        {
            kind = "directory";
            entries = InventoryDirectory(fullPath);
        }
        else if (File.Exists(fullPath) && new FileInfo(fullPath).LinkTarget is null)
        {
            var before = new FileInfo(fullPath);
            var bytes = before.Length;
            var write = before.LastWriteTimeUtc;
            var sha = JsonIO.Sha256File(fullPath);
            if (IsZip(fullPath))
            {
                using (var zip = ZipFile.OpenRead(fullPath))
                {
                    kind = "archive";
                    format = "zip";
                    entries = InventoryZip(zip);
                    container = new JsonObject
                    {
                        ["bytes"] = bytes,
                        ["sha256"] = sha,
                        ["metadata"] = new JsonObject { ["commentBase64"] = Convert.ToBase64String(zip.Comment is null ? [] : System.Text.Encoding.UTF8.GetBytes(zip.Comment)) },
                    };
                }
            }
            else
            {
                try
                {
                    entries = InventoryTar(fullPath);
                    kind = "archive";
                    format = "tar";
                    container = new JsonObject
                    {
                        ["bytes"] = bytes,
                        ["sha256"] = sha,
                        ["metadata"] = TarContainerMetadata(fullPath),
                    };
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                {
                    throw new ReleaseToolException($"Candidate is neither a directory, ZIP, nor TAR archive: '{fullPath}'.", exception);
                }
            }

            before.Refresh();
            Validation.Require(before.Length == bytes && before.LastWriteTimeUtc == write && JsonIO.Sha256File(fullPath) == sha, $"Archive changed while it was inventoried: '{fullPath}'.");
        }
        else
        {
            throw new ReleaseToolException($"Candidate does not exist or is not a regular file/directory: '{fullPath}'.");
        }

        entries.Sort((left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Path, right.Path);
            return comparison != 0 ? comparison : left.Occurrence.CompareTo(right.Occurrence);
        });
        var entryNodes = new JsonArray(entries.Select(entry => (JsonNode?)entry.ToJson()).ToArray());
        var contentNodes = new JsonArray(entries.Select(entry => (JsonNode?)entry.ContentJson()).ToArray());
        return new JsonObject
        {
            ["label"] = label,
            ["path"] = fullPath,
            ["kind"] = kind,
            ["archiveFormat"] = format,
            ["container"] = container,
            ["inventory"] = new JsonObject
            {
                ["entryCount"] = entries.Count,
                ["contentSha256"] = JsonIO.Sha256(JsonIO.CanonicalBytes(contentNodes)),
                ["evidenceSha256"] = JsonIO.Sha256(JsonIO.CanonicalBytes(entryNodes)),
                ["ambiguities"] = Ambiguities(entries),
                ["entries"] = entryNodes,
            },
        };
    }

    internal static JsonObject Compare(JsonObject candidateA, JsonObject candidateB)
    {
        var entriesA = Entries(candidateA);
        var entriesB = Entries(candidateB);
        var keys = entriesA.Keys.Union(entriesB.Keys).OrderBy(key => key.Path, StringComparer.Ordinal).ThenBy(key => key.Occurrence).ToArray();
        var onlyA = new JsonArray();
        var onlyB = new JsonArray();
        var types = new JsonArray();
        var content = new JsonArray();
        var metadata = new JsonArray();
        foreach (var key in keys)
        {
            if (!entriesA.TryGetValue(key, out var left)) { onlyB.Add(entriesB[key].DeepClone()); continue; }
            if (!entriesB.TryGetValue(key, out var right)) { onlyA.Add(left.DeepClone()); continue; }
            var identity = new JsonObject { ["path"] = key.Path, ["occurrence"] = key.Occurrence };
            if (left.RequiredString("type", "candidate entry") != right.RequiredString("type", "candidate entry"))
            {
                identity["candidateA"] = left["type"]!.DeepClone();
                identity["candidateB"] = right["type"]!.DeepClone();
                types.Add(identity);
                continue;
            }

            var leftFacts = ContentFacts(left);
            var rightFacts = ContentFacts(right);
            if (!JsonNode.DeepEquals(leftFacts, rightFacts)) content.Add(Difference(identity, leftFacts, rightFacts));
            if (!JsonNode.DeepEquals(left["metadata"], right["metadata"])) metadata.Add(Difference(identity, left["metadata"]!, right["metadata"]!));
        }

        var kindDifference = DifferenceOrNull(candidateA["kind"], candidateB["kind"]);
        var formatDifference = DifferenceOrNull(candidateA["archiveFormat"], candidateB["archiveFormat"]);
        var containerDifference = DifferenceOrNull(candidateA["container"], candidateB["container"]);
        bool? archiveBytesEqual = candidateA["container"] is null && candidateB["container"] is null
            ? null
            : candidateA["container"] is JsonObject leftContainer && candidateB["container"] is JsonObject rightContainer &&
              leftContainer["bytes"]!.GetValue<long>() == rightContainer["bytes"]!.GetValue<long>() &&
              leftContainer.RequiredString("sha256", "candidate container") == rightContainer.RequiredString("sha256", "candidate container");
        var payloadEqual = onlyA.Count == 0 && onlyB.Count == 0 && types.Count == 0 && content.Count == 0;
        var metadataEqual = payloadEqual && metadata.Count == 0;
        var sameForm = kindDifference is null && formatDifference is null;
        var bothArchives = candidateA.RequiredString("kind", "candidate A") == "archive" && candidateB.RequiredString("kind", "candidate B") == "archive";
        var deterministic = sameForm && metadataEqual && (!bothArchives || archiveBytesEqual == true);
        var categories = new JsonArray();
        AddCategory("candidate-kind", kindDifference is not null);
        AddCategory("candidate-format", formatDifference is not null);
        AddCategory("payload-inventory", onlyA.Count != 0 || onlyB.Count != 0);
        AddCategory("entry-type", types.Count != 0);
        AddCategory("entry-content", content.Count != 0);
        AddCategory("entry-metadata", metadata.Count != 0);
        AddCategory("archive-container", containerDifference is not null);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["reportKind"] = "stark-release-reproducibility-comparison",
            ["comparisonPolicy"] = new JsonObject
            {
                ["pathOrdering"] = "UTF-16 code-unit ordinal, then exact duplicate occurrence",
                ["pathNormalization"] = "none",
                ["archiveExtraction"] = false,
                ["archiveContainerBytesHashed"] = true,
                ["entryMetadataCompared"] = true,
                ["stageRootEntryIncluded"] = false,
                ["filesystemStorageIdentityExcluded"] = new JsonArray("device", "inode", "ctime"),
                ["differenceDisposition"] = "No difference is automatically classified as unavoidable; every reported difference requires review.",
            },
            ["candidateA"] = candidateA,
            ["candidateB"] = candidateB,
            ["result"] = new JsonObject { ["deterministicEqual"] = deterministic, ["payloadContentEqual"] = payloadEqual, ["entryMetadataEqual"] = metadataEqual, ["archiveBytesEqual"] = archiveBytesEqual, ["differenceCategories"] = categories },
            ["differences"] = new JsonObject
            {
                ["candidateKind"] = kindDifference,
                ["candidateFormat"] = formatDifference,
                ["onlyInCandidateA"] = onlyA,
                ["onlyInCandidateB"] = onlyB,
                ["entryType"] = types,
                ["entryContent"] = content,
                ["entryMetadata"] = metadata,
                ["archiveContainer"] = containerDifference,
                ["counts"] = new JsonObject { ["onlyInCandidateA"] = onlyA.Count, ["onlyInCandidateB"] = onlyB.Count, ["entryType"] = types.Count, ["entryContent"] = content.Count, ["entryMetadata"] = metadata.Count, ["archiveContainer"] = containerDifference is null ? 0 : 1 },
            },
        };

        void AddCategory(string name, bool present) { if (present) categories.Add(name); }
    }

    private static List<Entry> InventoryDirectory(string root)
    {
        var result = new List<Entry>();
        Visit(new DirectoryInfo(root));
        return result;

        void Visit(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var metadata = new JsonObject { ["mtimeUtcTicks"] = entry.LastWriteTimeUtc.Ticks, ["attributes"] = (int)entry.Attributes };
                if (!OperatingSystem.IsWindows())
                {
                    try { metadata["unixMode"] = (int)File.GetUnixFileMode(entry.FullName); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                if (entry.LinkTarget is { } target)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(target);
                    result.Add(new Entry(relative, 0, "symbolic-link", bytes.Length, JsonIO.Sha256(bytes), target, metadata));
                }
                else if (entry is DirectoryInfo child)
                {
                    result.Add(new Entry(relative, 0, "directory", null, null, null, metadata));
                    Visit(child);
                }
                else if (entry is FileInfo file)
                {
                    var length = file.Length;
                    var write = file.LastWriteTimeUtc;
                    var sha = JsonIO.Sha256File(file.FullName);
                    file.Refresh();
                    Validation.Require(file.Length == length && file.LastWriteTimeUtc == write, $"File changed while it was inventoried: '{file.FullName}'.");
                    result.Add(new Entry(relative, 0, "file", length, sha, null, metadata));
                }
                else throw new ReleaseToolException($"Unsupported filesystem entry '{entry.FullName}'.");
            }
        }
    }

    private static List<Entry> InventoryZip(ZipArchive archive)
    {
        var result = new List<Entry>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var occurrence = Next(counts, entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xffff;
            var directory = entry.FullName.EndsWith('/') || (mode & 0xf000) == 0x4000;
            long? size = null;
            string? sha = null;
            if (!directory)
            {
                using var stream = entry.Open();
                (size, sha) = Hash(stream);
                Validation.Require(size == entry.Length, $"ZIP entry size changed while reading '{entry.FullName}'.");
            }
            var type = directory ? "directory" : (mode & 0xf000) == 0xa000 ? "symbolic-link" : "file";
            result.Add(new Entry(entry.FullName, occurrence, type, size, sha, null, new JsonObject
            {
                ["sourceIndex"] = index,
                ["lastWriteTime"] = entry.LastWriteTime.ToString("O"),
                ["compressedBytes"] = entry.CompressedLength,
                ["declaredBytes"] = entry.Length,
                ["externalAttributes"] = entry.ExternalAttributes,
            }));
        }
        return result;
    }

    private static List<Entry> InventoryTar(string path)
    {
        using var file = File.OpenRead(path);
        Stream input = file;
        if (file.ReadByte() == 0x1f && file.ReadByte() == 0x8b)
        {
            file.Position = 0;
            input = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        }
        else file.Position = 0;
        using (input == file ? null : input)
        using (var reader = new TarReader(input, leaveOpen: true))
        {
            var result = new List<Entry>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            TarEntry? entry;
            var index = 0;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                var type = TarType(entry.EntryType);
                long? size = null;
                string? sha = null;
                string? link = type is "symbolic-link" or "hard-link" ? entry.LinkName : null;
                if (type == "file")
                {
                    Validation.Require(entry.DataStream is not null, $"Could not read TAR entry '{entry.Name}'.");
                    (size, sha) = Hash(entry.DataStream!);
                    Validation.Require(size == entry.Length, $"TAR entry size changed while reading '{entry.Name}'.");
                }
                result.Add(new Entry(entry.Name, Next(counts, entry.Name), type, size, sha, link, new JsonObject
                {
                    ["sourceIndex"] = index++,
                    ["entryType"] = entry.EntryType.ToString(),
                    ["mode"] = (int)entry.Mode,
                    ["mtime"] = entry.ModificationTime.ToUniversalTime().ToString("O"),
                    ["uid"] = entry.Uid,
                    ["gid"] = entry.Gid,
                    ["declaredBytes"] = entry.Length,
                }));
            }
            return result;
        }
    }

    private static JsonObject TarContainerMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[10];
        var read = stream.Read(header);
        if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b)
        {
            Validation.Require(read == 10, $"Truncated gzip header in '{path}'.");
            return new JsonObject
            {
                ["compression"] = "gzip",
                ["gzipHeader"] = new JsonObject { ["compressionMethod"] = header[2], ["flags"] = header[3], ["mtime"] = BitConverter.ToUInt32(header[4..8]), ["extraFlags"] = header[8], ["operatingSystem"] = header[9] },
            };
        }
        return new JsonObject { ["compression"] = "none" };
    }

    private static JsonObject Ambiguities(IEnumerable<Entry> entries)
    {
        var exact = new JsonArray(entries.GroupBy(entry => entry.Path, StringComparer.Ordinal).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => (JsonNode?)new JsonObject { ["path"] = group.Key, ["count"] = group.Count() }).ToArray());
        var collisions = new JsonArray(entries.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()).Where(paths => paths.Length > 1).OrderBy(paths => paths[0], StringComparer.Ordinal).Select(paths => (JsonNode?)new JsonArray(paths.Select(path => (JsonNode?)path).ToArray())).ToArray());
        return new JsonObject { ["exactDuplicates"] = exact, ["caseCollisions"] = collisions };
    }

    private static Dictionary<(string Path, int Occurrence), JsonObject> Entries(JsonObject candidate)
        => candidate.RequiredObject("inventory", "candidate").RequiredArray("entries", "candidate inventory").OfType<JsonObject>().ToDictionary(entry => (entry.RequiredString("path", "candidate entry"), entry.RequiredInt("occurrence", "candidate entry")));

    private static JsonObject ContentFacts(JsonObject entry) => new() { ["size"] = entry["size"]?.DeepClone(), ["sha256"] = entry["sha256"]?.DeepClone(), ["linkTarget"] = entry["linkTarget"]?.DeepClone() };
    private static JsonObject Difference(JsonObject identity, JsonNode left, JsonNode right) { var result = (JsonObject)identity.DeepClone(); result["candidateA"] = left.DeepClone(); result["candidateB"] = right.DeepClone(); return result; }
    private static JsonObject? DifferenceOrNull(JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(left, right) ? null : new JsonObject { ["candidateA"] = left?.DeepClone(), ["candidateB"] = right?.DeepClone() };
    private static int Next(Dictionary<string, int> counts, string path) { var value = counts.GetValueOrDefault(path); counts[path] = value + 1; return value; }
    private static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == 4 && signature[0] == (byte)'P' && signature[1] == (byte)'K' &&
            ((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6) || (signature[2] == 7 && signature[3] == 8));
    }

    private static (long Size, string Sha256) Hash(Stream stream)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long size = 0;
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            digest.AppendData(buffer, 0, read);
            size += read;
        }
        return (size, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }
    private static string TarType(TarEntryType type) => type switch { TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => "file", TarEntryType.Directory => "directory", TarEntryType.SymbolicLink => "symbolic-link", TarEntryType.HardLink => "hard-link", TarEntryType.Fifo => "fifo", TarEntryType.CharacterDevice => "character-device", TarEntryType.BlockDevice => "block-device", _ => $"other:{type}" };
}
