# Stark Roadmap

This document is the working implementation roadmap for Stark.

It is intended to track:

- what already exists
- what is partially in place but not finished
- what still needs to be built before Stark feels like a complete language and compiler

The checkboxes below should be updated as work lands.

Roadmap scope is frozen for now.

- do not add new roadmap items or sub-items without an explicit scope-reset decision
- work should come from completing, refining, or verifying the items already listed here

For roadmap items with nested sub-checklists:

- treat the parent item as an umbrella
- only mark the parent complete when the sub-items are complete
- feel free to split further as implementation work reveals a cleaner cut line

## Current Baseline

- [x] Core language design documents exist:
  - `general-idea.md`
  - `BorrowerSystem.md`
  - `ModulesAndVisibility.md`
  - `CompilerPipeline.md`
- [x] ANTLR grammar exists in `Stark.g4`
- [x] .NET parser host exists and builds
- [x] Parser and compiler solution exist in `Stark.slnx`
- [x] Pass-based compiler pipeline exists
- [x] Type checking exists for a meaningful core subset
- [x] Borrow/effect/ownership validation exists
- [x] MIR lowering exists
- [x] SSA lowering exists
- [x] LLVM IR emission exists for a meaningful scalar/control-flow subset
- [x] Native executable emission exists through the LLVM/Clang toolchain
- [x] Stark can compile a native `Hello World`
- [x] Automated test suite exists and is currently green

## Milestone 0: Language Design Foundation

Goal: the language has a coherent written design and a grammar skeleton.

- [x] General language direction documented
- [x] Function kinds documented:
  - `fn`
  - `finite`
  - `law`
  - `finite law`
- [x] Function modifiers documented:
  - `inline`
  - `noinline`
  - `inlinehint`
  - `hot`
  - `cold`
  - `ffi`
- [x] Borrower system documented
- [x] Modules and visibility documented
- [x] FFI/raw-pointer/null boundary documented
- [x] Grammar file created in ANTLR format
- [x] Finalize every remaining syntax decision that is still currently inferred or C#-style by fallback
- [x] Consolidate all language design decisions into a tighter language reference

## Milestone 1: Frontend and Semantic Core

Goal: Stark source can be parsed, modeled, checked, and validated before lowering.

### Parsing and Syntax

- [x] ANTLR lexer and parser generation
- [x] Compiler-owned syntax model
- [x] Declaration indexing
- [x] Basic parser test coverage
- [x] Expand grammar coverage tests toward full language conformance

### Modules and Names

- [x] Module declaration parsing
- [x] Import parsing
- [x] Visibility parsing for `internal`, `public`, `export`
- [x] Module graph pass
- [x] Symbol catalog pass
- [x] Basic unresolved-import diagnostics
- [x] Real source-loading module resolver
- [x] Cross-module symbol loading
- [x] Imported function/type/global binding
- [x] Re-export handling in the compiler, not just in docs

### Types and Typing

- [x] Builtin type resolution
- [x] Named type registration
- [x] Struct/record field shape collection
- [x] Function signature typing
- [x] Literal typing
- [x] Global declaration checking
- [x] Core expression typing
- [x] Basic switch pattern typing
- [x] Full type checking for all grammar-supported expression forms
  - [x] constructor and `new` argument checking against declared type shape
  - [x] full validation for object and array initializer typing edge cases
  - [x] remaining postfix/member-call typing paths
  - [x] explicit arithmetic operator typing without placeholder diagnostics
  - [x] regression tests covering each expression grammar family
- [x] Better diagnostics for type mismatches and coercions
  - [x] expected/actual type wording for assignment and return mismatches
  - [x] argument-position diagnostics for call-site mismatches
  - [x] member/index context in expression diagnostics
  - [x] coercion diagnostics that explain whether an explicit conversion is required


### Function Semantics and Validation

- [x] Function-effect derivation
- [x] `law` restrictions
- [x] `finite` restrictions
- [x] `ffi` effect boundary handling
- [x] `nounwind`/`mustprogress`/`willreturn` style derivation
- [x] More complete derivation of parameter-level guarantees
  - [x] derive `nonnull` from safe borrow and raw-pointer rules where valid
  - [x] derive readonly/writeonly behavior from qualifiers and usage
  - [x] derive `noalias`-style facts from ownership and borrow escape classes
  - [x] derive alignment and dereferenceability facts from concrete type layout
- [x] More precise per-call memory effect modeling
  - [x] track read/write/capture effects per argument
  - [x] summarize callee memory behavior in the semantic model
  - [x] propagate callee summaries into call-site validation
  - [x] feed refined call effects into LLVM attribute emission

### Borrowing and Ownership

- [x] Borrow escape-class validation
- [x] Raw-pointer boundary validation
- [x] Non-null safe-reference model
- [x] Ownership validation pass
- [x] Move tracking
- [x] Use-after-move rejection
- [x] Implicit drop tracking
- [x] Branch-sensitive ownership merging
- [x] Basic borrow lifetime source tracking
- [x] More precise non-lexical-style lifetime analysis on normalized MIR/SSA
  - [x] compute last-use information on normalized MIR or SSA
  - [x] shrink borrow live ranges below lexical block scope when provable
  - [x] allow reuse after proven last use instead of lexical scope end
  - [x] add branch and loop lifetime regression tests
- [x] Drop checking for richer aggregate types
  - [x] track partial initialization of aggregate locals
  - [x] track field-wise move state inside aggregates
  - [x] merge partially moved aggregate state across branches
  - [x] diagnose drops of not-fully-initialized aggregate values
- [x] Generic `frozen` alias and projection semantics
  - [x] field, index, and slice projections derived from `frozen` values remain `frozen`/readonly views
  - [x] address-of on data reached through `frozen` values preserves frozen provenance
  - [x] raw aliases derived from `frozen` values cannot be upgraded into mutable-capable aliases
  - [x] pointer and integer conversions preserve frozen provenance or are rejected
  - [x] regression tests for frozen-alias escape hatches outside `const` globals
- [x] Better diagnostics for borrow conflicts and lifetime errors
  - [x] point back to both the borrow source and the conflicting use
  - [x] distinguish move, alias, escape, and lifetime-end failures clearly
  - [x] explain return-path and parameter-path escape failures explicitly
  - [x] add regression tests for each major borrow diagnostic family

## Milestone 2: IR Pipeline and Native Hello World

Goal: Stark lowers through multiple IR stages and can produce native executables for a useful subset.

### IR Stages

- [x] High-level IR stage
- [x] Mid-level IR stage
- [x] MIR basic blocks
- [x] MIR typed operands and rvalues
- [x] SSA lowering
- [x] Phi node insertion
- [x] Unreachable block pruning
- [x] SSA cleanup / canonicalization pass
  - [x] remove trivial copies and identity phi nodes
  - [x] collapse empty or trampoline blocks
  - [x] canonicalize compare-and-branch shapes
  - [x] rerun unreachable-block pruning after cleanup
- [x] Value numbering / common subexpression cleanup
  - [x] local value numbering within a basic block
  - [x] deduplicate repeated arithmetic and comparison expressions
  - [x] reuse identical materialized constants and temporaries
  - [x] add regression tests for eliminated duplicate work
- [x] Constant propagation pass
  - [x] implement a scalar constant lattice over SSA values
  - [x] fold branches and switches with constant conditions
  - [x] replace known constant expressions and values
  - [x] prune dead blocks and instructions after propagation

### Control Flow Lowering

- [x] `if` lowering
- [x] `while` lowering
- [x] `for` lowering
- [x] `break` / `continue`
- [x] `return`
- [x] literal `switch` lowering
- [x] guarded `switch` lowering with `when`
- [x] `case var capture` lowering
- [x] More advanced pattern lowering strategy
  - [x] normalize multi-label `switch` sections before lowering
  - [x] make guard ordering explicit in the lowered control-flow shape
  - [x] lower more complex decision trees without ad-hoc fallthrough handling
  - [x] materialize capture bindings only after a case is selected

### Expression Lowering

- [x] integer literals
- [x] float literals
- [x] bool literals
- [x] string literals
- [x] null literals in raw-pointer contexts
- [x] direct calls
- [x] arithmetic operators for the current scalar subset
- [x] comparisons for the current scalar subset
- [x] short-circuit `&&`
- [x] short-circuit `||`
- [x] ternary conditional `?:`
- [x] character literal lowering
- [x] field access lowering
- [x] index access lowering
- [x] member call lowering
  - [x] lower receiver evaluation order explicitly
  - [x] lower value-receiver method calls
  - [x] lower borrow/reference receiver calls
  - [x] add ABI and codegen tests for receiver passing
- [x] pointer/address operations beyond the current subset
  - [x] raw address-of for locals, fields, and globals
  - [x] element and field address formation as first-class lowered values
  - [x] raw load/store through pointer values
  - [x] explicit null/raw boundary conversions that the language permits
- [x] complete conversion lowering
  - [x] integer widening and narrowing conversions
  - [x] integer/float conversion paths
  - [x] raw-pointer conversion paths
  - [x] array/slice/string view conversions that are semantically allowed

### LLVM IR and Toolchain

- [x] LLVM IR module emission
- [x] LLVM function declaration emission
- [x] LLVM function definition emission for supported bodies
- [x] SSA-based register-style LLVM output
- [x] LLVM string constant emission
- [x] native executable emission via Clang
- [x] `--check`
- [x] `--emit-mir`
- [x] `--emit-ssa`
- [x] `--emit-llvm`
- [x] `--emit-obj`
- [x] `--emit-exe`
- [x] native `Hello World` path
- [x] target triple detection
- [x] target data layout emission
- [x] object-file and link-step configurability
  - [x] linker and archiver path overrides
  - [x] explicit compile-only vs link-only command modes
  - [x] pass-through link arguments and library search paths
  - [x] option to preserve intermediate object and LLVM files

## Milestone 3: Real Data Model and ABI

Goal: Stark stops treating most non-scalar language values as opaque pointers and gains a real runtime data layout story.

This is the most important remaining compiler milestone.

### LLVM Type Lowering

- [x] Concrete LLVM lowering for `struct`
- [x] Concrete LLVM lowering for `record`
- [x] Concrete LLVM lowering for fixed arrays
- [x] Concrete LLVM lowering for slices
- [x] Concrete LLVM lowering for strings
- [x] Concrete LLVM lowering for named aggregate types
- [x] Stop mapping most named/aggregate types directly to `ptr`
  - [x] by-value aggregate locals in MIR and SSA where legal
  - [x] aggregate call arguments without implicit pointer fallback
  - [x] aggregate return values without implicit pointer fallback
  - [x] aggregate temporaries and phi nodes without pointer-only lowering

### Memory and Access Lowering

- [x] Field load lowering
- [x] Field store lowering
- [x] Address-of / element-address lowering
- [x] Array indexing lowering
- [x] Slice indexing lowering
- [x] Aggregate local allocation/lifetime strategy
  - [x] choose stack-slot vs scalarized lowering per aggregate shape
  - [x] emit LLVM lifetime markers when valid
  - [x] preserve stable addresses for borrowed aggregate locals
  - [x] add copy-elision rules for aggregate temporaries
- [x] Aggregate copy/move semantics in lowering
  - [x] distinguish trivial bitwise copy from semantic move
  - [x] lower small fixed-size aggregate copies efficiently
  - [x] lower large copies with memcpy-style helpers when appropriate
  - [x] invalidate moved-from aggregate places in lowering state

### ABI and Calling Convention

- [x] Internal `fastcc` usage for non-FFI functions
- [x] `ffi` disables the default internal calling convention
- [x] Real ABI lowering for aggregates
- [x] Stable lowering rules for return-by-value vs indirect return
- [x] Parameter ABI rules for slices/strings/aggregates
- [x] Calling convention strategy across executable vs library boundaries

### Globals and Constants

- [x] String global emission for supported literals
- [x] Three-class global binding model
  - [x] parse and represent `const`, bare, and `mut` globals distinctly
  - [x] enforce immutable-binding vs mutable-rebinding rules
  - [x] define `const` as a fully frozen reachable object graph
  - [x] diagnostics that distinguish illegal rebinding from illegal mutation
- [ ] Deep freeze alias semantics for `const` globals
  - [ ] projections from `const` graphs behave as frozen/readonly values, not merely root-guarded globals
  - [x] safe code cannot strengthen const-derived raw aliases into `rawmutptr`
  - [x] safe code cannot erase const-derived readonly raw alias provenance through integer conversions
  - [x] regression tests for `const` escape hatches through explicit conversions
- [x] Real fully frozen `const` global emission
  - [x] scalar frozen globals
  - [x] array and slice frozen globals
  - [x] struct and record frozen globals
  - [x] nested frozen object graphs
  - [x] correct `constant` and `unnamed_addr` style flags where valid
- [x] Real immutable global binding emission
  - [x] plain immutable scalar/value globals
  - [x] immutable globals pointing at mutable heap objects
  - [x] aggregate immutable bindings with stable addresses
  - [x] tests for immutable global load/address lowering
- [ ] Real mutable global rebinding emission
  - [x] zero-initialized mutable globals
  - [x] scalar-initialized mutable globals
  - [x] aggregate-initialized mutable globals
  - [x] tests for mutable global load/store lowering
- [ ] Better linkage/visibility lowering for globals
  - [x] module-private/internal/public/export mapping for globals
  - [x] linkage defaults for frozen vs immutable-binding vs mutable-rebinding globals
  - [x] package-boundary behavior for manifest-backed libraries
  - [x] regression tests over emitted LLVM/global symbol visibility
- [x] Frozen aggregate initializers for `const` globals
  - [x] array literal constants
  - [x] nested aggregate constants
  - [x] struct and record constants
  - [x] folding aggregate literals into frozen global initializers

## Milestone 4: Modules, Standard Library, and Runtime Surface

Goal: Stark can build real multi-file programs and expose a small but useful standard environment.

### Modules and Imports

- [x] Load imported modules from source files
- [x] Compile module graphs, not just a single input file
- [x] Resolve imported declarations into the type/effect/ownership passes
- [x] Support cross-module calls and type references end-to-end
- [x] Support package/library build boundaries
- [x] Manifest-backed library import resolution without source files
- [x] Package search paths beyond the local module directory
- [x] Standard library packaging as a manifest-backed Stark package

### Comparison Chains and Richer `switch`

- [x] Chained comparison support
  - [x] type-check comparison chains as adjacent pairwise comparisons
  - [x] define and preserve left-to-right single-evaluation semantics for shared operands
  - [x] lower chained comparisons into short-circuit boolean form without re-evaluating shared operands
  - [x] add regression tests for mixed comparison operators, side-effect ordering, and floating-point edge cases
- [x] Richer `switch` support with a performance-bounded subset
  - [x] define the first supported text-switch subset for `ascii` and/or `unicode` scrutinees with literal cases
  - [x] lower small text literal sets to ordered equality checks with normal short-circuit control flow
  - [x] investigate larger literal-set lowering via length partitioning, byte/code-unit decision trees, or generated trie-like dispatch
  - [x] decide whether richer non-text values beyond the scalar/raw-pointer subset belong in `switch` at all
  - [x] keep unsupported rich-switch shapes as explicit diagnostics rather than MIR fallback

### Slice Views And Array Initializers

- [x] Reject slice-typed `{ ... }` initializers
  - [x] define `{ ... }` as an array initializer only, not a slice literal
  - [x] diagnose `T[] x = { ... }` and similar slice-target forms as compile-time errors
  - [x] preserve explicit fixed-array-to-slice view formation as the supported path
  - [x] update tests and examples to use explicit backing storage before forming slices


### Runtime Surface

- [ ] Define program entrypoint conventions beyond raw `export ffi fn main`
  - [ ] hosted entrypoint rules around `main`
  - [ ] freestanding entrypoint form, if supported
  - [ ] argument and environment exposure model
  - [ ] startup/shutdown responsibility split between toolchain and stdlib
- [ ] Panic/assert/failure story
  - [ ] source-level `assert` / `panic` surface
  - [ ] lowering to trap-or-abort with no unwinding
  - [ ] hosted diagnostic message behavior, if any
  - [ ] regression tests for noreturn and unreachable behavior
- [ ] Exit code/runtime termination helpers
  - [ ] stdlib exit API surface
  - [ ] mapping between `main` return values and process exit codes
  - [ ] early termination path in hosted mode
- [ ] Minimal allocator/runtime boundary if heap allocation is exposed
  - [ ] default `heap` allocator ABI
  - [ ] `arena` allocator ABI and lexical lifetime rules
  - [ ] alignment and size contract for allocator calls
  - [ ] out-of-memory failure contract
- [ ] FFI support library or conventions for common C interop
  - [ ] C string and buffer helper types
  - [ ] symbol naming and header-generation guidance
  - [ ] common integer and pointer-width interop examples
  - [ ] package story for reusable interop shims

## Milestone 5: Core Language Completion

Goal: the language feels broadly usable, not just impressive on a narrow subset.

### Statements and Expressions

- [x] `if`
- [x] `while`
- [x] `for`
- [x] `switch` for the currently supported subset
- [x] return statements
- [x] local declarations
- [x] assignments
- [x] object creation lowering
  - [x] lower `new T()` without initializers
  - [x] lower constructor argument evaluation order
  - [x] lower storage-class-aware allocation choice
  - [x] tests for local and returned object creation
- [x] object initializer lowering
  - [x] lower field writes in source order
  - [x] lower nested object and array initializers
  - [x] lower constructor-plus-initializer combinations
  - [x] tests for initializer evaluation order and result placement
- [x] richer assignment target lowering
  - [x] field assignment targets
  - [x] index assignment targets
  - [x] compound assignment on field and index targets
  - [x] nested lvalue chain lowering
- [x] full postfix/operator coverage
  - [x] wrapping and saturating arithmetic lowering
  - [x] remaining postfix chain lowering across mixed call/member/index paths
  - [x] remaining bitwise and shift edge cases
  - [x] precedence and associativity regression coverage

### Types and Patterns

- [x] literal switch patterns
- [x] discard/match-all switch patterns
- [x] `var` capture switch patterns
- [ ] richer sum/variant pattern matching
  - [ ] define a concrete discriminated runtime representation
  - [ ] model variant constructors and payloads
  - [ ] lower discriminant tests and payload extraction
  - [ ] exhaustiveness and unreachable-arm diagnostics
- [ ] first-class optional/result/sum-type lowering if those are part of the surface language plan
  - [ ] define standard library or surface forms for optional/result values
  - [ ] define layout and ABI rules
  - [ ] integrate with `switch` and pattern binding
  - [ ] define FFI behavior for these types, if allowed

### Characters and Strings

- [x] string literal typing
- [x] string constant codegen for the supported path
- [x] character literal lowering
  - [x] ascii character lowering
  - [x] unicode character lowering
  - [x] regression tests for typing and codegen width rules
- [ ] richer escape support and validation
  - [ ] simple escapes
  - [ ] hex and unicode escapes
  - [ ] invalid escape diagnostics
  - [ ] tests for escape parsing and typing
- [ ] full `ascii` / `unicode` runtime/value model
  - [ ] concrete layout for `ascii` and `unicode`
  - [ ] indexing and slicing semantics
  - [ ] literal storage and encoding guarantees
  - [ ] stdlib helpers that depend on the text layout model

  

## Milestone 6: Traits and Doctrine

Goal: Stark's abstraction system becomes real, optimizable, and usable.

- [ ] Finalize doctrine semantics in the compiler
  - [ ] doctrine declaration and type model
  - [ ] effect and purity validation for doctrine members
  - [ ] no-state and no-capture enforcement
  - [ ] doctrine lookup and name-resolution rules
- [ ] Trait/doctrine constraint solving
  - [ ] collect obligations from generic and doctrine use sites
  - [ ] candidate lookup and matching
  - [ ] ambiguity and no-solution diagnostics
  - [ ] instantiate solved obligations into lowered calls
- [ ] Closed-world optimization rules for doctrines/traits
  - [ ] sealed-by-default assumption rules
  - [ ] devirtualization eligibility rules
  - [ ] monomorphization vs shared-code rules
  - [ ] specialization selection order
- [ ] Dynamic dispatch strategy, if any
  - [ ] decide whether trait objects or equivalent runtime dispatch exist
  - [ ] define object-safety or equivalent restrictions if they do
  - [ ] define runtime representation and ABI if they do
  - [ ] tests for dynamic dispatch lowering if supported
- [ ] Better lowering for laws/doctrines as optimization-friendly abstractions
  - [ ] lower doctrine calls to direct calls where possible
  - [ ] emit stronger readonly/noalias/capture facts
  - [ ] specialize and inline law calls where closed-world facts allow it
  - [ ] regression tests for emitted LLVM attributes and call shapes


  ### Standard Library Core

- [x] Define the first standard library module layout
- [x] `Console` or `Stdout` output abstraction
- [x] `Stderr` output abstraction
- [ ] File read API
  - [ ] bytes read-all API
  - [ ] text read-all API for Stark string types
  - [ ] handle-based read API if handles are exposed
  - [ ] tests through stdlib package import paths
- [ ] File write API
  - [ ] bytes write-all API
  - [ ] text write-all API for Stark string types
  - [ ] overwrite vs append mode selection
  - [ ] flush/close semantics if handles are exposed
- [ ] Basic path/file error modeling
  - [ ] canonical file/path error value cases
  - [ ] result-style IO return shapes
  - [ ] platform failure translation into Stark error values
  - [ ] tests and docs for file/path failure behavior
- [ ] String helpers required by the standard library
  - [ ] length and emptiness helpers
  - [ ] string slicing helpers required by IO APIs
  - [ ] basic search helpers required by stdlib code
  - [ ] ascii/unicode conversion helpers if the surface supports them


## Milestone 7: Optimization and Backend Quality

Goal: emitted LLVM becomes richer, more correct, and more competitive.

### Frontend Optimization Passes

- [ ] Constant folding
  - [ ] fold scalar arithmetic and comparison expressions
  - [ ] fold boolean and branch conditions
  - [ ] fold simple aggregate/initializer constants where safe
  - [ ] regression tests for folded MIR and SSA
- [ ] Compile-time evaluation
  - [ ] evaluator for pure literal and arithmetic expressions
  - [ ] evaluator for `law` calls with constant inputs where legal
  - [ ] diagnostics for non-evaluable expressions in constant-required contexts
  - [ ] regression tests for compile-time evaluation results
- [ ] Dead code elimination before LLVM emission
  - [ ] remove unreachable blocks after simplification passes
  - [ ] remove unused SSA instructions and temporaries
  - [ ] remove unused allocas and locals where safe
  - [ ] regression tests for removed dead code
- [ ] Better SSA cleanup/value numbering
  - [ ] canonicalize identical commutative expressions
  - [ ] remove redundant casts and materializations
  - [ ] coalesce equivalent phi nodes
  - [ ] rerun branch and block simplification after cleanup
- [ ] Simplify trivial branches and blocks
  - [ ] fold branch-on-constant
  - [ ] merge blocks with single predecessor/single successor
  - [ ] remove empty jump-only blocks
  - [ ] simplify trivial single-case or default-only switches
- [ ] Normalize more control-flow patterns before LLVM
  - [ ] canonical loop header/latch forms
  - [ ] normalized `switch` lowering structure
  - [ ] canonical early-return diamonds
  - [ ] regression tests over normalized MIR and SSA

### LLVM Semantic Richness

- [x] function-level `nounwind`/`nofree`/`nosync`/`willreturn`/`mustprogress` style emission for the supported cases
- [x] `hot` / `cold` / inline preference emission
- [x] parameter-level `noalias`
- [x] parameter-level `readonly` / `writeonly`
- [x] parameter-level `nonnull`
- [x] parameter-level `align`
- [x] parameter-level `dereferenceable`
- [ ] `captures(...)` style escape-derived lowering
- [ ] better `memory(...)` precision

### Targeting and Code Generation Quality

- [x] native LLVM `switch` for simple integer/bool cases
- [ ] better aggregate lowering quality
  - [ ] small aggregate scalarization heuristics
  - [ ] memcpy vs field-store heuristics
  - [ ] sret/byval tuning
  - [ ] aggregate call/return regression benchmarks
- [ ] better global data lowering quality
  - [ ] merge identical constants where legal
  - [ ] improve `unnamed_addr`, section, and alignment choices
  - [ ] reduce redundant helper globals
  - [ ] tests for cleaner emitted LLVM global sections
- [ ] target-aware code generation options
  - [ ] CLI target triple override
  - [ ] CPU and feature-string forwarding
  - [ ] relocation/PIC/code-model controls if exposed
  - [ ] tests for target option plumbing
- [ ] object emission and separate link steps
  - [ ] keep intermediate object files
  - [ ] compile-only vs link-only CLI modes
  - [ ] explicit linker and archiver selection
  - [ ] multi-object link orchestration tests
- [ ] optimization level controls
  - [ ] CLI optimization-level surface
  - [ ] pipeline behavior per optimization level
  - [ ] native toolchain flag forwarding
  - [ ] tests that optimization settings change tool invocation/output
- [ ] debug info emission
  - [ ] carry source spans through MIR and SSA
  - [ ] emit line-table debug info
  - [ ] emit local variable debug info where feasible
  - [ ] tests that debug metadata is present in LLVM output

## Milestone 8: Tooling, Diagnostics, and Developer Experience

Goal: Stark is pleasant to work on and pleasant to use.

### Tests

- [x] unit tests for parser/compiler pieces
- [x] IR/codegen regression tests
- [x] native hello-world validation path
- [x] broader grammar coverage tests
  - [x] valid parser conformance cases beyond smoke tests
  - [x] negative parser conformance cases beyond smoke tests
  - [x] parser edge-case and fuzz-style corpus
- [x] multi-file integration tests
  - [x] source-import graph integration tests
  - [x] manifest-backed package import integration tests
  - [x] re-export and visibility integration tests
- [ ] compile-and-run program suite
  - [ ] arithmetic and control-flow sample programs
  - [x] multi-module sample programs
  - [x] stdlib and native executable sample programs
- [x] diagnostics regression tests
  - [x] syntax diagnostic snapshots
  - [x] type-check diagnostic snapshots
  - [x] borrow/effect/ownership diagnostic snapshots
- [x] negative tests for borrow and ownership edge cases
  - [x] branch-merge ownership failures
  - [x] aggregate move and partial-move failures
  - [x] returned-borrow lifetime failures

### Compiler UX

- [x] CLI input support
- [x] `check`-only mode
- [x] `--emit-mir`
- [x] `--emit-ssa`
- [x] `--emit-llvm`
- [x] `--emit-obj`
- [x] `--emit-exe`
- [ ] better help/usage output
  - [x] group options by workflow in help output
  - [ ] document emit-mode defaults and examples
  - [x] document package/stdlib/native toolchain options
- [ ] structured diagnostic formatting
  - [ ] stable machine-readable diagnostic shape
  - [ ] grouped notes and related spans
  - [ ] summary/error-count formatting for CLI output
- [ ] source snippets in diagnostics
  - [ ] single primary-span source rendering
  - [ ] secondary-span rendering
  - [ ] underline/caret formatting with tabs and multiline spans

### Repository and Documentation

- [x] top-level design docs exist
- [x] grammar exists in source control
- [x] minimal getting-started guide in `README.md`
- [ ] roadmap kept up to date as work lands
- [x] standard library roadmap expanded in `StandardLibrary.md`
  - [x] module-by-module stdlib surface plan
  - [x] runtime/allocator/IO dependency map
  - [x] tests and packaging plan per stdlib area
- [x] examples directory with canonical Stark programs
  - [x] hello-world example
  - [x] multi-module example
  - [x] struct/record/object-initializer example
  - [x] static-library or FFI example

## Milestone 9: Release Readiness

Goal: Stark is not just a compiler experiment, but a coherent language/toolchain release.

- [ ] Define a minimum viable Stark language subset for a first release
  - [ ] feature inclusion matrix
  - [ ] platform and toolchain support matrix
  - [ ] cut line between release blockers and post-release features
- [ ] Freeze syntax for that subset
  - [ ] grammar audit against the language reference
  - [ ] parser regression lock for accepted and rejected syntax
  - [ ] remove temporary compatibility aliases and placeholders
- [ ] Freeze lowering rules for that subset
  - [ ] ABI and lowering document per supported type family
  - [ ] emitted LLVM/object invariants for supported constructs
  - [ ] regression tests keyed to the frozen lowering contract
- [ ] Document unsupported features explicitly
  - [ ] user-facing unsupported features list
  - [ ] stable diagnostic behavior for unsupported paths
  - [ ] README and release-note pointers to unsupported areas
- [ ] Provide a versioned standard library baseline
  - [ ] package versioning scheme
  - [ ] baseline module list
  - [ ] compatibility promise for shipped APIs
- [ ] Add release notes / changelog discipline
  - [ ] changelog template
  - [ ] release tagging and version-numbering process
  - [ ] upgrade-notes section for breaking changes
- [ ] Add CI for build and tests
  - [x] Linux build/test workflow
  - [x] parser-regeneration drift check
  - [ ] test artifact or failure-log upload
- [ ] Add sample projects that compile end-to-end
  - [ ] hello-world application
  - [ ] multi-module application
  - [ ] static-library plus consumer sample

## Milestone v1.1: Post-Release Surface Additions

Goal: add non-essential language surface after the first release without slowing v1.0 completion.

- [ ] Type aliases
  - [ ] grammar and syntax-model support
  - [ ] semantic identity and ABI rules
  - [ ] visibility and export behavior
- [ ] Generic function/type instantiation strategy
  - [ ] function instantiation triggers
  - [ ] type instantiation triggers
  - [ ] cross-module instantiation ownership
  - [ ] caching and deduplication of instantiations
  - [ ] Generic type parameter handling beyond basic shape support in `v1.1`
  - [ ] bind generic parameters on all declaration kinds that support them
  - [ ] substitute generic parameters through fields, returns, and locals
  - [ ] bind and validate `where` constraints semantically
  - [ ] instantiate generic functions and types at use sites
  - [ ] define specialization interaction with generic instantiation
- [ ] Monomorphization planning
  - [ ] symbol naming scheme
  - [ ] code-size control heuristics
  - [ ] linkage and dedup rules across objects and packages
- [ ] Specialization planning
  - [ ] overlap and priority rules
  - [ ] coherence and ambiguity diagnostics
  - [ ] specialization-driven codegen strategy

## Suggested Near-Term Execution Order

If the goal is to make Stark feel substantially more complete as a language, the recommended next order is:

- [ ] Real type/layout/ABI lowering
- [ ] Field/index/object lowering on top of that
- [x] Real multi-file module loading and imported symbol binding
- [ ] Minimal standard library and runtime surface
- [ ] Doctrine/trait solving and optimization
- [ ] Optimization and backend quality passes
- [ ] Tooling, diagnostics, and release hardening

## Definition of "Compiler Feels Complete"

The compiler should be considered broadly complete only when all of the following are true:

- [x] Stark can compile multi-file programs with imports
- [ ] Stark can lower real aggregates, not just scalar/pointer placeholders
- [ ] Stark can compile everyday code using fields, indexing, strings, and standard library APIs
- [ ] Borrowing and ownership guarantees hold across those features
- [ ] LLVM emission does not routinely fall back to declarations for common language constructs
- [ ] The toolchain can produce native binaries reliably across normal workflows
- [ ] The standard library is sufficient for basic command-line applications
- [ ] Diagnostics and tests are strong enough that refactoring the compiler is safe


Everything before this point is frozen
---------------------------------------------------

## Milestone v1.2: Expand Standard Library

### Expand Standard Library Definition For Full IO/Threading/Collections/TCP/HTTP

- [ ] Define the public module layout for `System.IO`
- [ ] Define the public module layout for `System.FileSystem`
- [ ] Define the public module layout for `System.Threading`
- [ ] Define the public module layout for `System.Collections`
- [ ] Define the public module layout for `System.Net.Tcp`
- [ ] Define the public module layout for `System.Net.Http`
- [ ] Define the error/result model used by stdlib APIs that can fail
- [ ] Define the allocator contract used by collections, buffering, and text-building APIs
- [ ] Define the text and buffer model used by IO and networking APIs
- [ ] Define ownership and borrowing rules for file handles, sockets, iterators, and collection views
- [ ] Decide the blocking and non-blocking API story for TCP and HTTP
- [ ] Define the minimum thread primitives Stark ships in `v1.2`
- [ ] Define the initial collection set Stark ships in `v1.2`
- [ ] Add packaged-consumption tests for every new public stdlib module family
- [ ] Add reference documentation for every new stdlib module family

### Linux Standard Libary Implementation with SysCall (not libc)

- [ ] Introduce a Linux syscall boundary module that the rest of the stdlib builds on
- [ ] Implement stdout and stderr text output without libc
- [ ] Implement stdin input without libc
- [ ] Implement file open, read, write, close, and seek primitives
- [ ] Implement directory iteration and metadata queries
- [ ] Implement path helpers required by the file APIs
- [ ] Implement process exit and basic process information helpers
- [ ] Implement allocator backing with the chosen Linux virtual memory strategy
- [ ] Implement TCP socket create, connect, bind, listen, accept, send, and receive
- [ ] Implement event waiting with the chosen Linux polling primitive
- [ ] Implement thread creation, join, and the selected synchronization primitives
- [ ] Add Linux integration tests that verify the stdlib package works without libc wrappers

### Windows Standard Library Implementation

- [ ] Introduce a Windows OS boundary module that mirrors the Linux stdlib shape
- [ ] Implement console input and output on Windows
- [ ] Implement file open, read, write, close, and seek primitives on Windows
- [ ] Implement directory iteration and metadata queries on Windows
- [ ] Implement path behavior and normalization rules on Windows
- [ ] Implement process exit and basic process information helpers on Windows
- [ ] Implement allocator backing with the chosen Windows virtual memory or heap API
- [ ] Implement TCP socket support through the chosen Winsock surface
- [ ] Implement thread creation, join, and the selected synchronization primitives on Windows
- [ ] Add Windows integration tests for packaged stdlib consumption
- [ ] Verify the public API shape matches Linux except where platform differences are explicitly documented

## Milestone v1.3 Examples and Website

### Create Simple Exammples Demonstrating syntax

- [ ] Basic syntax
- [ ] Type system
- [ ] Modules
- [ ] Borrowing
- [ ] FFI
- [ ] Standard library

### Create Intermediate Examples of semi-realworld usage 
- [ ] Build your own Git
- [ ] Build a neural network
- [ ] Build a simple Database based on https://cstack.github.io/db_tutorial/
- [ ] Build a Bit-torrent Client
- [ ] Build a Breakout Clone with Stark and Raylib

### Create Website to showcase language

- [ ] Choose Hugo and Caddy as the official docs website stack
- [ ] Build the site with a pinned Hugo binary
- [ ] Keep all site assets vendored in the repository
- [ ] Avoid npm and Python as required build dependencies for the website
- [ ] Serve the generated `public/` output directly from Caddy
- [ ] Deploy over SSH with `rsync`
- [x] Choose OVHcloud as the low-cost VPS vendor
- [ ] Choose the OVHcloud deployment region
- [x] Choose Cloudflare Registrar as the domain registrar
- [ ] Choose the primary Stark domain
- [ ] Configure Caddy for HTTPS, redirects, compression, and caching headers
- [ ] Configure VPS hardening: SSH keys only, firewall, automatic security updates, and log rotation
- [ ] Add backup and restore procedures for site content, generated output, and server config
- [ ] Add CI checks that build the website and verify internal links
- [ ] Publish pages for docs, examples, roadmap, benchmark results, and downloads

## Milestone v1.35: The Stark Book

### Book Architecture

- [ ] Define the target reader and prerequisites
- [ ] Define the chapter outline and learning path
- [ ] Split the book into concept chapters and project chapters
- [ ] Define how the book relates to the Language Reference and Standard Library docs
- [ ] Ensure every chapter has stable anchors and standalone Markdown sources
- [ ] Ensure every code sample is stored as a real file or generated from one
- [ ] Add CI that validates every code sample in the book
- [ ] Version the book alongside language milestones

### Core Language Chapters

- [ ] Introduction: what Stark is and why it exists
- [ ] Installing Stark and using the compiler and package toolchain
- [ ] Your first Stark program
- [ ] Variables, functions, and control flow
- [ ] Ownership, borrowing, and move semantics
- [ ] Storage classes: `stack`, `heap`, `arena`, and globals
- [ ] Structs, records, initialization, and layout-aware design
- [ ] Modules, visibility, packages, and manifests
- [ ] Arrays, slices, text types, and views
- [ ] Errors as values, assertions, traps, and no-unwinding semantics
- [ ] Traits, doctrines, generics, and specialization
- [ ] FFI, raw pointers, and strict boundaries

### Performance and Systems Chapters

- [ ] The Stark performance model
- [ ] Undefined behavior, explicit wrapping and saturating arithmetic, and overflow rules
- [ ] `strictfp`, floating-point policy, and optimizer contracts
- [ ] Closed-world optimization and what Stark guarantees to LLVM
- [ ] Memory layout, ABI shape, and interop expectations
- [ ] Diagnostics: how to read and act on Stark compiler errors

### Project Chapters

- [ ] Project: build a command-line text tool
- [ ] Project: build a small multi-module package
- [ ] Project: build a file-processing utility using the standard library
- [ ] Project: build a networking tool once TCP and HTTP land
- [ ] Project: build one performance-oriented example and analyze the generated IR

### Publication and Dataset Quality

- [ ] Cross-link every chapter to the Language Reference where appropriate
- [ ] Cross-link every chapter to the standard library docs and canonical examples
- [ ] Keep chapter source in plain Markdown or MDX for easy indexing
- [ ] Add chapter metadata so sections are easy to ingest and version
- [ ] Add a printable or exportable build of the book
- [ ] Add a “what changed since last version” page

## Milestone v1.4 Performance benchmarking vs C and Rust

### Create the benchmarks

- [ ] Define benchmark fairness rules for Stark vs C vs Rust
- [ ] Lock compiler flags and optimization levels for all benchmarked languages
- [ ] Create microbenchmarks for arithmetic, branching, calls, and memory access
- [ ] Create collection benchmarks for append, lookup, iteration, and resize
- [ ] Create IO benchmarks for file read, file write, and buffered output
- [ ] Create networking benchmarks for TCP client and server throughput
- [ ] Create parser or text-processing benchmarks
- [ ] Automate benchmark execution and result capture
- [ ] Record machine and hardware configuration with every benchmark run
- [ ] Add regression thresholds so performance drops are caught automatically

### Optimize For Performance Based on Results

- [ ] Rank the worst benchmark gaps against C and Rust
- [ ] Classify each gap as frontend, IR, LLVM, runtime, or stdlib overhead
- [ ] Add a concrete optimization task for each top-tier regression
- [ ] Compare generated LLVM IR and final assembly for representative failures
- [ ] Re-run benchmarks after every optimization batch
- [ ] Track which optimizations materially changed results
- [ ] Add permanent regression tests for every fixed benchmark cliff
