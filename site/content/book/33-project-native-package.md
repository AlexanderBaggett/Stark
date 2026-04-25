+++
title = "33. Project: Native-Backed Package"
weight = 330
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/32-project-file-processing/"
next = "/book/34-project-performance-case-study/"

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/docs/Userfacing/ProjectsAndSolutions.md"

[[example_refs]]
title = "FFI Example"
href = "/reference/examples/ffi/Ffi.stark"

[[example_refs]]
title = "Raylib Package"
href = "/reference/examples/raylib/README.md"
+++

# Project: Native-Backed Package

This project chapter teaches the Raylib-style package pattern.

## Goal

Build a Stark package that wraps a native library and lets downstream Stark
executables depend on it without repeating native link details.

The Raylib example is the current model.

## Stark Wrapper

The Stark source declares the FFI boundary and exposes a smaller Stark API:

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

The checked sample keeps the raw pointer work small and explicit. A native
package should do the same thing at package scale: keep raw native declarations
close to the package that owns them, then expose a smaller Stark API to
downstream code.

The `ffi fn native_value` declaration shows the boundary shape without making
the ordinary safe part of the program depend on raw pointers or nullable safe
borrows.

## Native Shim

Use a small C shim when the native library's ABI shape is awkward for direct
Stark declarations. The shim should translate between the native library and a
simple C ABI surface.

Keep the shim boring:

- no hidden ownership transfer
- no exceptions
- no global configuration unless unavoidable
- small functions with clear inputs and outputs

## Manifest Metadata

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

## Downstream Executable

The executable should only name the dependency:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

That is the point of package-owned native metadata: the package carries the
native facts, and consumers use ordinary Stark dependencies.

## Review Checklist

Before treating a native-backed package as reusable, check that:

- raw pointers stay inside the wrapper package unless the public API is
  deliberately low-level
- nullable native values are checked before conversion to safe Stark values
- C shims have boring ownership and error rules
- manifest metadata names native sources, discovery hooks, and fallback
  libraries in the package that owns the native boundary
- downstream examples depend on the package, not on handwritten link commands
