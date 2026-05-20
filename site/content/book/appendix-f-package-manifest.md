+++
title = "Appendix F: Package Manifest Reference"
weight = 420
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-e-storage-classes/"
next = "/book/appendix-g-current-boundaries/"

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/language/ProjectsAndSolutions/"

[[example_refs]]
title = "Examples Solution Manifest"
href = "/reference/examples/Stark.solution.toml"

[[example_refs]]
title = "Hello Project Manifest"
href = "/reference/examples/hello/Stark.toml"

[[example_refs]]
title = "Raylib Project Manifest"
href = "/reference/examples/raylib/Stark.toml"
+++

# Appendix F: Package Manifest Reference

Stark project and solution manifests use TOML.

## `Stark.toml`

Executable projects use `[executable]`:

{{< file-sample "static/reference/examples/hello/Stark.toml" "toml" >}}

Library projects use `[library]`:

{{< file-sample "static/reference/examples/static-library/Stark.toml" "toml" >}}

## Native Metadata

Native-backed packages keep their native requirements in the package manifest:

{{< file-sample "static/reference/examples/raylib/Stark.toml" "toml" >}}

Machine-local paths belong in user config:

```toml
[native.paths]
raylib-src = "/path/to/raylib/src"
```

## `Stark.solution.toml`

{{< file-sample "static/reference/examples/Stark.solution.toml" "toml" >}}

The project manifest describes one project. The solution manifest describes a
workspace.
