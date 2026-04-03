# `System.Text`

`System.Text` provides the shared encoding enum and the first owned-text helper functions.

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
public fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
public fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);
```

## Behavior

- `AsciiView` projects an immutable `ascii` view from an owned `Ascii` buffer.
- `UnicodeView` projects an immutable `unicode` view from an owned `Unicode` buffer.
- `AsciiData` and `AsciiLength` expose the exact pointer and length of an immutable `ascii` view without requiring it to be NUL-terminated.
- `UnicodeData` and `UnicodeLength` do the same for immutable `unicode` views.
- `TryConcatAscii` and `TryConcatUnicode` write into caller-owned storage and return `false` instead of allocating when capacity is insufficient.

## Example

```stark
import System
module App

fn bool Build(rawmutptr<Ascii> destination) {
    return System.Text.TryConcatAscii(destination, "Stark", " IO");
}
```

## Current Status

- The shared encoding enum is implemented.
- Zero-copy owned-text view projection is implemented.
- Low-level pointer/length access for immutable text views is implemented.
- Caller-provided concat helpers are implemented.
- General-purpose allocation-backed text building and full encoding-conversion APIs are still future work.
