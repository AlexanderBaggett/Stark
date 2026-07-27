# `System.IO.Path`

`System.IO.Path` provides separator helpers, caller-buffer current-directory and
temp-directory queries, glob matching, and compiler-grade path-shaping helpers.

## Current Surface

```stark
public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public fn System.Memory.MemoryStatus CurrentDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> CurrentDirectory();
public fn System.Memory.MemoryStatus TempDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempDirectory();
public finite law ascii ParentDirectory();
public finite law bool GlobMatches(ascii pattern, ascii path);
public fn System.Memory.MemoryStatus TryTempName(mut borrow System.Text.OwnedAscii destination, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempName(ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryStatus TryTempPathIn(mut borrow System.Text.OwnedAscii destination, ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempPathIn(ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii left, ascii right);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third, ascii fourth);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii left, const ascii right);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii first, const ascii second, const ascii third);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii first, const ascii second, const ascii third, const ascii fourth);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii first, ascii second, ascii third);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii first, ascii second, ascii third, ascii fourth);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii left, const ascii right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii first, const ascii second, const ascii third);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii first, const ascii second, const ascii third, const ascii fourth);
public fn System.Memory.MemoryStatus TryNormalizeSeparators(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryNormalizeSeparatorsConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparators(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparatorsConst(const ascii path);
public fn System.Memory.MemoryStatus TryNormalizeLexically(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryNormalizeLexicallyConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeLexically(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeLexicallyConst(const ascii path);
public fn System.Memory.MemoryStatus TryFullPath(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryFullPathConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> FullPath(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> FullPathConst(const ascii path);
public fn System.Memory.MemoryStatus TryChangeExtension(mut borrow System.Text.OwnedAscii destination, ascii path, ascii extension);
public fn System.Memory.MemoryStatus TryChangeExtensionConst(mut borrow System.Text.OwnedAscii destination, const ascii path, const ascii extension);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ChangeExtension(ascii path, ascii extension);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ChangeExtensionConst(const ascii path, const ascii extension);
public struct PathFacts;
public finite law PathFacts GetFacts(ascii path);
public finite law PathFacts GetConstFacts(const ascii path);
public finite law ascii Extension(ascii path);
public finite law ascii ExtensionConst(const ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii BaseNameConst(const ascii path);
public finite law ascii DirectoryName(ascii path);
public finite law ascii DirectoryNameConst(const ascii path);
public finite law ascii RootName(ascii path);
public finite law ascii RootNameConst(const ascii path);
public finite law bool IsRooted(ascii path);
public finite law bool IsRootedConst(const ascii path);
public finite law bool IsAbsolute(ascii path);
public finite law bool IsAbsoluteConst(const ascii path);
public finite law bool IsRelative(ascii path);
public finite law bool IsRelativeConst(const ascii path);
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

fn i32 ShowCurrentDirectory()
{
    stack mut System.Text.OwnedAscii owned = new();

    if (System.IO.Path.CurrentDirectory(owned) != System.Memory.MemoryStatus.Ok)
    {
        return 1;
    }

    stack mut System.Text.OwnedAscii joined = new();

    if (System.IO.Path.TryJoin(joined, owned.View(), "demo.txt") != System.Memory.MemoryStatus.Ok)
    {
        return 2;
    }

    System.Console.WriteLine(joined.View());
    return 0;
}
```

## Current Status

- Separator and dot-path helpers are implemented.
- `GlobMatches` matches path strings without allocation. `*` matches within one
  path segment, `?` matches one unit within one path segment, and `**` as a
  whole segment matches zero or more path segments. Separator matching honors
  the current platform separator and alternate separator.
- `TryJoin`, allocation-visible `Join`, `Extension`, `BaseName`, `DirectoryName`, `RootName`, rooted/absolute/relative checks, lexical normalization, full-path construction, and extension rewriting are implemented.
- `TryJoin` / `Join` support two, three, and four path parts. Multi-part joins reserve up front and append path ranges through direct tail-region copies without hidden intermediate owned strings.
- `Join` returns `System.Memory.MemoryResult<System.Text.OwnedAscii>` and allocates path storage for the normalized join result. It preserves the same separator normalization rules as `TryJoin`.
- `TryTempName` / `TempName` build explicit temp candidate names as `prefix + process-id + "-" + attempt + suffix`. An empty prefix defaults to `"stark-"`. The attempt is supplied by the caller, so there is no hidden counter, random source, or global mutable state.
- `TempDirectory` appends the platform temp root into caller-owned storage or
  returns an owned-text result. Linux and macOS return `/tmp`; Windows uses
  `GetTempPathW` and converts the result to UTF-8.
- `TryTempPathIn` / `TempPathIn` join a temp candidate name under an explicit parent directory. Filesystem creation and collision retries remain in `System.FileSystem`.
- `TryNormalizeSeparators` and `NormalizeSeparators` copy a path while using the
  current platform's directory separator.
- `TryNormalizeLexically` and `NormalizeLexically` additionally fold empty, `.`, and `..` path segments without touching the filesystem.
- `TryFullPath` and `FullPath` combine relative paths with `CurrentDirectory` and lexically normalize the result. They do not require the path to exist.
- `TryChangeExtension` and `ChangeExtension` replace or remove the last extension. An extension argument without a leading `.` receives one automatically.
- `TryJoinConst`, `JoinConst`, `TryNormalizeSeparatorsConst`,
  `NormalizeSeparatorsConst`, `TryNormalizeLexicallyConst`,
  `NormalizeLexicallyConst`, `TryFullPathConst`, `FullPathConst`,
  `TryChangeExtensionConst`, `ChangeExtensionConst`, `GetConstFacts`,
  `ExtensionConst`, `BaseNameConst`, `DirectoryNameConst`, `RootNameConst`,
  `IsRootedConst`, `IsAbsoluteConst`, and `IsRelativeConst` are available for constant ASCII paths.
- `GetFacts` computes the path length, trimmed end, root, extension, base-name, and directory-name ranges in one pass. Use it when a hot path needs more than one path component.
- `CurrentDirectory` appends into caller-owned `System.Text.OwnedAscii` storage or returns an owned-text result. Linux uses an internal raw `getcwd` syscall shim; Windows uses `GetCurrentDirectoryW` plus UTF-16LE to UTF-8 conversion.
- `TryJoin`, `CurrentDirectory`, and `TempDirectory` return `System.Memory.MemoryStatus`, keeping allocation failure visible while raw path buffers stay internal.
- Windows platform paths normalize UTF-8 input to UTF-16LE for `W` APIs, convert `/` to `\`, recognize drive-absolute and UNC paths, and add the `\\?\` long-path prefix when needed.
