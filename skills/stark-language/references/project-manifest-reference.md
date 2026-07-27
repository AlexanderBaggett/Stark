# Stark Project And Manifest Reference

Stark uses TOML manifests. A `Stark.toml` describes one project. A
`Stark.solution.toml` describes a workspace of projects.

The stdlib ships a reusable TOML reader/writer, `System.Toml` (see
[`docs/StandardLibrary/System.Toml.md`](../../../docs/StandardLibrary/System.Toml.md)), which parses these manifest files. It is the library Stark manifest decoding builds on; wiring `System.Toml` into the project driver in place of the host-style `SimpleToml` handling is still pending.

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
math-core = { path = "../math-core" }
```

Test projects compile to executables. Return `0` for success.

Project manifests do not list the bundled standard library or official vendor
library as dependencies. Installed `System.*` and `Vendor.*` imports resolve
through the exact ownership index in the active SDK's `sdk.json`; development
source/package roots are explicit SDK-manifest inputs. Use `[dependencies]`
only for ordinary project/package dependencies outside those reserved roots.

## Native-Backed Library

Native-backed packages own their native metadata so consumers do not repeat
linker flags.

The example below is a custom/source package authoring surface. An installed
official `Vendor.Raylib` package is already target-built and carries relative,
checksummed native payload and link facts in the SDK; applications import it
without this manifest, `pkg-config`, or user native paths.

```toml
[project]
name = "vendor"
version = "0.1.0"
kind = "library"

[library]
root = "src/Vendor/Raylib.stark"
output = "VendorRaylib"

[native]
pkg-config = ["raylib"]

[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11"]
```

Fallback tables also accept `link-args` for package-owned linker arguments
such as macOS framework pairs; keep each linker token as its own array entry.

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
stark build --target x86_64-unknown-linux-gnu --stage stage0
stark clean stage --target x86_64-unknown-linux-gnu
stark clean profile
```

Manifest discovery searches upward. The nearest `Stark.toml` runs in project
mode. The nearest `Stark.solution.toml` runs in solution mode.

Project command outputs use `build/<profile>/<target-triple>/<stage>/`.
The current host driver supports `stage0`: executables and libraries go under
`bin/<project>/`, saved native intermediates under `obj/<project>/`, library
package images under `pkg/<project>/`, and test executables plus generated
`[Fact]` runners under `tests/<project>/`. Project builds use the explicit
stdlib discovery order below and ignore `STARK_PATH`. `--target <triple>`
selects the codegen target and path segment. Stage1/Stage2 selectors are
reserved until those compilers exist. `stark clean` defaults to the active stage
and also accepts explicit `profile`, `target`, `diagnostics`, and `artifacts`
scopes.

For `kind = "test"` projects, `stark test` generates an explicit `main` runner
when the root contains `[Fact]` or `[Theory]` metadata. `[Fact]` functions must
be non-generic, no-argument `bool` functions with a body. `[Theory]` functions
follow the same rules but may take parameters and must declare one or more
data rows from `[InlineData(...)]` constants or typed indexed
`[MemberData(provider, rowType, count, ...fields)]` providers. The generated
runner enumerates tests at build time, expands inline/member data rows, applies
repeatable `--filter <text>` selections against generated display names,
resolves `[Platform(...)]` / `[SkipPlatform(...)]` gates from the selected target
triple, calls `System.Testing.RunFact` or `System.Testing.SkipFact`, applies
`[Collection(name, ...)]` collection groups, and returns a stable
process exit code. Gate selectors can be OS names, architecture names,
`os.arch` pairs, or exact target triple strings.

`[Collection(...)]` is variadic — it names one or more collections (at least
one; an empty `[Collection()]` is rejected). Attach it to the `module`
declaration (tagging every `[Fact]`/`[Theory]` in the file), to a `struct`/
`record`, or to an individual fact/member; a fact's effective collection set
is the **union** of its module-level, type-level, and member-level names. The
generated runner groups tests by the **first** listed name (so single-name
uses keep their previous contiguous-group behavior) and preserves source order
inside each group. `[Serial]` remains shorthand for `[Collection("Serial")]`.

Collection filtering happens at **runtime** in the generated runner, so
changing the filter never recompiles. `stark test --collection NAME` runs only
the facts tagged with the named collections; it is repeatable and comma-splits
inside each value, with union semantics:

    stark test --collection ownership,lexing
    stark test --collection toml --collection manifests

`stark test --list-collections` prints the project's known collection names
without running anything. An unknown collection name is an error that lists the
known collections (typo protection), and a run that selects zero facts **fails**
rather than silently passing. Filtering is gated on the project having any
collections — untagged projects keep the previous runner behavior. v1 scope is
per-project (the project `stark test` runs in); solution-wide collection runs
are a follow-up.

Do not declare a manual `main` in a generated-test root; manual
runners are reserved for bootstrap tests with no generated test metadata.

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
entries. For non-bundled packages, you usually need both:

```stark
import Geometry
module App
```

```toml
[dependencies]
geometry = { path = "../geometry" }
```

`System.*` and official `Vendor.*` imports are the exception: they are routed
through the active SDK index and do not get `[dependencies]` entries.

## Output Layout

Build outputs and caches live under the project or solution work area:

```text
build/<profile>/<target-triple>/stage0/bin/<project>
build/<profile>/<target-triple>/stage0/obj/<project>
build/<profile>/<target-triple>/stage0/pkg/<project>
build/<profile>/<target-triple>/stage0/tests/<project>
build/<profile>/<target-triple>/stage0/stdlib
build/<profile>/<target-triple>/stage0/vendor
.stark/cache
.stark/packages
```

Project builds resolve installed `System.*` and official `Vendor.*` modules from
the exact active `sdk.json` package index. Stage and development source/package
roots are explicit SDK-manifest inputs; project ancestors and nearby
`stdlib`/`vendor` directories are not searched. Project builds ignore
`STARK_PATH`; declare ordinary dependencies in manifests or use direct compiler
`-I` for low-level development invocations. Failed reserved imports report the
selected SDK/stage context plus the active profile, target, and stage.

When a normal source/package search root is indexed, nested `.stark` build
artifact manifests are ignored so stale package images under a project's build
directory cannot shadow an explicit source root later in the resolver list. If
the search root itself is inside `.stark`, package manifests there are considered
explicit inputs and remain resolvable.

Keep generated outputs out of source examples unless the task specifically
asks to inspect them.

## Manifest Review Checklist

- `kind` matches the section present: `[executable]`, `[library]`, or `[test]`.
- `root` points at the intended root `.stark` file.
- `output` is the artifact name, not a source module name.
- Dependencies name packages, not imported modules.
- Native metadata lives in the package that owns the FFI wrapper.
- Machine-local native paths are not committed into shared manifests.
- Manifests do not configure optimization: every build compiles fully
  optimized. `dev` and `release` are built-in profiles selected with
  `--dev`/`--release`; they choose the output layout under
  `build/<profile>/...` and the recorded package profile, never a codegen
  level.
