# Stark Modules and Visibility

## General Strategy

Stark should treat module structure and visibility as an optimization feature, not just a code-organization feature.

The central rule is:

- expose as little as possible
- export as little as possible
- keep most code and data inside the current compilation unit or package
- let the compiler and linker see the strongest possible closed-world structure

LLVM gets much stronger when functions and globals are not externally visible. Restrictive linkage enables:

- dead code elimination
- global constant propagation
- global scalar replacement
- calling convention changes
- better inlining
- direct calls instead of PLT indirection
- direct global access instead of GOT indirection
- more aggressive whole-program optimization under LTO

Stark should therefore default to privacy and require explicit opt-in for broader visibility.

## Core Model

Stark should have three structural layers:

- `package` or `library`
  - the build artifact and import boundary
- `module`
  - the source-file-level namespace and compilation unit
- declarations inside a module
  - functions, globals, types, laws, traits, constants, and related declarations

The important note is that only `package` and `module` are user-facing organizational concepts. The compiler may reason about individual declarations internally, but that is not a separate source-language construct.

## Modules As Namespaces

Stark should use modules as its namespace system.

That means:

- one source file corresponds to one module
- directories determine nested module paths
- imports reference module paths
- selected names may be re-exported explicitly

This avoids the complexity of a separate reopenable namespace system and lines up well with LLVM and ThinLTO.

### Why this is a good fit

- It keeps name lookup simple.
- It keeps source organization predictable.
- It preserves good incremental compilation boundaries.
- It fits ThinLTO's preferred granularity well: neither one huge module nor one module per function.

## File and Module Mapping

Recommended rules:

- one `.stark` source file = one Stark module
- module path is derived from package root plus directory structure
- the file name becomes the leaf module name unless explicitly overridden
- there should be no reopenable namespace construct spanning multiple files

Example:

```text
math/
  vector.stark
  matrix.stark
  simd/
    dot.stark
```

Possible module paths:

- `math.vector`
- `math.matrix`
- `math.simd.dot`

This model is simple and naturally supports one LLVM module per source file.

## Imports and Re-Exports

Imports should be explicit and module-based.

Recommended rules:

- importing a module does not automatically expose all nested modules
- wildcard imports should either be forbidden or strongly discouraged
- re-exports should be explicit
- re-exporting should not imply linker export

This distinction matters:

- source-level visibility controls whether Stark code may name something
- linker-level export controls whether foreign code or other link units may reference a symbol

Those are not the same thing and should not be collapsed into one concept.

## Visibility Levels

Stark should keep visibility very small and very clear.

Recommended visibility model:

- default module-private visibility
- `internal`
- `public`
- `export`

### 1. Module-Private By Default

This is the default when no modifier is written.

Meaning:

- visible only inside the current module
- not visible to sibling modules
- not part of package API
- not linker-exported

This should be the most common visibility level.

### 2. `internal`

Meaning:

- visible to other modules inside the same package or library
- not visible to downstream packages
- not part of the runtime ABI by default

This is the normal way to share helpers across a package without weakening optimization too much.

### 3. `public`

Meaning:

- visible to other Stark packages importing this package
- part of the source-level API
- does not necessarily imply a linker-exported symbol

This is important because many language-level APIs do not need to be externally preemptable or visible to foreign code.

### 4. `export`

Meaning:

- real ABI-visible symbol
- intended for FFI, runtime entry points, plugin boundaries, or stable binary interfaces
- should be used sparingly

This is the strongest visibility and the weakest from the optimizer's perspective.

## Why `public` and `export` Should Be Separate

Many languages accidentally conflate source visibility and binary export. Stark should avoid that.

If a function is merely part of the Stark package API:

- downstream Stark code may need to call it
- the compiler and linker may still know a great deal about how it is used
- under LTO or whole-program builds, it may still be internalized in final artifacts

If a function is truly `export`:

- it must survive as a visible linker symbol
- foreign code may reference it
- ABI and preemption concerns become much more important
- LLVM loses optimization opportunities

Keeping these apart gives Stark a much cleaner optimization model.

## Suggested Lowering Intent

The language spec does not need to expose LLVM linkage names directly, but the frontend should broadly aim for the following:

- module-private
  - prefer `private` or `internal`
- `internal`
  - prefer `internal`
- `public`
  - use the most restrictive linkage consistent with cross-module/package semantics
  - rely on package compilation model and LTO internalization where possible
- `export`
  - use true externally visible linkage

The frontend should always prefer the most restrictive correct linkage.

## Shared Libraries vs Executables

Stark should bias toward direct access whenever possible.

### When building executables

Recommended strategy:

- mark all non-FFI symbols `dso_local`
- avoid semantic interposition by default
- keep all non-export roots optimizable and internalizable

This avoids unnecessary PLT and GOT overhead.

### When building shared libraries

Recommended strategy:

- use hidden visibility by default
- export only explicitly marked `export` symbols
- avoid default-visible, preemptable symbols unless the language or ABI actually requires them

This preserves direct internal calls and direct internal global access inside the shared library.

## Generics, Monomorphization, and ODR-Style Deduplication

If Stark uses monomorphization for generics, multiple codegen units may emit identical instantiations.

Recommended strategy:

- use ODR-style deduplicable linkage for generic instantiations
- prefer the equivalent of `linkonce_odr`, not plain `linkonce`
- group associated generated data with the function in the same comdat when needed

This matters because:

- ODR-style linkage still permits inlining
- plain non-ODR weak/linkonce semantics block important optimization
- comdat grouping keeps related generated artifacts consistent during deduplication

## Constants and Global Data

Stark should aggressively distinguish immutable data from mutable global state.

Recommended rules:

- string literals are immutable
- lookup tables are immutable
- type metadata is immutable when possible
- sealed dispatch tables and variant tables are immutable when possible
- immutable globals should be emitted as constants

When address identity is not semantically meaningful, the compiler should also use address-insignificance-friendly lowering such as local unnamed-address style handling.

This enables:

- constant folding of loads
- constant merging
- less duplicate data
- better cache use
- stronger interprocedural propagation

## Closed-World Bias

The module and visibility system should reinforce Stark's broader closed-world philosophy.

That means:

- most declarations are not externally visible
- most helper functions are module-private or `internal`
- most type metadata stays local to the package or final binary
- dynamic linking and open-world replacement are explicit concessions, not defaults

This pairs naturally with Stark's goals around:

- internal fast calling conventions
- static dispatch
- sealed laws and traits
- stronger whole-program optimization

## LTO and Compilation Strategy

Stark should be designed with ThinLTO in mind.

Recommended strategy:

- one LLVM module per source file
- enable ThinLTO by default in optimized builds when practical
- rely on ThinLTO to import small hot functions cross-module
- rely on LTO internalization to shrink visibility further in final binaries

This gives Stark:

- cross-module inlining
- better dead stripping
- stronger whole-program constant propagation
- more chances to recover internal-style optimization on symbols that started more visible than necessary

But Stark should not rely on LTO alone. The frontend should still emit restrictive visibility up front.

## What To Avoid

Stark should avoid the following unless there is a strong justification:

- reopenable namespace systems
- default-public top-level declarations
- automatic runtime export of source-visible API
- open-ended symbol interposition semantics
- plain non-ODR weak/linkonce linkage for generics
- widespread use of "must keep" retention mechanisms
- exposing low-level linker concepts directly in ordinary source syntax

These either complicate the language or reduce optimization power without enough benefit.

## Recommended Stark Rules

If Stark wants a compact, optimization-friendly design, the simplest good rule set is:

- modules are namespaces
- one source file equals one module
- directory layout defines module path
- declarations are module-private by default
- `internal` exposes within the package
- `public` exposes to Stark source importers
- `export` exposes a real linker symbol
- shared libraries use hidden visibility by default
- executables use `dso_local`-style assumptions aggressively
- generics use ODR-style deduplicable linkage
- immutable globals are constants
- address identity is not preserved unless the language actually exposes it

## Summary

Modules and visibility are one of the highest-leverage parts of language design for optimization.

The best Stark design is one that:

- treats modules as namespaces
- defaults to privacy
- separates source visibility from binary export
- favors internalization
- favors hidden visibility
- favors immutable constants
- favors ThinLTO-friendly module granularity

That gives LLVM the strongest possible view of the program while still giving users a clear and simple source model.
