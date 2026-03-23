# Maximizing Pointer Optimizations in LLVM: A Language Designer's Reference

> **Purpose:** This document catalogs every mechanism LLVM provides for reasoning
> about pointers — aliasing, capture, provenance, validity, and memory access.
> If you're designing a language that targets LLVM IR and want the optimizer to
> produce the best possible code around pointer operations, this is your guide.

---

## Table of Contents

1. [How LLVM Reasons About Pointers](#1-how-llvm-reasons-about-pointers)
2. [Alias Analysis: The Foundation](#2-alias-analysis-the-foundation)
3. [Pointer Parameter Attributes](#3-pointer-parameter-attributes)
4. [The `captures` Attribute (Modern Capture Tracking)](#4-the-captures-attribute-modern-capture-tracking)
5. [Type-Based Alias Analysis (TBAA)](#5-type-based-alias-analysis-tbaa)
6. [Scoped Noalias Metadata](#6-scoped-noalias-metadata)
7. [GEP Flags: `inbounds`, `nusw`, `nuw`](#7-gep-flags-inbounds-nusw-nuw)
8. [Memory Lifetime & Invariance](#8-memory-lifetime--invariance)
9. [Pointer Provenance & `ptrtoint`/`inttoptr`](#9-pointer-provenance--ptrtointinttoptr)
10. [Non-Integral & Opaque Pointers](#10-non-integral--opaque-pointers)
11. [Volatile, Atomic, and Ordering](#11-volatile-atomic-and-ordering)
12. [What BasicAA Already Knows](#12-what-basicaa-already-knows)
13. [The Alias Analysis Pipeline](#13-the-alias-analysis-pipeline)
14. [Optimizations Unlocked by Alias Analysis](#14-optimizations-unlocked-by-alias-analysis)
15. [Language Design Implications](#15-language-design-implications)
16. [Sources](#16-sources)

---

## 1. How LLVM Reasons About Pointers

LLVM's memory model is fundamentally **untyped** — memory itself has no type.
Pointers are opaque (`ptr`) since LLVM 15+. All type information about what a
pointer points to must be communicated through **metadata**, **attributes**, and
**instruction flags**.

This is both a challenge and an opportunity for language designers. LLVM will
**not** assume anything about pointer aliasing, validity, or lifetime unless
you tell it explicitly. The more you tell it, the better it optimizes.

LLVM represents memory accesses as a **(pointer, size)** pair. Two memory accesses
alias if and only if their (pointer, size) ranges overlap. The alias analysis
infrastructure produces one of four results:

| Result | Meaning |
|---|---|
| `NoAlias` | The two memory ranges never overlap. Safe to reorder freely. |
| `MayAlias` | They might overlap. Must be conservative. |
| `PartialAlias` | They are known to overlap, but not perfectly. |
| `MustAlias` | They always refer to exactly the same starting address. |

Everything in this document ultimately feeds into producing more `NoAlias` and
`MustAlias` results and fewer `MayAlias` results.

---

## 2. Alias Analysis: The Foundation

LLVM chains multiple alias analysis implementations together. Each one gets a
chance to provide a definitive answer; if it can't, the query passes to the next.
The key implementations are:

### BasicAA (`-basic-aa`)

The workhorse. It knows many structural facts without any metadata at all:

- **Distinct allocations never alias:** Globals, stack allocations (`alloca`),
  and heap allocations (`malloc`/`calloc`/`new` return values) are all
  distinct from each other and from null.
- **Different struct fields don't alias:** If two GEPs index into different
  fields of the same struct, they're `NoAlias`.
- **Statically different array indices don't alias:** `a[0]` and `a[1]`
  are `NoAlias` when the access sizes don't overlap.
- **Non-escaping allocas are function-local:** If a stack allocation never
  escapes the function, no external call can modify it.
- **Known library functions:** `sin`, `cos`, `abs`, etc. are known to not
  access memory.

### TBAA (`-tbaa`)

Uses `!tbaa` metadata to determine that accesses through different types
cannot alias. See [Section 5](#5-type-based-alias-analysis-tbaa).

### Globals ModRef (`-globalsmodref-aa`)

Tracks which functions read/write which global variables. If a global's
address is never taken, only functions that directly reference it can
access it.

### SCEV-AA (`-scev-aa`)

Uses ScalarEvolution (loop induction variable analysis) to reason about GEP
offsets in loops. Can prove that `a[i]` and `a[i+1]` don't alias even when
`i` is a loop variable.

### Scoped NoAlias

Uses `!noalias` and `!alias.scope` metadata for fine-grained, instruction-level
alias disambiguation. See [Section 6](#6-scoped-noalias-metadata).

---

## 3. Pointer Parameter Attributes

These are the attributes you can place on pointer-typed function parameters
and return values. They are the single most impactful thing a frontend can
provide.

### Aliasing Attributes

| Attribute | Meaning | Optimization Impact |
|---|---|---|
| `noalias` | Memory accessed through this pointer does not alias memory accessed through any other pointer argument (for the duration of the call). Like C99 `restrict`. | **Massive.** Enables load/store reordering, redundant load elimination, vectorization across pointer args. The single most important pointer optimization attribute. |
| `noalias` (on return) | The returned pointer points to freshly allocated memory, disjoint from everything else. Like `malloc`. | Enables the optimizer to treat the returned pointer as unique. Dead store elimination, allocation merging. |

### Validity Attributes

| Attribute | Meaning | Optimization Impact |
|---|---|---|
| `nonnull` | This pointer is never null. | Eliminates null checks. Combined with `inbounds` GEP, proves non-nullness propagates through pointer arithmetic. |
| `dereferenceable(N)` | The pointer is valid for loading at least N bytes. | Enables speculative loads — LLVM can hoist loads above branches because it knows the load won't fault. Critical for loop optimization. |
| `dereferenceable_or_null(N)` | Either null, or dereferenceable for N bytes. | Enables a single null check followed by unconditional loads. |
| `align(N)` | The pointer has at least N-byte alignment. | Enables aligned SIMD/vector loads and stores. Can make the difference between one vector instruction and a slow unaligned load sequence. |
| `noundef` | The pointer value itself is not `undef` or `poison`. | Ensures well-defined comparison results and prevents poison propagation. |

### Memory Access Attributes (per-pointer)

| Attribute | Meaning | Optimization Impact |
|---|---|---|
| `readonly` | The function does not write through this pointer. | Load from this pointer after the call can reuse the value from before the call. |
| `readnone` | The function does not read or write through this pointer. | Full freedom to reorder accesses through this pointer around the call. |
| `writeonly` | The function only writes through this pointer. | Dead store elimination — stores before the call that go through this pointer may be dead. |

### Lifetime & Escape Attributes

| Attribute | Meaning | Optimization Impact |
|---|---|---|
| `nocapture` | The pointer is not stored anywhere that outlives the call. (Legacy; see `captures` below.) | **Huge.** Enables promotion of heap allocations to stack (`alloca`), since the pointer doesn't escape. Enables function-local reasoning. |
| `nofree` | The function does not free this pointer. | Memory is guaranteed to still be valid after the call. Enables hoisting loads across calls. |

---

## 4. The `captures` Attribute (Modern Capture Tracking)

LLVM recently (LLVM 20+) introduced the `captures(...)` attribute as a
more precise replacement for `nocapture`. It distinguishes between different
**components** of a pointer that might be captured:

### Capture Components

| Component | Meaning |
|---|---|
| `address` | The integral address (numerical value) of the pointer. |
| `address_is_null` | Only whether the address is null (subset of `address`). |
| `provenance` | The ability to read **and write** through the pointer after the function returns. |
| `read_provenance` | The ability to **read** (but not write) through the pointer after the function returns (subset of `provenance`). |

### Capture Locations

Captures can be restricted to specific locations:

| Location | Meaning |
|---|---|
| `ret` | Captured only through the return value. |
| (default) | Captured through any means other than the return value. |

### Examples

```llvm
; Pointer not captured at all (equivalent to old nocapture)
captures(none)

; Address leaks (e.g., printed or hashed), but pointer provenance doesn't escape
; — the function can't use the pointer to access memory after it returns
captures(address)

; Pointer escapes through return value only (e.g., a function that selects
; between two input pointers)
captures(ret: address, provenance)

; Only whether the pointer is null escapes (e.g., a null-check helper)
captures(address_is_null)

; Read-only provenance escapes — function may store the pointer for later
; reads, but not writes
captures(address, read_provenance)
```

### Why This Matters for Language Design

If your language has an ownership system, you can express very precise capture
semantics. For example, a "borrow" that allows reading but not storing the
pointer could use `captures(address, read_provenance)`. A pure function that
returns one of its arguments could use `captures(ret: address, provenance)`.

---

## 5. Type-Based Alias Analysis (TBAA)

TBAA is how you tell LLVM that accesses through different types cannot alias.
This is **not** built into LLVM IR by default — LLVM's memory is untyped, and
without `!tbaa` metadata, LLVM must assume that any pointer could alias any
other pointer.

### The TBAA Type Tree

TBAA metadata is organized as a tree (or DAG). The root represents "any memory."
Children represent increasingly specific types. Two accesses can only alias if
their types have an ancestor/descendant relationship in the tree.

**C/C++ example tree:**
```
Root ("Simple C/C++ TBAA")
└── char (omnipotent — aliases everything, per C standard)
    ├── int
    ├── float
    ├── any pointer
    ├── short
    └── long long
```

The `char` type is "omnipotent" in C because `char*` can alias anything
(per the C aliasing rules). This is a C-specific limitation. **A language
without this rule can have a much more powerful TBAA tree.**

### Struct-Path TBAA

The enhanced form of TBAA also encodes struct field offsets. This allows LLVM
to know that `mystruct.field_a` and `mystruct.field_b` don't alias even when
accessed through the same base pointer with different offsets.

```
MyStruct (struct type node)
├── field "x" at offset 0: int
├── field "y" at offset 4: float
└── field "z" at offset 8: pointer
```

### Attaching TBAA to Instructions

TBAA metadata is attached to individual `load` and `store` instructions:

```llvm
%val = load i32, ptr %p, align 4, !tbaa !5
store float 3.14, ptr %q, align 4, !tbaa !7
```

If `!5` (int access) and `!7` (float access) are not in an ancestor/descendant
relationship, LLVM knows these accesses cannot alias, even if `%p` and `%q`
might point to the same address.

### Impact

TBAA is extremely powerful for languages with strict type systems. If your
language guarantees that an `int*` and a `float*` never point to the same memory,
TBAA lets LLVM:

- Reorder loads and stores of different types freely
- Eliminate redundant loads across stores of different types
- Vectorize loops that access multiple typed arrays
- Hoist typed loads out of loops

### Language Design Implication

**If your language has no type punning / no union types / no `reinterpret_cast`,
you can build a TBAA tree where every type is fully disjoint.** This is
dramatically more powerful than C's TBAA (which must account for `char*` aliasing
everything and `union` type punning).

---

## 6. Scoped Noalias Metadata

While `noalias` on function parameters tells LLVM about aliasing at function
boundaries, **scoped noalias metadata** (`!noalias` and `!alias.scope`) provides
instruction-level aliasing information that survives inlining.

### How It Works

1. You define **alias scopes** — named domains of memory.
2. You attach `!alias.scope` to memory instructions to say "this access is in
   scope X."
3. You attach `!noalias` to memory instructions to say "this access does NOT
   alias anything in scope Y."

```llvm
; This load is in scope !10, and does not alias anything in scope !11
%val = load i32, ptr %p, !alias.scope !10, !noalias !11

; This store is in scope !11, and does not alias anything in scope !10
store i32 42, ptr %q, !alias.scope !11, !noalias !10
```

### Why It Exists

When a function with `noalias` parameters is inlined, the parameter-level
`noalias` disappears (because there's no longer a function boundary). LLVM's
inliner converts `noalias` parameter attributes into scoped noalias metadata
on the inlined instructions so the aliasing information is preserved.

**For a frontend**, you can also emit this metadata directly to express aliasing
facts that don't fit into the parameter attribute model. For example, if your
language can prove that two heap regions are disjoint, you can express this even
within a single function.

---

## 7. GEP Flags: `inbounds`, `nusw`, `nuw`

The `getelementptr` (GEP) instruction computes pointer offsets. It has three
flags that provide guarantees about the arithmetic:

### `inbounds`

The strongest flag. It implies `nusw` and additionally guarantees:

- The base pointer is within (or one past the end of) an allocated object.
- The result pointer is also within (or one past the end of) the same object.
- If any index is non-zero, the base pointer is not null (in address space 0).

**Optimization impact:**
- Alias analysis can use the "same allocated object" guarantee to prove that
  GEPs from different base allocations never produce aliasing pointers.
- Combined with `nonnull`, eliminates null checks.
- Helps SCEV-AA reason about array accesses in loops.

### `nusw` (No Unsigned Signed Wrap)

Added in LLVM 18 (2024). Guarantees:

- Index truncation to pointer index type preserves the signed value.
- Index × element size does not overflow in a signed sense.
- The successive addition of offsets does not overflow in a signed sense.

**Optimization impact:** Enables signed arithmetic reasoning about pointer
offsets, particularly important for loop induction variables.

### `nuw` (No Unsigned Wrap)

Also added in LLVM 18. Guarantees:

- Index truncation preserves the unsigned value.
- Index × element size does not overflow unsigned.
- The successive addition of offsets (including the base address interpreted as
  unsigned) does not overflow unsigned.

**Optimization impact:** This is particularly valuable because it lets LLVM
optimize pointer overflow checks. The pattern `ptr + offset >= ptr` (which C
programmers sometimes write to detect overflow) can be optimized away because
`nuw` guarantees it's always true.

### Relationship Between Flags

```
inbounds  ⟹  nusw  (inbounds implies nusw)
nuw and nusw are independent of each other
inbounds and nuw are independent
```

### Best Practice for Frontends

If your language guarantees that all pointer arithmetic stays within allocated
objects (e.g., you have bounds checking or an ownership system), **always emit
`inbounds` on GEPs**. If you can additionally guarantee the offsets are
non-negative (e.g., array indexing with unsigned indices), also add `nuw`.

---

## 8. Memory Lifetime & Invariance

These intrinsics and metadata tell LLVM about when memory is valid and when
its contents are known to be constant.

### Lifetime Markers

```llvm
call void @llvm.lifetime.start.p0(i64 <size>, ptr <alloca>)
; ... alloca is live and valid here ...
call void @llvm.lifetime.end.p0(i64 <size>, ptr <alloca>)
```

**Impact:**
- Enables stack coloring — multiple allocas with non-overlapping lifetimes
  can share the same stack slot, reducing stack frame size.
- Dead store elimination — a store to memory whose lifetime has ended is dead.
- Alias analysis — accesses to dead memory don't alias accesses to live memory.

### Invariant Markers

```llvm
%token = call ptr @llvm.invariant.start.p0(i64 <size>, ptr <mem>)
; ... memory at <mem> is guaranteed constant for <size> bytes ...
call void @llvm.invariant.end.p0(ptr %token, i64 <size>, ptr <mem>)
```

**Impact:** All loads from the invariant region can be CSE'd (common
subexpression eliminated) and hoisted out of loops.

### `!invariant.load` Metadata

```llvm
%val = load i32, ptr %p, !invariant.load !{}
```

Declares that this load always produces the same value, regardless of what
stores have occurred. The loaded memory is assumed to never be written after
initialization.

**Impact:** The load can be freely hoisted, CSE'd, or speculated. Extremely
powerful for language features like final/const fields, frozen values, or
immutable data.

### `!invariant.group` Metadata

Used to express that a set of loads/stores always refer to the same "virtual"
value, even if the pointer changes. Primary use case: C++ devirtualization
(the vtable pointer doesn't change for the lifetime of an object).

---

## 9. Pointer Provenance & `ptrtoint`/`inttoptr`

LLVM has a concept of pointer **provenance** — the idea that a pointer carries
not just an address but also information about *which allocation* it can
legitimately access.

### The Problem with `ptrtoint`/`inttoptr`

```llvm
%addr = ptrtoint ptr %p to i64      ; Loses provenance!
%q = inttoptr i64 %addr to ptr      ; Creates pointer with unknown provenance
```

Converting a pointer to an integer and back **destroys alias analysis
information**. LLVM cannot track which allocation `%q` refers to, so it must
conservatively assume `%q` could alias anything.

### Best Practices

From the LLVM Performance Tips for Frontend Authors:

- **Prefer GEPs over `ptrtoint`/`inttoptr`** for pointer arithmetic.
- **Prefer globals over `inttoptr` of constant addresses** — globals carry
  dereferencability information.
- Use `ptrtoint` only when you genuinely need to reason about the pointer as
  an integer (e.g., hashing, alignment checks).

### For Language Design

If your language can avoid ever converting pointers to integers (or restrict it
to well-defined cases like alignment checking), you preserve maximum provenance
information. Languages like Rust use `ptr.add()` instead of integer arithmetic
on pointers, which maps directly to `getelementptr` and preserves provenance.

### Future: `captures` on `ptrtoint`

There's ongoing work to add capture information to `ptrtoint` instructions,
which would allow LLVM to understand that "yes, the address leaked, but the
provenance (ability to access memory) didn't." This is relevant for languages
where you can print a pointer address but can't reconstruct a valid pointer
from an integer.

---

## 10. Non-Integral & Opaque Pointers

### Opaque Pointers (LLVM 15+)

All pointers in LLVM IR are now `ptr` (opaque) — there are no more typed pointers
like `i32*` or `float*`. This means:

- Type information about what's behind a pointer is carried entirely by
  the instructions that use it and by TBAA metadata.
- Two pointers to "different types" are the same LLVM type (`ptr`), so **you
  must use TBAA to distinguish them.**

### Non-Integral Pointers

LLVM allows address spaces to be marked as "non-integral" in the data layout:

```
datalayout = "...-ni:1-..."  ; Address space 1 is non-integral
```

For non-integral pointers:
- `ptrtoint` is ill-typed (compile error, not UB).
- `inttoptr` is ill-typed.
- The bit-pattern of the pointer may be unstable or contain metadata.

**Use case for language design:** If your pointers carry metadata (e.g., bounds
checking information, garbage collector tags, or capability bits), declare them
in a non-integral address space. This prevents the optimizer from accidentally
losing the metadata through integer round-trips.

---

## 11. Volatile, Atomic, and Ordering

These affect what the optimizer can do with pointer-based memory accesses.

### Volatile

```llvm
%val = load volatile i32, ptr %p
store volatile i32 42, ptr %p
```

- Volatile accesses cannot be eliminated, reordered with other volatile
  accesses, merged, or split.
- They *can* be reordered relative to non-volatile accesses.
- This is **not** Java's `volatile` — it provides no cross-thread
  synchronization.
- Volatile accesses cannot be converted to `memcpy`/`memmove`.

**Language design:** Only use for memory-mapped I/O or signal handlers. Don't
use for thread synchronization.

### Atomic Operations

```llvm
%val = load atomic i32, ptr %p seq_cst, align 4
store atomic i32 42, ptr %p release, align 4
%old = atomicrmw add ptr %p, i32 1 acq_rel
%res = cmpxchg ptr %p, i32 0, i32 1 seq_cst monotonic
```

**Impact on optimization:**
- Atomic operations are hard for the optimizer. They act as optimization
  barriers to varying degrees depending on the ordering.
- `monotonic` and `unordered` are the least restrictive — they allow most
  reorderings except elimination.
- `seq_cst` is the most restrictive — it's a full memory fence.

**Language design recommendation (from LLVM Frontend Performance Tips):** "Be
wary of ordered and atomic memory operations. They are hard to optimize and
may not be well optimized by the current optimizer. Depending on your source
language, you may consider using fences instead."

If your language uses garbage collection or reference counting, prefer using
`fence` instructions at strategic points rather than making every reference
count update `seq_cst`.

---

## 12. What BasicAA Already Knows

Even without any frontend-provided metadata, LLVM's BasicAA can prove many
things. Understanding what it already knows helps you focus your effort on
what it *can't* infer:

| Fact | How BasicAA Knows |
|---|---|
| Different `alloca`s don't alias | Distinct stack slots |
| `alloca` doesn't alias globals | Different memory regions |
| `alloca` doesn't alias `malloc` results | Different allocation origins |
| `malloc` results don't alias each other | Each `malloc` returns unique memory |
| Null doesn't alias anything (dereferenceable) | Null can't overlap valid memory |
| `gep %p, 0` vs `gep %p, 4` with 1-byte accesses | Statically different offsets |
| Non-escaping allocas are function-local | Capture tracking within the function |
| Pure/const functions don't modify memory | `memory(none)` / `memory(read)` |

### What BasicAA **Cannot** Know Without Help

| Fact | What You Need to Provide |
|---|---|
| Two pointer arguments don't alias | `noalias` attribute |
| A pointer points to valid memory | `dereferenceable(N)` |
| A pointer is not null | `nonnull` |
| An `int*` and `float*` don't alias | `!tbaa` metadata |
| A pointer stays within its allocation | `inbounds` on GEP |
| A pointer isn't stored for later use | `captures(none)` / `nocapture` |
| Memory contents don't change | `!invariant.load`, `readonly` |
| The function doesn't free the pointer | `nofree` |

---

## 13. The Alias Analysis Pipeline

LLVM chains its alias analyses together. A typical pipeline looks like:

```
Query → TBAA → Scoped NoAlias → BasicAA → Globals ModRef → SCEV-AA
```

If any analysis returns `NoAlias` or `MustAlias`, the query is resolved.
`MayAlias` means "I don't know, ask the next one." This means:

- Providing TBAA metadata is checked early and is fast.
- BasicAA provides the baseline structural analysis.
- SCEV-AA handles the complex loop-dependent cases.

For a frontend, you should **provide information at every level**:
- `noalias`/`nonnull`/`dereferenceable`/`align` on parameters (feeds BasicAA)
- `!tbaa` on loads/stores (feeds TBAA)
- `!noalias`/`!alias.scope` for complex intra-function aliasing (feeds Scoped NoAlias)
- `inbounds` on GEPs (feeds BasicAA and SCEV-AA)

---

## 14. Optimizations Unlocked by Alias Analysis

Here's what all this pointer information actually enables:

### Load/Store Optimizations

| Optimization | What It Needs | What It Does |
|---|---|---|
| Redundant Load Elimination (GVN) | `NoAlias` between a store and a load, or `MustAlias` between two loads | Eliminates the second load of the same value |
| Dead Store Elimination (DSE) | `MustAlias` between two stores with no intervening aliasing load | Eliminates the first store |
| Store-to-Load Forwarding | `MustAlias` between a store and a subsequent load | Replaces the load with the stored value |
| Load Hoisting (LICM) | `NoAlias` between the load and all stores in the loop | Moves the load above the loop |
| Store Sinking (LICM) | `MustAlias` for all loop stores to the same location, `NoAlias` with other loop accesses | Moves the store below the loop, promoting to a register |

### Interprocedural Optimizations

| Optimization | What It Needs | What It Does |
|---|---|---|
| Argument Promotion | `noalias` + `readonly` on pointer params | Passes the loaded value by-value instead of by-pointer |
| Heap-to-Stack (Allocation Sinking) | `nocapture` / `captures(none)` on all uses | Converts `malloc` to `alloca` |
| Call Slot Optimization | `noalias` on return value | Eliminates copy from returned buffer |

### Loop Optimizations

| Optimization | What It Needs | What It Does |
|---|---|---|
| Vectorization | `NoAlias` between all pointer pairs in the loop body, or a runtime alias check | Converts scalar loop to SIMD |
| Loop Unswitching | Alias info to prove a condition is loop-invariant | Duplicates loop with condition hoisted |
| Scalar Promotion | `MustAlias` for all accesses to a location in a loop | Promotes memory location to SSA register |

---

## 15. Language Design Implications

To maximize pointer optimization in LLVM, your language benefits from:

### Aliasing Guarantees

- **Ownership/borrowing system** → Emit `noalias` on unique/mutable references.
  This is the single most impactful optimization attribute. Rust emits `noalias`
  on `&mut T` references.
- **Strict type system with no type punning** → Build a fully disjoint TBAA tree.
  Far more powerful than C's tree, which has the `char*` escape hatch.
- **No raw pointer casts between unrelated types** → TBAA remains sound for all
  accesses.
- **Separate allocation regions per type** → Scoped noalias metadata can express
  "allocations of type A never overlap with allocations of type B."

### Pointer Validity Guarantees

- **No null pointers** → `nonnull` on every pointer.
- **Known sizes for all pointed-to objects** → `dereferenceable(N)` everywhere.
- **Alignment guarantees in the type system** → `align(N)` on every pointer.
- **Bounds-checked array access** → `inbounds` on all GEPs.
- **Non-negative indexing** → `nuw` on GEPs.

### Capture/Escape Guarantees

- **Borrowing with known lifetimes** → `captures(none)` on borrows.
- **Read-only borrows** → `captures(none)` + `readonly`.
- **No pointer-to-integer conversion** → Preserves provenance for alias
  analysis. Use non-integral address spaces if needed.
- **No global mutable pointers** → Function-local pointers can't escape.
- **No free of borrowed memory** → `nofree` on borrows.

### Immutability Guarantees

- **Immutable fields** → `!invariant.load` on loads from immutable fields.
- **Frozen/const data** → `!invariant.load` + `invariant.start`/`invariant.end`.
- **Value types** → No aliasing possible (passed by copy).

### Memory Model

- **Minimize atomics** → Use `fence` instead of per-operation atomics where
  possible. Avoid `seq_cst` unless required.
- **No `volatile` for thread sync** → Use atomics with appropriate ordering.
- **Clear object lifetimes** → Emit `lifetime.start`/`lifetime.end` for all
  stack allocations.

### The Dream Pointer

A maximally-annotated pointer parameter looks like:

```llvm
ptr noalias nonnull captures(none) nofree readonly
    dereferenceable(64) align(8) noundef %p
```

With loads from it annotated:

```llvm
%val = load i32, ptr %gep, align 4, !tbaa !5,
       !invariant.load !{}, !noalias !10, !alias.scope !11,
       !nonnull !{}, !dereferenceable !{i64 8}
```

Every annotation represents information your language's type system could
provide.

---

## 16. Sources

- [LLVM Language Reference Manual — Pointer Aliasing Rules](https://llvm.org/docs/LangRef.html#pointer-aliasing-rules)
- [LLVM Language Reference Manual — Parameter Attributes (`noalias`, `captures`, etc.)](https://llvm.org/docs/LangRef.html#parameter-attributes)
- [LLVM Language Reference Manual — TBAA Metadata](https://llvm.org/docs/LangRef.html#tbaa-metadata)
- [LLVM Language Reference Manual — `noalias` and `alias.scope` Metadata](https://llvm.org/docs/LangRef.html#noalias-and-alias-scope-metadata)
- [LLVM Language Reference Manual — `getelementptr` Instruction](https://llvm.org/docs/LangRef.html#getelementptr-instruction)
- [LLVM Language Reference Manual — Pointer Capture](https://llvm.org/docs/LangRef.html#pointer-capture)
- [LLVM Alias Analysis Infrastructure](https://llvm.org/docs/AliasAnalysis.html)
- [Performance Tips for Frontend Authors](https://llvm.org/docs/Frontend/PerformanceTips.html)
- [The Often Misunderstood GEP Instruction](https://llvm.org/docs/GetElementPtr.html)
- [This Year in LLVM (2024) — GEP nusw/nuw flags](https://www.npopov.com/2025/01/05/This-year-in-LLVM-2024.html)
- [Escape Analysis & Capture Tracking in LLVM](https://jonasdevlieghere.com/post/escape-analysis-capture-tracking-in-llvm/)
- [GEP nusw and nuw flags PR #90824](https://github.com/llvm/llvm-project/pull/90824)
- [Alias Analysis in LLVM (2012 Dev Meeting)](https://llvm.org/devmtg/2012-11/Gohman-AliasAnalysis.pdf)
- [ptr_provenance and @llvm.noalias (2021 Dev Meeting)](https://llvm.org/devmtg/2021-11/slides/2021-ptr_provenanceAndLlvmNoaliasTheTaleOfFullRestrict.pdf)

---

*Document generated February 2026. Based on LLVM 23.0.0git documentation.*
