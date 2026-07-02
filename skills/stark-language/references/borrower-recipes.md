# Stark Borrower Recipes

Use the strictest access form that still lets the function do its job. Stark
borrows are non-null, non-owning, and checked by the borrower rules. Raw
pointers are for explicit low-level boundaries.

## Quick Choice Table

| Need | Use |
| --- | --- |
| Function takes ownership | `T` |
| Temporary read-only access | `borrow T` |
| Temporary mutable access | `mut borrow T` |
| Return a borrow to caller-owned storage | `retborrow T` |
| Store or retain a borrow | `storeborrow T` |
| Deep read-only access for a borrow lifetime | `frozen T` |
| Permanent deeply immutable graph | `const T` |
| Shared state domain | `shared T` |
| Nullable, foreign, or unchecked address | `rawptr<T>` or `rawmutptr<T>` in `unsafe` |
| Caller-owned output slot | `out T` |
| Caller-owned uninitialized destination | `init T` |

## Temporary Read

Use `borrow` when the callee only reads and does not retain the value.

```stark
struct Counter
{
    i32[min max] Value;
}

finite law i32[min max] Current(borrow Counter counter)
{
    return counter.Value;
}
```

## Temporary Mutation

Use `mut borrow` when the callee mutates caller-owned storage but does not
take ownership.

```stark
finite void Add(mut borrow Counter counter, i32[min max] amount)
{
    counter.Value += amount;
    return;
}
```

## Return A Borrow

Use `retborrow` when the API deliberately returns a borrow to storage supplied
by the caller.

```stark
finite law retborrow i32[min max] Slot(retborrow Counter counter)
{
    return counter.Value;
}
```

Do not return a plain `borrow`. If the function only needs a value, return the
value instead.

## Write Into Caller Storage

Use `out` for a caller-owned slot that must be written before a successful
return. The callee may not read the previous contents.

```stark
fn bool TryDivide(
    i32[min max] numerator,
    i32[min max] denominator,
    out i32[min max] result)
{
    if (denominator == 0)
    {
        return false;
    }

    result = numerator / denominator;
    return true;
}
```

Use `init` when the destination starts uninitialized.

```stark
fn void Fill(init u32[0 max][] destination, u64[0 max] count, u32[0 max] value)
{
    for willexit independent (stack mut u64[0 max] index = 0; index < count; index += 1)
    {
        destination[index] = value;
    }

    return;
}
```

## Deep Read-Only Access

Use `frozen` when a function needs to read through a reachable graph without
allowing mutation through that access.

```stark
struct Box
{
    i32[min max] Value;
}

finite law i32[min max] ReadFrozen(frozen Box box)
{
    return box.Value;
}
```

Use `const` for values that are permanently deeply immutable and can be shared
as read-only data.

```stark
struct Table
{
    i32[min max][3] Values;
}

finite law i32[min max] ReadMiddle(const Table table)
{
    return table.Values[1];
}
```

`const` is not an aliasing promise. Use memory contracts when non-overlap is
part of the API.

## Memory Separation

Memory-backed parameters are non-overlapping by default for ordinary Stark
functions. Reach for a relation only when the API needs a different rule.

```stark
fn void Copy(borrow u8[0 max][] source, borrow mut u8[0 max][] destination)
{
    return;
}

fn void MoveOverlapSafe(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    return;
}

fn bool IsSameBuffer(
    borrow u8[0 max][] left,
    borrow u8[0 max][] right)
    where same(left, right)
{
    return true;
}
```

When one parameter (a threaded context or fact table) may overlap every other
memory-backed parameter, `where overlap_all(name)` replaces the N-clause
pairwise list; it expands at type-check into the same pairwise
`overlap(name, other)` facts, and pairs not involving `name` keep the default
non-overlap:

```stark
fn void LowerExpr(
    borrow u8[0 max][] source,
    borrow u8[0 max][] valueFacts,
    mut borrow u8[0 max][] output)
    where overlap_all(valueFacts)
{
    return;
}
```

Use `if disjoint(...)` when a function accepts overlap but can take a faster
path when the actual regions are separate.

```stark
fn bool TryCopyFast(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    if disjoint(source, destination)
    {
        Copy(source, destination);
        return true;
    }

    MoveOverlapSafe(source, destination);
    return false;
}
```

Use `unsafe assume disjoint(...)` only for a small audited region where an
external condition proves separation and Stark cannot see it.

## Bounded Raw Pointer Regions

Prefer bounded raw pointers over unbounded raw addresses when unsafe code
works over contiguous memory.

```stark
unsafe fn void CopyRaw(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
    where disjoint(source[0, length], destination[0, length])
{
    stack i8[min max][] sourceView = slice(source, length);
    stack mut i8[min max][] destinationView = slice(destination, length);

    for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
    {
        destinationView[index] = sourceView[index];
    }

    return;
}
```

Positive counts require a non-null pointer. A zero-length region may be
`null`. Keep the unsafe conversion close to the foreign or raw boundary.

## Common Fixes

- If a value is used after move, reinitialize it or change the callee to borrow.
- If a returned borrow is rejected, use `retborrow` or return an owned value.
- If a function only reads, do not accept `mut borrow`.
- If nullable input is real, keep it as `rawptr<T>` and convert to a Stark
  result/status after checking `null`.
- If two regions may overlap, write `where overlap(...)` and handle that case.
- If a closure mutates captured state, pass it as `mut borrow closure<mut ...>`.
- If a stored callback needs captured state, use a heap closure that owns or
  copies what it retains.
