+++
title = "4. A Small Stark Tour"
weight = 40
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/03-hello-stark/"
next = "/book/05-values-types-ranges/"
+++

# A Small Stark Tour

This sample uses a module, a `finite law` function, explicit stack locals,
mutation, a `while` loop, and an exported entrypoint.

{{< stark-sample "assets/book/samples/small-tour.stark" >}}

## Function Kinds

`finite law` is stronger than a plain `fn`.

- `finite` says the function is expected to return and make progress.
- `law` says the function is pure enough for strong reasoning.
- `finite law` combines both expectations.

Stark keeps this visible in the source because function guarantees affect what
callers may rely on and what the compiler can prove.

The tour uses `finite law` for `SumTo` because the function is a deterministic
calculation over its input. It does not allocate, perform IO, synchronize, or
cross an FFI boundary. A later API should not weaken that promise casually,
because callers may start depending on the stronger behavior.

## Explicit Storage

The locals use `stack`. Stark does not infer hidden heap allocation for this
kind of loop. The storage class is part of the source-level story.

`stack mut` says both where the value lives and whether it can be reassigned.
Those are separate decisions. A stack local can be immutable, and a mutable
binding still has a concrete storage location.

The loop uses `while willexit` because progress is part of the contract Stark
is trying to preserve. When code claims a loop exits, the source should make
that claim visible instead of leaving it as a backend guess.

## Ranged Integers

The parameter uses `i32[0 max]`, not just `i32`. Stark integer types carry range
information because range facts are useful for diagnostics, checks, and
optimization.

The range also documents intent for readers. `SumTo` accepts non-negative
limits, while its accumulator uses the full `i32` range because the running
total is allowed to grow beyond the input bound.
