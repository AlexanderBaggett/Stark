+++
title = "7. Ownership, Moves, and Drops"
weight = 70
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/06-bindings-control-flow/"
next = "/book/08-borrowing/"
+++

# Ownership, Moves, and Drops

Stark safe code is ownership-based and does not use garbage collection.
Owned values have one owner, moves transfer ownership, and values still owned at
scope exit are cleaned up deterministically.

{{< stark-sample "assets/book/samples/ownership-moves.stark" >}}

## Owned By Default

The `Box` value in the sample is an owned aggregate. Passing it to `Consume`
moves it into the callee:

```stark
Consume(box);
```

After that call, the old `box` binding does not own the value anymore.

## Reinitialization

A moved mutable binding can become usable again if it is initialized again:

```stark
box = new Box() {
    Value = 2
};
```

The important distinction is that Stark does not pretend the moved value is
still available. The binding becomes available again only because the code
stores a new owned value into it.

## Use After Move Is Rejected

This intentionally invalid example is checked as a negative book sample:

{{< stark-sample "assets/book/negative-samples/use-after-move.stark" >}}

The final `return box.Value;` is rejected because `box` was moved into
`Consume`. Safe Stark code cannot read through an owned binding after ownership
has left that binding.

## Scalars Copy

Not every assignment is a move. Trivially copyable scalar values, such as
integers and booleans, can be copied:

```stark
stack i32[min max] left = 10;
stack i32[min max] right = left;
```

Both `left` and `right` remain usable. Aggregates should be assumed move-only
unless the language and type rules say otherwise.

## Drop Is Deterministic

When a scope exits, values still owned by local bindings are dropped. Stark
does not need a tracing collector to discover that cleanup work. Ownership
already says which binding is responsible.

Safe Stark also avoids a general `forget`-style escape hatch. If ownership
could be silently abandoned, the language would lose one of the facts that make
cleanup and optimization predictable.
