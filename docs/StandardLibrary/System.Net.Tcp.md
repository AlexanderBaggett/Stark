# `System.Net.Tcp`

`System.Net.Tcp` provides the minimal TCP client and listener surface.

The first version should be blocking and concrete. It should be enough to build
client libraries and simple servers without exposing raw OS sockets to ordinary
Stark code.

## Current Public Surface

```stark
import System.Net
module System.Net.Tcp

public enum TcpShutdown {
    Receive,
    Send,
    Both,
}

public struct TcpClient {
    TcpClient();
    static fn System.Net.NetResult<TcpClient> Connect(System.Net.IPv4Endpoint endpoint);
    finite law bool IsOpen(self);
    fn System.Net.NetResult<i64[0 max]> Read(mut self, mut borrow i8[] destination);
    fn System.Net.NetResult<i64[0 max]> Write(mut self, borrow i8[] source);
    fn System.Net.NetStatus Shutdown(mut self, TcpShutdown how);
    fn System.Net.NetStatus Close(mut self);
}

public struct TcpListener {
    TcpListener();
    static fn System.Net.NetResult<TcpListener> Listen(System.Net.IPv4Endpoint endpoint);
    finite law bool IsOpen(self);
    fn System.Net.NetResult<TcpClient> Accept(mut self);
    fn System.Net.NetStatus Close(mut self);
}
```

The current constructors create closed handles. They exist so owned lifecycle,
drop cleanup, package-image shape, and close semantics can land before platform
socket creation.

`TcpClient.Connect` is the implemented fallible client construction path. It
returns `NetResult<TcpClient>` so recoverable network failures stay explicit
instead of being hidden inside a constructor.

`TcpListener.Listen` mirrors that shape for listener construction. It returns
`NetResult<TcpListener>` instead of hiding bind/listen failures in a
constructor.

## Client Construction

```stark
stack System.Net.IPv4Endpoint endpoint = new() {
    Address = new() { A = 127, B = 0, C = 0, D = 1 },
    Port = 8080
};

stack System.Net.NetResult<System.Net.Tcp.TcpClient> connected =
    System.Net.Tcp.TcpClient.Connect(endpoint);
```

Linux backs this path with `socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0)` and
`connect`. Windows backs it with Winsock startup, `WSASocketW`, and `connect`.

## Listener Construction

Listener construction lives on `TcpListener`:

```stark
stack System.Net.IPv4Endpoint endpoint = new() {
    Address = new() { A = 127, B = 0, C = 0, D = 1 },
    Port = 8080
};

stack System.Net.NetResult<System.Net.Tcp.TcpListener> listening =
    System.Net.Tcp.TcpListener.Listen(endpoint);
```

Linux backs this path with `socket`, `bind`, and `listen`. Windows backs it
with Winsock startup, `WSASocketW`, `bind`, and `listen`.

## Accept

`TcpListener.Accept` accepts one incoming connection and returns a
`NetResult<TcpClient>`. The first version follows the module blocking policy:
it waits according to the platform socket semantics and reports recoverable
failures through `NetworkError`.

Linux backs this path with `accept4(..., SOCK_CLOEXEC)`. Windows backs it with
Winsock `accept`.

## Read And Write

`TcpClient.Read` and `TcpClient.Write` use caller-provided byte slices:

```stark
stack mut i8[min max][4] buffer = { 0, 0, 0, 0 };
stack System.Net.NetResult<i64[0 max]> read = client.Read(buffer);
stack System.Net.NetResult<i64[0 max]> written = client.Write(buffer);
```

They return `NetResult<i64[0 max]>`, where `Ok(count)` is the byte count and
`Err(error)` preserves recoverable socket failures. A zero-byte read is reported
as `Ok(0)`.

## Buffer Model

The low-level `Read` and `Write` methods use safe Stark slices, not raw
pointers. This keeps the minimal TCP surface efficient without forcing unsafe
FFI-style code into ordinary applications.

Allocation-backed convenience helpers can be added after `System.Memory` and
`System.Collections` land, for example methods that read into an owned byte
buffer or append to a `List<i8>`.

## Function Kinds

`IsOpen` is `finite law` on both TCP handle types because it only reads local
handle state and always returns.

`Connect`, `Read`, `Write`, `Accept`, `Shutdown`, and `Close` are ordinary `fn`
operations because they route through external network state.

## Ownership And Drop

`TcpClient` and `TcpListener` are owned handles.

- `Close` is available when callers need deterministic error handling or
  ordering.
- Dropping an open socket performs best-effort close.
- Methods after close return `NotConnected` or `InvalidState` according to the
  final error mapping.
- OS socket handles remain inside the platform layer.

## Blocking Policy

The first version is blocking.

Non-blocking mode, polling, epoll/kqueue/IOCP wrappers, async IO, and event
loops are deferred until Stark has a clearer concurrency and package story.

## Current Status

- `TcpShutdown`, closed-handle `TcpClient`, closed-handle `TcpListener`,
  `TcpClient.Connect`, `TcpClient.Read`, `TcpClient.Write`,
  `TcpListener.Listen`, `TcpListener.Accept`, `IsOpen`, `Shutdown`, `Close`,
  and best-effort drop cleanup are implemented.
- Linux `TcpClient.Connect` is backed by direct `socket` and `connect` syscalls.
- Linux `TcpListener.Listen` is backed by direct `socket`, `bind`, and `listen`
  syscalls.
- Linux `TcpListener.Accept` is backed by the direct `accept4` syscall with
  close-on-exec enabled.
- Linux `TcpClient.Read` and `TcpClient.Write` are backed by direct read/write
  syscall paths on the socket handle.
- Linux `TcpClient.Shutdown` is backed by the direct `shutdown` syscall.
- Linux socket close is backed by the existing `close(2)` syscall path.
- Windows socket construction, connect, bind/listen, accept, read/write,
  shutdown, and close are backed by Winsock. Windows executable linking adds
  the Winsock import library when the emitted LLVM references the WSA boundary.
