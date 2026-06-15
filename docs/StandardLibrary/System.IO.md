# `System.IO`

`System.IO` holds the shared IO result and error types and re-exports IO submodules.

It re-exports:

- `System.IO.File`
- `System.IO.Path`

## Shared Types

```stark
public enum IOError
{
    NotFound,
    PermissionDenied,
    AlreadyExists,
    InvalidPath,
    BrokenPipe,
    DiskFull,
    NotADirectory,
    IsADirectory,
    DirectoryNotEmpty,
    TooManyLinks,
    Unknown(i32[min max]),
}

public enum IOResult<T>
{
    Ok(T),
    Err(IOError),
}

public enum IOStatus
{
    Ok,
    Err(IOError),
}
```

`IOError`, `IOStatus`, and related small enums should use the smallest sound
internal tag width. A small enum must not default to a 32-bit tag unless an
explicit ABI boundary requires that representation.

## Role And Concrete API Policy

`System.IO` remains the shared IO vocabulary and owned file-handle family.
Filesystem-wide operations such as directory listing and directory deletion move
to `System.FileSystem`.

The standard library should not add a general `Stream` abstraction in the first
post-`v1.0` expansion. Concrete owned types such as `System.IO.File.File`,
`System.Net.Tcp.TcpClient`, `System.FileSystem.Directory`, and owned buffers
come first. A stream interface can be reconsidered later if Stark has a
zero-cost static interface surface that does not force dynamic dispatch, hidden
allocation, or weaker optimizer facts.

## Function Kind Policy

`System.IO` itself mostly defines shared enum types. IO operations in child
modules should use ordinary `fn` whenever they touch the operating system,
mutate an owned handle, allocate storage, flush buffers, or depend on external
state.

Pure status classifiers and other value-only helpers may use `finite law` when
they always return and do not observe mutable external state.

## Example

```stark
import System
module App

finite law bool IsOk(System.IO.IOStatus status)
{
    switch (status)
    {
        case System.IO.IOStatus.Ok:
            return true;
        case System.IO.IOStatus.Err(var error):
            return false;
    }
}
```

## Current Status

- These shared types are implemented and used by `System.Console`.
- `System.IO.File` exposes owned file handles, slice/buffer-based byte IO,
  whole-file text/byte helpers, atomic whole-file replacement helpers, and
  line-oriented text reading; raw file handles are internal stdlib/platform
  boundaries.
- Directory-wide operations live in `System.FileSystem`.
