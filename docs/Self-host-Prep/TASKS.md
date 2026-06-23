# Self-Host-Prep Tasks

This is the executable task list for self-host prep. Keep this file as a work
queue plus any instructions needed to execute the work. Do not use it as a
progress ledger, and do not rewrite task descriptions just to record partial
progress.

Use `[x]` for complete, `[~]` for partially implemented, and `[ ]` for open.
Track status by flipping checkboxes only; put evidence, counts, and triage notes
in [TestPassLedger.md](TestPassLedger.md) or the relevant companion document.

Primary goal: implement the self-host-prep roadmap and make all tests runnable
on macOS pass. The test-pass work is last-mile work; most failing tests depend
on compiler infrastructure that is still being implemented.

Execution constraints:

- Preserve backend facts all the way through lowering to IR.
- Treat correctness and completeness as required scope, not expansion.
- Keep Stark's speed-focused design visible in implementation choices.
- Prefer full tasks over partial slices.
- Do not add package-manager release work; downloadable relocatable archives
  are the release path.

---

## 1. Compiler Port To Stark

- [~] Implement the front-end parser, syntax model, binding, and type resolver.
  - [x] Implement the handwritten lexer with exact spans and grammar-faithful tokenization.
  - [~] Implement the handwritten parser against `Stark.g4`.
    - [x] Parse headers, declarations, functions, fields, enum variants, statements, expressions, scopes, loops, and switch sections.
    - [ ] Capture full type spans for parameters, returns, fields, locals, and enum payloads.
    - [ ] Parse struct and enum `where` clauses.
    - [ ] Resolve switch-case pattern value references for constants, enum cases, aggregates, and lists.
    - [ ] Implement the parser facade and Stark-native syntax tree or parse-event model.
    - [ ] Port text literal decoding with current raw-string parity semantics.
  - [~] Implement name binding and type-reference resolution.
    - [x] Build declaration tables, function scopes, lexical local visibility, and structured bind diagnostics.
    - [x] Resolve value references, signature types, field types, enum payload types, local types, and function `where` constraints.
    - [ ] Resolve nested generic argument types and complete type compatibility facts.
    - [ ] Implement module resolution and imported-package/source lookup.
    - [ ] Implement overload resolution and generic use-site instantiation planning.
  - [~] Implement type checking and semantic validation.
    - [x] Diagnose non-boolean conditions, logical operands, void-return mismatches, duplicate enum variants, and invalid break/continue use.
    - [ ] Type identifiers, calls, member chains, assignments, returns, conversions, and coercions against resolved symbols.
    - [ ] Port semantic validation, ownership validation, borrow liveness, range facts, enum facts, and CTFE.

- [ ] Implement diagnostics, compiler artifacts, pipeline orchestration, and artifact rendering.
  - [ ] Port compiler diagnostic data structures and source-caret rendering integration.
  - [ ] Port compiler artifact storage and deterministic artifact text rendering.
  - [ ] Port the compiler pipeline and default pass orchestration.
  - [ ] Add targeted artifact output for tests and debugging.

- [~] Implement the IR memory model, MIR foundations, and fact-transfer substrate.
  - [x] Implement typed handle wrappers and the dense `IrTable<T>` model in `selfhost/Compiler/Ir.stark`.
  - [x] Implement initial `ValueFacts`, `AbiKind`, and present-fact inheritance helpers.
  - [x] Implement MIR instruction, block, function, global, control-flow, call, phi, and basic textual LLVM subset helpers.
  - [x] Implement MIR byte codecs, MIR1/MIR2 package-image sections, validation, inspection summaries, and file save/load helpers.
  - [ ] Define every concrete fact category with attach point, phase owner, durability, producer, consumer, and validation rule.
  - [ ] Add low-friction fact-transfer helpers for every lowering builder that creates new handles.
  - [ ] Add phase-boundary validation for stale handles, dropped `forbid-drop` facts, ABI facts, alias facts, layout facts, and durable package facts.

- [ ] Implement HIR/MIR lowering, drop lowering, switch lowering, and imported-template handling.
  - [ ] Port the MIR lowering pass shell and function MIR builder.
  - [ ] Port place lowering, runtime drop lowering, compile-time evaluator lowering, and switch-pattern lowering.
  - [ ] Preserve range, alias, ABI, layout, ownership, and assembly facts through MIR lowering.
  - [ ] Port imported-template lowering using package-image template facts.

- [ ] Implement SSA lowering, SSA validation, and optimization passes.
  - [ ] Port MIR-to-SSA lowering and SSA artifact construction.
  - [ ] Port SSA validator coverage including a structured invalid-IR fixture path.
  - [ ] Port value fact analysis, alias-aware optimization, cleanup, folding, scalar replacement, inlining, dynamic storage optimization, and ownership traffic optimization.
  - [ ] Preserve backend facts through every SSA rewrite and validate facts after optimization passes.

- [ ] Implement ABI lowering, libLLVM emission, and native output.
  - [ ] Port ABI lowering and C data-model/layout facts.
  - [ ] Add the LLVM C API binding layer with typed opaque handles, C strings, out pointers, messages, and deterministic dispose wrappers.
  - [ ] Build LLVM modules directly through libLLVM and emit object files in-process.
  - [ ] Keep textual LLVM as deterministic inspection/debug output from the in-memory LLVM module.
  - [ ] Preserve ABI, alignment, alias, noalias, range, volatile, and calling-convention facts through LLVM lowering.

- [~] Implement package-image models, builders, loaders, bridge codecs, binary load, and deterministic inspection.
  - [x] Decide binary-first package-image policy with JSON/text inspection views.
  - [x] Implement the selfhost MIR package-image leaf codec, validation statuses, deterministic text/JSON summaries, and on-disk round trips.
  - [ ] Finalize the public `.starkpkg` contract and `stark inspect-pkg --format json|text` behavior.
  - [ ] Design the durable sectioned binary format with magic, exact version, section IDs, offsets, lengths, string tables, typed indexes, and target/profile facts.
  - [ ] Port logical package models, builders, loaders, source bridge, shared codecs, and deterministic inspection rendering.
  - [ ] Add diagnostics for malformed headers, unknown required sections, bad offsets, version mismatches, target/profile mismatches, and legacy JSON bridge failures.
  - [ ] Route binary package images into the accepted build layout and keep inspection views explicit.

- [ ] Implement CLI, project driver, manifest handling, native-toolchain driver, and build entry points.
  - [ ] Port `Program`, `CompilerCli`, project driver, build entry points, and project command routing.
  - [ ] Replace host-style manifest parsing with `System.Toml` plus typed manifest decoding.
  - [ ] Port native toolchain discovery, target detection, linker/archiver invocation, and SDK checks.
  - [ ] Preserve project build layout, incremental stamps, stdlib discovery, and package-image generation.

- [~] Implement small fact and assembly-metadata leaf helpers.
  - [x] Add initial assembly architecture facts and MIR assembly metadata serialization.
  - [ ] Port register facts, target facts, native metadata facts, and any remaining small helper modules.

---

## 2. Bootstrap And Cutover

- [ ] Build Stage1 with the C# Stage0 host compiler.
  - [ ] Compile the Stark compiler sources into the first runnable Stark compiler.
  - [ ] Package the Stage1 standard library artifacts for the active target and stage.

- [ ] Build Stage2 with the Stage1 Stark compiler.
  - [ ] Compile the compiler with itself through the Stage1 binary.
  - [ ] Emit Stage2 package images, diagnostics, and native artifacts.

- [ ] Compare Stage1 and Stage2 outputs for determinism.
  - [ ] Compare deterministic package-image inspection output and clean-build compiler outputs.
  - [ ] Compare diagnostics, artifact text, native object metadata, and executable behavior where applicable.
  - [ ] Reserve raw binary comparison for codecs that explicitly guarantee byte determinism.

- [ ] Run the ported Stark suite against the self-hosted compiler.
  - [ ] Route tests through the self-hosted compiler path rather than only the C# host protocol.
  - [ ] Keep platform gates aligned with the macOS pass-bar policy.

- [ ] Document and perform cutover.
  - [ ] Keep the C# compiler as Stage0 until Stage2 builds and tests pass.
  - [ ] Document the bootstrap flow and recovery path.
  - [ ] Move the C# host `/src` to `/old_src` and make the Stark compiler own `/src`.

---

## 3. Tooling And Packaging

- [~] Complete libLLVM-primary backend integration through the LLVM C API.
  - [~] Finish `System.C` C string and owned foreign-message helper coverage needed by LLVM.
  - [ ] Implement LLVM C API bindings, version checks, required-symbol checks, and typed wrapper drops.
  - [ ] Add direct object emission, verifier diagnostics, optional module printing, and backend smoke tests.

- [~] Complete binary package-image generation/loading and `stark inspect-pkg`.
  - [x] Implement the selfhost MIR package-image leaf codec and deterministic summary inspection.
  - [ ] Implement the full compiler package-image logical section model and binary loader.
  - [ ] Add `stark inspect-pkg` as a top-level compiler command.
  - [ ] Update package-image docs and tests after public spelling lands.

- [~] Complete native/libLLVM toolchain discovery, bundled toolchain support, target facts, C data-model facts, and aggregate-layout facts.
  - [x] Resolve release policies for LLVM version, official archive acquisition, Linux no-libc policy, Windows linker-driver policy, macOS SDK policy, and `--toolchain-dir` scope.
  - [ ] Add a toolchain resolver for libLLVM, `clang`, linkers, archivers, SDKs, and helper tools.
  - [ ] Add override precedence for CLI flags, environment variables, user config, bundled tools, and `PATH`.
  - [ ] Validate target triple, data layout, C aliases, aggregate layout, and package compatibility before backend use.

- [ ] Complete release packaging, `stark doctor`, and clean-machine archive verification.
  - [ ] Define the release archive layout for compiler, stdlib, vendor, toolchain, licenses, install docs, and release metadata.
  - [ ] Add runtime-specific publish or native compiler archive assembly for Linux, Windows, and macOS.
  - [ ] Bundle pinned LLVM 22.1.8 artifacts and record source archives, checksums, and license files.
  - [ ] Build and include standard library and vendor library source plus required package/native artifacts.
  - [ ] Add a manually triggered release workflow that creates downloadable relocatable archives.
  - [ ] Add `stark doctor` with compiler version, runtime ID, toolchain paths, versions, target facts, stdlib path, and SDK status.
  - [ ] Add clean-machine smoke tests for archive help, check, MIR, SSA, LLVM, object, library, executable, native dependency, and runtime basics.

- [ ] Sync editor syntax and completions with the self-hosting language surface.
  - [ ] Update grammar-derived syntax highlighting, completions, snippets, and stdlib symbol data.
  - [ ] Verify coverage against the canonical language surface after parser/selfhost syntax changes land.

---

## 4. Standard Library And Porting APIs

- [~] Migrate stdlib and compiler-port APIs to `Option<T>` / `Result<T, E>` conventions.
  - [x] Implement role-based `[Ok]`/`[Err]` propagation, `try`, and `from` funnels.
  - [ ] Replace nullable-shaped APIs, ad hoc `Try*` out patterns, and exception-shaped recoverable failures in compiler-port code.
  - [ ] Keep invariant violations explicit as diagnostics, validation failures, or traps according to the relevant policy.

- [~] Finish standard library surfaces required by the compiler port.
  - [~] Finish compiler-grade text builders, formatting, escaping, golden/snapshot support, and diagnostic rendering.
  - [~] Finish generic collections, typed interning, deterministic output ordering, and compiler symbol-table migration.
  - [~] Finish file, filesystem, path, recursive walk, temp, and cross-platform metadata parity.
  - [~] Finish `System.Toml` by replacing project-driver manifest parsing with typed manifest decoding.
  - [~] Finish JSON/package inspection support needed by `stark inspect-pkg` and golden tests.
  - [~] Finish `System.C` C-string helpers and LLVM-specific owner wrappers.

- [~] Keep platform boundaries explicit.
  - [x] Use Linux syscall-backed/no-libc stdlib and runtime code for Stark-owned Linux behavior.
  - [x] Use the current Windows executable-generation path for the compiler release.
  - [x] Require local macOS SDK or Command Line Tools and diagnose missing pieces through `stark doctor`.
  - [ ] Add platform-specific diagnostics for SDK, CRT, pkg-config, and native/vendor dependency requirements.

- [ ] Preserve the official vendor library as a first-class release component.
  - [ ] Add vendor source and generated artifacts to release archive layout.
  - [ ] Add vendor package/native metadata discovery and diagnostics after the vendor branch merges.

---

## 5. Ported Test Pass

Do this after the compiler infrastructure above is online enough for the tests
to exercise the real self-hosted compiler path. Use
[TestPassLedger.md](TestPassLedger.md) for counts, failure-family notes, and
historical triage.

- [~] Fix the package-image input/protocol gap that blocks package-backed compiler and LLVM tests.
  - [x] Prove package-backed LLVM compilation through the existing host-test harness.
  - [ ] Add the remaining package-backed callable-value and manifest-backed compiler test coverage.
  - [ ] Add any missing typed-only package-codegen flag or equivalent protocol path.

- [~] Align SSA/MIR artifact selection and rendered-fragment expectations for ported text tests.
  - [x] Fix verified SSA families including ArithmeticFold, ValueFacts, AliasAware, ScopedNoAlias, and InlineSsa.
  - [ ] Fix remaining source-ok SSA text-class tests by selecting the actual artifact and spelling fragments as rendered.
  - [ ] Fix remaining source-expressible SSA type/range source ports.
  - [ ] Fix remaining MIR text and structural artifact expectations.

- [ ] Add the structured invalid-IR fixture path needed for source-inexpressible validator coverage.
  - [ ] Define a test-only fixture API for invalid MIR, SSA, and package-artifact validator inputs.
  - [ ] Port invalid-SSA validator tests to the fixture path or record explicit host-internal exclusions.

- [ ] Add target-triple pinning or platform gating for non-macOS artifact and native-runtime tests.
  - [ ] Cross-target compile artifact-only Linux and Windows tests on macOS where no foreign SDK/runtime is required.
  - [ ] Platform-gate tests that require foreign SDKs, linkers, syscalls, or runtime behavior.
  - [ ] Add comments explaining each platform-only pass condition.

- [ ] Finish option-toggle plumbing used by remaining LLVM lowering tests.
  - [ ] Add the missing host-test protocol switches for qualifier, internalization, target, package, and inspection variants.
  - [ ] Verify the remaining LLVM per-test residues after option plumbing lands.

- [ ] Resolve remaining suite failures after infrastructure lands.
  - [ ] Resolve `compiler.Tests` package-image, diagnostics, type-checking, ownership, pipeline, runtime, CLI, and example failures.
  - [ ] Resolve `compiler.SsaTests` cleanup, scalar replacement, function-address, constant-text, text-view, dynamic-storage, and cross-module failures.
  - [ ] Resolve `compiler.LlvmTests` package-image and genuine per-test residues.
  - [ ] Resolve `compiler.MirTests` MIR text and structural failures.
  - [ ] Resolve `stdlib.Port` platform-specific, source-stdlib, dispatch, and miscellaneous failures.
  - [ ] Recheck the lone `compiler.FeatureTests` failure and close it if still reproducible.

- [ ] Close test-scope hygiene.
  - [ ] Port the final unported qualifying C# test or record an explicit exclusion reason.
  - [ ] Audit excluded tests after the self-hosted backend lands and keep only CPU, target, or host-internal exclusions.
  - [ ] Rebaseline [TestPassLedger.md](TestPassLedger.md) only after a clean full-suite sweep.

---

## 6. Known Compiler Bugs Blocking Self-Host

No known host-compiler blockers currently tracked.

---

## 7. Docs And Book Work

Defer each item until its API/spelling lands.

- [ ] Document generic collections and interning.
  - [ ] Document collection contracts, exact text key semantics, and compiler interning as an architecture pattern.
- [ ] Document package images and `inspect-pkg`.
  - [ ] Document binary codec tests separately from JSON/text inspection golden tests.
- [ ] Document build-artifact layout.
  - [ ] Document stage/profile/target output layout after project driver behavior lands.
- [ ] Document `System.Toml`.
  - [ ] Document the supported TOML version and any temporary bootstrap subset.
- [ ] Document `Transferable` / `Shareable`.
  - [ ] Document call-site and thread-boundary enforcement after final consumer surfaces land.
- [ ] Document threading APIs.
  - [ ] Document threads, atomics, synchronized storage, channels, and platform behavior.
- [ ] Document the libLLVM backend.
  - [ ] Document bundled libLLVM, override paths, direct object emission, and textual inspection artifacts.

---

## 8. Post-Self-Host

- [ ] Rebuild broad `comptime` / `System.Compiler` in the Stark compiler and add conformance tests.
  - [ ] Add post-bootstrap CTFE value kinds only when compiler, stdlib, or vendor code needs them.
  - [ ] Keep new CTFE value kinds deterministic, cheap to compare/hash, and package-image-representable.

- [ ] Migrate bundled LLVM from 22.1.x to the latest stable LLVM 23.1.x release.
  - [ ] Update LLVM C API bindings, IR spelling, bundled toolchain acquisition, package checksums, and backend regression tests.

---

## 9. Open Decisions

No open decisions currently tracked.
