# `System.Console`

`System.Console` provides the current terminal output API.

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
```

## Behavior

- `Write` writes to stdout.
- `WriteError` writes to stderr.
- `WriteLine` and `WriteErrorLine` append `\n`.
- The current surface returns `System.IO.IOStatus` instead of `void`.

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
- Input APIs are not implemented yet.
- On Linux, the current `ascii` stdout/stderr path is syscall-backed through the internal platform layer.
- The `unicode` Linux console path is still transitional until the text-encoding boundary is finished.
