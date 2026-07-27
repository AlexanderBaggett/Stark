+++
title = "Getting Started"
weight = 10
+++

Stark is currently built from this repository while the binary release pipeline
is being finalized. This page separates what packaged compiler releases should
require from what source builds need today.

## Future Binary Release

The intended beginner path is:

1. Download the Stark compiler archive for your platform from the [Releases page](/releases/).
2. Extract the complete SDK and add its `bin` directory to `PATH`.
3. Run `stark doctor --strict` and `stark --version`.
4. Compile and run a small program.

Following Odin's distribution model, each target archive will include the
compiler and its runtime, its compiler-private LLVM backend, the System library,
the complete official Vendor collection advertised for that target, examples,
offline reference files, licenses, and manifests. A basic `hello world` or
official Vendor import will not require users to build Stark or install a
separate LLVM SDK or Vendor library.

The archive intentionally does not contain a complete LLVM development
distribution or an operating-system SDK. Native executable linking uses the
narrow host development layer: Xcode Command Line Tools/full Xcode on macOS,
the supported MSVC/Windows SDK components on Windows, and a supported Clang/
native development environment plus system ABI libraries on Linux. The optional
installer and `stark doctor` will detect and explain those platform prerequisites.

## Current Source Build

Today, install:

- the .NET SDK used by this repository
- `clang`, so Stark can detect the host target triple and data layout and drive
  native output
- a normal platform linker and C toolchain
- Git, if you are cloning the repository

From the repository root:

```bash
dotnet build Stark.slnx
```

Check that the compiler can process a file:

```bash
dotnet run --project src -- --check examples/hello.stark
```

Build a native executable:

```bash
dotnet run --project src -- --emit-exe examples/hello.stark -o hello
./hello
```

## Using The Standard Library

Build the standard-library package first:

```bash
./scripts/build-stdlib.sh
```

Then compile with the generated package directory on the search path:

```bash
dotnet run --project src -- examples/hello.stark --emit-exe -I stdlib/dist -o hello
./hello
```

## Platform Notes

### Linux

Install the .NET SDK for source builds, plus `clang` and a normal native
linking toolchain. On most distributions that means Clang, binutils or LLD, and
the system C development headers/libraries. Keep `clang` and the linker on
`PATH`; Stark uses the host toolchain to discover target details and emit native
executables.

For source-checkout examples that use the standard library, build the standard-library package
first and pass `-I stdlib/dist` when compiling directly. Raylib examples also
need the development SDK's native acquisition/build step or an explicitly
configured authoring fallback. Published official Vendor packages do not use
ambient `pkg-config`.

### Windows

Source builds need the .NET SDK, `clang`, and a Windows-native linker toolchain
available on `PATH`. Use the same shell consistently when building and running
examples so generated `.exe` paths, package image paths, and native library
lookup all agree.

The compiler emits `.exe` and `.lib` names on Windows. Standard-library package
builds should produce `System.lib`, and direct compiles that consume it should
point `-I` at the directory containing that package artifact.

## Optional Requirements

When working from a source checkout rather than an installed release SDK, some
examples need more than the basic compiler path:

- Raylib examples need the repository's native acquisition/build inputs or an
  explicitly configured package-author fallback.
- Networking examples may require local firewall permission.
- Benchmarks will need locked compiler flags and platform details once the
  benchmark suite is published.
- Source builds and compiler tests require the .NET SDK; release archives carry
  the Stage0 runtime and do not require a separate .NET installation.

## First Diagnostic Check

If setup is incomplete, start with these checks:

```bash
dotnet --version
clang --version
dotnet run --project src -- --check examples/hello.stark
```

The compiler should report missing files, package images, native sources, or
libraries directly. If it emits a setup diagnostic, fix that requirement before
debugging the Stark source program itself.
