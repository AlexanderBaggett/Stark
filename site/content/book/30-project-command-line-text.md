+++
title = "30. Project: Command-Line Text Tool"
weight = 300
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/29-generated-ir/"
next = "/book/31-project-multi-module-package/"

[[stdlib_refs]]
title = "System.Console"
href = "/reference/docs/StandardLibrary/System.Console.md"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/docs/StandardLibrary/System.Text.md"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Project: Command-Line Text Tool

This project chapter is the first larger walkthrough shape. The final version
should use hosted command-line arguments once that entrypoint model lands. Until
then, write the core text logic as ordinary functions and keep `main` as a small
driver.

## Current Project Shape

The project should have three layers:

- parsing and validation over `ascii` or `unicode` input
- processing functions that return status/result data
- a tiny `export ffi fn main` that maps success/failure to an exit code

That shape works today even before hosted argument arrays exist.

{{< stark-sample "assets/book/samples/text-tool-core.stark" >}}

The sample hard-codes `"run"` only because hosted command-line argument arrays
are not part of the current entrypoint model. The important project shape is
already useful: parse a text view, return a status enum, and convert the status
to a process exit code at the edge.

## Text Handling Rules

Prefer caller-owned or explicitly owned text:

- use `ascii` and `unicode` for views into existing text
- use `Ascii` and `Unicode` when the tool owns text storage
- use `System.Text` parse APIs for numeric/bool conversion
- use fixed-capacity buffers when the destination capacity should be chosen by
  the caller

Do not imply hidden allocation behind every string operation.

## Error Handling

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

## Manifest

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

When hosted command-line arguments land, this chapter should be updated to
replace the temporary fixed-input driver with real argument parsing.
