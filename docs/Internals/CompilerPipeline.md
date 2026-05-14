# Compiler Pipeline

## Strategy

Stark should treat parsing as the beginning of compilation, not the compiler architecture.

The compiler pipeline should be organized around explicit passes, typed artifacts, and clear phase boundaries. Each pass should consume a small number of well-defined artifacts and publish a new artifact for the next stage. This keeps the frontend flexible while the language is still evolving and makes it practical to replace or split passes later without rewriting the entire compiler.

The current pass system supports:

- dependency-based pass ordering
- a typed artifact store instead of a loose string dictionary
- execution history for debugging and profiling
- fail-fast behavior after diagnostics
- optional stop-after-pass execution for debugging and CLI emit modes
- optional continue-after-error execution when investigating crashes or cascading issues
- the ability to insert new analysis or lowering passes between existing stages

## Accepted-Program Contract

The central pipeline rule is that MIR lowering is not a language-validity
filter.

Parsing decides whether the source text is Stark syntax. Declaration indexing,
module loading, type checking, semantic validation, ownership validation, and
the pre-MIR lowering-contract validation decide whether grammatically valid
source is a valid Stark program. If the source is invalid, it must be rejected
before MIR exists, with a precise diagnostic at the phase that owns the rule.

Once compilation reaches `lower-mir` without diagnostics, the compiler has
accepted the program's executable semantics. From that point forward, lowering
passes must either lower the construct directly or report an internal compiler
invariant failure. They must not silently decide that the construct is not part
of the language, produce declaration-only bodies for accepted functions, or hide
behind `unsupported-lowering`, `llvm-body-fallback`, `llvm-asm-fallback`,
`llvm-body-pending`, or `missing-function-body` logs.

The practical split is:

- syntax errors are parser diagnostics
- unresolved names, invalid overloads, invalid arity, invalid indexing/slicing,
  invalid constructor/object/enum shapes, void calls used as values, invalid
  storage classes, invalid ownership, invalid borrows, invalid drops, and
  invalid raw-pointer use are front-end diagnostics before MIR
- missing typed facts needed by MIR are `validate-lowering-contract`
  diagnostics before MIR
- malformed SSA that cannot be emitted as LLVM is a `validate-ssa` diagnostic
  before `emit-llvm`
- any remaining lowering/codegen "unsupported" case for an accepted program is a
  compiler bug, not a language-validity decision

This contract matters for performance as much as correctness. If an accepted
program can fall back to declaration-only code generation or broad helper
lowering, the compiler loses the exact facts Stark was designed to guarantee to
LLVM. The valid-program path should keep those facts explicit through MIR, SSA,
ABI lowering, and LLVM emission.

## Default Stages

The current default compilation pipeline is dependency-ordered rather than hard-coded as one giant method. `DefaultCompilerPipeline.Create()` registers the passes, and the builder resolves them by dependencies, phase, and registration order. The list below is the normal resolved order and uses the actual pass IDs accepted by `StopAfterPassId`.

1. `parse`
   Reads source text and produces the ANTLR parse result plus syntax diagnostics.
2. `syntax-model`
   Lowers the raw ANTLR tree into a compiler-owned syntax model that extracts the module name, imports, top-level declarations, function kinds, modifiers, and signatures.
3. `declaration-index`
   Builds a declaration index keyed by name so later passes do not need to walk the parse tree again.
4. `module-graph`
   Resolves imports through the configured module resolver and builds the current module graph.
   This is where unresolved imports become compiler diagnostics instead of remaining parser-only facts, where transitive module names are discovered for later cross-module binding, where explicit re-export imports are turned into the root module's accessible import closure, and where source-backed and manifest-backed packages enter the same resolution path.
5. `load-modules`
   Loads source for resolved Stark modules when the resolver can provide it, parses those modules, and publishes a loaded module set.
   This is where multi-file Stark builds stop being just names in a graph and become real declaration sources for typing, effect derivation, and code generation, whether those declarations came from `.stark` source files or synthesized package interfaces from `.starkpkg.json` manifests.
6. `symbol-catalog`
   Builds the initial module symbol catalog and partitions names by visibility.
7. `function-effects`
   Derives compiler guarantees from Stark function syntax across the loaded module set.
   This is where `law`, `finite`, `ffi`, `hot`, `cold`, and inline preferences are converted into semantic facts such as `nounwind`, `mustprogress`, `willreturn`, `nofree`, `nosync`, and internal `fastcc` intent.
8. `type-check`
   Resolves named and builtin types, assigns literal types, validates globals and a useful subset of function bodies, and produces typed signatures for later lowering.
   Imported declarations are loaded into the same typed world, so qualified Stark calls and type references can flow across modules before LLVM lowering. Function-pointer types retain their callable kind (`fn`, `finite`, `law`, or `finite law`), memory contract groups, and bounded raw-pointer parameter count expressions here; promotion/assignment checks reject weaker callbacks, incompatible bounded-region contracts, `null` callbacks, and aggregate initializer shapes that would zero-fill `fnptr` storage before lowering.
9. `instantiation-ownership`
   Collects concrete generic function and type instantiation ownership across the loaded module set.
   This pass decides which module owns each instantiation trigger, expands type/function triggers that arise from generic use, tracks destructor-driven instantiations, and validates currently provable library constraints such as supported `System.Collections.Dictionary<K, V>` key types.
10. `monomorphization-plan`
   Builds the concrete generic realization plan from the instantiation ownership model.
   This is where generic functions and types receive deterministic symbol names, linkage choices, code-size heuristics, declaration-only fallback classification, and planning facts from source-backed modules and package images.
11. `enum-layout`
   Derives the current compiler-owned enum layout artifact from the typed enum declarations.
   Today this pass selects the direct tag runtime representation used by later semantic checks and LLVM lowering, and it merges enum layout facts loaded from package images.
12. `semantic-validate`
   Validates Stark-specific semantic contracts after typing.
   This is where borrow escape classes, `law` restrictions, `finite` restrictions, raw-pointer boundary rules, recursive finite-call cycles, parameter memory summaries, and call-memory summaries are checked. Indirect calls through `fnptr<law ...>` and `fnptr<finite ...>` are validated against the current function's obligations here; MIR lowering should receive an accepted indirect call, not rediscover whether the callback kind is legal.
13. `refine-function-effects`
   Refines function guarantees from semantic body analysis after the declaration-driven effect pass.
   This is where plain `fn` bodies that can be proven `law`, `finite`, or `finite law` are upgraded into the effect profiles consumed by HIR lowering, ABI lowering, and LLVM emission. It also publishes the closed-world optimization model that classifies direct-call eligibility, trait/doctrine sealing, law-call optimization opportunities, and caller-sensitive clone candidates.
14. `specialization-plan`
   Converts the monomorphization plan plus closed-world optimization facts into an ordered strategy list for each concrete generic function.
   This pass decides whether a function should prefer a caller-specialized law clone, an owned concrete body, an ABI-boundary fallback, or a combination of those paths.
15. `specialization-codegen-strategy`
   Lowers specialization planning decisions into the code-generation strategy artifact consumed by HIR, MIR, and LLVM emission.
   This pass records whether each concrete specialization is ABI-fallback-only, emits an owned concrete body, or emits an owned body while preferring law-caller clones where possible.
16. `ownership-validate`
   Validates deterministic ownership and lifetime rules after the higher-level semantic checks.
   This is where move tracking, use-after-move rejection, implicit drop scopes, branch-sensitive ownership state, and basic borrow lifetime sources are checked so safe code remains non-GC and leak-resistant by construction.
17. `validate-lowering-contract`
   Checks that accepted executable source has the typed facts MIR lowering
   requires.
   This pass is the final validity gate before body lowering. It verifies that
   calls, member calls, function-pointer calls, indexing/slicing operations,
   object/constructor/enum construction, switch shapes, layout queries,
   dynamic-storage operations, lambda facts, and imported/package-backed typed
   bodies have the recorded type, arity, addressability, layout, and receiver
   facts that MIR lowering consumes. Missing or contradictory facts are
   diagnostics here, not `lower-mir` decisions.
18. `lower-hir`
   Produces the current compiler-owned HIR shell.
   Today this is intentionally shallow: it packages root-module functions, source-loaded imported-module functions, non-capturing lambda bodies, and materialized specialized functions that already have typed signatures, body presence, derived effect profiles, and specialization strategy facts so later lowering passes stop depending on declaration-model details.
19. `lower-mir`
   Produces a mid-level IR with explicit locals, basic blocks, typed operands, typed rvalues, and terminators.
   This is where accepted structured source bodies and imported typed template
   bodies become control-flow-aware CFG form suitable for ownership precision
   and explicit value lowering, including aggregate field/index operations,
   slice formation, raw address formation, indirect load/store,
   constructor/destructor lowering, runtime drop lowering, function-pointer
   calls with their accepted callable-kind contracts, and explicit
   conversions. This pass consumes accepted typed facts. It must not decide
   whether a parsed construct is valid Stark; invalid source belongs in earlier
   diagnostics, and missing lowering support for an accepted construct is an
   internal compiler invariant failure.
20. `borrow-liveness`
   Refines ownership validation with non-lexical-style lifetime analysis over normalized MIR.
   This pass computes borrow-local liveness across the CFG and updates the ownership model when a move, overwrite, or return would conflict with still-live borrows. Its phase is semantic, but it intentionally depends on `lower-mir` and runs after MIR exists.
21. `lower-ssa`
   Produces SSA form from MIR, prunes unreachable CFG blocks, and inserts phi nodes where control-flow paths merge.
   This is where mutable Stark locals stop looking like source variables and start looking like compiler values, while addressable locals still surface as explicit allocate/store/lifetime operations across both root-module and source-loaded imported-module functions.
22. `cleanup-ssa`
   Canonicalizes and simplifies SSA before later consumers use it.
   This pass canonicalizes compare/branch shapes, simplifies trivial terminators and single-case switches, normalizes switch-lowering structures, reuses repeated SSA computations when memory ordering allows it, removes unused pure instructions and local storage, collapses trampoline blocks, merges linear blocks, canonicalizes early-return diamonds, and prunes unreachable blocks. At `O0` and `Og`, this pass is bypassed and the original SSA artifact is republished as optimized SSA.
23. `const-prop`
   Runs constant propagation over the cleaned SSA graph.
   This pass folds constant arithmetic, conversions, compares, branches, and simple `switch` decisions, then runs the SSA cleanup optimizer again and republishes the optimized SSA artifact consumed by LLVM emission. At `O0` and `Og`, this pass is also bypassed.
24. `devirt-ssa`
   Rewrites recoverable indirect calls into direct SSA calls.
   This pass currently handles singleton function-address cases produced by function item or non-capturing lambda lowering, preserves the address-taken function records that still matter after rewriting, and republishes the optimized SSA artifact. Finite non-singleton target sets remain indirect but are still available to LLVM emission as `!callees` metadata when every possible SSA target is a known function address. At `O0` and `Og`, this pass is bypassed.
25. `inline-ssa`
   Inlines eligible direct calls in SSA.
   This pass consumes refined effects, module-private visibility, declared law functions, monomorphization planning, and specialization codegen strategy facts. It inlines small safe candidates, gives declared law functions a larger inline budget, can inline with constant specialization arguments, rejects recursive and unsafe candidate shapes, then reruns SSA cleanup and constant propagation. At `O0` and `Og`, it is bypassed.
26. `value-facts`
   Computes SSA value facts for later optimization and LLVM emission.
   This pass records integer ranges, known bits, boolean constants, nullability, pointer alignment, length ranges, text literal payloads, and block-entry/block-exit fact maps. It also publishes a summary log entry so optimization decisions can be inspected.
27. `specialize-ascii-to-unicode-literals-ssa`
   Specializes ASCII-to-Unicode literal conversion paths using SSA value facts.
   This pass recognizes compile-time text literal payloads and rewrites supported conversion patterns into cheaper constant/view forms, then refreshes SSA value facts when it changes the graph. At `O0` and `Og`, it is bypassed.
28. `prune-branches`
   Removes branches and switch cases proven unreachable by SSA facts.
   This pass consumes the value-fact model, folds fact-known branch directions and switch targets, removes stale phi incomings, reruns cleanup and constant propagation, and republishes refreshed value facts. At `O0` and `Og`, it is bypassed.
29. `memory-opt-ssa`
   Runs alias-aware SSA memory optimization.
   This pass consumes refined memory/effect summaries to forward stack scalar loads, eliminate dead stack field stores, preserve memory barriers when calls or escapes can observe state, and keep only transformations justified by local storage and alias facts. It reruns cleanup, constant propagation, and value-fact analysis when it changes the graph. At `O0` and `Og`, it is bypassed.
30. `sroa-ssa`
   Performs scalar replacement for eligible aggregate memory operations.
   This pass removes dead aggregate copies, forwards exact scalar field/index copies when later loads can observe them, and stays conservative around moves, escaping addresses, non-scalar observations, and unsupported aggregate shapes. It reruns cleanup, constant propagation, and value-fact analysis when it changes the graph. At `O0` and `Og`, it is bypassed.
31. `shape-branches`
   Runs final branch shaping over optimized SSA.
   This pass enables select predication for simple return diamonds after the heavier SSA optimization passes have run, then refreshes value facts. At `O0` and `Og`, it republishes the existing optimized SSA unchanged.
32. `lower-abi`
   Produces a compiler-owned ABI model from typed Stark signatures and function effects.
   This is where internal aggregate parameters and returns are lowered to stable calling-convention rules, while `ffi` signatures keep their foreign-facing shape and imported Stark calls are assigned their dependency-facing symbol/ABI form.
33. `validate-ssa`
   Validates optimized SSA against the ABI and LLVM-emission contract.
   This pass catches malformed SSA before code generation: missing value
   definitions, malformed terminators, unsupported SSA node kinds, invalid call
   ABI metadata, unsupported conversions, invalid dynamic-storage and
   memory-copy shapes, malformed text/global/function address values, invalid
   address forms, and operator shapes that LLVM emission cannot represent.
34. `emit-llvm`
   Produces LLVM IR from the optimized SSA form plus semantic, type, and ABI metadata.
   The emitter generates real function bodies for accepted source bodies and
   materialized specializations, emits concrete aggregate/array/slice/string
   layouts, uses parameter memory summaries from semantic validation to emit
   stronger `readonly`/`writeonly`/`nocapture`/`captures(...)` facts for root
   Stark functions, consumes SSA value facts for range, alignment, assumption,
   and literal-data decisions, consumes scoped noalias, loop behavior, and
   loop-access groups from MIR/SSA so `willexit` backedges receive
   `!llvm.loop.mustprogress` and validated `independent` loops receive
   `!llvm.loop.parallel_accesses`, emits function-kind call-site attributes for indirect
   function-pointer calls where the `fnptr` type carries `finite`, `law`, or
   `finite law` guarantees, emits LLVM `!callees` metadata for indirect
   function-pointer calls whose SSA target set is a closed set of known function
   addresses, marks accepted direct function-pointer ABI parameters and returns
   `nonnull`, marks pointer-backed safe borrow returns `nonnull` and
   `dereferenceable` when layout is known,
   consumes the closed-world optimization model for caller-sensitive
   law-path specialization decisions, can materialize internal root-side clones
   of eligible imported law bodies and imported inline bodies for closed-world
   optimization, emits optimized raw-pointer and slice loops as
   `memcpy`, `memmove`, or `memset` when the source contracts prove the shape,
   emits imported Stark declarations using the ABI model, and qualifies non-FFI
   symbols for library builds. Declaration-only emission is reserved for true
   declarations such as FFI or intentionally bodyless ABI surfaces. If a source
   body, source asm body, or materialized specialization logs
   `llvm-body-fallback`, `llvm-asm-fallback`, or `llvm-body-pending`, the
   default pipeline converts that log into a compiler diagnostic instead of
   pretending the accepted program compiled.

## Extension Points And Remaining Work

The pass structure is no longer just a skeleton; the current compiler has real
typing, generic instantiation ownership, monomorphization planning,
specialization planning, semantic validation, ownership validation, MIR, SSA,
SSA cleanup, constant propagation, devirtualization, direct-call inlining,
value-fact analysis, fact-driven branch pruning, alias-aware memory
optimization, aggregate scalar replacement, pre-LLVM SSA validation, ABI
lowering, package-image loading, and LLVM emission. The pipeline still leaves
clear extension points for future performance and tooling work:

- broader interprocedural MIR/SSA inlining and clone cost modeling before LLVM
- cross-block value numbering and redundancy elimination beyond the current local reuse and cleanup passes
- destination propagation and copy-elision improvements
- deeper proof/range propagation for bounds, enum tags, non-null facts, and raw-region byte extents
- loop optimization before LLVM beyond current loop metadata and recognized raw-pointer/slice intrinsic lowering
- deeper package-aware generic/template inlining and clone pruning
- first-class value tracing and trace-file sinks for compiler observability
- allocator-backed `arena` lowering

These should fit naturally as additional dependency-ordered passes between the
existing semantic, lowering-contract, MIR, SSA, ABI, and code-generation
boundaries. The important constraint is not physical placement in one method;
it is publishing a typed artifact at a stable boundary and declaring the exact
pass dependencies.

## Current IR Boundary

Stark should not lower directly from the parse tree to LLVM IR.

The current ownership split is:

- parse tree:
  ANTLR-owned syntax structure
- syntax model:
  Stark-owned representation of modules, declarations, and signatures
- typed and semantic models:
  type information, instantiation ownership, monomorphization and specialization plans, enum layout, effect summaries, semantic validation, closed-world optimization facts, ownership validation, and ABI facts live as explicit artifacts instead of being hidden inside one monolithic IR
- lowering contract validation:
  the final pre-MIR validity gate for executable bodies; it proves MIR has the
  typed operation facts, layout facts, call facts, receiver/addressability facts,
  and ownership facts needed to lower directly
- HIR:
  currently a shallow compiler-owned function catalog for lowering orchestration, including lambda bodies and materialized specialized functions
- MIR:
  the first real body IR for accepted executable semantics, good for normalized
  control flow, explicit temporaries, address-based memory operations, runtime
  drop lowering, imported typed template lowering, and control-flow-aware
  ownership checks
- SSA:
  good for phi-aware dataflow, register-style values, and direct LLVM emission
- optimized SSA:
  the post-MIR middle-end artifact used by code generation, after cleanup,
  constant propagation, devirtualization, inlining, value-fact analysis,
  literal specialization, fact-driven branch pruning, alias-aware memory
  optimization, aggregate scalar replacement, and final branch shaping
- SSA validation:
  the pre-LLVM contract check that optimized SSA is well formed, has ABI facts,
  and uses only value, address, dynamic-storage, memory-copy, text, and operator
  shapes the emitter can lower directly
- LLVM IR:
  backend representation only

This keeps LLVM as the backend instead of letting LLVM constraints leak too early into the language frontend.

## Fallback And Invariant Policy

Fallback paths are allowed only where they represent a real program shape:

- FFI declarations and intentionally bodyless declarations may remain
  declaration-only because there is no Stark body to lower.
- ABI-boundary declarations for imported functions may be emitted when the root
  module is not responsible for the body.
- Tests may deliberately construct malformed SSA or legacy emitter fixtures, but
  those fixtures must opt into the legacy fallback behavior explicitly.

Fallback paths are not allowed for accepted source bodies, accepted imported
source bodies, accepted package-image template bodies, accepted materialized
generic bodies, or accepted source asm bodies. For those bodies:

- `unsupported-lowering` and `missing-function-body` logs from MIR lowering are
  promoted to compiler diagnostics by the default pipeline.
- `llvm-body-fallback`, `llvm-asm-fallback`, and `llvm-body-pending` logs from
  LLVM emission are promoted to compiler diagnostics by the default pipeline.
- The correct fix for a new fallback is to move invalidity earlier, complete the
  direct lowering/emission, or turn an unreachable malformed state into a
  compiler invariant with a focused regression test.

The long-term direction is to make invalid states unrepresentable at the MIR
boundary by carrying closed typed operation facts from type checking through
package images, imported templates, monomorphization, and inline clone planning.
Until that model is complete, `validate-lowering-contract` and `validate-ssa`
act as explicit gates so lowerers do not silently become validity filters.

## Why This Pass Structure Works

- It is robust because each stage has a single job and a stable input/output contract.
- It is flexible because new passes can be inserted by dependency rather than by rewriting one giant compilation method.
- It is debuggable because artifacts can be inspected after each pass.
- It keeps language validity in the frontend and lowering correctness in the
  lowerers, so phase ownership stays clear.
- It matches Stark's design direction:
  function semantics, effects, borrowing, and visibility should become compiler facts before LLVM lowering begins.
