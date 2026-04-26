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

This chapter teaches owned heap-backed library values.

{{< stark-sample "assets/book/stdlib-samples/memory-collections.stark" >}}

## `System.Memory`

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

## Fallible Growth

Growing a collection can fail, so growth operations return status values:

```stark
values.Push(10)
```

The sample switches on `System.Memory.MemoryStatus`. This follows the same
failure model used elsewhere in Stark: recoverable failure is data, not a
hidden exception.

## Collection Families

The first owned collection set includes:

- `System.Collections.List<T>` for growable contiguous storage
- `System.Collections.Stack<T>` for last-in, first-out storage
- `System.Collections.Queue<T>` for first-in, first-out storage
- `System.Collections.LinkedList<T>` for stable node storage
- `System.Collections.Dictionary<K, V>` for compiler-proven key types

These are owned values. Moving the collection moves ownership of its backing
storage.

## Elements And Borrows

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

## Dictionary Keys

`Dictionary<K, V>` is intentionally conservative. The first key set is limited
to types whose hash and equality behavior the compiler can prove, such as
`bool` and Stark integer types.

General user-defined key contracts need the constrained-generic story to land
before they become ordinary book examples.
