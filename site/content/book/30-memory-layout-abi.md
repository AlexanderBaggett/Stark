+++
title = "30. Memory Layout, ABI, and Interop Expectations"
weight = 300
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/29-performance-model/"
next = "/book/31-integers-floats-overflow/"
aliases = ["/book/26-memory-layout-abi/", "/book/27-memory-layout-abi/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[language_refs]]
title = "Modules and Visibility"
href = "/reference/language/ModulesAndVisibility/"
+++

# Memory Layout, ABI, and Interop Expectations

This chapter takes a struct or record from ordinary Stark source to an interop
boundary. The tutorial rule is to use source-facing layout for Stark code and
design ABI-facing layout separately.

{{< stark-sample "assets/book/samples/ffi-raw-pointers.stark" >}}

## Step 1: Start With Source-Facing Layout

Some layout details are visible in source because they affect ordinary code:

- a `struct` or `record` has named fields
- a fixed array has a fixed element count
- a slice is a view, not backing storage
- `dynamic T` owns growable element storage
- an `enum` carries exactly one active case
- a raw pointer is pointer-shaped and may be `null`

These details are enough for everyday Stark code to read fields, index arrays,
borrow values, and pass values inside Stark modules and packages.

{{< stark-sample "assets/book/samples/aggregates-layout.stark" >}}

The struct and record sample is source-facing layout: `Rectangle.Width`,
`Rectangle.Height`, `Point.X`, and `Point.Y` are the API the Stark program
uses. That does not mean the same types should automatically be treated as C
ABI records. Source-facing field access and binary representation stability
are separate promises.

Both `struct` and `record` are ordinary named data forms:

```stark
public struct Rectangle
{
    i32[min max] Width;
    i32[min max] Height;
}

public record Point
{
    i32[min max] X;
    i32[min max] Y;
}
```

Use a `struct` when the type represents mutable data or ordinary named storage.
Use a `record` when the type is mainly a simple value bundle. Both forms are
accessed through fields:

```stark
finite law i32[min max] RectangleArea(Rectangle rectangle)
{
    return rectangle.Width * rectangle.Height;
}

finite law i32[min max] Manhattan(Point point)
{
    return point.X + point.Y;
}
```

Fixed arrays own their elements. Slices view backing storage created elsewhere:

```stark
fn i32[min max] ReadFirst()
{
    stack i32[min max][3] values =
    {
        10, 20, 30
    };
    stack i32[min max][] view = values;
    return view[0];
}
```

Do not use a slice type when the function or object needs to own the storage.
Create an owning fixed array, `dynamic T`, owned text, or collection first, then
borrow or slice it where a view is enough.

Use `dynamic T` when the object owns a growable buffer:

```stark
struct IntBuffer
{
    dynamic i32[min max] Items;
}

fn bool Push(mut borrow IntBuffer buffer, i32[min max] value)
{
    if (!buffer.Items.TryReserve(1))
    {
        return false;
    }

    init buffer.Items[buffer.Items.Length] = value;
    return true;
}
```

Use an enum when the value has a small set of named cases:

```stark
public enum ShapeKind
{
    Point,
    Rectangle,
}

finite law i32[min max] ShapeTag(ShapeKind kind)
{
    switch (kind)
    {
        case ShapeKind.Point:
            return 1;
        case ShapeKind.Rectangle:
            return 2;
    }
}
```

## Step 2: Keep Representation Freedom Inside The Package

Ordinary Stark types are not automatically stable C ABI contracts. Keep normal
Stark types free for ordinary Stark use, and introduce a separate interop type
when a native boundary needs one.

That keeps the package easy to change. Downstream Stark code should depend on
the source API, not on private field order choices that were only meant for the
package internals.

```stark
public struct Rectangle
{
    i32[min max] Width;
    i32[min max] Height;
}

internal struct NativeRectangle
{
    f32 X;
    f32 Y;
    f32 Width;
    f32 Height;
}
```

Here `Rectangle` is the Stark source type. `NativeRectangle` exists only
because the native boundary wants that exact shape.

Keep the conversion explicit and close to the boundary:

```stark
internal finite law NativeRectangle ToNative(Rectangle rectangle)
{
    return new NativeRectangle()
    {
        X = 0.0f,
        Y = 0.0f,
        Width = (f32)rectangle.Width,
        Height = (f32)rectangle.Height
    };
}
```

Do not ask ordinary Stark callers to construct `NativeRectangle` unless the
native representation is truly part of the Stark API.

## Step 3: Choose `public` Or `export` By The Boundary

`public` is source visibility. It lets downstream Stark code use a declaration.

`export` is ABI visibility. It asks for a native symbol that code outside Stark
can find.

Do not use `export` just because a declaration is part of a Stark package API.
Use `export` when the declaration is truly a binary boundary: an entrypoint,
plugin hook, runtime hook, or FFI-facing function.

```stark
public finite law i32[min max] Area(Rectangle rectangle)
{
    return rectangle.Width * rectangle.Height;
}

export fn i32[min max] main()
{
    return 0;
}
```

The first declaration is for Stark callers. The second declaration is for the
hosted executable entrypoint.

An exported function can still call ordinary public helpers:

```stark
public finite law i32[min max] ExitCode(bool ok)
{
    if (ok)
    {
        return 0;
    }

    return 1;
}

export fn i32[min max] main()
{
    return ExitCode(true);
}
```

Keep `export` on the smallest function that truly needs a native symbol.

## Step 4: Design The C Surface Explicitly

C-facing APIs should be small and explicit:

- prefer scalar values and raw pointers at the boundary
- keep ownership transfer documented in the wrapper API
- convert raw or platform-specific failures into Stark result/status values
- avoid exposing ordinary Stark enums as C ABI values
- keep foreign unwinding out of Stark frames

This rejected example is the rule in code:

{{< stark-sample "assets/book/negative-samples/enum-abi-boundary.stark" >}}

If a struct or record must cross the C boundary, design it as an interop type
rather than assuming an ordinary internal type should double as an ABI type.

```stark
unsafe ffi fn void native_draw_rectangle(rawptr<NativeRectangle> rectangle);

fn void Draw(Rectangle rectangle)
{
    stack NativeRectangle native = new()
    {
        X = 0.0f,
        Y = 0.0f,
        Width = (f32)rectangle.Width,
        Height = (f32)rectangle.Height
    };

    unsafe
    {
        native_draw_rectangle(&native);
    }

    return;
}
```

The wrapper converts from Stark source values into the native shape at the
edge. Raw pointers stay inside the wrapper.

For enum-like data, cross the boundary with an explicit tag shape:

```stark
public enum DrawMode
{
    Lines,
    Filled,
}

internal finite law i32[min max] DrawModeTag(DrawMode mode)
{
    switch (mode)
    {
        case DrawMode.Lines:
            return 1;
        case DrawMode.Filled:
            return 2;
    }
}

unsafe ffi fn void native_set_draw_mode(i32[min max] mode);

public unsafe fn void SetDrawMode(DrawMode mode)
{
    native_set_draw_mode(DrawModeTag(mode));
    return;
}
```

The Stark enum stays in the Stark API. The C boundary receives the integer tag
the wrapper chose.

For output parameters, model the C boundary with a raw mutable pointer and
publish a safer Stark wrapper:

```stark
unsafe ffi fn i32[min max] native_read_size(rawmutptr<i32[min max]> output);

public enum SizeRead
{
    Failed,
    Ok(i32[min max] Value),
}

public unsafe fn SizeRead ReadSize()
{
    stack mut i32[min max] value = 0;
    stack i32[min max] status = native_read_size(&value);
    if (status != 0)
    {
        return SizeRead.Failed;
    }

    return SizeRead.Ok(value);
}
```

For nullable native results, check `null` before returning to safe callers:

```stark
unsafe ffi fn rawptr<i8[min max]> native_error_message();

public enum ErrorMessageState
{
    Missing,
    Present,
}

public unsafe fn ErrorMessageState HasErrorMessage()
{
    stack rawptr<i8[min max]> message = native_error_message();
    if (message == null)
    {
        return ErrorMessageState.Missing;
    }

    return ErrorMessageState.Present;
}
```

## Step 5: Hide Raw Details Behind The Package Surface

Package boundaries are source boundaries first. A package can expose public
Stark APIs while keeping helpers, raw FFI declarations, and native shims behind
the package surface.

That is the preferred shape: keep raw details close to the native dependency
and publish a smaller Stark API.

The public module should read like ordinary Stark:

```stark
public fn void DrawRectangle(Rectangle rectangle)
{
    Draw(rectangle);
    return;
}
```

The native declarations can stay internal and unsafe:

```stark
internal unsafe ffi fn void native_flush();

internal unsafe fn void FlushNative()
{
    native_flush();
    return;
}
```

That separation gives callers a normal Stark package while keeping native
requirements in one auditable place.
