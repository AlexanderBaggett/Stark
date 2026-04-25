# Stark Projects and Solutions

This document describes the initial user-facing project, solution, and package
workflow for Stark, plus the intended shape for the parts that are still
growing.

It is intentionally about ease of use first:

- normal builds should not require long command lines
- native dependency setup should belong to the project or package
- users should be able to type `stark build` and `stark run`; `stark test`
  remains an intended follow-up workflow
- machine-local native paths should live in user config, not in checked-in
  project files

The repository includes an initial project driver for building projects and
running solution defaults. The lower-level compiler CLI remains available for
direct compilation and advanced workflows.

## Goals

The design should make these cases simple:

- build a single Stark project from its directory
- build a whole solution from its root
- run an executable project with one short command
- consume a native-backed package such as Raylib without repeating linker flags
- keep local machine setup separate from shared project files

## Core Files

The proposal uses three TOML files.

### `Stark.toml`

This is the project manifest.

It lives in a project directory and describes:

- the project name
- whether the project is a library or executable
- the root Stark file
- dependencies
- native package metadata
- build profiles

### `Stark.solution.toml`

This is the solution-level manifest.

It lives at the root of a multi-project repository or solution and describes:

- which projects belong to the solution
- default build and run targets
- aliases
- shared profile defaults

The solution file is optional for a single standalone project.

### User Config

Machine-local configuration should live outside the shared project files.

Recommended locations:

- `~/.config/stark/config.toml`
- optionally a repo-local ignored file such as `Stark.user.toml`

User config is where local native paths should go.

Examples:

- the path to a local Raylib `src` directory
- custom tool locations
- local SDK or system library overrides

## Command Behavior

The command behavior should be simple and predictable.

### Manifest Discovery

The `stark` command should search upward from the current working directory.

The nearest matching manifest wins:

- if the nearest manifest is `Stark.toml`, treat the current directory tree as a
  project
- if the nearest manifest is `Stark.solution.toml`, treat the current directory
  tree as a solution

This means:

- inside a project directory, `stark build` builds that project
- inside a solution directory, `stark build` builds that solution

### `stark build`

`stark build` should behave like this:

- in a project directory: build the current project
- in a solution directory: build the current solution

When run from a solution directory:

- if the solution declares a default build set, build that
- otherwise build all buildable member projects

Examples:

```bash
stark build
stark build breakout
stark build raylib --release
```

### `stark run`

`stark run` should behave like this:

- in an executable project directory: build and run that project
- in a solution directory: build and run the solution default run target

If a solution contains multiple runnable projects and no default run target is
set, `stark run` should stop with a friendly message and ask the user to choose
one by name.

Examples:

```bash
stark run
stark run breakout
stark run --release
```

### `stark test`

`stark test` is the intended project-test workflow, but it is not implemented
yet. It depends on the v2.0 test-project model and a Stark-native
standard-library testing module, likely `System.Testing`, that ports the useful
parts of the current xUnit-style test vocabulary into Stark.

When implemented, it should behave like this:

- in a project directory: run tests for that project
- in a solution directory: run tests for the solution test set, or all test
  projects if no test set is declared

Examples:

```bash
stark test
stark test compiler.IntegrationTests
```

## Project Manifest Shape

This is the proposed shape of `Stark.toml`.

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

For a library:

```toml
[project]
name = "raylib"
version = "0.1.0"
kind = "library"

[library]
root = "Raylib.stark"
output = "RaylibStark"
```

## Solution Manifest Shape

This is the proposed shape of `Stark.solution.toml`.

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

The solution file should stay small.

It is there to answer:

- what projects belong to this solution
- what should build by default
- what should run by default
- what short names should work from the solution root

## Dependencies

The initial dependency model should stay simple.

Path dependencies are enough for the first version:

```toml
[dependencies]
raylib = { path = "../raylib" }
stdlib = { path = "../../stdlib" }
```

That covers:

- examples
- standard library development
- multi-project repositories
- native-backed packages inside the same solution

Registry and versioned dependencies can come later.

## Native Packages

Native dependency metadata should belong to the package that owns it.

For Raylib, the package manifest should declare:

- package-owned native shim sources
- package-owned discovery names such as `pkg-config`
- fallback native metadata for systems where discovery is unavailable

Example:

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

Then user-local config can supply the machine path:

```toml
[native.paths]
raylib-src = "/tmp/stark-raylib-research/raylib-5.5/src"
```

That makes the normal user command short:

```bash
stark run breakout
```

No repeated `--native-library`, `--native-include-dir`, or shell script
maintenance should be required for routine use.

## Friendly Native Resolution Rules

For a native-backed dependency, the build driver should use a simple order:

1. try package-declared discovery such as `pkg-config`
2. if that fails, try user-local native path overrides
3. if that still fails, stop early with a friendly message

The error should say what the user can do next in plain English:

- install the native package
- set a config value such as `native.paths.raylib-src`
- or choose a different local path

## Output Layout

Build artifacts should live under a tool-owned directory instead of cluttering
source folders.

Suggested layout:

```text
.stark/
  build/
    dev/
    release/
  cache/
  packages/
```

That gives:

- stable output locations
- fewer stray binaries in source directories
- room for package images, cached metadata, and temp objects

## Example: Breakout and Raylib

With this project system, the Raylib and Breakout examples should look like
this.

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

### Solution Root `Stark.solution.toml`

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

Then the user experience becomes:

```bash
stark build
stark run
```

Or:

```bash
stark run breakout
```

## Low-Level Escape Hatch

The low-level compiler CLI should still exist for advanced use.

A good split would be:

- `stark` for project, solution, dependency, build, and run workflows today,
  with test workflows added later
- `starkc` for direct low-level compilation

That keeps advanced workflows available without forcing ordinary users to think
in terms of linker flags and manual package assembly.

## Summary

This proposal aims to make Stark feel easy to build and run without weakening
its performance-focused design.

The core idea is:

- `Stark.toml` describes a project
- `Stark.solution.toml` describes a solution
- user config holds machine-local native paths
- `stark build` and `stark run` are the normal implemented workflow today;
  `stark test` remains future work

That should remove most shell scripts, repeated flags, and fragile local command
lines from everyday Stark development.
