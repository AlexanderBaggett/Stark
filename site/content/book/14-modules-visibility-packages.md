+++
title = "14. Modules, Visibility, and Packages"
weight = 140
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/13-arrays-slices-text/"
next = "/book/15-function-guarantees/"

[[language_refs]]
title = "Modules and Visibility"
href = "/reference/language/ModulesAndVisibility/"

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/language/ProjectsAndSolutions/"

[[example_refs]]
title = "Modules Example"
href = "/reference/examples/modules/App.stark"

[[example_refs]]
title = "Multi-Module Example"
href = "/reference/examples/multi-module/App.stark"

[[example_refs]]
title = "Examples Solution Manifest"
href = "/reference/examples/Stark.solution.toml"
+++

# Modules, Visibility, and Packages

Stark treats package boundaries as part of the language model. Visibility is
not just organization. It changes what downstream code can rely on and what the
current package can still keep private.

## Step 1: Start Each File With Imports And One Module

A source file imports the modules it needs, then declares exactly one module:

```stark
import Geometry
export import Units
module App
```

`import` makes visible declarations from another module available to the current
file. `export import` deliberately re-exports that module through the package
surface.

## Step 2: Add Backend Boundaries Only When They Are Real

Backend attributes use C#-style square brackets before the declaration they
control. The backend boundary attribute used in this tutorial is:

```stark
[Backend(Opaque)]
module System.Memory

[Backend(Opaque)]
finite law i32[0 max] Hash(i32[0 max] value) {
    return value;
}
```

`[Backend(Opaque)]` is not a visibility modifier. Importers still see the
visible `internal`, `public`, and `export` declarations according to the normal
rules. The attribute only says that backend whole-program optimization must
treat the marked module, callable, type, or doctrine as a compiled boundary:
callers should not import the affected bodies for ThinLTO, cross-module
inlining, backend cloning, or backend specialization.

Most application modules should omit it. Use it for runtime, platform, and
interop code when a real backend boundary is part of the implementation
contract. Prefer the narrowest boundary that preserves correctness and
performance.

## Step 3: Widen Visibility Deliberately

Stark defaults to module-private declarations. Widen visibility only when there
is a real reason:

- no keyword: visible only in the current module
- `internal`: visible inside the package
- `public`: visible to downstream Stark source
- `export`: visible as an ABI symbol

`public` and `export` are separate on purpose. A function can be part of a Stark
package API without becoming a binary symbol for FFI or a native runtime entry.

Use `export` for boundaries like:

- `export fn main`
- FFI-facing functions
- intentionally stable binary entry points

Use `public` for ordinary Stark APIs consumed by another Stark package.

{{< stark-sample "assets/book/samples/package-surface.stark" >}}

This sample has all three visibility ideas in one small package surface:

- `Rectangle` and `Area` are `public` because they are the Stark API
- `Multiply` is `internal` because it is a package helper
- `main` is `export` because it is a native entrypoint symbol

In a real package, the public API and the entrypoint usually live in different
projects. The distinction is the same: `public` is for Stark source consumers;
`export` is for ABI-visible boundaries.

## Step 4: Put Project Shape In `Stark.toml`

A Stark project is described by `Stark.toml`:

```toml
[project]
name = "modules"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "modules"
```

The manifest says what kind of project this is, which file is the root, and
what output name to use.

Path dependencies live in the same file:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

This keeps package dependencies in project metadata instead of scattering build
flags through scripts.

## Step 5: Add A Solution Only For Multi-Project Work

A multi-project workspace can use `Stark.solution.toml`:

```toml
[solution]
name = "Examples"
members = [
  "hello",
  "modules",
  "borrowing"
]

[defaults]
build = ["modules"]
run = "modules"

[aliases]
modules = "modules"
borrowing = "borrowing"
```

The solution manifest answers workspace-level questions:

- which projects belong to the solution
- what builds by default
- what runs by default
- which short aliases are accepted from the solution root

Single-project repositories do not need a solution file.

## Step 6: Keep Native Facts With The Wrapping Package

Native-backed packages should own their native metadata. A downstream Stark
project should be able to depend on the package without repeating include paths
or linker flags.

The book's native-package chapter expands this topic, but the package rule is
simple: FFI setup belongs with the package that wraps the native API.
