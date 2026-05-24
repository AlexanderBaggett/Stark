# Stark Callables And Closures Reference

Start with direct calls. Use callable values only when the API needs
indirection.

## Direct Function Calls

```stark
finite law i32[min max] Add(i32[min max] left, i32[min max] right)
{
    return left + right;
}

finite law i32[min max] UseAdd()
{
    return Add(20, 22);
}
```

A named function can become a callable value, but an ordinary call remains
direct source code.

## Function Pointers

Use `fnptr` for thin, non-capturing callable values.

```stark
finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

fn i32[min max] Apply(
    fnptr<finite law i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}

fn i32[min max] UseApply()
{
    return Apply(Double, 21);
}
```

The function kind is part of the type:

```stark
fnptr<fn i32[min max](i32[min max])>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

A stronger function can flow into a weaker callable slot, but a weaker
function cannot satisfy a stronger callable type.

## Function Pointer Memory Contracts

Function pointer types can describe memory-backed arguments using `arg0`,
`arg1`, and so on.

```stark
fnptr<fn void(borrow mut Buffer, borrow mut Buffer) where overlap(arg0, arg1)>
fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>
fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>
```

Use this when an indirect callback accepts overlap, requires sameness, or uses
a bounded raw pointer region.

## Non-Capturing Lambdas

Non-capturing lambdas can be assigned to compatible `fnptr` values.

```stark
fn i32[min max] UseLambda()
{
    stack fnptr<fn i32[min max](i32[min max])> increment =
        (i32[min max] value) => value + 1;

    return increment(41);
}
```

A `fnptr` has no environment storage, so captures are not allowed.

## Capture Lists

Capturing lambdas must say what they capture.

```stark
capture(copy scale) (i32[min max] value) => value * scale
```

Capture modes:

| Mode | Meaning |
| --- | --- |
| `copy x` | Copy a cheap copyable value into the closure |
| `move x` | Move ownership into the closure |
| `read x` | Capture read-only access to existing storage |
| `mut x` | Capture mutable access to existing storage |
| `out x` | Capture a write-only destination |
| `init x` | Capture uninitialized destination storage |
| `unsafe addr x` | Capture a low-level address capability in unsafe code |
| `unsafe shared x` | Capture explicit shared state in unsafe code |

Example with mutable capture:

```stark
fn i32[min max] CountEvents()
{
    stack mut i32[min max] total = 0;

    stack mut closure<mut fn void(i32[min max])> add =
        capture(mut total) (i32[min max] value) =>
        {
            total += value;
            return;
        };

    add(40);
    add(2);
    return total;
}
```

## Inline Closures

Use `inline closure<...>` for call-now helper APIs. The callback cannot be
stored, returned, placed in an array, or converted to `fnptr`.

```stark
inline fn i32[min max] ApplyInline(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] AddOffsetInline(i32[min max] offset)
{
    return ApplyInline(
        32,
        capture(copy offset) (i32[min max] value) => value + offset);
}
```

Use this for immediate algorithm adapters and small callback helpers.

## Borrow Closures

Use `borrow closure<...>` for a non-owning callback view that the callee may
call but may not retain.

```stark
fn i32[min max] ApplyBorrow(
    borrow closure<fn i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}

fn i32[min max] AddOffsetBorrow(i32[min max] offset)
{
    return ApplyBorrow(
        capture(copy offset) (i32[min max] value) => value + offset,
        32);
}
```

Use `mut borrow closure<mut ...>` when invocation mutates captured state.

```stark
fn void PushEvent(
    mut borrow closure<mut fn void(i32[min max])> sink,
    i32[min max] value)
{
    sink(value);
    return;
}
```

## Heap Closures

Use `heap closure<...>` when the callback is stored, returned, queued, or
retained.

```stark
fn heap closure<finite law i32[min max](i32[min max])> MakeAdder(
    i32[min max] offset)
{
    return heap capture(copy offset) (i32[min max] value) => value + offset;
}

fn i32[min max] UseAdder()
{
    stack heap closure<finite law i32[min max](i32[min max])> addTwo =
        MakeAdder(2);

    return addTwo(40);
}
```

Heap closures own their environment. They should copy or move data they retain,
not keep ordinary stack borrows by default.

## Once Closures

Use `once` when calling consumes the closure.

```stark
fn heap closure<once fn i32[min max]()> MakeOneShot(i32[min max] value)
{
    return heap capture(copy value) () => value;
}

fn i32[min max] RunOnce(heap closure<once fn i32[min max]()> producer)
{
    return producer();
}
```

After a `once` closure is called, it cannot be called again.

## Thread Entries

Thread entry values should be plain function items or non-capturing callable
values whose sharing behavior is visible.

```stark
import System.Threading
module Demo.Threads

fn i32[min max] Worker()
{
    return 7;
}

fn i32[min max] RunThread()
{
    stack ThreadEntry entry = Worker;
    stack mut Thread worker = new(entry);
    stack ThreadJoinResult joined = worker.Join();

    switch (joined)
    {
        case ThreadJoinResult.Ok(var value):
            return value;
        case ThreadJoinResult.Err(var error):
            return 1;
    }
}
```

Do not hide shared mutable state inside an unmarked callback.

## Selection Guide

| Intent | Spelling |
| --- | --- |
| Ordinary call | Named function call |
| Thin callback with no captures | `fnptr<...>` |
| Callback called immediately | `inline closure<...>` |
| Runtime callback view, not retained | `borrow closure<...>` |
| Callback mutates its environment | `mut borrow closure<mut ...>` |
| Stored or returned callback | `heap closure<...>` |
| Callback consumed by invocation | `heap closure<once ...>` |
