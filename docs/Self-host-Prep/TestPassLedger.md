# Self-Host-Prep Test Pass Ledger

This document is the historical triage/progress ledger for ported-test pass
state. It is reference material for fixing failures, not the authoritative task
list. Keep executable task ordering in [TASKS.md](TASKS.md); update this ledger
only when rebaselining suites, recording a new failure-family classification, or
capturing details that would otherwise clutter the task list.

Use [TASKS.md](TASKS.md) for compact task and subtask checkboxes, not for
failure evidence, run logs, or status-update prose.

---

## Baseline Snapshot

Porting is effectively done (2637/2638). The remaining test work is making the
ported facts pass on macOS. All 19 suites were baselined with clean
`rm -rf build && stark test` runs on 2026-06-19.

Summary: ~2796 / 3143 run-facts passing (~89%). 13 of 19 suites are 100% green.
347 failures live in 6 suites. Counts are runner `ok`/`FAILED`; `[Theory]`
rows expand, so run-fact totals differ slightly from static `[Fact]` counts.

| Suite | Passing | Failing | Notes |
|---|---:|---:|---|
| compiler.Tests | 1090 | **112** | largest suite: semantic/lowering diagnostics, type-checking, ownership, pipeline, runtime, package-image, CLI, examples |
| compiler.SsaTests | 346 | **61** | SSA lowering / validation / optimization text. ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + InlineSsa families are green; remaining failures are per-family text-format/source-port fixes plus a few cross-module cases needing `CompileSsaWithModule` |
| compiler.LlvmTests | 484 | **9** | fully triaged; ConfiguredTargetInfo datalayout now green |
| stdlib.Port | 177 | **50** | stdlib behavior ports |
| compiler.MirTests | 101 | **36** | MIR lowering text |
| compiler.FeatureTests | 212 | **1** | one stray failure |
| selfhost.Ir | 122 | 0 | green |
| selfhost.Binding | 82 | 0 | green |
| stdlib.Text | 59 | 0 | green |
| stdlib.Toml | 55 | 0 | green |
| selfhost.Parsing | 51 | 0 | green |
| stdlib.Testing | 34 | 0 | green |
| selfhost.Lexing | 18 | 0 | green |
| stdlib.IO.Path | 12 | 0 | green |
| stdlib.FileSystem | 10 | 0 | green |
| stdlib.Collections.Arena | 9 | 0 | green |
| selfhost.Typing | 5 | 0 | green |
| stdlib.Collections.Slice | 4 | 0 | green |
| stdlib.Json | 3 | 0 | green |

Suites still needing work:

- compiler.SsaTests: 346/407, 61 failing. ArithmeticFold + ValueFacts +
  AliasAware + ScopedNoAlias + InlineSsa are done and verified; same
  raw-vs-artifact-selection class as LlvmTests plus source-port fixes. Remaining
  families: Cleanup*, ScalarReplacement, FunctionAddress, ConstantText,
  TextView, DynamicStorage, and 2 cross-module InlineSsa.
- compiler.Tests: 1090/1202, 112 failing; broad suite needing failure-family
  subcategorization.
- stdlib.Port: 177/227, 50 failing.
- compiler.MirTests: 101/137, 36 failing; MIR text.
- compiler.LlvmTests: stale 2026-06-19 count was 483/493. Package-image
  helper and callable-value residues were fixed by targeted runs; no full-suite
  rebaseline was run because broad sweeps are intentionally avoided.
- compiler.Tests package-image typed-body integration ports now use typed-only
  package images and the shared helper restores CLI stdout, emitted-file,
  package-JSON typed-body, source-deletion, executable, and runtime exit-code
  assertions. Targeted direct probes for power, comparison-chain, and
  terminal-if package consume paths succeeded with zero diagnostics; a manual
  package-runtime power probe exited 81 after deleting the producer source; all
  `PackageImageTyped*IntegrationTests` source files pass single-file checks.
  A tiny direct executable probe that imports `CompilerTestSupport` and calls
  the package runtime helper now compiles and exits 0 after the ABI duplicate
  signature check was made structural for nested callback types. The generated
  `compiler.Tests` project runner was not rebaselined because broad sweeps are
  intentionally avoided.
- compiler.FeatureTests: 212/213, 1 failing.

Already green, no task: selfhost.Ir, selfhost.Binding, selfhost.Parsing,
selfhost.Lexing, selfhost.Typing, stdlib.Text, stdlib.Toml, stdlib.Testing,
stdlib.IO.Path, stdlib.FileSystem, stdlib.Collections.Arena,
stdlib.Collections.Slice, stdlib.Json.

---

## 2026-06-22 Target Pinning And Platform Gates

- Completed the `stdlib.Port` non-macOS target-pin/platform-gate pass. Artifact
  probes now use explicit Linux/Windows triples plus `STARK_PATH` source-stdlib
  resolution, and runtime/native behavior tests that require a real foreign
  platform are `[Platform(...)]` gated with source comments.
- Added a seeded target+`STARK_PATH` host-test wrapper for imported inline-clone
  probes whose platform helper bodies must remain visible in LLVM text.
- Narrow verification run:
  - `--check tests-stark/stdlib.Port/StdlibPortTests.stark --target arm64-apple-macosx26.0.0 --no-stark-path -I tests-stark/stdlib.Port -I stdlib/src`: passed.
  - `stark test --collection net-tcp --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --collection syscall --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --collection runtime-platform-linux --target arm64-apple-macosx26.0.0`: passed.
  - Direct host-test inspect for the three fixed Windows runtime-platform probes
    (`windows-path-behavior-wide-normalization`,
    `windows-dispatch-process-exit-no-symbol-collision`,
    `windows-dispatch-template-mirrors-linux-surface`): all compiled with zero
    diagnostics and rendered LLVM.
- Not a rebaseline: grouped `runtime-platform-windows` and grouped
  `standard-library-generic,io-file-runtime,io-path,memory,threading` runner
  checks were interrupted after proving too slow for targeted feedback; no
  broad suite sweep was run.

## 2026-06-22 SSA Cleanup Source-Port Fixes

- Fixed five `compiler.SsaTests` cleanup/source-port facts without a broad
  sweep: algebraic identities now inspect optimized SSA operator absence, the
  non-zero divide/modulo source uses an unsigned non-negative range, and three
  fixed-array fixtures use Stark's `T[N]` syntax.
- Narrow verification run:
  - `stark test --filter CleanupRemovesIntegerAlgebraicIdentities --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupRemovesSameOperandDivisionAndModuloWhenRangeExcludesZero --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupForwardsAggregateIndexThroughPhiWhenIncomingElementsMatch --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupForwardsAggregateIndexThroughSelectWhenSelectedElementsMatch --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupRemovesUnusedLocalStorageScaffolding --target arm64-apple-macosx26.0.0`: passed.

---

## compiler.LlvmTests Residue

- package-image (#4): mechanism built and proven with `CompileLlvmWithPackage`.
  All 9 ported compiler.LlvmTests package-image facts are green, including the
  4 `PackageImageBacked*` callable-value tests. The helper now builds package
  images and consumers with explicit matching target/data-layout facts.
  Typed-only package codegen is now available through `--package-typed-only`
  and the Stark host-test package builder switch; the reduced manifest-backed
  compiler assertions have source-level runtime/CLI equivalents restored.
- Flag/datalayout/source-backed LLVM residues are done:
  `ImmutableGlobalsWithoutAddressTaken`,
  `InternalizedImmutableGlobals`, `RootFunctionSymbolIsQualified`,
  `LibraryBuildQualifies`, `ExecutableInternalization`,
  `ConfiguredTargetInfoIsEmittedInHeader`,
  `LibraryBuildQualifiesPublicRootSymbols`,
  `ModulePrivateFunctionsLowerWithInternalLinkage`,
  `FunctionPointerCallSiteEffectAttributesFollowPointerKind`,
  `OptimizedDynamicStorageReserveNoop`, `DynamicStorageMoveAtEmitsDirectLengthUpdate`,
  `DirectoryEnumerationDoesNotExposeLargeDirectoryPayloadAsSsaValue`,
  `MemoryCopyFillHotLoopUsesInfallibleHelpers`,
  `TextFormattingBenchmarksSpecializeConstantIntegerFormatting`, and
  `WhitespaceOnlyLinesShorterThanTheClosingIndentation`.

---

## Failure Families

The 2026-06-19 sweep grouped the failures around a few broad levers rather than
hundreds of unrelated fixes. `compiler.FeatureTests`' lone failure did not
reproduce on rerun, leaving 5 main suites.

Cross-cutting levers:

- Package-image input, PAINPOINTS #4: roughly 39 tests across
  `compiler.Tests` ManifestBacked/PackageImage and `compiler.LlvmTests`
  PackageImage/Manifest. One protocol feature unblocks the group.
- SSA/MIR text alignment, PAINPOINTS #11 reframed: roughly 145 tests left across
  `compiler.SsaTests` and `compiler.MirTests`. The `optimized-ssa`/`mir`
  artifacts already carry operands, block labels, and typed terminators. Most
  failures are wrong-artifact-selection plus wrong-fragment-spelling, like the
  LLVM raw-vs-normalized gap. ArithmeticFold proved the method: request the
  artifact the assertion reads and spell fragments as they render.
- Target-triple pinning / platform gating: roughly 16 `stdlib.Port`
  `StdLibSourceLinux*`/`*Windows*` tests assert non-macOS syscall/codegen paths.
  Artifact/codegen-only tests may cross-target compile on macOS and assert
  emitted output. Tests that require a real foreign SDK, linker, syscall
  surface, execution, or native runtime behavior should be platform-gated with a
  source comment explaining the platform-only pass condition.
- Option toggles, PAINPOINTS #10: 6 LlvmTests; bridge half landed.

compiler.SsaTests detail:

- Done and verified: ArithmeticFold 24, ValueFacts 43-green/17-fixed,
  AliasAware 13, ScopedNoAlias 5.
- Fix classes seen:
  - Artifact selection: optimization-pass result lands in `optimized-ssa`, not
    terse `ssa`; switch `CompileSsaAfter` to `CompileSsaAfterOptimized` and
    `SsaContains`/`!SsaContains` to `OptimizedSsaContains`/`OptimizedSsaLacks`.
  - Source ports: common rewrites include `T~` to `dynamic`/`List<T>`, `*T` and
    `*mut T` to `rawptr<T>`/`rawmutptr<T>`, `#[ElementCount(n)] *T` to bounded
    `rawptr<T>[n]`, `as Type` to `(Type)(expr)`, raw-pointer functions marked
    `unsafe`, readonly-rawptr writes changed to rawmutptr, minimal-width
    non-negative ranges, `(unicode)"..."` literals, and removing redundant
    `where disjoint`.
- Cleanup partial: 3 of 12 green. Remaining cases are murky/source-port.
- Remaining 61 classified:
  - 17 source-ok text-class tests: probe `ssa` vs `optimized-ssa` for the
    asserted fragment and switch artifact/spelling. Watch
    `CleanupRemovesIntegerAlgebraic` and `CleanupRemovesRedundantSameType`;
    verify whether surviving binaries at `cleanup-ssa` are a real
    under-optimization before respelling.
  - About 28 `STK1000` parse-error `*FailsBeforeLlvmEmission` tests are
    SSA-validator unit tests whose C# originals hand-build invalid SSA modules.
    Keep coverage by adding a structured test-only invalid-IR fixture path; use
    explicit exclusions only for C# host-internal object-shape tests that do not
    map to a self-hosted IR invariant.
  - About 16 type/range source ports are fixable like ValueFacts/AliasAware
    where the shape is source-expressible.
- InlineSsa done: 10 of 12 green. Added
  `System.Testing.SsaFunctionBody(ascii ssaText, ascii fnName)` and
  `OptimizedSsaFunctionLacks/Contains`. Two cross-module failures remain and
  need a `CompileSsaWithModule`/staging harness path.

Other suite notes:

- compiler.Tests: about 30 package-image failures; remainder includes
  AsmDeclarations, LawBodies, CheckMode, BuildUses, EmitLlvm/EmitExecutable,
  TextDiagnostics/SystemText/RuntimeText/LawFunctions, and a long tail.
- stdlib.Port: 41 `StdLibSource*` lowering/intrinsic/syscall-path assertions,
  roughly 16 Linux/Windows platform-specific tests, WindowsDispatch 2,
  SourceStd 2, and miscellaneous cases.
- compiler.MirTests: MIR text/structural failures around MultiLabel,
  EnumSwitch, SwitchSections, RawPointer, NestedLvalue/Generic, LargeAggregate,
  TextLiteral, and mostly 1-2 test families.

---

## macOS Pass-Bar Decision

The macOS pass bar includes tests runnable on macOS plus artifact/codegen-only
cross-target tests whose expected Linux/Windows output can be asserted without a
foreign SDK/linker/runtime. Tests that need real non-macOS platform facilities
are excluded from the macOS pass bar by platform gating, and should carry
comments explaining which platform is required.
