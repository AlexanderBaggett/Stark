+++
title = "38. Project: File Processing Utility"
weight = 380
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/37-project-multi-module-package/"
next = "/book/39-project-native-package/"
aliases = ["/book/32-project-file-processing/", "/book/34-project-file-processing/", "/book/35-project-file-processing/"]

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

- `File` for owned file handles
- `System.FileSystem` for path-level and directory operations
- `IOStatus` and `IOResult<T>` for recoverable failure
- explicit close calls when ordering and status matter

{{< stark-sample "samples/files-directories-paths-text.stark" >}}

The snippets below assume the modules they use have been imported:

```stark
import System.Console
import System.FileSystem
import System.IO
import System.IO.File
import System.IO.Path
import System.Memory
import System.Text
```

`System.IO.File` and `System.FileSystem` both expose names such as `Exists` and
`Move`, so those overlapping calls stay fully qualified when both modules are
in scope.

The checked sample is intentionally small: write one file, check the path now
exists, then delete it. That gives the project a reliable spine before adding
directory walking, filtering, or larger text processing.

## Step 2: Open And Close Handles Deliberately

Open a file through `Open`:

```stark
stack IOResult<File> opened =
    Open("output.txt", FileMode.Write);

switch (opened)
{
    case IOResult<File>.Err(var error):
        return false;
    case IOResult<File>.Ok(var value):
        stack mut File file = value;
        return file.IsOpen();
}
```

Check `file.IsOpen()` before writing. Call `file.Close()` when the program
needs the close status.

For a utility, wrap that pattern in a helper that returns `IOStatus`:

```stark
fn IOStatus WriteMarker(ascii path, ascii text)
{
    stack IOResult<File> opened =
        Open(path, FileMode.Write);

    switch (opened)
    {
        case IOResult<File>.Err(var error):
            return IOStatus.Err(error);
        case IOResult<File>.Ok(var value):
            stack mut File file = value;
            stack IOStatus written = file.WriteLine(text);

            switch (written)
            {
                case IOStatus.Err(var error):
                    file.Close();
                    return IOStatus.Err(error);
                case IOStatus.Ok:
                    return file.Close();
            }
    }
}
```

That helper makes every failure visible: open failure, write failure, and close
failure are all ordinary return values.

Choose the file mode that matches the workflow:

```stark
stack IOResult<File> readOnly =
    Open("input.txt", FileMode.Read);

stack IOResult<File> appendOnly =
    Open("log.txt", FileMode.Append);

stack IOResult<File> readWrite =
    Open("state.bin", FileMode.ReadWrite);
```

Use buffering settings when the file's write pattern matters:

```stark
stack IOResult<File> opened =
    Open(
        "log.txt",
        FileMode.Append,
        FileBuffering.Line);
```

Call `Flush()` when the program needs buffered data written out before the
handle closes. Call `SyncAll()` when durable storage sync is part of the
utility's contract.

## Step 3: Query Paths Before Mutating Them

Path-level operations return result values too. Switch on them before deciding
whether to delete, move, or create:

```stark
fn bool PathExists(ascii path)
{
    stack IOResult<bool> exists = System.FileSystem.Exists(path);

    switch (exists)
    {
        case IOResult<bool>.Err(var error):
            return false;
        case IOResult<bool>.Ok(var value):
            return value;
    }
}
```

When an operation can fail for a reason callers should see, return `IOStatus`
instead of collapsing it to `bool`:

```stark
fn IOStatus DeleteIfPresent(ascii path)
{
    stack IOResult<bool> exists = System.FileSystem.Exists(path);

    switch (exists)
    {
        case IOResult<bool>.Err(var error):
            return IOStatus.Err(error);
        case IOResult<bool>.Ok(var value):
            if (!value)
            {
                return IOStatus.Ok;
            }

            return Delete(path);
    }
}

```

Use the more specific path queries when the workflow cares about the entry
kind:

```stark
fn bool IsRegularFile(ascii path)
{
    stack IOResult<bool> result = IsFile(path);
    switch (result)
    {
        case IOResult<bool>.Err(var error):
            return false;
        case IOResult<bool>.Ok(var value):
            return value;
    }
}
```

For directory setup, keep create/delete failures visible:

```stark
fn IOStatus RecreateDirectory(ascii path)
{
    stack IOStatus deleted = DeleteDirectory(path);
    switch (deleted)
    {
        case IOStatus.Err(var error):
            return IOStatus.Err(error);
        case IOStatus.Ok:
            return CreateDirectory(path);
    }
}
```

## Step 4: Switch On Directory Reads

Directory reads return enum-shaped data:

```stark
switch (next)
{
    case DirectoryReadResult.End:
        return true;
    case DirectoryReadResult.Err(var error):
        return false;
    case DirectoryReadResult.Entry(var entry):
        ...
}
```

The loop is usually `while non-deterministic` because the filesystem is external
state.

The complete loop opens the directory, reads until `End`, and closes the handle:

```stark
fn IOStatus VisitDirectory(ascii path)
{
    stack IOResult<Directory> opened =
        OpenDirectory(path);

    switch (opened)
    {
        case IOResult<Directory>.Err(var error):
            return IOStatus.Err(error);
        case IOResult<Directory>.Ok(var value):
            stack mut Directory directory = value;
            while non-deterministic (true)
            {
                stack DirectoryReadResult next = directory.ReadNext();
                switch (next)
                {
                    case DirectoryReadResult.End:
                        return directory.Close();
                    case DirectoryReadResult.Err(var error):
                        directory.Close();
                        return IOStatus.Err(error);
                    case DirectoryReadResult.Entry(var entry):
                        stack mut FileSystemEntry ownedEntry = entry;
                        WriteLine(ownedEntry.NameView());
                }
            }
    }
}
```

## Step 5: Build Paths Through Helpers

Use path helpers when joining path fragments. Avoid baking platform separators
through application logic. When path helpers allocate owned text, handle the
allocation-aware result.

```stark
stack mut OwnedAscii path = new();
if (TryJoin(path, "logs", "today.txt") != MemoryStatus.Ok)
{
    return 2;
}

stack ascii baseName = BaseName(path.View());
stack ascii extension = Extension(path.View());
```

When the utility needs several path details, ask for them together:

```stark
finite law bool IsStarkSource(ascii path)
{
    stack PathFacts facts = GetFacts(path);
    return facts.Extension() == ".stark" && facts.BaseNameLength() > 0;
}
```

Use the returned `PathFacts` value when later chapters or standard-library docs
show its fields directly. Until then, the smaller helpers are clearer for
ordinary code.

Use the allocation-returning join helper when the path should be owned by the
caller:

```stark
fn MemoryResult<OwnedAscii> MarkerPath(ascii directory)
{
    return Join(directory, "marker.txt");
}
```

Use caller-owned storage when the utility already owns a reusable buffer:

```stark
fn MemoryStatus BuildMarkerPath(
    mut borrow OwnedAscii destination,
    ascii directory)
{
    return TryJoin(destination, directory, "marker.txt");
}
```

Current-directory lookup follows the same pattern:

```stark
fn MemoryStatus WriteCurrentDirectory(
    mut borrow OwnedAscii destination)
{
    return CurrentDirectory(destination);
}
```

## Step 6: Make Cleanup Ordering Visible

Owned handles clean up at scope exit, but explicit close still matters when:

- the program needs the returned status
- the next operation depends on data being flushed
- the code needs clear ordering for a filesystem mutation

A complete project driver should keep that ordering easy to read:

```stark
finite law bool IsOk(IOStatus status)
{
    switch (status)
    {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}

export fn i32[min max] main()
{
    stack IOStatus wrote = WriteMarker("marker.txt", "ready");
    if (!IsOk(wrote))
    {
        return 1;
    }

    if (!PathExists("marker.txt"))
    {
        return 2;
    }

    stack IOStatus deleted = DeleteIfPresent("marker.txt");
    if (!IsOk(deleted))
    {
        return 3;
    }

    return 0;
}
```
