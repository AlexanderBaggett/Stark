+++
title = "8. Borrowing in Stark"
weight = 80
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/07-ownership-moves-drops/"
next = "/book/09-borrowing-vs-rust/"

[[language_refs]]
title = "Borrower System"
href = "/reference/docs/Userfacing/BorrowerSystem.md"

[[language_refs]]
title = "Language Reference"
href = "/reference/docs/Userfacing/LanguageReference.md"

[[example_refs]]
title = "Borrowing Examples"
href = "/reference/examples/borrowing/Borrowing.stark"

[[example_refs]]
title = "Borrow Kinds"
href = "/reference/examples/borrowing/BorrowKinds.stark"

[[example_refs]]
title = "Out Parameters"
href = "/reference/examples/borrowing/OutParameters.stark"
+++

# Borrowing in Stark

Borrowing lets code access a value without taking ownership of it.

{{< stark-sample "assets/book/samples/borrow-counter.stark" >}}

## The Three Borrow Forms In This Example

`borrow Counter` is shared temporary access. It can read the counter but cannot
mutate it.

`mut borrow Counter` is temporary mutable access. It can update the counter, but
it still does not own the counter.

`retborrow mut i32[min max]` says the function deliberately returns a borrow to
part of its input. A plain `borrow` return would be rejected because plain
borrows are non-escaping by default.

This intentionally invalid sample is also checked by the book:

{{< stark-sample "assets/book/negative-samples/plain-borrow-return-escape.stark" >}}

The fix is not to invent a lifetime parameter. The fix is to choose the source
form that matches the API:

- return an owned value when the caller should receive a copy or moved value
- return `retborrow` when the caller should receive a borrow tied to an input
- keep `borrow` for temporary access that does not escape the call

## Why This Is Stark-Specific

Stark does not make every borrow potentially escaping and then ask the
programmer to annotate lifetimes. The default safe borrow is local and
non-null. If a borrow needs to escape through a return value, that intent is
written directly with `retborrow`.

## Null Is A Raw-Pointer Concern

Safe borrows are never nullable. This intentionally invalid sample is part of
the book's negative sample check:

{{< stark-sample "assets/book/negative-samples/null-borrow.stark" >}}

Use raw pointers at FFI boundaries when a foreign API can return or accept
`null`. Convert into safe Stark borrows only after the boundary code has proved
the facts that safe code relies on.
