+++
title = "22. Memory and Collections"
weight = 220
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/21-console-process-platform/"
next = "/book/23-files-directories-paths-text/"
aliases = ["/book/21-memory-collections/"]

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

This chapter starts with the language's `dynamic T` storage primitive, then
shows when to use the owned collection types most programs actually want.

{{< stark-sample "assets/book/stdlib-samples/memory-collections.stark" >}}

The snippets below assume the modules they use have been imported:

```stark
import System.Collections
import System.Memory
```

## Step 1: Start With The Storage Shape

Before choosing a collection, understand the storage shape it can build on.
The language-level primitive for growable owned storage is `dynamic T`.
Collections use it when they need typed owned storage, an initialized element
prefix, and explicit spare capacity without exposing raw pointers in the public
API.

`dynamic T` is not a replacement for collection APIs. It is the storage
primitive those APIs can build on:

- the owner value tracks `Length` and `Capacity`
- initialized elements are the dense prefix `0..Length`
- spare capacity is written through `init T` or `init T[]`
- destruction drops exactly initialized elements and skips spare capacity

That keeps allocation and initialization visible in the source API.

Use `dynamic T` directly when you are building a data structure. Use
`System.Collections` when your program just needs a list, stack, queue,
dictionary, or lookup table.

## Step 2: Choose An Allocator Through The Collection API

`System.Memory` defines the allocation vocabulary used by heap-backed standard
library values. Ordinary user code should usually allocate through constructors
instead of raw allocation calls:

```stark
stack mut List<u32[0 max]> values = new();
```

Code that needs explicit allocator control can use the allocator-taking
constructor shape:

```stark
stack Allocator allocator = Allocator.Default();
stack mut List<u32[0 max]> values = new(allocator);
```

The allocator identity travels with the owned backing storage.

`Allocator.Default()` is the allocator ordinary code starts with:

```stark
stack Allocator allocator = Allocator.Default();
if (!allocator.IsDefault())
{
    return false;
}
```

Use `SupportsDynamicAllocator(allocator)` before passing a custom
allocator into code that must grow `dynamic T` storage:

```stark
finite law bool CanGrowDynamic(Allocator allocator)
{
    return SupportsDynamicAllocator(allocator);
}
```

## Step 3: Treat Growth As A Fallible Operation

Growing a collection can fail, so growth operations return status values:

```stark
values.Push(10)
```

The sample switches on `MemoryStatus`. This follows the same
failure model used elsewhere in Stark: recoverable failure is data, not a
hidden exception.

Use a helper when several operations return `MemoryStatus`:

```stark
finite law bool MemoryOk(MemoryStatus status)
{
    switch (status)
    {
        case MemoryStatus.Ok:
            return true;
        case MemoryStatus.Err(var error):
            return false;
    }
}
```

When the exact error matters, switch on `MemoryError`:

```stark
finite law bool IsCapacityProblem(MemoryError error)
{
    switch (error)
    {
        case MemoryError.OutOfMemory:
            return true;
        case MemoryError.InvalidLayout:
            return false;
        case MemoryError.UnsupportedAlignment:
            return false;
        case MemoryError.TooLarge:
            return true;
    }
}
```

Reserve when the caller knows the needed capacity:

```stark
fn bool AddTwo(mut borrow List<u32[0 max]> values)
{
    if (!MemoryOk(values.Reserve(2)))
    {
        return false;
    }

    if (!MemoryOk(values.Push(10)))
    {
        return false;
    }

    return MemoryOk(values.Push(20));
}
```

## Step 4: Pick The Collection By Its Ownership Shape

The first owned collection set includes:

- `List<T>` for growable contiguous storage
- `Stack<T>` for last-in, first-out storage
- `Queue<T>` for first-in, first-out storage
- `RingQueue<T>` for FIFO storage with explicit capacity checks
- `LinkedList<T>` for stable node storage
- `Dictionary<K, V>` for the currently supported key types
- `Lookup<T>` for readonly lookup tables

These are owned values. Moving the collection moves ownership of its backing
storage.

Use `List<T>` when indexing and contiguous storage matter:

```stark
stack mut List<u32[0 max]> values = new();
stack MemoryStatus firstPush = values.Push(10);
stack MemoryStatus secondPush = values.Push(20);
stack u64[0 2 ** 63 - 1] count = values.Count();
```

Inspect list state with the metadata helpers:

```stark
stack bool empty = values.IsEmpty();
stack u64[0 2 ** 63 - 1] capacity = values.Capacity();
```

Borrow the initialized part of a list as a slice when another API only needs a
view:

```stark
fn u64[0 2 ** 63 - 1] CountView(borrow List<u32[0 max]> values)
{
    stack u32[0 max][] view = values.AsSlice();
    return view.Length;
}
```

Use `GetMut` when the caller needs to modify one element:

```stark
fn void SetFirst(mut borrow List<u32[0 max]> values, u32[0 max] value)
{
    values.GetMut(0) = value;
    return;
}
```

Use `AsMutableSlice()` when another helper should update several elements:

```stark
fn void ClearFirstTwo(mut borrow List<u32[0 max]> values)
{
    stack mut u32[0 max][] view = values.AsMutableSlice();
    view[0] = 0;
    view[1] = 0;
    return;
}
```

Use `Stack<T>` when the newest item should come out first:

```stark
stack mut Stack<u32[0 max]> lifo = new();
stack MemoryStatus reserved = lifo.Reserve(2);
stack MemoryStatus firstPush = lifo.Push(1);
stack MemoryStatus secondPush = lifo.Push(2);
stack mut u32[0 max] top = 0;
stack bool hadTop = lifo.TryPop(top);
```

Use `Peek()` when the stack keeps owning the element:

```stark
fn u32[0 max] ReadTop(borrow Stack<u32[0 max]> lifo)
{
    return lifo.Peek();
}
```

Use `Queue<T>` when the oldest item should come out first:

```stark
stack mut Queue<u32[0 max]> queue = new();
stack MemoryStatus reserved = queue.Reserve(2);
stack MemoryStatus firstEnqueue = queue.Enqueue(1);
stack MemoryStatus secondEnqueue = queue.Enqueue(2);
stack mut u32[0 max] next = 0;
stack bool hadNext = queue.TryDequeue(next);
```

Use `Peek()` when the queue keeps owning the front element:

```stark
fn u32[0 max] ReadNext(borrow Queue<u32[0 max]> queue)
{
    return queue.Peek();
}
```

Use `RingQueue<T>` when FIFO behavior matters and the code also wants to watch
capacity directly:

```stark
stack mut RingQueue<u32[0 max]> ring = new();
if (!MemoryOk(ring.Reserve(4)))
{
    return false;
}

stack MemoryStatus enqueued = ring.Enqueue(5);
stack mut u32[0 max] dequeued = 0;
stack bool hadValue = ring.TryDequeue(dequeued);
```

Use `LinkedList<T>` when adding or removing at either end is the main operation:

```stark
stack mut LinkedList<u32[0 max]> list = new();
stack MemoryStatus firstAdd = list.AddFirst(2);
stack MemoryStatus secondAdd = list.AddLast(3);
stack mut u32[0 max] removed = 0;
stack bool hadRemoved = list.TryRemoveFirst(removed);
```

Reserve node storage when the workload knows it will add many nodes:

```stark
fn bool PrepareNodes(mut borrow LinkedList<u32[0 max]> list)
{
    return MemoryOk(list.ReserveNodes(16));
}
```

Remove from the back when the newest tail item should be moved out:

```stark
fn bool TryRemoveTail(
    mut borrow LinkedList<u32[0 max]> list,
    out u32[0 max] value)
{
    return list.TryRemoveLast(value);
}
```

Use `Dictionary<K, V>` when lookup by key is the main operation:

```stark
stack mut Dictionary<u32[0 max], u32[0 max]> scores = new();
stack u32[0 max] key = 7;
stack MemoryStatus stored = scores.Set(key, 100);
stack mut u32[0 max] score = 0;
stack bool found = scores.TryGet(key, score);
```

Use `ContainsKey` when the caller only needs membership:

```stark
finite law bool HasScore(borrow Dictionary<u32[0 max], u32[0 max]> scores)
{
    stack u32[0 max] key = 7;
    return scores.ContainsKey(key);
}
```

Use `Remove` to delete a key and report whether it was present:

```stark
fn bool RemoveScore(mut borrow Dictionary<u32[0 max], u32[0 max]> scores)
{
    stack u32[0 max] key = 7;
    return scores.Remove(key);
}
```

Use `Lookup<T>` for readonly lookup tables:

```stark
const i32[min max][3] Scores =
{
    10, 20, 30
};

fn retborrow frozen i32[min max] ScoreAt(u64[0 2 ** 63 - 1] index)
{
    return Lookup(Scores, index);
}
```

Use metadata helpers before operations with a non-empty precondition:

```stark
fn bool HasItems(borrow Queue<u32[0 max]> queue)
{
    return !queue.IsEmpty() && queue.Count() > 0;
}
```

`Peek()` and `Get(index)` are for cases where the caller already knows the item
exists. Use `TryPop`, `TryDequeue`, `TryRemoveFirst`, or `TryRemoveLast` when
empty storage is a normal possibility.

## Step 5: Move Elements Deliberately

Collections own their elements. Methods that add an element generally take
ownership of that element. Methods that return a borrowed view or borrowed item
must not let that borrow outlive the collection storage.

Use `out`-style methods such as `TryPop` when the caller should provide the
destination for a removed value:

```stark
stack mut u32[0 max] popped = 0;
if (!values.TryPop(popped))
{
    return 4;
}
```

That keeps element movement visible.

Use borrowed accessors when the collection keeps owning the element:

```stark
fn u32[0 max] FirstValue(borrow List<u32[0 max]> values)
{
    return values.Get(0);
}
```

Use `Clear` when the collection should drop its initialized elements but keep
the collection value usable:

```stark
fn void Reset(mut borrow List<u32[0 max]> values)
{
    values.Clear();
    return;
}
```

`Clear` is also available on the other owned collections:

```stark
fn void ResetAll(
    mut borrow Stack<u32[0 max]> stack,
    mut borrow Queue<u32[0 max]> queue,
    mut borrow Dictionary<u32[0 max], u32[0 max]> dictionary)
{
    stack.Clear();
    queue.Clear();
    dictionary.Clear();
    return;
}
```

## Step 6: Use Memory Helpers When You Are Moving Raw Elements

Most user code should prefer collections and text builders. Use
`System.Memory` helpers when you are writing a collection, buffer, parser, or
other low-level owner that needs to copy, move, fill, or append initialized
bytes or code points.

The byte helpers operate on `i8[min max]` storage:

```stark
stack i8[min max][4] source =
{
    1, 2, 3, 4
};
stack mut i8[min max][4] destination =
{
    0, 0, 0, 0
};
CopyBytesDisjoint(source, destination, 4);
```

Use the disjoint variants when the source and destination are separate storage:

```stark
CopyBytesDisjoint(source, destination, 4);
CopyBytesDisjointInfallible(source, destination, 4);
```

Use the overlap-safe variants when the source and destination may refer to the
same backing storage:

```stark
CopyBytes(source, destination, 4);
MoveBytes(source, destination, 4);
MoveBytesInfallible(source, destination, 4);
```

The same shape exists for Unicode code points stored as `i32[min max]`:

```stark
stack i32[min max][3] codePoints =
{
    65, 66, 67
};
stack mut i32[min max][3] copied =
{
    0, 0, 0
};
CopyCodePointsDisjoint(codePoints, copied, 3);
```

Use fill helpers when the destination should receive one repeated value:

```stark
stack mut i8[min max][8] bytes =
{
    1, 1, 1, 1, 1, 1, 1, 1
};
FillInitializedBytes(bytes, 0, 8);

stack mut i32[min max][4] codePoints =
{
    65, 66, 67, 68
};
FillInitializedCodePoints(codePoints, 0, 4);
```

The `init` helpers are for constructing uninitialized destination slots:

```stark
stack mut dynamic i8[min max] bytes = new();
if (bytes.TryReserve(4))
{
    stack init i8[min max][] tail = init bytes[bytes.Length, 4];
    FillBytes(tail, 0, 4);
}
```

Use `ReserveBytes` and `ReserveCodePoints` when the owner is a raw `dynamic`
buffer rather than a collection:

```stark
stack mut dynamic i8[min max] bytes = new();
if (!MemoryOk(ReserveBytes(bytes, 64)))
{
    return false;
}

stack mut dynamic i32[min max] codePoints = new();
if (!MemoryOk(ReserveCodePoints(codePoints, 16)))
{
    return false;
}
```

Use append helpers when the destination is a `dynamic` owner and new initialized
elements should be added to the end:

```stark
stack i8[min max][3] header =
{
    1, 2, 3
};
stack mut dynamic i8[min max] bytes = new();

if (!MemoryOk(AppendBytesDisjoint(bytes, header, 3)))
{
    return false;
}

if (!MemoryOk(AppendFillBytes(bytes, 0, 4)))
{
    return false;
}
```

The code-point helpers have the same shape:

```stark
stack i32[min max][2] letters =
{
    65, 66
};
stack mut dynamic i32[min max] codePoints = new();

if (!MemoryOk(AppendCodePointsDisjoint(codePoints, letters, 2)))
{
    return false;
}

if (!MemoryOk(AppendFillCodePoints(codePoints, 32, 1)))
{
    return false;
}
```

Use the non-`Disjoint` append forms when the source might be a view into the
same owner. They preserve the source values before growth or copying can change
the destination:

```stark
stack i8[min max][] prefix = bytes[0, 2];
AppendBytes(bytes, prefix, 2);
```

Use `InitializeBytes` or `InitializeCodePoints` when you already have an
`init` destination slice:

```stark
stack mut dynamic i8[min max] copied = new();
if (!copied.TryReserve(3))
{
    return false;
}

stack init i8[min max][] tail = init copied[copied.Length, 3];
InitializeBytesDisjoint(header, tail, 3);
```

After initialization, read the initialized prefix through a slice view:

```stark
stack i8[min max][] initialized = copied[0, copied.Length];
return initialized[0] == 1;
```

Choose the narrowest correct helper:

- `Initialize*` when destination slots are not initialized yet
- `Copy*Disjoint` when source and destination are separate
- `Copy*` or `Move*` when overlap is allowed
- `Fill*` for uninitialized destination slots
- `FillInitialized*` for already initialized destination slots
- `*Infallible` only when the caller has already guaranteed the operation's
  preconditions

If source and destination might overlap, do not use a disjoint-only helper just
because it is shorter to type.

## Step 7: Keep Dictionary Keys In The Supported Set

`Dictionary<K, V>` is intentionally conservative. The first key set is limited
to types with built-in hash and equality behavior, such as `bool` and Stark
integer types.

That is the tutorial rule: start with keys whose equality and hashing contracts
are already supported. When richer key support is added, use the documented
trait or doctrine shape instead of assuming any type can be a key.

The source names the key contract as `DictionaryKey<T>`. Built-in keys already
provide that behavior:

```stark
stack mut Dictionary<u32[0 max], u32[0 max]> byId = new();
stack mut Dictionary<bool, u32[0 max]> byFlag = new();
```

## Step 8: Remove Dictionary Values By The Ownership Result You Want

`Dictionary<K, V>` has three removal shapes.

Use `Remove(key)` when the caller only needs to know whether the key was
present:

```stark
fn bool DropScore(mut borrow Dictionary<u32[0 max], u32[0 max]> scores)
{
    stack u32[0 max] key = 7;
    return scores.Remove(key);
}
```

Use `TryRemove(key, out value)` when the removed value should be written into a
caller-owned destination:

```stark
fn bool TakeScore(
    mut borrow Dictionary<u32[0 max], u32[0 max]> scores,
    out u32[0 max] value)
{
    value = 0;
    stack u32[0 max] key = 7;
    return scores.TryRemove(key, value);
}
```

Use `RemoveMove(key)` when the result should carry either `Missing` or the
removed value:

```stark
fn u32[0 max] TakeScoreOrZero(
    mut borrow Dictionary<u32[0 max], u32[0 max]> scores)
{
    stack u32[0 max] key = 7;
    stack DictionaryRemoveResult<u32[0 max]> removed =
        scores.RemoveMove(key);

    switch (removed)
    {
        case DictionaryRemoveResult<u32[0 max]>.Missing:
            return 0;
        case DictionaryRemoveResult<u32[0 max]>.Removed(var value):
            return value;
    }
}
```

Choose by ownership:

- `Remove` discards the value
- `TryRemove` moves the value into an `out` destination
- `RemoveMove` returns an enum that either contains the moved value or says the
  key was missing
