+++
title = "9. Stark Borrowing Compared With Rust"
weight = 90
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/08-borrowing/"
next = "/book/10-storage-classes/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[example_refs]]
title = "Ownership Moves"
href = "/reference/examples/borrowing/OwnershipMoves.stark"

[[example_refs]]
title = "Borrowing Examples"
href = "/reference/examples/borrowing/Borrowing.stark"
+++

# Stark Borrowing Compared With Rust

Stark and Rust share a belief: memory safety and performance improve when
ownership and borrowing are checked statically.

They do not expose the same model to the programmer.

## What Is Similar

Both languages reject use-after-move in safe code. Both distinguish shared
access from mutable access. Both prefer deterministic cleanup over garbage
collection.

## Where Stark Is Stricter

Stark makes non-escaping borrows the default. A returned borrow must be written
as `retborrow`.

Stark safe borrows are never `null`. Raw pointers can be null, but raw pointers
are an explicit low-level boundary.

Stark does not currently provide standard safe equivalents of `Rc`, `RefCell`,
dynamic trait objects, or general interior mutability. Those patterns make
aliasing and mutation harder to prove, so Stark keeps them out of the ordinary
safe subset.

## Different Tools For Different Intent

Stark has source forms that do not map one-to-one with Rust syntax:

- `retborrow` marks a returned borrow
- `frozen` marks deeply read-only access
- `out` marks a write destination
- `stack`, `heap`, and `arena` make storage explicit

The point is not to be more ceremonial. The point is to keep escape,
mutability, initialization, and storage visible enough that the compiler can
make strong guarantees.

## Deep Read-Only Access

`frozen` is stronger than "I do not happen to mutate through this name." It says
the reachable data is deeply read-only for the duration of that access.

{{< stark-sample "assets/book/samples/frozen-read.stark" >}}

The sample reads a value through a frozen view of data reachable from a `const`
object graph. Code that receives `frozen Box` can read `box.Value`, but it
cannot turn that access into mutation.

This rejected sample is checked too:

{{< stark-sample "assets/book/negative-samples/frozen-write.stark" >}}

## A Fallible Write Without Exceptions

{{< stark-sample "assets/book/samples/out-parameter.stark" >}}

The `out` destination makes the write-before-read contract explicit. This keeps
fallible code in the value/result world rather than using hidden exceptions or
unwinding.
