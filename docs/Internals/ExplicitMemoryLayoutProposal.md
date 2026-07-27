# Explicit Memory Layout: Implemented Surface And Bitfield Proposal

**Status:** Explicit byte offsets, C layout, packing, and alignment are
implemented and canonical in
[LanguageReference.md](../Userfacing/LanguageReference.md#87-layout-control-attributes).
The bitfield and byte/bit-order portions of this document remain a proposal and
are not accepted Stark syntax. Any implementation work is tracked in
[`docs/Self-host-Prep/TASKS.md`](../Self-host-Prep/TASKS.md).
**Scope:** Struct layout control for FFI, wire formats, and hardware register access
**Conventions:** Allman braces, 4-space indent, PascalCase for types/fields, camelCase for locals

> Note: the field declarations and proposed byte/bit-order attributes in this
> design record are illustrative and are not compilable Stark. Use the
> Language Reference for the implemented field-offset, packing, and alignment
> syntax. The semantics below preserve the rationale for the remaining bitfield
> design.

---

## 1. Motivation

Stark already implements explicit byte offsets, C layout, packing, and aggregate
alignment. This record captures the rationale behind that surface and the one
related layout capability that remains unimplemented:

1. **Implemented field offsets (unions)** — overlapping fields at explicit byte
   offsets, retained here as design context.
2. **Proposed bitfields** — named sub-byte regions packed into a backing integer, e.g. a 3-bit mode and a
   5-bit priority sharing one byte. Common in C system headers and hardware registers.

C has both but leaves ordering and packing implementation-defined, which is the primary source of
portability bugs. C# offers explicit byte-offset layout (`StructLayout`/`FieldOffset`) but no true
bitfields, leaving them to manual shift/mask. Stark can provide both with **explicit, stated
semantics**, integrating with its existing memory-overlap contracts and range types.

---

## 2. Field Offsets (Implemented Design Rationale)

### 2.1 Illustrative Syntax

```stark
[StructLayout(Explicit)]
struct RegisterFile
{
    [FieldOffset(0)] Whole : u64;
    [FieldOffset(0)] Low   : u16;
    [FieldOffset(2)] Mid   : u16;
    [FieldOffset(4)] High  : u16;
    [FieldOffset(6)] Top   : u16;
}
```

### 2.2 Semantics

- `[StructLayout(Explicit)]` switches the struct from default layout to explicit byte placement.
- Each field MUST carry a `[FieldOffset(n)]` giving its starting byte offset within the struct.
- Fields MAY overlap. Overlap is the defining feature of this layout kind.
- The struct's size is `max(offset + sizeof(field))` over all fields, rounded up to the struct's
  alignment.
- The struct's alignment is the maximum alignment of its fields unless overridden (see §4).

### 2.3 Overlap Contracts

Silent overlap is the C union footgun. Stark ties explicit layout into its existing memory-overlap
contract system so that aliasing is declared and checked:

```stark
[StructLayout(Explicit)]
struct RegisterFile
    overlaps(Whole with Low, Mid, High, Top)
{
    [FieldOffset(0)] Whole : u64;
    [FieldOffset(0)] Low   : u16;
    [FieldOffset(2)] Mid   : u16;
    [FieldOffset(4)] High  : u16;
    [FieldOffset(6)] Top   : u16;
}
```

- The compiler verifies that the byte ranges implied by the offsets match the declared `overlaps`
  clause. An overlap not listed in the contract is a compile error; a declared overlap that does not
  actually occur is a warning.
- This makes unions safe-by-declaration rather than unsafe-by-default. Reading a field of an
  explicit-layout struct is well-defined (it reinterprets the underlying bytes); the contract
  documents *intent*.

### 2.4 Byte Order

For FFI and wire formats, which sub-field maps to which bytes of a larger field depends on
endianness. Byte order MUST be statable:

```stark
[StructLayout(Explicit, ByteOrder = LittleEndian)]
struct RegisterFile { ... }
```

- `ByteOrder` accepts `LittleEndian`, `BigEndian`, or `Native` (default `Native` if omitted).
- When `Native`, the layout matches the target platform; portable wire formats should specify one
  explicitly.

### 2.5 References to overlapping fields

Taking a reference/borrow to an overlapping field is permitted but participates in the borrow
model: two overlapping fields alias the same memory, so simultaneous mutable borrows of overlapping
fields are rejected by the borrow checker exactly as any other aliasing violation would be.

---

## 3. Bitfields (Sub-Byte Fields)

### 3.1 Syntax — sequential packing

```stark
[BitFields(Backing = u32, BitOrder = LsbFirst)]
struct StatusWord
{
    Mode     : 3;    // bits 0..2
    Priority : 5;    // bits 3..7
    Flags    : 8;    // bits 8..15
    Reserved : 16;   // bits 16..31
}
```

### 3.2 Syntax — explicit bit positions

```stark
[BitFields(Backing = u32, BitOrder = LsbFirst)]
struct StatusWord
{
    [BitOffset(0)]  Mode     : 3;
    [BitOffset(3)]  Priority : 5;
    [BitOffset(16)] Reserved : 16;   // bits 8..15 intentionally unused
}
```

### 3.3 Semantics

- `[BitFields(Backing = T, ...)]` declares the struct as a packed set of sub-fields within a single
  backing integer of type `T`. `Backing` is MANDATORY (no inference — C's inference is the main
  portability hazard).
- The `: N` suffix on each field is its width in bits.
- In sequential mode (no `[BitOffset]`), fields are placed in declaration order starting from the
  end of the backing word determined by `BitOrder`.
- In positioned mode, each field carries `[BitOffset(n)]` and may leave gaps.
- The sum of widths (sequential) or the highest `offset + width` (positioned) MUST be
  `<= sizeof(Backing) * 8`; otherwise it is a compile error.
- A field MUST NOT straddle in a way inconsistent with the declared `BitOrder`; the rule is fully
  determined by `BitOrder` and `Backing`, never implementation-defined.

### 3.4 Bit Order

- `BitOrder` is MANDATORY and accepts `LsbFirst` or `MsbFirst`.
- `LsbFirst`: the first declared field occupies the least-significant bits of the backing word.
- `MsbFirst`: the first declared field occupies the most-significant bits.
- This is the single most portability-critical setting and is therefore never defaulted.

### 3.5 Field Types and Range Integration

Each bitfield's value type is the smallest integer/range type that holds its width. Stark's range
types allow making the value bounds part of the type so out-of-range writes are a compile error
rather than a silent truncation:

```stark
[BitFields(Backing = u32, BitOrder = LsbFirst)]
struct StatusWord
{
    Mode     : 3 as u8 in 0..7;    // range-typed: writing 8 is a type error
    Priority : 5;                   // inferred: u8 masked to 0..31
    Flags    : 8;
    Reserved : 16;
}
```

- If a width is given without an explicit `as ... in ...`, the value type is inferred as the
  smallest unsigned integer holding the width, with valid range `0 .. (2^width - 1)`.
- Explicit range typing is recommended for any field with meaningful out-of-range values.
- Signed bitfields: a field MAY be declared with a signed range type (e.g. `as i8 in -4..3`); the
  read sign-extends from the field's high bit.

### 3.6 Addressability

A bitfield is NOT byte-addressable, so a reference/borrow CANNOT be taken to it (unlike a normal
field). Reads and writes go through generated get/mask and set/mask-and-merge operations on the
backing word. This is a deliberate semantic difference from `[FieldOffset]` fields, which ARE
addressable.

---

## 4. Alignment Control (shared)

Both layout kinds support alignment overrides, consistent with the broader FFI alignment item:

```stark
[StructLayout(Explicit, Align = 16)]
struct AlignedUnion { ... }

[StructLayout(Sequential, Pack = 1)]
struct PackedHeader { ... }
```

- `Align = N` sets the struct's alignment to `N` (must be a power of two).
- `Pack = N` caps field alignment at `N` (e.g. `Pack = 1` for fully packed, no padding).
- `Sequential` is the C-compatible ordered layout kind (the default target for plain FFI structs);
  it is listed here for completeness and pairs with `Pack`.

---

## 5. Design Decisions (Rationale)

| Decision | Choice | Why |
|---|---|---|
| Bitfield backing type | Mandatory, explicit | C inference varies by compiler — the top portability bug |
| Bit order | Mandatory, explicit (`LsbFirst`/`MsbFirst`) | The single most portability-breaking C behavior |
| Byte order on unions | Statable (`ByteOrder`) | Sub-field-to-byte mapping flips by endianness |
| Union overlap | Declared via overlap contracts | Replaces C's unsafe-by-default with safe-by-declaration |
| Bitfield addressability | Not referenceable | Honest: a sub-byte field has no address |
| Field-offset addressability | Referenceable (borrow-checked) | Real bytes, real address; aliasing handled by borrow model |
| Sequential vs positioned | Both supported | Mirrors C#'s `Sequential`/`Explicit`; covers auto and manual |

---

## 6. Illustrative Examples

### 6.1 Reinterpreting a 64-bit word as four 16-bit lanes

```stark
[StructLayout(Explicit, ByteOrder = LittleEndian)]
struct Simd64
    overlaps(Packed with Lane0, Lane1, Lane2, Lane3)
{
    [FieldOffset(0)] Packed : u64;
    [FieldOffset(0)] Lane0  : u16;
    [FieldOffset(2)] Lane1  : u16;
    [FieldOffset(4)] Lane2  : u16;
    [FieldOffset(6)] Lane3  : u16;
}
```

### 6.2 A hardware status register

```stark
[BitFields(Backing = u32, BitOrder = LsbFirst)]
struct ControlRegister
{
    Enable    : 1 as u8 in 0..1;
    Mode      : 2 as u8 in 0..3;
    Priority  : 3 as u8 in 0..7;
    Reserved0 : 2;
    Channel   : 8;
    Reserved1 : 16;
}
```

### 6.3 A packed wire header

```stark
[StructLayout(Sequential, Pack = 1, ByteOrder = BigEndian)]
struct PacketHeader
{
    Version : u8;
    Type    : u8;
    Length  : u16;
    Id      : u32;
}
```
