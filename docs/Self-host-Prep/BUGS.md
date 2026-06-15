# Stark Self-Hosting — Bug Triage Log

Bugs discovered while turning `PAIN_POINTS.md` into landed fixes. Each entry is for
*later triage*: a concrete description, a minimal repro path where known, the suspect
files, and a recommended next step. This is not a fix log — fixed items move to the
commit history; this file tracks what is **known-broken or deferred**.

Companion: [PAIN_POINTS.md](PAIN_POINTS.md) (the ergonomics roadmap these fixes came from).

---

## DEFERRED (deep) — `dynamic`-storage heap corruption in test executables (PAIN_POINTS #6)

**Status:** confirmed real, root-cause investigation deferred (multi-day, spans 4 subsystems). Flagged, not fixed.

**Symptom:** code compiled directly INTO a test executable corrupts the heap when it
grows `dynamic` storage (forces a `Reserve`/`realloc`); the identical operations are
correct when compiled into the stdlib *package*. The workaround discipline is baked into
live suites — e.g. `tests-stark/stdlib.Text/TextBuilderTests.stark:12-22` documents
"code compiled INTO the test executable crashes when it mutates `dynamic` storage (heap
corruption on growth)" and routes all building through stdlib entry points, with `[Fact]`
code only reading results.

**Minimal repro plan (not yet reduced to a committed test):**
1. A test project (kind=`test`, dep on the stdlib package image) whose single `[Fact]`
   does dynamic growth directly (e.g. `stack mut OwnedAscii s = ...; AppendU64(s, …)`
   enough times to force at least one `Reserve` grow), plus a twin where the same growth
   is wrapped in an `internal fn` in the stdlib and the `[Fact]` only reads. Both build
   through `ProjectCliDriver.BuildProjectAsync` (src/Compiler/ProjectCliDriver.cs:407-538)
   as one self-contained LLVM module importing the stdlib `.starkpkg`. Dump IR with
   `--save-temps` (ProjectCliDriver.cs:462) and diff the grow site.

**Prime suspect (most contained hypothesis):** the dynamic-storage SSA fact tracker runs
in `CompilerPhase.Lowering` for ALL opt levels incl. the test profile opt=0
(`DefaultCompilerPipeline.cs:4935-4968`, `OptimizeSsaDynamicStoragePass`).
`SsaDynamicStorageOptimizer.InvalidateDirectCallDynamicStorageFacts`
(src/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.cs:523-564) decides whether a
call invalidates the cached `(ptr,len,cap)` facts via
`SsaDynamicStorageCallFactPolicy.ShouldPreserveDirectCallArgumentDynamicFacts`
(src/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.cs:51-66), keyed off
`directCallParameterEffects`. `BuildDirectCallParameterEffects` (same file, 5-49) sources
effects from `semanticValidation.Functions` for *local* fns but from
`packageImageFacts.FunctionSemantics` for *imported* fns, with a template-name→`SymbolName`
remap (lines 32-38). A missing/mis-keyed imported grow-helper effect would leave stale
`(ptr,len,cap)` facts un-invalidated after an imported call reallocs the storage → a
later in-exe grow reallocs from a dangling base pointer → heap corruption. The in-package
build has the same effects locally, so it stays correct. Parameter effects DO round-trip
through the package image (`CompilerFactsSectionBuilder.cs:164-201` write;
`CompilerFactsSectionLoader.cs:1452-1517` read), so this would be a coverage/keying gap,
not total absence.

**Secondary suspects:** inline-vs-helper grow IR divergence
(`LlvmFunctionBodyEmitter.DynamicStorageAndAddresses.cs:150-378` inline path vs
`@__stark_dynamic_reserve` out-of-line helper in `LlvmBuiltinAndHelperEmitter.cs:584+`,
chosen by `CanSplitCurrentBlockForCallSiteControlFlow()`); allocator header/bucket
mismatch between alloc-path (`LlvmBuiltinAndHelperEmitter.cs:996-1000`) and realloc
OS-fast-path header rewrites (1259-1296); allocator bucket globals are `linkonce_odr
hidden` on macOS where `SupportsComdat()` is false (1796-1802) — Roadmap.md:1797 notes the
allocator was historically `weak_odr`, a possible regression to check.

**Recommended next step:** reduce the minimal repro, capture in-exe vs in-package IR for
the grow site, bisect to one of the suspects above, then add a permanent `[Fact]`/
`compiler.StandardLibraryTests` fixture that grows `dynamic` storage in-fact and asserts
on it under a memory checker. **Do NOT touch src/Compiler/TypeChecking.cs** (unrelated).

---

## FIXED (compiler) — f64/f32 constant emission renders invalid LLVM IR for integral / scientific values

**Status:** FIXED. The float→LLVM-IR text formatter now emits every f64/f32 constant
as a bit-exact hex float (`0xH…`), so integral and scientific values are no longer
rendered as `double 1` / `double 1E+17`. The stdlib workaround was removed: `ParseF64Ascii`
now indexes a real `const f64[23] ParseF64Pow10 = { 1e0 .. 1e22 };` table.

**Fix (landed; uncommitted in the working tree):**
- New helper `src/Compiler/LlvmIrEmission/LlvmFloatLiteral.cs` (`Render(double, bitWidth)`
  / `TryRenderLiteralText(text, bitWidth, out rendered)`) renders the IEEE-754 bit pattern
  as `0x` + 16 uppercase hex digits (for f32, the bit pattern of `(double)(float)value`,
  which zeroes the low 29 mantissa bits as LLVM requires).
- **Scalar path:** `LlvmFunctionBodyEmitter.FormatFloatLiteral`
  (`FunctionBodyEmitter/LlvmFunctionBodyEmitter.AbiAndStorage.cs`) routes all finite values
  through `LlvmFloatLiteral.Render` (the NaN/Inf branch already emitted hex; this unifies them).
- **Array / global-initializer path:** `LlvmGlobalInitializerPlanner` (`LlvmGlobalInitializerPlanner.cs`)
  re-renders all three float-text sites (`TypedConstantInitializerKind.Float`,
  `CompileTimeConstantKind.Float`, literal-context `FloatLiteral`) via a new `RenderFloatLiteral`
  helper. NOTE: `CompileTimeExpressionEvaluator.FormatFloatLiteral`/`StripFloatSuffix` were left
  untouched on purpose — that text also feeds MIR float operands and interpolation, which are
  not LLVM IR; only the LLVM emission boundary was changed.

**Verification:** `tests/compiler.Tests/LlvmIrEmissionTests.IntegralAndScientificFloatConstantsEmitValidHexFloatLiterals`
(IR-text) and `tests/compiler.Tests/FloatConstantEmissionRuntimeTests` (full `--emit-exe`
through the real LLVM backend, the original `integer constant must have integer type` repro)
both pass. `tests-stark/stdlib.Text` passes all `ParseF64Ascii`/`ParseF32` facts at opt=0 and
opt=3. The hex IR was independently validated with `llvm-as`.

The remainder of this entry records the original symptom for history.

**Original status:** confirmed real, found while landing `System.Text.ParseF64Ascii`
(PAIN_POINTS #8); worked around in stdlib (no compiler change).

**Symptom:** the LLVM IR emitter prints f64 constants whose value is integral, or whose
magnitude switches to scientific notation, WITHOUT a decimal point — e.g. `double 1`,
`double 10`, `double 1E+17`. LLVM rejects all of these (`error: integer constant must have
integer type`; a float literal needs a `.`, e.g. `1.0`, or a hex float `0xH…`).

**Two flavors observed:**
1. **Fixed-array f64 constant** — `const f64[23] Pow10 = { 1e0, 1e1, …, 1e22 };` emits
   `[23 x double] [double 1, double 10, …, double 1E+17, …]`: integral elements print as
   bare integers, `>= 1e17` elements as `1E+17` — both invalid (hit at
   `…/obj/.._.._stdlib/System_Text.ll:125`).
2. **Scalar f64 literal in scientific range** — `return 1e17;` emits `ret double 1E+17`
   (invalid). Decimal-range integral literals emit fine (`ret double 1.0`,
   `ret double 10000000000000000.0`); the break is at `1e17` where the formatter switches
   to `1E+17`.

**Root cause (likely):** the float→LLVM-IR text formatter (`src/Compiler/LlvmIrEmission/`,
constant-array path in the global-initializer/module-surface emitter, scalar path in the
function-body emitter) uses a numeric `ToString` that drops the `.0` for integral values
and emits bare `E+NN` scientific notation — neither is valid LLVM float-literal syntax.

**Correct fix:** emit f64/f32 constants as **hex floats** (`0xH…`, bit-exact and LLVM's
recommended form) or always with a fractional `.0` and a lowercase decimal-point mantissa
(`1.0e+17`). Hex floats round-trip every value exactly.

**Former workaround in stdlib (now REMOVED):** `stdlib/src/System/Text.stark` had
`ParseF64Pow10Low` (a `switch` over scalar f64 literals in plain *decimal* form for
`10**0 .. 10**16`) and `ParseF64Pow10Window` (reaching `10**17 .. 10**22` by multiplying
two such exact decimal-form entries at runtime) so no `1e17`-style literal and no f64
fixed-array constant was ever emitted. Both are now deleted; `ParseF64Ascii` indexes the
`const f64[23] ParseF64Pow10 = { 1e0 .. 1e22 };` table directly, since the compiler emits it
correctly. Behavior is identical (same Clinger exact window, same Overflow outside it).

---

## PARTLY FIXED — `BoundOperationKey.FilePath` couples in-process lowering to root input path (PAIN_POINTS #1)

**Status:** step 1 (remove `FilePath` from the key) LANDED & full-suite-verified (FOLLOWUP
#53); the lock-in regression test landed earlier; only step 2 (module-aware
`Location()`) remains deferred. See the step breakdown below.

**Finding (corrects the pain point's framing):** the package-image *serialized* template
member-call path is ALREADY file-path-independent — `BuildPublishedTemplateMemberCalls`
(GenericTemplateSectionBuilder.cs:5461-5531) assigns ordinals by syntactic-walk index;
signature match is `TemplateDirectCallFacts.BuildLookupKey = "{line}:{column}"`
(CompilerArtifacts.cs:3436-3444, NO FilePath); the consumer bridge keys by `int Ordinal`
only (PackageImageSourceBridge.cs:1217-1218). So the pain point's fear ("serialized
ordinal embeds a file path") does **not** exist.

**The real coupling** is in-process: `TypeChecker.Location()` (TypeChecking.cs:24350-24363)
stamps the single root `_context.Input.FilePath` on EVERY checked body; `RecordMemberCall`
(11418-11438) puts that on `BoundMemberCallOperation`; `BoundOperationKey`
(MidLevelIrLowering.cs:14) embeds `FilePath`; the lowering matcher
(FunctionMirBuilder.cs:12298-12361) compares against the PER-MODULE
`loweringContext.FilePath`. It works ONLY because `Location()` uniformly uses the root
path. The reverted "F2" change made `Location()` module-aware → desynced the app-side
`List<i32>.Push` lookup → exception caught as the generic STK9999 "Pass 'lower-mir'
crashed" (CompilerPipeline.cs:377).

**Step 1 — LANDED & verified (FOLLOWUP #53):** `FilePath` is removed from
`BoundOperationKey` identity; the map now matches purely by `(EnclosingFunctionName, Line,
Column)`. Updated `BoundOperationKey` (record fields), `BuildBoundOperationMap`
(MidLevelIrLowering.cs), and the matcher + `BoundOperationLookupKeys` (collapsing the
`_moduleFilePath` probe and the null-FilePath fallback into a single lookup,
FunctionMirBuilder.cs). This was safe because `Location()` currently stamps a single
constant root path, so the FilePath field disambiguated nothing — verified by
`compiler.Tests` **1717/1717** (incl. the cross-module lock-in test
`ImportedGenericMemberCallLowersAcrossModulesWithoutInvariantViolation` + the package-image
smoke test) and `compiler.StandardLibraryTests` at exactly the **11-test baseline** (zero
new failures). The F2-class fragility is gone: a future module-aware `Location()` can no
longer desync this in-process binding.

**Step 2 — still DEFERRED (separate change):** make diagnostic `Location()` module-aware
for correct per-module attribution. Now unblocked by step 1, but it is the riskier half
(it changes what file path every record stamps); do it as its own change with its own
full-suite run. **Never combine the two steps.**

**Landed mitigation:** `tests/compiler.Tests/...` now has a lock-in test that compiles a
cross-module imported-generic member call at `StopAfterPassId:"lower-mir"` and asserts
success, so a future `Location()` regression fails loudly with a named test instead of a
bare STK9999 (which is itself now enriched — see PAIN_POINTS #2 fix).

---

## PRE-EXISTING — WIP baseline test failures at `de225a9` (24 C# tests; triage reference)

These were failing BEFORE this work began (proven last session by an isolated baseline
comparison at the pre-merge commit `de225a9`) and are unrelated to the PAIN_POINTS fixes.
`compiler.Tests` is fully green (1708/1708 at `de225a9`; 1717/1717 in the current working
tree with this session's added tests). Recorded here so integration regressions can
be distinguished from this baseline. Several are stdlib-source linters worth a dedicated
triage pass.

- **compiler.FeatureTests (1):** `ComptimeFeatureTests.ComptimeIndexedEnumVariantFactsFoldToConstants`
- **compiler.PipelineTests (7):** `SourceBackedImportedInlineFunctionsCloneModulePrivateCalleeDependencies`, `SourceBackedImportedInlineFunctionsDeclareModulePrivateConstsUsedByClone`, `ImportedSourceModulesWithPrivateHelpersAndStringLiteralsLowerIntoMirAndSsa`, `ManifestBackedModulesPreservePublishedOwnershipFactsFromCompilerFactSections`, `InlineSsaPreservesOwnershipSummariesOnRewrittenFunctions`, `OwnershipTrafficSsaElidesDeadAggregateMoveTrafficForNonEscapedRoots`, `OwnershipTrafficSsaKeepsMoveInvalidationForRawEscapedRoots`
- **compiler.IntegrationTests (5):** `CompilerCliTests.HelpOutputGroupsOptionsByWorkflow`, `CompilerCliTests.TextDiagnosticsDoNotRepeatTheSameOwnershipMoveError`, `CompilerCliTests.TextDiagnosticsRenderSourceSnippetsForInfoNotesToo`, `DynTraitObjectRuntimeTests.BorrowDynTraitObjectDispatchesPolymorphicallyAtRuntime`, `StructLayoutInteropRuntimeTests.CStructLayoutAttributesMatchNativeCFixturesAtRuntime`
- **compiler.StandardLibraryTests (11):** `PackagedStdLibCommonErrorResultModelWorksWithoutSource`, `StdLibPackageBuildsFromRepositorySources`, `StdLibSourceCommonErrorResultModelUsesCompactEnumLayouts`, `StdLibSourceDictionaryCustomKeysUseExplicitStaticHashAndEqualsContract`, `StdLibSourceHashSetCustomKeysUseExplicitStaticHashAndEqualsContract`, `SourceStdLibAtomicWholeFileHelpersReplaceOnLinux`, `StdLibSourceMemoryModuleLowersRuntimeDisjointAppendFastPaths`, `StdLibSourceNetTcpCloseRoutesOpenHandlesThroughPlatformSocketClose`, `StdLibSourceUsesCanonicalRangeNotation`, `StdLibRawPointerUseStaysInDocumentedBoundaryFiles`, `StdLibSourceThreadingSurfaceSupportsThreadEntryAndSchedulerCalls`

Notable for stdlib hygiene: `StdLibRawPointerUseStaysInDocumentedBoundaryFiles` fails
because `System/Json.stark`, `System/Toml.stark`, and `System/Testing/HostCompiler.stark`
use raw pointers not listed in `docs/Internals/StandardLibraryRawPointerBoundaries.md`;
`StdLibSourceUsesCanonicalRangeNotation` enforces the no-half-open-interval rule on stdlib
source (new stdlib code must comply).

---
