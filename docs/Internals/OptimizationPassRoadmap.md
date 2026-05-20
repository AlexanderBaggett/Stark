# Optimization Pass Roadmap

This document tracks planned Stark optimization passes. Each section should
describe the pass goal, conservative implementation scope, correctness tests,
and performance/codegen checks needed before the pass can be considered done.

## Grounding Against LLVM And Existing Passes

This roadmap is not a request to reimplement LLVM's generic optimizer. Stark's
existing optimization survey already identifies LLVM `InstCombine`, `SROA`,
`mem2reg`, `GVN`, `LICM`, `SCCP`, `SimplifyCFG`, inlining, loop vectorization,
and SLP vectorization as the backend workhorses for generic canonicalization,
scalarization, redundancy elimination, and loop cleanup
([OptimizationPasses.md](./OptimizationPasses.md)).

The intended split is:

- Stark owns source-level proof and representation changes that LLVM cannot
  rediscover from lowered IR.
- Stark emits the strongest sound LLVM attributes, metadata, intrinsics, and IR
  shape so LLVM's existing passes can finish the low-level work.
- Stark only adds pre-LLVM CSE, hoisting, scalarization, or memory rewriting when
  the optimization depends on Stark-only facts that would otherwise be lost.
- LLVM owns target-level peepholes, instruction selection, generic GVN/LICM,
  generic SROA/mem2reg/DSE, vectorization cost modeling, and backend scheduling.

Grounding references:

- LLVM LangRef documents the relevant facts Stark can emit: `noalias`,
  `captures(...)`, `nonnull`, `dereferenceable`, `readonly`, `writeonly`,
  `initializes`, `dead_on_return`, `range`, fast-math flags, and metadata such
  as `!range`, `!invariant.load`, `!alias.scope`, and `!noalias`:
  <https://llvm.org/docs/LangRef.html>
- LLVM's pass reference documents generic simplification and optimization passes
  such as `instcombine`, `aggressive-instcombine`, `internalize`, `ipsccp`,
  `licm`, and related cleanup passes: <https://llvm.org/docs/Passes.html>
- LLVM AliasAnalysis documents memory-object and mod/ref reasoning, including
  globally constant and locally invariant memory: <https://llvm.org/docs/AliasAnalysis.html>
- LLVM vectorizer docs and transform metadata docs cover loop/vectorization
  behavior and loop metadata such as parallel-access facts:
  <https://llvm.org/docs/Vectorizers.html> and
  <https://llvm.org/docs/TransformMetadata.html>

### Ownership Decisions

| Area | LLVM already owns | Stark-owned work remains |
|---|---|---|
| Deep const graph optimization | `readonly`, `captures(...)`, alias analysis locally-invariant memory, `!invariant.load`, GVN/LICM once facts are visible | Permanent transitive const provenance, safe demotion at raw/FFI boundaries, const-specialized stdlib paths, package/import preservation |
| Region non-overlap and alias optimization | `noalias`, scoped alias metadata consumption, AliasAnalysis, `memcpyopt`, LICM, vectorizers | Default Stark non-overlap proof, `same`/`overlap`/subregion/branch-scoped facts, metadata placement, `memcpy` vs `memmove` semantic selection |
| Borrow escape and capture optimization | `captures(...)`, Attributor/function-attrs-style inference, ordinary escape-related cleanup | Typed borrow escape classes, closure allocation avoidance, safe function-pointer/closure boundary preservation |
| Ownership, move, drop, and initialization optimization | SROA, mem2reg, DSE, copy propagation once memory is ordinary LLVM IR | Source-correct direct construction, drop elision/order preservation, partial-move and branch-merge state, address/raw escape demotion |
| Dynamic storage initialized-prefix optimization | Memory intrinsics, bounds/range exploitation, vectorization, DSE after good IR shape | Dynamic owner/prefix/spare-region semantics, reserve/append/move/drop rewrites, exact `init` and sparse-proof boundaries |
| Law and finite effect optimization | Function attrs, Attributor, call CSE/LICM when memory effects are expressible | Callable type contracts, indirect-call effect preservation, package/import summaries, pre-LLVM rewrites only for Stark-only memory facts |
| Closed-world generic/trait/doctrine optimization | Inliner, internalize, IPSCCP, devirtualization when direct targets are visible | Monomorphization, doctrine/trait static dispatch, typed package-body import, bound-call reachability, ABI/visibility-safe internalization decisions |

## Semantic Fact Optimization Passes

These passes use facts Stark proves before LLVM sees the program. LLVM can often
consume fragments of these facts through attributes or metadata, but Stark owns
the source-level proof and should perform optimizations that depend on the full
semantic contract.

### Deep Const Graph Optimization

`const` parameters and `const` globals describe permanently immutable reachable
object graphs, not only readonly pointer arguments. This pass should preserve and
consume that provenance before lowering it into LLVM's narrower `readonly`,
`captures(...)`, and invariant-load fragments.

LLVM boundary: do not build a generic load-hoisting or GVN pass here. LLVM can
already hoist and CSE when alias/memory facts are visible. Stark's job is to keep
deep const provenance precise, specialize operations that depend on transitive
immutability, and emit sound invariant/readonly/capture facts.

- [x] Build a first-class const-provenance fact model.
  - [x] Distinguish permanent `const` provenance from ordinary immutable
        bindings, `static` bindings, `frozen` borrows, readonly raw pointers, and
        temporary readonly views.
  - [x] Propagate const provenance through field projection, indexing, slicing,
        text views, method receivers, aggregate construction, and package-image
        typed bodies.
  - [x] Preserve the fact across generic substitution and imported typed
        templates.
  - [x] Preserve the fact across inline clones and const-specialized helper
        calls.
  - [x] Demote or reject the fact at raw/unsafe/FFI boundaries that could expose
        mutation or replacement not covered by Stark's safe const contract.

- [ ] Optimize reads and calls over const graphs.
  - [x] Expose const graph loads in a shape LLVM GVN/LICM can see when the
        loaded object cannot be replaced for the lifetime represented in the IR.
  - [x] Add Stark-side CSE only for repeated pure/law calls whose equivalence
        depends on transitive const-graph facts LLVM cannot otherwise see.
  - [ ] Specialize hot stdlib helpers for const text, path, lookup-table, and
        dictionary-key inputs where length, payload, hash, or path facts can be
        precomputed or cached.
    - [x] Reuse the existing text literal length, text payload, ASCII-to-Unicode,
          and constant integer text-format specializers instead of duplicating
          LLVM-owned constant folding.
    - [x] Add a dedicated SSA const-stdlib-helper specialization pass for
          literal `System.IO.Path` facts and projections. The pass precomputes
          `GetFacts`/`GetConstFacts` field values and folds literal
          `Extension`/`BaseName`/`DirectoryName` calls using target-aware path
          separator semantics.
    - [x] Retarget literal or permanently const-provenance path writes from
          snapshot-capable helpers to const variants (`TryJoinConst`,
          `JoinConst`, `TryNormalizeSeparatorsConst`,
          `NormalizeSeparatorsConst`) when the const variant is present and the
          source arguments have permanent const memory provenance.
    - [x] Retarget literal or permanently const-provenance `System.Text`
          append/factory helpers to const and disjoint variants when the target
          helper is present
          (`AppendConstAsciiDisjoint`, `AppendConstUnicodeDisjoint`,
          `FromConstAscii`, `FromConstUnicode`, and
          `FromConstAsciiToUnicode`) so permanent const inputs skip
          snapshot-capable append paths.
    - [x] Specialize lookup-table reads whose indexes are compile-time constants
          but whose table payload is represented as a const graph rather than an
          LLVM-visible constant array.
      - [x] Preserve typed constant-initializer trees for package-image globals,
            including fixed-array element payloads, so source-removed package
            imports keep producer-side table facts.
      - [x] Add a dedicated `const-lookup-tables-ssa` pass that folds constant
            index reads from `const` fixed-array globals to SSA constants before
            LLVM emission.
      - [x] Add package producer/consumer coverage that removes source body text
            after package creation and still folds an imported const lookup from
            typed package facts alone.
      - [x] Add negative coverage proving `static` fixed arrays do not receive
            permanent const-graph lookup folding.
      - [x] Define a public stdlib lookup-table helper surface before adding
            helper-call-specific lookup-table specialization.
            `System.Collections.Lookup<T>(const T[] table, u64 index)` is now
            the public source contract. MIR can form const slice views from
            const fixed-array globals without moving global storage, and the
            `const-lookup-tables-ssa` pass folds scalar helper reads through
            slice element addresses back to typed fixed-array initializer facts.
            Negative coverage keeps runtime-index helper calls as loads.
    - [x] Keep dictionary-key bool/integer hash/equality specialization in the
          existing LLVM call-site specializer. These are the only compiler-proven
          dictionary-key contracts today, so adding an SSA pass would duplicate
          lower-level work without adding Stark-only facts.
    - [ ] Add Stark-side dictionary-key precompute/cache support when
          user-defined dictionary-key contracts become representable and their
          hash/equality facts are not already exposed to LLVM.
          Blocked on user-defined `DictionaryKey<T>` contract solving and a
          stable compiler-visible hash/equality summary for non-bool,
          non-integer keys; current bool/integer keys are already handled at the
          LLVM call-site specialization layer. Confirmed against the current
          frontend/backend model: type checking still rejects non-bool/non-integer
          `Dictionary<K, V>` keys before optimization, and SSA/LLVM builtin
          validation only admits bool/integer `DictionaryKey.Hash`/`Equals`.
  - [x] Remove defensive snapshots when a source is const and any mutable
        destination is separately proven disjoint.
    - [x] Route literal path joins and normalizations to const path helpers so
          their source/destination snapshot checks are not emitted for permanent
          literal sources.
    - [x] Route literal text appends and text factory helpers to const/disjoint
          helpers when the mutable destination and permanent literal source are
          known not to alias by construction.
    - [x] Generalize snapshot removal for const text and path append/copy
          helpers by retargeting nonliteral permanent const-provenance sources
          such as `const ascii`/`const unicode` parameters and const-provenance
          locals to the existing const/disjoint helper variants. Runtime
          non-const sources remain on the snapshot-capable helpers.

- [x] Emit the strongest sound LLVM facts.
  - [x] Attach `readonly`, `captures(none)` or readonly capture facts,
        `nonnull`, `dereferenceable`, and alignment attributes where ABI lowering
        makes them valid.
  - [x] Emit `!invariant.load` only for loads from permanent immutable storage
        or const provenance whose storage cannot be replaced within the
        represented IR lifetime.
  - [x] Avoid emitting `noalias` for const views unless the ordinary Stark
        memory-contract proof also proves non-overlap.

- [x] Add negative tests for incorrect const-graph optimization.
  - [x] Verify ordinary immutable locals, `static` globals, `frozen` borrows, and
        readonly raw pointers do not receive permanent const treatment. Static
        globals, frozen raw pointers, ordinary immutable pointer locals, and
        readonly raw-pointer locals have focused LLVM metadata regressions.
  - [x] Verify two const views may alias and therefore do not imply local
        non-overlap.
  - [x] Verify raw pointer casts, integer-laundered pointers, unsafe mutation
        boundaries, and FFI calls stop invariant-load/CSE assumptions.
  - [x] Verify replaceable or mutable global state reachable through `static` is
        not treated like a deeply frozen `const` graph.

### Region Non-Overlap And Alias Optimization

Stark memory-backed parameters are non-overlapping by default unless the
signature says `overlap` or `same`. The compiler also has subregion,
branch-scoped, and unsafe-assumed disjoint facts that are richer than plain LLVM
parameter `noalias`.

LLVM boundary: do not build a general-purpose alias analysis competitor. LLVM's
AliasAnalysis, LICM, memory intrinsic optimizers, and vectorizers should consume
the facts. Stark owns proving memory-region relations from source contracts and
placing attributes/metadata only where those proofs dominate the access.

- [x] Complete and centralize the normalized memory-region fact model.
  - [x] Represent parameter roots, field roots, slices, text views, dynamic
        regions, bounded raw pointer regions, `out` and `init` destinations, and
        raw pointer subregions using comparable byte or element intervals.
        The central effective-contract builder now lives in
        `ParameterMemoryContractFacts`; stale duplicate type-checker logic was
        removed so signatures, ABI lowering, packages, and imported templates
        share the same default-non-overlap suppression behavior.
  - [x] Preserve `disjoint`, `overlap`, and `same` contracts through overload
        resolution, generic substitution, function-pointer promotion,
        package-image typed bodies, imported templates, and inline clones.
        Added package-backed generic codegen coverage that removes source bodies
        and still emits scoped alias metadata from imported typed template facts.
  - [x] Track branch-scoped `if disjoint(...)` and scoped
        `unsafe assume disjoint(...)` facts with dominance-bounded lifetimes.
  - [x] Canonicalize same-region aliases so metadata and optimizer decisions use
        the same root identity.

- [x] Optimize memory operations using region facts.
  - [x] Remove runtime overlap checks when the region model already proves
        non-overlap.
  - [x] Select `memcpy` for proven-disjoint bulk copies and `memmove` for
        overlap-preserving copies.
  - [x] Attach scoped `!alias.scope` and `!noalias` metadata to loads, stores,
        memory intrinsics, and memory-touching calls through disjoint roots.
  - [x] Mark retained loops with access-group and parallel-access metadata when
        non-overlap proves no loop-carried memory dependency.
  - [x] Preserve alias facts through inlining instead of relying only on callee
        parameter attributes.

- [x] Add package and ABI compatibility coverage.
  - [x] Verify same-module, imported-package, generic-template, and function
        pointer calls expose equivalent memory-contract facts.
  - [x] Verify FFI and assembly declarations receive no default Stark
        non-overlap unless they explicitly opt in with a memory contract.
  - [x] Add IR gates for alias metadata on hot stdlib copy, append, text, path,
        IO, and encoding helpers.

- [x] Add negative tests for incorrect alias optimization.
  - [x] Verify `where overlap(...)` suppresses default non-overlap and prevents
        `memcpy`/`noalias` rewrites that require disjointness.
  - [x] Verify `where same(...)`, same-root arguments, overlapping slices,
        unknown indexes, call-result roots, and integer-laundered pointers do not
        receive disjoint metadata.
  - [x] Verify `if disjoint(...)` metadata is attached only in the dominated true
        branch, not before the check, after scope exit, or on the false branch.
  - [x] Verify zero-length raw regions preserve nullable-pointer behavior and do
        not accidentally become nonnull/dereferenceable.

### Borrow Escape And Capture Optimization

Stark borrow escape class is part of the type. `borrow`, `retborrow`, and
`storeborrow` describe whether a view may escape, which is stronger than what
LLVM can infer from a lowered pointer alone.

LLVM boundary: do not implement broad heap escape analysis here. LLVM can infer
some capture attributes, but Stark has typed borrow escape classes before
lowering. This pass should preserve those facts, avoid creating unnecessary
runtime storage, and emit capture attributes for LLVM.

- [x] Preserve borrow escape facts through SSA.
  - [x] Represent non-escaping `borrow`, return-only `retborrow`, and
        storable/escaping `storeborrow` views in MIR, SSA, package images, and
        imported templates.
  - [x] Track whether calls, closures, function pointers, or aggregate fields can
        capture each borrow class.
  - [x] Preserve nonnull, alignment, dereferenceability, readonly/mutable, and
        region-root facts while a borrow remains within its legal escape class.

- [x] Optimize using typed non-escape facts before lowering.
  - [x] Emit `captures(none)` or the narrowest capture attribute for safe
        non-escaping borrows.
        Stark emits LLVM `nocapture` for non-escaping borrows,
        `captures(ret: address, read_provenance)` for `retborrow`, and
        `captures(address, read_provenance)`/`captures(address, provenance)` for
        escaping `storeborrow` or weaker callable boundaries.
  - [x] Keep non-escaping borrowed views stack/local and avoid heap closure
        storage or retain-style root materialization.
  - [x] Remove defensive lifetime extension, root retention, and temporary view
        copies when borrow validation proves no escape.
  - [x] Inline non-capturing lambdas and function-item adapters without creating
        runtime closure storage.

- [x] Add cross-boundary coverage.
  - [x] Verify borrow escape facts survive direct calls, member calls, function
        pointer calls whose type carries the needed contract, generic
        monomorphization, imported typed templates, and inline clones.
  - [x] Verify exported or FFI boundaries emit only the facts the external ABI
        can soundly observe.

- [x] Add negative tests for incorrect borrow escape optimization.
  - [x] Verify `retborrow` and `storeborrow` are not optimized as non-escaping
        temporary `borrow` values.
        Added direct LLVM coverage that `storeborrow` emits an escaping capture
        attribute rather than `nocapture` or return-only capture.
  - [x] Verify forwarding a borrow to an unknown, opaque, FFI, or weaker
        function-pointer callee blocks non-escape assumptions.
  - [x] Verify heap closures cannot retain local non-owning borrows unless the
        source type explicitly allows the escape.
  - [x] Verify raw pointer conversion, unsafe storage, or integer pointer
        laundering demotes borrow-derived capture and lifetime facts.

### Ownership, Move, Drop, And Initialization Optimization

Stark validates ownership, moves, initialization, partial moves, drops, and
reinitialization before MIR. LLVM sees stores and calls; Stark knows which
object states are impossible.

LLVM boundary: do not duplicate generic SROA, mem2reg, or DSE. Stark should
perform source-semantic destination construction, drop-order preservation, and
move/drop elision before lowering object state into ordinary memory operations.

- [x] Build optimizer-visible ownership state summaries.
  - [x] Publish structured ownership events and root summaries from ownership
        validation instead of exposing only lossy move/drop string lists. The
        summary now carries typed move, field-move, implicit-drop,
        assignment-drop, reinitialization, and raw-address-taking events plus
        per-root flags for address-taken/raw-pointer exposure, partial moves,
        drop obligations, assignment drops, reinitialization, and final
        availability.
  - [x] Preserve initialized, moved, partially moved, borrowed, reinitialized,
        and drop-required facts through MIR, SSA, package images, generic
        substitution, and inline clones.
    - [x] Attach refined ownership summaries to MIR functions after
          borrow-liveness validation and carry the same summaries into SSA
          functions.
    - [x] Serialize ownership summaries into package images and reload them from
          compiler-facts sections after source bodies are removed. Package
          coverage now verifies typed ownership events and roots survive the
          producer/consumer path.
    - [x] Substitute ownership place/root types when attaching imported template
          summaries to concrete MIR specializations, and preserve function-level
          ownership summaries across SSA inline rewrites.
  - [x] Track field-wise move state for aggregates and exact drop obligations at
        branch merges.
        The validator already maintained these facts internally; the structured
        ownership summary now exposes field-move and drop-target events to later
        optimization passes.
  - [x] Mark address-taken or raw-escaped storage roots so move/drop elision does
        not assume invisible object state.
        Raw address-of operations conservatively mark the affected root as
        address-taken and raw-pointer-exposed in the ownership summary.

- [x] Eliminate unnecessary object traffic.
  - [x] Construct returned or assigned aggregates directly in the final
        destination when ownership and ABI facts allow it.
        Added a dedicated SSA aggregate-construction store pass for full
        non-escaped local aggregate constructions. It rewrites whole aggregate
        stores from complete `insertfield` construction chains into direct field
        stores on the destination root.
  - [x] Remove copies introduced only to preserve source-level move shape when
        the moved-from value is provably dead.
        Added a dedicated SSA ownership-traffic pass that uses ownership roots
        plus SSA liveness to remove dead aggregate move copies and moved-from
        `undef` invalidation stores for non-escaped local roots.
  - [x] Elide drops of uninitialized, moved-out, trivially destructible, or
        statically empty initialized regions.
        MIR runtime-drop lowering already tracks active drop state and type
        drop requirements, so uninitialized and moved-out roots are skipped and
        trivially destructible/empty structures do not materialize drop work.
  - [x] Preserve required assignment semantics by dropping the old initialized
        value before overwriting it.
        Existing MIR runtime-drop coverage verifies the old destructor-backed
        value is dropped between the first assignment and the overwrite.
  - [x] Replace large aggregate copy temporaries with destination-passing when
        Stark ownership and ABI facts prove the final destination; leave generic
        scalarized construction and dead-store cleanup to Stark SSA SROA and
        LLVM SROA/mem2reg/DSE.
        Existing LLVM ABI lowering forwards large aggregate initializer returns,
        direct-call forwards, local forwards, and indirect-parameter forwards
        into sret/byval storage without materializing extra return-buffer
        copies, with focused LLVM emission coverage.

- [x] Add ABI and package coverage.
  - [x] Verify sret, byval, inline clone, imported typed-template, and generic
        monomorphization paths preserve ownership and drop facts.
        Verified the focused ABI/package regressions covering sret/byval
        forwarding, inline-clone reachability, typed package imports with
        source removed/corrupted, and substituted generic ownership summaries
        for imported typed-template bodies.
  - [x] Add IR gates for large `IOResult<T>`, dynamic owner, enum payload, and
        aggregate return paths that should construct directly.
        Added a large generic `IOResult<Big>.Ok(...)` sret regression that
        rejects aggregate `insertvalue`/whole-object stores, and fixed LLVM
        aggregate lowering to canonicalize monomorphized named type keys and
        forward nested inserted aggregates into the final destination. Existing
        gates cover dynamic owner, enum payload, and non-generic aggregate
        sret/byval construction paths.

- [x] Add negative tests for incorrect ownership/drop optimization.
  - [x] Verify drops with side effects, nontrivial destructors, and required drop
        ordering are not removed or reordered.
        Covered by runtime-drop lowering tests for destructor ordering, dynamic
        storage element drops, enum payload drops, and mutable-global reloads
        across destructor-backed calls.
  - [x] Verify assignment to an initialized owned place still drops the previous
        value before storing the new one.
        Covered by ownership validation and MIR runtime-drop regression tests
        that assert the old drop is emitted between the first assignment and
        the overwrite.
  - [x] Verify partial moves, branch-sensitive initialization joins, and early
        returns still run exactly the required drops.
        Partial moves and branch-sensitive joins are covered by ownership
        roadmap diagnostics; runtime-drop lowering covers ordinary early return
        cleanup and large enum-payload early-return cleanup so the optimizer
        cannot erase required destructor paths.
  - [x] Keep panic/assert drop-path coverage out of this pass until Stark has
        the source-level `panic`/`assert` surface.
        Current failure traps are no-unwind/cold and do not expose a source
        ownership cleanup path to optimize. The dedicated language/runtime
        panic/assert roadmap owns that surface; once it exists, add its
        drop-path regressions beside the runtime-drop tests instead of teaching
        this pass about a placeholder construct.
  - [x] Verify address-taken, raw-escaped, or FFI-observable storage is not
        scalarized or elided in a way that changes visible memory behavior.
        Ownership-traffic SSA coverage now keeps moved-from invalidation for a
        raw pointer observed by an FFI call, and aggregate-construction SSA
        coverage keeps whole-object stores for raw-escaped locals.

### Dynamic Storage Initialized-Prefix Optimization

`dynamic T` has compiler-owned capacity, initialized-prefix, spare-region,
allocator, and drop facts. These facts should be used before dynamic storage is
lowered to ordinary pointers and lengths.

LLVM boundary: do not write a custom vectorizer or generic memory optimizer for
dynamic storage. Stark should expose exact initialized/spare regions, choose the
right memory intrinsic semantics, and feed alias/range/loop metadata to LLVM.

- [x] Preserve dynamic storage facts in SSA.
  - [x] Track owner root, backing allocation, element type, capacity range,
        initialized length range, spare initialization region, alignment, and
        allocator provenance.
        Added explicit dynamic-storage SSA region facts carrying owner root,
        backing allocation kind/identity, element type, capacity, initialized
        length/prefix, spare capacity range, portable element alignment, and
        allocator provenance. Value-fact analysis and the dynamic-storage SSA
        optimizer preserve allocation identity through header rewrites, use
        path-qualified owner roots for dynamic fields, mark zero-capacity
        storage as having no backing allocation, and drop backing identity when
        a reserve may reallocate.
  - [x] Preserve prefix facts through reserve, successful `TryReserve`, append,
        tail initialization, slice creation, `MoveLast`, `MoveAt`, clear, drop,
        and inline helper calls.
        Value facts already preserve allocation, reserve, successful
        `TryReserve`/`TryReserveCapacity`, length commits from tail
        initialization, dynamic slice creation from committed lengths, and
        `MoveLast`/`MoveAt` length updates. SSA fact coverage verifies compiler
        length-field stores as dense-prefix commits that preserve backing
        allocation identity. The fact analyzer and dynamic-storage optimizer now
        also preserve exact ranges for SSA values derived from a current
        `dynamic.Length` read, so imported/inline tail appends commit precise
        `oldLength + count` prefix facts instead of falling back to broad
        count-range intermediates. Branch-edge length refinement now treats
        contradictory dynamic-length comparisons as impossible edges and
        preserves the empty initialized prefix after clear-style `MoveLast`
        drain loops, allowing post-clear no-op reserves to fold.
  - [x] Demote prefix facts at unsafe sparse-slot proofs, raw pointer escapes,
        opaque calls, FFI boundaries, and failed fallible operations.
        The dynamic-storage SSA optimizer conservatively demotes local dynamic
        facts when owner addresses/raw pointers escape through stores, indirect
        writes, ordinary calls, indirect calls, and FFI-style opaque calls.
        Raw `dynamic.Data` pointer and derived-slice escapes are traced back to
        their dynamic owner, failed `TryReserve` edges preserve the pre-call
        owner, and unsafe sparse-slot proofs do not promote dense-prefix facts
        into later safe code.

- [x] Optimize dynamic storage operations.
  - [x] Remove redundant capacity, length, null, and bounds checks when dynamic
        facts prove them.
    - [x] Added a dedicated `dynamic-storage-ssa` pass that removes provably
          no-op `Reserve` operations and folds provably successful
          `TryReserve`/`TryReserveCapacity` operations to `true` before cleanup,
          constant propagation, and branch pruning.
    - [x] Mark `MoveLast` as known non-empty and `MoveAt` as known in-bounds
          when initialized-prefix and index-range facts prove the runtime trap
          edge impossible; LLVM emission now skips those empty/bounds checks.
    - [x] Remove dynamic-storage frees when SSA region facts prove the value has
          no backing allocation, while keeping frees when a backing allocation
          may exist.
  - [x] Rewrite append-through-length loops into explicit tail `init` regions
        after one reserve.
        Added a dedicated `dynamic-append-loop-ssa` pass that recognizes
        canonical append-through-`Length` loops, hoists the starting dynamic
        length/data pointer into the loop preheader, rewrites the body to write
        through a tail pointer plus induction index, removes the per-iteration
        `Length` commit, and stores `start + count` once on the exit path.
        The pass refuses loops where the initialized value observes the changing
        `Length`, preserving per-iteration semantics for those cases.
  - [x] Lower eligible tail initialization fills to `memset`, disjoint copies to
        `memcpy`, and overlap-preserving moves to `memmove`.
        Raw-pointer loop intrinsic recognition lowers bounded and init-slice
        fills/copies/moves to `llvm.memset`, `llvm.memcpy`, and `llvm.memmove`
        with byte-length representability checks and optional dynamic length
        commits. Added dynamic-tail fill coverage proving an init slice backed by
        `dynamic` storage lowers to `memset` plus one committed `Length` update.
  - [x] Drop exactly the initialized prefix and skip spare capacity.
        MIR lowering already emits a reverse initialized-prefix drop loop guarded
        by dynamic `Length` before freeing backing storage. LLVM coverage verifies
        dynamic element destructor calls are emitted before runtime free; the MIR
        loop coverage proves spare capacity is not traversed.
  - [x] Keep dynamic root and region facts visible through stdlib helper calls so
        imported helpers optimize like same-module helpers.
        Direct-call dynamic invalidation now uses semantic/imported parameter
        effects instead of treating every call argument as an opaque mutation,
        while full init-slice memory helpers preserve dynamic owner capacity and
        backing facts until their explicit length commit. Added same-module,
        source-removed imported-package, and unrecognized-helper negative
        coverage.

- [x] Add performance and package coverage.
  - [x] Add IR gates for text append, path construction, buffer reads/writes,
        queue/dictionary growth, encoding, and direct platform byte paths.
    - [x] Added an optimized LLVM IR gate proving a capacity-sufficient
          `Reserve` does not emit dynamic reserve/realloc control flow.
    - [x] Added optimized LLVM IR gates proving non-empty `MoveLast` and
          in-bounds `MoveAt` skip dynamic-storage trap branches.
    - [x] Added an optimized LLVM IR gate proving zero-capacity dynamic storage
          drops do not emit a runtime free path when SSA facts prove no backing
          allocation exists.
    - [x] Added an optimized LLVM IR gate proving append-through-`Length` byte
          fill loops lower to one `llvm.memset` over the explicit tail region
          and one final dynamic `Length` commit.
    - [x] Existing stdlib gates cover promoted text append tail-region helpers,
          path construction through dynamic tail copies, runtime buffer
          read/write fast paths, queue/dictionary sparse-slot growth and lookup
          paths, encoding helpers over bounded raw-pointer regions, and direct
          console/platform byte paths.
    - [x] Promote pointer-backed text append disjoint helper loops to direct
          memory intrinsics after inline cloning. Slice-backed text appends
          already lower to direct `llvm.memcpy`, and path/runtime-buffer pointer
          tail copies now do as well, but `OwnedAscii.AppendAsciiDisjoint`,
          `OwnedAscii.AppendConstAsciiDisjoint`,
          `OwnedUnicode.AppendUnicodeDisjoint`, and
          `OwnedUnicode.AppendConstUnicodeDisjoint` now lower their
          pointer-backed loops to direct `llvm.memcpy` after inline cloning.
          Raw-pointer loop recognition treats values defined in dominating
          blocks as available loop inputs, and text-data helper results retain
          their source parameter identity for alias proof.
      - [x] Preserve the source/destination disjoint contract from
            `InitializeBytesFromPointerDisjoint` and
            `InitializeCodePointsFromPointerDisjoint` through inline clones when
            the source pointer is produced by text-data helpers.
      - [x] Add negative tests proving the rewrite is not applied to
            snapshot-capable append paths, non-disjoint append helpers, nullable
            or invalid text layouts, and raw pointers whose provenance may
            overlap the destination tail.
            Stdlib IR gates now require pointer-backed disjoint append helpers to
            use `llvm.memcpy`, while ordinary append helpers keep overlap checks,
            snapshot paths, and invalid-layout null guards. Existing bounded
            raw-pointer loop regressions keep overlapping raw-pointer copies on
            scalar lowering without a no-alias proof.
  - [x] Verify imported inline helpers preserve capacity/prefix/disjoint facts
        after package loading and generic monomorphization.
        Added source-removed package coverage for an imported generic inline
        helper that appends through `dynamic.Length`; imported typed-template
        lowering now preserves the generated length commit, inlining accepts
        dynamic-storage SSA shapes, and the dynamic-storage pass keeps precise
        length-derived ranges so the follow-up `Reserve` folds away while
        `MoveLast` is proven non-empty.

- [x] Add negative tests for incorrect dynamic storage optimization.
  - [x] Verify failed `TryReserve` leaves the owner unchanged and does not expose
        success-path capacity or pointer facts.
        Value-fact tests now cover success and failure edge capacity facts plus
        explicit backing-allocation provenance: the success edge drops backing
        identity when reallocation may occur, while the failure edge keeps the
        original owner allocation. Dynamic-storage SSA tests keep `TryReserve`
        when capacity may be insufficient.
  - [x] Verify reallocation invalidates old raw views and demotes facts where a
        raw escape exists.
        Dynamic-storage SSA coverage now keeps a reserve after an opaque dynamic
        owner call demotes the local owner facts. Direct SSA regressions now
        prove calls receiving raw `dynamic.Data` pointers or slices derived from
        those pointers demote the dynamic owner before later reserve folding.
  - [x] Verify sparse initialized-slot proofs do not leak dense-prefix facts back
        into later safe code.
        Dynamic-storage SSA coverage keeps `MoveLast`'s empty check after an
        unsafe sparse read, proving the sparse proof was not promoted into a
        dense initialized-prefix fact.
  - [x] Verify overlapping source/destination ranges use `memmove`, not
        `memcpy`, and non-tail moves require `MoveAt` or an explicit valid sparse
        proof.
        Dynamic-storage SSA coverage now keeps the `MoveAt` bounds check when
        the index range may reach the initialized length. LLVM emission coverage
        now asserts scalar `MoveAt` tail compaction emits `llvm.memmove` and no
        `llvm.memcpy`; ownership roadmap coverage rejects non-tail dynamic moves
        outside an explicit sparse-slot proof.
  - [x] Verify zero-length dynamic storage and null backing pointers keep their
        nullable/empty behavior.
        Dynamic-storage SSA now elides frees only when the backing allocation is
        proven absent and keeps frees when a backing allocation may exist.

### Law And Finite Effect Optimization

`law` and `finite` are part of Stark's callable type system. They give the
compiler stronger purity, memory-effect, and progress facts than LLVM can infer
from a generic function pointer.

LLVM boundary: LLVM can already do call CSE/LICM and dead-call cleanup when
function memory effects and alias facts are expressible. Stark should first
preserve and emit those facts, especially for indirect/package/generic calls,
and only add Stark-side call rewrites for facts LLVM cannot observe.

- [x] Preserve effect facts through calls.
  - [x] Carry `law`, `finite`, memory effects, capture effects, `strictfp`,
        `hot`/`cold`, and inline preference through direct calls, member calls,
        function pointers, closures, packages, generics, and inline clones.
        Function-effect profiles, semantic memory/capture summaries, package
        compiler-fact manifests, generic-template facts, inline clone
        summaries, and LLVM call emission all consume the same structured
        effect model.
  - [x] Build a conservative memory-version model for law calls that read
        argument memory or const/global memory.
        Stark-side call CSE currently uses the strongest sound version model:
        repeated law reads are CSE'd only when every read memory argument has
        permanent const-graph provenance, so intervening ordinary writes cannot
        invalidate the value. Mutable argument-memory cases stay visible to LLVM
        through `memory(...)`, alias, readonly/writeonly, and capture facts
        instead of being speculated in Stark SSA.
  - [x] Treat function-pointer kinds as contracts so `fnptr<law ...>` and
        `fnptr<finite ...>` keep call-site optimization attributes.
        Indirect function-pointer and closure calls emit `willreturn`,
        `mustprogress`, `nosync`, `nofree`, `memory(...)`, capture, ABI, and
        `strictfp` call-site attributes according to the callable type, not the
        static caller alone.

- [x] Optimize effect-safe calls.
  - [x] Emit enough memory-effect and alias facts for LLVM to CSE identical law
        calls when scalar arguments and all read memory regions are unchanged.
  - [x] Emit enough loop-invariance, memory-effect, and alias facts for LLVM to
        hoist eligible law calls out of loops.
  - [x] Add Stark-side call CSE or hoisting only after IR gates show LLVM misses
        a case because the required Stark fact is not representable in LLVM IR.
        The dedicated Stark SSA pass is limited to const-graph law-call CSE,
        because LLVM cannot rediscover Stark's permanent transitive const graph
        from ordinary pointer IR. Generic scalar call CSE, LICM, and DCE remain
        LLVM-owned.
  - [x] Keep generic dead-law-call removal LLVM-owned unless a future IR gate
        proves a Stark-only fact is being lost.
        Calls already carry `memory(none)`/readonly/capture/progress facts when
        sound, so LLVM DCE can remove unused effect-free calls. Stark cleanup
        keeps unknown/plain function-pointer calls, preventing accidental
        elimination without an effect contract.
  - [x] Specialize higher-order helpers when callback function-pointer kinds
        expose finite/law guarantees.
        Function-item and closure devirtualization, singleton known-target
        lowering, `!callees` metadata, and inline closure adapters expose
        stronger callback contracts to later SSA passes and LLVM without
        inventing a runtime witness layer.
  - [x] Emit restrictive LLVM `memory(...)`, `willreturn`, `mustprogress`,
        `nosync`, `nofree`, `nounwind`, and call-site attributes where sound.

- [x] Add package and indirect-call coverage.
  - [x] Verify imported declarations, imported typed bodies, generic templates,
        non-capturing lambdas, closure adapters, and function-pointer calls keep
        equivalent effect summaries.
        Existing package/facade tests preserve function-effect manifests,
        strictfp/hot/cold/noinline metadata, callable type kinds, imported
        generic-template semantics, and package-backed callable aliases.
  - [x] Add IR gates proving same-module and imported law wrappers erase to the
        same optimized shape.
        Existing LLVM and pipeline gates cover imported law helper inlining,
        closed-world law wrapper inline decisions, singleton indirect-call
        devirtualization, and finite/law function-pointer call attributes.

- [x] Add negative tests for incorrect law/finite optimization.
  - [x] Verify a `law` call that reads argument memory is not CSE'd or hoisted
        across a write to that memory.
        Const-graph CSE refuses readonly raw-pointer locals without permanent
        const provenance, so mutable/replaceable argument memory is not treated
        as stable by the Stark pass.
  - [x] Verify `finite` alone does not imply purity, readonly behavior, or dead
        call eliminability.
  - [x] Verify weaker `fnptr<fn ...>` callbacks do not inherit `law` or `finite`
        facts from a stronger call site.
  - [x] Verify FFI, opaque, unsafe, varargs, strictfp-sensitive, trapping, or
        effect-unknown calls block the optimizations that require stronger facts.
        Existing regressions cover FFI non-CSE, plain function-pointer calls
        without finite/law attributes, strictfp constrained calls and call-site
        `strictfp`, backend opaque/package facts, and varargs/FFI boundaries.

### Closed-World Generic, Trait, And Doctrine Optimization

Stark has a closed-world bias, monomorphized generics, static dispatch by
default, compile-time-only traits, and doctrine bundles with no runtime identity.
These source facts should erase abstraction layers before LLVM has to infer
them.

LLVM boundary: keep relying on LLVM's inliner, internalizer, IPSCCP, and
devirtualization once concrete call targets and bodies are visible. Stark owns
making those bodies visible, substituting typed facts correctly, and avoiding
runtime representations the language does not require.

- [x] Preserve closed-world optimization facts.
  - [x] Track visibility, address-taken status, backend opacity, inline
        preference, function-item identity, generic instantiation arguments, and
        doctrine/trait dispatch targets.
        Existing monomorphization, specialization planning, address-taken
        pruning, backend opacity, inline clone, doctrine, and trait tests cover
        these facts through the pipeline.
  - [x] Ensure package images publish typed generic-template bodies and optimizer
        summaries for all API-visible specialization material.
        Package generic-template sections publish typed bodies, full
        bound-operation summaries, direct-call/function-address summaries, and
        backend optimization mode for public and reachable template material.
  - [x] Preserve bound-operation facts, memory contracts, effect summaries,
        layout facts, and reachable inline-clone calls through package loading.
        Package loader and source-corruption regressions preserve compiler
        facts, typed bodies, bound-operation families, effects, layouts, and
        reachable inline clones without source body text.

- [x] Erase closed-world abstraction overhead.
  - [x] Monomorphize generic functions, methods, doctrines, and trait-bound
        helpers into concrete bodies whenever source or package typed bodies are
        available.
  - [x] Lower doctrine and trait member uses to direct static calls without
        witness tables, trait objects, heap allocation, or runtime dispatch.
        Existing trait/doctrine feature and LLVM tests assert direct/static
        calls and no witness-table/vtable/function-pointer dispatch surface.
  - [x] Inline small wrappers and generic adapters after substitution so range,
        const, alias, and effect facts become visible to later passes.
  - [x] Internalize module-private and package-private symbols when export and
        backend-boundary rules allow it.
  - [x] Devirtualize function-item uses that have not been explicitly promoted
        to runtime function pointers.

- [x] Add cross-package verification.
  - [x] Corrupt or remove package source body text after package creation and
        verify imported generic bodies still specialize from typed/bound facts.
  - [x] Verify imported inline clones are driven by reachable bound calls, not by
        opportunistic source-text scans.
  - [x] Add benchmark and IR gates comparing hand-written, generic-wrapper,
        law-wrapper, doctrine, and trait-style APIs.
        Existing IR gates compare direct generic/law/doctrine/trait-style
        lowering shapes. Keep performance benchmark expansion in the general
        benchmark suite rather than as a separate compiler-modeling blocker.

- [x] Add negative tests for incorrect closed-world optimization.
  - [x] Verify exported, FFI, address-taken, `[Backend(Opaque)]`, `noinline`,
        `cold`, recursive, varargs, or body-missing boundaries are not inlined or
        internalized incorrectly.
  - [x] Verify different generic instantiations do not share substituted layouts,
        range facts, enum payload layouts, dynamic element layouts, or function
        pointer ABI signatures.
  - [x] Verify unsafe functions cannot be promoted to safe `fnptr` values and
        opaque external callbacks keep indirect-call behavior.
  - [x] Verify public ABI shape and package visibility are preserved even when
        internal helper layers are erased.

## Integer Arithmetic Folding

This pass family recognizes repeated typed integer arithmetic in Stark SSA and
rewrites it into compact, semantically equivalent operations before LLVM
emission.

The goal is to make Stark's own SSA optimizer expose stronger integer intent to
the backend instead of relying only on late LLVM target lowering to recover
obvious repeated-add, repeated-subtract, constant-coefficient, and
repeated-multiply shapes.

### Target Rewrites

Repeated identical add terms:

```stark
y = x + x + x + x + x + x;
```

become SSA equivalent to:

```stark
tmp = x * 6;
y = tmp;
```

Repeated terms plus constants:

```stark
y = x + x + x + 5;
```

become:

```stark
tmp = x * 3;
y = tmp + 5;
```

Subtraction contributes negative coefficients when the operation semantics make
that equivalence exact:

```stark
y = x + x + x - z - z + 5;
```

becomes:

```stark
tmp0 = x * 3;
tmp1 = z * 2;
y = tmp0 - tmp1 + 5;
```

Existing constant multipliers should also participate once their overflow and
range semantics match the surrounding chain:

```stark
y = (x * 2) + (x * 3) - x;
```

becomes:

```stark
y = x * 4;
```

Repeated multiplication by the same value should become a single integer
exponent operation when that is profitable and exact:

```stark
y = x * x * x * x;
```

becomes SSA equivalent to:

```stark
y = powi(x, 4);
```

Multiple repeated product factors can be grouped independently:

```stark
y = x * x * z * z * z;
```

becomes:

```stark
tmp0 = powi(x, 2);
tmp1 = powi(z, 3);
y = tmp0 * tmp1;
```

`powi` here means a typed Stark SSA integer exponent operation or compiler-owned
intrinsic/helper, not necessarily a public source-level function. Lowering may
expand small exponents back into a balanced multiply tree when that is faster,
or lower larger exponents to exponentiation-by-squaring or a target/runtime
helper. LLVM should still be responsible for target-specific lowering of
multiplication by a constant into `lea`, shift/add, or a real multiply depending
on the target cost model.

### Shared Placement

- [x] Add a Stark SSA optimization pass before LLVM emission.
  - [x] Place the pass after SSA has been canonicalized enough that repeated
        arithmetic chains are visible.
        Implemented as `arithmetic-fold-ssa` after branch shaping and after the
        existing inlining, cleanup, and earlier constant propagation window.
  - [x] Run the pass after constant propagation where possible so fully-known
        constants are folded first.
  - [x] Ensure the pass also runs after inlining or during the final SSA
        cleanup window, since inlining can expose new repeated arithmetic
        chains.
  - [x] Run existing SSA cleanup and constant propagation after the rewrite so
        dead intermediate arithmetic and newly-created constants disappear.
  - [x] Keep the pass disabled at no-optimization levels where preserving the
        source-shaped debug experience is more important than generated code
        quality.

### Linear Add/Sub Folding

- [x] Implement conservative integer linear folding.
  - [x] Support ordinary integer `+` and `-` chains.
        Ordinary subtraction folds only when static range facts prove the
        introduced multiply cannot create a new overflow point.
  - [x] Support wrapping integer `+%` chains by emitting wrapping
        multiplication.
  - [x] Support wrapping integer `-%` chains by carrying negative coefficients
        modulo the operation width.
  - [x] Skip saturating `+|` and `-|` chains because repeated saturating
        arithmetic is not generally equivalent to saturating multiply.
  - [x] Require an absolute coefficient of at least two before emitting a new
        multiply.
  - [x] Fold only identical SSA values and compile-time integer constants in the
        first version, not full symbolic algebra.
        Identical SSA values are folded, existing constant-coefficient terms
        participate when safe, and same-sign literal constant runs are combined
        without introducing broader reassociation.
  - [x] Preserve Stark integer type and range facts when constructing the new
        multiply/add/subtract rvalues.
  - [x] Preserve no-wrap, signedness, range, and wrapping semantics exactly for
        every rewritten operation.
        Ordinary rewrites are deliberately limited to contiguous repeated runs
        so the pass does not broaden no-overflow/reachability domains by
        reassociating unrelated terms.

- [x] Build the SSA analysis data needed for linear folding.
  - [x] Build a definition map for SSA value instructions.
  - [x] Identify candidate `SsaBinaryRValue` nodes whose operator is ordinary
        add/subtract or wrapping add/subtract.
  - [x] Require candidate expressions to have an integer result type.
  - [x] Recursively flatten compatible linear arithmetic trees through the
        definition map.
  - [x] Propagate coefficient signs through subtraction.
  - [x] Stop flattening when the operator, result type, or arithmetic semantics
        differ.
  - [x] Stop flattening at non-linear operations unless they are recognized
        constant-coefficient forms.
        Recognized constant-coefficient forms are ordinary/wrapping multiply by
        compile-time constants, unary negation, and proven-safe left shifts.
        Division, remainder, non-constant multiplication, and mixed arithmetic
        families remain opaque.
  - [x] Guard against accidental cycles or malformed SSA with an invariant
        failure.

- [x] Count and classify linear terms.
  - [x] Count operands using structural/value identity and signed coefficients.
        V1 compacts only contiguous repeated runs to preserve ordinary
        arithmetic's overflow-domain behavior.
  - [x] Preserve stable term order for deterministic SSA output.
  - [x] Keep operands with coefficient `1` or `-1` as add/subtract terms.
  - [x] Combine integer constants into one leftover constant where doing so is
        valid for the operation semantics and target type.
  - [x] Skip or preserve constants conservatively when folding would change
        wrapping or range behavior.
  - [x] Drop zero-coefficient terms and route all-constant results back through
        existing constant propagation.

- [x] Emit optimized linear SSA.
  - [x] Emit `operand * coefficient` for each operand whose absolute
        coefficient is at least two.
  - [x] Use wrapping multiply for folded wrapping arithmetic chains.
  - [x] Rebuild any remaining single operands and constants as add/subtract
        terms.
  - [x] Keep the original result name for the root value instruction when
        practical.
  - [x] Insert any required temporary SSA value instructions immediately before
        the rewritten root instruction.
  - [x] Preserve source locations and useful debug text for emitted temporaries.

### Constant-Coefficient Inputs

- [x] Extend linear folding to relevant constant-coefficient arithmetic.
  - [x] Recognize existing integer multiplication by compile-time constants as
        linear terms when the multiply semantics match the surrounding chain.
  - [x] Combine coefficients from shapes such as `(x * 2) + (x * 3) - x`.
  - [x] Treat unary negation as a negative coefficient only when the source type
        and overflow semantics make the rewrite exact.
  - [x] Recognize left shifts by compile-time constants as multiplication by a
        power of two only when the shift semantics, overflow behavior, and range
        facts prove equivalence.
  - [x] Reject or leave untouched division, remainder, non-constant
        multiplication, and mixed operation families because those are not
        generally linear rewrites.

### Repeated Multiplication And Exponent Folding

- [x] Define the representation for integer exponent operations.
  - [x] Decide whether SSA needs a dedicated `SsaIntegerPowerRValue`, a
        compiler intrinsic call, or a typed helper-call representation.
        The pass reuses `SsaBinaryRValue` exponent operations and adds an
        explicit `WrappingExponent` operator for wrapping product folds.
  - [x] Make the representation carry base type, result type, exponent value,
        arithmetic family, and overflow/wrapping semantics.
  - [x] Ensure a public source-level exponent operator is not required for the
        optimizer to emit the operation internally.
  - [x] Teach debug/artifact rendering to show exponent operations clearly.

- [x] Implement conservative product-chain recognition.
  - [x] Support ordinary integer `*` chains only when the exponent operation can
        preserve the same no-overflow/range guarantees.
  - [x] Support wrapping integer `*%` chains by emitting a wrapping exponent
        operation over the same integer width.
  - [x] Skip saturating `*|` chains unless Stark defines a saturating exponent
        operation with exactly matching step-by-step semantics.
  - [x] Require at least two identical multiplicative occurrences before
        emitting an exponent operation.
  - [x] Fold only repeated identical SSA values and compile-time integer
        constants at first.
  - [x] Stop at division, remainder, non-integer arithmetic, mixed arithmetic
        families, or type changes.

- [x] Count and classify product factors.
  - [x] Count repeated bases using structural/value identity.
  - [x] Preserve stable factor order for deterministic SSA output.
  - [x] Keep single-occurrence factors as multiplication terms.
  - [x] Combine literal integer constants through existing constant propagation
        when safe.
  - [x] Represent `x * x * x` as base `x` with exponent `3`, not as a chain of
        pairwise rewrites.

- [x] Lower exponent operations for performance.
  - [x] Use a cost model so tiny exponents can lower to a straight-line multiply tree
        when that beats a helper call.
        Constant integer exponents `0..8` lower directly to straight-line
        multiplies/copies/constants without emitting the helper.
  - [x] Lower larger constant exponents to exponentiation-by-squaring or a
        compiler-owned helper that is inlineable and monomorphized by integer
        type.
        The integer exponent helper is now per-bit-width and uses
        exponentiation by squaring instead of a linear multiply loop.
  - [x] Preserve wrapping and no-wrap semantics through lowering.
  - [x] Avoid introducing a runtime call in hot optimized code when a few direct
        multiplies are cheaper.
  - [x] Let later LLVM passes optimize the lowered multiply tree or helper body
        for the concrete target.

### Correctness Tests

- [x] Test `x + x + x` becomes `x * 3` when `x` is not compile-time known.
- [x] Test `x + x + x + x + x + x` becomes `x * 6`.
- [x] Test `x + x + x + 5` becomes `x * 3 + 5`.
- [x] Test multiple repeated terms such as `x + x + y + y + y + 4`.
- [x] Test `x + x + x - y - y + 4` becomes `x * 3 - y * 2 + 4`.
- [x] Test adjacent cancellation and explicitly cover deferred non-adjacent
      cancellation.
  - [x] Adjacent `x - x` folds to a zero constant without leftover binary
        operations.
  - [x] Non-adjacent `x + y - x` remains deferred because folding it would
        require reassociating unrelated terms; add only when range/no-wrap proof
        intentionally permits that broader algebra.
- [x] Test `(x * 2) + (x * 3) - x` becomes `x * 4` once constant-coefficient
      multiplication is enabled.
- [x] Test left-shift coefficient recognition only fires for cases where the
      shift is proven equivalent to multiplication by a power of two.
- [x] Test `x * x` becomes `powi(x, 2)` or the selected cheaper lowered shape.
- [x] Test `x * x * x * x` becomes a single exponent operation before final
      lowering.
- [x] Test `x * x * y * y * y` groups repeated bases independently.
- [x] Test product chains with single factors are preserved and do not become
      pointless exponent-one operations.
- [x] Test wrapping `+%`, `-%`, and `*%` chains emit wrapping semantics.
  - [x] `+%` and `-%` repeated linear chains emit wrapping multiply.
  - [x] `*%` emits `WrappingExponent`.
- [x] Test saturating `+|`, `-|`, and `*|` chains are not rewritten.
  - [x] `+|` repeated linear chains are preserved.
  - [x] Add explicit `-|` and `*|` product-family negatives with the product
        pass.
- [x] Test known constants still fold through the existing constant propagation
      path.
- [x] Test the optimization remains valid for imported typed templates and
      inline clones if the pass runs after generic monomorphization or inlining.
      Imported typed-template coverage removes the package source body and
      verifies the monomorphized SSA still folds from typed package facts.
      Same-module post-inline exposure is covered by pass ordering; add a
      dedicated imported inline-clone arithmetic regression when inline clones
      contain a non-generic repeated-arithmetic body.

### Performance And Codegen Checks

- [x] Assert optimized LLVM IR for unknown `x` contains a multiply by the repeat
      count instead of a left-associated add chain.
- [x] Assert optimized LLVM IR for subtraction-heavy linear chains contains
      multiply/subtract forms instead of repeated subtract/add chains.
- [x] Assert product-chain optimization avoids repeated multiply chains where
      the selected exponent lowering is cheaper.
- [x] Check native optimized output lets LLVM choose target-specific
      shift/add/`lea` forms where profitable.
      The x64 native codegen regression compiles a folded `x + x + x` body at
      `-O3`, disassembles the object file, and verifies LLVM selected a
      `lea`-style target instruction without a runtime call.
- [x] Confirm exponent lowering does not turn small hot expressions into slower
      runtime calls.
- [x] Confirm `-O0` behavior remains debuggable if this pass is disabled at
      no-optimization levels.
      The pass is disabled for both `O0` and `Og`; the current regression checks
      the debug-optimized `Og` path remains source-shaped.
- [x] Confirm optimized builds do not regress existing SSA cleanup, constant
      propagation, value numbering, or LLVM emission tests.
      Focused SSA and LLVM emission regressions pass after the pipeline hook.

### Polish And Maintainability

- [x] Add debug rendering coverage so the folded SSA shape is visible in
      artifact output.
- [x] Document the pass ordering in the compiler pipeline internals docs.
- [x] Keep the pass allocation-conscious by using per-function maps and
      reusable local collections where practical.
- [x] Avoid broad algebraic reassociation until range/no-wrap proof coverage is
      strong enough to make it unambiguous.
- [x] Keep linear-sum folding and product/exponent folding internally separated
      even if they share traversal infrastructure.


### Non-Goals For V1

- [x] Do not implement full symbolic algebra.
- [x] Do not reassociate unrelated mixed arithmetic trees.
- [x] Do not rewrite saturating arithmetic chains.
- [x] Do not rewrite division, remainder, or non-constant mixed multiplication.
- [x] Do not mix ordinary and wrapping arithmetic in one flattened chain.
- [x] Do not require a public Stark exponent operator before the optimizer can
      use an internal exponent representation.
- [x] Do not second-guess LLVM's target-specific lowering of multiply by a
      constant.
