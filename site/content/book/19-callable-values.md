+++
title = "19. Callable Values and Thread Entries"
weight = 190
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/18-generics-traits-doctrines/"
next = "/book/20-ffi-raw-pointers-native-packages/"
aliases = ["/book/18-callable-values/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[stdlib_refs]]
title = "System.Threading"
href = "/reference/standard-library/System.Threading/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Callable Values and Thread Entries

Callable values are deliberately narrow. Start with direct function calls, then
choose `fnptr` or a closure form only when the program actually needs a callable
value.

{{< stark-sample "assets/book/samples/callable-values.stark" >}}

## Step 1: Call Named Functions Directly First

A named function can be used as a function item:

```stark
fn i32[min max] Add(i32[min max] left, i32[min max] right)
{
    return left + right;
}
```

Calling `Add(20, 22)` is still an ordinary direct call. The function has not
become a raw pointer just because it has a name.

## Step 2: Promote To `fnptr` Only When Indirection Is Needed

Use `fnptr` when you want an indirect callable value:

```stark
stack fnptr<fn i32[min max](i32[min max], i32[min max])> add = Add;
```

That line is the explicit point where the function item is promoted to a
function pointer. The type includes the function kind and the parameter/return
types.

Function pointer signatures include the function kind:

```stark
fnptr<fn i32[min max](i32[min max])>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

A callable value must satisfy the kind expected by the `fnptr` type.

The kind matters most when a function accepts a callback. A general
`fnptr<fn ...>` can call ordinary code. A `fnptr<law ...>` requires a `law`
target. A `fnptr<finite ...>` requires a target that returns for valid inputs.
A `fnptr<finite law ...>` requires both. That keeps the callback rule visible
in the parameter type.

Use a helper when the callback is part of the API:

```stark
fn i32[min max] Apply(
    fnptr<finite law i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}

finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

fn i32[min max] UseApply()
{
    return Apply(Double, 21);
}
```

Function pointer values are non-null. They must be initialized from a compatible
function item or non-capturing lambda, so struct and record initializers need to spell out
any field or fixed-array element that contains a `fnptr` instead of relying on
zero-fill.

If a type stores a function pointer, initialize that field explicitly:

```stark
struct Operation
{
    fnptr<fn i32[min max](i32[min max])> Apply;
}

fn i32[min max] Increment(i32[min max] value)
{
    return value + 1;
}

fn Operation MakeOperation()
{
    return new Operation()
    {
        Apply = Increment
    };
}
```

The declaration on the function item matters. A general `fn` is not silently
upgraded to a stronger callable value just because its body is simple:

{{< stark-sample "assets/book/negative-samples/fnptr-kind-mismatch.stark" >}}

## Step 3: Put Memory Rules In `fnptr` Types When Needed

Function pointer types can also describe memory-backed parameters. Because the
type does not name callback parameters, use `arg0`, `arg1`, and so on inside
the `where` clause:

```stark
fnptr<fn void(borrow mut Buffer, borrow mut Buffer) where overlap(arg0, arg1)>
fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>
```

Use this when an indirect callback deliberately accepts overlap or requires two
arguments to name the same storage. If no relation is written, memory-backed
callback parameters follow the normal non-overlap rule.

Bounded raw pointer parameters can use earlier callback arguments as their
element count:

```stark
fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>
```

Here the first callback argument is valid for the number of elements supplied
as the second callback argument.

## Step 4: Use Non-Capturing Lambdas For Thin Callable Values

Non-capturing lambdas can be used where a matching function pointer is expected:

```stark
stack fnptr<fn i32[min max](i32[min max])> increment =
    (i32[min max] value) => value + 1;
```

This is intentionally narrow. A non-capturing lambda has no hidden environment
and can behave like a plain callable value.

## Step 5: Keep `fnptr` Thin And Non-Capturing

Capturing lambdas use an explicit capture list:

```stark
capture(copy scale, read table) (i32[0 max] index) =>
{
    return table[index] * scale;
}
```

Capture is never implicit. The capture list says whether a value is copied,
moved, read, mutably borrowed, or used as an output/init destination.

The safe capture modes are:

```stark
capture(copy scale) (i32[min max] value) => value * scale;
capture(move packet) () => packet.Length;
capture(read table) (i32[0 max] index) => table[index];
capture(mut total) (i32[min max] value) =>
{
    total += value; return total;
};
capture(out destination) () =>
{
    destination = 1; return true;
};
capture(init destination) () =>
{
    destination = new Item(); return true;
};
```

Two capture modes require an unsafe context because they cross ordinary safe
sharing rules:

```stark
capture(unsafe addr token) () => 0;
capture(unsafe shared state) () => 0;
```

Captures do not fit in an ordinary `fnptr`. A function pointer is just a thin
call target, so it cannot carry environment storage.

This intentionally rejected example is checked with the rest of the book:

{{< stark-sample "assets/book/negative-samples/capturing-lambda-fnptr.stark" >}}

Use a named function item or a non-capturing lambda when an API expects
`fnptr`. Use a closure target when the callback needs captures.

## Step 6: Use Inline Closures For Call-Now Helpers

An `inline closure<...>` is for APIs that call the callback during the current
operation and do not store it. Chapter 10 covers the full closure matrix; this
chapter uses `fn` examples unless the callable kind itself is the topic.

```stark
inline fn i32[min max] ApplyInline(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] AddOne()
{
    return ApplyInline(41, (i32[min max] value) => value + 1);
}
```

This is the shape to reach for in immediate APIs, UI layout helpers, small
algorithm adapters, and other code where the callback is invoked during the
current operation. Inline closures cannot be stored, returned, placed in arrays,
or converted to `fnptr`.

## Step 7: Use Borrow Closures For Non-Escaping Callback Views

A `borrow closure<...>` is a non-owning callback view. It may capture local
storage, but the view cannot outlive the storage it points at.

```stark
fn i32[min max] ApplyBorrow(
    borrow closure<fn i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}

fn i32[min max] AddOffset(i32[min max] offset)
{
    return ApplyBorrow(
        capture(copy offset) (i32[min max] value) => value + offset,
        41);
}
```

Use `mut borrow closure<mut ...>` when calling the closure mutates captured
state. `out` and `init` captures are write-only, and ownership validation
requires the closure body to assign them on every successful return path.

```stark
fn i32[min max] ApplyMut(
    mut borrow closure<mut fn i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}
```

## Step 8: Use Heap Closures For Retained Callback Storage

A `heap closure<...>` owns its environment. It is the form for callbacks that
are stored, returned, queued, or retained by another object.

```stark
fn heap closure<fn i32[min max](i32[min max])> MakeAdder(i32[min max] offset)
{
    return heap capture(copy offset) (i32[min max] value) => value + offset;
}

fn i32[min max] RunAdder()
{
    stack heap closure<fn i32[min max](i32[min max])> addTwo = MakeAdder(2);
    return addTwo(40);
}
```

Heap closures may capture copied values and moved owned values. They do not
retain ordinary stack borrows by default. A `heap closure<once ...>` is consumed
when called, so a second call is rejected as a use after move.

```stark
fn heap closure<once fn i32[min max]()> MakeOneShot(i32[min max] value)
{
    return heap capture(copy value) () => value;
}
```

## Step 9: Reuse Callable Rules For Thread Entries

Thread entry values are callable values too. Stark keeps them explicit because
threading widens the part of the program that can observe execution and shared
state.

A thread entry should be a plain function item or a callable value whose
capture and sharing behavior is visible. Do not smuggle shared mutable state
through an unmarked closure.

```stark
import System.Threading

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

`ThreadEntry` is a no-capture `fnptr` returning an integer exit
code. Use a named function or a non-capturing lambda for the first threading
surface.
