# `System.Collections`

`System.Collections` defines the first owned heap-backed collection set for
ordinary Stark programs.

The public collection APIs must be safe and usable. They should not require
users to pass `rawptr` or `rawmutptr` values except at explicit FFI or
low-level runtime boundaries.

## Module Layout

The root module is:

- `System.Collections`

Initial public declarations:

- `System.Collections.Ordering`
- `System.Collections.Eq`
- `System.Collections.Hash`
- `System.Collections.Ord`
- `System.Collections.Format`
- `System.Collections.List<T>`
- `System.Collections.Stack<T>`
- `System.Collections.Queue<T>`
- `System.Collections.RingQueue<T>`
- `System.Collections.LinkedList<T>`
- `System.Collections.Dictionary<K, V>`
- `System.Collections.HashSet<T>`
- `System.Collections.Equatable<T>`
- `System.Collections.Hashable<T>`
- `System.Collections.DictionaryKey<T>`
- `System.Collections.Lookup<T>`
- `System.Collections.SortBy<T>`
- `System.Collections.Sort<T>`

The implementation may split these into source files such as
`System/Collections/List.stark`, but the public package should make the common
types available from `System.Collections`.

`Dictionary<K, V>` and `HashSet<T>` are contract-driven. The public model is
canonical `Hash` + `Eq` support, with scalar fast paths for `bool`, integer
keys, and compiler-known text keys. User-defined key types provide the current
concrete hook with explicit static `finite law Hash` and `Equals` methods.
Unsupported or incompatible key contracts are compile-time diagnostics.

## Const Lookup Tables

`Lookup<T>` reads from a const slice and returns a readonly borrow of the
selected element:

```stark
public inline finite law retborrow frozen T Lookup<T>(
    const T[] table,
    u64[0 2 ** 63 - 1] index);
```

Const fixed-array globals can be passed directly as `const T[]` views. When the
table payload and index are compile-time constants, the SSA const lookup-table
pass folds scalar reads to constants from typed initializer/package facts.

## Sorting

`SortBy<T>` sorts a mutable slice in place through an explicit inline comparator:

```stark
public inline fn void SortBy<T>(
    mut borrow T[] values,
    inline closure<finite law Ordering(borrow T, borrow T) where overlap(arg0, arg1)> compare);
```

`Sort<T>` sorts a mutable slice in place through the canonical `Ord` contract:

```stark
public inline fn void Sort<T>(mut borrow T[] values)
    where T: Ord;
```

Both paths use the same heap-sort shape and allocate no runtime closure or
scratch collection. `Sort<T>` lowers to direct `Compare` calls after
monomorphization; a missing or incompatible `Ord.Compare` is a compile-time
error.

## Allocation Pattern

Default allocator:

```stark
stack mut System.Collections.List<i32[0 max]> values = new();
```

Custom allocator:

```stark
stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
stack mut System.Collections.List<i32[0 max]> values = new(allocator);
```

The collection value is an owned header. It owns any heap backing storage and
releases that storage in its destructor.

## `List<T>`

`List<T>` is the primary growable contiguous collection.

```stark
public struct List<T>
{
    finite law i64[0 max] Count(borrow List<T> self);
    finite law i64[0 max] Capacity(borrow List<T> self);
    finite law bool IsEmpty(borrow List<T> self);
    fn System.Memory.MemoryStatus Reserve(mut borrow List<T> self, i64[0 max] additional);
    fn System.Memory.MemoryStatus Push(mut borrow List<T> self, T value);
    fn bool TryPop(mut borrow List<T> self, out T value);
    law retborrow T Get(borrow List<T> self, i64[0 max] index);
    fn retborrow mut T GetMut(mut borrow List<T> self, i64[0 max] index);
    finite law retborrow T[] AsSlice(borrow List<T> self);
    finite retborrow mut T[] AsMutableSlice(mut borrow List<T> self);
    fn void Clear(mut borrow List<T> self);
}
```

Indexing must be bounds-checked unless the source range guarantees the index.
Collection APIs should keep ranges narrow so callers can write loops whose
index bounds are easy to see.

## `Stack<T>`

`Stack<T>` is a last-in, first-out collection backed by owned dynamic storage.

```stark
public struct Stack<T>
{
    finite law i64[0 max] Count(borrow Stack<T> self);
    finite law bool IsEmpty(borrow Stack<T> self);
    fn System.Memory.MemoryStatus Push(mut borrow Stack<T> self, T value);
    fn bool TryPop(mut borrow Stack<T> self, out T value);
    law retborrow T Peek(borrow Stack<T> self);
    fn void Clear(mut borrow Stack<T> self);
}
```

The first implementation can share the same contiguous backing strategy as
`List<T>`.

## `Queue<T>`

`Queue<T>` is a first-in, first-out collection backed by owned sparse slot
storage. Dequeue and peek are O(1): the implementation keeps head/length
metadata and moves only the occupied slot being removed.

```stark
public struct Queue<T>
{
    finite law i64[0 max] Count(borrow Queue<T> self);
    finite law bool IsEmpty(borrow Queue<T> self);
    fn System.Memory.MemoryStatus Enqueue(mut borrow Queue<T> self, T value);
    fn bool TryDequeue(mut borrow Queue<T> self, out T value);
    law retborrow T Peek(borrow Queue<T> self);
    fn void Clear(mut borrow Queue<T> self);
}
```

`RingQueue<T>` exposes the same ring-buffer strategy directly for callers that
need an explicit capacity check while keeping FIFO operations O(1).

```stark
public struct RingQueue<T>
{
    finite law i64[0 max] Count(borrow RingQueue<T> self);
    finite law i64[0 max] Capacity(borrow RingQueue<T> self);
    finite law bool IsEmpty(borrow RingQueue<T> self);
    fn System.Memory.MemoryStatus Reserve(mut borrow RingQueue<T> self, i64[0 max] additional);
    fn System.Memory.MemoryStatus Enqueue(mut borrow RingQueue<T> self, T value);
    fn bool TryDequeue(mut borrow RingQueue<T> self, out T value);
    law retborrow T Peek(borrow RingQueue<T> self);
    fn void Clear(mut borrow RingQueue<T> self);
}
```

## `LinkedList<T>`

`LinkedList<T>` provides stable node storage for workloads where contiguous
movement is undesirable.

```stark
public struct LinkedList<T>
{
    finite law i64[0 max] Count(borrow LinkedList<T> self);
    finite law bool IsEmpty(borrow LinkedList<T> self);
    fn System.Memory.MemoryStatus ReserveNodes(mut borrow LinkedList<T> self, i64[0 max] count);
    fn System.Memory.MemoryStatus AddFirst(mut borrow LinkedList<T> self, T value);
    fn System.Memory.MemoryStatus AddLast(mut borrow LinkedList<T> self, T value);
    fn bool TryRemoveFirst(mut borrow LinkedList<T> self, out T value);
    fn bool TryRemoveLast(mut borrow LinkedList<T> self, out T value);
    fn void Clear(mut borrow LinkedList<T> self);
}
```

The first public surface should avoid exposing node pointers. Node-handle APIs
can come later once the borrow and iterator story is deliberate.

## Collection Contract Requirements

The canonical generic collection contracts live in the source module:

```stark
public trait Eq
{
    finite law bool Equals(borrow Self left, borrow Self right)
        where overlap(left, right);
}

public trait Hash
{
    alias Code = u64[0 max];

    finite law Self.Code Hash(borrow Self value);
}

public trait Ord
{
    finite law Ordering Compare(borrow Self left, borrow Self right)
        where overlap(left, right);
}
```

`Equals`, `Hash`, and `Compare` are `finite law` because collection lookup and
sorting need pure, read-only behavior that returns for valid inputs.

`Equatable<T>`, `Hashable<T>`, and `DictionaryKey<T>` still exist as the
implementation vocabulary used by dictionary and set internals. New generic
surface area should use the canonical `Eq`, `Hash`, and `Ord` names.

## `Dictionary<K, V>`

`Dictionary<K, V>` is an owned hash table.

```stark
public struct Dictionary<K, V>
{
    finite law i64[0 max] Count(borrow Dictionary<K, V> self);
    finite law i64[0 max] Capacity(borrow Dictionary<K, V> self);
    finite law bool IsEmpty(borrow Dictionary<K, V> self);
    fn System.Memory.MemoryStatus Reserve(mut borrow Dictionary<K, V> self, i64[0 max] additional);
    fn System.Memory.MemoryStatus Set(mut borrow Dictionary<K, V> self, K key, V value);
    finite law bool ContainsKey(borrow Dictionary<K, V> self, borrow K key);
    fn bool TryGet(borrow Dictionary<K, V> self, borrow K key, out V value);
    fn bool Remove(mut borrow Dictionary<K, V> self, borrow K key);
    fn void Clear(mut borrow Dictionary<K, V> self);
}
```

`ContainsKey` is valid as a `law` method only for key types whose hash and
equality behavior is supported by the current standard-library surface.
User-defined key support currently uses explicit static `finite law Hash` and
`Equals` methods on the key type. Missing or incompatible hooks are diagnosed at
generic collection use sites.

## `HashSet<T>`

`HashSet<T>` is an owned open-addressed set using the same key-contract and
storage strategy as `Dictionary<K, V>`.

```stark
public struct HashSet<T>
{
    HashSet();
    HashSet(System.Memory.Allocator allocator);
    inline finite law u64[0 2 ** 63 - 1] Count(borrow HashSet<T> self);
    inline finite law u64[0 2 ** 63 - 1] Capacity(borrow HashSet<T> self);
    inline finite law bool IsEmpty(borrow HashSet<T> self);
    inline finite law u64[0 2 ** 63 - 1] FindIndex(borrow HashSet<T> self, borrow T value) where overlap(self, value);
    fn System.Memory.MemoryStatus Reserve(mut borrow HashSet<T> self, u64[0 2 ** 63 - 1] additional);
    inline fn System.Memory.MemoryStatus Add(mut borrow HashSet<T> self, T value);
    inline finite law bool Contains(borrow HashSet<T> self, borrow T value) where overlap(self, value);
    inline fn bool Remove(mut borrow HashSet<T> self, borrow T value) where overlap(self, value);
    fn void Clear(mut borrow HashSet<T> self);
}
```

## Design Rules

- Public APIs must use owned values, safe borrows, safe slices, and explicit
  result/status values.
- State inspection methods that only read collection metadata should be
  `finite law`.
- Read-only borrow accessors that may trap on invalid index or empty collection
  should be `law` unless their preconditions are encoded strongly enough to
  prove guaranteed return.
- Non-allocating in-memory mutations that always return, such as successful or
  unsuccessful `TryPop`-style operations, should use `finite`.
- Methods that mutate collection state, allocate, free, drop elements, or write
  through `out` parameters should remain ordinary `fn` unless they can make the
  stronger `finite` guarantee without depending on external state.
- Public collection APIs must not require `rawptr` or `rawmutptr`.
- Destructors must drop live elements before freeing backing storage.
- Moves transfer ownership of backing storage.
- Mutating methods require mutable access to the collection owner.
- Views and iterators must not outlive the collection storage.
- Small status and error enums should use appropriately small tag widths.

## Current Status

- `System.Collections` is now a source module re-exported from `System`.
- `List<T>` has default and allocator-taking constructors, owned backing
  storage, `Reserve`, `Push`, metadata inspection, `Clear`, and destructor
  cleanup.
- `Stack<T>` uses `List<T>` as its contiguous backing for construction, push,
  pop, metadata inspection, peek, and cleanup.
- `Queue<T>` has default and allocator-taking constructors, owned ring-buffer
  storage, `Reserve`, `Enqueue`, `TryDequeue`, `Peek`, metadata inspection,
  `Clear`, and destructor cleanup.
- `LinkedList<T>` owns nodes internally and does not expose public raw node
  pointers. The current implementation supports construction, `AddFirst`,
  `AddLast`, `TryRemoveFirst`, `TryRemoveLast`, metadata inspection, `Clear`,
  `ReserveNodes`, and destructor cleanup. `ReserveNodes(count)` is an explicit
  performance knob for workloads that know they will allocate many nodes; it
  pre-fills the list allocator's node bucket without adding elements to the list.
- `TryPop`, `TryDequeue`, `TryRemoveFirst`, and `TryRemoveLast` now have
  source-level `out T` bodies.
- `Get`, `GetMut`, and `Peek` now return safe retborrows from addressable
  storage. Queue and ring-queue peeks borrow from sparse slots, and
  `AsSlice`/`AsMutableSlice` lower to slice views over `List<T>` backing
  storage.
- `LinkedList<T>` now uses one allocation per internal node. Each node stores
  next/previous links and the element value together.
- `Eq`, `Hash`, `Ord`, and `Format` are present as the canonical generic
  contract names; `Equatable<T>`, `Hashable<T>`, and `DictionaryKey<T>` remain as
  implementation compatibility vocabulary for dictionary/set internals.
- `Dictionary<K, V>` and `HashSet<T>` are implemented as owned open-addressed
  hash tables for supported scalar, text, and explicit static `Hash`/`Equals`
  key types.
- Dictionary/set key diagnostics reject unsupported key types before the
  collection is used, including through package-image-backed
  `System.Collections` imports.
- `SortBy<T>` and `Sort<T>` provide deterministic in-place slice sorting without
  runtime allocation; `Sort<T>` uses the canonical `Ord` contract and reports
  missing or incompatible `Compare` implementations at compile time.
- Collection growth, move/drop, and package-consumption coverage now exists as
  checked coverage: source imports compile the full collection growth program,
  and package-image imports validate the same surface through `--check`.
- `benchmarks/collections` contains compile-only `List<T>` and `Queue<T>`
  growth benchmark sources. They are intentionally skipped by the executable
  benchmark runner until imported collection executable linking is complete.
- The first `List<T>` and `Queue<T>` implementations intentionally duplicate a
  small growth loop because same-module helper lookup from generic member
  functions is not yet reliable enough to share that policy without exposing an
  implementation helper publicly.
