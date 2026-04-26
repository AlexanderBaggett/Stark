# Stark Roadmap

Remember this language aims to be faster than idiomatic C or Rust on most projects, we must choose the best possible optimization strategy and explore optimization opportunities.

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
  - `Userfacing/general-idea.md`
  - `Userfacing/BorrowerSystem.md`
  - `Userfacing/ModulesAndVisibility.md`
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
- [x] Deep freeze alias semantics for `const` globals
  - [x] projections from `const` graphs behave as frozen/readonly values, not merely root-guarded globals
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
- [x] Real mutable global rebinding emission
  - [x] zero-initialized mutable globals
  - [x] scalar-initialized mutable globals
  - [x] aggregate-initialized mutable globals
  - [x] tests for mutable global load/store lowering
- [x] Better linkage/visibility lowering for globals
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
- [x] Exit code/runtime termination helpers
  - [x] Add `System.Process.CurrentId()` and `System.Process.Exit(code)` over the internal platform process boundary.
  - [x] Preserve the existing raw `main` return-value mapping while hosted entrypoint conventions remain separate roadmap work.
  - [x] Lower direct process-exit calls as no-return/unreachable so code cannot continue if the OS boundary unexpectedly returns.
  - [x] Add source and executable tests proving the public exit helper terminates with the requested process exit code.
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

- [x] Add first-class unsigned integer width types.
  - [x] Define the source widths `u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`, `u384`, `u512`, `u768`, and `u1024` alongside the existing signed `iN` widths.
  - [x] Extend the grammar and parser so unsigned widths are accepted anywhere integer source types are accepted.
  - [x] Apply the existing explicit integer range rules to unsigned widths, with `min` fixed to `0` and `max` fixed to `2**N - 1` for each width.
  - [x] Reject negative unsigned ranges and out-of-width unsigned endpoints with friendly diagnostics.
  - [x] Preserve unsigned-ness through syntax models, type checking, HIR/MIR/SSA lowering, LLVM emission, and package images instead of treating `uN[a b]` as only a signed integer range spelling.
  - [x] Ensure unsigned arithmetic, comparisons, shifts, integer/float conversions, and formatting/parsing hooks use unsigned semantics where the operation depends on signedness.
  - [x] Update the Language Reference and internals docs so `uN` is explicitly documented as a real language type family, not an accidental stdlib/API spelling.
  - [x] Add parser, type-checking, range-diagnostic, lowering, LLVM, package-image, and runtime tests for representative small, medium, and wide unsigned widths.
- [x] literal switch patterns
- [x] discard/match-all switch patterns
- [x] `var` capture switch patterns
- [x] exact-type named aggregate switch patterns with scalar field bindings as groundwork
- [x] exhaustiveness and unreachable-arm diagnostics

### Enums

- [x] define the first concrete enum runtime representation
  - [x] introduce an internal `EnumLayout` artifact
  - [x] implement `DirectTag` as the only enum layout strategy
  - [x] keep niche-based enum packing as a later optimization, not part of the first implementation
- [x] model enum constructors and payloads
  - [x] register enum declarations in syntax and type models
  - [x] retain enum case names and payload shapes in typed artifacts
  - [x] surface enum constructors in expressions
  - [x] type value-level constructor symbols for unit, tuple, and named-field cases
- [x] switch and pattern integration for enum cases
  - [x] dot-qualified enum case patterns
  - [x] discriminant-aware exhaustiveness and unreachable-arm diagnostics
  - [x] bind active-case payload fields in matched arms
- [x] MIR lowering for enum construct/test/project using `DirectTag`
  - [x] lower unit, tuple, and named-field constructors
  - [x] lower discriminant tests
  - [x] lower active-payload projection
- [x] ownership and destruction over the active enum case only
  - [x] move analysis across enum constructors and matches
  - [x] drop only the active case payload
- [x] settle optional/result treatment
  - [x] `Option` and `Result` are ordinary standard-library enums, not compiler-privileged surface forms
  - [x] they reuse ordinary enum construction, layout, ownership, `switch`, and pattern rules
  - [x] they do not define separate FFI behavior because Stark enums do not cross `ffi` or `export` boundaries
- [x] settle enum foreign-boundary policy
  - [x] Stark enums are never FFI-visible
  - [x] enum-specific `repr` or ABI annotations are out of scope for the current language surface
  - [x] `ffi` and `export` reject enum-dependent types rather than assigning them a foreign ABI shape

### Characters and Strings

- [x] string literal typing
- [x] string constant codegen for the supported path
- [x] character literal lowering
  - [x] ascii character lowering
  - [x] unicode character lowering
  - [x] regression tests for typing and codegen width rules
- [x] richer escape support and validation
  - [x] simple escapes
  - [x] hex and unicode escapes
  - [x] invalid escape diagnostics
  - [x] tests for escape parsing and typing
- [x] full `ascii` / `unicode` runtime/value model
  - [x] concrete layout for `ascii` and `unicode`
  - [x] indexing and slicing semantics
  - [x] literal storage and encoding guarantees
  - [x] stdlib helpers that depend on the text layout model

  

## Milestone 6: Traits and Doctrine

Goal: Stark's abstraction system becomes real, optimizable, and usable.

- [x] Finalize doctrine semantics in the compiler
  - [x] doctrine declaration and type model
  - [x] effect and purity validation for doctrine members
  - [x] no-state and no-capture enforcement
  - [x] doctrine lookup and name-resolution rules
- [x] Closed-world optimization rules for doctrines/traits
  - [x] sealed-by-default assumption rules
  - [x] devirtualization eligibility rules
  - [x] monomorphization vs shared-code rules
  - [x] specialization selection order
- [x] Dynamic dispatch strategy for v1.x
  - [x] decide that trait objects or equivalent runtime dispatch do not exist in v1.x
  - [x] state the no-dynamic-dispatch policy in docs and diagnostics
- [x] Better lowering for laws/doctrines as optimization-friendly abstractions
  - [x] lower doctrine calls to direct calls where possible
  - [x] emit stronger readonly/noalias/capture facts
  - [x] infer closed-world `alwaysinline` for eligible module-private law callees in root-module builds
  - [x] extend conservative closed-world `alwaysinline` inference to eligible source-loaded imported-module law helpers
  - [x] infer conservative `alwaysinline` on eligible non-export imported law entrypoints when every known caller in the build is also a law body
  - [x] carry source-loaded imported modules into HIR, MIR, and SSA artifacts
  - [x] emit internal root-side clones for eligible imported law bodies so closed-world law calls can optimize without changing dependency ABI
  - [x] broader specialization and inlining of law calls where closed-world facts allow it
  - [x] regression tests for emitted LLVM attributes and call shapes

### Strings Revisited

Revisit the temporary text MVP and align the implementation with the intended string model.

- [x] Correct the docs and tests to the intended model
- [x] Fix `ascii` so it is UTF-8 and `unicode` so it is UTF-32
- [x] Change literal and codegen representation for `unicode`
- [x] Replace the current fake `ascii`/`unicode` reinterpret-casts with real widening/narrowing rules
- [x] Add a slice operator for `ascii` and `unicode`
- [x] Add separate owning text types for `ascii` and `unicode`, analogous to Rust's pointer/length/capacity model
- [x] Design and implement a non-hidden-allocation concatenation path

## Milestone 6.5: Pre-StdLib Language Work

Goal: unlock the language surface the standard library wants before the stdlib is redesigned around it.

- [x] Generics sufficient for stdlib-facing result and helper types
  - [x] generic enums such as `IOResult<T>`
  - [x] generic substitution through returns, fields, locals, and methods
  - [x] instantiation at normal use sites for stdlib modules and consumers
  - [x] package and manifest support for generic stdlib declarations
- [x] Overload support for Stark-native APIs
  - [x] top-level function overload groups
  - [x] method overload groups
  - [x] ambiguity and no-match diagnostics
  - [x] symbol, manifest, and package behavior for overload sets
- [x] Destructor syntax and implementation
  - [x] source-level destructor declaration surface
  - [x] ownership integration for scope-exit cleanup
  - [x] MIR/SSA/LLVM lowering for destructor calls
- [x] Implement ASM Functions
  - [x] Freeze the v1 surface as `ffi asm(arch) fn` only, keeping v1 focused on syscall-oriented stdlib shims and deferring methods, generics, and trait/doctrine integration.
  - [x] Extend grammar and parsing for `asm(arch)` plus `in(...)`, `out(...)`, and `clobber(...)` clauses.
  - [x] Carry asm targets, templates, operands, and clobbers through the syntax model and compiler-owned artifacts.
  - [x] Normalize architecture names from the active LLVM target triple into a compiler enum used by asm selection.
  - [x] Select the matching `asm(arch)` declaration for the active target before symbol emission, lowering, and packaging.
  - [x] Diagnose missing target matches and conflicting target-specific declarations for the same user-facing function.
  - [x] Validate legal register names for each supported architecture.
  - [x] Validate operand structure, duplicate bindings, conflicting outputs, and illegal clobbers.
  - [x] Restrict and validate the v1 parameter and return type set for asm functions.
  - [x] Define conservative semantic and effect-model rules for asm functions, including memory, unwind, and optimization assumptions.
  - [x] Integrate selected asm functions with symbol naming and ABI lowering.
  - [x] Add HIR tracking, or an explicit HIR bypass marker, for selected asm declarations.
  - [x] Add MIR lowering support, or an explicit MIR bypass, for asm function bodies.
  - [x] Add SSA lowering support, or an explicit SSA bypass, while preserving correct direct-codegen behavior.
  - [x] Lower structured operands and clobbers into LLVM inline-asm constraint strings.
  - [x] Emit correct LLVM inline-asm calls with target-specific register bindings, side effects, and return handling for the syscall-oriented v1 subset.
  - [x] Persist asm declarations correctly in package manifests for published packages.
  - [x] Support importing and consuming packaged asm declarations from dependent modules.
  - [x] Add parser, semantic, and LLVM emission regression tests for asm functions.
  - [x] Add end-to-end and documentation coverage, including target-selection tests and one minimal asm example.
- [x] Compiler Logging and Traceability
  - [x] Add a structured compiler log stream with `info` / `warning` / `error` levels, stage/category tags, source locations, and key/value metadata.
  - [x] Log pass lifecycle events across the pipeline, including pass start, completion, skip-on-errors, stop-after boundaries, and pass crashes.
  - [x] Log unsupported or fallback lowering exits explicitly, including `MarkUnsupported()` paths in MIR lowering and LLVM declaration fallbacks in codegen.
  - [x] Add regression tests that lock down structured logs for normal pass execution, skipped/crashed passes, and unsupported lowering gaps.
  - [x] Surface compiler logs through CLI/debug tooling with filtering and human-readable formatting.
- [ ] Compiler Observability and Value Tracing
  - [x] Split verbosity from severity so Stark supports low-noise `normal` output and a richer opt-in `verbose` mode without overloading `info` as the only detail control.
  - [x] Add lightweight inherited logging scopes so stage, symbol, and source context can flow through nested compiler work without repeating boilerplate on every event.
  - [x] Introduce first-class `symbol`, `decision`, and `gap` event kinds with explicit outcomes such as `continued`, `stopped`, `skipped`, `bypassed`, and `unsupported`.
  - [ ] Convert `MarkUnsupported()`, early returns, declaration fallbacks, and other partial-lowering exits into gap events with feature tags, stop reasons, and source spans.
  - [x] Rework console and test-host formatting so message-first output is concise in `normal` and richer in `verbose`, while suppressing synthetic boilerplate that does not add signal.
  - [x] Add regression coverage for verbosity filtering, gap events, symbol/source context, and incomplete-feature auditability.
- [x] Create simple ASM shim to enable invoking of Syscall on Linux
  - [x] x86_64 
  - [x] aarch64 (ARM64)
  - [x] riscv64
  - [x] x86 (i386)
  - [x] arm (32-bit) 

## Milestone 7: Standard Library

Goal: replace the current libc-backed stdlib slice with a cross-platform `System` package that hides platform boundaries behind Stark APIs.
- Remember to reference `../StandardLibrary/StandardLibrary.md`
- [x] Define the public `System` module layout
  - [x] `System`
  - [x] `System.Console`
  - [x] `System.IO`
  - [x] `System.IO.File`
  - [x] `System.IO.Path`
  - [x] `System.Text`
- [x] Define the internal runtime/platform module layout
  - [x] `System.Runtime`
  - [x] `System.Runtime.Buffer`
  - [x] `System.Runtime.Platform`
  - [x] `System.Runtime.Platform.Linux`
  - [x] `System.Runtime.Platform.Windows`
- [x] Implement the shared stdlib surface
  - [x] `System.Console` output API
  - [x] `System.IO` error and result model
  - [x] `System.IO.File` owned handle type and destructor-based close behavior
  - [x] `System.IO.Path` helpers
    - [x] caller-buffer `CurrentDirectory(rawmutptr<Ascii>) -> bool` foundation
  - [x] `System.Text` encoding enum and shared conversion helpers for the core owned text types
    - [x] immutable text view pointer/length builtins for low-level stdlib boundaries
- [x] Implement userspace file buffering and encoding-aware text IO
  - [x] fixed-size linear and ring buffer foundations
  - [x] buffering modes
  - [x] newline policy
  - [x] byte IO
  - [x] ascii/unicode text IO
- [x] Linux platform implementation without libc/glibc
  - [x] syscall-backed write/read/open/close/delete/rename/stat/getcwd/ioctl boundary
    - [x] stdout/stderr `ascii` console write
    - [x] file open/read/write/close/delete/move/exists
    - [x] `getcwd`
    - [x] `ioctl` terminal detection
  - [x] terminal detection and buffering-policy support
  - [x] packaged integration tests proving no libc/glibc dependency
- [x] Windows platform implementation without CRT dependency
  - [x] kernel32-backed console and file APIs
  - [x] UTF-16 path conversion boundary
  - [x] terminal detection and buffering-policy support
  - [x] packaged integration tests proving no CRT dependency
- [x] Packaging and documentation
  - [x] package manifest coverage for the new module graph
  - [x] package consumption tests without stdlib source imports
  - [x] reference docs for each public stdlib module family
- [x] Destructor syntax and implementation
  - [x] stdlib regression tests for owned-resource cleanup


## Milestone 7.5 Standard Library: System.Math


### Single instruction Hardware Intrinsics (ASM/Compiler)

- [x] Prerequisite: extend `ffi asm(arch)` to support floating-point parameters and returns so single-instruction math intrinsics can bind FP registers on x86/x64 and ARM64
- [x] `Math.Sqrt` → x86: `vsqrtsd`/`vsqrtss` (AVX, fallback `sqrtsd` SSE2) | ARM64: `fsqrt`
- [x] `Math.FusedMultiplyAdd` → x86: `vfmadd213sd/ss` (FMA3, incl. `vfnmadd`/`vfmsub` variants) | ARM64: `fmadd`
- [x] `Math.ReciprocalSqrtEstimate` → x86: `rsqrtss` | ARM64: `frsqrte` (~12-bit precision)
- [x] `Math.ReciprocalEstimate` → x86: `rcpss` | ARM64: `frecpe`
- [x] `Math.Ceiling` → x86: `vroundsd` mode 2 (SSE4.1) | ARM64: `frintp`
- [x] `Math.Floor` → x86: `vroundsd` mode 1 (SSE4.1) | ARM64: `frintm`
- [x] `Math.Truncate` → x86: `vroundsd` mode 3 (SSE4.1) | ARM64: `frintz`
- [x] `Math.Round` (ToEven) → x86: `vroundsd` mode 0 (SSE4.1) | ARM64: `frintn`
- [x] `Math.Min` (float/double) → x86: `vminsd`/`vminss` | ARM64: `fminnm` (IEEE NaN semantics)
- [x] `Math.Max` (float/double) → x86: `vmaxsd`/`vmaxss` | ARM64: `fmaxnm` (IEEE NaN semantics)
- [x] `BitOperations.LeadingZeroCount` → x86: `lzcnt` (ABM) | ARM64: `clz`
- [x] `BitOperations.TrailingZeroCount` → x86: `tzcnt` (BMI1) | ARM64: `rbit` + `clz`
- [x] `BitOperations.PopCount` → x86: `popcnt` (POPCNT) | ARM64: `cnt` (NEON)
- [x] `BitOperations.RotateLeft/Right` → x86: `rol`/`ror` | ARM64: `ror`/`rorv`


### LLVM IR intrinsics BuiltIn Mappings

- [x] `Math.Sin` → `@llvm.sin.*`
- [x] `Math.Cos` → `@llvm.cos.*`
- [x] `Math.Tan` → `@llvm.tan.*`
- [x] `Math.Exp` → `@llvm.exp.*`
- [x] `Math.Exp2` → `@llvm.exp2.*`
- [x] `Math.Log` → `@llvm.log.*`
- [x] `Math.Log2` → `@llvm.log2.*`
- [x] `Math.Log10` → `@llvm.log10.*`
- [x] `Math.Asin` → `@llvm.asin.*`
- [x] `Math.Acos` → `@llvm.acos.*`
- [x] `Math.Atan` → `@llvm.atan.*`
- [x] `Math.Atan2` → `@llvm.atan2.*`
- [x] `Math.Pow` → `@llvm.pow.*`
- [x] `Math.Sinh` → `@llvm.sinh.*`
- [x] `Math.Cosh` → `@llvm.cosh.*`
- [x] `Math.Tanh` → `@llvm.tanh.*`
- [x] `Math.SinCos` → `@llvm.sincos.*`

## Milestone 8: Optimization and Backend Quality

Goal: emitted LLVM becomes richer, more correct, and more competitive.

### Frontend Optimization Passes

- [x] Constant folding
  - [x] fold scalar arithmetic and comparison expressions
  - [x] fold boolean and branch conditions
  - [x] fold simple aggregate/initializer constants where safe
  - [x] regression tests for folded MIR and SSA
- [x] Compile-time evaluation
  - [x] evaluator for pure literal and arithmetic expressions
  - [x] evaluator for `law` calls with constant inputs where legal
  - [x] diagnostics for non-evaluable expressions in constant-required contexts
  - [x] regression tests for compile-time evaluation results
- [x] Dead code elimination before LLVM emission
  - [x] remove unreachable blocks after simplification passes
  - [x] remove unused SSA instructions and temporaries
  - [x] remove unused allocas and locals where safe
  - [x] regression tests for removed dead code
- [x] Better SSA cleanup/value numbering
  - [x] canonicalize identical commutative expressions
  - [x] remove redundant casts and materializations
  - [x] coalesce equivalent phi nodes
  - [x] rerun branch and block simplification after cleanup
- [x] Simplify trivial branches and blocks
  - [x] fold branch-on-constant
  - [x] merge blocks with single predecessor/single successor
  - [x] remove empty jump-only blocks
  - [x] simplify trivial single-case or default-only switches
- [x] Normalize more control-flow patterns before LLVM
  - [x] canonical loop header/latch forms
  - [x] normalized `switch` lowering structure
  - [x] canonical early-return diamonds
  - [x] regression tests over normalized MIR and SSA

### LLVM Semantic Richness

- [x] function-level `nounwind`/`nofree`/`nosync`/`willreturn`/`mustprogress` style emission for the supported cases
- [x] `hot` / `cold` / inline preference emission
- [x] parameter-level `noalias`
- [x] parameter-level `readonly` / `writeonly`
- [x] parameter-level `nonnull`
- [x] parameter-level `align`
- [x] parameter-level `dereferenceable`
- [x] `captures(...)` style escape-derived lowering
- [x] better `memory(...)` precision

### Targeting and Code Generation Quality

- [x] native LLVM `switch` for simple integer/bool cases
- [x] better aggregate lowering quality
  - [x] small aggregate scalarization heuristics
  - [x] memcpy vs field-store heuristics
  - [x] sret/byval tuning
  - [x] aggregate call/return regression benchmarks
- [x] better global data lowering quality
  - [x] merge identical constants where legal
  - [x] improve `unnamed_addr`, section, and alignment choices
  - [x] reduce redundant helper globals
  - [x] tests for cleaner emitted LLVM global sections
- [x] target-aware code generation options
  - [x] CLI target triple override
  - [x] CPU and feature-string forwarding
  - [x] relocation/PIC/code-model controls if exposed
  - [x] tests for target option plumbing
- [x] object emission and separate link steps
  - [x] keep intermediate object files
  - [x] compile-only vs link-only CLI modes
  - [x] explicit linker and archiver selection
  - [x] multi-object link orchestration tests
- [x] optimization level controls
  - [x] CLI optimization-level surface
  - [x] pipeline behavior per optimization level
  - [x] native toolchain flag forwarding
  - [x] tests that optimization settings change tool invocation/output
- [x] debug info emission
  - [x] carry source spans through MIR and SSA
  - [x] emit line-table debug info
  - [x] emit local variable debug info where feasible
  - [x] tests that debug metadata is present in LLVM output

## Milestone 9: Tooling, Diagnostics, and Developer Experience

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
- [x] better help/usage output
  - [x] group options by workflow in help output
  - [x] document emit-mode defaults and examples
  - [x] document package/stdlib/native toolchain options
- [x] structured diagnostic formatting
  - [x] stable machine-readable diagnostic shape
  - [x] grouped notes and related spans
  - [x] summary/error-count formatting for CLI output
- [x] source snippets in diagnostics
  - [x] single primary-span source rendering
  - [x] secondary-span rendering
  - [x] underline/caret formatting with tabs
  - [x] multiline span rendering

### Repository and Documentation

- [x] top-level design docs exist
- [x] grammar exists in source control
- [x] minimal getting-started guide in `README.md`
- [x] roadmap kept up to date as work lands
- [x] standard library roadmap expanded in `../StandardLibrary/StandardLibrary.md`
  - [x] module-by-module stdlib surface plan
  - [x] runtime/allocator/IO dependency map
  - [x] tests and packaging plan per stdlib area
- [x] examples directory with canonical Stark programs
  - [x] hello-world example
  - [x] multi-module example
  - [x] struct/record/object-initializer example
  - [x] static-library or FFI example

## Milestone 10 (aka v1.0): Release Readiness

Goal: Stark is not just a compiler experiment, but a coherent language/toolchain release.

- [x] Define a minimum viable Stark language subset for a first release
  - [x] feature inclusion matrix
  - [x] platform and toolchain support matrix
  - [x] cut line between release blockers and post-release features
- [x] Freeze syntax for that subset
  - [x] grammar audit against the language reference
  - [x] parser regression lock for accepted and rejected syntax
  - [x] remove temporary compatibility aliases and placeholders
- [x] Freeze lowering rules for that subset
  - [x] ABI and lowering document per supported type family
  - [x] emitted LLVM/object invariants for supported constructs
  - [x] regression tests keyed to the frozen lowering contract
- [x] Document unsupported features explicitly
  - [x] user-facing unsupported features list
  - [x] stable diagnostic behavior for unsupported paths
  - [x] README and release-note pointers to unsupported areas
  - [x] MIR lowering gaps
    - [x] control-flow misuse diagnostics: `break` without an enclosing loop/switch and `continue` without an enclosing loop
    - [x] initializer gaps: unsupported variable initializer shapes; object/array initializers that do not materialize a MIR value; variable initializers that cannot lower to a MIR operand
    - [x] assignment and expression gaps: assignment targets or values that cannot be resolved/coerced; conditional expressions outside the direct ternary shape; expression statements that are neither assignments, rvalues, nor operands
    - [x] operator and type gaps: bitwise and shift operators still require integer operands; ordered comparison lowering now also supports same-kind fixed arrays and same-kind scalarizable `struct`/`record`/`enum` aggregates whose element types are ordered-comparable, and equality and inequality also support same-kind `ascii`/`unicode`, same-kind slices, and scalarizable aggregates over those leaf families
    - [x] name and call gaps: function names/function groups do not lower as first-class operands yet; void-valued direct/member/postfix calls cannot appear in value position
    - [x] aggregate construction gaps: object initializers require resolved named fields; primary object creation only supports matched primary constructors; enum named constructors require named-field variants with complete payloads; enum positional constructors require exact arity; array initializers only lower for fixed arrays
    - [x] place/update gaps: aggregate reads and writes still fall back when field/index paths or address materialization cannot be resolved
    - [x] switch gaps: switch scrutinees must lower to operands; only the current direct switch subset lowers; text-switch partitioning still requires supported `ascii`/`unicode` view types and literal cases
    - [x] indexing gaps: dynamic fixed-array indexing currently requires a local fixed-array source and an integer index; slice/raw-pointer indexing require integer indices; indexing is only supported for fixed arrays, raw pointers, slices, `ascii`, and `unicode`; text slicing currently requires exactly two integer indices
    - [x] runtime-drop gaps: enum/aggregate drop helpers still mark MIR unsupported when tag, field, or comparison temporaries cannot be materialized
  - [x] single-element text indexing and general runtime `ascii`/`unicode` conversion
- [x] Provide a versioned standard library baseline
  - [x] package versioning scheme
  - [x] baseline module list
  - [x] compatibility promise for shipped APIs
- [x] Add release notes / changelog discipline
  - [x] changelog template
  - [x] release tagging and version-numbering process
  - [x] upgrade-notes section for breaking changes
- [x] Add CI for build and tests
  - [x] Linux build/test workflow
  - [x] parser-regeneration drift check
  - [x] test artifact or failure-log upload
- [x] Add sample projects that compile end-to-end
  - [x] hello-world application
  - [x] multi-module application
  - [x] static-library plus consumer sample

## Milestone v1.1: Post-Release Surface Additions

Goal: add non-essential language surface after the first release without slowing v1.0 completion.

- [x] Type aliases
  - [x] grammar and syntax-model support
  - [x] semantic identity and ABI rules
  - [x] visibility and export behavior
- [x] Generic function/type instantiation strategy
  - [x] function instantiation triggers
  - [x] type instantiation triggers
  - [x] cross-module instantiation ownership
  - [x] caching and deduplication of instantiations
  - [x] Generic type parameter handling beyond basic shape support in `v1.1`
  - [x] bind generic parameters on all declaration kinds that support them
  - [x] substitute generic parameters through fields, returns, and locals
  - [x] instantiate generic functions and types at use sites
    - [x] instantiate generic types at use sites for source modules and manifest-backed consumers
    - [x] recursively expand nested generic type layouts from source and manifest-backed use sites
- [x] Monomorphization planning
  - [x] symbol naming scheme
  - [x] code-size control heuristics
  - [x] linkage and dedup rules across objects and packages
- [x] Specialization planning
  - [x] overlap and priority rules
  - [x] coherence and ambiguity diagnostics
  - [x] specialization-driven codegen strategy
- [x] Generic body generation
  - [x] materialize source-backed monomorphized functions into HIR and MIR
  - [x] rewrite instantiated generic calls to concrete specialization symbols
  - [x] ABI lowering for monomorphized function symbols
  - [x] enumerate materialized monomorphized functions during LLVM definition emission
  - [x] LLVM emission of source-backed monomorphized function bodies
  - [x] realize `linkonce_odr`/COMDAT linkage for source-backed imported specializations
  - [x] consumer-owned package-image-backed specialization emission
  - [x] recursive and nested generic specialization expansion during lowering
  - [x] end-to-end generic body regression coverage
- [x] Compiler-owned package image architecture
  - [x] Reframe the current package manifest as a broader package image artifact rather than a lossy package index
  - [x] Define the package image principles and invariants
    - [x] keep the artifact text-based and diffable in Git
    - [x] do not add an embedded format-version field; the compiler and image format evolve together in source control
    - [x] make the image sectioned so new compiler data can be added without collapsing into one flat record type
    - [x] make direct compiler loading the primary path instead of reconstructing fake Stark source from lossy strings
  - [x] Design a Stark-native, near-homoiconic package image syntax
    - [x] represent package and module boundaries explicitly
    - [x] represent exported and public source surface explicitly
    - [x] represent typed compiler-owned sections explicitly rather than hiding them inside string fields
    - [x] define which sections are human-authored, compiler-emitted, or compiler-only
  - [x] Add structured typed-interface sections
    - [x] encode types structurally instead of rendering them as plain strings
    - [x] encode functions, methods, globals, types, and aliases with visibility, generics, modifiers, and symbol names
    - [x] preserve primary-constructor type shape across package boundaries so imported generic bodies can construct published records without source
    - [x] encode re-exports and other package-boundary dependency surface directly
    - [x] preserve enough surface information that docs, tooling, and diagnostics do not need to recover it from lowered compiler facts
  - [x] Add compiler fact sections
    - [x] carry function effects and calling-convention facts across package boundaries
    - [x] carry ABI-lowering facts that should survive package publication
    - [x] carry aggregate and enum layout facts needed for downstream lowering and optimization
    - [x] carry ownership and borrow-related facts that are required for downstream validation or optimization
  - [x] Add generic template body sections
    - [x] publish compiler-owned generic template body sections for exported or public generic functions and methods as a bridge
    - [x] publish structured template planning facts needed for imported generic code-size heuristics
    - [x] publish deferred generic instantiation patterns needed for recursive imported specialization planning
    - [x] publish deferred generic type-instantiation patterns needed for recursive imported type planning
    - [x] publish typed object-creation constructor facts needed for imported generic MIR lowering
    - [x] publish typed object-creation target-type facts needed for imported generic type checking and MIR lowering
    - [x] publish typed object-initializer member facts needed for imported generic type checking and MIR lowering
    - [x] publish typed local declaration facts needed for imported generic type checking and MIR lowering
    - [x] publish typed explicit-conversion target facts needed for imported generic type checking and MIR lowering
    - [x] publish typed enum-constructor facts needed for imported generic type checking and MIR lowering
    - [x] publish typed tuple-enum-constructor call facts needed for imported generic type checking and MIR lowering
    - [x] publish typed unit-enum-case value facts needed for imported generic type checking and MIR lowering
    - [x] publish typed enum-pattern target facts needed for imported generic switch-pattern type checking and MIR lowering
    - [x] publish typed enum-pattern member facts needed for imported generic named-field switch-pattern type checking and MIR lowering
    - [x] publish typed aggregate-pattern target facts needed for imported generic switch-pattern type checking and MIR lowering
    - [x] publish typed literal and nested switch field-pattern facts needed for imported generic switch-pattern type checking and MIR lowering
    - [x] publish typed direct-call target facts needed for imported generic type checking and MIR lowering
    - [x] publish typed field-access facts needed for imported generic type checking and MIR lowering
    - [x] publish typed member-call target facts needed for imported generic type checking and MIR lowering
    - [x] publish the initial typed template-body milestone for public/export generic functions and methods
    - [x] publish typed template bodies for exported or public generic functions and methods
    - [x] preserve enough local, type, and effect information to specialize imported generics without reparsing source text
    - [x] define explicit rules for which templates are published and which stay package-private
    - [x] ensure the template-body representation is suitable for future optimization passes, not just minimal code generation
  - [x] Integrate package images into module loading and the compiler pipeline
    - [x] load package image data directly into compiler artifacts
    - [x] prefer package-image loading over synthetic source reconstruction whenever rich sections are available
    - [x] let the temporary source bridge use authored source-surface overload identity to recover published generic template bodies even when declaration emission still uses canonical typed-interface spellings
    - [x] carry published overload identity and generic-body availability directly in typed-interface function and method entries so structured loading and temporary bridge recovery no longer require source-surface function/type entries for imported generic declarations
    - [x] omit duplicated raw generic body text from package images when the published typed template body covers imported specialization, and keep textual bridge fallback only for legacy manifests that predate typed template publication
    - [x] let the temporary source bridge omit imported generic body text when published typed template bodies are available for downstream type checking and MIR lowering, and keep imported generic declarations declaration-only during structured package-image loading instead of re-rendering fake source bodies
    - [x] preserve authored hot/cold/inline modifier identity directly in typed-interface loading and temporary bridge reconstruction so imported planning and declaration recovery do not depend on compiler-fact sections for that surface
    - [x] centralize source-surface fallback so explicit source-surface sections win over legacy flat surface fields, while older flat source-surface data still preserves authored overload identity when explicit sections are missing
    - [x] emit new package images with explicit source-surface sections as the primary surface representation instead of duplicating authored surface data into legacy flat fields
    - [x] emit new package images with explicit compiler sections as the primary compiler-owned representation instead of duplicating typed interface, compiler facts, and generic templates into legacy flat fields
    - [x] keep legacy manifest reconstruction only as a temporary bridge while the package-image path is being completed
    - [x] remove synthetic-source dependence from manifest-backed generic, alias, doctrine, and trait imports once the package-image path is complete
      - [x] resolve imported public/export type aliases from package-image typed-interface facts instead of reparsed bridge alias declarations
      - [x] resolve imported public/export globals from package-image typed-interface facts instead of reparsed bridge global declarations
      - [x] resolve imported public/export named type shape and record primary-constructor data from package-image typed-interface facts instead of reparsed bridge type declarations
      - [x] resolve imported explicit struct and record constructor signatures from package-image typed-interface facts instead of relying on bridge constructor declarations
      - [x] resolve imported trait/doctrine method signatures from package-image typed-interface facts instead of reparsed bridge declarations
  - [x] Use package images to finish generic code generation across package boundaries
    - [x] emit consumer-owned specializations from imported generic template bodies
    - [x] support recursive and nested specialization expansion when templates come from package images
    - [x] define ownership, linkage, and dedup rules when one package publishes templates and another package owns concrete specializations
    - [x] ensure the package-boundary generic path stays zero-cost at runtime and does not introduce fallback indirection
  - [x] Use package images to improve optimizer capability
    - [x] use imported concrete layout facts during monomorphization planning so manifest-backed large by-value generic instantiations do not get treated like trivially inline helpers
    - [x] let specialization planning consume imported effect, ABI, and layout facts directly instead of re-deriving them from stringly data
    - [x] publish weighted generic body-cost summaries so imported monomorphization planning does not rely only on top-level statement count
    - [x] publish structural optimization summaries for generic templates so imported direct/member-call, field/index-access, aggregate-construction wrappers, simple local-update wrappers, simple binary/comparison wrappers, terminal `if`/`switch` selector wrappers, and simple explicit-conversion or unary pointer-wrapper planning plus module-private inline promotion can use preserved call/access/object-construction/operator/branch/unary/loop/object-creation shape instead of relying only on scalar body cost
    - [x] extend caller-clone lowering from imported doctrine members to imported top-level law-style helpers and root-owned package-backed specialization symbols
    - [x] preserve enough information for future cross-package inlining or richer package-aware optimizations beyond the current caller-clone, effective-kind, semantic-summary, and structural optimization-summary surface
    - [x] preserve resolved interprocedural call memory/capture summaries in package images so imported packages keep richer optimizer facts than just callee sets, effective kinds, and wrapper classifications
    - [x] keep package publication from throwing away semantic, call-graph, and planning facts that are expensive for the compiler to recover later
  - [x] Tooling, inspection, and diagnostics for package images
    - [x] emit package images from the CLI and standard-library packaging flow
    - [x] add a readable dump or inspect mode for package images
    - [x] add diagnostics for missing required sections, malformed structured facts, or unsupported package-image content
    - [x] document the package image as a compiler-owned source artifact rather than a narrow distribution manifest
  - [x] Test coverage for package images
    - [x] writer or loader round-trip tests for rich package images
    - [x] direct-import tests that no longer synthesize fake source when rich package-image sections are present
    - [x] end-to-end tests for imported generic specialization from package images
    - [x] compatibility tests for the temporary legacy manifest bridge while both paths coexist

## Major LLVM IR Emission Optimizations Available

- [ ] Emit full Stark definedness, nullability, and value-range contracts in LLVM IR
  - [x] add conservative `noundef` on direct scalar parameters/returns and qualified borrow/init pointer-like ABI values
  - [x] extend `noundef` across the remaining fully-defined ABI surfaces
  - [x] emit legal `!range` contracts for `bool`, Stark integer values, and enum discriminants where LLVM can encode a non-full range
  - [x] distinguish non-null safe borrows/views from nullable raw-pointer/FFI paths with `nonnull`, `dereferenceable`, and `dereferenceable_or_null`

- [ ] Emit integer UB-backed arithmetic flags for ordinary Stark arithmetic
  - [x] add `nsw` / `nuw` on ordinary add/sub/mul where Stark overflow is undefined behavior
  - [x] add proven `nsw` / `nuw` on shifts and `exact` on division or shift-right when proof facts justify it
  - [x] keep wrapping and saturating operators on the explicit non-UB lowering path with no incorrect flags

- [ ] Emit stronger GEP flags and pointer-arithmetic facts from Stark indexing rules
  - [x] preserve `inbounds` only where object-bound guarantees are actually sound
  - [x] add `nuw` / `nusw` on GEPs when Stark index and range facts prove non-wrapping address arithmetic
  - [x] carry the stronger flags through fixed-array, slice, text, field, and nested projection lowering

- [ ] Emit instruction-level alignment aggressively, not just parameter-level alignment
  - [x] add target-aware `align` on `alloca`, `load`, `store`, `memcpy`, and `memset`
  - [x] keep static allocas in the function entry block so LLVM can treat them as fixed frame slots
  - [x] propagate known alignment through typed field/index projections instead of dropping it after address formation

- [x] Emit immutable-data metadata for `const`, frozen, and once-initialized readonly storage
  - [x] expand `!invariant.load` across all truly immutable loads, not just the simplest const-rooted cases
  - [x] emit `llvm.invariant.start` for runtime-initialized storage that becomes permanently immutable after startup
  - [x] preserve invariance through field/index chains and package-image-backed imported readonly data

- [x] Emit conservative Stark TBAA for typed loads and stores
  - [x] build a Stark TBAA tree for scalars, text units, slices, fixed arrays, and aggregate fields
  - [x] attach struct-path TBAA to typed field and element accesses
  - [x] suppress or drop TBAA when raw-pointer casts or pointer-integer escapes destroy the type-based alias guarantee

- [x] Emit scoped noalias metadata from ownership and borrow exclusivity, not only parameter attributes
  - [x] lower unique borrows, `out`, `init`, fresh result slots, and non-overlapping exclusive regions into `!alias.scope` / `!noalias`
  - [x] preserve those scoped alias guarantees through inlining, monomorphization, and wrapper elimination
  - [x] attach scoped metadata to hot-loop memory accesses when Stark exclusivity proves disjointness

- [x] Emit richer allocator and fresh-allocation facts in LLVM IR
  - [x] annotate allocator declarations with `allocsize`, `noalias`, `noundef`, and `nounwind` where the current allocator contract makes them sound
  - [x] extend allocator declarations with `allocalign` and `nonnull` when the runtime contract proves them
  - [x] add call-result `align`, `dereferenceable`, `noundef`, and `noalias` when heap or arena allocation size/alignment is known
  - [x] preserve freshness facts for constructors and runtime helpers that produce unique storage

- [x] Emit branch prediction metadata directly from Stark source contracts
  - [x] lower `wN` branch and switch annotations into LLVM branch-weight metadata
  - [x] derive likely/unlikely weights from `hot`, `cold`, trap/error edges, and other explicit Stark intent signals
  - [x] use `llvm.expect` only where it is a better match than plain branch-weight metadata

- [x] Emit fast-math flags by default and strict floating-point lowering for `strictfp`
  - [x] attach `fast` to ordinary non-`strictfp` floating-point binary ops, comparisons, same-width float-to-float lowering, and direct float-returning calls
  - [x] extend fast-math coverage to the remaining floating-point instruction and call forms
  - [x] lower `strictfp` functions through constrained floating-point intrinsics or an equivalently strict LLVM surface
  - [x] ensure the optimizer-visible IR matches Stark's default fast-math contract instead of silently using generic strict operations

- [x] Emit contraction-friendly floating-point canonical forms
  - [x] form `llvm.fmuladd` or equivalent fused-friendly IR when Stark semantics allow multiply-add contraction
  - [x] reserve `llvm.fma` for explicit APIs or semantics that require a guaranteed fused operation
  - [x] add regression tests that ordinary floating-point kernels pick the optimizer-friendly form under non-`strictfp`

- [x] Emit stronger global linkage, visibility, and preemption facts
  - [x] prefer `private`, `internal`, `linkonce_odr`, and comdat aggressively under Stark visibility and monomorphization rules
  - [x] emit `dso_local` and other non-preemptable forms wherever the Stark package/runtime model makes them sound
  - [x] extend `unnamed_addr` / `local_unnamed_addr` to address-insignificant constants, helpers, and functions

- [x] Emit optimizer-only imported bodies from package images
  - [x] materialize imported package-image function bodies as `available_externally` when they should exist for optimization but not final ownership
  - [x] feed imported generics, wrappers, and helper bodies to LLVM without forcing duplicate final definitions
  - [x] combine package-image body publication with linkage rules that still permit dead stripping and internalization

- [x] Emit vectorization-friendly layout and constant-data alignment choices
  - [x] over-align eligible readonly numeric fixed arrays, lookup-table-like constants, and similar scalar-array readonly blocks with a 16-byte floor
  - [x] preserve high alignment on stack and heap objects whose element type and usage justify vector loads/stores
  - [x] tune constant/table layout so LLVM can merge, hoist, and vectorize accesses more aggressively

- [x] Emit tail-call markers and specialized calling conventions where Stark semantics make them profitable
  - [x] keep the fast internal calling convention path fully consistent across declarations, definitions, and direct calls
  - [x] add `tail`, `musttail`, or `notail` markers when Stark recursion/state-machine structure makes the choice provably correct
  - [x] use `coldcc`, `preserve_mostcc`, `preserve_nonecc`, or similar specialized conventions for traps, runtime helpers, and dispatch loops when they materially help

- [x] Emit trap-centric no-exception failure IR everywhere Stark guarantees no recovery
  - [x] lower unrecoverable failure to `llvm.trap`, abort helpers, and `unreachable` rather than exception-shaped control flow
  - [x] mark panic/assert/trap helpers `cold`, `noreturn`, and maximally non-throwing
  - [x] ensure unsupported foreign unwinding never causes conservative EH lowering in ordinary Stark code paths

- [x] Preserve pointer provenance aggressively in emitted LLVM IR
  - [x] avoid frontend-generated `ptrtoint` / `inttoptr` sequences for internal address arithmetic when GEP or other provenance-preserving IR is possible
  - [x] use provenance-preserving intrinsics such as `llvm.ptrmask` if Stark later exposes tagged-pointer patterns
  - [x] keep alias-analysis-friendly provenance through package-image-backed generic instantiations and wrapper lowering

- [x] Emit enum-tag and constrained-integer metadata plus optimization-friendly internal representations
  - [x] minimize internal discriminant width when Stark's enum layout contract permits it
  - [x] emit discriminant and constrained-integer range facts on legal LLVM value surfaces: `!range` on typed loads/calls feeding stores and switches, plus `range(...)` on returns, parameters, and direct call operands
  - [x] choose tag encodings that favor common empty/default fast paths where the language contract leaves the representation open

- [x] Use `llvm.assume` only for mid-function facts that cannot be expressed at the boundary
  - [x] add targeted `llvm.assume` for post-check facts such as proven non-null, alignment, or value-range narrowing
  - [x] prefer attributes and metadata first, and keep `llvm.assume` for facts discovered after control-flow refinement
  - [x] add regression tests proving the assumptions are only emitted when the source semantics make them airtight

## Current Completion Checkpoint

The earlier "compiler feels complete" checkpoint has mostly landed. The current
repository can compile multi-file programs, lower aggregates, fields, indexing,
text, generics, package-image imports, and a broad `System` standard-library
surface through native executable workflows.

Remaining near-term gaps should come from the explicit unchecked roadmap items
below, especially:

- [ ] hosted and freestanding entrypoint conventions beyond raw `export ffi fn main`
- [ ] source-level `assert` / `panic` surface over the existing trap/no-unwind failure model
- [ ] common C interop helper conventions and examples
- [ ] captured-lambda environment lowering and full capture-mode preservation
- [ ] named-constant, type-member, `sizeof`, and runtime-dependent integer range endpoints
- [ ] website deployment, hardening, and link-check CI
- [ ] Stark Book architecture and chapter work
- [ ] formal benchmark suite and performance regression tracking
- [ ] advanced MIR/SSA optimizations such as inlining, SROA, proof propagation, loop optimization, and allocation elimination


Everything before this point is frozen
---------------------------------------------------

## Milestone v1.2: Expand Standard Library

### Expand Standard Library Definition For Full IO/Threading/Collections/TCP/HTTP

- [x] Decide the standard-library allocation direction.
  - [x] Use target-typed `new()` for default construction.
  - [x] Use target-typed `new(allocator)` for custom allocator construction.
  - [x] Avoid Rust-style `Type.New()` factory calls as the public collection/handle pattern.
  - [x] Keep raw allocation APIs out of ordinary user-facing collection, IO, filesystem, threading, and TCP surfaces.
- [x] Add the dynamic memory allocation design document.
  - [x] Document default global allocation.
  - [x] Document optional custom allocator construction.
  - [x] Document deterministic drop and no-GC ownership behavior.
  - [x] Document the initial out-of-memory and fallible-growth policy.
- [x] Define the planned public module layout for `System.IO`.
  - [x] Keep `System.IO` as the shared IO error/result and owned file-handle family.
  - [x] Defer a general `Stream` abstraction until Stark has a zero-cost trait/doctrine story for it.
  - [x] Document that small IO enums should use appropriately small tags.
- [x] Define the planned public module layout for `System.Memory`.
  - [x] Define `MemoryError`, `MemoryStatus`, and `MemoryResult<T>`.
  - [x] Define the public `Allocator` vocabulary.
  - [x] Keep raw allocate/free details internal or runtime-adjacent.
- [x] Define the planned public module layout for `System.FileSystem`.
  - [x] Include `DeleteDirectory`.
  - [x] Include owned directory handles and owned directory entries.
  - [x] Keep recursive deletion out of the first surface.
  - [x] Document compatibility options for `System.IO.Path` versus a future `System.FileSystem.Path`.
- [x] Define the planned public module layout for `System.Collections`.
  - [x] Include `List<T>`.
  - [x] Include `Stack<T>`.
  - [x] Include `Queue<T>`.
  - [x] Include `LinkedList<T>`.
  - [x] Include `Dictionary<K, V>`.
  - [x] Require owned heap-backed storage instead of public raw-pointer APIs.
- [x] Define the planned public module layout for `System.Threading`.
  - [x] Put thread operations on the `Thread` struct.
  - [x] Include create/join/detach semantics.
  - [x] Exclude thread pools and synchronization primitives from the first slice.
  - [x] Avoid force-kill thread APIs.
- [x] Define the planned public module layout for `System.Net` and `System.Net.Tcp`.
  - [x] Put TCP operations on `TcpClient` and `TcpListener`.
  - [x] Choose blocking TCP for the first standard-library slice.
  - [x] Use safe slices and owned values instead of public raw socket buffers.
- [x] Remove `System.Net.Http` from the planned standard library.
  - [x] Document HTTP as package-layer work built on `System.Net.Tcp`.
  - [x] Avoid ASP.NET-style server framework scope in the standard library.
- [x] Define member-function visibility inheritance for public stdlib handle and collection types.
  - [x] Member functions inherit the visibility of their enclosing type by default.
  - [x] Member functions may explicitly narrow inherited visibility.
  - [x] Member functions may not be more visible than their enclosing type.
  - [x] `export` is not inherited accidentally and must be explicit.
  - [x] Field visibility remains a separate type-opacity and representation-stability design topic.
- [x] Apply Stark function kinds to the planned standard-library API shapes.
  - [x] Use `finite law` for pure state inspection such as `Count`, `Capacity`, `IsEmpty`, `IsOpen`, `IsJoinable`, and allocator identity helpers.
  - [x] Use `law` for read-only accessors that are pure but can trap when source-level preconditions are not encoded in the signature.
  - [x] Use `finite` for non-allocating in-memory mutations that always return, such as `TryPop`-style collection operations.
  - [x] Keep IO, filesystem, allocation, threading, TCP, blocking, scheduler, and OS-dependent operations as ordinary `fn`.
  - [x] Document the standard-library-wide function-kind policy.
- [x] Implement language and compiler support required by the allocation and stdlib API design.
  - [x] Add target-typed `new()` resolution.
  - [x] Add target-typed `new(args)` resolution.
  - [x] Define and implement struct/record constructor declaration syntax if it is not already complete.
  - [x] Define and implement static type-member functions such as `Thread.Yield()` and `Allocator.Default()` if they are not already complete.
  - [x] Parse and validate `finite`, `law`, and `finite law` on member functions and static type-member functions.
  - [x] Preserve function-kind contracts through member-function lowering, monomorphization, and package images.
  - [x] Add diagnostics that reject `law` bodies with visible side effects, allocation, freeing, synchronization, or mutable external-state observation.
  - [x] Add diagnostics that reject `finite` bodies with `infinite` or `non-deterministic` loops or other unproven non-returning control flow.
  - [x] Parse explicit visibility modifiers on member functions.
  - [x] Resolve omitted member-function visibility from the enclosing type.
  - [x] Reject member functions that are more visible than their enclosing type.
  - [x] Require explicit `export` on ABI-visible member functions.
  - [x] Preserve member-function visibility in package images and imported source surfaces.
  - [x] Add diagnostics and tests for inherited, narrowed, and invalid member-function visibility.
  - [x] Add constructor overload resolution for allocator-taking constructors.
  - [x] Ensure constructor lowering initializes ownership and drop state correctly.
  - [x] Ensure destructor lowering handles heap-backed fields and collection elements correctly.
  - [x] Diagnose public safe APIs that expose raw allocation where a safe owner type should be used.
- [x] Implement the runtime allocator contract.
  - [x] Add the `System.Memory` source module.
  - [x] Implement the default global allocator.
  - [x] Implement internal allocate, reallocate, and free operations.
  - [x] Track allocator provenance so values are freed by the allocator that created them.
  - [x] Add target-aware alignment support.
  - [x] Add Linux allocator backing without libc.
  - [x] Add Windows allocator backing without CRT dependency.
  - [x] Add allocator lowering facts for LLVM where sound.
- [x] Add a production-performance default allocator layer over the OS-backed primitives.
  - [x] Choose a small initial general-purpose allocator strategy that stays simple enough to audit.
  - [x] Keep very large allocations on the current OS virtual-memory path.
  - [x] Add reusable small and medium allocation buckets so collection growth and heap locals do not syscall on every allocation.
  - [x] Preserve target-aware alignment and allocator provenance through bucket allocation and free-list reuse.
  - [x] Add a `Reallocate` fast path that can grow or shrink in place when the bucket/layout makes that sound.
  - [x] Keep the allocate-copy-free fallback for cases the fast path cannot prove safe.
  - [x] Defer per-thread caches until `System.Threading` allocator interaction is deliberately designed.
  - [x] Preserve LLVM facts only where the higher-performance allocator still proves them.
  - [x] Add allocator microbenchmarks and regression tests for `List<T>` growth, `Queue<T>` growth, owned text/path buffers, and heap locals.
    - [x] Add LLVM IR regression coverage for heap locals and `System.Memory` allocate/reallocate/free lowering.
    - [x] Add a minimal allocator benchmark harness once Stark executable benchmark conventions are in place.
    - [x] Add `List<T>` growth benchmarks and regressions after `System.Collections.List<T>` is implemented.
      - [x] Add compile-only `List<T>` growth benchmark source coverage.
      - [x] Add source-imported LLVM lowering regression coverage for `List<T>` growth and move/drop paths.
      - [x] Promote `List<T>` growth to executable timing once imported collection helper linkage is complete.
    - [x] Add `Queue<T>` growth benchmarks and regressions after `System.Collections.Queue<T>` is implemented.
      - [x] Add compile-only `Queue<T>` growth benchmark source coverage.
      - [x] Add source-imported LLVM lowering regression coverage for `Queue<T>` growth and move/drop paths.
      - [x] Promote `Queue<T>` growth to executable timing once imported collection helper linkage is complete.
    - [x] Add owned text/path buffer benchmarks after those APIs allocate through `System.Memory`.
      - [x] Add compile-only caller-owned text/path buffer benchmark coverage for the current APIs.
      - [x] Add executable owned text allocation benchmark coverage now that `System.Text.ToAscii`, `System.Text.ToUnicode`, and owned text concatenation allocate through `System.Memory`.
      - [x] Add executable owned path allocation benchmark coverage now that `System.IO.Path.Join` allocates through `System.Memory`.
  - [x] Add symbol audits proving the faster allocator still does not introduce explicit `malloc`, `realloc`, `free`, libc, or CRT allocator dependencies.
    - [x] Audit packaged standard-library archives for allocator C-runtime symbols.
    - [x] Audit source-imported allocator executables for allocator C-runtime symbols.
    - [x] Audit packaged-stdlib allocator executables for allocator C-runtime symbols.
- [x] Implement the common stdlib error/result model.
  - [x] Keep no-exception result/status enums for recoverable failures.
  - [x] Ensure small enums use the smallest sound internal tag width by default.
  - [x] Preserve larger payload storage only for cases such as `Unknown(i32)`.
  - [x] Add package-image coverage for generic result/status types.
- [x] Implement `System.FileSystem`.
  - [x] Add `CreateDirectory`.
  - [x] Add non-recursive `DeleteDirectory`.
  - [x] Add `OpenDirectory`.
  - [x] Add owned `Directory` handles with best-effort drop cleanup.
  - [x] Add `Directory.ReadNext`.
  - [x] Add owned `FileSystemEntry` names.
  - [x] Add `Exists`, `IsFile`, and `IsDirectory`.
  - [x] Add packaged-consumption tests.
- [x] Implement `System.Collections`.
  - [x] Add the `System.Collections` source module and root `System` re-export.
  - [x] Implement shared owned-buffer/growth infrastructure without exposing implementation helpers publicly.
    - [x] Route `Stack<T>.Reserve` through the internal `List<T>` backing store so stack growth shares list overflow and allocation checks.
    - [x] Share the contiguous power-of-two growth calculation between `List<T>` and `Queue<T>` while keeping allocation and item movement collection-specific.
    - [x] Route `Dictionary<K, V>.Reserve` through an internal hash-storage growth helper that reuses the contiguous growth calculation while preserving dictionary load-factor capacity targets.
  - [x] Implement `List<T>` first with default/custom allocator constructors, reserve, push, clear, metadata inspection, and destructor cleanup.
  - [x] Implement `Stack<T>` on top of the shared contiguous backing strategy for construction, push, metadata inspection, and cleanup.
  - [x] Implement `Queue<T>` with owned ring-buffer storage for construction, reserve, enqueue, metadata inspection, and cleanup.
  - [x] Implement `LinkedList<T>` with owned nodes and no public raw node pointers for construction, front/back insertion, metadata inspection, and cleanup.
  - [x] Implement source-level `out T` bodies for `TryPop`, `TryDequeue`, `TryRemoveFirst`, and `TryRemoveLast`.
  - [x] Complete safe retborrow and slice-view lowering for `Get`, `GetMut`, `Peek`, `AsSlice`, and `AsMutableSlice`.
  - [x] Collapse `LinkedList<T>` to a single allocation per node once generic aggregate layout/drop lowering makes that sound and simple.
  - [x] Implement dictionary hash/equality constraints before exposing generic `Dictionary<K, V>`.
    - [x] Add source-level `Equatable<T>`, `Hashable<T>`, and `DictionaryKey<T>` contracts.
    - [x] Enforce generic key constraints in type checking before `Dictionary<K, V>` can be used.
    - [x] Preserve dictionary key constraints through monomorphization and package images.
    - [x] Add diagnostics that reject dictionary use when `K` has no proven hash/equality contract.
  - [x] Implement `Dictionary<K, V>` once constraints are available.
  - [x] Add move/drop/growth/package-consumption tests for every collection.
    - [x] Add source-imported LLVM lowering coverage for `List<T>`, `Stack<T>`, `Queue<T>`, `LinkedList<T>`, and `Dictionary<K, V>` growth plus move/drop consumption.
    - [x] Add package-image-backed executable coverage for the same collection consumption surface.
    - [x] Enable executable collection timing/tests once source-imported destructors link monomorphized `Clear` helpers and compiler-owned `DictionaryKey` builtins.
    - [x] Enable packaged collection executable consumption once imported constructor bodies and internal helper types lower from package images.
- [ ] Implement callable values and unsafe-boundary prerequisites for threading.
  - [x] Add function item typing for named `fn`, `finite`, `law`, and `finite law` functions in value position.
  - [x] Add explicit promotion from function items to function-pointer values and track address-taken functions.
    - [x] Preserve a unique address-taken function record when a named function item is promoted to a function pointer.
    - [x] Promote source-free package-qualified function items such as `Facade.Make` to ordinary function pointers when the target `fnptr` type matches.
    - [x] Cover target-typed function item promotion in return values and call arguments.
    - [x] Cover overload-specific address-taken facts when overloaded function items promote to distinct `fnptr` types.
    - [x] Cover source-free package-qualified function item promotion for `fn`, `finite`, `law`, and `finite law` declarations.
  - [x] Add function-pointer type syntax and calls while preserving function-kind obligations.
    - [x] Cover rejection when a promoted function item does not satisfy the target `fnptr` kind obligations.
  - [x] Add C#-style lambda syntax for non-capturing lambdas and explicit capture-list lambdas.
  - [ ] Implement safe capture modes: `copy`, `move`, `read`, `mut`, `out`, and `init`.
    - [x] Reject `mut`, `out`, and `init` captures when the listed binding is not writable.
    - [x] Reject `copy` captures for owned or move-only values so capture checking matches Stark ownership rules.
    - [x] Ensure `copy`, `move`, and `read` captures do not inherit writable local access inside the lambda body.
    - [x] Treat `out` and `init` captures as write-only destination bindings in the lambda body until closure lowering can track definite initialization.
  - [ ] Add unsafe/trusted capture modes: `unsafe addr` and `unsafe shared`.
    - [x] Require `addr` and `shared` captures to be written with the explicit `unsafe` marker.
    - [x] Reject `unsafe` markers on safe capture modes such as `copy`.
    - [x] Expose `unsafe addr` captures as readonly address values inside lambda body checking rather than ordinary captured values.
    - [x] Expose `unsafe shared` captures as shared read-only bindings inside lambda body checking.
  - [ ] Add the narrow unsafe model needed for trusted operations, unsafe functions, and small unsafe blocks without disabling ordinary ownership, borrow, range, or initialization validation.
    - [x] Prevent unsafe function items from being promoted to ordinary `fnptr` values while `fnptr` has no unsafe-call requirement in its type.
    - [x] Preserve that unsafe function-item promotion boundary for source-free package-image consumption.
    - [x] Cover unsafe function-item rejection in return values and call arguments.
    - [x] Preserve unsafe function call gating for source-free package-image consumption.
  - [ ] Preserve callable identity, capture-mode facts, function-kind facts, and unsafe-boundary facts through HIR/MIR/SSA lowering, LLVM emission, and package images.
    - [x] Preserve explicit lambda capture name, mode, unsafe marker, type, lambda location, and enclosing function facts in the type-check model.
    - [x] Preserve address-taken function facts through HIR, MIR, SSA, SSA cleanup, and the LLVM IR artifact.
    - [x] Preserve non-capturing lambda synthetic function identity in address-taken facts through HIR, MIR, SSA, SSA cleanup, and the LLVM IR artifact.
    - [x] Preserve `fnptr` function-kind facts for `fn`, `finite`, `law`, and `finite law` parameters in package typed interfaces.
    - [x] Preserve `fnptr<finite law ...>` function-kind facts through packaged type aliases and source-free package consumption.
    - [x] Preserve package-backed `fnptr` parameter target typing for non-capturing lambdas through HIR, MIR, SSA, optimized SSA, and LLVM, including the emitted synthetic lambda definition and packaged call argument.
    - [x] Preserve package-backed `fnptr<law ...>` target typing through lambda semantic validation.
    - [x] Preserve overload-specific address-taken callable identity for source-free package-image function items.
    - [x] Preserve function-kind obligation rejection for source-free package-image function items.
  - [ ] Add parser, type-checking, lowering, LLVM emission, package-image, and diagnostic coverage for function items, function-pointer promotion, lambdas, capture modes, and unsafe gating.
    - [x] Cover the current captured-lambda diagnostic boundary for `ThreadEntry` values and `Thread` construction while captured-lambda lowering remains unavailable.
    - [x] Cover duplicate-name rejection in explicit lambda capture lists.
    - [x] Cover unknown explicit capture clause names and capture modes.
    - [x] Cover read/write behavior for `copy`, `read`, `mut`, `out`, and `init` captures inside lambda bodies.
    - [x] Cover body type-checking behavior for `unsafe addr` and `unsafe shared` captures.
- [x] Implement `System.Threading`.
  - [x] Define the safe thread-entry callable model.
  - [x] Implement `Thread` construction.
  - [x] Implement `Thread.Join`.
  - [x] Implement `Thread.Detach`.
  - [x] Implement `Thread.Yield`.
  - [x] Implement `Thread.SleepMilliseconds`.
  - [x] Add Linux platform backing.
    - [x] Add scheduler backing for `Thread.Yield` and `Thread.SleepMilliseconds`.
    - [x] Add lifecycle backing for `Thread` construction, `Join`, and `Detach`.
  - [x] Add Windows platform backing.
    - [x] Add scheduler backing for `Thread.Yield` and `Thread.SleepMilliseconds`.
    - [x] Add lifecycle backing for `Thread` construction, `Join`, and `Detach`.
  - [x] Add packaged-consumption tests.
    - [x] Add packaged-consumption tests for `ThreadEntry`, `Thread.Yield`, and `Thread.SleepMilliseconds`.
    - [x] Add packaged-consumption tests for `Thread` construction, `Join`, and `Detach`.
    - [x] Add packaged executable coverage for non-capturing lambda `ThreadEntry` values and `Thread` construction.
- [x] Implement `System.Net` and `System.Net.Tcp`.
  - [x] Add shared networking result/status/error types.
  - [x] Add `IPv4Address` and `IPv4Endpoint`.
  - [x] Add the `System.Net.Tcp` source module, `TcpShutdown`, and owned closed-handle `TcpClient`/`TcpListener` lifecycle shape.
  - [x] Implement `TcpClient` construction.
    - [x] Add `TcpClient.Connect(IPv4Endpoint) -> NetResult<TcpClient>`.
  - [x] Implement `TcpClient.Read`.
  - [x] Implement `TcpClient.Write`.
  - [x] Implement `TcpClient.Shutdown`.
  - [x] Implement `TcpClient.Close`.
  - [x] Implement `TcpListener` construction.
  - [x] Implement `TcpListener.Accept`.
  - [x] Add Linux syscall-backed socket support.
    - [x] Add syscall-backed TCP socket create/connect.
    - [x] Add syscall-backed TCP bind/listen.
    - [x] Add syscall-backed TCP read/write.
    - [x] Add syscall-backed TCP accept.
    - [x] Add syscall-backed TCP shutdown.
    - [x] Add syscall-backed socket close.
  - [x] Add Windows Winsock support.
  - [x] Add packaged-consumption tests.
    - [x] Add packaged-consumption tests for `TcpClient.Connect`.
    - [x] Add packaged-consumption tests for `TcpClient.Read` and `TcpClient.Write`.
    - [x] Add packaged-consumption tests for `TcpListener.Listen`.
    - [x] Add packaged-consumption tests for `TcpListener.Accept`.
- [x] Update public documentation as implementation lands.
  - [x] Keep `docs/StandardLibrary/StandardLibrary.md` in sync with the actual package graph.
  - [x] Confirm `docs/StandardLibrary/StandardLibraryBaseline.md` keeps `v1.0` narrow until the release baseline expands.
  - [x] Add examples that use `new()` and `new(allocator)` rather than `Type.New()`.
  - [x] Document any APIs that intentionally remain concrete instead of stream-based.

### Reduce C-Runtime Dependencies

- [x] Define the supported runtime dependency profiles.
  - [x] Distinguish explicit Stark-emitted C runtime calls from Clang/LLVM toolchain-inherited dependencies.
  - [x] Define the default hosted profile and the explicit-C-runtime-free profile.
  - [x] Document that user-written `ffi` may still explicitly call C libraries.
  - [x] Document allowed OS boundaries such as Linux syscalls, Windows `kernel32`, Winsock, and selected Windows allocation APIs.
- [x] Audit current explicit Stark-emitted C runtime symbol dependencies.
  - [x] Make the existing Linux and Windows explicit-C-runtime symbol archive tests active if they are not currently discovered by the test runner.
  - [x] Expand archive/object deny lists to include `malloc`, `realloc`, `free`, and any other explicit C runtime symbols emitted by Stark-owned runtime lowering.
  - [x] Add final executable symbol audits for both source-imported stdlib builds and packaged stdlib builds.
  - [x] Add regression coverage that simple `import System` console programs do not pull unused allocator C symbols from `System.Memory`.
- [x] Replace the C-backed heap-local helper and `System.Memory` allocator lowering.
  - [x] Route heap-local allocation through Stark-owned runtime helpers instead of `malloc`.
  - [x] Route heap-local deallocation through Stark-owned runtime helpers instead of direct `free`.
  - [x] Route `System.Memory.Allocate`, `Reallocate`, and `Free` through the same allocator contract.
  - [x] Preserve allocator provenance through allocation, reallocation, move, drop, and collection growth.
  - [x] Implement true target-aware over-alignment instead of merely passing an alignment argument to `malloc`.
  - [x] Keep LLVM allocation facts such as `noalias`, `nonnull`, `allocsize`, `allocalign`, and `dereferenceable` only where the new runtime contract proves them.
- [x] Add Linux allocator backing without libc.
  - [x] Choose the initial Linux virtual-memory strategy.
  - [x] Implement syscall-backed allocate, deallocate, and reallocate-or-copy behavior.
  - [x] Implement metadata needed for sized free, alignment recovery, and allocation provenance.
  - [x] Add Linux tests proving no `malloc`, `realloc`, `free`, or allocator-related libc symbols remain.
- [x] Add Windows allocator backing without CRT dependency.
  - [x] Choose the initial Windows `Heap*` or `VirtualAlloc` strategy.
  - [x] Implement allocate, deallocate, and reallocate-or-copy behavior using Windows OS APIs.
  - [x] Implement metadata needed for sized free, alignment recovery, and allocation provenance.
  - [x] Add Windows tests proving no CRT allocation symbols remain.
- [x] Improve package and source-module linkage so unused modules do not drag runtime dependencies into final binaries.
  - [x] Avoid linking source-imported dependency objects that provide no referenced symbols.
  - [x] Make packaged static-library consumption cooperate with section garbage collection or finer object granularity.
  - [x] Ensure re-exporting `System.Memory` does not by itself force allocator symbols into unrelated programs.
  - [x] Preserve package-image metadata needed for precise dependency selection.


### Linux Standard Libary Implementation with SysCall (not libc)

- [x] Introduce a Linux syscall boundary module that the rest of the stdlib builds on
- [x] Implement stdout and stderr text output without libc
- [x] Implement stdin input without libc
- [x] Implement file open, read, write, close, and seek primitives
- [x] Implement directory iteration and metadata queries
- [x] Implement path helpers required by the file APIs
- [x] Implement process exit and basic process information helpers
- [x] Implement allocator backing with the chosen Linux virtual memory strategy
- [x] Implement TCP socket create, connect, bind, listen, accept, send, and receive
- [x] Implement event waiting with the chosen Linux polling primitive
- [x] Implement thread creation, join, and the selected synchronization primitives
- [x] Add Linux integration tests that verify the stdlib package works without libc wrappers

### Windows Standard Library Implementation

- [x] Introduce a Windows OS boundary module that mirrors the Linux stdlib shape
- [x] Implement console input and output on Windows
- [x] Implement file open, read, write, close, and seek primitives on Windows
- [x] Implement directory iteration and metadata queries on Windows
- [x] Implement path behavior and normalization rules on Windows
- [x] Implement process exit and basic process information helpers on Windows
- [x] Implement allocator backing with the chosen Windows virtual memory or heap API
- [x] Implement TCP socket support through the chosen Winsock surface
- [x] Implement thread creation, join, and the selected synchronization primitives on Windows
- [x] Add Windows integration tests for packaged stdlib consumption
- [x] Verify the public API shape matches Linux except where platform differences are explicitly documented

## Milestone v1.3 Examples and Website

### Create Simple Exammples Demonstrating syntax

- [x] Basic syntax
- [x] Type system
- [x] Modules
- [x] Borrowing
- [x] FFI
- [x] Standard library

### Create Intermediate Examples of semi-realworld usage 
- [x] Build your own Git
  - [x] Add repository initialization slice with stdlib filesystem and file IO coverage
  - [x] Add repository inspection slice with stdlib directory iteration coverage
  - [x] Add repository status slice with stdlib metadata query coverage
  - [x] Add demo commit-object writer slice with current stdlib file IO, without hashing/compression
  - [x] Add branch ref writer slice for `refs/heads/main` using current stdlib file IO
  - [x] Add object listing slice for `.starkgit/objects` using stdlib directory iteration
- [x] Build a neural network
  - [x] Add fixed-topology inference example with fixed-point inputs, ReLU activation, and deterministic classification
- [x] Build a simple Database based on https://cstack.github.io/db_tutorial/
  - [x] Add in-memory append-only table example with statement/result enums and VM-style execution
- [ ] Build a Bit-torrent Client
  - [x] Add fixed-shape bencoded tracker-response parser with status/result records
  - [x] Add fixed-buffer peer handshake construction and validation example
- [ ] Build a Breakout Clone with Stark and Raylib
  - [x] Add deterministic fixed-grid Breakout game-state update example before Raylib binding
  - [x] Research Raylib 5.5 local build and Linux link requirements
  - [x] Add split Raylib 5.5 Stark bindings for core, shapes, textures, text, models, audio, and shared types
  - [x] Add C ABI shim coverage for Raylib calls that pass or return structs by value
  - [x] Add headless Raylib smoke example that checks and links against the binding surface
  - [x] Discuss and design the initial Raylib-backed playable clone scope before implementation
  - [x] Add first playable Raylib Breakout shell with paddle input, ball bounce, fixed bricks, and score text

### Create Website to showcase language

- [x] Choose Hugo and Caddy as the official docs website stack
- [x] Build the site with a pinned Hugo binary
  - [x] Add pinned Hugo version contract and repository-local build script
- [x] Keep all site assets vendored in the repository
- [x] Avoid npm and Python as required build dependencies for the website
- [x] Serve the generated `public/` output directly from Caddy
- [ ] Deploy over SSH with `rsync`
  - [x] Add environment-driven `rsync` deployment script for generated site output
  - [x] Document the environment-driven SSH deployment flow and remote directory expectations
- [x] Choose OVHcloud as the low-cost VPS vendor
- [ ] Choose the OVHcloud deployment region
- [x] Choose Cloudflare Registrar as the domain registrar
- [ ] Choose the primary Stark domain
- [x] Configure Caddy for HTTPS, redirects, compression, and caching headers
- [ ] Configure VPS hardening: SSH keys only, firewall, automatic security updates, and log rotation
  - [x] Document the SSH, firewall, update, service-user, and log-retention baseline
  - [x] Add rotating Caddy access-log settings to the deployment Caddyfile
- [x] Add backup and restore procedures for site content, generated output, and server config
- [x] Add CI checks that build the website and verify internal links
- [x] Publish initial pages for docs, book, examples, roadmap, benchmark results, and releases
  - [x] Replace placeholder summaries with full published documentation pages
  - [ ] Add release artifact pages once binaries and package images are published

### Ease of Use Tasks

- [x] Implement package-owned native dependency metadata and build orchestration.
  - [x] Design the smallest source or package-image surface for native sources, include directories, library directories, libraries, and platform-specific link arguments.
  - [x] Allow packages such as Raylib to own their native shim and link requirements instead of forcing examples to spell every `--link-arg` manually.
  - [x] Preserve native dependency metadata in package images so imported packages carry their own native build facts.
  - [x] Teach the compiler/toolchain to gather transitive native dependency metadata from imported packages.
  - [x] Compile package-owned native shim sources once per build and include the resulting objects in the final link.
  - [x] De-duplicate native libraries and link arguments deterministically while preserving required link order.
  - [x] Support simple system discovery hooks such as `pkg-config` where available, while still allowing explicit local or vendored paths.
  - [x] Add friendly diagnostics when a native source, header path, or library path is missing.
  - [x] Add friendly diagnostics when a named native system library is missing.
  - [x] Add a Raylib package-image regression where Breakout builds through package-owned native metadata instead of a handwritten link command.
  - [x] Keep graphical execution out of CI unless a headless-safe display path is explicitly configured.
- [x] Implement C#-style interpolated text literals.
  - [x] Parse `$"text {value}"` string interpolation syntax.
  - [x] Bind each interpolation hole as an ordinary Stark expression.
  - [x] Require every interpolated value to have an explicit, known text formatting path.
  - [x] Define how interpolation chooses `Ascii`, `Unicode`, `ascii`, or `unicode` based on target typing and literal contents.
  - [x] Lower runtime interpolation through explicit text-building or caller-provided storage without hidden exceptions.
  - [x] Fold fully constant interpolations to ordinary text constants.
  - [x] Add fixed-capacity stack interpolation such as `stack Ascii label[64] = $"Score: {score}";` using caller-selected storage.
  - [x] Add friendly diagnostics for unsupported value types, missing capacity, or required conversions.
- [x] Implement text concatenation.
  - [x] Define `+` for compatible Stark text forms, including `ascii`, `unicode`, `Ascii`, and `Unicode`.
  - [x] Support common text-producing operands such as `"Score: " + score.ToAscii()`.
  - [x] Add fixed-capacity text buffer declaration syntax such as `stack Ascii combined[4096] = left + right;` and `stack Unicode combined[4096] = left + right;`.
  - [x] Restrict fixed-capacity declaration syntax to stack-owned `Ascii` and `Unicode` buffers until other storage classes have a clear ownership model.
  - [x] Require the capacity expression to be a positive compile-time integer and report beginner-friendly diagnostics for missing, invalid, or misplaced capacities.
  - [x] Lower fixed-capacity text concatenation to a hidden fixed array plus the existing `System.Text.TryConcatAscii` or `System.Text.TryConcatUnicode` copy loop.
  - [x] Define and implement the runtime overflow behavior for fixed-capacity text concatenation without hidden exceptions or silent truncation.
  - [x] Add parser, type-checking, lowering, LLVM, package-image, and runtime coverage for fixed-capacity text concatenation.
  - [x] Preserve Stark's no-exception failure model for allocation-backed or capacity-limited concatenation.
  - [x] Prefer zero-copy views when concatenation is compile-time constant or otherwise provably avoidable.
  - [x] Add full parser, type-checking, lowering, package-image, and runtime coverage for all planned text concatenation forms.
- [x] Implement friendly console text input helpers.
  - [x] Add `System.Console.ReadAsciiLine()` for the common byte-oriented line input path.
  - [x] Add `System.Console.ReadUnicodeLine()` as an explicit Unicode-named companion while keeping existing `ReadLine()` behavior stable.
  - [x] Document returned buffer ownership and lifetime so callers know whether they need to copy into their own `Ascii` or `Unicode` storage.
  - [x] Add compile and runtime coverage for ASCII and Unicode console line input without making CI depend on interactive terminals.
- [x] Implement standard value-to-text and text-to-value conversions.
  - [x] Add integer, floating-point, bool, and enum formatting APIs for `Ascii` and `Unicode`.
  - [x] Add first no-allocation fixed-buffer bool and signed integer formatting APIs for `Ascii` and `Unicode`.
  - [x] Add no-allocation fixed-buffer unsigned `u32` and `u64` formatting APIs for `Ascii` and `Unicode`.
  - [x] Add no-allocation fixed-buffer `i8`, `i16`, `u8`, and `u16` formatting APIs for `Ascii` and `Unicode`.
  - [x] Add no-allocation fixed-buffer `i24`, `i48`, `u24`, and `u48` formatting APIs for `Ascii` and `Unicode`.
  - [x] Add no-allocation fixed-buffer `i96`, `i128`, `u96`, and `u128` formatting APIs for `Ascii` and `Unicode`.
  - [x] Add no-allocation fixed-buffer `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`, and matching unsigned formatting APIs for `Ascii` and `Unicode`.
  - [x] Add first no-allocation fixed-buffer `f32` and `f64` formatting APIs for `Ascii` and `Unicode` with explicit unsupported-value failure.
  - [x] Add first concrete enum formatting APIs for `System.Text.Encoding` and `System.Text.TextError`.
  - [x] Add package-image consumption coverage for implemented fixed-buffer value formatting APIs.
  - [x] Expand no-allocation fixed-buffer formatting APIs across remaining numeric and enum cases.
  - [x] Add first explicit owned-text `System.Text.ToAscii` and `System.Text.ToUnicode` APIs where allocation and formatting failure flow through `System.Memory.MemoryResult<T>`.
  - [x] Add method-style owned-text convenience APIs such as `value.ToAscii()` and `value.ToUnicode()` where the allocation or failure model is explicit.
  - [x] Add package-image and runtime coverage for bool method-style owned-text conversions.
  - [x] Add owned-text `ToAscii` and `ToUnicode` overloads plus package/runtime coverage for the first concrete enum formatting APIs.
  - [x] Expand owned-text `ToAscii` and `ToUnicode` overloads across fixed signed and unsigned integer widths with type-sized allocation buffers.
  - [x] Add exact lowercase bool parsing APIs from `ascii` and `unicode` using `TextResult<bool>`.
  - [x] Add exact base-10 `i64` and `u64` parsing APIs from `ascii` and `unicode` using `TextResult<T>`.
  - [x] Add exact base-10 parsing wrappers through `i8`, `i16`, `i24`, `i32`, `i48`, `u8`, `u16`, `u24`, `u32`, and `u48`.
  - [x] Add exact base-10 parsing APIs through `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`, and matching unsigned widths.
  - [x] Add first concrete enum parsing APIs for `System.Text.Encoding` and `System.Text.TextError`.
  - [x] Add parsing APIs from `ascii` and `unicode` to numeric, bool, and enum values using result/status types.
  - [x] Document exact formatting defaults, including base 10 integer formatting, sign handling, float precision, and locale independence.

## Milestone v1.35: The Stark Book

The single source of truth for the book's full contents is
`docs/Book/SUMMARY.md`. Update that file first when chapters, appendices, or
chapter topics change.

This roadmap section is the single source of truth for tracking the work to
turn that book outline into published website content.

### Book Content Source

- [x] Create the canonical book contents file at `docs/Book/SUMMARY.md`
- [x] Define the target reader and prerequisites in the canonical outline
- [x] Define the full chapter, project, and appendix outline in the canonical outline
- [x] Include Stark-specific coverage rules in the canonical outline
- [x] Include a dedicated Stark borrowing versus Rust borrowing chapter in the canonical outline
- [ ] Review the canonical outline after major language milestones and update it before changing website chapter trackers

### Website Book Architecture

- [x] Add a top-level Book section to the Hugo site
- [x] Create one website page per canonical book chapter
- [x] Create one website page per canonical appendix
- [x] Give every book page stable anchors and URLs
- [x] Add previous/next navigation between book chapters
- [x] Add a book table-of-contents page generated from the website book pages
- [x] Add chapter metadata for title, order, section, status, and navigation
- [x] Version the published book alongside language milestones
- [x] Add a printable or exportable book build
- [x] Add a "what changed since last version" page

### Website Navigation and Publication Polish

- [x] Define a professional visual design system for the Stark website.
  - [x] Establish a programming-language-site information architecture with clear paths for learning, reference, examples, releases, roadmap, and benchmarks.
  - [x] Define Stark-specific brand primitives: logo usage, color palette, typography scale, spacing scale, page widths, borders, shadows, code colors, and responsive breakpoints.
  - [x] Design page templates for home, docs/reference, book chapters, examples, releases, roadmap, benchmarks, and generated Markdown pages.
  - [x] Create reusable website components for callouts, feature summaries, code previews, command blocks, status badges, example cards, release links, and benchmark/result summaries.
  - [x] Make the homepage communicate Stark's performance-oriented language identity in the first viewport, not just a generic documentation landing page.
  - [x] Avoid a sparse "Hello World" look by adding purposeful hierarchy, dense-but-readable documentation surfaces, code-forward visuals, and clear section affordances.
  - [x] Verify desktop and mobile layouts with screenshots before considering the design pass complete.
- [x] Add a clear Getting Started / prerequisites section for first-time Stark users.
  - [x] Document the expected installation path once compiler binaries are published.
  - [x] Identify every non-Stark dependency required to compile and run basic Stark programs on supported platforms.
  - [x] Separate the minimal "run hello world" requirements from optional native, graphical, networking, benchmark, or source-build requirements.
  - [x] Document how LLVM/toolchain dependencies are handled for binary releases versus source builds.
  - [x] Include platform-specific notes for Linux and Windows, including linker, C toolchain, shell, and PATH expectations where applicable.
  - [x] Add a first-run verification flow: install, check `stark --version`, compile a tiny program, run it, and inspect failure diagnostics if setup is incomplete.
  - [x] Link the Getting Started page from the homepage, Book install chapter, Releases page, and examples catalog.
- [x] Integrate the Geekdocs Hugo theme as the site documentation theme.
  - [x] Vendor a pinned prebuilt Geekdocs release bundle under the repository-local site tree.
  - [x] Configure `site/hugo.toml` to use Geekdocs while preserving the pinned Hugo binary and no-npm/no-Python website build.
  - [x] Remove or replace local layout overrides that would prevent Geekdocs layouts from taking effect.
  - [x] Preserve Stark-specific shortcodes for rendering checked-in book and example source files.
  - [x] Add Stark branding, favicon, and minimal `custom.css` overrides instead of editing generated Geekdocs assets directly.
  - [x] Verify `scripts/build-site.sh`, `scripts/check-site-links.sh`, and local `hugo server` still work with the vendored theme.
- [x] Apply the Stark design system on top of Geekdocs without forking generated theme assets.
  - [x] Use Geekdocs for documentation structure, sidebar behavior, search, dark mode, and code/documentation ergonomics.
  - [x] Add Stark-specific layout overrides only where the base theme cannot express the needed programming-language site polish.
  - [x] Keep custom styling in a small, reviewed `custom.css` layer with named design tokens instead of one-off page styling.
  - [x] Ensure generated docs, book pages, examples, roadmap, benchmarks, and releases all feel like one coherent site.
- [x] Add a left-side book navigation bar similar to Geekdocs or the Rust Book.
  - [x] Show all book chapters and appendices in a stable reading order.
  - [x] Highlight the active page and keep the existing previous/next chapter flow.
  - [x] Keep the navigation usable on mobile with a collapsible drawer or equivalent compact control.
  - [x] Preserve the repository-local, vendored Hugo build with no npm or Python requirement.
- [x] Publish repository documentation as rendered website pages instead of raw Markdown downloads.
  - [x] Keep the source Markdown under `docs/` as the source of truth.
  - [x] Generate or mount user-facing language and standard-library docs into the Hugo content tree during the site build.
  - [x] Render doc links with the same site header, sidebar, typography, code blocks, and link checker coverage as book pages.
  - [x] Prefer native Hugo rendering over iframe embedding; use an iframe only if preserving navigation around generated Markdown proves materially simpler.
  - [x] Rewrite website links that currently target `/reference/docs/*.md` so readers stay inside the rendered documentation site.
- [x] Investigate and fix HTML entity escaping in rendered Markdown/code samples.
  - [x] Reproduce the `&#34;` escaping issue in TOML examples on the generated site.
  - [x] Identify whether the escaping comes from Hugo shortcode rendering, generated Markdown exports, Goldmark configuration, syntax highlighting, or manual escaping in source content.
  - [x] Ensure TOML, Stark, C, args, and shell snippets render literal quotes and punctuation inside code blocks.
  - [x] Add a site build or link/check test that catches escaped quote regressions in rendered code blocks.
  - [x] Audit existing published pages for similar entity leakage after the fix.
- [x] Plan and implement the examples section as a real example catalog.
  - [x] Decide the display model before implementation: one page per example, grouped by basics, standard-library usage, intermediate projects, and native-backed or graphical examples.
  - [x] For each example page, show the purpose, source files, build/run command, expected output where applicable, and test/CI status.
  - [x] Render source code from checked-in example files rather than duplicating snippets by hand.
  - [x] Mark examples that require native dependencies, networking, graphics, or manual execution.
  - [x] Link examples back to relevant book chapters and reference docs.
- [x] Remove the public Unsupported Features documentation from the website.
  - [x] Remove the `UnsupportedFeatures.md` link from public docs navigation.
  - [x] Remove or replace the book's unsupported-features appendix so readers are not sent through a long page that mostly says current functionality is working.
  - [x] Keep real gaps tracked in this roadmap and in targeted diagnostics rather than in a broad public "unsupported" page.
- [x] Rename the Downloads section to Releases.
  - [x] Change navigation, page title, and content from Downloads to Releases.
  - [x] Point the initial Releases page at the canonical public repository and future release artifacts.
  - [x] Keep a redirect or compatibility path from `/downloads/` to `/releases/` if existing links already point there.

### Chapter Publication Progress

- [x] Publish draft Part I: First Contact
  - [x] Chapter 1: Introduction: Why Stark Exists
  - [x] Chapter 2: Installing Stark and Building Programs
  - [x] Chapter 3: Hello, Stark
  - [x] Chapter 4: A Small Stark Tour
- [x] Publish draft Part II: Stark's Core Language
  - [x] Draft Chapter 5: Values, Types, and Ranges
  - [x] Draft Chapter 6: Bindings, Mutation, and Control Flow
  - [x] Draft Chapter 7: Ownership, Moves, and Drops
  - [x] Draft Chapter 8: Borrowing in Stark
  - [x] Draft Chapter 9: Stark Borrowing Compared With Rust
  - [x] Draft Chapter 10: Storage Classes and Lifetimes
  - [x] Draft Chapter 11: Aggregates and Layout-Aware Design
  - [x] Draft Chapter 12: Enums and Pattern Matching
  - [x] Draft Chapter 13: Arrays, Slices, Text, and Views
- [x] Publish draft Part III: Packages, Effects, and Boundaries
  - [x] Draft Chapter 14: Modules, Visibility, and Packages
  - [x] Draft Chapter 15: Function Guarantees and Effects
  - [x] Draft Chapter 16: Errors Without Exceptions
  - [x] Draft Chapter 17: Generics, Traits, Doctrines, and Specialization
  - [x] Draft Chapter 18: Callable Values and Thread Entries
  - [x] Draft Chapter 19: FFI, Raw Pointers, and Native Packages
- [x] Publish draft/future Part IV: The Standard Library
  - [x] Draft Chapter 20: Console, Process, and Platform Basics
  - [x] Draft Chapter 21: Memory and Collections
  - [x] Draft Chapter 22: Files, Directories, Paths, and Text
  - [x] Draft Chapter 23: Threading and TCP
  - [x] Future placeholder Chapter 24: Testing Stark Code
- [x] Publish draft Part V: Performance and Systems Programming
  - [x] Draft Chapter 25: Stark's Performance Model
  - [x] Draft Chapter 26: Memory Layout, ABI, and Interop Expectations
  - [x] Draft Chapter 27: Integer, Floating-Point, and Overflow Policy
  - [x] Draft Chapter 28: Reading Stark Diagnostics
  - [x] Draft Chapter 29: Looking at Generated IR
- [x] Publish draft Part VI: Projects
  - [x] Draft Chapter 30: Project: Command-Line Text Tool
  - [x] Draft Chapter 31: Project: Multi-Module Package
  - [x] Draft Chapter 32: Project: File Processing Utility
  - [x] Draft Chapter 33: Project: Native-Backed Package
  - [x] Draft Chapter 34: Project: Performance Case Study
- [x] Publish draft Appendices
  - [x] Draft Appendix A: Keywords and Reserved Words
  - [x] Draft Appendix B: Operators and Symbols
  - [x] Draft Appendix C: Integer Widths and Range Rules
  - [x] Draft Appendix D: Function Kinds and Guarantees
  - [x] Draft Appendix E: Storage Classes and Ownership Quick Reference
  - [x] Draft Appendix F: Package Manifest Reference
  - [x] Draft Appendix G: Unsupported and Future Features
  - [x] Draft Appendix H: Stark for Rust Programmers
  - [x] Draft Appendix I: Stark for C# Programmers
  - [x] Draft Appendix J: Stark for C Programmers

### Book Sample Quality

- [x] Store every current book code sample as a real file or generate it from a real file
- [x] Add CI that validates every current compilable book sample
- [x] Mark intentionally rejected examples as negative tests where possible
- [x] Cross-link every chapter to the Language Reference where appropriate
- [x] Cross-link every chapter to standard-library docs and canonical examples
- [x] Keep chapter source in plain Markdown for easy indexing and future export

## Milestone v1.4 Performance benchmarking vs C and Rust

### Create the benchmarks

- [x] Define benchmark fairness rules for Stark vs C vs Rust
- [x] Lock compiler flags and optimization levels for all benchmarked languages
- [x] Add C and Rust baseline counterparts for current executable benchmark scenarios
- [x] Create microbenchmarks for arithmetic, branching, calls, and memory access
- [x] Create collection benchmarks for append, lookup, iteration, and resize
- [ ] Create IO benchmarks for file read, file write, and buffered output
- [ ] Create networking benchmarks for TCP client and server throughput
- [ ] Create parser or text-processing benchmarks
- [x] Automate benchmark execution and result capture
- [x] Compile and run Stark, C, and Rust benchmark rows from the shell harness
- [x] Record machine and hardware configuration with every benchmark run
- [ ] Add regression thresholds so performance drops are caught automatically

### Optimize For Performance Based on Results

- [x] Rank the worst benchmark gaps against C and Rust
  - Current top remaining gap: `benchmarks/text/OwnedTextAllocation`, with Stark still roughly 3x slower than the C/Rust rows after the first optimization batch.
- [x] Classify each gap as frontend, IR, LLVM, runtime, or stdlib overhead
  - `OwnedPathAllocation`: runtime allocator linkage plus `System.IO.Path` stdlib hot-loop overhead.
  - `OwnedTextAllocation`: mixed stdlib/runtime/codegen overhead; allocator sharing is fixed, but owned-result traffic and public `System.Text` call boundaries still dominate.
- [x] Add a concrete optimization task for each top-tier regression
- [x] Add whole-program or LTO-style optimization for Stark executables so stdlib calls can inline into user code. This is probably the highest-leverage fix.
- [x] Add direct Unicode integer formatting instead of ASCII formatting followed by full UTF-8 decode.
- [x] Add an ASCII-fast path for TryConvertAsciiToUnicode, especially for known ASCII sources.
- [x] Lower text concat/copy operations to llvm.memcpy where source/destination are contiguous.
- [x] Rewrite System.IO.Path internals to work from raw data pointer + length in hot loops, avoiding one-character ascii slice construction.
- [x] Consider a path “facts” helper that computes extension/base/directory ranges in one pass, or rely on cross-module inlining to make separate calls cheap.
- [x] Compare generated LLVM IR and final assembly for representative failures
- [x] Re-run benchmarks after every optimization batch
- [x] Track which optimizations materially changed results
  - Runtime allocator symbols changed from per-module internal state to shared `weak_odr` state, then thread-local buckets. This fixed the major Owned Path cliff.
  - Raw `System.IO.Path` data-pointer loops plus shared allocator state brought `OwnedPathAllocation` to C/Rust parity on the focused runs.
  - `System.IO.Path.GetFacts` now computes path length, extension, base-name, and directory-name ranges once for callers that need several components.
  - `TryConvertAsciiToUnicode` now lowers to an inline System.Text builtin that widens ASCII bytes directly to `i32` with zero-extension, with a dedicated `AsciiToUnicodeConversion` benchmark tracking the fast path. On the focused 30-run sample from April 26, 2026, this moved Stark from roughly 2.98 ms to 1.75 ms and put it in the same band as the C/Rust rows.
  - Direct Unicode integer formatting, ASCII conversion fast path, exact owned integer text sizing, and memcpy concat lowering improved the implementation shape but did not close the remaining Owned Text gap by themselves.
- [ ] Add permanent regression tests for every fixed benchmark cliff


## Milestone v2.0 TBD

### Project Testing and `System.Testing`

- [ ] Define the Stark test-project model.
  - [ ] Model keywords and syntax after Xunit, such as [Fact] [Theory]
  - [ ] decide whether test projects are a separate `kind = "test"` manifest kind or executable projects with test metadata
  - [ ] define how solution manifests identify default test sets
  - [ ] keep test discovery explicit and static; avoid runtime reflection as a required language feature
- [ ] Add a standard-library testing module inspired by xUnit.
  - [ ] add a `System.Testing` module or equivalent package-facing testing root
  - [ ] port the core assertion vocabulary needed by the current C# xUnit tests, such as truth checks, equality checks, and failure reporting
  - [ ] model assertion failure using Stark's no-exception failure/result story rather than hidden unwinding
  - [ ] keep allocation and formatting costs explicit so test-only helpers do not leak into normal runtime expectations
- [ ] Implement `stark test` on top of test projects.
  - [ ] build test projects through the existing project/solution manifest driver
  - [ ] run produced test executables and map their results into concise CLI output
  - [ ] support solution-level test aliases and default test sets
  - [ ] preserve `--dev`, `--release`, path dependencies, and package-backed dependencies for tests
- [ ] Add examples and docs for Stark-native tests.
  - [ ] add at least one standard-library test project using `System.Testing`
  - [ ] document how to port existing xUnit-style test cases into Stark test projects
  - [ ] add regression coverage for project-local and solution-level `stark test`

### Constrained Generics

- [ ] Trait/doctrine constraint solving
  - [ ] collect obligations from generic and doctrine use sites
  - [ ] candidate lookup and matching
  - [ ] ambiguity and no-solution diagnostics
  - [ ] instantiate solved obligations into lowered calls
- [ ] `where`-clause semantic binding and validation
- [ ] define specialization interaction with constrained generic instantiation
- [ ] C# style triple """ strings and @"  strings
- [ ] Rust style rumtime dynamic heap allocation with dynamic sizing via `List` like rusts `vec` C# function names, but with rust semantics


### Trait/Doctrine Runtime Dispatch, If Ever Added

- [ ] define object-safety or equivalent restrictions if runtime dispatch is ever added
- [ ] define runtime representation and ABI if runtime dispatch is ever added
- [ ] tests for dynamic dispatch lowering if runtime dispatch is ever supported

### Low-Level Platform Intrinsics

- [ ] Decide HOW Stark exposes inline assembly, syscall intrinsics, or another first-class low-level platform boundary
- [ ] Define the safety model and target restrictions for those intrinsics
- [ ] Evaluate whether the Linux stdlib should migrate from a linked syscall boundary shim to direct Stark-level intrinsics
- [ ] Revisit asm operand widening for `bool` and floating-point values after the syscall-oriented v1 surface ships
- [ ] First class vector types support for SIMD

### Compiler Observability and Trace Artifacts

- [ ] Add first-class `value` trace events for expression, HIR, MIR, SSA, and LLVM-entity flow
- [ ] Assign compilation-local correlation IDs to symbols, source expressions, and lowered values so one entity can be followed across stages
- [ ] Add opt-in structured trace-file sinks that emit machine-readable logs to disk
- [ ] Emit per-symbol trace files and gap-only audit artifacts for post-mortem debugging
- [ ] Add regression coverage for value-flow tracing, correlation IDs, and emitted trace artifacts


## Milestone V3.0

### Macro/Metaprogramming System


### Additional Optimization Work

Stark's optimization roadmap should assume a performance-first goal: generated
code should beat idiomatic Rust consistently and beat idiomatic C when Stark's
semantic restrictions expose facts that C cannot safely promise. The tasks below
are written as assignable implementation work. Each optimizer task must include
SSA or pipeline regression tests, LLVM emission checks when LLVM output changes,
and at least one executable benchmark or microbenchmark when the transform is
expected to affect runtime speed. Optimizations must preserve `-O0` and `-Og`
debuggability unless a task explicitly says otherwise. 

- [ ] Add an SSA value-fact model artifact
  - [ ] introduce a compiler artifact for per-function SSA facts, such as `SsaValueFactModel` or equivalent
  - [ ] represent integer ranges, known bits, boolean constants, known-null/non-null pointers, known pointer alignment, and known slice/text lengths
  - [ ] include block-entry and block-exit fact sets so facts learned from branches can be scoped correctly
  - [ ] make the fact lattice explicit: unknown, known fact, conflicting/overdefined fact
  - [ ] add diagnostic/log hooks so optimization traces can explain which facts were learned or rejected
  - [ ] add unit tests for fact joining at phis, branch targets, loops, and unreachable blocks

- [ ] Add an SSA value-range and proof-propagation pass
  - [ ] add a `value-facts` pass after `const-prop` and before `lower-abi`
  - [ ] run it at `-O1`, `-O2`, and `-O3`; skip fact-based rewrites at `-O0` and `-Og`
  - [ ] propagate source-level integer range constraints through `add`, `sub`, `mul`, shifts, bitwise operations, comparisons, casts, and phis
  - [ ] prove tighter result ranges for non-wrapping arithmetic where Stark's ordinary arithmetic makes overflow undefined
  - [ ] preserve separate handling for wrapping and saturating arithmetic so proof facts do not erase required semantics
  - [ ] propagate known slice length, text length, and fixed-array length facts through slice/text construction and indexing operations
  - [ ] propagate branch facts such as `x < n` on the true edge and `x >= n` on the false edge
  - [ ] propagate pointer facts from null checks, equality checks, borrow-derived non-null values, and address-of operations
  - [ ] add tests showing narrower ranges after branches, phis, casts, arithmetic, and loops

- [ ] Add fact-driven branch and switch pruning
  - [ ] add a rewrite pass after `value-facts` that consumes the SSA fact artifact
  - [ ] fold comparisons proven always true or always false
  - [ ] rewrite branches with proven conditions into gotos
  - [ ] remove switch cases whose match value is outside the proven input range
  - [ ] rewrite switches with one reachable case into direct gotos or simple branches
  - [ ] remove blocks that become unreachable, then rerun SSA cleanup and constant propagation
  - [ ] keep branch-weight metadata valid after deleting or merging edges
  - [ ] add tests proving dead switch arms, impossible branches, and unreachable phis are removed

- [ ] Feed value facts into LLVM emission
  - [ ] teach LLVM emission to consume the SSA value-fact artifact instead of relying only on local type-based range queries
  - [ ] emit stronger `range` metadata for loads and returns when propagated facts are narrower than source types
  - [ ] emit stronger `nuw`, `nsw`, and `exact` flags on arithmetic, division, and shifts when facts prove the contracts
  - [ ] emit stronger `inbounds`/`nuw` GEP flags for fixed-array, slice, text, and aggregate element accesses when index facts prove object bounds
  - [ ] emit `llvm.assume` for facts that are valuable to LLVM but not otherwise visible in IR
  - [ ] add LLVM tests that compare before/after IR for range metadata, arithmetic flags, GEP flags, and assumptions

- [ ] Add direct-call devirtualization for known function pointers
  - [ ] add an SSA rewrite pass after `cleanup-ssa` and before inlining
  - [ ] detect `SsaIndirectCallRValue` targets that are directly `SsaFunctionAddressValue`
  - [ ] detect targets loaded through phis where every reachable incoming value is the same function address
  - [ ] rewrite proven indirect calls into `SsaCallRValue` direct calls
  - [ ] preserve address-taken function facts when a function pointer value still escapes or is stored
  - [ ] remove address-taken function facts when devirtualization eliminates the only address use
  - [ ] add tests for direct fnptr calls, lambda calls, phi-joined identical targets, and non-devirtualized mixed targets

- [ ] Add Stark-level direct-call inlining
  - [ ] add an `inline-ssa` pass after devirtualization and before cleanup/const propagation reruns
  - [ ] inline small non-recursive module-private functions with available SSA bodies
  - [ ] inline wrapper-like functions identified by `FunctionOptimizationSummary`
  - [ ] inline `law` and `finite law` helpers more aggressively than ordinary functions
  - [ ] inline monomorphized generic helpers when the concrete body is owned by the current module or available from a package image
  - [ ] clone and inline call sites with constant arguments when the clone unlocks branch pruning, range narrowing, or aggregate scalarization
  - [ ] refuse inlining for `ffi`, `cold`, explicitly `noinline`, recursive, or unsupported direct-codegen bodies
  - [ ] rerun SSA cleanup, constant propagation, value-facts, and branch pruning after inlining
  - [ ] add tests showing abstraction wrappers disappear from optimized SSA and LLVM
  - [ ] add benchmarks comparing hand-written monomorphic code to equivalent wrapper/generic/law code

- [ ] Add alias-aware memory optimization
  - [ ] add a memory fact pass after inlining and cleanup
  - [ ] partition memory by stack local, global, field path, aggregate lane, borrow parameter, raw pointer, and unknown memory
  - [ ] use ownership validation, borrow kinds, `noalias`, parameter capture summaries, and function memory-effect summaries to decide which operations may alias
  - [ ] treat `law`/readonly calls as non-barriers for memory they cannot read or write
  - [ ] eliminate redundant loads when no intervening write can affect the loaded location
  - [ ] forward stored scalar values to later loads when the local/field/lane is proven unchanged
  - [ ] remove stores overwritten before any possible read
  - [ ] keep raw-pointer and FFI operations conservative unless facts prove isolation
  - [ ] add tests for redundant local loads, field loads, readonly calls, unknown raw-pointer barriers, and dead stores

- [ ] Add scalar replacement of aggregates before ABI lowering
  - [ ] add an SROA pass after inlining, memory optimization, and cleanup
  - [ ] identify non-escaping stack aggregate locals, temporary aggregate values, and small fixed arrays
  - [ ] split eligible structs, records, fixed arrays, and enum payloads into independent scalar SSA lanes
  - [ ] replace aggregate `insert`/`extract` chains with scalar values
  - [ ] replace aggregate load/store pairs with lane-level loads/stores when the address does not escape
  - [ ] remove `SsaCopyMemoryInstruction` when every copied lane can be forwarded or reconstructed
  - [ ] reconstruct the aggregate only at ABI, FFI, raw-pointer, or escaped-storage boundaries
  - [ ] add tests for struct, record, fixed-array, nested aggregate, enum payload, and escaped aggregate cases
  - [ ] add benchmarks for small vector-like structs and data-model examples compared to Rust and C equivalents

- [ ] Add destination propagation and result-location optimization
  - [ ] run after SROA so scalarized values are preferred over aggregate temporaries
  - [ ] forward object, record, enum, and fixed-array construction directly into the final destination when no observable temporary is required
  - [ ] propagate caller-owned return/result slots backward through wrappers and helper calls
  - [ ] eliminate temporary aggregate copies introduced by assignment chains, return statements, and aggregate updates
  - [ ] prefer direct stores to destination lanes over construct-then-copy lowering
  - [ ] add tests for constructor elision, return-slot propagation, helper wrapper elision, and copy-chain removal

- [ ] Add loop fact analysis
  - [ ] identify natural loops in optimized SSA using dominators and back edges
  - [ ] infer induction variables, initial values, step values, and exit bounds
  - [ ] feed induction ranges back into the SSA value-fact model
  - [ ] prove fixed trip counts where loop bounds and steps are compile-time known
  - [ ] prove index ranges for loops over fixed arrays, slices, and text
  - [ ] add tests for `while willexit`, ordinary `while`, and `for` loops once lowering supports each shape

- [ ] Add loop-invariant code motion
  - [ ] hoist pure arithmetic, conversions, address calculations, and law calls out of loops when operands are loop-invariant
  - [ ] hoist loads when alias-aware memory facts prove the loaded location is unchanged during the loop
  - [ ] avoid hoisting operations that may trap, allocate, call FFI, synchronize, or depend on strict floating-point state
  - [ ] preserve debug locations and branch weights after hoisting
  - [ ] add tests showing pure computations and proven-safe loads move to loop preheaders

- [ ] Add loop strength reduction and induction simplification
  - [ ] replace repeated multiplication in induction expressions with incremented recurrence values
  - [ ] simplify array/slice/text pointer stepping into canonical induction-address form
  - [ ] canonicalize loop exits into shapes LLVM's loop optimizer and vectorizers recognize
  - [ ] use Stark integer ranges to prove no-wrap induction increments where valid
  - [ ] add tests for index arithmetic, pointer stepping, and no-wrap loop increments

- [ ] Add small fixed-count loop unrolling
  - [ ] unroll loops with statically proven small trip counts at `-O2` and `-O3`
  - [ ] use a speed-first threshold by default, with a larger threshold for hot functions and a smaller threshold for cold functions
  - [ ] preserve semantics for `break`, `continue`, early returns, drops, and lifetime markers
  - [ ] rerun cleanup, constant propagation, and value-facts after unrolling
  - [ ] add benchmarks for small fixed-array loops and text/byte loops

- [ ] Add branch shaping and predication
  - [ ] implement jump threading from proven branch facts
  - [ ] merge diamonds into `select` when both arms are pure, cheap, and branch misprediction is likely more expensive than executing both arms
  - [ ] keep branches when either arm may trap, allocate, write memory, call FFI, or contain cold error handling
  - [ ] normalize boolean and comparison chains into backend-friendly forms before LLVM emission
  - [ ] add tests for branchless lowering, preserved cold branches, and fewer hot-path branches

- [ ] Add tail-call and recursion optimization
  - [ ] convert self-tail-recursive functions into loops before SSA cleanup
  - [ ] preserve sibling-tail-call eligibility through ABI lowering for compatible signatures
  - [ ] emit LLVM `tail`/`musttail` only when ABI proof is complete
  - [ ] add tests for tail-recursive wrappers, accumulator recursion, and recursive state machines

- [ ] Add escape analysis for future `heap` and `arena` lowering
  - [ ] classify allocations as non-escaping, return-escaping, store-escaping, call-escaping, or raw-pointer escaping
  - [ ] promote non-escaping heap allocations to stack storage when lifetime is bounded
  - [ ] scalar-replace non-escaping heap and arena aggregates
  - [ ] remove temporary allocation wrappers introduced by future stdlib abstractions
  - [ ] keep FFI and raw-pointer escapes conservative
  - [ ] add tests for heap-to-stack promotion, allocation removal, returned allocations, and raw-pointer escapes

- [ ] Add specialization-driven optimization for full generics and constrained generics
  - [ ] run monomorphization-time constant propagation for generic parameters and type-derived constants
  - [ ] clone generic functions when concrete type facts allow smaller or faster bodies
  - [ ] add SpecConstr-style specialization for recursive functions over known enum/state shapes
  - [ ] erase doctrine and trait abstraction overhead when constraint resolution is compile-time complete
  - [ ] add tests comparing optimized generic abstractions to hand-written monomorphic code

- [ ] Add rule-based optimization for pure and `law` code
  - [ ] define a phase-controlled rewrite rule format for selected `law` functions and standard-library combinators
  - [ ] support arithmetic, bitwise, text, slice, and future iterator/view rewrites
  - [ ] require each rewrite rule to declare side-effect, overflow, range, and alias preconditions
  - [ ] emit optimizer diagnostics for rule firings, missed firings, and inhibited rewrites
  - [ ] add benchmark-backed tests for intermediate-structure elimination and algebraic simplification

- [ ] Add equality-saturation experiments for selected hot kernels
  - [ ] build e-graph rewrite sets for arithmetic, bitwise, and pure `law` expressions
  - [ ] use an extraction cost model tuned for Stark ABI costs, register pressure, and LLVM lowering realities
  - [ ] gate e-graph optimization behind an explicit optimization tier or hot-kernel annotation until it proves broadly useful
  - [ ] compare extracted code against conventional GVN, PRE, reassociation, and LLVM-only optimization on representative workloads

- [ ] Add optimization benchmark gates against Rust and C
  - [ ] add a microbenchmark for every new optimizer pass before enabling it by default at `-O2` or `-O3`
  - [ ] compare each benchmark against idiomatic Rust, idiomatic C, and hand-tuned C where appropriate
  - [ ] track whether each pass improves runtime, code size, compile time, and LLVM optimization time
  - [ ] require regressions against Rust to be triaged before marking a pass complete
  - [ ] document cases where Stark beats C because range, ownership, alias, law, or closed-world facts are stronger than C can express
