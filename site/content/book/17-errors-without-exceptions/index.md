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

{{< stark-sample "samples/out-parameter.stark" >}}

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
fn bool TryDivide(i32[min max] numerator, i32[min max] denominator, out i32[min max] result)
{
    if (denominator == 0)
    {
        result = 0;
        return false;
    }

    result = numerator / denominator;
    return true;
}

fn i32[min max] UseTryDivide()
{
    stack mut i32[min max] quotient = 0;
    if (!TryDivide(21, 3, quotient))
    {
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
fn bool TryMakeReadyLabel(out OwnedAscii label)
{
    stack MemoryStatus copied = FromConstAscii(label, "ready");

    switch (copied)
    {
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

{{< stark-sample "samples/result-status-enum.stark" >}}

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
enum SaveStatus
{
    Ok,
    PermissionDenied,
    DiskFull,
}

fn SaveStatus SaveMarker(bool canWrite, bool hasSpace)
{
    if (!canWrite)
    {
        return SaveStatus.PermissionDenied;
    }

    if (!hasSpace)
    {
        return SaveStatus.DiskFull;
    }

    return SaveStatus.Ok;
}
```

The caller handles it with `switch`:

```stark
switch (SaveMarker(true, true))
{
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
finite law bool IOOk(IOStatus status)
{
    switch (status)
    {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}
```

For allocation-backed APIs, switch on `MemoryResult<T>`:

```stark
fn bool FormatNumber(i32[min max] value, out OwnedAscii text)
{
    stack MemoryResult<OwnedAscii> formatted = ToAscii(value);

    switch (formatted)
    {
        case MemoryResult<OwnedAscii>.Err(var error):
            text = new();
            return false;
        case MemoryResult<OwnedAscii>.Ok(var result):
            text = result;
            return true;
    }
}
```

## Step 3: Propagate With `try` When This Function Cannot Handle The Failure

`switch` is for the function that can actually do something about the failure.
Most functions in a call chain cannot: they only succeed when their callees
succeed. Writing a full `switch` around every fallible call buries the
interesting code under plumbing.

For that case Stark has one propagation expression, `try`:

{{< stark-sample "samples/try-propagation.stark" >}}

`try` does not work on special compiler-blessed types. It works on any
**propagatable** enum: a two-variant enum whose declaration marks one variant
as the success and one as the failure with the innate variant attributes
`[Ok]` and `[Err]`. That is exactly how the standard library declares its
result types:

```stark
public enum Result<T, E>
{
    [Ok] Ok(T),
    [Err] Err(E),
}

public enum Option<T>
{
    [Ok] Some(T),
    [Err] None,
}
```

Nothing in those declarations is privileged. A user enum with completely
different names is just as propagatable, because the roles come from the
attributes — never from the type name, the variant names, or standard-library
identity. The sample above declares its own:

```stark
enum ParseOutcome
{
    [Ok] Parsed(i32[min max]),
    [Err] Rejected(ParseError),
}
```

The role rules are checked where the enum is declared: exactly two variants,
one `[Ok]` and one `[Err]`, each carrying at most one payload, and the
attributes take no arguments. An enum with the right shape but no role
attributes is not propagatable — roles are opt-in, never inferred. That is why
`DivideResult` from Step 2 works with `switch` but not with `try`: it never
declared its roles.

`try expr` then does exactly one thing. If `expr` is the `[Ok]` variant, it
evaluates to that variant's payload and the function continues. If it is the
`[Err]` variant, the enclosing function returns that failure immediately —
rewrapped in its own return type's `[Err]` variant — running the same drops a
written `return` would.

```stark
fn System.Result<i32[min max], LoadError> LoadDigit(i32[min max] raw)
{
    stack i32[min max] digit = try ParseDigit(raw);
    return System.Result<i32[min max], LoadError>.Ok(digit * 10);
}
```

For `try ParseDigit(raw)` to compile, three requirements connect the operand
and the enclosing function:

- **The operand's type is a propagatable enum.** Here `ParseDigit(raw)`
  returns `ParseOutcome`, a user enum that carries the `[Ok]`/`[Err]` roles.
  `try` on anything else is rejected; there is nothing to unwrap.
- **The enclosing function's return type is also a propagatable enum.**
  `LoadDigit` returns `System.Result<i32[min max], LoadError>`, so a
  propagated failure has an `[Err]` variant to come back in. It does not need
  to be the same enum as the operand, or even the same generic family — here
  a `ParseOutcome` operand propagates through a `System.Result`-returning
  function.
- **The failure payloads are connected.** The operand fails with `ParseError`;
  the function fails with `LoadError`. Either the two types are identical, or
  the enclosing failure type declares a `from` funnel for the operand's
  failure type (shown below), or both `[Err]` variants are unit-like and
  carry no payload at all. If none of those hold, the `try` is a compile
  error — Stark never invents a conversion, and never silently discards an
  error value.

The success payloads do **not** need to match. `try ParseDigit(raw)` yields
the operand's success value — the `i32[min max]` inside `ParseOutcome.Parsed`
— for this function to keep working with. What `LoadDigit` later wraps in its
own `Ok(...)` is unrelated to what it unwrapped; only the failure path ties
the two signatures together.

Two more rules keep `try` honest:

- it is a leading keyword you can grep for, never a hidden suffix, so every
  early return out of a function is visible at the start of the line that
  causes it
- it may only appear where its early return sits at a statement boundary: the
  whole initializer of a binding, the whole right side of an assignment, the
  operand of `return`, or a bare expression statement

`Use(try a(), try b())` is rejected for that second rule. Bind each fallible
call to a local first, then use the locals:

{{< stark-sample "rejected/try-nested-in-call.stark" >}}

When the callee fails with a different error type than the caller returns, the
caller's error enum declares once how it absorbs that cause by marking a
variant with `from`:

```stark
enum LoadError
{
    Parse from ParseError,
    TooLarge,
}
```

`Parse from ParseError` is an ordinary single-payload variant plus a funnel
declaration: a `ParseError` propagated by `try` inside a
`Result<_, LoadError>` function is wrapped into `LoadError.Parse`
automatically, so the call sites stay bare. The conversion is a zero-cost
variant wrap with no hidden allocation or dispatch. The funnel also connects
different enum families: a `try` whose operand is a standard-library
`IOResult<T>` (which fails with `IOError`) inside a function returning
`Result<T, LoadError>` converts through an `Io from IOError` funnel declared
on `LoadError`. An enum may declare at most one `from` funnel per source
type, and a cross-family `try` with no matching funnel is a compile error
rather than a silent guess.

The division of labor is simple: `switch` where the failure is handled, `try`
everywhere in between.

## Step 4: Reserve Traps For Unrecoverable States

Some failures are not recoverable program states. Those belong in trap-style
paths rather than exception unwinding.

Stark has no general exception unwinding. Normal cleanup stays tied to ordinary
control flow, and recoverable failure stays in the function's return value.

Good unrecoverable examples are source mistakes or violated invariants:

```stark
fn i32[min max] RequireFirst(i32[min max][] values)
{
    return values[0];
}
```

That function is only correct when callers pass a non-empty slice. If empty
input is recoverable for the API, do not rely on a trap. Return a status or
result instead:

```stark
enum FirstResult
{
    Ok(i32[min max]),
    Empty,
}

fn FirstResult TryFirst(i32[min max][] values, u64[0 max] count)
{
    if (count == 0)
    {
        return FirstResult.Empty;
    }

    return FirstResult.Ok(values[0]);
}
```

## Step 5: Choose The Error Shape From The Caller Workflow

When designing a Stark API, ask which of these fits:

- return the value directly when failure is not part of the contract
- return `bool` or a status enum when the caller only needs success/failure
- return a result enum when the caller needs either a value or an error reason;
  mark its variants `[Ok]`/`[Err]` if callers should be able to propagate it
  with `try`
- use `out` when the caller should choose the destination storage
- propagate with `try` (and a `from` funnel for cross-family errors) when the
  current function cannot handle the failure itself

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
enum LoginStatus
{
    Ok,
    MissingUser,
    BadPassword,
    Locked,
}

fn LoginStatus CheckLogin(bool hasUser, bool passwordOk, bool locked)
{
    if (!hasUser)
    {
        return LoginStatus.MissingUser;
    }

    if (locked)
    {
        return LoginStatus.Locked;
    }

    if (!passwordOk)
    {
        return LoginStatus.BadPassword;
    }

    return LoginStatus.Ok;
}
```
