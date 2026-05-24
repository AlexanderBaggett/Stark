+++
title = "34. Reading Stark Diagnostics"
weight = 340
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/33-unsafe-stark-raw-pointers/"
next = "/book/35-generated-ir/"
aliases = ["/book/28-reading-diagnostics/", "/book/30-reading-diagnostics/", "/book/31-reading-diagnostics/"]
+++

# Reading Stark Diagnostics

Stark rejects programs when a required guarantee is not visible in the source.

## Step 1: Start With The Category

Diagnostics include a code and a category. The category tells you which part of
the program needs attention:

- parser diagnostics: the source shape is not valid Stark syntax
- type diagnostics: a value does not match the expected type
- ownership diagnostics: a moved value is used again
- borrow diagnostics: access or escape rules are not satisfied
- range diagnostics: an integer range or endpoint is invalid
- package diagnostics: imports or package metadata cannot be resolved
- native diagnostics: a native source, library, or discovery hook is missing

Read a diagnostic in this order:

1. Find the category.
2. Look at the highlighted source expression.
3. Ask what the type, ownership, borrow, range, or package rule expected.
4. Change the source so that expectation is visible.

Avoid trying to silence the diagnostic by moving code around blindly. Stark
diagnostics are usually pointing at a missing source condition.

For a syntax diagnostic, first check the shape against the grammar you meant to
write. Examples:

```stark
import System
module App

export fn i32[min max] main()
{
    return 0;
}
```

Imports come before `module`, and the function body uses braces.

For a type diagnostic, ask what type the left side promised:

```stark
fn i32[min max] UseWholeRange()
{
    stack i32[min max] value = 10;
    return value;
}
```

For a range diagnostic, put the accepted value range directly on the parameter
or local:

```stark
finite law u8[0 100] Percent(u8[0 100] value)
{
    return value;
}
```

## Step 2: Fix Move Errors By Restoring Ownership Clarity

This rejected example is checked by the book sample test:

{{< stark-sample "assets/book/negative-samples/use-after-move.stark" >}}

The important line is the read after `Consume(box)`. Stark tells you that the
value moved and must be reinitialized before it can be read.

The fix is to reinitialize the binding or change the callee to borrow instead
of taking ownership.

```stark
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

When the callee truly needs ownership, reinitialize before reading again:

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

## Step 3: Fix Borrow Escapes By Naming The Escape

This rejected example tries to return a plain `borrow`:

{{< stark-sample "assets/book/negative-samples/plain-borrow-return-escape.stark" >}}

The diagnostic tells you to use `retborrow` or `storeborrow`. That wording is
important. Stark is not asking for a hidden lifetime annotation; it is asking
whether the API really returns a borrow or stores one somewhere longer-lived.

```stark
fn retborrow Box ReturnBorrow(retborrow Box box)
{
    return box;
}
```

Use `borrow` when access is temporary, `retborrow` when the borrow is returned,
and a stored-borrow form only when the API truly stores it.

If the function only needs to read the value, do not return a borrow at all:

```stark
fn i32[min max] ReadValue(borrow Box box)
{
    return box.Value;
}
```

## Step 4: Fix Storage Errors By Creating Backing Storage

This rejected example asks a slice view to appear from an array initializer:

{{< stark-sample "assets/book/negative-samples/slice-literal-hidden-storage.stark" >}}

The diagnostic points at the initializer and explains that array initializers
need a fixed-size array target. The fix is to create backing storage first:

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};
stack i32[min max][] view = values;
```

The same rule applies to text views: use `ascii` or `unicode` for views, and
`Ascii`, `Unicode`, `OwnedAscii`, or `OwnedUnicode` when storage must be owned.

## Step 5: Fix Switch Coverage By Removing Dead Arms

This rejected example handles every enum variant, then adds a `default` arm:

{{< stark-sample "assets/book/negative-samples/unreachable-switch-default.stark" >}}

The diagnostic points out that the later arm is unreachable. The fix is to
remove the dead arm or make an earlier pattern narrower.

Range diagnostics have the same flavor: make the accepted range visible.

{{< stark-sample "assets/book/negative-samples/implicit-integer-narrowing.stark" >}}

The fix is not to hope the value is small. Put the small range on the input or
check before converting:

```stark
finite law i32[0 10] KeepSmall(i32[0 10] value)
{
    return value;
}
```

When a value is accepted from a wider range, narrow it only after the source
condition is visible:

```stark
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

## Step 6: Fix Function-Kind Errors By Moving The Effect

This rejected example calls an ordinary `fn` from a `law` function:

{{< stark-sample "assets/book/negative-samples/law-calls-fn.stark" >}}

The fix depends on what the helper really does. If it is pure and always
returns, mark the helper with the stronger kind:

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

If the helper performs IO, allocation, synchronization, or other general work,
make the caller an ordinary `fn` instead.

## Step 7: Fix Callable Errors By Matching The Callable Type

This rejected example assigns a callable value to a stronger `fnptr` type than
the function declaration promises:

{{< stark-sample "assets/book/negative-samples/fnptr-kind-mismatch.stark" >}}

The fix is to either weaken the expected callable type or strengthen the
function declaration when the body honestly satisfies it:

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

## Step 8: Fix Package And Native Diagnostics At The Boundary

Package diagnostics usually mean the module or project metadata does not name
what the source imports. Check the import and the manifest together:

```stark
import Geometry
module App
```

```toml
[dependencies]
geometry = { path = "../geometry" }
```

Native diagnostics usually mean the wrapping package did not name a native
source, discovery hook, or fallback library:

```toml
[native]
sources = ["RaylibNative.c"]
pkg-config = ["raylib"]
```

Fix the package that owns the native boundary. Do not copy linker flags into
every consuming executable.

For missing imports, check the source and the manifest together. The source
must import the module, and the project must depend on the package:

```stark
import System.Console
module App
```

```toml
[dependencies]
stdlib = { path = "../../stdlib" }
```

For missing native functions, check the Stark declaration and the native symbol
name together:

```stark
unsafe ffi fn i32[min max] stark_native_value();
```

```c
int stark_native_value(void) {
    return 42;
}
```

## Step 9: Make The Missing Condition Easy To See

When Stark reports that a guarantee is missing, make the condition explicit:

- split complex expressions into named locals
- write the storage class and range you intend
- choose `borrow` when a function does not need ownership
- use `retborrow` only when a borrow deliberately escapes by return
- handle status/result values with `switch`
- keep raw pointer work inside small wrapper functions
- avoid exposing private package details across package boundaries

Good Stark diagnostics should teach the missing condition. Good Stark source
should make that condition easy to see.
