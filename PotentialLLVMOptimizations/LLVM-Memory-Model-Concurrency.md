# LLVM Optimization Research: Memory Model and Concurrency

## Overview

This document covers how a language's concurrency and threading model maps to LLVM's memory ordering infrastructure, what the performance costs are at each level, and what a language frontend should emit to maximize optimization opportunities. This is the single highest-leverage area where a language can deviate from C/C++ defaults and gain significant performance: if your language can guarantee that certain data is never shared across threads, you unlock optimizations that C/C++ compilers must conservatively forgo.

LLVM's memory model is based on C++20's memory model (with errata corrections). It defines six levels of atomicity for memory operations, from completely non-atomic to sequentially consistent. The choice of level fundamentally controls what the optimizer is allowed to do and what hardware instructions get emitted.

---

## 1. The Six Atomicity Levels

LLVM defines these levels in order of increasing strength. Each level constrains the optimizer more and potentially emits more expensive hardware instructions.

### 1.1 NotAtomic (plain `load`/`store`)

```llvm
%val = load i32, ptr %p
store i32 42, ptr %p
```

**Semantics:** No atomicity guarantees. If two threads race on this location, the load returns `undef`. This is **not** undefined behavior in the LLVM sense (unlike C/C++ where data races are UB) — the load just produces `undef`, which can propagate poison downstream.

**What the optimizer can do:**
- Full freedom: GVN, LICM, DSE, SROA, memcpyopt, store-to-load forwarding, load elimination, speculative execution — everything works
- The optimizer *can* introduce speculative loads to shared addresses
- The optimizer *cannot* introduce stores along paths where they wouldn't otherwise execute (this is the one concurrency-related restriction even for non-atomic code)

**Code generation:** Normal MOV instructions. No fences, no barriers, no special instructions on any architecture.

**Language design implication:** This is your default for everything. Every local variable, every field of a non-shared struct, every element of a non-shared array should use plain loads and stores. The more code you can keep at this level, the better your optimization.

### 1.2 Unordered (`load atomic ... unordered`)

```llvm
%val = load atomic i32, ptr %p unordered, align 4
store atomic i32 42, ptr %p unordered, align 4
```

**Semantics:** Guarantees that a load will only see values that were actually stored (never a torn/partial write). No ordering guarantees whatsoever. This matches Java's memory model for shared variables.

**What the optimizer can do:**
- `isUnordered()` returns true — LICM can hoist these out of loops
- Cannot be split into multiple instructions (no tearing)
- Cannot be narrowed (e.g., can't convert a 32-bit unordered store into an 8-bit store to a sub-field)
- Cannot be folded into memcpy/memset
- Reordering with other unordered operations is fine
- CSE and DSE work on these

**Code generation:** Typically a single normal load/store instruction on most architectures. On x86, ARM, AArch64 for naturally-aligned native-width operations: identical to NotAtomic. Expensive only for wide operations (e.g., 64-bit on 32-bit ARM without LPAE requires special handling).

**Language design implication:** This is the right default for "safe" languages that want to avoid undefined behavior on data races but don't need synchronization. If your language guarantees freedom from data races through its type system (like Rust), you never need this — use NotAtomic. If your language is Java-like with shared-by-default memory, this is your baseline for shared variables. The performance cost over NotAtomic is very small (mainly optimizer restrictions, not hardware cost).

### 1.3 Monotonic / Relaxed (`load atomic ... monotonic`)

```llvm
%val = load atomic i32, ptr %p monotonic, align 4
store atomic i32 42, ptr %p monotonic, align 4
%old = atomicrmw add ptr %p, i32 1 monotonic
```

**Semantics:** Corresponds to C++ `memory_order_relaxed`. Guarantees a total order exists on all operations to the same address, and the operation is lock-free. No cross-address ordering.

**What the optimizer can do — and critically, cannot do:**
- `isUnordered()` returns **false** — LICM will *not* hoist these
- `mayReadFromMemory()`/`mayWriteToMemory()` return true
- Alias analysis returns ModRef for the accessed address
- Treated as a "read+write to a memory location" by optimizer
- CSE/DSE possible in limited cases but rarely profitable
- Cannot be reordered with stronger atomic operations to any address

**Code generation:**
- x86: Same as NotAtomic for loads/stores (MOV). RMW operations use LOCK prefix (e.g., `LOCK XADD`)
- ARM/AArch64: Same as NotAtomic for loads/stores. RMW uses LL/SC loops (LDXR/STXR)
- No fences emitted on any architecture

**Language design implication:** Use this for atomic counters, statistics, and similar cases where you need atomicity but not ordering. The hardware cost is zero for loads/stores on x86 and ARM, but the *optimizer* cost is significant because LICM and many other transforms stop working. Only use monotonic when you actually need cross-thread visibility.

### 1.4 Acquire (`load atomic ... acquire`)

```llvm
%val = load atomic i32, ptr %p acquire, align 4
; All subsequent reads/writes are ordered after this load
```

**Semantics:** Corresponds to C++ `memory_order_acquire`. Prevents subsequent memory operations from being reordered before this load. When paired with a Release store, creates a happens-before edge.

**What the optimizer can do:**
- Treated like a nothrow call by unaware optimizers
- Stores from *before* an Acquire load can be moved *after* it
- Non-Acquire loads from *before* an Acquire load can be moved *after* it
- Nothing after the Acquire can move before it (from the optimizer's perspective)

**Code generation:**
- x86: Plain MOV (x86 has Total Store Order, so loads are inherently acquire)
- AArch64: `LDAR` (Load-Acquire) instruction, or plain LDR + DMB fence on older ARM
- ARM (pre-v8): LDR + DMB ISH (full barrier)
- RISC-V: `fence r,rw` after load, or `lr.aq` for RMW
- PowerPC: LWZ + ISYNC (or CMPW+BNE+ISYNC optimized sequence)

**Language design implication:** This is one half of the acquire-release pair used for lock acquisition and message passing. On x86, acquire loads are free. On ARM/AArch64, they cost one barrier or one special instruction. Much cheaper than seq_cst.

### 1.5 Release (`store atomic ... release`)

```llvm
store atomic i32 42, ptr %p release, align 4
; All preceding reads/writes are ordered before this store
```

**Semantics:** Corresponds to C++ `memory_order_release`. Prevents preceding memory operations from being reordered after this store.

**What the optimizer can do:**
- Treated like a nothrow call by unaware optimizers
- Loads from *after* a Release store can be moved *before* it
- Non-Release stores from *after* a Release store can be moved *before* it

**Code generation:**
- x86: Plain MOV (x86 stores are inherently release for loads on other cores; but *not* for other stores — however, the x86 model is strong enough that release stores are plain MOVs)
- AArch64: `STLR` (Store-Release)
- ARM (pre-v8): DMB ISH + STR
- RISC-V: `fence rw,w` before store

**Language design implication:** The other half of acquire-release. On x86, free. On ARM/AArch64, one special instruction or barrier. This is what you emit when unlocking a mutex or publishing data.

### 1.6 AcquireRelease (`acq_rel`)

```llvm
%old = atomicrmw add ptr %p, i32 1 acq_rel
%result = cmpxchg ptr %p, i32 0, i32 1 acq_rel monotonic
```

**Semantics:** Combines Acquire and Release. Only meaningful for read-modify-write operations.

**Code generation:** Same barriers as the stronger of Acquire and Release for the given operation.

### 1.7 SequentiallyConsistent (`seq_cst`)

```llvm
%val = load atomic i32, ptr %p seq_cst, align 4
store atomic i32 42, ptr %p seq_cst, align 4
fence seq_cst
```

**Semantics:** Corresponds to C++ `memory_order_seq_cst`. Acquire semantics for loads, Release for stores, plus a global total order on all seq_cst operations. This is the most expensive and also the easiest to reason about.

**Code generation — this is where the cost becomes significant:**
- **x86:**
  - seq_cst load: plain MOV (same as acquire)
  - seq_cst store: **XCHG** (implicit LOCK prefix) — NOT a plain MOV! This is the key cost.
  - seq_cst fence: **MFENCE** — very expensive. There's an open LLVM issue (#91731) about using `LOCK ADD [rsp], 0` instead, which is faster than MFENCE on modern x86 (GCC already does this).
  - seq_cst RMW: LOCK CMPXCHG (same as other orderings)
- **AArch64:**
  - seq_cst load: LDAR (same as acquire)
  - seq_cst store: STLR (same as release) — but may need additional barrier if followed by acquire load
  - seq_cst fence: DMB ISH (full barrier)
- **ARM (pre-v8):** DMB ISH before AND after every seq_cst operation
- **RISC-V:** Full fence before and after

**Language design implication:** seq_cst is the default in C++ `std::atomic` and is almost always stronger than necessary. If your language exposes atomics, **do not make seq_cst the default**. Acquire-release is sufficient for almost all synchronization patterns (mutexes, message passing, publish-subscribe). seq_cst is only needed when you require a global total order across *multiple* atomic variables — a very rare requirement.

---

## 2. Optimization Impact Summary by Level

| Ordering | LICM Hoist | GVN/CSE | DSE | memcpyopt | Store Introduction | x86 Load Cost | x86 Store Cost | AArch64 Load | AArch64 Store |
|----------|-----------|---------|-----|-----------|-------------------|--------------|----------------|-------------|--------------|
| NotAtomic | ✅ | ✅ | ✅ | ✅ | ✅ (loads only) | MOV | MOV | LDR | STR |
| Unordered | ✅ | ✅ | ✅ | ❌ | ❌ | MOV | MOV | LDR | STR |
| Monotonic | ❌ | Limited | Limited | ❌ | ❌ | MOV | MOV | LDR | STR |
| Acquire | ❌ | ❌ | ❌ | ❌ | ❌ | MOV | N/A | LDAR | N/A |
| Release | ❌ | ❌ | ❌ | ❌ | ❌ | N/A | MOV | N/A | STLR |
| seq_cst | ❌ | ❌ | ❌ | ❌ | ❌ | MOV | **XCHG** | LDAR | STLR* |

*AArch64 seq_cst stores may need additional fencing depending on what follows.

The critical takeaway: **the biggest jump in optimization loss is from Unordered to Monotonic**. Going from NotAtomic to Unordered loses memcpyopt and store-introduction but keeps LICM and DSE. Going to Monotonic loses LICM — and for hot loops, that's often the most important optimization.

---

## 3. The Single-Thread Optimization: `syncscope("singlethread")`

LLVM supports a `syncscope` annotation on atomic operations and fences. The most important one for language design is `syncscope("singlethread")`:

```llvm
; Normal cross-thread fence: emits DMB on ARM, MFENCE on x86
fence seq_cst

; Single-thread fence: compiler barrier only, NO hardware instructions
fence syncscope("singlethread") seq_cst

; Single-thread atomic store: may lower to plain store on many targets
store atomic i32 42, ptr %p syncscope("singlethread") release, align 4
```

**What singlethread syncscope does:**
- Fences become pure compiler barriers — they prevent the optimizer from reordering operations across the fence, but emit **zero** hardware instructions (verified: on ARM, this produces `@ COMPILER BARRIER` comment, no `dmb`)
- Atomic loads/stores with singlethread scope *should* lower to plain loads/stores + compiler barriers, though there's a known AArch64 issue (#114580) where `store atomic syncscope("singlethread") release` still emits STLR instead of plain STR

**Language design implication:** If your language has coroutines, green threads, or an async runtime where "concurrency" means interleaving on a single OS thread, use `syncscope("singlethread")`. This gives you the compiler ordering guarantees you need for signal handlers or coroutine yields without any hardware cost.

**Gotcha:** Some LLVM backends (notably RISC-V, PowerPC, SPARC) historically did not optimize singlethread fences and would emit actual hardware barriers. This has been improving but test your target.

---

## 4. The `nosync` Function Attribute

```llvm
define void @pure_compute(ptr noalias %data, i64 %len) nosync nofree {
  ; LLVM knows this function doesn't synchronize with other threads
  ; and doesn't free memory
}
```

**Semantics:** `nosync` declares that a function does not synchronize with another thread in any way — no atomics at or above Monotonic ordering, no volatile operations, no convergent calls. The function may contain `unordered` atomics and `singlethread`-scoped fences.

**What this enables:**
- The Attributor pass in LLVM can use `nosync` to reason about whether memory might become visible to other threads
- Combined with `nofree`, it proves that memory `dereferenceable` before the call remains dereferenceable after — crucial for speculative load optimization
- Enables more aggressive alias analysis: if a function is `nosync`, `noalias` pointers passed to it can't have their underlying memory accessed by another thread via synchronization

**Interaction with `noalias`:** There's a subtle and active area of development here. The `noalias` attribute on pointers was designed for C's `restrict` which predates threads. In a multi-threaded context, a `nosync` call with `noalias` parameters allows much stronger reasoning — the optimizer knows no other thread can observe the modifications. Without `nosync`, a called function might synchronize, allowing another thread to "see through" the noalias guarantee.

**Language design implication:** If your language has a concept of "non-shared" data or pure computations, mark those functions `nosync`. If your language's type system can prove a function only touches thread-local data, emit `nosync`. This is cheap to add and compounds with other attributes.

---

## 5. volatile vs. Atomic: They're Orthogonal

```llvm
; Volatile but not atomic: hardware I/O, MMIO
store volatile i32 42, ptr %mmio_reg

; Atomic but not volatile: normal concurrent access
store atomic i32 42, ptr %shared_counter monotonic, align 4

; Both: extremely rare, almost never correct
store atomic volatile i32 42, ptr %p seq_cst, align 4
```

**Key facts:**
- `volatile` guarantees the operation won't be eliminated or reordered with other volatile operations — but it provides **no** cross-thread synchronization
- Volatile loads/stores can be freely reordered with non-volatile operations
- An `Acquire` load cannot be reordered with subsequent non-volatile loads, but a volatile load *can* be
- `volatile` prevents: elimination, merging, reordering among volatiles
- `volatile` does NOT prevent: reordering with non-volatiles, speculative execution around it

**Language design implication:** Don't use `volatile` for concurrency. If your language has a `volatile` keyword, it should map to LLVM `volatile` (for hardware I/O), not to atomics. For concurrent access, always use `load atomic`/`store atomic` with the appropriate ordering. If someone wants Java-style `volatile` (which means sequential consistency), emit `load atomic ... seq_cst`/`store atomic ... seq_cst`.

---

## 6. Fences

```llvm
fence acquire          ; Prevents subsequent loads/stores from moving before this point
fence release          ; Prevents preceding loads/stores from moving after this point
fence acq_rel          ; Both
fence seq_cst          ; Both + total order with other seq_cst operations
fence syncscope("singlethread") release  ; Compiler-only barrier
```

**How fences interact with Monotonic operations:**
- A Monotonic load followed by an Acquire fence is roughly equivalent to an Acquire load
- A Monotonic store preceded by a Release fence is roughly equivalent to a Release store

This is useful because you can separate the fence from the memory operation, which sometimes allows better optimization (the fence only needs to appear once for multiple subsequent operations).

**Code generation:**
- x86: Only `seq_cst` fences generate code (MFENCE). All other fence orderings generate nothing because x86's TSO already provides Acquire/Release.
- AArch64: `DMB ISH` for acquire/release/seq_cst fences
- ARM: `DMB ISH` for all non-singlethread fences

**Language design implication:** If your language has explicit fence operations (e.g., for unsafe/FFI code), prefer Monotonic operations + fences over Acquire/Release operations when multiple accesses need the same ordering — it can result in fewer barriers. But this is a micro-optimization; the real wins come from avoiding atomics entirely.

---

## 7. `cmpxchg` and `atomicrmw`

```llvm
; Compare-and-swap: two orderings (success, failure)
%result = cmpxchg ptr %p, i32 %expected, i32 %new acq_rel monotonic
; Returns { i32 %old_value, i1 %succeeded }

; cmpxchg weak: may spuriously fail (useful in loops, can be cheaper)
%result = cmpxchg weak ptr %p, i32 %expected, i32 %new acq_rel monotonic

; Atomic read-modify-write
%old = atomicrmw add ptr %counter, i32 1 monotonic
%old = atomicrmw xchg ptr %flag, i32 1 acquire
%old = atomicrmw max ptr %max_seen, i32 %val monotonic
```

**Operations available for `atomicrmw`:** `xchg`, `add`, `sub`, `and`, `nand`, `or`, `xor`, `max`, `min`, `umax`, `umin`, `fadd`, `fsub`, `fmax`, `fmin`, `inc`, `dec`

**Code generation on x86:**
- `atomicrmw xchg` → `XCHG`
- `atomicrmw add/sub` → `LOCK XADD`
- All others → `LOCK CMPXCHG` loop
- Some (`and`, `or`, `xor`) may use `LOCK AND`/`LOCK OR`/`LOCK XOR` if the old value isn't used

**`cmpxchg weak` vs. `cmpxchg`:** On LL/SC architectures (ARM, RISC-V, PowerPC), `weak` allows the LL/SC to fail spuriously, avoiding the loop-until-success that `strong` requires. On x86, `weak` and `strong` are identical (LOCK CMPXCHG never spuriously fails). Always prefer `weak` in loops.

**Language design implication:** If your language has compare-and-swap, expose both `weak` and `strong` variants. For `atomicrmw`, the `acq_rel` ordering for RMW in lock/unlock patterns is almost always sufficient — avoid defaulting to `seq_cst`.

---

## 8. Concrete Language Design Recommendations

### Recommendation 1 (CRITICAL): Prove Data is Unshared — Use NotAtomic Everywhere You Can

**Impact: Very High**

The single most important optimization is avoiding atomics entirely. Every load and store that can be proven thread-local should be plain `load`/`store`. Your type system should help:

- **Value types / stack allocations:** Always NotAtomic. Stack data is inherently thread-local (unless its address escapes to another thread).
- **Freshly allocated heap data:** NotAtomic until published to another thread.
- **Data protected by a mutex:** NotAtomic between lock acquire and release. The lock's acquire/release ordering makes the non-atomic accesses safe.
- **Data with exclusive ownership:** If your language has a Rust-like ownership system, all accesses through `&mut` references are NotAtomic.

```llvm
; What your mutex lock pattern should look like:
%locked = cmpxchg ptr %mutex, i32 0, i32 1 acquire monotonic
; ... all accesses inside critical section are plain load/store ...
%x = load i32, ptr %shared_data    ; NOT atomic — the mutex provides ordering
store i32 42, ptr %shared_data     ; NOT atomic
store atomic i32 0, ptr %mutex release, align 4
```

### Recommendation 2 (HIGH): Default to Acquire/Release, Not seq_cst

**Impact: High on non-x86 (ARM, AArch64, RISC-V), Moderate on x86**

If your language has atomic types, the default ordering should be Acquire for loads and Release for stores, not seq_cst. Almost all correct concurrent algorithms only need acquire-release. seq_cst is only needed for:
- Dekker/Peterson-style algorithms (very rare in practice)
- Patterns that require observing a total order across multiple unrelated atomics

On x86, this saves you from `XCHG` on stores (versus plain `MOV`). On AArch64, the hardware cost is similar (both use LDAR/STLR) but the *optimizer* has slightly more freedom with acquire/release. On ARM pre-v8 and RISC-V, seq_cst adds additional full fences.

### Recommendation 3 (HIGH): Mark Non-Synchronizing Functions `nosync`

**Impact: High (enables deref speculation, alias analysis)**

If your language can statically determine that a function:
- Does not access any atomic variable at Monotonic or stronger ordering
- Does not contain volatile accesses
- Does not call functions that might synchronize

Then emit `nosync` on that function. Combined with `nofree`, this enables LLVM to speculate loads across the call and maintain `dereferenceable` guarantees.

### Recommendation 4 (MEDIUM): Use `syncscope("singlethread")` for Coroutine/Async Barriers

**Impact: Medium (eliminates hardware fences in async code)**

If your language has async/await or green threads that are guaranteed to run on a single OS thread (or at least not preemptively on multiple cores), signal-fence-style barriers can use `syncscope("singlethread")` to avoid emitting any hardware instructions while still preventing compiler reordering.

### Recommendation 5 (MEDIUM): Use Unordered for "Safe but Unsynchronized" Access

**Impact: Medium (preserves LICM and DSE)**

If your language guarantees no undefined behavior on data races (like Java), use `unordered` rather than `monotonic` for the baseline shared access. Unordered preserves LICM (loop-invariant code motion), which is critical for loop performance. Monotonic kills LICM.

### Recommendation 6 (LOW-MEDIUM): Prefer `cmpxchg weak` in Loops

**Impact: Low on x86, Medium on ARM/RISC-V**

Always expose both weak and strong CAS. In retry loops, weak CAS avoids unnecessary inner loops on LL/SC architectures.

### Recommendation 7 (LOW): Use Monotonic + Fences for Batched Ordering

**Impact: Low (micro-optimization)**

When multiple accesses need the same ordering, it can be cheaper to use Monotonic operations bracketed by fences rather than individual Acquire/Release operations, since one fence covers all of them:

```llvm
fence acquire
%a = load atomic i32, ptr %x monotonic, align 4
%b = load atomic i32, ptr %y monotonic, align 4
%c = load atomic i32, ptr %z monotonic, align 4
; One fence instead of three LDAR instructions on AArch64
```

---

## 9. Gotchas and Known Issues

1. **singlethread syncscope on atomic stores (AArch64):** As of LLVM 20+, `store atomic syncscope("singlethread") release` on AArch64 still emits `STLR` instead of a plain `STR` (issue #114580). Workaround: use a plain `store` + `fence syncscope("singlethread") release` as separate instructions.

2. **MFENCE vs. LOCK on x86:** LLVM emits `MFENCE` for `fence seq_cst`, but `LOCK ADD [RSP], 0` (or similar) is faster on modern x86 CPUs. GCC and MSVC already use the LOCK prefix approach. This is tracked as LLVM issue #91731.

3. **Backend inconsistency for singlethread fences:** Not all backends treat `syncscope("singlethread")` as a compiler-only barrier. PowerPC, RISC-V, and SPARC backends have historically emitted real hardware fences. Test on your target.

4. **noalias + threads:** The interaction between `noalias` (based on C `restrict`) and multi-threaded code is an active area of LLVM development. `restrict` was designed before C had a memory model. If your language uses `noalias` on pointers that cross synchronization boundaries, be cautious — GVN may eliminate reloads that are actually necessary after a synchronization point. The solution is to ensure functions that synchronize are not marked `nosync`.

5. **Atomic operations and SROA:** LLVM's SROA (Scalar Replacement of Aggregates) can handle atomic loads/stores from constant globals, and can do limited promotion of atomic accesses. But any atomic access to an alloca prevents full SROA promotion in most cases.

6. **Width limitations:** Atomic operations must be lock-free, and LLVM guarantees this. If the width exceeds what the hardware supports natively (e.g., 128-bit on some x86 without `cmpxchg16b`), code generation will fail. Your frontend should be aware of target-specific atomic width limits and fall back to library calls (`__atomic_*`) for wider types.

---

## 10. Impact Ranking

Ranked by optimization impact for language design:

| Rank | Recommendation | Impact |
|------|---------------|--------|
| 1 | Prove data unshared → use NotAtomic | **Very High** — unlocks all optimizations |
| 2 | Default to acquire/release, not seq_cst | **High** — cheaper hardware, more optimizer freedom |
| 3 | Emit `nosync` on non-synchronizing functions | **High** — enables speculation and alias analysis |
| 4 | Use `syncscope("singlethread")` for coroutine barriers | **Medium** — eliminates hardware fences |
| 5 | Use `unordered` (not monotonic) for safe-language baseline | **Medium** — preserves LICM |
| 6 | Prefer `cmpxchg weak` in loops | **Low-Medium** — LL/SC architecture benefit |
| 7 | Monotonic + fences for batched ordering | **Low** — micro-optimization |

The theme is clear: the less your language needs atomics, the better LLVM can optimize. A language with ownership types (like Rust) or actor-based concurrency (like Erlang/Pony) that can statically prove most data is unshared will generate dramatically faster code than one where every heap access might be shared (like Java, which must use `unordered` at minimum for all shared fields).
