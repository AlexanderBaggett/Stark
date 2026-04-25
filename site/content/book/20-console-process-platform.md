+++
title = "20. Console, Process, and Platform Basics"
weight = 200
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/19-ffi-raw-pointers-native-packages/"
next = "/book/21-memory-collections/"

[[stdlib_refs]]
title = "System.Console"
href = "/reference/docs/StandardLibrary/System.Console.md"

[[stdlib_refs]]
title = "System.Process"
href = "/reference/docs/StandardLibrary/System.Process.md"

[[stdlib_refs]]
title = "System.IO"
href = "/reference/docs/StandardLibrary/System.IO.md"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Console, Process, and Platform Basics

This chapter introduces the standard-library surface most programs touch first.

{{< stark-sample "assets/book/stdlib-samples/console-process.stark" >}}

## Console Output

`System.Console` writes `ascii` and `unicode` text to stdout and stderr:

```stark
System.Console.WriteLine("Stark standard library")
System.Console.WriteErrorLine("diagnostic")
```

The write functions return `System.IO.IOStatus`, not `void`. That keeps
recoverable IO failure visible:

```stark
switch (System.Console.WriteLine("Hello")) {
    case System.IO.IOStatus.Ok:
        return true;
    case System.IO.IOStatus.Err(var error):
        return false;
}
```

## Console Input

The first input helpers are deliberately simple:

- `System.Console.ReadAsciiLine()`
- `System.Console.ReadUnicodeLine()`
- `System.Console.ReadLine()`
- `System.Console.Read()`

The line helpers return owned text without the trailing newline. If code needs
the returned text to survive another console read, copy it into caller-owned
storage or an owned buffer chosen by your API.

## Process Helpers

`System.Process.CurrentId()` returns the current operating-system process id.

`System.Process.Exit(code)` terminates the current process. It does not unwind
Stark-owned values, so ordinary application code should prefer returning from
`main` when normal cleanup should run.

## Platform Boundaries

Platform details live behind `System.*` modules. User code should not need to
know whether stdout, process ids, or process exit route through Linux syscalls,
Windows APIs, or another target's hosted runtime surface.

That separation preserves the high-level Stark rule: ordinary programs use
safe standard-library APIs, while OS-specific raw details stay inside small
implementation boundaries.
