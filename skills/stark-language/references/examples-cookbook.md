# Stark Examples Cookbook

These examples are embedded so the skill remains portable. They are small
patterns, not a replacement for a full project.

## Minimal Program

```stark
module Demo.Hello

export fn i32[min max] main()
{
    return 0;
}
```

## Imports And Console Output

```stark
import System.Console
module Demo.Console

export fn i32[min max] main()
{
    WriteLine("Hello from Stark");
    return 0;
}
```

## Struct And Record

```stark
module Demo.Data

struct Rectangle
{
    i32[min max] Width;
    i32[min max] Height;
}

record Point(i32[min max] X, i32[min max] Y)
{
}

finite law i32[min max] Area(Rectangle rectangle)
{
    return rectangle.Width * rectangle.Height;
}

finite law i32[min max] Manhattan(Point point)
{
    return point.X + point.Y;
}
```

## Enum Result With Switch

```stark
module Demo.Results

enum DivideResult
{
    DivideByZero,
    Ok(i32[min max]),
}

finite law DivideResult Divide(i32[min max] numerator, i32[min max] denominator)
{
    if (denominator == 0)
    {
        return DivideResult.DivideByZero;
    }

    return DivideResult.Ok(numerator / denominator);
}

finite law i32[min max] ValueOrZero(DivideResult result)
{
    switch (result)
    {
        case DivideResult.DivideByZero:
            return 0;
        case DivideResult.Ok(var value):
            return value;
    }
}
```

## Borrow And Mut Borrow

```stark
module Demo.Borrows

struct Counter
{
    i32[min max] Value;
}

finite law i32[min max] Current(borrow Counter counter)
{
    return counter.Value;
}

finite void Add(mut borrow Counter counter, i32[min max] amount)
{
    counter.Value += amount;
    return;
}
```

## Out Parameter

```stark
module Demo.OutParameters

fn bool TryDivide(
    i32[min max] numerator,
    i32[min max] denominator,
    out i32[min max] result)
{
    if (denominator == 0)
    {
        return false;
    }

    result = numerator / denominator;
    return true;
}
```

## Fixed Array And Slice

```stark
module Demo.Arrays

finite law i32[min max] SumThree(i32[min max][3] values)
{
    return values[0] + values[1] + values[2];
}

finite law i32[min max] First(i32[min max][] values)
{
    return values[0];
}

fn i32[min max] UseArray()
{
    stack i32[min max][3] values =
    {
        10, 20, 30
    };

    stack i32[min max][] view = values;
    return SumThree(values) + First(view);
}
```

## Function Pointer

```stark
module Demo.FunctionPointers

finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

fn i32[min max] Apply(
    fnptr<finite law i32[min max](i32[min max])> op,
    i32[min max] value)
{
    return op(value);
}

fn i32[min max] UseApply()
{
    return Apply(Double, 21);
}
```

## Capturing Closure

```stark
module Demo.Closures

inline fn i32[min max] ApplyInline(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] AddOffset(i32[min max] offset)
{
    return ApplyInline(
        32,
        capture(copy offset) (i32[min max] value) => value + offset);
}
```

## Non-Overlap Fast Path

```stark
module Demo.MemoryContracts

fn void CopySeparate(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
{
    for willexit independent (stack mut u64[0 max] index = 0; index < 4; index += 1)
    {
        destination[index] = source[index];
    }

    return;
}

fn bool TryCopyFast(
    borrow u8[0 max][] source,
    borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    if disjoint(source, destination)
    {
        CopySeparate(source, destination);
        return true;
    }

    return false;
}
```

## FFI Wrapper Shape

```stark
module Demo.Native

public enum NativeOpenResult
{
    Err,
    Ok(i32[min max]),
}

unsafe ffi fn i32[min max] native_open();

public unsafe fn NativeOpenResult TryOpenNative()
{
    stack i32[min max] handle = native_open();
    if (handle < 0)
    {
        return NativeOpenResult.Err;
    }

    return NativeOpenResult.Ok(handle);
}
```

## Project Manifest

```toml
[project]
name = "demo"
version = "0.1.0"
kind = "executable"

[executable]
root = "Demo.stark"
output = "demo"

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Do not add a `stdlib` dependency for `System.*` imports; project builds resolve
the bundled standard library automatically.
