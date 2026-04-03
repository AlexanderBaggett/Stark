# `System.IO`

`System.IO` holds the shared IO result and error types and re-exports IO submodules.

It re-exports:

- `System.IO.File`
- `System.IO.Path`

## Shared Types

```stark
public enum IOError {
    NotFound,
    PermissionDenied,
    AlreadyExists,
    InvalidPath,
    BrokenPipe,
    DiskFull,
    Unknown(i32),
}

public enum IOResult<T> {
    Ok(T),
    Err(IOError),
}

public enum IOStatus {
    Ok,
    Err(IOError),
}
```

## Example

```stark
import System
module App

fn bool IsOk(System.IO.IOStatus status) {
    switch (status) {
        case System.IO.IOStatus.Ok:
            return true;
        case System.IO.IOStatus.Err(var error):
            return false;
    }
}
```

## Current Status

- These shared types are implemented and used by `System.Console`.
- `System.IO.File` still exposes a compatibility-oriented mixed surface today: owned file handles plus raw-handle helpers.
