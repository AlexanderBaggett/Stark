# `System.Runtime.Buffer`

`System.Runtime.Buffer` provides standard-library byte buffers used by console,
file, and TCP APIs.

Import the module when code needs to name the buffer types directly:

```stark
import System.Runtime.Buffer
```

## Fixed Buffers

```stark
public struct FixedByteBuffer512;
public struct FixedByteBuffer4096;
public struct FixedByteBuffer8192;
```

The fixed buffers have the same public shape and differ only by capacity.

```stark
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow FixedByteBuffer512 self);
inline finite law bool IsEmpty(borrow FixedByteBuffer512 self);
inline finite law bool IsFull(borrow FixedByteBuffer512 self);
inline finite law u64[0 2 ** 63 - 1] Readable(borrow FixedByteBuffer512 self);
inline finite law u64[0 2 ** 63 - 1] Writable(borrow FixedByteBuffer512 self);
finite law retborrow i8[min max][] ReadSlice(borrow FixedByteBuffer512 self);
finite retborrow mut i8[min max][] ReadMutableSlice(mut borrow FixedByteBuffer512 self);
finite retborrow mut i8[min max][] WriteSlice(mut borrow FixedByteBuffer512 self);
fn System.Memory.MemoryStatus WriteByte(mut borrow FixedByteBuffer512 self, i8[min max] value);
fn System.Memory.MemoryStatus WriteSlice(mut borrow FixedByteBuffer512 self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count);
fn System.Memory.MemoryStatus WriteFill(mut borrow FixedByteBuffer512 self, i8[min max] value, u64[0 2 ** 63 - 1] count);
fn void AdvanceRead(mut borrow FixedByteBuffer512 self, u64[0 2 ** 63 - 1] count);
fn void AdvanceWrite(mut borrow FixedByteBuffer512 self, u64[0 2 ** 63 - 1] count);
fn void Compact(mut borrow FixedByteBuffer512 self);
fn void Clear(mut borrow FixedByteBuffer512 self);
```

`FixedByteBuffer4096` and `FixedByteBuffer8192` expose the same method names
with their own receiver type.

## Dynamic Buffer

```stark
public struct DynamicByteBuffer
{
    DynamicByteBuffer();
    inline finite law u64[0 2 ** 63 - 1] Length(borrow DynamicByteBuffer self);
    inline finite law u64[0 2 ** 63 - 1] Capacity(borrow DynamicByteBuffer self);
    inline finite law u64[0 2 ** 63 - 1] Readable(borrow DynamicByteBuffer self);
    inline finite law u64[0 2 ** 63 - 1] Writable(borrow DynamicByteBuffer self);
    inline finite law bool IsEmpty(borrow DynamicByteBuffer self);
    finite law retborrow i8[min max][] ReadSlice(borrow DynamicByteBuffer self);
    finite retborrow mut i8[min max][] ReadMutableSlice(mut borrow DynamicByteBuffer self);
    fn System.Memory.MemoryStatus Reserve(mut borrow DynamicByteBuffer self, u64[0 2 ** 63 - 1] additional);
    fn System.Memory.MemoryStatus WriteByte(mut borrow DynamicByteBuffer self, i8[min max] value);
    fn System.Memory.MemoryStatus WriteSlice(mut borrow DynamicByteBuffer self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count);
    fn System.Memory.MemoryStatus WriteFill(mut borrow DynamicByteBuffer self, i8[min max] value, u64[0 2 ** 63 - 1] count);
    fn bool TryReadByte(mut borrow DynamicByteBuffer self, out i8[min max] value);
    fn void AdvanceRead(mut borrow DynamicByteBuffer self, u64[0 2 ** 63 - 1] count);
    fn void Compact(mut borrow DynamicByteBuffer self);
    fn void Clear(mut borrow DynamicByteBuffer self);
}
```

## Cursor Model

- `Readable()` is the number of bytes between the read cursor and write cursor.
- `Writable()` is the available spare capacity.
- `ReadSlice()` borrows the readable bytes.
- `WriteSlice()` on fixed buffers borrows the writable tail.
- `AdvanceRead(count)` consumes readable bytes.
- `AdvanceWrite(count)` marks bytes written into a fixed buffer's writable tail.
- `Compact()` moves unread bytes to the front so consumed space can be reused.
- `Clear()` discards all readable bytes.

Use fixed buffers when the maximum size is part of the local design. Use
`DynamicByteBuffer` when the buffer should grow.
