# `System.BitOperations`

`System.BitOperations` provides the first integer bit-manipulation slice for Stark's standard library.

## Surface

```stark
public finite law i32 LeadingZeroCount(i32 value);
public finite law i64 LeadingZeroCount(i64 value);
public finite law i32 TrailingZeroCount(i32 value);
public finite law i64 TrailingZeroCount(i64 value);
public finite law i32 PopCount(i32 value);
public finite law i64 PopCount(i64 value);
public finite law i32 RotateLeft(i32 value, i32 amount);
public finite law i64 RotateLeft(i64 value, i64 amount);
public finite law i32 RotateRight(i32 value, i32 amount);
public finite law i64 RotateRight(i64 value, i64 amount);
```

## Behavior

- The current implementation is a compiler-provided builtin surface, not a handwritten Stark body.
- `LeadingZeroCount` lowers to `@llvm.ctlz.*` with `is_zero_undef = false`, so zero returns the full bit width.
- `TrailingZeroCount` lowers to `@llvm.cttz.*` with `is_zero_undef = false`, so zero returns the full bit width.
- `PopCount` lowers to `@llvm.ctpop.*`.
- `RotateLeft` and `RotateRight` lower to `@llvm.fshl.*` and `@llvm.fshr.*`.
- The current slice supports `i32` and `i64`.
- The backend is responsible for selecting the target instruction or short instruction sequence that matches the active target.

## Example

```stark
import System
module App

export ffi fn i32 main() {
    stack i32 value = 1;
    stack i32 leading = System.BitOperations.LeadingZeroCount(value);
    stack i32 rotated = System.BitOperations.RotateLeft(value, 31);
    return leading == 31 && rotated == -2147483648 ? 0 : 1;
}
```

## Current Status

- The Milestone 7.5 `System.BitOperations` hardware/compiler-intrinsic batch is implemented for `LeadingZeroCount`, `TrailingZeroCount`, `PopCount`, and `RotateLeft/Right`.
