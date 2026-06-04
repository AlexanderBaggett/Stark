# Stark Design Specification: Compile-Time Evaluation (`comptime`)

**Status:** Draft
**Feature:** `comptime` blocks and `comptime` expressions

---

## 1. Summary

`comptime` evaluates ordinary Stark code during compilation. It is a *time
selector*: code in a `comptime` context runs under the same type, ownership, and
contract rules it would obey at run time. There is no separate comptime
sublanguage.

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

---

## 3. Semantics

`comptime` contexts are evaluated by the const-eval engine before code
generation, then materialized as compile-time constants (scalars as immediates or
named constants, aggregates as constant aggregates / globals).

All ordinary Stark rules apply unchanged during evaluation — type checking,
ownership/borrow, and contracts. `comptime` adds no rules of its own beyond
Section 4.

---

## 4. Compile-Time Errors

A loop inside a `comptime` context may not be marked with the following loop
keywords:

- **`infinite`** — a loop declared `infinite` inside a `comptime` context is a
  compile error.
- **`non-deterministic`** — a loop declared `non-deterministic` inside a
  `comptime` context is a compile error.

---

## 5. Backend / LLVM Lowering

`comptime` never reaches the backend. By codegen, every `comptime` context has
been reduced to concrete values and concrete types. A `comptime` array lowers to
a constant aggregate / global; a `comptime` scalar to an immediate or named
constant. A function used only at comptime need not be codegen'd; a function used
at both comptime and runtime is codegen'd normally for its runtime call sites.
