+++
title = "25. Math Helpers"
weight = 250
book_part = "Part IV: The Standard Library"
book_status = "draft"
prev = "/book/24-threading-tcp/"
next = "/book/26-bit-operations/"

[[stdlib_refs]]
title = "System.Math"
href = "/reference/standard-library/System.Math/"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Math Helpers

`System.Math` is the standard place for scalar floating-point math and the
small deterministic pseudo-random generator in the standard library. Start with
the operation you want to express, choose `f32` or `f64`, then keep fallible or
stateful behavior visible in the source.

{{< stark-sample "assets/book/stdlib-samples/math-helpers.stark" >}}

The snippets below assume the module has been imported:

```stark
import System.Math
```

## Step 1: Choose `f32` Or `f64`

Most math helpers have both `f32` and `f64` overloads:

```stark
stack f32 shortRoot = Sqrt((f32)9.0);
stack f64 longRoot = Sqrt(9.0);
```

Use `f32` when the surrounding data is already single precision, such as packed
simulation state, game coordinates, compact samples, or graphics-style data.
Use `f64` when the calculation benefits from the wider type or when the rest of
the program is already working in `f64`.

Do not mix widths accidentally. Convert at the point where the program changes
precision:

```stark
fn f64 PromoteAndRoot(f32 value)
{
    return Sqrt((f64)value);
}
```

The return type follows the input width:

```stark
fn f32 UnitLengthF32(f32 x, f32 y)
{
    return Sqrt(x * x + y * y);
}

fn f64 UnitLengthF64(f64 x, f64 y)
{
    return Sqrt(x * x + y * y);
}
```

## Step 2: Use Everyday Helpers Directly

`Sqrt`, `Pow`, `Min`, `Max`, and the rounding helpers are the functions most
programs reach for first:

```stark
fn f64 ClampDistance(f64 x, f64 y, f64 limit)
{
    stack f64 distance = Sqrt(x * x + y * y);
    return Min(distance, limit);
}
```

Use `Pow` when the exponent is part of the value-level calculation:

```stark
fn f64 AreaFromRadius(f64 radius)
{
    return 3.141592653589793 * Pow(radius, 2.0);
}
```

Use `Floor`, `Ceiling`, `Truncate`, and `Round` based on the value you want:

```stark
stack f64 down = Floor(3.8);
stack f64 up = Ceiling(3.2);
stack f64 towardZero = Truncate(-3.8);
stack f64 nearest = Round(3.5);
```

`Min` and `Max` make bounds readable:

```stark
fn f32 Clamp01(f32 value)
{
    return Max((f32)0.0, Min(value, (f32)1.0));
}
```

## Step 3: Use `Sin` And `Cos`, Then `SinCos` When You Need Both

Use `Sin` and `Cos` directly when the program needs one result:

```stark
fn f64 VerticalOffset(f64 angle, f64 radius)
{
    return Sin(angle) * radius;
}

fn f64 HorizontalOffset(f64 angle, f64 radius)
{
    return Cos(angle) * radius;
}
```

When a calculation needs both sine and cosine of the same angle, call `SinCos`
once and read the named fields:

```stark
fn f64 RotatedX(f64 x, f64 y, f64 angle)
{
    stack SinCosF64 pair = SinCos(angle);
    return x * pair.Cos - y * pair.Sin;
}

fn f64 RotatedY(f64 x, f64 y, f64 angle)
{
    stack SinCosF64 pair = SinCos(angle);
    return x * pair.Sin + y * pair.Cos;
}
```

The `f32` form returns `SinCosF32`:

```stark
stack SinCosF32 pair = SinCos((f32)0.0);
stack f32 sine = pair.Sin;
stack f32 cosine = pair.Cos;
```

Teach and read this as a convenience: `Sin` and `Cos` are the basic operations;
`SinCos` is the shape for code that naturally needs both values together.

## Step 4: Use `FusedMultiplyAdd` When The Source Operation Is One Expression

`FusedMultiplyAdd(left, right, addend)` expresses `left * right + addend` as one
operation in your source:

```stark
fn f64 Linear(f64 scale, f64 value, f64 offset)
{
    return FusedMultiplyAdd(scale, value, offset);
}
```

Use it for polynomial steps, dot-product pieces, interpolation, and other
places where the source idea really is "multiply, then add":

```stark
fn f32 Lerp(f32 start, f32 end, f32 t)
{
    return FusedMultiplyAdd(end - start, t, start);
}
```

The estimate helpers are `f32` only:

```stark
stack f32 inv = ReciprocalEstimate(value);
stack f32 invRoot = ReciprocalSqrtEstimate(value);
```

Use estimates only when approximation is acceptable for the algorithm. For an
ordinary exact-looking division or square root, use `/` or `Sqrt`.

## Step 5: Use `XorShift32` For Fast Pseudo-Random Values

`XorShift32` is deterministic pseudo-random state. It is useful for
repeatable simulations, randomized tests, sampling, and simple procedural
variation.

Create it with the default constructor or an explicit seed:

```stark
stack mut XorShift32 defaultRandom = new();
stack mut XorShift32 random = XorShift32.Seeded(12345);
```

Use an explicit seed when reproducibility matters:

```stark
fn u32[0 max] FirstRoll(u32[0 max] seed)
{
    stack mut XorShift32 random = XorShift32.Seeded(seed);
    return random.NextU32();
}
```

The generator exposes three common result shapes:

```stark
stack u32[0 max] bits = random.NextU32();
stack i32[min max] signedBits = random.NextI32();
stack f32 unit = random.NextF32();
```

`NextF32()` produces a value in the half-open range `[0.0, 1.0)`, which is the
usual input shape for sampling:

```stark
fn f32 RandomBetween(mut borrow XorShift32 random, f32 low, f32 high)
{
    return low + (high - low) * random.NextF32();
}
```

You can reseed an existing generator:

```stark
random.Reseed(12345);
```

Use `CurrentState()` for saving or checking deterministic generator state:

```stark
stack u32[0 max] checkpoint = random.CurrentState();
```

A zero seed is normalized to the default nonzero state, so the generator does
not get stuck producing zero forever:

```stark
stack mut XorShift32 random = XorShift32.Seeded(0);
return random.CurrentState() != 0;
```

Do not use `XorShift32` for secrets, session tokens, passwords, key material, or
security decisions. It is pseudo-random and repeatable by design.

## Step 6: Keep The Full Math Surface Nearby

The chapter examples focus on the operations most programs use day to day. The
rest of `System.Math` follows the same overload pattern.

| Purpose | Signatures |
| --- | --- |
| Basic trig | `f32 Sin(f32 value)`, `f64 Sin(f64 value)`, `f32 Cos(f32 value)`, `f64 Cos(f64 value)`, `f32 Tan(f32 value)`, `f64 Tan(f64 value)` |
| Inverse trig | `f32 Asin(f32 value)`, `f64 Asin(f64 value)`, `f32 Acos(f32 value)`, `f64 Acos(f64 value)`, `f32 Atan(f32 value)`, `f64 Atan(f64 value)`, `f32 Atan2(f32 y, f32 x)`, `f64 Atan2(f64 y, f64 x)` |
| Exponential and logs | `f32 Exp(f32 value)`, `f64 Exp(f64 value)`, `f32 Exp2(f32 value)`, `f64 Exp2(f64 value)`, `f32 Log(f32 value)`, `f64 Log(f64 value)`, `f32 Log2(f32 value)`, `f64 Log2(f64 value)`, `f32 Log10(f32 value)`, `f64 Log10(f64 value)` |
| Powers and roots | `f32 Pow(f32 value, f32 exponent)`, `f64 Pow(f64 value, f64 exponent)`, `f32 Sqrt(f32 value)`, `f64 Sqrt(f64 value)` |
| Hyperbolic | `f32 Sinh(f32 value)`, `f64 Sinh(f64 value)`, `f32 Cosh(f32 value)`, `f64 Cosh(f64 value)`, `f32 Tanh(f32 value)`, `f64 Tanh(f64 value)` |
| Sine and cosine together | `SinCosF32 SinCos(f32 value)`, `SinCosF64 SinCos(f64 value)` |
| Multiply-add | `f32 FusedMultiplyAdd(f32 left, f32 right, f32 addend)`, `f64 FusedMultiplyAdd(f64 left, f64 right, f64 addend)` |
| Rounding | `f32 Ceiling(f32 value)`, `f64 Ceiling(f64 value)`, `f32 Floor(f32 value)`, `f64 Floor(f64 value)`, `f32 Truncate(f32 value)`, `f64 Truncate(f64 value)`, `f32 Round(f32 value)`, `f64 Round(f64 value)` |
| Bounds | `f32 Min(f32 left, f32 right)`, `f64 Min(f64 left, f64 right)`, `f32 Max(f32 left, f32 right)`, `f64 Max(f64 left, f64 right)` |
| Estimates | `f32 ReciprocalEstimate(f32 value)`, `f32 ReciprocalSqrtEstimate(f32 value)` |
