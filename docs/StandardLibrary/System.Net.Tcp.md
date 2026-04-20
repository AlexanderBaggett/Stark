# `System.Net.Tcp`

`System.Net.Tcp` provides the minimal TCP client and listener surface.

The first version should be blocking and concrete. It should be enough to build
client libraries and simple servers without exposing raw OS sockets to ordinary
Stark code.

## Planned Public Surface

```stark
import System.Net
module System.Net.Tcp

public enum TcpShutdown {
    Receive,
    Send,
    Both,
}

public struct TcpClient {
    finite law bool IsOpen(self);
    fn System.Net.NetResult<i64[0 max]> Read(mut self, mut i8[] destination);
    fn System.Net.NetResult<i64[0 max]> Write(mut self, borrow i8[] source);
    fn System.Net.NetStatus Shutdown(mut self, TcpShutdown how);
    fn System.Net.NetStatus Close(mut self);
}

public struct TcpListener {
    finite law bool IsOpen(self);
    fn System.Net.NetResult<TcpClient> Accept(mut self);
    fn System.Net.NetStatus Close(mut self);
}
```

Construction should live on the structs:

```stark
stack System.Net.IPv4Endpoint endpoint = new() {
    Address = new() { A = 127, B = 0, C = 0, D = 1 },
    Port = 8080
};

stack mut System.Net.Tcp.TcpClient client = new(endpoint);
```

Listener construction should use the same style:

```stark
stack mut System.Net.Tcp.TcpListener listener = new(endpoint);
stack System.Net.NetResult<System.Net.Tcp.TcpClient> accepted = listener.Accept();
```

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

`Read`, `Write`, `Accept`, `Shutdown`, and `Close` are ordinary `fn` methods
because they interact with the operating system, mutate handle state, or depend
on external network state.

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

- This is a planned `v1.2` module.
- Linux implementation will need syscall-backed socket, connect, bind, listen,
  accept, send, receive, shutdown, and close support.
- Windows implementation will need the corresponding Winsock support.
