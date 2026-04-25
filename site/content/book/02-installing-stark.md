+++
title = "2. Installing Stark and Building Programs"
weight = 20
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/01-why-stark/"
next = "/book/03-hello-stark/"
+++

# Installing Stark and Building Programs

Stark is currently built from this repository. The compiler, standard library,
examples, website, and tests live together while the language is still moving.

## Build The Compiler

From the repository root:

```bash
dotnet build Stark.slnx
```

During development, examples are normally compiled through `dotnet run`:

```bash
dotnet run --project src -- examples/basic-syntax/BasicSyntax.stark --check
```

## Direct Compiler Workflow

The low-level compiler command accepts a Stark source file and an output mode.
Common modes are:

- `--check`
- `--emit-exe`
- `--emit-lib`
- `--emit-llvm`

Example:

```bash
dotnet run --project src -- examples/arithmetic/Arithmetic.stark --emit-exe -o /tmp/stark-arithmetic
```

## Project Workflow

The project driver uses `Stark.toml` and `Stark.solution.toml`.

The smallest executable project manifest looks like the checked-in hello
example:

{{< file-sample "static/reference/examples/hello/Stark.toml" "toml" >}}

From the `examples` directory:

```bash
dotnet run --project ../src -- build
dotnet run --project ../src -- run
```

The implemented project workflow supports `build` and `run`. `stark test` is
reserved for future test projects and the planned `System.Testing` module.

A solution manifest collects several projects and names defaults:

{{< file-sample "static/reference/examples/Stark.solution.toml" "toml" >}}

## Why Manifests Matter

Manifests are not just convenience. They let Stark keep package boundaries,
native dependencies, and build outputs explicit. That matters for optimization
and reproducibility: a package should own its native shims and link metadata
instead of forcing downstream users to remember a long compiler command.
