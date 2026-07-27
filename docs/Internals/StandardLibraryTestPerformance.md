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
      `StopAfterPassId`: add the earliest sufficient stop pass. Each
      converted test: ~12s -> ~2s. Done for the book sample compiles
      (`StopAfterPassId: "borrow-liveness"`); the per-module LLVM-text
      tests remain.

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
- [x] Obsolete: the compiler has exactly one compilation mode (all Stark
      optimization passes plus clang -O3). Optimization levels were removed
      from the CLI and `CompilerOptions`; opting out of optimization passes
      was never an intended capability.

## Compiler Bugs (block the fixes above)

- [x] Fix duplicate FFI declarations in merged LLVM modules
      (`invalid redefinition of function 'close'`). The LLVM emitter now
      deduplicates external declarations by binary symbol name. A second
      hidden macOS bug behind it was also fixed: COMDAT groups were emitted
      for monomorphized generics, which Mach-O rejects; comdat emission is
      now target-gated. `build-stdlib.sh` works on macOS for the first time;
      26 previously failing tests now pass.
- [x] Obsolete: the unoptimized emission path no longer exists, so the
      clang crash it provoked is unreachable. The two formerly `-O0` packaged
      tests now build in the single compilation mode and run on macOS.
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
- [x] Binary package image load path (design documented in
      [Package Images](PackageImage.md)).
      The host compiler now emits and loads a binary `.starkpkg` container
      (STARKPKG magic + version + Brotli-compressed canonical JSON payload) by
      default; `--package-image-json` opts into the indented JSON sidecar and
      `--inspect-pkg` renders JSON/text from either form. Legacy
      `.starkpkg.json` files keep loading. The stdlib image shrank from 41MB
      JSON to 275KB binary (the JSON itself also halved after dropping
      serialized computed properties). The self-hosted compiler still owns the
      long-term sectioned byte-level encoding.

## Current Long Pole (June 2026) — fixed

After the class splits and parallel dependency compiles, suite wall time was
bounded by a single test: `SourceImportedStdLibTryFormatExecutableWritesText`
(~17 minutes), whose app reaches the full wide-integer (`i1024`/`u1024`)
formatting machinery. Profiling (dotnet-trace) put all the time inside
`emit-llvm`: the raw-pointer-loop intrinsic matcher rebuilt five
whole-function structures — including an O(blocks^2) dominator dataflow —
once per candidate loop preheader, making the scan quadratic-plus in block
count on large functions. Hoisting the five loop-invariant computations out
of the per-preheader matcher
(`LlvmFunctionBodyEmitter.RawPointerLoops.cs`) fixed it:

- TryFormat app compile: ~17 min -> 5.2 s (~200x); the test passes in 11 s.
- Heavy dictionary program `--emit-exe`: 65.2 s -> 4.4 s (the "expensive
  root compile" was mostly this same scan).
- All 480 LLVM-emission/intrinsic-filtered tests pass unchanged.

## Remaining Failures (macOS, June 2026)

After the package-generics round, 2 stdlib tests remain red, each a distinct
bug:

- [x] Generic-typed statics against packaged generics now emit. The global
      initializer planner and MIR object-creation lowering both resolve
      constructor shapes structurally (via the type model's new
      `ConstructorShapes` export) when a creation has no typing record —
      creations inside bridge-parsed package constructor bodies are never
      re-type-checked. `SystemThreadingSynchronizedGuardsSharedMutableStateAtRuntime`
      passes; `static mut Synchronized<Counter>` apps link and run against the
      package.
- [x] Switches over imported generic enums now lower: the published
      enum-pattern path resolved cases by re-parsing a *display name*
      ("Result<i32, i32>.Ok"), which never matches canonical instantiation
      keys; it now resolves from the substituted type symbol and enum layout
      directly. Packaged `System.Testing.ResultErr/ResultOk` compile and
      return correct values, including payload captures.
- [x] Effect-inference miscompile (latent in source mode too): a function
      whose callee could not be resolved during semantic validation kept a
      pure-looking memory summary, so e.g. a function whose only side effect
      is an implicit drop got `memory(none)` and LLVM legally deleted the
      drop's writes (a dropped `Receiver` never marked the channel closed at
      O3 on the package path). Unresolved callees now degrade the summary to
      conservative memory effects; destructor drop effects survive.
      `SystemThreadingChannelMovesMessagesAndObservesCloseAtRuntime` passes.
- [x] `SystemThreadingChannelHandlesContendedProducersAtRuntime`: bare generic
      function references inside packaged template bodies
      (`stack ThreadContextEntry thunk = ThreadPayloadThunk<T>;`) now lower
      via the template's published function-address summaries — MIR's
      `ResolveNamedOperand` falls back to the unique name-matched summary and
      substitutes the active specialization (the deferred-instantiation
      machinery already planned the thunk's monomorphization). The contended
      producers app compiles, links, and runs green.
- [x] `SourceStdLibTestingRichAssertionsExecutableRuns`: three independent
      stdlib/platform bugs, all fixed:
      1. The exit-139 SIGSEGV: `System.Process` passed raw pipe fds into the
         handle-based `Platform.CloseFile`, which on macOS dereferences a
         `MacOSHandle` struct. The platform dispatch surface gained
         `CloseRawDescriptor`/`ReadRawDescriptorBytes`/`WriteRawDescriptorBytes`
         (libc close/read/write by fd on macOS; the fd-in-pointer contract on
         Linux; stubs on Windows), and the process pipe paths use them.
      2. Process timeouts never fired on macOS: the monotonic clock used
         Linux's `CLOCK_MONOTONIC` id (1), which is invalid on Darwin (6).
         `MonotonicMilliseconds` joined the platform dispatch surface with a
         Darwin clock id.
      3. The packaged `System.Option`/`System.Result` aliases broke type
         identity (`TypeIs<System.Option<T>, System.Core.Option<T>>` false):
         the package type codec's module-prefix strip turned child-module
         names into unloadable relatives ("System.Core.Option" stored as
         "Core.Option"). The strip now only applies to names the loader can
         re-qualify (module-local single segments and vtable members).
      The full rich-assertions app passes in both source and package modes.

With these, the known macOS failure set is empty: the previously-failing
tests plus the threading/process/file-IO/console/packaged slices all pass
(targeted runs, June 2026).
- [x] The const-lookup fold regression from the CTFE materialization change:
      imported const aggregates lowered as inline `$tmp*_ctfe_array` stack
      copies instead of const-global loads, so `const-lookup-tables-ssa`
      never folded. Fixed in MIR lowering: a bare reference to a const-global
      aggregate now stays a `MidLevelIrGlobalOperand` (global load) instead of
      materializing element-by-element — both in named-value resolution and in
      the eager expression-level CTFE shortcut, which now probes whether the
      folded constant is exactly the named global's initializer before
      materializing (shadowing-safe; computed `comptime` aggregates still
      materialize inline). This restores the fold (a packaged
      `System.Collections.Lookup(Facade.Lookup, 2)` folds to the constant
      through the slice coercion) and is also a generated-code win: surviving
      uses read the const global rather than rebuilding the aggregate on the
      stack per use. The invariant-load test's load regex was also updated to
      count direct `%slot_*` loads, since local pointer round-trips now fold
      to direct slot loads. Both pipeline tests pass; compiler.PipelineTests
      is fully green (497/497) for the first time, and compiler.Tests passes
      1,624/1,624.

## Verification

- [x] Re-run the full suite and compare against the 38m baseline:
      `dotnet test tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj --logger "trx;LogFileName=stdlib-tests.trx"`.
      Target after test-infrastructure items: under 6 minutes wall on the
      baseline machine; after compiler items: 1-2 minutes.
      Final measurement (June 2026, same machine): **309 tests, 5m 49s wall,
      48.9 CPU-minutes (~8.4x parallel efficiency), 0 failures** — 6.6x
      faster than the 38m 18s baseline and the first fully green run on
      macOS. No single test exceeds 82s (was 1,043s); the remaining top
      tests are ~60-80s source-mode `--emit-exe` compiles, so the next
      meaningful lever toward the 1-2 minute stretch goal is converting
      more of those to the packaged path or trimming per-test O3 work.
- [ ] Replace the stale x86_64-only `stdlib/dist/libSystem.starkpkg.json`
      or document that dist images are target-specific and not portable
      fixtures.
