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

- `System.Collections.List<T>`
- `System.Collections.Stack<T>`
- `System.Collections.Queue<T>`
- `System.Collections.LinkedList<T>`
- `System.Collections.Dictionary<K, V>`
- `System.Collections.Equatable<T>`
- `System.Collections.Hashable<T>`
- `System.Collections.DictionaryKey<T>`

The implementation may split these into source files such as
`System/Collections/List.stark`, but the public package should make the common
types available from `System.Collections`.

`Dictionary<K, V>` is exposed only for key types with a compiler-proven
`DictionaryKey<K>` contract. The first implementation proves that contract for
`bool` and Stark integer key types. Struct, record, text, pointer, and other key
types remain rejected until Stark has full user-defined hash/equality
constraint solving.

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
public struct List<T> {
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

Indexing must be bounds-checked unless the compiler can prove the range. The
range facts should be preserved so LLVM can see non-wrapping index and GEP
facts after lowering.

## `Stack<T>`

`Stack<T>` is a last-in, first-out collection backed by owned dynamic storage.

```stark
public struct Stack<T> {
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

`Queue<T>` is a first-in, first-out collection backed by an owned ring buffer.

```stark
public struct Queue<T> {
    finite law i64[0 max] Count(borrow Queue<T> self);
    finite law bool IsEmpty(borrow Queue<T> self);
    fn System.Memory.MemoryStatus Enqueue(mut borrow Queue<T> self, T value);
    fn bool TryDequeue(mut borrow Queue<T> self, out T value);
    law retborrow T Peek(borrow Queue<T> self);
    fn void Clear(mut borrow Queue<T> self);
}
```

## `LinkedList<T>`

`LinkedList<T>` provides stable node storage for workloads where contiguous
movement is undesirable.

```stark
public struct LinkedList<T> {
    finite law i64[0 max] Count(borrow LinkedList<T> self);
    finite law bool IsEmpty(borrow LinkedList<T> self);
    fn System.Memory.MemoryStatus AddFirst(mut borrow LinkedList<T> self, T value);
    fn System.Memory.MemoryStatus AddLast(mut borrow LinkedList<T> self, T value);
    fn bool TryRemoveFirst(mut borrow LinkedList<T> self, out T value);
    fn bool TryRemoveLast(mut borrow LinkedList<T> self, out T value);
    fn void Clear(mut borrow LinkedList<T> self);
}
```

The first public surface should avoid exposing node pointers. Node-handle APIs
can come later once the borrow and iterator story is deliberate.

## Dictionary Key Contracts

Dictionary pre-work lives in the source module as compile-time contracts:

```stark
public trait Equatable<T> {
    finite law bool Equals(borrow T left, borrow T right);
}

public trait Hashable<T> {
    finite law u64[0 max] Hash(borrow T value);
}

public doctrine DictionaryKey<T> {
    finite law bool Equals(borrow T left, borrow T right);
    finite law u64[0 max] Hash(borrow T value);
}
```

`Equals` and `Hash` are `finite law` because dictionary lookup needs both
operations to be pure, read-only, and guaranteed to return for valid keys.

The compiler enforces a conservative first phase for dictionary keys: `bool`
and Stark integer types are accepted, while key types without a proven
hash/equality contract are rejected at generic use sites. Package-image-backed
imports preserve that same check because the compiler recognizes
`System.Collections.Dictionary<K, V>` after manifest loading.

## `Dictionary<K, V>`

`Dictionary<K, V>` is an owned hash table.

```stark
public struct Dictionary<K, V> {
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

`ContainsKey` is valid as a `law` method only because the accepted
`DictionaryKey<K>` operations are compiler-owned pure operations in this first
phase. User-defined key contracts need a later design pass before structs,
records, text, or other richer values can become dictionary keys.

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
  and destructor cleanup.
- `TryPop`, `TryDequeue`, `TryRemoveFirst`, and `TryRemoveLast` now have
  source-level `out T` bodies.
- `Get`, `GetMut`, and `Peek` now return safe retborrows from addressable
  storage, and `AsSlice`/`AsMutableSlice` lower to slice views over `List<T>`
  backing storage.
- `LinkedList<T>` now uses one allocation per internal node. Each node stores
  next/previous links and the element value together.
- `Equatable<T>`, `Hashable<T>`, and `DictionaryKey<T>` are present as the
  first dictionary key contract vocabulary.
- `Dictionary<K, V>` is implemented as an owned open-addressed hash table for
  compiler-proven `bool` and Stark integer key types.
- Dictionary key diagnostics reject unsupported key types before the dictionary
  is used, including through package-image-backed `System.Collections` imports.
- Collection growth, move/drop, and package-consumption coverage now exists as
  compiler regressions: source imports lower the full collection growth program
  through LLVM IR, and package-image imports validate the same surface through
  `--check`.
- `benchmarks/collections` contains compile-only `List<T>` and `Queue<T>`
  growth benchmark sources. They are intentionally skipped by the executable
  benchmark runner until imported collection executable linking is complete.
- The first `List<T>` and `Queue<T>` implementations intentionally duplicate a
  small growth loop because same-module helper lookup from generic member
  functions is not yet reliable enough to share that policy without exposing an
  implementation helper publicly.
