using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class ArchiveExtractor
{
    private const int MaximumEntries = 100_000;
    private const long MaximumFileBytes = 1L << 30;
    private const long MaximumTotalBytes = 4L << 30;

    private sealed record Record(int Index, string Path, string Kind, long Size, int Mode, string Target, string SourceName)
    {
        public string ResolvedTarget { get; set; } = string.Empty;
        public string UltimateTarget { get; set; } = string.Empty;
    }

    private sealed record TarPathRecord(string HeaderName, string LogicalName);

    public static JsonObject Extract(CommandLine command)
    {
        command.RejectUnknown("--archive", "--kind", "--destination", "--required-root", "--label");
        var archive = Path.GetFullPath(command.Required("--archive"));
        var kind = command.Required("--kind");
        var destination = Path.GetFullPath(command.Required("--destination"));
        var requiredRootValue = command.Optional("--required-root");
        var requiredRoot = string.IsNullOrEmpty(requiredRootValue) ? string.Empty : PortablePaths.Validate(requiredRootValue, "required archive root");
        var label = command.Optional("--label", "build-tool archive");

        if (Directory.Exists(destination) || File.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null)
        {
            throw new ReleaseToolException($"Destination '{destination}' must not already exist.");
        }

        var records = kind switch
        {
            "zip" => ReadZipRecords(archive, requiredRoot, label),
            "targz" => ReadTarRecords(archive, requiredRoot, label),
            _ => throw new ReleaseToolException($"Unsupported archive kind '{kind}'."),
        };
        ValidateRecords(records, requiredRoot, label);

        Directory.CreateDirectory(destination);
        try
        {
            ExtractRecords(kind, archive, destination, records, requiredRoot, label);
            return InventoryTree(destination);
        }
        catch
        {
            try
            {
                Directory.Delete(destination, recursive: true);
            }
            catch
            {
                // Preserve the primary security/extraction error.
            }

            throw;
        }
    }

    public static JsonObject Inventory(CommandLine command)
    {
        command.RejectUnknown("--root");
        return InventoryTree(Path.GetFullPath(command.Required("--root")));
    }

    public static JsonObject InventoryTree(string root)
    {
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
        {
            throw new ReleaseToolException($"Inventory root '{root}' is not a regular directory.");
        }

        var records = new List<JsonObject>();
        Visit(rootInfo);
        records.Sort((left, right) => StringComparer.Ordinal.Compare(left["path"]!.GetValue<string>(), right["path"]!.GetValue<string>()));
        using var digestInput = new MemoryStream();
        foreach (var record in records)
        {
            var bytes = JsonIO.CanonicalBytes(record);
            digestInput.Write(bytes);
            digestInput.WriteByte((byte)'\n');
        }

        var files = records.Where(record => record["kind"]!.GetValue<string>() == "file").ToArray();
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["fileCount"] = files.Length,
            ["logicalBytes"] = files.Sum(record => record["bytes"]!.GetValue<long>()),
            ["directoryCount"] = records.Count(record => record["kind"]!.GetValue<string>() == "directory"),
            ["symlinkCount"] = records.Count(record => record["kind"]!.GetValue<string>() == "symlink"),
            ["treeSha256"] = JsonIO.Sha256(digestInput.ToArray()),
        };

        void Visit(DirectoryInfo directory)
        {
            var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!child.Name.All(character => character <= 0x7f) || !folded.Add(child.Name))
                {
                    throw new ReleaseToolException($"Build-tool tree has unsafe or case-colliding name '{child.Name}'.");
                }

                var relative = Path.GetRelativePath(rootInfo.FullName, child.FullName).Replace(Path.DirectorySeparatorChar, '/');
                PortablePaths.Validate(relative, "build-tool tree entry");
                child.Refresh();
                if (child.LinkTarget is { } target)
                {
                    PortablePaths.ResolveLinkTarget(relative, target, string.Empty, "build-tool tree");
                    var resolved = child.ResolveLinkTarget(true) ?? throw new ReleaseToolException($"Build-tool tree symbolic link '{relative}' is dangling.");
                    EnsureInside(rootInfo.FullName, resolved.FullName, $"Build-tool tree symbolic link '{relative}'");
                    records.Add(new JsonObject { ["kind"] = "symlink", ["path"] = relative, ["target"] = target });
                }
                else if (child is DirectoryInfo childDirectory)
                {
                    records.Add(new JsonObject { ["kind"] = "directory", ["path"] = relative });
                    Visit(childDirectory);
                }
                else if (child is FileInfo file)
                {
                    var length = file.Length;
                    var writeTime = file.LastWriteTimeUtc;
                    var sha256 = JsonIO.Sha256File(file.FullName);
                    file.Refresh();
                    Validation.Require(file.Exists && file.LinkTarget is null && file.Length == length && file.LastWriteTimeUtc == writeTime, $"Build-tool tree file changed while it was inventoried: '{relative}'.");
                    records.Add(new JsonObject
                    {
                        ["kind"] = "file",
                        ["path"] = relative,
                        ["bytes"] = length,
                        ["sha256"] = sha256,
                        ["executable"] = IsExecutable(file.FullName),
                    });
                }
                else
                {
                    throw new ReleaseToolException($"Build-tool tree entry '{relative}' has a forbidden filesystem type.");
                }
            }
        }
    }

    private static List<Record> ReadZipRecords(string archivePath, string requiredRoot, string label)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var records = new List<Record>(archive.Entries.Count);
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var path = PortablePaths.Validate(entry.FullName, $"{label} ZIP entry", directory);
            ValidateRequiredRoot(path, requiredRoot, label);
            var attributes = unchecked((uint)entry.ExternalAttributes);
            var mode = (int)((attributes >> 16) & 0xffff);
            var fileType = mode & 0xf000;
            if ((attributes & 0x0400) != 0 || fileType == 0xa000)
            {
                throw new ReleaseToolException($"{label} ZIP entry '{path}' is a forbidden link or reparse point.");
            }

            if (directory)
            {
                if (fileType is not (0 or 0x4000) || entry.Length != 0)
                {
                    throw new ReleaseToolException($"{label} ZIP directory '{path}' has forbidden type or payload.");
                }

                records.Add(new Record(index, path, "directory", 0, mode, string.Empty, entry.FullName));
            }
            else
            {
                if (fileType is not (0 or 0x8000))
                {
                    throw new ReleaseToolException($"{label} ZIP entry '{path}' has forbidden file type {fileType:x}.");
                }

                records.Add(new Record(index, path, "file", entry.Length, mode, string.Empty, entry.FullName));
            }
        }

        return records;
    }

    private static List<Record> ReadTarRecords(string archivePath, string requiredRoot, string label)
    {
        var physicalPaths = ReadTarPathRecords(archivePath, label);
        using var stream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        var records = new List<Record>();
        TarEntry? entry;
        var index = 0;
        while ((entry = archive.GetNextEntry(copyData: false)) is not null)
        {
            if (entry.EntryType == TarEntryType.Directory && entry.Name.TrimEnd('/') == ".")
            {
                continue;
            }

            if (index >= physicalPaths.Count)
            {
                throw new ReleaseToolException($"{label} TAR logical entry count does not match its physical header stream.");
            }

            var physicalPath = physicalPaths[index];
            if (entry.Name != physicalPath.HeaderName && entry.Name != physicalPath.LogicalName)
            {
                throw new ReleaseToolException($"{label} TAR entry '{entry.Name}' does not match physical header path '{physicalPath.LogicalName}'.");
            }

            var directory = entry.EntryType == TarEntryType.Directory;
            var path = PortablePaths.Validate(physicalPath.LogicalName, $"{label} TAR entry", directory);
            ValidateRequiredRoot(path, requiredRoot, label);
            var kind = entry.EntryType switch
            {
                TarEntryType.Directory => "directory",
                TarEntryType.RegularFile or TarEntryType.V7RegularFile => "file",
                TarEntryType.SymbolicLink => "symlink",
                TarEntryType.HardLink => "hardlink",
                _ => throw new ReleaseToolException($"{label} TAR entry '{path}' has forbidden type '{entry.EntryType}'."),
            };
            var size = kind == "file" ? entry.Length : 0;
            if (kind != "file" && entry.Length != 0)
            {
                throw new ReleaseToolException($"{label} TAR {kind} entry '{path}' contains a forbidden payload.");
            }

            records.Add(new Record(index++, path, kind, size, (int)entry.Mode, kind is "symlink" or "hardlink" ? entry.LinkName : string.Empty, entry.Name));
        }

        if (index != physicalPaths.Count)
        {
            throw new ReleaseToolException($"{label} TAR logical entry count does not match its physical header stream.");
        }

        return records;
    }

    private static List<TarPathRecord> ReadTarPathRecords(string archivePath, string label)
    {
        const int blockSize = 512;
        const long maximumMetadataBytes = 1L << 20;
        var paths = new List<TarPathRecord>();
        string? pendingExtendedPath = null;
        string? pendingLongName = null;
        using var stream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        var header = new byte[blockSize];
        while (ReadBlock(gzip, header, allowEndOfStream: true, label))
        {
            if (header.All(value => value == 0))
            {
                break;
            }

            var headerName = ReadTarString(header.AsSpan(0, 100), $"{label} TAR header name");
            var prefix = ReadTarString(header.AsSpan(345, 155), $"{label} TAR header prefix");
            var type = header[156];
            var size = ReadTarNumber(header.AsSpan(124, 12), $"{label} TAR entry '{headerName}' size");
            if (size < 0)
            {
                throw new ReleaseToolException($"{label} TAR entry '{headerName}' has a negative size.");
            }

            var metadata = type is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K';
            byte[]? payload = null;
            if (metadata)
            {
                if (size > maximumMetadataBytes)
                {
                    throw new ReleaseToolException($"{label} TAR metadata entry '{headerName}' is too large ({size} bytes).");
                }

                payload = new byte[checked((int)size)];
                ReadExactly(gzip, payload, label);
                SkipPadding(gzip, size, label);
            }
            else
            {
                SkipPayload(gzip, size, label);
            }

            switch (type)
            {
                case (byte)'x':
                    pendingExtendedPath = ReadPaxPath(payload!, allowPath: true, label);
                    continue;
                case (byte)'g':
                    if (ReadPaxPath(payload!, allowPath: false, label) is not null)
                    {
                        throw new ReleaseToolException($"{label} TAR global PAX metadata must not define an entry path.");
                    }

                    continue;
                case (byte)'L':
                    pendingLongName = ReadNullTerminatedPayload(payload!, $"{label} GNU long-name metadata");
                    continue;
                case (byte)'K':
                    continue;
            }

            var joinedName = string.IsNullOrEmpty(prefix) ? headerName : $"{prefix}/{headerName}";
            var logicalName = pendingExtendedPath ?? pendingLongName ?? joinedName;
            paths.Add(new TarPathRecord(headerName, logicalName));
            pendingExtendedPath = null;
            pendingLongName = null;
        }

        if (pendingExtendedPath is not null || pendingLongName is not null)
        {
            throw new ReleaseToolException($"{label} TAR archive ends with unapplied path metadata.");
        }

        return paths;

        static bool ReadBlock(Stream input, byte[] buffer, bool allowEndOfStream, string archiveLabel)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = input.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    if (offset == 0 && allowEndOfStream)
                    {
                        return false;
                    }

                    throw new ReleaseToolException($"{archiveLabel} TAR archive ends inside a header or padding block.");
                }

                offset += read;
            }

            return true;
        }

        static void ReadExactly(Stream input, byte[] buffer, string archiveLabel)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = input.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new ReleaseToolException($"{archiveLabel} TAR archive ends inside an entry payload.");
                }

                offset += read;
            }
        }

        static void SkipPayload(Stream input, long size, string archiveLabel)
        {
            var buffer = new byte[64 * 1024];
            var remaining = size;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new ReleaseToolException($"{archiveLabel} TAR archive ends inside an entry payload.");
                }

                remaining -= read;
            }

            SkipPadding(input, size, archiveLabel);
        }

        static void SkipPadding(Stream input, long size, string archiveLabel)
        {
            var padding = (blockSize - (size % blockSize)) % blockSize;
            Span<byte> buffer = stackalloc byte[blockSize];
            while (padding > 0)
            {
                var read = input.Read(buffer[..(int)Math.Min(buffer.Length, padding)]);
                if (read == 0)
                {
                    throw new ReleaseToolException($"{archiveLabel} TAR archive ends inside entry padding.");
                }

                padding -= read;
            }
        }
    }

    private static string ReadTarString(ReadOnlySpan<byte> field, string label)
    {
        var terminator = field.IndexOf((byte)0);
        var value = terminator < 0 ? field : field[..terminator];
        try
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReleaseToolException($"{label} is not valid UTF-8.", exception);
        }
    }

    private static long ReadTarNumber(ReadOnlySpan<byte> field, string label)
    {
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0)
            {
                throw new ReleaseToolException($"{label} is negative.");
            }

            long result = field[0] & 0x3f;
            foreach (var value in field[1..])
            {
                result = checked((result << 8) | value);
            }

            return result;
        }

        long octal = 0;
        var foundDigit = false;
        foreach (var value in field)
        {
            if (value is 0 or (byte)' ')
            {
                if (foundDigit)
                {
                    break;
                }

                continue;
            }

            if (value is < (byte)'0' or > (byte)'7')
            {
                throw new ReleaseToolException($"{label} is not a valid TAR number.");
            }

            foundDigit = true;
            octal = checked((octal << 3) | (byte)(value - (byte)'0'));
        }

        return octal;
    }

    private static string? ReadPaxPath(byte[] payload, bool allowPath, string label)
    {
        var offset = 0;
        string? path = null;
        while (offset < payload.Length)
        {
            var space = Array.IndexOf(payload, (byte)' ', offset);
            if (space < 0)
            {
                throw new ReleaseToolException($"{label} TAR PAX metadata has no record-length separator.");
            }

            if (!int.TryParse(Encoding.ASCII.GetString(payload, offset, space - offset), out var length) || length <= space - offset + 1 || length > payload.Length - offset)
            {
                throw new ReleaseToolException($"{label} TAR PAX metadata has an invalid record length.");
            }

            var record = payload.AsSpan(offset, length);
            if (record[^1] != (byte)'\n')
            {
                throw new ReleaseToolException($"{label} TAR PAX metadata record is not newline-terminated.");
            }

            var contentStart = space - offset + 1;
            var equals = record[contentStart..^1].IndexOf((byte)'=');
            if (equals < 0)
            {
                throw new ReleaseToolException($"{label} TAR PAX metadata record has no key/value separator.");
            }

            equals += contentStart;
            var key = ReadTarString(record[contentStart..equals], $"{label} TAR PAX metadata key");
            if (key == "path")
            {
                if (!allowPath)
                {
                    return string.Empty;
                }

                path = ReadTarString(record[(equals + 1)..^1], $"{label} TAR PAX path");
            }

            offset += length;
        }

        return path;
    }

    private static string ReadNullTerminatedPayload(byte[] payload, string label)
    {
        var value = payload.AsSpan();
        var terminator = value.IndexOf((byte)0);
        if (terminator >= 0)
        {
            value = value[..terminator];
        }

        var result = ReadTarString(value, label);
        if (string.IsNullOrEmpty(result))
        {
            throw new ReleaseToolException($"{label} is empty.");
        }

        return result;
    }

    private static void ValidateRecords(List<Record> records, string requiredRoot, string label)
    {
        if (records.Count > MaximumEntries)
        {
            throw new ReleaseToolException($"{label} contains too many entries ({records.Count}).");
        }

        var byPath = new Dictionary<string, Record>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var record in records)
        {
            if (!byPath.TryAdd(record.Path, record))
            {
                throw new ReleaseToolException($"{label} contains duplicate path '{record.Path}'.");
            }

            if (record.Kind == "file")
            {
                if (record.Size < 0 || record.Size > MaximumFileBytes || checked(totalBytes + record.Size) > MaximumTotalBytes)
                {
                    throw new ReleaseToolException($"{label} file '{record.Path}' has unsafe size {record.Size}.");
                }

                totalBytes += record.Size;
            }
            else if (record.Kind == "symlink")
            {
                record.ResolvedTarget = PortablePaths.ResolveLinkTarget(record.Path, record.Target, requiredRoot, label);
            }
            else if (record.Kind == "hardlink")
            {
                record.ResolvedTarget = PortablePaths.Validate(record.Target, $"{label} hard-link target");
                ValidateRequiredRoot(record.ResolvedTarget, requiredRoot, label);
            }
        }

        var knownDirectories = new HashSet<string>(StringComparer.Ordinal);
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            AddPortablePath(record.Path);
            if (record.Kind == "directory")
            {
                knownDirectories.Add(record.Path);
            }

            var parts = record.Path.Split('/');
            for (var count = 1; count < parts.Length; count++)
            {
                var ancestor = string.Join('/', parts.Take(count));
                AddPortablePath(ancestor);
                knownDirectories.Add(ancestor);
                if (byPath.TryGetValue(ancestor, out var ancestorRecord) && ancestorRecord.Kind != "directory")
                {
                    throw new ReleaseToolException($"{label} path '{record.Path}' descends through non-directory '{ancestor}' ({ancestorRecord.Kind}).");
                }
            }
        }

        foreach (var link in records.Where(record => record.Kind is "symlink" or "hardlink"))
        {
            var ultimate = ResolveGraph(link.ResolvedTarget, byPath, knownDirectories, new HashSet<string>(StringComparer.Ordinal), label);
            link.UltimateTarget = ultimate.Path;
            if (link.Kind == "hardlink" && ultimate.Kind != "file")
            {
                throw new ReleaseToolException($"{label} hard link '{link.Path}' does not resolve to a regular file.");
            }
        }

        void AddPortablePath(string path)
        {
            if (portablePaths.TryGetValue(path, out var existing))
            {
                if (existing != path)
                {
                    throw new ReleaseToolException($"{label} contains case-colliding explicit or implicit paths '{existing}' and '{path}'.");
                }

                return;
            }

            portablePaths.Add(path, path);
        }
    }

    private static Record ResolveGraph(
        string path,
        IReadOnlyDictionary<string, Record> byPath,
        IReadOnlySet<string> knownDirectories,
        HashSet<string> active,
        string label)
    {
        var parts = path.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            var prefix = string.Join('/', parts.Take(index + 1));
            if (!byPath.TryGetValue(prefix, out var record))
            {
                if (!knownDirectories.Contains(prefix))
                {
                    throw new ReleaseToolException($"{label} link graph has dangling path '{path}' at '{prefix}'.");
                }

                if (index == parts.Length - 1)
                {
                    return new Record(-1, prefix, "directory", 0, 0, string.Empty, prefix);
                }

                continue;
            }

            var final = index == parts.Length - 1;
            if (record.Kind == "directory")
            {
                if (final)
                {
                    return record;
                }

                continue;
            }

            if (record.Kind == "file")
            {
                if (!final)
                {
                    throw new ReleaseToolException($"{label} link graph descends through file '{prefix}'.");
                }

                return record;
            }

            if (!final && record.Kind == "hardlink")
            {
                throw new ReleaseToolException($"{label} link graph descends through hard link '{prefix}'.");
            }

            if (!active.Add(prefix))
            {
                throw new ReleaseToolException($"{label} link graph contains a cycle through '{prefix}'.");
            }

            try
            {
                var redirected = record.ResolvedTarget;
                if (!final)
                {
                    redirected += "/" + string.Join('/', parts.Skip(index + 1));
                }

                return ResolveGraph(redirected, byPath, knownDirectories, active, label);
            }
            finally
            {
                active.Remove(prefix);
            }
        }

        throw new ReleaseToolException($"{label} could not resolve link graph path '{path}'.");
    }

    private static void ExtractRecords(string kind, string archivePath, string destination, List<Record> records, string requiredRoot, string label)
    {
        foreach (var record in records.Where(record => record.Kind == "directory").OrderBy(record => record.Path.Count(character => character == '/')).ThenBy(record => record.Path, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(PortablePaths.SafeDestination(destination, record.Path));
        }

        if (kind == "zip")
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var record in records.Where(record => record.Kind == "file").OrderBy(record => record.Path, StringComparer.Ordinal))
            {
                using var source = archive.Entries[record.Index].Open();
                WriteFile(source, record, destination, label);
            }
        }
        else
        {
            using var stream = File.OpenRead(archivePath);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var archive = new TarReader(gzip);
            var bySourceIndex = records.ToDictionary(record => record.Index);
            var index = 0;
            TarEntry? entry;
            while ((entry = archive.GetNextEntry(copyData: false)) is not null)
            {
                if (entry.EntryType == TarEntryType.Directory && entry.Name.TrimEnd('/') == ".")
                {
                    continue;
                }

                if (bySourceIndex.TryGetValue(index++, out var record) && record.Kind == "file")
                {
                    if (entry.Name != record.SourceName)
                    {
                        throw new ReleaseToolException($"{label} TAR changed between validation and extraction at '{record.Path}'.");
                    }

                    if (entry.DataStream is null && record.Size != 0)
                    {
                        throw new ReleaseToolException($"{label} file '{record.Path}' has no readable payload.");
                    }

                    WriteFile(entry.DataStream ?? Stream.Null, record, destination, label);
                }
            }
        }

        foreach (var record in records.Where(record => record.Kind == "hardlink").OrderBy(record => record.Path, StringComparer.Ordinal))
        {
            var path = PortablePaths.SafeDestination(destination, record.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var target = PortablePaths.SafeDestination(destination, record.UltimateTarget);
            NativeFileLinks.CreateHardLink(path, target);
        }

        foreach (var record in records.Where(record => record.Kind == "symlink").OrderBy(record => record.Path, StringComparer.Ordinal))
        {
            var path = PortablePaths.SafeDestination(destination, record.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.Exists(PortablePaths.SafeDestination(destination, record.UltimateTarget)))
            {
                Directory.CreateSymbolicLink(path, record.Target);
            }
            else
            {
                File.CreateSymbolicLink(path, record.Target);
            }
        }

        foreach (var record in records.Where(record => record.Kind == "symlink"))
        {
            var path = PortablePaths.SafeDestination(destination, record.Path);
            var info = File.Exists(path) ? (FileSystemInfo)new FileInfo(path) : new DirectoryInfo(path);
            var resolved = info.ResolveLinkTarget(true) ?? throw new ReleaseToolException($"{label} symbolic link '{record.Path}' is dangling after extraction.");
            EnsureInside(destination, resolved.FullName, $"{label} symbolic link '{record.Path}'");
        }

        if (!string.IsNullOrEmpty(requiredRoot) && !Directory.Exists(PortablePaths.SafeDestination(destination, requiredRoot)))
        {
            throw new ReleaseToolException($"{label} did not produce required root '{requiredRoot}'.");
        }
    }

    private static void WriteFile(Stream source, Record record, string destination, string label)
    {
        var path = PortablePaths.SafeDestination(destination, record.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                copied += read;
                if (copied > record.Size)
                {
                    throw new ReleaseToolException($"{label} file '{record.Path}' exceeds its declared size.");
                }

                output.Write(buffer, 0, read);
            }

            if (copied != record.Size)
            {
                throw new ReleaseToolException($"{label} file '{record.Path}' yielded {copied} bytes, expected {record.Size}.");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            var executable = (record.Mode & 0x49) != 0;
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void ValidateRequiredRoot(string path, string requiredRoot, string label)
    {
        if (!string.IsNullOrEmpty(requiredRoot) && path != requiredRoot && !path.StartsWith(requiredRoot + "/", StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"{label} path '{path}' is outside required root '{requiredRoot}'.");
        }
    }

    private static void EnsureInside(string root, string candidate, string label)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        if (fullCandidate != fullRoot && !fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"{label} resolves outside '{root}'.");
        }
    }

    private static bool IsExecutable(string path)
        => !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
