+++
title = "7. Ownership, Moves, and Cleanup"
weight = 70
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/06-bindings-control-flow/"
next = "/book/08-borrowing/"
+++

# Ownership, Moves, and Cleanup

Stark safe code is ownership-based and does not use garbage collection.
Owned values have one owner, moves transfer ownership, and values still owned at
scope exit are cleaned up deterministically.

You will still see the word `drop` in Stark, but it has a specific meaning:
`drop { ... }` is the destructor block a `struct` or `record` may declare for
custom cleanup. This chapter uses "cleanup" for the general ownership behavior
so the keyword stays distinct.

{{< stark-sample "samples/ownership-moves.stark" >}}

## Step 1: Assume Values Are Owned By Default

Every non-borrow, non-raw value has one owner. Structs, records, arrays, and
owned standard-library values should be read with that rule in mind.

The `Box` value in the sample is an owned struct value. Passing it to `Consume`
moves it into the callee:

```stark
Consume(box);
```

After that call, the old `box` binding does not own the value anymore. The
callee owns its parameter and is responsible for cleaning it up at function exit
unless it moves the value somewhere else first.

## Step 2: Reinitialize After A Move

A moved mutable binding can become usable again if it is initialized again:

```stark
box = new Box()
{
    Value = 2
};
```

The important distinction is that Stark does not pretend the moved value is
still available. The binding becomes available again only because the code
stores a new owned value into it.

Reassignment to a still-initialized owned place is different: the previous value
is cleaned up before the replacement becomes the new owner. A moved-from binding
has no old owned value to clean up.

## Step 3: Let Use-After-Move Fail Early

This intentionally invalid example is checked as a negative book sample:

{{< stark-sample "rejected/use-after-move.stark" >}}

The final `return box.Value;` is rejected because `box` was moved into
`Consume`. Safe Stark code cannot read through an owned binding after ownership
has left that binding.

## Step 4: Let Scalars Copy When That Is The Type Contract

Not every assignment is a move. Trivially copyable scalar values, such as
integers and booleans, can be copied:

```stark
stack i32[min max] left = 10;
stack i32[min max] right = left;
```

Both `left` and `right` remain usable. Structs and records should be assumed
move-only unless the language and type rules say otherwise.
The rule that decides this is *copyability*, and it is structural. A type is
copyable when copying its bytes is safe and leaves nothing to clean up:

- scalars, raw pointers, and text views are copyable
- an `enum` is copyable when every variant is a unit variant or carries only copyable fields
- a `struct` or `record` is copyable when every field is copyable
- a type with a destructor is never copyable, and neither is anything that owns storage: `dynamic`, the owning text containers, or heap closures

Reading a copyable value out of a field, an index, or a local is a copy, so the
source stays usable. Reading a move-only value out is a move, so it does not.

Sometimes a type is structurally copyable but should still behave like a unique
handle. Give it an empty destructor to keep move semantics:

```stark
struct FileHandle
{
    i32[min max] Descriptor;

    drop
    {
    }
}
```

A type that is meant to stay copyable can assert it at the declaration with
`[Copyable]`. Adding an owning field or a destructor later then fails at the
type instead of at every place that copied it:

```stark
[Copyable]
enum Direction
{
    North,
    South,
}
```

Generic code that needs to copy a value states the requirement the same way
`Transferable` and `Shareable` bounds are written:

```stark
fn bool Duplicate<T>(T value)
    where Copyable(T)
{
    stack T first = value;
    stack T second = value;
    return true;
}
```

Call sites with concrete type arguments are checked structurally; a generic
caller forwards the requirement by declaring the same `where Copyable(T)`.
`Copyable` is purely structural — it cannot be granted or denied with an
attribute.

## Step 5: Rely On Deterministic Cleanup For Owners

When a scope exits, values still owned by local bindings are cleaned up. Stark
does not need a tracing collector to discover that work. Ownership already says
which binding is responsible.

Safe Stark also avoids a general `forget`-style escape hatch. If ownership
could be silently abandoned, the language would lose one of the facts that make
cleanup and optimization predictable.

## Step 6: Use `drop` Only For Custom Type Cleanup

Automatic cleanup is the ordinary rule. A type only writes a destructor block
when it needs custom work. In this sketch, `PlatformClose` stands for a
non-fallible helper supplied by the type implementation:

```stark
struct FileHandle
{
    i64[min max] Handle;
    bool Closed;

    drop
    {
        if (!self.Closed)
        {
            PlatformClose(self.Handle);
        }
    }
}
```

Only `struct` and `record` bodies may declare destructor blocks. A type may
declare at most one block, and the block is not an ordinary method: it has no
name, return type, parameters, or visibility.

There are two forms:

- `drop { ... }` gives readonly `self`
- `mut drop { ... }` gives mutable `self` for cases such as disarming a handle
  before field cleanup continues

Safe destructors are deliberately narrow: they should not panic, synchronize, or
allocate. Fallible or user-controlled teardown belongs in an explicit API such
as `Close`, with automatic cleanup as the final deterministic backstop.
