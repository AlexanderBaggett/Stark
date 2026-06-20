# LLVM Invariant Metadata

Stark may emit LLVM immutability facts only when the LLVM contract is exactly
met. Source-level immutability is not automatically enough.

## `!invariant.load`

Attach `!invariant.load` only when the loaded address denotes memory whose
contents are stable for every point where that address is dereferenceable:

- immutable or const global storage emitted as LLVM constant storage
- imported readonly global storage declared as LLVM `constant`
- string/text literal payload storage
- field, element, slice, or raw-pointer address expressions that still resolve
  directly to one of those permanent immutable roots

Do not attach `!invariant.load` merely because a value is reached through:

- a `const` parameter
- a readonly/frozen/raw const pointer parameter
- an immutable stack local
- a stack local marked with `llvm.invariant.start`
- an immutable pointer variable whose pointee is not proven to be permanent
  immutable storage

LLVM treats `!invariant.load` as a stronger promise than Stark's ordinary
readonly or borrow guarantees. Using it for stack-built aggregates can let LLVM
fold unrelated field loads together and miscompile optimized code.

## `llvm.invariant.start`

`llvm.invariant.start` is the dynamic marker for memory that becomes immutable
after initialization. Stark may still emit it for stack locals that have one
direct initialization write, do not escape, and are not subsequently written.
Loads from those locals must not also receive `!invariant.load`.

## Regression Shape

Keep optimized native coverage for imported finite-law helper chains over
stack-built aggregates. A long `&&` chain over `System.Testing.Diagnostic`
predicates caught the previous invalid metadata because optimized folding
collapsed the chain incorrectly while the unfolded form behaved.
