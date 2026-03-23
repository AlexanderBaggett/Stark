# Maximizing Guarantees to LLVM: A Language Designer's Reference

> **Purpose:** This document catalogs every major attribute and annotation that an LLVM
> frontend can emit to give the optimizer maximum information. If you're designing a
> programming language that targets LLVM IR, this is your checklist.

---

## Table of Contents

1. [Memory Effect Attributes](#1-memory-effect-attributes)
2. [Control Flow & Termination Guarantees](#2-control-flow--termination-guarantees)
3. [Concurrency & Synchronization](#3-concurrency--synchronization)
4. [Recursion](#4-recursion)
5. [Memory Allocation](#5-memory-allocation)
6. [Inlining & Call Behavior](#6-inlining--call-behavior)
7. [Stack Behavior & Security](#7-stack-behavior--security)
8. [Parameter-Level Attributes](#8-parameter-level-attributes)
9. [Instruction-Level Annotations](#9-instruction-level-annotations)
10. [Metadata & Aliasing Info](#10-metadata--aliasing-info)
11. [Linkage & Visibility](#11-linkage--visibility)
12. [The Ideal Function: Putting It All Together](#12-the-ideal-function-putting-it-all-together)
13. [Language Design Implications](#13-language-design-implications)
14. [Sources](#14-sources)

---

## 1. Memory Effect Attributes

These tell LLVM what a function does (or doesn't do) with memory. They are
arguably the **highest-impact** attributes for optimization because they
unlock dead store elimination, load hoisting, common subexpression elimination,
and more.

The modern syntax (LLVM 16+) uses `memory(...)` with fine-grained location kinds.

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `memory(none)` | No memory access at all. Pure computation from args → return value. (Formerly `readnone`.) | CSE, hoisting, reordering, dead call elimination |
| `memory(read)` | Reads memory but never writes. (Formerly `readonly`.) | Redundant call elimination when no intervening writes |
| `memory(write)` | Writes memory but never reads. (Formerly `writeonly`.) | Can reorder with other reads |
| `memory(argmem: read)` | Only reads memory reachable through pointer arguments. | Powerful alias analysis — global state is untouched |
| `memory(argmem: readwrite)` | Only accesses memory through pointer arguments. (Formerly `argmemonly`.) | No global side effects; safe to reorder around global accesses |
| `memory(inaccessiblemem: readwrite)` | Only accesses memory invisible to the rest of the program (e.g., allocator bookkeeping). | Safe to reorder with all normal loads/stores |
| `memory(argmem: readwrite, inaccessiblemem: readwrite)` | Combo: touches arg memory and internal bookkeeping only. This is what `malloc`-like functions get. | Allocator-aware optimizations |

### Key Insight

The `memory(...)` system is compositional. You can combine location kinds
(`argmem`, `inaccessiblemem`, `other`) with access kinds (`none`, `read`,
`write`, `readwrite`) to express exactly what your function does. The more
restrictive you can be, the better.

---

## 2. Control Flow & Termination Guarantees

These tell LLVM about the function's control flow behavior — whether it can
throw, whether it returns, and whether it makes forward progress.

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `nounwind` | Never unwinds via exception. | Convert `invoke` → `call`, simplify landing pads, enable many transforms blocked by potential exception edges |
| `noreturn` | Never returns (e.g., `exit()`, `abort()`). | Dead code elimination after call, stack frame optimization |
| `willreturn` | Always eventually returns (no infinite loops, no calls to `exit`). | Combined with `memory(none)`, makes a function *total and pure* — can be deleted entirely if result is unused |
| `mustprogress` | Guaranteed forward progress: will return, unwind, or have observable side effects in finite time. | LLVM can delete infinite loops with no side effects. Critical for loop optimization. |

### Why `mustprogress` exists

C++ (11+) guarantees forward progress for all functions. C does not. Rust does not
(infinite loops are legal). This attribute was added because without it, LLVM must
conservatively assume infinite loops are intentional and cannot optimize them away,
even if they have no side effects. If your language guarantees forward progress,
always emit this.

### The "total pure function" combo

A function with `willreturn nounwind memory(none)` is a total pure function.
LLVM can:
- Delete calls whose results are unused
- Freely reorder calls
- CSE identical calls
- Hoist calls out of loops
- Speculate calls into branches

This is the gold standard.

---

## 3. Concurrency & Synchronization

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `nosync` | Does not synchronize with other threads. No non-monotonic atomics, no volatile accesses, no convergent calls. | Reordering across potential synchronization points, hoisting from loops |

### Note

`nosync` does *not* mean "not thread-safe." It means the function doesn't
perform any operations that establish happens-before relationships with other
threads. A function that only reads shared immutable data is `nosync` even
though it's used in a multithreaded context.

---

## 4. Recursion

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `norecurse` | Does not call itself, directly or indirectly. | Interprocedural analysis can reason about the call graph more precisely |

---

## 5. Memory Allocation

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `nofree` | Does not free any memory (doesn't call `free` or equivalent). | Alias analysis, memory lifetime reasoning — safe to assume allocations survive the call |

---

## 6. Inlining & Call Behavior

| Attribute | Meaning | Effect |
|---|---|---|
| `alwaysinline` | Strong request to always inline. | Overrides cost model |
| `noinline` | Never inline. | Useful for debugging, ensuring stack frames exist |
| `inlinehint` | Bias toward inlining. | Lowers the inlining threshold |
| `optnone` | Disable all optimizations on this function. | Debugging, ensuring predictable codegen |
| `cold` | Rarely called. | Affects code layout (moved to cold section), reduces inlining priority |
| `hot` | Frequently called. | Increases inlining priority, favorable code placement |
| `minsize` | Optimize aggressively for code size. | May use slower but smaller instruction sequences |
| `optsize` | Optimize for code size, but not as aggressively as `minsize`. | Balanced size/speed tradeoff |

---

## 7. Stack Behavior & Security

| Attribute | Meaning |
|---|---|
| `naked` | No function prologue/epilogue generated. For hand-written asm. |
| `ssp` | Stack smashing protector (standard). |
| `sspreq` | Stack smashing protector (required — always emit). |
| `sspstrong` | Stack smashing protector (strong — protects more variables). |
| `safestack` | Use SafeStack instrumentation (separate stack for unsafe buffers). |
| `uwtable` | Always emit an unwind table entry, even if the function is `nounwind`. Needed for backtraces on some ABIs. |

---

## 8. Parameter-Level Attributes

These are per-argument (or per-return-value) and are equally critical for optimization.

### Pointer Attributes

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `noalias` | This pointer doesn't alias any other accessible pointer. Like C's `restrict`. | **Massive** impact on alias analysis, load/store optimization |
| `nonnull` | This pointer is never null. | Branch elimination (null checks), dereferenceable reasoning |
| `dereferenceable(N)` | Points to at least N valid bytes. | Speculative loads, hoisting loads above null checks |
| `dereferenceable_or_null(N)` | Either null, or dereferenceable for N bytes. | Combined null-check + load optimization |
| `align(N)` | At least N-byte alignment. | Enables aligned vector loads/stores, better codegen |
| `nocapture` | Pointer is not stored anywhere that outlives the call. | Enables stack promotion of heap allocations passed to the function |
| `nofree` | This pointer won't be freed by the function. | Memory lifetime reasoning |
| `readonly` | The function doesn't write through this pointer. | Load elimination, hoisting |
| `readnone` | The function doesn't read or write through this pointer. | Full reordering freedom for accesses through this pointer |
| `writeonly` | The function only writes through this pointer, never reads. | Dead store optimization |

### Value Attributes

| Attribute | Meaning | Optimization Unlocked |
|---|---|---|
| `noundef` | Value is never `undef` or `poison`. | Enables reasoning that depends on well-defined values |
| `signext` | Integer is sign-extended to register width (ABI). | Codegen optimization |
| `zeroext` | Integer is zero-extended to register width (ABI). | Codegen optimization, sometimes folds into loads |
| `returned` | This argument is returned as the function's return value. | Copy elision, tail call optimization |
| `swiftself` / `swifterror` | Swift calling convention support. | Register allocation optimization for Swift-like languages |

---

## 9. Instruction-Level Annotations

Beyond function/parameter attributes, your frontend should emit these on individual instructions.

### Arithmetic Flags

| Flag | Applies To | Meaning | Impact |
|---|---|---|---|
| `nsw` (no signed wrap) | `add`, `sub`, `mul`, `shl` | Signed overflow is undefined behavior. | **Huge** for loop optimization — enables induction variable analysis, strength reduction, trip count computation |
| `nuw` (no unsigned wrap) | `add`, `sub`, `mul`, `shl` | Unsigned overflow is undefined behavior. | Similar to `nsw` for unsigned loops |
| `exact` | `udiv`, `sdiv`, `lshr`, `ashr` | Division/shift is exact (no remainder/bits lost). | Enables algebraic simplification |
| `inbounds` | `getelementptr` | Pointer arithmetic stays within the allocated object. | Alias analysis disambiguation |

### Fast-Math Flags (Floating Point)

| Flag | Meaning |
|---|---|
| `nnan` | No NaNs — assume inputs and results are never NaN. |
| `ninf` | No infinities. |
| `nsz` | No signed zeros — treat +0 and -0 as equivalent. |
| `arcp` | Allow reciprocal — `x / y` can become `x * (1/y)`. |
| `contract` | Allow fused multiply-add. |
| `afn` | Approximate function — allow approximate math library implementations. |
| `reassoc` | Allow reassociation of floating-point operations. |
| `fast` | All of the above combined. |

### Note for Language Design

If your language doesn't require IEEE 754 strictness, emitting `fast` on all
floating-point operations is an enormous optimization win, especially for
numerical code. Alternatively, you can pick specific flags (e.g., `nnan ninf`
but not `reassoc`) based on your language's semantics.

---

## 10. Metadata & Aliasing Info

These are attached to instructions via `!metadata` and provide additional information.

| Metadata | Meaning | Impact |
|---|---|---|
| `!tbaa` | Type-Based Alias Analysis. Tells LLVM that loads/stores of different types can't alias. | Major alias analysis improvement |
| `!tbaa.struct` | TBAA for aggregate copies. | Better memcpy optimization |
| `!noalias` / `!alias.scope` | Scoped noalias metadata. More precise than function-level `noalias`. | Fine-grained alias disambiguation |
| `!range` | Constrains the range of values a load or call can produce. | Branch elimination, overflow analysis |
| `!nonnull` | Load result is non-null. | Null check elimination |
| `!dereferenceable` | Load result is a dereferenceable pointer. | Speculative loads |
| `!invariant.load` | This load always returns the same value. | Load elimination, hoisting |
| `!invariant.group` | Group of loads/stores that refer to the same invariant value. | Devirtualization |
| `!prof` | Branch weights / profiling data. | Code layout, inlining decisions |
| `!unpredictable` | This branch is unpredictable. | Avoids branch prediction hints |
| `!llvm.loop.mustprogress` | Loop-level version of `mustprogress`. | Per-loop infinite loop elimination |

---

## 11. Linkage & Visibility

Not directly optimization attributes, but they affect what interprocedural
optimizations LLVM can do.

| Linkage | Meaning | Optimization Impact |
|---|---|---|
| `private` | Not visible outside the module. | Maximum interprocedural optimization |
| `internal` | Like `private`, but kept in symbol table. | Nearly as good as `private` |
| `linkonce_odr` | Can be merged across translation units; exactly one definition semantics. | Good for templates/generics |
| `external` | Visible to other modules, can be overridden. | Limits interprocedural optimization significantly |
| `available_externally` | Definition available for inlining but not emitted into object. | Enables cross-module inlining without code bloat |

**From the LLVM Frontend Performance Tips doc:** "For each function or global
emitted, use the most private linkage type possible. Doing so will make LLVM's
inter-procedural optimizations much more effective."

Also add `unnamed_addr` or `local_unnamed_addr` when the address of a function
or global doesn't matter — this enables merging identical functions/constants.

---

## 12. The Ideal Function: Putting It All Together

Here's what a maximally-annotated function looks like in LLVM IR:

```llvm
; A pure function that takes two non-null, non-aliasing, aligned,
; dereferenceable pointers and returns a non-null pointer.
define noundef nonnull ptr @ideal_function(
    ptr noalias nonnull nocapture readonly dereferenceable(64) align(8) %a,
    ptr noalias nonnull nocapture readonly dereferenceable(64) align(8) %b
) #0 {
entry:
  ; inbounds GEP — pointer arithmetic stays in-bounds
  %ptr = getelementptr inbounds i64, ptr %a, i64 0
  
  ; Load with TBAA and invariant metadata
  %val = load i64, ptr %ptr, align 8, !tbaa !0, !invariant.load !1
  
  ; Arithmetic with no-wrap flags
  %sum = add nsw nuw i64 %val, 42
  
  ; ...
  ret ptr %a
}

; Function attributes
attributes #0 = {
  mustprogress    ; guaranteed forward progress
  nofree          ; doesn't free memory
  norecurse       ; not recursive
  nosync          ; no synchronization
  nounwind        ; no exceptions
  willreturn      ; always terminates
  memory(argmem: read)  ; only reads through argument pointers
}
```

Every single attribute above enables specific optimizations. Remove any one of
them, and LLVM must be more conservative.

---

## 13. Language Design Implications

To be able to emit all of these guarantees, your language would benefit from:

### Type System Features

- **No null pointers** → emit `nonnull` on every pointer/reference
- **Ownership/borrowing system** (à la Rust) → emit `noalias` on mutable references, `nocapture` on borrows
- **Immutability by default** → emit `readonly` parameters, `memory(read)` or `memory(none)` functions
- **Value types / no interior mutability** → emit `memory(argmem: read)` more often
- **Known sizes** → emit `dereferenceable(N)` and `align(N)`

### Control Flow Features

- **No exceptions** → emit `nounwind` on every function
- **No unstructured control flow** → easier to emit `mustprogress`
- **Guaranteed termination** (e.g., total functions) → emit `willreturn`
- **No implicit synchronization** → emit `nosync` broadly

### Memory Model Features

- **No global mutable state** → emit `memory(argmem: ...)` broadly
- **No pointer aliasing** → emit `noalias` broadly
- **No freeing borrowed memory** → emit `nofree` on parameters
- **Strict aliasing / no type punning** → emit rich `!tbaa` metadata

### Arithmetic Features

- **Overflow is UB or checked** → emit `nsw`/`nuw` on all arithmetic
- **No NaN/Inf in normal code** → emit fast-math flags
- **Arrays always bounds-checked at a higher level** → emit `inbounds` on GEPs

### Module Features

- **No dynamic linking by default** → use `private` or `internal` linkage
- **No address-taken functions** → use `unnamed_addr`
- **Whole-program compilation** → use `internal` linkage + LTO

### The Dream Combination

A function in a language with all the above features would emit almost *every*
optimization attribute on *every* function. This is the theoretical maximum
amount of information you can feed to LLVM. Each attribute you can't emit is a
lost optimization opportunity.

---

## 14. Sources

- [LLVM Language Reference Manual — Function Attributes](https://llvm.org/docs/LangRef.html#function-attributes)
- [LLVM Language Reference Manual — Parameter Attributes](https://llvm.org/docs/LangRef.html#parameter-attributes)
- [Performance Tips for Frontend Authors](https://llvm.org/docs/Frontend/PerformanceTips.html)
- [LLVM `mustprogress` design discussion (D86233)](https://reviews.llvm.org/D86233)
- [LLVM `nosync` semantics (LangRef.rst)](https://github.com/llvm-mirror/llvm/blob/master/docs/LangRef.rst)

---

*Document generated February 2026. Based on LLVM 23.0.0git documentation.*
