# `System.IO.File`

`System.IO.File` provides the current file IO slice.

## Public Types

```stark
public enum FileMode {
    Read,
    Write,
    Append,
    ReadWrite,
}

public struct File {
    fn bool IsOpen(File self);
    fn i32 Close(mut borrow File self);
    fn i32 Flush(mut borrow File self);
    fn i64 ReadBytes(mut borrow File self, rawptr<i8> buffer, i64 size, i64 count);
    fn i64 WriteBytes(mut borrow File self, rawptr<i8> buffer, i64 size, i64 count);
    fn void WriteText(mut borrow File self, ascii text);
    fn void WriteText(mut borrow File self, unicode text);
    fn void WriteLine(mut borrow File self, ascii text);
    fn void WriteLine(mut borrow File self, unicode text);
}
```

The owned `File` handle has a `mut drop` that closes the handle on scope exit.

## Top-Level Helpers

```stark
public fn File Open(ascii path, FileMode mode);
public fn File Open(ascii path, FileMode mode, System.Text.Encoding encoding);

public fn rawptr<i8> OpenRead(ascii path);
public fn rawptr<i8> OpenWrite(ascii path);
public fn rawptr<i8> OpenAppend(ascii path);

public fn i32 Close(rawptr<i8> handle);
public fn i32 Flush(rawptr<i8> handle);
public fn i64 ReadBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle);
public fn i64 WriteBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle);
public fn void WriteText(rawptr<i8> handle, ascii text);
public fn void WriteText(rawptr<i8> handle, unicode text);
public fn void WriteLine(rawptr<i8> handle, ascii text);
public fn void WriteLine(rawptr<i8> handle, unicode text);
public fn i32 Delete(ascii path);
public fn i32 Move(ascii oldPath, ascii newPath);
public fn bool Exists(ascii path);
```

## Example

```stark
import System
module App

fn void WriteOwned() {
    stack mut System.IO.File.File file = System.IO.File.Open("owned-test.txt", System.IO.File.FileMode.Write);
    file.WriteLine("Owned");
    return;
}
```

## Current Status

- Owned file handles and destructor-driven close are implemented.
- Raw-handle helpers remain available for compatibility and tests.
- On Linux, open/read/write/close/delete/move/exists now go through the internal syscall-backed file-descriptor boundary.
- `Exists` now uses the Linux `stat` boundary instead of probing with open/close.
- Raw-handle text helpers support both `ascii` and `unicode`.
- Owned file writes now support `None`, `Line`, and `Full` userspace buffering with an internal fixed-size buffer.
- The owned public file surface now supports both `ascii` and `unicode` text writes.
- Broader per-encoding file conversions and text-reading APIs remain future shared text-IO work.
