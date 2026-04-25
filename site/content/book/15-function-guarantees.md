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

{{< stark-sample "assets/book/samples/function-guarantees.stark" >}}

## The Four Function Kinds

Stark has four ordinary source-level function kinds:

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

## Choosing A Function Kind

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

## Effects Should Be Visible

Stark does not use hidden exceptions or dynamic dispatch to disguise behavior.
When a function writes through a borrow, fills an `out` destination, performs
IO, allocates, synchronizes, or crosses an FFI boundary, the API should make
that behavior visible.

This is a design habit as much as a syntax rule. Stronger function kinds make
good small APIs easier to reason about. General `fn` remains available for code
that really is general.
