# `System.Math`

`System.Math` provides the first scalar floating-point math slice for Stark's standard library.

## Surface

```stark
public struct SinCosF32 {
    f32 Sin;
    f32 Cos;
}
public struct SinCosF64 {
    f64 Sin;
    f64 Cos;
}
public finite law f32 Sin(f32 value);
public finite law f64 Sin(f64 value);
public finite law f32 Cos(f32 value);
public finite law f64 Cos(f64 value);
public finite law f32 Tan(f32 value);
public finite law f64 Tan(f64 value);
public finite law f32 Exp(f32 value);
public finite law f64 Exp(f64 value);
public finite law f32 Exp2(f32 value);
public finite law f64 Exp2(f64 value);
public finite law f32 Log(f32 value);
public finite law f64 Log(f64 value);
public finite law f32 Log2(f32 value);
public finite law f64 Log2(f64 value);
public finite law f32 Log10(f32 value);
public finite law f64 Log10(f64 value);
public finite law f32 Asin(f32 value);
public finite law f64 Asin(f64 value);
public finite law f32 Acos(f32 value);
public finite law f64 Acos(f64 value);
public finite law f32 Atan(f32 value);
public finite law f64 Atan(f64 value);
public finite law f32 Atan2(f32 y, f32 x);
public finite law f64 Atan2(f64 y, f64 x);
public finite law f32 Pow(f32 value, f32 exponent);
public finite law f64 Pow(f64 value, f64 exponent);
public finite law f32 Sinh(f32 value);
public finite law f64 Sinh(f64 value);
public finite law f32 Cosh(f32 value);
public finite law f64 Cosh(f64 value);
public finite law f32 Tanh(f32 value);
public finite law f64 Tanh(f64 value);
public finite law SinCosF32 SinCos(f32 value);
public finite law SinCosF64 SinCos(f64 value);
public finite law f32 Sqrt(f32 value);
public finite law f64 Sqrt(f64 value);
public finite law f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend);
public finite law f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend);
public finite law f32 Ceiling(f32 value);
public finite law f64 Ceiling(f64 value);
public finite law f32 Floor(f32 value);
public finite law f64 Floor(f64 value);
public finite law f32 Truncate(f32 value);
public finite law f64 Truncate(f64 value);
public finite law f32 Round(f32 value);
public finite law f64 Round(f64 value);
public finite law f32 Min(f32 left, f32 right);
public finite law f64 Min(f64 left, f64 right);
public finite law f32 Max(f32 left, f32 right);
public finite law f64 Max(f64 left, f64 right);
public finite law f32 ReciprocalEstimate(f32 value);
public finite law f32 ReciprocalSqrtEstimate(f32 value);
```

## Behavior

- The current implementation is a compiler-provided builtin surface, not a handwritten Stark body.
- `Sin`, `Cos`, `Tan`, `Exp`, `Exp2`, `Log`, `Log2`, `Log10`, `Asin`, `Acos`, `Atan`, `Atan2`, `Pow`, `Sinh`, `Cosh`, `Tanh`, and `SinCos` lower to the corresponding LLVM math intrinsics.
- `Min` and `Max` lower to `@llvm.minnum.*` and `@llvm.maxnum.*` so the backend can preserve the intended floating-point semantics while still selecting the best target instructions.
- `Sqrt`, `FusedMultiplyAdd`, `ReciprocalEstimate`, `ReciprocalSqrtEstimate`, `Ceiling`, `Floor`, `Truncate`, and `Round` lower to single-instruction inline asm on x86/x64 and AArch64.
- The current slice is scalar-only. Most functions support both `f32` and `f64`; the reciprocal-estimate pair is currently `f32`-only so the shared surface stays aligned with the x86 scalar instruction set.
- `SinCos` returns a small aggregate with `Sin` and `Cos` fields so the surface stays explicit and cheap.
- The x86/x64 rounding family currently assumes SSE4.1 support; `Sqrt`, `ReciprocalEstimate`, and `ReciprocalSqrtEstimate` use SSE scalar instructions.
- `FusedMultiplyAdd` on x86/x64 currently uses `vfmadd213ss`/`vfmadd213sd` and therefore assumes FMA3 support at runtime.
- On non-Windows native links, the LLVM-intrinsic-backed transcendental math
  functions can require `-lm` after LLVM/toolchain lowering.

The `-lm` dependency is inherited through LLVM and the native toolchain. Under
the current dependency criteria, this is not treated as a C-backed
`System.Math` implementation and is not a standard-library replacement target.
Stark still emits optimizer-visible LLVM math intrinsics so LLVM can choose the
best target lowering.

## Example

```stark
import System
module App

export fn i32 main() {
    stack f64 root = System.Math.Sqrt(9.0);
    stack System.Math.SinCosF64 pair = System.Math.SinCos(0.0);
    return root == 3.0 && pair.Sin == 0.0 && pair.Cos == 1.0 ? 0 : 1;
}
```

## Current Status

- The LLVM-intrinsic mappings listed in Milestone 7.5 are implemented.
- The current hardware/compiler-intrinsic batch is implemented for `Sqrt`, `FusedMultiplyAdd`, `ReciprocalEstimate`, `ReciprocalSqrtEstimate`, `Ceiling`, `Floor`, `Truncate`, `Round`, `Min`, and `Max`.
- The transcendental math slice can inherit a libm dependency from LLVM/native
  toolchain lowering on non-Windows targets.
- The remaining integer bit-operations half of the milestone now lives in `System.BitOperations`.
