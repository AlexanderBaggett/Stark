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

Explicit re-exports use the `export import` form:

```stark
export import SomeModule
```

Additional rules:

- imports appear before the `module` declaration
- imports do not implicitly re-export anything
- `export import` is the explicit re-export declaration form
- importing a module does not automatically expose all nested modules
- wildcard imports are forbidden

Plain imports are compile-time name-resolution constructs. They do not by themselves imply re-export or linker export.

Importing a module brings that module's visible top-level declarations into
ordinary unqualified lookup by their final name. After `import
System.Collections`, code may write `List<T>` instead of
`System.Collections.List<T>`. This applies to visible `struct`, `record`,
`enum`, `trait`, `doctrine`, type alias, global, and function declarations.

If more than one imported module exposes the same final type or alias name, the
reference is ambiguous and Stark requires the fully qualified name. Local
declarations and declarations in the current module still take priority over
imported final-name lookup.

`export import` makes the imported module part of the package-facing Stark surface.

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

If no visibility keyword is written on a top-level declaration, the declaration
is module-private.

Member functions inside a `struct` or `record` inherit the visibility of their
enclosing type unless they declare an explicit member visibility.

These are source-language keywords, not just descriptive terms.

## Meaning of Each Visibility Level

### Module-Private

This is the default when no visibility keyword is present.

Meaning:

- visible only inside the current module
- not visible to sibling modules
- not visible to downstream packages
- not linker-exported

This is the default for ordinary top-level declarations.

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
- type alias declarations

These keywords may also apply to submodule declarations if Stark later adds explicit nested submodule declarations in source.

Visibility keywords may also apply to member functions inside `struct` and
`record` bodies.

Member-function visibility follows these rules:

- if no visibility keyword is written, the member function inherits the
  visibility of the enclosing type, except that omitted members inside an
  `export` type resolve to `public` rather than accidental ABI visibility
- an explicit member-function visibility overrides the inherited visibility
- a member function may narrow visibility relative to the enclosing type
- a member function may not be more visible than the enclosing type
- `export` is never inherited accidentally; an ABI-visible member function must
  write `export` explicitly and must still satisfy the enclosing-type visibility
  cap

Examples:

```stark
public struct TcpClient {
    fn bool IsOpen(self);             // public, inherited from TcpClient
    internal fn i64 RuntimeHandle(self); // internal, explicit narrowing
}

internal struct PlatformSocket {
    fn bool IsOpen(self);             // internal, inherited from PlatformSocket
    public fn i64 Handle(self);       // error: more visible than PlatformSocket
}
```

## What Visibility Keywords May Not Apply To

Visibility keywords do not apply to:

- local variables
- function parameters
- generic parameters
- block-scoped declarations
- statements
- expressions
- match arms
- plain imports

`export import` is a dedicated import form, not a general visibility modifier applied to imports.

In the initial model, visibility keywords do not apply to individual fields
inside a type body. Field visibility remains a separate type-opacity and
representation-stability design topic.

Visibility is defined at the top-level module declaration boundary.
Member functions are the only current exception: they may explicitly narrow the
visibility inherited from the enclosing type.

## Recommended Simplicity Rule

The simplest good rule set is:

- top-level declarations may carry `internal`, `public`, or `export`
- member functions inherit enclosing type visibility unless explicitly narrowed
- member functions may not be more visible than the enclosing type
- fields and local declarations do not carry visibility modifiers
- everything else is module-private by default

This keeps the surface model small and easy to reason about.

## Optimization Intent

The source language does not expose linker or backend terms directly.
Programmers choose only the Stark visibility level:

- module-private
- `internal`
- `public`
- `export`

The build then preserves the most restrictive correct binary visibility for the
selected output. This is why Stark separates `public` from `export`: a
declaration can be part of the Stark source API without becoming an open binary
ABI symbol.

## Executables and Shared Libraries

### Executables

When building executables, declarations that are not `export` are treated as
local to the final program wherever possible. This keeps ordinary calls and data
access direct and predictable.

### Shared Libraries

When building shared libraries, only declarations explicitly marked `export`
are intended to become ABI-visible. Ordinary `public` declarations remain Stark
source API, not automatic foreign ABI.

## Generics and Monomorphization

Stark generics are intended to stay zero-cost from the user's perspective.
Generic functions and generic types are specialized for the concrete types used
by a program when a body is available.

From source code, this means:

- a generic declaration can be `public` or `export` like any other declaration
- a public generic package API can be used by downstream Stark code without the
  downstream package having the original source files on disk
- repeated use of the same concrete instantiation behaves as one logical
  instantiation, not as user-visible duplicated work
- `cold`, `noinline`, `inline`, and `inlinehint` still express the author's
  intent for generic functions
- generic specialization does not introduce dynamic dispatch or hidden runtime
  lookup

Example:

```stark
module Boxes

public struct Box<T> {
    T Value;

    finite law T Get(borrow Box<T> self) {
        return self.Value;
    }
}

public finite law Box<T> MakeBox<T>(T value) {
    return new Box<T>() { Value = value };
}
```

A downstream module can import `Boxes`, call `MakeBox<i32[0 max]>`, and use
`Box<i32[0 max]>` as an ordinary concrete type.

Additional package and specialization rationale is documented in
[PackageImage.md](../Internals/PackageImage.md) and
[LanguageInternals.md](../Internals/LanguageInternals.md).

## Constants and Global Data

Stark aggressively distinguishes immutable global data from mutable global state.

The following are immutable unless explicitly declared otherwise:

- string literals
- numeric lookup tables
- sealed dispatch tables when possible
- type metadata when possible
- variant tables when possible

Immutable global data should be written with `const` when the reachable object
graph is meant to be deeply readonly. Use `static` or `static mut` when the
binding or reachable value needs ordinary global state semantics.

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

## Compilation Strategy

Stark is designed to work well with whole-program and package-aware optimized
builds.

The source-level rules that matter are:

- keep helpers module-private or `internal` unless they are true package API
- use `public` for downstream Stark source API
- use `export` only for ABI boundaries
- keep imports explicit
- keep package boundaries deliberate

Those choices give the build tools more room to optimize without changing the
source program's meaning.
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
- importing a module makes its visible top-level declarations available by final name
- top-level declarations are module-private by default
- `internal`, `public`, and `export` are the visibility keywords
- `public` and `export` are not the same concept
- visibility modifiers apply to top-level declarations and member functions, not to locals or statements
- member functions inherit enclosing type visibility unless explicitly narrowed
- member functions may not be more visible than their enclosing type
- fields and local declarations do not carry visibility modifiers in the initial model
- executables and shared libraries both default to the most restrictive practical visibility

This model is intentionally simple at the source level and intentionally restrictive at the optimization level.
