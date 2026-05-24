# Stark Diagnostics Guide

Stark diagnostics usually mean a required condition is not visible in the
source. Read the category first, then change the code so ownership, borrow,
range, storage, or package intent is explicit.

## Reading Order

1. Find the diagnostic category.
2. Look at the highlighted source expression.
3. Ask what type, ownership, borrow, range, package, or native rule was
   expected.
4. Make that condition visible in source.

Avoid moving code around blindly. A Stark diagnostic is usually pointing to a
missing source fact.

## Use After Move

If a value was moved, it cannot be read until reinitialized.

```stark
struct Box
{
    i32[min max] Value;
}

fn void Inspect(borrow Box value)
{
    return;
}

fn i32[min max] ReadAfterBorrow()
{
    stack Box box = new Box()
    {
        Value = 1
    };

    Inspect(box);
    return box.Value;
}
```

If the callee truly needs ownership, replace the value before reading it again.

```stark
fn void Consume(Box value)
{
    return;
}

fn i32[min max] MoveThenReplace()
{
    stack mut Box box = new Box()
    {
        Value = 1
    };

    Consume(box);
    box = new Box()
    {
        Value = 2
    };

    return box.Value;
}
```

## Borrow Escapes

Plain `borrow` is temporary. Use `retborrow` when the borrow deliberately
escapes through the return value.

```stark
struct Cell
{
    i32[min max] Value;
}

finite law retborrow i32[min max] GoodSlot(retborrow Cell cell)
{
    return cell.Value;
}
```

If callers only need the value, return an owned value instead.

## Immutable Local Assignment

Use `mut` only when the binding is meant to change.

```stark
fn i32[min max] CountToTwo()
{
    stack mut i32[min max] value = 0;
    value += 1;
    value += 1;
    return value;
}
```

Do not mark every local `mut`. Treat it as a source-level note that the value
changes.

## Null Borrow

Safe borrows are non-null. Keep nullable data as raw until checked, then return
a Stark status or enum.

```stark
public enum MessageStatus
{
    Missing,
    Present,
}

unsafe ffi fn rawptr<i8[min max]> platform_message();

public unsafe fn MessageStatus CheckMessage()
{
    stack rawptr<i8[min max]> pointer = platform_message();
    if (pointer == null)
    {
        return MessageStatus.Missing;
    }

    return MessageStatus.Present;
}
```

## Hidden Slice Storage

An array initializer needs owning backing storage. Create a fixed array first,
then take a slice view.

```stark
fn i32[min max] First()
{
    stack i32[min max][3] values =
    {
        1, 2, 3
    };

    stack i32[min max][] view = values;
    return view[0];
}
```

Do not assign an array initializer directly to a slice.

## Overlap Rejected

Ordinary memory-backed parameters are non-overlapping by default. If overlap
is part of the API, say so and handle it.

```stark
fn void MoveOverlapSafe(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    return;
}
```

Use `if disjoint(...)` inside an overlap-safe API when a fast path requires
separate regions.

## Range Or Narrowing Error

A function body must be valid for every value promised by its parameter type.
Put the smaller range on the input or check before converting.

```stark
finite law i32[0 10] KeepSmall(i32[0 10] value)
{
    return value;
}

fn bool TryKeepSmall(i32[min max] value, out i32[0 10] destination)
{
    if (value < 0 || value > 10)
    {
        return false;
    }

    destination = (i32[0 10])value;
    return true;
}
```

## Function Kind Mismatch

A stronger function kind cannot call work that violates its promise. If a
helper is pure and always returns, give the helper the stronger kind. If it
does IO, allocation, synchronization, or FFI, make the caller an ordinary
`fn`.

```stark
finite law i32[min max] AddOne(i32[min max] value)
{
    return value + 1;
}

finite law i32[min max] UseAddOne(i32[min max] value)
{
    return AddOne(value);
}
```

## Callable Mismatch

`fnptr` carries the function kind. A general `fn` cannot satisfy a
`fnptr<finite law ...>` slot.

```stark
finite law i32[min max] Clamp(i32[min max] value)
{
    if (value < 0)
    {
        return 0;
    }

    return value;
}

fn i32[min max] UseClamp()
{
    stack fnptr<finite law i32[min max](i32[min max])> op = Clamp;
    return op(4);
}
```

Capturing lambdas cannot be stored in a plain `fnptr`. Use a closure type.

## Switch Coverage And Dead Arms

When all enum variants are already handled, a later `default` arm is dead.
Remove the dead arm or make an earlier pattern narrower.

```stark
enum Token
{
    End,
    Integer(i32[min max]),
}

finite law i32[min max] Score(Token token)
{
    switch (token)
    {
        case Token.End:
            return 0;
        case Token.Integer(var value):
            return value;
    }
}
```

## Enum ABI Boundary

Do not expose ordinary Stark enums directly as C ABI values. Convert to an
explicit scalar tag or interop representation at the wrapper boundary.

```stark
public enum Mode
{
    Fast,
    Safe,
}

internal finite law i32[min max] ModeTag(Mode mode)
{
    switch (mode)
    {
        case Mode.Fast:
            return 1;
        case Mode.Safe:
            return 2;
    }
}
```

## Package And Native Diagnostics

Check source imports and manifest dependencies together.

```stark
import Geometry
module App
```

```toml
[dependencies]
geometry = { path = "../geometry" }
```

For native package errors, fix the package that owns the FFI boundary.

```toml
[native]
sources = ["RaylibNative.c"]
pkg-config = ["raylib"]
```

Do not copy linker flags into every consuming executable.

## Debugging Habits

- Split complex expressions into named locals.
- Write the storage class and integer range you intend.
- Use `borrow` when a function does not need ownership.
- Use `retborrow` only when a returned borrow is intentional.
- Handle status/result values with `switch`.
- Keep raw pointer work inside small wrapper functions.
- Keep private package details behind `internal` or module-private APIs.
