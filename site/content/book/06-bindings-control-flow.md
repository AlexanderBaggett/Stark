+++
title = "6. Bindings, Mutation, and Control Flow"
weight = 60
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/05-values-types-ranges/"
next = "/book/07-ownership-moves-drops/"
+++

# Bindings, Mutation, and Control Flow

Most Stark code reads like a C-family language, but Stark makes local storage,
mutation, and loop intent more explicit.

{{< stark-sample "assets/book/samples/bindings-control-flow.stark" >}}

## Locals Need Storage

A local binding starts with a storage class. The most common one is `stack`:

```stark
stack i32[min max] answer = 42;
stack mut i32[min max] total = 0;
```

The first binding is immutable after initialization. The second one is mutable
because it writes `mut`.

This is the shape to remember:

```stark
stack mut Type name = value;
```

Leave out `mut` when the binding should not be reassigned or updated.

The compiler rejects reassignment through an immutable local:

{{< stark-sample "assets/book/negative-samples/immutable-local-assignment.stark" >}}

## Assignment Is Visible Mutation

Mutable bindings can be assigned and can use compound assignment:

```stark
total += index;
index += 1;
```

That mutation is visible in the source. Stark does not hide mutation through
implicit property setters, reflection, or dynamic dispatch.

## Branches And Returns

`if` branches are ordinary statement blocks:

```stark
if (SumTo(4) != 10) {
    return 1;
}
```

Functions with a non-void return type must return a value on every accepted
path. This matters most for functions marked `finite`, because `finite` says
the function is expected to make progress and return.

## Loop Intent

The sample uses:

```stark
for willexit (stack mut i32[min max] index = 0; index < count; index += 1) {
    ...
}
```

`willexit` is a source-level promise about loop shape. Stark uses forms like
this because an unbounded loop is not the same contract as a loop that should
eventually exit. When a loop is intentionally not known to exit, the source
should use the form that says so.

`continue` and `break` are still explicit branch statements. In the sample,
`continue` skips early indexes and `break` stops before adding `stopAt`; both
choices are visible in the loop body rather than hidden behind an iterator
adapter.

## Switch

`switch` is the multi-way branch form. It is useful when the value being tested
is part of the program's explicit data model:

```stark
switch (value) {
    case 42:
        return true;
    default:
        return false;
}
```

Later enum chapters build on `switch` for variant-shaped data.
