using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class ArchiveCreator
{
    private const int MaximumEntries = 250_000;
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Entry(
        string Source,
        string ArchivePath,
        EntryKind Kind,
        int Mode,
        long Size,
        string LinkTarget,
        DateTime LastWriteTimeUtc);

    private enum EntryKind
    {
        Directory,
        File,
        SymbolicLink,
    }

    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--source-root", "--output", "--kind");
        return Create(command.Required("--source-root"), command.Required("--output"), command.Required("--kind"));
    }

    public static JsonObject Create(string sourceRoot, string outputPath, string kind)
    {
        if (kind is not ("zip" or "targz"))
        {
            throw new ReleaseToolException($"Unsupported release archive kind '{kind}'.");
        }

        var sourceInfo = new DirectoryInfo(Path.GetFullPath(sourceRoot));
        if (!sourceInfo.Exists || sourceInfo.LinkTarget is not null)
        {
            throw new ReleaseToolException($"Release source root must be a real directory: {sourceRoot}");
        }

        var source = sourceInfo.FullName.TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(outputPath);
        var expectedSuffix = kind == "zip" ? ".zip" : ".tar.gz";
        if (!output.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"{kind} release archive must end with '{expectedSuffix}': {output}");
        }

        if (new FileInfo(output).LinkTarget is not null || (Directory.Exists(output) && !File.Exists(output)))
        {
            throw new ReleaseToolException($"Release archive output is not a replaceable regular file: {output}");
        }

        var sourcePrefix = source + Path.DirectorySeparatorChar;
        if (output.StartsWith(sourcePrefix, PathComparison))
        {
            throw new ReleaseToolException("Release archive output must not be inside the staged source root.");
        }

        var entries = CollectEntries(sourceInfo);
        if (kind == "zip" && entries.Any(entry => entry.Kind == EntryKind.SymbolicLink))
        {
            var link = entries.First(entry => entry.Kind == EntryKind.SymbolicLink);
            throw new ReleaseToolException($"Windows ZIP release archives cannot contain symbolic links: {link.ArchivePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (kind == "zip")
            {
                WriteZip(entries, temporary);
            }
            else
            {
                WriteTarGzip(entries, temporary);
            }

            SetMode(temporary, 0x1a4);
            File.Move(temporary, output, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        var info = new FileInfo(output);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = "ok",
            ["kind"] = kind,
            ["sourceRoot"] = source,
            ["topLevelDirectory"] = sourceInfo.Name,
            ["output"] = output,
            ["entries"] = entries.Count,
            ["bytes"] = info.Length,
            ["sha256"] = JsonIO.Sha256File(output),
            ["metadataPolicy"] = new JsonObject
            {
                ["entryOrder"] = "ascii-ordinal",
                ["portablePaths"] = "ascii-windows-safe-case-insensitive-v1",
                ["modeNormalization"] = "directory-0755-symlink-0777-file-executable-0755-otherwise-0644",
                ["tarMtime"] = 0,
                ["tarUid"] = 0,
                ["tarGid"] = 0,
                ["tarUser"] = string.Empty,
                ["tarGroup"] = string.Empty,
                ["gzipMtime"] = 0,
                ["zipDateTime"] = new JsonArray(1980, 1, 1, 0, 0, 0),
                ["zipCompression"] = "deflate-smallest-size",
                ["symlinkPolicy"] = "tar-safe-relative-only-zip-rejected",
            },
        };
    }

    private static List<Entry> CollectEntries(DirectoryInfo sourceRoot)
    {
        var rootName = PortablePaths.Validate(sourceRoot.Name, "archive root");
        var root = sourceRoot.FullName.TrimEnd(Path.DirectorySeparatorChar);
        var entries = new List<Entry>
        {
            NewEntry(sourceRoot, rootName, root),
        };
        var portablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [rootName] = rootName,
        };

        Visit(sourceRoot, string.Empty);
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath));
        return entries;

        void Visit(DirectoryInfo directory, string relativeParent)
        {
            FileSystemInfo[] children;
            try
            {
                children = directory.EnumerateFileSystemInfos().OrderBy(child => child.Name, StringComparer.Ordinal).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ReleaseToolException($"Could not enumerate staged directory {directory.FullName}: {exception.Message}", exception);
            }

            foreach (var child in children)
            {
                PortablePaths.ValidateSegment(child.Name, "archive entry", child.Name);
                var relative = string.IsNullOrEmpty(relativeParent) ? child.Name : $"{relativeParent}/{child.Name}";
                var archivePath = PortablePaths.Validate($"{rootName}/{relative}", "archive entry");
                if (!portablePaths.TryAdd(archivePath, archivePath))
                {
                    throw new ReleaseToolException($"Archive contains duplicate or portable-colliding path '{archivePath}'.");
                }

                var entry = NewEntry(child, archivePath, root);
                entries.Add(entry);
                if (entries.Count > MaximumEntries)
                {
                    throw new ReleaseToolException($"Release tree contains more than {MaximumEntries} entries.");
                }

                if (entry.Kind == EntryKind.Directory)
                {
                    Visit((DirectoryInfo)child, relative);
                }
            }
        }
    }

    private static Entry NewEntry(FileSystemInfo info, string archivePath, string sourceRoot)
    {
        info.Refresh();
        var linkTarget = info.LinkTarget;
        if (linkTarget is not null)
        {
            PortablePaths.ResolveLinkTarget(archivePath, linkTarget, archivePath.Split('/')[0], "archive entry");
            var resolved = info.ResolveLinkTarget(true) ?? throw new ReleaseToolException($"Symbolic link '{archivePath}' is dangling or cyclic.");
            var resolvedPath = Path.GetFullPath(resolved.FullName);
            var rootPrefix = sourceRoot + Path.DirectorySeparatorChar;
            if (resolvedPath != sourceRoot && !resolvedPath.StartsWith(rootPrefix, PathComparison))
            {
                throw new ReleaseToolException($"Symbolic link '{archivePath}' resolves outside staged root.");
            }

            return new Entry(info.FullName, archivePath, EntryKind.SymbolicLink, 0x1ff, 0, linkTarget, info.LastWriteTimeUtc);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ReleaseToolException($"Archive entry '{archivePath}' is an unsupported reparse point.");
        }

        if (info is DirectoryInfo directory && directory.Exists)
        {
            return new Entry(info.FullName, archivePath, EntryKind.Directory, 0x1ed, 0, string.Empty, info.LastWriteTimeUtc);
        }

        if (info is FileInfo file && file.Exists)
        {
            var mode = IsExecutable(file.FullName) ? 0x1ed : 0x1a4;
            return new Entry(file.FullName, archivePath, EntryKind.File, mode, file.Length, string.Empty, file.LastWriteTimeUtc);
        }

        throw new ReleaseToolException($"Archive entry '{archivePath}' has an unsupported filesystem type.");
    }

    private static void WriteZip(IEnumerable<Entry> entries, string output)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var entry in entries)
        {
            var directory = entry.Kind == EntryKind.Directory;
            var item = archive.CreateEntry(entry.ArchivePath + (directory ? "/" : string.Empty), directory ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
            item.LastWriteTime = ZipEpoch;
            var fileType = directory ? 0x4000 : 0x8000;
            item.ExternalAttributes = unchecked((fileType | entry.Mode) << 16) | (directory ? 0x10 : 0);
            if (!directory)
            {
                AssertUnchanged(entry);
                using var source = File.OpenRead(entry.Source);
                using var destination = item.Open();
                source.CopyTo(destination, 1024 * 1024);
                AssertUnchanged(entry);
            }
        }
    }

    private static void WriteTarGzip(IEnumerable<Entry> entries, string output)
    {
        using var raw = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new DeterministicGZipWriteStream(raw);
        using var archive = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true);
        foreach (var entry in entries)
        {
            var tar = new GnuTarEntry(entry.Kind switch
            {
                EntryKind.Directory => TarEntryType.Directory,
                EntryKind.SymbolicLink => TarEntryType.SymbolicLink,
                _ => TarEntryType.RegularFile,
            }, entry.ArchivePath)
            {
                Mode = (UnixFileMode)entry.Mode,
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                UserName = string.Empty,
                GroupName = string.Empty,
            };

            if (entry.Kind == EntryKind.SymbolicLink)
            {
                tar.LinkName = entry.LinkTarget;
            }
            else if (entry.Kind == EntryKind.File)
            {
                AssertUnchanged(entry);
                tar.DataStream = File.OpenRead(entry.Source);
            }

            try
            {
                archive.WriteEntry(tar);
            }
            finally
            {
                tar.DataStream?.Dispose();
            }

            if (entry.Kind == EntryKind.File)
            {
                AssertUnchanged(entry);
            }
        }
    }

    private static void AssertUnchanged(Entry entry)
    {
        var info = new FileInfo(entry.Source);
        if (!info.Exists || info.LinkTarget is not null || info.Length != entry.Size || info.LastWriteTimeUtc != entry.LastWriteTimeUtc || (IsExecutable(info.FullName) ? 0x1ed : 0x1a4) != entry.Mode)
        {
            throw new ReleaseToolException($"Staged file changed while archiving: {entry.ArchivePath}");
        }
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)mode);
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
