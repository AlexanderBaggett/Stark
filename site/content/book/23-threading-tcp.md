+++
title = "23. Threading and TCP"
weight = 230
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/22-files-directories-paths-text/"
next = "/book/24-testing-stark-code/"

[[stdlib_refs]]
title = "System.Threading"
href = "/reference/docs/StandardLibrary/System.Threading.md"

[[stdlib_refs]]
title = "System.Net"
href = "/reference/docs/StandardLibrary/System.Net.md"

[[stdlib_refs]]
title = "System.Net.Tcp"
href = "/reference/docs/StandardLibrary/System.Net.Tcp.md"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Threading and TCP

This chapter covers the first concurrent and networking APIs.

{{< stark-sample "assets/book/stdlib-samples/threading-tcp.stark" >}}

## Thread Entries

`System.Threading.ThreadEntry` is a no-state function pointer returning an
integer exit code. Named functions and non-capturing lambdas are the ordinary
entry forms:

```stark
fn i32[min max] Worker() {
    return 7;
}

stack System.Threading.ThreadEntry entry = Worker;
```

Capturing thread entries are intentionally not the default example. Captures
need a visible sharing and lifetime story before they should become ordinary
threading code.

## Thread Lifecycle

`System.Threading.Thread` is an owned handle:

```stark
stack mut System.Threading.Thread worker = new(entry);
stack System.Threading.ThreadJoinResult joined = worker.Join();
```

`Join` waits for completion and returns either an exit code or a thread error.
`Detach` releases the obligation to join. Dropping a still-joinable thread
performs best-effort cleanup; it does not silently kill the running thread.

Scheduler helpers live on `Thread` as static functions:

```stark
System.Threading.Thread.Yield();
System.Threading.Thread.SleepMilliseconds(1);
```

## TCP Values

`System.Net` defines `IPv4Address`, `IPv4Endpoint`, and shared network
result/status types. `System.Net.Tcp` adds blocking TCP clients and listeners.

The first TCP constructors create closed handles. Fallible socket creation uses
named operations:

```stark
System.Net.Tcp.TcpClient.Connect(endpoint)
System.Net.Tcp.TcpListener.Listen(endpoint)
```

Both return `System.Net.NetResult<T>` so network failure stays explicit.

## Read And Write Buffers

TCP read/write uses safe Stark slices:

```stark
stack mut i8[min max][4] buffer = { 0, 0, 0, 0 };
client.Read(buffer);
client.Write(buffer);
```

The API boundary stays efficient without exposing raw socket pointers to
ordinary code.

## Current Synchronization Gaps

The first standard library threading slice is not a full concurrency framework.
The following remain future work:

- mutexes
- condition variables
- semaphores
- channels
- async/await
- non-blocking socket event loops

Until those land, keep shared mutable state out of examples unless the sharing
mechanism is explicit and documented.
