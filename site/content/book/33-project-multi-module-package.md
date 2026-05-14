+++
title = "33. Project: Multi-Module Package"
weight = 330
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/32-project-command-line-text/"
next = "/book/34-project-file-processing/"
aliases = ["/book/31-project-multi-module-package/"]

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
+++

# Project: Multi-Module Package

This project chapter starts from one public API and splits it into files,
visibility, a package manifest, and a solution that can consume the package.

## Step 1: Start From The Public Surface

Build a small package with:

- internal helpers
- a public Stark API
- explicit imports
- optional re-exports
- a `Stark.toml` manifest
- a solution manifest that can build an app consuming the package

Use the `examples/modules` and `examples/multi-module` directories as concrete
repository examples to mirror.

The package-surface sample from the modules chapter is the single-file nucleus
of this project:

{{< stark-sample "assets/book/samples/package-surface.stark" >}}

The project version splits that shape across files: a public geometry module, a
small internal helper, and an executable app that consumes the public API.

## Step 2: Split The Surface Across Modules

Each source file declares one module:

```stark
import Geometry
import Units
module App
```

Keep helpers module-private by default. Use `internal` for helpers shared
inside the package. Use `public` for declarations downstream Stark source
should import.

## Step 3: Re-Export Only The Modules You Mean To Publish

Use `export import` only when the package deliberately republishes another
module as part of its own source API.

Plain `import` is for local name resolution. It is not a package API promise.

## Step 4: Avoid Turning Source APIs Into ABI

Do not use `export` for ordinary package APIs. `export` is for ABI-visible
symbols such as entrypoints and FFI boundaries.

Most library declarations should be `public` or `internal`, not `export`.

## Step 5: Wire The Package And App Into A Solution

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
