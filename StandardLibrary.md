# Standard Library

This document describes the first standard library slice currently implemented in the repository.

## Goals

The initial standard library focuses on three things:

- a stable module layout
- packaging as a normal manifest-backed Stark package
- basic output through `stdout` and `stderr`

This keeps the first library surface small while exercising the compiler's module, package, ABI, and native-linking pipeline end to end.

## Module Layout

The first package root is `System`.

Repository source layout:

- `stdlib/src/System.stark`
- `stdlib/src/System/Console.stark`
- `stdlib/src/System/IO/Stdout.stark`
- `stdlib/src/System/IO/Stderr.stark`

Public module surface:

- `System`
- `System.Console`
- `System.IO.Stdout`
- `System.IO.Stderr`

`System.stark` is a pure package root that re-exports the public submodules:

```stark
export import System.Console
export import System.IO.Stdout
export import System.IO.Stderr
module System
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

The initial text surface is intentionally `ascii`. Wider string and formatting helpers can be added later.

## Runtime Strategy

The current implementation is backed by the C runtime:

- `fputs`
- `stdout`
- `stderr`

These details stay inside the standard library package. User code calls `System.Console` or `System.IO.*` instead of binding those libc symbols directly.

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

## Near-Term Library Work

The next standard-library additions on the roadmap are:

- file read APIs
- file write APIs
- path and file error modeling
- string helpers used by the library itself
