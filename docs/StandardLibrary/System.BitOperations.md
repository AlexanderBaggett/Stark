# `System.BitOperations`

`System.BitOperations` provides integer bit-manipulation helpers for Stark's
standard library.

## Surface

```stark
public finite law i32[min max] LeadingZeroCount(i32[min max] value);
public finite law i64[min max] LeadingZeroCount(i64[min max] value);
public finite law i32[min max] TrailingZeroCount(i32[min max] value);
public finite law i64[min max] TrailingZeroCount(i64[min max] value);
public finite law i32[min max] PopCount(i32[min max] value);
public finite law i64[min max] PopCount(i64[min max] value);
public finite law i32[min max] RotateLeft(i32[min max] value, i32[min max] amount);
public finite law i64[min max] RotateLeft(i64[min max] value, i64[min max] amount);
public finite law i32[min max] RotateRight(i32[min max] value, i32[min max] amount);
public finite law i64[min max] RotateRight(i64[min max] value, i64[min max] amount);
```

## Behavior

- `LeadingZeroCount(0)` returns the full bit width: `32` for `i32`, `64` for
  `i64`.
- `TrailingZeroCount(0)` returns the full bit width: `32` for `i32`, `64` for
  `i64`.
- `PopCount` returns the number of one bits in the value.
- `RotateLeft` and `RotateRight` wrap shifted-out bits around to the opposite
  end of the value.
- The current public surface supports `i32[min max]` and `i64[min max]`.

## Example

```stark
import System
module App

export fn i32 main() {
    stack i32[min max] value = 1;
    stack i32[min max] leading = System.BitOperations.LeadingZeroCount(value);
    stack i32[min max] rotated = System.BitOperations.RotateLeft(value, 31);
    return leading == 31 && rotated == -2147483648 ? 0 : 1;
}
```

## Current Status

- `LeadingZeroCount`, `TrailingZeroCount`, `PopCount`, `RotateLeft`, and
  `RotateRight` are available for `i32[min max]` and `i64[min max]`.
