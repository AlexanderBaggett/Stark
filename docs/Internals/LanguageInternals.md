# Stark Language Internals

This document describes compiler-facing and backend-facing implementation details for Stark.
For syntax, source rules, and the user-facing language contract, see [LanguageReference.md](../Userfacing/LanguageReference.md).

The goal of this document is explanatory rather than normative.
If there is ever a conflict, the source-level contract belongs in the language reference.

## 0. Compiler Layer Contract

The compiler pipeline has one central validity rule: MIR lowering is not a
language-validity filter. If source text is grammatically invalid, parsing must
reject it. If source text is grammatically valid but not a valid Stark program,
the declaration, symbol, type-checking, semantic validation, ownership
validation, or explicit pre-MIR contract validation stages must reject it before
MIR is built.

Once a function reaches MIR lowering without diagnostics, the compiler has
accepted the program's executable semantics. From that point forward, lowering
and backend code generation are obligated to preserve those semantics or report
an internal compiler invariant failure. They must not silently turn accepted
source constructs into declaration-only functions, fallback bodies, or
`unsupported-lowering` artifacts.

In practice this means each layer has a narrow responsibility:

- parsing rejects malformed token and grammar shapes only
- declaration and symbol stages resolve modules, names, overload sets,
  visibility, and function/type ownership of declarations
- type checking records executable operation facts such as call targets,
  receiver binding, argument coercions, indexing kind, constructor shape,
  enum layout use, dynamic-storage operation shape, function-pointer ABI shape,
  and `sizeof`/`alignof` target types
- semantic and ownership validation reject effect, borrowing, drop, lifetime,
  mutability, and memory-contract violations
- MIR lowering consumes the accepted typed facts and constructs MIR directly
- SSA, optimization, ABI lowering, LLVM emission, package-image lowering, and
  imported-template lowering must preserve the same accepted-program contract

Lowering invariant violations are compiler bugs, not user diagnostics. They are
still useful guardrails, but every invariant that can be reached by
grammatically valid invalid source should also have an earlier diagnostic test.
The long-term direction is to make invalid states unrepresentable at the MIR
boundary by carrying a typed executable expression model through source modules,
package images, generic template bodies, monomorphization, and inline clone
planning.

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
finite law i32[0 max] Hash(i32[0 max] value)
{
    return value;
}

[Backend(Opaque)]
struct RuntimeHandle
{
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

- optimize closed-world by default for executable builds when source bodies,
  package typed bodies, or package optimization facts are available
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
- stronger slice, dynamic storage, bounded raw pointer, and array qualifiers can justify `align`, better range reasoning, and more aggressive loop/vectorization facts

These are compiler outputs, not language syntax.
They are emitted only when the implementation can prove them from the source rules plus body analysis.
For pointer-backed `retborrow` returns, the internal ABI may attach `nonnull`
and concrete `dereferenceable` / `align` facts to the function return and to
call results. Direct borrow-view returns such as slices and text keep their
ordinary aggregate view representation, and `ffi` boundaries keep the foreign
ABI shape.

### 2.1 Parameter Memory Contracts

Memory-backed callable parameters are non-overlapping by default. The type checker turns each accepted function signature into pairwise parameter memory facts after overload resolution and generic substitution. `where overlap(...)` removes the default non-overlap obligation for the listed relation, and `where same(...)` records a same-region obligation that safe callers must prove.

Function-pointer types carry the same relation facts. Because `fnptr` parameter
lists do not bind source names, function-pointer memory contracts use synthetic
names `arg0`, `arg1`, and so on, and those facts are preserved through function
item promotion, package-image type references, imported templates, type
substitution, indirect-call validation, and LLVM indirect-call attributes.

The default applies to parameters that describe reachable caller storage: slices, text views, borrows, initialization views, bounded raw pointer regions, and raw pointers. Scalar value parameters and ordinary by-value owned aggregates do not receive a user-facing non-overlap obligation merely because the ABI may pass them indirectly.

`disjoint`, `overlap`, and `same` clauses are pairwise within each listed group. `where overlap(a, b, c)` removes default non-overlap for `a/b`, `a/c`, and `b/c`; it does not affect any unlisted parameter. `where same(a, b)` is stronger than overlap: the caller must prove both arguments have the same memory root and compatible region identity.

Explicit `where disjoint(...)` remains useful for subregions and computed memory-region expressions. Whole-parameter disjoint groups on ordinary Stark functions are redundant with the default and are rejected with a fix-it diagnostic. FFI and assembly declarations do not receive default Stark non-overlap, so explicit whole-parameter `disjoint` remains the opt-in spelling at external ABI boundaries.

Memory relations are not transitive. `same(a, b), same(b, c)` does not prove `same(a, c)` unless that relation is also stated or separately proven, and `overlap(a, b)` does not permit overlap between `a` and any unlisted parameter.

Call-site validation uses the proof facts the compiler can see directly: default parameter facts, explicit memory-contract clauses, runtime `if disjoint(...)` branch facts, scoped `unsafe assume disjoint(...)` facts, independent local storage roots, exclusive mutable borrow roots, `out`/`init` destination roots, immutable slice/text-view backing roots, bounded raw pointer parameter regions, raw pointer region expressions, method receiver roots, distinct field projections, distinct literal indexes, non-overlapping integer index ranges, and compiler-visible text slice ranges. Unknown unbounded raw pointers, call results or other arguments without a statically identifiable memory root, and overlapping or unknown index ranges are rejected for default non-overlap obligations unless a specific scoped proof covers that relation.

For function parameters, proven non-overlap gives the compiler these backend facts:

- a parameter whose whole reachable memory region is disjoint from every other accessible pointer region is emitted with LLVM `noalias` when the LLVM parameter rules allow it
- individual loads, stores, and memory-touching calls through disjoint roots carry scoped `!alias.scope` and `!noalias` metadata
- inlined bodies preserve disjointness through scoped noalias metadata instead of relying only on parameter attributes
- disjoint output or initialization destinations compose with `writeonly`, `initializes(...)`, and dead-store reasoning when the initialized byte range is known
- disjoint readonly inputs compose with `readonly`, `captures(none)`, or read-only `captures(...)` facts

The compiler treats non-overlap as a memory-range fact, not as a root-identity fact. Two slices or raw pointer regions from the same allocation can be disjoint when their element ranges do not overlap. Two different local values are not considered disjoint merely because their names differ; local pointer facts remain provenance-based, so pointer copies and simple casts preserve same-region identity.

### 2.2 Branch-Scoped Disjointness

`if disjoint(a, b)` creates a control-flow-scoped memory fact. The true branch carries a proven no-overlap relation for the listed memory regions. The false branch keeps ordinary conservative aliasing behavior.

For contiguous slices, text views, bounded raw pointer parameters, and raw pointer region expressions, the check lowers to pointer-range comparisons over the data pointer, element size, and length. Once the true branch is selected, memory operations through the checked regions receive scoped `!alias.scope` and `!noalias` metadata. If the fact is introduced inside a nested scope or loop body, the compiler uses a distinct alias-scope domain for that scope and emits `llvm.experimental.noalias.scope.decl` when the selected LLVM representation needs an explicit scope boundary.

The runtime check is a source-level fact boundary. Optimizer metadata must not be attached outside the dominated true-branch region unless later analysis proves the fact still holds.

`unsafe assume disjoint(a, b) statement` creates the same scoped fact without a runtime branch. It is the explicit unsafe boundary for trusted external separation facts. Inside an already unsafe context, the parser also accepts `assume disjoint(a, b) statement`; outside an unsafe context, bare `assume` is rejected before MIR. The assertion must name visible memory roots or representable subregions, so the compiler can attach the fact only to the intended roots. It does not make an ordinary `unsafe` block a blanket aliasing escape hatch, and it does not suppress same-root overlap, hidden helper-call roots, or pointer values laundered through integers.

### 2.3 Bounded Raw Pointer Regions

A bounded raw pointer region is the typed triple of base pointer, element type, and element count, plus an optional start offset for region expressions. Source forms include bounded raw pointer parameter types such as `rawptr<T>[count]` and `rawmutptr<T>[count]`, raw pointer region expressions such as `pointer[start, count]`, and unsafe raw slice construction with `slice(pointer, count)`.

The bound is a source contract. A positive element count requires a non-null base pointer that is valid for the full byte range `count * sizeof(T)`. A zero-length region may use `null`. The region carries the pointer's mutability, readonly or const provenance, alignment facts, memory root key, and element count range through MIR and SSA.

Bounded raw pointer regions produce these backend facts:

- loads and stores through the region use inbounds GEP flags only when the access index is proven within the bounded region
- parameters with statically known positive byte extents receive `nonnull`, `noundef`, `dereferenceable`, and `align` attributes when the pointer contract and target ABI rules make those attributes valid
- parameters with runtime-sized but statically positive element counts receive `nonnull` and concrete `align` attributes without pretending the byte extent is constant-size `dereferenceable`
- bounded raw pointer parameter count expressions inside `fnptr` types are stored on the function-pointer type using synthetic `argN` names, preserved through package-image type references, remapped from real parameter names during function-item promotion, and used to rebuild the synthetic ABI for indirect calls
- variable-size regions emit range facts and dominated `llvm.assume` facts where those assumptions strengthen alias analysis or loop optimization without inventing false constant-size dereferenceability
- disjoint bounded regions lower to LLVM parameter `noalias` where valid and to scoped `!alias.scope` / `!noalias` metadata on the individual memory operations that use the bounded roots
- readonly bounded regions from `rawptr<T>[count]`, `const`, or frozen provenance lower to `readonly`, read-only memory effects, and `!invariant.load` only where permanence and replacement rules make the load invariant
- unsafe `slice(pointer, count)` construction preserves the bounded region's root, length, alignment, mutability, const, and disjoint facts in the produced slice value
- recognized disjoint copy loops over bounded raw pointer regions may lower to `llvm.memcpy`; overlap-safe copies lower to `llvm.memmove` when the source semantics require overlap preservation
- recognized fill loops over bounded raw pointer regions may lower to `llvm.memset` when the element representation and initialization semantics make byte fill valid

Raw pointer region expressions inside `where disjoint(...)` and `if disjoint(...)` are lowered as first-class region facts, not as runtime slice aggregates. Their range comparisons use byte intervals derived from base pointer, start offset, element size, and count.

### 2.4 Dynamic Storage and Initialization Views

`dynamic T` is the compiler-owned representation for owned capacity-bearing element storage. It is a source-level value with allocation provenance, capacity, element type, initialized-region facts, and destructor obligations. It is not a standard-library wrapper over raw pointers, and safe Stark code does not recover a public raw pointer from it.

The semantic model separates three regions:

- the dynamic owner value, which moves by ownership and releases its backing allocation at drop
- the initialized element region, which can be observed through `T[]`, `borrow T`, `borrow mut T`, and ordinary element access
- the spare element region, which can be observed only through `init T` or `init T[]` destinations

`init T` and `init T[]` are write-only initialization destinations. A load from an `init` destination is invalid. An `init` store constructs a value, marks the destination initialized for the surrounding control-flow proof, and starts any required lifetime. For dynamic storage, ordinary `init` writes extend the dense initialized prefix: the compiler accepts the current `Length`, the next visible compile-time slot, or an initialization view whose previous slots were already initialized. `MoveLast()` transfers the tail initialized element out of dynamic storage, decrements the owner length, and marks the former tail slot spare. `MoveAt(index)` transfers one initialized element out, shifts the later initialized suffix left with overlap-safe move semantics, decrements the owner length, and marks the former tail slot spare. Non-tail moves that do not use `MoveAt` require an explicit sparse initialized-slot proof before the compiler can preserve safe initialized-length facts. Dynamic owner destruction walks exactly the initialized prefix, runs element drops when `T` needs runtime destruction, skips spare capacity, and then releases the backing allocation.

Sparse initialized-slot proofs are represented by unsafe proof boundaries. Inside an unsafe boundary, ownership validation accepts dynamic slot reads, replacements, non-tail moves, and initialization writes whose initialized-slot fact is supplied by the programmer rather than by the visible dense prefix. Accepting that proof demotes the compiler-visible dynamic prefix to unknown, so the fact cannot leak into later safe code. Dense-prefix operations keep the stronger exact prefix state and remain the performance-oriented default.

Dynamic storage preserves the performance shape of low-level buffer code while giving the compiler stronger facts than ordinary raw pointers:

- the backing allocation carries element type, capacity, alignment, and allocator provenance
- `0 <= initializedLength <= capacity` facts are explicit at reserve, slice, move, drop, and initialization sites
- initialized slices derived from dynamic storage inherit the dynamic root, length range, alignment, and readonly/mutable provenance
- spare initialization views derived from dynamic storage inherit the dynamic root, element count, alignment, write-only authority, and initialized-on-write contract
- reserve operations preserve the initialized prefix and may reallocate the backing storage without exposing a raw pointer escape
- `TryReserve(additional)` has the same prefix-preservation contract as `Reserve(additional)`, but lowers through fallible allocator helpers and returns `false` instead of trapping on capacity overflow, byte-size overflow, or allocation failure
- tail moves preserve the dense initialized prefix by loading the old last element and committing `initializedLength - 1` to the dynamic owner
- indexed dense-prefix moves preserve the dense initialized prefix by loading the removed element, lowering the suffix shift to `llvm.memmove`, and committing `initializedLength - 1` to the dynamic owner
- raw pointer escapes from dynamic storage are not part of the safe surface and demote optimizer facts at the unsafe or FFI boundary that performs the escape

LLVM lowering uses the direct representation the target and ABI require, normally an owned header containing a backing pointer plus capacity/allocation metadata. Dynamic operations do not introduce virtual dispatch or mandatory runtime metadata lookups. The compiler emits ordinary pointer arithmetic for proven element accesses and uses the source initialization facts to decide which operations may read, write, move, drop, or skip memory.

Backend facts from dynamic storage include:

- fresh dynamic allocations receive allocation facts such as `noalias`, `nonnull`, `noundef`, `align`, `dereferenceable`, and `allocsize` when the request is nonzero and the allocator contract proves them
- dynamic reallocation is conservative about `noalias` when the runtime may return the original block
- initialized element accesses use `inbounds` GEP only when the index is proven within the initialized range
- spare initialization writes lower as write-only stores and can feed `initializes(...)`-style facts when the byte range is known
- dynamic owner drops use the stored initialized length as the loop bound, so destructible elements are destroyed without scanning spare capacity
- eligible bulk initialization loops over `init T[]` lower to `llvm.memset` or vectorized stores when representation rules allow it
- eligible bulk moves/copies from initialized dynamic ranges to disjoint initialization ranges lower to `llvm.memcpy`; overlap-preserving forms lower to `llvm.memmove`
- independent loops over initialized slices or initialization views can carry `!llvm.access.group`, `!llvm.loop.parallel_accesses`, scoped `!alias.scope`, and `!noalias` metadata when source disjointness proves non-overlap

Sparse data structures use explicit source facts for initialized slots. When the compiler can see the initialized range or slot identity, reads, moves, and drops are safe. When a data structure keeps a dynamic sparse state that the type checker cannot prove from control flow, the proof boundary is explicit and unsafe; it does not require converting the storage to a raw pointer. Code that uses an unsafe sparse proof is responsible for preserving the runtime dynamic owner invariant before ordinary safe code observes or drops the owner.

The standard library's `System.Collections.SparseSlots<T>` is the current
internal sparse initialized-slot view for ring-shaped collections. It owns the
allocation, moves only the caller-declared live ring range during growth, and
exposes direct slot move/borrow helpers so `Queue<T>` and `RingQueue<T>` avoid
per-slot enum tags in their hot paths.

### 2.5 Standard Library Promotion Status

The release standard library exposes only canonical `System.*` modules. The
temporary `System.Experimental.*` comparison modules used during promotion have
been removed from `stdlib/src`, and package images are expected to contain only
canonical modules and root re-exports.

Promoted collection, text, runtime-buffer, IO, filesystem, console, and network
modules keep the dynamic-storage contracts validated during the comparison
period. `dynamic T.TryReserve(additional)` returns an explicit success bit,
preserves the initialized prefix on success, and leaves the owner unchanged on
failure. Standard-library code maps capacity overflow, byte-size overflow, and
allocation failure into status/result values without exposing raw allocation
storage in ordinary public APIs.

### 2.6 Independent Loops

`independent` on a `while` or `for` loop means loop iterations have no loop-carried memory dependencies. The loop body may still use induction variables, local scalar temporaries, and immutable reads, but a memory write in one iteration may not be read or written by another iteration.

Loop behavior and independent-loop contracts lower as separate facts. A
`willexit` loop carries a progress fact through MIR and SSA and receives
`!llvm.loop.mustprogress` on its loop backedge. An `independent` loop carries
memory-dependence facts through MIR and SSA and lowers to LLVM loop-dependence
metadata:

- memory operations covered by the contract receive `!llvm.access.group`
- the loop latch receives `!llvm.loop.parallel_accesses` referencing those access groups
- vectorization and interleaving hints are attached only when the independent contract and target cost model justify them

The contract is semantic, not a hint. If a loop marked `independent` contains a real loop-carried memory dependence, the program violates the Stark source contract. Safe Stark code must either prove the contract statically or establish the required disjointness through checked facts such as `if disjoint(...)` before entering the loop.

The accepted memory-backed subset requires a single mutable integer induction variable incremented by exactly one. Slice and fixed-array memory accesses use the simple form `root[index]`; bounded raw pointer memory accesses may also use the raw pointer spelling `*(&root[index])`. Field projections are accepted when they are rooted at the per-iteration element, such as `root[index].field`, and their field path participates in the memory-root key used for dependency checks. Structured `if` statements are accepted when their condition and every branch satisfy the same dependency-validation subset. In all cases, `index` is the loop induction variable, and write/read root pairs are either the same indexed root or proven disjoint by source facts, bounded raw pointer region facts, borrow exclusivity, or an enclosing `if disjoint(...)` fact. Law calls with scalar returns are allowed after their argument memory reads have been validated; calls with unproven memory effects report `STK3027`. Accepted independent loops carry their loop contract and access groups through MIR/SSA, emit LLVM `!llvm.access.group` on covered memory operations, and attach `!llvm.loop.parallel_accesses` metadata on the loop backedge; `willexit` controls whether that same backedge also receives `!llvm.loop.mustprogress`. Unbounded pointer dereferences, address-of expressions that create new unbounded regions, member projections that are not rooted at `root[index]`, non-induction indexes, memory-backed local declarations, nested loops, early exits, and unsupported calls remain outside the accepted subset.

### 2.7 Const Parameters

`const` on a parameter means the reachable object graph has const provenance and is deeply immutable. It is stronger than `frozen`, which is a borrow-duration readonly view. A const parameter describes memory that safe Stark code cannot mutate at any point through any reachable path.

Const parameters produce these backend facts:

- pointer-like ABI values are `nonnull`, `noundef`, `dereferenceable`, and aligned according to the concrete type layout
- argument memory is `readonly`, and functions that only read const parameters receive restrictive `memory(...)` attributes
- captures are `captures(none)` when the pointer does not escape, or `captures(address, read_provenance)` when readonly provenance is stored for later reads
- loads through permanently immutable const provenance carry `!invariant.load` when the loaded object cannot be replaced for the lifetime represented in IR
- projections from const parameters preserve frozen/readonly provenance and keep raw conversions from regaining mutable authority

Const does not imply local disjointness. Multiple local const views may alias the same immutable object graph. Memory-backed function parameters remain non-overlapping by default unless the callee declares `where overlap(...)` or `where same(...)`; the compiler emits `noalias` only when the resulting parameter contract and call-site proof make the fact sound.

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
`max` is `2 ** N - 1`.

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
`const BoardWidth = 80;` is stored as `u8[80 80]`, while
`const BigCount = 2 ** 16;` is stored as `u24[2 ** 16 2 ** 16]`.

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
LLVM emitter attaches indirect-call target metadata such as `!callees` when
every possible SSA target is a known function address. Opaque targets from
parameters, memory loads, call results, or external ABI boundaries do not get
target metadata.

Function-pointer types carry the source function kind as part of the type. A
`fnptr<law ...>` target has stronger source obligations than a general
`fnptr<fn ...>` target, and the compiler may preserve those obligations in
call-effect summaries and package-image facts. The runtime representation is
still a thin code pointer; the kind is a compiler contract, not a different
pointer ABI. LLVM lowering uses the preserved kind at indirect call sites:
`fnptr<finite ...>` calls may receive `willreturn` and `mustprogress`, while
`fnptr<law ...>` calls may receive the strongest sound readonly/purity,
`nosync`, `nofree`, and memory-effect attributes. `fnptr<finite law ...>`
combines both sets. Plain `fnptr<fn ...>` remains the general indirect-call
form and does not receive those stronger attributes without another proof.
Accepted `fnptr` values are also non-null. Type checking rejects `null`
assignment and rejects aggregate initializer shapes that would zero-fill a
function-pointer field or fixed-array element. LLVM ABI emission may therefore
mark direct function-pointer parameters and direct function-pointer returns as
`nonnull` without relying on a backend guess.
When a `fnptr` parameter is a bounded raw pointer such as
`rawptr<T>[arg1]`, the synthetic `arg1` count is part of the callable ABI
contract. Indirect-call lowering reconstructs a synthetic callee signature with
that count expression, so a positive runtime count parameter can still justify
`nonnull` and `align` on the pointer argument at the indirect call site.

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

The `System.Collections.DictionaryKey<T>` doctrine carries compiler-known equality/hash
machinery (`SystemCollectionsDictionaryKeyFacts`) that synthesizes element equality for the
built-in scalar/text key types — bool, the integer widths, `ascii`, and `unicode` — at
monomorphization. Generic algorithms can therefore call `System.Collections.DictionaryKey.Equals(a, b)`
directly on an *unbounded* type parameter to get element equality without per-type overloads
(the same path `Dictionary`/`HashSet` keys use). This is what backs the generic
`System.Testing.ContainsElement<T>` / `SequenceEqual<T>` helpers; equality does **not** route
through `==` on a generic `T` (the type-checker's equality gate has no trait-bound branch), so
the doctrine call is the idiom, not the operator.

This is useful compiler context, but the user-facing rules for writing doctrines remain part of [LanguageReference.md](../Userfacing/LanguageReference.md).
