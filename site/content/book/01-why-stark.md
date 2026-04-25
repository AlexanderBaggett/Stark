+++
title = "1. Introduction: Why Stark Exists"
weight = 10
book_part = "Part I: First Contact"
book_status = "draft"
next = "/book/02-installing-stark/"
+++

# Introduction: Why Stark Exists

Stark is a performance-first systems language. Its design starts from a blunt
premise: the fastest code is easier to produce when the source language refuses
to hide expensive or hard-to-prove behavior.

That means Stark is not trying to be the most permissive language. It is trying
to make ordinary safe code friendly to static proof, predictable layout,
deterministic cleanup, and aggressive optimization.

## Stark's Bet

Stark's bet is that restrictions can be a feature:

- safe code has no garbage collector
- ownership is explicit and deterministic
- borrows are non-null and non-escaping by default
- allocation is visible in the API shape
- failure is expressed with values or traps, not hidden unwinding
- dispatch and visibility are closed-world by default
- native interop boundaries are explicit

The goal is not merely to generate LLVM IR. The goal is to give the compiler
facts that are strong enough to matter.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

This small program is a Stark-shaped example of that bet. The fixed array,
explicit stack locals, ranged integer index, `finite law` helpers, and ordinary
exit code all expose facts that another language might leave implicit.

## What Stark Does Not Optimize For

Stark does not optimize for unrestricted convenience. It intentionally avoids
some features that are common in other languages when those features would make
aliasing, allocation, dispatch, or failure behavior less clear.

This is why the language treats concepts such as `borrow`, `retborrow`,
`frozen`, `out`, `finite`, and `law` as important source-level tools rather
than implementation details.

## The Safe Subset Is The Fast Subset

In Stark, safe code is meant to be the maximally optimizable code. Low-level
escape hatches exist, but they are explicit. When a program uses raw pointers,
FFI, native package metadata, or future unsafe regions, it is crossing a visible
boundary.

The rest of this book teaches Stark from that point of view: not "how do I
write Rust in different syntax?" or "how do I write C# without a runtime?", but
"how do I write Stark so the language can keep its promises?"
