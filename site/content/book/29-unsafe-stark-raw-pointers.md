+++
title = "29. Unsafe Stark and Raw Pointers"
weight = 290
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/28-performance-tuning/"
next = "/book/30-reading-diagnostics/"
+++

# Unsafe Stark and Raw Pointers

Unsafe Stark marks proof boundaries. It does not turn off parsing,
type-checking, ownership, initialization, range checking, or borrow validation.
Use unsafe code when the program depends on a fact the compiler cannot prove
from safe Stark alone.

## Step 1: Isolate The Unsafe Boundary

Keep raw pointer and FFI work small.

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

The safe part of the wrapper should check nullability, translate platform
status into Stark result/status values, and expose ordinary Stark types where
possible. Raw pointers should not leak through a public API unless the API is
deliberately low-level.

## Step 2: Prefer Bounded Raw Pointer Regions

When unsafe code processes contiguous memory, prefer bounded raw pointer
parameters over unbounded raw pointers.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

`rawptr<T>[count]` and `rawmutptr<T>[count]` make the memory region visible.
That is the difference between "some address" and "this many elements from this
base." The latter can participate in disjoint checks, subregion contracts, and
accepted `independent` loops.

## Step 3: Assert Only Facts You Audited

An `unsafe` block does not automatically prove non-overlap. If a low-level
boundary knows two regions are separate but safe Stark cannot prove it, write a
scoped assertion:

```stark
unsafe assume disjoint(source[0, count], destination[0, count]) {
    CopyFast(source, destination);
}
```

Inside an `unsafe fn` or an existing `unsafe { ... }` block, the leading
`unsafe` is optional:

```stark
assume disjoint(source[0, count], destination[0, count]) {
    CopyFast(source, destination);
}
```

The assertion is a promise by the unsafe boundary. It should name visible roots
or representable subregions, not pointer values laundered through integers or
hidden helper calls.

## Step 4: Keep Safe Borrows Stronger Than Raw Pointers

Raw pointers may be null and may alias unless a contract proves otherwise.
Safe borrows are non-null and validated by Stark's borrow rules.

This rejected sample shows one boundary the compiler must keep:

{{< stark-sample "assets/book/negative-samples/rawptr-strengthen-mutability.stark" >}}

Do not convert a readonly raw pointer into mutable authority. If native code
needs mutable access, make that mutability visible in the raw pointer type and
in the wrapper's contract.

## Step 5: Return To Safe Stark Quickly

A good unsafe API has a small trusted core and a boring safe surface. Keep the
unsafe proof local, then move back into ordinary Stark values, result enums,
bounded slices, or caller-owned buffers. That preserves the performance benefit
without making the rest of the program reason like C.
