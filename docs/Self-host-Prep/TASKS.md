# Self-Host-Prep Tasks

This is the executable task list for self-host prep. Keep this file as a work
queue plus any instructions needed to execute the work. Do not use it as a
progress ledger, and do not rewrite task descriptions just to record partial
progress.

Use `[x]` for complete, `[~]` for partially implemented, and `[ ]` for open.
Track status by flipping checkboxes only; put evidence, counts, and triage notes
in [TestPassLedger.md](TestPassLedger.md) or the relevant companion document.
Do not read raw checkbox counts as percent completion; tasks intentionally vary
in size, and self-hosting is not online until the cutover gates are complete.
Keep each checkbox to one sentence-sized deliverable; split larger work into
child checkboxes instead of expanding the sentence. Parent checkboxes may group
a workstream, but executable work belongs in child checkboxes.

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
    - [x] Parse imports and module headers.
    - [x] Parse top-level declarations.
    - [x] Parse function signatures and function bodies.
    - [x] Parse struct fields.
    - [x] Parse enum variants and enum payloads.
    - [x] Parse statements and nested scopes.
    - [x] Parse loops and switch sections.
    - [x] Parse expressions with current precedence.
    - [x] Capture type spans for signatures.
    - [x] Capture type spans for fields and enum payloads.
    - [x] Capture type spans for locals.
    - [x] Parse struct and enum `where` clauses.
    - [x] Parse switch-case pattern value references.
    - [x] Implement the parser facade.
    - [x] Implement the Stark-native syntax tree or parse-event model.
    - [x] Port text literal decoding with current raw-string parity semantics.
  - [~] Implement name binding and type-reference resolution.
    - [x] Build declaration tables.
    - [x] Build function scopes.
    - [x] Track lexical local visibility.
    - [x] Emit structured bind diagnostics.
    - [x] Resolve value references.
    - [x] Resolve signature types.
    - [x] Resolve field types.
    - [x] Resolve enum payload types.
    - [x] Resolve local types.
    - [x] Resolve function `where` constraints.
    - [x] Resolve nested generic argument types and complete type compatibility facts.
    - [x] Implement module resolution and imported-package/source lookup.
    - [x] Build callable candidate sets.
    - [x] Build receiver candidate sets.
    - [x] Build generic use-site syntax facts.
  - [~] Implement type checking and semantic validation.
    - [x] Diagnose non-boolean conditions.
    - [x] Diagnose invalid logical operands.
    - [x] Diagnose void-return mismatches.
    - [x] Diagnose duplicate enum variants.
    - [x] Diagnose invalid break and continue use.
    - [x] Build typed module symbols.
    - [x] Build typed declaration symbols.
    - [x] Build typed member and method symbols.
    - [x] Build typed local and parameter symbols.
    - [x] Build typed generic parameter symbols.
    - [x] Type function signatures.
    - [x] Type global declarations.
    - [x] Type struct and record fields.
    - [x] Type enum payloads.
    - [x] Type local declarations.
    - [x] Type explicit storage selectors.
    - [x] Type literal expressions.
    - [x] Type identifier expressions.
    - [x] Type call expressions.
    - [x] Type member-chain expressions.
    - [x] Type indexing and slicing expressions.
    - [x] Type assignment expressions.
    - [x] Type return expressions.
    - [x] Type conversions and coercions.
    - [x] Implement overload resolution.
    - [x] Implement method resolution.
    - [x] Implement trait conformance lookup.
    - [x] Implement generic use-site instantiation planning.
    - [x] Build function effect summaries.
    - [x] Enforce function-kind obligations.
    - [x] Validate law body restrictions.
    - [x] Validate externally visible effects.
    - [x] Validate allocation restrictions.
    - [x] Validate recursion rules.
    - [x] Validate layout-dependent semantics.
      - [x] Validate layout-control attributes and FFI-safe fields.
      - [x] Validate recursive inline-layout cycles.
      - [x] Validate packed-field safe borrows.
      - [x] Validate concrete layout availability for layout queries.
    - [x] Validate exported surfaces.
    - [~] Port ownership validation.
      - [x] Port structural copyability classification.
      - [x] Validate direct `where Copyable(...)` call predicates.
    - [~] Port borrow liveness.
      - [x] Validate straight-line local borrow conflicts.
      - [x] Validate straight-line assignment and local-initializer moves across live borrows.
    - [~] Port drop validity.
      - [x] Validate destructor declaration shape and local body restrictions.
      - [~] Validate destructor body memory effects.
        - [x] Reject direct storage allocation and dynamic-storage mutation in destructor bodies.
        - [ ] Build destructor effect summaries for transitive call and implicit-drop effects.
    - [~] Port move semantics.
      - [x] Validate straight-line direct-call moves before later reads.
      - [x] Validate straight-line whole-variable assignment and local-initializer moves before later reads.
    - [~] Port initialization guarantees.
      - [x] Reject straight-line constructor reads of owning `self` fields before assignment.
      - [~] Validate branch-complete constructor field assignment.
        - [x] Validate `if`/`else` constructor field assignment joins.
        - [ ] Validate exhaustive `switch` constructor field assignment joins.
        - [x] Validate early-exit constructor paths before merging assignment facts.
      - [~] Validate local and `out`/`init` write-before-read guarantees.
        - [x] Reject straight-line local and `out`/`init` reads before assignment.
        - [ ] Validate branch-complete local and `out`/`init` assignment before reads.
        - [ ] Validate successful-return write obligations for `out`/`init` outputs.
    - [~] Port range facts.
      - [x] Preserve arithmetic integer range endpoint facts through MIR LLVM range attributes.
      - [x] Propagate value-derived range facts through MIR lowering and joins.
      - [x] Validate range facts against branch and comparison proofs.
    - [~] Port enum layout facts.
      - [x] Build direct-tag enum variant and scalar payload layout fact tables.
      - [x] Resolve nested non-generic local struct and record enum payload layout facts.
      - [x] Resolve generic aggregate enum payload layout substitutions.
      - [x] Preserve C pack, explicit field offsets, and aggregate align attributes in enum payload size and alignment facts.
      - [x] Preserve enum payload storage misalignment facts where lowered payload storage is under-aligned.
      - [~] Fold enum layout facts through CTFE `System.Compiler` queries.
        - [x] Validate enum tag, variant, and payload layout query calls in the binder.
        - [ ] Fold enum tag layout queries to compile-time constants.
        - [ ] Fold enum variant payload layout queries to compile-time constants.
      - [~] Preserve enum layout facts through MIR, package images, ABI, and LLVM lowering.
        - [x] Define enum layout as a durable type-attached MIR fact category.
        - [x] Validate enum layout facts as layout facts at MIR phase boundaries.
        - [ ] Attach typed enum layout rows to MIR lowering fact records.
        - [ ] Serialize enum layout fact rows in package images.
        - [ ] Load enum layout fact rows from package images.
        - [ ] Carry enum tag and payload layout facts into ABI lowering.
        - [ ] Emit LLVM IR from enum layout facts without recomputing layout.
    - [ ] Port CTFE.
    - [ ] Port compile-time generic value evaluation.

- [ ] Implement diagnostics, compiler artifacts, pipeline orchestration, and artifact rendering.
  - [ ] Port diagnostic records.
  - [ ] Port stable diagnostic codes.
  - [ ] Port diagnostic severity handling.
  - [ ] Port source spans and source-file mapping.
  - [ ] Port source-caret rendering.
  - [ ] Port deterministic diagnostic text output.
  - [ ] Port artifact keys.
  - [ ] Port artifact storage.
  - [ ] Port artifact dependency validation.
  - [ ] Port typed artifact access.
  - [ ] Port parse and syntax artifact renderers.
  - [ ] Port type and semantic artifact renderers.
  - [ ] Port MIR and SSA artifact renderers.
  - [ ] Port LLVM and package artifact renderers.
  - [ ] Port the pass dependency graph.
  - [ ] Port pass execution records.
  - [ ] Port stop-after pass boundaries.
  - [ ] Port pass crash diagnostics.
  - [ ] Add fast typed artifact inspection APIs.
  - [ ] Add stage-comparison artifact APIs.

- [x] Implement the IR memory model, MIR foundations, and fact-transfer substrate.
  - [x] Implement typed handle wrappers in `selfhost/Compiler/Ir.stark`.
  - [x] Implement the dense `IrTable<T>` model in `selfhost/Compiler/Ir.stark`.
  - [x] Implement initial `ValueFacts`.
  - [x] Implement initial `AbiKind`.
  - [x] Implement present-fact inheritance helpers.
  - [x] Implement MIR instruction helpers.
  - [x] Implement MIR block helpers.
  - [x] Implement MIR function helpers.
  - [x] Implement MIR global helpers.
  - [x] Implement MIR control-flow helpers.
  - [x] Implement MIR call helpers.
  - [x] Implement MIR phi helpers.
  - [x] Implement basic textual LLVM subset helpers.
  - [x] Implement MIR byte codecs.
  - [x] Implement MIR1 package-image sections.
  - [x] Implement MIR2 package-image sections.
  - [x] Implement MIR package-image validation.
  - [x] Implement MIR inspection summaries.
  - [x] Implement MIR package-image file save helpers.
  - [x] Implement MIR package-image file load helpers.
  - [x] Define fact attach points.
  - [x] Define fact phase owners.
  - [x] Define fact durability rules.
  - [x] Define fact producers.
  - [x] Define fact consumers.
  - [x] Define fact validation rules.
  - [x] Add low-friction fact-transfer helpers for lowering builders.
  - [x] Add phase-boundary validation for stale handles.
  - [x] Add phase-boundary validation for dropped `forbid-drop` facts.
  - [x] Add phase-boundary validation for ABI facts.
  - [x] Add phase-boundary validation for alias facts.
  - [x] Add phase-boundary validation for layout facts.
  - [x] Add phase-boundary validation for durable package facts.

- [ ] Implement HIR/MIR lowering.
  - [ ] Define the self-host HIR model or explicit direct-to-MIR boundary.
  - [ ] Port the MIR lowering pass shell.
  - [ ] Port function builder state.
  - [ ] Port lowering symbol maps.
  - [ ] Port MIR block creation.
  - [ ] Lower literals.
  - [ ] Lower locals and parameters.
  - [ ] Lower assignments.
  - [ ] Lower arithmetic and comparisons.
  - [ ] Lower calls.
  - [ ] Lower returns.
  - [ ] Lower places and member access.
  - [ ] Lower indexing and slicing.
  - [ ] Lower globals.
  - [ ] Lower raw pointers.
  - [ ] Lower address-taking.
  - [ ] Lower if expressions and statements.
  - [ ] Lower loops.
  - [ ] Lower `for` and `foreach`.
  - [ ] Lower switch.
  - [ ] Lower `try`.
  - [ ] Lower `become`.
  - [ ] Lower recursion and tail calls.
  - [ ] Lower object construction.
  - [ ] Lower enum payloads.
  - [ ] Lower dynamic storage.
  - [ ] Lower all storage selectors.
  - [ ] Port runtime drop lowering.
  - [ ] Port destructor call insertion.
  - [ ] Port ownership-driven cleanup.
  - [ ] Port compile-time evaluator lowering.
  - [ ] Port compile-time evaluated expressions.
  - [ ] Port imported source lowering.
  - [ ] Port imported-template lowering.
  - [ ] Preserve range facts through MIR lowering.
  - [ ] Preserve alias facts through MIR lowering.
  - [ ] Preserve ABI facts through MIR lowering.
  - [ ] Preserve layout facts through MIR lowering.
  - [ ] Preserve ownership facts through MIR lowering.
  - [ ] Preserve assembly facts through MIR lowering.
  - [ ] Preserve arena facts through MIR lowering.
  - [ ] Preserve drop facts through MIR lowering.

- [ ] Implement SSA lowering.
  - [ ] Port MIR-to-SSA lowering.
  - [ ] Port SSA block shaping.
  - [ ] Port SSA phi construction.
  - [ ] Port memory operation lowering.
  - [ ] Port the SSA artifact model.
  - [ ] Port the SSA deterministic renderer.
  - [ ] Port the structured invalid-IR fixture path.
  - [ ] Port SSA type validation.
  - [ ] Port SSA dominance validation.
  - [ ] Port SSA control-flow validation.
  - [ ] Port SSA memory validation.
  - [ ] Port SSA range-fact validation.
  - [ ] Port SSA ABI-fact validation.
  - [ ] Port SSA package-fact validation.
  - [ ] Port value fact analysis.
  - [ ] Revalidate facts at each SSA rewrite boundary.
  - [ ] Port cleanup.
  - [ ] Port branch pruning.
  - [ ] Port branch shaping.
  - [ ] Port constant propagation.
  - [ ] Port arithmetic folding.
  - [ ] Port CSE.
  - [ ] Port direct-call inlining.
  - [ ] Port devirtualization.
  - [ ] Port constant text specialization.
  - [ ] Port dynamic storage optimization.
  - [ ] Port dynamic append loop specialization.
  - [ ] Port stdlib helper specialization.
  - [ ] Port alias-aware memory optimization.
  - [ ] Port aggregate construction optimization.
  - [ ] Port SROA.
  - [ ] Port ownership traffic optimization.
  - [ ] Preserve backend facts consumed by ABI and LLVM lowering through every SSA pass.

- [~] Implement ABI lowering.
  - [ ] Port ABI lowering from optimized SSA into target-shaped callable and global layouts.
  - [ ] Lower structs to ABI values.
  - [ ] Lower records to ABI values.
  - [ ] Lower enums to ABI values.
  - [ ] Lower slices to ABI values.
  - [ ] Lower dynamic storage to ABI values.
  - [ ] Lower function pointers to ABI values.
  - [ ] Lower closures to ABI values.
  - [ ] Lower FFI signatures to ABI values.
  - [x] Add LLVM C API typed opaque handles.
  - [x] Add LLVM C API C-string helpers.
  - [x] Add LLVM C API out-pointer helpers.
  - [x] Add LLVM diagnostic message wrappers.
  - [x] Add deterministic LLVM dispose wrappers.
  - [ ] Build LLVM modules directly from ABI/SSA through libLLVM.
  - [ ] Attach range facts to LLVM IR.
  - [ ] Attach noalias facts to LLVM IR.
  - [ ] Attach readonly facts to LLVM IR.
  - [ ] Attach alignment facts to LLVM IR.
  - [ ] Attach volatile facts to LLVM IR.
  - [ ] Attach calling-convention facts to LLVM IR.
  - [ ] Attach linkage facts to LLVM IR.
  - [ ] Attach section facts to LLVM IR.
  - [ ] Attach visibility facts to LLVM IR.
  - [ ] Emit object files through libLLVM.
  - [ ] Route emitted objects into the project build layout.
  - [ ] Keep textual LLVM as deterministic inspection/debug output from the in-memory LLVM module.

- [~] Implement package-image models, builders, loaders, bridge codecs, binary load, and deterministic inspection.
  - [x] Decide binary-first package-image policy with JSON/text inspection views.
  - [x] Implement the selfhost MIR package-image leaf codec.
  - [x] Implement selfhost MIR package-image validation statuses.
  - [x] Implement deterministic text summaries for selfhost MIR package images.
  - [x] Implement deterministic JSON summaries for selfhost MIR package images.
  - [x] Implement on-disk round trips for selfhost MIR package images.
  - [x] Finalize the public `.starkpkg` contract and `stark inspect-pkg --format json|text` behavior.
  - [x] Design durable package-image magic.
  - [x] Design durable package-image exact versioning.
  - [x] Design durable package-image section IDs.
  - [x] Design durable package-image section offsets.
  - [x] Design durable package-image section lengths.
  - [x] Design durable package-image string tables.
  - [x] Design durable package-image typed indexes.
  - [x] Design durable package-image target facts.
  - [x] Design durable package-image profile facts.
  - [ ] Implement the logical package-image load path.
    - [x] Validate the host logical `STRS`/`PINF`/`MANF` section wrapper.
    - [x] Inspect the host logical `STRS`/`PINF`/`MANF` section wrapper.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [ ] Decode `MANF` and build the self-host logical package model from binary images.
    - [ ] Materialize typed interface declarations.
    - [ ] Materialize typed interface functions and methods.
    - [ ] Materialize typed interface globals.
    - [ ] Materialize typed interface traits.
    - [ ] Materialize typed interface layouts and aliases.
    - [ ] Materialize effect fact sections.
    - [ ] Materialize ownership fact sections.
    - [ ] Materialize range fact sections.
    - [ ] Materialize aliasing fact sections.
    - [ ] Materialize ABI fact sections.
    - [ ] Materialize layout fact sections.
    - [ ] Materialize native metadata fact sections.
    - [ ] Materialize generic-template sections without source reconstruction.
    - [ ] Port the package-image source bridge for Stage0/Stage1 compatibility.
    - [ ] Validate package stage compatibility.
    - [ ] Validate package profile compatibility.
    - [ ] Validate package target compatibility.
    - [ ] Validate package backend fact compatibility.
    - [ ] Validate package version compatibility.
  - [ ] Port logical package models.
  - [ ] Port logical package builders.
  - [ ] Port shared package codecs.
  - [ ] Port deterministic package inspection rendering.
  - [x] Diagnose malformed package-image headers.
  - [x] Diagnose unknown required package-image sections.
  - [x] Diagnose bad package-image offsets.
  - [x] Diagnose package-image version mismatches.
  - [x] Diagnose package-image target mismatches.
  - [x] Diagnose package-image profile mismatches.
  - [x] Diagnose legacy JSON bridge failures.
  - [x] Route binary package images into the accepted build layout and keep inspection views explicit.

- [ ] Implement CLI and project driver.
  - [ ] Port `Program`.
  - [ ] Port `CompilerCli`.
  - [ ] Port command dispatch.
  - [ ] Port option parsing.
  - [ ] Port command diagnostics.
  - [ ] Port check mode.
  - [ ] Port MIR mode.
  - [ ] Port SSA mode.
  - [ ] Port LLVM mode.
  - [ ] Port object mode.
  - [ ] Port executable mode.
  - [ ] Port library mode.
  - [ ] Port package-inspection mode.
  - [ ] Port doctor mode.
  - [ ] Port test mode.
  - [ ] Port build mode.
  - [ ] Replace host-style manifest parsing with `System.Toml` plus typed manifest decoding.
  - [ ] Port project graph loading.
  - [ ] Port solution graph loading.
  - [ ] Port package references.
  - [ ] Port source roots.
  - [ ] Port dependency ordering.
  - [ ] Port stdlib discovery.
  - [ ] Port vendor discovery.
  - [ ] Port override path handling.
  - [ ] Port incremental stamps.
  - [ ] Port build layout.
  - [ ] Port artifact routing.
  - [ ] Port package-image generation.
  - [ ] Port target detection.
  - [ ] Port native toolchain discovery.
  - [ ] Port linker invocation.
  - [ ] Port archiver invocation.
  - [ ] Port SDK checks.
  - [ ] Port generated test runner creation.
  - [ ] Port test filtering.
  - [ ] Port test execution.
  - [ ] Port platform gating.

- [x] Implement small fact and assembly-metadata leaf helpers.
  - [x] Add initial assembly architecture facts and MIR assembly metadata serialization.
  - [x] Port register, target-triple architecture, target platform, FFI ABI, and C data-model fact helpers.
  - [x] Port native metadata manifest and implicit-library fact helpers.

---


## 3. Tooling And Packaging

- [~] Complete libLLVM-primary backend integration through the LLVM C API.
  - [x] Finish `System.C` C string and owned foreign-message helper coverage needed by LLVM.
  - [x] Implement LLVM C API bindings.
  - [x] Implement LLVM version checks.
  - [x] Implement LLVM required-symbol checks.
  - [x] Implement typed LLVM wrapper drops.
  - [x] Add direct object emission.
  - [x] Add verifier diagnostics.
  - [x] Add optional LLVM module printing.
  - [x] Add LLVM backend smoke tests.
    - [x] Expose typed wrappers for module target and data layout.
    - [x] Expose typed wrappers for target lookup.
    - [x] Expose typed wrappers for target-machine creation.
    - [x] Expose typed wrappers for function declarations.
    - [x] Expose typed wrappers for module printing and verification.
    - [x] Expose typed wrappers for object memory buffers.
    - [x] Expose typed wrappers for basic blocks, builder positioning, integer constants, and return terminators.
    - [x] Expose typed wrappers for global declarations.
    - [x] Expose typed wrappers for global linkage facts.
    - [x] Expose typed wrappers for global visibility facts.
    - [x] Expose typed wrappers for global alignment facts.
    - [x] Expose typed wrappers for global section facts.
    - [x] Expose typed wrappers for global constant and initializer state.
    - [x] Expose typed wrappers for load/store/GEP/call construction and ABI/performance fact attachments.
    - [x] Expose typed wrappers for function parameters.
    - [x] Expose typed wrappers for control flow.
    - [x] Expose typed wrappers for scalar integer ops.
    - [x] Expose typed wrappers for compares.
    - [x] Expose typed wrappers for selects.
    - [x] Expose typed wrappers for PHI incoming edges.
    - [x] Add libLLVM-linked smoke coverage for direct module construction.
    - [x] Add libLLVM-linked smoke coverage for verifier diagnostics.
    - [x] Add libLLVM-linked smoke coverage for module printing.
    - [x] Add libLLVM-linked smoke coverage for object emission.

- [~] Complete binary package-image generation/loading and `stark inspect-pkg`.
  - [x] Implement the selfhost MIR package-image leaf codec and deterministic summary inspection.
  - [ ] Implement the full compiler package-image logical section model and binary loader.
    - [x] Validate and inspect the host logical `STRS`/`PINF`/`MANF` section wrapper in self-host code.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [ ] Decode `MANF` and materialize logical package-image facts without source reconstruction.
  - [x] Add `stark inspect-pkg` as a top-level compiler command.
  - [x] Update package-image docs and tests after public spelling lands.

- [~] Complete native toolchain discovery and target facts.
  - [x] Resolve the LLVM version policy.
  - [x] Resolve the official archive acquisition policy.
  - [x] Resolve the Linux no-libc policy.
  - [x] Resolve the Windows linker-driver policy.
  - [x] Resolve the macOS SDK policy.
  - [x] Resolve the `--toolchain-dir` scope policy.
  - [x] Add a toolchain resolver for libLLVM, `clang`, linkers, archivers, SDKs, and helper tools.
  - [x] Add override precedence for CLI flags, environment variables, user config, bundled tools, and `PATH`.
  - [x] Validate the target triple before backend use.
  - [x] Validate the data layout before backend use.
  - [x] Validate C aliases before backend use.
  - [x] Validate aggregate layout before backend use.
  - [x] Validate package compatibility before backend use.

- [x] Complete release packaging, `stark doctor`, and clean-machine archive verification.
  - [x] Include the compiler in the release archive layout.
  - [x] Include the standard library in the release archive layout.
  - [x] Include the vendor library in the release archive layout.
  - [x] Include toolchain artifacts in the release archive layout.
  - [x] Include licenses in the release archive layout.
  - [x] Include install docs in the release archive layout.
  - [x] Include release metadata in the release archive layout.
  - [x] Add runtime-specific archive assembly for Linux.
  - [x] Add runtime-specific archive assembly for Windows.
  - [x] Add runtime-specific archive assembly for macOS.
  - [x] Bundle pinned LLVM 22.1.8 artifacts and record source archives, checksums, and license files.
  - [x] Build and include standard library and vendor library source plus required package/native artifacts.
  - [x] Add a manually triggered release workflow that creates downloadable relocatable archives.
  - [x] Add compiler version reporting to `stark doctor`.
  - [x] Add runtime ID reporting to `stark doctor`.
  - [x] Add toolchain path reporting to `stark doctor`.
  - [x] Add toolchain version reporting to `stark doctor`.
  - [x] Add target fact reporting to `stark doctor`.
  - [x] Add standard library path reporting to `stark doctor`.
  - [x] Add SDK status reporting to `stark doctor`.
  - [x] Add clean-machine archive smoke tests for `stark --help`.
  - [x] Add clean-machine archive smoke tests for `stark check`.
  - [x] Add clean-machine archive smoke tests for MIR output.
  - [x] Add clean-machine archive smoke tests for SSA output.
  - [x] Add clean-machine archive smoke tests for LLVM output.
  - [x] Add clean-machine archive smoke tests for object output.
  - [x] Add clean-machine archive smoke tests for library output.
  - [x] Add clean-machine archive smoke tests for executable output.
  - [x] Add clean-machine archive smoke tests for native dependencies.
  - [x] Add clean-machine archive smoke tests for runtime basics.

- [ ] Sync editor syntax and completions with the self-hosting language surface.
  - [ ] Update grammar-derived syntax highlighting, completions, snippets, and stdlib symbol data.
  - [ ] Verify coverage against the canonical language surface after parser/selfhost syntax changes land.

---

## 4. Standard Library And Porting APIs

- [~] Migrate stdlib and compiler-port APIs to `Option<T>` / `Result<T, E>` conventions.
  - [x] Implement role-based `[Ok]` propagation.
  - [x] Implement role-based `[Err]` propagation.
  - [x] Implement `try` propagation.
  - [x] Implement `from` funnels.
  - [ ] Replace nullable-shaped APIs in compiler-port code.
  - [ ] Replace ad hoc `Try*` out patterns in compiler-port code.
  - [ ] Replace exception-shaped recoverable failures in compiler-port code.
  - [ ] Keep diagnostic-shaped invariant violations explicit.
  - [ ] Keep validation-shaped invariant violations explicit.
  - [ ] Keep trap-shaped invariant violations explicit.

- [~] Finish standard library surfaces required by the compiler port.
  - [~] Finish compiler-grade text services.
    - [ ] Finish compiler-grade text builders.
    - [ ] Finish compiler-grade escaping.
    - [ ] Finish compiler-grade formatting.
    - [ ] Finish compiler-grade diagnostic rendering.
  - [~] Finish deterministic artifact snapshots.
    - [ ] Finish golden-file support.
    - [ ] Finish snapshot support.
    - [ ] Finish deterministic compiler-artifact comparison helpers.
  - [~] Finish deterministic collection surfaces.
    - [ ] Finish generic collections.
    - [ ] Finish sorted maps.
    - [ ] Finish sorted sets.
    - [ ] Finish deterministic iteration surfaces.
  - [~] Finish compiler symbol storage helpers.
    - [ ] Finish typed interning.
    - [ ] Finish compiler symbol-table migration helpers.
  - [~] Finish filesystem parity.
    - [ ] Finish file APIs.
    - [ ] Finish filesystem APIs.
    - [ ] Finish path APIs.
    - [ ] Finish recursive walk APIs.
    - [ ] Finish temp-directory APIs.
  - [~] Finish process metadata helpers.
    - [ ] Finish process helpers used by build drivers.
    - [ ] Finish platform metadata helpers used by native-toolchain drivers.
  - [~] Finish `System.Toml` typed manifest decoding.
    - [ ] Decode project manifests.
    - [ ] Decode package manifests.
    - [ ] Decode solution manifests.
  - [~] Finish package inspection data formats.
    - [ ] Finish JSON support for `stark inspect-pkg`.
    - [ ] Finish package inspection support for golden tests.
  - [x] Finish `System.C` C-string helpers and LLVM-specific owner wrappers.

- [~] Keep platform boundaries explicit.
  - [x] Use Linux syscall-backed/no-libc stdlib and runtime code for Stark-owned Linux behavior.
  - [x] Use the current Windows executable-generation path for the compiler release.
  - [x] Require local macOS SDK or Command Line Tools and diagnose missing pieces through `stark doctor`.
  - [x] Add platform-specific diagnostics for SDK requirements.
  - [x] Add platform-specific diagnostics for CRT requirements.
  - [x] Add platform-specific diagnostics for pkg-config requirements.
  - [x] Add platform-specific diagnostics for native dependency requirements.
  - [x] Add platform-specific diagnostics for vendor dependency requirements.

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
  - [x] Fix verified `ArithmeticFold` SSA tests.
  - [x] Fix verified `ValueFacts` SSA tests.
  - [x] Fix verified `AliasAware` SSA tests.
  - [x] Fix verified `ScopedNoAlias` SSA tests.
  - [x] Fix verified `Cleanup` SSA tests.
  - [x] Fix verified `ScalarReplacement` SSA tests.
  - [x] Fix verified `InlineSsa` SSA tests.
  - [x] Fix verified `FunctionAddress` SSA tests.
  - [x] Fix verified `ConstantText` SSA tests.
  - [x] Fix verified `TextView` SSA tests.
  - [x] Fix verified `DynamicStorage` SSA tests.
  - [x] Select the actual artifact for remaining source-ok SSA text-class tests.
  - [x] Spell expected SSA fragments as rendered.
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
  - [x] Add the host-test protocol switch for qualifier variants.
  - [x] Add the host-test protocol switch for internalization variants.
  - [x] Add the host-test protocol switch for target variants.
  - [x] Add the host-test protocol switch for package variants.
  - [x] Add the host-test protocol switch for inspection variants.
  - [x] Verify the remaining LLVM per-test residues after option plumbing lands.

- [ ] Resolve remaining suite failures after infrastructure lands.
  - [ ] Resolve `compiler.Tests` remaining failures.
    - [ ] Resolve remaining package-image failures.
    - [ ] Resolve remaining diagnostics failures.
    - [ ] Resolve remaining type-checking failures.
    - [ ] Resolve remaining ownership failures.
    - [ ] Resolve remaining pipeline failures.
    - [ ] Resolve remaining runtime failures.
    - [ ] Resolve remaining CLI failures.
    - [ ] Resolve remaining example failures.
    - [x] Restore project test builds to prefer built dependency package images before bundled source fallback.
    - [x] Preserve package archive link order after locally emitted object files.
    - [x] Avoid dependency library rebuilds when only selected test filters change.
    - [x] Repoint CLI signed-range ports at semantic validation.
    - [x] Resolve the `project-cli` collection's cross-module fixture failures.
    - [x] Resolve the `compiler-cli` collection's stale port reductions.
    - [x] Resolve the `package-image-architecture` collection's imported-template and comptime residuals.
    - [x] Resolve the `function-semantics` collection's semantic-validation stage residues.
  - [x] Resolve `compiler.SsaTests` dynamic-storage failures.
  - [x] Resolve `compiler.LlvmTests` package-image and genuine per-test residues.
  - [x] Resolve `compiler.MirTests` MIR text and structural failures.
  - [ ] Resolve `stdlib.Port` remaining failures.
    - [ ] Resolve remaining platform-specific failures.
    - [ ] Resolve remaining source-stdlib failures.
    - [ ] Resolve remaining dispatch failures.
    - [ ] Resolve remaining miscellaneous failures.
    - [x] Resolve the `standard-library-generic` collection residue.
    - [x] Resolve the `io-path` collection residue.
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
    - [x] Recheck the `net` collection residue.
    - [x] Recheck the `file-system` collection residue.
    - [x] Recheck the `json` collection residue.
    - [x] Recheck the `math` collection residue.
    - [x] Recheck the `c` collection residue.
    - [x] Recheck the `compiler-integer-facts` collection residue.
    - [x] Recheck the `backend-boundary-audit` collection residue.
    - [x] Resolve the `memory-contract-audit` collection residue.
    - [x] Resolve the `raw-pointer-audit` collection residue.
    - [x] Resolve the `range-notation` collection residue.
    - [x] Resolve the `runtime-platform-mac-os` collection residue.
    - [x] Recheck the `testing` collection residue.
    - [x] Recheck the `book-sample` collection residue.
    - [x] Recheck the `syscall` collection residue.
    - [x] Recheck the `net-tcp` collection residue.
    - [x] Recheck the `runtime-platform-linux` collection residue.
    - [x] Port the `collections-package-drop-regression` stdlib package regression.
  - [x] Recheck the lone `compiler.FeatureTests` failure and close it if still reproducible.

- [ ] Close test-scope hygiene.
  - [x] Port the final unported qualifying C# test or record an explicit exclusion reason.
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
  - [ ] Update LLVM C API bindings.
  - [ ] Update LLVM IR spelling expectations.
  - [ ] Update bundled toolchain acquisition.
  - [ ] Update bundled package checksums.
  - [ ] Update backend regression tests.

---

## 9. Open Decisions

No open decisions currently tracked.


## 10. Cutover - Deferred Until All Other Work Is Complete

- [ ] Keep the C# compiler in `/src` until Stage1 can build the Stark compiler.
- [ ] Build the Stage1 Stark compiler with Stage0 and emit the expected compiler executable.
- [ ] Build the Stage2 Stark compiler with Stage1 and emit the expected compiler executable.
- [ ] Compare Stage1 and Stage2 package images, diagnostics, artifacts, and executable behavior.
- [ ] Run compiler benchmarks for Stage0, Stage1, and Stage2.
- [ ] Address cutover-only divergences discovered by Stage1/Stage2 comparison.
