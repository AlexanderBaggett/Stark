# `System.Console`

`System.Console` provides the current terminal input and output API.

## Surface

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
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadLine();
public fn System.Memory.MemoryResult<System.Text.OwnedAscii> ReadAsciiLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadUnicodeLine();
public fn System.Memory.MemoryResult<System.Text.OwnedUnicode> Read();
```

## Behavior

- `Write` writes to stdout.
- `WriteError` writes to stderr.
- `WriteLine` and `WriteErrorLine` append `\n`.
- Byte-slice and byte-buffer overloads write the readable byte range.
- The current surface returns `System.IO.IOStatus` instead of `void`.
- `ReadBytes` appends bytes into a fixed or dynamic byte buffer and returns the
  number of bytes read.
- `ReadLine` returns the next stdin line as owned `Unicode` without the trailing newline.
- `ReadUnicodeLine` is an explicit-name alias for `ReadLine`.
- `ReadAsciiLine` returns the next stdin line as owned byte-oriented `Ascii` without the trailing newline.
- `Read` returns the next stdin code point as a one-element owned `Unicode`.
- `ReadLine`, `ReadUnicodeLine`, `ReadAsciiLine`, and `Read` return `System.Memory.MemoryResult<T>` so allocation and layout failures remain visible.
- End of input returns `Ok` with empty owned text.
- Owned input results can be kept by the caller; convert them to text views with `.View()` when passing them to APIs that accept `ascii` or `unicode`.

## Error Model

- `IOStatus.Ok` means the underlying platform write returned a non-negative result.
- `IOStatus.Err(IOError.Unknown(code))` is used when the current platform boundary reports a negative result code.

## Example

```stark
import System
module App

export fn i32 main()
{
    switch (System.Console.WriteLine("Hello"))
    {
        case System.IO.IOStatus.Ok:
            return 0;
        case System.IO.IOStatus.Err(var error):
            return 1;
    }
}
```

## Current Status

- Output is implemented.
- Basic `ReadLine`, `ReadUnicodeLine`, `ReadAsciiLine`, and `Read` input are implemented.
- On Linux, the `ascii` and `unicode` stdout/stderr paths are syscall-backed through the internal platform layer, with `unicode` text encoded as UTF-8 before write.
- `ReadLine` and `Read` decode UTF-8 stdin through a shared buffered console-input handle.
