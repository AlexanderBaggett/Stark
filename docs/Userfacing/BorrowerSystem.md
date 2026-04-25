# Stark Borrower System

For the user-facing language contract, see [LanguageReference.md](./LanguageReference.md).
For optimizer rationale beyond the source contract, see [LanguageInternals.md](../Internals/LanguageInternals.md).

## General Strategy

Stark uses a stricter borrower system than Rust in order to make ordinary safe code easier for the compiler to optimize.

The system is built around the following rules:

- safe code uses non-escaping borrows by default
- safe code has no null references
- safe code separates deep immutability from ordinary shared access
- initialization is expressed explicitly
- effects are part of the function model
- destructor behavior is heavily constrained
- shared mutable state is explicit
- dispatch is closed-world by default
- raw pointers exist only as an explicit low-level escape hatch

Every restriction in this system exists to make aliasing, escape, mutability, lifetime, initialization, and effect behavior more explicit.
The practical goal is predictable ownership and fast ordinary code without a garbage collector.

The safe subset of Stark is the maximally optimizable subset. More flexible behavior is available, but it must be requested explicitly.

## 1. Ownership By Default, Moves, and Lifetime Tracking

Stark safe code is ownership-based. It does not use garbage collection.

Every safe value is in exactly one of the following categories:

- owned
- borrowed
- raw
- static

The default category is owned.

The ownership rules are:

- every non-borrow, non-raw safe value has exactly one owner
- ownership transfer happens by move
- moved values are no longer usable from the old binding
- values that remain owned at scope exit are dropped automatically
- assignment to an initialized owned place drops the previous value before storing the new one
- parameters owned by the callee are dropped at the end of the function unless they were moved out earlier
- safe code has no `forget`-style escape hatch

Copying is not the default. Implicit copying is limited to trivially copyable scalar categories and immutable borrow forms.

This gives Stark the same fundamental memory-management model that eliminates the need for GC:

- ownership determines who is responsible for cleanup
- moves transfer that responsibility
- drop scopes ensure cleanup happens deterministically
- borrows never own and therefore never free

As a programmer, this means Stark tracks whether each owned binding is initialized, moved, borrowed, or available again after reinitialization. If the language cannot prove that a value is still live, initialized, and used through a valid borrow, the program is rejected.

This model is flow-sensitive and statically checked. The safe language requires proof instead of runtime GC.

Arena storage follows the same no-GC rule. In the current model, `arena` storage is region-owned and reclaimed when its lexical region ends. Safe code cannot create immortal arena allocations by accident.

Intentional leaks are permitted only through explicit raw or FFI escape hatches.

Example: moving an owned value consumes the old binding.

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

Reinitialization makes the binding usable again:

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

Copyable scalar values remain usable after assignment:

```stark
module Demo

finite law i32[min max] ScalarsCopy() {
    stack i32[min max] left = 10;
    stack i32[min max] right = left;
    return left + right;
}
```

## 2. Non-Escaping Borrows By Default

Stark makes escape class a first-class part of the borrower system.

The core borrow classes are:

- `borrow T`
- `retborrow T`
- `storeborrow T`

These classes have the following meaning:

- `borrow T`
  A non-owning borrow that may not be stored, returned, or forwarded to unknown code.
- `retborrow T`
  A non-owning borrow that may escape only through the return value.
- `storeborrow T`
  A non-owning borrow that may be stored or otherwise escape.

`borrow T` is the default borrow form in safe code.

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

Use `retborrow` when a borrow is deliberately returned to the caller:

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

A plain `borrow` return is rejected because the type says the borrow must not
escape:

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

## 3. Raw Pointers, FFI, and Null Handling

Safe Stark code does not have null references or nullable borrows.

Null is permitted only in the raw-pointer and FFI domain.

The raw pointer forms are:

- `rawptr<T>`
- `rawmutptr<T>`

These are the only pointer forms that may be:

- null
- dangling
- unaligned
- aliased
- pointers to pointers
- passed directly to or from foreign code

Raw pointers are allowed only in:

- `ffi fn`
- explicit raw or unsafe low-level regions
- explicit low-level runtime code
- explicit conversions at foreign boundaries

The following rules apply:

- dereferencing a raw pointer is never implicit
- raw pointers must be null-checked before conversion into safe Stark borrows
- null-checking proves only non-nullness
- null-checking does not prove alignment, lifetime, initialization, alias safety, or provenance validity
- conversion from a raw pointer into a safe borrow is always explicit

Pointers to pointers are forbidden in ordinary safe Stark code.

Pointers to pointers are permitted only through raw pointers in FFI or explicit low-level escape hatches.

Examples of permitted raw-only forms include:

- `rawptr<rawptr<i8>>`
- `rawmutptr<rawmutptr<void>>`

This boundary preserves the safe-code guarantees that matter most for optimization:

- safe borrows remain non-null
- safe borrows remain well-defined values rather than nullable raw handles
- safe borrows preserve stronger lifetime and alias reasoning than raw pointers

Example: `null` is raw-only.

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

The core distinction is:

- `frozen T`
- `shared T`

These forms have the following meaning:

- `frozen T`
  Nothing reachable through this reference may be mutated for the duration of the borrow.
- `shared T`
  Shared access is permitted, but explicit mutation-capable primitives may exist in this domain.

Under `frozen`, the language prohibits:

- interior mutability
- hidden atomics
- mutation through reachable aliases
- upgrading reachable readonly aliases back into mutable-capable raw aliases in safe code
- laundering reachable readonly aliases through integer conversions to regain mutation

This distinction exists so the compiler can rely on true read-only behavior rather than mere absence of writes through one syntactic path.

That, in turn, allows more aggressive reasoning about immutable memory and more freedom to reuse or hoist reads safely.

Example: frozen access permits reads but rejects mutation through the reachable
object graph.

```stark
module Demo

struct Box {
    i32[min max] Value;
}

finite law i32[min max] ReadFrozen(frozen Box box) {
    return box.Value;
}

fn void InvalidFrozenWrite(frozen Box box) {
    // Rejected: `box` and everything reachable through it are readonly.
    box.Value = 3;
}
```

## 5. First-Class `out` and `init` Parameters

Initialization is an explicit part of the Stark type and call model.

The core forms are:

- `out T`
- `out T[N]`
- `init T`

The contract is:

- the callee must write the required bytes before return
- the callee may not read bytes before initializing them
- the caller treats the destination as uninitialized until the call completes

These forms are used for construction, filling, decoding, and other write-before-read APIs.

They make initialization obligations explicit and keep fill-only routines honest.

Example: an `out` parameter is a write destination.

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

Inside an `out` function, the destination must be written before its new value is
observed by the caller. The callee should not use the old contents as input.

## 6. Function Guarantees

Stark exposes a small function model with stronger guarantees than ordinary
"everything can happen" functions.

The source-level function forms are:

- `fn`
- `finite`
- `law`
- `finite law`

Additional user-facing modifiers include:

- `inline`
- `noinline`
- `inlinehint`
- `hot`
- `cold`
- `ffi`

These are the source-language constructs the programmer writes.

The effective guarantee for a function comes from:

- the function kind
- the borrower rules
- the shared-state rules
- the destructor restrictions
- the actual function body

These guarantees are not separate keywords. Programmers write the small source
model, and Stark accepts the stronger guarantee only when the body satisfies it.

The most important derived guarantees are:

- no visible side effects
- read-only memory behavior
- no synchronization
- no allocation or freeing
- no unwind
- guaranteed return
- guaranteed progress

These guarantees define what callers may rely on.

The intended mapping is:

- `fn`
  - general function form
  - may still satisfy stronger guarantees if its body is restricted enough
- `finite`
  - implies guaranteed return and guaranteed progress
- `law`
  - implies purity, no visible side effects, and readonly-style guarantees
  - often allows stronger read-only and side-effect reasoning
- `finite law`
  - combines both sets of guarantees

This keeps Stark's surface syntax small while still making important behavior
visible in the function type.

Example: `law` functions may compose other pure/read-only work, while ordinary
IO stays in `fn`.

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
    // Console and file operations belong in ordinary `fn` functions because
    // they observe or modify the outside world.
    return;
}
```

## 7. Restricted Destruction

Destructor behavior in Stark is intentionally narrow.

The default destruction model is:

- POD-by-default
- trivial destructors by default
- explicit opt-in for more expensive cleanup categories

In safe code, destructors do not:

- panic
- synchronize
- allocate

unless the type is placed in an explicitly more expensive category.

This restriction exists to preserve simple, predictable destruction semantics and to keep automatic teardown optimizer-friendly.

Complex teardown logic belongs in explicit teardown functions rather than in unrestricted automatic destruction.

## 8. Explicit Shared-State Capability

Shared mutable state is not part of Stark's default object model.

The default model is:

- ordinary heap objects are non-shared
- publication into shared state is explicit
- shared memory belongs to a distinct type or capability domain
- atomics and mutex-backed mutation are legal only in that explicit shared domain

This keeps most code in a non-shared semantic world.

That, in turn, allows a simpler non-shared optimization model for most code, with stronger alias and memory reasoning and more room for speculation and loop optimization.

## 9. Closed-World Dispatch By Default

The borrower system is paired with a closed-world default compilation model.

The default dispatch rules are:

- static dispatch
- monomorphization for generics
- sealed laws or traits by default
- no address-taken functions unless explicitly requested
- internal linkage by default
- dynamic dispatch only through explicit runtime-facing constructs

This supports stronger whole-program reasoning and broader internalization.

It also improves:

- devirtualization
- inlining
- whole-program reasoning about callees and effects

## 10. Stronger Slice and Array Contracts

Stark slices and arrays carry stronger contracts in hot code than a minimal pointer-plus-length model.

The relevant qualifiers include:

- contiguous
- stride = 1
- aligned(16)
- aligned(32)
- aligned(64)
- exact length
- length multiple
- disjoint from another slice
- mutable but non-overlapping

These qualifiers are used for performance-critical APIs and loops.

They exist to support stronger reasoning about alignment, bounds, non-overlap, and vectorization opportunities.

This is one of Stark's clearest opportunities to expose more optimizer-relevant information than Rust does by default.

## Summary

The Stark borrower system is not "Rust borrowing with different syntax." It is a stricter semantic system organized around:

- ownership by default
- deterministic destruction without GC
- internal lifetime tracking
- explicit escape classes
- an explicit raw-pointer boundary
- null-free safe borrows
- transitive immutability
- explicit initialization contracts
- explicit effects
- restricted destruction
- explicit shared-state capability
- closed-world dispatch
- richer slice contracts

The system is designed so that:

- safe code remains the strongest optimization domain
- raw and FFI code are isolated
- flexibility weakens guarantees only when explicitly requested

This is the intended path for Stark to expose more optimization-relevant information than Rust in ordinary code.
