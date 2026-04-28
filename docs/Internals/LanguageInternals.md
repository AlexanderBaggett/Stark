# Stark Language Internals

This document describes compiler-facing and backend-facing implementation details for Stark.
For syntax, source rules, and the user-facing language contract, see [LanguageReference.md](../Userfacing/LanguageReference.md).

The goal of this document is explanatory rather than normative.
If there is ever a conflict, the source-level contract belongs in the language reference.

## 1. Backend Relationship

Stark is intentionally stricter than mainstream systems languages in a number of places because those restrictions let the compiler prove more about a program.

The current implementation targets LLVM.
That does not make LLVM details part of the language surface, but it does explain why some Stark rules were chosen to make aliasing, control flow, effects, purity, and floating-point behavior easier to communicate precisely to the backend.

### 1.1 Backend Optimization Boundaries

Stark's default compilation bias is transparent and closed-world: when source
or package-image bodies are available, the compiler should normally be free to
inline, specialize, clone, internalize, and otherwise optimize through module
boundaries.

Some modules need a deliberate backend boundary. Runtime allocation code,
platform code, FFI-heavy shims, or modules that expose a temporary backend
correctness issue may still be ordinary Stark modules at the source level while
being opaque to whole-program backend optimization.

The source form is a C#-style attribute on the declaration that needs the
backend boundary:

```stark
[Backend(Opaque)]
module System.Memory

[Backend(Opaque)]
finite law i32[0 max] Hash(i32[0 max] value) {
    return value;
}

[Backend(Opaque)]
struct RuntimeHandle {
    rawptr<void> Value;
}
```

`[Backend(Opaque)]` means:

- parse, type-check, validate, and compile the module normally
- keep the module's public Stark API visible to importers
- allow local optimization inside the module
- do not let backend whole-program optimization look through the marked module,
  callable, type, or contract boundary from callers
- do not import the affected function bodies into callers for ThinLTO,
  cross-module inlining, backend cloning, or backend specialization
- continue to emit ABI attributes and Stark-owned semantic summaries that are
  safe to expose across a compiled call boundary

Module-level opacity applies to the entire module. Function-level opacity is a
finer boundary: the marked callable remains a real backend call while unrelated
declarations in the same module can still participate in whole-program
optimization.

Type-level opacity applies to the marked `struct` or `record` and is inherited
by explicit constructors, destructors, methods, and monomorphized type-owned
method instantiations unless a future narrower rule is added. Compiler-generated
layout, move, and drop mechanics that do not produce a callable backend symbol
are treated as part of the opaque type operation rather than as separate
cross-module inline surfaces.

Doctrine-level opacity applies to the doctrine declaration and is inherited by
doctrine methods, generated doctrine helper/dispatch symbols, and concrete
instantiations that can otherwise become backend optimization surfaces. The goal
is narrow containment: standard-library code can isolate fragile runtime,
collection, or doctrine internals without making the whole module opaque.

Opaque does not mean source-hidden, abstract, private, or unavailable. It is
not a visibility modifier. It is an optimization-boundary request for the
compiler backend.

The default module behavior remains automatic:

```stark
module System.Text
```

With no backend attribute, the compiler chooses the fastest safe optimization
mode for the module and the current output kind. That policy may still keep a
module native-only if the compiler has recorded a backend-safety reason, but
ordinary source code should not need to mention ThinLTO or LLVM pass details.

The implementation should model backend optimization mode as metadata on the
module, package image, and toolchain plan rather than as source visibility. A
future policy can independently choose:

- whether the root executable module emits ThinLTO bitcode
- whether each dependency module emits ThinLTO bitcode
- whether normal LLVM optimization passes are allowed for a given module
- whether link-time optimization is enabled for the final executable
- which reason code explains each inclusion or exclusion

This keeps Stark's performance-first default while still giving the standard
library and advanced package authors a precise escape hatch for backend
boundaries.

Operationally, the policy is:

- optimize closed-world by default for `-O1`, `-O2`, and `-O3` executable
  builds when source bodies, package typed bodies, or package optimization facts
  are available
- prefer the narrowest opaque boundary that preserves correctness or measured
  performance: function before type, type before module
- use `[Backend(Opaque)]` for runtime/platform/interop code, fragile backend
  lowering, or deliberately tuned code where importing the body makes the whole
  program slower
- record every whole-program optimization decision in toolchain metrics so a
  benchmark run can explain which modules joined ThinLTO and which stayed
  native-only
- treat the attribute as a performance lever that must be justified by tests or
  benchmark ratios, not as a general encapsulation mechanism

## 2. Borrowing and Emitted Facts

The borrower system is one of Stark's main sources of optimizer-facing information.
The user-facing rules live in [BorrowerSystem.md](../Userfacing/BorrowerSystem.md); this section focuses on what those rules let the compiler emit when the proof is available.

Common consequences include:

- non-escaping borrow classes can justify `captures(...)` facts and, in some cases, `returned`
- null-free and well-defined safe borrows can justify `nonnull`, `noundef`, and `dereferenceable`
- exclusive ownership, unique destinations, and proven non-overlap can justify `noalias`
- `frozen` access and proven law-like read behavior can justify `readonly` and `memory(...)`
- constrained destruction and explicit shared-state rules can justify `nounwind`, `nosync`, and `nofree`
- `finite` reasoning can justify `willreturn` and `mustprogress`
- `out` and `init` contracts can justify `initializes(...)`, writable-destination reasoning, and `dead_on_return`
- stronger slice and array qualifiers can justify `align`, better range reasoning, and more aggressive loop/vectorization facts

These are compiler outputs, not language syntax.
They are emitted only when the implementation can prove them from the source rules plus body analysis.

### 2.1 Disjoint Function Parameters

`disjoint` is Stark's source-level contract for memory regions that do not overlap.
The parameter-prefix form and the relational `where disjoint(...)` form both feed the same internal memory-separation fact model.

Each `disjoint(...)` relation forms a pairwise non-overlap group. `where disjoint(a, b, c)` records `a` separate from `b`, `a` separate from `c`, and `b` separate from `c`. Multiple groups remain independent: `where disjoint(a, b), disjoint(c, d)` records only the two stated pairs and does not record any relationship between `a` or `b` and `c` or `d`.

Disjointness facts are not transitive. `disjoint(a, b), disjoint(b, c)` does not prove `disjoint(a, c)` unless that relation is also stated or separately proven.

For a four-parameter function where `a` and `b` do not overlap and `c` and `d` do not overlap, but cross-group pairs such as `b` and `d` may overlap, the source form is `where disjoint(a, b), disjoint(c, d)`. For a four-parameter function where all parameters are mutually separate, the source form is `where disjoint(a, b, c, d)`.

For function parameters, disjointness gives the compiler these backend facts:

- a parameter whose whole reachable memory region is disjoint from every other accessible pointer region is emitted with LLVM `noalias` when the LLVM parameter rules allow it
- individual loads, stores, and memory-touching calls through disjoint roots carry scoped `!alias.scope` and `!noalias` metadata
- inlined bodies preserve disjointness through scoped noalias metadata instead of relying only on parameter attributes
- disjoint output or initialization destinations compose with `writeonly`, `initializes(...)`, and dead-store reasoning when the initialized byte range is known
- disjoint readonly inputs compose with `readonly`, `captures(none)`, or read-only `captures(...)` facts

The compiler treats disjointness as a memory-range fact, not as a root-identity fact. Two slices from the same allocation can be disjoint when their element ranges do not overlap. Two different values are not considered disjoint merely because their names differ.

### 2.2 Branch-Scoped Disjointness

`if disjoint(a, b)` creates a control-flow-scoped memory fact. The true branch carries a proven no-overlap relation for the listed memory regions. The false branch keeps ordinary conservative aliasing behavior.

For contiguous slices and text views, the check lowers to pointer-range comparisons over the data pointer, element size, and length. Once the true branch is selected, memory operations through the checked regions receive scoped `!alias.scope` and `!noalias` metadata. If the fact is introduced inside a nested scope or loop body, the compiler uses a distinct alias-scope domain for that scope and emits `llvm.experimental.noalias.scope.decl` when the selected LLVM representation needs an explicit scope boundary.

The runtime check is a source-level fact boundary. Optimizer metadata must not be attached outside the dominated true-branch region unless later analysis proves the fact still holds.

### 2.3 Independent Loops

`independent` on a `while` or `for` loop means loop iterations have no loop-carried memory dependencies. The loop body may still use induction variables, local scalar temporaries, and immutable reads, but a memory write in one iteration may not be read or written by another iteration.

Independent loops lower to LLVM loop-dependence metadata:

- memory operations covered by the contract receive `!llvm.access.group`
- the loop latch receives `!llvm.loop.parallel_accesses` referencing those access groups
- existing termination facts continue to lower to `mustprogress` or `!llvm.loop.mustprogress` where valid
- vectorization and interleaving hints are attached only when the independent contract and target cost model justify them

The contract is semantic, not a hint. If a loop marked `independent` contains a real loop-carried memory dependence, the program violates the Stark source contract. Safe Stark code must either prove the contract statically or establish the required disjointness through checked facts such as `if disjoint(...)` before entering the loop.

### 2.4 Const Parameters

`const` on a parameter means the reachable object graph has const provenance and is deeply immutable. It is stronger than `frozen`, which is a borrow-duration readonly view. A const parameter describes memory that safe Stark code cannot mutate at any point through any reachable path.

Const parameters produce these backend facts:

- pointer-like ABI values are `nonnull`, `noundef`, `dereferenceable`, and aligned according to the concrete type layout
- argument memory is `readonly`, and functions that only read const parameters receive restrictive `memory(...)` attributes
- captures are `captures(none)` when the pointer does not escape, or `captures(address, read_provenance)` when readonly provenance is stored for later reads
- loads through permanently immutable const provenance carry `!invariant.load` when the loaded object cannot be replaced for the lifetime represented in IR
- projections from const parameters preserve frozen/readonly provenance and keep raw conversions from regaining mutable authority

Const does not imply disjointness. Multiple const parameters may alias the same immutable object graph. The compiler emits `noalias` for const parameters only when a separate disjointness fact is present or proven.

## 3. Internal ABI and FFI Boundaries

At the source level, `ffi` marks a foreign-facing function boundary.
Internally, the compiler treats ordinary non-`ffi` Stark calls differently from `ffi` calls.

The current implementation uses a faster internal calling convention for non-`ffi` internal Stark calls when it can.
By contrast, `ffi` boundaries preserve foreign ABI expectations and should be treated as the stable interop-facing surface.

This is an implementation detail.
It matters for code generation and interop, but it is not meant to change how ordinary Stark code is written.

## 4. Runtime and C-Runtime Boundaries

Stark should distinguish explicit Stark runtime dependencies from
toolchain-inherited dependencies.

Using Clang or LLVM to compile, optimize, assemble, or link Stark output is a
toolchain decision. That does not by itself mean Stark programs should depend
directly on libc, glibc, musl, libm, or the Windows CRT at runtime.

If a C-family runtime symbol appears only because LLVM or the native toolchain
selected it while lowering otherwise Stark-owned LLVM IR, it is classified as a
toolchain/backend dependency. That includes cases such as LLVM math intrinsics
lowering to libm, LLVM memory intrinsics lowering to `memset`/`memcpy`/`memmove`,
or a hosted link using conventional platform startup code.

The `Reduce C-Runtime Dependencies` roadmap section is for C runtime surfaces
that Stark itself explicitly emits or exposes, not for replacing LLVM's chosen
backend support-library strategy.

The supported runtime dependency profiles are:

- **Default hosted profile**: the normal compiler and standard-library mode.
  Stark may use LLVM, Clang, platform startup objects, backend-selected helper
  routines, and platform import libraries. C-family symbols that appear only
  because the native toolchain selected them are tracked as toolchain/backend
  dependencies, not as Stark-owned standard-library implementation choices.
- **Explicit-C-runtime-free profile**: an audit profile for Stark-owned runtime
  and standard-library code. In this profile, implemented Stark-owned platform
  paths must not explicitly call libc, glibc, musl, libm wrappers, or Windows
  CRT allocation/IO helpers. Linux code uses direct syscalls or Stark-owned
  runtime helpers. Windows code uses OS APIs such as `kernel32`, Winsock, and
  the selected OS heap or virtual-memory API.

User-written `ffi` remains outside those standard-library guarantees. If user
code imports `malloc`, `printf`, OpenSSL, SQLite, or any other C-family API,
that is an explicit source-level dependency chosen by the program.

Package-owned native dependency metadata should make those explicit choices
portable across package boundaries. For example, a Raylib package may publish
its native shim source and required native libraries so downstream programs can
build with a normal Stark command instead of repeating package-specific linker
flags. These package facts remain separate from Stark-owned runtime
dependencies: importing an FFI package records that package's native obligations,
but it does not make those obligations part of the standard-library runtime
profile.

The intended runtime direction is:

- Linux runtime and standard-library platform code should use Linux syscalls or
  Stark-owned runtime helpers rather than libc wrappers.
- Windows runtime and standard-library platform code should use OS APIs such as
  `kernel32`, Winsock, or the selected Windows heap/virtual-memory API rather
  than the C runtime.
- `ffi` remains available for user-requested foreign calls, including calls into
  C libraries, but those calls should be explicit source-level choices.

Current explicit Stark runtime-dependency caveats:

- Heap locals and `System.Memory` allocation now lower through Stark-owned
  runtime helpers rather than explicit `malloc`, `realloc`, or `free` calls.
  Linux allocator helpers use direct syscalls on supported Linux targets;
  Windows allocator helpers use OS heap APIs rather than the CRT allocator.
- Source-module and package linkage can pull in object files for re-exported
  modules that user code did not directly call, so unused `System.Memory` code
  must still be audited at the object, archive, and final executable levels.

Those caveats are implementation debt, not desired language semantics.
Explicit C-runtime dependency reduction should be validated at the object,
archive, and final executable levels by inspecting unresolved and linked runtime
symbols.

## 5. Generic Instantiation and Specialization

The current compiler monomorphizes generics by default.
In practice, that means a generic function such as `Identity<i32>` is usually realized as a concrete specialized body for that exact use when a body is available.

This matches Stark's speed-first design.
The baseline plan is to prefer a concrete specialized body, not to avoid specialization.

There are a few important implementation-level variations:

- declaration-only imports may need an ABI fallback path because no body is available to specialize
- some imported helpers may use a more aggressive caller-specific cloning strategy
- `cold` and `noinline` annotations can discourage the most duplicative specialization paths

The compiler also computes a small deterministic body-complexity score for generic function bodies.
That score is only a planning hint.
It does not affect type checking, semantic correctness, or the meaning of a Stark program.

In the current implementation, this score is mainly used to decide how aggressive specialization should be beyond the normal owned concrete body path.
It is not primarily used to decide whether ordinary specialization happens at all.

## 6. Closed-World Compilation Bias

Stark is designed with a closed-world bias, and the compiler takes advantage of that.

The implementation generally assumes:

- static dispatch by default
- restrictive visibility by default
- a small set of externally visible symbols
- aggressive internalization when module and package boundaries permit it
- generic specialization as a normal tool rather than an exceptional optimization

Dynamic dispatch and open-world behavior are still possible where the language provides them, but they are treated as explicit concessions rather than the default compilation model.

### Unsigned Integer Types

Unsigned integer widths (`u8` through `u1024`) are first-class integer type
facts. They are not represented internally as signed integers that merely happen
to have non-negative ranges. The parser and type resolver still apply the normal
explicit range rule, but for `uN` the type-relative `min` endpoint is `0` and
`max` is `2**N - 1`.

The unsigned fact is preserved through type checking, lowering, LLVM emission,
and package images so operations with signedness-sensitive meaning can choose
the correct backend behavior.

## 7. Const Numeric Storage

Scalar numeric `const` declarations should be treated as compile-time values,
not user-selected runtime storage commitments.

For integer constants, the type checker records the exact single-value range on
the smallest supported integer width that can represent the value. It does not
preserve a user-written integer range for scalar const storage, because the exact
value is already known and can be propagated as an LLVM constant. For example,
`const BoardWidth = 80;` is stored as `i8[80 80]`, while
`const BigCount = 2**16;` is stored as `i24[65536 65536]`.

For floating-point constants, the type checker follows the literal spelling.
An unsuffixed decimal such as `80.0` is `f64`; an `f`-suffixed decimal such as
`80.0f` is `f32`.

Explicit const types remain useful for non-scalar or ambiguous constants such as
raw-pointer nulls, fixed arrays, and aggregate initializers. They should not be
used to force scalar numeric const storage. A scalar integer const may name a
bare width such as `i32`, but it must not use a ranged integer source type such
as `i32[min max]`.

## 8. Callable Values, Capture Modes, and Unsafe Boundaries

Function items are the intended zero-cost foundation for first-class callable
values.

A named Stark function in value position should first resolve to a function
item: a compiler-known, zero-sized callable identity. A function item does not
capture state and does not by itself make the function address-taken. Calls
through a function item should stay eligible for direct-call lowering,
monomorphization, inlining, closed-world law-path specialization, and ordinary
function-effect reasoning.

When a function item is converted to a function-pointer type, the compiler
records that the function is address-taken. The runtime representation is a
thin code pointer. Calls through the pointer are indirect unless the compiler
can recover a singleton target set. Where the target set remains known, the
LLVM emitter may use indirect-call target metadata such as `!callees`.

Function-pointer types carry the source function kind as part of the type. A
`fnptr<law ...>` target has stronger source obligations than a general
`fnptr<fn ...>` target, and the compiler may preserve those obligations in
call-effect summaries and package-image facts.

Non-capturing lambdas should lower like anonymous internal function items. They
may promote to thin function pointers when required by the target type.

Capturing lambdas lower to a closure environment plus a callable entry. Stark
does not require a heap environment by default. The implementation should choose
stack, inline aggregate, or owned heap-backed storage according to ordinary
ownership and escape rules. The capture list is the source of truth for the
environment layout and optimizer facts.

Safe capture modes have the following intended lowering meaning:

- `copy`
  Store a copied value in the environment. The current safe subset is cheap
  copy values such as scalars, raw pointers, function pointers, and read-only
  borrows. Owned structs and owned text must use `move` or `read` until Stark
  has an explicit aggregate-copy contract.
- `move`
  Move ownership into the environment. The closure owns the captured value and
  is responsible for its eventual drop. A uniquely owned environment can support
  stronger noalias-style reasoning.
- `read`
  Store read-only access to existing storage. Safe borrowed captures remain
  non-null and may justify `readonly`, `nonnull`, `noundef`,
  `dereferenceable`, and capture facts that expose read provenance without
  writable provenance.
- `mut`
  Store exclusive mutable access. The source borrow rules must prevent other
  live access for the closure lifetime, allowing noalias-style facts where the
  implementation can prove non-overlap.
- `out`
  Store a write-only destination. The closure may initialize or overwrite the
  destination but may not read the old contents. This can justify `writeonly`
  and dead-store-oriented reasoning.
- `init`
  Store an uninitialized destination that must be initialized before successful
  return. This is the strongest destination contract and can feed
  `initializes(...)`-style backend facts when the implementation can prove the
  initialized byte range.

The `mut`, `out`, and `init` modes require a writable binding at the capture
site. This lets the type checker reject impossible closure environments before
closure lowering exists, and preserves the intended read/write facts for later
MIR and backend work.

Trusted capture modes deliberately weaken ordinary closed-world assumptions:

- `unsafe addr`
  Captures address or identity information without granting ordinary dereference
  authority. This maps to address/provenance capture behavior rather than
  read/write memory access and should block facts that require the address to
  remain unobserved.
- `unsafe shared`
  Publishes a value or capability into the shared/concurrent domain. This should
  suppress ordinary non-shared assumptions such as `nosync` where the body or
  call path can synchronize, and it should avoid noalias claims that are not
  justified by the shared-state capability.

Unsafe is intended as a narrow proof-boundary marker. It should not disable the
borrow checker, ownership validation, initialization validation, range typing,
or effect checking. Instead, it permits only explicitly gated operations whose
invariants must be upheld by the programmer or by a trusted standard-library
wrapper.

FFI imports with raw platform obligations should be modeled as unsafe unless
they are wrapped by a safe Stark API. Standard-library threading is one of the
main motivating cases: the public thread-entry surface should use function
items or closure values, while raw platform callback entry points and shared
publication live behind a small unsafe runtime boundary. Backend callback facts
such as LLVM `!callback` metadata may be useful for broker functions that relay
a callable to platform thread creation.

## 9. Doctrines and Static Realization

`doctrine` declarations are compile-time-only and do not have a runtime representation.
That makes them a natural fit for Stark's static dispatch and closed-world specialization model.

This is useful compiler context, but the user-facing rules for writing doctrines remain part of [LanguageReference.md](../Userfacing/LanguageReference.md).
