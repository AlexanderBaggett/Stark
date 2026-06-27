# Stark Standard Library Signatures

Generated from `stdlib/src/System` public modules. This reference lists public top-level functions plus public member functions/constructors on public structs, records, traits, and doctrines. It intentionally omits module-private and internal implementation helpers.

## Module Summary

- `System.BitOperations`: bit counting, leading/trailing zero counts, rotations, byte swaps, and powers of two.
- `System.C`: C primitive aliases, null-terminated C string views/owners/buffers, explicit text conversion, and foreign-owned C string copy/dispose helpers.
- `System.Collections`: owned list, stack, queue, ring queue, dictionary, hash set, lookup, and in-place sorting helpers.
- `System.Compiler.IntegerFacts`: bounded `i1024`/`u1024` compiler integer-fact helpers for range, storage, tag, checked arithmetic, known-bit, and two's-complement reasoning.
- `System.Console`: console input/output for text, byte buffers, and owned text containers.
- `System.Core`: canonical `Option<T>` and `Result<T, E>` enum definitions backing the root `System` aliases.
- `System.FileSystem`: directories, file existence/type checks, directory iteration, filesystem mutations, metadata, recursive walk, and streaming glob traversal.
- `System.IO`: shared IO result/status/error enums.
- `System.IO.File`: owned file handles, file open modes, buffering choices, byte/text reads, whole-file text/byte helpers, atomic whole-file replacement helpers, writes, close, delete, and move.
- `System.IO.Path`: path parsing, current/temp directory queries, glob matching, and path queries.
- `System.Math`: scalar math helpers, trigonometry, hyperbolic functions, `Exp`/`Log`/`Pow`, rounding, min/max/clamp, fused multiply-add, reciprocal estimates, and xorshift PRNG.
- `System.Memory`: reservation, append, copy, move, fill, allocation status/result contracts.
- `System.Net`: network result/status/error types and IPv4 endpoint values.
- `System.Net.Tcp`: owned blocking TCP clients/listeners, scalar and vectored I/O, waits, shutdown, and close.
- `System.Process`: process id/exit plus Linux-backed spawn/capture with optional stdin input, environment, cwd, and argv helpers.
- `System.Runtime.Buffer`: fixed and dynamic byte buffers with read/write cursors and slices.
- `System.Testing`: test assertions and failure status helpers used by generated `[Fact]` / `[Theory]` runners with inline and typed indexed member data.
- `System.Text`: owned ASCII/Unicode/UTF-16 text, conversions, formatting, Stark literal escaping/decoding, and encoding results.
- `System.Threading`: thread handles, no-payload and explicit payload thread start/join/detach, yield, sleep, guarded shared state, MPSC channels, and atomic types for every integer width plus bool (seq-cst shared state).

## Public API Signatures

### System.BitOperations

Source: `stdlib/src/System/BitOperations.stark`

Top-level functions:

```stark
public finite law i32[min max] LeadingZeroCount(i32[min max] value);
public finite law i64[min max] LeadingZeroCount(i64[min max] value);
public finite law i32[min max] TrailingZeroCount(i32[min max] value);
public finite law i64[min max] TrailingZeroCount(i64[min max] value);
public finite law i32[min max] PopCount(i32[min max] value);
public finite law i64[min max] PopCount(i64[min max] value);
public finite law i32[min max] RotateLeft(i32[min max] value, i32[min max] amount);
public finite law i64[min max] RotateLeft(i64[min max] value, i64[min max] amount);
public finite law i32[min max] RotateRight(i32[min max] value, i32[min max] amount);
public finite law i64[min max] RotateRight(i64[min max] value, i64[min max] amount);
```

### System.C

Source: `stdlib/src/System/C.stark`

Compiler-known C primitive aliases (target-resolved to Stark sized primitives):

`c_char`, `c_schar`, `c_uchar`, `c_short`, `c_ushort`, `c_int`, `c_uint`,
`c_long`, `c_ulong`, `c_longlong`, `c_ulonglong`, `c_size_t`, `c_ptrdiff_t`, and
`c_void` (incomplete pointee, valid only behind `rawptr`/`rawmutptr`). `VaList`
models C `va_list` only as an unsafe `ffi(c)`-compatible parameter, an `ffi(c)`
function-pointer parameter, or a direct raw-pointer pointee. The
compile-time bool `System.C.c_char_is_signed` reports the target's plain-`char`
signedness. `c_int` is `i32[min max]`; `c_long` is `i32[min max]` on
ILP32/LLP64 and `i64[min max]` on LP64; `c_size_t` is `u64[0 max]` on 64-bit and
`u32[0 max]` on 32-bit. No implicit conversion exists between `rawptr<c_char>`
and `ascii`, or between `rawptr<c_void>` and a typed raw pointer.

Public types:

- `enum CStringError`
- `enum CStringResult<T>`
- `struct CStr`
- `struct OwnedCStr`
- `struct CCharBuffer`
- `struct ForeignOwnedCStr`

Public aliases:

```stark
public alias CStringDisposer =
    fnptr<unsafe ffi(c) fn void(rawmutptr<System.C.c_char>)>;
```

Top-level functions:

```stark
public unsafe fn CStringResult<CStr> TryFromRawBounded(
    rawptr<System.C.c_char> data,
    u64[0 2 ** 63 - 1] maxLength);
public unsafe fn CStr FromRawUnchecked(
    rawptr<System.C.c_char> data,
    u64[0 2 ** 63 - 1] length);
public unsafe fn CStringResult<ForeignOwnedCStr> TryFromForeignOwnedRaw(
    rawmutptr<System.C.c_char> data);
public unsafe fn void DisposeForeignOwned(
    mut borrow ForeignOwnedCStr text,
    CStringDisposer dispose);
public fn CStringResult<OwnedCStr> FromAscii(ascii text);
public fn CStringResult<OwnedCStr> FromUnicodeUtf8(unicode text);
public fn CStringResult<System.Text.OwnedAscii> ToAscii(borrow CStr text);
public fn CStringResult<System.Text.OwnedUnicode> ToUnicodeUtf8(borrow CStr text);
public unsafe fn CStringResult<System.Text.OwnedAscii> CopyForeignOwnedAsciiAndDispose(
    mut borrow ForeignOwnedCStr text,
    CStringDisposer dispose,
    u64[0 2 ** 63 - 1] maxLength);
public unsafe fn CStringResult<System.Text.OwnedUnicode> CopyForeignOwnedUnicodeUtf8AndDispose(
    mut borrow ForeignOwnedCStr text,
    CStringDisposer dispose,
    u64[0 2 ** 63 - 1] maxLength);
public fn CStringResult<CCharBuffer> NewCCharBuffer(u64[1 2 ** 63 - 1] capacity);
```

Member functions:

`CStr`

```stark
inline finite law bool IsEmpty(borrow CStr self);
inline finite law u64[0 2 ** 63 - 1] Length(borrow CStr self);
unsafe inline finite law rawptr<System.C.c_char> Data(borrow CStr self);
```

`OwnedCStr`

```stark
inline finite law bool IsEmpty(borrow OwnedCStr self);
inline finite law u64[0 2 ** 63 - 1] Length(borrow OwnedCStr self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow OwnedCStr self);
unsafe finite law rawptr<System.C.c_char> Data(borrow OwnedCStr self);
unsafe finite law CStr View(borrow OwnedCStr self);
```

`CCharBuffer`

```stark
inline finite law bool IsEmpty(borrow CCharBuffer self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow CCharBuffer self);
unsafe finite law rawmutptr<System.C.c_char> Data(mut borrow CCharBuffer self);
unsafe fn CStringResult<CStr> TryAsCStr(borrow CCharBuffer self);
```

`ForeignOwnedCStr`

```stark
inline finite law bool IsNull(borrow ForeignOwnedCStr self);
unsafe inline finite law rawmutptr<System.C.c_char> Data(borrow ForeignOwnedCStr self);
unsafe fn CStringResult<CStr> TryViewBounded(
    borrow ForeignOwnedCStr self,
    u64[0 2 ** 63 - 1] maxLength);
```

### System.Core

Source: `stdlib/src/System/Core.stark`

Public types:

- `enum Result<T, E>`
- `enum Option<T>`

### System.Compiler.IntegerFacts

Source: `stdlib/src/System/Compiler/IntegerFacts.stark`

Public types:

- `enum IntegerFactError`
- `enum IntegerFactResult<T>`
- `enum IntegerRangeStorageViolationKind`
- `struct IntegerTypeBounds`
- `struct IntegerStorageClass`
- `struct IntegerStorageSuggestion`
- `struct SignedRange`
- `struct UnsignedRange`
- `struct IntegerTagType`
- `struct KnownBits`

Top-level functions:

```stark
public inline finite law bool IsValidBitWidth(u64[0 max] bitWidth);
public inline finite law bool IsValidBitCount(u64[0 max] bitCount);
public inline finite law bool IsSupportedIntegerWidth(u64[0 max] bitWidth);
public inline finite law i1024[min max] MinSigned(i1024[min max] left, i1024[min max] right);
public inline finite law i1024[min max] MaxSigned(i1024[min max] left, i1024[min max] right);
public inline finite law u1024[0 max] MinUnsigned(u1024[0 max] left, u1024[0 max] right);
public inline finite law u1024[0 max] MaxUnsigned(u1024[0 max] left, u1024[0 max] right);
public inline finite law IntegerFactResult<u1024[0 max]> BitMask(u64[0 max] bitCount);
public inline finite law IntegerFactResult<u1024[0 max]> UnsignedMaxForBitWidth(u64[0 max] bitWidth);
public inline finite law IntegerFactResult<i1024[min max]> SignedMinForBitWidth(u64[0 max] bitWidth);
public inline finite law IntegerFactResult<i1024[min max]> SignedMaxForBitWidth(u64[0 max] bitWidth);
public inline finite law IntegerFactResult<IntegerTypeBounds> GetIntegerTypeBounds(u64[0 max] bitWidth, bool isUnsigned);
public inline finite law bool FitsUnsigned(u1024[0 max] value, u64[0 max] bitWidth);
public inline finite law bool FitsSigned(i1024[min max] value, u64[0 max] bitWidth);
public inline finite law IntegerFactResult<u1024[0 max]> TrySignedToUnsigned(i1024[min max] value);
public inline finite law IntegerFactResult<i1024[min max]> TryUnsignedToSigned(u1024[0 max] value);
public inline finite law IntegerFactResult<SignedRange> CreateSignedRange(i1024[min max] min, i1024[min max] max);
public inline finite law IntegerFactResult<UnsignedRange> CreateUnsignedRange(u1024[0 max] min, u1024[0 max] max);
public inline finite law bool ContainsSigned(borrow SignedRange range, i1024[min max] value);
public inline finite law bool ContainsUnsigned(borrow UnsignedRange range, u1024[0 max] value);
public inline finite law IntegerFactResult<SignedRange> IntersectSignedRanges(borrow SignedRange left, borrow SignedRange right);
public inline finite law SignedRange UnionSignedRanges(borrow SignedRange left, borrow SignedRange right);
public inline finite law IntegerFactResult<UnsignedRange> IntersectUnsignedRanges(borrow UnsignedRange left, borrow UnsignedRange right);
public inline finite law UnsignedRange UnionUnsignedRanges(borrow UnsignedRange left, borrow UnsignedRange right);
public finite law IntegerFactResult<IntegerStorageClass> SmallestUnsignedStorageForRange(u1024[0 max] min, u1024[0 max] max);
public finite law IntegerFactResult<IntegerStorageClass> SmallestSignedStorageForRange(i1024[min max] min, i1024[min max] max);
public finite law IntegerFactResult<IntegerStorageSuggestion> GetStorageViolation(u64[0 max] currentBitWidth, bool currentIsUnsigned, i1024[min max] min, i1024[min max] max);
public finite law IntegerFactResult<IntegerTagType> TagTypeForVariantCount(u64[0 max] variantCount);
public finite law IntegerFactResult<i1024[min max]> CheckedAddSigned(i1024[min max] left, i1024[min max] right);
public finite law IntegerFactResult<i1024[min max]> CheckedSubSigned(i1024[min max] left, i1024[min max] right);
public finite law IntegerFactResult<u1024[0 max]> CheckedAddUnsigned(u1024[0 max] left, u1024[0 max] right);
public inline finite law IntegerFactResult<u1024[0 max]> CheckedSubUnsigned(u1024[0 max] left, u1024[0 max] right);
public finite law IntegerFactResult<u1024[0 max]> CheckedMulUnsigned(u1024[0 max] left, u1024[0 max] right);
public finite law IntegerFactResult<u1024[0 max]> CheckedShiftLeftUnsigned(u1024[0 max] value, u64[0 max] shift, u64[0 max] bitWidth);
public finite law IntegerFactResult<u1024[0 max]> ShiftLeftMaskedUnsigned(u1024[0 max] value, u64[0 max] shift, u64[0 max] bitWidth);
public finite law IntegerFactResult<u1024[0 max]> ShiftRightMaskedUnsigned(u1024[0 max] value, u64[0 max] shift, u64[0 max] bitWidth);
public finite law IntegerFactResult<KnownBits> CreateKnownBits(u64[0 max] bitWidth, u1024[0 max] knownZero, u1024[0 max] knownOne);
public inline finite law IntegerFactResult<KnownBits> UnknownKnownBits(u64[0 max] bitWidth);
public finite law IntegerFactResult<KnownBits> KnownBitsFromUnsignedConstant(u1024[0 max] value, u64[0 max] bitWidth);
public finite law IntegerFactResult<KnownBits> KnownBitsAnd(borrow KnownBits left, borrow KnownBits right);
public finite law IntegerFactResult<KnownBits> KnownBitsOr(borrow KnownBits left, borrow KnownBits right);
public finite law IntegerFactResult<KnownBits> KnownBitsXor(borrow KnownBits left, borrow KnownBits right);
public finite law IntegerFactResult<KnownBits> KnownBitsShiftLeft(borrow KnownBits value, u64[0 max] shift);
public finite law IntegerFactResult<KnownBits> KnownBitsLogicalShiftRight(borrow KnownBits value, u64[0 max] shift);
public inline finite law u1024[0 max] KnownMask(borrow KnownBits value);
public finite law IntegerFactResult<u1024[0 max]> TwosComplementNormalize(i1024[min max] value, u64[0 max] bitWidth);
public finite law IntegerFactResult<i1024[min max]> SignExtendUnsigned(u1024[0 max] value, u64[0 max] bitWidth);
```


### System.Collections

Source: `stdlib/src/System/Collections.stark`

Public types:

- `trait Equatable<T>`
- `trait Hashable<T>`
- `trait Eq`
- `trait Hash`
- `trait Ord`
- `trait Format`
- `enum Ordering`
- `doctrine DictionaryKey<T>`
- `struct List<T>`
- `struct Stack<T>`
- `struct Queue<T>`
- `struct RingQueue<T>`
- `enum DictionaryRemoveResult<T>`
- `struct Dictionary<K, V>`
- `struct HashSet<T>`
- `struct LinkedList<T>`

Top-level functions:

```stark
public inline finite law retborrow frozen T Lookup<T>(const T[] table, u64[0 2 ** 63 - 1] index);
public inline fn void SortBy<T>(
    mut borrow T[] values,
    inline closure<finite law Ordering(borrow T, borrow T) where overlap(arg0, arg1)> compare);
public inline fn void Sort<T>(mut borrow T[] values) where T: Ord;
```

Member functions:

`Eq`

```stark
finite law bool Equals(borrow Self left, borrow Self right) where overlap(left, right);
```

`Hash`

```stark
alias Code = u64[0 max];
finite law Self.Code Hash(borrow Self value);
```

`Ord`

```stark
finite law Ordering Compare(borrow Self left, borrow Self right) where overlap(left, right);
```

`Format`

```stark
alias Writer;
finite law System.Memory.MemoryStatus Format(borrow Self value, mut borrow Self.Writer writer);
```

`Equatable<T>`

```stark
finite law bool Equals(borrow T left, borrow T right) where overlap(left, right);
```

`Hashable<T>`

```stark
finite law u64[0 max] Hash(borrow T value);
```

`DictionaryKey<T>`

```stark
finite law bool Equals(borrow T left, borrow T right) where overlap(left, right);
finite law u64[0 max] Hash(borrow T value);
```

`List<T>`

```stark
List();
inline finite law u64[0 2 ** 63 - 1] Count(borrow List<T> self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow List<T> self);
inline finite law bool IsEmpty(borrow List<T> self);
fn System.Memory.MemoryStatus Reserve(mut borrow List<T> self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus Push(mut borrow List<T> self, T value);
fn bool TryPop(mut borrow List<T> self, out T value);
law retborrow T Get(borrow List<T> self, u64[0 2 ** 63 - 1] index);
fn retborrow mut T GetMut(mut borrow List<T> self, u64[0 2 ** 63 - 1] index);
finite law retborrow T[] AsSlice(borrow List<T> self);
finite retborrow mut T[] AsMutableSlice(mut borrow List<T> self);
fn void Clear(mut borrow List<T> self);
```

`Stack<T>`

```stark
Stack();
inline finite law u64[0 2 ** 63 - 1] Count(borrow Stack<T> self);
inline finite law bool IsEmpty(borrow Stack<T> self);
fn System.Memory.MemoryStatus Reserve(mut borrow Stack<T> self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus Push(mut borrow Stack<T> self, T value);
fn bool TryPop(mut borrow Stack<T> self, out T value);
law retborrow T Peek(borrow Stack<T> self);
fn void Clear(mut borrow Stack<T> self);
```

`Queue<T>`

```stark
Queue();
Queue(System.Memory.Allocator allocator);
inline finite law u64[0 2 ** 63 - 1] Count(borrow Queue<T> self);
inline finite law bool IsEmpty(borrow Queue<T> self);
fn System.Memory.MemoryStatus Reserve(mut borrow Queue<T> self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus Enqueue(mut borrow Queue<T> self, T value);
fn bool TryDequeue(mut borrow Queue<T> self, out T value);
law retborrow T Peek(borrow Queue<T> self);
fn void Clear(mut borrow Queue<T> self);
```

`RingQueue<T>`

```stark
RingQueue();
inline finite law u64[0 2 ** 63 - 1] Count(borrow RingQueue<T> self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow RingQueue<T> self);
inline finite law bool IsEmpty(borrow RingQueue<T> self);
fn System.Memory.MemoryStatus Reserve(mut borrow RingQueue<T> self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus Enqueue(mut borrow RingQueue<T> self, T value);
fn bool TryDequeue(mut borrow RingQueue<T> self, out T value);
law retborrow T Peek(borrow RingQueue<T> self);
fn void Clear(mut borrow RingQueue<T> self);
```

`Dictionary<K, V>`

```stark
Dictionary();
Dictionary(System.Memory.Allocator allocator);
inline finite law u64[0 2 ** 63 - 1] Count(borrow Dictionary<K, V> self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow Dictionary<K, V> self);
inline finite law bool IsEmpty(borrow Dictionary<K, V> self);
inline finite law bool ContainsIndex(borrow Dictionary<K, V> self, u64[0 2 ** 63 - 1] index);
law retborrow V GetAtIndex(borrow Dictionary<K, V> self, u64[0 2 ** 63 - 1] index);
fn retborrow mut V GetMutAtIndex(mut borrow Dictionary<K, V> self, u64[0 2 ** 63 - 1] index);
overlap(self, values), overlap(self, states);
inline finite law u64[0 2 ** 63 - 1] FindIndex(borrow Dictionary<K, V> self, borrow K key) where overlap(self, key);
fn System.Memory.MemoryStatus Reserve(mut borrow Dictionary<K, V> self, u64[0 2 ** 63 - 1] additional);
inline fn System.Memory.MemoryStatus Set(mut borrow Dictionary<K, V> self, K key, V value);
inline finite law bool ContainsKey(borrow Dictionary<K, V> self, borrow K key) where overlap(self, key);
inline fn bool TryGet(borrow Dictionary<K, V> self, borrow K key, out V value) where overlap(self, key), overlap(key, value);
inline fn DictionaryRemoveResult<V> RemoveMove(mut borrow Dictionary<K, V> self, borrow K key) where overlap(self, key);
inline fn bool TryRemove(mut borrow Dictionary<K, V> self, borrow K key, out V value) where overlap(self, key), overlap(key, value);
inline fn bool Remove(mut borrow Dictionary<K, V> self, borrow K key) where overlap(self, key);
fn void Clear(mut borrow Dictionary<K, V> self);
```

`HashSet<T>`

```stark
HashSet();
HashSet(System.Memory.Allocator allocator);
inline finite law u64[0 2 ** 63 - 1] Count(borrow HashSet<T> self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow HashSet<T> self);
inline finite law bool IsEmpty(borrow HashSet<T> self);
inline finite law u64[0 2 ** 63 - 1] FindIndex(borrow HashSet<T> self, borrow T value) where overlap(self, value);
fn System.Memory.MemoryStatus Reserve(mut borrow HashSet<T> self, u64[0 2 ** 63 - 1] additional);
inline fn System.Memory.MemoryStatus Add(mut borrow HashSet<T> self, T value);
inline finite law bool Contains(borrow HashSet<T> self, borrow T value) where overlap(self, value);
inline fn bool Remove(mut borrow HashSet<T> self, borrow T value) where overlap(self, value);
fn void Clear(mut borrow HashSet<T> self);
```

`LinkedList<T>`

```stark
LinkedList();
inline finite law u64[0 2 ** 63 - 1] Count(borrow LinkedList<T> self);
inline finite law bool IsEmpty(borrow LinkedList<T> self);
fn System.Memory.MemoryStatus ReserveNodes(mut borrow LinkedList<T> self, u64[0 2 ** 63 - 1] count);
fn System.Memory.MemoryStatus AddFirst(mut borrow LinkedList<T> self, T value);
fn System.Memory.MemoryStatus AddLast(mut borrow LinkedList<T> self, T value);
fn bool TryRemoveFirst(mut borrow LinkedList<T> self, out T value);
fn bool TryRemoveLast(mut borrow LinkedList<T> self, out T value);
fn void Clear(mut borrow LinkedList<T> self);
```


### System.Console

Source: `stdlib/src/System/Console.stark`

Top-level functions:

```stark
public fn System.IO.IOStatus Write(ascii text);
public fn System.IO.IOStatus Write(unicode text);
public fn System.IO.IOStatus Write(mut borrow System.Text.OwnedAscii text);
public fn System.IO.IOStatus Write(mut borrow System.Text.OwnedUnicode text);
public fn System.IO.IOStatus Write(borrow i8[min max][] source);
public fn System.IO.IOStatus Write(borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus Write(borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus Write(borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus Write(borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteLine(ascii text);
public fn System.IO.IOStatus WriteLine(unicode text);
public fn System.IO.IOStatus WriteLine(mut borrow System.Text.OwnedAscii text);
public fn System.IO.IOStatus WriteLine(mut borrow System.Text.OwnedUnicode text);
public fn System.IO.IOStatus WriteLine(borrow i8[min max][] source);
public fn System.IO.IOStatus WriteLine(borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteLine(borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteLine(borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteLine(borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteError(ascii text);
public fn System.IO.IOStatus WriteError(unicode text);
public fn System.IO.IOStatus WriteError(mut borrow System.Text.OwnedAscii text);
public fn System.IO.IOStatus WriteError(mut borrow System.Text.OwnedUnicode text);
public fn System.IO.IOStatus WriteError(borrow i8[min max][] source);
public fn System.IO.IOStatus WriteErrorLine(ascii text);
public fn System.IO.IOStatus WriteErrorLine(unicode text);
public fn System.IO.IOStatus WriteErrorLine(mut borrow System.Text.OwnedAscii text);
public fn System.IO.IOStatus WriteErrorLine(mut borrow System.Text.OwnedUnicode text);
public fn System.IO.IOStatus WriteErrorLine(borrow i8[min max][] source);
public fn System.IO.IOStatus WriteErrorLine(borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteErrorLine(borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteErrorLine(borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteErrorLine(borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.Memory.MemoryResult<u64[0 2 ** 63 - 1]> ReadBytes(mut borrow System.Runtime.Buffer.DynamicByteBuffer destination, u64[0 2 ** 63 - 1] maxCount);
public fn System.Memory.MemoryResult<u64[0 2 ** 63 - 1]> ReadBytes(mut borrow System.Runtime.Buffer.FixedByteBuffer512 destination, u64[0 2 ** 63 - 1] maxCount);
public fn System.Memory.MemoryResult<u64[0 2 ** 63 - 1]> ReadBytes(mut borrow System.Runtime.Buffer.FixedByteBuffer4096 destination, u64[0 2 ** 63 - 1] maxCount);
public fn System.Memory.MemoryResult<u64[0 2 ** 63 - 1]> ReadBytes(mut borrow System.Runtime.Buffer.FixedByteBuffer8192 destination, u64[0 2 ** 63 - 1] maxCount);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ReadAsciiLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadUnicodeLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> Read();
```


### System.FileSystem

Source: `stdlib/src/System/FileSystem.stark`

Public types:

- `enum FileSystemEntryKind`
- `struct FileSystemEntry`
- `struct FileSystemEntryInfo`
- `struct FileMetadata`
- `enum DirectoryReadResult`
- `enum DirectoryReadInfoResult`
- `struct Directory`

Top-level functions:

```stark
public fn System.IO.IOStatus CreateDirectory(ascii path);
public fn System.IO.IOStatus DeleteDirectory(ascii path);
public fn System.IO.IOStatus DeleteTree(ascii root);
public fn System.IO.IOStatus DeleteTreeIfExists(ascii root);
public fn System.IO.IOResult<Directory> OpenDirectory(ascii path);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOResult<bool> IsFile(ascii path);
public fn System.IO.IOResult<bool> IsDirectory(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<FileMetadata> Metadata(ascii path);
public fn System.IO.IOResult<System.Text.OwnedAscii> CreateTempDirectoryIn(ascii parent, ascii prefix);
public fn System.IO.IOResult<System.Text.OwnedAscii> CreateTempDirectory(ascii prefix);
public fn System.IO.IOStatus WalkRecursive(ascii root, inline closure<fn System.IO.IOStatus(ascii, FileSystemEntryKind)> visitor);
public fn System.IO.IOStatus Glob(ascii root, ascii pattern, inline closure<fn System.IO.IOStatus(ascii, FileSystemEntryKind)> visitor) where overlap(root, pattern);
```

Member functions:

`FileSystemEntry`

```stark
finite ascii NameView(mut borrow FileSystemEntry self);
```

`Directory`

```stark
finite law bool IsOpen(borrow Directory self);
inline fn System.IO.IOStatus Close(mut borrow Directory self);
fn DirectoryReadInfoResult ReadNextInfo(mut borrow Directory self);
fn DirectoryReadResult ReadNext(mut borrow Directory self);
```

`FileMetadata`

```stark
inline finite law bool IsExecutable(borrow FileMetadata self);
```


### System.IO

Source: `stdlib/src/System/IO.stark`

Public types:

- `enum IOError`
- `enum IOResult<T>`
- `enum IOStatus`


### System.IO.File

Source: `stdlib/src/System/IO/File.stark`

Public types:

- `enum FileMode`
- `enum FileBuffering`
- `enum SeekOrigin`
- `enum FileLineReadResult`
- `struct File`

Top-level functions:

```stark
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, FileBuffering buffering);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding, FileBuffering buffering);
public fn System.IO.IOStatus ReadAllBytesInto(ascii path, mut borrow System.Runtime.Buffer.DynamicByteBuffer destination);
public fn System.IO.IOResult<System.Runtime.Buffer.DynamicByteBuffer> ReadAllBytes(ascii path);
public fn System.IO.IOStatus ReadAllTextInto(ascii path, mut borrow System.Text.OwnedAscii destination);
public fn System.IO.IOResult<System.Text.OwnedAscii> ReadAllText(ascii path);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow i8[min max][] source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteAllBytes(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow i8[min max][] source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.DynamicByteBuffer source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
public fn System.IO.IOStatus WriteAllBytesAtomic(ascii path, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
public fn System.IO.IOStatus WriteAllText(ascii path, ascii text);
public fn System.IO.IOStatus WriteAllText(ascii path, unicode text);
public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, ascii text);
public fn System.IO.IOStatus WriteAllTextAtomic(ascii path, unicode text);
public fn System.IO.IOStatus Delete(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOStatus ReadLines(ascii path, inline closure<fn System.IO.IOStatus(ascii)> visitor);
```

Member functions:

`File`

```stark
inline fn bool IsOpen(borrow File self);
inline fn ascii BufferedAscii(mut borrow File self);
inline fn i32[min max] FlushBufferedWrite(mut borrow File self);
inline fn bool TryAppendBufferedAscii(mut borrow File self, ascii text);
unsafe inline fn bool TryAppendBufferedBytes(mut borrow File self, rawptr<i8[min max]>[count] data, u64[0 2 ** 63 - 1] count);
inline fn System.IO.IOStatus Close(mut borrow File self);
inline fn System.IO.IOStatus Flush(mut borrow File self);
inline fn System.IO.IOStatus SyncAll(mut borrow File self);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Seek(mut borrow File self, i64[min max] offset, SeekOrigin origin);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Read(mut borrow File self, mut borrow i8[min max][] destination);
fn FileLineReadResult ReadLine(mut borrow File self, mut borrow System.Text.OwnedAscii destination);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow i8[min max][] source);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow System.Runtime.Buffer.DynamicByteBuffer source);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
inline fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
fn System.IO.IOStatus WriteText(mut borrow File self, ascii text);
fn System.IO.IOStatus WriteText(mut borrow File self, unicode text);
fn System.IO.IOStatus WriteLine(mut borrow File self, ascii text);
fn System.IO.IOStatus WriteLine(mut borrow File self, unicode text);
```


### System.IO.Path

Source: `stdlib/src/System/IO/Path.stark`

Public types:

- `struct PathFacts`

Top-level functions:

```stark
public finite law ascii DirectorySeparator();
public finite law ascii AlternateDirectorySeparator();
public finite law ascii PathSeparator();
public fn System.Memory.MemoryStatus CurrentDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> CurrentDirectory();
public fn System.Memory.MemoryStatus TempDirectory(mut borrow System.Text.OwnedAscii destination);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempDirectory();
public finite law ascii ParentDirectory();
public finite law bool GlobMatches(ascii pattern, ascii path) where overlap(pattern, path);
public fn System.Memory.MemoryStatus TryTempName(mut borrow System.Text.OwnedAscii destination, ascii prefix, u64[0 max] attempt, ascii suffix) where overlap(destination, prefix), overlap(destination, suffix), overlap(prefix, suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempName(ascii prefix, u64[0 max] attempt, ascii suffix) where overlap(prefix, suffix);
public fn System.Memory.MemoryStatus TryTempPathIn(mut borrow System.Text.OwnedAscii destination, ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix) where overlap(destination, parent), overlap(destination, prefix), overlap(destination, suffix), overlap(parent, prefix), overlap(parent, suffix), overlap(prefix, suffix);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> TempPathIn(ascii parent, ascii prefix, u64[0 max] attempt, ascii suffix) where overlap(parent, prefix), overlap(parent, suffix), overlap(prefix, suffix);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii left, ascii right) where overlap(destination, left), overlap(destination, right), overlap(left, right);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third) where overlap(destination, first), overlap(destination, second), overlap(destination, third), overlap(first, second), overlap(first, third), overlap(second, third);
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii first, ascii second, ascii third, ascii fourth) where overlap(destination, first), overlap(destination, second), overlap(destination, third), overlap(destination, fourth), overlap(first, second), overlap(first, third), overlap(first, fourth), overlap(second, third), overlap(second, fourth), overlap(third, fourth);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii left, const ascii right) where overlap(left, right);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii first, const ascii second, const ascii third) where overlap(first, second), overlap(first, third), overlap(second, third);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii first, const ascii second, const ascii third, const ascii fourth) where overlap(first, second), overlap(first, third), overlap(first, fourth), overlap(second, third), overlap(second, fourth), overlap(third, fourth);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right) where overlap(left, right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii first, ascii second, ascii third) where overlap(first, second), overlap(first, third), overlap(second, third);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii first, ascii second, ascii third, ascii fourth) where overlap(first, second), overlap(first, third), overlap(first, fourth), overlap(second, third), overlap(second, fourth), overlap(third, fourth);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii left, const ascii right) where overlap(left, right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii first, const ascii second, const ascii third) where overlap(first, second), overlap(first, third), overlap(second, third);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii first, const ascii second, const ascii third, const ascii fourth) where overlap(first, second), overlap(first, third), overlap(first, fourth), overlap(second, third), overlap(second, fourth), overlap(third, fourth);
public fn System.Memory.MemoryStatus TryNormalizeSeparators(mut borrow System.Text.OwnedAscii destination, ascii path) where overlap(destination, path);
public fn System.Memory.MemoryStatus TryNormalizeSeparatorsConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparators(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparatorsConst(const ascii path);
public fn System.Memory.MemoryStatus TryNormalizeLexically(mut borrow System.Text.OwnedAscii destination, ascii path) where overlap(destination, path);
public fn System.Memory.MemoryStatus TryNormalizeLexicallyConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeLexically(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeLexicallyConst(const ascii path);
public fn System.Memory.MemoryStatus TryFullPath(mut borrow System.Text.OwnedAscii destination, ascii path) where overlap(destination, path);
public fn System.Memory.MemoryStatus TryFullPathConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> FullPath(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> FullPathConst(const ascii path);
public fn System.Memory.MemoryStatus TryChangeExtension(mut borrow System.Text.OwnedAscii destination, ascii path, ascii extension) where overlap(destination, path), overlap(destination, extension), overlap(path, extension);
public fn System.Memory.MemoryStatus TryChangeExtensionConst(mut borrow System.Text.OwnedAscii destination, const ascii path, const ascii extension) where overlap(path, extension);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ChangeExtension(ascii path, ascii extension) where overlap(path, extension);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ChangeExtensionConst(const ascii path, const ascii extension) where overlap(path, extension);
public finite law PathFacts GetFacts(ascii path);
public finite law PathFacts GetConstFacts(const ascii path);
public finite law ascii Extension(ascii path);
public finite law ascii ExtensionConst(const ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii BaseNameConst(const ascii path);
public finite law ascii DirectoryName(ascii path);
public finite law ascii DirectoryNameConst(const ascii path);
public finite law ascii RootName(ascii path);
public finite law ascii RootNameConst(const ascii path);
public finite law bool IsRooted(ascii path);
public finite law bool IsRootedConst(const ascii path);
public finite law bool IsAbsolute(ascii path);
public finite law bool IsAbsoluteConst(const ascii path);
public finite law bool IsRelative(ascii path);
public finite law bool IsRelativeConst(const ascii path);
```

Member functions:

`PathFacts`

```stark
inline finite law i64[min max] PathLength(borrow PathFacts self);
inline finite law i64[min max] ExtensionLength(borrow PathFacts self);
inline finite law ascii Extension(borrow PathFacts self);
inline finite law i64[min max] RootNameLength(borrow PathFacts self);
inline finite law ascii RootName(borrow PathFacts self);
inline finite law bool IsRooted(borrow PathFacts self);
inline finite law bool IsAbsolute(borrow PathFacts self);
inline finite law bool IsRelative(borrow PathFacts self);
inline finite law i64[min max] BaseNameLength(borrow PathFacts self);
inline finite law ascii BaseName(borrow PathFacts self);
inline finite law i64[min max] DirectoryNameLength(borrow PathFacts self);
inline finite law ascii DirectoryName(borrow PathFacts self);
```


### System.Math

Source: `stdlib/src/System/Math.stark`

Public types:

- `struct SinCosF32`
- `struct SinCosF64`
- `struct XorShift32`

Top-level functions:

```stark
public finite law f32 Sin(f32 value);
public finite law f64 Sin(f64 value);
public finite law f32 Cos(f32 value);
public finite law f64 Cos(f64 value);
public finite law f32 Tan(f32 value);
public finite law f64 Tan(f64 value);
public finite law f32 Exp(f32 value);
public finite law f64 Exp(f64 value);
public finite law f32 Exp2(f32 value);
public finite law f64 Exp2(f64 value);
public finite law f32 Log(f32 value);
public finite law f64 Log(f64 value);
public finite law f32 Log2(f32 value);
public finite law f64 Log2(f64 value);
public finite law f32 Log10(f32 value);
public finite law f64 Log10(f64 value);
public finite law f32 Asin(f32 value);
public finite law f64 Asin(f64 value);
public finite law f32 Acos(f32 value);
public finite law f64 Acos(f64 value);
public finite law f32 Atan(f32 value);
public finite law f64 Atan(f64 value);
public finite law f32 Atan2(f32 y, f32 x);
public finite law f64 Atan2(f64 y, f64 x);
public finite law f32 Pow(f32 value, f32 exponent);
public finite law f64 Pow(f64 value, f64 exponent);
public finite law f32 Sinh(f32 value);
public finite law f64 Sinh(f64 value);
public finite law f32 Cosh(f32 value);
public finite law f64 Cosh(f64 value);
public finite law f32 Tanh(f32 value);
public finite law f64 Tanh(f64 value);
public finite law SinCosF32 SinCos(f32 value);
public finite law SinCosF64 SinCos(f64 value);
public finite law f32 Sqrt(f32 value);
public finite law f64 Sqrt(f64 value);
public finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
public finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
public finite law f32 Ceiling(f32 value);
public finite law f64 Ceiling(f64 value);
public finite law f32 Floor(f32 value);
public finite law f64 Floor(f64 value);
public finite law f32 Truncate(f32 value);
public finite law f64 Truncate(f64 value);
public finite law f32 Round(f32 value);
public finite law f64 Round(f64 value);
public finite law f32 Min(f32 left, f32 right);
public finite law f64 Min(f64 left, f64 right);
public finite law f32 Max(f32 left, f32 right);
public finite law f64 Max(f64 left, f64 right);
public finite law f32 ReciprocalEstimate(f32 value);
public finite law f32 ReciprocalSqrtEstimate(f32 value);
```

Member functions:

`XorShift32`

```stark
XorShift32();
static inline fn XorShift32 Seeded(u32[0 max] seed);
inline fn void Reseed(mut borrow XorShift32 self, u32[0 max] seed);
inline finite law u32[0 max] CurrentState(borrow XorShift32 self);
inline fn u32[0 max] NextU32(mut borrow XorShift32 self);
inline fn i32[min max] NextI32(mut borrow XorShift32 self);
inline fn f32 NextF32(mut borrow XorShift32 self);
```


### System.Memory

Source: `stdlib/src/System/Memory.stark`

Public types:

- `enum MemoryError`
- `enum MemoryStatus`
- `enum MemoryResult<T>`
- `struct Allocator`

Top-level functions:

```stark
public inline finite law bool SupportsDynamicAllocator(Allocator allocator);
public inline fn MemoryStatus ReserveBytes(mut borrow dynamic i8[min max] storage, u64[0 2 ** 63 - 1] additional);
public inline fn MemoryStatus ReserveCodePoints(mut borrow dynamic i32[min max] storage, u64[0 2 ** 63 - 1] additional);
public inline fn MemoryStatus AppendBytesDisjoint(mut borrow dynamic i8[min max] storage, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count);
public fn MemoryStatus AppendBytes(mut borrow dynamic i8[min max] storage, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(storage, source);
public inline fn MemoryStatus AppendFillBytes(mut borrow dynamic i8[min max] storage, i8[min max] value, u64[0 2 ** 63 - 1] count);
public inline fn MemoryStatus AppendCodePointsDisjoint(mut borrow dynamic i32[min max] storage, borrow i32[min max][] source, u64[0 2 ** 63 - 1] count);
public fn MemoryStatus AppendCodePoints(mut borrow dynamic i32[min max] storage, borrow i32[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(storage, source);
public inline fn MemoryStatus AppendFillCodePoints(mut borrow dynamic i32[min max] storage, i32[min max] value, u64[0 2 ** 63 - 1] count);
public inline finite MemoryStatus InitializeBytesDisjoint(borrow i8[min max][] source, init i8[min max][] destination, u64[0 2 ** 63 - 1] count);
public inline finite MemoryStatus InitializeBytes(borrow i8[min max][] source, init i8[min max][] destination, u64[0 2 ** 63 - 1] count);
public inline finite void CopyBytesDisjointInfallible(borrow i8[min max][] source, borrow mut i8[min max][] destination, u64[0 2 ** 63 - 1] count);
public inline finite MemoryStatus CopyBytesDisjoint(borrow i8[min max][] source, borrow mut i8[min max][] destination, u64[0 2 ** 63 - 1] count);
public fn MemoryStatus CopyBytes(borrow i8[min max][] source, borrow mut i8[min max][] destination, u64[0 2 ** 63 - 1] count) where overlap(source, destination);
public inline finite void MoveBytesInfallible(borrow i8[min max][] source, borrow mut i8[min max][] destination, u64[0 2 ** 63 - 1] count) where overlap(source, destination);
public inline finite MemoryStatus MoveBytes(borrow i8[min max][] source, borrow mut i8[min max][] destination, u64[0 2 ** 63 - 1] count) where overlap(source, destination);
public inline finite MemoryStatus FillBytes(init i8[min max][] destination, i8[min max] value, u64[0 2 ** 63 - 1] count);
public inline finite void FillInitializedBytesInfallible(borrow mut i8[min max][] destination, i8[min max] value, u64[0 2 ** 63 - 1] count);
public inline finite MemoryStatus FillInitializedBytes(borrow mut i8[min max][] destination, i8[min max] value, u64[0 2 ** 63 - 1] count);
public inline finite MemoryStatus InitializeCodePointsDisjoint(borrow i32[min max][] source, init i32[min max][] destination, u64[0 2 ** 61 - 1] count);
public inline finite MemoryStatus InitializeCodePoints(borrow i32[min max][] source, init i32[min max][] destination, u64[0 2 ** 61 - 1] count);
public inline finite void CopyCodePointsDisjointInfallible(borrow i32[min max][] source, borrow mut i32[min max][] destination, u64[0 2 ** 61 - 1] count);
public inline finite MemoryStatus CopyCodePointsDisjoint(borrow i32[min max][] source, borrow mut i32[min max][] destination, u64[0 2 ** 63 - 1] count);
public fn MemoryStatus CopyCodePoints(borrow i32[min max][] source, borrow mut i32[min max][] destination, u64[0 2 ** 63 - 1] count) where overlap(source, destination);
public inline finite void MoveCodePointsInfallible(borrow i32[min max][] source, borrow mut i32[min max][] destination, u64[0 2 ** 61 - 1] count) where overlap(source, destination);
public inline finite MemoryStatus MoveCodePoints(borrow i32[min max][] source, borrow mut i32[min max][] destination, u64[0 2 ** 63 - 1] count) where overlap(source, destination);
public inline finite MemoryStatus FillCodePoints(init i32[min max][] destination, i32[min max] value, u64[0 2 ** 61 - 1] count);
public inline finite void FillInitializedCodePointsInfallible(borrow mut i32[min max][] destination, i32[min max] value, u64[0 2 ** 61 - 1] count);
public inline finite MemoryStatus FillInitializedCodePoints(borrow mut i32[min max][] destination, i32[min max] value, u64[0 2 ** 63 - 1] count);
```

Member functions:

`Allocator`

```stark
static inline finite law Allocator Default();
inline finite law bool IsDefault(borrow Allocator self);
```


### System.Net

Source: `stdlib/src/System/Net.stark`

Public types:

- `enum NetworkError`
- `enum NetStatus`
- `enum NetResult<T>`
- `struct IPv4Address`
- `struct IPv4Endpoint`


### System.Net.Tcp

Source: `stdlib/src/System/Net/Tcp.stark`

Public types:

- `enum TcpShutdown`
- `struct TcpClient`
- `struct TcpListener`

Member functions:

`TcpClient`

```stark
TcpClient();
static fn System.Net.NetResult<TcpClient> Connect(System.Net.IPv4Endpoint endpoint);
inline finite law bool IsOpen(borrow TcpClient self);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow i8[min max][] destination);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer512 destination);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer4096 destination);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer8192 destination);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.DynamicByteBuffer destination, u64[0 2 ** 63 - 1] maxCount);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow i8[min max][] source);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> ReadVectored(mut borrow TcpClient self, mut borrow i8[min max][] firstDestination, mut borrow i8[min max][] secondDestination);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> WriteVectored(mut borrow TcpClient self, borrow i8[min max][] firstSource, borrow i8[min max][] secondSource);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.DynamicByteBuffer source);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
inline fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
inline fn System.Net.NetStatus WaitReadable(mut borrow TcpClient self, i32[min max] timeoutMilliseconds);
inline fn System.Net.NetStatus WaitWritable(mut borrow TcpClient self, i32[min max] timeoutMilliseconds);
inline fn System.Net.NetStatus Shutdown(mut borrow TcpClient self, TcpShutdown how);
inline fn System.Net.NetStatus Close(mut borrow TcpClient self);
```

`TcpListener`

```stark
TcpListener();
static fn System.Net.NetResult<TcpListener> Listen(System.Net.IPv4Endpoint endpoint);
inline finite law bool IsOpen(borrow TcpListener self);
inline fn System.Net.NetResult<TcpClient> Accept(mut borrow TcpListener self);
inline fn System.Net.NetStatus WaitReadable(mut borrow TcpListener self, i32[min max] timeoutMilliseconds);
inline fn System.Net.NetStatus Close(mut borrow TcpListener self);
```


### System.Process

Source: `stdlib/src/System/Process.stark`

Top-level functions:

```stark
public fn i32[min max] CurrentId();
public fn void Exit(i32[min max] code);

public enum ProcessError;
public enum ProcessStatus;
public enum ProcessResult<T>;
public enum ProcessOption<T>;
public struct ProcessOutput;
public struct ProcessArguments;
public struct ProcessCommand;

public fn ProcessResult<ProcessCommand> Command(ascii executable);
public unsafe fn ProcessResult<ProcessOutput> RunCapture(mut borrow ProcessCommand command);
public unsafe fn ProcessResult<ProcessOutput> RunCapture(ascii executable);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithTimeout(mut borrow ProcessCommand command, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithTimeout(ascii executable, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithInput(mut borrow ProcessCommand command, ascii input);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithInput(ascii executable, ascii input);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithInputTimeout(mut borrow ProcessCommand command, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn ProcessResult<ProcessOutput> RunCaptureWithInputTimeout(ascii executable, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn ProcessResult<ProcessOption<System.Text.OwnedAscii>> GetEnvironment(ascii name);
public unsafe fn ProcessStatus SetEnvironment(ascii name, ascii value);
public unsafe fn ProcessStatus RemoveEnvironment(ascii name);
public fn ProcessResult<System.Text.OwnedAscii> CurrentDirectory();
public unsafe fn ProcessStatus SetCurrentDirectory(ascii path);
public unsafe fn ProcessResult<ProcessArguments> Arguments();
public unsafe fn ProcessResult<u64[0 2 ** 63 - 1]> ArgumentCount();

// ProcessCommand members
public fn ProcessStatus AddArgument(mut borrow ProcessCommand self, ascii argument);
public fn ProcessStatus SetWorkingDirectory(mut borrow ProcessCommand self, ascii path);

// ProcessOutput members
public inline finite law u64[0 2 ** 63 - 1] StdoutLength(borrow ProcessOutput self);
public inline finite law u64[0 2 ** 63 - 1] StderrLength(borrow ProcessOutput self);
public finite law retborrow i8[min max][] StdoutSlice(borrow ProcessOutput self);
public finite law retborrow i8[min max][] StderrSlice(borrow ProcessOutput self);
public inline finite law bool WasTimedOut(borrow ProcessOutput self);

// ProcessArguments members
public inline finite law u64[0 2 ** 63 - 1] Count(borrow ProcessArguments self);
public finite law retborrow System.Text.OwnedAscii[] AsSlice(borrow ProcessArguments self);
```


### System.Runtime.Buffer

Source: `stdlib/src/System/Runtime/Buffer.stark`

Public types:

- `struct FixedByteBuffer512`
- `struct FixedByteBuffer4096`
- `struct FixedByteBuffer8192`
- `struct DynamicByteBuffer`

Member functions:

`FixedByteBuffer512`

```stark
FixedByteBuffer512();
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow FixedByteBuffer512 self);
inline finite law bool IsEmpty(borrow FixedByteBuffer512 self);
inline finite law bool IsFull(borrow FixedByteBuffer512 self);
inline finite law u64[0 2 ** 63 - 1] Readable(borrow FixedByteBuffer512 self);
inline finite law u64[0 2 ** 63 - 1] Writable(borrow FixedByteBuffer512 self);
finite law retborrow i8[min max][] ReadSlice(borrow FixedByteBuffer512 self);
finite retborrow mut i8[min max][] ReadMutableSlice(mut borrow FixedByteBuffer512 self);
finite retborrow mut i8[min max][] WriteSlice(mut borrow FixedByteBuffer512 self);
fn System.Memory.MemoryStatus WriteByte(mut borrow FixedByteBuffer512 self, i8[min max] value);
fn System.Memory.MemoryStatus WriteSlice(mut borrow FixedByteBuffer512 self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(self, source);
fn System.Memory.MemoryStatus WriteFill(mut borrow FixedByteBuffer512 self, i8[min max] value, u64[0 2 ** 63 - 1] count);
fn void AdvanceRead(mut borrow FixedByteBuffer512 self, u64[0 2 ** 63 - 1] count);
fn void AdvanceWrite(mut borrow FixedByteBuffer512 self, u64[0 2 ** 63 - 1] count);
fn void Compact(mut borrow FixedByteBuffer512 self);
fn void Clear(mut borrow FixedByteBuffer512 self);
```

`FixedByteBuffer4096`

```stark
FixedByteBuffer4096();
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow FixedByteBuffer4096 self);
inline finite law bool IsEmpty(borrow FixedByteBuffer4096 self);
inline finite law bool IsFull(borrow FixedByteBuffer4096 self);
inline finite law u64[0 2 ** 63 - 1] Readable(borrow FixedByteBuffer4096 self);
inline finite law u64[0 2 ** 63 - 1] Writable(borrow FixedByteBuffer4096 self);
finite law retborrow i8[min max][] ReadSlice(borrow FixedByteBuffer4096 self);
finite retborrow mut i8[min max][] ReadMutableSlice(mut borrow FixedByteBuffer4096 self);
finite retborrow mut i8[min max][] WriteSlice(mut borrow FixedByteBuffer4096 self);
fn System.Memory.MemoryStatus WriteByte(mut borrow FixedByteBuffer4096 self, i8[min max] value);
fn System.Memory.MemoryStatus WriteSlice(mut borrow FixedByteBuffer4096 self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(self, source);
fn System.Memory.MemoryStatus WriteFill(mut borrow FixedByteBuffer4096 self, i8[min max] value, u64[0 2 ** 63 - 1] count);
fn void AdvanceRead(mut borrow FixedByteBuffer4096 self, u64[0 2 ** 63 - 1] count);
fn void AdvanceWrite(mut borrow FixedByteBuffer4096 self, u64[0 2 ** 63 - 1] count);
fn void Compact(mut borrow FixedByteBuffer4096 self);
fn void Clear(mut borrow FixedByteBuffer4096 self);
```

`FixedByteBuffer8192`

```stark
FixedByteBuffer8192();
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow FixedByteBuffer8192 self);
inline finite law bool IsEmpty(borrow FixedByteBuffer8192 self);
inline finite law bool IsFull(borrow FixedByteBuffer8192 self);
inline finite law u64[0 2 ** 63 - 1] Readable(borrow FixedByteBuffer8192 self);
inline finite law u64[0 2 ** 63 - 1] Writable(borrow FixedByteBuffer8192 self);
finite law retborrow i8[min max][] ReadSlice(borrow FixedByteBuffer8192 self);
finite retborrow mut i8[min max][] ReadMutableSlice(mut borrow FixedByteBuffer8192 self);
finite retborrow mut i8[min max][] WriteSlice(mut borrow FixedByteBuffer8192 self);
fn System.Memory.MemoryStatus WriteByte(mut borrow FixedByteBuffer8192 self, i8[min max] value);
fn System.Memory.MemoryStatus WriteSlice(mut borrow FixedByteBuffer8192 self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(self, source);
fn System.Memory.MemoryStatus WriteFill(mut borrow FixedByteBuffer8192 self, i8[min max] value, u64[0 2 ** 63 - 1] count);
fn void AdvanceRead(mut borrow FixedByteBuffer8192 self, u64[0 2 ** 63 - 1] count);
fn void AdvanceWrite(mut borrow FixedByteBuffer8192 self, u64[0 2 ** 63 - 1] count);
fn void Compact(mut borrow FixedByteBuffer8192 self);
fn void Clear(mut borrow FixedByteBuffer8192 self);
```

`DynamicByteBuffer`

```stark
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
fn System.Memory.MemoryStatus WriteSlice(mut borrow DynamicByteBuffer self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count) where overlap(self, source);
fn System.Memory.MemoryStatus WriteFill(mut borrow DynamicByteBuffer self, i8[min max] value, u64[0 2 ** 63 - 1] count);
fn bool TryReadByte(mut borrow DynamicByteBuffer self, out i8[min max] value);
fn void AdvanceRead(mut borrow DynamicByteBuffer self, u64[0 2 ** 63 - 1] count);
fn void Compact(mut borrow DynamicByteBuffer self);
fn void Clear(mut borrow DynamicByteBuffer self);
```


### System.Testing

Source: `stdlib/src/System/Testing.stark`

Public types:

- `enum TestStatus`
- `enum SnapshotResult`
- `struct SnapshotDifference`
- `enum DiagnosticSeverity`
- `struct DiagnosticLocation`
- `struct Diagnostic`
- `struct TempDirectory`

Member functions:

`TempDirectory`

```stark
finite ascii View(mut borrow TempDirectory self);
finite law bool IsActive(borrow TempDirectory self);
fn System.IO.IOResult<System.Text.OwnedAscii> PathFor(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
fn System.IO.IOStatus CreateDirectory(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
fn System.IO.IOStatus DeleteDirectory(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
fn System.IO.IOStatus WriteText(mut borrow TempDirectory self, ascii relativePath, ascii text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
fn System.IO.IOStatus WriteText(mut borrow TempDirectory self, ascii relativePath, unicode text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
fn System.IO.IOStatus WriteTextAtomic(mut borrow TempDirectory self, ascii relativePath, ascii text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
fn System.IO.IOStatus WriteTextAtomic(mut borrow TempDirectory self, ascii relativePath, unicode text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
fn System.IO.IOResult<System.Text.OwnedAscii> ReadText(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
fn System.IO.IOStatus DeleteFile(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
fn System.IO.IOStatus Cleanup(mut borrow TempDirectory self);
```

Top-level functions:

```stark
public fn System.IO.IOResult<TempDirectory> CreateTempDirectory(ascii prefix);
public finite law bool True(bool condition);
public finite law bool False(bool condition);
public fn bool Fail(ascii message);
public finite law bool Equal(bool expected, bool actual);
public finite law bool Equal(i32[min max] expected, i32[min max] actual);
public finite law bool Equal(i64[min max] expected, i64[min max] actual);
public finite law bool Equal(u32[0 max] expected, u32[0 max] actual);
public finite law bool Equal(u64[0 max] expected, u64[0 max] actual);
public finite law bool Equal(ascii expected, ascii actual);
public finite law bool Equal(unicode expected, unicode actual);
public finite law bool NotEqual(bool expected, bool actual);
public finite law bool NotEqual(i32[min max] expected, i32[min max] actual);
public finite law bool NotEqual(i64[min max] expected, i64[min max] actual);
public finite law bool NotEqual(u32[0 max] expected, u32[0 max] actual);
public finite law bool NotEqual(u64[0 max] expected, u64[0 max] actual);
public finite law bool NotEqual(ascii expected, ascii actual);
public finite law bool NotEqual(unicode expected, unicode actual);
public finite law bool InRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public finite law bool InRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public finite law bool InRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public finite law bool InRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);
public finite law bool NotInRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public finite law bool NotInRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public finite law bool NotInRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public finite law bool NotInRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);
public finite law bool Empty(ascii value);
public finite law bool Empty(unicode value);
public finite law bool Empty<T>(borrow T[] values);
public finite law bool Empty<T>(borrow System.Collections.List<T> values);
public finite law bool NotEmpty(ascii value);
public finite law bool NotEmpty(unicode value);
public finite law bool NotEmpty<T>(borrow T[] values);
public finite law bool NotEmpty<T>(borrow System.Collections.List<T> values);
public finite law bool Single<T>(borrow T[] values);
public finite law bool Single<T>(borrow System.Collections.List<T> values);
public finite law bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow T[] values);
public finite law bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow System.Collections.List<T> values);
public finite law bool Contains(ascii value, ascii expected) where overlap(value, expected);
public finite law bool Contains(unicode value, unicode expected) where overlap(value, expected);
public finite law bool DoesNotContain(ascii value, ascii expected) where overlap(value, expected);
public finite law bool DoesNotContain(unicode value, unicode expected) where overlap(value, expected);
public finite law bool StartsWith(ascii value, ascii expected) where overlap(value, expected);
public finite law bool StartsWith(unicode value, unicode expected) where overlap(value, expected);
public finite law bool EndsWith(ascii value, ascii expected) where overlap(value, expected);
public finite law bool EndsWith(unicode value, unicode expected) where overlap(value, expected);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(ascii value, ascii needle) where overlap(value, needle);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(unicode value, unicode needle) where overlap(value, needle);
public finite law bool Occurrences(u64[0 2 ** 63 - 1] expected, ascii value, ascii needle) where overlap(value, needle);
public finite law bool Occurrences(u64[0 2 ** 63 - 1] expected, unicode value, unicode needle) where overlap(value, needle);
public inline finite law bool DiagnosticCode(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticSeverityIs(DiagnosticSeverity expected, borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticStage(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticMessageEqual(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticMessageContains(borrow Diagnostic diagnostic, ascii expected) where overlap(diagnostic, expected);
public inline finite law bool DiagnosticHasLocation(borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticHasEndLocation(borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticFilePath(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticAt(borrow Diagnostic diagnostic, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column);
public inline finite law bool DiagnosticEndsAt(borrow Diagnostic diagnostic, u64[0 2 ** 63 - 1] endLine, u64[0 2 ** 63 - 1] endColumn);
public inline finite law bool DiagnosticMatches(borrow Diagnostic diagnostic, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains) where overlap(diagnostic, code), overlap(diagnostic, stage), overlap(diagnostic, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public inline finite law bool DiagnosticMatchesAt(borrow Diagnostic diagnostic, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column) where overlap(diagnostic, code), overlap(diagnostic, stage), overlap(diagnostic, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law bool DiagnosticsCount(u64[0 2 ** 63 - 1] expected, borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsEmpty(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsNotEmpty(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsContainCode(borrow Diagnostic[] diagnostics, ascii code) where overlap(diagnostics, code);
public finite law bool DiagnosticsContainMessage(borrow Diagnostic[] diagnostics, ascii messageContains) where overlap(diagnostics, messageContains);
public finite law bool DiagnosticsContain(borrow Diagnostic[] diagnostics, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains) where overlap(diagnostics, code), overlap(diagnostics, stage), overlap(diagnostics, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law bool DiagnosticsContainAt(borrow Diagnostic[] diagnostics, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column) where overlap(diagnostics, code), overlap(diagnostics, stage), overlap(diagnostics, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law u64[0 2 ** 63 - 1] DiagnosticsSeverityCount(DiagnosticSeverity severity, borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsErrorCount(borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsWarningCount(borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsInfoCount(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsHaveNoErrors(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsHaveErrors(borrow Diagnostic[] diagnostics);
public finite law bool TypeIs<TActual, TExpected>();
public finite law bool TypeIsBool<T>();
public finite law bool TypeIsInteger<T>();
public finite law bool TypeIsFloat<T>();
public finite law bool TypeIsRawPointer<T>();
public finite law bool TypeIsFixedArray<T>();
public finite law bool TypeIsSlice<T>();
public finite law bool TypeIsDynamic<T>();
public finite law bool TypeIsFunctionPointer<T>();
public finite law bool TypeIsClosure<T>();
public finite law bool TypeIsNamed<T>();
public finite law bool TypeIsStruct<T>();
public finite law bool TypeIsRecord<T>();
public finite law bool TypeIsEnum<T>();
public finite law bool TypeIsTrait<T>();
public finite law bool TypeIsDoctrine<T>();
public finite law bool TypeIsDynTrait<T>();
public finite law bool TypeHasConcreteLayout<T>();
public finite law bool TypeIsZeroSized<T>();
public finite law bool TypeSizeIs<T>(u64[0 max] expected);
public finite law bool TypeAlignIs<T>(u64[0 max] expected);
public finite law bool TypeDisplayName<T>(ascii expected);
public finite law bool TypeBaseName<T>(ascii expected);
public finite law bool TypeModuleName<T>(ascii expected);
public finite law bool TypeIsGenericInstantiation<T>();
public finite law bool TypeArgumentCount<T>(u64[0 max] expected);
public finite law bool TypeComptimeArgumentCount<T>(u64[0 max] expected);
public finite law bool OptionSome<T>(borrow System.Core.Option<T> value);
public finite law bool OptionNone<T>(borrow System.Core.Option<T> value);
public finite law bool ResultOk<T, E>(borrow System.Core.Result<T, E> value);
public finite law bool ResultErr<T, E>(borrow System.Core.Result<T, E> value);
public finite law bool ProcessExitCode(i32[min max] expected, borrow System.Process.ProcessOutput output);
public finite law bool ProcessTimedOut(borrow System.Process.ProcessOutput output);
public finite law bool ProcessCompleted(borrow System.Process.ProcessOutput output);
public finite law bool ProcessStdoutEqual(ascii expected, borrow System.Process.ProcessOutput output) where overlap(expected, output);
public finite law bool ProcessStderrEqual(ascii expected, borrow System.Process.ProcessOutput output) where overlap(expected, output);
public finite law bool ProcessStdoutContains(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrContains(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law u64[0 2 ** 63 - 1] ProcessStdoutCountOccurrences(borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law u64[0 2 ** 63 - 1] ProcessStderrCountOccurrences(borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStdoutOccurrences(u64[0 2 ** 63 - 1] expected, borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStderrOccurrences(u64[0 2 ** 63 - 1] expected, borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStdoutStartsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrStartsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStdoutEndsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrEndsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStdoutEmpty(borrow System.Process.ProcessOutput output);
public finite law bool ProcessStderrEmpty(borrow System.Process.ProcessOutput output);
public finite law bool ProcessOutputEqual(i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr, borrow System.Process.ProcessOutput output) where overlap(expectedStdout, expectedStderr), overlap(expectedStdout, output), overlap(expectedStderr, output);
public unsafe fn bool RunProcessMatches(mut borrow System.Process.ProcessCommand command, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(command, expectedStdout), overlap(command, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessMatchesWithInput(mut borrow System.Process.ProcessCommand command, ascii input, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(command, input), overlap(command, expectedStdout), overlap(command, expectedStderr), overlap(input, expectedStdout), overlap(input, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessTimesOut(mut borrow System.Process.ProcessCommand command, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn bool RunProcessTimesOutWithInput(mut borrow System.Process.ProcessCommand command, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds) where overlap(command, input);
public unsafe fn bool RunProcessMatches(ascii executable, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(executable, expectedStdout), overlap(executable, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessMatchesWithInput(ascii executable, ascii input, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(executable, input), overlap(executable, expectedStdout), overlap(executable, expectedStderr), overlap(input, expectedStdout), overlap(input, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessTimesOut(ascii executable, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn bool RunProcessTimesOutWithInput(ascii executable, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds) where overlap(executable, input);
public finite law TestStatus Status(bool assertion);
public fn u8[0 1] RunFact(ascii name, bool assertion);
public fn u8[0 1] RunFact(unicode name, bool assertion);
public fn u8[0 1] SkipFact(ascii name, ascii reason);
public fn u8[0 1] SkipFact(unicode name, unicode reason);
public finite law i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount);
public fn void Exit(u32[0 2 ** 31 - 1] failureCount);
public finite law SnapshotResult CompareSnapshotText(borrow System.Text.OwnedAscii expected, ascii actual) where overlap(expected, actual);
public fn SnapshotResult VerifySnapshot(ascii path, ascii actual);
public fn SnapshotResult UpdateSnapshot(ascii path, ascii actual);
public fn SnapshotResult VerifyOrUpdateSnapshot(ascii path, ascii actual, bool update);
public finite law bool SnapshotSucceeded(SnapshotResult result);
public fn System.Memory.MemoryStatus AppendSnapshotDifference(mut borrow System.Text.OwnedAscii writer, borrow SnapshotDifference difference);
```


### System.Text

Source: `stdlib/src/System/Text.stark`

Public types:

- `enum Encoding`
- `enum TextError`
- `enum TextResult<T>`
- `enum TextBuildError`
- `enum TextBuildResult<T>`
- `struct OwnedAscii`
- `struct OwnedUnicode`
- `struct OwnedUtf16`

Top-level functions:

```stark
public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law i64[min max] AsciiLength(ascii source);
public finite law i64[min max] UnicodeLength(unicode source);
public finite law bool StartsWith(ascii source, ascii prefix) where overlap(source, prefix);
public finite law bool StartsWith(unicode source, unicode prefix) where overlap(source, prefix);
public finite law bool EndsWith(ascii source, ascii suffix) where overlap(source, suffix);
public finite law bool EndsWith(unicode source, unicode suffix) where overlap(source, suffix);
public finite law bool Contains(ascii source, ascii needle) where overlap(source, needle);
public finite law bool Contains(unicode source, unicode needle) where overlap(source, needle);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(ascii source, ascii needle) where overlap(source, needle);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(unicode source, unicode needle) where overlap(source, needle);
public finite law bool AsciiBytesEqual(borrow i8[min max][] source, ascii expected) where overlap(source, expected);
public finite law bool AsciiBytesStartsWith(borrow i8[min max][] source, ascii prefix) where overlap(source, prefix);
public finite law bool AsciiBytesEndsWith(borrow i8[min max][] source, ascii suffix) where overlap(source, suffix);
public finite law bool AsciiBytesContains(borrow i8[min max][] source, ascii needle) where overlap(source, needle);
public finite law u64[0 2 ** 63 - 1] AsciiBytesCountOccurrences(borrow i8[min max][] source, ascii needle) where overlap(source, needle);
public fn System.Memory.MemoryStatus FromAscii(out OwnedAscii destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAscii(out OwnedAscii destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicode(out OwnedUnicode destination, unicode source);
public fn System.Memory.MemoryStatus FromConstUnicode(out OwnedUnicode destination, const unicode source);
public fn System.Memory.MemoryStatus FromAsciiToUnicode(out OwnedUnicode destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToAscii(out OwnedAscii destination, unicode source);
public fn TextBuildResult<OwnedAscii> EscapeAsciiForStringLiteral(ascii source);
public fn TextBuildResult<OwnedAscii> EscapeUnicodeForStringLiteral(unicode source);
public fn TextBuildResult<OwnedUnicode> DecodeStringLiteralToUnicode(ascii literal);
public fn TextBuildResult<OwnedAscii> DecodeStringLiteralToUtf8(ascii literal);
public fn TextBuildResult<i32[min max]> DecodeCharacterLiteralCodePoint(ascii literal);
public fn System.Memory.MemoryStatus FromAsciiToUtf16(out OwnedUtf16 destination, ascii source);
public fn System.Memory.MemoryStatus FromConstAsciiToUtf16(out OwnedUtf16 destination, const ascii source);
public fn System.Memory.MemoryStatus FromUnicodeToUtf16(out OwnedUtf16 destination, unicode source);
public finite TextResult<bool> ParseBoolAscii(ascii source);
public finite TextResult<bool> ParseBoolUnicode(unicode source);
public finite TextResult<Encoding> ParseEncodingAscii(ascii source);
public finite TextResult<Encoding> ParseEncodingUnicode(unicode source);
public finite TextResult<TextError> ParseTextErrorAscii(ascii source);
public finite TextResult<TextError> ParseTextErrorUnicode(unicode source);
public finite TextResult<i64[min max]> ParseI64Ascii(ascii source);
public finite TextResult<i64[min max]> ParseI64Unicode(unicode source);
public finite TextResult<u64[0 max]> ParseU64Ascii(ascii source);
public finite TextResult<u64[0 max]> ParseU64Unicode(unicode source);
public finite TextResult<i8[min max]> ParseI8Ascii(ascii source);
public finite TextResult<i8[min max]> ParseI8Unicode(unicode source);
public finite TextResult<i16[min max]> ParseI16Ascii(ascii source);
public finite TextResult<i16[min max]> ParseI16Unicode(unicode source);
public finite TextResult<i24[min max]> ParseI24Ascii(ascii source);
public finite TextResult<i24[min max]> ParseI24Unicode(unicode source);
public finite TextResult<i32[min max]> ParseI32Ascii(ascii source);
public finite TextResult<i32[min max]> ParseI32Unicode(unicode source);
public finite TextResult<i48[min max]> ParseI48Ascii(ascii source);
public finite TextResult<i48[min max]> ParseI48Unicode(unicode source);
public finite TextResult<u8[0 max]> ParseU8Ascii(ascii source);
public finite TextResult<u8[0 max]> ParseU8Unicode(unicode source);
public finite TextResult<u16[0 max]> ParseU16Ascii(ascii source);
public finite TextResult<u16[0 max]> ParseU16Unicode(unicode source);
public finite TextResult<u24[0 max]> ParseU24Ascii(ascii source);
public finite TextResult<u24[0 max]> ParseU24Unicode(unicode source);
public finite TextResult<u32[0 max]> ParseU32Ascii(ascii source);
public finite TextResult<u32[0 max]> ParseU32Unicode(unicode source);
public finite TextResult<u48[0 max]> ParseU48Ascii(ascii source);
public finite TextResult<u48[0 max]> ParseU48Unicode(unicode source);
public finite TextResult<i1024[min max]> ParseI1024Ascii(ascii source);
public finite TextResult<i1024[min max]> ParseI1024Unicode(unicode source);
public finite TextResult<u1024[0 max]> ParseU1024Ascii(ascii source);
public finite TextResult<u1024[0 max]> ParseU1024Unicode(unicode source);
public finite TextResult<i96[min max]> ParseI96Ascii(ascii source);
public finite TextResult<i96[min max]> ParseI96Unicode(unicode source);
public finite TextResult<i128[min max]> ParseI128Ascii(ascii source);
public finite TextResult<i128[min max]> ParseI128Unicode(unicode source);
public finite TextResult<i192[min max]> ParseI192Ascii(ascii source);
public finite TextResult<i192[min max]> ParseI192Unicode(unicode source);
public finite TextResult<i256[min max]> ParseI256Ascii(ascii source);
public finite TextResult<i256[min max]> ParseI256Unicode(unicode source);
public finite TextResult<i384[min max]> ParseI384Ascii(ascii source);
public finite TextResult<i384[min max]> ParseI384Unicode(unicode source);
public finite TextResult<i512[min max]> ParseI512Ascii(ascii source);
public finite TextResult<i512[min max]> ParseI512Unicode(unicode source);
public finite TextResult<i768[min max]> ParseI768Ascii(ascii source);
public finite TextResult<i768[min max]> ParseI768Unicode(unicode source);
public finite TextResult<u96[0 max]> ParseU96Ascii(ascii source);
public finite TextResult<u96[0 max]> ParseU96Unicode(unicode source);
public finite TextResult<u128[0 max]> ParseU128Ascii(ascii source);
public finite TextResult<u128[0 max]> ParseU128Unicode(unicode source);
public finite TextResult<u192[0 max]> ParseU192Ascii(ascii source);
public finite TextResult<u192[0 max]> ParseU192Unicode(unicode source);
public finite TextResult<u256[0 max]> ParseU256Ascii(ascii source);
public finite TextResult<u256[0 max]> ParseU256Unicode(unicode source);
public finite TextResult<u384[0 max]> ParseU384Ascii(ascii source);
public finite TextResult<u384[0 max]> ParseU384Unicode(unicode source);
public finite TextResult<u512[0 max]> ParseU512Ascii(ascii source);
public finite TextResult<u512[0 max]> ParseU512Unicode(unicode source);
public finite TextResult<u768[0 max]> ParseU768Ascii(ascii source);
public finite TextResult<u768[0 max]> ParseU768Unicode(unicode source);
public fn System.Memory.MemoryStatus FromUtf16ToUnicode(out OwnedUnicode destination, borrow OwnedUtf16 source);
public fn System.Memory.MemoryStatus FromUtf16ToAscii(out OwnedAscii destination, borrow OwnedUtf16 source);
public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right) where overlap(left, right);
public unsafe finite bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right) where overlap(left, right);
public unsafe finite bool TryFormatBoolAscii(rawmutptr<Ascii> destination, bool value);
public unsafe finite bool TryFormatEncodingAscii(rawmutptr<Ascii> destination, Encoding value);
public unsafe finite bool TryFormatTextErrorAscii(rawmutptr<Ascii> destination, TextError value);
public unsafe finite bool TryFormatI64Ascii(rawmutptr<Ascii> destination, i64[min max] value);
public unsafe finite bool TryFormatI1024Ascii(rawmutptr<Ascii> destination, i1024[min max] value);
public unsafe finite bool TryFormatI768Ascii(rawmutptr<Ascii> destination, i768[min max] value);
public unsafe finite bool TryFormatI512Ascii(rawmutptr<Ascii> destination, i512[min max] value);
public unsafe finite bool TryFormatI384Ascii(rawmutptr<Ascii> destination, i384[min max] value);
public unsafe finite bool TryFormatI256Ascii(rawmutptr<Ascii> destination, i256[min max] value);
public unsafe finite bool TryFormatI192Ascii(rawmutptr<Ascii> destination, i192[min max] value);
public unsafe finite bool TryFormatI128Ascii(rawmutptr<Ascii> destination, i128[min max] value);
public unsafe finite bool TryFormatI96Ascii(rawmutptr<Ascii> destination, i96[min max] value);
public unsafe finite bool TryFormatI48Ascii(rawmutptr<Ascii> destination, i48[min max] value);
public unsafe finite bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32[min max] value);
public unsafe finite bool TryFormatI24Ascii(rawmutptr<Ascii> destination, i24[min max] value);
public unsafe finite bool TryFormatI16Ascii(rawmutptr<Ascii> destination, i16[min max] value);
public unsafe finite bool TryFormatI8Ascii(rawmutptr<Ascii> destination, i8[min max] value);
public unsafe finite bool TryFormatU64Ascii(rawmutptr<Ascii> destination, u64[0 max] value);
public unsafe finite bool TryFormatU1024Ascii(rawmutptr<Ascii> destination, u1024[0 max] value);
public unsafe finite bool TryFormatU768Ascii(rawmutptr<Ascii> destination, u768[0 max] value);
public unsafe finite bool TryFormatU512Ascii(rawmutptr<Ascii> destination, u512[0 max] value);
public unsafe finite bool TryFormatU384Ascii(rawmutptr<Ascii> destination, u384[0 max] value);
public unsafe finite bool TryFormatU256Ascii(rawmutptr<Ascii> destination, u256[0 max] value);
public unsafe finite bool TryFormatU192Ascii(rawmutptr<Ascii> destination, u192[0 max] value);
public unsafe finite bool TryFormatU128Ascii(rawmutptr<Ascii> destination, u128[0 max] value);
public unsafe finite bool TryFormatU96Ascii(rawmutptr<Ascii> destination, u96[0 max] value);
public unsafe finite bool TryFormatU48Ascii(rawmutptr<Ascii> destination, u48[0 max] value);
public unsafe finite bool TryFormatU32Ascii(rawmutptr<Ascii> destination, u32[0 max] value);
public unsafe finite bool TryFormatU24Ascii(rawmutptr<Ascii> destination, u24[0 max] value);
public unsafe finite bool TryFormatU16Ascii(rawmutptr<Ascii> destination, u16[0 max] value);
public unsafe finite bool TryFormatU8Ascii(rawmutptr<Ascii> destination, u8[0 max] value);
public unsafe finite bool TryFormatF64Ascii(rawmutptr<Ascii> destination, f64 value);
public unsafe finite bool TryFormatF32Ascii(rawmutptr<Ascii> destination, f32 value);
public unsafe fn bool TryFormatBoolUnicode(rawmutptr<Unicode> destination, bool value);
public unsafe finite bool TryFormatEncodingUnicode(rawmutptr<Unicode> destination, Encoding value);
public unsafe finite bool TryFormatTextErrorUnicode(rawmutptr<Unicode> destination, TextError value);
public unsafe finite bool TryFormatI64Unicode(rawmutptr<Unicode> destination, i64[min max] value);
public unsafe finite bool TryFormatI1024Unicode(rawmutptr<Unicode> destination, i1024[min max] value);
public unsafe finite bool TryFormatI768Unicode(rawmutptr<Unicode> destination, i768[min max] value);
public unsafe finite bool TryFormatI512Unicode(rawmutptr<Unicode> destination, i512[min max] value);
public unsafe finite bool TryFormatI384Unicode(rawmutptr<Unicode> destination, i384[min max] value);
public unsafe finite bool TryFormatI256Unicode(rawmutptr<Unicode> destination, i256[min max] value);
public unsafe finite bool TryFormatI192Unicode(rawmutptr<Unicode> destination, i192[min max] value);
public unsafe finite bool TryFormatI128Unicode(rawmutptr<Unicode> destination, i128[min max] value);
public unsafe finite bool TryFormatI96Unicode(rawmutptr<Unicode> destination, i96[min max] value);
public unsafe finite bool TryFormatI48Unicode(rawmutptr<Unicode> destination, i48[min max] value);
public unsafe finite bool TryFormatI32Unicode(rawmutptr<Unicode> destination, i32[min max] value);
public unsafe finite bool TryFormatI24Unicode(rawmutptr<Unicode> destination, i24[min max] value);
public unsafe finite bool TryFormatI16Unicode(rawmutptr<Unicode> destination, i16[min max] value);
public unsafe finite bool TryFormatI8Unicode(rawmutptr<Unicode> destination, i8[min max] value);
public unsafe finite bool TryFormatU64Unicode(rawmutptr<Unicode> destination, u64[0 max] value);
public unsafe finite bool TryFormatU1024Unicode(rawmutptr<Unicode> destination, u1024[0 max] value);
public unsafe finite bool TryFormatU768Unicode(rawmutptr<Unicode> destination, u768[0 max] value);
public unsafe finite bool TryFormatU512Unicode(rawmutptr<Unicode> destination, u512[0 max] value);
public unsafe finite bool TryFormatU384Unicode(rawmutptr<Unicode> destination, u384[0 max] value);
public unsafe finite bool TryFormatU256Unicode(rawmutptr<Unicode> destination, u256[0 max] value);
public unsafe finite bool TryFormatU192Unicode(rawmutptr<Unicode> destination, u192[0 max] value);
public unsafe finite bool TryFormatU128Unicode(rawmutptr<Unicode> destination, u128[0 max] value);
public unsafe finite bool TryFormatU96Unicode(rawmutptr<Unicode> destination, u96[0 max] value);
public unsafe finite bool TryFormatU48Unicode(rawmutptr<Unicode> destination, u48[0 max] value);
public unsafe finite bool TryFormatU32Unicode(rawmutptr<Unicode> destination, u32[0 max] value);
public unsafe finite bool TryFormatU24Unicode(rawmutptr<Unicode> destination, u24[0 max] value);
public unsafe finite bool TryFormatU16Unicode(rawmutptr<Unicode> destination, u16[0 max] value);
public unsafe finite bool TryFormatU8Unicode(rawmutptr<Unicode> destination, u8[0 max] value);
public unsafe fn bool TryFormatF64Unicode(rawmutptr<Unicode> destination, f64 value);
public unsafe fn bool TryFormatF32Unicode(rawmutptr<Unicode> destination, f32 value);
public fn System.Memory.MemoryStatus ToAscii(out OwnedAscii destination, bool value);
public fn System.Memory.MemoryStatus ToAscii(out OwnedAscii destination, i64[min max] value);
public fn System.Memory.MemoryStatus ToAscii(out OwnedAscii destination, i32[min max] value);
public fn System.Memory.MemoryStatus ToAscii(out OwnedAscii destination, u64[0 max] value);
public fn System.Memory.MemoryStatus ToAscii(out OwnedAscii destination, u32[0 max] value);
public fn System.Memory.MemoryStatus ToUnicode(out OwnedUnicode destination, bool value);
public fn System.Memory.MemoryStatus ToUnicode(out OwnedUnicode destination, i64[min max] value);
public fn System.Memory.MemoryStatus ToUnicode(out OwnedUnicode destination, i32[min max] value);
public fn System.Memory.MemoryStatus ToUnicode(out OwnedUnicode destination, u64[0 max] value);
public fn System.Memory.MemoryStatus ToUnicode(out OwnedUnicode destination, u32[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(bool value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i8[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i16[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i24[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i32[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i48[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i64[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i96[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i128[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i192[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i256[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i384[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i512[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i768[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(i1024[min max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u8[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u16[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u24[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u32[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u48[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u64[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u96[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u128[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u192[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u256[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u384[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u512[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u768[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(u1024[0 max] value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f64 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(f32 value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(Encoding value);
public fn System.Memory.MemoryResult<OwnedAscii> ToAscii(TextError value);
public fn System.Memory.MemoryResult<OwnedAscii> ConcatAscii(ascii left, u64[0 2 ** 63 - 1] leftLength, System.Memory.MemoryResult<OwnedAscii> right);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(bool value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i8[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i16[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i24[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i32[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i48[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i64[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i96[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i128[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i192[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i256[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i384[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i512[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i768[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(i1024[min max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u8[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u16[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u24[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u32[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u48[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u64[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u96[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u128[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u192[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u256[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u384[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u512[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u768[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(u1024[0 max] value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f64 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(f32 value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(Encoding value);
public fn System.Memory.MemoryResult<OwnedUnicode> ToUnicode(TextError value);
public fn System.Memory.MemoryResult<OwnedUnicode> ConcatUnicode(unicode left, u64[0 2 ** 63 - 1] leftLength, System.Memory.MemoryResult<OwnedUnicode> right);
```

Member functions:

`OwnedAscii`

```stark
OwnedAscii();
inline finite law u64[0 2 ** 63 - 1] Length(borrow OwnedAscii self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow OwnedAscii self);
inline finite law bool IsEmpty(borrow OwnedAscii self);
finite law retborrow i8[min max][] AsSlice(borrow OwnedAscii self);
finite retborrow mut i8[min max][] AsMutableSlice(mut borrow OwnedAscii self);
finite ascii View(mut borrow OwnedAscii self);
fn System.Memory.MemoryStatus Reserve(mut borrow OwnedAscii self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus AppendByte(mut borrow OwnedAscii self, i8[min max] value);
fn System.Memory.MemoryStatus AppendSlice(mut borrow OwnedAscii self, borrow i8[min max][] source, u64[0 2 ** 63 - 1] count);
fn System.Memory.MemoryStatus AppendConstAscii(mut borrow OwnedAscii self, const ascii source);
fn System.Memory.MemoryStatus AppendAscii(mut borrow OwnedAscii self, ascii source) where overlap(self, source);
fn System.Memory.MemoryStatus AppendBool(mut borrow OwnedAscii self, bool value);
fn System.Memory.MemoryStatus AppendI64(mut borrow OwnedAscii self, i64[min max] value);
fn System.Memory.MemoryStatus AppendU64(mut borrow OwnedAscii self, u64[0 max] value);
fn System.Memory.MemoryStatus AppendCodePointAsUtf8(mut borrow OwnedAscii self, i32[min max] codePoint);
fn System.Memory.MemoryStatus AppendUnicodeAsUtf8(mut borrow OwnedAscii self, unicode source);
fn void Clear(mut borrow OwnedAscii self);
fn void Truncate(mut borrow OwnedAscii self, u64[0 2 ** 63 - 1] length);
```

`OwnedUnicode`

```stark
OwnedUnicode();
inline finite law u64[0 2 ** 63 - 1] Length(borrow OwnedUnicode self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow OwnedUnicode self);
inline finite law bool IsEmpty(borrow OwnedUnicode self);
finite law retborrow i32[min max][] AsSlice(borrow OwnedUnicode self);
finite retborrow mut i32[min max][] AsMutableSlice(mut borrow OwnedUnicode self);
finite unicode View(mut borrow OwnedUnicode self);
fn System.Memory.MemoryStatus Reserve(mut borrow OwnedUnicode self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus AppendCodePoint(mut borrow OwnedUnicode self, i32[min max] value);
fn System.Memory.MemoryStatus AppendSlice(mut borrow OwnedUnicode self, borrow i32[min max][] source, u64[0 2 ** 63 - 1] count);
fn System.Memory.MemoryStatus AppendConstUnicode(mut borrow OwnedUnicode self, const unicode source);
fn System.Memory.MemoryStatus AppendUnicode(mut borrow OwnedUnicode self, unicode source) where overlap(self, source);
fn System.Memory.MemoryStatus AppendAscii(mut borrow OwnedUnicode self, ascii source) where overlap(self, source);
fn System.Memory.MemoryStatus AppendConstAscii(mut borrow OwnedUnicode self, const ascii source);
fn System.Memory.MemoryStatus AppendBool(mut borrow OwnedUnicode self, bool value);
fn System.Memory.MemoryStatus AppendI64(mut borrow OwnedUnicode self, i64[min max] value);
fn System.Memory.MemoryStatus AppendU64(mut borrow OwnedUnicode self, u64[0 max] value);
fn void Clear(mut borrow OwnedUnicode self);
```

`OwnedUtf16`

```stark
OwnedUtf16();
inline finite law u64[0 2 ** 63 - 1] Length(borrow OwnedUtf16 self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow OwnedUtf16 self);
inline finite law bool IsEmpty(borrow OwnedUtf16 self);
finite law retborrow i16[min max][] AsSlice(borrow OwnedUtf16 self);
finite retborrow mut i16[min max][] AsMutableSlice(mut borrow OwnedUtf16 self);
fn System.Memory.MemoryStatus Reserve(mut borrow OwnedUtf16 self, u64[0 2 ** 63 - 1] additional);
fn System.Memory.MemoryStatus AppendCodeUnit(mut borrow OwnedUtf16 self, i16[min max] value);
fn System.Memory.MemoryStatus AppendCodePoint(mut borrow OwnedUtf16 self, i32[min max] codePoint);
fn System.Memory.MemoryStatus AppendAscii(mut borrow OwnedUtf16 self, ascii source);
fn System.Memory.MemoryStatus AppendConstAscii(mut borrow OwnedUtf16 self, const ascii source);
fn System.Memory.MemoryStatus AppendUnicode(mut borrow OwnedUtf16 self, unicode source);
fn void Clear(mut borrow OwnedUtf16 self);
```


### System.Threading

Source: `stdlib/src/System/Threading.stark`

Public types:

- `enum ThreadError`
- `enum ThreadStatus`
- `enum ThreadJoinResult`
- `enum ChannelError`
- `enum ChannelStatus`
- `enum ChannelSenderResult<T>`
- `enum ChannelReceiverResult<T>`
- `enum ChannelReceiveResult<T>`
- `struct Thread`
- `struct Synchronized<T>`
- `struct Locked<T>`
- `struct Channel<T>`
- `struct Sender<T>`
- `struct Receiver<T>`
- `struct AtomicBool`
- `struct AtomicI8` … `struct AtomicI1024` and `struct AtomicU8` … `struct AtomicU1024`
  (one atomic struct per Stark integer width: 8, 16, 24, 32, 48, 64, 96, 128, 192,
  256, 384, 512, 768, 1024)

Top-level functions:

```stark
public alias ThreadEntry = fnptr<fn i32[min max]()>;
public alias ThreadPayloadEntry<T> = fnptr<fn i32[min max](T)>;
```

Member functions:

`Thread`

```stark
Thread(ThreadEntry entry);
finite law bool IsJoinable(borrow Thread self);
fn ThreadJoinResult Join(mut borrow Thread self);
fn ThreadStatus Detach(mut borrow Thread self);
static fn Thread Start<T>(ThreadPayloadEntry<T> entry, T payload)
    where Transferable(T);
static fn void Yield();
static fn void SleepMilliseconds(u64[0 2 ** 63 - 1] milliseconds);
```

`Synchronized<T>` / `Locked<T>`

```stark
Synchronized(T initial);
fn Locked<T> Lock(storeborrow mut Synchronized<T> self);

fn retborrow mut T Value(mut borrow Locked<T> self);
mut drop;
```

`Channel<T>` / `Sender<T>` / `Receiver<T>`

```stark
Channel();
fn ChannelSenderResult<T> CreateSender(storeborrow mut Channel<T> self);
fn ChannelReceiverResult<T> CreateReceiver(storeborrow mut Channel<T> self);
fn u64[0 2 ** 63 - 1] PendingCount(storeborrow mut Channel<T> self);

fn ChannelStatus Send(mut borrow Sender<T> self, T value)
    where Transferable(T);
fn ChannelStatus Close(mut borrow Sender<T> self);
mut drop;

fn ChannelReceiveResult<T> Receive(mut borrow Receiver<T> self);
fn ChannelStatus Close(mut borrow Receiver<T> self);
mut drop;
```

`AtomicI64` (the same shape repeats for every `AtomicI*`/`AtomicU*` width; all
operations are seq-cst; RMW operations return the previous value; `Add`/`Sub` wrap)

```stark
AtomicI64(i64[min max] initial);
fn i64[min max] Load(borrow AtomicI64 self);
fn void Store(mut borrow AtomicI64 self, i64[min max] value);
fn i64[min max] Add(mut borrow AtomicI64 self, i64[min max] operand);
fn i64[min max] Sub(mut borrow AtomicI64 self, i64[min max] operand);
fn i64[min max] And(mut borrow AtomicI64 self, i64[min max] operand);
fn i64[min max] Or(mut borrow AtomicI64 self, i64[min max] operand);
fn i64[min max] Xor(mut borrow AtomicI64 self, i64[min max] operand);
fn i64[min max] Exchange(mut borrow AtomicI64 self, i64[min max] value);
fn bool CompareExchange(mut borrow AtomicI64 self, i64[min max] expected, i64[min max] desired);
```

`AtomicBool`

```stark
AtomicBool(bool initial);
fn bool Load(borrow AtomicBool self);
fn void Store(mut borrow AtomicBool self, bool value);
fn bool Exchange(mut borrow AtomicBool self, bool value);
fn bool CompareExchange(mut borrow AtomicBool self, bool expected, bool desired);
```
