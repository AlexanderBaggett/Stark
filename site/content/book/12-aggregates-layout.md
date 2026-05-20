+++
title = "12. Aggregates and Layout-Aware Design"
weight = 120
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/11-storage-classes/"
next = "/book/13-enums-patterns/"
aliases = ["/book/11-aggregates-layout/"]
+++

# Aggregates and Layout-Aware Design

Aggregates are how Stark programs make larger values out of smaller ones.
Because Stark is performance-focused, aggregate design is also layout design:
choose which fields live together, which fields are owned, which values are
small enough to pass around directly, and which types are meant to touch a
foreign ABI.

This chapter does not claim that every ordinary Stark aggregate has stable C
struct layout. The source layout is the Stark-facing contract: fields,
ownership, and construction are visible in code. If a type is meant to cross an
FFI boundary, design it as an interop type on purpose and keep that boundary
narrow.

{{< stark-sample "assets/book/samples/aggregates-layout.stark" >}}

## Step 1: Start With A `struct` When Behavior Belongs With Data

Use `struct` for ordinary named data with associated behavior:

```stark
struct Rectangle {
    i32[min max] Width;
    i32[min max] Height;

    finite law i32[min max] Area(borrow Rectangle self) {
        return self.Width * self.Height;
    }
}
```

The fields are part of the type. The method takes `borrow Rectangle self`,
which means it can read the rectangle without taking ownership.

Layout-aware design starts here: `Rectangle` is two integer fields and no owned
resource fields. That makes its source shape easy to reason about, and it keeps
the operation that belongs with the data close to the data.

## Step 2: Use A `record` For Compact Data Results

Use `record` for data-first aggregate shapes:

```stark
record Point(i32[min max] X, i32[min max] Y) { }
```

The positional constructor is visible at the call site:

```stark
stack Point point = new Point(1, 2);
```

Records are useful for compact result/status data and other cases where the
field list is the central point of the type.

A record can also group other aggregates when the shape itself is the useful
result:

```stark
struct Pixel {
    u8[0 max] R;
    u8[0 max] G;
    u8[0 max] B;
    u8[0 max] A;
}

record DrawCommand(Rectangle Bounds, Pixel Tint) { }
```

That design keeps the draw command as data. It does not hide work behind a
runtime object identity or virtual dispatch.

## Step 3: Initialize By Naming The Fields You Own

Object initializers name each field being initialized:

```stark
stack Rectangle rectangle = new Rectangle() {
    Width = 3,
    Height = 4
};
```

This form is intentionally explicit. The reader can see which fields are being
written, and Stark does not need hidden reflection or dynamic member lookup to
perform initialization.

Target-typed initialization keeps the same explicit field writes when the
destination already says the type:

```stark
fn Rectangle MakeRectangle(i32[min max] width, i32[min max] height) {
    return new() {
        Width = width,
        Height = height
    };
}
```

## Step 4: Read Fields Through The Source API

Field access uses `.`:

```stark
return point.X + point.Y;
```

Field access is ordinary source-visible data access. If a type wants to hide
representation from downstream packages, keep the type or the members inside an
appropriate visibility boundary rather than relying on runtime opacity.

When one aggregate contains another, access stays direct and source visible:

```stark
finite law i32[min max] CommandArea(DrawCommand command) {
    return command.Bounds.Area();
}
```

## Step 5: Prefer Construction That Shows Ownership

`new Type()` creates a value of the named type. Target-typed `new()` is also
available when the surrounding declaration, assignment, return, field, or array
element already names the target type.

Prefer explicit construction when it helps the reader see ownership and storage
clearly. That is especially true at package boundaries.

```stark
stack Pixel tint = new() {
    R = 255,
    G = 0,
    B = 0,
    A = 255
};

stack Rectangle bounds = MakeRectangle(3, 4);
stack DrawCommand command = new DrawCommand(bounds, tint);
```

The `DrawCommand` constructor consumes the two owned values. That is the point:
the source says which aggregate owns the rectangle and tint after construction.

## Step 6: Keep Destructors Boring

Owned aggregate values are cleaned up when their owner goes out of scope. A
`struct` or `record` may define a destructor block for cleanup that must happen
with the owned value.

Keep destructors boring. Fallible cleanup and user-chosen ordering belong in an
explicit method such as `Close`, not in hidden unwinding behavior. Stark has no
general exception unwinding model for destructors to participate in.

```stark
struct ScratchOwner {
    i32[min max] Handle;
    bool Closed;

    fn void Close(mut borrow ScratchOwner self) {
        self.Closed = true;
        return;
    }

    mut drop {
        self.Closed = true;
    }
}
```

The explicit `Close` method is where user-chosen ordering belongs. The
destructor is the deterministic backstop, and it stays simple enough for the
compiler and reader to understand.

## Step 7: Design ABI Types Separately

Do not assume an ordinary Stark aggregate is automatically a stable FFI
contract. Use `export` and `ffi` deliberately, and keep binary-facing types
small, explicit, and documented.

The safe default is to treat ordinary aggregates as Stark values first. When a
type is meant to cross a C ABI boundary, design that type as an interop type
from the beginning.

```stark
struct NativeRectangle {
    f32 X;
    f32 Y;
    f32 Width;
    f32 Height;
}

unsafe ffi fn void native_draw_rectangle(rawptr<NativeRectangle> rectangle);

fn void DrawNativeRectangle(NativeRectangle rectangle) {
    unsafe {
        native_draw_rectangle(&rectangle);
    }

    return;
}
```

This is the layout-aware rule in practice: keep the ordinary Stark type and the
native-facing type conceptually separate unless the type is deliberately part
of the boundary. Chapter 19 covers the raw pointer and `ffi` details; the
important habit here is to make ABI layout a conscious design decision.
