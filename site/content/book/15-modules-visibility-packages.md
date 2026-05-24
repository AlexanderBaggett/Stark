+++
title = "15. Modules, Visibility, and Packages"
weight = 150
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/14-arrays-slices-text/"
next = "/book/16-function-guarantees/"
aliases = ["/book/14-modules-visibility-packages/"]

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

One file declares one module. Do not split one module across several source
files, and do not put several module declarations in one file.

The usual file shape is:

```stark
import System.Text

module Tools.Format

public finite law bool IsDash(ascii text)
{
    return AsciiLength(text) == 1 && text[0] == "-";
}
```

Imports are explicit. There is no wildcard import. Importing a module lets you
use visible top-level declarations from that module by their short names. When
two imports expose the same final name, use the qualified name at the call site:

```stark
import Geometry
import Drawing

module App

fn i32[min max] UseGeometryRectangle(Geometry.Rectangle rectangle)
{
    return Geometry.Area(rectangle);
}
```

Declarations in the current module win over imported names. If that would make
a file hard to read, choose a more specific local name instead of relying on
the tie-breaker.

## Step 2: Widen Visibility Deliberately

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

The common patterns are:

```stark
module Geometry

const DefaultScale = 1;

fn i32[min max] PrivateHelper(i32[min max] value)
{
    return value + 1;
}

internal fn i32[min max] PackageHelper(i32[min max] value)
{
    return PrivateHelper(value);
}

public fn i32[min max] Area(i32[min max] width, i32[min max] height)
{
    return width * height;
}

export fn i32[min max] main()
{
    return Area(3, 4);
}
```

Use the narrowest visibility that matches the caller you actually want.

Visibility applies to the ordinary top-level things you write in Stark:

```stark
public const DefaultWidth = 80;

internal alias Score = i32[min max];

public struct Rectangle
{
    u16[0 1000] Width;
    u16[0 1000] Height;
}

public record Point
{
    i32[min max] X;
    i32[min max] Y;
}

public enum ShapeKind
{
    Point,
    Rectangle,
}

public trait Named
{
    law ascii Name();
}

public doctrine ScoreRules
{
    finite law bool IsPassing(u8[0 100] score)
    {
        return score >= 70;
    }
}
```

Use module-private declarations for helpers that only this file needs. Use
`internal` for helpers shared across files in one package. Use `public` for the
Stark API other packages should import.

## Step 3: Narrow Member Visibility When Needed

Member functions inside `struct` and `record` declarations inherit the
visibility of the enclosing type unless the member writes its own visibility.
That makes small public types pleasant to write:

```stark
public struct Counter
{
    u32[0 max] Value;

    finite law u32[0 max] Read(self)
    {
        return self.Value;
    }
}
```

`Read` is public because `Counter` is public. Narrow a member when downstream
code should not call it:

```stark
public struct PackageClient
{
    finite law bool IsOpen(self)
    {
        return true;
    }

    internal finite law i64[min max] RuntimeHandle(self)
    {
        return 0;
    }
}
```

A member cannot be more visible than its enclosing type:

```stark
internal struct PlatformSocket
{
    finite law bool IsOpen(self)
    {
        return true;
    }

    // Not allowed: a public member cannot sit on an internal type.
    public finite law i64[min max] Handle(self)
    {
        return 0;
    }
}
```

Fields do not have separate visibility keywords in the current language
surface. Put representation-sensitive helper functions in the same module, and
publish only the functions that callers should use.

## Step 4: Re-Export Only When It Is Part Of Your API

Use `export import` when your package intentionally republishes another module:

```stark
export import Geometry.Units
module Geometry

public struct Rectangle
{
    u16[0 1000] Width;
    u16[0 1000] Height;
}
```

Plain `import` is local to the file. It helps this file resolve names, but it
does not make the imported module part of your package's public source API.

```stark
import Geometry.InternalMath
module Geometry

public finite law i32[min max] Area(Rectangle rectangle)
{
    return Multiply(rectangle.Width, rectangle.Height);
}
```

This is the right shape when `Multiply` is a package helper and callers should
continue to call `Geometry.Area`.

## Step 5: Put Project Shape In `Stark.toml`

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

A library uses the library form:

```toml
[project]
name = "geometry"
version = "0.1.0"
kind = "library"

[library]
root = "Geometry.stark"
output = "Geometry"
```

A test project names a test root:

```toml
[project]
name = "geometry-tests"
version = "0.1.0"
kind = "test"

[test]
root = "GeometryTests.stark"
output = "geometry-tests"

[dependencies]
geometry = { path = "../geometry" }
stdlib = { path = "../../stdlib" }
```

Path dependencies live in the same file:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

This keeps package dependencies in project metadata instead of scattering build
flags through scripts.

## Step 6: Add A Solution Only For Multi-Project Work

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

## Step 7: Keep Native Settings With The Wrapping Package

Native-backed packages should own their native metadata. A downstream Stark
project should be able to depend on the package without repeating include paths
or linker flags.

The book's native-package chapter expands this topic, but the package rule is
simple: FFI setup belongs with the package that wraps the native API.
