+++
title = "35. Project: File Processing Utility"
weight = 350
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/34-project-multi-module-package/"
next = "/book/36-project-native-package/"
aliases = ["/book/32-project-file-processing/", "/book/34-project-file-processing/"]

[[stdlib_refs]]
title = "System.FileSystem"
href = "/reference/standard-library/System.FileSystem/"

[[stdlib_refs]]
title = "System.IO.File"
href = "/reference/standard-library/System.IO.File/"

[[stdlib_refs]]
title = "System.IO.Path"
href = "/reference/standard-library/System.IO.Path/"

[[example_refs]]
title = "Build Your Own Git"
href = "/reference/examples/build-your-own-git/Status.stark"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Project: File Processing Utility

This project chapter turns the file-system sample into a utility: write a file,
verify it, inspect paths, and clean up with explicit status handling.

## Step 1: Define The File Workflow

Build a small utility that writes a marker file, verifies it exists, inspects a
directory, and reports status through exit codes.

Build the workflow from:

- `System.IO.File.File` for owned file handles
- `System.FileSystem` for path-level and directory operations
- `System.IO.IOStatus` and `System.IO.IOResult<T>` for recoverable failure
- explicit close calls when ordering and status matter

{{< stark-sample "assets/book/stdlib-samples/files-directories-paths-text.stark" >}}

The checked sample is intentionally small: write one file, prove the path now
exists, then delete it. That gives the project a reliable spine before adding
directory walking, filtering, or larger text processing.

## Step 2: Open And Close Handles Deliberately

Open a file through `System.IO.File.Open`:

```stark
stack mut System.IO.File.File file =
    System.IO.File.Open("output.txt", System.IO.File.FileMode.Write);
```

Check `file.IsOpen()` before writing. Call `file.Close()` when the program
needs the close status.

## Step 3: Switch On Directory Reads

Directory reads return enum-shaped data:

```stark
switch (next) {
    case System.FileSystem.DirectoryReadResult.End:
        return true;
    case System.FileSystem.DirectoryReadResult.Err(var error):
        return false;
    case System.FileSystem.DirectoryReadResult.Entry(var entry):
        ...
}
```

The loop is usually `while non-deterministic` because the filesystem is external
state.

## Step 4: Build Paths Through Helpers

Use path helpers when joining path fragments. Avoid baking platform separators
through application logic. When path helpers allocate owned text, handle the
allocation-aware result.

## Step 5: Make Cleanup Ordering Visible

Owned handles clean up at scope exit, but explicit close still matters when:

- the program needs the returned status
- the next operation depends on data being flushed
- the code needs clear ordering for a filesystem mutation
