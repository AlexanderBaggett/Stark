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
  - [x] Remove `stark-experimental` benchmark variants after promotion.
  - [ ] Run the full compiler, standard library, integration, and benchmark
        suites on Windows and Linux before closing this task.

### Module Promotion Checklist

- [x] Replacement and namespace promotion.
  - [x] Promote `System.Experimental.Memory` into canonical `System.Memory`;
        keep the allocator ABI while exposing dynamic reserve, append, copy,
        move, fill, and disjoint helpers.
  - [x] Promote experimental implementations into canonical namespaces:
        `System.Collections`, `System.Console`, `System.FileSystem`,
        `System.IO`, `System.IO.File`, `System.IO.Path`, `System.Net`,
        `System.Net.Tcp`, `System.Runtime.Buffer`, and `System.Text`.
  - [x] Promote runtime and platform dispatch changes required by the
        experimental modules: `System.Runtime`, `System.Runtime.Platform`,
        `System.Runtime.Platform.Linux`, and `System.Runtime.Platform.Windows`.
  - [x] Confirm and keep or port modules with no experimental replacement:
        `System.BitOperations`, `System.Math`, `System.Process`,
        `System.Runtime.ConsoleInput`, `System.Syscall`, and
        `System.Threading`.
  - [x] Update `System` re-exports and public surface wiring after promoted
        modules land.

- [x] Standard library dependency rewiring.
  - [x] Update promoted experimental callers of memory helpers:
        `System.Experimental.Text`, `System.Experimental.Runtime.Buffer`, and
        `System.Experimental.IO.Path` now call canonical `System.Memory`.
  - [x] Replace all `System.Experimental.*` imports inside the standard library
        with canonical `System.*` imports after each promoted batch lands.
  - [x] Preserve source-compatible result and status types where needed, such as
        `IOStatus`, `IOResult<T>`, `MemoryStatus`, and `MemoryResult<T>`.
  - [x] Keep OS-specific APIs internal to platform/runtime modules.
  - [x] Preserve compiler-known runtime helper names or update compiler
        recognition at the same time as namespace promotion.

- [ ] Test and package updates.
  - [x] Add canonical `System.Memory` helper lowering, packaging, and executable
        coverage.
  - [x] Update text, runtime buffer, and path tests that consume promoted
        `System.Memory` helpers.
  - [x] Update source, executable, lowering, package-image, and integration
        tests for collections, console, filesystem, IO, net, runtime buffer,
        text, and platform batches.
  - [x] Verify package image manifests contain only canonical modules.
  - [x] Confirm no temporary compatibility shim is intentionally kept for this
        batch, so no shim-specific tests are required.
  - [ ] Run the full compiler, standard library, integration, and benchmark test
        suites on Windows and Linux before closing the promotion.

- [ ] Benchmark consolidation and experimental benchmark deletion.
  - [x] Replace canonical memory benchmarks with promoted helper-based
        implementations.
  - [x] Delete temporary memory experimental benchmarks:
        `ExperimentalMemoryCopyFill.stark` and
        `ExperimentalMemoryDynamicReserveGrowth.stark`.
  - [x] Preserve canonical benchmark names and report promoted Stark rows as
        language `stark`, not `stark-experimental`.
  - [x] Delete remaining experimental benchmark variants after their canonical
        standard-library modules are promoted.
  - [x] Update benchmark harness gates so promoted modules no longer require
        matching `Experimental*.stark` files.
  - [ ] Re-run focused benchmark smoke tests for each promoted batch against C
        and Rust.
  - [ ] Re-run the full benchmark suite after the promotion is complete.

- [ ] Behavioral and performance verification.
  - [x] Verify canonical `System.Memory` helper lowering keeps memcpy, memmove,
        memset, dynamic length commits, Windows heap declarations, and package
        attributes intact.
  - [x] Re-run focused `MemoryCopyFill` and `MemoryDynamicReserveGrowth`
        benchmark smoke tests against C and Rust.
  - [ ] Verify allocator attributes, realloc behavior, bucket reuse, and dynamic
        memory primitives in the full allocator suite.
  - [ ] Verify collection performance for list, stack, queue, linked list, and
        dictionary workloads.
  - [ ] Verify console redirected output, Windows console, and Linux terminal
        behavior.
  - [ ] Verify filesystem and IO behavior: buffered and unbuffered read/write,
        ordinary close without durable flush, directory enumeration correctness,
        Unicode, long-name, first-entry, empty-directory, and close paths.
  - [ ] Verify path behavior across Windows, Linux, and future macOS separators
        and normalization.
  - [ ] Verify networking behavior: socket startup/shutdown, scalar TCP paths,
        vectored TCP paths, and loopback throughput.
  - [ ] Verify runtime buffer fixed and dynamic behavior and benchmarks.
  - [ ] Verify text behavior: owned text, views, append, format, copy, Unicode,
        and path-related formatting.
  - [ ] Verify platform parity for Linux, Windows, and macOS dispatch surfaces.

- [ ] Cleanup and final removal.
  - [x] Delete `System.Experimental.Memory` after canonical `System.Memory`
        consumers and tests were updated.
  - [ ] Remove remaining `System.Experimental.*` public surface unless a
        compatibility shim is explicitly approved for one release.
  - [ ] Remove experimental namespace aliases after all consumers are canonical.
  - [ ] Remove temporary migration tests, docs, and benchmark gates that only
        existed to compare stable and experimental implementations.
  - [ ] Audit promoted modules against the new unsafe, raw-pointer, and range
        rules before release.

## 2. Require `unsafe` For Raw Pointer Use

- [x] Enforce an `unsafe` requirement for raw pointer use.
  - [x] Define every operation that requires `unsafe`:
        `rawptr`, `rawmutptr`, dereference, pointer arithmetic, pointer casts,
        bounded raw pointer region construction, `null`, and raw FFI handles.
  - [x] Decide whether raw pointer type names in declarations require `unsafe`
        or whether only construction, dereference, and mutation require it.
  - [x] Update grammar and syntax model if `unsafe` blocks/functions are not
        already represented everywhere needed.
  - [x] Add semantic validation diagnostics for raw pointer use outside unsafe
        contexts.
  - [x] Require explicit unsafe context at FFI and platform boundaries.
  - [x] Add diagnostics that explain the safe alternatives: borrow, slice,
        dynamic, owned handle, or platform wrapper.
  - [x] Add parser, semantic, ownership, lowering, and codegen tests.
  - [x] Update language reference.
  - [x] Update book and style guide.

## 3. Remove Unnecessary Raw Pointers From The Standard Library

- [ ] Disallow unnecessary raw pointer use in the standard library.
  - [x] Define allowed raw pointer zones: FFI declarations, OS platform modules,
        runtime allocation hooks, compiler-known ABI helpers, and carefully
        audited unsafe internals.
  - [x] Prefer `dynamic`, slices, borrowed values, fixed buffers, and owned
        handles everywhere else.
  - [x] Add standard library audit tests that fail on unexpected raw pointer
        usage outside allowlisted files or functions.
  - [x] Document every remaining raw pointer with the boundary it serves.

### Raw Pointer Replacement Checklist

Verified against `stdlib/src` in this pass. Checked items are complete in the
current source shape. Unchecked items still expose raw pointers publicly or keep
replaceable raw storage that should move behind `dynamic`, slices, owned values,
or a narrower explicitly unsafe boundary.

- [ ] `System`
  - [ ] Remove raw pointer re-exports from public surface unless required.
    - Still open: `System.stark` re-exports `System.IO.File` and
      `System.Text`, both of which still expose public raw-pointer APIs.
- [x] `System.BitOperations`
  - [x] Replace raw pointer helpers with value or slice APIs where present.
    - Verified raw-pointer free.
- [ ] `System.Collections`
  - [ ] Replace internal raw storage with `dynamic` or safe storage wrappers
        wherever possible.
    - [x] Replaced `Queue<T>` raw allocation storage with `dynamic T` storage
          in both stable and experimental collections.
    - [x] Verified `Stack<T>`, `RingQueue<T>`, and linked-list storage use
          `dynamic` storage instead of raw allocation storage.
    - [x] Documented `Dictionary<K, V>` raw sparse storage as the remaining
          collection raw pointer boundary.
    - [ ] Replace `Dictionary<K, V>` raw sparse key/value/state storage once
          `dynamic` can model sparse uninitialized slots with mutable borrows
          from occupied values.
- [x] `System.Console`
  - [x] Keep raw handles internal to platform calls.
    - Verified: stdin handle state is module-private and platform calls are the
      only raw handle consumers.
  - [x] Use slices or dynamic buffers for user-facing write paths.
    - Verified: public byte write/read APIs use slices, `DynamicByteBuffer`, or
      fixed runtime buffers.
- [ ] `System.FileSystem`
  - [x] Hide directory and file system handles behind owned types.
    - Verified: `Directory.Handle` is internal and the public surface returns
      owned `Directory` values.
  - [ ] Replace raw entry buffers with dynamic or fixed safe buffers.
    - Still open: `Directory` keeps an internal raw entry buffer plus
      `System.Memory.Allocation`.
- [x] `System.IO`
  - [x] Keep public IO contracts free of raw pointers.
    - Verified: the base `System.IO` result/status/error module is raw-pointer
      free; raw file APIs are tracked under `System.IO.File`.
- [ ] `System.IO.File`
  - [ ] Replace file buffers with slices, dynamic storage, or owned buffers.
    - Partially done: owned `File` read/write paths accept byte slices,
      `DynamicByteBuffer`, and fixed runtime buffers, but raw byte and region
      methods still remain on the stable file surface.
  - [ ] Keep OS handles internal.
    - Partially done: stable `File.Handle` is internal, but public unsafe
      handle helpers such as `OpenRead`, `Close`, `ReadBytes`, `WriteBytes`,
      `Seek`, `WriteText`, and `WriteLine` remain exported.
    - Also open: `System.Experimental.IO.File` is raw-free, but the
      `System.Experimental.IO` aggregator currently re-exports stable
      `System.IO.File`.
- [x] `System.IO.Path`
  - [x] Replace raw path buffers with dynamic text or fixed safe buffers.
    - Verified: public path APIs use `OwnedAscii`, text views, and value
      results; remaining raw pointers are internal read-only text scans.
- [x] `System.Math`
  - [x] Ensure math APIs remain raw-pointer free.
    - Verified raw-pointer free.
- [ ] `System.Memory`
  - [x] Keep raw allocation pointers internal to allocator implementation.
    - Verified: `Allocation` is internal.
  - [x] Expose `dynamic` memory primitives instead of raw allocation plumbing.
    - Verified: reserve, append, copy, move, and fill APIs operate on
      `dynamic`, slices, and initialized destinations.
  - [ ] Fence or replace public raw-pointer initialization helpers.
    - Still open: `InitializeBytesFromPointerDisjoint` and
      `InitializeCodePointsFromPointerDisjoint` remain public unsafe APIs.
- [x] `System.Net`
  - [x] Hide socket handles behind owned socket types.
    - Verified: the base networking module is raw-pointer free.
- [x] `System.Net.Tcp`
  - [x] Replace raw socket buffers with slices or vectored safe wrappers.
    - Verified: public reads/writes use byte slices, vectored slice APIs, or
      runtime buffers; socket handles are internal to `TcpClient` and
      `TcpListener`.
- [x] `System.Process`
  - [x] Keep process APIs raw-pointer free.
    - Verified raw-pointer free.
- [x] `System.Runtime`
  - [x] Allow raw pointers only for compiler/runtime ABI hooks.
    - Verified: raw pointers are confined to internal slice-part ABI structs and
      compiler-known slice extraction hooks.
- [ ] `System.Runtime.Buffer`
  - [x] Prefer dynamic and fixed buffers over raw pointer storage.
    - Verified: storage is `dynamic` or fixed arrays.
  - [ ] Remove or internalize stable fixed-buffer raw pointer accessors.
    - Still open: stable `FixedByteBuffer*` types expose unsafe `ReadPointer`,
      `ReadWritePointer`, and `WritePointer`; experimental buffers expose slices
      instead.
    - Also open: `System.Experimental.Runtime.Buffer` has the slice-only shape,
      but the `System.Experimental.Runtime` aggregator currently re-exports the
      stable buffer module.
- [x] `System.Runtime.ConsoleInput`
  - [x] Keep OS handle access internal and unsafe.
    - Verified: raw pointer helpers are module-private.
- [x] `System.Runtime.Platform`
  - [x] Keep raw pointers internal and explicitly unsafe.
    - Verified: platform dispatch functions are internal and raw consumers are
      unsafe.
- [x] `System.Runtime.Platform.Linux`
  - [x] Audit syscall buffers and handles.
  - [x] Wrap raw regions in narrow unsafe helpers.
    - Verified: Linux raw pointer use is internal unsafe platform and syscall
      handoff code.
- [x] `System.Runtime.Platform.Windows`
  - [x] Audit Kernel32, NtDll, Winsock, and console buffers.
  - [x] Wrap raw regions in narrow unsafe helpers.
    - Verified: Windows raw pointer use is internal unsafe platform and FFI
      handoff code.
- [ ] `System.Syscall`
  - [ ] Restrict or internalize user-facing raw syscall APIs.
    - Still open: `Syscall0` through `Syscall6` remain public unsafe direct
      syscall entry points even though their signatures use integer registers
      rather than typed raw pointers.
- [ ] `System.Text`
  - [ ] Replace raw text storage with dynamic/owned text and slices.
    - Partially done: `OwnedAscii` and `OwnedUnicode` provide owned/dynamic text
      surfaces.
    - Still open: public unsafe `AsciiData`, `UnicodeData`, caller-buffer
      formatters, UTF-16 conversion helpers, and `rawmutptr<Ascii>` /
      `rawmutptr<Unicode>` APIs remain in stable and experimental text.
- [x] `System.Threading`
  - [x] Hide thread handles behind owned thread types.
    - Verified: `Thread.Handle` is internal and public thread operations use the
      owned `Thread` type.

## 4. Enforce Integer Range Issues As Compile-Time Errors, using signed integers with positive-only range as compile time error, suggest use of unsigned integer instead.

- [x] Make invalid or unnecessarily wide integer range declarations compile-time
      errors by default.
  - [x] Add enforcement through `CompilerOptions.EnforceIntegerRangeStorageRules`
        and keep `--strict-integer-ranges` as a compatibility spelling for the
        default CLI behavior.
  - [x] Define the strict-mode rule for oversized storage ranges. Example:
        `i64[0 128]` should be rejected when a narrower integer type can express
        the declared range and no ABI, pointer-size, or platform reason is
        documented. use new `platform` keyword if required by abi contract to allow you to use a type you don't need to.
  - [x] Add an escape hatch or annotation only for ABI/platform cases that truly
        require a specific width. `[Platform]` declarations preserve ABI-required
        storage for signatures and aggregate fields without relaxing ordinary
        local, array, generic, or cast range checks.
  - [x] Reject impossible ranges, inverted ranges, endpoints outside the base
        integer type, and endpoints that force unnecessary storage width in strict mode.
  - [x] Emit diagnostics that suggest the smallest valid integer type in the error message.
  - [x] Update constant folding and range inference so exponent endpoints such
        as `2 ** 63 - 1` are validated before lowering.
  - [x] Add strict-mode tests for locals, fields, parameters, return types, arrays,
        generic instantiations, casts, signed-to-unsigned suggestions, narrower
        unsigned storage suggestions, signed narrowing suggestions, scalar const
        width/sign errors, and FFI ABI signature exemptions.
  - [x] Flip strict range enforcement on by default after the standard library
        integer range audit is complete.

### Standard Library Integer Range Audit

Completed against `stdlib/src` with the default strict integer range checks. Ordinary
non-negative signed ranges now use unsigned storage with the original upper
bounds preserved, and over-wide helper ranges now use the smallest signed or
unsigned storage that expresses them. Full-width signed ABI and syscall ranges
remain signed. Experimental mirrors were audited with the same rule.

- [x] `System`
- [x] `System.BitOperations`
- [x] `System.Collections`
- [x] `System.Console`
- [x] `System.FileSystem`
- [x] `System.IO`
- [x] `System.IO.File`
- [x] `System.IO.Path`
- [x] `System.Math`
- [x] `System.Memory`
- [x] `System.Net`
- [x] `System.Net.Tcp`
- [x] `System.Process`
- [x] `System.Runtime`
- [x] `System.Runtime.Buffer`
- [x] `System.Runtime.ConsoleInput`
- [x] `System.Runtime.Platform`
- [x] `System.Runtime.Platform.Linux`
- [x] `System.Runtime.Platform.Windows`
- [x] `System.Syscall`
- [x] `System.Text`
- [x] `System.Threading`

## 5. Normalize Standard Library Range Notation

- [x] Make standard library integer ranges use exponentiation or `[min max]`.
  - [x] Replace large literal endpoints with `[min max]` when the full primitive
        range is intended.
  - [x] Use exponentiation for explicit numeric bounds where the exact value is
        meaningful, such as `2 ** 31 - 1`.
  - [x] Prefer the narrowest integer type that expresses the range.
  - [x] Add format/lint tests that prevent regression to giant literal bounds.
  - [x] Update docs and examples to model the new style.

### Range Notation Module Checklist

- [x] `System`
- [x] `System.BitOperations`
- [x] `System.Collections`
- [x] `System.Console`
- [x] `System.FileSystem`
- [x] `System.IO`
- [x] `System.IO.File`
- [x] `System.IO.Path`
- [x] `System.Math`
- [x] `System.Memory`
- [x] `System.Net`
- [x] `System.Net.Tcp`
- [x] `System.Process`
- [x] `System.Runtime`
- [x] `System.Runtime.Buffer`
- [x] `System.Runtime.ConsoleInput`
- [x] `System.Runtime.Platform`
- [x] `System.Runtime.Platform.Linux`
- [x] `System.Runtime.Platform.Windows`
- [x] `System.Syscall`
- [x] `System.Text`
- [x] `System.Threading`



## 6 Project Testing and `System.Testing`

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


## 7. Add macOS Standard Library Platform Backend

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

## 8. Update Website Book

- [ ] Update the book portion of the website.
  - [ ] Convert the book plan into website pages with stable URLs.
  - [ ] Make every chapter a tutorial that builds on previous chapters.
  - [ ] Add content for any planned chapters that do not currently exist
  - [ ] renumber chapters after addition of new ones
  - [ ] Include multiple code examples per chapter.
  - [x] Add compile checks for code examples where possible.
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
- [ ] Chapter 28: Performance Tuning, Independent loops, inline, disjoint params, const params, 
- [ ] Chapter 29: Unsafe stark and rawpointers
- [ ] Chapter 30: Reading Stark Diagnostics
- [ ] Chapter 31: Looking at Generated IR
- [ ] Chapter 32: Project: Command-Line Text Tool
- [ ] Chapter 33: Project: Multi-Module Package
- [ ] Chapter 34: Project: File Processing Utility
- [ ] Chapter 35: Project: Native-Backed Package
- [ ] Chapter 36: Project: Performance Case Study
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

## 9. GitHub Release Pipeline

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
