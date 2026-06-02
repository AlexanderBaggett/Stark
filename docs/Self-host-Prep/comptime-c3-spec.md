# Stark Design Specification: Compile-Time Evaluation (C3 Model)

**Status:** Draft / Proposal
**Feature:** `comptime` blocks and `comptime` expressions
**Model:** C3 — call-site / context-based compile-time evaluation

---

## 1. Summary

This document specifies compile-time evaluation (CTFE) in Stark using the **C3
model**: comptime-ness is a property of a *context* (a block or an expression),
not of a function declaration. Any expression or block may be forced to evaluate
during compilation by prefixing it with the `comptime` keyword. The const-eval
engine runs ordinary Stark code "early," under the same type, ownership, and
contract rules that apply at run time.

The guiding principle: **`comptime` is a time selector, not a restricted
sublanguage.** Code inside a `comptime` context obeys exactly the rules it would
obey at run time. Comptime introduces no new prohibitions of its own except the
two that have no runtime analog — a **termination budget** and **input
availability** — both detailed in Section 7.

```stark
fn BuildTable(): [256]u8
{
    comptime
    {
        let mut table: [256]u8;
        for (let i in 0..256)
        {
            table[i] = ComputeCrc(i);
        }
        return table;
    }
}

let x: i32 = comptime ExpensiveConst();
```

---

## 2. Motivation

Stark already reuses one language across its function kinds (`fn`, `finite`,
`law`, `finite law`) rather than bolting on dialects. C3 extends that instinct to
metaprogramming: there is no separate "comptime language." A programmer writes
normal Stark and selects *when* it runs.

Compared to declaration-site comptime (marking whole functions `comptime`), the
C3 model:

- **Reuses the language verbatim.** No comptime-only constructs; the const-eval
  engine interprets ordinary Stark.
- **Maximizes flexibility.** The same function may be folded at one call site and
  emitted for runtime at another, with no duplication and no signature churn.
- **Minimizes spec surface.** The set of genuinely comptime-specific rules is
  small (Section 7). Everything else is "the normal rules, applied earlier."
- **Improves constant propagation.** Opportunistic folding at `comptime` sites
  can collapse call sites to constants, reducing emitted runtime bodies.

The tradeoff, accepted deliberately, is that comptime-ness is **not visible in a
function's signature**. A reader cannot tell from a declaration whether a
function is comptime-safe; that is discovered at the call site during evaluation.
This places a high bar on diagnostic quality (Section 8).

---

## 3. Surface Syntax

### 3.1 `comptime` block

A `comptime` block is a statement-position block that is fully evaluated during
compilation. It follows Stark's Allman brace style and 4-space indentation.

```stark
comptime
{
    // ordinary Stark statements, evaluated at compile time
}
```

A `comptime` block may appear:

- as a statement within a function body,
- as the sole expression producing a value (when it ends in `return`, see 3.3),
- at module scope, to compute a module-level constant or run a build-time effect.

### 3.2 `comptime` expression

The `comptime` keyword may prefix any single expression, forcing that expression
to be evaluated during compilation:

```stark
let x: i32 = comptime ExpensiveConst();
let n: usize = comptime (Width * Height);
```

The result is a compile-time constant of the expression's ordinary type.

### 3.3 Value-producing `comptime` blocks

A `comptime` block used in value position yields a value via `return`, exactly as
a function body does. The block's value type is the type of the returned
expression:

```stark
let table: [256]u8 = comptime
{
    let mut t: [256]u8;
    for (let i in 0..256)
    {
        t[i] = ComputeCrc(i);
    }
    return t;
};
```

A value-producing `comptime` block in statement position without a binding is a
warning (computed value discarded), consistent with Stark's treatment of unused
values elsewhere.

### 3.4 Statement-position effects

A `comptime` block or expression in statement position may run for its effect
alone:

```stark
comptime BuildLog::Write("generating table\n");
```

This is permitted (Section 6). It executes the effect *during compilation*.

---

## 4. Semantics

### 4.1 Evaluation timing

`comptime` contexts are evaluated by the const-eval engine during the
front/middle end, **before** code generation. The engine is an interpreter over
the same typed IR the rest of the compiler uses.

Evaluation order within a `comptime` context follows ordinary Stark evaluation
order. Across multiple `comptime` contexts, evaluation follows dependency order:
a `comptime` constant required to type or size another construct is evaluated
first.

### 4.2 Result materialization

The output of evaluating a value-producing `comptime` context is a concrete
value, materialized as a compile-time constant:

| Comptime result | Materialized as |
|---|---|
| Scalar (`i32`, `u8`, range type, …) | immediate / named constant |
| Aggregate (array, struct) | constant aggregate / global constant |
| Type value (if/when supported) | resolved concrete type; comptime-ness erased |

By the time codegen runs, no trace of `comptime` remains in the IR handed to the
backend (Section 9).

### 4.3 Rule inheritance

Code in a `comptime` context is subject to **all** ordinary Stark rules:

- **Type checking** — identical. Binding a `void`-typed effect call to an `i32`
  is a type error whether or not `comptime` precedes it. No comptime-specific
  rule is needed; the ordinary checker rejects it.
- **Ownership / borrow** — identical. Moves, borrows, and lifetimes are enforced
  during const-eval as written.
- **Contracts** — identical. Loop contracts, overlap contracts, and function
  contracts (`finite`, `law`, etc.) must hold during evaluation.

The engine does not relax or special-case any of these. It enforces them on the
comptime timeline.

---

## 5. Interaction with Function Kinds

C3 is **orthogonal** to the fn/finite/law axis. `comptime` is not a function kind
and does not appear in signatures. Any function may be invoked from a `comptime`
context, subject to the rules below.

| Called function kind | Behavior in `comptime` context |
|---|---|
| `law` / `finite law` (pure, deterministic) | Always foldable when inputs are compile-time-known. Ideal comptime callees. |
| `finite` (terminating) | Foldable when pure w.r.t. compile-time-known inputs; termination is already guaranteed, easing budget concerns. |
| `fn` (general) | Foldable if, during evaluation, it neither requires unavailable inputs (7.2) nor exceeds the budget (7.1). May perform effects in statement position (Section 6). |

A `comptime` call graph that bottoms out in compile-time-known leaves and pure
operations always folds. This lets the engine phrase many failures in terms
Stark programmers already know (e.g. "this depends on a value not available at
compile time") rather than a comptime-specific vocabulary.

---

## 6. Effects at Compile Time

Stark permits effectful operations inside `comptime` contexts. Compile-time
execution is real execution; running an effect during compilation is a coherent,
if niche, activity (code generation, build manifests, progress logging).

**The distinction is value vs. effect, enforced by the ordinary type system —
not by a comptime blocklist:**

```stark
// OK: effect in statement position, run during compilation
comptime BuildLog::Write("generating table\n");

// ERROR: ordinary type error — Write yields no i32 to bind.
// The `comptime` is irrelevant to the diagnosis.
let x: i32 = comptime BuildLog::Write("...");
```

The second line fails for exactly the reason it would fail at run time: a
`void`-returning (or byte-count-returning) effect call does not produce an `i32`.
There is no special comptime rule; the type checker already covers it.

### 6.1 Origin-of-value lint

Some operations *denote a value* but read it from the **build-time** environment
rather than the **run-time** environment — e.g. reading a clock, an environment
variable, or a file. These are legal and sometimes intended (embedding build
metadata such as a build timestamp or commit hash).

Because the value's origin (build machine, now) differs from what a programmer
might expect (user's machine, at run time), the compiler **SHOULD** emit a lint:

```
warning[W-COMPTIME-ORIGIN]: this freezes a build-time value into the binary
  --> src/Config.stark:12:22
   |
12 |     let seed: u32 = comptime Os::ReadEnv("CRC_SEED");
   |                     ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ reads the build
   |                     machine's environment now, not the program's at run time
   = note: this is valid and useful for build metadata; silence with
           an explicit `@build_value` annotation if intended
```

This is a **warning, not an error.** The compiler is correct either way; only the
programmer's expectation can be wrong.

---

## 7. Comptime-Specific Constraints

These are the only two constraints with no runtime analog. Everything else
reduces to ordinary Stark rules applied earlier.

### 7.1 Termination budget

At run time, a nonterminating loop is the user's problem. At compile time, a
nonterminating evaluation hangs **the compiler**, which is the compiler's
problem. Therefore const-eval runs under a **step budget.**

- The engine maintains a monotonic step counter over each top-level `comptime`
  evaluation.
- Exceeding the budget is a hard error (8.2) that reports the offending
  loop/recursion site and, where cheaply available, the offending value on the
  final step.
- The default budget is implementation-defined and configurable per evaluation
  via `@comptime_budget(N)` and project-wide via build configuration.
- `finite` and `finite law` callees carry a termination guarantee and so are
  least likely to hit the budget; the engine MAY relax counting for provably
  finite sub-evaluations.

### 7.2 Input availability

A `comptime` context may only read values that exist at evaluation time. Inputs
that come into existence only at run time are **not in scope** on the comptime
timeline:

- the runtime program's command-line arguments / `stdin`,
- a dereference of a pointer into runtime-allocated memory,
- any binding whose value is not a compile-time constant at the point of use.

Referencing such an input from a `comptime` context is a hard error (8.3). This
is essentially an ordinary scoping/lifetime error surfaced on the comptime
timeline: the input is not *prohibited*, it simply does not yet exist.

---

## 8. Diagnostics

Because C3 hides comptime-ness from signatures, diagnostic quality is the
load-bearing element of the design. A const-eval failure MUST read like a *trace
through evaluation*: trigger site, evaluation trail, and the precise blocking
operation with a concrete fix.

### 8.1 General shape

```
error[E-COMPTIME-xxxx]: <one-line classification>
  --> <trigger site>: required to be a compile-time constant here

note: comptime evaluation trail
   ↳ Frame A    file:line   (entered here)
   ↳ Frame B    file:line
   ↳ Frame C    file:line   (got stuck here)

  --> <blocking site>: <precise reason>
   = reason: <names the actual violated principle>

help: choose one
   = <fix A: make the dependency compile-time-known>
   = <fix B: move the work to run time / drop `comptime`>
```

The reader's eye lands first on their own `comptime`, the **trail** explains *why*
evaluation reached the wall, and the deepest frame carries the most detail.

### 8.2 Budget / termination failure

Replaces the call trail with the loop/recursion site, the step count vs. limit,
and (where cheap) the offending value:

```
error[E-COMPTIME-BUDGET]: compile-time evaluation exceeded its step budget
  --> src/Table.stark:9:5
   |
 9 |     for (let i in 0..n)
   |     ^^^^^^^^^^^^^^^^^^^ evaluating this loop
   |
   = const-eval exceeded 1_000_000 steps
   = the bound `n` was 4_294_967_295 on the final step — likely an
     unintended runtime-sized bound
   = note: raise the limit with `@comptime_budget(N)` if this is intended
```

### 8.3 Availability failure

```
error[E-COMPTIME-UNAVAILABLE]: value is not available at compile time
  --> src/Args.stark:4:24
   |
 4 |     let p: u16 = comptime ParsePort(Env::Args()[1]);
   |                           ^^^^^^^^^^^^^^^^^^^^^^^^^^ depends on `Env::Args()`
   |
   = reason: `Env::Args()` yields the running program's arguments, which do
             not exist during compilation
   = note: const-eval reached this 1 call deep from the `comptime` site
help: pass the value as a compile-time input instead, or evaluate at run time
   = let p: u16 = ParsePort(Env::Args()[1]);   // run-time
```

### 8.4 Dual-use clarification

When a function is used at both comptime and runtime sites and only the comptime
use fails, the diagnostic MUST say so, to prevent the reader concluding the
function is globally broken:

```
   = note: `BuildTable` is fine at run time (called at src/Main.stark:9);
           only its compile-time use at line 14 fails
```

---

## 9. Backend / LLVM Lowering

C3 has **no effect on backend fact emission.** By the time codegen runs, every
`comptime` context has been reduced to concrete values and concrete (already
monomorphized) types. The word `comptime` never reaches LLVM.

- A `comptime` array lowers to an LLVM constant aggregate / global constant.
- A `comptime` scalar lowers to an immediate or named constant.
- A comptime-resolved type is lowered as an ordinary concrete type.
- A function used **only** at comptime need not be codegen'd at all.
- A function used at **both** comptime and runtime is codegen'd normally for its
  runtime call sites; its comptime call sites are already folded.

Backend facts that matter to Stark — alignment, layout, ownership-derived
`noalias`/aliasing attributes, and the constant values themselves — are computed
from the **resolved** types and values, which are identical regardless of whether
comptime-ness was declared or selected at the call site. C3 vs. a
declaration-site model makes no difference to the backend.

The only backend-adjacent consequence is favorable and quantitative: more call
sites may collapse to constants, so fewer runtime function bodies may be emitted.
This is an optimization outcome, not a correctness or fact-emission difference.

---

## 10. Examples

### 10.1 Compile-time lookup table

```stark
fn BuildTable(): [256]u8
{
    comptime
    {
        let mut table: [256]u8;
        for (let i in 0..256)
        {
            table[i] = ComputeCrc(i);
        }
        return table;
    }
}
```

### 10.2 Compile-time constant expression

```stark
let x: i32 = comptime ExpensiveConst();
let bufferSize: usize = comptime (PageSize * PageCount);
```

### 10.3 Build metadata (origin lint applies)

```stark
@build_value
let builtAt: u64 = comptime Clock::UnixSeconds();   // intended build-time value
```

### 10.4 Effect in statement position

```stark
comptime BuildLog::Write("generating CRC table\n");
```

### 10.5 Rejected by ordinary type checking

```stark
let n: i32 = comptime BuildLog::Write("oops");   // ERROR: void/byte-count is not i32
```

---

## 11. Open Questions

1. **Default budget value.** What step count balances ergonomics against compiler
   responsiveness for typical Stark workloads?
2. **`@build_value` annotation.** Is an explicit opt-in annotation the right way
   to silence the origin lint, or should a dedicated `BuildTime::*` module be the
   sanctioned source of build-origin values?
3. **Type values.** This spec leaves first-class `type`-returning comptime
   (C4-style generics) out of scope. If pursued later, it layers on C3 without
   altering the rules here, but error-locality for generated types needs its own
   treatment.
4. **Caching.** Should identical `comptime` evaluations be memoized across call
   sites / compilation units, and what are the determinism requirements for that
   to be sound?
5. **Cross-module comptime effects.** Ordering and idempotency guarantees (if
   any) for build-time effects spanning modules.

---

## 12. Summary of Rules

1. `comptime` selects *when* code runs; it adds no sublanguage.
2. All ordinary type, ownership, and contract rules apply unchanged during
   evaluation.
3. Value-vs-effect is enforced by the ordinary type checker, not a comptime
   blocklist.
4. Effects in statement position are permitted at compile time.
5. Build-origin value reads are legal but linted for expectation mismatch.
6. The only comptime-specific hard errors are **budget** (don't hang the
   compiler) and **availability** (don't read what doesn't exist yet).
7. Diagnostics trace the evaluation path, classify the wall, and offer a dual
   fix.
8. The backend never sees `comptime`; it receives concrete values and types.
