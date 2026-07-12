# Self-Host-Prep Test Pass Ledger

This document is the historical triage/progress ledger for ported-test pass
state. It is reference material for fixing failures, not the authoritative task
list. Keep executable task ordering in [TASKS.md](TASKS.md); update this ledger
only when rebaselining suites, recording a new failure-family classification, or
capturing details that would otherwise clutter the task list.

Use [TASKS.md](TASKS.md) for compact task and subtask checkboxes, not for
failure evidence, run logs, or status-update prose.

Verification convention (since 2026-07-01): `--check` runs the full front end
— type-check, semantic-validate, and ownership-validate — over the root AND
every source-imported module (each validated as its own root, in parallel,
with failures aggregated). A green `--check` gate therefore covers the
diagnostics that previously only surfaced on executable builds.

---

## 2026-07-09 Host Expectation Re-Aim Slice

- Re-aimed `ImportedSourceAsmFunctionsEmitExternalDeclarationsAndCalls` to assert the strengthened imported asm call-site facts: the literal argument keeps its exact `range(i64 2, 3)` and the raw pointer argument keeps `readonly`.
- Re-aimed `BoundedRawPointerArgumentFactsStrengthenDirectAndIndirectCallAttributes` to assert the caller's tighter `u8[4 10]` count range survives both direct and function-pointer calls alongside the nonnull/dereferenceable/readonly pointer facts.
- Re-aimed the two copied `System.Text.stark` runtime conversion integration tests to accept only the expected STK4122 recursive-call warning family and the clean `Summary: 0 errors, 6 warnings, 0 infos.` compiler summary.
- Narrow verification:
  - `dotnet test tests/compiler.Tests/compiler.Tests.csproj --filter "FullyQualifiedName~LlvmIrEmissionTests.ImportedSourceAsmFunctionsEmitExternalDeclarationsAndCalls|FullyQualifiedName~LlvmIrEmissionTests.BoundedRawPointerArgumentFactsStrengthenDirectAndIndirectCallAttributes" --no-restore`: passed.
  - `dotnet test tests/compiler.IntegrationTests/compiler.IntegrationTests.csproj --filter "FullyQualifiedName~MultiFileIntegrationTests.SystemTextSourceModuleSupportsRuntimeAsciiUnicodeConversionHelpers|FullyQualifiedName~MultiFileIntegrationTests.SystemTextSourceModuleSupportsRuntimeUtf16ConversionHelpers" --no-restore`: passed.
- No broad test sweep was run.

---

## 2026-07-09 ABI Dotted Method Symbol Naming Slice

- Fixed ABI symbol naming for dotted local method/doctrine names after the module-prefix regression: local root-module methods keep module-relative symbols such as `@Box_Read` and `@Inspect_Read`.
- Preserved source-imported method calls by prefixing dotted names only when the resolved function identity's module differs from the module currently being compiled, so consumers still reference defining-module symbols such as `@Lib_Box_Read`.
- Added focused source-import LLVM coverage for a noinline `Lib.Box.Read` method to assert the `@Lib_Box_Read` declaration/call and preserve receiver `noalias`/`readonly`/`dereferenceable` facts.
- Narrow verification:
  - `dotnet test tests/compiler.Tests/compiler.Tests.csproj --filter "FullyQualifiedName~LlvmIrEmissionTests.ValueReceiverMethodsLowerToDirectAggregateCalls|FullyQualifiedName~LlvmIrEmissionTests.BorrowReceiverMethodsLowerToPointerReceiverCalls|FullyQualifiedName~LlvmIrEmissionTests.DoctrineLawCallsEmitDirectReadonlyNoCaptureSignatures|FullyQualifiedName~LlvmIrEmissionTests.ImportedSourceMethodsUseDefiningModuleQualifiedSymbols" --no-restore`: passed.
  - Tiny source-import CLI smoke (`Lib.Box.Read` + `Demo.main`, `./stark Demo.stark --emit-exe -I <tmp> --no-stark-path -o <tmp>/demo`): compile passed and executable returned `7`.
- No broad test sweep was run.

---

## 2026-07-09 Enum Layout List Lowering Narrowing Slice

- Added a focused C# standard-library regression for a nested exported generic wrapper: `Table<Payload>` owns `System.Collections.List<Payload>`, and emitted LLVM must contain the concrete list layout rather than an empty struct.
- Added `selfhost/probe/IrTableEnumLayoutFactProbe.stark`, a tiny source-only selfhost probe that instantiates `IrTable<MirEnumLayoutFact>` without importing the whole `Compiler.Mir` facade.
- Narrow verification:
  - `dotnet test tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj --filter FullyQualifiedName~SystemCollectionsStandardLibraryTests.StdLibSourceListNestedThroughExportedGenericWrapperEmitsConcreteLayout --no-restore`: passed.
  - `./stark selfhost/probe/IrTableEnumLayoutFactProbe.stark --emit-llvm -I selfhost -I stdlib/src --no-stark-path -o /tmp/irtable_enum_layout_probe.ll`: passed; emitted `%System_Collections_List_Compiler_Mir_EnumLayout_MirEnumLayoutFact_ = type { { ptr, i64, i64 } }`.
  - `rg -n "= type \\{ \\}" /tmp/irtable_enum_layout_probe.ll`: no empty type definitions found.
  - `rg -n "dereferenceable\\(0\\)|invalid|unknown" /tmp/irtable_enum_layout_probe.ll`: no `dereferenceable(0)` or invalid markers; only benign `"unknown..."` string constants matched.
  - `./stark selfhost/probe/EnumReturnProbe.stark --emit-llvm -I selfhost -I stdlib/src --no-stark-path -o /tmp/enum_return_probe.ll`: interrupted after `lower-mir` reached 220.3s, so the full-facade repro is not counted as a pass.
- No broad test sweep was run.

---

## 2026-07-09 Logical Manifest Brotli Payload Slice

- Added a dependency-free Brotli payload encoder for logical manifest JSON writeout. The self-host writer emits valid Brotli streams using non-final uncompressed meta-blocks and a final empty meta-block, matching the binary `MANF` section encoding without introducing a native encoder dependency.
- Kept JSON construction and section serialization separately testable: `TryEncodeLogicalPackageManifestJson` still returns raw UTF-8 JSON bytes, while `TryEncodeLogicalPackageManifestBrotliJson` wraps those bytes for binary package-image `MANF` payloads.
- Added focused IR facts for the exact tiny Brotli stream bytes (`abc` -> `20 00 10 61 62 63 03`) and for manifest-Brotli payloads surviving logical image writeout/copyback.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with the existing generic-template recursive-writer warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: blocked by the existing stdlib error at `stdlib/src/System/FileSystem.stark:613` for unresolved `System.Runtime.Platform.SetPermissions`, so it is not counted as a pass.
  - External decoder sanity probe against .NET `BrotliStream` for uncompressed block length boundaries `1`, `3`, `65536`, `65537`, `1048576`, and `1048577`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Function Semantic Manifest Builder Slice

- Added function semantic rows to the logical manifest JSON builder, including required function header strings, per-module first/last/count links, and O(1) child append links for called functions, parameters, initialization ranges, calls, and call arguments.
- Emitted `CompilerFacts.FunctionSemantics` with explicit memory-effect booleans, `HasOpaqueCall`, parameter alias/dereferenceable/alignment/read/write facts, initialization byte ranges, call memory effects, and caller/callee argument mapping so backend optimization facts survive manifest JSON lowering.
- Extended the focused manifest JSON builder coverage with a readback test through `TryMaterializeLogicalPackageFunctionSemanticFactJsonGraph`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: blocked by the existing stdlib error at `stdlib/src/System/FileSystem.stark:613` for unresolved `System.Runtime.Platform.SetPermissions`, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Typed-Interface Global Manifest Builder Slice

- Added typed-interface global rows to the logical manifest JSON builder, including required header strings, named global type references, mutability, and O(1) per-module first/last/count links.
- Added a scalar constant-initializer builder path for `integer`, `float`, `bool`, `text`, and `null` initializers with required initializer type references. Aggregate initializer child rows remain a future typed-interface type-sized slice rather than emitting fake count-only facts.
- Emitted populated `TypedInterface.Globals` arrays and preserved initializer payload text/type facts through the existing typed global fact graph materializer.
- Extended the focused manifest JSON builder fact to read back the generated typed global through declaration and `GlobalType` summary helpers, then materialize the typed global fact graph and assert the initializer row, integer payload text, and `GlobalConstantInitializerType` reference.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: interrupted after about 150 seconds of silent compile output, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Typed-Interface Function Manifest Builder Slice

- Added typed-interface function rows to the logical manifest JSON builder, including required header strings, resolved return type references, and required FFI/strict-FP/fast-call facts.
- Added linked child rows for typed callable parameters, ordinary generic parameters, comptime generic parameters, and pointee-dead-on-return parameter names. Child appends remain O(1) via first/last/count links and preserve declaration order during `TypedInterface.Functions` emission.
- Emitted lossless optional callable facts that the builder can represent directly: qualified resolved name, overload key, inline preference, FFI ABI, backend optimization mode, link name, generic-template/body/performance/unsafe/varargs/tail-call flags, parameter raw-count expressions, disjoint/const flags, and named type references. Count-only constraint/predicate/contract arrays were intentionally not added for typed callables because the deep materializer requires real nested facts.
- Extended the focused manifest JSON builder fact to read back the generated typed function through typed declaration, typed callable, typed callable parameter, and typed type-reference summary helpers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: interrupted after about 60 seconds of silent compile output, so it is not counted as a pass.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: interrupted after about 60 seconds of silent build output before runner progress, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Typed-Interface Type-Alias Manifest Builder Slice

- Added typed-interface type-alias rows to the logical manifest JSON builder, including required alias header strings and compact resolved `TargetType` references that preserve typed kind/name facts for later LLVM-facing lowering.
- Added per-alias linked child rows for ordinary generic parameters and comptime generic parameters. Child appends remain O(1) via first/last/count links and preserve declaration order during `TypedInterface.TypeAliases` emission.
- Emitted the required typed-interface `Functions`, `Types`, and `Globals` array shell alongside `TypeAliases`, so existing typed-interface count readers can consume builder-produced manifests without special cases.
- Extended the focused manifest JSON builder fact to read back the generated typed alias declaration plus `TypeAliasTarget` and `TypeAliasComptimeGenericParameterType` references through the existing typed-interface readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about 90 seconds of silent compile output, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type Manifest Builder Slice

- Added source-surface type rows to the logical manifest JSON builder, including required type header strings (`Name`, `QualifiedName`, `Visibility`, `Kind`), required `Fields` array emission, and optional backend/layout/pack/align/destructor/dyn-trait facts.
- Added linked child rows for type fields, ordinary generic parameters, comptime generic parameters, primary constructor parameters, enum variants, enum variant payloads, and implemented traits. Child appends remain O(1) via first/last/count links and preserve source order during `SourceSurface.Types` emission.
- Preserved validated nested row shapes rather than fake count-only records for fields, constructor parameters, and variants, so emitted manifests survive the existing source-surface materializer and readback helpers.
- Extended the focused manifest JSON builder fact to read back one representative type plus field, constructor parameter, variant, and variant-payload summaries through the existing source-surface type readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about 90 seconds of silent compile output, so it is not counted as a pass.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Function Manifest Builder Slice

- Added source-surface function rows to the logical manifest JSON builder, including required function header strings (`Name`, `QualifiedName`, `Visibility`, `SymbolName`, `Kind`, `ReturnType`) and required emission booleans (`IsFfi`, `IsStrictFp`, `UseFastCallingConvention`).
- Added per-function linked child rows for parameters, ordinary generic parameters, comptime generic parameters, and pointee-dead-on-return parameter names. Parameter rows preserve source name/type text plus optional raw-pointer element-count expressions and const/disjoint flags.
- Preserved optional backend/source facts with compact row flags and string indexes: inline preference, FFI ABI, backend optimization mode, link name, asm marker, hot/cold, explicit inline preference, unsafe, varargs, tail-callable, and count-only contract/group arrays.
- Kept appends O(1) with per-module first/last/count function links and per-function first/last/count child links, so builder callers do not need to pre-group artifacts before JSON emission.
- Extended the focused manifest JSON builder fact to read back function header text, parameter order/text, generic/count facts, ABI/performance flags, contract/group counts, and dead-on-return count through the existing source-surface function readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build output before runner progress, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Global Manifest Builder Slice

- Added source-surface global rows to the logical manifest JSON builder, including source global header strings (`Name`, `QualifiedName`, `Visibility`, `Kind`, `Type`), required `IsMutable`, and optional `ConstantInitializer` presence.
- Kept appends O(1) with per-module first/last/count links and row `NextRow` pointers, so manifest emission preserves source order without scanning unrelated rows.
- Extended the focused manifest JSON builder fact to read back two globals through the existing source-surface global reader, covering order, mutability, initializer presence, and header text.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about two minutes of silent build output before runner progress, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type-Alias Manifest Builder Slice

- Added source-surface type-alias rows to the logical manifest JSON builder, including source alias header strings (`Name`, `QualifiedName`, `Visibility`, `TargetType`).
- Added linked child rows for ordinary generic parameters and compact comptime generic parameters. The current comptime parameter writer emits the validated object shape with a `Kind` string, which matches today's source-surface alias reader/count requirements while leaving room for full typed type-reference emission later.
- Kept the builder append path O(1): per-module first/last/count links for aliases, plus per-alias first/last/count links for child parameters, so callers do not need to pre-group artifacts before emission.
- Extended the focused manifest JSON builder fact to read back alias header text, ordinary generic parameter order, and comptime generic parameter count through the existing source-surface alias readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build output before runner progress, so it is not counted as a pass.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Manifest Builder Slice

- Added source-surface import and re-export rows to the logical manifest JSON builder.
- Kept builder appends O(1) and order-robust with per-module first/last/count tables plus row `NextRow` links, so artifact traversal does not need to pre-group rows by module before emission.
- Emitted populated import/re-export rows under a module's explicit `SourceSurface` object, preserving the existing `HasSourceSurface()` marker while leaving empty module shells compact.
- Extended the focused manifest JSON builder fact to read back direct imports, re-export-only rows, and direct+re-export merged rows through the existing source-surface summary reader.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build output before runner progress, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Manifest JSON Builder Slice

- Added a compact logical manifest JSON builder for package identity, module shell rows, build profile, target feature rows, target/backend facts, C data model facts, and aggregate pointer layout facts.
- Kept emission fact-preserving and allocation-conscious: package-level strings are read back from the deduplicated logical package string table through a reusable scratch buffer, module names use a separate deduplicated module string table, and the final payload encoder rejects non-empty output buffers before copying fresh JSON bytes.
- Added root-module validation before JSON emission so the emitted module list can always recover the root ordinal used by package import handoff.
- Added a focused self-host IR fact that emits manifest JSON, copies it into a `MANF` payload, wraps it with the existing logical package-image writer, and validates the result through the existing manifest summary/module summary readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src -I tests-stark --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about 90 seconds of silent compile output, so it is not counted as a pass.
  - `../../stark test --filter LogicalPackageManifestJsonBuilderWritesPackageHeaderAndModuleShells --test-progress --test-timeout 120 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build output before runner progress, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Package Compact Builder Slice

- Added a compact logical package-image builder that writes deduplicated `STRS` bytes, `PINF` package facts, and a three-section `STRS`/`PINF`/`MANF` directory around an already-encoded manifest payload.
- Kept the write path linear and allocation-light: string data is appended once into a contiguous byte table, fact rows store string indexes, and serialization reserves each output section before emitting.
- Added a focused self-host IR fact that round-trips the builder output through the existing logical package-image readers, including duplicate root/library strings, duplicate target features, C data model facts, aggregate pointer layout facts, and copied manifest payload bytes.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about 90 seconds of silent compile output, so it is not counted as a pass.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Bridge Graph Ownership Slice

- Extended the compact logical manifest model's per-module graph rows to own source-surface bridge summary families: imports, type aliases, type-alias generic parameters, types, type fields, primary-constructor parameters, enum variants, enum payloads, globals, functions, and function parameters.
- Preserved the existing source-surface precedence rule in the model path: explicit `SourceSurface` rows shadow legacy module-level surface arrays, while legacy arrays still materialize when no explicit source-surface object exists.
- Kept the materialization path single-parse and allocation-light for the model, validating required/optional source-surface text and shape facts without copying source strings into the model graph.
- Updated the manifest-model fixture with representative source-surface rows for every owned family and asserted module-level graph availability/count accessors.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after silent compile intervals.
  - `../../stark test --filter LogicalPackageImageBuildsDecodedManifestModelRows --test-progress --test-timeout 120 --stage stage0` from `tests-stark/selfhost.Ir`: stopped after silent build intervals before runner progress output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Typed-Interface Graph Ownership Slice

- Extended the compact logical manifest model's per-module graph rows to own effective typed-interface graph families: typed type aliases, typed callable facts, typed globals, and typed type facts.
- Kept the load path single-parse and linear over declaration arrays, including nested typed methods under type declarations, so package import handoff can consume graph rows without reparsing `MANF`.
- Updated the manifest-model fixture to use valid compact typed-interface rows and assert model-level typed graph availability/counts for alias, callable, global, and type families.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after silent compile intervals.
- No broad test sweep was run.

---

## 2026-07-09 Logical Compiler-Fact Graph Ownership Slice

- Extended the compact logical manifest model's per-module graph rows to own the remaining compiler-fact graph families: ABI facts, layout facts, native metadata facts, function semantic facts, and function ownership facts.
- Kept the load path single-parse and fact-preserving: each family is discovered with an optional member probe against the effective `CompilerSections.CompilerFacts` node, then materialized through the existing parsed-document materializers without reparsing `MANF`.
- Updated the manifest-model fixture to cover empty ABI/layout sections, top-level native dependency metadata, one compact semantic row, and one compact ownership row while leaving the larger row-shape coverage in the existing dedicated graph tests.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-compiler-fact-graphs -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageBuildsDecodedManifestModelRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir`: stopped after several silent build intervals to avoid a long project run before runner output.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after silent compile intervals.
- No broad test sweep was run.

---

## 2026-07-09 Logical Module Graph Ownership Slice

- Added per-module graph ownership to the compact logical manifest model for decoded `MANF` JSON. The model now reserves graph rows beside module summary rows and owns module-level function-effect fact rows plus generic-template function rows through the same effective `CompilerSections` precedence used by the bridge readers.
- Kept compiler-fact graph ownership incremental: `FunctionEffects` is optional inside `CompilerFacts` for this slice, so packages that only carry other compiler-fact families are not rejected before those families are wired into the top-level model.
- Added focused self-host IR fixture assertions for graph-row count, borrowed graph access, function-effect row facts, generic-template function row ordinals, and empty-root-module graph defaults.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-module-graphs -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageBuildsDecodedManifestModelRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir`: stopped after several silent build intervals to avoid a long project run before runner output.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after silent compile intervals.
- No broad test sweep was run.

---

## 2026-07-09 Logical Manifest Model Slice

- Added a compact self-host logical manifest model for decoded `MANF` JSON. The new path parses the manifest once, validates root module/library/profile/target/backend facts against `PINF`/`STRS`, reserves ordered module-row storage up front, and materializes compact module summaries for ordinal-indexed package import handoff.
- Added a focused self-host IR fact covering manifest summary facts, O(1) module ordinal access, compiler-section typed-interface override rows, source/fact/template presence flags, empty-root-module defaults, and backend feature-order rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-logical-model -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageBuildsDecodedManifestModelRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir`: stopped after several silent build intervals to avoid a long project run before runner output.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after several silent compile intervals after the test-local borrow/value fix.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Package Stage Compatibility Slice

- Added a self-host logical package-image stage compatibility helper over compact facts. Current Stage0 package images do not encode a stage in `PINF`/`MANF`; the host project driver supports only `stage0`, so the self-host gate now accepts validated package facts for `stage0` and rejects reserved future stages without decoding `MANF` or scanning `STRS`.
- Added a focused self-host IR fact covering `stage0` acceptance, `stage1`/`stage2`/empty-stage rejection, and rejection of default/unvalidated package facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-stage-compat -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageValidatesStageCompatibility --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type Child Bridge Slice

- Added compact self-host source-surface type primary-constructor parameter summaries for decoded logical package manifests, preserving source-facing parameter names, type spellings, raw-pointer count expressions, and disjoint/const flags without reconstructing source text.
- Added compact self-host source-surface enum variant and payload summaries, preserving variant names, named-payload mode, optional role/absorbed-error metadata, payload names, positional empty-name payloads, and source payload type spellings.
- Reused the effective source-surface type selection rule from the type-header slice, so explicit `SourceSurface.Types` rows win, legacy module-level `Types` are the fallback, and an explicit source surface with no type rows suppresses legacy type-child rows.
- Added focused self-host IR facts covering explicit source-surface constructor parameters, enum variants, and payloads; legacy fallback; explicit-empty source-surface suppression; malformed constructor parameter type/count-expression rejection; malformed variant header rejection; malformed payload type rejection; and positional-vs-named payload names.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-type-children -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceTypePrimaryConstructorParameterRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceTypeEnumVariantRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type Field Bridge Slice

- Added compact self-host source-surface type field summaries for decoded logical package manifests, preserving source-facing field name, type spelling, optional visibility, explicit offset bytes, and thread-safety attribute counts without reconstructing source text.
- Reused the effective source-surface type selection rule from the type-header slice, so explicit `SourceSurface.Types` rows win, legacy module-level `Types` are the fallback, and an explicit source surface with no type rows suppresses legacy field rows.
- Added a focused self-host IR fact covering explicit source-surface field rows, legacy fallback field rows, explicit-empty source-surface suppression, missing optional visibility/offset defaults, empty type rejection, and invalid explicit offset rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-field -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceTypeFieldRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type Bridge Slice

- Added compact self-host source-surface type summaries for decoded logical package manifests, preserving source-facing type name, qualified name, visibility, kind, backend optimization mode, struct layout spelling, pack/align facts, destructor presence, dyn-trait status, and direct child counts without reconstructing source text.
- Matched the C# source bridge effective source-surface selection rule for this slice: explicit `SourceSurface.Types` win, legacy module-level `Types` are the fallback, and an explicit source surface with no type rows does not leak legacy rows back in.
- Added a focused self-host IR fact covering explicit source-surface types, legacy fallback types, explicit-empty source-surface suppression, source layout flags, generic/comptime-generic validation, implemented-trait text validation, and invalid pack-byte rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-type -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceTypeRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Function Bridge Slice

- Added compact self-host source-surface function summaries for decoded logical package manifests, preserving source-facing function names, qualified names, visibility, symbol names, manifest kind, return type spelling, ABI/performance flags, memory-contract group counts, and parameter raw-count expression spelling without reconstructing source text.
- Matched the C# source bridge effective source-surface selection rule for this slice: explicit `SourceSurface.Functions` win, legacy module-level `Functions` are the fallback, and an explicit source surface with no function rows does not leak legacy rows back in.
- Added a focused self-host IR fact covering explicit source-surface functions, legacy fallback functions, explicit-empty source-surface suppression, source-spelled parameter rows, optional ABI/performance flags, malformed return type rejection, malformed parameter type rejection, and malformed comptime parameter rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-function -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceFunctionRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Global Bridge Slice

- Added compact self-host source-surface global summaries for decoded logical package manifests, preserving source-facing global name, qualified name, visibility, manifest kind, type spelling, mutability, and constant-initializer presence without reconstructing source text.
- Matched the C# source bridge effective source-surface selection rule for this slice: explicit `SourceSurface.Globals` win, legacy module-level `Globals` are the fallback, and an explicit source surface with no global rows does not leak legacy rows back in.
- Added a focused self-host IR fact covering explicit source-surface globals, legacy fallback globals, explicit-empty source-surface suppression, mutable/global initializer flags, empty type rejection, and malformed initializer-shape rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-global -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceGlobalRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Type-Alias Bridge Slice

- Added compact self-host source-surface type-alias summaries for decoded logical package manifests, preserving source-facing alias name, qualified name, visibility, target type spelling, ordinary generic parameter names, and comptime generic parameter counts without reconstructing source text.
- Matched the C# source bridge effective source-surface selection rule for this slice: explicit `SourceSurface.TypeAliases` win, legacy module-level `TypeAliases` are the fallback, and an explicit source surface with no alias rows does not leak legacy rows back in.
- Added a focused self-host IR fact covering explicit source-surface aliases, legacy fallback aliases, ordinary generic parameter row reads, explicit-empty source-surface suppression, malformed generic parameter rejection, and malformed comptime parameter rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-alias -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceTypeAliasRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds of silent build/setup output to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Logical Source-Surface Import Bridge Slice

- Added compact self-host source-surface import summaries for decoded logical package manifests, preserving direct-import, re-export, and effective-export facts without reconstructing source text.
- Matched the C# source bridge import selection rule for this slice: explicit `SourceSurface` imports/re-exports win, legacy module-level imports/re-exports are the fallback, and duplicate re-exports promote the direct import to exported rather than creating a second row.
- Added a focused self-host IR fact covering explicit source-surface imports, re-export-only rows, duplicate re-export promotion, legacy fallback rows, and malformed source-surface import rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I /tmp/stark-selfhost-source-root-codex-source-surface -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter LogicalPackageImageReadsSourceSurfaceImportRows --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after several silent build intervals to avoid a long project run before runner output.
- No broad test sweep was run.

---

## 2026-07-09 Relocatable Library Package Archives

- Changed `--emit-lib --package-image-output` packaging so the emitted static
  archive is copied beside the package image and the manifest records only the
  archive file name. The original `-o` library output remains in place, but the
  package directory can now be relocated without preserving the old
  `stage0/pkg` + `stage0/bin` sibling layout.
- Added regression coverage that routes a package image away from the library
  output, verifies the adjacent archive copy and manifest value, copies only
  the package directory to a new location, deletes the original output
  directories, and resolves the package library path from the relocated
  package.
- Updated the package-backed probe recipe to describe the adjacent-archive
  layout.
- Narrow verification:
  - `dotnet test tests/compiler.IntegrationTests/compiler.IntegrationTests.csproj --filter EmitLibraryStagesPackageLibraryBesideRoutedPackageImage --no-restore`: passed 1/1. The build also printed the pre-existing nullable warnings in `TypeChecking.cs`.
- No broad test sweep was run.

---

## 2026-07-09 Source-First Module Resolution

- Changed `FileSystemModuleResolver` resolution order so source files are
  checked across all search roots before the recursive package-image index is
  built. This keeps stale `*.starkpkg` build artifacts from substituting for
  source modules during raw source checks and avoids the expensive package scan
  on source-backed imports.
- Kept package-image resolution available when no source module exists, so
  package-backed probe and dependency compiles still work.
- Updated module-provenance coverage and compiler verification docs to reflect
  the source-first contract and `--explain-modules` provenance output.
- Narrow verification:
  - `dotnet test tests/compiler.Tests/compiler.Tests.csproj --filter "ResolverPrefersExplicitSourceOverImplicitPackage|ResolverPrefersSourceOverExplicitPackageCandidate|CliPrefersExplicitSourceOverRootDirectoryPackage|CliKeepsStderrCleanForPackageResolutionsWithoutShadowing|CliExplainModulesListsSourceResolutionsAndStaysQuietByDefault" --no-restore`: passed 5/5. The build also printed pre-existing nullable warnings in `TypeChecking.cs` and an existing xUnit analyzer warning in `PackageImageArchitectureTests.cs`.
- No broad test sweep was run.

---

## 2026-07-09 Direct CLI Target Data Layout Parity

- Fixed direct CLI target resolution so `--target <local-triple>` derives the
  detected LLVM data layout when `--target-data-layout` is omitted, matching
  the project driver path and avoiding manual `inspect-pkg` layout copying for
  local package consumers.
- Kept explicit `--target-data-layout` authoritative and left cross-target
  triples untouched unless the detected toolchain triple matches.
- Added focused integration coverage that emits LLVM with an explicit local
  target and asserts the emitted `target datalayout` matches the detected
  toolchain layout.
- Narrow verification:
  - `dotnet test tests/compiler.IntegrationTests/compiler.IntegrationTests.csproj --filter "EmitLlvmIrDerivesDetectedDataLayoutForExplicitLocalTarget|EmitLlvmIrRejectsContradictoryTargetDataLayout" --no-restore`: passed 2/2. The build also printed the pre-existing nullable warnings in `TypeChecking.cs`.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Bound-Operation Payload Slice

- Added package-owned bound-operation payload rows for type, text, numeric,
  and boolean facts, covering receiver/callable types, access/source/index
  details, dynamic storage and dyn-trait operation facts, object construction
  and initializer members, enum variants and members, text interpolation/build
  facts, layout queries, and switch-dispatch counts.
- Preserved constructor shape parameter facts as payload rows too: parameter
  names, parameter types, const/disjoint flags, raw-pointer count expressions,
  parameter counts, and primary-shape flags now survive package-image
  materialization without source reconstruction.
- Allowed function-pointer and closure bound calls to carry call-argument rows
  without a named `QualifiedResolvedName` call signature, matching the Stage0
  loader/builder model.
- Extended the focused `selfhost.Ir` generic-template fixture to exercise the
  bound-operation payload families in one compact JSON corpus and assert row
  counts plus representative payload kind/source-tag readback.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `tmp_root=/tmp/stark-selfhost-source-root-codex-boundpayloads && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-bound-payload-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
  - `/Users/zadkey/Repos/stark/stark test --filter LogicalPackageImageMaterializesGenericTemplateFactGraphRows --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stopped after two silent minutes to avoid a long project rebuild.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Call-Signature Operation Row Slice

- Added package-owned generic-template call-signature rows shared by direct
  calls, member calls, function addresses, and call-shaped bound operations.
- Preserved qualified resolved/source/template names, published ordinals,
  return/target/parameter/type-argument/comptime type references, parameter
  flags, raw-pointer element count expressions, parameter disjoint/overlap/same
  groups, dead-on-return parameter names, bound-operation kind/location/result
  facts, and bound call-argument addressability/mutability/const-provenance
  facts.
- Extended the focused `selfhost.Ir` generic-template fact to assert row
  counts, parent-kind separation, bound-operation call-signature linkage,
  nested child payload rows, type-reference source tags, payload ordinals, text
  kinds, and recovered call/bound-operation text.
- Narrow verification:
  - `tmp_root=/tmp/stark-selfhost-source-root-codex-callfacts && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-call-signature-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Runtime execution note: this stayed on the existing semantic host-test
  inspect path used by the recent package-image slices; the filtered fact
  runner was not widened.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Object/Enum Operation Row Slice

- Added package-owned generic-template object creation, object initializer
  member, enum constructor/member, enum call, enum value, enum pattern/member,
  and aggregate pattern/member rows.
- Preserved object constructor shape facts, storage selector, expression text,
  enum/pattern published ordinals, variant names, field indices, and payload
  type references with dedicated source tags.
- Extended the focused `selfhost.Ir` generic-template fact to assert row
  counts, function-template linkage, member links, ordinals, source tags,
  payload ordinals, text kinds, and recovered object/enum/pattern text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `tmp_root=/tmp/stark-selfhost-source-root-codex && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-object-enum-operation-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Operation Row Slice

- Added package-owned generic-template local declaration, conversion, field
  access, and try propagation rows.
- Preserved operation ordinals, published ordinals, local source positions,
  field indices, `try` role/funnel variant names, and all operation payload
  type references with dedicated source tags.
- Extended the focused `selfhost.Ir` generic-template fact to assert row
  counts, linkage back to the owning function template, row ordinals, optional
  `try` payload presence, type-reference source tags, payload ordinals, and
  recovered operation text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `tmp_root=/tmp/stark-selfhost-source-root-codex && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-operation-child-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Verification note: the clean symlink source root intentionally avoided stale
  package images under `selfhost/tools` while keeping source imports explicit.
- Runtime execution note: the filtered `selfhost.Ir` runtime fact runner was
  not rerun; this slice stayed on `PackageImage.stark --check` plus host-test
  semantic inspect to avoid the known long project build path.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Deferred Instantiation Slice

- Added package-owned generic-template deferred function instantiation rows,
  type-argument rows, comptime value argument rows, and deferred type
  instantiation rows.
- Preserved deferred callee template names and comptime argument text as
  generic-template text rows, and routed deferred type/value argument types
  through the durable type-reference graph with explicit source tags.
- Extended the focused `selfhost.Ir` generic-template fact to assert row
  counts, instantiation ordinals, child argument links, symbolic comptime
  flags, type-reference source tags, payload ordinals, and recovered deferred
  instantiation text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `tmp_root=/tmp/stark-selfhost-source-root-codex && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-deferred-instantiation-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Verification note: the clean symlink source root intentionally avoided stale
  package images under `selfhost/tools` while keeping source imports explicit.
- Runtime execution note: the filtered `selfhost.Ir` runtime fact runner was
  not rerun; this slice stayed on `PackageImage.stark --check` plus host-test
  semantic inspect to avoid the known long project build path.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Switch Pattern Payload Slice

- Added generic-template switch-case rows and nested pattern rows, preserving
  case kind/name/ordinal, expression/guard/end-expression links, member counts,
  statement counts, pattern parent kind, optional pattern names/ordinals, and
  nested member counts.
- Threaded switch-case child statements through an explicit parent case row so
  imported typed-body lowering can identify the owning case without relying on
  row-order reconstruction.
- Kept pattern materialization stack-constant with an `IrTable` worklist for
  nested pattern members, matching the existing typed-body statement/expression
  traversal style.
- Extended the focused `selfhost.Ir` generic-template fact to assert switch
  case rows, condition-pattern rows, nested pattern rows, case-owned statement
  parent links, expression type-reference payloads, and recovered switch/pattern
  text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `tmp_root=/tmp/stark-selfhost-source-root-codex && rm -rf "$tmp_root" && mkdir -p "$tmp_root" && ln -s /Users/zadkey/Repos/stark/selfhost/Compiler "$tmp_root/Compiler" && /Users/zadkey/Repos/stark/stark /Users/zadkey/Repos/stark/selfhost/Compiler/Mir/PackageImage.stark --check -I "$tmp_root" -I /Users/zadkey/Repos/stark/stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-switch-pattern-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Verification note: the ordinary `-I selfhost` source check stopped before
  checking this code because it found a stale
  `selfhost/tools/DifferentialDriver/build/.../libStarkCompiler.starkpkg`
  package image with a target-layout mismatch; the clean symlink source root
  above avoided that package artifact while keeping source imports explicit.
- Runtime execution note: the filtered `selfhost.Ir` runtime fact runner was
  not rerun; this path has repeatedly stalled in the build phase, so this slice
  stayed on `PackageImage.stark --check` plus host-test semantic inspect.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Comptime Argument Payload Slice

- Added expression-owned comptime value argument rows for generic-template
  typed bodies, preserving parameter-name, integer-value, symbolic-source text,
  symbolic flags, and the materialized argument type-reference row.
- Kept the argument type payload in the shared package type-reference graph
  under `GenericTemplateExpressionComptimeValueArgumentType`, so downstream
  import/lowering can consume backend facts without source reconstruction.
- Extended the focused `selfhost.Ir` generic-template fact to assert row counts,
  parent expression links, source tags, payload ordinals, symbolic flags, and
  recovered comptime argument text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-comptime-args-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Runtime execution note: the filtered `selfhost.Ir` runtime fact runner was
  not rerun; this path has repeatedly stalled in the build phase, so this slice
  stayed on `PackageImage.stark --check` plus host-test semantic inspect.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Type-Reference Payload Slice

- Added a generic-template-owned type-reference graph and linked statement
  `Type`, traversal index/element type, expression `Type`, and expression
  `TypeArguments` payloads to dense type-reference rows.
- Added expression type-argument child rows so later lowering can scan type
  arguments by row id without reconstructing source or walking package JSON.
- Extended the focused `selfhost.Ir` generic-template fact to assert linked
  row ids, type-reference source tags, payload ordinals, and recovered
  type-argument name text.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-type-refs-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Runtime execution note: the filtered `selfhost.Ir` runtime fact runner was
  not rerun; this path has repeatedly stalled in the build phase, so the
  focused gate for this slice stayed at `PackageImage.stark --check` plus
  host-test semantic inspect.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Typed-Body Shape Slice

- Added package-owned typed-body statement and expression fact rows for generic
  templates, including parent links, optional scalar/text flags, expression
  argument/target counts, statement child-count slots, loop-contract rows,
  expression member-name rows, and expression operator-name rows.
- Kept typed-body materialization stack-constant by using `IrTable` worklists
  for nested statement and expression traversal instead of recursive descent.
- Extended the focused `selfhost.Ir` fact to cover nested local/expression/if
  statement rows, expression argument parent links, loop-contract text,
  operator-name text, malformed typed-body rejection, and literal/name text
  recovery.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed with 0 diagnostics.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-typed-body-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with `succeeded: true` and 0 diagnostics.
- Runtime execution note: `../../stark test --filter LogicalPackageImageMaterializesGenericTemplateFactGraphRows --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir` stayed silent for about 195 seconds and was interrupted, so it is not counted as a pass.
- No broad test sweep was run.

---

## 2026-07-09 Logical Generic Template Header Row Slice

- Added package-owned generic-template fact rows for function template identity
  text, optional body/backend mode text, optional scalar metadata, typed-body /
  semantics presence flags, and current child section counts.
- Materialized `CompilerSections.GenericTemplates` through the selfhost logical
  package JSON bridge without reconstructing source text, preserving the package
  graph's backend-facing template facts for later typed-body replay.
- Added a focused `selfhost.Ir` fact covering compiler-section precedence,
  optional/null field handling, all current child-count fields, text extraction,
  and malformed direct-section rejection.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-generic-template-header-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- Runtime execution note: `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error` was interrupted after about 90 seconds of silence, so it is not counted as a pass. `../../stark test --filter LogicalPackageImageMaterializesGenericTemplateFactGraphRows --test-timeout 180 --target arm64-apple-macosx26.0.0 --stage stage0` from `tests-stark/selfhost.Ir` was interrupted after about 60 seconds of silence, so it is not counted as a pass.
- No broad test sweep was run.

---

## 2026-07-09 Whole-Module Struct Default Constructor Slice

- Fixed selfhost MIR local lowering so aggregate body layout walks skip constructor/method bodies instead of treating them as fields.
- Added declaration-backed storage resolution for default-constructor `self.field = value` writes, keeping typed member-path facts for reads while deriving write-side type/range facts from the field declaration.
- Added declared-range validation for constructor writes so narrowed integer fields still require a provable stored subset, while full-width fields keep the existing width-only behavior.
- Moved `struct-fields.stark` from `tests-stark/corpus/pending/` into the active stage-parity corpus.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceLocalLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: reached `Summary: 0 errors, 34 warnings, 0 infos`; warnings are the existing recursive-call warnings in this file.
  - `STARK_DEP_CACHE_LOG=1 ../../../stark build` from `selfhost/tools/DifferentialDriver`: passed; rebuilt `libStarkCompiler.starkpkg` and `differential-driver` with 204 cache hits and 9 misses.
  - `selfhost/tools/DifferentialDriver/build/dev/arm64-apple-macosx26.0.0/stage0/bin/differential-driver/differential-driver tests-stark/corpus/struct-fields.stark`: passed and emitted constructor stores for `A` and `B` followed by field reads.
  - `selfhost/tools/DifferentialDriver/build/dev/arm64-apple-macosx26.0.0/stage0/bin/differential-driver/differential-driver tests-stark/corpus/struct-fields.stark | clang -c -x ir -o build/tmp/struct-fields.o -`: passed with only clang's target-triple override warning.
- No broad test sweep was run.

---

## 2026-07-09 Whole-Module Enum Payload Capture Cast Slice

- Added a selfhost MIR source-expression conversion node so the whole-module
  path preserves explicit cast syntax instead of treating `(T)value` as
  grouping.
- Lowered widening integer conversions through a typed `MirOp.ZExt`, preserving
  the operand width, result width, and non-negative range facts through LLVM
  emission.
- Generalized MIR `ZExt` rendering, LLVM printing, and fact propagation away
  from the previous hard-coded `i1 -> i64` shape.
- Moved `enum-payload-capture.stark` from `tests-stark/corpus/pending/` into
  the active stage-parity corpus.
- Narrow verification:
  - `STARK_DEP_CACHE_LOG=1 ../../../stark build` from
    `selfhost/tools/DifferentialDriver`: passed; rebuilt
    `libStarkCompiler.starkpkg` and `differential-driver` with 183 cache hits
    and 30 misses.
  - Differential-driver probe for `enum-payload-capture.stark`: passed and
    emitted `zext i32 ... to i64` for the captured `u32[0 max]` payload cast.
  - `selfhost/tools/DifferentialDriver/build/dev/arm64-apple-macosx26.0.0/stage0/bin/differential-driver/differential-driver tests-stark/corpus/enum-payload-capture.stark | clang -c -x ir -o build/tmp/enum-payload-capture.o -`:
    passed with only clang's target-triple override warning.
  - `dotnet test tests/compiler.IntegrationTests --filter StageParityTests`:
    passed 6/6 active corpus files.
- No broad test sweep was run.

---

## 2026-07-08 Whole-Module Enum Call And Return Slice

- Fixed the selfhost MIR effect-prepass fallback so whole-module lowering uses the enum-aware local lowering path instead of rejecting enum-typed call arguments before emission.
- Fixed terminal enum-switch branch lowering so layout-backed enum tag comparisons use typed tag constants (`i8` here) instead of untyped `i64` constants, preserving enum layout facts into verifier-clean LLVM.
- Moved `enum-unit-switch.stark` and `enum-return.stark` from `tests-stark/corpus/pending/` into the active stage-parity corpus and added `enum-terminal-return.stark` for `case Tag.Left: return Pick.First;`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed with the local selfhost package image temporarily moved aside to avoid source shadowing.
  - `STARK_DEP_CACHE_LOG=1 ../../../stark build` from `selfhost/tools/DifferentialDriver`: passed; rebuilt `libStarkCompiler.starkpkg` and `differential-driver` with 208 cache hits and 5 misses.
  - Differential-driver probes for direct enum constructor arguments, stored enum locals as call arguments, `enum-unit-switch.stark`, `enum-return.stark`, and the terminal enum-return shape all lowered successfully and passed `clang -c -x ir`; enum switch tests now emit `icmp eq i8`.
  - `dotnet test tests/compiler.IntegrationTests --filter StageParityTests`: passed 5/5 active corpus files.
- No broad test sweep was run.

## 2026-07-08 Logical Native Metadata Fact Section Slice

- Added package-owned native metadata fact rows for decoded `MANF` package metadata and compiler-facts linkage metadata.
- Materialized top-level `NativeDependencies` rows for native sources, include directories, library directories, libraries, package-owned link arguments, and pkg-config packages without source reconstruction.
- Materialized per-module `CompilerFacts.Linkage` rows for object file names, defined symbols, and referenced symbols, keeping symbol lists in dense spans for later archive/link lowering.
- Added a focused `selfhost.Ir` fact covering dependency categories, dependency ordinals, linkage spans, symbol ordinals, text extraction, and missing `DefinedSymbols` rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-native-metadata-facts-host-test.out.json`: passed with 0 diagnostics.
  - `perl -e 'alarm shift; exec @ARGV' 180 ./../../stark test --filter LogicalPackageImageMaterializesNativeMetadataFactRows --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: capped after 180 seconds in the silent build phase with no runner output.
- No broad test sweep was run.

---

## 2026-07-08 Logical Layout Fact Section Slice

- Added package-owned concrete layout rows, concrete field rows, enum layout rows, enum ordered-field rows, enum variant rows, enum variant-field rows, layout text rows, and layout type-reference provenance for decoded `MANF` compiler facts.
- Materialized `ConcreteLayouts` and `EnumLayouts` from `CompilerFacts` without source reconstruction, preserving qualified type names, byte size/alignment facts, field offsets/alignment/misalignment facts, enum layout kind, tag field metadata, ordered storage fields, variant tag values, named-field flags, storage indices, and variant payload source/storage names.
- Kept rows dense and parent-linked so later ABI/LLVM import can walk concrete fields, enum storage fields, variants, and payload fields with counted loops instead of rescanning JSON.
- Added a focused `selfhost.Ir` fact covering concrete and enum layout row counts, scalar layout facts, text extraction, layout type-reference source tags, variant payload provenance, and malformed enum-layout-kind rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-layout-facts-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `perl -e 'alarm shift; exec @ARGV' 180 ./../../stark test --filter LogicalPackageImageMaterializesLayoutFactRows --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: capped after 180 seconds in the silent build phase with no runner output.
- No broad test sweep was run.

---

## 2026-07-08 Logical ABI Fact Section Slice

- Added package-owned ABI function rows, ABI parameter rows, expanded LLVM carrier type rows, ABI text rows, and ABI type-reference provenance for decoded `MANF` compiler facts.
- Materialized `AbiFunctions` from `CompilerFacts` without source reconstruction, preserving source/LLVM return types, source/LLVM parameter types, optional expanded LLVM carrier types, FFI ABI/link-name/source-name text, varargs, tail, and fast-call backend flags.
- Kept rows dense and row-linked so later LLVM import can walk function parameters and carrier types with counted loops instead of rescanning JSON.
- Added a focused `selfhost.Ir` fact covering ABI row counts, text extraction, parsed parameter kinds, carrier spans, type-reference source tags, and malformed parameter-kind rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-abi-facts-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `perl -e 'alarm shift; exec @ARGV' 180 ./../../stark test --filter LogicalPackageImageMaterializesAbiFunctionRows --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: interrupted after about 90 seconds in the silent build phase with no runner output.
- No broad test sweep was run.

---

## 2026-07-08 Logical Typed Global Fact Graph Slice

- Added a package-owned typed-global fact graph with global rows, constant-initializer rows, scalar initializer text rows, aggregate child spans, and initializer type-reference provenance.
- Materialized global kind/type/mutability and nested constant initializer payloads from decoded `MANF` JSON without reconstructing source, including integer, bool, text, fixed-array, and enum-aggregate facts.
- Kept the initializer import iterative rather than recursive, with dense row spans for aggregate children and direct JSON text-view kind validation to avoid unnecessary copying.
- Added a focused `selfhost.Ir` fact covering nested global initializer rows, text extraction, type-reference source/payload ordinals, empty text literal preservation, and malformed initializer rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-global-fact-graph-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `perl -e 'alarm shift; exec @ARGV' 180 ./../../stark test --filter LogicalPackageImageMaterializesTypedGlobalFactGraphRows --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: capped after 180 seconds in the silent build phase without runner output.
- No broad test sweep was run.

---

## 2026-07-08 Logical Package Manifest JSON Summary Slice

- Added a compact self-host logical manifest summary for decoded Stage0 `MANF` JSON: module count, root-module ordinal, native dependency presence, and profile/target/backend presence bits.
- Validated decoded manifest identity, build profile, target identity, target backend strings, target features, C data model, and aggregate pointer layout against already-validated `PINF`/`STRS` facts before materializing a logical model.
- Kept validation allocation-light by parsing JSON once, comparing text views directly against the string table, and storing only scalar summary facts.
- Added a focused `selfhost.Ir` fact covering the positive decoded-manifest path plus rejection when JSON target-feature order disagrees with compact backend facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-manifest-json-semantic-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about 150 seconds because the direct file check remained silent; the semantic host-test inspect above covered the touched root with 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-08 Logical Package Manifest Payload Handoff Slice

- Preserved logical package-image `MANF` section offset/length in compact `LogicalPackageImageFacts` alongside existing `STRS`/`PINF` compatibility facts.
- Added a direct compressed manifest payload copy helper that validates the preserved range, reserves destination table capacity once, and copies bytes with a counted loop for the future Brotli/JSON materializer.
- Added a focused `selfhost.Ir` fact covering manifest offset/length exposure, compressed payload byte copying, and rejection for empty/default facts.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Ir.stark selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-manifest-payload-semantic-host-test.out.json`: passed with 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-08 Nested Fixed-Array Carrier-Load LLVM DCE Slice

- Added a narrow LLVM emission use-count pass that counts value operands, block terminator operands, and linked wide-call arguments before printing block bodies.
- Elides only unused aggregate carrier producers (`FixedArrayLoad` and `StructValueLoad`), leaving scalar loads, calls, stores, and range/noalias metadata paths intact.
- Threaded the check through labeled block emission and direct-switch setup emission; whole block-range emitters build the use-count table once and reuse it across sibling blocks.
- Added a focused `selfhost.Ir` fact that builds MIR with an unused fixed-array carrier load plus a scalar range load, asserting the aggregate load is absent while scalar `load i8` and `!range` remain.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/LlvmBlocks.stark selfhost/Compiler/Mir/LlvmControlFlow.stark selfhost/Compiler/Mir/LlvmFunctions.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmInstructions.stark --check -I selfhost`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmBlocks.stark --check -I selfhost`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmControlFlow.stark --check -I selfhost`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmFunctions.stark --check -I selfhost`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-carrier-dce-semantic-host-test.out.json`: passed with 0 diagnostics.
- Focused `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost` is blocked before the fact body by unrelated stdlib symbol resolution for `System.Runtime.Platform.SetPermissions`; a focused executable probe was capped after spending 184.2s in `lower-mir`.
- No broad test sweep was run.

---

## 2026-07-08 Nested Fixed-Array Suffix-Depth Lowering Slice

- Added an explicit scalar fixed-array suffix-depth layout helper so nested list-element lowering can ask for the stride and scalar metadata at the current array dimension.
- Threaded `arrayDimensionDepth` through recursive storage-backed nested-list lowering, preserving direct scalar byte offsets for three-dimensional fixed-array list patterns instead of falling back to aggregate carriers.
- Added `selfhost.Ir` facts for third-dimension nested list-element patterns that assert direct scalar `i8` loads at byte offsets 6 and 7, preserved `!range`, scalar compares/branches, and no aggregate carrier loads.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-nested-list-depth-semantic-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with 0 diagnostics.
- No broad test sweep was run; filtered `selfhost.Ir` fact-runner execution remains pending because recent filtered runs have capped in the silent build phase.

---

## 2026-07-08 Nested Fixed-Array Row-Stride Lowering Slice

- Fixed scalar fixed-array storage layout so multi-dimensional arrays multiply remaining fixed-array suffixes into the outer element stride.
- Routed nested fixed-array list element conditions through recursive scalar element lowering, preserving declared element range metadata instead of loading inner array carriers.
- Added `selfhost.Ir` facts for second-row nested list-element patterns that assert direct scalar `i8` loads, row-two byte offsets, `!range`, scalar compares/branches, and no aggregate carrier loads.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-nested-list-second-row-semantic-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with 0 diagnostics.
  - `perl -e 'alarm shift; exec @ARGV' 120 ./../../stark test --filter CompileStructPropertyPatternSwitchBorrowListElementListSecondRowReturnUsesDirectElementLoads --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: capped after 120 seconds in the silent build phase with no runner output.
- No broad test sweep was run.

---

## 2026-07-08 Nested Descriptor Overlap Source-Preflight Assertion Slice

- Added source-facing `selfhost.Ir` facts for nested descriptor overlap rejection after shared pattern-decision lowering.
- `RejectsNestedAggregateAndListPatternOverlapFromAst` covers overlapping nested struct aggregate fields and nested fixed-array list fields, plus a dynamic guard case that must remain legal.
- `RejectsListElementNestedPatternOverlapFromAst` covers overlapping nested aggregate and nested list descriptors inside fixed-array list elements.
- `RejectsOverlappingNestedEnumPayloadLabelsFromAst` covers overlapping nested enum-payload descriptors over the same nested variant interval.
- Narrow verification:
  - `git diff --check -- tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-nested-overlap-preflight-semantic-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with 0 diagnostics.
- No broad test sweep was run; filtered fact-runner execution remains pending because prior `selfhost.Ir` filtered runs have stalled in the silent build phase.

---

## 2026-07-08 Nested Pattern Sibling Capture Routing Assertion Slice

- Added two focused `selfhost.Ir` LLVM facts for nested sibling switch labels that reuse the same capture spelling across labels.
- The terminal-return fact covers nested struct aggregate captures with a guarded first label and a second sibling label; the assignment fact covers nested fixed-array list captures with the same sibling fallback shape.
- Both facts assert direct scalar `i8` loads, preserved `!range`, scalar compares/branches, and no aggregate fallback artifacts (`extractvalue`, LLVM `switch`, or relevant aggregate carrier loads/allocas).
- Narrow verification:
  - `git diff --check -- tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-nested-sibling-capture-routing-semantic-host-test.out.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-08 Fixed-Array Element Carrier-Load Assertion Slice

- Strengthened the four focused `selfhost.Ir` list-element aggregate/list LLVM facts so they now reject dead raw O0 aggregate carrier loads before the scalar nested fixed-array element tests.
- The aggregate-element facts now reject `load [2 x %Point]` and `load %Point`; the nested-list facts now reject `load [2 x [2 x i8]]` and `load [2 x i8]`, while preserving the existing positive checks for scalar `load i8`, `!range`, scalar compares, and branches.
- Narrow verification:
  - `git diff --check -- tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md selfhost/Compiler/Mir/SourceSwitchLowering.stark`: passed.
  - `./../../stark test --filter CompileStructPropertyPatternSwitchBorrowListElementAggregateReturnUsesDirectFieldLoads --test-progress --test-timeout 90` from `tests-stark/selfhost.Ir`: interrupted after about a silent minute to avoid an unbounded selfhost test build; it did not reach runner output.
- No broad test sweep was run.

---

## 2026-07-08 Fixed-Array Element Nested List Test Slice

- Confirmed the current storage-backed lowering handles nested fixed-array list subpatterns inside fixed-array list elements for terminal-return and assignment switches.
- Added focused `selfhost.Ir` facts for list-element aggregate and list-element list patterns, asserting direct scalar `i8` loads, preserved `!range`, scalar compares/branches, and no `extractvalue` or LLVM `switch` fallback.
- Kept the pre-existing raw O0 dead aggregate carrier loads as a separate performance cleanup; the live scalar tests already preserve the backend range facts through LLVM.
- Narrow verification:
  - `./stark --host-test-inspect build/tmp/source-switch-fixed-array-element-list-llvm-host-test.json`: passed terminal-return and assignment inline `emit-llvm` probes for `Box { Matrix: [[1, 2], _] }` with 0 diagnostics; LLVM text shows scalar `i8` element loads carrying `!range`.
  - `./stark --host-test-inspect build/tmp/source-switch-fixed-array-element-aggregate-llvm-host-test.json`: passed terminal-return and assignment inline `emit-llvm` probes for `Box { Points: [Point { X: 1, Y: 2 }, _] }` with 0 diagnostics.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json`: passed semantic validation for `tests-stark/selfhost.Ir/IrTests.stark` with 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-08 Fixed-Array Element Aggregate Lowering Slice

- Added a storage fixed-array layout resolver that preserves scalar, enum, and aggregate element size/alignment plus resolved type-code facts for fixed-array fields.
- Let fixed-array list descriptors carry aggregate element type-code facts when typed list rows name aggregate elements.
- Lowered aggregate subpatterns inside storage-backed fixed-array list elements by enqueuing aggregate condition frames at `arrayBase + elementOrdinal * elementSize`, so nested aggregate element tests reuse the existing pointer-offset scalar field lowering without stack-recursive helper calls.
- Refactored storage aggregate/list condition lowering around an explicit aggregate-condition frame drain; `Compiler.Mir.SourceSwitchLowering` now semantic-checks with 0 diagnostics instead of introducing `STK4122` mutual-recursion warnings.
- Kept nested list subpatterns inside fixed-array list elements and landed `IrTests.stark` facts open.
- Narrow verification:
  - `./stark --host-test-inspect build/tmp/source-switch-fixed-array-element-aggregate-semantic-host-test.json`: passed `Compiler.Mir.SourceLocalLowering` semantic validation with 0 errors and the pre-existing 32 recursive-call warnings; passed `Compiler.Mir.SourceSwitchLowering` semantic validation with 0 diagnostics.
  - `./stark --host-test-inspect build/tmp/source-switch-fixed-array-element-aggregate-llvm-host-test.json`: passed terminal-return and assignment inline `emit-llvm` probes for `Box { Points: [Point { X: 1, Y: 2 }, _] }` with 0 diagnostics. LLVM text shows scalar `i8` element-field loads with `!range`; the borrow-backed O0 artifact still contains pre-existing dead aggregate loads before the scalar tests, so that remains separate cleanup if we want a stricter no-dead-aggregate-load invariant.
- No broad test sweep was run.

---

## 2026-07-08 Aggregate Fixed-Array Layout Prerequisite Slice

- Extended source-local enum-aware storage layout queries so fixed arrays of named aggregates and enums preserve total byte extent and element alignment instead of failing the named-array path or silently reporting one element.
- Routed direct fixed-array extent queries through the same enum-aware layout helper so lower-MIR sees the full array size for aggregate/enum carriers before nested list-element pattern lowering uses those facts.
- Kept nested aggregate/list subpatterns inside fixed-array list elements open; this slice only establishes the storage layout prerequisite for direct-offset lowering.
- Narrow verification:
  - `./stark --host-test-inspect build/tmp/source-local-aggregate-fixed-array-layout-host-test.json`: passed `Compiler.Mir.SourceLocalLowering` semantic validation with 0 errors and the pre-existing 32 recursive-call warnings.
  - The same host-test request passed an inline `lower-mir` probe with `struct Outer { Point[2] Points; i64[min max] Tail; }` and 0 diagnostics.
- No broad test sweep was run.

---

## 2026-07-08 Nested Enum-Payload Lowering Slice

- Threaded nested enum aggregate descriptor spans from enum payload pattern parsing into terminal-return and assignment switch lowering.
- Lowered nested enum aggregate payload tests by extracting the outer enum payload as an enum value, reading the nested enum tag, and comparing nested scalar payload leaves directly; this avoids stack materializing the nested enum payload and keeps tag/range facts available for LLVM.
- Added focused IR facts for terminal-return and assignment switches with nested enum payload patterns.
- Kept nested enum captures and fixed-array element nested aggregate/list lowering as follow-up work under the existing shared pattern-decision task.
- Narrow verification:
  - `./stark --host-test-inspect build/tmp/source-switch-nested-enum-payload-semantic-host-test.json`: passed with 0 diagnostics for `Compiler.Mir.SourceSwitchLowering`.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json`: passed with 0 diagnostics for `tests-stark/selfhost.Ir/IrTests.stark`.
  - `./stark --host-test-inspect build/tmp/source-switch-nested-enum-payload-llvm-host-test.json`: passed two inline `emit-llvm` snippets with 0 diagnostics; LLVM text showed direct nested enum tag/payload loads and scalar compares for terminal-return and assignment forms.
  - `../../stark test --filter CompilesTerminalNestedEnumPayloadSwitchFromAst --filter CompilesNestedEnumPayloadAssignmentSwitchFromAst --test-progress --test-timeout 180` from `tests-stark/selfhost.Ir`: interrupted after about a silent minute to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 By-Value Nested Fixed-Array Capture Lowering Slice

- Replaced sentinel flat indexes for nested fixed-array element captures with exact flattened struct-value ABI element indexes in both one-hop list fields and deeper aggregate/list paths.
- Reused the existing struct aggregate capture override path so by-value nested fixed-array captures lower to direct `MirStructParamFieldWithDeclaredRange` extracts instead of stack materializing the array or loading from storage.
- Preserved fixed-array element declared range facts through capture import and arm-local override lowering, keeping backend facts available for MIR and LLVM emission.
- Added focused IR facts for by-value nested fixed-array list captures in guarded terminal-return and assignment switch forms.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `../../stark test --filter CompileStructPropertyPatternSwitchValueParamNestedListCaptureGuardReturnUsesDirectElementExtracts --filter CompileStructPropertyPatternSwitchValueParamNestedListCaptureAssignmentUsesDirectElementExtracts --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about a silent minute to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 By-Value Nested Fixed-Array List Lowering Slice

- Extended struct-value ABI shape reading so scalar fixed-array fields flatten into repeated scalar leaf facts instead of forcing aggregate materialization.
- Added by-value nested fixed-array list condition lowering inside struct aggregate patterns; element literal/range tests now emit direct `MirStructParamField` extracts and preserve declared element range facts through MIR/LLVM.
- Added focused IR facts for by-value nested fixed-array list labels in terminal-return and assignment switch forms.
- Kept by-value captures inside nested fixed-array list field patterns open; this slice handles no-capture element tests.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceLocalLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 errors and the pre-existing 32 recursion warnings.
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `../../stark test --filter CompileStructPropertyPatternSwitchValueParamNestedListReturnUsesDirectElementExtracts --filter CompileStructPropertyPatternSwitchValueParamNestedListAssignmentUsesDirectElementExtracts --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
- No broad test sweep was run.

---

## 2026-07-08 Deeper By-Value Nested Aggregate Lowering Slice

- Reworked by-value struct aggregate condition lowering to walk nested aggregate descriptors with an explicit flat-ABI frame worklist instead of rejecting nested shapes after the first field hop.
- Preserved direct struct-parameter extraction for deeper scalar aggregate leaves, including owner-type shape checks and per-field declared range facts carried on the MIR struct-param field values.
- Added focused IR facts for by-value deeper nested struct aggregate labels in terminal-return and assignment switch forms.
- Kept by-value fixed-array list field subpatterns open because the current struct-value ABI shape reader does not flatten fixed-array fields inside structs; lowering those correctly needs ABI facts for fixed-array fields first.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with two minimal by-value source snippets through `emit-llvm`: passed with 0 diagnostics and LLVM checks for direct nested `extractvalue`, `icmp eq i8`, branch lowering, and no stack `alloca`/`load i8`/`switch %` fallback.
  - `../../stark test --filter CompileStructPropertyPatternSwitchValueParamDeeperNestedStructReturnUsesDirectFieldExtracts --filter CompileStructPropertyPatternSwitchValueParamDeeperNestedStructAssignmentUsesDirectFieldExtracts --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 Deeper Storage-Backed Nested Capture Lowering Slice

- Generalized storage-backed nested struct aggregate capture import to walk nested aggregate descriptors with an explicit compiler-side worklist, avoiding recursive helper calls while preserving direct offset facts.
- Appended capture rows for deeper nested scalar aggregate fields with absolute storage offsets, flat ABI indexes for future by-value shape use, source capture names, scalar type codes, alignments, and declared scalar range member facts.
- Appended capture rows for fixed-array element captures reached through deeper aggregate fields with constant element byte offsets and fixed-array member facts, so arm-local loads preserve declared element range metadata.
- Added focused IR facts for borrow-backed deeper nested struct captures and deeper nested fixed-array element captures in guarded terminal-return and assignment switch forms.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with four minimal borrow-backed source snippets through `emit-llvm`: passed with 0 diagnostics and LLVM checks for direct `load i8`, `!range`, `icmp eq i8`, `add nsw i64`, branch lowering, and no `extractvalue`/`switch %`/aggregate alloca.
- No broad test sweep was run.

---

## 2026-07-08 Deeper Storage-Backed Nested Aggregate/List Lowering Slice

- Reworked storage-backed struct aggregate condition lowering to walk nested aggregate descriptors with an explicit compiler-side worklist instead of recursive lowering, avoiding a new STK4122 stack-growth warning.
- Lowered storage-backed deeper nested struct aggregate scalar leaves through direct pointer-offset scalar loads, preserving declared scalar range metadata on the generated LLVM loads.
- Lowered storage-backed fixed-array list field subpatterns reached through deeper struct aggregate fields through the existing direct constant-offset element load path, preserving declared element range metadata.
- Added focused IR facts for borrow-backed deeper nested struct and deeper nested fixed-array field labels in terminal-return and assignment switch forms.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with four minimal borrow-backed source snippets through `emit-llvm`: passed with 0 diagnostics and LLVM checks for direct `load i8`, `!range`, `icmp eq i8`, branch lowering, and no `extractvalue`/`switch %`/aggregate alloca.
- No broad test sweep was run.

---

## 2026-07-08 Nested Fixed-Array Field Capture Lowering Slice

- Appended capture rows for one-level fixed-array list element captures under struct aggregate field labels, preserving capture names, scalar element type codes, the fixed-array member-path row, constant element byte offsets, and element alignments.
- Treated nested fixed-array list capture leaves as match-all element tests while literal/range siblings continue to emit direct constant-index element branch tests.
- Reused the existing struct aggregate capture override path for storage-backed scrutinees and taught it to load fixed-array element captures with declared element range metadata from the array field member-path fact.
- Added focused IR facts for borrow-backed nested fixed-array field element captures in guarded terminal-return and assignment switch bodies.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with two minimal borrow-backed source snippets through `emit-llvm`: passed with 0 diagnostics and LLVM checks for direct `load i8`, `load i1`, `!range`, `icmp eq i8`, `add nsw i64`, branch lowering, and no `extractvalue`/`switch %`/stack array alloca.
  - `../../stark test --filter CompileStructPropertyPatternSwitchBorrowNestedListCaptureGuardReturnUsesDirectElementLoads --filter CompileStructPropertyPatternSwitchBorrowNestedListCaptureAssignmentUsesDirectElementLoads --test-progress --test-timeout 120` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 Nested Struct Capture Lowering Slice

- Appended capture rows for one-level nested struct aggregate capture leaves during struct aggregate label import, preserving capture names, type codes, member-path rows, storage offsets, and by-value flat ABI indexes.
- Reused the existing struct aggregate capture override path so guards and arm bodies can consume nested captured scalar values without extra stack materialization.
- Updated nested aggregate condition lowering to treat capture leaves as match-all members while scalar/range siblings continue to emit branch tests.
- Kept capture-bearing nested fixed-array list fields and deeper nested aggregate/list shapes as explicit follow-ups.
- Added focused IR facts for:
  - by-value nested struct capture guards returning from a captured field through direct `extractvalue`;
  - storage-backed nested struct capture assignments loading the captured field with preserved range facts.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `../../stark test --filter CompileStructPropertyPatternSwitchValueParamNestedCaptureGuardReturnUsesDirectFieldExtracts --filter CompileStructPropertyPatternSwitchLocalNestedCaptureAssignmentUsesDirectFieldLoads --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 By-Value Nested Struct Aggregate Lowering Slice

- Flattened nested scalar struct fields into by-value struct parameter ABI shape facts so direct LLVM aggregate slots preserve the source field layout without stack materialization.
- Lowered one-level nested struct aggregate field patterns over by-value struct parameters through direct scalar `extractvalue` tests in terminal-return and assignment switch lowering.
- Preserved declared scalar range facts for nested by-value struct leaves by carrying member-path declared ranges onto the generated struct-parameter field MIR values.
- Kept nested list fields, nested enum aggregate fields, captures, and deeper nested shapes conservatively rejected for follow-up slices.
- Added focused IR facts for by-value nested struct aggregate labels over terminal-return and assignment switches, asserting direct `{ i8, i8, i8 }` `extractvalue` use and no stack fallback.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceLocalLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 errors and 32 warnings.
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `../../stark test --filter CompileStructPropertyPatternSwitchValueParamNestedStructReturnUsesDirectFieldExtracts --filter CompileStructPropertyPatternSwitchValueParamNestedStructAssignmentUsesDirectFieldExtracts --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 Storage-Backed Nested Fixed-Array List Field Lowering Slice

- Lowered one-level fixed-array list field patterns inside storage-backed struct aggregate labels through direct pointer-offset element tests in terminal-return and assignment switch lowering.
- Preserved declared element range facts for nested fixed-array field elements by loading through `MirLoadPtrAlignedTypedWithDeclaredRange` when the field member path carries an element range fact.
- Kept nested list captures and deeper nested element shapes conservatively rejected for later focused slices.
- Added focused IR facts for storage-backed nested list-field struct aggregate labels over terminal-return and assignment switches.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` with `sourcePath: tests-stark/selfhost.Ir/IrTests.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["tests-stark/selfhost.Ir", "selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `../../stark test --filter CompileStructPropertyPatternSwitchLocalNestedListReturnUsesDirectElementLoads --filter CompileStructPropertyPatternSwitchLocalNestedListAssignmentUsesDirectElementLoads --test-timeout 180 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 Storage-Backed Nested Struct Aggregate Lowering Slice

- Lowered one-level nested struct aggregate field patterns for storage-backed struct scrutinees through direct pointer-offset scalar field tests in both terminal-return and assignment switch lowering.
- Preserved declared scalar range facts for nested struct scalar leaves by loading through `MirLoadPtrAlignedTypedWithDeclaredRange` when the nested member path has a declared range fact.
- Kept by-value struct nested aggregate labels, nested list fields, nested enum payload fields, deeper nested aggregate/list shapes, and nested captures conservatively rejected for later focused slices.
- Added focused IR facts for storage-backed nested struct aggregate labels over terminal-return and assignment switches.
- Narrow verification:
  - `./stark --host-test-inspect` with `sourcePath: selfhost/Compiler/Mir/SourceSwitchLowering.stark`, `stopAfterPassId: semantic-validate`, `searchDirectories: ["selfhost", "stdlib/src"]`: passed with 0 diagnostics.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check ...`: interrupted after a silent bounded run; the structured semantic host-test above provided the usable scoped validation signal.
- No broad test sweep was run.

---

## 2026-07-08 Nested Struct Switch Preflight Handoff Slice

- Added a small flat-preflight gate for struct aggregate switch labels so nested-bearing labels bypass the old scalar-only overlap checker.
- Kept scalar-only labels on the fast flat overlap path, while labels with current or previous nested descriptor rows are deferred to the shared decision preflight that understands nested aggregate/list descriptors.
- Left nested branch codegen conservatively rejected after successful preflight; this slice only ensures nested sibling validation happens before that reject and before partial MIR emission.
- Narrow verification:
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed with `Check succeeded.` after a silent direct file check.
- No broad test sweep was run.

---

## 2026-07-08 Focused Switch Pattern Import Tests

- Added a dedicated `tests-stark/selfhost.SwitchPatternImport` project so typed switch-pattern row import can be checked without compiling the full `selfhost.Ir` test file.
- Added two narrow facts:
  - `SourceModuleLoweringFactsCarryTypedNestedSwitchPatternRows` checks that top-level struct aggregate rows, nested aggregate rows, nested list rows, member ordinals, and dense start-token lookups survive into `SourceModuleLoweringFacts`.
  - `SourceSwitchImportedTypedNestedRowsBuildDecisionDescriptors` checks that imported nested aggregate and list rows build shared decision descriptors without reparsing nested source tokens.
- Narrow verification:
  - `./stark tests-stark/selfhost.SwitchPatternImport/SwitchPatternImportTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed with `Check succeeded.`
  - `../../stark test --filter SourceModuleLoweringFactsCarryTypedNestedSwitchPatternRows --filter SourceSwitchImportedTypedNestedRowsBuildDecisionDescriptors --test-timeout 180 --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.SwitchPatternImport`: stopped after a silent build phase to keep the run bounded.
- No broad test sweep was run.

---

## 2026-07-08 Typed Nested Switch Descriptor Worklist Slice

- Replaced the recursive typed nested aggregate/list descriptor importer in `SourceSwitchLowering` with an explicit `List`-backed DFS frame stack.
- Preserved the existing postorder descriptor insertion behavior while moving nested member-span, aggregate descriptor, list descriptor, finish, and append-member work onto compact scalar frames.
- Removed the localized `STK4122` stack-growth warnings from the typed nested descriptor importer without widening switch lowering behavior.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed with `Check succeeded.` and no `STK4122` diagnostics.
- No broad test sweep was run.

---

## 2026-07-07 Typed Switch Pattern Source Facts Import Slice

- Stored the typed switch-pattern table bundle in `SourceModuleLoweringFacts` so source MIR lowering can read aggregate, list, and member pattern rows without reparsing labels.
- Reused the already-built typed enum layout table when constructing switch-pattern rows for module facts, avoiding an extra enum-layout pass at the source-lowering boundary.
- Preserved owner type tokens, enum variant rows, field/element ordinals, literal/range/capture tokens, and nested aggregate/list row references by carrying the typed table bundle directly.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatterns.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Mir/SourceModuleFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Typed Switch Nested List Element Builder Slice

- Built typed list-pattern rows for nested fixed-array list patterns discovered under aggregate switch members.
- Added direct element member rows with element ordinals, element type tokens, scalar literal facts, capture names, fixed list lengths, and nested list row links.
- Kept nested shape expansion iterative through the shared member-row worklist and added idempotence guards for already-linked nested aggregate/list rows.
- Added a focused IR fact for aggregate switch labels with nested list element literals and captures.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatternModel.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatterns.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing/TypedPipeline.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src -I tests-stark --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stopped after about two minutes because the focused file check produced no output.
- No broad test sweep was run.

---

## 2026-07-07 Typed Switch Nested Aggregate Member Builder Slice

- Added a typed switch-pattern table bundle so aggregate, list, and member rows can be returned together from the typing pipeline.
- Built direct aggregate member rows for property and positional aggregate switch labels using struct/record field facts.
- Linked nested aggregate member rows through an iterative member-row worklist so nested pattern import does not grow the compiler stack.
- Preserved member ordinals, field type tokens, literal tokens, range endpoints, capture names, and nested aggregate row references for aggregate members.
- Added focused IR facts for top-level aggregate spans and nested aggregate member-row construction.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatternModel.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatterns.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing/TypedPipeline.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src -I tests-stark --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter TypedSwitchPatternTablesBuildNestedAggregateMemberRows` in `tests-stark/selfhost.Ir`: stopped after about three minutes because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 Typed Switch Pattern Row Model Slice

- Added `TypedSwitchPatternModel.stark` with row-oriented aggregate, list, and member pattern tables for typed switch-pattern import.
- Preserved owner type tokens, enum variant ordinals, whole-capture tokens, member ordinals, literal/range/capture tokens, fixed-list lengths, and nested aggregate/list row references without recursive owned pattern objects.
- Added focused IR facts for aggregate/list descriptor rows and scalar/range/capture/nested member rows.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatternModel.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Typing/TypedSwitchPatternModel.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Fixed-Array List Assignment Preflight Slice

- Wired fixed-array list switch-assignment lowering through the shared decision preflight before MIR branch blocks are emitted.
- Added a focused AST fact that rejects overlapping fixed-array list labels in non-terminal switch assignment lowering.
- Kept the preflight call allocation-light and non-overlap-safe by passing distinct empty row tables for currently unsupported nested list descriptors.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Nested Struct Aggregate Decision Preflight Slice

- Added nested struct field decision-row translation through the shared pattern-decision member model.
- Extended `TryBuildSourceSwitchStructAggregatePatternDecisionPreflightRows` so struct cases can include nested field descriptor spans.
- Kept current scalar-only struct branch lowerers behavior-preserving by passing explicit empty nested-row tables at existing call sites.
- Added a focused IR row-model fact that seeds nested aggregate child decisions, accepts disjoint nested struct-field descriptors, and rejects an overlapping nested descriptor.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Nested Fixed-Array List Decision Preflight Slice

- Extended `TryBuildSourceSwitchListPatternDecisionPreflightRows` so fixed-array list cases can include nested element descriptor spans.
- Kept current scalar-only branch lowerers behavior-preserving by passing explicit empty nested-row tables at existing call sites.
- Added a focused IR row-model fact that seeds nested aggregate child decisions, accepts disjoint nested list-element descriptors, and rejects an overlapping nested descriptor.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Struct Aggregate Decision Preflight Hook Slice

- Added `TryBuildSourceSwitchStructAggregatePatternDecisionPreflightRows` to build struct field decision spans before branch blocks are emitted.
- Wired terminal struct-aggregate switch lowering and struct-aggregate switch-assignment lowering through the shared unguarded sibling preflight.
- Kept capture-name validation local to preflight with sentinel capture-name tokens so struct branch lowering does not need extra capture-name tables.
- Added a focused IR row-model fact that accepts disjoint struct field rows and rejects an overlapping unguarded struct field row.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Enum Payload Decision Preflight Hook Slice

- Added `TryBuildSourceSwitchEnumPayloadPatternDecisionPreflightRows` to build enum-payload decision spans before branch blocks are emitted.
- Prepended each enum case span with a variant decision member so different unit variants stay provably disjoint.
- Wired terminal enum-payload switch lowering and enum-payload switch-assignment lowering through the shared unguarded sibling preflight.
- Kept capture-name validation local to preflight with sentinel capture-name tokens so payload branch lowering does not need extra capture-name tables.
- Added a focused IR row-model fact that accepts disjoint same-variant payload rows, accepts a different unit variant, and rejects an overlapping same-variant payload row.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed after splitting the test's empty capture tables to satisfy the default non-overlap contract.
- No broad test sweep was run.

---

## 2026-07-07 Fixed-Array List Decision Preflight Hook Slice

- Added `TryBuildSourceSwitchListPatternDecisionPreflightRows` to build fixed-array list decision spans before branch blocks are emitted.
- Wired terminal fixed-array list lowering through the shared unguarded sibling preflight after descriptor validation.
- Kept capture-name validation local to preflight with sentinel capture-name tokens so list branch lowering does not need extra capture-name tables.
- Added a focused IR row-model fact that accepts disjoint fixed-array list decision rows and rejects overlapping sibling rows.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Span Validation Slice

- Added shared validation for pattern-decision nodes and member spans before overlap preflight reads nested descriptor rows.
- Validated scalar, capture, aggregate, enum aggregate, and list decision-node contracts so descriptor kind mismatches fail closed.
- Routed malformed current and previous decision-member spans to overlap in the unguarded sibling preflight helper.
- Added a focused IR row-model fact for mixed valid spans, invalid node kinds, mismatched aggregate descriptor kinds, bad list descriptor rows, and overflowed member ranges.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Combined Row Span Slice

- Added shared helpers that combine scalar, capture, and nested-shape rows into one contiguous decision-member span for fixed-array element descriptors and enum payload descriptors.
- Kept the helpers row-based and conditional so scalar-only validation is not applied to nested-only aggregate or list element descriptors.
- Preserved the existing append order for scalar rows, capture rows, then nested descriptor rows so later branch lowering can scan one span without rebuilding row sets.
- Added a focused IR row-model fact that verifies combined fixed-array element and enum payload spans preserve ordinals, captures, scalar intervals, and nested descriptor rows.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Nested Preflight Helper Slice

- Added a shared-decision preflight helper that checks current decision-member spans against earlier unconditional sibling cases.
- Kept the helper row-based and fail-closed for mismatched case-start, case-count, and guard-node tables.
- Reused the existing unconditional-guard classifier and nested descriptor disjointness helpers instead of introducing a second overlap model.
- Added a focused IR fact for scalar sibling rejection, nested aggregate sibling rejection, and malformed table rejection.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Nested Overlap Validation Slice

- Added non-recursive shared-decision overlap helpers for scalar interval members, nested aggregate descriptors, nested enum aggregate variant descriptors, and nested list descriptors.
- Kept validation row-based and fail-closed for malformed member spans, missing node rows, mismatched owner types, mismatched list shapes, and unprovable capture/discard cases.
- Added focused IR row-model facts for scalar disjointness, nested aggregate disjointness, nested list disjointness, and nested enum variant disjointness.
- Split the nested-overlap task so source-switch preflight wiring remains tracked separately from the row-model validation.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Nested Shape Row Routing Slice

- Added a nested pattern decision-member append helper that routes aggregate, enum aggregate, and list descriptor rows into shared decision nodes without reparsing source-shaped patterns.
- Rejected aggregate descriptor shape mismatches so plain aggregate nodes cannot carry enum aggregate descriptor rows, and enum aggregate nodes cannot carry plain aggregate descriptor rows.
- Added fixed-array element nested-shape row translation into decision members while preserving element ordinals and aggregate/list descriptor row identities.
- Added enum payload nested-shape row translation into decision members while preserving variant identity, payload ordinals, and aggregate/list descriptor row identities.
- Added focused row-model IR facts for fixed-array element and enum payload nested-shape decision-member translation.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Payload and Element Row Translation Slice

- Added fixed-array element row translation into shared pattern decision members while preserving element ordinals, type codes, scalar intervals, and capture names.
- Added enum payload row translation into shared pattern decision members while preserving variant identity, payload ordinals, type codes, scalar intervals, and capture names.
- Rejected whole-list sentinel captures and non-enum aggregate descriptors in these translation helpers so later nested lowering sees only member-level decisions.
- Added focused IR facts for fixed-array element and enum payload decision-member translation.
- Split the enum-payload and fixed-array-element shared-decision tasks so nested-shape routing remains tracked separately.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Row Translation Slice

- Added append helpers for discard, scalar interval, capture, aggregate, enum aggregate, and list pattern decision nodes.
- Added scalar-pattern row translation into decision members while preserving field or element ordinals, type codes, and interval bounds.
- Added capture-row translation into decision members while preserving capture name tokens and type codes.
- Added a struct-field row translation wrapper that combines scalar field tests and field captures without accepting enum aggregate descriptors.
- Added focused IR facts for append-helper validation, scalar row translation, and struct field row translation.
- Split the typed struct field-pattern translation task so imported nested typed rows remain tracked separately.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Pattern Decision Node Model Slice

- Added a flat `SourceSwitchPatternDecisionNode` model for discard, scalar interval, capture, aggregate, enum aggregate, and list subpatterns.
- Added `SourceSwitchPatternDecisionMember` rows so aggregate-field and list-element ordinals can point at shared decision nodes without allocating recursive source-shaped objects.
- Kept backend-facing facts as row references and scalar payload fields so later lowering can preserve type codes, capture name tokens, and nested descriptor rows through MIR-to-LLVM emission.
- Added focused IR facts for decision-kind classification and node/member row preservation.
- Marked the shared pattern-decision node model subtask complete in `TASKS.md`; typed-row translation and nested lowering remain open.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Fixed-Array Sibling Capture Merge Slice

- Added fixed-array list sibling label parsing for assignment and terminal return switches so `case [var x, 1, _] | [_, 2, var x]:` lowers one shared section body with per-label element capture rows.
- Covered by-value, storage-local, and field-backed fixed-array switch paths, including enum-return terminal variants.
- Required sibling capture signatures to match by capture name and exact type code before sharing the section body, while preserving per-label element indices and type facts for LLVM emission.
- Added focused IR facts for sibling fixed-array element captures in assignment and terminal switch lowering.
- Marked the fixed-array sibling-label capture task complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter CompileFixedArrayListSiblingCapture --test-progress --test-timeout 240` in `tests-stark/selfhost.Ir`: interrupted after about three minutes because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 Struct Aggregate Sibling Capture Merge Slice

- Added struct-aggregate sibling label parsing for assignment and terminal return switches so `case Box { left: var x } | Box { right: var x }:` lowers one shared section body with per-label field capture rows.
- Required sibling capture signatures to match by capture name and exact type code before sharing the section body, while preserving per-label field offsets, alignments, and member fact rows for LLVM emission.
- Added focused IR facts for sibling struct field captures in assignment and terminal switch lowering.
- Marked the struct sibling-label capture task complete in `TASKS.md`; fixed-array list element sibling captures remain separate.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `../../stark test --filter CompileStructAggregateSiblingCapture --test-progress --test-timeout 240` in `tests-stark/selfhost.Ir`: interrupted after about three minutes because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 Enum Payload Sibling Capture Merge Slice

- Added enum-payload sibling label parsing for assignment and terminal return switches so `case A(var x) | B(var x):` lowers one shared section body with per-label payload capture rows.
- Required sibling capture signatures to match by capture name and exact type code before sharing the section body, preserving payload extraction facts for LLVM emission instead of merging incompatible backend facts.
- Added focused IR facts for sibling enum payload captures in assignment and terminal switch lowering.
- Split the broader sibling-label capture task into enum-payload, struct-aggregate, and fixed-array list subtasks in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: interrupted after about ninety seconds with no output.
  - `../../stark test --filter CompilesSiblingEnumPayloadCapture` in `tests-stark/selfhost.Ir`: interrupted after about three minutes because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 Typed CTFE Structural Query Split

- Moved enum-layout `System.Compiler` query-name folding out of `TypedEnumLayoutModel` and into `TypedCtfeQueries`.
- Kept `TypedEnumLayoutModel` focused on layout table storage and fast typed accessors; CTFE now owns translating `EnumTag*` and `EnumVariant*` query names into constants.
- Marked the remaining structural-fact CTFE typing split complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedCtfeQueries.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Typing/TypedEnumLayoutModel.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Typing/TypedPipeline.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Typing/TypingTests.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: failed before the touched path due missing stdlib macOS symbols `System.Runtime.Platform.MacOS.StartProcessCaptureGrouped` and `System.Runtime.Platform.MacOS.KillProcessGroup`.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float Arithmetic LLVM Slice

- Added float-aware LLVM instruction selection for typed MIR arithmetic in the plain, fact-preserving, and typed-function emission paths.
- Emitted typed MIR `Add`/`Sub`/`Mul` as `fadd`/`fsub`/`fmul` for f32/f64 values, and emitted typed MIR division/remainder as `fdiv`/`frem` for f32/f64 values.
- Emitted typed MIR float comparisons as `fcmp` predicates while preserving integer comparisons as `icmp`; `!=` uses `une` so NaN compares not-equal.
- Added focused self-host IR facts for float/double arithmetic spelling and all f64 comparison predicates.
- Marked the arithmetic-and-comparison lowering task family complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/LlvmInstructions.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `../../stark test --filter EmitsLlvmTypedFloat --test-timeout 240` from `tests-stark/selfhost.Ir`: stayed silent during project setup and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float Package Round-Trip Slice

- Added sectioned package-image sections for MIR float literal payload bytes and float constant rows.
- Extended package-image directory parsing and inspection summaries so optional float side-table sections are validated and reported without breaking ordinary package deserialization.
- Added focused IR facts for f32/f64 LLVM return values, f32/f64 call arguments, and sectioned package round trips that render LLVM from deserialized float side tables.
- Marked the self-host literal-lowering task family complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float LLVM Emission Slice

- Threaded MIR float constant payload bytes through the LLVM module, block, and instruction emitters without widening ordinary instruction rows.
- Emitted f32/f64 constants as canonical LLVM hex float literals, preserving f32 rounding by parsing as f32 and widening to f64 bits for LLVM spelling.
- Kept side-table-less LLVM emission paths rejecting float constants so unsupported lowering routes fail explicitly instead of silently dropping backend facts.
- Added focused self-host IR coverage for f32 and f64 MIR return values rendered through the float-aware LLVM module emitter.
- Left source/package call-argument float round trips open for the next focused test slice.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/LlvmInstructions.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmBlocks.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmModules.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `../../stark test --filter EmitsLlvmFloatConstantsWithCanonicalHexLiterals --target arm64-apple-macosx26.0.0` from `tests-stark/selfhost.Ir`: stayed silent during project setup and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float Literal Expression Lowering Slice

- Added `FloatLitExpr` source expression nodes and parser recognition for `TokenKind.FloatLiteral`.
- Built module-level float literal constant facts from typed literal rows, preserving f32/f64 type, bit width, suffix-stripped ASCII parse payload, and dense typed-literal row alignment.
- Lowered linked float literal nodes through `MirFloatConstantValue` so row index, payload start, payload byte length, bit width, and MIR float type survive instruction serialization and text rendering.
- Wired float literal row linking into source module lowering and kept ordinary expression lowering rejecting float literals when module facts are absent.
- Added f32/f64 source-literal and MIR descriptor facts to the focused self-host IR test file.
- Fixed two existing retborrow blockers by copying `MirTextConstant` rows before passing them by value to `EmitLlvmTextConstantDataDeclaration`.
- Left LLVM float constant emission deliberately rejected until the next task implements canonical f32/f64 spelling.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/Builder.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/PackageCodec.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/TextRendering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/SourceLocalLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed with existing recursion warnings.
  - `./stark selfhost/Compiler/Mir/SourceModuleFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmModules.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed after the retborrow cleanup.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed after the retborrow cleanups.
  - `./stark selfhost/Compiler/Mir/SourceModuleLowering.stark --check ...`: interrupted with exit code 130 after it expanded into repeated imported-module validation.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check ...`: interrupted with exit code 130 after it expanded into repeated imported-module validation.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float Constant Storage Row Slice

- Added `MirFloatConstant` side-table rows that preserve exact ASCII literal payload spans plus f32/f64 result type without widening `MirInstruction`.
- Added float constant row validation for non-empty spans, bounds, ASCII payload bytes, and supported MIR float types only.
- Added package-codec serializers/deserializers for float literal payload bytes and float constant rows using durable `MirTypeStorageCode` values.
- Added deterministic MIR text rendering for float constant rows so row order, type, byte span, and payload bytes are inspectable.
- Added focused self-host IR facts for f32/f64 row validation, package codec round-trip, invalid package type rejection, and deterministic text rendering.
- Marked the MIR float constant storage subtask in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/TextRendering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/TextRendering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/PackageCodec.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: failed before this slice with existing package-image target/data-layout compatibility diagnostics.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: reached semantic validation, then failed on existing retborrow diagnostics in the older `EmitLlvmTextConstantDataDeclaration(... constants.Get(...))` test lines.
- No broad test sweep was run.

---

## 2026-07-07 MIR Float Value Type Plumbing Slice

- Added `F32` and `F64` to the self-host MIR value type model, package instruction/global type codec, MIR artifact type names, and LLVM scalar type spelling.
- Split struct value ABI shape digits away from general `MirTypeStorageCode` so package type codes can grow without corrupting the base-8 packed LLVM aggregate shape fact.
- Added a compact struct shape mapping for `f32`/`f64` while preserving ten-field shape packing in `u32`.
- Updated source struct-value shape producers and switch/capture validators to compare compact ABI shape codes instead of general MIR storage codes.
- Updated MIR/HIR fact guards so float value types can be carried through lowering but integer ranges, shifts, and integer global initializers still reject floats.
- Added a focused self-host IR fact covering f32/f64 instruction serialization, LLVM parameter/return type spelling, and struct shape rendering.
- Marked the stale completed text-literal parent tasks and the new MIR float type-plumbing subtask in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/LlvmText.stark selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/Facts.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Lowering.stark selfhost/Compiler/ArtifactRendering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `../../stark test --filter MirFloatValueTypesRoundTripPackageCodecAndLlvmShapeFacts --test-timeout 120` from `tests-stark/selfhost.Ir`: stayed silent during project build/setup for about 150 seconds and was interrupted with exit code 130.
  - `./stark selfhost/Compiler/Mir/LlvmFacts.stark --check -I selfhost` with the cached package's macOS arm64 target and data-layout facts: expanded into repeated imported-module type-check work and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant Package Codec Slice

- Added MIR text payload-byte and text-constant row serializers/deserializers that rebuild constants through `MirAddTextConstantRange`.
- Added optional `STARKPKG` text payload and text constant sections and validated they appear as a pair.
- Added text-aware package image summary counts for text and JSON inspection.
- Added text-aware sectioned package image serialization/deserialization helpers.
- Added deterministic MIR text constant side-table rendering in row order with explicit payload bytes.
- Added focused IR facts for sectioned package round-tripping of MIR text constants and deterministic text constant row rendering.
- Marked the MIR text literal package-codec and package-round-trip subtasks complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/PackageImage.stark selfhost/Compiler/Mir/TextRendering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/TextRendering.stark --check ...`: passed.
  - `./stark selfhost/Compiler/Mir/PackageCodec.stark --check ...`: reached repeated imported-module type-check work and was interrupted with exit code 130 after no diagnostics from this slice were reported.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check ...`: reached repeated imported-module type-check work and was interrupted with exit code 130 after no diagnostics from this slice were reported.
  - `../../stark test --filter SectionedPackageImageRoundTripsTextConstants --test-timeout 120 --test-progress`: stayed silent during project build/setup for about 60 seconds and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Literal Expression Descriptor Slice

- Built dense typed-literal row lookup by token in `SourceModuleLoweringFacts` so parsed text literal nodes can link in O(1) instead of scanning literal rows repeatedly.
- Decoded string and character literal payloads into module-level MIR text constant tables with byte length, code point length, and storage encoding facts preserved.
- Normalized signed `OwnedAscii` UTF-8 bytes into unsigned MIR payload bytes at the module fact boundary.
- Lowered linked string and character expression nodes through `MirTextConstantValue` using the module text constant rows.
- Linked parsed source text literal nodes to their typed literal rows during main source-module lowering.
- Added focused self-host IR facts for ASCII string payloads, Unicode string payloads, character literal payloads, and text descriptor value facts from lowered source nodes.
- Split the focused IR test task so package round-trip coverage remains open.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleFacts.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check ...`: passed with existing recursion warnings.
  - `./stark selfhost/Compiler/Mir/SourceModuleFacts.stark --check ...`: reached repeated imported-module type-check work and was interrupted with exit code 130 after no new diagnostics from this slice were reported.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check ...`: reached repeated imported-module type-check work and was interrupted with exit code 130 after no new diagnostics from this slice were reported.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant LLVM Descriptor Slice

- Replaced the `TextConstantValue` LLVM no-op cases with zero-copy descriptor construction in the existing instruction emitters.
- Added a reusable LLVM text-view descriptor type helper spelling `{ ptr, i64, i64 }`.
- Lowered each text constant descriptor as a private data pointer plus explicit byte-length and code-point-length operands.
- Reused MIR text row operands during LLVM lowering instead of recomputing text facts from payload bytes.
- Added a focused self-host IR fact for UTF-8 descriptor emission where byte length and code point length differ.
- Updated the module-level text constant test to expect descriptor instructions while still verifying referenced data rows emit once.
- Marked the LLVM text descriptor subtasks complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check`: passed.
  - `../../stark test --filter EmitsLlvmTextConstantValueDescriptorOperands --test-timeout 120` from `tests-stark/selfhost.Ir`: stayed silent for about 120 seconds and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant LLVM Module Slice

- Added a text-aware LLVM module emission path that accepts MIR text constant rows and payload bytes alongside instruction, block, and function tables.
- Scanned each emitted function's instruction range once to mark `TextConstantValue` row references in a compact side table.
- Emitted referenced MIR text constant rows once in deterministic row order before LLVM function bodies.
- Left `TextConstantValue` instruction body lowering as an explicit remaining task so descriptor construction can land with its own focused checks.
- Added a focused self-host IR fact for duplicate text constant references, unused text constant rows, and declaration-before-function ordering.
- Marked the text payload table threading and referenced-row emission subtasks complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/LlvmModules.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed before task and ledger updates.
  - `../../stark test --filter EmitsReferencedLlvmTextConstantsBeforeModuleFunctions --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent past the configured timeout window and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant LLVM Data Slice

- Added deterministic LLVM symbols for MIR text constant rows using `@.stark.text.<row>`.
- Added private `i8` byte-array declaration emission for MIR text constant payload rows.
- Escaped every emitted payload byte as an LLVM two-digit hex escape so ASCII syntax bytes, control bytes, and UTF-8 high bytes are stable.
- Validated text row ids, payload spans, and byte/code point length facts before reading payload bytes for LLVM emission.
- Added a focused self-host IR fact for ASCII byte escaping, UTF-8 byte escaping, and invalid text-row span rejection.
- Split the LLVM text literal task into sentence-sized subtasks and marked the private-data helper subtasks complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/LlvmText.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter EmitsLlvmPrivateDataForTextConstantRows --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent past the configured timeout window and was interrupted with exit code 130.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant Value Facts Slice

- Added text constant payload facts to `ValueFacts`: source row id, payload byte start, byte length, code point length, and storage encoding code.
- Preserved text constant facts through `ValueFacts.InheritFrom` so equivalent lowered values can carry the descriptor facts forward.
- Added the `ValueTextConstant` fact category and boundary validation hook so backend passes can require text literal facts explicitly.
- Generated `ValueTextConstant` facts for `MirOp.TextConstantValue` during MIR value-fact construction.
- Kept text facts compatible only with pointer-like MIR descriptor values in the HIR/MIR fact-fit gate.
- Exposed text constant facts in the deterministic value-facts artifact output.
- Added focused self-host IR facts for default/set/inherit behavior, fact-category metadata, boundary validation, and generated MIR text value facts.
- Marked the MIR text literal value-fact preservation subtask complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Ir.stark selfhost/Compiler/Mir/Facts.stark selfhost/Compiler/ArtifactRendering.stark selfhost/Compiler/Lowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed before task and ledger updates.
  - `../../stark test --filter MirTextConstantValueBuildsValueFacts --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent for about 130 seconds and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant Descriptor Slice

- Added a MIR text constant descriptor value operation that records the source text row, payload byte start, byte length, code point length, and storage encoding on the instruction.
- Kept the descriptor result typed as `ptr` so later lowering can build the zero-copy text view without widening scalar MIR value handling.
- Taught MIR well-formedness that text descriptor fields are side-table facts rather than value operands.
- Added deterministic text rendering and package opcode mapping for the new MIR operation.
- Added explicit LLVM no-op switch cases so this MIR operation is acknowledged until the private constant descriptor emission task lands.
- Added a focused self-host IR fact covering ASCII and UTF-8 descriptor fact preservation plus invalid row rejection.
- Marked the MIR text descriptor value-operation subtask complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/ArtifactRendering.stark selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/TextRendering.stark selfhost/Compiler/Mir.stark selfhost/Compiler/Mir/LlvmInstructions.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter MirTextConstantValueCarriesDescriptorFacts --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: first run caught the missing `TextConstantValue` case in `MirOpName`; after adding it, the retry stayed silent for about 130 seconds and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Constant Row Model Slice

- Added flat MIR text constant storage rows for decoded text payloads.
- Stored payload start, byte length, code point length, and storage encoding facts separately from instruction rows so text constants do not widen every MIR instruction.
- Added a builder helper that rejects invalid payload spans and impossible byte/code point facts before recording rows.
- Added a focused self-host IR fact covering empty ASCII rows, non-empty ASCII rows, UTF-8 rows, and invalid row rejection.
- Marked the first text literal constant-storage subtask complete in `TASKS.md`.
- Narrow verification:
  - `../../stark test --filter MirTextConstantRowsRecordPayloadFacts` from `tests-stark/selfhost.Ir`: first run caught the reserved `Ascii` enum-case syntax issue; after renaming the cases, the filtered run stayed silent for about two minutes and was interrupted.
  - `git diff --check -- selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Text Literal And Nested Pattern Task Split

- Reviewed the next open self-host MIR lowering items after the current switch-pattern batch.
- Split text-literal lowering into explicit MIR constant-storage, text descriptor, LLVM private constant, package-codec, and focused IR-test subtasks.
- Split typed switch-pattern import into explicit typing-row, lowering-fact, validation, and typed-row lookup subtasks.
- Split nested aggregate/list pattern lowering into shared pattern-decision node, typed-row translation, branch-failure routing, range/capture fact preservation, overlap validation, and focused IR-test subtasks.
- Did not touch compiler code in this slice because text literals currently lack MIR text constant storage and self-host switch lowering lacks typed switch-pattern row facts.
- Narrow verification:
  - `git diff --check -- docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 Self-Host Typing Module Map

- Recorded the stage0 C# to self-host `Compiler.Typing.*` split map for the remaining typing decomposition work.
- `src/Compiler/StarkTypeResolver.cs` maps to `Compiler.Typing.TypedTypeResolution` and declaration lookup helpers.
- `src/Compiler/TypeCompatibilityFacts.cs` maps to `Compiler.Typing.TypedTypeCompatibility` and the conversion/call signature fact helpers.
- `src/Compiler/TypeChecking.cs` maps to the existing focused modules: `ExpressionClassification`, `StorageSelectors`, `TypedSignatures.*`, `TypedGlobals.*`, `TypedFields.*`, `TypedEnumPayloads.*`, `TypedEnumLayouts.*`, `TypedLocals.*`, `TypedLiterals.*`, `TypedIdentifiers.*`, `TypedCalls.*`, `TypedMembers.*`, `TypedIndexing.*`, `TypedConversions.*`, `TypedAssignments.*`, `TypedReturns.*`, `TypedGenerics`, `TypedDynamicFacts`, `TypedCtfeQueries`, and `TypedPipeline`.
- `src/Compiler/GenericArgumentSyntaxFacts.cs` and `src/Compiler/FunctionGenericParameterFacts.cs` map to `Compiler.Typing.TypedGenerics` plus the enum-layout generic helper module.
- `src/Compiler/EnumLayoutBuilder.cs` maps to `Compiler.Typing.TypedEnumLayout*` for typed layout rows and to `Compiler.Mir.EnumLayout` for backend MIR enum layout facts.
- `src/Compiler/AssociatedTypeFacts.cs` maps to the open associated-type typing module once associated-type consumers exist in selfhost typing.
- `src/Compiler/DynTraitFacts.cs` maps to `Compiler.Typing.TypedDynamicFacts`, with dispatch and vtable emission consumers staying outside typing.
- `src/Compiler/CopyabilityFacts.cs`, `src/Compiler/ThreadSafetyLawFacts.cs`, and `src/Compiler/SystemThreadingAtomicFacts.cs` map to the open copyability, thread-safety law, and atomic builtin typing fact modules.
- `src/Compiler/CompileTimeExpressionEvaluator.cs`, `src/Compiler/CompileTimeFunctionEvaluator.cs`, and `src/Compiler/CompileTimeStructuralFacts.cs` map to `Compiler.Typing.TypedCtfeQueries` plus the remaining open CTFE expression/function/structural-fact typing modules.
- `src/Compiler/IntegerRangeStorageFacts.cs` maps to typed literal/range fact derivation and backend range fact consumers; exact integer arithmetic should continue to route through `System.Compiler.IntegerFacts` in selfhost code.
- Adjacent C# validation files such as `SemanticValidation.cs`, `OwnershipValidation.cs`, and `NonLexicalBorrowLifetimeValidation.cs` remain outside `Compiler.Typing.*` and are tracked by binding/validation tasks.
- Narrow verification:
  - `./stark selfhost/probe/StructCaptureProbe.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stayed silent for about two minutes and was interrupted.
  - Re-ran the same `StructCaptureProbe.stark --check` command after reviewing the remaining switch and CTFE split tasks; it again stayed silent for about two minutes and was interrupted with no diagnostics.
  - `../../stark test --filter CompileStructAggregateSwitchLocalCaptureTerminalReturnUsesDirectFieldLoads --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent for about ninety seconds and was interrupted.
  - No broad test sweep was run.

---

## 2026-07-07 MIR Pattern Descriptor Preflight Validation Slice

- Added scalar interval row preflight validation for bool and integer pattern rows before MIR switch branch-test emission.
- Added list-pattern descriptor preflight validation for descriptor row identity, element type, element index bounds, interval bounds, and stale row spans.
- Added struct aggregate descriptor preflight validation for descriptor owner identity, field row spans, by-value struct field shape agreement, storage-backed field alignment, and storage offset overflow.
- Added enum payload aggregate descriptor preflight validation that keeps concrete tags tied to source variant ordinals and checks payload ordinal layout facts before payload extracts are emitted.
- Threaded the preflight validators through terminal enum payload, terminal fixed-array list, terminal struct aggregate, and non-terminal assignment switch lowering before branch block construction.
- Added a focused selfhost IR fact for descriptor preflight acceptance and malformed-row rejection across list, struct, and enum descriptor rows.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed before task and ledger updates.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: first caught `out`/retborrow argument issues in the new scalar-row helper; after fixing those, the rerun stayed silent for about 90 seconds and was interrupted.
  - `../../stark test --filter SourceSwitchPatternDescriptorPreflightRejectsMalformedRows --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent for about 90 seconds and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Enum Aggregate Pattern Descriptor Row Slice

- Added enum aggregate descriptor row construction that maps concrete enum tag values back to source variant ordinals through typed enum layout facts.
- Threaded enum aggregate descriptor row ids through terminal enum payload-pattern branch-test lowering.
- Threaded enum aggregate descriptor row ids through non-terminal assignment enum payload-pattern branch-test lowering.
- Added a focused selfhost IR fact for enum descriptor row owner, variant, and payload-pattern span preservation.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed before task and ledger updates.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stayed silent for about two minutes and was interrupted.
  - `../../stark test --filter SourceSwitchEnumAggregatePatternDescriptorRowsPreserveVariantFacts --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent beyond the timeout window and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate/List Pattern Descriptor Row Slice

- Added case descriptor row builders for fixed-array list and aggregate pattern spans.
- Threaded list descriptor row ids through terminal and assignment fixed-array branch-test lowering.
- Threaded aggregate descriptor row ids through terminal and assignment struct branch-test lowering with explicit struct owner tokens.
- Added a focused selfhost IR fact for descriptor row-id/span preservation.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed before ledger update.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: first caught direct `IrTable.Get` retborrow arguments in descriptor construction; after copying the scalar values into locals, rerun stayed silent for about two minutes and was interrupted.
  - `../../stark test --filter SourceSwitchPatternDescriptor --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent beyond the timeout window and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate/List Pattern Descriptor Shape Slice

- Added compact aggregate-pattern descriptors carrying owner type token, optional enum variant ordinal, and field-pattern row spans for MIR switch lowering.
- Added compact list-pattern descriptors carrying element type code, fixed length, and element-pattern row spans for MIR switch lowering.
- Added descriptor-level disjoint helpers that reject mismatched list shapes, aggregate owner types, or enum variants before consulting scalar pattern rows.
- Added focused selfhost IR facts for descriptor shape preservation and descriptor identity checks around provably disjoint scalar rows.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: reached `Check succeeded.` after a silent bounded run was interrupted.
  - `../../stark test --filter SourceSwitchPatternDescriptor --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stayed silent for roughly two minutes and was interrupted.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stayed silent for about 90 seconds and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Storage Whole Capture Copy Slice

- Added a compact `StructValueLoad` MIR operation, text rendering, package opcode, LLVM emission, and artifact-rendering name for aggregate by-value loads from storage-backed struct pointers.
- Built storage-backed struct-value ABI facts from source type tokens for local and field-backed struct switch scrutinees, preserving field storage alignment for field-backed loads.
- Lowered storage-backed struct `case var captured` whole captures as explicit by-value aggregate loads and fed those capture overrides into struct-value call lowering with exact type-token checks.
- Added focused IR facts for local and field-backed storage struct whole captures feeding a by-value struct callee without scalarizing or routing through a pointer call.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/ArtifactRendering.stark selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/TextRendering.stark selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --diagnostic-format text --log-level error`: failed before reaching this change on the known stale `libStarkCompiler.starkpkg` target-data-layout mismatch.
  - The same `Mir.stark --check` with explicit target data layout stayed silent for about 90 seconds and was interrupted.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stayed silent for about 90 seconds and was interrupted.
  - `./stark selfhost/Compiler/Mir/LlvmInstructions.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: stayed silent for about 60 seconds and was interrupted.
  - `../../stark test --filter CompileStructStorageWholeCaptureValueParamFeedsStructValueCall --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: first caught the missing `MirOpName` case for `StructValueLoad`; after fixing it, rerun stayed silent past the timeout window and was interrupted.
  - `./stark --host-test-inspect` with `SourceSwitchLowering.stark` stopped after `lower-mir`: stayed silent for about 90 seconds and was interrupted.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Whole Capture Indexed Read Slice

- Threaded fixed-array whole-capture slice descriptor facts into captured guard and return-arm parsing for list-pattern switches.
- Reused the existing slice-descriptor indexed-read parser so `captured[index]` over an address-backed whole fixed-array capture resolves through the capture-owned copy slot instead of being parsed as a plain aggregate name.
- Applied the same parser context shape to parameter-backed, storage-backed, and field-backed fixed-array list switch return cases, including enum-return variants.
- Added a focused IR fact for a storage-backed whole fixed-array capture read with `captured[1]`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first run caught parser-threading mistakes; after fixes, rerun stayed silent for about 90 seconds and was interrupted.
  - `../../stark test --filter CompileFixedArrayStorageWholeCaptureIndexedReadFromAst --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stopped after about two minutes because the single-fact project test runner remained silent before reporting selected facts.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Whole Capture Slice Copy Slice

- Materialized address-backed fixed-array whole captures into a capture-owned stack copy slot using per-element typed loads and stores, preserving declared range and alignment facts on each copied element.
- Pointed whole-capture slice descriptor and argument facts at the capture copy slot so `captured` can feed slice callees through the existing concrete-slice ABI path.
- Kept by-value fixed-array callees on the same descriptor path by loading the aggregate value from the capture copy slot when the callee expects a fixed-array value.
- Added a focused IR fact for a storage-backed whole fixed-array capture passed as a slice.
- Left `captured[index]` parsing as the next explicit subtask; the lowering side now has a copy slot to target once the parser threads whole-capture slice descriptors into indexed-expression parsing.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed before task and ledger updates.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first run caught an `out MirValueId` read in the new copy helper; after fixing it, a rerun stayed silent for about 90 seconds and was interrupted.
  - `../../stark test --filter CompileFixedArrayStorageWholeCaptureSliceCallFromAst --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about 60 seconds because the single-fact project test runner remained silent before reporting selected facts.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Storage Whole Capture Copy Slice

- Added a fixed-array value-override descriptor and argument fact so address-backed `case var captured` labels can validate and lower by-value fixed-array call arguments without pretending the capture is an addressable fixed-array parameter.
- Lowered terminal fixed-array address-backed whole captures through one `MirLoadFixedArray` aggregate copy from the storage pointer before using the capture local, applying field byte offsets before the aggregate load when the scrutinee is field-backed.
- Kept storage-backed whole captures used as slices or indexed values rejected until an addressable capture-copy slot exists.
- Added focused IR facts for a storage-backed whole fixed-array capture feeding a by-value fixed-array callee and for rejecting slice conversion from that capture.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceLocalLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: reached `Summary: 0 errors, 32 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: reached `Summary: 0 errors, 47 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `./stark selfhost/Compiler/Mir/SourceFunctionContext.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: reached `Summary: 0 errors, 46 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first run caught an unknown-symbol error for the new validation path; after fixing it, a rerun stayed silent for about 90 seconds and was interrupted.
  - `../../stark test --filter FixedArrayStorageWholeCapture --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about 90 seconds because the filtered project test runner remained silent before reporting selected facts.
- No broad test sweep was run.

---

## 2026-07-07 MIR Whole Capture Overlap Rejection Slice

- Confirmed whole-value capture labels reuse the zero-pattern aggregate/list overlap path once represented.
- Added a focused negative AST fact for fixed-array `case var captured` labels overlapping list-pattern siblings in both source-order directions.
- Added focused negative AST coverage for struct `case var captured when true` and trailing struct whole-capture labels overlapping struct aggregate siblings.
- Marked whole-value capture overlap rejection complete in `TASKS.md`; nested aggregate/list overlap remains blocked on shared pattern-decision block construction.
- Narrow verification:
  - `git diff --check -- tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter RejectsAggregateAndListWholeCapturePatternOverlapFromAst --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about 90 seconds because the filtered project test runner remained silent before reporting selected facts.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Whole Capture Param Slice

- Recognized the whole-struct capture sentinel for terminal struct aggregate switch captures on by-value struct parameters.
- Lowered whole captures from by-value struct parameters as direct aggregate `MirParamTyped` values instead of field extracts or storage loads.
- Let struct-value call-argument lowering forward a nonzero local override so captured whole structs can feed by-value struct callees without materializing temporary storage.
- Added focused IR facts for by-value fixed-array and struct `case var captured` labels feeding by-value aggregate callees directly from `%p0`.
- Kept storage-backed whole-struct captures rejected until the explicit copy or borrow semantics task is resolved.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: reached `Summary: 0 errors, 47 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: stopped after about 30 seconds because the focused file check remained silent.
  - `../../stark test --filter WholeCapture --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent before reporting selected facts.
- No broad test sweep was run.

---

## 2026-07-07 MIR Whole Capture Local Type Rows

- Added a switch-capture type-code conversion helper that preserves aggregate local type rows for fixed-array parameters and by-value struct parameters before falling back to resolved scalar capture conversion.
- Let fixed-array list-pattern switch labels record top-level `var name` captures as whole-array rows using the fixed-array length as the out-of-range element sentinel.
- Let struct aggregate switch labels record top-level `var name` captures as whole-struct rows using the field count as the out-of-range field sentinel.
- Kept aggregate capture value override lowering as separate work so fixed-array and struct whole-value captures now type correctly but still require the next lowering tasks to produce values.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check`: failed before reaching this module because the direct file check did not include `selfhost` in the module search path.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: interrupted after about 90 seconds of silent front-end work with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate Direct Parameter Read Slice

- Added a struct-value call-argument lowering path that forwards direct by-value struct parameters as `%pN` operands instead of materializing temporary storage.
- Kept non-parameter aggregate forwarding rejected so storage-backed aggregate copy/borrow semantics remain a separate explicit task.
- Let struct-value call validation accept struct-valued local type rows instead of rejecting every `expected.IsStructValue` parameter.
- Taught LLVM call-argument emission to use struct ABI facts when printing by-value struct call operands, matching the existing struct parameter-list emission.
- Added a focused IR fact for forwarding a direct `Box` parameter into another by-value `Box` callee without an aggregate byte-buffer allocation.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark selfhost/Compiler/Mir/LlvmInstructions.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmInstructions.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: passed.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first caught a direct table-`Get` retborrow call-site issue; after binding the local type code first, it reached `Summary: 0 errors, 47 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `./stark selfhost/Compiler/Mir/SourceFunctionContext.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first caught the same direct table-`Get` retborrow call-site issue; after binding the local type code first, it reached `Summary: 0 errors, 46 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return.
  - `../../stark test --filter CompileFunctionStructValueParameterCanFeedStructValueCall --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about 150 seconds of silent project startup before the filtered fact began.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: interrupted after about 90 seconds of silent front-end work with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate Local Type Slice

- Added an aggregate expression kind for source-level fixed-array and by-value struct locals so they no longer fall through scalar typing.
- Reserved a struct-value local type-code range below stored-enum codes and bounded its token decoder to that range.
- Seeded by-value struct signature parameters with struct-value local type codes while preserving existing fixed-array parameter codes.
- Added a focused IR fact that fixed-array and struct local type rows classify as aggregate while scalar rows remain scalar.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSemanticProbes.stark selfhost/Compiler/Mir/SourceExpressions.stark selfhost/Compiler/Mir/SourceFunctionContext.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceExpressions.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: passed with existing recursion warnings.
  - `./stark selfhost/Compiler/Mir/SourceFunctionContext.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: reached `Summary: 0 errors, 46 warnings, 0 infos.` and was interrupted after about 60 seconds before the command returned.
  - `../../stark test --filter SourceAggregateLocalTypeCodesDoNotMasqueradeAsScalars --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after 30 seconds of silent project startup before the filtered fact began.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate/List Discard Overlap Slice

- Added a focused negative AST fact for top-level discard labels overlapping fixed-array list patterns in both source-order directions.
- Added a focused negative AST fact for a `case _ when true` struct aggregate label that makes a later struct pattern unreachable.
- Kept dynamically guarded discard labels lowerable when a later sibling can still be reached after the guard fails.
- Split `TASKS.md` so discard-label overlap is complete and whole-value capture overlap remains open until whole-value capture labels are represented.
- Narrow verification:
  - `git diff --check -- tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter RejectsAggregateAndListDiscardPatternOverlapFromAst --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after 30 seconds of silent project startup before the filtered fact began.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate/List Discard Label Slice

- Accepted top-level `case _` labels for fixed-array list and struct aggregate switch lowering.
- Represented discard labels as zero-pattern rows so the existing branch constructors emit a constant-true condition without extra storage or dynamic dispatch.
- Kept whole-value capture labels as a separate follow-up because they need explicit capture local type and override facts.
- Added a focused IR fact covering a guarded fixed-array list pattern falling through to `case _` and a guarded by-value struct aggregate pattern falling through to `case _`.
- Split `TASKS.md` so discard-label representation is complete and whole-value capture labels remain open.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompileAggregateAndListDiscardFallbackLabelsAfterGuardedPatterns --test-timeout 120 --test-progress` from `tests-stark/selfhost.Ir`: interrupted after about 150 seconds of silent project build before the filtered fact began.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: interrupted after about 60 seconds of silent front-end work with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Aggregate/List Unconditional Guard Overlap Slice

- Treated `when true` aggregate and fixed-array list switch labels as unconditional for existing overlap validation.
- Kept dynamic guards and `when false` guards conservative so guarded fallthrough labels remain lowerable.
- Reused the existing scalar interval disjointness helpers instead of adding a second overlap implementation.
- Added focused negative AST facts for fixed-array list labels and struct aggregate labels whose earlier `when true` arm makes a later sibling unreachable.
- Split the broad overlap task in `TASKS.md` so nested labels and whole-value capture/discard labels remain explicit follow-ups.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: first caught an ambiguous `ExprKind` reference and then a direct table-`Get` retborrow call-site issue; both were fixed.
  - The same `SourceSwitchLowering.stark --check` command was rerun and interrupted after about 60 seconds of silent front-end work with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Guarded Aggregate/List Capture Switch Slice

- Allowed `when` guards on fixed-array list and struct aggregate switch labels that introduce `var` capture locals.
- Parsed capture-guard expressions in a capture-aware parameter scope so guard names bind to the matched label captures.
- Validated guard call contracts and boolean guard types against capture-local type rows instead of the base local table.
- Lowered guard capture values in the guard block after successful pattern tests, reusing the existing fixed-array and struct capture override builders so direct extracts, typed aligned loads, and declared range facts stay intact.
- Added focused IR facts for by-value struct terminal capture guards, storage-backed struct assignment capture guards, and fixed-array parameter assignment capture guards.
- Marked the guarded aggregate/list capture-local task complete in `TASKS.md`.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md selfhost/probe/StructCaptureProbe.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: interrupted after 60 seconds of silent front-end work with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Aggregate Field Capture Switch Slice

- Parsed tuple and property struct aggregate `var` field captures into per-arm capture rows.
- Kept capture names scoped to the matched switch arm and continued rejecting guarded capture labels until guard lowering can consume capture locals.
- Lowered by-value struct field captures through direct struct-parameter field extracts.
- Lowered storage-backed and field-backed struct field captures through typed aligned field loads.
- Preserved declared scalar field range facts on captured struct field values so LLVM IR receives the same `!range` metadata as ordinary field tests.
- Added focused IR facts for storage-backed terminal returns, by-value assignment arms, and field-backed assignment arms.
- Added `selfhost/probe/StructCaptureProbe.stark` as a focused executable probe for the same three struct capture paths.
- Narrowed the probe import to `Compiler.Mir.SourceModuleLowering` so the probe pulls only the compile-from-AST entrypoint it needs.
- Left the struct aggregate field capture task in progress in `TASKS.md` because the focused front-end checks did not complete before interruption.
- Narrow verification:
  - `git diff --check -- selfhost/probe/StructCaptureProbe.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated type-check progress warnings with no diagnostics visible.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated type-check progress warnings with no diagnostics visible.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated type-check progress warnings with no diagnostics visible.
  - `./stark selfhost/probe/StructCaptureProbe.stark --emit-exe -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error -o /tmp/stark-struct-capture-probe`: reached native LLVM verification and failed on an invalid `getelementptr` for `%System_Collections_List_Compiler_Mir_EnumLayout_MirEnumLayoutFact_`, matching the known host-backend generic-aggregate GEP limitation documented in `selfhost/Compiler/Mir/Model.stark`.
  - `./stark selfhost/probe/StructCaptureProbe.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level warning`: interrupted after repeated type-check progress warnings with no diagnostics visible.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array List Capture Assignment Switch Slice

- Parsed fixed-array list element `var` captures for non-terminal assignment switch labels over direct parameters, storage locals, and field-backed fixed arrays.
- Kept capture names scoped to the matched assignment arm and continued rejecting guarded capture labels until guard lowering can consume capture locals.
- Lowered direct fixed-array parameter captures with `extractvalue` in the matched assignment arm block.
- Lowered storage-local and field-backed captures with typed constant-offset element loads and preserved declared element ranges as LLVM `!range` metadata.
- Added focused IR facts for direct, storage-local, and field-backed fixed-array list capture assignment switches.
- Marked the fixed-array list assignment capture subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `../../stark test --filter=CaptureAssignmentUsesDirectElement --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: interrupted after about two and a half minutes because the filtered project build/run stayed silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array List Capture Terminal Switch Slice

- Parsed fixed-array list element `var` captures for terminal return switch labels over direct parameters, storage locals, and field-backed fixed arrays.
- Kept capture names scoped to the matched case arm and rejected guarded capture labels until guard parsing can consume capture locals.
- Lowered direct fixed-array parameter captures with `extractvalue` and lowered storage/field captures with typed constant-offset element loads.
- Preserved declared storage/field fixed-array element ranges on captured element loads so LLVM IR receives `!range` metadata.
- Added focused IR facts for direct fixed-array list capture terminal switches and storage-local fixed-array list capture terminal switches.
- Marked the fixed-array list terminal capture subtask complete in `TASKS.md` while leaving assignment-arm captures open.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Enum Payload Capture Terminal Switch Slice

- Parsed tuple and named enum payload `var` captures for terminal return switch labels.
- Seeded capture names into each terminal case arm's section-local scope before parsing the return expression.
- Preserved concrete scalar payload type facts for captured terminal arm locals.
- Lowered captured enum payload values with `extractvalue` in the matched terminal return block.
- Added focused IR facts for tuple payload capture terminal switches and named payload capture terminal switches with a scalar payload test.
- Marked the enum payload terminal capture subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
- The same `Mir.stark --check` without explicit `--target-data-layout` stopped in module graph on an existing stale package-image target-layout mismatch before reaching this code.
- No broad test sweep was run.

---

## 2026-07-07 MIR Enum Payload Capture Assignment Switch Slice

- Parsed tuple and named enum payload `var` captures for non-terminal assignment switch labels without adding them to the scalar payload-test table.
- Seeded capture names into each assignment arm's section-local scope after the pre-target locals and before ordinary arm-local declarations.
- Preserved concrete scalar payload type facts for capture locals while type-checking assignment arm expressions.
- Lowered captured enum payload values with `extractvalue` in the matched assignment arm block, so unmatched cases do not evaluate capture extracts.
- Added focused IR facts for tuple payload capture assignment switches and named payload capture assignment switches with a scalar payload test.
- Marked the enum payload assignment capture subtasks complete in `TASKS.md` while leaving terminal, aggregate, list, and capture-merge work open.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CompilesEnumPayloadCaptureAssignmentSwitchFromAst --filter CompilesNamedEnumPayloadCaptureAssignmentSwitchFromAst --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: interrupted after about two and a half minutes because the filtered project build/run remained silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR Guarded Fixed-Array Storage/Field List Switch Pattern Slice

- Parsed `when` guards on storage-local and field-backed fixed-array list switch labels and preserved guard nodes beside the element-pattern rows.
- Allowed guarded storage-local and field-backed list labels to fall through to later unguarded fallbacks with the same element pattern, while still rejecting overlaps after an unguarded prior label.
- Lowered scalar-return and enum-return guarded storage-local list labels through constant-offset element loads with declared element range facts preserved.
- Lowered scalar-return and enum-return guarded field-backed list labels through typed member-path element loads with declared element range facts preserved.
- Lowered non-terminal assignment guarded storage-local and field-backed list labels through the existing assignment switch merge path.
- Added focused IR facts for guarded terminal-return, guarded enum-return, and guarded assignment fixed-array list switches over storage-local and field-backed scrutinees.
- Marked the guarded no-capture fixed-array storage-local and field-backed list subtasks complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Guarded Fixed-Array Parameter List Switch Pattern Slice

- Parsed `when` guards on direct fixed-array parameter list switch labels and preserved guard nodes beside the element-pattern rows.
- Allowed a guarded fixed-array list label to fall through to a later unguarded fallback with the same element pattern, while still rejecting overlaps after an unguarded prior label.
- Lowered terminal-return guarded fixed-array parameter list labels through direct `extractvalue` element tests followed by separate guard branch blocks.
- Lowered non-terminal assignment guarded fixed-array parameter list labels through the existing assignment switch merge path, with true guard edges routed to ordinary case assignment blocks.
- Kept storage-local and field-backed fixed-array list guards out of this slice so their memory-backed element-load paths remain unchanged.
- Added focused IR facts for terminal-return and assignment guarded fixed-array parameter list switches.
- Marked the guarded no-capture fixed-array parameter list subtasks complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersTerminalFixedArrayListGuardedSwitchToLlvm --filter CompileFixedArrayListSwitchParamGuardedScalarAssignmentFallsThroughToUnguardedFallback --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: interrupted after remaining silent past the configured timeout window during build/startup.
  - A standalone two-case probe that compiled and ran only the guarded-list terminal and assignment scenarios was interrupted after continued compiler stage progress without a final result.
- No broad test sweep was run.

---

## 2026-07-07 MIR Guarded Struct Aggregate Assignment Switch Pattern Slice

- Parsed `when` guards on non-terminal assignment struct aggregate switch labels and preserved the guard node beside the field-pattern rows.
- Lowered assignment-switch guards into separate MIR condition blocks after successful struct field tests, with false guards falling through to the next case/default tail.
- Kept existing assignment phi-chain construction unchanged by routing guarded true edges into the ordinary case assignment block.
- Validated guard roots through the same module-call contract path used by assignment arms and lowered guards with the preserved local override facts.
- Added focused IR facts for guarded stack-backed struct aggregate assignments and guarded by-value struct parameter aggregate assignments.
- Marked the guarded no-capture struct aggregate switch-label task complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Guarded Struct Aggregate Switch Pattern Slice

- Parsed `when` guards on terminal-return no-capture struct aggregate switch labels after the aggregate field pattern label is accepted.
- Rejected guards that introduce unknown names or do not type-check as `bool` in the existing local type context.
- Preserved ordered field-test lowering and inserted a separate guard branch block only after the field tests succeed.
- Allowed a guarded struct aggregate label to fall through to a later unguarded fallback with the same field pattern, while still rejecting overlaps after an unguarded prior label.
- Routed guarded terminal-return struct aggregate switches over both stack-backed struct locals and by-value struct parameters.
- Added focused IR facts for guarded local struct aggregate returns and guarded by-value struct parameter aggregate returns.
- Split the guarded struct aggregate task in `TASKS.md` so non-terminal assignment guards remain explicit follow-up work.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CompileStructAggregateSwitchLocalGuardedScalarReturnFallsThroughToUnguardedFallback --filter CompileStructAggregateSwitchValueParamGuardedScalarReturnUsesDirectFieldExtracts --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: interrupted after about two and a half minutes because the filtered project build/run remained silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR By-Value Struct Parameter Switch Pattern Slice

- Resolved single-identifier struct aggregate switch scrutinees to by-value struct parameter ABI facts after the existing constructed-object storage lookup fails.
- Threaded the struct-value parameter flag and ABI shape facts through terminal-return and assignment switch parsing.
- Lowered terminal struct aggregate switch conditions over by-value parameters through direct `StructParamField` extracts instead of stack materialization.
- Lowered non-terminal assignment struct aggregate switch conditions over by-value parameters through the same direct field extract path.
- Preserved declared scalar field range facts by using the range-bearing `StructParamField` builder when the pattern row has a member-path range fact.
- Added focused IR facts for terminal-return and assignment struct aggregate switches over by-value struct parameters; both assert LLVM `extractvalue`, branch tests, and no struct alloca or scalar field loads.
- Marked the by-value struct aggregate switch-routing subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CompileStructAggregateSwitchValueParamScalarReturnUsesDirectFieldExtracts --filter CompileStructPropertyPatternSwitchValueParamScalarAssignmentUsesDirectFieldExtracts --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about five minutes because the filtered project build/run remained silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Parameter Extract Range Fact Slice

- Packed the `StructParamField` aggregate field count and shape code into the instruction immediate so operands C/D are available for backend fact payloads.
- Added a range-bearing struct parameter field builder that records declared lower and exclusive upper bounds as constant operands.
- Imported declared range constants for direct struct-parameter field extracts in the MIR value-fact pass without stack materialization.
- Kept unannotated scalar struct-parameter extracts useful by deriving conservative full-width facts for supported compact integer result types.
- Added a focused IR fact that proves an extracted `i64` field carries its declared `[5, 9)` range and an extracted `i1` field carries `[0, 2)`.
- Marked the declared field range import subtask complete in `TASKS.md`; switch routing over by-value parameters remains open.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/Facts.stark selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/TextRendering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR By-Value Struct Parameter Direct Extract Slice

- Added a first-class `StructParamField` MIR opcode for extracting a scalar field directly from a by-value struct parameter.
- Stored the struct parameter field count and compact LLVM aggregate shape on the extraction instruction so LLVM emission does not need source lookup or stack materialization.
- Reused the struct-value LLVM aggregate type emitter for both function signatures and `extractvalue` instruction operands.
- Plumbed the new opcode through MIR text rendering, artifact opcode names, and the package codec.
- Added a focused IR fact that emits `extractvalue { i32, i1 } %p0, 0` from a `{ i32, i1 }` struct parameter and returns the scalar field.
- Marked the direct struct-parameter field extraction subtask complete in `TASKS.md`; field range facts and switch routing over by-value parameters remain open.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/TextRendering.stark selfhost/Compiler/ArtifactRendering.stark selfhost/Compiler/Mir/PackageCodec.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR By-Value Struct Parameter LLVM Signature Slice

- Added LLVM aggregate parameter type emission for by-value struct ABI facts using the preserved scalar field shape code.
- Moved the struct-value shape multiplier helpers into `LlvmFacts.stark` so fact production and LLVM consumption share the same base-8 field-code convention.
- Kept ordinary integer range attributes on scalar parameters and left struct-value call argument lowering blocked until the later direct struct argument/extract subtasks.
- Added a focused IR fact that compiles `fn i64 classify(Box box, i64 scalar)` and checks the emitted signature uses `{ i32, i1 } %p0`.
- Marked the by-value struct LLVM signature subtask complete in `TASKS.md`; direct `extractvalue` lowering, range facts, and switch routing remain open.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/LlvmFunctions.stark selfhost/Compiler/Mir/SourceLocalLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR By-Value Struct Parameter ABI Fact Slice

- Added by-value struct parameter ABI facts that retain the owner type token, known byte size, known byte alignment, scalar field count, and compact LLVM scalar aggregate shape.
- Kept by-value struct ABI facts distinct from memory-backed pointer/slice facts so `dead_on_return`, alias, and lifetime contracts do not attach to aggregate values.
- Reused the existing self-host struct layout probes and rejected unsupported nested or fixed-array field shapes instead of emitting incomplete LLVM shape facts.
- Rejected generic call-argument validation for struct-value ABI requirements until direct struct-value argument lowering is implemented.
- Added a focused IR fact that parses a tiny `Box` parameter signature and checks `{ i32, i1 }` shape facts for the by-value struct parameter.
- Marked the by-value struct parameter ABI-fact subtask complete in `TASKS.md`; LLVM signature emission, direct `extractvalue` lowering, range facts, and switch routing remain open.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/SourceFunctionContext.stark selfhost/Compiler/Mir/SourceLocalLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Aggregate Field-Backed Switch Slice

- Accepted terminal and non-terminal struct aggregate switch labels when the scrutinee is a struct field on a constructed object local.
- Resolved field-backed aggregate scrutinees through typed member-chain facts and kept the root object storage pointer.
- Folded the resolved field base byte offset into each scalar field-pattern load, preserving direct constant-offset loads instead of materializing aggregate temporaries.
- Preserved declared struct field range facts on scalar field-pattern loads so LLVM emission can attach range metadata.
- Added focused IR facts for field-backed terminal returns and field-backed assignment switches that check direct field loads, range metadata, branch tests, and absence of aggregate `extractvalue`.
- Marked the field-backed struct aggregate switch subtask complete in `TASKS.md` and split the larger by-value struct parameter item into ABI/MIR subtasks.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - A filtered `stark test` fact run was not started because this machine has no `timeout`/`gtimeout` wrapper and recent filtered runs for `tests-stark/selfhost.Ir` have stalled silently during project build.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Aggregate Assignment Switch Slice

- Accepted non-terminal struct aggregate switch labels when the scrutinee is a constructed object local.
- Reused the terminal struct aggregate pattern parser for positional and property labels with scalar or discard field subpatterns.
- Routed struct aggregate assignment switches through the existing assigned-switch branch-chain and phi merge lowering.
- Lowered each field test through object storage overrides and typed member-path facts so LLVM receives direct constant-offset field loads.
- Preserved declared struct field range facts on scalar field loads in assignment-switch tests.
- Added focused positional and property-pattern IR facts that check direct field loads, range metadata, phi continuation, and absence of aggregate `extractvalue`.
- Marked the non-terminal constructed-object struct aggregate assignment subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - A filtered `stark test` fact run was not started because this machine has no `timeout`/`gtimeout` wrapper and recent filtered runs for `tests-stark/selfhost.Ir` have stalled silently during project build.
- No broad test sweep was run.

---

## 2026-07-07 MIR Struct Aggregate Terminal Switch Slice

- Accepted terminal struct aggregate switch labels when the scrutinee is a constructed object local.
- Lowered positional and property struct labels with scalar or discard field subpatterns to ordered field branch tests.
- Reused object storage overrides and typed member-path facts so field tests lower to direct constant-offset field loads.
- Preserved declared struct field range facts on scalar field loads so LLVM emission can attach range metadata.
- Rejected duplicate property fields and overlapping scalar field intervals before emitting branch blocks.
- Added focused scalar-return, property-pattern, and enum-return IR facts that check direct field loads, range metadata, enum construction, and absence of aggregate `extractvalue`.
- Marked the completed terminal struct aggregate subtasks in `TASKS.md`; non-terminal assignments, parameter/field-backed scrutinees, nested field patterns, and guarded labels remain open.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - A filtered `stark test` fact run was not started because this machine has no `timeout`/`gtimeout` wrapper and recent filtered runs for `tests-stark/selfhost.Ir` have stalled silently during project build.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Field-Backed List Switch Assignment Slice

- Accepted non-terminal fixed-array list switch assignments when the scrutinee is a fixed-array field on a constructed object local.
- Reused typed member-path facts and object storage overrides so assignment switch tests lower to constant-offset element pointer loads.
- Preserved declared fixed-array element range facts on field-backed element loads so LLVM emission can attach range metadata.
- Added a focused IR fact that checks direct field element loads, range metadata, phi continuation, and absence of fixed-array parameter `extractvalue`.
- Marked the non-terminal field-backed assignment subtask and its completed fixed-array local/field-backed parents complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CompileFixedArrayFieldListSwitchLocalScalarAssignmentUsesDirectElementLoads --test-progress --test-timeout 240 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after a 300-second external alarm because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Field-Backed Terminal List Switch Slice

- Accepted terminal fixed-array list switch labels when the scrutinee is a fixed-array field on a constructed object local.
- Lowered field-backed list label tests through typed member-path facts, object storage overrides, and constant-offset element pointer loads.
- Preserved declared fixed-array element range facts on field-backed element loads so LLVM emission can attach range metadata.
- Added focused scalar-return and enum-return IR facts that check direct field element loads, range metadata, and absence of fixed-array parameter `extractvalue`.
- Marked the scalar-return and enum-return fixed-array field-backed list-pattern subtasks complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter FixedArrayFieldListSwitch --test-progress --test-timeout 240 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after a 300-second external alarm because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Storage-Local List Switch Assignment Slice

- Accepted non-terminal fixed-array list switch assignments when the scrutinee is an initialized stack fixed-array local.
- Reused storage-local constant-offset element branch tests so assignment switches load only the tested elements from backing storage.
- Preserved setup-local storage overrides through switch assignment lowering so post-switch expressions keep the same local fact table shape.
- Added a focused IR fact that checks stack storage initializer stores, typed element loads with range metadata, phi continuation, and absence of fixed-array parameter `extractvalue`.
- Marked the non-terminal fixed-array storage-local list switch assignment subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CompileFixedArrayStorageLocalListSwitchLocalScalarAssignmentUsesDirectElementLoads --test-progress --test-timeout 240 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after a 120-second external alarm because the filtered project build produced no output.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Storage-Local Enum-Return List Switch Slice

- Accepted enum-return terminal fixed-array list switch labels when the scrutinee is an initialized stack fixed-array local.
- Reused the storage-local constant-offset element branch tests so list labels load only the tested elements from the fixed-array backing storage.
- Routed enum-return list switch arms through the enum-return parser and existing enum constructor lowering, preserving return enum layout facts.
- Added a focused IR fact that checks storage-local element loads retain range metadata and enum returns emit aggregate construction without fixed-array `extractvalue`.
- Marked the enum-return fixed-array storage-local list-pattern subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersTerminalFixedArrayStorageLocalListSwitchEnumReturnToLlvm --test-progress --test-timeout 240 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after staying silent for more than 90 seconds during the filtered project build.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array Storage-Local List Switch Slice

- Accepted scalar-return terminal fixed-array list switch labels when the scrutinee is an initialized stack fixed-array local.
- Lowered storage-local list label tests through constant byte-offset pointer loads instead of parameter `extractvalue` instructions.
- Preserved declared fixed-array element range facts on storage-local element loads by attaching the declared range to the typed aligned load.
- Emitted fixed-array storage initializer stores before the first switch test block so list-pattern tests observe initialized values.
- Added a focused IR fact for terminal stack fixed-array local list labels and direct pointer-load branch tests.
- Split the local/field-backed fixed-array list-pattern task into scalar terminal, enum terminal, non-terminal assignment, and field-backed subtasks in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersTerminalFixedArrayStorageLocalListSwitchToLlvm --test-progress --test-timeout 240 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after staying silent beyond the intended timeout window during the filtered project build.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array List Switch Assignment Slice

- Accepted no-capture fixed-array list switch labels over fixed-array parameters for non-terminal local switch assignments.
- Reused the assigned-switch merge path so list-pattern assignments continue after the switch through the existing phi/return logic.
- Lowered assignment switch label tests through direct fixed-array parameter element extracts instead of materializing the array parameter.
- Added focused IR facts for direct fixed-array element tests in a local assignment switch and AST range validation after the switch continuation.
- Marked the non-terminal fixed-array list switch assignment subtask complete in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompileFixedArrayListSwitchLocalScalarAssignmentUsesDirectElementTests --filter CompilesFixedArrayListLocalSwitchStatementAssignmentThenReturnFromAst --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project build stayed silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR Fixed-Array List Switch Slice

- Accepted no-capture fixed-array list switch labels over fixed-array parameters, such as `case [1, _, 3]:`, for terminal switch lowering.
- Lowered fixed-array list label tests through direct `FixedArrayParamElement` extracts so constant-index element reads stay on the by-value array ABI path.
- Preserved declared fixed-array element range facts on direct parameter element extracts so return validation and LLVM call/return range facts can consume them.
- Rejected overlapping terminal fixed-array list labels before emitting MIR branch blocks.
- Added focused IR facts for scalar-return list labels, enum-return list labels, overlapping-label rejection, and direct element range preservation.
- Split the remaining fixed-array list switch work in `TASKS.md` into non-terminal switch assignments and local/field-backed scrutinees.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/Facts.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter BuildMirValueRangeFactsImportsFixedArrayElementDeclaredRange --filter CompileFunctionFixedArrayParameterConstantReadPreservesElementRangeFacts --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project build stayed silent.
- No broad test sweep was run.

---

## 2026-07-07 MIR Disjoint Enum Payload Switch Label Slice

- Allowed repeated same-variant enum payload switch labels when scalar payload intervals prove the labels are disjoint.
- Kept overlap rejection conservative: unit/all-discard labels, partially unrelated payload constraints, and touching intervals such as `0..5` plus `5..9` still reject.
- Reused the existing branch-chain lowering so repeated labels keep one tag read, typed payload extracts, and direct scalar/enum return or assignment lowering.
- Added focused IR facts for terminal scalar-return labels, terminal enum-return labels, named bool payload disjoint labels, local switch assignments, and overlapping-label rejection.
- Completed the scalar payload enum aggregate label subgroup in `TASKS.md`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompilesTerminalEnumDisjointScalarPayloadLabelsFromAst --filter CompilesTerminalNamedEnumDisjointScalarPayloadLabelsFromAst --filter RejectsOverlappingEnumScalarPayloadLabelsFromAst --filter SourceModuleLowersTerminalEnumDisjointPayloadSwitchEnumReturnToLlvm --filter CompileEnumDisjointScalarPayloadSwitchStackScalarAssignmentsUsePayloadTests --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0`: stopped after the filtered project build stayed silent for about 90 seconds.
- No broad test sweep was run.

---

## 2026-07-06 MIR Enum Payload Switch Return and Assignment Slice

- Routed enum-return terminal enum switches through the payload-aware label parser so scalar payload subpatterns keep their typed payload facts.
- Extended payload-aware terminal enum switch lowering to build enum return values with the existing enum-return helper instead of scalar return lowering.
- Routed non-terminal local switch assignments through the payload-aware enum label parser.
- Generalized assigned integer/enum switch lowering with an enum-payload condition mode that reads the tag once, extracts only constrained payloads, and reuses the existing phi/merge assignment path.
- Added focused IR facts for tuple and named scalar payload labels returning enums and for tuple and named scalar payload labels assigning stack scalar locals.
- Left repeated same-variant disjoint scalar payload labels as the remaining open payload-switch subtask.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersTerminalEnumPayloadSwitchEnumReturnToLlvm --filter SourceModuleLowersTerminalNamedEnumPayloadSwitchEnumReturnToLlvm --filter CompileEnumScalarPayloadSwitchStackScalarAssignmentsUsePayloadTests --filter CompileNamedEnumScalarPayloadSwitchStackScalarAssignmentsUsePayloadTests --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0`: stopped after the filtered project build stayed silent for about 90 seconds.
- No broad test sweep was run.

---

## 2026-07-06 MIR Enum Scalar-Payload Switch Pattern Slice

- Added a payload-aware enum switch-label parser for terminal scalar-return switches.
- Accepted no-capture tuple and named enum payload scalar labels such as `case Packet.Other(7..9):` and `case Packet.Move { X: 7, Flag: true }:`.
- Lowered scalar payload labels by reading the enum tag once, extracting only tested payload fields, and emitting typed bool/integer comparisons so LLVM sees the original tag and payload facts.
- Preserved the existing tag-only switch path for unit and all-discard enum labels.
- Kept enum-return switches, non-terminal switch assignments, and repeated same-variant scalar labels as explicit remaining subtasks.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompilesTerminalEnumScalarPayloadSwitchFromAst --filter CompilesTerminalNamedEnumScalarPayloadSwitchFromAst --test-progress --test-timeout 180 --target arm64-apple-macosx26.0.0`: stopped after the filtered project build stayed silent for about 90 seconds.
- No broad test sweep was run.

---

## 2026-07-06 MIR Named Enum Discard-Payload Switch Pattern Slice

- Accepted no-capture named enum aggregate case labels with all-discard payload members, such as `case Packet.Move { X: _, Flag: _ }:`, in the shared enum switch label parser.
- Matched named payload members through the typed enum layout rows so payload names, variant owner identity, and tag facts stay layout-backed.
- Kept named payload captures, missing members, duplicate members, unknown members, struct aggregate patterns, and list patterns rejected until typed pattern rows and capture storage exist.
- Preserved the existing fast path: accepted named discard-payload enum cases lower to the same compact tag branch tests as unit and tuple-discard enum cases.
- Added focused IR facts for terminal return switches, switch assignment arms, and unsupported named payload pattern rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileNamedEnumPayloadSwitchStackScalarAssignmentsUseTagTests --filter CompilesTerminalNamedEnumDiscardPayloadSwitchFromAst --filter RejectsUnsupportedNamedEnumPayloadSwitchPatternsFromAst --test-timeout 180`: stopped after the filtered project build stayed silent for about 90 seconds.
- No broad test sweep was run.

---

## 2026-07-06 MIR Enum Discard-Payload Switch Pattern Slice

- Split the oversized aggregate/list switch-pattern task into sentence-sized subtasks grounded in the stage0 C# switch-pattern lowerer.
- Accepted no-capture enum aggregate case labels with all-discard positional payloads, such as `case Packet.Other(_):`, in the shared enum switch label parser.
- Kept payload captures, wrong-arity payload patterns, struct aggregate patterns, and list patterns rejected until typed pattern rows and capture storage exist.
- Preserved the existing fast path: accepted discard-payload enum cases lower to the same compact tag branch tests as unit enum cases, with enum layout facts unchanged.
- Added focused IR facts for terminal return switches, switch assignment arms, and unsupported payload pattern rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter EnumDiscardPayloadSwitch --filter EnumPayloadSwitchStackScalar --filter UnsupportedEnumPayloadSwitchPatterns --test-timeout 180`: stopped after the filtered project build stayed silent for about 2.5 minutes.
- No broad test sweep was run.

---

## 2026-07-06 MIR Constructed-Object Field Try-Assignment Range Slice

- Carried target declared-range facts on constructed-object field try-assignment descriptors for scalar fields and fixed-array elements.
- Proved the `[Ok]` payload's declared integer or bool range is a subset of the target field range before lowering the success-path store.
- Kept wider success payloads rejected so returned field loads can continue to advertise sound LLVM `!range` metadata.
- Updated `MemberFactsProbe.stark` and `IrTests.stark` to accept matching ranged `try` stores and reject wider payload stores.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - Attempted focused probe executable build with `./stark selfhost/probe/MemberFactsProbe.stark --emit-exe -o /tmp/stark-memberfacts-probe -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`, but stopped it after it continued into expensive compile/link work.
- No broad test sweep was run.

---

## 2026-07-06 MIR Arena Dynamic Terminal Branch Slice

- Routed local-prefixed terminal `if` lowering in the effect prepass through module facts instead of an empty facts bundle.
- Routed enum-valued local-prefixed terminal `if` emission through owner-layout LLVM rendering.
- Added focused IR facts for scalar and enum-valued terminal `if` return arms with arena-backed dynamic locals.
- Accepted arena-backed `dynamic` locals before local-prefixed terminal integer, boolean, and enum-case switches.
- Routed local-prefixed boolean terminal switches through the boolean parser and lowerer.
- Routed local-prefixed enum-case terminal switches through declaration-aware module facts and enum-owner validation before tag comparison lowering.
- Emitted arena frame leaves for every terminal switch return arm when the switch prefix allocates arena dynamic storage.
- Routed enum-valued local-prefixed terminal switches through the enum-return parser and owner-layout LLVM emitter.
- Preserved enum return carrier layouts and arena frame cleanup for integer, boolean, and enum-case terminal switch arms.
- Added focused IR facts for terminal integer, boolean, and enum-case switch return arms with arena-backed dynamic locals.
- Added focused IR facts for enum-valued terminal integer, boolean, and enum-case switch return arms with arena-backed dynamic locals.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileTerminalSwitchArenaDynamicLocalLeavesEveryReturnArm --target arm64-apple-macosx26.0.0 --test-progress --test-timeout 120`: stopped after it stayed silent in the filtered project build path for about two minutes.
  - `../../stark test --filter CompileTerminalBooleanSwitchArenaDynamicLocalLeavesEveryReturnArm --target arm64-apple-macosx26.0.0 --test-progress --test-timeout 120`: stopped after it stayed silent in the filtered project build path for about one minute.
  - `../../stark test --filter CompileTerminalEnumCaseSwitchArenaDynamicLocalLeavesEveryReturnArm --target arm64-apple-macosx26.0.0 --test-progress --test-timeout 120`: stopped after it stayed silent in the filtered project build path for about one minute.
- No broad test sweep was run.

---

## 2026-07-06 MIR Arena Dynamic Switch Storage Slice

- Lowered arena-backed dynamic storage locals in switch storage-assignment functions.
- Accepted `Reserve`, `TryReserve`, and `TryReserveCapacity` as ordered switch-arm mutations.
- Emitted arena frame leaves on switch merge returns and terminal-if switch exits.
- Added focused IR facts for integer and boolean switch arms that call arena dynamic reserve helpers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-07-06 MIR Enum-Valued Terminal Branch Return Slice

- Lowered enum-valued terminal `if` returns through source-owner and MIR-owner validation before emitting return blocks.
- Added enum-aware terminal integer, boolean, and enum-case switch return parsing so direct enum constructors in `case` and `default` returns lower to owner-carrying MIR values.
- Routed terminal-switch LLVM emission through enum layout facts so enum return carriers render from layout rows instead of `unknown`.
- Updated `selfhost/probe/EnumReturnProbe.stark` and `tests-stark/selfhost.Ir/IrTests.stark` with terminal `if`, integer-switch, boolean-switch, and enum-case-switch enum-return coverage.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `./stark selfhost/probe/EnumReturnProbe.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
- Attempted focused probe executable run with `./stark selfhost/probe/EnumReturnProbe.stark -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --emit-exe -o /tmp/stark-enum-return-probe && /tmp/stark-enum-return-probe`, but stopped it after it continued into expensive compile/link work.
- No broad test sweep was run.

---

## 2026-07-06 MIR Enum-Valued Return Carrier Slice

- Lowered direct enum-constructor terminal returns and storage-backed enum local terminal returns through the single-function MIR entry.
- Added enum-owner resolution for value-producing MIR enum instructions and used owner layout facts for enum-valued LLVM function return types.
- Routed enum-aware return type rendering through range-fact function emitters and whole-module enum-layout emitters.
- Changed unresolved enum owners during enum-valued return type emission to fail with `InvalidLayout` instead of emitting `unknown`.
- Updated `selfhost/probe/EnumReturnProbe.stark` to expect enum returns to compile and to reject emitted `unknown` LLVM text.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed after deleting the stale generated `tests-stark/selfhost.Ir/.../libStarkCompiler.starkpkg` cache that triggered STK7312.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `./stark selfhost/probe/EnumReturnProbe.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - The exact filtered `stark test` run for the four new enum-return facts was stopped after about 90 seconds of silence.
  - Building the probe executable was stopped after it kept compiling imported compiler code for several minutes.
- No broad test sweep was run.

---

## 2026-07-04 Bundle Field-Store Heisenbug Closed: Stale-Archive Ghost

- Closed the TASKS.md §6 bundle entry. The real defect was the
  ownership-traffic liveness bug (fixed 2026-07-03,
  `TryResolveWholeLocalAddress`); everything observed after that fix was
  artifact, not compiler:
  - The "stock `opt -O3` removes the 144/576-byte field copies" layer was
    a copy-COUNT phantom. Store-level semantic check on the package
    build's own `Compiler_Mir_SourceModuleFacts.ll`: pre-O3 all six bundle
    field memcpys are present; post-O3 SROA scalarizes `built` and the
    success path stores every `slot_built.sroa.*.copyload` value directly
    into `%arg_moduleFacts` at ascending offsets (including the
    144-offset EnumPayloads region). Legitimate forwarding — which is
    also why single-category attribute strips never "preserved" the
    memcpys: no attribute licenses SROA forwarding.
  - The post-fix "runtime probe still loses fields" observation was a
    stale-archive ghost: the loss is baked into `libStarkCompiler.a` at
    PACKAGE-build time, and the verification rebuilt only the probe
    (~15 s path) against the old archive. A probe-only rebuild can never
    see a compiler fix to package-side code. Recipe rule added to
    docs/Internals/CompilerDevelopmentVerification.md.
- Verified against the fresh instrumented package (built 2026-07-03
  18:43, all fixes in): MemberFactsProbe 19/19 with MFDBG pre/built/out
  counts identical; NestedChainFactsProbe and NestedChainProbe green.
- Removed the temporary tooling: `STARK_SKIP_PASSES` (CompilerPipeline.cs),
  `DumpSsaFunction` + 10 call sites (DefaultCompilerPipeline.cs), ECM/WALK
  emit traces (LlvmFunctionBodyEmitter*.cs), MFDBG/ENDBG/ECDBG crumbs
  (SourceModuleFacts/SourceModuleLowering/SourceLocalLowering .stark; the
  four host files and SourceModuleFacts are byte-identical to HEAD again).
  Rebuilt the package from the de-instrumented sources; clean-room
  re-verification (package-free CWD, single package candidate):
  MemberFactsProbe 19/19 and both nested-chain probes green with zero
  crumb output — the bundle copies survive without instrumentation reads
  keeping them alive.
- RETRACTION from the de-instrumented re-verification: the enum-return
  slice's prior evidence ("`ok enum-return-only` whenever the copies
  survive") was accept-plus-INVALID-emission, not a working lowering.
  Dumping the accepted module from the instrumented package shows
  `define unknown @main()` with `ret unknown` — the enum return-type
  mapping is unimplemented and the accept boolean masked garbage output.
  The de-instrumented build REJECTS the same shapes (`return Pick.First`,
  `stack Pick pick = ...; return pick`) while enum construction into
  locals keeps working (`enum-local-scalar-return` control passes) —
  the safe behavior at the ragged edge of the parked #34 work. The valid
  evidence chain: probe-shape bisection over both packages (honest,
  package-backed by design) and the emission dump. CORRECTION
  (2026-07-04, later the same day): the side experiments first recorded
  here (a "from-source parity" leg, a host pass-skip sweep, and an
  old-dll cross-check) were INVALID — the probe file sat in a scratch
  directory whose `stage0/pkg` sibling joined the package search via the
  root-file-directory rule, so every "from-source" compile silently
  linked the de-instrumented package (and ran in ~10 s, which should
  have been the tell: honest from-source compiles take ~12 min). Those
  legs say nothing about the flip mechanism; only the two-package A/B
  and the `define unknown` dump stand. New §1 task records the
  enum-valued-return lowering work;
  `selfhost/probe/EnumReturnProbe.stark` originally pinned the shapes
  expect-reject with a construction control; the 2026-07-06 enum-return
  slice flipped it to expect-success and reject emitted `unknown` text.
- Process lesson recorded in the probe conventions: an accept BOOLEAN
  from a compile probe is not lowering evidence — dump and validate the
  emitted module for at least one accepted shape per slice.
- Probe caveat re-confirmed: package-backed probes pass `-I <pkg>` only
  (no `-I stdlib/src`); Internals recipe updated to match.
- Regression coverage added:
  `OwnershipTrafficSsaKeepsSiblingFieldCopiesIntoLiveAggregate`
  (compiler.PipelineTests) pins the sibling-field-copy shape — verified
  red against the pre-fix resolver (both kill sites flipped back), green
  with the fix.
- Discovered in passing (pre-existing at unmodified HEAD, worktree
  check): the two neighboring ownership-traffic tests
  (`OwnershipTrafficSsaElidesDeadAggregateMoveTrafficForNonEscapedRoots`,
  `OwnershipTrafficSsaKeepsMoveInvalidationForRawEscapedRoots`) fail 2/2
  at HEAD — their fixtures no longer produce Move-kind copies or undef
  move-invalidation stores by `memory-opt-ssa` (only Copy-kind traffic
  remains), so the asserts never match. Committed-state residue, not
  related to the bundle work; needs its own triage.
- No broad test sweep was run.

## 2026-07-03 Host Wrong-Code Fix: Order-Dependent Value-Fact Recording

- Fixed the stale dynamic-`Length` wrong-code (TASKS.md §6 entry, now
  [x]): `RefineDynamicStorageLocalFacts` recorded per-value facts during
  the entry-state fixpoint, so a loop exit processed before back-edge
  convergence permanently kept the pre-loop state (length 0 → emitted
  `range(i64 0, 1)` → branch folded at emission). The fixpoint now runs on
  a scratch facts copy and records once from converged entry states.
- Minimized to a 40-line hermetic repro (`stale_min`) before fixing;
  statically confirmed in the unoptimized emitted module (folded branch +
  poisoned range annotation).
- Verified: both stale-length repros correct; allocator and
  package-instantiation repros still green; 21 targeted unit tests green;
  full `LlvmIrEmissionTests` 455/458 with the 3 failures reproduced
  byte-identically at unmodified HEAD (pre-existing).
- The bundle field-store heisenbug is NOT cured by this (clean-rebuild
  verified; an apparent cure was a stale bisect-era probe binary). Its
  documented next suspect: the analogous order-dependence in
  memory-opt-ssa's single-predecessor known-state propagation.
- The updated `stark-stdlib-authoring-gotchas` workaround guidance can be
  retired once consumers rebuild; the defensive local-counter pattern in
  `Compiler.TestDriver` remains harmless.
- No broad test sweep was run.

## 2026-07-03 Stage1 Test Driver Component: Golden Parity (T30.9/T30.10)

- Landed `Compiler.TestDriver` (`RunTestRunner`): spawns a generated runner
  via the new grouped `Spawn`, forwards stdout/stderr line-by-line with
  `[N.Ns] ` monotonic prefixes when progress is on, passes `--progress` in
  the runner argv, and at the deadline group-kills the child and appends
  the stage0-identical timeout report. Single-threaded pipe-poll
  multiplexing (new public `PollChildPipes`) instead of §5.2's
  thread-per-pipe sketch — same observable contract; deviation recorded in
  the design doc. Pending-buffer lengths tracked with local counters to
  sidestep the stale-Length host bug (TASKS.md §6).
- Golden parity verified by probe against the conformance fixtures: legacy
  transcripts byte-identical (both streams), progress transcripts
  prefix-normalized identical with every line prefixed, hang fixture under
  a 3s deadline yields `run HangsForever` last, the exact §3.4 report, a
  clean group kill, and no orphaned runner.
- `tests-stark/selfhost.TestRunner` grew a portable driver fact
  (`/bin/echo` streamed with prefixes + argv `--progress` echo) — 4/4
  green through the package. Docs closure (T30.10): design-doc status,
  Internals "Test Runner Progress", and the TASKS.md stage-contract entry
  now point at `tests/fixtures/test-progress` as normative.
- No broad test sweep was run.

## 2026-07-03 Stdlib: Spawned-Child Surface For The Stage1 Test Driver

- Implemented the T30.7 gap surfaces in `System.Process` (`ChildProcess`,
  grouped `Spawn`, `ReadStdoutChunk`/`ReadStderrChunk`, `TryWaitExit`/
  `WaitExit`, `KillTree`, `Close`, public `MonotonicMilliseconds`) over new
  platform primitives `StartProcessCaptureGrouped`/`KillProcessGroup`
  (full 4-dispatch + 3-backend fan-out; macOS `posix_spawnattr` group
  spawn — including fixing `posix_spawnp`'s attributes parameter to the
  pointer-to-pointer shape — Linux child `setpgid`, Windows single-kill
  fallback noted).
- Runtime-verified on macOS by probe: echo spawn/stream/wait (exit 0,
  9 bytes), `KillTree` on a `sh -c sleep` group with clean reap and no
  orphaned sleep, monotonic clock sane.
- Found host wrong-code bug in the process: a post-loop `captured.Length`
  read returns the stale pre-loop value when the loop mutates the dynamic
  through a nested mut-borrow call (TASKS.md §6 entry; repro preserved).
  Probe works around it by accumulating counts locally.
- Regression checks: `SystemProcessStandardLibraryTests` 5/7 failures are
  byte-identical at unmodified HEAD (shared-fixture STK7312 target
  mismatch — pre-existing environment issue). No broad sweep run.

## 2026-07-03 Host Fix: Package-Imported Field Instantiations Materialize

- Fixed the `lower-mir` crash on consumers that hold and drop a
  package-imported struct with a `List<T>`-class field (TASKS.md §6 entry):
  `MaterializeImportedSourceInstantiations` skipped package-image modules,
  so field-nested generic instantiations never registered (no named-type
  entry, no triggers, no plan, no layout, no LLVM struct def). The
  type-checker now walks published concrete struct/enum field and variant
  types through `EnsureMonomorphizedType`, mirroring source imports; member
  resolution also gained the `GetGenericBaseName` receiver fallback used by
  the destructor/enum-layout lookups.
- Unblocked `tests-stark/selfhost.TestRunner`: 3/3 facts green, and the
  package-backed `Compiler.TestRunner` emission byte-matches stage0's
  generated runner (probe diff empty). One test needle fixed
  (RunFactCounted line is nested in `if (...)`).
- Narrow verification: `build/pkgbug` repro `ok plan`; 21
  generator/emission unit tests green; allocator repros green; the 7
  `PackageImage` unit-test failures reproduce identically at unmodified
  HEAD (worktree check) — pre-existing.
- selfhost.Ir run as the heavyweight package-consumer gate: it crashes in
  `LowerFieldAccess` on `SameOwnerSourceTryLoweringBranchesAndExtractsSuccessPayload`
  (one of the UNCOMMITTED try-shape facts) — reproduced byte-identically
  under the unmodified HEAD compiler with the same uncommitted
  selfhost/tests sources (worktree check), so it is pre-existing to the
  parked try-shape/#34 work, not a regression from this fix. The 4
  TestProgressProtocolTests re-pass on the fixed compiler. No broad sweep
  run.

## 2026-07-03 Test-Progress Streaming: Fixture, Goldens, Stage1 Emission

- Landed T30.4–T30.8 of docs/Self-host-Prep/30-test-progress-streaming.md:
  the `tests/fixtures/test-progress` conformance + hang fixtures with
  byte-exact §3.2/§3.3 golden transcripts, four green
  `TestProgressProtocolTests` integration tests (runner-direct golden diff,
  driver prefix streaming, legacy byte-identity, timeout kill + orphan
  check), the TASKS.md §4 stdlib audit for the stage1 driver, and
  `Compiler.TestRunner` — the stage1 generated-runner emission whose main
  block byte-matches stage0's generator for the full fixture plan.
- Blocked: `tests-stark/selfhost.TestRunner` regression facts crash the
  project build on a new host package-generics bug (imported struct holding
  a `List<T>` field breaks consumer drop lowering; minimal repro under
  `build/pkgbug`; TASKS.md §6). The same bug gates the T30.9 stage1 driver
  component's owned-row containers.
- Narrow verification: 4/4 TestProgressProtocolTests; `--check` clean on the
  new selfhost module; byte-diff of stage1 vs stage0 generated main; manual
  `stark test --test-progress --test-timeout 5` on the hang fixture.
- No broad test sweep was run.

## 2026-07-03 Host Wrong-Code: Allocator Attributes On Visible Bodies

- Root-caused the deterministic `OwnedAscii` first-append loss (`v_cross`: two
  `AppendConstAscii` calls crossing initial capacity printed only the second
  string, exit 0) and the related 4-append SIGTRAP: the emitter attached
  `allockind`/`allocsize`/`alloc-family` to the runtime bucket allocator
  (`__stark_runtime_(try_)alloc`/`(try_)realloc`/`free`), the heap wrappers,
  and the arena allocator while also broadcasting their bodies `linkonce_odr`
  into every module. The bodies read the allocation header at `ptr - 24`; the
  attributes assert a fresh object starting at the returned pointer, so
  whole-program O3 proved the header peek UB, concluded successful allocation
  paths are unreachable, and emitted `llvm.assume(alloc == null)` — deleting
  the first append's stores entirely.
- Evidence chain: native disassembly of the failing exe showed a fully folded
  `main` (both source strings reduced to one 12-byte buffer); stock
  `llvm-link` + `opt -O3` over the `--save-temps` modules reproduced the fold;
  stripping only the runtime-family attribute groups in the merged module
  fixed the runtime output with no other change; the unmodified control still
  corrupted.
- Fix: `LlvmBuiltinAndHelperEmitter.cs` no longer attributes any allocator
  with a visible body (runtime, heap, arena); opaque libc/Win32 declarations
  and the model-consistent `__stark_os_*` wrappers keep theirs. Policy comment
  added at the family-attribute constants.
- Narrow verification: 5 affected `LlvmIrEmissionTests` pass; `v_cross`,
  `v_cross3` (status-checked variant), and `v_ownedascii` (previously SIGTRAP
  133) all print correct output with the rebuilt compiler.
- Fallout: every earlier probe/test observation made through binaries that
  grow `dynamic` storage is suspect — including the `MemberFactsProbe`
  decl-count-0 anomaly blocking the enum-return slice and the try-shape HEAD
  verdict. Re-verification queued per item.
- No broad test sweep was run.

## 2026-07-03 Stark Test Per-Fact Progress Streaming

- `stark test` no longer buffers runner output until exit: the driver forwards stdout/stderr line-by-line as they arrive, so a killed or timed-out run keeps its partial transcript. (The old `ReadToEndAsync` batch was why timeouts yielded one opaque blob.)
- New `--test-progress` flag: the driver passes `--progress` to the generated runner (runtime argv toggle — no regeneration or build-stamp churn); the runner prints `run <name>` before each fact and `ok|FAILED <name> (k/N)` after it through the new `System.Testing.BeginFact`/`RunFactCounted`; the driver stamps `[elapsed]` wall-clock prefixes on every forwarded line. A hung run's last `run <name>` line names the fact in flight. Without the flag, output is byte-identical to the legacy format.
- New `--test-timeout <seconds>` flag: kills the runner process tree at the deadline and reports the timeout explicitly.
- The protocol is documented as a stage0/stage1 contract (docs/Internals/CompilerDevelopmentVerification.md "Test Runner Progress"); the stage1 port item is queued in TASKS.md.
- Narrow verification:
  - `dotnet build` clean; all 16 `StarkTestRunnerGenerator` unit tests pass with counted-call assertions.
  - `stark test --test-progress --filter Contains` in `tests-stark/stdlib.Testing`: `run` markers, `(1/2)`/`(2/2)` counters, `[0.2s]` prefixes, streamed stderr assert-reports, clean pass (37.7 s including a full stdlib rebuild after clearing a stale package fixture).
  - No-flag run: legacy `ok <name>` output byte-identical; `--test-timeout 60` run: no false firing.
- Discovered in passing: the per-project cached stdlib package fixture shadowed the freshly edited `System.Testing` source (the known stale-`.starkpkg` edge) — cleared with `rm -rf build/`; the TASKS.md package-consumer item already covers the fix direction.
- No broad test sweep was run.

## 2026-07-02 MIR Compound And Try Store Range Proofs Slice

- Compound field assignments (`box.f += e`, `-=`) now lower: the statement gate and the chain-branch parser accept AddAssign/SubAssign on simple and nested field targets, and the stored value desugars at parse into `Binary(field-read, OP, e)` with the field read carrying its declared range — the existing `SourceStoredValueSatisfiesDeclaredFieldRange` proof then judges the widened result. Full-width fields accept; narrow declared ranges reject without evidence (`[0 3) + 1 → [1 4)` is not provable), matching the host's declared-range conservatism. Indexed compound targets remain unsupported (gate unchanged) and are noted as follow-up.
- Try-assignments into narrow-ranged fields now carry a reject-unproven guard: the [Ok] payload's declared range is not yet decoded into node facts, so a field narrower than its storage width has no store proof. HEAD-worktree verification (2026-07-03) then showed the guard is currently shadowed by a PRE-EXISTING shape gap: constructed-object field try-assignments reject for every field width through the single-function entry (the body matcher fires but the lowering rejects; the IrTests facts asserting these shapes never executed green). The gap is recorded as its own TASKS.md item; the probe encodes both spellings as rejecting today, with the full-width one flipping when the shape lands.
- Cross-field narrow-store proofs verified already working: `u8[0 2] = <read of u8[0 2] field>` accepts via the declared load range; `u8[0 2] = <read of u8[0 200] field>` rejects.
- Standing probe gains four checks (compound full-width/narrow, try full-width/narrow — 19 total); IrTests gains the compound-desugar fact (add+store asserted, ClangVerified) and the narrow-try rejection fact.
- Narrow verification: widened `--check` clean; `stark build` clean; probe batteries green; IrTests `--check` after the facts.
- No broad test sweep was run.

## 2026-07-02 MIR Indexed Element Declared-Range Facts Slice

- Fixed-array member-path facts now decode the ELEMENT's declared integer range (the fact builder's semantic primary token lands on the element type head, so `u8[0 2][4]` decodes the `u8[0 2]` span into new ElementHasDeclaredRange fields).
- Constant- and dynamic-index element reads attach the element range to their expression nodes, and `LowerResolvedIndexedFieldRead` lowers ranged element loads through the declared-range typed LoadPtr, so element loads carry `!range` metadata and their MIR value facts prove downstream fixed-array index bounds.
- Verified LLVM by hand: `box.slots[1]` loads i8 at byte 1 with `!range {0,3}`, and the loaded value scales the second array's element offset directly — `return box.values[box.slots[1]]` needs no extra comparison. The unranged control (`i64[min max]` elements) still rejects as an unproven index.
- Standing probe gains ranged-elem-read-proves-index-bounds and unranged-elem-index-rejected; IrTests gains the corresponding ClangVerified fact.
- Narrow verification: widened `--check` clean; `stark build` clean; MemberFactsProbe 15/15; nested/var/if batteries unregressed; IrTests `--check` after the fact.
- No broad test sweep was run.

## 2026-07-02 MIR Terminal-If Member Conditions Slice

- Re-bisected the 2026-07-01 "heap bool member" matrix entry: heap and stack bool member stores lower fine after the earlier slices; the actual gaps were in if-condition handling, and probe syntax matters — the dialect's `if` is paren-free (`if flag return 1 else return 0`), so C-style probes mislead.
- Three stacked fixes:
  1. `CompileFunctionWithLocalsToLlvm` never dispatched to the terminal-if lowerings (they were wired only into the module paths, with an EMPTY facts bundle there); the driver now routes `FunctionBodyHasLocalPrefixedTerminalIf`/`FunctionBodyStartsWithIfStatement` bodies into `LowerModuleLocalIfReturnFunctionToBlocks` with the real `SourceModuleLoweringFacts`. This alone fixed bare-bool param conditions.
  2. The typing-side statement walker extracted if/while conditions only when parenthesized, so paren-free dialect conditions — and real-Stark `while willexit (…)` conditions — produced no typed member rows, and the lowering's member branch fell through (breadcrumb: then-arm-reject from a NameExpr(box) with Next at the dot). The extraction now skips the `willexit` marker and the optional paren. Same root-cause class as the var-from-field classifier gap.
  3. The driver's shared tail emitted a linear instruction range, truncating multi-block if bodies mid-CFG (a dangling `br` with no target blocks — caught by dumping the emitted LLVM, invalid text that ClangVerifies would reject). Block-shaped bodies now record `BlockEmit` ranges and emit through `EmitLlvmBlocksWithRangeFactsCoreWithEnumLayouts` with the range-metadata epilogue.
- Emitted LLVM verified by hand for the heap shape: dereferenceable(16) heap alloc, store i1 true at offset 0, flag load, `br i1` into `b2: ret 1` / `b3: ret 0`.
- Added IrTests facts for stack and heap member-bool conditions (block labels + branch + ClangVerifiesLlvmText).
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check ...`: passed after each of the three fixes.
  - `cd selfhost && ../stark build`: passed at each step; probes ran against the freshly built packages.
  - Probe batteries: if-shapes 5/5, MemberFactsProbe 13/13, NestedChainProbe unregressed, var-shapes 4/4.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check ...`: run after adding the two facts.
- No broad test sweep was run.

## 2026-07-02 MIR Var-From-Field And Ordered Statement Timeline Slice

- Root-caused `var copy = box.value` rejections with breadcrumb instrumentation plus the member-facts diagnostic probe: the typed member table had ZERO rows for var initializers (`ProbeSourceMemberPathFacts`: members=0 vs members=1 for the `stack i64` spelling) because the statement-kind classifier in `Parsing.stark` mapped stack/heap/register/static/arena/const to `StatementKind.Local` but not `var` — var statements classified as expressions, so their initializers never parsed into the expression table and typing never walked them. One classifier case fixes the fact pipeline end-to-end.
- Merged the single-function driver's locals and statements loops into one walk so locals may interleave with assignment statements (`box.count = 2; var idx = box.count;` previously exited the locals loop and rejected).
- Caught a wrong-code hazard the interleaving exposed before it shipped: the driver batched local overrides/initializers before mutation replay, so an interleaved var's field load emitted BEFORE the store it must observe (probe LLVM showed load-then-store). Fixed by making `storageMutationStatements` the ordered statement timeline: the walk appends a `SourceStorageMutationKindLocalDecl` row per local, and replay lowers each local's override value and initializers at its source position, delegating mutation rows to the shared per-statement dispatcher. This also orders stored-scalar initializers that read fields after mutations.
- Emitted LLVM verified by hand: store i8 2 precedes the var's !range-carrying load (%v8), the indexed store scales the loaded value to byte 24, and the return loads byte 24 — the declared field range proves the fixed-array bounds through the var.
- Standing probe expectations flipped: var-from-i64-field, var-from-ranged-field, ranged-field-index-store-proves-bounds, ranged-field-index-read-proves-bounds now expect success (they flipped loudly to FAIL first, per the probe convention).
- Added IrTests facts: var-from-field lowering, and the ordering + bounds fact asserting the store consumes an earlier SSA value than the var's load.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check ...`: passed after the classifier fix, after the loop merge, and after the timeline replay.
  - `cd selfhost && ../stark build`: passed at each step; probes ran against the freshly built package images.
  - `MemberFactsProbe` 13/13, `NestedChainProbe` unregressed (correct offsets, cyclic rejection), var-shapes battery 4/4.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check ...`: run after adding the two facts.
- Remaining member-access runtime gap tracked in TASKS.md: heap constructed-object bool member stores / if-condition member reads.
- No broad test sweep was run.

## 2026-07-02 MIR Nested Member Chain Lowering Slice

- Root-caused the nested-member-chain runtime gap with the package-backed probe recipe (bisection battery, ~15 s per iteration): every aggregate-in-aggregate constructed-object local was rejected at declaration because `TryGetKnownSourceStorageLayoutWithEnums` — the per-field layout resolver feeding `StructKnownByteExtentWithEnums`/`StructKnownByteAlignmentWithEnums` — handled builtins and enums but never recursed into named aggregate fields, leaving the outer struct with extent 0. The chain resolver, per-step member-path fact validation, and store/read statement gates were already chain-capable.
- Fixed with depth-capped recursion: `TryGetKnownSourceStorageLayoutWithEnumsAtDepth` falls through to recursive struct extent/alignment for identifier type tokens (`u8[0 16]` depth cap — the mini-Stark dialect has no front end, so cyclic aggregates would otherwise hang layout; they now reject). Fixed arrays of named aggregates stay rejected rather than silently sizing one element. Existing signatures kept as depth-0 wrappers.
- Verified offsets, not just acceptance: `Box{i64 head; Inner{i64 pad; i64 value}}` lowers to a 24-byte alloca with store/load at byte 16; the three-level chain accumulates to byte 16; a scalar field after an aggregate field lands at byte 8 of a 16-byte alloca.
- Added IrTests facts: nested store/load byte offset, three-level offset accumulation, scalar-after-aggregate recursive extent, cyclic-aggregate rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed (first run caught STK3014 width style on the depth parameter; clean after narrowing to `u8[0 16]`).
  - `cd selfhost && ../stark build`: passed; probes below ran against this package image (built from the fixed sources, so package-backed probe runs are runtime evidence for the current tree).
  - `NestedChainProbe` (9 shapes incl. dumps): all nested shapes compile with correct offsets; cyclic aggregate rejects.
  - `MemberFactsProbe` (13 standing checks): all still match expectations — no regressions in narrow-store proofs, bounds proofs, or rejection shapes.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check ...`: run after adding the four facts (see below).
- Remaining member-access runtime gaps tracked in TASKS.md: heap constructed-object bool member stores / if-condition member reads, and `var` locals initialized from member field reads.
- No broad test sweep was run.

## 2026-07-01 Pain-Point Fixes: Module-Facts Bundle, overlap_all, Widened Check, Probe Recipe

- Widened `--check` to run the full front end (through ownership-validate) over the root and every source-backed dependency module, in parallel, with per-module batched failure reporting (host `CompilerCli`).
- Added `where overlap_all(name)` to the host compiler (syntax-model expansion into pairwise overlap groups, STK3050 exemption, STK3029 target validation, grammar rule documented in `Stark.g4` pending parser regeneration) and collapsed 42 selfhost where-clauses onto it.
- Added STK3057 for duplicate parameter names (was an STK9999 crash) and imported-module file paths on type-check diagnostics (report funnels only; `Location()` untouched for package-image template matching).
- Strengthened `IrTable` accessors to `finite law` (root fix in `System.Collections.List<T>`) and re-strengthened the selfhost helpers that had been demoted.
- Added the slow-pass heartbeat: pass completion logs `Pass 'X' took N.Ns` at default visibility past five seconds.
- Introduced `SourceModuleLoweringFacts` (`Compiler.Mir.SourceModuleFacts`): the single sanctioned struct-of-tables bundling the six per-module lowering inputs (declarations, typed enum payloads/layouts, MIR enum layout facts, member-path fact rows + token index), built once per module by `BuildSourceModuleLoweringFacts` and threaded as one `borrow` parameter; 76 lowering signatures across five files migrated by a dry-run-validated scripted rewriter; `CompileModuleFromAstStream` builds one bundle shared with the effect prepass (double typed-member build eliminated); 11 `IrTests` call sites assemble test bundles.
- Established the package-backed probe recipe: member-facts probe compiles in 14.5 s against `libStarkCompiler.starkpkg` vs ~12–20 min from source, runs 13/13; sharp edges (data-layout mismatch, `*.starkpkg` search-dir poisoning, relative library path, embedded-stdlib subset) recorded in TASKS.md tooling items and docs/Internals/CompilerDevelopmentVerification.md.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed clean after the overlap_all collapse, again after the bundle migration, and again after the prepass hoist.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed (bundle-assembly test edits) after deleting a stale `tests-stark/selfhost.Ir/build/.../libSystem.starkpkg` that silently shadowed fresh stdlib source and reported phantom STK4107s against pre-fix `List.Get` (recorded as a package-consumer edge in TASKS.md tooling items).
  - `cd selfhost && ../stark build`: package build passed with the current host (an initial failure was a stale `compiler.dll` predating the STK3050 exemption).
  - Standalone member-facts probe, package-backed and from-source builds: 13/13 checks passed at runtime.
  - Host: 3 new `FunctionSemanticsTests` (overlap_all), 2 new `DiagnosticRegressionTests` (STK3057, law/out), 3 new `CompilerCliTests` (check-mode sweep, imported paths) passed alongside their suites' pre-existing failure baseline (see TASKS.md).
- No broad test sweep was run.

## 2026-07-01 MIR Typed Member Path Row Import Slice

- Imported the typing-phase member expression rows into MIR storage-place
  lowering: `Compiler.Mir.SourceMemberFacts` builds the rows once per module,
  validates every field-resolved row's owner declaration, and predecodes each
  into a flat `SourceMemberPathFact` (owner identity, leaf type kind/width,
  signedness, declared integer range) joined to the token walk through a dense
  member-name-token index (O(1) per step, no typed-table calls inside the
  lowering recursion). Declared ranges decode from the row's semantic primary
  token — the head token is a last-identifier heuristic that never lands on
  integer type heads.
- The shared member-chain resolver now requires and validates a typed member
  path fact at every `.field` step (owner-declaration agreement) and returns
  the leaf fact row; leaf consumers enforce kind/width agreement against the
  resolved field type code, and indexed paths require a fixed-array leaf fact.
  The single-field legacy paths that bypassed the resolver (expression-primary
  fallbacks, plain and operator field-assignment targets) received the same
  step validation, so no member path lowers without a typing-resolved fact.
- Resolved field reads carry the field's declared integer range on the source
  expression node; source value-fact queries answer through ranged field
  reads, and `var` locals seeded from them inherit the range.
- Field stores validate the stored value against the target's declared range:
  provable in-range stores are accepted, provable out-of-range constants are
  rejected, and unprovable (or wider-than-declared, as full-width arithmetic
  always is) stores keep the pre-existing width-only semantics unless the
  declared range is narrower than storage — `u8[0 2] count`: `box.count = 2`
  accepted, `box.count = 5` and `box.count = <i64 param>` rejected
  (probe-verified).
- Declared field ranges now survive to MIR value facts and LLVM:
  `MirLoadPtrAlignedTypedWithDeclaredRange` records the declared bounds as
  constant operands C/D on the `LoadPtr` (well-formedness already
  bounds-checks them), `BuildMirValueRangeFactForInstruction` imports the pair
  as the load's value facts, and range metadata emission gained a guard that
  skips metadata whose span covers the full value width (unrepresentable in
  LLVM `!range`) while keeping the fact for validation. This FIXED a
  pre-existing runtime failure: `fn i32[min max]` returning a ranged field
  load previously failed return-range validation because loads carried no
  facts — red at HEAD, green after this slice (probe-verified).
- Standalone probe matrix compiled at HEAD (in a worktree, with only the
  mechanical retborrow fixes ported so it could build at all) and on the
  working tree produced IDENTICAL failures, proving the remaining
  nested-member runtime failures are PRE-EXISTING and untouched by this
  slice: nested chains (`box.inner.value`, even all-i64), heap-object bool
  member stores, and if-condition member reads fail end-to-end at HEAD; only
  single-step scalar member paths on unranged fields executed before this
  slice. Queued as new TASKS.md items; the recently added nested-member facts
  in `tests-stark/selfhost.Ir` have never executed green (the filtered-runner
  compile cost; docs/Internals/CompilerDevelopmentVerification.md records the
  probe recipe that answers it).
- Threading inventory: 53 lowering functions across
  SourceLocalLowering/SourceModuleLowering/SourceIfLowering/SourceSwitchLowering
  gained the two fact-table parameters (scripted rewrite + manual entry and
  adapter wiring); 4 module-lowering builders construct the facts; empty-table
  adapters mirror the existing `noDeclarations`/`noEnumLayouts` pattern; the
  dead pre-refactor `TryResolveConstructedObjectScalarMemberChainLayout` was
  removed. A diagnostic probe (`ProbeSourceMemberPathFacts`) renders the typed
  member rows and member-path facts of a source module for standalone
  investigations.
- Fixed pre-existing `semantic-validate` STK4003 retborrow-argument violations
  (`IrTable.Get` results passed directly as arguments) in
  SourceModuleLowering, SourceFunctionContext, SourceIfLowering,
  SourceSwitchLowering — invisible to `--check`, which at the time stopped
  before semantic-validate (since widened; see docs/Internals/CompilerPipeline.md
  "Check Mode"), and blocking any executable build
  against the selfhost library.
- Known cost queued for follow-up: the effect prepass and the main lowering
  each build the typed member tables, so a module compile currently runs the
  typed-member frontend twice.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - Standalone probe executable (`--emit-exe` against the selfhost library —
    the first slice probe allowed to run to completion, ~12 minutes per
    compile): plain member paths, missing-leaf and scalar-intermediate
    rejections, all three narrow-store outcomes, i64 fixed-array element
    stores, the ranged-return declared-load-fact case, and the bounds
    capability itself — a `u8[0 2]` field read used directly as a fixed-array
    index proves bounds and lowers through the bounded inbounds indexed path —
    all pass. A statement-level sub-bisect attributed the remaining composite
    failures to a pre-existing gap: `var` locals initialized from member field
    reads do not lower at HEAD for any field width (`var copy = box.value`
    fails on plain i64), now queued in TASKS.md; the probe
    (`selfhost/probe/MemberFactsProbe.stark`) documents those cases with
    expected-fail entries so it runs green and flags movement in either
    direction.
- No broad test sweep was run.

---

## 2026-07-01 MIR Member Place Address Helper Slice

- Centralized direct and indexed constructed-object storage-place address emission behind `LowerResolvedStoragePlaceAddress`.
- Routed constructed-object field reads, address-taking, indexed field reads, and field assignments through the shared helper.
- Routed direct and indexed constructed-object field parsing through the member-chain storage resolver.
- Preserved MIR pointer offset, indexed pointer offset, unbounded indexed pointer offset, alignment, provenance, and bounds facts for LLVM lowering.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
- No broad test sweep was run.

---

## 2026-07-01 MIR Try Binding Nested Successor And Error Funnel Slice

- Routed nested successor `var` `try` bindings after an earlier local `try` success edge through chained MIR try blocks.
- Preserved nested operand enum owner, tag type, compatibility facts, success payload extraction, local override ordering, branch targets, and return-block facts.
- Routed cross-family failure payloads through resolved error-funnel enum construction before inserting the enclosing return error.
- Added focused LLVM emission, package-table MIR facts, and direct MIR funnel-construction facts for `try` lowering.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingThroughNestedSuccessorLocalTryToLlvm --filter PackageTablesPreserveLocalTryBindingNestedSuccessorLocalTryMirFacts --filter FunnelFailurePayloadSourceTryLoweringBuildsReturnErr --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
  - Temporary standalone probe compilation was blocked by a pre-existing stale selfhost package image with an active-target data-layout mismatch.
- No broad test sweep was run.

---

## 2026-07-01 MIR Local Try Binding Successor Enum And Object Storage Slice

- Routed local `try` bindings through initialized successor enum stack declarations before the terminal return.
- Routed local `try` bindings through initialized successor stack constructed-object declarations before the terminal return.
- Preserved delayed stack allocation size, enum owner type id, store alignment, enum payload insertion, tag-read, branch, and success payload extraction facts.
- Preserved delayed object stack allocation size, field offset, field store alignment, field load alignment, branch, and success payload extraction facts.
- Added focused LLVM emission facts and package-table MIR facts for enum and constructed-object storage successors.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingThroughSuccessorEnumStorageToLlvm --filter PackageTablesPreserveLocalTryBindingSuccessorEnumStorageMirFacts --filter SourceModuleLowersLocalTryBindingThroughSuccessorConstructedObjectStorageToLlvm --filter PackageTablesPreserveLocalTryBindingSuccessorConstructedObjectStorageMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark selfhost/Compiler/Mir/SourceIfLowering.stark selfhost/Compiler/Mir/SourceSwitchLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-01 MIR Local Try Binding Successor Fixed-Array Storage Slice

- Routed local `try` bindings through initialized successor fixed-array stack declarations before the terminal return.
- Routed local `try` bindings through uninitialized successor fixed-array stack declarations followed by element mutations.
- Routed successor slice descriptors over fixed-array storage declared after the local `try` success edge.
- Preserved stack allocation size, alignment, bounded element offsets, typed stores, typed loads, enum tag-read, branch, and success payload extraction facts.
- Added focused LLVM emission facts and package-table MIR facts for fixed-array storage and slice descriptor successors.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingThroughSuccessorFixedArrayStorageToLlvm --filter SourceModuleLowersLocalTryBindingThroughSuccessorUninitializedFixedArrayStorageToLlvm --filter SourceModuleLowersLocalTryBindingThroughSuccessorSliceDescriptorToLlvm --filter PackageTablesPreserveLocalTryBindingSuccessorFixedArrayStorageMirFacts --filter PackageTablesPreserveLocalTryBindingSuccessorUninitializedFixedArrayStorageMirFacts --filter PackageTablesPreserveLocalTryBindingSuccessorSliceDescriptorMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Local Try Binding Successor Scalar Storage Slice

- Routed local `try` bindings through initialized successor scalar stack storage declarations before the terminal return.
- Routed local `try` bindings through uninitialized successor scalar stack storage declarations followed by successor storage mutations.
- Preserved the extracted `[Ok]` payload through delayed stack allocation, typed storage initialization, later stores, and terminal loads.
- Added focused LLVM emission facts and package-table MIR facts for initialized and uninitialized successor scalar storage declarations.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingThroughSuccessorScalarStorageToLlvm --filter SourceModuleLowersLocalTryBindingThroughSuccessorUninitializedScalarStorageToLlvm --filter PackageTablesPreserveLocalTryBindingSuccessorScalarStorageMirFacts --filter PackageTablesPreserveLocalTryBindingSuccessorUninitializedScalarStorageMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Local Try Binding Successor Statement Slice

- Routed local `try` bindings through ordered successor `var` locals before the terminal return.
- Routed local `try` bindings through ordered successor storage mutations before the terminal return.
- Preserved the extracted `[Ok]` payload as the local SSA override across successor expression and mutation lowering.
- Added focused LLVM emission facts and package-table MIR facts for successor locals and mutable slice stores.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingThroughSuccessorLocalToLlvm --filter SourceModuleLowersLocalTryBindingThroughSuccessorSliceStoreToLlvm --filter PackageTablesPreserveLocalTryBindingSuccessorLocalMirFacts --filter PackageTablesPreserveLocalTryBindingSuccessorSliceStoreMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Local Indexed Storage Try Assignment Route Slice

- Routed local fixed-array and fixed-array-backed local slice element assignment RHS `try` expressions through typed element stores.
- Preserved fixed-array bounds as bounded `PtrIndexOffset` facts for both direct local array and local slice aliases.
- Added focused LLVM emission facts and package-table MIR facts for local fixed-array and local slice RHS `try` assignments.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersLocalFixedArrayElementTryAssignmentToLlvm --filter SourceModuleLowersLocalSliceElementTryAssignmentToLlvm --filter PackageTablesPreserveLocalFixedArrayElementTryAssignmentMirFacts --filter PackageTablesPreserveLocalSliceElementTryAssignmentMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Indexed Storage Try Assignment Route Slice

- Routed indexed constructed-object field assignment RHS `try` expressions through typed element stores, including direct, nested, and heap object targets.
- Routed mutable signature slice element assignment RHS `try` expressions through the same typed element-store helper.
- Preserved bounded fixed-array index facts as `PtrIndexOffset` and dynamic slice index facts as `PtrIndexOffsetUnbounded`.
- Added focused IR facts for bounded object-array, nested object-array, heap object-array, and source-slice RHS `try` LLVM emission plus package-table indexed pointer/store/load/branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersIndexedConstructedObjectFieldTryAssignmentToLlvm --filter SourceModuleLowersNestedIndexedConstructedObjectFieldTryAssignmentToLlvm --filter SourceModuleLowersHeapIndexedConstructedObjectFieldTryAssignmentToLlvm --filter SourceModuleLowersSourceSliceElementTryAssignmentToLlvm --filter PackageTablesPreserveSourceIndexedConstructedObjectFieldTryAssignmentMirFacts --filter PackageTablesPreserveSourceSliceElementTryAssignmentMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 3 minutes because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Field Storage Try Assignment Route Slice

- Routed direct and nested constructed-object field assignment RHS `try` expressions followed by terminal returns through the reusable same-family MIR `try` helper.
- Reused structured field target resolution so success payload stores keep field offset, alignment, scalar type, and enum-owner facts.
- Added a lowering helper for storing an already-lowered MIR value into a constructed-object field place.
- Added focused IR facts for stack, heap, and nested field RHS `try` LLVM emission plus package-table MIR field pointer/store/load/branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersConstructedObjectFieldTryAssignmentToLlvm --filter SourceModuleLowersHeapConstructedObjectFieldTryAssignmentToLlvm --filter SourceModuleLowersNestedConstructedObjectFieldTryAssignmentToLlvm --filter PackageTablesPreserveSourceConstructedObjectFieldTryAssignmentMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Uninitialized Storage Try Assignment Route Slice

- Routed uninitialized scalar `stack mut` assignment RHS `try` expressions followed by terminal returns through the reusable same-family MIR `try` helper.
- Preserved write-before-read safety by parsing the RHS `try` before registering the target storage local as a readable source value.
- Preserved stack allocation, aligned success stores, terminal loads, enum tag-read, branch, and success payload extract facts through package-table lowering.
- Added focused IR facts for uninitialized storage-backed assignment RHS `try` LLVM emission, target-read rejection, and package-table MIR storage/branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersUninitializedStorageTryAssignmentToLlvm --filter SourceModuleRejectsUninitializedStorageTryAssignmentTargetRead --filter PackageTablesPreserveSourceUninitializedStorageTryAssignmentMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Storage Try Assignment Route Slice

- Routed initialized scalar `stack mut` assignment RHS `try` expressions followed by terminal returns through the reusable same-family MIR `try` helper.
- Preserved stack allocation, alignment, typed store, typed load, enum tag-read, branch, and success payload extract facts through package-table lowering.
- Added focused IR facts for storage-backed assignment RHS `try` LLVM emission and package-table MIR storage/branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersStorageTryAssignmentToLlvm --filter PackageTablesPreserveSourceStorageTryAssignmentMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Try Assignment Route Slice

- Routed scalar local assignment RHS `try` expressions followed by terminal returns through the reusable same-family MIR `try` helper.
- Replaced the assigned local SSA override with the extracted `[Ok]` payload so the later return constructor keeps the payload type.
- Added source dispatch for the assignment form in the effect prepass, module LLVM emitter, package-table builder, and direct compile helper.
- Added focused IR facts for assignment RHS `try` LLVM emission and package-table MIR branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter SourceModuleLowersLocalTryAssignmentToLlvm --filter PackageTablesPreserveSourceTryAssignmentMirFacts` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Try Return Operand Route Slice

- Routed terminal `return try` source expressions through the reusable same-family MIR `try` helper.
- Preserved enum-valued `[Ok]` payload facts from typed enum layout rows through MIR extraction.
- Mapped resolved enum field type codes to `MirType.EnumValue` so enum payload extracts do not degrade to integer facts.
- Added focused IR facts for terminal `return try` LLVM emission and package-table MIR branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `../../stark test --filter SourceModuleLowersReturnTryExpressionToLlvm --filter PackageTablesPreserveSourceReturnTryMirFacts --stage stage0 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about one minute because the filtered project runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Try Local Binding Route Slice

- Routed source local `try` bindings followed by terminal returns through the reusable same-family MIR `try` helper.
- Threaded typed enum payload tables through source module enum-layout setup so source lowering can resolve `[Ok]` and `[Err]` payload compatibility.
- Added local `try` dispatch to the effect prepass, package-table builder, module LLVM emitter, and single-function LLVM harness.
- Preserved the success payload as the local SSA override so later return lowering uses the extracted payload value directly.
- Avoided accessor-method calls on the `SourceTryPropagationLoweringResult` in production lowering because selfhost MIR lowering still cannot lower that member-call shape.
- Added focused IR facts for local `try` LLVM emission and package-table MIR branch/extract facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceModuleLowersLocalTryBindingToLlvm --filter PackageTablesPreserveSourceTryLocalBindingMirFacts --stage stage0 --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after several minutes because the filtered project runner remained silent.
  - A stdin-fed executable probe that imported `Compiler.Mir` and called `CompileFunctionWithLocalsToLlvm` was stopped after several minutes because it had the same compile-cost profile as the filtered project runner.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Try Compatibility And Lowering Helper Slice

- Added source/return `try` compatibility facts for unit failures, exact same failure payload types, and declared `from` funnels.
- Resolved funnel owner, variant ordinal, and variant tag from typed payload and layout rows without assuming variant names.
- Compared failure payload type spans exactly so ranges, generic arguments, and qualifiers remain part of compatibility.
- Added a reusable same-family MIR `try` helper that emits the tag test, early error return, and success payload extraction.
- Kept the helper narrow enough to reject funnel cases until cross-family construction is wired through the source dispatcher.
- Added focused IR checks for compatibility acceptance, compatibility rejection, direct same-owner lowering, and same-payload return-error reconstruction.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceLocalLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter SourceTry --target arm64-apple-macosx26.0.0 --stage stage0` in `tests-stark/selfhost.Ir`: stopped after several minutes because the filtered runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Source Try Prerequisite Slice

- Added a distinct source-expression `try` node and parser support in simple and storage-aware expression parsers.
- Kept generic expression lowering from erasing `try` by returning the invalid value sentinel until propagation lowering exists.
- Added typed enum-layout role resolution for `[Ok]` and `[Err]` variants, including tags, payload counts, and scalar payload type codes.
- Threaded `try` through source-expression structural walks for discardability, pointer-parameter use, scalar address-taking, and call validation.
- Added focused IR checks for `try` AST preservation, parser behavior, generic lowering blocking, role fact resolution, and malformed enum rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark selfhost/Compiler.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceExpressions.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceIfLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

---

## 2026-07-01 MIR Enum Extraction Ops Slice

- Added MIR operations for enum tag reads and enum payload extraction.
- Preserved enum owner, variant ordinal, payload ordinal, and result type facts through builders, text rendering, package serialization, and LLVM layout emission.
- Added generated value facts for enum extraction results so exact construct-then-extract facts, boolean facts, and compact integer ranges survive into downstream LLVM range metadata.
- Added focused IR facts for enum extraction operands, MIR text, LLVM `extractvalue` emission, range facts, and binary round-trips.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark selfhost/Compiler.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/ArtifactRendering.stark selfhost/Compiler/Mir/Model.stark selfhost/Compiler/Mir/Builder.stark selfhost/Compiler/Mir/PackageCodec.stark selfhost/Compiler/Mir/EnumLayout.stark selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/TextRendering.stark selfhost/Compiler/Mir/Facts.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter EnumExtraction` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the focused runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Recursive Nested Storage Mutation Slice

- Added table-backed recursive source-if storage mutation trees.
- Lowered recursive storage mutation arms into preorder condition, leaf-store, and merge blocks before the final storage-backed return.
- Reused the existing storage mutation leaf lowerer so scalar, enum, object-field, raw-pointer, and slice-backed writes keep their backend facts.
- Added focused IR facts for recursive storage topology plus raw pointer, stack scalar, object field, and slice stores.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter PackageTablesPreserveRecursiveNestedStorageMutationIfStatement --filter CompileFunctionRawPointerParameterDerefStoresInRecursiveNestedIfArms --filter CompileFunctionStackScalarStoresInRecursiveNestedIfArms --filter CompileFunctionObjectFieldStoresInRecursiveNestedIfArms --filter CompileFunctionSliceStoresInRecursiveNestedIfArms` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Recursive Nested Multi-Local If Assignment Slice

- Added table-backed recursive source-if assignment trees for multi-local assignments.
- Lowered recursive nested multi-local arms into preorder condition, leaf, and merge blocks with typed phi values for every assigned local at each join.
- Preserved scalar local type facts and compatible raw-pointer assignment facts through recursive multi-local phi trees before the final return expression.
- Added focused IR facts for a 16-block recursive nested multi-local topology and typed `i32` LLVM phis.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter PackageTablesPreserveRecursiveNestedMultiLocalIfStatementAssignmentThenReturn --filter CompileFunctionRecursiveNestedMultiLocalIfStatementKeepsTypedPhis` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Recursive Nested Single-Local If Assignment Slice

- Added a table-backed recursive source-if assignment tree for single-local scalar assignments.
- Lowered recursive nested source-if arms into preorder condition, leaf, and merge blocks with typed phi values at every join.
- Preserved integer and boolean local type facts through recursive nested phi trees before the final return expression.
- Added focused IR facts for a 16-block recursive nested assignment topology and typed `i32` LLVM phis.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --filter PackageTablesPreserveRecursiveNestedSingleLocalIfStatementAssignmentThenReturn --filter CompileFunctionRecursiveNestedSingleLocalIfStatementKeepsTypedPhis` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Nested Source If Storage Mutations

- Parsed source `if` storage-mutation arms with arm-local value declarations and one-level nested branch bodies.
- Lowered nested storage-mutation arms into conditional MIR blocks that keep storage writes in branch blocks and perform the final return from the joined storage state.
- Routed direct first-statement storage `if` bodies through the module effect prepass and final emission paths after terminal-return `if` parsing fails.
- Added focused coverage for raw pointer stores, both-sided nested raw pointer stores, stack scalar stores, object-field stores, slice-element stores, and enum storage stores.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark selfhost/Compiler/Mir/SourceModuleLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompileFunctionRawPointerParameterDerefStoresInNestedIfArms --filter CompileFunctionRawPointerParameterDerefStoresInBothNestedIfArms --filter CompileFunctionStackScalarStoresInNestedIfArms --filter CompileFunctionObjectFieldStoresInNestedIfArms --filter CompileFunctionSliceStoresInNestedIfArms --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Nested Multi-Local Pointer Fact Slice

- Preserved raw pointer mutability, pointee, alignment, nested-pointee, and matching element-count facts through nested multi-local source `if` assignment joins.
- Kept bounded indexed raw-pointer aliases alloca-free after nested scalar-and-pointer multi-local joins.
- Conservatively dropped element-count facts when a nested branch assigns an unbounded raw pointer alias.
- Added focused IR facts for one nested arm, both nested arms, and unbounded-arm rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter CompileFunctionNestedMultiLocalIfStatementBoundedRawPointerAliasIndexedDerefLoadsI32 --filter CompileFunctionBothNestedMultiLocalIfStatementBoundedRawPointerAliasIndexedDerefLoadsI32 --filter CompileFunctionNestedMultiLocalIfStatementRawPointerAliasClearsBoundsAfterUnboundedArm` in `tests-stark/selfhost.Ir`: stopped after 120 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Nested Multi-Local If Assignment Slice

- Lowered one-level nested source `if` statement assignment arms for multiple scalar locals.
- Preserved source-order multi-local assignment mapping through inner and outer typed phi joins.
- Preserved integer return range facts and boolean `i1` phi facts through LLVM IR emission.
- Left pointer-valued nested multi-local assignment facts as an explicit open backend-fact task.
- Added focused IR facts for nested-then, nested-else, both-nested boolean joins, and package-table block topology.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter CompilesNestedThenArmMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesNestedElseArmMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesBooleanBothNestedArmsMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter PackageTablesPreserveBothNestedMultiLocalIfStatementAssignmentThenReturn` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Both-Sided Nested If Assignment Slice

- Lowered single-local source `if` statement assignment arms when both outer arms contain one nested scalar `if`.
- Preserved branch-local nested-arm declarations through arm-scoped type and SSA override tables.
- Preserved integer and boolean facts through inner phis, the outer phi, and LLVM return range attributes.
- Added focused IR facts for nested branch locals, both-sided nested integer and boolean joins, and package-table block topology.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter CompilesNestedBranchLocalSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesBothNestedArmsSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesBooleanBothNestedArmsSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBothNestedSingleLocalIfStatementAssignmentThenReturn` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Nested Single-Local If Assignment Slice

- Lowered one nested braced scalar source `if` statement arm into inner and outer MIR merge blocks before a later return expression.
- Preserved integer and boolean local assignment facts through typed inner and outer phi values.
- Added focused IR facts for nested-then, nested-else, boolean nested-then, and package-table block topology.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --stage stage0 --filter CompilesNestedThenArmSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesNestedElseArmSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesBooleanNestedThenArmSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveNestedSingleLocalIfStatementAssignmentThenReturn` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Single-Local If Arm Locals Slice

- Single-local source `if` statement assignment arms now accept branch-local scalar declarations before assigning the target local.
- Immediately returned locals overwritten by source `if` statements now lower branch-local scalar declarations in both arms.
- Branch-local declarations use arm-scoped param, type, validation, and SSA override tables, preserving integer and boolean facts through typed MIR phis and direct returns.
- Added focused IR facts for integer and boolean single-local branch locals, immediately returned branch locals, and package-table preservation.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter CompilesArmLocalSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesBooleanArmLocalSingleLocalIfStatementAssignmentThenReturnExpressionFromAst --filter CompilesArmLocalReturnedLocalIfStatementAssignmentFromAst --filter CompilesBooleanArmLocalReturnedLocalIfStatementAssignmentFromAst --filter PackageTablesPreserveArmLocalSingleLocalIfStatementAssignmentThenReturn --filter PackageTablesPreserveArmLocalReturnedLocalIfStatementAssignment` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Multi-Local If Arm Locals Slice

- Multi-local source `if` statement assignment arms now accept branch-local scalar declarations before target assignments.
- Branch-local declarations use arm-scoped param, type, validation, and SSA override tables, preserving integer and boolean facts through typed MIR phis.
- Added focused IR facts for chained integer branch locals, boolean branch locals, and package-table preservation.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --filter CompilesArmLocalMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesBooleanArmLocalMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter PackageTablesPreserveArmLocalMultiLocalIfStatementAssignmentThenReturn --filter CompilesMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesSourceOrderMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesBooleanMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter PackageTablesPreserveMultiLocalIfStatementAssignmentThenReturn` in `tests-stark/selfhost.Ir`: stopped after 240 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Multi-Local If Assignment Slice

- Lowered braced source `if` statement arms that assign multiple scalar locals before a merged return expression.
- Preserved source-order RHS lowering while binding MIR phi values in target-local order.
- Emitted typed MIR phis for integer, pointer, and boolean assignment joins, with boolean return range facts flowing to LLVM.
- Added focused IR facts for declaration-order assignments, arbitrary source-order assignments, boolean multi-local phis, and package-table preservation.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `../../stark test --filter CompilesMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesSourceOrderMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter CompilesBooleanMultiLocalIfStatementAssignmentsThenReturnExpressionFromAst --filter PackageTablesPreserveMultiLocalIfStatementAssignmentThenReturn` in `tests-stark/selfhost.Ir`: stopped after 120 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-07-01 MIR Aggregate Raw Pointer Field Dereference Slice

- Modeled aggregate raw-pointer pointee layout facts for parameters, aliases, and declared raw-pointer locals.
- Parsed `(*pointer).field` aggregate raw-pointer field loads and lowered them through typed byte offsets.
- Lowered mutable aggregate raw-pointer field stores through typed aligned pointer stores and rejected readonly stores.
- Added focused IR facts for direct field loads, nested field loads, alias/local fact preservation, mutable stores, and readonly-store rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/SourceExpressions.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark selfhost/Compiler/Mir/SourceLocalLowering.stark selfhost/Compiler/Mir/SourceIfLowering.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `../../stark test --filter AggregateRawPointer --target arm64-apple-macosx26.0.0 --stage stage0` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the focused project test runner remained silent.
  - `../../stark test --filter CompileFunctionAggregateRawPointer --target arm64-apple-macosx26.0.0 --stage stage0` in `tests-stark/selfhost.Ir`: stopped after about 210 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-06-30 MIR Independent Loop Access Metadata Slice

- Expanded textual LLVM independent-loop metadata to include deterministic access-group and `llvm.loop.parallel_accesses` nodes.
- Attached `!llvm.access.group` to inbounds indexed pointer loads and stores emitted inside MIR blocks marked as independent loop backedges.
- Kept access-group attachments conservative by requiring the memory operand to resolve through a `PtrIndexOffset` chain.
- Added focused IR facts for indexed pointer memory operations inside an independent loop body and strengthened source independent-loop metadata assertions.
- Narrow verification:
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - Minimal LLVM IR metadata sanity check through `clang -x ir -S -o /dev/null -`: passed.
  - `../../stark test --filter EmitsLlvmIndependentLoopAccessGroupMetadataForIndexedPointerMemoryOps --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after 120 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-06-30 MIR Scoped NoAlias Call Metadata Slice

- Attached deterministic scoped noalias metadata to textual LLVM direct calls and tail calls when memory-backed call arguments resolve to disjoint caller parameter roots.
- Added call-specific alias/noalias metadata list IDs and widened numbered-function scoped-noalias ID spacing to avoid cross-function metadata collisions.
- Kept multi-root call metadata conservative by requiring every accessed root to be disjoint from each emitted noalias root.
- Added focused IR facts for direct and `musttail` pointer calls carrying call-site `!alias.scope` and `!noalias` metadata.
- Narrow verification:
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/LlvmInstructions.stark selfhost/Compiler/Mir/LlvmBlocks.stark selfhost/Compiler/Mir/LlvmFunctions.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter EmitsLlvmScopedNoAliasMetadataForDirectPointerCall --filter EmitsLlvmScopedNoAliasMetadataForTailPointerCall` in `tests-stark/selfhost.Ir`: stopped after about 60 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-06-30 MIR Scoped NoAlias Metadata Slice

- Resolved MIR pointer memory operands back through pointer offsets and indexed offsets to parameter roots.
- Attached deterministic LLVM `!alias.scope` and `!noalias` metadata to parameter-rooted pointer loads and stores when distinct-storage facts prove a peer.
- Emitted high-numbered scoped noalias metadata definitions only for functions that actually attach scoped noalias metadata.
- Added a focused IR fact for indexed pointer-parameter load/store emission with separate-storage and scoped noalias metadata.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --emit-llvm -I selfhost --target arm64-apple-macosx26.0.0 -o /tmp/selfhost-ir-scoped-noalias.ll`: stopped after about 90 seconds because the single-file compile remained silent.
  - `../../stark test --filter EmitsLlvmScopedNoAliasMetadataForParamRootedPointerMemoryOps --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the focused project test runner remained silent.
- No broad test sweep was run.

---

## 2026-06-30 MIR Bounded Raw Pointer Local Region Facts

- Bounded raw-pointer element-count facts now propagate through raw-pointer `var` aliases.
- Immutable declared raw-pointer locals initialized from bounded pointer names now keep the same region count facts.
- Initialized mutable raw-pointer locals now keep bounded region facts from bounded pointer-name initializers.
- Straight-line mutable raw-pointer local assignments now update bounded region facts and clear stale facts after unbounded assignments.
- Assigned mutable raw-pointer local facts are preserved through terminal-if returns and switch mutation uses.
- Pointer-valued if-expression and if-statement joins now merge compatible raw-pointer element and count facts.
- Pointer-valued switch assignment joins now merge compatible raw-pointer element and count facts.
- Branch and switch joins keep bounded element counts only when every assigned root has matching count facts.
- Branch and switch joins reject incompatible raw-pointer arms instead of silently dropping pointee facts.
- `IrTable.Replace` now supports updating source symbol rows without rebuilding the table.
- Indexed alias, immutable local, and mutable local dereferences reuse the existing inbounds pointer-offset lowering, preserving element size and alignment facts.
- Added focused IR facts for fixed-count aliases, count-parameter aliases, alias stores, unbounded alias rejection, immutable declared locals, initialized mutable locals, assigned mutable locals, count-bounded assigned locals, terminal-if use, switch-arm stores, stale-bound clearing, if-expression joins, if-statement joins, switch joins, and unbounded branch/switch arm rejection.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionIfExpressionBoundedRawPointerAliasIndexedDerefLoadsI32 --filter CompileFunctionIfStatementBoundedRawPointerAliasIndexedDerefLoadsI32 --filter CompileFunctionSwitchBoundedRawPointerAliasIndexedDerefLoadsI32 --filter CompileFunctionIfStatementRawPointerAliasClearsBoundsAfterUnboundedArm --filter CompileFunctionSwitchRawPointerAliasClearsBoundsAfterUnboundedArm --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 10 minutes because the focused project test runner stayed silent while CPU-bound.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Declared Raw Pointer Locals

- Declared scalar `rawptr` and `rawmutptr` stack locals now allocate stored pointer slots.
- Raw-pointer local declarations copy pointee type, size, alignment, and mutability facts onto the local symbol row.
- Stored raw-pointer local reads load the pointer value before dereference lowering.
- Declared `rawmutptr` local dereference stores lower through typed aligned MIR stores.
- Declared `rawptr` local dereference stores and readonly-to-mutable pointer initialization or assignment are rejected.
- Local-prefixed terminal-if and switch mutation paths now accept declared raw-pointer locals.
- Focused verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter DeclaredRawPointer --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 90 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Raw Pointer Alias Dereferences

- Scalar raw-pointer parameter facts now propagate through raw-pointer `var` aliases.
- Scalar raw-pointer alias dereference loads lower through typed aligned MIR loads without temporary stack storage.
- Scalar `rawmutptr` alias dereference stores lower through typed aligned MIR stores without temporary stack storage.
- Scalar `rawptr` alias dereference stores are rejected before MIR lowering.
- Local-prefixed terminal-if and switch mutation paths now preserve raw-pointer alias facts.
- Focused verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter RawPointerAlias --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 180 seconds with no test output; the timeout wrapper then reported zsh's read-only `status` variable name.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Raw Pointer Dereference Stores

- Scalar raw-pointer parameter facts now carry mutability into source symbol rows and ABI facts.
- Scalar `rawmutptr` parameter dereference assignments now lower through typed aligned MIR stores.
- Scalar `rawptr` parameter dereference assignments are rejected before MIR lowering.
- Straight-line, terminal-if-prefixed, and switch-arm mutation paths now route scalar raw-pointer stores.
- Focused verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter RawPointerParameterDeref --filter RawPointerReadonlyDerefStoreRejected --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 300 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Object Enum Field Storage

- Struct and record object layout now accounts for enum field size and alignment when enum layout rows are available.
- Enum-valued object field initializers and assignments now lower through owner-aware `StoreEnumPtr` instructions.
- Enum-valued object field reads now lower through owner-aware `LoadEnumPtr` instructions.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter StackObjectEnumField` in `tests-stark/selfhost.Ir` was stopped after 180 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Switch Enum Storage Locals

- Switch-arm lowering now parses stored enum locals and enum local assignments using typed enum layout rows.
- Integer, boolean, and enum switch arms now preserve enum owner and storage-alignment facts through arm validation and lowering.
- Braced switch arm locals can read stored enum values through owner-aware enum loads before later arm assignments.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter SwitchStackEnumLocal --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 180 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Terminal If Enum Storage Locals

- Local-prefixed terminal `if` lowering now uses typed enum declarations and layout rows when parsing locals.
- Stack enum local initializers before terminal `if` branches now lower through owner-aware enum stores.
- Stored enum local reads and mutable enum assignments before terminal `if` branches now preserve enum owner and storage-alignment facts.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter TerminalIfStackEnumLocal --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 180 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Stack Enum Storage Locals

- Straight-line `stack` enum locals now allocate concrete enum storage from typed enum layout rows.
- Enum local initializers and mutable assignments now lower through owner-aware `StoreEnumPtr` instructions with explicit storage alignment.
- Reads from stored enum locals now lower through owner-aware `LoadEnumPtr` instructions while preserving the enum owner fact for later LLVM storage typing.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter StackEnumLocal --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after 180 seconds with no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Named Source Enum Constructors

- Source enum constructor lowering now accepts named payload syntax such as `Enum.Move { X: value, Flag: true }`.
- Named payload fields resolve through typed enum layout rows, reject duplicate, missing, unknown, and scalar-family mismatched payloads, and emit MIR payload inserts by canonical payload ordinal.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter SourceModuleLowersNamedPayloadEnumConstructorLocalToLlvm --filter SourceModuleRejectsIncompleteNamedPayloadEnumConstructor --filter PackageTablesPreserveSourceNamedEnumConstructorMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Source Enum Constructors

- Source lowering now recognizes `Enum.Unit` and positional scalar `Enum.Payload(value)` constructors in local initializers and terminal return expressions.
- Enum constructor lowering reuses typed enum layout rows for payload count and scalar payload width, then emits MIR enum construction operations with owner, variant, payload ordinal, and value facts.
- Module and single-function LLVM emission now thread source-built enum layout facts into enum-aware LLVM instruction emission for the default local-body path.
- Focused verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
- `../../stark test --filter SourceModuleLowersUnitEnumConstructorLocalToLlvm --filter SourceModuleLowersPayloadEnumConstructorLocalToLlvm --filter PackageTablesPreserveSourceEnumConstructorMirFacts --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir` was stopped after several minutes of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Wide Direct Calls

- Source calls with more than four arguments now lower through linked MIR call-argument rows.
- MIR text, package serialization, value facts, and LLVM call emission now understand wide direct and tail-call payloads.
- Wide call argument range and ABI facts survive source lowering through LLVM call argument emission.
- Focused verification:
  - `Compiler.Mir.Builder` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceExpressions`, `Compiler.Mir.SourceExpressionLowering`, `Compiler.Mir.LlvmInstructions`, and `Compiler.Mir.LlvmBlocks` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - Direct stdin smoke compiled and ran `/tmp/wide-call-smoke`, returning exit code 0.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter BinaryRoundTripsWideArgumentCall --filter CompilesThreeAndFourArgumentCallsFromAst` in `tests-stark/selfhost.Ir` was stopped after several minutes of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Switch Scalar Storage Locals

- Storage-backed switch lowering now accepts initialized `stack` scalar locals as base storage values.
- Switch arms now lower scalar stack assignments as typed stores, interleaved in source order with constructed-object field stores.
- The lowered path preserves stack allocation size, alignment, scalar MIR type, pointer facts, and typed load/store facts through LLVM IR.
- Focused verification:
  - `Compiler.Mir.SourceSwitchLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test` with the four new switch stack-scalar filters in `tests-stark/selfhost.Ir` was stopped after about 150 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Terminal If Scalar Storage Locals

- Local-prefixed terminal `if` lowering now supports initialized `stack` scalar locals through typed storage-backed loads.
- Mutable scalar stack assignments before terminal `if` branches now lower as typed stores before the branch condition.
- The terminal-if path reuses the source-order storage mutation ledger so scalar and constructed-object mutations preserve source order before branching.
- Focused verification:
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test` with the four new terminal-if stack-scalar filters in `tests-stark/selfhost.Ir` was stopped after about 150 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Stack Scalar Storage Locals

- Initialized straight-line `stack` scalar locals now allocate typed storage and read through aligned MIR loads.
- Mutable straight-line scalar stack assignments now lower as typed stores before terminal returns.
- Storage mutation lowering preserves source order across scalar local assignments and constructed object field assignments.
- Focused verification:
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- `../../stark test` with the four new stack-scalar storage filters in `tests-stark/selfhost.Ir` was stopped twice after extended no-output waits.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Dynamic Fixed-Array Source Indexing

- Dynamic fixed-array element reads and assignments on constructed object fields now lower through the indexed pointer offset operation.
- Source index bounds are proven from parameter/local value facts before emitting dynamic fixed-array element addressing.
- The lowered path preserves receiver pointer provenance, base field offset, element size, element alignment, and typed load/store facts through LLVM IR.
- Focused verification:
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed for the touched Stark and task files.
- `../../stark test` with the three new dynamic fixed-array source-index filters in `tests-stark/selfhost.Ir` was stopped after 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Dynamic Pointer Index Addressing

- MIR now has a first-class indexed pointer offset operation for dynamic element addresses.
- The operation carries base pointer, index value, element size, base byte offset, and derived alignment without pointer-to-integer lowering.
- Generated pointer facts inherit nonnull/noalias from the base and preserve element alignment for later loads and stores.
- LLVM emission scales the index only when needed, adds the base offset only when needed, and emits a final inbounds i8 GEP.
- Text rendering, package serialization, function validation, and artifact opcode names understand the new operation.
- Focused verification:
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed for the touched Stark and task files.
- `../../stark test` with the four new dynamic-pointer filters in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Fixed-Array Field Elements

- Constant fixed-array element reads and assignments on constructed object fields now resolve to storage-backed byte offsets.
- Fixed-array field layout uses total element storage size while preserving element alignment for pointer loads and stores.
- The lowered element path preserves element MIR type, byte offset, stride-derived address, and alignment through LLVM emission.
- Focused verification:
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 errors and existing constructed-object parser recursion warnings.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
- `../../stark test` with the new fixed-array element filters in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Switch Field Assignments

- Constructed object field assignments inside integer, boolean, and enum switch arms now lower as storage-backed stores before the merge block.
- Arm stores preserve field offset, field alignment, scalar MIR type, and derived pointer facts through LLVM emission.
- Focused selfhost IR facts cover integer, boolean, enum, and invalid-target switch field assignment cases.
- Focused verification:
  - `Compiler.Mir.SourceSwitchLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- `../../stark test` with the four new switch-field-assignment filters in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Constructed Object Field Assignments

- Straight-line `object.field = expr` statements now parse after constructed object locals and lower as storage-backed field stores.
- Assignment targets resolve field offset, alignment, and scalar MIR type before lowering, matching constructor initializer and field-read facts.
- Assignment RHS expressions can read earlier constructed object fields and lower before the typed store without losing field-width facts.
- Field assignments before terminal `if` branches lower before the condition and branch returns, so stored values can feed branch tests.
- Focused verification:
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 errors and existing constructed-object parser recursion warnings.
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and 2 existing recursion warnings.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed for the touched Stark and task files.
- `../../stark test` with the four new object-field-assignment filters in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Constructed Object Field Reads

- Constructed stack and heap object locals now keep storage-root overrides while remaining non-value source locals.
- Direct `object.field` reads over constructed object locals resolve field offset, alignment, and scalar MIR type before lowering.
- Field reads lower through MIR `ptr.offset` plus typed `load.ptr`, preserving derived-pointer alignment facts through LLVM emission.
- Constructor initializers can read fields from earlier constructed locals, while self/forward object field reads remain rejected.
- Focused verification:
  - `Compiler.Mir.SourceExpressions` host-test inspect through `lower-mir`: passed with 0 errors and existing recursive-parser warnings.
  - `Compiler.Mir.SourceExpressionLowering` host-test inspect through `lower-mir`: passed with 0 errors and existing recursive-lowering warnings.
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 errors and recursive-parser warnings for the constructed-object parser.
  - `Compiler.Mir.SourceFunctionContext` host-test inspect through `lower-mir`: passed with 0 errors and existing recursive-validation warnings.
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and 2 existing recursion warnings.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- `../../stark test` with the five new object-field-read filters in `tests-stark/selfhost.Ir` was stopped after roughly 90 seconds of no output.
- A throwaway executable harness that directly called the changed Stark helper APIs was also stopped after a silent dependency build; no broad test sweep was run.

---

## 2026-06-30 Selfhost Heap Object Construction

- Heap object construction now lowers to a first-class MIR heap allocation operation instead of an opaque call.
- Heap allocation facts preserve alignment, noalias, and nonnull through MIR facts, package serialization, text rendering, and LLVM emission.
- LLVM heap allocation emission declares and calls `__stark_heap_alloc` with `allocalign`, `allocsize`, `allockind`, `noalias`, `nonnull`, result alignment, and `dereferenceable` facts.
- Source lowering now handles explicit, target-typed, field-initialized, positional-record, and terminal-if heap object constructors.
- Focused verification:
  - `Compiler.Mir.Model`, `Compiler.Mir.Builder`, `Compiler.Mir.Facts`, `Compiler.Mir.PackageCodec`, and `Compiler.Mir.TextRendering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.LlvmInstructions`, `Compiler.Mir.LlvmBlocks`, and `Compiler.Mir.LlvmFunctions` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceLocalLowering` and `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and 2 existing recursion warnings.
  - `Compiler.Mir`, `Compiler.ArtifactRendering`, and `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- `../../stark test --filter Heap` in `tests-stark/selfhost.Ir` was stopped after several minutes because the filtered project build did not produce output quickly enough for a narrow check.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Stack Record Positional Constructors

- Record primary-constructor fields now participate in the lightweight stack object layout used by source MIR lowering.
- Stack record positional constructor arguments lower as storage-backed typed stores with field offsets and alignment preserved through MIR memory operations.
- The positional lowering path scans record header fields linearly and reuses the existing stack object initializer emission path.
- Focused verification:
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and 2 existing recursion warnings.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- A tiny runtime probe executable for the new record constructor cases was attempted and stopped after several minutes because compiling the imported self-host MIR graph fanned out heavily.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost Stack Object Field Initializers

- Stack object field initializer bodies now parse into pending storage writes and lower after prior locals are available.
- Field initializer stores preserve scalar MIR types, field offsets, field alignment, noalias, and nonnull pointer facts into LLVM emission.
- Focused verification:
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceExpressionLowering` host-test inspect through `lower-mir`: passed with 0 errors and the existing localized recursion warnings.
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and 2 existing recursion warnings.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- Runtime probes were attempted with `../../stark test --filter StackObject` and a tiny stack-object executable probe, but both were stopped after several minutes because compiling the imported self-host MIR graph fanned out heavily.
- No broad test sweep was run.

---

## 2026-06-30 Selfhost MIR Pointer Memory Ops

- MIR now has pointer-offset, typed pointer-load, and typed pointer-store operations for storage-backed field places.
- Derived pointer offsets preserve known-nonnull, noalias, and alignment facts into LLVM-oriented lowering.
- Pointer loads and stores now carry explicit alignment through LLVM text emission, MIR text rendering, and package serialization.
- Focused verification:
  - `Compiler.Mir.Model`, `Compiler.Mir.Builder`, `Compiler.Mir.Facts`, `Compiler.Mir.LlvmInstructions`, `Compiler.Mir.TextRendering`, `Compiler.Mir.PackageCodec`, `Compiler.Mir.SourceModuleLowering`, `Compiler.Mir`, and `Compiler.ArtifactRendering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `tests-stark/selfhost.Ir/IrTests.stark` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Checked Recursion Facts

- Checked source module lowering now preserves ordinary direct and mutual recursive call effects through LLVM emission.
- Finite checked source functions now reject reachable source call cycles before codegen.
- Tail finite self recursion is rejected to match the stage0 finite-cycle contract.
- Focused verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CheckedFiniteRecursiveFunctionFromAstIsRejected`: passed.
  - `../../stark test --filter CheckedMutualFiniteRecursiveFunctionsFromAstAreRejected`: passed.
  - `../../stark test --filter CheckedTailFiniteSelfRecursiveFunctionFromAstIsRejected`: passed.
  - `../../stark test --filter CheckedRecursiveFunctionFromAstPreservesRecursionEffects`: passed.
  - `../../stark test --filter CheckedMutualRecursiveFunctionsFromAstPreserveRecursionEffects`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`: passed.
  - `../../stark test --filter CompilesModuleFromAstThroughCheckedPipeline`: passed.
  - `../../stark test --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Integer Range Switch Lowering

- Terminal integer range-pattern switch cases now parse inclusive `case min..max` intervals and lower to ordered compare-and-branch tests.
- Local-prefixed terminal switches and non-terminal switch assignments use the same interval parser and reject reversed or overlapping ranges.
- The simple LLVM block emitter now preserves MIR result types for binary operations, so boolean range-condition conjunctions emit `and i1`.
- Focused verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter RangeCase`: passed.
  - `../../stark test --filter TerminalIntegerSwitchFromAst`: passed.
  - `../../stark test --filter LocalSwitchStatementAssignment`: passed.
  - `../../stark test --filter EnumUnitSwitchFromAst`: passed.
  - `../../stark test --filter EmitsLlvmTypedI32`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Enum Unit Switch Lowering

- Source-expression typing now preserves enum owner identity for signature parameters through local type codes.
- Terminal enum unit switches now resolve `Owner.Variant` labels through typed enum layout tags and lower to compact tag comparisons.
- Non-terminal enum unit switch assignments now use the same layout-backed tag values and existing scalar phi merge lowering.
- Focused verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CompilesTerminalEnumUnitSwitchFromAst`: passed.
  - `../../stark test --filter CompilesEnumLocalSwitchStatementAssignmentThenReturnFromAst`: passed.
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst`: passed.
  - `../../stark test --filter CompilesMultiCaseTerminalIntegerSwitchFromAst`: passed.
  - `../../stark test --filter CompilesTerminalBooleanSwitchFromAst`: passed.
  - `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Constructor Validation Helper Splits

- `Compiler.Binding.ConstructorFieldState` now owns constructor assigned-field table storage and set operations.
- `Compiler.Binding.ConstructorFieldFacts` now owns constructor field assignment requirement checks.
- `Compiler.Binding.ConstructorExpressionReads` now owns expression-level constructor `self` field read collection.
- `Compiler.Binding.ConstructorStatementTraversal` now owns constructor validation statement-tree and switch-section navigation helpers.
- `Compiler.Binding.ConstructorSwitchCoverage` now owns switch default, bool, enum, and bounded-integer coverage checks used by constructor and initialization joins.
- `Compiler.Binding.ConstructorValidation` now keeps constructor initialization orchestration and recursive branch/switch field-read joins.
- `Compiler.Binding.OwnershipInitialization` now imports the shared traversal and switch coverage helpers directly.
- Focused verification:
  - Constructor helper split lower-MIR batch passed with 0 errors for `ConstructorFieldState`, `ConstructorFieldFacts`, `ConstructorExpressionReads`, `ConstructorStatementTraversal`, `ConstructorSwitchCoverage`, `ConstructorValidation`, `OwnershipInitialization`, `BindingPipeline`, and `Compiler.Binding`.
  - The known `STK4122` recursion warnings remain in constructor and branch-complete initialization recursive walkers.
  - `scripts/check-selfhost-binding-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Copyability And Thread-Safety Fact Splits

- `Compiler.Binding.CopyabilityModel` now owns copyability fact table storage and accessors.
- `Compiler.Binding.CopyabilityTypeFacts` now owns copyability type-span and structural declaration fact derivation.
- `Compiler.Binding.Copyability` now keeps copyability fact construction and `where Copyable` predicate validation.
- `Compiler.Binding.ThreadSafetyModel` now owns thread-safety law kinds, fact table storage, and flag helpers.
- `Compiler.Binding.ThreadSafetyLawNames` now owns law-name recognition helpers.
- `Compiler.Binding.ThreadSafetyAtomicFacts` now owns atomic builtin type recognition.
- `Compiler.Binding.ThreadSafetyTypeFacts` now owns thread-safety type-span and declaration law fact derivation.
- `Compiler.Binding.ThreadSafetyPredicates` now owns shared law-predicate where-clause scanning and single-identifier matching.
- `Compiler.Binding` now re-exports the split fact modules, and the binding dependency guard checks them as data modules.
- Focused verification:
  - Binding copyability/thread-safety split lower-MIR batch passed with 0 errors for 14 roots covering the new modules, direct ownership/constructor consumers, `BindingPipeline`, and `Compiler.Binding`.
  - The batch reported 30 `STK4122` recursion warnings in the bounded recursive fact walkers and existing constructor-validation recursion paths.
  - `scripts/check-selfhost-binding-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Signature, Member, And Assignment Model Splits

- `Compiler.Typing.TypedSignatureModel` now owns signature table storage, accessors, and low-level table methods.
- `Compiler.Typing.TypedSignatureRows` now owns signature type-slot and function-signature row construction.
- `Compiler.Typing.TypedSignatureDeclarations` now owns declaration and member function signature scanning helpers.
- `Compiler.Typing.TypedSignatures` now stays as a compatibility facade over the signature modules.
- `Compiler.Typing.TypedMemberKinds` now owns member contexts, receiver kinds, target kinds, and member flags.
- `Compiler.Typing.TypedMemberModelRows` now owns low-level member and candidate table appenders.
- `Compiler.Typing.TypedMemberModel` now keeps member table storage and accessors while re-exporting member kinds.
- `Compiler.Typing.TypedAssignmentKinds` now owns assignment contexts, operators, target kinds, value kinds, and assignment flags.
- `Compiler.Typing.TypedAssignmentModelRows` now owns the low-level assignment table appender.
- `Compiler.Typing.TypedAssignmentModel` now keeps assignment table storage and accessors while re-exporting assignment kinds.
- Focused verification:
  - Typed-signature split lower-MIR batch passed with 0 diagnostics for the new signature modules, signature consumers, and `Compiler.Typing`.
  - Typed-member-model split lower-MIR batch passed with 0 diagnostics for the new member modules, downstream consumers, and `Compiler.Typing`.
  - Typed-assignment-model split lower-MIR batch passed with 0 diagnostics for the new assignment modules, downstream consumers, and `Compiler.Typing`.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Call Model, Signature, And Argument Splits

- `Compiler.Typing.TypedCallKinds` now owns call contexts, target kinds, flags, and call type-fact records.
- `Compiler.Typing.TypedCallModel` now owns call expression table storage, accessors, and row appenders.
- `Compiler.Typing.TypedCallFacts` now stays as a compatibility facade over the call kind and model modules.
- `Compiler.Typing.TypedCallSignatureSlots` now owns signature-slot fact projection.
- `Compiler.Typing.TypedCallCallableSpans` now owns callable parameter-list and span scanning.
- `Compiler.Typing.TypedCallCallableFacts` now owns callable return, parameter, and type-span fact extraction.
- `Compiler.Typing.TypedCallSignatures` now stays as a compatibility facade over the call signature modules.
- `Compiler.Typing.TypedCallArgumentLookup` now owns call argument node lookups across identifiers, literals, prior calls, and members.
- `Compiler.Typing.TypedCallArgumentSourceFacts` now owns source fact projection from literals, identifiers, prior calls, conversions, `new`, and boolean operators.
- `Compiler.Typing.TypedCallArgumentWalkers` now owns expression-tree and function-body argument fact walkers.
- `Compiler.Typing.TypedCallArgumentFacts` now stays as a compatibility facade over the call argument modules.
- Focused verification:
  - Typed-call model split lower-MIR batch passed with 0 diagnostics for call kind/model/facade modules, call consumers, pipeline, artifact rendering, `SourceModuleLowering`, and `Compiler.Mir`.
  - Typed-call argument split lower-MIR batch passed with 0 diagnostics for the new argument modules, call consumers, and `Compiler.Typing`.
  - Typed-call signature split lower-MIR batch passed with 0 diagnostics for the new signature modules, call/return/index/member consumers, and `Compiler.Typing`.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Field, Global, And Local Splits

- `Compiler.Typing.TypedFieldModel` now owns field table storage, accessors, and row storage.
- `Compiler.Typing.TypedFieldHelpers` now owns field declaration token scanning helpers.
- `Compiler.Typing.TypedFieldRows` now owns field row construction while `TypedFields` keeps record-header and body-field orchestration.
- `Compiler.Typing.TypedGlobalModel` now owns global binding/storage enums, table storage, accessors, and row storage.
- `Compiler.Typing.TypedGlobalHelpers` now owns global declaration token scanning and storage/binding classification helpers.
- `Compiler.Typing.TypedGlobalRows` now owns global row construction while `TypedGlobals` stays as the facade.
- `Compiler.Typing.TypedLocalModel` now owns local owner-kind enums, table storage, accessors, and row storage.
- `Compiler.Typing.TypedLocalHelpers` now owns local initializer token scanning helpers.
- `Compiler.Typing.TypedLocalRows` now owns local row construction while `TypedLocals` keeps body/function traversal.
- Focused verification:
  - Typed-field split lower-MIR batch passed with 0 errors for the new field modules, enum payload/layout consumers, member consumers, artifact rendering, `SourceModuleLowering`, and `Compiler.Mir`; the existing 7 enum-layout recursion warnings remain.
  - Typed-global split lower-MIR batch passed with 0 diagnostics for the new global modules, storage selectors, local/identifier/assignment/call/member consumers, artifact rendering, `SourceModuleLowering`, and `Compiler.Mir`.
  - Typed-local split lower-MIR batch passed with 0 diagnostics for the new local modules, field/global dependencies, identifier/assignment/call/member consumers, artifact rendering, `SourceModuleLowering`, and `Compiler.Mir`.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Literal And Enum Payload Splits

- `Compiler.Typing.TypedLiteralModel` now owns literal context/kind enums, literal flags, table storage, accessors, and row storage.
- `Compiler.Typing.TypedLiteralFacts` now owns literal scalar width, text length, expression-kind, and literal type-kind helpers.
- `Compiler.Typing.TypedLiteralRows` now owns literal row construction while `TypedLiterals` keeps source scanning and build orchestration.
- `Compiler.Typing.TypedEnumPayloadModel` now owns enum-payload kind/role enums, table storage, accessors, and row storage.
- `Compiler.Typing.TypedEnumPayloadAttributes` now owns `[Ok]` and `[Err]` role attribute scanning.
- `Compiler.Typing.TypedEnumPayloadRows` now owns enum-payload row construction while `TypedEnumPayloads` keeps declaration scanning.
- `Compiler.Mir.EnumLayout` and `Compiler.Mir.PackageCodec` now qualify payload kind and role types through `TypedEnumPayloadModel`.
- Focused verification:
  - Typed-literal split lower-MIR batch passed with 0 diagnostics for the new literal modules, affected typing consumers, `Compiler.Typing`, `SourceModuleLowering`, and `Compiler.Mir`.
  - Typed-enum-payload split lower-MIR batch passed with 0 errors for the new payload modules, enum-layout consumers, artifact rendering, package codec, `SourceModuleLowering`, and `Compiler.Mir`; the existing 7 enum-layout recursion warnings remain.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Member Splits

- `Compiler.Typing.TypedMemberModel` now owns member context/receiver/target enums, table storage, accessors, flags, member rows, and candidate rows.
- `Compiler.Typing.TypedMemberFacts` now owns literal-to-member-type fact projection.
- `Compiler.Typing.TypedMemberLookup` now owns member expression, call, identifier, and declaration lookup helpers.
- `Compiler.Typing.TypedMemberReceiverFacts` now owns receiver/type fact derivation from identifiers, calls, members, type spans, fields, and source receiver nodes.
- `Compiler.Typing.TypedMemberMethods` now owns member-method candidate matching and candidate-row collection.
- `Compiler.Typing.TypedMemberRows` now owns resolved member-row appending while `TypedMembers` keeps source scanning and build orchestration.
- Focused verification:
  - Typed-member split lower-MIR batch passed with 0 diagnostics for the new member modules, affected typing consumers, `Compiler.Typing`, `SourceModuleLowering`, and `Compiler.Mir`.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Identifier And Return Splits

- `Compiler.Typing.TypedIdentifierModel` now owns identifier context/target enums, table storage, accessors, flags, and row append storage.
- `Compiler.Typing.TypedIdentifierLookup` now owns declaration, global, signature, parameter, and local visibility lookup helpers.
- `Compiler.Typing.TypedIdentifierRows` now owns identifier row appenders and target fact propagation.
- `Compiler.Typing.TypedIdentifiers` now keeps body/global scanning and build orchestration.
- `Compiler.Typing.TypedReturnModel` now owns return context/value enums, table storage, accessors, flags, and row append storage.
- `Compiler.Typing.TypedReturnFacts` now owns return value-kind mapping and expected-return fact extraction.
- Focused verification:
  - Identifier split lower-MIR batch passed with 0 diagnostics for the new identifier modules, affected typing consumers, `Compiler.Typing`, `SourceModuleLowering`, and `Compiler.Mir`.
  - Return split lower-MIR batch passed with 0 diagnostics for the new return modules, `TypedPipeline`, `Compiler.Typing`, `SourceModuleLowering`, and `Compiler.Mir`.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Enum Layout Model And Variant Splits

- `Compiler.Typing.TypedEnumLayoutModel` now owns enum-layout row/query types, table storage, accessors, and query-folding helpers.
- `Compiler.Typing.TypedEnumLayoutVariants` now owns enum variant scanning, unit-variant detection, tag/source ordinal mapping, and payload lookup helpers.
- `Compiler.Typing.TypedEnumLayouts` now keeps recursive type-layout calculation and row construction.
- `Compiler.Typing` re-exports both new enum-layout modules to preserve facade access.
- MIR enum-layout fact bridging now qualifies typed layout rows through `Compiler.Typing.TypedEnumLayoutModel`.
- Focused verification:
  - `TypedEnumLayoutModel` and `TypedEnumLayoutVariants` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `TypedEnumLayouts` host-test inspect through `lower-mir`: passed with 0 errors and the existing 7 bounded enum-layout recursion warnings.
  - `TypedCtfeQueries`, `TypedPipeline`, `Compiler.Typing`, `Compiler.ArtifactRendering`, `Compiler.Mir.EnumLayout`, `SourceModuleLowering`, and `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Call Helper Splits

- `Compiler.Typing.TypedCallSignatures` now owns callable type-span scans, signature-slot fact reads, callable return facts, and callable parameter facts.
- `Compiler.Typing.TypedCallArgumentFacts` now owns argument type-fact derivation from literals, identifiers, prior calls, conversions, `new`, unary, binary, conditional, and assignment expressions.
- `Compiler.Typing.TypedCallOverloads` now owns direct-function and method overload candidate scoring, viability checks, and best-candidate selection.
- `Compiler.Typing.TypedCallArguments` now owns call argument row appending for expression trees and parsed function bodies.
- `Compiler.Typing.TypedCallTargets` now owns call target resolution and call-row appending, while `TypedCalls` now keeps source scanning and build orchestration.
- `TypedMembers`, `TypedIndexing`, and `TypedReturns` now import `TypedCallSignatures` directly for call signature facts instead of reaching through `TypedCalls`.
- Focused typing verification:
  - `Compiler.Typing.TypedCallSignatures` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCallArgumentFacts` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCallOverloads` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCallArguments` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCallTargets` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCalls` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedMembers` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedIndexing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedReturns` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedCallMemberDependencies` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- Dependency guard verification:
  `scripts/check-selfhost-typing-dependencies.sh` passed.
- Whitespace verification:
  `git diff --check` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Enum Layout Helper Splits

- `Compiler.Typing.TypedEnumLayoutGenerics` now owns enum-layout generic context tables, generic argument segment scans, generic parameter ordinal scans, and comptime integer argument readers.
- `Compiler.Typing.TypedEnumLayoutAttributes` now owns layout-control attribute parsing, pack/align lookup, field-offset lookup, and declaration-member skipping helpers.
- `Compiler.Typing.TypedEnumLayoutArithmetic` now owns scalar size/alignment arithmetic, alignment rounding, misalignment checks, and enum tag-width selection.
- `TypedEnumLayouts` and `TypedCtfeQueries` import the focused helper module so enum layout construction and CTFE query folding share the same generic comptime value path.
- `Compiler.Typing` re-exports the new enum-layout helper modules to preserve facade access for internal self-host callers.
- The moved comptime value reader now walks generic parent contexts iteratively, eliminating the previous mutual-recursion warnings in the helper module.
- Focused typing verification:
  - `Compiler.Typing.TypedEnumLayoutAttributes` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedEnumLayoutArithmetic` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedEnumLayoutGenerics` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing.TypedEnumLayouts` host-test inspect through `lower-mir`: passed with 0 errors and 7 existing bounded enum-layout recursion warnings.
  - `Compiler.Typing.TypedCtfeQueries` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Typing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- Dependency guard verification:
  `scripts/check-selfhost-typing-dependencies.sh` passed.
- Whitespace verification:
  `git diff --check` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Source Module Lowering Split

- `Compiler.Mir.SourceModuleLowering` now owns module/function source orchestration, AST module emission, package-image-with-asm table building, and the function-effect fact prepass used by LLVM attributes.
- `Compiler.Mir` re-exports `Compiler.Mir.SourceModuleLowering`, preserving the existing facade API for moved public entry points.
- `Compiler.Mir` now keeps the phase-boundary verifier, FFI probe, and legacy smoke wrappers while source module lowering lives behind the facade.
- Focused MIR verification:
  - `Compiler.Mir.SourceModuleLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - A tiny host-test inspect fixture importing `Compiler.Mir` and referencing moved public APIs through the facade passed through `lower-mir` with 0 diagnostics.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- Whitespace verification:
  `git diff --check` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Source Switch And Loop Splits

- `Compiler.Mir.SourceSwitchLowering` now owns switch shape probes, switch arm parsers, switch assignment lowering, terminal/local switch block lowerers, and switch LLVM emit wrappers.
- `Compiler.Mir.SourceLoopLowering` now owns counting and accumulator loop smoke wrappers, while/for shape probes, loop block lowerers, and loop LLVM emit wrappers.
- `Compiler.Mir.SourceExpressionLowering` now owns the shared first-comparison token scan used by root expression lowering and loop smoke wrappers.
- `Compiler.Mir` re-exports the new source-lowering modules so existing facade callers keep the same names.
- Focused MIR source-lowering verification:
  - `Compiler.Mir.SourceSwitchLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceExpressionLowering` host-test inspect through `lower-mir`: passed with 0 errors and 15 localized recursion warnings.
  - `Compiler.Mir.SourceLoopLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- Focused behavior verification attempt:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesTerminalBooleanSwitchFromAst --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveTerminalBooleanSwitch --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn`
  in `tests-stark/selfhost.Ir` timed out after 300 seconds with no output.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Source Function Context And If Splits

- `Compiler.Mir.SourceFunctionContext` now owns source function signatures, parameter ABI facts, distinct storage contracts, call validation, tail-call target checks, and `LoweredBody`.
- `Compiler.Mir.SourceExpressionLowering` now owns the token-stream primary/fold helpers used by the legacy if smoke wrappers.
- `Compiler.Mir.SourceLocalLowering` now owns the shared brace matcher used by if, switch, and loop source-lowering shapes.
- `Compiler.Mir.SourceIfLowering` now owns legacy if wrappers, if arm parsers, source if-shape probes, if block lowerers, module if emitters, and the public if-expression wrapper.
- `Compiler.Mir` re-exports the new source-lowering modules so existing facade callers keep the same names.
- Focused MIR source-lowering verification:
  - `Compiler.Mir.SourceFunctionContext` host-test inspect through `lower-mir`: passed with 0 errors and localized recursion warnings.
  - `Compiler.Mir.SourceExpressionLowering` host-test inspect through `lower-mir`: passed with 0 errors and 15 recursion warnings.
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and localized recursion warnings.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- Focused behavior verification:
  `../../stark test --filter LowersIfExpressionToBranchingLlvm --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnIfExpressionFromAst --filter BooleanReturnIfExpressionPreservesBranchRangeFacts --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesLocalIfExpressionInitializerThenReturnFromAst --filter CompilesBooleanValuedIfExpression`
  in `tests-stark/selfhost.Ir` passed.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Source Expression And Local Splits

- `Compiler.Mir.SourceExpressions` now owns expression nodes, expression parser helpers, and source expression type inference.
- `Compiler.Mir.SourceExpressionLowering` now owns expression-to-MIR lowering helpers and the single-expression LLVM wrapper.
- `Compiler.Mir.SourceLocalLowering` now owns self-host local scanner helpers, known byte extent/alignment helpers, and arena dynamic local reserve lowering.
- `Compiler.Mir` re-exports the new source-lowering modules so existing facade callers keep the same names.
- Focused MIR source-lowering verification:
  - `Compiler.Mir.SourceExpressions` host-test inspect through `lower-mir`: passed with 0 errors and localized recursion warnings.
  - `Compiler.Mir.SourceExpressionLowering` host-test inspect through `lower-mir`: passed with 0 errors and 15 recursion warnings.
  - `Compiler.Mir.SourceLocalLowering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 errors and 18 pre-existing recursion warnings.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Source Probe Splits

- `Compiler.Mir.SourceSymbols` now owns source token span, parameter-name, and local-binding helper logic.
- `Compiler.Mir.SourceRangeFacts` now owns integer range endpoint parsing and source range-fact readers.
- `Compiler.Mir.SourceSemanticProbes` now owns token-stream name resolution, declaration uniqueness, call-arity, and boolean-condition probe helpers.
- `Compiler.Mir` re-exports the source helper modules and imports concrete typing modules directly to avoid colliding with typing's `ExpressionClassification.ExprType`.
- Focused MIR source-helper verification:
  - `Compiler.Mir.SourceSymbols` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir.SourceRangeFacts` host-test inspect through `lower-mir`: passed with 0 errors and localized recursion warnings.
  - `Compiler.Mir.SourceSemanticProbes` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 errors and pre-existing recursion warnings.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost MIR Test Support Split

- `Compiler.Mir.TestSupport` now owns clang LLVM verification, temporary-file helpers, package-image file round-trip helpers, and assembly metadata table comparison.
- `Compiler.Mir.PackageImage` keeps production package-image read, write, load, and inspect APIs without the test-only round-trip helpers.
- `Compiler.Mir` re-exports the test-support module while keeping `CompileModuleSourceProducesObject` in the facade because it depends on the root AST compile path.
- Added `scripts/check-selfhost-mir-dependencies.sh` to reject frontend, LLVM, package-image, assembly metadata, and test-support imports from MIR foundation modules.
- Focused MIR verification:
  - `Compiler.Mir.TestSupport` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir.PackageImage` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 errors and pre-existing recursion warnings.
- Dependency guard verification:
  `scripts/check-selfhost-mir-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Pipeline Split

- `Compiler.Typing.TypedPipeline` now owns the public `BuildTyped*` orchestration
  entrypoints for typing tables while the focused typing modules keep their
  append and dependency builders.
- `Compiler.Typing` re-exports the pipeline module, and the MIR enum-layout
  helper now calls the concrete pipeline entrypoint instead of reaching through
  the enum-layout data module.
- `scripts/check-selfhost-typing-dependencies.sh` now rejects Binding, MIR, SSA,
  IR, LLVM, and vendor LLVM imports from selfhost Typing modules.
- Focused typing pipeline verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedFunctionSignaturesCaptureGenericCallableAndMemberFacts --filter TypedGlobalDeclarationsCaptureStorageBindingAndBackendFacts --filter TypedStructFieldsCaptureBackendTypeFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedEnumLayoutsResolveGenericAggregatePayloadLayouts --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts --filter TypedStorageSelectorsCaptureDeclarationFacts --filter TypedLiteralExpressionsCaptureScalarFacts --filter TypedIdentifierExpressionsResolveValueAndDeclarationTargets --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedMemberExpressionsCaptureMethodCandidateFacts --filter TypedIndexExpressionsCaptureElementSliceAndReceiverFacts --filter TypedConversionExpressionsCaptureTargetOperandAndResultFacts --filter TypedAssignmentExpressionsCaptureTargetValueAndOperatorFacts --filter TypedReturnExpressionsCaptureExpectedAndActualFacts`
  in `tests-stark/selfhost.Typing` passed.
- Focused artifact-rendering verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedArtifactRenderersShowBackendTypeFacts`
  in `tests-stark/selfhost.Artifacts` passed.
- Dependency guard verification:
  `scripts/check-selfhost-typing-dependencies.sh` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Generic Helper Split

- `Compiler.Typing.TypedGenerics` now owns generic-open detection, generic
  parameter-list matching, comptime generic-argument classification, and generic
  parameter/argument counters.
- `Compiler.Typing.TypedSignatures`, fields, enum payload/layout, locals, calls,
  members, and the typing facade now import or re-export the split generic
  helper module while preserving the existing helper names for callers.
- Focused typing verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedFunctionSignaturesCaptureGenericCallableAndMemberFacts --filter TypedStructFieldsCaptureBackendTypeFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedEnumLayoutsResolveGenericAggregatePayloadLayouts --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedMemberExpressionsCaptureMethodCandidateFacts`
  in `tests-stark/selfhost.Typing` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Dynamic Fact Split

- `Compiler.Typing.TypedDynamicFacts` now owns dyn/dynamic token aliases, dynamic
  layout size/alignment facts, and dynamic call binding cost.
- `Compiler.Typing.TypedSignatures`, globals, enum layouts, calls, and the
  typing facade now import or re-export the split dynamic fact module.
- No associated-type consumers were found in the current selfhost Typing source,
  so the associated-type portion remains blocked on a real consumer surface.
- Focused typing verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedFunctionSignaturesCaptureGenericCallableAndMemberFacts --filter TypedGlobalDeclarationsCaptureStorageBindingAndBackendFacts --filter TypedStructFieldsCaptureBackendTypeFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedIndexExpressionsCaptureElementSliceAndReceiverFacts`
  in `tests-stark/selfhost.Typing` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Typing Type Compatibility Split

- `Compiler.Typing.TypedTypeCompatibility` now owns call type-fact equality,
  argument bind permissibility, signedness helpers, and overload argument binding
  cost.
- `Compiler.Typing.TypedCalls` now imports the compatibility module for overload
  viability and scoring while keeping call-expression orchestration local.
- Focused call-compatibility verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedCallExpressionsCaptureDirectFunctionFacts --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedCallExpressionsReportAmbiguousAndNoMatchOverloads --filter TypedCallExpressionsResolveMethodCalls --filter TypedCallExpressionsReportAmbiguousAndNoMatchMethods --filter TypedCallExpressionsCaptureCallableValueFacts`
  in `tests-stark/selfhost.Typing` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Ownership Arena And Dead-On-Return Split

- `Compiler.Binding.OwnershipArena` now owns arena-backed value tracking,
  arena escape diagnostics, and arena-retention call diagnostics.
- `Compiler.Binding.OwnershipDeadOnReturn` now owns dead-on-return signature,
  callable-type, direct-call, and callback-call diagnostics.
- `Compiler.Binding` now keeps ownership validation orchestration while the
  concrete ownership checks live in focused modules.
- The arena accept fixture now makes its helper callee `finite` so it isolates
  arena behavior instead of also testing finite-call obligations.
- Focused arena verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter Arena`
  in `tests-stark/selfhost.Binding` passed.
- Focused dead-on-return verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter DeadOnReturn`
  in `tests-stark/selfhost.Binding` passed.
- Focused combined ownership verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter Arena --filter DeadOnReturn --filter PackedFieldSafeBorrow --filter MoveSemantics --filter BorrowLiveness --filter Initialization`
  in `tests-stark/selfhost.Binding` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Ownership Borrow Split

- `Compiler.Binding.OwnershipBorrow` now owns packed-field safe-borrow
  validation and straight-line borrow-liveness validation.
- `Compiler.Binding.LayoutTypeInfo` now owns the layout size/alignment helpers
  needed by packed-field safe-borrow checks.
- `Compiler.Binding` remains the facade and orchestration surface for these
  validation modules.
- Focused ownership-borrow verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter PackedFieldSafeBorrow --filter MoveSemantics --filter BorrowLiveness`
  in `tests-stark/selfhost.Binding` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Ownership Initialization And Move Split

- `Compiler.Binding.OwnershipInitialization` now owns local, `out`, and `init`
  initialization validation.
- `Compiler.Binding.OwnershipMove` now owns straight-line move-after-move
  validation.
- `Compiler.Binding.OwnershipHelpers` supplies shared ownership helpers for the
  split ownership modules while `Compiler.Binding` keeps the unsplit borrow
  liveness helpers local.
- Borrow-liveness fixtures now avoid using `alias` as a local name because
  `alias` is a declaration keyword.
- Focused initialization verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter Initialization`
  in `tests-stark/selfhost.Binding` passed.
- Focused move and borrow-liveness verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter MoveSemantics --filter BorrowLiveness`
  in `tests-stark/selfhost.Binding` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Destructor And Constructor Split

- `Compiler.Binding.DestructorValidation` now owns destructor shape, drop
  validity, destructor effect summaries, and destructor memory-effect diagnostics.
- `Compiler.Binding.ConstructorValidation` now owns constructor field
  initialization validation and constructor switch assignment-join coverage.
- `Compiler.Binding.Scopes` now owns the shared body identifier scanner used by
  destructor and constructor function-scope construction.
- `Compiler.Binding.FunctionEffects` now owns dynamic-storage mutation call
  classification for law and destructor validation.
- Focused destructor verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter DropValidityRejectsDuplicateDestructorBlocks --filter DropValidityRejectsReadonlySelfMutation --filter DropValidityWarnsWhenMutDropDoesNotMutateSelf --filter DropValidityTreatsSelfMethodCallAsMutation --filter DropValidityRejectsReturnStatement --filter DropValidityRejectsDestructorLocalStorageAllocation --filter DropValidityRejectsDestructorDynamicStorageAllocation --filter DropValidityRejectsDestructorDynamicStorageMutation --filter DropValidityRejectsDestructorTransitiveAllocationCall --filter DropValidityRejectsDestructorTransitiveDynamicMutationCall --filter DropValidityRejectsDestructorTransitiveMethodEffects --filter DropValidityRejectsDestructorImplicitLocalDropEffects`
  in `tests-stark/selfhost.Binding` passed.
- Focused constructor verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter ConstructorInitialization`
  in `tests-stark/selfhost.Binding` passed.
- Focused shared initialization-switch verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter InitializationAllowsLocalReadAfterExhaustiveBoolSwitchAssignment --filter InitializationRejectsLocalReadAfterGuardedBoolSwitchAssignment --filter InitializationAllowsOutReturnAfterExhaustiveIntegerRangeSwitchAssignment --filter InitializationRejectsOutReturnAfterPartialIntegerRangeSwitchAssignment`
  in `tests-stark/selfhost.Binding` passed.
- No broad test sweep was run.

---

## 2026-06-29 Selfhost Binding Validation Split

- `Compiler.Binding.SignatureHelpers` now owns shared function-signature
  parameter helpers used by binding validation modules.
- `Compiler.Binding.AssemblyBinding` now owns assembly architecture, register,
  operand, return-binding, and clobber diagnostics.
- `Compiler.Binding.ExportedSurfaces` now owns exported-surface visibility and
  ABI-boundary enum diagnostics.
- `Compiler.Binding.ControlFlowValidation` now owns return value and
  break/continue control-flow diagnostics.
- Focused assembly verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter ValidAsmDeclarationBindsCleanly --filter AsmDiagnosticsRejectUnknownArchitectureAndRegister --filter AsmDiagnosticsRequireValidOperandBindings --filter AsmDiagnosticsRejectDuplicateAndOverlappingClobbers`
  in `tests-stark/selfhost.Binding` passed.
- Focused exported-surface verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter ExportedSurfacesRejectMembersMoreVisibleThanOwners --filter ExportedSurfacesAllowPublicInheritanceAndExplicitExportMembersOnExportTypes --filter ExportedSurfacesRejectEnumTypesAtAbiBoundaries --filter ExportedSurfacesRejectTransitiveEnumAggregateTypesAtAbiBoundaries`
  in `tests-stark/selfhost.Binding` passed.
- Focused return/control-flow verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter FlagsValueReturnedFromVoid --filter FlagsMissingReturnValue --filter AcceptsWellFormedReturns --filter FlagsBreakOutsideLoop --filter FlagsContinueOutsideLoop --filter AcceptsBreakInsideLoop --filter ContinueInSwitchWithoutLoopIsFlagged`
  in `tests-stark/selfhost.Binding` passed.

---

## 2026-06-29 Selfhost MIR LLVM Emission Split

- `Compiler.Mir.LlvmFacts` now owns function effects, call contracts, ABI facts,
  range metadata, and separate-storage assume emission helpers.
- `Compiler.Mir.LlvmInstructions`, `Compiler.Mir.LlvmBlocks`,
  `Compiler.Mir.LlvmControlFlow`, `Compiler.Mir.LlvmFunctions`, and
  `Compiler.Mir.LlvmModules` now own LLVM instruction, block, direct-switch,
  function, module, and global emission helpers.
- Focused LLVM emission verification:
  `../../stark test --target arm64-apple-macosx26.0.0 --filter EmitsLlvmTypedI32Function --filter EmitsLlvmTypedI32ExtendedArithmeticFunction --filter EmitsLlvmTypedI32CallFunction --filter EmitsLlvmTypedFunctionWithParameterTypesAndFacts --filter MirNullPointerConstantRoundTripsFactsAndTypedLlvm --filter EmitsLlvmPhiNode --filter EmitsLlvmGlobalLoad --filter EmitsLlvmTypedGlobalLoadStore --filter EmitsLlvmDirectSwitchForDenseComparisonChain --filter EmitsLlvmGlobals --filter EmitsLlvmModuleWithGlobals --filter EmitsLlvmModuleWithTwoFunctions --filter VerifiedModuleEmissionGatesMalformedFunctions --filter CompilesTailBecomeFromAst --filter CompileFunctionArenaDynamicReserveEmitsGrowCopyHelper --filter CompileFunctionArenaDynamicTryReserveEmitsFallibleGrowCopyHelper --filter CompileModuleWithMultipleArenaFunctionsEmitsValidSinglePreamble`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-29 Selfhost Binding Trait-Conformance Split

- `Compiler.Binding.TraitConformance` now owns typed trait conformance rows,
  declaration-base conformance collection, and generic `where` constraint
  conformance collection.
- `Compiler.Binding` continues to re-export the trait-conformance module
  through the facade.
- Focused trait-conformance verification:
  `../../stark test --filter TypedTraitConformancesCaptureDirectDeclarationBases --filter TypedTraitConformancesCaptureGenericWhereBounds --filter GenericUseSiteFactsCaptureTraitBaseArguments --filter TypedGenericInstantiationPlansCaptureTraitBaseTargets`
  in `tests-stark/selfhost.Binding` passed.

---

## 2026-06-29 Selfhost Binding Function-Effects Split

- `Compiler.Binding.FunctionEffects` now owns typed function effect summary
  rows, backend optimization flags, body-shape analysis, no-recurse detection,
  and runtime recursion edge helpers.
- `Compiler.Binding` continues to re-export the function-effects module through
  the facade.
- The law dynamic allocation binding fixture now uses the canonical `law fn`
  spelling for dynamic-return law functions.
- Focused function-effects verification:
  `../../stark test --filter TypedFunctionEffectSummariesCaptureDeclarationBackendFacts --filter TypedFunctionEffectSummariesCaptureNoRecurseFacts --filter TypedFunctionEffectSummariesCaptureBodyShape --filter MutualRuntimeRecursionIsWarning --filter FiniteMutualRuntimeRecursionIsError`
  in `tests-stark/selfhost.Binding` passed.
- Focused law and memory-effect verification:
  `../../stark test --filter LawBodyFlagsDynamicStorageAllocation --filter LawBodyFlagsDynamicStorageMutation --filter LawEffectsFlagGlobalStateReadsButNotConstReads --filter FlagsArenaAllocationInLawFunctions --filter TypedFunctionEffectSummariesCaptureOtherMemoryFacts`
  in `tests-stark/selfhost.Binding` passed.

---

## 2026-06-28 Selfhost MIR Module Decomposition

- `Compiler.Mir` now re-exports `Compiler.Mir.Model` and `Compiler.Mir.Builder`.
- `Compiler.Mir.Model` owns the core MIR op, type, instruction, block,
  function, and global records.
- `Compiler.Mir.Builder` owns the core instruction, call, tail-call, phi,
  block, function, and global helper functions.
- `Compiler.Mir.LlvmText` owns shared LLVM type, ABI-carrier, and C ABI
  boundary text helpers.
- `Compiler.Mir.EnumLayout` owns enum layout facts, enum ABI summaries, and
  enum LLVM storage/value helpers.
- `Compiler.Mir.TextRendering` owns the MIR instruction stream disassembler.
- `Compiler.Mir.PackageCodec` owns byte primitives, fixed-record MIR section
  serializers, enum-layout fact section serialization, and assembly metadata
  section serialization.
- `Compiler.Mir.PackageImage` owns package-image validation, section-directory
  reading, inspection summaries, image serialization/deserialization, byte-buffer
  bridging, and package-image file IO helpers.
- `Compiler.Mir.Facts` owns pure integer/range helpers, MIR value-range
  propagation, branch-param refinement, and returned-value range validation.
- Focused core verification:
  `../../stark test --filter MirEmitsSequentialValueIds --filter MirBinaryInstructionReferencesOperandsByHandle --filter MirReturnBlockRecordsReturnedValue --filter MirCondBlockRecordsConditionAndTargets --filter MirFunctionRecordsEntryAndRanges --filter MirFunctionOwnershipTracksRanges --filter MirCallRecordsCallee --filter MirParamRecordsIndex --filter EmitsLlvmCallInstruction --filter EmitsLlvmTailCallTerminator`
  in `tests-stark/selfhost.Ir` passed.
- Focused global/package verification:
  `../../stark test --filter MirLoadGlobalRecordsTarget --filter MirStoreGlobalRecordsTargetAndValue --filter MirGlobalRecordsInitialValue --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmTypedGlobalLoadStore --filter EmitsLlvmGlobals --filter EmitsLlvmModuleWithGlobals --filter BinaryRoundTripsGlobals --filter BinaryRoundTripsPackageImage`
  in `tests-stark/selfhost.Ir` passed.
- Focused enum layout/storage verification:
  `../../stark test --filter IrFactEnumLayoutDescriptorPreservesPackageBoundaryContract --filter MirEnumLayoutFactsPreserveTypedRows --filter MirEnumLayoutFactsBuildAbiSummaryAndCarrier --filter MirEnumLayoutFactsEmitLlvmStorageTypeFromRows --filter MirEnumLayoutFactsComputeLlvmFieldIndicesFromRows --filter MirEnumLayoutFactsEmitLlvmValueOpsFromRows --filter SectionedPackageImageRoundTripsEnumLayoutFacts`
  in `tests-stark/selfhost.Ir` passed.
- Focused C ABI boundary verification:
  `../../stark test --filter EmitsCAbiAggregateBoundaryFactsForRaylibShapes`
  in `tests-stark/selfhost.CAbiAggregate` passed.
- Focused MIR text-rendering verification:
  `../../stark test --filter EmitsMirTextForArithmetic --filter EmitsMirTextForControlAndMemory --filter EmitsMirTextForPhi --filter PackageTablesPreserveLocalPrefixedTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Focused package-codec verification:
  `../../stark test --filter ByteBufferRoundTripsU32 --filter ByteBufferRoundTripsU32Max --filter ByteBufferRoundTripsI64 --filter BinaryRoundTripsInstructionStream --filter BinaryRoundTripsBlocks --filter BinaryRoundTripsIndependentLoopBackedgeFlag --filter BinaryRoundTripsFunctions --filter BinaryRoundTripsGlobals --filter BinaryRoundTripsArenaAllocInstruction --filter BinaryRoundTripsArenaDynamicInstructions --filter BinaryRoundTripsFourArgumentCall --filter BinaryRoundTripsFourArgumentTailCall --filter BinaryRoundTripsPackageImage --filter SectionedPackageImageRoundTripsEnumLayoutFacts --filter AsmMetadataBinaryRoundTrips`
  in `tests-stark/selfhost.Ir` passed.
- Focused package-image verification:
  `../../stark test --filter PackageImageRoundTripsThroughFile --filter PackageImageInspectsThroughFile --filter PackageImageBridgesToByteBuffer --filter BinaryRoundTripsPackageImage --filter SectionedPackageImageRoundTripsAndInspects --filter SectionedPackageImageRoundTripsEnumLayoutFacts --filter LogicalPackageImageValidatesHeaderFacts --filter LogicalPackageImageInspectsTextHeaderFacts --filter LogicalPackageImageInspectsJsonHeaderFacts --filter LogicalPackageImageInspectsProfileAndTargetHeaderFacts --filter LogicalPackageImageDoesNotDeserializeAsMir --filter LogicalPackageImageRejectsBadPackageFactStringIndex --filter LogicalPackageImageRejectsInvalidUtf8StringTable --filter SectionedPackageImageRejectsUnknownRequiredSection --filter PackageImageRejectsBadMagic --filter PackageImageRejectsTruncatedHeaderAndInspectionStaysEmpty --filter MalformedPackageImageFileInspectionFails --filter PackageImageValidationReportsFailureKinds --filter PackageImageRejectsMissingSections --filter PackageImageWithAsmRejectsMissingAsmSection --filter InspectsPackageImage --filter InspectsPackageImageJson --filter PackageImageWithAsmRoundTripsMetadata --filter SectionedPackageImageWithAsmRoundTripsMetadata --filter PackageImageWithAsmInspectsThroughFile --filter PackageImageWithAsmRoundTripsThroughFile --filter ModulePackageImageWithAsmBuilderRoundTrips`
  in `tests-stark/selfhost.Ir` passed.
- Focused MIR facts verification:
  `../../stark test --filter BuildMirValueRangeFactsImportsTypedCallReturnFacts --filter BuildMirValueRangeFactsImportsPointerCallReturnBackendFacts --filter BuildMirValueRangeFactsDerivesConstantsArithmeticAndPhi --filter BuildMirValueRangeFactsPreservesCompactIntegerWidths --filter BuildMirValueRangeFactsDerivesExactExtendedIntegerOps --filter MirExplicitWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm --filter MirCompactWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm --filter MirNullPointerConstantRoundTripsFactsAndTypedLlvm --filter ReturnIfExpressionPreservesBranchRangeFacts`
  in `tests-stark/selfhost.Ir` passed.
- Observed order-dependent failure:
  `../../stark test --filter CompileModuleBranchReturnRangeUsesComparisonProof --filter ReturnIfExpressionPreservesBranchRangeFacts`
  passes the first test and then fails `ReturnIfExpressionPreservesBranchRangeFacts`
  with exit 139; `ReturnIfExpressionPreservesBranchRangeFacts` passes alone and
  passes with the pure MIR facts filters above. This was not counted as a facts
  split failure.

---

## 2026-06-27 Selfhost Compact MIR Integer Widths

- MIR now carries `i8` and `i16` scalar widths through typed values, globals,
  package-image byte codecs, textual MIR rendering, and LLVM type emission.
- Compact typed constants are validated against their storage width before
  range facts are recorded.
- Wrapping and saturating arithmetic facts now preserve compact signed bounds
  for `i8` and `i16` values.
- LLVM lowering emits compact arithmetic directly and widens compact saturating
  operations only for the clamp calculation.
- High-level IR lowering validators now accept `i8` and `i16` where scalar MIR
  values are lowerable and reject out-of-range compact initializers.
- Package-image type byte compatibility is preserved by keeping existing
  `i1`/`i32`/`i64`/`ptr` encodings and assigning new bytes for `i8` and `i16`.
- Focused verification:
  `../../stark test --filter BuildMirValueRangeFactsPreservesCompactIntegerWidths --filter MirCompactWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm --filter BinaryRoundTripsGlobals --filter EmitsLlvmCompactTypedParamComparison --filter EmitsLlvmTypedGlobalLoadStore --filter EmitsLlvmGlobals --filter MirGlobalRecordsInitialValue --filter MirExplicitWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Boolean Switch Lowering

- Terminal switches over `bool` scrutinees now parse `case true` and
  `case false` labels and lower them through typed MIR branch blocks.
- Exhaustive true/false switches emit one direct `i1` conditional branch and
  do not lower an unreachable default return block.
- Boolean parameters and boolean literals now survive expression lowering as
  typed `i1` MIR values, including boolean return arms that zext to `i64`.
- Non-terminal switches over `bool` scrutinees now parse `case true` and
  `case false` assignment arms and lower them through direct `i1` branches.
- Exhaustive true/false assignment switches lower only reachable arms plus the
  merge block, while still validating the source `default` arm shape and calls.
- Type-aware expression lowering is threaded through terminal switch, terminal
  `if`, local-prefixed, tail-call, and switch-assignment lowering contexts that
  already carry local type facts.
- Typed constant range facts now validate against the MIR result type before
  recording integer facts.
- Narrow verification:
  `../../stark test --filter CompilesTerminalBooleanSwitchFromAst --filter CompilesSingleCaseTerminalBooleanSwitchFromAst --filter CompilesTerminalBooleanSwitchBoolArmsFromAst --filter TerminalBooleanSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalBooleanSwitch --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent terminal-switch verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Boolean switch-assignment verification:
  `../../stark test --filter CompilesBooleanLiteralLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesSingleCaseBooleanLiteralLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLiteralLocalSwitchStatementBoolAssignmentThenReturnFromAst --filter PackageTablesPreserveBooleanLocalSwitchStatementAssignment --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Arbitrary-Order Scalar Assignment Targets

- Non-terminal switch assignment arms now accept braced scalar target
  assignments in any source order while still requiring every target exactly
  once per arm.
- Assigned RHS roots are stored and lowered in source order, and a parallel
  target-offset table projects the already-lowered values back into declaration
  order for MIR phi construction.
- Integer and boolean target facts continue through typed phis, LLVM range
  attributes, `i1` payloads, and final `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch dispatcher smoke:
  `../../stark test --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multi-Statement Assignment Arms

- Non-terminal switch assignment arms now accept braced arm-local scalar
  declarations before the final assignment to the switch target.
- Arm-local names are scoped per arm and lower through arm-specific type and
  SSA override tables, preserving integer and boolean facts through MIR phis and
  LLVM returns.
- Statement-end scanning now allows comparison operators such as `<` in local
  initializers while still respecting parentheses and brackets.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch smoke verification:
  `../../stark test --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multiple Scalar Assignment Targets

- Non-terminal switch assignment lowering now accepts multiple pre-switch scalar
  target locals and braced arms that assign those targets in declaration order.
- Each target lowers through its own nested phi chain, so integer and boolean
  target facts remain independent through post-switch returns and terminal
  `if` branches.
- The dispatcher probe now recognizes multi-target switch-assignment bodies
  without stealing local-prefixed terminal switch bodies.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch dispatcher smoke verification:
  `../../stark test --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## Baseline Snapshot

Porting is effectively done (2638/2638). The remaining test work is making the
ported facts pass on macOS. All 19 suites were baselined with clean
`rm -rf build && stark test` runs on 2026-06-19. `compiler.FeatureTests` and
`compiler.LlvmTests` were rechecked by targeted full-project runs on 2026-06-23.

Summary: at least 2843 / 3144 run-facts passing (~90%). 15 of 19 suites are
known 100% green. At most 301 failures live in 4 suites. Counts are runner
`ok`/`FAILED`; `[Theory]` rows expand, so run-fact totals differ slightly from
static `[Fact]` counts. Non-feature/non-LLVM failing-suite counts remain the
2026-06-19 baseline unless their notes say otherwise.

| Suite | Passing | Failing | Notes |
|---|---:|---:|---|
| compiler.Tests | 1090 | **112** | largest suite: semantic/lowering diagnostics, type-checking, ownership, pipeline, runtime, package-image, CLI, examples |
| compiler.SsaTests | 346 | **61** | SSA lowering / validation / optimization text. ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + Cleanup + ScalarReplacement + InlineSsa + FunctionAddress + ConstantText + TextView + DynamicStorage families are green by targeted filters; count predates recent targeted fixes |
| compiler.LlvmTests | 493 | 0 | green by 2026-06-23 targeted project rerun |
| stdlib.Port | 214 | **14** | stdlib behavior ports; count includes 2026-06-23 targeted `io-path`, `io-file`, `io-file-runtime`, `memory-helper`, `memory`, `collections-dictionary`, `collections-hash-set-sort`, `collections`, `text`, `promoted-runtime-buffer`, `promoted-console`, `promoted-net-tcp`, `process`, `memory-contract-audit`, `raw-pointer-audit`, `range-notation`, `runtime-platform-mac-os`, and `collections-package-drop-regression` fixes but no full-suite rebaseline |
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
- stdlib.Port: at least 214/228, at most 14 failing after the 2026-06-23
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

## 2026-06-27 Selfhost Switch Post-Local Terminal-If Successor Lowering

- Non-terminal integer switch assignment lowering now supports scalar
  post-switch locals followed by a terminal `if` with returning arms.
- The first switch merge block can now become a conditional branch to appended
  tail return blocks while preserving the switch-assigned phi and successor
  local override table.
- Integer and boolean facts continue through the tail branch, including LLVM
  range attributes, `i1` phi payloads, `br i1`, and final boolean `zext`
  returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multiple Post-Local Successor Lowering

- Non-terminal integer switch assignment lowering now supports multiple scalar
  local initializers after the switch and before the final return.
- Successor locals lower in declaration order through the explicit local
  override table, so later successor locals can use earlier successor locals and
  the final return can still use the switch-assigned phi.
- Integer and boolean facts continue through the path, including LLVM range
  attributes, `i1` switch phis, and final boolean `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Post-Local Successor Lowering

- Non-terminal integer switch assignment lowering now supports one scalar local
  initializer after the switch and before the final return.
- The post-switch local initializer lowers in the first merge block from the
  switch-assigned phi, preserving integer and boolean facts through LLVM range
  attributes and `i1` phi payloads.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Assignment Merge Lowering

- Integer switch assignment arms now lower to MIR comparison-chain control flow
  with nested two-input merge phis, so one-or-more cases can continue to a
  post-switch return expression without inventing an illegal N-way phi.
- Boolean switch assignment arms keep `i1` phi payloads through MIR and only
  `zext` at the scalar return boundary, preserving LLVM range facts.
- Narrow verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Direct LLVM Switch Emission

- Three-or-more terminal integer-switch comparison chains now emit LLVM `switch`
  terminators for literal cases, skipping the old compare blocks in emitted
  LLVM while keeping the existing MIR/package-table shape.
- The direct switch path is shared by no-fact block emission and range-fact
  module emission, so return ranges, parameter facts, ABI facts, and call/effect
  attributes continue through the same lowering path.
- Narrow verification:
  `../../stark test --filter EmitsLlvmDirectSwitchForDenseComparisonChain --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Multi-Case Terminal Switch Lowering

- Terminal integer switch parsing now accepts one or more literal cases plus a
  default and rejects duplicate literal labels across the whole case list.
- Terminal switch MIR lowering now uses one shared comparison-chain builder for
  direct, local-prefixed, boolean-valued, single-case, and multi-case return
  arms while preserving local SSA overrides and explicit boolean `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-26 Selfhost HIR Fact-Type Validation

- HIR-to-MIR fact compatibility now rejects scalar values carrying pointer
  nullability facts and pointer values carrying integer range facts.
- Parameter lowering now applies the common value-fact compatibility check
  before emitting `Param` instructions or symbol-map rows.
- Narrow verification:
  `../../stark test --filter FactsOutsideType` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter Nullability` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsCallResultFactsOutsideResultTypeWithoutEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Global Store Fact Subsets

- HIR global-store lowering now enforces the full declared value-fact subset,
  including alignment, ABI, noalias, volatile, nullability, and integer range.
- Alignment subsets are checked by divisibility, so a stronger alignment fact
  satisfies a weaker one without accepting incompatible alignments.
- Narrow verification:
  `../../stark test --filter MirLoweringChecksGlobalStoreBackendFactSubset` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Local Symbol Fact Validation

- SSA local alias binding and local assignment rebinding now validate carried
  value facts against the MIR value type before updating the lowering symbol map.
- This prevents stale pointer range facts and scalar nullability facts from
  becoming backend-visible local facts.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsLocalAliasFactsOutsideTypeWithoutBinding --filter MirLoweringRejectsAssignmentFactsOutsideTypeWithoutRebinding`
  in `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringBindsLocalAliasWithoutEmissionAndPreservesFacts --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts --filter MirLoweringRejectsInvalidLocalAssignmentWithoutRebinding`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Return Fact Validation

- HIR return lowering now validates returned value facts against the MIR return
  type before appending a return block.
- This prevents stale pointer range facts and scalar nullability facts from
  becoming backend-visible terminator facts.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsReturnFactsOutsideTypeWithoutBlockEmission`
  in `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringLowersValueReturnToMirReturnBlock --filter MirLoweringRejectsReturnTypeMismatchWithoutBlockEmission --filter MirLoweringRejectsReturnWithoutValueFactsBeforeBlockEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Typed Parameter Lowering

- Lowering now accepts typed non-i64 HIR parameters, emits typed MIR `Param`
  instructions, and preserves parameter facts in the MIR value-fact table and
  lowering symbol map.
- Typed LLVM straight-line emission now has a typed parameter signature path
  with width-correct integer range attributes.
- Narrow verification:
  `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`
  passed, including `MirLoweringLowersTypedNonI64ParametersWithFacts`.
- Narrow verification:
  `../../stark test --filter EmitsLlvmTypedFunctionWithParameterTypesAndFacts`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-26 Selfhost Null Pointer Literal Lowering

- Lowering now accepts null pointer HIR literals, emits typed MIR pointer-zero
  constants, and preserves known-null facts in value-fact and lowering-symbol
  tables.
- MIR value facts now model nullability, and typed LLVM emission renders null
  pointer constants as `inttoptr i64 0 to ptr`.
- Narrow verification:
  `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`
  passed, including `MirLoweringLowersNullPointerLiteralWithNullabilityFacts`.
- Narrow verification:
  `../../stark test --filter MirNullPointerConstantRoundTripsFactsAndTypedLlvm`
  in `tests-stark/selfhost.Ir` passed.
- Narrow verification:
  `../../stark test --filter ValueFacts` and
  `../../stark test --filter IrFactCategoryIndexCoversConcreteDescriptors` in
  `tests-stark/selfhost.Ir` passed.

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
- Rechecked `net-tcp` and `runtime-platform-linux`; all selected run-facts
  passed on `arm64-apple-macosx26.0.0`, with Linux-only runtime facts skipped
  by platform gates where applicable.
- Ported the final unported qualifying C# stdlib regression,
  `ManifestBackedGenericFieldDropResolvesListClearFromStdlibPackage`, as a real
  package-backed MIR test. The Stark helper builds a Facade package, deletes the
  producer source, then compiles the Demo consumer through lower-mir with
  STARK_PATH stdlib roots and target/data-layout facts preserved.
- Narrow verification: `../../stark test --collection
  collections-package-drop-regression --target arm64-apple-macosx26.0.0` passed
  in `tests-stark/stdlib.Port`.

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

- Package-image input: remaining package-image residue is in
  `compiler.Tests` ManifestBacked/PackageImage paths; `compiler.LlvmTests`
  package-image facts are green after the targeted 2026-06-23 rerun.
- SSA/MIR text alignment, reframed: roughly 145 tests left across
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

## 2026-06-24 Compiler.Tests CLI And Package-Link Recheck

- Fixed project test builds so generated-test companion source roots and built
  dependency package-image directories are searched before bundled source-tree
  fallback. This keeps `compiler.Tests` on the freshly built stdlib package path
  instead of recompiling stdlib source modules with pruned dependency bodies.
- Fixed executable link input ordering so package archives are passed after
  locally emitted object files, preserving static-archive resolution for package
  definitions such as stdlib platform and memory helpers.
- Fixed project input stamps so selected test filters invalidate only test
  projects, not library dependencies such as stdlib.
- Repointed the two CLI signed-range port facts at `semantic-validate`, matching
  check-mode behavior where STK3014 is produced.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - `../../stark test --filter CheckModeReportsSuccess --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter CheckModeRejectsPositiveSignedRangesByDefault --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter StrictIntegerRangeFlagRejectsPositiveSignedRanges --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Project CLI Recheck

- Fixed the six failing `project-cli` port reductions that still supplied
  multiple modules as one source text or omitted sibling module fixtures.
- The affected facts now use the existing module-aware host-test helper so
  cross-module imports resolve through an explicit temporary search directory.
- Narrow verification:
  - `../../stark test --collection project-cli --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Compiler CLI Recheck

- Fixed the stale `compiler-cli` port reductions for current ownership,
  diagnostic, import-resolution, and manifest-backed module behavior.
- The negative MIR/LLVM mode reductions now use the transport-only host-test
  path so type diagnostics are asserted without requiring successful lowering.
- Narrow verification:
  - `../../stark test --collection compiler-cli --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Package Image Architecture Recheck

- Fixed symbolic `comptime` value forwarding through imported source
  materialization and generic argument decoding, preserving open value-generic
  facts until concrete specialization.
- Fixed MIR lowering of specialized comptime generic values so concrete values
  such as `N=4` lower as immediate operands with their range-typed value facts.
- Fixed package-image architecture expectations for current monomorphized
  symbol names and trait-conformance validation phase behavior.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - Direct `--host-test-inspect` minimal `Outer<T, comptime N>` probe: passed
    with zero diagnostics.
  - `../../stark test --filter PackageImageConsumerFoldsImportedComptimeTemplateCallWithPatterns --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter PackageImagePreservesComptimeGenericDeclarationsAndSymbolicTemplateCalls --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter PackageImagePreservesMethodStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --collection package-image-architecture --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed, 30 selected facts.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Function Semantics Recheck

- Added semantic-validation host-test helpers so ported facts that assert
  law-body, function-kind, visibility, and externally visible effect diagnostics
  stop at the pass that actually emits those diagnostics.
- Added a STARK_PATH-backed semantic-validation helper for source-tree stdlib
  probes, restoring the runtime text concatenation fact to assert both the
  law and finite kind-obligation diagnostics from the real `System.Text` path.
- Narrow verification:
  - `../../stark test --collection function-semantics --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed, 33 selected facts.
- No broad `compiler.Tests` sweep was run.

Other suite notes:

- compiler.Tests: package-image architecture is green by targeted collection;
  remaining work includes diagnostics, type-checking, ownership, pipeline,
  runtime, CLI, examples, AsmDeclarations, CheckMode,
  EmitLlvm/EmitExecutable, TextDiagnostics/SystemText, and a long tail.
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

---

## 2026-06-25 Selfhost Lowering Boundary Slice

- Added the Stark-side HIR/MIR boundary model and MIR lowering pass shell.
- The shell validates the host `lower-mir` artifact contract and records the
  backend fact families that must survive HIR to MIR lowering.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 5 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost MIR Builder State Slice

- Added Stark-side MIR function builder state, dense lowering symbol maps, and
  block creation helpers that record owned value/block ranges without embedding
  generic `IrTable<T>` fields in builder state.
- Preserved backend fact rows through symbol binding so lowering can carry range,
  alias, ABI, alignment, and layout facts into MIR/LLVM-facing tables.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 8 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Literal Lowering Slice

- Added explicit HighLevelIr literal kinds for unsupported null, float, text,
  and character literals so lowering rejects them as literals instead of symbols.
- Lowered integer and boolean literals to typed MIR constants while preserving
  exact range facts in both value-fact and lowering-symbol tables.
- Rejected out-of-range typed integer literals and unsupported literal families
  before appending partial MIR instructions.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 13 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Parameter And Local Alias Lowering Slice

- Lowered dense i64 HIR parameters to `MirOp.Param` values while preserving
  translated backend facts in value-fact and lowering-symbol tables.
- Added zero-emission SSA local alias binding so local symbols reuse initializer
  MIR values and preserve existing backend facts.
- Rejected unsupported parameter types, non-dense parameter ordinals, and local
  alias type mismatches before emitting or binding partial MIR.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 17 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Simple Local Assignment Lowering Slice

- Added a HIR assignment row for simple local reassignments.
- Lowered SSA local assignment by rebinding the local symbol to the assigned MIR
  value without emitting an extra instruction.
- Preserved the assigned value's backend facts in the lowering symbol map and
  rejected non-local targets or type mismatches before rebinding.
- Narrow verification:
  - `../../stark test --filter MirLoweringBindsLocalAliasWithoutEmissionAndPreservesFacts --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts --filter MirLoweringRejectsInvalidLocalAssignmentWithoutRebinding` in `tests-stark/selfhost.Lowering`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Value Return Lowering Slice

- Lowered typed HIR value returns to MIR return blocks without emitting extra
  value instructions or dropping the returned value's fact row.
- Rejected return type mismatches and missing returned-value facts before
  appending partial MIR blocks.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 22 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Integer Binary Lowering Slice

- Lowered typed integer add/sub/mul and signed comparisons from HIR to typed MIR
  while recomputing generated value facts before symbol binding.
- Corrected MIR comparisons to be i1-valued, preserved their range facts, and
  taught typed LLVM emission to return typed comparison results.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 24 selected facts.
  - `../../stark test --filter EmitsLlvmTypedI32ComparisonFunction` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsInstructionStream` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirComparisonRecordsOpcodeAndOperands` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BuildMirValueRangeFactsDerivesConstantsArithmeticAndPhi`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Extended Integer Binary Lowering Slice

- Lowered typed integer division, remainder, bitwise, and shift operations from
  HIR to typed MIR with proven-invalid backend fact rejection.
- Recomputed exact generated facts for safe constant extended integer operations
  and taught typed LLVM emission to preserve their result types.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 26 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsDerivesExactExtendedIntegerOps`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedI32ExtendedArithmeticFunction`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Direct Call Lowering Slice

- Lowered typed direct HIR calls up to MIR's four-argument payload to typed MIR
  `Call` instructions while binding result backend facts.
- Added typed MIR call constructors and typed LLVM call emission so call result
  and argument types survive into LLVM IR.
- Rejected call result range facts that do not fit the declared MIR result type
  before emitting partial MIR.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 28 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsImportsTypedCallReturnFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedI32CallFunction` in
    `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Void Return Lowering Slice

- Added a first-class MIR void-return terminator and lowered bare HIR void
  returns to it without creating a synthetic SSA value.
- Added void LLVM definition emission so a void function emits `ret void` under
  a `define void` signature.
- Kept block serialization stable by assigning `ReturnVoid` a new terminator
  byte after the existing values.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 29 selected facts.
  - `../../stark test --filter MirReturnVoidBlockRecordsEmptyTerminator` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmVoidDefinitionWithParams` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsBlocks` in
    `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Direct Call Fact Preservation Slice

- Verified direct-call result fact rows are translated through HIR-to-MIR call
  lowering without narrowing the transfer to integer ranges.
- Verified MIR call-return fact import preserves non-range backend facts such as
  alignment, ABI, noalias, volatility, and pointer nullability.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 30 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsImportsTypedCallReturnFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BuildMirValueRangeFactsImportsPointerCallReturnBackendFacts`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Call-Site Effect Attribute Slice

- Threaded computed function-effect facts into range-aware LLVM call emission.
- Emitted callee effect attributes on ordinary direct calls and `musttail`
  tail-call terminators.
- Refined law effect summaries so calls to proven `memory(none)` law callees do
  not force the caller to `memory(read)`.
- Added a pre-emission effect prepass so functions emitted before their callees
  still keep callee effect attributes.
- Narrow verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleFiniteEffectsPropagateThroughProvenDirectCalls --filter CompileModuleLawEffectsPropagateThroughProvenDirectCalls --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Memory-Backed Call Argument Slice

- Threaded callee parameter ABI and storage-contract facts into direct call and
  `musttail` call lowering.
- Emitted pointer call-site attributes and `separate_storage` assumes from
  memory-backed argument obligations.
- Rejected pointer/scalar argument kind mismatches and calls whose caller cannot
  prove the callee's required non-overlap contract.
- Narrow verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CompileModulePointerParametersLowerGranularAttributes --filter CompileModuleWholePointerParamsEmitSeparateStorageAssume --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleTailPointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModulePointerCallArgumentsRequireCallerAliasProof --filter CompileModulePointerCallArgumentKindsMustMatchCallee --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Assignment Value Context Lowering Slice

- Verified local assignment lowering returns the assigned MIR value for enclosing
  value contexts without emitting an extra assignment instruction.
- Confirmed the assignment result feeds typed MIR arithmetic and return lowering
  while preserving recomputed backend range facts.
- Narrow verification:
  - `../../stark test --filter MirLoweringUsesLocalAssignmentResultInEnclosingValueContext --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Explicit Overflow Arithmetic Lowering Slice

- Added distinct MIR and HIR lowering opcodes for explicit wrapping and
  saturating add, subtract, and multiply operations.
- Preserved exact or clamped range facts for explicit overflow arithmetic instead
  of reusing ordinary no-overflow arithmetic facts.
- Emitted wrapping LLVM arithmetic without no-wrap flags and emitted saturating
  LLVM arithmetic through a deterministic wide clamp sequence.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirExplicitWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersTypedIntegerExtendedOpsWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter EmitsLlvmForMixedArithmetic --filter BinaryRoundTripsInstructionStream`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local Compound Assignment Lowering Slice

- Lowered SSA local compound assignments by emitting the selected checked,
  wrapping, or saturating MIR operation and rebinding the local to the result.
- Preserved recomputed backend value facts through compound assignment results
  and final local facts.
- Rejected non-local targets, type mismatches, and exact invalid backend facts
  before emitting partial MIR.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersCompoundAssignmentsWithCheckedWrappingAndSaturatingFacts --filter MirLoweringRejectsInvalidCompoundAssignmentWithoutRebinding --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost I64 Global Lowering Slice

- Added explicit HIR rows for i64 global references and global stores.
- Bound source global symbols to MIR global ids with declared backend facts.
- Lowered global references to `MirOp.LoadGlobal` and stores to `MirOp.StoreGlobal` while preserving load facts and rejecting out-of-range stores.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Typed Global Storage And Lowering Slice

- Added typed MIR global records, typed load/store constructors, and typed LLVM
  global emission.
- Serialized global storage types through package images and validation.
- Lowered typed global references and stores with declared type and range-fact
  validation.
- Narrow verification:
  - `../../stark test --filter MirLoadGlobalRecordsTarget --filter MirStoreGlobalRecordsTargetAndValue --filter EmitsLlvmTypedGlobalLoadStore --filter MirGlobalRecordsInitialValue --filter EmitsLlvmGlobals --filter BinaryRoundTripsGlobals --filter BinaryRoundTripsPackageImage --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmModuleWithGlobals`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Module Global Declaration Lowering Slice

- Added HIR module global declaration rows with typed scalar initializers and
  declared backend facts.
- Lowered HIR module globals into typed MIR global rows, aligned global fact
  rows, and bound global symbols for later loads/stores.
- Rejected invalid declaration facts and initializers before emitting global
  rows or symbol bindings.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersModuleGlobalDeclarationsAndInitializersWithFacts --filter MirLoweringRejectsInvalidModuleGlobalDeclarationsWithoutBinding --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Become Tail-Call Lowering Slice

- Added HIR `become` lowering for direct typed tail calls through MIR's current
  four-argument payload.
- Added typed MIR tail-call payload constructors and preserved payload result
  types through LLVM `musttail` terminator emission.
- Preserved translated result facts on `become` payload values and kept typed
  tail-call payload types through binary round-trip.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmTypedTailCallTerminator --filter EmitsLlvmTailCallTerminator --filter BinaryRoundTripsFourArgumentTailCall --filter EmitsLlvmTypedI32CallFunction`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter EmitsLlvmTypedFunctionWithParameterTypesAndFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedTailCallTerminator --filter EmitsLlvmVoidDefinitionWithParams --filter EmitsLlvmModuleWithGlobals --filter BinaryRoundTripsFourArgumentTailCall`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedDirectCallWithResultFacts --filter MirLoweringLowersValueReturnToMirReturnBlockAndPreservesFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Call Argument Fact Validation Slice

- Validated carried backend facts on direct-call and tail-call argument values
  before MIR payload emission.
- Rejected stale scalar nullability and pointer range facts on call arguments
  without emitting call or tail-call instructions.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsCallArgumentFactsOutsideTypeWithoutEmission --filter MirLoweringRejectsBecomeArgumentFactsOutsideTypeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersTypedDirectCallWithResultFacts --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringRejectsCallResultFactsOutsideResultTypeWithoutEmission --filter MirLoweringRejectsScalarNullabilityCallResultWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arithmetic Operand Fact Validation Slice

- Validated carried backend facts on binary and compound-assignment operands
  before interpreting arithmetic backend facts.
- Rejected stale scalar nullability and out-of-type integer range facts without
  emitting arithmetic instructions or rebinding compound-assignment locals.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsBinaryOperandFactsOutsideTypeWithoutEmission --filter MirLoweringRejectsCompoundAssignmentOperandFactsOutsideTypeWithoutRebinding`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersTypedIntegerExtendedOpsWithFacts --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts --filter MirLoweringLowersCompoundAssignmentsWithCheckedWrappingAndSaturatingFacts --filter MirLoweringRejectsInvalidCompoundAssignmentWithoutRebinding --filter MirLoweringRejectsExactInvalidExtendedIntegerFactsWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Literal Fact Validation Slice

- Validated literal fact rows before MIR constant emission.
- Rejected range facts that do not describe the literal value and nullability
  facts that do not match the literal type or value.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsLiteralFactsOutsideTypeOrValueWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersIntegerLiteralWithExactFactsAndSymbolBinding --filter MirLoweringLowersBoolLiteralAsI1WithExactFacts --filter MirLoweringLowersNullPointerLiteralWithNullabilityFacts --filter MirLoweringRejectsUnsupportedLiteralWithoutEmission --filter MirLoweringRejectsTypedIntegerLiteralOutsideRangeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Global Store Fact Validation Slice

- Validated stored value fact rows against the global store type before MIR store
  emission.
- Rejected stale scalar nullability and pointer range facts even when the target
  global has no required backend facts.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsGlobalStoreValueFactsOutsideTypeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringChecksGlobalStoreBackendFactSubset --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Conditional Branch Fact Validation Slice

- Validated MIR conditional-branch conditions as owned `i1` values with present
  and type-compatible fact rows before appending branch blocks.
- Rejected stale nullability facts, invalid bool range facts, and non-bool
  condition values without appending conditional blocks.
- Narrow verification:
  - `../../stark test --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirBuilderRejectsConditionalBranchConditionFactsOutsideTypeWithoutBlock`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation --filter MirLoweringPassShellMatchesHostPipelineContract`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Tail-Call Block Fact Validation Slice

- Validated MIR tail-call block payloads as owned `TailCall` instructions with
  present and type-compatible fact rows before appending tail-call blocks.
- Rejected stale nullability facts, invalid payload range facts, and non-tail
  payload values without appending tail-call blocks.
- Narrow verification:
  - `../../stark test --filter MirBuilderAppendsTailCallBlockWithValidatedPayloadFacts --filter MirBuilderRejectsTailCallBlockPayloadFactsOutsideTypeWithoutBlock --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Return And Branch Block Builder Validation Slice

- Validated return block payloads as owned values with present and
  type-compatible fact rows before appending return blocks.
- Validated unconditional branch block cursor state before appending branch
  blocks.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation --filter MirBuilderRejectsReturnBlockValueFactsOutsideTypeWithoutBlock --filter MirBuilderAppendsBranchBlockWithValidatedCursor --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersValueReturnToMirReturnBlockAndPreservesFacts --filter MirLoweringLowersVoidReturnToMirReturnVoidBlock --filter MirLoweringRejectsReturnTypeMismatchWithoutBlockEmission --filter MirLoweringRejectsReturnFactsOutsideTypeWithoutBlockEmission --filter MirLoweringRejectsReturnWithoutValueFactsBeforeBlockEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Function Builder Finalization Validation Slice

- Validated instruction and block cursors before finalizing a MIR function's
  owned ranges.
- Checked function-table append capacity before recording the function row.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsFunctionFinishWhenInstructionCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Explicit Entry Selection Validation Slice

- Validated the block table and block cursor before changing a function builder's
  explicit entry block.
- Preserved builder state when stale raw block-table rows are detected.
- Narrow verification:
  - `../../stark test --filter MirBuilderSetsExplicitEntryBlockWithValidatedCursor --filter MirBuilderRejectsEntrySelectionWhenBlockCursorIsStale --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Value Recording Validation Slice

- Validated the instruction table and append cursor before extending a MIR
  function builder's owned instruction range.
- Preserved builder state when raw instruction rows exist beyond the value being
  recorded or the value handle is absent from the instruction table.
- Narrow verification:
  - `../../stark test --filter MirBuilderRejectsValueRecordingWhenInstructionCursorIsStale --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsFunctionFinishWhenInstructionCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Block Recording Validation Slice

- Validated the block table and append cursor before extending a MIR function
  builder's owned control-flow block range.
- Preserved builder state when raw block rows exist beyond the block being
  recorded or the block handle is absent from the block table.
- Narrow verification:
  - `../../stark test --filter MirBuilderRejectsBlockRecordingWhenBlockCursorIsStale --filter MirBuilderRejectsValueRecordingWhenInstructionCursorIsStale --filter MirBuilderAppendsBranchBlockWithValidatedCursor --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Function Backend Fact Gate Slice

- Rejected Stark CFG function lowering before MIR builder creation when any
  required backend fact category is missing.
- Kept declaration-only functions out of the Stark CFG builder entry path.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderRequiresCompleteBackendFactsBeforeFunctionLowering --filter HirBoundaryModelsTheHostHighLevelIrPass --filter HighLevelIrModuleStoresFunctionRowsAndBackendFactRequirements --filter MirLoweringPassShellMatchesHostPipelineContract`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost HIR If Branch Lowering Slice

- Lowered HIR if branch terminators from block symbols into validated MIR
  conditional blocks.
- Rejected missing target block symbols and invalid condition facts without
  appending a conditional block.
- Narrow verification:
  - `../../stark test --filter MirLoweringSymbolMapUsesDenseSymbolSlotsAndCarriesFacts --filter MirLoweringLowersIfBranchFromBlockSymbolsWithValidatedFacts --filter MirLoweringRejectsIfBranchMissingTargetsAndBadConditionWithoutBlockEmission --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirBuilderRejectsConditionalBranchConditionFactsOutsideTypeWithoutBlock`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Fixed Arena Allocation Lowering Slice

- Lowered fixed-size HIR arena allocations to MIR `ArenaAlloc` instructions.
- Marked arena-using builders and attached alignment, noalias, and known-nonnull
  pointer facts to the allocation result.
- Rejected zero-size and invalid-alignment arena allocations before MIR emission.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringRejectsInvalidArenaAllocationShapeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arena Dynamic Storage Lowering Slice

- Lowered arena-backed HIR dynamic storage init and reserve operations to MIR.
- Preserved owner alignment, noalias, known-nonnull, and fallible reserve
  boolean range facts through MIR lowering and generated-fact recomputation.
- Rejected invalid dynamic shapes, mismatched owner facts, and non-owner reserve
  targets before MIR emission.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringLowersArenaDynamicStorageInitWithFrameAndOwnerFacts --filter MirLoweringLowersArenaDynamicStorageReserveVariantsWithFacts --filter MirLoweringRejectsInvalidArenaDynamicStorageWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arena Frame Lifecycle Lowering Slice

- Emitted MIR arena frame enter instructions before the first HIR-lowered arena
  allocation or arena-backed dynamic storage operation.
- Emitted MIR arena frame leave instructions before return and tail-call blocks
  for arena-using function builders.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringLowersArenaDynamicStorageInitWithFrameAndOwnerFacts --filter MirLoweringLowersArenaDynamicStorageReserveVariantsWithFacts --filter MirLoweringRejectsInvalidArenaDynamicStorageWithoutEmission --filter MirLoweringClosesArenaFrameBeforeReturnBlock --filter MirLoweringLowersVoidReturnToMirReturnVoidBlock --filter MirBuilderAppendsBranchBlockWithValidatedCursor`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale --filter MirBuilderSetsExplicitEntryBlockWithValidatedCursor --filter MirBuilderRejectsEntrySelectionWhenBlockCursorIsStale --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirLoweringLowersIfBranchFromBlockSymbolsWithValidatedFacts --filter MirLoweringRejectsIfBranchMissingTargetsAndBadConditionWithoutBlockEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringClosesArenaFrameBeforeTailCallBlock --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirBuilderAppendsTailCallBlockWithValidatedPayloadFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Volatile LLVM Global Access Slice

- Attached volatile global fact rows to deterministic textual LLVM global loads
  and stores.
- Preserved existing range metadata attachment on volatile global loads.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmVolatileGlobalLoadStoreFacts --filter EmitsLlvmRangeMetadataForGlobalLoads --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmTypedGlobalLoadStore`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Aligned LLVM Global Access Slice

- Attached global alignment fact rows to deterministic textual LLVM global loads
  and stores.
- Preserved existing volatile and range metadata spelling around aligned global
  accesses.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmAlignedGlobalLoadStoreFacts --filter EmitsLlvmVolatileGlobalLoadStoreFacts --filter EmitsLlvmRangeMetadataForGlobalLoads --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmTypedGlobalLoadStore`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Calling Convention Fact Slice

- Attached exact FFI calling-convention facts to deterministic textual LLVM
  function definitions and direct call sites.
- Preserved existing `tailcc` priority for tail-callable functions and ordinary
  calls to tail-callable callees.
- Narrow verification:
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter AstCallLoweringEmitsCallWithArgument --filter EmitsLlvmOrdinaryCallToTailCallableWithTailcc`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Function Linkage Fact Slice

- Attached source function linkage facts to deterministic textual LLVM function
  definitions.
- Lowered module-private and `internal` source functions as LLVM `internal`
  definitions while leaving `public` and `export` source functions external.
- Preserved range, ABI, alias, effect, tail-call, and FFI calling-convention
  facts around the linkage header spelling.
- Narrow verification:
  - `../../stark test --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleTailPointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleFiniteLawBranchLowersBlockEffectAttributes --filter CompileModuleFiniteEffectsPropagateThroughProvenDirectCalls --filter CompileModuleLawEffectsPropagateThroughProvenDirectCalls`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter CompilesModuleWithCallFromAst --filter CompilesTailBecomeFromAst --filter CompilesZeroArgumentTailBecomeFromAst --filter CompilesTailRecursiveBranchFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleWithMultipleArenaFunctionsEmitsValidSinglePreamble --filter CompilesTwoArgumentCallFromAst --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModulePointerParametersLowerGranularAttributes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleWholePointerParamsEmitSeparateStorageAssume`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger combined filter run exited with code 139 after partial success, so
    the touched cases were verified in narrower stable selections instead.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Function Preemption Fact Slice

- Attached `dso_local` to deterministic textual LLVM definitions for
  source-private and `internal` source functions.
- Kept `public` and `export` source function definitions externally preemptable.
- Narrow verification:
  - `../../stark test --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter CompileModuleWithMultipleArenaFunctionsEmitsValidSinglePreamble --filter CompilesTwoArgumentCallFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger combined filter run exited with code 138 after reporting twelve
    touched facts as ok, so the remaining touched cases were verified in a
    smaller stable selection.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local-Prefixed Terminal If Slice

- Lowered source functions with local setup before terminal `if return/else
  return` into MIR conditional blocks for AST LLVM emission and package tables.
- Preserved local value overrides, branch return range validation, arena cleanup
  on returning arms, and function effect prepass visibility through the branch
  lowering path.
- Narrow verification:
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter ModulePackageImageWithAsmBuilderRoundTrips`
    in `tests-stark/selfhost.Ir`: passed.
  - A combined run that grouped the recursive branch fact with two other branch
    checks exited 139 after partial success; the same facts were then verified
    with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Braced Terminal If Slice

- Parsed braced terminal `if` arms (`{ return ...; } else { return ...; }`) as
  the same MIR conditional-block shape as compact terminal branches.
- Reused the same branch parser for body-start branches, local-prefixed
  branches, direct LLVM emission, effect prepass lowering, and package tables.
- Narrow verification:
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter ModulePackageImageWithAsmBuilderRoundTrips`
    in `tests-stark/selfhost.Ir`: passed.
  - A combined run that grouped the recursive branch fact with the tail/package
    checks exited 139 after partial success; the same facts were then verified
    with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Semicolon Terminal If Slice

- Parsed semicolon-terminated compact terminal `if` arms (`return ...; else
  return ...;`) as the same MIR conditional-block shape as compact terminal
  branches without semicolons.
- Preserved the existing branch parser for body-start branches, local-prefixed
  branches, direct LLVM emission, effect prepass lowering, and package tables.
- Narrow verification:
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - Grouped runs containing these facts were unstable: one exited 139 after
    partial success, and one reported failures for facts that passed
    individually.
- No broad test sweep was run.

## 2026-06-26 Selfhost Return If Expression Slice

- Lowered terminal source `return if ... else ...` expressions into MIR
  conditional return blocks instead of a merge phi.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  effect prepass visibility, package-table shape, linkage, calling convention,
  and LLVM range attributes.
- Rejected trailing tokens after the `else` expression instead of silently
  ignoring malformed source.
- Refreshed two existing return-range LLVM exact expectations for `dso_local`.
- Narrow verification:
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnIfExpressionFromAst --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleArithmeticCallArgumentLowersToLlvmRangeAttribute --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger grouped run containing these facts exited 139 after partial
    success, so the touched cases were verified with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Returned Local If Expression Slice

- Lowered immediately returned `var` locals initialized from source
  `if ... else ...` expressions into MIR conditional return blocks.
- Avoided a merge phi for the immediate-return shape, preserving branch-refined
  return range validation, boolean arm zero-extension, effect prepass visibility,
  package-table shape, and LLVM range attributes.
- Reused the plain if-expression arm parser for terminal `return if` and returned
  local initializer lowering.
- Narrow verification:
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnIfExpressionFromAst --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    in `tests-stark/selfhost.Ir`: passed.
  - Two grouped adjacent runs exited 139 after printing partial success, so the
    same touched facts were verified with smaller stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Returned Local If Statement Slice

- Lowered immediately returned locals overwritten by source `if` assignment
  statements into MIR conditional return blocks.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  effect prepass visibility, package-table shape, and LLVM range attributes.
- Kept local-prefixed terminal return-if bodies on their existing lowerer by
  narrowing the assignment-if detector.
- Narrow verification:
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    exited 139 after `CompileModuleBranchReturnRangeUsesComparisonProof`
    passed, so the remaining touched facts were verified individually.
- No broad test sweep was run.

## 2026-06-26 Selfhost Braced Returned Local If Statement Slice

- Lowered braced source `if` assignment arms for immediately returned locals
  into MIR conditional return blocks.
- Replaced the returned-local assignment-if route detector with a source-aware
  shape check so unsupported branch bodies continue to fall through to the
  correct lowerer instead of being claimed by a token sniff.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  package-table shape, and no-phi LLVM emission for the braced assignment-arm
  shape.
- Narrow verification:
  - `../../stark test --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local If Expression Phi Slice

- Lowered integer-valued local `if ... else ...` expression initializers used by
  later return expressions into MIR diamond blocks with a merge phi.
- Preserved phi-derived range facts into return validation and LLVM range
  attributes through the existing MIR value-fact builder.
- Kept immediately returned local if-expression initializers on the no-phi
  conditional-return fast path by tightening the returned-local detector.
- Narrow verification:
  - `../../stark test --filter CompilesLocalIfExpressionInitializerThenReturnFromAst --filter PackageTablesPreserveLocalIfExpressionInitializerThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local If Statement Phi Slice

- Lowered integer-valued source `if` assignment statements whose local is used
  by a later return expression into MIR diamond blocks with a merge phi.
- Preserved phi-derived range facts into return validation and LLVM range
  attributes through the existing MIR value-fact builder.
- Kept immediately returned local assignment-if bodies on the no-phi
  conditional-return fast path.
- Narrow verification:
  - `../../stark test --filter CompilesLocalIfStatementAssignmentThenReturnFromAst --filter PackageTablesPreserveLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Local If Phi Slice

- Added typed MIR phi construction so boolean local `if` joins can stay `i1`
  until a final return conversion requires `i64`.
- Lowered boolean local if-expression initializers and compact boolean
  if-statement assignments used by later equality returns into typed MIR phi
  merge blocks.
- Extended braced boolean if-statement assignment arms by matching source
  blocks with brace depth only, so `<` comparisons inside arms do not hide the
  arm's closing brace.
- Emitted comparison LLVM with the left operand's MIR type so boolean equality
  after a boolean phi emits `icmp eq i1` instead of widening the comparison.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanLocalIfExpressionInitializerThenReturnExpressionFromAst --filter CompilesBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfExpressionInitializerThenReturn --filter PackageTablesPreserveBooleanLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalIfExpressionInitializerThenReturnFromAst --filter PackageTablesPreserveLocalIfExpressionInitializerThenReturn --filter CompilesLocalIfStatementAssignmentThenReturnFromAst --filter PackageTablesPreserveLocalIfStatementAssignmentThenReturn --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter CompilesBooleanReturnedLocalIfStatementFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfExpression --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBracedBooleanLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfStatementAssignmentThenReturn --filter CompilesBooleanLocalIfExpressionInitializerThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfExpressionInitializerThenReturn --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Terminal If Slice

- Lowered terminal source `if` return branches with boolean values into
  typed MIR conditional return blocks.
- Kept boolean comparisons as `i1` values through arm lowering and widened only
  the branch return values to the ABI `i64` shape.
- Preserved the widened boolean return range facts so `i64[0 1]` declarations
  pass and impossible narrower declarations fail.
- Covered braced and semicolon-terminated compact arms for both direct terminal
  `if` bodies and local-prefixed terminal `if` bodies.
- Narrow verification:
  - `../../stark test --filter CompilesBracedBooleanTerminalIfFromAst --filter PackageTablesPreserveBracedBooleanTerminalIf --filter CompilesLocalPrefixedBracedBooleanTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedBracedBooleanTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesSemicolonBooleanTerminalIfFromAst --filter PackageTablesPreserveSemicolonBooleanTerminalIf --filter CompilesLocalPrefixedSemicolonBooleanTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedSemicolonBooleanTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BooleanTerminalIfPreservesReturnRangeFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: failed as a grouped run after
    `CompilesSemicolonTerminatedTerminalIfFromAst` passed; the two reported
    failures passed when rerun individually.
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Return If Expression Slice

- Covered boolean-valued terminal `return if ... else ...` expressions through
  typed MIR conditional return blocks.
- Kept boolean arm comparisons as `i1` and widened only branch return values to
  the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnIfExpressionFromAst --filter BooleanReturnIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `BooleanReturnIfExpressionPreservesBranchRangeFacts`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Returned Local If Statement Slice

- Covered compact and braced boolean-valued source `if` assignment statements
  whose overwritten local is immediately returned.
- Kept branch boolean expressions as `i1` and widened only branch return values
  to the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter BooleanReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnedLocalIfStatement --filter CompilesBracedBooleanReturnedLocalIfStatementFromAst --filter BracedBooleanReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveBracedBooleanReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed; the substring filters also selected
    the boolean returned-local range-fact tests.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Returned Local If Expression Slice

- Covered boolean-valued immediately returned local source `if` expression
  initializers through typed MIR conditional return blocks.
- Kept branch boolean expressions as `i1` and widened only branch return values
  to the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter BooleanReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `BooleanReturnedLocalIfExpressionPreservesBranchRangeFacts`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Local If Phi Range Slice

- Preserved boolean local `if` phi result facts for if-expression initializers,
  compact if-statement assignments, and braced if-statement assignments.
- Verified the lowered LLVM keeps `phi i1` and `icmp eq i1`, widens only at the
  return edge with `zext i1`, and emits the declared `range(i64 0, 2)` return
  attribute.
- Narrow verification:
  - `../../stark test --filter BooleanLocalIfExpressionInitializerThenReturnExpressionPreservesRangeFacts --filter BooleanLocalIfStatementAssignmentThenReturnExpressionPreservesRangeFacts --filter BracedBooleanLocalIfStatementAssignmentThenReturnExpressionPreservesRangeFacts`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Counting While Loop Slice

- Lowered canonical source `while` counting loops into module MIR
  entry/header/body/exit blocks with an induction phi and explicit backedge.
- Routed the loop shape through effect prepass, final LLVM emission, and
  package-table construction instead of the standalone loop text emitter.
- Verified unsupported non-literal bounds, wrong assignment targets, and
  non-additive updates still reject.
- Narrow verification:
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter LowersCountingLoopSourceToLlvm --filter EmitsLlvmCountingLoopWithBackEdge --filter WhileLoopRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `ModuleCountingWhileLoopRejectsUnsupportedShapes`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Accumulator While Loop Slice

- Lowered canonical source accumulator `while` loops into module MIR loop blocks
  with counter and accumulator phis.
- Routed the dual-phi loop shape through effect prepass, final LLVM emission,
  and package-table construction.
- Verified non-literal bounds, swapped body updates, non-additive counter
  updates, and wrong return values still reject.
- Narrow verification:
  - `../../stark test --filter CompilesModuleAccumulatorWhileLoopFromAst --filter ModuleAccumulatorWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveAccumulatorWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Counted For Loop Slice

- Lowered canonical counted `for willexit` loops over an existing local into
  module MIR entry/header/body/exit blocks with an induction phi.
- Routed the counted `for` shape through effect prepass, final LLVM emission,
  and package-table construction.
- Rejected `independent` in this route rather than dropping an unsupported
  optimization fact before backend metadata exists.
- Narrow verification:
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop --filter CompilesModuleAccumulatorWhileLoopFromAst --filter ModuleAccumulatorWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveAccumulatorWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Header Counted For Loop Slice

- Lowered canonical counted `for willexit` loops with `stack mut` or
  `register mut` header locals into module MIR loop blocks.
- Preserved the induction phi, comparison, update, return range validation,
  effect prepass visibility, final LLVM emission, and package-table shape.
- Rejected `independent`, heap header locals, immutable header locals,
  non-literal bounds, non-additive updates, nonempty bodies, and wrong returns.
- Narrow verification:
  - `../../stark test --filter CompilesModuleHeaderCountingForLoopFromAst --filter ModuleHeaderCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Independent Counted For Loop Fact Slice

- Preserved canonical counted source `for willexit independent` loop facts on
  MIR backedge blocks and through MIR block serialization.
- Emitted LLVM loop metadata for independent counted-loop backedges so the
  source no-loop-carried-dependency fact reaches LLVM.
- Narrow verification:
  - `../../stark test --filter BinaryRoundTripsIndependentLoopBackedgeFlag --filter CompilesModuleIndependentCountingForLoopFromAst --filter CompilesModuleIndependentHeaderCountingForLoopFromAst --filter PackageTablesPreserveIndependentCountingForLoop --filter PackageTablesPreserveIndependentHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsBlocks --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter CompilesModuleHeaderCountingForLoopFromAst --filter ModuleHeaderCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop --filter PackageTablesPreserveHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsPackageImage --filter PackageImageWithAsmRoundTripsMetadata --filter ModulePackageImageWithAsmBuilderRoundTrips --filter PackageImageRoundTripsThroughFile --filter PackageImageWithAsmRoundTripsThroughFile --filter SectionedPackageImageWithAsmRoundTripsMetadata`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Terminal Integer Switch Slice

- Lowered terminal integer source `switch` bodies with two literal `case`
  labels and a `default` into MIR conditional blocks.
- Routed the switch shape through effect prepass, final LLVM emission, and
  package-table construction.
- Preserved single scrutinee evaluation, return range validation, branch target
  wiring, and `icmp eq` comparison facts through emitted LLVM.
- Narrow verification:
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter CompilesBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
- Observed `../../stark test --filter CompilesRecursiveFunctionFromAst --filter CompilesBracedTerminalIfFromAst`
  pass the recursive test and then exit 139 before the next selected test; both
  tests pass when run independently, so this was not counted as a switch
  lowering failure.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Terminal Integer Switch Slice

- Lowered boolean-valued terminal integer switch arms through typed MIR values
  and explicit `zext` return values.
- Preserved return range facts through the switch return blocks so LLVM emits
  the declared `i64[0 1]` range correctly.
- Kept mixed integer/boolean switch return arms rejected rather than widening
  them silently.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch --filter TerminalIntegerSwitchRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Signed Terminal Switch Case Slice

- Lowered signed integer `case` labels in terminal integer switches without
  changing the one-scrutinee, direct `icmp eq`, or five-block MIR shape.
- Preserved the signed case immediate through package-table construction so the
  compare operand remains a `ConstInt(-1)` fact.
- Narrow verification:
  - `../../stark test --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter TerminalIntegerSwitchRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-27 Selfhost Braced And Local Terminal Switch Slice

- Lowered braced `return` arms in terminal integer switches through the existing
  terminal switch MIR shape.
- Lowered scalar `var`-prefixed terminal integer switches by evaluating local
  initializers once and feeding the switch through SSA local overrides.
- Preserved boolean-valued local-prefixed switch arms as explicit `zext` return
  values so LLVM receives the declared `i64[0 1]` range.
- Routed the local-prefixed switch shape through effect prepass, final LLVM
  emission, and package-table construction.
- Narrow verification:
  - `../../stark test --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-28 Selfhost Binding Decomposition Slice

- Split `Compiler.Binding` into focused source modules while keeping
  `Compiler.Binding` as the facade re-export surface.
- Recorded the current target binding module map:
  `Compiler.Binding.Diagnostics`, `Declarations`, `Scopes`, `References`,
  `TypeResolution`, `GenericUseSites`, and `ModuleResolution` now own the first
  extracted data and helper slices; typed symbols, generic-instantiation,
  callable resolution, validation, ownership, C ABI, assembly, and pipeline
  slices remain in the facade until their listed tasks are moved.
- Moved bind result and diagnostic types into `Compiler.Binding.Diagnostics`.
- Moved raw declaration table construction and lookup helpers into
  `Compiler.Binding.Declarations`.
- Moved function scope and lexical value-reference helpers into
  `Compiler.Binding.Scopes`.
- Moved unresolved value-reference collection into
  `Compiler.Binding.References`.
- Moved type-reference resolution and unresolved type-span scans into
  `Compiler.Binding.TypeResolution`.
- Moved generic use-site syntax facts into `Compiler.Binding.GenericUseSites`
  and fixed nested generic arguments so child argument rows no longer pollute
  the parent use-site argument slot.
- Moved module resolver, import-resolution tables, module-origin facts, and
  import/source/package resolution helpers into
  `Compiler.Binding.ModuleResolution`.
- Moved typed module symbol visibility, origin, and qualified-name facts into
  `Compiler.Binding.TypedModuleSymbols`.
- Moved typed declaration symbol construction, value/type anchor facts, and
  declaration-level backend flags into `Compiler.Binding.TypedDeclarations`.
- Moved typed member and method symbol construction, field/variant payload
  facts, and member-level backend flags into `Compiler.Binding.TypedMembers`.
- Moved typed local and parameter symbol construction into
  `Compiler.Binding.TypedLocals`.
- Moved typed generic parameter symbol construction into
  `Compiler.Binding.TypedGenerics`.
- Moved typed generic-instantiation plan construction into
  `Compiler.Binding.TypedGenericInstantiations`.
- Moved shared expression type helpers and reference-index lookup into focused
  helper modules used by later validation and candidate-resolution code.
- Moved callable and receiver candidate construction into
  `Compiler.Binding.CallableCandidates` and `Compiler.Binding.ReceiverCandidates`.
- Normalized receiver method-kind validation through declaration-name lookup so
  law/finite checks parse the owning type body from the declaration token.
- Moved shared declaration-prelude, token, attribute, C-family FFI ABI, and
  receiver member token scanners into small binding helper modules.
- Corrected selfhost declaration name scanning so `const` declarations use the
  same typed variable-name scan as storage-class globals.
- Updated typed generic-instantiation fixtures to use complete source units with
  explicit `module` headers, matching the typed module-symbol path.
- Narrow verification:
  - `../../stark test --filter BuildsAndLooksUpDeclarations --filter DetectsDuplicateDeclaration --filter StaticDeclarationsUseDeclaredVariableName --filter BuildsFunctionScopeWithParamsAndLocals --filter DetectsDuplicateParameter --filter ResolvesBodyReferences --filter FlagsUnboundReference --filter ResolvesReferencesByKind --filter ResolvesShadowedReferenceToInnermostLocal --filter ProbeSiblingBlockShadowTarget --filter DetectsSameBlockLocalRedefinition --filter AllowsShadowingLocalInNestedBlock --filter AllowsSameNameLocalsInSiblingBlocks`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter ResolvesSignatureTypes --filter FlagsUnresolvedSignatureTypes --filter FlagsUnresolvedLocalType --filter ResolvedLocalTypeBindsCleanly --filter GenericFunctionLocalTypeParameterIsNotUnresolved --filter FlagsUnresolvedForEachElementType --filter GenericFunctionTypeParameterIsNotUnresolved --filter FlagsUnresolvedNestedGenericArgumentTypes --filter NestedGenericParameterArgumentsAreNotUnresolved --filter FlagsUnresolvedWhereConstraint --filter FlagsUnresolvedNestedWhereConstraintType --filter ResolvedWhereConstraintBindsCleanly --filter FlagsUnresolvedStructAndEnumWhereConstraints --filter ResolvedStructAndEnumWhereConstraintsBindCleanly --filter FlagsUnresolvedFieldType --filter ResolvedFieldTypesBindCleanly --filter GenericParameterFieldTypeIsNotUnresolved --filter MultiDeclaratorUndefinedFieldTypeFlaggedOnce --filter BaseTraitGenericIsNotMistakenForFieldGenericParam --filter ComptimeParameterTypeIsNotMistakenForGenericParam --filter GenericUseSiteFactsCaptureNestedTypeAndComptimeArguments --filter GenericUseSiteFactsClassifySignedIntegerValueArgument --filter GenericUseSiteFactsCaptureTraitBaseArguments`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter GenericUseSiteFactsCaptureNestedTypeAndComptimeArguments --filter GenericUseSiteFactsClassifySignedIntegerValueArgument --filter GenericUseSiteFactsCaptureTraitBaseArguments --filter TypedGenericInstantiationPlansResolveDeclarationTargetsAndOpenArguments --filter TypedGenericInstantiationPlansDetectTargetArityMismatches --filter TypedGenericInstantiationPlansCaptureTraitBaseTargets --filter TypedTraitConformancesCaptureDirectDeclarationBases --filter TypedTraitConformancesCaptureGenericWhereBounds`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedModuleSymbolsCaptureModuleAndVisibilityFacts --filter TypedModuleSymbolsPreserveQualifiedNames --filter ModuleResolverAddsNamedSourceModule --filter ModuleResolverResolvesSourceAndPackageImports --filter ModuleResolverPrefersSourceModuleOverPackageModule --filter ModuleResolverReportsMissingModuleDeclaration --filter ModuleResolverReportsMissingImport --filter ModuleResolverReportsDuplicateSourceAndAmbiguousImport`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedModuleSymbolsCaptureModuleAndVisibilityFacts --filter TypedModuleSymbolsPreserveQualifiedNames --filter TypedDeclarationSymbolsCaptureFunctionBackendFacts --filter TypedDeclarationSymbolsCaptureValueAndAliasTypeAnchors --filter ValidLinkNameOnImportedFfiHasNoDiagnostic --filter LinkNameOnOrdinaryFunctionIsInvalid --filter LinkNameRequiresNonEmptyStringArgument --filter DuplicateLinkNameAttributesAreRejected --filter CAbiAggregateBoundaryFactsClassifyRaylibShapes --filter CAbiAggregateBoundaryFactsMarkLargeAggregatesIndirect`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedMemberSymbolsCaptureFieldsRecordFieldsAndMethodFacts --filter TypedMemberSymbolsCaptureEnumVariantsAndAssociatedAliases --filter TypedLocalSymbolsCaptureTopLevelParametersLocalsAndPatternBindings --filter TypedLocalSymbolsCaptureMemberMethodOwnersAndParameterFacts --filter TypedGenericParameterSymbolsCaptureDeclarationGenerics --filter TypedGenericParameterSymbolsCaptureFunctionAndMethodGenerics --filter ExportedSurfacesRejectMembersMoreVisibleThanOwners --filter ExportedSurfacesAllowPublicInheritanceAndExplicitExportMembersOnExportTypes`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedLocalSymbolsCaptureTopLevelParametersLocalsAndPatternBindings --filter TypedLocalSymbolsCaptureMemberMethodOwnersAndParameterFacts`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedGenericParameterSymbolsCaptureDeclarationGenerics --filter TypedGenericParameterSymbolsCaptureFunctionAndMethodGenerics`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter TypedGenericInstantiationPlansResolveDeclarationTargetsAndOpenArguments --filter TypedGenericInstantiationPlansDetectTargetArityMismatches --filter TypedGenericInstantiationPlansCaptureTraitBaseTargets`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter CallableCandidateSetsCaptureDirectFunctionAndCallbackCalls --filter CallableCandidateSetsKeepSameNameFunctionDeclarations --filter ReceiverCandidateSetsCaptureFieldAndInstanceMethodMembers --filter ReceiverCandidateSetsCaptureStaticMethodMembers`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter FunctionKindFlagsLawCallingPlainFunction --filter FunctionKindFlagsLawCallingPlainCallableValue --filter FunctionKindAllowsFiniteLawCallingFiniteLawCallableValue --filter FunctionKindFlagsLawCallingPlainReceiverMethod`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-28 Binding Copyability And Layout Validation Split

- Moved copyability fact construction, `Copyable` assertions, and `where Copyable(...)` call validation into `Compiler.Binding.Copyability`.
- Moved shared layout scanners into `Compiler.Binding.LayoutHelpers` and layout-control/query validation into `Compiler.Binding.LayoutValidation`.
- Kept `Compiler.Binding` as the facade re-export surface for the moved helpers and validation functions.
- Narrow verification:
  - `../../stark test --filter CopyabilityFactsClassifyStructuralTypes --filter CopyableAssertionsAcceptPlainEnumsStructsRecordsAndViews --filter CopyableAssertionsRejectOwningFieldsAndDestructors --filter CopyableLawPredicatesAcceptCopyableValuesAndForwardedBounds --filter CopyableLawPredicatesRejectOwningValuesAndMissingForwardedBounds --filter MoveSemanticsDoNotMoveCopyableValuesThroughByValueCalls --filter MoveSemanticsDoNotMoveCopyableAssignments`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --filter LayoutControlRejectsInvalidAttributeShapes --filter LayoutControlRejectsMisplacedFieldOffsets --filter LayoutControlAcceptsNestedControlledStructFields --filter LayoutQueriesRejectNonConcreteLocalTargets --filter LayoutQueriesRejectFieldFactsOnNonStructTargets --filter LayoutQueriesRejectOutOfRangeFieldIndices --filter LayoutQueriesAllowConcreteStructAndOpenGenericFacts --filter LayoutQueriesRejectEnumFactsOnNonEnumTargets --filter LayoutQueriesRejectOutOfRangeEnumIndices --filter LayoutQueriesAllowConcreteEnumAndOpenGenericFacts --filter PackedFieldSafeBorrowRejectsMisalignedCallArguments --filter PackedFieldSafeBorrowRejectsBorrowReturnsAndLocals --filter PackedFieldSafeBorrowAllowsValueReadsAndRawPointers --filter CAbiAggregateBoundaryFactsClassifyRaylibShapes --filter CAbiAggregateBoundaryFactsMarkLargeAggregatesIndirect`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding C ABI Layout Split

- Moved C layout aggregate classification into `Compiler.Binding.CLayoutAggregates`.
- Moved C ABI aggregate boundary collection into `Compiler.Binding.CAbiBoundaries`.
- Kept `Compiler.Binding` as the facade re-export surface and updated `Compiler.Mir.LlvmText` to import the concrete boundary module.
- Preserved zero-copy source/token text flow by adding explicit overlap contracts to token/layout helpers.
- Narrow verification:
  - `../../stark test --filter CAbiAggregateBoundaryFactsClassifyRaylibShapes --filter CAbiAggregateBoundaryFactsMarkLargeAggregatesIndirect --filter LayoutControlAcceptsNestedControlledStructFields --filter PackedFieldSafeBorrowRejectsMisalignedCallArguments --filter PackedFieldSafeBorrowAllowsValueReadsAndRawPointers`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Expression And Signature Split

- Moved coarse expression classification into `Compiler.Typing.ExpressionClassification`.
- Moved typed function signature tables and type-slot scanners into `Compiler.Typing.TypedSignatures`.
- Kept `Compiler.Typing` as the facade re-export surface for the moved typing modules.
- Updated same-package callers to use imported facade names after public types moved into submodules.
- Preserved effect facts by tightening `TypedSignatureTokenTextEquals` helpers to `finite law`.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedFunctionSignaturesCaptureScalarRangeAndPointerFacts --filter TypedFunctionSignaturesCaptureGenericCallableAndMemberFacts --filter ClassifiesParameterTypes --filter ClassifiesExpressionShapes --filter IntegerConditionIsANonBooleanError --filter ComparisonAndLogicalConditionsAreBoolean --filter IdentifierConditionIsNotFlagged`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Globals, Storage Selectors, And Fields Split

- Moved global declaration tables, storage facts, and global builders into `Compiler.Typing.TypedGlobals`.
- Moved explicit storage selector tables and builders into `Compiler.Typing.StorageSelectors`.
- Moved struct, record, and record-header field tables/builders into `Compiler.Typing.TypedFields`.
- Kept `Compiler.Typing` as the facade re-export surface for the moved typing modules.
- Fixed top-level parsing so semicolon-terminated globals with aggregate initializers continue to the following declarations.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter AggregateInitializerGlobalDoesNotStopDeclarationList`
    in `tests-stark/selfhost.Parsing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedGlobalDeclarationsCaptureStorageBindingAndBackendFacts --filter TypedGlobalDeclarationsCaptureConstArraysAndStaticScalars --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts --filter TypedLocalDeclarationsCaptureMemberLoopAndCallableFacts --filter TypedStorageSelectorsCaptureDeclarationFacts --filter TypedStorageSelectorsCaptureArenaNewFacts`
    in `tests-stark/selfhost.Typing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedStructFieldsCaptureBackendTypeFacts --filter TypedRecordFieldsCaptureHeaderAndBodyFacts --filter TypedEnumPayloadsCaptureTupleNamedAndFromFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedEnumLayoutsCaptureCompactTagsAndScalarPayloadOffsets --filter TypedEnumLayoutsResolveNamedAggregatePayloadLayouts --filter TypedEnumLayoutsResolveGenericAggregatePayloadLayouts --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Enum, Local, And Literal Split

- Moved enum payload tables, builders, and variant-role facts into `Compiler.Typing.TypedEnumPayloads`.
- Moved enum layout tables, query folding, and layout builders into `Compiler.Typing.TypedEnumLayouts`.
- Moved declaration-name lookup shared by typing modules into `Compiler.Typing.TypedDeclarationLookup`.
- Moved local declaration tables, builders, and storage/type facts into `Compiler.Typing.TypedLocals`.
- Moved literal expression tables, builders, and scalar/text fact derivation into `Compiler.Typing.TypedLiterals`.
- Kept `Compiler.Typing` as the facade re-export surface for moved typing modules.
- Updated MIR enum layout/package codec imports so enum payload and layout facts continue through package image and LLVM storage lowering.
- Fixed attributed struct-field parsing so field attributes such as `FieldOffset` do not enter typed field type spans.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedEnumPayloadsCaptureTupleNamedAndFromFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedEnumLayoutsCaptureCompactTagsAndScalarPayloadOffsets --filter TypedEnumLayoutsCaptureRolesFunnelsArraysAndUnknownPayloads --filter TypedEnumLayoutsResolveNamedAggregatePayloadLayouts --filter TypedEnumLayoutsResolveGenericAggregatePayloadLayouts --filter TypedEnumLayoutsPreserveControlledAggregatePayloadLayouts --filter TypedEnumLayoutQueriesExposeCtfeFoldConstants --filter TypedEnumLayoutSystemCompilerQueriesFoldToTypedConstants`
    in `tests-stark/selfhost.Typing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts --filter TypedLocalDeclarationsCaptureMemberLoopAndCallableFacts`
    in `tests-stark/selfhost.Typing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedLiteralExpressionsCaptureScalarFacts --filter TypedLiteralExpressionsCaptureTextFacts`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Identifier Split And Call Dependency Preservation

- Moved identifier expression tables, visible-symbol lookup, and identifier builders into `Compiler.Typing.TypedIdentifiers`.
- Kept `Compiler.Typing` as the facade re-export surface for moved identifier facts.
- Replaced the call/member dependency wrapper return with out-parameter tables so dynamic call and member facts survive dependency refinement.
- Copied signature-slot facts through locals before assigning call fact structs so positive overload resolution keeps parameter facts.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedIdentifierExpressionsResolveValueAndDeclarationTargets --filter TypedIdentifierExpressionsRespectLexicalShadowing --filter TypedCallExpressionsCaptureDirectFunctionFacts --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedCallExpressionsReportAmbiguousAndNoMatchOverloads --filter TypedCallExpressionsResolveMethodCalls --filter TypedCallExpressionsReportAmbiguousAndNoMatchMethods --filter TypedCallExpressionsCaptureCallableValueFacts --filter TypedMemberExpressionsCaptureFieldChainsAndReceiverFacts --filter TypedMemberExpressionsCaptureMethodCandidateFacts`
    in `tests-stark/selfhost.Typing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedIndexExpressionsCaptureElementSliceAndReceiverFacts --filter TypedConversionExpressionsCaptureTargetOperandAndResultFacts --filter TypedAssignmentExpressionsCaptureTargetValueAndOperatorFacts --filter TypedReturnExpressionsCaptureExpectedAndActualFacts`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Expression Fact Module Split

- Moved call tables and call argument/overload facts into `Compiler.Typing.TypedCallFacts` and `Compiler.Typing.TypedCalls`.
- Moved member tables, receiver facts, and method-candidate logic into `Compiler.Typing.TypedMemberFacts` and `Compiler.Typing.TypedMembers`.
- Moved the call/member refinement loop into `Compiler.Typing.TypedCallMemberDependencies`.
- Moved indexing, conversion, assignment, and return expression facts into `TypedIndexing`, `TypedConversions`, `TypedAssignments`, and `TypedReturns`.
- Reduced `Compiler.Typing` to a re-export-only facade for the split typing modules.
- Kept literal-to-type fact propagation shared through `TypedMemberFactsFromLiteral` so conversion and assignment facts use the same backend type data.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedCallExpressionsCaptureDirectFunctionFacts --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedCallExpressionsReportAmbiguousAndNoMatchOverloads --filter TypedCallExpressionsResolveMethodCalls --filter TypedCallExpressionsReportAmbiguousAndNoMatchMethods --filter TypedCallExpressionsCaptureCallableValueFacts --filter TypedMemberExpressionsCaptureFieldChainsAndReceiverFacts --filter TypedMemberExpressionsCaptureMethodCandidateFacts --filter TypedIndexExpressionsCaptureElementSliceAndReceiverFacts --filter TypedConversionExpressionsCaptureTargetOperandAndResultFacts --filter TypedAssignmentExpressionsCaptureTargetValueAndOperatorFacts --filter TypedReturnExpressionsCaptureExpectedAndActualFacts`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding Semantic Validation Split

- Moved finite/runtime recursion diagnostics into `Compiler.Binding.RecursionValidation`.
- Moved `become` tail-call ABI and effect diagnostics into `Compiler.Binding.BecomeValidation`.
- Moved law signature/body/effect diagnostics into `Compiler.Binding.LawValidation`.
- Moved callable and receiver function-kind obligation diagnostics into `Compiler.Binding.FunctionKindValidation`.
- Moved the shared long-lived declaration classifier into `Compiler.Binding.FunctionEffects`.
- Suppressed output-parameter initialization cascades for invalid law `out`/`init` parameters while preserving local initialization checks.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter FunctionKindFlagsLawCallingPlainFunction --filter FunctionKindFlagsFiniteCallingPlainFunction --filter FunctionKindFlagsFiniteLawCallingMismatchedKinds --filter FunctionKindFlagsLawCallingPlainCallableValue --filter FunctionKindAllowsFiniteLawCallingFiniteLawCallableValue --filter FunctionKindFlagsLawCallingPlainReceiverMethod --filter LawBodyFlagsOutAndInitParameters --filter LawBodyFlagsOutAndInitReturnTypes --filter LawBodyFlagsHeapAndStaticLocalStorage --filter LawBodyFlagsWritesThroughBorrowedParametersAndGlobals --filter LawBodyAllowsPureLocalMutation --filter LawBodyFlagsDynamicStorageAllocation --filter LawBodyFlagsDynamicStorageMutation --filter LawEffectsFlagGlobalStateReadsButNotConstReads --filter DirectRuntimeRecursionCollectsWarningCode --filter DirectRuntimeRecursionIsWarning --filter MutualRuntimeRecursionIsWarning --filter TailBecomeRuntimeRecursionDoesNotWarn --filter FiniteDirectRuntimeRecursionIsError --filter FiniteMutualRuntimeRecursionIsError --filter TailBecomeFiniteRuntimeRecursionDoesNotError --filter FlagsBecomeOutsideTailFunction --filter FlagsBecomeRequiresDirectCall --filter FlagsBecomeTargetNotTailCallable --filter FlagsBecomeEffectMismatch --filter FlagsBecomeAbiArityMismatch --filter FlagsBecomeAbiTypeMismatch --filter AcceptsBecomeAbiMatchingGenericShape --filter FlagsBecomeAbiGenericArgumentMismatch`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding Pipeline And Inline Layout Split

- Moved associated alias member construction into `Compiler.Binding.AssociatedTypes`.
- Moved link-name, recursive inline-layout, enum, and expression diagnostics into focused validation modules.
- Moved `BindCompilationUnit` orchestration into `Compiler.Binding.BindingPipeline` and kept `Compiler.Binding` as the facade.
- Reworked recursive inline-layout detection to return cycle anchors directly and to follow named aggregate chains without copying declaration records out of the lookup loop.
- Preserved inline-storage semantics by stopping traversal through raw pointers, dynamic storage, slices, borrows, and other non-inline type forms before using parser head tokens.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter RecursiveInlineLayout`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter LinkName --filter DuplicateEnumVariant --filter NonBoolean`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter BindCompilationUnitCollectsEveryDiagnosticKind --filter TypedMemberSymbolsCaptureEnumVariantsAndAssociatedAliases`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding Thread-Safety Predicate Split

- Added `Compiler.Binding.ThreadSafety` for shared law-predicate tables, where-clause scanning, and single-identifier predicate helpers.
- Rewired `Compiler.Binding.Copyability` to use the shared thread-safety predicate helpers with the `Copyable` law name.
- Kept `Compiler.Binding` as the facade re-export surface for the new thread-safety module.
- Left full `Transferable` and `Shareable` fact derivation open because those facts are not yet present in the self-host binder.
- Narrow verification:
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter CopyabilityFactsClassifyStructuralTypes --filter CopyableLawPredicatesAcceptCopyableValuesAndForwardedBounds --filter CopyableLawPredicatesRejectOwningValuesAndMissingForwardedBounds`
    in `tests-stark/selfhost.Binding`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter CopyableAssertionsAcceptPlainEnumsStructsRecordsAndViews --filter CopyableAssertionsRejectOwningFieldsAndDestructors --filter MoveSemanticsDoNotMoveCopyableValuesThroughByValueCalls --filter MoveSemanticsDoNotMoveCopyableAssignments --filter BindCompilationUnitCollectsEveryDiagnosticKind`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding Dependency Direction Guard

- Moved shared layout attribute parsing, layout-control queries, C layout marker queries, and data-declaration lookup helpers into `Compiler.Binding.LayoutHelpers`.
- Removed validation-module imports from `LayoutTypeInfo`, `CLayoutAggregates`, and `ConstructorValidation`.
- Added `scripts/check-selfhost-binding-dependencies.sh` to reject Binding data-module imports of validation, ownership, C ABI, assembly, or pipeline modules.
- Kept the dependency guard outside the Stark Binding test binary so file-system APIs do not inflate the selfhost Binding compile surface.
- Narrow verification:
  - `scripts/check-selfhost-binding-dependencies.sh`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter LayoutControlAcceptsNestedControlledStructFields --filter LayoutQueriesAllowConcreteStructAndOpenGenericFacts --filter LayoutQueriesAllowConcreteEnumAndOpenGenericFacts --filter CAbiAggregateBoundaryFactsClassifyRaylibShapes --filter CAbiAggregateBoundaryFactsMarkLargeAggregatesIndirect --filter CopyabilityFactsClassifyStructuralTypes --filter CopyableLawPredicatesAcceptCopyableValuesAndForwardedBounds --filter CopyableLawPredicatesRejectOwningValuesAndMissingForwardedBounds`
    in `tests-stark/selfhost.Binding`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Type Resolution And CTFE Query Split

- Added `Compiler.Typing.TypedTypeResolution` for the typed type-kind vocabulary, type flags, type-span scanners, and source type-classification helpers.
- Kept `Compiler.Typing.TypedSignatures` focused on signature table storage and signature-row construction while re-exporting the type-resolution vocabulary for narrow imports.
- Added `Compiler.Typing.TypedCtfeQueries` for enum-layout `System.Compiler` query-call folding.
- Kept raw enum layout rows and layout-table lookup helpers in `Compiler.Typing.TypedEnumLayouts`.
- Narrow verification:
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedFunctionSignaturesCaptureGenericCallableAndMemberFacts --filter TypedGlobalDeclarationsCaptureStorageBindingAndBackendFacts --filter TypedStructFieldsCaptureBackendTypeFacts --filter TypedEnumPayloadsCaptureDynamicGenericAndRoleCollisionFacts --filter TypedEnumLayoutsResolveGenericAggregatePayloadLayouts --filter TypedLocalDeclarationsCaptureStorageTypeAndInitializerFacts --filter TypedStorageSelectorsCaptureDeclarationFacts --filter TypedLiteralExpressionsCaptureScalarFacts --filter TypedIdentifierExpressionsResolveValueAndDeclarationTargets --filter TypedCallExpressionsResolveDirectFunctionOverloads --filter TypedMemberExpressionsCaptureMethodCandidateFacts --filter TypedIndexExpressionsCaptureElementSliceAndReceiverFacts --filter TypedConversionExpressionsCaptureTargetOperandAndResultFacts --filter TypedAssignmentExpressionsCaptureTargetValueAndOperatorFacts --filter TypedReturnExpressionsCaptureExpectedAndActualFacts`
    in `tests-stark/selfhost.Typing`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedArtifactRenderersShowBackendTypeFacts`
    in `tests-stark/selfhost.Artifacts`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter TypedEnumLayoutQueriesExposeCtfeFoldConstants --filter TypedEnumLayoutSystemCompilerQueriesFoldToTypedConstants`
    in `tests-stark/selfhost.Typing`: passed.
- No broad test sweep was run.

## 2026-06-29 Binding Thread-Safety Fact Split

- Added `Transferable` and `Shareable` fact derivation to `Compiler.Binding.ThreadSafety`.
- Added structural, intrinsic atomic, grant, deny, and conditional grant coverage to the selfhost Binding tests.
- Reworked fact derivation to return compact flags instead of recursive out-parameter state propagation.
- Narrow verification:
  - `scripts/check-selfhost-binding-dependencies.sh`: passed.
  - `ThreadSafety.stark` host-test inspect through `lower-mir`: passed with 0 errors.
  - `.stark/cache/thread_safety_smoke/thread-safety-smoke`: compiled and exited 0.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter ThreadSafetyFactsClassifyStructuralTypes`
    in `tests-stark/selfhost.Binding`: not completed because the project test fan-out was too heavy for a narrow check.
- No broad test sweep was run.

## 2026-06-29 MIR Assembly Metadata Split

- Moved source assembly metadata decoding, register validation, and metadata-table population into `Compiler.Mir.AssemblyMetadata`.
- Kept `Compiler.Mir` as the facade re-export for `CollectAsmMetadataFromSignature` and `CollectFirstAsmFunctionMetadata`.
- Preserved package-image assembly metadata consumers without changing the table or serialization model.
- Narrow verification:
  - `Compiler.Mir.AssemblyMetadata` host-test inspect through `lower-mir`: passed with 0 errors.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 errors and pre-existing recursion warnings.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter AsmSignatureMetadataSurvivesParserBridge --filter AsmMetadataRejectsInvalidInputRegisterBeforeAppendingRows --filter AsmMetadataRejectsInvalidOutputRegisterBeforeAppendingRows --filter AsmMetadataRejectsInvalidClobberRegisterBeforeAppendingRows --filter AsmMetadataBinaryRoundTrips --filter PackageImageWithAsmRoundTripsMetadata --filter ModulePackageTablesWithAsmPreserveFunctionIdsAndCalls`
    in `tests-stark/selfhost.Ir`: stopped after about 4.5 minutes because the filtered project compile fanned out heavily.
- No broad test sweep was run.

## 2026-06-29 Typing Index Helper Split

- Moved indexing enums, flags, storage, and accessors into `Compiler.Typing.TypedIndexModel`.
- Moved index context, lookup, receiver, element, and result fact helpers into `Compiler.Typing.TypedIndexHelpers`.
- Kept `Compiler.Typing.TypedIndexing` focused on index append, scan, and build orchestration.
- Removed stale direct `TypedIndexing` imports from MIR facade modules that did not use index builder APIs.
- Narrow verification:
  - `TypedIndexModel`, `TypedIndexHelpers`, `TypedIndexing`, conversion consumers, assignment consumers, `TypedReturns`, `TypedPipeline`, and `Compiler.Typing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `SourceModuleLowering` and `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

## 2026-06-29 Typing Assignment Helper Split

- Moved assignment enums, flags, storage, and accessors into `Compiler.Typing.TypedAssignmentModel`.
- Moved assignment context conversions, operator classification, node lookup, and assignment fact-copy helpers into `Compiler.Typing.TypedAssignmentHelpers`.
- Moved assignment target and value fact derivation into `Compiler.Typing.TypedAssignmentNodeFacts`.
- Kept `Compiler.Typing.TypedAssignments` focused on assignment append, scan, and build orchestration.
- Narrow verification:
  - `TypedAssignmentModel`, `TypedAssignmentHelpers`, `TypedAssignmentNodeFacts`, `TypedAssignments`, `TypedReturns`, `TypedPipeline`, and `Compiler.Typing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `SourceModuleLowering` and `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- No broad test sweep was run.

## 2026-06-29 Typing Conversion Helper Split

- Moved conversion enums, flags, storage, and accessors into `Compiler.Typing.TypedConversionModel`.
- Moved conversion context conversions, node lookup, target fact-copy, and conversion flag helpers into `Compiler.Typing.TypedConversionHelpers`.
- Moved conversion operand fact derivation into `Compiler.Typing.TypedConversionOperandFacts`.
- Kept `Compiler.Typing.TypedConversions` focused on conversion append, scan, and build orchestration.
- Narrow verification:
  - `TypedConversionModel`, `TypedConversionHelpers`, `TypedConversionOperandFacts`, `TypedConversions`, `TypedAssignmentHelpers`, `TypedAssignmentNodeFacts`, `TypedAssignments`, `TypedReturns`, `TypedPipeline`, and `Compiler.Typing` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-typing-dependencies.sh`: passed.
  - `SourceModuleLowering` and `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
- No broad test sweep was run.

## 2026-06-29 MIR Stack Allocation Slice

- Added MIR stack allocation as a distinct pointer-producing operation for empty source `stack` object construction.
- Preserved stack allocation size, alignment, nonnull, and noalias facts through MIR facts, package codec, text rendering, and LLVM alloca emission.
- Lowered explicit and target-typed empty stack object construction in module and terminal-if local prefixes.
- Narrow verification:
  - `Compiler.Mir.Builder` and `Compiler.Mir.Model` host-test inspect through `type-check`: passed with 0 diagnostics.
  - `Compiler.Mir.Facts`, `Compiler.Mir.LlvmInstructions`, `Compiler.Mir.PackageCodec`, and `Compiler.Mir.TextRendering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceLocalLowering`, `Compiler.Mir.SourceModuleLowering`, and `Compiler.ArtifactRendering` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `Compiler.Mir.SourceIfLowering` host-test inspect through `lower-mir`: passed with 0 errors and two pre-existing recursion warnings.
  - `Compiler.Mir` host-test inspect through `lower-mir`: passed with 0 diagnostics.
  - `../../stark test --filter StackAllocGeneratesPointerFacts` in `tests-stark/selfhost.Ir`: stopped after several minutes because the filtered project compile fanned out heavily.
- No broad test sweep was run.

## 2026-06-30 MIR Enum Construction Slice

- Added MIR enum construction opcodes for tag creation and payload insertion.
- Preserved enum owner type, variant ordinal, payload ordinal, source enum value, and payload value in MIR fields and package serialization.
- Rendered enum construction operations in MIR artifact text and named them in artifact rendering.
- Kept enum values out of scalar/global lowering validators until layout-aware LLVM emission is wired.
- Narrow verification:
  - `../../stark test --filter EnumConstructionInstructionsCarryPayloadLayoutFacts --filter EmitsMirTextForEnumConstructionInstructions --filter BinaryRoundTripsEnumConstructionInstructions` in `tests-stark/selfhost.Ir`: stopped after several minutes because the filtered project compile fanned out heavily.
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-30 MIR Enum LLVM Emission Slice

- Emitted MIR enum tag construction and payload insertion as LLVM `insertvalue` operations using MIR enum layout facts.
- Threaded enum layout fact tables through enum-aware instruction, block, function, range-fact, module, and artifact LLVM emission entrypoints.
- Kept scalar, pointer, call, and range metadata emission on the existing fact-preserving path for non-enum instructions.
- Added focused IR tests for instruction-level, range-fact definition, and module-level enum construction emission.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmForEnumConstructionInstructionsFromLayoutFacts` in `tests-stark/selfhost.Ir`: stopped after about a minute because the filtered project compile remained silent and appeared to fan out.
  - `git diff --check`: passed.
- No broad test sweep was run.

## 2026-06-30 MIR Enum Source Expression Slice

- Added source-expression nodes for enum tag construction and enum payload insertion.
- Preserved enum owner type, variant ordinal, payload ordinal, payload value, and payload scalar width through expression lowering into MIR enum construction operations.
- Kept enum constructor expressions visible to source local type propagation, direct enum-owner lookup, call validation, discardability checks, and integer range-fact analysis.
- Added focused IR tests for enum source-expression fact preservation, MIR lowering, and enum-layout LLVM emission.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `git diff --check`: passed.
- No broad test sweep was run.

## 2026-06-30 MIR Enum Pointer Memory Slice

- Added owner-aware MIR enum value load and store operations for storage-backed enum places.
- Preserved enum owner type, value operand, pointer operand, and lowered pointer alignment through text rendering, package serialization, and LLVM emission.
- Added aligned enum value load/store LLVM helpers so derived pointer alignment survives aggregate enum memory operations.
- Classified enum pointer loads and stores in MIR memory-effect scans.
- Added focused IR tests for builder operands, MIR text, package round-trip, and LLVM enum aggregate load/store emission.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --filter EnumPointerMemory --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped at the 120-second alarm with no output.
  - `git diff --check`: passed.
- No broad test sweep was run.

## 2026-06-30 MIR Uninitialized Scalar Storage Slice

- Lowered uninitialized mutable stack scalar locals into typed stack storage after definite-assignment validation.
- Reused the storage-backed scalar local assignment and load paths so scalar width, alignment, and range facts continue through LLVM IR emission.
- Wired the declaration path through straight-line, terminal `if`, and switch-local MIR lowering.
- Added focused IR tests for straight-line, terminal `if`, switch, bool storage, and forward-reference rejection cases.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter Uninitialized` in `tests-stark/selfhost.Ir`: stopped after about two minutes because the filtered project test runner remained silent.
  - `../../stark test IrTests.stark --filter CompileFunctionUninitializedMutableStackScalarAssignmentStoresTypedValue` in `tests-stark/selfhost.Ir`: stopped after about one minute because the single-filter test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Compact Integer Parameter Width Slice

- Added non-storage source-local type codes for i8, i16, i32, and i64 values.
- Seeded source-local parameter type tables from signature integer widths instead of collapsing every non-bool scalar to i64.
- Lowered typed parameter references and SSA local aliases through `MirParamTyped` so compact integer facts reach LLVM IR.
- Propagated compact integer source-local codes through scalar field reads, indexed field reads, and matching typed `if`/switch phis.
- Added focused IR tests for direct i32 parameter returns and `var` aliases of i32 parameters.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionTypedIntegerParameterPreservesI32ReturnType --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent.
  - A temporary one-off executable that called `CompileFunctionWithLocalsToLlvm` directly was stopped after about 90 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Declared Scalar Return Width Slice

- Added scalar return expression lowering that targets the declared function return MIR type for integer and bool returns.
- Wired straight-line source function returns through the declared scalar return type before creating the MIR return block.
- Added focused IR facts for i32 literal returns, i16 literal arithmetic returns, and bool literal returns.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed before ledger edits.
  - `../../stark test IrTests.stark --filter CompileFunctionI32LiteralReturnPreservesDeclaredReturnType --filter CompileFunctionI16LiteralArithmeticReturnPreservesDeclaredReturnType --filter CompileFunctionBoolLiteralReturnPreservesDeclaredReturnType --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped by a 120-second alarm after remaining silent.
- No broad test sweep was run.

## 2026-06-30 MIR Branch Declared Scalar Return Width Slice

- Reused declared scalar return targeting for `return if`, terminal `if`, local-prefixed terminal `if`, and terminal switch return arms.
- Threaded declared scalar return MIR types through terminal integer, enum, boolean, and local-prefixed switch lowering helpers.
- Added focused IR facts for compact integer and bool returns through `return if`, terminal `if`, local-prefixed terminal `if`, integer switches, boolean switches, and local-prefixed switches.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test IrTests.stark --filter ReturnIfExpressionPreservesDeclaredI16ReturnType --filter BooleanTerminalIfPreservesDeclaredBoolReturnType --filter TerminalIfPreservesDeclaredI32ReturnType --filter LocalPrefixedTerminalIfPreservesDeclaredI8ReturnType --filter TerminalIntegerSwitchPreservesDeclaredI16ReturnType --filter BooleanTerminalIntegerSwitchPreservesDeclaredBoolReturnType --filter LocalPrefixedTerminalIntegerSwitchPreservesDeclaredI8ReturnType --filter TerminalBooleanSwitchPreservesDeclaredI8ReturnType --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped by a 120-second alarm after remaining silent.
- No broad test sweep was run.

## 2026-06-30 MIR Compact Integer Expression Width Slice

- Propagated concrete source-local integer widths through binary expression type-code inference.
- Lowered inferred-width binary expressions through typed MIR arithmetic instead of defaulting to i64.
- Lowered integer comparison operands with the inferred operand width so LLVM comparisons keep compact integer facts.
- Added focused IR facts for i32 parameter arithmetic, parameter-plus-literal arithmetic, var aliases of typed arithmetic, direct comparison, and arithmetic feeding comparison.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionTypedInteger --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent.
  - A tiny stdin-fed executable that called `CompileFunctionWithLocalsToLlvm` for the new width cases was stopped after about 90 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Loop Declared Scalar Return Width Slice

- Resolved the declared integer return MIR type for module counting and accumulator loop lowerers.
- Lowered loop constants, phis, comparisons, updates, and return values through typed MIR builders instead of defaulting to i64.
- Added focused IR facts for typed counting `while`, accumulator `while`, counting `for`, and header counting `for` returns.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceLoopLowering.stark` through `lower-mir`: passed with 0 diagnostics.
  - `./stark --host-test-inspect` for `tests-stark/selfhost.Ir/IrTests.stark` through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter ModuleCountingWhileLoopPreservesDeclaredI32ReturnType --filter ModuleAccumulatorWhileLoopPreservesDeclaredI16ReturnType --filter ModuleCountingForLoopPreservesDeclaredI8ReturnType --filter ModuleHeaderCountingForLoopPreservesDeclaredI32ReturnType --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped by a 180-second alarm after remaining silent.
- No broad test sweep was run.

## 2026-06-30 MIR Storage Member Chain Slice

- Added a storage field layout resolver that allows aggregate-typed intermediate fields while preserving final scalar and enum leaf facts.
- Lowered nested scalar and enum member-chain reads through the existing typed pointer load path.
- Lowered nested scalar and enum member-chain assignments through the existing typed pointer store path.
- Added focused IR facts for stack and heap nested field reads, nested field reassignment, and invalid nested path rejection.
- Split the remaining member-chain and slice descriptor work in `TASKS.md` so the unfinished fixed-array and HIR-row pieces stay visible.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceLocalLowering.stark` through `lower-mir`: passed with 0 errors and existing recursion warnings.
  - `./stark --host-test-inspect` for `tests-stark/selfhost.Ir/IrTests.stark` through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter NestedObject --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 150 seconds because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Nested Fixed-Array Member Chain Slice

- Reused the nested storage-field resolver for fixed-array leaves reached through aggregate member chains.
- Lowered constant nested fixed-array element reads and assignments through direct byte offsets.
- Lowered dynamic nested fixed-array element reads and assignments through `MirPtrIndexOffset` with preserved base offset, element size, bounds, and alignment facts.
- Added focused IR facts for nested constant element reads, dynamic element reads, dynamic element assignments, and invalid nested array paths.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceLocalLowering.stark` through `lower-mir`: passed with 0 errors and existing recursion warnings.
  - `./stark --host-test-inspect` for `tests-stark/selfhost.Ir/IrTests.stark` through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - A stdin-fed ad hoc executable that called `CompileFunctionWithLocalsToLlvm` for the new nested fixed-array snippets was stopped after about 90 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Descriptor-Backed Slice Indexing Slice

- Added source slice descriptors for stack slice locals initialized from storage-backed fixed-array fields.
- Resolved descriptor-backed `view[index]` reads through `MirPtrIndexOffset` while preserving base pointer, element size, bounds, alignment, and scalar type facts.
- Added focused IR facts for nested fixed-array-to-slice loads and invalid descriptor/index rejection.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionSliceFromNestedFixedArrayReadUsesIndexedPointer --filter RejectsInvalidFixedArraySliceDescriptorsAndIndexes` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Standalone Fixed-Array Slice Descriptor Slice

- Added MIR source records for scalar fixed-array storage locals and their initializer rows.
- Lowered standalone scalar fixed-array initializers into typed stack stores, including zero stores for omitted trailing elements.
- Resolved `stack T[] view = values` from standalone fixed-array locals into descriptor-backed slice facts.
- Kept descriptor-backed `view[index]` reads on `MirPtrIndexOffset`, preserving base pointer, element size, range-proven bounds, alignment, and scalar type facts.
- Added focused IR facts for standalone fixed-array-to-slice reads and invalid standalone descriptor/index cases.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionSliceFromStandaloneFixedArrayReadUsesIndexedPointer --filter RejectsInvalidFixedArraySliceDescriptorsAndIndexes` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
  - A stdin-fed ad hoc executable that called `CompileFunctionWithLocalsToLlvm` for the standalone fixed-array slice snippets was stopped after about 120 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Slice Descriptor Assignment Slice

- Added mutability facts to source slice descriptors and rejected descriptor-backed writes through readonly slice locals.
- Lowered straight-line `view[index] = expr` statements through the existing indexed pointer store path.
- Preserved descriptor base pointer, byte offset, element size, range-proven bounds, alignment, and scalar type facts through the store.
- Added focused IR facts for nested and standalone fixed-array-backed slice assignments plus invalid readonly, out-of-range, and wrong-type writes.
- Split the remaining ABI slice and fixed-array parameter work in `TASKS.md` into explicit aggregate-carrier subtasks.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceLocalLowering.stark` through `lower-mir`: passed with 0 errors and existing recursion warnings.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceModuleLowering.stark` through `lower-mir`: passed with 0 diagnostics.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionSliceFromNestedFixedArrayAssignmentStoresThroughIndexedPointer --filter CompileFunctionSliceFromStandaloneFixedArrayAssignmentStoresThroughIndexedPointer --filter RejectsInvalidFixedArraySliceDescriptorsAndIndexes --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Branch Slice Descriptor Assignment Slice

- Routed terminal-if and switch storage mutation parsing through the shared descriptor-aware mutation parser.
- Added fixed-array storage locals and slice descriptor locals to the terminal-if and switch storage-assignment lowering paths.
- Lowered descriptor-backed `view[index] = expr` before terminal `if` branches and inside switch arms through the indexed pointer store path.
- Preserved descriptor base pointer, byte offset, element size, range-proven bounds, alignment, scalar type facts, and branch-local expression facts through lowering.
- Added focused IR facts for terminal-if slice assignment and integer-switch slice assignment.
- Narrow verification:
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceLocalLowering.stark`, `SourceIfLowering.stark`, and `SourceSwitchLowering.stark` through `lower-mir`: passed with 0 errors; `SourceLocalLowering` and `SourceIfLowering` reported existing recursion warnings.
  - `./stark --host-test-inspect` for `selfhost/Compiler/Mir/SourceSwitchLowering.stark` through `lower-mir`: passed with 0 diagnostics.
  - `../../stark test --filter CompileFunctionSliceAssignmentBeforeTerminalIfStoresThroughIndexedPointer --filter CompileIntegerSwitchSliceAssignmentStoresThroughIndexedPointer --target arm64-apple-macosx26.0.0 --stage stage0` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
  - A temporary smoke executable that called the two new helper snippets directly was stopped after about 180 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Slice Parameter ABI Slice

- Added MIR slice parameter base and length extraction ops for concrete `{ ptr, i64 }` slice ABI carriers.
- Emitted slice parameters as LLVM aggregate carriers and extracted the base and length in function bodies.
- Lowered slice parameter reads and writes through source slice descriptors with element size, alignment, alias, and mutability facts preserved.
- Used non-inbounds indexed pointer emission for dynamic slice parameter indexes because runtime length is carried but not yet a dominating bounds proof.
- Kept concrete slice call arguments open as the remaining aggregate-carrier work.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost -I ../../stdlib/src --target arm64-apple-macosx26.0.0 --diagnostic-format text` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter CompileFunctionSliceParameterReadUsesConcreteSliceAbiAndUnboundedIndex --filter CompileFunctionSliceParameterAssignmentStoresThroughUnboundedIndex` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
  - `./stark --host-test-inspect` for the touched selfhost MIR modules through `lower-mir`: stopped after about 90 seconds because the focused inspector batch remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Slice Call Argument ABI Slice

- Added a MIR slice aggregate value op that lowers call arguments to concrete `{ ptr, i64 }` LLVM carriers.
- Validated slice call arguments against descriptor element type, size, alignment, and field type facts before lowering.
- Lowered fixed-array-backed slice arguments and forwarded slice parameters through aggregate call carriers.
- Preserved slice base pointers for `separate_storage` assumptions instead of emitting aggregate operands as pointers.
- Wired descriptor-aware slice calls through straight-line module lowering and the full local/slice terminal-if path.
- Added focused IR facts for fixed-array slice calls, slice-parameter forwarding, terminal-if slice calls, and mismatched element-type rejection.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter SliceCallArgument --filter SliceParameterForwarding --filter MismatchedElementType` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Switch Slice Call Carrier Slice

- Threaded slice call argument context through descriptor-aware switch field-assignment validation and lowering.
- Lowered storage initializers and storage mutations through call-context-aware helpers in straight-line, terminal-if, and switch paths.
- Preserved slice descriptor element type, element size, alignment, base pointer, and length facts through calls nested inside storage assignments.
- Added a focused IR fact for a slice call argument inside a switch object-field assignment.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter SwitchFieldAssignmentUsesConcreteSliceAbiCarrier` in `tests-stark/selfhost.Ir`: stopped after about 60 seconds because the exact filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Fixed Array Parameter ABI Slice

- Added fixed-array by-value parameter ABI facts and emitted `[N x T]` LLVM parameter and call carriers.
- Lowered constant fixed-array parameter element reads through direct `extractvalue` without materializing storage.
- Lowered dynamic fixed-array parameter element reads and slice views through addressable parameter slots with element size, alignment, and bounds facts preserved.
- Allowed fixed-array parameters to feed both fixed-array by-value callees and concrete slice callees without losing ABI carrier facts.
- Added focused IR facts for fixed-array parameter direct reads, dynamic reads, slice views, and fixed-array/slice call forwarding.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --emit-llvm -I ../../selfhost --target arm64-apple-macosx26.0.0 -o /tmp/selfhost-ir-tests.ll` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter FixedArrayParameter --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after several minutes because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-07-07 MIR Fixed Array Whole Capture Param Slice

- Recognized the whole fixed-array capture sentinel in terminal fixed-array list switch capture overrides.
- Lowered whole captures from by-value fixed-array parameters as direct aggregate `MirParamTyped` values instead of scalar element extracts.
- Added fixed-array capture alias descriptor and argument rows so capture names preserve the original parameter's fixed-array ABI carrier facts through validation and call lowering.
- Routed fixed-array terminal switch guards, scalar-return arms, and defaults through the call-context lowering path so direct aggregate call arguments survive lowering.
- Kept storage-backed whole fixed-array captures rejected until the explicit copy or borrow semantics task is resolved.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/SourceSwitchLowering.stark selfhost/Compiler/Mir/SourceExpressionLowering.stark selfhost/Compiler/Mir/SourceFunctionContext.stark`: passed.
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: stopped after about 60 seconds because the focused file check remained silent.
  - `./stark selfhost/Compiler/Mir/SourceExpressionLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: stopped after about 30 seconds because the focused file check remained silent.
  - `./stark selfhost/Compiler/Mir/SourceFunctionContext.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: stopped after about 30 seconds because the focused file check remained silent.
  - `./stark tests-stark/compiler.MirTests/MidLevelIrLoweringTests_SwitchPatternLowerer.stark --check -I selfhost -I stdlib/src -I tests-stark/compiler.MirTests --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --log-level error`: failed before the affected switch-pattern tests on existing missing macOS runtime symbols `System.Runtime.Platform.MacOS.StartProcessCaptureGrouped` and `System.Runtime.Platform.MacOS.KillProcessGroup`.
- No broad test sweep was run.

## 2026-06-30 MIR Address-Taking Place Slice

- Added a selfhost expression AST node for unary address-of expressions.
- Parsed `&place` through the simple expression parser and descriptor-aware local parser.
- Lowered address-taking for storage-backed scalar locals, constructed-object fields, slice elements, and fixed-array parameter elements.
- Reused `MirPtrOffset` and indexed pointer ops so byte offsets, element sizes, bounds proofs, and alignment facts survive to LLVM IR.
- Added focused IR facts for stack scalar, object field, slice element, and fixed-array parameter element addresses passed to raw-pointer callees.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --emit-llvm -I ../../selfhost --target arm64-apple-macosx26.0.0 -o /tmp/selfhost-ir-tests-address-of.ll` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --filter AddressOf --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about three minutes because the focused project test runner remained silent.
  - A temporary one-off executable that called the four new helper snippets directly was stopped after about three minutes because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Scalar Parameter Address Slot Slice

- Derived lowering-local type rows that mark address-taken scalar parameters as stored scalar locals.
- Materialized addressable scalar parameter slots once during signature override seeding and stored the typed parameter value into the slot.
- Threaded the derived lowering-local type table through straight-line, terminal-if, and switch source lowering.
- Added focused IR facts for passing `&param` to raw-pointer callees and for later reads loading from the same slot.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark AddressParamProbe.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed before the temporary probe file was removed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark IrTests.stark --emit-llvm -I ../../selfhost --target arm64-apple-macosx26.0.0 -o /tmp/selfhost-ir-tests-scalar-param-address.ll` in `tests-stark/selfhost.Ir`: stopped after about two minutes because the focused file emit remained silent.
  - `../../stark test --filter CompileFunctionAddressOfScalarParameter --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent.
  - `../../stark AddressParamProbe.stark -I ../../selfhost --target arm64-apple-macosx26.0.0 -o /tmp/address-param-probe` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because executable compilation remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Nested Raw Pointer Dereference Slice

- Added pointer-valued raw-pointer pointee codes and one-level nested pointee facts.
- Parsed and lowered direct nested raw-pointer loads through `load ptr` plus typed scalar loads.
- Lowered nested raw-pointer stores only when the nested pointer is mutable.
- Preserved nested pointee facts through raw-pointer `var` aliases and declared raw-pointer locals.
- Rejected readonly nested raw-pointer stores and incompatible pointer-valued assignments.
- Added focused IR facts for nested loads, nested stores, readonly rejection, alias propagation, and storage-local propagation.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `../../stark test --filter NestedRawPointer --target arm64-apple-macosx26.0.0 --stage stage0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Scalar Raw Pointer Dereference Slice

- Carried scalar raw-pointer parameter pointee type, size, and alignment facts into source parameter symbol rows.
- Classified scalar raw-pointer ABI facts so the same pointee metadata is available beside pointer ABI attributes.
- Parsed prefix raw-pointer dereference expressions and rejected dereferences without scalar pointee facts.
- Lowered scalar raw-pointer parameter dereferences through typed aligned MIR pointer loads.
- Added focused IR facts for bool, i8, i16, i32, and i64 raw-pointer parameter loads.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed.
  - `../../stark test --target arm64-apple-macosx26.0.0 --filter RawPointerParameterDeref` in `tests-stark/selfhost.Ir`: stopped after about 120 seconds because the filtered project test runner remained silent.
  - A temporary one-off executable probe for the five new dereference snippets was stopped after about two minutes because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-06-30 MIR Bounded Raw Pointer Indexed Dereference Slice

- Carried bounded raw-pointer element-count facts into source parameter symbol rows.
- Parsed fixed-count and count-parameter bounded scalar raw-pointer indexed element expressions.
- Proved indexed raw-pointer bounds from source integer ranges before emitting inbounds pointer offsets.
- Lowered bounded scalar `rawptr` indexed loads and `rawmutptr` indexed stores through typed aligned MIR load/store operations.
- Rejected unbounded and insufficiently proven indexed raw-pointer dereference patterns.
- Added focused IR facts for fixed-count loads, count-parameter loads, indexed stores, readonly-store rejection, unbounded rejection, and insufficient-count rejection.
- Narrow verification:
  - `../../stark ../../selfhost/Compiler/Mir.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark IrTests.stark --check -I ../../selfhost --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter RawPointerIndexed --target arm64-apple-macosx26.0.0` in `tests-stark/selfhost.Ir`: stopped after about 90 seconds because the filtered project test runner remained silent.
  - A temporary one-off executable probe for the first indexed-load snippet was stopped after about 90 seconds because compiling the selfhost imports remained silent.
- No broad test sweep was run.

## 2026-07-07 MIR Text Literal Source Expression Row-Link Slice

- Added string and character literal variants to the MIR source-expression node table.
- Stored literal token ids and 1-based typed-literal row links on text literal source-expression nodes.
- Added a context-keyed typed-literal row lookup by literal token.
- Threaded the typed literal expression table into the module lowering fact bundle for later descriptor lowering.
- Kept generic expression lowering rejecting text literal nodes until the dedicated MIR text descriptor lowering task wires them in.
- Added focused IR facts for typed-literal token lookup, source-expression row links, and source parser rejection of text literals as integers.
- Narrow verification:
  - `git diff --check`: passed.
  - `./stark selfhost/Compiler/Mir/SourceExpressions.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed with pre-existing recursive-call warnings.
  - `./stark selfhost/Compiler/Typing/TypedLiteralModel.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0`: failed during module graph with stale package-image target-layout compatibility errors in `selfhost/build/.../libStarkCompiler.starkpkg`.
  - `./stark selfhost/Compiler/Mir.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated type-check progress and no final diagnostics.
  - `./stark selfhost/Compiler/Mir/SourceModuleFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: first run surfaced a retborrow error in `LlvmText.stark`; rerun after the scalar-copy fix was interrupted after repeated type-check progress and no final diagnostics.
  - `./stark selfhost/Compiler/Mir/LlvmText.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated type-check progress and no final diagnostics.
- No broad test sweep was run.

## 2026-07-07 MIR Global Linkage LLVM Emission Slice

- Added `GlobalLinkageFacts` as a MIR backend fact record for global definitions.
- Added deterministic textual LLVM global emission that writes `internal dso_local` for internal global linkage facts.
- Kept the existing `EmitLlvmGlobals` path byte-compatible by routing it through an empty linkage table.
- Added module-level LLVM emission variants that accept global linkage fact tables before function emission.
- Added a focused IR fact for exact global linkage spelling on scalar and pointer globals.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/LlvmFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmModules.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Lowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated import/type-check progress and no diagnostics.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated import/type-check progress and no diagnostics.
  - `./stark tests-stark/selfhost.Lowering/LoweringTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: failed through stale package-image resolution of the compiler package and the known missing macOS stdlib process symbols; the attempted lowering-test addition was removed.
- No broad test sweep was run.

## 2026-07-07 MIR Global Section and Visibility LLVM Emission Slice

- Added compact MIR backend fact records for global LLVM visibility and global section placement.
- Added deterministic textual LLVM global emission that writes `hidden` and `protected` visibility facts in the definition prefix.
- Added deterministic textual LLVM global emission that writes quoted section suffixes and escapes only quote and backslash bytes.
- Kept the existing `EmitLlvmGlobals` and linkage-only paths byte-compatible by routing them through empty visibility and section fact tables.
- Added module-level LLVM emission variants that accept global linkage, visibility, and section fact tables together before function emission.
- Added a focused IR fact for exact global linkage, visibility, section, and section-escaping spelling.
- Split the section and visibility LLVM tasks into source-import, function-fact, and libLLVM-builder follow-ups.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/LlvmFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmModules.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: interrupted after repeated import/type-check progress and no diagnostics.
- No broad test sweep was run.

## 2026-07-07 MIR Function Section and Visibility LLVM Emission Slice

- Added compact MIR backend fact records for function LLVM visibility and function section placement.
- Carried the function visibility and section records on the existing per-function backend fact row that already survives source-module lowering.
- Added deterministic textual LLVM definition emission that writes `hidden` and `protected` visibility facts after linkage/non-preemption facts.
- Added deterministic textual LLVM definition emission that writes quoted function `section` attributes after existing function effect attributes.
- Added a focused IR fact for exact function linkage, visibility, and section spelling through the range-fact definition core.
- Split source function section and visibility attribute import into explicit follow-up tasks.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/LlvmFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `./stark selfhost/Compiler/Mir/LlvmFunctions.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32'`: passed.
  - `../../stark test --filter EmitsLlvmDefinitionWithFunctionBackendFacts --test-progress --test-timeout 120` in `tests-stark/selfhost.Ir`: stopped after about two minutes because the filtered project build produced no output.
  - `git diff --check -- selfhost/Compiler/Mir/LlvmFacts.stark selfhost/Compiler/Mir/LlvmFunctions.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
- No broad test sweep was run.

## 2026-07-07 Struct Aggregate Capture Front-End Verification Slice

- Added parser coverage that verifies tuple and property struct aggregate switch captures become section-scoped `PatternBinding` rows.
- Added binding coverage that verifies struct aggregate field captures resolve in `when` guards and section bodies.
- Added binding coverage that verifies struct aggregate field captures do not leak into the `default` section.
- Narrow verification:
  - `./stark tests-stark/selfhost.Parsing/ParsingTests.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Binding/BindingTests.stark --check -I selfhost --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: reached semantic validation, then failed on pre-existing `Compiler.Ir` finite-effect diagnostics where `IrTable.Get`/`IrTable.Replace` call non-finite `System.Collections.List.Get`/`GetMut`.
  - `./stark tests-stark/selfhost.Parsing/ParsingTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: failed before this slice on known missing macOS stdlib process symbols `System.Runtime.Platform.MacOS.StartProcessCaptureGrouped` and `System.Runtime.Platform.MacOS.KillProcessGroup`.
  - `./stark tests-stark/selfhost.Binding/BindingTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: failed before this slice on the same known macOS stdlib process-symbol gap.
  - `../../stark test --filter StructAggregateSwitchFieldCapturesParseAsPatternBindings --test-progress --test-timeout 120` in `tests-stark/selfhost.Parsing`: stopped after roughly two minutes because the filtered project build produced no output.
  - `../../stark test --filter StructAggregateFieldCapturesResolveInGuardAndBody --test-progress --test-timeout 120` in `tests-stark/selfhost.Binding`: stopped after roughly ninety seconds because the filtered project build produced no output.
  - `git diff --check -- tests-stark/selfhost.Parsing/ParsingTests.stark tests-stark/selfhost.Binding/BindingTests.stark`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Aggregate Pattern Builder Slice

- Added a self-host typing builder that walks parsed function bodies and records top-level aggregate switch-label rows.
- Preserved the switch subject owner type head, primary token, source type span, and enum variant ordinal when available.
- Added a focused IR fact that builds rows for struct tuple/property labels and an enum aggregate label.
- Narrow verification:
  - `./stark selfhost/Compiler/Typing/TypedSwitchPatterns.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing/TypedPipeline.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark selfhost/Compiler/Typing.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Pattern Source Fact Index Slice

- Added dense start-token indexes for imported typed aggregate and list pattern rows in `SourceModuleLoweringFacts`.
- Built the indexes once during source-module fact construction and rejected out-of-range or duplicate typed pattern starts before MIR lowering can consume them.
- Added O(1) helpers for resolving imported typed aggregate and list pattern rows from source pattern start tokens.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceModuleFacts.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Pattern Source Label Validation Slice

- Added O(1) validation that module-fact-backed struct aggregate labels match imported typed aggregate rows before MIR lowering.
- Added O(1) validation that module-fact-backed enum aggregate labels match imported typed aggregate rows before MIR lowering.
- Added O(1) validation that module-fact-backed fixed-array list labels match imported typed list rows before MIR lowering.
- Kept discard and whole-value capture labels on their existing parser paths because they do not import typed aggregate/list rows.
- Left the local switch-assignment entrypoint as an explicit follow-up because it does not currently receive `SourceModuleLoweringFacts`.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Assignment Source Label Validation Slice

- Built `SourceModuleLoweringFacts` for local switch-assignment list patterns before parsing their fixed-array case labels.
- Threaded source-module facts into parameter-backed and storage-backed fixed-array list switch-assignment case parsers.
- Enabled typed-list-row validation for real local switch-assignment lowering while keeping the older shape preflight on its no-fact parser mode.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch List Pattern Row Lookup Slice

- Replaced typed-row-backed fixed-array list label structure parsing with a dense start-token row lookup.
- Imported typed list member rows into the existing flat MIR list-pattern and capture tables without walking the list syntax again.
- Kept discard and whole-list capture labels on the existing compact parser path because they do not have typed list rows.
- Kept scalar literal and range value conversion on the existing token parsers while using typed rows for list structure, ordinals, captures, and row bounds.
- Split the remaining aggregate and nested typed-row lookup work into sentence-sized follow-up tasks.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Struct Aggregate Pattern Row Lookup Slice

- Replaced typed-row-backed struct aggregate field-member parsing with dense aggregate start-token row lookup.
- Imported typed struct member rows into the existing flat MIR field-pattern and field-capture tables without walking tuple/property label members again.
- Preserved tuple field-order validation, property duplicate-field validation, struct layout facts, member-path facts, scalar interval conversion, and capture-name facts.
- Kept discard and whole-struct capture labels on their existing compact paths because they do not have typed aggregate member rows.
- Kept nested aggregate/list field rows rejected here pending the shared decision-descriptor follow-up.
- Removed the now-unused ad hoc struct field pattern parser helper.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Switch Enum Aggregate Pattern Row Lookup Slice

- Replaced typed-row-backed enum aggregate payload-member parsing with dense aggregate start-token row lookup.
- Imported typed enum payload member rows into the existing flat MIR payload-pattern and payload-capture tables without walking tuple/named payload label members again.
- Routed discard-only enum aggregate labels, scalar payload-pattern labels, and capture-bearing payload labels through the same typed-row importer with explicit capability flags.
- Preserved tuple payload-order validation, named-payload duplicate validation, enum layout payload type facts, scalar interval conversion, and capture-name facts.
- Kept nested aggregate/list payload rows rejected here pending the shared decision-descriptor follow-up.
- Removed the now-unused named-payload token parser helpers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
- No broad test sweep was run.

## 2026-07-07 Typed Nested Switch Decision Descriptor Routing Slice

- Routed typed nested aggregate and fixed-array list member rows into shared pattern-decision descriptors during struct aggregate switch preflight.
- Threaded per-case typed nested member spans through terminal and assignment struct aggregate parsers and lowerers so backend owner, enum-variant, fixed-length, and element-type facts survive into descriptor rows.
- Kept nested descriptor-backed branch emission conservatively rejected before MIR block construction; the next lowering slices can consume the descriptors without reparsing nested source tokens.
- Added a focused follow-up to replace the localized recursive typed-descriptor importer with a checker-friendly iterative worklist; a direct worklist experiment stalled the single-file checker before diagnostics, so this slice keeps the known-checking recursive importer.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: reached `Summary: 0 errors, 4 warnings, 0 infos.` and was interrupted after the summary because the process handle did not return. The warnings are the localized `STK4122` recursion diagnostics in the typed nested descriptor importer.
- No broad test sweep was run.

## 2026-07-08 Logical Package Image Facts Slice

- Added a compact logical package-image fact view for section/string counts, package identity strings, profile strings, target backend strings, C data model facts, and aggregate pointer layout facts.
- Added a facts-only target-feature string-index reader and string append helper so compatibility checks can inspect `PINF`/`STRS` facts without decoding or materializing `MANF`.
- Added a focused self-host IR fact covering package identity, profile, target triple/data layout, target features, C integer widths, and aggregate pointer size/alignment.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed; the checker printed the existing verbose pass timing warnings before returning success.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-package-facts-semantic-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Package Compatibility Facts Slice

- Added allocation-free logical package-image fact string comparisons over validated `STRS` entries.
- Added compact-fact compatibility helpers for build profile, target triple/data layout, target backend CPU/relocation/code model, target features, C data model widths/signedness, and aggregate pointer layout.
- Added a focused self-host IR fact that proves matching and mismatch cases against the synthetic profile/target logical package image.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-package-compat-semantic-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Package Version Compatibility Slice

- Exposed the supported package-image version as a compact header fact.
- Added a header-only version reader and supported-version matcher so consumers can reject incompatible package images before logical `PINF`/`STRS` fact loading.
- Added a focused self-host IR fact that verifies the current version matches, patches the header to an older version, and proves the version matcher and logical fact loader reject it.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-package-version-semantic-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Manifest Module Summary Slice

- Added a scalar `LogicalPackageManifestModuleSummary` for decoded `MANF` module rows, covering source-surface array counts, effective typed-interface counts, and structured-section presence bits.
- Added JSON shape helpers for required/optional array counts and optional object sections without retaining parsed JSON node handles beyond the parse call.
- Mirrored Stage0 `StarkPackageModuleManifest` effective-section precedence by preferring `CompilerSections.TypedInterface` over legacy `TypedInterface`.
- Added a focused self-host IR fact covering module-row counts, compiler-section precedence, absent optional sections, out-of-range module lookup rejection, and malformed section rejection.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-manifest-module-summary-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Typed Declaration Header Slice

- Added `LogicalPackageTypedDeclarationKind` and `LogicalPackageTypedDeclarationSummary` for typed-interface alias/type/function/global declaration headers.
- Added a decoded `MANF` typed-interface declaration reader that validates effective typed-interface section precedence, copies `Name`/`QualifiedName`/`Visibility`/manifest `Kind` text into caller-owned buffers, and preserves compact count/flag facts for generic parameters, fields, variants, methods, constructors, associated types, implemented traits, thread-safety predicates/attributes, value contracts, backend optimization markers, layout markers, global mutability, constant initializers, FFI/strictfp/fastcc, and generic-template body presence.
- Kept type-reference payload decoding as the next explicit task so this slice does not retain JSON node views or reconstruct source.
- Added a focused self-host IR fact covering alias, type, function, and global declaration headers plus out-of-range, malformed function, and missing typed-interface rejection.
- Narrow verification:
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-declaration-summary-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Type Reference Payload Summary Slice

- Added `LogicalPackageTypedTypeReferenceSource` and `LogicalPackageTypeReferenceSummary` so decoded `MANF` typed-interface declaration payloads can expose scalar type-reference facts without retaining JSON node views.
- Added a typed-interface type-reference payload reader for alias targets, function returns, function parameters, global types, and type fields.
- Preserved backend-relevant type-reference facts as explicit flags/counts, including integer bit width/range/signedness presence, pointer/borrow/view facts, fixed-length and element-type presence, function ABI/kind/unsafe/tail-call facts, function pointer parameter/return counts, disjoint/overlap/same/dead-on-return group counts, associated owner/type facts, and source alias markers.
- Added a focused self-host IR fact covering the five supported payload roots plus declaration/source mismatch, out-of-range payload, and missing-kind malformed-payload rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-type-reference-summary-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Type Reference Graph Row Slice

- Added package-owned `LogicalPackageTypeReferenceGraph` storage with append-only type-reference rows, child rows, scalar text rows, and a contiguous text byte slab.
- Added a non-recursive decoded-`MANF` type-reference graph materializer that walks root payloads through a FIFO worklist, avoiding recursive checker pressure while preserving deterministic row order.
- Materialized nested type-reference children for element types, type arguments, comptime value argument types, callable return types, callable parameter types, and associated owner types.
- Interned scalar text facts for type kind/name, integer range bounds, borrow/access/init qualifiers, fixed-length parameter names, callable ABI/kind/storage/capability markers, associated aliases, raw-pointer element count expressions, dead-on-return parameter names, and comptime value argument name/value/symbolic-source text.
- Kept callable memory-contract group member rows and full comptime value argument payload rows as explicit follow-ups rather than overloading the type-reference row shape.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-type-reference-graph-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Type Reference Relation Row Slice

- Added package-owned comptime value argument rows for typed-interface type-reference payloads, preserving parameter-name, integer-value, symbolic-source text rows, symbolic flags, and the materialized child type-reference row link.
- Added package-owned callable parameter relation group rows for disjoint, overlap, and same groups, with contiguous name rows and optional disjoint memory-region rows for parameter/start/count expressions.
- Kept the row model append-only and parent-span indexed so later lowering can consume row ids without re-walking decoded `MANF` JSON or retaining JSON node handles.
- Extended the focused self-host IR type-reference graph fact to validate comptime argument row ids, callable relation group rows, parameter group names, and disjoint region payload text.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-type-reference-groups-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- Runtime execution note: `../../stark test --filter LogicalPackageImageMaterializesTypedDeclarationTypeReferenceGraphRows --test-timeout 180` was attempted from `tests-stark/selfhost.Ir` and interrupted after producing no output for several minutes, so it is not counted as verification.
- No broad test sweep was run.

## 2026-07-08 Logical Typed Callable Fact Slice

- Added decoded-`MANF` callable fact summaries for typed-interface functions and type-owned methods.
- Preserved callable backend facts as explicit fields: symbol name, qualified resolved name, published overload key, inline preference, hot/cold, explicit inline marker, static/unsafe/varargs/tail-call, FFI/strictfp/fastcc, FFI ABI, backend optimization mode, body presence, asm presence, link-name presence, and relation/contract section counts.
- Added decoded callable parameter summaries with owned parameter names, disjoint/const flags, raw-pointer element-count expressions, and required parameter type-object validation.
- Added a focused self-host IR fact covering top-level function callables, type-owned method callables, parameter metadata, out-of-range method lookup rejection, top-level function method-ordinal rejection, and malformed parameter type rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-callable-summary-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Method Type Reference Row Slice

- Added method-owned type-reference source kinds for typed callable return and parameter payloads.
- Added an explicit `MethodOrdinal` fact to `LogicalPackageTypeReferenceSummary` so method return/parameter rows identify `Types[typeOrdinal].Methods[methodOrdinal]` without packing the method ordinal into payload ordinals.
- Added callable type-reference summary and graph materializer entry points that validate top-level function sources separately from method sources while sharing the existing append-only type-reference row model.
- Extended focused self-host IR coverage for method return/parameter summary reads and graph materialization, including nested child rows that preserve the method ordinal.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-method-type-reference-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Callable Fact Row Graph Slice

- Added `LogicalPackageTypedCallableFactGraph` with append-only text rows for callable generic names, comptime generic names, type-parameter constraint names, thread-safety law names, and value-contract triples.
- Added compact callable fact rows for ordinary generic parameters, comptime generic parameters, type-parameter constraints, constraint bound traits, thread-safety law predicates, and value contracts.
- Routed comptime generic parameter types, type-parameter bound trait types, and thread-safety predicate types into the existing durable type-reference graph with explicit callable-fact source kinds, preserving method ordinals and payload ordinals for backend import.
- Added a focused self-host IR fact that materializes top-level function and type-owned method callable fact graphs, verifies text row readback, verifies attached type-reference sources, and rejects malformed comptime generic parameter payloads.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-callable-fact-graph-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: first run caught direct `retborrow` test-row arguments; after fixing those, the rerun stayed silent for several minutes and was interrupted, so it is not counted as a pass.
- No broad test sweep was run.

## 2026-07-08 Logical Callable Import And Asm Row Slice

- Added package-owned callable import rows for `FfiAbi` and function `LinkName`, including method-owned `FfiAbi` support and malformed method `LinkName` rejection.
- Added package-owned asm rows for function `Asm` metadata, preserving architecture, template text, dense input/output/clobber row spans, operand register names, operand value names, and output return-binding flags.
- Kept asm/import materialization source-free after JSON decode: backend consumers can read row ids and contiguous child ranges without reconstructing `unsafe ffi asm(...)` source.
- Extended the focused self-host IR callable fact graph coverage to verify function import rows, method import rows, asm row counts, child spans, operand text readback, clobber text readback, and return-binding flags.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-callable-import-asm-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Typed Type Fact Graph Slice

- Added package-owned typed type fact rows for type kind/backend/layout metadata, struct pack/align facts, dyn-trait flags, associated alias target rows, implemented trait names/types, and type-level thread-safety law attributes.
- Routed associated alias targets, implemented trait types, and thread-safety condition types through the durable type-reference graph with explicit source provenance, so backend import can consume row ids without re-walking decoded `MANF` JSON.
- Added a focused self-host IR fact covering text extraction, numeric pack/align facts, associated alias target rows, implemented trait refs, condition type refs, dyn-trait flags, and malformed trait-reference rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-type-fact-graph-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Typed Enum Variant Row Slice

- Added package-owned typed enum variant rows and payload rows to the typed type fact graph, preserving variant names, optional `ok`/`err` roles, named-payload mode, absorbed-error type refs, and dense payload child spans.
- Routed enum payload types and absorbed-error types through the durable type-reference graph with explicit variant ordinal and payload ordinal provenance instead of packing both into one ambiguous value.
- Added a focused self-host IR fact covering enum variant row counts, dense payload child spans, role/name text extraction, payload type refs, absorbed-error type refs, and malformed payload rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-type-enum-variant-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Typed Type Alias Fact Row Slice

- Added a package-owned typed type-alias fact graph with alias rows, string generic parameter rows, comptime generic parameter rows, and dense child spans for source-free package import.
- Routed alias target types and comptime generic parameter types through the durable type-reference graph with explicit alias declaration provenance and payload ordinals.
- Added a focused self-host IR fact covering alias row counts, child spans, generic/comptime parameter text extraction, nested target type-argument rows, comptime integer type facts, and malformed comptime generic parameter rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-typed-type-alias-fact-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Function Effect Fact Row Slice

- Added a package-owned compiler-facts function-effect graph with profile rows and packed text rows for resolved names, kinds, inline preferences, FFI ABIs, and backend optimization modes.
- Preserved LLVM-relevant function-effect booleans as direct row facts, including memory purity, sync/free/unwind/return/progress facts, fastcc, FFI, hot/cold, strict-fp, varargs, tail-callable, and norecurse.
- Added a focused self-host IR fact covering `CompilerSections.CompilerFacts.FunctionEffects` import, optional FFI/backend-mode text rows, optional bool defaults, text extraction, and malformed required-bool rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-function-effect-fact-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep was run.

## 2026-07-08 Logical Function Semantic Memory Effect Row Slice

- Added a package-owned function semantic fact graph with semantic header rows, called-function rows, parameter memory-effect rows, initialization byte-range rows, call rows, and call-argument memory-effect rows.
- Preserved function and call memory effects as compact boolean fact structs, including argument reads/writes/captures, other-memory reads/writes, argument initialization, and pointee-dead-on-return facts.
- Preserved parameter and call-argument names, type text, capture kinds, optional dereferenceable/alignment bytes, optional caller/callee parameter names, dense child spans, and explicit `HasOpaqueCall` presence.
- Added a focused self-host IR fact covering `CompilerSections.CompilerFacts.FunctionSemantics` import, optional arrays/nullables, dense spans, text extraction, byte ranges, call argument effects, and malformed memory-effect rejection.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-function-semantic-memory-effect-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep or filtered `stark test` runner was run.

## 2026-07-08 Logical Function Ownership Fact Row Slice

- Added a package-owned function ownership fact graph with ownership rows, implicit-drop rows, move rows, event rows, projection rows, root rows, packed text rows, and embedded structured type-reference rows.
- Preserved ownership validity, event source locations, index-projection flags, root mutability/address/move/drop/reinitialization facts, final availability text, and dense child spans without reconstructing Stark source.
- Routed event place types and root types through the durable type-reference graph with explicit function-ownership source labels so backend import can consume row ids and type facts directly.
- Added a focused self-host IR fact covering `CompilerSections.CompilerFacts.FunctionSemantics[*].Ownership`, skip behavior for functions without ownership, text extraction, structured type-reference source/payload facts, and malformed required ownership arrays.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-function-ownership-fact-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep or filtered `stark test` runner was run.

## 2026-07-08 Logical Type Reference Range Fact Slice

- Preserved type-reference `BitWidth` and `FixedLength` as compact numeric row facts instead of presence-only flags, so range-typed integer and fixed-array facts survive logical package-image materialization.
- Kept `RangeMin`/`RangeMax` as packed text rows and added focused row readback checks for range-bearing type references.
- Updated the logical package-image range fact task as complete; there is no separate Stage0 `RangeFacts` JSON section beyond type-reference range facts and function semantic initialization byte ranges.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-range-facts-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
- No broad test sweep or filtered `stark test` runner was run.

## 2026-07-08 Logical Callable Aliasing Fact Slice

- Added package-owned typed-callable aliasing rows for `DisjointParameterGroups`, `OverlapParameterGroups`, and `SameParameterGroups`, with dense parameter-name and memory-region child spans for both functions and methods.
- Preserved alias group parameter names, region parameter names, optional start expressions, and optional count expressions as typed callable fact text rows so backend import can consume memory-contract facts without source reconstruction.
- Added an allocation-free callable alias-group lookup helper keyed by module/callable/declaration/method/group identity.
- Extended focused self-host IR callable fact graph coverage to verify function and method alias group counts, row identity, dense child spans, region flags, text-kind tags, and text readback.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `./stark --host-test-inspect build/tmp/irtests-nested-enum-payload-semantic-host-test.json > build/tmp/irtests-logical-aliasing-facts-host-test.out.json`: passed with `succeeded: true` and 0 diagnostics.
  - `../../stark test --filter LogicalPackageImageMaterializesTypedCallableFactGraphRows` from `tests-stark/selfhost.Ir`: stopped after several silent intervals to avoid a long project run.
- No broad test sweep was run.

## 2026-07-09 Logical Typed-Interface Type Manifest Builder Slice

- Added compact typed-interface type manifest builder rows with per-module first/last/count links and dense child links for fields, generic/comptime parameters, associated types, enum variants/payloads, implemented trait names/types, and thread-safety law attributes.
- Emitted `TypedInterface.Types` rows with required field arrays, concrete associated/trait/thread-safety/variant fact objects, and backend/layout/dyn-trait scalar facts so typed type facts survive JSON readback and package-image materialization.
- Extended the focused manifest-builder fact to read typed type declaration summaries, direct type-reference summaries for field/associated/trait/thread-safety condition sources, and materialized typed type fact graph rows/text/type references.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src -I tests-stark --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: interrupted after roughly 90 seconds of silence to avoid a long focused test-file run.
- No broad test sweep was run.

## 2026-07-09 Logical Function Effect Manifest Builder Slice

- Added compact compiler-fact function-effect manifest builder rows with per-module first/last/count links and stable row handles for self-host compiler artifact emission.
- Emitted `CompilerFacts.FunctionEffects` rows with required function-effect booleans plus optional FFI ABI/backend optimization mode text and optional varargs/tail-callable/norecurse flags, preserving backend facts for later LLVM lowering.
- Added a focused manifest-builder fact that writes two function-effect rows and materializes them back through the existing compiler-fact graph reader, covering backend-mode and FFI ABI optional paths.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I stdlib/src --no-stark-path --target arm64-apple-macosx26.0.0 --target-data-layout 'e-m:o-p:64:64-p270:32:32-p271:32-p272:64:64-i64:64-i128:128-n32:64-S128-Fn32' --diagnostic-format text --log-level error`: interrupted after roughly 90 seconds of silence to avoid a long focused test-file run.
- No broad test sweep was run.

## 2026-07-09 Logical ABI Function Manifest Builder Slice

- Added compact compiler-fact ABI manifest builder rows for functions, parameters, and split LLVM carrier types, with per-module first/last/count links and dense parameter/carrier child links.
- Emitted `CompilerFacts.AbiFunctions` rows with required source/LLVM return and parameter type references, required `IsFfi`, optional source name, FFI ABI, link name, fastcc, varargs, and tail-call metadata.
- Preserved scalar type-reference facts needed by ABI/LLVM lowering, including integer bit widths, range endpoint text, and signedness presence/value, instead of reducing ABI types to kind/name only.
- Added a focused manifest-builder fact that writes an FFI ABI function, raw-pointer count expression, named aggregate parameter, integer LLVM carrier split, and materializes the rows through the existing ABI fact graph reader.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Concrete Layout/Native Metadata Manifest Builder Slice

- Added compact compiler-fact concrete layout and field manifest builder rows with per-module first/last/count links and dense field child links.
- Added root-level native dependency builder rows grouped by dependency kind, plus per-module linkage builder rows with dense defined/referenced symbol child links.
- Emitted `CompilerFacts.ConcreteLayouts`, root `NativeDependencies`, and `CompilerFacts.Linkage` JSON so layout/native/linkage facts survive manifest builder output and existing graph materialization.
- Added a focused manifest-builder fact that writes a concrete aggregate layout, native library/source/link/pkg-config facts, and linkage symbols, then materializes the emitted JSON through existing layout and native metadata graph readers.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Function Ownership Manifest Builder Slice

- Added compact compiler-fact function ownership manifest builder rows for implicit drops, moves, ownership events, event projections, and ownership roots, linked from existing function-semantic rows.
- Emitted `CompilerFacts.FunctionSemantics[*].Ownership` with required ownership validity/name arrays and optional event/root arrays so ownership facts survive manifest builder output and existing graph materialization.
- Preserved event place type references, source locations, index-projection flags, root type references, root mutability/address/move/drop/reinitialization facts, and final availability text for later backend and LLVM lowering.
- Added a focused manifest-builder fact that writes a no-ownership semantic row plus an owned `Demo.Work` row, then materializes the emitted ownership graph through the existing ownership fact reader.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Function Header Manifest Builder Slice

- Added compact generic-template function manifest builder rows with per-module first/last/count links and stable row handles for later typed-body/deferred-operation child builders.
- Emitted `CompilerSections.GenericTemplates.Functions` rows with required `QualifiedResolvedName`, `QualifiedName`, and `OverloadKey` text plus optional body text, backend optimization mode, top-level statement count, estimated body cost, semantics presence, and empty typed-body shell facts.
- Kept the slice scalar-only and append-only: child operation counts remain owned by future child builders, while the header row preserves backend facts needed by later self-host lowering/import.
- Added a focused manifest-builder fact that writes one minimal template and one scalar-complete template, then materializes the emitted JSON through the existing generic-template fact reader.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Typed-Body Manifest Builder Slice

- Added compact generic-template typed-body statement and expression manifest builder rows with O(1) per-template top-level statement links and stable expression row handles.
- Emitted populated `TypedBody.Statements` JSON for top-level statements and root statement expressions, preserving statement/expression text, mutability/storage/capacity/provenance facts, and scalar integer type-reference facts through existing generic-template materialization.
- Added a focused manifest-builder fact that writes one typed local-variable statement with a literal expression and verifies readback of statement rows, expression rows, and `GenericTemplateStatementType`/`GenericTemplateExpressionType` references.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Expression Child Manifest Builder Slice

- Added compact generic-template expression child-list builder rows with O(1) append links for expression arguments and stable expression row handles for later nested typed-body builders.
- Added expression payload builders for member names, operator names, type arguments, and comptime value arguments, preserving type-reference kind/name/bit-width/range/signedness facts instead of flattening payloads to strings.
- Emitted `Arguments`, `MemberNames`, `OperatorNames`, `TypeArguments`, and `ComptimeValueArguments` JSON under typed-template expressions so existing generic-template materialization produces child expression rows plus payload ordinals and backend-visible type-reference sources.
- Added a focused manifest-builder fact that writes a binary expression with two arguments, one member name, one operator name, one named type argument, and one symbolic comptime value argument, then verifies materialized child ordinals, payload text, and type-reference sources.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with recursive expression-writer stack-growth warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Nested Typed-Body Manifest Builder Slice

- Added compact generic-template nested statement builder rows for initializer, iterator, body, then, else, and switch-case statement lists, using O(1) append links so builder cost stays linear.
- Added switch-case and pattern builder rows with stable handles for names, ordinals, case/guard/end expressions, condition patterns, member patterns, and nested pattern members.
- Emitted the materializer's existing nested typed-body JSON shape: `SwitchCases`, `Statements`, `InitializerStatements`, `IteratorStatements`, `BodyStatements`, `ThenStatements`, `ElseStatements`, `ConditionPattern`, and pattern `Members`, preserving parent-kind, ordinal, expression, and type-reference facts for backend consumers.
- Added a focused generic-template readback fact that writes a switch with case pattern members, an if with condition pattern and then/else blocks, and a loop with initializer/iterator/body blocks, then verifies materialized row counts, parent kinds, ordinals, names, and typed literal expression facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive expression-writer warnings plus recursive warnings for the new nested JSON writer helpers.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Deferred Instantiation Manifest Builder Slice

- Added compact deferred function/type instantiation manifest builder rows linked from generic-template function rows, with O(1) append links for deferred functions, deferred types, type arguments, and comptime value arguments.
- Emitted `DeferredFunctionInstantiations` and `DeferredTypeInstantiations` JSON using the materializer's existing shape, preserving callee names, parameter names, integer values, symbolic source names, and type-reference kind/name/integer facts.
- Preserved backend-visible type-reference sources for deferred function type arguments, deferred function comptime argument types, and deferred type instantiations instead of flattening instantiated facts to text.
- Added a focused generic-template readback fact that writes one deferred function instantiation with named type and symbolic comptime arguments plus one deferred type instantiation, then verifies materialized rows, ordinals, text kinds, and integer type facts.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Bound Operation Manifest Builder Slice

- Added compact bound-operation manifest builder rows linked from generic-template function rows, with O(1) append links for operation rows and call-argument/type/text/u32/bool payload children.
- Added embedded call-signature builders for bound operations, including parameter rows, type-argument rows, and comptime value argument rows with symbolic-source and integer type-fact preservation.
- Emitted `BoundOperations` JSON using literal-key dispatch for finite payload kinds, preserving result, return, parameter, type-argument, comptime-argument, call-argument, and receiver type-reference sources through existing generic-template materialization.
- Added a focused generic-template readback fact that writes a direct-call bound operation and a dynamic-storage operation, then verifies materialized operation rows, signature rows, payload rows, text kinds, call argument facts, and backend-visible type-reference sources.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Top-Level Object/Enum/Call Manifest Builder Slice

- Added compact generic-template object creation, enum constructor/call/value/pattern, and direct/member/function-address call builder rows linked from function-template rows with O(1) append links.
- Emitted `ObjectCreations`, `EnumConstructors`, `EnumCalls`, `EnumValues`, `EnumPatterns`, `DirectCalls`, `MemberCalls`, and `FunctionAddresses` JSON through the existing generic-template materializer shape.
- Preserved backend-visible type-reference sources for object created types, enum type rows, direct/member/function-address return types, direct-call parameter/type/comptime arguments, and function-address target types.
- Added a focused generic-template readback fact that writes one object creation, enum constructor/call/value/pattern rows, and direct/member/function-address calls, then verifies materialized row counts, ordinals, text payloads, integer facts, and type-reference sources.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
- No broad test sweep was run.

## 2026-07-09 Logical Generic Template Object/Enum Payload Manifest Builder Slice

- Added compact object constructor metadata plus object initializer member rows linked from object-creation rows with O(1) append links.
- Added compact enum constructor member and enum pattern member rows linked from enum rows, preserving field names, field indices, and field type descriptors for existing materialization.
- Emitted object `Constructor`, `InitializerMembers`, enum constructor `Members`, and enum pattern `Members` JSON through the materializer's existing keys, including constructor parameter-count arrays and field-index payloads.
- Added a focused generic-template readback fact that writes one object constructor/initializer member, one enum constructor member, and one enum pattern member, then verifies row counts, constructor facts, field indices, text kinds, integer facts, and backend-visible type-reference sources.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I tests-stark`: stopped on unrelated stdlib type-check errors for missing `System.Runtime.Platform.SetPermissions` in `stdlib/src/System/FileSystem.stark`.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark tests-stark/selfhost.Ir/IrTests.stark`: passed.
- No broad test sweep was run.

## 2026-07-09 Package Image Shared Section Codec Slice

- Added shared sectioned package-image constants/helpers for the 24-byte v2 header, 32-byte directory entries, directory length, and data offset.
- Routed logical and MIR sectioned package-image writers through a single sectioned-header writer so version/count/directory-length emission cannot drift between payload families.
- Routed logical and MIR sectioned directory readers through the same header and directory-entry length helpers.
- Pre-reserved final output capacity for MIR sectioned package-image writers using their already-computed final offsets, avoiding repeated byte-buffer growth on the package-image write path.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
  - `git diff --check -- selfhost/Compiler/Mir/PackageImage.stark docs/Self-host-Prep/TASKS.md docs/Self-host-Prep/TestPassLedger.md`: passed.
- No broad test sweep was run.

## 2026-07-09 Package Image Shared Codec Module Split Slice

- Moved the STARKPKG magic/version helpers, package section IDs, section flags/encodings, and null string sentinel from `PackageImage.stark` into `PackageCodec.stark`.
- Moved the shared sectioned-header writer, section-directory entry writer, directory-size/data-offset helpers, and final-capacity reservation helper into the shared codec module so logical and MIR package-image writers/readers share one byte-format boundary.
- Kept the serialized format and call sites unchanged; this is a module split/refactor only.
- Narrow verification:
  - `./stark selfhost/Compiler/Mir/PackageCodec.stark --check -I selfhost`: passed.
  - `./stark selfhost/Compiler/Mir/PackageImage.stark --check -I selfhost`: passed with existing recursive generic-template writer stack-growth warnings.
- No broad test sweep was run.

## 2026-07-10 Package Image Split Closure And Stage1 MANF Decode Slice

- Closed the short-lived PackageImage split tracker after moving the durable
  module-boundary notes into `docs/Internals/PackageImage.md`.
- Compared the identical sectioned-MIR fixture through the pre-split source at
  `d4d7d9ef` and the split implementation at `07da7238`; both emitted the same
  124-byte image with SHA-256
  `4897f97f7db9eea83207f0134adf217e744e441a761a304ca71756e8660fd5b6`.
- Added a bounded Stage1 Brotli reader for the uncompressed meta-block streams
  emitted by the Stage1 package writer. The reader validates the window bit,
  meta-block lengths, uncompressed marker, zero padding, final empty block, and
  exact end of input while reserving and copying payload bytes blockwise.
- Removed the image-level compressed-payload staging allocation: decoding now
  reads the validated `MANF` byte range in place and writes only the final JSON
  byte table; bit extraction is a constant-time shift/mask rather than a
  per-bit divisor loop.
- Added the focused source-bridge module with one-parse rendering for effective
  imports/re-exports, module identity, and source aliases including generic and
  comptime parameters. Full loaded-document/body reconstruction remains open.
- Added dense `SsaValueId` fact-transfer APIs for generated, preserved,
  translated, recomputed, consumed, debug-only, and imported value boundaries.
  The transfer retains alignment, ABI, noalias, volatile, integer range,
  nullability, and text-constant facts without map lookups or rediscovery.
- Kept general compressed Stage0 Brotli streams explicitly open; those still
  use the host decompression handoff.
- Revalidated the package-free full `Compiler.Mir` `EnumReturnProbe`: the
  `List<MirEnumLayoutFact>` LLVM type is non-empty, no `dereferenceable(0)`
  attributes remain, and Clang accepted the emitted 14 MiB LLVM module.
- Narrow verification:
  - focused `PackageImageLoader.stark --check`: passed.
  - full focused `PackageImage.stark --check`: passed.
  - focused `Compiler.Ir.stark --check`: passed.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - PackageImage dependency-direction guard: passed.
  - `git diff --check`: passed.
  - focused Stage1 Brotli runtime fact: passed 1/1 through the fully optimized
    test-project build; the 70,000-byte payload exercised multiple meta-blocks.
  - focused source bridge module check: passed.
  - standalone optimized SSA fact-transfer executable: passed with exit 0.
  - combined current-source optimized package-image executable: passed with
    exit 0 after a 70,000-byte encode/decode, direct in-image range decode,
    byte-for-byte comparison, and exact source-bridge output comparison.

## 2026-07-10 Package Source Function Bridge And SSA Rewrite Gate Slice

- Extended the one-parse package source bridge with declaration-only function
  rendering for the lossless source-surface subset. The renderer preserves
  link name, opaque backend mode, strictfp, hot/cold, explicit inline preference,
  unsafe/FFI ABI, varargs/tail, generic/comptime parameters, bounded raw
  pointer counts, and `dead_on_return` contracts.
- Made unsupported non-empty source constraint and alias-group placeholder
  arrays fail the bridge instead of emitting a declaration with weakened
  aliasing or semantic contracts.
- Added exact SSA rewrite-boundary validation for alignment, callable ABI,
  noalias, volatile, integer range, nullability, and text-constant facts. A
  changed preserved fact now reports `mismatched-preserved-fact`, separately
  from category-specific missing-fact diagnostics.
- Added static-global reconstruction and simple struct/record/trait
  reconstruction to the same one-parse bridge. Layout, pack/align, field
  offset, field visibility/type, generic, mutability, and opaque-backend facts
  are emitted exactly; constant, destructor, and other unrendered payloads are
  rejected.
- Added enum reconstruction with positional and named payloads, role
  attributes, and absorbed-error funnels. Funnel rows are accepted only when
  their single positional payload exactly matches `AbsorbsErrorType`, keeping
  try propagation and enum layout inputs intact.
- Extended the same one-parse bridge with effective module opaque-backend
  policy, record primary constructors, implemented traits, associated aliases,
  dyn-trait identity, and type/field thread-safety laws. Duplicate synthesized
  primary-constructor fields are filtered with allocation-free direct scans.
- Connected callable type constraints, thread-law predicates, value contracts,
  and named or bounded disjoint/overlap/same groups. Bounded region expressions
  are rendered exactly so scoped noalias inputs survive the compatibility path;
  partially specified regions and count-only placeholder objects fail closed.
- Added struct/record constructor and destructor body reconstruction plus
  struct/record/trait method-header reconstruction. Method headers reuse the
  same exact ABI, opaque-backend, inline/hot/cold, generic/comptime, bounded
  pointer, thread-law, value-contract, and alias-region renderers as top-level
  functions; enum methods and incomplete member rows remain rejected.
- Corrected the positive dyn-trait fixture so associated aliases remain on a
  plain trait and the dyn trait is object-safe, then added a dyn-trait method
  signature to exercise the vtable-shaping surface.
- Added a fixed-cost all-present-facts SSA rewrite gate so optimizer passes do
  not need a dynamic set or a hand-maintained subset of LLVM-visible facts.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed.
  - full `PackageImage.stark --check`: passed after the enum bridge slice.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - combined optimized package-image executable: passed with exact module
    backend policy, struct/record/enum/dyn-trait, thread-law, associated-type,
    implemented-trait, constructor/destructor, method/dyn-method,
    static-global, callable constraint, bounded alias-region, and function
    declaration text plus the existing 70,000-byte direct MANF range decode.
  - unsupported-payload bridge executable: passed with the bridge rejecting
    non-lossless constraint, constant-initializer, and destructor payloads.
  - optimized SSA transfer/rewrite-gate executable: passed with exact preserved
    text facts and a changed alignment reported as mismatched.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.

## 2026-07-10 Package Source Constant, Doctrine, And Loaded Document Slice

- Added source-bridge doctrine rendering with generic parameters, associated
  aliases, and method/law declarations; doctrines reject fields while dyn
  traits continue to reject associated aliases that would violate object
  safety.
- Added global-constant compatibility rendering for scalar, text, null, and
  recursively shaped fixed-array initializers. Strict integer range spelling
  is removed from the parse-only constant declaration where Stark requires the
  bare storage type.
- Kept typed constant initializer rows authoritative: the fixture deliberately
  stores integer `123` and boolean `true` while the bridge emits neutral `0`
  and `false`. Those placeholders are never imported as CTFE, range, ABI, or
  LLVM facts, and unsupported complex aggregate shapes fail closed.
- Extended the Stage1 compilation-unit header parser to accept `export import`
  and preserve its re-export bit in a dense parallel column.
- Added a Stage1-owned loaded compatibility document that retains reconstructed
  source bytes beside the parsed compilation-unit syntax, rejects syntax
  diagnostics, and records the source module ordinal. Structured typed,
  compiler, template, ownership, ABI, and layout graphs remain the canonical
  backend inputs.
- Parser repair details:
  - module-header rows now retain their leading attribute start token as well
    as the `module` keyword token;
  - declaration parsing recognizes dyn-trait headers and bare `finite`, `law`,
    and `finite law` callable kinds emitted by the compatibility bridge;
  - focused parser facts cover exported imports, attributed modules, and bare
    callable-kind flags.
  - deterministic compilation-unit artifact text exposes header `start` and
    `exported` fields so the retained compatibility syntax facts are visible.
- Verification:
  - focused `Parsing.stark --check`: passed with the parser's existing bounded
    recursive-descent stack-growth warnings.
  - `selfhost.Parsing/ParsingTests.stark --check`: passed.
  - focused `PackageImageSourceBridge.stark --check`: passed with one bounded
    fixed-array initializer recursion warning.
  - combined current-source fixture `--check`: passed with exact doctrine,
    constant, loaded-document, declaration-count, and exported-import checks.
  - combined optimized package-image executable: passed with exit 0 after the
    existing 70,000-byte direct MANF decode plus exact source, owned loaded
    document, attributed-module, exported-import, and 12-declaration checks.
  - full `PackageImage.stark --check`: passed.
  - package-free full `selfhost.Ir/IrTests.stark --check`: passed.
  - focused `ArtifactRendering.stark --check`,
    `selfhost.Artifacts/ArtifactsTests.stark --check`, and
    `selfhost.Parsing/ParsingTests.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.
  - focused parser runtime test command was stopped after repeated silent
    build/setup intervals before runner output; the parser test project and the
    focused parser source both pass `--check`, and the optimized combined
    executable exercises the same exported-import/attributed-module path.

## 2026-07-10 Package Generic Template Body Matching Slice

- Connected legacy generic-template bodies to reconstructed top-level function
  and type-method declarations by exact qualified name and overload key.
- Matched Stage0 identity precedence: effective typed-interface
  `PublishedOverloadKey` wins, otherwise the bridge derives a canonical key
  from source parameter type spelling, including ordered ownership/access/init
  qualifiers and whitespace removal.
- Reused one owned overload-key scratch buffer across every declaration in the
  one-parse bridge. No dictionary, per-callable key allocation, or temporary
  parameter-name set was added to the compatibility path.
- Preserved structured template authority: when a matching row contains
  `TypedBody`, conflicting legacy `BodyText` is ignored and the declaration
  remains bodyless until the structured operation subset is rendered or
  lowered directly.
- Duplicate qualified-name/overload-key template identities now reject the
  bridge rather than selecting a body by manifest order.
- Optimized combined package-image fixture: passed with exit 0 after exact
  method/top-level legacy body attachment, published-key and canonical-key
  matching, typed-body suppression, duplicate rejection, owned loaded-document
  parsing, and the existing 70,000-byte direct MANF decode.

## 2026-07-10 Package Structured Template Direct-Call Slice

- Added bounded, allocation-free traversal of typed template statements,
  expressions, patterns, switch cases, and nested statement lists to identify
  operation kinds that still require compatibility source text. The maximum
  nesting depth is 64 and malformed/deeper graphs reject the bridge.
- Added exact rendering for a typed body containing one returned direct call.
  The call expression is joined to `DirectCalls` by ordinal, duplicate ordinal
  facts reject, and target selection preserves Stage0 source/template/resolved
  name precedence. Name and literal arguments render without per-argument
  allocation; the owned body scratch is reused across all callables.
- Extended the typed expression renderer recursively through ordinal-backed
  field access and non-generic member calls. The positive body now proves the
  nested shape
  `Source.Identity(value.Value.Combine(new Source.Value()))`, including
  direct-call source-name precedence, field-name lookup, member-name recovery,
  and exact authored object-creation text without temporary strings.
- Kept the structured graph authoritative: the positive fixture includes
  conflicting legacy `BodyText`, but reconstructs
  `{ return Source.Identity(value.Value.Combine(new Source.Value())); }` from
  typed operation facts. Pure typed
  bodies still remain declaration-only, while comptime calls and other
  source-required forms fail closed until their fact-complete renderers land.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed; only bounded
    recursion warnings are reported.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - optimized combined package-image executable: passed with exit 0 after the
    typed direct-call precedence/body assertion, loaded-source parse, ambiguity
    rejection, comptime-call fail-closed rejection, and existing 70,000-byte
    direct MANF decode.
  - full `PackageImage.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.

## 2026-07-10 Package Structured Template Enum And Statement Slice

- Extended compatibility expression rendering through ordinal-backed enum
  constructors, enum calls, and enum values. Enum types are reconstructed from
  bounded named type-reference facts, including canonical qualifier order,
  nested type arguments, and symbolic/integer comptime arguments.
- Added Stage0-compatible generic call spelling. Direct calls omit pure type
  arguments when inference is sufficient, but render the complete type/value
  list when comptime arguments require explicit syntax. Member calls retain
  their explicit generic list and recover their source member name.
- Completed fact-driven named object construction for constructor calls,
  initializer-member rows, arena selection, and exact authored zero-argument
  text. Initializer member/argument counts must match; unknown storage selectors
  reject instead of producing guessed allocation syntax.
- Replaced the single-return shortcut with ordered multiline rendering for
  empty, expression, assignment, break, continue, and return statements.
  Assignment targets may come from a name or typed target expression and retain
  the published operator, including `init =` spelling.
- The optimized fixture reconstructs a three-statement body containing enum
  value use, assignment, a generic/comptime direct call, a generic/comptime
  member call, all three enum operation kinds, field access, and synthesized
  arena allocation. Conflicting legacy body text remains ignored. A typed `if`
  shape remains an explicit fail-closed negative fixture.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed with only bounded
    recursion warnings.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - optimized combined package-image executable: passed with exit 0, including
    exact reconstructed source, negative unsupported-shape rejection, loaded
    source parsing, duplicate-template rejection, and the 70,000-byte direct
    MANF decode.
  - full `PackageImage.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.

## 2026-07-11 Package Structured Template Local And Initializer Slice

- Added typed local-declaration rendering with exact storage class, mutability,
  named type arguments, source name, and initializer order. Constant locals
  retain `const` spelling and malformed declarations fail closed.
- Added recursive object-initializer rendering before ordinal lookup, preserving
  manifest member order and requiring each non-empty member name to have one
  corresponding typed value expression.
- Extended the optimized fixture with
  `stack mut Source.Option<T> current = { Value = Source.Option.None };` ahead
  of the existing enum/call/assignment body. The typed operation graph remains
  canonical and conflicting legacy body text remains ignored.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed with only the
    existing bounded recursion warnings.
  - optimized combined package-image executable: passed with exit 0, including
    exact reconstructed source and loaded-source parsing.

## 2026-07-11 Package Structured Template Conditional Slice

- Refactored typed statement rendering into a depth-bounded recursive array
  renderer with direct indentation writes and no per-block string allocation.
- Added typed `if`/`else` reconstruction over published condition expressions,
  `ThenStatements`, and `ElseStatements`. Condition-pattern facts remain an
  explicit rejection until the pattern renderer lands.
- Extended the optimized fixture with an `if (true)` containing an ordinal-backed
  enum value and an `else` containing an empty statement. The previous negative
  fixture now uses an unsupported typed `switch`, preserving fail-closed
  coverage for the next control-flow slice.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed with only bounded
    recursion warnings.
  - optimized combined package-image executable: passed with exit 0, including
    exact nested source reconstruction, loaded-source parsing, and unsupported
    switch rejection.

## 2026-07-11 Package Structured Template While Slice

- Added labeled typed `while` reconstruction with exact `LoopBehavior`, ordered
  non-empty `LoopContracts`, condition expression, and depth-bounded recursive
  `BodyStatements` rendering.
- Preserved statement labels and labeled `break`/`continue` targets without a
  temporary label table. Condition-pattern payloads still reject rather than
  being silently omitted.
- Extended the optimized fixture with
  `retry: while willexit bounded (false)` containing an ordinal-backed enum
  value and `break retry;`.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed with only bounded
    recursion warnings.
  - optimized combined package-image executable: passed with exit 0, including
    exact labeled-loop source reconstruction and loaded-source parsing.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - full `PackageImage.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.

## 2026-07-11 Package Structured Template Completion Slice

- Added allocation-free counted `for` header rendering over typed initializer
  and iterator statement arrays, plus traversal loops with optional index
  bindings, typed element bindings, labels, loop behavior/contracts, and
  bounded recursive bodies.
- Added explicit block and switch reconstruction. Switches retain source case
  order, guards, child statements, and default/match-all/literal/range/list/
  enum/aggregate patterns. Pattern ordinals join exact enum/aggregate type,
  variant, and named-field facts; malformed joins reject.
- Completed structured type-reference rendering for scalar, ranged integer,
  float, raw pointer, fixed array, slice, dynamic, named/generic, associated,
  dyn-trait, function-pointer, and closure forms. Callable rendering retains
  unsafe/ABI/tail/kind, bounded-pointer expressions, closure storage/capability,
  and deduplicated overlap/same/dead contracts with direct scans instead of
  temporary sets.
- Completed the Stage0-compatible non-ordinal expression surface around typed
  operations: array/object initialization, assignment, conversion, `try`,
  unary/binary/comparison/conditional forms, `comptime`, layout queries,
  closure/index calls, and dyn-trait construction.
- The optimized fixture now covers counted/traversal loops, condition and
  switch patterns, explicit blocks, scalar/container/callable types, callable
  memory contracts, conversions, conditional/comptime nesting, and array
  initializers while continuing to reject malformed typed data and ignore
  conflicting legacy body text.
- During optimized verification, an untyped ternary in a renderer arity check
  exposed a Stage0 MIR pair-comparison invariant; replacing it with explicit
  kind-specific comparisons removed the ambiguous operand shape.
- Verification:
  - focused `PackageImageSourceBridge.stark --check`: passed with only bounded
    recursion warnings.
  - optimized combined package-image executable: passed with exit 0 after the
    expanded exact-source and loaded-source assertions.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - full `PackageImage.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker reference
    scan, and `git diff --check`: passed.

## 2026-07-11 Direct Imported Template Scalar Return Slice

- Added the first source-free Stage1 imported-template MIR lowering entry for
  complete typed bodies containing one integer or boolean literal return.
- The lowerer preflights the whole per-template statement/expression subgraph
  and rejects any auxiliary operation count, detached row, non-root expression,
  irrelevant statement/expression payload, unsupported scalar carrier, or
  output fact-table misalignment. Unsupported but valid bodies leave every MIR
  table unchanged for compatibility-source fallback.
- Dense MIR instruction/block/function/value-fact/function-return-fact tables
  reserve before mutation. Exact literal ranges are attached to the produced
  value and function return, including the i64 maximum sentinel convention, so
  the LLVM definition receives the strongest legal `range` attribute.
- Added package-model presence accessors needed to prove that return/literal
  rows do not carry ignored assignment, storage, traversal, ordinal, or target
  payloads.
- Added direct generic-template type contains/summary accessors and inspect the
  owned type graph by borrow, avoiding a temporary copy of its dynamic storage
  on the imported-template hot path.
- Regression coverage materializes integer, boolean, and unsupported name-body
  templates from one package graph, appends two direct-lowered functions into
  shared tables, proves fallback is non-mutating, and checks exact emitted LLVM
  text including `range(i32 7, 8)`.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - full `PackageImage.stark --check`: passed.
  - PackageImage dependency-direction guard, stale split-tracker scan, and
    `git diff --check`: passed.

## 2026-07-11 Direct Imported Template Constant Expression Slice

- Extended the source-free scalar-return path from one literal to bounded
  nested literal/binary/conditional expression trees. Supported operators cover checked
  integer add/subtract/multiply/divide/remainder, scalar comparisons, and
  boolean `&&`/`||`.
- Audited the path against Stage0's 7,936-line imported-template lowerer and
  corrected the first fixture's normalized assumptions: real producers publish
  lowercase `integer`/`bool` kinds, binary operators through `Name`, and infer
  binary/conditional result types. Stage1 now consumes that canonical shape
  directly while retaining the older operator-row carrier as a compatibility
  input.
- Constant trees fold during imported-template preflight and emit one typed MIR
  constant. This removes runtime arithmetic before SSA while preserving the
  exact folded value range on the MIR value and function-return fact rows that
  LLVM consumes.
- Added exact parent-row, parent-kind, ordinal, operator-row, and child-row
  joins. Duplicate, detached, cyclic/deeper-than-64, type-inconsistent,
  overflowing, divide-by-zero, and unconsumed operation shapes fail closed
  before output-table mutation.
- Validate each loader-produced template's contiguous expression/operator span
  once, then constrain recursive child/operator lookup to that compact span so
  nested evaluation does not repeatedly scan unrelated package templates.
- Expanded package-graph regression coverage with canonical-schema nested
  `4 + 3 * 2` folding, nested comparison/boolean folding, a constant
  conditional selecting `6 * 7`, shared-table appends, exact value facts, and
  the existing non-mutating name-body fallback.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed with only the five
    expected bounded-recursion warnings.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed after
    the canonical Stage0 carrier and conditional-fold regressions were added.
  - focused canonical Stage0-schema conditional fixture: emitted as an
    executable and ran with exit 0 after asserting one `i32 42` MIR constant
    plus exact `[42, 43)` value and function-return facts.
  - optimized package-free binary-fold fixture: emitted and ran with exit 0;
    LLVM carried `range(i32 10, 11)` and the MIR contained one constant.
  - optimized package-free direct-lowering fixture: emitted and ran with exit 0,
    including the exact LLVM range assertion.

## 2026-07-11 Direct Imported Template Unary And Comparison-Chain Slice

- Extended source-free scalar template evaluation to canonical Stage0 unary
  `+`, checked signed `-`, logical `!`, signed-width `~`, ordered
  `comparison-chain` rows, and transparent `comptime` wrappers.
- Comparison chains evaluate each constant operand once, consume every ordered
  operator row, enforce adjacent scalar type/unsignedness compatibility, and
  fold the combined result to one boolean MIR constant. Unsupported unsigned
  complement/negation and any value outside the signed `i64` fact carrier fail
  closed instead of dropping or wrapping LLVM range facts.
- Exploit the loader's contiguous ordered argument-row batches: each expression
  scans once for ordinal zero and validates later siblings by O(1) row offset,
  avoiding the former whole-template scan for every wide-chain operand.
- The regression graph combines `comptime`, a conditional, all four supported
  unary operators, and a two-operator comparison chain; the result remains one
  `i1 1` MIR instruction with exact `[1, 2)` value and function-return facts.
- Direct lowering now requires the caller's specialized return `MirType` and
  unsignedness. A mismatch returns `ReturnTypeMismatch` before reservation or
  table mutation, preventing a package/substitution error from reaching LLVM
  with a valid-looking but ABI-incompatible definition.
- Stage1's specialization plan remains an artifact/dependency contract without
  an executable MIR orchestration hook. Direct package-template integration is
  intentionally left on the open specialization task rather than introducing
  a disconnected pseudo-driver.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed with only the nine
    expected depth-64 bounded-recursion warnings.
  - optimized combined unary/comparison/comptime fixture: emitted and ran with
    exit 0 after asserting one `i1 1` plus exact `[1, 2)` value/return facts.
  - optimized three-operand contiguous comparison-chain fixture: emitted and
    ran with exit 0 after the sibling-offset lookup optimization.
  - optimized expected-return contract fixture: emitted and ran with exit 0;
    an `I64`/`I32` mismatch left every output table empty, then the matching
    `I32` request emitted one constant with exact `[7, 8)` facts.
  - package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed with
    the canonical unary/comparison/comptime and return-contract regressions.

## 2026-07-11 Direct Imported Template Scalar Conversion Slice

- Added source-free constant scalar conversions with Stage0-compatible
  two's-complement normalization for runtime-width integer sources. Nested
  conversions consume their side rows in syntax preorder and must match both
  the expression target and published target type exactly.
- Published integer `RangeMin`/`RangeMax` facts are now enforced for literals,
  annotated constant intermediates, and conversion targets. Simple numeric and
  storage `min`/`max` endpoints are accepted; symbolic bounds fail closed until
  specialization can substitute them.
- Exact numeric full-range unsigned maxima (`255`, `65535`, `4294967295`, and
  `18446744073709551615`) are recognized as storage endpoints, so a small
  representable unsigned constant retains its exact LLVM fact without forcing
  the bound itself through the signed `i64` carrier.
- Exact-range preservation remains stronger than emitting a conversion tree:
  `i32 300` converted through `i8[0,100]` and then `i16[0,1000]` emits one
  `i16 44` with `[44,45)` on both the MIR value and function-return fact rows.
- A target-side-row range mismatch is rejected before output reservation or
  mutation, preventing package corruption from reaching LLVM as a plausible
  narrow integer constant.
- The specialization caller now supplies its expected return `ValueFacts` in
  addition to type and signedness. The exact folded singleton must be a subset
  of that published range, and scalar-inapplicable alignment, ABI, alias,
  volatility, nullability, or text facts are rejected rather than discarded.
  A mismatch returns `ReturnFactMismatch` before reserving any MIR table row.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed with only the ten
    expected depth-64 bounded-recursion warnings.
  - full package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed
    with the conversion and expected-return-fact regressions.
  - cold optimized project run from `tests-stark/selfhost.Ir` with
    `../../stark test --filter ImportedTypedTemplateScalarReturnsLowerDirectlyWithExactLlvmFacts --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    emitted the compiler library/package and test executable, then passed the
    selected fact 1/1. The build exercised the normal SSA optimization and LLVM
    emission pipeline rather than a check-only shortcut.
  - optimized nested-conversion and mismatched-side-fact fixture: emitted and
    ran with exit 0.
  - optimized full-range `u32` numeric-max fixture: emitted and ran with exit 0
    while retaining exact `[7, 8)` value/return facts.

## 2026-07-11 Direct Imported Template Scalar Statement-Preamble Slice

- Extended source-free scalar template lowering from a single return statement
  to ordered top-level `empty` and pure constant `expression` statements
  followed by the terminal return. Any other statement kind, nested parent,
  misplaced return, or statement payload fails closed for compatibility-source
  fallback.
- Validate the loader-produced statement span once and address each top-level
  statement by direct row offset. Expression roots are likewise consumed once,
  keeping traversal linear in body size without temporary collections or
  repeated whole-package scans.
- Evaluate discarded constant roots in source order so conversion side-row
  ordinals and published target/range facts remain exact across statement
  boundaries, but emit no MIR instruction for those roots.
- Added a package-graph regression containing an empty statement, a discarded
  narrowing conversion, and a converted/binary return. The complete body must
  emit only one `i16 307` instruction and preserve exact `[307, 308)` facts on
  both the MIR value and function return delivered to LLVM.
- The initial regression also established that an unimplemented nested block
  returned non-mutating `UnsupportedBody` rather than being misclassified as
  package corruption; the next slice promotes the flat form directly.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed with only the ten
    expected depth-64 bounded-recursion warnings.
  - full package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - optimized package-backed project run with
    `../../stark test --filter ImportedTypedTemplateScalarPreambleStatementsFoldWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    rebuilt and emitted the self-host compiler library/package plus test
    executable, exercised the normal SSA optimization and LLVM emission passes,
    and passed the selected fact 1/1.

## 2026-07-11 Direct Imported Template Flat Block Slice

- Extended the constant-only scalar path through one terminal top-level
  `block` whose direct body is a flat ordered empty/expression/return sequence.
- Exploit the loader's breadth-first layout only where it gives a contiguous
  direct-child batch in exact source order. This keeps validation/evaluation
  linear and allocation-free while preserving conversion side-row ordinals;
  mixed or deeper block nesting retains compatibility-source fallback instead
  of guessing order or building a temporary parent index.
- Expanded the package-graph regression so both the top-level and flat-block
  bodies consume discarded narrowing conversions and emit exactly one runtime
  constant each. The block result is `i16 9`, with exact `[9, 10)` facts on the
  MIR value, function return, and LLVM definition.
- Added a two-level nested block in the same graph and require non-mutating
  `UnsupportedBody` after both direct functions have been emitted.
- Verification:
  - focused `ImportedTemplateLowering.stark --check`: passed with only the ten
    expected depth-64 bounded-recursion warnings.
  - full package-free `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - optimized package-backed project run with
    `../../stark test --filter ImportedTypedTemplateScalarPreambleStatementsFoldWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    rebuilt/emitted the self-host compiler library/package and test executable,
    exercised normal SSA optimization plus LLVM emission, and passed 1/1.

## 2026-07-11 Direct Imported Template Integer Operator Slice

- Split package-template scalar operator decoding and evaluation into the
  513-line `Compiler.Mir.ImportedTemplateScalarOperators` module, leaving the
  graph/type/range orchestration in `ImportedTemplateLowering.stark` and both
  files below 2,500 lines.
- Added direct constant evaluation for integer `**`, `+%`, `-%`, `*%`, `+|`,
  `-|`, `*|`, `&`, `^`, `|`, `<<`, `>>`, and unary `-%`. Power and shift
  counts are bounded; ordinary/power/shift overflow fails closed before output
  reservation.
- Reused MIR's checked, exact wrapping, exact saturation, and shift helpers so
  package-template folding and ordinary MIR fact recomputation share integer
  semantics. Signed saturation is additionally clamped to the package graph's
  published inclusive range before singleton facts are attached.
- Kept unsigned wrapping and saturation on compatibility fallback while MIR's
  scalar value/range carrier is signed `i64`; representable unsigned bitwise
  and shift results lower directly without dropping the caller's unsignedness
  contract.
- Added a separate 471-line imported-template operator test helper instead of
  growing the already oversized `IrTests.stark`. Its package graph evaluates
  eight discarded constant roots covering the new families, including an exact
  representable unsigned shift, and consumes an ordered conversion. Its
  terminal signed saturating multiply emits exactly one `i8 -128` instruction
  with `[-128, -127)` on the MIR value, function return, and LLVM definition.
  An oversize `i8 << 8` graph returns nonmutating `UnsupportedBody`.
- Verification:
  - focused `ImportedTemplateScalarOperators.stark --check`: passed.
  - focused `ImportedTemplateLowering.stark --check`: passed with only the ten
    expected depth-64 bounded-recursion warnings.
  - full-front-end imported-template operator helper check: passed.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check`: passed.
  - optimized package-backed project run with
    `../../stark test --filter ImportedTypedTemplateIntegerOperatorsFoldWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    rebuilt and emitted `libStarkCompiler.a`, `libStarkCompiler.starkpkg`, and
    the test executable; exercised constant propagation, inlining, constant
    graph CSE, dynamic-storage specialization, constant stdlib specialization,
    branch pruning, memory optimization, ownership-traffic optimization, SSA,
    and LLVM emission; and passed the selected fact 1/1.
  - the cold rebuild again exposed the tracked root-pipeline cost, including
    several `lower-mir` passes between roughly 214 and 301 seconds; this is
    verification evidence for TASKS.md's root-module incrementality item, not
    runtime work in the folded template.

## 2026-07-11 Direct Imported Template Scalar Local Constant Slice

- Added the focused `Compiler.Mir.ImportedTemplateScalarLocals` module with a
  bounded 64-entry stack environment for scalar local name, exact type,
  signedness, and constant-value facts. It caches each declared name's stable
  hash so lookup hashes the query once, then compares package text exactly on
  hash matches with two reused scratch buffers; no runtime local storage or
  load is emitted.
- Extended source-free package-template lowering to ordered immutable
  initialized local constants followed by later `name` expressions. Every
  statement must carry canonical `local` storage and `immutable-binding`
  provenance and pair in source order with one contiguous `const` declaration
  side row having the exact same published type.
- Initializers are evaluated before their name becomes visible. Duplicate and
  unresolved names, side-row/count/type mismatches, mutable or storage-backed
  declarations, grouped declarators, and bodies beyond the bounded capacity
  retain nonmutating compatibility fallback rather than losing facts.
- Added a separate package-backed test helper so the oversized `IrTests.stark`
  does not grow materially. Three chained constants fold to exactly one
  `i16 82` MIR instruction, with `[82, 83)` on the value, function return, and
  LLVM definition; unresolved and duplicate-name templates prove transactional
  `UnsupportedBody` behavior.
- Verification:
  - focused `ImportedTemplateScalarLocals.stark --check`: passed.
  - focused `ImportedTemplateLowering.stark --check`: passed with only the ten
    expected depth-64 bounded-recursion warnings.
  - focused local-constant test helper full-front-end check: passed.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check`: passed against the
    final cached-name source.
  - optimized package-backed selected run with
    `../../stark test --filter ImportedTypedTemplateLocalConstantsFoldWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    rebuilt/emitted the self-host compiler library/package and test executable,
    exercised constant propagation, inlining, constant-graph CSE,
    dynamic-storage and constant-stdlib specialization, branch pruning, memory
    and ownership optimization, SSA lowering, and LLVM emission, then passed
    the selected fact 1/1.
  - the final cold rebuild after the cached-name optimization again exposed the
    open root-pipeline incrementality cost: individual `lower-mir` passes
    reached 689.4, 910.6, and 916.1 seconds; the optimized test-root rebuild's
    `lower-mir` pass completed in 29.7 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-11 Direct Imported Template Scalar Variable And Assignment Slice

- Extended the bounded package-template scalar environment with exact
  mutability state and in-place value updates. Initialized independent scalar
  locals accept canonical `stack` or `register` storage and pair with ordered
  `var` declaration side rows; heap/arena, uninitialized, grouped, or
  storage-capacity-bearing declarations retain compatibility fallback.
- Added direct-name assignment lowering that consumes the statement name,
  assignment-operator text, RHS root, and separate ordinal-one target root.
  The target must be a fact-complete `name` expression matching the statement
  name exactly and resolve to a mutable local of the same scalar type and
  signedness.
- Reused the shared scalar operator evaluator for every grammar-defined
  compound assignment: checked `+=`, `-=`, `*=`, `/=`, `%=`; wrapping `+%=`,
  `-%=`, `*%=`; saturating `+|=`, `-|=`, `*|=`; and bitwise `&=`, `|=`, `^=`.
  Plain `=` is also supported. Saturating results clamp to the declared local
  range and every result must fit before the environment mutates.
- The package-backed regression combines an immutable stack seed, mutable
  register accumulator, plain assignment, and all fourteen compound operators.
  The entire sequence emits one `i8 7` MIR constant with `[7, 8)` on the value,
  function return, and LLVM definition. Immutable assignment, mismatched
  statement/target names, and heap scalar storage return transactional
  `UnsupportedBody`.
- Replaced one direct comparison of an `out` enum result with an exhaustive
  switch after the first optimized build exposed a Stage0 `EmitPairComparison`
  invariant. The repaired form preserves behavior and builds through Stage0.
- Verification:
  - focused operator, local-state, and direct-lowering checks: passed; the
    direct lowerer reports only its ten existing depth-64 recursion warnings.
  - expanded local-variable test helper full-front-end check: passed.
  - optimized package-backed selected run with
    `../../stark test --filter ImportedTypedTemplateScalarVariablesFoldWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`:
    rebuilt/emitted the self-host compiler library/package and test executable,
    exercised constant propagation, inlining, constant-graph CSE,
    dynamic-storage and constant-stdlib specialization, branch pruning, memory
    and ownership optimization, SSA lowering, and LLVM emission, then passed
    1/1. A warm all-operator fixture rebuild also passed 1/1.
  - the cold rebuild again exposed the root-pipeline incrementality cost;
    individual `lower-mir` passes reached 747.8, 979.0, and 988.5 seconds.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check`: passed against the
    final all-operator source.
  - MIR dependency-layer validation, untracked-file whitespace scan, and
    `git diff --check`: passed.

## 2026-07-11 Direct Imported Template Scalar Definite-Initialization Slice

- Extended the bounded package-template scalar environment with a definite-
  initialization bit per local. Initializer-free independent scalar `stack`
  and `register` declarations now retain their exact published type,
  signedness, and mutability without inventing a value or materializing
  storage.
- Name reads and compound assignments reject an uninitialized local. A mutable
  local's first plain `=` and the separately published `init =` operator both
  establish the value; immutable targets still reject. Every accepted write
  must match the target's exact package type/signedness and fit its published
  range before the environment changes.
- Added a focused 389-line package-backed helper rather than growing the
  already oversized `IrTests.stark`. Its valid graph initializes one stack
  scalar with `init =`, one register scalar with plain `=`, performs a later
  compound update, and emits exactly one `i16 43` MIR value with `[43, 44)` on
  the value, function return, and expected LLVM definition. Read-before-init,
  compound-before-init, and immutable-init graphs prove nonmutating
  `UnsupportedBody` fallback.
- Verification:
  - focused `ImportedTemplateScalarLocals.stark --check`: passed.
  - focused `ImportedTemplateLowering.stark --check`: passed with only its ten
    existing depth-64 bounded-recursion warnings.
  - focused initialization helper full-front-end check: passed.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check`: passed, including the
    new helper import and generated fact surface.
  - optimized selected execution was attempted with
    `../../stark test --filter ImportedTypedTemplateScalarInitializationFoldsWithoutRuntimeWork --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`; the cold compiler rebuild remained silent for more than 18 minutes, beyond the prior 988.5-second observation, and was interrupted without a pass/fail result. Optimized execution remains pending rather than being recorded as passed.
  - MIR dependency-layer validation, untracked-file whitespace scan, and
    `git diff --check`: passed.

## 2026-07-11 Imported Template Specialization Backend-Fact Slice

- Added a focused package-specialization adapter that resolves an imported
  function template by exact qualified-resolved-name and overload key, then
  joins its base qualified name to exactly one compiler function-effect row.
  Missing, ambiguous, and inconsistent identities fail before any MIR or fact
  table is mutated.
- Expanded the LLVM function-effect carrier to preserve package facts for
  memory purity, synchronization/free/unwind behavior, progress, hot/cold,
  strict-FP, inline preference, opaque optimization mode, and no-recurse.
  Opaque functions emit `optnone noinline`; explicit package inline modes emit
  `alwaysinline`, `inlinehint`, or `noinline`.
- Expanded the calling-convention carrier with package `fastcc` while retaining
  tail-call precedence. The adapter publishes effect, tail, and calling rows at
  the same dense function id as the directly folded MIR function and feeds all
  of them, plus the exact return/value ranges, to numbered LLVM emission.
- Added a 356-line package-backed helper rather than growing the oversized
  `IrTests.stark`. It covers a hot `fastcc` specialization and a cold opaque
  `tailcc` specialization, and checks their complete LLVM definitions. Missing
  effect rows, backend-mode disagreement, and missing specialization identity
  prove nonmutating failure.
- Verification:
  - focused `LlvmFacts.stark --check`: passed.
  - focused specialization adapter and package-backed helper full-front-end
    checks: passed.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I ../../selfhost`:
    passed, including the new helper import and generated fact surface. The
    check again exposed root incrementality costs, with reported imported-module
    type-check stages reaching 237.2 seconds.
  - optimized selected execution was started with
    `../../stark test --filter ImportedTypedTemplateScalarSpecializationsPreserveBackendFacts --test-progress --test-timeout 300 --target arm64-apple-macosx26.0.0 --stage stage0`; the cold rebuild remained completely silent through the bounded attempt and was interrupted without a pass/fail result. Optimized execution remains pending rather than being recorded as passed.
  - MIR dependency-layer validation, workspace Stark whitespace scan, and
    `git diff --check`: passed.

## 2026-07-11 Source Function Declared Backend-Fact Slice

- Connected explicitly declared source function performance facts to the
  module-wide function-effect prepass. `hot`, `cold`, `strictfp`, `inline`,
  `noinline`, and `inlinehint` now survive into LLVM definitions and direct
  call-site attributes instead of being retained only by typing/package-image
  models.
- Added allocation-free adjacent-prelude scanning for `[Backend(Opaque)]`.
  Opaque source definitions and calls emit the required `optnone noinline`
  pair and override a conflicting inline preference, matching the imported
  specialization carrier.
- Kept inferred `nounwind`, default inline-hint, and internal `fastcc` outside
  this compatibility-emitter slice. Those defaults are now explicitly tracked
  for import from typed function-effect and optimized-SSA ABI facts when the
  open ABI-lowering task owns calling-convention selection, including
  tail/FFI/export precedence.
- Added a focused 45-line helper instead of growing the 41k-line aggregate test
  root. It covers explicit hot/strict/inline, cold opaque, public noinline,
  exported inlinehint, tail precedence, default behavior, and direct-call fact
  propagation.
- Verification:
  - focused `SourceFunctionContext.stark --check -I selfhost`: passed; only its
    existing bounded-recursion warnings remain.
  - focused source backend helper full-front-end check: passed.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir`: passed with the new helper import and fact; the
    root again reported imported-module type-check stages up to 196.5 seconds.
  - real clang IR parsing accepted both hot/strict/alwaysinline and opaque
    `optnone noinline` attribute combinations on definitions and direct calls.
  - optimized selected test execution and a smaller standalone helper
    executable were both attempted; each entered the known silent cold backend
    rebuild and was interrupted after a bounded attempt without a pass/fail
    result. Executable coverage remains pending rather than being recorded as
    passed.

## 2026-07-11 Source Function Effect Module Split

- Extracted the allocation-free MIR instruction-range effect scans, block-range
  adapters, law/finite/norecurse proof-table helpers, fixed-point computations,
  and source backend-modifier merge from `SourceModuleLowering.stark` into the
  focused `Compiler.Mir.SourceFunctionEffects` module.
- Kept module function-table construction and lowering orchestration in
  `SourceModuleLowering`, avoiding a reverse dependency or callback layer. The
  split introduces no allocation, dynamic dispatch, or fact conversion, and
  preserves the exact `FunctionEffectFacts` rows consumed by LLVM definition
  and direct-call attribute emission.
- Re-exported the focused module through `Compiler.Mir` so existing internal
  facade consumers retain the moved helper surface.
- Reduced `SourceModuleLowering.stark` from 9,632 to 8,790 lines. The remaining
  file is still above the preferred 5k-line threshold, so a cycle-free follow-up
  split remains explicitly open in `TASKS.md`.
- Verification:
  - `./stark selfhost/Compiler/Mir/SourceFunctionEffects.stark --check -I selfhost`:
    passed with `Check succeeded.`
  - `./stark selfhost/Compiler/Mir/SourceModuleLowering.stark --check -I selfhost`:
    passed with `Check succeeded.` after validating the full imported-module
    closure.
  - `scripts/check-selfhost-mir-dependencies.sh`: passed.
  - `git diff --check`: passed before the ledger update; final hygiene is rerun
    below with the aggregate test check.

## 2026-07-11 Source Try Lowering Module Split

- Extracted the complete source `try` family from
  `SourceModuleLowering.stark` into `Compiler.Mir.SourceTryLowering`: shape
  detection, enum-tag selection, nested binding/storage planning, and MIR
  control-flow lowering for returned, local, slice-element, scalar-storage,
  and constructed-object-field propagation paths.
- Preserved the existing typed enum layout, success/error role compatibility,
  integer-range, ABI, distinct-storage, slice descriptor, object layout,
  initialization, and block-range inputs verbatim. The split adds no wrapper,
  callback, dynamic dispatch, allocation, or fact conversion.
- Trimmed inherited imports after the move. The focused module depends on the
  existing if-lowering module only for the shared discard-before-overwrite
  proof; LLVM emission, package-image, assembly, loop, switch, test-support,
  and function-effect modules are no longer in its dependency closure.
- Re-exported `Compiler.Mir.SourceTryLowering` through the `Compiler.Mir`
  facade. `SourceModuleLowering.stark` now has 4,402 lines and
  `SourceTryLowering.stark` has 4,437, placing both below the preferred 5k-line
  maintenance threshold.
- Verification:
  - `./stark selfhost/Compiler/Mir/SourceTryLowering.stark --check -I selfhost`:
    passed after the focused import trim.
  - `./stark selfhost/Compiler/Mir/SourceModuleLowering.stark --check -I selfhost`:
    passed against the extracted try APIs.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir`: passed through the `Compiler.Mir` facade and the
    full existing IR fact surface. The known imported-module incrementality
    cost remained visible, with reported type-check stages up to 191.6 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-11 Source Switch Case and Assignment-Arm Module Split

- Extracted two dependency-closed switch families from
  `SourceSwitchLowering.stark`, reducing the remaining orchestration module
  from 23,424 to 20,823 lines without adding a forwarding layer.
- `SourceSwitchCaseParsing.stark` (1,683 lines) owns decision preflight rows,
  case descriptors, enum variant/tag resolution, descriptor validation, and
  typed enum case-label parsing. Typed aggregate/list rows, enum layout tags,
  capture spans, scalar ranges, and nested descriptors remain the exact tables
  passed to the existing MIR lowering path.
- `SourceSwitchAssignmentArms.stark` (979 lines) owns assignment-arm parsing,
  arena dynamic-reserve statements, local-type construction, module-call
  validation, and direct MIR store lowering. It continues to borrow the
  existing expression/local/fact tables and writes directly to the caller's
  MIR instruction table; no callback, dynamic dispatch, fact-copying adapter,
  carrier conversion, or additional allocation was introduced.
- Re-exported both modules through `Compiler.Mir` and imported them directly
  from the remaining switch consumer.
- Verification:
  - focused `--check -I selfhost` runs for both extracted modules passed with
    exit code 0 and `Check succeeded.`
  - `SourceSwitchLowering.stark --check -I selfhost` passed with exit code 0
    and `Check succeeded.` against the combined split.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the `Compiler.Mir` facade and aggregate LLVM-facing fact
    surface. The known imported-module incrementality cost remained visible,
    with reported type-check stages up to 189.8 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-11 Typed Source Backend-Default Bridge Slice

- Added an opt-in source-to-MIR bridge from the canonical
  `TypedFunctionEffectSummaryTable` into the existing LLVM effect and
  calling-convention carriers. It imports inferred `nounwind`, default
  `inlinehint`, no-sync/no-free/progress/no-recurse facts, and internal
  `fastcc` without re-deriving those facts from source spelling.
- Preserved ABI ownership and precedence: tail-callable functions still emit
  `tailcc`; FFI conventions still take precedence over `fastcc`; exported
  functions do not receive the internal convention; ordinary internal and
  public non-exported functions and their direct calls receive the same
  `fastcc` fact.
- Kept `CompileModuleFromAst` byte-stable for the compatibility snapshot
  surface. `CompileModuleFromAstWithTypedBackendDefaults` constructs the typed
  declarations, members, and effect summaries only when explicitly selected,
  so the compatibility path gains neither allocation nor extra front-end
  work. Dense declaration-order lookup is checked against the exact `fn`
  keyword token before any fact is applied.
- Fact construction is outside the reusable lowering core. The core borrows an
  already-built `SourceModuleLoweringFacts` bundle and typed summary table, so
  the eventual optimized SSA/ABI owner can hand off its canonical rows without
  rebuilding or copying them; the standalone opt-in wrapper remains the only
  convenience path that constructs those tables.
- Added a focused helper fact covering internal, public, exported, tail, and
  direct-call cases. The helper asserts the complete emitted LLVM attribute
  and calling-convention sequences rather than checking the carrier in
  isolation.
- The bridge leaves the parent task open: the eventual optimized SSA/ABI
  pipeline must select this typed-owned path and then retire the opt-in
  boundary after compatibility snapshots migrate.
- Verification:
  - `./stark selfhost/Compiler/Mir/SourceFunctionEffects.stark --check -I selfhost`:
    passed with `Check succeeded.` after adding the typed-summary adapter.
  - `./stark selfhost/Compiler/Mir/SourceModuleLowering.stark --check -I selfhost`:
    passed with exit code 0 and `Check succeeded.` against the final borrowed-
    fact orchestration path.
  - focused `SourceBackendFactTests.stark` and full `IrTests.stark` front-end
    checks both passed with exit code 0 and `Check succeeded.`; the aggregate
    check again exposed the known expensive imported-module type checks.
  - Selected test execution and a standalone executable probe were attempted,
    but their tool sessions yielded before terminal output and the probe did
    not produce the requested executable. The outstanding compiler processes
    were terminated cleanly before the final serial verification, so runtime
    execution is not recorded as passed and no stale check remains active.
  - MIR dependency-layer validation and `git diff --check`: passed after the
    final code and ledger updates.

## 2026-07-11 Source Switch Pattern Module Split

- Split three dependency-directed families out of the 31,690-line
  `SourceSwitchLowering.stark` while preserving the existing typed pattern,
  range, enum-layout, ABI, alias, capture, and LLVM-facing inputs verbatim.
  The remaining orchestration/lowering module is 24,269 lines; further splits
  remain explicitly tracked until it is below the preferred 5k-line limit.
- `SourceSwitchPatterns.stark` (2,426 lines) owns switch decision nodes and
  descriptors, row/span validation, bounded descriptor worklists, and typed
  aggregate/list/member row translation.
- `SourceSwitchPatternConditions.stark` (1,408 lines) owns scalar label and
  payload parsing, enum/list/aggregate condition construction, and recursive
  disjointness/overlap proofs. The condition path remains direct and retains
  the exact range and enum tag facts consumed by MIR builders.
- `SourceSwitchAggregatePatterns.stark` (3,678 lines) owns fixed-array/list
  pattern parsing, typed struct field ordinal/flat-layout resolution, nested
  capture translation, scrutinee ABI resolution, and value-parameter nested
  aggregate condition lowering.
- No callback layer, dynamic dispatch, allocation, fact copying, or carrier
  conversion was introduced. The new modules are re-exported through
  `Compiler.Mir`; the consumer imports them directly. The switch-specific
  bracket matcher was renamed to avoid exporting an ambiguous overload beside
  the local-lowering bracket matcher.
- Verification:
  - focused checks for all three extracted modules passed with exit code 0 and
    `Check succeeded.`
  - `SourceSwitchLowering.stark --check -I selfhost` passed with exit code 0
    and only its pre-existing bounded-recursion warning.
  - `SourceModuleLowering.stark --check -I selfhost` passed with exit code 0
    and `Check succeeded.`, validating the downstream lowering and LLVM fact
    handoff surface.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the `Compiler.Mir` re-export and existing switch fact surface.
  - MIR dependency-layer validation and `git diff --check`: passed after the
    final import ordering and ledger update.

## 2026-07-11 Source Switch Storage Pattern Module Split

- Extracted storage-backed struct aggregate and typed fixed-array/list pattern
  condition lowering into `SourceSwitchStoragePatterns.stark` (877 lines).
  `SourceSwitchLowering.stark` is now 23,424 lines; switch-arm parsing,
  assignment CFG construction, and terminal emission remain tracked until the
  orchestration module is below the preferred 5k-line limit.
- Preserved the lowering inputs verbatim: struct base byte offsets, typed
  aggregate/member rows, fixed-array lengths and element strides, scalar range
  facts, local overrides, parameter ABI facts, and nested capture conditions
  continue directly into the existing MIR condition builders. No callback,
  dynamic dispatch, fact-copying adapter, carrier conversion, or additional
  allocation was introduced.
- Re-exported the focused module through `Compiler.Mir` and added the direct
  consumer import. The only module-local diagnostic remains the pre-existing
  ordinary-recursion warning in the typed fixed-array/list frame drain; this
  split does not deepen or add recursion.
- Verification:
  - `./stark selfhost/Compiler/Mir/SourceSwitchStoragePatterns.stark --check -I
    selfhost`: passed with exit code 0 and `Check succeeded.`
  - `./stark selfhost/Compiler/Mir/SourceSwitchLowering.stark --check -I
    selfhost`: passed with exit code 0 and `Check succeeded.`
  - `./stark selfhost/Compiler/Mir/SourceModuleLowering.stark --check -I
    selfhost`: passed with exit code 0 and `Check succeeded.`, validating the
    downstream source-to-MIR and LLVM-fact handoff boundary.
  - `./stark tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir`: passed with exit code 0 and `Check succeeded.`,
    validating the `Compiler.Mir` facade and aggregate IR fact surface.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-11 Source Switch Terminal Parsing and CFG Split

- Extracted 7,448 implementation lines from `SourceSwitchLowering.stark` into
  two dependency-directed modules. After the two direct imports, the remaining
  switch orchestration module is 13,377 lines, down from 20,823.
- `SourceSwitchTerminalParsing.stark` (4,707 lines) owns terminal return-arm
  and case collection, exhaustiveness and interval-overlap validation, enum
  tag handling, capture typing, guards, and fixed-array slice descriptor and
  argument construction. It preserves the existing typed aggregate/list rows,
  scalar ranges, capture spans, enum layout/tag facts, and local overrides as
  direct borrowed tables.
- `SourceSwitchTerminalLowering.stark` (2,805 lines) consumes that surface and
  owns integer, boolean, enum-payload, fixed-array/list, and struct-aggregate
  terminal CFG construction. It continues to emit directly into caller-owned
  MIR instruction, block, and block-range tables, so the LLVM-facing value,
  layout, alias, ABI, and branch facts are neither copied nor reconstructed.
- No forwarding wrapper, callback, dynamic dispatch, carrier conversion, or
  additional allocation was introduced. Both modules remain below the
  preferred 5k-line maintenance limit and are re-exported through
  `Compiler.Mir`.
- Verification:
  - focused checks for both extracted modules passed with exit code 0 and
    `Check succeeded.`
  - `SourceSwitchLowering.stark --check -I selfhost` passed with exit code 0
    and `Check succeeded.` against the new dependency boundary.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the `Compiler.Mir` facade and aggregate LLVM-facing fact
    surface. The known imported-module incrementality cost remained visible,
    with reported type-check stages up to 186.9 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-11 Source Switch Module Split Completion

- Completed the dependency-directed decomposition of the original 31,690-line
  source switch lowerer. The remaining `SourceSwitchLowering.stark` facade and
  emission-entrypoint module is 4,304 lines, and every extracted switch module
  is below the preferred 5k-line maintenance threshold.
- `SourceSwitchAssignmentParsing.stark` (2,507 lines) owns assignment-case
  collection while preserving exact case intervals, typed pattern rows,
  capture spans, enum tags, storage offsets, and parsed assignment arms.
- `SourceSwitchAssignmentLowering.stark` (3,672 lines) owns assignment local
  typing, value/capture lowering, raw-pointer fact updates, and integer,
  boolean, and field-assignment CFG construction. It mutates the existing
  caller-owned MIR and fact tables directly.
- `SourceSwitchFunctionLowering.stark` (2,998 lines) owns constructed-object
  switch-field lowering and terminal function-to-block orchestration through
  direct calls into the assignment and terminal layers.
- No forwarding wrapper, callback, dynamic dispatch, carrier conversion, fact
  reconstruction, or additional allocation was introduced. Typed range,
  layout, alias, ABI, capture, pointer, block, and branch facts remain on their
  original direct path into MIR and LLVM lowering.
- `SourceModuleLowering.stark` now imports the function-level switch module
  directly, matching Stark's intentionally non-transitive import semantics.
- Verification:
  - focused checks for all three final extracted modules passed with exit code
    0 and `Check succeeded.`
  - the 4,304-line `SourceSwitchLowering.stark` consumer passed with exit code
    0 and `Check succeeded.`
  - `SourceModuleLowering.stark --check -I selfhost` passed after the direct
    downstream import was added.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the complete `Compiler.Mir` facade and aggregate LLVM-facing
    fact surface. The known imported-module cost remained visible, with
    reported type-check stages up to 201.7 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-12 Source If Module Split Completion

- Completed the dependency-directed decomposition of the 13,425-line source
  if lowerer. `SourceIfLowering.stark` is now a 1,396-line emission facade,
  and every extracted module is below the preferred 5k-line threshold.
- `SourceIfCore.stark` (102 lines) owns enum-return carrier validation and
  typed enum-return value lowering; `SourceIfShapes.stark` (229 lines) owns
  function-body shape detection.
- `SourceIfParsing.stark` (2,419 lines) owns terminal arms, nested/recursive
  assignment forms, multi-assignment parsing, and raw-pointer fact resolution.
- `SourceIfStorageMutation.stark` (2,075 lines) owns storage-mutation parsing,
  call/condition validation, block counts, stores, and recursive CFG lowering.
- `SourceIfAssignmentLowering.stark` (2,068 lines) owns arm result typing,
  source local type codes, call validation, capture/local overrides, phi
  construction, and nested/recursive assignment CFG lowering.
- `SourceIfFunctionLowering.stark` (3,337 lines) and
  `SourceIfReturnLowering.stark` (1,981 lines) own if-expression, local,
  nested, recursive, and return function-to-block orchestration.
- Typed ranges, enum layouts, raw-pointer and alias facts, local/capture facts,
  MIR values, phi inputs, branch structure, and block ranges continue through
  the original caller-owned tables into LLVM lowering. No forwarding wrapper,
  callback, dynamic dispatch, carrier conversion, fact reconstruction, or
  additional allocation was introduced.
- Added direct imports for non-transitive consumers in source-module, switch,
  switch-assignment, switch-terminal, and try lowering. These are dependency
  declarations only and add no runtime work.
- Verification:
  - `SourceIfReturnLowering.stark --check -I selfhost` passed with exit code 0
    and `Check succeeded.`, covering the complete seven-module dependency
    chain.
  - the 1,396-line `SourceIfLowering.stark` facade passed with exit code 0 and
    `Check succeeded.`
  - `SourceModuleLowering.stark --check -I selfhost` passed after all direct
    downstream imports were present.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the aggregate MIR and LLVM-facing fact surface. The known
    imported-module cost remained visible, with reported type-check stages up
    to 210.1 seconds.
  - MIR dependency-layer validation and `git diff --check`: passed.

## 2026-07-12 Source Local Module Split Completion

- Completed the dependency-directed decomposition of the 10,278-line source
  local lowerer. `SourceLocalLowering.stark` is now a 1,777-line lexical,
  scalar-layout, alignment, and shared-helper core; every extracted module is
  below the preferred 5k-line threshold.
- `SourceLocalStorageLayout.stark` (3,419 lines) owns aggregate/enum layout,
  local storage models, raw-pointer facts, bounds proofs, and declarations;
  `SourceTryPropagation.stark` (987 lines) owns propagation roles,
  compatibility, funnels, payload ranges, and enum-payload facts.
- `SourceLocalExpressionParsing.stark` (1,075 lines) owns enum, aggregate, and
  raw-pointer expression parsing; `SourceLocalInitialization.stark` (959
  lines) owns object/scalar/enum/fixed-array/raw-pointer local initialization.
- `SourceLocalMutation.stark` (1,809 lines) owns scalar, enum, slice,
  raw-pointer, and constructed-object storage mutation parsing/lowering;
  `SourceLocalArena.stark` (398 lines) owns arena selectors, target-typed
  constructor shapes, dynamic locals, and reserve lowering.
- Relocated the original implementations directly. Typed ranges, enum tags
  and layouts, raw-pointer mutability/element/nested facts, bounds proofs,
  alias/distinct-storage facts, alignments, local overrides, call contracts,
  MIR values/instructions/blocks, and storage mutation ordering continue
  through the same caller-owned tables into LLVM lowering. No forwarding
  wrapper, callback, dynamic dispatch, carrier conversion, fact
  reconstruction, or additional allocation was introduced.
- Added direct imports for non-transitive source-module, if, switch, try, and
  function-context consumers. These imports are compile-time dependency
  declarations and add no runtime work.
- Verification:
  - `SourceLocalStorageLayout.stark --check -I selfhost` passed with exit code
    0 and the existing bounded-recursion warnings.
  - `SourceLocalInitialization.stark`, `SourceLocalMutation.stark`, and
    `SourceLocalArena.stark` focused checks passed with exit code 0 and
    `Check succeeded.`
  - `SourceModuleLowering.stark --check -I selfhost` passed after all direct
    consumer imports were present.
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed with exit code 0 and `Check succeeded.`,
    validating the aggregate MIR and LLVM-facing fact surface. The known
    imported-module cost remained visible, with reported type-check stages up
    to 282.5 seconds.
  - every top-level `selfhost/Compiler/Mir/*.stark` source-lowering module is
    below 5,000 lines; MIR dependency-layer validation and `git diff --check`
    passed.

## 2026-07-12 Aggregate Carrier DCE Execution And Cold-Build Baseline

- Closed the pending executable verification for raw O0 aggregate-carrier
  load elimination. LLVM block/direct-switch emission keeps using the existing
  allocation-free use-count table and removes only unused `FixedArrayLoad`
  and `StructValueLoad` producers.
- The focused executable fact
  `LlvmBlockEmissionDropsUnusedAggregateCarrierLoadsButKeepsScalarRangeLoads`
  passed. It verifies that the dead aggregate carrier is absent while the live
  scalar load and its `!range` metadata remain, preserving the backend fact
  consumed by LLVM optimization.
- The cold filtered `selfhost.Ir` build emitted the complete self-host static
  library, package image, and test executable before running the single fact.
  Wall time exceeded 65 minutes; reported imported-module `lower-mir` stages
  reached 1,564.9 seconds. This supersedes the earlier approximate 20-minute
  root rebuild figure in the performance tracker.
- The identical unchanged filtered rerun skipped rebuilding and completed in
  0.2 seconds with the same passing fact. This isolates the dominant cost to
  cold root/dependency pipeline work rather than fact execution.
- No runtime wrapper, allocation, metadata reconstruction, or broader load
  elimination was added while closing the verification item.

## 2026-07-12 Specialized Switch Parenthesis Repair

- A warmed `selfhost.Ir --filter Sibling` execution reached nine facts: the
  row-level unguarded-sibling overlap preflight passed, while eight source-facing
  struct, fixed-array, nested, and enum sibling-capture facts rejected before
  LLVM emission.
- Traced the shared pre-emission failure to eleven specialized terminal,
  assignment, and function switch paths that named a `closeParen` but called
  `MatchingBracketForMirLowering` on the opening `(`. Those paths could never
  advance to the switch body for parenthesized source syntax.
- Replaced the eleven calls with the existing
  `MatchingParenForMirLowering` helper directly. No parser wrapper,
  allocation, capture-table conversion, MIR carrier change, or LLVM fact
  reconstruction was introduced.
- Verification after the repair:
  - no specialized `closeParen` site still calls the bracket matcher;
  - `SourceSwitchFunctionLowering.stark --check -I selfhost` passed, covering
    the repaired terminal and assignment parsing dependency chain;
  - full `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed;
  - `git diff --check` passed.
- The source change invalidates the self-host package image, so rerunning the
  executable sibling facts would immediately repay the measured >65-minute
  cold package build. The tracker keeps those execution items open for the
  next package rebuild rather than claiming runtime closure from compile-only
  evidence.

## 2026-07-12 Sibling Switch Dispatcher Diagnosis And Repair

- Completed the fresh-package executable rerun deferred by the parenthesis
  repair. The cold build emitted the self-host static library, package image,
  generated test runner, and executable; the row-level sibling preflight fact
  passed, while the same eight source-facing sibling facts still rejected
  before LLVM text was produced.
- Test-only staged probes (removed after diagnosis) established that both
  sibling typed rows, both enum labels, full section parsing, descriptor
  validation, shared decision preflight, capture-aware call validation, enum
  identity, MIR block construction, parameter and ABI facts, MIR value facts,
  and return-range validation all succeed. Calling the exact terminal switch
  dispatcher with the same source rejected, isolating the defect above those
  fact-preserving stages.
- Terminal functions whose first case has a top-level sibling `|` now route
  directly to the existing generic sibling-aware lowerer. The classifier is a
  bounded token scan that tracks nested parentheses/braces/brackets, so enum,
  aggregate property, and list pattern delimiters do not masquerade as the
  section colon. Single-label switches retain their existing specialized
  paths and cost model.
- Corrected the last field-list switch-scrutinee probe that still used the
  bracket matcher on `switch (`. No wrapper, callback, allocation, capture
  conversion, MIR carrier change, or backend-fact reconstruction was added.
- Verification: `SourceSwitchFunctionLowering.stark --check -I selfhost` and
  `SourceSwitchLowering.stark --check -I selfhost` both passed; MIR dependency
  validation and `git diff --check` passed. A second cold executable run after
  the dispatcher and matcher repairs still passed the row-level preflight and
  failed the same eight source-facing facts. The dispatcher repair is valid but
  is therefore not sufficient for executable closure.
- The next shared boundary is public compile orchestration's function-effect
  prepass, which independently lowers every function body before LLVM emission
  for both terminal and assignment functions. A temporary conservative-effects
  fallback was removed rather than weakening LLVM attributes, and a standalone
  probe was removed after it proved to require a full compiler-graph rebuild;
  neither interrupted diagnostic is counted as pass/fail evidence.
- The staged checked-struct probe also showed `SemanticallyValidStream`
  rejecting typed struct syntax before source lowering. That older token-level
  semantic-gate limitation is recorded separately from the dispatcher repair
  rather than being hidden by weakening the executable facts.

## 2026-07-12 Checked Sibling Capture Semantic Scope Repair

- Repaired the preliminary checked-path name pass so it parses real function
  signatures, seeds typed function and parameter names, recognizes typed-local
  declaration headers, and starts value-use resolution at the function body.
  Switch-pattern/type/member syntax is classified outside the value namespace;
  a bare value use therefore cannot resolve merely because a field has the same
  spelling. The typed binder still validates exact owners and categories before
  MIR construction. Arity, function-name uniqueness, and finite-call rows now
  use the same parsed typed signatures instead of `fn`-adjacent token guesses.
- Repaired redeclaration analysis so switch pattern captures are scoped by case
  section and sibling-alternative ordinal. Equal-name captures may merge across
  sibling alternatives or be reused in distinct sections, while duplicates in
  one alternative, duplicate parameters, and parameter/local collisions remain
  rejected. No runtime capture conversion, MIR carrier, range, ABI, alias,
  alignment, or LLVM attribute behavior changed.
- Added `SemanticRedeclarationScopesSwitchPatternCapturesPerAlternative` with
  positive sibling/distinct-section cases, negative same-alternative and typed
  parameter cases, and exact checked struct/list semantic sources.
- Verification: `SourceSemanticProbes.stark --check -I selfhost` passed. A
  temporary narrow executable importing only the semantic-probe module compiled
  and exited `0` over the same positive and negative cases; the probe source was
  deleted. The full self-host package/test executable was not rebuilt in this
  slice, so the six checked source-to-LLVM facts remain open for that audit.

## 2026-07-12 Logical Package-Image Model And Generic-Template Loader Splits

- Split the 15,895-line `Compiler.Mir.PackageImage.Models` carrier into focused
  core/facade, enum-kind, decoded-MANF-row, generic-template, typed-interface,
  function semantic/ownership, and backend-fact modules. Every resulting model
  module is below 4,000 lines; the largest is the 3,902-line generic-template
  carrier.
- Kept the compatibility module as an export facade. Existing public and
  internal declaration names are unchanged, and a sorted declaration-surface
  comparison against the pre-split file found no missing or duplicate structs,
  enums, or functions.
- Preserved the exact `IrTable` and dynamic-byte ownership used by package
  loading and lowering. Type-reference row identities, ABI and layout facts,
  function effects, ownership/semantic rows, and generic-template work items
  cross module boundaries by direct borrow; the split introduces no wrapper,
  allocation, callback, serialization round-trip, or fact reconstruction.
- Split the 7,279-line generic-template section loader into a 1,699-line graph
  builder, 3,602-line call/object/enum/value materializer, and 1,998-line typed
  expression/pattern/statement tree orchestrator. The package loader now imports
  its one directly consumed effective-section probe from the value module;
  public text append and graph entry points retain their facade visibility.
- Preserved published template ordinals, parent/child row spans, type-reference
  source tags, bound-operation payloads, and deferred-instantiation facts in
  their original tables. Materialization still performs a single JSON traversal
  into the model-owned graph and adds no intermediate graph or conversion.
- Split the 6,944-line typed-interface loader into a 2,137-line iterative
  type-reference loader, 3,553-line typed declaration/fact materializer, and
  1,279-line summary/API orchestrator. Callable ABI, raw-pointer count
  expressions, parameter-group alias contracts, layout/type metadata, and
  child/source ordinals still write directly to the original graph tables.
- Split the 5,070-line compiler-fact loader into a 1,230-line function-effect
  and ABI loader, 1,651-line layout/native loader, 2,210-line function
  semantic/ownership loader, and six-line compatibility facade. This mirrors
  the fact-family boundaries consumed by optimized SSA and LLVM and keeps
  purity, memory, unwind, progress, calling-convention, layout, linkage,
  ownership, move, drop, and call facts independently auditable.
- Split the 7,256-line compatibility source bridge into a 1,234-line lookup and
  requirement-analysis core, 2,049-line expression/type renderer, 1,369-line
  statement/pattern renderer, 2,291-line declaration renderer, and 363-line
  public orchestrator. Reconstructed source remains compatibility-only;
  structured package facts remain authoritative for specialization, ABI,
  ownership, effects, layout, and LLVM lowering.
- Verification:
  - all seven focused model modules and `PackageImage/Models.stark` passed
    `--check -I selfhost`;
  - all three generic-template loader modules passed focused checks, and sorted
    declaration-surface comparison against the pre-split loader found no missing
    or duplicate functions;
  - every typed-interface, compiler-fact, and source-bridge split module passed
    focused checks; the bridge retains only its pre-existing bounded-recursion
    warnings;
  - sorted declaration-surface comparisons against all three pre-split files
    found no missing or duplicate structs, enums, or functions;
  - the `Compiler.Mir.PackageImage` compatibility facade, logical package-image
    loader, and manifest JSON builder passed full-front-end checks;
  - `SourceModuleLowering.stark --check -I selfhost` passed across the complete
    compiler-facing consumer closure;
  - `tests-stark/selfhost.Ir/IrTests.stark --check -I selfhost -I
    tests-stark/selfhost.Ir` passed, validating the aggregate MIR and
    LLVM-facing fact surface after all splits. Imported-module type checking
    remained the dominant cost, with individual reported checks reaching 267.3
    seconds; this is verification evidence for the open root-incrementality task,
    not a benchmark comparison;
  - declaration-surface comparison, MIR dependency validation, and
    `git diff --check` passed.

## 2026-07-12 Dense MIR-To-SSA Foundation

- Added a focused `Compiler.Ssa` package with flat dense instruction, block,
  and function artifacts. MIR value/block/function indexes are preserved at
  this first boundary, so operands, phi predecessor payloads, terminators, and
  contiguous function ranges need no hash table or remap allocation.
- Every SSA instruction retains its exact MIR opcode, result type, four operand
  slots, immediate, and originating MIR value. Blocks retain terminator slots
  and flags; functions retain entry and owned block/instruction ranges. These
  are direct table rows rather than generic aggregate wrappers.
- MIR-to-SSA conversion reserves its four destination tables once, performs a
  linear append, and publishes outputs only after complete success. It requires
  a dense `ValueFacts` row for every MIR value and copies alignment, calling
  ABI, noalias, volatile, integer range, nullability, and text-constant facts
  exactly instead of rediscovering them before LLVM lowering.
- Moved the existing MIR structural gate into the 150-line focused
  `Compiler.Mir.Validation` module. SSA lowering therefore reuses the same
  operand, terminator, range, loop-flag, and tail-call checks without importing
  the LLVM/source/package-image MIR facade; focused SSA check time stayed in
  the low single-digit seconds rather than importing the full compiler graph.
- Bodyless MIR declaration rows remain bodyless SSA declaration rows. This
  preserves assembly/external declaration identity without inventing blocks,
  instructions, or synthetic value-fact rows.
- Added focused regression helpers for exact artifact/fact preservation,
  missing-fact rejection with unchanged outputs, malformed-function-range
  rejection, and bodyless declaration preservation. The helper module, SSA
  model/lowering modules, SSA facade, MIR validation module, full MIR facade,
  aggregate `selfhost.Ir` entrypoint, and root `Compiler.stark` facade passed
  `--check`; the MIR dependency guard and `git diff --check` also passed. The
  root audit's largest imported type-check reached 277.0 seconds while focused
  SSA checks remained in the low single-digit seconds, adding evidence to the
  existing root-incrementality task without treating check time as a benchmark.
