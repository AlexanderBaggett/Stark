# Standard Library

Remember this language aims to be faster than idiomatic C or Rust on most projects, we must choose the best possible optimization strategy and explore optimization opportunities.


This document describes the current repository standard library design for
Stark and the implemented post-`v1.0` expansion surface.

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
- filesystem operations such as directory listing and directory deletion
- owned heap-backed collections
- minimal thread management
- minimal blocking TCP
- public process id/exit plus Linux-backed process spawn/capture with optional
  stdin input, env, and argv helpers
- explicit test-project assertion and process-output helpers
- text encoding support
- a small dynamic-memory contract for owned standard-library containers
- a platform abstraction layer that avoids stdio and keeps Linux syscall shims,
  Windows Win32 calls, and macOS libSystem/POSIX calls behind standard-library
  APIs

User code calls `System.Console` or `System.IO.*` and never touches platform syscalls or Win32 APIs directly. The platform boundary is an internal implementation detail hidden behind the library surface.

HTTP is intentionally not part of the standard library. HTTP clients and servers should be built as packages on top of `System.Net.Tcp` once the package-management story is ready.

## Reference Docs

The current public module references live here:

- [System](./System.md)
- [System.BitOperations](./System.BitOperations.md)
- [System.C](./System.C.md)
- [System.Console](./System.Console.md)
- [System.Collections](./System.Collections.md)
- [System.Compiler.IntegerFacts](./System.Compiler.IntegerFacts.md)
- [System.FileSystem](./System.FileSystem.md)
- [System.IO](./System.IO.md)
- [System.IO.File](./System.IO.File.md)
- [System.IO.Path](./System.IO.Path.md)
- [System.Math](./System.Math.md)
- [System.Memory](./System.Memory.md)
- [System.Net](./System.Net.md)
- [System.Net.Tcp](./System.Net.Tcp.md)
- [System.Process](./System.Process.md)
- [System.Runtime.Buffer](./System.Runtime.Buffer.md)
- [System.Testing](./System.Testing.md)
- [System.Threading](./System.Threading.md)
- [System.Text](./System.Text.md)
- [System.Text.Interning](./System.Text.Interning.md)
- [System.Toml](./System.Toml.md)

## Module Layout

The package root is `System`.

Repository source layout:

- `stdlib/src/System.stark`
- `stdlib/src/System/BitOperations.stark`
- `stdlib/src/System/C.stark`
- `stdlib/src/System/Compiler/IntegerFacts.stark`
- `stdlib/src/System/Console.stark`
- `stdlib/src/System/Collections.stark`
- `stdlib/src/System/Core.stark`
- `stdlib/src/System/FileSystem.stark`
- `stdlib/src/System/IO.stark`
- `stdlib/src/System/IO/File.stark`
- `stdlib/src/System/IO/Path.stark`
- `stdlib/src/System/Text.stark`
- `stdlib/src/System/Text/Interning.stark`
- `stdlib/src/System/Math.stark`
- `stdlib/src/System/Memory.stark`
- `stdlib/src/System/Net.stark`
- `stdlib/src/System/Net/Tcp.stark`
- `stdlib/src/System/Process.stark`
- `stdlib/src/System/Testing.stark`
- `stdlib/src/System/Threading.stark`
- `stdlib/src/System/Runtime.stark`
- `stdlib/src/System/Runtime/Buffer.stark`
- `stdlib/src/System/Runtime/ConsoleInput.stark`
- `stdlib/src/System/Runtime/Platform.stark`
- `stdlib/src/System/Runtime/Platform/Linux.stark`
- `stdlib/src/System/Runtime/Platform/MacOS.stark`
- `stdlib/src/System/Runtime/Platform/Windows.stark`
- `stdlib/src/System/Syscall.stark`

Current public module surface:

- `System`
- `System.BitOperations`
- `System.C`
- `System.Compiler.IntegerFacts`
- `System.Console`
- `System.Collections`
- `System.Core`
- `System.FileSystem`
- `System.IO`
- `System.IO.File`
- `System.IO.Path`
- `System.Memory`
- `System.Net`
- `System.Net.Tcp`
- `System.Process`
- `System.Runtime.Buffer`
- `System.Testing`
- `System.Text`
- `System.Text.Interning`
- `System.Math`
- `System.Threading`

Internal modules:

- `System.Runtime`
- `System.Runtime.Platform`
- `System.Runtime.Platform.Linux`
- `System.Runtime.Platform.MacOS`
- `System.Runtime.Platform.Windows`
- `System.Syscall`

The repository also currently contains `System.Runtime.ConsoleInput` as a staged
internal helper source file. The active console-input implementation currently
lives in `System.Console`, so `System.Runtime.ConsoleInput` is not part of the
current packaged module graph.

`System.stark` re-exports the public submodules and imports internal runtime and
syscall support needed during package build:

```stark
import System.Runtime
import System.Syscall
import System.Core
import System.Testing
export import System.BitOperations
export import System.Collections
export import System.Console
export import System.FileSystem
export import System.IO
export import System.IO.File
export import System.IO.Path
export import System.Math
export import System.Memory
export import System.Net
export import System.Net.Tcp
export import System.Process
export import System.Threading
module System
```

The repository `System` root now re-exports the implemented source slices for
`System.Memory`, `System.Collections`, `System.FileSystem`, `System.Threading`,
`System.Process` process id/exit and Linux-backed spawn/capture with optional
stdin input, timeout capture, env/argv surface,
the foundational `System.Net` value/result surface, and the
initial `System.Net.Tcp` owned lifecycle, `TcpClient.Connect`,
`TcpClient.Read`, `TcpClient.Write`, `TcpClient.Shutdown`,
`TcpListener.Listen`, `TcpListener.Accept`, and socket-close surface with Linux
syscall and Windows Winsock backends. The versioned `v1.0` baseline remains the
narrower module list in [StandardLibraryBaseline.md](./StandardLibraryBaseline.md).
`System.Text` remains a public module, but callers import it explicitly because
its current low-level text data and caller-buffer APIs are intentionally unsafe.
`System.Testing` is also packaged but imported explicitly so test helpers stay
out of the ordinary `System` root re-export set.

## Concrete APIs Before Streams

The current standard library intentionally exposes concrete owned types instead
of a general `Stream` abstraction:

- `System.IO.File.File` for file handles
- `System.Net.Tcp.TcpClient` and `System.Net.Tcp.TcpListener` for TCP sockets
- `System.FileSystem.Directory` for directory iteration
- owned `System.Collections` containers for heap-backed storage

This keeps ownership, buffering, blocking behavior, allocator use, and platform
handles visible to the compiler and backend. A shared stream-like abstraction is
deferred until Stark has a zero-cost static interface model that does not imply
dynamic dispatch, hidden allocation, or weaker LLVM facts.

`System.IO` re-exports the IO submodules and declares shared IO types:

```stark
export import System.IO.File
export import System.IO.Path
module System.IO

public enum IOError
{
    NotFound,
    PermissionDenied,
    AlreadyExists,
    InvalidPath,
    BrokenPipe,
    DiskFull,
    Unknown(i32),
}

public enum IOResult<T>
{
    Ok(T),
    Err(IOError),
}

public enum IOStatus
{
    Ok,
    Err(IOError),
}
```

`IOStatus` exists because Stark does not treat `void` as a first-class value type. Value-returning APIs use `IOResult<T>`. Effect-only APIs use `IOStatus`.

`System.Text` is the public text module. It declares the shared encoding enum plus the current owned-text helper APIs for view projection, byte-slice-to-ASCII scans, explicit runtime conversion, concatenation, formatting, parsing, and allocation-visible owned text convenience:

```stark
module System.Text

public enum Encoding
{
    Binary,
    UTF8,
    UTF16,
    UTF32,
}

public enum TextError
{
    InvalidFormat,
    Overflow,
}

public enum TextResult<T>
{
    Ok(T),
    Err(TextError),
}

public struct OwnedAscii
{
    finite ascii View(borrow OwnedAscii self);
    finite law i64 Length(borrow OwnedAscii self);
}

public struct OwnedUnicode
{
    finite unicode View(borrow OwnedUnicode self);
    finite law i64 Length(borrow OwnedUnicode self);
}

public struct OwnedUtf16
{
    finite law i64 Length(borrow OwnedUtf16 self);
    finite law i16[] AsSlice(borrow OwnedUtf16 self);
}

public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law i64 AsciiLength(ascii source);
public finite law i64 UnicodeLength(unicode source);
public fn System.Memory.MemoryStatus FromAscii(out OwnedAscii destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAscii(out OwnedAscii destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicode(out OwnedUnicode destination, unicode source);
public fn System.Memory.MemoryStatus FromConstUnicode(out OwnedUnicode destination, const unicode source);
public fn System.Memory.MemoryStatus FromAsciiToUnicode(out OwnedUnicode destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToAscii(out OwnedAscii destination, unicode source);
public fn System.Memory.MemoryStatus FromAsciiToUtf16(out OwnedUtf16 destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUtf16(out OwnedUtf16 destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToUtf16(out OwnedUtf16 destination, unicode source);
public fn System.Memory.MemoryStatus FromUtf16ToUnicode(out OwnedUnicode destination, borrow OwnedUtf16 source);
public fn System.Memory.MemoryStatus FromUtf16ToAscii(out OwnedAscii destination, borrow OwnedUtf16 source);
public finite TextResult<bool> ParseBoolAscii(ascii source);
public finite TextResult<bool> ParseBoolUnicode(unicode source);
public finite TextResult<i8> ParseI8Ascii(ascii source);
public finite TextResult<i8> ParseI8Unicode(unicode source);
public finite TextResult<i16> ParseI16Ascii(ascii source);
public finite TextResult<i16> ParseI16Unicode(unicode source);
public finite TextResult<i24> ParseI24Ascii(ascii source);
public finite TextResult<i24> ParseI24Unicode(unicode source);
public finite TextResult<i32> ParseI32Ascii(ascii source);
public finite TextResult<i32> ParseI32Unicode(unicode source);
public finite TextResult<i48> ParseI48Ascii(ascii source);
public finite TextResult<i48> ParseI48Unicode(unicode source);
public finite TextResult<i64> ParseI64Ascii(ascii source);
public finite TextResult<i64> ParseI64Unicode(unicode source);
public finite TextResult<i96> ParseI96Ascii(ascii source);
public finite TextResult<i96> ParseI96Unicode(unicode source);
public finite TextResult<i128> ParseI128Ascii(ascii source);
public finite TextResult<i128> ParseI128Unicode(unicode source);
public finite TextResult<i192> ParseI192Ascii(ascii source);
public finite TextResult<i192> ParseI192Unicode(unicode source);
public finite TextResult<i256> ParseI256Ascii(ascii source);
public finite TextResult<i256> ParseI256Unicode(unicode source);
public finite TextResult<i384> ParseI384Ascii(ascii source);
public finite TextResult<i384> ParseI384Unicode(unicode source);
public finite TextResult<i512> ParseI512Ascii(ascii source);
public finite TextResult<i512> ParseI512Unicode(unicode source);
public finite TextResult<i768> ParseI768Ascii(ascii source);
public finite TextResult<i768> ParseI768Unicode(unicode source);
public finite TextResult<i1024> ParseI1024Ascii(ascii source);
public finite TextResult<i1024> ParseI1024Unicode(unicode source);
public finite TextResult<u8> ParseU8Ascii(ascii source);
public finite TextResult<u8> ParseU8Unicode(unicode source);
public finite TextResult<u16> ParseU16Ascii(ascii source);
public finite TextResult<u16> ParseU16Unicode(unicode source);
public finite TextResult<u24> ParseU24Ascii(ascii source);
public finite TextResult<u24> ParseU24Unicode(unicode source);
public finite TextResult<u32> ParseU32Ascii(ascii source);
public finite TextResult<u32> ParseU32Unicode(unicode source);
public finite TextResult<u48> ParseU48Ascii(ascii source);
public finite TextResult<u48> ParseU48Unicode(unicode source);
public finite TextResult<u64> ParseU64Ascii(ascii source);
public finite TextResult<u64> ParseU64Unicode(unicode source);
public finite TextResult<u96> ParseU96Ascii(ascii source);
public finite TextResult<u96> ParseU96Unicode(unicode source);
public finite TextResult<u128> ParseU128Ascii(ascii source);
public finite TextResult<u128> ParseU128Unicode(unicode source);
public finite TextResult<u192> ParseU192Ascii(ascii source);
public finite TextResult<u192> ParseU192Unicode(unicode source);
public finite TextResult<u256> ParseU256Ascii(ascii source);
public finite TextResult<u256> ParseU256Unicode(unicode source);
public finite TextResult<u384> ParseU384Ascii(ascii source);
public finite TextResult<u384> ParseU384Unicode(unicode source);
public finite TextResult<u512> ParseU512Ascii(ascii source);
public finite TextResult<u512> ParseU512Unicode(unicode source);
public finite TextResult<u768> ParseU768Ascii(ascii source);
public finite TextResult<u768> ParseU768Unicode(unicode source);
public finite TextResult<u1024> ParseU1024Ascii(ascii source);
public finite TextResult<u1024> ParseU1024Unicode(unicode source);
public finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
public finite bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
public finite bool TryFormatBoolAscii(rawmutptr<Ascii> destination, bool value);
public finite bool TryFormatI8Ascii(rawmutptr<Ascii> destination, i8 value);
public finite bool TryFormatI16Ascii(rawmutptr<Ascii> destination, i16 value);
public finite bool TryFormatI24Ascii(rawmutptr<Ascii> destination, i24 value);
public finite bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32 value);
public finite bool TryFormatI48Ascii(rawmutptr<Ascii> destination, i48 value);
public finite bool TryFormatI64Ascii(rawmutptr<Ascii> destination, i64 value);
public finite bool TryFormatI96Ascii(rawmutptr<Ascii> destination, i96 value);
public finite bool TryFormatI128Ascii(rawmutptr<Ascii> destination, i128 value);
public finite bool TryFormatI192Ascii(rawmutptr<Ascii> destination, i192 value);
public finite bool TryFormatI256Ascii(rawmutptr<Ascii> destination, i256 value);
public finite bool TryFormatI384Ascii(rawmutptr<Ascii> destination, i384 value);
public finite bool TryFormatI512Ascii(rawmutptr<Ascii> destination, i512 value);
public finite bool TryFormatI768Ascii(rawmutptr<Ascii> destination, i768 value);
public finite bool TryFormatI1024Ascii(rawmutptr<Ascii> destination, i1024 value);
public finite bool TryFormatU8Ascii(rawmutptr<Ascii> destination, u8 value);
public finite bool TryFormatU16Ascii(rawmutptr<Ascii> destination, u16 value);
public finite bool TryFormatU24Ascii(rawmutptr<Ascii> destination, u24 value);
public finite bool TryFormatU32Ascii(rawmutptr<Ascii> destination, u32 value);
public finite bool TryFormatU48Ascii(rawmutptr<Ascii> destination, u48 value);
public finite bool TryFormatU64Ascii(rawmutptr<Ascii> destination, u64 value);
public finite bool TryFormatU96Ascii(rawmutptr<Ascii> destination, u96 value);
public finite bool TryFormatU128Ascii(rawmutptr<Ascii> destination, u128 value);
public finite bool TryFormatU192Ascii(rawmutptr<Ascii> destination, u192 value);
public finite bool TryFormatU256Ascii(rawmutptr<Ascii> destination, u256 value);
public finite bool TryFormatU384Ascii(rawmutptr<Ascii> destination, u384 value);
public finite bool TryFormatU512Ascii(rawmutptr<Ascii> destination, u512 value);
public finite bool TryFormatU768Ascii(rawmutptr<Ascii> destination, u768 value);
public finite bool TryFormatU1024Ascii(rawmutptr<Ascii> destination, u1024 value);
public finite bool TryFormatF64Ascii(rawmutptr<Ascii> destination, f64 value);
public finite bool TryFormatF32Ascii(rawmutptr<Ascii> destination, f32 value);
public fn bool TryFormatBoolUnicode(rawmutptr<Unicode> destination, bool value);
public fn bool TryFormatI8Unicode(rawmutptr<Unicode> destination, i8 value);
public fn bool TryFormatI16Unicode(rawmutptr<Unicode> destination, i16 value);
public fn bool TryFormatI24Unicode(rawmutptr<Unicode> destination, i24 value);
public fn bool TryFormatI32Unicode(rawmutptr<Unicode> destination, i32 value);
public fn bool TryFormatI48Unicode(rawmutptr<Unicode> destination, i48 value);
public fn bool TryFormatI64Unicode(rawmutptr<Unicode> destination, i64 value);
public fn bool TryFormatI96Unicode(rawmutptr<Unicode> destination, i96 value);
public fn bool TryFormatI128Unicode(rawmutptr<Unicode> destination, i128 value);
public fn bool TryFormatI192Unicode(rawmutptr<Unicode> destination, i192 value);
public fn bool TryFormatI256Unicode(rawmutptr<Unicode> destination, i256 value);
public fn bool TryFormatI384Unicode(rawmutptr<Unicode> destination, i384 value);
public fn bool TryFormatI512Unicode(rawmutptr<Unicode> destination, i512 value);
public fn bool TryFormatI768Unicode(rawmutptr<Unicode> destination, i768 value);
public fn bool TryFormatI1024Unicode(rawmutptr<Unicode> destination, i1024 value);
public fn bool TryFormatU8Unicode(rawmutptr<Unicode> destination, u8 value);
public fn bool TryFormatU16Unicode(rawmutptr<Unicode> destination, u16 value);
public fn bool TryFormatU24Unicode(rawmutptr<Unicode> destination, u24 value);
public fn bool TryFormatU32Unicode(rawmutptr<Unicode> destination, u32 value);
public fn bool TryFormatU48Unicode(rawmutptr<Unicode> destination, u48 value);
public fn bool TryFormatU64Unicode(rawmutptr<Unicode> destination, u64 value);
public fn bool TryFormatU96Unicode(rawmutptr<Unicode> destination, u96 value);
public fn bool TryFormatU128Unicode(rawmutptr<Unicode> destination, u128 value);
public fn bool TryFormatU192Unicode(rawmutptr<Unicode> destination, u192 value);
public fn bool TryFormatU256Unicode(rawmutptr<Unicode> destination, u256 value);
public fn bool TryFormatU384Unicode(rawmutptr<Unicode> destination, u384 value);
public fn bool TryFormatU512Unicode(rawmutptr<Unicode> destination, u512 value);
public fn bool TryFormatU768Unicode(rawmutptr<Unicode> destination, u768 value);
public fn bool TryFormatU1024Unicode(rawmutptr<Unicode> destination, u1024 value);
public fn bool TryFormatF64Unicode(rawmutptr<Unicode> destination, f64 value);
public fn bool TryFormatF32Unicode(rawmutptr<Unicode> destination, f32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(bool value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i8 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i16 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i24 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i48 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i96 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i128 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i192 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i256 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i384 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i512 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i768 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i1024 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u8 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u16 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u24 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u48 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u96 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u128 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u192 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u256 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u384 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u512 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u768 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u1024 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(Encoding value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(TextError value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(bool value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i8 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i16 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i24 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i48 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i96 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i128 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i192 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i256 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i384 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i512 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i768 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i1024 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u8 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u16 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u24 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u48 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u96 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u128 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u192 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u256 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u384 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u512 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u768 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u1024 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(Encoding value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(TextError value);
```

## Encoding Model

`System.Text` defines the `Encoding` enum used by both `System.IO.File` and explicit text conversion APIs. The owned text container types themselves are now core language types, so `System.Text` focuses on the shared encoding enum plus helper functions that operate on those core containers.

## Function Kind Policy

Standard-library APIs should use Stark's function kinds wherever the contract is
strong enough:

- use `finite law` for value-only helpers, metadata reads, constants, and
  projections that are pure and always return
- use `law` for read-only helpers that are pure but whose return is conditional
  on source-level preconditions not fully encoded in the signature
- use `finite` only for effectful APIs that still guarantee progress and return
  without needing purity
- use ordinary `fn` for IO, allocation, deallocation, mutation, synchronization,
  blocking, scheduler interaction, networking, and operations that depend on
  external platform state

The standard library should prefer stronger function kinds when they are true,
but it must not overstate purity or guaranteed return just to make an API look
more optimized.

The semantics are:

- `Binary` means the file handle does not request an alternate multibyte text encoding. Byte APIs always ignore encoding. `ascii` text writes are passthrough UTF-8 bytes, and `unicode` text writes use the same UTF-8 platform text path as the internal platform helper used by the owned-file surface.
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
- `System.Text.OwnedAscii`, `System.Text.OwnedUnicode`, and `System.Text.OwnedUtf16` for allocation-backed owned text/code-unit buffers returned by convenience APIs with drop cleanup
- `System.Text.AsciiLength(ascii)` and `System.Text.UnicodeLength(unicode)` for immutable view lengths; raw data extraction is internal to standard-library/platform boundaries
- `System.Text.ParseBoolAscii(ascii)` and `System.Text.ParseBoolUnicode(unicode)` for exact lowercase bool parsing through `TextResult<bool>`
- `System.Text.ParseI*Ascii`/`ParseI*Unicode` and `System.Text.ParseU*Ascii`/`ParseU*Unicode` through 1024-bit signed and unsigned widths for exact base-10 integer parsing through `TextResult<T>`
- `System.Text.FromAsciiToUnicode`, `FromUnicodeToAscii`, `FromAsciiToUtf16`, `FromUnicodeToUtf16`, `FromUtf16ToUnicode`, and `FromUtf16ToAscii` for owned UTF-8, UTF-16LE, and UTF-32 conversion through `MemoryStatus`
- `System.Text.TryConcatAscii(rawmutptr<Ascii>, ascii, ascii)` and `System.Text.TryConcatUnicode(rawmutptr<Unicode>, unicode, unicode)` for explicit concatenation into caller-provided storage
- `System.Text.TryFormatBoolAscii`, fixed-width integer `Ascii` formatting helpers through 1024-bit widths, including Stark's non-power-of-two integer widths, the first fixed-six `f32`/`f64` formatting helpers, and the matching `Unicode` forms for explicit caller-owned value formatting
- `System.Text.ToAscii` / `System.Text.ToUnicode` and method-style `value.ToAscii()` / `value.ToUnicode()` for allocation-visible owned text conversion of `bool`, all signed and unsigned integer widths from 8 bits through 1024 bits, `f32`, `f64`, `System.Text.Encoding`, and `System.Text.TextError` through `System.Memory.MemoryResult<T>`

These APIs make allocation visible in user code: owned conversions return `MemoryStatus`, owned formatters return `MemoryResult<T>`, and the remaining unsafe `TryConcat*`/`TryFormat*` helpers are fixed-buffer compiler hooks for no-allocation text construction.

For compile-time constants, `"left" + "right"` folds to one ordinary text
constant with no runtime allocation.

## Console API

`System.Console` is the user-facing module for terminal output and input. It replaces the previous `System.IO.Stdout` and `System.IO.Stderr` split.

```stark
module System.Console

public fn IOStatus Write(borrow ascii text);
public fn IOStatus Write(borrow unicode text);
public fn IOStatus Write(mut borrow System.Text.OwnedAscii text);
public fn IOStatus Write(mut borrow System.Text.OwnedUnicode text);
public fn IOStatus Write(borrow i8[min max][] source);
public fn IOStatus WriteLine(borrow ascii text);
public fn IOStatus WriteLine(borrow unicode text);
public fn IOStatus WriteLine(mut borrow System.Text.OwnedAscii text);
public fn IOStatus WriteLine(mut borrow System.Text.OwnedUnicode text);
public fn IOStatus WriteLine(borrow i8[min max][] source);
public fn IOStatus WriteError(borrow ascii text);
public fn IOStatus WriteError(borrow unicode text);
public fn IOStatus WriteError(mut borrow System.Text.OwnedAscii text);
public fn IOStatus WriteError(mut borrow System.Text.OwnedUnicode text);
public fn IOStatus WriteError(borrow i8[min max][] source);
public fn IOStatus WriteErrorLine(borrow ascii text);
public fn IOStatus WriteErrorLine(borrow unicode text);
public fn IOStatus WriteErrorLine(mut borrow System.Text.OwnedAscii text);
public fn IOStatus WriteErrorLine(mut borrow System.Text.OwnedUnicode text);
public fn IOStatus WriteErrorLine(borrow i8[min max][] source);
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadLine();
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ReadAsciiLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadUnicodeLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> Read();
```

Internal implementation:

- On Linux, the current `ascii` output path uses internal syscall-backed write shims on fd `1` and fd `2`.
- On Windows, `Write` and `WriteLine` call `WriteFile` on the handle from `GetStdHandle(STD_OUTPUT_HANDLE)`. `WriteError` and `WriteErrorLine` use `GetStdHandle(STD_ERROR_HANDLE)`.
- On Linux, `unicode` overloads convert UTF-32 to UTF-8 in the stdlib and then write through the syscall-backed fd boundary.
- `ReadLine` returns the next UTF-8 decoded line from stdin as owned `Unicode` without the trailing newline. `ReadUnicodeLine` is the explicit-name alias for that behavior. `ReadAsciiLine` returns the next line as owned byte-oriented `Ascii`. `Read` returns the next UTF-8 decoded code point as a one-element owned `Unicode`.
- `ReadLine`, `ReadUnicodeLine`, `ReadAsciiLine`, and `Read` return `System.Memory.MemoryResult<T>` so allocation and layout failures remain visible. End of input returns `Ok` with empty owned text.
- The current input implementation uses a shared buffered stdin handle. Returned input values own their text storage and can be kept by the caller.
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

public struct File
{
    finite law bool IsOpen(borrow File self);
    fn System.IO.IOStatus Close(mut borrow File self);
    fn System.IO.IOStatus Flush(mut borrow File self);
    fn System.IO.IOStatus SyncAll(mut borrow File self);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Seek(mut borrow File self, i64[min max] offset, SeekOrigin origin);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Read(mut borrow File self, mut borrow i8[min max][] destination);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow i8[min max][] source);
    fn System.IO.IOStatus WriteText(mut borrow File self, ascii text);
    fn System.IO.IOStatus WriteText(mut borrow File self, unicode text);
    fn System.IO.IOStatus WriteLine(mut borrow File self, ascii text);
    fn System.IO.IOStatus WriteLine(mut borrow File self, unicode text);
}

public fn System.IO.IOResult<File> Open(ascii path, FileMode mode);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, FileBuffering buffering);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding, FileBuffering buffering);

public fn System.IO.IOStatus ReadAllBytesInto(ascii path, mut borrow System.Runtime.Buffer.DynamicByteBuffer destination);
public fn System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer> ReadAllBytes(ascii path);
public fn System.IO.IOStatus ReadAllTextInto(ascii path, mut borrow System.Text.OwnedAscii destination);
public fn System.IO.IOResult<System.Text.OwnedAscii> ReadAllText(ascii path);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow i8[min max][] source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow i8[min max][] source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteAllText(ascii path, ascii text);
public fn System.IO.IOStatus WriteAllText(ascii path, unicode text);
public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, ascii text);
public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, unicode text);

public fn System.IO.IOStatus Delete(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<bool> Exists(ascii path);
```

Methods on a `public struct` inherit the struct's visibility unless explicitly
narrowed. Field visibility remains a separate type-opacity and representation
stability topic.

`IsOpen` is `finite law` because it only reads local handle state and always
returns. The remaining file methods are ordinary `fn` because they perform IO,
mutate handle or buffer state, or depend on filesystem state.

### File Modes

```stark
public enum FileMode
{
    Read,
    Write,
    CreateNew,
    Append,
    ReadWrite,
}
```

### File Buffering

```stark
public enum FileBuffering
{
    None,
    Line,
    Full,
}
```

Files default to `Full` buffering with an 8192-byte internal buffer. On Linux, the default `Open(...)` overload now checks the opened handle with `ioctl(TCGETS)` and switches to `Line` buffering for terminal-connected handles. `None` means every write goes directly to the OS.

`FileMode.CreateNew` uses exclusive create and returns `IOError.AlreadyExists`
if the target already exists. The `WriteAll*Atomic` helpers use same-directory
exclusive temporary files, write and `SyncAll` the full payload, close the file,
then publish with `Move`; failed attempts best-effort delete their temporary
path.

### File Ownership and Drop

`File` is an owned type. When a `File` value is dropped at scope exit, the destructor flushes the internal buffer and closes the underlying OS handle.

The destructor is constrained by Stark's destructor rules:

- it does not panic
- it does not synchronize
- it does not allocate

Explicit `Close` is available for code that wants to handle close ordering. After an explicit `Close`, the destructor is a no-op.

Because destructors cannot surface rich failure values, implicit destructor cleanup is best-effort. Code that needs flush or close error handling must call `Flush` and `Close` explicitly before scope exit. Code that needs durable-storage synchronization must call `SyncAll` explicitly.

### File Encoding Behavior

The `encoding` field and `System.Text.Encoding` enum are in place, but the current Milestone 7 slice is still narrower than the eventual text-IO design:

- owned `File` text writes support both `ascii` and `unicode`
- internal platform handoff helpers support both `ascii` and `unicode`
- on Linux, the current `unicode` write path converts UTF-32 to UTF-8 before issuing the write syscall
- owned-file `UTF8`, `UTF16`, and `UTF32` writes now honor the selected encoding for both `ascii` and `unicode`
- owned-file `UTF16` and `UTF32` writes flush any pending buffered ascii data before writing encoded bytes directly
- byte-level file reads and writes are implemented; whole-file text/byte helpers
  and line-oriented `File.ReadLine` / `ReadLines` helpers are available

`Read` and `Write` always ignore encoding and operate on raw bytes regardless.

### File Operations

Internal implementation:

- On Linux, `Open` calls the internal platform open boundary backed by `openat(2)`. `CreateNew` uses `O_CREAT | O_EXCL`. `Close` calls the internal close boundary backed by `close(2)`. `Read` calls the internal read boundary backed by `read(2)`. `Write` calls the internal write boundary backed by `write(2)`. `Flush` drains Stark userspace buffers. `SyncAll` calls `fsync`. `Delete` calls the internal delete boundary backed by `unlinkat(2)`. `Move` calls the internal rename boundary backed by `renameat2(2)`. `Exists` uses `newfstatat(2)`.
- On Windows, `Open` calls `CreateFileW`. `Close` calls `CloseHandle`. `Read` calls `ReadFile`. `Write` calls `WriteFile`. `Flush` drains Stark userspace buffers. `SyncAll` calls `FlushFileBuffers`. `Delete` calls `DeleteFileW`. `Move` calls `MoveFileExW`. `Exists` uses `GetFileAttributesW`.
- On macOS, `Open` calls `open`. `Close` calls `close`. `Read` calls `read`. `Write` calls `write`. `Flush` drains Stark userspace buffers. `SyncAll` calls `fsync`. `Delete` calls `unlink`. `Move` calls `rename`. `Exists` and file kind checks use `stat`.
- Path strings are converted at the platform boundary. On Linux and macOS, `ascii` paths pass through as-is. On Windows, `ascii` paths are converted from UTF-8 to UTF-16LE before calling the `W` APIs, and `GetCurrentDirectoryW` results are converted back to UTF-8 for `System.IO.Path.CurrentDirectory`.

## Path API

`System.IO.Path` provides path manipulation helpers. These are pure library functions with no OS calls unless noted.

```stark
import System.IO
module System.IO.Path

public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public finite law bool GlobMatches(ascii pattern, ascii path);
public fn System.Memory.MemoryStatus CurrentDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> CurrentDirectory();
public fn System.Memory.MemoryStatus TempDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempDirectory();
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii left, ascii right)
    where overlap(destination, left), overlap(destination, right), overlap(left, right);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third, ascii fourth);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right);
public fn System.Memory.MemoryStatus TryTempName(mut borrow System.Text.OwnedAscii destination, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempName(ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryStatus TryTempPathIn(mut borrow System.Text.OwnedAscii destination, ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempPathIn(ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix);
public fn System.Memory.MemoryStatus TryNormalizeLexically(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryFullPath(mut borrow System.Text.OwnedAscii destination, ascii path);
public fn System.Memory.MemoryStatus TryChangeExtension(mut borrow System.Text.OwnedAscii destination, ascii path, ascii extension);
public struct PathFacts;
public finite law PathFacts GetFacts(ascii path);
public finite law ascii Extension(ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii DirectoryName(ascii path);
public finite law ascii RootName(ascii path);
public finite law bool IsRooted(ascii path);
public finite law bool IsAbsolute(ascii path);
public finite law bool IsRelative(ascii path);
```

`DirectorySeparator` returns `"/"` on Linux and macOS and `"\\"` on Windows. `AlternateDirectorySeparator` returns `"/"` on Windows and `""` on Linux and macOS. `PathSeparator` returns `":"` on Linux and macOS and `";"` on Windows.

`Extension`, `BaseName`, and `DirectoryName` are `finite law` because they are pure, have no side effects, and always return.

`GetFacts` computes the reusable component ranges for callers that need several pieces of the same path. `PathFacts` exposes view and length helpers for the full path, root name, extension, base name, and directory name without rescanning, plus rooted/absolute/relative checks.

`GlobMatches` is allocation-free and path-segment aware. `*` matches within one
segment, `?` matches one unit within one segment, and `**` as a full segment
matches zero or more path segments.

`TryJoin` uses caller-owned `System.Text.OwnedAscii` storage rather than allocating hidden storage. It supports two, three, and four path parts, explicitly permits overlap among the destination and input views, snapshots when necessary, and returns `MemoryStatus` so allocation and layout failures remain explicit.

`Join` allocates an owned `System.Text.OwnedAscii` result through `System.Memory` and returns `System.Memory.MemoryResult<T>`, so allocation failure remains visible. It uses the same separator normalization rules as `TryJoin`.

`TempDirectory` appends the current platform temp root into caller-owned
storage or returns an owned-text `MemoryResult`. `TryTempName` / `TempName`
build explicit temp candidate names from a caller-supplied prefix, attempt, and
suffix plus the current process id. Empty prefixes default to `"stark-"`.
`TryTempPathIn` / `TempPathIn` join that candidate under an explicit parent
directory. These helpers do not create files or maintain hidden counters;
collision retries belong to the filesystem operation that observes collisions.

`TryNormalizeLexically` folds empty, `.`, and `..` segments without filesystem access. `TryFullPath` combines relative paths with `CurrentDirectory` before lexical normalization. `TryChangeExtension` replaces/removes the final extension and inserts a leading `.` when the extension argument omits it.

`CurrentDirectory` and `TempDirectory` are `fn` because they issue OS/platform
queries. They append into caller-owned `System.Text.OwnedAscii` storage or
return owned-text `MemoryResult` values; raw platform buffers stay internal.

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
| event wait | `epoll_wait` with `epoll_create1` and `epoll_ctl` | 232, 291, 233 |
| seek | `lseek` | 8 |
| flush | userspace buffer drain via `write` | 1 |
| delete | `unlinkat` | 263 |
| rename | `renameat` | 264 |
| stat or exists | `newfstatat` | 262 |
| getcwd | `getcwd` | 79 |
| process id | `getpid` | 39 |
| exit | `exit_group` | 231 |
| ioctl | `ioctl` | 16 |
| synchronization wait/wake | `futex` | 202 |
| thread virtual memory | `mmap`, `munmap` | 9, 11 |
| thread creation | `clone` | 56 |

`ioctl` is needed to detect whether a file descriptor is a terminal for buffering strategy selection.

The current implementation status is:

- `getcwd` is syscall-backed on Linux
- `ascii` and `unicode` console output are syscall-backed on Linux
- file open/read/write/close/seek/delete/move/exists are syscall-backed on Linux
- terminal detection uses `ioctl(TCGETS)` on Linux and feeds the default file-buffering policy
- internal readable/writable event waits use `epoll` on Linux
- internal thread create/join/detach uses raw `clone`, `mmap`/`munmap`, futex wait/wake, and an internal reference count on x86_64 Linux
- internal futex wait/wake helpers are available for no-libc thread join and synchronization work
- packaged Linux stdlib builds are regression-tested to avoid libc/glibc symbol dependencies

stdout and stderr are fd `1` and fd `2`. They exist at process start with no setup required.

### macOS Implementation

`System.Runtime.Platform.MacOS` calls libSystem/POSIX APIs directly for the OS
backend. It does not route file, console, directory, or socket IO through C
stdio. Native executable linking on macOS still needs Apple's SDK/Command Line
Tools for platform libraries such as `libSystem`; standalone LLVM/Clang remains
enough for LLVM IR and object emission.

The current macOS backend uses:

| Operation | macOS API |
|---|---|
| write | `write` |
| read | `read` |
| open | `open` |
| close | `close` |
| sync all | `fsync` |
| seek | `lseek` |
| delete | `unlink` |
| rename | `rename` |
| exists and file kind | `stat` |
| directories | `opendir`, `readdir`, `closedir`, `mkdir`, `rmdir` |
| current directory | `getcwd` |
| terminal detect | `isatty` |
| process id | `getpid` |
| exit | `exit` |
| threads | `pthread_create`, `pthread_join`, `pthread_detach`, `sched_yield`, `nanosleep` |
| sockets | `socket`, `connect`, `bind`, `listen`, `accept`, `recv`, `send`, `readv`, `writev`, `shutdown`, `poll` |
| synchronization wait/wake | `os_sync_wait_on_address`, `os_sync_wake_by_address_any`, `os_sync_wake_by_address_all` |
| allocator OS backing | `malloc`, `realloc`, `free` under Stark's runtime allocator |

Directory enumeration reads Darwin `dirent` entries directly from `readdir`,
including the `d_type` byte when available. File existence and file/directory
kind checks use Darwin `stat` mode bits. Thread joins preserve the `i32` returned
by the Stark entry function through `pthread_join` without heap-allocating a
return-code box.

The public metadata surface now includes size, modified time, permissions, and
entry kind on Linux, macOS, and Windows through `System.FileSystem.Metadata`.
Windows permissions are POSIX-like bits synthesized from file attributes.
Owner/group facts, symlink target reads, and a public monotonic clock API are not
exposed yet; benchmark timing is currently host-harness driven.

### Windows Implementation

`System.Runtime.Platform.Windows` calls Win32 APIs from `kernel32.dll`. No CRT dependency exists.

The required Win32 APIs are:

| Operation | Win32 API |
|---|---|
| write | `WriteFile` |
| read | `ReadFile` |
| open | `CreateFileW` |
| close | `CloseHandle` |
| flush | userspace buffer drain |
| sync all | `FlushFileBuffers` |
| seek | `SetFilePointerEx` |
| delete | `DeleteFileW` |
| rename | `MoveFileExW` |
| exists | `GetFileAttributesW` |
| getcwd | `GetCurrentDirectoryW` |
| process id | `GetCurrentProcessId` |
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

internal enum FileOpenResult
{
    Ok(rawptr<i8[min max]>),
    Err(i32[min max]),
}

internal unsafe fn FileOpenResult OpenFileReadResult(ascii path);
internal unsafe fn FileOpenResult OpenFileWriteResult(ascii path);
internal unsafe fn FileOpenResult OpenFileCreateNewResult(ascii path);
internal unsafe fn FileOpenResult OpenFileAppendResult(ascii path);
internal unsafe fn FileOpenResult OpenFileReadWriteResult(ascii path);
internal unsafe fn rawptr<i8[min max]> OpenFileRead(ascii path);
internal unsafe fn rawptr<i8[min max]> OpenFileWrite(ascii path);
internal unsafe fn rawptr<i8[min max]> OpenFileCreateNew(ascii path);
internal unsafe fn rawptr<i8[min max]> OpenFileAppend(ascii path);
internal unsafe fn rawptr<i8[min max]> OpenFileReadWrite(ascii path);
internal unsafe fn i64[min max] ReadFileBytes(rawmutptr<i8[min max]>[length] buffer, u64[0 2 ** 63 - 1] length, rawptr<i8[min max]> handle);
internal unsafe fn i64[min max] WriteFileBytes(rawptr<i8[min max]>[length] buffer, u64[0 2 ** 63 - 1] length, rawptr<i8[min max]> handle);
internal unsafe fn i32[min max] CloseFile(rawptr<i8[min max]> handle);
internal unsafe fn i32[min max] FlushFile(rawptr<i8[min max]> handle);
internal fn i32[min max] DeleteFile(ascii path);
internal fn i32[min max] MoveFile(ascii oldPath, ascii newPath);
internal fn bool FileExists(ascii path);
internal fn bool PathExists(ascii path);
internal unsafe fn bool TryCurrentDirectory(rawmutptr<Ascii> destination);
internal unsafe fn bool TryTempDirectory(rawmutptr<Ascii> destination);
internal unsafe fn bool IsTerminal(rawptr<i8[min max]> handle);
internal unsafe fn rawptr<i8[min max]> OpenStdout();
internal unsafe fn rawptr<i8[min max]> OpenStderr();
internal fn i32[min max] ProcessId();
internal fn void ExitProcess(i32[min max] code);
internal unsafe fn rawmutptr<i8[min max]> StartThread(fnptr<fn i32[min max]()> entry);
internal unsafe fn i32[min max] JoinThread(rawmutptr<i8[min max]> handle, rawmutptr<i32[min max]> exitCode);
internal unsafe fn i32[min max] DetachThread(rawmutptr<i8[min max]> handle);
internal fn void YieldThread();
internal fn void SleepThreadMilliseconds(i64[min max] milliseconds);
internal fn i32 WaitReadable(rawptr<i8> handle, i32 timeoutMilliseconds);
internal fn i32 WaitWritable(rawptr<i8> handle, i32 timeoutMilliseconds);
internal fn i32 FutexWait(rawptr<i32> address, i32 expected);
internal fn i32 FutexWake(rawptr<i32> address, i32 count);
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

These are implemented today through owned `System.Text` helpers:

- `FromAsciiToUnicode`
- `FromAsciiToUtf16`
- `FromUtf16ToUnicode`
- `FromUnicodeToAscii`
- `FromUnicodeToUtf16`
- `FromUtf16ToAscii`

Direct UTF-32 to UTF-16 conversion is preferred over routing through UTF-8 when performance matters.

## Error Model

IO operations that can fail return result values. Stark has no exceptions.

`IOError`, `IOResult<T>`, and `IOStatus` are declared in `System.IO` so they are available to all IO submodules and downstream user code.

The platform layer translates OS error codes into `IOError` values at the boundary:

- On Linux, negative syscall return values are negated errno codes. The platform layer maps `ENOENT` to `NotFound`, `EACCES` and `EPERM` to `PermissionDenied`, `EEXIST` to `AlreadyExists`, `ENOSPC` to `DiskFull`, `EPIPE` to `BrokenPipe`, and everything else to `Unknown`.
- On Windows, `GetLastError()` codes are mapped similarly. `ERROR_FILE_NOT_FOUND` and `ERROR_PATH_NOT_FOUND` become `NotFound`, `ERROR_ACCESS_DENIED` becomes `PermissionDenied`, `ERROR_ALREADY_EXISTS` becomes `AlreadyExists`, and so on.

## Runtime Strategy

The target design is zero explicit dependency on libc, glibc, musl, or the
Windows CRT from Stark-owned runtime and standard-library code, except where
user code explicitly opts into a foreign library or a target truly requires a
platform boundary. Clang and LLVM may still be used as the native toolchain.

If LLVM or the native toolchain lowers Stark-emitted LLVM IR to helper symbols
such as libm functions, `memset`, `memcpy`, `memmove`, or hosted startup code,
that is treated as a toolchain/backend dependency rather than a C-backed
standard-library implementation.

- On Linux, the current Milestone 7 slice uses syscall-backed boundaries for `getcwd`, process id, process exit, console output, and file-descriptor-based file operations.
- Owned-file unicode writes and the shared text-conversion APIs are implemented
  for the current surface, and the Linux platform layer no longer depends on
  libc/glibc for the implemented paths.
- On Windows, the target remains Win32 API calls from `kernel32.dll`.
- `System.Runtime.Buffer` now provides the internal fixed-size linear and ring buffer primitives used by stdlib IO. `File` uses those foundations for `None` / `Line` / `Full` write-buffering policy, and the default Linux path now switches between `Full` and `Line` based on `ioctl` terminal detection. `Console` still writes directly to the OS today.

Current explicit runtime dependency caveats:

- The `System.Memory` allocator and compiler-emitted heap-local helper now
  lower through Stark-owned runtime helpers instead of explicit
  `malloc`, `realloc`, or `free` calls in standard-library source. The macOS
  runtime allocator backend intentionally maps those helpers to libSystem
  `malloc`, `realloc`, and `free`.
- Small and medium allocator buckets may satisfy `Reallocate` in place when the
  new size and alignment still fit the bucket; otherwise the runtime uses the
  conservative allocate-copy-free fallback.
- The allocator benchmark harness lives under `benchmarks/allocator` and should
  remain quick enough for ordinary development smoke runs. Compile-only
  benchmark sources under `benchmarks/collections` and `benchmarks/text` are
  validated by compiler tests until those APIs can run as executable timing
  benchmarks.
- Source-module and package linkage can pull in object files for re-exported
  modules that were not directly called, so explicit C-runtime validation must
  inspect produced objects, archives, and final executables.

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

export fn i32 main()
{
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

### Dependency profiles

The standard library uses two dependency profiles:

- **Default hosted profile**: the normal CLI build mode. It may rely on the
  native LLVM/Clang toolchain, hosted startup objects, platform import
  libraries, and backend-selected helper routines. Those are tracked separately
  from Stark-owned standard-library implementation choices.
- **Explicit-C-runtime-free profile**: an audit profile for Stark-owned runtime
  and standard-library code. Implemented Linux platform paths use syscalls or
  Stark-owned runtime helpers instead of libc wrappers. Implemented Windows
  platform paths use `kernel32`, Winsock, and selected OS allocation APIs
  instead of CRT allocation or IO helpers.

User-written `ffi` calls may still target C libraries. Those calls are explicit
application dependencies, not dependencies introduced by the standard library.

### Runtime boundary

- The library depends on compiler-emitted or toolchain-provided runtime symbols internally, but user code does not.
- Startup and shutdown behavior is coordinated between the compiler toolchain and `System.Runtime`.
- Any API that needs process termination or host interaction is routed through a library wrapper.
- A hosted C-style executable link is allowed to use platform C startup code.
  That startup code is classified as a toolchain/entrypoint dependency, not as
  a C-backed standard-library implementation.
- Toolchain-lowered helpers such as libm calls or backend-emitted
  `memset`/`memcpy`/`memmove` are reported separately from explicit
  Stark-emitted C-runtime calls.

### Allocator boundary

- Console output avoids allocation.
- File buffering uses a fixed-size internal buffer.
- Owned text-returning APIs depend on the allocator contract used by the runtime and stdlib.
- Collections, allocation-backed filesystem helpers, text builders, and TCP convenience buffers use the shared `System.Memory` allocator contract.
- Ordinary user code constructs heap-backed containers with target-typed `new()` or `new(allocator)`, not `Type.New()` factory calls.
- Prefer a single allocator contract for stdlib internals rather than ad hoc allocation in each module.
- See [DynamicMemoryAllocation.md](../Internals/DynamicMemoryAllocation.md) for the allocation model.
- The current allocator backend uses Stark-owned runtime helpers with small and
  medium size-class reuse, Linux syscall-backed and Windows OS heap-backed
  fallback paths, and target-aware over-alignment.

### Math and helper boundary

- Hardware-lowerable math such as `Sqrt`, `FusedMultiplyAdd`, rounding, and the
  reciprocal estimates should keep using the selected instruction surface.
- Transcendental math such as `Sin`, `Cos`, `Pow`, and `Log` currently uses
  LLVM math intrinsics that may lower to libm. Under the current dependency
  criteria, that is a toolchain/backend choice inherited through LLVM, not a
  C-backed `System.Math` implementation that Stark needs to replace.
- Compiler-generated memory helpers such as backend-emitted `memset`,
  `memcpy`, and `memmove` are also classified as toolchain/backend dependencies
  when they arise from LLVM lowering rather than explicit Stark runtime calls.

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

- Linux integration tests that verify the stdlib works without explicit
  Stark-emitted libc runtime symbols
- Windows integration tests that verify the stdlib works without explicit
  Stark-emitted CRT runtime symbols
- archive and object-file symbol audits for explicit Stark-emitted C runtime
  dependencies such as `malloc`, `realloc`, and `free`
- informational reports for toolchain-inherited symbols such as libm,
  `memset`, `memcpy`, `memmove`, and hosted startup code
- cross-platform tests that verify the public API surface matches except where platform differences are documented

### Packaging checks

- verify the package image stays in sync with exported module names
- verify the package output location can be overridden
- verify the package remains usable from a clean consumer project with no source dependency

## Packaging Plan

- `scripts/build-stdlib.sh` remains the canonical build entry point
- the script emits both the archive and the package image
- package output is compatible with the compiler's `-I` lookup path
- packaging changes preserve the ability to build the stdlib in isolation from application code

## Remaining Work

The implemented standard-library surface is now broad enough for examples that
use console IO, files, paths, filesystem operations, allocation-backed
collections, process helpers, threading, and blocking TCP. Remaining work should
stay tied to the active roadmap rather than expanding the public surface by
default. Important open edges include:

- richer `File` text-reading APIs with explicit error/result behavior
- captured-lambda support for thread entries and other callback-style APIs
- future synchronization primitives once the memory model and atomic surface are documented
- package-layer HTTP built on `System.Net.Tcp`, not inside the standard library
- formal benchmarks for allocator, collection, text, IO, and networking paths


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
