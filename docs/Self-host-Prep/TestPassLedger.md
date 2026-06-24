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
`rm -rf build && stark test` runs on 2026-06-19. `compiler.FeatureTests` and
`compiler.LlvmTests` were rechecked by targeted full-project runs on 2026-06-23.

Summary: at least 2842 / 3143 run-facts passing (~90%). 15 of 19 suites are
known 100% green. At most 301 failures live in 4 suites. Counts are runner
`ok`/`FAILED`; `[Theory]` rows expand, so run-fact totals differ slightly from
static `[Fact]` counts. Non-feature/non-LLVM failing-suite counts remain the
2026-06-19 baseline unless their notes say otherwise.

| Suite | Passing | Failing | Notes |
|---|---:|---:|---|
| compiler.Tests | 1090 | **112** | largest suite: semantic/lowering diagnostics, type-checking, ownership, pipeline, runtime, package-image, CLI, examples |
| compiler.SsaTests | 346 | **61** | SSA lowering / validation / optimization text. ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + Cleanup + ScalarReplacement + InlineSsa + FunctionAddress + ConstantText + TextView + DynamicStorage families are green by targeted filters; count predates recent targeted fixes |
| compiler.LlvmTests | 493 | 0 | green by 2026-06-23 targeted project rerun |
| stdlib.Port | 213 | **14** | stdlib behavior ports; count includes 2026-06-23 targeted `io-path`, `io-file`, `io-file-runtime`, `memory-helper`, `memory`, `collections-dictionary`, `collections-hash-set-sort`, `collections`, `text`, `promoted-runtime-buffer`, `promoted-console`, `promoted-net-tcp`, `process`, `memory-contract-audit`, `raw-pointer-audit`, `range-notation`, and `runtime-platform-mac-os` fixes but no full-suite rebaseline |
| compiler.MirTests | 101 | **36** | MIR lowering text; count predates recent switch-pattern, place-lowerer, generic, and lowering-contract targeted fixes |
| compiler.FeatureTests | 213 | 0 | green by 2026-06-23 targeted project rerun |
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

- compiler.SsaTests: 346/407, 61 failing before recent targeted fixes.
  ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + Cleanup +
  ScalarReplacement + InlineSsa + FunctionAddress + ConstantText + TextView +
  DynamicStorage are done and verified by targeted filters. No full-suite
  rebaseline was run because broad sweeps are intentionally avoided.
- compiler.Tests: 1090/1202, 112 failing; broad suite needing failure-family
  subcategorization.
- stdlib.Port: at least 213/227, at most 14 failing after the 2026-06-23
  targeted `io-path`, `io-file`, `io-file-runtime`, `memory-helper`, and
  `memory` fixes plus the targeted collection fixes.
- compiler.MirTests: 101/137, 36 failing before recent targeted fixes.
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

Already green, no task: compiler.FeatureTests, compiler.LlvmTests,
selfhost.Ir, selfhost.Binding, selfhost.Parsing, selfhost.Lexing,
selfhost.Typing, stdlib.Text, stdlib.Toml, stdlib.Testing, stdlib.IO.Path,
stdlib.FileSystem, stdlib.Collections.Arena, stdlib.Collections.Slice,
stdlib.Json.

---

## 2026-06-23 Feature Tests Recheck

- Reproduced and fixed the lone `compiler.FeatureTests` residue in
  `ComptimeIndexedEnumVariantFactsFoldToConstants`.
- The embedded source now returns `u64[0 max]`, matching
  `System.Compiler.EnumVariantPayloadCount` while preserving the LLVM
  `ret i64 31` expectation.
- Narrow verification: the single fact passed with `--filter`, and the full
  `compiler.FeatureTests` project passed on `arm64-apple-macosx26.0.0`.
- No broad suite sweep was run.

---

## 2026-06-23 LLVM Tests Recheck

- Rechecked `compiler.LlvmTests` after the known package-image and option-toggle
  residues had landed; the full project now passes on `arm64-apple-macosx26.0.0`.
- Fixed the host-test runner so an empty request target still carries the
  detected target into `CompilerOptions`, not just stdlib resolution.
- Kept Linux/x86 LLVM assertions strong by pinning artifact-only COMDAT/coldcc
  tests to `x86_64-unknown-linux-gnu` and using source-stdlib resolution for
  Linux benchmark probes.
- Updated call-site expectations where lowering now preserves stronger backend
  facts, including raw-pointer count ranges and imported asm argument facts.
- Narrow verification: `dotnet build src/compiler.csproj --no-restore` passed,
  then `../../stark test --target arm64-apple-macosx26.0.0` passed in
  `tests-stark/compiler.LlvmTests`. No broad suite sweep was run.

---

## 2026-06-23 Stdlib Port Recheck

- `standard-library-generic` passed as a targeted `stdlib.Port` collection.
- Fixed `StdLibSourcePromotedPathLowersThroughDynamicStorage` by pinning the
  artifact probe to `x86_64-unknown-linux-gnu`, preserving the original
  libc-free dynamic-storage oracle.
- The `io-path` collection now passes on `arm64-apple-macosx26.0.0`; no broad
  `stdlib.Port` sweep was run.
- Fixed the `io-file` collection by compiling `stdlib/src/System/IO/File.stark`
  directly for the file flush/buffering LLVM probes. The buffered ASCII copy
  probe is pinned to `x86_64-unknown-linux-gnu`, preserving the target-specific
  `rep movsb` inline-asm oracle.
- Narrow verification: `../../stark test --collection io-file --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Added source-path compilation to the Stark host-test bridge so artifact probes
  can compile `stdlib/src/System/Memory.stark` directly instead of relying on
  wrapper imports.
- Fixed the `memory-helper` collection by restoring body-scoped LLVM checks for
  memory helper overlap guards, hot-tail memcpy/memset lowering, no scalar
  fallback, and helper attributes. Infallible moves now assert the stronger
  `llvm.memmove` lowering.
- Narrow verification: `../../stark test --collection memory-helper --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `memory` collection by pinning the allocator-symbol artifact probes
  to `x86_64-unknown-linux-gnu`, preserving the no-libc Linux allocator oracle
  instead of rejecting the host macOS allocator lowering. The allocator audit
  workload now mirrors the C# helper's heap-allocation loop.
- Narrow verification: `../../stark test --collection memory --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Added target-aware source-path host-test compilation and fixed the
  `io-file-runtime` collection by compiling
  `stdlib/src/System/Runtime/Platform/Linux.stark` directly for
  `x86_64-unknown-linux-gnu`, preserving the lseek/fsync syscall oracles.
- Narrow verification: `../../stark test --collection io-file-runtime --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `threading` collection; all 17 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify which, if any, of these facts were part of the failing
  baseline bucket.
- Rechecked the `threading-atomics` collection; all 12 facts passed on
  `arm64-apple-macosx26.0.0`, including the tier-1/tier-2/tier-3 lowering
  oracles for lock-free and spinlock-protected atomic operations. Counts were
  left unchanged because the previous ledger did not identify which, if any, of
  these facts were part of the failing baseline bucket.
- Rechecked the `runtime-platform-windows` collection; 13 artifact/compile facts
  passed and the 3 Windows-runtime facts skipped on macOS by platform gate.
  Counts were left unchanged for the same conservative-accounting reason.
- Fixed the `collections-dictionary` collection by restoring body-scoped custom-key
  LLVM checks while allowing the faster inlined `Symbol.Hash`/`Symbol.Equals`
  lowering. The probe now asserts the actual inline-clone dictionary path has no
  `DictionaryKey_Hash` or `DictionaryKey_Equals` fallback dispatch.
- Narrow verification: `../../stark test --collection collections-dictionary --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `collections-hash-set-sort` collection by restoring body-scoped LLVM
  checks for sort and custom-key HashSet paths. The sort probes now assert no
  allocation, fnptr-pair extraction, or indirect closure call inside `SortFixed`,
  while HashSet accepts inlined `Symbol.Hash`/`Symbol.Equals` and rejects
  `DictionaryKey_Hash`/`DictionaryKey_Equals` fallback dispatch in the actual
  probe bodies.
- Narrow verification: `../../stark test --collection collections-hash-set-sort
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `collections-stack-queue` collection; all 5 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Fixed the `collections` collection by pinning the promoted List dynamic-storage
  LLVM oracle to `x86_64-unknown-linux-gnu`, preserving the libc-free
  `__stark_runtime_try_realloc` and `__stark_dynamic_try_reserve` assertions and
  the negative libc allocator checks.
- Narrow verification: `../../stark test --filter
  StdLibSourcePromotedListLowersThroughDynamicStorage --target
  arm64-apple-macosx26.0.0` and `../../stark test --collection collections
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `text` collection by pinning the promoted text dynamic-storage LLVM
  oracle to `x86_64-unknown-linux-gnu`, compiling `stdlib/src/System/Text.stark`
  directly for append, wide-formatting, and wide-parse backend assertions, and
  restoring the source-text scan for bounded raw-pointer region contracts.
- Narrow verification: `../../stark test --collection text --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `text-runtime` collection; all 3 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Rechecked the `text-interning` collection; all 3 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged for the same
  conservative-accounting reason.
- Fixed the `promoted-runtime-buffer` collection by compiling
  `stdlib/src/System/Runtime/Buffer.stark` directly for runtime-buffer backend
  assertions and using function-scoped LLVM body checks for disjoint write
  guards, tail-region memcpy/memset paths, and allocation-free inline fixed
  storage.
- Narrow verification: `../../stark test --collection promoted-runtime-buffer
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `promoted-console` collection by compiling
  `stdlib/src/System/Console.stark` and
  `stdlib/src/System/Runtime/Platform/Linux.stark` directly for backend
  assertions, restoring scoped LLVM checks for direct platform write paths,
  small-buffer newline coalescing, and allocation-free byte-line writes.
- Narrow verification: `../../stark test --collection promoted-console
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `promoted-io-file-system` collection and restored the C# oracle's
  source-text assertions for platform raw-pointer file IO regions, fast
  directory/file entry points, and allocation-free `System.FileSystem` storage.
  Counts were left unchanged because the previous ledger did not identify whether
  this collection contributed to the failing baseline bucket.
- Narrow verification: `../../stark test --collection promoted-io-file-system
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `promoted-net-tcp` collection by compiling
  `stdlib/src/System/Net/Tcp.stark` directly for `x86_64-unknown-linux-gnu`,
  restoring source ABI scans, and updating the dynamic-buffer LLVM body symbol
  to the current max-count-mangled name while preserving bulk read/write-slice
  fast-path checks.
- Narrow verification: `../../stark test --collection promoted-net-tcp --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `runtime-buffer` collection; both facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Rechecked the `console` collection; all 5 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged for the same
  conservative-accounting reason.
- Fixed the `process` collection by updating the `System.Process.Exit` caller
  LLVM assertions for the current trap call spelling while still requiring the
  module-level `__stark_unreachable_trap` definition to carry `cold noreturn`.
- Narrow verification: `../../stark test --collection process --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked `net`, `file-system`, `json`, `math`, `c`,
  `compiler-integer-facts`, and `backend-boundary-audit`; all selected facts
  passed on `arm64-apple-macosx26.0.0`. The `file-system` run skipped the
  Linux-only runtime facts through platform gates.
- Fixed the `memory-contract-audit` collection by restoring the C# oracle's
  direct source-text scans for explicit overlap contracts in `System.Memory`,
  `System.Text`, `System.IO.Path`, and `System.Runtime.Buffer`.
- Fixed the `raw-pointer-audit` collection by replacing compile-only reductions
  with `System.FileSystem.Glob` source-tree scans, preserving the documented
  raw-pointer boundary allowlist, checking public raw-pointer declarations, and
  asserting the root module still excludes `System.Text`/`System.Testing` raw
  surfaces while re-exporting safe public modules.
- Updated `docs/Internals/StandardLibraryRawPointerBoundaries.md` and the host
  C# allowlist for the audited `System.Json`, `System.Toml`, and
  `System.Testing.HostCompiler` internal raw-pointer files.
- Narrow verification: `../../stark test --collection memory-contract-audit
  --target arm64-apple-macosx26.0.0` and `../../stark test --collection
  raw-pointer-audit --target arm64-apple-macosx26.0.0` passed in
  `tests-stark/stdlib.Port`.
- Fixed the `range-notation` collection by canonicalizing remaining stdlib
  source spellings (`2 ** 16`, `2 ** 15 - 1`, and spaced `2 ** 53` comments)
  and replacing the compile-only Stark reduction with a real source/template
  glob audit that ignores string literals like the C# oracle.
- Narrow verification: `dotnet test
  tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj
  --no-restore --filter FullyQualifiedName~SystemRangeNotationStandardLibraryTests`,
  `../../stark test --collection range-notation --target
  arm64-apple-macosx26.0.0`, `../../stark test --collection json --target
  arm64-apple-macosx26.0.0`, and `../../stark test --collection toml --target
  arm64-apple-macosx26.0.0` passed.
- Fixed the `runtime-platform-mac-os` collection by restoring direct
  source-path compilation of `System/Runtime/Platform/MacOS.stark` for
  `arm64-apple-macosx26.0.0`, including the original libSystem declaration
  checks and scoped `stat` mode-bit LLVM body checks.
- Narrow verification: `../../stark test --collection runtime-platform-mac-os
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked `testing`, `book-sample`, and `syscall`; all selected run-facts
  passed on `arm64-apple-macosx26.0.0`, with the Linux-only packaged syscall
  fact skipped by platform gate.

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

- Closed by the 2026-06-23 targeted project rerun.
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
hundreds of unrelated fixes. `compiler.FeatureTests` and `compiler.LlvmTests`
were fixed and verified by targeted project reruns, leaving 4 main suites.

Cross-cutting levers:

- Package-image input, PAINPOINTS #4: remaining package-image residue is in
  `compiler.Tests` ManifestBacked/PackageImage paths; `compiler.LlvmTests`
  package-image facts are green after the targeted 2026-06-23 rerun.
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
compiler.SsaTests detail:

- Done and verified: ArithmeticFold 24, ValueFacts 43-green/17-fixed,
  AliasAware 13, ScopedNoAlias 5, FunctionAddress 3, ConstantText 5,
  TextView 2, DynamicStorage 28.
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
- Cleanup done. The remaining source-port issues were ranged integer spelling,
  source-valid switch shape, loop behavior spelling, and optimized-artifact
  assertions for facts that only render after cleanup.
- Pre-fix failure classification to revisit on the next rebaseline:
  - 17 source-ok text-class tests: probe `ssa` vs `optimized-ssa` for the
    asserted fragment and switch artifact/spelling. Verify whether surviving
    binaries at a stopped pass are real under-optimizations before respelling.
  - Closed 2026-06-23: the `*FailsBeforeLlvmEmission` SSA-validator unit tests
    now use the structured `validatorFixture` host-test path instead of
    source-valid placeholder ports.
  - About 16 type/range source ports are fixable like ValueFacts/AliasAware
    where the shape is source-expressible.
- InlineSsa done. Added
  `System.Testing.SsaFunctionBody(ascii ssaText, ascii fnName)` and
  `OptimizedSsaFunctionLacks/Contains`; the source-built dependency boundary now
  stages `Math.stark` through `CompileSsaAfterOptimizedWithModule`.

## 2026-06-22 SSA Source Dependency Staging

- Added SSA host-test module staging with raw filesystem temp directories so
  source-built dependency tests can pass search directories through the host
  compile protocol.
- Restored `InlineSsaOptimizesThroughSourceBuiltDependencyBoundary` to assert the
  optimized `Run` body folds to `return 42` and has no surviving `AddOne` call.
- Narrow verification:
  - `../../stark test --filter InlineSsaOptimizesThroughSourceBuiltDependencyBoundary --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter InlineSsaInlinesSmallDirectCallsAndRerunsConstantPropagation --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupSsaRemovesSameOperandIntegerComparisons --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter InlineSsaInlinesSmallModulePrivateDirectCallsWithoutExplicitInline --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA Cleanup Family

- Completed the `compiler.SsaTests` cleanup family after source-port and
  rendered-artifact fixes:
  - `CleanupRemovesRedundantSameTypeConversions` now asserts the same-type
    conversion does not survive as a rendered `convert`.
  - `CleanupReusesIdenticalMaterializedConstantConversions` uses ranged `i8` and
    asserts exactly one rendered `raw:i32` materialization.
  - `CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget` uses a source-valid
    three-value range switch with one explicit case sharing the default return.
  - `CleanupRemovesLoopInvariantSelfReferentialPhiNodes` uses `while willexit`
    and asserts optimized SSA returns `arg_limit` with the invariant phi removed.
- Narrow verification:
  - `../../stark test --filter CleanupRemovesRedundantSameTypeConversions --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupReusesIdenticalMaterializedConstantConversions --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupRemovesLoopInvariantSelfReferentialPhiNodes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter Cleanup --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA ScalarReplacement Family

- Completed the `compiler.SsaTests` scalar-replacement family after source-port
  and rendered-artifact fixes:
  - `ScalarReplacementRemovesDeadStackFieldStoresFromSource` now reads
    optimized SSA at the `sroa-ssa` stop point.
  - `ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes` marks
    the raw-pointer helper `unsafe` and asserts retained escaped stack storage.
  - Aggregate-copy ports now assert the rendered optimized facts the source path
    exposes: scalar forwarding to `arg_value`, retained escaped destination
    storage, and move-only aggregate consumption.
- Narrow verification:
  - `../../stark test --filter ScalarReplacementRemovesDeadStackFieldStoresFromSource --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsAggregateCopiesObservedByLaterFieldLoad --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsAggregateCopiesAfterDestinationAddressEscapes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsDeadAggregateMoveCopiesConservative --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacement --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA FunctionAddress Family

- Completed the `compiler.SsaTests` function-address validator source ports by
  replacing stale `func<...>` snippets with current `fnptr<unsafe fn ...>` source
  and keeping the source-expressible positive equivalents.
- Cleaned two adjacent indirect-call validation ports touched by the same stale
  callable syntax, using current fixed-array source spelling and explicit array
  initializers.
- Narrow verification:
  - `../../stark test --filter FunctionAddress --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter IndirectCall --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA ConstantText Family

- Completed the `compiler.SsaTests` constant-text formatting specialization
  family by reading the post-pass `optimized-ssa` artifact and scoping
  call-removal/call-retention checks to the `Run` function body.
- Preserved the optimizer facts from the C# oracle in rendered-text form:
  `format_const` blocks, fixed ASCII/Unicode copy widths, length stores, bool
  phi, and normalized narrowed digit stores.
- Narrow verification:
  - `../../stark test --filter ConstantText --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA TextView Family

- Completed the `compiler.SsaTests` text-view validation source ports by
  replacing non-source-visible text field reads with source-visible text indexing
  and slicing operations.
- Narrow verification:
  - `../../stark test --filter TextView --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA DynamicStorage Family

- Completed the `compiler.SsaTests` dynamic-storage family after source-port
  fixes for current dynamic-storage syntax, non-negative capacity proofs,
  source-visible initialization, and raw pointer/slice escape shapes.
- Replaced remaining `System.Collections.List<T>` reductions with direct
  `dynamic T` sources so the rendered SSA keeps the dynamic-storage operations
  (`new`, `TryReserve`, `Length`, `Capacity`, `MoveLast`, `Reserve`, data
  pointer and slice escapes) visible to the text bridge.
- Narrow verification:
  - `../../stark test --filter DynamicStorage --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-23 MIR Artifact Alignment

- Completed the named `compiler.MirTests` switch-pattern residue by replacing
  broad switch-word checks with a MIR switch-terminator helper and respelling
  enum/text/raw-pointer fragments to the current renderer.
- Completed the place-lowerer address-chain residue by asserting rendered
  pointer/address facts for large aggregates, large arrays, slice views, raw
  pointer loads, globals, and frozen parameter addresses.
- Added MIR module staging so imported lowering-contract regressions compile
  with a real staged `Dep.stark` dependency instead of an impossible root-only
  reduction.
- Reworked the nested generic layout port to force the concrete nested generic
  field layouts through MIR, because the monomorphization-plan artifact has no
  text renderer in the host-test protocol.
- Narrow verification:
  - `../../stark test --collection mid-level-ir-lowering-tests-switch-pattern-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-place-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter Generic --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter MemberCallsDoNotCollide --target arm64-apple-macosx26.0.0`: passed.
- No full `compiler.MirTests` rebaseline was run because broad sweeps are
  intentionally avoided.

## 2026-06-23 MIR Named Collections Complete

- Added compact MIR artifact suffixes for structural facts that the ported Stark
  tests need to preserve from the C# object-model assertions: integer/float/bool
  return operands, binary operator result types and constant operands, converts,
  field/index insert/extract rvalues, and explicit object-construction facts.
- Added host-test rendering for the `enum-layout` artifact, including compact
  tag ranges, ordered fields, variant tags, payload storage fields, and concrete
  size/alignment where the type model is available.
- Fixed remaining named `compiler.MirTests` collections by asserting the
  structural facts that now render directly, plus current source spelling for
  arm64 asm bypasses, unsafe FFI calls, and frozen raw pointers.
- Narrow verification:
  - `../../stark test --collection mid-level-ir-lowering-tests-runtime-drop-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-core --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-compile-time-evaluator --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-lowering-invariant --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-dynamic-fixed-array-indexing --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection raw-single-line-literal --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-cli --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-arena-frame --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-hir --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-mir --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-abi --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-enum-layout --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-full --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection generic-use-site-instantiation --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection lowering-contract-fact-key --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --list-collections --target arm64-apple-macosx26.0.0`: passed and showed only the broad aggregate `compiler`/`mir` collections remain unrun by design.
- No full aggregate `compiler` or `mir` collection run was performed because
  those aliases are broad and the current policy is narrow targeted runs only.

## 2026-06-23 SSA Invalid-IR Fixture Path

- Added the host-test `validatorFixture` request object with generic
  `kind`/`name` fields; `ssa` is backed by a fixture catalog and MIR/package
  artifact validator kinds can use the same transport when their catalogs land.
- Added an SSA validator fixture catalog generated from
  `tests/compiler.Tests/SsaIrValidationTests.cs`, preserving the C# diagnostic
  contracts for 95 validator inputs.
- Ported all 98 Stark SSA validator test entries to the fixture path or an
  explicit host-internal constructor-guard exclusion:
  `ExtractIndexOutOfRangeIsUnrepresentable`,
  `InsertIndexValueMismatchIsUnrepresentable`, and
  `IndexOperationFamilyMismatchIsUnrepresentable`.
- Added the three arena-frame SSA validator cases that were present in the C#
  oracle but missing from the Stark port table.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - Direct `--host-test-inspect` smoke for invalid, valid, and excluded SSA
    validator fixtures: passed with expected protocol behavior.
  - `../../stark test --list-collections --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.SsaTests`: passed.
  - `../../stark test --collection ssa-ir --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.SsaTests`: passed.
  - No broad aggregate collection was run.

Other suite notes:

- compiler.Tests: about 30 package-image failures; remainder includes
  AsmDeclarations, LawBodies, CheckMode, BuildUses, EmitLlvm/EmitExecutable,
  TextDiagnostics/SystemText/RuntimeText/LawFunctions, and a long tail.
- stdlib.Port: at most 21 `StdLibSource*` lowering/intrinsic/syscall-path assertions,
  roughly 16 Linux/Windows platform-specific tests, WindowsDispatch 2,
  SourceStd 2, and miscellaneous cases.
- compiler.MirTests: all named non-aggregate collections are green by targeted
  runs. The broad `compiler`/`mir` aggregate aliases were not run by design.

---

## macOS Pass-Bar Decision

The macOS pass bar includes tests runnable on macOS plus artifact/codegen-only
cross-target tests whose expected Linux/Windows output can be asserted without a
foreign SDK/linker/runtime. Tests that need real non-macOS platform facilities
are excluded from the macOS pass bar by platform gating, and should carry
comments explaining which platform is required.
