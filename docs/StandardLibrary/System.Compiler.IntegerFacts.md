# `System.Compiler.IntegerFacts`

`System.Compiler.IntegerFacts` is the bounded integer-fact helper surface used
by the self-hosted compiler plan. It replaces the host implementation's C#
`BigInteger` convenience with explicit `i1024` / `u1024` operations and
diagnostic-friendly result values.

This module is ordinary standard-library code. It is separate from
compiler-known structural predicates such as `System.Compiler.IsStruct<T>()`,
which remain compile-time-only compiler facts and erase before runtime lowering.

## Public Types

- `IntegerFactError`: invalid bit widths, invalid shifts, empty ranges,
  negative unsigned conversion, overflow, known-bit conflicts, and mismatched
  bit widths.
- `IntegerFactResult<T>`: `[Ok]` / `[Err]` result wrapper for integer-fact
  operations.
- `IntegerRangeStorageViolationKind`: storage-shape diagnostics for range-typed
  integers.
- `IntegerTypeBounds`: signed/unsigned bounds for a bit width.
- `IntegerStorageClass` and `IntegerStorageSuggestion`: compact storage
  selection facts.
- `SignedRange` and `UnsignedRange`: bounded range endpoints.
- `IntegerTagType`: enum tag bit width and maximum tag.
- `KnownBits`: known-zero and known-one masks for SSA/value-fact reasoning.

## Public Surface

The module provides helpers for:

- validating bit widths and supported integer storage widths
- computing signed and unsigned min/max values
- checking signed and unsigned range fit
- converting signed values to unsigned and unsigned values to signed with
  explicit diagnostics
- creating, intersecting, unioning, and testing signed/unsigned ranges
- selecting the smallest signed or unsigned storage class for a range
- computing enum tag storage from variant counts
- checked signed/unsigned add, subtract, multiply, and shift operations
- masked shifts for source operations that explicitly want mask semantics
- constructing and combining known-bit facts
- two's-complement normalization and sign extension

## Example

```stark
import System.Compiler.IntegerFacts
module Example

export finite law bool FitsInUnsignedTag(u64[0 max] variants)
{
    stack IntegerFactResult<IntegerTagType> tag = TagTypeForVariantCount(variants);
    switch (tag)
    {
        case IntegerFactResult<IntegerTagType>.Err(var error):
            return false;
        case IntegerFactResult<IntegerTagType>.Ok(var info):
            return info.BitWidth <= 16;
    }
}
```
