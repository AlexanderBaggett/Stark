+++
title = "27. Integer, Floating-Point, and Overflow Policy"
weight = 270
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/26-memory-layout-abi/"
next = "/book/28-reading-diagnostics/"
+++

# Integer, Floating-Point, and Overflow Policy

This chapter covers numeric behavior as part of the performance contract.

{{< stark-sample "assets/book/samples/numeric-policy.stark" >}}

## Ranged Integers

Runtime integer types name both a width family and a range:

```stark
i32[0 100]
u8[0 255]
i64[min max]
```

Ranges help Stark prove facts about calls, branches, indexing, and arithmetic.
They also make narrowing and widening visible when a value leaves one range and
enters another.

## Ordinary Overflow Is Not A Feature

Ordinary signed or unsigned overflow is not the way to request wrapping
behavior. If code needs wrapping, write wrapping operations:

```stark
value +% step
value -% step
value *% factor
```

The compound forms are available too:

```stark
value +%= step;
```

## Saturating Arithmetic

Use saturating operators when the result should clamp at the numeric boundary:

```stark
value +| step
value -| step
value *| factor
```

The sample contrasts `+%` and `+|` at the signed 32-bit maximum.

## Conversions

Integer widening, narrowing, integer/float conversion, and raw-pointer/integer
conversion are explicit. Stark avoids hiding representation changes in ordinary
assignment.

If a conversion can lose range information or change representation, make it
visible with an explicit cast or helper API.

## Floating Point And `strictfp`

By default, floating-point code stays optimizer-friendly. Use `strictfp` when a
function needs strict IEEE-style floating-point behavior:

```stark
strictfp finite law f32 StrictAdd(f32 left, f32 right) {
    return left + right;
}
```

This keeps the tradeoff source-visible. Most code can use the faster default;
code that needs strict floating-point semantics says so at the function
boundary.
