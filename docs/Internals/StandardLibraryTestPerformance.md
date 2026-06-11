# Standard Library Test Suite Performance Plan

Task list to fix the slow `compiler.StandardLibraryTests` suite. Measured
baseline (June 2026, Apple M5, 10 cores, Debug compiler via `dotnet test`):
**309 tests, 38m 18s wall, 95.7 CPU-minutes, ~2.5x effective parallelism.**
91 tests take over 60s; the worst takes 8.5 minutes.

After the first improvement round (same machine, June 2026):
**309 tests, 11m 52s wall (3.2x), 68.6 CPU-minutes, ~7.8x effective
parallelism, 23 failures (down from 49; every remaining failure also failed
at baseline).**

Legend: `[ ]` not done, `[~]` partially done, `[x]` done.

## Quick Wins (test projects only)

- [x] Enable server GC in all test project csproj files
      (`<ServerGarbageCollection>true</ServerGarbageCollection>`).
      Measured: 8 concurrent in-process compiles 55.4s -> 26.7s (2.1x).
- [x] Split oversized test classes so xUnit can parallelize them.
      `SystemCollections` (29 tests) is now four classes, `SystemIOFile`
      (17) three, `SystemText` and `SystemIOPath` (10 each) two apiece —
      eleven classes capped at roughly nine tests and three or four
      CLI-native tests each, with test methods byte-identical and shared
      programs hoisted into `SystemCollectionsTestPrograms`. The suite now
      runs at ~7.6x parallel efficiency; the remaining wall-time floor is a
      single 17-minute test (see the long-pole note below).
- [~] Audit the ~104 in-process `pipeline.Run(...)` calls with no
      `StopAfterPassId`: add the earliest sufficient stop pass, and pass
      `OptimizationLevel: Og` where assertions do not inspect optimized
      SSA/LLVM text. Each converted test: ~12s -> ~2s. Done for the book
      sample compiles (`StopAfterPassId: "borrow-liveness"`); the per-module
      LLVM-text tests remain.

## Structural Fix (test infrastructure)

- [x] Build the stdlib package once per test run (static lazy fixture,
      ~30-60s amortized) and compile test programs against the package
      directory instead of `stdlib/src`. Measured compile side: 8.8s -> 2.2s;
      also eliminates per-dependency-module pipeline re-runs and per-test
      clang invocations in `--emit-exe` tests. Implemented as
      `SharedStdlibPackage` in the test project; ~60 call sites converted,
      including the per-test full-stdlib `--emit-lib`/`--emit-pkg` builds in
      the `Packaged*` tests.
- [x] Keep a small, deliberate set of source-import (`-I stdlib/src`) tests
      to cover the source-resolution path; the packaged path becomes the
      default for runtime-behavior tests.
      `StdLibPackageBuildsFromRepositorySources` and
      `PackagedStdLibCanBeConsumedWithoutSource` remain the canonical
      build-flow tests; in-process LLVM-text tests stay source-based.
- [ ] Decide the default optimization level for runtime-behavior tests
      (Og/O1 with explicit O3 opt-in for optimization-verification tests).
      Blocked by the low-opt clang crash below.

## Compiler Bugs (block the fixes above)

- [x] Fix duplicate FFI declarations in merged LLVM modules
      (`invalid redefinition of function 'close'`). The LLVM emitter now
      deduplicates external declarations by binary symbol name. A second
      hidden macOS bug behind it was also fixed: COMDAT groups were emitted
      for monomorphized generics, which Mach-O rejects; comdat emission is
      now target-gated. `build-stdlib.sh` works on macOS for the first time;
      26 previously failing tests now pass.
- [ ] Fix the clang crash on `--emit-exe` at `-O0`/`-Og`/`-O1` (unoptimized
      emission produces IR that only the O2/O3 Stark-side passes clean up).
      Blocks cheap native test builds. Two stack-overflow-deep recursions on
      the unoptimized path (`RequiresAggregateValueMaterialization`,
      `TryEmitStructuredAggregateStore`) were depth-capped to their safe
      fallbacks, but the clang crash remains; the two `-O0` packaged tests
      are gated off macOS until fixed.
- [x] Fix enum-receiver method calls against package-backed imports
      (`Encoding.UTF8.ToAscii()` passed type-check but crashed `lower-mir`
      with "Field 'ToAscii' could not be resolved"). Root cause was not
      package-specific: the speculative function-pointer call-target walk in
      MIR lowering called the throwing field-access lowering while probing
      chained `EnumType.Case.UfcsMethod()` expressions in any compilation
      mode. Speculative callers now pass `requireResolved: false` and fall
      through to receiver-call binding. Both
      `PackagedStdLibTryFormatSurfaceCanBeConsumedWithoutSource` and
      `SourceImportedStdLibTryFormatExecutableWritesText` now pass.

## Compiler Performance (helps all users, not just tests)

- [~] Reachability-filtered lowering: lower/optimize only functions reachable
      from `main`/exports (validation still covers everything). Hello-world
      previously lowered 1,233 MIR functions and emitted 34 (~97% waste in the
      MIR/SSA/O3 passes). CLI binary outputs (`--emit-obj`/`--emit-lib`/
      `--emit-exe` roots and every dependency compile) now lower SSA lazily
      from emission roots via `SsaEmissionReachability`, skipping SSA lowering
      and optimization for unreachable functions; the root pipeline for
      hello-world dropped 11.8s -> 4.9s. MIR lowering still covers the full
      closure, and inspection modes (`--check`/`--emit-mir`/`--emit-ssa`/
      `--emit-llvm`) keep the full lowered view by design.
- [x] Compile `--emit-exe` root plus source-dependency modules in one
      pipeline run, or share parsed modules and the type-check model across
      the per-module runs in `CompileAndEmitReferencedDependencyObjects`.
      Implemented as sharing plus per-run reachability: parsed source modules
      are shared across the sequential runs of one CLI invocation
      (`SharedSourceModuleParseCache`, diagnostic-free parses only), and each
      run lowers only emission-reachable functions. Measured (Release, M5):
      hello-world `--emit-exe` 37.5s -> 22.2s; full stdlib `--emit-lib`
      2m10s -> 47.9s; integration CLI test slice 5m21s -> 1m23s. All test
      gates unchanged (same pre-existing failures only). Remaining ideas:
      truly single-pipeline emission, sharing the type-check model itself,
      and MIR-level reachability.
- [x] Parallelize the sequential dependency-module compile loop in
      `CompilerCli.CompileAndEmitReferencedDependencyObjects`. Implemented as
      lazy symbol-driven waves: a dependency module's pipeline only runs when
      the unresolved-symbol loop suspects it (module-name shapes plus declared
      `asm`/`export` symbol names from the syntax model), each wave compiles
      in parallel, and fully inlined roots skip dependency compilation
      entirely. The library path compiles every archive member in parallel
      (`--emit-lib` peaked at ~492% CPU). The shared module resolver and parse
      cache are now thread-safe, and the compiler runs with server GC.
      Measured (Release, M5): full stdlib `--emit-lib` 50.8s -> 24.5s;
      hello-world `--emit-exe` 22.2s -> 7.6s; heavy dictionary program
      92.5s -> 65.2s. Package image byte-identical and archive symbol-set
      identical to sequential builds; all gates green.
- [x] Binary package image load path (design tracked in
      [../Self-host-Prep/20-package-image-format.md](../Self-host-Prep/20-package-image-format.md)).
      The host compiler now emits and loads a binary `.starkpkg` container
      (STARKPKG magic + version + Brotli-compressed canonical JSON payload) by
      default; `--package-image-json` opts into the indented JSON sidecar and
      `--inspect-pkg` renders JSON/text from either form. Legacy
      `.starkpkg.json` files keep loading. The stdlib image shrank from 41MB
      JSON to 275KB binary (the JSON itself also halved after dropping
      serialized computed properties). The self-hosted compiler still owns the
      long-term sectioned byte-level encoding.

## Current Long Pole (June 2026)

After the class splits and parallel dependency compiles, suite wall time is
bounded by a single test: `SourceImportedStdLibTryFormatExecutableWritesText`
(~17 minutes), whose app reaches the full wide-integer (`i1024`/`u1024`)
formatting machinery and pays one enormous O3 root compile. Reducing that one
compile (formatter code size, per-function pass budgets, or reachability
depth) is the next meaningful suite-time lever.

## Remaining Failures (macOS, June 2026)

After the failure-fixing round, 6 tests remain red, all sharing two deep
package-generics gaps:

- [ ] Imported generic templates that construct generic types or switch over
      generic enums still have gaps on the package path. The `Locked<T>`
      constructor-body publication now lowers (partial fix landed in
      `ImportedTemplateLowerer`), but the three `SystemThreading*AtRuntime`
      tests now fail one layer later: the consumer app's generic-typed static
      global (`App_Shared`) is not emitted, so linking fails. The
      `SourceStdLibTestingRichAssertionsExecutableRuns` test fails on switch
      lowering inside an imported `Result<T, E>` template
      ("Accepted switch shape could not be lowered").
- [ ] The const-lookup fold regression from the CTFE materialization change:
      imported const aggregates lower as inline `$tmp*_ctfe_array` stack
      copies instead of const-global loads, so `const-lookup-tables-ssa`
      never folds and no later pass folds the constant-index read either.
      Fails `ConstLookupTableOptimizationFoldsPackageConstLookupHelperFromTypedInitializer`
      and `ConstGlobalDerivedLoadsEmitInvariantLoadMetadataWithoutTaggingLocalLoads`
      in compiler.PipelineTests.

## Verification

- [~] Re-run the full suite and compare against the 38m baseline:
      `dotnet test tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj --logger "trx;LogFileName=stdlib-tests.trx"`.
      Target after test-infrastructure items: under 6 minutes wall on the
      baseline machine; after compiler items: 1-2 minutes.
      First round measured 11m 52s (3.2x); remaining wall time is dominated
      by the one-time shared package build under startup contention, the
      still-serial big classes, and 23 pre-existing macOS failures that
      burn full compile time before failing.
- [ ] Replace the stale x86_64-only `stdlib/dist/libSystem.starkpkg.json`
      or document that dist images are target-specific and not portable
      fixtures.
