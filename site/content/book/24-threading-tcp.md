+++
title = "24. Threading and TCP"
weight = 240
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/23-files-directories-paths-text/"
next = "/book/25-testing-stark-code/"
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

{{< stark-sample "assets/book/stdlib-samples/threading-tcp.stark" >}}

## Step 1: Start With A No-Capture Thread Entry

`System.Threading.ThreadEntry` is a no-state function pointer returning an
integer exit code. Named functions and non-capturing lambdas are the ordinary
entry forms:

```stark
fn i32[min max] Worker() {
    return 7;
}

stack System.Threading.ThreadEntry entry = Worker;
```

Capturing thread entries are intentionally not the tutorial starting point.
Start with a named function or non-capturing callable so ownership and sharing
stay visible.

## Step 2: Join Or Detach The Owned Handle

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

## Step 3: Create Network Values Explicitly

`System.Net` defines `IPv4Address`, `IPv4Endpoint`, and shared network
result/status types. `System.Net.Tcp` adds blocking TCP clients and listeners.

The first TCP constructors create closed handles. Fallible socket creation uses
named operations:

```stark
System.Net.Tcp.TcpClient.Connect(endpoint)
System.Net.Tcp.TcpListener.Listen(endpoint)
```

Both return `System.Net.NetResult<T>` so network failure stays explicit.

## Step 4: Read And Write Through Safe Slices

TCP read/write uses safe Stark slices:

```stark
stack mut i8[min max][4] buffer = { 0, 0, 0, 0 };
client.Read(buffer);
client.Write(buffer);
```

The API boundary stays efficient without exposing raw socket pointers to
ordinary code.

## Step 5: Stay Inside The Implemented Threading Slice

The first standard-library threading slice teaches threads, joins, detach,
sleep/yield, and blocking TCP. Keep tutorial code inside that slice unless the
chapter is deliberately documenting a missing library surface.

Do not write tutorial examples that depend on:

- mutexes
- condition variables
- semaphores
- channels
- async/await
- non-blocking socket event loops

Keep shared mutable state out of examples unless the sharing mechanism is
explicit and documented. That keeps the lesson honest: readers learn the APIs
that exist today, and they do not mistake absent synchronization types for
hidden runtime behavior.
