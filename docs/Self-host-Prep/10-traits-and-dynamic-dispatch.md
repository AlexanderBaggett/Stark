# Phase 10 - Traits, Default Members, and Dynamic Dispatch

Status: WIP. **Static trait support is working end-to-end.** Landed: the
`baseTraitList` grammar (TD01); the `Self` type; trait base lists on the type
model (TD02/TD03); same-module conformance with exact signature matching
(TD04 — STK3026/3032/3033); and **constrained generics / generic trait dispatch**
(TD05/TD06, CG01–CG07) — `where T: Trait` bounds are captured, enforced at call
sites (STK3034), trait methods are callable on bounded type parameters, and they
lower to **direct concrete calls** (no vtable); and **default members** (Phase B,
TD08–TD11) — a not-overridden default dispatches to the default body monomorphized
over `Self` (direct call) for both concrete and `where T: Trait` receivers, while
overrides win. Remaining: cross-module conformance (TD04), `dyn` objects
(Phase C), visible vtable (Phase D). This document tracks the work to make traits usable in Stark
and to add an explicit dynamic-dispatch surface, in service of self-hosting
(see `08-stark-feature-roadmap.md` and `09-self-hosted-compiler-architecture.md`).

The guiding constraint is Stark's cost-transparency principle: no hidden
allocation, no hidden dispatch. Every dispatch cost must be visible in source.

## 1. Goal

Self-hosting needs reusable behavior contracts (hashing, equality, ordering,
formatting, collection algorithms) and a small amount of genuinely open runtime
dispatch (e.g. module resolvers, plugin-like boundaries). Today Stark has the
static half of this (doctrines) but not the trait half. This phase delivers:

1. trait implementation and static conformance/dispatch,
2. default trait members,
3. an explicit dynamic-dispatch surface (`dyn` trait objects),
4. an optional, visible, roll-your-own vtable path.

## 2. Current State (verified)

Investigated against the committed compiler and tests. Doctrines are the
implemented static-reuse mechanism; traits are declaration-only and every *use*
is actively blocked.

| Capability | Declared / parses | Methods callable | Conformance / impl | Dispatch | Evidence |
|---|---|---|---|---|---|
| **Doctrine** | yes | yes (`Name.Method`, `Name<T>.Method`) | n/a (concrete law bundle) | static, by-name, monomorphized | `tests/compiler.FeatureTests/TraitsAndDoctrinesFeatureTests.cs` (`DoctrineLawCallsStayDirectAndPreserveBorrowFacts`, `GenericDoctrineMethodsPreserveStaticFiniteLawBorrowContracts`) |
| **Trait** | yes | **no** | **none** | **none** | see below |

Trait findings:

- Trait methods cannot be called: `STK3013 "Trait method '...' cannot be called
  directly"` (`tests/compiler.Tests/TypeTypingDiagnosticsTests.cs:1809`).
- A trait cannot be a value or a usable type: it resolves to
  `StarkTypeSymbols.Error` (`src/Compiler/SemanticValidation.cs:3500-3502`),
  in contrast to the doctrine branch four lines up (`:3496`). Tests assert
  `STK3013 "Cannot create an instance of compile-time-only trait"` and
  "no runtime dispatch values for traits or doctrines"
  (`TypeTypingDiagnosticsTests.cs:1785-1786`).
- No conformance machinery exists in `src/Compiler/TypeChecking.cs`.
- No implementation syntax exists: the grammar has no base list and no `impl`
  token (`Stark.g4` `structDeclaration`, `recordDeclaration`); traits connect to
  types only as generic bounds via `where T: Trait` (`Stark.g4:121-123`), and
  there is no evidence that bound is checked or that it enables calling trait
  methods on `T`.
- A trait declaration emits no code
  (`TraitContractsDoNotEmitRuntimeDispatchSurface`).
- The one "trait-like" feature that works is special-cased and is actually a
  doctrine: `Dictionary<K,V>` keys go through `doctrine DictionaryKey<T>`
  hardwired to **bool/integer key types only**
  (`src/Compiler/DefaultCompilerPipeline.cs:729-754`, bool/integer gate at
  `:747`; `DictionaryKey.Equals`/`.Hash` builtins in
  `src/Compiler/SsaIrValidation.cs:542-646`).
- `stdlib/src/System/Collections.stark:5-21` *declares* `trait Equatable<T>` and
  `trait Hashable<T>`, but nothing implements them and nothing calls through
  them.

Consequence: "only default trait members are missing" understates the gap.
Default members presuppose a way to implement a trait on a type and call trait
methods, and neither exists today. The build order therefore starts with trait
conformance, not with default members.

This corresponds to gap `L06` in `01-language-feature-gaps.md`.

## 3. Accepted Design Decisions

From the design discussion:

- **Dispatch shape:** `dyn Trait` with a compiler-generated vtable as the
  default, plus an *optional* path to supply your own vtable. The vtable is a
  real, nameable representation, not compiler magic.
- **Call disclosure:** type spelling only. A dynamic call is written
  `r.Method()`; the `dyn` in `r`'s declared type is the disclosure. No call-site
  sigil. This relies on Stark already requiring explicit types on every binding.
- **Implementation syntax:** C#-style base list, `struct X : Trait { ... }`,
  with trait methods written inline in the type body.
- **Receiver spelling (consistency):** receivers stay `<qualifier> <Type> self`
  everywhere, matching existing member methods (`mut borrow File self`). A trait
  method writes `borrow Self self`; the concrete impl writes the concrete type
  (`borrow FileResolver self`). No new bare-`self` shorthand is introduced —
  that would diverge from how Stark already spells receivers. `Self` is the only
  addition, implemented as an implicit type parameter of every trait method.

## 4. v1 Surface Design

### 4.1 Trait implementation (base list)

A concrete `struct`/`record` declares the traits it satisfies with a C#-style
base list and provides the required methods inline:

```stark
trait ModuleResolver
{
    finite ResolveResult Resolve(borrow Self self, ascii moduleName);
}

struct FileResolver : ModuleResolver
{
    Ascii Root;

    finite ResolveResult Resolve(borrow FileResolver self, ascii moduleName)
    {
        // ...
    }
}
```

`self` stays a plain identifier (the grammar has no `self` keyword). Receivers
are spelled `<qualifier> <Type> self` consistently across the language: a trait
method writes `borrow Self self`, where `Self` is the implementing type, exactly
mirroring a concrete method's `borrow Counter self`. The concrete impl writes the
concrete type (`borrow FileResolver self`), byte-for-byte identical to existing
member methods. `Self` is the only new piece — implemented as an implicit type
parameter of every trait method.

### 4.2 Static conformance and dispatch

- A type satisfies a trait when it provides every required method with a
  compatible signature (function kind at least as strong, parameter types,
  memory contracts, and self receiver shape all matching).
- Trait methods become callable on a value of a conforming concrete type and on
  a generic `T` bounded by `where T: Trait`.
- Static dispatch monomorphizes to the concrete implementation per
  instantiation and lowers to **direct calls** with no vtable, exactly like the
  existing doctrine path (`Inspect.Read` -> direct `fastcc`). This is the
  default, zero-indirection tier.

### 4.3 Default trait members

The grammar already permits a trait method body (`functionBody : block | SEMI |
asmFunctionBody`, `Stark.g4:125`, `traitMethodDeclaration` `:256-258`), so this
is semantic-only.

- A trait method with a `{ ... }` body is a default; one with `;` is required.
- A type satisfies the trait by providing all required methods; defaults fill
  the rest. An impl may override a default.
- Defaults may call other trait methods (required or default).
- **Follow Rust's model, not C# DIM:** defaults are fallback bodies resolved
  per impl and monomorphized; they do not become "reachable only through the
  trait." This fits Stark's static-by-default, closed-world specialization.

### 4.4 Dynamic dispatch: `dyn` trait objects

The design mirrors closures, which already spell their storage
(`inline`/`borrow`/`heap closure`).

**Opt in at the trait.** A trait is static-only unless declared `dyn trait`:

```stark
dyn trait ModuleResolver { ... }
```

This is the upfront signal that the trait pays the object-safety tax and can
grow a vtable. `dyn ModuleResolver` on a non-`dyn` trait is an error pointing at
either adding `dyn` or using an enum. Plain `trait` keeps today's static-only
behavior. Doctrines stay static; dynamic dispatch is a trait concept.

**Storage-prefixed trait-object types**, parallel to closures:

```stark
borrow dyn ModuleResolver        // non-owning fat view (data borrow + vtable). No allocation.
mut borrow dyn ModuleResolver    // mutable borrowed fat view
heap dyn ModuleResolver          // owned, heap-boxed trait object. The ONLY allocating form.
```

There is deliberately no bare `dyn Trait`: you cannot form a trait object
without choosing storage, and the storage prefix is what discloses allocation.
`borrow`/`mut borrow` come from the existing outer `typeQualifier*`; `heap` is
the storage prefix on the dyn type itself, exactly like `heap closure`.

**Coercion is implicit only into an explicitly-`dyn`-typed slot.** The `dyn` (or
`heap`) keyword visible at the statement is the disclosure:

```stark
stack FileResolver fr = new FileResolver() { Root = "src" };
borrow dyn ModuleResolver view  = fr;                                   // fat view, no alloc
heap   dyn ModuleResolver owned = heap new FileResolver() { Root = "stdlib" };  // one alloc, dropped at scope exit
```

A `heap dyn` is usable where `borrow dyn` is expected (reborrow).

### 4.5 Visible vtable and roll-your-own

For every `dyn trait T`, the compiler exposes nameable representation types so
the vtable is a real, inspectable thing:

```stark
// the fat pointer value - 2 words, what borrow/heap dyn T are
struct ModuleResolver.Object        // conceptual; spelled by the dyn type
{
    Context: rawptr<i8[min max]>;          // per-value data half
    Vtable:  borrow ModuleResolver.Vtable; // shared table half
}

// the table - one static instance per implementing type, generated from the trait
struct ModuleResolver.Vtable
{
    Resolve: fnptr<finite ResolveResult(rawptr<i8[min max]>, ascii)>;
    Drop:    fnptr<fn void(rawmutptr<i8[min max]>)>;  // used only by owning forms
    Size:  u64[0 max];
    Align: u64[0 max];
}
```

Receiver kind survives erasure: `borrow Self self` -> `rawptr<i8[min max]>` context,
`mut borrow Self self` -> `rawmutptr<i8[min max]>`. The read/write distinction is not
laundered away by going dynamic.

Three ways to touch it, ordered by how much you assert:

1. **Safe, default** - compiler builds the fat pointer and selects the table:
   `borrow dyn ModuleResolver r = fr;`
2. **Unsafe, roll-your-own** - supply context + table (plugins, FFI, runtime
   impls). Placeholder spellings, paralleling the existing unsafe
   `slice(pointer, count)` intrinsic; exact names TBD:
   ```stark
   unsafe
   {
       borrow dyn ModuleResolver r     = dynview(ctx, &customVtable);    // non-owning
       heap   dyn ModuleResolver owned = dynbox(ownedPtr, &customVtable); // owning; Drop/Size/Align must be valid
   }
   ```
3. **Unsafe decompose** - peek at the parts:
   ```stark
   unsafe
   {
       stack rawptr<i8[min max]> data        = r.Context;
       stack borrow ModuleResolver.Vtable vt = r.Vtable;
       stack ResolveResult res = vt.Resolve(data, name);  // manual dispatch if you want it
   }
   ```

The fully manual, no-`dyn` ops-table pattern from
`09-self-hosted-compiler-architecture.md` remains available for zero compiler
involvement. The gradient is: enum + switch (zero indirection) -> safe `dyn` ->
unsafe `dyn`-from-parts -> fully hand-rolled ops table.

### 4.6 Object safety

For a trait to be `dyn` (copy Rust's homework):

- the self receiver must be `borrow Self self` or `mut borrow Self self`
  (dispatchable on the fat pointer); by-value/consuming `self` is not
  object-safe in v1,
- no generic (type-parameterized) methods (a vtable cannot hold infinite slots),
- no by-value `Self` in parameter or return position (size unknown),
- `static` (no-self) members are excluded from the vtable and remain callable
  only through the concrete type.

A non-object-safe trait used as `dyn` is a compile-time diagnostic.

### 4.7 Effect preservation and lowering

The payoff that keeps dynamic dispatch honest: a `dyn` vtable is a table of
`fnptr<kind ...>` slots, and Stark already preserves function kind through
indirect calls (`docs/Internals/LanguageInternals.md` section 8;
`fnptr<law ...>` indirect calls get readonly/purity/`nosync`/`nofree`,
`fnptr<finite ...>` gets `willreturn`/`mustprogress`). Therefore:

- a `law` trait method called through `dyn` is still pure; the optimizer can
  CSE/hoist/reorder it even though it cannot inline it,
- a `finite` method keeps its termination guarantee.

Dynamic dispatch erases the body, never the effect contract. Lowering reuses
existing machinery:

- a `dyn` method call lowers to an indirect `fnptr`-kind call through the vtable
  slot, carrying the kind's call-site attributes,
- the `devirt-ssa` pass (`docs/Internals/CompilerPipeline.md` pass 24) recovers
  a direct, inlinable call when the concrete type is provable (e.g. a
  freshly-formed, non-escaped `dyn`), and `!callees` metadata is emitted for
  closed target sets. So `dyn` degrades gracefully instead of being a hard
  optimization wall.

## 5. Grammar Deltas

Surgical additions to `Stark.g4` (parser regen required: Java + `antlr4`; CI
checks generated files):

```
// lexer
DYN : 'dyn';

// trait opt-in for dynamic dispatch
traitDeclaration : DYN? TRAIT Identifier typeParameterList? traitBody ;

// implementation via base list (records too)
structDeclaration : STRUCT Identifier typeParameterList? baseTraitList? structBody ;
recordDeclaration : RECORD Identifier typeParameterList? primaryConstructorParameters? baseTraitList? recordBody ;
baseTraitList    : COLON type_ (COMMA type_)* ;

// the trait-object type, mirroring closureType exactly
nonArrayType  : dynamicType | rawPointerType | functionPointerType | closureType
              | dynTraitType | integerType | simpleType ;
dynTraitType  : dynStoragePrefix? DYN simpleType ;
dynStoragePrefix : HEAP ;   // borrow / mut borrow come from the outer typeQualifier*, like closures
```

Notes:

- Default members need **no** grammar change.
- `Self` needs no lexer token; it resolves as a type name in trait/impl scope.
- The unsafe roll-your-own from-parts construction (`dynview`/`dynbox`
  placeholders) and the `.Context`/`.Vtable` decomposition spelling are still
  open (section 7).

## 6. Work Breakdown

TDD-first, in dependency order. Each item should be gated by Stark/compiler
tests, mirroring the existing doctrine feature tests (assert direct calls and
the absence of `vtable`/`dispatch` where dispatch must not appear).

### Phase A - Trait conformance and static dispatch (foundation)

| ID | Item | Status | Notes |
|---|---|---|---|
| TD01 | Grammar: add `baseTraitList` to struct/record; regen parser | **done** | landed; `antlr4` 4.13.2 regen; full suite green |
| TD02 | Capture base-trait list on the type model | **done** | `NamedTypeSymbol.ImplementedTraits` populated for source struct/record (`TypeChecking.ResolveBaseTraitNames`); package-image carry for imported-type queries still pending, used later by `dyn` codegen |
| TD03 | type -> implemented-trait edges | **done** | exposed as `NamedTypeSymbol.ImplementedTraits` |
| TD04 | Conformance: required methods, compatible signatures, `Self` receiver | **partial** | `Self` type ✅; base-must-be-trait (**STK3026**) ✅; required-method presence (**STK3032**) ✅; arity + function-kind + **exact parameter/return-type matching** with `Self`/type-arg substitution (**STK3033**, via `SubstituteType` + a structural `TraitTypesEquivalent` comparator) ✅. Pending: cross-module (imported-trait) required-method detection |
| TD05 | Allow trait-method calls on conforming values and `where T: Trait` generics | **done** | concrete-receiver calls already worked; generic dispatch now resolves via `TryResolveTraitBoundMemberCall` at `ApplyMemberAccess` (consults the captured bound, `Self`-substituted). `Trait.Method(...)` via the trait name stays rejected (STK3013), which is correct |
| TD06 | Monomorphization + lowering: static trait calls resolve to the concrete impl as direct calls (no vtable) | **done** | `FunctionMirBuilder.IsTraitMethodTarget` reroutes a recorded trait-method call to the receiver's concrete-type impl; verified end-to-end (runs, returns 10) and LLVM shows `call fastcc @Widget_Width` with **zero** indirect/vtable surface |
| TD07 | Diagnostics tests: conformance success, missing-method failure, signature-mismatch failure | **partial** | reject-inheritance (STK3026), accept-conforming, reject-missing (STK3032), reject-kind-mismatch + reject-parameter-type-mismatch (STK3033) landed in `SemanticValidationTests` (5 tests); cross-module + generic-dispatch tests pending on TD05 |

### Phase B - Default trait members

| ID | Item | Depends | Acceptance |
|---|---|---|---|
| TD08 | Semantics: trait method with body = default; `;` = required | **done** | defaults are not required of implementers; `HasBody` on `TypedFunctionSignature` distinguishes default from abstract |
| TD09 | Override resolution: impl method wins over default | **done** | CG06 reroutes to the concrete override when the type defines one; otherwise the default body (monomorphized over `Self`) is used |
| TD10 | Defaults may call other trait methods | **done** | the implicit `Self: <trait>` bound resolves `self.X()` inside default bodies (CG05); verified direct-call lowering |
| TD11 | Tests: default conformance, default + override dispatch | **done** | `BaseListDoesNotRequireDefaultTraitMethods` + `TraitDefaultMethodsDispatchToMonomorphizedDirectCalls` |

### Phase C - `dyn` trait objects

| ID | Item | Depends | Acceptance |
|---|---|---|---|
| TD12 | Grammar: `DYN` token, `dyn trait`, `dynTraitType`, `dynStoragePrefix`; regen | TD01 | `borrow dyn T` / `heap dyn T` parse; `dyn trait` parses |
| TD13 | Object-safety check + diagnostic for non-`dyn` traits used as `dyn` and non-object-safe `dyn` traits | TD12, TD04 | violations rejected with guidance |
| TD14 | Vtable synthesis: per-(type, trait) static table of `fnptr<kind ...>` slots + Drop/Size/Align | TD06, TD13 | one read-only table per impl |
| TD15 | Coercion: concrete -> `borrow`/`mut borrow`/`heap dyn` into dyn-typed slots; `heap dyn` alloc + drop via vtable | TD14 | coercion type-checks; ownership/drop correct |
| TD16 | Lowering: `dyn` call -> indirect `fnptr`-kind call; preserve finite/law attributes; integrate `devirt-ssa` | TD14 | indirect call carries kind attributes; devirt recovers direct call when concrete |
| TD17 | Tests: dyn call indirect + law/finite attributes preserved; devirt to direct when concrete; `heap dyn` allocates+drops; `borrow dyn` no alloc | TD15-TD16 | feature tests |

### Phase D - Visible vtable / roll-your-own

| ID | Item | Depends | Acceptance |
|---|---|---|---|
| TD18 | Expose nameable `T.Vtable` type and fat-pointer representation | TD14 | `T.Vtable` usable as a type |
| TD19 | Unsafe from-parts construction (`dynview`/`dynbox`, final spelling per OQ) | TD18 | builds `borrow`/`heap dyn` from (context, vtable) under `unsafe` |
| TD20 | Unsafe decomposition `.Context` / `.Vtable` | TD18 | parts readable under `unsafe` |
| TD21 | Tests: round-trip construct/decompose, manual dispatch, FFI/plugin shape | TD19-TD20 | feature tests |

### Cross-cutting

| ID | Item | Depends | Acceptance |
|---|---|---|---|
| TD22 | User-facing docs: `LanguageReference.md` (§6.5 trait bounds, §8.5 impl/`Self`/required+default/static dispatch) + `skills/stark-language/SKILL.md` | **done** | both updated for implemented traits/generics/defaults; examples verified compile+run (`dyn` documented when it lands) |
| TD23 | Update `01-language-feature-gaps.md` L06 and `09-self-hosted-compiler-architecture.md` to reflect the chosen design | A-D | gap docs consistent |
| TD24 | Revisit `Dictionary<K,V>` keys: migrate the special-cased bool/integer `DictionaryKey` doctrine toward general trait-based hashing/equality | TD06 | string/symbol keys expressible; ties to L06/S06 |

## 7. Open Questions

| ID | Question | Options / lean |
|---|---|---|
| TD-OQ1 | Erased-context type in the vtable | raw `rawptr<i8[min max]>` (matches doc 09) vs a typed erased-handle wrapper. Lean raw for v1. |
| TD-OQ2 | Multi-trait method-name collisions in a base list | matching signatures unify into one impl; genuine conflicts error; qualified explicit-impl disambiguation can come later. |
| TD-OQ3 | `heap dyn` allocation-failure path | trap vs `MemoryResult<heap dyn T>`. Lean: match whatever a plain `heap new` local does, for consistency. |
| TD-OQ4 | Consuming dispatch `heap dyn<once>` | defer to v2; mirrors existing `heap closure<once>`. |
| TD-OQ5 | Final from-parts/decompose spelling | `dynview`/`dynbox` + `.Context`/`.Vtable` are placeholders paralleling `slice(...)`. |
| TD-OQ6 | Default-member lowering | monomorphized per implementing type vs a shared thunk. Lean monomorphized, matching doctrine path. |
| TD-OQ7 | Coherence / orphan rules | v1 scope: base list on your own type only. Implementing a foreign trait for a foreign type is out of scope for v1. |

## 8. Relationship to Existing Docs

- `01-language-feature-gaps.md` L06 - this phase closes the trait/conformance
  half of L06.
- `08-stark-feature-roadmap.md` - implements the "compile-time reuse",
  "explicit runtime dispatch (ops tables)", and "closed-world enum" sections as
  one coherent gradient, with `dyn` as the safe middle tier.
- `09-self-hosted-compiler-architecture.md` - the ops-table pattern there is the
  fully-manual end of the gradient; `dyn` is the safe sugar over the same
  representation.
- `docs/Internals/LanguageInternals.md` section 8 and
  `docs/Internals/CompilerPipeline.md` pass 24 - the `fnptr`-kind indirect-call
  attributes and `devirt-ssa` that `dyn` lowering reuses.
