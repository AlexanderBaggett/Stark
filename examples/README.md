# Examples

This directory contains small Stark programs that match the compiler and standard-library surface currently implemented in the repository.

## Project Manifests

The examples directory includes `Stark.toml` project manifests and a root
`Stark.solution.toml` for the current project driver.

From this directory, the default solution build covers the examples that do not
need a local native graphics library or a standard-library package rebuild:

```bash
dotnet run --project ../src -- build
dotnet run --project ../src -- run
```

Specific examples can be built by alias:

```bash
dotnet run --project ../src -- build hello
dotnet run --project ../src -- build build-your-own-git
dotnet run --project ../src -- build raylib
dotnet run --project ../src -- build breakout
dotnet run --project ../src -- build http-get
```

The `standard-library` manifest builds through the project driver and is the
recommended smoke test for examples that use the packaged standard library.

`raylib` and `breakout` require Raylib to be available through `pkg-config` or
through a local native path configured in `Stark.user.toml` or
`~/.config/stark/config.toml`.

## `hello.stark`

Minimal executable that imports `System.Console` and calls `WriteLine`.

Build the standard library package first, then compile and run it:

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/hello.stark --emit-exe -I stdlib/dist -o examples/hello
./examples/hello
```

## `basic-syntax/BasicSyntax.stark`

Small syntax tour that uses a module, constant, function declarations, stack locals, mutability, `if`, `while`, and `switch`.

Build and run it directly:

```bash
dotnet run --project src -- examples/basic-syntax/BasicSyntax.stark --emit-exe -o examples/basic-syntax/basic-syntax
./examples/basic-syntax/basic-syntax
```

## `type-system/TypeSystem.stark`

Focused type-system sample that combines a range alias, constrained struct and record fields, enum variants with payloads, and enum equality.

Build and run it directly:

```bash
dotnet run --project src -- examples/type-system/TypeSystem.stark --emit-exe -o examples/type-system/type-system
./examples/type-system/type-system
```

## `modules/`

Three-file module-boundary sample that imports public APIs from sibling modules while keeping helper functions internal to their defining module.

The root file is `examples/modules/App.stark`, which imports `Geometry.stark` and `Units.stark` from the same directory:

```bash
dotnet run --project src -- examples/modules/App.stark --emit-exe -o examples/modules/app
./examples/modules/app
```

## `borrowing/Borrowing.stark`

Small borrowing sample that uses immutable receiver borrows, mutable receiver borrows, borrow forwarding, and a returned mutable borrow view.

Build and run it directly:

```bash
dotnet run --project src -- examples/borrowing/Borrowing.stark --emit-exe -o examples/borrowing/borrowing
./examples/borrowing/borrowing
```

Additional focused borrowing examples:

- `borrowing/OwnershipMoves.stark`
  Shows that owned aggregate values move into by-value calls, while scalar
  values copy, and that a moved local can be used again after reinitialization.
- `borrowing/BorrowKinds.stark`
  Shows `borrow`, `mut borrow`, and `retborrow` on a small `Counter` type.
- `borrowing/OutParameters.stark`
  Shows a fallible operation writing through an `out` destination without using
  exceptions or hidden allocation.

Build them directly:

```bash
dotnet run --project src -- examples/borrowing/OwnershipMoves.stark --emit-exe -o examples/borrowing/ownership-moves
dotnet run --project src -- examples/borrowing/BorrowKinds.stark --emit-exe -o examples/borrowing/borrow-kinds
dotnet run --project src -- examples/borrowing/OutParameters.stark --emit-exe -o examples/borrowing/out-parameters
```

## `ffi/Ffi.stark`

Second-step interop sample that imports the C ABI `abs` function and calls it
from Stark code. This is intentionally separate from hello world so the first
language example stays focused on ordinary Stark plus the standard library.

Build and run it directly:

```bash
dotnet run --project src -- examples/ffi/Ffi.stark --emit-exe -o examples/ffi/ffi
./examples/ffi/ffi
```

## `standard-library/StandardLibrary.stark`

Standard-library sample that combines `System.BitOperations` with `System.Console` output and status handling.

Build it through the examples solution:

```bash
cd examples
dotnet run --project ../src -- build standard-library
./.stark/build/dev/standard-library/standard-library
```

For a quick source-imported check from the repository root:

```bash
dotnet run --project src -- examples/standard-library/StandardLibrary.stark --check -I stdlib/src
```

## `http-get/HttpGet.stark`

Minimal HTTPS GET client for `https://www.google.com/`. Stark builds and sends
the HTTP request and streams the response, while `HttpsNative.c` supplies the TLS
transport through OpenSSL because the standard library currently only ships TCP.

This example requires OpenSSL development headers and libraries discoverable via
`pkg-config openssl` or the platform fallback library names.

Build and run it from the examples solution:

```bash
dotnet run --project ../src -- build http-get
./.stark/build/dev/http-get/http-get
```

## `build-your-own-git/`

First intermediate example slices for a tiny Git-like tool.

- `Init.stark` initializes a local `starkgit-demo/.starkgit` directory, creates the `objects` and `refs/heads` directories, and writes `HEAD`.
- `Commit.stark` writes a simple demo commit text object under `.starkgit/objects`.
- `Ref.stark` writes the `refs/heads/main` branch tip for the demo commit.
- `Objects.stark` lists the demo object from `.starkgit/objects`.
- `Inspect.stark` opens the initialized metadata directory and verifies `HEAD`, `objects`, and `refs` through directory iteration.
- `Status.stark` checks the expected metadata paths and reports whether the repository is initialized.

Build the standard library package first, then compile it from the repository root and run it from an empty scratch directory:

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/build-your-own-git/Init.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/init
dotnet run --project src -- examples/build-your-own-git/Commit.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/commit
dotnet run --project src -- examples/build-your-own-git/Ref.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/ref
dotnet run --project src -- examples/build-your-own-git/Objects.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/objects
dotnet run --project src -- examples/build-your-own-git/Inspect.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/inspect
dotnet run --project src -- examples/build-your-own-git/Status.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/status
mkdir -p /tmp/stark-build-your-own-git
cd /tmp/stark-build-your-own-git
/path/to/Stark/examples/build-your-own-git/init
/path/to/Stark/examples/build-your-own-git/commit
/path/to/Stark/examples/build-your-own-git/ref
/path/to/Stark/examples/build-your-own-git/objects
/path/to/Stark/examples/build-your-own-git/inspect
/path/to/Stark/examples/build-your-own-git/status
```

## `neural-network/Inference.stark`

Fixed-topology neural-network inference example. It uses integer fixed-point style values, a two-neuron hidden layer, ReLU activation, and a final score threshold without heap allocation or dynamic dispatch.

Build and run it directly:

```bash
dotnet run --project src -- examples/neural-network/Inference.stark --emit-exe -o examples/neural-network/inference
./examples/neural-network/inference
```

## `simple-database/MemoryTable.stark`

Tiny in-memory database example inspired by cstack's SQLite clone tutorial. It uses a prepared-statement shape, enum result codes, a VM-style execute method, and a fixed-capacity append-only table.

Build and run it directly:

```bash
dotnet run --project src -- examples/simple-database/MemoryTable.stark --emit-exe -o examples/simple-database/memory-table
./examples/simple-database/memory-table
```

## `bit-torrent/TrackerResponse.stark`

Small BitTorrent tracker-response parsing slice. It validates a fixed bencoded dictionary shape, decodes integer status fields, and reports parse success with no exceptions or heap-backed parser state.

Build and run it directly:

```bash
dotnet run --project src -- examples/bit-torrent/TrackerResponse.stark --emit-exe -o examples/bit-torrent/tracker-response
./examples/bit-torrent/tracker-response
```

## `bit-torrent/Handshake.stark`

BitTorrent peer-handshake construction slice. It builds the 68-byte handshake buffer with fixed storage, protocol bytes, reserved bytes, info hash, and peer id, then validates the encoded fields.

Build and run it directly:

```bash
dotnet run --project src -- examples/bit-torrent/Handshake.stark --emit-exe -o examples/bit-torrent/handshake
./examples/bit-torrent/handshake
```

## `breakout/BreakoutCore.stark`

Deterministic Breakout game-core slice shared with the Raylib-backed example. It uses fixed brick storage, explicit ball/paddle state, and enum step results for brick hits, paddle bounces, win, and loss handling without depending on a local graphics library.

Build and run it directly:

```bash
dotnet run --project src -- examples/breakout/BreakoutCore.stark --emit-exe -o examples/breakout/breakout-core
./examples/breakout/breakout-core
```

## `breakout/BreakoutRaylib.stark`

First playable Raylib Breakout shell. It keeps the scope intentionally small: a keyboard/mouse controlled paddle, a bouncing ball, fixed colored bricks, brick destruction, and score text.

Build Raylib as described in `examples/raylib/README.md`, then let the helper
script build the Stark Raylib package image and compile Breakout from it:

```bash
bash examples/breakout/run-raylib.sh
./examples/breakout/breakout-raylib
```

## `raylib/`

Raylib 5.5 binding surface for future graphical examples. The bindings are split into `Raylib.Core`, `Raylib.Shapes`, `Raylib.Textures`, `Raylib.Text`, `Raylib.Models`, `Raylib.Audio`, and `Raylib.Types`, with `RaylibNative.c` providing C ABI shims for by-value Raylib structs.

See `examples/raylib/README.md` for the local Raylib build and headless smoke-test commands.

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

Shows `struct` and `record` declarations plus field-initializer syntax with `new Type() { ... }`.

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

## `standard-library-tests/`

Small `kind = "test"` project that uses `System.Testing` facts and the project
driver's generated `[Fact]` runner.

Run it from the examples solution:

```bash
cd examples
dotnet run --project ../src -- test standard-library-tests
```
