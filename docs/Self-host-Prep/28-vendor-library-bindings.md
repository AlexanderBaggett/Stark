# Vendor Library Binding Roadmap

Short-lived task document for expanding Stark's bundled `Vendor.*` library.
Delete this file once the bindings below have either landed or moved into the
normal roadmap.

## Documentation

Odin's `vendor:` collection is a useful model for Stark's `Vendor.*` root: it
ships native-library bindings with the language implementation, separate from
the core/standard library, so users can reach common C/C++ libraries without a
package manager. Odin's current vendor index includes packages such as `cgltf`,
`curl`, `lz4`, `miniaudio`, `stb`, `vulkan`, `zlib`, GLFW, SDL, and text
shaping helpers:

- <https://pkg.odin-lang.org/vendor/>
- <https://github.com/odin-lang/Odin/tree/master/vendor>

Stark's version should keep the same broad idea while staying aligned with
Stark's performance and safety model:

- public APIs should expose safe Stark-owned handles, slices, result enums, and
  range-typed counts where possible
- raw pointers, nullable handles, callbacks, and foreign ownership stay internal
  unless the underlying library truly requires an unsafe public escape hatch
- use direct `[LinkName]` FFI declarations for true symbol aliases
- reserve C adapter sources for real ABI adaptation, macro sentinels, callback
  trampolines, or platform-specific glue that Stark cannot represent directly
- package images must carry native dependency metadata (`pkg-config`, fallback
  include/library paths, required native sources, and link arguments)
- every binding needs at least one small example under `examples/` and focused
  C# integration coverage; Stark self-hosted tests should be added where the
  current Stark test harness can express the checks
- every binding task must document how to get the native library on Linux,
  macOS, and Windows, including the `pkg-config` name when one exists and the
  machine-local `Stark.user.toml` / environment-variable fallback names for
  source or binary installs

Suggested priority is small, high-value, easy-to-test libraries first:
compression and image/audio helpers before large generated graphics APIs.

## Binding Candidates

### `Vendor.LZ4`

Fast block and streaming compression. This is highly aligned with Stark's speed
goals and useful for logs, compiler artifacts, asset packs, network messages,
and cache files.

Initial surface:

- block compress/decompress from `borrow i8[min max][]` to
  `mut borrow i8[min max][]`
- worst-case compressed-size helper
- streaming encoder/decoder handles if the C library is available

Native dependency acquisition:

- Linux packages: install the distribution `liblz4` development package
  (`liblz4-dev`, `lz4-devel`, or equivalent). The expected `pkg-config` package
  is `liblz4`.
- macOS packages: install `lz4` with Homebrew or MacPorts and use the provided
  `liblz4.pc`, or set fallback include/library paths.
- Windows packages: use vcpkg (`lz4`) or an official/prebuilt LZ4 install that
  provides `lz4.h` and `lz4.lib`.
- Stark fallback inputs: `LZ4_INCLUDE_DIR` must contain `lz4.h`, and
  `LZ4_LIBRARY_DIR` must contain the native LZ4 library when `pkg-config`
  cannot resolve `liblz4`.

### `Vendor.Zlib`

Compatibility compression for gzip/zlib streams, PNG tooling, HTTP content
encoding, and older file/protocol formats.

Initial surface:

- one-shot deflate/inflate
- streaming deflate/inflate handles
- explicit compression-level enum and result errors

Native dependency acquisition:

- Linux packages: install `zlib` development headers (`zlib1g-dev`,
  `zlib-devel`, or equivalent). The common `pkg-config` package is `zlib`.
- macOS packages: use the system zlib or Homebrew/MacPorts `zlib`; prefer
  `pkg-config zlib` when available.
- Windows packages: use vcpkg (`zlib` or `zlib-ng` when explicitly selected)
  or a prebuilt zlib with headers and import library.
- Stark fallback inputs: `ZLIB_INCLUDE_DIR` and `ZLIB_LIBRARY_DIR`.

### `Vendor.Curl`

Mature HTTP(S) and transfer library. This gives Stark tooling dependable TLS,
redirects, proxies, uploads, and downloads without building a package manager
first.

Initial surface:

- easy-handle wrapper
- GET into `OwnedAscii` or `dynamic u8`
- status code, headers, and explicit error reporting
- callback bridge for write buffers

Native dependency acquisition:

- Linux packages: install libcurl development files (`libcurl4-openssl-dev`,
  `libcurl-devel`, or equivalent). The expected `pkg-config` package is
  `libcurl`.
- macOS packages: install `curl` with Homebrew/MacPorts when the system curl
  does not provide headers and `libcurl.pc`.
- Windows packages: use vcpkg (`curl`) with the intended TLS backend.
- Stark fallback inputs: `CURL_INCLUDE_DIR` and `CURL_LIBRARY_DIR`; document
  additional native libraries required by the selected TLS backend if
  `pkg-config` is unavailable.

### `Vendor.STB.Image`

Image load/write/resize support from the STB family. Useful for games,
screenshots, asset pipelines, tests, and examples.

Initial surface:

- load image from file and memory into owned pixel buffer
- write PNG/TGA/BMP where supported
- resize helper if the selected STB source provides it

Native dependency acquisition:

- STB is a header-only source drop. Vendor should pin upstream commit/hash and
  check in the exact `stb_image.h`, `stb_image_write.h`, and optional
  `stb_image_resize2.h` files under `vendor/native/stb/`.
- No system package or `pkg-config` dependency should be required for the core
  image binding.
- Stark fallback inputs: none for the bundled path; only add override paths if
  the binding deliberately supports an externally supplied STB snapshot.

### `Vendor.STB.Truetype`

TrueType parsing and rasterization. Pairs well with Raylib, future GUI work,
software rendering examples, and text/font tests.

Initial surface:

- safe font handle initialized from caller-provided font bytes; the native
  adapter may copy bytes once to keep STB's internal font-info pointers stable
- glyph metrics and bitmap rasterization
- atlas-building helper using caller-owned output buffers

Native dependency acquisition:

- STB Truetype is header-only. Vendor should pin upstream commit/hash and check
  in the exact `stb_truetype.h` file under `vendor/native/stb/`.
- Truetype examples/tests should use a checked-in redistributable font fixture
  with its license so glyph metrics and raster output are deterministic.
- No system package or `pkg-config` dependency should be required.
- Stark fallback inputs: none for the bundled path.

### `Vendor.Miniaudio`

Small cross-platform audio playback/capture library. This is a better first
audio binding than per-platform APIs.

Initial surface:

- playback device handle over a C-owned callback buffer for safe audio-thread
  lifetime
- simple decoder for common audio files with caller-owned PCM output buffers
- callback-safe sample buffer API that avoids exposing Stark storage to native
  audio callbacks
- capture optional after playback is stable

Native dependency acquisition:

- Miniaudio is a single-header source drop. Vendor should pin upstream
  commit/hash and check in `miniaudio.h` under `vendor/native/miniaudio/`.
- The first bundled package may compile only decoder/device I/O and disable
  unused engine/resource-manager/generator/encoder code to keep native build
  size and link cost down.
- Linux package/runtime notes: link the platform libraries required by the
  enabled backend (`pthread`, `m`, `dl`, and ALSA/PulseAudio/JACK only when
  that backend is compiled in).
- macOS notes: link the relevant CoreAudio/AudioToolbox frameworks.
- Windows notes: link the selected WASAPI/WinMM system libraries.
- Stark fallback inputs: none for the bundled header; backend-specific native
  link arguments belong in package metadata.

### `Vendor.Cgltf`

glTF 2.0 loader/parser. This complements Raylib and future GPU/renderer work.
The pinned upstream `cgltf.h` release is loader-focused; do not promise a
writer API unless a separate writer dependency is selected later.

Initial surface:

- parse glTF from file or memory
- safe Stark views over meshes, materials, buffers, nodes, and animations
- explicit ownership of parsed data and external buffer/image loading policy

Native dependency acquisition:

- cgltf is a single-header source drop. Vendor should pin upstream commit/hash
  and check in `cgltf.h` under `vendor/native/cgltf/`.
- No system package or `pkg-config` dependency should be required for the core
  parser.
- Stark fallback inputs: none for the bundled path.

### `Vendor.GLFW`

Window, input, and graphics-context setup for OpenGL/OpenGL ES/Vulkan. Useful
when Raylib is too opinionated and for low-level renderer examples.

Initial surface:

- initialization and window handle wrapper
- input polling
- Vulkan/OpenGL context helpers
- event callback bridge
- landed surface uses safe `Library`/`Window` handles, direct scalar polling
  calls, and a fixed native ring-buffer callback bridge so hot event polling
  stays allocation-free and does not retain Stark closures in GLFW

Native dependency acquisition:

- Linux packages: install GLFW development files (`libglfw3-dev`,
  `glfw-devel`, or equivalent). The expected `pkg-config` package is `glfw3`.
- macOS packages: install `glfw` with Homebrew/MacPorts and use `pkg-config`
  or fallback library/framework metadata.
- Windows packages: use vcpkg (`glfw3`) or an official/prebuilt GLFW package.
- Stark fallback inputs: `GLFW_INCLUDE_DIR` and `GLFW_LIBRARY_DIR`; fallback
  metadata must also list required platform/OpenGL/Vulkan system libraries.

### `Vendor.KbTextShape`

Unicode segmentation and OpenType shaping helper inspired by Odin's `kb` /
`kb_text_shape` binding. This gives Stark a route to correct line, word,
grapheme, bidirectional, and glyph-shaping behavior without hand-rolling it.

Initial surface:

- grapheme/word/line segmentation over `unicode`/UTF-8 input
- shaping API from text + font data into positioned glyphs
- no rasterization dependency in the first pass

Native dependency acquisition:

- Stark uses HarfBuzz for OpenType shaping and ICU break iterators for
  grapheme/word/line segmentation. Expected `pkg-config` packages are
  `harfbuzz`, `icu-uc`, and `icu-i18n`.
- Linux packages: install HarfBuzz and ICU development files (`libharfbuzz-dev`
  plus `libicu-dev`, `harfbuzz-devel` plus `libicu-devel`, or equivalent).
- macOS packages: install `harfbuzz` and `icu4c` with Homebrew/MacPorts and
  ensure their `.pc` files are visible through `PKG_CONFIG_PATH`.
- Windows packages: use vcpkg (`harfbuzz` and `icu`) or equivalent prebuilt
  development packages.
- Stark fallback inputs: `HARFBUZZ_INCLUDE_DIR`, `HARFBUZZ_LIBRARY_DIR`,
  `ICU_INCLUDE_DIR`, and `ICU_LIBRARY_DIR`.
- Landed surface keeps `hb_*`, `UBreakIterator`, `UText`, nullable handles,
  and native allocation inside `KbTextShapeBinding.c`; public Stark code uses
  reusable safe `Segmenter`/`Font` handles and caller-owned `TextBoundary[]` /
  `ShapedGlyph[]` output buffers.

### `Vendor.Vulkan`

Low-level GPU API for performance-focused graphics and compute. This should be
generated from Khronos headers rather than handwritten.

Initial surface:

- generated constants, enums, flags, handles, structs, and function signatures
- loader for instance/device function pointers
- minimal triangle or compute example after the binding validates
- landed loader/core slice pins `vulkan-sdk-1.4.350.0` registry XML, generates
  handle wrappers plus loader-level constants, structs, FFI declarations, safe
  API-version/proc/count queries, global dispatch-table construction, a small
  instance-dispatch loader, package metadata, and a no-GPU loader example;
  full device/extension dispatch generation remains a follow-up

Native dependency acquisition:

- Bindings must be generated from pinned Khronos Vulkan XML/headers. Check in
  the generator input version or a reproducible fetch script with hash
  verification.
- Linux packages: install Vulkan headers/loader development files
  (`vulkan-headers`, `vulkan-loader-devel`, `libvulkan-dev`, or equivalent).
  Common `pkg-config` packages are `vulkan` or `vulkan-loader`, depending on
  distro.
- macOS packages: use Vulkan SDK/MoltenVK; document required framework and
  loader paths.
- Windows packages: use the Vulkan SDK.
- Stark fallback inputs: `VULKAN_SDK`, or explicit `VULKAN_INCLUDE_DIR` and
  `VULKAN_LIBRARY_DIR`.

### `Vendor.SDL3`

Broad cross-platform windowing, input, audio, and platform integration. This
overlaps some of Raylib/GLFW/miniaudio, but it is a practical ecosystem binding
and should be part of Stark's Vendor story.

Initial surface:

- init/quit and subsystem flags
- window and renderer handles
- event polling
- audio stream basics
- landed surface keeps `SDL_Window*`, `SDL_Renderer*`, `SDL_AudioStream*`,
  `SDL_Event`, C `bool`, and callback-shaped audio APIs inside
  `Sdl3Binding.c`; public Stark code uses safe handles, result enums, a flat
  event record, and caller-owned byte buffers for audio stream I/O
- optional `Vendor.SDL3.Image` and `Vendor.SDL3.TTF` follow-ups

Native dependency acquisition:

- Linux packages: install SDL3 development files. The expected `pkg-config`
  package is `sdl3` when the distro/SDK provides one.
- macOS packages: use Homebrew/MacPorts `sdl3` or the official SDL3 framework.
- Windows packages: use vcpkg (`sdl3`) or official SDL3 development binaries.
- Stark fallback inputs: `SDL3_INCLUDE_DIR` and `SDL3_LIBRARY_DIR`; examples
  that require a display or audio device must have a deterministic headless
  skip/check mode.

## Tasks

- [x] Implement and verify `Vendor.LZ4`: use `pkg-config liblz4` or
  `LZ4_INCLUDE_DIR`/`LZ4_LIBRARY_DIR`, add direct C ABI declarations, safe
  caller-buffer block API, package metadata, build script, example, C#
  integration tests, and Stark self-hosted binding tests where expressible.
- [x] Implement and verify `Vendor.Zlib`: use `pkg-config zlib` or
  `ZLIB_INCLUDE_DIR`/`ZLIB_LIBRARY_DIR`, add one-shot and streaming APIs,
  package metadata, build script, example, C# integration tests, and Stark
  self-hosted binding tests where expressible.
- [x] Implement and verify `Vendor.Curl`: use `pkg-config libcurl` or
  `CURL_INCLUDE_DIR`/`CURL_LIBRARY_DIR` plus explicit TLS backend libraries,
  add easy-handle wrapper, write callback bridge, GET example, package
  metadata, C# integration tests, and Stark self-hosted binding tests where
  expressible.
- [x] Implement and verify `Vendor.STB.Image`: pin and vendor the required STB
  headers/sources, add owned image buffer API, load/write example, C#
  integration tests, and Stark self-hosted binding tests where expressible.
- [x] Implement and verify `Vendor.STB.Truetype`: pin and vendor
  `stb_truetype.h`, add font-info wrapper, glyph-metric/raster APIs, atlas or
  glyph example, C# integration tests, and Stark self-hosted binding tests
  where expressible.
- [x] Implement and verify `Vendor.Miniaudio`: pin and vendor `miniaudio.h`,
  add playback device/decoder wrapper, backend-specific package metadata,
  audio decode/playback example that can run headless or skip cleanly, C# integration
  tests, and Stark self-hosted binding tests where expressible.
- [x] Implement and verify `Vendor.Cgltf`: pin and vendor `cgltf.h`, add parser
  wrapper, safe views over core glTF data, small asset example, package
  metadata, C# integration tests, and Stark self-hosted binding tests where
  expressible.
- [x] Implement and verify `Vendor.GLFW`: use `pkg-config glfw3` or
  `GLFW_INCLUDE_DIR`/`GLFW_LIBRARY_DIR`, add init/window/input/context API,
  package metadata, non-graphical source checks plus graphical opt-in example,
  C# integration tests, and Stark self-hosted binding tests where expressible.
- [x] Implement and verify `Vendor.KbTextShape`: first choose and pin the native
  implementation/dependency set, then add segmentation/shaping API, font/text
  example, package metadata, C# integration tests, and Stark self-hosted binding
  tests where expressible.
- [x] Implement and verify generated `Vendor.Vulkan` loader/core slice: pin
  Khronos `vk.xml`, add a deterministic generator, safe loader-level API
  version/proc/count helpers, package metadata, build script, no-GPU loader
  example, C# integration audit coverage, and Stark self-hosted binding tests.
- [ ] Expand generated `Vendor.Vulkan` beyond the loader/core slice: generate
  the full enum/flag/struct/function-signature surface needed for device and
  extension dispatch tables, complete safe device/extension dispatch-table
  construction, and add an opt-in triangle or compute example.
- [x] Implement and verify `Vendor.SDL3`: use `pkg-config sdl3` or
  `SDL3_INCLUDE_DIR`/`SDL3_LIBRARY_DIR`, add init/window/event/audio basics,
  package metadata, example, C# integration tests, and Stark self-hosted
  binding tests where expressible.
- [x] Create and verify at least one `Vendor.LZ4` example under `examples/`
  that compresses and decompresses a small byte payload and checks round-trip
  equality.
- [x] Create and verify at least one `Vendor.Zlib` example under `examples/`
  that deflates and inflates a small payload or gzip-compatible stream.
- [x] Create and verify at least one `Vendor.Curl` example under `examples/`
  that performs an HTTP(S) GET and reports status/body data, with a deterministic
  local or skipped-network test path.
- [x] Create and verify at least one `Vendor.STB.Image` example under
  `examples/` that loads an image, inspects dimensions/channels, and writes or
  transforms image data.
- [x] Create and verify at least one `Vendor.STB.Truetype` example under
  `examples/` that loads font bytes, reads glyph metrics, and rasterizes one
  glyph or builds a tiny atlas.
- [x] Create and verify at least one `Vendor.Miniaudio` example under
  `examples/` that decodes audio or runs a playback smoke path, with headless
  CI behavior that skips cleanly when no device is available.
- [x] Create and verify at least one `Vendor.Cgltf` example under `examples/`
  that parses a tiny checked-in glTF asset and validates mesh/material/node
  counts.
- [x] Create and verify at least one `Vendor.GLFW` example under `examples/`
  that checks initialization and window/event wiring, with graphical execution
  opt-in and non-graphical source/package checks in CI.
- [x] Create and verify at least one `Vendor.KbTextShape` example under
  `examples/` that segments Unicode text and, when font data is available,
  shapes a short string into positioned glyphs.
- [x] Create and verify at least one `Vendor.Vulkan` example under `examples/`
  that loads Vulkan entry points and either enumerates instance/device facts or
  runs a minimal opt-in triangle/compute path.
- [x] Create and verify at least one `Vendor.SDL3` example under `examples/`
  that initializes SDL3, polls events or creates a window/renderer, and has a
  headless CI-safe check path.
- [x] Update user-facing docs, internals docs, website book, Stark skill, and
  VS Code docs/snippets for landed Vendor bindings through the
  `Vendor.Vulkan` loader/core slice.
- [ ] Update user-facing docs, internals docs, website book, Stark skill, and
  VS Code docs/snippets again when the full `Vendor.Vulkan` device/extension
  dispatch surface lands.
- [x] Add a generated or scripted vendor-binding audit check that rejects public
  raw handles when a safe wrapper exists, missing package-native metadata,
  undocumented native adapter sources, and examples that cannot compile through
  `vendor/dist`.
