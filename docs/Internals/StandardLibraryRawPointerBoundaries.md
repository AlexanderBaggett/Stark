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

- `System.Collections` and `System.Experimental.Collections`
  - `Dictionary<K, V>` still uses raw sparse key, value, and state storage.
    This is a temporary internal boundary for sparse uninitialized slots until
    `dynamic` can model occupied-slot borrows without forcing every slot to be
    initialized.
- `System.Console` and `System.Experimental.Console`
  - Raw stdin/stdout/stderr handles and byte handoff regions are internal
    platform ABI boundaries. Public console APIs use text, slices, dynamic
    buffers, or fixed runtime buffers.
- `System.FileSystem` and `System.Experimental.FileSystem`
  - Directory handles and directory-entry buffers are internal OS boundaries.
    Public APIs expose owned directory values and status/result data.
- `System.IO.File`
  - The stable file module still has public unsafe raw handle and raw byte APIs
    for compatibility with low-level callers: `OpenRead`, `OpenWrite`,
    `OpenAppend`, `Close`, `Flush`, `SyncAll`, `ReadBytes`, `WriteBytes`,
    `Seek`, `WriteText`, and `WriteLine`.
  - Owned `File` methods and newer paths should prefer byte slices,
    `DynamicByteBuffer`, fixed runtime buffers, and owned handles.
- `System.IO.Path` and `System.Experimental.IO.Path`
  - Raw pointers are internal read-only scans over compiler-known text data
    returned by `System.Text.AsciiData`.
- `System.Memory`
  - `Allocation.Pointer` is allocator-internal raw storage.
  - `InitializeBytesFromPointerDisjoint` and
    `InitializeCodePointsFromPointerDisjoint` remain public unsafe bridges from
    caller-owned raw regions into initialized Stark dynamic storage.
- `System.Net.Tcp` and `System.Experimental.Net.Tcp`
  - Socket handles and platform byte-buffer handoffs are internal OS ABI
    boundaries. Public TCP APIs use owned client/listener values, slices,
    vectored slices, dynamic buffers, or fixed runtime buffers.
- `System.Runtime`
  - Raw pointers are limited to compiler-known slice-part ABI structs and
    helper declarations used by lowering.
- `System.Runtime.Buffer` and `System.Experimental.Runtime.Buffer`
  - Fixed and dynamic byte buffers use raw pointers only as narrow views over
    their own storage for memory/platform calls. Stable fixed-buffer raw
    pointer accessors are still public unsafe compatibility APIs and should be
    internalized or replaced by slices.
- `System.Runtime.ConsoleInput`
  - Console input handles are internal platform ABI state.
- `System.Runtime.Platform`, `System.Runtime.Platform.Linux`,
  `System.Runtime.Platform.MacOS`, and `System.Runtime.Platform.Windows`
  - Raw pointers are required for syscall, kernel, Winsock, console, file,
    directory, memory, threading, and socket ABI calls. These functions are
    internal and explicitly unsafe.
- `System.Text` and `System.Experimental.Text`
  - `AsciiData` and `UnicodeData` are compiler-known text view data extractors.
  - Caller-buffer formatting and conversion APIs expose `rawmutptr<Ascii>`,
    `rawmutptr<Unicode>`, UTF-16 regions, and text data pointers so low-level
    formatting can write into caller-owned storage without hidden allocation.
- `System.Threading`
  - Thread handles are internal platform ABI state hidden behind the owned
    `Thread` type.

## Public Raw Pointer Surface

Public raw pointer APIs are allowed only in explicitly unsafe low-level
surfaces:

- `System.Text` and `System.Experimental.Text` for compiler-known text data,
  caller-buffer formatting, and conversion helpers.
- `System.IO.File` for stable raw file handle compatibility.
- `System.Memory` for raw-region initialization bridges.
- `System.Runtime.Buffer` for stable fixed-buffer raw pointer accessors that
  remain as unsafe compatibility APIs until they are internalized or replaced
  by slices.

All other standard-library public APIs should avoid `rawptr` and `rawmutptr`.
Use owned values, slices, `dynamic`, or module-private unsafe wrappers instead.
