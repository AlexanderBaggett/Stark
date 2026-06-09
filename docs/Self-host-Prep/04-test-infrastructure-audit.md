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
| TEST-01 | Test discovery and runner model | **Implemented:** `stark test` generates an explicit `main` runner from `[Fact]` / `[Theory]` metadata, enumerates tests at build time, expands `[InlineData]` rows and typed indexed `[MemberData]` rows, supports repeatable selected-test filters over generated display names, calls `System.Testing.RunFact`, and reports stable pass/fail output without runtime reflection | No longer blocks basic xUnit-style `[Fact]`, inline-data theory, or typed member-data theory ports |
| TEST-02 | Rich assertions | Partial base landed: finite-law `True`, `False`, primitive `Equal`, `NotEqual`, text contains/starts/ends/count occurrences, range, slice/List shape assertions, root `Option<T>` / `Result<T, E>` shape predicates, structured diagnostic predicates, compile-time type assertion predicates, and process-output predicates; `Fail` remains effectful | Remaining blockers: rich diagnostic rendering/adapters, richer collection predicates, null/raw-pointer policy |
| TEST-03 | Snapshot/golden text utilities | `System.Testing` has explicit `VerifySnapshot`, `UpdateSnapshot`, and `VerifyOrUpdateSnapshot` APIs for ASCII/UTF-8 text snapshots, normalized CRLF/LF comparison, first-difference line/column facts, and stable difference formatting | No longer blocks basic LLVM/MIR/SSA/diagnostic/package text golden checks; artifact access and package inspection still gate many tests |
| TEST-04 | Temp directory/file fixtures with cleanup | `System.Testing.TempDirectory` covers temp directory creation, safe relative fixture paths, file read/write/atomic edit helpers, explicit cleanup, and best-effort drop cleanup; per-test output capture remains separate | No longer blocks basic integration/package/project fixture files; output capture remains for runner parity |
| TEST-05 | Process execution capture | Linux-backed `System.Process.RunCapture`, `RunCaptureWithInput`, and timeout variants capture stdout/stderr/exit code, can feed stdin, and expose argv/env/cwd helpers; `System.Testing` has process output assertions plus run-match and timeout helpers; cross-platform backend parity remains | Basic CLI/native/project/runtime assertions can now use the harness; non-Linux process backends still gate broad ports |
| TEST-06 | Host-compiler target mode for ported tests | Initial host bridge landed: `compiler --host-test-inspect` and `compiler --host-test-server` let Stark tests target the current C# host compiler through structured JSON | Stark-side wrappers and process integration still need to consume the host bridge |
| TEST-07 | Fast compiler artifact inspection API for tests | Initial runner landed with structured diagnostics, logs, pass executions, loaded-module names, and selected LLVM/MIR/SSA artifact text | Typed Stark helper APIs and additional artifact renderers still gate broad pipeline/unit ports |
| TEST-08 | Parameterized tests and data providers | **Implemented:** generated runners support `[Theory]` plus `[InlineData(...)]` rows with string, bool, signed integer, and qualified-name constants, and typed indexed `[MemberData(provider, rowType, count, ...fields)]` providers that materialize only selected rows through direct row-struct calls. Row filters match generated display names such as `Adds(1, 2, 3)` and `Adds[AddRows:1]` | No longer blocks parser/shared-table theory ports; port-specific convenience adapters can be added without changing the runner protocol |
| TEST-09 | Platform gating and serial collections | Implemented for generated `[Fact]` / `[Theory]` runners: `[Platform(...)]` / `[SkipPlatform(...)]` resolve from the selected target triple at build time, gated-out entries report through `System.Testing.SkipFact`, `[Collection(name)]` creates stable serial scheduling groups, and `[Serial]` is shorthand for the reserved `Serial` collection | No longer blocks single-runner toolchain tests that need serialized shared resources; future parallel test execution must honor the same collection keys |
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
| Assertions | `Assert.Contains`, `DoesNotContain`, `Equal`, `NotEqual`, `True`, `False`, `Null`, `NotNull`, `Single`, `Empty`, `IsType`, collection predicates | TEST-02; value/text/count/range/slice/List shape, root `Option`/`Result`, structured diagnostic, and compile-time type predicate bases are implemented |
| Diagnostics | Compare code/message/line/column, render diagnostic bags, fail with rich messages | TEST-02 has structured diagnostic predicates; TEST-12 still covers rich rendering/diffs/adapters |
| Text matching | substring, starts/ends, count occurrences, normalized snapshot comparisons; regex or structured match remains separate | TEST-03, S04 |
| Fixture lifecycle | temp dirs/files and cleanup-on-failure helpers are implemented through `System.Testing.TempDirectory`; per-test output capture remains | TEST-04 |
| Process harness | run compiler executable, optionally feed stdin, capture stdout/stderr, assert exit code, assert timeouts, and serialize shared-resource facts with `[Collection]` / `[Serial]` | TEST-05, TEST-09 |
| Discovery | Generated explicit `main` from `[Fact]` / `[Theory]`, inline-data row expansion, typed indexed member-data provider expansion, selected-test filters, platform gates, and serial collections are implemented | TEST-01, TEST-08, TEST-09 |
| Snapshots | explicit update/verify mode, normalized line endings, stable first-difference output | TEST-03 |
| Compiler inspection | persistent/batched host compiler runner with structured results is available; structured diagnostic predicates can assert adapted result data; typed Stark helper APIs, selective extra artifact export, and JSON/result adapters remain | TEST-07, TEST-12, T15 |

## Portability Verdict

Tests are portable in principle, but not as-is. The C# tests are tightly coupled
to xUnit and compiler internals. The recommended TDD path is:

1. Build a Stark test harness that can invoke the current host compiler as a
   process and compare text outputs.
2. Add the TEST-07 inspection slices: a typed in-process compiler test API for
   speed, a persistent/batched compiler runner with structured results for
   host/cross-stage tests, and selective full artifact export for
   golden/stage/debug tests.
3. Port helper libraries first (`FeatureLlvmTestBase`, `CompilerPipelineTestSupport`,
   `FallbackLogAssertions`) into Stark.
4. Port tests category-by-category, starting with parser/diagnostic/LLVM inspection
   checks and leaving native/package/runtime integration until process/temp/JSON
   support is ready.
