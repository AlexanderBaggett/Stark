# Real-World Compiler Optimization Passes
## Production passes, zero-cost abstraction mechanisms, and rewrite systems across C, Rust, Go, Zig, Nim, Crystal, and GHC

## Executive summary

This document focuses on **optimizations that real production compilers actually run**, not just textbook constant folding.

There are three broad layers worth separating:

1. **Front-end rewriting / lowering / desugaring**  
   Examples: Rust lowering `async`/coroutines to state machines, Go lowering `range` to `for`, Zig `comptime` branch elimination, Nim templates/macros rewriting ASTs, Crystal macros, GHC `RULES`.

2. **Middle-end IR passes**  
   Examples: LLVM `SROA`, `mem2reg`, `GVN`, `LICM`, `SimplifyCFG`, `SCCP`; GCC Tree-SSA passes like PRE, FRE, CCP, VRP, SRA, DSE; Go's SSA passes like `nilcheckelim`, `sccp`, `dse`, `memcombine`, `prove`; Rust MIR passes like `gvn`, `inline`, `dest_prop`, dead-store elimination, drop elision.

3. **Back-end codegen passes**  
   Examples: instruction selection, scheduling, register allocation, late peepholes, post-RA scheduling, vector lowering.

The single biggest pattern behind **real zero-cost abstractions** is:

- specialize / monomorphize,
- inline aggressively where profitable,
- scalarize aggregates into SSA values,
- eliminate dynamic dispatch, checks, and temporary allocations,
- then let the backend schedule/register-allocate clean low-level code.

A useful reality check: **Zig, Nim, and Crystal do not expose nearly as rich a public middle-end pass catalog as LLVM, GCC, Go, or rustc**. In those languages, a lot of the "zero-cost" story comes from **front-end specialization and rewriting**, with the heavy low-level optimization usually delegated to **LLVM** or a **C/C++ backend compiler**.

---

## Scope and framing

When people say "compiler optimizations", they often lump together several very different things:

- **equational rewrites**: `x * 2 -> x + x`, `map f (map g xs) -> map (f . g) xs`
- **IR canonicalization**: normalize code into a form other passes can exploit
- **dataflow reasoning**: prove values/constants/ranges/non-nil facts
- **redundancy elimination**: kill duplicate work and dead work
- **lowering**: turn high-level constructs into lower-level control/data structures
- **backend scheduling/regalloc**: turn optimized IR into good machine code

For this document, I treat all of those as fair game **if they are genuinely used by real compilers**.

---

## Quick taxonomy

| Category | What it does | Representative real-world examples |
|---|---|---|
| Canonicalization / simplification | Rewrites code into easier-to-optimize forms | LLVM `InstCombine`, `SimplifyCFG`; GCC dominator opts / forward prop / reassociation; Go `opt`, `phiopt`, `branchelim`; Rust MIR `instsimplify`, `simplify` |
| Scalarization / SSA promotion | Breaks aggregates and stack slots into scalars/SSA values | LLVM `SROA`, `mem2reg`; GCC `SRA`, `IPA-SRA`; Rust MIR `sroa` |
| Propagation / proving | Constant/range/non-null reasoning | LLVM `SCCP`; GCC CCP/VRP; Go `sccp`, `prove`, nil-check elimination; Rust MIR const propagation / range propagation |
| Redundancy elimination | Removes duplicate loads/computations/stores | LLVM `GVN`; GCC PRE/FRE/DSE; Go CSE/DSE/memcombine; Rust MIR `gvn`, dead-store elimination |
| Control-flow cleanup | Merges blocks, removes dead branches, threads jumps | LLVM `SimplifyCFG`; GCC jump threading / control-dependence DCE / if-conversion; Go `short circuit`, `branchelim`, deadcode; Rust jump threading / unreachable propagation |
| Loop optimization / vectorization | Hoisting, unrolling, vector widening, SLP | LLVM `LICM`, loop vectorizer, SLP vectorizer; GCC loop optimizer, vectorizer, SLP, Graphite; backend-specific vector lowering |
| Interprocedural specialization / devirtualization | Uses callgraph/type info to specialize or direct-call | GCC IPA-CP / IPA-SRA / ICF / speculative devirtualization; Go static + PGO devirtualization; LLVM inliner + devirt-friendly IR |
| Allocation / copy elimination | Avoid heap/temp objects and copies | Go escape analysis, unread-local removal; Nim sink/cursor inference; Rust move/drop cleanup; LLVM scalar replacement enabling stack-to-register promotion |
| High-level abstraction lowering | Removes high-level constructs before machine code | Rust coroutine -> state machine; Go `range` -> `for`; Zig compile-time-known branch elimination; Nim templates/macros/TR macros; Crystal macros; GHC fusion/specialization |
| Backend code quality passes | Scheduling, register allocation, late lowering | GCC RTL scheduling; Go schedule/flagalloc/regalloc; LLVM codegen pipeline |

---

## The languages and compilers at a glance

| Language | Primary production compiler path | Where the optimization "personality" really lives |
|---|---|---|
| **C** | Clang -> LLVM IR -> LLVM passes, or GCC -> GIMPLE/Tree-SSA -> RTL | LLVM or GCC middle-end and backend |
| **Rust** | `rustc` -> MIR -> MIR optimizations -> monomorphization/codegen -> LLVM | Rust MIR passes **plus** LLVM |
| **Go** | `cmd/compile` (`gc`) -> SSA -> machine lowering -> regalloc | Go's own SSA pipeline |
| **Zig** | Zig frontend -> LLVM backend or self-hosted backend | Mostly frontend specialization + backend optimizer |
| **Nim** | Nim frontend -> generated C/C++/ObjC -> backend compiler, or other backends | Nim frontend rewrites + backend C/C++ optimizer |
| **Crystal** | Crystal frontend -> LLVM | Crystal frontend macros/semantics + LLVM |
| **Haskell (GHC)** | Core simplifier / `RULES` / specialization -> later codegen | Rewrite rules, fusion, specialization are first-class |

---

# 1. Production pass catalogs by compiler

## 1.1 LLVM / Clang (real-world C, and also backend for many Rust/Zig/Crystal builds)

### Why LLVM matters here

For **Clang/C**, LLVM is the real optimization engine. LLVM's docs describe optimizations as passes, and the pass reference explicitly notes that the published pass list is not fully authoritative for the modern pass manager; for an exact installed build, `opt -print-passes` and pipeline-printing tools are the right source of truth.

### High-value production LLVM passes

| Pass / mechanism | What it does | Why it matters for zero-cost abstractions |
|---|---|---|
| `InstCombine` | Canonical algebraic and bit-level simplification over instructions | Makes later passes "see through" abstractions and weird source patterns |
| `SimplifyCFG` | Merges blocks, simplifies branches, removes dead/unnecessary PHIs | Cleans up control flow after inlining/lowering |
| `SROA` | Splits aggregate allocas into scalar/member allocas | Turns structs/tuples/temporaries into SSA scalars |
| `mem2reg` | Promotes stack allocas used only by loads/stores into SSA registers | Erases a large class of frontend-generated stack traffic |
| `GVN` | Global value numbering; removes fully/partially redundant computations and loads | Important for abstraction removal after inlining |
| `LICM` | Hoists/sinks loop-invariant work; can promote must-alias memory inside loops | Key loop cleanup and scalar promotion step |
| `SCCP` | Sparse conditional constant propagation | Can fold values and prove blocks unreachable |
| Inliner | Inlines profitable calls | Core enabler for "zero-cost" wrappers and generic helpers |
| Tail call elimination | Converts eligible tail recursion/self-recursion into loops | Removes call overhead in recursive styles |
| Loop vectorizer | Widens loops into vector instructions | Real-world speedups for arithmetic loops |
| SLP vectorizer | Groups independent scalar ops into vector packs | Important for straight-line SIMD opportunities |

### LLVM passes most directly tied to "zero-cost abstraction"

A classic abstraction-erasure path in LLVM-based compilers is:

1. inline wrapper/helper/generic code,
2. run scalar replacement (`SROA`),
3. promote stack temporaries to SSA (`mem2reg`),
4. eliminate redundant computations (`GVN`) and dead branches (`SCCP`, `SimplifyCFG`),
5. optimize loops (`LICM`, vectorizers).

That is exactly why source-level abstractions can disappear so thoroughly in optimized C++/Rust/Zig/Crystal code when the front-end gives LLVM good IR.

### Important nuance

LLVM is a **shared optimizer**, not a language-specific abstraction engine by itself. Languages differ a lot in how much optimization they do **before** LLVM ever sees the program.

---

## 1.2 GCC (real-world C/C++ and others)

GCC has one of the clearest publicly documented production optimization stacks:

- **Tree-SSA / GIMPLE** middle-end passes
- **IPA** (interprocedural analysis / optimization) passes
- **RTL** backend/codegen passes

For "what exactly is enabled at `-O2` or `-O3` on this compiler build?", GCC's own docs point to:

```bash
gcc -Q --help=optimizers
```

### Tree-SSA / GIMPLE passes worth knowing

| Pass / family | What it does | Why it matters |
|---|---|---|
| Dominator opts (`dom`) | Copy/const propagation, expression simplification, jump threading | Early cleanup that exposes structure |
| `forwprop` | Forward propagation / simplification | Normalizes expressions and feeds later elimination |
| `phiopt` | PHI-driven simplification | Cleans CFG/SSA artifacts |
| CCP | Conditional/constant propagation | Proves branches/constants |
| VRP / range propagation | Uses ranges to simplify and remove checks/branches | Essential for branch and bounds-style cleanup |
| PRE / partial redundancy elimination | Eliminates computations redundant on some paths | Real, important workhorse optimization |
| FRE / full redundancy elimination | Removes fully redundant expressions | Same family, simpler case |
| DCE / DSE | Dead code and dead store elimination | Removes useless work after inlining/propagation |
| Reassociation | Reorders expressions to expose common structure | Helps CSE/vectorization/constant folding |
| SRA | Scalar replacement of aggregates | Very important for abstraction cleanup |
| Tail recursion / tail-call opts | Loopification / sibling-call optimization | Removes call overhead |
| If-conversion | Turns branches into predication/select-like forms | Helps vectorization and branch reduction |
| Vectorizer / SLP vectorizer | Loop and basic-block vectorization | Production SIMD |
| Loop opts | Invariant motion, induction-variable normalization, unswitching, peeling/interchange (varies by level) | Major loop performance work |

### IPA passes

| Pass / family | What it does | Why it matters |
|---|---|---|
| IPA constant propagation (`ipa-cp`) | Propagates constants across calls | Enables specialization |
| `ipa-cp-clone` | Clone functions for specialized constant call patterns | One of GCC's clearest real "zero-cost abstraction" tools |
| IPA-SRA | Scalar replacement across call boundaries | Removes aggregate calling overhead |
| ICF | Identical code folding | Deduplicates semantically identical functions |
| Speculative devirtualization | Turn indirect OO-style calls into direct calls when likely/safe | Important for abstraction overhead reduction |
| Whole-program / aggressive inlining | Exposes bodies to all the scalar passes | Multiplies the effect of everything else |

### Backend / RTL

GCC's RTL pipeline includes instruction scheduling and machine-specific late passes; GCC's docs explicitly describe instruction scheduling before and after register allocation, plus modulo scheduling in the relevant stage of the pipeline.

### GCC and zero-cost abstractions

If you want a very practical GCC mental model, it is this:

- inline/specialize at the callgraph level,
- scalarize aggregates (`SRA`, `IPA-SRA`),
- propagate constants/ranges (CCP, VRP),
- eliminate redundant work (PRE/FRE/DSE),
- then schedule/register-allocate well.

That is exactly how real C/C++ abstractions become "just the code you would have written by hand" when they are simple enough and optimization visibility is good.

---

## 1.3 Go (`cmd/compile`, the standard Go compiler)

Go is especially interesting because its production compiler exposes a **real named SSA pass pipeline** in its own source tree.

### Go's own machine-independent SSA pipeline includes passes such as

- early phi/copy elimination
- deadcode
- short-circuit simplification
- `opt`
- zero-arg CSE
- generic CSE
- `phiopt`
- `nilcheckelim`
- `prove`
- `divisible`
- `divmod`
- branch elimination
- `sccp`
- `dse`
- `memcombine`

and later stages include:

- lowering to machine-specific ops
- addressing-mode rewrites
- layout
- scheduling
- late nil-check work
- flag allocation
- register allocation
- loop rotation
- trimming

This is not hypothetical or academic; it is literally how the production compiler is organized.

### Go-specific optimization mechanisms that matter a lot

| Mechanism / pass | What it does | Why it matters |
|---|---|---|
| Escape analysis | Keeps non-escaping values on the stack instead of the heap | Huge for allocation-free abstractions |
| Bounds-check elimination | Removes redundant bounds checks | Necessary for fast slice-heavy code |
| `nilcheckelim` | Removes redundant nil checks, including dominated repeats | Cuts safety-check overhead |
| `prove` | Proves facts used by later simplification/check removal | One of the key reasoning passes in Go SSA |
| `sccp` | Conditional constant propagation | Dead branch cleanup and value simplification |
| `dse` / `memcombine` | Remove useless stores and combine memory ops | Cleans up after lowering/rewriting |
| Static devirtualization | Replaces some interface calls with direct concrete calls | Removes interface dispatch overhead where possible |
| PGO devirtualization/inlining feedback | Uses profiles to devirtualize and inline more effectively | A real production answer to "Go abstractions are not always free" |

### Go's front-end/lowering side also matters

Before and during SSA construction, Go lowers some higher-level constructs:

- the `copy` builtin becomes memmove-like operations
- `range` loops are lowered to ordinary `for` loops

That matters because a lot of "abstraction elimination" actually happens by **changing representation**, not by a single magic optimization pass.

### Go and zero-cost abstractions

Go is not usually marketed as aggressively as Rust/Zig on "zero-cost abstractions", but in practice its production compiler absolutely relies on:

- escape analysis,
- inlining,
- bounds-check elimination,
- nil-check elimination,
- CSE/DSE,
- devirtualization,

to make common idioms cheap.

The honest caveat is that **Go interface dispatch, heap allocation, and safety checks are not automatically free**. They become cheap when the compiler can prove enough.

---

## 1.4 Rust (`rustc` MIR + LLVM)

Rust is the clearest mainstream example of a language that gets "zero-cost abstractions" from **both** a language-owned middle-end and a shared backend.

### The big architectural fact

Rust has **MIR** (Mid-level IR), and the rustc dev guide explicitly says rustc does many optimizations on MIR because MIR is generic and amenable to optimization before LLVM runs. Then LLVM does more.

### Rust mechanisms/passes with real relevance

| Mechanism / pass | What it does | Why it matters |
|---|---|---|
| Monomorphization | Creates concrete copies of generic functions/types for actual type instantiations | The foundational reason many Rust generics cost nothing at runtime |
| MIR inlining | Inlines at MIR level | Exposes more simplification before LLVM |
| MIR `gvn` | Value-numbering style redundancy elimination | Cleans up repeated work |
| MIR `sroa` | Scalar replacement at MIR level | Breaks aggregates apart early |
| `dest_prop` | Propagates assignment destinations backward | Removes temporaries / copy-like artifacts |
| Dead-store elimination | Removes stores that are never observed | Cuts memory traffic |
| `remove_unneeded_drops` | Removes drops that are provably unnecessary | Important for ownership-driven generated code |
| `remove_zsts` | Removes zero-sized-type overhead | Makes phantom/type-level machinery disappear |
| Const/range propagation | Proves values and simplifies branches | Removes control/data overhead |
| Jump threading / unreachable propagation | CFG cleanup based on proven conditions | Cleanup after earlier analysis |
| Coroutine transformation | Lowers coroutines/async into explicit state machines | One of the most concrete examples of zero-cost high-level syntax |

### Rust async/await and coroutine lowering

The `rustc_mir_transform::coroutine` machinery explicitly transforms coroutines into state machines, computes the layout of the coroutine state object, rewrites `return`/`yield`, and builds resume/poll/drop-related entry points. This is exactly the kind of "abstraction removal by lowering" that people mean when they say a high-level feature can be near-zero-cost.

### Rust and zero-cost abstractions

Rust's strongest production zero-cost stack is:

1. monomorphize generics,
2. inline and simplify in MIR,
3. eliminate copies/drops/ZST artifacts,
4. scalarize data,
5. then let LLVM finish the job.

That is why iterators, adapters, wrapper types, and many generic helper layers often disappear into straight-line machine code.

### Important caveat

Not every Rust abstraction is free. Trait objects, heap allocation, atomics, synchronization, and poor inlining visibility all still cost something. The phrase "zero-cost abstractions" means: **you only pay for what remains semantically necessary after specialization and optimization**.

---

## 1.5 Zig

Zig is a little different from Rust/Go/GCC because the most visible optimization story is not a giant public pass catalog. It is more about **language semantics that force or expose specialization**.

### Real Zig mechanisms that matter

| Mechanism | What it does | Why it matters |
|---|---|---|
| `comptime` parameters | Zig's generic mechanism uses compile-time-known parameters | Enables specialization instead of runtime polymorphism |
| Compile-time-known branch elimination | Branches whose conditions are compile-time-known are skipped, and those branches are implicitly inlined | A direct path to abstraction erasure |
| `inline fn` | Semantic inlining at the callsite, not merely a backend hint | Strong guarantee compared with ordinary "please inline" hints |
| Result Location Semantics | Lets construction flow directly into the final destination | Reduces temporaries/copies for aggregates |
| Release modes / backend optimization | Zig build modes choose optimization behavior, and LLVM or self-hosted backends do low-level optimization | Important practical layer |

### Backend reality

Zig has long used LLVM heavily, while also developing self-hosted backends. The key point for this document is:

- Zig absolutely has real abstraction-elimination behavior,
- but much of the low-level pass machinery is either **LLVM's** or still evolving in self-hosted backends,
- so the public "named optimization pass list" is less central than in Go/GCC/rustc.

### Zig and zero-cost abstractions

Zig's strongest production story is:

- push decisions to compile time,
- specialize aggressively,
- inline semantically when requested,
- avoid temporaries through result-location semantics,
- then let the backend optimize normal low-level code.

That is a very real zero-cost strategy, even though it is less "catalog of middle-end passes" and more "language design + backend optimization".

---

## 1.6 Nim

Nim is another language where you have to be careful not to confuse **frontend metaprogramming** with a giant custom optimizer.

### The most important Nim reality

Nim's own documentation is unusually direct: **"Nim has no separate optimizer"** in the sense that it often generates efficient C and then relies on the C compiler's optimizer. Typical Nim compilation produces one or more C/C++/ObjC files and then uses the platform compiler.

That means two things are both true:

1. Nim absolutely can benefit from serious production optimization.
2. A lot of that optimization is really **Clang/GCC/MSVC/etc.**, not a large Nim-owned middle-end.

### Real Nim mechanisms worth documenting

| Mechanism | What it does | Why it matters |
|---|---|---|
| Templates | AST substitution/transformation during semantic processing | Removes abstraction overhead at compile time |
| Macros | Compile-time functions that transform syntax trees | Extremely strong source-level rewriting capability |
| Term rewriting macros/templates | User-defined rewrite system over AST patterns | A real grammar/AST algebraic rewrite mechanism |
| Inline iterators | Always inlined by the Nim compiler | A very direct zero-overhead abstraction feature |
| `.inline.` procs | Emit inline hints for the C backend rather than forcing Nim-side inlining | Useful, but weaker than inline iterators |
| Sink parameters / move semantics | Use control-flow analysis to treat last reads as moves | Eliminates copies in ownership-transfer patterns |
| Cursor inference / copy elision | Infers situations where copies can be avoided | Reduces temporary/move overhead |
| Release / danger / LTO backend modes | Hand more optimization opportunity to backend compiler/linker | Practical performance knobs |

### Nim term rewriting is especially relevant to your request

Nim's experimental/manual docs describe term rewriting macros/templates that match patterns after semantic checking and can express rewrites like:

- `x * 2  ->  x + x`

This is exactly the sort of **production-adjacent algebraic elimination of syntax/tree forms** you asked about. It is not just an academic paper idea; Nim exposes a real user-programmable rewriting mechanism.

### Nim and zero-cost abstractions

Nim's best zero-overhead story usually comes from:

- templates/macros eliminating abstraction before codegen,
- inline iterators,
- sink/cursor-based move/copy elimination,
- then handing the result to an optimized C/C++ backend.

So Nim belongs in this conversation, but the accurate framing is: **frontend rewriting + backend optimization**, not "huge standalone optimizer pipeline".

---

## 1.7 Crystal

Crystal, like Zig, gets much of its low-level optimization from LLVM.

### Real Crystal mechanisms

| Mechanism | What it does | Why it matters |
|---|---|---|
| Macros | Compile-time AST-based metaprogramming | Removes boilerplate and can erase abstraction layers before codegen |
| Compile-time flags | Include/exclude code based on compile-time conditions | Lets dead branches disappear at compile time |
| `-O0` .. `-O3`, `--release` | Crystal's compiler drives LLVM optimization levels/passes | The real low-level optimization engine is LLVM |
| `--single-module` in release-style builds | Gives LLVM better whole-module visibility | Helps inlining and other IPO-style wins |

### Honest framing

Crystal absolutely has real optimization, but the publicly visible "interesting" Crystal-specific side is more about:

- macros,
- compile-time conditions,
- whole-program/module visibility,

while the heavy scalar/vector/callgraph/backend lifting is mostly LLVM.

---

# 2. The production optimizations most responsible for real zero-cost abstractions

This section cuts across compiler brands.

## 2.1 Specialization / monomorphization

### Why it matters
If a generic abstraction is compiled into a **concrete version for concrete types**, then:

- indirection disappears,
- constants/types become known,
- inlining becomes easier,
- downstream scalar passes can see real fields and operations.

### Real examples
- **Rust**: monomorphization of generics.
- **Zig**: `comptime` parameters and compile-time-known conditions.
- **GCC**: IPA constant propagation + clone-based specialization.
- **GHC**: specialization and SpecConstr.
- **Nim**: templates/macros often eliminate abstraction before backend compilation.

### This is one of the main answers to
> "How do real compilers make abstractions actually free?"

They **stop compiling the abstraction as an abstraction**.

---

## 2.2 Inlining

Inlining is not just about removing a function call. Its bigger value is that it exposes:

- constants,
- branch conditions,
- aggregate fields,
- direct callees,
- dead code,
- loop invariants.

That is why inlining is usually the multiplier that makes all the other scalar passes fire.

### Real examples
- LLVM inliner
- GCC inlining / whole-program inlining
- Go inlining (and more aggressive profile-guided choices)
- Rust MIR/LLVM inlining
- Zig `inline fn`
- Nim inline iterators
- GHC `INLINE`/`NOINLINE` orchestration around `RULES`

---

## 2.3 Devirtualization

If a call can be rewritten from:

- interface/virtual/indirect dispatch

to

- a direct call to a known function,

then the compiler can often inline it and optimize the result like ordinary code.

### Real examples
- **GCC**: speculative devirtualization
- **Go**: static devirtualization and PGO devirtualization
- **LLVM-based ecosystems**: devirtualization opportunities usually become more visible after specialization/inlining/LTO

This is a major difference between "abstraction remains expensive" and "abstraction disappears."

---

## 2.4 Scalar replacement and register promotion

A lot of source abstractions look like:

- structs/tuples,
- iterator adapters,
- closures/environments,
- temporary aggregate results.

These become cheap only if the compiler can **break them apart**.

### Real examples
- LLVM `SROA` + `mem2reg`
- GCC `SRA` + `IPA-SRA`
- Rust MIR `sroa`

Once aggregate values become plain SSA scalars, most "object" overhead simply vanishes.

---

## 2.5 Check elimination and proving

Safe languages often insert:

- bounds checks,
- nil/null checks,
- tag/discriminant checks,
- drop/liveness/storage bookkeeping.

These are only cheap if the compiler can prove they are redundant.

### Real examples
- Go bounds-check elimination and `nilcheckelim`
- Go `prove`
- GCC VRP / CCP / control-flow cleanup
- LLVM `SCCP` and CFG simplification
- Rust const/range/unreachable propagation and drop cleanup

---

## 2.6 Allocation and copy elimination

A wrapper abstraction is not zero-cost if it quietly allocates or copies.

### Real examples
- Go escape analysis (stack instead of heap)
- Nim sink parameters / cursor inference / copy elision
- Rust destination propagation, dead-store elimination, unneeded-drop removal
- LLVM scalar replacement exposing values to register promotion

---

## 2.7 High-level lowering instead of "optimization" in the narrow sense

A lot of the best "optimizations" are really **representation changes**.

### Real examples
- Rust coroutines / async lowered to explicit state machines
- Go `range` lowered to `for`
- Zig compile-time-known branches removed before runtime code even exists
- Nim and Crystal macros expand into simpler concrete code
- GHC list fusion removes intermediate list structures through rewrite rules

This is critical because many abstraction costs are removed **before** scalar passes even start.

---

# 3. GHC `RULES`, fusion, and specialization

You specifically asked to include this, and it belongs here.

GHC is one of the strongest real-world examples of **rewrite-driven optimization** in a production compiler.

## 3.1 What `RULES` are

GHC's `RULES` pragma lets programmers or libraries specify source/Core-level rewrite rules that the optimizer may apply, for example classic fusion rules.

This is not just a toy feature. It is used to implement real performance techniques such as:

- fusion / deforestation,
- specialization,
- elimination of intermediate list structures,
- algebraic reassociation of library combinators.

## 3.2 Why `RULES` are important

A lot of "zero-cost functional abstraction" depends on turning code like:

```haskell
map f (map g xs)
```

into a composition that **does not allocate an intermediate list**.

GHC's docs explicitly describe list fusion via `RULES`, and the classic `foldr/build` rule is a canonical example.

## 3.3 `INLINE`, `NOINLINE`, and phase control matter

One subtle but important production detail:

- rewrite rules only fire when the relevant shape is still visible,
- inlining too early can destroy the pattern,
- so GHC provides phase control and interactions with `INLINE` / `NOINLINE` / `CONLIKE`.

That is exactly the kind of detail you only see in a real production optimizer, not a simplified classroom model.

## 3.4 Diagnostics

GHC exposes debugging support such as:

- `-ddump-rule-firings`
- `-ddump-rule-rewrites`
- `-ddump-simpl-stats`

which is further evidence that these rewrites are a real operational part of the optimizer, not just theory.

## 3.5 `SpecConstr`

GHC's `-fspec-constr` (enabled at `-O2`) specializes recursive functions for particular argument shapes. This can remove:

- repeated pattern matching,
- allocation,
- abstraction-layer overhead in recursive higher-level code.

This makes GHC a very strong "real world zero-cost-ish abstraction" example, especially in functional code where fusion/specialization are central.

## 3.6 Why GHC matters to this whole topic

GHC is probably the cleanest production example of this idea:

> The optimizer can remove abstraction overhead by equational rewriting at a semantic IR level, not only by low-level machine-ish optimization.

That is directly relevant to the "algebraic elimination of grammar constructs" angle you mentioned.

---

# 4. Academic and research lines that connect directly to real compiler practice

This section is intentionally separated from the production pass catalogs.

## 4.1 Deforestation / stream fusion

### Production relevance
Very high. This directly influenced real optimizers, especially GHC.

### Core idea
Turn producer/consumer pipelines into a form where intermediate structures disappear.

### Why it matters
This is exactly how you get expressive high-level sequence code without paying for every intermediate container.

### Best real-world link
GHC fusion and stream-fusion-style rewriting.

---

## 4.2 Equality saturation / e-graphs

### Production relevance
More research-heavy, but increasingly practical.

### Core idea
Instead of greedily applying one rewrite at a time, represent many equivalent expressions in an **e-graph**, saturate with rewrite rules, then extract the cheapest version.

### Why it matters
This is one of the strongest modern frameworks for algebraic optimization, DSL compilation, and rewrite-heavy optimization research.

### Honest status
It is highly relevant intellectually, and increasingly used in compilers/optimizers/research tools, but it is **not** the default general-purpose production optimization engine for Clang/GCC/rustc/Go/Zig/Nim/Crystal.

---

## 4.3 User-programmable rewrite systems inside production languages

A useful bridge between theory and production practice is:

- **GHC `RULES`**
- **Nim term rewriting macros/templates**
- **macro systems that erase syntax-level abstraction before lower IR optimization**

These are not full equality-saturation systems, but they are genuine, practical rewrite-driven optimization mechanisms.

---

# 5. Best examples of "real zero-cost abstraction" by language

## C (Clang/GCC)
The abstraction story is mostly:
- inline,
- scalarize,
- propagate constants/ranges,
- eliminate redundant work,
- optimize loops,
- schedule/register-allocate well.

For C, the important answer is the optimizer stack itself: LLVM or GCC.

## Rust
Best-in-class mainstream example for:
- monomorphization,
- MIR cleanup and lowering,
- coroutine lowering,
- drop/copy/ZST cleanup,
- LLVM finishing passes.

## Go
Best examples:
- escape analysis,
- bounds/nil-check elimination,
- devirtualization,
- explicit named SSA passes.

Go gives a very concrete production view of what an optimizer has to do to make a relatively high-level safe language fast.

## Zig
Best examples:
- `comptime`,
- semantic inlining,
- compile-time branch elimination,
- result-location semantics,
- backend optimization.

The core story is language-driven specialization more than a public pass catalog.

## Nim
Best examples:
- templates/macros,
- term rewriting,
- inline iterators,
- sink/cursor inference,
- backend C/C++ optimization.

Very relevant to the AST-rewrite angle.

## Crystal
Best examples:
- macros,
- compile-time flags,
- LLVM optimization levels and module-level visibility.

Again: strong frontend + LLVM, not a huge custom optimizer surface.

## GHC
Best examples:
- `RULES`,
- fusion / deforestation,
- specialization,
- SpecConstr.

Probably the clearest production proof that algebraic rewriting can remove real abstraction overhead.

---

# 6. Practical takeaways

## If you care about real-world optimization passes
Start with these four ecosystems:

1. **LLVM/Clang**
2. **GCC**
3. **Go SSA**
4. **Rust MIR + LLVM**

They have the strongest public documentation of actual production pass machinery.

## If you care specifically about zero-cost abstractions
The most important mechanisms are not "constant folding." They are:

- specialization / monomorphization
- inlining
- devirtualization
- scalar replacement / SSA promotion
- escape analysis / allocation elimination
- redundant check elimination
- rewrite-driven fusion / deforestation
- lowering of high-level constructs to simple state/data machines

## If you care specifically about algebraic elimination / grammar-level rewriting
The best production examples in this set are:

- **GHC `RULES`**
- **Nim term rewriting macros/templates**
- **macro/template systems that erase syntax before codegen**
- **stream fusion / deforestation literature**
- **e-graphs / equality saturation** as the strongest modern research direction

---

# 7. Bottom line

If I had to summarize the landscape in one paragraph:

- **LLVM and GCC** are the classic industrial middle-end/back-end optimizers.
- **Go** is a great case study in a production compiler with an explicit custom SSA pass pipeline.
- **Rust** is the strongest mainstream "zero-cost abstraction" example because it combines language-level monomorphization and MIR cleanup with LLVM.
- **Zig** gets much of its power from compile-time execution/specialization and semantic inlining, then delegates a lot of low-level work to LLVM or newer self-hosted backends.
- **Nim** and **Crystal** are real participants, but the honest framing is frontend rewriting/metaprogramming plus backend optimization, not giant standalone optimizer stacks.
- **GHC `RULES`** is the standout example of production rewrite-based optimization and fusion.
- **Equality saturation / e-graphs** are the most important research line to watch if you want the modern form of large-scale algebraic rewriting.

---

# 8. Reference URLs

## LLVM / Clang
- LLVM passes overview: https://llvm.org/docs/Passes.html
- LLVM vectorizers: https://llvm.org/docs/Vectorizers.html
- `opt` command: https://llvm.org/docs/CommandGuide/opt.html
- Clang command guide: https://clang.llvm.org/docs/CommandGuide/clang.html
- LLVM `InstCombine` docs / API references:
  - https://llvm.org/doxygen/classllvm_1_1InstCombinePass.html
  - https://llvm.org/doxygen/classllvm_1_1InstructionCombiningPass.html
- LLVM inliner docs / API references:
  - https://llvm.org/doxygen/classllvm_1_1InlinerPass.html
  - https://llvm.org/doxygen/classllvm_1_1ModuleInlinerPass.html

## GCC
- Tree-SSA passes: https://gcc.gnu.org/onlinedocs/gccint/Tree-SSA-passes.html
- IPA passes: https://gcc.gnu.org/onlinedocs/gccint/Regular-IPA-passes.html
- Optimize options / `-O` flags: https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html
- RTL passes: https://gcc.gnu.org/onlinedocs/gccint/RTL-passes.html

## Go
- Compiler README / architecture: https://go.dev/src/cmd/compile/README
- SSA pass pipeline: https://go.dev/src/cmd/compile/internal/ssa/compile.go
- Nil-check elimination source: https://go.dev/src/cmd/compile/internal/ssa/nilcheck.go
- Devirtualization source: https://go.dev/src/cmd/compile/internal/devirtualize/devirtualize.go
- GC guide / escape analysis: https://go.dev/doc/gc-guide
- PGO docs: https://go.dev/doc/pgo
- Go 1.22 release notes: https://go.dev/doc/go1.22
- Go 1.14 release notes (BCE / nil-check diagnostics context): https://go.dev/doc/go1.14

## Rust
- rustc dev guide overview: https://rustc-dev-guide.rust-lang.org/overview.html
- rustc monomorphization: https://rustc-dev-guide.rust-lang.org/backend/monomorph.html
- MIR transform crate index: https://doc.rust-lang.org/beta/nightly-rustc/rustc_mir_transform/index.html
- Coroutine transform docs: https://doc.rust-lang.org/stable/nightly-rustc/rustc_mir_transform/coroutine/index.html

## Zig
- Zig language reference: https://ziglang.org/documentation/master/
- Zig build system / optimize modes: https://ziglang.org/learn/build-system/
- Zig 0.15.1 release notes: https://ziglang.org/download/0.15.1/release-notes.html
- Zig 0.10.0 release notes: https://ziglang.org/download/0.10.0/release-notes.html

## Nim
- Nim backends: https://nim-lang.org/docs/backends.html
- Nim compiler docs / backend options: https://nim-lang.org/docs/nimc.html
- Nim tutorial part II (templates): https://nim-lang.org/docs/tut2.html
- Nim tutorial part III (macros): https://nim-lang.org/docs/tut3.html
- Destructors / sink and move optimization: https://nim-lang.org/docs/destructors.html
- Experimental manual / term rewriting: https://nim-lang.org/docs/manual_experimental.html
- Nim manual: https://nim-lang.org/docs/manual.html

## Crystal
- Crystal required libraries / LLVM dependency: https://crystal-lang.org/reference/1.19/man/required_libraries.html
- Crystal macros: https://crystal-lang.org/reference/1.19/syntax_and_semantics/macros/index.html
- Crystal performance guide: https://crystal-lang.org/reference/1.19/guides/performance.html
- Crystal 1.11 release notes (`-O0`..`-O3`): https://crystal-lang.org/2024/01/08/1.11.0-released/
- Crystal compile-time flags: https://crystal-lang.org/reference/1.19/syntax_and_semantics/compile_time_flags.html

## GHC / rewrite rules / fusion
- GHC rewrite rules: https://ghc.gitlab.haskell.org/ghc/doc/users_guide/exts/rewrite_rules.html
- GHC optimization flags / SpecConstr / simplifier options: https://ghc.gitlab.haskell.org/ghc/doc/users_guide/using-optimisation.html
- Stream fusion paper: https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf

## Equality saturation / e-graphs
- `egg`: Fast and Extensible Equality Saturation: https://arxiv.org/abs/2004.03082