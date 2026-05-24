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

- [Borrowing.stark](samples/Borrowing.stark)
- [OwnershipMoves.stark](samples/OwnershipMoves.stark)
- [BorrowKinds.stark](samples/BorrowKinds.stark)
- [OutParameters.stark](samples/OutParameters.stark)
- [Stark.toml](samples/Stark.toml)

### Borrowing.stark

{{< file-sample "samples/Borrowing.stark" "stark" >}}

### OwnershipMoves.stark

{{< file-sample "samples/OwnershipMoves.stark" "stark" >}}

### BorrowKinds.stark

{{< file-sample "samples/BorrowKinds.stark" "stark" >}}

### OutParameters.stark

{{< file-sample "samples/OutParameters.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Borrowing in Stark](/book/08-borrowing/)
- [Stark Borrowing Compared With Rust](/book/09-borrowing-vs-rust/)
- [Borrower system](/reference/language/BorrowerSystem/)
