# Stark Diagnostics Guide

Stark diagnostics usually mean a required condition is not visible in the
source. Read the category first, then change the code so ownership, borrow,
range, storage, or package intent is explicit.

## Reading Order

1. Find the diagnostic category.
2. Look at the highlighted source expression.
3. Ask what type, ownership, borrow, range, package, or native rule was
   expected.
4. Make that condition visible in source.

Avoid moving code around blindly. A Stark diagnostic is usually pointing to a
missing source fact.

## Use After Move

If a value was moved, it cannot be read until reinitialized.

```stark
struct Box
{
    i32[min max] Value;
}

fn void Inspect(borrow Box value)
{
    return;
}

fn i32[min max] ReadAfterBorrow()
{
    stack Box box = new Box()
    {
        Value = 1
    };

    Inspect(box);
    return box.Value;
}
```

If the callee truly needs ownership, replace the value before reading it again.

```stark
fn void Consume(Box value)
{
    return;
}

fn i32[min max] MoveThenReplace()
{
    stack mut Box box = new Box()
    {
        Value = 1
    };

    Consume(box);
    box = new Box()
    {
        Value = 2
    };

    return box.Value;
}
```

## Borrow Escapes

Plain `borrow` is temporary. Use `retborrow` when the borrow deliberately
escapes through the return value.

```stark
struct Cell
{
    i32[min max] Value;
}

finite law retborrow i32[min max] GoodSlot(retborrow Cell cell)
{
    return cell.Value;
}
```

If callers only need the value, return an owned value instead.

## Immutable Local Assignment

Use `mut` only when the binding is meant to change.

```stark
fn i32[min max] CountToTwo()
{
    stack mut i32[min max] value = 0;
    value += 1;
    value += 1;
    return value;
}
```

Do not mark every local `mut`. Treat it as a source-level note that the value
changes.

## Null Borrow

Safe borrows are non-null. Keep nullable data as raw until checked, then return
a Stark status or enum.

```stark
public enum MessageStatus
{
    Missing,
    Present,
}

unsafe ffi fn rawptr<i8[min max]> platform_message();

public unsafe fn MessageStatus CheckMessage()
{
    stack rawptr<i8[min max]> pointer = platform_message();
    if (pointer == null)
    {
        return MessageStatus.Missing;
    }

    return MessageStatus.Present;
}
```

## Hidden Slice Storage

An array initializer needs owning backing storage. Create a fixed array first,
then take a slice view.

```stark
fn i32[min max] First()
{
    stack i32[min max][3] values =
    {
        1, 2, 3
    };

    stack i32[min max][] view = values;
    return view[0];
}
```

Do not assign an array initializer directly to a slice.

## Overlap Rejected

Ordinary memory-backed parameters are non-overlapping by default. If overlap
is part of the API, say so and handle it.

```stark
fn void MoveOverlapSafe(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    return;
}
```

Use `if disjoint(...)` inside an overlap-safe API when a fast path requires
separate regions.

When STK3030 fires once per parameter against the same threaded context table,
collapse the pairwise list to `where overlap_all(tableName)`; it expands to
the same pairwise facts. STK3029 on an `overlap_all` clause means the named
target is not a parameter or is not memory-backed.

Related signature diagnostics: a duplicated parameter name is STK3057 (a
located diagnostic naming the parameter and function — not a crash), and
diagnostics raised inside an imported source module carry that module's own
file path, so trust the reported path when it differs from the root file.

## Range Or Narrowing Error

A function body must be valid for every value promised by its parameter type.
Put the smaller range on the input or check before converting.

```stark
finite law i32[0 10] KeepSmall(i32[0 10] value)
{
    return value;
}

fn bool TryKeepSmall(i32[min max] value, out i32[0 10] destination)
{
    if (value < 0 || value > 10)
    {
        return false;
    }

    destination = (i32[0 10])value;
    return true;
}
```

## Function Kind Mismatch

A stronger function kind cannot call work that violates its promise. If a
helper is pure and always returns, give the helper the stronger kind. If it
does IO, allocation, synchronization, or FFI, make the caller an ordinary
`fn`.

```stark
finite law i32[min max] AddOne(i32[min max] value)
{
    return value + 1;
}

finite law i32[min max] UseAddOne(i32[min max] value)
{
    return AddOne(value);
}
```

## Callable Mismatch

`fnptr` carries the function kind. A general `fn` cannot satisfy a
`fnptr<finite law ...>` slot.

```stark
finite law i32[min max] Clamp(i32[min max] value)
{
    if (value < 0)
    {
        return 0;
    }

    return value;
}

fn i32[min max] UseClamp()
{
    stack fnptr<finite law i32[min max](i32[min max])> op = Clamp;
    return op(4);
}
```

Capturing lambdas cannot be stored in a plain `fnptr`. Use a closure type.

## Switch Coverage And Dead Arms

Every `switch` must be exhaustive: cover all enum variants (or both bools, or every
value of a ranged integer), or include a `default`. A value with no matching arm is a
compile error — never a runtime trap or a silent fall-through. `when`-guarded arms do
not count toward coverage.

When all enum variants are already handled, a later `default` arm is dead.
Remove the dead arm or make an earlier pattern narrower.

```stark
enum Token
{
    End,
    Integer(i32[min max]),
}

finite law i32[min max] Score(Token token)
{
    switch (token)
    {
        case Token.End:
            return 0;
        case Token.Integer(var value):
            return value;
    }
}
```

## Missing Return Paths

A non-`void` function must return on every control-flow path. If control can fall
out of the body, the compiler reports it — add a `return`, give the final `if` an
`else` that returns, or make the final `switch` exhaustive with every section
returning.

```stark
finite law i32[min max] Sign(i32[min max] value)
{
    if (value < 0)
    {
        return -1;
    }
    else
    {
        return 1;
    }
}
```

## Error Propagation Roles And `try`

`try` only works when both the operand's type and the enclosing function's
return type are propagatable enums: two-variant enums whose declarations mark
one variant `[Ok]` and one `[Err]`. An enum with the right shape but no role
attributes is rejected — add the attributes; renaming variants changes nothing
because roles never come from names.

```stark
enum FetchError { Timeout }

enum FetchOutcome
{
    [Ok] Got(i32[min max]),
    [Err] Failed(FetchError),
}

fn FetchOutcome Read(i32[min max] x)
{
    if (x < 0)
    {
        return FetchOutcome.Failed(FetchError.Timeout);
    }

    return FetchOutcome.Got(x + 1);
}

fn FetchOutcome Pipe(i32[min max] x)
{
    stack i32[min max] value = try Read(x);
    return FetchOutcome.Got(value * 2);
}
```

Role declaration errors point at the enum itself: a role-carrying enum needs
exactly two variants, one of each role, each with at most one payload, and
`[Ok]`/`[Err]` take no arguments.

If the operand and the enclosing function fail with different error types,
declare a `from` funnel on the enclosing error enum
(`enum AppError { Fetch from FetchError }`); a missing funnel is a compile
error, never an inferred conversion. Unit failures (an `[Err]` variant with no
payload) only propagate into other unit failures.

`try` may only sit at a statement boundary: a binding initializer, an
assignment right side, the operand of `return`, or a bare expression
statement. `Use(try a(), try b())` is rejected — bind each fallible call to a
local first, then `try` each local on its own line.

## Enum ABI Boundary

Do not expose ordinary Stark enums directly as C ABI values. Convert to an
explicit scalar tag or interop representation at the wrapper boundary.

```stark
public enum Mode
{
    Fast,
    Safe,
}

internal finite law i32[min max] ModeTag(Mode mode)
{
    switch (mode)
    {
        case Mode.Fast:
            return 1;
        case Mode.Safe:
            return 2;
    }
}
```

## Package And Native Diagnostics

Check source imports and manifest dependencies together.

```stark
import Geometry
module App
```

```toml
[dependencies]
geometry = { path = "../geometry" }
```

For native package errors, fix the package that owns the FFI boundary.

For an installed official `Vendor.*` package, first run
`stark doctor --strict`. A missing native artifact or checksum mismatch is an
SDK-integrity failure; do not add `pkg-config`, `STARK_PATH`, `-I`, or `-L` as a
workaround. The custom/source package example below is the case where discovery
metadata belongs in the package manifest.

```toml
[native]
pkg-config = ["raylib"]
```

Do not copy linker flags into every consuming executable.

## Debugging Habits

- Split complex expressions into named locals.
- Write the storage class and integer range you intend.
- Use `borrow` when a function does not need ownership.
- Use `retborrow` only when a returned borrow is intentional.
- Handle status/result values with `switch`.
- Keep raw pointer work inside small wrapper functions.
- Keep private package details behind `internal` or module-private APIs.

## Hard-Won Checker And Lowering Gotchas

Patterns discovered while porting real test/stdlib code against the host
compiler (June 2026). Each has a proven restructure.

### Law Demotions (STK4106 Family)

`Law 'X' may only call other laws` — a `law` body calls a non-law. Either
strengthen the callee honestly or drop `law` from the caller (keep `finite`).
`out` parameters demote a function from `law` even when the body looks pure:
`internal finite bool TryFind(..., out u64[0 2 ** 63 - 1] start)` is the
ceiling; a sibling helper without out-params can stay `finite law`.

### Ownership Joins Across Branches (fixed June 2026)

An early `return value;` (a move) inside one `switch` arm or `if` branch used
to poison the binding on the other paths — later use reported "value ... is
not fully initialized" at the tail return, forcing flag-and-single-tail-return
rewrites. Fixed: a branch that returns from the function on every path never
reaches the join, so its end-state no longer merges. Early `return value;`
guards, per-arm field stores, and conditional-field-store-then-field-move-store
shapes all validate and run correctly now (the old "clang rejects with `use of
undefined value`" mislower does not reproduce either).

Still true, by design: a move before `break` or `continue` keeps merging into
the enclosing loop's join, so using the moved value below the loop errors —
those paths really do reach it.

### Bare-Name Ambiguity In Imported Source Modules

Imported source module bodies skip full type checking, but a focused scan still
catches a name exported by two of that module's imports (e.g.
`CreateTempDirectory` in `System.Testing` and `System.FileSystem`) used bare:
`STK3003: Imported symbol 'CreateTempDirectory' is ambiguous between
System.FileSystem.CreateTempDirectory, System.Testing.CreateTempDirectory. Use a
fully qualified name.`, located in the imported file. Qualify the ambiguous call.
(Before June 2026 this crashed `lower-mir` with `Named operand 'X' could not be
resolved`; type-position and constructor-body ambiguities can still crash and
remain follow-ups.)

### Literal-Mixed Integer Arithmetic

A bare integer literal adopts the other operand's ranged type when its value
fits, so `lineEnd + 1` on a `u64[0 2 ** 63 - 1]` is itself `u64[0 2 ** 63 - 1]`
— no narrowing cast is needed to store or return it (same as var-with-var
`end - start`). A literal that does not fit, or a negative literal against an
unsigned operand, still reports STK3002 and needs an explicit cast. (Before June
2026 any literal-mixed expression collapsed to `i64` and demanded the cast.)

### Misc Quick Fixes

- `from` is a keyword (enum funnels); it cannot name a parameter or local.
- Unit-length text views compare directly: `value[(i64[min max])index, 1] == "\n"`
  (the stdlib `IsActualUnit` idiom); multi-length slices use `System.Text.Equals`.
- Unit enum variants compare with `==`/`!=` (`status == IOStatus.Ok`); payload
  variants need `switch`/`is`.
- `slice(pointer, count)` rejects hidden roots (STK3029): bind the raw pointer
  to a visible local first, never pass a call result directly.
- Facts in generated `stark test` runners are plain safe `fn bool` collected
  from the root file; wrap harness calls in `unsafe { }` blocks inside the fact.
