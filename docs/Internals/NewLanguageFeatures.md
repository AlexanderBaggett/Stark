# New Language Features

This document tracks proposed or recently added language features that need both
user-facing language documentation and compiler implementation notes. It is a
staging document: once a feature is complete and stable, the user-facing parts
should move into `docs/Userfacing/LanguageReference.md`, and the implementation
parts should move into the relevant internals documents.

## Documentation

Each feature entry should describe the source-language contract first, then the
compiler contract that makes the feature worth having. For performance features,
the entry should also state what C, Rust, Zig, or other comparison languages can
and cannot express naturally, so benchmarks do not confuse a Stark language
advantage with an unfair baseline.

Feature entries should cover:

- User-facing syntax and semantics.
- Static restrictions and diagnostics.
- Lowering contract before MIR, MIR to SSA, and SSA optimization expectations.
- LLVM IR emission facts, attributes, metadata, and assumptions.
- Benchmark implications and fairness notes.

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

Stark should expose guaranteed tail calls as a semantic control-flow contract,
not as an optimizer hint. The source program should be rejected if the compiler
cannot lower the edge to a guaranteed tail call.

The proposed surface has two parts:

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

That means the granular capture model is implemented for ordinary ABI
surfaces. For a truly non-escaping ordinary `borrow`, the strongest fact is
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

- `arena` locals are valid executable local storage once this feature is
  implemented.
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

## Reminders

- Add future feature entries here before spreading them across the stable docs.
- For each feature, include both user-facing semantics and compiler lowering or
  LLVM IR emission requirements.
- For each performance-facing feature, include a benchmark fairness note.
- For each LLVM emission feature, add C# host compiler tests and matching Stark
  self-hosted compiler tests. The C# tests are required to pass before the
  feature is considered implemented; the Stark tests may be checked in as
  in-progress or expected-failing coverage while the self-hosted compiler
  catches up.
- Keep ordinary `llvm.assume(i1 condition)` for range refinements when the
  target LLVM does not support a range assume bundle, and document that fallback
  explicitly.
- LLVM 22.1.6 does not expose a bounded textual range assume-bundle spelling;
  do not emit the accepted but boundless `"range"(iN %value)` form.
- When the targeted LLVM grows a usable ConstantRange assume-bundle form, add a
  range bundle kind, lower branch-refined and opaque-source integer facts to it,
  and add C# plus Stark compiler tests for the positive and no-duplicate paths.
- Keep function memory effects on the modern structured `memory(...)` form; do
  not regress to legacy whole-function `readonly`, `readnone`, or `argmemonly`
  replacements.
- Do not emit `separate_storage` for merely disjoint subregions of the same
  allocation; keep those facts on scoped `!alias.scope` / `!noalias` metadata.
- Do not model arena reset/leave as ordinary per-object allocator-family `free`
  unless a precise LLVM allocation-family contract for bulk arena cleanup is
  designed and tested.
- Add or update tests when a feature entry graduates from proposal to
  implementation contract.

## Task List

- [x] Audit integer range facts for every LLVM value surface that can legally
      carry them: parameters, returns, calls, loads, aggregate field loads,
      globals, and re-materialized ABI values.
- [x] Confirm the exact LLVM assume-bundle support and syntax for integer range
      facts in the LLVM version Stark targets.
- [x] Add C# LLVM emission tests for the LLVM-supported integer range surfaces,
      including globals and narrow actual call arguments passed to broad formal
      parameters.
- [x] Add equivalent Stark self-hosted compiler tests for the LLVM-supported
      integer range surfaces; these may remain in-progress or expected-failing
      until the self-hosted emitter implements the feature.
- [x] Add a `norecurse` effect fact to the function-effect model if closed-world
      call-graph analysis proves a function is outside every dynamic recursion
      cycle.
- [x] Emit LLVM `norecurse` for eligible non-FFI functions and preserve the fact
      through package-image summaries.
- [x] Add regression tests that reject legacy whole-function
      `readonly`/`readnone`/`argmemonly` replacements.
- [x] Add `tail` as a function/callable modifier that composes with `fn`,
      `law`, `finite`, and `finite law`.
- [x] Add source syntax and type-model support for tail-callable function
      pointer and closure callable types.
- [x] Complete and test tail-callable trait-method and dynamic-dispatch
      callable surfaces, including slot compatibility and legal `become`
      through dispatch targets that carry the tail contract.
- [x] Add the `become` terminating statement and reject it when the call is not
      in true tail position.
- [x] Type-check `become` so the caller is `tail`, the callee is tail-callable,
      and the ordinary effect/call-capability rules still hold.
- [x] Finish the lifetime and cleanup audit for `become`: reject any edge where
      pending drops, defers, ownership finalization, conversions, or
      caller-local storage lifetimes would need work after the tail transfer or
      would be invalidated before the tail-call arguments are consumed.
- [x] Lower `tail` Stark functions to LLVM `tailcc` and lower every `become` to
      `musttail call tailcc` followed immediately by the matching `ret`.
- [x] Add backend rejection for ABI shapes that cannot satisfy LLVM `musttail`,
      including unsupported `sret`, incompatible `byval`, FFI, varargs, and
      assembly targets.
- [x] Replace backend tail-call ABI exceptions with front-end diagnostics that
      name the illegal ABI shape before LLVM emission.
- [x] Preserve the tail-callable contract through package-image function
      summaries, function pointer types, imported templates, and dynamic/trait
      callable surfaces that can legally support it.
- [x] Add initial passing C# parser, type-checking, and LLVM emission tests for
      `tail` and `become`.
- [x] Add passing C# and Stark self-hosted compiler tests for tail-call
      ABI-blocker diagnostics, including FFI/varargs, indirect return, and
      indirect parameter shapes.
- [x] Add passing C# MIR/SSA, package-image, mutual-recursion, indirect
      tail-callable function-pointer, and trait/dynamic dispatch tests for
      `tail` and `become`.
- [x] Add initial Stark self-hosted compiler tests for `tail` and `become`;
      these may remain in-progress or expected-failing until the self-hosted
      checker and emitter implement the feature.
- [x] Update the Stark self-hosted parser, AST/syntax model, and parser tests to
      parse contextual `tail` function modifiers, contextual `tail` callable
      signatures, and `become` statements without reserving `tail` as an
      identifier.
- [x] Replace non-escaping parameter `nocapture` emission with
      `captures(none)` once the targeted LLVM version accepts the new spelling.
- [x] Audit `capture(read x)` closure environments and retained readonly borrow
      paths so escaping read provenance emits
      `captures(address, read_provenance)`.
- [x] Add passing C# regression tests that ordinary call-scoped `borrow`
      parameters keep the strongest no-capture fact, while retained/returned
      readonly borrows use the read-provenance capture forms.
- [x] Add matching Stark self-hosted compiler tests for `captures(none)`,
      `captures(ret: address, read_provenance)`, and
      `captures(address, read_provenance)`; these may remain in-progress or
      expected-failing until the self-hosted emitter implements the feature.
- [x] Extend parameter and function memory summaries with initialization range
      facts for full-object `out`/eligible `init` destinations and a gated
      pointee-dead-after-return summary bit.
- [x] Confirm exact LLVM syntax and support for `initializes(...)`, `writable`,
      and `dead_on_return` in the LLVM version Stark targets.
- [x] Emit `initializes((0, N))` and `writable` for eligible full-object
      destination parameters.
- [x] Define and implement a Stark source contract for true
      pointee-dead-after-return destinations before emitting LLVM
      `dead_on_return`; ordinary `out`/`init` outputs must not use it because
      callers read the initialized value.
- [x] Add passing C# parser/semantic, ownership, callable compatibility, package
      image, and LLVM emission tests for `where dead_on_return(...)`.
- [x] Add matching Stark self-hosted compiler tests for `dead_on_return`
      lexing/parsing, ownership diagnostics, and LLVM emission; these may remain
      in-progress or expected-failing until the self-hosted checker and emitter
      implement the feature.
- [x] Add passing C# LLVM emission tests for `writeonly` plus
      `initializes(...)` on known full-range init destinations, including
      negative tests when the callee may read old contents first.
- [x] Add matching Stark self-hosted compiler tests for init/out destination
      attributes.
- [x] Add a `separate_storage` assume-bundle kind to
      `LlvmAssumeOperandBundleKind` and render it as
      `"separate_storage"(ptr %left, ptr %right)`.
- [x] Emit `llvm.assume(i1 true)` with `separate_storage` bundles for
      whole-allocation disjoint facts that dominate the relevant memory uses.
- [x] Add passing C# LLVM emission tests for positive `separate_storage`
      bundles on distinct allocation roots, plus negative tests for
      same-allocation subranges and `where overlap(...)` pairs.
- [x] Add matching Stark self-hosted compiler tests for `separate_storage`
      assume bundles; these may remain in-progress or expected-failing until
      the self-hosted emitter implements the feature.
- [x] Activate `arena` as an executable local storage class through semantic
      validation and SSA validation.
- [x] Add host grammar and type-checking support for `new(arena, ...)` as a
      dynamic-storage allocation selector, keeping `arena` as a language keyword
      rather than a `System.Memory.Arena` value.
- [x] Lower arena object locals to a hidden LLVM arena frame with
      `@__stark_arena_enter`, `@__stark_arena_alloc`, and
      `@__stark_arena_leave`, without heap allocator calls or stack `alloca` for
      the arena-owned object.
- [x] Lower positive constant-capacity arena dynamic storage allocation to a
      direct `@__stark_arena_alloc` call, skip per-owner runtime free on drop,
      and avoid emitting unused generic dynamic-storage helpers for arena-only
      allocation modules.
- [x] Add passing C# semantic and LLVM emission tests for executable arena
      locals, arena object allocation attributes, direct arena dynamic
      allocation, and no per-owner runtime free.
- [x] Add Stark self-hosted parser coverage for `new(arena, ...)` and typed
      `new Type(...)`, plus Stark host-protocol LLVM tests for arena object and
      arena dynamic allocation lowering.
- [x] Add ownership-level arena lifetime/root facts so arena-backed locals,
      dynamic backing storage, slices/views, borrows, raw pointers, aggregate
      construction, moves, projections, and branch/loop merges preserve the
      relevant arena provenance.
- [x] Implement safe-code escape diagnostics for arena-backed storage: returning
      it, storing it into heap/static/global or longer-lived aggregate state,
      retaining it in escaping closures, or passing it to callees that may retain
      the borrow must be rejected.
- [x] Reject every arena allocation form in `law` functions, including
      `arena` locals and `new(arena, ...)`, while preserving ordinary `finite`
      checking for arena-using functions.
- [ ] Add explicit MIR/SSA representation for arena frame creation, arena
      allocation operations, arena lifetime facts, live-value drops, and frame
      cleanup on all supported normal and early-exit paths.
- [ ] Implement arena-backed dynamic storage `Reserve`, `TryReserve`, and
      `TryReserveCapacity` with allocate-copy growth that leaves old backing
      storage for arena-frame cleanup; never lower arena growth to runtime
      `realloc` or per-object `free`.
- [ ] Ensure drop lowering drops arena-backed values and initialized dynamic
      elements with destructors before frame cleanup while still avoiding
      per-object frees for arena backing storage.
- [ ] Preserve arena lifetime and escape-relevant facts through package-image
      summaries for callable surfaces that mention arena-backed values or
      borrows.
- [x] Add passing C# borrow/escape and law-effect tests for arena lifetime
      returns, raw pointers, aggregate construction, heap/static storage,
      retaining callees, retained captures, non-retaining borrows, and
      `new(arena, ...)` in `law` functions.
- [x] Add matching Stark self-hosted checker tests for arena borrow/escape and
      law-effect diagnostics through the host compiler protocol.
- [ ] Add passing C# MIR/SSA, package-image, dynamic growth, destructor-drop,
      and remaining negative diagnostic tests for the remaining arena semantics.
- [ ] Add matching Stark self-hosted MIR/SSA and LLVM-emission tests for the
      remaining arena semantics; these may remain in-progress or expected-failing
      until the self-hosted checker and emitter implement the feature.
- [ ] Promote stable user-facing text into `docs/Userfacing/LanguageReference.md`.
- [ ] Append the completed Documentation section from this file into
      `docs/Internals/LanguageInternals.md`, preserving the implementation
      details and current/gap status. This should be a straightforward copy
      append once the entries are ready to graduate.
- [ ] Update downstream developer tooling docs after graduating feature syntax:
      refresh `skills/stark-language/SKILL.md` and the `vscode-stark` extension
      grammar, snippets, README, and related language data so Codex guidance and
      editor support match the implemented language surface.
