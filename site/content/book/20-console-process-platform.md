+++
title = "20. Console, Process, and Platform Basics"
weight = 200
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/19-ffi-raw-pointers-native-packages/"
next = "/book/21-memory-collections/"

[[stdlib_refs]]
title = "System.Console"
href = "/reference/standard-library/System.Console/"

[[stdlib_refs]]
title = "System.Process"
href = "/reference/standard-library/System.Process/"

[[stdlib_refs]]
title = "System.IO"
href = "/reference/standard-library/System.IO/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Console, Process, and Platform Basics

This chapter turns the small hello-world program into a program that treats IO
as fallible work. Start by writing one line, then add the status handling that a
real command-line program needs.

{{< stark-sample "assets/book/stdlib-samples/console-process.stark" >}}

## Step 1: Write One Line, Then Handle The Status

Begin with stdout and stderr:

```stark
System.Console.WriteLine("Stark standard library")
System.Console.WriteErrorLine("diagnostic")
```

Now keep the return value. The write functions return
`System.IO.IOStatus`, not `void`, so the tutorial path is to switch on success
and failure instead of ignoring IO:

```stark
switch (System.Console.WriteLine("Hello")) {
    case System.IO.IOStatus.Ok:
        return true;
    case System.IO.IOStatus.Err(var error):
        return false;
}
```

## Step 2: Add Input Only When The Program Owns The Text

When the program needs interactive input, use the line helpers as owned text
producers:

- `System.Console.ReadAsciiLine()`
- `System.Console.ReadUnicodeLine()`
- `System.Console.ReadLine()`
- `System.Console.Read()`

The line helpers return owned text without the trailing newline. If code needs
the returned text to survive another console read, copy it into caller-owned
storage or an owned buffer chosen by your API.

## Step 3: Return From `main` Before Reaching For Process Exit

`System.Process.CurrentId()` returns the current operating-system process id.

`System.Process.Exit(code)` terminates the current process. It does not unwind
Stark-owned values, so ordinary application code should prefer returning from
`main` when normal cleanup should run.

## Step 4: Leave Platform Details Behind `System.*`

Platform details live behind `System.*` modules. User code should not need to
know whether stdout, process ids, or process exit route through Linux syscalls,
Windows APIs, or another target's hosted runtime surface.

That separation preserves the high-level Stark rule: ordinary programs use
safe standard-library APIs, while OS-specific raw details stay inside small
implementation boundaries.
