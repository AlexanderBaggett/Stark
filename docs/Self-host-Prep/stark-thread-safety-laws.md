# Stark Language Design: Thread-Safety Laws

**Status:** Draft
**Feature:** `Transferable` / `Shareable` laws and field-level safety attributes
**Author:** Alexander Baggett

---

## 1. Summary

This document specifies how Stark guarantees memory safety across thread
boundaries. It introduces two compiler-proven properties — the `Transferable`
and `Shareable` laws — that govern whether a value may be moved to, or shared
with, another thread. These properties are derived structurally by default and
adjusted at the **field level** through `[Grant(...)]` and `[Deny(...)]`
attributes. Functions of any kind consume the properties through `where`
predicate constraints.

The design is the Stark analogue of Rust's `Send` and `Sync` marker traits, but
differs in two deliberate ways: the markers are **laws** (predicates the
compiler discharges) rather than traits a type implements, and overrides are
attached to the **field** that justifies them rather than to the type as a
whole.

---

## 2. Motivation

A systems language with an ownership/borrow model must prevent data races at
compile time. Two distinct guarantees are required:

1. That a value can be **moved** from one thread to another without leaving
   behind aliases that violate ownership.
2. That a value can be **shared by reference** across threads without permitting
   concurrent mutation of non-synchronized state.

Rust solves this with the `Send` and `Sync` auto-traits. That model is sound,
but it concentrates both opt-outs (`impl !Send`) and unsafe assertions
(`unsafe impl Sync`) at the type level, detached from the specific member that
causes the property to hold or fail. Stark already expresses compiler-proven
properties as **laws** and already supports C#-style **attributes**, so this
spec expresses thread-safety in those existing terms and localizes every
override to its responsible field.

---

## 3. The Two Laws

### 3.1 `Transferable`

A type `T` satisfies `Transferable` if ownership of a `T` value may be moved to
another thread. This is the Stark equivalent of Rust's `Send`.

### 3.2 `Shareable`

A type `T` satisfies `Shareable` if a shared borrow `&T` may be accessed from
another thread concurrently. This is the Stark equivalent of Rust's `Sync`.

Both are **laws** in the Stark sense: properties the compiler proves about a
type, not interfaces a type implements. They never appear in interface-position
on a type declaration.

---

## 4. Structural Derivation

By default both laws are derived structurally. A type satisfies a law if and
only if every one of its fields satisfies that law.

```
public struct Counter
{
    value: i32;
}
// Transferable(Counter) and Shareable(Counter) both hold,
// because i32 satisfies both.
```

A type with no fields satisfies both laws vacuously. Generic types propagate the
requirement to their parameters: `Vec<T>` satisfies `Transferable` if and only
if `Transferable(T)` holds (see §7).

No syntax is written for the common case. Derivation is silent.

---

## 5. Field-Level Overrides

When structural derivation is wrong — either too permissive or too
restrictive — the result is corrected with an attribute placed on the **field
responsible** for the discrepancy.

### 5.1 `[Deny(Law)]`

Removes a field's contribution to a law, forcing the enclosing type to fail that
law regardless of structural derivation. Used when a field's type is nominally
fine but its semantics are not (e.g. a raw pointer into shared mutable state).

```
public struct RawHandle
{
    [Deny(Transferable)]
    [Deny(Shareable)]
    ptr: ptr<u8>;
}
// Neither law holds for RawHandle: the ptr field denies both.
```

### 5.2 `[Grant(Law)]`

Asserts that a field satisfies a law the compiler cannot prove on its own. This
is the safety-critical, audited assertion — the equivalent of Rust's
`unsafe impl`. The author takes responsibility for the invariant.

```
public struct LockedCache
{
    [Grant(Shareable)]
    inner: Mutex<Map<String, Bytes>>;
}
// Shareable(LockedCache) holds because the Mutex makes concurrent
// &borrow access safe, asserted on the field that provides it.
```

### 5.3 Type-Level Form

For zero-field or opaque types where no field carries the justification, the
same attribute may ride on the type declaration:

```
[Grant(Transferable)]
public struct OpaqueToken
{
}
```

This is the only position in which a thread-safety attribute may appear off of a
field, and is reserved for types whose representation is not expressed as
ordinary fields.

---

## 6. Resolution Rules

For a given law `L` and type `T`, the compiler computes `L(T)` as follows:

1. If a type-level `[Grant(L)]` is present, `L(T)` holds. (Audited assertion.)
2. Otherwise, if a type-level `[Deny(L)]` is present, `L(T)` fails.
3. Otherwise, `L(T)` holds if and only if, for every field `f`:
   - `f` carries `[Grant(L)]`, **or**
   - `f` carries no attribute for `L` and `L(typeof f)` holds structurally,
   - and no field carries `[Deny(L)]`.

A single `[Deny(L)]` on any field is sufficient to make `L(T)` fail, unless a
type-level `[Grant(L)]` overrides it (rule 1). Conflicting attributes on the
*same* field (`[Grant(L)]` and `[Deny(L)]` together) are a compile error.

`Grant` is the only construct that introduces a proof obligation the compiler
does not verify; all other paths are mechanically checked.

---

## 7. Generics and Propagation

Law constraints propagate through generic type parameters. A generic type
satisfies a law conditionally on its parameters:

```
public struct Pair<A, B>
{
    first:  A;
    second: B;
}
// Transferable(Pair<A, B>) holds iff Transferable(A) and Transferable(B).
// Shareable likewise.
```

This conditional derivation is automatic and requires no annotation, matching
the structural rule of §4 applied to type parameters.

---

## 8. Consuming the Laws in Functions

A function requires a law by stating it as a **predicate constraint** in a
`where` clause. The constraint reads as a predicate over a type, reflecting that
laws are discharged by the compiler rather than implemented by the type.

This works uniformly across all four Stark function kinds (`fn`, `finite`,
`law`, `finite law`).

```
fn Spawn<T>(move T value) -> finite
    where Transferable(T)
{
    ...
}

finite Broadcast<T>(shared &T value)
    where Shareable(T)
{
    ...
}
```

Multiple constraints combine conjunctively:

```
fn Distribute<T>(move T value) -> finite
    where Transferable(T), Shareable(T)
{
    ...
}
```

A call that supplies a type for which the required law does not hold is a
compile error at the call site.

---

## 9. Enforcement at Thread Boundaries

The laws are checked wherever data crosses a thread boundary. The `spawn`
primitive requires the moved value's type to satisfy `Transferable`; sharing a
borrow with another thread requires `Shareable`.

```
fn Worker() -> finite law
{
    let c = Counter { value: 0 };
    spawn Process(move c);    // OK: Transferable(Counter) holds

    let h = RawHandle { ptr: ... };
    spawn Process(move h);    // ERROR: Transferable(RawHandle) denied by ptr field
}
```

The borrow checker additionally forbids holding a non-`Shareable` value across a
suspension point in an asynchronous, multithreaded context, consistent with the
existing borrow model. (Async interaction is specified separately.)

---

## 10. Comparison to Rust

| Concept | Rust | Stark |
| --- | --- | --- |
| Move across threads | `Send` | `Transferable` law |
| Share `&` across threads | `Sync` | `Shareable` law |
| Auto-derivation | structural auto-trait | structural derivation (§4) |
| Opt out | `impl !Send for T` (type-level) | `[Deny(Transferable)]` (field-level) |
| Unsafe assertion | `unsafe impl Sync for T` (type-level) | `[Grant(Shareable)]` (field-level) |
| Require in a function | `where T: Send` | `where Transferable(T)` |
| Conceptual model | marker trait the type implements | law the compiler discharges |
| Override location | type | responsible field |

The two intentional divergences:

- **Laws, not traits.** `Transferable(T)` is a predicate the compiler proves,
  written in predicate position, never in interface position on the type. This
  prevents the "looks like an inherited interface" reading that a trait-style
  `T : Transferable` syntax would invite.
- **Field-level overrides.** Both the opt-out and the unsafe assertion attach to
  the field that justifies them, so the audit surface is the member rather than
  the type. The cost is slightly more verbosity than Rust's single line per
  type; the benefit is that the reason for a property is always co-located with
  its cause.

---

## 11. Worked Example

```
// A handle wrapping a non-thread-safe foreign resource.
public struct Connection
{
    [Deny(Transferable)]
    [Deny(Shareable)]
    raw: ptr<u8>;

    id: i64;          // structurally Transferable + Shareable, no effect
}

// A pool that makes concurrent shared access safe via a lock.
public struct Pool
{
    [Grant(Shareable)]
    guard: Mutex<Vec<Connection>>;
}

// Generic worker: requires the moved payload be thread-transferable.
fn RunWorker<T>(move T payload) -> finite
    where Transferable(T)
{
    spawn Handle(move payload);
}

fn Main() -> finite law
{
    let p = Pool { guard: Mutex::New(Vec::New()) };

    // OK: Shareable(Pool) holds via the granted guard field.
    Broadcast(shared &p);

    // ERROR: Transferable(Connection) fails — denied by the raw field.
    let c = Connection { raw: ..., id: 1 };
    RunWorker(move c);
}
```

---

## 12. Open Questions

1. **Conditional grants.** Should `[Grant(L)]` support a predicate
   (e.g. `[Grant(Shareable) where Shareable(T)]`) for wrapper types that are
   conditionally safe, or is structural derivation sufficient for all such
   cases?
2. **Auto-`Deny` for known-unsafe primitives.** Should `ptr<T>` deny both laws
   automatically (as Rust does for raw pointers), making the explicit field
   attributes in §5.1 redundant for the common case?
3. **Diagnostics.** When a law fails, the compiler should name the specific
   field responsible. The field-level design makes this precise; the exact
   diagnostic wording is to be specified.
