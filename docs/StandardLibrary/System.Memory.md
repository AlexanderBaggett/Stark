# `System.Memory`

`System.Memory` defines the public allocation vocabulary used by owned
collections, allocation-backed text helpers, filesystem helpers, and networking
helpers.

The module is intentionally small. Ordinary user code should allocate through
constructors such as `new()` and `new(allocator)`, not through raw pointer APIs.

## Memory-Region Contracts

Stark parameters are non-overlapping by default when they describe caller
storage. `System.Memory` keeps that default for disjoint fast paths such as
`AppendBytesDisjoint`, `CopyBytesDisjointInfallible`, and
`CopyCodePointsDisjointInfallible`. APIs whose algorithm intentionally accepts
overlap spell that explicitly with `where overlap(...)`; examples include
`AppendBytes`, `AppendCodePoints`, `CopyBytes`, `CopyCodePoints`,
`MoveBytes`, and `MoveCodePoints`.

When adding a new memory helper, prefer the disjoint default for the fastest
path. Add `where overlap(source, destination)` only when the implementation is
actually overlap-safe, and use an internal disjoint helper for the hot path when
runtime checks can prove separation.

## Initial Public Surface

```stark
module System.Memory

public enum MemoryError {
    OutOfMemory,
    InvalidLayout,
    UnsupportedAlignment,
    TooLarge,
}

public enum MemoryStatus {
    Ok,
    Err(MemoryError),
}

public enum MemoryResult<T> {
    Ok(T),
    Err(MemoryError),
}

public struct Allocator {
    u8[0 127] Kind;

    static finite law Allocator Default();
    finite law bool IsDefault(borrow Allocator self);
}
```

Small enums such as `MemoryError` and `MemoryStatus` should use the smallest
sound internal tag width. They must not default to a 32-bit tag unless an
explicit ABI boundary requires that representation.

## Construction Pattern

Default allocation:

```stark
stack mut System.Collections.List<i32[0 max]> values = new();
```

Custom allocation:

```stark
stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
stack mut System.Collections.List<i32[0 max]> values = new(allocator);
```

The collection owns its backing storage. The allocator identity travels with
that backing storage so the collection can free or grow it with the same
allocator that created it.

`Allocator.Default()` is modeled as a static function on `Allocator` so the
surface stays consistent with Stark's C#-style type members.

## Internal Runtime Surface

`System.Memory` also defines an internal allocation carrier used by the
standard library:

```stark
internal struct Allocation {
    rawmutptr<i8[min max]> Pointer;
    i64[0 max] ByteLength;
    i64[1 max] Alignment;
    Allocator Allocator;
}

internal inline fn Allocation Allocate(Allocator allocator, i64[0 max] byteLength, i64[1 max] alignment);
internal inline fn Allocation Reallocate(Allocation allocation, i64[0 max] byteLength, i64[1 max] alignment);
internal inline fn void Free(Allocation allocation);
```

The allocator identity is stored with the allocation value. Owned containers
should keep that value with their backing storage so growth and cleanup can use
the allocator that created the storage.

The current default allocator lowering is compiler-backed and routes through
Stark-owned runtime helpers rather than explicit C allocator calls. Non-zero
allocation returns non-null storage or traps through `llvm.trap`; zero-byte
allocation returns a null allocation value that can be safely passed to `Free`.
The internal allocation wrappers are `inline` so optimized callers can see
the compiler-generated allocation facts hidden inside the aggregate
`Allocation` return value, including the fresh-allocation `noalias` fact on
the runtime allocation helper.

The runtime helper stores allocation metadata immediately before the returned
user pointer so it can recover the operating-system allocation, allocation size,
and target-aware alignment when freeing or reallocating. Linux targets use
direct syscall-backed virtual-memory allocation on supported syscall
architectures. Windows targets use OS heap APIs rather than the CRT allocator.
Small and medium allocations are cached in simple size-class free lists so
collection growth and heap locals do not need an operating-system allocation for
every reuse. When a size-class bucket is empty, the runtime allocates one
slab/page-sized region, returns the first block, and pushes the remaining blocks
onto the bucket free list. Very large allocations and allocations requiring
alignment above the current bucket guarantee stay on the OS-backed path. The
public `System.Memory` surface should stay stable as the backend continues to
grow.

The current retention policy favors hot-path reuse. Freed bucket blocks return
to the thread-local bucket list and the slab backing those blocks is retained
until process teardown. Large or over-aligned allocations that bypass buckets
are returned to the operating system immediately through the matching OS-backed
free path. A future allocator policy can add slab release under memory pressure,
but that should be measured separately because it trades steady-state speed for
lower peak retention.

`Reallocate` may reuse a small or medium bucket allocation in place when the new
requested byte length still fits the recorded bucket size and the requested
alignment is no stronger than the bucket alignment. If those facts are not
proven, the runtime keeps the conservative allocate-copy-free fallback.
Because in-place reallocation can return the original pointer, realloc lowering
must not expose fresh-allocation-only LLVM facts such as a blanket `noalias`
result.

## Function Kinds

`Allocator.Default()` and `Allocator.IsDefault` are `finite law` because they
return allocator identity information without allocating, freeing, mutating
state, synchronizing, or touching the operating system.

Constructors and collection growth operations that use an allocator are not
`law` functions. They allocate, may mutate owner state, and may fail or trap
through the allocation policy.

## Design Rules

- `new()` is the easy path for ordinary code.
- `new(allocator)` is the explicit path for code that needs allocator control.
- Safe code does not manually free heap-backed standard-library values.
- Raw allocation remains internal to `System.Memory` or `System.Runtime`.
- Destructors release backing storage after dropping live elements.
- Arena-backed storage must not escape the arena lifetime.

## Current Status

- `System.Memory` now has an initial source module and is re-exported by
  `System`.
- `Allocator.Default()` and `Allocator.IsDefault` are implemented as `finite law`
  member functions.
- Internal allocate, reallocate, and free operations lower through the compiler
  to Stark-owned runtime helpers and are marked `inline` so allocation facts are
  visible after ordinary backend inlining.
- The runtime helpers provide target-aware over-alignment and recover the
  original OS allocation through header metadata.
- Small and medium allocations reuse fixed size-class buckets and can reallocate
  in place while the new layout still fits the current bucket; very large or
  highly over-aligned allocations stay on the OS-backed path.
- Per-thread allocator caches are intentionally deferred until `System.Threading`
  interactions are designed.
- `benchmarks/allocator` contains the first quick allocator benchmark harness
  and covers heap-local bucket reuse plus bucket-reuse and fallback
  `System.Memory.Reallocate` paths.
- Collection growth benchmark sources exercise the allocator through executable
  `List<T>` and `Queue<T>` growth programs.
- `benchmarks/text/OwnedTextAllocation.stark` covers allocation-visible owned
  `ToAscii`/`ToUnicode` conversion and literal-prefix concatenation through
  `System.Memory`.
- `benchmarks/text/OwnedPathAllocation.stark` covers allocation-visible owned
  path joining through `System.Memory`.
- `benchmarks/text/TextPathCallerBuffer.stark` still covers caller-owned path
  buffers and low-level text conversion helpers.
- Linux syscall-backed and Windows OS heap-backed allocator paths are present
  for the current hosted runtime profile.
- Custom allocator dispatch remains future runtime work.
