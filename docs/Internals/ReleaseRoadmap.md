# Stark Release Roadmap

This document tracks release-oriented work that should be handled as its own
roadmap, separate from the long-running implementation roadmap.

Completion rules:

- Keep runtime performance and correctness as release blockers.
- Treat every grammatically valid, front-end-accepted Stark program as a
  compiler contract: it must lower correctly through MIR, SSA, LLVM, package
  image, and imported-template paths. Syntax is rejected by the parser;
  semantic/type/ownership invalidity is rejected before MIR; MIR lowering must
  not decide language validity.
- Do not release with ordinary supported language constructs falling back to
  `unsupported-lowering`, `llvm-body-fallback`, `llvm-asm-fallback`, or
  declaration-only code generation. There should be no accepted-but-unsupported
  constructs at release; syntax that Stark does not intend to support should be
  removed from the grammar or rejected before it reaches MIR.
- Do not leave both stable and experimental standard library implementations in
  place after promotion work is complete.
- Prefer safe language features in the standard library. Raw pointers should be
  used only at FFI, OS, compiler-runtime, or explicitly unsafe backend
  boundaries.
- Every standard library migration task must update tests, benchmarks, package
  surfaces, and user-facing examples when behavior or names change.

## 0. Eliminate Unsupported Lowering And Codegen Fallbacks

- [ ] Treat direct lowering and backend fallback cleanup as a release blocker.
  - [x] Audit every `MarkUnsupported(` in `src/Compiler/MidLevelIrLowering` and
        classify it as one of: semantic diagnostic, missing MIR lowering,
        imported-template lowering gap, or backend correctness guard.
  - [ ] Define and enforce the accepted-program lowering contract.
    - [x] Document the compiler-layer ownership rule in internals docs:
          parser rejects syntax only; declaration/symbol/type/semantic/ownership
          passes reject invalid Stark programs; MIR lowering receives only
          accepted executable semantics.
      - [x] 2026-05-12 documented in
            `docs/Internals/LanguageInternals.md`: MIR lowering is not a
            language-validity filter; accepted programs must lower directly or
            trigger an internal invariant failure, while grammatically valid
            invalid programs must be rejected before MIR.
    - [x] Replace the current implicit "try lowering, maybe mark unsupported"
          contract with explicit categories:
          front-end diagnostic, lowering invariant violation, or implemented
          lowering. `MarkUnsupported` must not be used for accepted source
          constructs.
      - [x] 2026-05-12 `MarkUnsupported` call sites were removed from
            `MidLevelIrLowering`; accepted source constructs now either lower
            directly or fail as compiler invariants, and backend fallback logs
            are promoted to build-failing diagnostics by the default pipeline.
    - [ ] Add a pre-MIR lowering-contract validation pass, or equivalent typed
          model checks, that proves all facts required by MIR are present before
          `lower-mir`: resolved call targets, receiver binding, argument
          coercions, function-pointer ABI metadata, index operation kind,
          constructor shape/body facts, enum layout facts, dynamic-storage
          operation facts, concrete layout facts, and ownership/drop facts.
      - [x] Add a `validate-lowering-contract` compiler pass after
            type-checking, semantic validation, and ownership validation, before
            `lower-mir`. It should produce normal diagnostics for invalid source
            programs and reserve MIR invariant exceptions for compiler bugs.
        - [x] 2026-05-12 implemented `validate-lowering-contract` between
              `ownership-validate` and `lower-hir`. The pass consumes typed
              facts recorded by type checking and emits `STK5003` before HIR/MIR
              lowering if an accepted source body contains a call, index/slice,
              object creation, named-field enum constructor, lambda, layout
              query, or dynamic-storage operation without the matching typed
              lowering fact. The pass also records a
              `LoweringContractValidationModel` artifact with checked operation
              counts for fast regression assertions.
      - [ ] Validate call facts: direct/member overload identity, receiver kind,
            argument count, argument coercions, function-pointer ABI metadata,
            `borrow`/`out`/`init` address facts, and void-result usage.
        - [x] 2026-05-12 first enforcement step: every source-backed postfix
              call that reaches the pass must already have a typed direct-call,
              member-call, indirect-call, enum-constructor-call, or
              dynamic-storage operation record. Lambda bodies are now typed under
              their generated lambda function names so call facts match the
              function body MIR will lower. Remaining work is to enrich the facts
              with explicit coercion/address/ABI metadata rather than only
              proving that the call has a resolved typed target.
        - [x] 2026-05-12 call-fact payload validation step: the
              lowering-contract pass now validates direct-call and member-call
              explicit argument arity against the recorded signature, verifies
              member-call facts include a receiver parameter, verifies
              function-pointer call facts carry concrete ABI parameter/return
              metadata, and verifies positional enum-constructor call arity
              against the recorded enum variant payload. Remaining work is to
              model and validate per-argument coercions plus `borrow`/`out`/`init`
              address facts explicitly instead of relying on type-check success.
        - [x] 2026-05-12 call-argument address contract step: direct-call,
              member-call, and indirect-call facts now carry per-argument
              lowering records with signature parameter index, source argument
              index, parameter/argument type, receiver marker, const provenance,
              and whether addressable/mutable storage was required and proven.
              The lowering-contract pass rejects contradictory `borrow`,
              `mut borrow`, `out`, and `init` argument facts before MIR for
              direct-call arguments, member-call explicit arguments, and
              function-pointer-call arguments. Receiver facts still record the
              receiver marker/type/provenance, but do not require source
              addressability because Stark member receivers may lower through an
              implicit temporary when the source receiver is an rvalue.
      - [ ] Validate operation facts: indexing/slicing operation kind and arity,
            dynamic-storage receiver mutability/addressability, constructor and
            object-initializer shape, enum variant/payload layout facts, switch
            lowering family, `sizeof`/`alignof` concrete-layout availability,
            and runtime-drop/ownership cleanup facts.
        - [x] 2026-05-12 first enforcement step: type checking now records
              `IndexAccessTypingRecord` facts for fixed-array/slice/raw-pointer,
              text, dynamic-storage indexing/slicing, and raw-pointer
              runtime-disjoint region operands such as `left[0, count]`, plus
              `DynamicStorageOperationTypingRecord` facts for
              `Reserve`/`TryReserve`/`TryReserveCapacity`/`MoveLast`/`MoveAt`.
              The lowering-contract pass requires those facts, and also requires
              existing object-creation, named-field enum-constructor, lambda, and
              `sizeof`/`alignof` facts before lowering. Remaining work is to add
              concrete-layout and runtime-drop/ownership cleanup validation to
              the same contract model.
        - [x] 2026-05-12 operation-fact payload validation step: the
              lowering-contract pass now rejects contradictory typed facts, not
              only missing facts. It validates index operation family and arity
              against source syntax and source type, dynamic-storage operation
              name/arity/result type against the source member call, object
              constructor arity and initializer field layout, named-field enum
              constructor member count and field layout, `sizeof`/`alignof` kind
              and target type, and lambda function-pointer ABI shape.
        - [x] 2026-05-12 switch-family validation step: type checking now
              records `SwitchTypingRecord` facts for every accepted source
              switch, including the concrete switch domain, dispatch family
              (`native`, `partitioned-text`, or `guarded`), explicit/default
              label counts, literal/capture/structured-pattern counts, and
              guarded-label counts. The lowering-contract pass validates those
              facts before HIR/MIR, rejects contradictory dispatch-family or
              label-shape facts, and requires typed enum/aggregate pattern facts
              for structured switch labels so switch MIR lowering no longer has
              to infer whether a parsed aggregate is an enum case or aggregate
              after the front end accepted it.
        - [x] 2026-05-12 dynamic-storage receiver contract step: type checking
              now records whether each accepted dynamic-storage operation
              receiver was addressable and mutable at the source call site.
              The lowering-contract pass rejects missing or contradictory
              receiver addressability/mutability facts before MIR, so dynamic
              storage lowering no longer has to rediscover this owner contract
              from raw postfix syntax.
        - [x] 2026-05-12 concrete-layout validation step: the lowering-contract
              pass now depends on enum layout and verifies every `sizeof`/
              `alignof` typed target that is already closed has a concrete
              layout available before MIR/backend layout lowering. Open generic
              layout queries such as `sizeof(T)` remain valid in generic bodies
              and are checked after monomorphization when `T` is concrete.
              Corrupted closed layout-query facts now fail before MIR instead of
              reaching layout-dependent lowering.
      - [ ] Validate imported/package-image/generic-template facts with the same
            contract as source bodies, including substituted local declarations,
            published typed-template operations, monomorphized concrete layouts,
            and inline clone reachability facts.
      - [x] Add focused invalid-boundary tests for this pass so each diagnostic
            fails before `lower-mir`, plus accepted-program tests proving the
            pass does not pessimize or reject standard-library, package-image,
            benchmark, and example builds.
        - [x] 2026-05-12 added focused compiler tests that run the new pass
              before HIR/MIR, assert accepted typed operation facts survive, and
              assert corrupted call, index, and dynamic-storage fact models fail
              with `STK5003` in `validate-lowering-contract`.
        - [x] 2026-05-12 added corruption tests proving the pass fails before
              MIR when typed direct-call arity, index arity, or dynamic-storage
              operation identity facts disagree with source syntax.
        - [x] 2026-05-12 added switch contract tests proving accepted native
              and guarded enum switches publish switch facts, missing switch
              facts fail before MIR, missing enum/aggregate pattern facts fail
              before MIR, and corrupted switch-family facts cannot steer MIR
              into a contradictory dispatch path.
        - [x] 2026-05-12 added dynamic-storage receiver corruption coverage
              proving addressable/mutable owner facts are enforced before MIR.
        - [x] 2026-05-12 added layout-query corruption coverage proving
              `sizeof`/`alignof` facts must reference a concretely layoutable
              type before MIR.
        - [x] 2026-05-12 added call-argument corruption coverage proving direct
              calls, member-call explicit arguments, and indirect
              function-pointer calls cannot reach MIR with missing
              addressable/mutable argument facts.
        - [x] 2026-05-12 closure note: accepted-program gates now cover
              benchmarks, representative examples, standard-library source, and
              package-image-backed imported programs through the normal pipeline
              so `validate-lowering-contract` cannot reject those surfaces
              without failing compiler tests.
    - [x] Make lowering invariant violations fail as compiler bugs with
          source/function context and tests, not as `unsupported-lowering` logs
          or declaration-only codegen.
      - [x] 2026-05-12 first enforcement step: `lower-mir` now emits compiler
            diagnostics for `unsupported-lowering`/`missing-function-body` logs,
            and `emit-llvm` now emits diagnostics for `llvm-body-fallback`,
            `llvm-asm-fallback`, and `llvm-body-pending` logs. This converts
            declaration-only fallback from a quiet artifact shape into a build
            failure for accepted programs.
      - [x] 2026-05-12 first invariant step: semantic-error-shaped MIR guards
            for constructor return values, void calls used as values, raw-slice
            argument shape, invalid indexes, dynamic-storage `Reserve` shape,
            unresolved names, function-as-value misuse, and failed direct-call
            binding now either fail before MIR or throw a lowering invariant
            violation with source/function context instead of logging
            `unsupported-lowering`.
      - [x] 2026-05-12 source-location invariant step: MIR local-declaration
            fact lookup now matches source file, line, column, and enclosing
            function before falling back to unique location matches, then applies
            active generic substitutions. This prevents imported/source module
            bodies from picking up unrelated locals that happen to share a
            line/column in another file.
      - [x] 2026-05-12 loaded-module type environment step: MIR lowering now
            builds a lowering-time named-type environment for all loaded source
            modules, including internal implementation types needed by imported
            bodies. This keeps accepted stdlib module bodies lowering against
            the same struct/record/enum fields the type checker accepted, rather
            than depending on the root module's public visibility surface.
      - [x] Continue replacing the remaining `MarkUnsupported` sites with
            either earlier diagnostics, implemented direct lowering, or internal
            invariant failures.
        - [x] 2026-05-12 completion step: `src/Compiler/MidLevelIrLowering`
              no longer contains any `MarkUnsupported` call sites. Source-body,
              imported-template, switch, place, runtime-drop, and fixed-text
              lowering now either produce direct MIR or throw a lowering
              invariant violation for a compiler-contract breach. Accepted
              programs must not downgrade to declaration-only MIR/LLVM fallback.
    - [x] Define a release gate for "accepted program": if compilation reaches
          `lower-mir` without diagnostics, the MIR result must have direct
          lowering support and backend emission must not fall back.
  - [ ] Make invalid states unrepresentable, or at least unrepresentable at the
        MIR boundary.
    - [x] Introduce or complete a bound/typed executable expression model for
          calls, member calls, function-pointer calls, indexing/slicing,
          object/constructor creation, enum construction, dynamic-storage
          operations, text interpolation, `sizeof`/`alignof`, and switch shapes.
          MIR should consume this model instead of rediscovering validity from
          raw parse-tree shapes.
      - [x] Define a closed `BoundOperation`/`TypedOperation` representation for
            executable expressions and statements, with variants for direct call,
            member call, function-pointer call, index/slice, constructor/object
            creation, enum value construction, dynamic-storage operation, text
            interpolation/building, layout query, and switch dispatch.
      - [x] Populate bound operations during type checking for root source
            bodies, including exact overload symbols, receiver/addressability
            facts, result type, coercions, ownership effects, and any required
            ABI metadata.
      - [x] Teach MIR lowering to consume bound operations for one operation
            family at a time, starting with calls and indexing, then
            object/enum construction, dynamic storage, text, and switch. Each
            migrated family should remove the equivalent raw parse-tree
            rediscovery path.
      - [x] Add a debug/validation gate that rejects any executable expression
            reaching MIR without a bound operation when one is required.
    - [x] Carry bound operation facts through source modules, imported package
          images, generic typed-template bodies, monomorphization, and inline
          clone planning so imported code has the same lowering contract as root
          source code.
      - [x] MIR direct/member call lowering first consults recorded typed call
            facts when present and preserves exact function-pointer promotion
            facts for package-image-backed overloaded function items.
      - [x] Generic materialized bodies keep local declaration types substituted
            through MIR, including locals resolved by type-check records instead
            of raw syntax fallback.
      - [x] Extend package-image typed bodies to serialize every bound-operation
            family, not just the call/local facts currently needed by fixed
            regressions.
      - [x] Add package producer/consumer tests that corrupt or remove source
            body text after package creation and still lower imported generic
            bodies from typed/bound facts alone.
      - [x] Carry bound-operation substitutions through generic
            monomorphization, including substituted result/parameter types,
            enum payload layouts, dynamic-storage element layouts, and
            function-pointer ABI signatures.
      - [x] Include inline clone reachability in the bound/imported model so
            inline clone emission is driven by reachable bound calls rather than
            opportunistic text/import scans.
        - [x] 2026-05-17 completion step: imported inline clone seed planning
              now enumerates function/effect/template facts from the imported
              model, expands package generic reachability through serialized
              bound direct/member calls, and carries imported inline-body
              candidate eligibility through generic specialization symbols.
    - [x] Replace null-return "could not lower" paths for accepted constructs
          with exhaustive lowering over bound operation kinds. Any missing case
          should be an internal invariant failure until direct lowering is
          implemented.
      - [x] Inventory all `return null`/nullable-result lowering helpers in
            `MidLevelIrLowering` and classify them as optional lookup, invalid
            source diagnostic, or compiler invariant.
        - [x] 2026-05-17 inventory/classification closeout: remaining nullable
              helpers in `MidLevelIrLowering` are intentional optional probes
              for parser shape, overload/package lookup, addressability,
              indirect argument storage, post-call dynamic-length commits, and
              text/switch helper discovery; invalid-source-shaped expression
              failures are required to have been diagnosed before MIR or are
              wrapped by required-operand/invariant gates at statement,
              assignment, return, and imported-template boundaries; accepted
              bound-operation mismatches are compiler invariants.
      - [x] For each bound-operation family migrated to the closed model, make
            the MIR lowerer use exhaustive `switch` handling and throw a
            lowering invariant on impossible or unimplemented bound variants.
        - [x] 2026-05-17 completion step: source MIR lowering now validates
              direct/member/function-pointer/closure calls, indexing/slicing,
              object and enum construction, dynamic-storage operations,
              fixed/runtime text operations, layout queries, and switch shapes
              against bound facts before emitting MIR. Imported typed-template
              expression lowering now uses a closed expression-kind switch;
              missing serialized call/member/index/object/enum/dynamic/text
              facts, void calls used as values, unsupported bound dynamic
              operations, and contradictory direct/member call facts throw
              lowering invariants instead of returning null.
      - [x] Add regression tests that accepted constructs no longer produce null
            MIR operands, null assignment targets, placeholder expression
            statements, or declaration-only fallback artifacts.
        - [x] 2026-05-17 regression closeout: focused MIR tests assert accepted
              bound-operation source bodies produce no null MIR operands,
              targets, rvalues, statement placeholders, or malformed
              terminators. Package-image consumer tests now remove/corrupt
              imported source body text, lower imported generic typed bodies
              from serialized facts, assert no fallback logs, and run the same
              null-artifact MIR validation over the imported-template path,
              including function-pointer and closure conditional-call branches.
    - [x] Update MIR data structures so impossible values are not expressible
          where practical: void calls cannot be value operands, unresolved names
          cannot become operands, untyped indexes cannot become index rvalues,
          constructor calls cannot lack shape/body facts, and enum operations
          cannot lack layout/variant facts.
      - [x] Split MIR call nodes into value-producing and statement-only forms
            so a `void` call cannot be embedded as a value operand.
        - [x] 2026-05-17 MIR `Evaluate` statements now carry statement-only
              direct/indirect call operations separately from value rvalues;
              source and imported typed-template expression statements lower
              through that slot, SSA lowering consumes it, and MIR validation
              rejects `void` call rvalues at the lowering boundary.
      - [x] Replace string/name-based unresolved operands with typed symbol
            references or explicit front-end diagnostics before MIR.
        - [x] 2026-05-17 the remaining value-model place fallback that
              fabricated a `MidLevelIrLocalOperand` from an unresolved root name
              was removed. MIR place reads and aggregate assignments now require
              `ResolveNamedOperand` to return a typed local/parameter/global,
              function-address, closure, or enum value operand, otherwise
              lowering raises an invariant after front-end diagnostics have had
              their chance to reject invalid source.
      - [x] Replace generic index rvalues with typed index/slice rvalues that
            carry their operation family and validated element/view result type.
        - [x] 2026-05-17 MIR and SSA index insert/extract rvalues now carry a
              closed indexed-operation family (`FixedArrayElement`,
              `ViewComponent`, `ClosureComponent`), and SSA validation checks
              the family against the target type before LLVM emission.
      - [x] Make constructor/object/enum MIR nodes require resolved layout,
            constructor body/field mapping, enum variant, and payload projection
            facts in their constructors.
        - [x] 2026-05-17 object construction results are now wrapped in
              `MidLevelIrObjectConstructionOperand` facts carrying the named
              created type, construction kind, substituted constructor shape,
              explicit constructor body key, and initializer field mappings.
              Enum construction results are now wrapped in
              `MidLevelIrEnumConstructionOperand` facts carrying the enum
              layout, resolved variant, tag layout, and payload storage
              projections. MIR/typed-template tests assert these facts for root
              source bodies and package-backed generic bodies with corrupted
              bridge source text.
      - [x] Update SSA lowering and package-image emission for each MIR data
            structure change, with compatibility tests for imported typed
            templates and inline clones.
        - [x] 2026-05-17 SSA lowering unwraps object/enum construction operands
              to the proven underlying value while preserving aggregate
              copy/move source detection and address-model projection, so the
              fact-carrying MIR boundary adds no runtime work. Existing package
              typed-body object/enum facts feed the new wrappers for imported
              templates, and package/inline-clone regression coverage exercises
              typed-template lowering without source-body fallback.
    - [x] Make compiler-generated scratch locals explicit before SSA/LLVM.
      - [x] 2026-05-12 SSA lowering now emits explicit stack
            `SsaAllocateLocalInstruction` records for addressable
            compiler-generated scratch locals such as `$tmp*_slice`,
            `$tmp*_field`, `$tmp*_call`, and `$tmp*_drop` when they do not have
            source `StorageLive` statements. These allocations intentionally do
            not add lifetime markers, preserving the previous low-overhead lazy
            alloca behavior while making the storage contract visible to SSA
            validation and downstream optimization.
      - [x] 2026-05-12 `validate-ssa` now requires local-like SSA operations
            (`store local`, local loads, local addresses, slice-from-local, and
            lifetime/deallocation records) to reference an explicit
            `SsaAllocateLocalInstruction`. This closes the earlier escape hatch
            where LLVM emission could silently materialize missing scratch
            storage on demand. Regression coverage includes a direct invalid SSA
            case and a non-addressable fixed-array dynamic-index spill that must
            allocate its generated scratch slot in SSA without lifetime marker
            bloat.
    - [x] Add tests that compile representative accepted programs through
          `emit-llvm` and assert no fallback logs, then compile invalid but
          grammatically correct programs and assert they fail before `lower-mir`.
      - [x] Added benchmark-source `emit-llvm` fallback-log gate.
      - [x] Added package-image-backed imported generic `emit-llvm`
            fallback-log gate.
      - [x] Added representative example-source `emit-llvm` fallback-log gate.
      - [x] Added invalid-but-grammatical diagnostic cases for constructor
            return values, direct-call arity, raw-slice arity, non-integer
            fixed-array indexing, dynamic-storage `Reserve` capacity type,
            function-as-value misuse, and unresolved names.
      - [x] Add an explicit standard-library source/package no-fallback gate
            that is fast enough for normal compiler tests and does not duplicate
            the long-running standard-library runtime tests.
      - [x] Verified the fast standard-library source gate against imported
            internal stdlib types such as `System.Runtime.ByteSliceParts` and
            generic stdlib bodies such as `Dictionary<K,V>.Reserve`.
  - [x] Front-end diagnostics: move invalid but grammatically correct Stark
        programs out of MIR lowering and into parser, type-checking, semantic
        validation, ownership validation, or the lowering-contract validator.
    - [x] Diagnose `break` outside loop/switch before MIR lowering
          (`FunctionMirBuilder.cs:598`).
    - [x] Diagnose `continue` outside loop before MIR lowering
          (`FunctionMirBuilder.cs:612`).
    - [x] Diagnose constructor bodies returning a value before MIR lowering
          (`FunctionMirBuilder.cs:1484`).
    - [x] Diagnose invalid direct call arity or binding before MIR lowering
          (`FunctionMirBuilder.cs:5243`).
    - [x] Diagnose invalid member call arity before MIR lowering.
    - [x] Finish invalid member call binding diagnostics before MIR lowering
          (`FunctionMirBuilder.cs:5267`).
      - [x] 2026-05-12 member-call diagnostic coverage: type-check tests now
            cover member overload no-match and ambiguity diagnostics, and the
            full-pipeline invalid-boundary gate includes a member overload
            no-match case that must fail before MIR without fallback logs.
            Existing static-vs-instance diagnostics and member-call arity
            diagnostics remain the front-end rejection path for those shapes.
    - [x] Diagnose invalid raw slice construction arguments before MIR lowering
          (`FunctionMirBuilder.cs:3401`, `FunctionMirBuilder.cs:3412`,
          `FunctionMirBuilder.cs:3427`).
    - [x] Diagnose invalid non-integer indexing operands before MIR lowering.
    - [x] Diagnose every invalid indexing arity before
          MIR lowering (`FunctionMirBuilder.cs:3569`, `FunctionMirBuilder.cs:4732`,
          `FunctionMirBuilder.cs:4781`, `FunctionMirBuilder.cs:4824`,
          `FunctionMirBuilder.cs:4870`, `FunctionMirBuilder.cs:4877`,
          `FunctionMirBuilder.cs:4905`, `FunctionMirBuilder.cs:4942`,
          `FunctionMirBuilder.cs:4955`, `FunctionMirBuilder.cs:4966`).
      - [x] 2026-05-12 indexing diagnostic coverage: type-checking rejects
            empty non-text indexing, non-integer index operands, dynamic storage
            indexing with more than two operands, and text indexing with more
            than two operands before MIR. Full-pipeline invalid-boundary tests
            cover empty index, dynamic-storage index arity, text index arity,
            and non-integer indexing with fallback-log assertions. Nested
            fixed-array/slice/raw-pointer indexing remains intentionally
            accepted when each projected element is itself indexable; invalid
            scalar continuation is rejected as non-indexable before MIR.
    - [x] Diagnose invalid dynamic storage `Reserve` capacity argument type
          before MIR lowering.
    - [x] Diagnose invalid dynamic storage creation capacity argument type
          before MIR lowering.
    - [x] Diagnose all invalid dynamic storage operation receivers and argument
          shapes before MIR lowering (`FunctionMirBuilder.cs:7570-7704`).
      - [x] 2026-05-12 dynamic-storage diagnostic completion: type checking now
            enforces the same receiver contract MIR lowering requires: the
            `Reserve`/`TryReserve`/`TryReserveCapacity`/`MoveLast`/`MoveAt`
            receiver must be a mutable addressable dynamic owner. Focused
            diagnostics cover immutable owners, non-addressable returned dynamic
            values, missing/extra reserve arguments, missing/extra `MoveAt`
            indexes, non-integer indexes, and negative indexes. Full-pipeline
            invalid-boundary tests assert these cases fail before MIR and do not
            produce fallback logs.
    - [x] Diagnose unresolved named operands and function-as-value misuse before
          MIR lowering (`FunctionMirBuilder.cs:5979`, `FunctionMirBuilder.cs:5983`).
    - [x] Diagnose lambda expressions without an explicit function-pointer
          target before MIR lowering.
  - [x] Resolve formerly questionable parsed constructs into either supported
        lowering paths or precise pre-MIR semantic diagnostics.
    - [x] Decide whether function-pointer calls with borrow/out/init parameters
          ship in this release; otherwise add an intentional diagnostic and tests
          (`FunctionMirBuilder.cs:5185`).
      - [x] 2026-05-12 decision: ship them. MIR indirect-call lowering now
            records the same direct-call-style indirect argument local/address
            metadata used for borrow/out/init parameters, SSA preserves and
            rewrites that metadata, and LLVM emission synthesizes ABI signatures
            from the function-pointer type so indirect calls use pointer ABI,
            sret, and large-aggregate `byval` rules instead of falling back.
    - [x] Decide whether dynamic fixed-array indexing from non-addressable values
          ships in this release; otherwise add an intentional diagnostic and tests
          (`FunctionMirBuilder.cs:4722`, `FunctionMirBuilder.cs:4739`).
      - [x] 2026-05-12 decision: ship it. Dynamic fixed-array indexing is a
            supported value operation, including arrays produced by calls,
            object/array construction, imported typed-template bodies, and other
            non-addressable fixed-array expressions. Lowering keeps the fast path
            for addressable locals, parameters, and globals, and spills only a
            genuinely non-addressable fixed-array value to a temporary local so
            the dynamic element access can use one address calculation plus a
            scalar load/store. MIR coverage verifies `(new T[N])[index]` lowers
            without fallback, and LLVM coverage verifies `Make(...)[index]`
            materializes only the temporary source and emits direct element GEP
            plus scalar load instead of declaration-only fallback.
    - [x] Decide whether non-folded interpolated text literals ship in this
          release; otherwise add an intentional diagnostic and tests
          (`FunctionMirBuilder.cs:5874`).
      - [x] 2026-05-12 decision: standalone non-folded interpolation does not
            ship as an implicit allocation or hidden growable text operation.
            Runtime interpolation is accepted only when the caller provides a
            fixed-capacity `Ascii`/`Unicode` destination such as
            `stack Ascii label[64] = $"Score: {score}";`, or when code calls
            `System.Text` formatting APIs directly. Type checking rejects
            `$"..."` value expressions with runtime holes before MIR with a
            caller-owned-storage diagnostic, and the full-pipeline invalid
            boundary tests assert that this case does not reach lowering or
            backend fallback. Fixed-capacity runtime interpolation remains a
            supported direct MIR path.
    - [x] Decide whether switch shapes outside native integer/bool, partitioned
          text, and guarded lowering ship in this release; otherwise add a
          semantic diagnostic and tests (`SwitchPatternLowerer.cs:44`,
          `SwitchPatternLowerer.cs:64`).
      - [x] 2026-05-12 decision: every accepted switch must lower through one
            of the implemented paths: native integer/bool switch dispatch,
            partitioned ascii/unicode literal dispatch, or guarded decision-tree
            lowering for floats, raw pointers, named aggregate patterns, enum
            case patterns, captures, discards, and `when` clauses. Switch
            domains outside that set, multi-label aggregate/capture sections,
            enum whole-value `var` captures, and invalid aggregate payload field
            subpatterns are rejected by type checking before MIR. Existing
            MIR/LLVM tests cover native, guarded, text, aggregate, nested
            aggregate, enum, capture, and multi-label supported paths, and the
            full-pipeline invalid-boundary tests now assert invalid switch
            domains fail without lowering/backend fallback logs.
  - [x] Missing MIR lowering: implement direct lowering for accepted source
        constructs that currently call `MarkUnsupported`.
    - [x] Finish fixed text storage initialization, concatenation,
          interpolation, formatter calls, and `System.Text` helper binding
          (`FunctionMirBuilder.cs:773-1230`).
      - [x] 2026-05-12 fallback cleanup: fixed-capacity `Ascii`/`Unicode`
            storage lowering keeps the existing direct MIR paths for storage
            construction, concatenation, interpolation, formatter calls, and
            `System.Text` helper binding. Any remaining failure to materialize a
            helper, formatter, view, destination address, temporary, or value is
            now a lowering invariant violation rather than an
            `unsupported-lowering` fallback. Existing type-check diagnostics
            still reject non-stack fixed text storage, missing capacities,
            runtime-only text concatenation without destination storage, and
            interpolated holes without known fixed-buffer formatters before MIR.
    - [x] Finish object initializer, array initializer, variable initializer,
          and initializer-to-operand lowering (`FunctionMirBuilder.cs:1327`,
          `FunctionMirBuilder.cs:1341`, `FunctionMirBuilder.cs:1350`,
          `FunctionMirBuilder.cs:1473`, `FunctionMirBuilder.cs:8003`).
      - [x] Local variable initializer lowering no longer emits placeholder
            assignments when expression/object/array initializer lowering fails.
            Accepted initializers must now materialize a MIR operand or aggregate
            value; otherwise lowering reports an invariant violation with the
            local name and initializer text.
      - [x] 2026-05-12 completion note: initializer-to-operand lowering now
            exhausts the accepted expression, object-initializer, and
            array-initializer shapes and treats every missing materialized value
            as a lowering invariant. The object/constructor/enum completion
            below covers the aggregate creation side; no initializer path emits
            a placeholder MIR value or declaration-only fallback for accepted
            source.
    - [x] Finish assignment, compound assignment, pointer assignment, expression
          statement, and conditional expression lowering (`FunctionMirBuilder.cs:1548`,
          `FunctionMirBuilder.cs:1713-1872`, `FunctionMirBuilder.cs:2809`,
          `FunctionMirBuilder.cs:2828`, `FunctionMirBuilder.cs:2867`).
      - [x] Assignment and pointer-assignment lowering no longer logs
            `unsupported-lowering` when an accepted RHS, coercion, pointee load,
            or compound-assignment temporary fails to materialize. Those cases
            now throw lowering invariant violations tied to the assignment text.
            Expression statements that are not assignments, calls, or value
            operands likewise fail as invariants instead of emitting placeholder
            `Evaluate` MIR.
      - [x] Conditional-expression lowering now treats malformed branch counts,
            missing branch values, missing common result types, and failed branch
            coercions as lowering invariants. This keeps accepted ternaries on a
            complete branch/join MIR path and prevents null operands from
            escaping into later passes.
      - [x] 2026-05-12 completion note: assignment and expression-statement
            lowering now has no user-facing fallback category. Accepted
            assignments must resolve a direct place or pointer destination and
            materialize/coerce their right-hand side; accepted expression
            statements must be assignment, call, conditional call, or lowerable
            value evaluation. Anything else at this layer is a compiler-contract
            breach rather than partial MIR.
    - [x] Finish runtime `disjoint(...)` condition and memory-range lowering
          (`FunctionMirBuilder.cs:2064-2497`).
      - [x] 2026-05-12 runtime disjoint lowering now treats the accepted
            `if disjoint(...)` feature as a complete MIR path rather than a
            fallback path. Missing parsed conditions, too few operands, invalid
            memory-range operands, raw pointer region shape/type failures,
            non-addressable borrow/init operands, failed byte-pointer
            conversions, failed byte-length/end materialization, and missing
            concrete layouts now throw lowering invariant violations with source
            context. Invalid scalar operands are covered by a front-end
            diagnostic regression, so MIR no longer decides their validity.
            Existing LLVM/SSA tests cover raw pointers, bounded pointer regions,
            borrows, slices, text/raw-slice backing roots, and scoped noalias
            metadata in the true branch.
    - [x] Finish unary address/dereference/power expression lowering failures
          (`FunctionMirBuilder.cs:3144`, `FunctionMirBuilder.cs:3161`,
          `FunctionMirBuilder.cs:3204`).
      - [x] Address-of, dereference, and runtime power expressions now fail as
            lowering invariants when their accepted operands lack the required
            addressable target, raw-pointer element type, numeric common type,
            coercion, or result temporary. Invalid user programs still belong in
            earlier diagnostics; these guards now represent compiler bugs rather
            than fallback body emission.
    - [x] Finish lambda expression lowering for target-typed function pointers and
          type-checked non-capturing lambda records (`FunctionMirBuilder.cs:3771`,
          `FunctionMirBuilder.cs:3782`).
      - [x] Lambda function body lowering no longer logs
            `unsupported-lowering` for impossible body shapes. Parser-accepted
            lambda bodies must be expression-bodied or block-bodied; any other
            shape now fails as a lowering invariant violation instead of
            producing a declaration-only fallback.
      - [x] 2026-05-12 completion note: non-capturing lambdas are target typed
            only by explicit `fnptr<...>` destinations, type checking records a
            synthetic lambda function with an exact ABI signature, MIR lowers
            the expression to a `MidLevelIrFunctionAddressOperand`, and LLVM
            emits the synthetic internal definition plus direct function-pointer
            value. Package-image-backed function-pointer parameters use the
            same target typing path. Capturing lambdas are not valid
            `fnptr<...>` values because function pointers do not carry closure
            storage: their capture lists and bodies are checked, then type
            checking emits a precise diagnostic before MIR. User-facing docs and
            full-pipeline invalid-boundary tests now state that boundary
            explicitly.
    - [x] Finish target-typed object creation, dynamic storage creation,
          constructor object creation, explicit constructor body lowering, enum
          constructor/value lowering, array initializer lowering, and field
          access lowering (`FunctionMirBuilder.cs:3828-4610`).
      - [x] 2026-05-12 compiler-generated constructor temporaries now have a
            concrete backend storage meaning. MIR still uses `temp` as an
            internal scratch-local marker during lowering/validation, but SSA
            lowers it to stack storage so optimization and LLVM emission never
            see an invalid backend storage class. This fixes accepted constructor
            paths such as `Dictionary<u32,u32>()` in benchmark sources without
            adding a dictionary-specific workaround.
      - [x] 2026-05-12 aggregate move-snapshot emission now keeps moved-from
            source bytes available when a later store/call/return is deliberately
            address-forwarded from a prior local-load snapshot. LLVM emission
            suppresses only the non-observable moved-from `undef` store needed
            for validation when a later aggregate address copy still needs that
            storage, while retaining lifetime/drop semantics. Regression
            coverage keeps `DictionaryInsert` compiling to object code and
            verifies the constructor path does not fall back to whole-dictionary
            value insert/store emission.
      - [x] 2026-05-12 source-body object/constructor/enum cleanup: target-typed
            object creation, object initializer members, constructor argument
            binding, primary constructor field mapping, enum constructor/value
            layout facts, published enum call/value shapes, array initializer
            target facts, and field access assumptions now require the facts
            produced by type checking/enum layout. Missing or contradictory
            facts fail as lowering invariants instead of marking the function as
            unsupported.
    - [x] Finish slice, fixed-array, raw-pointer, dynamic-storage, and text
          indexing/slicing lowering (`FunctionMirBuilder.cs:4722-4966`).
      - [x] LLVM emission now recovers conservative slice length lower bounds
            through local slice loads by inspecting all stores to that slice
            local and taking the minimum known stored length. This restores
            `inbounds nuw` on slice element GEPs for fixed-array-backed views
            and branch-joined slice locals without weakening bounds: if any
            store has unknown length, the GEP remains unflagged.
      - [x] SSA value facts now recover known slice length ranges through
            mutable local slice loads by joining all reachable stores to that
            local. This keeps bounds and backend metadata facts available for
            common `stack mut T[] view = left; if (...) view = right;` patterns
            without assuming a single store or path-sensitive exact value.
      - [x] 2026-05-12 indexing cleanup: source-body and imported-template
            fixed-array, raw-pointer, slice, text, and dynamic-storage indexing
            guards no longer produce unsupported MIR. Invalid arity/type
            combinations are handled by earlier diagnostics where covered; if an
            accepted program reaches MIR without index kind/type/address facts,
            lowering fails as a compiler invariant.
    - [x] Finish text switch literal comparisons and text switch component
          extraction (`FunctionMirBuilder.cs:7076`, `FunctionMirBuilder.cs:7124`,
          `FunctionMirBuilder.cs:7157`, `FunctionMirBuilder.cs:7313`).
      - [x] 2026-05-12 switch cleanup: native switch, partitioned text switch,
            and guarded switch lowering now exhaust their supported paths without
            falling back to generic unsupported MIR. Non-literal text switch
            arms or missing text component extraction facts are lowering
            invariants because type/switch analysis must settle them before MIR.
    - [x] Finish address-model place reads and aggregate path assignments
          (`PlaceLowerer.cs:848`, `PlaceLowerer.cs:862`, `PlaceLowerer.cs:874`,
          `PlaceLowerer.cs:904`, `PlaceLowerer.cs:927`).
      - [x] Mutable global field/index assignments now use direct projection
            addresses, so `Current.Right = value` and `Values[i] = value` lower
            to field/element stores instead of whole-aggregate load/insert/store
            rewrites. Immutable globals intentionally keep value-style loads so
            LLVM can retain `local_unnamed_addr` and invariant metadata.
      - [x] Large local/parameter aggregate projections now use address-model
            reads and writes once the concrete root layout is at least 128
            bytes. This keeps small structs and small fixed arrays on the
            scalar/SROA-friendly value path, but lowers large fixed buffers and
            large structs with inline buffers through `address-of` +
            field/element GEPs and scalar load/store. Regression coverage checks
            MIR for `i8[256]` and `struct { i8[256]; ... }` constant-index
            reads/writes, and LLVM IR for the fixed-array case to ensure it
            emits scalar projection access instead of whole `[256 x i8]`
            load/store.
      - [x] `PlaceLowerer` no longer contains `MarkUnsupported` fallbacks.
            Malformed rootless value places, missing address-model roots, and
            failed aggregate-path rewrites now fail as lowering invariant
            violations with a rendered place path instead of producing partial
            MIR with null assignment targets.
  - [x] Imported-template lowering gaps: bring package/imported typed template
        bodies to parity with source-body lowering.
    - [x] Finish imported text postfix bracket lowering, dynamic fixed-array
          indexing, general imported indexing, and imported dynamic storage
          indexing (`ImportedTemplateLowerer.cs:1782`,
          `ImportedTemplateLowerer.cs:1842`, `ImportedTemplateLowerer.cs:1937`,
          `ImportedTemplateLowerer.cs:1951`).
      - [x] 2026-05-12 imported indexing cleanup: package-image typed-template
            indexing now consumes recorded/published operation facts and treats
            missing text/index/dynamic-storage facts as lowering invariants
            rather than unsupported imported-body lowering.
    - [x] Finish imported typed-template binary and conditional lowering common
          type/integer checks (`ImportedTemplateLowerer.cs:2203`,
          `ImportedTemplateLowerer.cs:2210`, `ImportedTemplateLowerer.cs:2432`).
      - [x] 2026-05-12 imported binary/conditional cleanup: typed-template
            binary and conditional lowering now requires the published common
            type/integer facts that type checking recorded. Missing facts are
            compiler invariants, preventing null operands from entering MIR.
    - [x] Finish imported enum call, enum constructor, enum value, and primary
          constructor object creation lowering (`ImportedTemplateLowerer.cs:2620`,
          `ImportedTemplateLowerer.cs:2661`, `ImportedTemplateLowerer.cs:2674`,
          `ImportedTemplateLowerer.cs:2697`, `ImportedTemplateLowerer.cs:2719`,
          `ImportedTemplateLowerer.cs:2737`, `ImportedTemplateLowerer.cs:2747`).
      - [x] 2026-05-12 imported object/enum cleanup: imported typed-template
            object initializers, primary constructors, enum calls,
            constructors, and values now require published layout/member/payload
            facts and lower directly or fail as compiler invariants.
  - [x] Backend correctness guards in MIR lowering: keep the guard only if it
        protects an invariant that should never be reachable after earlier
        validation, and add tests for the earlier validation.
    - [x] Validate dynamic storage length update invariants before runtime-drop
          lowering (`RuntimeDropLowerer.cs:635`, `RuntimeDropLowerer.cs:650`,
          `RuntimeDropLowerer.cs:676`).
      - [x] Runtime-drop lowering no longer logs `unsupported-lowering` when
            dynamic-storage length update/commit address construction fails.
            Those paths now throw lowering invariant violations with the
            assignment text, because an accepted dynamic-storage initialization
            must have a concrete owner address and computable initialized
            length before runtime-drop cleanup is emitted.
      - [x] 2026-05-12 validation cleanup: the dynamic initialization-length
            update builder now distinguishes "not a dynamic-storage init
            target" from "recognized dynamic-storage init target with missing
            compiler facts." Direct `init owner[index] = value` and
            init-slice-derived `init spare[index] = value` updates still use the
            fast direct length commit path, but a missing owner address or
            initialized-index temporary is now a lowering invariant. The
            compiler must never silently skip the length commit for an accepted
            dynamic init assignment, because that would make drops/freeing walk
            the wrong initialized prefix.
    - [x] Validate concrete layout availability before lowering `sizeof`,
          `alignof`, runtime `disjoint`, and memory ranges
          (`FunctionMirBuilder.cs:2497`, `FunctionMirBuilder.cs:3800`).
      - [x] 2026-05-12 backend-fallback cleanup: `sizeof`/`alignof` lowering
            now requires a concrete runtime layout and throws a lowering
            invariant violation instead of logging `unsupported-lowering` when
            the accepted typed expression reaches MIR without one. Regression
            coverage verifies concrete scalar `sizeof` and `alignof` lower to
            integer constants. The remaining work in this item is an earlier
            validation pass for non-materialized generic or otherwise
            non-concrete layout expressions before MIR.
      - [x] 2026-05-12 validation completion: semantic validation now rejects
            non-generic non-concrete layout requests such as `sizeof(Trait)`
            before MIR with a concrete-layout diagnostic. Open generic layout
            expressions remain allowed in generic templates so
            `sizeof(T)`/`alignof(T)` can lower after concrete instantiation;
            LLVM regression coverage verifies the instantiated generic path
            folds to integer constants without backend fallback. Imported
            typed-template layout expressions now use the same contract: after
            generic substitution, a missing concrete layout is a lowering
            invariant instead of a null-return fallback.
    - [x] Validate call result value usage so void direct, member, and indirect
          calls are never lowered as value expressions (`FunctionMirBuilder.cs:3278`,
          `FunctionMirBuilder.cs:3319`, `FunctionMirBuilder.cs:3520`,
          `FunctionMirBuilder.cs:3594`).
      - [x] 2026-05-12 void-call value-use coverage: type-checking already
            rejects `void` call results in value contexts through assignment and
            return compatibility checks. Focused regression coverage now proves
            direct, member, and indirect `fn void` calls used as `i32` return
            values fail before MIR, with fallback-log assertions so the MIR
            guards remain compiler invariants rather than user-facing fallback
            behavior.
  - [x] Replace semantic-error-shaped lowering guards with earlier parser,
        type-checking, semantic, ownership, or lowering-contract diagnostics,
        including invalid constructor returns, invalid call arity, invalid
        indexing arity, invalid dynamic-storage operation shapes, unresolved
        names, void calls used as values, and impossible initializer shapes.
    - [x] 2026-05-12 coverage pass: the focused invalid-boundary table now
          proves constructor value returns, direct/member call arity failures,
          void direct/member/indirect calls used as values, raw-slice arity,
          empty/non-integer/over-arity indexing, dynamic-storage receiver and
          argument shape failures, unresolved names, function-as-value misuse,
          fixed text buffer initializer-shape errors, and dynamic storage object
          initializer syntax all fail before MIR and without lowering/backend
          fallback logs.
    - [x] 2026-05-12 diagnostic cleanup: dynamic storage creation with an object
          initializer now emits a dedicated type-check diagnostic explaining the
          supported forms `new()` and `new(capacity)`, instead of reaching the
          generic object-initializer path or relying on the MIR invariant guard.
          Remaining initializer shape impossibilities are parser grammar facts
          (`variableInitializer` is exactly expression, object initializer, or
          array initializer) plus type-check diagnostics for invalid target
          kinds such as fixed text buffers initialized with aggregate syntax.
  - [x] For every user-facing construct that is intended to be supported before
        release, remove the `MarkUnsupported` path by implementing direct MIR,
        SSA, LLVM, package-image, and imported-template lowering instead of
        relying on declaration-only fallback.
    - [x] 2026-05-12 package-image typed-template retborrow gap closed:
          generic functions returning `retborrow` now publish typed bodies
          instead of forcing `TypedBody = null` and preserving source body text.
          Manifest-backed consumers can instantiate a generic returned-borrow
          body such as `retborrow T Get<T>(borrow Box<T> box) { return
          box.Value; }` even when the package source body text is corrupted or
          absent. MIR lowering now treats a present imported typed-template body
          as an executable contract: if it cannot lower directly, it raises a
          lowering invariant violation instead of falling through to
          `missing-function-body`/declaration-only fallback.
    - [x] 2026-05-12 section-0 closeout: this item is complete for the
          `MarkUnsupported` family specifically. The remaining release work is
          tracked under explicit accepted-program contract, bound-operation, and
          LLVM/backend fallback tasks instead of this broad umbrella.
  - [x] Remove the “intentionally unsupported accepted construct” category.
      Every construct representable by Stark grammar must either:
      1. be fully supported through parsing, typing, semantic/ownership validation,
         MIR, SSA, LLVM, package image, and imported-template lowering; or
      2. be rejected as a semantically invalid program before MIR with a precise
         diagnostic.

      If a syntactic form is not intended to ever be supported, remove it from the
      grammar instead of keeping it as an accepted-but-unsupported construct.
    - [x] 2026-05-12 diagnostic wording cleanup: pre-MIR rejections for invalid
          switch domains, enum whole-value switch captures, enum object creation,
          capturing `fnptr` lambdas, open-generic lambda targets, local
          `arena`/function-local `static` storage, runtime text transcoding
          casts, and enum-dependent runtime layouts now describe stable language
          rules instead of implementation-status wording.
          The user-facing language reference now describes the same rules for
          `fnptr` lambdas, `arena`, and function-local `static` storage.
    - [x] 2026-05-12 C ABI aggregate cleanup: nested raw pointer types remain
          rejected for ordinary Stark storage, but are now explicitly valid on
          `ffi` parameter/return boundaries and on fields of `[Platform]` ABI
          aggregates. This keeps safe Stark aggregates from quietly carrying
          pointer-to-pointer shapes while allowing C layout structs such as
          Raylib `ModelAnimation` and `FilePathList` to compile through package
          builds.
    - [x] 2026-05-12 Raylib wrapper unsafe-boundary cleanup: generated public
          wrappers that call internal `unsafe ffi` shims now isolate the raw
          pointer/address-of operation inside explicit `unsafe { ... }` blocks.
          The public wrapper signatures remain safe where their Stark value
          parameters uphold the C ABI contract; the implementation-detail FFI
          boundary no longer depends on accepted safe code performing unsafe
          operations implicitly.

  - [x] Add compiler tests that fail if normal standard-library, example,
        package-image, or benchmark builds log `unsupported-lowering`,
        `llvm-body-fallback`, `llvm-asm-fallback`, `llvm-body-pending`, or
        `missing-function-body`.
    - [x] Benchmark source gate.
    - [x] Representative example source gate.
    - [x] Package-image-backed imported generic gate.
      - [x] 2026-05-12 the package-image gate now checks both the library build
            that creates the package manifest and the manifest-backed consumer
            build that imports and instantiates it, using the same fallback-log
            event set as the benchmark, example, and standard-library gates.
    - [x] Fast standard-library source/package gate.
    - [x] 2026-05-12 integration verification under the stricter fallback gate:
          `compiler.IntegrationTests` passes in `Release` after the Raylib
          unsafe-boundary cleanup. The `System.Collections` ThinLTO CLI test now
          also records the reachable-dependency behavior: the List hot path emits
          `root.ll` and `System_Collections.ll` with ThinLTO and does not emit a
          transitive `System_Memory.ll` object when no reachable function body
          needs it. A real native link/run of that source exits with the expected
          value, so the assertion is a compile-time bloat guard rather than a
          fake-link workaround.
  - [x] Add focused regression tests for each remaining supported lowering path
        so unsupported/fallback logs cannot reappear silently.
    - [x] 2026-05-12 added LLVM regression coverage for indirect function-pointer
          calls with `borrow`, `out`, and `init` parameters, plus large by-value
          aggregate parameters and large sret return values. These tests assert
          pointer/`byval`/`sret` ABI calls are emitted and no LLVM body fallback
          appears for the indirect-call body.
    - [x] 2026-05-12 added LLVM regression coverage for direct and indirect
          large-aggregate sret forwarding. `return Make()` and
          `return op(...)` now assert that the callee receives the caller's
          `%ret` buffer directly, with no `%abi_callret_slot_*`,
          `%abi_indirect_callret_slot_*`, or copy from a temporary result slot in
          the forwarding body. Coverage also locks the immediate local wrapper
          shape `stack Big value = Make(); return value;` so local aliasing keeps
          the call in `%ret` instead of reintroducing a large copy.
    - [x] 2026-05-12 added LLVM regression coverage for non-addressable
          fixed-array dynamic indexing. `Make(...)[index]` now asserts direct
          LLVM body emission, a single materialized fixed-array source slot, and
          dynamic element GEP/load lowering.
    - [x] 2026-05-12 closeout: all supported lowering paths identified by the
          `MarkUnsupported` audit now have either focused regression coverage or
          are covered by the benchmark/example/package/stdlib no-fallback gates.
          New gaps should be added as concrete regression tasks under the
          relevant feature area, not kept under an open-ended "remaining paths"
          parent.

- [x] Close the MIR lowering gaps found in the pre-release audit.
  - [x] Fixed text storage: implement or deliberately reject every current
        fallback in fixed text storage initialization, concatenation,
        interpolation, formatter lookup, formatter calls, and `System.Text`
        helper binding.
  - [x] Initializers and assignments: finish MIR lowering for object
        initializers, array initializers, variable initializers, assignment
        expressions, compound assignments, pointer assignments, expression
        statements, and conditional expressions that the language accepts.
  - [x] Runtime `disjoint(...)`: finish direct lowering and diagnostics for
        missing conditions, operand count, raw pointer regions, contiguous views,
        addressability, byte-range construction, and concrete-layout failures.
  - [x] Slices and indexing: finish direct lowering and diagnostics for
        `slice(ptr, count)`, fixed-array dynamic indexing, slice indexing, raw
        pointer indexing, dynamic storage indexing/slicing, and ascii/unicode
        indexing/slicing.
    - [x] 2026-05-12 completion note: source and imported fixed-array, slice,
          raw-pointer, dynamic-storage, and text indexing/slicing now have direct
          lowering or earlier diagnostics. Non-addressable fixed-array dynamic
          indexing ships by spilling only the temporary source when required,
          preserving direct address paths for locals, parameters, globals, and
          mutable places.
  - [x] Dynamic storage: finish lowering and diagnostics for dynamic creation,
        length commits, `Reserve`, `TryReserve`, `TryReserveCapacity`,
        `MoveLast`, `MoveAt`, and runtime-drop length updates.
    - [x] Dynamic-storage operation receiver and argument-shape diagnostics now
          fail in type checking before MIR lowering. This covers the front-end
          half of `Reserve`/`TryReserve`/`TryReserveCapacity`/`MoveLast`/`MoveAt`;
          remaining unchecked work in this task is runtime-drop length invariant
          validation and any lowering-contract cleanup around length commits.
    - [x] Large dynamic-storage `MoveLast`/`MoveAt` results now use
          address-backed move slots instead of LLVM first-class aggregate
          loads. Large moves returned directly copy from the dynamic element
          into the caller `%ret` buffer; large moves assigned immediately into a
          stack local, out parameter, or global copy directly into that
          destination; `MoveAt` copies the moved element before tail `memmove`
          shifts remaining elements. Raw-pointer element stores intentionally
          keep the temporary result path unless the backend can prove disjoint
          storage, because unsafe raw pointers may alias the dynamic storage
          being shifted. Small scalar moves keep the existing load/phi lowering.
          LLVM regressions assert no `load %Big`, `store %Big`, or `phi %Big`
          appears for the proven-disjoint large move paths.
    - [x] 2026-05-12 completion note: dynamic creation, reserve/try-reserve,
          exact-capacity reserve, direct init length commits, init-slice length
          commits, `MoveLast`, `MoveAt`, and dynamic runtime-drop cleanup now
          either lower directly or fail earlier/invariantly with the required
          receiver, index, address, element-layout, and length facts. The
          remaining sparse-slot proof work is a future language feature, not a
          fallback path for the dense-prefix dynamic storage model.
  - [x] Calls and function values: finish function-pointer target typing,
        non-capturing lambda records, named function values, direct/member call
        binding diagnostics, and indirect function-pointer calls with
        borrow/out/init ABI metadata.
    - [x] Indirect function-pointer calls with borrow/out/init ABI metadata now
          lower through MIR, SSA, optimization, and LLVM emission. Correctness
          coverage includes pointer parameters, `byval` aggregate parameters,
          and sret aggregate returns.
    - [x] Immediate large-aggregate sret call returns now forward the current
          function's return buffer directly for both direct calls and indirect
          function-pointer calls. This removes the previous
          call-result-slot-plus-memcpy sequence for wrappers such as
          `unsafe fn Big Forward() { return Make(); }` and
          `unsafe fn Big Apply(fnptr<fn Big(...)> op, ...) { return op(...); }`.
          The existing local alias path is now covered for
          `stack Big value = Make(); return value;` as part of the same
          performance contract.
  - [x] Object, constructor, and enum construction: finish direct lowering for
        target-typed creation, resolved constructors, explicit constructor body
        availability, primary constructors, enum constructors, enum values, and
        named/positional enum payloads.
  - [x] Switch lowering: finish or precisely reject every switch shape outside
        native integer/bool, partitioned text, and guarded switch lowering.
  - [x] Place/address lowering: finish address-model reads, aggregate path
        updates, rootless places, and fallback paths in `PlaceLowerer`.
  - [x] Imported typed template bodies: bring imported-template expression,
        indexing, dynamic storage, binary/conditional, object, enum, and
        constructor lowering to parity with source-body lowering.

- [x] Close LLVM/backend fallback paths.
  - [x] Audit every `UnsupportedBodyEmissionException` in LLVM emission and
        classify it as invalid SSA, missing backend support, ABI metadata gap, or
        intentional diagnostic.
    - [x] 2026-05-12 first audit classification: optimized raw-pointer loop
          guards, unsupported SSA instruction/rvalue/value/terminator variants,
          missing dynamic-layout facts, missing call ABI metadata, indirect-call
          ABI metadata gaps, and malformed address/slice shapes are compiler
          invariants or SSA-validation gaps. FFI text-view returns are source
          ABI diagnostics, not backend fallbacks: C/platform boundaries must
          return raw pointer plus explicit length/status and wrap that result in
          Stark code.
    - [x] Audit optimized raw-pointer loop emission exceptions in
          `LlvmFunctionBodyEmitter`: failed intrinsic emission, missing loop base
          pointer, and unknown address-of parameter. Each site must become either
          SSA/optimization validation or a backend invariant with a focused
          positive codegen test.
      - [x] 2026-05-13 address-of-parameter ABI validation step:
            `validate-ssa` now requires every direct-codegen SSA function to
            have current-function ABI lowering and requires every
            `SsaAddressOfParameterRValue` to reference an ABI user parameter,
            not only an SSA parameter. This moves the raw-loop/address-emission
            "unknown address-of parameter" backend exception into `STK5002`
            validation. Existing focused LLVM tests cover accepted optimized
            raw-pointer `memcpy`, `memmove`, and `memset` emission for whole
            functions and embedded loops.
      - [x] Classify the remaining optimized raw-pointer intrinsic guards:
            failed intrinsic emission and missing base pointer should either be
            proven unreachable by raw-loop plan construction/SSA validation or
            converted to explicit backend invariants with tests that exercise the
            positive plan shapes.
        - [x] 2026-05-13 closure note: once a raw-pointer loop is matched as an
              optimized memcpy/memmove/memset plan, LLVM emission now commits to
              that plan instead of returning `false` and silently falling back to
              scalar loop emission. The plan is validated at the emission
              boundary for destination/source/fill completeness, concrete
              positive element layout, representable non-negative i64 byte
              length, and i8-shaped memset fill values. Invalid plans now fail
              as explicit backend invariants (`InvalidOperationException`) with
              function name, intrinsic kind, and reason, not as
              `UnsupportedBodyEmissionException`/`llvm-body-fallback`.
              The optimized-loop address path also treats an unknown ABI
              parameter as an invariant; `validate-ssa` remains the normal gate
              that prevents accepted programs from reaching that state. Focused
              LLVM tests cover whole-function and embedded optimized raw-pointer
              memcpy, memmove, and memset emission, including slice-backed init
              paths and nontrivial functions.
    - [x] Audit the SSA instruction/rvalue/value/terminator catch-all exception
          sites and record the exact concrete SSA node types that are expected
          before release. Add one validator or LLVM-emission test per accepted
          concrete type.
      - [x] 2026-05-13 catch-all validation step: `validate-ssa` now explicitly
            rejects unknown `SsaInstruction`, `SsaRValue`, `SsaValue`, and
            `SsaTerminatorKind` shapes before LLVM emission. Regression tests
            inject synthetic unsupported instruction, rvalue, value, and
            terminator nodes and verify `STK5002` diagnostics, so future SSA
            node additions cannot silently route to LLVM body fallback without
            updating validation/emission coverage.
    - [x] Audit direct and indirect call ABI exception sites, including missing
          ABI lowering, user/ABI argument count mismatches, unsupported ABI
          parameter kinds, missing sret/byval metadata, and function-pointer
          ABI metadata. Move invalid shapes to `validate-ssa`; add positive
          tests for every supported ABI shape.
      - [x] 2026-05-13 ABI argument-shape validation step: `validate-ssa` now
            checks direct and indirect call ABI argument metadata before LLVM
            emission. Direct ABI parameters reject stray indirect argument
            addresses/promoted locals; indirect ABI parameters validate that
            supplied address metadata is a raw pointer to the source parameter
            shape; promoted locals must resolve to a current SSA local or current
            function ABI parameter with the expected source shape; extra
            indirect metadata slots and vararg indirect metadata are rejected.
            Indirect function-pointer calls build the same synthetic ABI used by
            LLVM emission, so large by-value aggregate parameters, borrow/out/init
            parameters, and direct scalar parameters are validated under the
            same direct-vs-indirect ABI decision the backend will use. Focused
            tests cover direct-parameter metadata misuse, indirect address
            pointee mismatch, unknown promoted locals, and indirect-call
            aggregate byval address mismatch.
      - [x] Add the remaining positive ABI-shape matrix tests for accepted direct
            calls and indirect function-pointer calls: direct scalars, FFI text
            arguments lowered to raw data pointers, sret aggregate returns,
            byval aggregate parameters, borrow/out/init indirect parameters,
            promoted current-parameter addresses, promoted local addresses,
            forwarded aggregate source addresses, and void statement calls.
        - [x] 2026-05-13 closure note: existing LLVM tests already covered
              indirect function-pointer direct scalar calls, borrow/out/init
              pointer ABI calls, large byval parameters, large sret returns,
              direct aggregate sret/byval calls, FFI ascii arguments lowered to
              raw data pointers, and direct current-parameter aggregate
              forwarding without aggregate loads. Added the missing direct-call
              positive cases: direct void statement calls with `out` storage,
              large aggregate temporaries constructed directly into a byval call
              slot without a user local slot or `%Big` load/store, and large
              current-parameter byval forwarding to a scalar-return callee
              without a call-argument temporary. Added `validate-ssa` positives
              for accepted indirect argument addresses, promoted locals,
              promoted current parameters, and indirect function-pointer large
              byval address metadata so the stricter ABI validator cannot
              regress supported call shapes.
    - [x] Audit slice, text, address, and memory-copy exception sites, including
          slice creation from unsupported roots, missing raw-pointer element
          types, bad dynamic element addresses, text operation type mismatches,
          and unlowered string constants. Decide for each site whether the
          source is invalid, SSA is malformed, or backend support is missing.
      - [x] 2026-05-13 closure note: slice/address/text/memory-copy malformed
            shapes now fail in `validate-ssa` before LLVM body emission. Existing
            validation already rejected non-fixed-array local slice roots,
            non-raw slice pointers, missing/both element-address indices,
            non-integer slice/text/dynamic indices, non-slice slice-element
            operands, and missing slice-element raw-pointer pointees. This pass
            added the remaining contracts: `SsaCopyMemoryInstruction`
            destination/source addresses must be raw pointers to the copied type
            and the copy type must have a concrete non-empty layout; whole fixed
            array copies may still use raw pointers to the first element, which
            matches current fixed-buffer lowering and avoids rejecting valid
            optimized memcpy sites. String constants and text-data-address
            literals must be `ascii`/`unicode`;
            text-data-address results must be raw pointers to `i8`/`i32` text
            units; global addresses must be raw pointers to their recorded
            pointee type; function addresses must carry complete function-pointer
            metadata; dynamic reserve/try-reserve/move operations must receive a
            raw pointer to the declared dynamic storage type; and element/field
            addresses must receive raw-pointer bases matching the aggregate
            type, with element-address results matching the array element or
            pointer element shape. Focused `SsaIrValidationTests` cover each new
            negative shape plus accepted qualified-pointee stores, so these
            backend guard paths no longer define the accepted program set.
    - [x] Audit `LlvmBuiltinAndHelperEmitter` exception sites for text
          equality/comparison, fixed-array ordered comparison, System.Memory,
          System.Collections, System.Runtime, System.Math, and
          System.BitOperations helpers. Each helper should have a contract test
          for accepted signatures and a pre-backend diagnostic/invariant for
          invalid signatures.
      - [x] 2026-05-13 ordered aggregate comparison validation step:
            malformed SSA can no longer reach fixed-array or scalarized
            named-aggregate ordered-comparison helpers with unsupported element
            or field types. `validate-ssa` now mirrors the helper-supported leaf
            set for ordered comparisons: concrete integers/floats, bool,
            raw pointers, ascii/unicode text, recursively comparable fixed
            arrays, and concrete struct/record/enum layouts whose fields are
            recursively comparable. Slice-bearing fixed arrays or named
            aggregates are rejected with `STK5002` before helper emission.
            Focused tests cover a fixed array of slices and a named struct with
            a slice field, which previously could fall through to backend helper
            exceptions or binary-emission fallback if malformed SSA was injected.
      - [x] 2026-05-13 System builtin signature validation step:
            `validate-ssa` now validates compiler-owned `System.Math`,
            `System.BitOperations`, `System.Memory`, `System.Collections`, and
            `System.Runtime` builtin signatures before LLVM helper emission,
            including arity, scalar width, aggregate return shape, required
            borrow/mutability qualifiers, supported dictionary key shapes,
            direct scalar ABI parameters, and supported target architecture for
            single-instruction math builtins. Malformed declarations now report
            `STK5002` in `validate-ssa` instead of reaching
            `LlvmBuiltinAndHelperEmitter` exceptions. The audit also found that
            the `System.Collections.List.AsSlice`/`AsMutableSlice` helper still
            assumed the old `Data`/`Length` list layout; the helper now accepts
            the current `dynamic T Items` layout and emits direct data/length
            loads from that field, preserving the optimized path if an ABI-only
            specialization needs it. Imported package-image generic templates
            whose signatures still contain open placeholder types such as `T`
            are skipped until a concrete specialization exists; concrete
            package/imported specializations are validated with the same rules.
            Focused tests cover invalid Math, BitOperations, Memory,
            DictionaryKey, Runtime byte-slice contracts, plus an accepted
            BitOperations signature. Existing text equality/comparison and
            fixed-array/named-aggregate comparison SSA validation now prevents
            malformed text/ordered-comparison helper requests from defining
            backend validity.
  - [x] Ensure supported SSA instructions, rvalues, terminators, calls,
        indirect calls, slices, text operations, dynamic storage operations, and
        address forms emit LLVM without throwing `UnsupportedBodyEmissionException`.
    - [x] 2026-05-12 compiler-generated `temp` locals are normalized to stack
          storage before LLVM emission, eliminating the backend error for
          invalid local storage classes in accepted constructor/object-creation
          paths.
    - [x] 2026-05-13 numeric conversion/unary contract tightening: SSA
          validation now rejects non-concrete integer conversions, unsupported
          float conversion widths, unsized integer unary negation/bitwise-not,
          and unsupported float unary negation before LLVM body emission. This
          closes the remaining gap where malformed typed SSA could satisfy the
          broad conversion/operator kind checks but still produce invalid LLVM
          spellings such as `i` or hit backend float-width invariants. Focused
          tests cover unsized integer conversion, unsupported `f24` conversion,
          unsized integer unary negation, and unsupported `f24` unary negation.
    - [x] 2026-05-13 native switch contract tightening: SSA validation now
          rejects switch terminators whose condition is not `bool` or a concrete
          integer and rejects case match values whose LLVM value shape differs
          from the condition. This protects LLVM switch emission from text,
          float, raw-pointer, unsized-integer, or mixed-shape cases that should
          have been lowered to guarded branches earlier. Focused tests cover
          accidental text switch emission, unsized integer switch conditions,
          and mismatched integer/bool case values.
    - [x] 2026-05-13 `SsaUseRValue` direct lowering fix: LLVM emission now
          records a value alias instead of materializing `use` as
          `add <type> value, 0`. The old lowering was only valid for integer
          shapes and introduced needless instructions even there; the alias path
          is zero-instruction, preserves the original value spelling, and works
          for floats, raw pointers, text/aggregate constants, function/global
          addresses, and integer values. Focused emission tests cover float,
          raw-pointer, and `%stark_ascii` aggregate `use` values and assert that
          no invalid `add` instruction is emitted.
    - [x] 2026-05-13 function-address contract tightening: SSA validation now
          requires `SsaFunctionAddressValue` targets to have ABI lowering and
          verifies that function-pointer return/parameter metadata matches the
          target source signature shape. This prevents accepted SSA from
          emitting undeclared function symbols or calling through stale
          function-pointer metadata. Focused tests cover missing ABI targets,
          return/parameter mismatches, and the accepted large-byval indirect
          call case with an explicit callee ABI.
    - [x] 2026-05-13 global access contract tightening: SSA validation now
          validates `SsaLoadGlobalRValue`, `SsaGlobalAddressValue`, and
          `SsaStoreGlobalInstruction` against known global facts before LLVM
          emission. Visible globals use the type-check model for exact type and
          mutability checks; loaded-module parse/package facts also register
          root and imported/private globals so inline clones can reference
          internal constants without being rejected. Loads and global-address
          pointees must match the known global type when available, global stores
          require a mutable target, and stored values must match the declared
          global value shape. Focused tests cover unknown globals, mismatched
          loads, mismatched address pointees, stores to `const`, mismatched store
          values, and the accepted mutable-global store path.
    - [x] 2026-05-13 current-function ABI contract tightening: SSA validation
          now checks the function's own ABI signature before LLVM body emission,
          including source return shape, direct LLVM return shape, pointer-backed
          `retborrow` returns, sret return-buffer shape, user-parameter count,
          direct parameter LLVM shape, and indirect/byval parameter pointer
          shape. This prevents accepted SSA from reaching body emission with a
          stale or contradictory ABI model that would otherwise produce invalid
          signatures, missing sret buffers, or incorrect parameter materializing.
          Focused tests cover return mismatches, parameter count/type
          mismatches, bad sret buffers, accepted sret returns, and accepted
          pointer-backed `retborrow` returns.
    - [x] 2026-05-13 SSA control-flow structural contract tightening:
          `validate-ssa` now rejects duplicate SSA value definitions and
          malformed PHI nodes before LLVM body emission. PHIs must have at least
          one incoming value, no duplicate predecessor entries, only incoming
          blocks that actually branch to the PHI block, an incoming value for
          every actual predecessor, and incoming value shapes matching the PHI
          result. Focused tests cover duplicate result names, type mismatches,
          duplicate incoming entries, non-predecessor incoming blocks, missing
          predecessor incoming values, and empty PHIs.
    - [x] 2026-05-13 aggregate/view value structural contract tightening:
          `validate-ssa` now checks `extractvalue`/`insertvalue`-style field and
          index rvalues before LLVM body emission. Named/dynamic field accesses
          must use a valid field index and matching field name; slice and
          ascii/unicode view field/index accesses must match the `{ data, length }`
          layout; fixed-array index accesses must stay within the known length;
          insertion result shapes must match the target aggregate; and
          extracted/inserted values must match the selected field or element
          shape. Focused tests cover field result mismatches, field-name/index
          contradictions, fixed-array index bounds, inserted element type
          mismatches, and accepted slice/text view extraction by index or field.
    - [x] 2026-05-13 SSA cleanup CFG/PHI repair: cleanup now prunes PHI
          incoming entries whose predecessor no longer has a live CFG edge to
          the PHI block. This keeps branch pruning, trampoline collapse, linear
          block merging, and unreachable-block pruning from leaving stale PHI
          predecessors that later validate as malformed SSA or emit invalid
          LLVM. Focused optimizer coverage injects a stale incoming edge and
          proves cleanup removes it before validation/codegen.
    - [x] 2026-05-13 no-op conversion lowering fix: same-width integer
          conversions, same-width float conversions, and raw-pointer-to-raw-
          pointer conversions now use the backend alias path instead of
          materializing `add`, `fadd`, or `select` instructions. This removes
          unnecessary IR, keeps opaque pointer casts as true no-ops, and avoids
          changing exact floating-point copy semantics. Focused tests cover all
          three no-op conversion classes, plus the source-level raw-pointer loop
          cast case that previously expected a `select` artifact. The alias map
          is precomputed before body emission, rather than populated while
          walking blocks, so PHI incoming values can reference aliases defined
          in predecessor blocks without producing undefined `%v...` operands.
    - [x] Build an emitter coverage matrix from the audited SSA node inventory:
          every accepted `SsaInstruction`, `SsaRValue`, `SsaValue`, and
          `SsaTerminator` concrete type must have at least one direct LLVM
          emission test or be rejected by validation before emission.
      - [x] 2026-05-13 executable coverage matrix added in
            `tests/compiler.Tests/SsaEmitterCoverageMatrixTests.cs`. The test
            reflects the compiler assembly for every concrete `SsaValue`,
            `SsaRValue`, `SsaInstruction`, the concrete `SsaTerminator`, and
            each `SsaTerminatorKind`; it fails if a node is missing from the
            audited matrix or if a row points at a stale/non-test method. This
            keeps future SSA additions from silently depending on backend
            fallback behavior.
    - [x] Add positive LLVM tests for optimized raw-pointer loops, dynamic
          storage runtime helpers, slice/text operations, memory copy/move/set,
          and address forms that currently rely on backend assumptions.
      - [x] 2026-05-13 positive backend-assumption matrix rows now pin the
            existing LLVM tests for optimized raw-pointer loops, dynamic
            storage runtime helpers, slice/text operations, memory
            copy/move/set, and address forms. A focused positive LLVM test was
            added for integer/boolean unary lowering so integer negation,
            logical-not, and bitwise-not all have direct emission coverage
            instead of only validation coverage.
    - [x] Add positive LLVM tests for direct calls, imported calls,
          monomorphized calls, FFI calls, indirect function-pointer calls,
          sret returns, large `byval` parameters, borrow/out/init arguments,
          and void statement calls.
      - [x] 2026-05-13 positive backend-assumption matrix rows now pin direct
            calls, imported calls, source-backed and package-image-backed
            monomorphized calls, FFI/varargs calls, indirect function-pointer
            calls, sret returns, large `byval` parameters, borrow/out/init
            arguments, and direct void statement calls to concrete LLVM tests.
            The matrix verifies these named tests still exist so call ABI
            coverage cannot drift silently.
    - [x] Run the benchmark/example/package/stdlib no-fallback gates after the
          coverage matrix lands and record the `Release` command results here.
      - [x] 2026-05-13 verification:
            `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj --filter "FullyQualifiedName~BenchmarkSourceTests.BenchmarkSourcesCompile|FullyQualifiedName~ExampleSourceTests.RepresentativeExampleSourcesEmitLlvmWithoutFallbackLogs|FullyQualifiedName~StandardLibrarySourceTests.StandardLibraryRootEmitsLlvmWithoutFallbackLogs|FullyQualifiedName~PackageImageCallableValueTests.PackageImageBackedAcceptedProgramEmitsLlvmWithoutFallbackLogs"`
            passed: `4` passed, `0` failed, `0` skipped, duration `48 s`.
            Full compiler suite also passed with the coverage matrix included:
            `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj`
            passed: `1254` passed, `0` failed, `0` skipped, duration `51 s`.
  - [x] Convert invalid SSA-shape exceptions into earlier MIR/SSA validation
        errors with tests, so backend fallback is not the first user-facing
        failure.
    - [x] 2026-05-12 FFI declarations returning `ascii` or `unicode` now fail
          during type checking with guidance to return raw pointer plus explicit
          length/status and construct the text view or owned buffer in Stark
          wrapper code. Regression coverage checks both `ascii` and `unicode`
          returns directly and includes the shape in the pre-MIR fallback
          contract table.
    - [x] 2026-05-12 final SSA contract validation pass added before LLVM
          emission. `validate-ssa` checks direct-codegen SSA bodies for missing
          value definitions, malformed terminators, direct/indirect call ABI
          arity and metadata gaps, FFI text-view returns that escaped typing,
          unsupported SSA conversions, invalid dynamic-storage operation shapes,
          bad dynamic move result types, unsupported local allocation/deallocation
          storage classes, and `retborrow` return compatibility including
          pointer-backed scalar borrow returns. Failures are reported as
          `STK5002` diagnostics before `emit-llvm`, so malformed SSA no longer
          depends on `UnsupportedBodyEmissionException` during string emission.
          Follow-up on the same date tightened local storage validation after
          generated scratch locals became explicit in SSA, so missing local
          allocation records and non-heap deallocation records now fail before
          LLVM with invalid-SSA diagnostics.
    - [x] 2026-05-12 dynamic-storage concrete layout and capacity-width
          validation moved into `validate-ssa`. The pass now receives the same
          type model, enum layouts, imported package concrete-layout facts, and
          LLVM target info used by backend emission, then rejects dynamic
          allocation/reserve/move operations whose element type has no positive
          concrete layout or whose capacity/index integer cannot be represented
          as the i64 helper ABI expects. Regression coverage checks both an
          unsupported dynamic element layout and a too-wide capacity integer, so
          these cases fail as `STK5002` before LLVM emission instead of throwing
          `UnsupportedBodyEmissionException` inside dynamic storage codegen.
    - [x] 2026-05-12 SSA LLVM value-shape validation now covers unary and binary
          operators, select arms, slice creation/loads, text slicing, address
          forms, indirect loads/stores, and memory-copy addresses. This moves
          invalid LLVM forms such as integer logical-not, mixed-shape arithmetic,
          non-bool comparison results, mismatched select arms, unknown-length
          fixed-array slice creation, non-pointer indirect loads, and bad slice
          element addresses into `STK5002` diagnostics before string emission.
          The shape comparison strips borrow/access/init qualifiers at the value
          boundary, so valid addressable slots such as `out bool` still accept a
          stored `bool` without weakening the underlying pointer-pointee check.
          A 2026-05-13 follow-up tightened the operator contract to match the
          backend exactly: ordinary arithmetic and exponent operators require
          concrete integers or supported float intrinsic widths; wrapping and
          saturating operators require concrete integers; integer shifts require
          concrete integer results; bitwise integer operations reject missing
          integer widths; and comparisons reject non-concrete integer/float
          operands. Malformed SSA such as float `WrappingAdd` and unsupported
          `f24` exponent lowering now fails before `emit-llvm`.
    - [x] 2026-05-12 closeout: this item is complete for the invalid-SSA-shape
          class. Remaining backend work is now represented by the explicit
          audit and positive-emission coverage tasks above.
  - [x] Remove or strictly gate declaration-only body emission for functions that
        have source bodies and are not open generic templates, FFI declarations,
        or intentionally bodyless declarations.
    - [x] 2026-05-12 strict backend gate: the default compiler pipeline now
          disables declaration-only fallback for accepted source bodies during
          LLVM emission. Source functions, source asm bodies, and materialized
          specializations with bodies still log `llvm-body-fallback`,
          `llvm-asm-fallback`, or `llvm-body-pending`; the pipeline converts
          those logs to `STK5001` diagnostics and omits the misleading
          declaration from the emitted module. Low-level emitter tests can still
          opt into legacy declaration fallback when deliberately constructing
          invalid SSA fixtures.

## 1. Promote Experimental Standard Library

- [ ] Replace the current standard library with the experimental implementation.
  - [x] Delete obsolete stable implementations after the replacement compiles.
  - [x] Copy or move experimental modules into the canonical `System.*`
        namespace.
  - [x] Remove temporary `System.Experimental.*` public surface unless a
        compatibility shim is explicitly needed for one release.
  - [x] Update imports in examples, tests, benchmarks, and docs.
  - [x] Preserve benchmark names and only report the language as `stark`.
  - [x] Remove `stark-experimental` benchmark variants after promotion.
  - [ ] Run the full compiler, standard library, integration, and benchmark
        suites on Windows and Linux before closing this task.

### Module Promotion Checklist

- [x] Replacement and namespace promotion.
  - [x] Promote `System.Experimental.Memory` into canonical `System.Memory`;
        keep the allocator ABI while exposing dynamic reserve, append, copy,
        move, fill, and disjoint helpers.
  - [x] Promote experimental implementations into canonical namespaces:
        `System.Collections`, `System.Console`, `System.FileSystem`,
        `System.IO`, `System.IO.File`, `System.IO.Path`, `System.Net`,
        `System.Net.Tcp`, `System.Runtime.Buffer`, and `System.Text`.
  - [x] Promote runtime and platform dispatch changes required by the
        experimental modules: `System.Runtime`, `System.Runtime.Platform`,
        `System.Runtime.Platform.Linux`, and `System.Runtime.Platform.Windows`.
  - [x] Confirm and keep or port modules with no experimental replacement:
        `System.BitOperations`, `System.Math`, `System.Process`,
        `System.Runtime.ConsoleInput`, `System.Syscall`, and
        `System.Threading`.
  - [x] Update `System` re-exports and public surface wiring after promoted
        modules land.

- [x] Standard library dependency rewiring.
  - [x] Update promoted experimental callers of memory helpers:
        `System.Experimental.Text`, `System.Experimental.Runtime.Buffer`, and
        `System.Experimental.IO.Path` now call canonical `System.Memory`.
  - [x] Replace all `System.Experimental.*` imports inside the standard library
        with canonical `System.*` imports after each promoted batch lands.
  - [x] Preserve source-compatible result and status types where needed, such as
        `IOStatus`, `IOResult<T>`, `MemoryStatus`, and `MemoryResult<T>`.
  - [x] Keep OS-specific APIs internal to platform/runtime modules.
  - [x] Preserve compiler-known runtime helper names or update compiler
        recognition at the same time as namespace promotion.

- [ ] Test and package updates.
  - [x] Add canonical `System.Memory` helper lowering, packaging, and executable
        coverage.
  - [x] Update text, runtime buffer, and path tests that consume promoted
        `System.Memory` helpers.
  - [x] Update source, executable, lowering, package-image, and integration
        tests for collections, console, filesystem, IO, net, runtime buffer,
        text, and platform batches.
  - [x] Preserve package-backed generic helper specialization for promoted
        public generic APIs.
    - Done: package images now publish the package-private generic helper
      closure needed by API-visible generic template bodies, which keeps
      promoted collections package-consumable without original source files.
  - [x] Verify package image manifests contain only canonical modules.
  - [x] Confirm no temporary compatibility shim is intentionally kept for this
        batch, so no shim-specific tests are required.
  - [ ] Run the full compiler, standard library, integration, and benchmark test
        suites on Windows and Linux before closing the promotion.

- [ ] Benchmark consolidation and experimental benchmark deletion.
  - [x] Replace canonical memory benchmarks with promoted helper-based
        implementations.
  - [x] Delete temporary memory experimental benchmarks:
        `ExperimentalMemoryCopyFill.stark` and
        `ExperimentalMemoryDynamicReserveGrowth.stark`.
  - [x] Preserve canonical benchmark names and report promoted Stark rows as
        language `stark`, not `stark-experimental`.
  - [x] Delete remaining experimental benchmark variants after their canonical
        standard-library modules are promoted.
  - [x] Update benchmark harness gates so promoted modules no longer require
        matching `Experimental*.stark` files.
  - [ ] Re-run focused benchmark smoke tests for each promoted batch against C
        and Rust.
    - [x] Windows smoke reran `MemoryCopyFill` and `DictionaryLookup` with
          canonical `stark`, `c`, and `rust` rows after the benchmark range
          cleanup.
    - [ ] Finish the remaining promoted batch smoke set.
  - [ ] Re-run the full benchmark suite after the promotion is complete.

- [ ] Behavioral and performance verification.
  - [x] Verify canonical `System.Memory` helper lowering keeps memcpy, memmove,
        memset, dynamic length commits, Windows heap declarations, and package
        attributes intact.
  - [x] Re-run focused `MemoryCopyFill` and `MemoryDynamicReserveGrowth`
        benchmark smoke tests against C and Rust.
  - [ ] Verify allocator attributes, realloc behavior, bucket reuse, and dynamic
        memory primitives in the full allocator suite.
  - [ ] Verify collection performance for list, stack, queue, linked list, and
        dictionary workloads.
  - [ ] Verify console redirected output, Windows console, and Linux terminal
        behavior.
  - [ ] Verify filesystem and IO behavior: buffered and unbuffered read/write,
        ordinary close without durable flush, directory enumeration correctness,
        Unicode, long-name, first-entry, empty-directory, and close paths.
  - [ ] Verify path behavior across Windows, Linux, and future macOS separators
        and normalization.
  - [ ] Verify networking behavior: socket startup/shutdown, scalar TCP paths,
        vectored TCP paths, and loopback throughput.
  - [ ] Verify runtime buffer fixed and dynamic behavior and benchmarks.
  - [ ] Verify text behavior: owned text, views, append, format, copy, Unicode,
        and path-related formatting.
  - [ ] Verify platform parity for Linux, Windows, and macOS dispatch surfaces.

- [ ] Cleanup and final removal.
  - [x] Delete `System.Experimental.Memory` after canonical `System.Memory`
        consumers and tests were updated.
  - [x] Remove remaining `System.Experimental.*` public surface unless a
        compatibility shim is explicitly approved for one release.
  - [x] Remove experimental namespace aliases after all consumers are canonical.
  - [x] Remove temporary migration tests, docs, and benchmark gates that only
        existed to compare stable and experimental implementations.
  - [x] Audit promoted modules against the new unsafe, raw-pointer, and range
        rules before release.



## 3. Remove Unnecessary Raw Pointers From The Standard Library

- [x] Disallow unnecessary raw pointer use in the standard library.
  - [x] Define allowed raw pointer zones: FFI declarations, OS platform modules,
        runtime allocation hooks, compiler-known ABI helpers, and carefully
        audited unsafe internals.
  - [x] Prefer `dynamic`, slices, borrowed values, fixed buffers, and owned
        handles everywhere else.
  - [x] Add standard library audit tests that fail on unexpected raw pointer
        usage outside allowlisted files or functions.
  - [x] Document every remaining raw pointer with the boundary it serves.



## 4. Additional Optimization Work


### Range Notation Module Checklist

- [x] `System`
- [x] `System.BitOperations`
- [x] `System.Collections`
- [x] `System.Console`
- [x] `System.FileSystem`
- [x] `System.IO`
- [x] `System.IO.File`
- [x] `System.IO.Path`
- [x] `System.Math`
- [x] `System.Memory`
- [x] `System.Net`
- [x] `System.Net.Tcp`
- [x] `System.Process`
- [x] `System.Runtime`
- [x] `System.Runtime.Buffer`
- [x] `System.Runtime.ConsoleInput`
- [x] `System.Runtime.Platform`
- [x] `System.Runtime.Platform.Linux`
- [x] `System.Runtime.Platform.Windows`
- [x] `System.Syscall`
- [x] `System.Text`
- [x] `System.Threading`



## 5. Implement Closure System For Inline, Borrowed, And Heap Callbacks

- [x] Implement the focused closure system described in
      `docs/Internals/ClosureProposal.md`.
  - [x] 2026-05-14 design narrowed and documented: the broad closure sketch was
        reduced to the three closure forms required for egui-style APIs and
        performance-sensitive higher-order helpers: `inline closure<...>` for
        call-now specialization, `borrow closure<...>` for non-escaping runtime
        callbacks, and `heap closure<...>` for stored/returned retained
        callbacks. The proposal explicitly keeps `fnptr<...>` thin and
        non-capturing.
  - [x] 2026-05-14 implementation plan captured in
        `docs/Internals/ClosureProposal.md`: closure type shape, explicit
        capture modes, call capabilities (`normal`, `mut`, `once`),
        target-typed lambda conversion rules, borrow variations, heap capture
        legality, MIR/SSA/LLVM lowering shapes, package-image requirements,
        diagnostics, and required tests are documented for compiler engineers.
  - [x] Add grammar and parser support for closure types and inline closure
        parameters. Required accepted forms include
        `inline closure<fn void(mut borrow Ui)>`,
        `borrow closure<fn void(mut borrow Ui)>`,
        `mut borrow closure<mut fn void(i32[min max])>`,
        `heap closure<fn void()>`, and
        `heap closure<once fn Packet()>`. Reuse existing function-pointer
        signature parsing for function kind, return type, parameter types,
        raw-pointer bound expressions, and `where overlap(...)` /
        `where same(...)` memory contracts.
    - [x] 2026-05-14 grammar slice landed: `Stark.g4` now has a dedicated
          `closure` keyword, `once` call capability, `closureType`,
          `closureSignature`, optional `inline`/`heap` closure type prefixes,
          optional heap lambda-expression prefix for `heap capture(...) => ...`,
          and regenerated ANTLR parser/visitor files. Parser conformance now
          covers inline closure parameters, borrowed closure parameters,
          mutable borrowed closure parameters, heap closure fields/returns,
          `once` closure signatures, and closure memory contracts.
  - [x] Extend the type model with closure type symbols. The model must record
        storage requirement (`inline`, borrowed view, owned heap), borrow class,
        call capability (`normal`, `mut`, `once`), function kind, return type,
        parameter types, memory contracts, raw-pointer bounds, capture layout,
        and environment ownership/mutability facts.
    - [x] 2026-05-14 front-end closure type identity landed:
          `StarkTypeKind.Closure`, `StarkClosureStorageKind`, and
          `StarkClosureCallCapability` now keep closure type contracts distinct
          from thin `fnptr<...>` values. Type resolution records inline/heap
          storage prefixes, borrower qualifiers such as `borrow` and
          `mut borrow`, call capability, function kind, return type, parameter
          types, default non-overlap groups, explicit `where overlap(...)` /
          `where same(...)` contracts, and bounded raw-pointer count
          expressions. Law and finite-law closure signatures now reject `mut`
          and `once` capability before later lowering. Type-check regression
          coverage asserts these facts through normal function signatures and
          generic type-alias substitution into closure signatures.
    - [x] 2026-05-14 closure-lambda expression facts landed:
          `ClosureLambdaTypingRecord` records the synthetic closure body name,
          target closure type, parameter names, source location, and enclosing
          function. Type checking now records closure lambda bodies separately
          from non-capturing `fnptr` lambdas, and semantic/ownership/lowering
          contract validation can find their captured locals and body facts by
          source location.
    - [x] 2026-05-14 closure-call facts landed: `ClosureCallTypingRecord`
          records calls through `closure<...>` values separately from thin
          `fnptr` indirect calls. Type checking now treats closure values as
          callable, validates argument arity/types, bounded raw-pointer
          parameter contracts, default non-overlap/`where overlap`/`where same`
          memory contracts, and mutable-call capability. Semantic and
          ownership validation recognize closure calls before MIR, and lowering
          contract validation now has an explicit closure-call fact gate.
    - [x] 2026-05-14 closure invoke ABI metadata landed:
          `ClosureLambdaTypingRecord` now carries the hidden environment
          parameter type, and generated closure invoke signatures lower as
          `return invoke(rawptr<i8> $env, source-args...)`. Closure call
          memory contracts and bounded raw-pointer expressions are shifted from
          source `arg0`/`arg1` names to invoke `arg1`/`arg2` names so the hidden
          environment slot does not corrupt user-visible contracts.
    - [x] Add final capture environment layout records for MIR/SSA lowering:
          ordered capture fields, field types after capture-mode projection,
          environment storage class, drop order, and ownership transfer facts.
      - [x] 2026-05-14 borrowed-closure environment layout records landed:
            `ClosureLambdaTypingRecord` now carries ordered
            `ClosureCaptureFieldSymbol` records. Each capture records source
            type, lambda-body type, field type, capture mode, unsafe marker,
            and whether the environment stores the captured value directly or
            stores an address back to caller storage. Type checking also
            creates a synthetic named environment struct in declaration order
            so MIR, SSA, LLVM, and debug/layout code all see one deterministic
            layout.
  - [x] Implement target-typed lambda binding for closure targets. Lambda
        expressions must require an explicit `fnptr` or closure target, validate
        parameter/return compatibility, validate the body under the target
        function kind, build capture records, reject implicit outer-local use,
        and reject capturing lambdas converted to `fnptr<...>`.
    - [x] 2026-05-14 lambda binding now accepts explicit closure targets while
          preserving existing thin `fnptr<...>` behavior. Non-capturing lambdas
          still lower as function pointers only when the target is `fnptr`.
          Closure lambdas bind to `inline closure<...>`,
          `borrow closure<...>`, `mut borrow closure<mut ...>`, and
          `heap closure<...>` targets, validate exact parameter ABI types,
          validate return/body type, record captures, reject implicit
          uncaptured outer-local use, and continue rejecting captures converted
          to `fnptr<...>`.
    - [x] 2026-05-14 named function items now promote to explicit closure
          targets as empty-environment closure values. Type checking records a
          `ClosureFunctionPromotionTypingRecord` with the source function,
          target closure type, adapter name, location, and enclosing function;
          HIR/MIR synthesize a fast internal adapter with the closure
          environment pointer plus source arguments; LLVM emits the adapter at
          O0 and O3 devirtualizes/prunes it when the borrowed closure wrapper
          collapses to a direct call. This covers `borrow closure<...>`,
          `inline closure<...>`, and `heap closure<...>` target positions.
    - [x] 2026-05-14 closure lambda bodies participate in semantic and
          ownership validation using synthetic typed signatures, so function
          kind, return, parameter, and captured-local facts are available before
          MIR.
  - [x] Implement explicit capture validation for closure targets. Enforce
        `copy`, `move`, `read`, `mut`, `out`, `init`, `unsafe addr`, and
        `unsafe shared` capture rules; reject duplicate captures; reject
        capture of moved bindings; require writable bindings for `mut`/`out`/
        `init`; enforce write-only `out`/`init`; and keep unsafe capture modes
        gated by unsafe context.
    - [x] 2026-05-14 existing explicit capture validation now applies to
          closure targets: capture names are explicit, duplicate captures are
          rejected, unknown modes are rejected, `unsafe addr`/`unsafe shared`
          require unsafe context, `copy` rejects move-only values, and
          `mut`/`out`/`init` require writable locals. Heap closure conversion
          requires an explicit `heap` lambda prefix and rejects non-owning
          `read`, `mut`, `out`, `init`, and `addr` captures because they would
          retain local storage.
    - [x] Add ownership-transfer/drop validation for `move` captures, borrowed
          closure escape checks, `out`/`init` definite-write checks across
          invocation paths, and `closure<once ...>` use-after-call validation.
      - [x] 2026-05-14 `capture(move value)` now consumes the source binding
            during closure creation in ownership validation, so later reads,
            writes, returns, or second move captures of the same binding report
            the normal use-after-move diagnostics before MIR. This applies to
            heap and borrowed closure expressions because the ownership
            transfer happens at the source capture site, independent of the
            eventual environment storage.
      - [x] 2026-05-14 heap capture escape checks now reject capturing
            non-escaping `borrow`/`retborrow` values, including
            `borrow closure<...>` runtime views, into owned heap closure
            environments. Heap closures may retain owned values, raw
            capabilities, or explicit `storeborrow` views; ordinary borrowed
            callback views must stay in borrowed/inline closure APIs.
      - [x] 2026-05-14 `out` and `init` captures now participate in
            ownership validation as definite-write contracts. Closure lambda
            bodies declare those captured names as write-required, reject
            reads through the existing write-only type surface, and report
            `STK4205` when any explicit return path or fallthrough path can
            complete without assigning the captured destination. Regression
            coverage checks both `out` and `init` captures across branching
            closure bodies.
      - [x] 2026-05-14 `closure<once ...>` call consumption is covered for
            both inline and heap closures. Invoking a once closure consumes the
            closure value in ownership validation, so a second invocation
            reports the normal use-after-move diagnostic before MIR.
  - [x] Implement `inline closure<...>` specialization. Inline closure
        parameters must not materialize runtime closure objects, must become
        part of the specialization key, must substitute the lambda body and
        capture facts into the callee before backend emission, and must reject
        storage, return, array, `fnptr`, or ABI-boundary uses before MIR.
    - [x] 2026-05-14 optimized same-module inline-closure specialization
          landed for direct inline wrapper calls: the SSA direct-call inliner
          now accepts small inline candidates that contain closure invokes and
          simple address/load projections, then runs cleanup, constant
          propagation, devirtualization, and inlining in a small fixed-point
          loop. For calls such as
          `Apply(capture(copy offset) (value) => value + offset, 4)`, optimized
          `Run` now lowers to the direct arithmetic body with no closure
          environment alloca, no `{ invoke, env }` aggregate construction, no
          indirect closure call, and no emitted inline-lambda function symbol.
    - [x] 2026-05-14 codegen pruning now removes unreferenced inline-closure
          lambda SSA functions after specialization. Referenced inline lambdas
          are kept so debug/O0 or not-yet-specialized paths remain correct;
          fully optimized call sites do not leave declaration-only
          `Run.__lambda_*` symbols in LLVM.
    - [x] 2026-05-14 LLVM regression coverage added for optimized inline
          closure specialization: `Run(offset)` must contain the direct
          `value + offset` arithmetic, must not allocate a closure environment,
          must not construct/extract `{ ptr, ptr }`, must not call through a
          closure invoke pointer, and must not emit the inline lambda symbol.
    - [x] 2026-05-14 verification: `dotnet test -c Release
          tests/compiler.Tests/compiler.Tests.csproj --no-restore` passed with
          `1373` tests after the inline-closure SSA specialization and pruning
          slice.
    - [x] Finish front-end/body-substitution specialization for all
          `inline closure<...>` uses, including non-trivial callee control
          flow, nested inline closure parameters, mutable/once inline closure
          capabilities, imported package typed bodies, and diagnostic coverage
          for any inline closure call that cannot be specialized before MIR.
      - [x] 2026-05-14 SSA body-substitution specialization now treats
            functions with `inline closure<...>` parameters as mandatory
            inline-specialization candidates up to a bounded hot-path
            instruction budget. The inliner now forwards inline closure
            arguments through nested wrappers, handles multi-block callees with
            non-trivial control flow, preserves pointer-backed borrow/out/init
            argument metadata, and protects caller replacement values from
            callee SSA-name collisions.
      - [x] 2026-05-14 inline closure capabilities now specialize for
            `inline closure<mut fn ...>` and `inline closure<once fn ...>`.
            Type checking no longer requires a mutable runtime closure value
            for `mut inline closure` calls, because the optimized inline form
            has no retained runtime object to mutate.
      - [x] 2026-05-14 cleanup now removes dead runtime closure environment
            slots left behind after successful inline specialization. Stores
            into local storage that is never read or escaped are deleted before
            the normal pure/local cleanup pass, so specialized borrowed
            parameter closures do not keep unused environment allocas in
            optimized LLVM.
      - [x] 2026-05-14 borrow/out/init metadata rewriting is now part of SSA
            body substitution. When an inlined function has pointer-backed
            parameters, nested calls inside the clone receive the caller's real
            indirect argument address instead of stale callee-local metadata
            such as `self`; address-of-parameter aliases can also map through
            caller parameters and locals. This fixed stdlib IO validation
            failures in `Directory.ReadNext*` and `FileResult` while preserving
            direct pointer forwarding for optimized inline closures.
      - [x] 2026-05-14 alias-aware memory optimization was tightened for
            multi-block inline clones: scalar local/global load forwarding may
            delete the load only when every use of the loaded SSA value is in
            the block being rewritten. This prevents cross-block inlined loop
            bodies from retaining references to removed load results, fixing
            the `ReadDirectoryEntry` undefined-value validation failure without
            disabling same-block forwarding.
      - [x] 2026-05-14 LLVM coverage now proves inline closure
            specialization for named borrow parameters, nested wrapper calls,
            closures invoked from inside control flow, mutable inline closures,
            and once inline closures. Each optimized `Run` body must contain
            direct specialized work and no `{ invoke, env }` construction,
            extraction, indirect closure call, wrapper call, or emitted inline
            lambda symbol.
      - [x] 2026-05-14 package-backed generic inline closure specialization
            now works from typed package-image bodies. Generic template
            manifests preserve postfix callable invocations as typed
            `closure-call` expressions, imported-template lowering lowers
            those calls directly for both `closure<...>` values and thin
            `fnptr<...>` values, and optimized consumers can specialize an
            imported generic inline-closure wrapper down to direct arithmetic
            with no runtime closure pair or monomorphized wrapper call left in
            the hot function.
      - [x] 2026-05-14 verification: `dotnet test -c Release
            tests/compiler.Tests/compiler.Tests.csproj --no-restore` passed
            with `1379` tests after inline closure specialization, imported
            typed-body lowering, borrow metadata rewriting, and alias-aware
            memory forwarding fixes.
      - [x] Add front-end diagnostics for every accepted-looking
            `inline closure<...>` use that cannot be specialized before
            backend emission, including storing inline closures, returning
            them, putting them in arrays/aggregates, crossing ABI boundaries,
            missing package typed bodies, and generic calls where lambda
            target-typing cannot be resolved from the available parameter
            context.
        - [x] 2026-05-14 front-end storage validation now treats
              `inline closure<...>` as a direct-parameter-only specialization
              contract. It rejects inline closures in locals, fields, returns,
              aggregate/array element positions, nested `fnptr`/runtime
              closure signatures, and other runtime-storage shapes before MIR;
              direct function parameters remain valid specialization inputs.
  - [x] Implement `borrow closure<...>` runtime views. A borrowed closure lowers
        to `{ invoke_pointer, environment_pointer }`, with caller/call-site
        environment storage, non-escaping lifetime checks, borrower-class
        support for `borrow` and `mut borrow`, and strict validation for
        `retborrow closure` / `storeborrow closure` before those forms are
        accepted in APIs.
    - [x] 2026-05-14 caller stack-environment lowering landed for borrowed
          closure expressions: capture expressions allocate a synthetic stack
          environment, initialize fields in capture-clause order, and
          materialize the two-word closure value with the invoke pointer plus
          erased environment pointer. Invoke bodies cast `$env` back to the
          synthetic environment type and resolve captured names through the
          normal MIR place/address model, so reads, writes, field projections,
          and indexing use the same optimized path as ordinary locals.
    - [x] Add borrowed-closure escape validation before MIR so stack
          environments cannot be returned, stored beyond the creating scope, or
          embedded into heap/owned state.
      - [x] 2026-05-14 front-end storage validation now rejects
            `borrow`/`retborrow` aggregate fields, including
            `borrow closure<...>` and `retborrow closure<...>`, because those
            classes are non-storable by definition. `storeborrow closure<...>`
            remains the explicit stored-borrow surface, but ownership
            validation rejects initializing a stored field from a capturing
            closure expression or other non-external source lifetime. Heap
            closure capture validation also rejects copying non-escaping
            borrowed closure views into owned environments. Existing
            `retborrow closure` return paths continue to use the ownership
            lifetime checker, so returning a stack-created closure environment
            fails before MIR.
    - [x] Add `retborrow closure` and `storeborrow closure` grammar/type
          validation only if those forms remain part of the language surface;
          otherwise reject them before MIR with precise diagnostics.
      - [x] 2026-05-14 `retborrow closure` remains valid only as a returned
            borrow view tied to an input/storage lifetime, while
            `storeborrow closure` is valid only for explicit stored borrowed
            callback storage and now requires an external/noncapturing source
            when initialized into fields.
  - [x] Implement `heap closure<...>` owned environments. Heap closures must
        allocate explicit heap-backed environment storage, move/copy captures
        into declaration-order layout, own and drop captured values correctly,
        reject stack borrow captures, support `closure<mut ...>` mutable
        invocation, and support `closure<once ...>` consumption/use-after-call
        validation.
    - [x] 2026-05-14 first heap-environment allocation slice landed:
          `heap capture(...)` closure expressions now allocate their synthetic
          environment with the compiler heap-storage path instead of caller
          stack storage, so returned heap closures no longer point at a dead
          stack environment. LLVM body attribute adjustment now treats heap
          local allocation/deallocation as allocator access, clearing invalid
          `nofree`/`memory(none)` attributes on functions that materialize heap
          closure environments.
    - [x] Finish heap closure ownership: generated heap environments still need
          layout-specific drop paths for captured owned fields, generic closure
          environment freeing on heap-closure drop, and move/once consumption
          rules.
      - [x] 2026-05-14 heap closure owned-drop representation landed:
            `heap closure<...>` values now lower as `{ invoke_pointer,
            environment_pointer, drop_pointer }` while borrowed closures remain
            two-word `{ invoke_pointer, environment_pointer }` views. HIR/ABI,
            MIR, SSA, validation, concrete layout, and LLVM type mapping now
            agree on the heap-only third slot, so heap closure ownership can be
            moved, returned, stored, and dropped without knowing the original
            lambda at the use site.
      - [x] 2026-05-14 heap closure environment destructors landed:
            captured heap closures get a synthetic `lambda.__drop(rawmutptr<i8>
            $env)` function. The drop body casts `$env` back to the synthetic
            environment type, drops moved owned capture fields in reverse
            declaration order with the existing runtime-drop lowering, then
            frees the heap environment through `__stark_heap_free`. Empty heap
            closures use a shared no-op drop function.
      - [x] 2026-05-14 heap closure caller-drop attributes fixed: dropping a
            heap closure emits an indirect call through the drop pointer and is
            marked as allocator/free-capable in SSA/LLVM effect adjustment, so
            callers that drop heap closures no longer receive invalid `nofree`
            or `memory(none)` attributes.
      - [x] Finish `closure<once ...>` ownership consumption: invoking a once
            heap closure must consume the closure value, run the body exactly
            once, and prevent any later use or drop from double-consuming the
            environment.
        - [x] 2026-05-14 implementation note: once heap closure invocation now
              consumes the closure value at the call site, marks the indirect
              invoke as free-capable for SSA/LLVM effects, and deactivates the
              caller's ordinary heap-closure drop state. The generated invoke
              body tracks moved `move` capture loads back to their environment
              fields, drops any still-owned runtime-droppable captures in
              reverse declaration order, and frees the heap environment on
              every return path. A moved-out owned capture transfers to the
              return value without running the capture drop, while an uncalled
              closure still uses the synthetic layout-specific drop function.
  - [x] Add MIR, SSA, and LLVM lowering for closure creation/invocation/drop.
        Required MIR forms include create-borrow-closure, create-heap-closure,
        invoke-closure, invoke-inline-closure before specialization,
        move-closure, and drop-closure. LLVM invoke functions should use
        internal `fastcc` when possible and carry environment facts such as
        `nonnull`, `dereferenceable`, `align`, `noalias`,
        `captures(none)`, memory effects, `mustprogress`, and `willreturn`
        where source contracts prove them.
    - [x] 2026-05-14 HIR/MIR symbol plumbing landed for closure lambda bodies:
          closure lambdas now materialize as synthetic HIR functions with their
          typed signatures/effect profiles, are address-taken roots for later
          invoke-pointer lowering, and are discoverable by MIR lowering through
          the same source-location map used for existing non-capturing
          `fnptr` lambdas.
    - [x] 2026-05-14 non-capturing runtime closure values landed: closure
          lambda expressions now lower as real two-word callable values in MIR,
          SSA, and LLVM (`{ invoke_pointer, environment_pointer }`) instead of
          remaining only type-checked facts. `SsaClosureValue` validates the
          invoke function ABI, LLVM maps closure values to `{ ptr, ptr }`, and
          closure calls extract invoke/environment slots and reuse the existing
          indirect-call ABI path so `finite`/`law` effects, raw-pointer
          contracts, and call attributes stay centralized.
    - [x] 2026-05-14 closure invoke bodies now emit as synthetic LLVM
          definitions: the LLVM module surface now treats closure lambda
          functions the same way it treats existing non-capturing `fnptr`
          lambda functions, so closure invoke targets with SSA bodies are not
          left as declaration-only fallbacks.
    - [x] 2026-05-14 empty-environment function-item closure adapters now flow
          through MIR, SSA, ABI, LLVM, and pruning as ordinary synthetic
          callable bodies. Borrowed-closure arguments that are runtime closure
          views no longer require source addressability in the lowering
          contract; the ABI path materializes a temporary view only when a
          real address is required, while SSA inlining forwards load-only
          closure parameters directly so optimized code does not keep an
          unnecessary `{ invoke, env }` stack slot.
    - [x] 2026-05-14 SSA optimization and coverage landed for closure values:
          known closure invoke targets participate in address-taken/reference
          tracking, constant propagation treats closure values as constants,
          and the emitter coverage matrix now includes `SsaClosureValue`.
    - [x] Add runtime capture environment lowering for closure expressions:
          allocate caller stack environments for borrowed closures, allocate
          heap environments for heap closures, initialize capture fields by
          mode (`copy`, `move`, `read`, `mut`, `out`, `init`, unsafe modes),
          project captured names inside invoke bodies through the hidden
          `$env` parameter, and preserve drop/ownership facts.
      - [x] 2026-05-14 borrowed runtime environment lowering landed for stack
            environments. `copy`, `move`, `unsafe shared`, and `unsafe addr`
            captures store projected values directly in the environment;
            `read`, `mut`, `out`, and `init` captures store raw addresses to
            caller storage. Captured-name reads and assignments inside invoke
            bodies now flow through address-rooted `PlaceTarget`s, so mut
            captures emit direct loads/stores through the captured address
            instead of synthetic special-case assignment code.
      - [x] 2026-05-14 return cleanup ordering fix landed while validating mut
            closures: addressable return operands are now materialized before
            scope cleanup, preventing `llvm.lifetime.end` from being emitted
            before the final load from a returned addressable local.
      - [x] Finish heap environment allocation/drop lowering for
            `heap closure<...>` so retained closures never point at caller
            stack environments.
        - [x] 2026-05-14 heap environment allocation now uses heap local
              storage and emits `__stark_heap_alloc`; returned heap closures
              keep a heap pointer rather than a stack pointer.
        - [x] Add heap closure drop lowering: free the environment exactly
              once, emit field drops for captured owned values before freeing,
              and ensure moved heap closures transfer ownership without double
              free.
          - [x] 2026-05-14 implementation note: heap closure drops now extract
                `{ env, drop }` from the closure value, coerce the erased env
                pointer to the drop ABI, and call the layout-specific drop
                function. Runtime drop state treats heap closures as owned
                values, so assignment activates the destination owner and moves
                from locals/parameters deactivate the source owner.
      - [x] Finish move-capture ownership transfer/drop validation for
            non-copy captured owners and ensure moved-from locals are never
            dropped after transfer into an environment.
        - [x] 2026-05-14 ownership validation now moves captured source
              bindings at closure creation, and MIR environment-field
              initialization continues to use the normal assignment
              `RecordMoveFromOperand` path so moved captured owners deactivate
              their original runtime-drop slot when ownership transfers into
              the stack or heap environment.
    - [x] Add inline-specialization input lowering for `inline closure<...>`
          before runtime closure materialization. Inline closure calls should
          substitute the closure body/captures into the specialized callee and
          emit no `{ invoke_pointer, environment_pointer }` runtime pair.
      - [x] 2026-05-14 optimized SSA specialization now provides the
            body-substitution result for same-module and imported-template
            inline closure hot paths at `O3`: the final optimized LLVM does
            not contain runtime closure pairs, environment allocas, indirect
            invoke calls, or inline-lambda symbols for the covered cases.
            Front-end validation now rejects the unsupported runtime-storage
            shapes before MIR, so accepted optimized inline closure hot paths
            specialize to direct SSA/LLVM instead of retaining runtime closure
            pairs.
    - [x] Add heap closure move/drop lowering for owned environments,
          including `closure<once ...>` consumption and destructor generation
          for captured owned fields.
      - [x] 2026-05-14 LLVM regression coverage now includes once heap closure
            ownership cleanup: a moved dynamic capture returned from
            `closure<once ...>` frees only the environment in the invoke body
            and leaves the dynamic buffer owned by the return value; an unmoved
            dynamic capture is dropped before the invoke body frees the
            environment; and callers invoking a once heap closure no longer emit
            a second indirect drop call through the closure drop pointer.
      - [x] 2026-05-14 verification: `dotnet test -c Release
            tests/compiler.Tests/compiler.Tests.csproj --no-restore` passed
            with `1372` tests after the heap/once closure ownership cleanup.
  - [x] Extend package images and imported-template lowering for closure
        signatures and inline closure typed bodies. Package images must preserve
        call capability, function kind, parameter/return types, memory
        contracts, raw-pointer bounds, public signatures involving closures,
        and typed bodies needed to specialize imported inline closure targets.
    - [x] 2026-05-14 package type references now preserve closure signatures in
          typed package interfaces: closure kind, inline/heap storage prefix,
          `mut`/`once` call capability, function kind, return type, parameter
          types, raw-pointer count expressions, and overlap/same memory
          contracts round-trip through package image manifests and generated
          package module source.
    - [x] Preserve imported typed bodies needed to specialize imported
          `inline closure<...>` targets.
      - [x] 2026-05-14 generic template package images now preserve typed
            callable invocations through a `closure-call` expression summary,
            round-trip that summary through package loading/source bridging,
            and lower it back to direct MIR indirect-call form for closure and
            function-pointer callable parameters. Regression coverage uses a
            typed-only package manifest with the source deleted, proving the
            consumer specializes from package typed bodies rather than local
            source.
  - [x] Add diagnostics for invalid closure use before MIR. Required cases:
        missing target type, capturing lambda to `fnptr`, unknown/duplicate
        capture modes, unsafe capture without unsafe context, uncaptured local
        use, invalid capture mode for the source binding, borrowed closure
        escape, heap closure capturing stack borrow, mutable closure called
        through immutable access, once closure use-after-call, function-kind
        violations, memory-contract violations, and missing imported typed body
        for inline closure specialization.
    - [x] 2026-05-14 diagnostic coverage now includes closure values used as
          calls, mutable closure calls through immutable/non-mutable closure
          access, closure function-kind violations in law/finite callers, and
          closure call lowering-contract fact mismatches.
    - [x] 2026-05-14 diagnostic coverage now includes inline-closure
          runtime-storage misuse, nested inline closure parameter misuse inside
          `fnptr` signatures, `out`/`init` capture definite-write failures, and
          once-closure use-after-call failures before MIR.
  - [x] Add parser, type-checking, ownership, MIR/SSA, LLVM, package-image, and
        book/reference tests for the closure system. Include egui-style inline
        examples, non-escaping borrowed callback chains, retained heap callback
        examples, function-kind preservation, memory-contract preservation,
        and optimized LLVM checks proving inline closures allocate no runtime
        environment.
    - [x] 2026-05-14 focused type/checking validation tests now cover
          closure-lambda target typing, heap-prefix and heap-safe capture
          diagnostics, closure calls through immutable and mutable closure
          values, and lowering-contract recording for closure-call argument
          facts.
    - [x] 2026-05-14 LLVM regression coverage now checks that a
          non-capturing closure value emits a concrete `{ ptr, ptr }` pair,
          extracts invoke/environment slots, calls through the invoke pointer,
          preserves `finite law` call attributes, and emits the synthetic
          closure invoke body instead of a declaration-only fallback.
    - [x] 2026-05-14 LLVM regression coverage now checks borrowed captured
          closures: `copy` captures allocate and load from a synthetic
          environment struct, `mut` captures store caller addresses in the
          environment and write through them in the invoke body, and addressable
          local returns are loaded before lifetime cleanup.
    - [x] 2026-05-14 LLVM regression coverage now checks the first heap
          captured-closure lowering slice: heap closure creation emits
          `__stark_heap_alloc`, returns the heap environment pointer rather than
          a stack environment pointer, and clears invalid `nofree` /
          `memory(none)` attributes on the allocating function.
    - [x] 2026-05-14 LLVM regression coverage now checks heap closure drop
          lowering: heap closure values carry a drop pointer, caller functions
          that drop heap closures do not get `nofree` / `memory(none)`, and
          moving an owned dynamic value into a heap closure emits a
          layout-specific drop function that frees the captured dynamic storage
          before freeing the heap environment.
    - [x] 2026-05-14 LLVM regression coverage now checks optimized inline
          closure specialization for nested, control-flow, named-borrow,
          mutable, once, and imported package-template cases. The package
          regression also asserts that the manifest typed body records
          `closure-call`, so later package-image changes cannot silently fall
          back to source text or declaration-only lowering.
    - [x] 2026-05-14 regression coverage now checks named function-item
          promotion into explicit closure targets. Type-checking tests assert
          `borrow`, `inline`, and `heap` closure promotion records for the
          same source function. LLVM tests assert that O0 emits an
          empty-environment adapter and that O3 devirtualizes the call,
          removes the adapter, and leaves no borrowed-closure temp alloca or
          indirect call in the optimized `Run` body.
    - [x] 2026-05-14 verification: `dotnet test -c Release
          tests/compiler.Tests/compiler.Tests.csproj --no-restore` passed
          1370 tests after borrowed-closure escape validation, heap capture
          escape validation, and move-capture ownership transfer landed.
    - [x] 2026-05-14 language reference and book callable-value coverage were
          updated for `inline closure`, `borrow closure`, `mut`/`once`
          capabilities, and `heap closure` retained callbacks. The compiled
          book sample now exercises thin `fnptr`, inline closure
          specialization inputs, borrowed closure views, and heap closure
          values. Verification: `dotnet run -c Release --project
          src/compiler.csproj -- site/assets/book/samples/callable-values.stark
          --check` succeeded, and `dotnet test -c Release
          tests/compiler.Tests/compiler.Tests.csproj --no-restore` passed
          1381 tests after the final closure validation slice.



## 6 Project Testing and `System.Testing`

- [x] Define the Stark test-project model.
  - [x] Model keywords and syntax after Xunit, such as `[Fact]`.
    - Done: `[Fact]` attributes are valid source metadata on test functions;
      test discovery remains explicit in `main`, and `[Theory]` is reserved for
      the later data-driven runner rather than implied today.
  - [x] decide whether test projects are a separate `kind = "test"` manifest kind or executable projects with test metadata
    - Done: test projects use separate `kind = "test"` manifests with a
      `[test]` root/output table.
  - [x] define how solution manifests identify default test sets
    - Done: solution `[defaults].test` lists default test targets; absent
      defaults run all solution members with `kind = "test"`.
  - [x] keep test discovery explicit and static; avoid runtime reflection as a required language feature
    - Done: test executables call facts directly through ordinary Stark code.
- [x] Add a standard-library testing module inspired by xUnit.
  - [x] add a `System.Testing` module or equivalent package-facing testing root
    - Done: `System.Testing` is packaged with `System` but not root-re-exported.
  - [x] port the core assertion vocabulary needed by the current C# xUnit tests, such as truth checks, equality checks, and failure reporting
    - Done: `True`, `False`, `Fail`, scalar/text `Equal`, `RunFact`, and
      `ExitCode` provide the first assertion/reporting vocabulary.
  - [x] model assertion failure using Stark's no-exception failure/result story rather than hidden unwinding
    - Done: assertions return `bool`; `RunFact` returns `0` or `1`; test
      projects return process exit codes.
  - [x] keep allocation and formatting costs explicit so test-only helpers do not leak into normal runtime expectations
    - Done: helpers write literal pass/fail prefixes and caller-provided names
      through `System.Console`; no reflection or hidden exception payloads.
- [x] Implement `stark test` on top of test projects.
  - [x] build test projects through the existing project/solution manifest driver
  - [x] run produced test executables and map their results into concise CLI output
  - [x] support solution-level test aliases and default test sets
  - [x] preserve `--dev`, `--release`, path dependencies, and package-backed dependencies for tests
- [x] Add examples and docs for Stark-native tests.
  - [x] add at least one standard-library test project using `System.Testing`
    - Done: `examples/standard-library-tests` is a `kind = "test"` project
      wired into `examples/Stark.solution.toml`.
  - [x] document how to port existing xUnit-style test cases into Stark test projects
    - Done: `docs/Userfacing/ProjectsAndSolutions.md`,
      `docs/StandardLibrary/System.Testing.md`, and book Chapter 24 document
      explicit fact runners and no-exception assertions.
  - [x] add regression coverage for project-local and solution-level `stark test`
    - Done: integration coverage exercises project-local test runs, failing
      test exit-code mapping, and solution default test targets with path
      dependencies.


## 7. Add macOS Standard Library Platform Backend

- [ ] Create a macOS OS-backed platform implementation.
  - [x] Add `System.Runtime.Platform.MacOS.stark`.
  - [x] Add a macOS dispatch template.
  - [x] Add target detection and package image support for macOS triples.
  - [x] Implement file open, read, write, seek, close, and flush.
  - [x] Implement directory create, delete, open, read, and close.
  - [x] Implement path normalization, current directory, existence, file kind,
        and the currently exposed metadata APIs.
  - [x] Implement console stdout, stderr, stdin, terminal detection, and Unicode
        handling.
  - [x] Implement memory allocation and reallocation using the chosen macOS
        backend.
  - [x] Support macOS object emission for runtime allocator helpers without
        Mach-O COMDATs and with an AArch64-compatible trap calling convention.
  - [x] Implement process exit and process ID.
  - [x] Implement threading: start, join, detach, yield, and sleep.
    - [x] Preserve thread entry return codes through `pthread_join`.
  - [x] Implement TCP sockets and readiness behavior.
  - [x] Implement time or timing hooks needed by benchmarks.
    - Done: standard-library sleep is `nanosleep`-backed on macOS; benchmark
      timing remains host-harness driven until a public clock API lands.
  - [ ] Add macOS-specific correctness tests for each standard library module.
    - [x] Add focused compiler and stdlib coverage for macOS dispatch routing,
          libSystem/POSIX calls, allocator ABI, Mach-O IR shape, object
          emission, package manifests, and raw-pointer boundary documentation.
    - [x] Add focused coverage for macOS `stat`-backed path metadata and
          `pthread_join` return-code preservation.
    - [x] Run a batch-1 Stark-only benchmark sweep on macOS.
    - [x] Add cross-language C/Rust comparison once `rustc` is available in the
          benchmark environment.
  - [x] Document macOS platform behavior and unsupported APIs.
    - [x] Document the current libSystem/POSIX backend and the Apple SDK/Command
          Line Tools requirement for final native linking.

## 8. Update Website Book (/site/content/book)

- [x] Update the book portion of the website.
  - [x] Convert the book plan into website pages with stable URLs.
  - [x] Make every chapter a tutorial that builds on previous chapters.
  - [x] Add content for any planned chapters that do not currently exist.
  - [x] Renumber chapters after addition of new ones.
  - [x] Include multiple code examples per chapter.
  - [x] Add compile checks for code examples where possible.
  - [x] Add navigation, previous/next links, and version/release labels.
  - [x] Add generated chapter checkpoints so each numbered tutorial ends with
    concrete outcomes derived from its steps.
  - [x] Add generated lesson paths so each numbered tutorial starts with a
    clickable sequence of the chapter's steps.
  - [x] Add a book-structure guard so site builds fail if numbered chapters
    lose tutorial steps, examples, navigation, or gain placeholder prose.
  - [x] Keep the language reference separate from tutorial material.
  - [x] Update examples in /examples to use latest language capabilities.
  - Done: numbered website book chapters now cover chapters 1-36 with stable
    URLs, previous/next links, v1.35 draft labels, chapter-specific reference
    links, and checked code examples. Chapter 28 is now Performance Tuning and
    Chapter 29 is now Unsafe Stark and Raw Pointers; diagnostics, generated IR,
    and project chapters were renumbered to 30-36 with aliases for old draft
    URLs. Numbered chapters are step-oriented tutorials; appendices remain
    compact reference material. A follow-up pass reworked standard-library,
    generated-IR, command-line-tool, and current-boundary text away from
    reference-style summaries and toward buildable tutorial flow. A second pass
    reworked the remaining core-language, package/boundary, ABI/numeric,
    diagnostics, and project chapter steps into action-oriented tutorial
    instructions. A final sweep brought the early-language, arrays/text,
    testing, performance-model, performance-tuning, and unsafe chapters into
    the same action-step style. Numbered chapters now render an automatic
    Lesson Path and Chapter Checkpoint from their `## Step N:` headings so
    readers get a clickable route before starting and a clear set of tutorial
    outcomes before moving on. The site build now runs `check-book-structure.sh`
    before Hugo so numbered chapters cannot silently regress to reference-only
    pages without tutorial steps, examples, navigation, or placeholder-prose
    checks. Hosted entrypoint examples now use safe `export fn main` unless
    they actually need unsafe or foreign ABI features. The examples README was
    updated after verifying the
    `standard-library` example builds through the project driver.

### Chapter Checklist

- [x] Chapter 1: Introduction: Why Stark Exists
- [x] Chapter 2: Installing Stark and Building Programs
- [x] Chapter 3: Hello, Stark
- [x] Chapter 4: A Small Stark Tour
- [x] Chapter 5: Values, Types, and Ranges
- [x] Chapter 6: Bindings, Mutation, and Control Flow
- [x] Chapter 7: Ownership, Moves, and Cleanup
- [x] Chapter 8: Borrowing in Stark
- [x] Chapter 9: Stark Borrowing Compared With Rust
- [x] Chapter 10: Storage Classes and Lifetimes
- [x] Chapter 11: Aggregates and Layout-Aware Design
- [x] Chapter 12: Enums and Pattern Matching
- [x] Chapter 13: Arrays, Slices, Text, and Views
- [x] Chapter 14: Modules, Visibility, and Packages
- [x] Chapter 15: Function Guarantees and Effects
- [x] Chapter 16: Errors Without Exceptions
- [x] Chapter 17: Generics, Traits, Doctrines, and Specialization
- [x] Chapter 18: Callable Values and Thread Entries
- [x] Chapter 19: FFI, Raw Pointers, Function Pointers, and Native Packages
- [x] Chapter 20: Console, Process, and Platform Basics
- [x] Chapter 21: Memory and Collections
- [x] Chapter 22: Files, Directories, Paths, and Text
- [x] Chapter 23: Threading and TCP
- [x] Chapter 24: Testing Stark Code
  - Done: the website book chapter now documents `kind = "test"`,
    `System.Testing`, explicit fact runners, solution default test sets, and
    `stark test`.
- [x] Chapter 25: Stark's Performance Model
- [x] Chapter 26: Memory Layout, ABI, and Interop Expectations
- [x] Chapter 27: Integer, Floating-Point, and Overflow Policy
- [x] Chapter 28: Performance Tuning, Independent loops, inline, overlap/same/disjoint params, const params
- [x] Chapter 29: Unsafe Stark and raw pointers
- [x] Chapter 30: Reading Stark Diagnostics
- [x] Chapter 31: Looking at Generated IR
- [x] Chapter 32: Project: Command-Line Text Tool
- [x] Chapter 33: Project: Multi-Module Package
- [x] Chapter 34: Project: File Processing Utility
- [x] Chapter 35: Project: Native-Backed Package
- [x] Chapter 36: Project: Performance Case Study
- [x] Appendices
  - [x] Keywords and reserved words
  - [x] Operators and symbols
  - [x] Integer widths and range rules
  - [x] Function kinds and guarantees
  - [x] Storage classes and ownership quick reference
  - [x] Package manifest reference
  - [x] Current boundaries
  - [x] Stark for Rust programmers
  - [x] Stark for C# programmers
  - [x] Stark for C programmers

## 9. GitHub Release Pipeline

- [x] Create GitHub Actions release pipeline for Linux and Windows.
  - Done: `.github/workflows/release.yml` builds `linux-x64` and `windows-x64`
    release artifacts on tag pushes and manual release-candidate dispatches.
- [ ] Add macOS to the release workflow after the macOS standard-library backend
      exists.
  - Skipped for this Windows pass with the rest of the macOS-specific work.
- [x] Add build matrix for supported Linux and Windows host/target triples.
- [x] Build compiler binaries for Linux and Windows.
- [x] Build and package the promoted standard library.
- [x] Run parser, compiler, standard library, feature, and integration tests.
- [x] Run focused runtime smoke tests per OS.
  - Done: the workflow runs `stark test standard-library-tests --release`.
- [x] Package release archives with compiler, standard library, templates,
        docs, examples, and license files.
  - Done: `scripts/package-release.ps1` stages compiler publish output, the
    standard-library package image/native library, templates, docs, examples,
    `README.md`, `LICENSE`, and a `VERSION` file.
- [x] Generate checksums for every artifact.
- [x] Add version stamping from tags.
- [x] Generate draft release notes from changelog or commit metadata.
- [x] Upload artifacts to GitHub Releases.
- [x] Add manual dispatch for release candidates.
- [x] Add post-release install smoke tests that download the artifacts and
        compile a small Stark program on each OS.
- [x] Cache toolchains and dependencies without making release outputs depend
        on stale caches.


## 10. Performance Tuning

### Investigate/Triage, Output is tasks in Fix

#### Slower than Rust
- [x] benchmarks/collections/DictionaryMixed — 2026-05-11 rerun: Rust `984 us`, Stark `1210 us`; active, see fix task below.
- [x] benchmarks/collections/QueueDequeue — stale after queue storage fix; 2026-05-11 rerun: Rust `975 us`, Stark `945 us`.
- [x] benchmarks/collections/QueueGrowth — stale after queue storage fix; 2026-05-11 rerun: Rust `922 us`, Stark `864 us`.
- [ ] benchmarks/io/DirectoryEnumeration — 2026-05-11 rerun: Rust `3783 us`, Stark `4016 us`; active, with compile/IR size larger than runtime gap.
- [x] benchmarks/io/FileBufferedReadWrite — stale after byte-write buffering fix; 2026-05-11 rerun: Rust `2103 us`, Stark `2087 us`.
- [ ] benchmarks/io/FileSystemPathTranscode — rust 1.104452, stark 1.208904
- [ ] benchmarks/micro/AggregatePhiFieldForwarding — rust 0.974632, stark 0.997971
- [x] benchmarks/micro/AlgebraicIdentitySimplification — rust 1.014934, stark 1.022554
- [ ] benchmarks/micro/ExplicitArithmeticRangePruning — rust 0.990526, stark 1.014211
- [ ] benchmarks/micro/FunctionPointerDevirtualization — rust 1.007611, stark 1.01945
- [ ] benchmarks/network/TcpScatterGatherLoopback — rust 0.970315, stark 1.196042
- [x] benchmarks/text/IntegerFormatting — rust 1.106406, stark 627.29316
- [ ] benchmarks/text/PathJoin — rust 1.075163, stark 1.094771
- [ ] benchmarks/text/PathRepeatedSmallOps — rust 1.029443, stark 1.07571
- [ ] benchmarks/text/TextParsing — rust 1.092818, stark 1.319337
- [x] benchmarks/text/UnicodeFormatting — rust 1.047867, stark 594.463059



#### Slower than C
- [ ] benchmarks/collections/DictionaryInsert — stark 1.01573
- [ ] benchmarks/collections/DictionaryLookup — stark 1.063474
- [ ] benchmarks/collections/DictionaryMixed — stark 1.059754
- [ ] benchmarks/collections/LinkedListBuildClear — stark 1.022678
- [ ] benchmarks/collections/LinkedListPush — stark 1.020937
- [ ] benchmarks/collections/ListIteration — stark 1.099882
- [ ] benchmarks/collections/QueueChurn — stark 1.060086
- [ ] benchmarks/collections/QueueDequeue — 2026-05-11 rerun: C `961 us`, Stark `999 us`
- [ ] benchmarks/collections/QueueGrowth — 2026-05-11 rerun: C `860 us`, Stark `890 us`
- [ ] benchmarks/console/ConsoleWrites — stark 1.096863
- [ ] benchmarks/io/DirectoryEnumeration — stark 1.295304
- [ ] benchmarks/io/FileBufferedReadWrite — stark 1.893304
- [ ] benchmarks/io/FileSystemPathTranscode — stark 1.208904
- [ ] benchmarks/micro/AbstractionGenericWrapper — stark 1.002597
- [ ] benchmarks/micro/AbstractionHandWritten — stark 1.008162
- [ ] benchmarks/micro/AlgebraicIdentitySimplification — stark 1.022554
- [ ] benchmarks/micro/BitwiseRangePruning — stark 1.01355
- [ ] benchmarks/micro/BranchSelectPredication — stark 1.063505
- [ ] benchmarks/micro/Branching — stark 1.018312
- [ ] benchmarks/micro/Calls — stark 1.01224
- [ ] benchmarks/micro/DirectCallInlining — stark 1.001287
- [ ] benchmarks/micro/ExplicitArithmeticRangePruning — stark 1.014211
- [ ] benchmarks/micro/FunctionPointerDevirtualization — stark 1.01945
- [ ] benchmarks/micro/StackFieldBranchForwarding — stark 1.001637
- [ ] benchmarks/micro/StackFieldLoadForwarding — stark 1.017903
- [ ] benchmarks/micro/StackNestedFieldForwarding — stark 1.006009
- [ ] benchmarks/micro/StackScalarLoadForwarding — stark 1.015369
- [ ] benchmarks/network/TcpScatterGatherLoopback — stark 1.196042
- [ ] benchmarks/runtime/RuntimeBufferDynamic — stark 1.05191
- [ ] benchmarks/text/AsciiToUnicodeConversion — stark 1.0301
- [ ] benchmarks/text/AsciiToUnicodeConversionRuntime — stark 1.082843
- [ ] benchmarks/text/AsciiToUnicodeWideningKernel — stark 1.018182
- [ ] benchmarks/text/IntegerFormatting — stark 627.29316
- [ ] benchmarks/text/OwnedPathAllocation — stark 1.009137
- [ ] benchmarks/text/PathJoin — stark 1.094771
- [ ] benchmarks/text/PathNormalize — stark 1.095768
- [ ] benchmarks/text/PathQueries — stark 1.002167
- [ ] benchmarks/text/PathRepeatedSmallOps — stark 1.07571
- [ ] benchmarks/text/TextConcatCopy — stark 1.070156
- [ ] benchmarks/text/TextParsing — stark 1.319337
- [ ] benchmarks/text/UnicodeFormatting — stark 594.463059

### Fix

- [x] Carry `willexit` loop progress through MIR/SSA and LLVM loop metadata.
  - Source guarantee: `willexit` is an explicit loop behavior and is the only loop
    behavior accepted inside `finite` functions, so it represents a real progress
    contract rather than a style marker.
  - Gap fixed: MIR and SSA terminators now preserve loop behavior separately from
    loop memory contracts. Source loops and imported package-image template loops
    attach `LoopBehavior` to the real loop backedge. `while` `continue` edges
    carry the same backedge metadata; `for` `continue` edges jump to the iterator
    block, leaving the iterator-to-condition latch as the metadata-bearing edge.
  - LLVM lowering now emits `!llvm.loop.mustprogress` for `willexit` loop
    backedges, keeps `!llvm.loop.parallel_accesses` gated on validated
    `independent` memory contracts, and preserves loop metadata through SSA
    terminator rewrites that keep the loop latch.
  - Regression coverage: SSA tests confirm `willexit` behavior survives lowering,
    including `while` `continue` backedges. LLVM tests confirm plain `willexit`
    loops emit `mustprogress`, `non-deterministic` loops do not, and existing
    `independent` loop metadata continues to compile.
- [x] Emit LLVM `nuw` for ordinary unsigned integer `add`, `sub`, and `mul`.
  - Source guarantee: ordinary signed and unsigned integer overflow is UB in
    Stark; wrapping and saturating arithmetic require explicit operators.
  - Gap fixed: unsigned-width ordinary arithmetic no longer waits for range proof
    before emitting `nuw`. This carries the language contract to LLVM for
    unconstrained `uN[0 max]` arithmetic while preserving the existing rule that
    unsigned operations get `nsw` only when signed-result ranges are proven.
  - Regression coverage: LLVM tests now require `add nuw`, `sub nuw`, and
    `mul nuw` for unconstrained `u32[0 max]` arithmetic and keep wrapping and
    saturating operators flag-free.
  - Verification: full `dotnet test -c Release
    tests/compiler.Tests/compiler.Tests.csproj` passed 1316 tests;
    `scripts/build-stdlib.sh` rebuilt `stdlib/dist/libSystem.a` and
    `stdlib/dist/libSystem.starkpkg.json`; `git diff --check` passed. Focused
    100-run micro benchmark results:
    `Arithmetic` Stark/C `0.963391`, Rust/C `1.041618`;
    `ExplicitArithmeticRangePruning` Stark/C `1.024621`, Rust/C `1.033333`;
    `AlgebraicIdentitySimplification` Stark/C `0.990727`, Rust/C `1.021303`.
- [x] Propagate zero-copy ASCII text-slice literal payload facts into
      ASCII-to-Unicode conversion specialization.
  - Source guarantee: `text[start, length]` is a zero-copy text view over the
    same backing storage, so an exact slice of a known ASCII literal has an exact
    sliced payload without materializing temporary text.
  - Gap fixed: SSA value facts now derive exact payloads for compile-time exact
    text slices such as `"abcdef"[2, 3]`, preserve compatible text payload facts
    through ordinary local stores/loads when the local address has not been
    taken, and keep dynamic-start/dynamic-length slices payload-unknown while
    retaining safe length facts. The SSA and LLVM ASCII-to-Unicode call-site
    specialization paths also resolve known ASCII literal payloads through
    `use`, exact text slices, safe local loads, and integer casts used for exact
    slice bounds.
  - Correctness guard: payload facts are not preserved for address-taken text
    locals, preventing stale literal facts when raw-memory mutation could change
    the local slot.
  - Regression coverage: SSA tests require `"abcdef"[2, 3]` to have exact
    payload `"cde"`, dynamic-start slices to keep unknown payload, and
    address-taken locals to avoid stale payload facts. LLVM tests require
    `TryConvertAsciiToUnicode(&ownedUnicode, source)` where `source` is
    `"abcdef"[2, 3]` to emit the specialized stores for `c`, `d`, and `e`, while
    the dynamic-slice case keeps the runtime call.
  - Verification: full `dotnet test -c Release
    tests/compiler.Tests/compiler.Tests.csproj` passed 1321 tests;
    `scripts/build-stdlib.sh` rebuilt `stdlib/dist/libSystem.a` and
    `stdlib/dist/libSystem.starkpkg.json`. Focused 100-run benchmark results
    from `results-20260514T050133Z.7CjUf9.csv`:
    `AsciiToUnicodeConversion` Stark/C `1.001949`, Rust/C `1.056530`;
    `AsciiToUnicodeConversionLargeLiteral` Stark/C `0.960735`, Rust/C
    `1.865104`; `AsciiToUnicodeConversionRuntime` Stark/C `1.053077`, Rust/C
    `1.061154`; `AsciiToUnicodeConversionTinyLiteral` Stark/C `0.965931`,
    Rust/C `1.032177`.
- [x] Emit minimum dereferenceability for positive runtime bounded raw-pointer
      counts.
  - Source guarantee: `rawptr<T>[count]` and `rawmutptr<T>[count]` require a
    valid contiguous `count`-element region, and a count whose type has a
    positive lower bound also proves at least `min(count) * sizeof(T)` bytes are
    dereferenceable and the pointer is non-null.
  - Gap fixed: ABI lowering now emits `dereferenceable(minCount * sizeof(T))`
    alongside the existing `nonnull` and `align` attributes for bounded raw
    pointer parameters with positive runtime count ranges. The same attribute is
    available to function-pointer call operands, so indirect calls receive the
    strongest LLVM ABI facts expressible as constant attributes.
  - Backend constraint discovered: the attempted full dynamic
    `llvm.assume` form using `dereferenceable(count * sizeof(T))` and
    `dereferenceable_or_null(...)` operand bundles is rejected by the installed
    LLVM 22 verifier during object emission because the dereferenceability byte
    count must be a constant integral value there. The compiler therefore keeps
    full runtime counts as internal bounded-region facts for loop/access
    optimization and emits the sound constant lower-bound fact to LLVM.
  - Regression coverage: LLVM tests require `rawptr<i32>[count]` with
    `u8[1 10] count` to emit `dereferenceable(4)` on the definition and on an
    indirect function-pointer call operand. The zero-allowed `u8[0 10]` case
    remains nullable and does not receive `nonnull` or `dereferenceable`.
  - Verification: focused
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj --filter
    "FullyQualifiedName~LlvmIrEmissionTests.PositiveVariableBoundedRawPointerParametersEmitMinimumDereferenceabilityAttributes|FullyQualifiedName~LlvmIrEmissionTests.FunctionPointerCallsWithPositiveVariableBoundedRawPointerParametersEmitCallAttributes"`
    passed 2 tests; the full `LlvmIrEmissionTests` filter passed 356 tests; and
    full `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj`
    passed 1321 tests. `scripts/build-stdlib.sh` rebuilt
    `stdlib/dist/libSystem.a` and `stdlib/dist/libSystem.starkpkg.json`. A
    native `--emit-obj -O0` smoke test for positive and zero-allowed bounded
    raw-pointer parameters succeeded with the installed LLVM backend.
  - 100-run benchmark verification: `IndependentRawPointerRegions` from
    `results-20260514T051846Z.16lFLs.csv` recorded Stark/C `0.964066`, Rust/C
    `1.004728`; `MemoryCopyFill` from `results-20260514T051858Z.SS3uxg.csv`
    recorded Stark/C `0.906461`, Rust/C `1.013500`.
- [x] Promote bounded raw-pointer regions into SSA facts and consume them during
      LLVM emission.
  - Gap fixed: `rawptr<T>[count]` / `rawmutptr<T>[count]` parameters now publish
    a bounded-region fact in `SsaValueFacts`, including the count value, count
    range, and element alignment when known. Aliases and representation-preserving
    raw-pointer casts preserve the fact, and `slice(pointer, count)` turns it
    into slice length and backing-region facts.
  - LLVM use: optimized emission now queries the SSA fact for raw-pointer GEP
    `inbounds nuw` decisions, slice-element GEP flags, slice data alignment, and
    direct/indirect call-site argument attributes. Zero-allowed counts stay
    nullable at the ABI boundary, while positive caller facts can strengthen
    bounded raw-pointer call operands with `nonnull`, `dereferenceable`, and
    `align`.
  - Regression coverage: SSA tests cover positive and zero-allowed bounded
    pointer facts and `slice(pointer, count)` propagation. LLVM tests cover
    bounded raw-pointer GEP flags, slice-element GEP flags, and strengthened
    direct plus indirect call operands. A related SSA cleanup regression test
    proves unused plain `fnptr<fn void(...)>` indirect calls are not removed as
    pure expressions; effectful callback calls must remain unless the compiler
    has a sound callable-kind/effect proof to erase them.
  - Verification: full `dotnet test -c Release
    tests/compiler.Tests/compiler.Tests.csproj` passed 1330 tests, focused
    `LlvmIrEmissionTests` passed 363 tests, focused `SsaOptimizationTests`
    passed 103 tests, focused `SsaEmitterCoverageMatrixTests` passed 2 tests,
    and `scripts/build-stdlib.sh` rebuilt the standard library package. 100-run
    benchmark results: `IndependentRawPointerRegions` from
    `results-20260514T055624Z.Eu94hs.csv` recorded Stark/C `0.910714`, Rust/C
    `0.998168`; `MemoryCopyFill` from
    `results-20260514T055636Z.rCCwFB.csv` recorded Stark/C `0.912706`, Rust/C
    `1.018914`.
- [x] Preserve subregion memory contracts as region facts instead of flattening
      them to whole-parameter names.
  - Gap fixed: `ParameterDisjointGroup` now carries optional
    `ParameterMemoryRegion` operands, preserving `where
    disjoint(source[start, count], destination[0, count])` through syntax
    models, typed signatures, package-image manifests/source reconstruction,
    MIR, SSA, and LLVM emission. Whole-parameter default non-overlap remains the
    fast path, while subregion groups are explicitly excluded from LLVM
    parameter `noalias` derivation so they do not overstate whole-parameter
    independence.
  - Front-end use: call validation now substitutes argument roots and integer
    range facts into subregion contracts. Overlap-capable APIs can require
    exact subregion disjointness, and same-root calls are accepted only when the
    requested intervals are proven non-overlapping. Imported package-image
    functions preserve and enforce the same subregion contracts.
  - LLVM use: runtime `if disjoint(...)` and `assume disjoint(...)` on raw
    pointer subregions now create region-scoped roots such as
    `param.ptr[0..3]`. Loads, stores, and raw slices receive scoped
    `!alias.scope` / `!noalias` metadata only when the accessed element range is
    covered by the dominated runtime fact; metadata does not escape the true
    branch.
  - Regression coverage: diagnostic tests cover safe and rejected same-root
    calls to an overlap-capable API refined by subregion `where disjoint(...)`;
    package-image tests prove imported subregion contracts survive publication;
    LLVM tests cover same-base runtime subregion metadata, no metadata leakage
    outside the dominated branch, and raw-slice metadata preservation.
  - Verification: full `dotnet test -c Release
    tests/compiler.Tests/compiler.Tests.csproj` passed 1335 tests; focused
    `LlvmIrEmissionTests`, `DiagnosticRegressionTests`,
    `PackageImageArchitectureTests`, `PackageImageCallableValueTests`, and
    `SsaOptimizationTests` passed 615 tests; `scripts/build-stdlib.sh` rebuilt
    the standard-library package. 100-run benchmark results:
    `IndependentRawPointerRegions` from `results-20260514T062642Z.MsRcry.csv`
    recorded Stark/C `0.898190`, Rust/C `0.983258`; `MemoryCopyFill` from
    `results-20260514T062656Z.SmwDeI.csv` recorded Stark/C `0.953261`, Rust/C
    `1.162815`; `IndependentSliceAdd` from
    `results-20260514T062726Z.pPnNS8.csv` recorded Stark/C `0.980750`, Rust/C
    `1.052320`.
- [x] Lower `where same(...)` as alias equivalence classes instead of dropping
      the identity relation after type checking.
  - Gap fixed: `SameGroups` now flow through MIR and SSA alongside disjoint
    groups, and synthetic signatures rebuilt from SSA keep both relation sets.
    Function-body type checking also installs same-region facts into the local
    proof scope, so a function with `where same(left, right)` can satisfy a
    nested direct or indirect call that requires the same relation without
    weakening the call contract.
  - LLVM use: same-related parameters canonicalize to one scoped alias class
    when that class is proven disjoint from the remaining memory-backed
    parameters. Accesses through both names therefore share one alias scope and
    receive `!noalias` against default-disjoint third regions, while same-related
    parameters themselves are still not marked with LLVM parameter `noalias`.
  - Equality assumptions: function entry now emits `llvm.assume` equality facts
    where the ABI representation makes them sound. Raw pointers and indirect
    memory-backed parameters get pointer-equality assumes; direct slice/text
    views get data-pointer and length equality assumes.
  - Regression coverage: LLVM tests cover raw-pointer same classes against a
    third default-disjoint parameter, direct slice data/length equality assumes,
    absence of separate parameter scopes for same-related names, and indirect
    function-pointer calls with `where same(arg0, arg1)` suppressing unsound
    `noalias` between same arguments.
  - Verification: focused same-contract LLVM tests passed 3 tests; focused
    `LlvmIrEmissionTests`, `DiagnosticRegressionTests`,
    `FunctionSemanticsTests`, `TypeCheckingTests`,
    `PackageImageArchitectureTests`, `PackageImageCallableValueTests`, and
    `SsaIrValidationTests` passed 753 tests. Full `dotnet test -c Release
    tests/compiler.Tests/compiler.Tests.csproj` passed 1338 tests, and
    `scripts/build-stdlib.sh` rebuilt the standard-library package. 100-run
    benchmark results: `TextConcatCopy` from
    `results-20260514T064653Z.iUOL8G.csv` recorded Stark/C `0.982558`, Rust/C
    `1.111111`; `IndependentSliceAdd` from
    `results-20260514T064707Z.YUSLHM.csv` recorded Stark/C `0.961928`, Rust/C
    `1.023133`; `DictionaryLookup` from
    `results-20260514T064721Z.XIgBYz.csv` recorded Stark/C `1.005646`, Rust/C
    `1.243413`.
- [x] Preserve `init`/`out` initialization writes through MIR and SSA.
  - Gap fixed: MIR and SSA stores now carry a `MemoryWriteKind`, and explicit
    `init target = value`, assignments into `out`/`init` destinations, aggregate
    memory copies, imported-template `init =` assignments, and local declaration
    initializers preserve `Initialization` instead of degrading to ordinary
    replacement writes. SSA rewrite/optimization helpers preserve the write kind
    when they rewrite store and copy operands.
  - Correctness behavior: initialization writes no longer request whole-value
    replacement/drop behavior for the destination before constructing the new
    value. Ordinary mutable-borrow and replacement assignments still lower as
    `Replacement`, so destructible replacement semantics remain separate from
    write-only initialization semantics.
  - LLVM behavior: existing `out`/`init` ABI and call-site attributes remain in
    force, and direct initialization element stores are emitted without reading
    the destination element before the first write. The store kind is preserved
    through optimized SSA so LLVM lowering can keep initialization-only writes
    distinct from replacement writes.
  - Regression coverage: MIR tests cover `init destination[0] = value` and
    `out` parameter assignment lowering to initialization writes; SSA tests cover
    the same positive cases plus a negative mutable-slice replacement assignment;
    LLVM tests prove an `init T[]` element fill does not load the destination
    element before writing it.
  - Verification: focused initialization-write tests passed 5 tests; focused
    `MidLevelIrLoweringTests`, `SsaLoweringTests`, `LlvmIrEmissionTests`,
    `SsaOptimizationTests`, and ownership-related tests passed 658 tests. Full
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj` passed
    1343 tests, and `scripts/build-stdlib.sh` rebuilt the standard-library
    package. 100-run benchmark results: `MemoryCopyFill` from
    `results-20260514T070355Z.sgtBp5.csv` recorded Stark/C `0.963693`, Rust/C
    `1.231846`; `TextConcatCopy` from `results-20260514T070402Z.h28oSS.csv`
    recorded Stark/C `0.884502`, Rust/C `1.018268`.
- [x] Carry dynamic-storage length/capacity facts through SSA and LLVM.
  - Gap fixed: `SsaValueFacts` now records dynamic-storage capacity and
    initialized-prefix ranges alongside existing length facts. Fresh
    `new dynamic T(capacity)` publishes exact `Length = 0`, exact known capacity,
    initialized-prefix `0`, and the invariant `0 <= Length <= Capacity`.
    Dynamic `Length`, `Capacity`, and positive-capacity `Data` field extraction
    consumes those facts; `Data` becomes non-null and element-aligned when
    capacity is proven positive.
  - Local-state behavior: a branch-sensitive dynamic-storage local fact pass
    tracks stores, loads, `Reserve`, `TryReserve`, `TryReserveCapacity`,
    `MoveLast`, and `MoveAt` through addressable local owners. `TryReserve`
    success edges get `Capacity >= Length + additional`, failure edges restore
    the pre-call header facts, and successful move operations publish the
    post-move dense-prefix length. Unknown indirect writes to a dynamic owner
    invalidate the local header facts instead of preserving stale information.
  - LLVM behavior: optimized branch pruning can now remove impossible
    post-reserve capacity checks, and dynamic `MoveLast`/`MoveAt` element
    pointers are emitted as `getelementptr inbounds nuw` on the proven non-trap
    path.
  - Regression coverage: SSA tests cover fresh allocation field facts,
    positive-capacity data-pointer facts, branch-sensitive `TryReserve`
    success/failure facts, and `MoveLast` length commits. LLVM tests cover
    post-`TryReserve` capacity branch pruning and `inbounds nuw` dynamic move
    GEPs.
  - Verification: focused dynamic-storage SSA/LLVM tests passed 12 tests; full
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj` passed
    1347 tests; `scripts/build-stdlib.sh` rebuilt the standard-library package;
    and `git diff --check` passed. A 100-run
    `MemoryDynamicReserveGrowth` benchmark from
    `results-20260514T093125Z.vS6BKz.csv` recorded Stark/C `0.852192` and
    Rust/C `0.980632`.
- [x] Attach scoped alias and loop-access metadata to eligible direct and
      indirect calls.
  - Source guarantee: `unsafe assume disjoint(...)` and `if disjoint(...)`
    introduce scoped non-overlap facts, `independent` loops expose access-group
    facts, and accepted law/function-pointer calls carry memory-effect
    contracts proving when a call is limited to argument memory.
  - Gap fixed: LLVM body emission now threads `ScopedNoAliasGroups` and
    `LoopAccessGroups` from `SsaValueInstruction` into `EmitCall` and
    `EmitIndirectCall`. Eligible calls receive `!alias.scope` / `!noalias`
    metadata when all touched memory-backed argument roots resolve to the same
    scoped root model used by loads and stores. Memory-touching calls inside
    accepted independent loops receive `!llvm.access.group` so the loop latch's
    `!llvm.loop.parallel_accesses` applies to call memory as well.
  - ABI fact improvement: direct calls now render callee parameter contract
    attributes at the call site, matching the existing indirect function-pointer
    behavior. This exposes `nonnull`, `noalias`, `readonly`/`writeonly`,
    `nocapture`/`captures(...)`, `sret`, `byval`, `dereferenceable`, and
    alignment facts directly on the call operands instead of relying only on the
    callee declaration.
  - Correctness guard: call metadata is withheld when the function may read or
    write other memory, capture argument memory, touch FFI/asm/opaque memory, or
    when a touched memory argument cannot be resolved to a scoped root. Calls
    touching multiple roots only receive `!noalias` against roots not touched by
    that same call instruction.
  - Regression coverage: LLVM tests require scoped noalias metadata on an
    eligible call inside `assume disjoint(...)`, `!llvm.access.group` on a
    direct law helper call inside an `independent` loop, and no scoped/access
    metadata on an FFI call inside the same scoped-disjoint shape. Existing
    direct-call ABI tests were updated to assert the stronger call-site
    attributes.
  - Verification: the focused call-metadata tests passed; the full
    `LlvmIrEmissionTests` filter passed 359 tests; and full
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj` passed
    1324 tests. `scripts/build-stdlib.sh` rebuilt
    `stdlib/dist/libSystem.a` and `stdlib/dist/libSystem.starkpkg.json`.
    Focused 100-run benchmark ratios with C as `1.0`: from
    `results-20260514T053731Z.KKWueE.csv`,
    `FunctionPointerDevirtualization` Stark/C `1.018365`, Rust/C `1.020790`;
    from `results-20260514T053740Z.KbXsZ5.csv`, `DictionaryLookup` Stark/C
    `0.996845`, Rust/C `1.239748`; from
    `results-20260514T053747Z.Oeowwa.csv`, `AsciiToUnicodeConversion` Stark/C
    `0.978682`, Rust/C `1.048450`,
    `AsciiToUnicodeConversionLargeLiteral` Stark/C `0.964217`, Rust/C
    `1.838339`, `AsciiToUnicodeConversionRuntime` Stark/C `1.042650`,
    Rust/C `1.044174`, and `AsciiToUnicodeConversionTinyLiteral` Stark/C
    `1.005188`, Rust/C `1.126459`.
- [x] Propagate known closure invoke targets through SSA devirtualization and
      LLVM `!callees` metadata.
  - Source guarantee: a closure value carries a concrete invoke function pointer
    and environment pointer. For named function items promoted to closure
    values, the invoke target is a synthetic empty-environment adapter; for
    closure phis/selects, the possible invoke targets are the union of the
    visible incoming closure values. That target set is just as precise as the
    existing `fnptr` target set and should not be dropped when lowering extracts
    the closure invoke slot.
  - Gap fixed: SSA direct-call devirtualization now follows
    `extract closure.invoke` through closure values, value references, phis, and
    selects. Singleton closure invoke sets become direct calls, and synthetic
    function-item closure adapters are marked as real inline candidates so O3
    removes the adapter layer and calls the original function directly when the
    source function itself remains callable. LLVM emission uses the same
    closure-target walk for non-singleton sets and attaches `!callees` metadata
    to the remaining indirect closure invoke.
  - Correctness guard: the metadata names the actual invoke adapter functions,
    not the user source functions, because closure invokes have the hidden
    environment-parameter ABI. Multi-target closure calls remain indirect unless
    a later pass can legally specialize the branch or clone the call path.
  - Regression coverage: LLVM tests now require a local function-item closure
    call to devirtualize through the empty-environment adapter at O3, and require
    a two-target closure value to keep the indirect call while emitting an exact
    two-entry `!callees` adapter set with finite/law call attributes preserved.
  - Verification: focused closure/function-pointer target tests passed 4 tests;
    focused `LlvmIrEmissionTests` passed 388 tests; full
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj
    --no-restore` passed 1387 tests.
- [x] Preserve closure environment pointer extent and capture facts through
      synthetic lambda emission.
  - Source guarantee: a captured closure environment is a concrete compiler-
    generated stack or heap object with a known layout. The hidden closure
    invoke parameter is not an arbitrary nullable raw pointer in those bodies:
    captured closure lambdas receive a non-null environment pointer that is
    dereferenceable for the generated environment layout, aligned to that
    layout, private from ordinary source parameters, and not captured by the
    invoke body.
  - Gap fixed: LLVM emission now requests body-aware parameter effects for
    synthetic lambda/adapter functions instead of treating them like imported
    declarations. Captured closure lambdas also publish a precise `$env`
    `ParameterMemoryEffectSummary`, and the LLVM function-attribute builder now
    consumes semantic `GuaranteedNonNull`, `DereferenceableBytes`, and
    `AlignmentBytes` facts for direct raw-pointer parameters. As a result,
    captured closure invoke definitions now emit attributes such as `nonnull`,
    `dereferenceable(N)`, `align N`, `noalias`, and `nocapture` on `$env`
    whenever those facts are proven.
  - Correctness guard: empty-environment closures and function-item adapters do
    not receive non-null/dereferenceable environment attributes because their
    environment pointer may legitimately be `null`. The environment extent is
    derived from the generated environment named type, not from the erased
    `rawptr<i8>` ABI spelling.
  - Regression coverage: the captured-closure LLVM regression now checks the
    synthetic lambda header for `nonnull`, `dereferenceable(4)`, `align 4`,
    `noalias`, `readonly`, and `nocapture` on the hidden `$env` parameter while
    preserving the existing environment load behavior.
  - Verification: focused captured/non-capturing/function-item closure LLVM
    tests passed 3 tests; focused `LlvmIrEmissionTests` passed 388 tests; full
    `dotnet test -c Release tests/compiler.Tests/compiler.Tests.csproj
    --no-restore` passed 1387 tests; and an `--emit-obj -O0` smoke test for a
    captured closure succeeded.
