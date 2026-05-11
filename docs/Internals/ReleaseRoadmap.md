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
  - [x] Delete obsolete stable implementations after the replacement compiles.
  - [x] Copy or move experimental modules into the canonical `System.*`
        namespace.
  - [x] Remove temporary `System.Experimental.*` public surface unless a
        compatibility shim is explicitly needed for one release.
  - [x] Update imports in examples, tests, benchmarks, and docs.
  - [x] Preserve benchmark names and only report the language as `stark`.
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
  - [x] Preserve package-backed generic helper specialization for promoted
        public generic APIs.
    - Done: package images now publish the package-private generic helper
      closure needed by API-visible generic template bodies, which keeps
      promoted collections package-consumable without original source files.
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
    - [x] Windows smoke reran `MemoryCopyFill` and `DictionaryLookup` with
          canonical `stark`, `c`, and `rust` rows after the benchmark range
          cleanup.
    - [ ] Finish the remaining promoted batch smoke set.
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
  - [x] Remove remaining `System.Experimental.*` public surface unless a
        compatibility shim is explicitly approved for one release.
  - [x] Remove experimental namespace aliases after all consumers are canonical.
  - [x] Remove temporary migration tests, docs, and benchmark gates that only
        existed to compare stable and experimental implementations.
  - [x] Audit promoted modules against the new unsafe, raw-pointer, and range
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

- [x] Disallow unnecessary raw pointer use in the standard library.
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

- [x] `System`
  - [x] Remove raw pointer re-exports from public surface unless required.
    - Done: `System.IO.File` no longer exposes public raw-pointer APIs, and
      `System.stark` no longer re-exports `System.Text`. Code that needs the
      current low-level text interop exception must import `System.Text`
      explicitly.
- [x] `System.BitOperations`
  - [x] Replace raw pointer helpers with value or slice APIs where present.
    - Verified raw-pointer free.
- [x] `System.Collections`
  - [x] Replace internal raw storage with `dynamic` or safe storage wrappers
        wherever possible.
    - [x] Replaced `Queue<T>` raw allocation storage with `dynamic T` storage
          in both stable and experimental collections.
    - [x] Verified `Stack<T>`, `RingQueue<T>`, and linked-list storage use
          `dynamic` storage instead of raw allocation storage.
    - [x] Retained `Dictionary<K, V>` raw sparse key/value/state storage as the
          remaining collection raw pointer boundary because current `dynamic`
          storage cannot model sparse uninitialized slots while returning
          mutable borrows from occupied values without moving generic payloads.
          Replace it once the language has first-class sparse initialized-slot
          storage or borrowed enum-payload projection.
- [x] `System.Console`
  - [x] Keep raw handles internal to platform calls.
    - Verified: stdin handle state is module-private and platform calls are the
      only raw handle consumers.
  - [x] Use slices or dynamic buffers for user-facing write paths.
    - Verified: public byte write/read APIs use slices, `DynamicByteBuffer`, or
      fixed runtime buffers.
- [x] `System.FileSystem`
  - [x] Hide directory and file system handles behind owned types.
    - Verified: `Directory.Handle` is internal and the public surface returns
      owned `Directory` values.
  - [x] Replace raw entry buffers with dynamic or fixed safe buffers.
    - Done: `Directory` now owns a `System.Runtime.Buffer.FixedByteBuffer8192`
      for platform entry reads and guards the platform-reported capacity before
      passing an internal raw pointer to the OS boundary.
- [x] `System.IO`
  - [x] Keep public IO contracts free of raw pointers.
    - Verified: the base `System.IO` result/status/error module is raw-pointer
      free; file handle and byte-region raw helpers are now internalized under
      `System.IO.File`.
- [x] `System.IO.File`
  - [x] Replace file buffers with slices, dynamic storage, or owned buffers.
    - Done: public owned `File` read/write paths accept byte slices,
      `DynamicByteBuffer`, fixed runtime buffers, and text views; raw byte and
      region helpers are internal stdlib/platform handoff code only.
  - [x] Keep OS handles internal.
    - Done: stable `File.Handle` and compatibility-style raw helpers such as
      `OpenRead`, `Close`, `ReadBytes`, `WriteBytes`, `Seek`, `WriteText`, and
      `WriteLine` are internal unsafe helpers, not public APIs.
- [x] `System.IO.Path`
  - [x] Replace raw path buffers with dynamic text or fixed safe buffers.
    - Verified: public path APIs use `OwnedAscii`, text views, and value
      results; remaining raw pointers are internal read-only text scans.
- [x] `System.Math`
  - [x] Ensure math APIs remain raw-pointer free.
    - Verified raw-pointer free.
- [x] `System.Memory`
  - [x] Keep raw allocation pointers internal to allocator implementation.
    - Verified: `Allocation` is internal.
  - [x] Expose `dynamic` memory primitives instead of raw allocation plumbing.
    - Verified: reserve, append, copy, move, and fill APIs operate on
      `dynamic`, slices, and initialized destinations.
  - [x] Fence or replace public raw-pointer initialization helpers.
    - Done: `InitializeBytesFromPointerDisjoint` and
      `InitializeCodePointsFromPointerDisjoint` are internal unsafe bridges for
      standard-library text/path internals, not public APIs.
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
- [x] `System.Runtime.Buffer`
  - [x] Prefer dynamic and fixed buffers over raw pointer storage.
    - Verified: storage is `dynamic` or fixed arrays.
  - [x] Remove or internalize stable fixed-buffer raw pointer accessors.
    - Done: stable `FixedByteBuffer*` public access now uses `ReadSlice`,
      `ReadMutableSlice`, and `WriteSlice`, matching the slice-only
      experimental buffer shape.
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
- [x] `System.Syscall`
  - [x] Restrict or internalize user-facing raw syscall APIs.
    - Done: `Syscall0` through `Syscall6` are internal unsafe ABI helpers; Linux
      platform code uses `System.Runtime.Platform.Linux` internal syscall
      shims, and packaged user code should go through safe modules such as
      `System.Process`.
- [x] `System.Text`
  - [x] Replace raw text storage with dynamic/owned text and slices.
    - Done: `OwnedAscii`, `OwnedUnicode`, and `OwnedUtf16` provide owned
      dynamic text/code-unit surfaces; public UTF conversion helpers now use
      owned destinations and `MemoryStatus`.
    - Done: `AsciiData`, `UnicodeData`, and raw UTF conversion helpers are
      internal standard-library/platform/compiler boundaries.
    - Retained: explicitly unsafe public `TryConcat*` and `TryFormat*`
      fixed-buffer hooks remain as the compiler-known no-allocation surface for
      stack `Ascii`/`Unicode` concatenation and interpolation.
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
remain signed. Benchmark `.stark` sources are now covered by the same default
strict range checks in `BenchmarkSourceTests`.

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
  - [x] Enforce full-width endpoint shorthand style in the compiler: the maximum
        endpoint of a ranged integer type must be written as `max`, the minimum
        endpoint of a signed integer type must be written as `min`, and unsigned
        ranges may still use `0` as the lower bound.
  - [x] Run the compiler, pipeline, integration, feature, benchmark,
        docs/examples searches, and focused standard-library range/platform/text
        surfaces after the diagnostic lands, then replace any newly reported
        manual full-width endpoint spellings with `min`/`max`.

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

- [x] Define the Stark test-project model.
  - [x] Model keywords and syntax after Xunit, such as `[Fact]`.
    - Done: `[Fact]` attributes are valid source metadata on test functions;
      test discovery remains explicit in `main`, and `[Theory]` is reserved for
      the later data-driven runner rather than implied today.
  - [x] decide whether test projects are a separate `kind = "test"` manifest kind or executable projects with test metadata
    - Done: test projects use separate `kind = "test"` manifests with a
      `[test]` root/output table.
  - [x] define how solution manifests identify default test sets
    - Done: solution `[defaults].test` lists default test targets; absent
      defaults run all solution members with `kind = "test"`.
  - [x] keep test discovery explicit and static; avoid runtime reflection as a required language feature
    - Done: test executables call facts directly through ordinary Stark code.
- [x] Add a standard-library testing module inspired by xUnit.
  - [x] add a `System.Testing` module or equivalent package-facing testing root
    - Done: `System.Testing` is packaged with `System` but not root-re-exported.
  - [x] port the core assertion vocabulary needed by the current C# xUnit tests, such as truth checks, equality checks, and failure reporting
    - Done: `True`, `False`, `Fail`, scalar/text `Equal`, `RunFact`, and
      `ExitCode` provide the first assertion/reporting vocabulary.
  - [x] model assertion failure using Stark's no-exception failure/result story rather than hidden unwinding
    - Done: assertions return `bool`; `RunFact` returns `0` or `1`; test
      projects return process exit codes.
  - [x] keep allocation and formatting costs explicit so test-only helpers do not leak into normal runtime expectations
    - Done: helpers write literal pass/fail prefixes and caller-provided names
      through `System.Console`; no reflection or hidden exception payloads.
- [x] Implement `stark test` on top of test projects.
  - [x] build test projects through the existing project/solution manifest driver
  - [x] run produced test executables and map their results into concise CLI output
  - [x] support solution-level test aliases and default test sets
  - [x] preserve `--dev`, `--release`, path dependencies, and package-backed dependencies for tests
- [x] Add examples and docs for Stark-native tests.
  - [x] add at least one standard-library test project using `System.Testing`
    - Done: `examples/standard-library-tests` is a `kind = "test"` project
      wired into `examples/Stark.solution.toml`.
  - [x] document how to port existing xUnit-style test cases into Stark test projects
    - Done: `docs/Userfacing/ProjectsAndSolutions.md`,
      `docs/StandardLibrary/System.Testing.md`, and book Chapter 24 document
      explicit fact runners and no-exception assertions.
  - [x] add regression coverage for project-local and solution-level `stark test`
    - Done: integration coverage exercises project-local test runs, failing
      test exit-code mapping, and solution default test targets with path
      dependencies.


## 7. Add macOS Standard Library Platform Backend

- [ ] Create a macOS OS-backed platform implementation.
  - [x] Add `System.Runtime.Platform.MacOS.stark`.
  - [x] Add a macOS dispatch template.
  - [x] Add target detection and package image support for macOS triples.
  - [x] Implement file open, read, write, seek, close, and flush.
  - [x] Implement directory create, delete, open, read, and close.
  - [x] Implement path normalization, current directory, existence, file kind,
        and the currently exposed metadata APIs.
  - [x] Implement console stdout, stderr, stdin, terminal detection, and Unicode
        handling.
  - [x] Implement memory allocation and reallocation using the chosen macOS
        backend.
  - [x] Support macOS object emission for runtime allocator helpers without
        Mach-O COMDATs and with an AArch64-compatible trap calling convention.
  - [x] Implement process exit and process ID.
  - [x] Implement threading: start, join, detach, yield, and sleep.
    - [x] Preserve thread entry return codes through `pthread_join`.
  - [x] Implement TCP sockets and readiness behavior.
  - [x] Implement time or timing hooks needed by benchmarks.
    - Done: standard-library sleep is `nanosleep`-backed on macOS; benchmark
      timing remains host-harness driven until a public clock API lands.
  - [ ] Add macOS-specific correctness tests for each standard library module.
    - [x] Add focused compiler and stdlib coverage for macOS dispatch routing,
          libSystem/POSIX calls, allocator ABI, Mach-O IR shape, object
          emission, package manifests, and raw-pointer boundary documentation.
    - [x] Add focused coverage for macOS `stat`-backed path metadata and
          `pthread_join` return-code preservation.
  - [ ] Add macOS benchmark runs to compare Stark, C, and Rust.
    - [x] Run a batch-1 Stark-only benchmark sweep on macOS.
    - [ ] Add cross-language C/Rust comparison once `rustc` is available in the
          benchmark environment.
  - [x] Document macOS platform behavior and unsupported APIs.
    - [x] Document the current libSystem/POSIX backend and the Apple SDK/Command
          Line Tools requirement for final native linking.

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
- [x] Chapter 24: Testing Stark Code
  - Done: the website book chapter now documents `kind = "test"`,
    `System.Testing`, explicit fact runners, solution default test sets, and
    `stark test`.
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

- [x] Create GitHub Actions release pipeline for Linux and Windows.
  - Done: `.github/workflows/release.yml` builds `linux-x64` and `windows-x64`
    release artifacts on tag pushes and manual release-candidate dispatches.
- [ ] Add macOS to the release workflow after the macOS standard-library backend
      exists.
  - Skipped for this Windows pass with the rest of the macOS-specific work.
- [x] Add build matrix for supported Linux and Windows host/target triples.
- [x] Build compiler binaries for Linux and Windows.
- [x] Build and package the promoted standard library.
- [x] Run parser, compiler, standard library, feature, and integration tests.
- [x] Run focused runtime smoke tests per OS.
  - Done: the workflow runs `stark test standard-library-tests --release`.
- [x] Package release archives with compiler, standard library, templates,
        docs, examples, and license files.
  - Done: `scripts/package-release.ps1` stages compiler publish output, the
    standard-library package image/native library, templates, docs, examples,
    `README.md`, `LICENSE`, and a `VERSION` file.
- [x] Generate checksums for every artifact.
- [x] Add version stamping from tags.
- [x] Generate draft release notes from changelog or commit metadata.
- [x] Upload artifacts to GitHub Releases.
- [x] Add manual dispatch for release candidates.
- [x] Add post-release install smoke tests that download the artifacts and
        compile a small Stark program on each OS.
- [x] Cache toolchains and dependencies without making release outputs depend
        on stale caches.


## 10. Performance Tuning

### Investigate/Triage, Output is tasks in Fix

#### Slower than Rust
- [ ] benchmarks/collections/DictionaryMixed — 2026-05-11 rerun: Rust `984 us`, Stark `1210 us`; active, see fix task below.
- [x] benchmarks/collections/QueueDequeue — stale after queue storage fix; 2026-05-11 rerun: Rust `975 us`, Stark `945 us`.
- [x] benchmarks/collections/QueueGrowth — stale after queue storage fix; 2026-05-11 rerun: Rust `922 us`, Stark `864 us`.
- [ ] benchmarks/io/DirectoryEnumeration — 2026-05-11 rerun: Rust `3783 us`, Stark `4016 us`; active, with compile/IR size larger than runtime gap.
- [x] benchmarks/io/FileBufferedReadWrite — stale after byte-write buffering fix; 2026-05-11 rerun: Rust `2103 us`, Stark `2087 us`.
- [ ] benchmarks/io/FileSystemPathTranscode — rust 1.104452, stark 1.208904
- [ ] benchmarks/micro/AggregatePhiFieldForwarding — rust 0.974632, stark 0.997971
- [ ] benchmarks/micro/AlgebraicIdentitySimplification — rust 1.014934, stark 1.022554
- [ ] benchmarks/micro/ExplicitArithmeticRangePruning — rust 0.990526, stark 1.014211
- [ ] benchmarks/micro/FunctionPointerDevirtualization — rust 1.007611, stark 1.01945
- [ ] benchmarks/network/TcpScatterGatherLoopback — rust 0.970315, stark 1.196042
- [ ] benchmarks/text/IntegerFormatting — rust 1.106406, stark 627.29316
- [ ] benchmarks/text/PathJoin — rust 1.075163, stark 1.094771
- [ ] benchmarks/text/PathRepeatedSmallOps — rust 1.029443, stark 1.07571
- [ ] benchmarks/text/TextParsing — rust 1.092818, stark 1.319337
- [ ] benchmarks/text/UnicodeFormatting — rust 1.047867, stark 594.463059



#### Slower than C
- [ ] benchmarks/collections/DictionaryInsert — stark 1.01573
- [ ] benchmarks/collections/DictionaryLookup — stark 1.063474
- [ ] benchmarks/collections/DictionaryMixed — stark 1.059754
- [ ] benchmarks/collections/LinkedListBuildClear — stark 1.022678
- [ ] benchmarks/collections/LinkedListPush — stark 1.020937
- [ ] benchmarks/collections/ListIteration — stark 1.099882
- [ ] benchmarks/collections/QueueChurn — stark 1.060086
- [ ] benchmarks/collections/QueueDequeue — 2026-05-11 rerun: C `961 us`, Stark `999 us`
- [ ] benchmarks/collections/QueueGrowth — 2026-05-11 rerun: C `860 us`, Stark `890 us`
- [ ] benchmarks/console/ConsoleWrites — stark 1.096863
- [ ] benchmarks/io/DirectoryEnumeration — stark 1.295304
- [ ] benchmarks/io/FileBufferedReadWrite — stark 1.893304
- [ ] benchmarks/io/FileSystemPathTranscode — stark 1.208904
- [ ] benchmarks/micro/AbstractionGenericWrapper — stark 1.002597
- [ ] benchmarks/micro/AbstractionHandWritten — stark 1.008162
- [ ] benchmarks/micro/AlgebraicIdentitySimplification — stark 1.022554
- [ ] benchmarks/micro/BitwiseRangePruning — stark 1.01355
- [ ] benchmarks/micro/BranchSelectPredication — stark 1.063505
- [ ] benchmarks/micro/Branching — stark 1.018312
- [ ] benchmarks/micro/Calls — stark 1.01224
- [ ] benchmarks/micro/DirectCallInlining — stark 1.001287
- [ ] benchmarks/micro/ExplicitArithmeticRangePruning — stark 1.014211
- [ ] benchmarks/micro/FunctionPointerDevirtualization — stark 1.01945
- [ ] benchmarks/micro/StackFieldBranchForwarding — stark 1.001637
- [ ] benchmarks/micro/StackFieldLoadForwarding — stark 1.017903
- [ ] benchmarks/micro/StackNestedFieldForwarding — stark 1.006009
- [ ] benchmarks/micro/StackScalarLoadForwarding — stark 1.015369
- [ ] benchmarks/network/TcpScatterGatherLoopback — stark 1.196042
- [ ] benchmarks/runtime/RuntimeBufferDynamic — stark 1.05191
- [ ] benchmarks/text/AsciiToUnicodeConversion — stark 1.0301
- [ ] benchmarks/text/AsciiToUnicodeConversionRuntime — stark 1.082843
- [ ] benchmarks/text/AsciiToUnicodeWideningKernel — stark 1.018182
- [ ] benchmarks/text/IntegerFormatting — stark 627.29316
- [ ] benchmarks/text/OwnedPathAllocation — stark 1.009137
- [ ] benchmarks/text/PathJoin — stark 1.094771
- [ ] benchmarks/text/PathNormalize — stark 1.095768
- [ ] benchmarks/text/PathQueries — stark 1.002167
- [ ] benchmarks/text/PathRepeatedSmallOps — stark 1.07571
- [ ] benchmarks/text/TextConcatCopy — stark 1.070156
- [ ] benchmarks/text/TextParsing — stark 1.319337
- [ ] benchmarks/text/UnicodeFormatting — stark 594.463059

### Fix
  
- [ ] Elide large owned aggregate moves in `DirectoryEnumeration` IO paths.
  - IR comparison: optimized Stark `DirectoryEnumeration` IR is `131017` lines
    and `12016664` bytes, while Rust is `2006` lines and `147102` bytes. Stark
    scalarizes the `IOResult<Directory>` success payload and inline 8192-byte
    directory buffer into thousands of per-byte field loads after
    `System.FileSystem.OpenDirectory`, plus large `File`/`Directory` drop
    temporaries; Rust keeps the hot path around compact `ReadDir` and `DirEntry`
    values.
  - Fix: teach ABI/ownership lowering to construct large returned aggregates
    directly into the final local when an `IOResult<T>.Ok(var value)` payload is
    immediately moved, and lower large fixed-buffer moves as one `memcpy` or a
    true move instead of scalar field extraction. If compiler-side move elision
    is not enough, add internal out-parameter fast paths for `OpenDirectory` and
    owned `File` creation so the stdlib can initialize caller storage without
    materializing and dropping extra 8KB temporaries.
  - Verify with an IR gate on `benchmarks/io/DirectoryEnumeration.stark`: the
    optimized `EnumerateOnce` body should not contain thousands of
    `fca.5.0.*.load` operations for the directory buffer and should not copy or
    drop extra `%System_FileSystem_Directory` temporaries around the success
    payload. Rerun `STARK_BENCH_RUNS=100 STARK_BENCH_FILTER=DirectoryEnumeration scripts/run-benchmarks.sh`
    and record the Stark/Rust/C averages here.

- [x] Fix byte-level owned `File.Write` to honor userspace buffering.
  - Context: `benchmarks/io/FileBufferedReadWrite` opened the writer with
    `FileBuffering.Full`, then performed many 32-byte byte-slice writes.
    `WriteTextRaw` used the owned `File` buffer, but byte-level `Write` bypassed
    it and called the platform write path for each small slice.
  - Completed on 2026-05-11 by adding a byte-buffer append path to
    `System.IO.File.File` and routing buffered byte writes through the existing
    `WriteBufferStorage`/`FlushRaw` mechanism. The focused 10-run benchmark
    averaged Stark `2087 us` and Rust `2103 us`, so the Rust-slower runtime row
    is stale. The remaining issue for IO benchmarks is compile/toolchain time
    and very large IR from owned `File`/`Directory` value copies and drop
    temporaries.

- [x] Fix `System.Collections.Queue<T>` front-dequeue performance.
  - Context: the 100-run full benchmark sweep on 2026-05-10 showed
    `benchmarks/collections/QueueDequeue` at Stark `11694 us`, C `1004 us`,
    and Rust `1083 us`. The current `Queue<T>.TryDequeue` implementation moves
    from index 0 with `self.Items.MoveAt(0)`, which makes repeated dequeues
    from the front expensive for large queues.
  - Replace the contiguous front-removal implementation with ring-buffer
    storage, or promote `RingQueue<T>` as the implementation behind
    `Queue<T>`. The existing `RingQueue<T>` code already tracks `Head` and
    `Length`, grows while preserving logical order, and dequeues without
    shifting all remaining elements.
  - Preserve the public `Queue<T>` API and drop semantics: `Count`,
    `IsEmpty`, `Reserve`, `Enqueue`, `TryDequeue`, `Peek`, and `Clear` should
    keep their behavior for plain values and values with destructors. Reuse the
    existing queue/ring-queue parity and drop tests as the starting point.
  - Rerun collection-focused standard-library tests and
    `STARK_BENCH_RUNS=100 STARK_BENCH_FILTER=Queue scripts/run-benchmarks.sh`.
    The expected outcome is that `QueueDequeue` is close to C/Rust and no
    longer scales with an O(n) shift per dequeue.
  - Completed on 2026-05-11 by converting `Queue<T>` and `RingQueue<T>` to the
    internal `SparseSlots<T>` storage view while preserving the public API.
    Focused IR tests confirm promoted `TryDequeue<u32>` moves through
    `SparseSlots.MoveAt`, whose specialization is a direct slot load, and
    contains no `QueueSlot<T>`, `dynamic_move_at`, or `llvm.memmove`. The final
    100-run queue benchmark pass averaged: `QueueChurn` Stark `915 us`,
    C `913 us`, Rust `954 us`; `QueueDequeue` Stark `999 us`, C `961 us`,
    Rust `987 us`; `QueueGrowth` Stark `890 us`, C `860 us`, Rust `969 us`.

- [x] Fix `benchmarks/text/TextParsing` native baselines so they perform the
      same source-level work as Stark.
  - Context: the Stark benchmark parses bool, i64, u64, signed i1024 Unicode,
    and unsigned u1024 Unicode values. The C variant currently validates the
    1024-bit cases with `strcmp(I1024_MIN_TEXT, I1024_MIN_TEXT)` and
    `strcmp(U1024_MAX_TEXT, U1024_MAX_TEXT)`. The Rust variant compares each
    1024-bit string constant with itself. At `-O3`, clang reduces the C
    benchmark body to setting `errno` and returning 0, and rustc reduces the
    Rust benchmark body to `retq`, so the benchmark mostly measures process
    startup instead of parsing.
  - Replace the C/Rust 1024-bit checks with real decimal parsing. A fair C
    baseline can use a 16-limb `u1024` parser that multiplies the current value
    by 10 and adds the next digit with overflow checks, then compares against
    the expected limb arrays. A fair Rust baseline can use an idiomatic helper
    type with `FromStr` or a small `parse_u1024_decimal` helper over `[u64; 16]`
    and compare parsed values against expected constants.
  - Keep the bool, i64, and u64 parse cases real as well. Avoid comparing a
    constant to itself or parsing only compile-time constants in a way the
    optimizer can fold away. If needed, route inputs through a small static
    slice/table and consume the parsed result in the checksum.
  - Verify by inspecting optimized assembly or LLVM IR before trusting the
    numbers: C/Rust `main` must still contain parse loops or helper calls after
    optimization. Then rerun
    `STARK_BENCH_RUNS=100 STARK_BENCH_FILTER=TextParsing scripts/run-benchmarks.sh`.
  - Completed on 2026-05-11. The C/Rust variants now parse the 1024-bit decimal
    inputs into 16-limb values and compare against expected limbs; optimized
    native output still contains parsing helper calls/loops. The Stark parser
    also had runtime `u1024 / 10` and `u1024 % 10` cutoff calculations in
    `ParseI1024*`/`ParseU1024*`; those were replaced with precomputed decimal
    cutoffs and covered by a focused IR test. The 100-run pass averaged
    `TextParsing` Stark `1179 us`, C `1108 us`, Rust `1139 us`.

- [x] Replace generic `u1024 / 10` and `u1024 % 10` loops in text formatting
      with limb-wise decimal formatting.
  - Context: after fixing the C/Rust `IntegerFormatting` and
    `UnicodeFormatting` benchmarks to perform real formatting work, Stark still
    measured roughly 250x slower than C/Rust on 2026-05-11:
    `IntegerFormatting` Stark `577490 us`, C `2235 us`, Rust `2302 us`;
    `UnicodeFormatting` Stark `571553 us`, C `2276 us`, Rust `2276 us`.
  - The hot path is `System.Text.TryFormatSignedU1024Ascii` and
    `System.Text.TryFormatSignedU1024Unicode`. They first count digits by
    repeatedly dividing a `u1024` by 10, then write digits by repeatedly using
    `remaining % 10` and `remaining / 10`. Saved LLVM IR contains
    `udiv i1024` and `urem i1024`; the generated native code for the 1024-bit
    formatter is very large.
  - Implement a fixed-width limb formatter for `u1024` instead. Use 16
    64-bit limbs and a carry-based divide-by-10 pass:
    `current = (carry << 64) | limb`, `limb = current / 10`,
    `carry = current % 10`, walking from the most significant limb to the least
    significant limb. Emit digits into a stack scratch buffer in reverse, then
    copy them to the destination. Share the core helper between ASCII and
    Unicode so the two paths do not drift.
  - Remove the separate digit-count pass if possible. The reverse scratch
    buffer already yields the digit count, so capacity can be checked once
    before copying into the destination. Preserve current failure behavior for
    null destinations, negative capacity, insufficient capacity, and signed
    i1024 minimum formatting.
  - Add focused standard-library tests for `i1024::min`, `u1024::max`, zero,
    single digit, and a mid-size value for both ASCII and Unicode formatting.
    Rerun the formatting benchmarks with
    `STARK_BENCH_RUNS=100 STARK_BENCH_FILTER=Formatting scripts/run-benchmarks.sh`
    and inspect LLVM IR to confirm no hot `udiv i1024`/`urem i1024` remains in
    the formatting helper.
  - Completed on 2026-05-11. `TryFormatSignedU1024Ascii` and
    `TryFormatSignedU1024Unicode` now share a 16-limb decimal digit generator
    and copy reversed digits into ASCII/Unicode destinations. Focused IR tests
    confirm the formatting bodies no longer contain `udiv i1024`/`urem i1024`;
    only the limb helper divides `i128` intermediates by 10. The 100-run
    formatting pass averaged `IntegerFormatting` Stark `2259 us`, C `2290 us`,
    Rust `2338 us`; `UnicodeFormatting` Stark `2329 us`, C `2325 us`,
    Rust `2389 us`.
