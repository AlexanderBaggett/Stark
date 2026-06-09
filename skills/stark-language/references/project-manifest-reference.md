# Stark Project And Manifest Reference

Stark uses TOML manifests. A `Stark.toml` describes one project. A
`Stark.solution.toml` describes a workspace of projects.

## Project Manifest

Executable project:

```toml
[project]
name = "hello"
version = "0.1.0"
kind = "executable"

[executable]
root = "../hello.stark"
output = "hello"

[dependencies]
stdlib = { path = "../../stdlib" }

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Library project:

```toml
[project]
name = "math-core"
version = "0.1.0"
kind = "library"

[library]
root = "MathCore.stark"
output = "MathCore"

[dependencies]
stdlib = { path = "../../stdlib" }
```

Test project:

```toml
[project]
name = "math-tests"
version = "0.1.0"
kind = "test"

[test]
root = "MathTests.stark"
output = "math-tests"

[dependencies]
stdlib = { path = "../../stdlib" }
math-core = { path = "../math-core" }
```

Test projects compile to executables. Return `0` for success.

## Native-Backed Library

Native-backed packages own their native metadata so consumers do not repeat
linker flags.

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

[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11"]
```

Machine-local paths belong in user config or ignored user manifests:

```toml
[native.paths]
raylib-src = "/path/to/raylib/src"
```

## Solution Manifest

```toml
[solution]
name = "Examples"
members = [
  "hello",
  "basic-syntax",
  "standard-library-tests",
]

[defaults]
build = [
  "basic-syntax",
]
run = "basic-syntax"
test = [
  "standard-library-tests",
]

[aliases]
hello = "hello"
stdlib-tests = "standard-library-tests"

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Use aliases for shorter command names, not for changing source module names.

## Common Commands

```bash
stark build
stark run
stark test
stark build app --release
stark run hello
stark test stdlib-tests
stark test stdlib-tests --filter Integer
```

Manifest discovery searches upward. The nearest `Stark.toml` runs in project
mode. The nearest `Stark.solution.toml` runs in solution mode.

For `kind = "test"` projects, `stark test` generates an explicit `main` runner
when the root contains `[Fact]` metadata. `[Fact]` functions must be
non-generic, no-argument `bool` functions with a body. The generated runner
enumerates facts at build time, applies repeatable `--filter <text>` selections
against fact display names, calls `System.Testing.RunFact`, and returns a stable
process exit code. Do not declare a manual `main` in a `[Fact]` test root;
manual runners are reserved for no-`[Fact]` bootstrap tests.

## Source And Package Shape

Each source file declares one module:

```stark
import System.Console
module App

export fn i32[min max] main()
{
    WriteLine("Hello");
    return 0;
}
```

Imports are source-level name resolution. Package dependencies are manifest
entries. You usually need both:

```stark
import Geometry
module App
```

```toml
[dependencies]
geometry = { path = "../geometry" }
```

## Output Layout

Build outputs and caches live under the project or solution work area:

```text
.stark/build/dev
.stark/build/release
.stark/cache
.stark/packages
```

Keep generated outputs out of source examples unless the task specifically
asks to inspect them.

## Manifest Review Checklist

- `kind` matches the section present: `[executable]`, `[library]`, or `[test]`.
- `root` points at the intended root `.stark` file.
- `output` is the artifact name, not a source module name.
- Dependencies name packages, not imported modules.
- Native metadata lives in the package that owns the FFI wrapper.
- Machine-local native paths are not committed into shared manifests.
- Profiles use explicit optimization levels for development and release.
