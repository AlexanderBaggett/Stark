# Generic Collections And Interning

Status: WIP, decision locked.

This document records the blessed self-hosting approach for generic hashing,
equality, ordering, and compiler symbol interning.

## 1. Decision

Use the generic contract surface as the public language and stdlib rule, and
use strongly typed interning as the compiler's preferred internal
representation for hot name/key paths.

Concretely:

- `Dictionary<K, V>` and `HashSet<T>` require explicit `Hash` + `Eq` support
  for non-scalar key types.
- Ordered collections, sorting, and deterministic emitted output use explicit
  `Ord` support.
- Formatting helpers use explicit `Format` support.
- Compiler front-end boundaries intern identifiers and stable compiler names
  once, then later compiler phases use compact typed IDs such as `SymbolId`,
  `TypeId`, `ModuleId`, and `PackageId`.
- Scalar dictionary fast paths for bool and integer keys remain valid
  performance optimizations.
- There are no compiler-only dictionary semantics for strings, symbols, or
  package keys.
- Ordinary strings and text values are not secretly interned.

This combines option A and option C from OQ-08:

- A is the blessed language/stdlib contract model.
- C is the compiler's blessed hot-path representation.
- B, specialized compiler-only dictionaries, is rejected except for temporary
  migration shims that disappear before self-hosted release adoption.

## 2. Public Collection Contract Model

The stdlib collection surface should be contract-driven:

| Collection / Operation | Required contract |
|---|---|
| `Dictionary<K, V>` insertion, lookup, update, removal | `K: Hash + Eq` |
| `HashSet<T>` insertion, lookup, removal | `T: Hash + Eq` |
| deterministic sorting | `T: Ord` |
| ordered map/set keys | `K: Ord` |
| diagnostic and artifact rendering | `T: Format` where generic formatting is used |

The existing scalar fast path is an implementation detail. User-defined keys
should work by satisfying the same explicit contracts the stdlib uses.

Missing or incompatible key contracts must be compile-time diagnostics. The
compiler should report the collection operation, the key type, and the missing
contract method or associated type rather than failing later during lowering or
codegen.

Current implementation: `Dictionary<K,V>` and `HashSet<T>` expose the canonical
`Hash` + `Eq` rule, with `static finite law Hash` / `Equals` methods as the
concrete implementation hook for user-defined key types. Source-backed and
package-backed collection use sites report `STK3023` at compile time when a key
type is unsupported, missing `Hash` / `Equals`, or has an incompatible
signature, return type, or overlap contract. `SortBy<T>` sorts slices in place
with an explicit inline `Ordering` comparator and no runtime closure allocation.
`Sort<T>` sorts slices in place through `T: Ord`, lowers to direct static
`Compare` calls after monomorphization, and reports missing or incompatible
`Ord.Compare` at compile time. Ordered map/set helpers remain future work.

## 3. Text Keys

Primitive `ascii` / `unicode`, owned text types, and borrowed text views used
by compiler APIs need reusable `Hash`, `Eq`, `Ord`, and `Format`
implementations where the operation is meaningful.

For language identifiers, module names, package names, and compiler-internal
symbol keys, equality should be exact and ordinal:

- no hidden case folding
- no hidden Unicode normalization
- no locale-sensitive comparison
- no allocation during equality checks or hash computation when a borrowed view
  is already available

Any higher-level normalization policy should happen at the source boundary and
be documented as a separate language decision.

Current implementation: `ascii` and `unicode` are compiler-known text key
types for `Dictionary<K,V>` and `HashSet<T>` and lower to direct ordinal
hash/equality helpers; `OwnedAscii` and `OwnedUnicode` provide explicit static
`Hash`/`Equals` hooks plus `Compare`/`Format` helpers using the same semantics.
`System.Text.Interning` provides ASCII-backed compiler interner types that
accept borrowed `ascii` lookup keys without allocating owned text.

## 4. Compiler Interning Pattern

The self-hosted compiler should intern stable names at ownership/domain
boundaries:

- lexer/parser identifiers
- module names
- package names
- type names
- doctrine and trait names
- field and member names
- artifact keys that are frequently compared or looked up

After that boundary, compiler passes should prefer typed IDs over repeated text
comparison:

```stark
struct SymbolId
{
    internal u32[0 max] Value;
}

struct TypeId
{
    internal u32[0 max] Value;
}

struct ModuleId
{
    internal u32[0 max] Value;
}
```

Each ID type is distinct. A `SymbolId` should not type-check where a `TypeId` is
required, even though both may lower to compact integer storage.

The interner owns the canonical text storage and preserves insertion order for
stable reverse lookup. Diagnostics and debug rendering may ask the interner to
resolve an ID back to text, but hot compiler paths should compare IDs directly.

## 5. Boundary Rules

Good interning boundaries:

- tokenization of identifiers
- source/module/package loading
- package image loading
- imported surface merging
- typed artifact construction

Do not require interning for:

- arbitrary user strings
- file contents
- diagnostic message text
- normal `System.Text` APIs
- FFI strings
- temporary strings that are not used as compiler lookup keys

This keeps interning visible and architectural rather than a hidden property of
all text values.

## 6. Determinism Rules

Hash table iteration order must not define observable compiler output.

Package images, diagnostics, generated source bridges, emitted metadata, and
golden-test artifacts must use one of these stable orders:

- source order
- package image order
- interner insertion order when that order is explicitly the chosen model
- sorted order through `Ord`
- an explicit pass-defined order

Compiler hashes used for maps and sets should be deterministic for a given key
value. If a future collection adds randomized hash seeding for a specific
security purpose, that collection must not be used to determine compiler output
order.

## 7. Performance Rules

The design is speed-first:

- Intern once, compare compact IDs in hot paths.
- Use scalar dictionary fast paths for bool and integer-like ID keys.
- Support borrowed lookup so callers can query `Dictionary<OwnedAscii, V>`,
  `HashSet<OwnedUnicode>`, or an interner from `ascii` / `unicode` input
  without allocating an owned key.
- Avoid virtual dispatch, trait objects, boxed keys, and per-call-site adapter
  allocation.
- Keep generated code for static contracts as direct calls after
  monomorphization.
- Sort only at deterministic output boundaries, not inside every lookup table.

## 8. Work Items

- [x] Decide the blessed model: public generic contracts plus compiler-internal
      typed interning.
- [x] Keep bool/integer dictionary scalar fast paths.
- [x] Land canonical `Eq`, `Hash`, `Ord`, and `Format` contract names.
- [x] Land explicit static `Dictionary<K, V>` key `Hash`/`Equals` support for
      non-primitive key types.
- [x] Align `Dictionary<K, V>` and `HashSet<T>` wording/API docs around
      the canonical `Hash` + `Eq` contract surface, with the current static
      `Hash`/`Equals` methods treated as the concrete implementation hook.
- [x] Implement or verify `Hash`, `Eq`, `Ord`, and `Format` contracts for
      primitive `ascii` / `unicode`, owned text values, and borrowed text views
      used by compiler lookup APIs.
- [x] Add allocation-free borrowed lookup APIs for text-key dictionaries and
      interners. `System.Text` now exposes allocation-free borrowed
      `ContainsAsciiKey` / `TryGetAsciiKey` for `Dictionary<OwnedAscii,V>`,
      `ContainsUnicodeKey` / `TryGetUnicodeKey` for
      `Dictionary<OwnedUnicode,V>`, and borrowed `Contains*Key` wrappers for
      `HashSet<OwnedAscii>` / `HashSet<OwnedUnicode>`. `System.Text.Interning`
      exposes `Contains` / `TryGet` borrowed `ascii` lookup on each compiler
      interner without allocating owned text.
- [x] Add `HashSet<T>` using the same `Hash` + `Eq` key rule.
- [x] Add deterministic ordered map/set helpers or a documented sorting path for
      compiler artifacts that need stable output.
- [x] Add compiler interner types and typed ID wrappers for symbols, types,
      modules, packages, fields, members, and artifact keys.
- [ ] Port compiler symbol tables so front-end text is interned once and later
      phases use typed IDs.
- [ ] Ensure package image writing, diagnostics, generated source bridges, and
      golden artifacts never depend on hash table iteration order.
- [x] Add compile-time diagnostics for missing or incompatible `Hash`/`Eq`/`Ord`
      contracts at collection use sites. `Dictionary<K,V>` and `HashSet<T>`
      now diagnose missing or incompatible `Hash` / `Equals` contracts for
      source-backed and package-backed use sites; `Sort<T>` diagnoses missing
      or incompatible `Ord.Compare` contracts for sorting use sites.
- [~] Add tests for text-key dictionaries, custom key types, missing-contract
      diagnostics, `HashSet<T>`, deterministic output ordering, typed interner
      IDs, and borrowed lookups. Deterministic `SortBy<T>` runtime/lowering
      tests, `Sort<T>` runtime/lowering/diagnostic tests, borrowed owned-text
      dictionary/set lookup tests, typed interner ID nominal-mismatch tests,
      interner borrowed lookup tests, and reverse-copy runtime tests have
      landed; deterministic artifact-output coverage remains.

## 9. Book And Reference Work

- [ ] Document the generic collection contract rule in the user-facing
      collections reference.
- [ ] Document exact ordinal text equality/hash/order semantics for language and
      compiler keys.
- [ ] Document interning as a compiler architecture pattern, not as hidden
      behavior of ordinary text values.
