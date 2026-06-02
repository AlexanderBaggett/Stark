# Phase 12 - Thread Ownership & Data-Race Freedom

Status: **design draft — no decision locked, no implementation.** This document frames
how Stark extends "invalid states are unrepresentable" across threads, presents the
design forks with concrete syntax options, and records what deliberately stays a
runtime concern. Each fork needs an explicit design lock before any implementation
(grammar, typing, stdlib, or otherwise) begins.

The driving observation (from design discussion with Alexander, 2026-06-01): the
single-threaded guarantees — ownership, borrows, ranges, exhaustiveness — extend to
threads because **ownership is per-value, not per-thread**. The compiler never needs
to know how many threads exist or when a dynamically created thread dies; it needs
the same fact it already tracks: every value has one owner, and borrows cannot
outlive what they borrow. A thread is just another owner/borrower.

## 1. Goal

Make the following invalid states unrepresentable at compile time:

1. **Data races** — two threads touching the same memory, at least one writing,
   without synchronization.
2. **Cross-thread use-after-free** — a thread referencing stack or owned memory that
   its creating scope has already destroyed.
3. **Thread-handle misuse** — joining a thread twice, detaching a joined thread
   (today these are *runtime* errors: `ThreadError.AlreadyJoined` /
   `AlreadyDetached`).

And explicitly **not** in scope for compile-time guarantees:

4. Deadlock, livelock, starvation — liveness properties. No production type system
   closes these; Stark will not pretend to. They stay observable runtime behavior
   (see §5).

## 2. Current State (verified)

- `System.Threading.Thread` takes a `ThreadEntry = fnptr<fn i32[min max]()>` — a
  **non-capturing function pointer**. Threads cannot borrow or own any caller state;
  the only sharing channel is `static mut` globals. This is the degenerate safe
  case: no data-race surface because there is no data sharing.
- `Join` / `Detach` take `mut borrow Thread self`, so a joined handle still exists
  afterward — which is exactly why `AlreadyJoined` / `AlreadyDetached` /
  `InvalidState` exist as runtime errors (4 of `ThreadError`'s 5 variants are
  handle-lifecycle errors, not platform failures).
- The type system already has the building blocks:
  - borrow-escape analysis (borrows cannot leave their scope),
  - move/use-after-move tracking,
  - `disjoint` / `overlap` parameter memory contracts,
  - an `independent` loop contract (iterations have no loop-carried memory
    dependency) — the data-parallel groundwork,
  - a `shared T` access qualifier ("explicit shared access domain") that is parsed
    and typed but has no concurrency semantics attached yet,
  - `capture(unsafe shared x)` as the explicit unsafe escape hatch into shared
    domains.
- S16 (threading/sync stdlib: mutex/once/atomics/channels) and L10 (build-driver
  concurrency) are open roadmap items that depend on this design.

## 3. The Design Frame

### 3.1 Two kinds of threads, two existing rules

| Thread kind | May it borrow caller state? | Why it is sound | Existing mechanism that enforces it |
|---|---|---|---|
| **Scoped** — provably joined before a lexical scope exits | Yes | The borrow's lifetime (the scope) contains the thread's lifetime | Ordinary borrow-escape rules: the closure's borrows are checked against the scope that joins it |
| **Owning** (detached / stored / dynamically managed) | No — it must **own** everything it touches | Ownership transfer is permanent; the thread's lifetime is irrelevant | Ordinary move semantics: captures are moves; use-after-move applies to the caller |

Dynamic thread creation does not break this. Spawning N owning threads in a loop is
N ownership transfers, each checked exactly like passing a value to any consuming
function. The count and the join order never enter into it.

### 3.2 Sharing between threads

When two threads must reach the same data, the data must be one of:

1. **Deeply immutable + lifetime-safe** — `frozen`/`const` views over data whose
   owner provably outlives both threads (for scoped threads: the enclosing scope;
   for owning threads: `static` or a shared-ownership box). Read-only sharing can
   never race.
2. **Inside a synchronization container** — a `Mutex<T>`-style type that **owns** its
   payload. The only access path is through a lock guard, which is a borrow that
   cannot escape the guard's scope — Stark's existing borrow-escape rule, reused.

Both are types. Nothing about them requires runtime bookkeeping beyond the lock
itself.

### 3.3 What may cross a thread boundary: a structural marker

Some types must never cross threads even by move: a borrow (points into another
thread's stack), a lock *guard*, a non-atomically-refcounted handle. The gate is a
**structural property of the type** — exactly the same philosophy as `[Ok]`/`[Err]`
roles (structural, never nominal, never stdlib-privileged):

- A type may cross a thread boundary if all of its fields may; primitives, owned
  aggregates, and owning containers cross; borrows and guards do not.
- The compiler derives this bottom-up. There is no per-type opt-in annotation to
  forget; unsafe FFI types opt **out** (or in) explicitly.

This is Rust's `Send` made structural-by-default. Whether Stark also needs the
`Sync` half (what may be *referenced* from two threads simultaneously) collapses
into the lock/immutable rules in §3.2 — a `&T`-style shared view only crosses inside
a container that already guarantees synchronization.

## 4. Surface Design — the forks to lock

> Everything below is **proposed syntax to react to**, not a decision. Each fork
> lists options and a lean. Locking any of them is Alexander's call.

### 4.1 Scoped threads (the borrowing kind)

**Option A — a `scope`-block spawn surface (lean):**

```stark
fn i64[min max] SumAll(i64[min max][] values)
{
    stack mut i64[min max] left = 0;
    stack mut i64[min max] right = 0;

    threads scope (pool)
    {
        pool.Spawn(fn() { left = Sum(values[0, values.Length / 2]); });
        pool.Spawn(fn() { right = Sum(values[values.Length / 2, values.Length / 2]); });
    }                          // ← every spawned thread is joined here, unconditionally

    return left + right;
}
```

- The block is the join point; it is not possible to write the spawn without the
  join.
- Closures may borrow anything from the enclosing scope (the loans end at the
  block's close, after the joins).
- The two closures above mutably borrow *different* locals; two closures mutably
  borrowing the *same* local is rejected by the ordinary exclusive-borrow rule. For
  slices, `disjoint` contracts let two threads write non-overlapping halves.

**Option B — library-only (no syntax): `ThreadScope.Run(fn(scope) { ... })`** — the
same semantics expressed as a stdlib higher-order function. Less visible, no grammar
change, but the "join is unskippable" guarantee now depends on closure/borrow rules
around the callback rather than a structural block.

**Option C — defer scoped threads entirely** (only owning threads in v1). Simplest;
loses borrowing parallelism (every parallel computation must copy or move its
inputs).

### 4.2 Owning threads (the dynamic kind)

Today's `Thread` becomes the owning kind, upgraded from `fnptr` to a move-capturing
closure:

```stark
public struct Thread
{
    // captures are MOVES; the closure must satisfy the cross-thread marker (§3.3)
    Thread(heap closure<once fn i32[min max]()> entry);

    fn ThreadJoinResult Join(Thread self);     // consumes the handle (§4.5)
    fn ThreadStatus Detach(Thread self);       // consumes the handle
}
```

- `heap closure<once fn ...>` is the existing owned, call-once closure type — the
  natural carrier for "runs exactly once on another thread."
- No borrows can be captured (the marker rejects them), so the thread's unbounded
  lifetime is safe by construction.
- Sharing into an owning thread goes through §4.4 containers or immutable
  `static`/shared-ownership data.

**Fork:** does the entry closure's return value stay `i32[min max]` (process-exit
style), or become generic `T` retrieved by `Join` (`ThreadJoinResult<T>`)? Lean:
generic, with `Join(Thread<T> self) -> ThreadJoinResult<T>` — it is the same
machinery and removes a whole class of "smuggle the result through a global"
patterns.

### 4.3 The cross-thread marker

**Fork: name and exposure.**

- Option A — fully implicit (lean): the property has no surface name; the compiler
  computes it structurally and diagnostics say *why* a capture is rejected ("captures
  a borrow", "captures a lock guard"). Nothing to learn until violated.
- Option B — a named trait-like marker (`Portable` / `ThreadSafe`) that appears in
  `where` clauses, so generic APIs can require it (`where T: Portable`). More
  expressive for generic stdlib code (channels need it), more surface area.
- Likely landing point: **A for v1** (concrete spawn APIs only), revisit B when
  S16's generic channels/pools need to state the bound.

### 4.4 Synchronization containers (lock owns data)

```stark
public struct Mutex<T>
{
    Mutex(T value);                         // the payload moves INTO the lock

    fn LockGuard<T> Lock(borrow Mutex<T> self);
}

public struct LockGuard<T>
{
    // deref-style access; the guard is a borrow-like value that cannot escape
    // its scope (existing borrow-escape rule) and unlocks on drop (existing drop)
    fn mut borrow T Value(mut borrow LockGuard<T> self);

    mut drop;                               // unlock
}
```

- There is no way to reach `T` without holding a guard; "forgot to lock" is
  unrepresentable.
- Guards do not satisfy the cross-thread marker, so a guard cannot be smuggled to
  another thread.
- **Fork:** is `Mutex` stdlib-only (S16, lean) or does it need language support?
  Lean: pure stdlib over existing ownership rules + an OS futex/`pthread_mutex` FFI;
  no language change. Same shape later for `RwLock<T>`, `Once<T>`, channels.

### 4.5 Handle-consuming `Join`/`Detach` (standalone, could land first)

Independent of everything above: change `Join`/`Detach` from `mut borrow Thread self`
to consuming `Thread self`. Joining twice becomes use-after-move (compile error);
`AlreadyJoined`, `AlreadyDetached`, and the handle-state half of `InvalidState`
disappear from `ThreadError`. The drop impl keeps detaching unjoined threads.

This is a pure API fix to today's stdlib using today's language. It could be locked
and implemented before (or with) any of §4.1–§4.4.

## 5. What Stays Runtime — the liveness boundary

Stated explicitly so it is never silently reinterpreted:

- **Deadlock** (lock ordering cycles), **livelock**, and **starvation** are liveness
  properties. The type system does not and will not prevent them. A deadlocked Stark
  program is a stuck-but-memory-safe program.
- Lock **poisoning** (a thread dies while holding a lock): with no panics/unwinding
  in Stark (L07 closed with no trap surface), a thread exits only by returning or by
  process exit — so poisoning as Rust knows it may not need to exist. To confirm
  during S16 design.
- Thread **start failure** (`ThreadError.StartFailed`) is a real environment error
  and stays a `Result`/`try` value.

## 6. Work Breakdown (TH*)

TDD-first, in dependency order. Nothing starts until its design fork (§4) is locked.

| ID | Item | Depends on lock of |
|---|---|---|
| TH01 | Handle-consuming `Join`/`Detach` + `ThreadError` cleanup + doc/test updates | §4.5 |
| TH02 | Cross-thread structural marker in the type model + capture-rejection diagnostics (no syntax) | §4.3 |
| TH03 | Owning `Thread` over `heap closure<once fn ...>` (move-capture entry) | §4.2, TH02 |
| TH04 | Generic thread results (`Thread<T>` / `ThreadJoinResult<T>`) | §4.2 fork |
| TH05 | Scoped-thread surface (grammar if Option A; stdlib if Option B) + borrow-lifetime integration | §4.1, TH02 |
| TH06 | `Mutex<T>` + `LockGuard<T>` (stdlib + platform FFI) | §4.4, TH02 |
| TH07 | `shared T` qualifier: either give it real semantics tied to this model, or retire it | §4.3/§4.4 review |
| TH08 | Atomics / `Once<T>` / channels (S16 completion) | TH06 |
| TH09 | Parallel build-driver port experiment (L10 / OQ-11 option B) | TH03 or TH05 |
| TH10 | User-facing docs: LanguageReference threading section, SKILL, book chapter, doc syncs | each landed TH |

## 7. Open Questions (OQ-TH*)

| ID | Question | Options / lean |
|---|---|---|
| OQ-TH1 | Scoped-thread surface | §4.1 A (block syntax) vs B (stdlib HOF) vs C (defer). Lean A — the join-unskippable property should be structural, like `try`'s visibility. |
| OQ-TH2 | Marker exposure | §4.3 A (implicit, lean for v1) vs B (named bound for generics). |
| OQ-TH3 | Generic thread results | `Thread<T>`/`ThreadJoinResult<T>` (lean) vs `i32`-only entry. |
| OQ-TH4 | `shared` qualifier fate | Attach real semantics (a view type only valid inside sync containers?) vs retire the keyword. Today it is surface without semantics. |
| OQ-TH5 | Does Stark need `Sync` separately from `Send`? | Lean no for v1 — shared views only cross inside containers (§3.2), which collapses the distinction. Revisit for lock-free read sharing. |
| OQ-TH6 | Atomics surface | Raw atomic types (`AtomicI64`) vs only inside containers. Defer to S16/TH08. |

## 8. Relationship to Existing Docs

- `01-language-feature-gaps.md` **L10** — this design is the substance behind
  "async / build-driver concurrency replacement"; resolving OQ-TH1..3 resolves L10's
  "unspecified" design state.
- `02-stdlib-gaps.md` **S16** — mutex/once/atomics/channels become TH06/TH08;
  **S15** (time) is independent.
- `07-open-questions.md` **OQ-11** — build-driver concurrency: option B (threads)
  becomes viable once TH03/TH05 exist; the port can still start synchronous (option
  A) regardless.
- `09-self-hosted-compiler-architecture.md` — the Error Model section's convention
  is unaffected; a future parallel build driver uses these primitives.
- `10/11` — the structural-marker philosophy here is the same as trait conformance
  and `[Ok]`/`[Err]` roles: properties derive from structure, never from names or
  stdlib identity.
