# Language Ergonomics Checklist (short-lived)

Source: Stage 1 lexer port experience, 2026-06-11. This file tracks three
approved features (two language, one test tooling) and five confirmed
compiler bugs, plus the documentation that must change with them. It is a working checklist, not a
design document: delete it once every box is checked and the durable spec
content lives in `docs/Userfacing/LanguageReference.md` and the book.

Decisions already made (do not reopen here):

- `Copyable` is derived structurally, with an optional `[Copyable]` assertion
  attribute for definition-site error locality on public types.
- Initialized-read contracts are spelled as **raw comparison expressions** in
  `where` clauses (not a named `initialized(...)` predicate).
- `raw"""` multiline strings get **full C# raw-string parity**: strip the
  newline after the opening delimiter, strip the newline before the closing
  delimiter, and strip per-line indentation based on the closing delimiter's
  column.
- Test collections UNIFY into the existing `[Collection]` attribute (variadic
  names, one concept), filtering happens at RUNTIME in the generated runner
  (no recompile per filter), and the CLI accepts both repeatable
  `--collection NAME` and comma-separated values.

Some bug rows may be fixed in parallel sessions; this checklist stays the
source of truth — check rows off here regardless of where the fix landed.

## 1. `Copyable` Doctrine

A type is `Copyable` iff reading it out of any place (field, index,
dereference) is byte-copy-safe and creates no drop obligation: scalars,
fnptrs, raw pointers, and slices/views as today; enums whose variants are
unit or carry only `Copyable` fields, with no destructor; structs whose
fields are all `Copyable`, with no destructor; fixed arrays of `Copyable`
elements. `dynamic`, `Owned*`, heap closures, and destructor-bearing types
are never `Copyable`. Reads of `Copyable` values from places are copies, not
moves.

- [x] Extend the ownership-validation move-only classification to recurse
      through enum variants and struct fields instead of classifying every
      enum as move-only. Landed as the shared `CopyabilityFacts` resolver
      (`src/Compiler/CopyabilityFacts.cs`) consulted by ownership
      validation, borrow-liveness validation, AND SSA lowering (SSA move
      classification invalidates the source value, so all three must agree).
      `NamedTypeSymbol` gained `HasDestructor` (populated at type-check from
      struct/record members; derived from the existing `Destructor` manifest
      on package-image load). Builtin owned-text structs (`Ascii`/`Unicode`
      look like {ptr,len,cap} but own their heap) are excluded by name.
      Uninstantiated generic TEMPLATES are non-copyable, but registered
      concrete instantiations classify structurally through their
      substituted payloads (e.g. `MemoryResult<ViewOnlyPayload>` is
      copyable, `LexResult<TokenStream>` is not) — `where Copyable(T)` is
      only needed to write generic code that REQUIRES copyability.
      `CreateConcreteStructLike` propagates `HasDestructor` from template to
      instantiation (destructor-bearing generic structs stay move-only).
      C# move-semantics tests (32) and their ported stark facts (26) had
      their scalar fixture types given empty `drop { }` blocks so they keep
      testing move machinery.
- [x] Treat place reads of `Copyable` types as reads, not consumes: STK4203
      move-out-of-place no longer fires for copyable fields, indexed places,
      and locals; copy sources stay usable. NOTE: whole-aggregate reads from
      dynamic slots (`return self.Items[index];`) still need the
      initialized-slot proof — section 2's flow facts unlock those; field
      reads (`self.Items[index].Tag`) work today.
- [x] Add `where Copyable(T)` as a generic bound: `Copyable` joined the law
      predicate names and rides the entire Transferable/Shareable pipeline
      — grammar (`lawPredicateContract` already parsed it), STK3049
      call-site conformance with field-chain reasons, open-generic
      forwarding hints ("declare `where Copyable(T)`"), caching, and
      package-image predicate round-trip — all for free. The verdict
      delegates to the same `CopyabilityFacts` resolver ownership uses
      (single source of truth); only failure reasons are synthesized in the
      evaluator. Grant/Deny never apply (structural only). The generic
      exclusion needed no lifting: concrete instantiations already classify
      structurally, and templates skip ownership validation; the bound's
      job is call-site conformance + documentation, which it now does.
- [x] Add the `[Copyable]` assertion attribute on struct, record, and enum
      declarations (grammar already covered it; the syntax-model attribute
      allowlists gained the name, args rejected with STK2113). A late
      type-check pass evaluates the law on each asserted type and reports
      STK3051 at the declaration with the reason and responsible field
      chain ("[Copyable] assertion failed: Type 'dynamic i64' is owning
      dynamic storage... Responsible field chain: Bad.Items.").
- [x] Verify derived copyability flows through package images and the
      imported-template lowering path. Package-dependency path: the lexer
      fact `KindAtReturnsCopyableValuesAcrossModules` copies the imported
      152-variant `TokenKind` across the `stark-compiler` package boundary.
      Facts path: `DestructorFlagRoundTripsThroughPackageImageFacts` builds
      a package image for a destructor-bearing struct, loads it via
      `TryBuildLoadedPackageImageFacts`, and asserts `HasDestructor`
      survives and `CopyabilityFacts` keeps the import move-only.
- [x] Verify comptime/CTFE evaluation treats `Copyable` reads consistently:
      `ComptimeEvaluationCopiesCopyableValuesConsistently` reads const-
      aggregate enum projections repeatedly through CTFE + full lowering.
      (Side observation, pre-existing semantics: const-global projections
      carry `frozen` provenance, so they bind to frozen forms or compare in
      place — unrelated to copyability.)
- [x] C# host tests: `CopyableDoctrineTests` (7) covers tag-enum/struct
      copies out of dynamic storage and locals, payload-owning enums and
      destructor structs staying move-only, `where Copyable(T)`
      accept/reject/forwarding with field-chain diagnostics, `[Copyable]`
      assertion success and both failure shapes, CTFE consistency, and the
      `.starkpkg` facts round-trip.
- [x] Stark validation: full accessor rewrite landed via section 4 —
      `KindAtIs` is deleted, facts compare values directly.

## 2. Initialized-Read Value Contracts

Dynamic storage keeps a dense initialized prefix: the first `Length` slots
(indices `0` through `Length - 1`) are initialized, everything at `Length`
and beyond is not. Two layers make safe reads provable without `unsafe`:

**Layer 1 — flow facts.** A read `place[index]` dominated by a comparison
establishing `index < place.Length` is proven. Sound under `borrow` because
the borrow freezes `Length`; under `mut borrow` the fact invalidates at any
mutating use of the place. Subsumes the loop-condition proof and fixes the
current asymmetry where guard-then-read compiles inside the owning type but
not as a free function.

**Layer 2 — `where` comparisons.** `where` clauses accept comparison
expressions over parameters, constants, and borrowed-place lengths
(`where start + length <= self.Items.Length`, `where index <
self.Tokens.Length`). Call sites must discharge the predicate from their own
flow facts or `where` clauses. Zero runtime cost; doubles as general range
preconditions.

- [x] Layer 1: branch-dominance facts from `Length` comparisons in
      ownership validation. Landed as `DynamicLengthFacts`
      (`src/Compiler/DynamicLengthFacts.cs`) + per-path fact sets on the
      ownership `FlowState`: strict `index < root.Length` / `root.Length >
      index` comparisons grant the fact to the true path, `>=`/`<=` to the
      false path, `&&` conjuncts each contribute, and an `if` whose branch
      always returns/breaks/continues grants the surviving facts to the
      continuation (the early-return guard idiom). `while`/`for` conditions
      seed the loop body. Facts match by whitespace-free source text, so the
      guard and the read must spell the index and storage path identically.
      Free functions and owning-type methods now behave the same.
      BONUS soundness fix: field projections through a dynamic slot
      (`items[i].Tag`) were previously UNCHECKED — they now carry the slot
      access and need the same proof (one stdlib site needed an honest
      `unsafe` annotation: Toml parent-link `ChildCount` update). Whole-slot
      moves keep the MoveLast() discipline and now get the more precise
      non-tail-slot diagnostic for field-projected moves.
      `InitializedReadFlowFactTests` covers positives and all invalidation
      negatives.
- [x] Layer 1: arithmetic propagation — `dyn[start + index]` is proven from
      a strict bound on one addend (`index < length`) plus a matching sum
      fact (`start + length <= dyn.Length`, contract or spelled guard;
      strictness required on exactly one side). Sound under the
      overflow-is-illegal arithmetic contract. Splitting is top-level-`+`
      aware (parentheses, range brackets, generic args do not split);
      both operand orders are tried. `ValueContractTests` covers the
      contracted body, the guard-spelled caller, and the no-contract
      rejection.
- [x] Layer 1: fact invalidation — assignments and compound assignments to
      any identifier a fact mentions, mut-borrow uses of the storage owner
      (calls taking `mut borrow`), shadowing declarations, and branch/loop
      merges that intersect facts across paths.
- [x] Layer 2: grammar — `valueContract: shiftExpression (< | > | <= | >=)
      shiftExpression` joined `parameterMemoryContract` in `Stark.g4`;
      parser regenerated with ANTLR 4.13.1 (now installed via Homebrew
      OpenJDK + pip antlr4-tools, pinned to the runtime version).
- [x] Layer 2: signature plumbing + ownership integration.
      `ParameterValueContract(LeftText, OperatorText, RightText)` flows
      SyntaxModelFactory → FunctionDeclarationModel → TypedFunctionSignature.
      Inside the callee, contracts seed entry facts (strict `< .Length`
      forms grant dynamic-read proofs; every contract joins a general
      comparison-fact set). At every call site, each contract is
      re-spelled with the receiver/argument texts (positional args;
      receiver from simple-place reconstruction) and must be discharged by
      a dominating comparison (general comparison facts now flow from `if`/
      loop conditions with negation on terminating guards), the caller's
      own matching `where` contract, or constant arguments — otherwise
      STK4206 names the contract AND the substituted obligation.
      `<=`/`>=` contracts are dischargeable by spelled guards; only the
      body-side dynamic-read proof remains strict-`<`. Open follow-ups:
      well-formedness diagnostics for contracts naming unknown identifiers,
      and public contracted APIs need public-surface phrasing (contracts
      over internal fields are correctly undischargeable outside the
      module — law-call facts would lift this).
- [x] Layer 2: package-image preservation — manifest record
      `StarkPackageValueContractManifest` on the four function/method
      manifest types, encoded by the typed-interface/source-surface
      builders, decoded by both loaders, and rendered by the source bridge
      as ` where L op R` (validated end to end: the lexer's contracted
      `TokenAtProven` compiles in the selfhost package and runs through
      every TokenAt fact).
- [x] Document the remaining `unsafe` boundary: genuinely sparse structures
      (hash slots, freelists, parent links) keep the explicit sparse
      initialized-slot proof, and whole-slot moves keep the MoveLast()
      discipline. Documented in `LanguageReference.md` §7.2 and the
      stark-language SKILL.
- [x] C# host tests: `InitializedReadFlowFactTests` covers free-function
      guard-then-read (direct, whole-value, field-projected, conjunction,
      while-loop), the now-checked unguarded field projection, index-write
      invalidation, mut-borrow invalidation, and the non-strict /
      non-terminating negatives. `ValueContractTests` covers entry
      seeding, guard/forwarded-contract/constant discharge, mirrored
      normalization, unproven-call diagnostics with substituted
      obligations, and non-leakage to unrelated indexes.
- [x] Stark validation: de-`unsafe`d the stdlib helpers — Toml `TextAt`/
      `KeyAt`/`I64At`/`TryFindMember`/`TryFindMemberOfKind`/`Parse` via
      guard-then-read, `KeyEquals` via contract + spelled range guard,
      `KeyRangeEquals` fully safe as `finite law` via contract + arithmetic
      propagation; Json accessors mirrored. Callers dropped their hoisted
      `unsafe { }` wrappers. Unsafe remains ONLY for raw-pointer view/slice
      construction, the parent-link `ChildCount` updates (inexpressible
      invariant), and the Json writer's pointer append chain. Toml suite
      17/17; full stdlib compiles clean. Lexer internals: see section 4.

## 3. Confirmed Compiler Bugs

- [x] Integer literal typing: a bare literal mixed into ranged-integer
      arithmetic collapsed the expression to default `i64`
      (`position + 1` on `u64[0 2 ** 63 - 1]` demanded a narrowing cast;
      var-with-var arithmetic did not, because `FindCommonType`'s
      same-display-name fast path preserves a shared ranged type while the
      literal's singleton signed type — `1` is `i8[1 1]` — misses it and
      merges to a full-width signed default). Fixed in `TypeChecking.cs`:
      `ExpressionBinding` gained `IntegerLiteralValue` (set in
      `EvaluateLiteral`, rides the unary/postfix/primary pass-through like
      `TextLiteral`); a new `ResolveIntegerLiteralOperandTypes` helper picks
      the single ranged-integer "anchor" shared by the chain's non-literal
      operands and lets each fitting literal adopt a PLAIN integer of the
      anchor's numeric shape (width/range/sign, qualifiers stripped — an
      `out`/`borrow`/`frozen` operand yields a plain value). Wired into both
      `EvaluateArithmeticChain` (additive/multiplicative) and
      `EvaluateBinaryChain` (bitwise). Non-fitting and negative-into-unsigned
      literals still require an explicit cast (soundness). Pure-constant
      expressions are unaffected (they fold upstream via
      `TryEvaluateCompileTimeIntegerExpression`). `IntegerLiteralTypingRegressionTests`
      (7 tests). Fallout, all fixed: one package-image test relied on the old
      `7 + <u64>` → `i64` collapse (cast added at its return); the stdlib's
      `System.Collections` capacity-growth `outParam * 2` exposed the
      qualifier-leak now stripped. Verified clean: full C# `compiler.Tests`
      (1679), LLVM-IR slice (428), full stdlib `--check`, `selfhost.Lexing`
      (18), `stdlib.Toml` (17).
- [x] `raw"""` full C# parity in `TextLiteralDecoder`: leading-newline
      strip, trailing-newline strip, closing-delimiter indentation strip,
      and the three C# error cases (content on the opening line, content on
      the closing line, under-indented lines) — landed, including the
      `$raw"""` interpolated path normalizing before hole splitting
      (`RawMultilineStringParityTests`, 7 tests). All 1420 stark-side raw
      blocks already used open-at-EOL / close-at-column-0 form, so only the
      delimiter newlines changed for them. Verified: full C# suite
      1641/1641; stark suites selfhost.Lexing, stdlib.Toml,
      compiler.FeatureTests, compiler.Tests all green.
      `docs/Userfacing/LanguageReference.md` strings section and the
      stark-language SKILL updated to the new rules.
- [x] Imported-module bare-name ambiguity: a name exported by two imported
      modules inside an imported source module crashed lower-mir with
      "Named operand ... could not be resolved" (STK9999) instead of a
      located ambiguity diagnostic. Root cause: imported non-generic source
      bodies are NOT type-checked (their module-private names can't resolve
      from this context — proven by experiment: full re-check floods the
      stdlib with false STK3003 unknown-symbol errors), yet they ARE lowered,
      and lower-mir has no diagnostic channel (only invariant-violation
      crashes). Fixed with a focused type-check scan
      (`CheckImportedModuleNameAmbiguities`): for each imported source module,
      compute the names exported (Public/Export) by >= 2 of its own imports
      — counting only each import's OWN declarations, so re-exports of one
      symbol aren't double-counted — minus names the module declares itself;
      a bare, unshadowed reference to such a name reports STK3003 naming the
      candidate modules (`ModA.Shared, ModB.Shared`), located in the imported
      file. Gated on a non-empty ambiguous-name set (empty for almost every
      module → no walk, no cost). Param/local shadowing is honored. v1 covers
      function/value references (types-in-declarations and ctor bodies are
      follow-ups). `ImportedModuleAmbiguityRegressionTests` (3). Verified: the
      repro now reports the diagnostic at `Mid.stark` instead of crashing,
      qualifying compiles clean, and full stdlib `--check` / `selfhost.Lexing`
      / `stdlib.Toml` / the C# suite show no false positives.
- [x] STK3005 hint: calling a module-level function with method syntax
      (`value.Fn(...)` where `Fn`'s first parameter matches the receiver
      type) now explains that methods are declared inside the type body and
      suggests the free-call form. Implemented in `ApplyMemberAccess`
      (`TryDescribeMethodSyntaxFreeFunctionHint`): when the missing member
      matches a free function whose first parameter is the receiver's named
      type, the STK3005 message gains "'Fn' is a free function — call it as
      'Fn(...)' with the receiver as the first argument. Methods are declared
      inside the type body." A genuinely missing member keeps the bare
      message. `MethodSyntaxHintRegressionTests` (2).
- [x] Ownership branch-join over-poisoning (the "early return-move" sharp
      edge): an early `return value;` (a move) inside an if branch or switch
      arm leaked its end-state into the join, so the surviving path reported
      bogus STK4205 "not fully initialized" — the bug behind every
      flag-and-single-tail-return rewrite in the ports. Fixed: branches that
      return from the function on every path are excluded from the join
      (`DynamicLengthFacts.ReturnsAlways` — stricter than `TerminatesAlways`
      because moves before `break`/`continue` must keep merging into the
      loop join, verified by probe). If-joins adopt the surviving branch;
      switch joins merge only non-returning sections. Soundness probed both
      ways: conditional/arm moves without return, else-branch moves under a
      returning then (now the precise STK4200 definite-move), and
      move-before-break below the loop all still error.
      `OwnershipBranchJoinRegressionTests` (7 tests); ownership-adjacent
      slices 83/83; selfhost.Lexing suite green; full C# suite gating.
- [x] Aggregate return miscompile (`%v74`-style): does NOT reproduce on the
      current compiler. The trigger recipe (conditional field store +
      unconditional field move-store + aggregate return) was unreachable
      through the front door until the join fix above; now unlocked, it
      compiles and RUNS with verified values in both if and switch form,
      with and without an early-return guard and an init-append
      (probes /tmp/v74probe/pv,g6,sw1,sw2 — exit-code-checked both paths).
      Presumed fixed by intervening lowering work (June 2026: slice element
      derivation, raw literal operands, enum tags, out-param drops). The old
      chip's exact original recipe died with the workaround rewrites; if it
      resurfaces, file fresh with a minimal repro.
- [x] Flow-fact polish: equality guards now feed indexed-read proofs —
      `if (dyn.Length != 1) { return ...; }` then `dyn[0]` compiles (no
      `<`/`>=` guard needed). Done in `DynamicLengthFacts`: a new
      `CollectFromEquality` (descended via `DescendToEquality`, which handles
      the `equalityExpression` level that `DescendToRelational` skips) reads
      `dyn.Length == k` / `dyn.Length != k` against a constant `k` and emits
      the in-bounds constant indices `[0, k)` as ordinary `DynamicLengthFact`s
      on the path where `Length == k` holds (the true branch of `==`, the
      surviving path of `!=`). A non-empty check (`!= 0`, since a length is
      non-negative) emits index 0. Reusing the existing fact type means the
      read proof, whitespace-free text matching, and write-invalidation are
      all shared — out-of-bounds constant indices and reads after a mutating
      use still fail. Constant indices capped at `[0, 64)` per guard.
      `EqualityGuardFlowFactTests` (5). Verified: full stdlib `--check`,
      `selfhost.Lexing`, `stdlib.Toml`, and the flow-fact/ownership C# slices
      all clean.
- [x] SSA cross-block load forwarding miscompile (the `%vN_inlK` STK5002):
      the alias-aware memory optimizer's load-indirect (field) forwarding
      dropped the defining load with a per-block replacement map and NO
      `ValueUsesAreConfinedToBlock` guard — unlike its load-local and
      load-global siblings. A result consumed in another block (an inlined
      call's continuation phi — surfaced by an imported law inlined twice
      into a caller itself inlined into `main`) kept referencing the deleted
      load. Fixed by adding the same confinement guard (the load survives
      when used cross-block). Diagnosed via an env-gated dump in the SSA
      validator (`STARK_DEBUG_SSA_VALIDATE=1`, kept for future failures).
      `SsaCrossBlockLoadForwardingRegressionTests`; SSA slices 248/248;
      all four repro variants build AND run exit 0.
- [x] Field-read-after-move soundness gap: `moved.Field.Length` /
      `.Capacity` bypassed the moved-root check because the dynamic
      header-read special case in `ApplyMemberAccess` returned a scalar
      with no variable attached, so the outer use check had nothing to
      inspect. Whole-value uses always errored; this was the field-path
      hole. Fixed by checking receiver availability in place
      (`TryEnsureValueAvailable` with the flow state now threaded into
      `ApplyMemberAccess`). Definite moves, maybe-moves (control-flow), and
      moved-receiver dynamic member calls all error STK4200 now;
      constructor flows reading already-assigned dynamic fields stay legal.
      `OwnershipFieldReadAfterMoveRegressionTests` (6 tests);
      ownership-adjacent slices 95/95; selfhost.Lexing suite green.
- [x] Discovered while probing the above: constructor bodies did not check
      READS of not-yet-assigned `self` fields (`self.Count =
      self.Items.Length;` before `self.Items = new();` compiled — reads the
      pre-construction zero state at runtime, empirically `Length` 0 rather
      than the intended value). Ownership validation never sees ctor bodies
      (they are type-checked via `CheckStructLikeConstructorBodies`, and the
      `self` receiver was treated as fully initialized), so the read bypassed
      all field-state tracking. Fixed in `TypeChecking.cs`:
      `ValidateConstructorFieldReads` walks the ctor block in evaluation order
      with a monotonic "assigned-so-far" set (`self.Field = ...` marks the
      field; union semantics at branches/loops, so valid code is never
      rejected), and a read of an unassigned field reports STK3055. Scoped to
      OWNING fields — `CopyabilityFacts.IsCopyable` plus a fixed-array
      exclusion — because scalars, fixed arrays, and copyable aggregates have
      a valid zero state and are written element-wise (this is what stopped a
      false positive on Json's `bool[64] HasItem` indexed writes). Indexed
      reads/writes into an unassigned dynamic (`self.Items[0] = ...`) are
      caught too. v1 is structs only (records pre-initialize their
      primary-constructor fields — a follow-up). `ConstructorFieldReadRegressionTests`
      (7). Verified: full stdlib `--check`, `selfhost.Lexing` (18),
      `stdlib.Toml` (17), and the C# `compiler.Tests` suite all clean.

## 4. Lexer Follow-Up (Apply The New Patterns)

Every item above came out of building the Stage 1 lexer, so the lexer is the
acceptance test for the whole checklist: once sections 1–3 land, sweep
`selfhost/Compiler/Lexing.stark` and `tests-stark/selfhost.Lexing` so the
first ported compiler stage uses the language as intended instead of the
workarounds it forced.

Order of operations for ANY language change (standing rule): `Stark.g4`
first, host parser regenerated, THEN the Stage 1 lexer must both lex the new
surface and (where it helps) use it.

- [x] Grammar-first audit for this checklist's changes: the only
      syntax-bearing change was the `valueContract` where-clause rule —
      `Stark.g4` was updated and the ANTLR parser regenerated at
      implementation time, before anything depended on it. `[Copyable]`
      rides the existing attribute rule, `where Copyable(T)` rides
      `lawPredicateContract`, and `raw"""` parity changed only the
      decoder, not the token. No new tokens exist, so the Stage 1 lexer's
      vocabulary is already complete; the fact
      `NewLanguageSurfaceLexesCleanly` lexes a program using every new form
      ([Copyable], Copyable(T) bounds, comparison contracts) and asserts
      clean diagnostics and token kinds.

- [x] Replace `KindAtIs`/`DiagnosticKindAtIs` with `Copyable`-powered
      value-returning accessors: both equality workarounds are DELETED
      (zero occurrences repo-wide), all facts use direct comparisons
      (`stream.KindAt(0) == TokenKind.EndOfFile`), and
      `LexDiagnosticKind` gained a `None` sentinel so
      `DiagnosticKindAt` stays total for past-the-end probes.
- [x] De-`unsafe` lexer internals: `SpanEquals`, `WidthDigitsAreValid*`,
      and `ClassifyWord` are safe `finite` (the pointer walk over the
      comparison word is one confined `unsafe { }` block), the three
      call-site hoist dances in `LexNextToken` are gone, and `LexText` is
      now a SAFE public `fn` (slice materialization confined). Exactly two
      `unsafe` blocks remain in the whole lexer, both genuine rawptr work.
      Bonus: every fact in `tests-stark/selfhost.Lexing` dropped its
      `unsafe { }` wrapper — the lexer's public API and its entire test
      suite are now 100% safe code.
- [x] Remove the literal-typing narrowing casts
      (`(u64[0 2 ** 63 - 1])(length - 1)`, `(... )(cursor.Position + 1)`
      and friends) once the literal fix lands. DONE: swept all 27 redundant
      casts from `selfhost/Compiler/Lexing.stark` — 25 `u64` (the literal
      `cursor.Position ± N` / `start + 1` / `length - 1` / `probe + 1` forms
      AND the already-redundant `cursor.Position - start` var-var forms,
      which `FindCommonType`'s same-name fast path always preserved) plus 2
      `u32` line/column `+ 1` casts. The 3 genuine `i64`→`u64` narrowings of
      `AsciiLength` results (`(u64[...])signedLength`/`sourceLength`, no
      paren) are correctly preserved. `selfhost.Lexing` green (18 facts);
      lexer `--check` clean. Note: the same now-redundant cast pattern
      survives at a few stdlib sites (e.g. `System.Collections`
      `(u64[...])(nextCapacity * 2)`) — harmless identity conversions, left
      in place as a separate stdlib-wide cleanup outside this lexer sweep.
- [x] Re-verify facts embedding `raw"""` sources under C#-parity decoding
      — `RealStarkSourceLexesCleanly` and `NewLanguageSurfaceLexesCleanly`
      green across every suite run since parity landed.
- [x] Rerun `tests-stark/selfhost.Lexing` green (18 facts); lexer row notes
      in `05-port-checklist.md` updated for the new API surface
      (`KindAt`/`TokenAt`/`TokenAtProven`/`DiagnosticKindAt`, `KindAtIs`
      removed, `LexText` safe).
- [x] Workarounds deleted versus added: DELETED — `KindAtIs` (+~85 call
      sites), `DiagnosticKindAtIs`, three call-site `stack mut` +
      `unsafe { }` hoist dances, four `unsafe` function markers, `LexText`'s
      unsafe marker, and all 18 test-fact `unsafe` wrappers. ADDED — one
      `None` sentinel variant (a design choice, not a workaround). No new
      inexpressible patterns found; the only open item is the
      literal-typing cast noise, already tracked as a section-3 bug.

## 5. Test Collections

Named, cross-cutting test selection: `[Collection("ownership", "lexing")]`
tags a module declaration (every fact in that module), a type, or an
individual fact with one or more collection names. A collection is simply
the set of everything tagged with its name — modules and individual facts
both, across files. `stark test --collection ownership` runs the union of
the selected collections instead of the whole project, which is the
targeted-slice discipline the stark side currently cannot express.

Decided shape: one attribute (the existing `[Collection]`, extended to
variadic names — no separate `[Collections]`), a fact's effective set is the
UNION of its module-level, type-level, and member-level names, run-grouping
in the generated runner keys on the first listed name (preserving today's
behavior for single-name uses), and filtering happens at runtime in the
generated runner so changing the filter never recompiles.

- [ ] Extend the runner generator (`src/Compiler/StarkTestRunnerGenerator.cs`)
      to variadic `[Collection("a", "b", ...)]` and module-level attachment
      (the grammar already allows `attributeList* MODULE name`). Effective
      collections = union of module/type/member names; replace the current
      type-vs-member conflict diagnostic with union semantics; keep
      run-grouping by the first listed name.
- [ ] Embed each fact's collection names in the generated runner and accept
      runtime filter arguments; an unknown collection name is an error that
      lists the known collections (typo protection), and a run that selects
      zero facts fails rather than silently passing.
- [ ] `stark test` CLI: repeatable `--collection NAME` with comma-splitting
      inside each value (`--collection ownership,lexing`), union semantics,
      forwarded to the runner binary. Optional discovery aid:
      `stark test --list-collections`.
- [ ] Scope note: v1 is per-project (the project `stark test` runs in);
      solution-wide collection runs across multiple test projects are a
      follow-up once `stark test` grows a solution mode. Module-level tags
      are still useful today (tagging the root module tags the project) and
      become more powerful when fact discovery extends beyond the root file.
- [ ] C# host tests: runner-generator coverage for variadic names,
      module-level attachment, union with type/member names, grouping by
      first name; CLI integration coverage for filtering, comma-splitting,
      unknown-name error, and zero-selection failure.
- [ ] Stark validation: tag the lexer facts (e.g. "lexing", "diagnostics")
      and a compiler.Tests swath (e.g. "ownership"), then verify
      `stark test --collection diagnostics` runs exactly the tagged subset.
- [ ] Docs: the projects/tooling doc's testing section and the
      stark-language SKILL ([Fact]/[Theory]/[Platform]/[Collection] row)
      document the variadic attribute, module tagging, and the CLI.

### Section 5 status (2026-06-12, partial)

LANDED and building: variadic `[Collection("a", "b")]` (runner generator +
both syntax-model validators accept one-or-more), module-level attachment
(`[Collection(...)]` before `module X`; module attr validator allows it),
union of module/type/member names, first-name run-grouping,
`StarkTestFact.CollectionNames` list model, generated-runner runtime filter
(arg loop via new `System.Testing.CollectionArgumentCount/
CollectionArgumentEquals` wrappers over `ProcessArguments.ArgumentEquals`,
unknown-name error listing known names, `--list-collections`, zero-selection
failure), `stark test --collection` (repeatable + comma-split) and
`--list-collections` CLI forwarding + help text. Filter emission is GATED on
the project having any collections, so untagged projects keep the previous
runner shape (both existing suites green).

- [x] FIXED: the STK5003 mis-association. Root cause: lowering-contract
  fact lookup keyed (enclosing fn, line, column) and FELL BACK to a
  null-function key on primary miss; constructor-body records carry a null
  enclosing function at THEIR OWN file's coordinates, so a member call in
  the generated root collided with the newly-materialized `OwnedCStr`
  constructor's direct call at the same line/column in `System/C.stark` and
  misvalidated as a cross-file direct call. Fix: removed the null-key
  fallback in `LoweringContractValidation.TryGetRecord` (a fallback hit was
  always a different call's fact). Enforcing regression: stdlib.Toml stays
  TAGGED (`[Collection("toml")]` module-level + member tags on
  `RealProjectManifestDecodes`) so every suite run exercises the filter
  machinery; `LoweringContractFactKeyRegressionTests` documents the
  collision shape (it does not trip the old fallback by itself — the
  materialization trigger needs the generated-runner chain).
- [x] CLI scenarios validated on stdlib.Toml: `--collection toml` runs all
  17, member tags select subsets (`--collection manifests` runs exactly
  `RealProjectManifestDecodes`), `--list-collections` prints names and
  exits clean, unknown names error with the known list and a failing exit.
- [x] FIXED: the build-layout duplicate-symbol gap. Root cause: a package
  archive bundles every module object its own build compiled (its modules
  PLUS its stdlib closure), while the consumer compiles its own reachable
  closure locally — the two sets overlapped once the tagged runner pulled
  System.Process/System.Memory into the consumer's set. Fix: the
  executable build reads each linked package's manifest
  (`CollectPackageProvidedModuleNames` in CompilerCli) and skips locally
  compiling any module the archive already provides. Enforcing regression:
  selfhost.Lexing stays TAGGED (module-level `[Collection("lexing")]` +
  `[Collection("diagnostics")]` on the three diagnostic facts), so every
  run exercises archive-vs-local dedup; `--collection diagnostics` runs
  exactly 3 facts and `--list-collections` prints both names. Known
  remaining edge (documented, not blocking): two dependency archives that
  each bundle the same stdlib module can still collide archive-vs-archive —
  the deeper fix is not bundling stdlib objects in package archives at all,
  which belongs to the build-artifact-layout work (doc 25).
- [x] C# runner-generator tests: the stale conflict-semantics test now
  asserts the at-least-one rule, and
  `CollectionsUnionAcrossModuleTypeAndMemberAndAcceptVariadicNames` asserts
  the module/type/member union order, first-name grouping, and the emitted
  filter/list machinery (16/16 generator tests green).
- [ ] Remaining: docs row (projects/tooling doc + SKILL [Collection] entry).

## 6. Documentation, Skill, And Memory Updates

- [ ] `docs/Userfacing/LanguageReference.md`: add the `Copyable` doctrine
      (definition, structural derivation, `[Copyable]` assertion,
      `where Copyable(T)` bound), `where` value contracts (accepted
      expression forms, call-site proof rules, flow facts), the corrected
      integer-literal typing rule, and the C#-parity `raw"""` semantics.
- [ ] `Stark.g4`: `where` comparison grammar; `[Copyable]` attribute
      coverage check.
- [ ] Book (`site/content/book`): strings chapter for `raw"""` semantics;
      ownership chapter for `Copyable`; contracts/where chapter for value
      predicates; integer chapter for literal typing.
- [ ] `skills/stark-language/SKILL.md` and references: remove superseded
      gotchas (KindAtIs-style equality workaround, literal narrowing-cast
      noise, `raw"""` leading-newline warning, unsafe hoisting for
      guard-then-read), add the new rules and the remaining sparse-`unsafe`
      boundary.
- [ ] Claude memory files (`stark-lang-core`, `stark-ownership-borrowing`,
      `stark-enums-errors`, `stark-text-storage`,
      `stark-test-port-harness`): update each affected fact once the
      features and fixes land.
- [ ] `docs/Self-host-Prep/ROADMAP.md`: keep the feature rows in
      sync as items land; when every box here is checked, fold durable
      content into LanguageReference.md/the book and delete this file (and
      its ROADMAP links).

### Section 6 status (2026-06-13, integer-literal typing parts)

The boxes above stay unchecked because they each span several features. The
**integer-literal typing** documentation (section 3 bug 1, now landed) is
complete: LanguageReference.md §11.6 states the literal-adopts-the-ranged-operand
rule (with the qualifier-strip and non-fitting caveats); the book's
`31-integers-floats-overflow` chapter contrasts literal adoption (no cast) with
runtime-value narrowing (cast required); the SKILL.md gotcha row and the
`stark-lang-core` / `stark-test-port-harness` memory facts were rewritten from the
old "literal collapses to i64, add a cast" wording to the new rule. No `Stark.g4`
or ROADMAP change was needed (the fix added no grammar surface and matches no
existing ROADMAP feature row). The remaining unchecked work under these rows is
the `Copyable` / `where`-contract / `raw"""` documentation owned by sections 1–2.
