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
- [ ] Generic const parameters (e.g. `struct FixedArray<T, const N>`).
      Without them, the stdlib's fixed-array surface has to be duplicated per
      size or fall back to `dynamic T`.
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
- [ ] Labeled `break` / `continue` ([Stark.g4:530-539](../../Stark.g4#L530-L539)).
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
- [ ] General `const fn` (or `finite law`-driven CTFE) to fold compiler-side
      table generation. Today
      [CompileTimeExpressionEvaluator.cs](../../src/Compiler/CompileTimeExpressionEvaluator.cs)
      (1265 lines) handles a fixed operator set, not arbitrary function
      execution. Several SSA passes hard-code recognized stdlib helpers by
      symbol name as a workaround.

### Build-driver concurrency
Stark has no async/await. This is consistent with the rest of the design
(no unwinding, explicit storage classes) and probably should stay that way:

- [ ] **Decision:** port [ProjectCliDriver.cs](../../src/Compiler/ProjectCliDriver.cs)
      (260+ `async Task<T>` use sites) to synchronous code on top of explicit
      thread + channel primitives. Do **not** add an `async` keyword unless a
      strong second use case appears.

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
- [ ] Wrap the existing fixed-width 1024-bit integers behind a `BigInt`-style
      facade with the operations the compiler needs (`Max`, shift-and-mask
      for bit-width range checks, signed/unsigned conversion). The raw
      types exist
      ([LanguageReference.md §6.1](../Userfacing/LanguageReference.md));
      the helpers used in
      [IntegerRangeStorageFacts.cs:40-46](../../src/Compiler/IntegerRangeStorageFacts.cs#L40-L46)
      and [EnumLayoutBuilder.cs:63](../../src/Compiler/EnumLayoutBuilder.cs#L63)
      do not.
- [ ] `log`, `log2`, `log10`, `pow`, `exp` in
      [Math.stark](../../stdlib/src/System/Math.stark) (currently trig + PRNG only).

### Collections
- [ ] Built-in string hashing so `Dictionary<Ascii, T>` / `Dictionary<Unicode, T>`
      work without per-call-site trait impl. Today
      [Collections.stark:742](../../stdlib/src/System/Collections.stark#L742)
      requires every key type to satisfy `DictionaryKey`. The compiler keys
      ~20 dictionaries on `string`
      ([TypeChecking.cs:72-119](../../src/Compiler/TypeChecking.cs#L72-L119)).
- [ ] `HashSet<T>`. None exists in
      [Collections.stark](../../stdlib/src/System/Collections.stark);
      [DefaultCompilerPipeline.cs:842](../../src/Compiler/DefaultCompilerPipeline.cs#L842)
      depends on one for generic-instantiation deduplication.
- [ ] String / symbol interner. The C# compiler relies on .NET string
      equality; Stark `Ascii`/`Unicode` are byte/codepoint storage and a
      compiler-grade symbol table needs interning.
- [ ] Sorted map or ordered set (used in pass artifacts that need
      deterministic iteration order).

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
- [ ] Arena allocator. The `arena` storage class is reserved in
      [LanguageReference.md §9](../Userfacing/LanguageReference.md) but
      explicitly not yet a valid executable local storage class, and called
      out as future work in [CompilerPipeline.md:258](./CompilerPipeline.md#L258).
- [ ] Shared-ownership wrapper. The C# compiler relies on GC for shared
      MIR/SSA references. Stark must either pick an `Rc`/`Arc` equivalent
      or refactor the IR to use arena ownership + handle indices. The
      latter is more idiomatic for Stark; pick the strategy explicitly.

### Concurrency
- [ ] `Mutex`, `RwLock`, `Once`. [Threading.stark](../../stdlib/src/System/Threading.stark)
      (154 lines) has only `Thread.Start/Join/Detach`.
- [ ] Atomic integer + pointer types.
- [ ] Channel / bounded queue.
- [ ] Work-stealing pool for pass-level parallelism (or accept single-threaded
      v1; see Non-Goals).

---

## Phase 2 — Compiler-Driver Surface

These cover what the C# compiler reaches into the .NET BCL for in its
outermost layers. Without them, a Stark-hosted compiler cannot drive a build.

### Process & environment
- [ ] `argv` / `argc` access. [Process.stark](../../stdlib/src/System/Process.stark)
      is currently 16 lines and exposes only `CurrentId()` and `Exit(code)`.
      [LanguageReference.md §14](../Userfacing/LanguageReference.md) notes
      `main` only needs `unsafe`/`ffi` if it touches raw `argc`/`argv`, so
      a safe wrapper API is the natural place.
- [ ] Environment variable read/write.
- [ ] Process spawn with stdout/stderr capture + exit code.
      [NativeToolchain.cs:30-507](../../src/Compiler/NativeToolchain.cs#L30-L507)
      shells out to `clang` and the linker repeatedly.

### Filesystem
- [ ] File metadata: size, mtime, permissions. Required for any incremental
      build.
- [ ] Recursive directory walk + glob. The C# driver uses
      `Directory.EnumerateFiles`
      ([NativeToolchain.cs:344](../../src/Compiler/NativeToolchain.cs#L344))
      for source discovery.
- [ ] Temp directory creation.
- [ ] Symlink read/create (lower priority).

### Serialization
- [ ] JSON parser + emitter — or a decision to migrate
      `.starkpkg.json` ([PackageImage/](../../src/Compiler/PackageImage/))
      to a Stark-native binary format. The current package model lives in
      [PackageImage/Models/PackageImageModels.cs](../../src/Compiler/PackageImage/Models/PackageImageModels.cs).

### LLVM integration
- [ ] Decide: keep emitting textual LLVM IR and shell out to `llc` / `opt`,
      or bind `libLLVM` directly. Textual IR keeps Phase 2 scope smaller but
      locks in the printing cost. The current emitter
      ([LlvmIrEmitter.cs](../../src/Compiler/LlvmIrEmitter.cs), 3096 lines)
      already emits text — porting it forward as text is the obvious path.
- [ ] If binding `libLLVM`: a C FFI surface broad enough to construct a
      module, type, function, basic block, and IR builder. Stark's
      `unsafe ffi asm` and `unsafe ffi varargs`
      ([LanguageReference.md §13](../Userfacing/LanguageReference.md))
      give the boundary; the bindings themselves do not yet exist.

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
- [ ] **Stage-zero compiler.** The first Stark-hosted compiler must be
      buildable by the current C# compiler. Plan for at least one
      release-gated "stage transition" PR.
- [ ] **Pass-by-pass migration vs. clean rewrite.** A clean rewrite is
      simpler given the accepted-program contract
      ([CompilerPipeline.md:19-56](./CompilerPipeline.md#L19-L56)) —
      front-end validation cannot be partially ported because lowering has
      no fallbacks once code reaches `lower-mir`.
- [ ] **LINQ replacement.** ~1989 LINQ method-chain calls in C# drive most
      pipeline transformations. Build an iterator-trait surface
      (`map` / `filter` / `collect` / `group_by` / `sort_by`) or accept
      that every chain becomes an explicit `willexit` loop with intermediate
      `dynamic T` allocations.
- [ ] **IR ownership model.** Pick one of:
      - shared-pointer wrapper (`Rc`/`Arc`) — closest to C# semantics but
        needs the Phase 1 shared-ownership type and accepts refcount cost;
      - arena + 32-bit handle indices — more idiomatic for Stark, plays
        nicely with the deterministic-output goal, but is a structural
        refactor of every IR pass.
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
      All optional under `-O0`; defer if needed.
- [ ] [AbiLowering.cs](../../src/Compiler/AbiLowering.cs) +
      [LlvmIrEmitter.cs](../../src/Compiler/LlvmIrEmitter.cs) (3096 lines) +
      [LlvmIrEmission/](../../src/Compiler/LlvmIrEmission/).
- [ ] Package image read/write
      ([src/Compiler/PackageImage/](../../src/Compiler/PackageImage/)).
- [ ] CLI driver + project driver
      ([CompilerCli.cs](../../src/Compiler/CompilerCli.cs) (4051 lines),
      [ProjectCliDriver.cs](../../src/Compiler/ProjectCliDriver.cs) (1746 lines)).

---

## Phase 5 — Validation & Cutover

- [ ] Differential testing: every test in [tests/](../../tests/) must
      pass under both the C# host and the Stark host before cutover.
- [ ] Bit-for-bit identical `.starkpkg.json` output between hosts (or a
      published manifest schema change).
- [ ] Performance budget: stage-1 compile time within an agreed multiple
      (e.g. ≤ 3x) of the C# compiler before we commit.
- [ ] Reproducible build of the Stark compiler **by itself** for one full
      release cycle before deleting the C# implementation.

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
- **GC for IR nodes.** Pick arena-and-handles or shared-pointer wrapper in
  Phase 3; do not retrofit a GC.
- **Incremental compilation in v1 of the self-hosted compiler.** Requires
  file-stat APIs (Phase 2) but is not on the cutover path.
- **A Stark-native parser generator.** Decide between hand-written parser
  and ported Antlr runtime; do not invent a third option.
- **Plugin / dynamic-loading support.** The current compiler does not have
  it, and adding it during self-host is out of scope.
