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

public enum FileSystemEntryKind {
    File,
    Directory,
    Symlink,
    Other,
}

public struct FileSystemEntry {
    System.Text.OwnedAscii Name;
    FileSystemEntryKind Kind;

    finite ascii NameView(mut borrow FileSystemEntry self);
}

public struct FileSystemEntryInfo {
    u64[0 2 ** 63 - 1] NameLength;
    FileSystemEntryKind Kind;
}

public enum DirectoryReadResult {
    Entry(FileSystemEntry),
    End,
    Err(System.IO.IOError),
}

public enum DirectoryReadInfoResult {
    Entry(FileSystemEntryInfo),
    End,
    Err(System.IO.IOError),
}

public struct Directory {
    finite law bool IsOpen(borrow Directory self);
    fn DirectoryReadInfoResult ReadNextInfo(mut borrow Directory self);
    fn DirectoryReadResult ReadNext(mut borrow Directory self);
    fn System.IO.IOStatus Close(mut borrow Directory self);
}

public fn System.IO.IOStatus CreateDirectory(ascii path);
public fn System.IO.IOStatus DeleteDirectory(ascii path);
public fn System.IO.IOResult<Directory> OpenDirectory(ascii path);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOResult<bool> IsFile(ascii path);
public fn System.IO.IOResult<bool> IsDirectory(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
```

`DeleteDirectory` is intentionally non-recursive so mistakes do not delete a
tree by accident. Recursive deletion can be added later as a deliberately named
helper.

## Directory Listing

Directory listing should return owned Stark values, not raw OS directory
entries.

```stark
fn System.IO.IOStatus PrintEntries(ascii path) {
    stack System.IO.IOResult<System.FileSystem.Directory> opened =
        System.FileSystem.OpenDirectory(path);

    switch (opened) {
        case System.IO.IOResult<System.FileSystem.Directory>.Err(var error):
            return System.IO.IOStatus.Err(error);
        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directory):
            stack mut System.FileSystem.Directory entries = directory;
            while non-deterministic (true) {
                stack System.FileSystem.DirectoryReadResult next = entries.ReadNext();
                switch (next) {
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

- `CreateDirectory`, non-recursive `DeleteDirectory`, `OpenDirectory`,
  `Directory.ReadNextInfo`, `Directory.ReadNext`, `Exists`, `IsFile`,
  `IsDirectory`, and `Move` are available.
- `Directory` is an owned handle with best-effort close-on-drop cleanup.
- `FileSystemEntry.Name` is owned entry-name storage, so callers are not tied to
  the directory iterator's internal buffer.
