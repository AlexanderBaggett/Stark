# Phase 6 - Self-hosting Roadmap

This roadmap is TDD-first. The host compiler remains the source of truth until a
self-hosted compiler can build itself and pass the ported tests.

## Milestone Summary

| Milestone | Theme | Primary Gates |
|---|---|---|
| M0 | Close test-infrastructure blockers | TEST-01 through TEST-12, S09-S12, S18, T12 |
| M1 | Port tests to Stark, still targeting host compiler | TEST-06, TEST-07, T03 |
| M2 | Close language blockers | L01, L06, L07, L11, L13, T01 |
| M3 | Close stdlib blockers | S01, S02, S05, S06, S09-S14, S17 |
| M4 | Close tooling blockers | T02-T11, T14 |
| M5 | Port compiler subsystems leaf-first | `05-port-checklist.md` compiler rows |
| M6 | Bootstrap | Stage1/Stage2 compiler equivalence, tests pass |
| M7 | Snapshot/staging strategy and host removal | T02, T14, release verification |

## M0 - Test Infrastructure First

| Field | Details |
|---|---|
| Entry Criteria | Current host compiler builds and tests can still run from C#; Phase 4 gap IDs accepted as the test-infrastructure backlog. |
| Work | Expand `System.Testing` and/or test harness to cover TEST-01 through TEST-12. Add temp file/dir, process capture, text diff/snapshot, rich assertions, platform gating, parameterized tests, and host-compiler execution support. |
| Exit Criteria | A Stark test executable can run selected tests against the current host compiler, capture stdout/stderr/exit code, compare text/golden output, and report failures clearly. |
| Key Risks | TEST-07 direct artifact inspection may require new CLI output or a compiler-as-library boundary; TEST-05 depends on process APIs S12; TEST-04 depends on file/path/temp APIs S09-S11. |
| Parallel Workstreams | Rich assertions TEST-02; process/temp fixtures TEST-04/TEST-05; snapshot/diff helpers TEST-03; runner/discovery TEST-01/TEST-08/TEST-09; artifact/diagnostic output TEST-07/TEST-12. |

## M1 - Port Test Suite To Stark Against Host Compiler

| Field | Details |
|---|---|
| Entry Criteria | M0 complete; host compiler runner is available from Stark tests; test target layout from `05-port-checklist.md` is accepted or revised. |
| Work | Port helper files first (`FeatureLlvmTestBase`, `CompilerPipelineTestSupport`, `FallbackLogAssertions`), then parser/diagnostic/LLVM text tests, then pipeline artifact tests, then package/native/stdlib integration tests. |
| Exit Criteria | A meaningful Stark test suite runs in CI against the C# host compiler and covers parser, type checking, ownership, MIR, SSA, LLVM, package image, project CLI, and stdlib slices. |
| Key Risks | TEST-07 may block deep pipeline tests if artifacts cannot be inspected; S14 blocks package image tests; T08/S12 block native integration tests. |
| Parallel Workstreams | Parser/diagnostic tests; LLVM text feature tests; stdlib compile-only tests; package image tests once JSON support exists; native/runtime tests once process/toolchain support exists. |

## M2 - Close Language Feature Blockers

| Field | Details |
|---|---|
| Entry Criteria | M1 has enough tests to guard feature changes; open decisions OQ-02 through OQ-08 have accepted directions. |
| Work | Resolve L01, L06, L07, L11, L13, and T01. Decide whether L02-L05/L08-L12 are language work or port conventions. Preserve Stark terminology and contracts: finite, law, borrow, retborrow, storeborrow, doctrine, range-typed integers, memory contracts. |
| Exit Criteria | Stark can express the compiler's data model, error model, invariant failures, alias/noalias proofs, optional values, generic collection constraints, and parser strategy without relying on C# semantics. |
| Key Risks | T01 parser strategy dominates front-end schedule; L13 alias/noalias mistakes must remain compile-time diagnostics; L06 string-key hashing may straddle language and stdlib doctrine design. |
| Parallel Workstreams | Parser strategy prototype T01; error/option convention L01/L11/S01; invariant API L07; alias-proof artifact checks L13; collection doctrine/hash design L06/S06. |

## M3 - Close Standard Library Blockers

| Field | Details |
|---|---|
| Entry Criteria | M2 directions accepted; stdlib API changes can be tested by the Stark test harness. |
| Work | Implement or finalize S01, S02, S05, S06, S09, S10, S11, S12, S13, S14, and S17. Keep S16 single-threaded for v1 unless build/test parallelism is chosen. |
| Exit Criteria | Stark code can read/write source, TOML, JSON/package images, spawn/capture tools, build text output, use compiler-grade collections, represent BigInt/range facts, and manage compiler IR memory deliberately. |
| Key Risks | S05 BigInt scope may grow if unbounded integer literals are required; S06 dictionary/string/hash work can affect many compiler modules; S17 ownership/arena decision affects every IR model. |
| Parallel Workstreams | File/path/process stack S09-S12; text/format builder S02/S03; BigInt S05; collections/interner S06/S07/S08; TOML/JSON S13/S14; memory/arena S17. |

## M4 - Close Tooling Blockers

| Field | Details |
|---|---|
| Entry Criteria | M3 supplies stdlib APIs needed by the driver; project/test harness can run under `stark test`. |
| Work | Finish T02-T11 and T14 enough for bootstrap: stage-aware build/run/test, `.stark/build/` layout, stdlib package discovery, package image tooling, native toolchain resolver/bundled LLVM path, target info, release/doctor checks. |
| Exit Criteria | `stark build`, `stark run`, and `stark test` can select host/stage compilers, place artifacts deterministically under `.stark/build/`, use stdlib packages, and drive native tools on supported platforms. |
| Key Risks | Native toolchain packaging T08/T14 may reveal platform SDK gaps; package image JSON T07/S14 may bottleneck module loading; cross-compilation T09 can expand scope. |
| Parallel Workstreams | Stage build layout T02/T03/T05; package image T07; stdlib package discovery T11; toolchain resolver T08/T09; release doctor T14/T15. |

## M5 - Port Compiler Subsystems Leaf-first

| Field | Details |
|---|---|
| Entry Criteria | M1 tests cover the first subsystem slice; M2-M4 blockers for that slice are closed. |
| Work | Follow `05-port-checklist.md`: parsing strategy, artifacts/diagnostics, syntax/module/type resolver, type checking, semantic validation, ownership, MIR, borrow liveness, SSA, optimizers, ABI/LLVM, package image, CLI. Each ported subsystem must be gated by its ported Stark tests running against host output first. |
| Exit Criteria | A Stage1 Stark compiler can compile representative Stark programs and produce matching diagnostics/artifacts/native output for the covered test suite. |
| Key Risks | Large C# files (`TypeChecking.cs`, `CompilerArtifacts.cs`, `DefaultCompilerPipeline.cs`, MIR/LLVM/SSA XL rows) can hide implicit BCL behavior; generated parser path can dwarf handwritten work. |
| Parallel Workstreams | Leaf helpers and artifacts; parser/source model; semantic/type validation; MIR/SSA; LLVM/package image; tests for each slice. |

## M6 - Bootstrap

| Field | Details |
|---|---|
| Entry Criteria | Stage1 Stark compiler builds with the C# host; M5 core compiler and CLI are ported enough to build the compiler project. |
| Work | Build Stage1 with host. Use Stage1 to build Stage2. Compare Stage1/Stage2 outputs enough to trust stability. Run the Stark test suite against Stage2. |
| Exit Criteria | Self-hosted compiler builds itself; ported tests pass; generated artifacts and diagnostics are stable for the supported platform set. |
| Key Risks | Non-deterministic package image/order output, native toolchain drift, stage-specific stdlib/package discovery, hidden host-only behavior in tests. |
| Parallel Workstreams | Determinism checks; artifact comparison; stage-specific build scripts; native smoke tests; stdlib package rebuild verification. |

## M7 - Snapshot/Staging Strategy And Drop Host Compiler

| Field | Details |
|---|---|
| Entry Criteria | M6 passes repeatedly; release packaging T14 and doctor/verification are green on supported platforms. |
| Work | Define snapshot compiler format/location, update release archives, document bootstrapping from source, remove or demote C# host from the normal build path, keep an emergency recovery story. |
| Exit Criteria | Stark release can be installed on a clean machine, `stark doctor` passes, stdlib package exists, compiler builds itself, and the host compiler is no longer required for ordinary development. |
| Key Risks | Snapshot trust and rollback policy, platform SDK/legal bundling constraints, unsupported cross-targets, editor/tooling drift T13. |
| Parallel Workstreams | Release packaging T14; docs; CI clean-machine verification; VS Code/editor parity T13; snapshot provenance/checksum policy. |

## Recommended First Three Concrete Actions

1. Decide OQ-02 parser strategy and OQ-14 test runner direction, because they shape the first two milestones.
2. Build the M0 host-compiler test harness slice: rich assertions, temp dirs, process capture, and text diff.
3. Add a machine-readable diagnostic/artifact output path for tests so TEST-07 does not force the Stark tests to link against compiler internals.
