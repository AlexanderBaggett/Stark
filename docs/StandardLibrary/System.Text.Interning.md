# `System.Text.Interning`

`System.Text.Interning` is the blessed string-interning surface for Stark. It is
an explicit *compiler-architecture* pattern: intern an `ascii` name once at a
boundary, then carry and compare the resulting compact typed ID in hot paths
instead of re-hashing or re-comparing the underlying bytes.

This is deliberately *not* hidden behavior of ordinary text. Two equal `ascii`
values are still two independent byte buffers; nothing is automatically interned.
You opt in by handing a name to an interner and getting back a small `u32`-backed
ID, and you keep using that ID until you need to materialize a name again.

The module declares one ID/interner pair per compiler-relevant namespace
(symbols, types, modules, packages, fields, members, artifact keys). The ID
types are distinct and **do not interconvert**: a `SymbolId` and a `TypeId` are
not assignment- or comparison-compatible even though both wrap a `u32`. That
separation is the point — it makes "I compared a type id against a symbol id" a
type error rather than a silent logic bug.

`System.Text.Interning` is a public module but is not re-exported from the package
root; import it explicitly with `import System.Text.Interning`.

## Public Surface

```stark
import System.Collections
import System.Memory
import System.Text
module System.Text.Interning

public enum TextInternerError
{
    TooManyItems,
    InvalidId,
    Memory from System.Memory.MemoryError,
}

public enum TextInternerResult<T>
{
    [Ok] Ok(T),
    [Err] Err(TextInternerError),
}

// Typed-ID wrappers. Each wraps a u32 index. Distinct ID types do not
// interconvert. All share the same value-only interface:
public struct SymbolId
{
    inline finite law u32[0 max] Index(borrow SymbolId self);
    static finite law u64[0 max] Hash(borrow SymbolId value);
    static finite law bool Equals(borrow SymbolId left, borrow SymbolId right) where overlap(left, right);
    static finite law System.Collections.Ordering Compare(borrow SymbolId left, borrow SymbolId right) where overlap(left, right);
}

public struct TypeId        { /* same Index/Hash/Equals/Compare interface */ }
public struct ModuleId      { /* same Index/Hash/Equals/Compare interface */ }
public struct PackageId     { /* same Index/Hash/Equals/Compare interface */ }
public struct FieldId       { /* same Index/Hash/Equals/Compare interface */ }
public struct MemberId      { /* same Index/Hash/Equals/Compare interface */ }
public struct ArtifactKeyId { /* same Index/Hash/Equals/Compare interface */ }

// One interner per ID type. Each owns its name storage and lookup tables.
// All share the same interface:
public struct SymbolInterner
{
    inline finite law u64[0 2 ** 63 - 1] Count(borrow SymbolInterner self);
    inline finite law bool IsEmpty(borrow SymbolInterner self);
    inline finite law bool Contains(borrow SymbolInterner self, ascii name) where overlap(self, name);
    inline fn bool TryGet(borrow SymbolInterner self, ascii name, out SymbolId id) where overlap(self, name), overlap(name, id);
    fn TextInternerResult<SymbolId> Intern(mut borrow SymbolInterner self, ascii name) where overlap(self, name);
    fn TextInternerResult<System.Text.OwnedAscii> CopyName(mut borrow SymbolInterner self, SymbolId id);
}

public struct TypeInterner        { /* same interface, TypeId */ }
public struct ModuleInterner      { /* same interface, ModuleId */ }
public struct PackageInterner     { /* same interface, PackageId */ }
public struct FieldInterner       { /* same interface, FieldId */ }
public struct MemberInterner      { /* same interface, MemberId */ }
public struct ArtifactKeyInterner { /* same interface, ArtifactKeyId */ }
```

The seven ID types — `SymbolId`, `TypeId`, `ModuleId`, `PackageId`, `FieldId`,
`MemberId`, `ArtifactKeyId` — each have a one-to-one matching interner —
`SymbolInterner`, `TypeInterner`, `ModuleInterner`, `PackageInterner`,
`FieldInterner`, `MemberInterner`, `ArtifactKeyInterner`.

## Result Vocabulary

`TextInternerError` is the failure enum:

- `TooManyItems` — the interner is full. IDs are `u32` indices, so an interner
  saturates at `2 ** 32 - 1` distinct names.
- `InvalidId` — an ID was handed back that the interner never produced (out of
  range for the current population). This is what `CopyName` returns for a bad
  ID.
- `Memory from System.Memory.MemoryError` — a funnel variant carrying an
  allocation failure raised while growing name storage or lookup tables.

`TextInternerResult<T>` is the `[Ok]`/`[Err]` result carrier used for the
fallible operations (`Intern`, `CopyName`). The infallible queries (`Count`,
`IsEmpty`, `Contains`) return plain values, and `TryGet` returns a `bool` plus an
`out` ID rather than a result.

## Interning And Lookup Behavior

`Intern(name)` returns the ID for `name`, allocating a fresh ID the first time a
name is seen and returning the *same* ID on every subsequent call with an equal
name — interning is idempotent. The `name` argument is a borrowed `ascii`; the
interner copies the bytes into its own storage, so the caller's buffer does not
need to outlive the call.

IDs are assigned in **insertion order**: the first distinct name interned gets
index `0`, the second gets index `1`, and so on. `Index()` exposes that raw
`u32`, which makes IDs usable as dense array indices into parallel side tables.

`Contains(name)` and `TryGet(name, out id)` are lookup-only: they hash the
borrowed `ascii` name and probe the existing table without inserting. `Contains`
returns a `bool`; `TryGet` returns `false` (leaving the `out` ID unset) on a miss
and `true` with the ID on a hit. Use these when you must not grow the interner.

`CopyName(id)` is the reverse mapping: given an ID the interner produced, it
returns a freshly allocated `System.Text.OwnedAscii` copy of the stored name. It
is a copy, not a borrow, so the returned text is independent of the interner's
internal buffer. An ID outside the interned range yields
`TextInternerError.InvalidId`.

`Count()` reports how many distinct names have been interned, and `IsEmpty()` is
the `Count() == 0` shorthand.

## Ordinal Text Key Semantics

Name equality and hashing inside an interner are **ordinal**: keys are compared
and hashed by exact byte / code-point value. There is:

- no case folding (`"Foo"` and `"foo"` are different keys),
- no Unicode normalization (no NFC/NFD folding of composed vs. decomposed
  sequences),
- no locale sensitivity.

Two names collide in an interner only when their bytes are identical. This is a
frozen contract: the interner is a determinism-critical compiler component, so
its key identity must be stable and platform-independent. If you need
case-insensitive or normalized identity, normalize the text yourself *before*
interning and treat the normalized form as the key.

## Usage Pattern

```stark
import System.Text.Interning
module App

fn System.Text.Interning.TextInternerResult<bool> SameSymbol(
    mut borrow System.Text.Interning.SymbolInterner symbols,
    ascii left,
    ascii right)
    where overlap(symbols, left),
          overlap(symbols, right)
{
    // Intern at the boundary; compare compact IDs in the hot path.
    stack System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId> leftResult =
        symbols.Intern(left);
    switch (leftResult)
    {
        case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Err(var error):
            return System.Text.Interning.TextInternerResult<bool>.Err(error);
        case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Ok(var leftId):
            stack System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId> rightResult =
                symbols.Intern(right);
            switch (rightResult)
            {
                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Err(var error):
                    return System.Text.Interning.TextInternerResult<bool>.Err(error);
                case System.Text.Interning.TextInternerResult<System.Text.Interning.SymbolId>.Ok(var rightId):
                    return System.Text.Interning.TextInternerResult<bool>.Ok(
                        System.Text.Interning.SymbolId.Equals(leftId, rightId));
            }
    }
}
```

Because the ID types are distinct, the same pattern with a `TypeInterner` would
produce `TypeId` values that cannot be accidentally compared against `SymbolId`
values — the type checker rejects it.

## Function Kinds

The value-only ID operations (`Index`, `Hash`, `Equals`, `Compare`) and the
read-only interner queries (`Count`, `IsEmpty`, `Contains`) are `finite law`:
they always return and observe no mutable external state. `TryGet` is an
`inline fn` because it writes through an `out` parameter. `Intern` and
`CopyName` are ordinary `fn` because they mutate interner storage and can fail
with an allocation error.
