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
- [ ] Split oversized test classes so xUnit can parallelize them
      (`SystemCollections` 29 tests, `SystemIOFile`, `SystemText`,
      `SystemIOPath`). Within-class tests run serially; classes set the
      wall-time floor.
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
- [ ] Fix enum-receiver method calls against package-backed imports
      (`Encoding.UTF8.ToAscii()` passes type-check but crashes `lower-mir`
      with "Field 'ToAscii' could not be resolved"). Pre-existing; fails
      `PackagedStdLibTryFormatSurfaceCanBeConsumedWithoutSource` and keeps
      `SourceImportedStdLibTryFormatExecutableWritesText` pinned to the
      source path.

## Compiler Performance (helps all users, not just tests)

- [ ] Reachability-filtered lowering: lower/optimize only functions reachable
      from `main`/exports (validation still covers everything). Hello-world
      currently lowers 1,233 MIR functions and emits 34 (~97% waste in the
      MIR/SSA/O3 passes).
- [ ] Compile `--emit-exe` root plus source-dependency modules in one
      pipeline run, or share parsed modules and the type-check model across
      the per-module runs in `CompileAndEmitReferencedDependencyObjects`.
      Hello-world currently runs the full 43-pass pipeline 4 times (37.5s);
      heavy imports trigger 8-12 runs.
- [ ] Parallelize the sequential dependency-module compile loop in
      `CompilerCli.CompileAndEmitReferencedDependencyObjects` (modules are
      independent; the compiler runs at ~105% CPU today).
- [ ] Binary package image load path (already tracked in
      [../Self-host-Prep/20-package-image-format.md](../Self-host-Prep/20-package-image-format.md));
      the current 24MB JSON image is parsed on every package-consuming
      compile.

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
