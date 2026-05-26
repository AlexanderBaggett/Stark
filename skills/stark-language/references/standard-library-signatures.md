# Stark Standard Library Signatures

Generated from `stdlib/src/System` public modules. This reference lists public top-level functions plus public member functions/constructors on public structs, records, traits, and doctrines. It intentionally omits module-private and internal implementation helpers.

## Module Summary

- `System.BitOperations`: bit counting, leading/trailing zero counts, rotations, byte swaps, and powers of two.
- `System.Collections`: owned list, stack, queue, ring queue, dictionary, and linked list containers.
- `System.Console`: console input/output for text, byte buffers, and owned text containers.
- `System.FileSystem`: directories, file existence/type checks, directory iteration, and filesystem mutations.
- `System.IO`: shared IO result/status/error enums.
- `System.IO.File`: owned file handles, file open modes, buffering choices, byte/text reads, writes, close, delete, and move.
- `System.IO.Path`: path parsing and simple path queries.
- `System.Math`: scalar math helpers, trigonometry, rounding, min/max/clamp, and xorshift PRNG.
- `System.Memory`: reservation, append, copy, move, fill, allocation status/result contracts.
- `System.Net`: network result/status/error types and IPv4 endpoint values.
- `System.Net.Tcp`: owned blocking TCP clients/listeners, scalar and vectored I/O, waits, shutdown, and close.
- `System.Process`: process id and process exit helpers.
- `System.Runtime.Buffer`: fixed and dynamic byte buffers with read/write cursors and slices.
- `System.Testing`: test assertions and failure status helpers.
- `System.Text`: owned ASCII/Unicode/UTF-16 text, conversions, formatting, and encoding results.
- `System.Threading`: thread handles, thread start/join/detach, yield, and sleep.

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


### System.Collections

Source: `stdlib/src/System/Collections.stark`

Public types:

- `trait Equatable<T>`
- `trait Hashable<T>`
- `doctrine DictionaryKey<T>`
- `struct List<T>`
- `struct Stack<T>`
- `struct Queue<T>`
- `struct RingQueue<T>`
- `enum DictionaryRemoveResult<T>`
- `struct Dictionary<K, V>`
- `struct LinkedList<T>`

Top-level functions:

```stark
public inline finite law retborrow frozen T Lookup<T>(const T[] table, u64[0 2 ** 63 - 1] index);
```

Member functions:

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
- `enum DirectoryReadResult`
- `enum DirectoryReadInfoResult`
- `struct Directory`

Top-level functions:

```stark
public fn System.IO.IOStatus CreateDirectory(ascii path);
public fn System.IO.IOStatus DeleteDirectory(ascii path);
public fn System.IO.IOResult<Directory> OpenDirectory(ascii path);
public fn System.IO.IOResult<bool> Exists(ascii path);
public fn System.IO.IOResult<bool> IsFile(ascii path);
public fn System.IO.IOResult<bool> IsDirectory(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
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
- `struct File`

Top-level functions:

```stark
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, FileBuffering buffering);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding);
public fn System.IO.IOResult<File> Open(ascii path, FileMode mode, System.Text.Encoding encoding, FileBuffering buffering);
public fn System.IO.IOStatus Delete(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<bool> Exists(ascii path);
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
public finite law ascii ParentDirectory();
public fn System.Memory.MemoryStatus TryJoin(mut borrow System.Text.OwnedAscii destination, ascii left, ascii right) where overlap(destination, left), overlap(destination, right), overlap(left, right);
public fn System.Memory.MemoryStatus TryJoinConst(mut borrow System.Text.OwnedAscii destination, const ascii left, const ascii right) where overlap(left, right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> Join(ascii left, ascii right) where overlap(left, right);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> JoinConst(const ascii left, const ascii right) where overlap(left, right);
public fn System.Memory.MemoryStatus TryNormalizeSeparators(mut borrow System.Text.OwnedAscii destination, ascii path) where overlap(destination, path);
public fn System.Memory.MemoryStatus TryNormalizeSeparatorsConst(mut borrow System.Text.OwnedAscii destination, const ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparators(ascii path);
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> NormalizeSeparatorsConst(const ascii path);
public finite law PathFacts GetFacts(ascii path);
public finite law PathFacts GetConstFacts(const ascii path);
public finite law ascii Extension(ascii path);
public finite law ascii ExtensionConst(const ascii path);
public finite law ascii BaseName(ascii path);
public finite law ascii BaseNameConst(const ascii path);
public finite law ascii DirectoryName(ascii path);
public finite law ascii DirectoryNameConst(const ascii path);
```

Member functions:

`PathFacts`

```stark
inline finite law i64[min max] PathLength(borrow PathFacts self);
inline finite law i64[min max] ExtensionLength(borrow PathFacts self);
inline finite law ascii Extension(borrow PathFacts self);
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

Top-level functions:

```stark
public fn bool True(bool condition);
public fn bool False(bool condition);
public fn bool Fail(ascii message);
public fn bool Equal(bool expected, bool actual);
public fn bool Equal(i32[min max] expected, i32[min max] actual);
public fn bool Equal(i64[min max] expected, i64[min max] actual);
public fn bool Equal(u32[0 max] expected, u32[0 max] actual);
public fn bool Equal(u64[0 max] expected, u64[0 max] actual);
public fn bool Equal(ascii expected, ascii actual);
public fn bool Equal(unicode expected, unicode actual);
public fn TestStatus Status(bool assertion);
public fn u8[0 1] RunFact(ascii name, bool assertion);
public fn u8[0 1] RunFact(unicode name, bool assertion);
public fn i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount);
public fn void Exit(u32[0 2 ** 31 - 1] failureCount);
```


### System.Text

Source: `stdlib/src/System/Text.stark`

Public types:

- `enum Encoding`
- `enum TextError`
- `enum TextResult<T>`
- `struct OwnedAscii`
- `struct OwnedUnicode`
- `struct OwnedUtf16`

Top-level functions:

```stark
public finite law ascii AsciiView(Ascii source);
public finite law unicode UnicodeView(Unicode source);
public finite law i64[min max] AsciiLength(ascii source);
public finite law i64[min max] UnicodeLength(unicode source);
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
finite retborrow i8[min max][] AsSlice(borrow OwnedAscii self);
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
```

`OwnedUnicode`

```stark
OwnedUnicode();
inline finite law u64[0 2 ** 63 - 1] Length(borrow OwnedUnicode self);
inline finite law u64[0 2 ** 63 - 1] Capacity(borrow OwnedUnicode self);
inline finite law bool IsEmpty(borrow OwnedUnicode self);
finite retborrow i32[min max][] AsSlice(borrow OwnedUnicode self);
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
finite retborrow i16[min max][] AsSlice(borrow OwnedUtf16 self);
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
- `struct Thread`

Top-level functions:

```stark
public alias ThreadEntry = fnptr<fn i32[min max]()>;
```

Member functions:

`Thread`

```stark
Thread(ThreadEntry entry);
finite law bool IsJoinable(borrow Thread self);
fn ThreadJoinResult Join(mut borrow Thread self);
fn ThreadStatus Detach(mut borrow Thread self);
static fn void Yield();
static fn void SleepMilliseconds(u64[0 2 ** 63 - 1] milliseconds);
```
