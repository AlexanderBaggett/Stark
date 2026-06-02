# Self-hosted Compiler Architecture

Status: WIP. This document records the architecture direction for the Stark
compiler once it is written in Stark. It is intentionally incomplete and should
grow as decisions are made.

## Design Principles

- The host compiler remains the ground truth until bootstrap succeeds.
- Stark source should not hide dispatch, allocation, aliasing, mutation, or
  failure.
- Traits and doctrines are compile-time contracts.
- Runtime dispatch must be explicit.
- Closed-world choices should use enums and exhaustive `switch`.
- Open runtime extension points should use explicit ops tables.
- Wrong alias/noalias proof use must be a compile-time diagnostic, not backend
  undefined behavior.
- Accepted programs should not fall into "unsupported lowering" paths. Missing
  support after acceptance is a compiler bug.

## Reuse Model

The self-hosted compiler should use three reuse patterns.

### 1. Compile-time Traits And Doctrines

Use traits/doctrines for static generic reuse:

- hashing
- equality
- ordering
- formatting
- collection algorithms
- range and type helper contracts
- pure `law` helper bundles

These contracts should not create runtime values. There should be no hidden
trait object, vtable, or dynamic dispatch.

### 2. Closed-world Enums

Use enums when the compiler knows every implementation.

Good candidates:

- module resolver variants
- diagnostic output formats
- package image section kinds
- target platform families
- compiler pass kinds
- artifact render modes

Example:

```stark
enum ModuleResolver
{
    Empty(EmptyModuleResolver),
    FileSystem(FileSystemModuleResolver),
    InMemory(InMemoryModuleResolver),
    Package(PackageImageResolver),
}
```

Calls through this shape are ordinary `switch` statements. That makes the
control flow visible and exhaustively checkable.

### 3. Explicit Ops Tables

Use explicit ops tables only when the implementation set is open at runtime.
This pattern is allowed because it makes the dynamic dispatch visible.

Example:

```stark
struct ModuleResolverOps
{
    Resolve: fnptr<fn ResolveResult(
        rawmutptr<i8[min max]> context,
        ascii moduleName)>;
}

struct ModuleResolverHandle
{
    Context: rawmutptr<i8[min max]>;
    Ops: ModuleResolverOps;
}
```

Rules for this pattern:

- The context pointer is visible.
- The ops table is visible.
- Function pointer kinds are explicit.
- Unsafe/type-erased context handling is explicit.
- Memory contracts should be written on callbacks when relevant.
- Prefer a closed enum when the set of implementations is known.

Likely use cases:

- long-term plugin-like tooling boundaries
- native/package integration boundaries
- optional external services

Likely non-use cases:

- ordinary compiler passes
- syntax/type/semantic helper abstractions
- fixed package image section loaders

## Pipeline Shape

The self-hosted compiler should keep the current pipeline contract:

- parse
- syntax model
- declaration/module graph
- type checking
- semantic validation
- ownership validation
- lowering contract validation
- HIR/MIR lowering
- borrow liveness
- SSA lowering
- SSA optimization
- ABI lowering
- SSA validation
- LLVM emission

The exact pass list should continue to track `docs/Internals/CompilerPipeline.md`.
The self-hosted architecture should preserve pass artifacts as first-class
typed values rather than implicit side effects.

## Compiler Pass Representation

Preferred first direction: represent passes as explicit records or closed-world
pass kinds, not hidden interface objects.

Possible shape:

```stark
enum CompilerPassKind
{
    Parse,
    SyntaxModel,
    TypeCheck,
    SemanticValidate,
    LowerMir,
    LowerSsa,
    EmitLlvm,
}
```

This can evolve, but the early self-hosted compiler should avoid recreating C#
interface objects with hidden dispatch. If open pass registration is ever
needed, use an explicit ops-table boundary.

## Artifact Model

The compiler should keep an explicit artifact store, but the representation is
still open.

Requirements:

- artifacts are typed or tagged enough to avoid unsafe downcasts in ordinary
  code
- missing required artifacts are explicit compiler invariant failures
- diagnostics are ordinary values
- logs are ordinary values
- pass execution records include duration when time APIs exist
- tests can inspect artifacts through a stable API or machine-readable output

Open design pressure:

- C# currently stores artifacts as `Dictionary<string, object?>`.
- Stark should avoid untyped hidden object storage.
- Candidate shapes are a closed `CompilerArtifact` enum, typed per-pass structs,
  or an explicit typed handle table.

## Module Resolution

Module resolution should start with closed-world variants:

- empty resolver
- in-memory resolver
- filesystem resolver
- package image resolver
- target-aware stdlib resolver

If module resolution later becomes plugin-driven, move the plugin boundary to an
explicit `ModuleResolverOps` table rather than adding trait objects.

## Error Model

Recoverable compiler failures should be values:

- parse diagnostics
- type diagnostics
- package image load errors
- file/process/tool failures
- project manifest errors

**Invariant failure convention (closes L07, resolves OQ-05).** Stark deliberately has
no `panic`/`trap`/`assert` surface and no exceptions: invalid states are made
unrepresentable rather than detected and aborted. The host compiler's ~236
`throw new` sites map onto Stark as follows, in priority order:

| Host pattern | Stark port discipline |
|---|---|
| "Artifact/record missing from dictionary" lookups between passes (`GetRequired`, location-keyed lookups) | **Restructure so the state cannot exist**: pass N hands pass N+1 a typed value attached to the right node, not a stringly-keyed lookup that can miss. |
| "Unexpected node kind" / "unsupported case" switches over IR shapes | **Exhaustive switches over closed enums** (and ranged integers): a missed case is a compile error in the ported compiler, not a runtime throw. |
| Toolchain and environment failures (clang missing, link failed, file unreadable) | **These are not bugs — they are errors.** `Result<T, E>` + `try` propagation up to the driver, reported as ordinary diagnostics. |
| True self-detected bugs (the small residual: "this should be impossible") | `Result<T, InternalError>` propagated with `try` to `main`, reported as `error: internal: <message>`; or, where threading a Result is genuinely impractical, print to stderr + `System.Process.Exit(1)`. |

The port never introduces a hidden control-flow giving-up mechanism. The compile-time
guarantees that make the first two rows possible — switch exhaustiveness and
definite-return analysis — are language features (STK3044/STK3045), so the ported
compiler's own impossible states are compile errors in the ported compiler's source.

## Alias And Memory Facts

Alias/noalias facts are part of the compiler's typed model.

Source facts:

- default non-overlap for memory-backed parameters
- `where disjoint(...)`
- `where overlap(...)`
- `where same(...)`
- `if disjoint(...)`
- `unsafe assume disjoint(...)`

Lowered facts:

- scoped noalias groups
- memory root keys
- parameter memory summaries
- call memory summaries
- loop independence facts

Architecture rule:

Wrong alias-class or wrong noalias-proof use must be rejected during type
checking, semantic validation, lowering contract validation, or SSA validation.
It must not become undefined behavior delegated to LLVM.

## Memory Ownership Direction

The C# host compiler relies on shared managed object graphs. The Stark compiler
should not copy that model blindly.

Preferred direction to investigate:

- arena or owned collection storage for large IR graphs
- stable handles/indices for cross references
- explicit ownership and drop behavior
- explicit unsafe shared capability only where truly required

Still open:

- whether `arena` becomes a real executable local storage class before the
  compiler port
- whether an `Rc`/`Arc` style stdlib type is needed
- how much of MIR/SSA should be represented as handle-indexed tables

## Testing Architecture

The port is TDD-first.

First-stage tests should be Stark tests that run against the current C# host
compiler. They need:

- process execution and capture
- temp directory/file fixtures
- rich assertions
- text diff/snapshot helpers
- diagnostic comparison helpers
- machine-readable compiler artifact output or a stable compiler test API

Only after those tests exist should compiler subsystems be ported.

## Bootstrap Shape

Planned stages:

- Stage0: current C# host compiler
- Stage1: Stark compiler built by Stage0
- Stage2: Stark compiler built by Stage1

Bootstrap success means Stage2 can build the compiler and pass the ported test
suite. Snapshot compiler policy is still open.
