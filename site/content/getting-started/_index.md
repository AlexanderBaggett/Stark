+++
title = "Getting Started"
weight = 10
+++

Stark is still built from this repository while the release pipeline is being
finished. This page separates what a future binary release should require from
what contributors need today.

## Future Binary Release

The intended beginner path is:

1. Download the Stark compiler archive for your platform from the [Releases page](/releases/).
2. Put the `stark` executable on `PATH`.
3. Run `stark --version`.
4. Compile and run a small program.

Binary releases should include the compiler and the matching standard-library
package image. A basic `hello world` should not require users to build the
compiler from source.

Stark emits native code through the platform toolchain. The exact packaged
toolchain policy still needs to be finalized, but users should expect a system
linker or C toolchain to be required for native executable output unless the
release bundle explicitly includes one.

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

For examples that use the standard library, build the standard-library package
first and pass `-I stdlib/dist` when compiling directly. Raylib examples also
need Raylib development files available through `pkg-config` or an explicit
native path.

### Windows

Source builds need the .NET SDK, `clang`, and a Windows-native linker toolchain
available on `PATH`. Use the same shell consistently when building and running
examples so generated `.exe` paths, package image paths, and native library
lookup all agree.

The compiler emits `.exe` and `.lib` names on Windows. Standard-library package
builds should produce `System.lib`, and direct compiles that consume it should
point `-I` at the directory containing that package artifact.

## Optional Requirements

Some examples need more than the basic compiler path:

- Raylib examples need Raylib available through `pkg-config` or a configured
  local path.
- Networking examples may require local firewall permission.
- Benchmarks will need locked compiler flags and platform details once the
  benchmark suite is published.
- Source builds and compiler tests require the .NET SDK; compiler binaries
  should not require it for ordinary use.

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
