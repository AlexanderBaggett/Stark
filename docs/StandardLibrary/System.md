# `System`

`System` is the public package root for the current Stark standard library slice.

It re-exports:

- `System.BitOperations`
- `System.C`
- `System.Collections`
- `System.Console`
- `System.Compiler.IntegerFacts`
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

It also imports internal runtime support, `System.Syscall`, `System.Core`, and
`System.Testing` during package build, but those modules are not re-exported
through `System`.
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
- `System.Option<T>` and `System.Result<T, E>` are public root aliases backed by
  the ordinary `[Ok]`/`[Err]` enums in `System.Core`.
- `System.Console` and `System.IO.File` are usable today.
- `System.BitOperations` currently exposes integer bit-manipulation helpers.
- `System.C` currently exposes target-mapped C primitive aliases and C string interop helpers.
- `System.Compiler.IntegerFacts` currently exposes bounded `i1024`/`u1024`
  compiler integer-fact helpers for range, storage, tag, checked arithmetic,
  known-bit, and two's-complement reasoning.
- `System.Math` currently exposes scalar math helpers, including `Sqrt`,
  `Min`, `Max`, `Sin`, `Cos`, `SinCos`, `Exp`, `Log`, `Pow`, rounding,
  reciprocal estimates, fused multiply-add, and `XorShift32`.
- `System.Memory` currently exposes the first allocator vocabulary and internal default-allocation contract needed for owned collections and buffers.
- `System.Collections` currently exposes the first owned allocator-backed
  collection surface for `List<T>`, `Stack<T>`, `Queue<T>`, `RingQueue<T>`,
  `LinkedList<T>`, `Dictionary<K, V>`, `HashSet<T>`, readonly `Lookup<T>`,
  comparator-based `SortBy<T>`, and direct `Ord`-based `Sort<T>`.
- `System.IO.File` currently provides owned file handles plus whole-file text/byte read and write helpers and line-oriented text reading.
- `System.IO.Path` currently provides separator/current-directory/temp-directory helpers, glob matching, multi-part joins, path facts, full/lexical path shaping, rooted/relative checks, and extension rewriting.
- `System.FileSystem` currently exposes directory creation/deletion, directory opening/iteration with owned entry names, cross-platform filesystem metadata queries, temp-directory creation, recursive walk, and streaming glob traversal.
- `System.Threading` currently exposes the no-state `ThreadEntry` callable alias, compact thread status/result enums, owned `Thread` construction, `Join`, `Detach`, best-effort drop cleanup, static scheduler helpers for yield and millisecond sleep, seq-cst atomics, `Synchronized<T>` / `Locked<T>` guarded shared state, and MPSC `Channel<T>` / `Sender<T>` / `Receiver<T>` handles.
- `System.Net` currently exposes the shared networking error/result/status vocabulary plus IPv4 address and endpoint value types.
- `System.Net.Tcp` currently exposes `TcpShutdown` plus owned `TcpClient` and
  `TcpListener`, including connect/listen/accept, slice reads and writes,
  byte-buffer overloads, vectored IO, wait helpers, shutdown, and close.
- `System.Process` currently exposes process id/exit helpers plus Linux-backed process spawn/capture with optional stdin input, timeout capture, environment, argv, and working-directory APIs.
- `System.Testing` currently exposes finite-law boolean/equality, text/count, range, slice/List shape assertions, root `Option`/`Result` shape predicates, process output predicates plus effectful run-match/timeout helpers, temp fixture helpers, snapshot/golden text helpers, and `RunFact`/exit helpers used by generated `stark test` `[Fact]` / `[Theory]` runners with inline and typed member data.
- `System.Text` currently exposes owned text helpers, text/count scans, byte-slice-to-ASCII scans, parsers, formatters, and low-level caller-buffer conversion APIs through explicit `import System.Text`.
- `System.Runtime.Buffer` exposes fixed and dynamic byte buffers through
  explicit `import System.Runtime.Buffer`.
- `System.Net.Http` is intentionally not planned for the standard library. HTTP should be provided by packages built on `System.Net.Tcp`.
