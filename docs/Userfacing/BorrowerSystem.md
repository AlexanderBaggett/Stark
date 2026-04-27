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

* `ffi fn`
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

ffi fn rawptr<i8[-128 127]> getenv(rawptr<i8[-128 127]> name);

fn bool RawNullIsExplicit() {
    stack rawptr<i8[-128 127]> missing = null;
    return missing == null;
}
```

The same value cannot be assigned to a safe borrow:

```stark
module Demo

fn void InvalidNullBorrow() {
    // Rejected: safe borrows are never null.
    stack borrow i8[-128 127] value = null;
}
```

## 4. Transitive Immutability

Stark distinguishes ordinary shared access from deep immutability.

Two forms:

* `frozen T`: nothing reachable through this reference may be mutated for the lifetime of the borrow.
* `shared T`: shared access is permitted, but mutation capable primitives may exist in this domain.

Under `frozen`, the language prohibits:

* interior mutability
* hidden atomics
* mutation through reachable aliases
* upgrading reachable readonly aliases back to mutable raw aliases in safe code
* laundering reachable readonly aliases through integer conversions

This distinction lets the compiler rely on true read only behavior rather than the absence of writes through one syntactic path. That allows hoisting, sharing, and reusing reads.

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
* mutable but non overlapping

These tell the compiler more about alignment, bounds, non overlap, and vectorization than a basic pointer plus length would.

## Summary

The Stark borrower system is organized around:

* ownership by default
* deterministic destruction without GC
* internal lifetime tracking
* explicit borrow escape classes
* an explicit raw pointer boundary
* null free safe borrows
* transitive immutability
* explicit initialization contracts
* explicit effects
* restricted destruction
* explicit shared state capability
* closed dispatch by default
* richer slice contracts

The system is designed so that:

* safe code is the strongest optimization domain
* raw and FFI code are isolated
* flexibility weakens guarantees only when explicitly requested
