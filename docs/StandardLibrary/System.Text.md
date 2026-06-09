# `System.Text`

`System.Text` provides the shared encoding enum and the current owned-text helper functions.

Owned text builders follow Stark's memory-contract rules. Builder methods keep
the default non-overlap contract for disjoint append fast paths and spell
intentional source/destination overlap with `where overlap(self, source)`.
Conversions that append into caller-owned text storage must either preserve that
explicit overlap contract or copy through a snapshot before mutating the
destination.

## Surface

```stark
public enum Encoding
{
    Binary,
    UTF8,
    UTF16,
    UTF32,
}

public enum TextError
{
    InvalidFormat,
    Overflow,
}

public enum TextResult<T>
{
    Ok(T),
    Err(TextError),
}

public struct OwnedAscii
{
    finite ascii View(borrow OwnedAscii self);
    finite law i64 Length(borrow OwnedAscii self);
    fn void Truncate(mut borrow OwnedAscii self, u64[0 2 ** 63 - 1] length);
}

public struct OwnedUnicode
{
    finite unicode View(borrow OwnedUnicode self);
    finite law i64 Length(borrow OwnedUnicode self);
}

public struct OwnedUtf16
{
    finite law i64 Length(borrow OwnedUtf16 self);
    finite i16[] AsSlice(borrow OwnedUtf16 self);
}

public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law i64 AsciiLength(ascii source);
public finite law i64 UnicodeLength(unicode source);
public finite law bool StartsWith(ascii source, ascii prefix) where overlap(source, prefix);
public finite law bool StartsWith(unicode source, unicode prefix) where overlap(source, prefix);
public finite law bool EndsWith(ascii source, ascii suffix) where overlap(source, suffix);
public finite law bool EndsWith(unicode source, unicode suffix) where overlap(source, suffix);
public finite law bool Contains(ascii source, ascii needle) where overlap(source, needle);
public finite law bool Contains(unicode source, unicode needle) where overlap(source, needle);
public fn System.Memory.MemoryStatus FromAscii(out OwnedAscii destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAscii(out OwnedAscii destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicode(out OwnedUnicode destination, unicode source);
public fn System.Memory.MemoryStatus FromConstUnicode(out OwnedUnicode destination, const unicode source);
public fn System.Memory.MemoryStatus FromAsciiToUnicode(out OwnedUnicode destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToAscii(out OwnedAscii destination, unicode source);
public fn System.Memory.MemoryStatus FromAsciiToUtf16(out OwnedUtf16 destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUtf16(out OwnedUtf16 destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToUtf16(out OwnedUtf16 destination, unicode source);
public fn System.Memory.MemoryStatus FromUtf16ToUnicode(out OwnedUnicode destination, borrow OwnedUtf16 source);
public fn System.Memory.MemoryStatus FromUtf16ToAscii(out OwnedAscii destination, borrow OwnedUtf16 source);
public finite TextResult<bool> ParseBoolAscii(ascii source);
public finite TextResult<bool> ParseBoolUnicode(unicode source);
public finite TextResult<Encoding> ParseEncodingAscii(ascii source);
public finite TextResult<Encoding> ParseEncodingUnicode(unicode source);
public finite TextResult<TextError> ParseTextErrorAscii(ascii source);
public finite TextResult<TextError> ParseTextErrorUnicode(unicode source);
public finite TextResult<i8> ParseI8Ascii(ascii source);
public finite TextResult<i8> ParseI8Unicode(unicode source);
public finite TextResult<i16> ParseI16Ascii(ascii source);
public finite TextResult<i16> ParseI16Unicode(unicode source);
public finite TextResult<i24> ParseI24Ascii(ascii source);
public finite TextResult<i24> ParseI24Unicode(unicode source);
public finite TextResult<i32> ParseI32Ascii(ascii source);
public finite TextResult<i32> ParseI32Unicode(unicode source);
public finite TextResult<i48> ParseI48Ascii(ascii source);
public finite TextResult<i48> ParseI48Unicode(unicode source);
public finite TextResult<i64> ParseI64Ascii(ascii source);
public finite TextResult<i64> ParseI64Unicode(unicode source);
public finite TextResult<i96> ParseI96Ascii(ascii source);
public finite TextResult<i96> ParseI96Unicode(unicode source);
public finite TextResult<i128> ParseI128Ascii(ascii source);
public finite TextResult<i128> ParseI128Unicode(unicode source);
public finite TextResult<i192> ParseI192Ascii(ascii source);
public finite TextResult<i192> ParseI192Unicode(unicode source);
public finite TextResult<i256> ParseI256Ascii(ascii source);
public finite TextResult<i256> ParseI256Unicode(unicode source);
public finite TextResult<i384> ParseI384Ascii(ascii source);
public finite TextResult<i384> ParseI384Unicode(unicode source);
public finite TextResult<i512> ParseI512Ascii(ascii source);
public finite TextResult<i512> ParseI512Unicode(unicode source);
public finite TextResult<i768> ParseI768Ascii(ascii source);
public finite TextResult<i768> ParseI768Unicode(unicode source);
public finite TextResult<i1024> ParseI1024Ascii(ascii source);
public finite TextResult<i1024> ParseI1024Unicode(unicode source);
public finite TextResult<u8> ParseU8Ascii(ascii source);
public finite TextResult<u8> ParseU8Unicode(unicode source);
public finite TextResult<u16> ParseU16Ascii(ascii source);
public finite TextResult<u16> ParseU16Unicode(unicode source);
public finite TextResult<u24> ParseU24Ascii(ascii source);
public finite TextResult<u24> ParseU24Unicode(unicode source);
public finite TextResult<u32> ParseU32Ascii(ascii source);
public finite TextResult<u32> ParseU32Unicode(unicode source);
public finite TextResult<u48> ParseU48Ascii(ascii source);
public finite TextResult<u48> ParseU48Unicode(unicode source);
public finite TextResult<u64> ParseU64Ascii(ascii source);
public finite TextResult<u64> ParseU64Unicode(unicode source);
public finite TextResult<u96> ParseU96Ascii(ascii source);
public finite TextResult<u96> ParseU96Unicode(unicode source);
public finite TextResult<u128> ParseU128Ascii(ascii source);
public finite TextResult<u128> ParseU128Unicode(unicode source);
public finite TextResult<u192> ParseU192Ascii(ascii source);
public finite TextResult<u192> ParseU192Unicode(unicode source);
public finite TextResult<u256> ParseU256Ascii(ascii source);
public finite TextResult<u256> ParseU256Unicode(unicode source);
public finite TextResult<u384> ParseU384Ascii(ascii source);
public finite TextResult<u384> ParseU384Unicode(unicode source);
public finite TextResult<u512> ParseU512Ascii(ascii source);
public finite TextResult<u512> ParseU512Unicode(unicode source);
public finite TextResult<u768> ParseU768Ascii(ascii source);
public finite TextResult<u768> ParseU768Unicode(unicode source);
public finite TextResult<u1024> ParseU1024Ascii(ascii source);
public finite TextResult<u1024> ParseU1024Unicode(unicode source);
public finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
public finite bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
public finite bool TryFormatBoolAscii(rawmutptr<Ascii> destination, bool value);
public finite bool TryFormatEncodingAscii(rawmutptr<Ascii> destination, Encoding value);
public finite bool TryFormatTextErrorAscii(rawmutptr<Ascii> destination, TextError value);
public finite bool TryFormatI8Ascii(rawmutptr<Ascii> destination, i8 value);
public finite bool TryFormatI16Ascii(rawmutptr<Ascii> destination, i16 value);
public finite bool TryFormatI24Ascii(rawmutptr<Ascii> destination, i24 value);
public finite bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32 value);
public finite bool TryFormatI48Ascii(rawmutptr<Ascii> destination, i48 value);
public finite bool TryFormatI64Ascii(rawmutptr<Ascii> destination, i64 value);
public finite bool TryFormatI96Ascii(rawmutptr<Ascii> destination, i96 value);
public finite bool TryFormatI128Ascii(rawmutptr<Ascii> destination, i128 value);
public finite bool TryFormatI192Ascii(rawmutptr<Ascii> destination, i192 value);
public finite bool TryFormatI256Ascii(rawmutptr<Ascii> destination, i256 value);
public finite bool TryFormatI384Ascii(rawmutptr<Ascii> destination, i384 value);
public finite bool TryFormatI512Ascii(rawmutptr<Ascii> destination, i512 value);
public finite bool TryFormatI768Ascii(rawmutptr<Ascii> destination, i768 value);
public finite bool TryFormatI1024Ascii(rawmutptr<Ascii> destination, i1024 value);
public finite bool TryFormatU8Ascii(rawmutptr<Ascii> destination, u8 value);
public finite bool TryFormatU16Ascii(rawmutptr<Ascii> destination, u16 value);
public finite bool TryFormatU24Ascii(rawmutptr<Ascii> destination, u24 value);
public finite bool TryFormatU32Ascii(rawmutptr<Ascii> destination, u32 value);
public finite bool TryFormatU48Ascii(rawmutptr<Ascii> destination, u48 value);
public finite bool TryFormatU64Ascii(rawmutptr<Ascii> destination, u64 value);
public finite bool TryFormatU96Ascii(rawmutptr<Ascii> destination, u96 value);
public finite bool TryFormatU128Ascii(rawmutptr<Ascii> destination, u128 value);
public finite bool TryFormatU192Ascii(rawmutptr<Ascii> destination, u192 value);
public finite bool TryFormatU256Ascii(rawmutptr<Ascii> destination, u256 value);
public finite bool TryFormatU384Ascii(rawmutptr<Ascii> destination, u384 value);
public finite bool TryFormatU512Ascii(rawmutptr<Ascii> destination, u512 value);
public finite bool TryFormatU768Ascii(rawmutptr<Ascii> destination, u768 value);
public finite bool TryFormatU1024Ascii(rawmutptr<Ascii> destination, u1024 value);
public finite bool TryFormatF64Ascii(rawmutptr<Ascii> destination, f64 value);
public finite bool TryFormatF32Ascii(rawmutptr<Ascii> destination, f32 value);
public fn bool TryFormatBoolUnicode(rawmutptr<Unicode> destination, bool value);
public finite bool TryFormatEncodingUnicode(rawmutptr<Unicode> destination, Encoding value);
public finite bool TryFormatTextErrorUnicode(rawmutptr<Unicode> destination, TextError value);
public fn bool TryFormatI8Unicode(rawmutptr<Unicode> destination, i8 value);
public fn bool TryFormatI16Unicode(rawmutptr<Unicode> destination, i16 value);
public fn bool TryFormatI24Unicode(rawmutptr<Unicode> destination, i24 value);
public fn bool TryFormatI32Unicode(rawmutptr<Unicode> destination, i32 value);
public fn bool TryFormatI48Unicode(rawmutptr<Unicode> destination, i48 value);
public fn bool TryFormatI64Unicode(rawmutptr<Unicode> destination, i64 value);
public fn bool TryFormatI96Unicode(rawmutptr<Unicode> destination, i96 value);
public fn bool TryFormatI128Unicode(rawmutptr<Unicode> destination, i128 value);
public fn bool TryFormatI192Unicode(rawmutptr<Unicode> destination, i192 value);
public fn bool TryFormatI256Unicode(rawmutptr<Unicode> destination, i256 value);
public fn bool TryFormatI384Unicode(rawmutptr<Unicode> destination, i384 value);
public fn bool TryFormatI512Unicode(rawmutptr<Unicode> destination, i512 value);
public fn bool TryFormatI768Unicode(rawmutptr<Unicode> destination, i768 value);
public fn bool TryFormatI1024Unicode(rawmutptr<Unicode> destination, i1024 value);
public fn bool TryFormatU8Unicode(rawmutptr<Unicode> destination, u8 value);
public fn bool TryFormatU16Unicode(rawmutptr<Unicode> destination, u16 value);
public fn bool TryFormatU24Unicode(rawmutptr<Unicode> destination, u24 value);
public fn bool TryFormatU32Unicode(rawmutptr<Unicode> destination, u32 value);
public fn bool TryFormatU48Unicode(rawmutptr<Unicode> destination, u48 value);
public fn bool TryFormatU64Unicode(rawmutptr<Unicode> destination, u64 value);
public fn bool TryFormatU96Unicode(rawmutptr<Unicode> destination, u96 value);
public fn bool TryFormatU128Unicode(rawmutptr<Unicode> destination, u128 value);
public fn bool TryFormatU192Unicode(rawmutptr<Unicode> destination, u192 value);
public fn bool TryFormatU256Unicode(rawmutptr<Unicode> destination, u256 value);
public fn bool TryFormatU384Unicode(rawmutptr<Unicode> destination, u384 value);
public fn bool TryFormatU512Unicode(rawmutptr<Unicode> destination, u512 value);
public fn bool TryFormatU768Unicode(rawmutptr<Unicode> destination, u768 value);
public fn bool TryFormatU1024Unicode(rawmutptr<Unicode> destination, u1024 value);
public fn bool TryFormatF64Unicode(rawmutptr<Unicode> destination, f64 value);
public fn bool TryFormatF32Unicode(rawmutptr<Unicode> destination, f32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(bool value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i8 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i16 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i24 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i48 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i96 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i128 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i192 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i256 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i384 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i512 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i768 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i1024 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u8 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u16 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u24 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u48 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u96 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u128 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u192 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u256 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u384 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u512 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u768 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u1024 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(Encoding value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(TextError value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(bool value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i8 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i16 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i24 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i48 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i96 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i128 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i192 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i256 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i384 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i512 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i768 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i1024 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u8 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u16 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u24 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u48 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u96 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u128 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u192 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u256 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u384 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u512 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u768 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u1024 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(Encoding value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(TextError value);
```

## Behavior

- `AsciiView` projects an immutable `ascii` view from an owned `Ascii` buffer.
- `UnicodeView` projects an immutable `unicode` view from an owned `Unicode` buffer.
- `OwnedAscii` and `OwnedUnicode` are allocation-backed owned text wrappers returned by convenience APIs. Dropping the wrapper releases the backing allocation.
- `OwnedAscii.View`, `OwnedUnicode.View`, and their `Length` helpers expose read-only text without transferring ownership.
- `OwnedAscii.Truncate` shortens an owned ASCII buffer in place without allocation; it is useful for reusable path and line buffers.
- `AsciiLength` and `UnicodeLength` expose immutable view lengths without exposing raw data pointers to user code.
- `ParseBoolAscii` and `ParseBoolUnicode` parse exact lowercase `true` or `false` and return `TextResult<bool>` instead of throwing.
- `ParseEncodingAscii`, `ParseEncodingUnicode`, `ParseTextErrorAscii`, and `ParseTextErrorUnicode` parse exact enum case names for the `System.Text` enum types.
- The implemented integer parse APIs for signed and unsigned widths from 8 bits through 1024 bits parse exact base-10 text from `ascii` or `unicode` and return `TextResult<T>` with `TextError.InvalidFormat` or `TextError.Overflow` on failure.
- `FromAscii`, `FromUnicode`, `FromAsciiToUnicode`, `FromUnicodeToAscii`, `FromAsciiToUtf16`, `FromUnicodeToUtf16`, `FromUtf16ToUnicode`, and `FromUtf16ToAscii` use owned/dynamic storage and return `MemoryStatus`.
- `TryConcatAscii` and `TryConcatUnicode` write into caller-owned storage and return `false` instead of allocating when capacity is insufficient.
- Source code can also use fixed-capacity stack text concatenation, such as `stack Ascii combined[4096] = left + right;`. That syntax lowers to the same `TryConcat*` copy loops, but traps if the selected capacity is too small. Call `TryConcat*` directly when failure must be handled as a value.
- Source code can use fixed-capacity stack interpolation, such as `stack Ascii label[64] = $"Score: {score}";`. Runtime holes use the fixed-buffer `TryFormat*` APIs and then append through `TryConcat*`, so the destination capacity stays visible in source.
- `TryFormatBoolAscii` writes `true` or `false` into caller-owned `Ascii` storage.
- `TryFormatEncodingAscii`, `TryFormatEncodingUnicode`, `TryFormatTextErrorAscii`, and `TryFormatTextErrorUnicode` write exact enum case names for the `System.Text` enum types.
- The fixed-width integer `TryFormat*Ascii` forms for `i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`, and the matching unsigned widths write base-10, locale-independent integer representations into caller-owned `Ascii` storage.
- `TryFormatBoolUnicode` and the matching integer `Unicode` forms write the same representations into caller-owned `Unicode` storage.
- `TryFormatF64Ascii`, `TryFormatF32Ascii`, `TryFormatF64Unicode`, and `TryFormatF32Unicode` are the first no-allocation float formatting slice. They write fixed-six-fractional-digit decimal text into caller-owned storage for finite values in the supported range and return `false` for unsupported values.
- `ToAscii` and `ToUnicode` currently support `bool`, all signed and unsigned integer widths from 8 bits through 1024 bits, `f64`, `f32`, `Encoding`, and `TextError` as both `System.Text.ToAscii(value)` / `System.Text.ToUnicode(value)` and method-style `value.ToAscii()` / `value.ToUnicode()` calls. They allocate an owned text wrapper sized for the selected type and return `System.Memory.MemoryResult<T>` so out-of-memory and too-large failures remain explicit.
- Invalid UTF-8 sequences, invalid UTF-16 sequences, and invalid Unicode scalar values are normalized to U+FFFD during conversion.

## Formatting Defaults

The implemented bool and integer formatting APIs use these defaults:

- bool values format as lowercase `true` or `false`
- integer values format in base 10 using only ASCII digits
- non-negative signed integers and all unsigned integers have no leading `+`
- negative signed integers use one leading `-`, including the minimum value for each signed width
- zero formats as `0`
- there are no digit separators, prefixes, suffixes, padding, or locale-specific characters
- `Ascii` destinations receive UTF-8 bytes and `Unicode` destinations receive the same scalar values as UTF-32 code units

The first floating-point formatting APIs use fixed-six-fractional-digit decimal
text with `.` as the separator, such as `-12.500000`. They currently reject
NaN, infinity, and values outside the exact integer range used by the initial
implementation. The planned complete floating-point formatting APIs should use
shortest round-trip decimal text for the exact float width being formatted, with
lowercase special values such as `nan`, `infinity`, and `-infinity`. Negative
zero should format as `-0.0`.

The implemented enum formatting/parsing APIs for `System.Text.Encoding` and
`System.Text.TextError` use exact declared case names. Future enum cases that
intentionally preserve unknown or payload data should expose an explicit numeric
or payload formatting path instead of guessing a display form.

The implemented bool parsing APIs intentionally accept only the exact lowercase
output of the matching formatters. They do not trim whitespace, accept culture
variants, or accept uppercase aliases.

The implemented integer parsing APIs accept only base-10 digits, with one
leading `-` allowed for signed values. They do not accept leading `+`,
whitespace, separators, prefixes, suffixes, or locale-specific digits. The
current integer parsing surface covers signed and unsigned widths through 1024
bits.

## Example

```stark
import System.Text
module App

fn System.Memory.MemoryStatus Build(out System.Text.OwnedUnicode destination)
{
    return System.Text.FromAsciiToUnicode(destination, "caf\u00E9");
}
```

## Current Status

- The shared encoding enum is implemented.
- Zero-copy owned-text view projection is implemented.
- Internal low-level pointer/length access for immutable text views is implemented for standard-library and platform boundaries.
- Owned UTF-8, UTF-16LE, and UTF-32 conversion is implemented.
- Caller-provided concat helpers are implemented.
- Compile-time text constant concatenation with `+` is implemented and folds to
  one ordinary text constant.
- Literal-prefix runtime text concatenation such as `"Score: " + score.ToAscii()`
  is implemented and returns an owned-text `System.Memory.MemoryResult<T>`.
- Caller-provided bool and fixed-width integer formatting to `Ascii` and `Unicode` is implemented.
- Caller-provided fixed-six float formatting to `Ascii` and `Unicode` is implemented for the first bounded no-allocation slice.
- Allocation-visible owned text convenience APIs are implemented for `bool`, all signed and unsigned integer widths from 8 bits through 1024 bits, `f64`, `f32`, `Encoding`, and `TextError`.
- Exact lowercase bool parsing from `ascii` and `unicode` is implemented.
- Exact base-10 integer parsing from `ascii` and `unicode` is implemented through 1024-bit signed and unsigned widths.
- General-purpose allocation-backed text building remains future work.

## Convenience Surface

User-friendly text construction sits on top of the explicit runtime helpers:

- C#-style interpolated text such as `stack Ascii label[64] = $"Score: {score}"`
- broader runtime text concatenation beyond literal-prefix owned-text results
- value-to-text formatting through `System.Text.ToAscii(value)`, `value.ToAscii()`, `System.Text.ToUnicode(value)`, `value.ToUnicode()`, and fixed-buffer APIs
- text-to-value parsing APIs that return result/status values instead of throwing

These conveniences should preserve Stark's no-exception model and keep
allocation or capacity requirements visible in the API surface.
