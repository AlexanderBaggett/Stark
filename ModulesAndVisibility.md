# Stark Modules and Visibility

## General Strategy

Stark treats module structure and visibility as optimization-relevant parts of the language.

The governing rules are:

- expose as little as possible
- export as little as possible
- keep most code and data inside the current compilation unit or package
- preserve a closed-world view whenever possible

Restrictive visibility is valuable because it enables:

- dead code elimination
- global constant propagation
- global scalar replacement
- more aggressive inlining
- more aggressive internalization
- direct calls instead of PLT indirection
- direct global access instead of GOT indirection
- stronger LTO and ThinLTO results

For this reason, Stark defaults to privacy and requires explicit widening of visibility.

## Core Model

Stark has two user-facing structural layers:

- `package` or `library`
  - the build artifact and import boundary
- `module`
  - the source-file-level namespace and compilation unit

Modules are the namespace system.

Stark does not use reopenable namespaces as a separate language feature.

## Module Declaration Syntax

Each source file declares exactly one module.

The syntax is:

```stark
module SomeModule
```

There is exactly one `module` declaration per source file.

The `module` declaration appears after all imports.

Example:

```stark
import Math
import Math.SIMD

module Vectors
```

The import section therefore precedes the module declaration.

## Import Syntax

Imports are explicit and module-based.

The syntax is:

```stark
import SomeModule
```

Additional rules:

- imports appear before the `module` declaration
- imports do not implicitly re-export anything
- importing a module does not automatically expose all nested modules
- wildcard imports should be avoided or forbidden

Imports are compile-time name-resolution constructs. They are not visibility modifiers and do not by themselves imply linker export.

## File and Module Mapping

Stark uses one source file per module.

The intended model is:

- one source file equals one module
- directory layout determines module nesting
- the module declaration names the file's module
- there are no reopenable namespace blocks spanning multiple files

Example layout:

```text
math/
  vectors.stark
  matrix.stark
  simd/
    dot.stark
```

This supports a straightforward module tree and matches ThinLTO-friendly compilation granularity.

## Visibility Keywords

Stark uses the following visibility keywords:

- `internal`
- `public`
- `export`

If no visibility keyword is written, the declaration is module-private.

These are source-language keywords, not just descriptive terms.

## Meaning of Each Visibility Level

### Module-Private

This is the default when no visibility keyword is present.

Meaning:

- visible only inside the current module
- not visible to sibling modules
- not visible to downstream packages
- not linker-exported

This is the default for all ordinary declarations.

### `internal`

Meaning:

- visible to other modules inside the same package or library
- not visible to downstream packages
- not a public package API
- not a linker-exported ABI symbol by default

`internal` is the normal way to share helpers across a package without weakening optimization more than necessary.

### `public`

Meaning:

- visible to downstream Stark code importing the package
- part of the source-level package API
- not necessarily a linker-exported symbol

`public` is a source-visibility concept, not automatically a binary-ABI concept.

### `export`

Meaning:

- real ABI-visible symbol
- intended for FFI, runtime entry points, plugin boundaries, or stable binary interfaces
- should be used sparingly

`export` is the weakest visibility level from the optimizer's perspective.

## Why `public` and `export` Are Separate

Stark keeps source visibility separate from binary export.

This distinction is required because:

- a declaration may need to be visible to downstream Stark code
- that same declaration may not need to exist as a fully visible linker symbol
- linker-visible symbols are much more restrictive for optimization

In Stark:

- `public` controls source-level API visibility
- `export` controls binary-level ABI visibility

The language does not collapse these into one concept.

## What Visibility Keywords May Apply To

Visibility keywords apply to top-level module declarations of the following kinds:

- functions
- laws
- finites
- finite laws
- global constants
- global variables
- `struct` declarations
- `record` declarations
- `trait` declarations
- `doctrine` declarations
- type aliases
- explicit re-exports

These keywords may also apply to submodule declarations if Stark later adds explicit nested submodule declarations in source.

## What Visibility Keywords May Not Apply To

Visibility keywords do not apply to:

- local variables
- function parameters
- generic parameters
- block-scoped declarations
- statements
- expressions
- match arms
- imports

In the initial model, visibility keywords also do not apply to individual fields or individual methods inside a type body.

Visibility is defined at the top-level module declaration boundary.

## Recommended Simplicity Rule

The simplest good rule set is:

- top-level declarations may carry `internal`, `public`, or `export`
- everything else is module-private by default
- fields and local declarations do not carry visibility modifiers

This keeps the surface model small and easy to reason about.

## Lowering Intent

The source language does not expose LLVM linkage terms directly, but the frontend should lower using the most restrictive correct linkage.

Broad lowering intent:

- module-private declarations
  - prefer `private` or `internal`
- `internal`
  - prefer `internal`
- `public`
  - use the most restrictive linkage consistent with package API semantics
  - rely on package compilation model and LTO internalization where possible
- `export`
  - use true externally visible linkage

The frontend always prefers the narrowest correct visibility and linkage.

## Executables and Shared Libraries

### Executables

When building executables, Stark should aggressively assume local resolution.

The intended strategy is:

- mark non-export symbols `dso_local`
- avoid semantic interposition by default
- keep non-export roots eligible for internalization

This preserves direct calls and direct global access.

### Shared Libraries

When building shared libraries, Stark should still preserve direct internal access wherever possible.

The intended strategy is:

- use hidden visibility by default
- export only explicitly marked `export` declarations
- avoid default-visible preemptable symbols unless the ABI really requires them

This preserves direct internal calls and direct internal data access inside the shared library.

## Generics and Monomorphization

If Stark uses monomorphization, identical instantiations may appear in multiple codegen units.

The intended lowering strategy is:

- use ODR-style deduplicable linkage for generic instantiations
- prefer the equivalent of `linkonce_odr`, not plain `linkonce`
- group associated generated data in the same comdat when required

This is required to preserve safe inlining and correct deduplication behavior.

## Constants and Global Data

Stark aggressively distinguishes immutable global data from mutable global state.

The following are immutable unless explicitly declared otherwise:

- string literals
- numeric lookup tables
- sealed dispatch tables when possible
- type metadata when possible
- variant tables when possible

Immutable global data should be emitted as constants.

When address identity is not semantically meaningful, the frontend should also use address-insignificance-friendly lowering such as local unnamed-address style handling.

This enables:

- constant folding of loads
- constant merging
- reduced duplicate data
- better cache behavior
- stronger interprocedural propagation

## Closed-World Bias

The module and visibility system reinforces Stark's broader closed-world design.

The default assumptions are:

- most declarations are not externally visible
- most helpers are module-private or `internal`
- most metadata remains local to the package or final binary
- dynamic linking and open-world replacement are explicit concessions, not defaults

This supports:

- internal fast calling conventions
- static dispatch
- sealed laws and traits
- stronger whole-program optimization

## LTO and Compilation Strategy

Stark is designed to work well with ThinLTO.

The intended compilation strategy is:

- one LLVM module per source file
- ThinLTO enabled by default in optimized builds when practical
- cross-module importing for small hot functions
- LTO internalization for final binaries

This enables:

- cross-module inlining
- better dead stripping
- stronger whole-program constant propagation
- recovery of internal-style optimization on declarations that were initially more visible than strictly necessary

Stark does not rely on LTO alone. Restrictive visibility is still emitted up front.

## What Stark Avoids

Stark avoids the following unless there is a strong justification:

- reopenable namespace systems
- default-public top-level declarations
- automatic linker export of source-visible API
- open-ended symbol interposition semantics
- plain non-ODR weak or linkonce linkage for generics
- widespread use of forced-retention mechanisms
- low-level linker terminology in ordinary source syntax

## Summary

The Stark module and visibility model is defined by the following rules:

- modules are namespaces
- one source file equals one module
- imports appear before the module declaration
- `module SomeModule` declares the module in the file
- top-level declarations are module-private by default
- `internal`, `public`, and `export` are the visibility keywords
- `public` and `export` are not the same concept
- visibility modifiers apply to top-level declarations, not to locals or statements
- fields and local declarations do not carry visibility modifiers in the initial model
- executables and shared libraries both default to the most restrictive practical visibility

This model is intentionally simple at the source level and intentionally restrictive at the optimization level.
