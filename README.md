# Stark

Stark is a performance-focused programming language targeting LLVM. It is built
around a simple rule: ordinary safe code should make ownership, aliasing,
allocation, and backend facts explicit enough for the compiler to produce
predictable native code.

The syntax is C#-adjacent, but the semantics are systems-oriented:
explicit storage classes, deterministic ownership, no hidden exceptions, no
hidden allocation, safe borrows, package-image-backed libraries, and a standard
library designed around visible costs.

This repository contains the compiler, the `System` standard library, a broad
compiler and standard-library test suite, executable examples, C/Rust benchmark
counterparts, and the public documentation site.

The active implementation roadmap lives in
[docs/Internals/Roadmap.md](./docs/Internals/Roadmap.md).

## Who This README Is For

- Language users: start with [Getting Started With Stark](#getting-started-with-stark).
- Compiler contributors: see [Compiler Development](#compiler-development).
- Standard-library contributors: see [Standard Library](#standard-library).
- Benchmark contributors: see [Benchmarks](#benchmarks).
- Website and docs contributors: see [Website And Docs](#website-and-docs).
- Grammar contributors: see [Parser Generation](#parser-generation).

## Requirements

Different parts of the repository need different tools.

| Area | Required tools |
| --- | --- |
| Build and test the compiler | .NET SDK 10.0.x |
| Emit LLVM, object files, executables, or libraries | .NET SDK 10.0.x, `clang`, and a native linker toolchain |
| Emit static Stark libraries | `clang` plus `llvm-ar`/`ar` on Unix-like systems, or `llvm-lib`/`lib` on Windows |
| Run Stark/C/Rust benchmarks | .NET SDK 10.0.x, `clang`, `rustc`, and a Unix-like shell |
| Build the website | pinned Hugo v0.160.1 at `tools/hugo/hugo` |
| Deploy the website | site build requirements, `rsync`, `ssh`, and Caddy on the server |
| Regenerate parser files | Java plus the `antlr4` command |
| Native-backed examples | whatever the example declares, commonly `pkg-config`, OpenSSL, or Raylib |

The compiler shells out to `clang` for host target detection and native output.
If `clang` is not on `PATH`, `--check`, `--emit-mir`, `--emit-ssa`, and
`--emit-llvm` are still useful, but native object/executable/library workflows
will fail.

## Getting Started With Stark

Build the compiler first:

```bash
dotnet build Stark.slnx
```

That also writes a convenience launcher at `./stark` on Unix-like systems or
`stark.cmd` on Windows. You can always use `dotnet run --project src -- ...`
instead.

Create `hello.stark`:

```stark
import System.Console
module Hello

fn i32[min max] main() {
    WriteLine("Hello, World!");
    return 0;
}
```

Build the standard library package and compile the program:

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- hello.stark --emit-exe -I stdlib/dist -o hello
./hello
```

The `-I stdlib/dist` argument tells the compiler where to find the packaged
`System` standard library. During compiler or standard-library development, it
is also common to import from source:

```bash
dotnet run --project src -- hello.stark --check -I stdlib/src
```

Useful first examples:

- [examples/hello.stark](./examples/hello.stark): minimal `System.Console`
  executable.
- [examples/ffi/Ffi.stark](./examples/ffi/Ffi.stark): second step showing how
  Stark calls a C ABI function through an explicit FFI declaration.
- [examples/basic-syntax/BasicSyntax.stark](./examples/basic-syntax/BasicSyntax.stark):
  syntax tour.
- [examples/borrowing/Borrowing.stark](./examples/borrowing/Borrowing.stark):
  borrow and ownership tour.
- [samples/hello-world/README.md](./samples/hello-world/README.md): end-to-end
  sample project using the packaged standard library.
- [examples/README.md](./examples/README.md): the full example catalog.

Core language docs:

- [docs/Userfacing/LanguageReference.md](./docs/Userfacing/LanguageReference.md)
- [docs/Userfacing/BorrowerSystem.md](./docs/Userfacing/BorrowerSystem.md)
- [docs/Userfacing/ModulesAndVisibility.md](./docs/Userfacing/ModulesAndVisibility.md)
- [docs/Userfacing/ProjectsAndSolutions.md](./docs/Userfacing/ProjectsAndSolutions.md)
- [docs/Userfacing/general-idea.md](./docs/Userfacing/general-idea.md)

## Compiler CLI

The compiler entry point is the .NET project under [src/](./src).

Common direct-file workflows:

```bash
dotnet run --project src -- app.stark --check
dotnet run --project src -- app.stark --emit-mir
dotnet run --project src -- app.stark --emit-ssa
dotnet run --project src -- app.stark --emit-llvm -o app.ll
dotnet run --project src -- app.stark --emit-obj -o app.o
dotnet run --project src -- app.stark --emit-exe -o app
dotnet run --project src -- library.stark --emit-lib -o libExample.a
dotnet run --project src -- libExample.starkpkg.json --inspect-pkg
```

With no input path, the compiler reads Stark source from stdin:

```bash
printf 'module Demo\nfn i32[min max] main() { return 1; }\n' \
  | dotnet run --project src -- --emit-llvm
```

Search paths:

- `-I <dir>` or `--search-dir <dir>` adds Stark source/package search roots.
- `STARK_PATH` adds search roots split with the platform path separator.
- `-L <dir>` or `--library-dir <dir>` adds native library search roots.

Native and target options:

- `--target <triple>` overrides the LLVM target triple.
- `--target-cpu <cpu>` and `--target-feature <feature>` forward target CPU
  facts to native codegen.
- `--linker <tool>` and `--archiver <tool>` override native tools.
- `--native-source`, `--native-library`, and `--native-pkg-config` attach
  native dependencies to a package or executable build.
- `--save-temps <dir>` preserves generated LLVM/object intermediates.
- `-O0`, `-Og`, `-O1`, `-O2`, and `-O3` select optimization level.

Run `dotnet run --project src -- --help` for the full option list.

## Project Manifests

Stark also has project and solution commands:

```bash
dotnet run --project src -- build
dotnet run --project src -- run
dotnet run --project src -- test
```

These commands discover `Stark.toml` or `Stark.solution.toml` by walking up from
the current directory. The examples directory has a solution manifest, so this
works from `examples/`:

```bash
cd examples
dotnet run --project ../src -- build hello
dotnet run --project ../src -- run hello
```

Manifest behavior is documented in
[docs/Userfacing/ProjectsAndSolutions.md](./docs/Userfacing/ProjectsAndSolutions.md).

## Compiler Development

Build the whole solution:

```bash
dotnet build Stark.slnx
```

Run the full test suite:

```bash
dotnet test Stark.slnx
```

Focused runs are usually faster while iterating:

```bash
dotnet test tests/compiler.Tests/compiler.Tests.csproj
dotnet test tests/compiler.PipelineTests/compiler.PipelineTests.csproj
dotnet test tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj --filter SystemNetTcp
```

The standard-library test project can take noticeably longer than the narrower
compiler test projects because many tests build the repository standard library
and emit LLVM/native artifacts.

Pipeline and internals docs:

- [docs/Internals/CompilerPipeline.md](./docs/Internals/CompilerPipeline.md)
- [docs/Internals/LanguageInternals.md](./docs/Internals/LanguageInternals.md)
- [docs/Internals/PackageImage.md](./docs/Internals/PackageImage.md)
- [docs/Internals/OptimizationPasses.md](./docs/Internals/OptimizationPasses.md)
- [docs/Internals/StyleGuide.md](./docs/Internals/StyleGuide.md)

## Standard Library

The standard library source lives under [stdlib/src/](./stdlib/src).

Build the package-image-backed `System` library:

```bash
./scripts/build-stdlib.sh
```

By default this emits to `stdlib/dist/`:

- Unix-like systems: `libSystem.a`
- Windows: `System.lib`
- sidecar package image: `System.starkpkg.json`

You can choose another output directory:

```bash
./scripts/build-stdlib.sh samples/hello-world/packages
```

Applications consume the package with `-I <package-dir>`.

Standard-library docs:

- [docs/StandardLibrary/StandardLibrary.md](./docs/StandardLibrary/StandardLibrary.md)
- [docs/StandardLibrary/StandardLibraryBaseline.md](./docs/StandardLibrary/StandardLibraryBaseline.md)
- [docs/StandardLibrary/System.md](./docs/StandardLibrary/System.md)
- [docs/StandardLibrary/System.Console.md](./docs/StandardLibrary/System.Console.md)
- [docs/StandardLibrary/System.Text.md](./docs/StandardLibrary/System.Text.md)
- [docs/StandardLibrary/System.IO.md](./docs/StandardLibrary/System.IO.md)
- [docs/StandardLibrary/System.Collections.md](./docs/StandardLibrary/System.Collections.md)
- [docs/StandardLibrary/System.Net.Tcp.md](./docs/StandardLibrary/System.Net.Tcp.md)

## Benchmarks

Benchmark sources live under [benchmarks/](./benchmarks). Executable benchmark
scenarios are expected to have same-stem Stark, C, and Rust implementations
when all languages are enabled:

```text
benchmarks/micro/Calls.stark
benchmarks/micro/Calls.c
benchmarks/micro/Calls.rs
```

Run all benchmark languages:

```bash
scripts/run-benchmarks.sh
```

Run only one scenario:

```bash
STARK_BENCH_FILTER=network/TcpLoopbackThroughput scripts/run-benchmarks.sh
```

Run only Stark benchmarks when C or Rust is not installed:

```bash
STARK_BENCH_LANGUAGES=stark scripts/run-benchmarks.sh
```

The benchmark runner compiles each selected program, performs one warmup run,
then writes CSV rows with microsecond timings:

```text
benchmark,language,runs,compile_us,min_us,avg_us,max_us
```

Result files and machine metadata are written under `benchmarks/results/` by
default. Important environment variables:

- `STARK_BENCH_RUNS`: measured runs after warmup, default `3`.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark paths.
- `STARK_BENCH_LANGUAGES`: comma-separated list, default `stark,c,rust`.
- `STARK_BENCH_C_COMPILER`: C compiler, default `clang`.
- `STARK_BENCH_RUST_COMPILER`: Rust compiler, default `rustc`.
- `STARK_TARGET`: optional LLVM target triple for Stark.
- `STARK_COMPILER_ARGS`: extra Stark compiler arguments.

Read [benchmarks/README.md](./benchmarks/README.md) and
[benchmarks/Fairness.md](./benchmarks/Fairness.md) before adding or publishing
benchmark results. The benchmark rules intentionally prefer natural C and Rust
counterparts over line-by-line translations of Stark internals.

## Website And Docs

The website source lives under [site/](./site). It uses Hugo with the Geekdoc
theme. Theme overrides live in [site/layouts/](./site/layouts) and
`site/themes/hugo-geekdoc/static/custom.css`.

The build script requires a pinned Hugo binary:

```text
tools/hugo/VERSION     # currently 0.160.1
tools/hugo/hugo        # executable Hugo binary, not checked in
```

Build the static site:

```bash
./scripts/build-site.sh
```

That script:

1. exports docs, examples, and benchmark sources into `site/static/reference/`
   and generated reference pages under `site/content/reference/`;
2. exports the book into `site/static/book/stark-book.md`;
3. runs Hugo into `site/public/`.

Check generated site links and escaped-code regressions:

```bash
./scripts/check-site-links.sh
```

Run a local Hugo server:

```bash
./scripts/export-reference-sources.sh
./scripts/export-book.sh
./tools/hugo/hugo --source site server \
  --bind 127.0.0.1 \
  --baseURL http://127.0.0.1:1313/
```

Deployment is static-file based. [deploy/Caddyfile](./deploy/Caddyfile) is the
server-side Caddy config. [scripts/deploy-site.sh](./scripts/deploy-site.sh)
builds the site and pushes `site/public/` over `rsync`/`ssh`.

Deployment environment variables:

- `STARK_SITE_HOST`: remote host for `rsync`.
- `STARK_SITE_USER`: remote SSH user.
- `STARK_SITE_REMOTE_DIR`: remote public directory.
- `STARK_SITE_SSH_PORT`: optional SSH port, default `22`.

Caddy environment variables used by `deploy/Caddyfile`:

- `STARK_SITE_DOMAIN`
- `STARK_SITE_ACME_EMAIL`
- `STARK_SITE_ROOT`, default `/var/www/stark/public`
- `STARK_SITE_ACCESS_LOG`, default `/var/log/caddy/stark-access.log`

Website internals are tracked in
[docs/Internals/Website.md](./docs/Internals/Website.md).

## Parser Generation

The grammar is [Stark.g4](./Stark.g4). Generated C# parser files live under
`src/Parsing/`.

Regenerate after grammar edits:

```bash
./scripts/regenerate-parser.sh
```

This requires Java and the `antlr4` command on `PATH`. CI verifies that
generated parser files are up to date.

## Script Reference

| Script | Purpose |
| --- | --- |
| `scripts/build-stdlib.sh [output-dir]` | Build the `System` standard-library package and package image. |
| `scripts/run-benchmarks.sh` | Compile and run Stark/C/Rust benchmark scenarios, writing CSV results and machine metadata. |
| `scripts/build-site.sh` | Generate reference/book content and build the Hugo site into `site/public/`. |
| `scripts/check-site-links.sh` | Validate generated site links and catch escaped embedded-code regressions. |
| `scripts/deploy-site.sh` | Build the site and deploy `site/public/` with `rsync` over SSH. |
| `scripts/export-reference-sources.sh` | Copy docs/examples/benchmarks into website reference content. |
| `scripts/export-book.sh` | Export the book pages into a single Markdown file for the website. |
| `scripts/check-book-samples.sh` | Compile accepted book samples and verify rejected samples fail. |
| `scripts/regenerate-parser.sh` | Regenerate C# parser files from `Stark.g4`. |

## Repository Map

- [src/](./src): compiler implementation.
- [tests/](./tests): compiler, pipeline, integration, feature, and standard-library tests.
- [stdlib/](./stdlib): Stark standard-library source and package manifest.
- [examples/](./examples): focused Stark example programs and project manifests.
- [samples/](./samples): end-to-end sample project layouts.
- [benchmarks/](./benchmarks): Stark/C/Rust benchmark scenarios and fairness rules.
- [docs/](./docs): user-facing, standard-library, and internals documentation.
- [site/](./site): Hugo website source.
- [deploy/](./deploy): Caddy deployment config.
- [scripts/](./scripts): build, benchmark, docs, site, and parser helpers.

## What Works Today

Stark currently has:

- a pass-based .NET compiler frontend;
- ANTLR grammar and generated parser;
- module resolution, type checking, semantic validation, and ownership/lifetime
  validation;
- MIR, SSA, and LLVM IR lowering;
- native object, executable, static library, and package image emission;
- package-image-backed imports;
- a source and package-backed `System` standard library with console, text,
  memory, collections, file/path, process, threading, math, and TCP APIs;
- examples and sample projects that compile and run through integration tests;
- a benchmark harness with Stark, C, and Rust rows for performance work;
- a Hugo documentation site backed by repository docs and checked examples.

The binary release pipeline is still being finalized. For now, treat the docs,
tests, and checked examples as the source of truth for the implemented surface.
