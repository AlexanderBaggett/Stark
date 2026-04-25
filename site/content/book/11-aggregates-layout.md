+++
title = "11. Aggregates and Layout-Aware Design"
weight = 110
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/10-storage-classes/"
next = "/book/12-enums-patterns/"
+++

# Aggregates and Layout-Aware Design

Aggregates are how Stark programs make larger values out of smaller ones.
Because Stark is performance-focused, aggregate design is also layout design:
fields, ownership, construction, and API boundaries should be visible in the
source.

{{< stark-sample "assets/book/samples/aggregates-layout.stark" >}}

## Structs

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

## Records

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

## Object Initializers

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

## Field Access

Field access uses `.`:

```stark
return point.X + point.Y;
```

Field access is ordinary source-visible data access. If a type wants to hide
representation from downstream packages, keep the type or the members inside an
appropriate visibility boundary rather than relying on runtime opacity.

## Constructors And Defaults

`new Type()` creates a value of the named type. Target-typed `new()` is also
available when the surrounding declaration, assignment, return, field, or array
element already names the target type.

Prefer explicit construction when it helps the reader see ownership and storage
clearly. That is especially true at package boundaries.

## Destructors

Owned aggregate values are cleaned up when their owner goes out of scope. A
`struct` or `record` may define a destructor block for cleanup that must happen
with the owned value.

Keep destructors boring. Fallible cleanup and user-chosen ordering belong in an
explicit method such as `Close`, not in hidden unwinding behavior. Stark has no
general exception unwinding model for destructors to participate in.

## ABI Boundaries

Do not assume an ordinary Stark aggregate is automatically a stable FFI
contract. Use `export` and `ffi` deliberately, and keep binary-facing types
small, explicit, and documented.

The safe default is to treat ordinary aggregates as Stark values first. When a
type is meant to cross a C ABI boundary, design that type as an interop type
from the beginning.
