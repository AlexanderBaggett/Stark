# Raylib Bindings

This directory contains Stark bindings for Raylib 5.5, split by Raylib area for readability.

- `Raylib/Types.stark`: structs, aliases, callback pointer aliases, enum constants, color helpers, and small constructors.
- `Raylib/Core.stark`
- `Raylib/Shapes.stark`
- `Raylib/Textures.stark`
- `Raylib/Text.stark`
- `Raylib/Models.stark`
- `Raylib/Audio.stark`
- `Raylib.stark`: convenience re-export module.
- `RaylibNative.c`: C ABI shim for Raylib functions that pass or return structs by value.

Stark direct FFI is used for scalar and pointer-only Raylib calls. Calls that pass or return Raylib structs by value go through `RaylibNative.c`, which keeps the C ABI layout and calling convention on the C side while exposing normal Stark functions. `GetFileModTime` also goes through the shim so Stark sees a stable `i64` instead of C's platform-dependent `long`.

Raylib's `TraceLog` and `TextFormat` are declared with `ffi varargs`; pass extra arguments only with C-varargs-stable types such as `i32`, wider integers, `f64`, raw pointers, or text. Callback typedefs are exposed as raw callback pointers until Stark has a dedicated C-callable callback ABI. Raylib color macros are exposed as zero-cost constructor functions such as `RAYWHITE()` because the current compiler does not materialize narrowed byte-field aggregate constants directly.

## Build Smoke

Build Raylib 5.5 locally:

```bash
curl -L -o /tmp/raylib-5.5.tar.gz https://github.com/raysan5/raylib/archive/refs/tags/5.5.tar.gz
mkdir -p /tmp/stark-raylib
tar -xzf /tmp/raylib-5.5.tar.gz -C /tmp/stark-raylib
make -C /tmp/stark-raylib/raylib-5.5/src PLATFORM=PLATFORM_DESKTOP RAYLIB_LIBTYPE=STATIC
```

Build the Stark Raylib package once, then compile the headless smoke test from
that package image:

```bash
bash examples/raylib/build-package.sh
./stark examples/raylib/RaylibSmoke.stark --emit-exe -I examples/raylib/dist -o /tmp/stark-raylib-smoke -O0
/tmp/stark-raylib-smoke
```

If Raylib is visible through `pkg-config`, the helper script uses
`Raylib.package.args` automatically. For a local Raylib build that does not
install `raylib.pc`, point the script at the Raylib `src` directory:

```bash
RAYLIB_SRC_DIR=/tmp/stark-raylib/raylib-5.5/src bash examples/raylib/build-package.sh
```

If neither `pkg-config` nor `RAYLIB_SRC_DIR` can provide Raylib, the helper
script now stops early and prints the same guidance instead of failing later
while linking a downstream example.

The emitted package keeps `RaylibNative.c` package-relative, so downstream
builds only need `-I examples/raylib/dist` and do not have to repeat the shim
source path.

`RaylibSmoke.stark` is intentionally headless. Playable graphical examples should be run manually on machines with a display server.

The first playable user-facing example is `examples/breakout/BreakoutRaylib.stark`.
From the repository root, build it with:

```bash
bash examples/breakout/run-raylib.sh
```

The script builds the Raylib package first, then compiles Breakout from that
package image. If Raylib is available through `pkg-config`, no extra flags are
needed. For a local Raylib build, set `RAYLIB_SRC_DIR` to the Raylib `src`
directory before running the script.
