# Phase 4 - Test Infrastructure Audit

Tests get ported first. The current host test suite is xUnit-based and assumes
rich .NET file/process/text/assertion APIs. Stark's current `System.Testing`
surface is intentionally tiny, so test infrastructure is a first-order blocker.

## Current Test Inventory

| Project | Tracked `.cs` Files | Primary Coverage |
|---|---:|---|
| `tests/compiler.Tests` | 43 | Parser, diagnostics, type checking, semantic validation, ownership, borrow liveness, MIR/SSA, LLVM emission, package image unit/regression tests, examples/benchmarks |
| `tests/compiler.PipelineTests` | 17 | Individual pipeline passes and full pipeline artifact gates |
| `tests/compiler.IntegrationTests` | 29 | CLI/project/package/native runtime integration tests |
| `tests/compiler.FeatureTests` | 13 | User-facing feature LLVM checks |
| `tests/compiler.StandardLibraryTests` | 30 | Stdlib source and backend-boundary tests for `System.*` |
| Total | 132 | 2,204 facts, 12 theories, 14,868 `Assert.` references |

Tool/API usage in tracked tests, excluding build output:

| Usage | Count |
|---|---:|
| `[Fact]` | 2,204 |
| `[Theory]` | 12 |
| `[InlineData]` | 13 |
| `MemberData` | 8 |
| `Assert.` | 14,868 |
| temp/file/path/directory references | 3,912 file/path API hits; 1,645 temp-related hits |
| process/exit references | 724 |
| regex/match references | 455 |

## Test Infrastructure Gap IDs

| ID | Capability | Current Stark Status | Blocking Scope |
|---|---|---|---|
| TEST-01 | Test discovery and runner model | **Decision specified:** generate an explicit `main` runner from `[Fact]` metadata; `System.Testing` currently has explicit `RunFact`, but the generator is not implemented | Blocks xUnit-style port of all projects until generator exists |
| TEST-02 | Rich assertions | `True`, `False`, primitive `Equal`, `Fail` only | Blocks most tests: contains/not contains, single, not null, type, collection, ranges, diagnostics |
| TEST-03 | Snapshot/golden text utilities | None found | Blocks LLVM/MIR/SSA/diagnostic/package text tests |
| TEST-04 | Temp directory/file fixtures with cleanup | Depends on missing stdlib temp/file helpers | Blocks integration/package/project tests |
| TEST-05 | Process execution capture | `System.Process` lacks spawn/capture | Blocks CLI, native, project, runtime, and self-host stage tests |
| TEST-06 | Host-compiler target mode for ported tests | Not present | Required for M1: Stark tests running against the current C# host compiler |
| TEST-07 | Fast compiler artifact inspection API for tests | Decision specified: typed in-process compiler test API is the blessed path; persistent/batched compiler runner returns structured results for host and cross-stage tests; full CLI artifact export is selective | Blocks pipeline/unit tests until the API/runner/export slices exist |
| TEST-08 | Parameterized tests and data providers | No equivalent to `[Theory]`, `[InlineData]`, `MemberData` | Blocks compact parser/stdlib test ports |
| TEST-09 | Platform gating and serial collections | No equivalent to xUnit collection/skip traits | Blocks toolchain tests and platform-specific stdlib tests |
| TEST-10 | Benchmark/regression harness | No Stark harness for benchmark source scripts/regression thresholds | Blocks benchmark regression tests |
| TEST-11 | Package image fixture editing and inspection comparison | Binary package-image codec and deterministic JSON/text inspection support missing | Blocks package image tests |
| TEST-12 | Diagnostic formatting and diff helpers | Minimal output only | Blocks diagnostics/parser/type-checking tests at current fidelity |

## Test Category Audit

| Category | Source Files | System.Testing Needs | Harness / Runner Needs | Golden / Snapshot Needs | Portability |
|---|---|---|---|---|---|
| Parser smoke/conformance/edge/trivia | `tests/compiler.Tests/ParserSmokeTests.cs`, `ParserConformanceTests.cs`, `ParserEdgeCaseTests.cs`, `CommentTriviaTests.cs` | TEST-02, TEST-08, TEST-12 | TEST-01, TEST-06, TEST-07 | Parser diagnostics/text snippets | Portable after parser strategy T01 and fast artifact/diagnostic API TEST-07 |
| Type checking and semantic validation | `TypeCheckingTests.cs`, `TypeTypingDiagnosticsTests.cs`, `TypeTypingExpressionFamilyTests.cs`, `SemanticValidationTests.cs`, `FunctionSemanticsTests.cs`, `V1LoweringContractTests.cs` | TEST-02, TEST-08, TEST-12 | TEST-01, TEST-06, TEST-07 | Diagnostics and typed model assertions | Portable with typed compiler test API or batched structured runner |
| Ownership/borrow/lowering contract | `OwnershipValidationTests.cs`, `BorrowLivenessValidationTests.cs`, `OwnershipRoadmapRegressionTests.cs`, `LoweringContractValidationTests.cs` | TEST-02, TEST-12 | TEST-01, TEST-06, TEST-07 | Diagnostics and ownership event assertions | Portable after artifact inspection model |
| MIR lowering | `tests/compiler.Tests/MidLevelIrArtifactValidationTests.cs`, `tests/compiler.Tests/MidLevelIrLowering/*.cs` | TEST-02, TEST-03, TEST-12 | TEST-01, TEST-06, TEST-07 | MIR text/artifact snapshots | Portable after TEST-03/TEST-07 |
| SSA lowering/validation/optimization | `SsaLoweringTests.cs`, `SsaIrValidationTests.cs`, `SsaOptimizationTests.cs`, pipeline optimize tests | TEST-02, TEST-03, TEST-12 | TEST-01, TEST-06, TEST-07 | SSA text/artifact snapshots | Portable after TEST-03/TEST-07 |
| LLVM inspection/unit feature checks | `LlvmIrEmissionTests.cs`, `LlvmEmitterConversionTests.cs`, `FixedArrayOrderedComparisonEmissionTests.cs`, `LlvmTextOrderedComparisonEmissionTests.cs`, `SsaEmitterCoverageMatrixTests.cs`, `tests/compiler.FeatureTests/*.cs` | TEST-02, TEST-03, TEST-12 | TEST-01, TEST-06, TEST-07 | LLVM substring/regex/body extraction helpers over optional textual LLVM artifacts printed from the in-memory module | Portable after text helpers, regex substitute, artifact access; these remain inspection tests and must not depend on parsing `.ll` as a backend input |
| libLLVM backend integration | New self-hosted backend tests from doc `23` | TEST-02, TEST-04, TEST-05, TEST-09, TEST-12 | TEST-01, TEST-05, TEST-06, TEST-07, TEST-09 | libLLVM discovery/version diagnostics, direct module-construction checks, verifier diagnostics, object emission, bundled-library clean-machine checks | Portable after process/path/toolchain support and libLLVM binding slice exist |
| Package image unit/regression | `PackageImageArchitectureTests.cs`, `PackageImageCallableValueTests.cs`, `PackageImageLoaderDiagnosticsTests.cs`, `PackageImageTypedArrayInitializerTests.cs`, package image integration files | TEST-02, TEST-03, TEST-11, TEST-12 | TEST-01, TEST-04, TEST-06, TEST-07 | Binary codec checks plus deterministic JSON/text inspection diffs | Portable after binary package-image support T07/doc `20`, inspection/export support S14, and fixture support |
| Compiler pipeline passes | `tests/compiler.PipelineTests/*.cs` | TEST-02, TEST-03, TEST-12 | TEST-01, TEST-06, TEST-07 | Artifact and pass execution assertions | Portable after test artifact API |
| CLI/project/toolchain integration | `CompilerCliTests.cs`, `ProjectCliTests.cs`, `PackageImageCliToolingTests.cs`, `SerialToolchainCollection.cs` | TEST-02, TEST-04, TEST-05, TEST-09, TEST-12 | TEST-01, TEST-05, TEST-06, TEST-09 | stdout/stderr/exit code diffs | Needs Stark process/env/file APIs before port |
| Runtime/native integration | `MidLevelIrRuntimeTests.cs`, `IntegerExponentRuntimeTests.cs`, `UnsignedIntegerRuntimeTests.cs`, `TextOrderedComparisonRuntimeTests.cs`, native codegen tests | TEST-02, TEST-04, TEST-05, TEST-09 | TEST-01, TEST-05, TEST-06, TEST-09 | stdout/exit/native artifact checks | Depends on native toolchain T08 and process S12 |
| Standard library tests | `tests/compiler.StandardLibraryTests/*.cs` | TEST-02, TEST-04, TEST-05, TEST-09, TEST-12 | TEST-01, TEST-05, TEST-06, TEST-09 | Source/LLVM/runtime text assertions | Portable in slices, but many require stdlib APIs under S09-S18 |
| Example/benchmark sources | `ExampleSourceTests.cs`, `BenchmarkSourceTests.cs`, `BenchmarkRegressionScriptTests.cs`, `ExamplesCompileRunTests.cs` | TEST-02, TEST-05, TEST-09, TEST-10 | TEST-01, TEST-05, TEST-06, TEST-09 | Runtime output/perf-script checks | Later port after core runner and process support |
| Helpers/base classes | `FallbackLogAssertions.cs`, `FeatureLlvmTestBase.cs`, `CompilerPipelineTestSupport.cs` | TEST-02, TEST-03, TEST-12 | TEST-06, TEST-07 | Shared diff/diagnostic helpers | Must be ported before their dependent tests |

## Project-by-Project Port Notes

| Project | First Portable Slice | Hardest Blockers |
|---|---|---|
| `compiler.Tests` | Parser smoke and simple diagnostic tests via typed test API or batched host-compiler runner | TEST-07 artifact access, TEST-03 snapshots, TEST-12 diagnostics |
| `compiler.PipelineTests` | Pass existence/order and simple syntax-model tests | TEST-07 direct artifact inspection |
| `compiler.FeatureTests` | LLVM substring checks for small single-file programs | TEST-03 text helpers, TEST-07 LLVM artifact access |
| `compiler.StandardLibraryTests` | Pure compile-only tests that do not spawn native artifacts | TEST-05 process, TEST-09 platform gating, stdlib file/process gaps |
| `compiler.IntegrationTests` | CLI smoke tests after process/temp support | TEST-04 temp fixtures, TEST-05 process capture, T08 native tools |

## Required `System.Testing` Expansion

| Needed API Family | Examples From Host Tests | Gap IDs |
|---|---|---|
| Assertions | `Assert.Contains`, `DoesNotContain`, `Equal`, `NotEqual`, `True`, `False`, `Null`, `NotNull`, `Single`, `Empty`, `IsType`, collection predicates | TEST-02 |
| Diagnostics | Compare code/message/line/column, render diagnostic bags, fail with rich messages | TEST-02, TEST-12 |
| Text matching | substring, starts/ends, count occurrences, regex or structured match, normalized line comparisons | TEST-03, S04 |
| Fixture lifecycle | temp dirs/files, cleanup on failure, per-test output capture | TEST-04 |
| Process harness | run compiler executable, capture stdout/stderr, assert exit code, timeout, serial collections | TEST-05, TEST-09 |
| Discovery | Generated explicit `main` from `[Fact]`, selected test filters, later `[Theory]`/data providers | TEST-01, TEST-08 |
| Snapshots | update/verify mode, stable diff output, path normalization | TEST-03 |
| Compiler inspection | typed in-process compile result API, persistent/batched host compiler runner with structured results, selective full artifact export, structured diagnostic/result assertions | TEST-07, TEST-12, T15 |

## Portability Verdict

Tests are portable in principle, but not as-is. The C# tests are tightly coupled
to xUnit and compiler internals. The recommended TDD path is:

1. Build the generated Stark test runner slice: scan `[Fact]` metadata at build
   time, emit an explicit `main`, and call `System.Testing` runner APIs without
   runtime reflection.
2. Build a Stark test harness that can invoke the current host compiler as a
   process and compare text outputs.
3. Add the TEST-07 inspection slices: a typed in-process compiler test API for
   speed, a persistent/batched compiler runner with structured results for
   host/cross-stage tests, and selective full artifact export for
   golden/stage/debug tests.
4. Port helper libraries first (`FeatureLlvmTestBase`, `CompilerPipelineTestSupport`,
   `FallbackLogAssertions`) into Stark.
5. Port tests category-by-category, starting with parser/diagnostic/LLVM inspection
   checks and leaving native/package/runtime integration until process/temp/JSON
   support is ready.
