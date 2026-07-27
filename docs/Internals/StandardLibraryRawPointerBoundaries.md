# Standard Library Raw Pointer Boundaries

Raw pointers (`rawptr<T>` / `rawmutptr<T>`) in the standard library must stay
inside documented FFI, platform, runtime, or audited unsafe boundary files.
`SystemRawPointerAuditStandardLibraryTests` enforces this list; when a new file
legitimately needs raw pointers, add it here with a rationale and to the test's
`DocumentedRawPointerFiles` allowlist in the same change.

## Boundary Files

| File | Why raw pointers are required |
| --- | --- |
| `System/C.stark` | C string interop: borrowed/owned null-terminated views, foreign-owned copy/dispose, and C primitive aliases are raw-pointer shaped by definition. |
| `System/Collections.stark` | Owned container internals address dynamic storage payloads directly. |
| `System/Console.stark` | Console reads/writes hand fixed buffers to platform write/read primitives. |
| `System/Cryptography/Sha256.stark` | SHA-256 bridges an `ascii` view to a bounded byte slice and forms bounded views over its fixed streaming file buffer inside narrow audited `unsafe` blocks; no raw pointer appears in its public API. |
| `System/FileSystem.stark` | Directory iteration and metadata calls pass platform structs and paths. |
| `System/IO/File.stark` | Owned file handles exchange buffers with platform read/write/seek calls. |
| `System/IO/Path.stark` | Path shaping fills caller-provided fixed buffers through platform queries. |
| `System/Json.stark` | The flat node-table parser/writer scans ASCII text through audited `System.Text.AsciiData` views while keeping raw pointers out of public declarations. |
| `System/Memory.stark` | The allocator surface itself: reserve/append/copy/move/fill primitives. |
| `System/Net/Tcp.stark` | Socket calls exchange address structs and payload buffers with the platform. |
| `System/Process.stark` | Process spawn/argv/environment plumbing passes argv vectors and status buffers to platform syscalls. |
| `System/Runtime.stark` | Runtime entry plumbing and platform dispatch glue. |
| `System/Runtime/Buffer.stark` | Fixed and dynamic byte buffers expose addressed regions for IO. |
| `System/Runtime/ConsoleInput.stark` | Line input fills raw byte regions from platform reads. |
| `System/Runtime/Platform.stark` | Cross-platform dispatch facade over the per-OS modules. |
| `System/Runtime/Platform/Linux.stark` | Linux syscall shims: every syscall argument is a raw region or scalar. |
| `System/Runtime/Platform/MacOS.stark` | macOS libSystem shims, same shape as the Linux boundary. |
| `System/Runtime/Platform/Windows.stark` | Win32 API shims, same shape as the Linux boundary. |
| `System/Testing/HostCompiler.stark` | Host-test JSON protocol helpers bind parsed string fields as text views and keep that unsafe parsing boundary inside test infrastructure. |
| `System/Text.stark` | Fixed-buffer concat/format helpers write through bounded raw regions. |
| `System/Threading.stark` | Thread entry trampolines and payload handoff cross an FFI boundary. |
| `System/Toml.stark` | The manifest TOML parser/writer mirrors the JSON flat-table design and uses audited raw text scans internally without exposing raw pointers publicly. |

## Public Raw Pointer Surfaces

Public APIs may expose raw pointers only in explicitly `unsafe` low-level
compatibility surfaces. The audit allowlists:

- `System/Text.stark`: bounded fixed-buffer `TryConcat` / `TryFormat` helpers.
- `System/Memory.stark`: only
  `InitializeUnsignedBytesFromPointerDisjoint(count, source[count], destination)`.
  The separately packaged `Vendor.STB.Image` binding uses this unsafe helper to
  copy exactly the decoded `byteLength` bytes from STB's native allocation into
  initialized Stark-owned dynamic storage before calling `stbi_image_free`.
  The `[count]` source extent preserves that native-region fact for checking and
  backend optimization; no other public `System.Memory` raw-pointer API is
  allowlisted.
- `System/C.stark`: the C string compatibility surface (`CStr`, `OwnedCStr`,
  `ForeignOwnedCStr`, `CStringDisposer`) — exposing C-shaped pointers is this
  module's purpose (see
  [System.C](../StandardLibrary/System.C.md)).

Everything else must keep raw pointers out of `public` declarations.
