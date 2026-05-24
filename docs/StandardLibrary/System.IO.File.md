# `System.IO.File`

`System.IO.File` provides the current file IO slice.

## Public Types

```stark
public enum FileMode
{
    Read,
    Write,
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

public struct File
{
    finite law bool IsOpen(File self);
    fn System.IO.IOStatus Close(mut borrow File self);
    fn System.IO.IOStatus Flush(mut borrow File self);
    fn System.IO.IOStatus SyncAll(mut borrow File self);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Seek(mut borrow File self, i64[min max] offset, SeekOrigin origin);
    fn System.IO.IOResult<u64[0 2 ** 63 - 1]> Read(mut borrow File self, mut borrow i8[min max][] destination);
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
public fn System.IO.IOStatus Delete(ascii path);
public fn System.IO.IOStatus Move(ascii oldPath, ascii newPath);
public fn System.IO.IOResult<bool> Exists(ascii path);
```

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
- On Windows, seek uses `SetFilePointerEx` through the internal platform boundary.
- `Flush` drains Stark userspace buffering only; `SyncAll` is the explicit durable-storage sync boundary.
- `Exists` now uses the Linux `stat` boundary instead of probing with open/close.
- Owned file writes now support `None`, `Line`, and `Full` userspace buffering with an internal fixed-size buffer.
- The owned public file surface now supports both `ascii` and `unicode` text writes.
- Owned files now honor explicit `UTF8`, `UTF16`, and `UTF32` encodings for text writes.
- Text-reading APIs remain future work.
