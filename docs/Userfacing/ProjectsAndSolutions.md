# Stark Projects and Solutions

Projects, solutions, and packages in Stark.

## Goals

Normal builds should not need long command lines.

* `stark build` and `stark run` should just work
* native dependencies belong to the package, not to every project that uses it
* machine specific paths belong in user config, not in checked in files
* `stark test` should build and run test projects without a separate harness command

Stark ships with a project driver for builds, runs, and test projects. The lower level compiler CLI is still available for advanced workflows.

## Cases That Should Be Simple

* build a single Stark project from its directory
* build a whole solution from its root
* run an executable project
* run a test project or a solution-level test set
* use a native backed package such as Raylib without repeating linker flags
* keep local machine setup separate from shared project files

## The Three Files

### `Stark.toml`

Project manifest. Lives in a project directory. Describes:

* project name
* library or executable
* test project
* root Stark file
* dependencies
* native package metadata
* build profiles

### `Stark.solution.toml`

Solution manifest. Lives at the root of a multi project repo. Describes:

* member projects
* default build, run, and test targets
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
* `--target <triple>` selects the target triple used for both codegen and the build output path
* `--stage stage0` selects the current C# host compiler stage; Stage1/Stage2 selectors are reserved until those compilers exist

When run from a solution, the declared default build set is used. If none is declared, every buildable member is built.

```bash
stark build
stark build breakout
stark build raylib --release
stark build --target x86_64-unknown-linux-gnu --stage stage0
```

### Incremental builds

`stark build`, `stark run`, and `stark test` are incremental. Each project's
outputs are stamped (in a `.stark-build-stamp` file beside them) with a hash of
everything that can change the result:

* the project's own `*.stark` sources and its manifest
* the standard-library sources it compiles against
* every dependency project's own stamp (so a transitive source change propagates)
* the selected target triple, profile, and `--filter`s
* the compiler binary itself, so a newer compiler invalidates every stamp

When nothing relevant has changed, the build is skipped and the existing
executable, library, and package image are reused. When any input changes, that
project's stale outputs — executable or library, the generated test runner,
intermediate files, and any emitted package image — are removed before it is
rebuilt, so a leftover package image can never shadow fresh source. Only the
projects whose inputs changed are rebuilt; editing one test suite does not
recompile the standard library.

Because of this, `stark test` reports trustworthy pass/fail counts immediately
after an edit — there is no need to delete the `build` directory by hand. To
force a full rebuild anyway, run `stark clean` or remove the `build` directory.

### `stark run`

* in an executable project directory: builds and runs that project
* in a solution directory: builds and runs the solution default run target
* supports the same `--target <triple>` and `--stage stage0` selectors as `stark build`

If a solution has multiple runnable projects and no default, `stark run` stops with a message asking for a name.

```bash
stark run
stark run breakout
stark run --release
```

### `stark test`

* in a test project directory: builds and runs that project's test executable
* in a solution directory: runs the declared test set, or every member with `kind = "test"` if none is declared
* a target name can be a solution alias, member path, or project name
* supports the same `--target <triple>` and `--stage stage0` selectors as `stark build`
* `[Fact]` and `[Theory]` tests use a generated explicit `main` runner; there is no runtime reflection
* assertions are ordinary Stark functions from `System.Testing`
* `--filter <text>` can be repeated to run only generated test names containing the filter text
* `[Theory]` rows can come from `[InlineData(...)]` constants or typed indexed `[MemberData(provider, rowType, count, ...fields)]` providers
* `[Platform(...)]` and `[SkipPlatform(...)]` gates on facts, structs, and records are resolved from the selected target triple at build time
* `[Collection(name)]` and `[Serial]` on facts, structs, and records create stable serial scheduling groups in the generated runner
* `--collection <name[,name...]>` can be repeated and comma-splits each value to run only facts tagged with the named `[Collection]`s, with union semantics across selections
* `--list-collections` prints the project's collection names without running any facts

```bash
stark test
stark test standard-library-tests
stark test standard-library-tests --filter Integer
stark test standard-library-tests --collection ownership,lexing
stark test standard-library-tests --list-collections
```

### `stark clean`

* deletes artifacts under the formal `build/<profile>/<target-triple>/<stage>/` layout
* default scope is `stage`
* `target`, `stage`, `diagnostics`, and `artifacts` use `--target <triple>` or the detected default target
* `profile` deletes `build/<profile>/` and does not require target discovery

```bash
stark clean
stark clean stage --target x86_64-unknown-linux-gnu
stark clean target --target x86_64-unknown-linux-gnu
stark clean profile
stark clean diagnostics --target x86_64-unknown-linux-gnu
stark clean artifacts --target x86_64-unknown-linux-gnu
```

Project command outputs use the formal build layout:

```text
build/<profile>/<target-triple>/<stage>/
  bin/<project>/
  obj/<project>/
  pkg/<project>/
  tests/<project>/
```

The current host driver writes Stage0 executable/library outputs under `bin`,
saved native intermediates under `obj`, and test executables plus generated
`[Fact]` runners under `tests`. Library package images go under `pkg` and can
refer back to the static library with a relative path. Project builds search the
active stage's `stdlib` directory for stage-local `System` artifacts, then the
nearest repo `stdlib/dist` package images, then the nearest repo `stdlib/src`
source tree for source-tree development, then bundled stdlib artifacts next to
the active compiler distribution. Project builds do not use `STARK_PATH`; use
manifest dependencies for ordinary packages, future explicit stdlib overrides,
or direct low-level compiler `-I` inputs instead. Stdlib artifact
generation/routing, diagnostic, and artifact-export routing are still part of
self-hosting prep.
When a `System.*` import cannot be resolved, project builds report the searched
stdlib paths and the active profile, target, and stage.

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

Test form:

```toml
[project]
name = "standard-library-tests"
version = "0.1.0"
kind = "test"

[test]
root = "StandardLibraryTests.stark"
output = "standard-library-tests"
```

The test root is compiled as an executable. If it contains `[Fact]` metadata,
`stark test` generates the executable `main` at build time and returns `0` for
success or a non-zero exit code for failure. Manual `main` runners are still
available for bootstrap test executables with no generated test metadata.
`System.Testing` provides the assertion helpers used by generated runners:

```stark
import System.Testing
module DemoTests

[Fact]
fn bool AddsNumbers()
{
    return System.Testing.Equal(4, 2 + 2);
}

[Fact]
[Platform(linux.x64)]
fn bool LinuxToolchainProbe()
{
    return true;
}

[Serial]
struct ToolchainState
{
    [Fact]
    static fn bool UsesSharedInstall()
    {
        return true;
    }
}

[Theory]
[InlineData(2, 2, 4)]
[InlineData(-3, 5, 2)]
finite law bool AddsExamples(i32[min max] left, i32[min max] right, i32[min max] expected)
{
    return left + right == expected;
}

record AddRow(i32[min max] Left, i32[min max] Right, i32[min max] Expected) { }

finite law AddRow AddRows(u64[0 2 ** 63 - 1] index)
{
    switch (index)
    {
        case 0:
            return new AddRow(2, 2, 4);
        default:
            return new AddRow(-3, 5, 2);
    }
}

[Theory]
[MemberData(AddRows, AddRow, 2, Left, Right, Expected)]
finite law bool AddsMemberExamples(i32[min max] left, i32[min max] right, i32[min max] expected)
{
    return left + right == expected;
}
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
test = ["examples/standard-library-tests"]

[aliases]
breakout = "examples/breakout"
raylib = "examples/raylib"
stdlib = "stdlib"

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

The solution file stays small. It answers five questions:

* what projects belong to this solution
* what builds by default
* what runs by default
* what tests by default
* what short names work from the root

## Dependencies

Path dependencies cover v1:

```toml
[dependencies]
raylib = { path = "../raylib" }
```

That handles multi project repos and native backed packages inside the same
solution. `System.*` modules come from the standard library discovery path, so
projects do not list `stdlib` as an ordinary dependency. Versioned and registry
dependencies come later.

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
```

### `Stark.solution.toml`

```toml
[solution]
name = "Examples"
members = [
  "examples/raylib",
  "examples/breakout",
  "examples/standard-library-tests"
]

[defaults]
build = ["examples/breakout"]
run = "examples/breakout"
test = ["examples/standard-library-tests"]

[aliases]
breakout = "examples/breakout"
raylib = "examples/raylib"
standard-library-tests = "examples/standard-library-tests"
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

* `stark`: projects, solutions, dependencies, builds, runs, and tests
* `starkc`: direct low level compilation

## Summary

* `Stark.toml` describes a project
* `Stark.solution.toml` describes a solution
* user config holds machine local native paths
* `stark build`, `stark run`, and `stark test` are the everyday commands
