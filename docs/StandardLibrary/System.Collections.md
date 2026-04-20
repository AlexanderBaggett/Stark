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
- `System.Collections.Equatable<T>`
- `System.Collections.Hashable<T>`
- `System.Collections.DictionaryKey<T>`

The implementation may split these into source files such as
`System/Collections/List.stark`, but the public package should make the common
types available from `System.Collections`.

`Dictionary<K, V>` remains planned. The first hash/equality vocabulary is in
place, but the dictionary type itself should wait until Stark enforces those
constraints at generic use sites and preserves them through package images.

## Allocation Pattern

Default allocator:

```stark
stack mut System.Collections.List<i32[0 max]> values = new();
```

Custom allocator:

```stark
stack mut System.Collections.List<i32[0 max]> values = new(myCustomAllocator);
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
    law bool Equals(borrow T left, borrow T right);
}

public trait Hashable<T> {
    finite law u64[0 max] Hash(borrow T value);
}

public doctrine DictionaryKey<T> {
    law bool Equals(borrow T left, borrow T right);
    finite law u64[0 max] Hash(borrow T value);
}
```

`Equals` is a `law` because dictionary lookup needs equality to be pure and
read-only. `Hash` is `finite law` because hashing should be pure and guaranteed
to return for valid keys.

The compiler still needs full trait/doctrine constraint solving before the
standard library exposes a generic `Dictionary<K, V>` type.

## Planned `Dictionary<K, V>`

`Dictionary<K, V>` is an owned hash table.

```stark
public struct Dictionary<K, V> {
    finite law i64[0 max] Count(self);
    finite law bool IsEmpty(self);
    fn System.Memory.MemoryStatus Add(mut self, K key, V value);
    fn System.Memory.MemoryStatus Set(mut self, K key, V value);
    law bool ContainsKey(self, borrow K key);
    fn bool TryGet(self, borrow K key, out V value);
    fn bool Remove(mut self, borrow K key);
    fn void Clear(mut self);
}
```

The generic dictionary requires a hash/equality constraint design. `ContainsKey`
is only valid as a `law` method if those hash/equality operations are themselves
pure enough for dictionary lookup to remain read-only and side-effect-free. If
the compiler does not have that constraint machinery when `System.Collections`
starts, the implementation should pause on generic `Dictionary<K, V>` rather
than exposing raw-pointer or untyped workarounds.

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
  first dictionary key contract vocabulary. Generic dictionary exposure is
  still blocked on compiler-enforced constraints.
- The first `List<T>` and `Queue<T>` implementations intentionally duplicate a
  small growth loop because same-module helper lookup from generic member
  functions is not yet reliable enough to share that policy without exposing an
  implementation helper publicly.
