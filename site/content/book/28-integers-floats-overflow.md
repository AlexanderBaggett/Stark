+++
title = "28. Integer, Floating-Point, and Overflow Policy"
weight = 280
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/27-memory-layout-abi/"
next = "/book/29-performance-tuning/"
aliases = ["/book/27-integers-floats-overflow/"]
+++

# Integer, Floating-Point, and Overflow Policy

This chapter turns numeric choices into a sequence: choose the range, choose
the overflow policy, make conversions explicit, then decide whether floating
point needs strict behavior.

{{< stark-sample "assets/book/samples/numeric-policy.stark" >}}

## Step 1: Choose The Integer Range First

Runtime integer types name both a width family and a range:

```stark
i32[0 100]
u8[0 255]
i64[min max]
```

Ranges help Stark prove facts about calls, branches, indexing, and arithmetic.
They also make narrowing and widening visible when a value leaves one range and
enters another.

## Step 2: Spell Wrapping Arithmetic Deliberately

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

## Step 3: Use Saturating Arithmetic When Clamping Is The Rule

Use saturating operators when the result should clamp at the numeric boundary:

```stark
value +| step
value -| step
value *| factor
```

The sample contrasts `+%` and `+|` at the signed 32-bit maximum.

## Step 4: Make Representation Changes Visible

Integer widening, narrowing, integer/float conversion, and raw-pointer/integer
conversion are explicit. Stark avoids hiding representation changes in ordinary
assignment.

If a conversion can lose range information or change representation, make it
visible with an explicit cast or helper API.

## Step 5: Opt Into `strictfp` Only When The API Needs It

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
