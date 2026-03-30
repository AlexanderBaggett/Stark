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

The current default compilation pipeline contains 20 passes and is dependency-ordered rather than hard-coded as one giant method:

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
9. `enum-layout`
   Derives the current compiler-owned enum layout artifact from the typed enum declarations.
   Today this pass selects the `DirectTag` runtime representation used by later semantic checks and LLVM lowering.
10. `semantic-validate`
   Validates Stark-specific semantic contracts after typing.
   This is where borrow escape classes, `law` restrictions, `finite` restrictions, raw-pointer boundary rules, recursive finite-call cycles, parameter memory summaries, and call-memory summaries are checked.
11. `refine-function-effects`
   Refines function guarantees from semantic body analysis after the declaration-driven effect pass.
   This is where plain `fn` bodies that can be proven `law`, `finite`, or `finite law` are upgraded into the effect profiles consumed by HIR lowering, ABI lowering, and LLVM emission, including more precise readonly memory classification for proven laws.
12. `ownership-validate`
   Validates deterministic ownership and lifetime rules after the higher-level semantic checks.
   This is where move tracking, use-after-move rejection, implicit drop scopes, branch-sensitive ownership state, and basic borrow lifetime sources are checked so safe code remains non-GC and leak-resistant by construction.
13. `lower-hir`
   Produces the current compiler-owned HIR shell.
   Today this is intentionally shallow: it packages root-module functions with their typed signatures, body presence, and derived effect profiles so later lowering passes stop depending on declaration-model details.
14. `lower-mir`
   Produces a mid-level IR with explicit locals, basic blocks, typed operands, typed rvalues, and terminators.
   This is where structured source bodies become control-flow-aware CFG form suitable for ownership precision and explicit value lowering, including aggregate field/index operations, slice formation, raw address formation, indirect load/store, and explicit conversions.
15. `borrow-liveness`
   Refines ownership validation with non-lexical-style lifetime analysis over normalized MIR.
   This pass computes borrow-local liveness across the CFG and updates the ownership model when a move, overwrite, or return would conflict with still-live borrows.
16. `lower-ssa`
   Produces SSA form from MIR, prunes unreachable CFG blocks, and inserts phi nodes where control-flow paths merge.
   This is where mutable Stark locals stop looking like source variables and start looking like compiler values, while addressable locals still surface as explicit allocate/store/lifetime operations.
17. `cleanup-ssa`
   Canonicalizes and simplifies SSA before later consumers use it.
   This pass removes trivial copy instructions, collapses identity phi nodes, collapses trampoline blocks, and performs value-numbering-style reuse of repeated SSA computations when memory ordering allows it.
18. `const-prop`
   Runs constant propagation over the cleaned SSA graph.
   This pass folds constant arithmetic, conversions, compares, branches, and simple `switch` decisions and republishes the optimized SSA artifact consumed by LLVM emission.
19. `lower-abi`
   Produces a compiler-owned ABI model from typed Stark signatures and function effects.
   This is where internal aggregate parameters and returns are lowered to stable calling-convention rules, while `ffi` signatures keep their foreign-facing shape and imported Stark calls are assigned their dependency-facing symbol/ABI form.
20. `emit-llvm`
   Produces LLVM IR from the optimized SSA form plus semantic, type, and ABI metadata.
   The current emitter generates real function bodies for the supported SSA subset, emits concrete aggregate/array/slice/string layouts, emits imported Stark declarations using the ABI model, qualifies non-FFI symbols for library builds, and falls back to declarations only for still-unsupported bodies.

## Near-Term Missing Passes

The current pass skeleton still leaves room for the next substantial compiler work:

- doctrine and trait constraint solving
- deeper dead-code elimination beyond the current cleanup/constant-propagation passes
- richer aggregate copy/move lowering
- allocator-backed `heap` and `arena` lowering
- debug-info and source-span propagation through MIR, SSA, and LLVM
- monomorphization or specialization planning

These should fit naturally as additional passes between `symbol-catalog` and `emit-llvm`.

## Current IR Boundary

Stark should not lower directly from the parse tree to LLVM IR.

The current ownership split is:

- parse tree:
  ANTLR-owned syntax structure
- syntax model:
  Stark-owned representation of modules, declarations, and signatures
- typed and semantic models:
  type information, enum layout, effect summaries, semantic validation, ownership validation, and ABI facts live as explicit artifacts instead of being hidden inside one monolithic IR
- HIR:
  currently a shallow compiler-owned function catalog for lowering orchestration
- MIR:
  the first real body IR, good for normalized control flow, explicit temporaries, address-based memory operations, and control-flow-aware lowering
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
