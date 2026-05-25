---
name: stark-language
description: Stark language development guidance for writing, reviewing, explaining, and editing .stark source files, Stark.toml project manifests, and Stark.solution.toml solution manifests. Use for Stark syntax, ownership and borrowing, callable values, modules and visibility, project/test/native-package setup, FFI and assembly boundaries, memory contracts, and Stark source style.
---

# Stark Language

Use this skill when producing or reviewing Stark code. Keep code performance-first, explicit, and close to the existing project style. Prefer restrictive visibility, direct calls, explicit memory contracts, and safe ownership/borrow forms before raw pointers or FFI.

## Source Shape

A source file has imports, then one module declaration, then declarations:

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

Rules:

- One source file declares exactly one module.
- Imports appear before `module`; neither imports nor `module` use semicolons.
- Wildcard imports are forbidden.
- `export import Some.Module` is the only re-export form.
- Importing a module makes visible top-level names available by final name; use fully qualified names only for ambiguity or clarity.
- One file is one module; modules are not reopened across files.

Top-level declarations include functions, structs, records, enums, traits, doctrines, aliases, constants, and globals.

## Visibility

Default to the narrowest usable visibility:

- no keyword: module-private, visible only in the current module
- `internal`: visible within the same package/library
- `public`: source API visible to downstream Stark code
- `export`: real binary symbol for FFI, runtime entrypoints, plugins, or stable ABI boundaries

`public` and `export` are intentionally different. Do not use `export` just because downstream Stark source should call something.

Visibility applies to top-level declarations and member functions. It does not apply to fields, locals, parameters, statements, expressions, or plain imports. Member functions inherit the enclosing type visibility unless explicitly narrowed. A member cannot be more visible than its type. `export` is never inherited; write it explicitly on binary-visible members.

## Functions

Function kinds:

- `fn`: general function
- `finite`: guaranteed progress and return
- `law`: pure/read-only/no visible side effects
- `finite law`: both sets of guarantees; keyword order is fixed

Common modifiers:

- `inline`, `noinline`, `inlinehint` are mutually exclusive.
- `hot` and `cold` are mutually exclusive.
- `strictfp` disables Stark's ordinary fast floating-point assumptions for that function.
- `ffi` marks a foreign boundary.
- `static` is only for member functions inside `struct` or `record`.

Use semicolon declarations for FFI and forward declarations:

```stark
unsafe ffi fn i32[min max] puts(ascii text);
```

The hosted entrypoint is:

```stark
export fn i32[min max] main()
{
    return 0;
}
```

Use `unsafe` on `main` only if the body or signature crosses an unsafe/raw/foreign boundary.

## Callable Values

Prefer direct named-function calls unless an API needs a callback value.

Function items are named functions used as callable values:

```stark
finite law i32[min max] Inc(i32[min max] value)
{
    return value + 1;
}

stack fnptr<finite law i32[min max](i32[min max])> op = Inc;
```

Use `fnptr<...>` for thin, non-capturing callbacks. The function kind is part of the type:

```stark
fnptr<fn void()>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

`fnptr` values must come from a compatible named function or non-capturing lambda:

```stark
stack fnptr<fn i32[min max](i32[min max])> square =
    (i32[min max] value) => value * value;
```

Capturing lambdas require an explicit capture list and should use a closure type, not `fnptr`:

```stark
inline fn i32[min max] Apply(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] AddOffset(i32[min max] offset)
{
    return Apply(10, capture(copy offset) (i32[min max] value) => value + offset);
}
```

Capture modes:

- `copy x`: copy a cheap copyable value
- `move x`: move ownership into the callable
- `read x`: capture readonly access to existing storage
- `mut x`: capture mutable access for the closure lifetime
- `out x`: capture a write-only destination
- `init x`: capture uninitialized destination storage

Closure forms:

- `inline closure<...>`: callback is called by the receiving function and cannot be stored or returned
- `borrow closure<...>`: non-owning callback view; captured storage must outlive the view
- `mut borrow closure<mut ...>`: needed when calling mutates the closure environment
- `heap closure<...>`: owned closure for stored, returned, or retained callbacks
- `heap closure<once ...>`: calling consumes the closure

## Types

Integers must include explicit ranges:

```stark
u8[0 max]
i32[min max]
u64[0 max]
u32[1024 * 1024 1024 * 1024 * 1024]
```

Use `uN` for non-negative runtime ranges and use the narrowest supported width when practical. Prefer `[min max]` for full-width signed ranges and `[0 max]` for full-width unsigned ranges.

Scalar integer constants usually omit a type so Stark derives the exact value and smallest storage width:

```stark
const PageSize = 2 ** 12;
const BoardWidth = 80;
```
* signed integer widths: `i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`
* unsigned integer widths: `u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`, `u384`, `u512`, `u768`, `u1024`

use the smallest integer width you need. If you only return values between 0-4 you can use an `u8` for example.


Floating types are `f16`, `f32`, `f64`, `f80`, and `f128`. Unsuffixed decimals are `f64`; suffix with `f` for `f32`.

Aggregate and view forms:

- fixed array: `T[N]` owns N elements
- slice: `T[]` is a non-owning view
- dynamic storage: `dynamic T` owns growable capacity-backed storage
- named data: `struct`, `record`
- closed variants: `enum`

`void` is valid only as a function return type.

## Data Declarations

Use `struct` for ordinary named data with methods and constructors. Use `record` for data-oriented named aggregates. Neither supports inheritance.

```stark
struct Box
{
    i32[min max] Width;
    i32[min max] Height;
}

record Point(i32[min max] X, i32[min max] Y)
{
}
```

Create values with explicit or target-typed `new`:

```stark
stack Box a = new Box();
stack Box b = new();
stack Box c = new()
{
    Width = 3, Height = 4
};
stack Point p = new Point(1, 2);
```

Enums are closed variant families:

```stark
enum Token
{
    End,
    Integer(i32[min max]),
    Move
    {
        X: i32[min max], Y: i32[min max]
    },
}

fn i32[min max] Read(Token token)
{
    switch (token)
    {
        case Token.End:
            return 0;
        case Token.Integer(var value):
            return value;
        case Token.Move
        {
            X: var x, Y: var y
        }:
            return x + y;
    }
}
```

Traits name behavior contracts; they are not runtime values or trait objects. Doctrines bundle `law` functions and constraints; they have no owned identity, heap allocation, or captured environment.

## Ownership And Borrows

Safe Stark has no garbage collector. Owned is the default.

- Non-borrow, non-raw values have exactly one owner.
- Ownership transfers by move.
- A moved value is unusable until reinitialized.
- Owned values drop at scope exit.
- Assignment to an initialized owned place drops the previous value first.
- Safe borrows are non-owning and never null.
- Raw pointers are the only null-capable pointer forms.

Borrow escape classes:

- `borrow T`: temporary access; cannot be stored, returned, or forwarded to unknown code
- `retborrow T`: may escape only through the return value
- `storeborrow T`: may be stored or otherwise escape

Use the strictest borrow that works:

```stark
struct Counter
{
    i32[min max] Value;

    finite law i32[min max] Current(borrow Counter self)
    {
        return self.Value;
    }

    finite void Add(mut borrow Counter self, i32[min max] amount)
    {
        self.Value += amount;
        return;
    }

    finite retborrow mut i32[min max] Slot(mut borrow Counter self)
    {
        return self.Value;
    }
}
```

Use `frozen T` for deeply read-only access during the borrow lifetime. Use `const T` for permanent deeply immutable reachable object graphs. Use `shared T` only for explicit shared-state domains.

## Memory Contracts

Memory-backed function parameters are non-overlapping by default for ordinary Stark functions. This applies to borrows, mutable borrows, slices, text views, `out`, `init`, bounded raw pointer regions, and similar reachable storage.

Use relational contracts only when the default is too strict or too imprecise:

```stark
fn void Copy(borrow u8[0 max][] source, borrow mut u8[0 max][] destination)
{
    return;
}

fn void MoveOverlapSafe(borrow u8[0 max][] source, borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    return;
}

fn void RequireSame(borrow u8[0 max][] left, borrow u8[0 max][] right)
    where same(left, right)
{
    return;
}

fn void CopyWindow(
    rawptr<i8[min max]> source,
    rawmutptr<i8[min max]> destination,
    i64[0 max] sourceStart,
    i64[0 max] length)
    where disjoint(source[sourceStart, length], destination[0, length])
{
    return;
}
```

Do not write whole-parameter `disjoint` on ordinary Stark functions; default parameter non-overlap already covers it. FFI and assembly declarations do not receive the default, so explicit whole-parameter disjointness is the opt-in spelling there.

Use `if disjoint(...)` for a runtime branch that grants non-overlap only in the true branch. Use `unsafe assume disjoint(...) { ... }` only for scoped, externally proven facts the compiler cannot prove.

## Initialization And Dynamic Storage

`out T` and `init T` are write-before-read contracts:

- the callee must write required bytes before successful return
- the callee may not read previous contents
- the caller treats the destination as uninitialized until completion

```stark
fn bool TryWrite(out i32[0 max] value)
{
    value = 7;
    return true;
}
```

`dynamic T` is owned capacity-backed storage, not a storage class. Use `Length`, `Capacity`, `Reserve`, `TryReserve`, `MoveLast`, and `MoveAt` rather than exposing raw pointers.

```stark
struct IntList
{
    dynamic i32[0 max] Items;
}

fn bool Append(mut borrow IntList self, i32[0 max] value)
{
    if (!self.Items.TryReserve(1))
    {
        return false;
    }

    init self.Items[self.Items.Length] = value;
    return true;
}
```

## Raw Pointers, Unsafe, And FFI

Raw pointer forms are `rawptr<T>` and `rawmutptr<T>`. They may be null, dangling, unaligned, aliased, or point to foreign memory. Safe borrows cannot be null.

Unsafe context is required for raw pointer signatures/declarations, FFI, raw locals, address-of `&`, dereference `*`, raw pointer conversions, `slice(pointer, count)`, unsafe calls, unsafe callback erasure, and unsafe capture modes.

Bound raw pointer parameters when possible:

```stark
unsafe fn void Fill(
    i64[0 max] length,
    rawmutptr<i32[min max]>[length] destination)
{
    unsafe
    {
        stack mut i32[min max][] view = slice(destination, length);
        for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
        {
            view[index] = 0;
        }
    }
}
```

FFI rules:

- Declare imported FFI as `unsafe ffi fn`.
- Preserve foreign symbol spelling exactly.
- Use safe wrappers only when they hide raw handles, combine calls, narrow a foreign surface, or define a real Stark-level abstraction.
- Do not let foreign code unwind through Stark frames.
- Stark enums do not cross `ffi` or `export` boundaries.
- C varargs use `unsafe ffi varargs fn`; callers must pass ABI-ready values explicitly.

Assembly functions are unsafe FFI boundaries for small platform/CPU shims:

```stark
internal unsafe ffi asm(x86_64) fn i64[min max] Syscall1(
    i64[min max] number,
    i64[min max] arg1)
    in("rax") number,
    in("rdi") arg1,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}
```

Use `unsafe ffi asm(arch) fn`, `in("reg") parameter`, `out("reg") return`,
and `clobber(...)`. Supported value families are integer scalars, floating
point scalars, raw pointers, and `void` returns. Calls require unsafe context.
Avoid non-return `out("reg") parameter` in source asm bodies; it parses but is
not fully emitted yet. For full rules and target/register details, read
[`references/assembly-functions-reference.md`](references/assembly-functions-reference.md).

## Control Flow

Loops require a behavior keyword:

- `infinite`: statically unconditional, no structural exit, not allowed in `finite`
- `non-deterministic`: may or may not exit, not allowed in `finite`
- `willexit`: expected to make progress and finish; required in `finite`

Use `independent` only when iterations have no loop-carried memory dependency:

```stark
for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
{
    output[index] = input[index] + 1;
}
```

`switch` supports literal cases, `default`, `when` guards, `case var capture`, `_`, enum case patterns, and exact aggregate patterns.

## Expressions And Operators

Expression forms include literals, identifiers, qualified names, calls, member access, indexing/slicing, `new`, field initializers, array initializers, unary/binary operators, ternary `?:`, assignments, and compound assignments.

Operator notes:

- `^` is bitwise XOR.
- `**` is exponentiation.
- Ordinary integer overflow and oversize shifts are illegal/undefined.
- Wrapping integer arithmetic uses `+%`, `-%`, `*%`.
- Saturating integer arithmetic uses `+|`, `-|`, `*|`.
- Comparison chains such as `a < b < c` evaluate each operand once and short-circuit adjacent comparisons.
- Explicit conversions use C-style casts: `(targetType)value`.
- `strictfp` is required for strict IEEE-style floating point; ordinary floating point is fast-math friendly.

Array initializer `{ ... }` needs owning backing storage:

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};
stack i32[min max][] view = values;
```

Do not assign an array initializer directly to a slice.

## Text

Text forms:

- `ascii`: UTF-8 view
- `unicode`: UTF-32 view
- `Ascii`: owned ASCII/UTF-8 container
- `Unicode`: owned Unicode container

String literals infer to `ascii` when possible. Use `(unicode)"..."` for Unicode target text. Text indexing/slicing is zero-copy:

```stark
text[]
text[index]
text[start, length]
```

C#-style interpolated text is supported. Fully compile-time interpolation folds to a text constant. Runtime interpolation needs caller-selected fixed storage:

```stark
fn Ascii Label(i32[min max] score)
{
    stack Ascii label[64] = $"Score: {score}";
    return label;
}
```

Use explicit `System.Text` APIs when overflow, allocation failure, formatting failure, or encoding conversion should be returned as data instead of trapping.

## Standard Library

Import standard-library modules explicitly when it improves readability. The root `System` module re-exports the common public modules, while `System.Text`, `System.Testing`, and `System.Runtime.Buffer` are usually imported directly when needed.

Public modules:

- `System.BitOperations`: bit counting, zero counts, rotations, byte swaps, powers of two
- `System.Collections`: `List`, `Stack`, `Queue`, `RingQueue`, `Dictionary`, `LinkedList`
- `System.Console`: console reads/writes for text, slices, and byte buffers
- `System.FileSystem`: directories, entry information, existence/type checks, move/delete
- `System.IO` / `System.IO.File` / `System.IO.Path`: IO result types, owned files, path helpers
- `System.Math`: float math, `SinCos`, min/max/rounding, `XorShift32`
- `System.Memory`: dynamic-storage reserve/append/copy/move/fill helpers
- `System.Net` / `System.Net.Tcp`: network result types, IPv4 endpoints, TCP clients/listeners
- `System.Process`: process id and exit
- `System.Runtime.Buffer`: fixed and dynamic byte buffers
- `System.Testing`: simple assertion/status helpers
- `System.Text`: owned text, encoding conversion, parsing, formatting
- `System.Threading`: threads, joins, detach, yield, sleep

For exact public standard-library signatures, read [`references/standard-library-signatures.md`](references/standard-library-signatures.md). It is generated from `stdlib/src/System` and bundled with this skill.

## Bundled References

Use these bundled references when the task needs more detail while staying self-contained:

- [`references/syntax-quick-reference.md`](references/syntax-quick-reference.md): source structure, keywords, operators, ranges, switches, text, and callable syntax.
- [`references/borrower-recipes.md`](references/borrower-recipes.md): choosing `borrow`, `mut borrow`, `retborrow`, `storeborrow`, `frozen`, `const`, `out`, `init`, raw pointers, and memory contracts.
- [`references/callables-closures-reference.md`](references/callables-closures-reference.md): function items, `fnptr`, lambdas, inline closures, borrowed closures, heap closures, once closures, and thread entries.
- [`references/assembly-functions-reference.md`](references/assembly-functions-reference.md): `unsafe ffi asm(arch) fn`, operands, clobbers, target selection, supported types, and current lowering limits.
- [`references/project-manifest-reference.md`](references/project-manifest-reference.md): `Stark.toml`, `Stark.solution.toml`, project kinds, profiles, dependencies, native metadata, and commands.
- [`references/ffi-native-layout-reference.md`](references/ffi-native-layout-reference.md): FFI declarations, `export`, raw pointer regions, ABI-facing layout, enum tags, safe wrappers, and native package metadata.
- [`references/performance-cookbook.md`](references/performance-cookbook.md): source-level performance recipes for kernels, non-overlap, independent loops, raw regions, `const`, allocation, numeric policy, and benchmarks.
- [`references/diagnostics-guide.md`](references/diagnostics-guide.md): common diagnostic categories and source-level fixes.
- [`references/examples-cookbook.md`](references/examples-cookbook.md): portable embedded examples for common Stark patterns.
- [`references/standard-library-signatures.md`](references/standard-library-signatures.md): generated public standard-library module summaries and signatures.

## Projects And Solutions

Use `Stark.toml` for a project:

```toml
[project]
name = "app"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "app"

[dependencies]
stdlib = { path = "../stdlib" }

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Project kinds are `executable`, `library`, and `test`. A test project compiles to an executable and should return `0` for success.

Use `Stark.solution.toml` for multi-project repos:

```toml
[solution]
name = "Workspace"
members = ["app", "tests"]

[defaults]
build = ["app"]
run = "app"
test = ["tests"]

[aliases]
app = "app"
tests = "tests"
```

Everyday commands:

```bash
stark build
stark run
stark test
stark build app --release
```

Manifest discovery searches upward. The nearest `Stark.toml` runs in project mode; the nearest `Stark.solution.toml` runs in solution mode.

Native-backed packages keep native metadata in the package manifest:

```toml
[native]
sources = ["NativeShim.c"]
pkg-config = ["raylib"]

[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m"]
```

Machine-local paths belong in user config, such as `~/.config/stark/config.toml` or ignored `Stark.user.toml`:

```toml
[native.paths]
raylib-src = "/path/to/raylib/src"
```

Build outputs live under `.stark/build/dev`, `.stark/build/release`, `.stark/cache`, and `.stark/packages`.

## Style

Prefer surrounding code style when editing existing files. Defaults for new Stark code:

- modules, types, fields, records, functions, methods, globals, and constants: `PascalCase`
- parameters and locals: `camelCase`
- no `I` prefix for traits
- no leading `_`, `m_`, `s_`, `g_`, or casing tricks for visibility/storage
- 4 spaces, no tabs
- imports first, then `module`, blank line, then declarations
- preserve foreign FFI spellings exactly
- keep unsafe blocks small and audited
- prefer importing standard-library modules and using short names when unambiguous
- keep helpers private or `internal`; use `public` for Stark API; use `export` only for ABI
- Use Allman Braces

When in doubt, inspect nearby `.stark` files and keep edits consistent. Do not add wrappers, allocation, indirection, visibility, dynamic dispatch, or raw pointers for cosmetic reasons.
