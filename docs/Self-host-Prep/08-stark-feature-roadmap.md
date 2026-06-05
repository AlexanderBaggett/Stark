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

Useful self-hosting targets:

- `Dictionary<K, V>` keys for symbols and interned-name structs via explicit
  static `Hash`/`Equals`; `ascii`/`unicode` helpers still need stdlib contracts.
- Generic equality and ordering for deterministic compiler output via
  `System.Collections.Eq` and `System.Collections.Ord`.
- Generic hashing via `System.Collections.Hash` with associated `Code`.
- Generic formatting via `System.Collections.Format` with required associated
  `Writer`.
- Reusable collection algorithms without a runtime dispatch layer.

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
- [ ] Decide whether list and property patterns are needed before self-hosting.

Notes:

Or-patterns are section-local alternatives: `case A | B when guard:` tests
`A`, then `B`, and either successful alternative flows into the same guarded
body. Capture-bearing alternatives must agree on names and types so the body's
locals are definitely initialized regardless of which alternative matched.

Range patterns are inclusive and integer-only. Type checking rejects empty or
non-overlapping ranges, coverage uses intervals rather than value expansion,
and lowering emits equality, one-sided, or two-sided comparisons depending on
the target type bounds.

## Error And Optional Values

- [ ] Define shared `Option<T>` and `Result<T, E>` conventions.
- [ ] Decide whether Stark gets a `?`-style propagation operator.
- [ ] Add a compiler-invariant failure API for explicit trap/abort paths.

Self-hosting needs a replacement for C# nullability, exceptions, and
`TryGet(... out value)` patterns. Recoverable failures should remain values.
Internal compiler bugs should have an explicit, documented failure path.

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

- [ ] String-key dictionaries for `ascii` and `unicode`.
- [ ] `HashSet<T>`.
- [ ] Symbol interning.
- [ ] Deterministic sorting and ordered set/map helpers.
- [ ] Text builder and formatting APIs.
- [ ] JSON support for `.starkpkg.json`, unless the package image format changes.
- [ ] TOML support for `Stark.toml` and `Stark.solution.toml`.
- [ ] File read-all/write-all helpers.
- [ ] Temp directory helpers.
- [ ] Process spawn with stdout/stderr capture.
- [ ] Environment and argv APIs.

## Memory Model Work

- [ ] Decide compiler IR storage strategy: arena plus handles, explicit shared
      ownership, or owned trees plus cross-reference tables.
- [ ] Keep `arena` explicit if it becomes an executable storage class.
- [ ] Keep unsafe shared ownership visible in source.

Likely direction for the compiler: arena or owned collections with stable
handles, not a hidden garbage-collected object graph.
