+++
title = "18. Generics, Traits, Doctrines, and Specialization"
weight = 180
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/17-errors-without-exceptions/"
next = "/book/19-callable-values/"
aliases = ["/book/17-generics-traits-doctrines/"]
+++

# Generics, Traits, Doctrines, and Specialization

Stark generics are meant to keep reusable code static and optimizer-friendly.
This chapter starts with source-backed generic functions and types that are
instantiated at use sites, then adds the compile-time contracts that keep
generic code honest.

{{< stark-sample "assets/book/samples/generics-specialization.stark" >}}

## Step 1: Instantiate Generic Functions At The Call Site

Generic parameters are written after the function name:

```stark
fn T Identity<T>(T value) {
    return value;
}
```

The caller usually does not need to write the type argument. Stark can infer
`T` from the argument and return context:

```stark
stack i32[min max] answer = Identity(42);
stack bool flag = Identity(true);
```

These are different concrete uses of the same source function.

## Step 2: Make Reusable Types Concrete Where They Are Used

Structs, records, and enums can also be generic:

```stark
struct Box<T> {
    T Value;
}

enum Option<T> {
    None,
    Some(T),
}
```

When using a generic type in a value position, write the concrete type
arguments:

```stark
Box<i32[min max]>
Option<bool>
```

Generic `Option<T>` or `Result<T, E>` shapes are ordinary Stark enums. They are
not compiler magic.

{{< stark-sample "assets/book/samples/generic-option.stark" >}}

Notice the concrete variant constructors:

```stark
Option<i32[min max]>.Some(7)
Option<bool>.Some(true)
```

The source generic is reusable, but each value still has a concrete type. That
is the key distinction to keep in mind when reading Stark generic code.

## Step 3: Let Specialization Stay Static

Stark's generic model is static. A generic function used with `i32[min max]`
and the same generic function used with `bool` are compiled as concrete
instantiations for those types.

That fits Stark's performance goals:

- no required runtime generic dispatch
- no hidden boxing for ordinary generic calls
- no reflection requirement
- more room for inlining and constant propagation

The tradeoff is that generic code must be valid for the concrete operations it
actually performs. A generic function that adds two values needs an explicit
static contract proving addition is available for those values.

## Step 4: Treat Traits As Compile-Time Contracts

Traits declare behavior contracts. They are compile-time contracts, not runtime
objects.

Keep these trait rules in mind while building examples:

- no trait objects
- no vtable-style runtime dispatch values
- trait names are not ordinary runtime value types

This is different from languages where a trait or interface value can hold an
unknown implementation behind a pointer. Stark keeps ordinary dispatch static
unless a feature explicitly asks for indirection.

This rejected example shows the boundary: `Comparable` can describe a contract,
but it cannot be stored as a field, allocated, or used as a local runtime
object.

{{< stark-sample "assets/book/negative-samples/trait-runtime-value.stark" >}}

## Step 5: Put Proof-Oriented Rules In Doctrines

Doctrines are compile-time bundles of law-like behavior and constraints. They
are intended for proof-oriented APIs rather than runtime object modeling.

Like traits, doctrines have no ordinary runtime identity. They are a way to
organize static facts, not a way to allocate objects or dispatch dynamically.

Call doctrine members through the doctrine name:

{{< stark-sample "assets/book/samples/doctrine-facts.stark" >}}

The sample groups score-related laws without creating a `ScoreRules` value.
That is the current doctrine model in miniature: organize static facts, keep the
runtime value model concrete.

## Step 6: Preserve Constraints Across Package Boundaries

Generic package APIs should preserve the same story across package boundaries:
the API is reusable, but each concrete use still has a concrete type. That lets
package-backed code remain compatible with Stark's static dispatch bias.

When documenting a package API, write the constraints in the API and examples.
Do not imply that unconstrained generic code can perform arbitrary operations on
`T`.
