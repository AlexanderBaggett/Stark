+++
title = "17. Errors Without Exceptions"
weight = 170
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/16-function-guarantees/"
next = "/book/18-generics-traits-doctrines/"
aliases = ["/book/16-errors-without-exceptions/"]
+++

# Errors Without Exceptions

Stark does not hide recoverable failure behind unwinding. Recoverable failure
should be ordinary data in the signature.

{{< stark-sample "assets/book/samples/out-parameter.stark" >}}

## Step 1: Try Status Plus Caller-Owned Output First

`TryWriteAnswer` returns `bool` and writes into an `out` destination:

```stark
fn bool TryWriteAnswer(out i32[min max] value)
```

The caller can branch on the status and read the destination only on the path
where the write succeeded.

This style is useful when the successful result should be written into
caller-owned storage. It keeps allocation out of the error path unless the API
explicitly asks for allocation.

## Step 2: Upgrade To A Result Enum When The Caller Needs A Value Or Error

For richer APIs, use a status enum or a result enum. The exact type should say
what callers need to know:

- status-only APIs return a small enum or `bool`
- APIs with a successful value can return a result-shaped enum
- APIs that fill existing storage can return status and use `out`

The common thread is that failure is visible. A caller can see from the
signature that the operation may fail.

{{< stark-sample "assets/book/samples/result-status-enum.stark" >}}

`DivideResult` keeps success and failure in the type itself. The caller cannot
accidentally ignore the divide-by-zero case while extracting the quotient,
because the quotient only exists inside the `Ok` variant.

This is the ordinary Stark shape for recoverable failure:

- the function returns directly when it succeeds without special conditions
- the enum says which failure cases callers can handle
- `switch` makes success and failure paths visible in the caller
- no stack unwinding is needed to move control to a distant handler

Use result-shaped enums when the caller needs a value on success. Use smaller
status enums or `bool` when the caller only needs to know whether an operation
worked.

## Step 3: Reserve Traps For Unrecoverable States

Some failures are not recoverable program states. Those belong in trap-style
paths rather than exception unwinding.

Stark has no general stack unwinding model. That is a language and runtime
constraint, not just a missing library convenience. It keeps cleanup,
optimization, and FFI boundaries easier to reason about.

## Step 4: Choose The Error Shape From The Caller Workflow

When designing a Stark API, ask which of these fits:

- return the value directly when failure is not part of the contract
- return `bool` or a status enum when the caller only needs success/failure
- return a result enum when the caller needs either a value or an error reason
- use `out` when the caller should choose the destination storage

Do not make recoverable failure invisible.
