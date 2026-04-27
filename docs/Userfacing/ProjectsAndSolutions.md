# Stark Projects and Solutions

Projects, solutions, and packages in Stark.

## Goals

Normal builds should not need long command lines.

* `stark build` and `stark run` should just work
* native dependencies belong to the package, not to every project that uses it
* machine specific paths belong in user config, not in checked in files
* `stark test` is planned but not implemented yet

Stark ships with a project driver for builds and runs. The lower level compiler CLI is still available for advanced workflows.

## Cases That Should Be Simple

* build a single Stark project from its directory
* build a whole solution from its root
* run an executable project
* use a native backed package such as Raylib without repeating linker flags
* keep local machine setup separate from shared project files

## The Three Files

### `Stark.toml`

Project manifest. Lives in a project directory. Describes:

* project name
* library or executable
* root Stark file
* dependencies
* native package metadata
* build profiles

### `Stark.solution.toml`

Solution manifest. Lives at the root of a multi project repo. Describes:

* member projects
* default build and run targets
* aliases
* shared profile defaults

Optional for a single standalone project.

### User Config

Machine local config lives outside shared project files. Locations:

* `~/.config/stark/config.toml`
* a repo local ignored file such as `Stark.user.toml`

Use it for local native paths, custom tool locations, and local SDK or system library overrides.

## Command Behavior

### Manifest Discovery

The `stark` command searches upward from the current directory. The nearest manifest wins.

* `Stark.toml` nearest: project mode
* `Stark.solution.toml` nearest: solution mode

### `stark build`

* in a project directory: builds that project
* in a solution directory: builds that solution

When run from a solution, the declared default build set is used. If none is declared, every buildable member is built.

```bash
stark build
stark build breakout
stark build raylib --release
```

### `stark run`

* in an executable project directory: builds and runs that project
* in a solution directory: builds and runs the solution default run target

If a solution has multiple runnable projects and no default, `stark run` stops with a message asking for a name.

```bash
stark run
stark run breakout
stark run --release
```

### `stark test`

Planned. Not implemented yet. Depends on the v2.0 test project model and a Stark native testing module, likely `System.Testing`.

When implemented:

* in a project directory: runs that project's tests
* in a solution directory: runs the declared test set, or every test project if none is declared

```bash
stark test
stark test compiler.IntegrationTests
```

## `Stark.toml`

```toml
[project]
name = "breakout"
version = "0.1.0"
kind = "executable"

[executable]
root = "BreakoutRaylib.stark"
output = "breakout-raylib"

[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Library form:

```toml
[project]
name = "raylib"
version = "0.1.0"
kind = "library"

[library]
root = "Raylib.stark"
output = "RaylibStark"
```

## `Stark.solution.toml`

```toml
[solution]
name = "StarkRepo"
members = [
  "stdlib",
  "examples/raylib",
  "examples/breakout"
]

[defaults]
build = ["examples/breakout"]
run = "examples/breakout"

[aliases]
breakout = "examples/breakout"
raylib = "examples/raylib"
stdlib = "stdlib"

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

The solution file stays small. It answers four questions:

* what projects belong to this solution
* what builds by default
* what runs by default
* what short names work from the root

## Dependencies

Path dependencies cover v1:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

That handles examples, standard library work, multi project repos, and native backed packages inside the same solution. Versioned and registry dependencies come later.

## Native Packages

Native dependency metadata belongs to the package that needs it.

A package manifest declares its own:

* native shim sources
* discovery names such as `pkg-config`
* fallback metadata for systems where discovery fails

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
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11", "Xrandr", "Xi", "Xcursor", "Xinerama"]
```

User config supplies the machine path:

```toml
[native.paths]
raylib-src = "/tmp/stark-raylib-research/raylib-5.5/src"
```

The everyday command stays short:

```bash
stark run breakout
```

No `--native-library`, `--native-include-dir`, or shell scripts.

## Native Resolution Order

For a native backed dependency, the build driver tries:

1. package declared discovery (`pkg-config` and similar)
2. user local native path overrides
3. stop with a friendly message

The error names the next step in plain English: install the native package, set a config value such as `native.paths.raylib-src`, or pick a different local path.

## Output Layout

Build artifacts live under a tool owned directory:

```text
.stark/
  build/
    dev/
    release/
  cache/
  packages/
```

Stable output locations, no stray binaries in source folders, room for package images and cached metadata.

## Example: Breakout and Raylib

### `examples/raylib/Stark.toml`

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
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11", "Xrandr", "Xi", "Xcursor", "Xinerama"]
```

### `examples/breakout/Stark.toml`

```toml
[project]
name = "breakout"
version = "0.1.0"
kind = "executable"

[executable]
root = "BreakoutRaylib.stark"
output = "breakout-raylib"

[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

### `Stark.solution.toml`

```toml
[solution]
name = "Examples"
members = [
  "examples/raylib",
  "examples/breakout"
]

[defaults]
build = ["examples/breakout"]
run = "examples/breakout"

[aliases]
breakout = "examples/breakout"
raylib = "examples/raylib"
```

From the solution root:

```bash
stark build
stark run
```

Or by name:

```bash
stark run breakout
```

## Low Level Escape Hatch

The lower level compiler CLI is still available. The split:

* `stark`: projects, solutions, dependencies, builds, runs (and tests, eventually)
* `starkc`: direct low level compilation

## Summary

* `Stark.toml` describes a project
* `Stark.solution.toml` describes a solution
* user config holds machine local native paths
* `stark build` and `stark run` are the everyday commands
* `stark test` is planned
