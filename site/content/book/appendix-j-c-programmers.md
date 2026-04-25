+++
title = "Appendix J: Stark for C Programmers"
weight = 440
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-i-csharp-programmers/"
+++

# Appendix J: Stark for C Programmers

Stark is close to C in its concern for layout, ABI, and predictable native
execution, but safe Stark code deliberately removes many C hazards.

## Familiar Ground

- explicit integer widths
- raw pointers for FFI and low-level code
- `export ffi fn main` as the hosted entrypoint shape
- status/result values instead of exceptions
- direct native code generation through LLVM

## Stronger Rules

- owned values clean up deterministically
- moved values cannot be reused
- safe borrows are non-null
- safe code does not use arbitrary pointer arithmetic as ordinary programming
- integer ranges are part of source types
- ordinary overflow is not a feature; use wrapping or saturating operators

## Headers And Modules

Stark modules replace C header/source include patterns. A module is a source
namespace and package unit. Use `import` instead of textual includes.

## FFI

Use C shims when a native API has an awkward ABI surface. Keep raw pointers and
foreign ownership rules behind a small Stark package API when possible.

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

The important habit is containment. Raw pointers are available, but they should
not leak through ordinary safe APIs unless the API is deliberately a low-level
boundary. Wrap foreign ownership and nullability near the C edge.

Stark also keeps raw pointer mutability explicit. A readonly raw pointer cannot
be cast into a mutable one:

{{< stark-sample "assets/book/negative-samples/rawptr-strengthen-mutability.stark" >}}

## Memory Management

Do not manually `malloc` and `free` in ordinary Stark code. Use owned values,
standard-library collections, and explicit allocator-aware constructors.

Raw allocation belongs in `System.Memory`, runtime internals, or carefully
reviewed FFI packages.
