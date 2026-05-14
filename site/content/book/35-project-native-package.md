+++
title = "35. Project: Native-Backed Package"
weight = 350
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/34-project-file-processing/"
next = "/book/36-project-performance-case-study/"
aliases = ["/book/33-project-native-package/"]

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/language/ProjectsAndSolutions/"

[[example_refs]]
title = "FFI Example"
href = "/reference/examples/ffi/Ffi.stark"

[[example_refs]]
title = "Raylib Package"
href = "/reference/examples/raylib/README.md"
+++

# Project: Native-Backed Package

This project chapter wraps a native library once, then lets downstream Stark
projects consume it through ordinary package dependencies.

## Step 1: Define The Wrapper Boundary

Build a Stark package that wraps a native library and lets downstream Stark
executables depend on it without repeating native link details.

Use the Raylib example as the concrete model for this package shape.

## Step 2: Write The Stark Wrapper First

The Stark source declares the FFI boundary and exposes a smaller Stark API:

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

The checked sample keeps the raw pointer work small and explicit. A native
package should do the same thing at package scale: keep raw native declarations
close to the package that owns them, then expose a smaller Stark API to
downstream code.

The `unsafe ffi fn native_value` declaration shows the boundary shape without
making the ordinary safe part of the program depend on raw pointers or nullable
safe borrows.

## Step 3: Add A Boring Native Shim

Use a small C shim when the native library's ABI shape is awkward for direct
Stark declarations. The shim should translate between the native library and a
simple C ABI surface.

Keep the shim boring:

- no hidden ownership transfer
- no exceptions
- no global configuration unless unavoidable
- small functions with clear inputs and outputs

## Step 4: Put Native Build Facts In The Manifest

Native requirements belong in `Stark.toml`:

```toml
[project]
name = "raylib"
version = "0.1.0"
kind = "library"

[library]
root = "Raylib.stark"
output = "RaylibStark"

[native]
sources = ["RaylibNative.c"]
pkg-config = ["raylib"]
```

Platform fallback metadata can refer to user-local paths:

```toml
[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11"]
```

User-local paths belong in config, not checked-in package files.

## Step 5: Consume The Package Without Repeating Native Flags

The executable should only name the dependency:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

That is the point of package-owned native metadata: the package carries the
native facts, and consumers use ordinary Stark dependencies.

## Step 6: Review The Boundary Before Publishing

Before treating a native-backed package as reusable, check that:

- raw pointers stay inside the wrapper package unless the public API is
  deliberately low-level
- nullable native values are checked before conversion to safe Stark values
- C shims have boring ownership and error rules
- manifest metadata names native sources, discovery hooks, and fallback
  libraries in the package that owns the native boundary
- downstream examples depend on the package, not on handwritten link commands

This rejected boundary is the rule to keep in mind while reviewing:

{{< stark-sample "assets/book/negative-samples/enum-abi-boundary.stark" >}}

Ordinary Stark enums are source-level values. Do not expose them as C ABI
contracts unless the language and ABI design explicitly say that type is an
interop representation.
