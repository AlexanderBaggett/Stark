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

The full shape is:

```stark
fn bool TryDivide(i32[min max] numerator, i32[min max] denominator, out i32[min max] result) {
    if (denominator == 0) {
        result = 0;
        return false;
    }

    result = numerator / denominator;
    return true;
}

fn i32[min max] UseTryDivide() {
    stack mut i32[min max] quotient = 0;
    if (!TryDivide(21, 3, quotient)) {
        return -1;
    }

    return quotient;
}
```

Use this shape when the caller already owns the destination storage, when the
result is large, or when failure should leave the caller in charge of what to
write next.

Use an `out` destination for owned results too:

Assume the standard-library modules are imported before these snippets:

```stark
import System.IO
import System.Memory
import System.Text
```

```stark
fn bool TryMakeReadyLabel(out OwnedAscii label) {
    stack MemoryStatus copied = FromConstAscii(label, "ready");

    switch (copied) {
        case MemoryStatus.Ok:
            return true;
        case MemoryStatus.Err(var error):
            label = new();
            return false;
    }
}
```

If failure means the destination is not meaningful, make every successful path
write the destination before returning `true`.

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

A status enum is useful when there is no success value:

```stark
enum SaveStatus {
    Ok,
    PermissionDenied,
    DiskFull,
}

fn SaveStatus SaveMarker(bool canWrite, bool hasSpace) {
    if (!canWrite) {
        return SaveStatus.PermissionDenied;
    }

    if (!hasSpace) {
        return SaveStatus.DiskFull;
    }

    return SaveStatus.Ok;
}
```

The caller handles it with `switch`:

```stark
switch (SaveMarker(true, true)) {
    case SaveStatus.Ok:
        return 0;
    case SaveStatus.PermissionDenied:
        return 1;
    case SaveStatus.DiskFull:
        return 2;
}
```

Use standard-library status enums the same way:

```stark
finite law bool IOOk(IOStatus status) {
    switch (status) {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}
```

For allocation-backed APIs, switch on `MemoryResult<T>`:

```stark
fn bool FormatNumber(i32[min max] value, out OwnedAscii text) {
    stack MemoryResult<OwnedAscii> formatted = ToAscii(value);

    switch (formatted) {
        case MemoryResult<OwnedAscii>.Err(var error):
            text = new();
            return false;
        case MemoryResult<OwnedAscii>.Ok(var result):
            text = result;
            return true;
    }
}
```

## Step 3: Reserve Traps For Unrecoverable States

Some failures are not recoverable program states. Those belong in trap-style
paths rather than exception unwinding.

Stark has no general exception unwinding. Normal cleanup stays tied to ordinary
control flow, and recoverable failure stays in the function's return value.

Good unrecoverable examples are source mistakes or violated invariants:

```stark
fn i32[min max] RequireFirst(i32[min max][] values) {
    return values[0];
}
```

That function is only correct when callers pass a non-empty slice. If empty
input is recoverable for the API, do not rely on a trap. Return a status or
result instead:

```stark
enum FirstResult {
    Ok(i32[min max]),
    Empty,
}

fn FirstResult TryFirst(i32[min max][] values, u64[0 max] count) {
    if (count == 0) {
        return FirstResult.Empty;
    }

    return FirstResult.Ok(values[0]);
}
```

## Step 4: Choose The Error Shape From The Caller Workflow

When designing a Stark API, ask which of these fits:

- return the value directly when failure is not part of the contract
- return `bool` or a status enum when the caller only needs success/failure
- return a result enum when the caller needs either a value or an error reason
- use `out` when the caller should choose the destination storage

Do not make recoverable failure invisible.

The quick rule:

```stark
fn i32[min max] AlwaysWorks();
fn bool TryWrite(out i32[min max] value);
fn SaveStatus Save();
fn FirstResult TryReadFirst(i32[min max][] values, u64[0 max] count);
```

If the function name starts with `Try`, make the failure path visible in the
return type and make the caller handle that return value.

Choose `bool` only when the caller does not need a reason. Choose a named enum
when the reason changes what the caller should do:

```stark
enum LoginStatus {
    Ok,
    MissingUser,
    BadPassword,
    Locked,
}

fn LoginStatus CheckLogin(bool hasUser, bool passwordOk, bool locked) {
    if (!hasUser) {
        return LoginStatus.MissingUser;
    }

    if (locked) {
        return LoginStatus.Locked;
    }

    if (!passwordOk) {
        return LoginStatus.BadPassword;
    }

    return LoginStatus.Ok;
}
```
