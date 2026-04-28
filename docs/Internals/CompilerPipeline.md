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
   Imported declarations are loaded into the same typed world, so qualified Stark calls and type references can flow across modules before LLVM lowering.
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
   This is where borrow escape classes, `law` restrictions, `finite` restrictions, raw-pointer boundary rules, recursive finite-call cycles, parameter memory summaries, and call-memory summaries are checked.
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
17. `lower-hir`
   Produces the current compiler-owned HIR shell.
   Today this is intentionally shallow: it packages root-module functions, source-loaded imported-module functions, non-capturing lambda bodies, and materialized specialized functions that already have typed signatures, body presence, derived effect profiles, and specialization strategy facts so later lowering passes stop depending on declaration-model details.
18. `lower-mir`
   Produces a mid-level IR with explicit locals, basic blocks, typed operands, typed rvalues, and terminators.
   This is where structured source bodies and imported typed template bodies become control-flow-aware CFG form suitable for ownership precision and explicit value lowering, including aggregate field/index operations, slice formation, raw address formation, indirect load/store, constructor/destructor lowering, runtime drop lowering, and explicit conversions.
19. `borrow-liveness`
   Refines ownership validation with non-lexical-style lifetime analysis over normalized MIR.
   This pass computes borrow-local liveness across the CFG and updates the ownership model when a move, overwrite, or return would conflict with still-live borrows. Its phase is semantic, but it intentionally depends on `lower-mir` and runs after MIR exists.
20. `lower-ssa`
   Produces SSA form from MIR, prunes unreachable CFG blocks, and inserts phi nodes where control-flow paths merge.
   This is where mutable Stark locals stop looking like source variables and start looking like compiler values, while addressable locals still surface as explicit allocate/store/lifetime operations across both root-module and source-loaded imported-module functions.
21. `cleanup-ssa`
   Canonicalizes and simplifies SSA before later consumers use it.
   This pass canonicalizes compare/branch shapes, simplifies trivial terminators and single-case switches, normalizes switch-lowering structures, reuses repeated SSA computations when memory ordering allows it, removes unused pure instructions and local storage, collapses trampoline blocks, merges linear blocks, canonicalizes early-return diamonds, and prunes unreachable blocks. At `O0` and `Og`, this pass is bypassed and the original SSA artifact is republished as optimized SSA.
22. `const-prop`
   Runs constant propagation over the cleaned SSA graph.
   This pass folds constant arithmetic, conversions, compares, branches, and simple `switch` decisions, then runs the SSA cleanup optimizer again and republishes the optimized SSA artifact consumed by LLVM emission. At `O0` and `Og`, this pass is also bypassed.
23. `lower-abi`
   Produces a compiler-owned ABI model from typed Stark signatures and function effects.
   This is where internal aggregate parameters and returns are lowered to stable calling-convention rules, while `ffi` signatures keep their foreign-facing shape and imported Stark calls are assigned their dependency-facing symbol/ABI form.
24. `emit-llvm`
   Produces LLVM IR from the optimized SSA form plus semantic, type, and ABI metadata.
   The current emitter generates real function bodies for the supported SSA subset, emits concrete aggregate/array/slice/string layouts, uses parameter memory summaries from semantic validation to emit stronger `readonly`/`writeonly`/`nocapture` facts for root Stark functions, consumes the closed-world optimization model for caller-sensitive law-path specialization decisions, can materialize internal root-side clones of eligible imported law bodies for closed-world law-call optimization with caller-sensitive law-path rewriting, emits imported Stark declarations using the ABI model, qualifies non-FFI symbols for library builds, and falls back to declarations only for still-unsupported bodies.

## Extension Points And Remaining Work

The pass structure is no longer just a skeleton; the current compiler has real
typing, generic instantiation ownership, monomorphization planning,
specialization planning, semantic validation, ownership validation, MIR, SSA,
ABI lowering, package-image loading, and LLVM emission. The pipeline still
leaves clear extension points for future performance and tooling work:

- interprocedural MIR/SSA inlining before LLVM
- cross-block value numbering and redundancy elimination
- scalar replacement of aggregates before ABI lowering
- destination propagation and copy-elision improvements
- deeper proof/range propagation for bounds, enum tags, and non-null facts
- loop optimization before LLVM
- package-aware generic/template inlining and clone pruning
- first-class value tracing and trace-file sinks for compiler observability
- allocator-backed `arena` lowering

These should fit naturally as additional dependency-ordered passes between the
existing semantic, MIR, SSA, ABI, and code-generation boundaries. The important
constraint is not physical placement in one method; it is publishing a typed
artifact at a stable boundary and declaring the exact pass dependencies.

## Current IR Boundary

Stark should not lower directly from the parse tree to LLVM IR.

The current ownership split is:

- parse tree:
  ANTLR-owned syntax structure
- syntax model:
  Stark-owned representation of modules, declarations, and signatures
- typed and semantic models:
  type information, instantiation ownership, monomorphization and specialization plans, enum layout, effect summaries, semantic validation, closed-world optimization facts, ownership validation, and ABI facts live as explicit artifacts instead of being hidden inside one monolithic IR
- HIR:
  currently a shallow compiler-owned function catalog for lowering orchestration, including lambda bodies and materialized specialized functions
- MIR:
  the first real body IR, good for normalized control flow, explicit temporaries, address-based memory operations, runtime drop lowering, imported typed template lowering, and control-flow-aware ownership checks
- SSA:
  good for phi-aware dataflow, register-style values, and direct LLVM emission
- optimized SSA:
  cleanup and constant-propagation result used by code generation
- LLVM IR:
  backend representation only

This keeps LLVM as the backend instead of letting LLVM constraints leak too early into the language frontend.

## Why This Pass Structure Works

- It is robust because each stage has a single job and a stable input/output contract.
- It is flexible because new passes can be inserted by dependency rather than by rewriting one giant compilation method.
- It is debuggable because artifacts can be inspected after each pass.
- It matches Stark's design direction:
  function semantics, effects, borrowing, and visibility should become compiler facts before LLVM lowering begins.
