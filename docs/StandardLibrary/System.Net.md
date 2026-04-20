# `System.Net`

`System.Net` is the planned root for networking APIs in the standard library.

The first standard-library networking slice includes TCP only. HTTP is
explicitly not part of the Stark standard library; it should be built later as a
package on top of `System.Net.Tcp`.

## Planned Public Surface

```stark
export import System.Net.Tcp
module System.Net

public enum NetworkError {
    AddressInvalid,
    AddressInUse,
    ConnectionRefused,
    ConnectionReset,
    TimedOut,
    NotConnected,
    WouldBlock,
    Unsupported,
    Unknown(i32),
}

public enum NetStatus {
    Ok,
    Err(NetworkError),
}

public enum NetResult<T> {
    Ok(T),
    Err(NetworkError),
}

public struct IPv4Address {
    i8 [0 2**8 - 1] A;
    i8 [0 2**8 - 1] B;
    i8 [0 2**8 - 1] C;
    i8 [0 2**8 - 1] D;
}

public struct IPv4Endpoint {
    IPv4Address Address;
    i16 [0 2**16 - 1] Port;
}
```

Small networking enums should use appropriately small tags. Only payload cases
such as `Unknown(i32)` need larger payload storage.

## Non-Goals

The standard library will not include:

- `System.Net.Http`
- TLS
- DNS resolution beyond a deliberately scoped future helper
- async socket APIs
- an ASP.NET-style server framework

Those belong in packages once the package-management story exists.

## Current Status

- This is a planned `v1.2` module family.
- `System.Net.Tcp` is the only planned networking child module for the standard
  library.
