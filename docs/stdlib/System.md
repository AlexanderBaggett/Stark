# `System`

`System` is the public package root for the current Stark standard library slice.

It re-exports:

- `System.Console`
- `System.IO`
- `System.Text`

It also imports internal runtime support and the `System.Syscall` module during package build, but `System.Syscall` is not re-exported through `System`.
Internal runtime modules such as `System.Runtime.Buffer` are compiled into the package implementation, but they are not part of the public package manifest or supported import surface.

## Example

```stark
import System
module App

export ffi fn i32 main() {
    System.Console.WriteLine("Hello");
    return 0;
}
```

## Current Status

- The package root and public module graph are stable for the current Milestone 7 slice.
- `System.Console` and `System.IO.File` are usable today.
- `System.IO.Path` currently provides the separator/dot-path helpers plus a low-level caller-buffer `CurrentDirectory` API documented in its module reference.
