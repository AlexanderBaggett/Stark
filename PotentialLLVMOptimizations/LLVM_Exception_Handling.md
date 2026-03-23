# Exception Handling Strategy: LLVM Optimization Research for Language Design

## Overview

The exception handling (EH) model chosen by a language has a **pervasive** effect on the LLVM IR it emits. Unlike most other optimization decisions (which affect individual instructions or functions), the EH model affects *every call instruction in the entire program*. The choice determines whether calls are emitted as `call` or `invoke`, whether landing pad blocks exist, whether the `nounwind` attribute can be applied globally, whether unwind tables are generated, and what function attributes can be inferred — all of which cascade into major differences in CFG complexity, code size, inlining decisions, and code motion opportunities.

This document covers the four EH models available in LLVM, ranks them by optimization impact, and provides concrete language design recommendations.

---

## The Four EH Models in LLVM

### Model 1: No Exceptions (`nounwind` everywhere)

**What it means at the IR level:**
Every function in the program gets the `nounwind` attribute, every call site uses `call` (never `invoke`), no `landingpad` or `catchswitch` instructions exist anywhere, and no `personality` function is required.

```llvm
; With no-exceptions: simple, clean CFG
define i32 @process(ptr %data, i32 %len) nounwind {
entry:
  %result = call i32 @compute(ptr %data, i32 %len) nounwind
  %adjusted = add nsw i32 %result, 1
  call void @store_result(i32 %adjusted) nounwind
  ret i32 %adjusted
}
```

**Optimizations enabled:**

1. **`call` instead of `invoke` — CFG simplification.** This is the single most impactful benefit. An `invoke` is a terminator instruction that splits a basic block into two successors (normal and unwind). A `call` is not a terminator. This means:
   - Fewer basic blocks in every function (often 2-5x fewer for functions with multiple calls).
   - SimplifyCFG can merge blocks that would otherwise be separated by `invoke` edges.
   - PHI nodes at block boundaries are eliminated or simplified.
   - The dominator tree is simpler, enabling more aggressive code motion.

2. **LICM (Loop-Invariant Code Motion) is more effective.** When a call inside a loop uses `invoke`, the unwind edge creates a side exit from the loop. LLVM must conservatively assume that memory could be modified on that unwind path. With `call nounwind`, there is no side exit, so LICM can more aggressively hoist loads and sink stores.

3. **Inlining cost model improves.** The inliner's cost calculation accounts for the CFG complexity introduced by `invoke` instructions. Functions with `nounwind` calls are cheaper to inline because they don't introduce additional exceptional control flow edges in the caller.

4. **Dead store elimination.** Without unwind edges, stores that are overwritten before the next observable side effect can be eliminated more aggressively. With `invoke`, a store before a call might be "observed" by the unwind path's landing pad.

5. **Tail call optimization.** `invoke` instructions cannot be tail calls. Period. Only `call` instructions can be marked `musttail` or optimized into tail calls by the backend. A no-exceptions language gets tail calls for free everywhere.

6. **No `.eh_frame` / unwind table overhead.** Without the `uwtable` attribute, the compiler does not emit `.eh_frame` sections. On x86-64 Linux (which conventionally uses `uwtable` even for C code for backtrace support), this can save 10-20% of binary size in code-heavy programs. Note: you may still *want* unwind tables for debugger/profiler backtrace support, in which case you can use `uwtable` with `nounwind` — the tables are emitted but the optimizer still knows no exception unwinds will occur.

7. **The `willreturn` attribute becomes inferrable.** Combined with `nounwind`, if LLVM can prove a function has no infinite loops, it can mark it `willreturn`, enabling further dead code elimination.

```llvm
; WITHOUT nounwind: invoke creates complex CFG
define i32 @process_with_eh(ptr %data, i32 %len) personality ptr @__gxx_personality_v0 {
entry:
  %result = invoke i32 @compute(ptr %data, i32 %len)
    to label %cont unwind label %lpad

cont:
  %adjusted = add nsw i32 %result, 1
  invoke void @store_result(i32 %adjusted)
    to label %ret unwind label %lpad

ret:
  ret i32 %adjusted

lpad:
  %exc = landingpad { ptr, i32 }
    cleanup
  resume { ptr, i32 } %exc
}

; WITH nounwind: clean linear CFG, same semantics for happy path
define i32 @process_no_eh(ptr %data, i32 %len) nounwind {
entry:
  %result = call i32 @compute(ptr %data, i32 %len) nounwind
  %adjusted = add nsw i32 %result, 1
  call void @store_result(i32 %adjusted) nounwind
  ret i32 %adjusted
}
; The nounwind version has 1 basic block instead of 4.
; The optimizer sees a straight-line sequence, not a diamond CFG.
```

**Quantified impact:** Rust's experience with `panic=abort` (which enables `nounwind` on all functions and removes `invoke`) showed an **11% compile time improvement and 13% binary size reduction** when compiling the Cargo build tool as a library (16s→18s, 15MB→13MB). Runtime performance improvements are harder to measure in aggregate but are significant for tight loops and heavily-inlined code.

### Model 2: DWARF/Itanium "Zero-Cost" Exceptions (landingpad model)

**What it means at the IR level:**
Functions that may unwind do NOT get `nounwind`. Calls to functions that might throw use `invoke` with a `landingpad` in the unwind destination. A `personality` function is declared on any function containing EH constructs.

```llvm
define i32 @example() personality ptr @__gxx_personality_v0 {
entry:
  %val = invoke i32 @may_throw()
    to label %cont unwind label %lpad

cont:
  ret i32 %val

lpad:
  %exc = landingpad { ptr, i32 }
    catch ptr @_ZTIi         ; catch int exceptions
    cleanup                  ; also run cleanup code
  ; ... exception dispatch logic ...
  resume { ptr, i32 } %exc
}
```

**"Zero-cost" is misleading — the costs are real but shifted:**

- **Zero runtime overhead on the non-exceptional path** — no setjmp, no registration, no bookkeeping. The program runs at full speed until an exception is actually thrown.
- **Non-zero code size overhead** — `.eh_frame` unwind tables and `.gcc_except_table` LSDA (Language-Specific Data Area) tables are generated for every function. These are read-only data sections, but they cost binary size (typically 10-20% of `.text` size).
- **Non-zero optimizer overhead** — the `invoke` instructions and landing pad blocks are visible to all LLVM optimization passes. Every pass must respect the exceptional control flow edges. This inhibits the same optimizations listed under Model 1 (LICM, DSE, block merging, tail calls).
- **Extremely expensive throw path** — when an exception is actually thrown, the runtime must parse the `.eh_frame` tables, walk the stack, consult personality functions, and execute cleanup code. This is orders of magnitude slower than normal returns.

**Partial mitigation — `nounwind` on individual functions:**
LLVM's `PruneEH` pass (now part of function attribute inference) analyzes the call graph and adds `nounwind` to functions that provably cannot unwind. SimplifyCFG then converts their `invoke` calls into `call` instructions. This is effective for leaf functions and functions that only call other `nounwind` functions, but it requires whole-program visibility (or at minimum LTO). External functions without declarations cannot be proven `nounwind`.

**Language design implication:** If your language uses this model, aggressively annotate `nounwind` on functions that you know cannot throw — especially runtime library functions, builtins, and FFI-boundary functions. The optimizer can infer it for internal functions, but cannot do so for external ones.

### Model 3: Windows Structured Exception Handling (funclet model)

**What it means at the IR level:**
Instead of `landingpad`, Windows SEH uses `catchswitch`, `catchpad`, `cleanuppad`, and `catchret` instructions organized into "funclets." Each funclet is an independent region of code with its own scope.

```llvm
define void @example() personality ptr @__CxxFrameHandler3 {
entry:
  invoke void @may_throw()
    to label %cont unwind label %catch.dispatch

cont:
  ret void

catch.dispatch:
  %cs = catchswitch within none [label %handler] unwind to caller

handler:
  %cp = catchpad within %cs [ptr @_ZTIi]
  call void @handle_exception() [ "funclet"(token %cp) ]
  catchret from %cp to label %cont
}
```

**Optimization impact compared to DWARF model:**

- **Same `invoke` overhead** — Windows SEH still uses `invoke` instructions, so the CFG complexity costs are identical.
- **Funclet constraints are *more* restrictive** — LLVM enforces strict funclet nesting rules. Each funclet must have exactly one unwind destination. Code cannot be freely moved between funclets. This further limits code motion optimizations.
- **`catchswitch` is an opaque terminator** — unlike `landingpad` (which at least returns a value that can be inspected), `catchswitch` has no `nounwind` variant and creates control flow that is harder for passes to reason about.
- **No cross-funclet optimization** — LLVM explicitly does not optimize across funclet boundaries. Instructions cannot be hoisted from one funclet into another.

**Language design recommendation:** Only use the Windows SEH / funclet model if you must interoperate with Windows C++ exceptions or Windows SEH (`__try/__except`). It has strictly worse optimization characteristics than the DWARF model, and the DWARF model already has strictly worse characteristics than no-exceptions.

### Model 4: Setjmp/Longjmp (SJLJ)

**What it means at the IR level:**
Uses `llvm.eh.sjlj.setjmp` and `llvm.eh.sjlj.longjmp` intrinsics. Each function that does exception processing registers itself on a global frame list at runtime. Landing pad selection uses an index stored in the function context.

```llvm
; Conceptual SJLJ lowering (simplified)
define void @example() {
entry:
  %buf = alloca [5 x ptr]
  %status = call i32 @llvm.eh.sjlj.setjmp(ptr %buf)
  %is_exc = icmp ne i32 %status, 0
  br i1 %is_exc, label %handler, label %try_body

try_body:
  call void @may_throw()
  br label %done

handler:
  ; exception was caught
  br label %done

done:
  ret void
}
```

**This is the worst model for optimization by a significant margin:**

- **Runtime overhead on EVERY function entry/exit** — the function must register/deregister itself with the global frame list, and `setjmp` must save register state. This cost is paid whether or not an exception is thrown.
- **`setjmp` is a compiler barrier** — the LLVM optimizer treats `setjmp` as a call that may clobber all registers and local variables. Variables live across a `setjmp` must be spilled to the stack (not kept in registers). This cripples register allocation and SROA.
- **Landing pad dispatch uses a switch table** — adding overhead to the exceptional path as well.
- **Still uses `invoke`** — so all the CFG complexity costs of the DWARF model apply too.

The LLVM documentation explicitly states: SJLJ "results in faster exception handling at the expense of slower execution when no exceptions are thrown. As exceptions are, by their nature, intended for uncommon code paths, DWARF exception handling is generally preferred to SJLJ."

**Language design recommendation:** Never use SJLJ as your primary EH model. Its only legitimate use case is on platforms where DWARF unwind tables aren't available (certain embedded targets, or old 32-bit ARM).

---

## The Hybrid Approach: Result Types + Selective Unwinding

The best-performing language design for LLVM optimization is often a **hybrid** strategy:

1. **Normal error handling uses result types** (like Rust's `Result<T, E>`, Haskell's `Either`, or Go's multiple returns). These are represented as simple return values in LLVM IR — no `invoke`, no landing pads, no overhead.

2. **Truly unrecoverable errors** (out of memory, assertion violations, stack overflow) use one of:
   - **Process abort** — `call void @abort() noreturn nounwind`, never generates unwind infrastructure.
   - **Trap instruction** — `call void @llvm.trap() noreturn nounwind`, even lighter weight.
   - **Optional unwinding** — compile-time flag to choose between abort and unwind, like Rust's `panic=abort` vs `panic=unwind`.

```llvm
; Result-type error handling: zero EH overhead
define { i32, i1 } @parse_int(ptr %str) nounwind {
entry:
  ; ... parsing logic ...
  %valid = icmp ne i32 %result, -1
  %ret.0 = insertvalue { i32, i1 } undef, i32 %result, 0
  %ret.1 = insertvalue { i32, i1 } %ret.0, i1 %valid, 1
  ret { i32, i1 } %ret.1
}

; Unrecoverable error: just abort, no unwind infrastructure
define void @assert_valid(i1 %condition) nounwind {
entry:
  br i1 %condition, label %ok, label %fail

ok:
  ret void

fail:
  call void @llvm.trap() noreturn nounwind
  unreachable
}
```

**Why this is optimal for LLVM:**

- Every function can be `nounwind` because the language guarantees errors are returned as values, not thrown.
- No `invoke` instructions, no landing pads, no personality functions, no unwind tables (unless opted in for debugging).
- LLVM sees clean, straight-line CFGs and can apply all optimizations without restriction.
- The "error" return value is just data — it can be constant-folded, dead-code-eliminated, and inlined through normally.

---

## Case Studies: How C and Rust Handle This

Understanding how existing languages map to LLVM's EH infrastructure is instructive for new language design.

### What C Does

C has no language-level exception mechanism, so you might expect it to get `nounwind` everywhere for free. It nearly does — but with a caveat.

**Clang's actual behavior when compiling C on x86-64 Linux/macOS:**

```llvm
; Typical C function compiled by Clang on x86-64 Linux
define i32 @add(i32 %a, i32 %b) #0 {
  %sum = add nsw i32 %a, %b
  ret i32 %sum
}

attributes #0 = { nounwind uwtable }
```

Every C function gets `nounwind` (C functions cannot throw). All calls are `call` (never `invoke`), no landing pads exist, no personality function. The optimizer gets full freedom. However, Clang *also* emits `uwtable` on every function because the x86-64 System V ABI conventionally requires unwind tables so that `backtrace()`, profilers, and debuggers work. The `.eh_frame` section is still generated for every function, costing binary size even though no exception handling actually occurs.

**The exceptions to `nounwind` in C:** If you compile C with `-fexceptions` (which some projects do for interop with C++ or Objective-C), Clang drops `nounwind` from C functions because a C function might be in a call chain between a C++ thrower and a C++ catcher — the C frame needs unwind tables so the unwinder can pass through it. This is relatively rare but worth knowing about.

**C's `setjmp`/`longjmp`:** These are NOT modeled as LLVM's SJLJ exception handling. They're regular function calls in the IR. However, `setjmp` gets the `returns_twice` attribute, which severely constrains the optimizer around the setjmp call site (similar to SJLJ EH in practice, but only affecting functions that actually use it rather than every function in the program).

### What Rust Does

Rust has a two-mode system controlled by a compile-time flag.

**Mode 1: `panic=unwind` (the default)**

Rust uses DWARF/Itanium zero-cost exceptions under the hood to implement `panic!()` unwinding. Every function that could transitively reach a panic site uses `invoke` instead of `call`, and landing pads are generated for cleanup (running `Drop` destructors):

```llvm
; Rust function with panic=unwind
define void @process(ptr %vec) personality ptr @rust_eh_personality {
  invoke void @might_panic(ptr %vec)
    to label %cont unwind label %cleanup

cont:
  ret void

cleanup:
  %lp = landingpad { ptr, i32 }
    cleanup
  call void @drop_vec(ptr %vec)    ; run destructor
  resume { ptr, i32 } %lp
}
```

Every function that owns a value with a `Drop` impl and calls something that could panic gets this treatment. In practice, that's a lot of functions — almost any function that allocates or holds a `Vec`, `String`, `Box`, etc. However, Rust does mark functions `nounwind` when it can prove they won't panic (simple arithmetic, field access, functions with no panicking operations), and all `extern "C"` FFI functions are marked `nounwind` because unwinding across an FFI boundary is UB in Rust.

**Mode 2: `panic=abort`**

When compiled with `-C panic=abort`, rustc:

- Marks every function `nounwind`
- Emits `call` instead of `invoke` everywhere
- Drops all landing pad blocks
- Removes the `uwtable` attribute (on targets where it's not ABI-required)
- Replaces the panic machinery with a direct call to `abort()`

The resulting IR looks essentially like C — clean straight-line CFGs, no exceptional control flow.

**Rust's `Result<T, E>` for recoverable errors** compiles to a tagged union returned by value. At the LLVM IR level this is just a struct return — no exceptions involved:

```llvm
; Result<i32, Error> returned as a tagged struct
define { i64, i32 } @parse_number(ptr %input) nounwind {
  ; Return Ok(42):  tag=0, value=42
  ret { i64, i32 } { i64 0, i32 42 }
}
```

The `?` operator is just a conditional branch — check the tag, branch to early return or continue. No `invoke`, no landing pads. This is the part of Rust's error handling that has genuinely zero overhead.

### Performance Comparison

**C ≈ Rust (`panic=abort`) > Rust (`panic=unwind`)**

For the happy path (no errors), C and Rust-with-abort are essentially identical at the LLVM IR level. Both get `nounwind`, both use `call`, both have clean CFGs. The optimizer treats them the same way.

Rust `panic=unwind` is measurably slower than both:

- **Binary size:** ~10-15% larger due to landing pads and unwind tables for cleanup code. The Rust RFC for `panic=abort` measured 13% smaller binaries (15MB → 13MB for Cargo).
- **Compile time:** ~10% faster with abort because the compiler doesn't generate landing pad blocks and the optimizer doesn't reason about exceptional edges. The same RFC measured 11% faster compilation.
- **Runtime:** Harder to quantify in aggregate, but the mechanisms are clear — with `panic=unwind`, LICM is less effective in loops containing calls to panicking functions, DSE can't eliminate stores before potential panic points, and inlining produces more complex CFGs.

**A key nuance:** Rust's `Result`-based error handling (the common case) is already as fast as C's error handling. The `panic=unwind` overhead only applies to the panic infrastructure — bounds check failures, integer overflow in debug mode, `unwrap()` on `None`, etc. For well-written Rust code that primarily uses `Result` and `?`, the `invoke` instructions exist as "just in case" infrastructure for panics that rarely or never fire. Switching to `panic=abort` eliminates that infrastructure entirely.

**The practical takeaway for a new language:** If you adopt Rust's strategy (result types for normal errors + abort for bugs), you get C-equivalent LLVM optimization characteristics. If you want optional unwinding, follow Rust's two-mode approach — it's the most battle-tested design for this exact tradeoff.

---

## Detailed Optimization Impact: `invoke` vs `call`

To understand the full scope of what `nounwind` buys you, here's a systematic breakdown of every LLVM pass affected:

### SimplifyCFG
**Direct impact.** SimplifyCFG explicitly "changes invoke instructions to nounwind functions to be calls." Once the invoke is gone, SimplifyCFG can also merge the former normal-destination block with its predecessor (since the predecessor is no longer an `invoke` terminator) and eliminate the now-unreachable landing pad block.

### Inlining
**Major impact.** When a `call nounwind` is inlined, the inlined body is simply spliced into the caller's basic block. When an `invoke` is inlined, the inliner must:
- Remap all `invoke` instructions inside the inlined body to use the outer `invoke`'s unwind destination.
- Create new landing pad blocks or merge with existing ones.
- Handle PHI nodes at unwind destinations.
This makes inlining through `invoke` more expensive at compile time and produces more complex post-inline CFGs.

### LICM (Loop-Invariant Code Motion)
**Significant impact.** An `invoke` inside a loop creates an unwind edge that exits the loop. LICM must consider this edge as a potential write to memory (since the unwinding process may modify state). With `call nounwind`, the loop body has only the backedge and the normal exit, allowing LICM to hoist loads and sink stores more freely.

### Dead Store Elimination (DSE)
**Moderate impact.** A store followed by an `invoke` cannot be eliminated even if the store is dead on the normal path, because the unwind path might observe it (e.g., a destructor in a landing pad reading the stored value). With `call nounwind`, DSE sees a continuous basic block and can eliminate the store if it's overwritten later without any intervening read.

### GVN (Global Value Numbering) / SCCP
**Moderate impact.** Fewer basic blocks means fewer PHI nodes, which means more values can be tracked as single definitions rather than merged at join points.

### Tail Call Optimization
**Binary impact.** `invoke` cannot be a tail call. Only `call` can. If your language's control flow naturally produces tail-position calls (functional languages, state machines, interpreters), this is critical.

### Register Allocation (Backend)
**Indirect but real impact.** Fewer basic blocks means simpler liveness ranges, which means better register allocation with less spilling. Landing pad blocks that reference values from the normal path create long-lived values that may need to be spilled.

---

## Interaction with Other Attributes

The `nounwind` attribute has synergistic interactions with several other function attributes:

| Attribute Combination | Effect |
|---|---|
| `nounwind` + `readonly` | Function can be speculatively executed — LICM can hoist calls out of loops even if the loop might not execute them |
| `nounwind` + `willreturn` | Function is guaranteed to terminate normally — enables dead code elimination after the call if result is unused |
| `nounwind` + `nosync` | Function has no synchronization — combined with nounwind, enables aggressive reordering |
| `nounwind` + `norecurse` | Enables stack depth analysis and more precise alias analysis |
| `nounwind` + `mustprogress` | On loops: guarantees the loop will terminate or have a side effect — enables dead loop elimination |

---

## The `uwtable` Question

Even with `nounwind`, you may want unwind tables for debugging. LLVM provides two levels:

```llvm
; uwtable(async) — full tables, support asynchronous unwinding (signal handlers)
define void @f() nounwind uwtable(async) { ... }

; uwtable(sync) — minimal tables, only support synchronous unwinding (call frames)
define void @f() nounwind uwtable(sync) { ... }

; No uwtable — no tables at all, smallest binary, no backtrace support
define void @f() nounwind { ... }
```

The critical insight: `uwtable` and `nounwind` are orthogonal. `uwtable` controls table generation for the *backend*. `nounwind` controls the *optimizer's* knowledge. You can have both, and doing so gives you backtraces without sacrificing optimization.

### What the "ABI mandate" actually means

The x86-64 System V ABI specification says that functions *should* provide unwind information so the stack can be unwound through them. But this is not a hardware requirement or OS kernel requirement — it's a convention that other software relies on. Specifically, three things consume unwind tables:

1. **`backtrace()` / debugger stack walking.** Without unwind tables, a debugger trying to walk the stack through your function will stop or produce garbage.
2. **C++ exceptions unwinding through your code.** If someone calls your function from C++ and your function calls back into C++ code that throws, the unwinder needs to pass through your frame.
3. **Profilers and sampling tools.** `perf`, `dtrace`, Instruments, etc. use unwind tables to attribute samples to the correct call chain.

None of these affect the correctness of your program's own execution. A new language has full control over whether to honor this convention.

### Strategies for avoiding `uwtable` overhead

**Strategy 1: Just don't emit `uwtable`.** Nothing stops you. Your LLVM frontend simply doesn't add the attribute, and LLVM won't generate `.eh_frame` entries.

```llvm
; No uwtable — no .eh_frame for this function
define i32 @your_function(i32 %x) nounwind {
  %r = add nsw i32 %x, 1
  ret i32 %r
}
```

What breaks: `backtrace()` stops at your frames, profiler stack traces are truncated, C++ exceptions can't unwind through you. What doesn't break: your program runs correctly. The OS and kernel don't care.

**Who does this today:** Go omits standard `.eh_frame` tables and uses its own runtime metadata for stack walking. It has its own unwinder, its own backtrace mechanism, its own GC stack maps. This is a real, shipped, successful approach (though GDB support for Go was historically poor partly because of this).

**Strategy 2: Frame pointer-based unwinding.** Rather than unwind tables, guarantee that every function maintains a frame pointer (`rbp` on x86-64). Debuggers and profilers can walk the stack by following the `rbp` chain — no tables needed.

```llvm
; Frame pointer instead of uwtable — backtraces work, no .eh_frame
define i32 @your_function(i32 %x) nounwind "frame-pointer"="all" {
  %r = add nsw i32 %x, 1
  ret i32 %r
}
```

The tradeoff: you permanently sacrifice one general-purpose register (`rbp`). On x86-64, losing one of 16 GPRs is roughly a 6% reduction in available registers. In practice, this measures at 1-3% runtime overhead in register-pressure-heavy code.

**Who does this today:** Apple mandates frame pointers on all platforms (macOS, iOS) for both x86-64 and AArch64. Fedora Linux enabled frame pointers by default across their entire distribution. The performance cost has been measured extensively and deemed acceptable.

**Strategy 3: Custom metadata format.** Design your own stack map format that's cheaper than `.eh_frame`. The reason `.eh_frame` is expensive is that it's extremely general — it describes arbitrary register save/restore sequences, variable frame sizes, mid-function layout changes, etc. If your language has regular stack layouts, your metadata can be much more compact (e.g., a single integer per function for the frame size).

**Who does this:** Go, the JVM (JIT-compiled code has its own metadata), and many game engines with custom crash reporters.

**Strategy 4: Emit `uwtable` selectively.** Only emit `uwtable` on functions at the FFI boundary where foreign unwinders might need to pass through, and omit it for all internal functions.

```llvm
; Internal function — no uwtable, minimal binary
define internal i32 @internal_helper(i32 %x) nounwind {
  %r = add nsw i32 %x, 1
  ret i32 %r
}

; FFI boundary — uwtable for interop safety
define i32 @exported_to_c(i32 %x) nounwind uwtable {
  %r = call i32 @internal_helper(i32 %x)
  ret i32 %r
}
```

### Recommended build-mode strategy

The cleanest approach is to tie `uwtable` emission to the build mode. Debug builds already accept worse performance for better developer experience; release builds already sacrifice debuggability for speed. This fits naturally:

```llvm
; === Debug mode ===
; Full unwind tables + frame pointers. Backtraces work everywhere.
; Optimizer still gets full nounwind benefits.
define i32 @f(i32 %x) nounwind uwtable(sync) "frame-pointer"="all" { ... }

; === Release mode ===
; Frame pointers only. Backtraces still work (via rbp chain).
; No .eh_frame overhead. ~1-3% cost from lost register.
define i32 @f(i32 %x) nounwind "frame-pointer"="all" { ... }

; === Release (max performance) mode ===
; Nothing. Smallest binary, all 16 GPRs, no backtrace support.
define i32 @f(i32 %x) nounwind { ... }
```

This creates no semantic difference between build modes — the EH behavior is identical (no unwinding ever). The only difference is whether external tools can see into your stack. That's a tooling difference, not a behavioral one, which avoids the class of bugs where debug and release builds behave differently.

The three tiers:

| Mode | Attributes | Backtraces | Profiling | Binary Size | Runtime Cost |
|---|---|---|---|---|---|
| Debug | `nounwind uwtable(sync) "frame-pointer"="all"` | Full (DWARF + FP) | Full | Largest | ~1-3% (lost register) |
| Release | `nounwind "frame-pointer"="all"` | FP-chain only | Works | Smaller | ~1-3% (lost register) |
| Max Perf | `nounwind` | None | Limited | Smallest | None |

For most new languages, the release tier (frame pointers, no uwtable) is the sweet spot — you get production crash backtraces and profiler support at a small, well-understood cost, with none of the `.eh_frame` binary bloat. Users who need maximum performance or minimal binary size can opt into the third tier.

---

## Interaction with Calling Conventions

The EH model interacts with calling convention choice:

- **`fastcc` + `nounwind`:** This is the ideal combination for internal functions. `fastcc` enables register-based parameter passing and tail call optimization. `nounwind` ensures `invoke` isn't needed. Together, they enable the most aggressive inlining and tail call chaining.
- **`tailcc` + `nounwind`:** If your language guarantees tail calls (e.g., Scheme-like languages), `tailcc` + `nounwind` is the only way to get guaranteed TCO — `invoke` is incompatible with `musttail`, so exceptions and guaranteed tail calls are mutually exclusive.
- **At FFI boundaries:** Switch to `ccc` (C calling convention) and drop `nounwind` only if the foreign function might actually throw. If the foreign code is C (not C++), you can keep `nounwind`.

---

## Optimization Impact Ranking (within this category)

1. **No exceptions — `nounwind` everywhere** (Highest impact, ~10-20% code size, measurable runtime improvement in hot loops)
2. **Hybrid result-types + abort** (Same as #1 in practice — this is the recommended implementation strategy)
3. **DWARF zero-cost exceptions with aggressive `nounwind` inference** (Moderate impact — depends on how many functions can be proven `nounwind`)
4. **DWARF zero-cost exceptions without `nounwind` inference** (Significant overhead on all call-heavy code)
5. **Windows SEH/funclet model** (Same as #4 plus additional funclet constraints)
6. **SJLJ** (Worst — runtime overhead on every function entry, severe register allocation degradation)

---

## Actionable Language Design Recommendations

### If your language can avoid exceptions entirely (RECOMMENDED):

1. **Use result/option types for all recoverable errors.** Represent them as LLVM struct returns: `{ T, i1 }` or `{ T, ErrorEnum }`. This is what makes Rust's `Result`-based error handling as fast as C — the error path is just a conditional branch, not an exception.
2. **Use `call void @llvm.trap() noreturn nounwind` + `unreachable` for unrecoverable errors** (or `call void @abort() noreturn nounwind`).
3. **Mark every function `nounwind`.** This is the single most impactful EH-related optimization. It gets you C-equivalent LLVM optimizer behavior.
4. **Use the three-tier build-mode strategy for `uwtable`:**
   - Debug: `nounwind uwtable(sync) "frame-pointer"="all"` — full backtraces.
   - Release: `nounwind "frame-pointer"="all"` — frame-pointer backtraces, no `.eh_frame` bloat.
   - Max perf: `nounwind` — nothing, smallest binary, all registers available.
5. **Use `fastcc` for all internal functions** — with `nounwind`, this enables maximum tail call and inlining optimization.
6. **At FFI boundaries where the foreign code uses C++ exceptions:** wrap the foreign call in an `invoke` with a landing pad that catches and converts to your result type. Only that single wrapper function lacks `nounwind`. If the foreign code is C (not C++), keep `nounwind` — C functions cannot throw.

### If your language must support exceptions:

1. **Use the DWARF/Itanium model** (landingpad) on non-Windows, the funclet model on Windows.
2. **Annotate `nounwind` aggressively** on every function you know can't throw — builtins, trivial accessors, arithmetic, FFI calls to C functions.
3. **Provide a "no exceptions" compile mode** (like Rust's `panic=abort`) that flips all functions to `nounwind` and converts `invoke` to `call`. This gives users the choice.
4. **Minimize the scope of `try` blocks.** In codegen, only emit `invoke` for call instructions that are lexically inside a `try` — all other calls can use `call` if the function's own signature is `nounwind`.
5. **Consider making "throws" part of the function type** (checked exceptions). This lets you precisely annotate `nounwind` on functions that don't declare any thrown types, without needing whole-program inference.

### What NOT to do:

- **Don't use SJLJ** unless forced by platform constraints.
- **Don't mark functions `nounwind` if they can actually unwind** — this is instant UB. LLVM will optimize assuming no unwind, and if one happens, the behavior is completely undefined (not just "catches the wrong thing" — the stack may be corrupted).
- **Don't emit `invoke` for calls to `nounwind` functions** — the optimizer will clean this up (SimplifyCFG converts them to `call`), but emitting `call` directly saves compile time and produces cleaner IR from the start.
- **Don't assume "zero-cost" means "free"** — the binary size and optimizer overhead of unwind tables and `invoke` CFG complexity are real, measurable costs on the non-exceptional path.