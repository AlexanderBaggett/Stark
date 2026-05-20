+++
title = "33. Project: Command-Line Text Tool"
weight = 330
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/32-generated-ir/"
next = "/book/34-project-multi-module-package/"
aliases = ["/book/30-project-command-line-text/", "/book/32-project-command-line-text/"]

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

{{< stark-sample "assets/book/samples/text-tool-core.stark" >}}

The sample keeps input selection in `main`, but the important project shape is
the same for any entrypoint: parse a text view, return a status enum, and
convert the status to a process exit code at the edge.

## Step 2: Choose The Text Ownership Model

Prefer caller-owned or explicitly owned text:

- use `ascii` and `unicode` for views into existing text
- use `Ascii` and `Unicode` when the tool owns text storage
- use `System.Text` parse APIs for numeric/bool conversion
- use fixed-capacity buffers when the destination capacity should be chosen by
  the caller

Do not imply hidden allocation behind every string operation.

## Step 3: Return Status Instead Of Throwing

Recoverable problems should return ordinary values:

```stark
enum ToolStatus {
    Ok,
    InvalidInput,
    OutputFailed,
}
```

The entrypoint maps those values to process exit codes. It should not depend on
exceptions or unwinding.

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
