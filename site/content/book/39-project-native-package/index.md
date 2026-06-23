+++
title = "39. Project: Native-Backed Package"
weight = 390
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/38-project-file-processing/"
next = "/book/40-project-performance-case-study/"
aliases = ["/book/33-project-native-package/", "/book/35-project-native-package/", "/book/36-project-native-package/"]

[[language_refs]]
title = "Projects and Solutions"
href = "/reference/language/ProjectsAndSolutions/"

[[example_refs]]
title = "FFI Example"
href = "/reference/examples/ffi/Ffi.stark"

[[example_refs]]
title = "Raylib Package"
href = "/reference/examples/raylib/README.md"
+++

# Project: Native-Backed Package

This project chapter wraps a native library once, then lets downstream Stark
projects consume it through ordinary package dependencies.

## Step 1: Define The Wrapper Boundary

Build a Stark package that wraps a native library and lets downstream Stark
executables depend on it without repeating native link details.

Use the Raylib example as the concrete model for direct symbol bindings and
C-layout aggregate carriers. Use `Vendor.SDL3` as the model when a native
adapter is required for real ABI reasons: SDL3 keeps C `bool` returns,
`SDL_Event`'s union layout, nullable handles, and callback-shaped audio entry
points inside `Sdl3Binding.c`, while safe Stark callers use handles, result
enums, flat event values, and caller-owned byte buffers.
Use `Vendor.KbTextShape` as the same pattern for native text engines: HarfBuzz
and ICU handles stay inside `KbTextShapeBinding.c`, while Stark code passes
UTF-8 text and caller-owned output slices for boundaries and shaped glyphs.
Use `Vendor.Vulkan` as the generated direct-binding model: pin the upstream
registry input, regenerate Stark handle wrappers and C ABI carriers, keep raw
`vk*` symbols internal, and let the emitted package image carry
`pkg-config vulkan` or explicit loader-library metadata. No adapter source is
needed when the C ABI shape is already expressible by Stark.

## Step 2: Write The Stark Wrapper First

The Stark source declares the FFI boundary and exposes a smaller Stark API:

{{< stark-sample "samples/ffi-raw-pointers.stark" >}}

The checked sample keeps the raw pointer work small and explicit. A native
package should do the same thing at package scale: keep raw native declarations
close to the package that owns them, then expose a smaller Stark API to
downstream code.

The `unsafe ffi fn native_value` declaration shows the boundary shape without
making the ordinary safe part of the program depend on raw pointers or nullable
safe borrows.

Keep the public wrapper source-shaped and small:

```stark
unsafe ffi fn i32[min max] native_value();

public unsafe fn i32[min max] ReadNativeValue()
{
    return native_value();
}
```

If the wrapper can check everything the caller would otherwise have to know,
make the public wrapper safe and keep `unsafe` inside:

```stark
unsafe ffi fn i32[min max] native_abs_i32(i32[min max] value);

public fn i32[min max] AbsI32(i32[min max] value)
{
    unsafe
    {
        return native_abs_i32(value);
    }
}
```

If the caller must uphold a native lifetime, thread, or buffer rule, keep the
wrapper `unsafe` and document that rule in the API text.

When the native API can fail, return a Stark enum or status value instead of
turning the failure into a magic integer:

```stark
public enum NativeValueResult
{
    Ok(i32[min max]),
    Err,
}

public unsafe fn NativeValueResult TryReadNativeValue()
{
    stack i32[min max] value = native_value();
    if (value < 0)
    {
        return NativeValueResult.Err;
    }

    return NativeValueResult.Ok(value);
}
```

If the native API returns nullable pointers, keep the nullable value in the
wrapper and publish a Stark result:

```stark
unsafe ffi fn rawptr<i8[min max]> native_error_message();

public enum NativeMessageStatus
{
    None,
    Available,
}

public unsafe fn NativeMessageStatus HasNativeMessage()
{
    stack rawptr<i8[min max]> pointer = native_error_message();
    if (pointer == null)
    {
        return NativeMessageStatus.None;
    }

    return NativeMessageStatus.Available;
}
```

Do not convert a native pointer into a safe Stark view unless the wrapper can
state the lifetime, encoding, and nullability rules clearly.

For native output parameters, use raw mutable pointers at the boundary and a
result value at the Stark surface:

```stark
unsafe ffi fn i32[min max] native_read_count(rawmutptr<i32[min max]> count);

public enum NativeCountResult
{
    Err,
    Ok(i32[min max] Count),
}

public unsafe fn NativeCountResult ReadCount()
{
    stack mut i32[min max] count = 0;
    stack i32[min max] status = native_read_count(&count);
    if (status != 0)
    {
        return NativeCountResult.Err;
    }

    return NativeCountResult.Ok(count);
}
```

For C varargs, keep the declaration narrow and unsafe:

```stark
public unsafe ffi varargs fn i32[min max] printf(ascii format);
```

Only wrap the argument shapes your package actually supports.

## Step 3: Name Native Symbols Directly

Use `[LinkName("symbol")]` when the Stark declaration name should differ from
the foreign linker symbol:

```stark
[LinkName("vendor_current_value")]
unsafe ffi(c) fn i32[min max] CurrentValue();

[LinkName("vendor_abs_i32")]
unsafe ffi(c) fn i32[min max] AbsI32(i32[min max] value);
```

This is the zero-overhead path: the Stark source name stays clear and private to
the binding, while LLVM declares and calls the named foreign symbol directly.
`LinkName` does not change the ABI shape. The parameters, return type, calling
convention, ownership, and safety contract must already match the native
function.

When a C struct is passed or returned by value, `[StructLayout(C)]` on the Stark
struct plus `ffi(c)` on the declaration is the matching ABI contract. The
compiler lowers those aggregate parameters and returns through the target C ABI
carrier shape, so use `[LinkName]` directly for raylib-style `Vector2`,
`Vector3`, `Vector4`, `Rectangle`, and similar C-layout structs.

## Step 4: Add A Boring Native Shim

Use a small C shim only when the native library's ABI shape is genuinely
awkward for direct Stark declarations. The shim should translate between the
native library and a simple C ABI surface.

Keep the shim boring:

- no hidden ownership transfer
- no exceptions
- no global configuration unless unavoidable
- small functions with clear inputs and outputs

A shim should do real ABI adaptation, not just rename a symbol:

```c
#include "vendor_math.h"

int stark_read_count(int *count) {
    return vendor_read_count(count);
}
```

The Stark declarations should match the shim, not the larger vendor header:

```stark
unsafe ffi fn i32[min max] stark_read_count(rawmutptr<i32[min max]> count);
```

For callbacks, expose an explicit registration wrapper:

```stark
unsafe fn i32[min max] OnNativeEvent()
{
    return 0;
}

unsafe ffi fn void stark_register_callback(fnptr<fn i32[min max]()> callback);

public unsafe fn void RegisterNativeCallback()
{
    stark_register_callback(OnNativeEvent);
    return;
}
```

Use that shape only when the native side stores a plain function pointer and
the callback's requirements are satisfied by the registration call.

## Step 5: Put Native Build Settings In The Manifest

Native requirements belong in `Stark.toml`:

```toml
[project]
name = "raylib"
version = "0.1.0"
kind = "library"

[library]
root = "Raylib.stark"
output = "RaylibStark"

[native]
pkg-config = ["raylib"]
```

Platform fallback metadata can refer to user-local paths:

```toml
[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m", "pthread", "dl", "rt", "X11"]
```

User-local paths belong in config, not checked-in package files.

Keep platform differences in the manifest instead of asking every consumer to
remember them:

```toml
[native.fallback.windows]
libraries = ["raylib", "winmm", "gdi32", "opengl32"]

[native.fallback.macos]
libraries = ["raylib"]
frameworks = ["CoreVideo", "IOKit", "Cocoa", "OpenGL"]
```

## Step 6: Consume The Package Without Repeating Native Flags

The executable should only name the dependency:

```toml
[dependencies]
raylib = { path = "../raylib" }
```

That is the point of package-owned native metadata: the package carries the
native settings, and consumers use ordinary Stark dependencies.

The consuming app should call the wrapper API, not the native shim:

```stark
import Raylib
module App

export unsafe fn i32[min max] main()
{
    stack i32[min max] value = Raylib.ReadNativeValue();
    if (value < 0)
    {
        return 1;
    }

    return 0;
}
```

If the wrapper exposes a result enum, keep the switch in the app:

```stark
import NativeValues
module App

export unsafe fn i32[min max] main()
{
    stack NativeValues.NativeValueResult result = NativeValues.TryReadNativeValue();
    switch (result)
    {
        case NativeValues.NativeValueResult.Err:
            return 1;
        case NativeValues.NativeValueResult.Ok(var value):
            return value;
    }
}
```

## Step 7: Review The Boundary Before Publishing

Before treating a native-backed package as reusable, check that:

- raw pointers stay inside the wrapper package unless the public API is
  deliberately low-level
- nullable native values are checked before conversion to safe Stark values
- C shims have boring ownership and error rules
- manifest metadata names native sources, discovery hooks, and fallback
  libraries in the package that owns the native boundary
- downstream examples depend on the package, not on handwritten link commands

This rejected boundary is the rule to keep in mind while reviewing:

{{< stark-sample "rejected/enum-abi-boundary.stark" >}}

Ordinary Stark enums are source-level values. Do not expose them as C ABI
contracts unless the language and ABI design explicitly say that type is an
interop representation.
