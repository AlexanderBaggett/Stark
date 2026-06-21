# Stark Language Reference

The user facing Stark language. This document defines the source level contract: how Stark code is written, what constructs exist, and what behavior programmers can rely on.

Lower level compiler strategy and optimizer rationale live in [LanguageInternals.md](../Internals/LanguageInternals.md).

[Roadmap.md](../Internals/Roadmap.md) tracks milestone ordering and work sequencing.

## 1. Design Goals

Stark is a performance first systems language.

The priorities:

* speed and predictable low level behavior over convenience
* explicitness over hidden work
* restrictions that keep expensive behavior visible
* ownership and effect rules that make aliasing, mutation, and failure explicit
* a default preference for direct calls and minimal visibility

Stark is intentionally more restrictive than mainstream systems languages when that restriction produces better code.

## 2. Source File Structure

A Stark compilation unit:

```stark
import Some.Module
export import Another.Module
module Current.Module

// top-level declarations
```

Rules:

* each source file declares exactly one module
* imports appear before the `module` declaration
* the `module` declaration appears exactly once
* one source file equals one module
* `import` and `module` declarations do not end in semicolons
* wildcard imports are forbidden
* `export import` is the only re export form
* importing a module makes its visible top level declarations available by final name, so `import System.Collections` allows `List<T>` instead of `System.Collections.List<T>`

### 2.1 Comments

Comments are source trivia. They are ignored before parsing, type checking, and lowering, so commented-out Stark code has no semantic or generated-code effect.

Supported forms:

* line comments: `// comment text`
* block comments: `/* comment text */`
* C# style XML documentation line comments: `/// <summary>...</summary>`
* C# style XML documentation block comments: `/** <summary>...</summary> */`

Block comments do not nest.

```stark
module Demo

/// <summary>Returns the fixed answer.</summary>
finite law i32[0 max] Run()
{
    // stack i32[0 max] ignored = 99;
    /*
    return ignored;
    */
    return 42;
}
```

## 3. Top Level Declarations

The top level declaration categories:

* functions
* `struct` declarations
* `record` declarations
* `enum` declarations
* `trait` declarations
* `doctrine` declarations
* type alias declarations
* global constants
* global variables

## 4. Modules and Visibility

Three explicit visibility modifiers:

* `internal`
* `public`
* `export`

A top level declaration with no visibility modifier is **module private**.

The meanings:

* **module private**: visible only inside the current module
* `internal`: visible to other modules in the same package or library
* `public`: visible to downstream Stark source that imports the package
* `export`: a real binary symbol intended for FFI, runtime entrypoints, or stable binary boundaries

`public` and `export` are intentionally different:

* `public` is source visibility
* `export` is binary visibility

Visibility applies to top level declarations and to member functions inside `struct` and `record` bodies. Member functions inherit the visibility of their enclosing type unless explicitly overridden, and a member function may not be more visible than its enclosing type. `export` is the careful edge: an `export struct` or `export record` makes omitted member visibility `public`, not `export`, so binary visible member functions must write `export` explicitly.

Visibility does not apply to locals, statements, expressions, or fields.

### 4.1 Package Owned Native Dependencies

Interop packages can describe the native source files and native libraries they require so downstream users can build with ordinary Stark commands.

The current package author surface is CLI metadata:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --package-image-output dist/pkg/libRaylibStark.starkpkg.json \
  --native-source RaylibNative.c \
  --native-pkg-config raylib
```

The package records those native dependency declarations. A downstream executable that imports the package gathers the package owned native build metadata automatically.

When the native dependency is not available through `pkg-config`, the package can use explicit metadata:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --package-image-output dist/pkg/libRaylibStark.starkpkg.json \
  --native-source RaylibNative.c \
  --native-include-dir /path/to/raylib/src \
  --native-library-dir /path/to/raylib/src \
  --native-library raylib
```

If a named native library cannot be found during the final native link, the build reports the missing library and suggests installing it or adding its directory with `-L` or `--native-library-dir`.

If a package owned `pkg-config` dependency cannot be resolved, the build names the package and suggests installing the native package, setting `PKG_CONFIG_PATH`, or using explicit native metadata.

Native dependency declarations are for explicit FFI and package interop. They do not make those dependencies part of Stark's standard library runtime profile.

## 5. Functions

### 5.1 Function Kinds

Four source level function kinds:

* `fn`
* `finite`
* `law`
* `finite law`

The keyword order is fixed: `finite law`, not `law finite`.

The meanings:

* `fn`: general function form
* `finite`: guaranteed progress and guaranteed return
* `law`: pure, readonly, no visible side effects
* `finite law`: pure, plus guaranteed progress and return

### 5.2 Function Modifiers

The modifiers:

* `inline`
* `noinline`
* `inlinehint`
* `hot`
* `cold`
* `ffi`
* `strictfp`
* `static`
* `tail`

Rules:

* `inline`, `noinline`, and `inlinehint` are mutually exclusive
* `hot` and `cold` are mutually exclusive
* `ffi` marks a foreign facing function boundary, and may carry an explicit calling convention as `ffi(abi)` (see 13)
* `strictfp` selects strict IEEE style floating point semantics for the function
* `static` is only valid on member functions inside `struct` or `record` declarations
* `tail` marks a callable as tail-callable and permits guaranteed tail-transfer edges with `become`

Static member functions belong to the type rather than to a value. They are called through the type name and do not receive a `self` argument:

```stark
struct Thread
{
    static fn void Yield();
}

fn void Run()
{
    Thread.Yield();
}
```

Instance member functions are called through a value and use an explicit receiver parameter:

```stark
struct Counter
{
    i32[0 max] Value;

    finite law i32[0 max] Get(borrow Counter self)
    {
        return self.Value;
    }
}
```

### 5.3 Declarations and Bodies

Function declarations may appear with either:

* a block body
* a trailing `;`

Semicolon form is used for FFI functions and forward declarations.

Function parameters are normally written as `T name`. Parameter memory contracts may add a prefix before the type or a `where` clause after the parameter list.

Default argument syntax such as `fn i32 Add(i32 left = 1)` is not part of Stark.

**Definite return.** A function with a non-`void` return type must return on every
control-flow path; a body that control can fall out of is a compile error, not a
runtime failure. A path counts as returning when it ends in a `return`, an
`if`/`else` whose branches both return, an exhaustive `switch` (10.4) whose sections
all return, or an `infinite` loop that contains no `break`. `void` functions may fall
off the end (an implicit return).

### 5.3.1 Guaranteed Tail Calls

`tail` is a callable contract modifier. It composes with the ordinary function
kinds, so `tail fn`, `tail finite`, `tail law`, and `tail finite law` are all
valid when the function otherwise satisfies that kind's rules.

```stark
tail finite i64[min max] SumTo(i64[0 max] remaining, i64[min max] total)
{
    if (remaining == 0)
    {
        return total;
    }

    become SumTo(remaining - 1, total + remaining);
}
```

`become call(args);` is a terminating statement. It replaces the current stack
frame with the target call and guarantees that the accepted edge does not grow
the stack. This is a checked language contract, not an optimizer hint.

The rules:

* the enclosing function must be declared `tail`
* the expression after `become` must be a single direct, member, function-pointer,
  closure, trait, or dynamic-dispatch call
* the target must be tail-callable
* the `become` statement must be in true tail position, with no computation,
  conversion, drop, defer, cleanup, or ownership finalization left to run after
  the call
* the target result must match the enclosing function result exactly; `void`
  functions may only become calls that return `void`
* ordinary effect rules still apply, so a `tail law` function may only become a
  call that a `law` function may call
* ABI shapes that cannot support a guaranteed tail transfer are rejected

Function pointer and closure callable types carry the same contract:

```stark
fnptr<tail fn i32[min max](i32[min max])>
closure<tail law i32[min max](i32[min max])>
```

Self-recursive and mutually recursive `become` cycles are allowed. `finite`
remains a separate termination proof: it proves that the function returns, while
`tail` and `become` prove that the tail-transfer edge is stack-constant.

### 5.4 Parameter Memory Contracts

Function parameters support memory-separation and deep-immutability contracts.
These contracts are part of the function type and are checked at call sites.

Memory-backed parameters are non-overlapping by default. For ordinary `fn`, `finite`, `law`, and `finite law` declarations, every pair of parameters that describes reachable caller storage must be passed non-overlapping regions unless the callee says otherwise. This includes `borrow`, `borrow mut`, `retborrow`, `storeborrow`, slices, text views, `out`, `init`, bounded raw pointer regions, `rawptr`, and `rawmutptr`. Scalar value parameters and ordinary by-value owned aggregates do not create a user-facing non-overlap obligation just because the ABI may lower them indirectly.

The default makes this common shape a non-overlap contract without extra syntax:

```stark
fn void Add(
    borrow f32[] left,
    borrow f32[] right,
    borrow mut f32[] output)
{
    return;
}
```

The relational `where overlap(...)` form opts out for an intentional may-overlap relation. It removes the default non-overlap obligation for only the listed pair or group:

```stark
fn void MoveBytes(borrow u8[] source, borrow mut u8[] destination)
    where overlap(source, destination)
{
    return;
}
```

The relational `where same(...)` form requires the listed parameters to identify the same memory region. A safe call must prove same-region identity:

```stark
fn void CompareViewWithBacking(borrow u8[] view, borrow u8[] backing)
    where same(view, backing)
{
    return;
}
```

The relational `where disjoint(...)` form remains available for exact disjointness relations the parameter default cannot express, especially bounded subregions and disjointness inside an otherwise overlap-capable API:

```stark
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

Whole-parameter `disjoint` groups are redundant with the default for memory-backed Stark parameters and are rejected with a fix-it diagnostic. Remove parameter-prefix `disjoint` and whole-parameter `where disjoint(a, b)` clauses; keep `where disjoint(pointer[start, count], other[start, count])` for subregions the default cannot express. FFI and assembly declarations are the exception: they do not receive Stark's default non-overlap contract, so explicit whole-parameter `disjoint` remains the opt-in spelling for external ABI boundaries. `overlap(a, b, c)` and `same(a, b, c)` are pairwise within the listed group. Multiple clauses in the same `where` clause are separated with commas:

```stark
fn void ProcessPairs(
    borrow u8[] a,
    borrow u8[] b,
    borrow u8[] c,
    borrow mut u8[] d)
    where overlap(a, b), same(c, d)
{
    return;
}
```

These relations are not transitive. `where same(a, b), same(b, c)` does not prove `same(a, c)` unless that relation is also stated or separately proven, and `where overlap(a, b)` does not permit overlap between `a` and any unlisted parameter.

A memory contract is about memory ranges, not only root values. Two slices that point into the same allocation satisfy a non-overlap obligation when their element ranges do not overlap. Contract operands must be memory-backed parameters or raw pointer region expressions; scalar value parameters cannot carry a memory-region contract.

`where dead_on_return(name)` marks a memory-backed parameter's pointee storage as
unavailable to the caller after the call returns. Use it for destructive
destinations where the callee consumes, overwrites, or otherwise ends the old
pointee lifetime. Ordinary `out` and `init` parameters do not imply
`dead_on_return`: callers normally read initialized outputs after those calls.

```stark
fn bool Destroy(out u32[0 max] value) where dead_on_return(value)
{
    value = 0;
    return true;
}
```

The contract is part of callable type compatibility. In function pointer and
closure callable types, use synthetic argument names:

```stark
fnptr<fn bool(out u32[0 max]) where dead_on_return(arg0)>
```

A function that forwards a parameter to a `dead_on_return` callee must either
own that storage locally or declare the same `dead_on_return` contract on its
own parameter. This keeps the caller-visible lifetime end explicit across
wrapper and callback boundaries.

Raw pointer parameters may expose their bounded element region directly. The forms `rawptr<T>[count]` and `rawmutptr<T>[count]` are raw pointer parameters whose valid source region contains `count` contiguous elements of `T`:

```stark
fn void CopyBytes(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
{
    return;
}
```

The pointer value is still a raw pointer, but the function contract includes the region bound. A nonzero count requires a non-null pointer that is valid for every element in `0 <= index < count`; a zero-length region may use `null`. The bound expression is an integer expression over the function parameters and compile-time constants, and cyclic bounds are rejected.

Raw pointer region expressions use two-index slicing syntax inside memory contracts and disjoint checks. `pointer[start, count]` names the contiguous region beginning at `pointer + start` and containing `count` elements.

The expression `pointer[start, count]` is a memory-region expression, not an owning value. It is valid in `where disjoint(...)`, `if disjoint(...)`, and places where the language expects a bounded raw pointer region fact.

At a safe call site, the compiler must prove each memory relation. Default parameter pairs require non-overlap. `where same(...)` pairs require same-region identity. `where overlap(...)` pairs impose no non-overlap obligation for that relation. Passing the same memory region twice to default parameters, passing a whole object together with one of its fields, passing two indexed regions whose indexes are not proven separate, passing a call result or other expression whose memory root is not visible, or passing two raw pointer or slice variables whose regions have not been proven separate violates the default contract:

```stark
fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
{
    return;
}

fn void Bad(rawmutptr<i32[min max]> ptr)
{
    Touch(ptr, ptr); // STK3030: overlapping disjoint arguments
}

fn void MaybeBad(i32[0 2] i, i32[0 2] j)
{
    stack mut i32[min max][3] values =
    {
        1, 2, 3
    };
    Touch(&values[i], &values[j]); // STK3030 unless the indexes are proven separate
}

fn void Unknown(rawmutptr<i32[min max]> maybeLeft, rawmutptr<i32[min max]> maybeRight)
    where overlap(maybeLeft, maybeRight)
{
    Touch(maybeLeft, maybeRight); // STK3030: different pointer names are not a proof
}

fn rawmutptr<i32[min max]> Identity(rawmutptr<i32[min max]> ptr)
{
    return ptr;
}

fn void HiddenRoot(rawmutptr<i32[min max]> maybeLeft, rawmutptr<i32[min max]> maybeRight)
    where overlap(maybeLeft, maybeRight)
{
    Touch(Identity(maybeLeft), maybeRight); // STK3030: the left root is hidden behind a call
}
```

An ordinary `unsafe` block does not bypass parameter non-overlap. Trusted external separation must be written as a scoped unsafe assertion:

```stark
unsafe fn void ExternallySeparated(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
    where overlap(left, right)
{
    unsafe assume disjoint(left, right)
    {
        Touch(left, right);
    }
}
```

`unsafe assume disjoint(...)` introduces a scoped non-overlap promise for the nested statement without a runtime check. The assertion must name visible memory roots or representable subregions. It does not silence obvious same-root overlap, hidden call results, or integer-laundered pointers.

Inside an `unsafe fn` or an existing `unsafe { ... }` block, the leading `unsafe` is optional:

```stark
unsafe fn void AlreadyUnsafe(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
    where overlap(left, right)
{
    assume disjoint(left, right)
    {
        Touch(left, right);
    }
}
```

Distinct visible projections, non-overlapping index ranges, visible text slice ranges such as `text[0, 4]` and `text[4, 4]`, bounded raw pointer region expressions, exclusive mutable borrow roots, `out`/`init` destination roots, immutable slice/text views with visible backing storage, separately addressed local storage, declared parameter contracts, and true branches of `if disjoint(...)` may satisfy the contract. Local raw pointers, slice locals, text locals, and borrowed local views are not non-overlapping merely because they are separate declarations; local facts come from provenance. Pointer copies and simple casts preserve same-region identity.

```stark
struct Pair
{
    i32[min max] Left;
    i32[min max] Right;
}

fn void Fields(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
{
    return;
}

fn void Good(borrow mut Pair pair)
{
    Fields(&pair.Left, &pair.Right);
}

fn void Forward(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
{
    Fields(left, right);
}
```

A `const` parameter is a parameter whose reachable object graph is deeply immutable. It is stronger than ordinary readonly access and stronger than `frozen` borrow access because it requires permanent const provenance rather than only a call-scoped readonly view:

```stark
fn i32[0 max] Lookup(const Table table, i32[0 max] key)
{
    return table.Find(key);
}
```

Projections from a `const` parameter remain deeply readonly. Safe code may not derive a mutable borrow, mutable raw pointer, or mutation-capable alias from any reachable part of a const parameter graph.

`const` does not imply local non-overlap. Local const views can refer to the same immutable object graph. Memory-backed function parameters remain non-overlapping by default unless the callee declares `where overlap(...)` or `where same(...)`.

### 5.5 Function Items and Function Pointers

Stark's first class callable model starts with **function items**.

A function item is the callable value represented by a named function. It is not
a raw pointer by default, does not capture state, and can be called directly
unless the program explicitly stores it in a callable value.

```stark
fn i32[min max] Worker()
{
    return 0;
}

fn void Start()
{
    stack mut System.Threading.Thread worker = new(Worker);
}
```

Function items may be promoted to explicit function pointer values when a runtime pointer is required.

```stark
stack fnptr<fn i32[min max]()> entry = Worker;
stack i32[min max] result = entry();
```

Promotion to a function pointer is the point where the function becomes address taken. Ordinary function item use stays direct; indirect callable behavior is requested explicitly through a `fnptr` value.

Function pointer types carry the function kind in their signature:

```stark
fnptr<fn i32[min max](i32[min max])>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

The kind is part of the callable contract. `fnptr<fn ...>` is the general
form. `fnptr<finite ...>` may only hold callbacks that guarantee progress and
return. `fnptr<law ...>` may only hold callbacks that are pure/read-only and
have no visible side effects. `fnptr<finite law ...>` requires both sets of
guarantees.

A function pointer type may also carry an explicit FFI calling convention,
written as `ffi(abi)` immediately inside the angle brackets (after an optional
`unsafe`, before the function kind). The ABI is part of the function-pointer type
identity, so `fnptr<ffi(c) fn void()>` and `fnptr<ffi(stdcall) fn void()>` are
distinct, incompatible types (see 13). Closures do not carry an FFI ABI.

```stark
fnptr<ffi(c) fn i32[min max](rawptr<i8[min max]>)>
fnptr<unsafe ffi(win64) fn void(rawmutptr<i8[min max]>)>
```

Stronger function items can be used where a weaker function pointer is expected:

```stark
finite law i32[min max] Clamp(i32[min max] value)
{
    return value;
}

fn void Register()
{
    stack fnptr<fn i32[min max](i32[min max])> general = Clamp;
    stack fnptr<finite i32[min max](i32[min max])> bounded = Clamp;
    stack fnptr<law i32[min max](i32[min max])> pure = Clamp;
    stack fnptr<finite law i32[min max](i32[min max])> strict = Clamp;
    return;
}
```

A weaker function item cannot be promoted to a stronger function pointer. This
keeps higher-order APIs honest: a `law` function can call a
`fnptr<law ...>` callback without losing its own law guarantee, and a `finite`
function can call a `fnptr<finite ...>` callback without accepting a callback
that may fail to make progress. The function-pointer type carries those
requirements at every indirect call site.

`fnptr` values are non-null callable pointers. A function pointer must come from
a compatible function item or non-capturing lambda; `null` is not assignable to a
`fnptr`. Struct fields and fixed-array elements that contain function pointers
must be explicitly initialized when an aggregate initializer would otherwise
zero-fill them.

Function pointer types also carry memory-separation contracts for memory-backed
parameters. Because `fnptr` parameter lists do not name parameters, contract
clauses use synthetic names `arg0`, `arg1`, and so on. Memory-backed `fnptr`
parameters are non-overlapping by default; use `where overlap(arg0, arg1)` when
the indirect callee permits overlap, or `where same(arg0, arg1)` when the call
requires identical storage.
Bounded raw pointer parameters inside a `fnptr` use the same synthetic names in
their count expressions. For example, `rawptr<T>[arg1]` means the first callback
argument is valid for the element count supplied as the second callback
argument. The bound is part of the function-pointer contract and is preserved
through package images and indirect-call lowering.

```stark
fnptr<fn void(borrow mut Buffer, borrow mut Buffer) where overlap(arg0, arg1)>
fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>
fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>
```

An ordinary `fnptr<fn ...>` (and `fnptr<ffi(c) fn ...>` without `unsafe`) is a safe callable pointer, and an unsafe function item cannot be promoted into it, because that would hide the unsafe requirement from later calls. To carry an unsafe foreign callback, write the `unsafe` keyword inside the `fnptr` type (`fnptr<unsafe ffi(c) fn ...>`) and promote inside an unsafe context; calling it still requires unsafe (see 13.3). Otherwise, call unsafe functions directly inside an `unsafe` block, or expose a safe wrapper that checks the required conditions.

### 5.6 Lambdas and Capture Modes

Lambda syntax follows the C# arrow form:

```stark
stack fnptr<fn i32[min max](i32[min max])> square =
    (i32[min max] value) => value * value;

stack fnptr<fn i32[min max](rawmutptr<State>)> worker =
    (rawmutptr<State> state) =>
    {
        return Worker(state);
    };
```

A lambda with no capture list is non capturing. It may be used where a matching function pointer is expected.

Capturing lambdas use an explicit capture list. Capture is never implicit.
Capture lists are checked, but a lambda converted to `fnptr<...>` cannot capture
local state because a function pointer does not carry closure storage. Use a
named function item or pass captured state explicitly.

```stark
UseTransform(capture(copy scale, read table) (i32[0 max] index) =>
{
    return table[index] * scale;
});
```

The safe capture modes:

* `capture(copy x)`: copies a cheap copy value (bool, integer, float, raw pointer, function pointer, or readonly borrow) into the closure environment. Owned structs and owned text are not silently copied; use `move` or `read` instead.
* `capture(move x)`: moves ownership into the closure environment. The source binding is consumed.
* `capture(read x)`: captures readonly access to existing storage.
* `capture(mut x)`: captures exclusive mutable access to existing storage for the closure's lifetime.
* `capture(out x)`: captures a write only destination. The closure may write the destination but may not read the old value.
* `capture(init x)`: captures uninitialized destination storage that the closure must initialize before returning successfully.

`mut`, `out`, and `init` captures require `x` to be a writable binding such as a `mut` local or mutable destination. Immutable values may still be captured with modes such as `copy` or `read`.

Two capture modes are trusted operations and require an unsafe context:

```stark
capture(unsafe addr x)
capture(unsafe shared x)
```

* `capture(unsafe addr x)`: captures address or identity information without ordinary dereference authority. A low level escape hatch for tracking where a pointer came from and for interop.
* `capture(unsafe shared x)`: publishes a value or capability into a shared or concurrent domain that ordinary non shared Stark code cannot model by itself.

These modes are explicit because they weaken Stark's ordinary closed and non shared memory assumptions.

### 5.7 Closure Types

Closures are the capturing callable values. A closure signature reuses the
function pointer signature shape, but the storage form is visible in the type:

```stark
inline closure<fn void(mut borrow Ui)>
borrow closure<fn i32[min max](i32[min max])>
mut borrow closure<mut fn void(i32[min max])>
heap closure<fn void()>
heap closure<once fn Packet()>
```

`inline closure<...>` is a call-now parameter form, not runtime storage. It may
appear directly as a function parameter when the callee invokes the callback
during that operation. It cannot be stored in a local or field, returned, placed
in an array, nested inside `fnptr` or another runtime callable type, or
converted to `fnptr`.

```stark
inline fn i32[min max] ApplyInline(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] RunInline(i32[min max] offset)
{
    return ApplyInline(
        41,
        capture(copy offset) (i32[min max] value) => value + offset);
}
```

`borrow closure<...>` is a non escaping callable view. Captured stack storage
must outlive the borrowed closure view, and the view cannot be returned or
stored unless the API uses an explicit stored borrow form.

```stark
fn i32[min max] ApplyBorrow(
    borrow closure<fn i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}
```

`heap closure<...>` owns a heap allocated environment. It is the form for
stored, returned, or retained callbacks. Heap closures may capture copied values
and moved owned values; they reject ordinary stack borrows unless the program
uses an explicit safe stored-borrow or unsafe shared capability.

```stark
fn heap closure<fn i32[min max](i32[min max])> MakeAdder(i32[min max] offset)
{
    return heap capture(copy offset) (i32[min max] value) => value + offset;
}
```

Closure call capability is part of the type:

* no marker: the closure may be called repeatedly without mutating or consuming
  its environment.
* `mut`: calling the closure may mutate the environment and requires mutable
  access to the closure value, except for inline closures where there is no
  runtime closure value.
* `once`: calling the closure consumes it. A second call is a use after move.

## 6. Types

### 6.1 Builtin Types

The builtin type families:

* `bool`
* `ascii`
* `unicode`
* `Ascii`
* `Unicode`
* signed integer widths: `i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`
* unsigned integer widths: `u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`, `u384`, `u512`, `u768`, `u1024`
* floating point widths: `f16`, `f32`, `f64`, `f80`, `f128`

Examples:

* `u8[0 max]`
* `i32[min max]`
* `i64[min max]`
* `u128[0 max]`
* `f16`, `f32`, `f64`, `f80`, `f128`

Integer source types are always written as explicit ranged forms over one of the supported widths.

```stark
u8[0 max]
i32[min max]
u64[0 max]
u8[min 127]
u48[10 ** 2 10 ** 10]
u32[1024 * 1024 1024 * 1024 * 1024]
```

Within an integer range, `min` and `max` are type relative endpoint names. For signed `iN` ranges they mean the signed minimum and maximum for that width. For unsigned `uN` ranges they mean `0` and `2 ** N - 1`.

Unsigned integer widths are real integer types, not aliases for signed integers with non negative ranges. For `uN`, `min` is `0` and `max` is `2 ** N - 1`. Negative endpoints and endpoints outside that width are rejected.

Stark rejects non negative signed ranges and unnecessarily wide integer range storage by default. For example, write `u8[0 max]` instead of `i32[0 255]`, and use a narrower supported width when the declared range fits. `ffi` signatures and declarations annotated with `[Platform]` may preserve signedness and width when they mirror an external platform ABI. When an endpoint is the full minimum or maximum for its base integer type, use the type relative shorthand: signed lower endpoints use `min`, all full-width upper endpoints use `max`, and unsigned lower endpoints may use either `0` or `min`. Stylistically we prefer `[min max]` over exponentiation for the same values. We prefer exponentiation for values > 1024 and values < -1024.

Range endpoints support compile time integer arithmetic over literals and type relative endpoint names. Supported endpoint operators: `+`, `-`, `*`, `/`, `%`, `**`, unary `-`, and parentheses. Endpoint arithmetic is checked during compile time evaluation.

Bare width names such as `i32` are convenient family labels in prose, but they are not the full Stark integer source form by themselves. The source level type must carry an explicit range.

Scalar integer constants are the exception: they should be declared without an explicit integer type. A `const` integer is compile time known and cannot change, so Stark derives both the exact single value range and the smallest supported storage width that can hold it. If a scalar integer const does name a type, it uses only the bare width form such as `i8` or `i32`; ranged forms such as `i32[min max]` are for runtime integer values, not scalar constants.

An explicit scalar integer const width or sign must also be canonical. For example, `const u8 Count = 80;` is accepted, while `const i32 Count = 80;` is rejected with a suggestion to use `u8` or omit the explicit integer type.

```stark
const PageSize = 2 ** 12;      // i16 storage
const BoardWidth = 80;      // u8 storage
const BigCount = 2 ** 16;     // u24 storage
const i8 SmallCount = 80;   // accepted explicit width
const i32 WideCount = 80;   // compile-time error; use u8 or omit the explicit type
```

For floating point constants, an unsuffixed decimal such as `80.0` is `f64`. Use an `f` suffix for `f32`, as in `80.0f`.

Floating point source types use the bare width form directly. Stark supports `f16`, `f32`, `f64`, `f80`, and `f128`.

`void` is not a first class Stark value type. It is valid only as a function return type.

### 6.2 Aggregates and Views

The aggregate and view forms:

* fixed arrays: `T[N]`
* slices: `T[]`
* dynamic owned storage: `dynamic T`
* named aggregates through `struct` and `record`
* named variant families through `enum`

Fixed arrays are owning aggregate values.

Slices are non owning views. A slice does not materialize or own backing storage; it refers to storage established elsewhere.

`dynamic T` is owned, dynamically sized, capacity-bearing storage for elements of `T`. A dynamic value owns its backing allocation, has a capacity, and may contain both initialized elements and spare uninitialized slots. Dynamic storage is the safe language primitive for growable collections, owned text builders, and other data structures that need `Vec`-style spare capacity without raw pointer storage.

Dynamic storage is a value type, not a local storage class. The local or field that owns the dynamic header still uses ordinary placement such as `stack`, `heap`, or a struct field. The dynamic backing storage is managed by the dynamic value.

```stark
struct IntList
{
    dynamic i32[0 max] Items;
}

fn void Push(mut borrow IntList self, i32[0 max] value)
{
    if (self.Items.Length == self.Items.Capacity)
    {
        self.Items.Reserve(1);
    }

    init self.Items[self.Items.Length] = value;
}
```

`Length` is the initialized element count. `Capacity` is the number of element slots currently available in the backing allocation. `Reserve(additional)` ensures that at least `additional` spare slots exist after the initialized prefix. It preserves initialized elements, may grow the backing allocation, and traps on capacity overflow or allocation failure rather than returning a nullable raw pointer.

`TryReserve(additional)` has the same growth contract, but returns `bool` instead of trapping for capacity overflow, target-size overflow, or allocation failure. It returns `true` when the existing capacity is already sufficient or the grow succeeds, and `false` when the dynamic value is left unchanged. Library APIs that report allocation status use `TryReserve` to keep failure explicit without exposing raw pointers.

`TryReserveCapacity(targetCapacity)` is the exact-capacity fallible form. It
ensures that `Capacity >= targetCapacity`, returns `true` if the current capacity
is already enough or growth succeeds, and returns `false` without changing the
dynamic value on capacity overflow, allocation-size overflow, or allocation
failure.

An `init` assignment into dynamic storage extends the dense initialized prefix. Direct element initialization targets the current tail, normally with `items.Length`; compile-time constant slots are accepted when the preceding slots are visibly already initialized. Initialization views backed by dynamic storage are initialized in ascending slot order.

Sparse initialized-slot proofs are explicit unsafe proof boundaries. Inside an `unsafe` block, code may assert that a dynamic slot or initialization-view slot is initialized even when that fact is not visible from the dense prefix. The proof applies only inside that unsafe boundary; after a sparse proof, later safe code treats the dynamic initialized prefix as unknown until it re-establishes an ordinary dense-prefix proof.

```stark
fn i32[0 max] ReadOccupied(dynamic i32[0 max] values, i32[0 max] index)
{
    unsafe
    {
        return values[index];
    }
}
```

`MoveLast()` moves the last initialized element out of dynamic storage, decrements `Length`, and leaves the former tail slot spare. It traps if `Length` is zero. This is the safe dense-prefix pop operation for dynamic storage; it keeps initialized elements as the contiguous range `0..Length`.

`MoveAt(index)` moves the initialized element at `index`, shifts later initialized elements left by one slot, decrements `Length`, and leaves the old tail slot spare. It traps when `index >= Length`. This is the safe dense-prefix removal operation for queues, ordered buffers, and collection internals that need front or middle removal without spelling raw pointers.

```stark
fn i32[0 max] Pop(mut borrow IntList self)
{
    return self.Items.MoveLast();
}

fn i32[0 max] RemoveFirst(mut borrow IntList self)
{
    return self.Items.MoveAt(0);
}
```

The initialized part of dynamic storage can be viewed as a normal slice:

```stark
fn retborrow i32[0 max][] AsSlice(borrow IntList self)
{
    return self.Items[0, self.Items.Length];
}
```

The spare part can be viewed as an initialization destination:

```stark
fn bool AppendDefaults(mut borrow IntList self, i64[0 max] count)
{
    if (!self.Items.TryReserve(count))
    {
        return false;
    }

    for willexit (stack mut i64[0 max] index = 0; index < count; index += 1)
    {
        init self.Items[self.Items.Length] = 0;
    }

    return true;
}
```

`dynamic T` has no implicit public raw pointer. Safe code accesses it through initialized element views, initialization views, tail moves, and explicit capacity operations.

When a `dynamic T` owner is destroyed, Stark drops exactly the initialized prefix and then releases the backing allocation. Spare capacity is uninitialized memory and is skipped.

### 6.3 Type Qualifiers

The qualifiers:

* `borrow`
* `retborrow`
* `storeborrow`
* `frozen`
* `shared`
* `const`
* `out`
* `init`
* `mut`

These are part of the type model, not local syntax sugar.

`const` is valid on function parameters and means the parameter refers to a deeply immutable reachable object graph. Top-level `const` declarations use the same deep immutability contract for global objects.

`init T` is a write-only initialization destination for a single `T`. `init T[]` is a write-only initialization destination for a contiguous region of `T` slots. Code may write to an `init` destination, but may not read its previous contents. Assigning with `init` constructs the value in that slot and marks it initialized for the surrounding control-flow proof.

```stark
fn void Fill(init i32[0 max][] destination, i32[0 max] value)
{
    for willexit independent (stack mut i64[0 max] index = 0; index < destination.Length; index += 1)
    {
        init destination[index] = value;
    }
}
```

`MoveLast()` transfers the tail initialized value and marks that slot uninitialized by decreasing the dynamic length. Spare uninitialized slots are never read by safe code.

Inside an `unsafe` sparse proof boundary, non-tail dynamic moves are permitted when the data structure's own invariants guarantee that the moved slot is initialized and that any hole is repaired, skipped by a valid sparse drop strategy, or otherwise never observed as initialized. Safe code cannot rely on that proof after the boundary.

### 6.4 Raw Pointers

The raw pointer forms:

* `rawptr<T>`
* `rawmutptr<T>`

Safe Stark code has no null references and no nullable borrows.

`null` exists only in the raw and FFI domain. A Stark program may compare raw pointers against `null` and may store `null` in raw pointer storage, but may not assign `null` to safe values or borrows.

Raw pointers may carry an explicit element bound in parameter positions and memory-region expressions:

```stark
fn bool Fill(
    i64[0 max] length,
    rawmutptr<i32[min max]>[length] destination,
    i32[min max] value)
{
    return true;
}
```

The bounded form does not turn the pointer into an owning value and does not make pointer arithmetic safe outside the stated region. It gives the compiler and type checker a concrete contiguous region for nullability, bounds, aliasing, and loop-dependence reasoning.

An unsafe raw slice construction materializes an ordinary slice view from a raw pointer region:

```stark
fn void Copy(
    i64[0 max] length,
    rawptr<i8[min max]>[length] source,
    rawmutptr<i8[min max]>[length] destination)
    where disjoint(source[0, length], destination[0, length])
{
    unsafe
    {
        stack i8[min max][] sourceView = slice(source, length);
        stack mut i8[min max][] destinationView = slice(destination, length);
        for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
        {
            destinationView[index] = sourceView[index];
        }
    }
}
```

`slice(pointer, count)` is unsafe because the caller asserts the raw pointer is valid for `count` elements and that the requested mutability matches the pointer provenance. Once the slice exists, ordinary slice rules apply: bounds, mutability, disjoint contracts, const provenance, and `independent` loop validation all use the slice view.

### 6.5 Generic Parameters and Type Aliases

Generic type parameters may appear on functions, `struct` declarations, `record` declarations, `enum` declarations, `trait` declarations, and `doctrine` declarations.

Generic parameters participate in name resolution and type substitution.

#### Trait bounds

A generic type parameter may be constrained to implement a trait with a `where` clause:

```stark
finite law i32[min max] TotalWidth<T>(borrow T left, borrow T right) where T: Drawable
{
    return left.Width() + right.Width();
}
```

A `where T: Trait` bound makes the trait's methods callable on `T` inside the body (see 8.5) and requires every concrete type argument to implement the trait; passing a non-conforming type is a compile error. Bounds are resolved statically — each instantiation monomorphizes the bounded calls to direct calls on the concrete type, with no runtime dispatch. A parameter may carry several bounds (`where T: A, B`), and multiple parameters may be constrained with separate `where` clauses.

Type aliases introduce alternate names for existing types:

```stark
alias Byte = i8;
alias BufferView<T> = borrow T[];
```

A type alias does not by itself create a distinct runtime type or ABI identity. The declaration keyword is `alias`. Like other top level declarations, aliases may be module private, `internal`, `public`, or `export`. `public` and `export` aliases are published as part of the package facing Stark surface.

Implementation details are described in [LanguageInternals.md](../Internals/LanguageInternals.md).

### 6.6 Compile-Time Value Parameters

A generic parameter list may contain ordinary type parameters and typed
compile-time value parameters. Stark spells const generics with `comptime`
because `const` already means deep interior immutability:

```stark
finite law u8[0 max] Length<T, comptime u8[1 8] N>(borrow T[N] values)
{
    return N;
}
```

A `comptime` parameter is written `comptime Type Name`, where `Type` is a ranged
integer type. Current compiler support covers range-typed integer `comptime`
parameters used as fixed-array lengths (`T[N]`) and inferred from concrete
fixed-array arguments. The parameter participates in generic identity, so two
instantiations with different `comptime` values are different specializations.

Explicit `comptime` value arguments are written as literal integers in the
argument list, after any type arguments. Use `comptime N` to forward an enclosing
`comptime` value parameter:

```stark
stack i32[min max][3] values = { 1, 2, 3 };
stack u8[0 max] size = Length<i32[min max], 3>(values);
```

`comptime` value parameters, their symbolic values, and imported generic template
substitutions are preserved in package images.

## 7. Ownership, Borrowing, and Lifetimes

Safe Stark is ownership based. There is no garbage collection.

The main rules:

* values are owned by default
* moves transfer ownership
* moved values cannot be used again
* owned values are dropped automatically at scope exit
* assignment to an initialized owned place drops the previous value first
* parameters owned by the callee are dropped at function exit unless moved out
* safe borrows are non owning and non null
* raw pointers are the only null capable pointer forms
* safe code cannot use `forget` style escape hatches

### 7.1 Copyable Types

Move semantics only apply to types that own something. A named type is
**copyable** when copying its bytes is safe and creates no drop obligation,
derived structurally:

* scalars (`bool`, the integer widths, the floating point widths), raw pointers, function pointers, and the `ascii` / `unicode` text views are copyable
* a slice (`T[]`) is copyable — it is a non-owning `{ptr, len}` view, so copying it is byte-safe regardless of the element type (the referent's lifetime is governed separately by borrow checking)
* an `enum` is copyable when every variant is a unit variant or carries only copyable fields, and it has no destructor
* a `struct` or `record` is copyable when every field is copyable and it has no destructor
* a fixed array `T[N]` is copyable when its element type is copyable
* a type with a destructor is never copyable
* anything owning — `dynamic` storage, the `Ascii` / `Unicode` owning text containers, heap closures, heap `dyn` trait objects — is never copyable

Reads of copyable values out of fields, indexed places, and locals are
copies, not moves: the source stays usable, and accessors can return the
value directly.

```stark
struct Token
{
    TokenKind Kind;
    u64[0 2 ** 63 - 1] Start;
}

public finite law TokenKind KindAt(borrow TokenStream self, u64[0 2 ** 63 - 1] index)
{
    if (index >= self.Tokens.Length)
    {
        return TokenKind.EndOfFile;
    }

    return self.Tokens[index].Kind;    // a copy; the table is untouched
}
```

Concrete generic instantiations classify through their substituted payloads
(`Option<TokenKind>` is copyable; `Option<OwnedAscii>` is not). Uninstantiated
generic templates are conservatively move-only inside their own bodies.

To force a scalar-only type to keep move semantics — unique handles,
capability tokens — give it an empty destructor:

```stark
struct FileHandle
{
    i32[min max] Descriptor;

    drop
    {
    }
}
```

Generic code that requires copyability states it with the law-predicate
spelling used by `Transferable` and `Shareable`:

```stark
fn bool Duplicate<T>(T value)
    where Copyable(T)
{
    stack T first = value;
    stack T second = value;
    return true;
}
```

Call sites with concrete type arguments are checked structurally; a generic
caller forwards the requirement by declaring the same `where Copyable(T)`.
`Copyable` is purely structural — it cannot be granted or denied with
attributes.

A type that is meant to stay copyable can assert it at the definition, so
adding an owning field or a destructor later errors at the type instead of
at every downstream copy site:

```stark
[Copyable]
enum TokenKind
{
    EndOfFile,
    Identifier,
}
```

### 7.2 Initialized-Read Proofs For Dynamic Storage

`dynamic T` storage keeps a dense initialized prefix: the first `Length`
slots are initialized, everything at `Length` and beyond is spare capacity.
Reading a slot requires a compile-time proof that it is initialized.

A strict comparison against the storage's `Length` proves reads on the path
it dominates — in any function, for direct reads, whole-value copies, and
field projections through the slot:

```stark
finite law TokenKind KindAt(borrow TokenStream self, u64[0 2 ** 63 - 1] index)
{
    if (index >= self.Tokens.Length)
    {
        return TokenKind.EndOfFile;
    }

    return self.Tokens[index].Kind;    // proven: the guard above returned
}
```

The accepted forms are `index < storage.Length` (and the mirrored
`storage.Length > index`) in a positive `if` branch or a loop condition, the
negated `index >= storage.Length` guard whose branch always returns, breaks,
or continues, and `&&` conjunctions of those. The guard and the read must
spell the index and the storage path with the same source text; bind a local
first when the index is computed. The proof is invalidated by any write to
something it mentions: assigning or compound-assigning the index variable,
passing the storage's owner by `mut borrow`, or shadowing a name. Non-strict
`<=` comparisons prove nothing.

An equality guard against a constant length proves the corresponding *constant*
indices. `if (storage.Length == k) { ... }` proves `storage[0]` through
`storage[k - 1]` inside the branch, and `if (storage.Length != k) { return; }`
proves them on the path that continues; the non-empty check
`if (storage.Length != 0)` proves `storage[0]`. A variable index still needs a
`<` / `>=` comparison guard or a `where` value contract.

A summed index is proven by combining two facts. `storage[start + index]` is
proven from a strict bound on one addend (`index < length`) together with a
matching sum fact (`start + length <= storage.Length`, from either a contract or
a spelled guard); exactly one of the two facts must be strict. This is sound
because overflow is illegal (11.6), so `start + index` cannot wrap below the
proven bound. The summands are matched by their top-level `+` split, and both
operand orders are tried.

Genuinely sparse structures — hash slots, free lists, parent links — where
initialization is an invariant the type system cannot see keep the explicit
`unsafe { }` sparse initialized-slot proof. Moving a whole value out of a
slot is a separate concern from reading it: non-tail slot moves stay
restricted to `MoveLast()` or an explicit sparse proof so the dense prefix
never acquires holes.

### 7.3 Value Contracts

A function can move the proof obligation to its callers with a comparison
in its `where` clause. The comparison joins the same clause list as the
memory contracts, using `<`, `>`, `<=`, or `>=` over parameters and paths
through them:

```stark
finite law Token TokenAtProven(borrow TokenStream self, u64[0 2 ** 63 - 1] index)
    where index < self.Tokens.Length
{
    return self.Tokens[index];    // proven by the contract
}
```

Inside the function, a strict `< storage.Length` contract proves the body's
reads exactly like a guard would. At every call site the contract is
re-spelled with the actual receiver and arguments, and must be proven by
one of:

* a dominating comparison the caller already made (`if (index >= stream.Tokens.Length) { return ...; }` proves the negation on the path that continues)
* the caller's own matching `where` contract, forwarding the obligation outward
* constant arguments that satisfy the comparison outright

An unproven call site is a compile error that names both the declared
contract and the obligation as spelled at that call site. Comparisons match
by spelling: the guard, the contract, and the call must write the same
expressions for the same things (bind a local first when an argument is
computed). Mirrored forms (`a < b` and `b > a`) are recognized as the same
fact.

Contracts over module-internal fields are only provable inside that module —
public contracted functions should phrase their contracts over their public
surface, or keep a guarded total wrapper public and the contracted core
internal, as the lexer does with `TokenAt` / `TokenAtProven`.

The borrow classes:

* `borrow T`: cannot be stored or returned
* `retborrow T`: may escape only through the return value
* `storeborrow T`: may be stored or otherwise escape

The access qualifiers:

* `frozen T`: deeply immutable for the lifetime of the borrow
* `shared T`: explicit shared access domain

The deep-const parameter form is:

* `const T`: deeply immutable reachable object graph with const provenance

The memory-separation forms are:

* `disjoint T name`: external-boundary opt-in disjointness for FFI/asm parameters
* `where disjoint(a[start, count], b[start, count])`: relational disjointness between subregions or computed memory regions

`disjoint` means the named memory regions do not overlap. `const` means the reachable memory cannot be mutated through safe Stark code. The two contracts are independent; immutable memory can still alias another immutable view.

Destruction is intentionally restricted:

* trivial POD style destruction is the default
* safe destructors do not panic, synchronize, or allocate

For the full borrowing model and design rationale, see [BorrowerSystem.md](./BorrowerSystem.md).

## 8. Data Declarations

### 8.1 Structs

`struct` is the primary named aggregate form for ordinary data with associated methods and constructors.

`struct` declarations do not support inheritance.

### 8.2 Records

`record` is the data oriented named aggregate form.

`record` declarations do not support inheritance.

### 8.2.1 Recursive Inline Layout

A `struct`, `record`, or `enum` payload may not contain itself through only
by-value inline storage. The compiler rejects direct and mutual inline layout
cycles because such a type would have infinite size.

```stark
struct Bad
{
    Bad Next; // error: recursive inline layout
}

struct AlsoBad
{
    Wrapper<AlsoBad> Value; // error if Wrapper<T> stores T inline
}

enum List
{
    Empty,
    Cons(List), // error: recursive inline enum payload
}
```

Use an explicit indirection or out-of-line owner for recursive data:

```stark
struct Node
{
    rawmutptr<Node> Next;      // explicit raw-pointer indirection
    dynamic Node Children;     // owned out-of-line element storage
}
```

`dynamic T`, slices, borrows, raw pointers, function pointers, closures, and
trait-object handles have fixed-size stored representations and therefore break
the inline layout chain. The element or pointee type is still type checked; it
is simply not stored inline inside the containing aggregate.

### 8.3 Destructors

`struct` and `record` declarations may declare one destructor block.

Readonly form. `PlatformClose` is a placeholder for a non-fallible platform
cleanup helper supplied by the type:

```stark
drop
{
    if (!self.Closed)
    {
        PlatformClose(self.Handle);
    }
}
```

Mutable form:

```stark
mut drop
{
    self.Ptr = null;
    self.Closed = true;
}
```

Destructor blocks have these properties:

* not ordinary functions or methods
* no name, return type, parameter list, or visibility
* run automatically when an owned value is dropped
* `self` is implicit inside the block
* may not use `return`
* only `struct` and `record` bodies may declare them
* a type may declare at most one destructor block

The two forms differ in what they may do to `self`:

* `drop { ... }`: `self` is readonly. The destructor may inspect fields and perform cleanup work, but may not assign to `self` or to any field reachable through `self`.
* `mut drop { ... }`: `self` is mutable. For deliberate state rewrites during destruction such as disarming raw resource state before the final field drop sequence.

If a destructor is declared as `mut drop` but does not actually mutate `self`, the compiler warns and recommends the plain `drop` form instead.

Destructors are restricted:

* they do not panic
* they do not synchronize
* they do not allocate
* explicit teardown APIs such as `Close` are the right place for fallible cleanup and user controlled ordering

After the destructor block runs, ordinary field destruction still proceeds.

### 8.4 Enums

`enum` declares a closed Rust style enum family.

Enum cases may be:

* unit like: `End`
* tuple like: `Integer(i32)`
* named field: `Move { X: i32, Y: i32 }`

Example:

```stark
enum Token
{
    End,
    Integer(i32),
    Move
    {
        X: i32, Y: i32
    },
}
```

Value level enum construction uses `.` qualification from the enum type:

```stark
stack Token a = Token.End;
stack Token b = Token.Integer(5);
stack Token c = Token.Move
{
    X: 1, Y: 2
};
```

Standard library types such as `Option<T>` or `Result<T, E>`, when provided, are ordinary enums rather than compiler privileged forms. A two-variant enum may mark its variants with the innate `[Ok]`/`[Err]` role attributes, which makes it propagatable with the `try` expression, and an error enum can mark a variant with `from` to declare how `try` absorbs a lower-layer error (see 10.5).

Pattern matching uses the same case qualification:

```stark
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
```

For ownership:

* directly capturing a move only enum payload with `var` moves that payload out of the matched enum value
* after such a match, the matched enum value must be reinitialized before later use or scope exit drop
* matching through `_` or through nested scalar only subpatterns does not by itself move the whole enum value
* enum destruction only applies to the active case payload that requires drop; unit like and copy only cases do not create enum drop work by themselves

The default enum runtime contract is a direct tag representation:

* every enum value carries a discriminant and exactly one active payload
* enum construction selects one case and initializes only that case's payload
* enum matching tests the discriminant and then projects fields from the matched active case
* destruction drops only the active case payload

Default enum layout is not a stable FFI contract:

* niche based enum packing is not part of the current language contract
* explicit enum `repr` or ABI controls are not part of the current language surface
* Stark enums do not cross `ffi` or `export` boundaries

### 8.5 Traits

`trait` declares a named behavior contract: a set of method requirements a type can implement. Traits do not imply class style inheritance.

#### Implementing a trait

A `struct` or `record` implements traits with a base list and provides the methods inline:

```stark
trait Drawable
{
    finite law i32[min max] Width(borrow Self self);

    finite law i32[min max] DoubledWidth(borrow Self self)
    {
        return self.Width() * 2;
    }
}

struct Button : Drawable
{
    i32[min max] W;

    finite law i32[min max] Width(borrow Button self)
    {
        return self.W;
    }
}
```

`Self` names the implementing type. A trait method's receiver is spelled like any other receiver — `borrow Self self`, `mut borrow Self self` — and the implementation writes the concrete type (`borrow Button self`), exactly like an ordinary member method. Only traits may appear in a base list; "inheriting" from a `struct`, `record`, `enum`, or `doctrine` is rejected.

#### Required and default methods

* a trait method with a `;` body is **required**: every implementer must provide it
* a trait method with a `{ ... }` body is a **default**: implementers may use it as-is or override it

A type satisfies a trait when it provides every required method with a compatible signature: matching parameter count, matching parameter and return types (with `Self` resolved to the implementing type), and a function kind at least as strong as the trait's, so `law` and `finite` obligations are preserved. A missing required method or an incompatible signature is a compile error. Default method bodies may call other trait methods on `self`.

#### Calling trait methods

Trait methods are called on values, not through the trait name:

* on a conforming concrete value: `button.Width()`
* on a generic type parameter bounded by the trait (see 6.5): `value.Width()` inside a function declaring `where T: Drawable`

Every such call is resolved statically and monomorphized to a **direct call** — an overridden method dispatches to the override, and a not-overridden default dispatches to the default body specialized for the concrete type. There is no vtable, no runtime indirection, and no hidden dispatch; the trait abstraction is erased at compile time. This is the default, zero-indirection tier.

A plain `trait` is compile-time-only: it has no trait-object values, its name is rejected in value positions, and a trait method cannot be invoked through the trait name (`Drawable.Width(x)` is an error). Runtime dispatch is opt-in per trait, with `dyn trait` (below).

#### Associated types

A trait, doctrine, `struct`, or `record` body may declare associated type aliases. `alias Item;` is a *required* associated type each implementer must define; `alias Item = i32[min max];` provides a concrete or default associated type. Signatures refer to one through `Self.Item` in a trait, or `T.Item` through a bounded generic:

```stark
trait Container
{
    alias Item;

    finite law Self.Item First(borrow Self self);
}

struct IntBox : Container
{
    alias Item = i32[min max];

    i32[min max] Value;

    finite law i32[min max] First(borrow IntBox self)
    {
        return self.Value;
    }
}
```

Conformance requires every required associated alias to be defined and every signature to match after `Self`, trait type arguments, and associated types are substituted. A `dyn trait` currently may not declare associated types.

#### Dynamic dispatch with `dyn` trait objects

Sometimes the concrete type genuinely is not known until run time. A trait opts into runtime dispatch by being declared `dyn trait`; the cost is disclosed at every use by spelling `dyn` in the type.

```stark
dyn trait Shape
{
    finite law i32[min max] Area(borrow Self self);
}

struct Square : Shape { i32[min max] Side;  finite law i32[min max] Area(borrow Square self) { return self.Side * self.Side; } }
struct Box    : Shape { i32[min max] W; i32[min max] H; finite law i32[min max] Area(borrow Box self) { return self.W * self.H; } }

// Dispatches dynamically: the concrete type is erased behind the trait object.
finite law i32[min max] AreaOf(borrow dyn Shape shape)
{
    return shape.Area();
}
```

A **trait object** is a two-word fat pointer — a data pointer plus a pointer to a per-implementing-type vtable. It is spelled with a storage prefix that discloses its cost:

* `borrow dyn Shape` / `mut borrow dyn Shape` — a non-owning fat *view*: a borrow of some value plus its vtable. **No allocation.** The `mut` form permits calling `mut borrow Self` methods.
* `heap dyn Shape` — an *owning*, heap-boxed trait object: the value is moved into a heap box the trait object owns. **The only allocating form.** At scope exit it drops the boxed value (through the vtable's drop slot) and frees the box. The `heap` here is the dyn type's storage prefix, parallel to `heap closure`; the local's own storage class (e.g. `stack`) is written separately, as in `stack heap dyn Shape`.

A conforming concrete value coerces implicitly into a `dyn`-typed slot — the visible `dyn` in the slot's type is the disclosure that a trait object is being formed:

```stark
stack Square square = new Square() { Side = 4 };
stack borrow dyn Shape view = square;                  // borrowed fat view; no allocation
return view.Area();                                    // dynamic call through the vtable

stack heap dyn Shape owned = new Square() { Side = 7 };  // boxes the value on the heap; owned
return owned.Area();                                    // dropped + freed at scope exit
```

A call on a trait object (`view.Area()`) lowers to a single **indirect call** through the vtable slot. Crucially, the method's effect contract survives erasure: a `law` method called through `dyn` is still pure and a `finite` method still terminates, because the vtable slot carries the function kind. Dynamic dispatch erases the body, never the cost contract.

Only a `dyn trait` can form a trait object (`dyn` over a plain `trait` is an error, with guidance to add `dyn` or use an enum). To be `dyn`, every instance method must be **object-safe**: its receiver is `borrow Self`/`mut borrow Self`, it has no method-level generic parameters, and it does not pass or return `Self` by value. `static` (no-self) members are allowed but are not callable through the trait object.

The gradient of dispatch costs is therefore explicit in the source: an `enum` + `switch` (zero indirection) → static trait calls / `where T: Trait` (zero indirection, monomorphized) → a borrowed `dyn` trait object (one disclosed indirection, no allocation) → an owning `heap dyn` trait object (one indirection plus one disclosed allocation). Every step up in flexibility is visible in the type.

### 8.6 Doctrines

`doctrine` declares a named bundle of `law` functions and related constraints.

Doctrines have these properties:

* no owned identity
* no owned data
* no heap allocation
* no environment capture
* members may be referenced directly through their qualified doctrine name
* members are called directly by name

Doctrine members may carry default `law` / `finite law` bodies, and doctrine bodies may declare associated type aliases the same way traits do (see 8.5).

### 8.7 Layout Control Attributes

By default Stark chooses field order and padding for best layout. An FFI-facing `struct` or `record` that must match a C ABI opts into C-compatible layout with attributes:

* `[StructLayout(C)]`: lay fields out in declaration order with C padding rules
* `[StructLayout(Explicit)]`: place every field at an explicit byte offset
* `[Pack(N)]`: cap each field's effective alignment at `N` bytes
* `[Align(N)]`: raise the aggregate's alignment to at least `N` bytes
* `[FieldOffset(N)]`: the explicit byte offset of a field under `Explicit` layout

```stark
[StructLayout(C)]
struct Timespec
{
    System.C.c_long Seconds;
    System.C.c_long Nanoseconds;
}

[StructLayout(C), Pack(1)]
struct WireHeader
{
    u8[0 max] Tag;
    u32[0 max] Length;
}

[StructLayout(Explicit)]
struct WordParts
{
    [FieldOffset(0)] u32[0 max] Whole;
    [FieldOffset(0)] u16[0 max] Low;
    [FieldOffset(2)] u16[0 max] High;
}
```

`[StructLayout(C)]` lays fields out in declaration order using the active target's C ABI alignment for each resolved field type, inserts padding before fields as needed, and rounds the aggregate size up to the struct alignment. The struct alignment is the maximum effective field alignment unless `[Align(N)]` raises it. This is Stark's `repr(C)` equivalent.

`[Pack(N)]` caps each field's effective alignment at `min(naturalAlignment, N)`, which removes the inter-field padding that field alignment would otherwise add. `[Pack(1)]` is the fully packed form. `[Align(N)]` only raises the aggregate alignment; it never caps a field alignment or moves a field except through the final tail-padding rule. When both appear, field offsets come from `Pack(N)` and `Align(N)` then raises the whole-struct alignment. `N` for `Pack`, `Align`, and `FieldOffset` is a byte count; `Pack(N)` and `Align(N)` require a positive power-of-two literal (`1, 2, 4, 8, 16, 32, 64, ...`).

`[StructLayout(C)]` does not make every field type FFI-safe. C-layout field types are limited to:

* Stark sized integer and floating point primitives
* `System.C` target-mapped aliases (see 13.4)
* raw pointers, including `rawptr<System.C.c_void>` / `rawmutptr<System.C.c_void>`
* fixed arrays of FFI-safe element types
* nested `[StructLayout(C)]` or `[StructLayout(Explicit)]` structs

Stark enums, dynamic storage, safe borrows, closures, trait objects, and owning heap values are rejected across an FFI layout boundary because their layout or drop behavior is Stark-specific. Those types can still appear in ordinary (non-`StructLayout`) structs.

**Packed-field safety.** A field whose computed offset does not satisfy its natural alignment (under `Pack` or `Explicit` layout) is a *packed field*:

* Reading and writing a packed field is allowed; the compiler emits unaligned loads and stores.
* Taking a **safe borrow** of a misaligned packed field is a compile-time error, because a safe borrow promises natural alignment.
* Taking a **raw pointer** to a misaligned packed field is allowed in unsafe code and preserves the misalignment fact.
* Passing a packed field by address to `ffi` requires the external declaration to accept the actual pointer alignment.

This keeps packed layout from becoming hidden undefined behavior. Default Stark layout is not a stable ABI; these attributes are the opt-in for predictable C-compatible layout. The compiler never uses the host machine's C layout when compiling for a different target.

The `System.Compiler` compile-time facts (`StructLayoutIsC<T>()`, `StructHasPack<T>()`, `FieldHasExplicitOffset<T, I>()`, `FieldIsMisaligned<T, I>()`, and the field/offset/size/align facts) expose this layout metadata for generic code; see the comptime structural facts reference.

## 9. Globals and Storage Classes

Top level globals use dedicated global declaration forms. Globals are written as `const`, `static`, or `static mut`. Global lifetime is implied by being a top level declaration.

Stark has three classes of globals:

* `const name = ...;` or `const T name = ...;`
  A fully frozen global object graph. `const` is stronger than an immutable binding: the value and everything transitively reachable through it are deeply immutable for the lifetime of the program. A typed `const` may be a fixed array, with the dimension written on the **type** (the grammar is `type variableDeclarators`), not the name: `const f64[4] Weights = { 1.0, 0.5, 0.25, 0.125 };` — not the C-style `const f64 Weights[4]`.
* `static mut T name = ...;`
  A mutable global rebinding. The global binding itself may be reassigned after initialization.
* `static T name = ...;`
  An immutable global binding. The binding cannot be rebound, but the referenced value may still contain mutable heap state or other mutability that the type permits. This form also covers ordinary plain immutable global values.

The distinction:

* `const`: fully frozen global object graph
* `static`: immutable binding, not deep freeze
* `static mut`: mutable global rebinding

The source level global model:

* mutable global rebinding
* immutable global pointer to mutable heap object
* fully frozen global object graph

Specifically, `const` means:

* every reachable field and element is observed as readonly
* any pointer like or view like value reachable through the hierarchy is itself readonly
* safe code may not derive a mutable capable alias from any reachable part of the graph
* safe code may not regain mutation through explicit raw pointer or integer conversion chains

Reading from a `const` global behaves like reading through a deeply frozen view of the entire reachable hierarchy, not merely through a root binding that happens not to be assignable.

Compile time scalar numeric constants do not use an integer range. Integer consts use the smallest supported integer width that preserves the value; the source program does not spell that range. Floating point consts follow the written number: `80.0` is `f64`, and `80.0f` is `f32`. Use an explicit `const T name = ...;` form for non scalar or otherwise ambiguous constants such as raw pointer nulls, fixed arrays, or aggregate initializers.

Local variables still require an explicit storage class.

The storage classes:

* `stack`
* `heap`
* `register`
* `static`
* `arena`

`register` is a local only value style storage class. A `register` local has no stable source visible address: safe code may not take `&local`, form a slice view from it, or otherwise require something with an address. It is not a promise that a hardware register will be allocated; it is a request to keep the value in registers when possible. Use `stack` when a stable address is required.

Function local `static` storage is not a valid local storage class. Use a top level `static` global for global lifetime storage.

`dynamic T` is not a storage class. It is an owned dynamic storage type. A declaration such as `stack mut dynamic i32[0 max] values = new();` places the dynamic owner/header in the stack local, while the dynamic value manages its own capacity-bearing backing storage. The backing storage may still be selected by an allocation form such as `new(arena, capacity)`.

The standardized allocation backed storage classes:

* `heap`: uses the default global general purpose allocator. Safe Stark code does not manually free `heap` values; ownership and scope still govern destruction.
* `arena`: uses a compiler-managed lexical arena frame. Arena-backed values are owned Stark values while alive, and their backing storage is reclaimed in bulk at the end of the arena lifetime.

`arena` is a language storage class like `stack`, `heap`, and `register`; it is
not a `System.Memory.Arena` object and user code does not pass an arena handle
to allocate from the current arena frame.

```stark
fn void Build()
{
    arena Node root = new Node();
    arena mut dynamic Token[0 max] tokens = new(1024);
}
```

The compiler creates a hidden arena frame for scopes that contain arena
allocations. At the end of the arena lifetime, Stark drops live arena-backed
values that require destruction and then releases or resets the arena backing
storage in bulk. Safe code does not manually free individual arena-backed
objects.

A non-arena owner may request arena-backed dynamic storage with the `arena`
storage selector:

```stark
fn void Tokenize()
{
    stack mut dynamic Token[0 max] tokens = new(arena, 1024);
}
```

`new(arena, capacity)` creates a `dynamic T` value whose owner/header is stored
where the declaration places it, but whose backing element storage belongs to
the current arena lifetime. An optional third argument may specify a stronger
positive power-of-two backing alignment.

Arena-backed storage must not escape the arena lifetime through returns,
heap/static/global stores, retained borrows, escaping closures, or longer-lived
aggregate fields. Safe borrows into arena storage may be passed to callees only
when the callee's contract does not retain the borrow beyond the call.

Arena allocation is not allowed in `law` functions because it mutates hidden
allocation state and can fail or trap according to the allocation policy.
`finite` functions may use arena allocation when their ordinary progress and
return rules are satisfied.

Arena-backed dynamic growth uses allocate-copy semantics. `Reserve`,
`TryReserve`, and `TryReserveCapacity` allocate a new arena backing buffer when
growth is needed, copy the initialized prefix, and leave the old backing storage
for arena-frame cleanup. They do not lower to per-object `free`.

Mutability remains opt in:

* bindings are immutable by default
* `mut` enables reassignment where the binding form permits it
* `const` on a global freezes the reachable object graph rather than merely freezing the top level name

### 9.1 Sharing Globals Between Threads

A `static mut` global that more than one thread touches is a data race unless every
access goes through an atomic type. `System.Threading` provides one atomic struct per
integer width (`AtomicI8` … `AtomicI1024`, `AtomicU8` … `AtomicU1024`) plus
`AtomicBool`; their operations are compiler builtins that lower to hardware atomic
instructions, and every operation is sequentially consistent.

```stark
static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

fn void Bump()
{
    Counter.Add(1);    // one indivisible instruction — never a load/store pair
}
```

There is no atomic qualifier keyword and no way to make plain assignment atomic: the
atomic types are the sharing surface. See the `System.Threading` standard library
reference for the full operation set and the per-width cost model.

For guarded mutable aggregates rather than single scalars, `System.Threading`
provides `Synchronized<T>` (a lock-backed owner) and the `Locked<T>` guard it
hands out, so a shared aggregate global stays correct without spelling raw locks.

### 9.2 Thread-Safety Laws

Stark has two compiler-known thread-safety laws that describe how a value may
cross threads:

* `Transferable(T)`: ownership of a `T` may move to another thread
* `Shareable(T)`: a borrow of a `T` may be accessed from several threads at once

The compiler computes these facts structurally at type-check time, including
propagation through generic type arguments and fields. Use a law in a callable
`where` clause to require the fact at the call site:

```stark
fn T MoveToThread<T>(T value) where Transferable(T)
{
    return value;
}
```

Raw pointers and `storeborrow` fields deny both laws by default; the
`System.Threading.Atomic*` types satisfy both by intrinsic compiler grant. Two
attributes audit the structural result on a type or field:

* `[Grant(Law)]` asserts a law the structural derivation cannot see, optionally
  conditioned on another law with `[Grant(Law) where OtherLaw(T)]`
* `[Deny(Law)]` opts a type out of a law it would otherwise satisfy

```stark
struct Synchronized<T>
{
    [Grant(Shareable) where Transferable(T)]
    T Payload;
}
```

These laws are consumed by direct and member calls, explicit payload thread
starts, channels, `Synchronized<T>`, and thread-entry reachable mutable statics.
`[Grant(...)]` is for audited overrides only; `[Deny(...)]` is for semantic
opt-outs that structural derivation cannot infer.

## 10. Control Flow

The statement forms:

* blocks
* local constant declarations
* local variable declarations
* `if` and `else`
* `switch`
* `while`
* `for`
* `return`
* `break`
* `continue`
* expression statements

### 10.1 Branch Weights

`if` and `switch` may carry an optional branch weight annotation such as `w9` or `w99`.

### 10.2 Loops

`while` and `for` require an explicit loop behavior keyword:

* `infinite`
* `non-deterministic`
* `willexit`

This is part of Stark's source level control flow contract, especially for `finite` functions.

Loop behavior rules:

* `infinite`
  * must use a statically unconditional condition
  * `while` must use literal `true`
  * `for` must omit the condition expression
  * may not contain a structural exit from the current loop or function such as `break` or `return`
  * is not accepted inside a declared `finite` function
* `non-deterministic`
  * may or may not exit
  * is not accepted inside a declared `finite` function
* `willexit`
  * is the only loop form accepted inside a declared `finite` function
  * if the loop condition is statically unconditional (`while willexit (true)` or `for willexit (;;)`), the body must contain at least one structural `break` or `return`

Loops may also carry the `independent` memory contract:

```stark
for willexit independent (stack i32[0 max] i = 0; i < count; i += 1)
{
    output[i] = left[i] + right[i];
}
```

`independent` means loop iterations have no loop-carried memory dependencies. A memory write in one iteration may not be read or written by another iteration, and a call inside the loop may not create cross-iteration memory dependence. Reads from immutable memory are allowed by the language contract. Writes are allowed when each iteration writes a region proven separate from the regions read or written by other iterations.

Scalar-only `while` and `for` loops may use scalar local values directly, and their bodies may declare stack or register scalar locals with pure scalar initializers. Canonical `for` loops may also use slice, fixed-array, and bounded raw pointer region element accesses when the element index is the loop induction variable, the induction variable is incremented by exactly one, and every write/read root pair is either the same indexed root or proven disjoint by parameter contracts, borrow exclusivity, raw pointer region facts, or an enclosing `if disjoint(...)` fact. The accepted memory-backed form includes structured `if` statements whose conditions and branches satisfy the same subset, and it includes field projections rooted at the per-iteration element, such as `root[index].field`. Calls inside that memory-backed subset are accepted when they resolve to law functions with scalar returns, so the call itself introduces no unproven memory effect.

```stark
fn i32[0 10] CountFour()
{
    stack mut i32[0 10] value = 0;
    while willexit independent (value < 4)
    {
        value += 1;
    }

    return value;
}
```

Accepted `willexit` loops state that the loop is expected to make progress and finish. Accepted `independent` loops additionally state that iterations have no loop-carried memory dependence. Raw pointer accesses in this subset may use the normal raw pointer spelling `*(&root[index])` when `root` has a bounded raw pointer region. Unbounded pointer dereferences, address-of expressions that create new unbounded regions, member access that is not rooted at `root[index]`, non-induction indexes, memory-backed local declarations, nested loops, early exits, and calls with unproven memory effects produce `STK3027`.

**Traversal loops.** A `for` loop may iterate the elements of a fixed array,
slice, or `dynamic` value directly with the `in` form. It lowers to a counted
loop with no iterator object and no hidden runtime dispatch. The element binding
must be `borrow T` or `borrow mut T`; mutable traversal requires mutable element
storage:

```stark
for willexit (borrow Token token in tokens)
{
    Process(token);
}

for willexit (borrow mut Token token in tokens)
{
    Normalize(token);
}
```

An optional leading index binding uses `stack` or `register` storage and an
integer range wide enough for every source index:

```stark
for willexit (stack u64[0 max] index, borrow Token token in tokens)
{
    Record(index, token);
}
```

Traversal loops still require a loop-behavior keyword, and `independent` applies
to them as it does to counted loops.

### 10.3 Disjoint Branch Conditions

`if disjoint(...)` tests memory-region overlap and introduces a branch-scoped fact:

```stark
if disjoint(source, destination)
{
    CopyDisjoint(source, destination);
}
 else
{
    CopyOverlapSafe(source, destination);
}
```

Inside the true branch, every memory region listed in `disjoint(...)` is known to be pairwise non-overlapping. The false branch does not receive the disjoint fact and must use overlap-safe behavior. For contiguous slices, text views, bounded raw pointer parameters, and raw pointer region expressions, the check compares the memory ranges represented by their data pointer, element size, and length.

The true-branch fact can satisfy a callee's `disjoint` parameter contract for the same regions or for subregions covered by those checked regions. The false branch cannot use that fact.

### 10.4 Switch and Patterns

The switch surface includes:

* literal cases
* `default`
* `when` guards
* `case var capture`
* discard `_`
* dot qualified enum case patterns for unit, tuple, and named field enum cases
* exact type named aggregate patterns with nested aggregate subpatterns
* aggregate property patterns that match named fields: `case Box { Width: var w }:`
* exact-length list patterns over fixed arrays, slices, and dynamic storage: `case [first, second]:`
* switch-label or-patterns that share one body: `case A | B:`
* inclusive integer range patterns: `case 0..9:`

Property patterns must name every aggregate field exactly once. List patterns are
exact-length: a fixed-array length mismatch is a compile-time error, and
slice/dynamic list patterns lower to a length check plus direct element tests with
no iterator protocol or hidden allocation. Or-pattern alternatives must bind the
same captures with the same types. Range patterns are integer-only, are checked as
intervals rather than expanded into individual values, and may appear at top level
or nested inside enum, aggregate, and list field patterns.

```stark
switch (token)
{
    case Token.Integer(0..9) | Token.Integer(10..19):
        return 1;
    case Token.Integer(_):
        return 2;
    case Token.End:
        return 0;
}
```

**Exhaustiveness.** Every `switch` must cover its scrutinee's whole domain. A value
that matches no arm has nowhere to go, so the gap is a compile error rather than a
hidden runtime trap or a silent fall-through:

* an enum switch covers every variant, or has a `default`
* a `bool` switch covers `true` and `false`, or has a `default`
* an integer switch covers every value of the scrutinee's (possibly ranged) type —
  practical for narrow ranges such as `u8[0 3]` — or has a `default`
* switches over types whose values cannot be enumerated by cases (floats, text, raw
  pointers, aggregates) need a `default` or a match-all pattern (`var` / `_`)

`when`-guarded arms never count toward coverage: a guard can decline at runtime, so a
guarded arm proves nothing about the domain. Exhaustiveness also feeds definite-return
analysis (5.3): an exhaustive switch whose sections all return counts as returning on
all paths. Adding a variant to an enum is therefore a compile-time event — every
non-`default` switch over that enum reports the missing case until it is handled.

### 10.5 Error Propagation with `try`, `[Ok]`/`[Err]` Roles, and `from`

Errors in Stark are values: a fallible function returns an enum with a success variant and a failure variant, and the caller inspects it. Handling every fallible call with an explicit `switch` is correct but verbose, so Stark provides one propagation expression, `try`, that early-returns the failure while keeping the divert point visible. Stark has no exceptions; `try` is the only construct that turns an error value into an early return, and unlike a trailing sigil it is a leading keyword you can grep for.

**Propagatable enums and variant roles.** `try` works with any two-variant enum that declares which variant means success and which means failure. The declaration uses the innate variant attributes `[Ok]` and `[Err]`:

```stark
public enum Result<T, E>
{
    [Ok] Ok(T),
    [Err] Err(E),
}

public enum Option<T>
{
    [Ok] Some(T),
    [Err] None,
}

enum FetchOutcome              // a user enum with arbitrary names: equally propagatable
{
    [Ok] Got(Payload),
    [Err] Failed(FetchError),
}
```

Recognition is purely structural: the roles come from the attributes, never from variant names, type names, or standard-library identity. The standard library's `Result<T, E>`, `Option<T>`, `IOResult<T>`, and the other status/result enums are propagatable because their declarations carry these attributes — they are not compiler-privileged. The role rules, checked where the enum is declared:

* a role-carrying enum has exactly two variants, one marked `[Ok]` and one marked `[Err]`;
* a role-marked variant carries at most one payload (or none);
* `[Ok]` and `[Err]` take no arguments, and they are the only variant attributes that exist.

An enum that merely has the right shape but no role attributes is not propagatable: roles are opt-in, never inferred.

**What `try` does.** `try expr` evaluates `expr`, which must be a value of a propagatable enum:

* if the value is the `[Ok]` variant, `try` evaluates to that variant's payload (or to no value when the `[Ok]` variant is unit-like) and execution continues;
* if the value is the `[Err]` variant, `try` **returns from the enclosing function**, wrapping the failure in the enclosing return type's `[Err]` variant, after running the same drops a written `return` would run.

Three requirements connect the operand and the enclosing function:

* the operand's type must be a propagatable enum;
* the enclosing function's return type must also be a propagatable enum — not necessarily the same enum, and not necessarily the same generic family;
* the failure payloads must be connected: either both `[Err]` variants are unit-like (no payload), or both carry the same error type, or the enclosing `[Err]` payload type declares a `from` funnel for the operand's error type (below). A unit failure cannot propagate into a payload-carrying failure or vice versa — `try` never invents an error value and never silently discards one. If the failure payloads are not connected, the `try` is a compile error.

The success payloads are deliberately independent: `try` yields the operand's success value for the enclosing function to keep computing with, and what that function later wraps in its own `[Ok]` variant is unrelated to what it unwrapped. Only the failure path ties the two signatures together.

```stark
fn Result<Module, LoadError> LoadModule(ascii path)
{
    stack ascii  text = try ReadFile(path);   // [Ok] -> text; [Err] -> return the error
    stack Ast    ast  = try Parse(text);
    stack Module mod  = try Resolve(ast);
    return Result<Module, LoadError>.Ok(mod);
}
```

**Position.** Because `try` injects a hidden early return, it is restricted to positions where that return sits at a statement boundary: the whole initializer of a binding, the whole right side of an assignment, the operand of `return`, or a bare expression statement (which discards the success value but still propagates the failure). It may not be nested inside a larger expression — `Foo(try a(), try b())` is rejected. Bind the fallible call to a local first, then `try` that local.

**Cross-family conversion with `from`.** When the operand and the enclosing function fail with different error types, the lower-layer error must be converted. An error `enum` declares how it absorbs a cause by marking the absorbing variant with `from`:

```stark
enum LoadError
{
    Io      from IoError,        // variant Io, payload IoError, and the funnel for IoError
    Parse   from ParseError,
    Resolve from ResolveError,
}
```

`Io from IoError` is an ordinary single-payload variant (identical at runtime to `Io(IoError)`) plus a declaration that `try` may wrap a propagating `IoError` into `LoadError.Io`. With the funnels declared once on the error type, every `try` whose operand fails with `IoError`/`ParseError`/`ResolveError` converts automatically; the call sites stay bare. The funnel connects whole enum families: a `try` whose operand is a stdlib `IOResult<T>` (failing with `IOError`) inside a function returning `Result<T, LoadError>` converts through `LoadError`'s `Io from IOError` funnel. When the operand's error type already matches the enclosing error type, no conversion happens and no `from` is needed. An enum may declare at most one `from` funnel per source type, and a cross-family `try` whose enclosing error type has no funnel for the operand's error is a compile error — add a `from` variant or handle the error with `switch`.

`try` lowers to a discriminant test plus the existing early-return and drop machinery, and the conversion is a zero-cost variant wrap; a `law` method behind `try` stays pure and a `finite` method stays terminating. There is no hidden cost beyond the disclosed early return.

### 10.6 Pattern Conditions in `if` and `while`

`if` and `while` conditions accept a pattern-match form, `expr is pattern`, that tests a value against a pattern and binds its captures into the branch that runs on a match. The pattern is exactly the `switch case` pattern surface (10.4): enum case patterns, nested aggregate patterns, named-field patterns, `var` captures, `_`, and literals.

```stark
if (Lookup(key) is Option<Value>.Some(var value))
{
    Use(value);          // `value` is bound here, on the match path only
}
else
{
    UseDefault();        // no binding here
}
```

* In `if (expr is pattern) then else else`, `expr` is evaluated; if it matches the pattern, the captures bind and `then` runs; otherwise `else` runs (or control continues past the `if`). The captures are in scope only in `then`.
* In `while behavior (expr is pattern) body`, each iteration evaluates `expr` and, on a match, binds the captures and runs `body` (the captures are re-bound each iteration); the loop exits on the first non-match. This is the `while let` drain idiom.

Capturing a move-only payload with `var` moves it out of the matched value, exactly as in `switch`; the capture is dropped at the end of the branch/body. When the matched value is a local, the move is tracked so its own scope drop does not double-drop. The condition expression of `expr is pattern` is the scrutinee, not a boolean — only a plain (non-`is`) condition is required to be `bool`.

`expr is pattern` lowers to the same discriminant-test-and-bind machinery as `switch`, so it carries no extra cost over the equivalent single-case `switch`.

### 10.7 Labeled `break` and `continue`

A loop or `switch` may carry a label, written as `name:` before the statement. A
`break name;` exits the labeled loop or switch, and a `continue name;` continues
the labeled loop. This is how an inner loop targets an enclosing loop without a
flag variable:

```stark
fn i32[min max] Find(borrow i32[min max][] rows, i32[min max] needle)
{
    outer: for willexit (stack u64[0 max] r, borrow i32[min max] row in rows)
    {
        if (row == needle)
        {
            break outer;
        }

        if (row < 0)
        {
            continue outer;
        }
    }

    return 0;
}
```

`break name;` targets a labeled loop or `switch`; `continue name;` targets a
labeled loop only.

## 11. Expressions

The expression surface includes:

* literals
* identifiers and qualified names
* dot qualified enum constructor expressions
* calls
* member access
* indexing
* object creation with `new`
* object initializers
* array initializers
* unary operators
* binary operators
* ternary conditional `?:`
* assignments and compound assignments
* error propagation `try` (see 10.5)
* compile-time evaluation `comptime` and the layout queries `sizeof` / `alignof` (see 11.7)

Object creation supports both explicit and target typed forms:

```stark
stack Box explicitBox = new Box();        // calls Box's default constructor
stack Box defaultBox = new();             // calls the target type's default constructor
stack Box constructedBox = new(value);    // calls the corresponding constructor
stack Box initializedBox = new()
{
    Value = value
};
```

Target typed `new()` and `new(args)` require the surrounding code to already say which `struct` or `record` type is being created. That target type can come from a local declaration, assignment target, return type, object initializer field type, or fixed array element type. If Stark cannot tell which `struct` or `record` you mean, use the explicit form such as `new Box(...)`.

`new()` calls the zero argument constructor for the target `struct` or `record`. If the type declares no constructors, Stark provides an implicit default constructor that default initializes the value. `new(value)` and `new(args)` call a matching constructor if one exists. If the type declares constructors but none match the supplied arguments, compilation fails.

Inside a constructor body, `self` begins with every field in its zero state; a field's intended value exists only after the body assigns it. Reading an owning field of `self` — one backed by dynamic storage or owned text — before it has been assigned is a compile error, because it would read that zero state rather than the constructed value. Assign the field first, then read it:

```stark
struct Tokens
{
    u64[0 2 ** 63 - 1] Count;
    dynamic i32[min max] Items;

    Tokens()
    {
        self.Items = new();
        self.Count = self.Items.Length;   // ok: Items is assigned above
    }
}
```

Scalars, fixed arrays, and other inline storage have a valid zero state and may be read or written element-wise without a prior whole-field assignment.

Text views use the existing postfix indexing form:

```stark
text[]
text[index]
text[start, length]
```

For `ascii` and `unicode`, these forms produce zero copy text views over the same backing storage.

`{ ... }` is an array initializer, not a slice literal.

An array initializer may materialize a fixed array value or participate in nested aggregate initialization where the target storage is already defined. For fixed arrays, omitted trailing elements are zero initialized. It may not target a slice type directly.

This is invalid:

```stark
stack i32[] view =
{
    1, 2, 3
};
```

because `i32[]` is a view type and Stark does not silently create hidden backing storage for slice targets.

Instead, the backing storage must be made explicit:

```stark
stack i32[3] values =
{
    1, 2, 3
};
stack i32[] view = values;
```

### 11.1 Operators

Operator families:

* arithmetic: `+`, `-`, `*`, `/`, `%`
* bitwise: `&`, `^`, `|`
* shifts: `<<`, `>>`
* wrapping integer arithmetic: `+%`, `-%`, `*%`
* saturating integer arithmetic: `+|`, `-|`, `*|`
* comparisons: `<`, `>`, `<=`, `>=`, `==`, `!=`
* logical: `&&`, `||`, `!`
* raw pointer address of: unary `&`
* raw pointer dereference: unary `*`
* unary bitwise not: `~`
* unary wrapping negate: `-%`
* conditional: `?:`
* assignment: `=`, `+=`, `-=`, `*=`, `+%=`, `-%=`, `*%=`, `+|=`, `-|=`, `*|=`, `/=`, `%=`, `&=`, `^=`, `|=`
* exponentiation: `**`

`^` is bitwise XOR, not exponentiation.

`**` is the exponent operator.

Explicit conversions use C style syntax: `(targetType)expression`.

This is the required surface for conversions Stark keeps explicit:

* integer widening and narrowing
* integer and float conversions
* raw pointer to integer and integer to raw pointer conversions
* raw pointer to raw pointer conversions
* fixed array to slice view conversions
* ascii and unicode text conversions for compile time text constants

Runtime conversion between `ascii`, owned UTF-16 buffers, and `unicode` values uses the explicit `System.Text` helper APIs such as `FromAsciiToUnicode`, `FromAsciiToUtf16`, `FromUtf16ToUnicode`, `FromUnicodeToAscii`, `FromUnicodeToUtf16`, and `FromUtf16ToAscii`. These use owned/dynamic destination storage and report allocation failure with `System.Memory.MemoryStatus`.

These conversions may not strengthen mutability. Safe code may not use explicit conversions to turn a readonly raw pointer into `rawmutptr<T>`, and may not strip the readonly or frozen origin off a raw pointer to regain mutation later.

### 11.2 Precedence

From highest to lowest:

1. postfix: calls, indexing, member access
2. exponentiation: `**` with right associative parsing
3. unary: explicit conversions, unary `+`, unary `-`, `!`, `~`, raw `&`, raw `*`
4. multiplicative: `*`, `/`, `%`
5. additive: `+`, `-`
6. shifts: `<<`, `>>`
7. bitwise and: `&`
8. bitwise xor: `^`
9. bitwise or: `|`
10. equality and relational comparisons
11. logical `&&`
12. logical `||`
13. conditional `?:`
14. assignment

Comparison chains are formed at comparison precedence.

### 11.3 Comparison Chains

Comparison operators may be chained.

Examples:

* `a < b < c`
* `min <= value < max`
* `left == middle != right`

A comparison chain:

* evaluates operand expressions left to right
* evaluates each operand expression exactly once
* compares each adjacent operand pair using the written operator
* short circuits on the first false comparison
* produces `bool`

`a < b < c` is not interpreted as `(a < b) < c`. It has the semantics of:

```stark
a < b && b < c
```

with the shared middle operand evaluated only once.

More generally, `a op1 b op2 c op3 d` behaves like:

```stark
a op1 b && b op2 c && c op3 d
```

with each operand evaluated once and then reused for the adjacent comparisons.

Each adjacent comparison must be individually legal under Stark's ordinary comparison rules. If any adjacent comparison is ill typed, the chain is ill typed.

### 11.4 Exponentiation

Exponentiation uses `**`.

* `**` is legal for floating point operands
* integer `**` is legal for integer operands and compile time constant exponent expressions are folded before runtime lowering
* ordinary runtime integer exponentiation is supported for integer operands

### 11.5 Floating Point Contract

Ordinary floating point code uses aggressive fast math semantics.

* ordinary floating point code uses optimizer friendly non strict floating point operations
* strict IEEE style semantics require the explicit `strictfp` function modifier
* `strictfp` is the source level escape hatch from Stark's default fast math model

### 11.6 Integer Arithmetic Contract

Ordinary integer arithmetic in Stark is performance first and intentionally strict.

* signed and unsigned overflow in ordinary arithmetic is illegal and treated as undefined behavior
* shift counts greater than or equal to the bit width of the shifted value are illegal and treated as undefined behavior
* wrapping arithmetic uses the Zig style spellings `+%`, `-%`, `*%` and the corresponding compound assignments
* saturating arithmetic uses the Zig style spellings `+|`, `-|`, `*|` and the corresponding compound assignments

A bare integer literal carries no fixed width of its own in mixed arithmetic. When
it is combined with a runtime ranged-integer operand, the literal adopts that
operand's ranged type whenever its value fits, so the expression keeps the
operand's type instead of widening to a signed default. This is the same outcome
as variable-with-variable arithmetic over a shared ranged type, and it means a
result can be stored or returned in the operand's type without a narrowing cast:

```stark
finite law u64[0 2 ** 63 - 1] Next(u64[0 2 ** 63 - 1] position)
{
    return position + 1;   // u64[0 2 ** 63 - 1]; no narrowing cast required
}
```

The literal adopts only the other operand's numeric shape — its width, range, and
sign — never an `out`, `borrow`, or `frozen` qualifier, because arithmetic yields
a plain value. A literal whose value does not fit the operand's range, or a
negative literal paired with an unsigned operand, keeps its own type and still
needs an explicit narrowing conversion. Fully compile-time expressions fold on
their own and are unaffected.

### 11.7 Compile-Time Evaluation

`comptime expr` forces an expression to be evaluated during compilation, and
`comptime { ... }` runs a compile-time block when local compile-time state or a
bounded loop makes the code clearer. The two layout queries `sizeof(T)` and
`alignof(T)` fold to `u64` constants when `T` has a concrete runtime layout in the
current target:

```stark
const PairSize = comptime sizeof(Pair);

finite law i64[min max] LayoutScore()
{
    return comptime sizeof(Pair) + comptime alignof(Pair);
}
```

Compile-time evaluation supports ordinary constants, `law` / `finite law` calls,
fixed-array tables, named and enum aggregates, layout queries, `switch` and
pattern-condition execution, and fixed-array traversal. A `willexit` loop inside
compile-time evaluation has an iteration budget and reports a diagnostic when it is
exhausted; recursive compile-time `law` calls are likewise bounded rather than
allowed to recurse forever.

Compile-time-only structural predicates live under `System.Compiler`. They answer
questions about types, fields, enum variants, layout, callables, traits, and
associated types — for example `IsStruct<T>()`, `FieldCount<T>()`,
`Implements<T, Trait>()`, and `TypeSize<T>()`. They may be called only inside
`comptime`; a runtime call is rejected, and the facts are erased before runtime
lowering, so they create no reflection metadata:

```stark
finite law i64[min max] Score<T>()
{
    if (comptime System.Compiler.IsInteger<T>())
    {
        return sizeof(T);
    }

    if (comptime System.Compiler.IsStruct<T>())
    {
        return sizeof(T) + alignof(T);
    }

    return 0;
}
```

## 12. Strings and Characters

Stark distinguishes two text forms:

* `ascii`
* `unicode`

The core owned text container forms:

* `Ascii`
* `Unicode`

String literals infer to:

* `ascii` by default when the decoded literal can be stored as UTF-8
* `unicode` when explicitly requested with a compile time text conversion such as `(unicode)"..."`

Supported escapes in string and character literals:

* simple escapes: `\\`, `\"`, `\'`, `\0`, `\b`, `\t`, `\n`, `\f`, `\r`
* hex escapes: `\xNN`
* unicode escapes: `\uNNNN`

Character literals follow the same inference path instead of using a dedicated standalone `char` type.

Raw string literals do not process escape sequences; the characters between the delimiters are taken verbatim, which suits regexes, Windows paths, and embedded source text:

* `raw"..."` is a single-line raw literal: no escapes, no embedded newlines
* `raw"""..."""` with content on one line is a single-line raw literal whose content may include `"` characters
* `raw"""..."""` spanning lines is a multiline raw literal and follows the C# raw-string rules below

Multiline raw literals delimit their content by lines, exactly like C# raw string literals:

* the opening `raw"""` ends its line; the newline after it is not part of the value (content on the opening-quote line is an error)
* the closing `"""` stands alone on its line; the newline before it is not part of the value
* the whitespace before the closing `"""` defines an indentation that is stripped from every content line, so the literal can be indented with the surrounding code
* a non-blank content line that does not start with that exact indentation is an error; whitespace-only lines contribute empty lines

```stark
stack ascii usage = raw"""
    usage: stark build [--release]
           stark run
    """;
// value: "usage: stark build [--release]\n       stark run"

stack ascii pattern = raw"\d+\.\d+";
```

Raw literals compose with interpolation as `$raw"..."` and `$raw"""..."""`: the verbatim escape rules and the multiline trimming rules still hold while `{ ... }` holes are evaluated. Raw literals infer to `ascii` when the contents fit UTF-8, like ordinary string literals.

The text runtime contract:

* `ascii` is a UTF-8 text view
* `unicode` is a UTF-32 text view
* `Ascii` and `Unicode` are the owning text container forms
* `text[]` returns the full same kind text view
* `text[index]` returns a same kind one element text view
* `text[start, length]` returns another zero copy text view of the same text kind
* explicit text conversion is required where widening, narrowing, or ownership changes are involved

### 12.1 Interpolated Text

Stark supports C# style interpolated text literals. If every `{...}` hole can be folded at compile time, the whole interpolation behaves like one ordinary text constant:

```stark
finite law ascii ScoreLabel()
{
    const score = 100;
    return $"Score: {score}";
}
```

Each `{...}` hole is parsed and checked as an ordinary Stark expression. Compile time holes may be integer values, floating point values, `bool`, or text literals. Constant interpolation chooses `ascii` or `unicode` from the folded literal contents and can be target typed where an ordinary text literal conversion is valid.

Runtime holes need caller selected storage:

```stark
fn Ascii ScoreLabel(i32[min max] score)
{
    stack Ascii label[64] = $"Score: {score}";
    return label;
}
```

The `[64]` is the buffer capacity. Runtime interpolation writes through the `System.Text` formatting and concatenation APIs into the selected `Ascii` or `Unicode` buffer. If the selected capacity is too small, the operation traps instead of throwing an exception or silently cutting off text. Use the `System.Text` APIs directly when overflow should be handled as a returned value.

The rules:

* `$"..."` uses the same literal decoding rules as ordinary string literals
* constant interpolation chooses `ascii` or `unicode` from the folded literal contents and can be target typed to either text view where an ordinary text literal conversion is valid
* fixed capacity runtime interpolation chooses `Ascii` or `Unicode` from the destination buffer type
* each runtime hole must already be matching text, or must have a known fixed buffer formatter such as `TryFormatI32Ascii` or `TryFormatBoolUnicode`
* fully constant interpolations fold to ordinary text constants
* runtime interpolation uses Stark's ordinary no exception failure model internally and keeps the destination capacity visible in source

### 12.2 Text Concatenation and Planned Conversion

Stark supports `+` for compile time text constants:

```stark
finite law ascii ScoreLabel()
{
    return "Score: " + "100";
}
```

This is one ordinary text constant, so it does not allocate or copy at runtime.

Stark also supports the common literal prefix runtime form when the right side returns an explicit owned text result:

```stark
fn System.Memory.MemoryResult<System.Text.OwnedAscii> ScoreLabel(i64[min max] score)
{
    return "Score: " + score.ToAscii();
}
```

This returns `System.Memory.MemoryResult<System.Text.OwnedAscii>`, so allocation failure remains visible to the caller.

Text concatenation is intended for readable, ordinary code. When runtime text must be copied into caller owned storage, put the capacity on the stack text buffer:

```stark
fn bool CombineTwoLines()
{
    stack System.Memory.MemoryResult<System.Text.OwnedAscii> leftResult =
        System.Console.ReadAsciiLine();
    stack System.Memory.MemoryResult<System.Text.OwnedAscii> rightResult =
        System.Console.ReadAsciiLine();

    stack mut System.Text.OwnedAscii left = new();
    stack mut System.Text.OwnedAscii right = new();

    switch (leftResult)
    {
        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
            return false;
        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
            left = value;
    }

    switch (rightResult)
    {
        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
            return false;
        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
            right = value;
    }

    stack Ascii combined[4096] = left.View() + right.View();
    return true;
}
```

The `[4096]` is the destination storage. It must be a positive compile time integer, and this narrow syntax is currently only for stack `Ascii` and `Unicode` locals. The declaration uses fixed local storage and the same `System.Text.TryConcatAscii` or `System.Text.TryConcatUnicode` copy behavior available through explicit library code. If the joined text does not fit, execution traps instead of silently truncating.

Use the explicit `System.Text.TryConcatAscii` and `System.Text.TryConcatUnicode` APIs when overflow needs to be recoverable.

The implemented conversion surface includes:

* `ToAscii()` and `ToUnicode()` formatting for integer, floating point, bool, and enum values
* parse APIs from `ascii` and `unicode` to numeric, bool, and enum values
* fixed buffer formatting APIs for no allocation paths
* result and status based failure reporting for conversions that can fail
* locale independent defaults for ordinary numeric formatting

The first explicit owned conversion APIs live as ordinary `System.Text` functions and method style convenience calls for bool, integer, floating point, and the first concrete standard library enum values:

```stark
stack System.Memory.MemoryResult<System.Text.OwnedAscii> label =
    System.Text.ToAscii((i64)42);

stack i64[min max] score = 42;
stack System.Memory.MemoryResult<System.Text.OwnedAscii> methodLabel =
    score.ToAscii();

stack System.Memory.MemoryResult<System.Text.OwnedAscii> encodingLabel =
    System.Text.Encoding.UTF8.ToAscii();
```

These return `System.Memory.MemoryResult<T>` so allocation failure and unsupported formatting are visible in ordinary code.

The first implemented fixed buffer formatting primitives are `System.Text.TryFormatBoolAscii`, fixed width signed and unsigned integer `Ascii` helpers such as `TryFormatI24Ascii`, `TryFormatI32Ascii`, `TryFormatI48Ascii`, `TryFormatI128Ascii`, `TryFormatI1024Ascii`, and `TryFormatU1024Ascii`, and the matching `Unicode` forms for integer widths through 1024 bits. The integer forms write base-10 text into caller owned `Ascii` or `Unicode` storage. These APIs return `false` when the destination is missing or too small.

The first implemented no allocation floating point formatting primitives are `TryFormatF64Ascii`, `TryFormatF32Ascii`, `TryFormatF64Unicode`, and `TryFormatF32Unicode`. This initial slice writes fixed six fractional digit decimal text such as `3.250000` into caller owned storage for finite values in the supported range and returns `false` when the value is unsupported or the destination is too small.

The default value text rules are intentionally plain and locale independent:

* bool values use lowercase `true` or `false`
* integers use base 10, no digit separators, no prefixes, and no leading `+`
* negative signed integers use one leading `-`, including signed minimum values
* zero uses `0`
* the first implemented floating point formatting slice uses fixed six fractional digits for finite supported values
* complete shortest round trip floating point formatting remains future work; the current `f32` and `f64` formatting slice writes fixed six fractional digit finite values
* the first implemented enum formatting and parsing APIs cover `System.Text.Encoding` and `System.Text.TextError` using exact declared case names
* general enum formatting remains future work; the current enum formatting and parsing APIs cover `System.Text.Encoding` and `System.Text.TextError`

The first text to value parsing primitives are exact bool and integer parsers through 1024 bit widths. `System.Text.ParseBoolAscii` and `System.Text.ParseBoolUnicode` accept only exact lowercase `true` or `false`. The `ParseI*Ascii`, `ParseI*Unicode`, `ParseU*Ascii`, and `ParseU*Unicode` integer forms through `i1024` and `u1024` accept only base-10 integer text. They return `System.Text.TextResult<T>` so invalid text and overflow are handled as ordinary data:

```stark
stack System.Text.TextResult<bool> parsed = System.Text.ParseBoolAscii("true");
stack System.Text.TextResult<i64> count = System.Text.ParseI64Ascii("-42");
```

## 13. FFI and Raw Boundaries

`ffi` marks the foreign boundary.

At that boundary:

* raw pointers are allowed
* nested raw pointers are allowed
* null may appear in raw pointer values and raw pointer checks
* foreign ABI shape is preserved
* Stark enums do not cross `ffi` or `export` boundaries
* foreign code must not unwind through Stark frames

Outside that boundary:

* safe borrows are non null
* safe values may not be assigned `null`
* pointers to pointers are not part of ordinary safe Stark code
* conversions from raw pointers into safe borrows must be explicit

**Calling conventions.** An `ffi` function uses the target's C ABI by default. Write an explicit convention as `ffi(abi)` when a foreign symbol uses a different one:

```stark
public unsafe ffi(c) fn i32[min max] puts(rawptr<i8[min max]> text);
public unsafe ffi(stdcall) fn i32[min max] LegacyCall(i32[min max] value);
```

Supported ABI names are `c` (the default), `cdecl`, `stdcall`, `fastcall`, `thiscall`, `vectorcall`, `sysv`, `win64`, `aapcs`, and `aapcs64`; each is honored only on targets that support it. A bare `unsafe ffi fn` (no parentheses) means `ffi(c)`.

Imported FFI declarations preserve the foreign symbol spelling. Identifiers may
start with `_` so C symbols such as `__error` can be declared directly; bare
`_` remains the discard token/pattern, not a bindable name.

One declaration can select a different ABI per target with `ffi(platform(...))`. Keys are `os.arch` (such as `windows.x64`), a bare `os`, or `default`; the most specific matching key wins, and a missing match with no `default` is a compile-time error:

```stark
public unsafe ffi(platform(
    windows.x86: stdcall,
    windows.x64: win64,
    linux.x64: sysv,
    default: c
)) fn i32[min max] HostCall(rawptr<i8[min max]> context);
```

The resolved ABI is part of the function and function-pointer type, so a `fnptr<ffi(c) fn ...>` is a distinct, incompatible type from `fnptr<ffi(stdcall) fn ...>` or an ordinary `fnptr<fn ...>`, and the ABI is preserved through package images and indirect calls (see 5.5 for ABI-bearing function-pointer types). `ffi(abi)` does not combine with `asm(...)` declarations. An ABI that the active target does not support is a compile-time error (STK2111).

For the C-facing types used at these boundaries, see 13.4 (C primitive aliases) and 13.5 (C strings). For C ABI struct layout, see 8.7.

### 13.1 C Style Varargs

Foreign C APIs that use variadic arguments may be declared with `ffi varargs`:

```stark
public unsafe ffi(c) varargs fn System.C.c_int printf(rawptr<System.C.c_char> format);
```

`varargs` is only valid on `ffi` declarations that end with `;`. Stark functions do not define C style variadic bodies.

The fixed parameters are checked normally. Extra call arguments are allowed after the fixed parameters, but they must already be safe to pass through the C varargs ABI as written:

* `i32[min max]`, `u32[0 max]`, or wider integers
* `f64`
* raw pointers, including `rawptr<System.C.c_char>` for C strings

Stark does not hide C's default argument promotions. If a C variadic function expects a floating point value, pass `f64`; cast `f32` to `f64` yourself. If a value is smaller than 32 bits, cast it to an explicit `i32[min max]` or `u32[0 max]` first.

```stark
public unsafe ffi(c) varargs fn System.C.c_int printf(ascii format);

unsafe fn System.C.c_int PrintScore(i32[min max] score)
{
    return printf("Score: %d\n", score);
}
```

**`%s` takes C strings, not Stark text.** Stark `ascii` and `unicode` are length-carrying views, not null-terminated `char*`. A `%s` conversion that receives ordinary Stark text is a compile-time error (STK3009): the argument must be `rawptr<System.C.c_char>` or `rawmutptr<System.C.c_char>`. Convert Stark text to a `System.C.OwnedCStr` first and pass `OwnedCStr.Data()`:

```stark
public unsafe ffi(c) varargs fn System.C.c_int printf(rawptr<System.C.c_char> format);

fn System.C.CStringResult<System.C.c_int> PrintName(ascii name)
{
    stack System.C.OwnedCStr format = try System.C.FromAscii("name: %s\n");
    stack System.C.OwnedCStr cName = try System.C.FromAscii(name);
    unsafe
    {
        return System.C.CStringResult<System.C.c_int>.Ok(
            printf(format.Data(), cName.Data()));
    }
}
```

See 13.5 for the `System.C` C-string types.

### 13.2 Assembly Functions

Assembly functions are Stark's current low level inline assembly boundary. They
are intended for small platform shims such as Linux syscalls, CPU instructions,
and tightly audited runtime helpers. Ordinary application code should prefer
safe standard library wrappers.

The implemented v1 surface is:

```stark
visibility unsafe ffi asm(architecture) fn ReturnType Name(parameters)
    in("register") parameterName,
    out("register") return,
    clobber("register1", "register2")
{
    "assembly template"
}
```

Example: a Linux x86_64 syscall shim with one integer argument:

```stark
module Platform.Syscall

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

Assembly declarations must use `unsafe ffi asm(architecture) fn`. The current
surface does not support generic assembly functions, member assembly functions,
`finite`, `law`, `inline`, `noinline`, `hot`, `cold`, `strictfp`, or ordinary
Stark statement bodies. The body is a single string containing the assembly
template.

The `architecture` name is matched against the active target. Supported names
are:

* `x86_64` or `amd64`
* `aarch64` or `arm64`
* `riscv64`
* `x86`, `i386`, `i486`, `i586`, or `i686`
* `arm`, `arm32`, or `thumb`

Multiple assembly declarations may share the same function name when each one
targets a different architecture. During compilation, Stark keeps exactly the
declaration that matches the active target and rejects the group if none or
more than one match.

Operands are explicit:

* `in("reg") parameterName` binds a parameter to an input register.
* `out("reg") return` binds the function return value to an output register.
* `clobber("reg1", "reg2")` lists registers the template may modify.

Non-void assembly functions must bind exactly one return value with
`out("reg") return`. Void assembly functions must not bind `return`.

The current LLVM emission path supports direct return bindings only. The grammar
also accepts `out("reg") parameterName` for output parameters, but source
assembly bodies using non-return outputs are not fully implemented yet and
should not be used in portable code.

Assembly parameters may be integer scalars, floating point scalars, or raw
pointers. Assembly return types may be one of those types or `void`. Text views,
borrows, slices, structs, records, enums, dynamic storage, and ordinary Stark
owned objects must be handled by a safe wrapper around the assembly boundary.

Register classes are checked. Integer and raw pointer operands must use
general-purpose registers. Floating point operands must use floating point
registers. For example, x86_64 integer values can bind `rax`, `rdi`, `rsi`,
`rdx`, `r8`, and similar general-purpose registers, while `f32` and `f64`
values use `xmm` registers.

Assembly declarations are foreign boundaries. They do not receive the default
Stark non-overlap contract for memory-backed parameters, so write explicit
`where disjoint(...)`, `where overlap(...)`, or `where same(...)` contracts when
the wrapper needs those facts.

Calls to assembly functions require an unsafe context:

```stark
unsafe fn i64[min max] RawSyscall1(i64[min max] number, i64[min max] value)
{
    return Syscall1(number, value);
}
```

The compiler lowers root source assembly bodies to LLVM inline assembly with
side effects. It also adds an implicit `memory` clobber, and on x86/x86_64 it
adds the standard direction-flag, floating-point-status, and flags clobbers.

### 13.3 Unsafe Operations

Stark's unsafe model marks proof boundaries rather than disabling the language's ordinary safety rules.

Unsafe code may perform only operations that are explicitly gated as unsafe. Ownership, initialization, range, type, and ordinary borrow validation still apply inside unsafe code.

Dynamic sparse-slot proofs are one such unsafe operation. The proof asserts that a particular dynamic storage slot is initialized even though safe code cannot show that fact from the dense `0..Length` prefix. That assertion stays inside the unsafe boundary; later safe code must re-establish ordinary dense-prefix proof before relying on it.

Use `unsafe fn` for functions whose contract depends on caller-proven raw memory or ABI invariants. Use an `unsafe { ... }` block when a safe API has a small audited unsafe step:

```stark
unsafe fn rawmutptr<T> FromAddress<T>(i64[0 max] address);

fn void UseAddress(i64[0 max] address)
{
    unsafe
    {
        stack rawmutptr<State> state = FromAddress<State>(address);
    }
}
```

The following require an unsafe context:

* declaring a function with `rawptr` or `rawmutptr` in its signature
* declaring FFI or assembly functions
* declaring local raw pointer variables
* using `&` to form a raw address
* using `*` to dereference a raw pointer
* converting to or from raw pointer types, including pointer/integer conversions
* constructing raw slices with `slice(pointer, count)`
* calling unsafe functions
* erasing unsafe function items to ordinary `fnptr` values for callback ABI use
* using unsafe capture modes such as `capture(unsafe addr value)`

Unsafe operation markers may also appear at the operation that crosses the proof boundary:

```stark
RegisterCallback(capture(unsafe addr token) () =>
{
    return 0;
});
```

FFI imports and assembly declarations must be declared `unsafe`. Raw pointers should normally be avoided outside FFI, OS/platform, allocator/runtime, and tightly audited low-level code. Prefer borrows, slices, `dynamic` storage, owned handle types, or platform wrappers for ordinary APIs.

An unsafe function item may be promoted to a function pointer only when the target `fnptr` type also carries `unsafe`, and only inside an unsafe context. Promoting an unsafe item to an ordinary safe `fnptr<...>` (one without `unsafe`) is rejected (STK3024), because that would hide the unsafe requirement from later indirect calls. This is intended for ABI callback registration where the platform API stores a plain function pointer:

```stark
public unsafe ffi(c) fn i32[min max] puts(rawptr<i8[min max]> text);

unsafe fn void Register()
{
    unsafe
    {
        stack fnptr<unsafe ffi(c) fn i32[min max](rawptr<i8[min max]>)> cb = puts;
    }
    return;
}
```

After promotion, the call site still needs an unsafe context to invoke the pointer, and the programmer owns the proof that the callback is invoked only under the unsafe function's documented invariants.

### 13.4 C Primitive Type Aliases

C headers spell integer and pointer-size types as `int`, `long`, `size_t`, and so on, whose widths depend on the target's C data model. `System.C` provides compiler-known aliases for those spellings so FFI declarations mirror the header instead of hard-coding a width:

| Alias | C spelling | Alias | C spelling |
|---|---|---|---|
| `System.C.c_char` | `char` | `System.C.c_ulong` | `unsigned long` |
| `System.C.c_schar` | `signed char` | `System.C.c_longlong` | `long long` |
| `System.C.c_uchar` | `unsigned char` | `System.C.c_ulonglong` | `unsigned long long` |
| `System.C.c_short` | `short` | `System.C.c_size_t` | `size_t` |
| `System.C.c_ushort` | `unsigned short` | `System.C.c_ptrdiff_t` | `ptrdiff_t` |
| `System.C.c_int` | `int` | `System.C.c_void` | `void` (raw pointee only) |
| `System.C.c_uint` | `unsigned int` | | |
| `System.C.c_long` | `long` | | |

These are **target-resolved aliases**: the compiler maps each one onto an ordinary Stark sized primitive for the active target before layout, type checking, and codegen. They do not create a second integer type system, hidden conversions, or distinct ABI identities. For example, `System.C.c_int` resolves to `i32[min max]`, and `System.C.c_long` resolves to `i32[min max]` on ILP32/LLP64 targets but `i64[min max]` on LP64 targets. Two aliases that resolve to the same primitive are the same runtime type on that target.

```stark
public unsafe ffi(c) fn System.C.c_int close(System.C.c_int fd);

public unsafe ffi(c) fn System.C.c_size_t strlen(rawptr<System.C.c_char> text);

public unsafe ffi(c) fn rawmutptr<System.C.c_void> malloc(System.C.c_size_t bytes);

public unsafe ffi(c) fn void free(rawmutptr<System.C.c_void> ptr);
```

After a value enters Stark logic, prefer a stable Stark type. The conversion point is explicit. Because the C-style cast parser does not accept a qualified `System.C` alias as a cast target, leave the platform-width surface by **binding into a typed local** (or casting the alias value to a Stark width), rather than writing `(System.C.c_long)value`:

```stark
unsafe ffi(c) fn System.C.c_long PlatformCount();

unsafe fn i64[min max] StableCount()
{
    stack System.C.c_long nativeCount = PlatformCount();
    return (i64[min max])nativeCount;        // cast FROM the alias to a Stark width
}
```

`c_char` signedness is target-dependent. Plain `System.C.c_char` follows the target's `char` signedness (exposed as the compile-time bool `System.C.c_char_is_signed`); `System.C.c_schar` is always signed 8-bit and `System.C.c_uchar` is always unsigned 8-bit. Use `c_char` only where the header says `char`. For byte buffers that are not text, use `System.C.c_uchar` or `u8[0 max]`. `c_char` signedness is a C ABI fact, not an encoding fact, and there is no implicit conversion between `rawptr<System.C.c_char>` and Stark `ascii`.

`System.C.c_void` is an incomplete foreign pointee. It is valid only as the direct pointee of a raw pointer (`rawptr<System.C.c_void>` / `rawmutptr<System.C.c_void>`); using it as a value, field, array element, or function return is a compile-time error (STK3050). C functions that return `void` use Stark's ordinary `void` return type. Conversions between `rawptr<System.C.c_void>` and a typed raw pointer are explicit raw pointer conversions and stay unsafe.

For ABI aggregates, C aliases preserve layout intent; the layout engine resolves each field alias for the active target before computing offsets (see 8.7):

```stark
[StructLayout(C)]
struct StatLike
{
    System.C.c_ulong Device;
    System.C.c_ulong Inode;
    System.C.c_long Size;
}
```

### 13.5 C Strings

A C `char*` is a foreign byte pointer with a trailing zero byte, no carried length, and nullable raw-pointer behavior. It is not a Stark `ascii` or `unicode` view, and there is no implicit conversion in either direction. `System.C` models C strings explicitly:

* `CStr`: a non-owning, validated null-terminated view (`Data` non-null, `Length` excludes the terminator, no interior zero).
* `OwnedCStr`: Stark-allocated, always-null-terminated storage; the normal way to pass Stark text to a C `const char*`. Its destructor frees the storage.
* `CCharBuffer`: caller-owned mutable storage for C APIs that write into a provided `char*`. `Capacity` includes the terminator slot.
* `ForeignOwnedCStr`: a wrapper over a pointer owned by a *foreign* allocator, used while copying the string into Stark text and then releasing it with the matching foreign dispose function.
* `CStringResult<T>` / `CStringError`: the result and error vocabulary for these operations.

Construction goes through checked or explicitly unsafe functions so invalid C-string states are not ordinary user-constructible values. `OwnedCStr` is only for Stark-allocator memory; never adopt a foreign pointer into it.

**Stark text to a C string.** `System.C.FromAscii` / `FromUnicodeUtf8` allocate `Length + 1` bytes, append the terminator, and reject interior zero bytes (`CStringError.InteriorNull`). Pass the raw pointer with `OwnedCStr.Data()` (unsafe, because it exposes the raw boundary):

```stark
public unsafe ffi(c) fn System.C.c_int puts(rawptr<System.C.c_char> text);

fn System.C.CStringResult<System.C.c_int> Puts(ascii text)
{
    stack System.C.OwnedCStr cText = try System.C.FromAscii(text);
    unsafe
    {
        return System.C.CStringResult<System.C.c_int>.Ok(puts(cText.Data()));
    }
}
```

**A raw C string to Stark text.** Validate first. `System.C.TryFromRawBounded(data, maxLength)` scans at most `maxLength` bytes for the terminator and returns a borrowed `CStr` (or an error for `null`, no terminator within the bound, or an unrepresentable length); `FromRawUnchecked` stays unsafe for call sites that already hold the proof. `System.C.ToAscii` / `ToUnicodeUtf8` then copy a validated `CStr` into owned Stark text, validating UTF-8 (`CStringError.InvalidUtf8`) and reporting allocation failure as a value.

**Caller-provided output buffers.** Allocate a `CCharBuffer` with `NewCCharBuffer`, hand its `Data()` to the C API, then validate with `TryAsCStr`. Because the C-style cast parser does not accept a qualified C alias as a cast target, bind the capacity into a `c_size_t` local instead of writing `(System.C.c_size_t)buffer.Capacity()`:

```stark
public unsafe ffi(c) fn rawmutptr<System.C.c_char> getcwd(
    rawmutptr<System.C.c_char> buffer,
    System.C.c_size_t size);

fn System.C.CStringResult<System.Text.OwnedAscii> CurrentDirectory()
{
    stack mut System.C.CCharBuffer buffer = try System.C.NewCCharBuffer(4096);
    unsafe
    {
        stack System.C.c_size_t capacity = buffer.Capacity();
        if (getcwd(buffer.Data(), capacity) == null)
        {
            return System.C.CStringResult<System.Text.OwnedAscii>.Err(
                System.C.CStringError.NullPointer);
        }
    }

    unsafe
    {
        stack System.C.CStr view = try buffer.TryAsCStr();
        return System.C.ToAscii(view);
    }
}
```

**Foreign-owned strings.** A string returned by a foreign allocator must be freed by that allocator. Either write a small owner type whose `drop` calls the matching free function, or use the generic copy-and-dispose helpers, which validate within a caller bound, copy into Stark text, and dispose the foreign pointer exactly once:

```stark
public unsafe ffi(c) fn rawmutptr<System.C.c_char> get_message();
public unsafe ffi(c) fn void dispose_message(rawmutptr<System.C.c_char> text);

fn System.C.CStringResult<System.Text.OwnedAscii> ReadMessage()
{
    unsafe
    {
        stack System.C.CStringResult<System.C.ForeignOwnedCStr> created =
            System.C.TryFromForeignOwnedRaw(get_message());
        switch (created)
        {
            case System.C.CStringResult<System.C.ForeignOwnedCStr>.Err(var error):
                return System.C.CStringResult<System.Text.OwnedAscii>.Err(error);
            case System.C.CStringResult<System.C.ForeignOwnedCStr>.Ok(var value):
                stack mut System.C.ForeignOwnedCStr message = value;
                return System.C.CopyForeignOwnedAsciiAndDispose(
                    message,
                    dispose_message,
                    4096);
        }
    }
}
```

The `CStringDisposer` alias (`fnptr<unsafe ffi(c) fn void(rawmutptr<System.C.c_char>)>`) is the C-ABI callback shape these helpers accept. See the `System.C` standard library reference for the full surface.

## 14. Runtime Surface

Stark has no exceptions and no stack unwinding.

The runtime contract:

* recoverable errors are represented as ordinary values
* panic, assert, and failure paths are unrecoverable and do not unwind
* unrecoverable failure terminates execution through a trap or abort style path
* the canonical safe hosted entrypoint is `export fn i32[min max] main()`
* `main` only needs `unsafe` or `ffi` when its signature/body uses unsafe or
  foreign boundary features, such as raw hosted `argc`/`argv`
* normal process termination happens by returning from `main`
* foreign unwinding into or through Stark code is unsupported

## 15. Closed by Default

Stark is designed with a closed by default bias. For ordinary Stark code, that means the language leans toward static structure and explicit boundaries.

The programmer facing consequences:

* direct calls by default
* restrictive visibility by default
* most declarations stay inside module or package boundaries unless deliberately exposed
* open world and dynamic patterns are explicit choices, not the default style

Additional rationale for how Stark uses this model lives in [LanguageInternals.md](../Internals/LanguageInternals.md).
