+++
title = "31. Project: Multi-Module Package"
weight = 310
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/30-project-command-line-text/"
next = "/book/32-project-file-processing/"

[[language_refs]]
title = "Modules and Visibility"
href = "/reference/docs/Userfacing/ModulesAndVisibility.md"

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/docs/Userfacing/ProjectsAndSolutions.md"

[[example_refs]]
title = "Modules Example"
href = "/reference/examples/modules/App.stark"

[[example_refs]]
title = "Multi-Module Example"
href = "/reference/examples/multi-module/App.stark"
+++

# Project: Multi-Module Package

This project chapter builds a package with explicit module boundaries.

## Goal

Build a small package with:

- internal helpers
- a public Stark API
- explicit imports
- optional re-exports
- a `Stark.toml` manifest
- a solution manifest that can build an app consuming the package

The `examples/modules` and `examples/multi-module` directories are the current
repository examples to mirror.

The package-surface sample from the modules chapter is the single-file nucleus
of this project:

{{< stark-sample "assets/book/samples/package-surface.stark" >}}

The project version splits that shape across files: a public geometry module, a
small internal helper, and an executable app that consumes the public API.

## Module Shape

Each source file declares one module:

```stark
import Geometry
import Units
module App
```

Keep helpers module-private by default. Use `internal` for helpers shared
inside the package. Use `public` for declarations downstream Stark source
should import.

## Re-Exports

Use `export import` only when the package deliberately republishes another
module as part of its own source API.

Plain `import` is for local name resolution. It is not a package API promise.

## Avoid Accidental ABI

Do not use `export` for ordinary package APIs. `export` is for ABI-visible
symbols such as entrypoints and FFI boundaries.

Most library declarations should be `public` or `internal`, not `export`.

## Solution Manifest

A solution can name both the package and a consuming app:

```toml
[solution]
name = "GeometryWorkspace"
members = [
  "geometry",
  "app"
]

[defaults]
build = ["app"]
run = "app"

[aliases]
app = "app"
geometry = "geometry"
```

The solution manifest answers workspace questions. The package manifest answers
project questions.
