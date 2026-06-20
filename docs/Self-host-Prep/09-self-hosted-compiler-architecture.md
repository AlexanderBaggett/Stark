# Self-hosted Compiler Architecture

Status: WIP. This document records the architecture direction for the Stark
compiler once it is written in Stark. It is intentionally incomplete and should
grow as decisions are made.

## Design Principles

- The host compiler remains the ground truth until bootstrap succeeds.
- Stark source should not hide dispatch, allocation, aliasing, mutation, or
  failure.
- Traits and doctrines are compile-time contracts.
- Runtime dispatch must be explicit.
- Closed-world choices should use enums and exhaustive `switch`.
- Open runtime extension points should use explicit ops tables.
- Compile-time specialization should use `comptime` CTFE, typed `comptime`
  generic value parameters, and explicit program-structure facts, not runtime
  reflection.
- Wrong alias/noalias proof use must be a compile-time diagnostic, not backend
  undefined behavior.
- Accepted programs should not fall into "unsupported lowering" paths. Missing
  support after acceptance is a compiler bug.

## Reuse Model

The self-hosted compiler should use three reuse patterns.

### 1. Compile-time Traits And Doctrines

Use traits/doctrines for static generic reuse:

- hashing
- equality
- ordering
- formatting
- collection algorithms
- range and type helper contracts
- pure `law` helper bundles

These contracts should not create runtime values. There should be no hidden
trait object, vtable, or dynamic dispatch.

### 1.5 Compile-time Structure Branching

Before self-hosting, use only the frozen `comptime` baseline for generated
constants, tables, range facts, concrete layout facts, and typed integer
`comptime` generics. Prefer ordinary runtime compiler data structures for
package metadata and declaration traversal.

The broad post-self-host direction may branch at compile time over explicit
structural facts:

- types and type categories
- fields and enum variants
- function signatures and function kinds
- attributes, ABI facts, layout facts, and memory contracts
- trait/doctrine conformance and associated type bindings
- package metadata and imported typed interfaces

Those facts are compile-time-only and erase before backend lowering. This broad
surface is deferred until after bootstrap; if a pre-self-host pass needs runtime
data derived from program structure, it should materialize ordinary Stark values
through normal compiler phases instead of expanding broad `comptime`.

Use typed `comptime` generic value parameters for reusable fixed-size compiler
abstractions:

```stark
struct FixedBuffer<T, comptime u64[0 max] N>
{
    T[N] Items;
}
```

These are compile-time specialization parameters, not `const` immutable object
parameters and not runtime values.

### 2. Closed-world Enums

Use enums when the compiler knows every implementation.

Good candidates:

- module resolver variants
- diagnostic output formats
- package image section kinds
- target platform families
- compiler pass kinds
- artifact render modes

Example:

```stark
enum ModuleResolver
{
    Empty(EmptyModuleResolver),
    FileSystem(FileSystemModuleResolver),
    InMemory(InMemoryModuleResolver),
    Package(PackageImageResolver),
}
```

Calls through this shape are ordinary `switch` statements. That makes the
control flow visible and exhaustively checkable.

### 3. Explicit Ops Tables

Use explicit ops tables only when the implementation set is open at runtime.
This pattern is allowed because it makes the dynamic dispatch visible.

Example:

```stark
struct ModuleResolverOps
{
    fnptr<fn ResolveResult(
        rawmutptr<i8[min max]> context,
        ascii moduleName)> Resolve;
}

struct ModuleResolverHandle
{
    rawmutptr<i8[min max]> Context;
    ModuleResolverOps Ops;
}
```

Rules for this pattern:

- The context pointer is visible.
- The ops table is visible.
- Function pointer kinds are explicit.
- Unsafe/type-erased context handling is explicit.
- Memory contracts should be written on callbacks when relevant.
- Prefer a closed enum when the set of implementations is known.

Likely use cases:

- long-term plugin-like tooling boundaries
- native/package integration boundaries
- optional external services

Likely non-use cases:

- ordinary compiler passes
- syntax/type/semantic helper abstractions
- fixed package image section loaders

## Pipeline Shape

The self-hosted compiler should keep the current pipeline contract:

- parse
- syntax model
- declaration/module graph
- type checking
- semantic validation
- ownership validation
- lowering contract validation
- HIR/MIR lowering
- borrow liveness
- SSA lowering
- SSA optimization
- ABI lowering
- SSA validation
- LLVM emission

The exact pass list should continue to track `docs/Internals/CompilerPipeline.md`.
The self-hosted architecture should preserve pass artifacts as first-class
typed values rather than implicit side effects.

## Repository Layout

**Decision: when the self-hosted compiler source is introduced, `/src` becomes
the Stark compiler source root.** At that migration point, the existing C# host
compiler directory is renamed to `/old_src`, then a fresh `/src` is created for
the Stark implementation.

Rules:

- Do not maintain a long-lived `src-stark/` compiler tree.
- `/old_src` preserves the host compiler for emergency recovery, comparison,
  and historical reference.
- `/src` remains the canonical compiler source location after cutover.
- This decision is about source placement only. Bootstrap staging uses the
  existing C# host as Stage0 until self-hosting succeeds, and build artifacts use
  the `build/<profile>/<target-triple>/<stage>/` layout from
  `25-build-artifact-layout.md`.

## Build Artifact Layout

**Decision: build artifacts use
`build/<profile>/<target-triple>/<stage>/`.** See
`25-build-artifact-layout.md`.

The self-hosted compiler and build driver should route artifacts into stable
subdirectories:

- `bin/` for final executable/library outputs
- `obj/` for object files and native intermediates
- `pkg/` for package images
- `stdlib/` for stage-local `System` package artifacts
- `native/` for build-owned native dependency artifacts
- `tests/` for generated runners and test executables
- `diagnostics/` for targeted diagnostic output
- `artifacts/` for requested compiler artifacts such as MIR, SSA, LLVM text,
  package inspection output, and stage-comparison output

The stage segment is the compiler stage that produced the artifacts: `stage0`
for the current C# host, `stage1` for the Stark compiler built by Stage0, and
`stage2` for the Stark compiler built by Stage1.

## Stdlib And Package Discovery

**Decision: `System` discovery uses an explicit ordered search.** The compiler
does not silently search global package locations or the network.

Resolution order:

1. Explicit override from CLI, project manifest, solution manifest, or user
   config.
2. Stage/build-local stdlib artifacts for bootstrap builds.
3. Repo source or `stdlib/dist` artifacts for source-tree development.
4. Installed bundled stdlib package next to the compiler distribution.

If discovery fails, the diagnostic must list the searched paths and the active
target, profile, and compiler stage. Ordinary non-stdlib package dependencies
remain explicit manifest/package references unless a future package-manager
decision adds a separate layer.

## Build Driver Concurrency

**Decision: the self-hosted build driver ports synchronously first.** The C# host
driver uses `async Task` as a sequential I/O idiom, not as real parallel
scheduling, so Stark does not need `async`/`await` for self-hosting.

If build or test execution is parallelized before or after bootstrap, the
architecture should use the narrow coordination surface from
`22-threading-coordination.md`:

- explicit payload thread starts for owned worker inputs,
- `System.Threading.Synchronized<T>` / `Locked<T>` for shared mutable compiler or
  build state,
- MPSC channels for worker progress, diagnostics, and result publication.

Rules:

- Workers should not print diagnostics directly to stdout/stderr.
- Workers should publish events/results; the driver aggregates and emits in a
  deterministic order.
- Shared mutable state should not be represented as naked `static mut` compiler
  data.
- Thread pools, work stealing, `RwLock`, `Once`, condition variables, semaphores,
  thread locals, and parallel compiler passes are not part of the self-hosting
  architecture unless a later decision explicitly adds them.

## Parsing Architecture

**Decision: `Stark.g4` remains the canonical grammar reference, but the
self-hosted compiler uses a handwritten parser.** The Stark compiler should not
port the ANTLR runtime, generated C# parser, generated visitor, or generated
parse-context object graph.

Expected shape:

- a handwritten lexer/token stream with exact source spans
- a handwritten recursive-descent parser, with Pratt or precedence-climbing
  expression parsing
- compact Stark-native syntax nodes or parse events, not ANTLR context classes
- diagnostics emitted as ordinary parser diagnostics with source locations
- text literal decoding kept as a separate parsing helper

Rules:

- `Stark.g4` is updated when syntax changes and remains the grammar authority.
- The handwritten parser is updated manually to match `Stark.g4`.
- Parser conformance tests compare the handwritten parser against the canonical
  grammar and, while the C# host exists, the host ANTLR parser as an oracle.
- Generated parser files are host-maintenance artifacts only; they are not a
  self-hosted compiler dependency.
- A Stark-native parser generator may be reconsidered after bootstrap, but it is
  not part of the self-hosting critical path.

This choice keeps parsing explicit, removes a large generated runtime from the
bootstrap path, and lets the self-hosted compiler optimize parser data
structures for speed and memory locality.

## Backend And LLVM Integration

**Decision: libLLVM is the primary backend integration.** The self-hosted compiler
uses the LLVM C API to verify modules, run target/codegen configuration, and emit
object files in-process. Textual LLVM remains available only as a debug,
diagnostic, golden-test, stage-comparison, and artifact-inspection output.

Rules:

- Bind LLVM's C API, not LLVM's C++ API.
- Wrap opaque LLVM refs in distinct Stark types such as context, module, builder,
  value, type, target, target-machine, pass-manager, and memory-buffer handles.
- Owning LLVM handles must release their resource through `drop`.
- LLVM-owned diagnostic/error strings must be copied into Stark text and disposed
  with the matching LLVM API.
- Final platform linking and native C source compilation still go through the
  native toolchain resolver.
- Textual LLVM must be an output-only inspection artifact. The compiler must not
  parse `.ll` as a bootstrap bridge or production object-emission path.

Implementation path:

1. Prove libLLVM discovery, version checking, diagnostics, direct module
   construction, verification, and object emission through the C API.
2. Keep textual LLVM emission by printing the LLVM module when inspection is
   requested.

## Compiler Pass Representation

Preferred first direction: represent passes as explicit records or closed-world
pass kinds, not hidden interface objects.

Possible shape:

```stark
enum CompilerPassKind
{
    Parse,
    SyntaxModel,
    TypeCheck,
    SemanticValidate,
    LowerMir,
    LowerSsa,
    EmitLlvm,
}
```

This can evolve, but the early self-hosted compiler should avoid recreating C#
interface objects with hidden dispatch. If open pass registration is ever
needed, use an explicit ops-table boundary.

## Name, Key, And Interner Model

**Decision: generic contracts are the public collection model; typed interning
is the compiler hot-path model.** See
`19-generic-collections-and-interning.md`.

Compiler APIs that expose ordinary collection behavior should use the canonical
`Hash` + `Eq`, `Ord`, and `Format` contracts. This keeps `Dictionary<K,V>`,
`HashSet<T>`, sorted helpers, and formatting reusable for stdlib and user code.

Compiler phases should avoid repeated text hashing/comparison in hot paths.
Stable names are interned at source/package boundaries:

- lexer/parser identifiers
- module names
- package names
- type names
- doctrine and trait names
- field and member names
- artifact keys that are repeatedly queried

After interning, compiler data structures use distinct ID types such as
`SymbolId`, `TypeId`, `ModuleId`, and `PackageId`. These IDs may lower to compact
integer storage, but they are not interchangeable at the type level.
The current stdlib surface for this model lives in `System.Text.Interning` and
includes nominal compiler ID wrappers, borrowed `ascii` lookup, and reverse
lookup for diagnostics/debug rendering.

Text remains ordinary text outside those boundaries. Diagnostics, file
contents, FFI strings, user strings, and `System.Text` APIs must not depend on
hidden global interning.

Deterministic compiler output must not depend on hash table iteration order.
Package images, diagnostics, generated source bridges, and golden artifacts use
source order, package image order, interner insertion order, sorted `Ord` order,
or another explicit pass-defined order.

## Artifact Model

The compiler should keep explicit typed artifacts. The self-hosted model must not
copy the C# host's `Dictionary<string, object?>` shape into Stark.

Requirements:

- artifacts are typed or tagged enough to avoid unsafe downcasts in ordinary
  code
- missing required artifacts are explicit compiler invariant failures
- diagnostics are ordinary values
- logs are ordinary values
- pass execution records include duration when time APIs exist
- tests can inspect artifacts through the blessed test inspection model without
  changing normal user-program codegen

Blessed direction:

- closed per-pass artifact records for ordinary pass handoff
- typed handle tables for graph-shaped artifacts and cross references
- typed fact tables for backend, optimization, semantic, package, and diagnostic
  facts
- no stringly untyped object store on the hot path

Text artifact rendering remains a view for diagnostics, tests, and inspection.
Deep pipeline tests should prefer typed artifacts through the fast inspection API.

## Test Inspection Model

**Decision: test inspection is a compiler/tooling feature, not a user-program
runtime feature.** It must not change generated code or runtime execution speed
for normal Stark applications.

Blessed shape:

1. Primary path: a typed in-process compiler test API that returns compile
   results, structured diagnostics, and requested typed artifacts.
2. Cross-stage path: a persistent/batched compiler runner with structured
   results for tests that must target the C# host, Stage1, or Stage2 compiler as
   an external executable without spawning once per fact.
3. Debug/golden path: selective full CLI artifact export for stage comparison,
   golden snapshots, and failure investigation.
4. Text snapshots remain useful for rendered diagnostics, MIR, SSA, LLVM, and
   package text, but text is not the only source of truth for deep pipeline
   tests.

Structured diagnostics available through the fast path should include:

- code
- severity
- message
- primary source span
- related spans/notes
- stage or pass

## Compiler Integer Fact Domain

**Decision: the self-hosted compiler does not require a public `BigInt` or true
arbitrary-precision integer type.** The compiler's integer fact domain is capped
at Stark's fixed-width ceiling: `i1024` and `u1024`.

Use bounded compiler-internal helpers for:

- integer literal parsing and validation
- range-typed integer endpoints
- enum tag layout
- CTFE integer folding
- SSA integer range facts
- known-bit masks and shift/mask reasoning

The pre-self-host standard-library surface for this domain is
`System.Compiler.IntegerFacts`. It is ordinary library code, not arbitrary
precision numerics and not runtime reflection. Compiler-known structural facts
under `System.Compiler.<FactName>` remain compile-time-only predicates; the
`System.Compiler.IntegerFacts` module is a reusable bounded arithmetic/fact
helper namespace.

If a literal, range endpoint, enum tag computation, CTFE result, or SSA fold
requires a value outside the `i1024`/`u1024` domain, the compiler emits a
compile-time diagnostic. The compiler must not silently wrap, saturate, or hide
the overflow unless the source operation explicitly asks for wrapping or
saturating semantics.

## Package Image Format

**Decision: binary package images are the normal compiler load format; JSON/text
are deterministic inspection and export views.** See
`20-package-image-format.md`.

The package image's logical role remains the same: preserve package-boundary
facts for source-surface declarations, typed interfaces, compiler facts,
generic templates, native metadata, and target/profile/layout facts. The
self-hosted compiler should keep those as typed sections in its internal model.

The storage and tooling split is:

- compiler dependency loading reads binary package images
- `stark inspect-pkg` renders deterministic JSON or text from a package image
- normal package builds emit the binary load artifact
- JSON/text output is an inspection view, not an independent source of truth
- legacy `.starkpkg.json` loading may exist during migration, but should not be
  the long-term hot path

Binary package images should support section skipping, string/name tables that
cooperate with typed interning, explicit compatibility diagnostics, and stable
target/profile checks. Package-image tests should validate both the binary codec
and the deterministic inspection output.

## Project And Manifest Configuration

**Decision: Stark manifests remain TOML and are parsed through reusable
`System.Toml`.** See `21-system-toml.md`.

`System.Toml` owns TOML syntax, TOML values, source spans, parse diagnostics,
and deterministic TOML emission. The project driver owns Stark-specific schema
validation for `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml`, and any
future user config files.

The self-hosted compiler should not carry a private `SimpleToml` clone. Config
loading should be:

1. read file through `System.IO`
2. parse TOML through `System.Toml`
3. decode the TOML value tree into typed manifest records
4. report schema errors with TOML source spans

## Module Resolution

Module resolution should start with closed-world variants:

- empty resolver
- in-memory resolver
- filesystem resolver
- package image resolver
- target-aware stdlib resolver

If module resolution later becomes plugin-driven, move the plugin boundary to an
explicit `ModuleResolverOps` table rather than adding trait objects.

## Error Model

Recoverable compiler failures should be values:

- parse diagnostics
- type diagnostics
- package image load errors
- file/process/tool failures
- project manifest errors

**Invariant failure convention (closes L07, resolves OQ-05).** Stark deliberately has
no `panic`/`trap`/`assert` surface and no exceptions: invalid states are made
unrepresentable rather than detected and aborted. The host compiler's ~236
`throw new` sites map onto Stark as follows, in priority order:

| Host pattern | Stark port discipline |
|---|---|
| "Artifact/record missing from dictionary" lookups between passes (`GetRequired`, location-keyed lookups) | **Restructure so the state cannot exist**: pass N hands pass N+1 a typed value attached to the right node, not a stringly-keyed lookup that can miss. |
| "Unexpected node kind" / "unsupported case" switches over IR shapes | **Exhaustive switches over closed enums** (and ranged integers): a missed case is a compile error in the ported compiler, not a runtime throw. |
| Toolchain and environment failures (clang missing, link failed, file unreadable) | **These are not bugs — they are errors.** `Result<T, E>` + `try` propagation up to the driver, reported as ordinary diagnostics. |
| True self-detected bugs (the small residual: "this should be impossible") | `Result<T, InternalError>` propagated with `try` to `main`, reported as `error: internal: <message>`; or, where threading a Result is genuinely impractical, print to stderr + `System.Process.Exit(1)`. |

The port never introduces a hidden control-flow giving-up mechanism. The compile-time
guarantees that make the first two rows possible — switch exhaustiveness and
definite-return analysis — are language features (STK3044/STK3045), so the ported
compiler's own impossible states are compile errors in the ported compiler's source.

## Alias And Memory Facts

Alias/noalias facts are part of the compiler's typed model. The chosen
self-hosted model uses explicit compile-time-only proof carriers for APIs that
need alias facts. These carriers are ordinary visible requirements in the
typed compiler source, but they erase before codegen and never become runtime
objects.

Source facts:

- default non-overlap for memory-backed parameters
- `where disjoint(...)`
- `where overlap(...)`
- `where same(...)`
- `if disjoint(...)`
- `unsafe assume disjoint(...)`

Lowered facts:

- scoped noalias groups
- memory root keys
- typed alias proof carriers for runtime/trusted disjoint scopes
- parameter memory summaries
- call memory summaries
- loop independence facts

Architecture rule:

Wrong alias-class or wrong noalias-proof use must be rejected during type
checking, semantic validation, lowering contract validation, or SSA validation.
It must not become undefined behavior delegated to LLVM.

External alias facts are allowed only through a narrow unsafe construction:
`unsafe assume disjoint(...)`. That construct may create a proof carrier only
after explicit memory-root checks. It is for FFI and externally-known
disjointness, not for bypassing ordinary borrow or memory-contract failures.

Host implementation checkpoint:

- Declaration memory contracts are durable package-image facts.
- `if disjoint(...)` creates a runtime-checked proof carrier for the true
  branch only.
- `assume disjoint(...)` creates a trusted unsafe proof carrier after the same
  root checks.
- SSA validation rejects malformed carrier ids, mismatched root sets, duplicate
  or blank roots, invalid root-key shapes, and roots that do not name parameters
  of the owning function before LLVM metadata emission.

For self-hosting, "alias class" means a compile-time memory-root/proof category
used by the borrow checker, memory contracts, lowering, SSA validation, and LLVM
metadata emission. It is not a Stark type alias and it does not create a runtime
class of values.

## IR Memory And Fact Model

**Decision: use arena/table storage with typed handle indices for compiler IR
graphs, plus first-class extensible fact tables and fact-transfer verification.**
See `24-ir-memory-and-fact-model.md`.

The C# host compiler relies on shared managed object graphs. The Stark compiler
does not translate that model directly. MIR, SSA, compiler artifacts, package
imports, backend state, symbol/type tables, and other graph-shaped compiler data
should be owned by dense tables or arenas and referenced by typed handles such as
`MirValueId`, `MirBlockId`, `SsaValueId`, `TypeId`, `SymbolId`, and `PackageId`.

Owned trees remain acceptable for truly tree-shaped data. Cross references from
trees into compiler state still use typed handles.

`Rc`/`Arc`-style shared ownership is not the default IR model for self-hosting.
Add it later only if a concrete non-IR use case proves that arena/table ownership
is the wrong fit.

The `arena` storage class does not have to become executable source syntax before
the compiler can use this model. A library/compiler-owned arena or table API is
enough if it provides explicit ownership, fast dense storage, and bulk release.

Backend and optimization facts are first-class compiler data, not comments or
loose metadata. Facts attach to typed handles through dense side tables or typed
sparse tables. New fact categories must declare where they attach, who creates
them, who consumes them, whether they are transient/recomputable/durable, whether
they serialize through package images, and which verifier catches incorrect loss
or misuse.

Lowering passes must define a fact policy for each relevant category:

| Policy | Meaning |
|---|---|
| `preserve` | Carry the fact to the equivalent lowered handle. |
| `translate` | Convert the fact into the lower phase's representation. |
| `consume` | Use the fact intentionally and remove it from later phases. |
| `recompute` | Invalidate the fact and require a later pass to rebuild it. |
| `forbid-drop` | Treat accidental loss as an internal compiler validation error. |
| `debug-only` | Allow the fact to disappear from optimized output. |

Durable facts, such as public type layout, ABI/calling convention, extern
signatures, struct layout, C alias mappings, doctrine/trait satisfaction,
associated types, exported symbol metadata, generic-template planning facts, and
downstream constant facts, flow through package images. Transient pass-local MIR
or SSA facts stay in compiler tables unless a package section explicitly needs a
stable summary.

Validation should run at phase boundaries: HIR to MIR, MIR validation, MIR to
SSA, SSA validation, optimization boundaries that create/delete values, ABI
lowering, libLLVM emission, and package-image write/load. Wrong alias-class use,
missing ABI/layout/alignment facts, stale handles, or dropped `forbid-drop` facts
must be caught by the compiler rather than delegated to LLVM or runtime behavior.

## Testing Architecture

The port is TDD-first.

**Test discovery decision:** Stark tests should use a build-time generated
explicit `main` runner from `[Fact]` metadata. There is no runtime reflection or
runtime metadata walking. The generated runner enumerates tests in source/package
metadata order, applies selected-test filters, calls `System.Testing` runner APIs,
and reports stable results. Handwritten explicit runners are allowed only as an
early bootstrap fallback, not as the blessed long-term pattern.

First-stage tests should be Stark tests that run against the current C# host
compiler. They need:

- generated explicit test runners
- process execution and capture
- temp directory/file fixtures
- rich assertions
- text diff/snapshot helpers
- diagnostic comparison helpers
- fast typed compiler test API, persistent/batched host/cross-stage runner, and
  selective artifact export

Only after those tests exist should compiler subsystems be ported.

## Bootstrap Shape

Planned stages:

- Stage0: current C# host compiler
- Stage1: Stark compiler built by Stage0
- Stage2: Stark compiler built by Stage1

Bootstrap success means Stage2 can build the compiler and pass the ported test
suite. No separate snapshot compiler artifact is part of self-host prep; the
current C# host remains Stage0 until the Stark compiler can build itself.
