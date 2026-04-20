# Dynamic Memory Allocation Design

This document defines the planned dynamic-memory model for Stark's `v1.2`
standard-library expansion.

The goal is a usable allocation story that still fits Stark's performance and
ownership model:

- no garbage collector
- no public raw-pointer allocation surface for ordinary users
- deterministic cleanup through ownership and drop
- default allocation that is easy to use
- optional allocator control when code needs it
- enough runtime structure to build real collections, text builders, filesystem
  helpers, and networking APIs

## Baseline Decision

Stark will use a hybrid allocation model:

- `new()` uses the default global allocator when the constructed type needs
  heap-backed storage.
- `new(allocator)` uses an explicit allocator selected by the caller.
- Arena-backed construction uses the same constructor shape once arena allocator
  values are available.
- Raw allocation and deallocation stay inside `System.Runtime` or advanced
  `System.Memory` implementation code, not in ordinary collection, IO, or TCP
  APIs.

This keeps simple code simple:

```stark
import System
module App

fn void Build() {
    stack mut System.Collections.List<i32[0 max]> values = new();
    values.Push(10);
    values.Push(20);
}
```

Code that needs allocator control still gets it without changing the collection
API shape:

```stark
import System
module App

fn void BuildWithAllocator(System.Memory.Allocator myCustomAllocator) {
    stack mut System.Collections.List<i32[0 max]> values = new(myCustomAllocator);
    values.Push(10);
}
```

`System.Collections.List<T>.New()` is not the public pattern. Stark structs
should use C#-style constructors invoked with `new(...)`.

## Storage Model

An owned collection value is usually a small owner object whose backing storage
lives elsewhere.

For example:

```stark
stack mut System.Collections.List<i32[0 max]> values = new();
```

The `values` owner is stack-stored. Its element buffer is heap-allocated by the
constructor. When `values` is dropped, its destructor releases the backing
buffer after dropping any live elements.

This distinction matters:

- the binding storage class controls where the owner object lives
- the collection constructor controls where the backing storage lives
- moving the owner transfers responsibility for the backing storage
- borrowing the collection does not transfer ownership
- safe code never manually frees the backing storage

The direct heap form remains useful for individually heap-owned values:

```stark
heap Widget widget = new(10, 20);
```

Collection APIs should usually prefer stack-owned collection headers with
heap-backed buffers because that gives deterministic owner lifetime without
forcing every local collection object itself onto the heap.

## Constructor Rules

The language work should make these forms first-class:

- target-typed `new()`
- target-typed `new(arg0, arg1, ...)`
- explicit `new TypeName(...)` where no target type is available
- object initializers after either explicit or target-typed construction where
  the type supports them
- constructor overload resolution using the target type and argument types

Examples:

```stark
stack mut System.Collections.List<i32[0 max]> a = new();
stack mut System.Collections.List<i32[0 max]> b = new(myCustomAllocator);
stack mut System.Net.Tcp.TcpClient client = new(endpoint);
```

The constructor body belongs to the struct or record. Public standard-library
types should not expose Rust-style `New` factory functions as their normal
construction path.

## Failure Policy

Stark has no exceptions, so allocation failure needs an explicit policy.

The initial user-facing policy is:

- default constructors such as `new()` may treat out-of-memory as unrecoverable
  and route to Stark's trap/abort failure path
- APIs that can reasonably recover from growth failure return a status/result
  value, for example `TryReserve`, `TryPush`, or `MemoryStatus`
- low-level allocator operations return `MemoryResult<T>` or `MemoryStatus`
  inside `System.Memory` and `System.Runtime`
- a later language pass may add fallible-constructor syntax if Stark wants
  recoverable allocation at construction sites without factory functions

This avoids contaminating every ordinary collection creation with boilerplate,
while still giving systems code a path to explicit recovery when it matters.

## Public Allocator Shape

`System.Memory` owns the public allocator vocabulary.

The first public surface should be small:

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

`MemoryError`, `MemoryStatus`, and similar small enums should use the smallest
sound internal tag width. An enum with only a few cases must not silently become
a 32-bit tag unless ABI compatibility explicitly requires it.

`Allocator.Default()` is intentionally a static type member rather than a
Rust-style `Allocator.New()` factory.

These allocator identity helpers are `finite law` because they do not allocate,
free, mutate, synchronize, or observe external platform state.

The first allocator object does not need to expose raw allocation methods to
ordinary users. Collections, text builders, and buffers can consume
`Allocator` safely through constructors and methods.

Advanced raw allocation can exist in one of two places:

- `internal` APIs inside `System.Memory` for the standard library itself
- `System.Runtime` APIs that are not re-exported as public standard-library
  surface

If a public low-level allocation API is added later, it should be treated as an
unsafe/FFI-adjacent surface and should not be required for normal collection
use.

## Default Global Allocator

The default allocator is the allocator used by:

- `new()` for heap-backed standard-library containers
- owned text builders
- filesystem helpers that return owned names or paths
- TCP helpers that allocate owned buffers
- collection growth when no custom allocator was supplied

Runtime implementation requirements:

- support target-aware alignment
- support sized allocation and deallocation
- support reallocation or an equivalent allocate-copy-free fallback
- preserve allocator provenance so a value is freed by the allocator that
  created it
- expose optimization facts such as non-null, alignment, noalias, and allocsize
  where the runtime contract proves them
- avoid libc, glibc, musl, or Windows CRT allocation dependencies in the
  explicit-C-runtime-free allocator profile

The Linux implementation starts with syscall-backed virtual-memory allocation.
The Windows implementation starts with OS heap APIs rather than the CRT
allocator. A small size-class free-list layer sits above those OS primitives for
small and medium allocations, while very large allocations stay on the OS-backed
path. The public Stark API should not expose those choices.

Current implementation note: the compiler-backed allocator now routes heap
locals and `System.Memory` allocation through Stark-owned runtime helpers. The
helpers over-allocate for target-aware alignment, keep header metadata needed
for sized free, reuse fixed buckets when the requested size and alignment fit
the bucket contract, and avoid explicit `malloc`, `realloc`, and `free` lowering
in Stark-owned runtime code. `Reallocate` reuses an existing bucket block in
place only when the new layout still fits that bucket; otherwise it falls back
to allocate-copy-free. Per-thread caches are deliberately deferred until
`System.Threading` owns enough runtime policy to make them safe and measurable.
The first executable benchmark convention lives in `benchmarks/allocator` and
keeps allocator smoke measurements short enough for normal development runs.

## Custom Allocators

Custom allocator support starts with constructor injection:

```stark
stack mut System.Collections.List<i32[0 max]> values = new(myCustomAllocator);
```

The collection stores enough allocator identity to free and grow its backing
storage correctly. The allocator value must outlive any collection that depends
on it unless the allocator is a copyable handle to a globally valid allocator.

The borrower rules should reject storing short-lived allocator borrows in
long-lived collections. If the first implementation cannot prove allocator
lifetime safely, custom allocators should be restricted to allocator handles
with stable lifetime.

## Arena Allocation

Arena allocation remains a separate storage strategy rather than the default.

Arena-backed construction is valuable for:

- request/response processing
- parsers
- temporary graph construction
- short-lived TCP buffers
- batch filesystem enumeration

Example target shape:

```stark
fn void Parse(System.Memory.Arena arena) {
    stack mut System.Collections.List<Token> tokens = new(arena);
}
```

Arena-backed values still follow ownership rules. The arena owns the backing
region, and safe Stark code must not allow values backed by the arena to escape
the arena's lifetime.

## Collection Requirements

The collection APIs built on this model must:

- hide raw pointers from ordinary users
- expose safe methods and safe slice/view access
- drop live elements before releasing backing storage
- preserve ownership through moves
- reject use-after-move and use-after-drop
- reject mutation while incompatible borrows or views are alive
- keep indexing bounds visible to the compiler for optimization
- use default allocation through `new()` and custom allocation through
  `new(allocator)`

Example:

```stark
fn i32 Sum(System.Collections.List<i32[0 max]> values) {
    stack mut i32[0 max] total = 0;
    for willexit (stack mut i64[0 max] i = 0; i < values.Count(); i += 1) {
        total += values.Get(i);
    }

    return total;
}
```

## Threading And TCP Requirements

Thread and TCP APIs should follow the same pattern:

- constructors create owned handles
- methods live on the owning structs
- destructors perform best-effort cleanup
- explicit `Close`, `Join`, or `Detach` methods exist when user code needs
  deterministic ordering or error handling
- public APIs use safe Stark buffers, owned text, and owned collections rather
  than `rawptr` or `rawmutptr`

Examples:

```stark
stack mut System.Threading.Thread worker = new(WorkerMain, state);
stack System.Threading.ThreadJoinResult joined = worker.Join();
```

```stark
stack mut System.Net.Tcp.TcpClient client = new(endpoint);
stack System.Net.NetResult<i64[0 max]> sent = client.Write(messageBytes);
```

## Non-Goals

The initial dynamic-memory design does not include:

- a garbage collector
- reference-counted ownership as the default
- public raw allocation as the ordinary user path
- thread pools
- async/await
- HTTP in the standard library
- a full stream abstraction

Reference-counted types such as `Rc<T>` or `Arc<T>` can be added later as
explicit library types if shared ownership becomes necessary. They should not
become the default allocation story.

## Implementation Work

Language and compiler work:

- [ ] Add target-typed `new()` and `new(args)` resolution.
- [ ] Define constructor declaration syntax and overload resolution for structs
      and records.
- [ ] Support allocator-taking constructors without requiring factory methods.
- [x] Ensure constructor lowering initializes ownership state before user code
      can observe the value.
- [x] Ensure destructor lowering drops live elements before releasing backing
      storage.
- [ ] Preserve allocator provenance through moves, field projection, and generic
      instantiation.
- [x] Diagnose raw-pointer allocation or deallocation attempts outside approved
      low-level modules.

Runtime and standard-library work:

- [x] Add `System.Memory`.
- [x] Implement the initial default global allocator.
- [x] Add internal allocate, reallocate, and free operations.
- [x] Track allocator provenance in the internal allocation value.
- [x] Replace the C-backed bootstrap allocator with target-specific runtime
      helpers.
- [ ] Add allocator-aware owned buffers.
- [ ] Convert collection APIs to safe owned allocation.
- [ ] Convert allocation-backed text and path helpers away from caller-owned raw
      buffers where appropriate.
- [x] Add Linux allocator backing without libc.
- [x] Add Windows allocator backing without CRT dependency.
- [ ] Add tests for construction, move, drop, growth, allocation failure, and
      package consumption.
