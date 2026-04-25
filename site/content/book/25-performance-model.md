+++
title = "25. Stark's Performance Model"
weight = 250
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/24-testing-stark-code/"
next = "/book/26-memory-layout-abi/"
+++

# Stark's Performance Model

Stark treats restrictions as part of the performance model. The goal is not to
make source code ceremonial. The goal is to keep important cost and proof facts
visible enough that the compiler can generate simple native code.

{{< stark-sample "assets/book/samples/small-tour.stark" >}}

## Safe Code Is The Fast Subset

Safe Stark code is designed to preserve strong facts:

- owned values have clear owners
- borrows are non-null
- ordinary borrows do not escape by default
- storage is visible
- failure is returned as data
- dispatch is static unless indirection is explicit

These are not only safety rules. They are optimization rules. If the compiler
can trust them, it can make stronger decisions before handing code to LLVM.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

This sample is small, but it shows the kind of source Stark wants performance
work to start from:

- the array has fixed storage and a visible length
- the loop state is explicit stack storage
- the helper functions are `finite law`
- the entrypoint reports success with an ordinary exit code
- no collection, text, or allocator API is involved

Before reaching for generated IR, first ask whether the source already says the
facts you expect the compiler to rely on.

## No Hidden Allocation

Allocation should be visible in the API. A growable collection may allocate
when it grows, so its mutating methods return `System.Memory.MemoryStatus`.
Owned text conversion returns allocation-aware result values. Caller-owned text
buffers write their capacity in the source.

Small value helpers should not hide allocation behind pleasant syntax.

## No Hidden Unwinding

Stark has no general exception unwinding model. Recoverable failure is ordinary
data. Unrecoverable failure is a trap-or-abort style path.

That makes cleanup and FFI boundaries easier to reason about. A C call should
not suddenly need to understand Stark stack unwinding, and a Stark destructor
should not become part of an exception-control-flow story.

## Static Dispatch By Default

Ordinary function calls are direct. Generic functions are instantiated for
concrete use sites. Traits and doctrines are compile-time contracts rather than
runtime object pointers.

When you want indirection, write it:

```stark
fnptr<fn i32[min max](i32[min max])>
```

The source should say when a call becomes a callable value.

## Explicit Storage

`stack`, `heap`, `arena`, fixed arrays, slices, `Ascii`, and `Unicode` all
carry storage meaning. Stark avoids making an owned backing allocation appear
from a slice literal or a text expression without the source naming the storage
choice.

This is why fixed-array initializers and slice views are separate concepts.

## Native Boundaries

FFI, raw pointers, and native package metadata are deliberately explicit. Stark
lets you cross into C or platform APIs, but it does not pretend those calls have
the same guarantees as ordinary safe code.
