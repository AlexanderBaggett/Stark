# `System.Net`

`System.Net` is the root for networking APIs in the standard library.

The current implemented slice defines shared networking result/status/error
types plus concrete IPv4 address and endpoint value types. TCP is the first
networking child module after this foundation. HTTP is explicitly not part of
the Stark standard library; it should be built later as a package on top of
`System.Net.Tcp`.

## Public Surface

```stark
module System.Net

public enum NetworkError
{
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

public enum NetStatus
{
    Ok,
    Err(NetworkError),
}

public enum NetResult<T>
{
    Ok(T),
    Err(NetworkError),
}

public struct IPv4Address
{
    u8[0 max] A;
    u8[0 max] B;
    u8[0 max] C;
    u8[0 max] D;
}

public struct IPv4Endpoint
{
    IPv4Address Address;
    u16[0 max] Port;
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

- `System.Net` is implemented for the shared result/status/error model and IPv4
  value types.
- `System.Net.Tcp` is the only networking child module in the standard library.
  Its owned closed-handle shell, `TcpClient.Connect`, `TcpClient.Read`,
  `TcpClient.Write`, `TcpClient.Shutdown`, `TcpListener.Listen`,
  `TcpListener.Accept`, and platform socket-close boundary are implemented.
  Linux backs client connect, listener bind/listen/accept, read/write, and
  shutdown with direct syscalls. Windows backs the same TCP surface with
  Winsock.
