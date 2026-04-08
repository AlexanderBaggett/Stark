# `System.Text`

`System.Text` provides the shared encoding enum and the current owned-text helper functions.

## Surface

```stark
public enum Encoding {
    Binary,
    UTF8,
    UTF16,
    UTF32,
}

public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law rawptr<i8> AsciiData(ascii source);
public finite law i64 AsciiLength(ascii source);
public finite law rawptr<i32> UnicodeData(unicode source);
public finite law i64 UnicodeLength(unicode source);
public fn bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);
public fn bool TryConvertAsciiToUtf16(rawmutptr<i16> destination, i64 capacity, ascii source, rawmutptr<i64> writtenLength);
public fn bool TryConvertUtf16ToUnicode(rawmutptr<Unicode> destination, rawptr<i16> source, i64 sourceLength);
public fn bool TryConvertUnicodeToAscii(rawmutptr<Ascii> destination, unicode source);
public fn bool TryConvertUnicodeToUtf16(rawmutptr<i16> destination, i64 capacity, unicode source, rawmutptr<i64> writtenLength);
public fn bool TryConvertUtf16ToAscii(rawmutptr<Ascii> destination, rawptr<i16> source, i64 sourceLength);
public fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
public fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
```

## Behavior

- `AsciiView` projects an immutable `ascii` view from an owned `Ascii` buffer.
- `UnicodeView` projects an immutable `unicode` view from an owned `Unicode` buffer.
- `AsciiData` and `AsciiLength` expose the exact pointer and length of an immutable `ascii` view without requiring it to be NUL-terminated.
- `UnicodeData` and `UnicodeLength` do the same for immutable `unicode` views.
- `TryConvertAsciiToUnicode` decodes UTF-8 text into caller-owned `Unicode` storage.
- `TryConvertAsciiToUtf16` decodes UTF-8 text into caller-owned UTF-16 code-unit storage and writes the unit count through `writtenLength`.
- `TryConvertUtf16ToUnicode` decodes caller-provided UTF-16 code units into owned `Unicode` storage.
- `TryConvertUnicodeToAscii` encodes caller-owned `Unicode` text into UTF-8 `Ascii` storage.
- `TryConvertUnicodeToUtf16` encodes caller-owned `Unicode` text into caller-owned UTF-16 code-unit storage and writes the unit count through `writtenLength`.
- `TryConvertUtf16ToAscii` encodes caller-provided UTF-16 code units into UTF-8 `Ascii` storage.
- `TryConcatAscii` and `TryConcatUnicode` write into caller-owned storage and return `false` instead of allocating when capacity is insufficient.
- Invalid UTF-8 sequences, invalid UTF-16 sequences, and invalid Unicode scalar values are normalized to U+FFFD during conversion.

## Example

```stark
import System
module App

fn bool Build(rawmutptr<Unicode> destination) {
    return System.Text.TryConvertAsciiToUnicode(destination, "caf\u00E9");
}
```

## Current Status

- The shared encoding enum is implemented.
- Zero-copy owned-text view projection is implemented.
- Low-level pointer/length access for immutable text views is implemented.
- Explicit caller-owned UTF-8, UTF-16LE, and UTF-32 conversion is implemented.
- Caller-provided concat helpers are implemented.
- General-purpose allocation-backed text building remains future work.
