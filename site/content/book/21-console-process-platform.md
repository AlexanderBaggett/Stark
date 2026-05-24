+++
title = "21. Console, Process, and Platform Basics"
weight = 210
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/20-ffi-raw-pointers-native-packages/"
next = "/book/22-memory-collections/"
aliases = ["/book/20-console-process-platform/"]

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

The snippets below assume the modules they use have been imported:

```stark
import System.Console
import System.IO
import System.Memory
import System.Process
import System.Runtime.Buffer
import System.Text
```

## Step 1: Write One Line, Then Handle The Status

Begin with stdout and stderr. Use `Write` when the caller controls line endings,
and `WriteLine` when the standard library should append `\n`:

```stark
Write("status: ");
WriteLine("ready");
WriteErrorLine("diagnostic");
```

Now keep the return value. The write functions return
`IOStatus`, not `void`, so the tutorial path is to switch on success
and failure instead of ignoring IO:

```stark
switch (WriteLine("Hello")) {
    case IOStatus.Ok:
        return true;
    case IOStatus.Err(var error):
        return false;
}
```

Most command-line programs want a tiny helper for this:

```stark
finite law bool StatusOk(IOStatus status) {
    switch (status) {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}
```

The stdout and stderr families are deliberately small:

```stark
Write("same line");
WriteLine("with newline");
WriteError("same error line");
WriteErrorLine("error with newline");
```

All four functions accept `ascii`, `unicode`, `OwnedAscii`, `OwnedUnicode`, and
byte slices. `Write`, `WriteLine`, and `WriteErrorLine` also accept
`DynamicByteBuffer`, `FixedByteBuffer512`, `FixedByteBuffer4096`, and
`FixedByteBuffer8192`. For ordinary command-line programs, start with text
literals and owned text values.

When the text is owned, keep it mutable if the API asks for a mutable borrow:

```stark
fn bool WriteOwned(mut borrow OwnedAscii text) {
    return StatusOk(WriteLine(text));
}
```

When the program wants to write bytes, pass a byte slice:

```stark
fn bool WriteBytes() {
    stack i8[min max][3] bytes = { 65, 66, 67 };
    return StatusOk(Write(bytes));
}

fn bool WriteErrorBytes() {
    stack i8[min max][3] bytes = { 69, 82, 82 };
    return StatusOk(WriteError(bytes));
}
```

## Step 2: Add Input As An Owned Result

When the program needs interactive input, use the line helpers as owned text
producers:

- `ReadAsciiLine()`
- `ReadUnicodeLine()`
- `ReadLine()`
- `Read()`

The input helpers return `MemoryResult<T>`. That keeps allocation
failure visible and gives the caller an owned text value on success:

```stark
fn bool ReadCommand() {
    stack MemoryResult<OwnedAscii> result = ReadAsciiLine();

    switch (result) {
        case MemoryResult<OwnedAscii>.Err(var error):
            return false;
        case MemoryResult<OwnedAscii>.Ok(var line):
            stack mut OwnedAscii command = line;
            return command.Length() > 0;
    }
}
```

Use the ASCII helper for byte-oriented command lines. Use the Unicode helper
when the command text should be decoded as Unicode. `ReadLine()` is the
plain-name Unicode line helper, and `Read()` reads one Unicode code point.

The four input helpers are chosen by the text kind you want:

```stark
stack MemoryResult<OwnedAscii> asciiLine = ReadAsciiLine();

stack MemoryResult<OwnedUnicode> unicodeLine = ReadUnicodeLine();

stack MemoryResult<OwnedUnicode> defaultLine = ReadLine();

stack MemoryResult<OwnedUnicode> oneCodePoint = Read();
```

Handle them with the same `switch` pattern before using `.View()` or `.Length()`.

Turn owned input into a text view only after the `Ok` branch:

```stark
fn bool ReadNonEmptyAscii() {
    stack MemoryResult<OwnedAscii> result = ReadAsciiLine();

    switch (result) {
        case MemoryResult<OwnedAscii>.Err(var error):
            return false;
        case MemoryResult<OwnedAscii>.Ok(var line):
            stack mut OwnedAscii owned = line;
            stack ascii view = owned.View();
            return AsciiLength(view) > 0;
    }
}
```

## Step 3: Use Byte Buffers When Input Is Not Line Text

Use the line helpers when the program wants text. Use `ReadBytes` when the
program wants raw bytes from standard input:

```stark
fn bool ReadSomeBytes() {
    stack mut FixedByteBuffer512 buffer = new();
    stack MemoryResult<u64[0 2 ** 63 - 1]> result = ReadBytes(buffer, 128);

    switch (result) {
        case MemoryResult<u64[0 2 ** 63 - 1]>.Err(var error):
            return false;
        case MemoryResult<u64[0 2 ** 63 - 1]>.Ok(var count):
            return count <= 128 && buffer.Readable() == count;
    }
}
```

`ReadBytes(destination, maxCount)` stops when it reaches `maxCount`, reaches end
of input, or fills a fixed buffer. The dynamic-buffer overload can grow:

```stark
stack mut DynamicByteBuffer bytes = new();
stack MemoryResult<u64[0 2 ** 63 - 1]> read = ReadBytes(bytes, 4096);
```

Console output accepts byte slices and byte buffers. Use `WriteError` for an
error byte slice, and `WriteErrorLine` when the error path already has a buffer:

```stark
stack i8[min max][3] bytes = { 65, 66, 67 };
Write(bytes);
WriteError(bytes);

stack mut FixedByteBuffer512 buffer = new();
buffer.WriteByte(65);
buffer.WriteByte(66);
buffer.WriteByte(67);
Write(buffer);
WriteLine(buffer);
WriteErrorLine(buffer);
```

Choose the API by the data shape:

- `ReadAsciiLine`, `ReadUnicodeLine`, `ReadLine`, and `Read` for decoded text
- `ReadBytes` for byte streams
- `Write` and `WriteLine` for stdout
- `WriteError` and `WriteErrorLine` for stderr

## Step 4: Return From `main` Before Reaching For Process Exit

`CurrentId()` returns the current operating-system process id.

`Exit(code)` terminates the current process. It does not unwind
Stark-owned values, so ordinary application code should prefer returning from
`main` when normal cleanup should run.

```stark
export fn i32[min max] main() {
    if (CurrentId() <= 0) {
        return 1;
    }

    if (!StatusOk(WriteLine("done"))) {
        return 2;
    }

    return 0;
}
```

Use `Exit` for process-fatal boundaries where skipping local cleanup is the
intended behavior:

```stark
fn void StopNow() {
    WriteErrorLine("fatal");
    Exit(2);
}
```

For ordinary failure, prefer returning an exit code:

```stark
export fn i32[min max] main() {
    if (!StatusOk(WriteLine("starting"))) {
        return 1;
    }

    if (CurrentId() <= 0) {
        return 2;
    }

    return 0;
}
```

## Step 5: Leave Platform Details Behind `System.*`

Platform differences live behind `System.*` modules. User code should not need
to choose a different stdout, process-id, or process-exit API for each hosted
target.

That separation keeps the rule simple: ordinary programs use safe
standard-library APIs, while platform-specific raw details stay inside the
standard library or a deliberately low-level package.
