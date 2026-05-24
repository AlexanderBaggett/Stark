# `System`

`System` is the public package root for the current Stark standard library slice.

It re-exports:

- `System.BitOperations`
- `System.Collections`
- `System.Console`
- `System.FileSystem`
- `System.IO`
- `System.IO.File`
- `System.IO.Path`
- `System.Math`
- `System.Memory`
- `System.Net`
- `System.Net.Tcp`
- `System.Process`
- `System.Threading`

It also imports internal runtime support, `System.Syscall`, and `System.Testing`
during package build, but those modules are not re-exported through `System`.
`System.Runtime.Buffer` is not re-exported through `System`, but its byte-buffer
types are part of public Console, File, and TCP APIs. Code that names those
types directly should import `System.Runtime.Buffer`.

`System.Text` is still a public module, but it is intentionally not re-exported
by `System` because its current low-level caller-buffer APIs expose explicit
unsafe raw pointers. Code that needs text helpers should import `System.Text`
directly.
`System.Testing` is also explicit-import only so assertion helpers stay in test
projects instead of the ordinary root namespace.

## Example

```stark
import System
module App

export fn i32 main()
{
    System.Console.WriteLine("Hello");
    return 0;
}
```

## Current Status

- The package root and public module graph are stable for the current Milestone 7 slice.
- `System.Console` and `System.IO.File` are usable today.
- `System.BitOperations` currently exposes integer bit-manipulation helpers.
- `System.Math` currently exposes scalar math helpers, including `Sqrt`,
  `Min`, `Max`, `Sin`, `Cos`, `SinCos`, rounding, reciprocal estimates, and
  `XorShift32`.
- `System.Memory` currently exposes the first allocator vocabulary and internal default-allocation contract needed for owned collections and buffers.
- `System.Collections` currently exposes the first owned allocator-backed
  collection surface for `List<T>`, `Stack<T>`, `Queue<T>`, `RingQueue<T>`,
  `LinkedList<T>`, `Dictionary<K, V>`, and readonly `Lookup<T>`.
- `System.IO.Path` currently provides the separator/dot-path helpers plus a low-level caller-buffer `CurrentDirectory` API documented in its module reference.
- `System.FileSystem` currently exposes directory creation/deletion, directory opening/iteration with owned entry names, and filesystem metadata queries.
- `System.Threading` currently exposes the no-state `ThreadEntry` callable alias, compact thread status/result enums, owned `Thread` construction, `Join`, `Detach`, best-effort drop cleanup, and static scheduler helpers for yield and millisecond sleep.
- `System.Net` currently exposes the shared networking error/result/status vocabulary plus IPv4 address and endpoint value types.
- `System.Net.Tcp` currently exposes `TcpShutdown` plus owned `TcpClient` and
  `TcpListener`, including connect/listen/accept, slice reads and writes,
  byte-buffer overloads, vectored IO, wait helpers, shutdown, and close.
- `System.Process` currently exposes `CurrentId` and `Exit` as the public process helper surface over the internal platform layer.
- `System.Testing` currently exposes boolean/equality assertions and an explicit `RunFact` helper for `stark test` executables.
- `System.Text` currently exposes owned text helpers, parsers, formatters, and low-level caller-buffer conversion APIs through explicit `import System.Text`.
- `System.Runtime.Buffer` exposes fixed and dynamic byte buffers through
  explicit `import System.Runtime.Buffer`.
- `System.Net.Http` is intentionally not planned for the standard library. HTTP should be provided by packages built on `System.Net.Tcp`.
