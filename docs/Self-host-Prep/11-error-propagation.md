# Phase 11 - Error-Value Propagation (`try` + `from`)

Status: **implemented (v2) — `[Ok]`/`[Err]` role attributes.** The v1 implementation
(EP01-EP10, EP13) landed `try` + `from` end-to-end (grammar, type-checking, MIR
lowering, native codegen, drop-correctness, diagnostics STK3037-STK3041, runtime +
diagnostic tests, LLVM fold checks), **but recognized propagatable types by type
name** ("Result"/"Option" + arity). Design review with Alexander rejected that: a
language feature must not privilege a stdlib type or a naming convention. v2 replaced
recognition with **`[Ok]`/`[Err]` variant role attributes** (§3, §4.2): any
two-variant enum that marks one success and one failure variant is propagatable —
user or stdlib, any names, no compiler-privileged types.

The v2 rework (EP16-EP20, §7 Phase F) is **complete**: variant attribute lists in the
grammar, the `EnumVariantRole` model + role/misuse diagnostics (STK3042/STK3043) with
roles and funnels threaded through generic monomorphization, role-based recognizers in
typing and lowering (name-based recognizers deleted), stdlib annotation + rebuilt
dist package, diagnostics/runtime tests (including cross-family stdlib→user
propagation and drop correctness through funnels), and user-facing docs.

**What stood from v1 (no rework was needed):** the `try` keyword + grammar + position
rule, the `from` funnel declaration syntax and its conversion semantics, the
drop-on-return lowering, and the diagnostics framework.

**EP15 (cross-module) is also complete:** the package image serializes the `[Ok]`/`[Err]`
roles and `from` funnels on every published enum variant (source-surface, typed-interface,
and compiler-facts sections), the loader restores them (and generic instantiation threads
them), and exported generic templates whose bodies contain `try` republish per-`try`
propagation facts (ordinal-keyed) that downstream MIR lowering consumes without
re-type-checking. `try` therefore works against packaged dependencies — including the
dist stdlib package — exactly as it does against source imports. **Still open:** EP11/EP12
(proposed funnel overrides — need OQ-EP5), and a typed-template-body representation of
`try` (today a `try`-containing template publishes its body as source text plus the
republished facts; the structured typed-body lowerer does not yet have a `try` case).

This phase closes `L01` (error-value propagation) from `01-language-feature-gaps.md`
and the "Error And Optional Values" section of `08-stark-feature-roadmap.md`. The
`Result<T, E>` and `Option<T>` enums in `System` (`stdlib/src/System.stark`) are
**ordinary annotated enums** under this design — recommended general-purpose types,
with no language privilege.

The guiding constraint is Stark's control-flow-transparency principle: errors are
values, returned and never thrown, and **no control flow is hidden**. `try` is the
*disclosure* — a visible, greppable early-return point — which is precisely why
Stark does not adopt Rust's trailing `?` (easy to skim past, hides the return).

## 1. Goal

The host compiler has thousands of fallible calls (`src/Compiler/CompilerPipeline.cs`,
`NativeToolchain.cs`, `CompilerCli.cs`, parser/package helpers) expressed with
`throw`/`try`/`catch`, nullable returns, and `TryGet(out ...)`. Stark has no
exceptions by design. Without a propagation construct, every fallible call in the
ported compiler expands into a nested `switch`, and the code drowns in boilerplate
(the L01 audit finding). This phase delivers:

1. `try` — a visible early-return propagation operator over `Result<T, E>` (and
   `Option<T>`),
2. `from` — terse, intentional, upfront declaration of how a composite error
   absorbs its causes, so cross-layer `try` needs no per-call conversion clause.

## 2. Current State (verified)

- `Result<T, E>` (`Ok(T)`/`Err(E)`) and `Option<T>` (`Some(T)`/`None`) are ordinary
  generic enums in `System` (`stdlib/src/System.stark`), not compiler-privileged
  forms — consistent with `LanguageReference.md` §8.4 ("Standard library types such
  as `Option<T>` or `Result<T, E>`, when provided, are ordinary enums").
- The stdlib already carries many layer-specific result enums in the same shape:
  `System.IO.IOResult<T>`/`IOStatus`/`IOError`, `System.Memory.MemoryResult<T>`/
  `MemoryStatus`, `System.Net.NetResult<T>`, `System.Threading.ThreadJoinResult`,
  etc. These are handled today with explicit `switch` (e.g.
  `stdlib/src/System/FileSystem.stark:323` and the nested early-returns in
  `OpenDirectoryWithPlatform`), which is exactly the verbosity `try` removes.
- There is **no** `try`/`catch`/`throw` in the grammar; `try` and `from` are free
  identifiers (a repo-wide scan finds `from` only inside comments and `try` not at
  all), so both can be taken as keywords with no source churn.
- The trait system (doc `10`) dispatches on a **receiver** (`self.Method()`) and via
  `where T: Trait` bounds. The grammar already parses `static` (no-self) trait
  methods (`functionModifier*` includes `STATIC`), but **target-directed** resolution
  — "find `E : FromError<F>` keyed on the *target* error type `E`" — is not built.
  This matters for the conversion-representation choice in §4.3.

## 3. Accepted Design Decisions

Settled with Alexander in design discussion:

- **Propagation construct: `try`, leading keyword.** `stack ascii text = try ReadFile(path);`
  The early-return is visible at the call. The keyword `try` is kept (Zig/Swift
  precedent); Stark has no exceptions, so it carries no `catch` baggage.
- **No trailing sigil.** Rust's `?` is rejected: a divert point must be greppable,
  not a skimmable suffix.
- **Conversion lives off the call site.** The per-call `maperr (E e) => Variant(e)`
  clause is rejected as being at the wrong altitude (it re-declares the conversion at
  every call). The conversion's home is the **error type**, declared once.
- **`from`-on-variant is the conversion surface.** A composite error marks each
  absorbing variant with `from`:

  ```stark
  enum LoadError
  {
      Io      from IoError,        // variant Io carries an IoError AND is the canonical funnel for IoError
      Parse   from ParseError,
      Resolve from ResolveError,
  }
  ```

  `Io from IoError` is net-zero lines over the `Io(IoError)` you would write anyway,
  yet it also declares the funnel. Absorption is **opt-in and explicit** (you wrote
  `from`), declared **once** on the type, and **upfront** (in the error tree's own
  declaration). It is never inferred from enum shape and never silent.
- **From-on-target, not Into-on-source.** The composite (`LoadError`) owns all its
  conversions, so the whole error tree's shape and absorption rules live in one
  place. (Into-on-source would scatter them across the leaf errors.)

Added in the v2 design review:

- **Recognition is by `[Ok]`/`[Err]` variant role attributes — never by type name
  and never by stdlib identity.** A propagatable enum marks exactly one variant
  `[Ok]` (the success role: `try` unwraps it) and exactly one variant `[Err]` (the
  failure role: `try` propagates it). Any two-variant enum qualifies — user-defined
  or stdlib, any type name, any variant names, any module:

  ```stark
  enum ParseOutcome<T>
  {
      [Ok]  Found(T),
      [Err] Failed(ParseError),
  }

  enum Lookup<T>
  {
      [Ok]  Hit(T),
      [Err] Miss,            // failure with no payload (the Option/None shape)
  }

  enum Result<T, E> { [Ok] Ok(T), [Err] Err(E) }   // the stdlib types are just
  enum Option<T>    { [Ok] Some(T), [Err] None }   // ordinary annotated enums
  ```

  This rejects the v1 name-based recognition (which accepted any enum *named*
  `Result`/`Option` and rejected structurally identical enums with other names —
  including the stdlib's own `IOResult`). The role model also unifies
  Result/Option/Status into one mechanism: the roles are the contract, payload
  arity distinguishes the shapes.
- **Attributes describe roles; syntax defines data.** `[Ok]`/`[Err]` are
  compiler-recognized ("innate") attributes on the existing attribute surface
  (the same surface as `[Fact]`). `from` is **not** an annotation and does not
  migrate to one: `Io from IoError` is declaration syntax that defines the
  variant's payload *and* its funnel in one stroke. The two mechanisms are
  deliberately different because they are different kinds of facts.
- **`try` semantics under roles** (Alexander's formulation): unwrap the `[Ok]`
  variant; propagate the `[Err]` variant; if the error payload type differs from
  the enclosing function's `[Err]` payload type, convert through the enclosing
  error type's `from` funnel; if there is no valid funnel, compile-time error.
- **Innate attributes are strict.** Once attributes are load-bearing, an
  unrecognized attribute (e.g. `[Okk]`) is a compile error, not inert metadata —
  a typo must not silently produce a non-propagatable enum. (Final policy
  wording in OQ-EP7.)

## 4. v1 Surface Design

### 4.1 The canonical shape

```stark
enum LoadError
{
    Io      from IoError,
    Parse   from ParseError,
    Resolve from ResolveError,
}

fn System.Result<Module, LoadError> LoadModule(ascii path)
{
    stack ascii  text = try ReadFile(path);    // IoError      -> LoadError.Io
    stack Ast    ast  = try Parse(text);       // ParseError   -> LoadError.Parse
    stack Module mod  = try Resolve(ast);      // ResolveError -> LoadError.Resolve
    return System.Result<Module, LoadError>.Ok(mod);
}
```

Each `try` is a visible early-return; each conversion was declared once, upfront, on
`LoadError`. The body is flat and linear.

### 4.2 `try` semantics (v2 — role-based)

A type is **propagatable** when it is an enum with exactly two variants, one marked
`[Ok]` and one marked `[Err]`. Each role variant carries zero or one payload.

`try expr` requires:

1. **the operand is propagatable** — `expr`'s type has `[Ok]`/`[Err]` roles;
2. **the enclosing function's return type is propagatable** — `try` must be able to
   construct that type's `[Err]` variant for the early return;
3. **the failure payloads are connected** —
   - operand `[Err]` payload type `F`, enclosing `[Err]` payload type `E`: either
     `F == E` (propagate unchanged), or `E`'s enum declares a `from F` funnel (§4.3)
     and the payload is converted through it; **no funnel → compile error**;
   - operand `[Err]` with **no payload** propagates only into an enclosing `[Err]`
     with **no payload** (the Option/None shape); payload/no-payload mixing is
     rejected — an error value is never silently discarded and never invented.

Then:

- **on the `[Ok]` variant:** `try expr` evaluates to that variant's payload (or to
  nothing when the `[Ok]` variant is unit — only meaningful as an expression
  statement), and execution continues;
- **on the `[Err]` variant:** the enclosing function early-returns its own `[Err]`
  variant wrapping the (possibly funnel-converted) failure payload.

The success payloads of the operand and the enclosing function are deliberately
unrelated; only the failure path ties the two signatures together.

The early-return reuses the existing **drop-on-return** machinery: all live locals
that require drop at the `try` point are dropped before the function returns its
`[Err]`, exactly as a written `return` would.

Lowering of one `try` is the desugaring the audit feared writing by hand (shown
here with the stdlib's annotated `Result`, but identical for any propagatable enum):

```stark
// stack U name = try expr;   lowers to:
stack U name;
switch (expr)
{
    case Result.Ok(var v):  name = v;                                  // continue
    case Result.Err(var f): return Result<_, E>.Err(convert(f));       // drop live locals, then return
}
```

### 4.3 How `from` carries the conversion (and the `FromError` trait question)

`from` records, in the type model, that variant `Io` **absorbs** source type
`IoError`. `try`'s cross-layer case looks up that record and wraps. Two
implementation strategies, and a deliberate v1 choice:

- **(S2) Marker-backed — the v1 choice.** `from` is tracked as compiler metadata on
  the enum (source type → absorbing variant). `try` synthesizes the wrap directly. No
  user-visible conversion trait is required. This is the smallest correct path and
  does **not** require generalizing the trait system to target-directed resolution.
- **(S1) Trait-backed — deferred.** A stdlib `trait FromError<E> { static finite law Self FromError(E cause); }`,
  with `from` desugaring to a generated impl and `try` resolving `E : FromError<F>`.
  This is more "library mechanism over compiler magic" and would let generic code
  bound `where T: FromError<E>`, but it requires **static (associated) trait members
  returning `Self`** *and* **target-directed (by-return-type) resolution**, neither of
  which the doc `10` trait system implements yet.

**Answer to "do we need the verbose `FromError<E>` trait + base list + `finite law`
bodies?": no — `from` subsumes it as the everyday surface.** The hand-written form

```stark
enum LoadError : FromError<IoError>, FromError<ParseError>, FromError<ResolveError>
{
    Io(IoError), Parse(ParseError), Resolve(ResolveError),
    finite law LoadError FromError(IoError cause)    { return LoadError.Io(cause); }
    finite law LoadError FromError(ParseError cause) { return LoadError.Parse(cause); }
    finite law LoadError FromError(ResolveError cause){ return LoadError.Resolve(cause); }
}
```

declares exactly what `Io from IoError` declares, in a base-list entry **plus** a law
body **per** variant. `from` collapses both into one token. The `FromError` trait is
retained only as an **optional future** (tracked as OQ-EP1): surface it if and when
(a) generic algorithms need a `where T: FromError<E>` bound, or (b) someone wants a
*non-wrap* type-level conversion (add context, pick a variant by inspecting the
cause) to live on the type as a trait impl rather than as the override forms in §4.5.
Until then, non-wrap conversions use those overrides, and `try` resolves wraps from
`from`-markers directly.

### 4.4 `Option<T>` and `Status` shapes (subsumed by roles in v2)

Under the role model, `Option<T>` is not a special case: it is simply a propagatable
enum whose `[Err]` variant (`None`) carries no payload. Likewise the stdlib's status
enums (`IOStatus`, `MemoryStatus`, ...) are propagatable enums whose `[Ok]` variant
is unit. All of the following fall out of the §4.2 rules with no extra machinery:

- `Option`-shaped (`[Ok] Some(T)`, `[Err] None`): `try` yields `T`; `None`
  propagates into the enclosing no-payload `[Err]`. Covers the host's
  `TryGet(out value)` / nullable-return patterns (L11).
- `Status`-shaped (`[Ok] Ok`, `[Err] Err(E)`): `try status();` as an expression
  statement propagates the failure and yields nothing on success.
- Payload/no-payload `[Err]` mixing stays rejected (§4.2): converting a `None` into
  an error value, or discarding an error value into a `None`, is always explicit.

### 4.5 Overrides (proposed; not yet locked)

For the cases `from` does not cover, a gradient of more-local overrides — each
**proposed**, to be finalized in Phase D:

- **Function-local funnel** (override the type's default, or put the mapping under the
  reader's eye), reusing the existing `where` slot:

  ```stark
  fn System.Result<Module, LoadError> LoadModule(ascii path)
      where IoError => LoadError.Io, ParseError => LoadError.Parse, ResolveError => LoadError.Resolve
  { ... }
  ```

- **Site wrap** — one deviating call, no clause: `stack Ast ast = try Parse(text) as LoadError.Parse;`
- **Site transform** — a genuine (non-wrap) conversion only:
  `try Parse(text) maperr (ParseError e) => LoadError.ParseAt(e, path);`

Conversion-explicitness gradient (pay ceremony only where the mapping is not obvious):

| Where the conversion lives | Spelling | Use when | Status |
|---|---|---|---|
| Error type (default) | `Io from IoError` | the canonical absorption, once per error tree | **locked** |
| Function signature | `where IoError => LoadError.Io` | override the default / show it in the signature | proposed |
| Call site | `try expr as LoadError.Io` | a single deviating call | proposed |
| Call site | `try expr maperr (E e) => …` | a real transform (add context / pick by value) | proposed |

### 4.6 `try` position (control-flow visibility)

To keep the divert greppable and never buried inside a sub-expression, v1 restricts
`try` to positions where its early-return sits at a statement boundary: the **full
initializer of a binding**, the **operand of `return`**, or a **bare expression
statement**. `Foo(try a(), try b())` (two returns hidden in an argument list) is
rejected. This is stricter than Zig/Swift and follows Stark's "no hidden control
flow." (Relaxable later; tracked as OQ-EP2.)

## 5. Grammar Deltas

Surgical additions to `Stark.g4` (parser regen required: `antlr4` 4.13.2; CI checks
generated files). MVP = the `try` expression + the `from` variant payload.

```
// lexer (collision-free per repo scan; reserved keywords)
TRY  : 'try';
FROM : 'from';

// try as a leading prefix at unary precedence (binds the whole postfix chain:
// `try Parse(text)` = try of the call). Position is constrained semantically (§4.6),
// not grammatically, in v1.
unaryExpression
    : powerExpression
    | INIT unaryExpression
    | LPAREN conversionType RPAREN unaryExpression
    | unaryOperator unaryExpression
    | TRY unaryExpression            // NEW
    ;

// from-on-variant: a single-payload absorbing variant. `Io from IoError`.
enumVariantPayload
    : LPAREN (type_ (COMMA type_)*)? COMMA? RPAREN
    | LBRACE (enumVariantFieldDeclaration (COMMA enumVariantFieldDeclaration)* COMMA?)? RBRACE
    | FROM type_                     // NEW
    ;
```

Phase D (overrides) would add, when locked:

```
// function-local funnel: where <SourceErrorType> => <EnumCaseTarget>
errorFunnelClause : WHERE errorFunnel (COMMA errorFunnel)* COMMA? ;
errorFunnel       : type_ ARROW enumCaseTarget ;   // disambiguated from `where T:` by ARROW vs COLON
// site overrides on a try expression: `try expr as <case>` / `try expr maperr (<param>) => <expr>`
```

Notes:

- `Result`/`Option` need no grammar (already enums).
- `from` and `try` are safe as reserved keywords (no identifier collisions); a
  contextual-keyword treatment is possible but unnecessary.
- The `=>` in the Phase-D funnel is the existing `ARROW` token (lambda arrow),
  matching how `maperr` reuses it.

## 6. Lowering

- `try` lowers to a tag `switch` on the `Result`/`Option`: the Ok/Some arm binds the
  payload and falls through; the Err/None arm performs the conversion (§4.3) and a
  structural early-return, going through the **same drop-on-return path** a written
  `return` uses (so all live drop-required locals are released).
- The conversion in the cross-layer arm is a zero-cost wrap: construct the
  `from`-declared variant around the cause. No allocation, no dispatch.
- **Effect preservation:** `try` introduces only a `switch` and a `return`, both
  already legal in `finite`/`law` bodies, so a `try` in a `finite` function keeps its
  termination obligation and a `try` in a `law` function stays pure. The conversion
  wrap is a `finite law` operation. Dynamic dispatch is never introduced.

## 7. Work Breakdown (EP*)

TDD-first, in dependency order, mirroring doc `10`. IDs are stable across docs.

### Phase A - Substrate

| ID | Item | Status | Notes |
|---|---|---|---|
| EP01 | `Result<T, E>` + `Option<T>` in `System` | **done** | added to `stdlib/src/System.stark`; ordinary generic enums |
| EP02 | Decide conversion representation (S2 marker-backed vs S1 trait-backed) | **done (lean)** | v1 = S2 marker-backed; `FromError` trait deferred (OQ-EP1) |

### Phase B - `try` propagation (the L01 mechanism)

| ID | Item | Status | Notes |
|---|---|---|---|
| EP03 | Grammar: `TRY` token + `try` unary expr; regen parser | **done** | `Stark.g4` `unaryExpression : … \| TRY unaryExpression`; `TRY`/`FROM` reserved (no in-tree identifier collisions); `antlr4` 4.13.2 regen |
| EP04 | Typing: enclosing fn returns a propagatable enum; `try` unwraps the `[Ok]` payload and types the value; position rule; misuse diagnostics | **done (v2)** | `EvaluateTryExpression` + `TryResolvePropagationRoles`; STK3037/3038/3039; the position rule, `_currentFunctionReturnType`, and the diagnostics framework carried over from v1; recognizers are role-based (EP18) |
| EP05 | Lowering: tag test → `[Ok]` unwrap, `[Err]` early-return via drop-on-return | **done (v2)** | `LowerTryPropagation`/`BuildTryErrorReturnValue` use the role-variant names recorded by typing (no hard-coded names); drop-on-return reuse, operand consumed like a `switch … var` capture |
| EP06 | Tests: passthrough, drop-correctness, unit-failure propagation, effect preservation | **done (v2)** | `TryPropagationRuntimeTests` rewritten against role-annotated enums (non-conventional names, exits 17/123/3 + stdlib cross-family 67) |

### Phase C - `from` conversion surface

| ID | Item | Status | Notes |
|---|---|---|---|
| EP07 | Grammar: `Ident from type_` enum-variant payload; regen | **done** | `enumVariantPayload : … \| FROM type_`; lowers to a single positional-field variant + an `AbsorbsErrorType` marker on `EnumVariantSymbol` |
| EP08 | Model + diagnostics: source-type → absorbing-variant; reject duplicate/ambiguous `from` | **done** (in-module) | `BuildEnumNamedType` reads `from`; STK3040 (two `from` for one source type). **Cross-module package-image carry of the marker is EP15.** |
| EP09 | Conversion in `try`: cross-family wraps via the `from`-variant; STK error if absent/ambiguous | **done (v2)** | funnel resolution (`ResolveErrorFunnelVariant`, STK3041, nested `LowerDirectTagEnumConstructor`) carried over from v1; its inputs (operand/enclosing failure payload types) come from the `[Err]` role variants (EP18) |
| EP10 | Tests: from-wrap, missing-funnel, ambiguous-funnel | **done (v2)** | `TryPropagationDiagnosticsTests` rewritten against role-annotated enums; cross-family accept + STK3040/STK3041 rejections |

### Phase D - Overrides (proposed; finalize spellings first)

| ID | Item | Status | Notes |
|---|---|---|---|
| EP11 | Function-local `where Source => Variant` funnel (override type default) | not started | depends OQ-EP5; not needed for the core feature |
| EP12 | Site overrides: `try expr as Variant` / `maperr (E e) => …` | not started | depends OQ-EP5 |

### Phase E - Close-out

| ID | Item | Status | Notes |
|---|---|---|---|
| EP13 | User-facing docs: `LanguageReference.md` §10.5 + `skills/stark-language/SKILL.md` + book Ch17/Ch13/appendices | **done (v2)** | all describe the role model: LanguageReference §10.5 + §8.4, SKILL + diagnostics-guide + syntax-quick-reference, book Ch17 (prose + sample shows a user propagatable enum propagating cross-family into `System.Result`), appendices H/I, changes.md |
| EP14 | Gap-doc sync: flip `L01`, cross-link `S01`, resolve `OQ-04` (note `OQ-06`) | **done (v2)** | doc `01` L01 entry + ROADMAP status/counts synced to the implemented v2 model |
| EP15 | Cross-module: serialize the `from` `AbsorbsErrorType` marker **and the `[Ok]`/`[Err]` role markers** in the package image; republish `try` facts inside *exported* generic templates | **done** | roles + funnels serialized on enum variants in all three package-image sections (source-surface text form, typed-interface/compiler-facts type-reference form) and restored by the loader; `EnsureMonomorphizedType` is called at `try` typing so imported generic instantiations (and their funnels) materialize on demand; exported templates with `try` bodies publish ordinal-keyed `TryPropagations` facts that the consumer's MIR lowering reads (`ResolveImportedTemplateTryPropagation`) instead of re-type-checking; bridge synthetic source re-renders `[Ok]`/`[Err]`/`from`; verified against the dist stdlib package. Follow-up (not blocking): a typed-template-body `try` expression kind so `try`-containing templates can publish structured bodies instead of body text |

### Phase F - v2: `[Ok]`/`[Err]` role attributes (replaces name-based recognition)

| ID | Item | Status | Notes |
|---|---|---|---|
| EP16 | Grammar: attribute lists on enum variants (`enumVariantDeclaration : attributeList* Identifier enumVariantPayload?`); regen parser | **done** | the attribute surface (`[...]`) already existed for declarations and members; extended to variants. No new keywords |
| EP17 | Innate-attribute model: recognize `[Ok]`/`[Err]` on variants into the type model (role on `EnumVariantSymbol`); diagnostics — exactly one `[Ok]` + exactly one `[Err]`, exactly two variants, role payload arity (0 or 1), duplicate/conflicting roles, and **unknown attribute = compile error** | **done** | `EnumVariantRole` + `ResolveEnumVariantRole` (STK3042) + `ValidateEnumPropagationRoles` (STK3043); roles **and** `from` funnels threaded through generic monomorphization (`CreateConcreteEnum`); compiler-attribute registry established (policy per OQ-EP7) |
| EP18 | Rework recognizers to roles: typing (`EvaluateTryExpression` consults roles, failure payload types come from the `[Err]` variants) + lowering (`LowerTryPropagation` uses the recorded role-variant names); delete the name-based `TryGetResultShape`/`TryGetOptionShape`/`IsBlessedPropagationType` | **done** | `TryResolvePropagationRoles` in typing; `TryPropagationTypingRecord` carries role-variant names + payload types; lowering reads the record; name-based recognizers deleted; position rule, drop-on-return lowering, funnel resolution, and diagnostics framework carried over unchanged |
| EP19 | Stdlib annotation: add `[Ok]`/`[Err]` to `System.Result`/`System.Option` and the existing result/status enums (`IOResult`, `IOStatus`, `MemoryResult`, `MemoryStatus`, `NetResult`, `NetStatus`, `ThreadStatus`, `ThreadJoinResult`) ; rebuild `dist/libSystem.starkpkg.json` | **done** | 10 stdlib enums annotated; the whole existing stdlib error surface is propagatable; `DirectoryReadResult`/`DirectoryReadInfoResult` (3 variants) and `TestStatus` deliberately stay unannotated; dist rebuilt via `build-stdlib.sh` |
| EP20 | Rework tests + user-facing docs to the role model: `TryPropagationDiagnosticsTests`/`TryPropagationRuntimeTests`, role-misuse diagnostics tests, book samples, `LanguageReference.md` §10.5, `SKILL.md`, book Ch17/Ch13/appendices | **done** | 15 diagnostics tests (non-conventional variant names; STK3037-3043) + 4 runtime tests incl. cross-family `try` of a stdlib `IOResult` operand inside a user `AppResult` function (compiled with `-I stdlib/src`) and drop correctness through funnels; book Ch17 sample now declares its own propagatable `ParseOutcome` enum and propagates it into `System.Result` |

## 8. Open Questions

| ID | Question | Options / lean |
|---|---|---|
| OQ-EP1 | Surface a real `FromError<E>` trait? | (S1) trait-backed — needs static + target-directed resolution; enables `where T: FromError<E>` + type-level non-wrap impls. (S2) marker-backed — v1/v2. **Lean S2 now; revisit S1 if generic conversion bounds are needed.** |
| OQ-EP2 | `try` position | **Resolved (v1, carries into v2):** restricted to binding-RHS / assignment-RHS / `return` operand / expr-stmt (§4.6). |
| OQ-EP3 | `Option` ↔ `Result` mixing under one `try` | **Resolved by the v2 role model:** payload/no-payload `[Err]` mixing is rejected (§4.2/§4.4); errors are never discarded into `None` and never invented from it. |
| OQ-EP4 | `from` / `try` keyword reservation | reserved vs contextual. **Lean reserved** (no identifier collisions in-tree). Note: `[Ok]`/`[Err]` add **no** keywords — they are attributes. |
| OQ-EP5 | Final override spellings | `where … =>` funnel, site `as`, site `maperr` (Phase D). Confirm before EP11/EP12. |
| OQ-EP6 | Keep `maperr` at all? | `from` + `where`-funnel may suffice; keep a site transform escape vs drop it. **Lean keep** a minimal transform escape. |
| OQ-EP7 | Innate-attribute policy | **Resolved for enum variants (v2 implementation, lean adopted):** a fixed compiler registry; on enum variants only `[Ok]`/`[Err]` are recognized, and an unrecognized or malformed variant attribute is a compile error (STK3042), so `[Okk]` cannot silently produce a non-propagatable enum. Whether user-defined (tooling-only) attributes get an escape namespace, and the registry policy for non-variant positions, stays open for future attribute work (`[NoReturn]`, L07). |

## 9. Relationship to Existing Docs

- `01-language-feature-gaps.md` **L01** — this phase closes it; also touches **L11**
  (optional-value conventions, via `Option` + `try`).
- `08-stark-feature-roadmap.md` "Error And Optional Values" — implements the
  `Option`/`Result` conventions and the propagation-operator decision; the
  compiler-invariant failure path (`panic`/`trap`) stays **L07**, separate.
- `09-self-hosted-compiler-architecture.md` "Error Model" — recoverable failures as
  values is exactly what `Result` + `try` make ergonomic across the ported pipeline.
- `02-stdlib-gaps.md` **S01** — shared `Option`/`Result` conventions; EP01 lands the
  types, this phase makes them usable.
- `07-open-questions.md` **OQ-04** (propagation surface) resolved here as leading
  `try` + `from` over structural `[Ok]`/`[Err]` enums; no `?`-style propagation.
- `07-open-questions.md` **OQ-06** (optional/null replacement) is resolved as:
  C# nullable values and `TryGet(... out T?)` port to the blessed
  `System.Option<T>` convention (`[Ok] Some(T)` / `[Err] None`).
- `10-traits-and-dynamic-dispatch.md` — the deferred S1 (trait-backed conversion)
  would build on its conformance machinery, extended with target-directed resolution.
