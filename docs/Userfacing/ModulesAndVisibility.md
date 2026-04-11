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
- type alias declarations

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
- plain imports

`export import` is a dedicated import form, not a general visibility modifier applied to imports.

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

Within one compilation, Stark should assign each generic instantiation to a
single owning module:

- source-backed templates are owned by their defining module
- manifest-backed imported templates are owned by the root consumer module

This keeps ownership deterministic for later monomorphization without forcing
runtime indirection or duplicate local-specialization planning inside one build.

Identical concrete instantiations for the same owner module should also be
deduplicated before later lowering so the compiler does not plan or emit the
same specialization work more than once per build.

For `v1.1` planning, these owned instantiations should use deterministic,
fully-spelled internal symbol names derived from owner module, template name,
and concrete type arguments rather than sequence numbers or hash-only names.

The current code-size planning heuristics are intentionally simple:

- declaration-only generics stay declaration-only in the plan
- `cold` or `noinline` generics prefer reduced cloning
- tiny or explicitly inline generics prefer inline-friendly specialization
- everything else stays on the default specialization path

The current linkage planning rules are also intentionally simple:

- root-owned instantiations prefer single-owner internal linkage
- manifest-consumer-owned instantiations also stay single-owner internal
- source-backed imported instantiations prefer ODR-style deduplicable linkage with comdat-compatible grouping

When a package image publishes public or export generic template bodies, a
manifest-backed consumer may materialize and emit its owned concrete
specialization without needing the original source module on disk.

Package-image modules also preserve an explicit public or export source-surface
section for imports, re-exports, aliases, globals, types, and functions, so
tooling and fallback source bridging do not have to recover that surface only
from typed compiler facts.

Compiler-owned package image data is now also grouped explicitly under
compiler sections, so typed interface data, compiler facts, and generic
template sections no longer need to exist only as flat module-level fields.
The older flat fields remain temporarily as a compatibility bridge while the
sectioned path becomes primary.

New compiler-emitted package images now write typed interface data, compiler
facts, and generic template sections to those explicit compiler sections
instead of duplicating the same compiler-owned data into the older flat
compiler fields.

When both representations are present at once, the compiler prefers the
explicit compiler sections over the legacy flat fields.

The explicit source-surface section is similarly preferred over the older flat
source-surface fields. If the explicit source-surface section is absent, the
older flat surface fields remain as a compatibility fallback for authored
overload identity and temporary source bridging.

New compiler-emitted package images now write authored source-surface data to
the explicit source-surface section instead of duplicating that same authored
surface into legacy flat fields.

Imported package-image concrete layout facts are now also consumed during
monomorphization planning so manifest-backed generic instantiations with large
by-value aggregate ABI cost prefer code-size reduction instead of being treated
like trivially inline helpers.

Specialization planning now also uses imported ABI facts directly when deciding
whether a manifest-backed generic specialization can keep an ABI fallback path.
If a package image publishes a specializable generic body but omits ABI facts
for that template, the compiler now plans only the owned concrete body path
instead of claiming an unavailable ABI boundary fallback.
that explicit source-surface section instead of duplicating the same authored
surface into the older flat source-surface fields.

That source-surface section now preserves authored declaration spellings for
published type references such as alias-based function signatures, record
primary-constructor parameters, record fields, and published method
signatures, instead of normalizing all of them through the typed interface
first.

When a typed interface and explicit source surface are both present, the
temporary package-image source bridge can now use that authored source-surface
overload identity to find published generic template bodies, even while its
emitted fallback declarations still use canonical typed-interface spellings.

If a published imported generic function or method also carries a supported
typed template body,
including simple explicit conversion helpers, unary and binary operator
helpers, conditional helpers with binary or logical conditions, simple
module-qualified direct-call helpers, receiver-style member-call helpers,
simple index-access helpers over already-supported MIR indexable families,
including text-slice helpers, simple chained field/index/member receiver forms,
grouped-expression receiver forms, and direct-call-result or
object-creation-result receiver forms,
side-effect-only direct/member-call statements for void helpers, explicit
`return;` in void helpers, simple local `const` helpers, and local-update
helpers with mutable reassignment and simple `if`/`else` branching or simple
`while`/`for` loops, including structural `break` and `continue` inside those
loops, plus simple switch-pattern helpers over already-published enum and
aggregate pattern facts, that end in a return,
the temporary package-image source bridge may now emit only the declaration
surface and let downstream type checking plus MIR lowering consume the typed
template body directly instead of relying on reconstructed body text.

Those package-image template sections now also preserve published code-size
planning facts such as `cold`/`noinline` intent, top-level statement count,
and typed primary-constructor facts for object creation lowering, so imported
generic planning and MIR lowering do not need to recover all of that from
reconstructed source text.

They also preserve typed local declaration facts, so imported generic bodies
do not need to recover local `const`, local variable, or `for` initializer
types from bridge source text during type checking and MIR lowering.

They also preserve typed direct-call target facts, so imported generic bodies
can keep resolving direct helper calls even when the bridge body text is no
longer the source of truth for callee lookup.

They also preserve typed explicit-conversion target facts, so imported generic
bodies can keep lowering explicit casts even when the bridge conversion type
text is no longer trustworthy.

They also preserve typed enum-constructor facts, so imported generic bodies can
keep lowering named-field enum constructors even when the bridge enum case
target or member names are no longer trustworthy.

They also preserve typed tuple-enum-constructor call facts, so imported generic
bodies can keep lowering positional enum constructor calls even when the bridge
enum case target is no longer trustworthy.

They also preserve typed unit-enum-case value facts, so imported generic bodies
can keep lowering unit enum cases even when the bridge enum case target is no
longer trustworthy.

They also preserve typed enum-pattern target facts, so imported generic bodies
can keep type-checking and lowering enum switch patterns even when the bridge
enum case target is no longer trustworthy.

They also preserve typed enum-pattern member facts, so imported generic bodies
can keep type-checking and lowering named-field enum switch patterns even when
the bridge member names are no longer trustworthy.

They also preserve typed aggregate-pattern target facts, so imported generic
bodies can keep type-checking and lowering aggregate switch patterns even when
the bridge aggregate type text is no longer trustworthy.

They also preserve typed field-access facts, so imported generic bodies can
keep lowering projected field reads without rediscovering field layout or
projected field types from the bridge body text.

They also preserve typed object-creation target types, so imported generic
bodies can keep resolving `new TypeName(...)` aggregate targets even when the
bridge type text is no longer trustworthy.

They also preserve typed object-initializer member facts, so imported generic
bodies can keep lowering `new T() { ... }` field assignments even when bridge
field names are no longer the source of truth.

They also preserve typed member-call target facts, so imported generic bodies
can keep lowering receiver-style helper calls even when the bridge body text is
no longer trustworthy for member lookup.

They also preserve a first typed template-body subset for simple helper
bodies such as `return value;`, `return 1;`,
`return takeLeft ? left : right;`, `return new Box<T>(value);`,
`return Boxed<T>.Value { Data: value, Tag: tag };`,
`return Boxed<T>.Value { Data: value, Tag: 1 };`,
`return Option<T>.Some(value);`, `return Option<T>.None;`,
`return box.Value;`, `return box.Echo(value);`, `return Callee(value);`, and
`stack T copy = value; return copy;`, plus local `const` helpers like
`const T copy = value; return copy;`, plus void helper statements like
`ResetValue(box);` or `box.Reset();`, so imported generic MIR lowering can
prefer structured body facts over reconstructed bridge text for those helper
shapes.

Package-image modules also preserve plain imports in addition to `export
import` re-exports, so imported generic bodies can continue to resolve
transitive module dependencies after package publication.

Imported public and export type aliases now also resolve from typed
package-image facts instead of reconstructed bridge alias declarations, so
manifest-backed consumers do not need to trust bridge alias target text just
to use a published alias.

Imported public and export globals now also resolve from typed package-image
facts instead of reconstructed bridge global declarations, so manifest-backed
consumers do not need to trust bridge global type text just to use a published
constant or static variable.

Imported public and export named type shape now also resolves from typed
package-image facts instead of reconstructed bridge type declarations, and
record primary-constructor shapes do the same, so manifest-backed consumers do
not need to trust bridge field or primary-constructor type text just to create
or project those imported types.

Imported explicit struct and record constructor signatures also resolve from
typed package-image facts instead of reconstructed bridge declarations, so
manifest-backed consumers do not need published constructor bodies or
declaration text just to call those imported constructors.

Imported trait and doctrine methods now also take their published signatures
from typed package-image facts instead of reconstructed bridge declarations,
so manifest-backed consumers do not need to trust bridge return or parameter
type text just to use those imported compile-time-only method surfaces.

Published package-image record types also preserve their primary-constructor
shape, so imported generic bodies can construct those records directly rather
than depending on a field-only approximation at the package boundary.

They also preserve deferred nested-generic instantiation patterns, so
recursive package-boundary specialization planning can follow generic callees
without rediscovering that structure from the imported body text first.

They now also preserve deferred nested generic type-instantiation patterns, so
package-boundary monomorphization planning can keep concrete imported helper
types aligned with the concrete generic bodies that need them.

That specialization materialization is now recursive for discovered generic
callee dependencies, so one owned concrete body may cause additional owned
concrete bodies to be emitted in the same build.

Within that build, repeated requests for the same concrete instantiation stay
deduplicated under one single-owner internal symbol, and call sites target
that concrete symbol directly instead of an ABI fallback or template symbol.

The current specialization-priority rules are also intentionally simple:

- declaration-only instantiations stay on the direct ABI fallback path
- source-backed generic instantiations prefer an owned concrete body before any ABI fallback
- eligible imported `law` instantiations may add a caller-specialized clone path ahead of the owned body
- `cold` or `noinline` planning suppresses that clone path to keep code-size-oriented instantiations on the owned-body or ABI path

If two different generic templates would map to the same fully spelled internal
specialization symbol, Stark now reports that conflict during specialization
planning rather than silently picking one.

The current codegen-strategy bridge is also intentionally simple:

- ABI-only specializations keep using the existing Stark ABI surface
- owned specializations plan one concrete emitted body under the monomorphized symbol
- eligible imported `law` specializations may additionally expose a law-caller clone path while keeping the owned body as the general fallback

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
