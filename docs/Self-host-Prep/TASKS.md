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
    - [x] Capture full type spans for parameters, returns, fields, locals, and enum payloads.
    - [x] Parse struct and enum `where` clauses.
    - [x] Resolve switch-case pattern value references for constants, enum cases, aggregates, and lists.
    - [x] Implement the parser facade and Stark-native syntax tree or parse-event model.
    - [x] Port text literal decoding with current raw-string parity semantics.
  - [~] Implement name binding and type-reference resolution.
    - [x] Build declaration tables, function scopes, lexical local visibility, and structured bind diagnostics.
    - [x] Resolve value references, signature types, field types, enum payload types, local types, and function `where` constraints.
    - [x] Resolve nested generic argument types and complete type compatibility facts.
    - [x] Implement module resolution and imported-package/source lookup.
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

- [x] Implement the IR memory model, MIR foundations, and fact-transfer substrate.
  - [x] Implement typed handle wrappers and the dense `IrTable<T>` model in `selfhost/Compiler/Ir.stark`.
  - [x] Implement initial `ValueFacts`, `AbiKind`, and present-fact inheritance helpers.
  - [x] Implement MIR instruction, block, function, global, control-flow, call, phi, and basic textual LLVM subset helpers.
  - [x] Implement MIR byte codecs, MIR1/MIR2 package-image sections, validation, inspection summaries, and file save/load helpers.
  - [x] Define every concrete fact category with attach point, phase owner, durability, producer, consumer, and validation rule.
  - [x] Add low-friction fact-transfer helpers for every lowering builder that creates new handles.
  - [x] Add phase-boundary validation for stale handles, dropped `forbid-drop` facts, ABI facts, alias facts, layout facts, and durable package facts.

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
  - [x] Finalize the public `.starkpkg` contract and `stark inspect-pkg --format json|text` behavior.
  - [x] Design the durable sectioned binary format with magic, exact version, section IDs, offsets, lengths, string tables, typed indexes, and target/profile facts.
  - [ ] Port logical package models, builders, loaders, source bridge, shared codecs, and deterministic inspection rendering.
    - [x] Validate and inspect the host logical `STRS`/`PINF`/`MANF` section wrapper in the self-host package-image reader.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [ ] Decode `MANF` and build the self-host logical package model from binary images.
    - [ ] Port builders, source bridge, shared codecs, and deterministic inspection rendering for logical sections.
  - [x] Add diagnostics for malformed headers, unknown required sections, bad offsets, version mismatches, target/profile mismatches, and legacy JSON bridge failures.
  - [x] Route binary package images into the accepted build layout and keep inspection views explicit.

- [ ] Implement CLI, project driver, manifest handling, native-toolchain driver, and build entry points.
  - [ ] Port `Program`, `CompilerCli`, project driver, build entry points, and project command routing.
  - [ ] Replace host-style manifest parsing with `System.Toml` plus typed manifest decoding.
  - [ ] Port native toolchain discovery, target detection, linker/archiver invocation, and SDK checks.
  - [ ] Preserve project build layout, incremental stamps, stdlib discovery, and package-image generation.

- [x] Implement small fact and assembly-metadata leaf helpers.
  - [x] Add initial assembly architecture facts and MIR assembly metadata serialization.
  - [x] Port register, target-triple architecture, target platform, FFI ABI, and C data-model fact helpers.
  - [x] Port native metadata manifest and implicit-library fact helpers.

---


## 3. Tooling And Packaging

- [~] Complete libLLVM-primary backend integration through the LLVM C API.
  - [x] Finish `System.C` C string and owned foreign-message helper coverage needed by LLVM.
  - [x] Implement LLVM C API bindings, version checks, required-symbol checks, and typed wrapper drops.
  - [x] Add direct object emission, verifier diagnostics, optional module printing, and backend smoke tests.
    - [x] Expose typed wrappers for module target/data-layout, target lookup, target-machine creation, function declarations, module printing/verification, and object memory buffers.
    - [x] Expose typed wrappers for basic blocks, builder positioning, integer constants, and return terminators.
    - [x] Expose typed wrappers for global declarations and global-object facts including linkage, visibility, alignment, section, and constant/initializer state.
    - [x] Expose typed wrappers for load/store/GEP/call construction and ABI/performance fact attachments.
    - [x] Expose typed wrappers for function parameters, control flow, scalar integer ops, compares, selects, and PHI incoming edges.
    - [x] Add libLLVM-linked smoke coverage for direct module construction, verifier diagnostics, module printing, and object emission.

- [~] Complete binary package-image generation/loading and `stark inspect-pkg`.
  - [x] Implement the selfhost MIR package-image leaf codec and deterministic summary inspection.
  - [ ] Implement the full compiler package-image logical section model and binary loader.
    - [x] Validate and inspect the host logical `STRS`/`PINF`/`MANF` section wrapper in self-host code.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [ ] Decode `MANF` and materialize logical package-image facts without source reconstruction.
  - [x] Add `stark inspect-pkg` as a top-level compiler command.
  - [x] Update package-image docs and tests after public spelling lands.

- [~] Complete native/libLLVM toolchain discovery, bundled toolchain support, target facts, C data-model facts, and aggregate-layout facts.
  - [x] Resolve release policies for LLVM version, official archive acquisition, Linux no-libc policy, Windows linker-driver policy, macOS SDK policy, and `--toolchain-dir` scope.
  - [x] Add a toolchain resolver for libLLVM, `clang`, linkers, archivers, SDKs, and helper tools.
  - [x] Add override precedence for CLI flags, environment variables, user config, bundled tools, and `PATH`.
  - [x] Validate target triple, data layout, C aliases, aggregate layout, and package compatibility before backend use.

- [x] Complete release packaging, `stark doctor`, and clean-machine archive verification.
  - [x] Define the release archive layout for compiler, stdlib, vendor, toolchain, licenses, install docs, and release metadata.
  - [x] Add runtime-specific publish or native compiler archive assembly for Linux, Windows, and macOS.
  - [x] Bundle pinned LLVM 22.1.8 artifacts and record source archives, checksums, and license files.
  - [x] Build and include standard library and vendor library source plus required package/native artifacts.
  - [x] Add a manually triggered release workflow that creates downloadable relocatable archives.
  - [x] Add `stark doctor` with compiler version, runtime ID, toolchain paths, versions, target facts, stdlib path, and SDK status.
  - [x] Add clean-machine smoke tests for archive help, check, MIR, SSA, LLVM, object, library, executable, native dependency, and runtime basics.

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
  - [x] Finish `System.C` C-string helpers and LLVM-specific owner wrappers.

- [~] Keep platform boundaries explicit.
  - [x] Use Linux syscall-backed/no-libc stdlib and runtime code for Stark-owned Linux behavior.
  - [x] Use the current Windows executable-generation path for the compiler release.
  - [x] Require local macOS SDK or Command Line Tools and diagnose missing pieces through `stark doctor`.
  - [x] Add platform-specific diagnostics for SDK, CRT, pkg-config, and native/vendor dependency requirements.

- [x] Preserve the official vendor library as a first-class release component.
  - [x] Add vendor source and generated artifacts to release archive layout.
  - [x] Add vendor package/native metadata discovery and diagnostics after the vendor branch merges.

---

## 5. Ported Test Pass

Do this after the compiler infrastructure above is online enough for the tests
to exercise the real self-hosted compiler path. Use
[TestPassLedger.md](TestPassLedger.md) for counts, failure-family notes, and
historical triage.

- [x] Fix the package-image input/protocol gap that blocks package-backed compiler and LLVM tests.
  - [x] Prove package-backed LLVM compilation through the existing host-test harness.
  - [x] Add the remaining package-backed LLVM callable-value coverage.
  - [x] Add the remaining manifest-backed compiler test coverage.
    - [x] Route typed-body package-image compiler ports through typed-only package images.
    - [x] Restore CLI stdout/file-existence/manifest-byte/runtime assertions or explicit equivalents.
  - [x] Add any missing typed-only package-codegen flag or equivalent protocol path.

- [x] Align SSA/MIR artifact selection and rendered-fragment expectations for ported text tests.
  - [x] Fix verified SSA families including ArithmeticFold, ValueFacts, AliasAware, ScopedNoAlias, Cleanup, ScalarReplacement, InlineSsa, FunctionAddress, ConstantText, TextView, and DynamicStorage.
  - [x] Fix remaining source-ok SSA text-class tests by selecting the actual artifact and spelling fragments as rendered.
  - [x] Fix remaining source-expressible SSA type/range source ports.
  - [x] Fix remaining MIR text and structural artifact expectations.
    - [x] Fix verified MIR switch-pattern, place-lowerer, generic, and lowering-contract artifact expectations.
    - [x] Recheck remaining MIR failure families with narrow filters or an intentional rebaseline.

- [x] Add the structured invalid-IR fixture path needed for source-inexpressible validator coverage.
  - [x] Define a test-only fixture API for invalid MIR, SSA, and package-artifact validator inputs.
  - [x] Port invalid-SSA validator tests to the fixture path or record explicit host-internal exclusions.

- [x] Add target-triple pinning or platform gating for non-macOS artifact and native-runtime tests.
  - [x] Cross-target compile artifact-only Linux and Windows tests on macOS where no foreign SDK/runtime is required.
  - [x] Platform-gate tests that require foreign SDKs, linkers, syscalls, or runtime behavior.
  - [x] Add comments explaining each platform-only pass condition.

- [x] Finish option-toggle plumbing used by remaining LLVM lowering tests.
  - [x] Add the missing host-test protocol switches for qualifier, internalization, target, package, and inspection variants.
  - [x] Verify the remaining LLVM per-test residues after option plumbing lands.

- [ ] Resolve remaining suite failures after infrastructure lands.
  - [ ] Resolve `compiler.Tests` package-image, diagnostics, type-checking, ownership, pipeline, runtime, CLI, and example failures.
  - [x] Resolve `compiler.SsaTests` dynamic-storage failures.
  - [x] Resolve `compiler.LlvmTests` package-image and genuine per-test residues.
  - [x] Resolve `compiler.MirTests` MIR text and structural failures.
  - [ ] Resolve `stdlib.Port` platform-specific, source-stdlib, dispatch, and miscellaneous failures.
    - [x] Resolve the `standard-library-generic` and `io-path` collection residues.
    - [x] Resolve the `io-file` collection residues.
    - [x] Resolve the `io-file-runtime` collection residues.
    - [x] Resolve the `memory-helper` collection residues.
    - [x] Resolve the `memory` collection residues.
    - [x] Recheck the `threading` collection residue.
    - [x] Recheck the `threading-atomics` collection residue.
    - [x] Recheck the `runtime-platform-windows` collection residue.
    - [x] Resolve the `collections-dictionary` collection residue.
    - [x] Resolve the `collections-hash-set-sort` collection residue.
    - [x] Recheck the `collections-stack-queue` collection residue.
    - [x] Resolve the `collections` collection residue.
    - [x] Resolve the `text` collection residue.
    - [x] Recheck the `text-runtime` collection residue.
    - [x] Recheck the `text-interning` collection residue.
    - [x] Resolve the `promoted-runtime-buffer` collection residue.
    - [x] Resolve the `promoted-console` collection residue.
    - [x] Recheck and strengthen the `promoted-io-file-system` collection residue.
    - [x] Resolve the `promoted-net-tcp` collection residue.
    - [x] Recheck the `runtime-buffer` collection residue.
    - [x] Recheck the `console` collection residue.
    - [x] Resolve the `process` collection residue.
    - [x] Recheck the `net`, `file-system`, `json`, `math`, `c`, `compiler-integer-facts`, and `backend-boundary-audit` collection residues.
    - [x] Resolve the `memory-contract-audit` collection residue.
    - [x] Resolve the `raw-pointer-audit` collection residue.
  - [x] Recheck the lone `compiler.FeatureTests` failure and close it if still reproducible.

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


## 10 Cut over -Deferred until ALL other things are complete
- keep C# compiler in Src
- Self-host compiler eventually is compiable by itself
- Sus out any issues that show up by switching to compiling itself
  - address them as subtasks here:
- Add update benchmarks to run for each stage of the compiler. So we should have 3 stage0, stage1, and stage2
