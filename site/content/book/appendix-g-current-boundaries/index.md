+++
title = "Appendix G: Current Boundaries"
weight = 430
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-f-package-manifest/"
next = "/book/appendix-h-rust-programmers/"
slug = "appendix-g-current-boundaries"
aliases = ["/book/appendix-g-unsupported/"]
+++

# Appendix G: Current Boundaries

This appendix summarizes source patterns that tutorials handle deliberately.
The goal is to keep book examples aligned with implemented Stark behavior
instead of implying hidden runtime features.

## Testing

`stark test`, `kind = "test"` manifests, and `System.Testing` use explicit fact
runners. Book examples use executable return codes unless the chapter is
specifically teaching the test project model.

{{< stark-sample "samples/manual-test-executable.stark" >}}

This pattern is intentionally plain: a checked executable returns `0` when its
conditions pass and a non-zero code when one fails. Do not write examples that
imply reflection, hidden discovery, or exception unwinding. Tests are ordinary
Stark calls that report ordinary status.

## Command-Line Arguments

Project chapters write parse/processing logic as ordinary functions and keep
`main` small. That makes the tutorial useful without depending on a special
hosted argument abstraction.

{{< stark-sample "samples/text-tool-core.stark" >}}

The parse-and-status shape is the important lesson: input becomes a text view,
domain logic returns a status enum, and `main` converts that status to a
process exit code.

## Unsafe And Raw Pointers

FFI declarations, exported ABI entrypoints, raw pointer signatures, `null`,
raw pointer casts, dereference, pointer arithmetic, and raw slice construction
must be written inside an explicit unsafe boundary. Examples should use
`unsafe ffi fn`, `export unsafe fn`, `export unsafe ffi fn`, `unsafe fn`, or a
small `unsafe { ... }` block as appropriate. Safe hosted entrypoints should use
plain `export fn main`.

Prefer safe examples built from borrows, slices, `dynamic`, owned handles, and
standard-library wrappers. Use raw pointers only when the chapter is directly
teaching FFI, platform ABI work, or a deliberately low-level standard-library
boundary.

## Constrained Generics

Generic functions and types exist. Do not imply arbitrary operations are valid
for every `T`; teach generic code through operations that are actually
available from the function body or static contract.

## Capturing Lambda Lowering

Non-capturing lambdas work as function-pointer values. Capturing lambdas have
an explicit source shape, but tutorials should not present them as ordinary
`fnptr` values.

{{< stark-sample "rejected/capturing-lambda-fnptr.stark" >}}

The rejected sample is the useful teaching point: a capturing callable is not a
thin function pointer.

## Concurrency And IO

The standard library has thread lifecycle APIs and blocking TCP. It does not
provide async/await, thread pools, channels, mutexes, semaphores, non-blocking
socket event loops, HTTP, TLS, or DNS helpers.

## Runtime Object Features

Stark does not have OOP inheritance, runtime reflection, dynamic
trait/interface objects, general interior mutability, or standard smart-pointer
families like `Rc`/`RefCell`.

When in doubt, prefer examples that use implemented source forms and make every
cost or proof boundary visible.
