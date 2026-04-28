# Stark Modules and Visibility

## Strategy

Stark treats module structure and visibility as performance relevant.

The rules:

* expose as little as possible
* export even less
* keep most code inside the current file or package
* let the compiler see as much of the program as possible

Restrictive visibility enables:

* dead code elimination
* constant folding across the program
* breaking structs into registers
* more aggressive inlining
* direct calls instead of runtime indirection
* direct global access instead of runtime indirection
* stronger whole program optimization

Stark defaults to private. You ask for more visibility when you need it.

## Core Model

Stark has two structural layers:

* `package` (or `library`): the build artifact and import boundary
* `module`: the file level namespace and compilation unit

Modules are the namespace system. There is no separate concept of reopenable namespaces.

## Module Declaration

Each source file declares one module:

```stark
module SomeModule
```

One `module` declaration per file. It goes after the imports.

```stark
import Math
import Math.SIMD

module Vectors
```

Imports first, then `module`, then the rest of the file.

## Imports

Imports are explicit and module based:

```stark
import SomeModule
```

Re export with `export import`:

```stark
export import SomeModule
```

Rules:

* imports go before the `module` declaration
* a plain `import` does not re export anything
* `export import` is the explicit re export form
* importing a module does not expose its nested modules
* there is no wildcard import

A plain `import` is a compile time name resolution construct. It does not imply re export or a binary symbol.

After `import System.Collections`, code may write `List<T>` instead of `System.Collections.List<T>`. This applies to any visible top level declaration: `struct`, `record`, `enum`, `trait`, `doctrine`, type alias, global, or function.

If two imports expose the same final name, the reference is ambiguous and the fully qualified name is required. Local declarations in the current module win over imports.

`export import` makes the imported module part of the package's public Stark surface.

## Files and Modules

One source file, one module.

* one file equals one module
* directory layout determines module nesting
* the `module` declaration names the file's module
* a module cannot be reopened across multiple files

```text
math/
  vectors.stark
  matrix.stark
  simd/
    dot.stark
```

## Visibility Keywords

Three keywords:

* `internal`
* `public`
* `export`

No keyword on a top level declaration means **module private**.

Member functions inside `struct` and `record` types inherit the enclosing type's visibility unless an explicit keyword is written.

## Meaning of Each Level

### Module Private (default)

* visible only inside this module
* not visible to sibling modules
* not visible to other packages
* not a binary symbol

The default for ordinary top level declarations.

### `internal`

* visible to other modules in the same package
* not visible to other packages
* not part of the package's public API
* not a binary symbol by default

Use `internal` for helpers shared across files in a package.

### `public`

* visible to other Stark code that imports the package
* part of the package's public Stark API
* not necessarily a binary symbol

`public` is a source visibility concept, not a binary symbol concept.

### `export`

* a real binary symbol
* for FFI, runtime entry points, plugin boundaries, or stable binary interfaces
* use sparingly

`export` is the weakest visibility from the optimizer's point of view.

## `public` vs `export`

A declaration may need to be:

* visible to downstream Stark code, but
* not exposed as a binary symbol

These are two concepts. Stark keeps them separate:

* `public` controls source level API visibility
* `export` controls binary level symbol visibility

Many languages collapse these. Stark does not, because binary symbols are much more restrictive for optimization.

## What Visibility Applies To

Top level declarations:

* functions
* laws
* finites
* finite laws
* global constants
* global variables
* `struct` declarations
* `record` declarations
* `trait` declarations
* `doctrine` declarations
* type aliases

Visibility also applies to member functions inside `struct` and `record` bodies. Member function rules:

* without a keyword, a member inherits the enclosing type's visibility, except that an omitted keyword on a member of an `export` type resolves to `public`, not accidental binary exposure
* an explicit member visibility overrides the inherited visibility
* a member may narrow the inherited visibility
* a member may not be more visible than its enclosing type
* `export` is never inherited; an `export` member must be written explicitly and must still satisfy the enclosing type's visibility cap

```stark
public struct TcpClient {
    fn bool IsOpen(self);                 // public, inherited
    internal fn i64 RuntimeHandle(self);  // internal, narrowed
}

internal struct PlatformSocket {
    fn bool IsOpen(self);                 // internal, inherited
    public fn i64 Handle(self);           // error: more visible than the type
}
```

## What Visibility Does Not Apply To

* local variables
* function parameters
* generic parameters
* block scoped declarations
* statements
* expressions
* match arms
* plain imports

`export import` is its own form, not a visibility modifier on an import.

Field level visibility is not part of the initial model. It is a separate type opacity and representation stability topic.

Visibility is defined at the top level declaration boundary. Member functions are the one exception: they may narrow what they inherit.

## The Simple Rule

* top level declarations may carry `internal`, `public`, or `export`
* member functions inherit the enclosing type's visibility unless explicitly narrowed
* members may not be more visible than their type
* fields and locals do not carry visibility
* everything else is module private

## Optimization Intent

The source language does not expose linker terms directly. Programmers pick a Stark visibility level:

* module private
* `internal`
* `public`
* `export`

The build then preserves the most restrictive correct binary visibility for the selected output. This is why `public` and `export` are separate: a declaration can be part of the Stark source API without becoming an open binary symbol.

## Executables and Shared Libraries

### Executables

Declarations that are not `export` are treated as local to the program wherever possible. Calls and global access stay direct.

### Shared Libraries

Only `export` declarations become binary symbols. `public` declarations remain Stark source API, not foreign ABI.

## Generics

Stark generics are zero cost. Generic functions and types are specialized for the concrete types used by a program when a body is available.

* a generic declaration can be `public` or `export` like any other declaration
* a public generic API can be used by downstream Stark code without the original source files on disk
* repeated use of the same concrete instantiation behaves as one logical instantiation
* `cold`, `noinline`, `inline`, and `inlinehint` apply to generics
* generic specialization does not introduce dynamic dispatch or runtime lookup

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

A downstream module can import `Boxes`, call `MakeBox<i32[0 max]>`, and use `Box<i32[0 max]>` as an ordinary concrete type.

Package and specialization details: [PackageImage.md](../Internals/PackageImage.md), [LanguageInternals.md](../Internals/LanguageInternals.md).

## Constants and Globals

Stark distinguishes immutable global data from mutable global state.

These are immutable unless declared otherwise:

* string literals
* numeric lookup tables
* sealed dispatch tables when possible
* type metadata when possible
* variant tables when possible

Use `const` when the reachable object graph is meant to be deeply readonly. Use `static` or `static mut` for ordinary global state.

This enables:

* constant folding of loads
* constant merging
* reduced duplicate data
* better cache behavior
* the compiler can track values across function boundaries more aggressively

## Closed by Default

The visibility model fits Stark's broader closed by default model.

Defaults:

* most declarations are not externally visible
* most helpers are module private or `internal`
* most metadata stays local to the package or final binary
* dynamic linking and runtime replacement are explicit, not default

This supports:

* fast internal calling conventions
* static dispatch
* sealed laws and traits
* stronger whole program optimization

## Practical Guidance

* keep helpers module private or `internal` unless they are real package API
* use `public` for downstream Stark source API
* use `export` only at ABI boundaries
* keep imports explicit
* keep package boundaries deliberate

These choices do not change the program's meaning. They give the build more room to optimize.

## What Stark Avoids

* reopenable namespace systems
* default public top level declarations
* automatic binary export of source visible API
* open ended symbol substitution semantics
* the kinds of weak linkage tricks that complicate generics
* widespread forced retention
* low level linker terminology in source syntax

## Summary

* modules are namespaces
* one file equals one module
* imports come before the `module` declaration
* `module SomeModule` declares the module
* importing a module makes its visible top level names available
* top level declarations are module private by default
* `internal`, `public`, and `export` are the visibility keywords
* `public` and `export` are separate concepts
* visibility applies to top level declarations and member functions, not to locals or statements
* members inherit type visibility unless explicitly narrowed
* members may not be more visible than their type
* fields and locals do not carry visibility in the initial model
* executables and shared libraries default to the most restrictive practical visibility
