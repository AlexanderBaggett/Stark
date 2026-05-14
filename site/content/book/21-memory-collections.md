+++
title = "21. Memory and Collections"
weight = 210
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/20-console-process-platform/"
next = "/book/22-files-directories-paths-text/"

[[stdlib_refs]]
title = "System.Memory"
href = "/reference/standard-library/System.Memory/"

[[stdlib_refs]]
title = "System.Collections"
href = "/reference/standard-library/System.Collections/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Memory and Collections

This chapter builds from the language's `dynamic T` storage primitive to the
owned collection types most programs actually use.

{{< stark-sample "assets/book/stdlib-samples/memory-collections.stark" >}}

## Step 1: Start With The Storage Contract

Before choosing a collection, understand the storage contract it can build on.
The language-level primitive for growable owned storage is `dynamic T`.
Collections use it when they need a typed backing allocation, an initialized
element prefix, and explicit spare capacity without exposing raw pointers in
their public implementation shape.

`dynamic T` is not a replacement for collection APIs. It is the lower-level
storage contract those APIs can build on:

- the owner value tracks `Length` and `Capacity`
- initialized elements are the dense prefix `0..Length`
- spare capacity is written through `init T` or `init T[]`
- destruction drops exactly initialized elements and skips spare capacity

That gives collection internals room to be fast while keeping allocation and
initialization visible to the compiler.

## Step 2: Choose An Allocator Through The Collection API

`System.Memory` defines the allocation vocabulary used by heap-backed standard
library values. Ordinary user code should usually allocate through constructors
instead of raw allocation calls:

```stark
stack mut System.Collections.List<i32[0 max]> values = new();
```

Code that needs explicit allocator control can use the allocator-taking
constructor shape:

```stark
stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
stack mut System.Collections.List<i32[0 max]> values = new(allocator);
```

The allocator identity travels with the owned backing storage.

## Step 3: Treat Growth As A Fallible Operation

Growing a collection can fail, so growth operations return status values:

```stark
values.Push(10)
```

The sample switches on `System.Memory.MemoryStatus`. This follows the same
failure model used elsewhere in Stark: recoverable failure is data, not a
hidden exception.

## Step 4: Pick The Collection By Its Ownership Shape

The first owned collection set includes:

- `System.Collections.List<T>` for growable contiguous storage
- `System.Collections.Stack<T>` for last-in, first-out storage
- `System.Collections.Queue<T>` for first-in, first-out storage
- `System.Collections.LinkedList<T>` for stable node storage
- `System.Collections.Dictionary<K, V>` for compiler-proven key types

These are owned values. Moving the collection moves ownership of its backing
storage.

## Step 5: Move Elements Deliberately

Collections own their elements. Methods that add an element generally take
ownership of that element. Methods that return a borrowed view or borrowed item
must not let that borrow outlive the collection storage.

Use `out`-style methods such as `TryPop` when the caller should provide the
destination for a removed value:

```stark
stack mut i32[0 max] popped = 0;
if (!values.TryPop(popped)) {
    return 4;
}
```

That keeps element movement visible.

## Step 6: Keep Dictionary Keys In The Proven Set

`Dictionary<K, V>` is intentionally conservative. The first key set is limited
to types whose hash and equality behavior the compiler can prove, such as
`bool` and Stark integer types.

That is the tutorial rule: start with keys whose equality and hashing contracts
are already known. When a later chapter introduces richer static contracts, use
those contracts to make additional key types explicit.
