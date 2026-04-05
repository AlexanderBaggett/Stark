# Examples

This directory contains small Stark programs that match the compiler and standard-library surface currently implemented in the repository.

## `hello.stark`

Minimal executable that prints with `System.Console.WriteLine`.

Build the standard library package first, then compile and run it:

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/hello.stark --emit-exe -I stdlib/dist -o examples/hello
./examples/hello
```

## `arithmetic/Arithmetic.stark`

Small arithmetic check that exercises local variables, integer operators, and a simple `if` guard.

Build and run it directly:

```bash
dotnet run --project src -- examples/arithmetic/Arithmetic.stark --emit-exe -o examples/arithmetic/arithmetic
./examples/arithmetic/arithmetic
```

## `control-flow/ControlFlow.stark`

Compact control-flow sample that combines a `while` loop with a `switch` statement.

Build and run it directly:

```bash
dotnet run --project src -- examples/control-flow/ControlFlow.stark --emit-exe -o examples/control-flow/control-flow
./examples/control-flow/control-flow
```

## `multi-module/`

Two-source-file example that shows cross-module imports and a public helper function.

The root file is `examples/multi-module/App.stark`, which imports `Math.stark` from the same directory. Compile it directly:

```bash
dotnet run --project src -- examples/multi-module/App.stark --emit-exe -o examples/multi-module/app
./examples/multi-module/app
```

## `data-model/DataModel.stark`

Shows `struct` and `record` declarations plus object-initializer syntax with `new Type() { ... }`.

You can validate or build it directly:

```bash
dotnet run --project src -- examples/data-model/DataModel.stark --check
dotnet run --project src -- examples/data-model/DataModel.stark --emit-exe -o examples/data-model/data-model
./examples/data-model/data-model
```

## `static-library/`

Small library-style example that exercises cross-module code and the `--emit-lib` package flow.

The root file is `examples/static-library/Facade.stark`, which imports `Math.stark` from the same directory. Build it as a library package like this:

```bash
dotnet run --project src -- examples/static-library/Facade.stark --emit-lib -o examples/static-library/libFacade.a
```

That command produces the archive and a sidecar `libFacade.starkpkg.json` manifest in the same directory.
