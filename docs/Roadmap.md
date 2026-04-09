# Stark Roadmap

Remember this languge aims to be faster than idiomatic C or Rust on most projects, we must chose the best posible optimization strategy and explore optimization opportunities.

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
- [ ] full `ascii` / `unicode` runtime/value model
  - [x] concrete layout for `ascii` and `unicode`
  - [ ] indexing and slicing semantics
  - [x] literal storage and encoding guarantees
  - [ ] stdlib helpers that depend on the text layout model

  

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
- Remember to reference StandardLibrary.md
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
- [ ] Generic function/type instantiation strategy
  - [x] function instantiation triggers
  - [x] type instantiation triggers
  - [x] cross-module instantiation ownership
  - [x] caching and deduplication of instantiations
  - [x] Generic type parameter handling beyond basic shape support in `v1.1`
  - [x] bind generic parameters on all declaration kinds that support them
  - [x] substitute generic parameters through fields, returns, and locals
  - [ ] instantiate generic functions and types at use sites
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
- [ ] Compiler-owned package image architecture
  - [ ] Reframe the current package manifest as a broader package image artifact rather than a lossy package index
  - [ ] Define the package image principles and invariants
    - [ ] keep the artifact text-based and diffable in Git
    - [ ] do not add an embedded format-version field; the compiler and image format evolve together in source control
    - [ ] make the image sectioned so new compiler data can be added without collapsing into one flat record type
    - [ ] make direct compiler loading the primary path instead of reconstructing fake Stark source from lossy strings
  - [ ] Design a Stark-native, near-homoiconic package image syntax
    - [ ] represent package and module boundaries explicitly
    - [x] represent exported and public source surface explicitly
    - [x] represent typed compiler-owned sections explicitly rather than hiding them inside string fields
    - [ ] define which sections are human-authored, compiler-emitted, or compiler-only
  - [ ] Add structured typed-interface sections
    - [x] encode types structurally instead of rendering them as plain strings
    - [x] encode functions, methods, globals, types, and aliases with visibility, generics, modifiers, and symbol names
    - [x] preserve primary-constructor type shape across package boundaries so imported generic bodies can construct published records without source
    - [x] encode re-exports and other package-boundary dependency surface directly
    - [x] preserve enough surface information that docs, tooling, and diagnostics do not need to recover it from lowered compiler facts
  - [ ] Add compiler fact sections
    - [x] carry function effects and calling-convention facts across package boundaries
    - [x] carry ABI-lowering facts that should survive package publication
    - [x] carry aggregate and enum layout facts needed for downstream lowering and optimization
    - [x] carry ownership and borrow-related facts that are required for downstream validation or optimization
  - [ ] Add generic template body sections
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
    - [x] publish a first typed template-body subset for public/export generic functions and methods covering simple helper bodies such as literal returns, explicit conversion returns, unary operator returns including direct raw-pointer dereference and address-of over the same supported addressable target subset, binary operator returns including exponentiation, equality and ordered comparison chains with once-evaluated semantics, conditional returns including binary/logical conditions, object-creation returns including supported nested object-initializer and fixed-array member initializers, named enum-constructor returns including literal payload members, enum-call returns, enum-value returns, direct-call returns, member-call returns, field-access returns, simple index-access returns over already-supported MIR indexable families, including the full currently supported text postfix bracket family (`text[]`, `text[index]`, and `text[start, length]`), simple chained field/index/member receiver forms, grouped-expression receiver forms, and direct-call-result or object-creation-result receiver forms, discarded supported expression statements for void or non-void temporaries, assignment expressions over the same supported local and named-root field/index target subset as published assignment statements plus raw-pointer dereference-root targets including projected field/index chains rooted at `*expr`, explicit `return;` in void helpers, local `const` helpers, grouped local variable declarators including uninitialized declarators, object-initializer variable initializers, and other supported initializers, grouped local constant declarators with supported initializers, simple local-update helpers with supported local reassignment and simple `if`/`else`, `while`, or `for` control flow, including grouped `for`-initializer local declarators with uninitialized declarators, object-initializer variable initializers, or other supported initializers plus structural `break` and `continue` inside the loop subset, terminal non-void `if`/`else` chains whose branches all return, and simple switch-pattern helpers over already-published enum and aggregate pattern facts, including literal field tests and nested enum/aggregate field subpatterns, that end in a return or other terminal structured control-flow return
    - [ ] publish typed template bodies for exported or public generic functions and methods
    - [ ] preserve enough local, type, and effect information to specialize imported generics without reparsing source text
    - [ ] define explicit rules for which templates are published and which stay package-private
    - [ ] ensure the template-body representation is suitable for future optimization passes, not just minimal code generation
  - [ ] Integrate package images into module loading and the compiler pipeline
    - [x] load package image data directly into compiler artifacts
    - [x] prefer package-image loading over synthetic source reconstruction whenever rich sections are available
    - [x] let the temporary source bridge use authored source-surface overload identity to recover published generic template bodies even when declaration emission still uses canonical typed-interface spellings
    - [x] carry published overload identity and generic-body availability directly in typed-interface function and method entries so structured loading and temporary bridge recovery no longer require source-surface function/type entries for the supported imported generic path
    - [x] omit duplicated raw generic body text from package images when the published typed template body already covers the supported imported specialization path, and keep textual bridge fallback only for templates that still need it
    - [x] let the temporary source bridge omit imported generic body text when a published typed template body is sufficient for downstream type checking and MIR lowering, including simple module-qualified direct-call helpers and receiver-style member-call helpers, and keep that supported imported generic subset declaration-only during structured package-image loading instead of re-rendering fake source bodies
    - [x] preserve authored hot/cold/inline modifier identity directly in typed-interface loading and temporary bridge reconstruction so imported planning and declaration recovery do not depend on compiler-fact sections for that surface
    - [x] centralize source-surface fallback so explicit source-surface sections win over legacy flat surface fields, while older flat source-surface data still preserves authored overload identity when explicit sections are missing
    - [x] emit new package images with explicit source-surface sections as the primary surface representation instead of duplicating authored surface data into legacy flat fields
    - [x] emit new package images with explicit compiler sections as the primary compiler-owned representation instead of duplicating typed interface, compiler facts, and generic templates into legacy flat fields
    - [ ] keep legacy manifest reconstruction only as a temporary bridge while the package-image path is being completed
    - [ ] remove synthetic-source dependence from manifest-backed generic, alias, doctrine, and trait imports once the package-image path is complete
      - [x] resolve imported public/export type aliases from package-image typed-interface facts instead of reparsed bridge alias declarations
      - [x] resolve imported public/export globals from package-image typed-interface facts instead of reparsed bridge global declarations
      - [x] resolve imported public/export named type shape and record primary-constructor data from package-image typed-interface facts instead of reparsed bridge type declarations
      - [x] resolve imported explicit struct and record constructor signatures from package-image typed-interface facts instead of relying on bridge constructor declarations
      - [x] resolve imported trait/doctrine method signatures from package-image typed-interface facts instead of reparsed bridge declarations
  - [ ] Use package images to finish generic code generation across package boundaries
    - [x] emit consumer-owned specializations from imported generic template bodies
    - [x] support recursive and nested specialization expansion when templates come from package images
    - [x] define ownership, linkage, and dedup rules when one package publishes templates and another package owns concrete specializations
    - [x] ensure the package-boundary generic path stays zero-cost at runtime and does not introduce fallback indirection
  - [ ] Use package images to improve optimizer capability
    - [x] use imported concrete layout facts during monomorphization planning so manifest-backed large by-value generic instantiations do not get treated like trivially inline helpers
    - [x] let specialization planning consume imported effect, ABI, and layout facts directly instead of re-deriving them from stringly data
    - [x] publish weighted generic body-cost summaries so imported monomorphization planning does not rely only on top-level statement count
    - [x] extend caller-clone lowering from imported doctrine members to imported top-level law-style helpers and root-owned package-backed specialization symbols
    - [ ] preserve enough information for future cross-package inlining or richer package-aware optimizations beyond the current caller-clone, effective-kind, and semantic-summary surface
    - [x] keep package publication from throwing away semantic, call-graph, and planning facts that are expensive for the compiler to recover later
  - [ ] Tooling, inspection, and diagnostics for package images
    - [x] emit package images from the CLI and standard-library packaging flow
    - [x] add a readable dump or inspect mode for package images
    - [x] add diagnostics for missing required sections, malformed structured facts, or unsupported package-image content
    - [ ] document the package image as a compiler-owned source artifact rather than a narrow distribution manifest
  - [ ] Test coverage for package images
    - [x] writer or loader round-trip tests for rich package images
    - [x] direct-import tests that no longer synthesize fake source when rich package-image sections are present
    - [x] end-to-end tests for imported generic specialization from package images
    - [x] compatibility tests for the temporary legacy manifest bridge while both paths coexist

## Suggested Near-Term Execution Order

If the goal is to make Stark feel substantially more complete as a language, the recommended next order is:

- [ ] Real type/layout/ABI lowering
- [ ] Field/index/object lowering on top of that
- [x] Real multi-file module loading and imported symbol binding
- [ ] Minimal runtime surface
- [ ] Doctrine/trait optimization
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
- [ ] Define the public module layout for `System.Collections`including Stack, Queue, List, Linked List, and Dictionary
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


## Milestone v2.0 TBD

### Constrained Generics

- [ ] Trait/doctrine constraint solving
  - [ ] collect obligations from generic and doctrine use sites
  - [ ] candidate lookup and matching
  - [ ] ambiguity and no-solution diagnostics
  - [ ] instantiate solved obligations into lowered calls
- [ ] `where`-clause semantic binding and validation
- [ ] define specialization interaction with constrained generic instantiation
- [ ] Template string literals via (ascii/unicode) via `$`
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

- [ ] Interprocedural MIR/SSA inlining
  - [ ] inline tiny wrapper, `law`, and monomorphized helper bodies before LLVM
  - [ ] use a performance-first cost model that prefers runtime speed over code size and compile time
  - [ ] clone hot callees when constant arguments or closed-world facts create faster specialized bodies
  - [ ] add regression tests that wrapper abstractions disappear from MIR, SSA, and LLVM

- [ ] Full SSA global value numbering and redundancy elimination
  - [ ] add cross-block value numbering for pure expressions
  - [ ] eliminate redundant loads using ownership, `noalias`, and future `captures(...)` facts
  - [ ] implement PRE and FRE for partially and fully redundant expressions
  - [ ] reassociate arithmetic and bitwise expressions to expose more common subexpressions

- [ ] MIR/SSA scalar replacement of aggregates
  - [ ] implement real SROA for stack locals, temporaries, and small enum payloads before ABI lowering
  - [ ] scalar-replace eligible small aggregates across call boundaries
  - [ ] remove address-taken artifacts that exist only to service copies, returns, or temporary borrows
  - [ ] add regression tests for struct, record, fixed-array, and enum scalarization

- [ ] Destination propagation and result-location optimization
  - [ ] forward object and fixed-array construction directly into final destinations
  - [ ] propagate return/result slots backward through wrappers and helper calls
  - [ ] eliminate copy-like temporaries introduced by assignment chains and aggregate updates
  - [ ] add regression tests for constructor, call, and copy elision

- [ ] Memory-aware dead-store and drop cleanup
  - [ ] perform dead-store elimination for locals, fields, and scalarized aggregate lanes
  - [ ] remove stores overwritten before any observable read
  - [ ] elide destructor/drop work for trivially droppable or already-disarmed values
  - [ ] collapse redundant lifetime markers and no-op drop scaffolding

- [ ] Proof/range propagation engine
  - [ ] add integer range and known-bits reasoning
  - [ ] add enum tag/value correlation reasoning
  - [ ] add non-null and noalias-derived pointer facts for raw-pointer fast paths
  - [ ] feed proven facts back into branch simplification and redundancy elimination

- [ ] Safety-check elimination on top of proof facts
  - [ ] remove redundant bounds checks for fixed arrays, slices, and text indexing once check lowering exists
  - [ ] remove redundant discriminant and tag checks
  - [ ] remove dominated duplicate raw-pointer guard checks where Stark semantics introduce them
  - [ ] add regression tests for removed checks in MIR, SSA, and LLVM

- [ ] Loop optimization pipeline before LLVM
  - [ ] perform loop-invariant code motion for pure operations and proven-safe loads
  - [ ] simplify induction variables and apply strength reduction
  - [ ] implement loop unswitching, peeling, and rotation for hot loops
  - [ ] canonicalize loops specifically to maximize LLVM loop-vectorizer and SLP uptake

- [ ] Branch shaping and predication
  - [ ] implement jump threading from proven branch conditions
  - [ ] perform if-conversion of hot diamonds into `select` and other branchless forms where profitable
  - [ ] normalize boolean and compare chains into backend-friendly branch shapes
  - [ ] add regression tests for branchless lowering and fewer hot-path branches

- [ ] Tail-call and recursion elimination
  - [ ] convert self-tail-recursion into loops
  - [ ] preserve sibling-tail-call eligibility through Stark ABI lowering
  - [ ] add regression tests for tail-recursive wrappers and recursive state machines

- [ ] Escape analysis and allocation elimination once `heap` and `arena` lowering are real
  - [ ] promote non-escaping heap allocations to stack storage
  - [ ] scalar-replace non-escaping heap and arena aggregates
  - [ ] eliminate temporary allocation wrappers introduced by future stdlib abstractions
  - [ ] add regression tests for heap-to-stack promotion and allocation removal

- [ ] Specialization-driven optimization after full generics and constrained generics land
  - [ ] run monomorphization-time constant propagation and clone specialization
  - [ ] add SpecConstr-style specialization for recursive functions over known enum and state shapes
  - [ ] erase doctrine and trait abstraction overhead when constraint resolution is compile-time complete
  - [ ] add regression tests comparing generic abstractions to hand-written monomorphic code

- [ ] Rule-based optimizer for pure and `law` code
  - [ ] support phase-controlled rewrite rules for `law` functions and selected stdlib combinators
  - [ ] add fusion and deforestation for future iterator, view, and text pipelines
  - [ ] emit diagnostics for rule firings, missed firings, and inhibited rewrites
  - [ ] add benchmark-backed regression tests for intermediate-structure elimination

- [ ] Equality-saturation experiments for selected hot kernels
  - [ ] build e-graph rewrite sets for arithmetic, bitwise, and pure `law` expressions
  - [ ] use an extraction cost model tuned for Stark ABI and LLVM lowering realities
  - [ ] confine usage to explicit optimization tiers or hot kernels until it proves broadly useful
  - [ ] compare extracted code against conventional GVN, PRE, and reassociation on representative workloads
