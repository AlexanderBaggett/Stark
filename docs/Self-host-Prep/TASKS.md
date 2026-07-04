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
  - [ ] Record the target `Compiler.Typing.*` module map from the stage0 C# counterpart files.
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
    - [ ] Move remaining `System.Compiler` structural-fact typing hooks into CTFE typing modules.
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
  - [x] Move value-range transfer, branch refinement, and returned-value validation helpers into a MIR facts module.
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
  - [x] Move assembly metadata collection into an assembly metadata module.
  - [x] Move clang verification and temporary-file helpers into a test-support or compiler harness module.
  - [x] Add dependency-direction checks so MIR core does not import parsing, LLVM emission, package images, or test helpers.
  - [x] Run focused behavior-preserving tests after each module split.
    - [x] Run focused lower-MIR checks for the source-expression and source-local lowering splits.
    - [x] Run focused lower-MIR checks and selected if facts for the source function-context and source if-lowering splits.
    - [x] Run focused lower-MIR checks for the source switch-lowering and source loop-lowering splits.
    - [x] Run focused lower-MIR and API re-export checks for the source module-lowering split.

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
  - [~] Lower literals.
    - [x] Lower integer and boolean literals to typed MIR constants with exact range facts.
    - [x] Validate literal fact rows before emitting MIR constants.
    - [x] Reject unsupported literal families before emitting partial MIR.
    - [ ] Lower character and text literals through the MIR constant storage model.
    - [ ] Lower floating-point literals once MIR has typed float constants.
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
  - [~] Lower arithmetic and comparisons.
    - [x] Lower typed integer add/sub/mul and signed comparisons with recomputed value facts.
      - [x] Validate carried operand facts before interpreting binary operation facts.
      - [x] Preserve compact integer widths through inferred binary-expression and comparison operand lowering.
    - [x] Lower integer division, remainder, bitwise, and shift operators with checked backend facts.
    - [x] Lower wrapping and saturating arithmetic operators with explicit overflow semantics.
    - [ ] Lower floating-point arithmetic and comparisons after MIR has typed float operations.
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
      - [ ] Fix constructed-object field try-assignment lowering through the single-function entry (discovered 2026-07-03, HEAD-worktree verified pre-existing: `box.value = try f()` rejects for every field width even though the body matcher fires and the module-path slices landed the machinery; the IrTests facts asserting these shapes have never executed green — root-cause with the probe recipe as its own slice).
      - [x] Fix if-condition member reads in the single-function dialect paths (fixed 2026-07-02; three stacked causes: `CompileFunctionWithLocalsToLlvm` never dispatched to the terminal-if lowerings at all — wired in `LowerModuleLocalIfReturnFunctionToBlocks` with the real module facts, which alone fixed bare-bool param conditions; the typing-side statement walker only extracted parenthesized if/while conditions, so paren-free dialect conditions (and real-Stark `while willexit (…)` conditions) had no typed member rows — the extraction now skips the `willexit` marker and the optional paren; and the driver's shared tail emitted a linear instruction range that truncated multi-block bodies mid-CFG — block-shaped bodies now record `BlockEmit` ranges and emit through `EmitLlvmBlocksWithRangeFactsCoreWithEnumLayouts`. Heap and stack bool member conditions branch correctly on the loaded flag; all probe batteries green).
      - [x] Fix `var` locals initialized from member field reads (fixed 2026-07-02; two stacked root causes: the statement-kind classifier in Parsing.stark mapped every storage class to StatementKind.Local except `var`, so var initializers never entered the expression table and typing produced no member rows for them — one classifier case fixes the facts; and the single-function driver batched locals before mutation replay, so accepting interleaved statements required making `storageMutationStatements` the ordered statement timeline with LocalDecl rows — a local's override and initializers now lower at their source position, which also fixes stored-scalar initializers that read fields after mutations. Probes: var-from-field, var-from-ranged-field, and both var-indexed bounds-proof shapes flipped to passing; emitted LLVM verified load-after-store with !range-carried bounds).
      - [~] Lower typed HIR member path rows through shared storage-place addressing.
        - [x] Route constructed-object field reads and address-taking through a shared storage-place address helper.
        - [x] Route constructed-object field assignments through the shared storage-place address helper.
        - [x] Route direct and indexed constructed-object field parsing through the member-chain resolver.
        - [x] Import typed HIR member path rows into the shared place-address resolver.
        - [x] Extend declared-range facts to indexed fixed-array element reads (2026-07-02: fixed-array member-path facts decode the ELEMENT's declared range from the element type head; const- and dynamic-index element reads attach it to their nodes, and the indexed load lowers through the declared-range typed LoadPtr — element loads carry `!range` and their values prove second-array index bounds; unranged elements still reject as unproven indexes).
        - [x] Prove compound-assignment and try-assignment stored ranges against narrow declared field ranges (2026-07-02: `+=`/`-=` on simple and nested field targets desugar at parse into `field OP value` over a range-carrying field read, so the existing declared-range store proof judges the widened result — full-width fields lower, narrow fields reject without evidence, matching the host's conservatism; try-assignments into narrow-ranged fields reject as unproven because the [Ok] payload's range is not yet decoded into node facts; the guard is currently shadowed by the pre-existing shape gap above (try field-assignments reject for every width in this entry) — payload-range subset proofs, indexed compound targets, and the shape gap are the follow-ups).
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
      - [ ] Lower aggregate and list pattern switch cases through MIR branch tests.
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
    - [ ] Lower enum-valued function returns through owner-aware enum return carriers (the single-function entry currently rejects `return Pick.First`; a prior instrumented build accepted the shape while emitting an invalid `unknown`-typed module — evidence retracted, see the 2026-07-04 ledger entry; `selfhost/probe/EnumReturnProbe.stark` pins the shapes and must validate the emitted module when this lands).
  - [~] Lower dynamic storage.
    - [x] Lower arena-backed HIR dynamic storage init and reserve operations to MIR.
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
  - [ ] Port imported-template lowering.
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
    - [ ] Attach source global linkage facts after source-backed global lowering exists.
    - [ ] Attach linkage facts through the libLLVM module builder after ABI/SSA object emission lands.
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
    - [x] Keep manifest-backed module resolution from falling back to raw `.starkpkg` source reads.
    - [x] Keep library package images scoped to source-owned modules while preserving direct external imports.
    - [x] Compile library dependency objects only from source-backed imports and use package-backed facts as imports.
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
  - [x] Add per-fact test progress streaming to `stark test` (2026-07-03): the driver streams runner output line-by-line (never buffers until exit); `--test-progress` passes `--progress` to the generated runner, which prints `run <name>` markers and `ok|FAILED <name> (k/N)` counters via the new `System.Testing.BeginFact`/`RunFactCounted`, and the driver stamps `[elapsed]` prefixes; `--test-timeout <seconds>` kills the process tree and reports the in-flight fact. Default output stays byte-identical.
  - [x] Preserve the test-runner progress protocol in the stage1 `stark test` port (2026-07-03): the protocol components are ported and golden-parity verified — `Compiler.TestRunner` emits the generated runner byte-identically to stage0, and `Compiler.TestDriver` reproduces the streaming/prefix/timeout contract against the normative `tests/fixtures/test-progress` goldens (pinned by `tests-stark/selfhost.TestRunner`, 4/4). The eventual stage1 CLI port wires these components into project discovery/build orchestration (tracked with the CLI port items; see docs/Self-host-Prep/30-test-progress-streaming.md).
  - [ ] Smooth the package-consumer edges found by the probe-recipe work (2026-07-01):
    - [ ] Raw `--target` invocations derive a different LLVM data layout than project builds embed, so consuming a project-built package requires copying `--target-data-layout` from `--inspect-pkg` by hand; derive the same layout by default for a bare `--target` triple.
    - [ ] Search-dir resolution enumerates `*.starkpkg` recursively, so a package image left under a source search root (e.g. `selfhost/build/` under `-I selfhost`) poisons source-only compiles with STK7312; scope package discovery away from source roots or prefer fresh source over images outside the project driver. Worse, the root file's own directory joins the search, so a stale test-project package (e.g. `tests-stark/selfhost.Ir/build/.../libSystem.starkpkg`) silently SHADOWS fresh stdlib source in raw `--check` runs — observed 2026-07-01 as phantom STK4107 kind errors against pre-fix `List.Get`; the raw CLI has no freshness stamps, only the project driver does. Delete the stale image (build output) to unblock.
    - [ ] A library package records its static library by relative path (`../../bin/...`), so relocated packages must replicate the `stage0/pkg` + `stage0/bin` layout; consider embedding a layout-independent reference.

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


## 10. Cutover - Deferred Until All Other Work Is Complete

- [ ] Keep the C# compiler in `/src` until Stage1 can build the Stark compiler.
- [ ] Build the Stage1 Stark compiler with Stage0 and emit the expected compiler executable.
- [ ] Build the Stage2 Stark compiler with Stage1 and emit the expected compiler executable.
- [ ] Compare Stage1 and Stage2 package images, diagnostics, artifacts, and executable behavior.
- [ ] Run compiler benchmarks for Stage0, Stage1, and Stage2.
- [ ] Address cutover-only divergences discovered by Stage1/Stage2 comparison.
- [ ] Migrate durable Self-host-Prep content to Userfacing/Internals and retire this folder: fold the surviving parts of the numbered companion documents, TASKS.md, and TestPassLedger.md into docs/Userfacing or docs/Internals (append to existing documents where one fits), then delete the Self-host-Prep documents. (The pain-points tracker was retired 2026-07-01 after its durable rationale — the widened `--check`, `overlap_all`, the module-facts bundle, the probe recipe — landed in Userfacing/Internals; its resolution log survives as the 2026-07-01 pain-point-fixes entry in TestPassLedger.md.)
