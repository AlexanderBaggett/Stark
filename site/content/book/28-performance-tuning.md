+++
title = "28. Performance Tuning"
weight = 280
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/27-integers-floats-overflow/"
next = "/book/29-unsafe-stark-raw-pointers/"
+++

# Performance Tuning

This chapter turns the performance model into a repeatable tuning loop. The
goal is not to write surprising code. The goal is to make the fast facts the
program already depends on visible in source, then confirm they survive through
compilation.

## Step 1: Start From Clear Source Facts

Begin with the smallest kernel that still represents the work.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

The useful facts are all ordinary Stark facts:

- the loop works over fixed storage
- the helper functions are `finite law`
- the integer ranges are explicit
- no allocation or dynamic dispatch is hidden in the kernel

This is the baseline style. If the kernel cannot be explained this plainly,
separate setup, allocation, IO, and parsing from the hot path before tuning.

## Step 2: Make Non-Overlap Explicit

When a hot path touches multiple memory regions, decide whether overlap is
allowed.

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

Default Stark parameters are non-overlapping when they are memory-backed.
Use `where overlap(...)` only for an API that is intentionally overlap-safe.
Use `where same(...)` when two parameters must name the same region. Use
`if disjoint(...)` when one function supports both a fast disjoint branch and
an overlap-safe branch.

## Step 3: Give Raw Pointer Loops Bounds

Raw pointer code only becomes optimizer-friendly when the source names a
bounded region.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

The bound turns a raw pointer into a region fact: base, element type, count,
mutability, and any disjoint subregion contract. That lets an accepted
`independent` loop carry no-loop-carried-memory-dependence facts down to LLVM
without pretending the pointer is a safe borrow.

## Step 4: Use `inline` Deliberately

Use `inline` for tiny wrappers whose call overhead or abstraction boundary is
part of the hot path. Use `inlinehint` for helpers that are usually profitable
but should still be budgeted by the optimizer. Use `noinline` for boundaries
that protect code size, diagnostics, platform calls, or benchmarking setup.

The source contract still comes first. Inlining is most useful after ranges,
storage, and memory relations are already visible.

## Step 5: Measure, Inspect, And Repeat

Tune in a loop:

1. Run the benchmark in `Release`.
2. Compare against C and Rust using C as `1.0`.
3. Inspect optimized LLVM IR only for the specific fact under question.
4. Change source or compiler lowering when a Stark guarantee is missing.
5. Rerun the benchmark and keep the result with the change.

Do not accept a benchmark win that weakens ownership, skips cleanup, changes
the public API shape for convenience, or moves required work out of the timed
region in only one language.
