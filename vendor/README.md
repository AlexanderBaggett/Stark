# Vendor Library

`Vendor` is Stark's bundled vendor-library root for bindings to established
native libraries. It is separate from `System` so the standard library can stay
small and portable while common native bindings are still available without a
package manager.

## Native Payloads

Native-backed vendor packages ship target-local headers, static libraries, and
licenses under `vendor/dist/<target-triple>/native/<package>/`. Package images
in the same target directory are built against those relative paths so downstream
imports keep the native facts they need without using package managers. The
payload layout intentionally mirrors Raylib: the native library and public
headers live directly under `native/<package>/`, with any upstream include
namespace below that directory. The current macOS arm64 payload is under
`vendor/dist/arm64-apple-macosx26.0.0/native/` and includes GLFW, Raylib, SDL3,
and SQLite.

Build scripts prefer bundled target-local payloads when present, then fall back
to `pkg-config` or explicit environment variables for local development.

## Miniaudio

Use the bundled Miniaudio binding with:

```stark
import Vendor.Miniaudio
```

The binding vendors Miniaudio 0.11.25 as a pinned single-header source drop.
The current package uses `MiniaudioImplementation.c`, enables decoder and
device I/O support, and disables unused encoding, generation, resource-manager,
node-graph, and engine code for a smaller native object. Decoders expose safe
Stark handles and read PCM frames into caller-owned `f32` or `i16` buffers.
Playback devices copy the supplied `f32` sample buffer into C-owned memory
before opening the native callback, so safe Stark code does not have to keep a
buffer pinned for an audio thread.

Build the package image with:

```bash
bash vendor/build-miniaudio-package.sh
```

No system package or `pkg-config` dependency is required. On Linux the package
metadata links `pthread`, `m`, and `dl`; macOS package metadata carries the
CoreAudio/AudioToolbox/CoreFoundation framework link arguments.

## Cgltf

Use the bundled cgltf binding with:

```stark
import Vendor.Cgltf
```

The binding vendors cgltf v1.15 as a pinned single-header source drop and uses
`CgltfImplementation.c` behind a safe `Document` handle. Parsing from memory
copies the input bytes once into C-owned storage so GLB/bin-chunk references
remain stable for the document lifetime. Parsing from file keeps
external/data-URI buffer loading explicit through `BufferLoadPolicy`. Mesh,
material, buffer, node, scene, animation, accessor, and primitive data are
exposed as small Stark value views; names copy into caller-owned byte buffers.

Build the package image with:

```bash
bash vendor/build-cgltf-package.sh
```

No system package or `pkg-config` dependency is required. The pinned header
lives under `vendor/native/cgltf/`; see `vendor/native/cgltf/VERSION.md` for
the upstream release, commit, and hash.

## GLFW

Use the bundled GLFW binding with:

```stark
import Vendor.GLFW
```

The binding exposes safe `Library` and `Window` handles for GLFW initialization
and window lifetime, plus direct wrappers for window hints, hidden/no-API
window creation, context/swap calls, window size/framebuffer queries, key and
mouse polling, cursor position, time, and event polling. Raw `GLFWwindow*`
handles stay internal. Callback-driven events are routed through
`GlfwEventBridge.c`, a fixed-size native ring buffer that installs GLFW
callbacks and lets Stark code poll `GlfwEvent` values without retaining Stark
closures inside native code.

Build the package image with:

```bash
bash vendor/build-glfw-package.sh
```

The script emits the package image under the active target triple. On macOS it
uses the bundled static GLFW payload when present. If no bundled payload is
available, the script uses `pkg-config glfw3`; otherwise set `GLFW_INCLUDE_DIR`
and `GLFW_LIBRARY_DIR`:

```bash
GLFW_INCLUDE_DIR=/usr/include GLFW_LIBRARY_DIR=/usr/lib bash vendor/build-glfw-package.sh
```

## SDL3

Use the bundled SDL3 binding with:

```stark
import Vendor.SDL3
```

The binding exposes safe `Library`, `Window`, `Renderer`, and `AudioStream`
handles for SDL3 initialization, hidden window creation, renderer clear/present
smoke paths, event polling, and basic byte-oriented audio streams. Raw
`SDL_Window*`, `SDL_Renderer*`, `SDL_AudioStream*`, SDL C `bool` returns,
callback-shaped audio entry points, and the native `SDL_Event` union stay
inside `Sdl3Binding.c`. The adapter flattens events into a C-layout Stark
record and opens simplified audio streams without callbacks, so normal Stark
callers can stay in safe code and keep hot event/audio buffers caller-owned.

Build the package image with:

```bash
bash vendor/build-sdl3-package.sh
```

The script emits the package image under the active target triple. On macOS it
uses the bundled static SDL3 payload and framework link metadata when present.
If no bundled payload is available, the script uses `pkg-config sdl3`; otherwise
set `SDL3_INCLUDE_DIR` and `SDL3_LIBRARY_DIR`:

```bash
SDL3_INCLUDE_DIR=/usr/include SDL3_LIBRARY_DIR=/usr/lib bash vendor/build-sdl3-package.sh
```

Headless examples and tests can use SDL's dummy drivers:

```bash
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy SDL_RENDER_DRIVER=software stark run
```

## STB Image

Use the bundled STB image binding with:

```stark
import Vendor.STB.Image
```

The binding vendors a pinned STB snapshot and uses `StbImageImplementation.c`
behind safe Stark-owned `Image` values. Loads from memory or file copy the
native STB allocation once into a dynamic `u8` pixel buffer, then free the
native allocation immediately. PNG, BMP, and TGA writes pass the existing pixel
buffer directly to STB. Resize has a caller-owned `ResizeLinearInto(...)` path
for hot loops and an owned `ResizeLinear(...)` convenience wrapper.

Build the package image with:

```bash
bash vendor/build-stb-image-package.sh
```

No system package or `pkg-config` dependency is required. The pinned headers
live under `vendor/native/stb/`; see `vendor/native/stb/VERSION.md` for the
upstream commit.

## Raylib

Use the bundled Raylib 6.0 binding with:

```stark
import Vendor.Raylib
```

The binding is split into `Vendor.Raylib.Core`, `Vendor.Raylib.Shapes`,
`Vendor.Raylib.Textures`, `Vendor.Raylib.Text`, `Vendor.Raylib.Models`,
`Vendor.Raylib.Audio`, and `Vendor.Raylib.Types`. The root `Vendor.Raylib`
module re-exports those modules.

The public Stark API keeps unsafe FFI contained inside the binding modules.
Pure Raylib symbol aliases use `[LinkName("RaylibSymbol")]` directly, so they
do not pay for or depend on a C rename shim. By-value C-layout aggregates such
as `Vector2`, `Vector3`, `Vector4`, `Rectangle`, and `Color` bind directly
through Stark's C ABI aggregate carrier lowering. Platform C types stay
header-shaped at the FFI boundary; for example `GetFileModTime` returns
`System.C.c_long` internally and widens to the public Stark `i64` wrapper.

Build the package image with:

```bash
bash vendor/build-raylib-package.sh
```

The script emits the Raylib package image and bundled native payload under the
active target triple, for example
`vendor/dist/x86_64-pc-linux-gnu/libVendorRaylib.starkpkg` and
`vendor/dist/x86_64-pc-linux-gnu/native/raylib/libraylib.a`. Downstream builds
can still import through `-I vendor/dist`; the compiler filters target-named
package directories to the active target.

On macOS the script uses the bundled static Raylib payload and framework link
metadata when present. If no bundled payload is available, the script uses
`pkg-config raylib`; otherwise set `RAYLIB_SRC_DIR` to a local Raylib `src`
directory:

```bash
RAYLIB_SRC_DIR=/path/to/raylib/src bash vendor/build-raylib-package.sh
```

## SQLite

Use the bundled SQLite binding with:

```stark
import Vendor.SQLite
```

The binding exposes safe Stark handles for `Database` and `Statement`, status
and result enums, `Open*` helpers, `Execute`, prepared-statement
`Prepare`/`Step`/`Finalize`, integer/double/null/text binding, and result-column
readers. Raw SQLite handles stay internal. `SQLiteTextBinding.c` provides the
small text-lifetime helper: text entering SQLite is copied with SQLite's
transient lifetime contract, and text leaving SQLite is copied into
`System.Text.OwnedAscii`.

Build the package image with:

```bash
bash vendor/build-sqlite-package.sh
```

The script emits the package image under the active target triple. On macOS it
uses the bundled SQLite amalgamation static payload when present; this payload
contains SQLite APIs newer than Apple's system SQLite, including
`sqlite3_set_errmsg`. If no bundled payload is available, the script uses
`pkg-config sqlite3`; otherwise set `SQLITE_INCLUDE_DIR` and
`SQLITE_LIBRARY_DIR`:

```bash
SQLITE_INCLUDE_DIR=/usr/include SQLITE_LIBRARY_DIR=/usr/lib bash vendor/build-sqlite-package.sh
```
