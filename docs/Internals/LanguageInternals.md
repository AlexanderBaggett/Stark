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

The SSA pipeline exposes two rendered artifacts at different layers: `lowering.ssa`
is the pre-optimization SSA, printed tersely (each instruction as `vN = <op> @
line:col`, operator and source location only, without operands), and
`lowering.ssa.optimized` is the post-optimization SSA the optimization passes
write into, printed with fold-synthesized operands and coefficients (`v1_mul_1 =
arg_value * 3`, `arg_value ** 3`, constant-folded terminators like `return 0`).
Both artifacts are produced even when compilation stops after an early
optimization pass, so a golden test inspecting a fold/cleanup/inline/memory-opt
result reads `lowering.ssa.optimized` at that stop point. See
`docs/Internals/HostCompilerTestProtocol.md` for the test-harness surface.

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

Call-site validation uses the proof facts the compiler can see directly: default parameter facts, explicit memory-contract clauses, runtime `if disjoint(...)` branch facts, scoped `unsafe assume disjoint(...)` facts, independent local storage roots, exclusive mutable borrow roots, `out`/`init` destination roots, immutable slice/text-view backing roots, bounded raw pointer parameter regions, raw pointer region expressions, method receiver roots, distinct field projections, distinct literal indexes, non-overlapping integer index ranges, compiler-visible text slice ranges, and read-only constant arguments. A read-only constant argument lives in independent const storage that can never be the writer in an aliasing hazard, so it is disjoint from every other argument: a direct string literal has always qualified, and a conditional/ternary whose branches are all read-only constants (including nested conditionals, and a local bound to such an expression) qualifies as well — `cond ? "llvm" : "llvm-normalized"` carries an independent-storage memory root through `EvaluateConditionalExpression`. A conditional with at least one non-constant branch is not treated as constant storage and still requires an ordinary proof. Unknown unbounded raw pointers, call results or other arguments without a statically identifiable memory root, and overlapping or unknown index ranges are rejected for default non-overlap obligations unless a specific scoped proof covers that relation.

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

Vendor bindings live under a separate bundled `Vendor.*` root rather than inside
`System.*`. In a release SDK, both namespaces resolve through the exact
module-to-package ownership index in `<sdk-root>/sdk.json`; the compiler does
not scan a nearby `vendor` directory, walk project ancestors, or consult
`STARK_PATH`. A development or bootstrap SDK may expose source and stage roots,
but those roots are explicit relative paths in its manifest and do not change
the namespace ownership rule.

An official native-backed vendor package is a complete, target-specific SDK
asset. Its package image preserves the Stark/LLVM ABI and optimization facts,
its Stark archive carries the compiled wrappers, and its package-local native
directory carries the required native archives, headers, licenses, runtime
files, and ordered link facts. Every path stored in a release manifest is
relative to the SDK root, every required file is checksummed, and release
packages contain no unresolved `pkg-config` query or machine-local path. The
final linker receives only the selected package graph; unused vendor packages
are not loaded or linked. `pkg-config`, user paths, and source-native fallback
remain authoring inputs for custom or development packages, not installation
steps for an application importing an official `Vendor.*` module.
The complete discovery and archive contract is in
[SDK Layout and Resolution](SdkLayoutAndResolution.md); the serialized compiler
and native fact contract is in [Stark Package Image](PackageImage.md).
Safe public vendor surfaces should keep raw handles and ABI-only carrier shapes
inside the binding whenever possible. For example, `Vendor.SDL3` exposes safe
Stark handles and result enums while its package-owned `Sdl3Binding.c` adapter
normalizes SDL's C `bool` returns, flattens the `SDL_Event` union into a
C-layout event record, and avoids exposing callback-shaped audio APIs to safe
Stark callers. This is a legitimate native adapter use: it preserves
allocation-free event/audio paths without making ordinary Stark code reason
about C unions, nullable handles, or callback lifetimes.
`Vendor.Miniaudio` and `Vendor.Cgltf` use pinned single-header source drops
behind small C implementation files, keeping native ownership and callback
details internal while Stark callers operate on safe handles and caller-owned
buffers. `Vendor.GLFW` uses a package-owned callback bridge for window events,
while `Vendor.Raylib`, `Vendor.Raymath`, and `Vendor.Rlgl` use direct
`[LinkName]` declarations and C-layout aggregate carriers where the native ABI
is already expressible.

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

### 2.8 Concrete Inline Layout Validation

Named aggregate lowering requires a finite concrete layout before MIR, ABI, or
LLVM IR emission can safely materialize storage, zero constants, field offsets,
drop order, or aggregate LLVM types. The type checker therefore rejects
recursive inline-layout cycles with `STK3056`.

The layout-cycle walk follows source-resolved storage edges:

- `struct` and `record` fields are inline edges
- enum tuple payloads, named payload fields, and `from` payloads are inline
  edges from the enum to the payload type
- fixed arrays are inline edges to their element type
- generic aggregate fields are checked after substituting the concrete type
  arguments at the use site, so `Node -> Box<Node> -> Node` is rejected when
  `Box<T>` stores `T` inline

Pointer-like or out-of-line representations stop the inline walk:

- `rawptr<T>` and `rawmutptr<T>`
- safe borrows and initialization destinations
- slices, text views, function pointers, closures, and dyn-trait handles
- `dynamic T`, whose stored owner/header is fixed-size while its elements live
  in backing storage

After this validation succeeds, aggregate layout code can assume every accepted
named struct, record, enum payload graph, and generic instantiation has finite
inline size. `ConcreteTypeLayoutHelper` still treats unresolved or cyclic layout
queries as non-computable for defensive package/layout facts, but user-reachable
recursive by-value layout is a front-end diagnostic rather than a lowering
failure. LLVM type emission should only see finite aggregate bodies; recursive
source data structures must cross an explicit indirection boundary before they
reach LLVM representation selection.

## 3. Internal ABI and FFI Boundaries

At the source level, `ffi` marks a foreign-facing function boundary.
Internally, the compiler treats ordinary non-`ffi` Stark calls differently from `ffi` calls.

The current implementation uses a faster internal calling convention for non-`ffi` internal Stark calls when it can.
By contrast, `ffi` boundaries preserve foreign ABI expectations and should be treated as the stable interop-facing surface.

This is an implementation detail.
It matters for code generation and interop, but it is not meant to change how ordinary Stark code is written.

### 3.1 FFI Link Names

An imported FFI function has two names in the compiler:

- the Stark source/resolved name used for lookup, overload resolution, function
  items, wrappers, package APIs, and diagnostics
- the external link name used for the LLVM global symbol

Without `[LinkName("...")]`, the external link name is the Stark declaration
name, preserving the original FFI behavior. With `[LinkName("foreign_symbol")]`,
type checking stores the decoded symbol on the `TypedFunctionSignature` as the
function's external link name. MIR and SSA continue to refer to the resolved
Stark function symbol; ABI lowering copies the external link name into
`AbiFunctionSignature.SymbolName`.

LLVM emission consumes `AbiFunctionSignature.SymbolName` for:

- external FFI `declare` signatures
- direct FFI call targets
- function-address materialization for FFI function items promoted to `fnptr`

Non-FFI Stark definitions continue to use the normal internal/export symbol
rules. `LinkName` does not affect ABI classification, calling convention,
varargs, sret/indirect aggregate lowering, parameter attributes, ownership, or
effect facts.

Package images preserve the lowered symbol in ABI facts and the explicit link
name in typed/source surfaces. Source bridging re-emits `[LinkName("...")]`
when an imported FFI declaration's foreign symbol differs from the Stark source
name, so downstream packages do not need the original source file to call the
correct native symbol.

ABI lowering rejects conflicting FFI declarations that map to the same
`(resolved FFI ABI, external link name)` but lower to incompatible LLVM
signatures. Compatibility is checked on the lowered ABI return, parameter kinds,
parameter LLVM types, and varargs flag, because that is the boundary LLVM and
the native linker observe.

### 3.2 C `va_list` Carrier

`System.C.VaList` is a compiler-known C ABI carrier, not an ordinary source
struct. Type resolution recognizes the `System.C.VaList` spelling through the
same compiler-known `System.C` alias path as `c_int`, `c_size_t`, and `c_void`.

The type checker permits `System.C.VaList` only in places where the frontend can
preserve the C ABI contract:

- a direct parameter of an unsafe `ffi(c)`-compatible function
- a direct parameter of an `ffi(c)`-compatible `fnptr`
- the direct pointee of `rawptr<System.C.VaList>` or
  `rawmutptr<System.C.VaList>`

Direct locals, fields, arrays, returns, ordinary Stark function parameters, and
non-FFI callback parameters are rejected with STK3051. This keeps `va_list`
from becoming part of Stark's own calling convention or layout model.

LLVM ABI emission lowers a direct `System.C.VaList` parameter to the active
target's C ABI carrier. On the currently supported C varargs ABIs this is an
opaque pointer (`ptr`), matching the shape Clang exposes for `va_list`
parameters on those targets. Package images preserve the source alias spelling
so downstream source bridges re-emit `System.C.VaList` rather than an
implementation pointer type.

### 3.3 C ABI Aggregate Carriers

`[StructLayout(C)]` controls memory layout; `ffi(c)` controls the call ABI. When
both apply to a by-value aggregate parameter or return, ABI lowering classifies
the source Stark struct with the active target's C ABI and records the selected
LLVM carrier shape in `AbiFunctionSignature`.

The source type is not rewritten. MIR and ordinary Stark calls still see the
named aggregate. The ABI edge packs a Stark aggregate value into one or more
LLVM carrier values before a call, and materializes returned or incoming carrier
values back into the source aggregate type at the Stark boundary.

On x86_64 System V, current lowering follows Clang's small-aggregate carrier
forms:

- `struct { f32, f32 }`: one `<2 x float>` carrier for parameters and returns.
- `struct { f32, f32, f32 }`: parameter carriers `<2 x float>, float`; return
  carrier `{ <2 x float>, float }`.
- `struct { f32, f32, f32, f32 }`: parameter carriers `<2 x float>, <2 x
  float>`; return carrier `{ <2 x float>, <2 x float> }`.
- integer/pointer storage of up to two eightbytes uses integer carriers such as
  `i32`, `i64`, or `{ i64, i64 }` for returns and split integer parameters.
- aggregates larger than 16 bytes, misaligned aggregates, and unclassifiable
  shapes use the target ABI's indirect form (`sret`, `byval`, or equivalent).

On AArch64 AAPCS64, the currently implemented non-HFA/HVA integer-like
aggregate slice follows Clang's distinction between argument and result
carriers:

- a parameter no larger than 8 bytes is copied into one general-purpose
  register and represented as an `i64` carrier, with bytes above the aggregate
  value treated as padding
- a return no larger than 8 bytes uses its exact integer width, such as `i32`
  for a four-byte aggregate
- a 9-16 byte parameter or return uses `[2 x i64]`
- an aggregate larger than 16 bytes uses the target ABI's indirect form

Consequently, parameter and return carriers are independent facts even when
the source type is the same. Raylib's four-byte `Color` is the canonical
example: a direct C call returns it as `i32` but passes it as `i64`. Reusing the
return carrier for the parameter would place only the first byte correctly on
Apple arm64, producing corrupt colors or a zero alpha channel. Homogeneous
floating-point/vector aggregate classification remains a distinct AAPCS64
path; it must not be coerced through the integer-like rule.

The ABI model can represent one or many physical LLVM values for one Stark
source parameter. `AbiParameterSymbol.LlvmType` retains the canonical direct
aggregate carrier, while `LlvmParameterTypes` records a distinct physical
parameter-carrier sequence. That sequence is significant even when it contains
only one value: AArch64 `Color` has canonical `i32` and physical parameter list
`[i64]`. Multi-register System V aggregates use the same field for sequences
such as `[<2 x float>, <2 x float>]`. LLVM declarations, definitions, calls,
function-entry reconstruction, and function pointers must consume the physical
carrier sequence; return lowering must consume the independently classified
return carrier.

Package images preserve all three facts. Compiler-facts ABI manifests serialize
the source type, canonical aggregate carrier, and physical parameter-carrier
sequence—including a one-element sequence that differs from the canonical
carrier—so downstream packages emit the same external declaration when the
original Stark source file is absent.

This makes compiler and SDK publication an atomic ABI operation. If target ABI
classification or carrier packing changes, every affected official package
image and wrapper archive must be rebuilt with that compiler before an SDK is
assembled. Replacing only `bin/stark` while retaining an older
`Vendor.Raylib.starkpkg` or `libVendorRaylib.a` is unsupported even when the
source declarations did not change. SDK API/content identities and file
checksums bind the exact artifacts selected during assembly and prevent later
substitution. Clean package rebuilding plus release qualification that executes
native by-value parameter and return round trips are what detect a semantically
stale wrapper at publication time.

### 3.4 Assembly Clauses, Opaque Reachability, And Memory Effects

Assembly functions are unsafe FFI boundaries, but their compiler facts must be
structured rather than inferred from target-specific text. `Stark.g4` therefore
extends the assembly clause list with these contextual productions:

```text
asmSymbolClause  : "symbol" "(" qualifiedName ")"
asmMemoryClause  : "memory" "(" "none" ")"
                 | "memory" "(" asmMemoryAccess ("," asmMemoryAccess)* ")"
asmMemoryAccess  : ("read" | "write" | "readwrite")
                   "(" parameterName ")"
```

The actual grammar recognizes the words through contextual-keyword predicates,
so `symbol` and `memory` are not globally reserved identifiers. Semantic
validation, rather than the permissive identifier tokens in the grammar,
restricts the accepted words to `none`, `read`, `write`, and `readwrite` and
reports malformed contracts as STK2109.

The syntax model represents three distinct states:

- no memory clause: unknown/arbitrary memory, retaining LLVM `~{memory}`
- an explicit memory model with no operands: `memory(none)`
- an explicit list of bounded raw-pointer argument accesses

Named accesses must refer to a parameter, the parameter must be a bounded
`rawptr<T>[count]` or `rawmutptr<T>[count]`, it must also be an assembly input,
and a write requires `rawmutptr`. A parameter occurs at most once in the memory
list; read plus write is represented as `readwrite`. Only one memory clause is
accepted. These checks prove that LLVM can associate the declaration with
argument memory; they cannot prove that the target template is honest or
complete, which remains part of the unsafe source contract.

`symbol(Name)` holds a typed source name for a function or global that is
otherwise visible only in the template string. Resolution is relative to the
assembly owner's module unless the name is qualified. Duplicate and unresolved
references are STK2109 errors. The clause never edits the template. When a live
assembly call or bridge is emitted, the resolved LLVM symbol receives one
deduplicated `@llvm.used` entry. Ordinary calls and address-taking use normal
LLVM/linker reachability and never need this retention path; dead assembly does
not retain its opaque references.

Both models are serialized in package-image assembly metadata alongside the
architecture, template, operands, and clobbers. A source-free package consumer
must recreate the same inline-assembly plan. Legacy declarations and package
metadata with no explicit memory model remain conservative rather than being
silently upgraded to `memory(none)`.

At LLVM lowering:

- a direct call becomes inline assembly at its call site
- an address-taken or exported assembly function keeps a callable bridge
- omitted memory keeps `~{memory}` and emits no contradictory memory attribute
- `memory(none)` removes the barrier and emits `memory(none)`
- named accesses remove the universal barrier and emit the union as
  `memory(argmem: read)`, `write`, or `readwrite`
- an explicit opaque symbol reference alone creates an `@llvm.used` root

The memory-effect clause is independent from Stark's relational parameter
contracts. `memory(read(source), write(destination))` describes operations;
`where disjoint(source, destination)` describes aliasing. Preserving both lets
LLVM optimize aggressively without inventing facts. Full source examples,
validation rules, bridge linkage, and migration guidance live in
[`ASMFunctionApproach.md`](ASMFunctionApproach.md).

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

Native callback registration is the direct composition of `ffi(abi)` function
bodies and `fnptr<unsafe ffi(abi) fn ...>` promotion. When a Stark function body
is declared with `unsafe ffi(c)` or another explicit ABI, the function-effect
model marks it as FFI, ABI lowering uses the target C calling convention rather
than `fastcc`, and promotion to a matching unsafe FFI function-pointer type
records the function as address-taken. LLVM emission defines the callback body
with the foreign ABI and passes the symbol address directly to the registering
foreign function; no thunk, closure allocation, or trampoline is introduced.
`export` only changes symbol visibility for name-based lookup and is not needed
when the foreign side receives the callback pointer as an argument. Callback
registration wrappers may remain safe when their surface accepts an already
unsafe `fnptr<unsafe ffi(...) fn ...>`; declaration checking treats the unsafe
function-pointer carrier as the proof boundary while direct raw pointer
parameters and safe function pointers with raw pointer parameters still require
an unsafe function. Callback signatures containing ABI-sensitive C helper types
that Stark does not yet model exactly, such as `va_list`, must stay at a raw
unsafe edge or use a native adapter until a target-aware carrier exists.
Unsafe explicit conversions between raw pointers and function pointers are the
escape hatch for native loader APIs. Type checking accepts only an explicit
cast in an unsafe context; callers are responsible for proving the raw pointer
is non-null and names code with the exact ABI/signature carried by the `fnptr`
type. MIR and SSA carry this as a normal conversion, but LLVM opaque pointer
emission treats raw-pointer/function-pointer conversions as value-shape no-ops:
no `ptrtoint`, `inttoptr`, or `bitcast` is emitted, and downstream uses format
the original `ptr` value directly. This preserves pointer provenance and
keeps dispatch-table construction free of wrapper calls.
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

## 10. Graduated Feature Implementation Contracts

This section records compiler-facing contracts that graduated from feature
design and implementation tracking. Each entry pairs the source-language
contract with the lowering and LLVM emission facts that make the feature
valuable.

### Integer Range Facts At The LLVM Boundary

Stark range-typed integers carry value bounds in the type system:

```stark
unsafe fn u8[0 10] Bounded(u8[0 10] input)
{
    return input;
}

unsafe fn i32[min max] Mask(u8[0 15] value)
{
    return value & 7;
}
```

The user-facing contract is that `u8[0 10]`, `i32[-7 10]`, `u32[0 max]`,
single-value constant ranges, and range endpoints derived from `min` and `max`
are not comments. They are checked types. Values crossing a typed boundary must
remain inside the declared range, and operations that produce narrower facts can
be represented by the compiler when the proof is available.

The compiler contract is that range facts should be preserved at the LLVM
boundary whenever LLVM has a legal representation for them:

- Direct non-FFI scalar parameters use LLVM `range(...)` attributes when the
  Stark type is a non-full integer range.
- Direct non-FFI scalar returns use LLVM `range(...)` attributes. When SSA value
  facts prove a narrower return range than the declared return type, the
  narrower fact should be preferred.
- Loads of range-typed integer storage use LLVM `!range` metadata when the range
  is non-full.
- Direct call operands and call results carry range facts when the callee ABI
  and result type allow it.
- Mid-function control-flow refinements may emit `llvm.assume` for facts that
  cannot be expressed as boundary attributes or load metadata.

This is a real source-language advantage. C, Rust, and Zig can express machine
integer widths, but they cannot generally express "this parameter is a `u32` in
`[0, 3]`" as a first-class type and have that fact flow to LLVM as
`range(i32 0, 4)`. They can get similar facts only through visible runtime
checks, manual intrinsics, or specialized library/compiler behavior.

Current implementation notes:

- `LlvmValueRangeFacts` builds LLVM `range(...)` attributes and `!range`
  metadata bodies from Stark integer types and SSA integer range facts.
- `LlvmFunctionAttributeBuilder` emits parameter and return `range(...)`
  attributes for eligible non-FFI ABI surfaces.
- `LlvmFunctionBodyEmitter` emits `!range` metadata on many typed load and call
  result paths.
- Branch-refined integer comparisons currently emit ordinary `llvm.assume(i1
  condition)` facts. LLVM range operand bundles are not currently modeled as a
  separate assume bundle kind.

Missing range-assume-bundle work:

Stark currently handles the main boundary forms well: parameter and return
`range(...)` attributes, plus `!range` metadata on loads and call results. The
remaining gap is mid-function range facts that arise after control-flow
refinement, opaque-source materialization, or other value-fact analysis where no
parameter, return, load, or call-result surface exists.

Today, Stark emits branch-refined integer facts as boolean assumptions, for
example by assuming the branch condition or its negation in a dominated
successor block. That is correct, but it forces LLVM to rediscover the range
from an instruction sequence. The desired improvement is to emit the most direct
LLVM representation for a known value range at the assume site, analogous to how
`nonnull` and `align` are already represented as assume operand bundles in
Stark's emitter.

The implementation should not guess the syntax. Before adding a new bundle kind,
confirm the exact assume-bundle support in the LLVM version Stark targets. If
that LLVM version accepts a range assume operand bundle, model it explicitly in
the emitter and use it for known integer range facts. If it does not, document
that ordinary `llvm.assume(i1 condition)` is still the canonical representation
for mid-function integer range refinement, and keep range facts on the existing
attribute and metadata surfaces.

Confirmed LLVM 22.1.6 status: bounded integer range facts are not available as
a useful textual assume operand bundle. The assembler accepts a one-operand
`"range"(iN %value)` bundle, but that spelling carries no lower/upper bound;
bounded forms such as `"range"(iN %value, iN lo, iN hi)` are rejected. Stark
must therefore keep using `range(...)` attributes, `!range` metadata, and
ordinary boolean `llvm.assume` conditions until the targeted LLVM exposes a
usable ConstantRange assume-bundle form.

The intended source scenarios are:

```stark
unsafe fn u8[0 100] Refine(i32[min max] value)
{
    if (value >= 0 && value <= 100)
    {
        // Inside this block, value is known to be in [0, 100].
        return (u8[0 100])value;
    }

    return 0;
}

unsafe fn u8[0 10] FromOpaque(rawptr<u8[0 max]> source)
{
    stack u8[0 max] value = *source;
    if (value <= 10)
    {
        // The load may only have the storage type's broad range. The dominated
        // block has a narrower value fact that should be visible to LLVM.
        return (u8[0 10])value;
    }

    return 0;
}
```

The generated IR should avoid weakening the existing boundary facts. Parameters,
returns, loads, and calls should continue to use `range(...)` and `!range`
directly. Assume emission is only for facts discovered after those boundaries or
facts attached to an SSA value that cannot otherwise carry range metadata.

Example LLVM shape:

```llvm
define fastcc noundef range(i8 0, 11) i8 @Bounded(
    i8 noundef range(i8 0, 11) %arg_input)

define fastcc noundef range(i32 0, 8) i32 @Mask(
    i8 noundef range(i8 0, 16) %arg_value)

%v1 = load i8, ptr %slot_value, !range !32
!32 = !{i8 -10, i8 11}
```

Benchmark notes:

- Benchmarks that are meant to show range-typed integer wins should state that
  the compared C/Rust/Zig source cannot express the same type-level range fact
  directly.
- A C/Rust/Zig baseline may still use natural visible checks when that is how a
  competent implementation would validate input.
- Do not replace a normal C/Rust/Zig implementation with hand-written LLVM
  assumptions or source patterns that exist only to mimic Stark's backend facts,
  unless the benchmark is explicitly labeled as an optimizer-parity experiment.

### Function Effects And Structured Memory Attributes

Stark function kinds carry effect guarantees that should reach LLVM directly:

```stark
law i32[min max] ReadOnly(borrow Box box)
{
    return box.Value;
}

finite i32[min max] Terminates()
{
    return 1;
}

finite law i32[min max] PureAndTerminates()
{
    return Terminates();
}
```

The user-facing contract is that `law` means no visible side effects and no
cross-thread synchronization, while `finite` means the function returns to its
caller rather than diverging. `finite law` combines both guarantees. Those
source contracts are stronger than optimizer inference, especially across
package-image boundaries where the original body may not be present.

Current LLVM emission status:

- `law` functions receive `nosync` and `nofree`.
- `finite` functions receive `willreturn` and `mustprogress`.
- eligible internal functions receive `nounwind`.
- function and function-pointer call sites use the modern structured
  `memory(...)` attribute form, including shapes such as `memory(none)`,
  `memory(read)`, and `memory(argmem: read)`.
- closed-world proven nonrecursive functions receive LLVM `norecurse`; functions
  that can reach opaque call edges such as FFI, varargs, function pointers,
  closures, dynamic dispatch, unresolved calls, and declaration-only calls
  without imported summaries stay conservative.
- package images preserve `FunctionEffectProfile` and
  `FunctionMemoryEffectSummary`, so imported/package-backed code can rebuild the
  same function-effect and memory-effect attributes.
- package images preserve the `NoRecurse` function-effect fact for imported
  functions whose package summary proves it.

This is already a Stark strength. C, Rust, and Zig generally rely on backend
inference for these facts at module boundaries, while Stark has source-level
function kinds and package summaries.

The implementation should keep using `memory(...)` rather than regressing to
legacy whole-function `readonly`, `readnone`, or `argmemonly` spellings. Pointer
parameter attributes such as `readonly` and `writeonly` remain separate ABI
facts and are still useful.

### Guaranteed Tail Calls With `tail` And `become`

Stark exposes guaranteed tail calls as a semantic control-flow contract, not as
an optimizer hint. The source program is rejected if the compiler cannot lower
the edge to a guaranteed tail call.

The surface has two parts:

- `tail` is a callable contract modifier. It says the function uses Stark's
  tail-callable internal ABI and can participate in guaranteed tail-call edges.
  It is contextual in callable modifier/signature positions; `tail` remains a
  valid ordinary identifier elsewhere.
- `become` is a terminating statement. It says "replace this stack frame with
  the callee" and must lower to an LLVM `musttail` call followed immediately by
  the matching `ret`.

`tail` composes with the existing function kinds because stack behavior,
effects, and termination are separate promises:

```stark
tail fn State Dispatch(State state)
{
    become Step(state);
}

tail law i32[min max] Normalize(Node node)
{
    become NormalizeNode(node);
}

tail finite i32[min max] Countdown(i32[0 max] remaining)
{
    if (remaining == 0)
    {
        return 0;
    }

    become Countdown(remaining - 1);
}

tail finite law State Eval(State state)
{
    switch (state.Kind)
    {
        case .Parse:
            become Parse(state);

        case .Execute:
            become Execute(state);

        case .Done:
            return state;
    }
}
```

The user-facing contract is:

- `become f(args);` is a terminator like `return`; code after it is unreachable.
- `become` is legal only in true tail position.
- the caller must be `tail`.
- the callee must be tail-callable: a `tail` Stark function, a tail-callable
  function pointer type, or a trait/dynamic dispatch target whose callable type
  carries the tail contract.
- `become` may target `fn`, `law`, `finite`, or `finite law` functions as long
  as the ordinary effect and call-capability rules allow the call.
- the callee's result must be returned directly, with no pending computation,
  drop, defer, cleanup, ownership finalization, or conversion after the call.
- FFI, varargs, assembly functions, and ABI shapes that cannot satisfy LLVM
  `musttail` are rejected as `become` targets unless a future backend proves a
  target-specific legal lowering.

This is intentionally stricter than "the optimizer might perform tail-call
elimination." A successful `become` means the edge is stack-constant by
construction.

Expected LLVM lowering:

```llvm
define tailcc %State @Eval(%State %arg_state) {
entry:
  %next = call fastcc %State @BuildNext(%State %arg_state)
  %result = musttail call tailcc %State @Dispatch(%State %next)
  ret %State %result
}
```

For `void` callees:

```llvm
define tailcc void @Step(%State %arg_state) {
entry:
  %next = call fastcc %State @BuildNext(%State %arg_state)
  musttail call tailcc void @Step(%State %next)
  ret void
}
```

Lowering contract:

- `tail` Stark functions lower to LLVM `tailcc` rather than the usual internal
  `fastcc`.
- every `become` lowers to `musttail call tailcc`, followed immediately by `ret`
  returning the call result or `ret void`.
- caller and callee LLVM calling conventions must match.
- the emitter must reject or avoid ABI lowering that inserts incompatible
  `sret`, `byval`, varargs, or other ABI-impacting differences that break
  `musttail` verification.
- source pointer-like parameters such as `borrow T` may participate in
  `musttail` when the lowered LLVM ABI type matches; hidden by-value aggregate
  indirect ABI parameters remain illegal for guaranteed tail calls.
- dynamic trait dispatch is legal for `become` only when the trait slot carries
  the `tail` contract and the caller/callee erased ABI shapes are compatible.
- SSA optimizers must treat tail-call terminator targets, arguments, and
  indirect argument addresses as normal uses so cleanup, alias, scalar
  replacement, ownership, inlining, and address-taken pruning cannot delete
  values needed by the final `musttail` edge.
- package images must preserve the tail-callable function contract on function
  declarations, function pointer types, and imported callable summaries.
- self-recursive and mutually recursive `become` cycles are allowed; `finite`
  remains a separate termination proof and is not required for stack-constant
  tail recursion.

Diagnostics should name the specific reason a `become` cannot be guaranteed:
not in tail position, caller is not `tail`, callee is not tail-callable, pending
drop/cleanup after the call, incompatible return ABI, FFI/varargs target, or
unsupported indirect-call contract.

This is a meaningful Stark-only performance and correctness contract. C and Zig
have per-call-site escape hatches, Rust stable has no guaranteed tail-call
surface, and none of them makes guaranteed stack-constant tail control flow a
first-class checked callable contract.

### Granular Capture, Initialization, And Destination Attributes

Stark's borrow and destination modes describe how pointer-like parameters may be
used:

```stark
unsafe fn i32[min max] Read(borrow Box box)
{
    return box.Value;
}

unsafe fn retborrow Box Echo(retborrow Box value)
{
    return value;
}

unsafe fn storeborrow Box Hold(storeborrow Box value)
{
    return value;
}
```

The compiler already models three capture classes for parameters:

- `None`: the pointer does not escape the call.
- `Return`: the pointer may escape only through the return value.
- `Escape`: the pointer may be retained or otherwise escape.

Current LLVM emission status:

- non-escaping parameters emit granular LLVM `captures(none)`.
- readonly return-only borrows emit `captures(ret: address, read_provenance)`.
- mutable or provenance-writing return-only borrows emit
  `captures(ret: address, provenance)`.
- readonly escaping borrows emit `captures(address, read_provenance)`.
- mutable or provenance-writing escaping borrows emit
  `captures(address, provenance)`.
- indirect function-pointer call attributes rebuild compatible capture facts
  from the function pointer type and parameter memory summaries.
- write-only destination parameters emit LLVM `writeonly`.
- full-object `out` destinations and eligible full-object `init` destinations
  with a known concrete extent emit LLVM `writable` and
  `initializes((0, N))`, and the parameter summary carries the byte range that
  justifies the attribute.

That means the granular capture model is implemented for ordinary ABI surfaces.
For a truly non-escaping ordinary `borrow`, the strongest fact is
`captures(none)` rather than `captures(address, read_provenance)`. For source
constructs that store readonly borrow provenance for later reads, such as
`storeborrow`, `retborrow`, or closure `capture(read x)` environments when the
environment actually retains the pointer, the fact must be the read-provenance
form and must not be collapsed to `captures(none)`.

The destination-initialization model is intentionally full-range only for now.
An `out T` parameter, or an `init T` parameter whose destination is a known
object rather than an open-ended slice span, can say that the first access to
`[0, sizeof(T))` is a write. Dynamic spans such as `init T[]` need an extent
that LLVM can name before Stark can emit a precise range.

True pointee-dead-after-return destinations are explicit source contracts:

```stark
fn bool Destroy(out u32[0 max] value) where dead_on_return(value)
{
    value = 0;
    return true;
}

unsafe fn bool Apply(
    fnptr<fn bool(out u32[0 max]) where dead_on_return(arg0)> op,
    out u32[0 max] value)
    where dead_on_return(value)
{
    return op(value);
}
```

The contract marks a whole memory-backed parameter as unavailable to the caller
after the call returns. It lowers to LLVM `dead_on_return` on the parameter or
indirect-call operand, composes with `writeonly`, `writable`, and
`initializes((0, N))` when the destination is a full known object, and is part of
callable type compatibility so a destructive callback cannot be silently stored
in a plain function pointer slot. Ordinary `out` and `init` parameters still do
not emit `dead_on_return`: callers read initialized outputs after those calls.

Known gaps:

- `init T[]` and other dynamically sized destinations need a named extent model
  before Stark can emit non-constant `initializes(...)` ranges.

The implementation should avoid weakening facts: `captures(none)` is stronger
than `captures(address, read_provenance)` for a call-scoped non-escaping
borrow. Read-provenance capture is correct only when the pointer's readonly
provenance can survive beyond the call boundary or be returned.

### Whole-Allocation Separate Storage Assumptions

Stark already has rich non-overlap facts:

```stark
unsafe fn void Copy(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
{
    *left = *right;
}

unsafe fn i32[min max] Trusted(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
    where overlap(left, right)
{
    assume disjoint(left, right)
    {
        *left = 7;
        return *right;
    }

    return 0;
}
```

Current LLVM emission status:

- default and explicit whole-parameter non-overlap can emit LLVM parameter
  `noalias` where the ABI rules allow it.
- loads, stores, memory intrinsics, and memory-touching calls through proven
  disjoint roots carry scoped `!alias.scope` and `!noalias` metadata.
- `if disjoint(...)` and `assume disjoint(...)` create dominance-scoped
  disjoint facts and attach scoped noalias metadata inside the proven region.
- same-parameter facts emit ordinary equality `llvm.assume` checks for pointer
  and length equality.
- fresh positive-capacity dynamic allocation roots emit
  `llvm.assume(i1 true)` with `"separate_storage"(ptr %a, ptr %b)` bundles once
  both backing pointers are available and the earlier root dominates the later
  use point.

LLVM's `separate_storage` assume bundle is stronger than a byte-range
non-overlap fact: it says no pointer based on one operand can alias any pointer
based on the other. It is appropriate only for whole-allocation disjoint facts,
not for two non-overlapping subranges of the same allocation such as `ptr[0, 4]`
and `ptr[4, 4]`. Subrange disjointness should continue to use scoped noalias
metadata and range-aware memory-root facts.

The desired lowering is:

```llvm
call void @llvm.assume(i1 true) ["separate_storage"(ptr %left, ptr %right)]
```

Use this only when Stark's memory-region model proves distinct allocation roots
for the two operands and the fact dominates the memory operations that rely on
it. Existing `noalias` parameter attributes and scoped noalias metadata should
remain; the assume bundle is an additional whole-allocation fact, not a
replacement for operation-scoped metadata.

### Arena Storage And Arena Allocation

`arena` is a language storage class, not a standard-library allocator value. It
belongs beside `stack`, `heap`, and `register` in the source language. The
standard library may expose helper APIs later, but the core feature does not
require a `System.Memory.Arena` type and user code should not need to pass an
arena handle around to get arena-backed storage.

The primary user-facing surface is local arena storage:

```stark
fn void Parse()
{
    arena mut dynamic Token[0 max] tokens = new(1024);
    arena Node root = new Node()
    {
        Kind = NodeKind.Root
    };
}
```

The compiler creates a hidden lexical arena frame for scopes that contain arena
allocations. `arena` locals allocate their owned storage from that frame. At the
end of the lexical arena lifetime, Stark drops live values that require
destruction and then releases or resets the arena storage in bulk. Individual
arena-backed values are not manually freed by safe code.

When a non-arena owner needs arena-backed dynamic storage, the allocation
expression may use the `arena` keyword as a storage selector:

```stark
fn void Tokenize()
{
    stack mut dynamic Token[0 max] tokens = new(arena, 1024);
}
```

This is still a language keyword form. It does not mean `new(System.Memory.Arena,
...)`, and it does not require a user-visible arena object. The result carries an
arena lifetime fact, so it is subject to the same escape restrictions as an
`arena` local.

The user-facing contract is:

- `arena` locals are valid executable local storage.
- arena-backed values are owned values with ordinary move, borrow, drop, and
  mutability rules while they are alive.
- arena-backed storage must not escape the arena lifetime through returns,
  heap/static stores, escaping closures, retained borrows, global state, or
  longer-lived aggregate fields.
- safe borrows from arena-backed values may be passed to callees only when the
  callee's parameter contract does not retain them beyond the call.
- unsafe raw pointers may name arena storage, but safe code must not convert
  those raw pointers back into longer-lived safe views after the arena lifetime.
- `arena` allocation is not `law`: it mutates hidden allocation state and may
  fail or trap according to the allocation policy. `finite` remains allowed if
  ordinary termination rules are satisfied.
- dynamic arena-backed growth uses allocate-copy semantics and leaves old arena
  backing storage to be reclaimed by the arena frame. It must not lower to
  per-object `free`.

Escape diagnostics should name the lifetime boundary clearly:

```stark
unsafe fn rawptr<Node> Bad()
{
    arena Node node = new Node();
    return &node; // error: arena-backed storage cannot escape its arena scope
}

fn void AlsoBad()
{
    heap mut Holder holder = new Holder();
    arena Node node = new Node();
    holder.Node = &node; // error: storing arena storage into heap object escapes
}
```

Lowering contract:

- The front end records an arena lifetime/root fact on every arena-backed local,
  dynamic backing buffer, slice/view derived from arena storage, and raw pointer
  derived from arena storage.
- MIR/SSA represents hidden arena frame creation, arena allocation, live-value
  drops, and arena frame cleanup explicitly enough that validation can prove
  cleanup dominates every normal exit and every supported early-exit path.
- Drop lowering drops live elements and owned fields, but arena backing storage
  is reclaimed only by the arena frame cleanup.
- SSA memory facts treat each successful arena allocation result as fresh
  storage disjoint from all other live allocation results that the same arena
  frame has already returned.
- Package-image summaries must preserve arena lifetime and escape-relevant facts
  on callable surfaces that mention arena-backed values or borrows.

Expected LLVM shape:

```llvm
%arena = alloca %__stark_arena_frame, align 8
call void @__stark_arena_enter(ptr nonnull %arena)

%node = call noalias nonnull noundef align 8 dereferenceable(32) ptr
    @__stark_arena_alloc(ptr nonnull %arena,
                         i64 noundef 32,
                         i64 noundef 8)

; live arena-backed values are dropped here when needed
call void @__stark_arena_leave(ptr nonnull %arena)
```

The allocation helper is backend-owned, not a user-visible standard-library
allocator. Its declaration should carry allocator facts analogous to heap
allocation:

```llvm
define internal dso_local noalias nonnull noundef ptr @__stark_arena_alloc(
    ptr captures(none) nonnull %arena_frame,
    i64 noundef %size,
    i64 noundef allocalign %alignment)
    unnamed_addr
    allocsize(1)
    allockind("alloc,uninitialized,aligned")
    "alloc-family"="__stark_arena_alloc"
    nounwind
```

Call sites should additionally attach the concrete alignment and
`dereferenceable(N)` facts when the requested layout is known. Arena reset or
leave helpers should not pretend to be ordinary per-object frees unless the
backend has a precise LLVM allocation-family model for that operation. The first
implementation should prefer bulk arena cleanup as a hidden runtime/compiler
operation and keep individual arena-backed values out of allocator-family free
lowering.

Benchmark notes:

- Arena benchmarks may compare against idiomatic C/Rust/Zig arena or bump
  allocators. If those languages pass an explicit arena object while Stark uses a
  storage keyword, that is a source-language difference, not automatically an
  unfair benchmark.
- Baselines should still use normal arena allocator APIs rather than artificially
  allocating every temporary with `malloc`/`free`.
- Stark wins are fair when they come from compile-time escape checks, hidden
  lexical lifetime management, or allocator facts emitted for arena allocation
  results. They are not fair if the C/Rust/Zig baseline could naturally use the
  same bump allocation structure but the benchmark prevents it.
