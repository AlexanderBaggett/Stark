# `System.IO.Path`

`System.IO.Path` currently provides the basic separator helpers, caller-buffer current-directory query, and a small path-shaping helper set.

## Current Surface

```stark
public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public fn System.Memory.MemoryStatus CurrentDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> CurrentDirectory();
public finite law ascii ParentDirectory();
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii left, ascii right);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii left, const ascii right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii left, const ascii right);
public fn System.Memory.MemoryStatus TryNormalizeSeparators(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryNormalizeSeparatorsConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparators(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparatorsConst(const ascii path);
public struct PathFacts;
public finite law PathFacts GetFacts(ascii path);
public finite law PathFacts GetConstFacts(const ascii path);
public finite law ascii Extension(ascii path);
public finite law ascii ExtensionConst(const ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii BaseNameConst(const ascii path);
public finite law ascii DirectoryName(ascii path);
public finite law ascii DirectoryNameConst(const ascii path);
```

## Current Values

On Linux:

- `DirectorySeparator()` returns `"/"`
- `AlternateDirectorySeparator()` returns `""`
- `PathSeparator()` returns `":"`
- `ParentDirectory()` returns `".."`

On Windows:

- `DirectorySeparator()` returns `"\\"`
- `AlternateDirectorySeparator()` returns `"/"`
- `PathSeparator()` returns `";"`
- `ParentDirectory()` returns `".."`

## Example

```stark
import System
import System.Text
module App

fn i32 ShowCurrentDirectory() {
    stack mut System.Text.OwnedAscii owned = new();

    if (System.IO.Path.CurrentDirectory(owned) != System.Memory.MemoryStatus.Ok) {
        return 1;
    }

    stack mut System.Text.OwnedAscii joined = new();

    if (System.IO.Path.TryJoin(joined, owned.View(), "demo.txt") != System.Memory.MemoryStatus.Ok) {
        return 2;
    }

    System.Console.WriteLine(joined.View());
    return 0;
}
```

## Current Status

- Separator and dot-path helpers are implemented.
- `TryJoin`, allocation-visible `Join`, `Extension`, `BaseName`, and `DirectoryName` are implemented.
- `Join` returns `System.Memory.MemoryResult<System.Text.OwnedAscii>` and allocates exactly enough path storage for the normalized join result. It preserves the same separator normalization rules as `TryJoin`.
- `TryNormalizeSeparators` and `NormalizeSeparators` copy a path while using the
  current platform's directory separator.
- `TryJoinConst`, `JoinConst`, `TryNormalizeSeparatorsConst`,
  `NormalizeSeparatorsConst`, `GetConstFacts`, `ExtensionConst`,
  `BaseNameConst`, and `DirectoryNameConst` are available for constant ASCII
  paths.
- `GetFacts` computes the path length, trimmed end, extension, base-name, and directory-name ranges in one pass. Use it when a hot path needs more than one path component.
- `CurrentDirectory` appends into caller-owned `System.Text.OwnedAscii` storage or returns an owned-text result. Linux uses an internal raw `getcwd` syscall shim; Windows uses `GetCurrentDirectoryW` plus UTF-16LE to UTF-8 conversion.
- `TryJoin` and `CurrentDirectory` return `System.Memory.MemoryStatus`, keeping allocation failure visible while raw path buffers stay internal.
- Windows platform paths normalize UTF-8 input to UTF-16LE for `W` APIs, convert `/` to `\`, recognize drive-absolute and UNC paths, and add the `\\?\` long-path prefix when needed.
