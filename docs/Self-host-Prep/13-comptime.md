# Phase 13 - Compile-Time Evaluation (`comptime`)

Status: **design proposal (C3 model) — drafted by Alexander; awaiting full design
lock. No implementation.** Module-scope semantics locked 2026-06-02 (§4.1): const
and static initializers are implicit comptime contexts (no keyword), and
free-standing module-scope `comptime` blocks do not exist. Original draft preserved
in git history (`comptime-c3-spec.md`, commit `d6d1ed6`).

The guiding principle: **`comptime` is a time selector, not a restricted
sublanguage.** Comptime-ness is a property of a *context* (a block or an
expression), never of a function declaration. Any expression or block may be forced
to evaluate during compilation by prefixing it with the `comptime` keyword; the
const-eval engine runs ordinary Stark code "early," under the same type, ownership,
and contract rules that apply at run time. Comptime introduces no new prohibitions
of its own except the two that have no runtime analog — a **termination budget**
and **input availability** (§8).

```stark
fn u8[0 max][256] BuildCrcTable()
{
    comptime
    {
        stack mut u8[0 max][256] table = {};
        for willexit (stack mut i64[0 max] index = 0; index < 256; index += 1)
        {
            table[index] = ComputeCrc(index);
        }

        return table;
    }
}

stack i32[min max] x = comptime ExpensiveConst();
```

## 1. Goal

Close `L09` (general compile-time function evaluation / table generation): give
Stark a way to compute lookup tables, derived constants, and configuration data at
compile time by running ordinary Stark code, instead of hand-maintaining precomputed
literals or pushing the work to program startup.

This phase delivers:

1. `comptime` blocks — statement-position blocks fully evaluated during compilation,
2. `comptime` expressions — any expression forced to a compile-time constant,
3. the const-eval engine extensions, budget/availability rules, and diagnostics
   that make the above predictable.

## 2. Current State (verified)

Stark already evaluates a useful subset of code at compile time; C3 generalizes an
existing capability rather than introducing a new phase:

- `const` declarations (local and global) require compile-time-constant
  initializers, folded by `CompileTimeExpressionEvaluator`
  (`src/Compiler/CompileTimeExpressionEvaluator.cs`).
- Integer range bounds are compile-time expressions today: `u64[0 2 ** 63 - 1]`
  evaluates `2 ** 63 - 1` during type checking.
- Constant text interpolation (`$"..."` with compile-time holes) folds to literals.
- The C#-style innate attribute registry exists (established by `[Ok]`/`[Err]`,
  doc `11`), giving `[ComptimeBudget(...)]` / `[BuildValue]` a home.
- What does **not** exist: calling functions at compile time, comptime loops,
  comptime aggregate construction, or any user-facing keyword to request early
  evaluation.

## 3. The C3 Model

Stark already reuses one language across its function kinds (`fn`, `finite`, `law`,
`finite law`) rather than bolting on dialects. C3 extends that instinct to
metaprogramming: there is no separate "comptime language." A programmer writes
normal Stark and selects *when* it runs.

Compared to declaration-site comptime (marking whole functions `comptime`), the C3
model:

- **Reuses the language verbatim.** No comptime-only constructs; the const-eval
  engine interprets ordinary Stark.
- **Maximizes flexibility.** The same function may be folded at one call site and
  emitted for runtime at another, with no duplication and no signature churn.
- **Minimizes spec surface.** The set of genuinely comptime-specific rules is small
  (§8). Everything else is "the normal rules, applied earlier."
- **Improves constant propagation.** Opportunistic folding at `comptime` sites can
  collapse call sites to constants, reducing emitted runtime bodies.

The tradeoff, accepted deliberately: comptime-ness is **not visible in a function's
signature**. A reader cannot tell from a declaration whether a function is
comptime-safe; that is discovered at the call site during evaluation. This places a
high bar on diagnostic quality (§9).

## 4. Surface Design

### 4.1 `comptime` block

A statement-position block fully evaluated during compilation:

```stark
comptime
{
    // ordinary Stark statements, evaluated at compile time
}
```

A `comptime` block may appear:

- as a statement within a function body,
- as the whole initializer of a binding (when it ends in `return`, see 4.3).

**Module scope (locked 2026-06-02).** Module scope needs no `comptime` keyword and
gets no new grammar:

- **Const and static initializers are implicit comptime contexts.** They already
  must be compile-time evaluable; the C3 engine simply makes them more powerful.
  Calling a function in a module-level initializer just works:

  ```stark
  const u8[0 max][256] CrcTable = BuildCrcTable();        // const-evaluated, no keyword
  static const Config DefaultConfig = LoadDefaults();      // same
  ```

  Writing `comptime` inside an already-comptime context is redundant and is linted
  as such (the keyword is a no-op there, like redundant parentheses).

- **Free-standing module-scope `comptime` blocks do not exist.** Every compile-time
  evaluation has an owner — the constant, static, or function-body context it
  initializes. This keeps evaluation ordering equal to ordinary dependency order,
  gives memoization a key, and avoids anonymous build-time effects (a build-tool
  concern, not a language concern). Build-time effects are still possible — inside
  an owned context (§4.4, §7).

### 4.2 `comptime` expression

The `comptime` keyword may prefix any single expression, forcing it to a
compile-time constant of its ordinary type:

```stark
stack i32[min max] x = comptime ExpensiveConst();
stack u64[0 max] bufferSize = comptime (PageSize * PageCount);
```

### 4.3 Value-producing `comptime` blocks

A `comptime` block in value position yields its value via `return`, exactly as a
function body does:

```stark
stack u8[0 max][256] table = comptime
{
    stack mut u8[0 max][256] entries = {};
    for willexit (stack mut i64[0 max] index = 0; index < 256; index += 1)
    {
        entries[index] = ComputeCrc(index);
    }

    return entries;
};
```

A value-producing `comptime` block in statement position with the value unused is a
warning (computed value discarded), consistent with Stark's treatment of unused
values elsewhere.

At module scope the same computation is written without the keyword — the const
initializer is already a comptime context (§4.1):

```stark
const u8[0 max][256] CrcTable = BuildCrcTable();
```

### 4.4 Statement-position effects

A `comptime` block or expression in statement position **inside a function body**
may run for its effect alone, executing the effect *during compilation* (§7):

```stark
comptime System.Console.WriteLine("generating CRC table");
```

There is no module-scope analog (§4.1): a build-time effect always lives inside an
owned context — a function-body `comptime` block, or the evaluation of a const or
static initializer that something depends on.

### 4.5 Grammar deltas

- `COMPTIME : 'comptime';` — new keyword (in-tree identifier collisions to be
  audited before lock).
- `comptime` block: statement alternative `COMPTIME block` (function bodies only —
  falls out of statements not existing at module scope).
- `comptime` expression: expression prefix `COMPTIME unaryExpression` (same shape
  as `try`).
- Attributes `[ComptimeBudget(N)]` (declaration-level) and `[BuildValue]`
  (binding-level) extend the innate attribute registry.
- **No top-level grammar changes.** Module-scope comptime is a semantic widening of
  existing const/static initializers (CT02), not new syntax.

## 5. Semantics

### 5.1 Evaluation timing

`comptime` contexts are evaluated by the const-eval engine during the front/middle
end, **before** code generation. The engine is an interpreter over the same typed IR
the rest of the compiler uses.

Evaluation order within a `comptime` context follows ordinary Stark evaluation
order. Across multiple `comptime` contexts, evaluation follows dependency order: a
`comptime` constant required to type or size another construct is evaluated first.
Because every comptime evaluation has an owner (§4.1), this is the same dependency
order const initializers already obey today — no new ordering rules exist.

### 5.2 Result materialization

| Comptime result | Materialized as |
|---|---|
| Scalar (integer, float, bool, range type) | immediate / named constant |
| Aggregate (fixed array, struct, enum value) | constant aggregate / global constant |
| Type value (if/when supported — out of scope, OQ-CT3) | resolved concrete type; comptime-ness erased |

By the time codegen runs, no trace of `comptime` remains in the IR handed to the
backend (§10).

### 5.3 Rule inheritance

Code in a `comptime` context is subject to **all** ordinary Stark rules — the
engine does not relax or special-case any of them; it enforces them on the comptime
timeline:

- **Type checking** — identical. Binding a `void` effect call to an `i32` is a type
  error whether or not `comptime` precedes it.
- **Ownership / borrow** — identical. Moves, borrows, and drops are enforced during
  const-eval as written.
- **Contracts** — identical. Loop behavior keywords, `disjoint`/`overlap`
  contracts, and function kinds (`finite`, `law`) must hold during evaluation.
- **Switch exhaustiveness / definite return (STK3044/STK3045)** — identical.

## 6. Interaction with Function Kinds

C3 is **orthogonal** to the fn/finite/law axis. `comptime` is not a function kind
and never appears in signatures. Any function may be invoked from a `comptime`
context, subject to:

| Called function kind | Behavior in `comptime` context |
|---|---|
| `law` / `finite law` (pure, deterministic) | Always foldable when inputs are compile-time-known. Ideal comptime callees. |
| `finite` (terminating) | Foldable when pure w.r.t. compile-time-known inputs; termination is already guaranteed, easing budget concerns. |
| `fn` (general) | Foldable if, during evaluation, it neither requires unavailable inputs (§8.2) nor exceeds the budget (§8.1). May perform effects in statement position (§7). |

A `comptime` call graph that bottoms out in compile-time-known leaves and pure
operations always folds. This lets the engine phrase failures in terms Stark
programmers already know ("this depends on a value not available at compile time")
rather than a comptime-specific vocabulary.

## 7. Effects at Compile Time

Stark permits effectful operations inside `comptime` contexts. Compile-time
execution is real execution; running an effect during compilation is a coherent, if
niche, activity (code generation, build manifests, progress logging).

**The distinction is value vs. effect, enforced by the ordinary type system — not by
a comptime blocklist:**

```stark
// OK: effect in statement position, run during compilation
comptime System.Console.WriteLine("generating table");

// ERROR: ordinary type error — WriteLine yields no i32 to bind.
// The `comptime` is irrelevant to the diagnosis.
stack i32[min max] x = comptime System.Console.WriteLine("oops");
```

### 7.1 Origin-of-value lint

Some operations *denote a value* but read it from the **build-time** environment
rather than the **run-time** environment — a clock, an environment variable, a
file. These are legal and sometimes intended (embedding build metadata such as a
timestamp or commit hash). Because the value's origin (build machine, now) differs
from what a reader might expect (user's machine, at run time), the compiler emits a
**warning** (STK3048, proposed):

```text
warning STK3048 [comptime]: this freezes a build-time value into the binary
  12 |     stack u32[0 max] seed = comptime System.Process.ReadEnvironment("CRC_SEED");
     |                             ^ reads the build machine's environment now,
     |                               not the program's environment at run time
  note: valid and useful for build metadata; silence with [BuildValue] if intended
```

This is a warning, not an error: the compiler is correct either way; only the
programmer's expectation can be wrong. `[BuildValue]` on the binding silences it.

## 8. Comptime-Specific Constraints

The only two constraints with no runtime analog. Everything else reduces to
ordinary Stark rules applied earlier.

### 8.1 Termination budget

At run time, a nonterminating loop is the user's problem. At compile time, it hangs
**the compiler**, which is the compiler's problem. Const-eval therefore runs under a
**step budget**:

- The engine maintains a monotonic step counter per top-level `comptime` evaluation.
- Exceeding the budget is a hard error (STK3046, proposed) reporting the offending
  loop/recursion site and, where cheaply available, the offending value on the final
  step.
- The default budget is implementation-defined and configurable per declaration via
  `[ComptimeBudget(N)]` and project-wide via build configuration.
- `finite` / `finite law` callees carry a termination guarantee and are least likely
  to hit the budget; the engine MAY relax counting for provably finite
  sub-evaluations.

### 8.2 Input availability

A `comptime` context may only read values that exist at evaluation time. Inputs that
come into existence only at run time are **not in scope** on the comptime timeline:

- the running program's command-line arguments / stdin,
- a dereference of a pointer into runtime-allocated memory,
- any binding whose value is not a compile-time constant at the point of use.

Referencing such an input from a `comptime` context is a hard error (STK3047,
proposed). This is essentially an ordinary scoping error surfaced on the comptime
timeline: the input is not *prohibited*, it simply does not yet exist.

## 9. Diagnostics

Because C3 hides comptime-ness from signatures, diagnostic quality is the
load-bearing element of the design. A const-eval failure MUST read like a *trace
through evaluation*: trigger site, evaluation trail, and the precise blocking
operation with a concrete fix.

Proposed codes (final numbers assigned at implementation):

| Code | Meaning |
|---|---|
| STK3046 | Compile-time evaluation exceeded its step budget |
| STK3047 | Value not available at compile time |
| STK3048 (warning) | Build-time value frozen into the binary (origin lint) |

### 9.1 General shape

```text
error STK304x [comptime]: <one-line classification>
  --> <trigger site>: required to be a compile-time constant here

  note: comptime evaluation trail
     -> FrameA    file:line   (entered here)
     -> FrameB    file:line
     -> FrameC    file:line   (got stuck here)

  --> <blocking site>: <precise reason>
  help: <fix A: make the dependency compile-time-known>
        <fix B: move the work to run time / drop `comptime`>
```

The reader's eye lands first on their own `comptime`, the **trail** explains why
evaluation reached the wall, and the deepest frame carries the most detail.

### 9.2 Budget / termination failure

```text
error STK3046 [comptime]: compile-time evaluation exceeded its step budget
   9 |     for willexit (stack mut i64[0 max] i = 0; i < n; i += 1)
     |     ^ evaluating this loop
  const-eval exceeded 1_000_000 steps
  the bound `n` was 4_294_967_295 on the final step - likely an unintended
  runtime-sized bound
  note: raise the limit with [ComptimeBudget(N)] if this is intended
```

### 9.3 Availability failure

```text
error STK3047 [comptime]: value is not available at compile time
   4 |     stack u16[0 max] port = comptime ParsePort(System.Process.Args()[1]);
     |                             ^ depends on `System.Process.Args()`
  reason: `System.Process.Args()` yields the running program's arguments, which
          do not exist during compilation
  note: const-eval reached this 1 call deep from the `comptime` site
  help: pass the value as a compile-time input, or evaluate at run time:
        stack u16[0 max] port = ParsePort(System.Process.Args()[1]);
```

### 9.4 Dual-use clarification

When a function is used at both comptime and runtime sites and only the comptime use
fails, the diagnostic MUST say so, to prevent the reader concluding the function is
globally broken:

```text
  note: `BuildTable` is fine at run time (called at Main.stark:9);
        only its compile-time use at line 14 fails
```

## 10. Backend / LLVM Lowering

C3 has **no effect on backend fact emission.** By the time codegen runs, every
`comptime` context has been reduced to concrete values and concrete (already
monomorphized) types. The word `comptime` never reaches LLVM.

- A `comptime` array lowers to an LLVM constant aggregate / global constant.
- A `comptime` scalar lowers to an immediate or named constant.
- A function used **only** at comptime need not be emitted at all.
- A function used at **both** comptime and runtime is emitted normally for its
  runtime call sites; its comptime call sites are already folded.

Backend facts that matter to Stark — alignment, layout, ownership-derived aliasing
attributes, and the constant values themselves — are computed from the **resolved**
types and values, identical regardless of whether comptime-ness was declared or
selected at the call site. The only backend-adjacent consequence is favorable:
more call sites collapse to constants, so fewer runtime bodies may be emitted.

## 11. Work Breakdown (CT*)

TDD-first, in dependency order. Nothing starts until the design is locked.

| ID | Item | Status |
|---|---|---|
| CT01 | Grammar: `comptime` keyword, block + expression forms; `[ComptimeBudget]`/`[BuildValue]` attributes; regen parser | not started |
| CT02 | Const-eval engine: extend `CompileTimeExpressionEvaluator` into an interpreter over typed IR (calls, loops, locals, aggregates, enums) | not started |
| CT03 | Termination budget: step counting, `[ComptimeBudget(N)]`, project-wide configuration, STK3046 | not started |
| CT04 | Input availability analysis + STK3047 with evaluation-trail diagnostics | not started |
| CT05 | Statement-position effects at compile time + origin lint (STK3048) + `[BuildValue]` | not started |
| CT06 | Result materialization: constant aggregates/globals in MIR/LLVM; fold-only functions skipped in codegen | not started |
| CT07 | Tests: diagnostics (budget/availability/lint), runtime equivalence (comptime result == runtime result), LLVM emission (tables become constant globals) | not started |
| CT08 | User-facing docs: LanguageReference, SKILL + references, book chapter, doc/roadmap sync | not started |

## 12. Open Questions (OQ-CT*)

| ID | Question | Notes / lean |
|---|---|---|
| OQ-CT1 | Default step budget | What count balances ergonomics against compiler responsiveness for typical workloads? |
| OQ-CT2 | `[BuildValue]` vs a sanctioned `System.BuildTime` module | Is an opt-in annotation the right way to silence the origin lint, or should a dedicated module be the sanctioned source of build-origin values? |
| OQ-CT3 | Type values (C4-style generics) | Out of scope for C3. If pursued later it layers on top without altering these rules, but error locality for generated types needs its own treatment. |
| OQ-CT4 | Memoization | Should identical `comptime` evaluations be cached across call sites / compilation units, and what determinism requirements make that sound? The module-scope lock (§4.1) guarantees every evaluation has an owner, so cache keys exist. |
| OQ-CT5 | Cross-module comptime effects | **Narrowed by the §4.1 lock**: effects always live inside owned contexts, so ordering is dependency order. Residual question: idempotency when one module's constants are evaluated in multiple downstream compilations (re-run per compile vs. cached via package image). |

## 13. Relationship to Existing Docs

- `01-language-feature-gaps.md` **L09** — this phase closes it. Also relevant to
  **L05** (const generics): C3 deliberately excludes type values (OQ-CT3), but a
  future const-generics design would consume comptime-evaluated values.
- `02-stdlib-gaps.md` **S05** (big integers) — comptime arithmetic inherits whatever
  integer story the language has; no special interaction.
- `11-error-propagation.md` — the innate attribute registry established there
  (`[Ok]`/`[Err]`, OQ-EP7) is where `[ComptimeBudget]`/`[BuildValue]` register.
- Doc `12` (atomics) — no interaction; atomics are runtime constructs, and a
  `comptime` context that touches one created at run time fails availability (§8.2).
- **Self-hosting**: the host compiler does not use CTFE, so L09 stays
  *workaround-exists* for the port itself. The motivating use cases are user-facing
  (lookup tables, derived constants) and compiler-internal table generation once
  self-hosted.

## 14. Summary of Rules

1. `comptime` selects *when* code runs; it adds no sublanguage.
2. All ordinary type, ownership, and contract rules apply unchanged during
   evaluation.
3. Value-vs-effect is enforced by the ordinary type checker, not a comptime
   blocklist.
4. Effects in statement position are permitted at compile time, inside owned
   contexts (function-body `comptime`, or an initializer something depends on).
5. Module scope needs no keyword: const/static initializers are implicit comptime
   contexts, and free-standing module-scope blocks do not exist (locked, §4.1).
6. Build-origin value reads are legal but linted (STK3048) for expectation mismatch.
7. The only comptime-specific hard errors are **budget** (STK3046 — don't hang the
   compiler) and **availability** (STK3047 — don't read what doesn't exist yet).
8. Diagnostics trace the evaluation path, classify the wall, and offer a dual fix.
9. The backend never sees `comptime`; it receives concrete values and types.
