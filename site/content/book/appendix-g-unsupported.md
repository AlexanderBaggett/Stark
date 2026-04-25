+++
title = "Appendix G: Unsupported and Future Features"
weight = 410
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-f-package-manifest/"
next = "/book/appendix-h-rust-programmers/"
+++

# Appendix G: Unsupported and Future Features

This appendix summarizes features that should not be implied by examples yet.

## Testing

`stark test`, test-project manifests, and `System.Testing` are v2.0 work.
Current examples should use executable return codes.

{{< stark-sample "assets/book/samples/manual-test-executable.stark" >}}

This pattern is intentionally plain: a checked executable returns `0` when its
conditions pass and a non-zero code when one fails. Do not write examples that
look like a Stark-native test framework until that module and CLI behavior
exist.

## Command-Line Arguments

The canonical hosted argument model is future work. Project chapters should
write parse/processing logic as ordinary functions and keep `main` small until
argument passing lands.

{{< stark-sample "assets/book/samples/text-tool-core.stark" >}}

The fixed input is a placeholder for the future hosted argument model. The
parse-and-status shape is the part that is valid today.

## Constrained Generics

Generic functions and types exist. Full user-defined constrained generics are
still roadmap work. Do not imply arbitrary operations are valid for every `T`.

## Capturing Lambda Lowering

Non-capturing lambdas work as function-pointer values. Capturing lambdas have
an explicit source shape, but use them carefully until capture lowering is
complete for the desired target.

{{< stark-sample "assets/book/negative-samples/capturing-lambda-fnptr.stark" >}}

The source shape is reserved and checked, but this should not be presented as a
working `fnptr` pattern yet.

## Concurrency And IO

The current standard library has thread lifecycle APIs and blocking TCP. It
does not yet provide async/await, thread pools, channels, mutexes, semaphores,
non-blocking socket event loops, HTTP, TLS, or DNS helpers.

## Runtime Object Features

Stark does not currently have OOP inheritance, runtime reflection, dynamic
trait/interface objects, general interior mutability, or standard smart-pointer
families like `Rc`/`RefCell`.

When in doubt, check the roadmap and prefer examples that use implemented
source forms.
