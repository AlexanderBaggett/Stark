# Stark Design Specification: Compile-Time Evaluation (`comptime`)

**Status:** Accepted direction; partial implementation
**Feature:** `comptime` blocks, `comptime` expressions, and `comptime` generic
value parameters

---

## 1. Summary

`comptime` evaluates ordinary Stark code during compilation. It is a *time
selector*: code in a `comptime` context runs under the same type, ownership, and
contract rules it would obey at run time. There is no separate comptime
sublanguage.

The accepted self-hosting scope is **CTFE plus broad compile-time branching over
program structure**. Stark code may compute constants, tables, range facts, and
layout facts, and may branch at compile time based on explicit structural facts
about declarations such as types, fields, enum variants, functions, attributes,
and doctrine/trait conformance.

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
  cases, but the self-hosting requirement is not "every possible fact exists";
  it is that every fact and CTFE behavior the compiler port actually depends on
  is implemented and tested end-to-end.
- The current-host compiler-port CTFE audit is recorded in
  [../internal/ctfe-self-host-compiler-audit.md](../internal/ctfe-self-host-compiler-audit.md).
  The feature remains in-progress until the remaining parity, package
  preservation, and port-driven fact gaps are closed. Do not treat each
  compiler-known structural fact as a separate roadmap task.

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

The CTFE engine must support the compiler-port use cases:

- scalar and aggregate constant construction
- table generation
- range and integer fact computation
- enum and layout fact computation
- string/text constants used by diagnostics, package metadata, and LLVM text
- calls to declared `finite`, `law`, and `finite law` Stark functions and
  methods whose bodies stay within the supported compile-time subset
- local mutation inside bounded compile-time execution
- `willexit` loops whose bounds can be validated by the compile-time evaluator

### 3.2 Program-Structure Branching

Compile-time code may inspect explicit program-structure facts and branch over
them with ordinary Stark control flow, especially `if` and exhaustive `switch`.

Required structural inputs include:

- type identity and type category
- field names, order, types, offsets, and attributes where layout is known
- enum variants, variant payloads, role attributes such as `[Ok]` / `[Err]`,
  and discriminant/layout facts
- function signatures, function kind (`fn`, `finite`, `law`, `finite law`),
  storage/ownership annotations, memory contracts, and ABI facts
- trait/doctrine conformance and associated type bindings
- compile-time alias/noalias proof facts where exposed by the typed model
- named type declaration module identity and package-visible metadata needed by
  package-image generation

The named structural-query surface is `System.Compiler`. It is a
compiler-known, compile-time-only API for asking explicit questions about types,
fields, enum variants, callable shapes, ABI/layout facts, associated types,
trait/doctrine conformance, and package-visible metadata. The exact fact list is
reference material, not roadmap task granularity.

The language decision is that the capability exists and is explicit:
compile-time branching over program structure is part of `comptime`, not a
runtime reflection feature. The current-host audit found no need for hidden
runtime reflection or package enumeration facts; remaining facts should be
driven by concrete self-hosted compiler queries and supported end-to-end.

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
compile-time value kinds should be added only when concrete compiler or stdlib
code needs them.

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

- [x] Decide that `comptime` includes CTFE plus broad compile-time branching
      over explicit program-structure facts.
- [x] Decide that Stark const generics are spelled as typed `comptime` generic
      value parameters, not `const`, because Stark `const` already means deep
      interior immutability.
- [x] Keep program-structure facts compile-time-only; do not add runtime
      reflection as part of this feature.
- [~] Implement the self-hosting `comptime` capability in the host compiler.
      Current broad support includes expression/block CTFE, typed `comptime`
      generics, deterministic local mutation, bounded loops/traversal,
      aggregate constants, layout queries, switch/pattern execution, explicit
      conversion/cast evaluation, CTFE `try` propagation over role-marked
      enums, generic CTFE substitution, declared `finite`/`law` function and
      method calls including chained receiver calls, trait-default receiver
      calls, and nested calls that preserve symbolic `comptime` value
      arguments until specialization, cross-package/package-image preservation
      for CTFE `try` and typed-template structural facts, many explicit
      `System.Compiler` structural facts, typed thread-safety law attribute
      condition type predicate/metadata facts for `[Grant]` / `[Deny]` on
      types and fields,
      implemented-trait metadata facts, declaration and actual `comptime`
      generic argument type predicate/metadata facts, ordinary actual type
      argument predicate/metadata facts, method `where` law predicate type
      predicate/metadata facts, method module identity facts, method parameter
      name facts, method generic trait-bound facts, C source alias identity
      facts including closure
      return/parameter type aliases, named type module identity facts, callable
      return/parameter qualifier facts, field/enum-payload qualifier facts,
      callable bounded raw-pointer count-expression facts, diagnostics for
      unsupported compile-time execution, and erasure before runtime lowering.
    - [x] Finish the compiler-port CTFE use-case audit against the current host
          compiler implementation, recording the required constants, generated
          tables, layout/range facts, structural facts, and compile-time
          branching forms. Audit reference:
          [../internal/ctfe-self-host-compiler-audit.md](../internal/ctfe-self-host-compiler-audit.md).
    - [~] Close evaluator parity gaps between
          `CompileTimeFunctionEvaluator` and the MIR/imported-template CTFE
          evaluator so type checking, MIR lowering, and cross-package typed
          template lowering accept and reject the same compile-time subset.
          MIR CTFE now preserves open structural-fact targets and default
          constants consistently with type-check CTFE, and imported
          typed-template structural facts fold through the same
          `System.Compiler` evaluator after generic substitution. Source-free
          imported typed templates also preserve ordinary unary `comptime`
          expressions over deterministic manifest-backed constants. Open
          integer `comptime` generic values now validate symbolically during
          template type checking, survive nested finite/law CTFE calls, and
          fold to concrete immediates after specialization.
    - [~] Close required ordinary-expression CTFE gaps found by the audit.
          Explicit conversion/cast evaluation and CTFE `try` propagation over
          role-marked result/option/status-shaped enums have landed; integer
          arithmetic over typed `comptime` generic values now validates in
          generic templates and folds after specialization; nested finite/law
          calls can forward symbolic `comptime` value arguments until a
          concrete specialization is available; any remaining
          ordinary-expression work is driven by the CTFE closure pass and
          concrete compiler-port use.
    - [~] Close required package-image, source-bridge, and imported-template
          preservation gaps for CTFE forms used across package boundaries:
          conversions, structural facts, receiver calls, aggregate constants,
          `try` fallbacks, ordinary unary `comptime` expressions, and
          `comptime` generic substitution. CTFE `try` now
          publishes typed-template `try` expressions, ordinal-keyed propagation
          facts, source-bridge rendering, and imported-template MIR lowering;
          typed-template `System.Compiler` structural facts now preserve fact
          name, type arguments, and comptime value arguments and fold in
          imported MIR lowering; ordinary unary `comptime` expressions now
          publish/load/render as typed-template expressions and fold source-free
          when their operand is a deterministic manifest-backed constant or
          specialized integer `comptime` generic expression;
          source-free imported typed templates now resolve and fold
          manifest-backed direct finite/law calls with concrete `comptime`
          substitutions;
          published generic enum calls resolve through direct enum-layout facts
          instead of display-name parsing; typed package-template bodies are
          authoritative and bridge source emits declaration-only APIs for them
          instead of reconstructing bodies from stale or corrupted legacy
          `BodyText`.
    - [ ] Close required `System.Compiler` structural-fact coverage from actual
          compiler-port queries, including callable/package/module facts,
          ABI/layout facts, field/enum/associated-type/doctrine facts, and
          typed thread-safety law attribute facts, and wrong-target/out-of-range
          diagnostics. Type/field `[Grant]` / `[Deny]` law attribute
          count/name/kind/condition/type-predicate/type-metadata facts,
          implemented-trait count/type-predicate/type/metadata facts,
          declaration `comptime` generic parameter
          type-predicate/type/metadata facts, callable return/parameter
          qualifier facts, field/enum-payload qualifier facts, method module
          identity facts, method `where` law predicate
          count/name/type-predicate/type/metadata facts, method parameter
          name facts, method generic trait-bound count/type-predicate/type/metadata
          facts, C source alias identity facts including closure return and
          parameter types, named type module identity facts, and
          function-pointer/closure return and parameter nested-type metadata
          facts, plus
          function-pointer/closure/method bounded raw-pointer parameter
          count-expression facts, now fold in CTFE, reject invalid runtime use,
          and are covered through package-backed typed aliases,
          package-backed trait metadata, or method imports.
    - [x] Complete runtime-boundary verification: structural fact calls and
          bare structural fact references report STK3054 outside `comptime`;
          compile-time-only trait/doctrine/integer storage remains rejected in
          runtime contexts; scalar, aggregate, enum, layout, and
          structural-fact CTFE materialization is covered by targeted
          regressions that assert no compile-time calls leak into MIR/LLVM.
- [ ] Complete the self-hosting CTFE audit and closure pass. This is the real
      remaining task: audit the self-hosted compiler port against the host
      implementation, identify exact missing CTFE behaviors or structural facts,
      implement those gaps end-to-end, verify package-image/source-bridge
      preservation for required cases, and verify runtime erasure.

Reference notes:

- The exact `System.Compiler` fact inventory belongs in reference material and
  tests, not as separate roadmap tasks in this document. Current reference:
  [comptime-structural-facts-reference.md](../../skills/stark-language/references/comptime-structural-facts-reference.md).
- Do not split this feature into new roadmap subtasks merely to show partial
  progress. Add a new task only when the compiler port reveals genuinely
  unplanned work at the same planning granularity as the existing roadmap.
