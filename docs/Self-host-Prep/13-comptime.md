# Stark Design Specification: Compile-Time Evaluation (`comptime`)

**Status:** Accepted direction; pre-self-host baseline frozen, broad expansion
deferred
**Feature:** `comptime` blocks, `comptime` expressions, and `comptime` generic
value parameters

---

## 1. Summary

`comptime` evaluates ordinary Stark code during compilation. It is a *time
selector*: code in a `comptime` context runs under the same type, ownership, and
contract rules it would obey at run time. There is no separate comptime
sublanguage.

The feature is now split into two schedules:

- **Pre-self-host:** preserve the currently implemented, tested baseline needed
  by the C# host and by core Stark language semantics. This includes
  expression/block CTFE, typed integer `comptime` generics, deterministic
  aggregate/table constants, concrete layout queries, supported finite/law
  calls, and already-landed compile-time-only structural facts.
- **Post-self-host:** revisit the broad Zig-like `comptime` direction after the
  Stark compiler can build itself. That phase owns full evaluator parity,
  future `System.Compiler` fact expansion, and broad cross-package preservation.

Program-structure facts are compile-time-only. They must not create runtime
reflection metadata, hidden dispatch, hidden allocation, or backend-visible
objects unless the program explicitly materializes ordinary data from them.

```stark
fn u8[0 max][256] BuildTable()
{
    comptime
    {
        stack mut u8[0 max][256] table;
        for willexit (stack mut u64[0 max] index = 0; index < 256; index += 1)
        {
            table[index] = ComputeCrc(index);
        }
        return table;
    }
}

const i32 x = comptime ExpensiveConst();

struct FixedBuffer<T, comptime u64[0 max] N>
{
    Items: T[N];
}
```

---

## 2. Surface Syntax

### 2.1 `comptime` block

A statement-position block, evaluated fully during compilation. Allman braces,
4-space indent. When used in value position, it yields a value via `return`.

```stark
const u8[0 max][256] table = comptime
{
    stack mut u8[0 max][256] values;
    for willexit (stack mut u64[0 max] index = 0; index < 256; index += 1)
    {
        values[index] = ComputeCrc(index);
    }
    return values;
};
```

### 2.2 `comptime` expression

The `comptime` keyword prefixes a single expression, forcing it to evaluate
during compilation. The result is a compile-time constant of the expression's
ordinary type.

```stark
const i32 x = comptime ExpensiveConst();
```

Implementation status summary:

- `comptime expr` and value-position `comptime { ... }` blocks are accepted and
  must fold during type checking, or the compiler reports STK3053.
- The host compiler has broad partial CTFE support for deterministic Stark code:
  constants, typed `comptime` generic values, local mutation, assignments,
  bounded `willexit` loops, explicit fixed-array traversal, `if`, `switch`,
  pattern conditions, aggregate constants, layout queries, and calls into
  supported declared `finite`, `law`, and `finite law` function and method
  bodies, including receiver methods with ordinary borrow receivers, chained
  receiver calls through compile-time call results, and trait-default receiver
  methods that dispatch directly to compile-time-visible concrete overrides.
- The host compiler has a broad `System.Compiler` structural-fact surface for
  compile-time-only program-structure branching. These facts are ordinary CTFE
  values, are rejected in runtime expressions, and must erase before MIR/codegen.
- Package-image and source-bridge preservation exists for many landed CTFE
  cases. Before self-hosting, this support is maintained as a regression
  baseline, not expanded into a complete broad-CTFE parity project.
- The current-host compiler-port CTFE audit is recorded in
  [../internal/ctfe-self-host-compiler-audit.md](../internal/ctfe-self-host-compiler-audit.md).
- The pre-self-host scope is locked in
  [26-comptime-pre-self-host-scope.md](26-comptime-pre-self-host-scope.md). The
  post-self-host expansion scope is tracked in
  [27-comptime-post-self-host-scope.md](27-comptime-post-self-host-scope.md).

### 2.3 `comptime` generic parameters

In a generic parameter list, `comptime` introduces a typed compile-time value
parameter. This is Stark's const-generic spelling.

```stark
struct FixedBuffer<T, comptime u64[0 max] N>
{
    Items: T[N];
}

fn u64[0 max] Length<T, comptime u64[0 max] N>(borrow T[N] items)
{
    return N;
}
```

This is a declaration-context use of `comptime`, not expression syntax. It means
that `N` is a generic argument known during specialization. `FixedBuffer<u8, 16>`
and `FixedBuffer<u8, 32>` are distinct concrete instantiations.

Stark uses `comptime`, not `const`, because `const` already means deep interior
immutability of an object hierarchy. A comptime generic parameter is about
compile-time specialization, not object immutability.

---

## 3. Semantics

`comptime` contexts are evaluated by the const-eval engine before code
generation, then materialized as compile-time constants (scalars as immediates or
named constants, aggregates as constant aggregates / globals).

All ordinary Stark rules apply unchanged during evaluation — type checking,
ownership/borrow, and contracts. `comptime` adds no rules of its own beyond
Section 4.

### 3.1 CTFE Scope

The pre-self-host CTFE baseline supports the use cases already implemented and
covered by tests:

- scalar and aggregate constant construction
- table generation
- range and integer fact computation
- enum and layout fact computation
- string/text constants used by diagnostics, package metadata, and LLVM text
- calls to declared `finite`, `law`, and `finite law` Stark functions and
  methods whose bodies stay within the already implemented deterministic subset
- local mutation inside bounded compile-time execution
- `willexit` loops whose bounds can be validated by the compile-time evaluator

### 3.2 Program-Structure Branching

Compile-time code may inspect explicit program-structure facts and branch over
them with ordinary Stark control flow, especially `if` and exhaustive `switch`.
For self-hosting, the currently implemented fact surface is maintained as a
baseline. Expanding this into a complete program-structure API is deferred until
after self-hosting.

Required structural inputs include:

- type identity and type category
- field names, order, types, offsets, and attributes where layout is known
- enum variants, variant payloads, role attributes such as `[Ok]` / `[Err]`,
  and discriminant/layout facts
- function signatures, function kind (`fn`, `finite`, `law`, `finite law`),
  storage/ownership annotations, memory contracts, and ABI facts
- trait/doctrine conformance and associated type bindings
- compile-time alias/noalias proof facts where exposed by the typed model
- named and nested type declaration module identity and package-visible metadata
  needed by package-image generation

The named structural-query surface is `System.Compiler`. It is a
compiler-known, compile-time-only API for asking explicit questions about types,
fields, enum variants, callable shapes, ABI/layout facts, associated types,
trait/doctrine conformance, and package-visible metadata. The exact fact list is
reference material, not roadmap task granularity.

`System.Compiler` is not currently a standard-library module. Calls under that
namespace are recognized and evaluated by the compiler during `comptime`; using
them outside compile-time contexts is a diagnostic, and no runtime library symbol
is emitted.

The language decision is that the capability remains explicit:
compile-time branching over program structure is part of broad `comptime`, not
a runtime reflection feature. The broad version is post-self-host work; before
self-hosting, only the already implemented fact surface and regression fixes are
in scope.

### 3.3 Comptime Generic Specialization

Comptime generic arguments participate in generic identity, overload resolution,
package-image identity, and monomorphization. A concrete instantiation includes
both type arguments and comptime value arguments.

Implementation status: range-typed integer `comptime` generic values are
supported for the self-hosting use cases known so far: fixed-array lengths,
layout facts, fixed-capacity buffers, table shapes, target facts, explicit value
arguments, symbolic forwarding with `comptime N`, monomorphization identity,
package-image/source-bridge preservation, and materializing a specialized value
parameter as an ordinary scalar expression (for example `return N`). Additional
compile-time value kinds are post-self-host work and should be added only when
concrete compiler, stdlib, or vendor-library code needs them. They must remain
deterministic and package-image-representable; Stark should not default to
allowing arbitrary runtime-shaped values as generic arguments.

Comptime generic values may be used in compile-time type/value positions where
their type is valid, such as fixed-array lengths:

```stark
struct Lookahead<T, comptime u64[1 max] N>
{
    Items: T[N];
}
```

Explicit value arguments are supplied after the type arguments. Literal integer
arguments can be written directly; a forwarded or computed value expression uses
the expression-form `comptime` marker so it cannot be mistaken for a type name.

```stark
finite law u8[0 max] Length<T, comptime u8[1 8] N>(borrow T[N] items)
{
    return N;
}

stack i32[min max][3] values = { 10, 20, 30 };
stack u8[0 max] size = Length<i32[min max], 3>(values);

struct Buffer<T, comptime u8[1 8] N>
{
    Items: T[N];
}

finite law u8[0 max] Probe<T, comptime u8[1 8] N>(borrow Buffer<T, comptime N> buffer)
{
    return N;
}
```

---

## 4. Compile-Time Errors

A loop inside a `comptime` context may not be marked with the following loop
keywords:

- **`infinite`** — a loop declared `infinite` inside a `comptime` context is a
  compile error.
- **`non-deterministic`** — a loop declared `non-deterministic` inside a
  `comptime` context is a compile error.

Current implementation note: unsupported CTFE loop shapes are reported through
STK3053 at the enclosing `comptime` expression/block. Accepted `willexit` loops
have a compile-time iteration budget; exceeding it reports STK3053 with the
loop kind and iteration count. Recursive compile-time `finite` / `law` callable
bodies also report STK3053 instead of recursing indefinitely.

The evaluator must also reject:

- attempts to use runtime-only values in compile-time decisions
- attempts to carry program-structure facts into runtime values without an
  explicit materialization step
- calls to APIs that are not valid in a compile-time context
- compile-time execution that cannot be proven to terminate under the accepted
  evaluator rules
- comptime generic arguments that are not compile-time constants of the declared
  parameter type
- uses of comptime generic parameters in runtime-only positions without an
  explicit materialization as ordinary data

---

## 5. Backend / LLVM Lowering

`comptime` never reaches the backend. By codegen, every `comptime` context has
been reduced to concrete values and concrete types. A `comptime` array lowers to
a constant aggregate / global; a `comptime` scalar to an immediate or named
constant. A function used only at comptime need not be codegen'd; a function used
at both comptime and runtime is codegen'd normally for its runtime call sites.

Program-structure facts also erase before backend lowering. If compile-time
code chooses a concrete type, function, layout, or generated constant from those
facts, only the resulting ordinary Stark program reaches lowering.

Comptime generic parameters erase as parameters. Their values affect the
selected concrete instantiation and any ordinary constants or layout facts
materialized from that instantiation, but no hidden runtime generic-value object
is emitted.

---

## 6. Work Items

- [x] Decide that `comptime` has two schedules: a frozen pre-self-host baseline
      and a deferred post-self-host expansion.
- [x] Decide that Stark const generics are spelled as typed `comptime` generic
      value parameters, not `const`, because Stark `const` already means deep
      interior immutability.
- [x] Keep program-structure facts compile-time-only; do not add runtime
      reflection as part of this feature.
- [x] Freeze the pre-self-host `comptime` baseline at the currently implemented
      host capability. See
      [26-comptime-pre-self-host-scope.md](26-comptime-pre-self-host-scope.md).
- [x] Preserve current runtime-boundary verification: structural fact calls and
      bare structural fact references report STK3054 outside `comptime`;
      compile-time-only trait/doctrine/integer storage remains rejected in
      runtime contexts; scalar, aggregate, enum, layout, and structural-fact
      CTFE materialization is covered by targeted regressions that assert no
      compile-time calls leak into MIR/LLVM.
- [ ] Revisit broad `comptime` after self-hosting: rebuild the evaluator in the
      Stark compiler architecture, settle the stable structural fact surface,
      finish broad evaluator parity, and complete package/source preservation
      for broad CTFE helper bodies. See
      [27-comptime-post-self-host-scope.md](27-comptime-post-self-host-scope.md).

Reference notes:

- The exact `System.Compiler` fact inventory belongs in reference material and
  tests, not as separate roadmap tasks in this document. Current reference:
  [comptime-structural-facts-reference.md](../../skills/stark-language/references/comptime-structural-facts-reference.md).
- Before self-hosting, `comptime` work is maintenance-only unless the active
  compiler port proves a missing behavior is required. Broad expansion belongs
  to the post-self-host scope document.
