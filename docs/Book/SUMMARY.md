# The Stark Book

This file is the single source of truth for the planned contents of the Stark
Book.

The book should have Rust Book-like coverage, but it must be centered on
Stark's own design: restrictive semantics, explicit storage, visible guarantees,
native interop, and performance guarantees.

## Audience

The book should assume the reader can read a C-like language, has basic command
line comfort, and wants to learn Stark as a performance-first systems language.
It should not assume Rust experience, though comparison notes may help Rust and
C# users understand Stark's choices.

## Coverage Rules

The book should cover the same broad learning territory as the Rust Book, but
with Stark-specific framing:

- write numbered chapters as ordered tutorial steps that build on previous
  chapters; keep appendices and language-reference pages separate from the
  tutorial voice
- replace a guessing-game style tutorial with an example that does not imply
  hidden allocation, hidden exceptions, dynamic callable indirection, or
  reflection
- teach explicit storage, integer ranges, and function guarantees early
- teach Stark's stricter default borrow escape rules instead of leading with
  Rust-style lifetime syntax
- teach `public` versus `export`, package manifests, solution manifests, and
  package-owned native metadata as first-class project concepts
- teach owned collections and allocator-aware growth instead of smart-pointer
  patterns
- teach result/status enums and trap-only unrecoverable failure instead of
  exception or unwinding patterns
- split implemented generic/function-pointer/lambda behavior from future
  constrained-generic and capture support
- teach the implemented `kind = "test"` project model, explicit `System.Testing`
  fact runners, and `stark test`
- teach callable values and current lambda boundaries without implying a
  Rust-style iterator ecosystem
- teach threading through `System.Threading` and safe thread entries; keep
  synchronization and shared mutable state scoped to what Stark actually
  supports
- mark async, macros, runtime object patterns, and OOP-style inheritance as
  absent from current tutorial examples unless roadmap items land

## Book Metadata

- Book Changes
  - current published draft version
  - user-facing changes between published drafts
  - links back to the canonical contents and v1.35 roadmap tracker

## Part I: First Contact

1. Introduction: Why Stark Exists
   - Stark's goals: C/Rust-class performance through restrictions
   - What Stark deliberately does not optimize for
   - The safe subset as the fast subset
   - How this book relates to the language reference
2. Installing Stark and Building Programs
   - repository build
   - compiler CLI
   - project manifests and solution manifests
   - `stark build` and `stark run`
   - `stark test`
3. Hello, Stark
   - first executable
   - `export fn main`
   - return codes
   - using `System.Console`
4. A Small Stark Tour
   - modules
   - functions, `finite`, `law`, and `finite law`
   - locals, mutability, and explicit storage
   - `if`, `while`, and `switch`

## Part II: Stark's Core Language

5. Values, Types, and Ranges
   - signed and unsigned integer widths
   - explicit integer ranges
   - floats and `strictfp`
   - bool
   - text literals and escape rules
6. Bindings, Mutation, and Control Flow
   - `stack` locals
   - mutable and immutable bindings
   - assignment and compound assignment
   - expression typing and conversions
   - loops, switches, and guaranteed return
7. Ownership, Moves, and Cleanup
   - owned values by default
   - move semantics
   - reinitialization after move
   - deterministic drop
   - copyable scalar values
8. Borrowing in Stark
   - `borrow`
   - `mut borrow`
   - `retborrow`
   - non-escaping borrows by default
   - null-free safe references
   - raw pointers as explicit boundary values
9. Stark Borrowing Compared With Rust
   - what Stark borrows from Rust's ownership model
   - where Stark is stricter: non-escaping default borrows, no safe null, no
     reference-counted standard escape hatch, narrower destructor behavior
   - what Rust features Stark does not have: lifetime syntax as a
     normal user tool, `Rc`, `RefCell`, dynamic trait objects, and general
     interior mutability
   - how Stark's `retborrow`, `frozen`, `out`, and explicit storage classes
     express intent differently
   - examples translated between Rust-like thinking and Stark code
   - why the restrictions matter for aliasing, allocation, and visible cost
10. Storage Classes and Lifetimes
    - `stack`
    - `heap`
    - `arena`
    - globals, `const`, `static`, and `static mut`
    - allocator-aware ownership
11. Structs, Records, and Layout-Aware Design
    - structs
    - records
    - field initializers
    - constructors and destructors
    - field access
    - representation stability and ABI boundaries
12. Enums and Pattern Matching
    - enum variants
    - payloads
    - switch patterns
    - exhaustiveness expectations
    - compact tag layout as an implementation freedom
13. Arrays, Slices, Text, and Views
    - fixed arrays
    - dynamic indexing
    - slices and slice views
    - no hidden backing storage for slice targets
    - `ascii`, `unicode`, `Ascii`, and `Unicode`
    - conversion and formatting APIs

## Part III: Packages, Effects, and Boundaries

14. Modules, Visibility, and Packages
    - one file per module
    - imports and re-exports
    - `internal`, `public`, and `export`
    - project manifests
    - solution manifests
    - package-backed dependencies
15. Function Guarantees and Effects
    - `fn`
    - `finite`
    - `law`
    - `finite law`
    - visible side effects
    - allocation, synchronization, and progress
16. Errors Without Exceptions
    - result and status enums
    - `out` parameters for fallible writes
    - traps and unrecoverable failure
    - no unwinding as a language and runtime constraint
17. Generics, Traits, and Doctrines
    - generic types and functions
    - generic enums and variant constructors
    - trait contracts
    - doctrine law helpers
    - choosing the right abstraction
18. Callable Values and Thread Entries
    - function items
    - function pointers
    - non-capturing lambdas
    - explicit capture-list lambdas
    - current capture limits
    - thread entry values
19. FFI, Raw Pointers, and Native Packages
    - `unsafe ffi fn`
    - raw pointers and `null`
    - bounded raw pointer region parameters
    - unsafe raw slice construction
    - package-owned native sources
    - `pkg-config` and native fallback paths
    - C ABI boundaries

## Part IV: The Standard Library

21. Console, Process, and Platform Basics
    - console output and input
    - byte-buffer console input and output
    - process exit
    - target-specific runtime boundaries
22. Memory and Collections
    - `System.Memory`
    - default allocator
    - byte and code-point memory helpers
    - `List<T>`, `Stack<T>`, `Queue<T>`, `LinkedList<T>`, and `Dictionary<K,V>`
    - dictionary key and removal patterns
    - ownership of collection elements
23. Files, Directories, Paths, and Text
    - `System.IO`
    - `System.IO.File`
    - `System.IO.Path`
    - `System.FileSystem`
    - path normalization and path facts
    - directory entry and directory-info iteration
    - owned text and path buffers
24. Threading and TCP
    - threads, join, detach, yield, and sleep
    - blocking TCP
    - safe slices at IO boundaries
    - byte-buffer, vectored, and wait-based TCP APIs
    - current synchronization gaps
25. Math Helpers
    - `System.Math`
    - `Sqrt`, `Pow`, `Min`, `Max`, rounding, `Sin`, `Cos`, and `FusedMultiplyAdd`
    - `SinCos` when both sine and cosine are needed
    - deterministic pseudo-random values with `XorShift32`
    - compact signature reference for the remaining math helpers
26. Bit Operations
    - `System.BitOperations`
    - leading zero count, trailing zero count, and population count
    - rotate-left and rotate-right
    - masks, flags, compact indexes, and hash-style mixing
27. Byte Buffers
    - `System.Runtime.Buffer`
    - fixed byte buffers and dynamic byte buffers
    - readable and writable cursor workflows
    - console, file, and TCP buffer handoff
28. Testing Stark Code
    - `kind = "test"` manifests
    - explicit `System.Testing`
    - xUnit-inspired assertions without hidden exceptions
    - `stark test`

## Part V: Performance and Systems Programming

29. Stark's Performance Model
    - why restrictions are part of the design
    - memory separation requirements and `if disjoint`
    - raw pointer region expressions in `where disjoint` and `if disjoint`
    - bounded raw pointer copy, fill, transform, and overlap-safe fallback paths
    - const parameters and deep immutability
    - `independent` loop requirements for slices, arrays, and bounded raw pointer regions
    - no hidden allocation
    - direct calls by default
    - no unwinding
    - safe code with visible costs
30. Memory Layout, ABI, and Interop Expectations
    - source-visible layout promises
    - ABI-visible `export`
    - C interop expectations
    - package boundaries
31. Integer, Floating-Point, and Overflow Policy
    - range-checked integers
    - wrapping and saturating operations
    - float conversions
    - `strictfp` versus default fast math
32. Performance Tuning
    - small measured kernels
    - `inline`, `inlinehint`, and `noinline`
    - default non-overlap, `where overlap`, `where same`, and `if disjoint`
    - bounded raw pointer regions in hot loops
    - benchmark and output-inspection workflow
33. Unsafe Stark and Raw Pointers
    - unsafe as an audited boundary
    - bounded raw pointer regions
    - scoped `assume disjoint(...)`
    - null and mutability boundaries
    - returning to safe Stark wrappers
34. Reading Stark Diagnostics
    - parse diagnostics
    - type diagnostics
    - ownership and borrow diagnostics
    - package/native dependency diagnostics
35. Inspecting Generated Output
    - output views as focused troubleshooting tools
    - when to inspect generated output
    - relating source choices to emitted behavior

## Part VI: Projects

36. Project: Command-Line Text Tool
    - arguments once the entrypoint model supports them
    - console and text processing
    - result/status handling
37. Project: Multi-Module Package
    - internal helpers
    - public package API
    - solution manifests
    - package consumption
38. Project: File Processing Utility
    - file open/read/write
    - directory inspection
    - owned handles and cleanup
39. Project: Native-Backed Package
    - Raylib/GLFW/SDL3/KbTextShape-style native adapter
    - package-owned native metadata
    - downstream executable build
40. Project: Performance Case Study
    - choose a tight loop
    - write Stark with explicit storage
    - compare against C/Rust-like implementation strategy
    - inspect emitted output and benchmark output

## Appendices

A. Keywords and Reserved Words
B. Operators and Symbols
C. Integer Widths and Range Rules
D. Function Kinds and Guarantees
E. Storage Classes and Ownership Quick Reference
F. Package Manifest Reference
G. Current Boundaries
H. Stark for Rust Programmers
I. Stark for C# Programmers
J. Stark for C Programmers
