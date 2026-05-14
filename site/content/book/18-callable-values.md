+++
title = "18. Callable Values and Thread Entries"
weight = 180
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/17-generics-traits-doctrines/"
next = "/book/19-ffi-raw-pointers-native-packages/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[stdlib_refs]]
title = "System.Threading"
href = "/reference/standard-library/System.Threading/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Callable Values and Thread Entries

Callable values are deliberately narrow while Stark preserves strong guarantees.

{{< stark-sample "assets/book/samples/callable-values.stark" >}}

## Function Items

A named function can be used as a function item:

```stark
fn i32[min max] Add(i32[min max] left, i32[min max] right) {
    return left + right;
}
```

Calling `Add(20, 22)` is still an ordinary direct call. The function has not
become a raw pointer just because it has a name.

## Function Pointers

Use `fnptr` when you want an indirect callable value:

```stark
stack fnptr<fn i32[min max](i32[min max], i32[min max])> add = Add;
```

That line is the explicit point where the function item is promoted to a
function pointer. The type includes the function kind and the parameter/return
types.

Function pointer signatures preserve function-kind obligations:

```stark
fnptr<fn i32[min max](i32[min max])>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

A callable value must satisfy the kind expected by the `fnptr` type.

The kind matters most at higher-order boundaries. A general
`fnptr<fn ...>` can call ordinary code. A `fnptr<law ...>` preserves purity
through an indirect call. A `fnptr<finite ...>` preserves progress and return
guarantees. A `fnptr<finite law ...>` carries both, which lets functional-style
helpers accept callbacks without giving up the guarantees that direct calls
would have given the compiler.

Function pointer values are non-null. They must be initialized from a compatible
function item or non-capturing lambda, so aggregate initializers need to spell out
any field or fixed-array element that contains a `fnptr` instead of relying on
zero-fill.

The declaration on the function item matters. A general `fn` is not silently
upgraded to a stronger callable value just because its body is simple:

{{< stark-sample "assets/book/negative-samples/fnptr-kind-mismatch.stark" >}}

## Non-Capturing Lambdas

Non-capturing lambdas can be used where a matching function pointer is expected:

```stark
stack fnptr<fn i32[min max](i32[min max])> increment =
    (i32[min max] value) => value + 1;
```

This is intentionally narrow. A non-capturing lambda has no hidden environment
and can behave like a plain callable value.

## Capturing Lambdas

Capturing lambdas use an explicit capture list:

```stark
capture(copy scale, read table) (i32[0 max] index) => {
    return table[index] * scale;
}
```

Capture is never implicit. The capture list says whether a value is copied,
moved, read, mutably borrowed, or used as an output/init destination.

The current safe rule of thumb is conservative: use non-capturing lambdas for
ordinary `fnptr` values, and treat capturing lambdas as a feature boundary until
the capture-lowering work is complete for the use case you need.

This intentionally rejected example is checked with the rest of the book:

{{< stark-sample "assets/book/negative-samples/capturing-lambda-fnptr.stark" >}}

The capture list is explicit and type-checked, but today it cannot be lowered
into an ordinary `fnptr` value. Use a named function item or a non-capturing
lambda when a current API expects `fnptr`.

## Thread Entries

Thread entry values are callable values too. Stark keeps them explicit because
threading widens the part of the program that can observe execution and shared
state.

A thread entry should be a plain function item or a callable value whose
capture and sharing behavior is visible. Do not smuggle shared mutable state
through an unmarked closure.
