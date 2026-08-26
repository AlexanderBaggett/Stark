# Raylib Bindings

This directory contains a normal SDK-consumer example plus standalone Stark
bindings for Raylib 6.0, split by Raylib area for readability. `Stark.toml`
builds `VendorRaylibSafeApis.stark` against the bundled `Vendor.Raylib`,
`Vendor.Raymath`, and `Vendor.Rlgl` packages with no native setup. The local
binding sources remain available for contributors using `build-package.sh`.

- `Raylib/Types.stark`: structs, aliases, callback pointer aliases, enum constants, color helpers, and small constructors.
- `Raylib/Core.stark`
- `Raylib/Shapes.stark`
- `Raylib/Textures.stark`
- `Raylib/Text.stark`
- `Raylib/Models.stark`
- `Raylib/Audio.stark`
- `Raylib.stark`: convenience re-export module.

The binding uses `[LinkName("...")]`, `[StructLayout(C)]`, and `ffi(c)` for
direct calls, including small by-value structs such as `Vector2`, `Rectangle`,
and `Color`. No Raylib-specific C shim is required for these aggregate carriers.
On AArch64, `Color` is passed through an `i64` carrier and returned through
`i32`; those directions intentionally use different ABI facts.

An application using an installed SDK imports `Vendor.Raylib` and runs ordinary
`stark build`/`stark run` commands. From `examples`, `stark build raylib` builds
the SDK-consumer project. The local build steps below are specifically for
binding authors rebuilding this standalone source package.

Raylib's `TraceLog` and `TextFormat` are declared with `ffi varargs`; pass extra arguments only with C-varargs-stable types such as `i32`, wider integers, `f64`, raw pointers, or text. Callback typedefs are exposed as raw callback pointers until Stark has a dedicated C-callable callback ABI. Raylib color macros are exposed as zero-cost constructor functions such as `RAYWHITE()` because the current compiler does not materialize narrowed byte-field aggregate constants directly.

## Headless Geometry Example

Build Raylib 6.0 locally:

```bash
curl -L -o /tmp/raylib-6.0.tar.gz https://github.com/raysan5/raylib/archive/refs/tags/6.0.tar.gz
mkdir -p /tmp/stark-raylib
tar -xzf /tmp/raylib-6.0.tar.gz -C /tmp/stark-raylib
make -C /tmp/stark-raylib/raylib-6.0/src PLATFORM=PLATFORM_DESKTOP RAYLIB_LIBTYPE=STATIC
```

Build the Stark Raylib package once, then compile the headless geometry example
from that package image:

```bash
bash examples/raylib/build-package.sh
tmpdir="$(mktemp -d /tmp/stark-raylib-headless-geometry-XXXXXX)"
cp examples/raylib/RaylibHeadlessGeometry.stark "$tmpdir/RaylibHeadlessGeometry.stark"
./stark "$tmpdir/RaylibHeadlessGeometry.stark" --emit-exe -I examples/raylib/dist -o "$tmpdir/stark-raylib-headless-geometry"
"$tmpdir/stark-raylib-headless-geometry"
```

If Raylib is visible through `pkg-config`, the helper script uses
`Raylib.package.args` automatically. For a local Raylib build that does not
install `raylib.pc`, point the script at the Raylib `src` directory:

```bash
RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-6.0/src bash examples/raylib/build-package.sh
```

If neither `pkg-config` nor `RAYLIB_SRC_DIR` can provide Raylib, the helper
script now stops early and prints the same guidance instead of failing later
while linking a downstream example.

The emitted package keeps Raylib native dependency metadata package-relative, so
downstream builds only need `-I examples/raylib/dist` and do not have to repeat
Raylib library flags.

`RaylibHeadlessGeometry.stark` is intentionally headless. Playable graphical examples should be run manually on machines with a display server.

The first playable user-facing example is `examples/breakout/BreakoutRaylib.stark`.
From the repository root, build it with:

```bash
bash examples/breakout/run-raylib.sh
```

The script builds the Raylib package first, then compiles Breakout from that
package image. If Raylib is available through `pkg-config`, no extra flags are
needed. For a local Raylib build, set `RAYLIB_SRC_DIR` to the Raylib `src`
directory before running the script.
