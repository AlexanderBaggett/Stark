# `v1.0` Lowering Contract

This document freezes the current lowering and ABI contract for the supported `v1.0` Stark subset.

It is intentionally limited to the constructs that are already part of the `v1.0` release baseline in [V1ReleaseSubset.md](./V1ReleaseSubset.md). If a feature is listed as unsupported or out of `v1.0`, this document does not promise a stable lowering shape for it.

The primary release baseline is:

- host and target: `x86_64-unknown-linux-gnu`
- internal Stark code generation through LLVM IR and the native toolchain
- manifest-backed package consumption for the included subset

## Scope

This contract freezes:

- the current ABI split between ordinary internal Stark functions and `ffi` functions
- the concrete runtime and ABI shapes used for the included scalar, view, array, pointer, and aggregate families
- the current threshold-based direct-vs-indirect aggregate ABI behavior
- the LLVM-level invariants that release tests should treat as stable for the included subset

This contract does not freeze:

- unsupported MIR lowering paths listed in [UnsupportedFeatures.md](./UnsupportedFeatures.md)
- future post-`v1.0` representation work such as enum `repr` controls or richer FFI guarantees
- target-specific codegen details outside the primary release baseline

## Internal Stark Call ABI

For non-`ffi` Stark functions in the included subset:

- the current compiler uses `fastcc`
- scalar parameters and scalar returns stay direct
- direct internal calls remain direct `fastcc` calls in emitted LLVM IR

This is part of Stark's performance-first release contract. A regression from direct scalar passing to unnecessary indirection is a `v1.0` regression.

## `ffi` Boundary ABI

For `ffi` declarations in the included subset:

- the default internal `fastcc` is not used
- the foreign-facing declaration keeps an ordinary foreign ABI shape
- the currently supported text boundary used by shipped examples and tests lowers `ascii` arguments to raw pointer ABI form at the call site

For `v1.0`, downstream user code should treat raw pointers as the most explicit and stable foreign-boundary spelling for custom low-level interop surfaces.

## Type Family Contract

### Scalars

Included scalar families:

- `bool`
- integer widths such as `i32`, `i64`
- floating-point widths such as `f32`, `f64`
- raw pointers

Contract:

- internal Stark ABI passes them directly
- LLVM lowering uses the matching first-class scalar or pointer LLVM type

### Text Views

Included immutable text view families:

- `ascii`
- `unicode`

Contract:

- internal Stark ABI lowers both to a two-field value shape `{ ptr, i64 }`
- the pointer is the data pointer
- the `i64` field is the logical length in code units for the text kind
- compiler-emitted literals return this concrete value shape directly in internal Stark code

### Slices

Included slice family:

- `T[]`

Contract:

- internal Stark ABI lowers slices as `{ ptr, i64 }`
- the pointer addresses the first element
- the `i64` field is the element count

### Fixed Arrays

Included fixed-array family:

- `T[N]`

Contract:

- fixed arrays are first-class owning values, not hidden slice sugar
- the direct ABI shape is the concrete LLVM array type
- fixed arrays participate in the same direct-vs-indirect size threshold described below

### Borrowed Values

Included borrow families:

- `borrow T`
- `borrow mut T`
- `retborrow T`
- other supported safe borrow-qualified forms in the `v1.0` subset

Contract:

- borrow parameters lower indirectly as pointers
- readonly or writeonly-style LLVM parameter facts are derived from the source qualifier and semantic analysis
- safe borrows stay eligible for non-null, capture, dereferenceability, and alignment facts when the compiler can prove them

### Named Aggregates

Included named aggregate families:

- `struct`
- `record`

Contract:

- internal Stark ABI uses the concrete LLVM aggregate type for direct-by-value cases
- field order follows the declared source order as lowered by the current concrete layout rules
- small value aggregates stay on the direct ABI path when they fit within the current direct threshold
- larger aggregates switch to indirect ABI lowering

## Direct vs Indirect Aggregate ABI Threshold

The current `v1.0` aggregate ABI threshold is:

- direct by value at `16` bytes or smaller
- indirect when the concrete aggregate size is greater than `16` bytes

Current lowering consequences:

- large by-value parameters lower as pointer parameters with `byval(...)`
- large returns lower through an explicit `sret(...)` pointer parameter
- small aggregates must not silently regress to `byval` or `sret`

This threshold is intentionally frozen for the `v1.0` subset because changing it would change performance-sensitive call behavior and object-level ABI expectations for shipped code.

## Frozen LLVM/Object Invariants

For the included `v1.0` subset, the following are release invariants:

- ordinary internal Stark functions keep `fastcc`
- `ffi` functions do not silently pick up the internal Stark calling convention
- `ascii`, `unicode`, and slice values keep their current two-field ABI shapes
- borrowed aggregate parameters remain indirect and keep the derived non-null and readonly/writeonly-style facts when proven
- small aggregates remain direct by value
- large aggregates remain indirect through `byval` and `sret`

The authoritative release lock for these invariants is:

1. this document
2. the dedicated lowering-contract regression tests
3. existing end-to-end executable, object, and package tests that consume the same lowered shapes

## What Counts As A `v1.0` Regression

For the supported subset, examples of regressions include:

- an internal `fn` losing `fastcc` without an explicit language change
- `ascii` or slice values ceasing to lower as `{ ptr, i64 }`
- a `16`-byte aggregate switching from direct passing to `byval`
- a `24`-byte aggregate switching from `sret`/`byval` to direct-by-value return or parameter passing
- a proven readonly borrow losing its derived readonly-style parameter facts

Changes outside those frozen cases should be treated according to [UnsupportedFeatures.md](./UnsupportedFeatures.md), [StandardLibraryBaseline.md](./StandardLibraryBaseline.md), and release notes.
