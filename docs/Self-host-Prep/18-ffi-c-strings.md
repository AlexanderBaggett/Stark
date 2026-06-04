# Phase 18 - FFI Null-Terminated C Strings

Status: **design direction locked - no implementation yet.**

Null-terminated C strings live in `System.C`. They are not ordinary Stark
`ascii` or `unicode` values. A C `char*` is a foreign byte pointer with a
terminating zero byte, no carried length, nullable raw-pointer behavior at the
ABI boundary, target-dependent `c_char` signedness, and separate ownership
rules.

Stark should expose C string interop through explicit helper types and
conversions, not implicit coercions.

## 1. Goals

- Model C `char*` / null-terminated strings distinctly from Stark text views.
- Provide borrowed, owned, and mutable-output-buffer forms.
- Require explicit conversion from Stark text to C string storage.
- Require bounded validation before converting raw C strings to Stark text.
- Make allocation ownership visible.
- Keep all C string helpers under `System.C`.
- Avoid implicit `ascii`/`unicode` to `char*` conversions, including varargs.

## 2. Core Types

The initial surface lives in `System.C`:

```stark
module System.C

public struct CStr
{
    internal rawptr<c_char> Data;
    internal u64[0 max] Length;
}

public struct OwnedCStr
{
    internal rawmutptr<c_char> Data;
    internal u64[0 max] Length;
    internal u64[0 max] Capacity;
    internal System.Memory.Allocator Allocator;
}

public struct CCharBuffer
{
    internal rawmutptr<c_char> Data;
    internal u64[0 max] Capacity;
    internal System.Memory.Allocator Allocator;
}

public enum CStringError
{
    NullPointer,
    MissingTerminator,
    InteriorNull,
    InvalidUtf8,
    TooLarge,
    Memory from System.Memory.MemoryError,
    Text from System.Text.TextError,
}
```

The field list is conceptual and may use an internal allocation record in the
implementation. The required public contract is:

- `CStr` is a non-owning validated C string view.
- `OwnedCStr` is Stark-owned storage that is always null-terminated.
- `CCharBuffer` is mutable caller-owned storage for C APIs that write into a
  provided buffer.
- `CStringError` represents validation, encoding, size, and allocation failure.

The public fields should remain hidden. Construction must go through checked
functions or explicitly unsafe raw-pointer functions so invalid C string states
are not ordinary user-constructible values.

Default construction must also be defined deliberately. `CStr` and `OwnedCStr`
should default to a valid empty C string, either backed by a static read-only
terminator or by an implementation-defined empty sentinel. `CCharBuffer` may
default to a zero-capacity closed buffer; safe methods on that value report
ordinary `CStringError` failures until storage is allocated.

## 3. Borrowed C Strings

`CStr` represents a non-owning C string view:

- `Data` is non-null.
- `Length` excludes the trailing zero byte.
- `Data[Length]` is zero.
- `Data[0..Length]` contains no interior zero byte.
- The pointed-to storage is not owned by the `CStr`.

Raw construction from foreign pointers is unsafe because Stark cannot prove the
pointer's lifetime or validity:

```stark
public unsafe fn System.Result<CStr, CStringError> TryFromRawBounded(
    rawptr<c_char> data,
    u64[0 max] maxLength);

public unsafe fn CStr FromRawUnchecked(
    rawptr<c_char> data,
    u64[0 max] length);
```

`TryFromRawBounded` scans at most `maxLength` bytes looking for the terminator.
If it sees `null`, reaches `maxLength` without a terminator, or cannot represent
the length, it returns an error. It does not take ownership of the memory.

`FromRawUnchecked` is only for call sites that already have an external proof
that the pointer is non-null, valid for `length + 1` bytes, and terminated at
`length`. It must remain unsafe.

## 4. Owned C Strings

`OwnedCStr` owns Stark-allocated null-terminated storage. It is the normal way
to pass Stark text to a C API that expects `const char*`.

```stark
public fn System.Result<OwnedCStr, CStringError> FromAscii(ascii text);
public fn System.Result<OwnedCStr, CStringError> FromUnicodeUtf8(unicode text);

public unsafe finite law rawptr<c_char> Data(borrow OwnedCStr self);
public finite law u64[0 max] Length(borrow OwnedCStr self);
public unsafe finite law CStr View(borrow OwnedCStr self);
```

Rules:

- Conversion allocates `Length + 1` bytes.
- The final byte is always zero.
- Interior zero bytes in the Stark source text are rejected with
  `CStringError.InteriorNull`.
- `Length` excludes the terminator.
- The storage is freed by `OwnedCStr`'s destructor.
- `Data()` returns a raw pointer for FFI calls and is unsafe to make the raw
  boundary visible.
- `View()` returns a non-owning view into the owned storage. It remains unsafe
  until Stark can tie the view lifetime to the borrowed owner in the type
  system.

Example:

```stark
public unsafe ffi(c) fn System.C.c_int puts(rawptr<System.C.c_char> text);

fn System.Result<System.C.c_int, System.C.CStringError> Puts(ascii text)
{
    stack System.C.OwnedCStr cText = try System.C.FromAscii(text);
    unsafe
    {
        return System.Result<System.C.c_int, System.C.CStringError>.Ok(
            puts(cText.Data()));
    }
}
```

## 5. Converting C Strings To Stark Text

C strings are bytes. They are not automatically UTF-8 and not automatically
Stark `ascii`.

```stark
public fn System.Result<System.Text.OwnedAscii, CStringError> ToAscii(
    borrow CStr text);

public fn System.Result<System.Text.OwnedUnicode, CStringError> ToUnicodeUtf8(
    borrow CStr text);
```

Rules:

- The input must already be a validated `CStr`.
- `ToAscii` validates that bytes are valid UTF-8 before creating Stark text.
- Invalid UTF-8 returns `CStringError.InvalidUtf8`.
- The conversion allocates owned Stark text and reports allocation failure as a
  value.
- No implicit conversion exists from `rawptr<c_char>` to `ascii` or `unicode`.

For legacy APIs that use platform-specific encodings, add explicitly named
helpers later. Do not make the default C string conversion locale-dependent.

## 6. Mutable Output Buffers

Some C APIs write into caller-provided `char*` buffers. Use `CCharBuffer` for
that pattern.

```stark
public fn System.Result<CCharBuffer, CStringError> NewCCharBuffer(
    u64[1 max] capacity);

public unsafe finite law rawmutptr<c_char> Data(mut borrow CCharBuffer self);
public finite law u64[0 max] Capacity(borrow CCharBuffer self);

public unsafe fn System.Result<CStr, CStringError> TryAsCStr(
    borrow CCharBuffer self);
```

`Capacity` includes space for the terminator. `TryAsCStr` scans the buffer for a
terminator and returns a borrowed `CStr` view if the buffer contains one. It is
unsafe because the returned view is backed by the buffer and must not outlive it.

Example:

```stark
public unsafe ffi(c) fn rawmutptr<System.C.c_char> getcwd(
    rawmutptr<System.C.c_char> buffer,
    System.C.c_size_t size);

fn System.Result<System.Text.OwnedAscii, System.C.CStringError> CurrentDirectory()
{
    stack mut System.C.CCharBuffer buffer = try System.C.NewCCharBuffer(4096);
    unsafe
    {
        if (getcwd(buffer.Data(), (System.C.c_size_t)buffer.Capacity()) == null)
        {
            return System.Result<System.Text.OwnedAscii, System.C.CStringError>.Err(
                System.C.CStringError.NullPointer);
        }
    }

    unsafe
    {
        stack System.C.CStr view = try buffer.TryAsCStr();
        return System.C.ToAscii(view);
    }
}
```

## 7. External Ownership

`OwnedCStr` is only for memory allocated by Stark's allocator. It must not be
used for pointers returned by foreign allocators.

For C APIs that return owned strings, create wrapper-specific owner types whose
destructor calls the correct foreign free function:

```stark
public unsafe ffi(c) fn rawmutptr<System.C.c_char> strdup(
    rawptr<System.C.c_char> text);

public unsafe ffi(c) fn void free(rawmutptr<System.C.c_void> pointer);

public struct LibcOwnedCStr
{
    internal rawmutptr<System.C.c_char> Data;

    drop
    {
        unsafe
        {
            free((rawmutptr<System.C.c_void>)self.Data);
        }
    }
}
```

This keeps ownership explicit: the type name and destructor say which allocator
will release the memory.

## 8. FFI And Varargs

FFI declarations continue to use raw pointers:

```stark
public unsafe ffi(c) fn System.C.c_int puts(rawptr<System.C.c_char> text);
public unsafe ffi(c) varargs fn System.C.c_int printf(rawptr<System.C.c_char> format);
```

Safe wrappers convert Stark text to `OwnedCStr` before crossing the boundary.

No special case should allow this:

```stark
printf("name: %s\n", name); // error for %s-style use
```

Instead, the call must pass the raw pointer from a C string value:

```stark
stack System.C.OwnedCStr format = try System.C.FromAscii("name: %s\n");
stack System.C.OwnedCStr cName = try System.C.FromAscii(name);
unsafe
{
    printf(format.Data(), cName.Data());
}
```

The FFI docs should be updated so `%s` examples use `System.C` C strings rather
than plain `ascii`.

## 9. Relationship To `c_char`

C string storage uses `System.C.c_char` because it mirrors C headers that spell
`char*`.

Rules:

- Use `rawptr<System.C.c_char>` for C `const char*`.
- Use `rawmutptr<System.C.c_char>` for mutable C `char*`.
- Use `System.C.c_uchar` or `u8[0 max]` for byte buffers that are not text.
- Do not infer text encoding from `c_char` signedness.

`c_char` signedness is a C ABI fact, not an encoding fact.

## 10. Diagnostics

Recommended diagnostics:

| Condition |
|---|
| Implicit conversion from `ascii` or `unicode` to `rawptr<c_char>`. |
| Implicit conversion from `rawptr<c_char>` to `ascii` or `unicode`. |
| `FromAscii` source contains an interior zero byte. |
| `TryFromRawBounded` receives `null`. |
| `TryFromRawBounded` reaches `maxLength` without a terminator. |
| `ToAscii` sees invalid UTF-8. |
| `OwnedCStr` attempted to adopt memory from a foreign allocator. |
| `TryAsCStr` finds no terminator within capacity. |
| C `%s` varargs call receives ordinary Stark text instead of C string data. |

## 11. Work Items

- [ ] Add `System.C.CStringError`.
- [ ] Add `System.C.CStr` with hidden fields and checked/unsafe constructors.
- [ ] Add `System.C.OwnedCStr` with allocator-backed null-terminated storage
      and destructor.
- [ ] Add `System.C.CCharBuffer` for mutable output buffers.
- [ ] Implement `FromAscii` and `FromUnicodeUtf8`.
- [ ] Implement `ToAscii` and `ToUnicodeUtf8` with UTF-8 validation.
- [ ] Implement bounded scanning for raw C string validation.
- [ ] Add diagnostics that reject implicit Stark text / C string conversions.
- [ ] Update FFI varargs validation and docs so `%s` uses `System.C` C strings.
- [ ] Add tests for interior zeros, missing terminators, bounded scans, invalid
      UTF-8, allocation failure paths, output buffers, and destructor behavior.
- [ ] Update user-facing FFI docs and Stark language skill references.
