# Phase 6 - Self-hosting Roadmap

This roadmap is TDD-first. The host compiler remains the source of truth until a
self-hosted compiler can build itself and pass the ported tests.

## Milestone Summary

| Milestone | Theme | Primary Gates |
|---|---|---|
| M0 | Close test-infrastructure blockers | TEST-01 through TEST-12, S09-S12, S18, T12 |
| M1 | Port tests to Stark, still targeting host compiler | TEST-06, TEST-07, T03 |
| M2 | Close language blockers | L01, L06, L07, L11, T01 |
| M3 | Close stdlib blockers | S01, S02, S06, S07, S09-S14, S17 |
| M4 | Close tooling blockers | T02-T11, T14 |
| M5 | Port compiler subsystems leaf-first | `05-port-checklist.md` compiler rows |
| M6 | Bootstrap | Stage1/Stage2 compiler equivalence, tests pass |
| M7 | Cutover and host removal | T02, T14, release smoke check |

## M0 - Test Infrastructure First

| Field | Details |
|---|---|
| Entry Criteria | Current host compiler builds and tests can still run from C#; Phase 4 gap IDs accepted as the test-infrastructure backlog. |
| Work | Expand `System.Testing` and the test harness to cover TEST-01 through TEST-12. Build-time `[Fact]` / `[Theory]` discovery, inline-data rows, typed indexed member-data providers, explicit generated `main` runners, target-triple platform gates, serial collection grouping, process capture/timeout assertions, snapshots, and core rich assertions have landed; continue with rich diagnostic adapters and host-compiler execution wrappers. |
| Exit Criteria | A Stark test executable can run selected tests against the current host compiler, feed stdin when needed, capture stdout/stderr/exit code, compare text/golden output, and report failures clearly. |
| Key Risks | TEST-07 must avoid slow per-test process/JSON overhead while still supporting host and cross-stage inspection; TEST-05 depends on process APIs S12; TEST-04 depends on file/path/temp APIs S09-S11. |
| Parallel Workstreams | Rich assertions TEST-02; process/temp fixtures TEST-04/TEST-05; snapshot/diff helpers TEST-03; fast in-process artifact/diagnostic API plus batched runner TEST-07/TEST-12/T15. |

## M1 - Port Test Suite To Stark Against Host Compiler

| Field | Details |
|---|---|
| Entry Criteria | M0 complete; host compiler runner is available from Stark tests; test target layout from `05-port-checklist.md` is accepted or revised. |
| Work | Port helper files first (`FeatureLlvmTestBase`, `CompilerPipelineTestSupport`, `FallbackLogAssertions`), then parser/diagnostic/LLVM text tests, then pipeline artifact tests, then package/native/stdlib integration tests. |
| Exit Criteria | A meaningful Stark test suite runs in CI against the C# host compiler and covers parser, type checking, ownership, MIR, SSA, LLVM, package image, project CLI, and stdlib slices. |
| Key Risks | TEST-07 may block deep pipeline tests if artifacts cannot be inspected; T07/doc `20` and S14 block package image tests until binary codec plus deterministic JSON/text inspection exist; T08/S12 block native integration tests. |
| Parallel Workstreams | Parser/diagnostic tests; LLVM text feature tests; stdlib compile-only tests; package image tests once binary codec and inspection output exist; native/runtime tests once process/toolchain support exists. |

## M2 - Close Language Feature Blockers

| Field | Details |
|---|---|
| Entry Criteria | M1 has enough tests to guard feature changes; open decisions through OQ-08 are resolved. OQ-02 is resolved as handwritten parser + canonical `Stark.g4`; OQ-08 is resolved as generic collection contracts plus compiler-internal typed interning. |
| Work | Resolve L01, L06, L07, and L11; implement the specified L03 traversal-loop slice, the specified L05 typed `comptime` generic value parameters, the remaining L09 aggregate CTFE beyond the scalar/fixed-array/table/named-aggregate/enum/layout-query slices, structural-fact queries, compile-time branching model, and T01. Decide whether L02, L04, L10, L12 are language work or port conventions. L08 source-literal syntax has landed; the first expression-form `comptime` CTFE slice, fixed-array aggregate CTFE slice, value-position `comptime` block slice, bounded `willexit` CTFE table-generation slice, named struct/record aggregate CTFE slice, enum aggregate CTFE slice, concrete layout-query CTFE slice, and L13 alias/noalias proof-carrier model have landed; S02/S03 still track Stark-side text helpers. Preserve Stark terminology and contracts: finite, law, borrow, retborrow, storeborrow, doctrine, range-typed integers, memory contracts. |
| Exit Criteria | Stark can express the compiler's data model, error model, invariant failures, alias/noalias proofs, optional values, generic collection constraints, and handwritten parser implementation without relying on C# semantics. |
| Key Risks | T01 handwritten parser implementation dominates front-end schedule; L09 structural-fact queries must stay explicit and compile-time-only; L06/S06 collection work now hinges on complete stdlib contract coverage and deterministic output discipline. |
| Parallel Workstreams | Handwritten parser prototype and conformance tests T01; traversal-loop syntax/lowering L03; comptime generic parameters and fixed-array use sites L05; error/option convention L01/L11/S01; invariant API L07; remaining aggregate evaluator plus structural-fact queries L09; generic collection contracts plus typed interner implementation L06/S06/S07. |

## M3 - Close Standard Library Blockers

| Field | Details |
|---|---|
| Entry Criteria | M2 directions accepted; stdlib API changes can be tested by the Stark test harness. |
| Work | Implement or finalize S01, S02, S06, S07, S09, S10, S11, S12, S13, S14, and S17. S05 is closed by `System.Compiler.IntegerFacts`. S17 is the accepted arena/table IR storage model with typed handles, first-class fact tables, lowering policies, package-image durable facts, and validation. S16 is not required for the synchronous self-hosting path; if build/test parallelism is chosen before bootstrap, limit S16 to doc `22`'s explicit payload thread starts, `Synchronized<T>` / `Locked<T>`, and MPSC channels. |
| Exit Criteria | Stark code can read/write source, parse/emit TOML through `System.Toml`, read/write binary package images, render package inspection JSON/text, spawn/capture tools, build text output, use compiler-grade collections, represent bounded `i1024`/`u1024` integer/range facts, and manage compiler IR memory/facts through typed handles and verified fact transfer. |
| Key Risks | S06/S07 dictionary/text/interner work can affect many compiler modules and must not make compiler output depend on hash iteration order; S17 touches every IR model and must keep backend facts from being dropped or watered down during lowering. |
| Parallel Workstreams | File/path/process stack S09-S12; text/format builder S02/S03; collections/interner S06/S07/S08; `System.Toml` S13/T04; package inspection JSON/text S14; IR arena/table plus fact model S17/doc `24`; narrow threading coordination S16/doc `22` only if parallel build/test work is scheduled. |

## M4 - Close Tooling Blockers

| Field | Details |
|---|---|
| Entry Criteria | M3 supplies stdlib APIs needed by the driver; project/test harness can run under `stark test`. |
| Work | Finish T02-T11 and T14 enough for bootstrap: stage-aware build/run/test, the formal `build/<profile>/<target-triple>/<stage>/` layout from doc `25`, stdlib package discovery, package image tooling, libLLVM-primary backend integration, native toolchain resolver/bundled LLVM path, target info, release/doctor checks. |
| Exit Criteria | `stark build`, `stark run`, and `stark test` can select host/stage compilers, place artifacts deterministically under `build/<profile>/<target-triple>/<stage>/`, use stdlib packages, build LLVM modules and emit objects through libLLVM, print textual LLVM inspection artifacts when requested, and drive linker/native tools on supported platforms. |
| Key Risks | libLLVM versioning/bundling T10/T08 can expose platform packaging and ABI drift; native toolchain packaging T08/T14 may reveal platform SDK gaps; binary package-image codec T07 must be fast and validated; JSON/text inspection S14 must remain deterministic without becoming the normal load path; cross-compilation T09 can expand scope. |
| Parallel Workstreams | Stage build layout T02/T03/T05; binary package image and inspection outputs T07/S14/doc `20`; stdlib package discovery T11; libLLVM binding/backend T10/doc `23`; toolchain resolver T08/T09; release doctor T14/T15. |

## M5 - Port Compiler Subsystems Leaf-first

| Field | Details |
|---|---|
| Entry Criteria | M1 tests cover the first subsystem slice; M2-M4 blockers for that slice are closed. |
| Work | Follow `05-port-checklist.md`: parsing strategy, artifacts/diagnostics, syntax/module/type resolver, type checking, semantic validation, ownership, MIR, borrow liveness, SSA, optimizers, ABI/libLLVM backend, package image, CLI. Each ported subsystem must be gated by its ported Stark tests running against host output first. |
| Exit Criteria | A Stage1 Stark compiler can compile representative Stark programs and produce matching diagnostics/artifacts/native output for the covered test suite. |
| Key Risks | Large C# files (`TypeChecking.cs`, `CompilerArtifacts.cs`, `DefaultCompilerPipeline.cs`, MIR/LLVM/SSA XL rows) can hide implicit BCL behavior; handwritten parser fidelity and diagnostics can dwarf early front-end work. |
| Parallel Workstreams | Leaf helpers and artifacts; parser/source model; semantic/type validation; MIR/SSA; LLVM/package image; tests for each slice. |

## M6 - Bootstrap

| Field | Details |
|---|---|
| Entry Criteria | Stage1 Stark compiler builds with the C# host; M5 core compiler and CLI are ported enough to build the compiler project. |
| Work | Build Stage1 with host. Use Stage1 to build Stage2. Compare Stage1/Stage2 outputs enough to trust stability. Run the Stark test suite against Stage2. |
| Exit Criteria | Self-hosted compiler builds itself; ported tests pass; generated artifacts and diagnostics are stable for the supported platform set. |
| Key Risks | Non-deterministic package image/order output, native toolchain drift, stage-specific stdlib/package discovery, hidden host-only behavior in tests. |
| Parallel Workstreams | Determinism checks; artifact comparison; stage-specific build scripts; native smoke tests; stdlib package rebuild verification. |

## M7 - Cutover And Drop Host Compiler

| Field | Details |
|---|---|
| Entry Criteria | M6 passes repeatedly; minimal release packaging T14 and `stark doctor` work for the platforms being published. |
| Work | Use the existing C# host as Stage0 until the Stark compiler can build itself. After M6 passes, update release archives, document the migration bootstrap flow, rename the current C# `/src` tree to `/old_src` when the Stark `/src` tree takes over, and remove or demote the C# host from the normal build path. |
| Exit Criteria | Stark release can be installed from an archive, `stark doctor` passes, stdlib package exists, compiler builds itself, and the host compiler is no longer required for ordinary development. |
| Key Risks | Platform SDK/legal bundling constraints, unsupported cross-targets, editor/tooling drift T13, hidden dependency on `/old_src` after cutover. |
| Parallel Workstreams | Minimal release packaging T14; docs; release smoke checks; syntax/editor updates for syntax that actually changed; removal of C# host from ordinary build/test paths. |

## Recommended First Three Concrete Actions

1. Build the M0 host-compiler test harness slice: rich assertions, temp dirs, process capture, and text diff.
2. Add the TEST-07 fast compiler artifact/diagnostic inspection API and batched runner.
3. Start the T01 handwritten parser conformance slice against canonical `Stark.g4` and the host parser oracle.
