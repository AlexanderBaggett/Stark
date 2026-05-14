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

## Memory Separation Contracts

`disjoint` is how Stark lets source code state that memory regions do not
overlap. The compiler uses that fact for call validation and, when it reaches
LLVM, for `noalias` and scoped alias metadata.

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

The sample has two paths. `AddSeparate` requires non-overlapping pointer
arguments, so the compiler will not accept arbitrary raw pointer variables at a
safe call site. `TryAddPairFast` first runs `if disjoint(left, right)`. Only
the true branch receives the separation fact and can call the faster contract.
The false branch keeps an overlap-safe implementation.

This distinction is intentional. Different pointer names are not proof that
the pointed-to regions differ. Proof can come from distinct fields, distinct
constant indexes, separately addressed storage, declared `where disjoint(...)`
contracts, or a runtime `if disjoint(...)` check that dominates the call.

Loop `independent` uses the same design philosophy. The current compiler
accepts scalar loops and a canonical memory-backed subset over slices, fixed
arrays, and bounded raw pointer regions. Accepted memory operations carry LLVM
access-group metadata, and accepted loops carry `parallel_accesses` plus the
existing progress facts.

## Bounded Raw Pointer Regions

Raw pointer performance code can give Stark a precise region without turning
the pointer into a safe borrow:

```stark
rawmutptr<i32[min max]>[count]
```

The bound says the raw pointer is valid for `count` contiguous elements. A
positive count requires a non-null pointer; a zero-length region may use
`null`. Region expressions such as `source[0, count]` and
`destination[start, count]` can appear in `where disjoint(...)` and
`if disjoint(...)` so fast paths are still tied to explicit source facts.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

The checked sample contains the common pattern:

- `CopyFast` uses bounded raw pointer parameters and an `independent` loop.
- `TryCopy` uses `if disjoint(...)` to choose an `independent` copy loop.
- `Fill` writes one element per iteration through a bounded `rawmutptr`.
- `Transform` reads from one bounded region and writes another.
- the false branch falls back to an overlap-safe temporary copy when ranges
  overlap.

When an unsafe boundary needs an ordinary slice view, `slice(pointer, count)`
materializes one from the bounded raw pointer region inside an `unsafe` block.
The slice keeps the raw region's root, length, mutability, const provenance,
alignment, and disjoint facts, so normal slice indexing and loop validation can
take over after the boundary conversion.

## Const Parameter Provenance

`const` on a parameter means the reachable object graph is deeply immutable.
It is stronger than an ordinary immutable binding and stronger than a temporary
`frozen` borrow. The caller must pass a value whose provenance is already const,
or a value the compiler can prove is equivalently immutable.

{{< stark-sample "assets/book/samples/const-parameter-provenance.stark" >}}

Inside `ReadMiddle`, the compiler can treat the table graph as readonly.
`ForwardConst` can forward that provenance to another const parameter without
weakening it. A projection such as `table.Values[1]` does not regain mutation
authority. If a const parameter contains a raw mutable pointer field, reading
that field through the const graph yields readonly access, not a mutable raw
alias.

Const is not an aliasing promise. Two const parameters may point at the same
immutable graph, so `const` and `disjoint` remain separate contracts.

## Independent Loop Contracts

`independent` is the loop-level form of the same proof-carrying style:

```stark
stack mut i32[0 10] value = 0;
while willexit independent (value < 4) {
    value += 1;
}
```

The intended contract is semantic, not a soft optimization hint. A write in
one iteration must not be read or written by another iteration unless the
compiler has a proof that the accessed regions are per-iteration separate.
Those proofs are expected to come from index ranges, exclusive borrows,
disjoint slice regions, and call memory effects.

The compiler accepts canonical slice, fixed-array, and bounded raw pointer
region element accesses when the loop induction variable is the element index
and each read/write root pair is either the same indexed root or proven
disjoint. Raw pointer loops use the spelling `*(&root[index])` when `root` has
a bounded raw pointer region. Unbounded raw pointer dereferences, hidden roots,
non-induction indexes, nested loops, early exits, and calls with unproven memory
effects report `STK3027`.

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

The source should say when a call becomes a callable value. It should also say
which guarantees survive that indirection. A `fnptr<finite law ...>` callback is
still a thin function pointer at runtime, but it tells the compiler that the
indirect target preserves progress, return, purity, and readonly behavior. That
lets LLVM see call-site facts such as progress/return and memory effects even
when the target cannot be statically inlined or devirtualized.

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

## Backend Optimization Boundaries

By default, optimized Stark builds try to give LLVM a broad view of the program.
When source or package bodies are available, the compiler can choose
whole-program-style optimization so small standard-library helpers and package
functions can inline into callers.

Some modules intentionally need a compiled backend boundary:

```stark
[Backend(Opaque)]
module System.Memory
```

The same attribute can be used on a narrower callable or type-owned surface:

```stark
[Backend(Opaque)]
finite law i32[0 max] Hash(i32[0 max] value) {
    return value;
}
```

`[Backend(Opaque)]` keeps the source API visible, but prevents callers from
looking through the marked boundary for ThinLTO, cross-module inlining, backend
cloning, or backend specialization. It is for runtime, platform, and interop
code where the boundary itself is part of the performance or correctness
strategy. Prefer a function or type boundary over a whole-module boundary when
that gives the compiler more safe code to optimize.
