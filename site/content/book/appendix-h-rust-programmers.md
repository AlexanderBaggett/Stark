+++
title = "Appendix H: Stark for Rust Programmers"
weight = 420
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-g-unsupported/"
next = "/book/appendix-i-csharp-programmers/"
+++

# Appendix H: Stark for Rust Programmers

Rust instincts help, but Stark is not Rust with C#-like syntax.

## Similar Instincts

- ownership matters
- moves make old bindings unavailable
- borrowing is statically checked
- deterministic cleanup is preferred over garbage collection
- raw pointers are explicit low-level values

## Important Differences

- ordinary safe borrows are non-escaping by default
- returned borrows use `retborrow`
- safe borrows are never `null`
- `frozen` expresses deep immutability
- `out` expresses caller-owned write destinations
- `stack`, `heap`, and `arena` are visible storage choices

## Traits And Dispatch

Stark traits are compile-time contracts, not trait objects. There is no ordinary
`dyn Trait` equivalent and no standard vtable-style dynamic dispatch model.

Use direct calls, generic instantiation, and explicit `fnptr` values when
indirection is intended.

## Smart Pointers

Do not reach for `Rc`, `Arc`, `RefCell`, or `Box` as default design patterns.
Stark does not currently provide safe standard equivalents for general shared
ownership or interior mutability. Keep ownership direct unless the language
surface explicitly says otherwise.

## Panic And Errors

Stark has no general unwinding. Recoverable failure is a return value. Traps are
for unrecoverable paths.

{{< stark-sample "assets/book/samples/out-parameter.stark" >}}

This is the Stark instinct to reach for instead of `Result<T, E>` in every
small fallible helper. If the caller should own the destination storage, return
a status value and write through `out`. Use a result-shaped enum when the caller
needs the successful value and the failure reason in one returned value.

## Borrowing Habit Shift

Do not start by inventing lifetime parameters. Start by asking whether a borrow
needs to escape at all:

- if it is temporary, use `borrow` or `mut borrow`
- if it returns to the caller, use `retborrow`
- if it must be stored, use the explicit stored-borrow form
- if the API crosses FFI or permits null, use raw pointers at the boundary and
  convert only after checking the facts

## Packages

Stark uses `Stark.toml` and `Stark.solution.toml` rather than Cargo manifests.
Path dependencies are the current ordinary package story.
