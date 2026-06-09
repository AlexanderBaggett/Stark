# `System.C`

`System.C` contains the standard-library interop vocabulary for C-facing code:
target-mapped C primitive aliases, null-terminated string views and owners, and
helpers for copying or disposing foreign-owned strings.

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

## Example

```stark
import System.C
module Example

export fn CStringResult<OwnedCStr> BuildName(ascii name)
{
    return FromAscii(name);
}
```
