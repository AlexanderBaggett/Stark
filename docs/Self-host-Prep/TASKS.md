# Self-Host-Prep Tasks

This is the executable task list for self-host prep. Keep this file as a work
queue plus any instructions needed to execute the work. Do not use it as a
progress ledger, and do not rewrite task descriptions just to record partial
progress.

Use `[x]` for complete, `[~]` for partially implemented, and `[ ]` for open.
Track status by flipping checkboxes only; put evidence, counts, and triage notes
in [TestPassLedger.md](TestPassLedger.md) or the relevant companion document.
Do not read raw checkbox counts as percent completion; tasks intentionally vary
in size, and self-hosted releases are not online until the bootstrap and
release-adoption gates are complete.
Keep each checkbox to one sentence-sized deliverable; split larger work into
child checkboxes instead of expanding the sentence. Parent checkboxes may group
a workstream, but executable work belongs in child checkboxes.

Primary goal: implement the self-host-prep roadmap and make all tests runnable
on macOS pass. The test-pass work is last-mile work; most failing tests depend
on compiler infrastructure that is still being implemented.

Execution constraints:

- Verify slices with `--check`, which since 2026-07-01 runs the full front end
  (through semantic-validate and ownership-validate) over the root and every
  source-imported module; deep emit-path probes remain for runtime behavior.
- Preserve backend facts all the way through lowering to IR.
- Treat correctness and completeness as required scope, not expansion.
- Keep Stark's speed-focused design visible in implementation choices.
- Prefer full tasks over partial slices.
- Do not add package-manager release work; downloadable relocatable archives
  are the release path.

---

## 1. Compiler Port To Stark

Differential-harness findings (2026-07-08, `tests-stark/corpus/pending/`, each
is a stage0-validated program that joins the StageParityTests gate when its
family lands):

- [x] Whole-module path: enum-typed call arguments (`choose(pick)` /
  `choose(Pick.Second)`) reject; enum-typed parameters in the callee
  signature already accept.
- [x] Whole-module path: enum-valued call results into locals
  (`stack Pick pick = choose();`) and enum-valued terminal returns from
  switch cases (`case Tag.Left: return Pick.First;`) reject — the open
  remainder of the enum-return slice.
- [x] Whole-module path: payload capture with a cast in a case terminal
  return (`case Packet.Other(var value): return (i64[min max])value;`)
  rejects.
- [x] Whole-module path: struct declaration + `new()` constructor + field
  reads reject (the single-function bundle path supports these).

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
    - [x] Port drop validity.
      - [x] Validate destructor declaration shape and local body restrictions.
      - [x] Validate destructor body memory effects.
        - [x] Reject direct storage allocation and dynamic-storage mutation in destructor bodies.
        - [x] Build destructor effect summaries for transitive call and implicit-drop effects.
    - [~] Port move semantics.
      - [x] Validate straight-line direct-call moves before later reads.
      - [x] Validate straight-line whole-variable assignment and local-initializer moves before later reads.
    - [x] Port initialization guarantees.
      - [x] Reject straight-line constructor reads of owning `self` fields before assignment.
      - [x] Validate branch-complete constructor field assignment.
        - [x] Validate `if`/`else` constructor field assignment joins.
        - [x] Validate `switch` constructor field assignment joins.
          - [x] Validate default-covered switch constructor field assignment joins.
          - [x] Validate exhaustiveness-proven non-default switch constructor field assignment joins.
        - [x] Validate early-exit constructor paths before merging assignment facts.
      - [x] Validate local and `out`/`init` write-before-read guarantees.
        - [x] Reject straight-line local and `out`/`init` reads before assignment.
        - [x] Validate branch-complete local and `out`/`init` assignment before reads.
          - [x] Validate `if`/`else` local and direct output-parameter assignment joins.
          - [x] Validate default-covered switch local and direct output-parameter assignment joins.
          - [x] Validate branch-complete `init` element assignment joins.
          - [x] Validate exhaustiveness-proven non-default switch local and `out`/`init` assignment joins.
            - [x] Validate unguarded exhaustive bool-switch joins without `default`.
            - [x] Validate enum-variant switch joins without `default`.
            - [x] Validate bounded-integer and range-pattern switch joins without `default`.
        - [x] Validate successful-return write obligations for `out`/`init` outputs.
    - [~] Port range facts.
      - [x] Preserve arithmetic integer range endpoint facts through MIR LLVM range attributes.
      - [x] Propagate value-derived range facts through MIR lowering and joins.
      - [x] Validate range facts against branch and comparison proofs.
    - [x] Port enum layout facts.
      - [x] Build direct-tag enum variant and scalar payload layout fact tables.
      - [x] Resolve nested non-generic local struct and record enum payload layout facts.
      - [x] Resolve generic aggregate enum payload layout substitutions.
      - [x] Preserve C pack, explicit field offsets, and aggregate align attributes in enum payload size and alignment facts.
      - [x] Preserve enum payload storage misalignment facts where lowered payload storage is under-aligned.
      - [x] Fold enum layout facts through CTFE `System.Compiler` queries.
        - [x] Validate enum tag, variant, and payload layout query calls in the binder.
        - [x] Expose typed enum layout query helpers for CTFE folding.
        - [x] Fold enum tag layout queries to compile-time constants.
        - [x] Fold enum variant payload layout queries to compile-time constants.
      - [x] Preserve enum layout facts through MIR, package images, ABI, and LLVM lowering.
        - [x] Define enum layout as a durable type-attached MIR fact category.
        - [x] Validate enum layout facts as layout facts at MIR phase boundaries.
        - [x] Attach typed enum layout rows to MIR lowering fact records.
        - [x] Serialize enum layout fact rows in package images.
        - [x] Load enum layout fact rows from package images.
        - [x] Carry enum tag and payload layout facts into ABI lowering.
        - [x] Emit LLVM IR from enum layout facts without recomputing layout.
          - [x] Emit enum storage LLVM types from MIR enum layout rows.
          - [x] Emit enum value constructors, loads, stores, and tag reads from MIR enum layout rows.
    - [ ] Port CTFE.
    - [ ] Port compile-time generic value evaluation.

- [~] Decompose the oversized self-host binding implementation into focused modules.
  - [x] Record the target `Compiler.Binding.*` module map from the stage0 C# counterpart files.
  - [x] Keep `Compiler.Binding` as an API-compatible facade that re-exports the new binding submodules.
  - [x] Move bind result, bind diagnostic, and diagnostic-list types into a diagnostics module.
  - [x] Move declaration table, duplicate declaration, and raw declaration lookup helpers into a declarations module.
  - [x] Move function scope, lexical local visibility, and raw value-reference scope helpers into a scopes module.
  - [x] Move reference table construction and unresolved reference counting into a references module.
  - [x] Move type-reference resolution and type-span unresolved-reference scans into a type-resolution module.
  - [x] Move generic use-site syntax collection into a generic-use-sites module.
  - [x] Move module resolver, import resolution, and module-origin tables into a module-resolution module.
  - [x] Move typed module symbol construction into a typed-module-symbols module.
  - [x] Move typed declaration symbol construction into a typed-declarations module.
  - [x] Move typed member and method symbol construction into a typed-members module.
  - [x] Move typed local and parameter symbol construction into a typed-locals module.
  - [x] Move typed generic parameter symbol construction into a typed-generics module.
  - [x] Move generic instantiation planning into a generic-instantiation module.
  - [x] Move callable candidate and receiver candidate construction into callable-resolution modules.
  - [x] Move function effect summary construction into a function-effects module.
  - [x] Move typed trait conformance table construction into a trait-conformance module.
  - [x] Move associated alias symbol construction into an associated-types module without creating a typed-members cycle.
  - [x] Move copyability fact construction and `where Copyable` predicate validation into a copyability module.
    - [x] Move copyability table storage and accessors into a copyability-model module.
    - [x] Move copyability type-span and declaration structural facts into a copyability-type-facts module.
  - [x] Move shared function-signature parameter helpers into a signature helpers module.
  - [x] Move thread-safety law facts and law predicate helpers into a thread-safety module.
    - [x] Move thread-safety table storage and flag helpers into a thread-safety-model module.
    - [x] Move thread-safety law-name helpers into a thread-safety-law-names module.
    - [x] Move atomic builtin recognition into a thread-safety-atomic-facts module.
    - [x] Move thread-safety type-span and declaration law facts into a thread-safety-type-facts module.
    - [x] Move law-predicate where-clause scanning into a thread-safety-predicates module.
    - [x] Move shared law-predicate table and single-identifier helpers into a thread-safety module.
    - [x] Port `Transferable` and `Shareable` binding facts into the thread-safety module.
  - [x] Move layout-control attribute validation and concrete-layout query validation into a layout-validation module.
  - [x] Move C layout aggregate facts and C ABI aggregate boundary facts into C ABI layout modules.
  - [x] Move semantic validation diagnostics into focused validation modules.
    - [x] Move exported-surface visibility and ABI enum diagnostics into an exported-surfaces module.
    - [x] Move return and break/continue diagnostics into a control-flow validation module.
    - [x] Move function-kind obligation diagnostics into a function-kind validation module.
    - [x] Move law signature and law body diagnostics into a law validation module.
    - [x] Move recursion and tail-become diagnostics into recursion validation modules.
  - [x] Move destructor shape, drop validity, and destructor effect diagnostics into destructor validation modules.
  - [x] Move constructor initialization, local initialization, move, borrow, arena, and dead-on-return checks into ownership validation modules.
    - [x] Move constructor field initialization validation into a constructor validation module.
      - [x] Move constructor assigned-field tracking into a constructor field state module.
      - [x] Move constructor field assignment requirement checks into a constructor field facts module.
      - [x] Move constructor expression field-read collection into a constructor expression reads module.
      - [x] Move constructor statement traversal helpers into a constructor statement traversal module.
      - [x] Move constructor switch exhaustiveness coverage checks into a constructor switch coverage module.
    - [x] Move local and output initialization validation into an ownership initialization module.
    - [x] Move straight-line move-after-move validation into an ownership move module.
    - [x] Move straight-line borrow-liveness validation into an ownership borrow module.
    - [x] Move packed-field safe-borrow validation into an ownership borrow module.
    - [x] Move arena escape and arena retention validation into an ownership arena module.
    - [x] Move dead-on-return contract and call validation into an ownership dead-on-return module.
  - [x] Move assembly signature and register diagnostics into an assembly binding module.
  - [x] Move `BindCompilationUnit` orchestration into a narrow binding pipeline module.
  - [x] Add dependency-direction checks so binding data modules do not import semantic, ownership, C ABI, or assembly validation modules.
  - [~] Run focused selfhost binding and typing tests after each module split.
    - [x] Run focused lower-MIR checks for the constructor validation helper splits.
    - [x] Run focused lower-MIR checks for the copyability and thread-safety fact splits.
### Typing.stark
- [~] Decompose the oversized self-host typing implementation into focused modules.
  - [x] Record the target `Compiler.Typing.*` module map from the stage0 C# counterpart files.
  - [x] Keep `Compiler.Typing` as an API-compatible facade that re-exports the new typing submodules.
  - [x] Move coarse expression classification and non-boolean condition checks into an expression-classification module.
  - [x] Move typed function signature tables into a typed-signatures module.
    - [x] Move typed signature table storage and accessors into a typed-signature-model module.
    - [x] Move typed signature row construction into a typed-signature-rows module.
    - [x] Move typed signature declaration and member scanning into a typed-signature-declarations module.
  - [x] Move Stark type-resolution, type-kind flags, type-span scanners, and alias-aware type-head classification helpers into a type-resolution module.
  - [x] Move type compatibility, conversion permissibility, and conversion-cost facts into a type-compatibility module.
  - [x] Move global declaration typing and global storage facts into a typed-globals module.
    - [x] Move global table storage and accessors into a typed-global-model module.
    - [x] Move global declaration token and storage/binding helpers into a typed-global-helpers module.
    - [x] Move global row construction into a typed-global-rows module.
  - [x] Move explicit storage selector collection into a storage-selectors module.
  - [x] Move struct, record, and record-header field typing into a typed-fields module.
    - [x] Move field table storage and accessors into a typed-field-model module.
    - [x] Move field declaration token helpers into a typed-field-helpers module.
    - [x] Move field row construction into a typed-field-rows module.
  - [x] Move enum payload typing and enum variant role facts into a typed-enum-payloads module.
    - [x] Move enum-payload table storage and accessors into a typed-enum-payload-model module.
    - [x] Move enum-payload role and attribute readers into a typed-enum-payload-attributes module.
    - [x] Move enum-payload row construction into a typed-enum-payload-rows module.
  - [x] Move enum layout construction and layout-table query helpers into a typed-enum-layouts module.
    - [x] Move enum-layout table storage, accessors, and query folding into a typed-enum-layout-model module.
    - [x] Move enum-layout variant scanning, tag mapping, and payload lookup into a typed-enum-layout-variants module.
  - [x] Move enum-layout attribute and field-offset readers into a typed-enum-layout-attributes module.
  - [x] Move enum-layout scalar sizing and alignment arithmetic into a typed-enum-layout-arithmetic module.
  - [x] Move enum-layout generic contexts and comptime-value readers into a typed-enum-layout-generics module.
  - [x] Move local declaration typing and local storage facts into a typed-locals module.
    - [x] Move local table storage and accessors into a typed-local-model module.
    - [x] Move local initializer token helpers into a typed-local-helpers module.
    - [x] Move local row construction into a typed-local-rows module.
  - [x] Move literal expression typing and literal scalar/text fact derivation into a typed-literals module.
    - [x] Move literal table storage and accessors into a typed-literal-model module.
    - [x] Move literal scalar, text, and expression-kind facts into a typed-literal-facts module.
    - [x] Move literal row construction into a typed-literal-rows module.
  - [x] Move identifier expression typing and visible-symbol lookup into a typed-identifiers module.
    - [x] Move identifier table storage and accessors into a typed-identifier-model module.
    - [x] Move identifier symbol, signature, parameter, and local lookup helpers into a typed-identifier-lookup module.
    - [x] Move identifier row appenders and target-fact propagation into a typed-identifier-rows module.
  - [x] Move direct call typing, call argument facts, and overload resolution into a typed-calls module.
    - [x] Move call context, target-kind, flag, and type-fact records into a typed-call-kinds module.
    - [x] Move call expression table storage and accessors into a typed-call-model module.
  - [x] Move callable signature extraction and callable return or parameter facts into a typed-call-signatures module.
    - [x] Move signature-slot fact projection into a typed-call-signature-slots module.
    - [x] Move callable parameter-list and span scanning into a typed-call-callable-spans module.
    - [x] Move callable return, parameter, and type-span fact extraction into a typed-call-callable-facts module.
  - [x] Move call argument type-fact derivation into a typed-call-argument-facts module.
    - [x] Move call argument node lookups into a typed-call-argument-lookup module.
    - [x] Move call argument source fact projection into a typed-call-argument-source-facts module.
    - [x] Move call argument expression walkers into a typed-call-argument-walkers module.
  - [x] Move direct and method overload scoring into a typed-call-overloads module.
  - [x] Move call argument row appenders into a typed-call-arguments module.
  - [x] Move call target resolution and call-row appending into a typed-call-targets module.
  - [x] Move member expression typing and method-candidate logic into a typed-members module.
    - [x] Move member table storage and accessors into a typed-member-model module.
    - [x] Move member context, receiver, target, and flag facts into a typed-member-kinds module.
    - [x] Move member table row storage appenders into a typed-member-model-rows module.
    - [x] Move member node and declaration lookup helpers into a typed-member-lookup module.
    - [x] Move member receiver and type-fact derivation into a typed-member-receiver-facts module.
    - [x] Move member method-candidate collection into a typed-member-methods module.
    - [x] Move member row appending into a typed-member-rows module.
  - [x] Move indexing and slicing expression typing into a typed-indexing module.
    - [x] Move indexing table storage and accessors into a typed-index-model module.
    - [x] Move indexing context, lookup, receiver, element, and result fact helpers into a typed-index-helpers module.
  - [x] Move explicit conversion typing and target/operand fact propagation into a typed-conversions module.
    - [x] Move conversion table storage and accessors into a typed-conversion-model module.
    - [x] Move conversion context, lookup, target fact-copy, and flag helpers into a typed-conversion-helpers module.
    - [x] Move conversion operand fact derivation into a typed-conversion-operand-facts module.
  - [x] Move assignment target typing, assignment value facts, and compound-operator typing into a typed-assignments module.
    - [x] Move assignment table storage and accessors into a typed-assignment-model module.
    - [x] Move assignment context, operator, target, value, and flag facts into a typed-assignment-kinds module.
    - [x] Move assignment table row storage appender into a typed-assignment-model-rows module.
    - [x] Move assignment context, operator, and node-lookup helpers into a typed-assignment-helpers module.
    - [x] Move assignment target and value fact derivation into a typed-assignment-node-facts module.
  - [x] Move return expression typing and expected-return fact derivation into a typed-returns module.
    - [x] Move return table storage and accessors into a typed-return-model module.
    - [x] Move return value-kind and expected-return fact helpers into a typed-return-facts module.
  - [x] Move generic argument syntax helpers and function generic parameter helpers into generic-typing modules.
  - [~] Move associated-type and dyn-trait facts consumed by typing into focused fact modules.
    - [x] Move dyn-trait token, layout, and call-cost facts into a typed-dynamic-facts module.
    - [ ] Move associated-type facts after associated-type consumers exist in selfhost typing.
  - [ ] Move copyability, thread-safety law, and atomic builtin facts consumed by typing into focused fact modules.
  - [~] Move CTFE expression, CTFE function, and structural-fact typing hooks into CTFE typing modules.
    - [x] Move enum-layout structural query-call folding into a typed CTFE query module.
    - [x] Share enum-layout generic comptime-value readers between CTFE query folding and enum layout construction.
    - [ ] Move CTFE expression typing hooks into CTFE typing modules.
    - [ ] Move CTFE function typing hooks into CTFE typing modules.
    - [x] Move remaining `System.Compiler` structural-fact typing hooks into CTFE typing modules.
  - [x] Move `BuildTyped*` orchestration into a narrow typing pipeline module.
  - [x] Add dependency-direction checks so typing data modules do not import semantic validation, ownership validation, MIR, or LLVM modules.
  - [~] Run focused selfhost typing and artifact-rendering tests after each module split.
    - [x] Run focused tests for the type-resolution and CTFE query module splits.
    - [x] Run focused lower-MIR checks for the enum-layout attribute and arithmetic splits.
    - [x] Run focused lower-MIR checks for the enum-layout generic helper split.
    - [x] Run focused lower-MIR checks for the enum-layout model and variant helper splits.
    - [x] Run focused lower-MIR checks for the typed-call helper splits.
    - [x] Run focused lower-MIR checks for the typed-conversion helper splits.
    - [x] Run focused lower-MIR checks for the typed-assignment helper splits.
    - [x] Run focused lower-MIR checks for the typed-index helper split.
    - [x] Run focused lower-MIR checks for the typed-identifier helper splits.
    - [x] Run focused lower-MIR checks for the typed-return helper splits.
    - [x] Run focused lower-MIR checks for the typed-member helper splits.
    - [x] Run focused lower-MIR checks for the typed-literal helper splits.
    - [x] Run focused lower-MIR checks for the typed-enum-payload helper splits.
    - [x] Run focused lower-MIR checks for the typed-field, typed-global, and typed-local helper splits.
    - [x] Run focused lower-MIR checks for the typed-call model, signature, and argument splits.
    - [x] Run focused lower-MIR checks for the typed-signature, typed-member-model, and typed-assignment-model splits.
    - [x] Run focused checks for the CTFE structural query split.
    - [ ] Run focused tests for future typing module splits.

- [x] Implement diagnostics, compiler artifacts, pipeline orchestration, and artifact rendering.
  - [x] Port diagnostic records.
  - [x] Port stable diagnostic codes.
  - [x] Port diagnostic severity handling.
  - [x] Port source spans and source-file mapping.
  - [x] Port source-caret rendering.
  - [x] Port deterministic diagnostic text output.
  - [x] Port artifact keys.
  - [x] Port artifact storage.
  - [x] Port artifact dependency validation.
  - [x] Port typed artifact access.
  - [x] Port parse and syntax artifact renderers.
  - [x] Port type and semantic artifact renderers.
  - [x] Port MIR and SSA artifact renderers.
  - [x] Port LLVM and package artifact renderers.
  - [x] Port the pass dependency graph.
  - [x] Port pass execution records.
  - [x] Port stop-after pass boundaries.
  - [x] Port pass crash diagnostics.
  - [x] Add fast typed artifact inspection APIs.
  - [x] Add stage-comparison artifact APIs.

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

- [x] Decompose the oversized self-host MIR implementation into focused modules.
  - [x] Keep `Compiler.Mir` as an API-compatible facade that re-exports the new MIR submodules.
  - [x] Move MIR op, type, instruction, block, function, and global models into a core MIR model module.
  - [x] Move MIR instruction, block, function, global, call, and phi builders into a core MIR builder module.
  - [x] Move MIR enum layout, enum ABI, and enum LLVM storage helpers into an enum-layout module.
  - [x] Move LLVM module, function, block, terminator, instruction, global, and typed-value emission into LLVM emission modules.
    - [x] Move shared LLVM type, ABI-carrier, and C ABI boundary text helpers into a shared LLVM text module.
    - [x] Move LLVM instruction and typed-value emission helpers into an instruction-emission module.
    - [x] Move LLVM terminator and labeled block emission helpers into a block-emission module.
    - [x] Move LLVM function, module, and global emission helpers into function and module emission modules.
  - [x] Move direct-switch and control-flow LLVM emission helpers into a control-flow emission module.
  - [x] Move function effect, call-contract, ABI, range-metadata, and separate-storage emission helpers into LLVM fact modules.
  - [x] Move MIR text rendering helpers into a MIR text-rendering module.
  - [x] Move MIR byte codecs and fixed-record section serializers into a package-image codec module.
  - [x] Move package-image validation, inspection, summary, and file IO helpers into package-image modules.
  - [x] Split the 15,895-line logical package-image model carrier into focused core, enum, MANF-row, generic-template, typed-interface, function, and backend-fact modules, keeping every resulting model module below 4,000 lines without changing row identity or adding fact conversion. (2026-07-12)
  - [x] Split the 7,279-line generic-template section loader into focused graph-builder, value-materialization, and typed-tree orchestration modules, keeping each below 4,000 lines and preserving direct manifest ordinals/type-reference rows. (2026-07-12)
  - [x] Split the 6,944-line typed-interface loader into focused type-reference, declaration/fact, and summary/API modules, keeping each below 4,000 lines and preserving callable ABI, alias-contract groups, layout metadata, and stable source ordinals. (2026-07-12)
  - [x] Split the 5,070-line compiler-fact loader by LLVM-facing effect/ABI, layout/native, and semantic/ownership families, keeping each below 2,300 lines without changing fact rows or text payloads. (2026-07-12)
  - [x] Split the 7,256-line package source bridge into focused core lookup, expression/type rendering, statement/pattern rendering, declaration rendering, and public orchestration modules, keeping each below 2,400 lines while structured package facts remain authoritative. (2026-07-12)
  - [x] Move value-range transfer, branch refinement, and returned-value validation helpers into a MIR facts module.
  - [x] Move direct imported-template scalar operator decoding and constant evaluation into a focused module before that lowering path grows beyond the maintenance threshold.
  - [x] Move source-expression parsing, name checks, arity checks, and semantic probes out of MIR into source-lowering modules.
    - [x] Move integer range endpoint parsing and range fact readers into a source range-facts module.
    - [x] Move source token span, parameter-name, and local-binding helpers into a source symbols module.
    - [x] Move name resolution, declaration uniqueness, function-name uniqueness, and call-arity probes into a source semantic-probes module.
    - [x] Move boolean condition and boolean operand probes into the source semantic-probes module.
    - [x] Move structural expression nodes, the expression parser, and source expression type inference into a source expressions module.
  - [x] Move structured source lowering for `if`, loops, switches, locals, calls, and function bodies into lowering-builder modules.
    - [x] Move single-expression and single-return source lowering wrappers into a source expression-lowering module.
    - [x] Move self-host local, storage, and layout scanner helpers into a source local-lowering module.
    - [x] Move shared source signature, ABI, contract, and call-validation helpers into a source function-context module.
    - [x] Move `if` and if-expression arm parsers and lowering helpers into a source if-lowering module.
    - [x] Move switch arm parsers and lowering helpers into a source switch-lowering module.
    - [x] Move while and for loop shape detection and lowering helpers into a source loop-lowering module.
    - [x] Move module/function AST lowering orchestration and package-image-with-asm table builders into a source module-lowering module.
    - [x] Move allocation-free MIR effect scans, law/finite/norecurse fixed-point proofs, and source backend-modifier fact merging into a focused source function-effects module. (2026-07-11)
    - [x] Move source `try` shape detection and typed enum/range/storage/control-flow lowering into a focused source try-lowering module, bringing both it and `SourceModuleLowering.stark` below the preferred 5k-line maintenance threshold without allocation or indirection. (4,437 and 4,402 lines respectively after focused import trimming, 2026-07-11.)
  - [x] Move assembly metadata collection into an assembly metadata module.
  - [x] Move clang verification and temporary-file helpers into a test-support or compiler harness module.
  - [x] Add dependency-direction checks so MIR core does not import parsing, LLVM emission, package images, or test helpers.
  - [x] Run focused behavior-preserving tests after each module split.
    - [x] Run focused lower-MIR checks for the source-expression and source-local lowering splits.
    - [x] Run focused lower-MIR checks and selected if facts for the source function-context and source if-lowering splits.
    - [x] Run focused lower-MIR checks for the source switch-lowering and source loop-lowering splits.
    - [x] Run focused lower-MIR and API re-export checks for the source module-lowering split.
    - [x] Run focused checks for the source function-effects module and its source module-lowering consumer after the effect-prepass split. (2026-07-11)
    - [x] Run focused checks for the source try-lowering module and its source module-lowering consumer after the try-family split. (2026-07-11)
    - [x] Run focused checks for every logical package-image model module plus the compatibility facade, logical loader, and manifest builder after the model-carrier split. (2026-07-12)
    - [x] Run focused checks for the generic-template graph builder, value materializer, typed-tree orchestrator, direct package-loader consumer, and package-image facade after the loader split. (2026-07-12)
    - [x] Run focused checks and declaration-parity comparisons for the typed-interface loader, compiler-fact loader, and package source-bridge splits, followed by the aggregate package-image facade check. (2026-07-12)
    - [x] Run full-front-end checks for `SourceModuleLowering.stark` and the `selfhost.Ir` fact root after all package-image files were brought below 5,000 lines. (2026-07-12)

- [x] Continue decomposing source-lowering modules that remain above the preferred 5k-line maintenance threshold without adding allocation, callback indirection, or fact conversion.
  - [x] Split the oversized source switch lowerer along dependency-directed pattern and lowering boundaries.
    - [x] Extract typed switch decision nodes/descriptors, validation, worklist translation, and typed row conversion into `SourceSwitchPatterns.stark` (2,426 lines). (2026-07-11)
    - [x] Extract scalar/enum condition construction and recursive list/aggregate overlap proofs into `SourceSwitchPatternConditions.stark` (1,408 lines). (2026-07-11)
    - [x] Extract fixed-array/list label parsing, typed struct field flattening, nested capture translation, and value-parameter aggregate condition lowering into `SourceSwitchAggregatePatterns.stark` (3,678 lines). (2026-07-11)
    - [x] Extract storage-backed struct aggregate and typed fixed-array/list condition lowering into `SourceSwitchStoragePatterns.stark` (877 lines), preserving direct storage offsets, typed member layout, ABI facts, and capture conditions. (2026-07-11)
    - [x] Extract decision preflight rows, case descriptors, enum variant/tag resolution, descriptor validation, and typed enum case-label parsing into `SourceSwitchCaseParsing.stark` (1,683 lines). (2026-07-11)
    - [x] Extract switch assignment-arm parsing, arena-reserve handling, local-type construction, module-call validation, and direct MIR store lowering into `SourceSwitchAssignmentArms.stark` (979 lines). (2026-07-11)
    - [x] Extract terminal return-arm and case collection, exhaustiveness/overlap checks, capture typing, scalar range/enum tag preservation, and fixed-array slice descriptor construction into `SourceSwitchTerminalParsing.stark` (4,707 lines). (2026-07-11)
    - [x] Extract terminal integer/boolean/enum/fixed-array/aggregate CFG lowering and capture materialization into `SourceSwitchTerminalLowering.stark` (2,805 lines), retaining direct MIR instruction/block/range emission. (2026-07-11)
    - [x] Extract assignment-case collection into `SourceSwitchAssignmentParsing.stark` (2,507 lines), preserving case intervals, typed patterns, capture spans, enum tags, and parsed assignment arms. (2026-07-11)
    - [x] Extract assignment value/capture handling, raw-pointer fact updates, and assignment CFG construction into `SourceSwitchAssignmentLowering.stark` (3,672 lines), retaining direct MIR and fact-table mutation. (2026-07-11)
    - [x] Extract constructed-object and terminal function-to-block orchestration into `SourceSwitchFunctionLowering.stark` (2,998 lines). (2026-07-11)
    - [x] Bring `SourceSwitchLowering.stark` below the preferred 5k-line threshold (4,304 lines) while keeping every extracted module below 5k and preserving direct-call dependency direction. (2026-07-11)
  - [x] Split the oversized source if lowerer into dependency-directed modules without adding allocation, callbacks, or fact conversion. (2026-07-12)
    - [x] Extract enum-return carrier helpers into `SourceIfCore.stark` (102 lines) and source-shape detection into `SourceIfShapes.stark` (229 lines).
    - [x] Extract typed arm/assignment parsing and raw-pointer fact resolution into `SourceIfParsing.stark` (2,419 lines).
    - [x] Extract storage-mutation parsing, validation, and CFG lowering into `SourceIfStorageMutation.stark` (2,075 lines).
    - [x] Extract assignment value/capture typing, validation, phi construction, and recursive CFG lowering into `SourceIfAssignmentLowering.stark` (2,068 lines).
    - [x] Extract if-expression, local-assignment, and nested function-to-block orchestration into `SourceIfFunctionLowering.stark` (3,337 lines).
    - [x] Extract local-if and if-return function lowering into `SourceIfReturnLowering.stark` (1,981 lines).
    - [x] Bring `SourceIfLowering.stark` below the preferred 5k-line threshold (1,396 lines), with every extracted module below 5k and direct downstream imports for non-transitive consumers.
  - [x] Split the remaining oversized `SourceLocalLowering.stark` module (10,278 lines) along dependency-directed local parsing, storage, and lowering boundaries. (2026-07-12)
    - [x] Keep lexical/type scanning, scalar layout, alignment, and shared low-level helpers in the 1,777-line `SourceLocalLowering.stark` core.
    - [x] Extract aggregate/enum storage layout, local models, raw-pointer facts, bounds proofs, and declaration handling into `SourceLocalStorageLayout.stark` (3,419 lines).
    - [x] Extract propagation role, compatibility, error-funnel, payload-range, and enum-payload facts into `SourceTryPropagation.stark` (987 lines).
    - [x] Extract enum/aggregate/raw-pointer expression parsing into `SourceLocalExpressionParsing.stark` (1,075 lines).
    - [x] Extract object, scalar, enum, fixed-array, and initialized raw-pointer local setup/lowering into `SourceLocalInitialization.stark` (959 lines).
    - [x] Extract scalar, enum, slice, raw-pointer, and constructed-object storage mutation parsing/lowering into `SourceLocalMutation.stark` (1,809 lines).
    - [x] Extract arena selector, target-typed constructor, dynamic-local, and reserve lowering into `SourceLocalArena.stark` (398 lines).
    - [x] Add direct imports for every non-transitive consumer and verify that every top-level MIR source-lowering module is below 5,000 lines.

- [ ] Implement HIR/MIR lowering.
  - [x] Define the self-host HIR model or explicit direct-to-MIR boundary.
  - [x] Port the MIR lowering pass shell.
  - [x] Port function builder state.
  - [x] Gate function-builder entry on complete MIR backend fact coverage.
  - [x] Validate value recording before extending builder-owned instruction ranges.
  - [x] Validate block recording before extending builder-owned control-flow ranges.
  - [x] Validate function-builder finalization before recording owned function ranges.
  - [x] Validate explicit function-builder entry selection before changing entry blocks.
  - [x] Port lowering symbol maps.
  - [x] Port MIR block creation.
  - [x] Validate return and branch block append helpers before recording builder-owned control-flow blocks.
  - [x] Lower literals.
    - [x] Lower integer and boolean literals to typed MIR constants with exact range facts.
    - [x] Validate literal fact rows before emitting MIR constants.
    - [x] Reject unsupported literal families before emitting partial MIR.
    - [x] Lower character and text literals through the MIR constant storage model.
      - [x] Add MIR constant-storage rows for decoded ASCII and Unicode text literal payloads.
      - [x] Add MIR value operations for building ASCII and Unicode text view descriptors from constant-storage rows.
      - [x] Preserve text literal payload, byte length, code point length, and storage encoding facts through MIR value facts.
      - [x] Emit LLVM private constant data and zero-copy text descriptors for MIR text literals.
        - [x] Add deterministic LLVM symbol and private byte-array declaration emission for MIR text constant rows.
        - [x] Add focused LLVM text constant data tests for byte escaping, UTF-8 payloads, and invalid span rejection.
        - [x] Thread MIR text payload byte tables into LLVM module emission.
        - [x] Emit each referenced MIR text constant row once before LLVM function bodies.
        - [x] Replace `TextConstantValue` LLVM no-op lowering with zero-copy text view construction.
        - [x] Preserve text byte length and code point length operands through descriptor lowering.
      - [x] Parse character and string literal expression nodes with stable links to typed literal rows.
      - [x] Lower character and text literal expression nodes through the MIR text descriptor operations.
      - [x] Preserve MIR text literal constants through package codec and deterministic text rendering.
      - [x] Add focused IR tests for ASCII strings, Unicode strings, character literals, and package round trips.
        - [x] Add focused IR tests for ASCII string, Unicode string, and character literal MIR text constants.
        - [x] Add focused IR tests for MIR text literal package round trips.
    - [x] Lower floating-point literals once MIR has typed float constants.
      - [x] Add f32/f64 MIR value-type plumbing across type codes, package codec, text rendering, LLVM type names, and struct ABI shape facts.
      - [x] Add MIR float constant storage rows that preserve f32/f64 literal text or canonical bits without widening instruction rows.
      - [x] Lower parsed f32/f64 literal expression nodes to MIR float constant values.
      - [x] Emit LLVM f32/f64 float constants from MIR float constant rows with canonical hex literal spelling.
      - [x] Add focused IR tests for f32 and f64 literal return, call argument, and package round trips.
    - [x] Lower null pointer literals to typed pointer constants with nullability facts.
  - [~] Lower locals and parameters.
    - [x] Lower dense i64 parameters with translated backend facts.
    - [x] Bind SSA local aliases without emitting extra MIR.
    - [x] Validate carried value facts before binding SSA local aliases.
    - [x] Lower typed non-i64 parameters once MIR models parameter result types.
    - [x] Preserve compact integer widths for typed parameter references and SSA local aliases.
    - [~] Lower mutable and storage-backed locals.
      - [x] Lower initialized straight-line stack scalar locals with typed storage-backed loads before terminal returns.
      - [x] Lower uninitialized storage-backed scalar locals after definite-assignment checks exist.
      - [x] Lower storage-backed scalar locals through terminal `if` branches.
      - [x] Lower storage-backed scalar locals through switch arms.
    - [ ] Lower local lifetime, move, and drop facts.
    - [~] Lower assignments.
      - [x] Lower simple SSA local reassignments by rebinding the local symbol to the assigned value.
        - [x] Validate carried value facts before rebinding SSA local assignments.
      - [x] Lower mutable storage-backed place assignments.
        - [x] Lower straight-line scalar stack local assignments before terminal returns.
        - [x] Lower straight-line constructed object field assignments before terminal returns.
        - [x] Lower straight-line constructed object field assignments before terminal `if` branches.
        - [x] Lower scalar stack local assignments before terminal `if` branches.
        - [x] Lower scalar stack local assignments inside switch arms.
        - [x] Lower storage-backed switch-arm assignments.
        - [x] Lower scalar and enum member-chain assignments on storage-backed constructed object fields.
      - [~] Lower indexed and sliced storage-backed assignments.
        - [x] Lower constant fixed-array element assignments on storage-backed constructed object fields.
        - [x] Lower dynamic fixed-array element assignments on storage-backed constructed object fields.
        - [~] Lower slice element assignments on storage-backed places.
          - [x] Lower straight-line descriptor-backed slice element assignments.
          - [x] Lower descriptor-backed slice element assignments before terminal `if` branches.
          - [x] Lower descriptor-backed slice element assignments in switch arms.
          - [x] Lower ABI slice parameter element assignments after slice ABI carriers exist.
      - [x] Lower compound assignments with checked, wrapping, and saturating arithmetic semantics.
        - [x] Validate carried operand facts before emitting compound assignment operations.
      - [x] Lower assignment expressions in enclosing value contexts.
  - [x] Lower arithmetic and comparisons.
    - [x] Lower typed integer add/sub/mul and signed comparisons with recomputed value facts.
      - [x] Validate carried operand facts before interpreting binary operation facts.
      - [x] Preserve compact integer widths through inferred binary-expression and comparison operand lowering.
    - [x] Lower integer division, remainder, bitwise, and shift operators with checked backend facts.
    - [x] Lower wrapping and saturating arithmetic operators with explicit overflow semantics.
    - [x] Lower floating-point arithmetic and comparisons after MIR has typed float operations.
  - [~] Lower calls.
    - [x] Lower typed direct calls up to MIR's four-argument payload with result facts.
      - [x] Validate carried argument facts before emitting MIR direct-call payloads.
    - [x] Lower direct calls with more than four arguments once MIR has side-table argument storage.
    - [ ] Lower function-pointer, closure, method, and dynamic trait calls.
    - [x] Preserve callable ABI, effect, ownership, and alias facts through call lowering.
      - [x] Preserve direct-call result value facts through HIR-to-MIR lowering and MIR call-return fact import.
      - [x] Preserve callee callable ABI and effect facts at call sites.
        - [x] Emit LLVM direct-call attributes from computed callee effect facts.
        - [x] Preserve `memory(none)` across law calls to memory-none callees.
        - [x] Precompute effect summaries before emission so forward direct calls keep attributes.
        - [x] Preserve explicitly declared source hot/cold, strict-FP, inline/noinline/inlinehint, and `[Backend(Opaque)]` facts through the module effect prepass into LLVM definitions and direct-call attributes; opaque mode forces `optnone noinline`, and the prelude scan is allocation-free. (2026-07-11)
        - [~] Import inferred `nounwind`, default inline-hint, and internal fast-call facts from the typed function-effect/ABI summaries when the open optimized-SSA ABI lowering owns those defaults, preserving tail/FFI/export precedence without changing the compatibility emitter independently.
          - [x] Add an opt-in typed-summary bridge for the source-to-MIR path that imports the inferred effect and calling-convention rows into the existing LLVM definition and direct-call carriers, with exact function-token identity checks and tail/FFI/export precedence. Its core accepts already-built module facts and typed summaries so the future optimized pipeline does not rebuild them; the compatibility entry point remains byte-stable. (2026-07-11)
          - [ ] Route the optimized SSA/ABI pipeline through the typed-summary bridge when that pipeline replaces the compatibility entry point, then retire the opt-in boundary after snapshot migration.
      - [x] Preserve ownership and alias obligations for memory-backed call arguments.
  - [~] Lower returns.
    - [x] Lower value returns to MIR return blocks without dropping value facts.
      - [x] Validate carried value facts before emitting MIR return blocks.
      - [x] Preserve declared scalar return widths for straight-line literal, arithmetic, and bool returns.
      - [x] Preserve declared scalar return widths for `return if` and terminal `if` return arms.
      - [x] Preserve declared scalar return widths for terminal integer, enum, and boolean `switch` return arms.
      - [x] Preserve declared scalar return widths for loop induction and accumulator return values.
    - [x] Lower bare void returns once MIR has a void-return terminator.
    - [ ] Lower return-time cleanup edges after ownership cleanup lowering exists.
  - [~] Lower places and member access.
    - [x] Lower scalar field reads from storage-backed constructed objects.
    - [x] Lower constant fixed-array element reads from storage-backed constructed object fields.
    - [~] Lower general typed member chains from HIR.
      - [x] Lower scalar and enum leaf chains on storage-backed constructed object locals.
      - [x] Lower fixed-array leaves reached through nested member chains.
      - [x] Fix nested member chain runtime lowering end-to-end (root cause: the per-field layout resolver `TryGetKnownSourceStorageLayoutWithEnums` never recursed into named aggregate fields, so any struct containing a struct had extent 0 and its constructed-object local was rejected before member access; fixed 2026-07-02 with depth-capped recursive extent/alignment — the cap rejects cyclic aggregates instead of hanging. Two- and three-level chains now lower with correct accumulated byte offsets; probe + IrTests facts cover store/load, offset accumulation, scalar-after-aggregate, and cyclic rejection).
      - [x] Fix constructed-object field try-assignment lowering through the single-function entry (fixed 2026-07-06: the try-assignment parser now carries the target field or fixed-array element declared range into the assignment descriptor, and lowering proves the `[Ok]` payload range is a subset before storing; matching ranged payloads lower, wider payloads reject, and returned field loads keep their LLVM `!range` metadata).
      - [x] Fix if-condition member reads in the single-function dialect paths (fixed 2026-07-02; three stacked causes: `CompileFunctionWithLocalsToLlvm` never dispatched to the terminal-if lowerings at all — wired in `LowerModuleLocalIfReturnFunctionToBlocks` with the real module facts, which alone fixed bare-bool param conditions; the typing-side statement walker only extracted parenthesized if/while conditions, so paren-free dialect conditions (and real-Stark `while willexit (…)` conditions) had no typed member rows — the extraction now skips the `willexit` marker and the optional paren; and the driver's shared tail emitted a linear instruction range that truncated multi-block bodies mid-CFG — block-shaped bodies now record `BlockEmit` ranges and emit through `EmitLlvmBlocksWithRangeFactsCoreWithEnumLayouts`. Heap and stack bool member conditions branch correctly on the loaded flag; all probe batteries green).
      - [x] Fix `var` locals initialized from member field reads (fixed 2026-07-02; two stacked root causes: the statement-kind classifier in Parsing.stark mapped every storage class to StatementKind.Local except `var`, so var initializers never entered the expression table and typing produced no member rows for them — one classifier case fixes the facts; and the single-function driver batched locals before mutation replay, so accepting interleaved statements required making `storageMutationStatements` the ordered statement timeline with LocalDecl rows — a local's override and initializers now lower at their source position, which also fixes stored-scalar initializers that read fields after mutations. Probes: var-from-field, var-from-ranged-field, and both var-indexed bounds-proof shapes flipped to passing; emitted LLVM verified load-after-store with !range-carried bounds).
      - [~] Lower typed HIR member path rows through shared storage-place addressing.
        - [x] Route constructed-object field reads and address-taking through a shared storage-place address helper.
        - [x] Route constructed-object field assignments through the shared storage-place address helper.
        - [x] Route direct and indexed constructed-object field parsing through the member-chain resolver.
        - [x] Import typed HIR member path rows into the shared place-address resolver.
        - [x] Extend declared-range facts to indexed fixed-array element reads (2026-07-02: fixed-array member-path facts decode the ELEMENT's declared range from the element type head; const- and dynamic-index element reads attach it to their nodes, and the indexed load lowers through the declared-range typed LoadPtr — element loads carry `!range` and their values prove second-array index bounds; unranged elements still reject as unproven indexes).
        - [x] Prove compound-assignment and try-assignment stored ranges against narrow declared field ranges (2026-07-06 update: `+=`/`-=` on simple and nested field targets still prove through range-carrying field reads, and constructed-object field `try` assignments now decode the `[Ok]` payload range and require it to fit the target field or fixed-array element range before storing; wider payload ranges reject without weakening field-load `!range` facts).
        - [x] Record declared field-load result range facts through MIR value facts and LLVM load range metadata (verified complete 2026-07-02: declared ranges ride the typed LoadPtr's ConstInt range operands into MIR value facts — consumed by fixed-array bounds proofs and return-range validation — and narrow ranges emit `!range` load metadata while full-width spans are skipped as unrepresentable; probes `ranged-return-through-declared-load-facts` and both var-index bounds shapes, plus the `!range`-asserting IrTests facts, cover the chain).
        - [x] Share module-level typed member tables between the effect prepass and the main lowering pass (the `SourceModuleLoweringFacts` bundle is built once in `CompileModuleFromAstStream` and threaded through the effect prepass; see the 2026-07-01 pain-point-fixes entry in TestPassLedger.md).
    - [~] Lower address-taking place reads for locals, fields, and parameters.
      - [x] Lower address-taking for storage-backed scalar and aggregate locals.
      - [x] Lower address-taking for storage-backed field and indexed element places.
      - [x] Lower address-taking for descriptor-backed slice elements.
      - [x] Lower address-taking for fixed-array parameter elements.
      - [x] Lower address-taking for by-value scalar parameters that need temporary storage slots.
  - [x] Lower indexing and slicing.
    - [x] Lower constant fixed-array element reads from storage-backed constructed object fields.
    - [x] Lower dynamic fixed-array indexing through a MIR address operation that preserves element size, bounds, and alignment facts.
      - [x] Add a MIR indexed pointer offset operation that preserves pointer provenance, element size, index value facts, and derived alignment.
      - [x] Validate source dynamic fixed-array index bounds before emitting the indexed pointer operation.
      - [x] Lower dynamic fixed-array element reads from storage-backed constructed object fields.
    - [x] Lower slice indexing through a MIR address operation that preserves base, length, element size, and alias facts.
      - [x] Add a source slice descriptor for storage-backed fixed-array views.
      - [x] Build checked slice element addresses from descriptor facts for range-proven indexes.
      - [x] Lower descriptor-backed slice element loads through typed pointer loads.
      - [x] Lower ABI slice parameter element loads through descriptor facts.
        - [x] Add MIR ABI carrier metadata for concrete slice `{ ptr, i64 }` parameters.
        - [x] Emit concrete slice parameters as aggregate ABI carriers.
        - [x] Emit concrete slice call arguments as aggregate ABI carriers.
          - [x] Emit slice call carriers for straight-line module lowering.
          - [x] Emit slice call carriers for the descriptor-aware terminal-if lowerer.
          - [x] Thread slice call carrier context through switch lowerer helper paths.
        - [x] Extract slice parameter base and length values into source descriptors.
        - [x] Lower dynamic slice parameter element loads through extracted descriptor facts.
    - [x] Lower slice creation from fixed arrays and storage-backed places.
      - [x] Create slice descriptors from fixed arrays.
        - [x] Create descriptors from storage-backed fixed-array fields.
        - [x] Create descriptors from standalone fixed-array locals.
      - [x] Create slice descriptors from storage-backed places.
        - [x] Create descriptors from storage-backed constructed object fields.
        - [x] Create descriptors from scalar fixed-array storage locals.
        - [x] Create descriptors from fixed-array parameters.
          - [x] Add MIR ABI carrier metadata for fixed-array by-value parameters.
          - [x] Emit fixed-array parameters and calls as `[N x T]` ABI values.
          - [x] Materialize addressable fixed-array parameter slots for indexed and addressed uses.
          - [x] Create source slice descriptors from fixed-array parameter slots.
  - [x] Lower globals.
    - [x] Lower i64 global references and stores through MIR load/store while preserving declared facts.
    - [x] Lower typed global definitions and typed global load/store once MIR global storage records value types.
    - [x] Lower module-scope global declarations and initializers from HIR.
    - [x] Validate stored value fact rows before emitting global stores.
  - [~] Lower raw pointers.
  - [~] Lower address-taking.
    - [x] Parse unary address-of expressions in selfhost expression lowering.
    - [x] Lower address-of place expressions to MIR pointer values without losing layout facts.
    - [~] Lower raw-pointer dereference expressions after pointee type facts are modeled.
      - [x] Model scalar raw-pointer parameter pointee type, size, and alignment facts for source lowering.
      - [x] Parse scalar raw-pointer parameter dereference expressions.
      - [x] Lower scalar raw-pointer parameter dereference loads through typed aligned MIR loads.
      - [x] Propagate scalar raw-pointer parameter facts through raw-pointer `var` aliases.
      - [x] Lower raw-pointer dereference stores.
        - [x] Track scalar raw-pointer parameter mutability facts for source and ABI lowering.
        - [x] Lower scalar `rawmutptr` parameter dereference stores through typed aligned MIR stores.
        - [x] Reject scalar `rawptr` parameter dereference stores.
        - [x] Route scalar raw-pointer parameter dereference stores through terminal-if and switch mutation paths.
        - [x] Lower raw-pointer local dereference stores after local pointer element facts are tracked.
          - [x] Lower scalar `rawmutptr` alias dereference stores through typed aligned MIR stores.
          - [x] Reject scalar `rawptr` alias dereference stores.
          - [x] Model declared raw-pointer stack locals as stored pointer slots with copied element facts.
          - [x] Lower declared `rawmutptr` local dereference stores through typed aligned MIR stores.
          - [x] Reject declared `rawptr` local dereference stores and readonly-to-mutable pointer assignment.
      - [x] Lower raw-pointer local dereference loads after local pointer element facts are tracked.
        - [x] Lower scalar raw-pointer alias dereference loads through typed aligned MIR loads.
        - [x] Lower declared raw-pointer local dereference loads after typed raw-pointer locals are modeled.
      - [x] Lower aggregate and nested-pointer dereferences after pointee layout facts are tracked.
        - [x] Model pointer-valued raw-pointer pointee facts for one nested pointee level.
        - [x] Lower direct nested raw-pointer dereference loads through pointer-sized MIR loads.
        - [x] Lower direct nested raw-pointer dereference stores when the nested pointer is mutable.
        - [x] Preserve nested raw-pointer pointee facts through `var` aliases and declared raw-pointer locals.
        - [x] Reject stores through readonly nested raw-pointer pointees.
        - [x] Model aggregate raw-pointer pointee layout facts.
        - [x] Lower aggregate raw-pointer field dereferences through typed field offsets.
        - [x] Preserve aggregate pointee facts through raw-pointer aliases and locals.
      - [x] Lower indexed raw-pointer dereference patterns through bounded pointer-region facts.
        - [x] Parse bounded scalar raw-pointer parameter indexed element expressions.
        - [x] Prove fixed-count bounded parameter indexes from source integer ranges.
        - [x] Prove count-parameter bounded indexes from count lower bounds.
        - [x] Lower bounded scalar raw-pointer parameter indexed loads through inbounds indexed MIR pointer offsets.
        - [x] Lower bounded scalar `rawmutptr` parameter indexed stores through inbounds indexed MIR pointer offsets.
        - [x] Reject unbounded and insufficiently proven indexed raw-pointer dereferences.
        - [x] Track bounded raw-pointer region facts for declared pointer locals and aliases.
          - [x] Propagate bounded region facts through raw-pointer `var` aliases.
          - [x] Propagate bounded region facts into immutable declared raw-pointer locals initialized from bounded pointer names.
          - [x] Propagate bounded region facts into initialized mutable raw-pointer locals.
          - [x] Update mutable raw-pointer local bounds on straight-line pointer assignments.
          - [x] Clear mutable raw-pointer local bounds after unbounded pointer assignments.
          - [x] Preserve mutable raw-pointer assignment facts through terminal-if and switch mutation uses.
          - [x] Merge bounded raw-pointer region facts across branch and switch assignments.
        - [x] Attach raw-pointer indexed region alias facts to calls, loops, and scoped noalias metadata.
          - [x] Attach scoped noalias metadata to parameter-rooted MIR pointer loads and stores.
          - [x] Attach scoped noalias metadata to direct and tail calls with memory-backed pointer arguments.
          - [x] Attach independent-loop access metadata for raw-pointer indexed loop bodies.
  - [~] Lower if expressions and statements.
    - [x] Validate conditional branch conditions before emitting MIR conditional blocks.
    - [x] Lower HIR if branch terminators from block symbols to validated MIR conditional blocks.
    - [x] Lower local-prefixed terminal source `if` returns into MIR conditional blocks.
    - [x] Lower braced terminal source `if` return branches into MIR conditional blocks.
    - [x] Lower braced terminal source `if` return branches with boolean values into typed MIR conditional return blocks.
    - [x] Lower semicolon-terminated compact terminal source `if` returns into MIR conditional blocks.
    - [x] Lower semicolon-terminated compact terminal source `if` return branches with boolean values into typed MIR conditional return blocks.
    - [x] Lower terminal source `return if ... else ...` expressions into MIR conditional return blocks.
    - [x] Lower boolean-valued terminal source `return if ... else ...` expressions into typed MIR conditional return blocks.
    - [x] Lower immediately returned local source if-expression initializers into MIR conditional return blocks.
    - [x] Lower boolean-valued immediately returned local source if-expression initializers into typed MIR conditional return blocks.
    - [x] Lower immediately returned locals overwritten by source if statements into MIR conditional return blocks.
    - [x] Lower braced source if assignment arms for immediately returned locals into MIR conditional return blocks.
    - [x] Lower compact boolean-valued immediately returned locals overwritten by source if statements into typed MIR conditional return blocks.
    - [x] Lower braced boolean-valued immediately returned locals overwritten by source if statements into typed MIR conditional return blocks.
    - [x] Lower local source if-expression initializers used by later return expressions into MIR phi merge blocks.
    - [x] Lower integer local source if-statement assignments used by later return expressions into MIR phi merge blocks.
    - [x] Lower boolean local source if-expression initializers used by later equality returns into typed MIR phi merge blocks.
    - [x] Lower compact boolean local source if-statement assignments used by later equality returns into typed MIR phi merge blocks.
    - [x] Lower braced boolean local source if-statement assignments used by later equality returns into typed MIR phi merge blocks.
    - [x] Preserve boolean local source if-expression phi return range facts through LLVM range attributes.
    - [x] Preserve compact boolean local source if-statement phi return range facts through LLVM range attributes.
    - [x] Preserve braced boolean local source if-statement phi return range facts through LLVM range attributes.
    - [~] Lower source if statements and if expressions into MIR conditional blocks.
      - [x] Lower braced source if-statement arms assigning multiple scalar locals in declaration order.
      - [x] Lower braced source if-statement arms assigning multiple scalar locals in arbitrary source order.
      - [x] Preserve boolean phi types and return range facts for multiple scalar if-statement assignments.
      - [x] Lower arm-local declarations inside non-terminal source if statement bodies.
        - [x] Lower branch-local declarations inside multi-local source if-statement assignment arms.
        - [x] Lower branch-local declarations inside single-local source if-statement assignment arms.
        - [x] Lower branch-local declarations inside immediately returned source if-statement arms.
      - [~] Lower nested non-terminal source if statement bodies through shared statement lowering.
        - [x] Lower one nested braced scalar source if-statement arm before a later return expression.
        - [x] Preserve boolean phi types and return range facts for one nested scalar source if-statement arm.
        - [x] Lower nested source if statement arms with branch-local declarations.
        - [x] Lower nested scalar source if-statement arms on both sides of the same join.
        - [x] Lower nested source if statement arms with storage-backed mutation statements.
        - [x] Lower nested source if statement arms with multiple assigned locals.
          - [x] Lower nested source if statement arms with multiple scalar assigned locals.
          - [x] Preserve pointer-valued nested multi-local assignment facts through typed phis.
        - [x] Lower recursive nested source if statement arms on both sides of the same join.
          - [x] Lower recursive nested single-local scalar source if statement arms through typed phi trees.
          - [x] Lower recursive nested multi-local scalar source if statement arms through typed phi trees.
          - [x] Lower recursive nested storage-backed mutation source if statement arms through branch-local stores.
  - [~] Lower loops.
    - [x] Lower canonical source while counting loops into MIR entry, header, body, and exit blocks.
    - [x] Lower canonical source accumulator while loops into MIR dual-phi loop blocks.
  - [~] Lower `for` and `foreach`.
    - [x] Lower canonical counted source `for willexit` loops over existing locals into MIR entry, header, body, and exit blocks.
    - [x] Lower canonical counted source `for willexit` loops with header locals into MIR entry, header, body, and exit blocks.
    - [x] Preserve canonical counted source `for willexit independent` facts through MIR block flags and LLVM loop metadata.
  - [~] Lower switch.
    - [x] Lower terminal integer switches with two literal cases and a default return into MIR conditional blocks.
    - [x] Lower boolean-valued terminal integer switch arms through typed MIR returns and LLVM range facts.
    - [x] Lower signed literal labels in terminal integer switches.
    - [x] Lower braced return arms in terminal integer switches.
    - [x] Lower scalar local-prefixed terminal integer switches through SSA local overrides.
    - [x] Lower scalar local-prefixed boolean terminal switch arms through explicit `zext` returns.
    - [x] Lower terminal integer switches with one or more literal cases through a shared comparison-chain path.
    - [x] Lower scalar local-prefixed multi-case terminal integer switches through SSA local overrides.
    - [x] Reject duplicate literal labels across all terminal integer switch cases.
    - [x] Lower non-terminal source switch statements into MIR control-flow with merge blocks.
      - [x] Lower integer switch assignment statements that continue to a return expression through nested MIR merge blocks.
      - [x] Lower switch arms with multiple statements before merging.
      - [x] Lower switch statements that continue to non-return successor statements.
        - [x] Lower one post-switch scalar local initializer before the final return.
        - [x] Lower multiple post-switch successor statements before the final return.
        - [x] Lower post-switch successor statements that branch again before returning.
    - [~] Lower switch arms that assign locals and continue after the switch.
      - [x] Lower compact and braced integer switch arms assigning one scalar local before a post-switch return.
      - [x] Lower boolean switch assignment arms into typed `i1` phi merges and zext returns.
      - [~] Lower switch assignments to multiple locals or storage-backed places.
        - [x] Lower declaration-order braced switch arms assigning multiple scalar locals.
        - [x] Lower arbitrary-order multiple scalar switch assignments without reordering side effects.
        - [x] Lower switch assignments to storage-backed places.
    - [~] Lower non-integer and pattern switch cases into MIR branch tests.
      - [x] Lower boolean literal terminal switch cases through typed MIR branch tests.
      - [x] Lower boolean literal non-terminal switch assignments through typed MIR branch tests.
      - [x] Lower enum unit switch cases through MIR branch tests.
        - [x] Add compact MIR scalar widths needed for enum tag branch tests.
        - [x] Carry enum owner identity from source signatures into expression typing.
        - [x] Resolve unit enum case labels to layout-backed variant tags.
        - [x] Lower terminal enum unit switches to compact tag comparisons.
        - [x] Lower non-terminal enum unit switch assignments to compact tag comparisons.
      - [x] Lower integer range-pattern switch cases through MIR branch tests.
        - [~] Lower aggregate and list pattern switch cases through MIR branch tests.
        - [x] Import typed switch pattern rows into the self-host MIR source-lowering facts.
          - [x] Port the self-host typed aggregate-pattern row model from the stage0 aggregate pattern typing records.
          - [x] Build typed aggregate-pattern rows during self-host typing for top-level switch labels.
          - [x] Build typed nested aggregate-pattern member rows during self-host typing.
          - [x] Build typed nested list-pattern element rows during self-host typing.
          - [x] Preserve pattern row owner type, enum variant, field ordinal, element ordinal, literal, range, capture, and nested-shape facts.
          - [x] Store typed switch pattern row tables in `SourceModuleLoweringFacts`.
          - [x] Build dense start-token lookup tables for imported typed aggregate and list pattern rows.
          - [x] Validate that source switch labels match their typed pattern rows before MIR lowering.
            - [x] Validate module-fact-backed struct, enum, and fixed-array list switch labels against imported typed rows before MIR lowering.
            - [x] Thread source-module facts into the local switch-assignment lowering entrypoint so its fixed-array list labels can use typed row validation.
          - [x] Replace ad hoc aggregate/list pattern token parsing in source MIR lowering with typed pattern row lookup.
            - [x] Replace fixed-array list label structure parsing with typed row lookup for typed-row-backed labels.
            - [x] Replace struct aggregate field-member parsing with typed row lookup for typed-row-backed labels.
            - [x] Replace enum aggregate payload-member parsing with typed row lookup for typed-row-backed labels.
            - [x] Route nested typed aggregate and list rows into shared decision descriptors without reparsing nested source tokens. (2026-07-07: terminal and assignment struct aggregate preflight now threads typed nested member spans into recursive aggregate/list decision descriptors from `SourceModuleLoweringFacts`; nested codegen remains conservatively rejected until the shared block-construction tasks lower those descriptors.)
            - [x] Replace the recursive typed nested descriptor importer with a checker-friendly iterative worklist to remove the localized `STK4122` stack-growth warnings without changing descriptor order. (2026-07-08: `SourceSwitchLowering` now drives typed nested aggregate/list descriptor import through an explicit `List`-backed DFS frame stack, preserves postorder descriptor insertion, and checks cleanly without the previous localized recursion warnings.)
          - [x] Add focused typing and MIR tests for imported top-level, nested aggregate, and nested list pattern rows. (2026-07-08: added a dedicated `selfhost.SwitchPatternImport` test project with two narrow facts covering `SourceModuleLoweringFacts` typed-row import and descriptor construction from imported nested aggregate/list rows; the isolated root checks successfully, while the generated runner was capped after a silent build.)
        - [x] Represent lowerable switch labels for discard and whole-value capture patterns.
          - [x] Represent top-level discard labels for fixed-array list and struct aggregate switch lowering.
          - [x] Represent whole-value capture labels for fixed-array list and struct aggregate switch lowering.
            - [x] Add aggregate-valued source expression typing for fixed-array and struct local type codes.
            - [x] Add aggregate-valued source expression lowering for direct fixed-array and struct parameter reads.
            - [x] Carry whole-value capture local type rows without routing through scalar capture type conversion.
            - [x] Lower fixed-array parameter whole-value capture overrides as direct aggregate parameter values (2026-07-07: terminal fixed-array parameter `case var capture` rows now lower to direct `MirParamTyped` aggregate overrides, carry capture-name fixed-array ABI descriptor and argument facts as aliases of the original parameter, and validate/lower calls in fixed-array switch guards and scalar-return arms through the call-context path; storage-backed whole captures still reject pending explicit copy/borrow semantics).
            - [x] Lower by-value struct parameter whole-value capture overrides as direct aggregate parameter values (2026-07-07: terminal by-value struct parameter `case var capture` rows now recognize the field-count sentinel, lower to direct `MirParamTyped` aggregate overrides, forward those overrides through struct call-argument lowering, and keep storage-backed whole-struct captures rejected pending explicit copy/borrow semantics).
            - [x] Define storage-backed whole-value capture copy or borrow semantics before lowering local and field-backed scrutinees.
              - [x] Lower terminal fixed-array address-backed whole-value captures as by-value aggregate copies for fixed-array callees (2026-07-07: local or field-backed `case var captured` over a fixed-array scrutinee now records a fixed-array value-override descriptor, lowers the capture through one `MirLoadFixedArray` with field byte offsets preserved, and rejects slice conversion until an addressable capture-copy slot exists).
              - [x] Define addressable copy storage for storage-backed fixed-array whole captures used as slices or indexed values.
                - [x] Materialize address-backed fixed-array whole captures into a capture-owned stack copy for slice call arguments (2026-07-07: address-backed fixed-array whole captures now copy each element with typed loads and stores, point slice descriptors at the capture copy slot, and reuse the slot for slice calls and by-value fixed-array callees).
                - [x] Thread whole-capture slice descriptors into captured indexed-expression parsing so `captured[index]` can lower through the copy slot (2026-07-07: fixed-array list switch parsers now build capture-aware local type, range fact, and slice descriptor tables before parsing captured guards and return arms, so storage-backed whole captures resolve indexed reads through the capture-owned copy slot).
              - [x] Lower storage-backed struct whole-value captures as explicit by-value copies with preserved struct ABI facts (2026-07-07: local and field-backed `case var captured` over struct scrutinees now build struct-value ABI facts from the source type token, preserve field alignment for field-backed storage, emit a by-value `StructValueLoad`, and feed struct-value callees through exact type-token checked capture overrides).
            - [x] Add focused IR facts for by-value fixed-array and struct whole-value capture labels (2026-07-07: selfhost IR facts now cover fixed-array and struct `case var captured` labels feeding by-value aggregate callees directly from `%p0`, with negative checks against byte-buffer materialization and scalarized calls).
        - [x] Represent lowerable aggregate-pattern descriptors with owner type, optional enum variant, and field pattern rows.
          - [x] Add compact aggregate-pattern descriptor rows with owner type, optional enum variant, and field row spans.
          - [x] Store aggregate-pattern descriptor row ids for terminal and assignment struct aggregate labels.
          - [x] Store aggregate-pattern descriptor row ids for enum aggregate labels.
        - [x] Represent lowerable list-pattern descriptors with element type, fixed length, and element pattern rows.
          - [x] Add compact list-pattern descriptor rows with element type, fixed length, and element row spans.
          - [x] Store list-pattern descriptor row ids for terminal and assignment fixed-array labels.
        - [x] Validate aggregate and list switch labels before emitting any MIR branch blocks.
        - [x] Lower no-capture enum aggregate patterns to tag branch tests with preserved enum layout facts.
          - [x] Lower tuple enum aggregate labels with all-discard payloads to tag branch tests.
          - [x] Lower named enum aggregate labels with all-discard payloads to tag branch tests.
          - [x] Lower no-capture enum aggregate labels with scalar payload subpatterns to payload field branch tests.
            - [x] Lower terminal scalar-return enum labels with bool and integer payload subpatterns through typed payload extracts.
            - [x] Lower enum-return enum labels with scalar payload subpatterns through typed payload extracts.
            - [x] Lower non-terminal enum switch assignments with scalar payload subpatterns through typed payload extracts.
            - [x] Allow repeated same-variant scalar payload labels when their payload intervals are provably disjoint.
        - [~] Lower no-capture struct aggregate patterns to ordered field branch tests with preserved field range facts.
          - [x] Lower terminal constructed-object struct aggregate labels whose arms return scalar values to ordered field branch tests.
          - [x] Lower terminal constructed-object struct aggregate labels whose arms return enum values to ordered field branch tests.
          - [x] Preserve declared struct field range facts on scalar field-pattern loads.
          - [x] Reject duplicate property fields and overlapping scalar field intervals before emitting branch blocks.
          - [x] Lower non-terminal constructed-object struct aggregate switch assignments to ordered field branch tests.
          - [x] Lower struct aggregate labels over by-value struct parameters.
            - [x] Represent by-value struct parameter ABI facts with owner type identity and LLVM aggregate shape.
            - [x] Emit by-value struct parameters as LLVM aggregate types in self-host function signatures.
            - [x] Add direct struct-parameter field extraction that lowers to LLVM `extractvalue` without stack materialization.
            - [x] Import declared field range facts for direct struct-parameter field extracts.
            - [x] Route terminal and assignment struct aggregate switch labels over by-value parameters through direct field extracts.
          - [x] Lower struct aggregate labels over field-backed struct scrutinees.
          - [x] Lower top-level discard struct aggregate labels through the zero-pattern branch path.
          - [~] Lower struct aggregate labels with nested aggregate or list field subpatterns through shared pattern-decision block construction.
            - [x] Build a shared pattern-decision node model for scalar, discard, capture, aggregate, enum aggregate, and list subpatterns. (2026-07-07: added flat row-based decision node and member rows that carry scalar, capture, aggregate, enum aggregate, and list descriptor references.)
            - [~] Translate typed struct field-pattern rows into shared pattern-decision nodes.
              - [x] Translate scalar struct field-pattern rows into decision members with preserved field ordinals, type codes, and scalar intervals.
              - [x] Translate struct field capture rows into decision members with preserved capture names and type codes.
              - [x] Add descriptor-backed append helpers for nested aggregate, enum aggregate, and list decision nodes.
              - [x] Translate imported typed nested struct field-pattern rows into decision members after typed pattern row import lands. (2026-07-07: typed nested aggregate/list field rows now append descriptor-backed decision members during struct aggregate preflight; scalar leaves remain on the existing flat tables and nested branch emission remains a follow-up.)
            - [x] Lower nested struct-field aggregate patterns over by-value struct parameters. (2026-07-08: one-level nested scalar struct aggregate members on by-value struct parameters now flatten through the parameter ABI shape and lower to direct `extractvalue` scalar tests for terminal returns and switch assignments; deeper aggregate/list/capture subpatterns remain follow-ups.)
            - [x] Lower nested struct-field aggregate patterns over storage-backed struct scrutinees. (2026-07-08: one-level nested struct aggregate members on storage-backed scrutinees lower to direct pointer-offset scalar field tests for terminal returns and switch assignments; deeper aggregate/list/capture subpatterns remain follow-ups.)
            - [x] Lower nested struct-field list patterns over fixed-array fields. (2026-07-08: storage-backed struct scrutinees now lower one-level fixed-array list field subpatterns to direct constant-offset element loads for terminal returns and switch assignments; nested list captures and deeper element shapes remain follow-ups.)
            - [x] Preserve declared scalar range facts while testing nested field and element subpatterns.
              - [x] Preserve declared scalar range facts while testing storage-backed nested struct aggregate scalar leaves.
              - [x] Preserve declared scalar range facts while testing by-value nested struct aggregate scalar leaves.
              - [x] Preserve declared scalar range facts while testing nested fixed-array element subpatterns. (2026-07-08: nested fixed-array element loads use declared element range metadata from member-path facts.)
            - [x] Preserve capture-local facts for values captured inside nested struct aggregate patterns. (2026-07-08: one-level nested struct aggregate capture leaves now append capture-local rows with flat ABI indexes for by-value params and byte offsets for storage-backed scrutinees, so guards and arm bodies reuse the existing capture override path.)
            - [x] Add focused IR tests for nested struct, nested list-field, and capture-bearing struct aggregate labels.
	              - [x] Add focused IR tests for storage-backed nested struct aggregate labels over terminal return and assignment switches.
	              - [x] Add focused IR tests for by-value nested struct aggregate labels over terminal return and assignment switches.
	              - [x] Add focused IR tests for nested list-field struct aggregate labels over terminal return and assignment switches.
	              - [x] Add focused IR tests for capture-bearing struct aggregate labels.
	              - [x] Add focused IR tests for capture-bearing nested fixed-array list field labels over borrow-backed terminal return and assignment switches.
	              - [x] Add focused IR tests for storage-backed deeper nested aggregate/list field labels over borrow-backed terminal return and assignment switches.
	              - [x] Add focused IR tests for storage-backed deeper nested aggregate/list captures over borrow-backed terminal return and assignment switches.
	              - [x] Add focused IR tests for by-value deeper nested aggregate labels over terminal return and assignment switches.
	              - [x] Add focused IR tests for by-value nested fixed-array list field labels over terminal return and assignment switches.
	              - [x] Add focused IR tests for by-value nested fixed-array list field captures over terminal return and assignment switches.
	            - [x] Lower capture-bearing nested fixed-array list field patterns. (2026-07-08: storage-backed fixed-array field element captures now append capture rows with constant element offsets, reuse struct aggregate guard/body capture overrides, and preserve declared element range facts through emitted LLVM loads; deeper nested element shapes remain follow-ups.)
	            - [x] Lower deeper nested aggregate/list subpatterns beyond one field hop.
	              - [x] Lower storage-backed deeper nested struct aggregate scalar leaves without aggregate materialization. (2026-07-08: storage-backed struct aggregate condition lowering now walks nested aggregate descriptors with an explicit worklist and emits direct pointer-offset scalar loads with declared range metadata.)
	              - [x] Lower storage-backed deeper nested fixed-array list field scalar leaves without aggregate materialization. (2026-07-08: nested fixed-array field descriptors reached through deeper struct aggregate fields reuse the direct constant-offset element load path and preserve declared element range metadata.)
	              - [x] Preserve capture-local facts for storage-backed captures inside deeper nested aggregate/list subpatterns. (2026-07-08: deeper aggregate capture import now walks nested aggregate descriptors with an explicit worklist, appends direct storage offsets for scalar captures, appends constant element offsets for deeper fixed-array element captures, and preserves scalar/element range facts for guard and arm-local loads.)
	              - [x] Lower by-value deeper nested aggregate/list subpatterns beyond one field hop.
	                - [x] Lower by-value deeper nested struct aggregate scalar leaves without aggregate materialization. (2026-07-08: by-value struct aggregate condition lowering now walks nested aggregate descriptors with an explicit flat-ABI frame worklist and emits direct struct-parameter scalar extracts for deeper aggregate leaves.)
	                - [x] Extend by-value struct ABI shape/facts for fixed-array fields and lower no-capture by-value nested fixed-array list field scalar leaves without aggregate materialization. (2026-07-08: struct-value ABI shape walking now expands fixed-array fields into repeated scalar leaves, and by-value aggregate condition lowering emits direct struct-parameter element extracts with declared element range facts.)
	                - [x] Preserve capture-local facts for by-value captures inside nested fixed-array list field subpatterns. (2026-07-08: fixed-array element capture import now records flat ABI element indexes alongside storage offsets so by-value arm overrides emit direct struct-parameter element extracts with declared element range facts.)
	          - [x] Lower guarded no-capture struct aggregate labels after successful field tests.
            - [x] Lower terminal-return guarded no-capture struct aggregate labels after successful field tests.
            - [x] Lower non-terminal assignment guarded no-capture struct aggregate labels after successful field tests.
        - [~] Lower no-capture fixed-array list patterns to indexed element branch tests with preserved element range facts.
          - [x] Lower terminal fixed-array parameter list labels to direct constant-index element branch tests.
          - [x] Preserve declared fixed-array element range facts on direct parameter element extracts.
          - [x] Reject overlapping terminal fixed-array list labels before emitting MIR branch blocks.
          - [x] Reject overlapping non-terminal fixed-array list labels before emitting MIR branch blocks.
          - [x] Lower terminal fixed-array list labels whose arms return enum values.
          - [x] Lower non-terminal fixed-array list switch assignments that continue after the switch.
          - [x] Lower terminal guarded no-capture fixed-array parameter list labels after successful element tests.
          - [x] Lower non-terminal assignment guarded no-capture fixed-array parameter list labels after successful element tests.
          - [x] Lower fixed-array list patterns whose scrutinee is a local or field-backed value.
            - [x] Lower scalar-return terminal fixed-array storage-local list labels through constant-offset element branch tests.
            - [x] Lower enum-return terminal fixed-array storage-local list labels through constant-offset element branch tests.
            - [x] Lower non-terminal fixed-array storage-local list switch assignments through constant-offset element branch tests.
            - [x] Lower fixed-array field-backed list labels through typed member-path element branch tests.
              - [x] Lower scalar-return terminal fixed-array field-backed list labels through typed member-path element branch tests.
              - [x] Lower enum-return terminal fixed-array field-backed list labels through typed member-path element branch tests.
              - [x] Lower non-terminal fixed-array field-backed list switch assignments through typed member-path element branch tests.
          - [x] Lower top-level discard fixed-array list labels through the zero-pattern branch path.
        - [ ] Lower nested aggregate and list field patterns through shared pattern-decision block construction.
          - [x] Use the shared pattern-decision node model for enum payload subpatterns.
            - [x] Translate enum payload scalar and capture rows into decision members with preserved payload ordinals, type codes, capture names, and variant identity.
            - [x] Route enum payload nested-shape rows through shared decision block construction.
            - [x] Combine enum payload scalar, capture, and nested-shape rows into one contiguous decision-member span.
          - [x] Use the shared pattern-decision node model for fixed-array element subpatterns.
            - [x] Translate fixed-array element scalar and capture rows into decision members with preserved element ordinals, type codes, and capture names.
            - [x] Route fixed-array element nested-shape rows through shared decision block construction.
            - [x] Combine fixed-array element scalar, capture, and nested-shape rows into one contiguous decision-member span.
          - [x] Lower nested enum aggregate subpatterns inside enum payload fields. (2026-07-08: enum-payload parse paths now carry nested enum aggregate descriptor spans into terminal and assignment lowering; MIR lowering extracts the outer enum payload, reads the nested enum tag, compares nested scalar payload leaves, and preserves the path to LLVM without stack materializing the enum payload.)
          - [x] Preserve enum and aggregate fixed-array storage layout facts before list-element nested lowering. (2026-07-08: source-local layout now sizes fixed arrays from enum/aggregate element storage layout facts instead of treating named element arrays as unsupported or one element, preserving byte size and alignment facts through lower-MIR.)
          - [x] Lower nested aggregate subpatterns inside fixed-array list elements. (2026-07-08: storage-backed fixed-array list element aggregate descriptors now lower through an explicit aggregate-condition frame stack, preserving element aggregate type/layout facts and emitting scalar pointer-offset field tests without recursive helper calls; focused terminal and assignment LLVM probes pass with range metadata on element-field loads.)
          - [x] Lower nested list subpatterns inside fixed-array list elements. (2026-07-08: focused `emit-llvm` probes over `Box { Matrix: [[1, 2], _] }` pass for terminal and assignment switches, preserving direct scalar `i8` loads with `!range` through LLVM.)
          - [x] Preserve multi-dimensional scalar fixed-array row strides while lowering nested list-element list patterns. (2026-07-08: fixed-array storage layout now multiplies remaining scalar array suffixes into the outer element stride, nested list-element lowering recursively descends into inner array rows, and `selfhost.Ir` second-row facts assert direct scalar loads at row-two byte offsets without aggregate carrier loads.)
          - [x] Thread fixed-array suffix depth through recursive nested list-element list lowering. (2026-07-08: recursive storage-backed nested-list lowering now asks layout for the current array suffix depth instead of always reusing depth one, keeping three-dimensional scalar list elements on direct scalar loads at the correct byte offsets.)
          - [x] Eliminate dead aggregate carrier loads emitted before scalar nested fixed-array element tests in raw O0 LLVM. (Completed 2026-07-12: LLVM block/direct-switch emission counts value uses, terminators, and wide-call operands, then elides only unused `FixedArrayLoad`/`StructValueLoad` carriers; scalar loads and `!range` metadata remain. The focused `LlvmBlockEmissionDropsUnusedAggregateCarrierLoadsButKeepsScalarRangeLoads` executable fact passed.)
          - [~] Route nested pattern failures to the next sibling label without leaking captures. (2026-07-12 update: the post-parenthesis-repair cold `Sibling` execution rebuilt the self-host library/package/test executable but still failed the same eight source-facing facts; the row-level preflight continued to pass. Staged probes proved typed rows for both alternatives, both enum label parses, full section parsing, descriptor validation, decision preflight, capture-aware call validation, enum identity, MIR construction, parameter/ABI/value facts, and return-range validation all pass. The exact terminal dispatcher alone rejected because it speculatively tried narrower lowerers before the generic sibling-aware path. Terminal switches with a top-level first-case `|` now route directly to that generic path, and the remaining field-list `switch (` scan now uses `MatchingParenForMirLowering`. Both owning modules pass focused checks. A second cold executable run after those repairs still produced the same eight failures, so the dispatcher repair is retained but executable closure remains open; the next boundary to stage is the public compile orchestration/function-effect prepass shared by assignment and terminal functions. A temporary conservative-effect fallback and a standalone import probe were both removed without claiming evidence: the first test invocation produced no progress before interruption, and the standalone probe would have rebuilt and optimized the entire compiler graph. The checked-path semantic blocker is now repaired: the preliminary name pass parses typed signature parameters and excludes typed signature syntax from value-use resolution, while redeclaration analysis scopes pattern captures by case section and sibling alternative. A narrow executable probe passed the exact struct/list sibling semantic sources plus duplicate-negative controls; full source-to-LLVM closure remains pending.)
            - [x] Repair checked semantic scoping for typed signatures and sibling pattern captures. (Completed 2026-07-12: name resolution now seeds parsed typed function/parameter names, recognizes typed-local headers, and keeps switch-pattern/type/member syntax out of the value namespace while leaving exact owner/category validation to the typed binder; redeclaration checking permits equal-name captures only across distinct sibling alternatives or case sections and still rejects same-alternative, parameter/local, and duplicate-parameter declarations. Function arity, uniqueness, and finite-call rows also use parsed typed signatures. The owning module check and a narrow executable semantic probe passed.)
          - [~] Add overlap validation for nested aggregate and list descriptors before emitting MIR. (2026-07-08: added source-facing `selfhost.Ir` facts for nested aggregate/list, list-element aggregate/list, and nested enum-payload overlap rejection; dynamic nested aggregate guards still allow later overlapping unguarded siblings. `IrTests.stark` semantic host-test passed with 0 diagnostics; filtered fact-runner execution remains pending.)
            - [x] Add row-model overlap validation for scalar, nested aggregate, nested enum variant, and nested list descriptor decisions.
            - [x] Hook nested descriptor overlap validation into source switch preflight before MIR branch block emission.
              - [x] Add a shared-decision unguarded sibling preflight helper over decision-member spans.
              - [x] Validate decision-member spans before the shared-decision preflight compares sibling cases.
              - [x] Call the shared-decision preflight helper from terminal fixed-array list branch-block lowering.
              - [x] Call the shared-decision preflight helper from fixed-array list assignment branch-block lowering.
              - [x] Call the shared-decision preflight helper from enum-payload terminal and assignment branch-block lowering.
              - [x] Call the shared-decision preflight helper from struct-aggregate terminal and assignment branch-block lowering.
              - [x] Allow fixed-array list preflight rows to carry nested element descriptor spans.
              - [x] Allow struct-aggregate preflight rows to carry nested field descriptor spans.
              - [x] Call the shared-decision preflight helper from nested branch-block lowering. (2026-07-08: struct aggregate terminal and assignment parse paths now defer nested-bearing labels past the scalar-only flat overlap check so the descriptor-backed shared-decision preflight owns nested sibling validation before the conservative nested-codegen reject.)
              - [x] Add source-facing rejection facts for nested aggregate/list and enum-payload overlaps after shared-decision lowering. (2026-07-08: `RejectsNestedAggregateAndListPatternOverlapFromAst`, `RejectsListElementNestedPatternOverlapFromAst`, and `RejectsOverlappingNestedEnumPayloadLabelsFromAst` cover source switch preflight paths; semantic host-test passed with 0 diagnostics.)
          - [x] Add focused IR tests for nested enum-payload, list-element aggregate, and list-element list patterns.
            - [x] Add focused row-model IR facts for enum-payload nested-shape rows, fixed-array element nested-shape rows, combined decision-member spans, nested descriptor overlap validation, decision-span validation, unguarded sibling preflight validation, fixed-array list preflight row building, nested fixed-array list preflight row building, enum-payload preflight row building, struct-aggregate preflight row building, and nested struct-aggregate preflight row building.
            - [x] Add focused lowering IR tests for nested enum-payload terminal and assignment patterns.
            - [x] Add focused lowering IR tests for list-element aggregate and list-element list patterns.
              - [x] Add focused host-test lowering probes for list-element aggregate terminal and assignment patterns. (2026-07-08: inline `emit-llvm` probes over `Box { Points: [Point { X: 1, Y: 2 }, _] }` pass with no diagnostics and show scalar `i8` element-field loads carrying `!range`.)
              - [x] Add focused lowering IR tests for list-element aggregate patterns in `tests-stark/selfhost.Ir`. (2026-07-08: added borrow-backed terminal and assignment facts for `Point[2]` element aggregate patterns with direct scalar `i8` field loads, `!range`, and no `extractvalue`/LLVM switch fallback.)
              - [x] Add focused lowering IR tests for list-element list patterns. (2026-07-08: added borrow-backed terminal and assignment facts for `i8[2][2]` list-element list patterns with direct scalar element loads, `!range`, and no `extractvalue`/LLVM switch fallback.)
        - [~] Lower switch capture bindings into section-local storage without aliasing unrelated locals.
          - [x] Lower tuple enum payload capture bindings for non-terminal assignment switch arms.
          - [x] Lower named enum payload capture bindings for non-terminal assignment switch arms.
          - [x] Keep enum payload capture locals scoped to their matched assignment arm.
          - [x] Extract captured enum payload values in the matched assignment arm block.
          - [x] Lower enum payload capture bindings for terminal return switch arms.
          - [x] Lower struct aggregate field capture bindings for terminal and assignment switch arms.
            - [x] Parse tuple and property struct aggregate field captures into arm-local capture rows.
            - [x] Lower terminal struct field captures through direct field extracts or typed aligned loads.
            - [x] Lower assignment struct field captures through arm-local override values.
            - [x] Preserve declared scalar field range facts on captured struct field values.
            - [x] Add focused IR facts for storage-backed and by-value struct field capture switches.
            - [x] Add a focused self-host probe for struct aggregate field capture lowering.
            - [x] Finish focused front-end verification for struct aggregate field capture lowering. (2026-07-07: added parser coverage for tuple/property struct capture `PatternBinding` rows and binding coverage for guard/body resolution plus default-section non-leakage.)
          - [x] Lower fixed-array list element capture bindings for terminal and assignment switch arms.
            - [x] Lower fixed-array list element capture bindings for terminal return switch arms.
            - [x] Lower fixed-array list element capture bindings for non-terminal assignment switch arms.
        - [~] Merge pattern capture facts across sibling labels before lowering section bodies.
          - [~] Merge enum payload capture facts across sibling labels before lowering shared assignment and terminal section bodies. (2026-07-12 update: fresh-package sibling runs before and after the terminal-dispatch repair still reject the assignment and terminal enum facts. Staged terminal probes passed parsing through MIR/value-fact construction; first-case sibling alternatives now select the generic sibling-aware terminal path directly, but the unchanged public-compile failures show another earlier/shared orchestration boundary remains. The checked semantic-gate repair clears the six checked aggregate/list facts but does not affect these two unchecked enum facts. The function-effect prepass remains the next staged target because it independently lowers every body before emission and is shared by assignment and terminal functions. Neither enum item is marked complete from check-only evidence.)
          - [x] Merge struct aggregate field capture facts across sibling labels before lowering shared section bodies. (2026-07-07: implemented assignment and terminal return lowering with per-label field capture rows; direct lowering and IR test file checks passed, filtered IR test runner stayed silent and was interrupted.)
          - [x] Merge fixed-array list element capture facts across sibling labels before lowering shared section bodies. (2026-07-07: implemented by-value, storage-local, and field-backed assignment plus terminal return paths with per-label element capture rows; direct lowering and IR test file checks passed, filtered IR test runner stayed silent and was interrupted.)
        - [x] Lower guarded aggregate and list switch labels after guard expression lowering can consume capture locals.
          - [x] Lower guarded no-capture struct aggregate labels after successful field tests.
          - [x] Lower guarded no-capture fixed-array parameter list labels after successful element tests.
          - [x] Lower guarded no-capture fixed-array storage-local and field-backed list labels after successful element tests.
            - [x] Lower scalar-return terminal guarded fixed-array storage-local list labels after successful element tests.
            - [x] Lower enum-return terminal guarded fixed-array storage-local list labels after successful element tests.
            - [x] Lower non-terminal assignment guarded fixed-array storage-local list labels after successful element tests.
            - [x] Lower scalar-return terminal guarded fixed-array field-backed list labels after successful element tests.
            - [x] Lower enum-return terminal guarded fixed-array field-backed list labels after successful element tests.
            - [x] Lower non-terminal assignment guarded fixed-array field-backed list labels after successful element tests.
          - [x] Lower guarded aggregate and list labels that use capture locals.
        - [~] Reject duplicate or overlapping aggregate and list switch labels before emitting partial MIR.
          - [x] Treat `when true` aggregate and list labels as unconditional for overlap validation.
          - [x] Reject overlapping nested aggregate and list labels after shared pattern-decision block construction lands. (2026-07-08: source-facing `selfhost.Ir` facts now cover nested aggregate/list, list-element aggregate/list, and nested enum-payload overlap rejection; filtered fact-runner execution remains pending.)
            - [x] Detect provably disjoint nested descriptor decisions over scalar intervals, nested enum variants, nested aggregate descriptors, and nested list descriptors.
            - [x] Add shared-decision unguarded sibling overlap preflight over nested descriptors.
            - [x] Reject overlapping nested aggregate and list labels in source switch preflight once nested lowering emits shared decision blocks. (2026-07-08: added source-facing `selfhost.Ir` rejection facts and passed semantic host-test validation.)
          - [x] Reject overlapping top-level discard labels against unconditional aggregate/list sibling labels.
          - [x] Reject overlapping whole-value capture labels once those label forms are represented (2026-07-07: whole captures now reuse the zero-pattern overlap path, so unconditional fixed-array and struct `case var capture` labels reject both before and after sibling aggregate/list labels, with `when true` treated as unconditional).
    - [x] Add a backend switch terminator or direct LLVM switch emission for dense literal switch lowering.
  - [~] Lower `try`.
    - [x] Represent source `try` expressions in the MIR source expression model without erasing operand identity.
    - [x] Resolve typed `[Ok]` and `[Err]` role tags plus scalar and enum payload facts from enum layout rows.
    - [x] Resolve source and return enum-family compatibility for `try`.
    - [x] Lower same-family `try` expressions into tag tests, payload extraction, and early error returns.
      - [x] Build a reusable MIR helper for same-family `try` tag tests, early error returns, and success payload extraction.
      - [x] Route same-family source `try` expression lowering through the reusable MIR helper.
        - [x] Route local `try` bindings followed by terminal returns through the reusable MIR helper.
        - [x] Route terminal return-operand `try` expressions through the reusable MIR helper.
        - [x] Route assignment right-side `try` expressions through the reusable MIR helper.
          - [x] Route scalar local assignment right-side `try` expressions followed by terminal returns through the reusable MIR helper.
          - [x] Route storage-backed assignment right-side `try` expressions through the reusable MIR helper.
            - [x] Route initialized scalar stack storage assignment right-side `try` expressions through stack slots and typed stores.
            - [x] Route uninitialized scalar stack storage assignment right-side `try` expressions after preserving write-before-read proofs.
            - [x] Route field storage assignment right-side `try` expressions through typed place stores.
            - [x] Route indexed storage assignment right-side `try` expressions through typed element stores.
              - [x] Route constructed-object fixed-array field assignment RHS `try` expressions through bounded typed element stores.
              - [x] Route mutable signature-slice element assignment RHS `try` expressions through unbounded typed element stores.
              - [x] Route local fixed-array and local slice element assignment RHS `try` expressions through typed element stores.
    - [x] Lower same-family `try` bindings across statement boundaries without dropping payload facts.
      - [x] Lower immediate local `try` bindings before terminal returns without dropping success payload facts.
      - [x] Lower local `try` bindings across ordered successor `var` locals without dropping success payload facts.
      - [x] Lower local `try` bindings across ordered successor storage mutations without dropping success payload facts.
      - [x] Lower local `try` bindings across successor storage-creating declarations without dropping success payload facts.
        - [x] Lower initialized scalar stack storage declarations after local `try` success edges.
        - [x] Lower uninitialized scalar stack storage declarations before later successor mutations after local `try` success edges.
        - [x] Lower fixed-array and slice storage declarations after local `try` success edges.
        - [x] Lower enum storage declarations after local `try` success edges.
        - [x] Lower stack constructed-object storage declarations after local `try` success edges.
      - [x] Lower nested successor `try` bindings after an earlier local `try` success edge.
    - [x] Lower cross-family `try` expressions through declared error-funnel construction.
  - [x] Lower `become`.
    - [x] Lower direct i64 `become` calls to MIR tail-call payloads and terminator blocks with facts.
    - [x] Lower typed non-i64 `become` calls once typed tail-call terminator emission exists.
    - [x] Validate carried argument facts before emitting MIR tail-call payloads.
    - [x] Validate tail-call payload fact rows before emitting MIR tail-call blocks.
  - [x] Lower recursion and tail calls.
    - [x] Preserve direct i64 tail-call payloads through HIR-to-MIR lowering.
    - [x] Lower typed tail-call terminators for all scalar MIR return types.
    - [x] Lower self-recursive source functions through the full HIR body pipeline.
    - [x] Preserve recursive call facts through checked source lowering.
    - [x] Reject source call cycles reachable from finite checked source functions.
  - [x] Lower object construction.
    - [x] Lower empty `stack T value = new T()` construction into MIR stack allocation.
    - [x] Lower target-typed empty `stack T value = new()` construction into MIR stack allocation.
    - [x] Preserve stack allocation alignment, noalias, and nonnull facts through LLVM emission.
    - [x] Add MIR pointer-offset operations for storage-backed field places.
    - [x] Add typed MIR pointer load and store operations for storage-backed values.
    - [x] Preserve derived pointer alignment, noalias, and nonnull facts through MIR memory operations.
    - [x] Emit MIR pointer memory operations through LLVM, text rendering, and package serialization.
    - [x] Lower stack object field initializers into storage-backed writes.
    - [x] Lower positional record constructors into storage-backed writes.
    - [x] Lower heap object construction through runtime allocation and pointer facts.
    - [x] Lower constructed object field and member reads from storage-backed places.
  - [~] Lower enum payloads.
    - [x] Preserve enum payload layout rows through MIR enum layout facts, package images, and LLVM storage helpers.
    - [x] Add MIR enum tag and payload extraction operations with range facts and LLVM layout emission.
    - [x] Represent enum-valued source expressions in the MIR source expression model.
    - [x] Add MIR enum construction operations that carry enum owner, variant, payload ordinal, and payload value facts.
    - [x] Thread enum layout fact tables through normal MIR-to-LLVM instruction emission.
    - [x] Lower source enum constructors into MIR enum construction operations.
      - [x] Lower source unit enum constructors into MIR enum construction operations.
      - [x] Lower source positional scalar payload constructors into MIR enum construction operations.
      - [x] Lower source named payload constructors into MIR enum construction operations.
    - [~] Lower enum payload loads and stores for storage-backed places.
      - [x] Add owner-aware MIR enum value load and store operations for storage-backed enum places.
      - [x] Emit owner-aware enum value loads and stores through LLVM layout helpers.
      - [x] Preserve enum value load and store operations through MIR text and package serialization.
      - [x] Classify enum value loads and stores in MIR memory-effect scans.
      - [x] Lower storage-backed enum local initializers and assignments.
        - [x] Represent stored enum locals distinctly from SSA enum values in source local type codes.
        - [x] Lower straight-line stack enum local initializers through owner-aware enum stores.
        - [x] Lower straight-line stack enum local reads through owner-aware enum loads.
        - [x] Lower straight-line mutable stack enum local assignments through owner-aware enum stores.
        - [x] Lower storage-backed enum locals through terminal `if` branches.
        - [x] Lower storage-backed enum locals through switch arms.
      - [x] Lower enum-valued field and member reads and writes on storage-backed object places.
    - [x] Lower enum-valued function returns through owner-aware enum return carriers.
      - [x] Render enum-valued MIR return types from owner layout facts instead of `unknown`.
      - [x] Lower direct enum-constructor terminal returns through the single-function entry.
      - [x] Lower storage-backed enum local terminal returns through the single-function entry.
      - [x] Lower enum-valued terminal `if` and `switch` returns through the same owner validation path.
        - [x] Lower enum-valued terminal `if` returns through the owner validation path.
        - [x] Lower enum-valued integer terminal `switch` returns through the owner validation path.
        - [x] Lower enum-valued boolean and enum-case terminal `switch` returns through the owner validation path.
  - [~] Lower dynamic storage.
    - [x] Lower arena-backed HIR dynamic storage init and reserve operations to MIR.
    - [x] Lower arena-backed dynamic reserve statements inside switch storage arms.
    - [x] Emit arena frame leaves for switch paths that allocate arena dynamic storage.
    - [x] Lower arena-backed dynamic locals before terminal `if` returns.
    - [x] Lower arena-backed dynamic locals before terminal integer switch returns.
    - [x] Lower arena-backed dynamic locals before terminal boolean switch returns.
    - [x] Lower arena-backed dynamic locals before terminal enum-case switch returns.
    - [x] Lower arena-backed dynamic locals before enum-valued terminal `if` returns.
    - [x] Lower arena-backed dynamic locals before enum-valued terminal integer switch returns.
    - [x] Lower arena-backed dynamic locals before enum-valued terminal boolean switch returns.
    - [x] Lower arena-backed dynamic locals before enum-valued terminal enum-case switch returns.
    - [x] Emit arena frame leaves for terminal switch return arms that allocate arena dynamic storage.
  - [~] Lower all storage selectors.
    - [x] Lower fixed-size HIR arena allocations to MIR arena allocation instructions.
    - [x] Lower fixed-size source stack allocations to MIR stack allocation instructions.
  - [ ] Port runtime drop lowering.
  - [ ] Port destructor call insertion.
  - [ ] Port ownership-driven cleanup.
  - [ ] Port compile-time evaluator lowering.
  - [ ] Port compile-time evaluated expressions.
  - [~] Port imported source lowering.
    - [x] Lower unit enum values referenced through an imported source module's own imports.
  - [~] Port imported-template lowering.
    - [x] Lower fact-complete typed integer/bool constant-expression return templates directly from the package graph to MIR, without reconstructed source. Consume ordered top-level empty and pure constant-expression preambles plus the terminal return, including the same sequence inside one terminal flat block; consume canonical Stage0 lowercase type kinds and inferred `Name`-backed unary/binary results; fold bounded nested literal/unary/binary/comparison-chain/conditional/comptime/conversion arithmetic, comparison, and boolean trees to one MIR constant; join ordered conversion expressions to exact published target-type/range side facts across statement roots; require an exact specialized return type/signedness/range-fact match; reject scalar-inapplicable caller fact families rather than dropping LLVM inputs; reject overflow, malformed/detached rows, and every template carrying unconsumed operation families; keep statement/expression traversal linear and allocation-free with transactional output reservation; and publish exact MIR value plus function-return ranges to LLVM. (2026-07-11)
    - [x] Fold package-template integer power, signed wrapping/saturating arithmetic, signed wrapping negation, bitwise operations, and checked shifts directly to exact MIR constants; clamp signed saturation to the published ranged type and retain compatibility fallback where the signed fact carrier cannot represent the full unsigned result. (2026-07-11)
    - [x] Fold ordered immutable scalar local constants and later name references directly from typed statements plus exact `const` declaration side rows; validate local storage/provenance/type/range facts, reject duplicate or unresolved names transactionally, keep the environment bounded to 64 stack entries with cached name hashes and exact collision comparison through reusable scratch, and publish only the terminal singleton MIR/LLVM range. (2026-07-11)
    - [x] Fold initialized independent scalar `stack`/`register` variables and direct-name assignments from exact `var` declaration side rows; consume the duplicated statement/target name facts and both root expression trees, enforce mutability and declared range, support `=` plus every checked/wrapping/saturating/bitwise compound assignment, and eliminate all local storage while publishing the terminal singleton range to LLVM. (2026-07-11)
    - [x] Track definite initialization for independent scalar package-template locals; admit initializer-free `stack`/`register` declarations, reject name reads and compound updates before initialization, accept both Stage0-compatible first plain `=` and distinct `init =` writes only to mutable locals, and eliminate the storage after validating exact declaration/type/range/operator facts. (2026-07-11)
    - [~] Connect direct package-template MIR lowering to imported generic specialization and expand it across the remaining structured statement/expression families before deleting the compatibility source bridge.
      - [x] Resolve a requested specialization by exact qualified-resolved-name plus overload key, join its base qualified name to exactly one package compiler-effect row, validate template/effect backend-mode consistency before mutation, and carry purity/memory, progress, unwind, hot/cold, strict-FP, inline/opaque, fast-call, tail, and norecurse facts through numbered LLVM definition emission. Missing, ambiguous, or inconsistent package facts fail transactionally. (2026-07-11)
      - [ ] Route the specialization adapter from the Stage1 package-import orchestration once that driver owns imported package selection and concrete type/comptime substitution.
      - [ ] Expand direct lowering across the remaining structured statement/expression families before deleting the compatibility source bridge.
  - [~] Preserve range facts through MIR lowering.
    - [x] Reject integer range facts on pointer HIR values at the common value-fact compatibility boundary.
    - [x] Reject stale pointer range facts when binding or rebinding SSA local symbols.
    - [x] Reject stale pointer range facts before emitting MIR return blocks.
    - [x] Reject stale pointer range facts before emitting call and tail-call argument payloads.
    - [x] Reject integer range facts outside scalar operand types before emitting binary and compound arithmetic.
    - [x] Reject literal range facts that do not describe emitted scalar constants.
    - [x] Reject stale pointer range facts before emitting MIR global stores.
    - [x] Reject invalid condition range facts before emitting MIR conditional branches.
    - [x] Reject invalid tail-call payload range facts before emitting MIR tail-call blocks.
    - [x] Attach boolean range facts to fallible arena dynamic storage reserve results.
    - [ ] Audit range fact validation when each remaining HIR value producer is added.
  - [~] Preserve pointer nullability facts through MIR lowering.
    - [x] Reject pointer nullability facts on scalar HIR values at the common value-fact compatibility boundary.
    - [x] Enforce known-null and known-nonnull global-store obligations before MIR store emission.
    - [x] Reject stale scalar nullability facts when binding or rebinding SSA local symbols.
    - [x] Reject stale scalar nullability facts before emitting MIR return blocks.
    - [x] Reject stale scalar nullability facts before emitting call and tail-call argument payloads.
    - [x] Reject stale scalar nullability facts before emitting binary and compound arithmetic.
    - [x] Reject invalid scalar and false pointer nullability facts before emitting constants.
    - [x] Reject stale scalar nullability facts before emitting MIR global stores.
    - [x] Reject stale scalar nullability facts before emitting MIR conditional branches.
    - [x] Reject stale scalar nullability facts before emitting MIR tail-call blocks.
    - [x] Attach known-nonnull pointer facts to fixed-size stack allocation results.
    - [x] Attach known-nonnull pointer facts to fixed-size arena allocation results.
    - [x] Attach known-nonnull pointer facts to arena dynamic storage owner results.
    - [x] Preserve known-nonnull facts on derived MIR pointer offsets.
    - [ ] Audit nullability fact validation when each remaining pointer-producing HIR construct is added.
  - [~] Preserve alias facts through MIR lowering.
    - [x] Enforce noalias global-store obligations before MIR store emission.
    - [x] Attach noalias facts to fixed-size stack allocation results.
    - [x] Attach noalias facts to fixed-size arena allocation results.
    - [x] Attach noalias facts to arena dynamic storage owner results.
    - [x] Preserve noalias facts on derived MIR pointer offsets.
    - [ ] Audit alias fact transfer when each remaining memory-producing HIR construct is added.
  - [~] Preserve ABI facts through MIR lowering.
    - [x] Enforce calling-ABI global-store obligations before MIR store emission.
    - [ ] Audit ABI fact transfer when each remaining callable-producing HIR construct is added.
  - [~] Preserve layout facts through MIR lowering.
    - [x] Enforce alignment global-store obligations before MIR store emission.
    - [x] Attach alignment facts to fixed-size stack allocation results.
    - [x] Attach alignment facts to fixed-size arena allocation results.
    - [x] Attach alignment facts to arena dynamic storage owner results.
    - [x] Preserve derived alignment facts on MIR pointer offsets and aligned pointer memory ops.
    - [ ] Audit layout fact transfer when each remaining aggregate and pointer-producing HIR construct is added.
  - [ ] Preserve ownership facts through MIR lowering.
  - [ ] Preserve assembly facts through MIR lowering.
  - [~] Preserve arena facts through MIR lowering.
    - [x] Mark MIR function builders that lower fixed-size arena allocations as arena-frame users.
    - [x] Mark MIR function builders that lower arena dynamic storage operations as arena-frame users.
    - [x] Emit arena frame enter and leave instructions for arena-using MIR function exits.
    - [ ] Audit arena fact transfer when each remaining arena-producing HIR construct is added.
  - [ ] Preserve drop facts through MIR lowering.

- [~] Implement SSA lowering.
  - [~] Port MIR-to-SSA lowering.
    - [x] Add the allocation-linear, zero-remap MIR-to-SSA foundation for dense instructions, blocks, functions, and value facts.
    - [x] Reject malformed MIR and missing/misaligned value-fact rows before publishing any SSA output table.
    - [x] Preserve bodyless MIR declaration rows without inventing blocks, values, or empty backend-fact carriers.
    - [ ] Replace the index-preserving foundation with full CFG shaping, phi placement, and SSA renaming as the remaining lowering slices land.
  - [ ] Port SSA block shaping.
  - [ ] Port SSA phi construction.
  - [ ] Port memory operation lowering.
  - [~] Port the SSA artifact model.
    - [x] Add flat dense instruction, block, and function rows with typed SSA handles, contiguous ownership ranges, and originating MIR identities.
    - [ ] Add the remaining Stage 0 SSA artifact payloads required by shaped blocks, validation diagnostics, deterministic rendering, and optimized-SSA output.
  - [ ] Port the SSA deterministic renderer.
  - [ ] Port the structured invalid-IR fixture path.
  - [ ] Port SSA type validation.
  - [ ] Port SSA dominance validation.
  - [ ] Port SSA control-flow validation.
  - [ ] Port SSA memory validation.
  - [ ] Port SSA range-fact validation.
    - [x] Validate exact integer-range preservation at the foundational SSA rewrite boundary.
  - [ ] Port SSA ABI-fact validation.
    - [x] Validate exact callable-ABI preservation at the foundational SSA rewrite boundary.
  - [~] Port SSA package-fact validation.
    - [x] Require one exact `ValueFacts` row per dense MIR/SSA value at the initial phase boundary; never synthesize a missing backend fact row.
    - [ ] Validate the remaining durable package fact tables as their SSA carriers land.
  - [ ] Port value fact analysis.
  - [ ] Revalidate facts at each SSA rewrite boundary.
    - [x] Establish dense `SsaValueId` generated/empty/preserve/translate/recompute/consume and parameter/global/return import APIs for every current backend value-fact family.
    - [x] Distinguish missing facts from changed preserved facts and validate exact alignment, ABI, noalias, volatile, range, nullability, and text-constant payloads.
    - [x] Add one constant-time gate that validates every backend value fact present on a rewritten SSA value, avoiding per-pass category omissions and temporary collections.
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
    - [x] Preserve alignment, ABI, noalias, volatile, range, nullability, and text-constant facts through the foundational SSA value-transfer boundary.
    - [x] Add an exact preservation gate for those fact families so a rewrite cannot silently weaken or mutate LLVM inputs.
    - [x] Provide an all-present-facts optimizer gate for the default SSA rewrite path before ABI/LLVM lowering.

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
  - [~] Attach range facts to LLVM IR.
    - [x] Attach scalar function return and parameter range facts in deterministic textual LLVM emission.
    - [x] Attach call-result and global-load range metadata in deterministic textual LLVM emission.
    - [ ] Attach range facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach noalias facts to LLVM IR.
    - [x] Emit whole-parameter disjoint facts as deterministic textual LLVM separate-storage assumes.
    - [x] Attach noalias arena allocation facts in deterministic textual LLVM emission.
    - [x] Attach parameter-rooted scoped noalias metadata to deterministic textual LLVM loads and stores.
    - [x] Attach call-site scoped noalias metadata to deterministic textual LLVM direct and tail calls.
    - [ ] Attach noalias facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach readonly facts to LLVM IR.
    - [x] Attach readonly borrow parameter facts in deterministic textual LLVM emission.
    - [ ] Attach readonly facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach alignment facts to LLVM IR.
    - [x] Attach global load/store alignment facts in deterministic textual LLVM emission.
    - [ ] Attach alignment facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach volatile facts to LLVM IR.
    - [x] Attach volatile global load/store facts in deterministic textual LLVM emission.
    - [ ] Attach volatile facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach calling-convention facts to LLVM IR.
    - [x] Attach tail-callable function and call-site facts as deterministic textual `tailcc` and `musttail`.
    - [x] Attach FFI and target ABI calling conventions in textual LLVM emission.
    - [ ] Attach calling-convention facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach linkage facts to LLVM IR.
    - [x] Attach source function linkage facts in deterministic textual LLVM emission.
    - [x] Attach source function non-preemption facts in deterministic textual `dso_local` emission.
    - [x] Attach MIR global linkage fact tables in deterministic textual LLVM global emission.
    - [ ] Import source global visibility into MIR global linkage facts after source-backed global lowering exists.
    - [ ] Attach linkage facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach section facts to LLVM IR.
    - [x] Attach MIR global section fact tables in deterministic textual LLVM global emission.
    - [ ] Import source global section attributes into MIR global section facts after source-backed global lowering exists.
    - [x] Attach function section facts in deterministic textual LLVM emission after function section facts are modeled.
    - [ ] Import source function section attributes into MIR function section facts after function section attributes are parsed.
    - [ ] Attach section facts through the libLLVM module builder after ABI/SSA object emission lands.
  - [~] Attach visibility facts to LLVM IR.
    - [x] Attach MIR global LLVM visibility fact tables in deterministic textual LLVM global emission.
    - [ ] Import source global LLVM visibility attributes into MIR global visibility facts after source-backed global lowering exists.
    - [x] Attach function LLVM visibility facts in deterministic textual LLVM emission after function visibility facts are modeled.
    - [ ] Import source function LLVM visibility attributes into MIR function visibility facts after function visibility attributes are parsed.
    - [ ] Attach visibility facts through the libLLVM module builder after ABI/SSA object emission lands.
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
  - [~] Implement the logical package-image load path.
    - [x] Validate the host logical `STRS`/`PINF`/`MANF` section wrapper.
    - [x] Inspect the host logical `STRS`/`PINF`/`MANF` section wrapper.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [x] Expose compact logical package-image facts for compatibility checks without materializing `MANF`.
    - [x] Keep manifest-backed module resolution from falling back to raw `.starkpkg` source reads.
    - [x] Keep library package images scoped to source-owned modules while preserving direct external imports.
    - [x] Compile library dependency objects only from source-backed imports and use package-backed facts as imports.
    - [~] Decode `MANF` and build the self-host logical package model from binary images.
      - [x] Expose validated `MANF` section range facts and compressed payload copying for the self-host logical load handoff.
      - [x] Decode Stage1-produced uncompressed Brotli `MANF` streams without a host handoff.
      - [ ] Decode general Brotli `MANF` streams emitted by Stage0 before the host handoff is removed.
      - [x] Parse already-decoded Stage0 `MANF` JSON into a compact self-host manifest summary while validating identity/profile/target/backend facts against `PINF`/`STRS`.
      - [x] Parse decoded Stage0 `MANF` module rows into compact module-section summaries for typed-interface loader handoff.
      - [x] Build a compact single-parse self-host logical manifest model with manifest summary facts and ordered module rows.
    - [x] Materialize typed interface declarations.
      - [x] Parse typed-interface declaration header rows with owned name/qualified-name/visibility/kind text and compact count/flag facts.
      - [x] Materialize typed-interface declaration type-reference payloads into package-owned fact rows.
        - [x] Parse top-level declaration type-reference payload summaries for alias targets, function returns/parameters, globals, and type fields.
        - [x] Intern decoded type-reference text and nested type-reference graph rows into package-owned fact tables.
          - [x] Materialize root and nested type-reference rows plus scalar text rows for element, type-argument, comptime-value type, return, parameter, and associated-owner children.
          - [x] Materialize callable memory-contract group rows and full comptime value argument payload rows.
    - [x] Materialize typed interface functions and methods.
      - [x] Parse typed function and method callable facts with owned name/qualified-name/visibility/symbol/kind/resolved-name/overload/inline/ABI/backend/link-name text and compact backend flag/count facts.
      - [x] Parse typed function and method parameter metadata facts with owned parameter names, const/disjoint flags, raw-pointer element-count expressions, and required parameter type-object validation.
      - [x] Materialize callable return and parameter type-reference row links for methods without overloading top-level function payload ordinals.
      - [x] Materialize callable generic parameters, comptime generic parameters, type-parameter constraints, thread-safety predicates, and value contracts into package-owned rows.
      - [x] Materialize callable asm payload facts and link-name/FFI ABI rows for backend import without source reconstruction.
    - [x] Materialize typed interface globals.
    - [x] Materialize typed interface traits.
    - [x] Materialize typed interface layouts and aliases.
      - [x] Materialize typed type layout metadata, pack/align facts, and associated alias target rows.
      - [x] Materialize enum variant payload type rows.
      - [x] Materialize remaining source-level type-alias rows needed by package import.
    - [x] Materialize effect fact sections.
      - [x] Materialize compiler-facts function effect profile rows.
      - [x] Materialize function semantic memory effect rows.
    - [x] Materialize ownership fact sections.
    - [x] Materialize range fact sections.
    - [x] Materialize aliasing fact sections.
    - [x] Materialize ABI fact sections.
    - [x] Materialize layout fact sections.
    - [x] Materialize native metadata fact sections.
    - [x] Materialize generic-template sections without source reconstruction.
      - [x] Materialize generic-template function header rows and section child counts.
      - [x] Materialize generic-template typed-body statement/expression rows.
        - [x] Materialize typed-body statement/expression shape rows, parent links, and text-list rows with stack-constant worklists.
        - [x] Materialize typed-body type-reference payload rows and switch pattern/case rows.
          - [x] Materialize typed-body statement/expression type-reference links and expression type-argument rows.
          - [x] Materialize typed-body comptime value argument payload rows.
          - [x] Materialize typed-body switch case and pattern rows.
      - [x] Materialize generic-template deferred instantiation and operation child rows.
        - [x] Materialize generic-template deferred function/type instantiation rows and argument payload rows.
        - [x] Materialize generic-template operation child rows.
          - [x] Materialize local declaration, conversion, field access, and try propagation operation rows.
          - [x] Materialize object and enum construction/value/pattern rows with field/member payload rows.
          - [x] Materialize direct/member/function-address/bound-operation call-signature rows and nested operation payload rows.
            - [x] Materialize direct/member/function-address and bound-operation common call-signature rows with parameter, type-argument, comptime, parameter-group, dead-on-return, and bound call-argument payload rows.
            - [x] Materialize bound-operation non-call payload rows for receiver/function-pointer/closure/access/source/index/name/text/object/enum/interpolation/query/switch payloads.
    - [~] Port the package-image source bridge for Stage0/Stage1 compatibility.
      - [x] Establish the focused single-parse bridge module and render effective imports/re-exports, module identity plus effective module `[Backend(Opaque)]`, and type aliases with generic/comptime parameters.
      - [x] Render fact-preserving source-surface function declarations for the supported header subset, including ABI, opaque-backend, inline/hot/cold, unsafe/varargs/tail, raw-pointer count, and dead-on-return facts.
      - [x] Render callable type constraints, thread-law predicates, value contracts, and named/bounded disjoint/overlap/same parameter groups; reject count-only placeholder rows instead of dropping semantic, range, or alias facts.
      - [x] Render immutable and mutable source-surface static globals plus parse-only scalar/text/null/fixed-array global constants; keep the typed constant graph canonical for CTFE/LLVM and reject malformed or unsupported aggregate initializer shapes instead of substituting backend values.
      - [x] Render simple source-surface struct/record/trait declarations with generics, opaque/layout/pack/align attributes, field offsets, field visibility, and field types.
      - [x] Render record primary constructors, implemented-trait bases, associated aliases, dyn-trait identity, and type/field thread-safety law attributes without temporary name sets.
      - [x] Render struct/record constructors and destructors with authored bodies plus struct/record/trait method declarations with ABI, performance, generic, thread-law, value-contract, and alias-region facts; reject enum methods and malformed/count-only member payloads.
      - [x] Render source-surface enums with generic/comptime parameters, positional and named payloads, `[Ok]`/`[Err]` roles, and `from` error funnels while validating the funnel payload type exactly.
      - [x] Render doctrine declarations with generic parameters, associated aliases, and law/method headers while preserving dyn-trait object-safety rejection separately.
      - [~] Reconstruct source declarations and generic-template bodies into a Stage1 loaded source-document model.
        - [x] Own reconstructed source bytes beside Stage1 compilation-unit syntax tables, reject syntax diagnostics, and preserve `export import` identity in the parsed header. (2026-07-10: added the one-render/one-parse loaded compatibility document and extended the Stage1 header parser with an allocation-free exported-import flag column.)
        - [~] Connect renderable generic-template bodies to the matching function and method declarations, preferring typed template graphs and using legacy `BodyText` only when no typed body exists.
          - [x] Match legacy `BodyText` to top-level functions and methods by qualified name plus typed-interface published overload key, with canonical source-parameter fallback; reject duplicate template identities and never use legacy text when `TypedBody` is present. (2026-07-10: added one-parse effective compiler-section lookup, reusable overload-key scratch storage, Stage0-compatible qualifier canonicalization, exact body attachment, and optimized positive/ambiguity coverage.)
          - [~] Render the structured typed-template operation subset that still requires compatibility source text; keep declaration-only output for typed bodies fully consumed by direct imported-template lowering.
            - [x] Detect source-required operations through bounded typed statement/expression/pattern traversal and render nested direct calls, field accesses, non-generic member calls, and zero-argument object creations with exact authored `ExpressionText`. Preserve source-name/template-name/resolved-name precedence, member source-name recovery, recursive name/literal arguments, and reusable body scratch; reject duplicate ordinals and unsupported source-required shapes instead of dropping structured facts. (2026-07-10)
            - [x] Render ordinal-backed enum constructor/call/value expressions; named generic type references with ordered ownership/access/init qualifiers and symbolic/integer comptime arguments; generic/comptime direct and member calls; synthesized constructor, initializer, and arena object creation; and ordered empty/expression/assignment/break/continue/return statements. Direct calls omit pure inferred type arguments unless comptime arguments require explicit syntax, matching Stage0. (2026-07-10)
            - [x] Render typed local declarations and object-initializer expressions with exact storage, mutability, type, member-name, and initializer ordering. (2026-07-11)
            - [x] Render bounded typed `if`/`else` statement trees with recursive fact-driven bodies while rejecting unimplemented condition patterns. (2026-07-11)
            - [x] Render labeled typed `while` loops with loop behavior, ordered loop contracts, bounded recursive bodies, and labeled breaks/continues. (2026-07-11)
            - [x] Render typed `for`, traversal, block, and switch control flow plus condition/switch patterns from ordered published facts. (2026-07-11)
            - [x] Render scalar, container, associated, dyn-trait, function-pointer, and closure type references with qualifiers, ABI, bounded-pointer, and callable memory-contract facts. (2026-07-11)
            - [x] Render the remaining Stage0-compatible array/assignment/conversion/try/operator/conditional/comptime/layout/closure/index/dyn-trait expression forms. (2026-07-11)
            - [ ] Remove the Stage0 compatibility source bridge after direct imported-template lowering consumes every structured typed-body family.
      - [x] Materialize effective source-surface import and re-export rows for bridge-compatible module import rendering. (2026-07-09: added compact source-surface import summaries that prefer explicit `SourceSurface`, fall back to legacy module imports/re-exports, and fold duplicate re-exports into exported direct-import rows.)
      - [x] Materialize effective source-surface type-alias header and generic-parameter rows for bridge-compatible alias rendering. (2026-07-09: added compact source-surface alias summaries that prefer explicit `SourceSurface`, fall back to legacy module aliases, preserve source target spelling, count/validate comptime parameter rows, and expose ordinary generic parameter names.)
      - [x] Materialize effective source-surface type header and direct child-count rows for bridge-compatible source-only type rendering. (2026-07-09: added compact source-surface type summaries that prefer explicit `SourceSurface`, fall back to legacy module types, preserve source kind/layout spelling, validate generic/comptime generic rows, and expose fields, constructors, variants, methods, associated types, traits, thread-safety attributes, destructor, dyn-trait, and pack/align counts/flags.)
      - [x] Materialize effective source-surface type field rows for bridge-compatible source-only field rendering. (2026-07-09: added compact source-surface field summaries that preserve source field type spelling, optional visibility, explicit offset bytes, and thread-safety attribute counts while honoring explicit `SourceSurface` suppression of legacy fields.)
      - [x] Materialize effective source-surface type primary-constructor parameter rows for bridge-compatible record constructor rendering. (2026-07-09: added compact source-surface type constructor-parameter summaries that preserve source parameter name/type spelling, raw-pointer count expressions, and disjoint/const flags while honoring explicit `SourceSurface` suppression of legacy constructor parameters.)
      - [x] Materialize effective source-surface enum variant and payload rows for bridge-compatible enum rendering. (2026-07-09: added compact source-surface enum variant and payload summaries that preserve variant names, named-payload mode, role/absorbed-error metadata, payload names, positional empty-name payloads, and source payload type spelling.)
      - [x] Materialize effective source-surface global rows for bridge-compatible source-only global rendering. (2026-07-09: added compact source-surface global summaries that prefer explicit `SourceSurface`, fall back to legacy module globals, preserve source type spelling, validate mutability and initializer shape, and expose constant-initializer presence.)
      - [x] Materialize effective source-surface function header and parameter rows for bridge-compatible source-only function rendering. (2026-07-09: added compact source-surface function summaries that prefer explicit `SourceSurface`, fall back to legacy module functions, preserve source return/parameter type spelling, count memory-contract child groups, and expose ABI/performance flags plus parameter raw-count expressions.)
    - [x] Validate package stage compatibility. (2026-07-09: added an O(1) compact-fact stage compatibility helper that accepts validated logical package images only for the currently supported `stage0` build stage and rejects reserved future stages.)
    - [x] Validate package profile compatibility. (2026-07-08: added compact-fact profile matching over `PINF`/`STRS` without decoding `MANF`.)
    - [x] Validate package target compatibility. (2026-07-08: added compact-fact target triple/data-layout matching over `PINF`/`STRS`.)
    - [x] Validate package backend fact compatibility. (2026-07-08: added compact-fact CPU/relocation/code-model, feature, C data model, and aggregate pointer layout matching.)
    - [x] Validate package version compatibility. (2026-07-08: added compact header-version read/match helpers and rejection coverage for unsupported logical package versions before `PINF`/`STRS` facts load.)
  - [x] Port logical package models.
    - [x] Add compact self-host logical manifest model rows for decoded `MANF` manifest/module summaries. (2026-07-09: added a single-parse model builder that validates root/library/profile/target/backend facts once, reserves ordered module-row storage up front, and exposes O(1) ordinal access for package import handoff.)
    - [x] Add typed-interface, source-surface, compiler-fact, and generic-template graph ownership into the top-level logical package model.
      - [x] Own module-level function-effect fact rows and generic-template function rows from effective compiler sections in the logical manifest model. (2026-07-09: added per-module graph rows beside manifest module summaries, preserving effective `CompilerSections` precedence and exposing borrowed graph/count access without reparsing `MANF`.)
      - [x] Own remaining compiler-fact graph families in the logical manifest model. (2026-07-09: added model-owned ABI, layout, native metadata, function semantic, and function ownership graphs with optional section probes, single-parse materialization, and module-level count/availability accessors.)
      - [x] Own typed-interface declaration/callable/global/type graph families in the logical manifest model. (2026-07-09: added model-owned typed alias, callable, global, and type graphs from effective `TypedInterface` sections, including nested method callable facts, single-parse materialization, and module-level count/availability accessors.)
      - [x] Own source-surface bridge graph families in the logical manifest model. (2026-07-09: added a model-owned source-surface bridge graph for import, type-alias, type, field, constructor-parameter, enum-variant, payload, global, function, and function-parameter summaries, preserving explicit `SourceSurface` shadowing over legacy module arrays with single-parse materialization and module-level availability/count accessors.)
  - [x] Port logical package builders.
    - [x] Add a compact logical STRS/PINF/MANF writer for already-encoded manifest payloads. (2026-07-09: added a deduplicating string-table builder, package-fact writer, section-directory wrapper, and focused round-trip fact for profile/target/C-data/aggregate facts.)
    - [x] Port source-surface, typed-interface, compiler-fact, and generic-template manifest builders from self-host compiler artifacts.
      - [x] Add source-surface import/re-export manifest JSON builder rows. (2026-07-09: added compact append-only import/re-export builder rows with per-module first/last/count links, JSON emission under `SourceSurface`, and readback coverage for direct, re-export, and merged effective imports.)
      - [x] Add source-surface type-alias/type/global/function manifest builders.
        - [x] Add source-surface type-alias manifest JSON builder rows. (2026-07-09: added compact alias rows with per-module first/last/count links, per-alias generic/comptime parameter child links, JSON emission under `SourceSurface.TypeAliases`, and readback coverage for alias header text and generic parameter order/counts.)
        - [x] Add source-surface type manifest builders. (2026-07-09: added compact type rows with per-module first/last/count links, child links for fields, generic/comptime parameters, primary constructor parameters, enum variants/payloads, and implemented traits, plus layout/backend/destructor/dyn-trait scalar facts emitted under `SourceSurface.Types` with focused readback coverage.)
        - [x] Add source-surface global manifest builders. (2026-07-09: added compact global rows with per-module first/last/count links, JSON emission under `SourceSurface.Globals`, required mutability facts, optional constant-initializer presence, and readback coverage for global header text/order.)
        - [x] Add source-surface function manifest builders. (2026-07-09: added compact function rows with per-module first/last/count links, per-function parameter/generic/comptime/dead-parameter child links, optional ABI/performance/inline/link/asm flags, count-preserving contract/group arrays, JSON emission under `SourceSurface.Functions`, and readback coverage for function header, parameter, and backend fact text/order.)
      - [x] Add typed-interface manifest builders.
        - [x] Add typed-interface type-alias manifest JSON builder rows. (2026-07-09: added compact resolved alias rows with per-module first/last/count links, per-alias generic/comptime parameter child links, required typed-interface array shell emission, and readback coverage for alias headers plus target/comptime typed type-reference facts.)
        - [x] Add typed-interface function manifest builders. (2026-07-09: added compact resolved function rows with per-module first/last/count links, child links for typed parameters, generic/comptime parameters, and dead-on-return names, plus lossless ABI/performance/body/header flags emitted under `TypedInterface.Functions` with focused declaration/callable/type-reference readback coverage.)
        - [x] Add typed-interface global manifest builders. (2026-07-09: added compact resolved global rows with per-module first/last/count links, required typed global type references, scalar constant-initializer payload/type facts, `TypedInterface.Globals` emission, and focused declaration/type-reference/fact-graph readback coverage.)
        - [x] Add typed-interface type manifest builders. (2026-07-09: added compact resolved type rows with per-module first/last/count links, child links for fields, generic/comptime parameters, associated types, enum variants/payloads, implemented trait names/types, and thread-safety law attributes, plus backend/layout/dyn-trait scalar facts emitted under `TypedInterface.Types` with focused declaration/type-reference/fact-graph readback coverage.)
      - [x] Add compiler-fact manifest builders.
        - [x] Add function-effect fact manifest builders. (2026-07-09: added compact function-effect builder rows with per-module first/last/count links, JSON emission under `CompilerFacts.FunctionEffects`, optional FFI ABI/backend-mode facts, and focused readback coverage through the existing compiler-fact materializer.)
        - [x] Add ABI function fact manifest builders. (2026-07-09: added compact ABI function/parameter/carrier rows with per-module and dense child links, JSON emission under `CompilerFacts.AbiFunctions`, optional FFI/calling/link metadata, scalar type-reference bit-width/range facts, and focused readback coverage through the existing ABI fact materializer.)
        - [x] Add concrete layout/native metadata fact manifest builders. (2026-07-09: added compact concrete-layout/field builder rows, root-level native dependency rows grouped by dependency kind, per-module linkage rows with dense defined/referenced symbol child links, JSON emission for `CompilerFacts.ConcreteLayouts`, root `NativeDependencies`, and `CompilerFacts.Linkage`, plus focused readback coverage through existing layout/native materializers.)
        - [x] Add function semantic/ownership fact manifest builders.
          - [x] Add function semantic fact manifest builders. (2026-07-09: added compact function-semantic rows with per-module first/last/count links, dense child rows for called functions, parameters, initialization ranges, calls, and call arguments, JSON emission under `CompilerFacts.FunctionSemantics`, explicit memory-effect/alias/dereferenceable/alignment facts, and focused readback coverage through the existing function semantic materializer.)
          - [x] Add function ownership fact manifest builders. (2026-07-09: added compact ownership name/event/projection/root builder rows under function-semantic rows, JSON emission under `CompilerFacts.FunctionSemantics[*].Ownership`, preserved location/index-projection/root availability/type-reference facts, and focused readback coverage through the existing ownership fact materializer.)
      - [x] Add generic-template manifest builders.
        - [x] Add generic-template function header manifest builders. (2026-07-09: added compact per-module function-template rows with required resolved/name/overload keys, optional body/backend/cost/scalar section facts, `CompilerSections.GenericTemplates.Functions` JSON emission, and focused readback coverage through the existing generic-template fact materializer.)
        - [x] Add generic-template typed-body statement/expression manifest builders.
          - [x] Add generic-template top-level typed-body statement and root expression manifest builders. (2026-07-09: added compact append-only typed-body statement/expression rows, per-template top-level statement links, scalar statement/expression setters, JSON emission under `TypedBody.Statements`, integer type-reference facts, and focused generic-template readback coverage.)
          - [x] Add generic-template nested statement blocks, switch cases, patterns, expression child lists, and expression argument payload builders.
            - [x] Add generic-template expression child-list and expression argument payload builders. (2026-07-09: added compact linked expression argument rows plus member/operator/type/comptime payload builders, JSON emission for `Arguments`, `MemberNames`, `OperatorNames`, `TypeArguments`, and `ComptimeValueArguments`, and focused generic-template readback coverage for child ordinals and payload type-reference sources.)
            - [x] Add generic-template nested statement blocks, switch cases, and patterns. (2026-07-09: added compact nested statement, switch-case, and pattern builder rows with O(1) append links, JSON emission for `SwitchCases`, statement block arrays, and pattern members/condition patterns, plus focused generic-template readback coverage for parent kinds, ordinals, and backend-visible expression/type facts.)
        - [x] Add generic-template deferred-instantiation and operation manifest builders.
          - [x] Add generic-template deferred function/type instantiation manifest builders. (2026-07-09: added compact deferred function/type instantiation rows under generic-template function rows, dense type/comptime child argument links, JSON emission for `DeferredFunctionInstantiations` and `DeferredTypeInstantiations`, and focused readback coverage for preserved type-reference/text facts.)
          - [x] Add generic-template object, enum, call, and bound-operation manifest builders.
            - [x] Add generic-template bound-operation call/signature/payload manifest builders. (2026-07-09: added compact bound-operation rows under function-template rows, embedded call-signature/parameter/type-argument/comptime-argument rows, call-argument rows, type/text/u32/bool payload rows, and JSON emission through the existing generic-template materializer.)
            - [x] Add generic-template top-level object, enum, and call manifest builders. (2026-07-09: added compact object creation, enum constructor/call/value/pattern, and direct/member/function-address call rows linked from generic-template function rows, JSON emission through the existing materializer shape, and focused readback coverage for preserved type/text facts.)
            - [x] Add generic-template object/enum-specific constructor, initializer, member, and field-index payload builders. (2026-07-09: added compact object constructor metadata plus object initializer, enum constructor member, and enum pattern member rows with field-index/type facts, JSON emission through existing materializer keys, and focused readback coverage.)
    - [x] Add logical manifest JSON encoding and compression before binary package-image writeout.
      - [x] Add uncompressed logical manifest JSON payload encoding for package identity, build profile, target/backend facts, and module shell rows. (2026-07-09: added a JsonWriter-backed manifest shell builder, root-module validation against module rows, and a payload encoder that copies fresh JSON bytes into MANF input buffers.)
      - [x] Add Brotli compression for logical manifest JSON payloads before MANF writeout. (2026-07-09: added a dependency-free Brotli stream writer that wraps manifest JSON bytes in standards-compliant uncompressed meta-blocks plus a final empty block, and added focused payload/image writeout facts.)
  - [x] Port shared package codecs.
    - [x] Share sectioned package-image header and directory-length helpers across logical and MIR sectioned writers. (2026-07-09: added shared v2 header/directory constants, a common sectioned-header writer, and final-capacity reservation for MIR sectioned package-image writers so repeated byte appends do not drive avoidable reallocations.)
    - [x] Move reusable section-directory reader/writer helpers into the shared package codec module after package-section IDs are split away from `PackageImage.stark`. (2026-07-09: moved the STARKPKG magic/version, section IDs, flags/encodings, directory length/data-offset helpers, sectioned-header writer, directory-entry writer, and package-image capacity reservation into `Compiler.Mir.PackageCodec`.)
  - [x] Port deterministic package inspection rendering. (2026-07-09: verified the self-host package-image renderer covers legacy MIR, sectioned MIR, and logical `STRS`/`PINF`/`MANF` images through deterministic text and JSON entry points.)
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

- [ ] Root-module pipeline incrementality: the dependency LLVM cache (docs/Self-host-Prep/DevVelocity.md §1.4, `DependencyLlvmCache`) removes per-dependency pipeline re-runs, but a package build's root module (the entire selfhost library) still runs the full pipeline on every build; per-function or per-module caching inside the root pipeline is the remaining lever. A 2026-07-12 cold filtered `selfhost.Ir` build after the source-lowering splits took more than 65 minutes before executing one fact, with individual imported `lower-mir` stages reported up to 1,564.9 seconds; the identical unchanged rerun skipped rebuilding and completed in 0.2 seconds. The earlier ~20-minute estimate is no longer representative of a cold rebuild at the current compiler size.
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
  - [~] Implement the full compiler package-image logical section model and binary loader.
    - [x] Validate and inspect the host logical `STRS`/`PINF`/`MANF` section wrapper in self-host code.
    - [x] Preserve and inspect logical package identity/profile/target facts from `PINF`/`STRS`.
    - [x] Preserve and inspect logical target backend facts from `PINF` without materializing `MANF`.
    - [~] Decode `MANF` and materialize logical package-image facts without source reconstruction.
      - [x] Decode the uncompressed Brotli stream emitted by Stage1 and preserve exact JSON payload bytes.
      - [ ] Decode general Stage0 Brotli streams directly in Stage1.
  - [x] Add `stark inspect-pkg` as a top-level compiler command.
  - [x] Update package-image docs and tests after public spelling lands.
  - [x] Add per-fact test progress streaming to `stark test` (2026-07-03): the driver streams runner output line-by-line (never buffers until exit); `--test-progress` passes `--progress` to the generated runner, which prints `run <name>` markers and `ok|FAILED <name> (k/N)` counters via the new `System.Testing.BeginFact`/`RunFactCounted`, and the driver stamps `[elapsed]` prefixes; `--test-timeout <seconds>` kills the process tree and reports the in-flight fact. Default output stays byte-identical.
  - [x] Preserve the test-runner progress protocol in the stage1 `stark test` port (2026-07-03): the protocol components are ported and golden-parity verified — `Compiler.TestRunner` emits the generated runner byte-identically to stage0, and `Compiler.TestDriver` reproduces the streaming/prefix/timeout contract against the normative `tests/fixtures/test-progress` goldens (pinned by `tests-stark/selfhost.TestRunner`, 4/4). The eventual stage1 CLI port wires these components into project discovery/build orchestration (tracked with the CLI port items; see docs/Self-host-Prep/30-test-progress-streaming.md).
  - [x] Smooth the package-consumer edges found by the probe-recipe work (2026-07-01):
    - [x] Raw `--target` invocations derive a different LLVM data layout than project builds embed, so consuming a project-built package requires copying `--target-data-layout` from `--inspect-pkg` by hand; derive the same layout by default for a bare `--target` triple. (2026-07-09: direct CLI target resolution now enriches an explicit local target with the detected toolchain data layout when no override is supplied.)
    - [x] Search-dir resolution enumerates `*.starkpkg` recursively, so a package image left under a source search root (e.g. `selfhost/build/` under `-I selfhost`) poisons source-only compiles with STK7312; scope package discovery away from source roots or prefer fresh source over images outside the project driver. Worse, the root file's own directory joins the search, so a stale test-project package (e.g. `tests-stark/selfhost.Ir/build/.../libSystem.starkpkg`) silently SHADOWS fresh stdlib source in raw `--check` runs — observed 2026-07-01 as phantom STK4107 kind errors against pre-fix `List.Get`; the raw CLI has no freshness stamps, only the project driver does. Delete the stale image (build output) to unblock. (2026-07-09: direct resolver selection is now source-first across all search roots before building the recursive package-image index, so package images are used only when no source module exists.)
    - [x] A library package records its static library by relative path (`../../bin/...`), so relocated packages must replicate the `stage0/pkg` + `stage0/bin` layout; consider embedding a layout-independent reference. (2026-07-09: `--emit-lib` now stages a copy of the static archive beside the package image and records only that archive file name, so a package directory relocates as a self-contained pair.)

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

- [~] Complete release packaging, `stark doctor`, and clean-machine archive verification.
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
  - [ ] Complete the relocatable SDK, official bundled-library resolution, and
    real vendor-package release gates tracked in
    [31-relocatable-sdk-and-bundled-vendor-resolution.md](31-relocatable-sdk-and-bundled-vendor-resolution.md).

- [ ] Sync editor syntax and completions with the self-hosting language surface.
  - [ ] Update grammar-derived syntax highlighting, completions, snippets, and stdlib symbol data.
  - [ ] Verify coverage against the canonical language surface after parser/selfhost syntax changes land.

---

## 4. Standard Library And Porting APIs

- [x] Stdlib surface for the stage1 `stark test` driver (T30.7 audit +
  implementation, 2026-07-03; see
  docs/Self-host-Prep/30-test-progress-streaming.md §5.2). Landed in
  `System.Process`: `ChildProcess` (public spawned-child handle),
  `Spawn` (piped stdout/stderr, child in its own process group),
  `ReadStdoutChunk`/`ReadStderrChunk` (blocking appends, Ok(0)=EOF),
  `TryWaitExit`/`WaitExit`, `KillTree` (group kill), `Close`, and public
  `MonotonicMilliseconds`. Platform layer: `StartProcessCaptureGrouped` +
  `KillProcessGroup` through the full 4-dispatch + 3-backend fan-out
  (macOS: `posix_spawnattr` + `POSIX_SPAWN_SETPGROUP`, and `posix_spawnp`'s
  attributes parameter corrected to the pointer-to-pointer shape; Linux:
  child-side `setpgid(0,0)` in the fork/exec path; Windows: grouped spawn
  falls back to plain spawn and `KillProcessGroup` to single-process
  terminate — job-object tree kill is a follow-up). Runtime-verified on
  macOS via probe: spawn+stream+wait `/bin/echo` (exit 0, 9 bytes),
  `KillTree` on a `sh -c 'sleep 60'` group with clean reap and no orphaned
  `sleep`, monotonic clock sane. The deadline-wait piece stays composed in
  the driver from `TryWaitExit` + the clock (no extra stdlib surface
  needed). Regression: `SystemProcessStandardLibraryTests` fails 5/7
  IDENTICALLY at unmodified HEAD (shared-stdlib fixture STK7312 target
  mismatch — pre-existing environment issue, same family as the
  PackageImage unit-test failures).

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

- [x] Fixed (2026-07-09): the AbiLowering dotted-name module-prefix change
  (bbfd7858, `ComputeSymbolName`'s dotted fallthrough) fixed
  manifest-imported method symbols (`@Facade_Counter_Reset`) but regressed
  LOCAL root-module methods: `Box.Read` in module Demo emitted
  `@Demo_Box_Read` instead of `@Box_Read`, breaking 3 emission tests
  (`ValueReceiverMethodsLowerToDirectAggregateCalls`,
  `DoctrineLawCallsEmitDirectReadonlyNoCaptureSignatures`,
  `BorrowReceiverMethodsLowerToPointerReceiverCalls`) and the source-import
  link scenario. The ABI symbol gate now prefixes dotted method/doctrine names
  only when the resolved function identity's module differs from the module
  currently being compiled; local root-module methods stay module-relative,
  while source-imported methods still call the defining module's qualified
  symbol (covered by `ImportedSourceMethodsUseDefiningModuleQualifiedSymbols`
  plus a focused source-import `--emit-exe` smoke).

- [x] Fixed (2026-07-09): three C# host test expectations were stale, all
  verified pre-existing on an unmodified tree at 7cb7be18:
  `ImportedSourceAsmFunctionsEmitExternalDeclarationsAndCalls` and
  `BoundedRawPointerArgumentFactsStrengthenDirectAndIndirectCallAttributes`
  expect call lines without the `noundef` call-site attributes the emitter
  now produces, and the two
  `MultiFileIntegrationTests.SystemTextSourceModuleSupports...` tests
  assert empty stderr while a warning diagnostic is now emitted for their
  fixtures. Classified as intent-preserving assertion re-aims: the LLVM tests
  now assert the stronger range/readonly/count facts at the call sites, and
  the copied `System.Text.stark` executable tests assert the specific STK4122
  recursive-call warning family plus the clean `0 errors, 6 warnings` summary
  instead of requiring empty compiler stderr.

- [x] Fixed/revalidated (2026-07-10): `List<Compiler.Mir.EnumLayout.MirEnumLayoutFact>`
  lowers to an EMPTY LLVM struct in from-source compiles of the selfhost
  graph (`%System_Collections_List_..._MirEnumLayoutFact_ = type { }`)
  while sibling `List<T>` instantiations are correct
  (`type { { ptr, i64, i64 } }`). Zero fields → size 0, which is the single
  root cause behind BOTH the ~460 `dereferenceable(0)` receiver sites on
  `IrTable<MirEnumLayoutFact>.Add` (the emitter guard landed 2026-07-07
  suppresses the invalid attribute) AND `error: invalid getelementptr
  indices` on `getelementptr %List<...>, ptr, i32 0, i32 0` in
  borrow-extract paths, which currently blocks `EnumReturnProbe` (and any
  root module that inlines these accessors) from compiling from source.
  Suspect: generic-instantiation field materialization fails when the type
  argument lives in a nested submodule (`Compiler.Mir.EnumLayout`), same
  family as the O3-refactor's nested-module qualified-name splits. Repro:
  `compiler selfhost/probe/EnumReturnProbe.stark --emit-llvm -I selfhost
  -I stdlib/src --no-stark-path` from a package-free cwd, then grep the
  emitted module for `MirEnumLayoutFact_ = type`.
  Narrowing note (2026-07-09): isolated source repros for
  `System.Collections.List<Lib.Nested.Payload>` through an exported generic
  wrapper and for `IrTable<MirEnumLayoutFact>` both emit non-empty list
  layouts (`type { { ptr, i64, i64 } }`); the selfhost probe also emits no
  `dereferenceable(0)`. The full `Compiler.Mir` facade repro remains the only
  unconfirmed failing shape; a focused `EnumReturnProbe` run was stopped after
  `lower-mir` reached 220.3s to avoid a broad/stalling verification pass.
  The full package-free `Compiler.Mir` facade repro completed on 2026-07-10:
  `%System_Collections_List_Compiler_Mir_EnumLayout_MirEnumLayoutFact_` emitted
  as `{ { ptr, i64, i64 } }`, no `dereferenceable(0)` facts remained, and Clang
  accepted the 14 MiB LLVM module.

- [x] Fixed (2026-07-08): deferred aggregate insert over a branch join emitted
  invalid LLVM (`use of undefined value '%vN'`), breaking every selfhost
  package/from-source build once PackageImage grew the shape (repro:
  `LogicalPackageAbiFactGraphAddParameterRow` — a large by-value row copied
  from a table, field-updated differently in two branches, updated again
  after the join, then passed by value to `Replace`). Root cause: the
  emitter defers large `SsaInsertFieldRValue` materializations on the
  promise that consumers rebuild the value through
  `TryEmitStructuredAggregateStore`, but that walk had no case for a phi
  base (phis live in `_phisByResultName`, not `_valueDefinitions`), so the
  structured store failed and the generic fallback referenced the
  never-emitted insert register. The first fix's defensive backstop then
  surfaced four more latent instances in committed SourceSwitchLowering
  functions (e.g. `TryParseFixedArrayFieldListSwitchAssignmentCases`,
  `TryParseStructAggregateSwitchAssignmentCases`) with a second failure
  mode: chains whose `LoadLocal` base fails the emit-time
  forwarding/lifetime check at the consumer — there the slot no longer
  holds the loaded bytes, so address reconstruction would be WRONG and the
  register chain is required. Fix (LlvmFunctionBodyEmitter.Aggregates):
  structured stores write phi'd aggregate bases directly from their
  registers and slot-backed bases via address copy; the deferral decision
  only fires when the insert chain's base is verifiably reconstructible;
  and when a consumer still needs the register form, the skipped insert
  chain is re-emitted on demand into fresh temps at that consumer
  (`TryMaterializeDeferredAggregateValue` — operands sit at their SSA
  definition points, so dominance holds), with a hard
  `UnsupportedBodyEmissionException` instead of a dangling reference if
  even that fails. Regression: `PhiAggregateInsertEmissionTests`
  (undefined-register scan + LLVM verifier round-trip).

- [x] Fixed (2026-07-03): package-imported struct holding a `List<T>` field
  broke consumer drop lowering (found by the stage1 test-runner port; repro:
  `RowPlan { List<NamedRow> Rows; }` imported from a package — a consumer
  that merely holds and drops one crashed `lower-mir`, while a
  consumer-local `List<NamedRow>` worked). Root cause: package images
  publish no parse tree, and `MaterializeImportedSourceInstantiations`
  explicitly skipped package-image modules — so generic instantiations
  nested in imported field declarations were never materialized: no
  registered instantiated named type, no type trigger, no monomorphization
  plan entry, no concrete layout, and no LLVM struct-type definition
  (`EmitNamedTypeDefinitions` emits only from `typeModel.NamedTypes`). The
  drop glue then inline-lowered `List<T>.Drop` against an unregistered
  instantiation and failed member resolution. Fix (three pieces, all
  landed):
  1. `MaterializeImportedSourceInstantiations` (TypeChecking.cs) now walks
     package-image modules' published concrete struct/enum field and variant
     types through `EnsureMonomorphizedType`, mirroring the source-import
     walk — this is the load-bearing fix.
  2. `TryBuildMemberCall`/`TryBuildMemberCallStatement` gained the
     `GetGenericBaseName` receiver fallback that the destructor/enum-layout
     lookups already used (defensive; matches existing convention).
  3. A latent secondary defect was characterized along the way: destructor-
     trigger-derived specializations for UNREGISTERED instantiations emitted
     open receivers (opaque `ptr`, `dereferenceable(8)`, invalid GEPs) —
     with (1) in place the instantiations are always registered before
     specialization, so the broken combination can no longer occur.
  Validation: `build/pkgbug` repro prints `ok plan`;
  `tests-stark/selfhost.TestRunner` 3/3 green (package-backed emission
  byte-identical to stage0); 21 generator/emission unit tests green;
  allocator wrong-code repros still green; the 7 failing
  `FullyQualifiedName~PackageImage` unit tests fail identically at
  unmodified HEAD (pre-existing, tracked separately). Still open from this
  investigation: the template-coordinate diagnostics leak (crash locations
  like `<root>:1986:19` stamp template-body coordinates onto the root file
  path).

- [x] Fixed (2026-07-03): stale dynamic `Length` after a mutating loop.
  Root cause was ORDER-DEPENDENT FACT RECORDING in
  `SsaValueFactAnalyzer.RefineDynamicStorageLocalFacts`: consumer-visible
  per-value facts (e.g. a dynamic's length range) were recorded into
  `values` DURING the entry-state fixpoint — a loop exit block processed on
  the first iteration recorded the not-yet-joined pre-loop state (length 0
  from `new()`), and nothing retracted it after the back edge merged in.
  The emitted IR then carried `range(i64 0, 1)` on the loaded length and
  the `!= 9` branch was folded at emission (the runtime value stayed
  correct — hence the interleaved-read "heisenbug" mask). Statically
  visible in the UNOPTIMIZED emitted module; unaffected by any
  `STARK_SKIP_PASSES` combination because the analyzer itself was the
  source. Fix: the fixpoint now runs against a scratch copy of the value
  facts and records once from the CONVERGED entry states. Verified: the
  hermetic `stale_min` repro and the original spawn-probe branch both
  correct; v_cross/v_ownedascii/pkgbug/bundle_field2 repros green; 21
  generator/emission unit tests green; full `LlvmIrEmissionTests` 455/458
  with the 3 failures byte-identical at unmodified HEAD (pre-existing).
  NOTE: this did NOT fix the bundle heisenbug below (verified with a clean
  probe rebuild — an earlier apparent fix was a stale bisect-era binary);
  given this bug's shape, the bundle bug's next suspect is the analogous
  order-dependence in memory-opt-ssa's single-predecessor known-state
  propagation (`TryGetSinglePredecessorExitKnownLocals` runs one in-order
  pass with no fixpoint over loop back edges).

- [x] Closed (2026-07-04): bundle field-store elimination heisenbug.
  `BuildSourceModuleLoweringFacts` lost `built.Declarations`/
  `built.EnumPayloads` (bundle fields 0/1) in package-backed probes. Root
  cause was `SsaOwnershipTrafficOptimizer`'s dead-aggregate-copy
  elimination treating a copy into a FIELD as a whole-local kill (a later
  sibling-field copy killed the aggregate's liveness and earlier sibling
  copies died as "dead"); fixed 2026-07-03 with a whole-local-only resolver
  (`TryResolveWholeLocalAddress`) at both kill sites. The suspected
  "remaining layer" (stock LLVM `opt -O3` removing the 144/576-byte field
  copies from correct per-module IR) was a copy-COUNT phantom: the
  store-level semantic check shows SROA scalarizes the bundle and stores
  every `slot_built` value directly into `%arg_moduleFacts` on the success
  path — legitimate forwarding, not deletion (which is also why
  single-category attribute strips could never "preserve" the memcpys).
  The post-fix "runtime probe still loses fields" observation was a
  stale-artifact ghost: the loss is baked into `libStarkCompiler.a` at
  PACKAGE-build time, and the verification rebuilt only the probe (~15 s
  path), which links the old archive — a probe-only rebuild can never see
  a compiler fix. Rule recorded in the Internals probe recipe: after any
  compiler or selfhost change, rebuild the PACKAGE before probing.
  Verified 2026-07-04 against a fresh de-instrumented package (clean-room:
  package-free CWD, single package candidate): MemberFactsProbe 19/19 and
  NestedChainFactsProbe/NestedChainProbe green with zero crumb reads — the
  copies survive without instrumentation keeping them alive. The temporary
  tooling (`STARK_SKIP_PASSES` gate, per-pass SSA dump hooks, ECM/WALK
  emit traces, MFDBG/ENDBG/ECDBG crumbs) is removed. IMPORTANT RETRACTION
  discovered during that re-verification: the enum-return slice's prior
  evidence (`ok enum-return-only` whenever the copies survive) was
  accept-plus-INVALID-emission — dumping the accepted module shows
  `define unknown @main()` / `ret unknown` (the enum return-type mapping
  was never implemented; the probe checked only the boolean). The
  crumb-free package rejects the shape instead, which is the safe behavior
  of the same unfinished path. #34 is therefore still open work (tracked
  in §1 under enum payload lowering), no longer masked by this bug; its
  probe (`selfhost/probe/EnumReturnProbe.stark`) pins the shapes as
  expect-reject with a construction-control check, and must validate the
  emitted module (not just the accept boolean) when the slice lands. Probe recipe caveat (still true): the selfhost package
  embeds `System.Process`/Platform modules (via `Compiler.TestDriver`), so
  package-backed probes must NOT also pass `-I stdlib/src` — compile
  probes with `-I <pkg>` only.

- [x] Fixed (2026-07-03): wrong-code from LLVM allocator attributes on
  visible allocator bodies. The emitter attached `allockind`/`allocsize`/
  `alloc-family` to `__stark_runtime_(try_)alloc`/`(try_)realloc`/`free`,
  `__stark_heap_alloc/free`, and `__stark_arena_alloc` while also emitting
  their bucket/bump bodies `linkonce_odr` into every module; the bodies read
  the allocation header at `ptr - 24`, which is out of bounds of the abstract
  fresh-object model the attributes assert, so whole-program O3 proved UB on
  every successful allocation that later flowed into realloc/free and deleted
  the path (`llvm.assume(alloc == null)`, silently lost appends, spurious
  SIGTRAPs — e.g. `OwnedAscii` dropping its first `AppendConstAscii` once a
  later append crossed capacity, and `dynamic` list contents vanishing).
  Reproduced and bisected with stock `llvm-link` + `opt -O3` on the
  `--save-temps` modules; stripping only those attributes fixed the runtime
  behavior with no other change. Attributes remain on opaque libc/Win32
  declarations and the model-consistent `__stark_os_*` wrappers (see the
  policy comment in `LlvmBuiltinAndHelperEmitter.cs`). Every prior probe or
  test observation made through binaries that grew dynamic storage is suspect
  and needs re-verification (tracked per item).

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


## 10. Bootstrap Completion And Self-Hosted Release Adoption

- [x] Retain the C# Stage0 compiler in `/src` and the Stark compiler in `/selfhost`; M6/M7 do not rename source directories or remove the Stage0 implementation.
- [ ] Build the Stage1 Stark compiler with Stage0 and emit the expected compiler executable.
- [ ] Build the Stage2 Stark compiler with Stage1 and emit the expected compiler executable.
- [ ] Compare Stage1 and Stage2 package images, diagnostics, artifacts, and executable behavior.
- [ ] Run compiler benchmarks for Stage0, Stage1, and Stage2.
- [ ] Address bootstrap-only divergences discovered by Stage1/Stage2 comparison.
- [ ] Re-establish or update release builds to publish the qualified self-hosted compiler, stdlib package, and native tooling while keeping Stage0 explicitly buildable.
- [ ] Add release and CI smoke coverage for explicit compiler-stage selection so published artifacts use the self-hosted compiler without silently disabling Stage0 maintenance builds.
- [ ] Migrate durable Self-host-Prep content to Userfacing/Internals and retire this folder: fold the surviving parts of the numbered companion documents, TASKS.md, and TestPassLedger.md into docs/Userfacing or docs/Internals (append to existing documents where one fits), then delete the Self-host-Prep documents. (The pain-points tracker was retired 2026-07-01 after its durable rationale — the widened `--check`, `overlap_all`, the module-facts bundle, the probe recipe — landed in Userfacing/Internals; its resolution log survives as the 2026-07-01 pain-point-fixes entry in TestPassLedger.md.)
