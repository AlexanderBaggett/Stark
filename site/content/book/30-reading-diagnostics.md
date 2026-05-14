+++
title = "30. Reading Stark Diagnostics"
weight = 300
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/29-unsafe-stark-raw-pointers/"
next = "/book/31-generated-ir/"
aliases = ["/book/28-reading-diagnostics/"]
+++

# Reading Stark Diagnostics

Stark rejects programs when it cannot prove the required guarantees.

## Step 1: Start With The Category

Diagnostics include a code and a category. The category tells you which part of
the language contract failed:

- parser diagnostics: the source shape is not valid Stark syntax
- type diagnostics: a value does not match the expected type
- ownership diagnostics: a moved value is used again
- borrow diagnostics: access or escape rules are not satisfied
- range diagnostics: an integer range or endpoint is invalid
- package diagnostics: imports or package metadata cannot be resolved
- native diagnostics: a native source, library, or discovery hook is missing

## Step 2: Fix Move Errors By Restoring Ownership Clarity

This rejected example is checked by the book sample test:

{{< stark-sample "assets/book/negative-samples/use-after-move.stark" >}}

The important line is the read after `Consume(box)`. Stark tells you that the
value moved and must be reinitialized before it can be read.

The fix is to reinitialize the binding or change the callee to borrow instead
of taking ownership.

## Step 3: Fix Borrow Escapes By Naming The Escape

This rejected example tries to return a plain `borrow`:

{{< stark-sample "assets/book/negative-samples/plain-borrow-return-escape.stark" >}}

The diagnostic tells you to use `retborrow` or `storeborrow`. That wording is
important. Stark is not asking for a hidden lifetime annotation; it is asking
whether the API really returns a borrow or stores one somewhere longer-lived.

## Step 4: Fix Storage Errors By Creating Backing Storage

This rejected example asks a slice view to appear from an array initializer:

{{< stark-sample "assets/book/negative-samples/slice-literal-hidden-storage.stark" >}}

The diagnostic points at the initializer and explains that array initializers
need a fixed-size array target. The fix is to create backing storage first:

```stark
stack i32[min max][3] values = { 1, 2, 3 };
stack i32[min max][] view = values;
```

## Step 5: Fix Switch Coverage By Removing Dead Arms

This rejected example handles every enum variant, then adds a `default` arm:

{{< stark-sample "assets/book/negative-samples/unreachable-switch-default.stark" >}}

The diagnostic points out that the later arm is unreachable. The fix is to
remove the dead arm or make an earlier pattern narrower.

## Step 6: Simplify The Proof The Compiler Needs

When Stark cannot prove something, make the proof easier:

- split complex expressions into named locals
- write the storage class and range you intend
- choose `borrow` when a function does not need ownership
- use `retborrow` only when a borrow deliberately escapes by return
- handle status/result values with `switch`
- keep raw pointer work inside small wrapper functions
- avoid exposing implementation details across package boundaries

Good Stark diagnostics should teach the missing fact. Good Stark source should
make that fact easy to see.
