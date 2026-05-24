+++
title = "37. Project: Multi-Module Package"
weight = 370
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/36-project-command-line-text/"
next = "/book/38-project-file-processing/"
aliases = ["/book/31-project-multi-module-package/", "/book/33-project-multi-module-package/", "/book/34-project-multi-module-package/"]

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

{{< stark-sample "samples/package-surface.stark" >}}

The project version splits that shape across files: a public geometry module, a
small internal helper, and an executable app that consumes the public API.

One simple directory shape is:

```text
geometry/
  Stark.toml
  Geometry.stark
  InternalMath.stark
app/
  Stark.toml
  App.stark
Stark.solution.toml
```

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

The default is module-private:

```stark
module Geometry.InternalMath

finite law i32[min max] Square(i32[min max] value)
{
    return value * value;
}
```

Sibling modules cannot call `Square` until you deliberately widen visibility.

`geometry/InternalMath.stark` can hold package-only helpers:

```stark
module Geometry.InternalMath

internal finite law i32[min max] Multiply(u16[0 1000] left, u16[0 1000] right)
{
    return (i32[min max])left * (i32[min max])right;
}
```

`geometry/Geometry.stark` can expose the actual package API:

```stark
import Geometry.InternalMath
module Geometry

public struct Rectangle
{
    u16[0 1000] Width;
    u16[0 1000] Height;
}

public finite law i32[min max] Area(Rectangle rectangle)
{
    return Multiply(rectangle.Width, rectangle.Height);
}
```

`app/App.stark` imports only the public surface:

```stark
import Geometry
module App

export fn i32[min max] main()
{
    stack Geometry.Rectangle rectangle = new Geometry.Rectangle()
    {
        Width = 6,
        Height = 7
    };

    if (Geometry.Area(rectangle) != 42)
    {
        return 1;
    }

    return 0;
}
```

The package manifest names the library root:

```toml
[project]
name = "geometry"
version = "0.1.0"
kind = "library"

[library]
root = "Geometry.stark"
output = "Geometry"
```

The app manifest depends on that package:

```toml
[project]
name = "geometry-app"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "geometry-app"

[dependencies]
geometry = { path = "../geometry" }
stdlib = { path = "../../stdlib" }
```

If the app needs another source module from the same app package, import that
module explicitly:

```stark
import App.Commands
module App
```

There is no wildcard import. When two imported modules expose the same short
name, write the qualified name at the use site:

```stark
stack Geometry.Rectangle rectangle = new Geometry.Rectangle()
{
    Width = 6,
    Height = 7
};
```

## Step 3: Re-Export Only The Modules You Mean To Publish

Use `export import` only when the package deliberately republishes another
module as part of its own source API.

Plain `import` is for local name resolution. It is not a package API promise.

For example, a package can re-export its main public module:

```stark
export import Geometry
module Geometry.Package
```

Downstream code can import `Geometry.Package` and receive the names that module
deliberately republishes.

Do not use `export import` for private convenience imports:

```stark
import Geometry.InternalMath
module Geometry
```

That import is only for this file.

## Step 4: Avoid Turning Source APIs Into ABI

Do not use `export` for ordinary package APIs. `export` is for ABI-visible
symbols such as entrypoints and FFI boundaries.

Most library declarations should be `public` or `internal`, not `export`.

Member functions follow the enclosing type unless you narrow them:

```stark
public struct PackageClient
{
    fn bool IsOpen(self)
    {
        return true;
    }

    internal fn i64[min max] RuntimeHandle(self)
    {
        return 0;
    }
}
```

Here `IsOpen` is public because the type is public. `RuntimeHandle` is narrowed
to package-only use.

A member cannot be more visible than its enclosing type:

```stark
internal struct InternalCounter
{
    fn i32[min max] Current(self)
    {
        return 0;
    }
}
```

`Current` is internal because the type is internal. Make the type public first
if downstream packages should name the member.

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

From the solution root, use the aliases you declared:

```bash
stark build geometry
stark run app
```

From inside a project directory, the nearest `Stark.toml` is enough:

```bash
stark build
```
