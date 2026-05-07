# Standard Library Raw Pointer Boundaries

The standard library should prefer Stark-owned safe contracts: `borrow`,
`mut borrow`, slices, `dynamic T`, fixed buffers, owned text, result/status
values, and owned handles. Raw pointers remain only where the module is
crossing a backend, platform, compiler-known ABI, or intentionally low-level
caller-owned memory boundary.

The audit test
`SystemRawPointerAuditStandardLibraryTests.StdLibRawPointerUseStaysInDocumentedBoundaryFiles`
keeps raw pointer spelling confined to the files listed here. New raw pointer
use should either fit one of these boundaries or come with a narrow design
justification and a test update.

## Documented Boundaries

- `System.Collections`
  - `Dictionary<K, V>` still uses raw sparse key, value, and state storage.
    This is a temporary internal boundary for sparse uninitialized slots until
    `dynamic` can model occupied-slot borrows without forcing every slot to be
    initialized. A dynamic enum-slot rewrite was rejected for the release audit
    because current enum payload matching moves generic values instead of
    projecting stable mutable borrows from occupied slots.
- `System.Console`
  - Raw stdin/stdout/stderr handles and byte handoff regions are internal
    platform ABI boundaries. Public console APIs use text, slices, dynamic
    buffers, or fixed runtime buffers.
- `System.FileSystem`
  - Directory handles are internal OS boundaries. Directory-entry storage is an
    owned fixed runtime buffer; raw pointers appear only as temporary internal
    platform handoff views over that buffer.
- `System.IO.File`
  - Raw handles and byte-region helpers are internal OS ABI handoff code.
    Public file APIs use owned `File` values, `IOStatus`/`IOResult<T>`, byte
    slices, `DynamicByteBuffer`, fixed runtime buffers, and text views.
- `System.IO.Path`
  - Raw pointers are internal read-only scans over compiler-known text data
    returned by `System.Text.AsciiData`.
- `System.Memory`
  - `Allocation.Pointer` is allocator-internal raw storage.
  - `InitializeBytesFromPointerDisjoint` and
    `InitializeCodePointsFromPointerDisjoint` are internal unsafe bridges used
    by text and path internals to copy compiler-known text pointers into
    initialized Stark dynamic storage.
- `System.Net.Tcp`
  - Socket handles and platform byte-buffer handoffs are internal OS ABI
    boundaries. Public TCP APIs use owned client/listener values, slices,
    vectored slices, dynamic buffers, or fixed runtime buffers.
- `System.Runtime`
  - Raw pointers are limited to compiler-known slice-part ABI structs and
    helper declarations used by lowering.
- `System.Runtime.Buffer`
  - Fixed and dynamic byte buffers use raw pointers only as narrow views over
    their own storage for memory/platform calls. Public fixed-buffer access uses
    read, mutable-read, and write slices instead of raw pointer accessors.
- `System.Runtime.ConsoleInput`
  - Console input handles are internal platform ABI state.
- `System.Runtime.Platform`, `System.Runtime.Platform.Linux`, and
  `System.Runtime.Platform.Windows`
  - Raw pointers are required for syscall, kernel, Winsock, console, file,
    directory, memory, threading, and socket ABI calls. These functions are
    internal and explicitly unsafe.
- `System.Text`
  - `AsciiData` and `UnicodeData` are internal compiler-known text view data
    extractors used by standard-library text, path, IO, and platform code.
  - Public raw pointer use is limited to explicitly unsafe fixed-buffer
    `TryConcat*` and `TryFormat*` helpers. These are compiler-known hooks for
    stack `Ascii`/`Unicode` concatenation and interpolation where the caller
    deliberately chooses no hidden allocation. General text conversion uses
    `OwnedAscii`, `OwnedUnicode`, `OwnedUtf16`, and `MemoryStatus`.
- `System.Threading`
  - Thread handles are internal platform ABI state hidden behind the owned
    `Thread` type.

## Public Raw Pointer Surface

Public raw pointer APIs are allowed only in explicitly unsafe low-level
surfaces:

- `System.Text` for compiler-known fixed-buffer concat and format helpers.

`System.Text` is intentionally not re-exported by the root `System` module, so
callers must opt into this low-level surface with `import System.Text`.

All other standard-library public APIs should avoid `rawptr` and `rawmutptr`.
Use owned values, slices, `dynamic`, or module-private unsafe wrappers instead.
