+++
title = "Appendix E: Storage Classes and Ownership Quick Reference"
weight = 390
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-d-function-kinds/"
next = "/book/appendix-f-package-manifest/"
+++

# Appendix E: Storage Classes and Ownership Quick Reference

This appendix summarizes the ownership and storage vocabulary used throughout
the book.

## Local Storage

- `stack`: ordinary local storage owned by the current scope
- `heap`: allocation-backed owned storage
- `arena`: region-style owned storage reclaimed with the arena region
- `register`: reserved low-level storage spelling

## Globals

- `const`: deeply frozen global object graph
- `static`: immutable global binding
- `static mut`: mutable global rebinding

Keep globals rare because they widen the part of the program that can observe
or depend on a value.

## Ownership

- values are owned by default
- moves transfer ownership
- moved bindings cannot be read until reinitialized
- owned values still live at scope exit are dropped
- safe code has no general `forget` escape hatch

## Borrow Forms

- `borrow T`: temporary non-owning access; non-escaping by default
- `mut borrow T`: temporary mutable non-owning access
- `retborrow T`: borrow deliberately returned to the caller
- `storeborrow T`: borrow allowed to be stored or otherwise escape
- `frozen T`: deeply immutable access
- `out T`: caller-owned write destination
- `init T`: initialization destination

Safe borrows are non-null. Use raw pointers only at explicit low-level
boundaries.

## Frozen Access

{{< stark-sample "assets/book/samples/frozen-read.stark" >}}

`frozen` is the spelling to reach for when an API needs deep read-only access.
The value can be read, but reachable storage cannot be mutated through that
access.

{{< stark-sample "assets/book/negative-samples/frozen-write.stark" >}}

## Small Example

{{< stark-sample "assets/book/samples/storage-lifetimes.stark" >}}

Read this sample from the storage outward:

- `values` is owned stack storage in `main`
- `total` is mutable caller-owned stack storage
- `TryTotal` writes through `out` instead of allocating a result
- `ExpectedTotal` is a compile-time fact, not a mutable global

That is the style this appendix is summarizing: ownership, storage, and escape
behavior should be visible in the source shape.
