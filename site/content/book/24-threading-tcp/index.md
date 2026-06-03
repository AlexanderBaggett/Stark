+++
title = "24. Threading and TCP"
weight = 240
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/23-files-directories-paths-text/"
next = "/book/25-math-helpers/"
aliases = ["/book/23-threading-tcp/"]

[[stdlib_refs]]
title = "System.Threading"
href = "/reference/standard-library/System.Threading/"

[[stdlib_refs]]
title = "System.Net"
href = "/reference/standard-library/System.Net/"

[[stdlib_refs]]
title = "System.Net.Tcp"
href = "/reference/standard-library/System.Net.Tcp/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Threading and TCP

This chapter builds the smallest concurrent/networking shape Stark encourages:
start a no-capture thread, join it, then use blocking TCP APIs with safe slice
buffers.

{{< stark-sample "samples/threading-tcp.stark" >}}

The snippets below assume the modules they use have been imported:

```stark
import System.Net
import System.Net.Tcp
import System.Runtime.Buffer
import System.Threading
```

## Step 1: Start With A No-Capture Thread Entry

`ThreadEntry` is a no-state function pointer returning an
integer exit code. Named functions and non-capturing lambdas are the ordinary
entry forms:

```stark
fn i32[min max] Worker()
{
    return 7;
}

stack ThreadEntry entry = Worker;
```

You can also use a non-capturing lambda when the target type is already a
`ThreadEntry`:

```stark
stack ThreadEntry entry = () => 0;
```

Capturing thread entries are intentionally not the tutorial starting point.
Start with a named function or non-capturing callable so ownership and sharing
stay visible.

Keep the worker result small and explicit:

```stark
fn i32[min max] Worker()
{
    // Return a process-style status code from the thread.
    return 0;
}
```

## Step 2: Join Or Detach The Owned Handle

`Thread` is an owned handle:

```stark
stack mut Thread worker = new(entry);
stack ThreadJoinResult joined = worker.Join();
```

`Join` waits for completion and returns either an exit code or a thread error.
`Detach` releases the obligation to join. Dropping a still-joinable thread
performs best-effort cleanup; it does not silently kill the running thread.

Use `IsJoinable()` when a helper receives a thread handle and needs to decide
whether `Join` or `Detach` still makes sense:

```stark
fn bool JoinIfReady(mut borrow Thread worker)
{
    if (!worker.IsJoinable())
    {
        return false;
    }

    stack ThreadJoinResult joined = worker.Join();
    switch (joined)
    {
        case ThreadJoinResult.Ok(var code):
            return code == 0;
        case ThreadJoinResult.Err(var error):
            return false;
    }
}
```

Scheduler helpers live on `Thread` as static functions:

```stark
Thread.Yield();
Thread.SleepMilliseconds(1);
```

Handle the join result with a normal `switch`:

```stark
switch (joined)
{
    case ThreadJoinResult.Ok(var code):
        return code == 7;
    case ThreadJoinResult.Err(var error):
        return false;
}
```

When the program deliberately will not join, detach explicitly and handle the
status:

```stark
stack ThreadStatus status = worker.Detach();
switch (status)
{
    case ThreadStatus.Ok:
        return true;
    case ThreadStatus.Err(var error):
        return false;
}
```

Use a helper when several thread operations return `ThreadStatus`:

```stark
finite law bool ThreadOk(ThreadStatus status)
{
    switch (status)
    {
        case ThreadStatus.Ok:
            return true;
        case ThreadStatus.Err(var error):
            return false;
    }
}
```

The thread error names are small and direct: `StartFailed`, `JoinFailed`,
`AlreadyJoined`, `AlreadyDetached`, and `InvalidState`. Most application code
can log the error and return a failure status; tests may switch on the exact
case when they are checking handle state.

A complete no-capture thread example is:

```stark
fn bool RunWorker()
{
    stack ThreadEntry entry = Worker;
    stack mut Thread worker = new(entry);
    stack ThreadJoinResult joined = worker.Join();

    switch (joined)
    {
        case ThreadJoinResult.Err(var error):
            return false;
        case ThreadJoinResult.Ok(var code):
            return code == 0;
    }
}
```

## Step 3: Share State Between Threads With Atomics

Threads that never share state only communicate through their exit codes. The
moment two threads need to see the same counter, flag, or value, use the atomic
types — they are the only safe way to share mutable state between threads.

`System.Threading` has one atomic type per integer width (`AtomicI8` through
`AtomicI1024`, `AtomicU8` through `AtomicU1024`) plus `AtomicBool`. Every
operation is one indivisible, sequentially-consistent action; there is no way to
write a racy read-modify-write through this API.

Shared atomics live in `static mut` module state. Module-level declarations
spell out the qualified type name:

```stark
static mut System.Threading.AtomicI64 SharedCounter = new System.Threading.AtomicI64(0);
static mut System.Threading.AtomicBool SharedReady = new System.Threading.AtomicBool(false);
```

Workers update shared state through the atomic operations, never through plain
assignment:

```stark
fn i32[min max] CountingWorker()
{
    stack mut i32[min max] i = 0;
    while willexit (i < 1000)
    {
        SharedCounter.Add(1);
        i += 1;
    }

    SharedReady.Store(true);
    return 0;
}
```

`Add` returns the *previous* value and is one atomic instruction: two threads
running this loop produce exactly 2000, every run. After both workers join, the
main thread reads the results:

```stark
fn bool AtomicSharingShapeWorks()
{
    stack mut Thread first = new(CountingWorker);
    stack mut Thread second = new(CountingWorker);
    first.Join();
    second.Join();

    if (SharedCounter.Load() != 2000)
    {
        return false;
    }

    if (!SharedReady.Load())
    {
        return false;
    }

    if (!SharedCounter.CompareExchange(2000, 0))
    {
        return false;
    }

    return SharedCounter.Load() == 0;
}
```

The operation set is the same on every atomic type:

- `Load()` / `Store(value)` — read or replace the value.
- `Add` / `Sub` / `And` / `Or` / `Xor` — read-modify-write; each returns the
  **previous** value. `Add`/`Sub` wrap at the value's width.
- `Exchange(value)` — replace and return the previous value.
- `CompareExchange(expected, desired)` — replace only if the current value is
  `expected`; returns whether the swap happened.

`AtomicBool` works as a ready/shutdown flag: one thread publishes data through
atomics, then stores `true`; other threads check the flag before reading.

Cost model: 8/16/32/64-bit atomics and `AtomicBool` are single hardware
instructions. 24/48/96/128-bit atomics stay lock-free (only `Add`/`Sub` may
retry under contention). 192-bit and wider serialize through a small lock
embedded in the struct itself — visible in `sizeof`, never hidden global state.

## Step 4: Create Network Values Explicitly

`System.Net` defines `IPv4Address`, `IPv4Endpoint`, and shared network
result/status types. `System.Net.Tcp` adds blocking TCP clients and listeners.

The first TCP constructors create closed handles. Fallible socket creation uses
named operations:

```stark
TcpClient.Connect(endpoint)
TcpListener.Listen(endpoint)
```

Both return `NetResult<T>` so network failure stays explicit.

Use helpers for shared network status shapes:

```stark
finite law bool NetOk(NetStatus status)
{
    switch (status)
    {
        case NetStatus.Ok:
            return true;
        case NetStatus.Err(var error):
            return false;
    }
}
```

Switch on `NetworkError` when the program can recover differently from different
failures:

```stark
finite law bool ShouldRetry(NetworkError error)
{
    switch (error)
    {
        case NetworkError.AddressInvalid:
            return false;
        case NetworkError.AddressInUse:
            return false;
        case NetworkError.ConnectionRefused:
            return false;
        case NetworkError.ConnectionReset:
            return true;
        case NetworkError.TimedOut:
            return true;
        case NetworkError.NotConnected:
            return false;
        case NetworkError.WouldBlock:
            return true;
        case NetworkError.Unsupported:
            return false;
        case NetworkError.Unknown(var code):
            return false;
    }
}
```

```stark
stack IPv4Endpoint endpoint = new()
{
    Address = new()
    {
        A = 127,
        B = 0,
        C = 0,
        D = 1
    },
    Port = 8080
};

stack NetResult<TcpClient> connected =
    TcpClient.Connect(endpoint);

switch (connected)
{
    case NetResult<TcpClient>.Err(var error):
        return false;
    case NetResult<TcpClient>.Ok(var value):
        stack mut TcpClient client = value;
        return client.IsOpen();
}
```

Listeners follow the same result shape:

```stark
stack NetResult<TcpListener> listening =
    TcpListener.Listen(endpoint);

switch (listening)
{
    case NetResult<TcpListener>.Err(var error):
        return false;
    case NetResult<TcpListener>.Ok(var value):
        stack mut TcpListener listener = value;
        stack NetResult<TcpClient> accepted =
            listener.Accept();
        switch (accepted)
        {
            case NetResult<TcpClient>.Err(var error):
                listener.Close();
                return false;
            case NetResult<TcpClient>.Ok(var client):
                stack mut TcpClient connection = client;
                connection.Close();
                listener.Close();
                return true;
        }
}
```

## Step 5: Read And Write Through Safe Slices

TCP read/write uses safe Stark slices:

```stark
stack mut i8[min max][4] buffer =
{
    0, 0, 0, 0
};
stack NetResult<u64[0 2 ** 63 - 1]> read = client.Read(buffer);
stack NetResult<u64[0 2 ** 63 - 1]> written = client.Write(buffer);
```

The API boundary stays efficient without exposing raw socket pointers to
ordinary code.

Switch on the byte count:

```stark
switch (written)
{
    case NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
        return false;
    case NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
        return count == 4;
}
```

Use `Shutdown` when one half of the connection should close before the owned
handle closes:

```stark
client.Shutdown(TcpShutdown.Send);
client.Close();
```

The shutdown modes are:

```stark
client.Shutdown(TcpShutdown.Receive);
client.Shutdown(TcpShutdown.Send);
client.Shutdown(TcpShutdown.Both);
```

Always close when ordering or returned status matters:

```stark
if (!NetOk(client.Close()))
{
    return false;
}

return true;
```

Closed default handles are useful as empty values:

```stark
stack TcpClient closed = new();
if (closed.IsOpen())
{
    return false;
}
```

Listeners have the same closed-handle shape:

```stark
stack TcpListener listener = new();
return !listener.IsOpen();
```

## Step 6: Use Buffers, Vectored IO, And Wait Helpers When They Match The Work

Use a fixed byte buffer when the receive size is bounded and should stay in the
owning value:

```stark
stack mut FixedByteBuffer4096 buffer = new();
stack NetResult<u64[0 2 ** 63 - 1]> read = client.Read(buffer);
```

On success, the buffer's write cursor advances by the number of bytes read:

```stark
switch (read)
{
    case NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
        return false;
    case NetResult<u64[0 2 ** 63 - 1]>.Ok(var count):
        return buffer.Readable() == count;
}
```

Use a dynamic byte buffer when the receive side should grow up to a caller-chosen
limit:

```stark
stack mut DynamicByteBuffer bytes = new();
stack NetResult<u64[0 2 ** 63 - 1]> read =
    client.Read(bytes, 8192);
```

Write accepts slices and buffers:

```stark
stack i8[min max][4] header =
{
    1, 2, 3, 4
};
client.Write(header);
client.Write(buffer);
```

Use vectored IO when a protocol naturally has two adjacent pieces, such as a
header and a payload:

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

For vectored reads, the two destination slices must be separate mutable storage:

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

Use `WaitReadable` or `WaitWritable` when a blocking program wants an explicit
timeout before attempting the IO operation:

```stark
if (!NetOk(client.WaitReadable(1000)))
{
    return false;
}

stack NetResult<u64[0 2 ** 63 - 1]> read = client.Read(buffer);
```

The listener also has `WaitReadable(timeoutMilliseconds)` for waiting before
`Accept()`:

```stark
if (NetOk(listener.WaitReadable(1000)))
{
    stack NetResult<TcpClient> accepted = listener.Accept();
}
```

## Step 7: Stay Inside The Supported Threading Slice

This chapter teaches threads, joins, detach, sleep/yield, atomics, and blocking
TCP. Keep tutorial code inside that set unless the chapter is deliberately
naming an API that does not exist yet.

Do not write tutorial examples that depend on:

- mutexes
- condition variables
- semaphores
- channels
- async/await
- non-blocking socket event loops

When examples share mutable state between threads, share it through the atomic
types and nothing else. That keeps the lesson honest: readers learn the APIs
that exist today, and they do not mistake absent synchronization types for
hidden runtime behavior.
