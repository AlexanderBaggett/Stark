+++
title = "15. Function Guarantees and Effects"
weight = 150
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/14-modules-visibility-packages/"
next = "/book/16-errors-without-exceptions/"
+++

# Function Guarantees and Effects

Function kinds are a Stark-specific part of the performance model. A function's
kind says more than how to call it. It says what kind of behavior callers may
rely on.

Function declarations also carry modifiers. The kind says what the function
promises; modifiers say how the compiler or ABI should treat that promise:
inlining, hot/cold placement, unsafe proof boundaries, foreign calls, strict
floating point, and type-owned `static` methods.

{{< stark-sample "assets/book/samples/function-guarantees.stark" >}}

## Step 1: Start With The Strongest Honest Kind

Read the four ordinary source-level function kinds as the base contract:

- `fn`: general function form
- `finite`: guaranteed progress and return
- `law`: pure/read-only function form with no visible side effects
- `finite law`: both pure/read-only and guaranteed to return

The keyword order is fixed:

```stark
finite law i32[min max] ClampToZero(i32[min max] value) {
    ...
}
```

Write `finite law`, not `law finite`.

Visibility keywords such as `internal`, `public`, and `export` can appear on
functions too, but they answer a different question: who can see or link the
function. Chapter 14 covers visibility; this chapter focuses on behavior.

## Step 2: Pick The Kind From The Body

Use `finite law` for small deterministic computations over input values. It is
the strongest ordinary promise and usually the best fit for arithmetic helpers,
classifiers, and pure value transformations.

Use `finite` when the function should return but has visible effects or mutates
borrowed state.

Use `law` only when purity matters but progress is not part of the contract.

Use `fn` when the function is general: it might perform IO, call foreign code,
block, allocate, or otherwise not fit the stronger categories.

Function kinds compose from the inside out. A stronger function cannot keep its
promise by calling work that observes or changes state outside that promise:

{{< stark-sample "assets/book/negative-samples/law-calls-fn.stark" >}}

The fix is to move the shared-state work out of the stronger function, make the
helper carry the stronger guarantee honestly, or weaken the caller so its
signature matches what the body actually does.

## Step 3: Add Inlining And Placement Hints Deliberately

Use `inline`, `inlinehint`, and `noinline` for call-shape intent:

```stark
inline finite law i32[min max] ClampToZero(i32[min max] value) {
    if (value < 0) {
        return 0;
    }

    return value;
}

inlinehint finite law i32[min max] AddBias(i32[min max] value) {
    return value + 1;
}

noinline cold fn i32[min max] RareFallback(i32[min max] value) {
    return value - 1;
}
```

`inline` is the strong request for tiny functions that should disappear into
callers. `inlinehint` is softer. `noinline` is for boundaries you want to keep
separate, such as rare paths, diagnostics, or intentionally isolated code.
Only one of those three may appear on a function.

Use `hot` and `cold` for code placement and optimization priority:

```stark
hot fn i32[min max] Choose(bool useClamp, i32[min max] value) {
    if (useClamp) {
        return ClampToZero(value);
    }

    return value;
}
```

`hot` says the path is expected to matter for steady-state performance. `cold`
says the opposite. A function may not be both.

## Step 4: Keep Unsafe And Foreign Boundaries Obvious

Use `unsafe fn` when callers must uphold a proof the compiler cannot derive
from safe Stark alone. Use `ffi` for a foreign ABI declaration:

```stark
unsafe ffi fn i32[min max] native_abs(i32[min max] value);
```

Foreign C variadic APIs add `varargs`, and Stark requires that they remain FFI
declarations rather than Stark function bodies:

```stark
unsafe ffi varargs fn i32[min max] printf(ascii format);
```

Assembly declarations use `asm(architecture)` after the modifiers and before
the function kind. They are low-level foreign boundaries and must be unsafe:

```stark
internal unsafe ffi asm(x86_64) fn i64[min max] LinuxSyscall0(i64[min max] number)
    in("rax") number,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}
```

The important habit is not to hide these words. They are part of the API
contract and part of what later lowering may assume.

## Step 5: Use `strictfp` Only When The Floating-Point Contract Needs It

Ordinary Stark floating point is optimizer-friendly. Add `strictfp` when the
function needs strict IEEE-style behavior instead:

```stark
strictfp finite law f64 AddStrict(f64 left, f64 right) {
    return left + right;
}
```

That spelling is intentionally visible. It tells the reader and optimizer that
this function chose reproducibility rules over the default fast-math model.

## Step 6: Use `static` For Type-Owned Member Functions

`static` is only valid on member functions inside a `struct` or `record`. It
means the function belongs to the type and does not receive `self`:

```stark
struct ScoreMath {
    static inline finite law i32[min max] Double(i32[min max] value) {
        return value * 2;
    }
}

fn i32[min max] Run() {
    return ScoreMath.Double(6);
}
```

Use a static member when the function is part of the type's namespace but does
not need a particular value. Use an instance member with an explicit `self`
parameter when the function reads or mutates one value.

## Step 7: Move Effects To The Signature

Stark does not use hidden exceptions or dynamic dispatch to disguise behavior.
When a function writes through a borrow, fills an `out` destination, performs
IO, allocates, synchronizes, or crosses an FFI boundary, the API should make
that behavior visible.

This is a design habit as much as a syntax rule. Stronger function kinds make
good small APIs easier to reason about. General `fn` remains available for code
that really is general.
