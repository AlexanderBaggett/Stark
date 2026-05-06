# Stark Borrower System

For the user facing language contract, see [LanguageReference.md](./LanguageReference.md).
For optimizer rationale beyond the source contract, see [LanguageInternals.md](../Internals/LanguageInternals.md).

## Strategy

Stark's borrower system is stricter than Rust's. The goal is to make ordinary safe code easier for the compiler to optimize.

The rules:

* safe code uses non escaping borrows by default
* safe code has no null references
* safe code separates deep immutability from ordinary shared access
* initialization is explicit
* effects are part of the function model
* destructor behavior is heavily restricted
* shared mutable state is explicit
* dispatch is closed by default
* raw pointers are an explicit low level escape hatch

Every restriction makes aliasing, escape, mutability, lifetime, initialization, or effect behavior more explicit.

The safe subset of Stark is the most optimizable subset. More flexible behavior is available, but it must be requested explicitly.

## 1. Ownership, Moves, and Lifetimes

Safe Stark is ownership based. There is no garbage collector.

Every safe value is in one of four categories:

* owned
* borrowed
* raw
* static

Owned is the default.

Rules:

* every non borrow, non raw value has exactly one owner
* ownership transfers by move
* a moved value is no longer usable from the old binding
* values still owned at scope exit are dropped automatically
* assignment to an initialized owned place drops the previous value first
* parameters owned by the callee are dropped at function exit unless moved out
* safe code has no `forget` style escape hatch

Implicit copying is rare. It is limited to trivially copyable scalars and immutable borrow forms.

This gives Stark the same fundamental memory model that removes the need for a GC:

* ownership determines who is responsible for cleanup
* moves transfer that responsibility
* drop scopes ensure cleanup happens at predictable points
* borrows never own and therefore never free

Stark tracks whether each owned binding is initialized, moved, borrowed, or available again after reinitialization. If the compiler cannot tell that a value is live, initialized, and used through a valid borrow, the program is rejected.

The check is flow sensitive and entirely at compile time. The safe language requires proof instead of relying on a GC.

Arena storage follows the same rule. `arena` storage is region owned and reclaimed when its lexical region ends. Safe code cannot create immortal arena allocations by accident.

Intentional leaks require an explicit raw or FFI escape hatch.

Moving an owned value consumes the old binding:

```stark
module Demo

struct Box {
    i32[min max] Value;
}

fn void Consume(Box value) {
    return;
}

fn i32[min max] InvalidUseAfterMove() {
    stack Box box = new Box() { Value = 1 };
    Consume(box);

    // Rejected: `box` was moved into `Consume`.
    return box.Value;
}
```

Reinitializing makes the binding usable again:

```stark
module Demo

struct Box {
    i32[min max] Value;
}

fn void Consume(Box value) {
    return;
}

fn i32[min max] MoveThenReinitialize() {
    stack mut Box box = new Box() { Value = 1 };
    Consume(box);

    box = new Box() { Value = 2 };
    return box.Value;
}
```

Copyable scalars stay usable after assignment:

```stark
module Demo

finite law i32[min max] ScalarsCopy() {
    stack i32[min max] left = 10;
    stack i32[min max] right = left;
    return left + right;
}
```

## 2. Non Escaping Borrows by Default

Borrow escape class is part of the type.

The three borrow forms:

* `borrow T`: temporary access. May not be stored, returned, or forwarded to unknown code.
* `retborrow T`: may escape only through the return value.
* `storeborrow T`: may be stored or otherwise escape.

`borrow T` is the default in safe code.

Use `borrow` for temporary access:

```stark
module Demo

struct Counter {
    i32[min max] Value;

    finite law i32[min max] Current(borrow Counter self) {
        return self.Value;
    }

    finite void Add(mut borrow Counter self, i32[min max] amount) {
        self.Value += amount;
        return;
    }
}

fn i32[min max] Run() {
    stack mut Counter counter = new Counter() { Value = 2 };
    counter.Add(3);
    return counter.Current();
}
```

Use `retborrow` when a borrow is deliberately returned:

```stark
module Demo

struct Counter {
    i32[min max] Value;

    finite retborrow mut i32[min max] Slot(mut borrow Counter self) {
        return self.Value;
    }
}

fn i32[min max] Run() {
    stack mut Counter counter = new Counter() { Value = 1 };
    counter.Slot() = 9;
    return counter.Value;
}
```

A plain `borrow` return is rejected because the type says the borrow must not escape:

```stark
module Demo

struct Box {
    i32[min max] Value;
}

fn borrow Box InvalidReturn(borrow Box box) {
    // Rejected: use `retborrow Box` for a returned borrow.
    return box;
}
```

## 3. Raw Pointers, FFI, and Null

Safe Stark has no null references and no nullable borrows.

Null exists only in the raw pointer and FFI domain.

Raw pointer types:

* `rawptr<T>`
* `rawmutptr<T>`

These are the only pointer forms that may be:

* null
* dangling
* unaligned
* aliased
* pointers to pointers
* passed to or from foreign code

Raw pointers are allowed only in:

* `unsafe ffi fn`
* explicit raw or unsafe regions
* explicit low level runtime code
* explicit conversions at FFI boundaries

Rules:

* dereferencing a raw pointer is never implicit
* a raw pointer must be null checked before conversion to a safe borrow
* a null check tells you the pointer is not null, nothing more
* a null check does not tell you whether the pointer is aligned, lives long enough, points to initialized memory, has aliases, or even points to valid memory at all
* converting a raw pointer to a safe borrow is always explicit

Pointers to pointers are forbidden in safe Stark. They exist only through raw pointers in FFI or explicit low level code, e.g. `rawptr<rawptr<i8>>` or `rawmutptr<rawmutptr<void>>`.

This boundary preserves the safe code guarantees that matter for optimization:

* safe borrows are never null
* safe borrows are real values, not nullable raw handles
* safe borrows give the compiler more information about lifetimes and aliasing than raw pointers do

`null` is raw only:

```stark
module Demo

unsafe ffi fn rawptr<i8[min max]> getenv(rawptr<i8[min max]> name);

unsafe fn bool RawNullIsExplicit() {
    stack rawptr<i8[min max]> missing = null;
    return missing == null;
}
```

The same value cannot be assigned to a safe borrow:

```stark
module Demo

fn void InvalidNullBorrow() {
    // Rejected: safe borrows are never null.
    stack borrow i8[min max] value = null;
}
```

## 4. Transitive Immutability

Stark distinguishes ordinary shared access from deep immutability.

The forms:

* `frozen T`: nothing reachable through this reference may be mutated for the lifetime of the borrow.
* `shared T`: shared access is permitted, but mutation capable primitives may exist in this domain.
* `const T`: the reachable object graph has permanent const provenance and remains deeply immutable beyond a single borrow lifetime.

Under `frozen`, the language prohibits:

* interior mutability
* hidden atomics
* mutation through reachable aliases
* upgrading reachable readonly aliases back to mutable raw aliases in safe code
* laundering reachable readonly aliases through integer conversions

This distinction lets the compiler rely on true read-only behavior rather than the absence of writes through one syntactic path. That allows hoisting, sharing, and reusing reads.

`const` is stronger than `frozen`. A `frozen` borrow is readonly for the lifetime of that borrow. A `const` parameter or global describes memory that safe Stark code cannot mutate through any reachable path.

Frozen access permits reads but rejects mutation through anything reachable:

```stark
module Demo

struct Box {
    i32[min max] Value;
}

finite law i32[min max] ReadFrozen(frozen Box box) {
    return box.Value;
}

fn void InvalidFrozenWrite(frozen Box box) {
    // Rejected: `box` and everything reachable through it is readonly.
    box.Value = 3;
}
```

## 5. `out` and `init` Parameters

Initialization is part of the type and call model.

The forms:

* `out T`
* `out T[N]`
* `init T`

The contract:

* the callee must write the required bytes before return
* the callee may not read bytes before initializing them
* the caller treats the destination as uninitialized until the call completes

These are used for construction, fill operations, decoding, and other write before read APIs.

An `out` parameter is a write destination:

```stark
module Demo

fn bool TryWrite(out i32[0 max] value) {
    value = 7;
    return true;
}

fn i32[0 max] Run() {
    stack mut i32[0 max] value = 0;
    if (!TryWrite(value)) {
        return 0;
    }

    return value;
}
```

Inside an `out` function, the destination must be written before its new value is observed by the caller. The callee may not use the old contents as input.

## 6. Function Guarantees

Stark exposes a small function model with stronger guarantees than ordinary functions.

Source level forms:

* `fn`
* `finite`
* `law`
* `finite law`

User facing modifiers:

* `inline`
* `noinline`
* `inlinehint`
* `hot`
* `cold`
* `ffi`

The effective guarantee for a function comes from:

* the function kind
* the borrower rules
* the shared state rules
* the destructor restrictions
* the actual function body

The guarantees Stark cares about:

* no visible side effects
* readonly memory behavior
* no synchronization
* no allocation or freeing
* no unwinding
* guaranteed return
* guaranteed progress

These are not separate keywords. The programmer writes the small surface form. Stark grants the stronger guarantee when the body satisfies it.

The mapping:

* `fn`: general form. May still satisfy stronger guarantees if the body is restricted enough.
* `finite`: guaranteed return and guaranteed progress.
* `law`: pure, no visible side effects, readonly style guarantees.
* `finite law`: combines both sets.

`law` functions may compose other pure or read only work. IO belongs in `fn`:

```stark
module Demo

finite law i32[min max] Clamp(i32[min max] value, i32[min max] low, i32[min max] high) {
    if (value < low) {
        return low;
    }

    if (value > high) {
        return high;
    }

    return value;
}

fn void PrintScore(i32[min max] score) {
    // Console and file IO belong in plain `fn`. They observe or modify the
    // outside world.
    return;
}
```

## 7. Restricted Destruction

Destructor behavior is intentionally narrow.

Default model:

* POD by default
* trivial destructors by default
* explicit opt in for more expensive cleanup categories

In safe code, destructors do not:

* panic
* synchronize
* allocate

unless the type is in an explicitly more expensive category.

Complex teardown belongs in explicit teardown functions, not in unrestricted automatic destruction.

## 8. Explicit Shared State

Shared mutable state is not part of the default object model.

Defaults:

* ordinary heap objects are not shared
* publication into shared state is explicit
* shared memory is its own type or capability domain
* atomics and mutex backed mutation are legal only in that explicit domain

This keeps most code in a non shared world. The compiler can track aliasing more cheaply, speculate more freely, and optimize loops more aggressively.

## 9. Closed Dispatch by Default

Dispatch defaults:

* static dispatch
* generic specialization
* sealed laws and traits
* no address taken functions unless explicitly requested
* internal linkage by default
* dynamic dispatch only through explicit runtime facing constructs

This supports:

* turning virtual calls into direct calls
* inlining
* whole program tracking of what gets called and what side effects happen

## 10. Stronger Slice and Array Contracts

Slices and arrays in hot code carry stronger contracts than a basic pointer plus length.

Available qualifiers:

* contiguous
* stride = 1
* aligned(16), aligned(32), aligned(64)
* exact length
* length multiple
* disjoint from another slice
* mutable but non-overlapping

These tell the compiler more about alignment, bounds, non-overlap, and vectorization than a basic pointer plus length would.

## 11. Memory-Separation Composition

`disjoint` is a memory-region contract. It composes with the borrower qualifiers but does not replace them.

Rules:

* `disjoint borrow T` means readonly borrowed access to a region that does not overlap the other regions named by the same disjoint contract.
* `disjoint borrow mut T` means mutable borrowed access to a region that does not overlap the other regions named by the same disjoint contract.
* `out T` and `init T` keep their write-before-read requirements when they are also disjoint from input regions.
* `frozen T` and `const T` remain deeply readonly; adding `disjoint` also proves memory separation.
* `shared T` does not establish disjointness by itself. Shared-state capabilities still need explicit disjoint contracts or other proof before the compiler treats accesses as non-overlapping.

The relational form states exact relationships:

```stark
fn void Copy(borrow u8[] source, borrow mut u8[] destination)
    where disjoint(source, destination) {
    return;
}
```

Inside `Copy`, `source` and `destination` are known not to overlap for the duration of the call. The readonly or mutable authority still comes from `borrow` and `borrow mut`; `disjoint` only supplies the non-overlap fact.

The checked branch form scopes the same fact to the true branch:

```stark
if disjoint(source, destination) {
    Copy(source, destination);
}
```

The false branch receives no disjoint fact and must use overlap-safe code.

`const` and `disjoint` are independent. Two const parameters can alias the same immutable object graph. The compiler treats them as non-overlapping only when `disjoint` is written or proven.

## 12. Bounded Raw Pointer Regions

Bounded raw pointer regions keep raw pointer work explicit while giving the borrower and optimizer a concrete memory range to reason about.

The parameter forms:

* `rawptr<T>[count]`
* `rawmutptr<T>[count]`

mean that the pointer is valid for `count` contiguous elements of `T`. A positive count requires a non-null pointer. A zero-length region may use `null`.

```stark
module Demo

fn void Copy(
    i64[0 max] length,
    disjoint rawptr<i8[min max]>[length] source,
    disjoint rawmutptr<i8[min max]>[length] destination)
    where disjoint(source[0, length], destination[0, length]) {
    return;
}
```

The bound does not make the pointer safe in the same sense as `borrow`. It gives the compiler a source-level region fact: base pointer, element type, element count, mutability, readonly or const provenance, and any stated disjointness.

Composition rules:

* `rawptr<T>[count]` gives readonly raw access to the bounded region.
* `rawmutptr<T>[count]` gives mutable raw access to the bounded region.
* `disjoint rawptr<T>[count]` and `disjoint rawmutptr<T>[count]` state non-overlap with the other regions in the same disjoint group.
* `const rawptr<T>[count]` and `frozen` provenance keep reachable memory readonly; they do not prove non-overlap by themselves.
* `borrow mut` remains the safe mutable-borrow form. A bounded `rawmutptr` can be used at an unsafe or FFI boundary, but it does not become a safe borrow automatically.
* `out` and `init` still express write-before-read initialization. A bounded raw pointer region may be proven disjoint from `out` or `init` destinations, but raw pointer mutability is not a substitute for the `out` or `init` initialization contract.

Raw pointer region expressions name subregions without constructing a slice:

```stark
fn bool RangesDoNotOverlap(
    rawptr<i32[min max]>[count] left,
    rawptr<i32[min max]>[count] right,
    i32[0 max] start,
    i32[0 max] count) {
    if disjoint(left[start, count], right[0, count]) {
        return true;
    }

    return false;
}
```

Inside the true branch, the listed raw pointer regions are known to be pairwise separate. The false branch receives no such fact.

Unsafe raw slice construction converts a bounded raw pointer region into an ordinary slice view:

```stark
fn i32[min max] ReadFirst(rawptr<i32[min max]>[count] pointer, i32[0 max] count) {
    unsafe {
        stack i32[min max][] view = slice(pointer, count);
        return view[0];
    }

    return 0;
}
```

The resulting slice keeps the raw region's length, root, alignment, mutability, const, and disjoint facts. A readonly `rawptr<T>` produces a readonly slice view. A mutable slice view requires `rawmutptr<T>` provenance.

Common bounded raw pointer diagnostics:

```stark
module Demo

fn void NeedsData(rawptr<i32[min max]>[1] input) {
    return;
}

fn void NullPositiveCount() {
    NeedsData(null); // STK3029: positive bounded raw pointer regions cannot be null.
}

fn rawptr<i32[min max]> Identity(rawptr<i32[min max]> pointer) {
    return pointer;
}

fn void HiddenRoot(rawptr<i32[min max]> pointer, i32[0 max] count) {
    unsafe {
        stack i32[min max][] view = slice(Identity(pointer), count); // STK3029: hidden raw pointer root.
    }

    return;
}

fn void StrengthenMutability(rawptr<i32[min max]>[count] pointer, i32[0 max] count) {
    unsafe {
        stack mut mut i32[min max][] view = slice(pointer, count); // STK3002: readonly raw provenance cannot create a mutable slice.
    }

    return;
}

fn void UnboundedIndependent(rawmutptr<i32[min max]> output, i32[0 max] count) {
    for willexit independent (stack mut i32[0 max] index = 0; index < count; index += 1) {
        *(&output[index]) = 0; // STK3027: independent raw pointer loops need a bounded raw pointer region.
    }

    return;
}
```

## Summary

The Stark borrower system is organized around:

* ownership by default
* deterministic destruction without GC
* internal lifetime tracking
* explicit borrow escape classes
* an explicit raw pointer boundary
* null free safe borrows
* transitive immutability
* permanent const provenance
* explicit initialization contracts
* explicit disjoint memory-region contracts
* explicit bounded raw pointer regions at unsafe and FFI boundaries
* explicit effects
* restricted destruction
* explicit shared state capability
* closed dispatch by default
* richer slice contracts

The system is designed so that:

* safe code is the strongest optimization domain
* raw and FFI code are isolated
* flexibility weakens guarantees only when explicitly requested
