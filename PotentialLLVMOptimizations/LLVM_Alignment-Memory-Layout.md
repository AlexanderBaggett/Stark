# LLVM Alignment and Memory Layout: Language Design Reference

## Overview

Alignment is one of those optimization concerns that seems trivial on the surface — "just align everything to 16 bytes" — but interacts deeply with speculative execution, loop optimization, vectorization, allocator design, and even alias analysis. For a language designer, the decisions you make about alignment permeate every level of the generated code.

The core principle: **LLVM can only optimize what it can prove is safe.** Alignment and dereferenceability information are the primary mechanisms by which LLVM proves that loads and stores are safe to reorder, hoist, vectorize, or speculatively execute. Every alignment guarantee your language provides is an opportunity LLVM can exploit.

---

## The Alignment Landscape in LLVM

LLVM has alignment information at multiple levels, and each serves a different purpose:

### 1. The Data Layout String

Every LLVM module starts with a `target datalayout` string that specifies the ABI and preferred alignment for every type on the target. For x86-64 Linux, it looks like:

```llvm
target datalayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
```

This string encodes:
- `e` — little-endian
- `p270:32:32` — pointers in address space 270 are 32-bit with 32-bit alignment
- `i64:64` — i64 has 64-bit (8-byte) ABI alignment
- `i128:128` — i128 has 128-bit (16-byte) ABI alignment
- `f80:128` — x87 80-bit floats have 128-bit (16-byte) ABI alignment
- `n8:16:32:64` — native integer widths
- `S128` — natural stack alignment is 128 bits (16 bytes)

**Language design relevance:** The data layout string is specified by the target, not by the frontend. Your frontend must emit the correct data layout for the target you're compiling for. However, understanding this string is important because it determines the *default* alignments LLVM uses when you don't specify one explicitly.

**ABI alignment vs. Preferred alignment:** The data layout string supports both. ABI alignment is the minimum required by the platform (violating it may cause hardware faults on some architectures). Preferred alignment is what LLVM tries to use when it can, for performance. On x86-64, most misaligned accesses don't fault but are slower, especially for SIMD.

### 2. The `align` Attribute on Pointers

```llvm
define void @process(ptr align 16 %data, i64 %len) {
  ...
}
```

When placed on a function parameter, `align N` tells LLVM that the pointer is guaranteed to be N-byte aligned. If it isn't, the behavior is undefined (when combined with `noundef`). This is a promise from the frontend to the optimizer.

**What this enables:**
- LLVM can emit aligned load/store instructions (e.g., `movaps` instead of `movups` on x86)
- LICM can hoist loads speculatively when alignment + dereferenceability is known
- The loop vectorizer can use aligned vector loads without generating alignment checks or peeling loops

**Critical interaction with dereferenceability:** Alignment alone does NOT imply the pointer is safe to load from. You need BOTH `align` AND `dereferenceable` for speculative loads. There is a subtle bug-prone area here: on loads/stores, `align` DOES imply dereferenceability up to the alignment boundary, but on function parameters, `align` does NOT imply dereferenceability. This distinction has caused real miscompilation bugs in LLVM.

```llvm
; align on parameter: does NOT imply dereferenceable
define void @foo(ptr align 32 %p) {
  ; LLVM cannot speculatively load from %p just because of align 32
  ; It needs: ptr align 32 dereferenceable(32) %p
  ...
}

; align on load: DOES imply dereferenceable up to alignment
; (because if the load executes, the address must be valid)
%v = load i32, ptr %p, align 32
; This tells LLVM: if this load executes, 32 bytes starting at %p are valid
```

**Language design recommendation:** Always emit BOTH `align` AND `dereferenceable` on reference/pointer parameters. If your language guarantees that references are never null and always point to valid objects, emit `nonnull noundef dereferenceable(N) align A` where N is the object size and A is the type's alignment. This quadruple is the maximum information you can provide.

### 3. The `align` on Load/Store Instructions

```llvm
%v = load <4 x float>, ptr %p, align 16    ; aligned SIMD load
store <4 x float> %v, ptr %q, align 16     ; aligned SIMD store

%v2 = load <4 x float>, ptr %p, align 4    ; unaligned — may use movups
```

The alignment on a load/store instruction is a contract: "the address IS aligned to this value, or the behavior is undefined." The backend uses this to select between aligned and unaligned memory instructions.

**x86 specifics:** On modern x86, aligned vs. unaligned SSE/AVX loads have minimal performance difference on naturally-aligned data. However, when crossing cache line boundaries (64 bytes), unaligned loads can be significantly slower. For AVX-512 (64-byte vectors), 64-byte alignment matters more.

**ARM/AArch64 specifics:** Some ARM implementations fault on unaligned accesses. Even those that don't fault may take multiple cycles for unaligned loads. Alignment matters more on ARM than on x86.

**MIPS specifics:** Unaligned loads/stores are not natively supported. LLVM must generate a multi-instruction sequence (shift + or) for unaligned access, which is dramatically slower.

### 4. The `dereferenceable` Attribute

```llvm
define void @process(ptr dereferenceable(64) %arr) {
  ; LLVM knows %arr points to at least 64 valid bytes
  ; It can speculatively load from %arr even in conditional code
  ...
}
```

`dereferenceable(N)` tells LLVM that N bytes starting at the pointer are safe to load from without trapping. This is independent of alignment.

**Key optimization it enables — speculative load hoisting in LICM:**

```llvm
; WITHOUT dereferenceable: LLVM cannot hoist the load
define void @loop(ptr %p, i64 %n) {
entry:
  br label %loop

loop:
  %i = phi i64 [0, %entry], [%i.next, %loop]
  ; This load is loop-invariant, but LLVM doesn't know
  ; if %p is safe to load from on the first iteration
  %v = load i32, ptr %p, align 4
  ; ... use %v ...
  %i.next = add i64 %i, 1
  %done = icmp eq i64 %i.next, %n
  br i1 %done, label %exit, label %loop

exit:
  ret void
}

; WITH dereferenceable: LLVM can hoist the load to entry
define void @loop(ptr dereferenceable(4) align 4 %p, i64 %n) {
entry:
  %v = load i32, ptr %p, align 4     ; hoisted!
  br label %loop

loop:
  %i = phi i64 [0, %entry], [%i.next, %loop]
  ; ... use %v ...
  %i.next = add i64 %i, 1
  %done = icmp eq i64 %i.next, %n
  br i1 %done, label %exit, label %loop

exit:
  ret void
}
```

This real-world LICM optimization was confirmed by LLVM bug #80616: a `dereferenceable(8)` pointer without `align 8` failed to have its load hoisted. Adding `align 8` fixed it. Both attributes were needed.

**`dereferenceable_or_null(N)`:** Weaker version that allows the pointer to be null, but if it's non-null, N bytes are dereferenceable. LLVM can use this with a null check to still enable speculative optimization:

```llvm
define void @maybe_process(ptr dereferenceable_or_null(64) %arr) {
  %is_null = icmp eq ptr %arr, null
  br i1 %is_null, label %skip, label %do_work

do_work:
  ; LLVM knows %arr is non-null here (by the branch condition)
  ; Combined with dereferenceable_or_null(64), it knows 64 bytes are safe
  %v = load <4 x float>, ptr %arr, align 16  ; can be speculated
  ...
}
```

**Language design recommendation:** If your language has non-nullable reference types, use `dereferenceable(N)`. If it has Option/Maybe types that lower to nullable pointers, use `dereferenceable_or_null(N)`. Either is vastly better than neither.

---

## Alignment and Vectorization

This is the area where alignment has the most dramatic performance impact.

### How Alignment Affects the Loop Vectorizer

When LLVM's loop vectorizer transforms scalar loops into vector loops, it needs to know the alignment of the memory accesses. The vectorizer has three strategies:

**Strategy 1: Known aligned — no overhead**
```llvm
; If LLVM knows %arr is 32-byte aligned and elements are 4-byte floats:
; It can directly use aligned 256-bit (32-byte) loads
define void @add(ptr align 32 dereferenceable(128) %arr, i64 %n) {
  ; Vectorized to: vmovaps (%rdi,%rax), %ymm0
  ;                vaddps  %ymm1, %ymm0, %ymm0
  ;                vmovaps %ymm0, (%rdi,%rax)
}
```

**Strategy 2: Unknown alignment — peeling or runtime check**
```llvm
; If alignment is unknown, the vectorizer must either:
; (a) Peel iterations until alignment is reached
; (b) Use unaligned loads (movups instead of movaps)
; (c) Add a runtime alignment check and branch

define void @add_unaligned(ptr %arr, i64 %n) {
  ; Vectorized but with: vmovups (%rdi,%rax), %ymm0
  ; Or with a peeling prologue that processes elements until
  ; the pointer becomes aligned
}
```

**Strategy 3: Alignment too low — vectorization disabled or degraded**
```llvm
; If the vectorizer determines that alignment is incompatible with
; the desired vector width, it may choose a narrower vector or
; refuse to vectorize entirely
```

### What Alignment Values Matter for SIMD

| SIMD Width | Instruction Set | Ideal Alignment | Array Element Alignment |
|-----------|----------------|-----------------|------------------------|
| 128-bit | SSE, NEON | 16 bytes | 16 bytes for aligned loads |
| 256-bit | AVX, AVX2 | 32 bytes | 32 bytes for aligned loads |
| 512-bit | AVX-512 | 64 bytes | 64 bytes for aligned loads |

**Language design recommendation for arrays:**

If your language has array types, consider guaranteeing that heap-allocated arrays of SIMD-friendly types (f32, f64, i32, etc.) are aligned to at least 16 bytes (SSE), preferably 32 bytes (AVX). This can be done by having your allocator return properly aligned memory and annotating the resulting pointers.

On modern x86, the performance difference between aligned and unaligned loads is small for SSE/AVX, but it still exists, especially when:
- The access crosses a cache line boundary (64 bytes)
- The access crosses a page boundary (4096 bytes)
- AVX-512 is in use (where 64-byte alignment matters significantly)

### Over-Alignment Tradeoffs

Over-aligning data (e.g., aligning all arrays to 64 bytes) has costs:
- **Memory waste:** Padding between objects wastes cache space
- **Allocator complexity:** The allocator needs to handle alignment requests
- **Array of structs:** Over-aligning small structs in arrays wastes significant space

The sweet spot depends on your language's target use case:
- **Numerical/scientific computing:** 32 or 64-byte alignment for arrays is worth it
- **General-purpose:** 16-byte alignment is a good default
- **Embedded/memory-constrained:** ABI-minimum alignment to save space

---

## The `allocalign` and `allocsize` Attributes

These attributes describe custom allocator functions to LLVM, enabling it to derive alignment and size information for dynamically allocated memory.

### `allocsize(N)` and `allocsize(N, M)`

```llvm
; Tell LLVM that the return pointer points to %size bytes
declare ptr @my_malloc(i64 %size) allocsize(0)

; Tell LLVM the allocation is %count * %size bytes
declare ptr @my_calloc(i64 %count, i64 %size) allocsize(0, 1)
```

LLVM uses this to determine the size of the allocated region, enabling:
- `__builtin_object_size` computation
- Buffer overflow detection
- Dead allocation elimination (if the allocated memory is never used)

### `allocalign(N)`

```llvm
; Tell LLVM that the return pointer is aligned to %align bytes
declare ptr @my_aligned_alloc(i64 %align, i64 %size) allocalign(0) allocsize(1)
```

This tells LLVM that the returned pointer has the alignment specified by parameter N. LLVM can then propagate this alignment information to all users of the pointer.

### Complete Allocator Annotation

For a language with its own allocator, the ideal annotation set is:

```llvm
; Allocation function
declare noalias noundef ptr @lang_alloc(i64 %size, i64 %align)
  allocsize(0) allocalign(1) nounwind

; With null return on failure:
declare noalias ptr @lang_alloc_or_null(i64 %size, i64 %align)
  allocsize(0) allocalign(1) nounwind

; At call sites:
%p = call noalias noundef nonnull dereferenceable(128) align 16
  ptr @lang_alloc(i64 128, i64 16)
```

**Language design recommendation:** If your language has a runtime allocator, annotate it fully with `allocsize`, `allocalign`, `noalias`, and `nounwind`. At each call site, if you know the allocation size and alignment at compile time, emit `dereferenceable(N)` and `align A` on the call result. This gives LLVM maximum information about every heap allocation.

---

## Stack Alignment: `alloca` and the Entry Block

### `alloca` Alignment

```llvm
; Default alignment (determined by data layout)
%x = alloca i32                    ; align 4 on most targets

; Explicit over-alignment for SIMD
%vec = alloca <8 x float>, align 32  ; 32-byte aligned for AVX

; Array on stack with alignment
%arr = alloca [16 x float], align 64 ; 64-byte aligned for AVX-512
```

**Language design recommendation:** Place all `alloca` instructions at the beginning of the entry block. LLVM treats entry-block `alloca`s as "static allocas" — they have fixed offsets from the frame pointer and are cheaper to optimize. Non-entry-block allocas are treated as dynamic stack adjustments and inhibit some optimizations.

```llvm
define void @good() {
entry:
  %a = alloca i32, align 4           ; static alloca — good
  %b = alloca [16 x float], align 32 ; static alloca — good
  br label %work
work:
  ; use %a and %b
  ret void
}

define void @bad(i1 %cond) {
entry:
  br i1 %cond, label %then, label %else
then:
  %a = alloca i32, align 4  ; dynamic alloca — inhibits some optimizations
  ...
}
```

### Natural Stack Alignment

The `S128` in the data layout string means the stack is naturally 16-byte aligned. This means:
- Function prologues maintain 16-byte stack alignment
- `alloca` with alignment ≤ 16 bytes doesn't need extra alignment code
- `alloca` with alignment > 16 bytes (e.g., 32 or 64 for AVX) requires a dynamic stack alignment sequence (save/restore frame pointer, AND the stack pointer)

On x86-64 System V, the ABI guarantees 16-byte stack alignment at function entry. Requesting 32-byte or 64-byte alignment for stack variables incurs a cost: the function prologue must emit extra instructions to align the stack, and a frame pointer is required.

**Language design implication:** If your language frequently uses SIMD types on the stack, you have two options:
1. Accept the per-function cost of dynamic alignment (simple, correct)
2. Increase the required stack alignment in your ABI (avoids per-function cost but requires all call sites to maintain the alignment)

---

## Struct Layout and Padding

### How LLVM Lays Out Structs

LLVM lays out non-packed structs according to the data layout:
- Each field is placed at the next available offset that satisfies its alignment
- Padding bytes are inserted between fields as needed
- The struct's overall alignment is the maximum of its fields' alignments
- The struct's size is padded to a multiple of its alignment

```llvm
; Example: suboptimal field ordering
%bad = type { i8, i64, i8 }
; Layout: [i8, 7 bytes padding, i64, i8, 7 bytes padding]
; Size: 24 bytes, Align: 8

; Reordered:
%good = type { i64, i8, i8 }
; Layout: [i64, i8, i8, 6 bytes padding]
; Size: 16 bytes, Align: 8
```

**Language design options:**

1. **Automatic field reordering:** Your compiler sorts struct fields by alignment (largest first) to minimize padding. Rust does this by default for `repr(Rust)` types. This is purely a compile-time decision and invisible to LLVM.

2. **C-compatible layout:** Use `repr(C)` / natural ordering when you need FFI compatibility. Emit the struct in LLVM in the order the user declared.

3. **Packed structs:** `<{ i8, i64, i8 }>` (packed) eliminates all padding. Size: 10 bytes, Align: 1. But every access to the i64 field is unaligned, which can be expensive (especially on ARM/MIPS).

### SROA and Struct Access Patterns

LLVM's SROA (Scalar Replacement of Aggregates) pass works best when:
- Struct fields are accessed individually via GEP + load/store
- The struct's address doesn't escape
- Fields have natural alignment

```llvm
; Good for SROA: individual field access
%p = alloca %Point   ; {f64, f64}
%xp = getelementptr %Point, ptr %p, i32 0, i32 0
store f64 %x, ptr %xp, align 8
%yp = getelementptr %Point, ptr %p, i32 0, i32 1
store f64 %y, ptr %yp, align 8
; SROA splits this into two independent f64 registers

; Bad for SROA: whole-struct operations
%p = alloca %Point
store %Point { f64 1.0, f64 2.0 }, ptr %p, align 8
; This whole-struct store may inhibit splitting
```

**Language design recommendation:** When lowering struct construction, prefer individual field stores over aggregate stores. SROA handles individual field access much more reliably.

---

## Alignment and Memory Ordering (Atomics)

A critical constraint: **atomic loads and stores REQUIRE the specified alignment.** Unlike non-atomic operations, LLVM cannot split an unaligned atomic access into multiple smaller operations (that would break atomicity).

```llvm
; Valid: naturally aligned atomic
%v = load atomic i64, ptr %p seq_cst, align 8

; INVALID on most targets: under-aligned atomic
%v = load atomic i64, ptr %p seq_cst, align 4
; LLVM CANNOT lower this to a sequence of two 4-byte atomics
; On x86, this might still work (with a LOCK prefix), but on ARM it faults
```

**Language design recommendation:** If your language has atomic types, ensure they are always naturally aligned. This is typically free — just don't put atomics in packed structs. If you need atomic access to fields in packed structs, reject it at compile time.

---

## Global Variable Alignment

```llvm
; Default alignment from data layout
@table = global [256 x i32] zeroinitializer

; Explicit over-alignment for SIMD
@simd_table = global [256 x float] zeroinitializer, align 64

; Constant with alignment
@lookup = constant [16 x <4 x float>] [...], align 64
```

Over-aligning global arrays is essentially free (it only wastes a few padding bytes in the binary) and directly enables SIMD optimization for any code that accesses them.

**Maximum alignment:** LLVM supports alignments up to 2^32 bytes. In practice, alignments above 4096 (page size) are rarely useful and may cause issues with some linkers.

**Language design recommendation:** For global arrays of numeric types, always emit at least `align 16`. For arrays that might be accessed with SIMD, emit `align 32` or `align 64`. The cost is negligible and the benefit for vectorization is significant.

---

## The Speculative Load Optimization Chain

The most impactful optimization enabled by alignment + dereferenceability is the chain of speculative transformations. Here's how it works end-to-end:

**Step 1: Frontend provides guarantees**
```llvm
define void @sum(ptr nonnull noundef align 4 dereferenceable(400) %arr, i64 %n) {
```

**Step 2: LICM hoists loop-invariant loads**
Because the pointer is dereferenceable AND aligned, LLVM can move loads from inside loops to before the loop, even if the loop might execute zero times.

**Step 3: The vectorizer can use aligned loads**
Because alignment is known, the vectorizer doesn't need to generate peel loops or runtime alignment checks.

**Step 4: SimplifyCFG speculates across branches**
If a load is dereferenceable, it can be moved before a conditional branch:
```llvm
; Before: load is conditional
  br i1 %cond, label %then, label %else
then:
  %v = load i32, ptr %p, align 4  ; only executes if %cond is true

; After: load is speculated (if %p is dereferenceable)
  %v = load i32, ptr %p, align 4  ; always executes
  br i1 %cond, label %then, label %else
then:
  ; use %v
```

This eliminates a branch and can enable further optimizations (the loaded value is now available on both paths).

**Step 5: GVN and instcombine benefit**
With loads hoisted and speculated, Global Value Numbering can find more redundant loads and eliminate them.

---

## The `!invariant.load` Metadata

```llvm
%v = load i32, ptr %p, align 4, !invariant.load !{}
```

This tells LLVM the memory at `%p` will never change for the entire lifetime of the program (from the perspective of this load). This is stronger than `dereferenceable` — it says the VALUE is constant, not just that the memory is safe to read.

**Use case:** Vtable pointers, type metadata, class hierarchy information, constant globals accessed through pointers.

**Language design recommendation:** If your language has immutable data (truly constant after initialization), mark loads from it with `!invariant.load`. This enables LLVM to hoist these loads across any operation, including function calls, since the value can never change.

---

## Impact Ranking

From highest to lowest impact for a language designer:

1. **`dereferenceable(N)` + `align A` on reference parameters** — This is the single highest-impact alignment-related annotation. It enables LICM hoisting, speculative loads, and vectorizer optimizations. Applicable to every function that takes a reference/pointer parameter. Cost: zero (it just documents what the language already guarantees).

2. **`dereferenceable(N)` on heap allocation results** — Same benefits as #1 but for dynamically allocated memory. Requires knowing the allocation size at the call site (which your language's type system often provides).

3. **`allocsize` + `allocalign` on allocator functions** — Enables LLVM to reason about the size and alignment of all heap allocations throughout the program. High-value because it has global effects.

4. **Over-alignment of global arrays** — Free or nearly free (a few wasted bytes), directly enables SIMD optimization. Emit `align 32` or `align 64` on global arrays.

5. **`!invariant.load` on truly-constant data** — Enables aggressive hoisting of loads from constant data across calls and loops. Particularly valuable for vtable loads in OOP languages.

6. **Explicit `align` on stack allocas for SIMD** — When your language has vector types or arrays that will be vectorized, use `alloca [...], align 32/64` to avoid dynamic alignment overhead.

7. **Automatic field reordering in structs** — Reduces padding and improves cache utilization. A compile-time-only optimization with no LLVM IR cost.

8. **`nonnull noundef` on all non-nullable pointers** — Helps LLVM eliminate null checks and enables speculative optimizations. Tiny annotation, pervasive benefit.

---

## Gotchas and Known Issues

1. **`align` on parameters does NOT imply dereferenceability.** This is a common source of confusion. You need both `align N` and `dereferenceable(N)` for speculative loads. LLVM bug #90446 demonstrated that propagating parameter alignment to a load alignment was unsound because of this distinction.

2. **`dereferenceable` implies `nonnull` in address space 0** (unless `null_pointer_is_valid` is set). But `nonnull` does NOT imply `dereferenceable`. A one-past-the-end pointer is `nonnull` but not dereferenceable.

3. **`dereferenceable` also implies `noundef`.** You don't need to add `noundef` separately if you have `dereferenceable`.

4. **Alignment must be a power of 2.** LLVM will reject non-power-of-two alignments.

5. **Atomic operations cannot be under-aligned.** The backend cannot split an atomic access into multiple smaller accesses. If you under-align an atomic, LLVM will emit an error or generate incorrect code. Some targets (x86) are more forgiving than others (ARM).

6. **Over-alignment of stack variables > natural stack alignment requires a frame pointer.** On x86-64 (16-byte natural alignment), `alloca [...], align 32` forces the use of a frame pointer and dynamic stack alignment. This has a small cost per function.

7. **Clang's `LargeArrayMinWidth` auto-alignment.** Clang aligns stack arrays larger than a certain size (16 bytes on x86, 128 bits on WebAssembly) to 16 bytes automatically. If your frontend targets the same architectures, consider mimicking this behavior.

8. **`!invariant.load` is very strong.** If the memory CAN be modified (even through another pointer, even in another thread), using `!invariant.load` will cause miscompilation. Only use it for genuinely immutable data.

9. **The `align` attribute on loads/stores has UB semantics.** If you emit `load i32, ptr %p, align 16` and `%p` is only 4-byte aligned, this is immediate UB (not a performance degradation — undefined behavior). Be conservative: only claim alignment you can actually guarantee.
