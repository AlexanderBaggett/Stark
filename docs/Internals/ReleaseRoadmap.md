# Stark Release Roadmap

This document tracks release-oriented work that should be handled as its own
roadmap, separate from the long-running implementation roadmap.

Completion rules:

- Keep runtime performance and correctness as release blockers.
- Do not leave both stable and experimental standard library implementations in
  place after promotion work is complete.
- Prefer safe language features in the standard library. Raw pointers should be
  used only at FFI, OS, compiler-runtime, or explicitly unsafe backend
  boundaries.
- Every standard library migration task must update tests, benchmarks, package
  surfaces, and user-facing examples when behavior or names change.

## 1. Promote Experimental Standard Library

- [ ] Replace the current standard library with the experimental implementation.
  - [ ] Delete obsolete stable implementations after the replacement compiles.
  - [ ] Copy or move experimental modules into the canonical `System.*`
        namespace.
  - [ ] Remove temporary `System.Experimental.*` public surface unless a
        compatibility shim is explicitly needed for one release.
  - [ ] Update imports in examples, tests, benchmarks, and docs.
  - [ ] Preserve benchmark names and only report the language as `stark`.
  - [ ] Remove `stark-experimental` benchmark variants after promotion.
  - [ ] Run the full compiler, standard library, integration, and benchmark
        suites on Windows and Linux before closing this task.

### Module Promotion Checklist

- [ ] `System`
  - [ ] Replace re-exports with promoted experimental modules.
  - [ ] Remove experimental namespace aliases once consumers are updated.
  - [ ] Verify package image manifests contain only canonical modules.
- [ ] `System.BitOperations`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Audit APIs against the new range and unsafe rules.
  - [ ] Keep or port the module into the promoted standard library.
- [ ] `System.Collections`
  - [ ] Promote `System.Experimental.Collections`.
  - [ ] Update collection tests and benchmarks.
  - [ ] Verify list, stack, queue, linked list, and dictionary performance.
- [ ] `System.Console`
  - [ ] Promote `System.Experimental.Console`.
  - [ ] Preserve redirected output behavior.
  - [ ] Verify Windows console and Linux terminal behavior.
- [ ] `System.FileSystem`
  - [ ] Promote `System.Experimental.FileSystem`.
  - [ ] Preserve directory enumeration correctness and performance.
  - [ ] Verify first-entry, empty-directory, Unicode, long-name, and close paths.
- [ ] `System.IO`
  - [ ] Promote `System.Experimental.IO`.
  - [ ] Update all dependent imports.
  - [ ] Verify `IOStatus` and `IOResult<T>` behavior remains source-compatible.
- [ ] `System.IO.File`
  - [ ] Promote `System.Experimental.IO.File`.
  - [ ] Verify buffered and unbuffered read/write paths.
  - [ ] Verify ordinary close no longer performs durable flush work.
- [ ] `System.IO.Path`
  - [ ] Promote `System.Experimental.IO.Path`.
  - [ ] Verify Windows, Linux, and future macOS path separators and normalization.
  - [ ] Keep Unicode and long-path behavior covered by tests.
- [ ] `System.Math`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Audit APIs against range notation and integer-width rules.
  - [ ] Keep or port the module into the promoted standard library.
- [ ] `System.Memory`
  - [ ] Promote `System.Experimental.Memory`.
  - [ ] Verify allocator attributes, realloc behavior, bucket reuse, and dynamic
        memory primitives.
  - [ ] Re-run allocator benchmarks against C and Rust.
- [ ] `System.Net`
  - [ ] Promote `System.Experimental.Net`.
  - [ ] Verify socket startup and shutdown behavior on each OS.
  - [ ] Keep raw socket handles behind safe owned abstractions.
- [ ] `System.Net.Tcp`
  - [ ] Promote `System.Experimental.Net.Tcp`.
  - [ ] Verify scalar and vectored TCP paths.
  - [ ] Re-run loopback throughput benchmarks.
- [ ] `System.Process`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Audit process exit and process ID APIs against promoted runtime platform
        boundaries.
  - [ ] Keep or port the module into the promoted standard library.
- [ ] `System.Runtime`
  - [ ] Promote `System.Experimental.Runtime` where applicable.
  - [ ] Preserve runtime helper imports and compiler-known symbols.
  - [ ] Verify package image behavior.
- [ ] `System.Runtime.Buffer`
  - [ ] Promote `System.Experimental.Runtime.Buffer`.
  - [ ] Verify fixed and dynamic buffer behavior.
  - [ ] Re-run runtime buffer benchmarks.
- [ ] `System.Runtime.ConsoleInput`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Audit raw input buffers and platform calls.
  - [ ] Keep or port the module into the promoted standard library.
- [ ] `System.Runtime.Platform`
  - [ ] Promote dispatch surface changes required by experimental modules.
  - [ ] Keep OS-specific APIs internal.
  - [ ] Verify Linux, Windows, and macOS dispatch parity.
- [ ] `System.Runtime.Platform.Linux`
  - [ ] Preserve Linux behavior while accepting promoted API signatures.
  - [ ] Verify syscall wrappers and file, directory, console, memory, thread,
        and socket paths.
- [ ] `System.Runtime.Platform.Windows`
  - [ ] Preserve Windows behavior while accepting promoted API signatures.
  - [ ] Verify Kernel32, NtDll, Winsock, console, file, directory, memory,
        thread, and socket paths.
- [ ] `System.Syscall`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Keep syscall APIs isolated to platform/runtime internals where possible.
  - [ ] Verify user-facing code does not need raw syscall handles.
- [ ] `System.Text`
  - [ ] Promote `System.Experimental.Text`.
  - [ ] Verify owned text, views, append, format, copy, and Unicode paths.
  - [ ] Re-run text benchmarks.
- [ ] `System.Threading`
  - [ ] Confirm whether no experimental replacement is needed.
  - [ ] Audit thread entry, join, detach, yield, and sleep APIs against unsafe and
        raw-pointer rules.
  - [ ] Keep or port the module into the promoted standard library.

## 2. Require `unsafe` For Raw Pointer Use

- [ ] Enforce an `unsafe` requirement for raw pointer use.
  - [ ] Define every operation that requires `unsafe`:
        `rawptr`, `rawmutptr`, dereference, pointer arithmetic, pointer casts,
        bounded raw pointer region construction, `null`, and raw FFI handles.
  - [ ] Decide whether raw pointer type names in declarations require `unsafe`
        or whether only construction, dereference, and mutation require it.
  - [ ] Update grammar and syntax model if `unsafe` blocks/functions are not
        already represented everywhere needed.
  - [ ] Add semantic validation diagnostics for raw pointer use outside unsafe
        contexts.
  - [ ] Require explicit unsafe context at FFI and platform boundaries.
  - [ ] Add diagnostics that explain the safe alternatives: borrow, slice,
        dynamic, owned handle, or platform wrapper.
  - [ ] Add parser, semantic, ownership, lowering, and codegen tests.
  - [ ] Update language reference, book, and style guide.

## 3. Remove Unnecessary Raw Pointers From The Standard Library

- [ ] Disallow unnecessary raw pointer use in the standard library.
  - [ ] Define allowed raw pointer zones: FFI declarations, OS platform modules,
        runtime allocation hooks, compiler-known ABI helpers, and carefully
        audited unsafe internals.
  - [ ] Prefer `dynamic`, slices, borrowed values, fixed buffers, and owned
        handles everywhere else.
  - [ ] Add standard library audit tests that fail on unexpected raw pointer
        usage outside allowlisted files or functions.
  - [ ] Document every remaining raw pointer with the boundary it serves.

### Raw Pointer Replacement Checklist

- [ ] `System`
  - [ ] Remove raw pointer re-exports from public surface unless required.
- [ ] `System.BitOperations`
  - [ ] Replace raw pointer helpers with value or slice APIs where present.
- [ ] `System.Collections`
  - [ ] Replace internal raw storage with `dynamic` or safe storage wrappers
        wherever possible.
- [ ] `System.Console`
  - [ ] Keep raw handles internal to platform calls.
  - [ ] Use slices or dynamic buffers for user-facing write paths.
- [ ] `System.FileSystem`
  - [ ] Hide directory and file system handles behind owned types.
  - [ ] Replace raw entry buffers with dynamic or fixed safe buffers.
- [ ] `System.IO`
  - [ ] Keep public IO contracts free of raw pointers.
- [ ] `System.IO.File`
  - [ ] Replace file buffers with slices, dynamic storage, or owned buffers.
  - [ ] Keep OS handles internal.
- [ ] `System.IO.Path`
  - [ ] Replace raw path buffers with dynamic text or fixed safe buffers.
- [ ] `System.Math`
  - [ ] Ensure math APIs remain raw-pointer free.
- [ ] `System.Memory`
  - [ ] Keep raw allocation pointers internal to allocator implementation.
  - [ ] Expose `dynamic` memory primitives instead of raw allocation plumbing.
- [ ] `System.Net`
  - [ ] Hide socket handles behind owned socket types.
- [ ] `System.Net.Tcp`
  - [ ] Replace raw socket buffers with slices or vectored safe wrappers.
- [ ] `System.Process`
  - [ ] Keep process APIs raw-pointer free.
- [ ] `System.Runtime`
  - [ ] Allow raw pointers only for compiler/runtime ABI hooks.
- [ ] `System.Runtime.Buffer`
  - [ ] Prefer dynamic and fixed buffers over raw pointer storage.
- [ ] `System.Runtime.ConsoleInput`
  - [ ] Keep OS handle access internal and unsafe.
- [ ] `System.Runtime.Platform`
  - [ ] Keep raw pointers internal and explicitly unsafe.
- [ ] `System.Runtime.Platform.Linux`
  - [ ] Audit syscall buffers and handles.
  - [ ] Wrap raw regions in narrow unsafe helpers.
- [ ] `System.Runtime.Platform.Windows`
  - [ ] Audit Kernel32, NtDll, Winsock, and console buffers.
  - [ ] Wrap raw regions in narrow unsafe helpers.
- [ ] `System.Syscall`
  - [ ] Restrict or internalize user-facing raw syscall APIs.
- [ ] `System.Text`
  - [ ] Replace raw text storage with dynamic/owned text and slices.
- [ ] `System.Threading`
  - [ ] Hide thread handles behind owned thread types.

## 4. Enforce Integer Range Issues As Compile-Time Errors

- [ ] Make invalid or unnecessarily wide integer range declarations compile-time
      errors.
  - [ ] Define the exact rule for oversized storage ranges. Example:
        `i64[0 128]` should be rejected when a narrower integer type can express
        the declared range and no ABI, pointer-size, or platform reason is
        documented.
  - [ ] Add an escape hatch or annotation only for ABI/platform cases that truly
        require a specific width.
  - [ ] Reject impossible ranges, inverted ranges, endpoints outside the base
        integer type, and endpoints that force unnecessary storage width.
  - [ ] Emit diagnostics that suggest the smallest valid integer type in the error message.
  - [ ] Update constant folding and range inference so exponent endpoints such
        as `(2**63) - 1` are validated before lowering.
  - [ ] Add tests for locals, fields, parameters, return types, arrays, generic
        instantiations, casts, and inferred expressions.

### Standard Library Integer Range Audit

- [ ] `System`
- [ ] `System.BitOperations`
- [ ] `System.Collections`
- [ ] `System.Console`
- [ ] `System.FileSystem`
- [ ] `System.IO`
- [ ] `System.IO.File`
- [ ] `System.IO.Path`
- [ ] `System.Math`
- [ ] `System.Memory`
- [ ] `System.Net`
- [ ] `System.Net.Tcp`
- [ ] `System.Process`
- [ ] `System.Runtime`
- [ ] `System.Runtime.Buffer`
- [ ] `System.Runtime.ConsoleInput`
- [ ] `System.Runtime.Platform`
- [ ] `System.Runtime.Platform.Linux`
- [ ] `System.Runtime.Platform.Windows`
- [ ] `System.Syscall`
- [ ] `System.Text`
- [ ] `System.Threading`

## 5. Normalize Standard Library Range Notation

- [ ] Make standard library integer ranges use exponentiation or `[min max]`.
  - [ ] Replace large literal endpoints such as
        `-9223372036854775808 9223372036854775807` with `[min max]` when the full
        primitive range is intended.
  - [ ] Use exponentiation for explicit numeric bounds where the exact value is
        meaningful, such as `(2**31) - 1`.
  - [ ] Prefer the narrowest integer type that expresses the range.
  - [ ] Add format/lint tests that prevent regression to giant literal bounds.
  - [ ] Update docs and examples to model the new style.

### Range Notation Module Checklist

- [ ] `System`
- [ ] `System.BitOperations`
- [ ] `System.Collections`
- [ ] `System.Console`
- [ ] `System.FileSystem`
- [ ] `System.IO`
- [ ] `System.IO.File`
- [ ] `System.IO.Path`
- [ ] `System.Math`
- [ ] `System.Memory`
- [ ] `System.Net`
- [ ] `System.Net.Tcp`
- [ ] `System.Process`
- [ ] `System.Runtime`
- [ ] `System.Runtime.Buffer`
- [ ] `System.Runtime.ConsoleInput`
- [ ] `System.Runtime.Platform`
- [ ] `System.Runtime.Platform.Linux`
- [ ] `System.Runtime.Platform.Windows`
- [ ] `System.Syscall`
- [ ] `System.Text`
- [ ] `System.Threading`

## 6. Add macOS Standard Library Platform Backend

- [ ] Create a macOS OS-backed platform implementation.
  - [ ] Add `System.Runtime.Platform.MacOS.stark`.
  - [ ] Add a macOS dispatch template.
  - [ ] Add target detection and package image support for macOS triples.
  - [ ] Implement file open, read, write, seek, close, and flush.
  - [ ] Implement directory create, delete, open, read, and close.
  - [ ] Implement path normalization, current directory, existence, file kind,
        and metadata APIs.
  - [ ] Implement console stdout, stderr, stdin, terminal detection, and Unicode
        handling.
  - [ ] Implement memory allocation and reallocation using the chosen macOS
        backend.
  - [ ] Implement process exit and process ID.
  - [ ] Implement threading: start, join, detach, yield, and sleep.
  - [ ] Implement TCP sockets and readiness behavior.
  - [ ] Implement time or timing hooks needed by benchmarks.
  - [ ] Add macOS-specific correctness tests for each standard library module.
  - [ ] Add macOS benchmark runs to compare Stark, C, and Rust.
  - [ ] Document macOS platform behavior and unsupported APIs.

## 7. Update Website Book

- [ ] Update the book portion of the website.
  - [ ] Convert the book plan into website pages with stable URLs.
  - [ ] Make every chapter a tutorial that builds on previous chapters.
  - [ ] Include multiple code examples per chapter.
  - [ ] Add compile checks for code examples where possible.
  - [ ] Add navigation, previous/next links, and version/release labels.
  - [ ] Keep the language reference separate from tutorial material.

### Chapter Checklist

- [ ] Chapter 1: Introduction: Why Stark Exists
- [ ] Chapter 2: Installing Stark and Building Programs
- [ ] Chapter 3: Hello, Stark
- [ ] Chapter 4: A Small Stark Tour
- [ ] Chapter 5: Values, Types, and Ranges
- [ ] Chapter 6: Bindings, Mutation, and Control Flow
- [ ] Chapter 7: Ownership, Moves, and Drops
- [ ] Chapter 8: Borrowing in Stark
- [ ] Chapter 9: Stark Borrowing Compared With Rust
- [ ] Chapter 10: Storage Classes and Lifetimes
- [ ] Chapter 11: Aggregates and Layout-Aware Design
- [ ] Chapter 12: Enums and Pattern Matching
- [ ] Chapter 13: Arrays, Slices, Text, and Views
- [ ] Chapter 14: Modules, Visibility, and Packages
- [ ] Chapter 15: Function Guarantees and Effects
- [ ] Chapter 16: Errors Without Exceptions
- [ ] Chapter 17: Generics, Traits, Doctrines, and Specialization
- [ ] Chapter 18: Callable Values and Thread Entries
- [ ] Chapter 19: FFI, Raw Pointers, and Native Packages
- [ ] Chapter 20: Console, Process, and Platform Basics
- [ ] Chapter 21: Memory and Collections
- [ ] Chapter 22: Files, Directories, Paths, and Text
- [ ] Chapter 23: Threading and TCP
- [ ] Chapter 24: Testing Stark Code
- [ ] Chapter 25: Stark's Performance Model
- [ ] Chapter 26: Memory Layout, ABI, and Interop Expectations
- [ ] Chapter 27: Integer, Floating-Point, and Overflow Policy
- [ ] Chapter 28: Reading Stark Diagnostics
- [ ] Chapter 29: Looking at Generated IR
- [ ] Chapter 30: Project: Command-Line Text Tool
- [ ] Chapter 31: Project: Multi-Module Package
- [ ] Chapter 32: Project: File Processing Utility
- [ ] Chapter 33: Project: Native-Backed Package
- [ ] Chapter 34: Project: Performance Case Study
- [ ] Appendices
  - [ ] Keywords and reserved words
  - [ ] Operators and symbols
  - [ ] Integer widths and range rules
  - [ ] Function kinds and guarantees
  - [ ] Storage classes and ownership quick reference
  - [ ] Package manifest reference
  - [ ] Current boundaries
  - [ ] Stark for Rust programmers
  - [ ] Stark for C# programmers
  - [ ] Stark for C programmers

## 8. GitHub Release Pipeline

- [ ] Create GitHub Actions release pipeline for Linux, Windows, and macOS.
  - [ ] Add build matrix for supported host and target triples.
  - [ ] Build compiler binaries for Linux, Windows, and macOS.
  - [ ] Build and package the promoted standard library.
  - [ ] Run parser, compiler, standard library, and integration tests.
  - [ ] Run focused smoke benchmarks or runtime smoke tests per OS.
  - [ ] Package release archives with compiler, standard library, templates,
        docs, examples, and license files.
  - [ ] Generate checksums for every artifact.
  - [ ] Add version stamping from tags.
  - [ ] Generate draft release notes from changelog or commit metadata.
  - [ ] Upload artifacts to GitHub Releases.
  - [ ] Add manual dispatch for release candidates.
  - [ ] Add post-release install smoke tests that download the artifacts and
        compile a small Stark program on each OS.
  - [ ] Cache toolchains and dependencies without making release outputs depend
        on stale caches.
