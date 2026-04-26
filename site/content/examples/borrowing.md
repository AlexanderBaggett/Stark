+++
title = "Borrowing"
weight = 70
+++

The borrowing examples cover immutable receiver borrows, mutable receiver
borrows, borrow forwarding, returned mutable borrow views, ownership moves, and
out parameters.

## Build And Run

```bash
dotnet run --project src -- examples/borrowing/Borrowing.stark --emit-exe -o examples/borrowing/borrowing
./examples/borrowing/borrowing
```

Expected behavior: exits with status `0` and no output. The focused borrowing
files are also compiled and run by integration tests.

Status: covered by `ExamplesCompileRunTests.BorrowingExamplesCompileAndRun`.

## Source Files

- [Borrowing.stark](/reference/examples/borrowing/Borrowing.stark)
- [OwnershipMoves.stark](/reference/examples/borrowing/OwnershipMoves.stark)
- [BorrowKinds.stark](/reference/examples/borrowing/BorrowKinds.stark)
- [OutParameters.stark](/reference/examples/borrowing/OutParameters.stark)
- [Stark.toml](/reference/examples/borrowing/Stark.toml)

{{< file-sample "static/reference/examples/borrowing/Borrowing.stark" "stark" >}}

## Related

- [Borrowing in Stark](/book/08-borrowing/)
- [Stark Borrowing Compared With Rust](/book/09-borrowing-vs-rust/)
- [Borrower system](/reference/language/BorrowerSystem/)
