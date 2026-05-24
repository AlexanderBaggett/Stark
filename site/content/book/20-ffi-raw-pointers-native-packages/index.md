+++
title = "20. FFI, Raw Pointers, and Native Packages"
weight = 200
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/19-callable-values/"
next = "/book/21-console-process-platform/"
aliases = ["/book/19-ffi-raw-pointers-native-packages/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/language/ProjectsAndSolutions/"

[[example_refs]]
title = "FFI Example"
href = "/reference/examples/ffi/Ffi.stark"

[[example_refs]]
title = "Raylib Package"
href = "/reference/examples/raylib/README.md"
+++

# FFI, Raw Pointers, and Native Packages

This chapter builds a low-level boundary in layers: declare the foreign call,
contain raw pointers, add region bounds where possible, then return to a safe
Stark wrapper.

{{< stark-sample "samples/ffi-raw-pointers.stark" >}}

## Step 1: Put The Foreign Declaration At The Boundary

Use `unsafe ffi fn` for foreign-facing declarations:

```stark
unsafe ffi fn i32[min max] native_value();
```

An FFI declaration tells Stark that the function body lives outside ordinary
Stark source. The declaration should use types that make sense at the foreign
boundary. It is marked `unsafe` because callers and wrapper authors must
document the native function's lifetime, aliasing, unwinding, and ABI behavior.

Ordinary Stark enums are not automatic native ABI types. Design the boundary
with explicit scalar tags, payload pointers, or a purpose-built interop
representation instead:

{{< stark-sample "rejected/enum-abi-boundary.stark" >}}

Use `export fn` for safe Stark functions that must be visible to the native
world, such as the hosted entrypoint:

```stark
export fn i32[min max] main()
{
    return 0;
}
```

Add `unsafe` and `ffi` only when the exported boundary itself uses unsafe or
foreign ABI features, such as raw pointer arguments or a platform callback
shape.

Foreign C varargs are also explicit:

```stark
unsafe ffi varargs fn i32[min max] printf(ascii format);

unsafe fn i32[min max] PrintScore(i32[min max] score)
{
    return printf("score: %d\n", score);
}
```

Pass C varargs in the type the C function expects. For example, pass `f64` for
a floating-point C vararg and cast smaller integer values to an explicit
`i32` or `u32` when that is what the format expects.

## Step 2: Keep Raw Pointer Work Small And Audited

Raw pointers are the explicit low-level pointer forms:

- `rawptr<T>` for readonly raw access
- `rawmutptr<T>` for mutable raw access

Raw pointers may be `null`. Safe borrows may not.

```stark
unsafe fn bool IsMissing(rawptr<i32[min max]> value)
{
    stack rawptr<i32[min max]> missing = null;
    return value == missing;
}
```

Declaring raw pointer signatures, constructing `null`, dereferencing raw
pointers, pointer arithmetic, raw pointer casts, bounded raw pointer regions,
and `slice(pointer, count)` all require an unsafe context. Use an `unsafe fn`
when the caller must uphold raw memory or ABI conditions. Use an `unsafe { ... }`
block when a safe wrapper contains a small audited low-level step.

Readonly raw access cannot be upgraded into mutable raw access. If a value is
not mutable, taking its address gives readonly raw access, and a cast cannot
strengthen that permission:

{{< stark-sample "rejected/rawptr-strengthen-mutability.stark" >}}

Use raw pointers for FFI, runtime internals, and small carefully reviewed
low-level boundaries. Do not use them as a general replacement for `borrow` or
`mut borrow`.

Check nullable native results before converting them to an ordinary Stark API:

```stark
unsafe ffi fn rawptr<i8[min max]> native_message();

public enum NativeMessage
{
    Missing,
    Present,
}

public unsafe fn NativeMessage CheckNativeMessage()
{
    stack rawptr<i8[min max]> message = native_message();
    if (message == null)
    {
        return NativeMessage.Missing;
    }

    return NativeMessage.Present;
}
```

The wrapper keeps the nullable pointer inside the unsafe layer. Callers receive
an ordinary enum.

## Step 3: Add Bounds To Raw Pointer Regions

Raw pointer parameters can state an element count:

```stark
unsafe fn void Fill(
    i64[0 max] length,
    rawmutptr<i32[min max]>[length] destination,
    i32[min max] value)
{
    return;
}
```

`rawptr<T>[count]` and `rawmutptr<T>[count]` are still raw pointers. They may
belong to FFI-owned storage, and they do not become safe borrows. The bound
says the region is valid for `count` contiguous elements of `T`. Positive
counts require non-null pointers; zero-length regions may be `null`.

Region expressions name subranges for contracts without building slices:

```stark
where disjoint(source[0, length], destination[0, length])
```

The same region expression can be checked at runtime with `if disjoint(...)`.
That lets low-level wrappers pick a fast non-overlap path and keep an
overlap-safe fallback for the branch where the regions may alias.

Inside `unsafe`, `slice(pointer, count)` turns a bounded raw pointer region
into an ordinary slice view. After that conversion, the wrapper can use ordinary
slice indexing rules.

A complete bounded copy wrapper has a fast disjoint path and a fallback:

```stark
unsafe fn void CopyBytesFast(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
    where disjoint(source[0, length], destination[0, length])
{
    stack i8[min max][] sourceView = slice(source, length);
    stack mut i8[min max][] destinationView = slice(destination, length);

    for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
    {
        destinationView[index] = sourceView[index];
    }

    return;
}

unsafe fn void CopyBytes(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
    where overlap(source, destination)
{
    if disjoint(source[0, length], destination[0, length])
    {
        CopyBytesFast(length, source, destination);
        return;
    }

    // Use an overlap-safe temporary copy here.
    return;
}
```

## Step 4: Return To Safe Borrows Or Status Values Quickly

Safe borrows carry stronger guarantees:

- non-null
- non-owning
- checked by Stark's borrow rules
- not pointer-to-pointer escape hatches

Raw pointers carry fewer guarantees and therefore ask for more care:

- they may be null
- they may point outside Stark-owned storage
- they may represent foreign lifetime rules
- dereference and conversion are explicit boundary operations

When possible, wrap raw-pointer work in a small API that returns ordinary Stark
status/result data.

```stark
public enum NativeOpenResult
{
    Ok(i32[min max]),
    Err,
}

unsafe ffi fn i32[min max] native_open();

public unsafe fn NativeOpenResult TryOpenNative()
{
    stack i32[min max] handle = native_open();
    if (handle < 0)
    {
        return NativeOpenResult.Err;
    }

    return NativeOpenResult.Ok(handle);
}
```

## Step 5: Put Native Metadata In The Package

Native-backed packages should own their native build settings. A downstream
program should not repeat linker flags for every executable that uses the
package.

A package manifest can declare native sources and discovery names:

```toml
[project]
name = "raylib"
version = "0.1.0"
kind = "library"

[library]
root = "Raylib.stark"
output = "RaylibStark"

[native]
sources = ["RaylibNative.c"]
pkg-config = ["raylib"]
```

Fallback metadata can describe local or platform-specific native paths:

```toml
[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11"]
```

Machine-local paths belong in user config, not in shared package manifests:

```toml
[native.paths]
raylib-src = "/path/to/raylib/src"
```

The package author writes the native requirements once. Package consumers use a
normal Stark dependency.

The consuming project should only name the package:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

## Step 6: Review The C ABI Surface For Smallness

Keep C-facing APIs small and deliberate:

- prefer simple scalar and pointer types at the boundary
- keep ownership transfer explicit
- do not let foreign exceptions or unwinding cross Stark frames
- convert raw/foreign results into Stark result/status values quickly
- keep ABI-visible `export` declarations rare

FFI is a power tool. Stark makes it available without pretending it has the
same guarantees as ordinary safe code.
