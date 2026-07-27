# `System.C`

`System.C` contains the standard-library interop vocabulary for C-facing code:
target-mapped C primitive aliases, null-terminated string views and owners, and
helpers for copying or disposing foreign-owned strings.

## C Primitive Aliases

The compiler provides aliases for C's target-dependent integer and pointer-size
spellings so FFI declarations mirror C headers instead of hard-coding a width.
Each alias resolves to an ordinary Stark sized primitive for the active target
before layout, type checking, and codegen; they do not add a second integer type
system, hidden conversions, or distinct ABI identities.

| Alias | C spelling | Alias | C spelling |
|---|---|---|---|
| `c_char` | `char` | `c_ulong` | `unsigned long` |
| `c_schar` | `signed char` | `c_longlong` | `long long` |
| `c_uchar` | `unsigned char` | `c_ulonglong` | `unsigned long long` |
| `c_short` | `short` | `c_size_t` | `size_t` |
| `c_ushort` | `unsigned short` | `c_ptrdiff_t` | `ptrdiff_t` |
| `c_int` | `int` | `c_void` | `void` (raw pointee only) |
| `c_uint` | `unsigned int` | `VaList` | `va_list` (ABI parameter carrier only) |
| `c_long` | `long` | | |

Resolution depends on the target's C data model. For example `c_int` is always
`i32[min max]`, while `c_long` is `i32[min max]` on ILP32/LLP64 targets and
`i64[min max]` on LP64 targets; `c_size_t` is `u64[0 max]` on 64-bit targets and
`u32[0 max]` on 32-bit targets.

* `c_char` follows the target's plain-`char` signedness, exposed as the
  compile-time bool `System.C.c_char_is_signed`. `c_schar` is always signed
  8-bit and `c_uchar` is always unsigned 8-bit. Use `c_char` only where the C
  header says `char`; use `c_uchar` or `u8[0 max]` for non-text byte buffers.
* `c_void` is an incomplete foreign pointee. It is valid only as the direct
  pointee of `rawptr<c_void>` / `rawmutptr<c_void>`; using it as a value, field,
  array element, or function return is a compile-time error. C functions
  returning `void` use Stark's ordinary `void` return type.
* `VaList` models C `va_list` for fixed-arity C APIs and callbacks that receive
  an existing varargs list. It is valid only as an unsafe `ffi(c)`-compatible
  function parameter, an `ffi(c)`-compatible function-pointer parameter, or the
  direct pointee of `rawptr<VaList>` / `rawmutptr<VaList>`. Stark code cannot
  construct a `VaList`, store it, return it, or define C-style variadic bodies.
* No implicit conversion exists between `rawptr<c_char>` and Stark `ascii`, or
  between `rawptr<c_void>` and a typed raw pointer. To leave the platform-width
  surface, bind the alias value into a Stark-typed local or cast it to a Stark
  width (the C-style cast parser does not accept a qualified alias as a cast
  target, so `(System.C.c_size_t)value` does not parse).

```stark
public unsafe ffi(c) fn c_int close(c_int fd);
public unsafe ffi(c) fn c_size_t strlen(rawptr<c_char> text);
public unsafe ffi(c) fn rawmutptr<c_void> malloc(c_size_t bytes);
public unsafe ffi(c) fn void free(rawmutptr<c_void> ptr);
public unsafe ffi(c) fn rawmutptr<c_char> vformat(rawptr<c_char> format, VaList args);
```

## Public Types

- `CStringError`: null pointer, missing terminator, interior null, invalid UTF-8,
  too large, memory, and text conversion errors.
- `CStringResult<T>`: `[Ok]` / `[Err]` result wrapper for C string operations.
- `CStr`: borrowed null-terminated C string view.
- `OwnedCStr`: owned null-terminated C string storage.
- `CCharBuffer`: mutable output buffer for C APIs that write strings.
- `ForeignOwnedCStr`: owned-by-foreign-code C string pointer that must be
  copied or disposed with a caller-provided disposer.

## Public Alias

```stark
public alias CStringDisposer =
    fnptr<unsafe ffi(c) fn void(rawmutptr<System.C.c_char>)>;
```

The C primitive aliases such as `c_char`, `c_int`, and `c_size_t` are target
mapped by the compiler and are intended for FFI declarations.

## Public Surface

The module provides helpers for:

- bounded construction of borrowed `CStr` views from raw pointers
- unchecked `CStr` construction when the caller already owns the invariant
- wrapping and disposing foreign-owned C strings
- converting Stark `ascii` and UTF-8 `unicode` text into owned C strings
- copying C strings back into owned Stark text
- allocating mutable C character output buffers

C strings are distinct from Stark text. `ascii` / `unicode` are length-carrying
views; a C `char*` is a foreign null-terminated byte pointer. There is no
implicit conversion either way, including at `%s` varargs positions: a `%s`
argument must be `rawptr<c_char>` or `rawmutptr<c_char>`, so convert Stark text
with `FromAscii` / `FromUnicodeUtf8` and pass `OwnedCStr.Data()`.

## Example

Convert Stark text to a C string and call a C function:

```stark
import System.C
module Example

public unsafe ffi(c) fn c_int puts(rawptr<c_char> text);

public fn CStringResult<c_int> Puts(ascii text)
{
    stack OwnedCStr cText = try FromAscii(text);
    unsafe
    {
        return CStringResult<c_int>.Ok(puts(cText.Data()));
    }
}
```

Build an owned C string from Stark text:

```stark
import System.C
module BuildExample

public fn CStringResult<OwnedCStr> BuildName(ascii name)
{
    return FromAscii(name);
}
```
