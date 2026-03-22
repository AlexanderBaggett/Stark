# Stark Borrower System

## General Strategy

Stark should not try to beat Rust by copying Rust's borrowing model and making it slightly more verbose. If Stark wants to extract more performance from LLVM than Rust typically can, it needs restrictions that produce stronger compiler facts more often.

The core strategy is:

- Make the common case more restrictive than Rust.
- Reserve flexible behavior for explicit escape hatches.
- Encode escape behavior, mutability, effects, sharing, and dispatch in the source language.
- Lower those source-level guarantees into LLVM attributes and metadata automatically.
- Prefer a few high-value restrictions that unlock major optimizations over many low-value restrictions that only make the language harder to use.

The important design principle is that every restriction should justify itself by unlocking stronger IR:

- more `captures(...)`
- more `noalias`
- more `readonly`
- more `dereferenceable`
- more `align`
- more `nounwind`
- more `nosync`
- more `nofree`
- more `memory(...)`
- more `willreturn`
- more `mustprogress`
- more `initializes(...)`
- more `dead_on_return`

Stark's identity should be:

- Rust is strongest on ownership safety and general-purpose ergonomics.
- Stark is strongest on effect visibility, escape visibility, transitive immutability, and closed-world optimization.

That means the safe default in Stark should be:

- non-escaping borrows
- transitive immutability
- non-shared data
- static dispatch
- explicit output initialization
- trivial cleanup behavior

Everything more flexible should be requested explicitly.

## 1. Non-Escaping Borrows By Default

Rust borrows describe validity, but they do not make escape class the main source-level distinction. Stark can be stricter by making escape behavior explicit in the type system.

Suggested borrow classes:

- `borrow T`: may not be stored, returned, or forwarded to unknown code
- `retborrow T`: may escape only through the return value
- `storeborrow T`: may be stored or otherwise escape

This is stricter than a generic borrow model because the default borrow is ephemeral and call-local.

### Why it matters

If most borrows are guaranteed non-escaping, the compiler can emit stronger capture information much more often. That helps alias analysis, load forwarding, stack promotion, and interprocedural optimization.

### LLVM information unlocked

- `captures(none)` for ordinary `borrow`
- `captures(ret: address, provenance)` for `retborrow`
- `returned` when a function literally returns the same pointer argument
- `nofree` reasoning stays stronger when uncaptured pointers cross calls

### Design consequence

Most code should use `borrow` by default. Escaping a borrow should be a separate, visible capability rather than the default behavior.

## 2. Transitive Immutability, Not Just Shared Reference

Rust shared references are not maximally immutable because interior mutability exists. Stark can extract more optimizer facts by distinguishing simple sharing from deep freezing.

Suggested distinction:

- `frozen T`: nothing reachable through this reference may be mutated
- `shared T`: shared access is allowed, but explicit mutation-capable primitives may exist

Under `frozen`, the language should ban interior mutability, hidden atomics, and mutation through reachable aliases.

### Why it matters

LLVM optimization gets much stronger when the compiler knows that all reachable memory is truly read-only for the duration of the borrow, not just "not written through this particular path."

### LLVM information unlocked

- `readonly` on parameters much more often
- `memory(argmem: read)` or stronger function memory effects
- invariance-style reasoning for truly immutable memory
- more load hoisting and redundant load elimination

### Design consequence

Stark should separate "shared access" from "deeply frozen access." The latter is the real optimization lever.

## 3. First-Class `out` and `init` Parameters

Rust can model output initialization with library patterns like `MaybeUninit`, but it is not a primary function-typing concept. Stark can be stricter and clearer by making initialization contracts part of the language.

Suggested forms:

- `out T`
- `out [T; N]`
- `init T`

The contract should be:

- the callee must write the required bytes before return
- the callee may not read bytes before initializing them
- the caller treats the destination as uninitialized until the call completes

### Why it matters

This turns a common low-level pattern into something LLVM can reason about directly instead of inferring through ordinary pointer traffic.

### LLVM information unlocked

- `initializes(...)`
- `writable`
- `dead_on_return`
- better dead-store elimination
- better reasoning about fill-only APIs and constructors

### Design consequence

Buffer-filling and structure-construction APIs should prefer `out` and `init` parameters over ordinary mutable borrows when the real intent is initialization rather than mutation of a live value.

## 4. A Real Effect System

This is one of the biggest opportunities to be meaningfully more explicit than Rust. Stark should make operational guarantees part of function types or declarations, not just inferred backend properties.

Suggested effects:

- `pure`
- `read`
- `nosync`
- `nofree`
- `nounwind`
- `willreturn`
- `mustprogress`
- `cold`
- `tail`

These should be source-level guarantees, not merely optimization hints.

### Why it matters

When effect classes are explicit, LLVM does not need to rediscover them by inference. More functions can carry the strongest valid attributes from the start.

### LLVM information unlocked

- `memory(none)`
- `memory(argmem: read)`
- `nosync`
- `nofree`
- `nounwind`
- `willreturn`
- `mustprogress`
- better inlining and tail-call decisions

### Design consequence

Stark should treat effects as a core type-system feature, not just optional annotations. This is a major source of information Rust usually cannot express directly in function signatures.

## 5. Restrict Destructors Much More Than Rust

Rust `Drop` is powerful, but that power weakens optimizer certainty. A destructor that may allocate, synchronize, panic, or call arbitrary code makes IR more conservative.

Stark can be stricter by making trivial destruction the default and sharply limiting effectful cleanup.

Suggested policy:

- POD-by-default
- trivial destructors by default
- effectful destructors only in explicit zones
- destructors in safe code cannot panic
- destructors in safe code cannot synchronize or allocate unless explicitly opted into a more expensive category

### Why it matters

The more cleanup code can do, the harder it is to preserve `nounwind`, `nosync`, `nofree`, and tail-call-friendly control flow.

### LLVM information unlocked

- broader `nounwind`
- broader `nosync`
- simpler CFGs
- better tail-call opportunities
- fewer hidden call edges

### Design consequence

Stark should strongly prefer explicit teardown functions for complex cleanup rather than letting all cleanup behavior hide behind automatic destruction.

## 6. Explicit Shared-State Capability

If shared mutable state is part of the ordinary object model, the optimizer must stay conservative more often. Stark can be stricter by separating ordinary owned data from explicitly shared data.

Suggested model:

- normal heap objects are non-shared by default
- publishing to shared state is an explicit transition
- shared memory lives in a distinct type family or capability domain
- atomics and mutex-backed mutation are legal only in that explicit shared domain

### Why it matters

If the compiler can assume most data is not concurrently shared, it can keep ordinary loads and stores non-atomic and mark more functions `nosync`.

### LLVM information unlocked

- plain `load` and `store` in more code
- fewer atomics
- broader `nosync`
- stronger alias and dereferenceability reasoning
- more speculation and loop optimization

### Design consequence

Shared-state programming should be possible, but clearly marked. The default semantic world should be non-shared.

## 7. Closed-World Dispatch By Default

Dynamic dispatch, address-taken functions, and open-world linking all reduce optimization visibility. Stark can be stricter by making closed-world assumptions the default.

Suggested defaults:

- static dispatch
- monomorphization for generics
- sealed laws or traits by default
- no address-taken functions unless explicitly requested
- internal linkage by default
- dynamic dispatch only through explicit runtime-facing constructs

### Why it matters

LLVM becomes much stronger when the frontend can present finite callee sets, internal functions, and closed dispatch graphs.

### LLVM information unlocked

- better devirtualization
- more internalization
- more `fastcc`
- better ThinLTO and inlining outcomes
- indirect-call promotion opportunities
- finite-callee metadata opportunities

### Design consequence

Stark should optimize for whole-program or mostly-closed-world compilation first, and treat open-world dispatch as an explicit concession to flexibility.

## 8. Stronger Slice and Array Contracts

Rust slices already carry length, but Stark can go further and make more hot-path layout and aliasing facts explicit.

Suggested slice qualifiers:

- contiguous
- stride = 1
- aligned(16/32/64)
- exact length
- length multiple
- disjoint from another slice
- mutable but non-overlapping

These should be part of the type or parameter contract for performance-critical APIs.

### Why it matters

Loop vectorization and bounds-check elimination get much easier when alignment, disjointness, and trip-shape constraints are explicit rather than inferred from arbitrary pointer arithmetic.

### LLVM information unlocked

- `align`
- `dereferenceable(N)`
- `noalias`
- `range` attributes and metadata
- better GEP flags
- stronger vectorization-friendly loop facts

### Design consequence

Stark should make "fast slice" contracts explicit and ergonomic for hot code. This is one of the clearest places to expose more information than Rust does by default.

## Summary

The borrower system Stark needs is not "Rust borrowing with different syntax." It is a stricter semantic system built around:

- escape classes
- transitive immutability
- first-class initialization contracts
- explicit effects
- heavily limited destructors
- explicit shared-state capabilities
- closed-world dispatch
- richer slice contracts

These restrictions matter because they give the compiler stronger facts more often, and those facts map directly to LLVM optimization power.

The high-level rule should be:

- safe code is the maximally optimizable subset
- flexible behavior exists, but must be explicitly requested
- every extra capability should weaken IR only when the programmer actually needs it

That is the most plausible path for Stark to expose more optimizer-relevant information than Rust in ordinary code.
