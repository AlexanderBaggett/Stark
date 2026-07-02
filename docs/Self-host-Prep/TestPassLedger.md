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
