+++
title = "10. Storage Classes and Lifetimes"
weight = 100
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/09-borrowing-vs-rust/"
next = "/book/11-aggregates-layout/"
+++

# Storage Classes and Lifetimes

Stark wants storage to be visible. A local declaration does not merely say
"there is a variable." It says where the value lives.

{{< stark-sample "assets/book/samples/storage-lifetimes.stark" >}}

## Step 1: Put Temporary Values On The Stack

`stack` is the ordinary local storage class:

```stark
stack i32[min max] answer = 42;
stack mut Counter counter = new Counter() { Value = 0 };
```

Use `stack` when the value belongs to the current scope and should be cleaned up
when that scope exits.

In the sample, both `values` and `total` belong to `main`. `TryTotal` receives
the fixed array by value and writes the result into caller-owned storage through
`out`. The function does not allocate a result object or return a hidden buffer.

That is the storage habit Stark wants to encourage: make the owner visible, make
the destination visible, and make mutation explicit with `mut` or `out`.

## Step 2: Use Heap Storage Only When Ownership Needs Allocation

`heap` is for owned values that need allocation-backed storage. A heap value is
still owned. It is not a garbage-collected reference and it does not make
cleanup optional.

The important rule is the same as for stack values: ownership decides who is
responsible for the value.

Choose heap storage when a value must outlive the current stack frame or when
the API deliberately returns owned allocation-backed data. Do not choose heap
storage simply because another language would silently allocate for the same
operation.

## Step 3: Group Shared-Lifetime Work In An Arena

`arena` is for region-style allocation. The useful mental model is:

- many values can be allocated in the same region
- the region has a lexical lifetime
- cleanup happens in bulk when that region ends

That makes arena storage useful for workloads where many temporary values share
the same lifetime.

Arena storage should still be visible in the API shape. If a parser, compiler
pass, or temporary graph builder needs an arena, pass that region deliberately
instead of letting allocation policy disappear behind ordinary calls.

## Step 4: Keep Globals Rare And Explicit

Global declarations are not locals and do not use the same local declaration
shape. The main global categories are:

- `const` for compile-time scalar facts
- immutable global values
- mutable global values, when the source explicitly asks for that shared state

Keep globals rare. A global widens the part of the program that can observe or
depend on a value, which weakens local reasoning.

## Step 5: Read Storage Choices From The Signature

Storage classes are part of Stark's performance model because hidden storage is
hidden cost. A reader should be able to tell whether code is using stack
storage, allocation-backed ownership, region storage, or global state without
guessing.

This also helps API design. If a function can fill caller-owned storage instead
of allocating, the signature should say that with an `out` destination or a
borrowed buffer rather than hiding allocation inside the call.
