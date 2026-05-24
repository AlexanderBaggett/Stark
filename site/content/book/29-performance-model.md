+++
title = "29. Stark's Performance Model"
weight = 290
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/28-testing-stark-code/"
next = "/book/30-memory-layout-abi/"
aliases = ["/book/25-performance-model/", "/book/26-performance-model/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"
+++

# Stark's Performance Model

Stark treats restrictions as part of how you write fast code. The goal is not
to make source code ceremonial. The goal is to make ownership, storage,
failure, and boundaries visible in the program instead of hiding them behind
runtime behavior.

{{< stark-sample "assets/book/samples/small-tour.stark" >}}

## Step 1: Start With Plain Safe Code

Safe Stark code already asks you to write the important choices down:

- owned values have clear owners
- borrows are non-null
- ordinary borrows do not escape by default
- storage is visible
- failure is returned as data
- callable values are explicit when you need indirection

Those are not separate "performance mode" features. They are ordinary Stark.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

This sample is small, but it shows the kind of source Stark wants performance
work to start from:

- the array has fixed storage and a visible length
- the loop state is explicit stack storage
- the helper functions are `finite law`
- the entrypoint reports success with an ordinary exit code
- no collection, text, or allocator API is involved

Before reaching for tooling, first ask whether the source already says what the
program depends on.

## Step 2: State Memory Separation

`disjoint` is how Stark lets source code state that memory regions do not
overlap.

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

The sample has two paths. `AddSeparate` requires non-overlapping pointer
arguments. `TryAddPairFast` first runs `if disjoint(left, right)`. The true
branch calls the non-overlap helper. The false branch keeps an overlap-safe
path.

This distinction is intentional. Different pointer names are not enough to say
the pointed-to regions differ. Use distinct fields, distinct constant indexes,
separately addressed storage, a declared `where disjoint(...)` relation, or a
runtime `if disjoint(...)` check.

The three relation spellings mean different things:

```stark
where disjoint(left[0, count], right[0, count])
where overlap(left, right)
where same(left, right)
```

Use `disjoint` when two regions must not overlap. Use `overlap` when an API is
written to handle overlap safely. Use `same` when two parameters are required
to name the same region.

Loop `independent` uses the same source-level style. Use it only when each
iteration can run without depending on reads or writes from another iteration.

## Step 3: Bound Raw Pointer Regions Before Tuning Them

Raw pointer performance code can give Stark a precise region without turning
the pointer into a safe borrow:

```stark
rawmutptr<i32[min max]>[count]
```

The bound says the raw pointer is valid for `count` contiguous elements. A
positive count requires a non-null pointer; a zero-length region may use
`null`. Region expressions such as `source[0, count]` and
`destination[start, count]` can appear in `where disjoint(...)` and
`if disjoint(...)` so the non-overlap path stays explicit.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

The checked sample contains the common pattern:

- `CopyFast` uses bounded raw pointer parameters and an `independent` loop.
- `TryCopy` uses `if disjoint(...)` to choose an `independent` copy loop.
- `Fill` writes one element per iteration through a bounded `rawmutptr`.
- `Transform` reads from one bounded region and writes another.
- the false branch falls back to an overlap-safe temporary copy when ranges
  overlap.

When an unsafe boundary needs an ordinary slice view, `slice(pointer, count)`
creates one from the bounded raw pointer region inside an `unsafe` block. After
that conversion, the wrapper can use normal slice indexing rules.

## Step 4: Use `const` For Deep Readonly Parameters

`const` on a parameter means the reachable object graph is deeply immutable.
It is stronger than an ordinary immutable binding and stronger than a temporary
`frozen` borrow. The caller must pass a value that is already acceptable as
deeply readonly.

{{< stark-sample "assets/book/samples/const-parameters.stark" >}}

Inside `ReadMiddle`, the table graph is readonly. `ForwardConst` can forward
the same readonly parameter to another `const` parameter. A projection such as
`table.Values[1]` does not regain mutation authority. If a const parameter
contains a raw mutable pointer field, reading that field through the const graph
yields readonly access, not mutable raw authority.

Const is not an aliasing promise. Two const parameters may point at the same
immutable graph, so `const` and `disjoint` remain separate contracts.

## Step 5: Mark Independent Loops Only When The Source Shows Independence

`independent` is the loop-level form of the same explicit style:

```stark
stack mut i32[0 10] value = 0;
while willexit independent (value < 4)
{
    value += 1;
}
```

The keyword has real meaning. A write in one iteration must not be read or
written by another iteration. Show that through index ranges, exclusive
borrows, disjoint slice regions, and calls whose signatures keep memory effects
clear.

The easiest accepted shape is a simple counted loop over a slice, fixed array,
or bounded raw pointer region where the induction variable is the element
index. Raw pointer loops use `*(&root[index])` when `root` has a bounded raw
pointer region. Avoid unbounded raw pointer dereferences, hidden roots,
non-induction indexes, nested loops, early exits, and unclear call effects in an
`independent` loop.

## Step 6: Keep Allocation Visible

Allocation should be visible in the API. A growable collection may allocate
when it grows, so its mutating methods return `MemoryStatus`.
Owned text conversion returns allocation-aware result values. Caller-owned text
buffers write their capacity in the source.

Small value helpers should not hide allocation behind pleasant syntax.

Use the collection status directly. These snippets assume the modules they use
have been imported:

```stark
import System.Collections
import System.Console
import System.Memory
import System.Text
```

```stark
fn bool PushScore(mut borrow List<i32[min max]> scores, i32[min max] score)
{
    stack MemoryStatus pushed = scores.Push(score);
    switch (pushed)
    {
        case MemoryStatus.Ok:
            return true;
        case MemoryStatus.Err(var error):
            return false;
    }
}
```

Use owned text results when formatting may allocate:

```stark
fn bool PrintScore(i64[min max] score)
{
    stack MemoryResult<OwnedAscii> formatted = ToAscii(score);

    switch (formatted)
    {
        case MemoryResult<OwnedAscii>.Err(var error):
            return false;
        case MemoryResult<OwnedAscii>.Ok(var value):
            stack mut OwnedAscii text = value;
            WriteLine(text.View());
            return true;
    }
}
```

Use fixed-capacity text when the maximum size is part of the function:

```stark
fn Ascii SmallLabel(i32[min max] value)
{
    stack Ascii label[64] = $"Value: {value}";
    return label;
}
```

## Step 7: Keep Failure Out Of Hidden Unwinding

Stark has no general exception unwinding model. Recoverable failure is ordinary
data. Unrecoverable failure is a trap-or-abort style path.

That keeps cleanup and FFI boundaries straightforward. A C call should not
suddenly need to understand Stark stack unwinding, and a Stark destructor should
not become part of an exception-control-flow story.

Write recoverable failures as values:

```stark
enum ParseCountResult
{
    Ok(i32[min max]),
    Invalid,
}

fn ParseCountResult ParseCount(ascii text)
{
    stack TextResult<i32[min max]> parsed = ParseI32Ascii(text);

    switch (parsed)
    {
        case TextResult<i32[min max]>.Err(var error):
            return ParseCountResult.Invalid;
        case TextResult<i32[min max]>.Ok(var value):
            return ParseCountResult.Ok(value);
    }
}
```

## Step 8: Use Callable Values Only When You Need Indirection

Ordinary function calls are direct. Generic functions are used with concrete
types. Traits and doctrines name source requirements; they are not runtime
object pointers.

When you want indirection, write it:

```stark
fnptr<fn i32[min max](i32[min max])>
```

The source should say when a call becomes a callable value. It should also say
which guarantees the target must satisfy. A `fnptr<finite law ...>` callback
requires a target that returns, is pure, and reads only through its visible
inputs.

Keep ordinary calls ordinary:

```stark
finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

finite law i32[min max] UseDirectCall(i32[min max] value)
{
    return Double(value);
}
```

Introduce `fnptr` only when a value needs to store or pass the callable:

```stark
fn i32[min max] Apply(
    fnptr<fn i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}
```

## Step 9: Make Storage Choices Source-Visible

`stack`, `heap`, `arena`, fixed arrays, slices, `Ascii`, and `Unicode` all
carry storage meaning. Stark avoids making an owned backing allocation appear
from a slice literal or a text expression without the source naming the storage
choice.

This is why fixed-array initializers and slice views are separate concepts.

Name the storage you want:

```stark
stack i32[min max][4] fixedValues =
{
    1, 2, 3, 4
};
stack i32[min max][] fixedView = fixedValues;

heap i32[min max] heapValue = 42;
stack Ascii label[32] = "ready";
```

## Step 10: Isolate Native Boundaries

FFI, raw pointers, and native package metadata are deliberately explicit. Stark
lets you cross into C or platform APIs, but it does not pretend those calls have
the same guarantees as ordinary safe code.

Keep the raw declaration at the edge:

```stark
unsafe ffi fn i32[min max] native_value();

public unsafe fn i32[min max] ReadNativeValue()
{
    return native_value();
}
```

## Step 11: Keep Low-Level Boundaries Small

Runtime, platform, and interop code sometimes needs a hard boundary. Put that
boundary around the smallest surface that needs it. Most application code should
stay in ordinary Stark APIs: public package functions for Stark callers,
`export` only for native entrypoints, and `unsafe ffi` only where foreign code
is actually involved.
