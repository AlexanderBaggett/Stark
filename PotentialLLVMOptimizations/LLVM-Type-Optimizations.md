# LLVM Type-Level Optimizations for Language Design

A comprehensive reference for programming language designers seeking to maximize the optimization information conveyed to LLVM through the type system. Every type choice, annotation, and IR emission pattern described here is backed by LLVM documentation, frontend performance guides, and real-world compiler implementations (primarily Rust and Clang).

---

## Table of Contents

1. [Core Principle](#core-principle)
2. [Integers](#integers)
3. [Floats](#floats)
4. [Enums](#enums)
5. [Characters and Unicode](#characters-and-unicode)
6. [Strings](#strings)
7. [Arrays](#arrays)
8. [Structs and Aggregates](#structs-and-aggregates)
9. [Array-of-Structs vs Struct-of-Arrays](#array-of-structs-vs-struct-of-arrays)
10. [Poison, Undef, and noundef](#poison-undef-and-noundef)
11. [Optimization Impact Ranking](#optimization-impact-ranking)
12. [Sources](#sources)

---

## Core Principle

LLVM is a constraint-based optimizer. The more guarantees a frontend provides about the values flowing through a program, the more aggressively LLVM can optimize. A language designed for maximum performance should emit the richest possible metadata, attributes, and type constraints on every value, parameter, return, and memory access.

The guiding question for every type design decision: **What can LLVM prove about this value, and what IR annotations make that proof available to every optimization pass?**

---

## Integers

### Bit Width Selection

LLVM supports integer types from `i1` to `i8388607` (2^23 − 1 bits). Non-power-of-2 widths like `i7`, `i21`, and `i24` are legal but carry hidden costs: the backend often inserts bit-masking and sign/zero-extension operations to fit values into machine registers.

The practical sweet spot is to **use the smallest standard width** (`i8`, `i16`, `i32`, `i64`) that can hold the value range, then **constrain the actual range with metadata**. This gives LLVM a standard-width value it handles natively, plus range information it can exploit for dead code elimination, branch folding, and loop analysis.

Clang's `_ExtInt` (now `_BitInt`) feature exposes arbitrary-width integers for specialized domains like FPGA programming, but for general-purpose language design, standard widths with range constraints dominate exotic bit widths.

### Value Range Constraints

This is the single most impactful integer optimization lever. LLVM provides three mechanisms for expressing that an integer value falls within a known range.

**Mechanism 1: `!range` metadata on loads and calls.**

```llvm
%x = load i8, ptr %p, !range !0
!0 = !{i8 1, i8 5}   ; value is in [1, 5) — i.e., 1, 2, 3, or 4
```

The range is half-open: `[low, high)`. Disjoint and wrapping ranges are supported. For example, `!range !{i32 1, i32 0}` means [1, MAX_INT] ∪ [MIN_INT, 0) — effectively "never zero." This is exactly how Rust compiles `NonZeroU32`: the load of its inner value carries `!range !{i32 1, i32 0}`, which allows LLVM to eliminate null checks entirely and enables niche optimization for `Option<NonZeroU32>`.

Limitations: `!range` can only be attached to `load` and `call` instructions, not to function parameters directly.

**Mechanism 2: `range` attribute on parameters and return values (LLVM 18+).**

```llvm
define i32 @foo(i32 range(i32 0, 100) %x) {
  ...
}
```

This newer mechanism addresses cases where values are passed by register (not loaded from memory), such as function arguments after SROA promotes stack allocas to SSA values. It currently supports only a single contiguous range, unlike `!range` metadata which supports disjoint ranges.

**Mechanism 3: `llvm.assume` with operand bundles.**

```llvm
call void @llvm.assume(i1 true) ["range"(i32 %val, i32 0, i32 100)]
```

More verbose but works anywhere in the code. This is part of LLVM's move toward using operand bundles for expressing assumptions.

**What range information enables:**

- Eliminates redundant comparisons and dead branches. If `%x` is in [0, 10), then `icmp ult %x, 100` folds to `true`.
- Converts `sext` to `zext`. If a value is known non-negative, sign extension becomes zero extension, which on x86-64 is often free (a 32-bit write implicitly zero-extends to 64 bits, while sign extension requires an explicit `movsx` instruction).
- Better loop trip count analysis. SCEV (Scalar Evolution) uses range information to compute exact or bounded trip counts, enabling loop unrolling and vectorization.
- Proves array indices in-bounds, eliminating bounds checks.
- Enables constant folding of range-dependent operations.

### zext vs sext

LLVM's own Frontend Performance Tips document explicitly recommends: **prefer `zext` over `sext` when the value is known non-negative.**

On x86-64, zero extension from 32 to 64 bits is literally free — writing to a 32-bit register implicitly zeros the upper 32 bits. Sign extension requires an explicit `movsx` instruction. This matters especially for GEP (GetElementPtr) index calculations, where LLVM internally promotes indices to pointer width. If an index is non-negative (as array indices typically are), emitting `zext` instead of `sext` avoids unnecessary instructions.

A language where array indices are unsigned or constrained to non-negative values can emit `zext` for every index promotion, gaining a small but pervasive win.

---

## Floats

### nofpclass Attribute

The float equivalent of `!range` for integers. The `nofpclass` attribute declares that a floating-point parameter or return value never contains certain special value classes:

```llvm
define nofpclass(nan inf nzero) float @safe_float(float nofpclass(nan inf nzero) %x) {
  ...
}
```

Available classes to exclude:

| Class | Meaning |
|-------|---------|
| `nan` | Never NaN (quiet or signaling) |
| `inf` | Never ±infinity |
| `zero` | Never ±0.0 |
| `nzero` | Never −0.0 |
| `sub` | Never subnormal/denormal |

These are combinable. A language that traps or converts on NaN, infinity, and negative zero can emit `nofpclass(nan inf nzero)` on every float parameter and return value, allowing LLVM to eliminate NaN/Inf checks, simplify floating-point comparisons (no need for unordered comparisons), and assume operations won't produce special values.

### Fast-Math Flags

Per-instruction flags that relax IEEE 754 semantics:

| Flag | Permits |
|------|---------|
| `nnan` | Assume no NaNs |
| `ninf` | Assume no infinities |
| `nsz` | Treat −0.0 as +0.0 |
| `arcp` | Allow reciprocal approximation |
| `contract` | Allow fused multiply-add |
| `reassoc` | Allow reassociation of operations |
| `fast` | All of the above |

If a language doesn't guarantee strict IEEE 754 behavior, emitting `fast` on every floating-point operation is enormously impactful for numerical code: it enables vectorization, operation reordering, strength reduction (e.g., replacing division with reciprocal multiplication), and FMA contraction.

### Language Design Opportunity

A float type that traps on NaN, infinity, and negative zero by default — converting or signaling at the point of creation rather than propagating — can emit both `nofpclass(nan inf nzero)` on every parameter/return and fast-math flags on every operation. This gives LLVM maximum freedom to optimize floating-point code while the language runtime handles the rare exceptional cases.

---

## Enums

Enums map to integer discriminants in LLVM IR. The optimization opportunities come from constraining the discriminant's value range and choosing representations carefully.

### Discriminant Range Metadata

An enum with variants at discriminant values 0, 1, 2, 3 should emit:

```llvm
%disc = load i8, ptr %enum_ptr, !range !0
!0 = !{i8 0, i8 4}   ; [0, 4) — valid values are 0, 1, 2, 3
```

This enables LLVM to eliminate default branches in switch instructions, prove match exhaustiveness (allowing the optimizer to treat the switch as covering all cases), and fold comparisons against out-of-range values.

### Smallest Integer Width

Use the smallest standard integer type that fits the discriminant range: 5 variants → `i8`. While `i3` is technically legal, `i8` avoids the bit-masking overhead of non-byte-sized types.

LLVM's Frontend Performance Tips explicitly advise: **avoid loads and stores of non-byte-sized types like `i1`.** Instead, store `i1` by zero-extending to `i8`, and load `i8` then truncate to `i1`.

### Zero Discriminant for "None" / "Empty"

Placing the "empty" or "none" variant at discriminant 0 enables `test eax, eax` (2 bytes, sets flags directly) instead of `cmp eax, N` (3+ bytes, less efficient). This is a small but consistent win. Rust addressed this in PR #87794.

### Niche Optimization

A frontend technique pioneered by Rust. When a payload field has unused bit patterns (e.g., a `NonZeroU32` can never be 0), the discriminant can be stored in those unused patterns rather than in a separate field. This allows `Option<NonZeroU32>` to be 4 bytes instead of 8.

The key enabler is `!range` metadata: by telling LLVM that the inner value is never zero, the compiler can use the zero representation for the `None` discriminant. The `!range` metadata then ensures LLVM knows the combined value's constraints.

### Disjoint Range Limitation

For enums with non-contiguous discriminant values (e.g., 1, 2, 4, 8), `!range` metadata supports disjoint ranges:

```llvm
!range !{i8 1, i8 3, i8 4, i8 5, i8 8, i8 9}  ; [1,3) ∪ [4,5) ∪ [8,9)
```

However, the newer `range` attribute on parameters currently supports only a single contiguous range, so passing such an enum by value loses precision. This is tracked in Rust issue #133822.

---

## Characters and Unicode

LLVM has no character type. Characters are integers with range constraints. The performance distinction between ASCII and Unicode comes entirely from representation width and metadata.

### ASCII Characters

```llvm
%c = load i8, ptr %char_ptr, !range !0
!0 = !{i8 0, i8 128}   ; values 0–127
```

The high bit is always zero, so zero extension to `i32` or `i64` is free (no sign bit to worry about). Comparisons against values ≥ 128 are dead code. Array indexing with an ASCII character as index requires no sign-extension. This is a concrete win if the language distinguishes ASCII from general bytes at the type level.

### Unicode Code Points

```llvm
%cp = load i32, ptr %codepoint_ptr, !range !0
!0 = !{i32 0, i32 1114112}   ; 0 to 0x10FFFF
```

The top 11 bits are always zero. LLVM can exploit this for faster comparisons and can eliminate bounds checks for lookup tables with ≤ 1,114,112 entries.

### Why the Type Distinction Matters

A language that distinguishes `AsciiChar` (i8, range [0, 128)) from `UnicodeChar` (i32, range [0, 0x110000)) at the type level enables LLVM to emit tighter code for ASCII-only operations without any runtime overhead — the constraint is purely in the metadata.

---

## Strings

LLVM has no native string type. Strings are arrays of bytes or integers, and the performance implications come from representation choices and the intrinsics used to operate on them.

### Representation

- **UTF-8 / ASCII**: `[N x i8]` — compact, cache-friendly for sequential access.
- **UTF-32**: `[N x i32]` — constant-time indexing by code point, 4× memory cost.

The optimizer understands these as arrays and can reason about element sizes, alignment, and access patterns.

### Bulk Operations

Always use LLVM's memory intrinsics for string operations:

- `llvm.memcpy` — non-overlapping copy
- `llvm.memmove` — potentially overlapping copy
- `llvm.memset` — fill with a byte value

These get excellent platform-specific code generation (using SIMD, REP MOVSB, etc.) and are understood by alias analysis.

### Length-Prefixed vs Null-Terminated

Length-prefixed strings allow the `dereferenceable(N)` attribute on the string pointer, telling LLVM that at least N bytes can be read from the pointer. This enables speculative reads and prefetching. Null-terminated strings don't provide this guarantee (the length is unknown until the terminator is found).

### noundef on String Data

Marking string data as `noundef` prevents poison propagation through string operations, allowing LLVM to optimize more aggressively. If the language guarantees all string data is initialized, this is a free win.

---

## Arrays

### GEP Flags

Every array element access should emit `getelementptr inbounds` with `nuw` (no unsigned wrap):

```llvm
%elem = getelementptr inbounds [100 x i32], ptr %arr, i64 0, i64 %idx
```

The `inbounds` flag tells LLVM the result pointer is within the allocated object, enabling alias analysis to reason about non-overlapping accesses. The `nuw` flag (added in LLVM 18) provides additional guarantees about unsigned offset arithmetic.

### Index Range Metadata

If the language has bounds-checked arrays, the compiler knows at the point of element access that the index is in [0, N). Emitting `!range` on the index value enables LLVM to:

- Prove the GEP is in-bounds without runtime checks (for subsequent accesses)
- Use `zext` instead of `sext` for index promotion (since the index is non-negative)
- Compute precise loop trip counts when iterating over the array

### What LLVM Handles Well

- Static-size arrays with `inbounds` GEPs
- Individual scalar loads/stores of array elements
- Vectors (`<4 x float>`, `<8 x i32>`) — first-class SIMD support with dedicated instructions

### What LLVM Handles Poorly

- Loading/storing entire large arrays as aggregate values
- Non-byte-sized element types (arrays of `i1` — use `i8` and truncate)
- Dynamic arrays without bounds information

---

## Structs and Aggregates

This is where LLVM's limitations are most important for language design.

### The Critical Rule

From LLVM's Frontend Performance Tips, explicitly stated: **avoid creating values of aggregate types.** Avoid loading and storing entire structs. Avoid `insertvalue` and `extractvalue` on large aggregates.

LLVM's SROA (Scalar Replacement of Aggregates) pass attempts to break aggregates into individual SSA values, but it only works on `alloca` instructions in the entry basic block. If SROA/Mem2Reg can't eliminate an alloca, the optimizer is dramatically less effective for that value.

### What the Compiler Should Emit

Instead of loading a whole struct and extracting fields:

```llvm
; BAD — loads entire struct as a value
%whole = load %struct.Point, ptr %p
%x = extractvalue %struct.Point %whole, 0
%y = extractvalue %struct.Point %whole, 1
```

Emit individual field accesses through GEPs:

```llvm
; GOOD — individual field loads through GEP
%xptr = getelementptr inbounds %struct.Point, ptr %p, i64 0, i32 0
%x = load float, ptr %xptr, align 4
%yptr = getelementptr inbounds %struct.Point, ptr %p, i64 0, i32 1
%y = load float, ptr %yptr, align 4
```

This is a **compiler implementation choice**, not a language restriction. The user writes `p.x` and the compiler emits the right GEP + scalar load. The language doesn't need to restrict what types can appear in structs; it just needs a compiler that emits field-level accesses.

### Small Return Structs

For functions returning multiple values, small structs (≤ 2 machine words) are returned in registers on most calling conventions. A two-element struct `{i64, i64}` is returned in `rax` and `rdx` on x86-64 — no memory allocation needed. This is the ideal way to return multiple values.

### TBAA (Type-Based Alias Analysis)

Every load and store should carry TBAA metadata describing the type being accessed. This allows LLVM's alias analysis to prove that a load of a `float` field can't alias a store to an `i32` field, enabling instruction reordering and elimination:

```llvm
%x = load float, ptr %p, !tbaa !5
store i32 42, ptr %q, !tbaa !6
; LLVM knows these can't alias (different types) → can reorder freely
```

A language with a strict type system (no arbitrary type punning) can emit precise TBAA metadata for every memory access, which is a significant optimization enabler.

---

## Array-of-Structs vs Struct-of-Arrays

For performance-critical array iteration, the memory layout choice between AoS and SoA can have a larger impact than almost any other type-level decision.

### The Problem with Array-of-Structs

```
AoS memory layout: [x y x y x y x y ...]  (fields interleaved)
```

When a loop iterates over an array of structs but only touches one field (e.g., summing all `x` values), the `y` values waste cache line space. Worse, LLVM's loop vectorizer struggles with interleaved data — it needs gather/scatter operations or explicit deinterleaving shuffles, which are much slower than contiguous vector loads.

### The Struct-of-Arrays Advantage

```
SoA memory layout: [x x x x ...] [y y y y ...]  (fields contiguous)
```

Each field is a contiguous array. LLVM's loop vectorizer can load `<4 x float>` directly from the `x` array, process it with SIMD, and store back — no shuffling needed. This is the single biggest enabler for auto-vectorization of data-processing loops.

### Flattening Nested Structs for SoA

For flat structs, the transformation is trivial:

```
struct Point { x: f32, y: f32 }
Array<Point, 100>  →  struct { xs: [100 x f32], ys: [100 x f32] }
```

For nested structs, the compiler recursively collects leaf (primitive) fields:

```
struct Line { start: Point, end: Point, thickness: f32 }

Full flatten → struct {
    start_x:   [100 x f32]
    start_y:   [100 x f32]
    end_x:     [100 x f32]
    end_y:     [100 x f32]
    thickness: [100 x f32]
}
```

The flattening algorithm walks the struct recursively, collecting all leaf fields with their full path, then generates one array per leaf field. All field paths are known at compile time, so `arr[i].start.x` resolves to a GEP into the `start_x` array at index `i` with zero runtime cost.

### Tradeoffs of Full Flattening

**Wins:**

- Every array is a contiguous run of identical primitives — optimal for vectorization.
- Each field array has perfect spatial locality for single-field iteration.

**Costs:**

- Cannot take a reference to a sub-struct (`&arr[i].start` has no contiguous `Point` in memory).
- Copying an element requires N separate operations (one per leaf field), scattered across memory.
- Accessing all fields of one element touches N different cache lines instead of one contiguous region.

### Recommended Language Design

- **Default: AoS layout**, with the compiler emitting individual field GEPs (not whole-struct loads). This is correct, predictable, and already well-optimized.
- **Opt-in SoA annotation** (e.g., `@soa Array<Line, 100>`) that recursively flattens to leaf primitives, with the compiler enforcing at compile time that interior references are prohibited.
- Alternatively, expose SoA as an explicit data structure (like Zig's `MultiArrayList`) rather than a transparent layout transformation.

---

## Poison, Undef, and noundef

LLVM distinguishes two kinds of "undefined" values, and the choice matters for optimization.

### undef vs poison

- **`undef`**: Can be any value, chosen independently at each use. This unpredictability blocks many optimizations because the compiler can't assume two uses of the same `undef` produce the same value.
- **`poison`**: The result of undefined behavior. Propagates predictably through operations — any operation on poison produces poison. This predictability enables folding and simplification.

LLVM's Frontend Performance Tips explicitly state: **prefer poison over undef.** Poison is more optimizable because it propagates consistently.

### The noundef Attribute

Marking a parameter or return value as `noundef` tells LLVM the value is neither `undef` nor `poison` — it is a well-defined concrete value:

```llvm
define i32 @foo(i32 noundef %x) {
  ...
}
```

This enables transforms that would be unsound if the value might be `undef` or `poison`. It's a free win for any language that prohibits uninitialized variables.

### Language Design Implication

If the language guarantees that all variables are initialized before use (via mandatory initialization, default values, or compile-time checking), emit `noundef` on every parameter and return value. This is one of the highest-impact, lowest-effort optimizations available — it costs nothing at runtime and enables LLVM transforms across the entire program.

---

## Optimization Impact Ranking

Ordered by estimated impact on generated code quality, from highest to lowest:

1. **`noundef` on all parameters and returns** — Free if the language has no uninitialized values. Enables the widest range of LLVM transforms.

2. **`!range` metadata / `range` attribute on integers** — Eliminates branches, converts sext→zext, enables loop analysis and bounds check elimination. Applies to every integer-typed value with a known constraint.

3. **`nofpclass` on floats** — Eliminates NaN/Inf checks, simplifies comparisons. High impact for any code with floating-point operations.

4. **Fast-math flags** — If the language doesn't require strict IEEE 754 semantics, enables vectorization, reassociation, FMA contraction, and strength reduction. Massive impact on numerical code.

5. **Standard-width integers with range constraints** — Use `i8`/`i16`/`i32`/`i64` constrained by `!range`, not exotic bit widths like `i7` or `i21`.

6. **`zext` over `sext` for non-negative values** — Free zero extension on x86-64; pervasive win for array indices and unsigned types.

7. **Individual field access for structs** — Never load/store whole aggregates. Emit GEP + scalar load/store per field. Critical for SROA effectiveness.

8. **Small return structs** — Return multiple values as small structs (≤ 2 machine words) that fit in registers.

9. **`inbounds` + `nuw` on array GEPs** — With `!range` on indices. Enables alias analysis, bounds check elimination, and precise loop analysis.

10. **TBAA on all loads and stores** — Type-based alias analysis. Proves non-aliasing between accesses of different types, enabling instruction reordering and dead store elimination.

11. **SoA layout for hot arrays** — Opt-in struct-of-arrays layout for performance-critical iteration patterns. Enables auto-vectorization.

---

## Sources

- LLVM Language Reference Manual (LangRef), multiple versions through 23.0.0git
- LLVM Frontend Performance Tips documentation
- LLVM Alias Analysis Infrastructure documentation
- Rust compiler source and issues: #76628 (range on by-value args), #133822 (disjoint ranges), PR #87794 (zero discriminant)
- LLVM GitHub issues: GEP nusw/nuw (PR #90824), SROA behavior (#150824)
- Clang `_ExtInt` / `_BitInt` documentation
- LLVM doxygen: `Argument`, `Function`, `GetElementPtrInst` class references
- LLVM Discourse: range metadata on array subscripts, NaN semantics
- Blog: "How LLVM Optimizes a Function" (Regehr)
- LLVM byte type proposal (GitHub gist)
