+++
title = "26. Bit Operations"
weight = 260
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/25-math-helpers/"
next = "/book/27-byte-buffers/"

[[stdlib_refs]]
title = "System.BitOperations"
href = "/reference/standard-library/System.BitOperations/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Bit Operations

`System.BitOperations` gives integer code names for common bit-level questions:
how many leading zeroes, how many trailing zeroes, how many one bits, and what
the value looks like after a rotate.

{{< stark-sample "assets/book/stdlib-samples/bit-operations.stark" >}}

The snippets below assume the module has been imported:

```stark
import System.BitOperations
```

## Step 1: Pick The Integer Width

The public bit helpers are available for `i32[min max]` and `i64[min max]`.
Choose the width that matches the value you are working with:

```stark
stack i32[min max] small = 16;
stack i64[min max] large = 16;

stack i32[min max] smallLeading = LeadingZeroCount(small);
stack i64[min max] largeLeading = LeadingZeroCount(large);
```

The functions return the same width they receive. That makes it easy to keep
index arithmetic and masks in one integer family:

```stark
fn i32[min max] UsedBits32(i32[min max] value) {
    return 32 - LeadingZeroCount(value);
}

fn i64[min max] UsedBits64(i64[min max] value) {
    return 64 - LeadingZeroCount(value);
}
```

## Step 2: Count Leading Zeroes, Trailing Zeroes, And One Bits

`LeadingZeroCount(value)` counts zero bits before the highest one bit:

```stark
stack i32[min max] leading = LeadingZeroCount(1);
```

For a 32-bit value, `LeadingZeroCount(1)` is `31`. For a 64-bit value,
`LeadingZeroCount((i64[min max])1)` is `63`.

`TrailingZeroCount(value)` counts zero bits after the lowest one bit:

```stark
stack i32[min max] trailing = TrailingZeroCount(16);
```

`PopCount(value)` counts one bits:

```stark
stack i32[min max] ones = PopCount(7);
```

For zero, the zero-count helpers return the full width:

```stark
stack i32[min max] leadingZero = LeadingZeroCount(0);
stack i32[min max] trailingZero = TrailingZeroCount(0);
```

That means `leadingZero == 32` and `trailingZero == 32` for `i32`. For `i64`,
the corresponding value is `64`.

Use these helpers when the bit pattern itself is the data:

```stark
fn bool HasExactlyOneFlag(i32[min max] flags) {
    return PopCount(flags) == 1;
}
```

## Step 3: Rotate When Bits Wrap Around

Shifts discard bits. Rotates move the shifted-out bits around to the other end.
Use `RotateLeft` and `RotateRight` when the wraparound is part of the algorithm:

```stark
fn i32[min max] Mix32(i32[min max] value) {
    return RotateLeft(value, 5) ^ value;
}
```

Rotate amounts use the same integer width as the value:

```stark
stack i64[min max] rotated =
    RotateRight((i64[min max])8, (i64[min max])1);
```

Rotates are common in hash mixing, checksums, compact pseudo-random transforms,
and ring-style bit layouts. Do not replace a shift with a rotate unless the
shifted-out bits should stay in the value.

## Step 4: Use Bit Helpers For Masks And Compact Indexes

Bit operations are most readable when wrapped in a small domain helper.

For flags:

```stark
finite law bool HasReadAndWrite(i32[min max] flags) {
    stack i32[min max] read = 1;
    stack i32[min max] write = 2;
    return (flags & (read | write)) == (read | write);
}
```

For power-of-two capacity checks:

```stark
finite law bool IsPowerOfTwo(i32[min max] value) {
    return value > 0 && PopCount(value) == 1;
}
```

For choosing a compact bucket number from a nonzero capacity:

```stark
finite law i32[min max] HighestBitIndex(i32[min max] value) {
    return 31 - LeadingZeroCount(value);
}
```

For ring-buffer indexes with power-of-two capacity:

```stark
finite law i32[min max] NextIndex(i32[min max] index, i32[min max] capacityMask) {
    return (index + 1) & capacityMask;
}
```

Keep the mask or capacity invariant close to the helper. That makes the bit
operation read as part of the data structure rather than as a loose trick.

## Step 5: Keep The Full Bit Surface Nearby

| Purpose | Signatures |
| --- | --- |
| Leading zero count | `i32[min max] LeadingZeroCount(i32[min max] value)`, `i64[min max] LeadingZeroCount(i64[min max] value)` |
| Trailing zero count | `i32[min max] TrailingZeroCount(i32[min max] value)`, `i64[min max] TrailingZeroCount(i64[min max] value)` |
| One-bit count | `i32[min max] PopCount(i32[min max] value)`, `i64[min max] PopCount(i64[min max] value)` |
| Rotate left | `i32[min max] RotateLeft(i32[min max] value, i32[min max] amount)`, `i64[min max] RotateLeft(i64[min max] value, i64[min max] amount)` |
| Rotate right | `i32[min max] RotateRight(i32[min max] value, i32[min max] amount)`, `i64[min max] RotateRight(i64[min max] value, i64[min max] amount)` |
