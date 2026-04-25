# `System.IO.Path`

`System.IO.Path` currently provides the basic separator helpers, caller-buffer current-directory query, and a small path-shaping helper set.

## Current Surface

```stark
public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public fn bool CurrentDirectory(rawmutptr<Ascii> destination);
public finite law ascii ParentDirectory();
public fn bool TryJoin(rawmutptr<Ascii> destination, ascii left, ascii right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right);
public finite law ascii Extension(ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii DirectoryName(ascii path);
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
module App

fn i32 ShowCurrentDirectory() {
    stack mut i8[64] buffer = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };
    stack mut Ascii owned = new Ascii() {
        Data = &buffer[0],
        Length = 0,
        Capacity = 64
    };

    if (!System.IO.Path.CurrentDirectory(&owned)) {
        return 1;
    }

    stack mut i8[64] joinedBuffer = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };
    stack mut Ascii joined = new Ascii() {
        Data = &joinedBuffer[0],
        Length = 0,
        Capacity = 64
    };

    if (!System.IO.Path.TryJoin(&joined, System.Text.AsciiView(owned), "demo.txt")) {
        return 2;
    }

    System.Console.WriteLine(System.Text.AsciiView(joined));
    return 0;
}
```

## Current Status

- Separator and dot-path helpers are implemented.
- `TryJoin`, allocation-visible `Join`, `Extension`, `BaseName`, and `DirectoryName` are implemented.
- `Join` returns `System.Memory.MemoryResult<System.Text.OwnedAscii>` and allocates exactly enough path storage for the normalized join result. It preserves the same separator normalization rules as `TryJoin`.
- `CurrentDirectory(rawmutptr<Ascii>)` is implemented on Linux with an internal raw `getcwd` syscall shim and on Windows with `GetCurrentDirectoryW` plus UTF-16LE to UTF-8 conversion.
- The caller-provided `Ascii` buffer must leave room for the returned text. On Linux it must also leave one trailing zero byte reserved for the raw `getcwd` syscall.
- Windows platform paths normalize UTF-8 input to UTF-16LE for `W` APIs, convert `/` to `\`, recognize drive-absolute and UNC paths, and add the `\\?\` long-path prefix when needed.
