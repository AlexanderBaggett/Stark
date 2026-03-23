# LLVM Calling Conventions: Language Design Reference

## Overview

A calling convention (CC) determines how function arguments are passed, how return values are delivered, which registers must be saved across calls, and whether tail call optimization is possible. For a language designer targeting LLVM, the calling convention choice is one of the most impactful decisions — it affects every single function call in the program and interacts with inlining, tail call optimization, register allocation, and code size.

The key insight: your language likely has two distinct worlds of function calls — internal calls (where both sides are under your control) and external/FFI calls (where you must match the platform ABI). You should use different conventions for each.

---

## The Calling Conventions

### 1. `ccc` — The C Calling Convention (Default)

**LLVM IR syntax:** (no annotation, or explicitly `ccc`)

```llvm
define i64 @foo(i64 %x, i64 %y) {
  ...
}

%r = call i64 @foo(i64 %a, i64 %b)
```

**What it does:** Matches the platform's native C ABI. On x86-64 System V, this means arguments in RDI, RSI, RDX, RCX, R8, R9 (integer) and XMM0-XMM7 (float), with return values in RAX/RDX and XMM0/XMM1. Callee-saved registers: RBX, RBP, R12-R15.

**Properties:**
- Supports varargs
- Tolerates prototype mismatches (like C does)
- Does NOT support guaranteed tail call optimization
- The platform ABI is completely fixed — LLVM cannot rearrange it

**When to use:** Only at FFI boundaries where you must be compatible with C code, shared libraries, or system calls.

**Optimization implications:** Because the register allocation is fixed by the platform ABI, LLVM cannot choose more optimal register assignments. The fixed set of callee-saved registers means functions must spill/restore RBX, RBP, R12-R15 if they use them, even for trivial leaf functions. Aggregate arguments (structs) follow platform-specific decomposition rules that may force values onto the stack unnecessarily.

**Language design note:** Rust currently uses the C calling convention for most internal calls, though it uses LLVM's ABI-level features (like decomposing structs into register-sized pieces) to improve on raw C behavior. There is active discussion in the Rust community about adopting a custom convention to avoid this limitation.


### 2. `fastcc` — The Fast Calling Convention

**LLVM IR syntax:**

```llvm
define fastcc i64 @internal_func(i64 %x, i64 %y) {
  ...
}

%r = call fastcc i64 @internal_func(i64 %a, i64 %b)
```

**What it does:** Gives LLVM freedom to pass arguments and return values however it wants, without conforming to any external ABI. The target can use any registers it chooses, pass more arguments in registers, and adjust callee-saved sets.

**Properties:**
- Does NOT support varargs
- Requires exact prototype match between caller and callee
- Enables tail call optimization (with `tail` marker)
- LLVM's `GlobalOpt` pass can automatically promote `internal` functions from `ccc` to `fastcc`

**Key optimization benefits:**

1. **More register arguments:** The target can pass more values in registers than the C ABI allows, reducing stack traffic.

2. **Tail call optimization:** The `tail` marker on a call is only optimized when using `fastcc`, `tailcc`, `ghccc`, or `preserve_nonecc`. With `ccc`, the `tail` marker is merely a hint that the callee doesn't access the caller's stack — it does NOT guarantee a tail call.

3. **Flexible register allocation:** The backend can choose a register assignment strategy optimal for the specific function, rather than being constrained by the platform ABI.

**The `tail` call marker with `fastcc`:**

```llvm
define fastcc i64 @factorial(i64 %n, i64 %acc) {
entry:
  %done = icmp eq i64 %n, 0
  br i1 %done, label %base, label %recurse

base:
  ret i64 %acc

recurse:
  %n1 = sub i64 %n, 1
  %acc1 = mul i64 %n, %acc
  %r = tail call fastcc i64 @factorial(i64 %n1, i64 %acc1)
  ret i64 %r
}
```

With `fastcc`, this compiles to an actual loop (jump back to function entry) rather than a recursive call. With `ccc`, the `tail` marker would be silently ignored on most targets.

**CRITICAL GOTCHA:** The calling convention must match on BOTH the function definition AND every call site. Mismatched conventions produce undefined behavior that LLVM exploits aggressively — the function body can be deleted entirely:

```llvm
; BUG: fastcc function called without fastcc → undefined behavior
define fastcc void @foo() { ret void }
define void @bar() {
  call void @foo()   ; WRONG: missing "fastcc" on call site
  ret void
}
; After optimization, @bar becomes unreachable!
```

This is a documented FAQ in LLVM. The IR verifier does NOT reject this as an error because there are legitimate cases where it appears in dead code after inlining.

**GlobalOpt automatic promotion:** LLVM's `GlobalOpt` pass automatically promotes `internal` linkage functions with `ccc` to `fastcc`, along with all their call sites. This means if your frontend emits internal functions with the default CC, LLVM will likely upgrade them anyway. However, there are conditions where this doesn't work — if the function's address is taken and used in contexts where the CC matters (indirect calls), or if the function is in `@llvm.used`. Explicitly emitting `fastcc` from the frontend is more reliable.

**Language design recommendation:** **Use `fastcc` for all internal function calls.** This is the single highest-impact calling convention decision. Only use `ccc` at FFI boundaries.


### 3. `tailcc` — Guaranteed Tail Call Convention

**LLVM IR syntax:**

```llvm
define tailcc i64 @state_machine(i64 %state, ptr %data) {
  ...
  %r = musttail call tailcc i64 @next_handler(i64 %new_state, ptr %data)
  ret i64 %r
}
```

**What it does:** Equivalent to `fastcc` but with an additional guarantee: calls in tail position WILL be tail call optimized. With plain `fastcc`, tail calls are best-effort. With `tailcc`, they are mandatory.

**Properties:**
- Same restrictions as `fastcc` (no varargs, exact prototype match)
- The callee pops its own arguments from the stack (callee-cleanup), which is what enables guaranteed TCO even when the callee needs more stack argument space than the caller
- Slightly different stack layout from `fastcc` to support the guarantee

**When to use:** When your language semantics REQUIRE tail calls to not consume stack space — not as an optimization, but for correctness. Examples: implementing loops via tail recursion (functional languages), state machine interpreters, continuation-passing style.

**Performance tradeoff:** The callee-cleanup convention has slightly different codegen than `fastcc`. For functions that are NOT tail-called, `tailcc` may produce marginally worse code than `fastcc` because of the different stack discipline. The historical LLVM discussion noted that `-tailcallopt` (the old mechanism for guaranteed tail calls via `fastcc`) "significantly pessimizes code in most cases" — the cost is in non-tail-call paths.

**Language design recommendation:** Use `tailcc` only for functions that are part of a tail-call chain where TCO is a correctness requirement. For general-purpose code where tail calls are merely a nice optimization, `fastcc` is better.


### 4. `coldcc` — Cold Calling Convention

**LLVM IR syntax:**

```llvm
define coldcc void @error_handler(i32 %code, ptr %msg) {
  ...
}

call coldcc void @error_handler(i32 42, ptr %errmsg)
```

**What it does:** Preserves as many registers as possible from the caller's perspective, under the assumption that this function is rarely called. Almost all registers become callee-saved, so the call site doesn't need to spill anything.

**Properties:**
- No varargs, exact prototype match required
- The inliner does NOT consider `coldcc` functions for inlining
- Callee-saved set is maximized — the callee bears the cost of saving/restoring
- Makes call sites very cheap (no register spills around the call)

**When to use:** Error handlers, assertion failure paths, logging functions on cold paths, any function that your language semantics indicate is rarely executed.

**Key interaction:** `coldcc` and the `cold` function attribute are complementary but different. The `cold` attribute affects inlining decisions and basic block placement. `coldcc` changes the actual register allocation convention. You typically want BOTH on cold functions:

```llvm
define coldcc void @panic(ptr %msg) #0 cold noreturn {
  ...
}
```

**Important:** GlobalOpt will NOT promote `coldcc` functions to `fastcc` — it recognizes that the explicit cold convention was intentional and respects it.

**Language design recommendation:** Mark panic/assertion/error paths as `coldcc`. The benefit is that hot calling code doesn't need to save/restore registers around calls to these functions.


### 5. `preserve_mostcc` — PreserveMost Calling Convention

**LLVM IR syntax:**

```llvm
define preserve_mostcc i1 @runtime_check(ptr %obj) {
  ...
}
```

**What it does:** Like `coldcc` in its register preservation (callee saves almost everything), but designed for HOT paths, not cold ones. On x86-64, the callee preserves all GPRs except R11 and return registers. Floating-point registers (XMM/YMM) are NOT preserved (they follow the platform convention).

**Properties:**
- Unlike `coldcc`, does NOT suppress inlining
- Currently supported on X86-64 and AArch64
- Arguments and return values are passed the same way as the C convention

**When to use:** Runtime support functions that are called frequently from hot code, where the runtime function's hot path is tiny (doesn't use many registers) but its cold path may call into other functions. The classic use case: runtime write barriers, type checks, or GC safe points where the common case is fast but the rare case is complex.

**The idea:** Your runtime function checks one condition and returns (fast path), or falls through to complex logic (slow path). The caller doesn't need to save any registers around the call because the callee promises to preserve them. If the callee does hit the slow path and needs to call other functions, it bears the cost of saving/restoring.

```llvm
; Hot loop with a runtime check that rarely takes the slow path
define void @process(ptr %arr, i64 %n) {
loop:
  ; ... lots of values live in registers ...
  %need_gc = call preserve_mostcc i1 @gc_safepoint()
  ; All our registers are still live! No spills needed.
  ; ... continue processing ...
}
```


### 6. `preserve_allcc` — PreserveAll Calling Convention

**LLVM IR syntax:**

```llvm
define preserve_allcc void @runtime_leaf(ptr %obj) {
  ...
}
```

**What it does:** Even more aggressive than `preserve_mostcc`. On x86-64, preserves ALL general purpose registers except R11, AND all floating-point registers (XMM/YMM). The callee must save absolutely everything it touches.

**When to use:** Leaf runtime functions that are guaranteed to not call any other functions. Since they don't call out, the save/restore cost is minimal (they only save registers they actually use), but the caller benefits enormously by not needing to spill anything.

**Language design recommendation:** Use for GC barriers, reference counting operations, or other runtime intrinsics that are leaves and called extremely frequently.


### 7. `preserve_nonecc` — PreserveNone Calling Convention (New, High Impact)

**LLVM IR syntax:**

```llvm
define preserve_nonecc void @interpreter_dispatch(ptr %pc, ptr %sp, ptr %frame) {
  ...
  musttail call preserve_nonecc void @next_opcode(ptr %new_pc, ptr %sp, ptr %frame)
  ret void
}
```

**What it does:** The opposite extreme from `preserve_allcc` — NO general purpose registers are callee-saved. All GPRs are caller-saved. Additionally, ALL GPRs are available for passing arguments (up to 12 on x86-64: R12, R13, R14, R15, RDI, RSI, RDX, RCX, R8, R9, R11, RAX — note: non-volatile registers are listed first in the assignment order). Floating-point registers still follow the C convention.

**Properties:**
- Currently supported on X86-64 and AArch64
- No callee-saved GPRs means functions have zero register save/restore overhead
- Up to 12 GPR arguments on x86-64 (vs. 6 in the C convention)
- Non-volatile registers (R12-R15) are assigned FIRST for arguments, meaning if a `preserve_nonecc` function calls a normal C function, the arguments it received in R12-R15 survive the call without being spilled

**Performance impact:** The protobuf team reported 3-10% improvement in protocol buffer parsing functions when using `preserve_nonecc` for hot tail-call chains.

**The interpreter use case:** This convention is transformative for bytecode interpreters implemented as tail-call chains. Each opcode handler is a separate function that tail-calls the next handler:

```llvm
; Interpreter state is passed entirely in registers
; PC in %r12, stack pointer in %r13, frame pointer in %r14, accumulator in %r15
define preserve_nonecc void @op_add(ptr %pc, ptr %sp, ptr %frame, i64 %acc) {
  ; Load operands from virtual stack
  %a = load i64, ptr %sp
  %sp1 = getelementptr i64, ptr %sp, i64 1
  %b = load i64, ptr %sp1
  %result = add i64 %a, %b
  
  ; Advance PC and dispatch
  %next_pc = getelementptr i8, ptr %pc, i64 1
  %opcode = load i8, ptr %next_pc
  %handler = getelementptr ptr, ptr @dispatch_table, i8 %opcode
  %target = load ptr, ptr %handler
  musttail call preserve_nonecc void %target(ptr %next_pc, ptr %sp1, ptr %frame, i64 %result)
  ret void
}
```

Without `preserve_nonecc`, each opcode handler would need to save/restore callee-saved registers, adding ~6 push/pop pairs per dispatch. With it, each handler is just the useful work plus a `jmp`.

**The "pinning" trick:** Because non-volatile registers (R12-R15) are assigned first as argument registers, if the `preserve_nonecc` function needs to call a normal C function in the middle, those first four arguments are automatically preserved across the call (since R12-R15 are callee-saved in the C convention). This means interpreter state can be "pinned" in registers across calls to helper functions.

**Language design recommendation:** Use `preserve_nonecc` for performance-critical internal dispatch loops, interpreter main loops, and hot tail-call chains. Combine with `musttail` for guaranteed tail calls. This is one of the highest-impact recent additions to LLVM's calling convention support.


### 8. `ghccc` — GHC Calling Convention

**LLVM IR syntax:**

```llvm
define ghccc void @stg_entry(i64 %r1, i64 %r2, ptr %sp, ptr %hp) {
  ...
}
```

**What it does:** Designed for the Glasgow Haskell Compiler. Passes everything in registers, disables all callee-saved registers, and supports register pinning for runtime components. Similar in spirit to `preserve_nonecc` but predates it and was the inspiration for it.

**Properties:**
- X86-32: up to 4 integer parameters, no floating point
- X86-64: up to 10 integer parameters and 6 floating point parameters
- AArch64 and RISC-V: also supported with target-specific limits
- Supports tail call optimization
- No callee-saved registers at all

**Limitations vs. `preserve_nonecc`:** `ghccc` was not exposed to Clang until `preserve_nonecc` was created as a more general alternative. `preserve_nonecc` is the recommended convention for new projects; `ghccc` exists mainly for GHC compatibility.

**Language design recommendation:** Prefer `preserve_nonecc` over `ghccc` for new language implementations, unless you specifically need GHC compatibility.


### 9. `swiftcc` and `swifttailcc` — Swift Calling Conventions

**LLVM IR syntax:**

```llvm
define swiftcc { i64, i64 } @swift_func(i64 %x) {
  ...
}

define swifttailcc void @swift_continuation(ptr %ctx) {
  ...
  musttail call swifttailcc void @next_continuation(ptr %new_ctx)
  ret void
}
```

**What `swiftcc` does:** On x86-64, makes RCX, R8, XMM2, and XMM3 available as additional return value registers. This allows returning up to 4 integer values and 4 floating-point values in registers, compared to the C convention's 2+2.

**What `swifttailcc` does:** Like `swiftcc` but with callee-cleanup stack discipline (like `tailcc`), enabling guaranteed tail calls. Created specifically for Swift's concurrency model where async functions resume via tail calls.

**Language design relevance:** If your language returns multiple values frequently (tuples, tagged unions, Result types), `swiftcc` gives you more return registers. The extra return registers can eliminate memory allocation for small multi-value returns.

```llvm
; C convention: can only return {i64, i64} in RAX, RDX
; swiftcc: can return {i64, i64, i64, i64} in RAX, RDX, RCX, R8
define swiftcc { i64, i64, i64, i64 } @multi_return() {
  ret { i64, i64, i64, i64 } { i64 1, i64 2, i64 3, i64 4 }
}
```

---

## The `tail`, `musttail`, and `notail` Call Markers

These are not calling conventions but interact critically with CC choice.

### `tail`

```llvm
%r = tail call fastcc i64 @callee(i64 %x)
ret i64 %r
```

**Semantics:** A hint that the callee does not access allocas or varargs of the caller. With `fastcc`/`tailcc`/`ghccc`/`preserve_nonecc`, LLVM will attempt to optimize this into a jump. With `ccc`, this is merely a hint for alias analysis (tells LLVM the callee won't access the caller's stack frame), but does NOT produce a tail call on most targets.

### `musttail`

```llvm
%r = musttail call fastcc i64 @callee(i64 %x)
ret i64 %r
```

**Semantics:** A GUARANTEE that this call will be tail-call optimized. If LLVM cannot honor this, compilation fails. Requirements:
- Must immediately precede a `ret` instruction (or a bitcast then `ret`)
- The `ret` must return the value from the call (or void)
- Caller and callee prototypes must match exactly
- Calling conventions must match
- All ABI-impacting attributes (sret, byval, inreg, etc.) must match

**Language design impact:** `musttail` is a correctness mechanism, not an optimization hint. Use it when stack overflow would result from not performing TCO. The strict prototype-matching requirement means your language needs to ensure tail-called functions have compatible signatures, or you need a uniform dispatch signature (like the interpreter pattern above).

### `notail`

```llvm
%r = notail call i64 @callee(i64 %x)
```

**Semantics:** Explicitly prevents tail call optimization even if it would otherwise be valid. Used when you need the caller's stack frame to persist (e.g., for stack traces, debugger support, or address sanitizer).

---

## Interaction Matrix: Calling Convention × Feature

| Convention | Tail Calls | Varargs | Prototype Match | Callee-Saved GPRs (x86-64) | Max GPR Args (x86-64) | Inlining |
|-----------|------------|---------|-----------------|---------------------------|----------------------|----------|
| `ccc` | Opportunistic only | Yes | Tolerant | RBX, RBP, R12-R15 (6) | 6 | Normal |
| `fastcc` | With `tail` marker | No | Exact | Target-chosen | Target-chosen | Normal |
| `tailcc` | Guaranteed | No | Exact | Minimal (for TCO) | Target-chosen | Normal |
| `coldcc` | No | No | Exact | Nearly all | 6 (C-like) | Suppressed |
| `preserve_mostcc` | No | No | Exact | All except R11 | 6 (C-like) | Normal |
| `preserve_allcc` | No | No | Exact | All except R11 + FP regs | 6 (C-like) | Normal |
| `preserve_nonecc` | With `musttail` | No | Exact | None | 12 | Normal |
| `ghccc` | With `tail` marker | No | Exact | None | 10 | Normal |
| `swiftcc` | No | No | Exact | Same as C | 6 (+ extra returns) | Normal |
| `swifttailcc` | Guaranteed | No | Exact | Minimal (for TCO) | 6 (+ extra returns) | Normal |

---

## Practical Language Design Strategy

### The Two-Convention Architecture

The simplest and most effective strategy:

```
┌─────────────────────────────────────────────────┐
│                Your Language                      │
│                                                   │
│  Internal calls: fastcc                           │
│  ┌───────────────────────────────────────────┐   │
│  │  fn process(data: &[u8]) -> Result {      │   │
│  │    // call fastcc @helper(...)            │   │
│  │    // call fastcc @transform(...)         │   │
│  │  }                                        │   │
│  └───────────────────────────────────────────┘   │
│                                                   │
│  FFI boundary: ccc                                │
│  ┌───────────────────────────────────────────┐   │
│  │  extern "C" fn exported_api(x: i32) {     │   │
│  │    // visible to C code, uses ccc         │   │
│  │  }                                        │   │
│  └───────────────────────────────────────────┘   │
└─────────────────────────────────────────────────┘
```

**Rule 1:** All functions that are never exposed to foreign code get `fastcc`. This is the vast majority of functions in most programs.

**Rule 2:** Functions exposed at FFI boundaries get `ccc`. The frontend inserts thin wrappers if needed to translate between conventions.

**Rule 3:** If the function is cold (error handling, panic), use `coldcc`.

**Rule 4:** If your language guarantees tail calls (functional language, CPS), use `tailcc` for the functions that participate in those guarantees.

### The Interpreter/Dispatch Pattern

For languages that include an interpreter or use computed-goto-style dispatch:

```llvm
; All opcode handlers share this signature
; preserve_nonecc + musttail = maximum performance dispatch
define preserve_nonecc void @op_load(ptr %pc, ptr %sp, ptr %env, i64 %acc) {
  ; ... do the work ...
  %next = ; compute next handler
  musttail call preserve_nonecc void %next(ptr %new_pc, ptr %sp, ptr %env, i64 %result)
  ret void
}
```

This pattern eliminates all function call overhead: no prologue, no epilogue, no register saves, no stack frame. Each "function" compiles to just its body followed by a `jmp`.

### Handling Indirect Calls and Function Pointers

Indirect calls (function pointers, closures, virtual dispatch) require that the calling convention is known at the call site. Two approaches:

**Approach A: Use `fastcc` for all indirect calls within the language.** Since you control both sides, all function pointers are `fastcc`. This works well but prevents using standard C function pointers directly.

**Approach B: Use `ccc` for indirect calls.** Simpler, compatible with C function pointers, but loses the `fastcc` benefits. This is what Rust effectively does today.

**Approach C: Dual dispatch.** Internal indirect calls use `fastcc`; FFI function pointers use `ccc`. The type system distinguishes between them (like Rust's `fn` vs `extern "C" fn`).

### The Multiple Return Value Opportunity

If your language has native tuple types, Result/Either types, or multi-return functions, consider `swiftcc`:

```llvm
; With ccc: the {i64, i64, i64} return must go partially to stack
; With swiftcc: all three values fit in RAX, RDX, RCX
define swiftcc { i64, i64, i64 } @divide(i64 %a, i64 %b) {
  %q = sdiv i64 %a, %b
  %r = srem i64 %a, %b
  %ok = icmp ne i64 %b, 0
  %ok_ext = zext i1 %ok to i64
  %res = insertvalue { i64, i64, i64 } undef, i64 %q, 0
  %res1 = insertvalue { i64, i64, i64 } %res, i64 %r, 1
  %res2 = insertvalue { i64, i64, i64 } %res1, i64 %ok_ext, 2
  ret { i64, i64, i64 } %res2
}
```

However, `swiftcc` can be combined with `fastcc` benefits — they're not mutually exclusive concepts at the design level, but they are distinct LLVM CCs. If your primary concern is register argument passing and tail calls, `fastcc` is the better base; if you need extra return registers, `swiftcc` wins there.

---

## Advanced Topics

### Register Pressure and Callee-Save Tradeoffs

The choice of callee-saved registers is fundamentally a tradeoff:

**More callee-saved registers (toward `preserve_allcc`):**
- Caller doesn't need to save values across calls → smaller call sites
- Callee must save/restore every register it uses → larger function prologues
- Best when: callee is small/leaf, caller has many live values

**Fewer callee-saved registers (toward `preserve_nonecc`):**
- Callee has no save/restore overhead → smaller functions
- Caller must save all live values across calls → more spills at call sites  
- Best when: functions are very small (dispatch handlers), or only called via tail calls (no caller to spill)

**The `fastcc` sweet spot:** LLVM gets to choose a balance, typically similar to the C convention but with freedom to adjust per-target.

### Function Pointer Tables and Dispatch

When building vtables or dispatch tables with non-C conventions, every entry and every call site must use the same convention. A common pattern:

```llvm
@vtable = internal constant [3 x ptr] [
  ptr @method_a,  ; all fastcc
  ptr @method_b,
  ptr @method_c
]

; dispatch
%slot = getelementptr ptr, ptr @vtable, i64 %method_idx
%fn = load ptr, ptr %slot
%r = call fastcc i64 %fn(ptr %self, i64 %arg)
```

### LTO Interaction

With Link-Time Optimization (LTO), LLVM can see across module boundaries and can promote `internal` functions to `fastcc` even when they're defined in separate compilation units. However, explicitly emitting `fastcc` from the frontend is still preferable:
- It works without LTO enabled
- It ensures correctness from the start rather than relying on optimization
- It avoids the window where pre-LTO object code uses the wrong convention

### Interaction with Exception Handling

If your language uses no exceptions (all functions are `nounwind`), calling convention choice is simpler:
- No need for `invoke` instructions (use `call` everywhere)
- No landing pads to worry about with tail calls
- `musttail` is easier to satisfy since there are no cleanup actions

If your language DOES have exceptions, `musttail` calls cannot be inside a `try`/`invoke` because the exception-catching mechanism requires the caller's stack frame to exist. This fundamentally conflicts with guaranteed TCO.

### The `cxx_fast_tlscc` Convention

Mentioned for completeness: Clang generates this for C++ thread-local storage access functions. It has a fast path (check if TLS is initialized) and slow path (initialize it). Unlikely to be relevant for a new language unless you're implementing TLS in a specific way.

---

## Impact Ranking

From highest to lowest impact for a new language:

1. **`fastcc` for all internal calls** — The single most impactful decision. Enables tail calls, gives LLVM freedom for register allocation, passes more arguments in registers. Almost zero downside.

2. **`preserve_nonecc` for hot dispatch loops** — Transformative for interpreters and state machines. 3-10% measured improvement in real-world code (protobuf). Eliminates all calling overhead in tail-call chains.

3. **`coldcc` for error/panic paths** — Easy to implement, immediately reduces register pressure in hot code. Works especially well with `nounwind` on the cold functions.

4. **`musttail` for guaranteed tail calls** — If your language promises tail call elimination, this makes it real. Correctness feature, not just performance.

5. **`swiftcc` for multi-return values** — Valuable if your language returns tuples or Result types frequently. Extra return registers eliminate memory traffic.

6. **`preserve_mostcc` / `preserve_allcc` for runtime intrinsics** — Niche but valuable for runtime-heavy languages (GC'd languages, languages with write barriers or type checks on hot paths).

7. **`tailcc` for guaranteed-TCO functions** — Only needed when TCO is a language-level guarantee, not just an optimization.

---

## Gotchas and Known Issues

1. **Convention mismatch is silent UB.** LLVM will not warn you. The function may simply be deleted after optimization. Always ensure CC matches between definition and all call sites.

2. **`fastcc` on x86-64 with >8 stack-passed arguments has had bugs.** There was a confirmed LLVM issue (GitHub #60972) where `fastcc` with 9+ arguments on AArch64 produced incorrect spill code. Test edge cases thoroughly.

3. **`preserve_nonecc` is x86-64 and AArch64 only.** If your language targets other architectures, you need a fallback strategy.

4. **`musttail` requires exact signature match.** This is stricter than most language designers expect. You cannot musttail-call a function with a different signature, even if the target platform could handle it. There is ongoing discussion about relaxing this (a proposed `nonportable_musttail`).

5. **Indirect `musttail` calls require the function pointer type to exactly match.** This is critical for dispatch tables — all functions in the table must have the exact same type.

6. **`coldcc` suppresses inlining.** Don't use it on functions you expect to be inlined. If a function is sometimes cold and sometimes hot, leave it as `fastcc` and use the `cold` attribute plus branch weights instead.

7. **GlobalOpt will promote `ccc` → `fastcc` but NOT `coldcc` → `fastcc`.** If you explicitly set `coldcc`, it sticks. Same for any other non-default CC.

8. **`tail` without `fastcc`/`tailcc` is almost useless for TCO.** On most targets, `tail call ccc` will NOT produce a tail call. The `tail` marker with `ccc` only tells LLVM the callee doesn't access the caller's alloca — it's an alias analysis hint, not a TCO directive.
