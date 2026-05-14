+++
title = "Appendix D: Function Kinds and Guarantees"
weight = 380
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-c-integer-widths/"
next = "/book/appendix-e-storage-classes/"
+++

# Appendix D: Function Kinds and Guarantees

Stark function kinds are part of the API contract.

{{< stark-sample "assets/book/samples/function-guarantees.stark" >}}

Use the strongest kind that honestly describes the function. Stronger kinds
make APIs easier to reason about, but they are promises, not decoration.

## Quick Reference

- `fn`: general function form
- `finite`: guaranteed progress and return
- `law`: pure/read-only function form with no visible side effects
- `finite law`: both pure/read-only and guaranteed to return

The keyword order is fixed:

```stark
finite law i32[min max] Clamp(i32[min max] value) {
    return value;
}
```

Write `finite law`, not `law finite`.

## Common Choices

Use `finite law` for value-only computations that always return.

Use `finite` for in-memory mutations that always return but are not pure.

Use `fn` for IO, allocation, blocking, synchronization, FFI, process exit, file
operations, TCP operations, and other externally visible work.

Use `law` only when purity matters but progress is not part of the contract.

Stronger function kinds cannot call work that violates their contract. For
example, a `finite law` wrapper cannot hide a general function that reads
shared state:

{{< stark-sample "assets/book/negative-samples/law-calls-fn.stark" >}}

## Function Pointers

`fnptr` signatures carry the function kind:

```stark
fnptr<fn i32[min max](i32[min max])>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law bool(borrow Item)>
```

A callable value must satisfy the kind expected by the function-pointer type.
Stronger values can flow into weaker function-pointer slots, but a weaker
callback cannot satisfy a stronger slot. This is why a `finite law` function can
be stored in `fnptr<fn ...>`, while an ordinary `fn` cannot be stored in
`fnptr<finite law ...>`.

{{< stark-sample "assets/book/negative-samples/fnptr-kind-mismatch.stark" >}}
