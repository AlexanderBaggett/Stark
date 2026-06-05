# Stark Syntax Quick Reference

This is a compact lookup for writing Stark source. Use Allman braces in Stark
code.

## Source File Shape

```stark
import System.Console
import System.IO
module Demo.App

export fn i32[min max] main()
{
    if (WriteLine("Hello") != IOStatus.Ok)
    {
        return 1;
    }

    return 0;
}
```

- Imports come before `module`.
- Imports and `module` declarations do not end in semicolons.
- One source file declares one module.
- Wildcard imports are forbidden.
- Use `export import Some.Module` only when intentionally re-exporting.

## Keywords By Area

Modules and visibility:

```text
import module internal public export
```

Functions and modifiers:

```text
fn finite law inline noinline inlinehint hot cold ffi varargs unsafe strictfp asm static
```

Data declarations:

```text
struct record enum trait doctrine alias drop const from
```

Storage and access:

```text
stack heap register arena borrow retborrow storeborrow frozen shared out init mut rawptr rawmutptr fnptr sizeof alignof
```

Control flow and patterns:

```text
if else switch case default when while for infinite non-deterministic willexit return break continue where var try is
```

Builtins and literals:

```text
void bool ascii unicode Ascii Unicode true false null
i8 i16 i24 i32 i48 i64 i96 i128 i192 i256 i384 i512 i768 i1024
u8 u16 u24 u32 u48 u64 u96 u128 u192 u256 u384 u512 u768 u1024
f16 f32 f64 f80 f128
```

## Function Kinds

| Kind | Use |
| --- | --- |
| `fn` | General function, including IO, allocation, blocking, FFI, and shared state |
| `finite` | Guaranteed progress and return, but not pure |
| `law` | Pure/read-only, no visible side effects |
| `finite law` | Pure/read-only and guaranteed to return |

The keyword order is fixed:

```stark
finite law i32[min max] Clamp(i32[min max] value)
{
    return value;
}
```

## Traits And Associated Types

```stark
trait Reader
{
    alias Item;

    finite law Self.Item Read(borrow Self self);
}

struct Counter : Reader
{
    alias Item = i32[min max];

    finite law i32[min max] Read(borrow Counter self)
    {
        return 0;
    }
}
```

- `alias Name;` is a required associated type in a trait.
- `alias Name = Type;` defines a concrete/default associated type in a trait,
  doctrine, struct, or record.
- Use `Self.Name` or `T.Name` in type positions.
- Ordinary trait dispatch is static; `dyn trait` is explicit runtime dispatch
  and currently cannot declare associated types.

## Visibility

| Spelling | Meaning |
| --- | --- |
| no keyword | Module-private |
| `internal` | Same package/library |
| `public` | Downstream Stark source API |
| `export` | Binary symbol for entrypoints, FFI, plugins, or ABI boundaries |

Use `public` for ordinary Stark APIs. Use `export` only for a real binary
boundary.

## Integer Widths And Ranges

Signed widths:

```text
i8 i16 i24 i32 i48 i64 i96 i128 i192 i256 i384 i512 i768 i1024
```

Unsigned widths:

```text
u8 u16 u24 u32 u48 u64 u96 u128 u192 u256 u384 u512 u768 u1024
```

Runtime integer types carry ranges:

```stark
i32[0 100]
i32[min max]
u8[0 255]
u64[0 max]
i32[-(2 ** 10) (2 ** 10) - 1]
```

For unsigned widths, `min` is `0`. For signed widths, `min` is the signed
minimum for that width. `max` is the largest value for the chosen width.

## Operators

Arithmetic:

```text
+ - * / % **
```

Wrapping arithmetic:

```text
+% -% *%
+%= -%= *%=
```

Saturating arithmetic:

```text
+| -| *|
+|= -|= *|=
```

Assignment:

```text
= += -= *= /= %= &= |= ^=
```

Comparison and logic:

```text
== != < > <= >= && || !
```

Bitwise and shifts:

```text
& | ^ ~ << >>
```

Notes:

- `**` is exponentiation.
- `^` is bitwise XOR, not exponentiation.
- Comparison chains such as `a < b < c` are valid.
- Explicit conversion uses `(targetType)value`.
- The conditional expression is `condition ? whenTrue : whenFalse`.

## Calls, Access, And Containers

```stark
Add(1, 2)              // call
value.Field            // member access
System.Console.Write   // qualified name
values[index]          // index
values[start, length]  // slice/window
new Box()              // constructor/object creation
sizeof(T)              // size query
alignof(T)             // alignment query
```

Fixed arrays own storage. Slices are views.

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};

stack i32[min max][] view = values;
```

## Switch Shapes

```stark
switch (token)
{
    case Token.End:
        return 0;
    case Token.Integer(0..9) | Token.Integer(10..19):
        return 1;
    case Token.Integer(var value):
        return value;
    case Token.Move
    {
        X: var x, Y: var y
    }:
        return x + y;
}
```

Supported patterns include literals, enum cases, aggregate fields, `_`,
`var` captures, switch-label or-patterns (`case A | B:`), inclusive integer
range patterns (`case 0..10:`), `default`, and `when` guards. Range patterns
are integer-only and can appear inside enum/aggregate field patterns.

Switches must be exhaustive (cover every enum variant / bool value / ranged-integer
value, for example with `case 0..3:`, or add `default`), and non-`void`
functions must return on every path.
`when`-guarded arms do not count toward coverage.

## Error Propagation And Pattern Conditions

```stark
enum Outcome<T>
{
    [Ok] Got(T),
    [Err] Failed(FetchError),
}

stack i32[min max] value = try Fetch(x);

if (Lookup(key) is Option<Value>.Some(var found))
{
    Use(found);
}

while willexit (queue.Pop() is Option<Job>.Some(var job))
{
    Run(job);
}
```

- `[Ok]`/`[Err]` variant attributes make any two-variant enum propagatable.
- `try expr` unwraps the `[Ok]` payload or early-returns the `[Err]` failure,
  rewrapped in the enclosing return type's `[Err]` variant (`from` funnels
  convert differing error types).
- `try` sits only at statement boundaries: binding initializer, assignment
  right side, `return` operand, or bare expression statement.
- `expr is pattern` in `if`/`while` conditions binds captures on the matching
  path only.

## Text Forms

```stark
ascii view = "ascii text";
unicode wide = (unicode)"unicode text";
stack Ascii label[64] = $"Score: {score}";
```

- `ascii` and `unicode` are views.
- `Ascii` and `Unicode` own storage.
- Runtime interpolation needs caller-selected storage.

## Callable Forms

```stark
fnptr<finite law i32[min max](i32[min max])>
inline closure<fn i32[min max](i32[min max])>
borrow closure<fn i32[min max](i32[min max])>
mut borrow closure<mut fn i32[min max](i32[min max])>
heap closure<fn i32[min max](i32[min max])>
heap closure<once fn i32[min max]()>
```

Use direct calls first. Use `fnptr` for thin non-capturing callbacks. Use
closures when capture is needed.
