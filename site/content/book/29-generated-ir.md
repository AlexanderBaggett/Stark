+++
title = "29. Looking at Generated IR"
weight = 290
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/28-reading-diagnostics/"
next = "/book/30-project-command-line-text/"
+++

# Looking at Generated IR

Most Stark programmers should not need to read generated IR every day, but it
is useful for performance work.

## When To Inspect IR

Inspect generated IR when you need to answer a performance question:

- did this direct call stay direct?
- did this generic function become a concrete instantiation?
- did this fixed array lower without hidden allocation?
- did this range fact survive into the backend?
- did an FFI boundary stay small and explicit?
- did a `[Backend(Opaque)]` module remain a real compiled call boundary?

Do not use IR as the first way to learn ordinary Stark. Source semantics come
first. IR is a debugging and performance tool.

## MIR, SSA, And LLVM

The compiler can expose different intermediate views:

- MIR is useful when checking whether high-level constructs lowered as expected.
- SSA is useful when checking dataflow, constants, and simplified control flow.
- LLVM IR is useful when checking backend-facing calls, attributes, range facts,
  and native interop shape.

The exact internal naming is not a user-facing contract. Prefer looking for
behavioral facts: direct call versus indirect call, allocation versus no
allocation, return value versus out pointer, or raw boundary versus safe API.

## A Small Example

Use a tiny checked program when inspecting IR:

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

Build one small question at a time. For example:

```bash
dotnet run --project src -- site/assets/book/samples/performance-tight-loop.stark --emit-llvm
```

Then look for the shape you care about. For this sample, interesting facts
include:

- `SumFixed` and `HasExpectedTotal` are direct Stark functions
- the loop counter and total are explicit local scalar values
- the fixed array is source-visible storage
- no collection, text, or allocator API is involved
- the entrypoint is `export unsafe ffi fn main`

The point is not to memorize generated names. The point is to connect a
specific source promise to a specific output question.

## Opaque Modules In IR Work

When a module is marked `[Backend(Opaque)]`, optimized callers should still see
its declarations, but they should not see through its implementation for
ThinLTO-style backend import. In generated IR or saved toolchain temporaries,
that usually means calls into the module remain calls across an object or
library boundary instead of becoming inlined caller code.

Use that as a boundary check, not as a general performance goal. Ordinary Stark
modules are normally better when the compiler can optimize through them.

## Keep IR Out Of Normal API Docs

IR is excellent for investigation, but normal user-facing documentation should
not explain features by leaking compiler internals. The book should teach the
source contract first, then use generated IR only in chapters like this one
where the reader has deliberately opted into backend inspection.
