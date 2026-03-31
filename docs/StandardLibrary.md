# Standard Library

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

This design does not require Stark-level syscall intrinsics or inline assembly in `v1.x`.

On Linux, the stdlib targets the Linux syscall ABI through a tiny target-specific boundary shim supplied by the runtime/toolchain layer. The stdlib itself stays in Stark source and normal FFI declarations. Direct Stark-level syscall intrinsics remain a post-`v2.0` topic.

## Goals

The standard library provides:

- a stable module layout organized around `System`
- basic console output and input
- file and path operations
- text encoding support
- a platform abstraction layer that talks directly to the OS without libc

User code calls `System.Console` or `System.IO.*` and never touches platform syscalls or Win32 APIs directly. The platform boundary is an internal implementation detail hidden behind the library surface.

## Module Layout

The package root is `System`.

Repository source layout:

- `stdlib/src/System.stark`
- `stdlib/src/System/Console.stark`
- `stdlib/src/System/IO.stark`
- `stdlib/src/System/IO/File.stark`
- `stdlib/src/System/IO/Path.stark`
- `stdlib/src/System/Text.stark`
- `stdlib/src/System/Text/Encoding.stark`
- `stdlib/src/System/Runtime.stark`
- `stdlib/src/System/Runtime/Platform.stark`
- `stdlib/src/System/Runtime/Platform/Linux.stark`
- `stdlib/src/System/Runtime/Platform/Windows.stark`

Public module surface:

- `System`
- `System.Console`
- `System.IO`
- `System.IO.File`
- `System.IO.Path`
- `System.Text`
- `System.Text.Encoding`

Internal modules:

- `System.Runtime`
- `System.Runtime.Platform`
- `System.Runtime.Platform.Linux`
- `System.Runtime.Platform.Windows`

`System.stark` is a pure package root that re-exports the public submodules:

```stark
export import System.Console
export import System.IO
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

`System.Text` is a namespace module that re-exports the text submodules:

```stark
export import System.Text.Encoding
module System.Text
```

## Encoding Model

`System.Text.Encoding` defines the encoding enum used by both `System.IO.File` and any future text conversion APIs. It lives in its own module so that both `System.IO` and `System.Text` can depend on it without circular imports.

```stark
module System.Text.Encoding

public enum Encoding {
    Binary,
    UTF8,
    UTF16,
    UTF32,
}
```

The semantics are:

- `Binary` means raw bytes with no text conversion. Byte APIs always ignore encoding. Text-returning and text-writing APIs treat `Binary` as passthrough over the backing bytes of the Stark text value they operate on.
- `UTF8` means the file stream converts to and from UTF-8. Writing an `ascii` string is a passthrough. Writing a `unicode` string converts UTF-32 to UTF-8 before writing.
- `UTF16` means the file stream converts to and from UTF-16LE. Both `ascii` and `unicode` strings are converted before writing.
- `UTF32` means the file stream converts to and from UTF-32. Writing a `unicode` string is a passthrough. Writing an `ascii` string converts UTF-8 to UTF-32 before writing.

`Binary` is the default encoding for file handles. Console output defaults to `UTF8`.

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
```

Internal implementation:

- On Linux, `Write` and `WriteLine` call the internal platform write boundary on fd `1`. `WriteError` and `WriteErrorLine` call it on fd `2`.
- On Windows, `Write` and `WriteLine` call `WriteFile` on the handle from `GetStdHandle(STD_OUTPUT_HANDLE)`. `WriteError` and `WriteErrorLine` use `GetStdHandle(STD_ERROR_HANDLE)`.
- `unicode` overloads convert UTF-32 to UTF-8 before writing.
- `WriteLine` always appends `\n` on both Linux and Windows. The library does not perform CRLF translation.
- Console output is unbuffered by default. The write goes directly to the OS.

## File API

`System.IO.File` provides safe owned file handles with automatic close on drop.

### The File Struct

`File` is the public owned handle type for file IO. Its concrete representation is intentionally not part of the public API contract. The implementation stores the OS handle, buffering state, buffering policy, and encoding internally.

If Stark reaches this redesign before it has a stronger type-opacity story, the first implementation can still preserve this API shape while treating the exact field layout as stdlib-internal convention rather than stable user-facing structure.

```stark
import System.IO
import System.Text.Encoding
module System.IO.File

public struct File {
    fn IOStatus Close(mut borrow File self);
    fn IOStatus Flush(mut borrow File self);

    fn IOResult<i64> ReadBytes(mut borrow File self, mut i8[] buffer);
    fn IOResult<ascii> ReadText(mut borrow File self);
    fn IOResult<unicode> ReadTextUnicode(mut borrow File self);

    fn IOResult<i64> WriteBytes(mut borrow File self, borrow i8[] data);
    fn IOResult<i64> WriteText(mut borrow File self, borrow ascii text);
    fn IOResult<i64> WriteText(mut borrow File self, borrow unicode text);
    fn IOStatus WriteLine(mut borrow File self, borrow ascii text);
    fn IOStatus WriteLine(mut borrow File self, borrow unicode text);
}

public fn IOResult<File> Open(borrow ascii path, FileMode mode);
public fn IOResult<File> Open(borrow ascii path, FileMode mode, Encoding encoding);
public fn IOStatus Delete(borrow ascii path);
public fn IOStatus Move(borrow ascii oldPath, borrow ascii newPath);
public fn IOResult<bool> Exists(borrow ascii path);
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

Files default to `Full` buffering with an 8192-byte internal buffer. Terminal-connected handles default to `Line` buffering. `None` means every write goes directly to the OS.

### File Ownership and Drop

`File` is an owned type. When a `File` value is dropped at scope exit, the destructor flushes the internal buffer and closes the underlying OS handle.

The destructor is constrained by Stark's destructor rules:

- it does not panic
- it does not synchronize
- it does not allocate

Explicit `Close` is available for code that wants to handle close errors or control close ordering. After an explicit `Close`, the destructor is a no-op.

Because destructors cannot surface rich failure values, implicit destructor cleanup is best-effort. Code that needs flush or close error handling must call `Flush` and `Close` explicitly before scope exit.

### File Encoding Behavior

The `encoding` field on `File` controls what happens during `WriteText`, `ReadText`, and `ReadTextUnicode`:

| String type | File encoding | Write behavior |
|---|---|---|
| `ascii` | `Binary` | passthrough raw bytes |
| `ascii` | `UTF8` | passthrough (ascii is UTF-8) |
| `ascii` | `UTF16` | convert UTF-8 to UTF-16LE |
| `ascii` | `UTF32` | convert UTF-8 to UTF-32 |
| `unicode` | `Binary` | passthrough raw backing bytes |
| `unicode` | `UTF8` | convert UTF-32 to UTF-8 |
| `unicode` | `UTF16` | convert UTF-32 to UTF-16LE |
| `unicode` | `UTF32` | passthrough (unicode is UTF-32) |

`ReadBytes` and `WriteBytes` always ignore encoding and operate on raw bytes regardless.

### File Operations

Internal implementation:

- On Linux, `Open` calls the internal platform open boundary backed by `openat(2)`. `Close` calls the internal close boundary backed by `close(2)`. `ReadBytes` calls the internal read boundary backed by `read(2)`. `WriteBytes` calls the internal write boundary backed by `write(2)`. `Flush` drains the userspace buffer via repeated writes. `Delete` calls the internal delete boundary backed by `unlinkat(2)`. `Move` calls the internal rename boundary backed by `renameat2(2)`. `Exists` uses `newfstatat(2)`.
- On Windows, `Open` calls `CreateFileW`. `Close` calls `CloseHandle`. `ReadBytes` calls `ReadFile`. `WriteBytes` calls `WriteFile`. `Flush` calls `FlushFileBuffers`. `Delete` calls `DeleteFileW`. `Move` calls `MoveFileExW`. `Exists` uses `GetFileAttributesW`.
- Path strings are converted at the platform boundary. On Linux, `ascii` paths pass through as-is. On Windows, `ascii` paths are converted from UTF-8 to UTF-16LE before calling the `W` APIs.

## Path API

`System.IO.Path` provides path manipulation helpers. These are pure library functions with no OS calls unless noted.

```stark
import System.IO
module System.IO.Path

public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public finite law ascii Join(borrow ascii left, borrow ascii right);
public finite law ascii Extension(borrow ascii path);
public finite law ascii BaseName(borrow ascii path);
public finite law ascii DirectoryName(borrow ascii path);

public fn IOResult<ascii> CurrentDirectory();
```

`DirectorySeparator` returns `"/"` on Linux and `"\\"` on Windows. `AlternateDirectorySeparator` returns `"/"` on Windows and `""` on Linux. `PathSeparator` returns `":"` on Linux and `";"` on Windows.

`Join`, `Extension`, `BaseName`, and `DirectoryName` are `finite law` because they are pure, have no side effects, and always return.

`CurrentDirectory` is `fn` because it issues an OS call.

## Platform Abstraction Layer

The platform layer is internal to the standard library. User code never imports it.

### Design

The platform layer defines a minimal set of operations that the rest of the stdlib builds on. Each operation has a Linux implementation and a Windows implementation. The build selects the correct implementation based on the target triple.

In `v1.x`, this selection is done through the package build and runtime/toolchain boundary. It is not dependent on Stark-level inline assembly or syscall intrinsics.

### Linux Implementation

`System.Runtime.Platform.Linux` targets the Linux syscall ABI without depending on libc or glibc.

In `v1.x`, the actual syscall instruction is issued beneath the stdlib by a tiny target-specific boundary shim linked as part of the runtime/toolchain layer. The stdlib calls that boundary through normal Stark declarations.

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
internal fn IOResult<ascii> PlatformGetCwd();
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

These are pure computational functions with no platform dependency. They live in `System.Text` or `System.Text.Encoding` and are shared by both the IO layer and any future user-facing text APIs.

Direct UTF-32 to UTF-16 conversion is preferred over routing through UTF-8 when performance matters.

## Error Model

IO operations that can fail return result values. Stark has no exceptions.

`IOError`, `IOResult<T>`, and `IOStatus` are declared in `System.IO` so they are available to all IO submodules and downstream user code.

The platform layer translates OS error codes into `IOError` values at the boundary:

- On Linux, negative syscall return values are negated errno codes. The platform layer maps `ENOENT` to `NotFound`, `EACCES` and `EPERM` to `PermissionDenied`, `EEXIST` to `AlreadyExists`, `ENOSPC` to `DiskFull`, `EPIPE` to `BrokenPipe`, and everything else to `Unknown`.
- On Windows, `GetLastError()` codes are mapped similarly. `ERROR_FILE_NOT_FOUND` and `ERROR_PATH_NOT_FOUND` become `NotFound`, `ERROR_ACCESS_DENIED` becomes `PermissionDenied`, `ERROR_ALREADY_EXISTS` becomes `AlreadyExists`, and so on.

## Runtime Strategy

The standard library has zero dependency on libc, glibc, musl, or the Windows CRT.

- On Linux, IO is performed through a syscall-backed internal boundary beneath the stdlib. The stdlib does not depend on libc or glibc.
- On Windows, IO is performed through Win32 API calls from `kernel32.dll`. The only system-library dependency is the normal Windows API surface.
- Userspace buffering is implemented entirely in Stark inside the `File` type.

The previous libc-backed implementation is fully replaced.

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
5. Implement the `Encoding` enum and the UTF-8 to UTF-32 and UTF-32 to UTF-8 conversion pair
6. Implement UTF-8 to UTF-16 and UTF-16 to UTF-8 conversion for Windows path handling
7. Implement the Windows platform boundary without CRT dependency
8. Extend `System.IO.Path` with join, extension, base-name, and directory-name helpers
9. Add platform-specific integration tests proving the package works without libc/glibc on Linux and without CRT dependency on Windows
