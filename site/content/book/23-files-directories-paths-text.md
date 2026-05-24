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

The snippets below assume the modules they use have been imported:

```stark
import System.Console
import System.FileSystem
import System.IO
import System.IO.File
import System.IO.Path
import System.Memory
import System.Runtime.Buffer
import System.Text
```

`System.IO.File` and `System.FileSystem` both expose names such as `Exists` and
`Move`. When a snippet uses one of those overlapping names, it keeps the full
module path so the call is unambiguous.

## Step 1: Switch On The IO Result First

Before opening files, learn the shared result vocabulary:

- `IOStatus`
- `IOResult<T>`
- `IOError`

These are ordinary Stark enums. Use `switch` at the call site before adding more
filesystem behavior; it keeps the happy path and cleanup path visible.

```stark
fn bool StatusOk(IOStatus status) {
    switch (status) {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}
```

The shared error cases are the names you will usually branch on when a command
can recover:

```stark
fn bool CanCreateMissingParent(IOError error) {
    switch (error) {
        case IOError.NotFound:
            return true;
        case IOError.PermissionDenied:
            return false;
        case IOError.AlreadyExists:
            return false;
        case IOError.InvalidPath:
            return false;
        case IOError.BrokenPipe:
            return false;
        case IOError.DiskFull:
            return false;
        case IOError.Unknown(var code):
            return false;
    }
}
```

## Step 2: Open An Owned File Handle

`File` is an owned file handle:

```stark
stack IOResult<File> opened =
    Open(path, FileMode.Write);

switch (opened) {
    case IOResult<File>.Err(var error):
        return false;
    case IOResult<File>.Ok(var value):
        stack mut File file = value;
        if (!StatusOk(file.WriteLine("marker"))) {
            return false;
        }

        return StatusOk(file.Close());
}
```

Methods such as `WriteLine`, `Flush`, `Seek`, and `Close` act on the owned
handle. `IsOpen()` is a read-only status query.

Close a file explicitly when the program needs ordering or a returned close
status. Owned cleanup still exists as a backstop, but it is not a substitute for
clear error handling.

The open helpers cover the common file-mode choices:

```stark
Open("input.txt", FileMode.Read);
Open("output.txt", FileMode.Write);
Open("log.txt", FileMode.Append);
Open("data.bin", FileMode.ReadWrite);
```

Choose buffering by the write behavior you want:

```stark
Open("direct.bin", FileMode.Write, FileBuffering.None);
Open("lines.log", FileMode.Write, FileBuffering.Line);
Open("bulk.log", FileMode.Write, FileBuffering.Full);
```

When buffering or text encoding matters, choose it at open time:

```stark
Open("line-log.txt", FileMode.Write, FileBuffering.Line);
Open("utf8.txt", FileMode.Write, Encoding.UTF8);
Open("utf16.txt", FileMode.Write, Encoding.UTF16, FileBuffering.Full);
```

Use byte slices for binary IO:

```stark
stack mut i8[min max][4] buffer = { 1, 2, 3, 4 };
stack IOResult<u64[0 2 ** 63 - 1]> written = file.Write(buffer);
stack IOResult<u64[0 2 ** 63 - 1]> read = file.Read(buffer);
```

Most examples become easier to read with a small byte-count helper:

```stark
fn bool ByteCountIs(
    IOResult<u64[0 2 ** 63 - 1]> result,
    u64[0 2 ** 63 - 1] expected) {
        switch (result) {
            case IOResult<u64[0 2 ** 63 - 1]>.Err(var error):
                return false;
            case IOResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                return count == expected;
        }
}
```

For a small binary round trip, open for writing, close, then open for reading:

```stark
fn bool WriteThenRead(ascii path) {
    stack i8[min max][4] source = { 1, 2, 3, 4 };
    stack mut i8[min max][4] destination = { 0, 0, 0, 0 };

    stack mut File output =
        OpenOrEmpty(Open(path, FileMode.Write));
    if (!ByteCountIs(output.Write(source), 4)) {
        output.Close();
        return false;
    }

    output.Close();

    stack mut File input =
        OpenOrEmpty(Open(path, FileMode.Read));
    if (!ByteCountIs(input.Seek(0, SeekOrigin.Begin), 0)) {
        input.Close();
        return false;
    }

    if (!ByteCountIs(input.Read(destination), 4)) {
        input.Close();
        return false;
    }

    input.Close();
    return destination[0] == 1 && destination[3] == 4;
}
```

Switch on byte counts when the count matters:

```stark
switch (written) {
    case IOResult<u64[0 2 ** 63 - 1]>.Err(var error):
        return false;
    case IOResult<u64[0 2 ** 63 - 1]>.Ok(var count):
        return count == 4;
}
```

Use `Flush` for buffered Stark writes and `SyncAll` when the program needs a
durable-storage boundary:

```stark
if (!StatusOk(file.Flush())) {
    return false;
}

return StatusOk(file.SyncAll());
```

Use `WriteText` when the caller controls line endings, and `WriteLine` when the
library should append one. The file's text encoding is chosen by the `Open`
overload:

```stark
stack mut File file =
    OpenOrEmpty(Open(
        "report.txt",
        FileMode.Write,
        Encoding.UTF8,
        FileBuffering.Line));

file.WriteText("status: ");
file.WriteLine((unicode)"ready");
file.Flush();
file.Close();
```

Use `Seek` when the next read or write should start somewhere else:

```stark
stack IOResult<u64[0 2 ** 63 - 1]> position =
    file.Seek(0, SeekOrigin.Begin);
```

The seek origin names describe where the offset starts:

```stark
file.Seek(0, SeekOrigin.Begin);
file.Seek(4, SeekOrigin.Current);
file.Seek(-4, SeekOrigin.End);
```

Files also accept standard-library byte buffers. A buffer write uses the readable
part of the buffer:

```stark
stack mut FixedByteBuffer512 bytes = new();
bytes.WriteByte(65);
bytes.WriteByte(66);
bytes.WriteByte(67);
file.Write(bytes);
```

`Write` accepts `DynamicByteBuffer`, `FixedByteBuffer512`,
`FixedByteBuffer4096`, and `FixedByteBuffer8192`.

## Step 3: Move Whole-Path Work To `System.FileSystem`

Use `System.FileSystem` for operations that are broader than an already-open
file:

- create or delete a directory
- open a directory for iteration
- ask whether a path exists
- ask whether a path is a file or directory
- move a path

Directory iteration returns owned Stark values for entries rather than asking
ordinary code to manage raw OS directory buffers.

```stark
stack IOResult<Directory> opened =
    OpenDirectory(".");

switch (opened) {
    case IOResult<Directory>.Err(var error):
        return false;
    case IOResult<Directory>.Ok(var directory):
        stack mut Directory entries = directory;
        while non-deterministic (true) {
            stack DirectoryReadResult next = entries.ReadNext();
            switch (next) {
                case DirectoryReadResult.End:
                    return StatusOk(entries.Close());
                case DirectoryReadResult.Err(var error):
                    entries.Close();
                    return false;
                case DirectoryReadResult.Entry(var entry):
                    stack mut FileSystemEntry ownedEntry = entry;
                    WriteLine(ownedEntry.NameView());
            }
        }
}
```

Each directory entry has a name and a kind:

```stark
fn bool IsVisibleFile(mut borrow FileSystemEntry entry) {
    return entry.Kind == FileSystemEntryKind.File
        && entry.NameView() != ".";
}
```

Use `ReadNextInfo()` when the program needs the entry kind but not an owned copy
of the entry name:

```stark
fn bool DirectoryHasAnyEntryInfo(mut borrow Directory directory) {
    stack DirectoryReadInfoResult next = directory.ReadNextInfo();
    switch (next) {
        case DirectoryReadInfoResult.End:
            return false;
        case DirectoryReadInfoResult.Err(var error):
            return false;
        case DirectoryReadInfoResult.Entry(var info):
            return info.Kind == FileSystemEntryKind.File
                || info.Kind == FileSystemEntryKind.Directory
                || info.Kind == FileSystemEntryKind.Symlink
                || info.Kind == FileSystemEntryKind.Other;
    }
}
```

Use `ReadNext()` when the program needs the entry name:

```stark
stack DirectoryReadResult next = entries.ReadNext();
```

Use `ReadNextInfo()` when the program only needs metadata such as
`FileSystemEntryKind`.

Use `Delete` and `System.IO.File.Move` for file-oriented
whole-path operations. Use `CreateDirectory`,
`DeleteDirectory`, `Exists`, `IsFile`, `IsDirectory`, and `Move` when the
operation is about the filesystem entry rather than a file handle.

The path-level helpers return the same status/result families:

```stark
fn bool EnsureDirectory(ascii path) {
    stack IOStatus created = CreateDirectory(path);
    switch (created) {
        case IOStatus.Ok:
            return true;
        case IOStatus.Err(var error):
            return false;
    }
}
```

```stark
fn bool PathIsFile(ascii path) {
    stack IOResult<bool> result = IsFile(path);
    switch (result) {
        case IOResult<bool>.Err(var error):
            return false;
        case IOResult<bool>.Ok(var value):
            return value;
    }
}
```

Move and delete operations should stay explicit:

```stark
System.FileSystem.Move("old-name.txt", "new-name.txt");
Delete("new-name.txt");
DeleteDirectory("empty-dir");
```

## Step 4: Build Paths As Text, Not As Platform Guesses

Path APIs use Stark text types. Prefer explicit path construction helpers when
combining path fragments instead of hand-writing platform separators into
application logic.

Ask `System.IO.Path` for platform separators instead of spelling them in the
program:

```stark
stack ascii separator = DirectorySeparator();
stack ascii alternate = AlternateDirectorySeparator();
stack ascii listSeparator = PathSeparator();
stack ascii parent = ParentDirectory();
```

`DirectorySeparator()` is the separator used when Stark builds a path.
`AlternateDirectorySeparator()` is accepted by platforms that have a second
separator spelling. `PathSeparator()` is for path lists such as environment
variables. `ParentDirectory()` returns the portable parent marker `".."`.

Owned path and text helpers return allocation-aware result values where
allocation can fail. Caller-owned text buffers remain available when the caller
should choose the destination capacity.

```stark
stack mut OwnedAscii current = new();
if (CurrentDirectory(current) != MemoryStatus.Ok) {
    return false;
}

stack mut OwnedAscii joined = new();
if (TryJoin(joined, current.View(), "output.txt") != MemoryStatus.Ok) {
    return false;
}

WriteLine(joined);
```

Use the allocation-returning form when the function should return owned path
text:

```stark
fn MemoryResult<OwnedAscii> BuildOutputPath(ascii directory) {
    return Join(directory, "output.txt");
}
```

There is also an allocation-returning current-directory form:

```stark
fn bool HasCurrentDirectory() {
    stack MemoryResult<OwnedAscii> result =
        CurrentDirectory();

    switch (result) {
        case MemoryResult<OwnedAscii>.Err(var error):
            return false;
        case MemoryResult<OwnedAscii>.Ok(var value):
            stack mut OwnedAscii path = value;
            return path.View() != "";
    }
}
```

Use `TryJoinConst` or `JoinConst` when both pieces are literal path text:

```stark
fn bool BuildConstPath(mut borrow OwnedAscii destination) {
    return TryJoinConst(destination, "samples", "main.stark")
        == MemoryStatus.Ok;
}
```

```stark
fn MemoryResult<OwnedAscii> BuildConstPath() {
    return JoinConst("samples", "main.stark");
}
```

Use separator normalization when path text may contain the alternate separator:

```stark
fn MemoryResult<OwnedAscii> CleanPath(ascii path) {
    return NormalizeSeparators(path);
}
```

Use the caller-owned form when the caller already owns the destination buffer:

```stark
fn bool CleanInto(mut borrow OwnedAscii destination, ascii path) {
    return TryNormalizeSeparators(destination, path)
        == MemoryStatus.Ok;
}
```

The constant forms are for literal paths:

```stark
fn MemoryResult<OwnedAscii> CleanConstPath() {
    return NormalizeSeparatorsConst("samples\\main.stark");
}
```

Use `TryNormalizeSeparatorsConst` when the caller already owns the destination:

```stark
fn bool CleanConstInto(mut borrow OwnedAscii destination) {
    return TryNormalizeSeparatorsConst(destination, "samples/main.stark")
        == MemoryStatus.Ok;
}
```

Use the non-mutating path queries when you only need a view:

```stark
stack ascii extension = Extension("logs/app.txt");
stack ascii baseName = BaseName("logs/app.txt");
stack ascii directory = DirectoryName("logs/app.txt");
```

When you need several pieces of the same path, compute them together:

```stark
fn bool IsStarkFile(ascii path) {
    stack PathFacts facts = GetFacts(path);
    return facts.Extension() == ".stark" && facts.BaseNameLength() > 0;
}
```

The constant forms are useful for literal paths:

```stark
finite law ascii SampleExtension() {
    return ExtensionConst("samples/main.stark");
}
```

When several facts are needed from the same literal path, use
`GetConstFacts`:

```stark
finite law bool ConstSampleLooksLikeStarkSource() {
    stack PathFacts facts =
        GetConstFacts("samples/main.stark");

    return facts.DirectoryName() == "samples"
        && facts.BaseName() == "main"
        && facts.Extension() == ".stark";
}
```

The direct constant helpers are available when you only need one view:

```stark
BaseNameConst("samples/main.stark");
DirectoryNameConst("samples/main.stark");
ExtensionConst("samples/main.stark");
```

## Step 5: Keep The Example Concrete

The standard library favors concrete types here: files, directories, TCP
clients, TCP listeners, and owned buffers.

That is intentional for the tutorial. Use the concrete type that owns the
resource, then factor shared helper functions only when their cost and
ownership are still visible in the signature.
