+++
title = "5. Values, Types, and Ranges"
weight = 50
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/04-small-tour/"
next = "/book/06-bindings-control-flow/"
+++

# Values, Types, and Ranges

Stark integer types carry their range in the source. The range is not a comment
or a documentation habit. It is part of the type.

{{< stark-sample "samples/values-ranges.stark" >}}

`i32[0 100]` says the value uses the signed 32-bit integer family and is known
to be between `0` and `100`. `u8[0 127]` says the value uses the unsigned 8-bit
integer family and is known to fit in the lower half of that storage width.

The common full-range spelling uses `min` and `max`:

```stark
i32[min max]
u64[min max]
```

For signed integer families, `min` and `max` mean the signed bounds for that
width. For unsigned integer families, `min` means `0` and `max` means the
largest value representable by that unsigned width.

## Step 1: Write The Range You Mean

Stark asks you to write ranges because ranges affect ordinary language
semantics:

- whether a literal fits in a destination type
- whether an assignment can be accepted without a conversion
- whether arithmetic has enough information to prove the result shape
- whether an API accepts every value a caller might pass

This keeps range-changing operations visible at the source level. If a value
must be widened, narrowed, reinterpreted, or rounded, the code should say so
instead of hiding that work in an implicit conversion.

For example, a full-range `i32[min max]` cannot be returned as `i32[0 10]`
just because some callers happen to pass small values:

{{< stark-sample "rejected/implicit-integer-narrowing.stark" >}}

The function signature must either accept the narrower range up front or use an
explicit check/conversion path that handles values outside the destination
range.

## Step 2: Use Arithmetic For Larger Endpoints

Range endpoints are compile-time integer expressions. That means you can write
large or structured bounds in the same notation you would use for constants:

```stark
u32[0 2 ** 20 - 1]
u64[0 1024 * 1024 * 1024]
i64[-(2 ** 40) 2 ** 40 - 1]
```

`**` is exponentiation. It is the operator you want for powers of two. `^` is
bitwise XOR, not exponentiation.

Use `min` and `max` for full-width endpoints:

```stark
u32[0 max]
i64[min max]
```

Use arithmetic when the bound is meaningful but not the whole width, such as a
1 MiB limit or a protocol field capped at `2 ** 20 - 1`.

## Step 3: Separate Compile-Time Facts From Runtime Values

Runtime integer values should use explicit ranged source types:

```stark
stack i32[0 100] score = 40;
stack u8[0 127] byteValue = 42;
```

Scalar integer constants are different. A plain numeric constant can be
declared without spelling a runtime range:

```stark
const PageSize = 4096;
const ExpectedAnswer = 42;
```

That form is for compile-time scalar facts. Locals, parameters, fields, and
return values still spell the runtime range they promise.

## Step 4: Add Bool, Text, And Float Values Deliberately

`bool` is the ordinary true/false type and does not carry a numeric range.

Text literals are values too, but text storage and capacity matter. Later
chapters cover `ascii`, `unicode`, `Ascii`, `Unicode`, and owned text in more
detail.

Floating-point code uses the normal function model. If a function needs strict
IEEE-style floating-point behavior, write `strictfp` on the function. Otherwise
Stark keeps the default model performance-oriented.
