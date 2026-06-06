# IR Memory And Fact Model

Status: accepted direction for self-hosting prep.

This document locks in the compiler IR memory model decision from OQ-16/S17.
It is a work-in-progress implementation guide, not a byte-level data-structure
spec.

## 1. Decision

The self-hosted compiler uses arena/table storage with typed handle indices for
compiler IR graphs.

Use this model for MIR, SSA, compiler artifacts, symbol/type tables, package
fact imports, and other graph-shaped compiler data:

- dense arrays/tables own the data
- typed handles identify rows in those tables
- handles are distinct types, not interchangeable integers
- pass builders allocate through typed table APIs
- bulk release happens at the compilation, module, function, or pass boundary

Owned trees remain acceptable for truly tree-shaped data, such as syntax trees
or simple HIR forms, when cross references are not central. Cross references
from trees into compiler state still use typed handles.

Reference-counted shared ownership (`Rc`/`Arc`) is not the default compiler IR
model for self-hosting. Add it later only if a concrete non-IR use case proves
that arena/table ownership is the wrong fit.

## 2. Goals

- Keep compiler IR storage explicit, fast, and deterministic.
- Avoid translating the C# host's garbage-collected object graph directly.
- Make backend and optimization facts easy to carry through lowering.
- Make fact loss visible to validation, not a quiet backend bug.
- Keep package-image durable facts distinct from transient pass-local facts.
- Preserve Stark's explicit ownership model without adding hidden runtime
  dispatch or hidden shared state.

## 3. Typed Handles

Typed handles should be small value types around compact indices:

```stark
struct MirValueId
{
    Index: u32,
}

struct MirBlockId
{
    Index: u32,
}

struct SsaValueId
{
    Index: u32,
}

struct TypeId
{
    Index: u32,
}
```

The examples are illustrative only. The final spelling should follow the
compiler's actual Stark struct and visibility rules.

Rules:

- `MirValueId`, `MirBlockId`, `SsaValueId`, `TypeId`, `SymbolId`, `PackageId`,
  and similar IDs are different types.
- APIs accept the narrowest handle type they need.
- Raw integer indices do not cross module boundaries as ordinary compiler API.
- Prefer no slot reuse inside a phase for speed and simpler diagnostics.
- Add generation counters only for tables where deletion/reuse is proven
  necessary.

## 4. Fact Tables

Backend facts, optimization facts, and source-derived semantic facts are
first-class compiler data. They are not comments, string metadata, or optional
side effects that passes try to remember.

Facts attach to typed handles through dense side tables or typed sparse tables:

```stark
struct ValueFacts
{
    Alignment: Option<AlignmentFact>,
    AliasClass: Option<AliasClassId>,
    Layout: Option<TypeLayoutId>,
    CallingAbi: Option<AbiKind>,
    IsVolatile: bool,
    NoAlias: bool,
}
```

The exact fact records can expand over time. New fact categories should declare:

- where the fact attaches: value, block, function, type, symbol, package, or
  diagnostic
- which phase owns it: syntax, HIR, MIR, SSA, backend, package, or tooling
- who creates it
- who consumes it
- whether it is transient, recomputable, or durable
- whether it is serialized through package images
- which verifier catches incorrect loss or misuse

## 5. Lowering Policy

Every lowering pass that creates new handles from old handles must define the
policy for each relevant fact category:

| Policy | Meaning |
|---|---|
| `preserve` | Carry the fact directly to the equivalent lowered handle. |
| `translate` | Convert the fact into the lower phase's fact representation. |
| `consume` | Use the fact intentionally and remove it from later phases. |
| `recompute` | Invalidate the fact and require a later pass to rebuild it. |
| `forbid-drop` | Report an internal compile-time/compiler validation error if the fact is lost. |
| `debug-only` | Allow the fact to disappear from optimized output. |

Lowering builders should make the common path low effort:

```stark
stack dst = builder.EmitLoad(srcAddress);
facts.Inherit(dst, srcAddress);
facts.SetAlignment(dst, knownAlignment);
```

The examples are illustrative only. The core rule is that fact transfer should
be part of the builder contract, not an afterthought in each lowering pass.

## 6. Verification

The compiler should validate fact flow at phase boundaries:

- HIR to MIR
- MIR validation
- MIR to SSA
- SSA validation
- optimization pass boundaries that create or delete values
- ABI lowering
- libLLVM emission
- package-image write/load boundaries for durable facts

Validation should catch:

- dropped `forbid-drop` facts
- wrong alias class or noalias proof use
- ABI/calling-convention facts missing from callable values
- layout/alignment facts missing where backend lowering requires them
- package-image durable facts missing after serialization or load
- stale handles from a previous phase or table

Accepted Stark programs must not rely on LLVM or native backend behavior to
catch these mistakes. Backend validation may still assert the compiler's own
invariants.

## 7. Package Image Boundary

Package images carry durable facts that must survive package boundaries, such as:

- public type layout
- ABI and calling convention
- extern signatures
- struct layout, packing, alignment, and packed-field facts
- platform C alias mappings
- doctrine/trait satisfaction and associated-type facts
- exported symbol metadata
- generic-template bodies and planning facts
- constant values and bounded integer facts needed downstream

Pass-local facts remain in compiler IR fact tables unless the package image
explicitly stores lowered IR or a durable summary needs the fact.

The package-image model should use the same discipline as compiler IR facts:
typed sections, declared durability, explicit compatibility checks, and
validation before facts enter compiler tables.

## 8. Non-goals For Self-hosting V1

- Do not add a general garbage-collected compiler object graph.
- Do not make `Rc`/`Arc` the normal IR representation.
- Do not serialize every transient MIR/SSA fact through package images.
- Do not make hash-table iteration define fact, package-image, diagnostic, or
  golden output order.
- Do not require the `arena` storage class to exist before the compiler can use
  arena/table style data structures. A library-owned arena/table API is enough
  if it satisfies the ownership and performance requirements.

## 9. Work Items

- [x] Decide OQ-16/S17: arena/table storage with typed handles is the blessed IR
      memory model for self-hosting.
- [ ] Define the shared typed-handle conventions for compiler IDs, including
      naming, visibility, invalid-handle policy, and whether any tables need
      generation counters.
- [ ] Define table/arena ownership scopes for syntax, HIR, MIR, SSA, package
      imports, backend state, and per-pass temporary state.
- [ ] Add or finalize `System.Memory` support needed for compiler-owned
      arenas/tables, bulk release, and fast dense storage.
- [ ] Define the first fact categories for values, functions, blocks, types,
      symbols, packages, diagnostics, alias proofs, ABI, layout, alignment,
      integer ranges, and ownership/drop facts.
- [ ] Define fact durability: transient, recomputable, durable package fact, or
      debug-only.
- [ ] Define the lowering policy table for HIR to MIR, MIR to SSA, SSA
      optimization, ABI lowering, libLLVM emission, and package-image write/load.
- [ ] Add fact-transfer helpers to IR builders so common preserve/translate paths
      are explicit and low effort.
- [ ] Add validation that reports dropped `forbid-drop` facts and stale/wrong
      handle use at phase boundaries.
- [ ] Teach package-image sections to preserve durable compiler facts and reject
      malformed or incompatible fact sections during load.
- [ ] Add focused tests for fact preservation through lowering, optimization,
      package-image round trip, and libLLVM emission.
