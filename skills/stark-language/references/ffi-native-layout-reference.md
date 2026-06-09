# Stark FFI, Native Layout, And ABI Reference

Keep the native boundary small. Ordinary Stark APIs should use ordinary Stark
types. FFI wrappers should translate to scalar values, raw pointers, status
enums, and explicit interop structs at the edge.

## Imported Foreign Functions

Use `unsafe ffi fn` for foreign functions:

```stark
unsafe ffi fn i32[min max] native_value();
unsafe ffi fn void native_draw(rawptr<NativeRectangle> rectangle);
```

Use C varargs only through an unsafe varargs declaration:

```stark
unsafe ffi(c) varargs fn System.C.c_int printf(rawptr<System.C.c_char> format);

unsafe fn i32[min max] PrintScore(i32[min max] score)
{
    stack System.C.CStringResult<System.C.OwnedCStr> created =
        System.C.FromAscii("score: %d\n");
    switch (created)
    {
        case System.C.CStringResult<System.C.OwnedCStr>.Err(var error):
            return -1;
        case System.C.CStringResult<System.C.OwnedCStr>.Ok(var value):
            stack mut System.C.OwnedCStr format = value;
            return printf(format.Data(), score);
    }
}
```

Pass ABI-ready values explicitly. Do not rely on hidden conversions at C
vararg boundaries.

## C Strings

Use `System.C` for null-terminated C strings. Stark `ascii` and `unicode` are
not implicitly convertible to `char*`.

- Pass `rawptr<System.C.c_char>` for C `const char*`.
- Pass `rawmutptr<System.C.c_char>` for mutable C `char*` or C-owned messages.
- Use `System.C.FromAscii` / `System.C.FromUnicodeUtf8` to create Stark-owned
  `OwnedCStr` values before calling C.
- Use `System.C.TryFromRawBounded` before viewing a raw C string returned by C.
- Use `System.C.ForeignOwnedCStr` plus a `System.C.CStringDisposer` for
  C-owned message strings that must be copied into Stark text and then released
  by the matching C dispose function.

## Exported Stark Functions

Use `export` for a real binary symbol: hosted entrypoints, plugin hooks,
runtime hooks, or deliberate native ABI functions.

```stark
export fn i32[min max] main()
{
    return 0;
}
```

Use `public` for downstream Stark source APIs. Do not use `export` just
because another Stark package should call a function.

## Raw Pointer Forms

```stark
rawptr<T>       // read-only raw pointer
rawmutptr<T>    // mutable raw pointer
```

Raw pointers may be `null`, dangling, unaligned, aliased, or foreign-owned.
Safe borrows cannot be null.

Check nullable native results while they are still raw:

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

Do not strengthen read-only raw access into mutable raw access. If the native
API writes through a pointer, make that visible with `rawmutptr<T>`.

## Bounded Raw Regions

Prefer bounded raw pointer regions for contiguous memory.

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
```

`slice(pointer, count)` is unsafe because the wrapper promises that the memory
is valid for the requested region. Keep that conversion close to the audited
boundary.

## Layout Expectations

Source-facing structs and records are ordinary Stark types:

```stark
public struct Rectangle
{
    i32[min max] Width;
    i32[min max] Height;
}
```

Do not assume an ordinary Stark type is automatically a C ABI contract. When a
native API expects a specific memory shape, introduce an interop type and
convert explicitly.

```stark
internal struct NativeRectangle
{
    f32 X;
    f32 Y;
    f32 Width;
    f32 Height;
}

internal finite law NativeRectangle ToNative(Rectangle rectangle)
{
    return new NativeRectangle()
    {
        X = 0.0f,
        Y = 0.0f,
        Width = (f32)rectangle.Width,
        Height = (f32)rectangle.Height
    };
}
```

Keep interop types `internal` unless the native representation is truly part
of the Stark package API.

## Enums At Native Boundaries

Stark enums are for Stark APIs. Cross a C boundary with explicit tags or
purpose-built payload structs.

```stark
public enum DrawMode
{
    Lines,
    Filled,
}

internal finite law i32[min max] DrawModeTag(DrawMode mode)
{
    switch (mode)
    {
        case DrawMode.Lines:
            return 1;
        case DrawMode.Filled:
            return 2;
    }
}

unsafe ffi fn void native_set_draw_mode(i32[min max] mode);

public unsafe fn void SetDrawMode(DrawMode mode)
{
    native_set_draw_mode(DrawModeTag(mode));
    return;
}
```

## Status And Output Wrappers

Translate status-plus-output native APIs into Stark result enums.

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

## Native Package Metadata

Put native requirements in the package that owns the wrapper:

```toml
[native]
sources = ["NativeShim.c"]
pkg-config = ["raylib"]

[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m"]
```

Consumers should depend on the package, not repeat its linker settings.

## Boundary Checklist

- Preserve foreign symbol spellings exactly.
- Declare underscore-leading C symbols directly, such as `__error`; bare `_`
  is still the discard token and pattern.
- Keep unsafe blocks as small as the raw or foreign operation.
- Convert raw failures into Stark status/result values quickly.
- Do not let foreign unwinding cross Stark frames.
- Keep Stark enums, closures, and ordinary source-facing structs off C ABI
  surfaces unless a purpose-built interop contract exists.
- Use `public` for Stark callers and `export` only for binary visibility.
