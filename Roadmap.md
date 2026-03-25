# Stark Roadmap

This document is the working implementation roadmap for Stark.

It is intended to track:

- what already exists
- what is partially in place but not finished
- what still needs to be built before Stark feels like a complete language and compiler

The checkboxes below should be updated as work lands.

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
- [ ] Expand grammar coverage tests toward full language conformance

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
- [ ] Full type checking for all grammar-supported expression forms
- [ ] Better diagnostics for type mismatches and coercions
- [ ] Generic type parameter handling beyond basic shape support in `v1.1`

### Function Semantics and Validation

- [x] Function-effect derivation
- [x] `law` restrictions
- [x] `finite` restrictions
- [x] `ffi` effect boundary handling
- [x] `nounwind`/`mustprogress`/`willreturn` style derivation
- [ ] More complete derivation of parameter-level guarantees
- [ ] More precise per-call memory effect modeling

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
- [ ] More precise non-lexical-style lifetime analysis on normalized MIR/SSA
- [ ] Drop checking for richer aggregate types
- [ ] Better diagnostics for borrow conflicts and lifetime errors

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
- [ ] SSA cleanup / canonicalization pass
- [ ] Value numbering / common subexpression cleanup
- [ ] Constant propagation pass

### Control Flow Lowering

- [x] `if` lowering
- [x] `while` lowering
- [x] `for` lowering
- [x] `break` / `continue`
- [x] `return`
- [x] literal `switch` lowering
- [x] guarded `switch` lowering with `when`
- [x] `case var capture` lowering
- [ ] More advanced pattern lowering strategy

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
- [ ] character literal lowering
- [x] field access lowering
- [x] index access lowering
- [ ] member call lowering
- [ ] pointer/address operations beyond the current subset
- [ ] complete conversion lowering

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
- [ ] object-file and link-step configurability

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
- [ ] Stop mapping most named/aggregate types directly to `ptr`

### Memory and Access Lowering

- [x] Field load lowering
- [x] Field store lowering
- [x] Address-of / element-address lowering
- [x] Array indexing lowering
- [x] Slice indexing lowering
- [ ] Aggregate local allocation/lifetime strategy
- [ ] Aggregate copy/move semantics in lowering

### ABI and Calling Convention

- [x] Internal `fastcc` usage for non-FFI functions
- [x] `ffi` disables the default internal calling convention
- [x] Real ABI lowering for aggregates
- [x] Stable lowering rules for return-by-value vs indirect return
- [x] Parameter ABI rules for slices/strings/aggregates
- [x] Calling convention strategy across executable vs library boundaries

### Globals and Constants

- [x] String global emission for supported literals
- [ ] Real immutable global constant emission
- [ ] Real mutable global data emission
- [ ] Better linkage/visibility lowering for globals
- [ ] Constant aggregate initializers

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

### Standard Library Core

- [x] Define the first standard library module layout
- [x] `Console` or `Stdout` output abstraction
- [x] `Stderr` output abstraction
- [ ] File read API
- [ ] File write API
- [ ] Basic path/file error modeling
- [ ] String helpers required by the standard library

### Runtime Surface

- [ ] Define program entrypoint conventions beyond raw `ffi fn main`
- [ ] Panic/assert/failure story
- [ ] Exit code/runtime termination helpers
- [ ] Minimal allocator/runtime boundary if heap allocation is exposed
- [ ] FFI support library or conventions for common C interop

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
- [ ] object creation lowering
- [ ] object initializer lowering
- [ ] richer assignment target lowering
- [ ] full postfix/operator coverage

### Types and Patterns

- [x] literal switch patterns
- [x] discard/match-all switch patterns
- [x] `var` capture switch patterns
- [ ] richer sum/variant pattern matching
- [ ] first-class optional/result/sum-type lowering if those are part of the surface language plan

### Characters and Strings

- [x] string literal typing
- [x] string constant codegen for the supported path
- [ ] character literal lowering
- [ ] richer escape support and validation
- [ ] full `ascii` / `unicode` runtime/value model

## Milestone 6: Traits and Doctrine

Goal: Stark's abstraction system becomes real, optimizable, and usable.

- [ ] Finalize doctrine semantics in the compiler
- [ ] Trait/doctrine constraint solving
- [ ] Closed-world optimization rules for doctrines/traits
- [ ] Dynamic dispatch strategy, if any
- [ ] Better lowering for laws/doctrines as optimization-friendly abstractions

## Milestone 7: Optimization and Backend Quality

Goal: emitted LLVM becomes richer, more correct, and more competitive.

### Frontend Optimization Passes

- [ ] Constant folding
- [ ] Compile-time evaluation
- [ ] Dead code elimination before LLVM emission
- [ ] Better SSA cleanup/value numbering
- [ ] Simplify trivial branches and blocks
- [ ] Normalize more control-flow patterns before LLVM

### LLVM Semantic Richness

- [x] function-level `nounwind`/`nofree`/`nosync`/`willreturn`/`mustprogress` style emission for the supported cases
- [x] `hot` / `cold` / inline preference emission
- [ ] parameter-level `noalias`
- [ ] parameter-level `readonly` / `writeonly`
- [ ] parameter-level `nonnull`
- [ ] parameter-level `align`
- [ ] parameter-level `dereferenceable`
- [ ] `captures(...)` style escape-derived lowering
- [ ] better `memory(...)` precision

### Targeting and Code Generation Quality

- [x] native LLVM `switch` for simple integer/bool cases
- [ ] better aggregate lowering quality
- [ ] better global data lowering quality
- [ ] target-aware code generation options
- [ ] object emission and separate link steps
- [ ] optimization level controls
- [ ] debug info emission

## Milestone 8: Tooling, Diagnostics, and Developer Experience

Goal: Stark is pleasant to work on and pleasant to use.

### Tests

- [x] unit tests for parser/compiler pieces
- [x] IR/codegen regression tests
- [x] native hello-world validation path
- [ ] broader grammar coverage tests
- [ ] multi-file integration tests
- [ ] compile-and-run program suite
- [ ] diagnostics regression tests
- [ ] negative tests for borrow and ownership edge cases

### Compiler UX

- [x] CLI input support
- [x] `check`-only mode
- [x] `--emit-mir`
- [x] `--emit-ssa`
- [x] `--emit-llvm`
- [x] `--emit-obj`
- [x] `--emit-exe`
- [ ] better help/usage output
- [ ] structured diagnostic formatting
- [ ] source snippets in diagnostics

### Repository and Documentation

- [x] top-level design docs exist
- [x] grammar exists in source control
- [x] minimal getting-started guide in `README.md`
- [ ] roadmap kept up to date as work lands
- [ ] standard library roadmap expanded in `StandardLibrary.md`
- [ ] examples directory with canonical Stark programs

## Milestone 9: Release Readiness

Goal: Stark is not just a compiler experiment, but a coherent language/toolchain release.

- [ ] Define a minimum viable Stark language subset for a first release
- [ ] Freeze syntax for that subset
- [ ] Freeze lowering rules for that subset
- [ ] Document unsupported features explicitly
- [ ] Provide a versioned standard library baseline
- [ ] Add release notes / changelog discipline
- [ ] Add CI for build and tests
- [ ] Add sample projects that compile end-to-end

## Milestone v1.1: Post-Release Surface Additions

Goal: add non-essential language surface after the first release without slowing v1.0 completion.

- [ ] Type aliases
- [ ] Generic function/type instantiation strategy
- [ ] Monomorphization planning
- [ ] Specialization planning

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
