# LLVM Metadata Reference

## Address Values

SSA address values that are only zero-offset projections should be emitted as
aliases to the underlying pointer, not as textual `getelementptr ..., i32 0`
instructions. This applies to address-of-local, address-of-indirect-parameter,
and non-fixed-array element address with constant index `0`.

Tests should assert the semantic address shape directly, for example `%arg_box`
or `%slot_values`, rather than requiring an intermediate `%vN` address temp when
the temp would only name the same pointer.

Borrow-backed callable and aggregate values use their pointee storage for
component loads. A `borrow closure<...>` call must load the invoke/environment
fields from the borrowed closure object; it must not `extractvalue` from the
runtime pointer.

## TBAA

TBAA roots for pointer-backed borrow parameters are the borrowed pointee
storage, not the pointer carrier. For example, field and fixed-array element
loads through `borrow Buffer` should use struct-path tags rooted at
`stark.Buffer`, with field offsets accumulated through the aggregate path.

Do not attach TBAA when a root escapes through raw pointer or pointer-integer
conversion. Keeping raw-pointer escapes conservative is more important than
recovering local aliases after the provenance has been erased.

## Invariant Loads

Emit LLVM `!invariant.load` only for permanent immutable memory roots:

- immutable/const globals emitted or imported as LLVM `constant`
- string/text literal payload storage
- field, element, slice, or raw-pointer address expressions that still resolve
  directly to those permanent roots

Do not emit `!invariant.load` for `const` parameters, frozen/readonly pointer
parameters, immutable stack locals, stack locals with `llvm.invariant.start`, or
immutable pointer variables whose pointee is not proven permanent immutable.

`llvm.invariant.start` may still mark once-initialized, non-escaping stack
storage after initialization. It is a dynamic immutability marker; it does not
license `!invariant.load` on loads from that storage.

Optimized runtime coverage should include long imported-helper boolean chains
over stack-built aggregates, because these expose unsound invariant-load
metadata that `-O0` will not catch.
