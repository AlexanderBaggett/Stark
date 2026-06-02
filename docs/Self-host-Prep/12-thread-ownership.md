# Phase 12 - Thread Ownership & Data-Race Freedom

Status: **design direction locked — no implementation yet.** This document records
the thread-ownership model Alexander accepted on 2026-06-01. Syntax examples show
the intended shape, but parser/API spelling can still be adjusted during
implementation.

The central rule:

> A Stark thread owns an execution root: one function or closure invocation, plus
> the dynamic call tree reachable from that invocation.

Functions themselves are static code and are not owned. A thread owns invocations,
stack frames, temporaries, moved captures, owned values created by the call tree,
and borrows created inside that call tree. Borrowing is checked relative to that
execution region. Static storage is not part of any thread's owned execution tree.

## 1. Goals

Make these invalid states unrepresentable in safe Stark:

1. **Data races** — two thread execution trees touching the same memory, at least
   one writing, without an explicit synchronization or atomic mechanism.
2. **Cross-thread use-after-free** — a thread retaining a borrow into a parent
   stack frame or owned value after that parent scope exits.
3. **Thread-handle misuse** — joining twice, detaching after join, or detaching
   twice.
4. **Unrepresentable start failure** — a failed OS thread start should not create
   a half-valid `Thread` handle.

These stay runtime concerns:

- deadlock
- livelock
- starvation
- scheduler fairness
- platform thread start/join/detach failure

Deadlock is memory safe but stuck. Stark does not pretend the type system can prove
general liveness.

## 2. Current State

`System.Threading.Thread` currently takes a non-capturing
`ThreadEntry = fnptr<fn i32[min max]()>`. That keeps ordinary caller state out of
thread entries, but it does **not** make global mutable state safe. Direct access to
`static mut` from multiple threads would be a data-race surface unless every access
is forced through an explicit safe mechanism.

`Join` and `Detach` currently take `mut borrow Thread self`, so a handle remains
usable after either operation. That is why `ThreadError.AlreadyJoined`,
`ThreadError.AlreadyDetached`, and part of `ThreadError.InvalidState` exist as
runtime errors.

Existing language pieces this design reuses:

- ownership and move/use-after-move tracking
- borrow escape analysis
- closure capture modes (`copy`, `move`, `read`, `mut`, `out`, `init`)
- `heap closure<once fn ...>` for owned call-once work
- `disjoint` / `overlap` memory contracts
- `frozen` / `const` deep-immutability concepts
- `shared T` and `capture(unsafe shared x)`, which need real semantics or removal

## 3. Locked Model

### 3.1 Thread Execution Roots

An owning thread starts from a moved execution root:

```stark
stack System.Threading.Thread<i32[min max]> worker =
    try System.Threading.Thread<i32[min max]>.Start(
        heap capture(move config) once fn i32[min max]()
        {
            return CompilePackage(config);
        });
```

The spawned thread owns:

- the closure invocation
- `config`, because it was moved into the closure
- every stack frame under `CompilePackage`
- owned values created by that call tree
- borrows created inside that call tree

The caller no longer owns `config`. Use-after-move rules handle that exactly like
any other ownership transfer.

### 3.2 Method Calls And Receivers

Methods matter because the receiver says what authority enters the thread's
execution tree.

```stark
fn i32[min max] Run(Worker self);             // owning receiver
fn i32[min max] Inspect(borrow Worker self);  // readonly borrow
fn void Mutate(mut borrow Worker self);       // exclusive mutable borrow
```

For an owning thread, `Worker self` may be moved into the execution root. Ordinary
parent-scope borrows may not be captured because the new thread can outlive the
parent scope.

For a scoped thread, `borrow Worker self` and `mut borrow Worker self` may be
captured only when the scoped-thread construct proves every child execution joins
before the borrowed storage can be destroyed or reused.

Doctrines remain compile-time contracts, not runtime instances. Doctrine-constrained
methods participate through the concrete receiver and ordinary ownership rules.

### 3.3 Scoped Threads

Scoped parallelism is a language-level lifetime form, not just an ordinary library
callback. The handle type can remain in `System.Threading`, but the compiler needs
to understand the lexical join boundary.

```stark
fn i64[min max] SumAll(i64[min max][] values)
{
    stack mut i64[min max] left = 0;
    stack mut i64[min max] right = 0;

    thread scope (pool)
    {
        pool.Spawn(capture(mut left, read values) fn void()
        {
            left = Sum(values[0, values.Length / 2]);
        });

        pool.Spawn(capture(mut right, read values) fn void()
        {
            right = Sum(values[values.Length / 2, values.Length / 2]);
        });
    }

    return left + right;
}
```

The `thread scope` block is the join point. Every child execution spawned from the
scope is joined before the block exits, including early-return paths. Borrowed
captures are valid only because the block structurally proves the children finish
before the parent scope continues.

The ordinary borrow checker still applies:

- two child executions may not mutably borrow the same local
- a parent may not mutate a value while a child holds a live borrow of it
- mutable slice sharing requires visible disjointness, such as non-overlapping
  ranges or a `where disjoint(...)` proof

### 3.4 Owning Threads

Owning threads are dynamic and may be stored, joined later, or detached. They cannot
capture ordinary parent-scope borrows. They must own everything they need, or access
shared state through one of the explicit shared mechanisms in section 4.

Locked API direction:

```stark
public enum ThreadError
{
    StartFailed,
    JoinFailed,
    InvalidState,
}

public enum ThreadStartResult<T>
{
    [Ok] Ok(Thread<T>),
    [Err] Err(ThreadError),
}

public enum ThreadJoinResult<T>
{
    [Ok] Ok(T),
    [Err] Err(ThreadError),
}

public enum ThreadStatus
{
    [Ok] Ok,
    [Err] Err(ThreadError),
}

public struct Thread<T>
{
    static fn ThreadStartResult<T> Start(heap closure<once fn T()> entry);
    fn ThreadJoinResult<T> Join(Thread<T> self);
    fn ThreadStatus Detach(Thread<T> self);
}
```

`Start` returns a result. A failed start never creates a half-valid thread handle.
`Join` and `Detach` consume the handle, so joining twice and detach-after-join become
ordinary use-after-move compile errors.

`Thread<T>` is a standard-library type. Thread execution regions and scoped
threading are type-system concepts the compiler understands.

## 4. Static And Shared State

### 4.1 `static mut`

`static mut` is not owned by any thread. A thread does not borrow it and later
return ownership. It is global storage with program lifetime, reachable from
multiple execution roots.

Locked rule:

> Safe threaded Stark code may not directly read or write `static mut`.

Direct mutable global access must be one of:

```stark
static const Config DefaultConfig = ...;          // shared immutable data
threadlocal mut DiagnosticsBuffer Diagnostics;   // per-thread mutable data
static Mutex<GlobalCache> Cache;                  // shared mutable data via guard
static AtomicI64 Counter;                         // shared mutable data via atomics
unsafe static mut PlatformState RawState;         // unsafe/platform boundary only
```

Accessing `unsafe static mut` requires an unsafe context and carries no safe
data-race guarantee. It is for runtime, platform, and FFI internals, not ordinary
portable Stark code.

### 4.2 Thread-Local Storage

Thread-local storage is the safe answer for mutable global-shaped state that should
be independent per thread.

```stark
threadlocal mut DiagnosticsBuffer Diagnostics;
threadlocal Arena Scratch;
```

Rules:

- every thread has its own instance
- ordinary access is local to the current thread's execution tree
- ordinary borrows of a thread-local may not cross into another thread
- thread-local destructors run when the owning thread exits
- initialization failure, if any, must be represented explicitly in the type/API

Thread locals are not shared state. They are per-thread owned static-duration
storage.

### 4.3 Immutable Shared State

`static const` and proven `const` / `frozen` data may be shared by multiple threads.
Read-only sharing cannot race, and safe Stark already prevents mutation through
deeply immutable views.

Owning threads may read immutable static data. Scoped threads may borrow immutable
parent data when the scope proves the borrow lifetime is contained.

### 4.4 Synchronized Shared State

Shared mutable data must live inside a synchronization container that owns the data.
The thread borrows the guard, not the global variable directly.

```stark
static Mutex<i64[min max]> Counter;

fn void Bump()
{
    stack LockGuard<i64[min max]> guard = Counter.Lock();
    guard.Value() = guard.Value() + 1;
}
```

`Mutex<T>` / `RwLock<T>` / `Once<T>` are standard-library types backed by platform
synchronization. The compiler-visible facts are:

- the payload is unreachable except through a guard
- the guard cannot cross a thread boundary
- borrows produced by a guard cannot escape the guard lifetime
- unlocking on guard cleanup is permitted as a synchronization-resource exception
  to the ordinary "destructors do not synchronize" rule, or must be represented by
  a future `lock scope` form if Stark rejects that exception

This exception must be documented before `Mutex<T>` lands. It is the main remaining
semantic wrinkle for locks.

### 4.5 Atomics

Atomic types are the only safe direct shared-mutation escape hatch.

```stark
static AtomicI64 Counter;
```

Atomic operations are explicit operations on atomic types. A plain `i64[min max]`
static never becomes atomic by being global.

The memory-ordering surface is not locked here. It belongs to the S16 atomics work.

### 4.6 Channels

Channels are preferred for many self-hosted compiler workflows because they move
ownership between thread execution trees instead of sharing mutable memory.

```stark
stack Channel<CompileJob> jobs = Channel<CompileJob>.New();
stack Channel<CompileResult> results = Channel<CompileResult>.New();
```

Values sent through a channel must satisfy the cross-thread transfer rules in
section 5.

## 5. Cross-Thread Transfer Facts

The compiler needs two internal facts, even if the final surface does not expose
Rust-style `Send` / `Sync` names:

| Internal fact | Meaning |
|---|---|
| **Thread-transferable** | A value may be moved into another thread execution tree. |
| **Thread-shareable** | A reference/capability may be reached from multiple thread execution trees. |

Thread-transferable is structural by default for ordinary owned data:

- primitive values are transferable
- owned structs/records/enums are transferable when all owned fields are
  transferable
- `heap closure<once fn T()>` is transferable only when its captured environment is
  transferable
- ordinary borrows are not transferable into owning threads
- lock guards are not transferable
- raw pointers, opaque FFI handles, and unsafe platform types require explicit unsafe
  classification

Thread-shareable is narrower:

- deeply immutable data is shareable
- synchronization containers are shareable through their safe APIs
- atomics are shareable through atomic operations
- ordinary mutable storage is not shareable

Diagnostics should name these facts directly, for example:

```text
STK-TH02: cannot move 'guard' into thread: LockGuard<T> is not thread-transferable.
STK-TH03: cannot capture 'buffer' by borrow for owning thread: parent-stack borrows
          cannot outlive the spawning invocation.
STK-TH04: cannot access static mut 'Counter' from safe threaded code; use
          threadlocal, Mutex, Atomic, or unsafe static mut.
```

Whether these facts become named doctrine/trait-style bounds is deferred until
generic channels, pools, and shared-ownership wrappers need to spell the constraint.

## 6. Work Breakdown (TH*)

TDD-first, in dependency order.

| ID | Item |
|---|---|
| TH01 | Change thread start to `ThreadStartResult<T>` so failed starts do not create invalid handles. |
| TH02 | Make `Join(Thread<T> self)` and `Detach(Thread<T> self)` consume handles; remove joined/detached runtime states. |
| TH03 | Add compiler thread-execution-root model and diagnostics for owning-thread captures. |
| TH04 | Implement `Thread<T>` over `heap closure<once fn T()>`. |
| TH05 | Add `thread scope` as a compiler-known lexical join/lifetime form. |
| TH06 | Add `threadlocal` storage and thread-exit destruction rules. |
| TH07 | Forbid direct safe `static mut` access from threaded code; define `unsafe static mut`. |
| TH08 | Add internal thread-transferable and thread-shareable facts. |
| TH09 | Add channels for ownership-moving communication. |
| TH10 | Add `Mutex<T>` / `RwLock<T>` / `Once<T>` once guard cleanup semantics are documented. |
| TH11 | Add atomics and memory-ordering surface. |
| TH12 | Decide whether `shared T` is tied to synchronized/atomic capabilities or retired. |
| TH13 | User-facing docs: LanguageReference threading section, StandardLibrary threading docs, SKILL, book samples, roadmap sync. |

## 7. Remaining Decisions

The broad model above is locked. These implementation details remain open:

| ID | Decision |
|---|---|
| OQ-TH1 | Exact spelling of the scoped-thread construct: `thread scope`, `threads scope`, or another grammar form. |
| OQ-TH2 | Whether thread-transferable/thread-shareable become named doctrine/trait-style bounds for generic APIs. |
| OQ-TH3 | Whether lock guard cleanup is an allowed destructor-synchronization exception or requires a `lock scope` language form. |
| OQ-TH4 | The public atomic type set and memory-ordering vocabulary. |
| OQ-TH5 | The fate of existing `shared T`: attach it to synchronized/atomic capabilities, or retire it before it gains users. |
| OQ-TH6 | Whether `unsafe static mut` is allowed only in runtime/platform modules or also in user unsafe code. |

## 8. Relationship To Self-Hosting

This design resolves the shape of L10 as "threads and ownership," not `async` /
`await`. The self-hosted compiler can still be ported synchronously first.

Recommended self-hosting path:

1. Port the compiler synchronously.
2. Use channels and owning worker threads for parallel build orchestration.
3. Use scoped threads later for data-parallel compiler passes where borrow/disjoint
   proofs are valuable.
4. Avoid shared mutable compiler globals. Prefer `threadlocal` diagnostics/scratch
   state, immutable configuration, channels, and owned per-pass state.

This keeps Stark's rule intact: shared mutation is never accidental.
