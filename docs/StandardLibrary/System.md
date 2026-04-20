# `System`

`System` is the public package root for the current Stark standard library slice.

It re-exports:

- `System.BitOperations`
- `System.Collections`
- `System.Console`
- `System.IO`
- `System.Math`
- `System.Memory`
- `System.Text`

Planned `v1.2` additions:

- `System.FileSystem`
- `System.Net`
- `System.Net.Tcp`
- `System.Threading`

It also imports internal runtime support and the `System.Syscall` module during package build, but `System.Syscall` is not re-exported through `System`.
Internal runtime modules such as `System.Runtime.Buffer` are compiled into the package implementation, but they are not part of the public package image or supported import surface.

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
- `System.BitOperations` currently exposes the first integer bit-manipulation builtin slice.
- `System.Math` currently exposes the LLVM-intrinsic scalar math slice plus the current hardware/compiler-intrinsic batch, including `Sqrt`, `Min`, `Max`, the reciprocal-estimate pair, and the rounding helpers.
- `System.Memory` currently exposes the first allocator vocabulary and internal default-allocation contract needed for owned collections and buffers.
- `System.Collections` currently exposes the first owned allocator-backed collection surface for `List<T>`, `Stack<T>`, `Queue<T>`, and `LinkedList<T>`. Pop/remove accessors now use source-level `out T` bodies, safe retborrow accessors and `List<T>` slice views are lowered, and the first dictionary key contract vocabulary is present while generic `Dictionary<K, V>` remains planned.
- `System.IO.Path` currently provides the separator/dot-path helpers plus a low-level caller-buffer `CurrentDirectory` API documented in its module reference.
- `System.FileSystem`, `System.Net`, `System.Net.Tcp`, and `System.Threading` are planned `v1.2` modules and are documented as design targets rather than implemented public package surface today.
- `System.Net.Http` is intentionally not planned for the standard library. HTTP should be provided by packages built on `System.Net.Tcp`.
