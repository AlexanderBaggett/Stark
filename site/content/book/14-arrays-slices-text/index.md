+++
title = "14. Arrays, Slices, Dynamic Storage, Text, and Views"
weight = 140
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/13-enums-patterns/"
next = "/book/15-modules-visibility-packages/"
aliases = ["/book/13-arrays-slices-text/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/standard-library/System.Text/"

[[example_refs]]
title = "Type System Examples"
href = "/reference/examples/type-system/TypeSystem.stark"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Arrays, Slices, Dynamic Storage, Text, and Views

Arrays, slices, dynamic storage, and text all deal with contiguous data, but
they do not have the same ownership story. Stark keeps that distinction visible.

{{< stark-sample "samples/arrays-text-views.stark" >}}

## Step 1: Start With Fixed Storage

`T[N]` is a fixed array with `N` elements:

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};
```

The backing storage is real storage owned by the array value. Indexing uses the
same postfix form as other C-family languages:

```stark
values[0]
values[1]
values[2]
```

Omitted trailing fixed-array elements are zero-initialized when the target
array size is known.

You can read and write elements when the array binding is mutable:

```stark
fn i32[min max] UpdateSecond()
{
    stack mut i32[min max][3] values =
    {
        1, 2, 3
    };
    values[1] = 20;
    return values[0] + values[1] + values[2];
}
```

Use a fixed array when the owner and element count are part of the local
design:

```stark
stack u8[0 max][4] rgba =
{
    255, 128, 64, 255
};
stack bool[3] flags =
{
    true, false, true
};
```

## Step 2: Borrow Views Instead Of Inventing Backing Storage

`T[]` is a slice view. It refers to backing storage created elsewhere. It does
not allocate and it does not secretly create an array.

That means this intentionally invalid example is rejected:

{{< stark-sample "rejected/slice-literal-hidden-storage.stark" >}}

The fix is to write the backing storage first, then form a view:

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};
stack i32[min max][] view = values;
```

That is the Stark rule in miniature: if storage exists, make it visible.

Use a full slice when the callee needs all initialized elements:

```stark
fn i32[min max] First(i32[min max][] values)
{
    return values[0];
}

fn i32[min max] UseFirst()
{
    stack i32[min max][3] values =
    {
        10, 20, 30
    };
    return First(values);
}
```

Use a range slice when the callee should see only part of the backing storage:

```stark
fn i32[min max] ReadMiddle()
{
    stack i32[min max][4] values =
    {
        1, 2, 3, 4
    };
    stack i32[min max][] middle = values[1, 2];
    return middle[0] + middle[1];
}
```

Use `mut` on the view only when the callee should write through it:

```stark
fn void SetFirst(mut borrow i32[min max][] values, i32[min max] value)
{
    values[0] = value;
    return;
}
```

## Step 3: Use `dynamic T` When Capacity Must Grow

`dynamic T` is owned, growable storage for elements of `T`. It has a visible
`Length`, a visible `Capacity`, and spare slots that can be initialized without
making callers use raw pointers.

{{< stark-sample "samples/dynamic-storage.stark" >}}

The important operations are direct:

- `Reserve(additional)` preserves initialized elements and makes room for more
- `TryReserve(additional)` does the same growth work but returns `false` instead
  of trapping when capacity or allocation fails
- `init items[index] = value` constructs an element in spare storage
- `items[0, items.Length]` creates a normal initialized slice view
- `MoveLast()` moves the tail element out and decrements `Length`
- `MoveAt(index)` moves one initialized element out, shifts the later suffix
  left, and decrements `Length`

This is the safe shape for vector-like storage. The owner, initialized prefix,
spare-capacity writes, and element type are all visible in the source.

The smallest manual push operation looks like this:

```stark
fn bool Push(mut borrow dynamic i32[min max] items, i32[min max] value)
{
    if (!items.TryReserve(1))
    {
        return false;
    }

    init items[items.Length] = value;
    return true;
}
```

Use `MoveLast()` when the last initialized element should be removed:

```stark
fn bool TryPop(mut borrow dynamic i32[min max] items, out i32[min max] value)
{
    if (items.Length == 0)
    {
        value = 0;
        return false;
    }

    value = items.MoveLast();
    return true;
}
```

Use `MoveAt(index)` when the element is not at the tail and the remaining
initialized suffix should close the gap:

```stark
fn i32[min max] RemoveFirst(mut borrow dynamic i32[min max] items)
{
    return items.MoveAt(0);
}
```

The same storage shape works for collection backing stores, byte builders, and
path/text buffers. The public surface can stay slice-shaped while the
owner keeps spare capacity private:

{{< stark-sample "samples/dynamic-storage-patterns.stark" >}}
Reading a dynamic slot is only safe when the slot is part of the initialized
prefix. The compiler proves that at compile time, with no runtime bounds check,
in two ways.

The first is a comparison guard. A strict `index < storage.Length` check (or the
mirrored `storage.Length > index`) proves reads of that slot on the path the
guard dominates — in any function, not only inside the owning type:

```stark
fn i32[min max] ReadOrZero(borrow dynamic i32[min max] items, u64[0 max] index)
{
    if (index >= items.Length)
    {
        return 0;
    }

    return items[index];
}
```

The early-return guard above leaves the in-range fact on the path that
continues. A loop condition seeds the same fact for the loop body, and `&&`
joins several facts. The guard and the read must spell the index and the storage
path the same way, so bind a local first when the index is computed. Any write
the fact mentions — assigning the index, or passing the storage owner by
`mut borrow` — retires the fact.

The second way moves the obligation to the caller with a `where` value contract.
The comparison joins the same `where` clause list as the memory contracts and
uses `<`, `>`, `<=`, or `>=` over parameters and paths through them:

```stark
finite law i32[min max] ItemAt(borrow dynamic i32[min max] items, u64[0 max] index)
    where index < items.Length
{
    return items[index];
}
```

Inside the function, the strict `< items.Length` contract proves the read just
like a guard would. At every call site the contract is re-spelled with the
actual receiver and argument and must be proven by a dominating comparison the
caller already made, the caller's own matching `where` contract forwarding the
obligation outward, or a constant argument that satisfies it. An unproven call
is a compile error that names both the declared contract and the obligation as
spelled at that call site — no runtime cost, and the contract rides through
package images. Value contracts also read as ordinary range preconditions, so
`where start + length <= items.Length` works the same way.

Genuinely sparse structures — hash slots, free lists, parent links — keep the
explicit `unsafe { }` sparse initialized-slot proof, because their
initialization is an invariant the dense prefix cannot show.

## Step 4: Keep Text Views Zero-Copy

`ascii` is a UTF-8 text view. `unicode` is a UTF-32 text view.

Text indexing and slicing create views of the same text kind:

```stark
text[]
text[index]
text[start, length]
```

In the sample, `text[0]` returns a one-element `ascii` view, so it can be
compared with `"s"` or returned as `ascii`.

The standard-library text snippets below assume the modules they use have been
imported:

```stark
import System.Console
import System.IO
import System.IO.File
import System.Memory
import System.Text
```

Use the full view form when an API should receive the whole text:

```stark
fn i64[min max] LengthOf(ascii text)
{
    stack ascii whole = text[];
    return AsciiLength(whole);
}
```

Use the indexed form when you need one text element as a view:

```stark
fn ascii FirstAscii(ascii text)
{
    return text[0];
}
```

Use the range form when the caller should receive a view into existing text,
not an owned copy:

```stark
fn ascii Prefix(ascii text)
{
    return text[0, 4];
}
```

Use the Unicode equivalents when the source text is `unicode`:

```stark
fn i64[min max] UnicodeLength(unicode text)
{
    return UnicodeLength(text);
}

fn unicode FirstCodePoint(unicode text)
{
    return text[0];
}
```
When a literal should be taken verbatim — a regex, a Windows path, or embedded
source text — use a raw string. `raw"..."` is a single-line raw literal with no
escape processing:

```stark
stack ascii pattern = raw"\d+\.\d+";
```

For multiline text, `raw"""` opens a block that follows the same rules as C# raw
string literals. The content starts on the line after the opening quotes and
ends on the line before the closing quotes, and the indentation in front of the
closing quotes is stripped from every content line so the literal can sit at the
indentation of the surrounding code:

```stark
stack ascii usage = raw"""
    usage: stark build [--release]
           stark run
    """;
// value: "usage: stark build [--release]\n       stark run"
```

Three shapes are errors, because they make the trimming ambiguous: content on
the opening-quote line, content on the closing-quote line, and a non-blank
content line indented less than the closing quotes. Whitespace-only lines are
kept as empty lines. The newline right after `raw"""` and the newline right
before the closing `"""` are never part of the value.

Raw literals compose with interpolation as `$raw"..."` and `$raw"""..."""`: the
block is trimmed by these same rules first, then the `{ ... }` holes are
evaluated. Like ordinary string literals, raw literals infer to `ascii` when the
contents fit UTF-8.

## Step 5: Choose Owned Text When Storage Must Survive

`Ascii` and `Unicode` are owning text container forms. Use them when code needs
owned text storage rather than a borrowed view into existing text.

When runtime text needs caller-owned destination storage, Stark asks for the
capacity in the source:

```stark
stack Ascii label[64] = $"Score: {score}";
```

The capacity is part of the local storage choice. If the operation does not fit
the selected destination, Stark does not silently truncate and does not throw a
hidden exception.

This checked sample formats the same numeric value into caller-owned ASCII and
Unicode buffers:

{{< stark-sample "samples/text-formatting.stark" >}}

The fixed-capacity interpolation form is for destination-owned text:

```stark
fn Ascii ScoreLabel(i32[min max] score)
{
    stack Ascii label[64] = $"Score: {score}";
    return label;
}
```

The `[64]` is part of the storage choice. Use a capacity that fits the largest
text the function is allowed to produce.

## Step 6: Concatenate Text By Choosing The Destination

Compile-time text concatenation is just an ordinary text constant:

```stark
finite law ascii ScorePrefix()
{
    return "Score: " + " ";
}
```

Runtime concatenation into fixed storage names the destination capacity:

```stark
fn Ascii JoinLabels(ascii left, ascii right)
{
    stack Ascii combined[128] = left + right;
    return combined;
}
```

When owned formatting allocates, return the allocation-aware result:

```stark
fn MemoryResult<OwnedAscii> ScoreOwned(i64[min max] score)
{
    return ToAscii(score);
}
```

Choose the form based on what the caller should own:

- `ascii` and `unicode` when the caller only needs a view
- fixed-capacity `Ascii` or `Unicode` when the function owns bounded local text
- `OwnedAscii` or `OwnedUnicode` when the function returns allocated owned text

## Step 7: Format And Parse Through Explicit APIs

The standard text APIs expose conversion costs and failure:

- fixed-buffer formatting writes into caller-owned `Ascii` or `Unicode`
- owned formatting returns an allocation-aware result type
- parsing returns result/status data instead of throwing

This keeps text processing aligned with the rest of Stark: allocation, storage
capacity, and recoverable failure are visible in the API.

Fixed-buffer formatting returns `bool`:

```stark
unsafe fn bool WriteCount(rawmutptr<Ascii> destination, i32[min max] count)
{
    return TryFormatI32Ascii(destination, count);
}
```

Owned formatting returns `MemoryResult<T>`:

```stark
stack MemoryResult<OwnedAscii> valueText =
    ToAscii((i64)42);
```

Parsing returns `TextResult<T>`:

```stark
fn i32[min max] ParseOrZero(ascii source)
{
    stack TextResult<i32[min max]> parsed =
        ParseI32Ascii(source);

    switch (parsed)
    {
        case TextResult<i32[min max]>.Err(var error):
            return 0;
        case TextResult<i32[min max]>.Ok(var value):
            return value;
    }
}
```

## Step 8: Convert Between ASCII, Unicode, And UTF-16

`System.Text` has three owned text builders:

- `OwnedAscii` stores bytes
- `OwnedUnicode` stores Unicode code points
- `OwnedUtf16` stores UTF-16 code units

Use `OwnedAscii` when the data is byte-oriented text, `OwnedUnicode` when the
program wants code-point text, and `OwnedUtf16` when a boundary requires UTF-16:

```stark
stack mut OwnedAscii asciiText = new();
stack mut OwnedUnicode unicodeText = new();
stack mut OwnedUtf16 utf16Text = new();
```

The builders expose the same everyday shape:

```stark
stack bool empty = asciiText.IsEmpty();
stack u64[0 2 ** 63 - 1] length = asciiText.Length();
stack u64[0 2 ** 63 - 1] capacity = asciiText.Capacity();
```

Use `View()` on owned ASCII or Unicode when an API accepts `ascii` or
`unicode`:

```stark
fn bool HasOwnedAsciiText(mut borrow OwnedAscii text)
{
    stack ascii view = text.View();
    return AsciiLength(view) > 0;
}
```

Use `AsSlice()` or `AsMutableSlice()` when code should operate on bytes, code
points, or UTF-16 code units directly:

```stark
stack i8[min max][] bytes = asciiText.AsSlice();
stack i32[min max][] codePoints = unicodeText.AsSlice();
stack i16[min max][] codeUnits = utf16Text.AsSlice();
```

Conversion helpers write through an `out` destination and return
`MemoryStatus`:

```stark
fn bool MakeUtf16FromAscii()
{
    stack mut OwnedUtf16 text = new();
    stack MemoryStatus status =
        FromAsciiToUtf16(text, "hello");

    switch (status)
    {
        case MemoryStatus.Err(var error):
            return false;
        case MemoryStatus.Ok:
            return text.Length() == 5;
    }
}
```

The `const` forms are for constant text inputs:

```stark
fn bool MakeOwnedAsciiFromLiteral()
{
    stack mut OwnedAscii text = new();
    return FromConstAscii(text, "literal") == MemoryStatus.Ok;
}
```

Use the direction in the function name:

| Conversion | Helper |
| --- | --- |
| ASCII view to owned ASCII | `FromAscii(out OwnedAscii destination, ascii source)` |
| constant ASCII to owned ASCII | `FromConstAscii(out OwnedAscii destination, const ascii source)` |
| Unicode view to owned Unicode | `FromUnicode(out OwnedUnicode destination, unicode source)` |
| constant Unicode to owned Unicode | `FromConstUnicode(out OwnedUnicode destination, const unicode source)` |
| ASCII to Unicode | `FromAsciiToUnicode(out OwnedUnicode destination, ascii source)` |
| constant ASCII to Unicode | `FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source)` |
| Unicode to ASCII | `FromUnicodeToAscii(out OwnedAscii destination, unicode source)` |
| ASCII to UTF-16 | `FromAsciiToUtf16(out OwnedUtf16 destination, ascii source)` |
| constant ASCII to UTF-16 | `FromConstAsciiToUtf16(out OwnedUtf16 destination, const ascii source)` |
| Unicode to UTF-16 | `FromUnicodeToUtf16(out OwnedUtf16 destination, unicode source)` |
| UTF-16 to Unicode | `FromUtf16ToUnicode(out OwnedUnicode destination, borrow OwnedUtf16 source)` |
| UTF-16 to ASCII | `FromUtf16ToAscii(out OwnedAscii destination, borrow OwnedUtf16 source)` |

## Step 9: Build Owned Text Incrementally

Owned text builders are useful when a function assembles text over several
steps. Reserve when the caller already knows the approximate size, append each
piece, then pass a view or clear the builder for reuse.

For ASCII output, use `OwnedAscii`:

```stark
fn bool BuildAsciiLabel(mut borrow OwnedAscii text)
{
    if (text.Reserve(32) != MemoryStatus.Ok)
    {
        return false;
    }

    if (text.AppendConstAscii("score=") != MemoryStatus.Ok)
    {
        return false;
    }

    if (text.AppendU64(42) != MemoryStatus.Ok)
    {
        return false;
    }

    if (text.AppendByte(33) != MemoryStatus.Ok)
    {
        return false;
    }

    stack ascii view = text.View();
    return view == "score=42!";
}
```

`OwnedAscii` also accepts byte slices and Unicode data encoded as UTF-8:

```stark
fn bool AppendAsciiPieces(mut borrow OwnedAscii text)
{
    stack i8[min max][3] suffix =
    {
        88, 89, 90
    };

    return text.AppendAscii("abc") == MemoryStatus.Ok
        && text.AppendSlice(suffix, 3) == MemoryStatus.Ok
        && text.AppendBool(true) == MemoryStatus.Ok
        && text.AppendI64(-7) == MemoryStatus.Ok
        && text.AppendCodePointAsUtf8(65) == MemoryStatus.Ok
        && text.AppendUnicodeAsUtf8((unicode)"wide") == MemoryStatus.Ok;
}
```

For code-point text, use `OwnedUnicode`:

```stark
fn bool BuildUnicodeLabel(mut borrow OwnedUnicode text)
{
    stack i32[min max][2] marks =
    {
        33, 63
    };

    return text.AppendUnicode((unicode)"score=") == MemoryStatus.Ok
        && text.AppendU64(42) == MemoryStatus.Ok
        && text.AppendSlice(marks, 2) == MemoryStatus.Ok
        && text.AppendAscii(" ok") == MemoryStatus.Ok
        && text.AppendConstAscii(" done") == MemoryStatus.Ok;
}
```

For UTF-16 storage, append code units, code points, ASCII, or Unicode:

```stark
fn bool BuildUtf16Text(mut borrow OwnedUtf16 text)
{
    return text.AppendCodeUnit(65) == MemoryStatus.Ok
        && text.AppendCodePoint(66) == MemoryStatus.Ok
        && text.AppendAscii("C") == MemoryStatus.Ok
        && text.AppendConstAscii("D") == MemoryStatus.Ok
        && text.AppendUnicode((unicode)"E") == MemoryStatus.Ok;
}
```

Use `Clear()` when the owned builder should keep its storage but forget the
current contents:

```stark
fn bool ReuseAsciiBuilder(mut borrow OwnedAscii text)
{
    if (text.AppendAscii("temporary") != MemoryStatus.Ok)
    {
        return false;
    }

    text.Clear();
    return text.IsEmpty();
}
```

All of these append helpers return `MemoryStatus`. Check the
status before using the new contents when the append can allocate, decode text,
or fail because a value is too large.

## Step 10: Use `Encoding` To Name Byte Layouts

`Encoding` describes the byte layout expected at IO and conversion
boundaries:

```stark
Encoding.Binary
Encoding.UTF8
Encoding.UTF16
Encoding.UTF32
```

Use it when opening text files or when a function should make the encoding
choice explicit:

```stark
stack IOResult<File> opened =
    Open("data.txt", FileMode.Write, Encoding.UTF8);
```

`Binary` is the right name when bytes are bytes, not text. `UTF8`, `UTF16`, and
`UTF32` are for text data whose representation matters at the boundary.

`Encoding` also participates in the parse/format helpers:

```stark
stack TextResult<Encoding> parsed =
    ParseEncodingAscii("UTF8");
```

```stark
stack MemoryResult<OwnedAscii> text =
    ToAscii(Encoding.UTF16);
```

## Step 11: Learn The Parse And Format Naming Pattern

The text API is wide, but the naming rule is regular:

- `Parse<Type>Ascii(ascii source)` parses ASCII text
- `Parse<Type>Unicode(unicode source)` parses Unicode text
- `TryFormat<Type>Ascii(destination, value)` writes into caller-owned ASCII
- `TryFormat<Type>Unicode(destination, value)` writes into caller-owned Unicode
- `ToAscii(value)` allocates owned ASCII
- `ToUnicode(value)` allocates owned Unicode

The integer families cover Stark's signed and unsigned widths:

```text
i8, i16, i24, i32, i48, i64, i96, i128, i192, i256, i384, i512, i768, i1024
u8, u16, u24, u32, u48, u64, u96, u128, u192, u256, u384, u512, u768, u1024
```

Booleans, `Encoding`, `TextError`, `f32`, and `f64` also have formatting
helpers. Booleans, `Encoding`, and `TextError` have parse helpers.

The failure branch matters. A parse can fail because the text is not a valid
value or because the value does not fit the requested type:

```stark
fn bool ParseSmallUnsigned(ascii source, out u8[0 max] value)
{
    value = 0;
    stack TextResult<u8[0 max]> parsed =
        ParseU8Ascii(source);

    switch (parsed)
    {
        case TextResult<u8[0 max]>.Err(var error):
            return false;
        case TextResult<u8[0 max]>.Ok(var parsedValue):
            value = parsedValue;
            return true;
    }
}
```

For allocated formatting, switch on the memory result before using the owned
text:

```stark
fn bool WriteFormattedCount(i32[min max] count)
{
    stack MemoryResult<OwnedUnicode> result =
        ToUnicode(count);

    switch (result)
    {
        case MemoryResult<OwnedUnicode>.Err(var error):
            return false;
        case MemoryResult<OwnedUnicode>.Ok(var text):
            stack mut OwnedUnicode owned = text;
            return WriteLine(owned) == IOStatus.Ok;
    }
}
```
