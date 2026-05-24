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
to hide expensive behavior.

That means Stark is not trying to be the most permissive language. It is trying
to make ordinary safe code friendly to visible guarantees, predictable layout,
deterministic cleanup, and aggressive optimization.

## Step 1: Start From Stark's Bet

Stark's bet is that restrictions can be a feature:

- safe code has no garbage collector
- ownership is explicit and deterministic
- borrows are non-null and non-escaping by default
- allocation is visible in the API shape
- failure is expressed with values or traps, not hidden unwinding
- direct calls and narrow visibility are the default
- native interop boundaries are explicit

The goal is to make the source clear enough that the fast path is usually the
obvious path.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

This small program is a Stark-shaped example of that bet. The fixed array,
explicit stack locals, ranged integer index, `finite law` helpers, and ordinary
exit code all make cost and behavior visible in the program.

## Step 2: Notice What Stark Refuses To Hide

Stark does not optimize for unrestricted convenience. It intentionally avoids
some features that are common in other languages when those features would make
aliasing, allocation, dispatch, or failure behavior less clear.

This is why the language treats concepts such as `borrow`, `retborrow`,
`frozen`, `out`, `finite`, and `law` as important source-level tools rather
than implementation details.

## Step 3: Treat The Safe Subset As The Fast Path

In Stark, safe code is meant to be the maximally optimizable code. Low-level
escape hatches exist, but they are explicit. When a program uses raw pointers,
FFI, native package metadata, or future unsafe regions, it is crossing a visible
boundary.

The smallest executable shape keeps that same idea: the entrypoint returns an
ordinary status value and does not hide startup behavior behind reflection or
exception machinery.

{{< stark-sample "assets/book/samples/hello-return-code.stark" >}}

The rest of this book teaches Stark from that point of view: not "how do I
write Rust in different syntax?" or "how do I write C# without a runtime?", but
"how do I write Stark so the language can keep its promises?"
