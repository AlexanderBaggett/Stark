# Stark

Stark is an experimental programming language and compiler targeting LLVM.

The repository currently contains:

- language design documents
- an ANTLR grammar
- a pass-based .NET compiler frontend
- MIR and SSA lowering
- LLVM IR emission
- native executable emission through Clang
- static library emission with a package image
- package-image-backed Stark package imports without source files

## Status

Stark can currently:

- parse and validate a meaningful language subset
- lower core control flow and scalar expressions to LLVM IR
- compile a native `Hello World`
- emit MIR, SSA, LLVM IR, or a native executable from the CLI
- emit a static library plus a package image from the CLI
- emit object files from the CLI

The active implementation checklist lives in [docs/Internals/Roadmap.md](./docs/Internals/Roadmap.md).

Known unsupported or partial language, lowering, runtime, and standard-library areas are tracked in [docs/Userfacing/UnsupportedFeatures.md](./docs/Userfacing/UnsupportedFeatures.md).

The current `v1.0` release baseline is defined in [docs/V1ReleaseSubset.md](./docs/V1ReleaseSubset.md).

The current standard-library release baseline is defined in [docs/StandardLibrary/StandardLibraryBaseline.md](./docs/StandardLibrary/StandardLibraryBaseline.md).

Pre-release notes currently live in [docs/ReleaseNotes.md](./docs/ReleaseNotes.md).

Release tagging, changelog, and upgrade-note expectations live in [docs/ReleaseProcess.md](./docs/ReleaseProcess.md).

## Build

```bash
dotnet build Stark.slnx
```

## Test

```bash
dotnet test Stark.slnx
```

## Compiler CLI

The compiler entry point is the `compiler` project under `src/`.

Examples:

```bash
dotnet run --project src -- --check examples/hello.stark
dotnet run --project src -- --emit-mir examples/hello.stark
dotnet run --project src -- --emit-ssa examples/hello.stark
dotnet run --project src -- --emit-llvm examples/hello.stark
dotnet run --project src -- --emit-obj examples/hello.stark -o hello.o
dotnet run --project src -- --emit-lib examples/math.stark -o libmath.a
dotnet run --project src -- --emit-exe examples/hello.stark -o hello
dotnet run --project src -- --check app/app.stark -I packages
```

You can also pipe source in through stdin:

```bash
printf 'module Demo\nfn i32 Main() { return 1; }\n' | dotnet run --project src -- --emit-llvm
```

For LLVM-based output modes, Stark will attempt to detect the host target triple and data layout from `clang` automatically.

`--emit-lib` produces a static library archive and a sidecar package image named `<basename>.starkpkg.json`.

That `.starkpkg.json` file is Stark's compiler-owned package image.

When a source file is not present, Stark will also resolve imports from matching `.starkpkg.json` package images in the module search directory and link the referenced static library during `--emit-exe`.

Additional module/package search roots can be supplied with `-I <dir>` or `--search-dir <dir>`. Stark also honors `STARK_PATH`, split with the platform path separator.

## Standard Library Package

The repository now includes a first standard-library package under `stdlib/src/`.

Build it with:

```bash
./scripts/build-stdlib.sh
```

That emits a package-image-backed package into `stdlib/dist/` by default. Applications can then consume it with `-I stdlib/dist`.

## Minimal Hello World

```stark
module Hello

ffi fn i32 puts(ascii s);

export ffi fn i32 main() {
    puts("Hello, world!\n");
    return 0;
}
```

Compile it:

```bash
dotnet run --project src -- --emit-exe hello.stark -o hello
./hello
```

## Hello World With `System.Console`

```stark
import System
module Hello

export ffi fn i32 main() {
    System.Console.WriteLine("Hello, world!");
    return 0;
}
```

Build the standard library package first, then compile the app:

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- hello.stark --emit-exe -I stdlib/dist -o hello
./hello
```

## Key Docs

- [docs/Userfacing/LanguageReference.md](./docs/Userfacing/LanguageReference.md)
- [docs/Internals/LanguageInternals.md](./docs/Internals/LanguageInternals.md)
- [docs/Userfacing/general-idea.md](./docs/Userfacing/general-idea.md)
- [docs/Userfacing/BorrowerSystem.md](./docs/Userfacing/BorrowerSystem.md)
- [docs/Userfacing/ModulesAndVisibility.md](./docs/Userfacing/ModulesAndVisibility.md)
- [docs/Internals/CompilerPipeline.md](./docs/Internals/CompilerPipeline.md)
- [docs/Internals/PackageImage.md](./docs/Internals/PackageImage.md)
- [docs/StandardLibrary/StandardLibrary.md](./docs/StandardLibrary/StandardLibrary.md)
- [docs/Userfacing/UnsupportedFeatures.md](./docs/Userfacing/UnsupportedFeatures.md)
- [docs/V1ReleaseSubset.md](./docs/V1ReleaseSubset.md)
- [docs/V1LoweringContract.md](./docs/V1LoweringContract.md)
- [docs/StandardLibrary/StandardLibraryBaseline.md](./docs/StandardLibrary/StandardLibraryBaseline.md)
- [docs/ReleaseNotes.md](./docs/ReleaseNotes.md)
- [docs/ReleaseProcess.md](./docs/ReleaseProcess.md)
- [docs/Internals/Roadmap.md](./docs/Internals/Roadmap.md)
- [samples/README.md](./samples/README.md)
