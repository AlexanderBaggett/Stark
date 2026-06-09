# `System.FileSystem`

`System.FileSystem` is the public module for filesystem-level operations that
are broader than an already-open file handle.

`System.IO.File` remains the owned file-handle API. `System.FileSystem` owns
directory creation/deletion, directory listing, metadata queries, and whole-path
operations.

## Public Surface

```stark
import System.IO
module System.FileSystem

public enum FileSystemEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

public struct FileSystemEntry
{
    System.Text.OwnedAscii Name;
    FileSystemEntryKind Kind;

    finite ascii NameView(mut borrow FileSystemEntry self);
}

public struct FileSystemEntryInfo
{
    u64[0 2 ** 63 - 1] NameLength;
    FileSystemEntryKind Kind;
}

public struct FileMetadata
{
    u64[0 2 ** 63 - 1] Size;
    i64[min max] ModifiedUnixSeconds;
    i64[min max] ModifiedNanoseconds;
    u32[0 max] Permissions;
    FileSystemEntryKind Kind;

    inline finite law bool IsExecutable(borrow FileMetadata self);
}

public enum DirectoryReadResult
{
    Entry(FileSystemEntry),
    End,
    Err(System.IO.IOError),
}

public enum DirectoryReadInfoResult
{
    Entry(FileSystemEntryInfo),
    End,
    Err(System.IO.IOError),
}

public struct Directory
{
    finite law bool IsOpen(borrow Directory self);
    fn DirectoryReadInfoResult ReadNextInfo(mut borrow Directory self);
    fn DirectoryReadResult ReadNext(mut borrow Directory self);
    fn System.IO.IOStatus Close(mut borrow Directory self);
}

public fn System.IO.IOStatus CreateDirectory(ascii path);
public fn System.IO.IOStatus DeleteDirectory(ascii path);
public fn System.IO.IOStatus DeleteTree(ascii root);
public fn System.IO.IOStatus DeleteTreeIfExists(ascii root);
public fn System.IO.IOResult<Directory> OpenDirectory(ascii path);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOResult<bool> IsFile(ascii path);
public fn System.IO.IOResult<bool> IsDirectory(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<FileMetadata> Metadata(ascii path);
public fn System.IO.IOResult<System.Text.OwnedAscii> CreateTempDirectoryIn(ascii parent, ascii prefix);
public fn System.IO.IOResult<System.Text.OwnedAscii> CreateTempDirectory(ascii prefix);
public fn System.IO.IOStatus WalkRecursive(ascii root, inline closure<fn System.IO.IOStatus(ascii, FileSystemEntryKind)> visitor);
public fn System.IO.IOStatus Glob(ascii root, ascii pattern, inline closure<fn System.IO.IOStatus(ascii, FileSystemEntryKind)> visitor) where overlap(root, pattern);
```

`DeleteDirectory` is intentionally non-recursive so mistakes do not delete a
tree by accident. Recursive deletion is deliberately named `DeleteTree`, while
`DeleteTreeIfExists` is the idempotent cleanup helper for temp fixtures.

`Metadata` reports size, modification time, permissions, and entry kind for a
single path. Linux and macOS preserve POSIX mode permission bits. Windows maps
read-only/directory attributes into POSIX-like read/write/execute bits so shared
compiler logic can make deterministic permission checks. The internal platform
metadata boundary maps common OS errors into `IOError` values.

## Directory Listing

Directory listing should return owned Stark values, not raw OS directory
entries.

```stark
fn System.IO.IOStatus PrintEntries(ascii path)
{
    stack System.IO.IOResult<System.FileSystem.Directory> opened =
        System.FileSystem.OpenDirectory(path);

    switch (opened)
    {
        case System.IO.IOResult<System.FileSystem.Directory>.Err(var error):
            return System.IO.IOStatus.Err(error);
        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directory):
            stack mut System.FileSystem.Directory entries = directory;
            while non-deterministic (true)
            {
                stack System.FileSystem.DirectoryReadResult next = entries.ReadNext();
                switch (next)
                {
                    case System.FileSystem.DirectoryReadResult.End:
                        return entries.Close();
                    case System.FileSystem.DirectoryReadResult.Err(var error):
                        entries.Close();
                        return System.IO.IOStatus.Err(error);
                    case System.FileSystem.DirectoryReadResult.Entry(var entry):
                        stack mut System.FileSystem.FileSystemEntry ownedEntry = entry;
                        System.Console.WriteLine(ownedEntry.NameView());
                }
            }
    }
}
```

Use `ReadNextInfo` when only the entry kind and name length are needed:

```stark
stack System.FileSystem.DirectoryReadInfoResult next = entries.ReadNextInfo();
```

Use `ReadNext` when the caller needs an owned entry name. The returned
`FileSystemEntry.Name` can be viewed with `NameView()`.

The loop is marked `non-deterministic` because directory iteration depends on
external filesystem state and therefore should not be used inside a `finite`
function.

## Recursive Walk

`WalkRecursive` visits the root path and each descendant path with its
`FileSystemEntryKind`. It streams through a caller-provided callback instead of
allocating a full path list.

```stark
fn System.IO.IOStatus Visit(ascii path, System.FileSystem.FileSystemEntryKind kind)
{
    System.Console.WriteLine(path);
    return System.IO.IOStatus.Ok;
}

fn System.IO.IOStatus PrintTree(ascii root)
{
    return System.FileSystem.WalkRecursive(root, Visit);
}
```

The current walk does not follow symlink directories, which avoids accidental
cycles.

## Glob

`Glob` walks `root` recursively and calls the visitor only for paths that match
the root-relative `pattern`. The matcher uses `System.IO.Path.GlobMatches`: `*`
matches within one path segment, `?` matches one unit within one path segment,
and `**` as a whole segment matches zero or more path segments.

```stark
fn System.IO.IOStatus VisitSource(ascii path, System.FileSystem.FileSystemEntryKind kind)
{
    if (kind == System.FileSystem.FileSystemEntryKind.File)
    {
        System.Console.WriteLine(path);
    }

    return System.IO.IOStatus.Ok;
}

fn System.IO.IOStatus PrintStarkSources(ascii root)
{
    return System.FileSystem.Glob(root, "**/*.stark", VisitSource);
}
```

`Glob` streams matches through the callback and does not allocate a result list.
It uses the same symlink-directory policy as `WalkRecursive`.

## Function Kinds

`Directory.IsOpen` is `finite law` because it only reads local handle state and
always returns.

Directory reads, directory creation/deletion, metadata queries, moves, and
close operations are ordinary `fn` because they touch the operating system,
mutate handle state, allocate owned entry names, or can fail due to external
filesystem state.

## Relationship To `System.IO.Path`

`System.IO.Path` exists today and should remain source-compatible.

For a later standard-library slice, the filesystem documentation should decide
whether path helpers are:

- kept in `System.IO.Path` and re-exported from `System.FileSystem`
- moved to `System.FileSystem.Path` with compatibility forwarding
- left as-is until a later breaking standard-library release

The first implementation should prefer compatibility over a perfect namespace
split.

## Current Status

- `CreateDirectory`, non-recursive `DeleteDirectory`, recursive `DeleteTree`,
  idempotent `DeleteTreeIfExists`, `OpenDirectory`,
  `Directory.ReadNextInfo`, `Directory.ReadNext`, `Exists`, `IsFile`,
  `IsDirectory`, `Move`, `Metadata`, `CreateTempDirectoryIn`,
  `CreateTempDirectory`, `WalkRecursive`, and `Glob` are available.
- `CreateTempDirectoryIn` builds candidate paths with
  `System.IO.Path.TryTempPathIn` and retries explicit attempts on
  `AlreadyExists`; callers that only need candidate paths can use
  `System.IO.Path` directly.
- `CreateTempDirectory` uses `System.IO.Path.TempDirectory` for the platform
  temp root before applying the same explicit retry loop.
- `Directory` is an owned handle with best-effort close-on-drop cleanup.
- `FileSystemEntry.Name` is owned entry-name storage, so callers are not tied to
  the directory iterator's internal buffer.
- Metadata behavior is implemented for Linux, macOS, and Windows. Symlink
  target reads and richer path errors remain tracked under self-host prep.
