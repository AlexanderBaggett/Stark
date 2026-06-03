# Stark Self-Hosting — Canonical Prep Roadmap

**This is the single at-a-glance tracker for self-hosting preparation.** It is a
pure checklist. Rationale, audits, and per-file detail live in the companion docs
linked below; update the checkboxes here as work lands so this file always shows
current state.

**Legend:** `[ ]` not started · `[~]` in progress / partial · `[x]` done.
Each task is `**ID** — one-line task — severity — → detail doc`. IDs are stable
across all docs, so an agent can jump to the detail doc for full context.

**Companion docs** (all under `docs/Self-host-Prep/` unless noted):
`00` host-compiler inventory ·
`01` language-feature gaps (L*) ·
`02` stdlib gaps (S*) ·
`03` tooling gaps (T*) ·
`04` test-infra audit (TEST-*) ·
`05` per-file port checklist ·
`06` milestone narrative (M0–M7) ·
`07` open questions (OQ*) ·
`08` Stark feature roadmap ·
`09` self-hosted compiler architecture ·
`10` traits & dynamic dispatch (TD*) ·
`11` error-value propagation `try`/`from` (EP*) ·
`12` atomics (AT*; full thread-ownership model parked in git history) ·
`13` compile-time evaluation `comptime` (CT*, design proposal — awaiting lock) ·
`14` thread-safety laws `Transferable`/`Shareable` (TS*, design proposal — awaiting lock) ·
`SelfHostingRoadmap.md` (capability rationale) ·
`ToolchainPackagingRoadmap.md` (release/packaging detail) ·
`docs/Internals/CompilerPipeline.md` (pass list) ·
`docs/Internals/ReleaseRoadmap.md` (separate release track).

---

## Status at a glance

| Phase | Theme | State |
|---|---|---|
| Audit & design (Phases 0–9) | gap analyses + self-hosted architecture | `[x]` complete (docs 00–09) |
| **M0** | Test infrastructure first | `[ ]` not started |
| **M1** | Port test suite vs host compiler | `[ ]` not started |
| **M2** | Close language blockers | `[~]` static traits + constrained generics + default members + **`dyn` trait objects (borrowed + owning)** done (TD01–TD17, CG01–CG07; TD04/TD07 partial — cross-module); **error propagation (L01) done: `try` + `from` funnels + `[Ok]`/`[Err]` role attributes (v2, EP16–EP20) incl. cross-module package carry (EP15)**; **`if let`/`while let` pattern conditions (L02) implemented**; **invariant-failure policy (L07) closed via switch exhaustiveness + definite return (STK3044/STK3045), no trap surface**; vtable Phase D (TD18–21) + remaining L03–L14 |
| **M3** | Close stdlib blockers | `[ ]` not started |
| **M4** | Close tooling blockers | `[ ]` not started |
| **M5** | Port compiler subsystems leaf-first | `[ ]` not started |
| **M6** | Bootstrap (Stage1/Stage2) | `[ ]` not started |
| **M7** | Snapshot strategy & drop host | `[ ]` not started |

**Counts:** Language 3/14 (L01, L02, L07 done) · Stdlib 0/18 ·
Tooling 0/15 · Test-infra 0/12 · Traits/Dispatch 16/24 done (TD01–TD03, TD05–TD06,
TD08–TD17, TD22) + 2 partial (TD04, TD07: cross-module left) · Constrained generics
7/7 done · Error propagation 18/20 done (EP01–EP10, EP13–EP20; v2 role attributes +
cross-module package carry) + 2 open (EP11/EP12 proposed — depend OQ-EP5) ·
Decisions (OQ) 1/20 formally resolved (OQ-04, in doc
11; 4 trait/dispatch decisions settled separately in doc 10 §3) · Compiler files
to port ~115 · Test files to port ~90.

**Critical path (recommended order, per doc 06):**
M0 → M1 → resolve OQ-02 (parser) & OQ-14 (test runner) → M2/M3/M4 in parallel →
M5 → M6 → M7.

---

## M0 — Test infrastructure first  → `04`, `02` (S18)

The host suite is xUnit with rich .NET APIs; Stark `System.Testing` is tiny. Tests
port first, so this is the first-order blocker.

- [ ] **TEST-01** — test discovery + runner model (`[Fact]`/`[Theory]` without runtime reflection) — → `04`, OQ-14
- [ ] **TEST-02** — rich assertions (contains, single, null, type, collection, ranges, diagnostics) — → `04`
- [ ] **TEST-03** — snapshot/golden text utilities (LLVM/MIR/SSA/diag/package text) — → `04`
- [ ] **TEST-04** — temp dir/file fixtures with cleanup — *depends S10/S09* — → `04`
- [ ] **TEST-05** — process execution + stdout/stderr/exit capture — *depends S12* — → `04`
- [ ] **TEST-06** — host-compiler target mode (run Stark tests against the C# host) — → `04`
- [ ] **TEST-07** — compiler-as-library or machine-readable artifact/diagnostic inspection — *blocks deep pipeline tests* — → `04`, OQ-15
- [ ] **TEST-08** — parameterized tests / data providers (`[Theory]`/`[InlineData]`/`MemberData` equivalent) — → `04`
- [ ] **TEST-09** — platform gating + serial collections — → `04`
- [ ] **TEST-10** — benchmark/regression harness — → `04`
- [ ] **TEST-11** — package-image fixture editing + JSON comparison — *depends S14* — → `04`
- [ ] **TEST-12** — diagnostic formatting + diff helpers — → `04`
- [ ] **S18** — expand `System.Testing` to back TEST-01..12 — *blocker* — → `02`

## M1 — Port test suite to Stark, still targeting host compiler  → `05` (test sections), `04`

Port helpers first, then text-only tests, then artifact tests, then integration.

- [ ] Port test **helpers** first: `FeatureLlvmTestBase`, `CompilerPipelineTestSupport`, `FallbackLogAssertions` — → `05`
- [ ] Port `tests/compiler.FeatureTests` (13 files) — LLVM substring checks — → `05`
- [ ] Port `tests/compiler.Tests` (43 files) — parser/diag/type/ownership/MIR/SSA/LLVM/package — → `05`
- [ ] Port `tests/compiler.PipelineTests` (17 files) — pass + full-pipeline artifact gates — *depends TEST-07* — → `05`
- [ ] Port `tests/compiler.StandardLibraryTests` (30 files) — compile-only slices first — → `05`
- [ ] Port `tests/compiler.IntegrationTests` (29 files) — CLI/project/native — *depends S12/T08* — → `05`
- [ ] Generate `TestRunner.stark.toml` per project (replace `xunit.runner.json`) — *depends T12* — → `05`

## M2 — Close language feature blockers  → `01`, `10`

- [x] **L01** — error-value propagation (replace throw/try/catch/nullable) — *blocker* — **done (v2):** `try` expression + `from` funnels + **`[Ok]`/`[Err]` variant role attributes** (any two-variant enum is propagatable; no privileged stdlib types; recognition never by name). Position rule, drop-on-return lowering, cross-family funnel conversion, role/misuse diagnostics (STK3037–STK3043), stdlib annotation, runtime + diagnostics tests, user-facing docs — → `01`, `11`, OQ-04
- [x] **L02** — pattern binding in `if`/`while` conditions (`if let`/`while let`) — **implemented** as `is`-pattern conditions: `if (expr is pattern)` / `while behavior (expr is pattern)` reuse the `switch case` pattern grammar + binder verbatim (full enum/nested/struct/named-field/`var`/`_`/literal), captures bind into the consequent; lowers via the shared switch decision machinery (single-case-switch cost), same move/drop semantics. `is` reserved; runtime + diagnostic tests; LanguageReference §10.6 + SKILL updated — → `01`
- [ ] **L03** — iterator/foreach/collection traversal surface — *workaround exists* — → `01`
- [ ] **L04** — rich switch patterns (or/range/list/property patterns) — *workaround exists* — → `01`
- [ ] **L05** — generic value / const generic parameters — *workaround exists* — → `01`
- [~] **L06** — operator/hash/equality doctrine + trait surface for generics — *blocker* — trait conformance landing via TD (below); hashing/eq doctrine still open — → `01`, `10`
- [x] **L07** — compiler invariant-failure policy — *blocker* — **closed without adding panic/trap/assert (OQ-05 resolved):** invalid states are made unrepresentable instead. New language guarantees: **switch exhaustiveness (STK3044)** — every `switch` covers its whole domain (all enum variants / both bools / every value of a ranged integer) or has `default`; **definite return (STK3045)** — non-`void` functions must return on every path. These remove the silent runtime-trap and fall-off-the-end-UB paths. Port convention for host `throw` sites documented in doc `09` Error Model (restructure → exhaustive types → `Result`+`try` → stderr+`Process.Exit` residual) — → `01`, `09`, OQ-05
- [ ] **L08** — raw/multiline string literal ergonomics for compiler text — *workaround exists* — → `01`
- [ ] **L09** — general compile-time function evaluation / table generation — *workaround exists* — **design proposal in doc `13`** (C3 model: `comptime` blocks/expressions as a time selector over ordinary Stark; only new rules are the step budget + input availability). Module-scope semantics locked (const/static initializers are implicit comptime contexts; no free-standing blocks); CT01–CT08 + remaining OQ-CT* await full design lock — → `01`, `13`
- [ ] **L10** — async / build-driver concurrency replacement — *workaround exists* — **verified non-blocking for self-hosting:** the host compiler is functionally single-threaded (zero parallelism; `async` is sequential I/O idiom), so the synchronous port is behaviorally identical. Full thread-ownership model parked (git history, doc 12 pre-atomics revision); its language-side prerequisite — the `Transferable`/`Shareable` thread-safety laws — is specified in doc `14` (TS*, awaiting lock); revisit post-self-hosting for parallel builds — → `01`, `14`, OQ-11
- [ ] **L11** — nullability / optional-value conventions (no safe nulls) — *blocker* — → `01`, OQ-06
- [ ] **L12** — partial/nested/generated type layout ergonomics — *workaround exists* — → `01`
- [ ] **L13** — alias/noalias proof carriers + wrong-alias compile-time diagnostics — *blocker* — → `01`, OQ-17
- [ ] **L14 / T01** — parser generator/runtime strategy (also tracked as T01) — *blocker* — → `01`, `03`, OQ-02

### Traits & dynamic dispatch (closes the trait half of L06)  → `10`

- [x] **TD01** — `baseTraitList` grammar on struct/record + parser regen — → `10`
- [x] **TD02** — base-trait list captured on `NamedTypeSymbol.ImplementedTraits` (source modules; package-image carry pending for imported-type queries) — → `10`
- [x] **TD03** — type → implemented-trait edges = `NamedTypeSymbol.ImplementedTraits` — → `10`
- [~] **TD04** — conformance: `Self` type ✅, base-must-be-trait (STK3026) ✅, required-method presence (STK3032) ✅, arity+kind ✅, **exact param/return-type match with `Self`/type-arg substitution** (STK3033) ✅; **pending** cross-module (imported-trait) required-method detection — → `10`
- [x] **TD05** — trait-method calls on conforming values **and** `where T: Trait` generics: both resolve via `TryResolveTraitBoundMemberCall` at `ApplyMemberAccess` (captured bound, `Self`-substituted; = CG05); `Trait.Method(...)` via the trait name stays rejected (STK3013) — → `10`
- [x] **TD06** — lowering: concrete **and** generic-`T` trait calls bind to the receiver's concrete impl (`FunctionMirBuilder.IsTraitMethodTarget`; = CG06) → direct `call fastcc`, zero indirect/vtable — → `10`
- [~] **TD07** — conformance diagnostics tests: STK3026/3032/3033 incl. param-type-mismatch landed (5 tests in `SemanticValidationTests`); **pending** cross-module + generic-dispatch test coverage (tied to TD04 cross-module) — → `10`
- [x] **TD08–TD11** — default trait members: `;`-body = required / `{ }`-body = default (not required); a not-overridden default dispatches to the default body monomorphized over `Self` (direct call) for concrete **and** `where T: Trait` receivers; overrides win; defaults call other trait methods via the implicit `Self: <trait>` bound; tests landed — → `10`
- [x] **TD12–TD17** — `dyn` trait objects **done** (borrowed + owning): `dyn trait` opt-in; `borrow`/`mut borrow dyn Trait` fat-pointer views (no alloc) and owning `heap dyn Trait` (boxes + owns + drops the value via a synthesized per-type drop thunk in the vtable); object-safety diagnostics STK3035/3036; per-(type,trait) vtable synthesis; concrete→dyn coercion; indirect-call lowering that **preserves the `law`/`finite` effect contract**; runtime (borrowed + owned) + LLVM + diagnostic tests, all suites green. **Perf follow-up only:** Stark-level dyn-call devirt + DSE-precision (provenance model; today a conservative `ReadsOtherMemory` DSE barrier keeps it correct) — → `10`
- [ ] **TD18–TD21** — visible vtable / roll-your-own (`T.Vtable`, unsafe from-parts + decompose) — → `10`
- [~] **TD22–TD24** — TD22 **done**: `LanguageReference.md` §6.5 (trait bounds) + §8.5 (impl/`Self`/required+default/static dispatch) and `skills/stark-language/SKILL.md` updated, examples verified to compile+run; TD23 gap-doc sync + TD24 Dictionary-keys → general hashing/eq remain — → `10`

### Constrained generics (`where T: Trait` / doctrine bounds)  → `docs/Internals/Roadmap.md` §Constrained Generics (1829) + §specialization (2696)

Pulled from the original internals roadmap. This is the general constraint-solving
mechanism; **CG05/CG06 are the same work as TD05/TD06** (the trait-method-dispatch
slice). `where` clauses are currently parsed but entirely ignored.

- [x] **CG01** — `where`-clause bounds captured on `TypedFunctionSignature.Constraints` (`TypeChecking.ParseTypeParameterConstraints`) — → Roadmap §1836
- [x] **CG02** — obligation collection = the instantiation-ownership model (`FunctionInstantiationOwnership`) — → Roadmap §1832
- [x] **CG03** — each type argument must satisfy its bound (`ImplementedTraits`), checked in the instantiation-ownership pass; reuses TD02/TD04 — → Roadmap §1833
- [x] **CG04** — no-solution diagnostic **STK3034** at the call site — → Roadmap §1834
- [x] **CG05** — in-body resolution of bound members on a type parameter (= **TD05**): `ApplyMemberAccess` resolves `value.Member()` via the captured bound to the trait method (`Self`-substituted), `TryResolveTraitBoundMemberCall` — → `10`
- [x] **CG06** — trait-method calls rebound to the concrete impl in `FunctionMirBuilder` (`IsTraitMethodTarget` → falls through to receiver-concrete-type resolution) → **direct `call fastcc @Type_Method`** (= **TD06**) — → Roadmap §1835
- [x] **CG07** — abstraction fully erased: verified LLVM emits direct concrete calls, zero indirect/vtable, trait contract has "no runtime callable surface" — → Roadmap §1837, §2700

### Error propagation `try` / `from` (closes L01)  → `11`

Errors are values, returned not thrown; `try` is the **visible** early-return (no
trailing `?`), and `from` declares once, on the error type, how a composite absorbs
its causes. **v2 (implemented):** propagatable types are recognized by `[Ok]`/`[Err]`
**variant role attributes** on any two-variant enum — never by type name, never by
stdlib identity. The v1 (name-based) recognizers were replaced by the v2 role model
(Phase F); see doc `11` §3/§4.2 for the rules. Roles, funnels, and template `try`
facts survive the package-image boundary (EP15), so packaged dependencies — including
the dist stdlib — are `try`-propagatable exactly like source imports.

- [x] **EP01** — `Result<T,E>` + `Option<T>` as ordinary enums in `System` — *done* (`stdlib/src/System.stark`) — → `11`
- [x] **EP02** — conversion-representation decision: marker-backed `from` (no surfaced `FromError<E>` trait); trait deferred (OQ-EP1) — → `11`
- [x] **EP03** — grammar: `TRY` token + `try` unary expr; regen parser — `Stark.g4`, `antlr4` 4.13.2; `TRY`/`FROM` reserved (no in-tree collisions) — *unchanged by v2* — → `11`
- [x] **EP04** — typing — position rule + `_currentFunctionReturnType` + STK3037/3038/3039; recognizers reworked to role lookup in EP18 — → `11`
- [x] **EP05** — lowering — drop-on-return structure + `LowerDirectTagEnumConstructor`; variant lookup reworked to recorded roles in EP18 — → `11`
- [x] **EP06** — `try` tests — rewritten against role-annotated enums (EP20) — → `11`
- [x] **EP07** — grammar: `Ident from type_` absorbing enum-variant payload; regen — *unchanged by v2* — → `11`
- [x] **EP08** — model + diagnostics (in-module): `EnumVariantSymbol.AbsorbsErrorType`; STK3040 (duplicate funnel). *Cross-module carry = EP15* — *unchanged by v2* — → `11`
- [x] **EP09** — `try` cross-family conversion via `from` — funnel resolution + STK3041; inputs (failure payload types) come from `[Err]` role variants (EP18) — → `11`
- [x] **EP10** — `from` tests — rewritten against role-annotated enums (EP20) — → `11`
- [ ] **EP11** — *(proposed)* function-local `where Source => Variant` funnel (override type default) — *depends OQ-EP5* — → `11`
- [ ] **EP12** — *(proposed)* site overrides `try expr as Variant` / `maperr (E e) => …` — *depends OQ-EP5* — → `11`
- [x] **EP13** — user-facing docs (`LanguageReference.md` §10.5, `SKILL.md` + skill references, book Ch17/Ch13/appendices) — rewritten to the role model (EP20) — → `11`
- [x] **EP14** — gap-doc sync — doc `01` L01 entry + ROADMAP counts synced to v2 — → `11`
- [x] **EP15** — cross-module: roles + funnels serialized in all package-image sections and restored on load (generic instantiation threads them; `try` typing materializes imported instantiations on demand); exported generic templates with `try` bodies republish ordinal-keyed propagation facts consumed by downstream MIR lowering; bridge re-renders `[Ok]`/`[Err]`/`from` in synthetic source; verified against the dist stdlib package — → `11`

**Phase F — v2 rework: `[Ok]`/`[Err]` role attributes (the redo) — complete**

- [x] [x] **EP16** — grammar: attribute lists on enum variants (`enumVariantDeclaration : attributeList* Identifier enumVariantPayload?`); regen. No new keywords — → `11`
- [x] [x] **EP17** — innate-attribute model: `[Ok]`/`[Err]` roles on `EnumVariantSymbol` (`EnumVariantRole`); diagnostics STK3042 (unknown attribute / args) + STK3043 (role configuration: exactly one of each, exactly two variants, payload arity 0/1); roles + funnels threaded through generic monomorphization (`CreateConcreteEnum`) — → `11`, OQ-EP7
- [x] [x] **EP18** — recognizers reworked to roles: typing (`TryResolvePropagationRoles`) + lowering (recorded variant names in `TryPropagationTypingRecord`) consult `[Ok]`/`[Err]`; `TryGetResultShape`/`TryGetOptionShape`/`IsBlessedPropagationType` deleted; funnel inputs come from `[Err]` payload types — → `11`
- [x] [x] **EP19** — stdlib annotation: `[Ok]`/`[Err]` on `Result`/`Option`/`IOResult`/`IOStatus`/`MemoryResult`/`MemoryStatus`/`NetResult`/`NetStatus`/`ThreadStatus`/`ThreadJoinResult`; `dist/libSystem.starkpkg.json` rebuilt — → `11`
- [x] [x] **EP20** — tests + user-facing docs reworked to roles: diagnostics tests (non-conventional variant names, STK3037–3043), runtime tests (incl. cross-family `try` of a stdlib `IOResult` operand inside a user `AppResult` function, drop correctness through funnels), book Ch17 + samples, LanguageReference §10.5, SKILL + skill references — → `11`

## M3 — Close stdlib blockers  → `02`

- [ ] **S01** — shared `Option<T>` / `Result<T,E>` conventions — *blocker* — → `02`, OQ-06
- [ ] **S02** — text builder + formatted output (StringBuilder-equiv, interpolation, IR/diag rendering) — *blocker* — → `02`
- [ ] **S03** — text escaping + raw/multiline literal support — *blocker* — → `02`
- [ ] **S04** — regex or structured pattern-matching helper — *workaround exists* — → `02`
- [ ] **S05** — arbitrary-precision integers (or confirm `i1024`/`u1024` is the cap) — *blocker* — → `02`, OQ-07
- [ ] **S06** — compiler-grade collections (string-key `Dictionary`, `HashSet`, ordered maps/sets, deterministic iteration) — *blocker* — → `02`, OQ-08
- [ ] **S07** — symbol interning — *workaround exists* — → `02`
- [ ] **S08** — sorting/searching helpers — *workaround exists* — → `02`
- [ ] **S09** — file read / whole-file / line / UTF helpers — *blocker* — → `02`
- [ ] **S10** — filesystem metadata / temp dirs / recursive walk — *blocker* — → `02`
- [ ] **S11** — full path manipulation (absolute/relative/combine/change-ext/temp) — *blocker* — → `02`
- [ ] **S12** — process spawn / capture / env / argv / working dir — *blocker* — → `02`
- [ ] **S13** — TOML parser/emitter (`Stark.toml`, `Stark.solution.toml`, user config) — *blocker* — → `02`, OQ-10
- [ ] **S14** — JSON parser/emitter (`.starkpkg.json`) — *blocker unless format changes* — → `02`, OQ-09
- [ ] **S15** — time/stopwatch (pass durations, metrics) — *parity* — → `02`
- [~] **S16** — threading/sync primitives (mutex/once/atomics/channels) — *workaround for single-threaded v1* — **atomics design locked + in progress (doc `12`, AT01–AT08 below)**; mutex/once/channels deferred (build on atomics + existing platform futex; their safety rules consume the doc `14` thread-safety laws once locked) — → `02`, `12`, `14`, OQ-11
- [ ] **S17** — allocator/arena/shared-ownership strategy for IR graphs — *blocker* — → `02`, OQ-16
- [ ] **S18** — testing/golden/snapshot support (see M0) — *blocker* — → `02`
- [ ] **S19** _ File IO conveniences functions mirror .Net System.File api surface such as WriteAllLines, ReadAllLines

### Atomics (first slice of S16)  → `12`

Safe shared state for the threads Stark already ships. Locked design: atomic **data
types** (never a keyword qualifier) covering **every Stark integer width** (i8…i1024,
u8…u1024) plus `AtomicBool`; seq-cst only; ops Load/Store/Add/Sub/And/Or/Xor/Exchange/
CompareExchange (RMW returns previous value). Three implementation tiers: single
instructions (8–64 bit), lock-free CAS loops (24/48/96/128), embedded spinlock
(192–1024; visible in `sizeof`, never a hidden lock table). Not required for
self-hosting — this is shipped-surface hardening.

- [ ] **AT01** — stdlib type surface: `AtomicBool` + 28 integer atomic types in `System.Threading` — → `12`
- [ ] **AT02** — intrinsic recognition + LLVM lowering, tier 1 (8/16/32/64): single `atomicrmw`/`cmpxchg`/`load atomic`/`store atomic` — → `12`
- [ ] **AT03** — tier 2 lowering (24/48/96/128): CAS loops on power-of-2 storage containers — → `12`
- [ ] **AT04** — tier 3 lowering (192–1024): embedded-spinlock layout + serialized ops — → `12`
- [ ] **AT05** — runtime tests: multi-threaded exact-count increments per tier, CAS contention, `AtomicBool` flag pattern — → `12`
- [ ] **AT06** — LLVM emission tests: tier-1 single-instruction guarantees, tier-3 lock-word layout — → `12`
- [ ] **AT07** — dist stdlib rebuild + package-image verification — → `12`
- [ ] **AT08** — user-facing docs: stdlib reference, LanguageReference/SKILL, book Ch24 safe-sharing section, doc sync — → `12`

## M4 — Close tooling blockers  → `03`, `ToolchainPackagingRoadmap.md`

- [ ] **T01** — parser strategy (ANTLR port / handwritten / Stark-native generator) — *blocker* — → `03`, OQ-02, OQ-03
- [ ] **T02** — bootstrap staging + snapshot compiler policy — *blocker* — → `03`, OQ-13
- [ ] **T03** — stage-aware `stark build`/`run`/`test` — *blocker* — → `03`
- [ ] **T04** — manifest parser/config in Stark (TOML) — *blocker* — → `03`, S13
- [ ] **T05** — stable `.stark/build/` artifact layout (per profile/target/stage) — *blocker* — → `03`, OQ-18
- [ ] **T06** — incremental dependency scanning / cache — *nice-to-have* — → `03`
- [ ] **T07** — package-image generation/loading in Stark — *blocker* — → `03`, S14
- [ ] **T08** — native toolchain resolver + bundled LLVM — *blocker* — → `03`, `ToolchainPackagingRoadmap.md`
- [ ] **T09** — cross-compilation target info + SDK discovery — *blocker for parity* — → `03`
- [ ] **T10** — LLVM integration decision (keep textual IR + shell-out vs bind `libLLVM`) — *blocker decision* — → `03`, OQ-12
- [ ] **T11** — stdlib package build/discovery for self-host — *blocker* — → `03`, OQ-19
- [ ] **T12** — Stark-native test runner/harness integration — *blocker* — → `03`, OQ-14
- [ ] **T13** — VS Code / editor tooling parity — *nice-to-have unless syntax changes* — → `03`, OQ-20
- [ ] **T14** — release packaging / doctor / clean-machine verification — *blocker before drop* — → `03`, `ToolchainPackagingRoadmap.md`
- [ ] **T15** — machine-readable diagnostics/logs/metrics — *workaround exists* — → `03`

## M5 — Port compiler subsystems leaf-first  → `05` (per-file checklist with effort + deps)

Port subsystem-by-subsystem; each ported subsystem gated by its ported Stark
tests running against host output first. `05` holds the exact per-file rows,
effort (S/M/L/XL), and gap dependencies.

- [ ] **Grammar & parsing** (7 files: `Stark.g4`, lexer, parser, visitors, `StarkSyntax`, `TextLiteralDecoder`) — *XL, parser-strategy-gated* — → `05`, T01
- [ ] **Entry / CLI / project driver / native toolchain** (`Program`, `CompilerCli`, `ProjectCliDriver`, `NativeToolchain`, `LlvmTargetInfo`, `compiler.csproj`→`Stark.toml`) — *XL* — → `05`
- [ ] **Core artifacts / diagnostics / pipeline** (`CompilerArtifacts`, `CompilerDiagnostics`, `CompilerPipeline`, `DefaultCompilerPipeline`, `ArtifactTextRenderer`) — *XL* — → `05`
- [ ] **Front-end models / modules / names / types** (`SyntaxModelFactory`, `ModuleResolution`, `StarkTypeResolver`, `FunctionOverloads`, `TypeCompatibilityFacts`, + small fact helpers) — *L* — → `05`
- [ ] **Type checking / semantic validation / ownership** (`TypeChecking`, `SemanticValidation`, `OwnershipValidation`, borrow-liveness, lowering-contract, range/const/enum facts, CT evaluator) — *XL* — → `05`
- [ ] **HIR / MIR lowering** (`MidLevelIrLowering` + FunctionMirBuilder partials: place/drop/switch/imported-template/CT) — *XL* — → `05`
- [ ] **SSA lowering / validation / optimization** (`SsaLowering`, `SsaIrValidation`, ~18 `SsaOptimization/*` passes) — *XL* — → `05`
- [ ] **ABI & LLVM emission** (`AbiLowering`, `LlvmIrEmitter`, `LlvmIrEmission/*`, `FunctionBodyEmitter/*` partials) — *XL* — → `05`
- [ ] **Package image** (Models, Builders, Loaders, Bridge, Shared codecs) — *XL, JSON-gated* — → `05`, S14, T07
- [ ] **Assembly / small fact helpers** (`StarkAsmArchitectureFacts`, `StarkAsmRegisterFacts`) — *S* — → `05`

## M6 — Bootstrap  → `06`

- [ ] Build **Stage1** (Stark compiler) with the C# host (Stage0) — → `06`
- [ ] Build **Stage2** with Stage1 — → `06`
- [ ] Compare Stage1/Stage2 artifacts + diagnostics for determinism — → `06`
- [ ] Run the ported Stark test suite against Stage2 (green) — → `06`
- [ ] Determinism checks: package-image ordering, symbol naming, native toolchain drift — → `06`

## M7 — Snapshot / staging strategy & drop host compiler  → `06`, `ToolchainPackagingRoadmap.md`

- [ ] Define snapshot compiler format/location + provenance/checksums — → OQ-13, `ToolchainPackagingRoadmap.md`
- [ ] Update release archives to ship snapshot toolchain + `System` package — → `ToolchainPackagingRoadmap.md`
- [ ] Document building from source via snapshot bootstrap — → `ToolchainPackagingRoadmap.md`
- [ ] `stark doctor` + clean-machine verification matrix green on supported platforms — → `ToolchainPackagingRoadmap.md`
- [ ] Remove/demote C# host from the normal build path; keep emergency recovery story — → `06`
- [ ] Editor/tooling parity confirmed (T13) before cutover — → `03`

---

## Open decisions  → `07` (full options/trade-offs)

Resolve before or during the milestone that depends on each. OQ-04 is resolved in
`11`; the four trait/dispatch decisions are settled in `10` §3; the rest remain open.

- [ ] **OQ-01** self-hosted source location (`src-stark/` vs replace `src/` vs `compiler/` package)
- [ ] **OQ-02** parser strategy (port ANTLR / handwritten / Stark-native generator) — *shapes M1–M2*
- [ ] **OQ-03** ANTLR version mismatch (runtime 4.13.1 vs generated 4.13.2) — *note: local regen now produces 4.13.2*
- [x] **OQ-04** error-propagation surface — **resolved (doc `11`, v2):** `try` (visible early-return) + `[Ok]`/`[Err]` role attributes (propagatable types — structural, no privileged stdlib types) + `from` (conversion on the error type); not a trailing `?`-sigil, not switch-only — *sub-questions OQ-EP1..7 in `11`*
- [ ] **OQ-05** compiler invariant-failure policy (Trap/Unreachable/Assert vs Result vs unsafe trap)
- [ ] **OQ-06** optional/null replacement (`Option<T>` vs project enums vs unsafe nullable raw)
- [ ] **OQ-07** BigInt scope (true arbitrary precision vs `i1024` facade vs hybrid)
- [ ] **OQ-08** generic hashing/equality (doctrine surface vs compiler-specific dicts vs intern-to-int)
- [ ] **OQ-09** package-image format (keep `.starkpkg.json` vs binary vs dual)
- [ ] **OQ-10** TOML strategy (general parser vs small manifest parser vs format change)
- [ ] **OQ-11** build-driver concurrency (sync first vs threads/channels now vs `async`)
- [ ] **OQ-12** LLVM integration (textual + shell-out vs `libLLVM` vs both)
- [ ] **OQ-13** bootstrap snapshot policy (checked-in artifact vs prev-release builds-next vs permanent C# stage0)
- [ ] **OQ-14** Stark test-runner design (generated `main` from `[Fact]` vs runtime discovery vs handwritten)
- [ ] **OQ-15** test artifact access (machine-readable CLI vs compiler-as-library vs text-only)
- [ ] **OQ-16** IR memory model (arena + handles vs ref-counted vs owned trees + cross-ref tables)
- [ ] **OQ-17** alias/noalias misuse policy (all compile-time diagnostics vs narrow unsafe escape vs backend-catch)
- [ ] **OQ-18** `.stark/build/` layout (formal per-profile/target/stage vs ad hoc vs per-project)
- [ ] **OQ-19** stdlib/package discovery (bundled-next-to-compiler vs source-tree vs explicit path)
- [ ] **OQ-20** VS Code/editor blocking status (required before M7 vs track-not-block vs only-if-syntax-changes)

**Settled (in `10` §3):** dynamic dispatch = `dyn` over a visible vtable (+ optional
roll-your-own) · call disclosure = type-spelling only · impl syntax = C#-style base
list · receiver model = `<qualifier> Self self`.

**Settled (in `11`):** error propagation = `try` leading keyword (no trailing `?`) ·
propagatable types = **`[Ok]`/`[Err]` variant role attributes on any two-variant
enum** (v2 — never by type name, never by stdlib identity; stdlib `Result`/`Option`
are ordinary annotated enums) · conversion = `from`-on-variant, declared once on the
error type (From-on-target; declaration syntax, not an attribute) · per-call `maperr`
rejected as the default · conversion is marker-backed (no surfaced `FromError<E>`
trait yet, OQ-EP1) · innate attributes are strict (unknown attribute = compile
error, OQ-EP7).

---

## How to keep this current

When a task lands: flip its box to `[x]` (or `[~]` for partial, noting what
remains inline), update the **Status at a glance** table and **Counts**, and add
detail/dated notes to the relevant companion doc rather than here. This file stays
terse; the `NN` docs hold the depth.
