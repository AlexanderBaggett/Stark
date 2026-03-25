# Compiler Pipeline

## Strategy

Stark should treat parsing as the beginning of compilation, not the compiler architecture.

The compiler pipeline should be organized around explicit passes, typed artifacts, and clear phase boundaries. Each pass should consume a small number of well-defined artifacts and publish a new artifact for the next stage. This keeps the frontend flexible while the language is still evolving and makes it practical to replace or split passes later without rewriting the entire compiler.

The pass system should support:

- dependency-based pass ordering
- a typed artifact store instead of a loose string dictionary
- execution history for debugging and profiling
- fail-fast behavior after diagnostics
- the ability to insert new analysis or lowering passes between existing stages

## Default Stages

The current planned pipeline after parsing is:

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
9. `semantic-validate`
   Validates Stark-specific semantic contracts after typing.
   This is where borrow escape classes, `law` restrictions, `finite` restrictions, raw-pointer boundary rules, and recursive finite-call cycles are checked.
10. `ownership-validate`
   Validates deterministic ownership and lifetime rules after the higher-level semantic checks.
   This is where move tracking, use-after-move rejection, implicit drop scopes, branch-sensitive ownership state, and basic borrow lifetime sources are checked so safe code remains non-GC and leak-resistant by construction.
11. `lower-hir`
   Produces a high-level IR that is still close to the source structure but no longer depends on ANTLR parse-tree APIs.
12. `lower-mir`
   Produces a mid-level IR with explicit locals, basic blocks, typed operands, typed rvalues, and terminators.
   This is where structured source bodies become control-flow-aware CFG form suitable for ownership precision and explicit value lowering.
13. `lower-ssa`
   Produces SSA form from MIR, prunes unreachable CFG blocks, and inserts phi nodes where control-flow paths merge.
   This is where mutable Stark locals stop looking like stack slots and start looking like compiler values.
14. `lower-abi`
   Produces a compiler-owned ABI model from typed Stark signatures and function effects.
   This is where internal aggregate parameters and returns are lowered to stable calling-convention rules, while `ffi` signatures keep their foreign-facing shape and imported Stark calls are assigned their dependency-facing symbol/ABI form.
15. `emit-llvm`
   Produces LLVM IR from the stabilized SSA form and semantic metadata.
   The current emitter generates real register-based function bodies for the supported SSA subset, emits imported Stark declarations using the ABI model, qualifies non-FFI symbols for library builds, and falls back to declarations for unsupported constructs.

## Near-Term Missing Passes

The current pass skeleton leaves room for the next real compiler work:

- doctrine and trait constraint solving
- CFG refinement for switch and pattern matching
- SSA value numbering and cleanup
- constant folding and compile-time evaluation
- concrete LLVM type lowering
- monomorphization or specialization planning

These should fit naturally as additional passes between `symbol-catalog` and `emit-llvm`.

## Recommended IR Boundary

Stark should not lower directly from the parse tree to LLVM IR.

The recommended ownership split is:

- parse tree:
  ANTLR-owned syntax structure
- syntax model:
  Stark-owned representation of modules, declarations, and signatures
- HIR:
  good for name binding, type checking, effect analysis, and borrow analysis
- MIR:
  good for normalized control flow, explicit temporaries, and control-flow-aware lowering
- SSA:
  good for phi-aware dataflow, register-style values, and direct LLVM emission
- LLVM IR:
  backend representation only

This keeps LLVM as the backend instead of letting LLVM constraints leak too early into the language frontend.

## Why This Pass Structure Works

- It is robust because each stage has a single job and a stable input/output contract.
- It is flexible because new passes can be inserted by dependency rather than by rewriting one giant compilation method.
- It is debuggable because artifacts can be inspected after each pass.
- It matches Stark's design direction:
  function semantics, effects, borrowing, and visibility should become compiler facts before LLVM lowering begins.
