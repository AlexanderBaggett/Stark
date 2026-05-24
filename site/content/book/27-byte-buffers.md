+++
title = "27. Byte Buffers"
weight = 270
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/26-bit-operations/"
next = "/book/28-testing-stark-code/"

[[stdlib_refs]]
title = "System.Console"
href = "/reference/standard-library/System.Console/"

[[stdlib_refs]]
title = "System.IO.File"
href = "/reference/standard-library/System.IO.File/"

[[stdlib_refs]]
title = "System.Net.Tcp"
href = "/reference/standard-library/System.Net.Tcp/"

[[stdlib_refs]]
title = "System.Memory"
href = "/reference/standard-library/System.Memory/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Byte Buffers

Byte buffers are the standard-library shape for incremental byte IO. They keep
the readable bytes and writable spare capacity visible, so console, file, and
TCP code can pass around byte storage without hiding ownership.

{{< stark-sample "assets/book/stdlib-samples/byte-buffers.stark" >}}

The snippets below assume the modules they use have been imported:

```stark
import System.Console
import System.IO
import System.Memory
import System.Runtime.Buffer
```

## Step 1: Import The Buffer Module And Choose A Size

Buffer types live in `System.Runtime.Buffer`. They appear in public console,
file, and TCP APIs, so import the module when your program wants to name the
types directly.

Use a fixed buffer when the maximum size is part of the local design.

```stark
stack mut FixedByteBuffer512 small = new();
stack mut FixedByteBuffer4096 page = new();
stack mut FixedByteBuffer8192 large = new();
```

The three fixed sizes have the same methods. Choose the size from the largest
chunk this part of the program should hold: small command fragments fit in
`FixedByteBuffer512`, page-sized work commonly fits in `FixedByteBuffer4096`,
and larger file or network chunks can use `FixedByteBuffer8192`.

Use `DynamicByteBuffer` when the buffer should grow:

```stark
stack mut DynamicByteBuffer bytes = new();
```

The fixed buffers store bytes inline in the buffer value. The dynamic buffer
owns growable byte storage.

## Step 2: Read The Cursor State Before Writing

All buffer types expose readable and writable counts:

```stark
stack u64[0 2 ** 63 - 1] readable = buffer.Readable();
stack u64[0 2 ** 63 - 1] writable = buffer.Writable();
```

Fixed buffers also expose `Capacity`, `IsEmpty`, and `IsFull`:

```stark
stack bool empty = buffer.IsEmpty();
stack bool full = buffer.IsFull();
stack u64[0 2 ** 63 - 1] capacity = buffer.Capacity();
```

Dynamic buffers expose `Length`, `Capacity`, `Readable`, `Writable`, and
`IsEmpty`:

```stark
stack u64[0 2 ** 63 - 1] length = bytes.Length();
stack u64[0 2 ** 63 - 1] capacity = bytes.Capacity();
```

Think of each buffer as two cursors:

- bytes before the read cursor have already been consumed
- bytes between the read and write cursors are readable
- bytes after the write cursor are writable

That model is enough for most IO code.

## Step 3: Write Bytes And Consume Readable Bytes

Use `WriteByte` for one byte:

```stark
stack MemoryStatus status = buffer.WriteByte(65);
```

Use `WriteSlice(source, count)` when the source bytes are already in a slice:

```stark
stack i8[min max][3] source =
{
    65, 66, 67
};
stack MemoryStatus status = buffer.WriteSlice(source, 3);
```

Use `WriteFill(value, count)` for repeated bytes:

```stark
stack MemoryStatus status = buffer.WriteFill(0, 16);
```

Read the initialized unread bytes with `ReadSlice()`:

```stark
stack i8[min max][] readableBytes = buffer.ReadSlice();
```

Use `ReadMutableSlice()` only when the caller should edit readable bytes in
place:

```stark
fn void ReplaceFirst(mut borrow i8[min max][] readableBytes)
{
    readableBytes[0] = 42;
    return;
}

ReplaceFirst(buffer.ReadMutableSlice());
```

After processing bytes, advance the read cursor:

```stark
buffer.AdvanceRead(count);
```

If `count` is greater than or equal to the readable count, the buffer becomes
empty. Check `Readable()` first when advancing too far would hide a caller bug.

## Step 4: Fill A Fixed Buffer Through Its Writable Slice

Fixed buffers also let an IO API write directly into spare capacity. Pass the
writable tail from the zero-argument `WriteSlice()` to the helper that will fill
it. After the helper writes bytes into that slice, call `AdvanceWrite(count)` so
the buffer knows those bytes are now readable:

```stark
fn void FillPrefix(mut borrow i8[min max][] destination)
{
    destination[0] = 65;
    destination[1] = 66;
    return;
}

FillPrefix(buffer.WriteSlice());
buffer.AdvanceWrite(2);
```

After that, the same bytes are visible through the readable side of the buffer:

```stark
ReplaceFirst(buffer.ReadMutableSlice());
```

This is the shape used by APIs that receive bytes from outside the program:
borrow the writable tail, fill some prefix of it, then advance by the number of
bytes actually written.

`AdvanceWrite(count)` saturates at the fixed buffer capacity. Check `Writable()`
first when writing too far should be treated as a bug in the caller.

## Step 5: Compact Or Clear When Space Should Be Reused

`AdvanceRead` does not move unread bytes. It only moves the read cursor. That is
cheap and usually what you want inside a loop.

When consumed space should become writable again, call `Compact()`:

```stark
buffer.AdvanceRead(32);
buffer.Compact();
```

Compaction moves the remaining readable bytes to the front of the buffer and
resets the cursors around that unread data.

Use `Clear()` when all readable data should be discarded:

```stark
buffer.Clear();
```

The buffer value remains usable after `Clear()`.

## Step 6: Use Dynamic Buffers For Growing Byte Streams

`DynamicByteBuffer` grows as bytes are appended. Reserve space before a known
large write:

```stark
stack MemoryStatus reserved = bytes.Reserve(1024);
```

Then write byte data the same way:

```stark
stack i8[min max][3] source =
{
    65, 66, 67
};
bytes.WriteByte(65);
bytes.WriteSlice(source, 3);
bytes.WriteFill(0, 3);
```

Use `TryReadByte` when consuming one byte at a time:

```stark
stack mut i8[min max] value = 0;
if (bytes.TryReadByte(value))
{
    WriteLine("had a byte");
}
```

Dynamic buffers do not expose a writable tail slice. They grow through
`Reserve`, `WriteByte`, `WriteSlice`, and `WriteFill`.

## Step 7: Pass Buffers To Console, File, And TCP APIs

Console output accepts byte buffers:

```stark
WriteLine(buffer);
```

Console input can fill a buffer:

```stark
stack MemoryResult<u64[0 2 ** 63 - 1]> read = ReadBytes(buffer, 512);
```

Files can write buffer contents:

```stark
stack IOResult<u64[0 2 ** 63 - 1]> written = file.Write(buffer);
```

TCP clients can read into fixed or dynamic buffers and write buffer contents:

```stark
client.Read(buffer);
client.Write(buffer);
```

The common rule is simple: the buffer owns byte storage, `ReadSlice()` exposes
the readable bytes, and write-oriented APIs advance the buffer only after they
know how many bytes were received.

## Step 8: Keep The Buffer Surface Nearby

The three fixed buffers have the same public shape:

| Type | Capacity |
| --- | --- |
| `FixedByteBuffer512` | 512 bytes |
| `FixedByteBuffer4096` | 4096 bytes |
| `FixedByteBuffer8192` | 8192 bytes |

| Fixed-buffer operation | Signature shape |
| --- | --- |
| Capacity and state | `Capacity()`, `Readable()`, `Writable()`, `IsEmpty()`, `IsFull()` |
| Readable views | `ReadSlice()`, `ReadMutableSlice()` |
| Writable view | `WriteSlice()` |
| Append bytes | `WriteByte(value)`, `WriteSlice(source, count)`, `WriteFill(value, count)` |
| Move cursors | `AdvanceRead(count)`, `AdvanceWrite(count)` |
| Reuse storage | `Compact()`, `Clear()` |

`DynamicByteBuffer` has this public shape:

| Dynamic-buffer operation | Signature shape |
| --- | --- |
| Capacity and state | `Length()`, `Capacity()`, `Readable()`, `Writable()`, `IsEmpty()` |
| Readable views | `ReadSlice()`, `ReadMutableSlice()` |
| Reserve and append | `Reserve(additional)`, `WriteByte(value)`, `WriteSlice(source, count)`, `WriteFill(value, count)` |
| Read one byte | `TryReadByte(out value)` |
| Reuse storage | `AdvanceRead(count)`, `Compact()`, `Clear()` |
