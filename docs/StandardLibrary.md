# Standard Library

Remember this languge aims to be faster than idiomatic C or Rust on most projects, we must chose the best posible optimization strategy and explore optimization opportunities.


This document describes the planned standard library design for Stark.

It replaces the current first-slice libc-backed plan with a cross-platform `System` package built around:

- a stable module layout
- packaged consumption through manifests
- owned file handles and destructor-driven cleanup
- encoding-aware text IO
- a platform abstraction layer that talks to the OS without exposing libc/glibc or Win32 details to user code

## Preconditions

This design assumes the language work tracked in Milestone 6.5:

- generics for `IOResult<T>` and related shared types
- overloads for `ascii` and `unicode` APIs with the same user-facing name
- destructor syntax and implementation for owned-resource cleanup

On Linux, the stdlib targets the Linux syscall ABI through tiny internal `ffi asm` shims in the platform layer. User code still stays on the `System.*` surface and does not touch syscall register details directly.

## Goals

The standard library provides:

- a stable module layout organized around `System`
- basic console output and input
- file and path operations
- text encoding support
- a platform abstraction layer that talks directly to the OS without libc

User code calls `System.Console` or `System.IO.*` and never touches platform syscalls or Win32 APIs directly. The platform boundary is an internal implementation detail hidden behind the library surface.

## Reference Docs

The current public module references live here:

- [System](stdlib/System.md)
- [System.BitOperations](stdlib/System.BitOperations.md)
- [System.Console](stdlib/System.Console.md)
- [System.IO](stdlib/System.IO.md)
- [System.IO.File](stdlib/System.IO.File.md)
- [System.IO.Path](stdlib/System.IO.Path.md)
- [System.Math](stdlib/System.Math.md)
- [System.Text](stdlib/System.Text.md)

## Module Layout

The package root is `System`.

Repository source layout:

- `stdlib/src/System.stark`
- `stdlib/src/System/BitOperations.stark`
- `stdlib/src/System/Console.stark`
- `stdlib/src/System/IO.stark`
- `stdlib/src/System/IO/File.stark`
- `stdlib/src/System/IO/Path.stark`
- `stdlib/src/System/Text.stark`
- `stdlib/src/System/Math.stark`
- `stdlib/src/System/Runtime.stark`
- `stdlib/src/System/Runtime/Buffer.stark`
- `stdlib/src/System/Runtime/Platform.stark`
- `stdlib/src/System/Runtime/Platform/Linux.stark`
- `stdlib/src/System/Runtime/Platform/Windows.stark`

Public module surface:

- `System`
- `System.BitOperations`
- `System.Console`
- `System.IO`
- `System.IO.File`
- `System.IO.Path`
- `System.Text`
- `System.Math`

Internal modules:

- `System.Runtime`
- `System.Runtime.Buffer`
- `System.Runtime.Platform`
- `System.Runtime.Platform.Linux`
- `System.Runtime.Platform.Windows`

`System.stark` is a pure package root that re-exports the public submodules:

```stark
export import System.BitOperations
export import System.Console
export import System.IO
export import System.Math
export import System.Text
module System
```

`System.IO` re-exports the IO submodules and declares shared IO types:

```stark
export import System.IO.File
export import System.IO.Path
module System.IO

public enum IOError {
    NotFound,
    PermissionDenied,
    AlreadyExists,
    InvalidPath,
    BrokenPipe,
    DiskFull,
    Unknown(i32),
}

public enum IOResult<T> {
    Ok(T),
    Err(IOError),
}

public enum IOStatus {
    Ok,
    Err(IOError),
}
```

`IOStatus` exists because Stark does not treat `void` as a first-class value type. Value-returning APIs use `IOResult<T>`. Effect-only APIs use `IOStatus`.

`System.Text` is the public text module. It declares the shared encoding enum plus the current owned-text helper APIs for view projection, explicit runtime conversion, and concatenation:

```stark
module System.Text

public enum Encoding {
    Binary,
    UTF8,
    UTF16,
    UTF32,
}

public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law rawptr<i8> AsciiData(ascii source);
public finite law i64 AsciiLength(ascii source);
public finite law rawptr<i32> UnicodeData(unicode source);
public finite law i64 UnicodeLength(unicode source);
public fn bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);
public fn bool TryConvertAsciiToUtf16(rawmutptr<i16> destination, i64 capacity, ascii source, rawmutptr<i64> writtenLength);
public fn bool TryConvertUtf16ToUnicode(rawmutptr<Unicode> destination, rawptr<i16> source, i64 sourceLength);
public fn bool TryConvertUnicodeToAscii(rawmutptr<Ascii> destination, unicode source);
public fn bool TryConvertUnicodeToUtf16(rawmutptr<i16> destination, i64 capacity, unicode source, rawmutptr<i64> writtenLength);
public fn bool TryConvertUtf16ToAscii(rawmutptr<Ascii> destination, rawptr<i16> source, i64 sourceLength);
public fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
public fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
```

## Encoding Model

`System.Text` defines the `Encoding` enum used by both `System.IO.File` and explicit text conversion APIs. The owned text container types themselves are now core language types, so `System.Text` focuses on the shared encoding enum plus helper functions that operate on those core containers.

The semantics are:

- `Binary` means the file handle does not request an alternate multibyte text encoding. Byte APIs always ignore encoding. `ascii` text writes are passthrough UTF-8 bytes, and `unicode` text writes use the same UTF-8 platform text path as the raw-handle helpers for compatibility with the current default owned-file surface.
- `UTF8` means the file stream converts to and from UTF-8. Writing an `ascii` string is a passthrough. Writing a `unicode` string converts UTF-32 to UTF-8 before writing.
- `UTF16` means the file stream converts to and from UTF-16LE. Both `ascii` and `unicode` strings are converted before writing.
- `UTF32` means the file stream converts to and from UTF-32. Writing a `unicode` string is a passthrough. Writing an `ascii` string converts UTF-8 to UTF-32 before writing.

`Binary` is the default encoding for file handles. Console output defaults to `UTF8`.

## Owned Text Types

`Ascii` and `Unicode` are the core owned text containers, analogous to Rust's pointer/length/capacity model.

- `Ascii.Data` points at mutable UTF-8 bytes
- `Unicode.Data` points at mutable UTF-32 code units
- `Length` counts initialized elements in the same units as the pointer element type
- `Capacity` counts allocated elements in the same units as the pointer element type
- these are owning containers, not aliases for the immutable `ascii` and `unicode` view types

The currently implemented bridge APIs are:

- `System.Text.AsciiView(Ascii)` and `System.Text.UnicodeView(Unicode)` for zero-copy immutable view projection
- `System.Text.AsciiData(ascii)`, `System.Text.AsciiLength(ascii)`, `System.Text.UnicodeData(unicode)`, and `System.Text.UnicodeLength(unicode)` for explicit pointer/length access when stdlib code needs exact view boundaries at low-level OS or FFI edges
- `System.Text.TryConvertAsciiToUnicode(rawmutptr<Unicode>, ascii)`, `System.Text.TryConvertUnicodeToAscii(rawmutptr<Ascii>, unicode)`, `System.Text.TryConvertAsciiToUtf16(rawmutptr<i16>, i64, ascii, rawmutptr<i64>)`, `System.Text.TryConvertUtf16ToUnicode(rawmutptr<Unicode>, rawptr<i16>, i64)`, `System.Text.TryConvertUnicodeToUtf16(rawmutptr<i16>, i64, unicode, rawmutptr<i64>)`, and `System.Text.TryConvertUtf16ToAscii(rawmutptr<Ascii>, rawptr<i16>, i64)` for explicit caller-owned UTF-8, UTF-16LE, and UTF-32 conversion
- `System.Text.TryConcatAscii(rawmutptr<Ascii>, ascii, ascii)` and `System.Text.TryConcatUnicode(rawmutptr<Unicode>, unicode, unicode)` for explicit concatenation into caller-provided storage

These APIs make allocation visible in user code: the caller owns the backing buffer, fills `Data` and `Capacity`, and conversion or concat returns `false` instead of allocating when the destination is too small.

## Console API

`System.Console` is the user-facing module for terminal output and input. It replaces the previous `System.IO.Stdout` and `System.IO.Stderr` split.

```stark
module System.Console

public fn IOStatus Write(borrow ascii text);
public fn IOStatus Write(borrow unicode text);
public fn IOStatus WriteLine(borrow ascii text);
public fn IOStatus WriteLine(borrow unicode text);
public fn IOStatus WriteError(borrow ascii text);
public fn IOStatus WriteError(borrow unicode text);
public fn IOStatus WriteErrorLine(borrow ascii text);
public fn IOStatus WriteErrorLine(borrow unicode text);
public fn Unicode ReadLine();
public fn Unicode Read();
```

Internal implementation:

- On Linux, the current `ascii` output path uses internal syscall-backed write shims on fd `1` and fd `2`.
- On Windows, `Write` and `WriteLine` call `WriteFile` on the handle from `GetStdHandle(STD_OUTPUT_HANDLE)`. `WriteError` and `WriteErrorLine` use `GetStdHandle(STD_ERROR_HANDLE)`.
- On Linux, `unicode` overloads convert UTF-32 to UTF-8 in the stdlib and then write through the syscall-backed fd boundary.
- `ReadLine` returns the next UTF-8 decoded line from stdin as `Unicode` without the trailing newline. `Read` returns the next UTF-8 decoded code point from stdin as a one-element `Unicode`.
- `ReadLine` and `Read` currently return empty `Unicode` on EOF or input failure instead of a richer result type.
- The current input implementation uses a shared buffered stdin handle plus reusable internal `Unicode` backing buffers rather than allocator-backed per-call ownership. Each new `ReadLine` overwrites the previous `ReadLine` result, and each new `Read` overwrites the previous `Read` result.
- `WriteLine` always appends `\n` on both Linux and Windows. The library does not perform CRLF translation.
- Console output is unbuffered by default. The write goes directly to the OS.

## File API

`System.IO.File` provides safe owned file handles with automatic close on drop.

### The File Struct

`File` is the public owned handle type for file IO. Its concrete representation is intentionally not part of the public API contract. The implementation stores the OS handle, buffering state, buffering policy, and encoding internally.

If Stark reaches this redesign before it has a stronger type-opacity story, the first implementation can still preserve this API shape while treating the exact field layout as stdlib-internal convention rather than stable user-facing structure.

```stark
import System.IO
module System.IO.File

public struct File {
    fn bool IsOpen(borrow File self);
    fn i32 Close(mut borrow File self);
    fn i32 Flush(mut borrow File self);
    fn i64 ReadBytes(mut borrow File self, rawptr<i8> buffer, i64 size, i64 count);
    fn i64 WriteBytes(mut borrow File self, rawptr<i8> buffer, i64 size, i64 count);
    fn void WriteText(mut borrow File self, ascii text);
    fn void WriteText(mut borrow File self, unicode text);
    fn void WriteLine(mut borrow File self, ascii text);
    fn void WriteLine(mut borrow File self, unicode text);
}

public fn File Open(ascii path, FileMode mode);
public fn File Open(ascii path, FileMode mode, FileBuffering buffering);
public fn File Open(ascii path, FileMode mode, System.Text.Encoding encoding);
public fn File Open(ascii path, FileMode mode, System.Text.Encoding encoding, FileBuffering buffering);

public fn rawptr<i8> OpenRead(ascii path);
public fn rawptr<i8> OpenWrite(ascii path);
public fn rawptr<i8> OpenAppend(ascii path);

public fn i32 Close(rawptr<i8> handle);
public fn i32 Flush(rawptr<i8> handle);
public fn i64 ReadBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle);
public fn i64 WriteBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle);
public fn void WriteText(rawptr<i8> handle, ascii text);
public fn void WriteText(rawptr<i8> handle, unicode text);
public fn void WriteLine(rawptr<i8> handle, ascii text);
public fn void WriteLine(rawptr<i8> handle, unicode text);
public fn i32 Delete(ascii path);
public fn i32 Move(ascii oldPath, ascii newPath);
public fn bool Exists(ascii path);
```

Methods on a `public struct` are accessible wherever the struct is visible. Visibility modifiers do not apply to individual methods or fields inside a type body per the Stark module system rules.

### File Modes

```stark
public enum FileMode {
    Read,
    Write,
    Append,
    ReadWrite,
}
```

### File Buffering

```stark
public enum FileBuffering {
    None,
    Line,
    Full,
}
```

Files default to `Full` buffering with an 8192-byte internal buffer. On Linux, the default `Open(...)` overload now checks the opened handle with `ioctl(TCGETS)` and switches to `Line` buffering for terminal-connected handles. `None` means every write goes directly to the OS.

### File Ownership and Drop

`File` is an owned type. When a `File` value is dropped at scope exit, the destructor flushes the internal buffer and closes the underlying OS handle.

The destructor is constrained by Stark's destructor rules:

- it does not panic
- it does not synchronize
- it does not allocate

Explicit `Close` is available for code that wants to handle close ordering. After an explicit `Close`, the destructor is a no-op.

Because destructors cannot surface rich failure values, implicit destructor cleanup is best-effort. Code that needs flush or close error handling must call `Flush` and `Close` explicitly before scope exit.

### File Encoding Behavior

The `encoding` field and `System.Text.Encoding` enum are in place, but the current Milestone 7 slice is still narrower than the eventual text-IO design:

- owned `File` text writes support both `ascii` and `unicode`
- raw-handle helpers already support both `ascii` and `unicode`
- on Linux, the current `unicode` write path converts UTF-32 to UTF-8 before issuing the write syscall
- owned-file `UTF8`, `UTF16`, and `UTF32` writes now honor the selected encoding for both `ascii` and `unicode`
- owned-file `UTF16` and `UTF32` writes flush any pending buffered ascii data before writing encoded bytes directly
- text-reading APIs remain future work

`ReadBytes` and `WriteBytes` always ignore encoding and operate on raw bytes regardless.

### File Operations

Internal implementation:

- On Linux, `Open` calls the internal platform open boundary backed by `openat(2)`. `Close` calls the internal close boundary backed by `close(2)`. `ReadBytes` calls the internal read boundary backed by `read(2)`. `WriteBytes` calls the internal write boundary backed by `write(2)`. `Flush` drains the userspace buffer via repeated writes. `Delete` calls the internal delete boundary backed by `unlinkat(2)`. `Move` calls the internal rename boundary backed by `renameat2(2)`. `Exists` uses `newfstatat(2)`.
- On Windows, `Open` calls `CreateFileW`. `Close` calls `CloseHandle`. `ReadBytes` calls `ReadFile`. `WriteBytes` calls `WriteFile`. `Flush` calls `FlushFileBuffers`. `Delete` calls `DeleteFileW`. `Move` calls `MoveFileExW`. `Exists` uses `GetFileAttributesW`.
- Path strings are converted at the platform boundary. On Linux, `ascii` paths pass through as-is. On Windows, `ascii` paths are converted from UTF-8 to UTF-16LE before calling the `W` APIs, and `GetCurrentDirectoryW` results are converted back to UTF-8 for `System.IO.Path.CurrentDirectory`.

## Path API

`System.IO.Path` provides path manipulation helpers. These are pure library functions with no OS calls unless noted.

```stark
import System.IO
module System.IO.Path

public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public fn bool TryJoin(rawmutptr<Ascii> destination, ascii left, ascii right);
public finite law ascii Extension(borrow ascii path);
public finite law ascii BaseName(borrow ascii path);
public finite law ascii DirectoryName(borrow ascii path);

public fn bool CurrentDirectory(rawmutptr<Ascii> destination);
```

`DirectorySeparator` returns `"/"` on Linux and `"\\"` on Windows. `AlternateDirectorySeparator` returns `"/"` on Windows and `""` on Linux. `PathSeparator` returns `":"` on Linux and `";"` on Windows.

`Extension`, `BaseName`, and `DirectoryName` are `finite law` because they are pure, have no side effects, and always return.

`TryJoin` uses a caller-provided `Ascii` destination rather than allocating hidden storage. It returns `false` if the destination buffer is too small.

`CurrentDirectory` is `fn` because it issues an OS call. In the current Milestone 7 slice it uses a caller-provided `Ascii` buffer rather than performing hidden allocation, and it returns `bool` success instead of a richer result type. On the current Linux-backed implementation, the destination buffer must have room for the path text plus one trailing zero byte reserved for the raw `getcwd` syscall. Allocation-backed convenience path APIs are deferred to `v2.0`.

## Platform Abstraction Layer

The platform layer is internal to the standard library. User code never imports it.

### Design

The platform layer defines a minimal set of operations that the rest of the stdlib builds on. Each operation has a Linux implementation and a Windows implementation. The build selects the correct implementation based on the target triple.

In `v1.x`, this selection is done through the package build and target-specific internal modules. The current Linux implementation uses Stark-level `ffi asm` shims internally, but user-facing code still stays on the `System.*` surface.

### Linux Implementation

`System.Runtime.Platform.Linux` targets the Linux syscall ABI without depending on libc or glibc.

In the current Milestone 7 slice, the actual syscall instruction is issued through tiny internal `ffi asm` shims inside the Linux platform module.

The syscall ABI on x86_64 Linux is:

- syscall number in `rax`
- arguments in `rdi`, `rsi`, `rdx`, `r10`, `r8`, `r9`
- return value in `rax`
- `rcx` and `r11` are clobbered by the kernel

The required syscalls are:

| Operation | Syscall | Number (x86_64) |
|---|---|---|
| write | `write` | 1 |
| read | `read` | 0 |
| open | `openat` | 257 |
| close | `close` | 3 |
| flush | userspace buffer drain via `write` | 1 |
| delete | `unlinkat` | 263 |
| rename | `renameat2` | 316 |
| stat or exists | `newfstatat` | 262 |
| getcwd | `getcwd` | 79 |
| exit | `exit_group` | 231 |
| ioctl | `ioctl` | 16 |

`ioctl` is needed to detect whether a file descriptor is a terminal for buffering strategy selection.

The current implementation status is:

- `getcwd` is syscall-backed on Linux
- `ascii` and `unicode` console output are syscall-backed on Linux
- file open/read/write/close/delete/move/exists are syscall-backed on Linux
- terminal detection uses `ioctl(TCGETS)` on Linux and feeds the default file-buffering policy
- packaged Linux stdlib builds are regression-tested to avoid libc/glibc symbol dependencies

stdout and stderr are fd `1` and fd `2`. They exist at process start with no setup required.

### Windows Implementation

`System.Runtime.Platform.Windows` calls Win32 APIs from `kernel32.dll`. No CRT dependency exists.

The required Win32 APIs are:

| Operation | Win32 API |
|---|---|
| write | `WriteFile` |
| read | `ReadFile` |
| open | `CreateFileW` |
| close | `CloseHandle` |
| flush | `FlushFileBuffers` |
| delete | `DeleteFileW` |
| rename | `MoveFileExW` |
| exists | `GetFileAttributesW` |
| getcwd | `GetCurrentDirectoryW` |
| exit | `ExitProcess` |
| console detect | `GetConsoleMode` |
| stdout handle | `GetStdHandle(STD_OUTPUT_HANDLE)` |
| stderr handle | `GetStdHandle(STD_ERROR_HANDLE)` |

All file and path APIs use the `W` variants. The platform layer converts UTF-8 path bytes to UTF-16LE before every call and converts UTF-16LE results back to UTF-8 on return.

Windows has a `MAX_PATH` limit unless paths are prefixed with `\\?\`. The platform layer prepends this prefix transparently when needed.

### Platform Internal API Shape

The internal platform module exposes roughly this surface to the rest of the stdlib:

```stark
import System.IO
module System.Runtime.Platform

internal fn IOResult<i64> PlatformWrite(i64 handle, borrow i8[] data);
internal fn IOResult<i64> PlatformRead(i64 handle, mut i8[] buffer);
internal fn IOResult<i64> PlatformOpen(borrow ascii path, i32 flags, i32 mode);
internal fn IOStatus PlatformClose(i64 handle);
internal fn IOStatus PlatformFlush(i64 handle);
internal fn IOStatus PlatformDelete(borrow ascii path);
internal fn IOStatus PlatformRename(borrow ascii oldPath, borrow ascii newPath);
internal fn IOResult<bool> PlatformExists(borrow ascii path);
internal fn bool TryCurrentDirectory(rawmutptr<Ascii> destination);
internal fn bool PlatformIsTerminal(i64 handle);
internal fn i64 PlatformGetStdout();
internal fn i64 PlatformGetStderr();
```

On Linux, `i64 handle` maps directly to a file descriptor. On Windows, `i64 handle` stores a `HANDLE` value cast to `i64`. The rest of the stdlib never interprets handle values; it only passes them back into platform calls.

## Text Conversion

The encoding conversions needed by the stdlib are:

- UTF-8 to UTF-16LE
- UTF-16LE to UTF-8
- UTF-32 to UTF-8
- UTF-8 to UTF-32
- UTF-32 to UTF-16LE
- UTF-16LE to UTF-32

These are pure computational functions with no platform dependency. They live in `System.Text` and are shared by both the IO layer and user-facing text APIs.

These are implemented today through the six caller-owned `System.Text` helpers:

- `TryConvertAsciiToUnicode`
- `TryConvertAsciiToUtf16`
- `TryConvertUtf16ToUnicode`
- `TryConvertUnicodeToAscii`
- `TryConvertUnicodeToUtf16`
- `TryConvertUtf16ToAscii`

Direct UTF-32 to UTF-16 conversion is preferred over routing through UTF-8 when performance matters.

## Error Model

IO operations that can fail return result values. Stark has no exceptions.

`IOError`, `IOResult<T>`, and `IOStatus` are declared in `System.IO` so they are available to all IO submodules and downstream user code.

The platform layer translates OS error codes into `IOError` values at the boundary:

- On Linux, negative syscall return values are negated errno codes. The platform layer maps `ENOENT` to `NotFound`, `EACCES` and `EPERM` to `PermissionDenied`, `EEXIST` to `AlreadyExists`, `ENOSPC` to `DiskFull`, `EPIPE` to `BrokenPipe`, and everything else to `Unknown`.
- On Windows, `GetLastError()` codes are mapped similarly. `ERROR_FILE_NOT_FOUND` and `ERROR_PATH_NOT_FOUND` become `NotFound`, `ERROR_ACCESS_DENIED` becomes `PermissionDenied`, `ERROR_ALREADY_EXISTS` becomes `AlreadyExists`, and so on.

## Runtime Strategy

The target design is zero dependency on libc, glibc, musl, or the Windows CRT for stdlib IO paths.

- On Linux, the current Milestone 7 slice uses syscall-backed boundaries for `getcwd`, console output, and file-descriptor-based file operations.
- Owned-file unicode buffering and broader text-conversion APIs are still part of the remaining shared text-IO work, but the Linux platform layer itself no longer depends on libc/glibc for the implemented paths.
- On Windows, the target remains Win32 API calls from `kernel32.dll`.
- `System.Runtime.Buffer` now provides the internal fixed-size linear and ring buffer primitives used by stdlib IO. `File` uses those foundations for `None` / `Line` / `Full` write-buffering policy, and the default Linux path now switches between `Full` and `Line` based on `ioctl` terminal detection. `Console` still writes directly to the OS today.

## Building the Package

Build the standard library package with:

```bash
./scripts/build-stdlib.sh
```

By default that emits the package into `stdlib/dist/`.

You can also choose another output directory:

```bash
./scripts/build-stdlib.sh /tmp/stark-stdlib
```

The emitted package contains:

- a static library archive
- a sidecar `.starkpkg.json` manifest

## Using the Packaged Standard Library

Build the package first, then compile an application with `-I` pointing at the package directory.

Example:

```stark
import System
module Hello

export ffi fn i32 main() {
    System.Console.WriteLine("Hello, world!");
    System.Console.WriteErrorLine("stderr works too");
    return 0;
}
```

Compile:

```bash
dotnet run --project src -- hello.stark --emit-exe -I stdlib/dist -o hello
```

## Runtime, Allocator, and IO Dependencies

### Runtime boundary

- The library depends on compiler-emitted or toolchain-provided runtime symbols internally, but user code does not.
- Startup and shutdown behavior is coordinated between the compiler toolchain and `System.Runtime`.
- Any API that needs process termination or host interaction is routed through a library wrapper.

### Allocator boundary

- Console output avoids allocation.
- File buffering uses a fixed-size internal buffer.
- Owned text-returning APIs depend on the allocator contract used by the runtime and stdlib.
- Prefer a single allocator contract for stdlib internals rather than ad hoc allocation in each module.

### IO boundary

- File handles are safe owned Stark values. No `FILE*` pointers are exposed.
- IO error reporting uses Stark enum values, not raw C return codes.
- Platform-specific file descriptors, handles, paths, and encoding behavior are contained inside the platform layer.

## Testing Plan

### Current smoke coverage target

- compile the stdlib into a package artifact
- verify the emitted manifest lists the expected modules
- verify the package can be consumed without importing stdlib source files directly
- keep end-to-end executable coverage for `System.Console` and `System.IO.File` consumption

### Coverage for new modules

- add one build smoke test per new public module family
- validate re-export behavior whenever the package root changes
- add consumption tests for APIs that are meant to work from packaged output only
- add regression tests for error cases as soon as a module starts modeling failures

### Platform-specific tests

- Linux integration tests that verify the stdlib works without any libc linkage
- Windows integration tests that verify the stdlib works without CRT linkage
- cross-platform tests that verify the public API surface matches except where platform differences are documented

### Packaging checks

- verify the package manifest stays in sync with exported module names
- verify the package output location can be overridden
- verify the package remains usable from a clean consumer project with no source dependency

## Packaging Plan

- `scripts/build-stdlib.sh` remains the canonical build entry point
- the script emits both the archive and the package manifest
- package output is compatible with the compiler's `-I` lookup path
- packaging changes preserve the ability to build the stdlib in isolation from application code

## Near-Term Work Ordering

The next implementation pass should focus on:

1. Complete Milestone 6.5 so generics, overloads, and destructors exist for the stdlib surface
2. Implement the Linux platform boundary without libc or glibc
3. Rewrite `System.Console` to call through the platform layer
4. Implement the `File` type with userspace buffering and destructor-driven cleanup
5. Implement UTF-8 to UTF-16 and UTF-16 to UTF-8 conversion for Windows path handling
6. Implement the Windows platform boundary without CRT dependency
7. Extend `System.IO.Path` with join, extension, base-name, and directory-name helpers
8. Add platform-specific integration tests proving the package works without libc/glibc on Linux and without CRT dependency on Windows


## System.Math

Current status:

- The LLVM-intrinsic scalar math slice is implemented for `f32` and `f64`.
- `Math.SinCos` now returns a small `SinCosF32`/`SinCosF64` aggregate backed by `@llvm.sincos.*`.
- The current hardware/compiler-intrinsic math batch is implemented for `Math.Sqrt`, `Math.FusedMultiplyAdd`, `Math.ReciprocalEstimate`, `Math.ReciprocalSqrtEstimate`, `Math.Ceiling`, `Math.Floor`, `Math.Truncate`, `Math.Round`, `Math.Min`, and `Math.Max` on x86/x64 and ARM64.
- On non-Windows targets, only the LLVM-intrinsic-backed math functions currently add `-lm`; the hardware-asm batch does not.
- `Math.ReciprocalEstimate` and `Math.ReciprocalSqrtEstimate` are currently exposed as `f32` only so the shared single-instruction surface stays aligned across x86/x64 and ARM64.
- For v1, x86/x64 hardware math lowerings target a modern baseline rather than per-feature CPU gating. `Math.Ceiling`, `Math.Floor`, `Math.Truncate`, and `Math.Round` therefore assume SSE4.1-capable x86/x64 machines.
- `Math.FusedMultiplyAdd` on x86/x64 currently assumes FMA3 support.
- If older x86/x64 compatibility is added later, prefer a compile-time fallback lowering mode over runtime CPU dispatch so the `System.Math` API surface stays unchanged and builds remain explicit and reproducible.
- `System.BitOperations` is now public and lowers through LLVM bit intrinsics so the backend can pick the documented instruction or short instruction sequence for the active target.

### Use ASM/Compiler Intrinsics for these functions

| Function | x86/x64 Instruction | ARM64 Instruction | Notes |
|---|---|---|---|
| `Math.Sqrt` | `vsqrtsd` / `vsqrtss` | `fsqrt` | AVX; falls to `sqrtsd` on SSE2 |
| `Math.FusedMultiplyAdd` | `vfmadd213sd/ss` | `fmadd` | Requires FMA3; also emits `vfnmadd`, `vfmsub` variants |
| `Math.ReciprocalSqrtEstimate` | `rsqrtss` | `frsqrte` | Approximate; ~12-bit precision; currently `f32` only |
| `Math.ReciprocalEstimate` | `rcpss` | `frecpe` | Approximate 1/x; currently `f32` only |
| `Math.Ceiling` | `vroundsd` (mode 2) | `frintp` | Requires SSE4.1 |
| `Math.Floor` | `vroundsd` (mode 1) | `frintm` | Requires SSE4.1 |
| `Math.Truncate` | `vroundsd` (mode 3) | `frintz` | Promoted to HW intrinsic in .NET 7 |
| `Math.Round` (ToEven) | `vroundsd` (mode 0) | `frintn` | Banker's rounding; SSE4.1 |
| `Math.Min` (float/double) | `vminsd` / `vminss` | `fminnm` | IEEE NaN semantics |
| `Math.Max` (float/double) | `vmaxsd` / `vmaxss` | `fmaxnm` | IEEE NaN semantics |
| `BitOperations.LeadingZeroCount` | `lzcnt` | `clz` | Requires LZCNT (ABM) |
| `BitOperations.TrailingZeroCount` | `tzcnt` | `rbit` + `clz` | Requires BMI1 |
| `BitOperations.PopCount` | `popcnt` | `cnt` (NEON) | Requires POPCNT |
| `BitOperations.RotateLeft/Right` | `rol` / `ror` | `ror` / `rorv` | Native rotation instructions |


### Use LLVM BuiltIns for these:
- @sin, @cos, @tan, @exp, @exp2, @log, @log2, @log10
- LLVM recently added in 2024 llvm.asin.*, llvm.acos.*, llvm.atan.*, llvm.atan2.*, llvm.pow.*, llvm.sinh.*, llvm.cosh.*, llvm.tanh.* and llvm.sincos.*
  - So We will use those too.
- These will map to
  - Math.Sin
  - Math.Cos
  - Math.Tan
  - Math.Exp
  - Math.Exp2
  - Math.Log
  - Math.Log10
  - Math.Asin
  - Math.Atan
  - Math.Atan2
  - Math.Pow
  - Math.Sinh
  - Math.Cosh
  - Math.Tanh
  - Math.SinCos
