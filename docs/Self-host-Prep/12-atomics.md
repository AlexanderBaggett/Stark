# Phase 12 - Atomics

Status: **implemented (2026-06-02).** All 29 atomic types (AT01-AT07) are in the
stdlib, lower through all three tiers, and are verified by runtime/emission/package
tests. Design locked by Alexander: atomic types covering Stark's full integer width
family, as data types (not a keyword qualifier), seq-cst only in v1.

This phase delivers the **minimal safe-sharing primitive for the threads Stark
already ships**. The broader thread-ownership redesign (execution roots, scoped
threads, `threadlocal`, the `static mut` rules) is **parked until after
self-hosting** — the host compiler is functionally single-threaded (zero uses of
parallelism; its `async` is sequential I/O idiom), so a synchronous port is
behaviorally identical and none of that machinery is needed for self-hosting. The
parked model is preserved in git history (`docs/Self-host-Prep/12-thread-ownership.md`
prior to this revision, commit `1f8ee74`) and the evolving `Transferable`/`Shareable`
law design lives in `stark-thread-safety-laws.md` (draft).

## 1. Why Atomics Now

Threading is already shipped, user-facing surface: `System.Threading.Thread` exists
and the book documents it (Ch24). But the **only** way two threads can share state
today is `static mut` — which is a data race. Atomics close that gap with the
smallest possible scope:

- They work with today's `fnptr`-entry threads and struct statics (both verified).
- The LLVM emitter already produces atomic instructions (the heap allocator's
  spinlock uses `atomicrmw`/`store atomic` in every Stark executable today).
- They are the foundation the doc `22` coordination layer
  (`Synchronized<T>`, captured thread payloads, and channels) builds on — nothing
  here is throwaway.

## 2. Locked Design Decisions

| Decision | Choice | Why |
|---|---|---|
| Surface form | **Data types** (`AtomicI64`, …), never a keyword/qualifier | A qualifier makes `Counter = Counter + 1` look atomic while actually being a racy load+store — the invalid state becomes representable. Types make it unrepresentable: there is no `=`/`+`, only `.Add(1)` (one indivisible op) or visibly separate `.Load()`/`.Store()`. Also keeps atomic cost visible at call sites and avoids adding another qualifier axis to the type system. |
| Width coverage | **Every integer width Stark has** — i8…i1024, u8…u1024 (28 types) + `AtomicBool` | The atomic family mirrors the language's integer family exactly; no arbitrary-looking gaps. Hardware reality is handled by implementation tiers (§4), not by trimming the API. |
| Memory ordering | **Seq-cst only, no ordering parameter** | The safe default. Acquire/release/relaxed variants are a future extension if profiling ever justifies them (S16 follow-up). |
| Module | `System.Threading` | Keeps the thread-related surface in one module. |
| Genericity | **Monomorphic named types**, not `Atomic<T>` | `Atomic<T>` needs a "T is atomically operable" bound — that is L06 constraint machinery. Named types need nothing and each exposes exactly the operations that make sense. |

## 3. The Types and Operations

```stark
// In System.Threading — one type per Stark integer width, plus bool.
public struct AtomicI64
{
    AtomicI64(i64[min max] initial);

    fn i64[min max] Load(borrow AtomicI64 self);
    fn void Store(mut borrow AtomicI64 self, i64[min max] value);
    fn i64[min max] Add(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Sub(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] And(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Or(mut borrow AtomicI64 self, i64[min max] operand);        // returns previous value
    fn i64[min max] Xor(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Exchange(mut borrow AtomicI64 self, i64[min max] value);    // returns previous value
    fn bool CompareExchange(mut borrow AtomicI64 self, i64[min max] expected, i64[min max] desired);
}

public struct AtomicBool
{
    AtomicBool(bool initial);

    fn bool Load(borrow AtomicBool self);
    fn void Store(mut borrow AtomicBool self, bool value);
    fn bool Exchange(mut borrow AtomicBool self, bool value);
    fn bool CompareExchange(mut borrow AtomicBool self, bool expected, bool desired);
}
```

The same shape repeats for every width: `AtomicI8`, `AtomicI16`, `AtomicI24`,
`AtomicI32`, `AtomicI48`, `AtomicI64`, `AtomicI96`, `AtomicI128`, `AtomicI192`,
`AtomicI256`, `AtomicI384`, `AtomicI512`, `AtomicI768`, `AtomicI1024`, and the
unsigned `AtomicU*` family.

RMW operations return the **previous** value (fetch-and-modify semantics), which is
the strictly more powerful form; callers that want the new value compute it.

Canonical usage with today's threads:

```stark
import System.Threading
module Demo

// Module-level declarations use qualified type names (unqualified import
// resolution at module scope is a separate, pre-existing gap).
static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

fn i32[min max] Worker()
{
    stack mut i32[min max] i = 0;
    while willexit (i < 1000)
    {
        Counter.Add(1);
        i += 1;
    }
    return 0;
}

export fn i32[min max] main()
{
    stack mut System.Threading.Thread a = new(Worker);
    stack mut System.Threading.Thread b = new(Worker);
    a.Join();
    b.Join();
    return (i32[min max])Counter.Load();    // exactly 2000, every run
}
```

## 4. Implementation Tiers

Same API at every width; three lowering strategies. The tier is an implementation
fact documented on each type, never an API difference.

| Widths | Strategy | Cost character |
|---|---|---|
| 8, 16, 32, 64 | Single hardware instructions: `atomicrmw`, `cmpxchg`, `load atomic`, `store atomic` | One locked instruction |
| 24, 48, 96, 128 | **Lock-free CAS loop** on the value's existing power-of-2 storage container (i24 already stores in 4 bytes, i48 in 8, i96/i128 in 16) | Retry loop under contention; i128 uses `cmpxchg16b`-class instructions |
| 192, 256, 384, 512, 768, 1024 | **Embedded spinlock**: the struct carries its own lock word alongside the value | Serialized; not lock-free (no CPU supports lock-free atomics above 128 bits) |

Honesty rules for the wide tier:

- The lock word lives **inside the struct** — `sizeof(AtomicI256) > sizeof(i256[min max])`,
  visible in the layout. Never a hidden global lock table (no cross-value contention
  coupling, no hidden global state).
- The tier strategy is documented on the types and in the stdlib reference. The
  wording is factual: "no hardware supports lock-free atomics at this width; this
  type serializes through an embedded lock."
- The API being identical across tiers is deliberate: migrating a counter from
  `AtomicI64` to `AtomicI256` changes the documented cost, not the code.

## 5. Compiler Integration

- The atomic types are stdlib structs; their methods lower through compiler-known
  intrinsics (the `System.Math` intrinsic pattern) onto LLVM `atomicrmw`,
  `cmpxchg`, `load atomic`, and `store atomic` instructions — the same instruction
  family the heap allocator's lock already emits.
- Tier-1 widths must lower to single instructions. An LLVM emission test per width
  guards against accidental fallback to the CAS-loop or lock tiers.
- The borrow rules need no changes: `Load` takes `borrow self`, mutating operations
  take `mut borrow self`. Two threads reaching the same atomic through a `static`
  is exactly the sharing model the operations are designed for. The
  `Transferable`/`Shareable` law computation pass grants both laws intrinsically
  to compiler-known `System.Threading.Atomic*` types — they are the primitive that
  makes shared mutation safe.

## 6. Work Breakdown (AT*)

TDD-first, in dependency order.

| ID | Item | Status |
|---|---|---|
| AT01 | Stdlib type surface: `AtomicBool` + the 28 integer atomic types in `System.Threading`, constructors + method signatures | **done** |
| AT02 | Compiler intrinsic recognition + LLVM lowering, tier 1 (8/16/32/64): single-instruction `atomicrmw`/`cmpxchg`/`load atomic`/`store atomic` | **done** |
| AT03 | Tier 2 lowering (24/48/96/128): CAS loops on power-of-2 storage containers | **done** |
| AT04 | Tier 3 lowering (192–1024): embedded-spinlock layout + serialized operations | **done** |
| AT05 | Runtime tests: multi-threaded exact-count increments per tier, CAS contention, `AtomicBool` flag/spinlock pattern | **done** |
| AT06 | LLVM emission tests: tier-1 single-instruction guarantees, tier-3 layout (lock word present) | **done** |
| AT07 | Dist stdlib rebuild + package-image verification (atomics usable from packaged stdlib) | **done** |
| AT08 | User-facing docs: stdlib reference, LanguageReference/SKILL touch-ups, book Ch24 safe-sharing section, ROADMAP/doc sync | **done** |

## 6.1 Implementation Record (what shipped, beyond the spec)

The implementation matches §2-§5 with these refinements discovered during the work:

- **Canonical-extension invariant (tier 2).** A narrower-than-container value
  (24/48/96-bit) is always stored sign/zero-extended to its power-of-2 container, by
  the constructor and every mutation. Because extension commutes with
  `xchg`/`and`/`or`/`xor` and both comparands of `cmpxchg` can be pre-extended,
  **only `Add`/`Sub` need the CAS retry loop** — Load/Store/Exchange/And/Or/Xor/
  CompareExchange stay single lock-free instructions even at tier 2. (Carries do not
  commute with extension, which is why Add/Sub re-extend through the loop.)
- **`Add`/`Sub` wrap** at the value width (two's complement), like the `+%`/`-%`
  operators. This is the only implementable semantic for fetch-and-modify hardware
  instructions.
- **x86-64 baseline includes `cmpxchg16b`.** 128-bit atomics need it; the compiler
  stamps `"target-features"="+cx16"` on every emitted x86-64 function so inlining/LTO
  can never strand a 128-bit atomic in a function without the feature (which would
  produce un-linkable `__atomic_*` libcalls). Every x86-64 CPU since ~2006 has it.
- **Tier-3 layout**: value at offset 0 (consistent with the other tiers — the receiver
  pointer is the value pointer), `u32` lock word after it.
  `%System_Threading_AtomicI256 = type { i256, i32 }`. Lock acquire/release are
  seq-cst.
- **Builtin architecture**: recognition + layout facts live in
  `SystemThreadingAtomicFacts` (shared by SSA validation and LLVM emission);
  recognition is name-based (`System.Threading.AtomicXxx.Op`), the same pattern as
  the `System.Math`/`System.Collections` builtins.
- **Statics needed real compiler work**: `static mut AtomicI64 C = new AtomicI64(0);`
  required (a) the global-initializer planner to compile-time-trace explicit
  constructor bodies (assignments + constant-foldable `if`/`else`) — statics are
  comptime contexts per doc `13` — and (b) fixing MIR so method calls on globals pass
  the global's address as `self` rather than a copy. Both fixes are general, not
  atomics-specific.

## 7. Deferred (parked, not abandoned)

| Item | Where it lives |
|---|---|
| Full thread-ownership model (execution roots, scoped threads, `threadlocal`, `static mut` rules, owning `Thread<T>`) | Git history: doc 12 prior to this revision (commit `1f8ee74`) |
| `Transferable` / `Shareable` enforcement at call sites and thread boundaries | Doc `14`; declaration surface and law computation have landed |
| Captured thread payloads, `Synchronized<T>` / `Locked<T>`, and MPSC channels | Doc `22`; build on these atomics + existing platform wait/wake hooks where needed |
| Memory orderings beyond seq-cst | S16 follow-up, only with profiling justification |
| Consuming `Join`/`Detach` + `ThreadStartResult` API fix | Ride along whenever Threading.stark is next opened |

## 8. Relationship to the Roadmap

- **S16** (threading coordination): atomics are the first delivered slice; doc
  `22` limits the follow-up to captured thread payloads, `Synchronized<T>` /
  `Locked<T>`, and MPSC channels.
- **L10** (concurrency replacement): unaffected — confirmed that self-hosting needs
  no concurrency (the host compiler is single-threaded; its `async` is I/O idiom).
  The synchronous port path stands.
- **Self-hosting**: atomics are not required for the port. This is shipped-product
  surface hardening (safe sharing for threads that already exist), sized small on
  purpose.
