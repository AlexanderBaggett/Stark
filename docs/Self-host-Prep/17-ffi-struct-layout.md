# Phase 17 - FFI Struct Layout And Alignment

Status: **implementation landed; book-facing docs pending.**

Implementation evidence:

- Parser and type checking recognize `[StructLayout(C)]`, `[StructLayout(Explicit)]`,
  `[Pack(N)]`, `[Align(N)]`, and `[FieldOffset(N)]`.
- The type model, package images, ABI lowering, and LLVM emission preserve layout
  metadata and resolved field offsets.
- Packed-field misalignment is tracked through borrow validation and LLVM
  unaligned load/store emission.
- Runtime C interop coverage verifies x86_64 SysV integer/pointer C aggregate
  parameters against a native clang/GNU fixture. Broader cross-target aggregate
  ABI fixture expansion remains part of the native target/toolchain matrix.

Stark will use C#-style attributes for FFI-facing struct layout control. The
canonical C ABI layout spelling is:

```stark
[StructLayout(C)]
struct Timespec
{
    System.C.c_long Seconds;
    System.C.c_long Nanoseconds;
}
```

This is Stark's `repr(C)` equivalent: declaration order is preserved, field
alignment follows the active target's C ABI, padding is inserted according to
that ABI, and the result is suitable for C interop when the field types are also
FFI-safe.

## 1. Goals

- Provide a C-compatible sequential layout for FFI aggregates.
- Support packed layout for wire formats and packed C structs.
- Support explicit aggregate alignment for ABI and hardware-facing structs.
- Keep layout facts visible at the declaration site.
- Avoid new layout keywords or Rust-style `repr(...)` syntax.
- Fit Stark's existing attribute grammar and C#-leaning mental model.

## 2. Core Syntax

### 2.1 C ABI Layout

```stark
[StructLayout(C)]
struct StatLike
{
    System.C.c_ulong Device;
    System.C.c_ulong Inode;
    System.C.c_long Size;
}
```

`[StructLayout(C)]` means:

- fields are laid out in declaration order
- each field uses the active target's C ABI alignment for its resolved type
- padding is inserted before fields as needed
- tail padding rounds the struct size up to the struct alignment
- the struct alignment is the maximum effective field alignment unless raised
  by `Align(N)`

This attribute is the FFI declaration that the type's binary layout is meant to
match a C aggregate on the selected target.

### 2.2 Packed Layout

```stark
[StructLayout(C), Pack(1)]
struct PackedHeader
{
    u8[0 max] Version;
    u16[0 max] Length;
    u32[0 max] Flags;
}
```

`Pack(N)` caps each field's effective alignment at `N`.

For each field:

```text
effectiveFieldAlignment = min(naturalCFieldAlignment, N)
```

`Pack(1)` is the fully packed form. It removes automatic inter-field padding
caused by field alignment. Tail padding still follows the aggregate alignment,
which is normally `1` for `Pack(1)` unless `Align(N)` raises it.

### 2.3 Aggregate Alignment Override

```stark
[StructLayout(C), Align(16)]
struct AlignedBlock
{
    u8[0 max][16] Bytes;
}
```

`Align(N)` raises the aggregate alignment to at least `N`.

It does not reduce any natural alignment, does not cap field alignment, and does
not change field offsets except through the final tail-padding rule. Use
`Pack(N)` when field alignment should be capped.

### 2.4 Pack And Align Together

```stark
[StructLayout(C), Pack(1), Align(4)]
struct WireHeader
{
    u8[0 max] Tag;
    u32[0 max] Length;
}
```

When both are present:

1. Field offsets are computed using `Pack(N)`.
2. Struct alignment is the maximum effective field alignment.
3. `Align(N)` raises the struct alignment if needed.
4. Struct size is rounded up to the final struct alignment.

This makes packed fields explicit while still allowing a whole aggregate to meet
an external alignment requirement.

## 3. Attribute Surface

Initial accepted attributes:

| Attribute | Applies to | Meaning |
|---|---|---|
| `[StructLayout(C)]` | `struct` | C-compatible sequential layout for the active target. |
| `[StructLayout(Explicit)]` | `struct` | Explicit byte offsets through field-level `[FieldOffset(N)]`. |
| `[Pack(N)]` | `struct` with `[StructLayout(C)]` | Cap field alignment at `N`. |
| `[Align(N)]` | `struct` with `[StructLayout(C)]` or `[StructLayout(Explicit)]` | Raise aggregate alignment to at least `N`. |
| `[FieldOffset(N)]` | field in `[StructLayout(Explicit)]` | Place field at byte offset `N`. |

The initial layout-control surface is attribute-based. Stark should not add:

- `repr(C)`
- `packed struct`
- `align(N)` as a keyword
- hidden C layout for ordinary structs

The attribute spelling keeps layout metadata with other declaration metadata
such as `[Ok]`, `[Err]`, and `[Platform]`, while preserving `ffi(...)` as the
function-boundary modifier.

## 4. Valid Values

`Pack(N)` and `Align(N)` require a positive power-of-two integer literal.

Recommended minimum accepted values:

```text
1, 2, 4, 8, 16, 32, 64
```

The compiler may allow larger powers of two if the target and backend support
them. Unsupported values are compile-time errors.

`Pack(N)` must not be larger than the target's maximum meaningful C aggregate
alignment unless the target descriptor explicitly allows it. A too-large `Pack`
is normally useless and should be diagnosed.

`Align(N)` may be larger than the natural C aggregate alignment when the target
supports that explicit alignment.

## 5. FFI Safety Rules

`[StructLayout(C)]` does not make every field type FFI-safe. The field types
must still be valid for a C ABI aggregate.

Allowed initial field categories:

- Stark sized integer and floating point primitives
- `System.C` target-mapped aliases from [16-ffi-c-types.md](16-ffi-c-types.md)
- raw pointers, including `rawptr<System.C.c_void>` and
  `rawmutptr<System.C.c_void>`
- fixed arrays of FFI-safe element types
- nested `[StructLayout(C)]` or `[StructLayout(Explicit)]` structs

Rejected across the FFI layout boundary:

- Stark enums
- dynamic storage
- safe borrows
- closures
- trait objects
- owning heap values whose layout or drop behavior is Stark-specific

Those types can still exist in ordinary Stark structs; they just cannot be
claimed as C-layout fields.

## 6. Packed Field Access

Packed fields may be misaligned. Stark should keep that fact visible to the
borrower and lowering layers.

Rules:

- Reading or writing a packed field is allowed.
- Lowering must use unaligned loads/stores when the computed field offset is not
  aligned for the field type.
- Taking a safe borrow to a misaligned packed field is a compile-time error.
- Taking a raw pointer to a misaligned packed field is unsafe and must preserve
  the misalignment fact.
- Passing a packed field by address to `ffi` requires the external declaration
  to accept the actual pointer alignment.

This avoids turning packed layout into hidden undefined behavior.

## 7. Explicit Layout Relationship

`[StructLayout(Explicit)]` is the byte-offset companion to C sequential layout:

```stark
[StructLayout(Explicit)]
struct WordParts
{
    [FieldOffset(0)] u32[0 max] Whole;
    [FieldOffset(0)] u16[0 max] Low;
    [FieldOffset(2)] u16[0 max] High;
}
```

Explicit layout does not use `Pack(N)` because every field offset is written.
It may use `Align(N)` to raise the aggregate alignment.

The detailed union and bitfield surface remains in
[stark-explicit-layout-spec.md](stark-explicit-layout-spec.md). This document
locks the alignment spelling and the C-compatible sequential layout spelling.

## 8. Target Model

The target descriptor must provide the C aggregate layout facts needed by
`[StructLayout(C)]`:

- size and alignment for every Stark primitive that may cross FFI
- resolved size and alignment for every `System.C` alias
- pointer size and pointer alignment
- maximum supported explicit aggregate alignment
- backend support for unaligned loads and stores, or a lowering strategy when
  the backend requires synthesized byte operations

The compiler must never use the host machine's C layout when compiling for a
different target.

## 9. Package Images

Package images must preserve layout attributes as source-facing facts and also
carry enough resolved target layout information for imported types.

If package images are target-specific, they may serialize:

- source layout attributes
- resolved field offsets
- resolved size
- resolved alignment
- packed-field misalignment facts

If target-independent package images are introduced later, they must serialize
the symbolic layout attributes and resolve them when imported for a concrete
target.

## 10. Diagnostics

Recommended diagnostics:

| Condition |
|---|
| `Pack(N)` or `Align(N)` is not a positive power of two. |
| `Pack(N)` used without `[StructLayout(C)]`. |
| `Pack(N)` used with `[StructLayout(Explicit)]`. |
| Multiple `StructLayout`, `Pack`, or `Align` attributes on one type. |
| `[StructLayout(C)]` contains a field type that is not FFI-safe. |
| Target descriptor does not define size or alignment for a required field type. |
| Safe borrow attempted from a misaligned packed field. |
| `Align(N)` requested but unsupported by the active target/backend. |
| C-layout type used across package boundary without serialized layout facts. |

## 11. Implementation Work Items

- [x] Implement layout-control attributes and symbol metadata for
      `StructLayout`, `Pack`, `Align`, and `FieldOffset`.
- [x] Implement target-aware C layout computation, including
      `[StructLayout(C)]` field offsets/sizes/alignment, `Pack(N)` alignment
      caps, `Align(N)` aggregate alignment raising, and active target layout
      facts.
- [x] Implement packed-field safety end to end: misalignment tracking through
      type checking, borrow validation, MIR, ABI lowering, LLVM emission, and
      compile-time rejection of safe borrows to misaligned packed fields.
- [x] Preserve and expose layout facts through package images, source bridge
      output, and `System.Compiler` compile-time structural facts, including
      package-backed typed interfaces.
- [x] Add diagnostics and coverage for invalid attributes, unsupported target
      layout facts, non-FFI-safe fields, parser/type-check/package/ABI/LLVM
      behavior, and platform-specific C runtime layout fixtures.

## 12. Book And Reference Work

- [x] Update Stark language skill references for layout-control structural facts.
- [ ] Update user-facing FFI docs for layout-control attributes and packed-field
      safety.
