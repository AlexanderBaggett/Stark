+++
title = "22. Files, Directories, Paths, and Text"
weight = 220
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/21-memory-collections/"
next = "/book/23-threading-tcp/"

[[stdlib_refs]]
title = "System.FileSystem"
href = "/reference/docs/StandardLibrary/System.FileSystem.md"

[[stdlib_refs]]
title = "System.IO.File"
href = "/reference/docs/StandardLibrary/System.IO.File.md"

[[stdlib_refs]]
title = "System.IO.Path"
href = "/reference/docs/StandardLibrary/System.IO.Path.md"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/docs/StandardLibrary/System.Text.md"

[[example_refs]]
title = "Build Your Own Git"
href = "/reference/examples/build-your-own-git/Init.stark"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Files, Directories, Paths, and Text

This chapter combines owned handles with text and filesystem APIs.

{{< stark-sample "assets/book/stdlib-samples/files-directories-paths-text.stark" >}}

## Shared IO Results

`System.IO` defines shared status and result types:

- `System.IO.IOStatus`
- `System.IO.IOResult<T>`
- `System.IO.IOError`

These are ordinary Stark enums. Use `switch` to handle success and failure.

## Owned File Handles

`System.IO.File.File` is an owned file handle:

```stark
stack mut System.IO.File.File file =
    System.IO.File.Open(path, System.IO.File.FileMode.Write);
```

Methods such as `WriteLine`, `Flush`, `Seek`, and `Close` act on the owned
handle. `IsOpen()` is a read-only status query.

Close a file explicitly when the program needs ordering or a returned close
status. Owned cleanup still exists as a backstop, but it is not a substitute for
clear error handling.

## Filesystem Operations

Use `System.FileSystem` for operations that are broader than an already-open
file:

- create or delete a directory
- open a directory for iteration
- ask whether a path exists
- ask whether a path is a file or directory
- move a path

Directory iteration returns owned Stark values for entries rather than asking
ordinary code to manage raw OS directory buffers.

## Paths And Text

Path APIs use Stark text types. Prefer explicit path construction helpers when
combining path fragments instead of hand-writing platform separators into
application logic.

Owned path and text helpers return allocation-aware result values where
allocation can fail. Caller-owned text buffers remain available when the caller
should choose the destination capacity.

## No Stream Abstraction Yet

The current standard library favors concrete types: files, directories, TCP
clients, TCP listeners, and owned buffers.

A general stream abstraction can wait until Stark has a zero-cost static
interface story that does not force hidden allocation, dynamic dispatch, or
weaker optimizer facts into basic IO.
