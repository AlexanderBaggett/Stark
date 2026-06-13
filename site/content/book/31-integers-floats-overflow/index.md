+++
title = "31. Integer, Floating-Point, and Overflow Policy"
weight = 310
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/30-memory-layout-abi/"
next = "/book/32-performance-tuning/"
aliases = ["/book/27-integers-floats-overflow/", "/book/28-integers-floats-overflow/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"
+++

# Integer, Floating-Point, and Overflow Policy

This chapter turns numeric choices into a sequence: choose the range, choose
the overflow policy, make conversions explicit, then decide whether floating
point needs strict behavior.

{{< stark-sample "samples/numeric-policy.stark" >}}

## Step 1: Choose The Integer Range First

Runtime integer types name both a width family and a range:

```stark
i32[0 100]
u8[0 255]
i64[min max]
u128[0 max]
i24[-100 100]
```

The width chooses storage. The range chooses the values this particular value
is allowed to hold. Stark supports signed and unsigned integer widths, and the
range is always written on runtime integer types.

Ranges are part of the type. They tell readers which values are valid at this
point in the program, and they make narrowing or widening visible when a value
leaves one range and enters another.

Use the narrowest honest range at API boundaries:

```stark
finite law u8[0 100] Percent(u8[0 100] value)
{
    return value;
}
```

Use `min max` when the whole storage range is allowed:

```stark
finite law i32[min max] KeepWholeI32(i32[min max] value)
{
    return value;
}
```

`min` and `max` are relative to the integer family you wrote:

```stark
u8[0 max]       // 0 through 255
i8[min max]    // -128 through 127
u32[min max]   // same value range as u32[0 max]
i32[-10 10]
```

For unsigned integers, `min` means `0`. For signed integers, `min` means the
negative storage minimum for that width.

Range endpoints may use compile-time arithmetic:

```stark
stack u16[0 10 * 10] percent = 80;
stack i32[-(2 ** 10) (2 ** 10) - 1] signedWindow = 12;
stack u32[0 1024 * 1024] bufferBytes = 4096;
```

The endpoint operators are ordinary integer expressions over compile-time
values:

```stark
u32[0 (4 * 1024) - 1]
i32[-(2 ** 15) (2 ** 15) - 1]
u64[0 (10 ** 9) + 7]
```

Scalar integer constants are different. Let Stark infer the exact value unless
the width itself matters:

```stark
const Answer = 42;
const u8 MaxSmall = 255;
```

Use unsigned widths for non-negative ranges:

```stark
u8[0 max]
u16[0 1000]
u64[0 max]
```

Use signed widths only when negative values are part of the source meaning:

```stark
i32[-100 100]
i64[min max]
```

## Step 2: Use Ordinary Arithmetic For Ordinary Numbers

Ordinary arithmetic is the right default when overflow is not part of the
operation's meaning:

```stark
left + right
left - right
left * right
left / right
left % right
```

Use the same operators in compound assignments when the target should be
updated in place:

```stark
total += value;
remaining -= used;
scale *= factor;
count /= divisor;
count %= bucketSize;
```

Division and remainder should be written only when the divisor is valid for
the value range you are working with. If zero is not a valid divisor, put that
fact in the type:

```stark
finite law u32[0 max] Average(
    u32[0 max] total,
    u32[1 max] count
)
{
    return total / count;
}
```

For exponentiation, use `**`:

```stark
const PageSize = 2 ** 12;

finite law u32[0 max] Square(u32[0 max] value)
{
    return value ** 2;
}
```

The operator `^` is bitwise XOR, not exponentiation.

## Step 3: Spell Wrapping Arithmetic Deliberately

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
value -%= step;
value *%= factor;
```

Use wrapping for counters, hashes, checksums, and other APIs where wraparound
is the intended result:

```stark
finite law u32[0 max] RotateCounter(u32[0 max] value)
{
    return value +% 1;
}
```

Use wrapping only where the wrap is meaningful to the caller:

```stark
finite law u32[0 max] Mix(u32[0 max] hash, u32[0 max] value)
{
    return (hash *% 16777619) +% value;
}
```

Wrapping negation has its own unary form:

```stark
finite law i32[min max] NegateWrapping(i32[min max] value)
{
    return -%value;
}
```

## Step 4: Use Saturating Arithmetic When Clamping Is The Rule

Use saturating operators when the result should clamp at the numeric boundary:

```stark
value +| step
value -| step
value *| factor
```

The sample contrasts `+%` and `+|` at the signed 32-bit maximum.

The compound forms mirror the ordinary assignment style:

```stark
value +|= step;
value -|= step;
value *|= factor;
```

Use saturating arithmetic when the API is about bounded quantities:

```stark
finite law u8[0 100] AddProgress(u8[0 100] current, u8[0 100] step)
{
    return current +| step;
}
```

Saturating subtraction is useful for counters that should stop at zero:

```stark
finite law u8[0 100] SpendEnergy(u8[0 100] current, u8[0 100] amount)
{
    return current -| amount;
}
```

Do not use saturating operators when wraparound is the required behavior, and
do not use wrapping operators when clamping is the required behavior.

## Step 5: Use Bitwise And Shift Operators For Bits

Bitwise operators are ordinary integer operations:

```stark
value & mask
value | flag
value ^ flag
~value
value << shift
value >> shift
```

The compound forms are available for the binary bitwise operators:

```stark
flags &= mask;
flags ^= toggle;
flags |= option;
```

Use bitwise operators when the value is meant to be treated as bits:

```stark
finite law u32[0 max] SetLowByte(u32[0 max] value, u32[0 255] low)
{
    return (value & 0xFFFFFF00) | low;
}
```

Use parentheses when mixing arithmetic and bitwise operations:

```stark
finite law u32[0 max] PackBytes(u32[0 255] high, u32[0 255] low)
{
    return (high << 8) | low;
}
```

Shift counts must make sense for the width being shifted. When an API accepts
a shift count, give that count a range:

```stark
finite law u32[0 max] ShiftLeft(u32[0 max] value, u8[0 31] amount)
{
    return value << amount;
}
```

## Step 6: Make Representation Changes Visible

Integer widening, narrowing, integer/float conversion, and raw-pointer/integer
conversion are explicit. Stark avoids hiding representation changes in ordinary
assignment.

If a conversion can lose range information or change representation, make it
visible with an explicit cast or helper API.

Typical examples are:

```stark
stack i64[min max] wide = (i64)value;
stack i32[min max] narrowed = (i32)wide;
stack f64 floating = (f64)wide;
```

Integer-to-integer casts should be paired with a source range that makes the
conversion valid:

```stark
finite law i32[0 10] NarrowKnownSmall(i64[0 10] value)
{
    return (i32[0 10])value;
}
```

Use explicit casts for float/integer boundaries:

```stark
stack f32 ratio = (f32)count / 100.0f;
stack i32[min max] rounded = (i32)ratio;
```

Floating-point literal width is visible in the suffix:

```stark
stack f64 wide = 80.0;
stack f32 narrow = 80.0f;
```

Fixed arrays can be converted to slice views explicitly when a callee expects
a view:

```stark
finite law u32[0 max] First(u32[0 max][] values)
{
    return values[0];
}

fn u32[0 max] UseArray()
{
    stack u32[0 max][3] values = [10, 20, 30];
    return First((u32[0 max][])values);
}
```

A bare integer literal mixed into ranged-integer arithmetic is not a hidden
representation change: the literal adopts the other operand's ranged type when its
value fits, so the result keeps that type and needs no cast.

```stark
finite law u64[0 2 ** 63 - 1] Next(u64[0 2 ** 63 - 1] position)
{
    return position + 1;
}
```

Only a value that does not provably fit needs an explicit conversion: a runtime
value of a wider range, or a literal too large or wrong-signed for the operand.

The rejected narrowing example in the book samples shows the important rule:
do not return a whole-range value from a smaller-range function without making
the conversion valid.

{{< stark-sample "rejected/implicit-integer-narrowing.stark" >}}

If the API only accepts `0..10`, put that range on the input too:

```stark
finite law i32[0 10] KeepSmall(i32[0 10] value)
{
    return value;
}
```

## Step 7: Choose Comparison And Conditional Shapes Directly

Comparisons produce `bool`:

```stark
value < limit
value <= limit
value > limit
value >= limit
value == other
value != other
```

Comparison chains read the way they are usually spoken:

```stark
finite law bool InPercentRange(u8[0 100] value)
{
    return 0 <= value <= 100;
}
```

Use the conditional operator when both branches are expressions of the same
result kind:

```stark
finite law u8[0 100] ClampPercent(u8[0 max] value)
{
    return value > 100 ? 100 : value;
}
```

Use `if`/`else` when the operation is easier to read as statements:

```stark
finite law u8[0 100] ClampPercentWithIf(u8[0 max] value)
{
    if (value > 100)
    {
        return 100;
    }

    return value;
}
```

## Step 8: Opt Into `strictfp` Only When The API Needs It

By default, floating-point code uses Stark's fast math rules. Use `strictfp`
when a function needs strict IEEE-style floating-point behavior:

```stark
strictfp finite law f32 StrictAdd(f32 left, f32 right)
{
    return left + right;
}
```

This keeps the tradeoff source-visible. Most code can use the faster default;
code that needs strict floating-point semantics says so at the function
boundary.

Use the default for ordinary numeric work:

```stark
finite law f32 FastAdd(f32 left, f32 right)
{
    return left + right;
}
```

Use `strictfp` when bit-for-bit floating-point behavior is part of the API:

```stark
strictfp finite law f32 StrictAverage(f32 left, f32 right)
{
    return (left + right) / 2.0f;
}
```

Keep the whole function `strictfp` when strict behavior is part of the API. Do
not mix strict and fast floating-point expectations inside one helper.

Floating-point arithmetic uses the ordinary arithmetic operators:

```stark
finite law f64 Ratio(f64 used, f64 total)
{
    return used / total;
}

finite law f32 Scale(f32 value, f32 factor)
{
    return value * factor;
}
```

Use `strictfp` for code where the caller expects strict floating-point rules:

```stark
strictfp finite law f64 StrictRatio(f64 used, f64 total)
{
    return used / total;
}
```
