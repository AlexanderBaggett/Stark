# `System.FileSystem`

`System.FileSystem` is the planned public module for filesystem-level operations
that are broader than an already-open file handle.

`System.IO.File` remains the owned file-handle API. `System.FileSystem` owns
directory creation/deletion, directory listing, metadata queries, and whole-path
operations.

## Planned Public Surface

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
    Ascii Name;
    FileSystemEntryKind Kind;
}

public enum DirectoryReadResult {
    Entry(FileSystemEntry),
    End,
    Err(System.IO.IOError),
}

public struct Directory {
    finite law bool IsOpen(self);
    fn DirectoryReadResult ReadNext(mut self);
    fn System.IO.IOStatus Close(mut self);
}

public fn System.IO.IOStatus CreateDirectory(ascii path);
public fn System.IO.IOStatus DeleteDirectory(ascii path);
public fn System.IO.IOResult<Directory> OpenDirectory(ascii path);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOResult<bool> IsFile(ascii path);
public fn System.IO.IOResult<bool> IsDirectory(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
```

`DeleteDirectory` is intentionally part of the first planned surface. The
initial operation should be non-recursive so mistakes do not delete a tree by
accident. Recursive deletion can be added later as a deliberately named helper.

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
                        System.Console.WriteLine(System.Text.AsciiView(entry.Name));
                }
            }
    }
}
```

The example uses owned entry names so users do not have to manage raw directory
buffers. Implementations may reuse internal buffers, but that reuse must not
leak into the public safety contract.

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

For `v1.2`, the filesystem documentation should decide whether path helpers are:

- kept in `System.IO.Path` and re-exported from `System.FileSystem`
- moved to `System.FileSystem.Path` with compatibility forwarding
- left as-is until a later breaking standard-library release

The first implementation should prefer compatibility over a perfect namespace
split.

## Current Status

- This is a planned `v1.2` module.
- File deletion and move already exist today through `System.IO.File`.
- Directory deletion, directory listing, metadata queries, and allocation-backed
  owned entry names remain implementation work.
