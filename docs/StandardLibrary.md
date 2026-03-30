# Standard Library

This document describes the first standard library slice currently implemented in the repository.

## Goals

The initial standard library focuses on three things:

- a stable module layout
- packaging as a normal manifest-backed Stark package
- basic console output plus a small file/path surface

This keeps the first library surface small while exercising the compiler's module, package, ABI, and native-linking pipeline end to end.

## Module Layout

The first package root is `System`.

Repository source layout:

- `stdlib/src/System.stark`
- `stdlib/src/System/Console.stark`
- `stdlib/src/System/IO.stark`
- `stdlib/src/System/IO/Stdout.stark`
- `stdlib/src/System/IO/Stderr.stark`
- `stdlib/src/System/IO/File.stark`
- `stdlib/src/System/IO/Path.stark`

Public module surface:

- `System`
- `System.IO`
- `System.Console`
- `System.IO.Stdout`
- `System.IO.Stderr`
- `System.IO.File`
- `System.IO.Path`

`System.stark` is a pure package root that re-exports the public submodules:

```stark
export import System.Console
export import System.IO
module System
```

`System.IO` is itself a pure namespace module that re-exports the currently implemented IO-related submodules:

```stark
export import System.IO.Stdout
export import System.IO.Stderr
export import System.IO.File
export import System.IO.Path
module System.IO
```

## Current API

`System.Console`:

- `public fn void Write(ascii text)`
- `public fn void WriteLine(ascii text)`
- `public fn void WriteError(ascii text)`
- `public fn void WriteErrorLine(ascii text)`

`System.IO.Stdout`:

- `public fn void Write(ascii text)`
- `public fn void WriteLine(ascii text)`

`System.IO.Stderr`:

- `public fn void Write(ascii text)`
- `public fn void WriteLine(ascii text)`

`System.IO.File`:

- `public fn rawptr<i8> OpenRead(ascii path)`
- `public fn rawptr<i8> OpenWrite(ascii path)`
- `public fn rawptr<i8> OpenAppend(ascii path)`
- `public fn i32 Close(rawptr<i8> handle)`
- `public fn i32 Flush(rawptr<i8> handle)`
- `public fn i64 ReadBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle)`
- `public fn i64 WriteBytes(rawptr<i8> buffer, i64 size, i64 count, rawptr<i8> handle)`
- `public fn void WriteText(rawptr<i8> handle, ascii text)`
- `public fn void WriteLine(rawptr<i8> handle, ascii text)`
- `public fn i32 Delete(ascii path)`
- `public fn i32 Move(ascii oldPath, ascii newPath)`

`System.IO.Path`:

- `public fn ascii DirectorySeparator()`
- `public fn ascii AlternateDirectorySeparator()`
- `public fn ascii PathSeparator()`
- `public fn ascii CurrentDirectory()`
- `public fn ascii ParentDirectory()`

The initial text surface is intentionally `ascii`. The current file APIs also intentionally expose raw C-runtime-style handles while the higher-level Stark IO model is still small.

## Runtime Strategy

The current implementation is backed by the C runtime:

- `fputs`
- `stdout`
- `stderr`
- `fopen`
- `fclose`
- `fflush`
- `fread`
- `fwrite`
- `remove`
- `rename`

These details stay inside the standard library package. User code calls `System.Console` or `System.IO.*` instead of binding those libc symbols directly. The current `System.IO.Path` helpers are pure library wrappers returning ASCII path constants.

Two current compiler limitations matter here:

- source-level globals require an initializer
- `void` is not yet accepted as a general type argument, so opaque stream handles cannot currently be written as `rawptr<void>`

Because of that, the internal libc stream handles are declared as `rawptr<i8>` with placeholder `null` initializers in Stark source even though LLVM emission lowers them as external globals at link time.

## Building the Package

Build the standard library package with:

```bash
./scripts/build-stdlib.sh
```

By default that emits the package into `stdlib/dist/`.

You can also choose another output directory:

```bash
./scripts/build-stdlib.sh /tmp/stark-stdlib
```

The emitted package contains:

- a static library archive
- a sidecar `.starkpkg.json` manifest

## Using the Packaged Standard Library

Build the package first, then compile an application with `-I` pointing at the package directory.

Example:

```stark
import System
module Hello

export ffi fn i32 main() {
    System.Console.WriteLine("Hello, world!");
    System.Console.WriteErrorLine("stderr works too");
    return 0;
}
```

Compile:

```bash
dotnet run --project src -- hello.stark --emit-exe -I stdlib/dist -o hello
```

## Roadmap Scope

This document is a library roadmap, not a language-spec chapter and not a milestone schedule.

It tracks:

- the public stdlib surface that is already implemented
- the next modules and namespaces to flesh out
- runtime, allocator, and IO dependencies that must stay behind the library boundary
- the test and packaging work needed to keep the package consumable as the surface grows

The intent is to keep the stdlib organized around stable module boundaries rather than around internal compiler phases. The implemented surface above remains the source of truth for the current package shape.

## Near-Term Module Surface Plan

The next work slice should grow the stdlib by module family, not by ad hoc helper functions.

### `System`

Keep the root module as an import hub only.

- continue to re-export the modules that are intended to be user-facing entry points
- avoid putting behavior in the package root unless it is truly cross-cutting
- treat this module as the stable top-level namespace for package consumers

### `System.Console`

Keep `System.Console` as the friendly high-level output module.

- preserve the current text output API
- prefer small wrapper APIs here over exposing raw runtime handles
- add formatting-oriented helpers only when the text model underneath is ready for them
- keep error output alongside normal output so command-line applications have one obvious entry point

### `System.IO.Stdout` and `System.IO.Stderr`

Keep these modules as the minimal stream-backed sinks.

- preserve them as the low-level text emission layer under `System.Console`
- share as much implementation as possible without leaking shared runtime details into the surface
- keep them narrow until the stdlib has a broader file and stream model

### `System.IO`

`System.IO` now exists as a parent namespace and re-export hub for the current IO-related modules.

Likely first additions:

- file existence and basic metadata queries
- richer stream-state helpers
- a less raw handle and ownership model for file operations
- path-friendly helpers used by file APIs

The parent module should group the related pieces; it should not become a dumping ground for unrelated runtime helpers.

### `System.Text`

Add text helpers once the library needs more than raw `ascii` passthrough.

Likely first additions:

- string slicing and search helpers
- simple join/concat helpers
- lightweight formatting helpers for console and diagnostics output
- helpers for translating between library-internal text representations

The text layer should be designed so future allocator-backed string types can be added without rewriting the public module layout.

### `System.IO.Path`

`System.IO.Path` now exists with basic separator and current/parent-directory helpers.

Likely first additions:

- join/combine helpers
- extension/base-name helpers
- normalization helpers that stay platform-aware without overpromising canonicalization

Path helpers should be pure library code where possible, with platform-specific behavior isolated behind a small abstraction layer.

### `System.Runtime`

Reserve a runtime-facing namespace for library support that is not directly user-facing.

Likely first additions:

- process start and shutdown helpers
- exit-code plumbing
- environment and host capability access
- low-level allocation entry points if the compiler/runtime contract requires them

This namespace should expose the smallest stable set needed by the rest of the stdlib.

## Runtime, Allocator, and IO Dependencies

The stdlib should treat these dependencies as implementation boundaries, not public design goals.

### Runtime boundary

- The library can depend on compiler-emitted or toolchain-provided runtime symbols, but user code should not.
- Startup and shutdown behavior should stay coordinated between the compiler toolchain and `System.Runtime`, not encoded in application code.
- Any API that needs process termination or host interaction should be routed through a library wrapper instead of direct FFI calls in user code.

### Allocator boundary

- The current `ascii` output slice avoids allocation, which keeps the initial surface simple.
- Future text, path, file, and collection helpers will need a clear allocator story before they can become ergonomic.
- Prefer a single allocator contract for stdlib internals rather than ad hoc allocation helpers in each module.
- If the compiler/runtime eventually supplies a default allocator, the library should use that through a narrow, well-named boundary.

### IO boundary

- The current output modules intentionally hide the libc stream handles.
- Later file and stream APIs should still hide `FILE*`-style details behind Stark types or opaque handles.
- IO error reporting should be modeled in the stdlib surface, not exposed as raw C return codes.
- Keep platform-specific file descriptors, paths, and newline behavior contained inside the IO layer.

## Testing Plan

Stdlib tests should cover the package as a package, not only the individual source files.

### Current smoke coverage

- compile the repository stdlib root into a package artifact
- verify the emitted manifest lists the expected modules
- verify the package can be consumed without importing stdlib source files directly
- keep end-to-end executable coverage for `System.Console` plus packaged `System.IO.File` and `System.IO.Path` consumption

### Coverage for new modules

- add one build smoke test per new public module family
- validate re-export behavior whenever the package root changes
- add consumption tests for APIs that are meant to work from packaged output only
- add regression tests for error cases as soon as a module starts modeling failures
- keep tests close to the surface shape they validate so failures point back to the right module

### Packaging checks

- verify the package manifest stays in sync with exported module names
- verify the package output location can be overridden
- verify the package remains usable from a clean consumer project with no source dependency
- verify the emitted artifact set remains stable enough for downstream tooling

## Packaging Plan

The packaging flow should stay simple and scriptable.

- `scripts/build-stdlib.sh` remains the canonical build entry point for the repository package
- the script should continue to emit both the archive and the package manifest
- package output should remain compatible with the compiler's `-I` source/package lookup path
- packaging changes should preserve the ability to build the stdlib in isolation from application code
- if more than one stdlib package appears later, each package should still be buildable through the same basic workflow

## Near-Term Work Ordering

The next implementation pass should focus on:

- factoring shared IO logic under the current `System` slice without widening the public surface prematurely
- tightening the current `System.IO` namespace around a more intentional handle and error model
- introducing text helpers only when they can be backed by a clear string and allocator story
- keeping runtime, allocator, and IO concerns behind narrow stdlib boundaries
- extending tests and packaging checks alongside each new module family
