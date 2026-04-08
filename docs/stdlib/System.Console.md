# `System.Console`

`System.Console` provides the current terminal input and output API.

## Surface

```stark
public fn System.IO.IOStatus Write(ascii text);
public fn System.IO.IOStatus Write(unicode text);
public fn System.IO.IOStatus WriteLine(ascii text);
public fn System.IO.IOStatus WriteLine(unicode text);
public fn System.IO.IOStatus WriteError(ascii text);
public fn System.IO.IOStatus WriteError(unicode text);
public fn System.IO.IOStatus WriteErrorLine(ascii text);
public fn System.IO.IOStatus WriteErrorLine(unicode text);
public fn Unicode ReadLine();
public fn Unicode Read();
```

## Behavior

- `Write` writes to stdout.
- `WriteError` writes to stderr.
- `WriteLine` and `WriteErrorLine` append `\n`.
- The current surface returns `System.IO.IOStatus` instead of `void`.
- `ReadLine` returns the next stdin line as `Unicode` without the trailing newline.
- `Read` returns the next stdin code point as a one-element `Unicode`.
- `ReadLine` and `Read` currently return empty `Unicode` on EOF or input failure.
- The current input implementation reuses fixed internal `Unicode` backing buffers instead of allocating fresh storage for each call.

## Error Model

- `IOStatus.Ok` means the underlying platform write returned a non-negative result.
- `IOStatus.Err(IOError.Unknown(code))` is used when the current platform boundary reports a negative result code.

## Example

```stark
import System
module App

export ffi fn i32 main() {
    switch (System.Console.WriteLine("Hello")) {
        case System.IO.IOStatus.Ok:
            return 0;
        case System.IO.IOStatus.Err(var error):
            return 1;
    }
}
```

## Current Status

- Output is implemented.
- Basic `ReadLine` and `Read` input are implemented.
- On Linux, the `ascii` and `unicode` stdout/stderr paths are syscall-backed through the internal platform layer, with `unicode` text encoded as UTF-8 before write.
- `ReadLine` and `Read` decode UTF-8 stdin through a shared buffered console-input handle.
