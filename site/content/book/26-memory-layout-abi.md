+++
title = "26. Memory Layout, ABI, and Interop Expectations"
weight = 260
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/25-performance-model/"
next = "/book/27-integers-floats-overflow/"
+++

# Memory Layout, ABI, and Interop Expectations

This chapter takes an aggregate from ordinary Stark source to an interop
boundary. The tutorial rule is to use source-facing layout for Stark code and
design ABI-facing layout separately.

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

## Step 1: Start With Source-Facing Layout

Some layout facts are visible in source because they affect ordinary code:

- a `struct` or `record` has named fields
- a fixed array has a fixed element count
- a slice is a view, not backing storage
- a raw pointer is pointer-shaped and may be `null`

These facts are enough for everyday Stark code to read fields, index arrays,
borrow values, and pass values inside Stark modules and packages.

{{< stark-sample "assets/book/samples/aggregates-layout.stark" >}}

The aggregate sample is source-facing layout: `Rectangle.Width`, `Rectangle.Height`,
`Point.X`, and `Point.Y` are the API the Stark program uses. That does not mean
the same types should automatically be treated as C ABI records. Source-facing
field access and binary representation stability are separate promises.

## Step 2: Keep Representation Freedom Inside The Package

Ordinary Stark types are not automatically stable C ABI contracts. The compiler
should retain freedom to choose efficient internal representation when no
source-level boundary promises otherwise.

That freedom matters for performance. It lets Stark keep implementation details
inside a package while exposing only the source API that downstream Stark code
needs.

## Step 3: Choose `public` Or `export` By The Boundary

`public` is source visibility. It lets downstream Stark code use a declaration.

`export` is ABI visibility. It asks for a native symbol that code outside Stark
can find.

Do not use `export` just because a declaration is part of a Stark package API.
Use `export` when the declaration is truly a binary boundary: an entrypoint,
plugin hook, runtime hook, or FFI-facing function.

## Step 4: Design The C Surface Explicitly

C-facing APIs should be small and explicit:

- prefer scalar values and raw pointers at the boundary
- keep ownership transfer documented in the wrapper API
- convert raw or platform-specific failures into Stark result/status values
- avoid exposing ordinary Stark enums as C ABI values
- keep foreign unwinding out of Stark frames

This rejected example is the rule in code:

{{< stark-sample "assets/book/negative-samples/enum-abi-boundary.stark" >}}

If an aggregate must cross the C boundary, design it as an interop type rather
than assuming an ordinary internal type should double as an ABI type.

## Step 5: Hide Raw Details Behind The Package Surface

Package boundaries are source boundaries first. A package can expose public
Stark APIs while keeping helpers, raw FFI declarations, and native shims behind
the package surface.

That is the preferred shape: keep raw details close to the native dependency
and publish a smaller Stark API.
