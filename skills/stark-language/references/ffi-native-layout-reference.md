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

Use `[LinkName("foreign_symbol")]` when the Stark declaration name should
differ from the linker symbol and the ABI shape already matches:

```stark
[LinkName("vendor_current_value")]
unsafe ffi(c) fn i32[min max] CurrentValue();
```

`LinkName` is exact and zero-overhead. It does not change calling convention,
parameter lowering, return lowering, ownership, unwinding, or safety. Use a C
shim only when the native signature needs real ABI adaptation.

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
vararg boundaries. A `%s` conversion takes `rawptr<System.C.c_char>` or
`rawmutptr<System.C.c_char>`, never Stark `ascii`/`unicode` (compile-time error
STK3009); convert with `System.C.FromAscii(...)` and pass `OwnedCStr.Data()`.

## Calling Conventions

An `ffi` function uses the target's C ABI by default. A bare `unsafe ffi fn`
means `ffi(c)`. Spell a different convention explicitly with `ffi(abi)`:

```stark
unsafe ffi(c) fn i32[min max] puts(rawptr<i8[min max]> text);
unsafe ffi(stdcall) fn i32[min max] LegacyCall(i32[min max] value);
```

Supported names: `c`, `cdecl`, `stdcall`, `fastcall`, `thiscall`, `vectorcall`,
`sysv`, `win64`, `aapcs`, `aapcs64`. An ABI the active target does not support
is a compile-time error (STK2111), so convention-specific declarations are only
checkable under a matching `--target`.

The ABI is part of function-pointer type identity: `fnptr<ffi(c) fn void()>`,
`fnptr<ffi(stdcall) fn void()>`, and `fnptr<fn void()>` are distinct,
incompatible types, and there is no implicit conversion between them. Safety is
a separate fact: an unsafe foreign callback needs `fnptr<unsafe ffi(c) fn ...>`,
and promoting an unsafe function item into it requires an unsafe context.

One declaration can select a different ABI per target with `ffi(platform(...))`.
Keys are `os.arch`, a bare `os`, or `default`; the most specific match wins:

```stark
unsafe ffi(platform(
    windows.x86: stdcall,
    windows.x64: win64,
    linux.x64: sysv,
    default: c
)) fn i32[min max] HostCall(rawptr<i8[min max]> context);
```

## C Primitive Aliases

Use `System.C` aliases so FFI declarations mirror C headers instead of
hard-coding a width. They are target-resolved to ordinary Stark primitives.

```stark
unsafe ffi(c) fn System.C.c_int close(System.C.c_int fd);
unsafe ffi(c) fn System.C.c_size_t strlen(rawptr<System.C.c_char> text);
unsafe ffi(c) fn rawmutptr<System.C.c_void> malloc(System.C.c_size_t bytes);
unsafe ffi(c) fn void free(rawmutptr<System.C.c_void> ptr);
```

- Integer aliases: `c_char`, `c_schar`, `c_uchar`, `c_short`, `c_ushort`,
  `c_int`, `c_uint`, `c_long`, `c_ulong`, `c_longlong`, `c_ulonglong`,
  `c_size_t`, `c_ptrdiff_t`. `c_int` is `i32[min max]`; `c_long` is
  `i32[min max]` on ILP32/LLP64 and `i64[min max]` on LP64; `c_size_t` is
  `u64[0 max]` on 64-bit and `u32[0 max]` on 32-bit.
- `c_char` follows the target's plain-`char` signedness
  (`System.C.c_char_is_signed`). Use `c_schar`/`c_uchar` for explicit-signedness
  `char`, and `c_uchar`/`u8[0 max]` for non-text bytes.
- `c_void` is valid only as `rawptr<c_void>` / `rawmutptr<c_void>`; C `void`
  returns use Stark `void`.
- To leave the platform-width surface, bind into a Stark-typed local or cast the
  alias value to a Stark width. A qualified alias is not a valid C-style cast
  target, so `(System.C.c_size_t)value` does not parse — write
  `stack System.C.c_size_t size = value;` instead.

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

## C ABI Layout Attributes

Default Stark layout is not a stable ABI. A struct that must match a C aggregate
opts into C-compatible layout with attributes (Stark's `repr(C)` equivalent):

```stark
[StructLayout(C)]
struct Timespec
{
    System.C.c_long Seconds;
    System.C.c_long Nanoseconds;
}

[StructLayout(C), Pack(1)]
struct WireHeader
{
    u8[0 max] Tag;
    u32[0 max] Length;
}

[StructLayout(Explicit)]
struct WordParts
{
    [FieldOffset(0)] u32[0 max] Whole;
    [FieldOffset(0)] u16[0 max] Low;
    [FieldOffset(2)] u16[0 max] High;
}
```

- `[StructLayout(C)]`: declaration order, target C ABI alignment and padding.
- `[StructLayout(Explicit)]`: every field placed by `[FieldOffset(N)]`.
- `[Pack(N)]`: cap each field's effective alignment at `N` (power of two).
  `Pack(1)` is fully packed.
- `[Align(N)]`: raise the aggregate alignment to at least `N` (power of two); it
  never caps field alignment. With both, offsets come from `Pack`, then `Align`
  raises the struct alignment.

C-layout field types are limited to Stark sized primitives, `System.C` aliases,
raw pointers (including `rawptr<System.C.c_void>`), fixed arrays of FFI-safe
elements, and nested C/Explicit structs. Stark enums, dynamic storage, safe
borrows, closures, trait objects, and owning heap values are rejected.

For by-value C aggregate parameters and returns, `[StructLayout(C)]` plus
`ffi(c)` is the contract. The source declaration keeps the named struct type,
and the compiler lowers the FFI edge through the target C ABI carrier shape.
Use `[LinkName("NativeSymbol")]` when the Stark declaration name differs from
the C symbol; do not add a C shim merely to pass or return a C-layout aggregate
by value.

```stark
[StructLayout(C)]
public struct Vector2
{
    public f32 X;
    public f32 Y;
}

[LinkName("GetMonitorPosition")]
internal unsafe ffi(c) fn Vector2 raylib_GetMonitorPosition(i32[min max] monitor);

[LinkName("DrawLineV")]
internal unsafe ffi(c) fn void raylib_DrawLineV(Vector2 start, Vector2 end, Color tint);
```

On x86_64 System V this emits the same carrier forms Clang uses: `Vector2` as
`<2 x float>`, `Vector3` as `<2 x float>, float`, `Vector4`/`Rectangle` as two
`<2 x float>` carriers, and four-byte integer structs such as `Color` as one
integer carrier. Larger aggregates use the target ABI's indirect form.

Packed-field safety: a packed (misaligned) field reads/writes through unaligned
loads and stores; taking a **safe borrow** of one is a compile-time error;
taking a **raw pointer** to one is allowed in unsafe code and preserves the
misalignment. Inspect layout in `comptime` with `StructLayoutIsC<T>()`,
`StructHasPack<T>()`, `FieldHasExplicitOffset<T, I>()`, and
`FieldIsMisaligned<T, I>()`.

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
- Prefer `[LinkName("foreign_symbol")]` over a C rename shim when only the
  external symbol name differs.
- Declare underscore-leading C symbols directly, such as `__error`; bare `_`
  is still the discard token and pattern.
- Keep unsafe blocks as small as the raw or foreign operation.
- Convert raw failures into Stark status/result values quickly.
- Do not let foreign unwinding cross Stark frames.
- Keep Stark enums, closures, and ordinary source-facing structs off C ABI
  surfaces unless a purpose-built interop contract exists.
- Use `System.C` aliases for C integer/pointer-size types and `[StructLayout(C)]`
  for C aggregates; do not hand-pick widths or rely on default layout for ABI.
- Use `System.C` C strings (`OwnedCStr`, `CStr`, `CCharBuffer`) at `char*`
  boundaries; never pass Stark text to a `%s` varargs position.
- Use `public` for Stark callers and `export` only for binary visibility.
