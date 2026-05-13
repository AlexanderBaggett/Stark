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
    - [ ] Introduce or complete a bound/typed executable expression model for
          calls, member calls, function-pointer calls, indexing/slicing,
          object/constructor creation, enum construction, dynamic-storage
          operations, text interpolation, `sizeof`/`alignof`, and switch shapes.
          MIR should consume this model instead of rediscovering validity from
          raw parse-tree shapes.
      - [ ] Define a closed `BoundOperation`/`TypedOperation` representation for
            executable expressions and statements, with variants for direct call,
            member call, function-pointer call, index/slice, constructor/object
            creation, enum value construction, dynamic-storage operation, text
            interpolation/building, layout query, and switch dispatch.
      - [ ] Populate bound operations during type checking for root source
            bodies, including exact overload symbols, receiver/addressability
            facts, result type, coercions, ownership effects, and any required
            ABI metadata.
      - [ ] Teach MIR lowering to consume bound operations for one operation
            family at a time, starting with calls and indexing, then
            object/enum construction, dynamic storage, text, and switch. Each
            migrated family should remove the equivalent raw parse-tree
            rediscovery path.
      - [ ] Add a debug/validation gate that rejects any executable expression
            reaching MIR without a bound operation when one is required.
    - [ ] Carry bound operation facts through source modules, imported package
          images, generic typed-template bodies, monomorphization, and inline
          clone planning so imported code has the same lowering contract as root
          source code.
      - [x] MIR direct/member call lowering first consults recorded typed call
            facts when present and preserves exact function-pointer promotion
            facts for package-image-backed overloaded function items.
      - [x] Generic materialized bodies keep local declaration types substituted
            through MIR, including locals resolved by type-check records instead
            of raw syntax fallback.
      - [ ] Extend package-image typed bodies to serialize every bound-operation
            family, not just the call/local facts currently needed by fixed
            regressions.
      - [ ] Add package producer/consumer tests that corrupt or remove source
            body text after package creation and still lower imported generic
            bodies from typed/bound facts alone.
      - [ ] Carry bound-operation substitutions through generic
            monomorphization, including substituted result/parameter types,
            enum payload layouts, dynamic-storage element layouts, and
            function-pointer ABI signatures.
      - [ ] Include inline clone reachability in the bound/imported model so
            inline clone emission is driven by reachable bound calls rather than
            opportunistic text/import scans.
    - [ ] Replace null-return "could not lower" paths for accepted constructs
          with exhaustive lowering over bound operation kinds. Any missing case
          should be an internal invariant failure until direct lowering is
          implemented.
      - [ ] Inventory all `return null`/nullable-result lowering helpers in
            `MidLevelIrLowering` and classify them as optional lookup, invalid
            source diagnostic, or compiler invariant.
      - [ ] For each bound-operation family migrated to the closed model, make
            the MIR lowerer use exhaustive `switch` handling and throw a
            lowering invariant on impossible or unimplemented bound variants.
      - [ ] Add regression tests that accepted constructs no longer produce null
            MIR operands, null assignment targets, placeholder expression
            statements, or declaration-only fallback artifacts.
    - [ ] Update MIR data structures so impossible values are not expressible
          where practical: void calls cannot be value operands, unresolved names
          cannot become operands, untyped indexes cannot become index rvalues,
          constructor calls cannot lack shape/body facts, and enum operations
          cannot lack layout/variant facts.
      - [ ] Split MIR call nodes into value-producing and statement-only forms
            so a `void` call cannot be embedded as a value operand.
      - [ ] Replace string/name-based unresolved operands with typed symbol
            references or explicit front-end diagnostics before MIR.
      - [ ] Replace generic index rvalues with typed index/slice rvalues that
            carry their operation family and validated element/view result type.
      - [ ] Make constructor/object/enum MIR nodes require resolved layout,
            constructor body/field mapping, enum variant, and payload projection
            facts in their constructors.
      - [ ] Update SSA lowering and package-image emission for each MIR data
            structure change, with compatibility tests for imported typed
            templates and inline clones.
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

## 2. Require `unsafe` For Raw Pointer Use

- [x] Enforce an `unsafe` requirement for raw pointer use.
  - [x] Define every operation that requires `unsafe`:
        `rawptr`, `rawmutptr`, dereference, pointer arithmetic, pointer casts,
        bounded raw pointer region construction, `null`, and raw FFI handles.
  - [x] Decide whether raw pointer type names in declarations require `unsafe`
        or whether only construction, dereference, and mutation require it.
  - [x] Update grammar and syntax model if `unsafe` blocks/functions are not
        already represented everywhere needed.
  - [x] Add semantic validation diagnostics for raw pointer use outside unsafe
        contexts.
  - [x] Require explicit unsafe context at FFI and platform boundaries.
  - [x] Add diagnostics that explain the safe alternatives: borrow, slice,
        dynamic, owned handle, or platform wrapper.
  - [x] Add parser, semantic, ownership, lowering, and codegen tests.
  - [x] Update language reference.
  - [x] Update book and style guide.

## 2.5. Make Pointers Non-Overlapping By Default

- [ ] Change Stark's pointer and pointer-like parameter aliasing model so
      function parameters are assumed to describe non-overlapping memory regions
      by default, and overlap must be requested explicitly.
  - [x] 2026-05-13 proposal direction: make non-overlap a callable-boundary
        contract, not a blanket property of every local pointer variable.
        Ordinary `fn`, `finite`, `law`, and `finite law` callable forms should
        treat memory-backed parameters as pairwise non-overlapping by default.
        The canonical opt-out forms should be relational clauses:
        `where overlap(a, b)` for APIs that permit overlapping regions and
        `where same(a, b)` for APIs that require the same region. Do not use
        `alias` for the same-region form because Stark already uses `alias` for
        type aliases and because aliasing usually means "may be the same"
        instead of "must be the same". Local pointers should keep
        provenance-derived alias facts: copying a pointer creates a same-region
        local alias, while taking addresses of distinct proven roots may create
        a local non-overlap fact. Unknown local/raw provenance remains unknown
        until proven by source-visible roots, range facts, `if disjoint(...)`, or
        a future explicit unsafe assertion form.
  - [ ] Define the source-level contract precisely.
      - [ ] Distinguish parameter-level from expression-level disjointness in the
            source-level contract. Parameter-level non-overlap is a call
            contract between the caller and callee for the full duration of the
            call. Expression-level `disjoint(...)` facts describe specific
            computed regions and may be static, branch-scoped, or loop-scoped.
      - [ ] Document that parameter-level non-overlap is now expressed by the
            default and that writing `disjoint` on whole parameters or in
            `where disjoint(a, b)` clauses over whole parameters is redundant.
      - [ ] Preserve `disjoint` for expression-level facts the default cannot
            express, including subregions of a single parameter
            (`where disjoint(buffer[0, n], buffer[n, 2*n])`), partial disjointness
            inside an explicit `overlap` group, and disjointness between
            computed slice or raw pointer region expressions.
      - [ ] Preserve runtime-checked `if disjoint(...)` branches as the
            body-level refinement form for APIs that opt into overlap or for
            data-dependent subregions.
      - [ ] Preserve loop-carried disjoint facts used by `for independent` and
            related constructs, since iteration-space disjointness is not a
            parameter contract.
      - [ ] Document that local raw pointers, slice locals, text locals, and
            borrowed local views are not non-overlapping by declaration default.
            Local facts are inferred from provenance: pointer copies and simple
            casts preserve same-region identity; address-of distinct visible
            roots, non-overlapping projections, fresh allocation, exclusive
            mutable borrows, and proven range splits can establish local
            non-overlap.
      - [ ] Define fix-it diagnostics that remove redundant whole-parameter
            `disjoint` qualifiers and redundant whole-parameter
            `where disjoint(...)` clauses, while leaving expression-level,
            branch-scoped, and loop-scoped `disjoint` forms untouched.
      - [ ] Add tests that confirm redundant whole-parameter `disjoint` is
            rejected with a fix-it, while expression-level, branch-scoped, and
            loop-scoped `disjoint` continue to compile and continue to produce
            the same alias-scope, noalias, and loop-access-group metadata as
            before.
    - [ ] Decide the exact set of parameter families covered by the default:
          `borrow`, `borrow mut`, `retborrow`, `storeborrow`, slices, text views,
          `init`, `out`, bounded raw pointer regions, `rawptr`, and `rawmutptr`.
    - [ ] Decide whether the default applies to every memory-backed parameter or
          only to pointer-like parameters, and document the treatment of owned
          by-value aggregates that lower indirectly through ABI rules.
      - [ ] Proposed rule: apply the source-level default to parameters that
            describe reachable caller storage (`borrow`, `borrow mut`,
            `retborrow`, `storeborrow`, slices, text views, `init`, `out`,
            bounded raw pointer regions, `rawptr`, and `rawmutptr`). Do not add
            user-facing non-overlap obligations for ordinary scalar or owned
            by-value aggregate parameters merely because the ABI lowers a large
            value indirectly; source by-value semantics already describe a
            value transfer/copy. Backend `byval`/sret noalias facts may still be
            emitted when the ABI contract itself makes them sound.
    - [ ] Define how the new default composes with `const`, `frozen`, `shared`,
          `out`, `init`, `unsafe`, `ffi`, `asm`, member receivers, generic
          parameters, function pointers, lambdas, methods, trait requirements,
          doctrine members, and package-image imports.
    - [ ] Define explicit opt-out syntax for allowed overlap using
          `where overlap(a, b)` as the canonical spelling. This removes the
          default non-overlap obligation only for the listed relation and must
          suppress `noalias`/scoped-noalias facts between those regions unless a
          nested `if disjoint(...)` or expression-level fact proves a narrower
          non-overlap path.
    - [ ] Define explicit same-region syntax for APIs that intentionally require
          the same memory using `where same(a, b)` as the canonical spelling.
          This is stronger than `overlap`: a safe call must prove both operands
          identify the same region, not merely that overlap is allowed.
    - [ ] Decide whether explicit legacy `disjoint` remains legal as redundant
          documentation, becomes an error with a fix-it, or is kept only for
          local/runtime `if disjoint(...)` checks.
    - [ ] Define the safe-code rule: a call that passes overlapping arguments to
          default-non-overlap parameters is rejected unless the callee explicitly
          permits that overlap relation.
      - [ ] Proposed enforcement rule: every safe call generates call-site
            memory obligations after overload resolution and generic
            substitution. Default parameter pairs require proof of
            non-overlap; `where same(a, b)` pairs require proof of same-region
            identity; `where overlap(a, b)` pairs generate no non-overlap
            obligation for that relation. If the compiler proves overlap for a
            default pair, emit a diagnostic. If the compiler cannot prove
            non-overlap, emit a diagnostic in safe code rather than trusting
            distinct variable names.
    - [ ] Define the unsafe-code rule: unsafe may assert facts the compiler cannot
          prove, but it must not disable obvious self-overlap diagnostics unless
          an explicit overlap/same-memory contract permits the call shape.
      - [ ] Proposed unsafe rule: `unsafe` does not automatically bypass
            default non-overlap. Add a separate explicit assertion form later,
            such as `unsafe assume disjoint(a, b) { ... }`, for trusted external
            facts the compiler cannot prove. Even that form must not silence
            obvious same-root/self-overlap unless it names a region split the
            compiler can represent.
  - [ ] Update grammar, parsing, and syntax modeling.
    - [ ] Add parser support for relational memory-contract clauses:
          `where overlap(a, b)` and `where same(a, b)`. Prefer these clauses
          over bare parameter prefixes because overlap and same-memory are
          relations between regions; a prefix without an operand set is too
          broad unless later accepted as an explicit "may overlap all
          memory-backed parameters" shorthand.
    - [ ] Add syntax-model representation for default-non-overlap parameters,
          explicit overlap groups, and explicit same-memory groups.
    - [ ] Preserve the new facts through declared function syntax, member
          functions, constructors if applicable, trait/doctrine declarations,
          overload identity, generic templates, package images, and source
          bridge fallbacks.
    - [ ] Add parser diagnostics for malformed overlap/same-memory clauses,
          repeated operands, unknown operands, scalar operands, and invalid raw
          pointer region expressions.
  - [ ] Update type checking and semantic validation.
    - [ ] Invert the current `disjoint` model so memory-backed parameters produce
          default pairwise non-overlap groups unless an explicit overlap or
          same-memory relation removes or narrows that requirement. This allows
          Stark to emit `noalias` and scoped-noalias facts by default where the
          source contract and call-site proof make them sound.
    - [ ] Extend `ValidateParameterDisjointContracts` into a general parameter
          memory-contract validator that handles default non-overlap, explicit
          overlap, explicit same-memory, and explicit disjoint facts together.
    - [ ] Build a call-site proof engine over a canonical memory-region model.
          Each argument region should carry root identity, projection path,
          byte/element range when known, mutability/readonly/init/out facts, and
          provenance flags such as raw, FFI-derived, integer-laundered, fresh,
          borrowed, dynamic-owner, or package-imported. Pairwise comparison
          should produce exactly one of `same`, `disjoint`, or `unknown`; safe
          default-non-overlap calls accept only `disjoint`, `where same` accepts
          only `same`, and `where overlap` accepts all three while suppressing
          default noalias facts for that relation.
    - [ ] Update call-site validation so overlapping arguments are rejected by
          default, including same root, root/field, field/field, slice ranges,
          text ranges, dynamic initialized/spare views, bounded raw pointer
          regions, address-of forms, method receivers, and hidden-root call
          results.
    - [ ] Add compile-time cheat detection for obvious attempts to disguise
          overlap: address-of aliases, local raw-pointer copies, simple casts,
          pointer-to-integer-to-pointer round trips, slice-from-pointer aliases,
          dynamic storage views over the same owner, field/index projections, and
          function-item or helper-call roots that hide known argument provenance.
    - [ ] Preserve conservative behavior for unknown raw/FFI provenance: reject
          safe default-non-overlap calls without proof instead of silently trusting
          distinct variable names.
    - [ ] Update diagnostics so they explain whether the user should pass
          distinct storage, call an overlap-safe API, add `where overlap(...)`
          to the callee, use `where same(...)` when the API requires identical
          storage, guard the call with `if disjoint(...)`, or use an explicit
          unsafe assumption form once that form exists.
    - [ ] Revisit `unsafe` diagnostics so unsafe code can express trusted
          external facts without weakening Stark's ordinary ownership,
          initialization, range, const, or borrow validation.
  - [ ] Update ownership, borrowing, and pointer interactions.
    - [ ] Ensure the borrower model states that non-overlap is a parameter-level
          region contract, while borrow qualifiers still control mutability,
          escape, initialization, const provenance, and lifetime.
    - [ ] Ensure local pointer and local view facts remain provenance-based, not
          declaration-default-based. A local raw pointer copy, slice local copied
          from another slice, or simple pointer cast must retain the same-region
          identity. Distinct locals become non-overlapping only through known
          distinct storage roots, non-overlapping projections/ranges, fresh
          allocation, exclusive borrow facts, or branch-scoped/runtime
          `if disjoint(...)` facts.
    - [ ] Verify `borrow mut` exclusivity, `out`, and `init` destination rules
          compose with default non-overlap without double-counting or emitting
          unsound noalias facts.
    - [ ] Define how readonly borrows and const/frozen views behave: readonly
          does not permit mutation, but default parameter non-overlap may still
          give noalias when the call contract requires separate regions.
    - [ ] Define raw pointer behavior: raw pointers remain nullable/unsafe as
          appropriate, but parameter-region overlap is still prohibited by
          default unless the API opts out.
    - [ ] Audit dynamic storage, slices, text views, initialization views, and
          sparse-slot internals for same-owner region handling under the new
          default.
  - [ ] Update compiler pipeline lowering and backend facts.
    - [ ] Carry default non-overlap, explicit overlap, and explicit same-memory
          facts through type models, semantic summaries, HIR, MIR, SSA, ABI
          lowering, package images, and imported generic template bodies.
    - [ ] Represent call-site memory obligations explicitly before MIR so the
          lowerer receives accepted calls whose default non-overlap, explicit
          overlap, and explicit same-memory requirements have already been
          proven or diagnosed. MIR lowering must not rediscover whether a call is
          valid.
    - [ ] Update semantic memory summaries so `GuaranteedNoAlias` reflects the
          new default only where the callee contract covers the full reachable
          region and LLVM `noalias` is legal.
    - [ ] Update MIR runtime fact lowering so `if disjoint(...)` remains a
          branch-scoped refinement for APIs that explicitly allow overlap or for
          data-dependent subregions.
    - [ ] Update SSA scoped-noalias propagation so default parameter facts and
          runtime branch facts produce separate, sound alias-scope domains.
    - [ ] Update LLVM parameter attributes, scoped `!alias.scope` / `!noalias`,
          loop access groups, memcpy/memmove selection, and inlined-body metadata
          to use the new default without attaching metadata to explicitly
          overlapping or same-memory regions.
    - [ ] Add guardrails that suppress or drop noalias metadata when raw pointer
          escapes, integer pointer laundering, FFI capture, `shared` publication,
          or explicit overlap contracts make the fact unsound.
  - [ ] Update standard library and examples.
    - [ ] Audit every public and internal `System.*` API that currently relies on
          implicit possible overlap and add explicit overlap/same-memory
          contracts where overlap is part of the algorithm.
    - [ ] Remove redundant `disjoint` annotations where the new default is enough,
          or keep them only where the design says redundant documentation is
          allowed.
    - [ ] Update memory, text, path, runtime-buffer, IO, networking, and
          collection fast paths so disjoint fast paths remain fast and
          overlap-safe fallbacks remain correct.
    - [ ] Update benchmarks and examples to use the new spelling for intentional
          overlap and same-memory behavior.
  - [ ] Add compiler and standard-library tests.
    - [ ] Add parser and syntax-model tests for the new overlap/same-memory forms
          and for rejected malformed contracts.
    - [ ] Add type-checking tests proving default rejection of same-root,
          root/field, overlapping fields, overlapping indexes, overlapping
          ranges, hidden roots, call-result roots, raw pointer aliases, and
          slice/text/dynamic views over the same storage.
    - [ ] Add positive tests for provably independent locals, distinct fields,
          non-overlapping ranges, explicit overlap contracts, explicit
          same-memory contracts, and `if disjoint(...)` guarded calls.
    - [ ] Add call-site proof tests for the three-outcome region comparison:
          default non-overlap accepts proven `disjoint` and rejects `same` or
          `unknown`; `where same(...)` accepts only proven same-region
          arguments; `where overlap(...)` accepts same, disjoint, and unknown
          relations while suppressing default noalias facts between the
          overlapping-capable parameters.
    - [ ] Add unsafe-boundary tests that distinguish trusted unknown raw-pointer
          facts from obvious self-overlap that should still be rejected.
    - [ ] Add semantic summary tests for `GuaranteedNoAlias`, readonly/writeonly,
          capture effects, and package-image preservation.
    - [ ] Add MIR, SSA, and LLVM tests for default noalias attributes,
          scoped-noalias metadata, runtime disjoint branch metadata, loop access
          groups, memcpy lowering, and memmove/snapshot fallback when explicit
          overlap is allowed.
    - [ ] Add standard-library audit tests that catch accidental public APIs whose
          overlap behavior is undocumented under the new default.
  - [ ] Update documentation.
    - [ ] Update `docs/Userfacing/LanguageReference.md` with the new default,
          explicit opt-out syntax, same-memory syntax, diagnostics, and examples.
    - [ ] Update `docs/Userfacing/BorrowerSystem.md` so borrowing, const/frozen,
          raw pointers, initialization destinations, and default non-overlap are
          described as separate but composable contracts.
    - [ ] Update `docs/Internals/LanguageInternals.md` with the new compiler fact
          model, call-site proof model, branch-scoped disjoint facts, LLVM
          lowering, and metadata soundness rules.
    - [ ] Update standard-library docs for APIs whose overlap behavior changes or
          becomes explicit.
    - [ ] Update book/style-guide material once the source spelling is final.


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

### Raw Pointer Replacement Checklist

Verified against `stdlib/src` in this pass. Checked items are complete in the
current source shape. Unchecked items still expose raw pointers publicly or keep
replaceable raw storage that should move behind `dynamic`, slices, owned values,
or a narrower explicitly unsafe boundary.

- [x] `System`
  - [x] Remove raw pointer re-exports from public surface unless required.
    - Done: `System.IO.File` no longer exposes public raw-pointer APIs, and
      `System.stark` no longer re-exports `System.Text`. Code that needs the
      current low-level text interop exception must import `System.Text`
      explicitly.
- [x] `System.BitOperations`
  - [x] Replace raw pointer helpers with value or slice APIs where present.
    - Verified raw-pointer free.
- [x] `System.Collections`
  - [x] Replace internal raw storage with `dynamic` or safe storage wrappers
        wherever possible.
    - [x] Replaced `Queue<T>` raw allocation storage with `dynamic T` storage
          in both stable and experimental collections.
    - [x] Verified `Stack<T>`, `RingQueue<T>`, and linked-list storage use
          `dynamic` storage instead of raw allocation storage.
    - [x] Retained `Dictionary<K, V>` raw sparse key/value/state storage as the
          remaining collection raw pointer boundary because current `dynamic`
          storage cannot model sparse uninitialized slots while returning
          mutable borrows from occupied values without moving generic payloads.
          Replace it once the language has first-class sparse initialized-slot
          storage or borrowed enum-payload projection.
- [x] `System.Console`
  - [x] Keep raw handles internal to platform calls.
    - Verified: stdin handle state is module-private and platform calls are the
      only raw handle consumers.
  - [x] Use slices or dynamic buffers for user-facing write paths.
    - Verified: public byte write/read APIs use slices, `DynamicByteBuffer`, or
      fixed runtime buffers.
- [x] `System.FileSystem`
  - [x] Hide directory and file system handles behind owned types.
    - Verified: `Directory.Handle` is internal and the public surface returns
      owned `Directory` values.
  - [x] Replace raw entry buffers with dynamic or fixed safe buffers.
    - Done: `Directory` now owns a `System.Runtime.Buffer.FixedByteBuffer8192`
      for platform entry reads and guards the platform-reported capacity before
      passing an internal raw pointer to the OS boundary.
- [x] `System.IO`
  - [x] Keep public IO contracts free of raw pointers.
    - Verified: the base `System.IO` result/status/error module is raw-pointer
      free; file handle and byte-region raw helpers are now internalized under
      `System.IO.File`.
- [x] `System.IO.File`
  - [x] Replace file buffers with slices, dynamic storage, or owned buffers.
    - Done: public owned `File` read/write paths accept byte slices,
      `DynamicByteBuffer`, fixed runtime buffers, and text views; raw byte and
      region helpers are internal stdlib/platform handoff code only.
  - [x] Keep OS handles internal.
    - Done: stable `File.Handle` and compatibility-style raw helpers such as
      `OpenRead`, `Close`, `ReadBytes`, `WriteBytes`, `Seek`, `WriteText`, and
      `WriteLine` are internal unsafe helpers, not public APIs.
- [x] `System.IO.Path`
  - [x] Replace raw path buffers with dynamic text or fixed safe buffers.
    - Verified: public path APIs use `OwnedAscii`, text views, and value
      results; remaining raw pointers are internal read-only text scans.
- [x] `System.Math`
  - [x] Ensure math APIs remain raw-pointer free.
    - Verified raw-pointer free.
- [x] `System.Memory`
  - [x] Keep raw allocation pointers internal to allocator implementation.
    - Verified: `Allocation` is internal.
  - [x] Expose `dynamic` memory primitives instead of raw allocation plumbing.
    - Verified: reserve, append, copy, move, and fill APIs operate on
      `dynamic`, slices, and initialized destinations.
  - [x] Fence or replace public raw-pointer initialization helpers.
    - Done: `InitializeBytesFromPointerDisjoint` and
      `InitializeCodePointsFromPointerDisjoint` are internal unsafe bridges for
      standard-library text/path internals, not public APIs.
- [x] `System.Net`
  - [x] Hide socket handles behind owned socket types.
    - Verified: the base networking module is raw-pointer free.
- [x] `System.Net.Tcp`
  - [x] Replace raw socket buffers with slices or vectored safe wrappers.
    - Verified: public reads/writes use byte slices, vectored slice APIs, or
      runtime buffers; socket handles are internal to `TcpClient` and
      `TcpListener`.
- [x] `System.Process`
  - [x] Keep process APIs raw-pointer free.
    - Verified raw-pointer free.
- [x] `System.Runtime`
  - [x] Allow raw pointers only for compiler/runtime ABI hooks.
    - Verified: raw pointers are confined to internal slice-part ABI structs and
      compiler-known slice extraction hooks.
- [x] `System.Runtime.Buffer`
  - [x] Prefer dynamic and fixed buffers over raw pointer storage.
    - Verified: storage is `dynamic` or fixed arrays.
  - [x] Remove or internalize stable fixed-buffer raw pointer accessors.
    - Done: stable `FixedByteBuffer*` public access now uses `ReadSlice`,
      `ReadMutableSlice`, and `WriteSlice`, matching the slice-only
      experimental buffer shape.
- [x] `System.Runtime.ConsoleInput`
  - [x] Keep OS handle access internal and unsafe.
    - Verified: raw pointer helpers are module-private.
- [x] `System.Runtime.Platform`
  - [x] Keep raw pointers internal and explicitly unsafe.
    - Verified: platform dispatch functions are internal and raw consumers are
      unsafe.
- [x] `System.Runtime.Platform.Linux`
  - [x] Audit syscall buffers and handles.
  - [x] Wrap raw regions in narrow unsafe helpers.
    - Verified: Linux raw pointer use is internal unsafe platform and syscall
      handoff code.
- [x] `System.Runtime.Platform.Windows`
  - [x] Audit Kernel32, NtDll, Winsock, and console buffers.
  - [x] Wrap raw regions in narrow unsafe helpers.
    - Verified: Windows raw pointer use is internal unsafe platform and FFI
      handoff code.
- [x] `System.Syscall`
  - [x] Restrict or internalize user-facing raw syscall APIs.
    - Done: `Syscall0` through `Syscall6` are internal unsafe ABI helpers; Linux
      platform code uses `System.Runtime.Platform.Linux` internal syscall
      shims, and packaged user code should go through safe modules such as
      `System.Process`.
- [x] `System.Text`
  - [x] Replace raw text storage with dynamic/owned text and slices.
    - Done: `OwnedAscii`, `OwnedUnicode`, and `OwnedUtf16` provide owned
      dynamic text/code-unit surfaces; public UTF conversion helpers now use
      owned destinations and `MemoryStatus`.
    - Done: `AsciiData`, `UnicodeData`, and raw UTF conversion helpers are
      internal standard-library/platform/compiler boundaries.
    - Retained: explicitly unsafe public `TryConcat*` and `TryFormat*`
      fixed-buffer hooks remain as the compiler-known no-allocation surface for
      stack `Ascii`/`Unicode` concatenation and interpolation.
- [x] `System.Threading`
  - [x] Hide thread handles behind owned thread types.
    - Verified: `Thread.Handle` is internal and public thread operations use the
      owned `Thread` type.

## 4. Enforce Integer Range Issues As Compile-Time Errors, using signed integers with positive-only range as compile time error, suggest use of unsigned integer instead.

- [x] Make invalid or unnecessarily wide integer range declarations compile-time
      errors by default.
  - [x] Add enforcement through `CompilerOptions.EnforceIntegerRangeStorageRules`
        and keep `--strict-integer-ranges` as a compatibility spelling for the
        default CLI behavior.
  - [x] Define the strict-mode rule for oversized storage ranges. Example:
        `i64[0 128]` should be rejected when a narrower integer type can express
        the declared range and no ABI, pointer-size, or platform reason is
        documented. use new `platform` keyword if required by abi contract to allow you to use a type you don't need to.
  - [x] Add an escape hatch or annotation only for ABI/platform cases that truly
        require a specific width. `[Platform]` declarations preserve ABI-required
        storage for signatures and aggregate fields without relaxing ordinary
        local, array, generic, or cast range checks.
  - [x] Reject impossible ranges, inverted ranges, endpoints outside the base
        integer type, and endpoints that force unnecessary storage width in strict mode.
  - [x] Emit diagnostics that suggest the smallest valid integer type in the error message.
  - [x] Update constant folding and range inference so exponent endpoints such
        as `2 ** 63 - 1` are validated before lowering.
  - [x] Add strict-mode tests for locals, fields, parameters, return types, arrays,
        generic instantiations, casts, signed-to-unsigned suggestions, narrower
        unsigned storage suggestions, signed narrowing suggestions, scalar const
        width/sign errors, and FFI ABI signature exemptions.
  - [x] Flip strict range enforcement on by default after the standard library
        integer range audit is complete.

### Standard Library Integer Range Audit

Completed against `stdlib/src` with the default strict integer range checks. Ordinary
non-negative signed ranges now use unsigned storage with the original upper
bounds preserved, and over-wide helper ranges now use the smallest signed or
unsigned storage that expresses them. Full-width signed ABI and syscall ranges
remain signed. Benchmark `.stark` sources are now covered by the same default
strict range checks in `BenchmarkSourceTests`.

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

## 5. Normalize Standard Library Range Notation

- [x] Make standard library integer ranges use exponentiation or `[min max]`.
  - [x] Replace large literal endpoints with `[min max]` when the full primitive
        range is intended.
  - [x] Use exponentiation for explicit numeric bounds where the exact value is
        meaningful, such as `2 ** 31 - 1`.
  - [x] Prefer the narrowest integer type that expresses the range.
  - [x] Add format/lint tests that prevent regression to giant literal bounds.
  - [x] Update docs and examples to model the new style.
  - [x] Enforce full-width endpoint shorthand style in the compiler: the maximum
        endpoint of a ranged integer type must be written as `max`, the minimum
        endpoint of a signed integer type must be written as `min`, and unsigned
        ranges may still use `0` as the lower bound.
  - [x] Run the compiler, pipeline, integration, feature, benchmark,
        docs/examples searches, and focused standard-library range/platform/text
        surfaces after the diagnostic lands, then replace any newly reported
        manual full-width endpoint spellings with `min`/`max`.

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
  - [ ] Add macOS benchmark runs to compare Stark, C, and Rust.
    - [x] Run a batch-1 Stark-only benchmark sweep on macOS.
    - [ ] Add cross-language C/Rust comparison once `rustc` is available in the
          benchmark environment.
  - [x] Document macOS platform behavior and unsupported APIs.
    - [x] Document the current libSystem/POSIX backend and the Apple SDK/Command
          Line Tools requirement for final native linking.

## 8. Update Website Book

- [ ] Update the book portion of the website.
  - [ ] Convert the book plan into website pages with stable URLs.
  - [ ] Make every chapter a tutorial that builds on previous chapters.
  - [ ] Add content for any planned chapters that do not currently exist
  - [ ] renumber chapters after addition of new ones
  - [ ] Include multiple code examples per chapter.
  - [x] Add compile checks for code examples where possible.
  - [ ] Add navigation, previous/next links, and version/release labels.
  - [ ] Keep the language reference separate from tutorial material.

### Chapter Checklist

- [ ] Chapter 1: Introduction: Why Stark Exists
- [ ] Chapter 2: Installing Stark and Building Programs
- [ ] Chapter 3: Hello, Stark
- [ ] Chapter 4: A Small Stark Tour
- [ ] Chapter 5: Values, Types, and Ranges
- [ ] Chapter 6: Bindings, Mutation, and Control Flow
- [ ] Chapter 7: Ownership, Moves, and Drops
- [ ] Chapter 8: Borrowing in Stark
- [ ] Chapter 9: Stark Borrowing Compared With Rust
- [ ] Chapter 10: Storage Classes and Lifetimes
- [ ] Chapter 11: Aggregates and Layout-Aware Design
- [ ] Chapter 12: Enums and Pattern Matching
- [ ] Chapter 13: Arrays, Slices, Text, and Views
- [ ] Chapter 14: Modules, Visibility, and Packages
- [ ] Chapter 15: Function Guarantees and Effects
- [ ] Chapter 16: Errors Without Exceptions
- [ ] Chapter 17: Generics, Traits, Doctrines, and Specialization
- [ ] Chapter 18: Callable Values and Thread Entries
- [ ] Chapter 19: FFI, Raw Pointers, and Native Packages
- [ ] Chapter 20: Console, Process, and Platform Basics
- [ ] Chapter 21: Memory and Collections
- [ ] Chapter 22: Files, Directories, Paths, and Text
- [ ] Chapter 23: Threading and TCP
- [x] Chapter 24: Testing Stark Code
  - Done: the website book chapter now documents `kind = "test"`,
    `System.Testing`, explicit fact runners, solution default test sets, and
    `stark test`.
- [ ] Chapter 25: Stark's Performance Model
- [ ] Chapter 26: Memory Layout, ABI, and Interop Expectations
- [ ] Chapter 27: Integer, Floating-Point, and Overflow Policy
- [ ] Chapter 28: Performance Tuning, Independent loops, inline, disjoint params, const params, 
- [ ] Chapter 29: Unsafe stark and rawpointers
- [ ] Chapter 30: Reading Stark Diagnostics
- [ ] Chapter 31: Looking at Generated IR
- [ ] Chapter 32: Project: Command-Line Text Tool
- [ ] Chapter 33: Project: Multi-Module Package
- [ ] Chapter 34: Project: File Processing Utility
- [ ] Chapter 35: Project: Native-Backed Package
- [ ] Chapter 36: Project: Performance Case Study
- [ ] Appendices
  - [ ] Keywords and reserved words
  - [ ] Operators and symbols
  - [ ] Integer widths and range rules
  - [ ] Function kinds and guarantees
  - [ ] Storage classes and ownership quick reference
  - [ ] Package manifest reference
  - [ ] Current boundaries
  - [ ] Stark for Rust programmers
  - [ ] Stark for C# programmers
  - [ ] Stark for C programmers

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
- [ ] benchmarks/micro/AlgebraicIdentitySimplification — rust 1.014934, stark 1.022554
- [ ] benchmarks/micro/ExplicitArithmeticRangePruning — rust 0.990526, stark 1.014211
- [ ] benchmarks/micro/FunctionPointerDevirtualization — rust 1.007611, stark 1.01945
- [ ] benchmarks/network/TcpScatterGatherLoopback — rust 0.970315, stark 1.196042
- [ ] benchmarks/text/IntegerFormatting — rust 1.106406, stark 627.29316
- [ ] benchmarks/text/PathJoin — rust 1.075163, stark 1.094771
- [ ] benchmarks/text/PathRepeatedSmallOps — rust 1.029443, stark 1.07571
- [ ] benchmarks/text/TextParsing — rust 1.092818, stark 1.319337
- [ ] benchmarks/text/UnicodeFormatting — rust 1.047867, stark 594.463059



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
