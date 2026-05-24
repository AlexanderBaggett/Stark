+++
title = "33. Unsafe Stark and Raw Pointers"
weight = 330
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/32-performance-tuning/"
next = "/book/34-reading-diagnostics/"
aliases = ["/book/29-unsafe-stark-raw-pointers/", "/book/30-unsafe-stark-raw-pointers/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"
+++

# Unsafe Stark and Raw Pointers

Unsafe Stark marks audited boundaries. It does not turn off parsing,
type-checking, ownership, initialization, range checking, or borrow validation.
Use unsafe code when the program depends on a condition that safe Stark cannot
check by itself.

The core habit is simple: write the unsafe operation in the smallest possible
place, then return to ordinary Stark values.

## Step 1: Isolate The Unsafe Boundary

Keep raw pointer and FFI work small.

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

The safe part of the wrapper should check nullability, translate platform
status into Stark result/status values, and expose ordinary Stark types where
possible. Raw pointers should not leak through a public API unless the API is
deliberately low-level.

Use an `unsafe fn` when callers must uphold the contract:

```stark
unsafe fn i32[min max] ReadAt(rawptr<i32[min max]> pointer)
{
    return *pointer;
}
```

Use an `unsafe { ... }` block when one small operation inside a safe wrapper
needs the audited boundary:

```stark
fn i32[min max] ReadKnownAddress(i64[0 max] address)
{
    unsafe
    {
        stack rawptr<i32[min max]> pointer = (rawptr<i32[min max]>)address;
        return *pointer;
    }
}
```

Keep the unsafe block as small as the operation that needs it.

Raw pointer and integer conversions also belong in that small region:

```stark
unsafe fn rawptr<i32[min max]> PointerFromAddress(i64[0 max] address)
{
    return (rawptr<i32[min max]>)address;
}

unsafe fn i64[0 max] AddressOf(rawptr<i32[min max]> pointer)
{
    return (i64[0 max])pointer;
}
```

Do not pass integer-address APIs through ordinary application code. Put them
behind a named wrapper whose contract explains where the address came from.

Unsafe code is needed for specific operations, not for a whole subsystem by
default. Common unsafe operations include:

- declaring or calling `unsafe ffi fn`
- declaring raw pointer locals
- taking a raw address with `&`
- dereferencing a raw pointer with `*`
- converting between raw pointers and integers
- constructing a slice from raw memory with `slice(pointer, count)`
- calling an `unsafe fn`
- registering an unsafe function item as a plain callback pointer

For example, taking a raw address and dereferencing it both belong inside an
unsafe boundary:

```stark
unsafe fn i32[min max] ReadLocalThroughPointer()
{
    stack mut i32[min max] value = 7;
    stack rawmutptr<i32[min max]> pointer = &value;
    return *pointer;
}
```

Prefer a safe borrow when the caller does not need raw pointer behavior:

```stark
fn i32[min max] ReadThroughBorrow(borrow i32[min max] value)
{
    return value;
}
```

## Step 2: Prefer Bounded Raw Pointer Regions

When unsafe code processes contiguous memory, prefer bounded raw pointer
parameters over unbounded raw pointers.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

`rawptr<T>[count]` and `rawmutptr<T>[count]` make the memory region visible.
That is the difference between "some address" and "this many elements from this
base." The latter can participate in disjoint checks, subregion contracts, and
accepted `independent` loops.

When a bounded raw region should behave like a slice inside the function,
materialize the slice explicitly:

```stark
fn void CopyBytes(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
    where disjoint(source[0, length], destination[0, length])
{
    unsafe
    {
        stack i8[min max][] sourceView = slice(source, length);
        stack mut i8[min max][] destinationView = slice(destination, length);

        for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
        {
            destinationView[index] = sourceView[index];
        }
    }
}
```

`slice(pointer, count)` is unsafe because the wrapper promises that the pointer
really is valid for that many elements.

Use `rawptr<T>` for read-only raw access and `rawmutptr<T>` for mutable raw
access:

```stark
unsafe fn i32[min max] ReadOnly(rawptr<i32[min max]> pointer)
{
    return *pointer;
}

unsafe fn void WriteOne(rawmutptr<i32[min max]> pointer)
{
    *pointer = 1;
    return;
}
```

Do not take a read-only raw pointer and turn it into mutable authority. If the
native API writes through the pointer, the wrapper should say so with
`rawmutptr<T>`.

## Step 3: Assert Only Guarantees You Audited

An `unsafe` block does not automatically make two regions separate. If a
low-level boundary has already checked or guaranteed separation, write a scoped
assertion:

```stark
unsafe assume disjoint(source[0, count], destination[0, count])
{
    CopyFast(source, destination);
}
```

Inside an `unsafe fn` or an existing `unsafe { ... }` block, the leading
`unsafe` is optional:

```stark
assume disjoint(source[0, count], destination[0, count])
{
    CopyFast(source, destination);
}
```

The assertion is a promise by the unsafe boundary. It should name visible roots
or representable subregions, not pointer values laundered through integers or
hidden helper calls.

## Step 4: Keep Safe Borrows Stronger Than Raw Pointers

Raw pointers may be null and may alias unless the API explicitly says otherwise.
Safe borrows are non-null and validated by Stark's borrow rules.

Check `null` while the value is still raw:

```stark
unsafe ffi fn rawptr<i8[min max]> platform_message();

unsafe fn bool HasMessage()
{
    stack rawptr<i8[min max]> pointer = platform_message();
    return pointer != null;
}
```

Do not assign `null` to a safe borrow or safe value. Convert raw platform
results into a Stark result enum before publishing them to ordinary callers.

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

This rejected sample shows one boundary Stark keeps:

{{< stark-sample "assets/book/negative-samples/rawptr-strengthen-mutability.stark" >}}

Do not convert a readonly raw pointer into mutable authority. If native code
needs mutable access, make that mutability visible in the raw pointer type and
in the wrapper's signature and documentation.

For native calls that return both a status and an output value, convert the
pair into a Stark enum:

```stark
public enum ReadNumberResult
{
    Err,
    Ok(i32[min max] Value),
}

unsafe ffi fn i32[min max] platform_read_number(rawmutptr<i32[min max]> value);

public unsafe fn ReadNumberResult ReadNumber()
{
    stack mut i32[min max] value = 0;
    stack i32[min max] status = platform_read_number(&value);
    if (status != 0)
    {
        return ReadNumberResult.Err;
    }

    return ReadNumberResult.Ok(value);
}
```

The public result has no nullable borrow and no exposed raw pointer.

## Step 5: Return To Safe Stark Quickly

A good unsafe API has a small trusted core and a boring safe surface. Keep the
unsafe reasoning local, then move back into ordinary Stark values, result enums,
bounded slices, or caller-owned buffers. That preserves the performance benefit
without making the rest of the program reason like C.

When registering a platform callback, keep unsafe erasure at the registration
site:

```stark
unsafe fn i32[min max] CallbackEntry()
{
    return 0;
}

unsafe ffi fn void register_callback(fnptr<fn i32[min max]()> callback);

unsafe fn void RegisterCallback()
{
    register_callback(CallbackEntry);
    return;
}
```

Only do this when the platform API really stores a plain function pointer and
the callback's unsafe preconditions are satisfied by that platform call.

For C-style variadic functions, keep the declaration unsafe and pass values in
the form the C side expects:

```stark
public unsafe ffi varargs fn i32[min max] printf(ascii format);

unsafe fn i32[min max] PrintScore(i32[min max] score)
{
    return printf("Score: %d\n", score);
}
```

Do not hide C varargs behind a broad formatting API until the wrapper has a
clear Stark contract for each supported argument type.
