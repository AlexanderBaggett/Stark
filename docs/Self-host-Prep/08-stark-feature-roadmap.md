# Stark Feature Roadmap For Self-hosting

Status: WIP. This is a living task list for language and stdlib features that
help Stark become a practical implementation language for its own compiler.

The main design pressure is reuse without hidden behavior. Stark should keep
runtime costs, dispatch, allocation, aliasing, and failure explicit.

## Compile-time Reuse

- [x] Strengthen compile-time-only `trait` and `doctrine` support.
- [x] Add default method bodies for compile-time-only traits/doctrines.
- [x] Add associated types for compile-time contracts.
- [x] Define canonical `Hash`, `Eq`, `Ord`, and `Format` style contracts.
- [x] Keep ordinary traits/doctrines as compile-time contracts, not runtime objects.
- [x] Do not add hidden trait objects, hidden vtables, or implicit dynamic dispatch.

Notes:

Default method bodies, associated type requirements/defaults, and canonical
`Eq`, `Hash`, `Ord`, and `Format` contracts make generic compiler code much
easier to write without hidden trait objects. The compiler resolves ordinary
trait/doctrine contracts statically and emits concrete code.

Landed associated-type slice:

- Traits, doctrines, structs, and records may declare associated aliases inside
  their body.
- `alias Name;` is a required associated type in a trait.
- `alias Name = Type;` defines a concrete/default associated type.
- Implementers must define every required trait associated type; missing
  definitions are compile-time diagnostics.
- `Self.Name` and `T.Name` are valid type positions for associated types.
- Generic instantiation resolves concrete associated aliases before SSA/LLVM
  validation and emission, preserving direct static dispatch.
- Package images preserve associated type requirements/defaults in the typed
  interface, source bridge, and compiler facts.
- `dyn trait` currently rejects associated types until Stark has an explicit
  object spelling for associated-type bindings.

Landed slice: `Dictionary<K, V>` now accepts non-primitive key types when the key
type declares explicit static `finite law` methods:
`u64[0 max] Hash(borrow K value)` and
`bool Equals(borrow K left, borrow K right) where overlap(left, right)`. Bool
and integer keys still use the compiler-known scalar fast path.

Blessed self-hosting model: public collections use generic `Hash` + `Eq`,
`Ord`, and `Format` contracts; the compiler interns stable names at front-end
and package boundaries and then uses distinct typed IDs in hot paths. See
`19-generic-collections-and-interning.md`.

Useful self-hosting targets:

- `Dictionary<K, V>` and `HashSet<T>` keys through canonical `Hash` + `Eq`
  contracts; `Ascii`/`Unicode` helpers still need stdlib contracts.
- Generic equality and ordering for deterministic compiler output via
  `System.Collections.Eq` and `System.Collections.Ord`.
- Generic hashing via `System.Collections.Hash` with associated `Code`.
- Generic formatting via `System.Collections.Format` with required associated
  `Writer`.
- Strongly typed compiler ID keys such as `SymbolId`, `TypeId`, `ModuleId`, and
  `PackageId` for interned compiler names.
- Reusable collection algorithms without a runtime dispatch layer.

## Alias/Noalias Proofs

- [x] Decide the proof model: explicit compile-time-only proof carriers for
      APIs that need alias facts.
- [x] Require wrong alias/noalias proof use to be a compile-time diagnostic,
      not backend undefined behavior.
- [x] Keep external alias facts fenced behind `unsafe assume disjoint(...)`
      with explicit memory-root checks.
- [ ] Implement proof-carrier symbols/types, validation, package-image
      preservation, and diagnostics.

Notes:

Proof carriers make alias-sensitive compiler APIs explicit without adding
runtime cost. They are visible in Stark source, checked by type/lowering/SSA
validation, and erased before codegen. The compiler may still lower verified
facts into scoped noalias groups, memory root keys, and LLVM metadata, but LLVM
is not the first line of defense.

## Threading Coordination

- [x] Decide build-driver concurrency scope: synchronous self-hosting driver
      first, no `async`/`await`.
- [x] Limit future parallel build/test support to captured thread payloads,
      ergonomic guarded shared state, and channels.
- [ ] Add captured/payload thread starts checked by `Transferable`.
- [ ] Add `System.Threading.Synchronized<T>` and `Locked<T>` as the blessed easy
      shared-state primitive.
- [ ] Add MPSC channels for progress, diagnostics, and result publication.
- [ ] Keep thread pools, work stealing, `RwLock`, `Once`, condition variables,
      semaphores, thread locals, and parallel compiler passes out of the
      self-hosting scope unless a later decision reopens them.

Notes:

This is deliberately small. Stark already has threads and atomics; doc `22`
defines the coordination layer needed if project builds or test execution become
parallel. Shared mutable data should be wrapped in `Synchronized<T>` and accessed
through an owned guard. Workers should publish events through channels, and the
driver should aggregate diagnostics/artifacts in deterministic order.

## libLLVM Backend Integration

- [x] Decide LLVM integration: libLLVM is the primary backend through the LLVM C
      API.
- [x] Keep textual LLVM as a debug, diagnostic, golden-test, stage-comparison,
      and artifact-inspection output.
- [ ] Implement the verified FFI pieces still required by libLLVM: C strings,
      out-pointer patterns, typed opaque handles, deterministic foreign-resource
      disposal, and C enum/bitflag constants.
- [ ] Add the initial direct LLVM module-construction and object-emission path
      through typed wrappers.
- [ ] Expand direct LLVM module construction until it covers the full backend.

Notes:

This direction treats libLLVM as roadmap work, not as a blocker to avoid. The
binding must use LLVM's C API only. Textual LLVM remains valuable for debugging
and golden artifacts, but it must be printed from the in-memory module and never
parsed as a bootstrap or production object-emission path. See
`23-libllvm-integration.md`.

## Explicit Runtime Dispatch

- [ ] Add a blessed pattern for explicit runtime dispatch using ops tables.
- [ ] Keep ops tables visible in source as ordinary structs.
- [ ] Require dispatch function pointers to spell their function kind, such as
      `fn`, `finite`, `law`, or `finite law`.
- [ ] Make any type-erased context pointer or unsafe boundary explicit.
- [ ] Prefer closed-world enums when the set of implementations is known.
- [ ] Reserve ops tables for genuinely open runtime extension points.

An explicit ops table is basically a vtable, but it is not hidden. The caller
can see the context pointer, the ops table, and the function pointer types.
That keeps the cost model and safety boundary Stark-shaped.

Example shape:

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

Open questions for this pattern:

- [ ] Should the context pointer always be raw, or should Stark offer a typed
      erased-handle wrapper?
- [ ] Should ops tables be `const` by convention?
- [ ] Should ops functions carry explicit memory contracts such as
      `where disjoint(...)` when they touch caller buffers?
- [ ] Should there be a standard naming convention: `FooOps`, `FooHandle`,
      `FooContext`?

## Closed-world Runtime Choice

- [ ] Prefer `enum` plus exhaustive `switch` for runtime variation when all
      implementations are known.
- [ ] Use this for compiler-internal choices such as module resolver kind,
      diagnostic output mode, package section kind, target platform, and pass
      kind where practical.

Example shape:

```stark
enum ModuleResolver
{
    Empty(EmptyModuleResolver),
    FileSystem(FileSystemModuleResolver),
    InMemory(InMemoryModuleResolver),
    Package(PackageImageResolver),
}
```

This keeps dispatch visible and gives the compiler exhaustiveness checks.

## Pattern Matching

- [x] Add switch-label or-pattern alternatives: `case A | B:`.
- [x] Require alternatives that share a switch body to bind the same capture
      names with the same types.
- [x] Preserve native literal-switch lowering for literal-only or-patterns.
- [x] Add inclusive integer range patterns for dense numeric/compiler-token
      classification: `case 0..10:`.
- [x] Support range patterns inside enum/aggregate field patterns and typed
      package-image templates.
- [x] Add aggregate property patterns: `case Box { Field: pattern }:`.
- [x] Add exact-length list patterns over fixed arrays, slices, and dynamic
      storage: `case [first, second]:`.
- [x] Preserve property/list pattern facts in typed package-image templates.

Notes:

Or-patterns are section-local alternatives: `case A | B when guard:` tests
`A`, then `B`, and either successful alternative flows into the same guarded
body. Capture-bearing alternatives must agree on names and types so the body's
locals are definitely initialized regardless of which alternative matched.

Range patterns are inclusive and integer-only. Type checking rejects empty or
non-overlapping ranges, coverage uses intervals rather than value expansion,
and lowering emits equality, one-sided, or two-sided comparisons depending on
the target type bounds.

Property patterns name aggregate fields explicitly and must mention every field
exactly once. List patterns are exact-length only: fixed-array length mismatches
are compile-time errors, while slice and dynamic-storage patterns lower to a
runtime length check followed by direct element tests. No iterator protocol,
hidden allocation, or runtime dispatch is involved.

## Traversal Loops

- [x] Decide the pre-self-hosting traversal surface: exactly three explicit
      `for ... in ...` loop forms.
- [x] Do not add a general iterator protocol, `yield`, LINQ-style traversal,
      hidden iterator allocation, or hidden runtime dispatch before
      self-hosting.
- [x] Implement borrowed element traversal:

```stark
for willexit (borrow Token token in tokens)
{
    Process(token);
}
```

- [x] Implement mutable borrowed element traversal:

```stark
for willexit (borrow mut Token token in tokens)
{
    Normalize(token);
}
```

- [x] Implement indexed borrowed traversal:

```stark
for willexit (stack u64[0 max] index, borrow Token token in tokens)
{
    Record(index, token);
}
```

Notes:

These are loop syntax conveniences over optimized collection/slice traversal.
They must preserve Stark's explicit loop behavior keyword (`willexit`,
`independent`, and so on) and must not allocate iterator objects. Hot compiler
paths and shapes outside these three forms should continue to use explicit
`Length` / index / slice APIs. Mutating with an index remains an explicit
C-style indexed loop before any broader traversal design.

Implementation status: landed for fixed arrays, slices, and dynamic storage.
Traversal lowers to a counted loop over existing element-address operations; it
does not allocate an iterator object or introduce hidden runtime dispatch.
Typed package-image generic bodies preserve traversal source/index/element
bindings explicitly, so imported generic helpers lower without falling back to
body text or generated parser source.

## Comptime Generics

- [x] Decide Stark's const-generic spelling: typed `comptime` generic value
      parameters.
- [x] Use `comptime`, not `const`, because Stark `const` means deep interior
      immutability while this feature means compile-time generic
      specialization.
- [x] Implement typed comptime generic value parameters:

```stark
struct FixedBuffer<T, comptime u64[0 max] N>
{
    Items: T[N];
}
```

- [x] Allow range-typed integer comptime generic values in fixed-array lengths
      and materialize specialized values as scalar expressions such as
      `return N`.
- [x] Define monomorphization identity, overload resolution, diagnostics,
      and package-image/source-bridge representation for comptime generic
      arguments.
- [x] First implementation slice: parse `comptime` generic parameters, preserve
      range-typed integer value parameters in typed signatures/named types,
      allow symbolic fixed-array lengths such as `T[N]`, infer `N` from
      concrete fixed-array arguments during overload resolution, reject
      out-of-range inferred lengths, and include comptime values in function
      instantiation keys.
- [x] Add explicit integer value-argument syntax at type and function call
      sites, including symbolic forwarding with `comptime N`.
- [x] Remaining implementation work: value substitution through imported
      template bodies and full package/source-surface metadata for comptime
      parameter declarations.

Notes:

`comptime` in a generic parameter list is a declaration marker, not expression
syntax. It binds a typed compile-time value parameter that participates in
generic specialization:

```stark
fn u64[0 max] Length<T, comptime u64[0 max] N>(borrow T[N] items)
{
    return N;
}

stack i32[min max][3] values = { 10, 20, 30 };
stack u64[0 max] count = Length<i32[min max], 3>(values);
```

The first self-hosting slice should focus on range-typed integer values for
array sizes, layout facts, fixed-capacity buffers, and table shapes. Additional
compile-time value kinds can be added when a concrete compiler or stdlib use
requires them.

## Comptime

- [x] Decide `comptime` scope: CTFE plus broad compile-time branching over
      explicit program-structure facts.
- [x] Keep program-structure facts compile-time-only and erased before backend
      lowering; do not add runtime reflection as part of this feature.
- [ ] Implement ordinary Stark CTFE for constants, aggregates, table
      generation, range/layout facts, local mutation, calls, and bounded
      `willexit` loops.
- [ ] Implement explicit structural-fact queries for types, fields, enum
      variants, functions, attributes, doctrines/traits, associated types,
      ABI/layout facts, and package metadata.
- [ ] Add diagnostics for runtime-only values, unsupported compile-time calls,
      non-terminating compile-time execution, and illegal leakage of structural
      facts into runtime.
- [ ] Preserve required structural facts through package images and imported
      typed interfaces.

Notes:

`comptime` is still ordinary Stark code selected to run during compilation.
Compile-time branching over program structure should use visible structural
facts and ordinary `if` / `switch`, not hidden runtime reflection or a separate
macro sublanguage.

## Error And Optional Values

- [x] Define shared `Option<T>` and `Result<T, E>` conventions.
- [x] Use leading `try` propagation; do not add a `?`-style propagation operator.
- [x] Do not add a compiler-invariant failure API; make invalid states
      unrepresentable and report the residual through explicit error values or
      process exit.

Self-hosting needs a replacement for C# nullability, exceptions, and
`TryGet(... out value)` patterns. Recoverable failures should remain values.
The blessed replacement for C# nullable values is `System.Option<T>` with
`[Ok] Some(T)` / `[Err] None`. Safe Stark references and borrows are never
nullable; raw `null` remains only for raw pointers and FFI. Project-local
option-shaped enums remain legal because propagation is structural over
`[Ok]`/`[Err]`, but compiler-port code should default to `System.Option<T>`.
Internal compiler bugs use the error model documented in
`09-self-hosted-compiler-architecture.md`.

## Compiler Text Literals

- [x] Add exact-preserving single-line raw string literals: `raw"..."`.
- [x] Add exact-preserving multiline raw string literals: `raw"""..."""`.
- [x] Compose raw literals with interpolation: `$raw"..."` and
      `$raw"""..."""`.
- [x] Keep raw literals in the existing `StringLiteral` token family so
      parser, typing, lowering, and package-image code continue to use the
      ordinary text-literal pipeline.
- [ ] Add Stark-side text escaping/decoding helpers in `System.Text` for
      diagnostics, LLVM text, golden files, and source snippets. Track this
      as stdlib gap S03.

Rules landed in the host compiler:

- raw literals do not interpret escape sequences
- multiline raw literals preserve content exactly between the delimiters
- raw single-line literals cannot contain an unescaped `"` or a line break
- raw multiline literals close at the next `"""`
- interpolation holes still use `{...}`, with `{{` and `}}` for literal braces

## Compiler-grade Standard Library

- [ ] String-key dictionaries for `Ascii` and `Unicode` through canonical
      `Hash` + `Eq` contracts.
- [ ] `HashSet<T>`.
- [ ] Strongly typed compiler symbol/name interning.
- [ ] Deterministic sorting and ordered set/map helpers.
- [ ] Text builder and formatting APIs.
- [ ] JSON support for `.starkpkg.json`, unless the package image format changes.
- [ ] Reusable `System.Toml` parser/emitter for `Stark.toml`,
      `Stark.solution.toml`, `Stark.user.toml`, tests, tools, and user code.
- [ ] File read-all/write-all helpers.
- [ ] Temp directory helpers.
- [ ] Process spawn with stdout/stderr capture.
- [ ] Environment and argv APIs.

## Memory Model Work

- [x] Decide compiler IR storage strategy: arena/table ownership with typed
      handle indices, first-class extensible fact tables, explicit lowering
      policies, package-image durable facts, and phase-boundary validation. See
      [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).
- [ ] Define typed compiler handles for MIR, SSA, types, symbols, packages,
      artifacts, and backend state. Handles are distinct types, not
      interchangeable integers.
- [ ] Define compiler fact categories for values, functions, blocks, types,
      symbols, packages, diagnostics, alias proofs, ABI, layout, alignment,
      integer ranges, ownership/drop, and future backend facts.
- [ ] Add fact-transfer helpers and validation so lowering policies preserve,
      translate, consume, recompute, or intentionally drop facts according to the
      declared rule.
- [ ] Preserve durable compiler facts through package images; keep transient
      pass-local facts in compiler IR tables unless a package section explicitly
      needs a stable summary.
- [ ] Keep `arena` explicit if it becomes an executable storage class. The
      compiler IR decision does not require source-level `arena` before
      self-hosting if library/compiler-owned tables satisfy the model.
- [ ] Keep unsafe shared ownership visible in source. `Rc`/`Arc` is not the
      default compiler IR model for self-hosting.
