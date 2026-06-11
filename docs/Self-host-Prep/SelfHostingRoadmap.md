# Self-Hosting Roadmap

A pre-flight checklist for porting the Stark compiler from C# to Stark itself.
Each item is grounded in a concrete gap visible in the current source tree
or in [LanguageReference.md](../Userfacing/LanguageReference.md) /
[LanguageInternals.md](./LanguageInternals.md).

The order matters: items earlier in the list block items later in the list.
Most early-phase items are **language** changes — the bootstrap order, not
raw line count, is the dominant obstacle.

> **Framing note.** Stark deliberately has *no* exceptions, *no* unwinding,
> *no* trait objects, *no* dynamic dispatch by default, *no* garbage collection,
> *no* implicit copies of owned aggregates, and *no* `null` in safe code
> ([LanguageReference.md §14](../Userfacing/LanguageReference.md), §7, §8.5).
> The C# compiler relies on every one of those. The port is therefore mostly
> a *translation* problem (mechanical, large) rather than a *capability*
> problem — except for the gaps listed below, which are real and block work.

---

## Phase 0 — Language Capability Gates

These are features the grammar in [Stark.g4](../../Stark.g4) does not yet
admit. Either the surface needs to grow, or the porting team needs an agreed
workaround pattern.

### Error-propagation ergonomics
Stark's error model is correct (errors are values, see
[LanguageInternals.md §0](./LanguageInternals.md#0-compiler-layer-contract)
and [LanguageReference.md §14](../Userfacing/LanguageReference.md)) — but the
*syntactic surface* is thin. The C# compiler has 720+ `throw`/`catch`/`finally`
sites (e.g. [NativeToolchain.cs:30-100](../../src/Compiler/NativeToolchain.cs#L30-L100)).
Every one must be rewritten as an explicit `switch` on a `Result`-shaped enum
unless we add one of the following:

- [ ] A `?`-style early-return propagation operator on `Result`/`Option`
      shapes. Without it, every fallible call site is a 4-line `switch`
      (see [LanguageReference.md §12.2](../Userfacing/LanguageReference.md)
      for the current pattern).
- [ ] `if let` / `while let` (binding-in-condition). Today
      [Stark.g4](../../Stark.g4) only binds inside `switch case`.
- [ ] Decide whether the compiler is allowed to call `trap`/`abort` for
      unrecoverable internal-invariant failures (the natural successor to
      `throw new InvalidOperationException(...)`).

### Traits
Stark traits now support required/default methods, `Self`, associated types, and
static constrained-generic dispatch. Ordinary traits remain compile-time
contracts; `dyn trait` is explicit runtime dispatch.

- [x] Add default method bodies in `traitMember` at
      [Stark.g4:201](../../Stark.g4#L201), **or** publish the convention of
      shipping default impls as free functions in the same module.
- [x] Add associated types and `Self`-type reference.
- [~] Operator-overloading traits (`Add`, `Sub`, `Mul`, `Eq`, `Ord`, `Hash`).
      Their absence is why [Text.stark:3534-4744](../../stdlib/src/System/Text.stark#L3534-L4744)
      reimplements `TryFormatI8`…`TryFormatI1024` for every width. Canonical
      `Eq`, `Hash`, `Ord`, and `Format` contract names exist; arithmetic
      operator contracts and stdlib implementations are still broader work.

### Generics
- [x] Decide generic value parameter spelling: typed `comptime` generic
      parameters, e.g. `struct FixedArray<T, comptime u64[0 max] N>`.
      This avoids overloading Stark's `const`, which means deep interior
      immutability, while still giving fixed-size stdlib/compiler abstractions
      a reusable compile-time value parameter.
- [x] Implement the pre-self-host typed integer `comptime` generic slice,
      including monomorphization identity, diagnostics, package-image
      preservation, and fixed-array use sites.
- [ ] User-definable variadics for ordinary Stark functions. `VARARGS`
      ([Stark.g4:831](../../Stark.g4#L831)) is currently FFI-only
      ([LanguageReference.md §13.1](../Userfacing/LanguageReference.md));
      a Stark-side `format!`-style API is impossible without it.

### Pattern matching
- [x] Or-patterns (`case A | B:`). Landed as switch-label alternatives with
      shared `when` guard/body semantics, compile-time capture consistency
      checks, and native literal-switch lowering where applicable.
- [x] Inclusive integer range patterns (`case 0..10:`). Landed for switch
      labels and enum/aggregate field subpatterns with interval coverage,
      optimized guarded lowering, and typed package-image support.
- [ ] Decide whether list/property patterns are still needed before the
      self-hosted compiler port.

### Control flow & literals
- [x] Labeled `break` / `continue`. Labels attach to loops and switches;
      `break label;` targets labeled loops or switches, and `continue label;`
      targets labeled loops only. Parser, semantic diagnostics, MIR lowering,
      CTFE, runtime lowering, and package-image typed-template preservation
      are covered by targeted host tests.
- [x] Multiline / raw string literals in
      [StarkLexer.cs](../../src/Parsing/StarkLexer.cs). LLVM IR templates
      and diagnostic messages currently use C#'s `@"..."` and `$$"""..."""`
      forms extensively. Landed syntax: `raw"..."`, `raw"""..."""`, and
      `$raw` interpolation.
- [ ] Confirm or extend the source surface for `for-each` over collections.
      Today only the canonical C-style `for (init; cond; step)` form is
      shown in [LanguageReference.md §10.2](../Userfacing/LanguageReference.md);
      a Stark-hosted compiler that iterates over IR-node lists, symbol
      tables, etc. needs an ergonomic story.

### Compile-time evaluation
- [x] Freeze the pre-self-host `comptime` baseline at the current host
      implementation. Broad Zig-like CTFE, complete evaluator parity, and
      expanded structural facts are deferred until after bootstrap; see
      [13-comptime.md](13-comptime.md),
      [26-comptime-pre-self-host-scope.md](26-comptime-pre-self-host-scope.md),
      and
      [27-comptime-post-self-host-scope.md](27-comptime-post-self-host-scope.md).

### Build-driver concurrency
Stark has no async/await. This is consistent with the rest of the design
(no unwinding, explicit storage classes) and probably should stay that way:

- [x] **Decision:** port [ProjectCliDriver.cs](../../src/Compiler/ProjectCliDriver.cs)
      (260+ `async Task<T>` use sites) synchronously first. Do **not** add an
      `async` keyword for self-hosting. If build/test execution later becomes
      parallel, use the narrow doc `22` coordination surface: explicit payload
      thread starts, `Synchronized<T>` / `Locked<T>`, and MPSC channels.

### Syntactic-burden items (no language change required — but big translation cost)
These are not gaps; they are deliberate features that make the C# → Stark
translation verbose. They appear here because tooling can help.

- [ ] Every Stark local needs an explicit storage class — `stack`, `heap`,
      `register`, or `static`
      ([LanguageReference.md §9](../Userfacing/LanguageReference.md#9-globals-and-storage-classes)).
      C# locals are uniformly stack-or-heap-by-type. A mechanical translator
      that picks the storage class is worth building before mass porting.
- [ ] No default arguments
      ([LanguageReference.md §5.3](../Userfacing/LanguageReference.md)).
      C# default args must be expanded into overload sets at every call
      site. [FunctionOverloads.cs](../../src/Compiler/FunctionOverloads.cs)
      (1034 lines) already shows the compiler accepts overload sets, so this
      is purely a syntactic translation.

---

## Phase 1 — Stdlib Foundations

These unblock writing data structures, text manipulation, and the build
driver in Stark.

### Numerics
- [x] Build bounded compiler integer-fact helpers over the existing
      `i1024`/`u1024` ceiling, not a public `BigInt` or arbitrary-precision
      type. Include the operations the compiler needs (`Max`, shift-and-mask
      for bit-width range checks, signed/unsigned conversion) and diagnostics
      for oversized literals or compile-time integer overflow. The raw types
      exist
      ([LanguageReference.md §6.1](../Userfacing/LanguageReference.md));
      the helpers used in
      [IntegerRangeStorageFacts.cs:40-46](../../src/Compiler/IntegerRangeStorageFacts.cs#L40-L46)
      and [EnumLayoutBuilder.cs:63](../../src/Compiler/EnumLayoutBuilder.cs#L63)
      are covered by `System.Compiler.IntegerFacts`.
- [x] `log`, `log2`, `log10`, `pow`, `exp` in
      [Math.stark](../../stdlib/src/System/Math.stark). `System.Math` now
      exposes these scalar floating-point intrinsics, and the packaged stdlib
      math tests exercise them.

### Collections
- [x] Generic text-key contracts so primitive `ascii` / `unicode` and owned
      `OwnedAscii` / `OwnedUnicode` keys work through the blessed `Hash` + `Eq`
      surface, with exact ordinal equality/hash/order semantics. The compiler
      now has built-in `ascii`/`unicode` dictionary/set key contracts and owned
      text uses explicit static `Hash`/`Equals` hooks under the canonical
      contracts.
      The host compiler keys ~20 dictionaries on `string`
      ([TypeChecking.cs:72-119](../../src/Compiler/TypeChecking.cs#L72-L119)).
- [x] `HashSet<T>` using the same explicit static `Hash`/`Equals` key hook and
      bool/integer scalar fast path as `Dictionary<K,V>`.
- [ ] Strongly typed compiler interning. The C# compiler relies on .NET string
      equality; the Stark compiler should intern stable names at
      front-end/package boundaries and use typed IDs such as `SymbolId`,
      `TypeId`, `ModuleId`, and `PackageId` in hot paths.
- [ ] Sorted map or ordered set (used in pass artifacts that need
      deterministic iteration order). Hash-table iteration must not define
      package image, diagnostic, or golden output order.

### Text
- [ ] `format!`-style variadic formatter. Blocked on user-definable variadics
      (Phase 0). Until then, every formatter is a hand-written
      `Try*` (see [LanguageReference.md §12.1](../Userfacing/LanguageReference.md)
      for the current fixed-capacity interpolation form).
- [ ] An efficient `StringBuilder`-equivalent dynamic text builder used to
      assemble LLVM IR. `dynamic Ascii` and `OwnedAscii` exist in
      [Text.stark](../../stdlib/src/System/Text.stark) but the porting team
      needs an ergonomic `Write*` surface.

### Memory
- [ ] `Allocator` trait with at least `alloc`, `realloc`, `free`. Today
      [Memory.stark:23](../../stdlib/src/System/Memory.stark#L23) exposes
      `Allocator.IsDefault()` only.
- [ ] Arena/table allocator support for compiler-owned IR storage. The `arena`
      storage class is reserved in
      [LanguageReference.md §9](../Userfacing/LanguageReference.md) but
      explicitly not yet a valid executable local storage class, and called
      out as future work in [CompilerPipeline.md:258](./CompilerPipeline.md#L258).
- [ ] Typed handle and fact-table support. The self-hosted compiler uses
      arena/table ownership plus typed handles for MIR, SSA, artifacts, package
      imports, and backend state. Backend facts attach through first-class fact
      tables with explicit lowering policies and validation; durable facts flow
      through package images. See [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).

### Concurrency
- [x] Atomic integer types are implemented as the first safe-sharing primitive.
- [x] Payload thread starts checked by `Transferable`.
- [x] `System.Threading.Synchronized<T>` / `Locked<T>` as the easy guarded
      shared-state primitive.
- [x] MPSC channels for build/test progress and result publication.
- [ ] Accept single-threaded compiler/build-driver v1 unless the doc `22`
      coordination surface is explicitly scheduled.

---

## Phase 2 — Compiler-Driver Surface

These cover what the C# compiler reaches into the .NET BCL for in its
outermost layers. Without them, a Stark-hosted compiler cannot drive a build.

### Process & environment
- [x] `argv` / `argc` access. [Process.stark](../../stdlib/src/System/Process.stark)
      exposes copied argument access through `Arguments()` and `ArgumentCount()`
      on the current Linux backend.
      [LanguageReference.md §14](../Userfacing/LanguageReference.md) notes
      `main` only needs `unsafe`/`ffi` if it touches raw `argc`/`argv`, so
      a safe wrapper API is the natural place.
- [x] Environment variable read/write on the current Linux backend.
- [x] Process spawn with stdin/stdout/stderr capture + exit code on the current Linux backend.
      [NativeToolchain.cs:30-507](../../src/Compiler/NativeToolchain.cs#L30-L507)
      shells out to `clang` and the linker repeatedly.

### Filesystem
- [x] File metadata: size, mtime, permissions. Cross-platform Linux/macOS/Windows
      support has landed; Windows permissions are synthesized from file attributes.
      Required for any incremental build.
- [x] Recursive directory walk + glob. Recursive walk and streaming glob have landed.
      The C# driver uses
      `Directory.EnumerateFiles`
      ([NativeToolchain.cs:344](../../src/Compiler/NativeToolchain.cs#L344))
      for source discovery.
- [x] Temp directory creation. Platform-backed temp-root creation has landed.
- [ ] Symlink read/create (lower priority).

### Serialization
- [x] Decide TOML strategy: add reusable `System.Toml`; do not change manifest
      format and do not bless a manifest-only parser.
- [ ] Implement `System.Toml` parser/emitter and use typed manifest decoding for
      `Stark.toml`, `Stark.solution.toml`, and `Stark.user.toml`.
- [x] Decide package-image format: binary package images are the normal compiler
      load path; deterministic JSON/text are inspection and export views.
      `stark inspect-pkg` renders those views on demand; build-time sidecars are
      deferred unless repeated debug/test usage proves they are worth it.
- [ ] Implement the binary package-image codec and deterministic JSON/text
      `stark inspect-pkg` output for the logical package model in
      [PackageImage/Models/PackageImageModels.cs](../../src/Compiler/PackageImage/Models/PackageImageModels.cs).

### LLVM integration
- [x] Decide: use libLLVM as the primary backend integration through the LLVM C
      API. Textual LLVM remains only for debug, diagnostics, golden/artifact
      inspection, and stage comparison.
- [ ] Implement the required FFI support for libLLVM: `System.C` C strings,
      LLVM-style out-pointer patterns, typed opaque-handle wrappers, deterministic
      dispose/drop wrappers, and C enum/bitflag constants.
- [ ] Add the first libLLVM backend slice: discover/load pinned libLLVM, verify
      version/API availability, construct a narrow LLVM module directly through
      typed C API wrappers, verify it, emit object bytes in-process, and print
      textual LLVM only as an optional inspection artifact.
- [ ] Expand direct LLVM module construction until it replaces the current
      textual emitter as the full backend implementation.

---

## Phase 3 — Bootstrapping Strategy

Decisions that need to be made before code starts moving.

- [ ] **Parser strategy.** Today [StarkParser.cs](../../src/Parsing/StarkParser.cs)
      is 12,041 lines of Antlr-generated code; the runtime is
      `Antlr4.Runtime.Standard 4.13.1`. Choose one:
      - Port the Antlr runtime to Stark (sizeable, but keeps the grammar
        file as the single source of truth).
      - Write a hand-rolled recursive-descent parser (sizeable, but lets
        us drop the Antlr dependency entirely).
      - Generate a parser via a Stark-native parser generator (does not
        exist yet — would itself need to be built; not recommended).
- [x] **Stage-zero compiler.** Keep it simple: the current C# compiler is
      Stage0 until Stark can build itself. Stage0 builds Stage1; Stage1 builds
      Stage2; when Stage2 passes the ported tests and comparisons, cut over.
- [ ] **Pass-by-pass migration vs. clean rewrite.** A clean rewrite is
      simpler given the accepted-program contract
      ([CompilerPipeline.md:19-56](./CompilerPipeline.md#L19-L56)) —
      front-end validation cannot be partially ported because lowering has
      no fallbacks once code reaches `lower-mir`.
- [x] **LINQ replacement direction.** Do not build a broad iterator-trait
      surface before self-hosting. Use exactly three explicit traversal loop
      forms: borrowed element, mutable borrowed element, and indexed borrowed
      traversal.
- [ ] Implement the traversal loop forms and keep hot pipeline transformations
      on explicit `Length` / index / slice APIs where that is clearer or faster.
- [x] **IR ownership model.** Use arena/table ownership with typed handle
      indices, first-class extensible fact tables, explicit fact lowering
      policies, package-image durable facts, and phase-boundary validation. This
      is a structural refactor of the C# object graph, but it keeps storage
      explicit, deterministic, and fast while preventing backend facts from
      disappearing during lowering. See [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).
- [ ] **String-symbol stability policy.** Several SSA passes — e.g.
      `SsaConstStdlibHelperSpecializer`, `SsaConstantTextFormatSpecializer`
      ([src/Compiler/SsaOptimization/](../../src/Compiler/SsaOptimization/))
      — are hard-coded to recognize stdlib helpers by symbol name.
      Choose either a declarative binding (attribute on the stdlib function)
      or a name-stability contract before porting these passes; otherwise
      renames will silently disable optimizations.
- [ ] **Function-kind discipline.** Decide up front which compiler-internal
      operations are `law` (pure), which are `finite`, and which stay `fn`.
      [LanguageReference.md §5.1](../Userfacing/LanguageReference.md). Be
      conservative: most pipeline code is `fn` because it allocates IR nodes
      via the chosen ownership model. Mark only proven-pure helpers (hash,
      compare, arithmetic) as `law` / `finite law`.

---

## Phase 4 — Compiler Port (Frontend → Backend)

Once Phases 0-3 land, the actual port can begin. Listed in the same order
as [CompilerPipeline.md](./CompilerPipeline.md), with C# line counts as a
rough sizing hint.

- [ ] Lexer + parser (~12.7 kloc generated, smaller hand-written).
- [ ] [SyntaxModelFactory.cs](../../src/Compiler/SyntaxModelFactory.cs) (1693 lines).
- [ ] Module resolution + symbol catalog
      ([ModuleResolution.cs](../../src/Compiler/ModuleResolution.cs),
      [DefaultCompilerPipeline.cs](../../src/Compiler/DefaultCompilerPipeline.cs)).
- [ ] [TypeChecking.cs](../../src/Compiler/TypeChecking.cs) (16,597 lines —
      the largest single file).
- [ ] [SemanticValidation.cs](../../src/Compiler/SemanticValidation.cs)
      (6629 lines).
- [ ] [OwnershipValidation.cs](../../src/Compiler/OwnershipValidation.cs)
      (6152 lines).
- [ ] [NonLexicalBorrowLifetimeValidation.cs](../../src/Compiler/NonLexicalBorrowLifetimeValidation.cs)
      (1173 lines).
- [ ] [LoweringContractValidation.cs](../../src/Compiler/LoweringContractValidation.cs)
      (2267 lines).
- [ ] HIR + [MidLevelIrLowering/](../../src/Compiler/MidLevelIrLowering/).
- [ ] [SsaLowering.cs](../../src/Compiler/SsaLowering.cs) (1843 lines) +
      [SsaIrValidation.cs](../../src/Compiler/SsaIrValidation.cs) (3723 lines).
- [ ] 20 in-tree SSA passes
      ([src/Compiler/SsaOptimization/](../../src/Compiler/SsaOptimization/)).
      All optional; defer if needed.
- [ ] [AbiLowering.cs](../../src/Compiler/AbiLowering.cs) +
      [LlvmIrEmitter.cs](../../src/Compiler/LlvmIrEmitter.cs) (3096 lines) +
      [LlvmIrEmission/](../../src/Compiler/LlvmIrEmission/).
- [ ] Binary package image read/write plus JSON/text inspection export
      ([src/Compiler/PackageImage/](../../src/Compiler/PackageImage/)).
- [ ] CLI driver + project driver
      ([CompilerCli.cs](../../src/Compiler/CompilerCli.cs) (4051 lines),
      [ProjectCliDriver.cs](../../src/Compiler/ProjectCliDriver.cs) (1746 lines)).

---

## Phase 5 — Validation & Cutover

- [ ] Differential testing: every test in [tests/](../../tests/) must
      pass under both the C# host and the Stark host before cutover.
- [ ] Deterministic package-image comparison between hosts: binary codec tests
      plus stable JSON/text inspection output for human-readable diffs.
- [ ] Performance budget: stage-1 compile time within an agreed multiple
      (e.g. ≤ 3x) of the C# compiler before we commit.
- [ ] Stage2 builds the Stark compiler and passes the ported tests before the
      C# implementation is removed from the normal build path. Keep `/old_src`
      for reference/emergency recovery during cutover.

---

## Non-Goals (Explicit)

Documenting these so they do not creep into the critical path:

- **Async/await.** Stark has no unwinding and no GC; adding `async` would
  fight the rest of the design. The build driver becomes synchronous.
- **Trait objects / dynamic dispatch by default.** Explicitly absent from
  the language ([LanguageReference.md §8.5](../Userfacing/LanguageReference.md));
  do not add to ease the port.
- **Exceptions or stack unwinding.** Stark's runtime contract
  ([LanguageReference.md §14](../Userfacing/LanguageReference.md)) is that
  recoverable errors are values and unrecoverable failure traps. The port
  follows that contract.
- **GC for IR nodes.** The accepted model is arena/table storage with typed
  handles and first-class fact tables; do not retrofit a GC or make shared
  pointers the default IR representation.
- **Incremental compilation in v1 of the self-hosted compiler.** Requires
  file-stat APIs (Phase 2) but is not on the cutover path.
- **A Stark-native parser generator.** Decide between hand-written parser
  and ported Antlr runtime; do not invent a third option.
- **Plugin / dynamic-loading support.** The current compiler does not have
  it, and adding it during self-host is out of scope.
