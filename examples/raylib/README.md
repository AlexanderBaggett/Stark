# Raylib Bindings

This directory contains standalone Stark bindings for Raylib 6.0, split by
Raylib area for readability. New code can also use the bundled `Vendor.Raylib`
package under `/vendor`; both bindings use Stark's direct C ABI aggregate
carrier lowering for C-layout Raylib structs.

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

Raylib's `TraceLog` and `TextFormat` are declared with `ffi varargs`; pass extra arguments only with C-varargs-stable types such as `i32`, wider integers, `f64`, raw pointers, or text. Callback typedefs are exposed as raw callback pointers until Stark has a dedicated C-callable callback ABI. Raylib color macros are exposed as zero-cost constructor functions such as `RAYWHITE()` because the current compiler does not materialize narrowed byte-field aggregate constants directly.

## Build Smoke

Build Raylib 6.0 locally:

```bash
curl -L -o /tmp/raylib-6.0.tar.gz https://github.com/raysan5/raylib/archive/refs/tags/6.0.tar.gz
mkdir -p /tmp/stark-raylib
tar -xzf /tmp/raylib-6.0.tar.gz -C /tmp/stark-raylib
make -C /tmp/stark-raylib/raylib-6.0/src PLATFORM=PLATFORM_DESKTOP RAYLIB_LIBTYPE=STATIC
```

Build the Stark Raylib package once, then compile the headless smoke test from
that package image:

```bash
bash examples/raylib/build-package.sh
tmpdir="$(mktemp -d /tmp/stark-raylib-smoke-XXXXXX)"
cp examples/raylib/RaylibSmoke.stark "$tmpdir/RaylibSmoke.stark"
./stark "$tmpdir/RaylibSmoke.stark" --emit-exe -I examples/raylib/dist -o "$tmpdir/stark-raylib-smoke"
"$tmpdir/stark-raylib-smoke"
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
