# Vendor Library

`Vendor` is Stark's bundled vendor-library root for bindings to established
native libraries. It is separate from `System` so the standard library can stay
small and portable while common native bindings are still available without a
package manager.

## LZ4

Use the bundled LZ4 binding with:

```stark
import Vendor.LZ4
```

The binding exposes direct one-shot block compression and decompression over
caller-owned byte slices. Raw native pointers stay inside the binding; callers
provide source and destination buffers and receive explicit result sizes or
`LZ4Error` values. The hot path does not allocate.

Build the package image with:

```bash
bash vendor/build-lz4-package.sh
```

The script uses `pkg-config liblz4` when available. If LZ4 is not visible to
`pkg-config`, set `LZ4_INCLUDE_DIR` and `LZ4_LIBRARY_DIR`:

```bash
LZ4_INCLUDE_DIR=/usr/include LZ4_LIBRARY_DIR=/usr/lib bash vendor/build-lz4-package.sh
```

## Zlib

Use the bundled zlib binding with:

```stark
import Vendor.Zlib
```

The binding exposes direct one-shot compression/decompression over caller-owned
byte slices, plus streaming deflate/inflate handles. One-shot calls go straight
to zlib's C ABI. Streaming uses `ZlibStreamBinding.c` because `z_stream`
contains nullable C callback fields and macro-shaped initialization that Stark
should not model as public unsafe state.

Build the package image with:

```bash
bash vendor/build-zlib-package.sh
```

The script uses `pkg-config zlib` when available. If zlib is not visible to
`pkg-config`, set `ZLIB_INCLUDE_DIR` and `ZLIB_LIBRARY_DIR`:

```bash
ZLIB_INCLUDE_DIR=/usr/include ZLIB_LIBRARY_DIR=/usr/lib bash vendor/build-zlib-package.sh
```

## Curl

Use the bundled libcurl binding with:

```stark
import Vendor.Curl
```

The binding exposes a caller-owned `GetInto(...)` path for hot code that wants
fixed buffers and no Stark allocation, plus `GetBytes(...)` for convenience
when an owned dynamic byte response is acceptable. Raw `CURL*`, varargs
`curl_easy_setopt`, callbacks, and native response buffers stay inside the
binding or `CurlEasyBinding.c`.

Build the package image with:

```bash
bash vendor/build-curl-package.sh
```

The script uses `pkg-config libcurl` when available. If libcurl is not visible
to `pkg-config`, set `CURL_INCLUDE_DIR` and `CURL_LIBRARY_DIR`; fallback link
metadata may also need TLS/backend libraries for the local libcurl build:

```bash
CURL_INCLUDE_DIR=/usr/include CURL_LIBRARY_DIR=/usr/lib bash vendor/build-curl-package.sh
```

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

The script uses `pkg-config glfw3` when available. If GLFW is not visible to
`pkg-config`, set `GLFW_INCLUDE_DIR` and `GLFW_LIBRARY_DIR`:

```bash
GLFW_INCLUDE_DIR=/usr/include GLFW_LIBRARY_DIR=/usr/lib bash vendor/build-glfw-package.sh
```

## KbTextShape

Use the bundled Unicode segmentation and shaping binding with:

```stark
import Vendor.KbTextShape
```

The binding uses ICU break iterators for grapheme, word, and line segmentation
over UTF-8, and HarfBuzz for OpenType glyph shaping. Public Stark code works
with reusable safe `Segmenter` and `Font` handles, `TextBoundary` and
`ShapedGlyph` C-layout value records, and caller-owned output slices. Raw
`hb_*`, `UBreakIterator`, `UText`, nullable handles, and native allocation stay
inside `KbTextShapeBinding.c`. Segmentation boundaries are UTF-8 byte offsets,
so they can be used directly with Stark `ascii`/UTF-8 views.

Build the package image with:

```bash
bash vendor/build-kb-text-shape-package.sh
```

The script uses `pkg-config harfbuzz`, `pkg-config icu-uc`, and
`pkg-config icu-i18n` when available. If those packages are not visible to
`pkg-config`, set `HARFBUZZ_INCLUDE_DIR`, `HARFBUZZ_LIBRARY_DIR`,
`ICU_INCLUDE_DIR`, and `ICU_LIBRARY_DIR`:

```bash
HARFBUZZ_INCLUDE_DIR=/usr/include/harfbuzz HARFBUZZ_LIBRARY_DIR=/usr/lib ICU_INCLUDE_DIR=/usr/include ICU_LIBRARY_DIR=/usr/lib bash vendor/build-kb-text-shape-package.sh
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

The script uses `pkg-config sdl3` when available. If SDL3 is not visible to
`pkg-config`, set `SDL3_INCLUDE_DIR` and `SDL3_LIBRARY_DIR`:

```bash
SDL3_INCLUDE_DIR=/usr/include SDL3_LIBRARY_DIR=/usr/lib bash vendor/build-sdl3-package.sh
```

Headless examples and tests can use SDL's dummy drivers:

```bash
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy SDL_RENDER_DRIVER=software stark run
```

## Vulkan

Use the bundled Vulkan binding with:

```stark
import Vendor.Vulkan
```

The binding is generated from the pinned Khronos Vulkan registry XML in
`vendor/native/vulkan/vk.xml`. The landed surface exposes safe loader-level
queries for API version, global entry-point availability, instance layer and
extension counts, plus generated handle wrappers and the C-layout ABI carriers
needed for instance creation. Raw `Vk*` dispatchable handles and native
`vk*` entry points stay internal; public code works with small Stark result
enums, `ApiVersion`, and safe `VkInstance` wrapper values. The first package
does not use a C adapter. `VkInstance` owns its native instance and destroys it
on drop; `DestroyInstance` is available for explicit early release.

Build the package image with:

```bash
bash vendor/build-vulkan-package.sh
```

The script uses `pkg-config vulkan` when available. If Vulkan is not visible to
`pkg-config`, set `VULKAN_SDK`, or set `VULKAN_INCLUDE_DIR` and
`VULKAN_LIBRARY_DIR`:

```bash
VULKAN_INCLUDE_DIR=/usr/include VULKAN_LIBRARY_DIR=/usr/lib bash vendor/build-vulkan-package.sh
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

## STB Truetype

Use the bundled STB Truetype binding with:

```stark
import Vendor.STB.Truetype
```

The binding exposes a safe `Font` handle initialized from caller-provided font
bytes. `StbTruetypeImplementation.c` copies the font bytes once and owns that
stable memory for the handle lifetime, so later glyph metric, bitmap, and atlas
calls do not depend on Stark dynamic storage staying at a fixed address. Glyph
bitmap and atlas APIs write into caller-owned `u8` buffers and report explicit
`TrueTypeError` values.

Build the package image with:

```bash
bash vendor/build-stb-truetype-package.sh
```

No system package or `pkg-config` dependency is required. The pinned header and
deterministic Bitstream Vera font fixture live under `vendor/native/stb/`; see
`vendor/native/stb/VERSION.md` for upstream and fixture details.

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

The script uses `pkg-config raylib` when available. If raylib is not visible to
`pkg-config`, set `RAYLIB_SRC_DIR` to a local Raylib `src` directory:

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

The script uses `pkg-config sqlite3` when available. If sqlite3 is not visible
to `pkg-config`, set `SQLITE_INCLUDE_DIR` and `SQLITE_LIBRARY_DIR`:

```bash
SQLITE_INCLUDE_DIR=/usr/include SQLITE_LIBRARY_DIR=/usr/lib bash vendor/build-sqlite-package.sh
```
