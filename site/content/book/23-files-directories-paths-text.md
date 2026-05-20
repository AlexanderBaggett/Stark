+++
title = "23. Files, Directories, Paths, and Text"
weight = 230
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/22-memory-collections/"
next = "/book/24-threading-tcp/"
aliases = ["/book/22-files-directories-paths-text/"]

[[stdlib_refs]]
title = "System.FileSystem"
href = "/reference/standard-library/System.FileSystem/"

[[stdlib_refs]]
title = "System.IO.File"
href = "/reference/standard-library/System.IO.File/"

[[stdlib_refs]]
title = "System.IO.Path"
href = "/reference/standard-library/System.IO.Path/"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/standard-library/System.Text/"

[[example_refs]]
title = "Build Your Own Git"
href = "/reference/examples/build-your-own-git/Init.stark"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Files, Directories, Paths, and Text

This chapter builds a tiny filesystem workflow: create text, write a file,
check the path, and clean up through owned handles.

{{< stark-sample "assets/book/stdlib-samples/files-directories-paths-text.stark" >}}

## Step 1: Switch On The IO Result First

Before opening files, learn the shared result vocabulary:

- `System.IO.IOStatus`
- `System.IO.IOResult<T>`
- `System.IO.IOError`

These are ordinary Stark enums. Use `switch` at the call site before adding more
filesystem behavior; it keeps the happy path and cleanup path visible.

## Step 2: Open An Owned File Handle

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

## Step 3: Move Path-Level Work To `System.FileSystem`

Use `System.FileSystem` for operations that are broader than an already-open
file:

- create or delete a directory
- open a directory for iteration
- ask whether a path exists
- ask whether a path is a file or directory
- move a path

Directory iteration returns owned Stark values for entries rather than asking
ordinary code to manage raw OS directory buffers.

## Step 4: Build Paths As Text, Not As Platform Guesses

Path APIs use Stark text types. Prefer explicit path construction helpers when
combining path fragments instead of hand-writing platform separators into
application logic.

Owned path and text helpers return allocation-aware result values where
allocation can fail. Caller-owned text buffers remain available when the caller
should choose the destination capacity.

## Step 5: Keep The Example Concrete

The standard library favors concrete types here: files, directories, TCP
clients, TCP listeners, and owned buffers.

That is intentional for the tutorial. Use the concrete type that owns the
resource, then factor shared helper functions only when their cost and
ownership are still visible in the signature.
