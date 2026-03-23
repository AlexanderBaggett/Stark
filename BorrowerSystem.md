# Stark Borrower System

## General Strategy

Stark uses a stricter borrower system than Rust in order to expose more optimizer-relevant information to LLVM in ordinary code.

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

Every restriction in this system exists to unlock stronger IR. The intended result is broader use of:

- `captures(...)`
- `noalias`
- `readonly`
- `dereferenceable`
- `align`
- `nonnull`
- `noundef`
- `nounwind`
- `nosync`
- `nofree`
- `memory(...)`
- `willreturn`
- `mustprogress`
- `initializes(...)`
- `dead_on_return`

The safe subset of Stark is the maximally optimizable subset. More flexible behavior is available, but it must be requested explicitly.

## 1. Non-Escaping Borrows By Default

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

This model gives the compiler stronger and more explicit capture information than a generic borrow model. In particular, it supports broad emission of:

- `captures(none)` for ordinary `borrow`
- `captures(ret: address, provenance)` for `retborrow`
- `returned` when a function returns the same pointer argument
- stronger `nofree` reasoning across calls involving uncaptured borrows

## 2. Raw Pointers, FFI, and Null Handling

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
- explicit runtime or backend-facing code
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
- safe borrows remain eligible for `nonnull`
- safe borrows remain eligible for `noundef`
- safe borrows remain eligible for `dereferenceable`
- safe borrows remain eligible for stronger alias reasoning

## 3. Transitive Immutability

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

This distinction exists so the compiler can rely on true read-only behavior rather than mere absence of writes through one syntactic path.

It enables broader use of:

- `readonly`
- `memory(argmem: read)`
- invariance-style reasoning for immutable memory
- aggressive load hoisting and redundant load elimination

## 4. First-Class `out` and `init` Parameters

Initialization is an explicit part of the Stark type and call model.

The core forms are:

- `out T`
- `out [T; N]`
- `init T`

The contract is:

- the callee must write the required bytes before return
- the callee may not read bytes before initializing them
- the caller treats the destination as uninitialized until the call completes

These forms are used for construction, filling, decoding, and other write-before-read APIs.

They exist to support direct lowering to:

- `initializes(...)`
- `writable`
- `dead_on_return`

They also improve dead-store elimination and reasoning about constructors and fill-only routines.

## 5. Compiler-Derived Function Guarantees

Stark exposes a small user-facing function model and derives stronger compiler guarantees from it.

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

The compiler then derives semantic guarantees from:

- the function kind
- the borrower rules
- the shared-state rules
- the destructor restrictions
- the actual function body

These derived guarantees are not separate user-facing keywords. They are internal semantic facts that the compiler lowers to LLVM when valid.

The most important derived guarantees are:

- no visible side effects
- read-only memory behavior
- no synchronization
- no allocation or freeing
- no unwind
- guaranteed return
- guaranteed progress

These support direct lowering to:

- `memory(none)`
- `memory(argmem: read)`
- `nosync`
- `nofree`
- `nounwind`
- `willreturn`
- `mustprogress`

The intended mapping is:

- `fn`
  - general function form
  - the compiler infers as many guarantees as possible from the body and surrounding rules
- `finite`
  - implies guaranteed return and guaranteed progress
  - lowers to `willreturn` and `mustprogress`
- `law`
  - implies purity, no visible side effects, and readonly-style guarantees
  - lowers toward `memory(none)` or `memory(argmem: read)` depending on what the function reads
  - also allows broad inference of `nosync`, `nofree`, and `nounwind`
- `finite law`
  - combines both sets of guarantees

This keeps Stark's surface syntax small while still allowing the compiler to emit strong function attributes deliberately.

## 6. Restricted Destruction

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

This restriction exists to preserve:

- `nounwind`
- `nosync`
- `nofree`
- tail-call-friendly control flow
- simple and optimizer-friendly CFG structure

Complex teardown logic belongs in explicit teardown functions rather than in unrestricted automatic destruction.

## 7. Explicit Shared-State Capability

Shared mutable state is not part of Stark's default object model.

The default model is:

- ordinary heap objects are non-shared
- publication into shared state is explicit
- shared memory belongs to a distinct type or capability domain
- atomics and mutex-backed mutation are legal only in that explicit shared domain

This keeps most code in a non-shared semantic world.

That, in turn, allows broader use of:

- plain `load` and `store`
- `nosync`
- stronger alias reasoning
- stronger dereferenceability reasoning
- more aggressive speculation and loop optimization

## 8. Closed-World Dispatch By Default

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
- ThinLTO importing
- internal fast calling convention use
- finite-callee reasoning

## 9. Stronger Slice and Array Contracts

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

They exist to support broader use of:

- `align`
- `dereferenceable(N)`
- `noalias`
- `range` attributes and metadata
- stronger GEP flags
- stronger vectorization-friendly loop facts

This is one of Stark's clearest opportunities to expose more optimizer-relevant information than Rust does by default.

## Summary

The Stark borrower system is not "Rust borrowing with different syntax." It is a stricter semantic system organized around:

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

This is the intended path for Stark to expose more optimizer-relevant information than Rust in ordinary code.
