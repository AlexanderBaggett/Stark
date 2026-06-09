# `System.IO.File`

`System.IO.File` provides the current file IO slice.

## Public Types

```stark
public enum FileMode
{
    Read,
    Write,
    CreateNew,
    Append,
    ReadWrite,
}

public enum FileBuffering
{
    None,
    Line,
    Full,
}

public enum SeekOrigin
{
    Begin,
    Current,
    End,
}

public enum FileLineReadResult
{
    Line,
    End,
    Err(System.IO.IOError),
}

public struct File
{
    finite law bool IsOpen(File self);
    fn System.IO.IOStatus Close(mut borrow File self);
    fn System.IO.IOStatus Flush(mut borrow File self);
    fn System.IO.IOStatus SyncAll(mut borrow File self);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Seek(mut borrow File self, i64[min max] offset, SeekOrigin origin);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Read(mut borrow File self, mut borrow i8[min max][] destination);
    fn FileLineReadResult ReadLine(mut borrow File self, mut borrow System.Text.OwnedAscii destination);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Write(mut borrow File self, borrow i8[min max][] source);
    fn System.IO.IOStatus WriteText(mut borrow File self, ascii text);
    fn System.IO.IOStatus WriteText(mut borrow File self, unicode text);
    fn System.IO.IOStatus WriteLine(mut borrow File self, ascii text);
    fn System.IO.IOStatus WriteLine(mut borrow File self, unicode text);
}
```

The owned `File` handle has a `mut drop` that closes the handle on scope exit.

`IsOpen` is `finite law` because it only reads local handle state and always
returns. The other file methods are ordinary `fn` because they mutate handle or
buffer state, perform IO, or depend on filesystem state.

## Top-Level Helpers

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

`ReadAllTextInto` and `ReadAllBytesInto` append to the destination supplied by
the caller. They do not clear it first. The returned-value forms allocate a fresh
owned destination and return it through `IOResult<T>`.

`File.ReadLine` clears and reuses the supplied `OwnedAscii` destination. It
returns `Line` for a line without the trailing newline, strips a trailing `\r`
before `\n`, and returns `End` after the last line has been consumed.
`ReadLines` opens a path and streams each line to the supplied callback without
building a full list of lines.

`FileMode.CreateNew` uses the platform exclusive-create boundary and returns
`IOError.AlreadyExists` when the target already exists. The atomic whole-file
helpers create a same-directory temporary file with `CreateNew`, write the full
payload, call `SyncAll`, close the handle, then publish with `Move`. On failure
they best-effort delete the temporary path. A reader should observe either the
old path contents or the new path contents, not a partially-written target file.

## Example

```stark
import System
module App

fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
{
    switch (result)
    {
        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
            return value;
        case System.IO.IOResult<System.IO.File.File>.Err(var error):
            return new();
    }
}

fn void WriteOwned()
{
    stack mut System.IO.File.File file =
        OpenOrEmpty(System.IO.File.Open("owned-test.txt", System.IO.File.FileMode.Write));
    file.WriteLine("Owned");
    return;
}
```

## Current Status

- Owned file handles and destructor-driven close are implemented.
- Raw-handle helpers are internal stdlib/platform handoff code rather than
  public APIs.
- On Linux, open/read/write/close/seek/delete/move/exists now go through the internal syscall-backed file-descriptor boundary.
- On Linux, macOS, and Windows, file open uses an internal error-preserving
  platform result so public `Open` can map `AlreadyExists`, `NotFound`,
  `PermissionDenied`, `DiskFull`, `BrokenPipe`, and `InvalidPath` instead of
  collapsing open failures to `Unknown`.
- On Windows, seek uses `SetFilePointerEx` through the internal platform boundary.
- `Flush` drains Stark userspace buffering only; `SyncAll` is the explicit durable-storage sync boundary.
- `Exists` now uses the Linux `stat` boundary instead of probing with open/close.
- Owned file writes now support `None`, `Line`, and `Full` userspace buffering with an internal fixed-size buffer.
- The owned public file surface now supports both `ascii` and `unicode` text writes.
- Owned files now honor explicit `UTF8`, `UTF16`, and `UTF32` encodings for text writes.
- Whole-file text and byte helpers now read through fixed 8192-byte stack buffers
  into `OwnedAscii` / `DynamicByteBuffer` destinations and write through owned
  file handles.
- Atomic whole-file text and byte helpers write through exclusive same-directory
  temporary files, synchronize the file contents, close the handle, then publish
  with `Move`.
- Line-oriented text reading now uses fixed 8192-byte stack chunks and a reused
  `OwnedAscii` destination so callers can process large files without allocating
  one object per line.
