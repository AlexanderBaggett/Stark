# LLVM Optimization Reference: Intrinsics and Builtin Operations

## Overview

LLVM intrinsics are "compiler-internal" functions (prefixed with `llvm.`) whose semantics are defined directly by LLVM rather than by an external library. They give a frontend precise control over code generation in ways that ordinary IR instructions and libm calls cannot achieve. However, intrinsics vary widely in how well the optimizer handles them — some are fully transparent to optimization passes, while others act as optimization barriers.

This document covers the intrinsics most relevant to language frontend design, ordered by optimization impact. The key principle from LLVM's own Frontend Performance Tips is: **prefer attributes and metadata over intrinsics when possible** (attributes are essentially free; metadata is cheap; intrinsics introduce extra instructions and value uses that can inhibit transforms). Use intrinsics when there is no other way to express the semantics.

---

## 1. Overflow-Checked Arithmetic Intrinsics (HIGH IMPACT)

### 1.1 The `*.with.overflow` Family

These intrinsics perform an arithmetic operation and return both the result and a boolean indicating whether overflow occurred.

```llvm
; Signed addition with overflow detection
%result = call { i32, i1 } @llvm.sadd.with.overflow.i32(i32 %a, i32 %b)
%value  = extractvalue { i32, i1 } %result, 0   ; the sum
%ovf    = extractvalue { i32, i1 } %result, 1   ; true if overflowed

; Full family:
declare { i32, i1 } @llvm.sadd.with.overflow.i32(i32, i32)   ; signed add
declare { i32, i1 } @llvm.uadd.with.overflow.i32(i32, i32)   ; unsigned add
declare { i32, i1 } @llvm.ssub.with.overflow.i32(i32, i32)   ; signed sub
declare { i32, i1 } @llvm.usub.with.overflow.i32(i32, i32)   ; unsigned sub
declare { i32, i1 } @llvm.smul.with.overflow.i32(i32, i32)   ; signed mul
declare { i32, i1 } @llvm.umul.with.overflow.i32(i32, i32)   ; unsigned mul
```

**What they enable:**

- The backend maps these directly to hardware flags. On x86, `sadd.with.overflow` becomes `add` + `jo` (jump-on-overflow). This is a single instruction that sets the overflow flag, far more efficient than the alternative of widening to a larger type and comparing.
- The optimizer understands their semantics: it can constant-fold them, eliminate redundant checks, and use the result to infer range information.
- They work with any integer bitwidth (not just i32/i64).

**How Rust uses them:** Rust's debug-mode integer arithmetic compiles to these intrinsics. A typical pattern is:

```llvm
%result = call { i32, i1 } @llvm.smul.with.overflow.i32(i32 %x, i32 %x)
%value  = extractvalue { i32, i1 } %result, 0
%ovf    = extractvalue { i32, i1 } %result, 1
%expect = call i1 @llvm.expect.i1(i1 %ovf, i1 false)  ; overflow is unlikely
br i1 %expect, label %panic, label %continue
```

**Language design recommendation:** If your language has checked arithmetic as a mode or default, always use these intrinsics rather than widening + compare. They produce strictly better code. The struct return type is a special case that LLVM handles well — the "avoid aggregates" advice from Performance Tips explicitly exempts `.with.overflow` return types.

### 1.2 Saturating Arithmetic Intrinsics

```llvm
; Saturating: clamps to min/max instead of wrapping or trapping
%r = call i32 @llvm.sadd.sat.i32(i32 %a, i32 %b)   ; signed saturating add
%r = call i32 @llvm.uadd.sat.i32(i32 %a, i32 %b)   ; unsigned saturating add
%r = call i32 @llvm.ssub.sat.i32(i32 %a, i32 %b)   ; signed saturating sub
%r = call i32 @llvm.usub.sat.i32(i32 %a, i32 %b)   ; unsigned saturating sub
```

**What they enable:**

- On ARM/AArch64, these map directly to hardware saturating instructions (SQADD, UQADD, SQSUB, UQSUB for NEON vectors; SSAT/USAT for scalars).
- On x86, vector variants map to PADDSS/PADDUS (SSE2 packed saturating add/sub).
- The optimizer can reason about them: if it can prove no overflow, the saturation is removed and it becomes a plain add. InstCombine canonicalizes select+icmp patterns into these intrinsics.
- They are marked as trivially vectorizable, so the loop vectorizer can vectorize code using them.

**Language design recommendation:** If your language offers saturating arithmetic (common in DSP, audio, image processing, and safe numeric types), use these intrinsics directly. They produce dramatically better code than the manual clamp pattern, especially for vector code. Rust switched from a `.with.overflow`-based implementation to these intrinsics (LLVM 8+) and saw significant codegen improvements.

### 1.3 The Relationship Between Intrinsics and UB Flags

It's worth understanding the design spectrum:

| Approach | Overflow Behavior | LLVM IR | Optimization Potential |
|----------|------------------|---------|----------------------|
| UB on overflow | Poison (UB to use) | `add nsw` / `add nuw` | **Highest** — optimizer can assume it doesn't happen |
| Wrapping | Wraps (two's complement) | `add` (no flags) | Medium — well-defined but fewer assumptions |
| Checked | Trap/panic on overflow | `*.with.overflow` + branch | Medium — overflow path is cold |
| Saturating | Clamp to min/max | `*.sat` | Medium — well-optimized, DSP-friendly |

**The LLVM Performance Tips explicitly say:** "Avoid using arithmetic intrinsics unless you are required by your source language specification to emit a particular code sequence. The optimizer is quite good at reasoning about general control flow and arithmetic, it is not anywhere near as strong at reasoning about the various intrinsics."

This means: if your language allows UB on overflow (like C), use `nsw`/`nuw` flags instead of intrinsics. Only use the intrinsics when you genuinely need the checked/saturating behavior.

---

## 2. Memory Lifetime Intrinsics (HIGH IMPACT)

### 2.1 `llvm.lifetime.start` / `llvm.lifetime.end`

```llvm
; Mark the beginning and end of a stack variable's lifetime
%buf = alloca [256 x i8], align 16
call void @llvm.lifetime.start.p0(i64 256, ptr %buf)
; ... use %buf ...
call void @llvm.lifetime.end.p0(i64 256, ptr %buf)
```

**What they enable:**

- **Stack coloring**: When two allocas have non-overlapping lifetimes (determined by these markers), the StackColoring pass can assign them the same stack slot. This is critical for reducing stack frame sizes, especially after inlining.
- **Dead store elimination**: A store immediately before `lifetime.end` can be eliminated (the memory is about to become dead). A load after `lifetime.end` (but before the next `lifetime.start`) returns undef/poison.
- **After inlining**: This is where lifetime markers have the largest impact. When function B is inlined into function A, B's locals get allocas at the top of A. Without lifetime markers, all of B's stack space is reserved for the entire duration of A. With markers, the inliner wraps the inlined code with `lifetime.start`/`lifetime.end`, allowing B's stack to be reused by other inlined functions.

**Real-world impact:** Rust had a known issue where functions used 5× more stack space than necessary because lifetime intrinsics were not being emitted. Adding them brought stack usage to parity with the non-inlined version. In large programs with heavy inlining, this can mean the difference between stack overflows and normal operation.

**Semantics (simplified):**
- `lifetime.start(size, ptr)`: The memory at `ptr` of `size` bytes is now live. Any prior content is undefined.
- `lifetime.end(size, ptr)`: The memory at `ptr` is now dead. Subsequent accesses before the next `lifetime.start` are UB.
- If `size` is -1, it refers to the entire alloca.

**Language design recommendation:** Emit lifetime markers for every alloca whose scope is narrower than the enclosing function. This is especially important if your language encourages large stack allocations or if you expect significant inlining. The markers should tightly bound the actual use of the variable — place `lifetime.start` just before the first use and `lifetime.end` just after the last use within a scope.

**Gotcha:** Lifetime markers must be placed in the entry block's alloca region correctly. The markers themselves can be in any block, but the alloca must be in the entry block for SROA/Mem2Reg to work. Also, be aware that optimization passes can move lifetime markers around, sometimes into configurations that confuse later passes. LLVM is still actively improving the quality of stack coloring (as of 2024, there's ongoing work on issue #109204).

### 2.2 `llvm.invariant.start` / `llvm.invariant.end`

```llvm
; After initialization, mark memory as invariant (constant)
store i32 42, ptr @runtime_init_global
%inv = call ptr @llvm.invariant.start.p0(i64 4, ptr @runtime_init_global)

; Later loads can be assumed to always return 42
%v = load i32, ptr @runtime_init_global  ; optimizer can fold this to 42
; ...

; Optionally, end the invariant (e.g., before a destructor)
call void @llvm.invariant.end.p0(ptr %inv, i64 4, ptr @runtime_init_global)
```

**What they enable:**

- Solve the "runtime-initialized constant" problem: variables that need a constructor or initialization function but are constant thereafter. The global cannot be marked `constant` in IR (because there's a store), but after the store, `invariant.start` tells the optimizer the memory is now frozen.
- GVN and other load-elimination passes can CSE/fold loads from invariant memory.
- The `!invariant.load` metadata on individual load instructions serves a similar purpose for specific loads.

**Current limitations:** In practice, LLVM's exploitation of `invariant.start`/`invariant.end` is not as aggressive as one might hope. The optimization support has improved over time but is not at the level of `constant` globals. The `!invariant.load` metadata on loads tends to be better supported by current passes.

**Language design recommendation:** For globals that are initialized once at program/module startup and then never modified, emit the initialization stores followed by `llvm.invariant.start`. For loads from such globals in hot code, add `!invariant.load` metadata. This combination gives the optimizer the best chance to eliminate redundant loads. Consider filing bugs if specific optimization opportunities are missed — this is an area of active improvement.

---

## 3. Branch Prediction: `llvm.expect` (MEDIUM IMPACT)

```llvm
; Tell the optimizer that %cond is expected to be true
%likely = call i1 @llvm.expect.i1(i1 %cond, i1 true)
br i1 %likely, label %fast_path, label %slow_path

; Also works with integers (for switch statements)
%val = call i64 @llvm.expect.i64(i64 %tag, i64 0)
switch i64 %val, label %default [ i64 0, label %case0 ... ]
```

**What it enables:**

- Sets `!prof` branch weight metadata on the subsequent branch/switch, which guides:
  - **BasicBlock layout**: Hot paths are placed as fall-through, cold paths are placed out-of-line.
  - **Inlining decisions**: Functions with expected-cold paths may be inlined more aggressively because the effective size (along the hot path) is smaller.
  - **Loop optimizations**: Helps the optimizer decide which branch is the loop-continuing branch.
- Does NOT generate CPU branch prediction hints on most architectures (x86 branch hints are generally ignored by modern CPUs). The benefit is entirely from code layout and optimizer heuristics.

**Language design recommendation:** Use `llvm.expect` for:
- Error checks where failure is genuinely rare (null checks, bounds checks, type checks)
- Overflow checks in checked arithmetic (overflow is the cold path)
- `unlikely()` / `likely()` hints from the programmer

The cost is near-zero (the intrinsic is eliminated early in the pipeline, leaving only metadata), so use it liberally on branches where you have strong expectations.

---

## 4. Optimization Assumptions: `llvm.assume` (MEDIUM IMPACT, USE WITH CAUTION)

```llvm
; Basic: assert that %ptr is non-null
call void @llvm.assume(i1 %is_not_null)

; Modern form with operand bundles (preferred):
call void @llvm.assume(i1 true) ["align"(ptr %p, i64 16)]
call void @llvm.assume(i1 true) ["nonnull"(ptr %p)]
call void @llvm.assume(i1 true) ["dereferenceable"(ptr %p, i64 100)]
call void @llvm.assume(i1 true) ["align"(ptr %p, i64 32), "nonnull"(ptr %p)]
```

**What it enables:**

- Communicates arbitrary facts to the optimizer that cannot be expressed through attributes or metadata.
- The optimizer can use assumed facts for value range analysis, alias analysis, dead branch elimination, etc.
- Operand bundles (the modern form) are preferred over boolean conditions because they're more compact, easier for the optimizer to parse, and can express attribute-like properties directly.

**The crucial warning from LLVM's own documentation:**

> "Avoid using the assume intrinsic until you've established that a) there's no other way to express the given fact and b) that fact is critical for optimization purposes. Assumes are a great prototyping mechanism, but they can have negative effects on both compile time and optimization effectiveness."

**Why assumes can hurt:**

- Each `llvm.assume` is a call instruction that creates value uses. These uses can prevent transformations that would otherwise simplify or eliminate the used values.
- The assume call itself acts as a (very mild) optimization barrier — it's a function call that the compiler must keep if there's no proof the condition is always true.
- In pathological cases, many assumes can significantly slow compilation.

**The cost hierarchy** (from LLVM's "Intrinsics, Metadata, and Attributes" talk):

1. **Attributes** — essentially free, use whenever you can
2. **Metadata** — some cost (processing many metadata nodes slows the optimizer)
3. **Intrinsics** (including `llvm.assume`) — most expensive, can inhibit transformations

**Language design recommendation:** Prefer parameter/return attributes (`nonnull`, `align`, `dereferenceable`, `range`, etc.) over `llvm.assume`. Only use `llvm.assume` for facts that emerge mid-function (not at the function boundary) and cannot be expressed any other way. For example, after a successful type check narrows a union type, you might assume alignment or value range properties of the narrowed pointer.

---

## 5. Math Intrinsics vs. libm Calls (MEDIUM IMPACT)

### 5.1 The Standard Math Intrinsics

LLVM provides intrinsic equivalents for most standard C math library functions:

```llvm
; These are NOT libm calls — they're compiler intrinsics with defined semantics
declare float @llvm.sqrt.f32(float)
declare float @llvm.sin.f32(float)
declare float @llvm.cos.f32(float)
declare float @llvm.pow.f32(float, float)
declare float @llvm.exp.f32(float)
declare float @llvm.exp2.f32(float)
declare float @llvm.log.f32(float)
declare float @llvm.log2.f32(float)
declare float @llvm.log10.f32(float)
declare float @llvm.fma.f32(float, float, float)
declare float @llvm.fmuladd.f32(float, float, float)
declare float @llvm.fabs.f32(float)
declare float @llvm.floor.f32(float)
declare float @llvm.ceil.f32(float)
declare float @llvm.trunc.f32(float)
declare float @llvm.round.f32(float)
declare float @llvm.rint.f32(float)
declare float @llvm.nearbyint.f32(float)
declare float @llvm.copysign.f32(float, float)
declare float @llvm.minnum.f32(float, float)
declare float @llvm.maxnum.f32(float, float)
declare float @llvm.minimum.f32(float, float)   ; propagates NaN
declare float @llvm.maximum.f32(float, float)   ; propagates NaN
```

**Newer intrinsics being added (as of 2024-2025):** `llvm.asin`, `llvm.acos`, `llvm.atan`, `llvm.atan2`, `llvm.sinh`, `llvm.cosh`, `llvm.tanh`, `llvm.tan` — driven largely by HLSL/GPU shader support but generally useful.

### 5.2 Intrinsic vs. libm: Key Differences

| Property | `@llvm.sqrt.f32(float)` | `@sqrtf(float)` (libm) |
|----------|------------------------|------------------------|
| Semantics | UB for negative inputs (unless `afn` flag) | Sets `errno` for negative inputs |
| Vectorizable | Yes, automatically | Only if the optimizer can prove no errno set needed |
| Constant-foldable | Yes, at compile time | Yes, but only for known-constant args |
| Linkage required | None (compiler built-in) | Requires libm at link time |
| NaN handling | May differ from IEEE | IEEE 754 compliant |

The critical distinction: LLVM math intrinsics generally do **not** set `errno` and have relaxed NaN handling compared to their libm counterparts. This makes them more optimizable (can be vectorized, constant-folded, reassociated) but means they're only correct for languages that don't require strict C-style `errno` semantics.

### 5.3 `llvm.fma` vs. `llvm.fmuladd`

This distinction matters for numerics:

```llvm
; llvm.fma: guaranteed fused multiply-add (infinite precision intermediate)
%r = call float @llvm.fma.f32(float %a, float %b, float %c)
; MUST use a single FMA instruction if available, or emulate it.
; Result is (a*b)+c computed with only one rounding.

; llvm.fmuladd: fuse if profitable, otherwise separate mul+add
%r = call float @llvm.fmuladd.f32(float %a, float %b, float %c)
; Backend may emit FMA instruction OR separate fmul+fadd.
; This is the "I'd like FMA if it's free" hint.
```

**Language design recommendation:** Use `llvm.fmuladd` when your language permits FMA contraction (the common case). Use `llvm.fma` only when the language semantics *require* a fused operation (e.g., a user explicitly called `fma()`). Using `llvm.fma` on targets without hardware FMA forces an expensive software emulation.

### 5.4 General Recommendation

**For a new language without C-style errno requirements:**

- Emit LLVM math intrinsics (not libm calls) for all standard math operations. This gives the optimizer maximum freedom.
- Add fast-math flags when your language's floating-point model allows it.
- The optimizer may still lower intrinsics to libm calls on targets without hardware support — this is fine and expected.

**For strict IEEE compliance:**

- Use the `constrained` floating-point intrinsics (`@llvm.experimental.constrained.fadd`, etc.) which model the full floating-point environment including rounding mode and exception behavior.
- Or use libm calls directly if you need `errno` behavior.

---

## 6. Memory Operation Intrinsics (MEDIUM IMPACT)

### 6.1 `llvm.memcpy`, `llvm.memmove`, `llvm.memset`

```llvm
; These are NOT calls to libc — they're intrinsics the optimizer understands
call void @llvm.memcpy.p0.p0.i64(ptr %dst, ptr %src, i64 %n, i1 false)
call void @llvm.memmove.p0.p0.i64(ptr %dst, ptr %src, i64 %n, i1 false)
call void @llvm.memset.p0.i64(ptr %dst, i8 0, i64 %n, i1 false)
; The last i1 is the "volatile" flag
```

**What the optimizer does with these:**

- **Small constant sizes** are expanded inline (e.g., `memcpy(dst, src, 16)` becomes two 8-byte load/stores).
- **Known alignment** enables wider loads/stores (aligned memcpy can use vector instructions).
- **Dead store elimination** can remove memsets followed by full overwrites.
- **SROA** can see through small memcpys to track individual field values.
- **GVN** can do load-from-memcpy forwarding (loading a value from a destination that was just memcpy'd from a source where the value is known).

**Language design recommendation:** Use the intrinsics (not libc calls) for struct copies, array fills, etc. The optimizer handles them much better than opaque function calls. Add alignment information when known — it enables wider vector operations.

### 6.2 `llvm.memcpy.inline`

```llvm
; Like llvm.memcpy but MUST be inlined — never generates a library call
call void @llvm.memcpy.inline.p0.p0.i64(ptr align 8 %dst, ptr align 8 %src, i64 32, i1 false)
```

Use when you know the size is small and want to guarantee no function call overhead. The size must be a constant.

---

## 7. Bit Manipulation Intrinsics (LOW-MEDIUM IMPACT)

```llvm
; Count leading zeros
%n = call i32 @llvm.ctlz.i32(i32 %x, i1 true)  ; i1 = "is_zero_poison"

; Count trailing zeros
%n = call i32 @llvm.cttz.i32(i32 %x, i1 true)

; Population count (number of set bits)
%n = call i32 @llvm.ctpop.i32(i32 %x)

; Byte swap (endianness conversion)
%r = call i32 @llvm.bswap.i32(i32 %x)

; Bit reversal
%r = call i32 @llvm.bitreverse.i32(i32 %x)

; Funnel shift (rotate)
%r = call i32 @llvm.fshl.i32(i32 %a, i32 %b, i32 %shift)  ; shift left
%r = call i32 @llvm.fshr.i32(i32 %a, i32 %b, i32 %shift)  ; shift right

; Absolute value
%r = call i32 @llvm.abs.i32(i32 %x, i1 true)  ; i1 = "is_int_min_poison"

; Min/max
%r = call i32 @llvm.smin.i32(i32 %a, i32 %b)
%r = call i32 @llvm.smax.i32(i32 %a, i32 %b)
%r = call i32 @llvm.umin.i32(i32 %a, i32 %b)
%r = call i32 @llvm.umax.i32(i32 %a, i32 %b)
```

**Why use intrinsics over manual IR:**

- `ctlz`/`cttz`: The manual IR pattern for "count leading zeros" is complex and target-specific. The intrinsic maps directly to `lzcnt`/`bsr` on x86, `clz` on ARM, etc.
- The `is_zero_poison` flag (the `i1` parameter) is critical: when true, passing zero is UB, which allows the backend to use the faster instruction variant (e.g., `bsr` on x86 which is undefined for zero, rather than `lzcnt` which handles zero but requires a newer ISA).
- `fshl`/`fshr`: These express rotate operations which are very hard for LLVM to pattern-match from shifts and ORs.
- `smin`/`smax`/`umin`/`umax`: The optimizer canonicalizes `select(icmp, a, b)` patterns to these intrinsics. Using them directly skips the pattern-matching step.

**Language design recommendation:** If your language exposes bit manipulation operations (count leading/trailing zeros, popcount, rotate, byte swap, clamp), emit the corresponding intrinsics. They will always generate better code than the manual equivalent. The `is_zero_poison` flag on `ctlz`/`cttz` is a free optimization win if your language already guarantees non-zero inputs (or if zero is UB).

---

## 8. Address-Space and Pointer Intrinsics (SITUATIONAL)

### 8.1 `llvm.ptrmask`

```llvm
; Mask pointer bits — useful for tagged pointers
%masked = call ptr @llvm.ptrmask.p0.i64(ptr %tagged_ptr, i64 -16)  ; clear low 4 bits
```

This is more optimization-friendly than the `ptrtoint` + `and` + `inttoptr` sequence because it preserves pointer provenance information for alias analysis.

### 8.2 `llvm.threadlocal.address`

```llvm
@tls_var = thread_local global i32 0
%addr = call ptr @llvm.threadlocal.address(ptr @tls_var)
%val = load i32, ptr %addr
```

Used to access TLS variables in a way that the optimizer can reason about (CSE multiple accesses to the same TLS variable within a function).

---

## 9. Trap and Unreachable Patterns (LOW-MEDIUM IMPACT)

### 9.1 `llvm.trap` and `llvm.debugtrap`

```llvm
; Unconditional abort — generates ud2 on x86
call void @llvm.trap()
unreachable

; Debug trap — generates int3 on x86, continues execution
call void @llvm.debugtrap()
```

### 9.2 `llvm.ubsantrap`

```llvm
; Like trap but encodes a failure kind for diagnostics
call void @llvm.ubsantrap(i8 22)   ; 22 = some error code
unreachable
```

### 9.3 The `unreachable` Instruction

While not an intrinsic, `unreachable` is essential: it tells LLVM that a code point is never reached. This enables dead code elimination of everything feeding into it and can simplify control flow. Use `unreachable` after trap calls, after `noreturn` function calls, and in branches that your type system proves impossible.

**Canonical form for unconditional UB:**

```llvm
; The recommended way to express "this point is unreachable"
store i1 true, ptr poison, align 1
unreachable
```

---

## 10. Summary: What to Emit and When

### Tier 1 — Always Emit These (High ROI, well-optimized)

| Intrinsic | When | Why |
|-----------|------|-----|
| `llvm.lifetime.start/end` | Every alloca with limited scope | Stack coloring, DSE |
| `*.with.overflow` | Checked arithmetic | Maps to hardware flags |
| `*.sat` | Saturating arithmetic | Maps to hardware saturating ops |
| `llvm.expect` | Branches with known bias | Code layout, inlining decisions |
| `llvm.memcpy/memmove/memset` | Struct/array copies/fills | Better than libc calls |

### Tier 2 — Emit When Applicable (Medium ROI)

| Intrinsic | When | Why |
|-----------|------|-----|
| `llvm.invariant.start` | After runtime init of immutable data | Enables load elimination |
| `llvm.ctlz/cttz/ctpop` | Bit manipulation builtins | Direct HW instruction mapping |
| `llvm.fshl/fshr` | Rotate operations | Hard to pattern-match otherwise |
| `llvm.smin/smax/umin/umax` | Clamp/min/max operations | Canonical form |
| `llvm.abs` | Absolute value | Canonical form |
| `llvm.bswap` | Endianness conversion | Direct HW instruction |
| `llvm.fmuladd` | FP multiply-add when FMA contraction OK | May use HW FMA |
| Math intrinsics (`sqrt`, `sin`, etc.) | When errno not needed | Enables vectorization |

### Tier 3 — Use With Caution

| Intrinsic | When | Caveat |
|-----------|------|--------|
| `llvm.assume` | Facts not expressible as attributes | Can inhibit optimization; use sparingly |
| `llvm.fma` | Only when FMA semantics required | Forces expensive SW emulation without HW FMA |
| `llvm.invariant.end` | Object becomes mutable again | Limited optimizer support |

### Prefer These Instead of Intrinsics

| Instead of... | Use... |
|---------------|--------|
| `llvm.assume(i1 %ptr_nonnull)` | `nonnull` parameter attribute |
| `llvm.assume(... ["align"(ptr, N)])` | `align N` parameter attribute |
| `llvm.assume(... ["dereferenceable"(...)])` | `dereferenceable(N)` parameter attribute |
| Manual overflow detection logic | `*.with.overflow` intrinsics |
| `ptrtoint` + mask + `inttoptr` | `llvm.ptrmask` |
| Library `memcpy()` call | `llvm.memcpy` intrinsic |

---

## 11. Interactions with Other Categories

### Intrinsics × Exception Handling
If your language uses `nounwind` (no exceptions), all `invoke` instructions become `call` instructions, which means intrinsics like `llvm.lifetime.end` in landing pads are unnecessary. The simpler CFG allows better stack coloring and DSE.

### Intrinsics × UB Flags
The `*.with.overflow` intrinsics interact with `nsw`/`nuw` flags: if your language has a "release mode" that assumes no overflow, you switch from `call @llvm.sadd.with.overflow.i32` to `add nsw i32`, getting better optimization. This mode-switching is a key language design decision.

### Intrinsics × Globals and Module Structure
`llvm.invariant.start` is the bridge between runtime-initialized globals and the `constant` global attribute. Under LTO, if the optimizer can see that a global is initialized once in a constructor and then has `invariant.start` called, it can potentially treat subsequent loads as constant-foldable.

### Intrinsics × Alignment and Vectorization
The `llvm.assume` with `"align"` operand bundles, or better yet explicit `align` attributes on loads/stores, enables the vectorizer to emit aligned vector operations. This interacts with the alignment decisions in your data layout.

### Intrinsics × Control Flow
`llvm.expect` interacts with loop optimization: marking the loop-continuation branch as likely helps the optimizer identify the loop's hot path. Combined with `mustprogress` (from your control-flow document), this gives LLVM maximum information about loop behavior.
