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
- calls to Stark functions that are valid in a compile-time context
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
- module/package metadata needed by package-image generation

The exact names and shapes of the structural-query APIs are implementation
work. The language decision is that the capability exists and is explicit:
compile-time branching over program structure is part of `comptime`, not a
runtime reflection feature.

### 3.3 Comptime Generic Specialization

Comptime generic arguments participate in generic identity, overload resolution,
package-image identity, and monomorphization. A concrete instantiation includes
both type arguments and comptime value arguments.

Implementation status: the self-hosting range-typed integer slice has landed
for declaration syntax, symbolic fixed-array lengths (`T[N]`), fixed-array
argument inference, range diagnostics, explicit integer value arguments at
type/function call sites, symbolic value forwarding with `comptime N`, function
monomorphization keys, type-reference package-image preservation of symbolic
fixed-array lengths, imported-template value substitution, source-bridge
round-tripping, and materializing a specialized value parameter as an ordinary
scalar expression (for example `return N`).

The first self-hosting slice should focus on range-typed integer comptime
generic values used for fixed-array lengths, layout facts, fixed-capacity
buffers, table shapes, and target facts. Additional compile-time value kinds can
be added when concrete compiler or stdlib code needs them.

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
      value parameters.
- [x] Keep program-structure facts compile-time-only; do not add runtime
      reflection as part of this feature.
- [ ] Implement the CTFE evaluator for ordinary Stark expressions, calls,
      local mutation, aggregate construction, and bounded `willexit` loops.
- [ ] Define and implement the explicit structural-fact surface for types,
      fields, enum variants, functions, attributes, doctrines/traits,
      associated types, ABI/layout facts, and package metadata.
- [ ] Add diagnostics for runtime-only values, unsupported compile-time calls,
      non-terminating compile-time execution, and illegal leakage of structural
      facts into runtime.
- [ ] Preserve required compile-time facts through package images and imported
      typed interfaces.
- [ ] Ensure compile-time-only functions and structural facts erase before
      backend lowering.
- [x] Implement typed comptime generic value parameters, monomorphization
      identity, type/package-image preservation, diagnostics, and fixed-array
      use sites.
- [x] Land first range-typed integer slice: parser support for `comptime`
      generic parameters, typed signature/named-type metadata, symbolic
      fixed-array lengths, overload inference from concrete fixed-array
      arguments, range rejection for inferred values, and function
      instantiation keys that include comptime value arguments.
- [x] Add explicit integer value-argument syntax at type and function call
      sites, including symbolic forwarding with `comptime N`.
- [x] Allow specialized range-typed integer comptime generic values to be
      materialized as ordinary scalar expressions after specialization.
- [x] Substitute comptime generic values through imported template bodies and
      publish full comptime parameter declarations in package/source-surface
      metadata.
