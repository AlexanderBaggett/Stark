+++
title = "36. Project: Command-Line Text Tool"
weight = 360
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/35-generated-ir/"
next = "/book/37-project-multi-module-package/"
aliases = ["/book/30-project-command-line-text/", "/book/32-project-command-line-text/", "/book/33-project-command-line-text/"]

[[stdlib_refs]]
title = "System.Console"
href = "/reference/standard-library/System.Console/"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/standard-library/System.Text/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Project: Command-Line Text Tool

This project chapter is the first larger walkthrough shape. It starts by
separating text parsing from process startup, so the useful logic can be tested
and reused while `main` stays a small driver.

## Step 1: Split The Tool Into Layers

Build the project in three layers:

- parsing and validation over `ascii` or `unicode` input
- processing functions that return status/result data
- a tiny safe `export fn main` that maps success/failure to an exit code

The snippets below assume the modules they use have been imported:

```stark
import System.Console
import System.IO
import System.Memory
import System.Text
```

{{< stark-sample "assets/book/samples/text-tool-core.stark" >}}

The sample keeps input selection in `main`, but the important project shape is
the same for any entrypoint: parse a text view, return a status enum, and
convert the status to a process exit code at the edge.

Keep the parsing function independent from console input:

```stark
enum Command {
    Help,
    Run,
    Unknown,
}

finite law Command ParseCommand(ascii input) {
    if (input == "help") {
        return Command.Help;
    }

    if (input == "run") {
        return Command.Run;
    }

    return Command.Unknown;
}
```

Then map the parsed command into the tool's status:

```stark
finite law ToolStatus ClassifyCommand(ascii input) {
    switch (ParseCommand(input)) {
        case Command.Help:
            return ToolStatus.Ok;
        case Command.Run:
            return ToolStatus.Ok;
        case Command.Unknown:
            return ToolStatus.InvalidInput;
    }
}
```

For an interactive command-line tool, the entrypoint usually adds one more
layer: read owned text, switch on the allocation result, then pass a text view
to the parser.

```stark
fn i32[min max] RunCommand(mut borrow OwnedAscii line) {
    return ExitCode(ClassifyCommand(line.View()));
}

export fn i32[min max] main() {
    stack MemoryResult<OwnedAscii> read =
        ReadAsciiLine();

    switch (read) {
        case MemoryResult<OwnedAscii>.Err(var error):
            WriteErrorLine("could not read command");
            return 1;
        case MemoryResult<OwnedAscii>.Ok(var line):
            stack mut OwnedAscii command = line;
            return RunCommand(command);
    }
}
```

## Step 2: Choose The Text Ownership Model

Prefer caller-owned or explicitly owned text:

- use `ascii` and `unicode` for views into existing text
- use `Ascii` and `Unicode` when the tool owns text storage
- use `System.Text` parse APIs for numeric/bool conversion
- use fixed-capacity buffers when the destination capacity should be chosen by
  the caller

Do not imply hidden allocation behind every string operation.

Convert owned text to a view at the processing boundary:

```stark
fn ToolStatus RunOwned(mut borrow OwnedAscii input) {
    return ClassifyCommand(input.View());
}
```

Parse text by switching on `TextResult<T>`:

```stark
fn ToolStatus ReadCount(ascii input, out i32[min max] destination) {
    stack TextResult<i32[min max]> parsed =
        ParseI32Ascii(input);

    switch (parsed) {
        case TextResult<i32[min max]>.Err(var error):
            return ToolStatus.InvalidInput;
        case TextResult<i32[min max]>.Ok(var value):
            destination = value;
            return ToolStatus.Ok;
    }
}
```

Use the matching parser for the text kind you actually have:

```stark
stack TextResult<bool> asciiFlag =
    ParseBoolAscii("true");

stack TextResult<bool> unicodeFlag =
    ParseBoolUnicode((unicode)"true");
```

When formatting a small result, use fixed-capacity text when the capacity is
part of the program shape:

```stark
fn IOStatus WriteScore(i32[min max] score) {
    stack Ascii line[32] = $"score={score}";
    return WriteLine(AsciiView(line));
}
```

When conversion allocates owned text, switch on the memory result:

```stark
fn ToolStatus PrintNumber(i32[min max] value) {
    stack MemoryResult<OwnedAscii> converted =
        ToAscii(value);

    switch (converted) {
        case MemoryResult<OwnedAscii>.Err(var error):
            return ToolStatus.OutputFailed;
        case MemoryResult<OwnedAscii>.Ok(var text):
            stack mut OwnedAscii output = text;
            switch (WriteLine(output)) {
                case IOStatus.Ok:
                    return ToolStatus.Ok;
                case IOStatus.Err(var ioError):
                    return ToolStatus.OutputFailed;
            }
    }
}
```

## Step 3: Return Status Instead Of Throwing

Recoverable problems should return ordinary values:

```stark
enum ToolStatus {
    Ok,
    MissingInput,
    InvalidInput,
    OutputFailed,
}
```

The entrypoint maps those values to process exit codes. It should not depend on
exceptions or unwinding.

```stark
finite law i32[min max] ExitCode(ToolStatus status) {
    switch (status) {
        case ToolStatus.Ok:
            return 0;
        case ToolStatus.MissingInput:
            return 1;
        case ToolStatus.InvalidInput:
            return 2;
        case ToolStatus.OutputFailed:
            return 3;
    }
}
```

Use small helpers to keep `main` readable:

```stark
finite law bool IsOk(ToolStatus status) {
    switch (status) {
        case ToolStatus.Ok:
            return true;
        case ToolStatus.MissingInput:
            return false;
        case ToolStatus.InvalidInput:
            return false;
        case ToolStatus.OutputFailed:
            return false;
    }
}
```

Console writes also return status values:

```stark
fn ToolStatus PrintUsage() {
    switch (WriteLine("usage: text-tool <command>")) {
        case IOStatus.Ok:
            return ToolStatus.Ok;
        case IOStatus.Err(var error):
            return ToolStatus.OutputFailed;
    }
}
```

## Step 4: Put Build Shape In The Manifest

The project manifest is the ordinary executable shape:

```toml
[project]
name = "text-tool"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "text-tool"

[dependencies]
stdlib = { path = "../../stdlib" }
```

The manifest stays boring on purpose. Source code owns parsing and status
handling; the project file owns how the executable is built.
