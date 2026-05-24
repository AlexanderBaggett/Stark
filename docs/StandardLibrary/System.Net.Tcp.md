# `System.Net.Tcp`

`System.Net.Tcp` provides blocking TCP clients and listeners using safe Stark
byte slices and standard-library byte buffers.

## Current Public Surface

```stark
import System.Net
import System.Runtime.Buffer
module System.Net.Tcp

public enum TcpShutdown
{
    Receive,
    Send,
    Both,
}

public struct TcpClient
{
    TcpClient();
    static fn System.Net.NetResult<TcpClient> Connect(System.Net.IPv4Endpoint endpoint);
    finite law bool IsOpen(borrow TcpClient self);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow i8[min max][] destination);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer512 destination);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer4096 destination);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.FixedByteBuffer8192 destination);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Read(mut borrow TcpClient self, mut borrow System.Runtime.Buffer.DynamicByteBuffer destination, u64[0 2 ** 63 - 1] maxCount);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow i8[min max][] source);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.DynamicByteBuffer source);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer512 source);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer4096 source);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> Write(mut borrow TcpClient self, borrow System.Runtime.Buffer.FixedByteBuffer8192 source);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> ReadVectored(mut borrow TcpClient self, mut borrow i8[min max][] firstDestination, mut borrow i8[min max][] secondDestination);
    fn System.Net.NetResult<u64[0 2 ** 63 - 1]> WriteVectored(mut borrow TcpClient self, borrow i8[min max][] firstSource, borrow i8[min max][] secondSource);
    fn System.Net.NetStatus WaitReadable(mut borrow TcpClient self, i32[min max] timeoutMilliseconds);
    fn System.Net.NetStatus WaitWritable(mut borrow TcpClient self, i32[min max] timeoutMilliseconds);
    fn System.Net.NetStatus Shutdown(mut borrow TcpClient self, TcpShutdown how);
    fn System.Net.NetStatus Close(mut borrow TcpClient self);
}

public struct TcpListener
{
    TcpListener();
    static fn System.Net.NetResult<TcpListener> Listen(System.Net.IPv4Endpoint endpoint);
    finite law bool IsOpen(borrow TcpListener self);
    fn System.Net.NetResult<TcpClient> Accept(mut borrow TcpListener self);
    fn System.Net.NetStatus WaitReadable(mut borrow TcpListener self, i32[min max] timeoutMilliseconds);
    fn System.Net.NetStatus Close(mut borrow TcpListener self);
}
```

The constructors create closed handles. Use `TcpClient.Connect` and
`TcpListener.Listen` for fallible socket creation.

## Client And Listener Construction

```stark
stack System.Net.IPv4Endpoint endpoint = new()
{
    Address = new()
    {
        A = 127, B = 0, C = 0, D = 1
    },
    Port = 8080
};

stack System.Net.NetResult<System.Net.Tcp.TcpClient> connected =
    System.Net.Tcp.TcpClient.Connect(endpoint);

stack System.Net.NetResult<System.Net.Tcp.TcpListener> listening =
    System.Net.Tcp.TcpListener.Listen(endpoint);
```

Both operations return `System.Net.NetResult<T>` so recoverable network failure
is visible to the caller.

## Read And Write

The base read/write methods use safe byte slices:

```stark
stack mut i8[min max][4] buffer =
{
    0, 0, 0, 0
};
stack System.Net.NetResult<u64[0 2 ** 63 - 1]> read = client.Read(buffer);
stack System.Net.NetResult<u64[0 2 ** 63 - 1]> written = client.Write(buffer);
```

`Ok(count)` is the byte count. `Err(error)` reports a recoverable network
failure.

Fixed byte buffers can be read into directly. On success, the buffer advances
its write cursor by the count returned:

```stark
stack mut System.Runtime.Buffer.FixedByteBuffer4096 bytes = new();
stack System.Net.NetResult<u64[0 2 ** 63 - 1]> read = client.Read(bytes);
```

Dynamic byte buffers grow up to a caller-provided limit:

```stark
stack mut System.Runtime.Buffer.DynamicByteBuffer bytes = new();
stack System.Net.NetResult<u64[0 2 ** 63 - 1]> read = client.Read(bytes, 8192);
```

All byte buffers can be written through their readable slice:

```stark
client.Write(bytes);
```

## Vectored IO

Use vectored IO when a protocol naturally has two adjacent byte ranges, such as
a header and body:

```stark
stack i8[min max][4] header =
{
    1, 2, 3, 4
};
stack i8[min max][8] body =
{
    0, 0, 0, 0, 0, 0, 0, 0
};
client.WriteVectored(header, body);
```

For `ReadVectored`, pass two separate mutable destinations:

```stark
stack mut i8[min max][4] header =
{
    0, 0, 0, 0
};
stack mut i8[min max][8] body =
{
    0, 0, 0, 0, 0, 0, 0, 0
};
client.ReadVectored(header, body);
```

## Waiting, Shutdown, And Close

`WaitReadable` and `WaitWritable` let blocking code wait with a timeout before
attempting the operation:

```stark
client.WaitReadable(1000);
client.WaitWritable(1000);
listener.WaitReadable(1000);
```

Use `Shutdown` when one half of a connection should close before the whole
handle closes:

```stark
client.Shutdown(System.Net.Tcp.TcpShutdown.Send);
client.Close();
```

`TcpClient` and `TcpListener` are owned handles. Call `Close` when ordering or
returned status matters.

## Function Kinds

`IsOpen` is `finite law` on both TCP handle types because it only reads local
handle state and always returns.

`Connect`, `Read`, `Write`, `ReadVectored`, `WriteVectored`, `WaitReadable`,
`WaitWritable`, `Accept`, `Shutdown`, and `Close` are ordinary `fn` operations
because they can interact with external network state.
