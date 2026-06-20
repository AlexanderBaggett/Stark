# `System.Toml`

`System.Toml` is the blessed reusable TOML reader and deterministic writer for
Stark. It backs `Stark.toml`, `Stark.solution.toml`, and `Stark.user.toml`
manifests, but it is an ordinary library usable by tools, tests, and user code.

A parsed document is a flat node table with parent links, decoded text storage,
and a line/column span on every node, mirroring `System.Json`'s proven shape.
Source spans make every diagnostic point at a file position.

The parser currently accepts the staged manifest subset of TOML; values outside
that subset are reported as span-carrying diagnostics rather than silently
accepted (see [Supported subset](#supported-subset)).

## Public Surface

```stark
import System.Toml
module System.Toml

public enum TomlKind
{
    Table,
    Array,
    Text,
    Integer,
    Float,
    True,
    False,
    Datetime,
}

public enum TomlError
{
    OutOfMemory,
    UnexpectedEndOfInput { Line, Column },
    UnexpectedCharacter { Line, Column },
    InvalidEscape       { Line, Column },
    UnterminatedString  { Line, Column },
    InvalidNumber       { Line, Column },
    InvalidDatetime     { Line, Column },
    DuplicateKey        { Line, Column },
    UnsupportedValue    { Line, Column },
    DepthExceeded       { Line, Column },
}

public enum TomlStatus;            // [Ok] Ok | [Err] Err(TomlError)
public enum TomlResult<T>;         // [Ok] Ok(T) | [Err] Err(TomlError)

public const TomlRootNode = 0;

// Which RFC 3339 shape a decoded TomlDateTime carries.
public enum TomlDateTimeKind
{
    OffsetDateTime,    // YYYY-MM-DDTHH:MM:SS[.fff](Z|+HH:MM|-HH:MM)
    LocalDateTime,     // YYYY-MM-DDTHH:MM:SS[.fff]
    LocalDate,         // YYYY-MM-DD
    LocalTime,         // HH:MM:SS[.fff]
}

// A decoded, calendar-validated RFC 3339 date-time. Fields absent for the
// `Kind` are zero; `HasOffset` is true only for `OffsetDateTime`, where
// `OffsetMinutes` is signed minutes east of UTC (`Z` decodes as 0).
public struct TomlDateTime
{
    TomlDateTimeKind Kind;
    u16[0 9999]      Year;
    u8[0 12]         Month;
    u8[0 31]         Day;
    u8[0 23]         Hour;
    u8[0 59]         Minute;
    u8[0 60]         Second;       // 60 permits an RFC 3339 leap second
    u32[0 999999999] Nanosecond;
    bool             HasOffset;
    i16[-1439 1439]  OffsetMinutes;
}

public struct TomlDocument
{
    public inline finite law u64 Count(borrow TomlDocument self);
    public finite law TomlKind KindAt(borrow TomlDocument self, u64 node);
    public finite law u64 ChildCountAt(borrow TomlDocument self, u64 node);
    public finite law bool BoolAt(borrow TomlDocument self, u64 node);
    public finite law u32 LineAt(borrow TomlDocument self, u64 node);
    public finite law u32 ColumnAt(borrow TomlDocument self, u64 node);
    public finite ascii TextAt(mut borrow TomlDocument self, u64 node);
    public finite ascii KeyAt(mut borrow TomlDocument self, u64 node);
    public finite ascii DatetimeAt(mut borrow TomlDocument self, u64 node);   // verbatim token
    public finite TomlResult<i64> I64At(mut borrow TomlDocument self, u64 node);
    public finite TomlResult<f64> F64At(mut borrow TomlDocument self, u64 node);
    public finite TomlResult<TomlDateTime> DateTimeAt(mut borrow TomlDocument self, u64 node);

    public finite bool TryFindMember(
        borrow TomlDocument self, u64 tableNode, ascii name, out u64 member);
    public fn bool TryChildAt(
        borrow TomlDocument self, u64 containerNode, u64 childIndex, out u64 child);
    public finite bool TryFindMemberOfKind(
        borrow TomlDocument self, u64 tableNode, ascii name, TomlKind kind, out u64 member);
}

public fn TomlResult<TomlDocument> Parse(borrow i8[min max][] source);
public unsafe fn TomlResult<TomlDocument> ParseText(ascii source);

public struct TomlWriter
{
    public finite ascii View(mut borrow TomlWriter self);
    public finite law retborrow i8[min max][] Bytes(borrow TomlWriter self);
    public finite law u64 Length(borrow TomlWriter self);
}

public fn TomlStatus Write(mut borrow TomlWriter writer, mut borrow TomlDocument document);
public fn TomlResult<System.Text.OwnedAscii> Emit(mut borrow TomlDocument document);

public enum TomlFileError
{
    Io from System.IO.IOError,
    Toml from TomlError,
}

public enum TomlFileResult<T>;     // [Ok] Ok(T) | [Err] Err(TomlFileError)
public enum TomlFileStatus;        // [Ok] Ok    | [Err] Err(TomlFileError)

public fn TomlFileResult<TomlDocument> ReadFile(ascii path);
public fn TomlFileStatus WriteFile(ascii path, mut borrow TomlDocument document);
public fn TomlFileStatus WriteFileAtomic(ascii path, mut borrow TomlDocument document);
```

The integer-width spellings above are abbreviated; the source uses ranged
primitives such as `u64[0 2 ** 63 - 1]` and `u32[0 max]`.

## Document Model

A `TomlDocument` is a flat array of nodes. Node `0` (`TomlRootNode`) is the
implicit root table; every other node carries a parent link, an optional decoded
key, and either decoded scalar text or a child count.

- `Count()` returns the total number of nodes (including the root).
- `KindAt(node)` returns the node's `TomlKind`. Out-of-range indices answer
  `Table` rather than trapping.
- `ChildCountAt(node)` returns the number of direct children of a table or
  array container.
- `LineAt(node)` / `ColumnAt(node)` return the 1-based source position the value
  began at, for diagnostics.

### Accessors

- `KeyAt(node)` returns the node's decoded key bytes as an `ascii` view, or `""`
  when the node has no key (array elements and the root).
- `TextAt(node)` returns the decoded scalar text of a `Text` or `Integer` node
  as an `ascii` view (escapes already applied for strings; the normalized digit
  token for integers).
- `BoolAt(node)` returns `true` only when the node's kind is `True`.
- `I64At(node)` decodes an `Integer` node through `System.Text.ParseI64Ascii`
  (or the `0x`/`0o`/`0b` radix decoder), returning `TomlResult<i64>`; a
  non-integer node or an out-of-range value yields `Err(InvalidNumber)` with the
  node's span.
- `F64At(node)` reads a `Float` (or widens an `Integer`) node to `f64` through
  the correctly-rounded `System.Text.ParseF64Ascii`, returning `TomlResult<f64>`.
- `DatetimeAt(node)` returns a `Datetime` node's verbatim RFC 3339 token as an
  `ascii` view (the original spelling), or `""` for any other kind.
- `DateTimeAt(node)` decodes a `Datetime` node into a calendar-validated
  `TomlDateTime` (typed components plus a `TomlDateTimeKind`), returning
  `TomlResult<TomlDateTime>`; a non-datetime node yields `Err(InvalidDatetime)`.
  The reader already rejects out-of-range calendar fields at parse time, so a
  parsed `Datetime` node always decodes successfully.

`KeyAt`, `TextAt`, and `DatetimeAt` take `mut borrow` because they project an
`ascii` view over the document's decoded-text storage.

### Lookup

- `TryFindMember(tableNode, name, out member)` finds a table member by exact
  decoded key bytes and reports `false` when the container is not a table or the
  key is absent.
- `TryFindMemberOfKind(tableNode, name, kind, out member)` is the same lookup but
  also requires the found member to have the given `TomlKind`.
- `TryChildAt(containerNode, childIndex, out child)` returns the zero-based
  N-th direct child of an array (or table) container.

## Parsing

- `Parse(source)` parses one document from UTF-8 bytes.
- `ParseText(source)` parses one document from an `ascii` view (an empty view is
  a valid empty root table). It is `unsafe` because it bridges the view to a byte
  slice.

Both return `TomlResult<TomlDocument>`; a malformed document yields a
`TomlError` carrying the offending line and column. Nesting deeper than the fixed
maximum reports `DepthExceeded`.

## Writing

`Write(writer, document)` serializes a document into a `TomlWriter` using a
canonical, deterministic ordering: each table's members are sorted by decoded
key bytes, scalar `key = value` lines come before sub-table headers, sub-tables
use dotted-path `[a.b.c]` headers, array elements keep source order, and inline
tables are sorted. The same document therefore emits byte-identical output, and
`parse -> emit -> parse` is stable.

`Emit(document)` is the convenience form: it serializes to a fresh
`System.Text.OwnedAscii` and returns `TomlResult<OwnedAscii>`. Value kinds the
parser cannot yet produce route through `UnsupportedValue` rather than inventing
a spelling.

## File Helpers

The file helpers compose `System.IO.File` without hiding IO failures. They use
`TomlFileError`, which funnels **both** IO failures (`Io from System.IO.IOError`)
and parse/emit failures (`Toml from TomlError`), so nothing is swallowed:

- `ReadFile(path)` reads a file with `System.IO.File.ReadAllTextInto` and parses
  it, returning `TomlFileResult<TomlDocument>`. A missing file surfaces as
  `Err(Io(...))`; a malformed document surfaces as `Err(Toml(...))`.
- `WriteFile(path, document)` emits the document and writes it with
  `System.IO.File.WriteAllText`, returning `TomlFileStatus`.
- `WriteFileAtomic(path, document)` is the same but routes through
  `System.IO.File.WriteAllTextAtomic` so the target is replaced in one step.

## Supported subset

`System.Toml` currently implements the staged manifest subset of TOML. This is
tracked as temporary implementation work in
[`21-system-toml.md`](../Self-host-Prep/21-system-toml.md), not the design
target. Unsupported syntax is reported as a span-carrying diagnostic, never
silently accepted.

Parses today:

- tables and table headers (`[a.b]`)
- dotted keys (`a.b.c = ...`)
- inline tables (`{ k = v, ... }`)
- arrays (`[ v, v, ... ]`), including multiline content inside the brackets
- decimal integers, with underscore-grouping validation
- booleans (`true` / `false`)
- basic strings (`"..."`) with `\b \t \n \f \r \" \\ \uXXXX` escapes
- literal strings (`'...'`)
- bare, basic-quoted, and literal-quoted keys
- comments (`# ...`) and blank lines

Reports a diagnostic today (not yet supported):

- multiline strings (`"""..."""` / `'''...'''`) -> `UnsupportedValue`
- floats -> `UnexpectedCharacter` / `InvalidNumber`
- date-times and dates/times -> not parsed as values
- arrays of tables (`[[a]]`) -> `UnsupportedValue`
- hex / octal / binary integers -> not part of the decimal-integer subset
- the `\U` (8-digit) unicode escape -> `InvalidEscape`

Duplicate keys within a table are rejected with `DuplicateKey`.

## Example

Read a manifest, look up `project.name`, and report its source position:

```stark
import System.Toml
module Manifest

public fn System.Toml.TomlFileResult<System.Text.OwnedAscii> ProjectName(ascii path)
{
    stack mut System.Toml.TomlDocument document = new();
    switch (System.Toml.ReadFile(path))
    {
        case System.Toml.TomlFileResult<System.Toml.TomlDocument>.Err(var error):
            return System.Toml.TomlFileResult<System.Text.OwnedAscii>.Err(error);
        case System.Toml.TomlFileResult<System.Toml.TomlDocument>.Ok(var parsed):
            document = parsed;
    }

    stack mut u64[0 2 ** 63 - 1] project = 0;
    if (!document.TryFindMemberOfKind(
            System.Toml.TomlRootNode, "project", System.Toml.TomlKind.Table, project))
    {
        return System.Toml.TomlFileResult<System.Text.OwnedAscii>.Err(
            System.Toml.TomlFileError.Toml(System.Toml.TomlError.UnsupportedValue
            {
                Line: 1, Column: 1
            }));
    }

    stack mut u64[0 2 ** 63 - 1] name = 0;
    if (!document.TryFindMemberOfKind(
            project, "name", System.Toml.TomlKind.Text, name))
    {
        return System.Toml.TomlFileResult<System.Text.OwnedAscii>.Err(
            System.Toml.TomlFileError.Toml(System.Toml.TomlError.UnsupportedValue
            {
                Line: document.LineAt(project), Column: document.ColumnAt(project)
            }));
    }

    stack mut System.Text.OwnedAscii owned = new();
    stack System.Memory.MemoryStatus copied = owned.AppendAscii(document.TextAt(name));
    switch (copied)
    {
        case System.Memory.MemoryStatus.Err(var memoryError):
            return System.Toml.TomlFileResult<System.Text.OwnedAscii>.Err(
                System.Toml.TomlFileError.Toml(System.Toml.TomlError.OutOfMemory));
        case System.Memory.MemoryStatus.Ok:
            return System.Toml.TomlFileResult<System.Text.OwnedAscii>.Ok(owned);
    }
}
```

## Current Status

- The reader, deterministic writer, and IO-aware file helpers are implemented in
  `stdlib/src/System/Toml.stark`, with 26 facts in `tests-stark/stdlib.Toml`
  covering conformance, malformed input, source spans, real-manifest decoding,
  emitter determinism, round-trip idempotence, and the file helpers.
- The parser implements the staged manifest subset; the remaining TOML 1.x
  surface (multiline strings, floats, date-times, arrays of tables, non-decimal
  integers, and `\U` escapes) is tracked in
  [`21-system-toml.md`](../Self-host-Prep/21-system-toml.md#7-work-items).
- Typed manifest decoding for the self-hosted project driver (replacing the
  host-style `SimpleToml` handling) is still future work; `System.Toml` is the
  library it will build on.
