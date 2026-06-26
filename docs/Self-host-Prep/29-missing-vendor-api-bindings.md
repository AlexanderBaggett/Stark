# Missing Vendor API Bindings

Short-lived audit/task document for tracking gaps between upstream native
library API surfaces and Stark's bundled `Vendor.*` bindings. Delete this file
when the gaps have either been implemented or moved into the normal roadmap.

Audit rule: for each retained Vendor binding, use the latest stable upstream
API surface as the source of truth, list what Stark currently exposes, and
document every missing upstream entry point. Stable public APIs become
implementation tasks. Deprecated, obsolete, private, or static-linking-only
APIs are documented separately and should not become first-class Stark APIs
unless a compatibility need is explicit.

## Audit Status

| Binding | Upstream surface audited | Status |
| --- | --- | --- |
| `Vendor.STB.Image` | STB snapshot `31c1ad37456438565541f4919958214b6e762fb4`: `stb_image.h` v2.30, `stb_image_write.h` v1.16, `stb_image_resize2.h` v2.18 | Incomplete; 8-bit load, PNG/BMP/TGA file write, and one packed uint8 linear resize path only |
| `Vendor.Miniaudio` | miniaudio `0.11.25` `miniaudio.h` | Incomplete; safe decode/playback helper only, with encoding/generation/resource-manager/node-graph/engine disabled in the native build |
| `Vendor.Cgltf` | cgltf `v1.15` `cgltf.h`, plus upstream-but-not-vendored `cgltf_write.h` | Incomplete; safe asset-summary wrapper only, no raw data graph, accessor payload reads, transform helpers, extensions/extras, custom options, or writer API |
| `Vendor.GLFW` | GLFW `3.4` `GLFW/glfw3.h`, `GLFW/glfw3native.h` | Incomplete; minimal window/context/input/Vulkan helper only, no monitors, cursors, clipboard, joystick/gamepad, gamma, full window attributes, full callbacks, native access, init/platform options, or complete constants |
| `Vendor.SQLite` | SQLite `3.53.2` official C interface, `sqlite3.h`, and `sqlite3ext.h` | Incomplete only at higher-level policy/builders; safe open/prepare/step/basic bind/basic column wrapper plus lifecycle/shared-cache/busy-timeout/lock-timeout helpers, checked db-config flag helpers, global directory variable helpers, typed global `sqlite3_config` wrappers for threading, logging, SQLLOG, memory statistics, lookaside, page cache, heap memory, URI/default planner toggles, mmap and sorter tuning, page-cache header-size readback, rowid-in-view, and Win32 heap sizing, test-control probes, deprecated memory-alarm clearing, SQLite printf text-literal helpers, raw `va_list` printf entrypoints, normalized-SQL and scan-status optional-symbol wrappers, optional snapshot owner/helpers, optional carray numeric/text/blob slice binding helpers, optional Win32 directory setters, extension-loading entrypoint, auto-extension controls, file-control raw/data-version/file-object helpers plus typed common file-control wrappers, VFS lookup/unsafe view registration helpers, virtual-table module registration/config/helper APIs, prepared-statement metadata, database introspection, limits, status counters, blob bind/result helpers, incremental blob I/O, backup, serialization/deserialization, WAL checkpoint/hook helpers, no-callback utility/introspection APIs, connection client-data keys/destructors, UTF-16 text copies, column metadata, scalar/aggregate/window custom functions, collation registration/needed callbacks, aggregate context, value/result helpers, SQLite-owned byte allocation, legacy get-table ownership, SQLite-owned filename construction, SQLite mutex owner/view/debug-assertion helpers, SQLite dynamic string-builder owner/helpers, database callback hooks, preupdate callback value accessors, explicit `Vendor.SQLite.Raw` ABI module, raw VFS/memory/mutex/io/pcache method-table carriers, full `sqlite3_api_routines` C-layout field table, all 304 official SQLite function names, and complete official constant coverage; no Stark-authored virtual-table/VFS safe builders or loadable-extension packaging policy |
| `Vendor.SDL3` | SDL `3.4.10` public symbol index and local `SDL3/SDL*.h` headers | Incomplete; init/window/renderer/event/audio-stream convenience wrapper only, no complete raw SDL3 surface, GPU, surfaces/textures, input devices, filesystem/storage, threading/sync, properties, dialogs, clipboard, platform, Vulkan/OpenGL/EGL/Metal, or complete constants/types |

## `Vendor.STB.Image`

### Source Of Truth

Audited against Stark's vendored STB snapshot:

- Upstream: <https://github.com/nothings/stb>
- Vendored commit: `31c1ad37456438565541f4919958214b6e762fb4`
- Vendored date: 2026-04-15
- Local headers:
  - `vendor/native/stb/stb_image.h` v2.30
  - `vendor/native/stb/stb_image_write.h` v1.16
  - `vendor/native/stb/stb_image_resize2.h` v2.18
- Current native implementation unit: `vendor/StbImageImplementation.c`

`StbImageImplementation.c` defines `STB_IMAGE_IMPLEMENTATION`,
`STB_IMAGE_WRITE_IMPLEMENTATION`, and `STB_IMAGE_RESIZE_IMPLEMENTATION`.
It does not define `STBI_NO_STDIO`, `STBI_WINDOWS_UTF8`,
`STBIW_WINDOWS_UTF8`, or `STBIR_PROFILE`, so stdio APIs are part of the
compiled surface, Windows wide-character conversion APIs are not compiled, and
resize profiling APIs are not compiled.

### Current Stark Coverage

Current file: `vendor/src/Vendor/STB/Image.stark`.

Stark currently exposes:

- `StbImageMaxNativeInputLength`
- `ImageChannels`
- `StbImageError`
- `StbImageStatus`
- `Image`
- `ImageResult`
- `LoadFromMemory(encoded, requestedChannels)`
- `LoadFromFile(filename, requestedChannels)`
- `WritePng(filename, image)`
- `WriteBmp(filename, image)`
- `WriteTga(filename, image)`
- `ResizeLinearInto(source, destination, outputWidth, outputHeight)`
- `ResizeLinear(source, outputWidth, outputHeight)`

Current internal native symbols:

- `stbi_load_from_memory`
- `stbi_load`
- `stbi_image_free`
- `stbi_write_png`
- `stbi_write_bmp`
- `stbi_write_tga`
- `stbir_resize_uint8_linear`

Assessment: `Vendor.STB.Image` is incomplete. The current binding is a useful
safe 8-bit image convenience layer, but it is not a full binding for the
vendored STB image stack. It omits most decode variants, image probes, load
controls, write targets/formats, writer controls, srgb and float resize paths,
medium-complexity resize controls, and the extended resize API that lets users
prebuild samplers and split a resize across threads.

### Covered By Stark Equivalents Or Internal Ownership

- `stbi_uc`: represented naturally as `u8`.
- `stbi_us`: represented naturally as `u16`.
- `STBI_default`, `STBI_grey`, `STBI_grey_alpha`, `STBI_rgb`,
  `STBI_rgb_alpha`: covered semantically by `ImageChannels`, though the native
  names are not exported.
- `stbi_image_free`: bound internally and used after copying STB-owned pixels
  into Stark-owned `dynamic` storage. This is appropriate for the current safe
  wrapper shape. If raw load APIs are added, a raw unsafe free surface or a
  Stark-owned native image handle will still be needed.
- `stbir_resize_uint8_linear`: bound internally, but only exposed through a
  packed `Image` wrapper using the source channel count as the pixel layout.
  A public full-argument wrapper is still missing for arbitrary strides and
  layouts.

### Missing Public Decode API From `stb_image.h`

- `STBI_VERSION`: expose the native header API version as a Stark constant.
- `stbi_io_callbacks`: expose the C callback carrier once callback ABI support
  is strong enough.
- `stbi_load_from_callbacks`: 8-bit decode from arbitrary callback input.
- `stbi_load_from_file`: 8-bit decode from an existing `FILE *`.
- `stbi_load_gif_from_memory`: animated GIF decode with frame count and per-frame delays.
- `stbi_load_16_from_memory`: 16-bit decode from memory.
- `stbi_load_16_from_callbacks`: 16-bit decode from callback input.
- `stbi_load_16`: 16-bit decode from filename.
- `stbi_load_from_file_16`: 16-bit decode from an existing `FILE *`.
- `stbi_loadf_from_memory`: float decode from memory.
- `stbi_loadf_from_callbacks`: float decode from callback input.
- `stbi_loadf`: float decode from filename.
- `stbi_loadf_from_file`: float decode from an existing `FILE *`.
- `stbi_hdr_to_ldr_gamma`: configure HDR-to-LDR gamma conversion.
- `stbi_hdr_to_ldr_scale`: configure HDR-to-LDR scale conversion.
- `stbi_ldr_to_hdr_gamma`: configure LDR-to-HDR gamma conversion.
- `stbi_ldr_to_hdr_scale`: configure LDR-to-HDR scale conversion.
- `stbi_is_hdr_from_callbacks`: query HDR status from callback input.
- `stbi_is_hdr_from_memory`: query HDR status from memory.
- `stbi_is_hdr`: query HDR status from filename.
- `stbi_is_hdr_from_file`: query HDR status from an existing `FILE *`.
- `stbi_failure_reason`: expose the most recent decode failure reason.
- `stbi_info_from_memory`: query width, height, and component count without decoding from memory.
- `stbi_info_from_callbacks`: query width, height, and component count without decoding from callback input.
- `stbi_info`: query width, height, and component count without decoding from filename.
- `stbi_info_from_file`: query width, height, and component count without decoding from an existing `FILE *`.
- `stbi_is_16_bit_from_memory`: query whether memory input is 16-bit.
- `stbi_is_16_bit_from_callbacks`: query whether callback input is 16-bit.
- `stbi_is_16_bit`: query whether filename input is 16-bit.
- `stbi_is_16_bit_from_file`: query whether `FILE *` input is 16-bit.
- `stbi_set_unpremultiply_on_load`: process premultiplied iPhone PNG alpha globally.
- `stbi_convert_iphone_png_to_rgb`: convert iPhone PNG BGRA data globally.
- `stbi_set_flip_vertically_on_load`: flip all decoded images globally.
- `stbi_set_unpremultiply_on_load_thread`: thread-local premultiplied alpha setting.
- `stbi_convert_iphone_png_to_rgb_thread`: thread-local iPhone PNG conversion setting.
- `stbi_set_flip_vertically_on_load_thread`: thread-local vertical flip setting.
- `stbi_zlib_decode_malloc_guesssize`: zlib decode to STB-allocated output.
- `stbi_zlib_decode_malloc_guesssize_headerflag`: zlib/no-header decode to STB-allocated output.
- `stbi_zlib_decode_malloc`: zlib decode to STB-allocated output.
- `stbi_zlib_decode_buffer`: zlib decode into caller storage.
- `stbi_zlib_decode_noheader_malloc`: raw deflate decode to STB-allocated output.
- `stbi_zlib_decode_noheader_buffer`: raw deflate decode into caller storage.

Conditional decode API not currently compiled:

- `stbi_convert_wchar_to_utf8`: requires defining `STBI_WINDOWS_UTF8`.

### Missing Public Write API From `stb_image_write.h`

- `stbi_write_tga_with_rle`: exported global writer setting for TGA RLE.
- `stbi_write_png_compression_level`: exported global writer setting for PNG compression level.
- `stbi_write_force_png_filter`: exported global writer setting for forced PNG filter mode.
- `stbi_write_hdr`: write float HDR image data to a filename.
- `stbi_write_jpg`: write 8-bit image data to a filename with quality control.
- `stbi_write_png_to_func`: write PNG bytes through a callback.
- `stbi_write_bmp_to_func`: write BMP bytes through a callback.
- `stbi_write_tga_to_func`: write TGA bytes through a callback.
- `stbi_write_hdr_to_func`: write HDR bytes through a callback.
- `stbi_write_jpg_to_func`: write JPG bytes through a callback.
- `stbi_flip_vertically_on_write`: global writer vertical flip setting.

Exported but less-prominent `stb_image_write.h` symbols also missing:

- `stbi_write_png_to_mem`: write PNG bytes to STB-allocated memory.
- `stbi_zlib_compress`: compress bytes with STB's writer zlib helper.

Conditional write API not currently compiled:

- `stbiw_convert_wchar_to_utf8`: requires defining `STBIW_WINDOWS_UTF8`.

### Missing Resize API From `stb_image_resize2.h`

Missing public pixel-layout constants:

- `STBIR_1CHANNEL`
- `STBIR_2CHANNEL`
- `STBIR_RGB`
- `STBIR_BGR`
- `STBIR_4CHANNEL`
- `STBIR_RGBA`
- `STBIR_BGRA`
- `STBIR_ARGB`
- `STBIR_ABGR`
- `STBIR_RA`
- `STBIR_AR`
- `STBIR_RGBA_PM`
- `STBIR_BGRA_PM`
- `STBIR_ARGB_PM`
- `STBIR_ABGR_PM`
- `STBIR_RA_PM`
- `STBIR_AR_PM`
- `STBIR_RGBA_NO_AW`
- `STBIR_BGRA_NO_AW`
- `STBIR_ARGB_NO_AW`
- `STBIR_ABGR_NO_AW`
- `STBIR_RA_NO_AW`
- `STBIR_AR_NO_AW`

Missing public edge-mode constants:

- `STBIR_EDGE_CLAMP`
- `STBIR_EDGE_REFLECT`
- `STBIR_EDGE_WRAP`
- `STBIR_EDGE_ZERO`

Missing public filter constants:

- `STBIR_FILTER_DEFAULT`
- `STBIR_FILTER_BOX`
- `STBIR_FILTER_TRIANGLE`
- `STBIR_FILTER_CUBICBSPLINE`
- `STBIR_FILTER_CATMULLROM`
- `STBIR_FILTER_MITCHELL`
- `STBIR_FILTER_POINT_SAMPLE`
- `STBIR_FILTER_OTHER`

Missing public data-type constants:

- `STBIR_TYPE_UINT8`
- `STBIR_TYPE_UINT8_SRGB`
- `STBIR_TYPE_UINT8_SRGB_ALPHA`
- `STBIR_TYPE_UINT16`
- `STBIR_TYPE_FLOAT`
- `STBIR_TYPE_HALF_FLOAT`

Missing public resize functions and types:

- `stbir_resize_uint8_srgb`: easy 8-bit srgb resize.
- `stbir_resize_uint8_linear`: full-argument public wrapper for arbitrary layout and stride.
- `stbir_resize_float_linear`: easy float resize.
- `stbir_resize`: medium-complexity resize with data type, edge mode, and filter control.
- `stbir_input_callback`: input scanline callback type.
- `stbir_output_callback`: output scanline callback type.
- `stbir__kernel_callback`: custom filter kernel callback type.
- `stbir__support_callback`: custom filter support callback type.
- `STBIR_RESIZE`: extended resize state carrier.
- `stbir_resize_init`: initialize extended resize state.
- `stbir_set_datatypes`: set separate input and output data types.
- `stbir_set_pixel_callbacks`: set input/output pixel callbacks.
- `stbir_set_user_data`: set callback/user context data.
- `stbir_set_buffer_ptrs`: update input/output buffers and strides.
- `stbir_set_pixel_layouts`: set separate input and output pixel layouts.
- `stbir_set_edgemodes`: set horizontal and vertical edge modes.
- `stbir_set_filters`: set horizontal and vertical filters.
- `stbir_set_filter_callbacks`: set custom filter callbacks.
- `stbir_set_pixel_subrect`: set matching input/output pixel subrects.
- `stbir_set_input_subrect`: set subpixel input subrect.
- `stbir_set_output_pixel_subrect`: set output pixel subrect.
- `stbir_set_non_pm_alpha_speed_over_quality`: choose faster lower-quality non-premultiplied-alpha handling.
- `stbir_build_samplers`: prebuild samplers for repeated resizes.
- `stbir_free_samplers`: free prebuilt sampler storage.
- `stbir_resize_extended`: execute an extended resize.
- `stbir_build_samplers_with_splits`: prebuild samplers for split/threaded resize.
- `stbir_resize_extended_split`: execute one split of a threaded resize.

Conditional resize profile API not currently compiled:

- `STBIR_PROFILE_INFO`: profile result carrier.
- `stbir_resize_build_profile_info`: collect build-sampler profile info.
- `stbir_resize_extended_profile_info`: collect synchronous resize profile info.
- `stbir_resize_split_profile_info`: collect split resize profile info.

### Binding Gaps That Need Design Attention

- Callback APIs need `ffi(c)` callback carriers or a tiny C bridge. Prefer direct
  Stark `fnptr<ffi(c)>` once callback ABI/lifetime tests are solid; use C shims
  only for real lifetime or calling-convention adaptation.
- `FILE *` APIs need an agreed C file-handle carrier, likely from `System.C`.
  They can be unsafe raw APIs first; safe file-path and memory APIs should stay
  the normal user-facing path.
- Writer configuration globals (`stbi_write_tga_with_rle`,
  `stbi_write_png_compression_level`, `stbi_write_force_png_filter`) need either
  direct FFI global-variable support or narrow native getter/setter shims. Direct
  FFI globals are preferable if the compiler supports them; accessors are an
  acceptable ABI bridge if it does not.
- 16-bit and float decode/write APIs should not be squeezed into the existing
  `Image` byte container. Add distinct `Image16` and `ImageF32`, or a generic
  image carrier only if Stark's current generic/layout support keeps it zero
  overhead.
- Extended resize state should be modeled as an ABI carrier with explicit
  lifetime rules. The performance win is sampler reuse and split execution; do
  not hide that behind an allocation-heavy wrapper.

### Tasks

- [ ] Add STB decode constants, types, and metadata carriers:
  - expose `STBI_VERSION`
  - expose native channel constants or document `ImageChannels` as the stable Stark mapping
  - add `ImageInfo` for width, height, and component count
  - add `Image16` for `u16` pixels
  - add `ImageF32` for `f32` pixels
  - add `GifImage` or `AnimatedImage` with frame count, per-frame delays, channel count, and owned pixels
  - add C# compiler/package-image tests for the new public API shape
  - add Stark self-hosted source/API tests

- [ ] Add missing `stb_image.h` memory and filename APIs:
  - bind and wrap `stbi_info_from_memory`
  - bind and wrap `stbi_info`
  - bind and wrap `stbi_is_hdr_from_memory`
  - bind and wrap `stbi_is_hdr`
  - bind and wrap `stbi_is_16_bit_from_memory`
  - bind and wrap `stbi_is_16_bit`
  - bind and wrap `stbi_load_16_from_memory`
  - bind and wrap `stbi_load_16`
  - bind and wrap `stbi_loadf_from_memory`
  - bind and wrap `stbi_loadf`
  - bind and wrap `stbi_load_gif_from_memory`
  - bind and wrap `stbi_failure_reason`
  - test success/failure paths with tiny embedded PNG/PPM/HDR-or-float-capable fixtures where practical
  - test invalid input, oversized input, unsupported component counts, and ownership cleanup

- [ ] Add missing `stb_image.h` global and thread-local decode controls:
  - bind `stbi_hdr_to_ldr_gamma`
  - bind `stbi_hdr_to_ldr_scale`
  - bind `stbi_ldr_to_hdr_gamma`
  - bind `stbi_ldr_to_hdr_scale`
  - bind `stbi_set_unpremultiply_on_load`
  - bind `stbi_convert_iphone_png_to_rgb`
  - bind `stbi_set_flip_vertically_on_load`
  - bind `stbi_set_unpremultiply_on_load_thread`
  - bind `stbi_convert_iphone_png_to_rgb_thread`
  - bind `stbi_set_flip_vertically_on_load_thread`
  - test that flip settings affect deterministic image fixtures
  - document global-vs-thread-local state in the module docs

- [ ] Add missing `stb_image.h` callback and `FILE *` decode APIs:
  - bind `stbi_load_from_callbacks`
  - bind `stbi_load_from_file`
  - bind `stbi_load_16_from_callbacks`
  - bind `stbi_load_from_file_16`
  - bind `stbi_loadf_from_callbacks`
  - bind `stbi_loadf_from_file`
  - bind `stbi_info_from_callbacks`
  - bind `stbi_info_from_file`
  - bind `stbi_is_hdr_from_callbacks`
  - bind `stbi_is_hdr_from_file`
  - bind `stbi_is_16_bit_from_callbacks`
  - bind `stbi_is_16_bit_from_file`
  - add callback lifetime and non-unwinding tests before exposing safe wrappers
  - add `FILE *` tests once `System.C` has a stable file-handle carrier

- [ ] Decide and implement policy for STB zlib helper exposure:
  - either leave STB's private zlib helper family unsupported, or expose it from `Vendor.STB.Image` for upstream API completeness
  - if exposed, bind `stbi_zlib_decode_malloc_guesssize`
  - bind `stbi_zlib_decode_malloc_guesssize_headerflag`
  - bind `stbi_zlib_decode_malloc`
  - bind `stbi_zlib_decode_buffer`
  - bind `stbi_zlib_decode_noheader_malloc`
  - bind `stbi_zlib_decode_noheader_buffer`
  - copy STB-allocated outputs into Stark-owned storage or wrap them in a native-owned handle with deterministic free
  - test zlib and raw-deflate success/failure paths

- [ ] Add missing `stb_image_write.h` file writers and writer controls:
  - bind and wrap `stbi_write_hdr`
  - bind and wrap `stbi_write_jpg`
  - bind `stbi_flip_vertically_on_write`
  - expose `stbi_write_tga_with_rle`
  - expose `stbi_write_png_compression_level`
  - expose `stbi_write_force_png_filter`
  - use direct FFI global-variable support if available; otherwise add narrow getter/setter shims
  - test PNG/BMP/TGA/JPG/HDR output with deterministic small images
  - test quality/compression/filter/flip settings where the output difference is deterministic

- [ ] Add missing `stb_image_write.h` callback and memory writers:
  - bind `stbi_write_png_to_func`
  - bind `stbi_write_bmp_to_func`
  - bind `stbi_write_tga_to_func`
  - bind `stbi_write_hdr_to_func`
  - bind `stbi_write_jpg_to_func`
  - bind `stbi_write_png_to_mem`
  - decide whether `stbi_zlib_compress` belongs in the public `Vendor.STB.Image` surface or stays internal to STB PNG writing
  - add safe callback adapters only after callback ABI/lifetime tests exist
  - test PNG-to-memory round trip through `LoadFromMemory`

- [ ] Add missing resize constants and simple resize APIs:
  - expose `StbirPixelLayout` with every public `STBIR_*` pixel-layout value
  - expose `StbirEdge` with every public edge value
  - expose `StbirFilter` with every public filter value
  - expose `StbirDataType` with every public data-type value
  - bind and wrap full-argument `stbir_resize_uint8_linear`
  - bind and wrap `stbir_resize_uint8_srgb`
  - bind and wrap `stbir_resize_float_linear`
  - preserve explicit stride and pixel-layout control for performance-sensitive callers
  - test linear, srgb, and float resize paths on deterministic tiny fixtures

- [ ] Add the medium-complexity resize API:
  - bind and wrap `stbir_resize`
  - expose data type, edge mode, filter, stride, and pixel layout without hidden allocation
  - test clamp/reflect/zero edge behavior and point/triangle/mitchell filters where deterministic

- [ ] Add the extended resize API with reusable sampler support:
  - model `STBIR_RESIZE` as an ABI-layout Stark struct or opaque native-owned handle
  - bind `stbir_resize_init`
  - bind `stbir_set_datatypes`
  - bind `stbir_set_pixel_callbacks`
  - bind `stbir_set_user_data`
  - bind `stbir_set_buffer_ptrs`
  - bind `stbir_set_pixel_layouts`
  - bind `stbir_set_edgemodes`
  - bind `stbir_set_filters`
  - bind `stbir_set_filter_callbacks`
  - bind `stbir_set_pixel_subrect`
  - bind `stbir_set_input_subrect`
  - bind `stbir_set_output_pixel_subrect`
  - bind `stbir_set_non_pm_alpha_speed_over_quality`
  - bind `stbir_build_samplers`
  - bind `stbir_free_samplers`
  - bind `stbir_resize_extended`
  - bind `stbir_build_samplers_with_splits`
  - bind `stbir_resize_extended_split`
  - test sampler reuse over multiple frames without repeated sampler allocation
  - test split execution with deterministic per-split output assembly
  - benchmark simple vs extended sampler-reuse resize for the performance cookbook/examples

- [ ] Decide whether to enable conditional STB APIs:
  - if Windows UTF-8 conversion is required, compile with `STBI_WINDOWS_UTF8` and bind `stbi_convert_wchar_to_utf8`
  - if writer Windows UTF-8 conversion is required, compile with `STBIW_WINDOWS_UTF8` and bind `stbiw_convert_wchar_to_utf8`
  - if resize profiling is useful for benchmarks, compile a profiling variant with `STBIR_PROFILE`
  - if profiling is enabled, bind `STBIR_PROFILE_INFO`
  - bind `stbir_resize_build_profile_info`
  - bind `stbir_resize_extended_profile_info`
  - bind `stbir_resize_split_profile_info`

- [ ] Update the STB image example set:
  - keep the current resize example
  - add an image-info/probe example
  - add a 16-bit or float load example if a small deterministic fixture is available
  - add JPG or HDR write example once those writers land
  - add PNG-to-memory round-trip example once memory writing lands
  - add extended-resize sampler-reuse example once the extended API lands
  - keep console output as simple `WriteLine(...)` calls without checking every status

- [ ] Add C# compiler/integration tests for every new STB Image surface:
  - package-image metadata and native symbol availability
  - safe wrapper success and failure paths
  - ownership cleanup for STB-allocated decode/write buffers
  - callback ABI/lifetime behavior when callback APIs are exposed
  - deterministic image output/probe assertions
  - resize correctness and stride/layout handling

- [ ] Add Stark self-hosted tests for every new STB Image surface that the current Stark test harness can express:
  - source-level API shape tests
  - memory load/probe tests
  - write/read round-trip tests
  - resize correctness tests
  - extended sampler API tests as ABI carriers and native linking support allow

## `Vendor.Miniaudio`

### Source Of Truth

Audited against latest stable upstream miniaudio release `0.11.25`, which
matches Stark's vendored snapshot:

- Upstream: <https://github.com/mackron/miniaudio>
- Documentation: <https://miniaud.io/docs/manual/index.html>
- Release: <https://github.com/mackron/miniaudio/releases/tag/0.11.25>
- Vendored commit: `9634bedb5b5a2ca38c1ee7108a9358a4e233f14d`
- Vendored date: 2026-03-03
- Local header: `vendor/native/miniaudio/miniaudio.h`
- Current native implementation unit: `vendor/MiniaudioImplementation.c`

The current native implementation defines these feature-removal macros before
including `miniaudio.h`:

- `MA_NO_ENCODING`
- `MA_NO_GENERATION`
- `MA_NO_RESOURCE_MANAGER`
- `MA_NO_NODE_GRAPH`
- `MA_NO_ENGINE`

That means the compiled `libVendorMiniaudio.a` intentionally omits encoding,
waveform/noise generation, resource management, node graph, and engine support.
The audit below still uses the full `0.11.25` public surface as source of
truth, because a complete vendor binding should either expose those families or
explicitly keep them unsupported.

The public function inventory was extracted from the pinned header with:

```bash
awk '
  /^MA_API / {
    line = $0
    sub(/\(.*/, "", line)
    n = split(line, parts, /[ *\t]+/)
    name = parts[n]
    if (name ~ /^(ma|ma_dr)_/) print name
  }
' vendor/native/miniaudio/miniaudio.h \
  | sort -u
```

This produces 1,190 unique public `MA_API` functions in `0.11.25`. The
family counts from that inventory are:

- core/log/allocation/thread/string/platform helpers: 95
- PCM conversion, channel maps, volume, resampling, and data converters: 151
- DSP/filtering/spatialization helpers: 240
- context/device/duplex device helpers: 36
- VFS helpers: 19
- data source helpers: 30
- audio buffer and paged audio buffer helpers: 41
- ring buffer and PCM ring buffer helpers: 38
- high-level decoding facade and built-in `ma_wav`/`ma_mp3`/`ma_flac`/`ma_stbvorbis` wrappers: 67
- embedded dr_wav API: 94
- embedded dr_flac API: 31
- embedded dr_mp3 API: 29
- encoder API: 10
- waveform/noise generation API: 19
- resource manager API: 64
- node graph API: 41
- engine API: 38
- sound and sound group API: 142
- miscellaneous backend/buffer-size/decoding-backend helpers: 6

### Current Stark Coverage

Current file: `vendor/src/Vendor/Miniaudio.stark`.

Stark currently exposes:

- `MiniAudioMaxNativeSampleCount`
- `SampleFormat` with `S16` and `F32` only
- `MiniAudioError`
- `MiniAudioStatus`
- `DecoderResult`
- `DecoderInfoResult`
- `ReadFramesResult`
- `PlaybackDeviceResult`
- `DecoderInfo`
- `Decoder`
- `PlaybackDevice`
- `OpenDecoderFromMemory(encoded, outputFormat, outputChannels, outputSampleRate)`
- `OpenDecoderFromFile(path, outputFormat, outputChannels, outputSampleRate)`
- `GetDecoderInfo(decoder)`
- `ReadF32Frames(decoder, destination, frameCount)`
- `ReadS16Frames(decoder, destination, frameCount)`
- `SeekToPcmFrame(decoder, frameIndex)`
- `CreatePlaybackDeviceF32(samples, frameCount, channels, sampleRate)`
- `Start(device)`
- `Stop(device)`
- `IsComplete(device)`

Current internal native symbols:

- `stark_ma_decoder_create_memory`
- `stark_ma_decoder_create_file`
- `stark_ma_decoder_destroy`
- `stark_ma_decoder_get_info`
- `stark_ma_decoder_read_pcm_frames`
- `stark_ma_decoder_seek_to_pcm_frame`
- `stark_ma_playback_create_f32`
- `stark_ma_playback_destroy`
- `stark_ma_playback_start`
- `stark_ma_playback_stop`
- `stark_ma_playback_is_complete`

The C shim internally uses a small subset of upstream miniaudio:

- `ma_decoder_config_init`
- `ma_decoder_init_memory`
- `ma_decoder_init_file`
- `ma_decoder_uninit`
- `ma_decoder_get_data_format`
- `ma_decoder_get_length_in_pcm_frames`
- `ma_decoder_read_pcm_frames`
- `ma_decoder_seek_to_pcm_frame`
- `ma_device_config_init`
- `ma_device_init`
- `ma_device_uninit`
- `ma_device_start`
- `ma_device_stop`

Assessment: `Vendor.Miniaudio` is incomplete. It is a safe convenience wrapper
for decoding to `f32`/`s16` and playing a complete `f32` sample buffer through a
default playback device. It does not expose miniaudio's low-level device/context
API, capture/duplex/loopback, device enumeration, callbacks, VFS, data-source
interface, conversion/resampling/filtering DSP, built-in encoder, generators,
audio/ring buffers, raw dr_wav/dr_flac/dr_mp3 APIs, resource manager, node graph,
engine, sounds, sound groups, spatialization controls, backend controls, or most
native result/format/backend constants and ABI carriers.

### Covered By Stark Equivalents Or Internal Ownership

- `ma_decoder_config_init`, `ma_decoder_init_memory`, `ma_decoder_init_file`,
  `ma_decoder_uninit`, `ma_decoder_get_data_format`,
  `ma_decoder_get_length_in_pcm_frames`, `ma_decoder_read_pcm_frames`, and
  `ma_decoder_seek_to_pcm_frame` are covered only through the current owned
  `Decoder` wrapper.
- `ma_device_config_init`, `ma_device_init`, `ma_device_uninit`,
  `ma_device_start`, and `ma_device_stop` are covered only through a restricted
  default playback wrapper that copies a complete `f32` sample buffer.
- `ma_format_f32` and `ma_format_s16` are covered by `SampleFormat.F32` and
  `SampleFormat.S16`. `ma_format_u8`, `ma_format_s24`, `ma_format_s32`, and
  `ma_format_unknown` are not exposed.
- `MA_MAX_CHANNELS` is enforced indirectly through the `u8[1 254]` public
  channel range, but the native constant is not exported.

### Missing Core And ABI Foundation

Missing core function families include every public function in these extracted
groups:

- version/result helpers: `ma_version`, `ma_version_string`,
  `ma_result_description`
- allocation and aligned allocation: `ma_malloc`, `ma_calloc`, `ma_realloc`,
  `ma_free`, `ma_aligned_malloc`, `ma_aligned_free`
- logging: `ma_log_callback_init`, `ma_log_init`, `ma_log_uninit`,
  `ma_log_register_callback`, `ma_log_unregister_callback`, `ma_log_post`,
  `ma_log_postv`, `ma_log_postf`, `ma_log_level_to_string`
- dynamic library helpers: `ma_dlopen`, `ma_dlclose`, `ma_dlsym`
- file/string helpers: `ma_fopen`, `ma_wfopen`, `ma_copy_string`,
  `ma_copy_string_w`, `ma_strcmp`, `ma_strcmp_WCHAR`, `ma_strcpy_s`,
  `ma_strcpy_s_WCHAR`, `ma_strlen_WCHAR`, `ma_strcat_s`, `ma_strncat_s`,
  `ma_strncpy_s`, `ma_strappend`, `ma_wcscmp`, `ma_wcscpy_s`, `ma_wcslen`,
  `ma_itoa_s`
- threading/synchronization helpers: `ma_mutex_init`, `ma_mutex_uninit`,
  `ma_mutex_lock`, `ma_mutex_unlock`, `ma_semaphore_init`,
  `ma_semaphore_uninit`, `ma_semaphore_wait`, `ma_semaphore_release`,
  `ma_event_init`, `ma_event_uninit`, `ma_event_wait`, `ma_event_signal`,
  `ma_fence_init`, `ma_fence_uninit`, `ma_fence_acquire`, `ma_fence_release`,
  `ma_fence_wait`, `ma_spinlock_lock`, `ma_spinlock_lock_noyield`,
  `ma_spinlock_unlock`
- async/job/slot helpers: `ma_async_notification_event_init`,
  `ma_async_notification_event_uninit`, `ma_async_notification_event_wait`,
  `ma_async_notification_event_signal`, `ma_async_notification_poll_init`,
  `ma_async_notification_poll_is_signalled`, `ma_async_notification_signal`,
  `ma_job_init`, `ma_job_process`, `ma_job_queue_config_init`,
  `ma_job_queue_get_heap_size`, `ma_job_queue_init`,
  `ma_job_queue_init_preallocated`, `ma_job_queue_uninit`,
  `ma_job_queue_post`, `ma_job_queue_next`, `ma_slot_allocator_config_init`,
  `ma_slot_allocator_get_heap_size`, `ma_slot_allocator_init`,
  `ma_slot_allocator_init_preallocated`, `ma_slot_allocator_uninit`,
  `ma_slot_allocator_alloc`, `ma_slot_allocator_free`
- backend/vector helpers: `ma_get_backend_name`, `ma_get_backend_from_name`,
  `ma_get_enabled_backends`, `ma_is_backend_enabled`, `ma_is_loopback_supported`,
  `ma_atomic_vec3f_init`, `ma_atomic_vec3f_get`, `ma_atomic_vec3f_set`,
  `ma_vec3f_init_3f`, `ma_vec3f_sub`, `ma_vec3f_len`, `ma_vec3f_len2`,
  `ma_vec3f_dot`, `ma_vec3f_cross`, `ma_vec3f_normalize`, `ma_vec3f_dist`,
  `ma_vec3f_neg`

Missing ABI constants and carriers include the result-code enum, backend enum,
device type/state/notification enums, sample-format enum, standard channel maps,
channel positions, allocation callback carrier, log carrier, thread primitives,
job/slot carriers, `ma_vec3f`, and the public config structs used by every
family below.

### Missing Device, Context, Capture, Duplex, And Loopback API

Missing function families:

- context initialization/enumeration: `ma_context_config_init`,
  `ma_context_sizeof`, `ma_context_init`, `ma_context_uninit`,
  `ma_context_get_log`, `ma_context_enumerate_devices`,
  `ma_context_get_devices`, `ma_context_get_device_info`,
  `ma_context_is_loopback_supported`
- device initialization/state/info: `ma_device_config_init`, `ma_device_init`,
  `ma_device_init_ex`, `ma_device_uninit`, `ma_device_get_context`,
  `ma_device_get_log`, `ma_device_get_info`, `ma_device_get_name`,
  `ma_device_start`, `ma_device_stop`, `ma_device_is_started`,
  `ma_device_get_state`, `ma_device_post_init`,
  `ma_device_handle_backend_data_callback`, `ma_device_id_equal`,
  `ma_device_info_add_native_data_format`
- device volume: `ma_device_set_master_volume`,
  `ma_device_get_master_volume`, `ma_device_set_master_volume_db`,
  `ma_device_get_master_volume_db`
- device job thread and duplex ring buffer helpers:
  `ma_device_job_thread_config_init`, `ma_device_job_thread_init`,
  `ma_device_job_thread_uninit`, `ma_device_job_thread_post`,
  `ma_device_job_thread_next`, `ma_duplex_rb_init`, `ma_duplex_rb_uninit`

The current Stark playback wrapper does not expose capture, full-duplex,
loopback, specific device selection, device enumeration, data callbacks,
period/buffer sizing, backend selection, backend-specific fields, master volume,
device state, or callback lifetime rules.

### Missing VFS And Data Source API

Missing VFS functions:

- `ma_default_vfs_init`
- `ma_vfs_open`
- `ma_vfs_open_w`
- `ma_vfs_close`
- `ma_vfs_read`
- `ma_vfs_write`
- `ma_vfs_seek`
- `ma_vfs_tell`
- `ma_vfs_info`
- `ma_vfs_open_and_read_file`
- `ma_vfs_open_and_read_file_w`
- `ma_vfs_or_default_open`
- `ma_vfs_or_default_open_w`
- `ma_vfs_or_default_close`
- `ma_vfs_or_default_read`
- `ma_vfs_or_default_write`
- `ma_vfs_or_default_seek`
- `ma_vfs_or_default_tell`
- `ma_vfs_or_default_info`

Missing data-source functions:

- `ma_data_source_config_init`
- `ma_data_source_init`
- `ma_data_source_uninit`
- `ma_data_source_read_pcm_frames`
- `ma_data_source_seek_pcm_frames`
- `ma_data_source_seek_to_pcm_frame`
- `ma_data_source_seek_seconds`
- `ma_data_source_seek_to_second`
- `ma_data_source_get_data_format`
- `ma_data_source_get_cursor_in_pcm_frames`
- `ma_data_source_get_cursor_in_seconds`
- `ma_data_source_get_length_in_pcm_frames`
- `ma_data_source_get_length_in_seconds`
- `ma_data_source_set_looping`
- `ma_data_source_is_looping`
- `ma_data_source_set_range_in_pcm_frames`
- `ma_data_source_get_range_in_pcm_frames`
- `ma_data_source_set_loop_point_in_pcm_frames`
- `ma_data_source_get_loop_point_in_pcm_frames`
- `ma_data_source_set_current`
- `ma_data_source_get_current`
- `ma_data_source_set_next`
- `ma_data_source_get_next`
- `ma_data_source_set_next_callback`
- `ma_data_source_get_next_callback`
- `ma_data_source_node_config_init`
- `ma_data_source_node_init`
- `ma_data_source_node_uninit`
- `ma_data_source_node_set_looping`
- `ma_data_source_node_is_looping`

### Missing Audio Buffer And Ring Buffer API

Missing audio buffer functions:

- `ma_audio_buffer_config_init`
- `ma_audio_buffer_alloc_and_init`
- `ma_audio_buffer_init`
- `ma_audio_buffer_init_copy`
- `ma_audio_buffer_uninit`
- `ma_audio_buffer_uninit_and_free`
- `ma_audio_buffer_read_pcm_frames`
- `ma_audio_buffer_seek_to_pcm_frame`
- `ma_audio_buffer_map`
- `ma_audio_buffer_unmap`
- `ma_audio_buffer_at_end`
- `ma_audio_buffer_get_available_frames`
- `ma_audio_buffer_get_cursor_in_pcm_frames`
- `ma_audio_buffer_get_length_in_pcm_frames`
- `ma_audio_buffer_ref_init`
- `ma_audio_buffer_ref_uninit`
- `ma_audio_buffer_ref_read_pcm_frames`
- `ma_audio_buffer_ref_seek_to_pcm_frame`
- `ma_audio_buffer_ref_map`
- `ma_audio_buffer_ref_unmap`
- `ma_audio_buffer_ref_at_end`
- `ma_audio_buffer_ref_set_data`
- `ma_audio_buffer_ref_get_available_frames`
- `ma_audio_buffer_ref_get_cursor_in_pcm_frames`
- `ma_audio_buffer_ref_get_length_in_pcm_frames`
- every `ma_paged_audio_buffer_*` and `ma_paged_audio_buffer_data_*` function

Missing ring buffer functions:

- every `ma_rb_*` function
- every `ma_pcm_rb_*` function

These are performance-sensitive APIs. Stark wrappers should preserve explicit
caller-owned storage, preallocated initialization, and zero-copy map/acquire
operations rather than forcing hidden dynamic allocation.

### Missing PCM Conversion, Resampling, Volume, And Channel Map API

Missing function families:

- sample conversion: every `ma_pcm_*_to_*` function, `ma_pcm_convert`,
  `ma_convert_pcm_frames_format`, `ma_convert_frames`, and
  `ma_convert_frames_ex`
- interleave/deinterleave/copy/silence/mix/blend/clip: every
  `ma_pcm_interleave_*`, `ma_pcm_deinterleave_*`, `ma_interleave_pcm_frames`,
  `ma_deinterleave_pcm_frames`, `ma_copy_pcm_frames`, `ma_silence_pcm_frames`,
  `ma_mix_pcm_frames_f32`, `ma_blend_f32`, `ma_clip_samples_*`, and
  `ma_clip_pcm_frames`
- volume helpers: every `ma_apply_volume_factor*`,
  `ma_copy_and_apply_volume_factor*`,
  `ma_copy_and_apply_volume_and_clip*`, `ma_volume_db_to_linear`, and
  `ma_volume_linear_to_db`
- channel map helpers: `ma_channel_map_init_blank`,
  `ma_channel_map_init_standard`, `ma_channel_map_copy`,
  `ma_channel_map_copy_or_default`, `ma_channel_map_is_valid`,
  `ma_channel_map_is_blank`, `ma_channel_map_is_equal`,
  `ma_channel_map_get_channel`, `ma_channel_map_find_channel_position`,
  `ma_channel_map_contains_channel_position`, `ma_channel_map_to_string`, and
  `ma_channel_position_to_string`
- channel converter: every `ma_channel_converter_*` function
- linear and general resamplers: every `ma_linear_resampler_*` and
  `ma_resampler_*` function
- data converter: every `ma_data_converter_*` function
- frame count/buffer sizing helpers:
  `ma_calculate_frame_count_after_resampling`,
  `ma_calculate_buffer_size_in_frames_from_descriptor`,
  `ma_calculate_buffer_size_in_frames_from_milliseconds`,
  `ma_calculate_buffer_size_in_milliseconds_from_frames`

### Missing DSP, Filtering, Panning, Fading, And Spatialization API

Missing non-node DSP families:

- biquad: every `ma_biquad_*` function
- low-pass filters: every `ma_lpf1_*`, `ma_lpf2_*`, and `ma_lpf_*` function
- high-pass filters: every `ma_hpf1_*`, `ma_hpf2_*`, and `ma_hpf_*` function
- band-pass filters: every `ma_bpf2_*` and `ma_bpf_*` function
- notch, peak, low-shelf, and high-shelf filters: every `ma_notch2_*`,
  `ma_peak2_*`, `ma_loshelf2_*`, and `ma_hishelf2_*` function
- delay, gainer, panner, and fader: every `ma_delay_*`, `ma_gainer_*`,
  `ma_panner_*`, and `ma_fader_*` function
- spatialization: every `ma_spatializer_listener_*` and
  `ma_spatializer_*` function

Missing node graph DSP wrappers include every `ma_*_node_*` function for
biquad, LPF, HPF, BPF, notch, peak, low shelf, high shelf, delay, data source,
engine node, and splitter nodes.

### Missing Decoding And Embedded Decoder API

Missing high-level decode API:

- `ma_decoder_config_init`
- `ma_decoder_config_init_default`
- `ma_decoder_config_init_copy`
- `ma_decoder_init`
- `ma_decoder_init_file`
- `ma_decoder_init_file_w`
- `ma_decoder_init_memory`
- `ma_decoder_init_vfs`
- `ma_decoder_init_vfs_w`
- `ma_decoder_uninit`
- `ma_decoder_read_pcm_frames`
- `ma_decoder_seek_to_pcm_frame`
- `ma_decoder_get_data_format`
- `ma_decoder_get_cursor_in_pcm_frames`
- `ma_decoder_get_length_in_pcm_frames`
- `ma_decoder_get_available_frames`
- `ma_decoding_backend_config_init`
- `ma_decode_file`
- `ma_decode_memory`
- `ma_decode_from_vfs`

Missing built-in decoder wrappers:

- every `ma_wav_*` function
- every `ma_mp3_*` function
- every `ma_flac_*` function
- every `ma_stbvorbis_*` function

Missing embedded decoder APIs:

- every public `ma_dr_wav_*` function from the embedded dr_wav block
- every public `ma_dr_flac_*` function from the embedded dr_flac block
- every public `ma_dr_mp3_*` and `ma_dr_mp3dec_*` function from the embedded dr_mp3 block

The current Stark `Decoder` wrapper covers only a narrow `f32`/`s16` path. It
does not expose `u8`, `s24`, `s32`, native format passthrough, custom decoding
backends, VFS decoding, wide path APIs, cursor queries, available-frame queries,
format-specific metadata, WAV write/read metadata, FLAC metadata iteration, MP3
seek tables, or STB-owned one-shot decode buffers.

### Missing Encoding API

This family is completely disabled by `MA_NO_ENCODING` in the current native
build. Missing functions:

- `ma_encoder_config_init`
- `ma_encoder_preinit`
- `ma_encoder_init`
- `ma_encoder_init_file`
- `ma_encoder_init_file_w`
- `ma_encoder_init_vfs`
- `ma_encoder_init_vfs_w`
- `ma_encoder_write_pcm_frames`
- `ma_encoder_uninit`
- `ma_encoder_init__internal`

The only upstream encoder target in `0.11.25` is WAV, but the embedded dr_wav
write API also exposes lower-level WAV writing functions and memory writers.

### Missing Waveform And Noise Generation API

This family is completely disabled by `MA_NO_GENERATION` in the current native
build. Missing functions:

- `ma_waveform_config_init`
- `ma_waveform_init`
- `ma_waveform_uninit`
- `ma_waveform_read_pcm_frames`
- `ma_waveform_seek_to_pcm_frame`
- `ma_waveform_set_type`
- `ma_waveform_set_sample_rate`
- `ma_waveform_set_amplitude`
- `ma_waveform_set_frequency`
- `ma_pulsewave_config_init`
- `ma_pulsewave_init`
- `ma_pulsewave_uninit`
- `ma_pulsewave_read_pcm_frames`
- `ma_pulsewave_seek_to_pcm_frame`
- `ma_pulsewave_set_sample_rate`
- `ma_pulsewave_set_amplitude`
- `ma_pulsewave_set_frequency`
- `ma_pulsewave_set_duty_cycle`
- `ma_noise_config_init`
- `ma_noise_get_heap_size`
- `ma_noise_init`
- `ma_noise_init_preallocated`
- `ma_noise_uninit`
- `ma_noise_read_pcm_frames`
- `ma_noise_set_type`
- `ma_noise_set_seed`
- `ma_noise_set_amplitude`
- `ma_debug_fill_pcm_frames_with_sine_wave`

### Missing Resource Manager API

This family is completely disabled by `MA_NO_RESOURCE_MANAGER` in the current
native build. Missing functions:

- every `ma_resource_manager_*` function
- every `ma_resource_manager_data_buffer_*` function
- every `ma_resource_manager_data_stream_*` function
- every `ma_resource_manager_data_source_*` function

The missing surface includes configuration, init/uninit, file/data registration,
decoded/encoded data registration, job posting/processing, async pipeline
notifications, data buffers, data streams, data sources, copy/init variants,
map/unmap, read/seek, loop controls, and result polling.

### Missing Node Graph API

This family is completely disabled by `MA_NO_NODE_GRAPH` in the current native
build. Missing functions:

- `ma_node_config_init`
- `ma_node_get_heap_size`
- `ma_node_init_preallocated`
- `ma_node_init`
- `ma_node_uninit`
- `ma_node_get_node_graph`
- `ma_node_get_input_bus_count`
- `ma_node_get_output_bus_count`
- `ma_node_get_input_channels`
- `ma_node_get_output_channels`
- `ma_node_attach_output_bus`
- `ma_node_detach_output_bus`
- `ma_node_detach_all_output_buses`
- `ma_node_set_output_bus_volume`
- `ma_node_get_output_bus_volume`
- `ma_node_set_state`
- `ma_node_get_state`
- `ma_node_set_state_time`
- `ma_node_get_state_time`
- `ma_node_get_state_by_time`
- `ma_node_get_state_by_time_range`
- `ma_node_set_time`
- `ma_node_get_time`
- `ma_node_graph_config_init`
- `ma_node_graph_init`
- `ma_node_graph_uninit`
- `ma_node_graph_get_endpoint`
- `ma_node_graph_read_pcm_frames`
- `ma_node_graph_get_time`
- `ma_node_graph_set_time`
- `ma_node_graph_get_channels`
- `ma_node_graph_get_processing_size_in_frames`
- every node-specific `ma_*_node_*` function listed under DSP

### Missing Engine, Sound, And Sound Group API

This family is disabled by `MA_NO_ENGINE` and indirectly by `MA_NO_NODE_GRAPH`
in the current native build.

Missing engine functions:

- every `ma_engine_*` function, including configuration, init/uninit,
  start/stop, device/resource-manager/node-graph accessors, time controls,
  volume/gain controls, listener controls, closest-listener query,
  `ma_engine_read_pcm_frames`, `ma_engine_play_sound`, and
  `ma_engine_play_sound_ex`

Missing sound and sound group functions:

- every `ma_sound_*` function
- every `ma_sound_group_*` function

The missing surface includes file/data-source/copy sound initialization,
async/stream/decode flags, playback state, looping, seeking, cursor/length
queries, end callbacks, volume/pan/pitch/fade scheduling, start/stop scheduling,
spatialization enablement, pinned listeners, attenuation, cones, position,
direction, velocity, rolloff, min/max gain, min/max distance, doppler, and sound
group equivalents.

### Complete Miniaudio Public Function Inventory

This inventory is generated from `MA_API` declarations in `vendor/native/miniaudio/miniaudio.h` for miniaudio `0.11.25`. It intentionally records every public function symbol so missing APIs are searchable by exact upstream name. The previous broad sections group the work by subsystem; this section is the exact symbol checklist.

Status values:

- `covered by current safe wrapper; raw API still not exposed` means Stark exposes equivalent behavior through `Vendor.Miniaudio` today, but not the raw miniaudio function.
- `missing` means there is no Stark public surface for that upstream function.

- `ma_aligned_free` - missing
- `ma_aligned_malloc` - missing
- `ma_apply_volume_factor_f32` - missing
- `ma_apply_volume_factor_pcm_frames_f32` - missing
- `ma_apply_volume_factor_pcm_frames_s16` - missing
- `ma_apply_volume_factor_pcm_frames_s24` - missing
- `ma_apply_volume_factor_pcm_frames_s32` - missing
- `ma_apply_volume_factor_pcm_frames_u8` - missing
- `ma_apply_volume_factor_pcm_frames` - missing
- `ma_apply_volume_factor_s16` - missing
- `ma_apply_volume_factor_s24` - missing
- `ma_apply_volume_factor_s32` - missing
- `ma_apply_volume_factor_u8` - missing
- `ma_async_notification_event_init` - missing
- `ma_async_notification_event_signal` - missing
- `ma_async_notification_event_uninit` - missing
- `ma_async_notification_event_wait` - missing
- `ma_async_notification_poll_init` - missing
- `ma_async_notification_poll_is_signalled` - missing
- `ma_async_notification_signal` - missing
- `ma_atomic_vec3f_get` - missing
- `ma_atomic_vec3f_init` - missing
- `ma_atomic_vec3f_set` - missing
- `ma_audio_buffer_alloc_and_init` - missing
- `ma_audio_buffer_at_end` - missing
- `ma_audio_buffer_config_init` - missing
- `ma_audio_buffer_get_available_frames` - missing
- `ma_audio_buffer_get_cursor_in_pcm_frames` - missing
- `ma_audio_buffer_get_length_in_pcm_frames` - missing
- `ma_audio_buffer_init_copy` - missing
- `ma_audio_buffer_init` - missing
- `ma_audio_buffer_map` - missing
- `ma_audio_buffer_read_pcm_frames` - missing
- `ma_audio_buffer_ref_at_end` - missing
- `ma_audio_buffer_ref_get_available_frames` - missing
- `ma_audio_buffer_ref_get_cursor_in_pcm_frames` - missing
- `ma_audio_buffer_ref_get_length_in_pcm_frames` - missing
- `ma_audio_buffer_ref_init` - missing
- `ma_audio_buffer_ref_map` - missing
- `ma_audio_buffer_ref_read_pcm_frames` - missing
- `ma_audio_buffer_ref_seek_to_pcm_frame` - missing
- `ma_audio_buffer_ref_set_data` - missing
- `ma_audio_buffer_ref_uninit` - missing
- `ma_audio_buffer_ref_unmap` - missing
- `ma_audio_buffer_seek_to_pcm_frame` - missing
- `ma_audio_buffer_uninit_and_free` - missing
- `ma_audio_buffer_uninit` - missing
- `ma_audio_buffer_unmap` - missing
- `ma_biquad_clear_cache` - missing
- `ma_biquad_config_init` - missing
- `ma_biquad_get_heap_size` - missing
- `ma_biquad_get_latency` - missing
- `ma_biquad_init_preallocated` - missing
- `ma_biquad_init` - missing
- `ma_biquad_node_config_init` - missing
- `ma_biquad_node_init` - missing
- `ma_biquad_node_reinit` - missing
- `ma_biquad_node_uninit` - missing
- `ma_biquad_process_pcm_frames` - missing
- `ma_biquad_reinit` - missing
- `ma_biquad_uninit` - missing
- `ma_blend_f32` - missing
- `ma_bpf2_config_init` - missing
- `ma_bpf2_get_heap_size` - missing
- `ma_bpf2_get_latency` - missing
- `ma_bpf2_init_preallocated` - missing
- `ma_bpf2_init` - missing
- `ma_bpf2_process_pcm_frames` - missing
- `ma_bpf2_reinit` - missing
- `ma_bpf2_uninit` - missing
- `ma_bpf_config_init` - missing
- `ma_bpf_get_heap_size` - missing
- `ma_bpf_get_latency` - missing
- `ma_bpf_init_preallocated` - missing
- `ma_bpf_init` - missing
- `ma_bpf_node_config_init` - missing
- `ma_bpf_node_init` - missing
- `ma_bpf_node_reinit` - missing
- `ma_bpf_node_uninit` - missing
- `ma_bpf_process_pcm_frames` - missing
- `ma_bpf_reinit` - missing
- `ma_bpf_uninit` - missing
- `ma_calculate_buffer_size_in_frames_from_descriptor` - missing
- `ma_calculate_buffer_size_in_frames_from_milliseconds` - missing
- `ma_calculate_buffer_size_in_milliseconds_from_frames` - missing
- `ma_calculate_frame_count_after_resampling` - missing
- `ma_calloc` - missing
- `ma_channel_converter_config_init` - missing
- `ma_channel_converter_get_heap_size` - missing
- `ma_channel_converter_get_input_channel_map` - missing
- `ma_channel_converter_get_output_channel_map` - missing
- `ma_channel_converter_init_preallocated` - missing
- `ma_channel_converter_init` - missing
- `ma_channel_converter_process_pcm_frames` - missing
- `ma_channel_converter_uninit` - missing
- `ma_channel_map_contains_channel_position` - missing
- `ma_channel_map_copy_or_default` - missing
- `ma_channel_map_copy` - missing
- `ma_channel_map_find_channel_position` - missing
- `ma_channel_map_get_channel` - missing
- `ma_channel_map_init_blank` - missing
- `ma_channel_map_init_standard` - missing
- `ma_channel_map_is_blank` - missing
- `ma_channel_map_is_equal` - missing
- `ma_channel_map_is_valid` - missing
- `ma_channel_map_to_string` - missing
- `ma_channel_position_to_string` - missing
- `ma_clip_pcm_frames` - missing
- `ma_clip_samples_f32` - missing
- `ma_clip_samples_s16` - missing
- `ma_clip_samples_s24` - missing
- `ma_clip_samples_s32` - missing
- `ma_clip_samples_u8` - missing
- `ma_context_config_init` - missing
- `ma_context_enumerate_devices` - missing
- `ma_context_get_device_info` - missing
- `ma_context_get_devices` - missing
- `ma_context_get_log` - missing
- `ma_context_init` - missing
- `ma_context_is_loopback_supported` - missing
- `ma_context_sizeof` - missing
- `ma_context_uninit` - missing
- `ma_convert_frames_ex` - missing
- `ma_convert_frames` - missing
- `ma_convert_pcm_frames_format` - missing
- `ma_copy_and_apply_volume_and_clip_pcm_frames` - missing
- `ma_copy_and_apply_volume_and_clip_samples_f32` - missing
- `ma_copy_and_apply_volume_and_clip_samples_s16` - missing
- `ma_copy_and_apply_volume_and_clip_samples_s24` - missing
- `ma_copy_and_apply_volume_and_clip_samples_s32` - missing
- `ma_copy_and_apply_volume_and_clip_samples_u8` - missing
- `ma_copy_and_apply_volume_factor_f32` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames_f32` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames_s16` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames_s24` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames_s32` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames_u8` - missing
- `ma_copy_and_apply_volume_factor_pcm_frames` - missing
- `ma_copy_and_apply_volume_factor_per_channel_f32` - missing
- `ma_copy_and_apply_volume_factor_s16` - missing
- `ma_copy_and_apply_volume_factor_s24` - missing
- `ma_copy_and_apply_volume_factor_s32` - missing
- `ma_copy_and_apply_volume_factor_u8` - missing
- `ma_copy_pcm_frames` - missing
- `ma_copy_string_w` - missing
- `ma_copy_string` - missing
- `ma_data_converter_config_init_default` - missing
- `ma_data_converter_config_init` - missing
- `ma_data_converter_get_expected_output_frame_count` - missing
- `ma_data_converter_get_heap_size` - missing
- `ma_data_converter_get_input_channel_map` - missing
- `ma_data_converter_get_input_latency` - missing
- `ma_data_converter_get_output_channel_map` - missing
- `ma_data_converter_get_output_latency` - missing
- `ma_data_converter_get_required_input_frame_count` - missing
- `ma_data_converter_init_preallocated` - missing
- `ma_data_converter_init` - missing
- `ma_data_converter_process_pcm_frames` - missing
- `ma_data_converter_reset` - missing
- `ma_data_converter_set_rate_ratio` - missing
- `ma_data_converter_set_rate` - missing
- `ma_data_converter_uninit` - missing
- `ma_data_source_config_init` - missing
- `ma_data_source_get_current` - missing
- `ma_data_source_get_cursor_in_pcm_frames` - missing
- `ma_data_source_get_cursor_in_seconds` - missing
- `ma_data_source_get_data_format` - missing
- `ma_data_source_get_length_in_pcm_frames` - missing
- `ma_data_source_get_length_in_seconds` - missing
- `ma_data_source_get_loop_point_in_pcm_frames` - missing
- `ma_data_source_get_next_callback` - missing
- `ma_data_source_get_next` - missing
- `ma_data_source_get_range_in_pcm_frames` - missing
- `ma_data_source_init` - missing
- `ma_data_source_is_looping` - missing
- `ma_data_source_node_config_init` - missing
- `ma_data_source_node_init` - missing
- `ma_data_source_node_is_looping` - missing
- `ma_data_source_node_set_looping` - missing
- `ma_data_source_node_uninit` - missing
- `ma_data_source_read_pcm_frames` - missing
- `ma_data_source_seek_pcm_frames` - missing
- `ma_data_source_seek_seconds` - missing
- `ma_data_source_seek_to_pcm_frame` - missing
- `ma_data_source_seek_to_second` - missing
- `ma_data_source_set_current` - missing
- `ma_data_source_set_loop_point_in_pcm_frames` - missing
- `ma_data_source_set_looping` - missing
- `ma_data_source_set_next_callback` - missing
- `ma_data_source_set_next` - missing
- `ma_data_source_set_range_in_pcm_frames` - missing
- `ma_data_source_uninit` - missing
- `ma_debug_fill_pcm_frames_with_sine_wave` - missing
- `ma_decode_file` - missing
- `ma_decode_from_vfs` - missing
- `ma_decode_memory` - missing
- `ma_decoder_config_init_copy` - missing
- `ma_decoder_config_init_default` - missing
- `ma_decoder_config_init` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_get_available_frames` - missing
- `ma_decoder_get_cursor_in_pcm_frames` - missing
- `ma_decoder_get_data_format` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_get_length_in_pcm_frames` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_init_file_w` - missing
- `ma_decoder_init_file` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_init_memory` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_init_vfs_w` - missing
- `ma_decoder_init_vfs` - missing
- `ma_decoder_init` - missing
- `ma_decoder_read_pcm_frames` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_seek_to_pcm_frame` - covered by current safe wrapper; raw API still not exposed
- `ma_decoder_uninit` - covered by current safe wrapper; raw API still not exposed
- `ma_decoding_backend_config_init` - missing
- `ma_default_vfs_init` - missing
- `ma_deinterleave_pcm_frames` - missing
- `ma_delay_config_init` - missing
- `ma_delay_get_decay` - missing
- `ma_delay_get_dry` - missing
- `ma_delay_get_wet` - missing
- `ma_delay_init` - missing
- `ma_delay_node_config_init` - missing
- `ma_delay_node_get_decay` - missing
- `ma_delay_node_get_dry` - missing
- `ma_delay_node_get_wet` - missing
- `ma_delay_node_init` - missing
- `ma_delay_node_set_decay` - missing
- `ma_delay_node_set_dry` - missing
- `ma_delay_node_set_wet` - missing
- `ma_delay_node_uninit` - missing
- `ma_delay_process_pcm_frames` - missing
- `ma_delay_set_decay` - missing
- `ma_delay_set_dry` - missing
- `ma_delay_set_wet` - missing
- `ma_delay_uninit` - missing
- `ma_device_config_init` - covered by current safe wrapper; raw API still not exposed
- `ma_device_get_context` - missing
- `ma_device_get_info` - missing
- `ma_device_get_log` - missing
- `ma_device_get_master_volume_db` - missing
- `ma_device_get_master_volume` - missing
- `ma_device_get_name` - missing
- `ma_device_get_state` - missing
- `ma_device_handle_backend_data_callback` - missing
- `ma_device_id_equal` - missing
- `ma_device_info_add_native_data_format` - missing
- `ma_device_init_ex` - missing
- `ma_device_init` - covered by current safe wrapper; raw API still not exposed
- `ma_device_is_started` - missing
- `ma_device_job_thread_config_init` - missing
- `ma_device_job_thread_init` - missing
- `ma_device_job_thread_next` - missing
- `ma_device_job_thread_post` - missing
- `ma_device_job_thread_uninit` - missing
- `ma_device_post_init` - missing
- `ma_device_set_master_volume_db` - missing
- `ma_device_set_master_volume` - missing
- `ma_device_start` - covered by current safe wrapper; raw API still not exposed
- `ma_device_stop` - covered by current safe wrapper; raw API still not exposed
- `ma_device_uninit` - covered by current safe wrapper; raw API still not exposed
- `ma_dlclose` - missing
- `ma_dlopen` - missing
- `ma_dlsym` - missing
- `ma_dr_flac_close` - missing
- `ma_dr_flac_free` - missing
- `ma_dr_flac_init_cuesheet_track_iterator` - missing
- `ma_dr_flac_init_vorbis_comment_iterator` - missing
- `ma_dr_flac_next_cuesheet_track` - missing
- `ma_dr_flac_next_vorbis_comment` - missing
- `ma_dr_flac_open_and_read_pcm_frames_f32` - missing
- `ma_dr_flac_open_and_read_pcm_frames_s16` - missing
- `ma_dr_flac_open_and_read_pcm_frames_s32` - missing
- `ma_dr_flac_open_file_and_read_pcm_frames_f32` - missing
- `ma_dr_flac_open_file_and_read_pcm_frames_s16` - missing
- `ma_dr_flac_open_file_and_read_pcm_frames_s32` - missing
- `ma_dr_flac_open_file_w` - missing
- `ma_dr_flac_open_file_with_metadata_w` - missing
- `ma_dr_flac_open_file_with_metadata` - missing
- `ma_dr_flac_open_file` - missing
- `ma_dr_flac_open_memory_and_read_pcm_frames_f32` - missing
- `ma_dr_flac_open_memory_and_read_pcm_frames_s16` - missing
- `ma_dr_flac_open_memory_and_read_pcm_frames_s32` - missing
- `ma_dr_flac_open_memory_with_metadata` - missing
- `ma_dr_flac_open_memory` - missing
- `ma_dr_flac_open_relaxed` - missing
- `ma_dr_flac_open_with_metadata_relaxed` - missing
- `ma_dr_flac_open_with_metadata` - missing
- `ma_dr_flac_open` - missing
- `ma_dr_flac_read_pcm_frames_f32` - missing
- `ma_dr_flac_read_pcm_frames_s16` - missing
- `ma_dr_flac_read_pcm_frames_s32` - missing
- `ma_dr_flac_seek_to_pcm_frame` - missing
- `ma_dr_flac_version_string` - missing
- `ma_dr_flac_version` - missing
- `ma_dr_mp3_bind_seek_table` - missing
- `ma_dr_mp3_calculate_seek_points` - missing
- `ma_dr_mp3_free` - missing
- `ma_dr_mp3_get_mp3_and_pcm_frame_count` - missing
- `ma_dr_mp3_get_mp3_frame_count` - missing
- `ma_dr_mp3_get_pcm_frame_count` - missing
- `ma_dr_mp3_init_file_w` - missing
- `ma_dr_mp3_init_file_with_metadata_w` - missing
- `ma_dr_mp3_init_file_with_metadata` - missing
- `ma_dr_mp3_init_file` - missing
- `ma_dr_mp3_init_memory_with_metadata` - missing
- `ma_dr_mp3_init_memory` - missing
- `ma_dr_mp3_init` - missing
- `ma_dr_mp3_malloc` - missing
- `ma_dr_mp3_open_and_read_pcm_frames_f32` - missing
- `ma_dr_mp3_open_and_read_pcm_frames_s16` - missing
- `ma_dr_mp3_open_file_and_read_pcm_frames_f32` - missing
- `ma_dr_mp3_open_file_and_read_pcm_frames_s16` - missing
- `ma_dr_mp3_open_memory_and_read_pcm_frames_f32` - missing
- `ma_dr_mp3_open_memory_and_read_pcm_frames_s16` - missing
- `ma_dr_mp3_read_pcm_frames_f32` - missing
- `ma_dr_mp3_read_pcm_frames_s16` - missing
- `ma_dr_mp3_seek_to_pcm_frame` - missing
- `ma_dr_mp3_uninit` - missing
- `ma_dr_mp3_version_string` - missing
- `ma_dr_mp3_version` - missing
- `ma_dr_mp3dec_decode_frame` - missing
- `ma_dr_mp3dec_f32_to_s16` - missing
- `ma_dr_mp3dec_init` - missing
- `ma_dr_wav_alaw_to_f32` - missing
- `ma_dr_wav_alaw_to_s16` - missing
- `ma_dr_wav_alaw_to_s32` - missing
- `ma_dr_wav_bytes_to_f32` - missing
- `ma_dr_wav_bytes_to_s16` - missing
- `ma_dr_wav_bytes_to_s32` - missing
- `ma_dr_wav_bytes_to_s64` - missing
- `ma_dr_wav_bytes_to_u16` - missing
- `ma_dr_wav_bytes_to_u32` - missing
- `ma_dr_wav_bytes_to_u64` - missing
- `ma_dr_wav_f32_to_s16` - missing
- `ma_dr_wav_f32_to_s32` - missing
- `ma_dr_wav_f64_to_f32` - missing
- `ma_dr_wav_f64_to_s16` - missing
- `ma_dr_wav_f64_to_s32` - missing
- `ma_dr_wav_fmt_get_format` - missing
- `ma_dr_wav_fourcc_equal` - missing
- `ma_dr_wav_free` - missing
- `ma_dr_wav_get_cursor_in_pcm_frames` - missing
- `ma_dr_wav_get_length_in_pcm_frames` - missing
- `ma_dr_wav_guid_equal` - missing
- `ma_dr_wav_init_ex` - missing
- `ma_dr_wav_init_file_ex_w` - missing
- `ma_dr_wav_init_file_ex` - missing
- `ma_dr_wav_init_file_w` - missing
- `ma_dr_wav_init_file_with_metadata_w` - missing
- `ma_dr_wav_init_file_with_metadata` - missing
- `ma_dr_wav_init_file_write_sequential_pcm_frames_w` - missing
- `ma_dr_wav_init_file_write_sequential_pcm_frames` - missing
- `ma_dr_wav_init_file_write_sequential_w` - missing
- `ma_dr_wav_init_file_write_sequential` - missing
- `ma_dr_wav_init_file_write_w` - missing
- `ma_dr_wav_init_file_write` - missing
- `ma_dr_wav_init_file` - missing
- `ma_dr_wav_init_memory_ex` - missing
- `ma_dr_wav_init_memory_with_metadata` - missing
- `ma_dr_wav_init_memory_write_sequential_pcm_frames` - missing
- `ma_dr_wav_init_memory_write_sequential` - missing
- `ma_dr_wav_init_memory_write` - missing
- `ma_dr_wav_init_memory` - missing
- `ma_dr_wav_init_with_metadata` - missing
- `ma_dr_wav_init_write_sequential_pcm_frames` - missing
- `ma_dr_wav_init_write_sequential` - missing
- `ma_dr_wav_init_write_with_metadata` - missing
- `ma_dr_wav_init_write` - missing
- `ma_dr_wav_init` - missing
- `ma_dr_wav_mulaw_to_f32` - missing
- `ma_dr_wav_mulaw_to_s16` - missing
- `ma_dr_wav_mulaw_to_s32` - missing
- `ma_dr_wav_open_and_read_pcm_frames_f32` - missing
- `ma_dr_wav_open_and_read_pcm_frames_s16` - missing
- `ma_dr_wav_open_and_read_pcm_frames_s32` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_f32_w` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_f32` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_s16_w` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_s16` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_s32_w` - missing
- `ma_dr_wav_open_file_and_read_pcm_frames_s32` - missing
- `ma_dr_wav_open_memory_and_read_pcm_frames_f32` - missing
- `ma_dr_wav_open_memory_and_read_pcm_frames_s16` - missing
- `ma_dr_wav_open_memory_and_read_pcm_frames_s32` - missing
- `ma_dr_wav_read_pcm_frames_be` - missing
- `ma_dr_wav_read_pcm_frames_f32` - missing
- `ma_dr_wav_read_pcm_frames_f32be` - missing
- `ma_dr_wav_read_pcm_frames_f32le` - missing
- `ma_dr_wav_read_pcm_frames_le` - missing
- `ma_dr_wav_read_pcm_frames_s16` - missing
- `ma_dr_wav_read_pcm_frames_s16be` - missing
- `ma_dr_wav_read_pcm_frames_s16le` - missing
- `ma_dr_wav_read_pcm_frames_s32` - missing
- `ma_dr_wav_read_pcm_frames_s32be` - missing
- `ma_dr_wav_read_pcm_frames_s32le` - missing
- `ma_dr_wav_read_pcm_frames` - missing
- `ma_dr_wav_read_raw` - missing
- `ma_dr_wav_s16_to_f32` - missing
- `ma_dr_wav_s16_to_s32` - missing
- `ma_dr_wav_s24_to_f32` - missing
- `ma_dr_wav_s24_to_s16` - missing
- `ma_dr_wav_s24_to_s32` - missing
- `ma_dr_wav_s32_to_f32` - missing
- `ma_dr_wav_s32_to_s16` - missing
- `ma_dr_wav_seek_to_pcm_frame` - missing
- `ma_dr_wav_take_ownership_of_metadata` - missing
- `ma_dr_wav_target_write_size_bytes` - missing
- `ma_dr_wav_u8_to_f32` - missing
- `ma_dr_wav_u8_to_s16` - missing
- `ma_dr_wav_u8_to_s32` - missing
- `ma_dr_wav_uninit` - missing
- `ma_dr_wav_version_string` - missing
- `ma_dr_wav_version` - missing
- `ma_dr_wav_write_pcm_frames_be` - missing
- `ma_dr_wav_write_pcm_frames_le` - missing
- `ma_dr_wav_write_pcm_frames` - missing
- `ma_dr_wav_write_raw` - missing
- `ma_duplex_rb_init` - missing
- `ma_duplex_rb_uninit` - missing
- `ma_encoder_config_init` - missing
- `ma_encoder_init__internal` - missing
- `ma_encoder_init_file_w` - missing
- `ma_encoder_init_file` - missing
- `ma_encoder_init_vfs_w` - missing
- `ma_encoder_init_vfs` - missing
- `ma_encoder_init` - missing
- `ma_encoder_preinit` - missing
- `ma_encoder_uninit` - missing
- `ma_encoder_write_pcm_frames` - missing
- `ma_engine_config_init` - missing
- `ma_engine_find_closest_listener` - missing
- `ma_engine_get_channels` - missing
- `ma_engine_get_device` - missing
- `ma_engine_get_endpoint` - missing
- `ma_engine_get_gain_db` - missing
- `ma_engine_get_listener_count` - missing
- `ma_engine_get_log` - missing
- `ma_engine_get_node_graph` - missing
- `ma_engine_get_resource_manager` - missing
- `ma_engine_get_sample_rate` - missing
- `ma_engine_get_time_in_milliseconds` - missing
- `ma_engine_get_time_in_pcm_frames` - missing
- `ma_engine_get_time` - missing
- `ma_engine_get_volume` - missing
- `ma_engine_init` - missing
- `ma_engine_listener_get_cone` - missing
- `ma_engine_listener_get_direction` - missing
- `ma_engine_listener_get_position` - missing
- `ma_engine_listener_get_velocity` - missing
- `ma_engine_listener_get_world_up` - missing
- `ma_engine_listener_is_enabled` - missing
- `ma_engine_listener_set_cone` - missing
- `ma_engine_listener_set_direction` - missing
- `ma_engine_listener_set_enabled` - missing
- `ma_engine_listener_set_position` - missing
- `ma_engine_listener_set_velocity` - missing
- `ma_engine_listener_set_world_up` - missing
- `ma_engine_node_config_init` - missing
- `ma_engine_node_get_heap_size` - missing
- `ma_engine_node_init_preallocated` - missing
- `ma_engine_node_init` - missing
- `ma_engine_node_uninit` - missing
- `ma_engine_play_sound_ex` - missing
- `ma_engine_play_sound` - missing
- `ma_engine_read_pcm_frames` - missing
- `ma_engine_set_gain_db` - missing
- `ma_engine_set_time_in_milliseconds` - missing
- `ma_engine_set_time_in_pcm_frames` - missing
- `ma_engine_set_time` - missing
- `ma_engine_set_volume` - missing
- `ma_engine_start` - missing
- `ma_engine_stop` - missing
- `ma_engine_uninit` - missing
- `ma_event_init` - missing
- `ma_event_signal` - missing
- `ma_event_uninit` - missing
- `ma_event_wait` - missing
- `ma_fader_config_init` - missing
- `ma_fader_get_current_volume` - missing
- `ma_fader_get_data_format` - missing
- `ma_fader_init` - missing
- `ma_fader_process_pcm_frames` - missing
- `ma_fader_set_fade_ex` - missing
- `ma_fader_set_fade` - missing
- `ma_fence_acquire` - missing
- `ma_fence_init` - missing
- `ma_fence_release` - missing
- `ma_fence_uninit` - missing
- `ma_fence_wait` - missing
- `ma_flac_get_cursor_in_pcm_frames` - missing
- `ma_flac_get_data_format` - missing
- `ma_flac_get_length_in_pcm_frames` - missing
- `ma_flac_init_file_w` - missing
- `ma_flac_init_file` - missing
- `ma_flac_init_memory` - missing
- `ma_flac_init` - missing
- `ma_flac_read_pcm_frames` - missing
- `ma_flac_seek_to_pcm_frame` - missing
- `ma_flac_uninit` - missing
- `ma_fopen` - missing
- `ma_free` - missing
- `ma_gainer_config_init` - missing
- `ma_gainer_get_heap_size` - missing
- `ma_gainer_get_master_volume` - missing
- `ma_gainer_init_preallocated` - missing
- `ma_gainer_init` - missing
- `ma_gainer_process_pcm_frames` - missing
- `ma_gainer_set_gain` - missing
- `ma_gainer_set_gains` - missing
- `ma_gainer_set_master_volume` - missing
- `ma_gainer_uninit` - missing
- `ma_get_backend_from_name` - missing
- `ma_get_backend_name` - missing
- `ma_get_bytes_per_sample` - missing
- `ma_get_enabled_backends` - missing
- `ma_get_format_name` - missing
- `ma_get_format_priority_index` - missing
- `ma_hishelf2_config_init` - missing
- `ma_hishelf2_get_heap_size` - missing
- `ma_hishelf2_get_latency` - missing
- `ma_hishelf2_init_preallocated` - missing
- `ma_hishelf2_init` - missing
- `ma_hishelf2_process_pcm_frames` - missing
- `ma_hishelf2_reinit` - missing
- `ma_hishelf2_uninit` - missing
- `ma_hishelf_node_config_init` - missing
- `ma_hishelf_node_init` - missing
- `ma_hishelf_node_reinit` - missing
- `ma_hishelf_node_uninit` - missing
- `ma_hpf1_config_init` - missing
- `ma_hpf1_get_heap_size` - missing
- `ma_hpf1_get_latency` - missing
- `ma_hpf1_init_preallocated` - missing
- `ma_hpf1_init` - missing
- `ma_hpf1_process_pcm_frames` - missing
- `ma_hpf1_reinit` - missing
- `ma_hpf1_uninit` - missing
- `ma_hpf2_config_init` - missing
- `ma_hpf2_get_heap_size` - missing
- `ma_hpf2_get_latency` - missing
- `ma_hpf2_init_preallocated` - missing
- `ma_hpf2_init` - missing
- `ma_hpf2_process_pcm_frames` - missing
- `ma_hpf2_reinit` - missing
- `ma_hpf2_uninit` - missing
- `ma_hpf_config_init` - missing
- `ma_hpf_get_heap_size` - missing
- `ma_hpf_get_latency` - missing
- `ma_hpf_init_preallocated` - missing
- `ma_hpf_init` - missing
- `ma_hpf_node_config_init` - missing
- `ma_hpf_node_init` - missing
- `ma_hpf_node_reinit` - missing
- `ma_hpf_node_uninit` - missing
- `ma_hpf_process_pcm_frames` - missing
- `ma_hpf_reinit` - missing
- `ma_hpf_uninit` - missing
- `ma_interleave_pcm_frames` - missing
- `ma_is_backend_enabled` - missing
- `ma_is_loopback_supported` - missing
- `ma_itoa_s` - missing
- `ma_job_init` - missing
- `ma_job_process` - missing
- `ma_job_queue_config_init` - missing
- `ma_job_queue_get_heap_size` - missing
- `ma_job_queue_init_preallocated` - missing
- `ma_job_queue_init` - missing
- `ma_job_queue_next` - missing
- `ma_job_queue_post` - missing
- `ma_job_queue_uninit` - missing
- `ma_linear_resampler_config_init` - missing
- `ma_linear_resampler_get_expected_output_frame_count` - missing
- `ma_linear_resampler_get_heap_size` - missing
- `ma_linear_resampler_get_input_latency` - missing
- `ma_linear_resampler_get_output_latency` - missing
- `ma_linear_resampler_get_required_input_frame_count` - missing
- `ma_linear_resampler_init_preallocated` - missing
- `ma_linear_resampler_init` - missing
- `ma_linear_resampler_process_pcm_frames` - missing
- `ma_linear_resampler_reset` - missing
- `ma_linear_resampler_set_rate_ratio` - missing
- `ma_linear_resampler_set_rate` - missing
- `ma_linear_resampler_uninit` - missing
- `ma_log_callback_init` - missing
- `ma_log_init` - missing
- `ma_log_level_to_string` - missing
- `ma_log_post` - missing
- `ma_log_postf` - missing
- `ma_log_postv` - missing
- `ma_log_register_callback` - missing
- `ma_log_uninit` - missing
- `ma_log_unregister_callback` - missing
- `ma_loshelf2_config_init` - missing
- `ma_loshelf2_get_heap_size` - missing
- `ma_loshelf2_get_latency` - missing
- `ma_loshelf2_init_preallocated` - missing
- `ma_loshelf2_init` - missing
- `ma_loshelf2_process_pcm_frames` - missing
- `ma_loshelf2_reinit` - missing
- `ma_loshelf2_uninit` - missing
- `ma_loshelf_node_config_init` - missing
- `ma_loshelf_node_init` - missing
- `ma_loshelf_node_reinit` - missing
- `ma_loshelf_node_uninit` - missing
- `ma_lpf1_clear_cache` - missing
- `ma_lpf1_config_init` - missing
- `ma_lpf1_get_heap_size` - missing
- `ma_lpf1_get_latency` - missing
- `ma_lpf1_init_preallocated` - missing
- `ma_lpf1_init` - missing
- `ma_lpf1_process_pcm_frames` - missing
- `ma_lpf1_reinit` - missing
- `ma_lpf1_uninit` - missing
- `ma_lpf2_clear_cache` - missing
- `ma_lpf2_config_init` - missing
- `ma_lpf2_get_heap_size` - missing
- `ma_lpf2_get_latency` - missing
- `ma_lpf2_init_preallocated` - missing
- `ma_lpf2_init` - missing
- `ma_lpf2_process_pcm_frames` - missing
- `ma_lpf2_reinit` - missing
- `ma_lpf2_uninit` - missing
- `ma_lpf_clear_cache` - missing
- `ma_lpf_config_init` - missing
- `ma_lpf_get_heap_size` - missing
- `ma_lpf_get_latency` - missing
- `ma_lpf_init_preallocated` - missing
- `ma_lpf_init` - missing
- `ma_lpf_node_config_init` - missing
- `ma_lpf_node_init` - missing
- `ma_lpf_node_reinit` - missing
- `ma_lpf_node_uninit` - missing
- `ma_lpf_process_pcm_frames` - missing
- `ma_lpf_reinit` - missing
- `ma_lpf_uninit` - missing
- `ma_malloc` - missing
- `ma_mix_pcm_frames_f32` - missing
- `ma_mp3_get_cursor_in_pcm_frames` - missing
- `ma_mp3_get_data_format` - missing
- `ma_mp3_get_length_in_pcm_frames` - missing
- `ma_mp3_init_file_w` - missing
- `ma_mp3_init_file` - missing
- `ma_mp3_init_memory` - missing
- `ma_mp3_init` - missing
- `ma_mp3_read_pcm_frames` - missing
- `ma_mp3_seek_to_pcm_frame` - missing
- `ma_mp3_uninit` - missing
- `ma_mutex_init` - missing
- `ma_mutex_lock` - missing
- `ma_mutex_uninit` - missing
- `ma_mutex_unlock` - missing
- `ma_node_attach_output_bus` - missing
- `ma_node_config_init` - missing
- `ma_node_detach_all_output_buses` - missing
- `ma_node_detach_output_bus` - missing
- `ma_node_get_heap_size` - missing
- `ma_node_get_input_bus_count` - missing
- `ma_node_get_input_channels` - missing
- `ma_node_get_node_graph` - missing
- `ma_node_get_output_bus_count` - missing
- `ma_node_get_output_bus_volume` - missing
- `ma_node_get_output_channels` - missing
- `ma_node_get_state_by_time_range` - missing
- `ma_node_get_state_by_time` - missing
- `ma_node_get_state_time` - missing
- `ma_node_get_state` - missing
- `ma_node_get_time` - missing
- `ma_node_graph_config_init` - missing
- `ma_node_graph_get_channels` - missing
- `ma_node_graph_get_endpoint` - missing
- `ma_node_graph_get_processing_size_in_frames` - missing
- `ma_node_graph_get_time` - missing
- `ma_node_graph_init` - missing
- `ma_node_graph_read_pcm_frames` - missing
- `ma_node_graph_set_time` - missing
- `ma_node_graph_uninit` - missing
- `ma_node_init_preallocated` - missing
- `ma_node_init` - missing
- `ma_node_set_output_bus_volume` - missing
- `ma_node_set_state_time` - missing
- `ma_node_set_state` - missing
- `ma_node_set_time` - missing
- `ma_node_uninit` - missing
- `ma_noise_config_init` - missing
- `ma_noise_get_heap_size` - missing
- `ma_noise_init_preallocated` - missing
- `ma_noise_init` - missing
- `ma_noise_read_pcm_frames` - missing
- `ma_noise_set_amplitude` - missing
- `ma_noise_set_seed` - missing
- `ma_noise_set_type` - missing
- `ma_noise_uninit` - missing
- `ma_notch2_config_init` - missing
- `ma_notch2_get_heap_size` - missing
- `ma_notch2_get_latency` - missing
- `ma_notch2_init_preallocated` - missing
- `ma_notch2_init` - missing
- `ma_notch2_process_pcm_frames` - missing
- `ma_notch2_reinit` - missing
- `ma_notch2_uninit` - missing
- `ma_notch_node_config_init` - missing
- `ma_notch_node_init` - missing
- `ma_notch_node_reinit` - missing
- `ma_notch_node_uninit` - missing
- `ma_offset_pcm_frames_const_ptr` - missing
- `ma_offset_pcm_frames_ptr` - missing
- `ma_paged_audio_buffer_config_init` - missing
- `ma_paged_audio_buffer_data_allocate_and_append_page` - missing
- `ma_paged_audio_buffer_data_allocate_page` - missing
- `ma_paged_audio_buffer_data_append_page` - missing
- `ma_paged_audio_buffer_data_free_page` - missing
- `ma_paged_audio_buffer_data_get_head` - missing
- `ma_paged_audio_buffer_data_get_length_in_pcm_frames` - missing
- `ma_paged_audio_buffer_data_get_tail` - missing
- `ma_paged_audio_buffer_data_init` - missing
- `ma_paged_audio_buffer_data_uninit` - missing
- `ma_paged_audio_buffer_get_cursor_in_pcm_frames` - missing
- `ma_paged_audio_buffer_get_length_in_pcm_frames` - missing
- `ma_paged_audio_buffer_init` - missing
- `ma_paged_audio_buffer_read_pcm_frames` - missing
- `ma_paged_audio_buffer_seek_to_pcm_frame` - missing
- `ma_paged_audio_buffer_uninit` - missing
- `ma_panner_config_init` - missing
- `ma_panner_get_mode` - missing
- `ma_panner_get_pan` - missing
- `ma_panner_init` - missing
- `ma_panner_process_pcm_frames` - missing
- `ma_panner_set_mode` - missing
- `ma_panner_set_pan` - missing
- `ma_pcm_convert` - missing
- `ma_pcm_deinterleave_f32` - missing
- `ma_pcm_deinterleave_s16` - missing
- `ma_pcm_deinterleave_s24` - missing
- `ma_pcm_deinterleave_s32` - missing
- `ma_pcm_deinterleave_u8` - missing
- `ma_pcm_f32_to_f32` - missing
- `ma_pcm_f32_to_s16` - missing
- `ma_pcm_f32_to_s24` - missing
- `ma_pcm_f32_to_s32` - missing
- `ma_pcm_f32_to_u8` - missing
- `ma_pcm_interleave_f32` - missing
- `ma_pcm_interleave_s16` - missing
- `ma_pcm_interleave_s24` - missing
- `ma_pcm_interleave_s32` - missing
- `ma_pcm_interleave_u8` - missing
- `ma_pcm_rb_acquire_read` - missing
- `ma_pcm_rb_acquire_write` - missing
- `ma_pcm_rb_available_read` - missing
- `ma_pcm_rb_available_write` - missing
- `ma_pcm_rb_commit_read` - missing
- `ma_pcm_rb_commit_write` - missing
- `ma_pcm_rb_get_channels` - missing
- `ma_pcm_rb_get_format` - missing
- `ma_pcm_rb_get_sample_rate` - missing
- `ma_pcm_rb_get_subbuffer_offset` - missing
- `ma_pcm_rb_get_subbuffer_ptr` - missing
- `ma_pcm_rb_get_subbuffer_size` - missing
- `ma_pcm_rb_get_subbuffer_stride` - missing
- `ma_pcm_rb_init_ex` - missing
- `ma_pcm_rb_init` - missing
- `ma_pcm_rb_pointer_distance` - missing
- `ma_pcm_rb_reset` - missing
- `ma_pcm_rb_seek_read` - missing
- `ma_pcm_rb_seek_write` - missing
- `ma_pcm_rb_set_sample_rate` - missing
- `ma_pcm_rb_uninit` - missing
- `ma_pcm_s16_to_f32` - missing
- `ma_pcm_s16_to_s16` - missing
- `ma_pcm_s16_to_s24` - missing
- `ma_pcm_s16_to_s32` - missing
- `ma_pcm_s16_to_u8` - missing
- `ma_pcm_s24_to_f32` - missing
- `ma_pcm_s24_to_s16` - missing
- `ma_pcm_s24_to_s24` - missing
- `ma_pcm_s24_to_s32` - missing
- `ma_pcm_s24_to_u8` - missing
- `ma_pcm_s32_to_f32` - missing
- `ma_pcm_s32_to_s16` - missing
- `ma_pcm_s32_to_s24` - missing
- `ma_pcm_s32_to_s32` - missing
- `ma_pcm_s32_to_u8` - missing
- `ma_pcm_u8_to_f32` - missing
- `ma_pcm_u8_to_s16` - missing
- `ma_pcm_u8_to_s24` - missing
- `ma_pcm_u8_to_s32` - missing
- `ma_pcm_u8_to_u8` - missing
- `ma_peak2_config_init` - missing
- `ma_peak2_get_heap_size` - missing
- `ma_peak2_get_latency` - missing
- `ma_peak2_init_preallocated` - missing
- `ma_peak2_init` - missing
- `ma_peak2_process_pcm_frames` - missing
- `ma_peak2_reinit` - missing
- `ma_peak2_uninit` - missing
- `ma_peak_node_config_init` - missing
- `ma_peak_node_init` - missing
- `ma_peak_node_reinit` - missing
- `ma_peak_node_uninit` - missing
- `ma_pulsewave_config_init` - missing
- `ma_pulsewave_init` - missing
- `ma_pulsewave_read_pcm_frames` - missing
- `ma_pulsewave_seek_to_pcm_frame` - missing
- `ma_pulsewave_set_amplitude` - missing
- `ma_pulsewave_set_duty_cycle` - missing
- `ma_pulsewave_set_frequency` - missing
- `ma_pulsewave_set_sample_rate` - missing
- `ma_pulsewave_uninit` - missing
- `ma_rb_acquire_read` - missing
- `ma_rb_acquire_write` - missing
- `ma_rb_available_read` - missing
- `ma_rb_available_write` - missing
- `ma_rb_commit_read` - missing
- `ma_rb_commit_write` - missing
- `ma_rb_get_subbuffer_offset` - missing
- `ma_rb_get_subbuffer_ptr` - missing
- `ma_rb_get_subbuffer_size` - missing
- `ma_rb_get_subbuffer_stride` - missing
- `ma_rb_init_ex` - missing
- `ma_rb_init` - missing
- `ma_rb_pointer_distance` - missing
- `ma_rb_reset` - missing
- `ma_rb_seek_read` - missing
- `ma_rb_seek_write` - missing
- `ma_rb_uninit` - missing
- `ma_realloc` - missing
- `ma_resampler_config_init` - missing
- `ma_resampler_get_expected_output_frame_count` - missing
- `ma_resampler_get_heap_size` - missing
- `ma_resampler_get_input_latency` - missing
- `ma_resampler_get_output_latency` - missing
- `ma_resampler_get_required_input_frame_count` - missing
- `ma_resampler_init_preallocated` - missing
- `ma_resampler_init` - missing
- `ma_resampler_process_pcm_frames` - missing
- `ma_resampler_reset` - missing
- `ma_resampler_set_rate_ratio` - missing
- `ma_resampler_set_rate` - missing
- `ma_resampler_uninit` - missing
- `ma_resource_manager_config_init` - missing
- `ma_resource_manager_data_buffer_get_available_frames` - missing
- `ma_resource_manager_data_buffer_get_cursor_in_pcm_frames` - missing
- `ma_resource_manager_data_buffer_get_data_format` - missing
- `ma_resource_manager_data_buffer_get_length_in_pcm_frames` - missing
- `ma_resource_manager_data_buffer_init_copy` - missing
- `ma_resource_manager_data_buffer_init_ex` - missing
- `ma_resource_manager_data_buffer_init_w` - missing
- `ma_resource_manager_data_buffer_init` - missing
- `ma_resource_manager_data_buffer_is_looping` - missing
- `ma_resource_manager_data_buffer_read_pcm_frames` - missing
- `ma_resource_manager_data_buffer_result` - missing
- `ma_resource_manager_data_buffer_seek_to_pcm_frame` - missing
- `ma_resource_manager_data_buffer_set_looping` - missing
- `ma_resource_manager_data_buffer_uninit` - missing
- `ma_resource_manager_data_source_config_init` - missing
- `ma_resource_manager_data_source_get_available_frames` - missing
- `ma_resource_manager_data_source_get_cursor_in_pcm_frames` - missing
- `ma_resource_manager_data_source_get_data_format` - missing
- `ma_resource_manager_data_source_get_length_in_pcm_frames` - missing
- `ma_resource_manager_data_source_init_copy` - missing
- `ma_resource_manager_data_source_init_ex` - missing
- `ma_resource_manager_data_source_init_w` - missing
- `ma_resource_manager_data_source_init` - missing
- `ma_resource_manager_data_source_is_looping` - missing
- `ma_resource_manager_data_source_map` - missing
- `ma_resource_manager_data_source_read_pcm_frames` - missing
- `ma_resource_manager_data_source_result` - missing
- `ma_resource_manager_data_source_seek_to_pcm_frame` - missing
- `ma_resource_manager_data_source_set_looping` - missing
- `ma_resource_manager_data_source_uninit` - missing
- `ma_resource_manager_data_source_unmap` - missing
- `ma_resource_manager_data_stream_get_available_frames` - missing
- `ma_resource_manager_data_stream_get_cursor_in_pcm_frames` - missing
- `ma_resource_manager_data_stream_get_data_format` - missing
- `ma_resource_manager_data_stream_get_length_in_pcm_frames` - missing
- `ma_resource_manager_data_stream_init_ex` - missing
- `ma_resource_manager_data_stream_init_w` - missing
- `ma_resource_manager_data_stream_init` - missing
- `ma_resource_manager_data_stream_is_looping` - missing
- `ma_resource_manager_data_stream_read_pcm_frames` - missing
- `ma_resource_manager_data_stream_result` - missing
- `ma_resource_manager_data_stream_seek_to_pcm_frame` - missing
- `ma_resource_manager_data_stream_set_looping` - missing
- `ma_resource_manager_data_stream_uninit` - missing
- `ma_resource_manager_get_log` - missing
- `ma_resource_manager_init` - missing
- `ma_resource_manager_next_job` - missing
- `ma_resource_manager_pipeline_notifications_init` - missing
- `ma_resource_manager_post_job_quit` - missing
- `ma_resource_manager_post_job` - missing
- `ma_resource_manager_process_job` - missing
- `ma_resource_manager_process_next_job` - missing
- `ma_resource_manager_register_decoded_data_w` - missing
- `ma_resource_manager_register_decoded_data` - missing
- `ma_resource_manager_register_encoded_data_w` - missing
- `ma_resource_manager_register_encoded_data` - missing
- `ma_resource_manager_register_file_w` - missing
- `ma_resource_manager_register_file` - missing
- `ma_resource_manager_uninit` - missing
- `ma_resource_manager_unregister_data_w` - missing
- `ma_resource_manager_unregister_data` - missing
- `ma_resource_manager_unregister_file_w` - missing
- `ma_resource_manager_unregister_file` - missing
- `ma_result_description` - missing
- `ma_semaphore_init` - missing
- `ma_semaphore_release` - missing
- `ma_semaphore_uninit` - missing
- `ma_semaphore_wait` - missing
- `ma_silence_pcm_frames` - missing
- `ma_slot_allocator_alloc` - missing
- `ma_slot_allocator_config_init` - missing
- `ma_slot_allocator_free` - missing
- `ma_slot_allocator_get_heap_size` - missing
- `ma_slot_allocator_init_preallocated` - missing
- `ma_slot_allocator_init` - missing
- `ma_slot_allocator_uninit` - missing
- `ma_sound_at_end` - missing
- `ma_sound_config_init_2` - missing
- `ma_sound_config_init` - missing
- `ma_sound_get_attenuation_model` - missing
- `ma_sound_get_cone` - missing
- `ma_sound_get_current_fade_volume` - missing
- `ma_sound_get_cursor_in_pcm_frames` - missing
- `ma_sound_get_cursor_in_seconds` - missing
- `ma_sound_get_data_format` - missing
- `ma_sound_get_data_source` - missing
- `ma_sound_get_direction_to_listener` - missing
- `ma_sound_get_direction` - missing
- `ma_sound_get_directional_attenuation_factor` - missing
- `ma_sound_get_doppler_factor` - missing
- `ma_sound_get_engine` - missing
- `ma_sound_get_length_in_pcm_frames` - missing
- `ma_sound_get_length_in_seconds` - missing
- `ma_sound_get_listener_index` - missing
- `ma_sound_get_max_distance` - missing
- `ma_sound_get_max_gain` - missing
- `ma_sound_get_min_distance` - missing
- `ma_sound_get_min_gain` - missing
- `ma_sound_get_pan_mode` - missing
- `ma_sound_get_pan` - missing
- `ma_sound_get_pinned_listener_index` - missing
- `ma_sound_get_pitch` - missing
- `ma_sound_get_position` - missing
- `ma_sound_get_positioning` - missing
- `ma_sound_get_rolloff` - missing
- `ma_sound_get_time_in_milliseconds` - missing
- `ma_sound_get_time_in_pcm_frames` - missing
- `ma_sound_get_velocity` - missing
- `ma_sound_get_volume` - missing
- `ma_sound_group_config_init_2` - missing
- `ma_sound_group_config_init` - missing
- `ma_sound_group_get_attenuation_model` - missing
- `ma_sound_group_get_cone` - missing
- `ma_sound_group_get_current_fade_volume` - missing
- `ma_sound_group_get_direction_to_listener` - missing
- `ma_sound_group_get_direction` - missing
- `ma_sound_group_get_directional_attenuation_factor` - missing
- `ma_sound_group_get_doppler_factor` - missing
- `ma_sound_group_get_engine` - missing
- `ma_sound_group_get_listener_index` - missing
- `ma_sound_group_get_max_distance` - missing
- `ma_sound_group_get_max_gain` - missing
- `ma_sound_group_get_min_distance` - missing
- `ma_sound_group_get_min_gain` - missing
- `ma_sound_group_get_pan_mode` - missing
- `ma_sound_group_get_pan` - missing
- `ma_sound_group_get_pinned_listener_index` - missing
- `ma_sound_group_get_pitch` - missing
- `ma_sound_group_get_position` - missing
- `ma_sound_group_get_positioning` - missing
- `ma_sound_group_get_rolloff` - missing
- `ma_sound_group_get_time_in_pcm_frames` - missing
- `ma_sound_group_get_velocity` - missing
- `ma_sound_group_get_volume` - missing
- `ma_sound_group_init_ex` - missing
- `ma_sound_group_init` - missing
- `ma_sound_group_is_playing` - missing
- `ma_sound_group_is_spatialization_enabled` - missing
- `ma_sound_group_set_attenuation_model` - missing
- `ma_sound_group_set_cone` - missing
- `ma_sound_group_set_direction` - missing
- `ma_sound_group_set_directional_attenuation_factor` - missing
- `ma_sound_group_set_doppler_factor` - missing
- `ma_sound_group_set_fade_in_milliseconds` - missing
- `ma_sound_group_set_fade_in_pcm_frames` - missing
- `ma_sound_group_set_max_distance` - missing
- `ma_sound_group_set_max_gain` - missing
- `ma_sound_group_set_min_distance` - missing
- `ma_sound_group_set_min_gain` - missing
- `ma_sound_group_set_pan_mode` - missing
- `ma_sound_group_set_pan` - missing
- `ma_sound_group_set_pinned_listener_index` - missing
- `ma_sound_group_set_pitch` - missing
- `ma_sound_group_set_position` - missing
- `ma_sound_group_set_positioning` - missing
- `ma_sound_group_set_rolloff` - missing
- `ma_sound_group_set_spatialization_enabled` - missing
- `ma_sound_group_set_start_time_in_milliseconds` - missing
- `ma_sound_group_set_start_time_in_pcm_frames` - missing
- `ma_sound_group_set_stop_time_in_milliseconds` - missing
- `ma_sound_group_set_stop_time_in_pcm_frames` - missing
- `ma_sound_group_set_velocity` - missing
- `ma_sound_group_set_volume` - missing
- `ma_sound_group_start` - missing
- `ma_sound_group_stop` - missing
- `ma_sound_group_uninit` - missing
- `ma_sound_init_copy` - missing
- `ma_sound_init_ex` - missing
- `ma_sound_init_from_data_source` - missing
- `ma_sound_init_from_file_internal` - missing
- `ma_sound_init_from_file_w` - missing
- `ma_sound_init_from_file` - missing
- `ma_sound_is_looping` - missing
- `ma_sound_is_playing` - missing
- `ma_sound_is_spatialization_enabled` - missing
- `ma_sound_reset_fade` - missing
- `ma_sound_reset_start_time` - missing
- `ma_sound_reset_stop_time_and_fade` - missing
- `ma_sound_reset_stop_time` - missing
- `ma_sound_seek_to_pcm_frame` - missing
- `ma_sound_seek_to_second` - missing
- `ma_sound_set_attenuation_model` - missing
- `ma_sound_set_cone` - missing
- `ma_sound_set_direction` - missing
- `ma_sound_set_directional_attenuation_factor` - missing
- `ma_sound_set_doppler_factor` - missing
- `ma_sound_set_end_callback` - missing
- `ma_sound_set_fade_in_milliseconds` - missing
- `ma_sound_set_fade_in_pcm_frames` - missing
- `ma_sound_set_fade_start_in_milliseconds` - missing
- `ma_sound_set_fade_start_in_pcm_frames` - missing
- `ma_sound_set_looping` - missing
- `ma_sound_set_max_distance` - missing
- `ma_sound_set_max_gain` - missing
- `ma_sound_set_min_distance` - missing
- `ma_sound_set_min_gain` - missing
- `ma_sound_set_pan_mode` - missing
- `ma_sound_set_pan` - missing
- `ma_sound_set_pinned_listener_index` - missing
- `ma_sound_set_pitch` - missing
- `ma_sound_set_position` - missing
- `ma_sound_set_positioning` - missing
- `ma_sound_set_rolloff` - missing
- `ma_sound_set_spatialization_enabled` - missing
- `ma_sound_set_start_time_in_milliseconds` - missing
- `ma_sound_set_start_time_in_pcm_frames` - missing
- `ma_sound_set_stop_time_in_milliseconds` - missing
- `ma_sound_set_stop_time_in_pcm_frames` - missing
- `ma_sound_set_stop_time_with_fade_in_milliseconds` - missing
- `ma_sound_set_stop_time_with_fade_in_pcm_frames` - missing
- `ma_sound_set_velocity` - missing
- `ma_sound_set_volume` - missing
- `ma_sound_start` - missing
- `ma_sound_stop_with_fade_in_milliseconds` - missing
- `ma_sound_stop_with_fade_in_pcm_frames` - missing
- `ma_sound_stop` - missing
- `ma_sound_uninit` - missing
- `ma_spatializer_config_init` - missing
- `ma_spatializer_get_attenuation_model` - missing
- `ma_spatializer_get_cone` - missing
- `ma_spatializer_get_direction` - missing
- `ma_spatializer_get_directional_attenuation_factor` - missing
- `ma_spatializer_get_doppler_factor` - missing
- `ma_spatializer_get_heap_size` - missing
- `ma_spatializer_get_input_channels` - missing
- `ma_spatializer_get_master_volume` - missing
- `ma_spatializer_get_max_distance` - missing
- `ma_spatializer_get_max_gain` - missing
- `ma_spatializer_get_min_distance` - missing
- `ma_spatializer_get_min_gain` - missing
- `ma_spatializer_get_output_channels` - missing
- `ma_spatializer_get_position` - missing
- `ma_spatializer_get_positioning` - missing
- `ma_spatializer_get_relative_position_and_direction` - missing
- `ma_spatializer_get_rolloff` - missing
- `ma_spatializer_get_velocity` - missing
- `ma_spatializer_init_preallocated` - missing
- `ma_spatializer_init` - missing
- `ma_spatializer_listener_config_init` - missing
- `ma_spatializer_listener_get_channel_map` - missing
- `ma_spatializer_listener_get_cone` - missing
- `ma_spatializer_listener_get_direction` - missing
- `ma_spatializer_listener_get_heap_size` - missing
- `ma_spatializer_listener_get_position` - missing
- `ma_spatializer_listener_get_speed_of_sound` - missing
- `ma_spatializer_listener_get_velocity` - missing
- `ma_spatializer_listener_get_world_up` - missing
- `ma_spatializer_listener_init_preallocated` - missing
- `ma_spatializer_listener_init` - missing
- `ma_spatializer_listener_is_enabled` - missing
- `ma_spatializer_listener_set_cone` - missing
- `ma_spatializer_listener_set_direction` - missing
- `ma_spatializer_listener_set_enabled` - missing
- `ma_spatializer_listener_set_position` - missing
- `ma_spatializer_listener_set_speed_of_sound` - missing
- `ma_spatializer_listener_set_velocity` - missing
- `ma_spatializer_listener_set_world_up` - missing
- `ma_spatializer_listener_uninit` - missing
- `ma_spatializer_process_pcm_frames` - missing
- `ma_spatializer_set_attenuation_model` - missing
- `ma_spatializer_set_cone` - missing
- `ma_spatializer_set_direction` - missing
- `ma_spatializer_set_directional_attenuation_factor` - missing
- `ma_spatializer_set_doppler_factor` - missing
- `ma_spatializer_set_master_volume` - missing
- `ma_spatializer_set_max_distance` - missing
- `ma_spatializer_set_max_gain` - missing
- `ma_spatializer_set_min_distance` - missing
- `ma_spatializer_set_min_gain` - missing
- `ma_spatializer_set_position` - missing
- `ma_spatializer_set_positioning` - missing
- `ma_spatializer_set_rolloff` - missing
- `ma_spatializer_set_velocity` - missing
- `ma_spatializer_uninit` - missing
- `ma_spinlock_lock_noyield` - missing
- `ma_spinlock_lock` - missing
- `ma_spinlock_unlock` - missing
- `ma_splitter_node_config_init` - missing
- `ma_splitter_node_init` - missing
- `ma_splitter_node_uninit` - missing
- `ma_stbvorbis_get_cursor_in_pcm_frames` - missing
- `ma_stbvorbis_get_data_format` - missing
- `ma_stbvorbis_get_length_in_pcm_frames` - missing
- `ma_stbvorbis_init_file` - missing
- `ma_stbvorbis_init_memory` - missing
- `ma_stbvorbis_init` - missing
- `ma_stbvorbis_read_pcm_frames` - missing
- `ma_stbvorbis_seek_to_pcm_frame` - missing
- `ma_stbvorbis_uninit` - missing
- `ma_strappend` - missing
- `ma_strcat_s` - missing
- `ma_strcmp_WCHAR` - missing
- `ma_strcmp` - missing
- `ma_strcpy_s_WCHAR` - missing
- `ma_strcpy_s` - missing
- `ma_strlen_WCHAR` - missing
- `ma_strncat_s` - missing
- `ma_strncpy_s` - missing
- `ma_vec3f_cross` - missing
- `ma_vec3f_dist` - missing
- `ma_vec3f_dot` - missing
- `ma_vec3f_init_3f` - missing
- `ma_vec3f_len2` - missing
- `ma_vec3f_len` - missing
- `ma_vec3f_neg` - missing
- `ma_vec3f_normalize` - missing
- `ma_vec3f_sub` - missing
- `ma_version_string` - missing
- `ma_version` - missing
- `ma_vfs_close` - missing
- `ma_vfs_info` - missing
- `ma_vfs_open_and_read_file_w` - missing
- `ma_vfs_open_and_read_file` - missing
- `ma_vfs_open_w` - missing
- `ma_vfs_open` - missing
- `ma_vfs_or_default_close` - missing
- `ma_vfs_or_default_info` - missing
- `ma_vfs_or_default_open_w` - missing
- `ma_vfs_or_default_open` - missing
- `ma_vfs_or_default_read` - missing
- `ma_vfs_or_default_seek` - missing
- `ma_vfs_or_default_tell` - missing
- `ma_vfs_or_default_write` - missing
- `ma_vfs_read` - missing
- `ma_vfs_seek` - missing
- `ma_vfs_tell` - missing
- `ma_vfs_write` - missing
- `ma_volume_db_to_linear` - missing
- `ma_volume_linear_to_db` - missing
- `ma_wav_get_cursor_in_pcm_frames` - missing
- `ma_wav_get_data_format` - missing
- `ma_wav_get_length_in_pcm_frames` - missing
- `ma_wav_init_file_w` - missing
- `ma_wav_init_file` - missing
- `ma_wav_init_memory` - missing
- `ma_wav_init` - missing
- `ma_wav_read_pcm_frames` - missing
- `ma_wav_seek_to_pcm_frame` - missing
- `ma_wav_uninit` - missing
- `ma_waveform_config_init` - missing
- `ma_waveform_init` - missing
- `ma_waveform_read_pcm_frames` - missing
- `ma_waveform_seek_to_pcm_frame` - missing
- `ma_waveform_set_amplitude` - missing
- `ma_waveform_set_frequency` - missing
- `ma_waveform_set_sample_rate` - missing
- `ma_waveform_set_type` - missing
- `ma_waveform_uninit` - missing
- `ma_wcscmp` - missing
- `ma_wcscpy_s` - missing
- `ma_wcslen` - missing
- `ma_wfopen` - missing

### Binding Gaps That Need Design Attention

- Miniaudio's core model uses transparent structs whose addresses must remain
  stable for their lifetimes. Stark should use explicit ABI carriers and
  address-stable storage, or native-owned handles where the layout is too broad
  to make public safely.
- The current playback helper copies the entire sample buffer into a native
  allocation. That is safe, but it is not a substitute for low-latency
  callback-driven playback, capture, duplex, streaming, or ring-buffer APIs.
- Callback ABI is central to devices, VFS, data sources, custom backends,
  resource-manager jobs, node graphs, and sound end callbacks. Do not expose
  safe callback wrappers until lifetime, non-unwinding, and audio-thread
  restrictions are tested.
- Performance-sensitive APIs should preserve preallocated initialization,
  caller-owned heaps, direct slice processing, explicit channel maps, and
  zero-copy acquire/map operations. Avoid wrappers that allocate on every audio
  callback or hide format conversion in hot paths.
- Re-enabling `MA_NO_*` families changes the native object size and exported
  symbol set. The package metadata should record enabled miniaudio feature
  families, and tests should verify those symbols exist in `libVendorMiniaudio.a`.

### Tasks

- [ ] Add a generated miniaudio API inventory test:
  - extract unique `MA_API` function names from `vendor/native/miniaudio/miniaudio.h`
  - compare against an expected checked-in inventory for `0.11.25`
  - classify each symbol as bound, covered by a safe wrapper, intentionally unsupported, or pending
  - fail C# tests when the vendored header changes without updating the audit/inventory
  - add Stark self-hosted source tests for the public Stark API shape that exists today

- [ ] Add core miniaudio constants and ABI carriers:
  - expose version constants and `ma_version` / `ma_version_string`
  - expose complete result-code mapping and `ma_result_description`
  - expose all sample formats, backend IDs, device types/states/notifications, channel positions, standard channel maps, seek origins, allocation callbacks, log callbacks, and core config carriers
  - add C# ABI layout tests for every public carrier
  - add Stark self-hosted compile/API tests for the constants and carrier names

- [ ] Add full device/context support:
  - bind every `ma_context_*` function
  - bind every `ma_device_*` function
  - expose playback, capture, duplex, and loopback device configs
  - expose device enumeration and selected device IDs
  - expose master volume/gain and device state
  - test default playback, capture enumeration, selected-device initialization where available, callback lifetime, and start/stop rules

- [ ] Add VFS and data-source support:
  - bind every `ma_vfs_*`, `ma_vfs_or_default_*`, and `ma_default_vfs_*` function
  - bind every `ma_data_source_*` function
  - add safe adapters for Stark-owned file/memory data sources only after callback ABI tests exist
  - test read/seek/loop/range/next-chain behavior

- [ ] Add audio buffer and ring buffer support:
  - bind every `ma_audio_buffer_*`, `ma_audio_buffer_ref_*`, and `ma_paged_audio_buffer_*` function
  - bind every `ma_rb_*` and `ma_pcm_rb_*` function
  - preserve preallocated and map/acquire APIs
  - test zero-copy map/acquire, read/write cursor movement, wraparound, reset, and no hidden allocation in hot paths

- [ ] Add PCM conversion, resampling, channel maps, and volume helpers:
  - bind every sample conversion, interleave/deinterleave, copy, silence, clip, mix, blend, volume, channel map, channel converter, resampler, linear resampler, and data converter function listed in the missing conversion family
  - expose explicit channel maps and format conversion configs
  - test format conversion correctness, clipping behavior, channel remap behavior, resampling frame-count predictions, latency reporting, and in-place conversion cases

- [ ] Add DSP/filtering/spatialization support:
  - bind all non-node biquad, LPF, HPF, BPF, notch, peak, low-shelf, high-shelf, delay, gainer, panner, fader, spatializer listener, and spatializer functions
  - preserve preallocated init APIs
  - test deterministic filter output, latency reporting, gain/pan/fade behavior, and spatialization parameter setters/getters

- [ ] Expand decode support:
  - expose the raw `ma_decoder_*` API in addition to the existing safe `Decoder`
  - expose `u8`, `s24`, `s32`, and native-format decode paths
  - expose VFS decode and custom backend configuration
  - bind built-in `ma_wav_*`, `ma_mp3_*`, `ma_flac_*`, and `ma_stbvorbis_*` wrappers
  - bind embedded dr_wav, dr_flac, and dr_mp3 APIs needed for metadata, seek tables, memory/file one-shot decode, and format-specific behavior
  - test WAV/MP3/FLAC memory and file decode, cursor/length/available-frame queries, seeking, metadata paths, and malformed input

- [ ] Re-enable and bind encoding:
  - remove `MA_NO_ENCODING` when building the vendor package or provide a separate full-feature native object
  - bind every `ma_encoder_*` function
  - bind dr_wav write and memory-write APIs
  - test WAV file and memory encoding, sequential write sizes, and read-back through the decoder

- [ ] Re-enable and bind waveform/noise generation:
  - remove `MA_NO_GENERATION` when building the vendor package or provide a separate full-feature native object
  - bind every `ma_waveform_*`, `ma_pulsewave_*`, `ma_noise_*`, and debug sine-fill function
  - test deterministic generated samples, seeking, parameter setters, and no allocation in read paths

- [ ] Re-enable and bind the resource manager:
  - remove `MA_NO_RESOURCE_MANAGER` when building the vendor package or provide a separate full-feature native object
  - bind every resource-manager, data-buffer, data-stream, and data-source function
  - expose file/data registration, async job processing, pipeline notifications, result polling, and loop controls
  - test synchronous and async loads, streaming, data registration, unregister behavior, and job queue behavior

- [ ] Re-enable and bind the node graph:
  - remove `MA_NO_NODE_GRAPH` when building the vendor package or provide a separate full-feature native object
  - bind every `ma_node_*` and `ma_node_graph_*` function
  - bind every node-specific DSP function
  - test graph construction, bus attach/detach, output bus volume, state scheduling, time control, and readback through graph endpoint

- [ ] Re-enable and bind the engine, sounds, and sound groups:
  - remove `MA_NO_ENGINE` when building the vendor package or provide a separate full-feature native object
  - bind every `ma_engine_*`, `ma_sound_*`, and `ma_sound_group_*` function
  - expose sound flags, listener controls, spatialization, fades, scheduled starts/stops, looping, callbacks, and group controls
  - test simple engine playback, no-device rendering, file/data-source sounds, groups, scheduled fade/start/stop behavior, seeking, looping, and listener spatialization

- [ ] Update Miniaudio examples:
  - keep the current decode/playback example
  - add device enumeration example
  - add capture or duplex example when safe callback wrappers exist
  - add decode-to-buffer example covering `s16` and `f32`
  - add resample/convert example
  - add filter/spatialization example
  - add WAV encode round-trip example once encoding is enabled
  - add engine/sound example once the engine is enabled
  - keep console output as simple `WriteLine(...)` calls without checking every status

- [ ] Add C# compiler/integration tests for every new Miniaudio surface:
  - native symbol availability for each enabled family
  - package-image native metadata and feature macro coverage
  - ABI layout tests for carriers
  - deterministic decode/convert/filter/generator tests
  - callback lifetime/non-unwinding tests
  - platform-gated device/capture/engine tests

- [ ] Add Stark self-hosted tests for every new Miniaudio surface that the current Stark test harness can express:
  - source-level API shape tests
  - deterministic memory decode tests
  - conversion/resampling/filtering tests
  - generated audio tests
  - encoder round-trip tests
  - device/engine tests only where native linking and platform gates are available

## `Vendor.Cgltf`

### Source Of Truth

Audited against latest upstream cgltf release `v1.15`, which matches Stark's
vendored loader snapshot:

- Upstream: <https://github.com/jkuhlmann/cgltf>
- Releases: <https://github.com/jkuhlmann/cgltf/releases>
- Loader header: `vendor/native/cgltf/cgltf.h`
- Upstream writer header: <https://raw.githubusercontent.com/jkuhlmann/cgltf/v1.15/cgltf_write.h>
- Vendored release: `v1.15`
- Vendored commit: `360db1a95480fe102ae9c69b27c5d101167ff5ba`
- Vendored date: 2025-02-09
- Current native implementation unit: `vendor/CgltfImplementation.c`

The upstream `v1.15` release is marked latest on GitHub and added
`cgltf_find_accessor`, sampler filter/wrap enums, `EXT_texture_webp`, and
`KHR_materials_diffuse_transmission`. Stark's vendor drop only includes
`cgltf.h`; upstream also ships `cgltf_write.h` with two public writer
functions. A complete cgltf vendor binding should either vendor and expose the
writer header or explicitly document that Stark's `Vendor.Cgltf` is loader-only.

The public loader function inventory was extracted from the pinned header with:

```bash
sed -n '843,904p' vendor/native/cgltf/cgltf.h \
  | rg 'cgltf_[A-Za-z0-9_]+\(' \
  | sed -E 's/.*(cgltf_[A-Za-z0-9_]+)\(.*/\1/' \
  | sort -u
```

This produces 37 public loader functions in `cgltf.h`. The public data-model
inventory contains 66 unique public `cgltf_*` enum/struct carriers.

### Current Stark Coverage

Current Stark file: `vendor/src/Vendor/Cgltf.stark`.

Current native file: `vendor/CgltfImplementation.c`.

Stark currently exposes:

- `Document`, an owning safe handle around an internal `stark_cgltf_document`
- `ParseFromMemory`
- `ParseFromFile`
- `Validate`
- `GetCounts`
- `GetMeshInfo`
- `GetMaterialInfo`
- `GetBufferInfo`
- `GetNodeInfo`
- `GetSceneInfo`
- `GetAnimationInfo`
- `GetAccessorInfo`
- `GetPrimitiveInfo`
- `CopyMeshNameBytes`
- `CopyMaterialNameBytes`
- `CopyBufferNameBytes`
- `CopyBufferUriBytes`
- `CopyNodeNameBytes`
- `CopySceneNameBytes`
- `CopyAnimationNameBytes`

The wrapper is useful for small asset-summary examples, but it is not a full
glTF binding. It does not expose the cgltf object graph, raw arrays, most
metadata fields, accessor payload reads, image/texture/sampler/skin/camera/light
details, extensions, extras JSON, material extension payloads, transform helpers,
custom file/memory callbacks, or writing.

### Complete Loader Function Inventory

- `cgltf_parse` - covered by `ParseFromMemory`, but raw options, zero-copy caller-owned lifetime, forced file type, token-count tuning, custom allocators, and custom file callbacks are missing.
- `cgltf_parse_file` - covered by `ParseFromFile`, but raw options, custom callbacks, and forced file type are missing.
- `cgltf_load_buffers` - partially covered only inside `ParseFromFile(..., LoadExternalBuffers, ...)`; there is no explicit post-parse load, no memory-parse buffer load, and no caller-controlled path.
- `cgltf_load_buffer_base64` - missing.
- `cgltf_decode_string` - missing.
- `cgltf_decode_uri` - missing.
- `cgltf_validate` - covered by `Validate`.
- `cgltf_free` - covered by `Document` drop, but raw ownership is not exposed.
- `cgltf_node_transform_local` - missing.
- `cgltf_node_transform_world` - missing.
- `cgltf_buffer_view_data` - missing.
- `cgltf_find_accessor` - missing.
- `cgltf_accessor_read_float` - missing.
- `cgltf_accessor_read_uint` - missing.
- `cgltf_accessor_read_index` - missing.
- `cgltf_num_components` - missing.
- `cgltf_component_size` - missing.
- `cgltf_calc_size` - missing.
- `cgltf_accessor_unpack_floats` - missing.
- `cgltf_accessor_unpack_indices` - missing.
- `cgltf_copy_extras_json` - missing; upstream marks it deprecated in favor of direct `cgltf_extras.data`, which is also missing.
- `cgltf_mesh_index` - partially covered internally for selected references, but not public.
- `cgltf_material_index` - partially covered internally for primitive material references, but not public.
- `cgltf_accessor_index` - partially covered internally for primitive index references, but not public.
- `cgltf_buffer_view_index` - partially covered internally for accessor buffer-view references, but not public.
- `cgltf_buffer_index` - missing.
- `cgltf_image_index` - missing.
- `cgltf_texture_index` - missing.
- `cgltf_sampler_index` - missing.
- `cgltf_skin_index` - partially covered internally for node skin references, but not public.
- `cgltf_camera_index` - partially covered internally for node camera references, but not public.
- `cgltf_light_index` - missing.
- `cgltf_node_index` - missing.
- `cgltf_scene_index` - missing.
- `cgltf_animation_index` - missing.
- `cgltf_animation_sampler_index` - missing.
- `cgltf_animation_channel_index` - missing.

### Complete Writer Function Inventory

These functions are in upstream `cgltf_write.h`, but `cgltf_write.h` is not
vendored and no writer surface exists in Stark:

- `cgltf_write_file` - missing.
- `cgltf_write` - missing.

### Complete Public Type Inventory

Status values:

- `covered` means Stark exposes an equivalent safe value today.
- `partial` means Stark exposes only a small summary or a renamed subset.
- `missing` means there is no public Stark equivalent.

- `cgltf_accessor` - partial; `AccessorInfo` omits offset, min/max, sparse details, extras, extensions, and raw buffer access.
- `cgltf_accessor_sparse` - missing.
- `cgltf_alpha_mode` - partial; `AlphaMode` exposes opaque/mask/blend, but not exact native max enum.
- `cgltf_animation` - partial; `AnimationInfo` only exposes name length, sampler count, and channel count.
- `cgltf_animation_channel` - missing.
- `cgltf_animation_path_type` - missing.
- `cgltf_animation_sampler` - missing.
- `cgltf_anisotropy` - missing.
- `cgltf_asset` - missing.
- `cgltf_attribute` - missing.
- `cgltf_attribute_type` - missing.
- `cgltf_buffer` - partial; `BufferInfo` exposes name length, URI length, size, and loaded flag only.
- `cgltf_buffer_view` - missing except `DocumentCounts.BufferViews`.
- `cgltf_buffer_view_type` - missing.
- `cgltf_camera` - missing except `DocumentCounts.Cameras` and node camera index flags.
- `cgltf_camera_orthographic` - missing.
- `cgltf_camera_perspective` - missing.
- `cgltf_camera_type` - missing.
- `cgltf_clearcoat` - missing.
- `cgltf_component_type` - partial; `ComponentType` exposes invalid/i8/u8/i16/u16/u32/f32 only.
- `cgltf_data` - partial; `Document` owns it but does not expose the raw root data graph.
- `cgltf_data_free_method` - missing.
- `cgltf_diffuse_transmission` - missing.
- `cgltf_dispersion` - missing.
- `cgltf_draco_mesh_compression` - missing.
- `cgltf_emissive_strength` - missing.
- `cgltf_extension` - missing.
- `cgltf_extras` - missing.
- `cgltf_file_options` - missing.
- `cgltf_file_type` - missing.
- `cgltf_filter_type` - missing.
- `cgltf_image` - missing except `DocumentCounts.Images`.
- `cgltf_interpolation_type` - missing.
- `cgltf_ior` - missing.
- `cgltf_iridescence` - missing.
- `cgltf_light` - missing except `DocumentCounts.Lights`.
- `cgltf_light_type` - missing.
- `cgltf_material` - partial; `MaterialInfo` exposes name length, two PBR flags, alpha mode, double-sided, and unlit only.
- `cgltf_material_mapping` - missing.
- `cgltf_material_variant` - missing.
- `cgltf_memory_options` - missing.
- `cgltf_mesh` - partial; `MeshInfo` exposes name length, primitive count, weight count, and target-name count only.
- `cgltf_mesh_gpu_instancing` - missing.
- `cgltf_meshopt_compression` - missing.
- `cgltf_meshopt_compression_filter` - missing.
- `cgltf_meshopt_compression_mode` - missing.
- `cgltf_morph_target` - missing.
- `cgltf_node` - partial; `NodeInfo` exposes name length, counts, selected reference indices, and transform-presence flags only.
- `cgltf_options` - missing.
- `cgltf_pbr_metallic_roughness` - missing except `MaterialInfo.HasPbrMetallicRoughness`.
- `cgltf_pbr_specular_glossiness` - missing except `MaterialInfo.HasPbrSpecularGlossiness`.
- `cgltf_primitive` - partial; `PrimitiveInfo` exposes mode, counts, indices accessor index, and material index only.
- `cgltf_primitive_type` - partial; `PrimitiveMode` exposes the primary values.
- `cgltf_result` - partial; `CgltfError` maps result values plus Stark wrapper errors.
- `cgltf_sampler` - missing except `DocumentCounts.Samplers`.
- `cgltf_scene` - partial; `SceneInfo` exposes name length and node count only.
- `cgltf_sheen` - missing.
- `cgltf_skin` - missing except `DocumentCounts.Skins` and node skin index flags.
- `cgltf_specular` - missing.
- `cgltf_texture` - missing except `DocumentCounts.Textures`.
- `cgltf_texture_transform` - missing.
- `cgltf_texture_view` - missing.
- `cgltf_transmission` - missing.
- `cgltf_type` - partial; `AccessorType` exposes scalar/vector/matrix values.
- `cgltf_volume` - missing.
- `cgltf_wrap_mode` - missing.

### Missing Data-Model Coverage

The most important gap is not just individual helper functions. cgltf is
designed as a transparent data graph, and Stark currently exposes only small
summary structs. Missing public data includes:

- root data: file type, asset metadata, default scene, variants, extensions
  used/required, unprocessed root extensions, extras, JSON chunk, BIN chunk,
  and custom memory/file callback state
- buffers and buffer views: buffer data pointer, data-free method, buffer-view
  offset/size/stride/type/data override, meshopt compression fields, extras,
  and extensions
- accessors: byte offset, min/max arrays, sparse count/index/value buffer views,
  sparse offsets/component types, extensions, extras, direct read helpers, and
  unpack helpers
- mesh primitives: attributes and attribute semantic/index pairs, morph targets,
  Draco compression, material variant mappings, extras, and extensions
- materials: full PBR metallic/roughness factors and textures, specular/glossiness,
  clearcoat, transmission, volume, IOR, specular, sheen, emissive strength,
  iridescence, diffuse transmission, anisotropy, dispersion, normal/occlusion/
  emissive texture views, alpha cutoff, emissive factor, extras, and extensions
- images/textures/samplers: image URI, buffer-view image data, MIME type,
  texture images including BasisU and WebP, sampler filter/wrap modes, extras,
  and extensions
- skins/cameras/lights: joints, skeleton, inverse bind matrices, perspective and
  orthographic camera parameters, punctual light type/color/intensity/range/spot
  cone fields, and extras/extensions
- nodes/scenes/animations: parent/child arrays, raw TRS/matrix values,
  mesh-gpu-instancing attributes, scene node arrays, animation samplers, channels,
  interpolation, target paths, and extras/extensions
- writer support: Stark cannot emit glTF JSON or GLB because `cgltf_write.h` is
  absent and there is no Stark-owned way to construct or expose a full `cgltf_data`
  graph

### Binding Gaps That Need Design Attention

- A complete binding should not copy vertex/index payloads unnecessarily. Accessor
  read/unpack helpers and buffer-view data should write into caller-owned buffers
  or expose borrow-scoped views tied to the owning `Document`.
- Exposing raw `cgltf_data` directly would require ABI carriers for many nested
  pointer-heavy structs. Safer high-level views can still be complete if every
  array item and helper has an index-based accessor that preserves document
  lifetime and avoids per-call heap allocation.
- `ParseFromMemory` currently copies the entire encoded input so cgltf's internal
  pointers stay valid. That is correct for safety, but a performance-complete
  binding should also expose a zero-copy parse path for caller-owned stable bytes
  whose lifetime is proven to outlive the `Document`.
- Custom `cgltf_options` matter for performance and integration: fixed file type,
  JSON token count, custom allocators, custom file callbacks, and controlled
  buffer loading all change allocations and I/O behavior.
- Writer support probably requires either a Stark-owned builder API for `cgltf_data`
  or a lower-level unsafe ABI layer. A safe writer that only serializes documents
  parsed by cgltf is useful, but it is not enough for asset-generation workflows.

### Tasks

- [ ] Add generated cgltf API inventory tests:
  - extract the 37 public loader functions from `vendor/native/cgltf/cgltf.h`
  - fetch or vendor `cgltf_write.h` and extract `cgltf_write_file` and `cgltf_write`
  - extract the 66 public loader data-model carriers from `cgltf.h`
  - classify each function/type as bound, covered by a safe wrapper, intentionally unsupported, or pending
  - fail C# tests when the vendored headers change without updating this audit and the checked-in inventory
  - add Stark self-hosted source tests for the public API names that exist today

- [ ] Vendor and build upstream `cgltf_write.h`:
  - add `vendor/native/cgltf/cgltf_write.h` pinned to `v1.15`
  - record its SHA-256 in `vendor/native/cgltf/VERSION.md`
  - update `vendor/CgltfImplementation.c` or add a dedicated implementation unit with `CGLTF_WRITE_IMPLEMENTATION`
  - update package build metadata so writer symbols are included in `libVendorCgltf.a`
  - test native symbol availability for `cgltf_write` and `cgltf_write_file`

- [ ] Add complete enum coverage:
  - expose exact Stark enums for `cgltf_file_type`, `cgltf_buffer_view_type`, `cgltf_attribute_type`, `cgltf_animation_path_type`, `cgltf_interpolation_type`, `cgltf_camera_type`, `cgltf_light_type`, `cgltf_data_free_method`, `cgltf_meshopt_compression_mode`, `cgltf_meshopt_compression_filter`, `cgltf_filter_type`, and `cgltf_wrap_mode`
  - complete the existing component/accessor/primitive/alpha/result mappings
  - add C# ABI/value tests against the pinned C enum values
  - add Stark self-hosted enum compile tests

- [ ] Add complete indexed data-model views:
  - expose index-addressed accessors for asset, meshes, primitives, attributes, morph targets, materials, textures, images, samplers, buffers, buffer views, accessors, skins, cameras, lights, nodes, scenes, animations, variants, extras, and extensions
  - include every field listed in the public type inventory, either as a value field or as an explicit accessor for pointer/array fields
  - avoid heap allocation on simple metadata reads
  - add C# tests with glTF fixtures covering every object family
  - add Stark self-hosted source tests for every new public view/accessor name

- [ ] Add parse/load/options coverage:
  - expose forced file type, JSON token count, validation choice, custom allocator hooks where Stark FFI can safely model them, and custom file callbacks when callback ABI restrictions are tested
  - expose explicit `LoadBuffers` for already parsed documents, including documents parsed from memory with a caller-supplied base path
  - expose `LoadBufferBase64`, `DecodeStringInPlace`, and `DecodeUriInPlace`
  - add tests for GLTF, GLB, invalid options, invalid JSON, data-too-short, legacy GLB, base64 buffers, percent-encoded URIs, and custom path handling

- [ ] Add zero-copy and owned-memory parse modes:
  - keep the current safe copying `ParseFromMemory`
  - add a zero-copy parse variant whose input lifetime is tied to the returned document
  - reject or make unsafe any parse mode whose backing bytes cannot outlive the document
  - test that GLB BIN chunk and JSON string references remain valid for the full document lifetime

- [ ] Add accessor payload APIs:
  - bind `cgltf_buffer_view_data`
  - bind `cgltf_find_accessor`
  - bind `cgltf_accessor_read_float`, `cgltf_accessor_read_uint`, and `cgltf_accessor_read_index`
  - bind `cgltf_num_components`, `cgltf_component_size`, and `cgltf_calc_size`
  - bind `cgltf_accessor_unpack_floats` and `cgltf_accessor_unpack_indices`
  - write into caller-owned buffers and return exact element counts
  - test scalar/vector/matrix data, normalized integer conversion, sparse accessors, index unpacking, invalid output sizes, and unloaded-buffer behavior

- [ ] Add transform APIs:
  - bind `cgltf_node_transform_local`
  - bind `cgltf_node_transform_world`
  - expose stable 4x4 matrix output carriers
  - test TRS, matrix, parent-child world transforms, and identity/default cases

- [ ] Add public index helper coverage:
  - expose `cgltf_mesh_index`, `cgltf_material_index`, `cgltf_accessor_index`, `cgltf_buffer_view_index`, `cgltf_buffer_index`, `cgltf_image_index`, `cgltf_texture_index`, `cgltf_sampler_index`, `cgltf_skin_index`, `cgltf_camera_index`, `cgltf_light_index`, `cgltf_node_index`, `cgltf_scene_index`, `cgltf_animation_index`, `cgltf_animation_sampler_index`, and `cgltf_animation_channel_index`
  - return `CgltfNoIndex` or a typed option/result for null/out-of-document references
  - test valid references, null references, and references from the wrong owner document

- [ ] Add extras and extension APIs:
  - expose direct `cgltf_extras.data` access for every object family
  - expose unprocessed extension name/data pairs for every object family
  - bind deprecated `cgltf_copy_extras_json` only as a compatibility helper and mark it as deprecated in Stark docs
  - test root, material, node, light, camera, accessor, buffer, and extension JSON payloads

- [ ] Add material/texture/image/sampler coverage:
  - expose every field of `cgltf_material`, all material extension structs, all texture views, texture transforms, image sources, BasisU/WebP image references, and sampler filter/wrap modes
  - test metallic/roughness, specular/glossiness, clearcoat, transmission, volume, IOR, specular, sheen, emissive strength, iridescence, diffuse transmission, anisotropy, dispersion, unlit, normal, occlusion, emissive, texture transform, BasisU, and WebP fixtures

- [ ] Add skin/camera/light/animation coverage:
  - expose skins, joints, inverse bind matrices, cameras, perspective/orthographic fields, punctual lights, animation samplers, animation channels, interpolation, and target paths
  - test skeletal, camera, light, and animated glTF fixtures

- [ ] Add writer APIs:
  - bind `cgltf_write` for caller-owned output buffers
  - bind `cgltf_write_file`
  - support GLTF JSON and GLB output based on `cgltf_options.type`
  - expose serialization for parsed documents first
  - add a Stark-owned builder or unsafe raw data-construction path before claiming asset-generation support
  - test size-query mode, exact output length, GLTF JSON round-trip, GLB round-trip, extension emission, and file-write errors

- [ ] Update cgltf examples:
  - keep the current asset-summary example
  - add a mesh payload example that reads positions and indices through accessor APIs
  - add a material/texture summary example
  - add a scene transform example
  - add an animation summary example
  - add a GLTF/GLB write round-trip example once writer support exists
  - keep console output as simple `WriteLine(...)` calls without checking every status

- [ ] Add C# compiler/integration tests for every new cgltf surface:
  - native symbol availability for loader and writer functions
  - package-image native metadata for `cgltf.h` and `cgltf_write.h`
  - ABI layout/value tests for every public carrier and enum
  - fixture-based parse, validate, buffer load, accessor unpack, transform, extras, extension, and writer tests
  - invalid-input and bounds-check tests for every index-addressed accessor

- [ ] Add Stark self-hosted tests for every new cgltf surface that the current Stark test harness can express:
  - source-level API shape tests for every public enum, struct, and function
  - parse/count/accessor/material/texture/scene/animation tests using local fixtures
  - writer round-trip tests once native linking and file output are available
## `Vendor.GLFW`

### Source Of Truth

Audited against upstream GLFW `3.4`, the latest GitHub release and the API version already hardcoded by `Vendor.GLFW` as `GlfwVersionMajor = 3`, `GlfwVersionMinor = 4`, `GlfwVersionRevision = 0`.

- Upstream: <https://github.com/glfw/glfw>
- Release: <https://github.com/glfw/glfw/releases/tag/3.4>
- Documentation: <https://www.glfw.org/docs/3.4/>
- Header used for inventory: <https://raw.githubusercontent.com/glfw/glfw/3.4/include/GLFW/glfw3.h>
- Native-access header: <https://raw.githubusercontent.com/glfw/glfw/3.4/include/GLFW/glfw3native.h>
- Local Stark binding: `vendor/src/Vendor/GLFW.stark`
- Local native callback bridge: `vendor/GlfwEventBridge.c`
- Build dependency: system `glfw3` package through `pkg-config` or explicit `GLFW_INCLUDE_DIR` / `GLFW_LIBRARY_DIR`

The current environment did not have `pkg-config glfw3` available when this audit was written, so the local installed-header version could not be checked. The audit is pinned to upstream `3.4` because that is the version declared by the Stark binding and the current GLFW latest release.

The public function inventory was extracted from `glfw3.h` with:

```bash
awk '/^GLFWAPI / { for (i=1;i<=NF;i++) if ($i ~ /^glfw[A-Za-z0-9_]+\(/) { sub(/\(.*/, "", $i); print $i; } }' glfw3.h | sort -u
```

This produces 124 public core `glfw*` functions. `glfw3native.h` adds 25
platform-native access functions, gated by `GLFW_EXPOSE_NATIVE_*` macros. The
public macro inventory contains 332 `GLFW_*` constants/macros. The public type
inventory contains 3 opaque handle types, 5 value/ABI structs, 2 generic
procedure typedefs, and 24 callback/allocator typedefs.

### Current Stark Coverage

Stark currently exposes safe ownership wrappers for:

- `Library`, initialized by `Initialize()` and terminated in `drop`
- `Window`, created by `CreateWindow` / `CreateHiddenWindow` and destroyed in `drop`
- version query, last-error enum mapping, default/window hints for a small hint subset, window size/framebuffer size, close flag, OpenGL context attach/detach, buffer swap/swap interval, time, key/mouse-button polling, cursor position, event polling/waiting, required Vulkan extension count/token, and Vulkan surface creation
- a fixed native event bridge for close, window size, framebuffer size, key, mouse button, cursor position, scroll, and focus events

The binding is useful for hidden windows, simple OpenGL/Vulkan bootstrap, and a fixed event queue. It is not a complete GLFW binding. It lacks monitors/video modes/gamma, fullscreen windows, window position/title/icon/opacity/attributes, cursor objects and cursor modes, clipboard, joystick/gamepad, most callbacks, custom allocators, init/platform selection, timer value/frequency, OpenGL extension/proc lookup, Vulkan proc/presentation helpers, user pointers, and most constants.

### Complete Public Type Inventory

Status values: `covered` means Stark exposes a usable equivalent today, `partial` means only a small safe subset exists, and `missing` means no public Stark equivalent exists.

- `GLFWwindow` - partial; represented by internal `GLFWwindowNative` and owned `Window`, but raw/shared/fullscreen/current-context handles are not public.
- `GLFWmonitor` - partial internally for `glfwCreateWindow`, but monitors are not public and monitor APIs are missing.
- `GLFWcursor` - missing.
- `GLFWvidmode` - missing.
- `GLFWgammaramp` - missing.
- `GLFWimage` - missing.
- `GLFWgamepadstate` - missing.
- `GLFWallocator` - missing.
- `GLFWglproc` - missing.
- `GLFWvkproc` - missing.
- `GLFWallocatefun` - missing.
- `GLFWreallocatefun` - missing.
- `GLFWdeallocatefun` - missing.
- `GLFWerrorfun` - missing.
- `GLFWwindowposfun` - missing.
- `GLFWwindowsizefun` - covered only by fixed native event bridge.
- `GLFWwindowclosefun` - covered only by fixed native event bridge.
- `GLFWwindowrefreshfun` - missing.
- `GLFWwindowfocusfun` - covered only by fixed native event bridge.
- `GLFWwindowiconifyfun` - missing.
- `GLFWwindowmaximizefun` - missing.
- `GLFWframebuffersizefun` - covered only by fixed native event bridge.
- `GLFWwindowcontentscalefun` - missing.
- `GLFWmousebuttonfun` - covered only by fixed native event bridge.
- `GLFWcursorposfun` - covered only by fixed native event bridge.
- `GLFWcursorenterfun` - missing.
- `GLFWscrollfun` - covered only by fixed native event bridge.
- `GLFWkeyfun` - covered only by fixed native event bridge.
- `GLFWcharfun` - missing.
- `GLFWcharmodsfun` - missing and deprecated upstream, but still part of the `3.4` public header.
- `GLFWdropfun` - missing.
- `GLFWmonitorfun` - missing.
- `GLFWjoystickfun` - missing.

### Complete Public Function Inventory

- `glfwCreateCursor` - missing.
- `glfwCreateStandardCursor` - missing.
- `glfwCreateWindowSurface` - covered by CreateVulkanSurface with raw instance token and no custom allocator.
- `glfwCreateWindow` - partially covered by CreateWindow/CreateHiddenWindow; monitor fullscreen and shared context parameters are missing.
- `glfwDefaultWindowHints` - covered by DefaultWindowHints.
- `glfwDestroyCursor` - missing.
- `glfwDestroyWindow` - covered by Window drop.
- `glfwExtensionSupported` - missing.
- `glfwFocusWindow` - missing.
- `glfwGetClipboardString` - missing.
- `glfwGetCurrentContext` - partially covered by HasCurrentContext; current window handle is not returned.
- `glfwGetCursorPos` - covered by GetCursorPosition.
- `glfwGetError` - partially covered by GetLastError and internal error mapping; description string is missing.
- `glfwGetFramebufferSize` - covered by GetFramebufferSize.
- `glfwGetGamepadName` - missing.
- `glfwGetGamepadState` - missing.
- `glfwGetGammaRamp` - missing.
- `glfwGetInputMode` - missing.
- `glfwGetInstanceProcAddress` - missing.
- `glfwGetJoystickAxes` - missing.
- `glfwGetJoystickButtons` - missing.
- `glfwGetJoystickGUID` - missing.
- `glfwGetJoystickHats` - missing.
- `glfwGetJoystickName` - missing.
- `glfwGetJoystickUserPointer` - missing.
- `glfwGetKeyName` - missing.
- `glfwGetKeyScancode` - missing.
- `glfwGetKey` - covered by GetKey.
- `glfwGetMonitorContentScale` - missing.
- `glfwGetMonitorName` - missing.
- `glfwGetMonitorPhysicalSize` - missing.
- `glfwGetMonitorPos` - missing.
- `glfwGetMonitorUserPointer` - missing.
- `glfwGetMonitorWorkarea` - missing.
- `glfwGetMonitors` - missing.
- `glfwGetMouseButton` - covered by GetMouseButton.
- `glfwGetPhysicalDevicePresentationSupport` - missing.
- `glfwGetPlatform` - missing.
- `glfwGetPrimaryMonitor` - missing.
- `glfwGetProcAddress` - missing.
- `glfwGetRequiredInstanceExtensions` - partially covered by RequiredVulkanInstanceExtensions; only count/token is exposed, not extension names.
- `glfwGetTime` - covered by GetTime.
- `glfwGetTimerFrequency` - missing.
- `glfwGetTimerValue` - missing.
- `glfwGetVersionString` - missing.
- `glfwGetVersion` - covered by GetVersion.
- `glfwGetVideoMode` - missing.
- `glfwGetVideoModes` - missing.
- `glfwGetWindowAttrib` - missing.
- `glfwGetWindowContentScale` - missing.
- `glfwGetWindowFrameSize` - missing.
- `glfwGetWindowMonitor` - missing.
- `glfwGetWindowOpacity` - missing.
- `glfwGetWindowPos` - missing.
- `glfwGetWindowSize` - covered by GetWindowSize.
- `glfwGetWindowTitle` - missing.
- `glfwGetWindowUserPointer` - missing.
- `glfwHideWindow` - missing.
- `glfwIconifyWindow` - missing.
- `glfwInitAllocator` - missing.
- `glfwInitHint` - missing.
- `glfwInitVulkanLoader` - missing.
- `glfwInit` - covered by Initialize.
- `glfwJoystickIsGamepad` - missing.
- `glfwJoystickPresent` - missing.
- `glfwMakeContextCurrent` - covered by MakeContextCurrent and DetachCurrentContext.
- `glfwMaximizeWindow` - missing.
- `glfwPlatformSupported` - missing.
- `glfwPollEvents` - covered by PollEvents.
- `glfwPostEmptyEvent` - covered by PostEmptyEvent.
- `glfwRawMouseMotionSupported` - missing.
- `glfwRequestWindowAttention` - missing.
- `glfwRestoreWindow` - missing.
- `glfwSetCharCallback` - missing.
- `glfwSetCharModsCallback` - missing.
- `glfwSetClipboardString` - missing.
- `glfwSetCursorEnterCallback` - missing.
- `glfwSetCursorPosCallback` - covered only through GlfwEventBridge cursor-position events; raw callback setter is not public.
- `glfwSetCursorPos` - missing.
- `glfwSetCursor` - missing.
- `glfwSetDropCallback` - missing.
- `glfwSetErrorCallback` - missing.
- `glfwSetFramebufferSizeCallback` - covered only through GlfwEventBridge framebuffer-size events; raw callback setter is not public.
- `glfwSetGammaRamp` - missing.
- `glfwSetGamma` - missing.
- `glfwSetInputMode` - missing.
- `glfwSetJoystickCallback` - missing.
- `glfwSetJoystickUserPointer` - missing.
- `glfwSetKeyCallback` - covered only through GlfwEventBridge key events; raw callback setter is not public.
- `glfwSetMonitorCallback` - missing.
- `glfwSetMonitorUserPointer` - missing.
- `glfwSetMouseButtonCallback` - covered only through GlfwEventBridge mouse-button events; raw callback setter is not public.
- `glfwSetScrollCallback` - covered only through GlfwEventBridge scroll events; raw callback setter is not public.
- `glfwSetTime` - covered by SetTime.
- `glfwSetWindowAspectRatio` - missing.
- `glfwSetWindowAttrib` - missing.
- `glfwSetWindowCloseCallback` - covered only through GlfwEventBridge close events; raw callback setter is not public.
- `glfwSetWindowContentScaleCallback` - missing.
- `glfwSetWindowFocusCallback` - covered only through GlfwEventBridge focus events; raw callback setter is not public.
- `glfwSetWindowIcon` - missing.
- `glfwSetWindowIconifyCallback` - missing.
- `glfwSetWindowMaximizeCallback` - missing.
- `glfwSetWindowMonitor` - missing.
- `glfwSetWindowOpacity` - missing.
- `glfwSetWindowPosCallback` - missing.
- `glfwSetWindowPos` - missing.
- `glfwSetWindowRefreshCallback` - missing.
- `glfwSetWindowShouldClose` - covered by SetWindowShouldClose.
- `glfwSetWindowSizeCallback` - covered only through GlfwEventBridge window-size events; raw callback setter is not public.
- `glfwSetWindowSizeLimits` - missing.
- `glfwSetWindowSize` - covered by SetWindowSize.
- `glfwSetWindowTitle` - missing.
- `glfwSetWindowUserPointer` - missing.
- `glfwShowWindow` - missing.
- `glfwSwapBuffers` - covered by SwapBuffers.
- `glfwSwapInterval` - covered by SwapInterval.
- `glfwTerminate` - covered by Library drop.
- `glfwUpdateGamepadMappings` - missing.
- `glfwVulkanSupported` - covered by VulkanSupported.
- `glfwWaitEventsTimeout` - covered by WaitEventsTimeout.
- `glfwWaitEvents` - covered by WaitEvents.
- `glfwWindowHintString` - missing.
- `glfwWindowHint` - partially covered by SetWindowHint/SetWindowHintBool and typed helpers for a small hint subset.
- `glfwWindowShouldClose` - covered by WindowShouldClose.

### Complete Native-Access Function Inventory

These functions are in upstream `glfw3native.h`. They are conditionally
declared by platform macros such as `GLFW_EXPOSE_NATIVE_WIN32`,
`GLFW_EXPOSE_NATIVE_COCOA`, `GLFW_EXPOSE_NATIVE_X11`,
`GLFW_EXPOSE_NATIVE_WAYLAND`, `GLFW_EXPOSE_NATIVE_WGL`,
`GLFW_EXPOSE_NATIVE_NSGL`, `GLFW_EXPOSE_NATIVE_GLX`,
`GLFW_EXPOSE_NATIVE_EGL`, and `GLFW_EXPOSE_NATIVE_OSMESA`. Stark does not bind
or package this header today.

- `glfwGetCocoaMonitor` - missing.
- `glfwGetCocoaView` - missing.
- `glfwGetCocoaWindow` - missing.
- `glfwGetEGLContext` - missing.
- `glfwGetEGLDisplay` - missing.
- `glfwGetEGLSurface` - missing.
- `glfwGetGLXContext` - missing.
- `glfwGetGLXWindow` - missing.
- `glfwGetNSGLContext` - missing.
- `glfwGetOSMesaColorBuffer` - missing.
- `glfwGetOSMesaContext` - missing.
- `glfwGetOSMesaDepthBuffer` - missing.
- `glfwGetWGLContext` - missing.
- `glfwGetWaylandDisplay` - missing.
- `glfwGetWaylandMonitor` - missing.
- `glfwGetWaylandWindow` - missing.
- `glfwGetWin32Adapter` - missing.
- `glfwGetWin32Monitor` - missing.
- `glfwGetWin32Window` - missing.
- `glfwGetX11Adapter` - missing.
- `glfwGetX11Display` - missing.
- `glfwGetX11Monitor` - missing.
- `glfwGetX11SelectionString` - missing.
- `glfwGetX11Window` - missing.
- `glfwSetX11SelectionString` - missing.

### Complete Public Macro Inventory

These are the public `GLFW_*` macros from `glfw3.h`. Statuses describe whether the current Stark API exposes the same concept, even if it does not expose the original C macro spelling.

- `GLFW_ACCUM_ALPHA_BITS` - missing.
- `GLFW_ACCUM_BLUE_BITS` - missing.
- `GLFW_ACCUM_GREEN_BITS` - missing.
- `GLFW_ACCUM_RED_BITS` - missing.
- `GLFW_ALPHA_BITS` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_D3D11` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_D3D9` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_METAL` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_NONE` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_OPENGLES` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_OPENGL` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE_VULKAN` - missing.
- `GLFW_ANGLE_PLATFORM_TYPE` - missing.
- `GLFW_ANY_PLATFORM` - missing.
- `GLFW_ANY_POSITION` - missing.
- `GLFW_ANY_RELEASE_BEHAVIOR` - missing.
- `GLFW_API_UNAVAILABLE` - covered by GlfwError.ApiUnavailable.
- `GLFW_ARROW_CURSOR` - missing.
- `GLFW_AUTO_ICONIFY` - covered by WindowHint.AutoIconify.
- `GLFW_AUX_BUFFERS` - missing.
- `GLFW_BLUE_BITS` - missing.
- `GLFW_CENTER_CURSOR` - covered by WindowHint.CenterCursor.
- `GLFW_CLIENT_API` - covered by WindowHint.ClientApi.
- `GLFW_COCOA_CHDIR_RESOURCES` - missing.
- `GLFW_COCOA_FRAME_NAME` - missing.
- `GLFW_COCOA_GRAPHICS_SWITCHING` - missing.
- `GLFW_COCOA_MENUBAR` - missing.
- `GLFW_COCOA_RETINA_FRAMEBUFFER` - missing.
- `GLFW_CONNECTED` - missing.
- `GLFW_CONTEXT_CREATION_API` - missing.
- `GLFW_CONTEXT_DEBUG` - missing.
- `GLFW_CONTEXT_NO_ERROR` - missing.
- `GLFW_CONTEXT_RELEASE_BEHAVIOR` - missing.
- `GLFW_CONTEXT_REVISION` - missing.
- `GLFW_CONTEXT_ROBUSTNESS` - missing.
- `GLFW_CONTEXT_VERSION_MAJOR` - covered by WindowHint.ContextVersionMajor.
- `GLFW_CONTEXT_VERSION_MINOR` - covered by WindowHint.ContextVersionMinor.
- `GLFW_CROSSHAIR_CURSOR` - missing.
- `GLFW_CURSOR_CAPTURED` - missing.
- `GLFW_CURSOR_DISABLED` - missing.
- `GLFW_CURSOR_HIDDEN` - missing.
- `GLFW_CURSOR_NORMAL` - missing.
- `GLFW_CURSOR_UNAVAILABLE` - covered by GlfwError.CursorUnavailable.
- `GLFW_CURSOR` - missing.
- `GLFW_DECORATED` - covered by WindowHint.Decorated.
- `GLFW_DEPTH_BITS` - missing.
- `GLFW_DISCONNECTED` - missing.
- `GLFW_DONT_CARE` - missing.
- `GLFW_DOUBLEBUFFER` - missing.
- `GLFW_EGL_CONTEXT_API` - missing.
- `GLFW_FALSE` - covered internally and through bool wrappers.
- `GLFW_FEATURE_UNAVAILABLE` - covered by GlfwError.FeatureUnavailable.
- `GLFW_FEATURE_UNIMPLEMENTED` - covered by GlfwError.FeatureUnimplemented.
- `GLFW_FLOATING` - covered by WindowHint.Floating.
- `GLFW_FOCUSED` - covered by WindowHint.Focused.
- `GLFW_FOCUS_ON_SHOW` - covered by WindowHint.FocusOnShow.
- `GLFW_FORMAT_UNAVAILABLE` - covered by GlfwError.FormatUnavailable.
- `GLFW_GAMEPAD_AXIS_LAST` - missing.
- `GLFW_GAMEPAD_AXIS_LEFT_TRIGGER` - missing.
- `GLFW_GAMEPAD_AXIS_LEFT_X` - missing.
- `GLFW_GAMEPAD_AXIS_LEFT_Y` - missing.
- `GLFW_GAMEPAD_AXIS_RIGHT_TRIGGER` - missing.
- `GLFW_GAMEPAD_AXIS_RIGHT_X` - missing.
- `GLFW_GAMEPAD_AXIS_RIGHT_Y` - missing.
- `GLFW_GAMEPAD_BUTTON_A` - missing.
- `GLFW_GAMEPAD_BUTTON_BACK` - missing.
- `GLFW_GAMEPAD_BUTTON_B` - missing.
- `GLFW_GAMEPAD_BUTTON_CIRCLE` - missing.
- `GLFW_GAMEPAD_BUTTON_CROSS` - missing.
- `GLFW_GAMEPAD_BUTTON_DPAD_DOWN` - missing.
- `GLFW_GAMEPAD_BUTTON_DPAD_LEFT` - missing.
- `GLFW_GAMEPAD_BUTTON_DPAD_RIGHT` - missing.
- `GLFW_GAMEPAD_BUTTON_DPAD_UP` - missing.
- `GLFW_GAMEPAD_BUTTON_GUIDE` - missing.
- `GLFW_GAMEPAD_BUTTON_LAST` - missing.
- `GLFW_GAMEPAD_BUTTON_LEFT_BUMPER` - missing.
- `GLFW_GAMEPAD_BUTTON_LEFT_THUMB` - missing.
- `GLFW_GAMEPAD_BUTTON_RIGHT_BUMPER` - missing.
- `GLFW_GAMEPAD_BUTTON_RIGHT_THUMB` - missing.
- `GLFW_GAMEPAD_BUTTON_SQUARE` - missing.
- `GLFW_GAMEPAD_BUTTON_START` - missing.
- `GLFW_GAMEPAD_BUTTON_TRIANGLE` - missing.
- `GLFW_GAMEPAD_BUTTON_X` - missing.
- `GLFW_GAMEPAD_BUTTON_Y` - missing.
- `GLFW_GREEN_BITS` - missing.
- `GLFW_HAND_CURSOR` - missing.
- `GLFW_HAT_CENTERED` - missing.
- `GLFW_HAT_DOWN` - missing.
- `GLFW_HAT_LEFT_DOWN` - missing.
- `GLFW_HAT_LEFT_UP` - missing.
- `GLFW_HAT_LEFT` - missing.
- `GLFW_HAT_RIGHT_DOWN` - missing.
- `GLFW_HAT_RIGHT_UP` - missing.
- `GLFW_HAT_RIGHT` - missing.
- `GLFW_HAT_UP` - missing.
- `GLFW_HOVERED` - missing.
- `GLFW_HRESIZE_CURSOR` - missing.
- `GLFW_IBEAM_CURSOR` - missing.
- `GLFW_ICONIFIED` - missing.
- `GLFW_INVALID_ENUM` - covered by GlfwError.InvalidEnum.
- `GLFW_INVALID_VALUE` - covered by GlfwError.InvalidValue.
- `GLFW_JOYSTICK_10` - missing.
- `GLFW_JOYSTICK_11` - missing.
- `GLFW_JOYSTICK_12` - missing.
- `GLFW_JOYSTICK_13` - missing.
- `GLFW_JOYSTICK_14` - missing.
- `GLFW_JOYSTICK_15` - missing.
- `GLFW_JOYSTICK_16` - missing.
- `GLFW_JOYSTICK_1` - missing.
- `GLFW_JOYSTICK_2` - missing.
- `GLFW_JOYSTICK_3` - missing.
- `GLFW_JOYSTICK_4` - missing.
- `GLFW_JOYSTICK_5` - missing.
- `GLFW_JOYSTICK_6` - missing.
- `GLFW_JOYSTICK_7` - missing.
- `GLFW_JOYSTICK_8` - missing.
- `GLFW_JOYSTICK_9` - missing.
- `GLFW_JOYSTICK_HAT_BUTTONS` - missing.
- `GLFW_JOYSTICK_LAST` - missing.
- `GLFW_KEY_0` - missing.
- `GLFW_KEY_1` - missing.
- `GLFW_KEY_2` - missing.
- `GLFW_KEY_3` - missing.
- `GLFW_KEY_4` - missing.
- `GLFW_KEY_5` - missing.
- `GLFW_KEY_6` - missing.
- `GLFW_KEY_7` - missing.
- `GLFW_KEY_8` - missing.
- `GLFW_KEY_9` - missing.
- `GLFW_KEY_APOSTROPHE` - missing.
- `GLFW_KEY_A` - covered by KeyA.
- `GLFW_KEY_BACKSLASH` - missing.
- `GLFW_KEY_BACKSPACE` - missing.
- `GLFW_KEY_B` - missing.
- `GLFW_KEY_CAPS_LOCK` - missing.
- `GLFW_KEY_COMMA` - missing.
- `GLFW_KEY_C` - missing.
- `GLFW_KEY_DELETE` - missing.
- `GLFW_KEY_DOWN` - covered by KeyDown.
- `GLFW_KEY_D` - covered by KeyD.
- `GLFW_KEY_END` - missing.
- `GLFW_KEY_ENTER` - covered by KeyEnter.
- `GLFW_KEY_EQUAL` - missing.
- `GLFW_KEY_ESCAPE` - covered by KeyEscape.
- `GLFW_KEY_E` - missing.
- `GLFW_KEY_F10` - missing.
- `GLFW_KEY_F11` - missing.
- `GLFW_KEY_F12` - missing.
- `GLFW_KEY_F13` - missing.
- `GLFW_KEY_F14` - missing.
- `GLFW_KEY_F15` - missing.
- `GLFW_KEY_F16` - missing.
- `GLFW_KEY_F17` - missing.
- `GLFW_KEY_F18` - missing.
- `GLFW_KEY_F19` - missing.
- `GLFW_KEY_F1` - missing.
- `GLFW_KEY_F20` - missing.
- `GLFW_KEY_F21` - missing.
- `GLFW_KEY_F22` - missing.
- `GLFW_KEY_F23` - missing.
- `GLFW_KEY_F24` - missing.
- `GLFW_KEY_F25` - missing.
- `GLFW_KEY_F2` - missing.
- `GLFW_KEY_F3` - missing.
- `GLFW_KEY_F4` - missing.
- `GLFW_KEY_F5` - missing.
- `GLFW_KEY_F6` - missing.
- `GLFW_KEY_F7` - missing.
- `GLFW_KEY_F8` - missing.
- `GLFW_KEY_F9` - missing.
- `GLFW_KEY_F` - missing.
- `GLFW_KEY_GRAVE_ACCENT` - missing.
- `GLFW_KEY_G` - missing.
- `GLFW_KEY_HOME` - missing.
- `GLFW_KEY_H` - missing.
- `GLFW_KEY_INSERT` - missing.
- `GLFW_KEY_I` - missing.
- `GLFW_KEY_J` - missing.
- `GLFW_KEY_KP_0` - missing.
- `GLFW_KEY_KP_1` - missing.
- `GLFW_KEY_KP_2` - missing.
- `GLFW_KEY_KP_3` - missing.
- `GLFW_KEY_KP_4` - missing.
- `GLFW_KEY_KP_5` - missing.
- `GLFW_KEY_KP_6` - missing.
- `GLFW_KEY_KP_7` - missing.
- `GLFW_KEY_KP_8` - missing.
- `GLFW_KEY_KP_9` - missing.
- `GLFW_KEY_KP_ADD` - missing.
- `GLFW_KEY_KP_DECIMAL` - missing.
- `GLFW_KEY_KP_DIVIDE` - missing.
- `GLFW_KEY_KP_ENTER` - missing.
- `GLFW_KEY_KP_EQUAL` - missing.
- `GLFW_KEY_KP_MULTIPLY` - missing.
- `GLFW_KEY_KP_SUBTRACT` - missing.
- `GLFW_KEY_K` - missing.
- `GLFW_KEY_LAST` - missing.
- `GLFW_KEY_LEFT_ALT` - missing.
- `GLFW_KEY_LEFT_BRACKET` - missing.
- `GLFW_KEY_LEFT_CONTROL` - missing.
- `GLFW_KEY_LEFT_SHIFT` - missing.
- `GLFW_KEY_LEFT_SUPER` - missing.
- `GLFW_KEY_LEFT` - covered by KeyLeft.
- `GLFW_KEY_L` - missing.
- `GLFW_KEY_MENU` - missing.
- `GLFW_KEY_MINUS` - missing.
- `GLFW_KEY_M` - missing.
- `GLFW_KEY_NUM_LOCK` - missing.
- `GLFW_KEY_N` - missing.
- `GLFW_KEY_O` - missing.
- `GLFW_KEY_PAGE_DOWN` - missing.
- `GLFW_KEY_PAGE_UP` - missing.
- `GLFW_KEY_PAUSE` - missing.
- `GLFW_KEY_PERIOD` - missing.
- `GLFW_KEY_PRINT_SCREEN` - missing.
- `GLFW_KEY_P` - missing.
- `GLFW_KEY_Q` - missing.
- `GLFW_KEY_RIGHT_ALT` - missing.
- `GLFW_KEY_RIGHT_BRACKET` - missing.
- `GLFW_KEY_RIGHT_CONTROL` - missing.
- `GLFW_KEY_RIGHT_SHIFT` - missing.
- `GLFW_KEY_RIGHT_SUPER` - missing.
- `GLFW_KEY_RIGHT` - covered by KeyRight.
- `GLFW_KEY_R` - missing.
- `GLFW_KEY_SCROLL_LOCK` - missing.
- `GLFW_KEY_SEMICOLON` - missing.
- `GLFW_KEY_SLASH` - missing.
- `GLFW_KEY_SPACE` - covered by KeySpace.
- `GLFW_KEY_S` - covered by KeyS.
- `GLFW_KEY_TAB` - missing.
- `GLFW_KEY_T` - missing.
- `GLFW_KEY_UNKNOWN` - missing.
- `GLFW_KEY_UP` - covered by KeyUp.
- `GLFW_KEY_U` - missing.
- `GLFW_KEY_V` - missing.
- `GLFW_KEY_WORLD_1` - missing.
- `GLFW_KEY_WORLD_2` - missing.
- `GLFW_KEY_W` - covered by KeyW.
- `GLFW_KEY_X` - missing.
- `GLFW_KEY_Y` - missing.
- `GLFW_KEY_Z` - missing.
- `GLFW_LOCK_KEY_MODS` - missing.
- `GLFW_LOSE_CONTEXT_ON_RESET` - missing.
- `GLFW_MAXIMIZED` - covered by WindowHint.Maximized.
- `GLFW_MOD_ALT` - missing.
- `GLFW_MOD_CAPS_LOCK` - missing.
- `GLFW_MOD_CONTROL` - missing.
- `GLFW_MOD_NUM_LOCK` - missing.
- `GLFW_MOD_SHIFT` - missing.
- `GLFW_MOD_SUPER` - missing.
- `GLFW_MOUSE_BUTTON_1` - covered by MouseButtonLeft value.
- `GLFW_MOUSE_BUTTON_2` - covered by MouseButtonRight value.
- `GLFW_MOUSE_BUTTON_3` - covered by MouseButtonMiddle value.
- `GLFW_MOUSE_BUTTON_4` - missing.
- `GLFW_MOUSE_BUTTON_5` - missing.
- `GLFW_MOUSE_BUTTON_6` - missing.
- `GLFW_MOUSE_BUTTON_7` - missing.
- `GLFW_MOUSE_BUTTON_8` - missing.
- `GLFW_MOUSE_BUTTON_LAST` - missing.
- `GLFW_MOUSE_BUTTON_LEFT` - covered by MouseButtonLeft.
- `GLFW_MOUSE_BUTTON_MIDDLE` - covered by MouseButtonMiddle.
- `GLFW_MOUSE_BUTTON_RIGHT` - covered by MouseButtonRight.
- `GLFW_MOUSE_PASSTHROUGH` - missing.
- `GLFW_NATIVE_CONTEXT_API` - missing.
- `GLFW_NOT_ALLOWED_CURSOR` - missing.
- `GLFW_NOT_INITIALIZED` - covered by GlfwError.NotInitialized.
- `GLFW_NO_API` - covered by ClientApi.None.
- `GLFW_NO_CURRENT_CONTEXT` - covered by GlfwError.NoCurrentContext.
- `GLFW_NO_ERROR` - covered by GlfwError.NoError.
- `GLFW_NO_RESET_NOTIFICATION` - missing.
- `GLFW_NO_ROBUSTNESS` - missing.
- `GLFW_NO_WINDOW_CONTEXT` - covered by GlfwError.NoWindowContext.
- `GLFW_OPENGL_ANY_PROFILE` - covered by OpenGLProfile.Any.
- `GLFW_OPENGL_API` - covered by ClientApi.OpenGL.
- `GLFW_OPENGL_COMPAT_PROFILE` - covered by OpenGLProfile.Compatibility.
- `GLFW_OPENGL_CORE_PROFILE` - covered by OpenGLProfile.Core.
- `GLFW_OPENGL_DEBUG_CONTEXT` - missing.
- `GLFW_OPENGL_ES_API` - covered by ClientApi.OpenGLES.
- `GLFW_OPENGL_FORWARD_COMPAT` - covered by WindowHint.OpenGLForwardCompat.
- `GLFW_OPENGL_PROFILE` - covered by WindowHint.OpenGLProfile.
- `GLFW_OSMESA_CONTEXT_API` - missing.
- `GLFW_OUT_OF_MEMORY` - covered by GlfwError.OutOfMemory.
- `GLFW_PLATFORM_COCOA` - missing.
- `GLFW_PLATFORM_ERROR` - covered by GlfwError.PlatformError.
- `GLFW_PLATFORM_NULL` - missing.
- `GLFW_PLATFORM_UNAVAILABLE` - covered by GlfwError.PlatformUnavailable.
- `GLFW_PLATFORM_WAYLAND` - missing.
- `GLFW_PLATFORM_WIN32` - missing.
- `GLFW_PLATFORM_X11` - missing.
- `GLFW_PLATFORM` - missing.
- `GLFW_POINTING_HAND_CURSOR` - missing.
- `GLFW_POSITION_X` - missing.
- `GLFW_POSITION_Y` - missing.
- `GLFW_PRESS` - covered by InputAction.Press.
- `GLFW_RAW_MOUSE_MOTION` - missing.
- `GLFW_RED_BITS` - missing.
- `GLFW_REFRESH_RATE` - missing.
- `GLFW_RELEASE_BEHAVIOR_FLUSH` - missing.
- `GLFW_RELEASE_BEHAVIOR_NONE` - missing.
- `GLFW_RELEASE` - covered by InputAction.Release.
- `GLFW_REPEAT` - covered by InputAction.Repeat.
- `GLFW_RESIZABLE` - covered by WindowHint.Resizable.
- `GLFW_RESIZE_ALL_CURSOR` - missing.
- `GLFW_RESIZE_EW_CURSOR` - missing.
- `GLFW_RESIZE_NESW_CURSOR` - missing.
- `GLFW_RESIZE_NS_CURSOR` - missing.
- `GLFW_RESIZE_NWSE_CURSOR` - missing.
- `GLFW_SAMPLES` - missing.
- `GLFW_SCALE_FRAMEBUFFER` - missing.
- `GLFW_SCALE_TO_MONITOR` - covered by WindowHint.ScaleToMonitor.
- `GLFW_SRGB_CAPABLE` - missing.
- `GLFW_STENCIL_BITS` - missing.
- `GLFW_STEREO` - missing.
- `GLFW_STICKY_KEYS` - missing.
- `GLFW_STICKY_MOUSE_BUTTONS` - missing.
- `GLFW_TRANSPARENT_FRAMEBUFFER` - covered by WindowHint.TransparentFramebuffer.
- `GLFW_TRUE` - covered internally and through bool wrappers.
- `GLFW_VERSION_MAJOR` - covered by GlfwVersionMajor.
- `GLFW_VERSION_MINOR` - covered by GlfwVersionMinor.
- `GLFW_VERSION_REVISION` - covered by GlfwVersionRevision.
- `GLFW_VERSION_UNAVAILABLE` - covered by GlfwError.VersionUnavailable.
- `GLFW_VISIBLE` - covered by WindowHint.Visible.
- `GLFW_VRESIZE_CURSOR` - missing.
- `GLFW_WAYLAND_APP_ID` - missing.
- `GLFW_WAYLAND_DISABLE_LIBDECOR` - missing.
- `GLFW_WAYLAND_LIBDECOR` - missing.
- `GLFW_WAYLAND_PREFER_LIBDECOR` - missing.
- `GLFW_WIN32_KEYBOARD_MENU` - missing.
- `GLFW_WIN32_SHOWDEFAULT` - missing.
- `GLFW_X11_CLASS_NAME` - missing.
- `GLFW_X11_INSTANCE_NAME` - missing.
- `GLFW_X11_XCB_VULKAN_SURFACE` - missing.

### Missing API Groups

- Initialization and platform selection: `glfwInitHint`, `glfwInitAllocator`, `glfwInitVulkanLoader`, `glfwGetVersionString`, `glfwSetErrorCallback`, `glfwGetPlatform`, and `glfwPlatformSupported` are missing.
- Monitors/video/gamma: every monitor enumeration, workarea, content-scale, physical-size, name, user-pointer, monitor-callback, video-mode, gamma, and gamma-ramp API is missing.
- Full window management: title get/set, icons, position, size limits, aspect ratio, frame size, content scale, opacity, iconify/restore/maximize/show/hide/focus/attention, fullscreen monitor reassignment, window attributes, and window user pointers are missing.
- Callbacks: Stark only exposes a fixed queued bridge for eight events. Raw callback setters and missing event kinds such as position, refresh, iconify, maximize, content scale, cursor enter, character input, file drop, monitor, joystick, and error callbacks are missing.
- Input/cursor: input modes, raw mouse motion, key names/scancodes, cursor setting, cursor objects, standard cursors, cursor position setting, clipboard, sticky keys/buttons, lock modifiers, and raw mouse motion constants are missing.
- Joystick/gamepad: every joystick/gamepad function, `GLFWgamepadstate`, joystick hats/buttons/axes constants, gamepad mapping update, and connection callback are missing.
- Context/proc lookup: `glfwExtensionSupported`, `glfwGetProcAddress`, timer value/frequency, and most context hints/constants are missing.
- Vulkan helpers: `glfwGetInstanceProcAddress` and `glfwGetPhysicalDevicePresentationSupport` are missing; required instance extensions are only exposed as a raw token and count.
- Native access: all `glfw3native.h` functions are missing, including Win32,
  Cocoa, X11, Wayland, WGL, NSGL, GLX, EGL, and OSMesa handles.
- Constants: most key codes, modifier masks, joystick/gamepad constants, framebuffer/context/window/input hints, cursor modes/shapes, platform constants, native context API constants, and init hints are missing from the public Stark surface.

### Binding Gaps That Need Design Attention

- GLFW has strict main-thread, callback, and pointer-lifetime rules. Safe Stark wrappers should encode or document main-thread-only usage and ensure callbacks cannot unwind into C.
- The current `GlfwEventBridge.c` uses one global fixed-size queue for all windows. That is simple, but a complete event model should support per-window queues or user data so high-rate input does not cause unrelated windows to drop events.
- The event bridge currently stores only integers and two doubles. Character input, file drop paths, monitor/joystick events, content scale, window position, and callback user data need lifetime-safe payload handling.
- Required Vulkan instance extensions should expose zero-copy indexed names or caller-owned copies, not just an opaque token. The token-only API is not enough to build a Vulkan instance in Stark without helper code.
- For performance, monitor/video-mode arrays, joystick axes/buttons/hats, gamepad state, and Vulkan extension names should expose borrowed views tied to GLFW lifetime, with optional caller-owned copy helpers where needed.
- Because GLFW is currently system-linked, tests should record the runtime/header version used by `pkg-config glfw3` and gate features that require exactly `3.4`.

### Tasks

- [ ] Add generated GLFW API inventory tests:
  - fetch or locate `GLFW/glfw3.h` for the supported API version
  - fetch or locate `GLFW/glfw3native.h` for the supported API version
  - extract the 124 public core `glfw*` functions, 25 native-access functions, and 332 public `GLFW_*` macros for `3.4`
  - extract opaque handles, value structs, callback typedefs, and allocator typedefs
  - classify each item as bound, covered by a safe wrapper, intentionally unsupported, or pending
  - fail C# tests when the supported GLFW header changes without updating this audit and checked-in inventory
  - add Stark self-hosted source tests for the public API names that exist today

- [ ] Add complete constant and enum coverage:
  - expose every key, modifier, mouse button, joystick, gamepad button/axis, error, window hint/attribute, framebuffer hint, context hint, input mode, cursor mode/shape, init hint, platform, and connection constant from `glfw3.h`
  - keep typed Stark enums for higher-level APIs while preserving exact numeric values
  - add C# constant-value tests against the `3.4` header
  - add Stark self-hosted compile tests for the exported constants/enums

- [ ] Add initialization/platform/options coverage:
  - bind `glfwInitHint`, `glfwInitAllocator`, `glfwInitVulkanLoader`, `glfwGetVersionString`, `glfwSetErrorCallback`, `glfwGetPlatform`, and `glfwPlatformSupported`
  - expose custom allocator setup with callback ABI tests before making it safe
  - expose platform selection including Null platform support
  - test version string, platform support, init hints, allocator failure paths, and error callback delivery

- [ ] Add monitor, video mode, and gamma APIs:
  - bind every monitor, video mode, gamma, gamma ramp, monitor callback, and monitor user-pointer function
  - expose `GLFWmonitor`, `GLFWvidmode`, and `GLFWgammaramp` through safe borrowed views
  - test monitor enumeration where available and Null/headless behavior where not available

- [ ] Add complete window management APIs:
  - bind title get/set, icon set, position get/set, size limits, aspect ratio, frame size, content scale, opacity, iconify, restore, maximize, show, hide, focus, request attention, monitor get/set, attributes, and user pointers
  - extend `CreateWindow` to support fullscreen monitors and shared contexts without making the common path slower
  - expose `GLFWimage` for window icons
  - test hidden, shown, fullscreen/headless-gated, resizable, fixed-size, opacity-gated, and content-scale paths

- [ ] Replace or extend the event bridge for full callback coverage:
  - support window position, size, close, refresh, focus, iconify, maximize, framebuffer size, content scale, mouse button, cursor position, cursor enter, scroll, key, char, charmods, drop, monitor, joystick, and error callbacks
  - preserve the fixed-allocation fast path and avoid heap allocation in high-rate callbacks
  - add per-window routing or user data so multiple windows do not contend on one global queue
  - test event ordering, overflow accounting, per-window routing, and no-unwind callback behavior

- [ ] Add input, cursor, and clipboard APIs:
  - bind input modes, raw mouse motion support, key names, key scancodes, cursor position set, cursor creation, standard cursors, cursor destroy/set, clipboard get/set, and cursor enter callbacks
  - expose `GLFWcursor` as an owning Stark handle
  - test sticky input, lock modifiers, raw mouse-motion availability, cursor lifetime, clipboard round-trip, and null/headless behavior

- [ ] Add joystick and gamepad APIs:
  - bind joystick presence, axes, buttons, hats, name, GUID, user pointer, gamepad detection, mapping update, gamepad name, gamepad state, and joystick callback
  - expose `GLFWgamepadstate` and borrowed views for axes/buttons/hats
  - test disconnected behavior deterministically and connected-device behavior behind platform gates

- [ ] Add OpenGL context/proc/timer APIs:
  - bind `glfwExtensionSupported`, `glfwGetProcAddress`, `glfwGetTimerValue`, and `glfwGetTimerFrequency`
  - expose all context creation/release/robustness/debug/no-error/ANGLE/native API constants
  - test proc lookup only with a current context and timer monotonicity/frequency

- [ ] Complete Vulkan helper APIs:
  - expose required instance extension names by index or borrowed slice, not only token/count
  - bind `glfwGetInstanceProcAddress` and `glfwGetPhysicalDevicePresentationSupport`
  - add allocator parameter support for `glfwCreateWindowSurface` once Vulkan allocation callbacks have Stark ABI carriers
  - test extension-name retrieval, Vulkan support false path, presentation support, and surface creation through the triangle example

- [ ] Add native-access APIs:
  - decide whether `glfw3native.h` lives in `Vendor.GLFW.Native` or platform-specific modules such as `Vendor.GLFW.Native.Win32`, `Vendor.GLFW.Native.X11`, and `Vendor.GLFW.Native.Wayland`
  - bind Win32, Cocoa, X11, Wayland, WGL, NSGL, GLX, EGL, and OSMesa native handle functions behind platform gates
  - expose native handles as explicit unsafe/raw tokens with platform-specific names
  - test compile-time availability for each platform module and runtime smoke paths only where the platform backend is present

- [ ] Update GLFW examples:
  - keep the hidden-window example
  - add monitor/video-mode summary example
  - add keyboard/text-input/cursor example
  - add clipboard example
  - add joystick/gamepad summary example with disconnected fallback
  - add fullscreen or window-mode switch example where a monitor is available
  - keep console output as simple `WriteLine(...)` calls without checking every status

- [ ] Add C# compiler/integration tests for every new GLFW surface:
  - native symbol availability for all bound functions
  - package-image native dependency metadata for `pkg-config glfw3`
  - ABI layout tests for `GLFWvidmode`, `GLFWgammaramp`, `GLFWimage`, `GLFWgamepadstate`, and `GLFWallocator` carriers
  - constant-value tests against the `3.4` header
  - platform-gated runtime tests for windows, monitors, input, callbacks, clipboard, gamepad, OpenGL, and Vulkan

- [ ] Add Stark self-hosted tests for every new GLFW surface that the current Stark test harness can express:
  - source-level API shape tests for constants, enums, structs, and functions
  - deterministic Null/headless tests for initialization, hints, version, constants, and no-device joystick paths
  - platform-gated window/input/Vulkan tests where native linking and display support are available
## `Vendor.SQLite`

### Source Of Truth

`Vendor.SQLite` is a Stark-owned safe wrapper over the SQLite C API. The current vendor package links the system `sqlite3` library through `pkg-config sqlite3` or explicit include/library directories; it does not vendor the SQLite amalgamation. The audit therefore uses SQLite `3.53.2`, matching both the current official SQLite download page and the local `pkg-config --modversion sqlite3` / `sqlite3 --version` result.

- Official download/version source: <https://www.sqlite.org/download.html>
- Official C interface function list: <https://www.sqlite.org/c3ref/funclist.html>
- Official complete C interface reference: <https://www.sqlite.org/capi3ref.html>
- Local public binding entrypoint: `vendor/src/Vendor/SQLite.stark`
- Local safe wrapper implementation: `vendor/src/Vendor/SQLite/Core.stark`
- Local raw ABI declarations: `vendor/src/Vendor/SQLite/Raw.stark`
- Local public types/constants: `vendor/src/Vendor/SQLite/Types.stark`
- Local native helper: `vendor/SQLiteTextBinding.c`
- Build script: `vendor/build-sqlite-package.sh`

The official SQLite `3.53.2` C reference inventory tracked below lists 304 public function entries, 495 constants, and 29 public object/type entries. The current Stark binding covers all 304 official SQLite function names, all 495 official constants, 4 owning handles directly (`sqlite3`, `sqlite3_stmt`, `sqlite3_blob`, and `sqlite3_backup`), owning legacy result-table, filename, allocator-byte, duplicate-value, dynamic-mutex, dynamic string-builder, and optional snapshot wrappers, a database-owned mutex view, non-owning file-object, VFS, and virtual-table module views, owned SQLite pointer type-token and client-data key wrappers, database hook callback wrappers, aggregate/window SQL function registration, collation registration/needed callbacks, auto-extension controls, aggregate context accessors, preupdate value accessors, virtual-table module registration/config/helper APIs, global logging/config/test-control helpers, global directory variable helpers, typed SQLite printf text-literal helpers, raw `va_list` printf entrypoints through `System.C.VaList`, checked database-configuration wrappers including the non-boolean `MAINDBNAME`, `LOOKASIDE`, and `FP_DIGITS` shapes, optional normalized-SQL and scan-status helpers, optional carray numeric/text/blob slice binding helpers, unsafe zero-copy borrowed byte-view wrappers for column/value blob and text reads, optional debug mutex assertion helpers, optional Win32 directory setters, SQLite version data-symbol access, a low-level `Vendor.SQLite.Raw` module with 316 public unsafe FFI declarations including 8 varargs declarations, carriers for every public object/type entry including the full 277-slot `sqlite3_api_routines` C-layout field table, and loadable-extension and auto-extension entrypoint ABI aliases, with the integer typedefs represented by native Stark integer types rather than named aliases. The missing official C surface is 0 functions, 0 constants, and 0 object/type carrier names. Remaining object-shaped work is safe construction/lifetime policy for callback tables.

The official function inventory was extracted from the SQLite function list with:

```bash
perl -ne 'while(/sqlite3_[A-Za-z0-9_]+/g){ print "$&\n" }' /tmp/sqlite-funclist.html | sort -u
```

The official object and constant inventories were extracted from the generated `capi3ref.html` index sections with:

```bash
awk '/<h2>List Of Objects/{flag=1;next}/<h2>List Of Constants/{flag=0}flag' capi3ref.html | perl -ne 'while(/\b(?:sqlite3|sqlite3_[A-Za-z0-9_]+|sqlite_[A-Za-z0-9_]+)\b/g){ print "$&\n" }' | sort -u
awk '/<h2>List Of Constants/{flag=1;next}/<h2>List Of Functions/{flag=0}flag' capi3ref.html | perl -ne 'while(/\bSQLITE_[A-Z][A-Z0-9_]+\b/g){ print "$&\n" }' | sort -u
```

### Current Stark Coverage

The public Stark API currently exposes:

- owning `Database`, `Statement`, `Blob`, `Backup`, `SQLiteTable`, `SQLiteFilename`, `SQLiteOwnedBytes`, `SQLiteOwnedValue`, `SQLiteMutex`, and `SQLiteStringBuilder` structs that close/finalize/free in `drop`, plus non-owning `SQLiteMutexView` and `SQLiteFileObjectView` wrappers for database-owned/VFS-owned objects
- primary result/status and column-type enums
- primary and extended result-code constants, version/source-control text constants, authorizer/action/access/trace/lock/conflict constants, global and database config constants, file-control/sync/shared-memory constants, IO capability constants, virtual-table constraint/operator constants, mutex constants, test-control constants, carray type constants, Win32 directory-type constants, destructor sentinel values, function-property constants, basic column type constants, limit codes, transaction-state codes, global/db/statement/scan-status counters, prepare flags, UTF text encoding constants, checkpoint/serialization flags, and the complete current `SQLITE_OPEN_*` flag set
- library version/source-id/thread-safety helpers, `sqlite3_version[]` data-symbol access, explicit initialize/OS-initialize hooks, unsafe shutdown/OS-shutdown hooks, deprecated global recover/thread-cleanup hooks, unsafe `sqlite3_temp_directory` / `sqlite3_data_directory` copy/set/clear helpers, optional Win32 directory setters, and shared-cache toggling
- compile-option, keyword, SQL-complete, string compare/glob/like, randomness, sleep, memory high-water, global release-memory, soft/hard heap-limit helpers, deprecated soft-heap-limit setter, global log config/logging, test-control probes, deprecated memory-alarm clearing, SQLite printf text-literal helpers, and SQLite allocator-backed owned byte allocation/resizing/size helpers
- dynamic mutex helpers: `AllocateMutex`, `EnterMutex`, `TryEnterMutex`, `LeaveMutex`, optional `IsMutexHeld`/`IsMutexNotHeld`, and corresponding `SQLiteMutexView` helpers for database-owned mutexes
- raw C-layout method-table carriers: `SQLite3VfsNative`, `SQLiteMemoryMethods`, `SQLiteMutexMethods`, `SQLiteIoMethods`, `SQLitePcachePage`, `SQLitePcacheMethods2`, and the 277-slot `SQLite3ApiRoutinesNative` extension API table, plus loadable-extension and auto-extension entrypoint ABI aliases, the typed `unsafe ffi(c)` callback aliases they contain, and typed unsafe global config wrappers for memory/mutex/pcache method tables, retained config callbacks, default lookaside, page-cache memory, heap memory, mmap, planner, sorter, memory-statistics, rowid-in-view, and platform tuning options
- dynamic string-builder helpers: `CreateStringBuilder`, `CreateStringBuilderForDatabase`, `StringBuilderAppend`, `StringBuilderAppendPrefix`, `StringBuilderAppendSqlTextLiteral`, `StringBuilderAppendRepeatedAsciiByte`, `StringBuilderReset`, `StringBuilderTruncate`, `StringBuilderStatus`, `StringBuilderByteLength`, `StringBuilderValue`, and `FinishStringBuilder`
- `Open`, `OpenDefault`, `OpenUtf16Ascii`, `OpenUtf16Unicode`, `OpenReadOnly`, `OpenReadWrite`, `OpenReadWriteCreate`, and `OpenInMemory`
- error-state helpers: `LastErrorCode`, `LastExtendedErrorCode`, `ErrorOffset`, `SystemErrno`, `SetExtendedResultCodes`, `SetErrorMessage`, `SetDefaultErrorMessage`, `ErrorMessage`, and `ErrorMessage16`
- database introspection/configuration helpers: `IsAutocommit`, `SetBusyTimeout`, `SetLockTimeout`, checked `SetDatabaseConfigFlag`, `SetMainDatabaseName` with retained schema-name storage, SQLite-managed and Stark-owned lookaside configuration/disable helpers, floating-point digit query/set helpers, API-only/legacy extension-loading toggles, unsafe extension loading, auto-extension registration/cancel/reset, `OverloadFunction`, `FileControlRaw`, typed file-control wrappers for lock state, data version, file/journal/VFS object views, size hints, chunk size, size limit, persistent WAL, powersafe overwrite, VFS names, temp filenames, mmap size, moved-file detection, atomic write brackets, lock timeout, external-reader checks, and cache reset, `DatabaseName`, `DatabaseFileName`, URI/default filename helpers, `CreateFilename`/`SQLiteFilename` helpers, `DatabaseReadonly`, `TransactionState`, `TableColumnMetadata`, legacy `GetTable`/`SQLiteTable` helpers, `CurrentLimit`, `SetLimit`, `HasOpenStatements`, `OpenStatementCount`, `ReleaseMemory`, `CacheFlush`, `DatabaseMutex`, `Interrupt`, `IsInterrupted`, `GlobalStatus`, `GlobalStatus32`, `DatabaseStatus`, and `DatabaseStatus32`
- virtual-table helper APIs: non-owning `SQLiteVirtualTableModuleView`, module register/unregister/drop wrappers, callback-only virtual-table declaration/config helpers, conflict policy and no-change helpers, planner collation/distinct/`IN` helpers, right-hand-side value extraction, and `IN` sequence iteration
- `Close`, `CloseStrict`, `Execute`, `Prepare`, `PrepareLegacy`, `PrepareWithFlags`, `PrepareUtf16LegacyAscii`, `PrepareUtf16Ascii`, `PrepareUtf16AsciiWithFlags`, `Finalize`, `Reset`, `ClearBindings`, and `Step`
- `CreatePointerType`, `BindNull`, `BindInt64`, `BindInt`, `BindDouble`, `BindText`, `BindText64`, `BindText16Ascii`, `BindText16Unicode`, `BindBytes`, optional `BindCArrayInt32`/`BindCArrayInt64`/`BindCArrayDouble`/`BindCArrayText`/`BindCArrayBlob` plus `_V2` variants, `BindValue`, `BindPointerNoDestructor`, destructor-backed `BindPointer<T>`, `BindZeroBlob`, `BindZeroBlob64`, `BindParameterCount`, `BindParameterIndex`, and `BindParameterName`
- `ColumnCount`, `DataCount`, `ColumnType`, `ColumnValueRaw`, `ColumnInt64`, `ColumnInt`, `ColumnDouble`, `ColumnText`, `ColumnText16`, `ColumnTextBytes`, `ColumnText16Bytes`, `ColumnTextBytesView`, `ColumnText16BytesView`, `ColumnBytes16`, declared-type/database/table/origin metadata helpers, `ColumnBlobLength`, `ColumnBlobCopy`, `ColumnBlobBytes`, `ColumnBlobView`, `ColumnName`, and `ColumnName16`
- owning `Blob` incremental I/O helpers: `OpenBlob`, `OpenBlobReadOnly`, `OpenBlobReadWrite`, `BlobByteLength`, `ReadBlob`, `WriteBlob`, `ReopenBlob`, and `CloseBlob`
- owning `Backup` helpers: `OpenBackup`, `StepBackup`, `BackupRemaining`, `BackupPageCount`, and `FinishBackup`
- serialization helpers: `SerializeDatabase`, `SerializeDatabaseWithFlags`, `DeserializeDatabase`, `DeserializeDatabaseReadOnly`, `DeserializeDatabaseFromSerialized`, `DeserializeDatabaseReadOnlyFromSerialized`, and `DeserializeDatabaseWithCapacity`
- WAL checkpoint helpers: `WalAutoCheckpoint`, `WalCheckpoint`, and `WalCheckpointWithMode`
- optional snapshot helpers: `SnapshotAvailable`, owning `SQLiteSnapshot`, `GetSnapshot`, `OpenSnapshot`, `RecoverSnapshots`, and `CompareSnapshots`, with `SQLiteStatus.NotFound` returned on SQLite builds that omit `SQLITE_ENABLE_SNAPSHOT`
- statement SQL/introspection helpers: `StatementSql`, `ExpandedSql`, optional `NormalizedSql`, optional scan-status availability/probe/reset helpers, `StatementHasDatabase`, `StatementExpired`, `TransferBindings`, `IsStatementReadOnly`, `IsStatementBusy`, `StatementExplainMode`, `SetStatementExplainMode`, and `StatementStatus`
- scalar/aggregate/window custom-function registration and collation registration/needed callbacks with typed callback aliases, callback argument extraction, callback-local aggregate context helpers, user/aux data helpers including destructor-backed aux-data slots, connection client-data helpers with typed `storeborrow mut T` destructor callbacks, context database error access, `sqlite3_value` copy/read helpers, unsafe zero-copy `sqlite3_value` byte views, pointer type-token lookup helpers, and `sqlite3_result` writers including destructor-backed pointer results
- database hook registration with typed `storeborrow mut T` callback data for busy, authorizer, trace/trace-v2/profile, progress, commit/rollback/update, autovacuum pages with optional destructor, WAL, unlock notify, and preupdate, plus preupdate old/new value/count/depth/blob-write accessors
- `LastInsertRowId`, `SetLastInsertRowId`, `Changes32`, `Changes`, `TotalChanges32`, and `TotalChanges`

The native helper exists to call `sqlite3_bind_text`/`sqlite3_bind_blob`/`sqlite3_bind_blob64`, `sqlite3_carray_bind`/`sqlite3_carray_bind_v2`, and `sqlite3_result_text`/`sqlite3_result_blob` families with `SQLITE_TRANSIENT`, to expose optional normalized-SQL, scan-status, snapshot, carray, debug mutex assertion, and Win32 directory symbols without making lean SQLite builds fail to link, to read/copy/set the SQLite global data symbols while Stark lacks imported-data FFI, and to extract `sqlite3_value**` and legacy table entries while hiding C-only pointer shapes and destructor-function sentinels from Stark source.

The C# vendor audit suite includes `SQLiteAuditInventoryMatchesRecordedCoverage`, which pins the SQLite `3.53.2` recorded inventory counts, rejects duplicate inventory entries, verifies that no official SQLite function inventory entries remain missing, and checks the complete scalar `SQLITE_*` constant inventory against `Vendor.SQLite.Types`.

### Complete SQLite Object Inventory

- `sqlite3` - covered as safe `Database` owner
- `sqlite3_api_routines` - covered as full C-layout `SQLite3ApiRoutinesNative` field table with one opaque `SQLiteExtensionApiRoutine` function-pointer slot per `sqlite3ext.h` routine entry
- `sqlite3_backup` - covered as safe `Backup` owner
- `sqlite3_blob` - covered as safe `Blob` owner
- `sqlite3_context` - covered as typed callback context carrier
- `sqlite3_data_directory` - covered by unsafe `DataDirectory`, `SetDataDirectory`, and `ClearDataDirectory` helpers through native SQLite-owned copies
- `sqlite3_file` - covered as non-owning `SQLiteFileObjectView`
- `sqlite3_filename` - covered as safe `SQLiteFilename` owner
- `sqlite3_index_info` - covered as typed virtual-table callback carrier
- `sqlite3_int64` - represented by Stark `i64`, no named public alias
- `sqlite3_io_methods` - covered as raw C-layout `SQLiteIoMethods`
- `sqlite3_mem_methods` - covered as raw C-layout `SQLiteMemoryMethods`
- `sqlite3_module` - covered as typed module carrier plus non-owning `SQLiteVirtualTableModuleView`
- `sqlite3_mutex` - covered as safe dynamic `SQLiteMutex` owner and non-owning `SQLiteMutexView`
- `sqlite3_mutex_methods` - covered as raw C-layout `SQLiteMutexMethods`
- `sqlite3_pcache` - covered as opaque `SQLite3PcacheNative`
- `sqlite3_pcache_methods2` - covered as raw C-layout `SQLitePcacheMethods2`
- `sqlite3_pcache_page` - covered as raw C-layout `SQLitePcachePage`
- `sqlite3_snapshot` - covered as optional-symbol safe `SQLiteSnapshot` owner
- `sqlite3_stmt` - covered as safe `Statement` owner
- `sqlite3_str` - covered as safe `SQLiteStringBuilder` owner
- `sqlite3_temp_directory` - covered by unsafe `TempDirectory`, `SetTempDirectory`, and `ClearTempDirectory` helpers through native SQLite-owned copies
- `sqlite3_uint64` - represented by Stark `u64`, no named public alias
- `sqlite3_value` - covered as typed SQL value carrier and owning duplicate wrapper
- `sqlite3_vfs` - covered as raw C-layout `SQLite3VfsNative` plus non-owning `SQLiteVfsView`
- `sqlite3_vtab` - covered as typed virtual-table callback carrier
- `sqlite3_vtab_cursor` - covered as typed virtual-table cursor callback carrier
- `sqlite_int64` - represented by Stark `i64`, no named public alias
- `sqlite_uint64` - represented by Stark `u64`, no named public alias

### Complete SQLite Function Inventory

- `sqlite3_aggregate_context` - covered by unsafe callback-only `AggregateContext` / `ExistingAggregateContext`
- `sqlite3_aggregate_count` - covered by unsafe callback-only `AggregateCountDeprecated`
- `sqlite3_auto_extension` - covered by `RegisterAutoExtension`
- `sqlite3_autovacuum_pages` - covered by typed `SetAutovacuumPagesNoDestructor` / `SetAutovacuumPages<T>` plus clear wrapper
- `sqlite3_backup_finish` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_backup_init` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_backup_pagecount` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_backup_remaining` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_backup_step` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_blob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_blob64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_double` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_int` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_int64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_null` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_parameter_count` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_parameter_index` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_parameter_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_pointer` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_text` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_text16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_text64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_value` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_zeroblob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_bind_zeroblob64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_bytes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_close` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_open` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_read` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_reopen` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_blob_write` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_busy_handler` - covered by typed `SetBusyHandler<T>` plus clear wrapper
- `sqlite3_busy_timeout` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_cancel_auto_extension` - covered by `CancelAutoExtension`
- `sqlite3_carray_bind` - covered by optional-symbol `BindCArrayInt32` / `BindCArrayInt64` / `BindCArrayDouble` / `BindCArrayText` / `BindCArrayBlob` slice wrappers that return `SQLiteStatus.NotFound` when the SQLite build omits carray
- `sqlite3_carray_bind_v2` - covered by optional-symbol `BindCArrayInt32V2` / `BindCArrayInt64V2` / `BindCArrayDoubleV2` / `BindCArrayTextV2` / `BindCArrayBlobV2` slice wrappers that return `SQLiteStatus.NotFound` when the SQLite build omits the v2 symbol
- `sqlite3_changes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_changes64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_clear_bindings` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_close` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_close_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_collation_needed` - covered by typed `SetCollationNeeded*` plus clear wrapper
- `sqlite3_collation_needed16` - covered by typed `SetCollationNeededUtf16*` plus clear wrapper
- `sqlite3_column_blob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_bytes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_bytes16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_count` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_database_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_database_name16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_decltype` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_decltype16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_double` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_int` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_int64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_name16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_origin_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_origin_name16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_table_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_table_name16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_text` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_text16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_type` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_column_value` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_commit_hook` - covered by typed `SetCommitHook<T>` plus clear wrapper
- `sqlite3_compileoption_get` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_compileoption_used` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_complete` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_complete16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_config` - covered by global threading-mode wrappers and `SetConfigLog` / `ClearConfigLog`
- `sqlite3_context_db_handle` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_create_collation` - covered by typed `RegisterCollation*` / `ClearCollation`
- `sqlite3_create_collation16` - covered by typed `RegisterCollationUtf16Ascii*` / `ClearCollationUtf16Ascii`
- `sqlite3_create_collation_v2` - covered by typed `RegisterCollationWithUserData<T>` with destructor callback
- `sqlite3_create_filename` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_create_function` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_create_function16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_create_function_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_create_module` - covered by `RegisterVirtualTableModuleViewNoDestructor` / `UnregisterVirtualTableModule`
- `sqlite3_create_module_v2` - covered by `RegisterVirtualTableModuleView`
- `sqlite3_create_window_function` - covered by typed `RegisterWindowFunction*`
- `sqlite3_data_count` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_database_file_object` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_cacheflush` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_config` - covered by checked boolean-flag wrappers plus dedicated `MAINDBNAME`, `LOOKASIDE`, and `FP_DIGITS` wrappers
- `sqlite3_db_filename` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_handle` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_mutex` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_readonly` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_release_memory` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_status` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_db_status64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_declare_vtab` - covered by `DeclareVirtualTable` / `DeclareVirtualTableForCallback`
- `sqlite3_deserialize` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_drop_modules` - covered by `DropAllVirtualTableModules`
- `sqlite3_enable_load_extension` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_enable_shared_cache` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_errcode` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_errmsg` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_errmsg16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_error_offset` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_errstr` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_exec` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_expanded_sql` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_expired` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_extended_errcode` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_extended_result_codes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_file_control` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_filename_database` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_filename_journal` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_filename_wal` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_finalize` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_free` - covered internally by `ExpandedSql` cleanup and `SQLiteOwnedBytes` ownership
- `sqlite3_free_filename` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_free_table` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_get_autocommit` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_get_auxdata` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_get_clientdata` - covered by `ClientData` plus owned `SQLiteClientDataKey`
- `sqlite3_get_table` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_global_recover` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_hard_heap_limit64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_initialize` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_interrupt` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_is_interrupted` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_keyword_check` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_keyword_count` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_keyword_name` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_last_insert_rowid` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_libversion` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_libversion_number` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_limit` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_load_extension` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_log` - covered by `LogMessage`
- `sqlite3_malloc` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_malloc64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_memory_alarm` - covered by deprecated `SetMemoryAlarmDeprecated` / `ClearMemoryAlarmDeprecated`
- `sqlite3_memory_highwater` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_memory_used` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mprintf` - covered by `FormatSqlTextLiteral`
- `sqlite3_msize` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mutex_alloc` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mutex_enter` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mutex_free` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mutex_held` - covered by optional-symbol `MutexHeldAvailable` / `IsMutexHeld` / `IsMutexViewHeld`, returning `SQLiteStatus.NotFound` when the SQLite build omits debug mutex assertions
- `sqlite3_mutex_leave` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_mutex_notheld` - covered by optional-symbol `MutexNotHeldAvailable` / `IsMutexNotHeld` / `IsMutexViewNotHeld`, returning `SQLiteStatus.NotFound` when the SQLite build omits debug mutex assertions
- `sqlite3_mutex_try` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_next_stmt` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_normalized_sql` - covered by optional-symbol `NormalizedSqlAvailable` / `NormalizedSql`
- `sqlite3_open` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_open16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_open_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_os_end` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_os_init` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_overload_function` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare16_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare16_v3` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_prepare_v3` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_preupdate_blobwrite` - covered by callback-only `PreupdateBlobWriteColumn`
- `sqlite3_preupdate_count` - covered by callback-only `PreupdateColumnCount`
- `sqlite3_preupdate_depth` - covered by callback-only `PreupdateDepth`
- `sqlite3_preupdate_hook` - covered by typed `SetPreupdateHook<T>` plus clear wrapper
- `sqlite3_preupdate_new` - covered by callback-only `PreupdateNewValue`
- `sqlite3_preupdate_old` - covered by callback-only `PreupdateOldValue`
- `sqlite3_profile` - covered by typed deprecated `SetProfileDeprecated<T>` plus clear wrapper
- `sqlite3_progress_handler` - covered by typed `SetProgressHandler<T>` plus clear wrapper
- `sqlite3_randomness` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_realloc` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_realloc64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_release_memory` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_reset` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_reset_auto_extension` - covered by `ResetAutoExtensions`
- `sqlite3_result_blob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_blob64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_double` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_error` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_error16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_error_code` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_error_nomem` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_error_toobig` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_int` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_int64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_null` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_pointer` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_subtype` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_text` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_text16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_text16be` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_text16le` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_text64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_value` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_zeroblob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_result_zeroblob64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_rollback_hook` - covered by typed `SetRollbackHook<T>` plus clear wrapper
- `sqlite3_serialize` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_set_authorizer` - covered by typed `SetAuthorizer<T>` plus clear wrapper
- `sqlite3_set_auxdata` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_set_clientdata` - covered by `SetClientDataNoDestructor`, typed `SetClientData<T>` with `storeborrow mut T` destructor data, and `ClearClientDataNoDestructor`
- `sqlite3_set_errmsg` - covered by `SetErrorMessage` and `SetDefaultErrorMessage`
- `sqlite3_set_last_insert_rowid` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_setlk_timeout` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_shutdown` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_sleep` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_snapshot_cmp` - covered by optional-symbol `CompareSnapshots`
- `sqlite3_snapshot_free` - covered by optional-symbol `SQLiteSnapshot` owner cleanup
- `sqlite3_snapshot_get` - covered by optional-symbol `GetSnapshot`
- `sqlite3_snapshot_open` - covered by optional-symbol `OpenSnapshot`
- `sqlite3_snapshot_recover` - covered by optional-symbol `RecoverSnapshots`
- `sqlite3_snprintf` - covered by `FormatSqlTextLiteralFixed`
- `sqlite3_soft_heap_limit` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_soft_heap_limit64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_sourceid` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_sql` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_status` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_status64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_step` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stmt_busy` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stmt_explain` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stmt_isexplain` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stmt_readonly` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stmt_scanstatus` - covered by optional-symbol typed scan-status wrappers
- `sqlite3_stmt_scanstatus_reset` - covered by optional-symbol `ResetStatementScanStatus`
- `sqlite3_stmt_scanstatus_v2` - covered by optional-symbol typed scan-status wrappers with explicit flags and `StatementScanStatusV2Available`
- `sqlite3_stmt_status` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_append` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_appendall` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_appendchar` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_appendf` - covered by `StringBuilderAppendSqlTextLiteral`
- `sqlite3_str_errcode` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_finish` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_free` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_length` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_new` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_reset` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_truncate` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_value` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_str_vappendf` - covered by raw `Vendor.SQLite.Raw` `System.C.VaList` declaration
- `sqlite3_strglob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_stricmp` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_strlike` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_strnicmp` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_system_errno` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_table_column_metadata` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_test_control` - covered by unsafe `TestControlIsInitialized` / `TestControlByteOrder`
- `sqlite3_thread_cleanup` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_threadsafe` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_total_changes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_total_changes64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_trace` - covered by typed deprecated `SetTraceDeprecated<T>` plus clear wrapper
- `sqlite3_trace_v2` - covered by typed `SetTraceHandler<T>` plus clear wrapper
- `sqlite3_transfer_bindings` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_txn_state` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_unlock_notify` - covered by typed `SetUnlockNotify<T>` plus clear wrapper
- `sqlite3_update_hook` - covered by typed `SetUpdateHook<T>` plus clear wrapper
- `sqlite3_uri_boolean` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_uri_int64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_uri_key` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_uri_parameter` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_user_data` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_blob` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_bytes` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_bytes16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_double` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_dup` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_encoding` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_free` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_frombind` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_int` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_int64` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_nochange` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_numeric_type` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_pointer` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_subtype` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_text` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_text16` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_text16be` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_text16le` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_value_type` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_version` - covered by `LibraryVersionConstant` through the native helper while Stark lacks direct imported-data FFI
- `sqlite3_vfs_find` - covered by `DefaultVfs` / `FindVfs`
- `sqlite3_vfs_register` - covered by unsafe `RegisterVfsView`
- `sqlite3_vfs_unregister` - covered by unsafe `UnregisterVfsView`
- `sqlite3_vmprintf` - covered by raw `Vendor.SQLite.Raw` `System.C.VaList` declaration
- `sqlite3_vsnprintf` - covered by raw `Vendor.SQLite.Raw` `System.C.VaList` declaration
- `sqlite3_vtab_collation` - covered by `VirtualTableConstraintCollation`
- `sqlite3_vtab_config` - covered by virtual-table config wrappers
- `sqlite3_vtab_distinct` - covered by `VirtualTableDistinctMode`
- `sqlite3_vtab_in` - covered by `VirtualTableInCanProcessAllAtOnce` / `SetVirtualTableInAllAtOnce`
- `sqlite3_vtab_in_first` - covered by `VirtualTableInFirst`
- `sqlite3_vtab_in_next` - covered by `VirtualTableInNext`
- `sqlite3_vtab_nochange` - covered by `VirtualTableNoChange`
- `sqlite3_vtab_on_conflict` - covered by `VirtualTableConflictPolicy`
- `sqlite3_vtab_rhs_value` - covered by `VirtualTableRightHandSideValue`
- `sqlite3_wal_autocheckpoint` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_wal_checkpoint` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_wal_checkpoint_v2` - covered by current `Vendor.SQLite` wrapper/native helper
- `sqlite3_wal_hook` - covered by typed `SetWalHook<T>` plus clear wrapper
- `sqlite3_win32_set_directory` - covered by optional-symbol unsafe `SetWin32DirectoryNative` plus `ClearWin32DirectoryNative`, returning `SQLiteStatus.NotFound` when unavailable
- `sqlite3_win32_set_directory16` - covered by optional-symbol `SetWin32DirectoryUtf16Ascii` / `SetWin32DirectoryUtf16Unicode` plus `ClearWin32DirectoryUtf16`, returning `SQLiteStatus.NotFound` when unavailable
- `sqlite3_win32_set_directory8` - covered by optional-symbol `SetWin32DirectoryUtf8` plus `ClearWin32DirectoryUtf8`, returning `SQLiteStatus.NotFound` when unavailable

### Complete SQLite Constant Inventory

- `SQLITE_ABORT` - covered
- `SQLITE_ABORT_ROLLBACK` - covered
- `SQLITE_ACCESS_EXISTS` - covered
- `SQLITE_ACCESS_READ` - covered
- `SQLITE_ACCESS_READWRITE` - covered
- `SQLITE_ALTER_TABLE` - covered
- `SQLITE_ANALYZE` - covered
- `SQLITE_ANY` - covered
- `SQLITE_ATTACH` - covered
- `SQLITE_AUTH` - covered
- `SQLITE_AUTH_USER` - covered
- `SQLITE_BLOB` - covered
- `SQLITE_BUSY` - covered
- `SQLITE_BUSY_RECOVERY` - covered
- `SQLITE_BUSY_SNAPSHOT` - covered
- `SQLITE_BUSY_TIMEOUT` - covered
- `SQLITE_CANTOPEN` - covered
- `SQLITE_CANTOPEN_CONVPATH` - covered
- `SQLITE_CANTOPEN_DIRTYWAL` - covered
- `SQLITE_CANTOPEN_FULLPATH` - covered
- `SQLITE_CANTOPEN_ISDIR` - covered
- `SQLITE_CANTOPEN_NOTEMPDIR` - covered
- `SQLITE_CANTOPEN_SYMLINK` - covered
- `SQLITE_CARRAY_BLOB` - covered
- `SQLITE_CARRAY_DOUBLE` - covered
- `SQLITE_CARRAY_INT32` - covered
- `SQLITE_CARRAY_INT64` - covered
- `SQLITE_CARRAY_TEXT` - covered
- `SQLITE_CHECKPOINT_FULL` - covered
- `SQLITE_CHECKPOINT_NOOP` - covered
- `SQLITE_CHECKPOINT_PASSIVE` - covered
- `SQLITE_CHECKPOINT_RESTART` - covered
- `SQLITE_CHECKPOINT_TRUNCATE` - covered
- `SQLITE_CONFIG_COVERING_INDEX_SCAN` - covered
- `SQLITE_CONFIG_GETMALLOC` - covered
- `SQLITE_CONFIG_GETMUTEX` - covered
- `SQLITE_CONFIG_GETPCACHE` - covered
- `SQLITE_CONFIG_GETPCACHE2` - covered
- `SQLITE_CONFIG_HEAP` - covered
- `SQLITE_CONFIG_LOG` - covered
- `SQLITE_CONFIG_LOOKASIDE` - covered
- `SQLITE_CONFIG_MALLOC` - covered
- `SQLITE_CONFIG_MEMDB_MAXSIZE` - covered
- `SQLITE_CONFIG_MEMSTATUS` - covered
- `SQLITE_CONFIG_MMAP_SIZE` - covered
- `SQLITE_CONFIG_MULTITHREAD` - covered
- `SQLITE_CONFIG_MUTEX` - covered
- `SQLITE_CONFIG_PAGECACHE` - covered
- `SQLITE_CONFIG_PCACHE` - covered
- `SQLITE_CONFIG_PCACHE2` - covered
- `SQLITE_CONFIG_PCACHE_HDRSZ` - covered
- `SQLITE_CONFIG_PMASZ` - covered
- `SQLITE_CONFIG_ROWID_IN_VIEW` - covered
- `SQLITE_CONFIG_SCRATCH` - covered
- `SQLITE_CONFIG_SERIALIZED` - covered
- `SQLITE_CONFIG_SINGLETHREAD` - covered
- `SQLITE_CONFIG_SMALL_MALLOC` - covered
- `SQLITE_CONFIG_SORTERREF_SIZE` - covered
- `SQLITE_CONFIG_SQLLOG` - covered
- `SQLITE_CONFIG_STMTJRNL_SPILL` - covered
- `SQLITE_CONFIG_URI` - covered
- `SQLITE_CONFIG_WIN32_HEAPSIZE` - covered
- `SQLITE_CONSTRAINT` - covered
- `SQLITE_CONSTRAINT_CHECK` - covered
- `SQLITE_CONSTRAINT_COMMITHOOK` - covered
- `SQLITE_CONSTRAINT_DATATYPE` - covered
- `SQLITE_CONSTRAINT_FOREIGNKEY` - covered
- `SQLITE_CONSTRAINT_FUNCTION` - covered
- `SQLITE_CONSTRAINT_NOTNULL` - covered
- `SQLITE_CONSTRAINT_PINNED` - covered
- `SQLITE_CONSTRAINT_PRIMARYKEY` - covered
- `SQLITE_CONSTRAINT_ROWID` - covered
- `SQLITE_CONSTRAINT_TRIGGER` - covered
- `SQLITE_CONSTRAINT_UNIQUE` - covered
- `SQLITE_CONSTRAINT_VTAB` - covered
- `SQLITE_COPY` - covered
- `SQLITE_CORRUPT` - covered
- `SQLITE_CORRUPT_INDEX` - covered
- `SQLITE_CORRUPT_SEQUENCE` - covered
- `SQLITE_CORRUPT_VTAB` - covered
- `SQLITE_CREATE_INDEX` - covered
- `SQLITE_CREATE_TABLE` - covered
- `SQLITE_CREATE_TEMP_INDEX` - covered
- `SQLITE_CREATE_TEMP_TABLE` - covered
- `SQLITE_CREATE_TEMP_TRIGGER` - covered
- `SQLITE_CREATE_TEMP_VIEW` - covered
- `SQLITE_CREATE_TRIGGER` - covered
- `SQLITE_CREATE_VIEW` - covered
- `SQLITE_CREATE_VTABLE` - covered
- `SQLITE_DBCONFIG_DEFENSIVE` - covered
- `SQLITE_DBCONFIG_DQS_DDL` - covered
- `SQLITE_DBCONFIG_DQS_DML` - covered
- `SQLITE_DBCONFIG_ENABLE_ATTACH_CREATE` - covered
- `SQLITE_DBCONFIG_ENABLE_ATTACH_WRITE` - covered
- `SQLITE_DBCONFIG_ENABLE_COMMENTS` - covered
- `SQLITE_DBCONFIG_ENABLE_FKEY` - covered
- `SQLITE_DBCONFIG_ENABLE_FTS3_TOKENIZER` - covered
- `SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION` - covered
- `SQLITE_DBCONFIG_ENABLE_QPSG` - covered
- `SQLITE_DBCONFIG_ENABLE_TRIGGER` - covered
- `SQLITE_DBCONFIG_ENABLE_VIEW` - covered
- `SQLITE_DBCONFIG_FP_DIGITS` - covered
- `SQLITE_DBCONFIG_LEGACY_ALTER_TABLE` - covered
- `SQLITE_DBCONFIG_LEGACY_FILE_FORMAT` - covered
- `SQLITE_DBCONFIG_LOOKASIDE` - covered
- `SQLITE_DBCONFIG_MAINDBNAME` - covered
- `SQLITE_DBCONFIG_MAX` - covered
- `SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE` - covered
- `SQLITE_DBCONFIG_RESET_DATABASE` - covered
- `SQLITE_DBCONFIG_REVERSE_SCANORDER` - covered
- `SQLITE_DBCONFIG_STMT_SCANSTATUS` - covered
- `SQLITE_DBCONFIG_TRIGGER_EQP` - covered
- `SQLITE_DBCONFIG_TRUSTED_SCHEMA` - covered
- `SQLITE_DBCONFIG_WRITABLE_SCHEMA` - covered
- `SQLITE_DBSTATUS` - covered by complete `SQLITE_DBSTATUS_*` family; header keyword only
- `SQLITE_DBSTATUS_CACHE_HIT` - covered
- `SQLITE_DBSTATUS_CACHE_MISS` - covered
- `SQLITE_DBSTATUS_CACHE_SPILL` - covered
- `SQLITE_DBSTATUS_CACHE_USED` - covered
- `SQLITE_DBSTATUS_CACHE_USED_SHARED` - covered
- `SQLITE_DBSTATUS_CACHE_WRITE` - covered
- `SQLITE_DBSTATUS_DEFERRED_FKS` - covered
- `SQLITE_DBSTATUS_LOOKASIDE_HIT` - covered
- `SQLITE_DBSTATUS_LOOKASIDE_MISS_FULL` - covered
- `SQLITE_DBSTATUS_LOOKASIDE_MISS_SIZE` - covered
- `SQLITE_DBSTATUS_LOOKASIDE_USED` - covered
- `SQLITE_DBSTATUS_MAX` - covered
- `SQLITE_DBSTATUS_SCHEMA_USED` - covered
- `SQLITE_DBSTATUS_STMT_USED` - covered
- `SQLITE_DBSTATUS_TEMPBUF_SPILL` - covered
- `SQLITE_DELETE` - covered
- `SQLITE_DENY` - covered
- `SQLITE_DESERIALIZE_FREEONCLOSE` - covered
- `SQLITE_DESERIALIZE_READONLY` - covered
- `SQLITE_DESERIALIZE_RESIZEABLE` - covered
- `SQLITE_DETACH` - covered
- `SQLITE_DETERMINISTIC` - covered
- `SQLITE_DIRECTONLY` - covered
- `SQLITE_DONE` - covered
- `SQLITE_DROP_INDEX` - covered
- `SQLITE_DROP_TABLE` - covered
- `SQLITE_DROP_TEMP_INDEX` - covered
- `SQLITE_DROP_TEMP_TABLE` - covered
- `SQLITE_DROP_TEMP_TRIGGER` - covered
- `SQLITE_DROP_TEMP_VIEW` - covered
- `SQLITE_DROP_TRIGGER` - covered
- `SQLITE_DROP_VIEW` - covered
- `SQLITE_DROP_VTABLE` - covered
- `SQLITE_EMPTY` - covered
- `SQLITE_ERROR` - covered
- `SQLITE_ERROR_KEY` - covered
- `SQLITE_ERROR_MISSING_COLLSEQ` - covered
- `SQLITE_ERROR_RESERVESIZE` - covered
- `SQLITE_ERROR_RETRY` - covered
- `SQLITE_ERROR_SNAPSHOT` - covered
- `SQLITE_ERROR_UNABLE` - covered
- `SQLITE_FAIL` - covered
- `SQLITE_FCNTL_BEGIN_ATOMIC_WRITE` - covered
- `SQLITE_FCNTL_BLOCK_ON_CONNECT` - covered
- `SQLITE_FCNTL_BUSYHANDLER` - covered
- `SQLITE_FCNTL_CHUNK_SIZE` - covered
- `SQLITE_FCNTL_CKPT_DONE` - covered
- `SQLITE_FCNTL_CKPT_START` - covered
- `SQLITE_FCNTL_CKSM_FILE` - covered
- `SQLITE_FCNTL_COMMIT_ATOMIC_WRITE` - covered
- `SQLITE_FCNTL_COMMIT_PHASETWO` - covered
- `SQLITE_FCNTL_DATA_VERSION` - covered
- `SQLITE_FCNTL_EXTERNAL_READER` - covered
- `SQLITE_FCNTL_FILESTAT` - covered
- `SQLITE_FCNTL_FILE_POINTER` - covered
- `SQLITE_FCNTL_GET_LOCKPROXYFILE` - covered
- `SQLITE_FCNTL_HAS_MOVED` - covered
- `SQLITE_FCNTL_JOURNAL_POINTER` - covered
- `SQLITE_FCNTL_LAST_ERRNO` - covered
- `SQLITE_FCNTL_LOCKSTATE` - covered
- `SQLITE_FCNTL_LOCK_TIMEOUT` - covered
- `SQLITE_FCNTL_MMAP_SIZE` - covered
- `SQLITE_FCNTL_NULL_IO` - covered
- `SQLITE_FCNTL_OVERWRITE` - covered
- `SQLITE_FCNTL_PDB` - covered
- `SQLITE_FCNTL_PERSIST_WAL` - covered
- `SQLITE_FCNTL_POWERSAFE_OVERWRITE` - covered
- `SQLITE_FCNTL_PRAGMA` - covered
- `SQLITE_FCNTL_RBU` - covered
- `SQLITE_FCNTL_RESERVE_BYTES` - covered
- `SQLITE_FCNTL_RESET_CACHE` - covered
- `SQLITE_FCNTL_ROLLBACK_ATOMIC_WRITE` - covered
- `SQLITE_FCNTL_SET_LOCKPROXYFILE` - covered
- `SQLITE_FCNTL_SIZE_HINT` - covered
- `SQLITE_FCNTL_SIZE_LIMIT` - covered
- `SQLITE_FCNTL_SYNC` - covered
- `SQLITE_FCNTL_SYNC_OMITTED` - covered
- `SQLITE_FCNTL_TEMPFILENAME` - covered
- `SQLITE_FCNTL_TRACE` - covered
- `SQLITE_FCNTL_VFSNAME` - covered
- `SQLITE_FCNTL_VFS_POINTER` - covered
- `SQLITE_FCNTL_WAL_BLOCK` - covered
- `SQLITE_FCNTL_WIN32_AV_RETRY` - covered
- `SQLITE_FCNTL_WIN32_GET_HANDLE` - covered
- `SQLITE_FCNTL_WIN32_SET_HANDLE` - covered
- `SQLITE_FCNTL_ZIPVFS` - covered
- `SQLITE_FLOAT` - covered
- `SQLITE_FORMAT` - covered
- `SQLITE_FULL` - covered
- `SQLITE_FUNCTION` - covered
- `SQLITE_IGNORE` - covered
- `SQLITE_INDEX_CONSTRAINT_EQ` - covered
- `SQLITE_INDEX_CONSTRAINT_FUNCTION` - covered
- `SQLITE_INDEX_CONSTRAINT_GE` - covered
- `SQLITE_INDEX_CONSTRAINT_GLOB` - covered
- `SQLITE_INDEX_CONSTRAINT_GT` - covered
- `SQLITE_INDEX_CONSTRAINT_IS` - covered
- `SQLITE_INDEX_CONSTRAINT_ISNOT` - covered
- `SQLITE_INDEX_CONSTRAINT_ISNOTNULL` - covered
- `SQLITE_INDEX_CONSTRAINT_ISNULL` - covered
- `SQLITE_INDEX_CONSTRAINT_LE` - covered
- `SQLITE_INDEX_CONSTRAINT_LIKE` - covered
- `SQLITE_INDEX_CONSTRAINT_LIMIT` - covered
- `SQLITE_INDEX_CONSTRAINT_LT` - covered
- `SQLITE_INDEX_CONSTRAINT_MATCH` - covered
- `SQLITE_INDEX_CONSTRAINT_NE` - covered
- `SQLITE_INDEX_CONSTRAINT_OFFSET` - covered
- `SQLITE_INDEX_CONSTRAINT_REGEXP` - covered
- `SQLITE_INDEX_SCAN_HEX` - covered
- `SQLITE_INDEX_SCAN_UNIQUE` - covered
- `SQLITE_INNOCUOUS` - covered
- `SQLITE_INSERT` - covered
- `SQLITE_INTEGER` - covered
- `SQLITE_INTERNAL` - covered
- `SQLITE_INTERRUPT` - covered
- `SQLITE_IOCAP_ATOMIC` - covered
- `SQLITE_IOCAP_ATOMIC16K` - covered
- `SQLITE_IOCAP_ATOMIC1K` - covered
- `SQLITE_IOCAP_ATOMIC2K` - covered
- `SQLITE_IOCAP_ATOMIC32K` - covered
- `SQLITE_IOCAP_ATOMIC4K` - covered
- `SQLITE_IOCAP_ATOMIC512` - covered
- `SQLITE_IOCAP_ATOMIC64K` - covered
- `SQLITE_IOCAP_ATOMIC8K` - covered
- `SQLITE_IOCAP_BATCH_ATOMIC` - covered
- `SQLITE_IOCAP_IMMUTABLE` - covered
- `SQLITE_IOCAP_POWERSAFE_OVERWRITE` - covered
- `SQLITE_IOCAP_SAFE_APPEND` - covered
- `SQLITE_IOCAP_SEQUENTIAL` - covered
- `SQLITE_IOCAP_SUBPAGE_READ` - covered
- `SQLITE_IOCAP_UNDELETABLE_WHEN_OPEN` - covered
- `SQLITE_IOERR` - covered
- `SQLITE_IOERR_ACCESS` - covered
- `SQLITE_IOERR_AUTH` - covered
- `SQLITE_IOERR_BADKEY` - covered
- `SQLITE_IOERR_BEGIN_ATOMIC` - covered
- `SQLITE_IOERR_BLOCKED` - covered
- `SQLITE_IOERR_CHECKRESERVEDLOCK` - covered
- `SQLITE_IOERR_CLOSE` - covered
- `SQLITE_IOERR_CODEC` - covered
- `SQLITE_IOERR_COMMIT_ATOMIC` - covered
- `SQLITE_IOERR_CONVPATH` - covered
- `SQLITE_IOERR_CORRUPTFS` - covered
- `SQLITE_IOERR_DATA` - covered
- `SQLITE_IOERR_DELETE` - covered
- `SQLITE_IOERR_DELETE_NOENT` - covered
- `SQLITE_IOERR_DIR_CLOSE` - covered
- `SQLITE_IOERR_DIR_FSYNC` - covered
- `SQLITE_IOERR_FSTAT` - covered
- `SQLITE_IOERR_FSYNC` - covered
- `SQLITE_IOERR_GETTEMPPATH` - covered
- `SQLITE_IOERR_IN_PAGE` - covered
- `SQLITE_IOERR_LOCK` - covered
- `SQLITE_IOERR_MMAP` - covered
- `SQLITE_IOERR_NOMEM` - covered
- `SQLITE_IOERR_RDLOCK` - covered
- `SQLITE_IOERR_READ` - covered
- `SQLITE_IOERR_ROLLBACK_ATOMIC` - covered
- `SQLITE_IOERR_SEEK` - covered
- `SQLITE_IOERR_SHMLOCK` - covered
- `SQLITE_IOERR_SHMMAP` - covered
- `SQLITE_IOERR_SHMOPEN` - covered
- `SQLITE_IOERR_SHMSIZE` - covered
- `SQLITE_IOERR_SHORT_READ` - covered
- `SQLITE_IOERR_TRUNCATE` - covered
- `SQLITE_IOERR_UNLOCK` - covered
- `SQLITE_IOERR_VNODE` - covered
- `SQLITE_IOERR_WRITE` - covered
- `SQLITE_LIMIT_ATTACHED` - covered
- `SQLITE_LIMIT_COLUMN` - covered
- `SQLITE_LIMIT_COMPOUND_SELECT` - covered
- `SQLITE_LIMIT_EXPR_DEPTH` - covered
- `SQLITE_LIMIT_FUNCTION_ARG` - covered
- `SQLITE_LIMIT_LENGTH` - covered
- `SQLITE_LIMIT_LIKE_PATTERN_LENGTH` - covered
- `SQLITE_LIMIT_PARSER_DEPTH` - covered
- `SQLITE_LIMIT_SQL_LENGTH` - covered
- `SQLITE_LIMIT_TRIGGER_DEPTH` - covered
- `SQLITE_LIMIT_VARIABLE_NUMBER` - covered
- `SQLITE_LIMIT_VDBE_OP` - covered
- `SQLITE_LIMIT_WORKER_THREADS` - covered
- `SQLITE_LOCKED` - covered
- `SQLITE_LOCKED_SHAREDCACHE` - covered
- `SQLITE_LOCKED_VTAB` - covered
- `SQLITE_LOCK_EXCLUSIVE` - covered
- `SQLITE_LOCK_NONE` - covered
- `SQLITE_LOCK_PENDING` - covered
- `SQLITE_LOCK_RESERVED` - covered
- `SQLITE_LOCK_SHARED` - covered
- `SQLITE_MISMATCH` - covered
- `SQLITE_MISUSE` - covered
- `SQLITE_MUTEX_FAST` - covered
- `SQLITE_MUTEX_RECURSIVE` - covered
- `SQLITE_MUTEX_STATIC_APP1` - covered
- `SQLITE_MUTEX_STATIC_APP2` - covered
- `SQLITE_MUTEX_STATIC_APP3` - covered
- `SQLITE_MUTEX_STATIC_LRU` - covered
- `SQLITE_MUTEX_STATIC_LRU2` - covered
- `SQLITE_MUTEX_STATIC_MAIN` - covered
- `SQLITE_MUTEX_STATIC_MEM` - covered
- `SQLITE_MUTEX_STATIC_MEM2` - covered
- `SQLITE_MUTEX_STATIC_OPEN` - covered
- `SQLITE_MUTEX_STATIC_PMEM` - covered
- `SQLITE_MUTEX_STATIC_PRNG` - covered
- `SQLITE_MUTEX_STATIC_VFS1` - covered
- `SQLITE_MUTEX_STATIC_VFS2` - covered
- `SQLITE_MUTEX_STATIC_VFS3` - covered
- `SQLITE_NOLFS` - covered
- `SQLITE_NOMEM` - covered
- `SQLITE_NOTADB` - covered
- `SQLITE_NOTFOUND` - covered
- `SQLITE_NOTICE` - covered
- `SQLITE_NOTICE_RBU` - covered
- `SQLITE_NOTICE_RECOVER_ROLLBACK` - covered
- `SQLITE_NOTICE_RECOVER_WAL` - covered
- `SQLITE_NULL` - covered
- `SQLITE_OK` - covered
- `SQLITE_OK_LOAD_PERMANENTLY` - covered
- `SQLITE_OK_SYMLINK` - covered
- `SQLITE_OPEN_AUTOPROXY` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_CREATE` - covered
- `SQLITE_OPEN_DELETEONCLOSE` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_EXCLUSIVE` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_EXRESCODE` - covered
- `SQLITE_OPEN_FULLMUTEX` - covered
- `SQLITE_OPEN_MAIN_DB` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_MAIN_JOURNAL` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_MEMORY` - covered
- `SQLITE_OPEN_NOFOLLOW` - covered
- `SQLITE_OPEN_NOMUTEX` - covered
- `SQLITE_OPEN_PRIVATECACHE` - covered
- `SQLITE_OPEN_READONLY` - covered
- `SQLITE_OPEN_READWRITE` - covered
- `SQLITE_OPEN_SHAREDCACHE` - covered
- `SQLITE_OPEN_SUBJOURNAL` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_SUPER_JOURNAL` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_TEMP_DB` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_TEMP_JOURNAL` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_TRANSIENT_DB` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_OPEN_URI` - covered
- `SQLITE_OPEN_WAL` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_PERM` - covered
- `SQLITE_PRAGMA` - covered
- `SQLITE_PREPARE_DONT_LOG` - covered
- `SQLITE_PREPARE_FROM_DDL` - covered
- `SQLITE_PREPARE_NORMALIZE` - covered
- `SQLITE_PREPARE_NO_VTAB` - covered
- `SQLITE_PREPARE_PERSISTENT` - covered
- `SQLITE_PROTOCOL` - covered
- `SQLITE_RANGE` - covered
- `SQLITE_READ` - covered
- `SQLITE_READONLY` - covered
- `SQLITE_READONLY_CANTINIT` - covered
- `SQLITE_READONLY_CANTLOCK` - covered
- `SQLITE_READONLY_DBMOVED` - covered
- `SQLITE_READONLY_DIRECTORY` - covered
- `SQLITE_READONLY_RECOVERY` - covered
- `SQLITE_READONLY_ROLLBACK` - covered
- `SQLITE_RECURSIVE` - covered
- `SQLITE_REINDEX` - covered
- `SQLITE_REPLACE` - covered
- `SQLITE_RESULT_SUBTYPE` - covered
- `SQLITE_ROLLBACK` - covered
- `SQLITE_ROW` - covered
- `SQLITE_SAVEPOINT` - covered
- `SQLITE_SCANSTAT_COMPLEX` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_EST` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_EXPLAIN` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_NAME` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_NCYCLE` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_NLOOP` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_NVISIT` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_PARENTID` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCANSTAT_SELECTID` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_SCHEMA` - covered
- `SQLITE_SCM_BRANCH` - covered
- `SQLITE_SCM_DATETIME` - covered
- `SQLITE_SCM_TAGS` - covered
- `SQLITE_SELECT` - covered
- `SQLITE_SELFORDER1` - covered
- `SQLITE_SERIALIZE_NOCOPY` - covered
- `SQLITE_SETLK_BLOCK_ON_CONNECT` - covered
- `SQLITE_SHM_EXCLUSIVE` - covered
- `SQLITE_SHM_LOCK` - covered
- `SQLITE_SHM_NLOCK` - covered
- `SQLITE_SHM_SHARED` - covered
- `SQLITE_SHM_UNLOCK` - covered
- `SQLITE_SOURCE_ID` - covered
- `SQLITE_STATIC` - covered
- `SQLITE_STATUS_MALLOC_COUNT` - covered
- `SQLITE_STATUS_MALLOC_SIZE` - covered
- `SQLITE_STATUS_MEMORY_USED` - covered
- `SQLITE_STATUS_PAGECACHE_OVERFLOW` - covered
- `SQLITE_STATUS_PAGECACHE_SIZE` - covered
- `SQLITE_STATUS_PAGECACHE_USED` - covered
- `SQLITE_STATUS_PARSER_STACK` - covered
- `SQLITE_STATUS_SCRATCH_OVERFLOW` - covered
- `SQLITE_STATUS_SCRATCH_SIZE` - covered
- `SQLITE_STATUS_SCRATCH_USED` - covered
- `SQLITE_STMTSTATUS` - covered by complete `SQLITE_STMTSTATUS_*` family; header keyword only
- `SQLITE_STMTSTATUS_AUTOINDEX` - covered
- `SQLITE_STMTSTATUS_FILTER_HIT` - covered
- `SQLITE_STMTSTATUS_FILTER_MISS` - covered
- `SQLITE_STMTSTATUS_FULLSCAN_STEP` - covered
- `SQLITE_STMTSTATUS_MEMUSED` - covered
- `SQLITE_STMTSTATUS_REPREPARE` - covered
- `SQLITE_STMTSTATUS_RUN` - covered
- `SQLITE_STMTSTATUS_SORT` - covered
- `SQLITE_STMTSTATUS_VM_STEP` - covered
- `SQLITE_SUBTYPE` - covered
- `SQLITE_SYNC_DATAONLY` - covered
- `SQLITE_SYNC_FULL` - covered
- `SQLITE_SYNC_NORMAL` - covered
- `SQLITE_TESTCTRL_ALWAYS` - covered
- `SQLITE_TESTCTRL_ASSERT` - covered
- `SQLITE_TESTCTRL_ATOF` - covered
- `SQLITE_TESTCTRL_BENIGN_MALLOC_HOOKS` - covered
- `SQLITE_TESTCTRL_BITVEC_TEST` - covered
- `SQLITE_TESTCTRL_BYTEORDER` - covered
- `SQLITE_TESTCTRL_EXPLAIN_STMT` - covered
- `SQLITE_TESTCTRL_EXTRA_SCHEMA_CHECKS` - covered
- `SQLITE_TESTCTRL_FAULT_INSTALL` - covered
- `SQLITE_TESTCTRL_FIRST` - covered
- `SQLITE_TESTCTRL_FK_NO_ACTION` - covered
- `SQLITE_TESTCTRL_GETOPT` - covered
- `SQLITE_TESTCTRL_IMPOSTER` - covered
- `SQLITE_TESTCTRL_INTERNAL_FUNCTIONS` - covered
- `SQLITE_TESTCTRL_ISINIT` - covered
- `SQLITE_TESTCTRL_ISKEYWORD` - covered
- `SQLITE_TESTCTRL_JSON_SELFCHECK` - covered
- `SQLITE_TESTCTRL_LAST` - covered
- `SQLITE_TESTCTRL_LOCALTIME_FAULT` - covered
- `SQLITE_TESTCTRL_LOGEST` - covered
- `SQLITE_TESTCTRL_NEVER_CORRUPT` - covered
- `SQLITE_TESTCTRL_ONCE_RESET_THRESHOLD` - covered
- `SQLITE_TESTCTRL_OPTIMIZATIONS` - covered
- `SQLITE_TESTCTRL_PARSER_COVERAGE` - covered
- `SQLITE_TESTCTRL_PENDING_BYTE` - covered
- `SQLITE_TESTCTRL_PRNG_RESET` - covered
- `SQLITE_TESTCTRL_PRNG_RESTORE` - covered
- `SQLITE_TESTCTRL_PRNG_SAVE` - covered
- `SQLITE_TESTCTRL_PRNG_SEED` - covered
- `SQLITE_TESTCTRL_RESERVE` - covered
- `SQLITE_TESTCTRL_RESULT_INTREAL` - covered
- `SQLITE_TESTCTRL_SCRATCHMALLOC` - covered
- `SQLITE_TESTCTRL_SEEK_COUNT` - covered
- `SQLITE_TESTCTRL_SORTER_MMAP` - covered
- `SQLITE_TESTCTRL_TRACEFLAGS` - covered
- `SQLITE_TESTCTRL_TUNE` - covered
- `SQLITE_TESTCTRL_USELONGDOUBLE` - covered
- `SQLITE_TESTCTRL_VDBE_COVERAGE` - covered
- `SQLITE_TEXT` - covered
- `SQLITE_TOOBIG` - covered
- `SQLITE_TRACE` - covered by complete `SQLITE_TRACE_*` family; header keyword only
- `SQLITE_TRACE_CLOSE` - covered
- `SQLITE_TRACE_PROFILE` - covered
- `SQLITE_TRACE_ROW` - covered
- `SQLITE_TRACE_STMT` - covered
- `SQLITE_TRANSACTION` - covered
- `SQLITE_TRANSIENT` - covered
- `SQLITE_TXN_NONE` - covered
- `SQLITE_TXN_READ` - covered
- `SQLITE_TXN_WRITE` - covered
- `SQLITE_UPDATE` - covered
- `SQLITE_UTF16` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_UTF16BE` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_UTF16LE` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_UTF16_ALIGNED` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_UTF8` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_UTF8_ZT` - covered by current `Vendor.SQLite` wrapper/native helper
- `SQLITE_VERSION` - covered
- `SQLITE_VERSION_NUMBER` - covered
- `SQLITE_VTAB_CONSTRAINT_SUPPORT` - covered
- `SQLITE_VTAB_DIRECTONLY` - covered
- `SQLITE_VTAB_INNOCUOUS` - covered
- `SQLITE_VTAB_USES_ALL_SCHEMAS` - covered
- `SQLITE_WARNING` - covered
- `SQLITE_WARNING_AUTOINDEX` - covered
- `SQLITE_WIN32_DATA_DIRECTORY_TYPE` - covered
- `SQLITE_WIN32_TEMP_DIRECTORY_TYPE` - covered

### Missing API Groups

- Connection lifecycle and configuration: covered except for intentionally raw/internal VFS-only file-control opcodes whose arguments are VFS-private or SQLite-internal. Boolean and non-boolean database configuration shapes are covered by checked wrappers.
- Statement preparation and introspection: covered, including optional-symbol normalized SQL and scan-status wrappers that report `SQLITE_NOTFOUND` on lean SQLite builds.
- Binding and column value coverage: owned byte-copy wrappers and unsafe zero-copy borrowed byte views for column/value text/blob reads are covered through `SQLiteOwnedBytes` and `SQLiteByteView`; fully safe borrowed views remain pending until callback and statement-step lifetimes can be expressed in source. No-destructor and destructor-backed pointer binding/result/aux-data paths, transient numeric/text/blob carray slice binding, `sqlite3_value_*` copy/read helpers, and `sqlite3_result_*` writers are covered.
- Custom SQL extension points: loadable-extension and auto-extension entrypoint ABI aliases over the full `sqlite3_api_routines` field table are covered; Stark-authored extension packaging, export naming, and callback lifetime policy remain pending. Scalar/aggregate/window function registration, collation registration/needed callbacks, aggregate context, user data, aux data, result APIs, value APIs, overload placeholders, auto-extension controls, direct extension loading, authorizer, and database hooks are covered.
- Virtual tables and VFS: Stark-authored `sqlite3_module` method-table construction, virtual-table implementation examples/tests, and safe VFS construction policy. Non-owning `sqlite3_file`, `sqlite3_vfs`, and virtual-table module views plus VFS lookup/register/unregister, raw `sqlite3_vfs`/`sqlite3_io_methods`, and virtual-table helper wrappers are covered; safe method-table construction remains future virtual-table/VFS work.
- Memory, mutex, and page-cache customization: raw custom allocator, mutex, and pcache method-table carriers plus unsafe global config accessors are covered. Typed wrappers now cover the global performance knobs: memory-statistics and small-malloc hints, default lookaside, SQLite-managed or retained aligned page-cache memory, retained aligned heap memory, URI/default planner toggles, mmap sizing, page-cache header-size readback, PMA size, statement-journal spill threshold, sorter-reference threshold, memory-database max size, rowid-in-view query/set, Win32 heap size, config log, and optional SQLLOG. Higher-level safe registration/lifetime helpers for custom allocator, mutex, and pcache method tables are still pending and should stay explicit `unsafe` unless Stark can prove callback and allocator lifetime safety. The basic SQLite allocator functions are covered through `SQLiteOwnedBytes`, dynamic/database mutex objects are covered through `SQLiteMutex`/`SQLiteMutexView`, optional debug mutex assertions return `SQLiteStatus.NotFound` on non-debug SQLite builds, `sqlite3_memory_alarm` is covered as deprecated compatibility, and dynamic string-builder APIs are covered.
- Introspection/utilities: `sqlite3_vmprintf`, `sqlite3_vsnprintf`, and `sqlite3_str_vappendf` are covered in `Vendor.SQLite.Raw` through `System.C.VaList`. `sqlite3_version[]` is covered through the native helper; direct imported-data FFI can replace that helper later.
- Constants: complete. `SQLITE_STATIC` is exposed as the zero destructor-sentinel value. `SQLITE_DBSTATUS`, `SQLITE_STMTSTATUS`, and `SQLITE_TRACE` are official reference-index keywords rather than scalar `#define` macros in `sqlite3.h`; Stark covers them through the complete exported `SQLITE_DBSTATUS_*`, `SQLITE_STMTSTATUS_*`, and `SQLITE_TRACE_*` families.

### Design Notes

- The existing safe API is useful and should stay; full coverage should be additive, likely split into safe convenience wrappers and a lower-level `Raw`/`C` submodule where the C API contract is necessarily exposed.
- `Vendor.SQLite.Raw` is now that lower-level ABI module. It owns the callable `sqlite3_*` and `stark_sqlite_*` unsafe FFI declarations that back `Vendor.SQLite.Core`, re-exports the raw carrier types from `Vendor.SQLite.Types`, and is imported by `Vendor.SQLite.Core`. Destructor-only free functions used by owner `drop` blocks remain internal to `Vendor.SQLite.Types` to avoid a module cycle. The safe `Vendor.SQLite` root does not re-export `Vendor.SQLite.Raw`; callers have to opt into the raw boundary explicitly.
- Scalar, aggregate, window, collation SQL callbacks, database hooks, auto-extension callbacks, loadable-extension entrypoints, and virtual-table helper callbacks now use Stark `fnptr<unsafe ffi(c)>` values directly with typed retained callback data or typed ABI aliases where SQLite accepts standalone callbacks. Remaining callback-heavy SQLite surfaces still need explicit lifetime/unwind policy and, where C expects function-table layouts, may require generated static carrier tables. This affects Stark-authored virtual-table modules, custom VFS implementations, memory/mutex/pcache methods, and extension packaging.
- `SQLiteExtensionErrorMessagePointer` names SQLite's `char*` error-message slot value; `SQLiteLoadExtensionEntry` spells the ABI as `rawmutptr<SQLiteExtensionErrorMessagePointer>` because Stark permits raw pointer-to-pointer shapes at FFI boundaries but rejects a standalone public `rawmutptr<rawmutptr<T>>` alias.
- `SQLiteByteView` is intentionally unsafe for pointer access and copying. SQLite owns the memory and invalidates column views on the next relevant `Step`, `Reset`, `Finalize`, or conversion of the same column; value views are callback-scoped. The view removes hot-path allocations without pretending those lifetimes are safe yet. `ColumnTextBytes`, `ColumnText16Bytes`, `ColumnBlobBytes`, `ValueTextBytes`, and `ValueText16Bytes` provide stable owned copies for callers that need byte-correct SQLite text/blob data without carrying a borrowed SQLite lifetime.
- Text convenience wrappers still return `ascii`/`OwnedAscii`; byte-oriented UTF-8/UTF-16 SQLite text can use the owned byte-copy wrappers or unsafe byte views instead of forcing callers through ASCII.
- SQLite examples now cover simple in-memory queries, prepared statement reuse with task-report output, scalar-function and collation callbacks, byte-safe text/blob reads, and optional snapshot/WAL behavior. The remaining virtual-table example belongs with Stark-authored virtual-table module support, because the binding has raw helper coverage but no safe method-table construction policy yet.
- Stark self-hosted source tests now cover SQLite constant parity, C-layout method/extension carriers, the loadable-extension entrypoint alias, major owner/view struct shapes, result enum OK/ERR payload shapes, representative callback ABI aliases, representative `Vendor.SQLite.Raw` ABI function items, and 493 public wrapper function items, including overlap-contract wrappers, generic `storeborrow mut T` wrappers, and callback-parameter wrappers.
- The current `sqlite3_bind_text`/`sqlite3_bind_blob` helper is acceptable while Stark cannot spell `SQLITE_TRANSIENT` as a destructor sentinel directly. If Stark gains a safe way to model that sentinel, this helper can disappear.
- Performance-sensitive wrappers should avoid per-call allocations for hot statement loops. Current blob writes can use caller-owned buffers through incremental blob I/O; borrowed zero-copy column slices remain future work until the statement-step lifetime can be expressed explicitly.

### Tasks

- [ ] Complete safe lifetime-checked borrowed value/column views:
  - replace or supplement the current unsafe `SQLiteByteView` byte accessors with safe borrowed blob/text views once statement-step and callback lifetimes can be expressed in source
  - test null handling, conversion failures, and borrowed read-view lifetime rules

- [ ] Complete Stark-authored loadable-extension packaging and callback policy:
  - define how a Stark library exports the SQLite entrypoint symbol, including `sqlite3_extension_init` naming and any `[LinkName]`/export restrictions
  - implement required callback lifetime/unwind policy and generated non-allocating C carrier tables where SQLite expects struct-of-function-pointer layouts
  - test extension loading through SQLite, exported-symbol gates, and callback lifetime diagnostics

- [ ] Add Stark-authored virtual table and custom VFS implementation support:
  - implement function-pointer table layout support or generated static C shim tables for Stark-defined virtual-table modules and VFS implementations
  - add safe construction policy for `sqlite3_module`, `sqlite3_vtab`, `sqlite3_vtab_cursor`, and `sqlite3_index_info` callback lifetimes without exposing raw pointers outside the callback edge
  - add safe construction policy for the existing raw `sqlite3_vfs`/`sqlite3_io_methods` carriers and remaining database-file object helpers
  - test a minimal virtual table, constraint pushdown, `IN` handling, vtab config paths, URI parameters, and a read-only custom VFS smoke path

- [ ] Add safe custom allocator, mutex, and pcache method-table lifetime helpers:
  - design safe or explicitly unsafe lifetime helpers around the existing custom allocator, mutex, and pcache method-table config accessors; non-method-table global memory/page-cache tuning wrappers are already covered
  - test custom allocator tables, custom mutex method tables, pcache method tables, and config ordering before/after initialization

- [ ] Add remaining introspection, utility, and compatibility APIs:
  - replace the `sqlite3_version[]` helper with direct imported-data FFI if/when the language gains it, and keep deprecated compatibility entries clearly marked
  - test compatibility table helpers, deprecated API behavior, and constant parity against `sqlite3.h`

Test requirements for future SQLite work:

- Add C# compiler/integration coverage for package-image native dependency metadata, native symbol availability by API group, ABI layout for every new public carrier and callback table, runtime parity where the installed SQLite supports the feature, and hot prepare/bind/step plus zero-copy column-read performance regressions.
- Add Stark self-hosted coverage for source-level API shape tests, example compile tests for safe wrapper usage, and native/runtime tests as the self-hosted compiler gains those test capabilities.

## `Vendor.SDL3`

### Source Of Truth

`Vendor.SDL3` is a Stark-owned safe wrapper over core SDL3. The current vendor package links the system `sdl3` library through `pkg-config sdl3` or explicit include/library directories, and includes only `<SDL3/SDL.h>` in `vendor/Sdl3Binding.c`.

- Official SDL3 public symbol index: <https://wiki.libsdl.org/SDL3/CategoryAPI>
- Official SDL releases: <https://github.com/libsdl-org/SDL/releases>
- Local SDL headers: `/usr/include/SDL3/SDL*.h`
- Local public binding: `vendor/src/Vendor/SDL3.stark`
- Local native bridge: `vendor/Sdl3Binding.c`
- Build script: `vendor/build-sdl3-package.sh`

The local development headers and library report SDL `3.4.10`, and the upstream releases page lists `3.4.10` as the latest stable release. The SDL wiki `CategoryAPI` page states that it is a list of every public symbol in SDL. For this audit, the generated public inventory is taken from that wiki index and cross-checked against the installed `3.4.10` headers. The wiki currently lists 1,321 functions, 1,329 macros, 137 datatypes, 124 structs, and 97 enum types. The installed headers expose 1,276 linkable `SDL_DECLSPEC`/`SDLMAIN_DECLSPEC` functions; 1,274 overlap with the wiki function list, 47 wiki-listed functions are inline/helper/future/extension-style symbols rather than linkable functions in these local headers, and 2 local runtime thread entrypoints are declared in the headers but not listed in the wiki function index.

The public symbol lists were extracted from the wiki with:

```bash
perl -0ne '
while(/<h2 class="anchorText" id="([^"]+)">.*?<!-- BEGIN CATEGORY LIST:.*?-->(.*?)<!-- END CATEGORY LIST -->/sg){
  my ($section,$body)=($1,$2);
  while($body =~ /<li><a href="[^"]+">([^<]+)<\/a><\/li>/g){ print "$section:$1\n"; }
}
' /tmp/sdl3-categoryapi.html
```

The local linkable function list was extracted from headers with:

```bash
perl -0777 -ne 'while(/(?:extern\s+)?(?:SDLMAIN_DECLSPEC|SDL_DECLSPEC)\s+[^;]*?\bSDLCALL\s+(SDL_[A-Za-z0-9_]+)\s*\(/sg){ print "$1\n" }' /usr/include/SDL3/SDL*.h | sort -u
```

### Current Stark Coverage

The public Stark API currently exposes:

- initialization owner `Library`, `Initialize`, `WasInitialized`, and `GetVersion`
- app metadata setting
- owning `Window`, `Renderer`, and `AudioStream` wrappers
- hidden/general window creation, show/hide, set/get size
- default/named renderer creation, draw color, clear, and present
- event pump, poll, wait-with-timeout, and push-quit helper
- a flattened `SdlEvent` record for selected window, keyboard, mouse, wheel, and audio-device events
- `AudioSpec`, default playback stream opening, byte put/get, available byte count, flush, clear, pause, resume, and paused query
- a small set of renamed Stark constants for init flags, window flags, key values, mouse buttons, audio formats, and default audio devices

The current native bridge calls 31 SDL functions. It exposes 6 SDL datatypes through safe owners or raw opaque carriers, partially/fully covers 2 structs, partially covers 2 enum types by hardcoded selected values, and covers 39 wiki-listed macros through renamed Stark constants or internal native use. It does not expose a raw SDL3 module with original SDL symbol names.

### Complete SDL3 Function Inventory

- `SDL_AcquireCameraFrame` - missing
- `SDL_AcquireGPUCommandBuffer` - missing
- `SDL_AcquireGPUSwapchainTexture` - missing
- `SDL_AddAtomicInt` - missing
- `SDL_AddAtomicU32` - missing
- `SDL_AddEventWatch` - missing
- `SDL_AddGamepadMapping` - missing
- `SDL_AddGamepadMappingsFromFile` - missing
- `SDL_AddGamepadMappingsFromIO` - missing
- `SDL_AddHintCallback` - missing
- `SDL_AddSurfaceAlternateImage` - missing
- `SDL_AddTimer` - missing
- `SDL_AddTimerNS` - missing
- `SDL_AddVulkanRenderSemaphores` - missing
- `SDL_AppEvent` - missing
- `SDL_AppInit` - missing
- `SDL_AppIterate` - missing
- `SDL_AppQuit` - missing
- `SDL_AsyncIOFromFile` - missing
- `SDL_AttachVirtualJoystick` - missing
- `SDL_AudioDevicePaused` - missing
- `SDL_AudioStreamDevicePaused` - covered by current safe wrapper/native bridge
- `SDL_BeginGPUComputePass` - missing
- `SDL_BeginGPUCopyPass` - missing
- `SDL_BeginGPURenderPass` - missing
- `SDL_BindAudioStream` - missing
- `SDL_BindAudioStreams` - missing
- `SDL_BindGPUComputePipeline` - missing
- `SDL_BindGPUComputeSamplers` - missing
- `SDL_BindGPUComputeStorageBuffers` - missing
- `SDL_BindGPUComputeStorageTextures` - missing
- `SDL_BindGPUFragmentSamplers` - missing
- `SDL_BindGPUFragmentStorageBuffers` - missing
- `SDL_BindGPUFragmentStorageTextures` - missing
- `SDL_BindGPUGraphicsPipeline` - missing
- `SDL_BindGPUIndexBuffer` - missing
- `SDL_BindGPUVertexBuffers` - missing
- `SDL_BindGPUVertexSamplers` - missing
- `SDL_BindGPUVertexStorageBuffers` - missing
- `SDL_BindGPUVertexStorageTextures` - missing
- `SDL_BlitGPUTexture` - missing
- `SDL_BlitSurface` - missing
- `SDL_BlitSurface9Grid` - missing
- `SDL_BlitSurfaceScaled` - missing
- `SDL_BlitSurfaceTiled` - missing
- `SDL_BlitSurfaceTiledWithScale` - missing
- `SDL_BlitSurfaceUnchecked` - missing
- `SDL_BlitSurfaceUncheckedScaled` - missing
- `SDL_BroadcastCondition` - missing
- `SDL_CalculateGPUTextureFormatSize` - missing
- `SDL_CancelGPUCommandBuffer` - missing
- `SDL_CaptureMouse` - missing
- `SDL_ClaimWindowForGPUDevice` - missing
- `SDL_CleanupTLS` - missing
- `SDL_ClearAudioStream` - covered by current safe wrapper/native bridge
- `SDL_ClearClipboardData` - missing
- `SDL_ClearComposition` - missing
- `SDL_ClearError` - missing
- `SDL_ClearProperty` - missing
- `SDL_ClearSurface` - missing
- `SDL_ClickTrayEntry` - missing
- `SDL_CloseAsyncIO` - missing
- `SDL_CloseAudioDevice` - missing
- `SDL_CloseCamera` - missing
- `SDL_CloseGamepad` - missing
- `SDL_CloseHaptic` - missing
- `SDL_CloseIO` - missing
- `SDL_CloseJoystick` - missing
- `SDL_CloseSensor` - missing
- `SDL_CloseStorage` - missing
- `SDL_CompareAndSwapAtomicInt` - missing
- `SDL_CompareAndSwapAtomicPointer` - missing
- `SDL_CompareAndSwapAtomicU32` - missing
- `SDL_ComposeCustomBlendMode` - missing
- `SDL_ConvertAudioSamples` - missing
- `SDL_ConvertEventToRenderCoordinates` - missing
- `SDL_ConvertPixels` - missing
- `SDL_ConvertPixelsAndColorspace` - missing
- `SDL_ConvertSurface` - missing
- `SDL_ConvertSurfaceAndColorspace` - missing
- `SDL_CopyFile` - missing
- `SDL_CopyGPUBufferToBuffer` - missing
- `SDL_CopyGPUTextureToTexture` - missing
- `SDL_CopyProperties` - missing
- `SDL_CopyStorageFile` - missing
- `SDL_CreateAnimatedCursor` - missing
- `SDL_CreateAsyncIOQueue` - missing
- `SDL_CreateAudioStream` - missing
- `SDL_CreateColorCursor` - missing
- `SDL_CreateCondition` - missing
- `SDL_CreateCursor` - missing
- `SDL_CreateDirectory` - missing
- `SDL_CreateEnvironment` - missing
- `SDL_CreateGPUBuffer` - missing
- `SDL_CreateGPUComputePipeline` - missing
- `SDL_CreateGPUDevice` - missing
- `SDL_CreateGPUDeviceWithProperties` - missing
- `SDL_CreateGPUGraphicsPipeline` - missing
- `SDL_CreateGPURenderState` - missing
- `SDL_CreateGPURenderer` - missing
- `SDL_CreateGPUSampler` - missing
- `SDL_CreateGPUShader` - missing
- `SDL_CreateGPUTexture` - missing
- `SDL_CreateGPUTransferBuffer` - missing
- `SDL_CreateGPUXRSession` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_CreateGPUXRSwapchain` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_CreateHapticEffect` - missing
- `SDL_CreateMutex` - missing
- `SDL_CreatePalette` - missing
- `SDL_CreatePopupWindow` - missing
- `SDL_CreateProcess` - missing
- `SDL_CreateProcessWithProperties` - missing
- `SDL_CreateProperties` - missing
- `SDL_CreateRWLock` - missing
- `SDL_CreateRenderer` - covered by current safe wrapper/native bridge
- `SDL_CreateRendererWithProperties` - missing
- `SDL_CreateSemaphore` - missing
- `SDL_CreateSoftwareRenderer` - missing
- `SDL_CreateStorageDirectory` - missing
- `SDL_CreateSurface` - missing
- `SDL_CreateSurfaceFrom` - missing
- `SDL_CreateSurfacePalette` - missing
- `SDL_CreateSystemCursor` - missing
- `SDL_CreateTexture` - missing
- `SDL_CreateTextureFromSurface` - missing
- `SDL_CreateTextureWithProperties` - missing
- `SDL_CreateThread` - missing
- `SDL_CreateThreadWithProperties` - missing
- `SDL_CreateTray` - missing
- `SDL_CreateTrayMenu` - missing
- `SDL_CreateTraySubmenu` - missing
- `SDL_CreateTrayWithProperties` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_CreateWindow` - covered by current safe wrapper/native bridge
- `SDL_CreateWindowAndRenderer` - missing
- `SDL_CreateWindowWithProperties` - missing
- `SDL_CursorVisible` - missing
- `SDL_DateTimeToTime` - missing
- `SDL_Delay` - missing
- `SDL_DelayNS` - missing
- `SDL_DelayPrecise` - missing
- `SDL_DestroyAsyncIOQueue` - missing
- `SDL_DestroyAudioStream` - covered by current safe wrapper/native bridge
- `SDL_DestroyCondition` - missing
- `SDL_DestroyCursor` - missing
- `SDL_DestroyEnvironment` - missing
- `SDL_DestroyGPUDevice` - missing
- `SDL_DestroyGPURenderState` - missing
- `SDL_DestroyGPUXRSwapchain` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_DestroyHapticEffect` - missing
- `SDL_DestroyMutex` - missing
- `SDL_DestroyPalette` - missing
- `SDL_DestroyProcess` - missing
- `SDL_DestroyProperties` - missing
- `SDL_DestroyRWLock` - missing
- `SDL_DestroyRenderer` - covered by current safe wrapper/native bridge
- `SDL_DestroySemaphore` - missing
- `SDL_DestroySurface` - missing
- `SDL_DestroyTexture` - missing
- `SDL_DestroyTray` - missing
- `SDL_DestroyWindow` - covered by current safe wrapper/native bridge
- `SDL_DestroyWindowSurface` - missing
- `SDL_DetachThread` - missing
- `SDL_DetachVirtualJoystick` - missing
- `SDL_DisableScreenSaver` - missing
- `SDL_DispatchGPUCompute` - missing
- `SDL_DispatchGPUComputeIndirect` - missing
- `SDL_DownloadFromGPUBuffer` - missing
- `SDL_DownloadFromGPUTexture` - missing
- `SDL_DrawGPUIndexedPrimitives` - missing
- `SDL_DrawGPUIndexedPrimitivesIndirect` - missing
- `SDL_DrawGPUPrimitives` - missing
- `SDL_DrawGPUPrimitivesIndirect` - missing
- `SDL_DuplicateSurface` - missing
- `SDL_EGL_GetCurrentConfig` - missing
- `SDL_EGL_GetCurrentDisplay` - missing
- `SDL_EGL_GetProcAddress` - missing
- `SDL_EGL_GetWindowSurface` - missing
- `SDL_EGL_SetAttributeCallbacks` - missing
- `SDL_EnableScreenSaver` - missing
- `SDL_EndGPUComputePass` - missing
- `SDL_EndGPUCopyPass` - missing
- `SDL_EndGPURenderPass` - missing
- `SDL_EnterAppMainCallbacks` - missing
- `SDL_EnumerateDirectory` - missing
- `SDL_EnumerateProperties` - missing
- `SDL_EnumerateStorageDirectory` - missing
- `SDL_EventEnabled` - missing
- `SDL_FillSurfaceRect` - missing
- `SDL_FillSurfaceRects` - missing
- `SDL_FilterEvents` - missing
- `SDL_FlashWindow` - missing
- `SDL_FlipSurface` - missing
- `SDL_FlushAudioStream` - covered by current safe wrapper/native bridge
- `SDL_FlushEvent` - missing
- `SDL_FlushEvents` - missing
- `SDL_FlushIO` - missing
- `SDL_FlushRenderer` - missing
- `SDL_GDKResumeGPU` - missing
- `SDL_GDKResumeRenderer` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GDKSuspendComplete` - missing
- `SDL_GDKSuspendGPU` - missing
- `SDL_GDKSuspendRenderer` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GL_CreateContext` - missing
- `SDL_GL_DestroyContext` - missing
- `SDL_GL_ExtensionSupported` - missing
- `SDL_GL_GetAttribute` - missing
- `SDL_GL_GetCurrentContext` - missing
- `SDL_GL_GetCurrentWindow` - missing
- `SDL_GL_GetProcAddress` - missing
- `SDL_GL_GetSwapInterval` - missing
- `SDL_GL_LoadLibrary` - missing
- `SDL_GL_MakeCurrent` - missing
- `SDL_GL_ResetAttributes` - missing
- `SDL_GL_SetAttribute` - missing
- `SDL_GL_SetSwapInterval` - missing
- `SDL_GL_SwapWindow` - missing
- `SDL_GL_UnloadLibrary` - missing
- `SDL_GPUSupportsProperties` - missing
- `SDL_GPUSupportsShaderFormats` - missing
- `SDL_GPUTextureFormatTexelBlockSize` - missing
- `SDL_GPUTextureSupportsFormat` - missing
- `SDL_GPUTextureSupportsSampleCount` - missing
- `SDL_GUIDToString` - missing
- `SDL_GamepadConnected` - missing
- `SDL_GamepadEventsEnabled` - missing
- `SDL_GamepadHasAxis` - missing
- `SDL_GamepadHasButton` - missing
- `SDL_GamepadHasCapSense` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GamepadHasSensor` - missing
- `SDL_GamepadSensorEnabled` - missing
- `SDL_GenerateMipmapsForGPUTexture` - missing
- `SDL_GetAndroidActivity` - missing
- `SDL_GetAndroidCachePath` - missing
- `SDL_GetAndroidExternalStoragePath` - missing
- `SDL_GetAndroidExternalStorageState` - missing
- `SDL_GetAndroidInternalStoragePath` - missing
- `SDL_GetAndroidJNIEnv` - missing
- `SDL_GetAndroidSDKVersion` - missing
- `SDL_GetAppMetadataProperty` - missing
- `SDL_GetAssertionHandler` - missing
- `SDL_GetAssertionReport` - missing
- `SDL_GetAsyncIOResult` - missing
- `SDL_GetAsyncIOSize` - missing
- `SDL_GetAtomicInt` - missing
- `SDL_GetAtomicPointer` - missing
- `SDL_GetAtomicU32` - missing
- `SDL_GetAudioDeviceChannelMap` - missing
- `SDL_GetAudioDeviceFormat` - missing
- `SDL_GetAudioDeviceGain` - missing
- `SDL_GetAudioDeviceName` - missing
- `SDL_GetAudioDriver` - missing
- `SDL_GetAudioFormatName` - missing
- `SDL_GetAudioPlaybackDevices` - missing
- `SDL_GetAudioRecordingDevices` - missing
- `SDL_GetAudioStreamAvailable` - covered by current safe wrapper/native bridge
- `SDL_GetAudioStreamData` - covered by current safe wrapper/native bridge
- `SDL_GetAudioStreamDevice` - missing
- `SDL_GetAudioStreamFormat` - missing
- `SDL_GetAudioStreamFrequencyRatio` - missing
- `SDL_GetAudioStreamGain` - missing
- `SDL_GetAudioStreamInputChannelMap` - missing
- `SDL_GetAudioStreamOutputChannelMap` - missing
- `SDL_GetAudioStreamProperties` - missing
- `SDL_GetAudioStreamQueued` - missing
- `SDL_GetBasePath` - missing
- `SDL_GetBooleanProperty` - missing
- `SDL_GetCPUCacheLineSize` - missing
- `SDL_GetCameraDriver` - missing
- `SDL_GetCameraFormat` - missing
- `SDL_GetCameraID` - missing
- `SDL_GetCameraName` - missing
- `SDL_GetCameraPermissionState` - missing
- `SDL_GetCameraPosition` - missing
- `SDL_GetCameraProperties` - missing
- `SDL_GetCameraSupportedFormats` - missing
- `SDL_GetCameras` - missing
- `SDL_GetClipboardData` - missing
- `SDL_GetClipboardMimeTypes` - missing
- `SDL_GetClipboardText` - missing
- `SDL_GetClosestFullscreenDisplayMode` - missing
- `SDL_GetCurrentAudioDriver` - missing
- `SDL_GetCurrentCameraDriver` - missing
- `SDL_GetCurrentDirectory` - missing
- `SDL_GetCurrentDisplayMode` - missing
- `SDL_GetCurrentDisplayOrientation` - missing
- `SDL_GetCurrentRenderOutputSize` - missing
- `SDL_GetCurrentThreadID` - missing
- `SDL_GetCurrentTime` - missing
- `SDL_GetCurrentVideoDriver` - missing
- `SDL_GetCursor` - missing
- `SDL_GetDXGIOutputInfo` - missing
- `SDL_GetDateTimeLocalePreferences` - missing
- `SDL_GetDayOfWeek` - missing
- `SDL_GetDayOfYear` - missing
- `SDL_GetDaysInMonth` - missing
- `SDL_GetDefaultAssertionHandler` - missing
- `SDL_GetDefaultCursor` - missing
- `SDL_GetDefaultLogOutputFunction` - missing
- `SDL_GetDefaultTextureScaleMode` - missing
- `SDL_GetDesktopDisplayMode` - missing
- `SDL_GetDeviceFormFactor` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GetDeviceFormFactorName` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GetDirect3D9AdapterIndex` - missing
- `SDL_GetDisplayBounds` - missing
- `SDL_GetDisplayContentScale` - missing
- `SDL_GetDisplayForPoint` - missing
- `SDL_GetDisplayForRect` - missing
- `SDL_GetDisplayForWindow` - missing
- `SDL_GetDisplayName` - missing
- `SDL_GetDisplayProperties` - missing
- `SDL_GetDisplayUsableBounds` - missing
- `SDL_GetDisplays` - missing
- `SDL_GetEnvironment` - missing
- `SDL_GetEnvironmentVariable` - missing
- `SDL_GetEnvironmentVariables` - missing
- `SDL_GetError` - covered by current safe wrapper/native bridge
- `SDL_GetEventDescription` - missing
- `SDL_GetEventFilter` - missing
- `SDL_GetFloatProperty` - missing
- `SDL_GetFullscreenDisplayModes` - missing
- `SDL_GetGDKDefaultUser` - missing
- `SDL_GetGDKTaskQueue` - missing
- `SDL_GetGPUDeviceDriver` - missing
- `SDL_GetGPUDeviceProperties` - missing
- `SDL_GetGPUDriver` - missing
- `SDL_GetGPURendererDevice` - missing
- `SDL_GetGPUShaderFormats` - missing
- `SDL_GetGPUSwapchainTextureFormat` - missing
- `SDL_GetGPUTextureFormatFromPixelFormat` - missing
- `SDL_GetGPUXRSwapchainFormats` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GetGamepadAppleSFSymbolsNameForAxis` - missing
- `SDL_GetGamepadAppleSFSymbolsNameForButton` - missing
- `SDL_GetGamepadAxis` - missing
- `SDL_GetGamepadAxisFromString` - missing
- `SDL_GetGamepadBindings` - missing
- `SDL_GetGamepadButton` - missing
- `SDL_GetGamepadButtonFromString` - missing
- `SDL_GetGamepadButtonLabel` - missing
- `SDL_GetGamepadButtonLabelForType` - missing
- `SDL_GetGamepadCapSense` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_GetGamepadConnectionState` - missing
- `SDL_GetGamepadFirmwareVersion` - missing
- `SDL_GetGamepadFromID` - missing
- `SDL_GetGamepadFromPlayerIndex` - missing
- `SDL_GetGamepadGUIDForID` - missing
- `SDL_GetGamepadID` - missing
- `SDL_GetGamepadJoystick` - missing
- `SDL_GetGamepadMapping` - missing
- `SDL_GetGamepadMappingForGUID` - missing
- `SDL_GetGamepadMappingForID` - missing
- `SDL_GetGamepadMappings` - missing
- `SDL_GetGamepadName` - missing
- `SDL_GetGamepadNameForID` - missing
- `SDL_GetGamepadPath` - missing
- `SDL_GetGamepadPathForID` - missing
- `SDL_GetGamepadPlayerIndex` - missing
- `SDL_GetGamepadPlayerIndexForID` - missing
- `SDL_GetGamepadPowerInfo` - missing
- `SDL_GetGamepadProduct` - missing
- `SDL_GetGamepadProductForID` - missing
- `SDL_GetGamepadProductVersion` - missing
- `SDL_GetGamepadProductVersionForID` - missing
- `SDL_GetGamepadProperties` - missing
- `SDL_GetGamepadSensorData` - missing
- `SDL_GetGamepadSensorDataRate` - missing
- `SDL_GetGamepadSerial` - missing
- `SDL_GetGamepadSteamHandle` - missing
- `SDL_GetGamepadStringForAxis` - missing
- `SDL_GetGamepadStringForButton` - missing
- `SDL_GetGamepadStringForType` - missing
- `SDL_GetGamepadTouchpadFinger` - missing
- `SDL_GetGamepadType` - missing
- `SDL_GetGamepadTypeForID` - missing
- `SDL_GetGamepadTypeFromString` - missing
- `SDL_GetGamepadVendor` - missing
- `SDL_GetGamepadVendorForID` - missing
- `SDL_GetGamepads` - missing
- `SDL_GetGlobalMouseState` - missing
- `SDL_GetGlobalProperties` - missing
- `SDL_GetGrabbedWindow` - missing
- `SDL_GetHapticEffectStatus` - missing
- `SDL_GetHapticFeatures` - missing
- `SDL_GetHapticFromID` - missing
- `SDL_GetHapticID` - missing
- `SDL_GetHapticName` - missing
- `SDL_GetHapticNameForID` - missing
- `SDL_GetHaptics` - missing
- `SDL_GetHint` - missing
- `SDL_GetHintBoolean` - missing
- `SDL_GetIOProperties` - missing
- `SDL_GetIOSize` - missing
- `SDL_GetIOStatus` - missing
- `SDL_GetJoystickAxis` - missing
- `SDL_GetJoystickAxisInitialState` - missing
- `SDL_GetJoystickBall` - missing
- `SDL_GetJoystickButton` - missing
- `SDL_GetJoystickConnectionState` - missing
- `SDL_GetJoystickFirmwareVersion` - missing
- `SDL_GetJoystickFromID` - missing
- `SDL_GetJoystickFromPlayerIndex` - missing
- `SDL_GetJoystickGUID` - missing
- `SDL_GetJoystickGUIDForID` - missing
- `SDL_GetJoystickGUIDInfo` - missing
- `SDL_GetJoystickHat` - missing
- `SDL_GetJoystickID` - missing
- `SDL_GetJoystickName` - missing
- `SDL_GetJoystickNameForID` - missing
- `SDL_GetJoystickPath` - missing
- `SDL_GetJoystickPathForID` - missing
- `SDL_GetJoystickPlayerIndex` - missing
- `SDL_GetJoystickPlayerIndexForID` - missing
- `SDL_GetJoystickPowerInfo` - missing
- `SDL_GetJoystickProduct` - missing
- `SDL_GetJoystickProductForID` - missing
- `SDL_GetJoystickProductVersion` - missing
- `SDL_GetJoystickProductVersionForID` - missing
- `SDL_GetJoystickProperties` - missing
- `SDL_GetJoystickSerial` - missing
- `SDL_GetJoystickType` - missing
- `SDL_GetJoystickTypeForID` - missing
- `SDL_GetJoystickVendor` - missing
- `SDL_GetJoystickVendorForID` - missing
- `SDL_GetJoysticks` - missing
- `SDL_GetKeyFromName` - missing
- `SDL_GetKeyFromScancode` - missing
- `SDL_GetKeyName` - missing
- `SDL_GetKeyboardFocus` - missing
- `SDL_GetKeyboardNameForID` - missing
- `SDL_GetKeyboardState` - missing
- `SDL_GetKeyboards` - missing
- `SDL_GetLogOutputFunction` - missing
- `SDL_GetLogPriority` - missing
- `SDL_GetMasksForPixelFormat` - missing
- `SDL_GetMaxHapticEffects` - missing
- `SDL_GetMaxHapticEffectsPlaying` - missing
- `SDL_GetMemoryFunctions` - missing
- `SDL_GetMice` - missing
- `SDL_GetModState` - missing
- `SDL_GetMouseFocus` - missing
- `SDL_GetMouseNameForID` - missing
- `SDL_GetMouseState` - missing
- `SDL_GetNaturalDisplayOrientation` - missing
- `SDL_GetNumAllocations` - missing
- `SDL_GetNumAudioDrivers` - missing
- `SDL_GetNumCameraDrivers` - missing
- `SDL_GetNumGPUDrivers` - missing
- `SDL_GetNumGamepadTouchpadFingers` - missing
- `SDL_GetNumGamepadTouchpads` - missing
- `SDL_GetNumHapticAxes` - missing
- `SDL_GetNumJoystickAxes` - missing
- `SDL_GetNumJoystickBalls` - missing
- `SDL_GetNumJoystickButtons` - missing
- `SDL_GetNumJoystickHats` - missing
- `SDL_GetNumLogicalCPUCores` - missing
- `SDL_GetNumRenderDrivers` - missing
- `SDL_GetNumVideoDrivers` - missing
- `SDL_GetNumberProperty` - missing
- `SDL_GetOriginalMemoryFunctions` - missing
- `SDL_GetPathInfo` - missing
- `SDL_GetPenDeviceType` - missing
- `SDL_GetPerformanceCounter` - missing
- `SDL_GetPerformanceFrequency` - missing
- `SDL_GetPixelFormatDetails` - missing
- `SDL_GetPixelFormatForMasks` - missing
- `SDL_GetPixelFormatFromGPUTextureFormat` - missing
- `SDL_GetPixelFormatName` - missing
- `SDL_GetPlatform` - missing
- `SDL_GetPointerProperty` - missing
- `SDL_GetPowerInfo` - missing
- `SDL_GetPrefPath` - missing
- `SDL_GetPreferredLocales` - missing
- `SDL_GetPrimaryDisplay` - missing
- `SDL_GetPrimarySelectionText` - missing
- `SDL_GetProcessInput` - missing
- `SDL_GetProcessOutput` - missing
- `SDL_GetProcessProperties` - missing
- `SDL_GetPropertyType` - missing
- `SDL_GetRGB` - missing
- `SDL_GetRGBA` - missing
- `SDL_GetRealGamepadType` - missing
- `SDL_GetRealGamepadTypeForID` - missing
- `SDL_GetRectAndLineIntersection` - missing
- `SDL_GetRectAndLineIntersectionFloat` - missing
- `SDL_GetRectEnclosingPoints` - missing
- `SDL_GetRectEnclosingPointsFloat` - missing
- `SDL_GetRectIntersection` - missing
- `SDL_GetRectIntersectionFloat` - missing
- `SDL_GetRectUnion` - missing
- `SDL_GetRectUnionFloat` - missing
- `SDL_GetRelativeMouseState` - missing
- `SDL_GetRenderClipRect` - missing
- `SDL_GetRenderColorScale` - missing
- `SDL_GetRenderDrawBlendMode` - missing
- `SDL_GetRenderDrawColor` - missing
- `SDL_GetRenderDrawColorFloat` - missing
- `SDL_GetRenderDriver` - missing
- `SDL_GetRenderLogicalPresentation` - missing
- `SDL_GetRenderLogicalPresentationRect` - missing
- `SDL_GetRenderMetalCommandEncoder` - missing
- `SDL_GetRenderMetalLayer` - missing
- `SDL_GetRenderOutputSize` - missing
- `SDL_GetRenderSafeArea` - missing
- `SDL_GetRenderScale` - missing
- `SDL_GetRenderTarget` - missing
- `SDL_GetRenderTextureAddressMode` - missing
- `SDL_GetRenderVSync` - missing
- `SDL_GetRenderViewport` - missing
- `SDL_GetRenderWindow` - missing
- `SDL_GetRenderer` - missing
- `SDL_GetRendererFromTexture` - missing
- `SDL_GetRendererName` - missing
- `SDL_GetRendererProperties` - missing
- `SDL_GetRevision` - missing
- `SDL_GetSIMDAlignment` - missing
- `SDL_GetSandbox` - missing
- `SDL_GetScancodeFromKey` - missing
- `SDL_GetScancodeFromName` - missing
- `SDL_GetScancodeName` - missing
- `SDL_GetSemaphoreValue` - missing
- `SDL_GetSensorData` - missing
- `SDL_GetSensorFromID` - missing
- `SDL_GetSensorID` - missing
- `SDL_GetSensorName` - missing
- `SDL_GetSensorNameForID` - missing
- `SDL_GetSensorNonPortableType` - missing
- `SDL_GetSensorNonPortableTypeForID` - missing
- `SDL_GetSensorProperties` - missing
- `SDL_GetSensorType` - missing
- `SDL_GetSensorTypeForID` - missing
- `SDL_GetSensors` - missing
- `SDL_GetSilenceValueForFormat` - missing
- `SDL_GetStorageFileSize` - missing
- `SDL_GetStoragePathInfo` - missing
- `SDL_GetStorageSpaceRemaining` - missing
- `SDL_GetStringProperty` - missing
- `SDL_GetSurfaceAlphaMod` - missing
- `SDL_GetSurfaceBlendMode` - missing
- `SDL_GetSurfaceClipRect` - missing
- `SDL_GetSurfaceColorKey` - missing
- `SDL_GetSurfaceColorMod` - missing
- `SDL_GetSurfaceColorspace` - missing
- `SDL_GetSurfaceImages` - missing
- `SDL_GetSurfacePalette` - missing
- `SDL_GetSurfaceProperties` - missing
- `SDL_GetSystemPageSize` - missing
- `SDL_GetSystemRAM` - missing
- `SDL_GetSystemTheme` - missing
- `SDL_GetTLS` - missing
- `SDL_GetTextInputArea` - missing
- `SDL_GetTextureAlphaMod` - missing
- `SDL_GetTextureAlphaModFloat` - missing
- `SDL_GetTextureBlendMode` - missing
- `SDL_GetTextureColorMod` - missing
- `SDL_GetTextureColorModFloat` - missing
- `SDL_GetTexturePalette` - missing
- `SDL_GetTextureProperties` - missing
- `SDL_GetTextureScaleMode` - missing
- `SDL_GetTextureSize` - missing
- `SDL_GetThreadID` - missing
- `SDL_GetThreadName` - missing
- `SDL_GetThreadState` - missing
- `SDL_GetTicks` - missing
- `SDL_GetTicksNS` - missing
- `SDL_GetTouchDeviceName` - missing
- `SDL_GetTouchDeviceType` - missing
- `SDL_GetTouchDevices` - missing
- `SDL_GetTouchFingers` - missing
- `SDL_GetTrayEntries` - missing
- `SDL_GetTrayEntryChecked` - missing
- `SDL_GetTrayEntryEnabled` - missing
- `SDL_GetTrayEntryLabel` - missing
- `SDL_GetTrayEntryParent` - missing
- `SDL_GetTrayMenu` - missing
- `SDL_GetTrayMenuParentEntry` - missing
- `SDL_GetTrayMenuParentTray` - missing
- `SDL_GetTraySubmenu` - missing
- `SDL_GetUserFolder` - missing
- `SDL_GetVersion` - covered by current safe wrapper/native bridge
- `SDL_GetVideoDriver` - missing
- `SDL_GetWindowAspectRatio` - missing
- `SDL_GetWindowBordersSize` - missing
- `SDL_GetWindowDisplayScale` - missing
- `SDL_GetWindowFlags` - missing
- `SDL_GetWindowFromEvent` - missing
- `SDL_GetWindowFromID` - missing
- `SDL_GetWindowFullscreenMode` - missing
- `SDL_GetWindowICCProfile` - missing
- `SDL_GetWindowID` - missing
- `SDL_GetWindowKeyboardGrab` - missing
- `SDL_GetWindowMaximumSize` - missing
- `SDL_GetWindowMinimumSize` - missing
- `SDL_GetWindowMouseGrab` - missing
- `SDL_GetWindowMouseRect` - missing
- `SDL_GetWindowOpacity` - missing
- `SDL_GetWindowParent` - missing
- `SDL_GetWindowPixelDensity` - missing
- `SDL_GetWindowPixelFormat` - missing
- `SDL_GetWindowPosition` - missing
- `SDL_GetWindowProgressState` - missing
- `SDL_GetWindowProgressValue` - missing
- `SDL_GetWindowProperties` - missing
- `SDL_GetWindowRelativeMouseMode` - missing
- `SDL_GetWindowSafeArea` - missing
- `SDL_GetWindowSize` - covered by current safe wrapper/native bridge
- `SDL_GetWindowSizeInPixels` - missing
- `SDL_GetWindowSurface` - missing
- `SDL_GetWindowSurfaceVSync` - missing
- `SDL_GetWindowTitle` - missing
- `SDL_GetWindows` - missing
- `SDL_GlobDirectory` - missing
- `SDL_GlobStorageDirectory` - missing
- `SDL_HapticEffectSupported` - missing
- `SDL_HapticRumbleSupported` - missing
- `SDL_HasARMSIMD` - missing
- `SDL_HasAVX` - missing
- `SDL_HasAVX2` - missing
- `SDL_HasAVX512F` - missing
- `SDL_HasAltiVec` - missing
- `SDL_HasClipboardData` - missing
- `SDL_HasClipboardText` - missing
- `SDL_HasEvent` - missing
- `SDL_HasEvents` - missing
- `SDL_HasExactlyOneBitSet32` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_HasGamepad` - missing
- `SDL_HasJoystick` - missing
- `SDL_HasKeyboard` - missing
- `SDL_HasLASX` - missing
- `SDL_HasLSX` - missing
- `SDL_HasMMX` - missing
- `SDL_HasMouse` - missing
- `SDL_HasNEON` - missing
- `SDL_HasPrimarySelectionText` - missing
- `SDL_HasProperty` - missing
- `SDL_HasRectIntersection` - missing
- `SDL_HasRectIntersectionFloat` - missing
- `SDL_HasSSE` - missing
- `SDL_HasSSE2` - missing
- `SDL_HasSSE3` - missing
- `SDL_HasSSE41` - missing
- `SDL_HasSSE42` - missing
- `SDL_HasSVE2` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_HasScreenKeyboardSupport` - missing
- `SDL_HideCursor` - missing
- `SDL_HideWindow` - covered by current safe wrapper/native bridge
- `SDL_IOFromConstMem` - missing
- `SDL_IOFromDynamicMem` - missing
- `SDL_IOFromFile` - missing
- `SDL_IOFromMem` - missing
- `SDL_IOprintf` - missing
- `SDL_IOvprintf` - missing
- `SDL_Init` - covered by current safe wrapper/native bridge
- `SDL_InitHapticRumble` - missing
- `SDL_InitSubSystem` - missing
- `SDL_InsertGPUDebugLabel` - missing
- `SDL_InsertTrayEntryAt` - missing
- `SDL_IsAudioDevicePhysical` - missing
- `SDL_IsAudioDevicePlayback` - missing
- `SDL_IsChromebook` - missing
- `SDL_IsDeXMode` - missing
- `SDL_IsGamepad` - missing
- `SDL_IsJoystickHaptic` - missing
- `SDL_IsJoystickVirtual` - missing
- `SDL_IsMainThread` - missing
- `SDL_IsMouseHaptic` - missing
- `SDL_IsPhone` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_IsTV` - missing
- `SDL_IsTablet` - missing
- `SDL_IsUbuntuTouch` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_JoystickConnected` - missing
- `SDL_JoystickEventsEnabled` - missing
- `SDL_KillProcess` - missing
- `SDL_LoadBMP` - missing
- `SDL_LoadBMP_IO` - missing
- `SDL_LoadFile` - missing
- `SDL_LoadFileAsync` - missing
- `SDL_LoadFile_IO` - missing
- `SDL_LoadFunction` - missing
- `SDL_LoadJPG` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_LoadJPG_IO` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_LoadObject` - missing
- `SDL_LoadPNG` - missing
- `SDL_LoadPNG_IO` - missing
- `SDL_LoadSurface` - missing
- `SDL_LoadSurface_IO` - missing
- `SDL_LoadWAV` - missing
- `SDL_LoadWAV_IO` - missing
- `SDL_LockAudioStream` - missing
- `SDL_LockJoysticks` - missing
- `SDL_LockMutex` - missing
- `SDL_LockProperties` - missing
- `SDL_LockRWLockForReading` - missing
- `SDL_LockRWLockForWriting` - missing
- `SDL_LockSpinlock` - missing
- `SDL_LockSurface` - missing
- `SDL_LockTexture` - missing
- `SDL_LockTextureToSurface` - missing
- `SDL_Log` - missing
- `SDL_LogCritical` - missing
- `SDL_LogDebug` - missing
- `SDL_LogError` - missing
- `SDL_LogInfo` - missing
- `SDL_LogMessage` - missing
- `SDL_LogMessageV` - missing
- `SDL_LogTrace` - missing
- `SDL_LogVerbose` - missing
- `SDL_LogWarn` - missing
- `SDL_MapGPUTransferBuffer` - missing
- `SDL_MapRGB` - missing
- `SDL_MapRGBA` - missing
- `SDL_MapSurfaceRGB` - missing
- `SDL_MapSurfaceRGBA` - missing
- `SDL_MaximizeWindow` - missing
- `SDL_MemoryBarrierAcquireFunction` - missing
- `SDL_MemoryBarrierReleaseFunction` - missing
- `SDL_Metal_CreateView` - missing
- `SDL_Metal_DestroyView` - missing
- `SDL_Metal_GetLayer` - missing
- `SDL_MinimizeWindow` - missing
- `SDL_MixAudio` - missing
- `SDL_MostSignificantBitIndex32` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_OnApplicationDidChangeStatusBarOrientation` - missing
- `SDL_OnApplicationDidEnterBackground` - missing
- `SDL_OnApplicationDidEnterForeground` - missing
- `SDL_OnApplicationDidReceiveMemoryWarning` - missing
- `SDL_OnApplicationWillEnterBackground` - missing
- `SDL_OnApplicationWillEnterForeground` - missing
- `SDL_OnApplicationWillTerminate` - missing
- `SDL_OpenAudioDevice` - missing
- `SDL_OpenAudioDeviceStream` - covered by current safe wrapper/native bridge
- `SDL_OpenCamera` - missing
- `SDL_OpenFileStorage` - missing
- `SDL_OpenGamepad` - missing
- `SDL_OpenHaptic` - missing
- `SDL_OpenHapticFromJoystick` - missing
- `SDL_OpenHapticFromMouse` - missing
- `SDL_OpenIO` - missing
- `SDL_OpenJoystick` - missing
- `SDL_OpenSensor` - missing
- `SDL_OpenStorage` - missing
- `SDL_OpenTitleStorage` - missing
- `SDL_OpenURL` - missing
- `SDL_OpenUserStorage` - missing
- `SDL_OpenXR_GetXrGetInstanceProcAddr` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_OpenXR_LoadLibrary` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_OpenXR_UnloadLibrary` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_OutOfMemory` - missing
- `SDL_PauseAudioDevice` - missing
- `SDL_PauseAudioStreamDevice` - covered by current safe wrapper/native bridge
- `SDL_PauseHaptic` - missing
- `SDL_PeepEvents` - missing
- `SDL_PlayHapticRumble` - missing
- `SDL_PointInRect` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_PointInRectFloat` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_PollEvent` - covered by current safe wrapper/native bridge
- `SDL_PopGPUDebugGroup` - missing
- `SDL_PremultiplyAlpha` - missing
- `SDL_PremultiplySurfaceAlpha` - missing
- `SDL_PumpEvents` - covered by current safe wrapper/native bridge
- `SDL_PushEvent` - covered by current safe wrapper/native bridge
- `SDL_PushGPUComputeUniformData` - missing
- `SDL_PushGPUDebugGroup` - missing
- `SDL_PushGPUFragmentUniformData` - missing
- `SDL_PushGPUVertexUniformData` - missing
- `SDL_PutAudioStreamData` - covered by current safe wrapper/native bridge
- `SDL_PutAudioStreamDataNoCopy` - missing
- `SDL_PutAudioStreamPlanarData` - missing
- `SDL_QueryGPUFence` - missing
- `SDL_Quit` - covered by current safe wrapper/native bridge
- `SDL_QuitSubSystem` - missing
- `SDL_RaiseWindow` - missing
- `SDL_ReadAsyncIO` - missing
- `SDL_ReadIO` - missing
- `SDL_ReadProcess` - missing
- `SDL_ReadS16BE` - missing
- `SDL_ReadS16LE` - missing
- `SDL_ReadS32BE` - missing
- `SDL_ReadS32LE` - missing
- `SDL_ReadS64BE` - missing
- `SDL_ReadS64LE` - missing
- `SDL_ReadS8` - missing
- `SDL_ReadStorageFile` - missing
- `SDL_ReadSurfacePixel` - missing
- `SDL_ReadSurfacePixelFloat` - missing
- `SDL_ReadU16BE` - missing
- `SDL_ReadU16LE` - missing
- `SDL_ReadU32BE` - missing
- `SDL_ReadU32LE` - missing
- `SDL_ReadU64BE` - missing
- `SDL_ReadU64LE` - missing
- `SDL_ReadU8` - missing
- `SDL_RectEmpty` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RectEmptyFloat` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RectToFRect` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RectsEqual` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RectsEqualEpsilon` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RectsEqualFloat` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RegisterApp` - missing
- `SDL_RegisterEvents` - missing
- `SDL_ReleaseCameraFrame` - missing
- `SDL_ReleaseGPUBuffer` - missing
- `SDL_ReleaseGPUComputePipeline` - missing
- `SDL_ReleaseGPUFence` - missing
- `SDL_ReleaseGPUGraphicsPipeline` - missing
- `SDL_ReleaseGPUSampler` - missing
- `SDL_ReleaseGPUShader` - missing
- `SDL_ReleaseGPUTexture` - missing
- `SDL_ReleaseGPUTransferBuffer` - missing
- `SDL_ReleaseWindowFromGPUDevice` - missing
- `SDL_ReloadGamepadMappings` - missing
- `SDL_RemoveEventWatch` - missing
- `SDL_RemoveHintCallback` - missing
- `SDL_RemoveNotification` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_RemovePath` - missing
- `SDL_RemoveStoragePath` - missing
- `SDL_RemoveSurfaceAlternateImages` - missing
- `SDL_RemoveTimer` - missing
- `SDL_RemoveTrayEntry` - missing
- `SDL_RenamePath` - missing
- `SDL_RenameStoragePath` - missing
- `SDL_RenderClear` - covered by current safe wrapper/native bridge
- `SDL_RenderClipEnabled` - missing
- `SDL_RenderCoordinatesFromWindow` - missing
- `SDL_RenderCoordinatesToWindow` - missing
- `SDL_RenderDebugText` - missing
- `SDL_RenderDebugTextFormat` - missing
- `SDL_RenderFillRect` - missing
- `SDL_RenderFillRects` - missing
- `SDL_RenderGeometry` - missing
- `SDL_RenderGeometryRaw` - missing
- `SDL_RenderLine` - missing
- `SDL_RenderLines` - missing
- `SDL_RenderPoint` - missing
- `SDL_RenderPoints` - missing
- `SDL_RenderPresent` - covered by current safe wrapper/native bridge
- `SDL_RenderReadPixels` - missing
- `SDL_RenderRect` - missing
- `SDL_RenderRects` - missing
- `SDL_RenderTexture` - missing
- `SDL_RenderTexture9Grid` - missing
- `SDL_RenderTexture9GridTiled` - missing
- `SDL_RenderTextureAffine` - missing
- `SDL_RenderTextureRotated` - missing
- `SDL_RenderTextureTiled` - missing
- `SDL_RenderViewportSet` - missing
- `SDL_ReportAssertion` - missing
- `SDL_RequestAndroidPermission` - missing
- `SDL_RequestNotificationPermission` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_ResetAssertionReport` - missing
- `SDL_ResetHint` - missing
- `SDL_ResetHints` - missing
- `SDL_ResetKeyboard` - missing
- `SDL_ResetLogPriorities` - missing
- `SDL_RestoreWindow` - missing
- `SDL_ResumeAudioDevice` - missing
- `SDL_ResumeAudioStreamDevice` - covered by current safe wrapper/native bridge
- `SDL_ResumeHaptic` - missing
- `SDL_RotateSurface` - missing
- `SDL_RumbleGamepad` - missing
- `SDL_RumbleGamepadTriggers` - missing
- `SDL_RumbleJoystick` - missing
- `SDL_RumbleJoystickTriggers` - missing
- `SDL_RunApp` - missing
- `SDL_RunHapticEffect` - missing
- `SDL_RunOnMainThread` - missing
- `SDL_SaveBMP` - missing
- `SDL_SaveBMP_IO` - missing
- `SDL_SaveFile` - missing
- `SDL_SaveFile_IO` - missing
- `SDL_SavePNG` - missing
- `SDL_SavePNG_IO` - missing
- `SDL_ScaleSurface` - missing
- `SDL_ScreenKeyboardShown` - missing
- `SDL_ScreenSaverEnabled` - missing
- `SDL_SeekIO` - missing
- `SDL_SendAndroidBackButton` - missing
- `SDL_SendAndroidMessage` - missing
- `SDL_SendGamepadEffect` - missing
- `SDL_SendJoystickEffect` - missing
- `SDL_SendJoystickVirtualSensorData` - missing
- `SDL_SetAppMetadata` - covered by current safe wrapper/native bridge
- `SDL_SetAppMetadataProperty` - missing
- `SDL_SetAssertionHandler` - missing
- `SDL_SetAtomicInt` - missing
- `SDL_SetAtomicPointer` - missing
- `SDL_SetAtomicU32` - missing
- `SDL_SetAudioDeviceGain` - missing
- `SDL_SetAudioPostmixCallback` - missing
- `SDL_SetAudioStreamFormat` - missing
- `SDL_SetAudioStreamFrequencyRatio` - missing
- `SDL_SetAudioStreamGain` - missing
- `SDL_SetAudioStreamGetCallback` - missing
- `SDL_SetAudioStreamInputChannelMap` - missing
- `SDL_SetAudioStreamOutputChannelMap` - missing
- `SDL_SetAudioStreamPutCallback` - missing
- `SDL_SetBooleanProperty` - missing
- `SDL_SetClipboardData` - missing
- `SDL_SetClipboardText` - missing
- `SDL_SetCurrentThreadPriority` - missing
- `SDL_SetCursor` - missing
- `SDL_SetDefaultTextureScaleMode` - missing
- `SDL_SetEnvironmentVariable` - missing
- `SDL_SetError` - missing
- `SDL_SetErrorV` - missing
- `SDL_SetEventEnabled` - missing
- `SDL_SetEventFilter` - missing
- `SDL_SetFloatProperty` - missing
- `SDL_SetGPUAllowedFramesInFlight` - missing
- `SDL_SetGPUBlendConstants` - missing
- `SDL_SetGPUBufferName` - missing
- `SDL_SetGPURenderState` - missing
- `SDL_SetGPURenderStateFragmentUniforms` - missing
- `SDL_SetGPURenderStateSamplerBindings` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_SetGPURenderStateStorageBuffers` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_SetGPURenderStateStorageTextures` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_SetGPUScissor` - missing
- `SDL_SetGPUStencilReference` - missing
- `SDL_SetGPUSwapchainParameters` - missing
- `SDL_SetGPUTextureName` - missing
- `SDL_SetGPUViewport` - missing
- `SDL_SetGamepadEventsEnabled` - missing
- `SDL_SetGamepadLED` - missing
- `SDL_SetGamepadMapping` - missing
- `SDL_SetGamepadPlayerIndex` - missing
- `SDL_SetGamepadSensorEnabled` - missing
- `SDL_SetHapticAutocenter` - missing
- `SDL_SetHapticGain` - missing
- `SDL_SetHint` - missing
- `SDL_SetHintWithPriority` - missing
- `SDL_SetInitialized` - missing
- `SDL_SetJoystickEventsEnabled` - missing
- `SDL_SetJoystickLED` - missing
- `SDL_SetJoystickPlayerIndex` - missing
- `SDL_SetJoystickVirtualAxis` - missing
- `SDL_SetJoystickVirtualBall` - missing
- `SDL_SetJoystickVirtualButton` - missing
- `SDL_SetJoystickVirtualHat` - missing
- `SDL_SetJoystickVirtualTouchpad` - missing
- `SDL_SetLinuxThreadPriority` - missing
- `SDL_SetLinuxThreadPriorityAndPolicy` - missing
- `SDL_SetLogOutputFunction` - missing
- `SDL_SetLogPriorities` - missing
- `SDL_SetLogPriority` - missing
- `SDL_SetLogPriorityPrefix` - missing
- `SDL_SetMainReady` - missing
- `SDL_SetMemoryFunctions` - missing
- `SDL_SetModState` - missing
- `SDL_SetNumberProperty` - missing
- `SDL_SetPaletteColors` - missing
- `SDL_SetPointerProperty` - missing
- `SDL_SetPointerPropertyWithCleanup` - missing
- `SDL_SetPrimarySelectionText` - missing
- `SDL_SetRelativeMouseTransform` - missing
- `SDL_SetRenderClipRect` - missing
- `SDL_SetRenderColorScale` - missing
- `SDL_SetRenderDrawBlendMode` - missing
- `SDL_SetRenderDrawColor` - covered by current safe wrapper/native bridge
- `SDL_SetRenderDrawColorFloat` - missing
- `SDL_SetRenderLogicalPresentation` - missing
- `SDL_SetRenderScale` - missing
- `SDL_SetRenderTarget` - missing
- `SDL_SetRenderTextureAddressMode` - missing
- `SDL_SetRenderVSync` - missing
- `SDL_SetRenderViewport` - missing
- `SDL_SetScancodeName` - missing
- `SDL_SetStringProperty` - missing
- `SDL_SetSurfaceAlphaMod` - missing
- `SDL_SetSurfaceBlendMode` - missing
- `SDL_SetSurfaceClipRect` - missing
- `SDL_SetSurfaceColorKey` - missing
- `SDL_SetSurfaceColorMod` - missing
- `SDL_SetSurfaceColorspace` - missing
- `SDL_SetSurfacePalette` - missing
- `SDL_SetSurfaceRLE` - missing
- `SDL_SetTLS` - missing
- `SDL_SetTextInputArea` - missing
- `SDL_SetTextureAlphaMod` - missing
- `SDL_SetTextureAlphaModFloat` - missing
- `SDL_SetTextureBlendMode` - missing
- `SDL_SetTextureColorMod` - missing
- `SDL_SetTextureColorModFloat` - missing
- `SDL_SetTexturePalette` - missing
- `SDL_SetTextureScaleMode` - missing
- `SDL_SetTrayEntryCallback` - missing
- `SDL_SetTrayEntryChecked` - missing
- `SDL_SetTrayEntryEnabled` - missing
- `SDL_SetTrayEntryLabel` - missing
- `SDL_SetTrayIcon` - missing
- `SDL_SetTrayTooltip` - missing
- `SDL_SetWindowAlwaysOnTop` - missing
- `SDL_SetWindowAspectRatio` - missing
- `SDL_SetWindowBordered` - missing
- `SDL_SetWindowFillDocument` - missing
- `SDL_SetWindowFocusable` - missing
- `SDL_SetWindowFullscreen` - missing
- `SDL_SetWindowFullscreenMode` - missing
- `SDL_SetWindowHitTest` - missing
- `SDL_SetWindowIcon` - missing
- `SDL_SetWindowKeyboardGrab` - missing
- `SDL_SetWindowMaximumSize` - missing
- `SDL_SetWindowMinimumSize` - missing
- `SDL_SetWindowModal` - missing
- `SDL_SetWindowMouseGrab` - missing
- `SDL_SetWindowMouseRect` - missing
- `SDL_SetWindowOpacity` - missing
- `SDL_SetWindowParent` - missing
- `SDL_SetWindowPosition` - missing
- `SDL_SetWindowProgressState` - missing
- `SDL_SetWindowProgressValue` - missing
- `SDL_SetWindowRelativeMouseMode` - missing
- `SDL_SetWindowResizable` - missing
- `SDL_SetWindowShape` - missing
- `SDL_SetWindowSize` - covered by current safe wrapper/native bridge
- `SDL_SetWindowSurfaceVSync` - missing
- `SDL_SetWindowTitle` - missing
- `SDL_SetWindowsMessageHook` - missing
- `SDL_SetX11EventHook` - missing
- `SDL_SetiOSAnimationCallback` - missing
- `SDL_SetiOSEventPump` - missing
- `SDL_ShouldInit` - missing
- `SDL_ShouldQuit` - missing
- `SDL_ShowAndroidToast` - missing
- `SDL_ShowCursor` - missing
- `SDL_ShowFileDialogWithProperties` - missing
- `SDL_ShowMessageBox` - missing
- `SDL_ShowNotification` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_ShowNotificationWithProperties` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_ShowOpenFileDialog` - missing
- `SDL_ShowOpenFolderDialog` - missing
- `SDL_ShowSaveFileDialog` - missing
- `SDL_ShowSimpleMessageBox` - missing
- `SDL_ShowWindow` - covered by current safe wrapper/native bridge
- `SDL_ShowWindowSystemMenu` - missing
- `SDL_SignalAsyncIOQueue` - missing
- `SDL_SignalCondition` - missing
- `SDL_SignalSemaphore` - missing
- `SDL_StartTextInput` - missing
- `SDL_StartTextInputWithProperties` - missing
- `SDL_StepBackUTF8` - missing
- `SDL_StepUTF8` - missing
- `SDL_StopHapticEffect` - missing
- `SDL_StopHapticEffects` - missing
- `SDL_StopHapticRumble` - missing
- `SDL_StopTextInput` - missing
- `SDL_StorageReady` - missing
- `SDL_StretchSurface` - missing
- `SDL_StringToGUID` - missing
- `SDL_SubmitGPUCommandBuffer` - missing
- `SDL_SubmitGPUCommandBufferAndAcquireFence` - missing
- `SDL_SurfaceHasAlternateImages` - missing
- `SDL_SurfaceHasColorKey` - missing
- `SDL_SurfaceHasRLE` - missing
- `SDL_Swap16` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_Swap32` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_Swap64` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_SwapFloat` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_SyncWindow` - missing
- `SDL_TellIO` - missing
- `SDL_TextInputActive` - missing
- `SDL_TimeFromWindows` - missing
- `SDL_TimeToDateTime` - missing
- `SDL_TimeToWindows` - missing
- `SDL_TryLockJoysticks` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_TryLockMutex` - missing
- `SDL_TryLockRWLockForReading` - missing
- `SDL_TryLockRWLockForWriting` - missing
- `SDL_TryLockSpinlock` - missing
- `SDL_TryWaitSemaphore` - missing
- `SDL_UCS4ToUTF8` - missing
- `SDL_UnbindAudioStream` - missing
- `SDL_UnbindAudioStreams` - missing
- `SDL_UnloadObject` - missing
- `SDL_UnlockAudioStream` - missing
- `SDL_UnlockJoysticks` - missing
- `SDL_UnlockMutex` - missing
- `SDL_UnlockProperties` - missing
- `SDL_UnlockRWLock` - missing
- `SDL_UnlockSpinlock` - missing
- `SDL_UnlockSurface` - missing
- `SDL_UnlockTexture` - missing
- `SDL_UnmapGPUTransferBuffer` - missing
- `SDL_UnregisterApp` - missing
- `SDL_UnsetEnvironmentVariable` - missing
- `SDL_UpdateGamepads` - missing
- `SDL_UpdateHapticEffect` - missing
- `SDL_UpdateJoysticks` - missing
- `SDL_UpdateNVTexture` - missing
- `SDL_UpdateSensors` - missing
- `SDL_UpdateTexture` - missing
- `SDL_UpdateTrays` - missing
- `SDL_UpdateWindowSurface` - missing
- `SDL_UpdateWindowSurfaceRects` - missing
- `SDL_UpdateYUVTexture` - missing
- `SDL_UploadToGPUBuffer` - missing
- `SDL_UploadToGPUTexture` - missing
- `SDL_Vulkan_CreateSurface` - missing
- `SDL_Vulkan_DestroySurface` - missing
- `SDL_Vulkan_GetInstanceExtensions` - missing
- `SDL_Vulkan_GetPresentationSupport` - missing
- `SDL_Vulkan_GetVkGetInstanceProcAddr` - missing
- `SDL_Vulkan_LoadLibrary` - missing
- `SDL_Vulkan_UnloadLibrary` - missing
- `SDL_WaitAndAcquireGPUSwapchainTexture` - missing
- `SDL_WaitAsyncIOResult` - missing
- `SDL_WaitCondition` - missing
- `SDL_WaitConditionTimeout` - missing
- `SDL_WaitEvent` - missing
- `SDL_WaitEventTimeout` - covered by current safe wrapper/native bridge
- `SDL_WaitForGPUFences` - missing
- `SDL_WaitForGPUIdle` - missing
- `SDL_WaitForGPUSwapchain` - missing
- `SDL_WaitProcess` - missing
- `SDL_WaitSemaphore` - missing
- `SDL_WaitSemaphoreTimeout` - missing
- `SDL_WaitThread` - missing
- `SDL_WarpMouseGlobal` - missing
- `SDL_WarpMouseInWindow` - missing
- `SDL_WasInit` - covered by current safe wrapper/native bridge
- `SDL_WindowHasSurface` - missing
- `SDL_WindowSupportsGPUPresentMode` - missing
- `SDL_WindowSupportsGPUSwapchainComposition` - missing
- `SDL_WriteAsyncIO` - missing
- `SDL_WriteIO` - missing
- `SDL_WriteS16BE` - missing
- `SDL_WriteS16LE` - missing
- `SDL_WriteS32BE` - missing
- `SDL_WriteS32LE` - missing
- `SDL_WriteS64BE` - missing
- `SDL_WriteS64LE` - missing
- `SDL_WriteS8` - missing
- `SDL_WriteStorageFile` - missing
- `SDL_WriteSurfacePixel` - missing
- `SDL_WriteSurfacePixelFloat` - missing
- `SDL_WriteU16BE` - missing
- `SDL_WriteU16LE` - missing
- `SDL_WriteU32BE` - missing
- `SDL_WriteU32LE` - missing
- `SDL_WriteU64BE` - missing
- `SDL_WriteU64LE` - missing
- `SDL_WriteU8` - missing
- `SDL_abs` - missing
- `SDL_acos` - missing
- `SDL_acosf` - missing
- `SDL_aligned_alloc` - missing
- `SDL_aligned_alloc_zero` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_aligned_free` - missing
- `SDL_asin` - missing
- `SDL_asinf` - missing
- `SDL_asprintf` - missing
- `SDL_atan` - missing
- `SDL_atan2` - missing
- `SDL_atan2f` - missing
- `SDL_atanf` - missing
- `SDL_atof` - missing
- `SDL_atoi` - missing
- `SDL_bsearch` - missing
- `SDL_bsearch_r` - missing
- `SDL_calloc` - missing
- `SDL_ceil` - missing
- `SDL_ceilf` - missing
- `SDL_copysign` - missing
- `SDL_copysignf` - missing
- `SDL_cos` - missing
- `SDL_cosf` - missing
- `SDL_crc16` - missing
- `SDL_crc32` - missing
- `SDL_exp` - missing
- `SDL_expf` - missing
- `SDL_fabs` - missing
- `SDL_fabsf` - missing
- `SDL_floor` - missing
- `SDL_floorf` - missing
- `SDL_fmod` - missing
- `SDL_fmodf` - missing
- `SDL_free` - missing
- `SDL_getenv` - missing
- `SDL_getenv_unsafe` - missing
- `SDL_hid_ble_scan` - missing
- `SDL_hid_close` - missing
- `SDL_hid_device_change_count` - missing
- `SDL_hid_enumerate` - missing
- `SDL_hid_exit` - missing
- `SDL_hid_free_enumeration` - missing
- `SDL_hid_get_device_info` - missing
- `SDL_hid_get_feature_report` - missing
- `SDL_hid_get_indexed_string` - missing
- `SDL_hid_get_input_report` - missing
- `SDL_hid_get_manufacturer_string` - missing
- `SDL_hid_get_product_string` - missing
- `SDL_hid_get_properties` - missing
- `SDL_hid_get_report_descriptor` - missing
- `SDL_hid_get_serial_number_string` - missing
- `SDL_hid_init` - missing
- `SDL_hid_open` - missing
- `SDL_hid_open_path` - missing
- `SDL_hid_read` - missing
- `SDL_hid_read_timeout` - missing
- `SDL_hid_send_feature_report` - missing
- `SDL_hid_set_nonblocking` - missing
- `SDL_hid_write` - missing
- `SDL_iconv` - missing
- `SDL_iconv_close` - missing
- `SDL_iconv_open` - missing
- `SDL_iconv_string` - missing
- `SDL_isalnum` - missing
- `SDL_isalpha` - missing
- `SDL_isblank` - missing
- `SDL_iscntrl` - missing
- `SDL_isdigit` - missing
- `SDL_isgraph` - missing
- `SDL_isinf` - missing
- `SDL_isinff` - missing
- `SDL_islower` - missing
- `SDL_isnan` - missing
- `SDL_isnanf` - missing
- `SDL_isprint` - missing
- `SDL_ispunct` - missing
- `SDL_isspace` - missing
- `SDL_isupper` - missing
- `SDL_isxdigit` - missing
- `SDL_itoa` - missing
- `SDL_lltoa` - missing
- `SDL_log` - missing
- `SDL_log10` - missing
- `SDL_log10f` - missing
- `SDL_logf` - missing
- `SDL_lround` - missing
- `SDL_lroundf` - missing
- `SDL_ltoa` - missing
- `SDL_main` - missing
- `SDL_malloc` - missing
- `SDL_memcmp` - missing
- `SDL_memcpy` - missing
- `SDL_memmove` - missing
- `SDL_memset` - missing
- `SDL_memset4` - missing
- `SDL_modf` - missing
- `SDL_modff` - missing
- `SDL_murmur3_32` - missing
- `SDL_pow` - missing
- `SDL_powf` - missing
- `SDL_qsort` - missing
- `SDL_qsort_r` - missing
- `SDL_rand` - missing
- `SDL_rand_bits` - missing
- `SDL_rand_bits_r` - missing
- `SDL_rand_r` - missing
- `SDL_randf` - missing
- `SDL_randf_r` - missing
- `SDL_realloc` - missing
- `SDL_round` - missing
- `SDL_roundf` - missing
- `SDL_scalbn` - missing
- `SDL_scalbnf` - missing
- `SDL_setenv_unsafe` - missing
- `SDL_sin` - missing
- `SDL_sinf` - missing
- `SDL_size_add_check_overflow` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_size_mul_check_overflow` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_snprintf` - missing
- `SDL_sqrt` - missing
- `SDL_sqrtf` - missing
- `SDL_srand` - missing
- `SDL_sscanf` - missing
- `SDL_strcasecmp` - missing
- `SDL_strcasestr` - missing
- `SDL_strchr` - missing
- `SDL_strcmp` - missing
- `SDL_strdup` - missing
- `SDL_strlcat` - missing
- `SDL_strlcpy` - missing
- `SDL_strlen` - missing
- `SDL_strlwr` - missing
- `SDL_strncasecmp` - missing
- `SDL_strncmp` - missing
- `SDL_strndup` - missing
- `SDL_strnlen` - missing
- `SDL_strnstr` - missing
- `SDL_strpbrk` - missing
- `SDL_strrchr` - missing
- `SDL_strrev` - missing
- `SDL_strstr` - missing
- `SDL_strtod` - missing
- `SDL_strtok_r` - missing
- `SDL_strtol` - missing
- `SDL_strtoll` - missing
- `SDL_strtoul` - missing
- `SDL_strtoull` - missing
- `SDL_strupr` - missing
- `SDL_swprintf` - missing
- `SDL_tan` - missing
- `SDL_tanf` - missing
- `SDL_tolower` - missing
- `SDL_toupper` - missing
- `SDL_trunc` - missing
- `SDL_truncf` - missing
- `SDL_uitoa` - missing
- `SDL_ulltoa` - missing
- `SDL_ultoa` - missing
- `SDL_unsetenv_unsafe` - missing
- `SDL_utf8strlcpy` - missing
- `SDL_utf8strlen` - missing
- `SDL_utf8strnlen` - missing
- `SDL_vasprintf` - missing
- `SDL_vsnprintf` - missing
- `SDL_vsscanf` - missing
- `SDL_vswprintf` - missing
- `SDL_wcscasecmp` - missing
- `SDL_wcscmp` - missing
- `SDL_wcsdup` - missing
- `SDL_wcslcat` - missing
- `SDL_wcslcpy` - missing
- `SDL_wcslen` - missing
- `SDL_wcsncasecmp` - missing
- `SDL_wcsncmp` - missing
- `SDL_wcsnlen` - missing
- `SDL_wcsnstr` - missing
- `SDL_wcsstr` - missing
- `SDL_wcstol` - missing
- `SDL_wcstoll` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_wcstoul` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers
- `SDL_wcstoull` - missing; wiki-listed public/helper symbol not declared as a linkable SDL_DECLSPEC function in the local 3.4.10 headers

### Complete SDL3 Datatype Inventory

- `SDL_AppEvent_func` - missing
- `SDL_AppInit_func` - missing
- `SDL_AppIterate_func` - missing
- `SDL_AppQuit_func` - missing
- `SDL_AssertionHandler` - missing
- `SDL_AsyncIO` - missing
- `SDL_AsyncIOQueue` - missing
- `SDL_AudioDeviceID` - covered by current safe wrapper/raw opaque carrier
- `SDL_AudioPostmixCallback` - missing
- `SDL_AudioStream` - covered by current safe wrapper/raw opaque carrier
- `SDL_AudioStreamCallback` - missing
- `SDL_AudioStreamDataCompleteCallback` - missing
- `SDL_BlendMode` - missing
- `SDL_Camera` - missing
- `SDL_CameraID` - missing
- `SDL_CleanupPropertyCallback` - missing
- `SDL_ClipboardCleanupCallback` - missing
- `SDL_ClipboardDataCallback` - missing
- `SDL_CompareCallback` - missing
- `SDL_CompareCallback_r` - missing
- `SDL_Condition` - missing
- `SDL_Cursor` - missing
- `SDL_DialogFileCallback` - missing
- `SDL_DisplayID` - missing
- `SDL_DisplayModeData` - missing
- `SDL_EGLAttrib` - missing
- `SDL_EGLAttribArrayCallback` - missing
- `SDL_EGLConfig` - missing
- `SDL_EGLDisplay` - missing
- `SDL_EGLIntArrayCallback` - missing
- `SDL_EGLSurface` - missing
- `SDL_EGLint` - missing
- `SDL_EnumerateDirectoryCallback` - missing
- `SDL_EnumeratePropertiesCallback` - missing
- `SDL_Environment` - missing
- `SDL_EventFilter` - missing
- `SDL_FingerID` - missing
- `SDL_FunctionPointer` - missing
- `SDL_GLContext` - missing
- `SDL_GLContextFlag` - missing
- `SDL_GLContextReleaseFlag` - missing
- `SDL_GLContextResetNotification` - missing
- `SDL_GLProfile` - missing
- `SDL_GPUBuffer` - missing
- `SDL_GPUBufferUsageFlags` - missing
- `SDL_GPUColorComponentFlags` - missing
- `SDL_GPUCommandBuffer` - missing
- `SDL_GPUComputePass` - missing
- `SDL_GPUComputePipeline` - missing
- `SDL_GPUCopyPass` - missing
- `SDL_GPUDevice` - missing
- `SDL_GPUFence` - missing
- `SDL_GPUGraphicsPipeline` - missing
- `SDL_GPURenderPass` - missing
- `SDL_GPURenderState` - missing
- `SDL_GPUSampler` - missing
- `SDL_GPUShader` - missing
- `SDL_GPUShaderFormat` - missing
- `SDL_GPUTexture` - missing
- `SDL_GPUTextureUsageFlags` - missing
- `SDL_GPUTransferBuffer` - missing
- `SDL_Gamepad` - missing
- `SDL_GlobFlags` - missing
- `SDL_Haptic` - missing
- `SDL_HapticDirectionType` - missing
- `SDL_HapticEffectID` - missing
- `SDL_HapticEffectType` - missing
- `SDL_HapticID` - missing
- `SDL_HintCallback` - missing
- `SDL_HitTest` - missing
- `SDL_IOStream` - missing
- `SDL_InitFlags` - covered by current safe wrapper/raw opaque carrier
- `SDL_Joystick` - missing
- `SDL_JoystickID` - missing
- `SDL_KeyboardID` - missing
- `SDL_Keycode` - missing
- `SDL_Keymod` - missing
- `SDL_LogOutputFunction` - missing
- `SDL_MainThreadCallback` - missing
- `SDL_MessageBoxButtonFlags` - missing
- `SDL_MessageBoxFlags` - missing
- `SDL_MetalView` - missing
- `SDL_MouseButtonFlags` - missing
- `SDL_MouseID` - missing
- `SDL_MouseMotionTransformCallback` - missing
- `SDL_Mutex` - missing
- `SDL_NSTimerCallback` - missing
- `SDL_PenID` - missing
- `SDL_PenInputFlags` - missing
- `SDL_Process` - missing
- `SDL_PropertiesID` - missing
- `SDL_RWLock` - missing
- `SDL_Renderer` - covered by current safe wrapper/raw opaque carrier
- `SDL_RequestAndroidPermissionCallback` - missing
- `SDL_Semaphore` - missing
- `SDL_Sensor` - missing
- `SDL_SensorID` - missing
- `SDL_SharedObject` - missing
- `SDL_SpinLock` - missing
- `SDL_Storage` - missing
- `SDL_SurfaceFlags` - missing
- `SDL_TLSDestructorCallback` - missing
- `SDL_TLSID` - missing
- `SDL_Thread` - missing
- `SDL_ThreadFunction` - missing
- `SDL_ThreadID` - missing
- `SDL_Time` - missing
- `SDL_TimerCallback` - missing
- `SDL_TimerID` - missing
- `SDL_TouchID` - missing
- `SDL_Tray` - missing
- `SDL_TrayCallback` - missing
- `SDL_TrayClickCallback` - missing
- `SDL_TrayEntry` - missing
- `SDL_TrayEntryFlags` - missing
- `SDL_TrayMenu` - missing
- `SDL_Window` - covered by current safe wrapper/raw opaque carrier
- `SDL_WindowFlags` - covered by current safe wrapper/raw opaque carrier
- `SDL_WindowID` - missing
- `SDL_WindowsMessageHook` - missing
- `SDL_X11EventHook` - missing
- `SDL_calloc_func` - missing
- `SDL_free_func` - missing
- `SDL_hid_device` - missing
- `SDL_iOSAnimationCallback` - missing
- `SDL_iconv_t` - missing
- `SDL_main_func` - missing
- `SDL_malloc_func` - missing
- `SDL_realloc_func` - missing
- `Sint16` - represented by Stark primitive integer type; no named SDL alias
- `Sint32` - represented by Stark primitive integer type; no named SDL alias
- `Sint64` - represented by Stark primitive integer type; no named SDL alias
- `Sint8` - represented by Stark primitive integer type; no named SDL alias
- `Uint16` - represented by Stark primitive integer type; no named SDL alias
- `Uint32` - represented by Stark primitive integer type; no named SDL alias
- `Uint64` - represented by Stark primitive integer type; no named SDL alias
- `Uint8` - represented by Stark primitive integer type; no named SDL alias

### Complete SDL3 Struct Inventory

- `SDL_AssertData` - missing
- `SDL_AsyncIOOutcome` - missing
- `SDL_AtomicInt` - missing
- `SDL_AtomicU32` - missing
- `SDL_AudioDeviceEvent` - missing
- `SDL_AudioSpec` - covered by ABI carrier `AudioSpec`
- `SDL_CameraDeviceEvent` - missing
- `SDL_CameraSpec` - missing
- `SDL_ClipboardEvent` - missing
- `SDL_Color` - missing
- `SDL_CommonEvent` - missing
- `SDL_CursorFrameInfo` - missing
- `SDL_DateTime` - missing
- `SDL_DialogFileFilter` - missing
- `SDL_DisplayEvent` - missing
- `SDL_DisplayMode` - missing
- `SDL_DropEvent` - missing
- `SDL_Event` - partially covered by flattened native event bridge; full union missing
- `SDL_FColor` - missing
- `SDL_FPoint` - missing
- `SDL_FRect` - missing
- `SDL_Finger` - missing
- `SDL_GPUBlitInfo` - missing
- `SDL_GPUBlitRegion` - missing
- `SDL_GPUBufferBinding` - missing
- `SDL_GPUBufferCreateInfo` - missing
- `SDL_GPUBufferLocation` - missing
- `SDL_GPUBufferRegion` - missing
- `SDL_GPUColorTargetBlendState` - missing
- `SDL_GPUColorTargetDescription` - missing
- `SDL_GPUColorTargetInfo` - missing
- `SDL_GPUComputePipelineCreateInfo` - missing
- `SDL_GPUDepthStencilState` - missing
- `SDL_GPUDepthStencilTargetInfo` - missing
- `SDL_GPUGraphicsPipelineCreateInfo` - missing
- `SDL_GPUGraphicsPipelineTargetInfo` - missing
- `SDL_GPUIndexedIndirectDrawCommand` - missing
- `SDL_GPUIndirectDispatchCommand` - missing
- `SDL_GPUIndirectDrawCommand` - missing
- `SDL_GPUMultisampleState` - missing
- `SDL_GPURasterizerState` - missing
- `SDL_GPURenderStateCreateInfo` - missing
- `SDL_GPUSamplerCreateInfo` - missing
- `SDL_GPUShaderCreateInfo` - missing
- `SDL_GPUStencilOpState` - missing
- `SDL_GPUStorageBufferReadWriteBinding` - missing
- `SDL_GPUStorageTextureReadWriteBinding` - missing
- `SDL_GPUTextureCreateInfo` - missing
- `SDL_GPUTextureLocation` - missing
- `SDL_GPUTextureRegion` - missing
- `SDL_GPUTextureSamplerBinding` - missing
- `SDL_GPUTextureTransferInfo` - missing
- `SDL_GPUTransferBufferCreateInfo` - missing
- `SDL_GPUTransferBufferLocation` - missing
- `SDL_GPUVertexAttribute` - missing
- `SDL_GPUVertexBufferDescription` - missing
- `SDL_GPUVertexInputState` - missing
- `SDL_GPUViewport` - missing
- `SDL_GPUVulkanOptions` - missing
- `SDL_GUID` - missing
- `SDL_GamepadAxisEvent` - missing
- `SDL_GamepadBinding` - missing
- `SDL_GamepadButtonEvent` - missing
- `SDL_GamepadCapSenseEvent` - missing
- `SDL_GamepadDeviceEvent` - missing
- `SDL_GamepadSensorEvent` - missing
- `SDL_GamepadTouchpadEvent` - missing
- `SDL_HapticCondition` - missing
- `SDL_HapticConstant` - missing
- `SDL_HapticCustom` - missing
- `SDL_HapticDirection` - missing
- `SDL_HapticEffect` - missing
- `SDL_HapticLeftRight` - missing
- `SDL_HapticPeriodic` - missing
- `SDL_HapticRamp` - missing
- `SDL_IOStreamInterface` - missing
- `SDL_InitState` - missing
- `SDL_JoyAxisEvent` - missing
- `SDL_JoyBallEvent` - missing
- `SDL_JoyBatteryEvent` - missing
- `SDL_JoyButtonEvent` - missing
- `SDL_JoyDeviceEvent` - missing
- `SDL_JoyHatEvent` - missing
- `SDL_KeyboardDeviceEvent` - missing
- `SDL_KeyboardEvent` - missing
- `SDL_Locale` - missing
- `SDL_MessageBoxButtonData` - missing
- `SDL_MessageBoxColor` - missing
- `SDL_MessageBoxColorScheme` - missing
- `SDL_MessageBoxData` - missing
- `SDL_MouseButtonEvent` - missing
- `SDL_MouseDeviceEvent` - missing
- `SDL_MouseMotionEvent` - missing
- `SDL_MouseWheelEvent` - missing
- `SDL_NotificationAction` - missing
- `SDL_NotificationEvent` - missing
- `SDL_Palette` - missing
- `SDL_PathInfo` - missing
- `SDL_PenAxisEvent` - missing
- `SDL_PenButtonEvent` - missing
- `SDL_PenMotionEvent` - missing
- `SDL_PenProximityEvent` - missing
- `SDL_PenTouchEvent` - missing
- `SDL_PinchFingerEvent` - missing
- `SDL_PixelFormatDetails` - missing
- `SDL_Point` - missing
- `SDL_QuitEvent` - missing
- `SDL_Rect` - missing
- `SDL_RenderEvent` - missing
- `SDL_SensorEvent` - missing
- `SDL_StorageInterface` - missing
- `SDL_Surface` - missing
- `SDL_TextEditingCandidatesEvent` - missing
- `SDL_TextEditingEvent` - missing
- `SDL_TextInputEvent` - missing
- `SDL_Texture` - missing
- `SDL_TouchFingerEvent` - missing
- `SDL_UserEvent` - missing
- `SDL_Vertex` - missing
- `SDL_VirtualJoystickDesc` - missing
- `SDL_VirtualJoystickSensorDesc` - missing
- `SDL_VirtualJoystickTouchpadDesc` - missing
- `SDL_WindowEvent` - missing
- `SDL_hid_device_info` - missing

### Complete SDL3 Enum Inventory

- `SDL_AppResult` - missing
- `SDL_ArrayOrder` - missing
- `SDL_AssertState` - missing
- `SDL_AsyncIOResult` - missing
- `SDL_AsyncIOTaskType` - missing
- `SDL_AudioFormat` - partially covered by selected Stark audio-format constants; full enum missing
- `SDL_BitmapOrder` - missing
- `SDL_BlendFactor` - missing
- `SDL_BlendOperation` - missing
- `SDL_CameraPermissionState` - missing
- `SDL_CameraPosition` - missing
- `SDL_Capitalization` - missing
- `SDL_ChromaLocation` - missing
- `SDL_ColorPrimaries` - missing
- `SDL_ColorRange` - missing
- `SDL_ColorType` - missing
- `SDL_Colorspace` - missing
- `SDL_DateFormat` - missing
- `SDL_DisplayOrientation` - missing
- `SDL_EnumerationResult` - missing
- `SDL_EventAction` - missing
- `SDL_EventType` - partially covered by selected Stark event constants and `SdlEventKind`; full enum missing
- `SDL_FileDialogType` - missing
- `SDL_FlashOperation` - missing
- `SDL_FlipMode` - missing
- `SDL_Folder` - missing
- `SDL_FormFactor` - missing
- `SDL_GLAttr` - missing
- `SDL_GPUBlendFactor` - missing
- `SDL_GPUBlendOp` - missing
- `SDL_GPUCompareOp` - missing
- `SDL_GPUCubeMapFace` - missing
- `SDL_GPUCullMode` - missing
- `SDL_GPUFillMode` - missing
- `SDL_GPUFilter` - missing
- `SDL_GPUFrontFace` - missing
- `SDL_GPUIndexElementSize` - missing
- `SDL_GPULoadOp` - missing
- `SDL_GPUPresentMode` - missing
- `SDL_GPUPrimitiveType` - missing
- `SDL_GPUSampleCount` - missing
- `SDL_GPUSamplerAddressMode` - missing
- `SDL_GPUSamplerMipmapMode` - missing
- `SDL_GPUShaderStage` - missing
- `SDL_GPUStencilOp` - missing
- `SDL_GPUStoreOp` - missing
- `SDL_GPUSwapchainComposition` - missing
- `SDL_GPUTextureFormat` - missing
- `SDL_GPUTextureType` - missing
- `SDL_GPUTransferBufferUsage` - missing
- `SDL_GPUVertexElementFormat` - missing
- `SDL_GPUVertexInputRate` - missing
- `SDL_GamepadAxis` - missing
- `SDL_GamepadBindingType` - missing
- `SDL_GamepadButton` - missing
- `SDL_GamepadButtonLabel` - missing
- `SDL_GamepadCapSenseType` - missing
- `SDL_GamepadType` - missing
- `SDL_HintPriority` - missing
- `SDL_HitTestResult` - missing
- `SDL_IOStatus` - missing
- `SDL_IOWhence` - missing
- `SDL_InitStatus` - missing
- `SDL_JoystickConnectionState` - missing
- `SDL_JoystickType` - missing
- `SDL_LogCategory` - missing
- `SDL_LogPriority` - missing
- `SDL_MatrixCoefficients` - missing
- `SDL_MessageBoxColorType` - missing
- `SDL_MouseWheelDirection` - missing
- `SDL_PackedLayout` - missing
- `SDL_PackedOrder` - missing
- `SDL_PathType` - missing
- `SDL_PenAxis` - missing
- `SDL_PenDeviceType` - missing
- `SDL_PixelFormat` - missing
- `SDL_PixelType` - missing
- `SDL_PowerState` - missing
- `SDL_ProcessIO` - missing
- `SDL_ProgressState` - missing
- `SDL_PropertyType` - missing
- `SDL_RendererLogicalPresentation` - missing
- `SDL_Sandbox` - missing
- `SDL_ScaleMode` - missing
- `SDL_Scancode` - missing
- `SDL_SensorType` - missing
- `SDL_SystemCursor` - missing
- `SDL_SystemTheme` - missing
- `SDL_TextInputType` - missing
- `SDL_TextureAccess` - missing
- `SDL_TextureAddressMode` - missing
- `SDL_ThreadPriority` - missing
- `SDL_ThreadState` - missing
- `SDL_TimeFormat` - missing
- `SDL_TouchDeviceType` - missing
- `SDL_TransferCharacteristics` - missing
- `SDL_hid_bus_type` - missing

### Complete SDL3 Macro Inventory

- `SDLCALL` - missing
- `SDLK_0` - missing
- `SDLK_1` - missing
- `SDLK_2` - missing
- `SDLK_3` - missing
- `SDLK_4` - missing
- `SDLK_5` - missing
- `SDLK_6` - missing
- `SDLK_7` - missing
- `SDLK_8` - missing
- `SDLK_9` - missing
- `SDLK_A` - missing
- `SDLK_AC_BACK` - missing
- `SDLK_AC_BOOKMARKS` - missing
- `SDLK_AC_CLOSE` - missing
- `SDLK_AC_EXIT` - missing
- `SDLK_AC_FORWARD` - missing
- `SDLK_AC_HOME` - missing
- `SDLK_AC_NEW` - missing
- `SDLK_AC_OPEN` - missing
- `SDLK_AC_PRINT` - missing
- `SDLK_AC_PROPERTIES` - missing
- `SDLK_AC_REFRESH` - missing
- `SDLK_AC_SAVE` - missing
- `SDLK_AC_SEARCH` - missing
- `SDLK_AC_STOP` - missing
- `SDLK_AGAIN` - missing
- `SDLK_ALTERASE` - missing
- `SDLK_AMPERSAND` - missing
- `SDLK_APOSTROPHE` - missing
- `SDLK_APPLICATION` - missing
- `SDLK_ASTERISK` - missing
- `SDLK_AT` - missing
- `SDLK_B` - missing
- `SDLK_BACKSLASH` - missing
- `SDLK_BACKSPACE` - missing
- `SDLK_C` - missing
- `SDLK_CALL` - missing
- `SDLK_CANCEL` - missing
- `SDLK_CAPSLOCK` - missing
- `SDLK_CARET` - missing
- `SDLK_CHANNEL_DECREMENT` - missing
- `SDLK_CHANNEL_INCREMENT` - missing
- `SDLK_CLEAR` - missing
- `SDLK_CLEARAGAIN` - missing
- `SDLK_COLON` - missing
- `SDLK_COMMA` - missing
- `SDLK_COPY` - missing
- `SDLK_CRSEL` - missing
- `SDLK_CURRENCYSUBUNIT` - missing
- `SDLK_CURRENCYUNIT` - missing
- `SDLK_CUT` - missing
- `SDLK_D` - missing
- `SDLK_DBLAPOSTROPHE` - missing
- `SDLK_DECIMALSEPARATOR` - missing
- `SDLK_DELETE` - missing
- `SDLK_DOLLAR` - missing
- `SDLK_DOWN` - missing
- `SDLK_E` - missing
- `SDLK_END` - missing
- `SDLK_ENDCALL` - missing
- `SDLK_EQUALS` - missing
- `SDLK_ESCAPE` - covered by renamed Stark constant or internal native use
- `SDLK_EXCLAIM` - missing
- `SDLK_EXECUTE` - missing
- `SDLK_EXSEL` - missing
- `SDLK_EXTENDED_MASK` - missing
- `SDLK_F` - missing
- `SDLK_F1` - missing
- `SDLK_F10` - missing
- `SDLK_F11` - missing
- `SDLK_F12` - missing
- `SDLK_F13` - missing
- `SDLK_F14` - missing
- `SDLK_F15` - missing
- `SDLK_F16` - missing
- `SDLK_F17` - missing
- `SDLK_F18` - missing
- `SDLK_F19` - missing
- `SDLK_F2` - missing
- `SDLK_F20` - missing
- `SDLK_F21` - missing
- `SDLK_F22` - missing
- `SDLK_F23` - missing
- `SDLK_F24` - missing
- `SDLK_F3` - missing
- `SDLK_F4` - missing
- `SDLK_F5` - missing
- `SDLK_F6` - missing
- `SDLK_F7` - missing
- `SDLK_F8` - missing
- `SDLK_F9` - missing
- `SDLK_FIND` - missing
- `SDLK_G` - missing
- `SDLK_GRAVE` - missing
- `SDLK_GREATER` - missing
- `SDLK_H` - missing
- `SDLK_HASH` - missing
- `SDLK_HELP` - missing
- `SDLK_HOME` - missing
- `SDLK_I` - missing
- `SDLK_INSERT` - missing
- `SDLK_J` - missing
- `SDLK_K` - missing
- `SDLK_KP_0` - missing
- `SDLK_KP_00` - missing
- `SDLK_KP_000` - missing
- `SDLK_KP_1` - missing
- `SDLK_KP_2` - missing
- `SDLK_KP_3` - missing
- `SDLK_KP_4` - missing
- `SDLK_KP_5` - missing
- `SDLK_KP_6` - missing
- `SDLK_KP_7` - missing
- `SDLK_KP_8` - missing
- `SDLK_KP_9` - missing
- `SDLK_KP_A` - missing
- `SDLK_KP_AMPERSAND` - missing
- `SDLK_KP_AT` - missing
- `SDLK_KP_B` - missing
- `SDLK_KP_BACKSPACE` - missing
- `SDLK_KP_BINARY` - missing
- `SDLK_KP_C` - missing
- `SDLK_KP_CLEAR` - missing
- `SDLK_KP_CLEARENTRY` - missing
- `SDLK_KP_COLON` - missing
- `SDLK_KP_COMMA` - missing
- `SDLK_KP_D` - missing
- `SDLK_KP_DBLAMPERSAND` - missing
- `SDLK_KP_DBLVERTICALBAR` - missing
- `SDLK_KP_DECIMAL` - missing
- `SDLK_KP_DIVIDE` - missing
- `SDLK_KP_E` - missing
- `SDLK_KP_ENTER` - missing
- `SDLK_KP_EQUALS` - missing
- `SDLK_KP_EQUALSAS400` - missing
- `SDLK_KP_EXCLAM` - missing
- `SDLK_KP_F` - missing
- `SDLK_KP_GREATER` - missing
- `SDLK_KP_HASH` - missing
- `SDLK_KP_HEXADECIMAL` - missing
- `SDLK_KP_LEFTBRACE` - missing
- `SDLK_KP_LEFTPAREN` - missing
- `SDLK_KP_LESS` - missing
- `SDLK_KP_MEMADD` - missing
- `SDLK_KP_MEMCLEAR` - missing
- `SDLK_KP_MEMDIVIDE` - missing
- `SDLK_KP_MEMMULTIPLY` - missing
- `SDLK_KP_MEMRECALL` - missing
- `SDLK_KP_MEMSTORE` - missing
- `SDLK_KP_MEMSUBTRACT` - missing
- `SDLK_KP_MINUS` - missing
- `SDLK_KP_MULTIPLY` - missing
- `SDLK_KP_OCTAL` - missing
- `SDLK_KP_PERCENT` - missing
- `SDLK_KP_PERIOD` - missing
- `SDLK_KP_PLUS` - missing
- `SDLK_KP_PLUSMINUS` - missing
- `SDLK_KP_POWER` - missing
- `SDLK_KP_RIGHTBRACE` - missing
- `SDLK_KP_RIGHTPAREN` - missing
- `SDLK_KP_SPACE` - missing
- `SDLK_KP_TAB` - missing
- `SDLK_KP_VERTICALBAR` - missing
- `SDLK_KP_XOR` - missing
- `SDLK_L` - missing
- `SDLK_LALT` - missing
- `SDLK_LCTRL` - missing
- `SDLK_LEFT` - missing
- `SDLK_LEFTBRACE` - missing
- `SDLK_LEFTBRACKET` - missing
- `SDLK_LEFTPAREN` - missing
- `SDLK_LEFT_TAB` - missing
- `SDLK_LESS` - missing
- `SDLK_LEVEL5_SHIFT` - missing
- `SDLK_LGUI` - missing
- `SDLK_LHYPER` - missing
- `SDLK_LMETA` - missing
- `SDLK_LSHIFT` - missing
- `SDLK_M` - missing
- `SDLK_MEDIA_EJECT` - missing
- `SDLK_MEDIA_FAST_FORWARD` - missing
- `SDLK_MEDIA_NEXT_TRACK` - missing
- `SDLK_MEDIA_PAUSE` - missing
- `SDLK_MEDIA_PLAY` - missing
- `SDLK_MEDIA_PLAY_PAUSE` - missing
- `SDLK_MEDIA_PREVIOUS_TRACK` - missing
- `SDLK_MEDIA_RECORD` - missing
- `SDLK_MEDIA_REWIND` - missing
- `SDLK_MEDIA_SELECT` - missing
- `SDLK_MEDIA_STOP` - missing
- `SDLK_MENU` - missing
- `SDLK_MINUS` - missing
- `SDLK_MODE` - missing
- `SDLK_MULTI_KEY_COMPOSE` - missing
- `SDLK_MUTE` - missing
- `SDLK_N` - missing
- `SDLK_NUMLOCKCLEAR` - missing
- `SDLK_O` - missing
- `SDLK_OPER` - missing
- `SDLK_OUT` - missing
- `SDLK_P` - missing
- `SDLK_PAGEDOWN` - missing
- `SDLK_PAGEUP` - missing
- `SDLK_PASTE` - missing
- `SDLK_PAUSE` - missing
- `SDLK_PERCENT` - missing
- `SDLK_PERIOD` - missing
- `SDLK_PIPE` - missing
- `SDLK_PLUS` - missing
- `SDLK_PLUSMINUS` - missing
- `SDLK_POWER` - missing
- `SDLK_PRINTSCREEN` - missing
- `SDLK_PRIOR` - missing
- `SDLK_Q` - missing
- `SDLK_QUESTION` - missing
- `SDLK_R` - missing
- `SDLK_RALT` - missing
- `SDLK_RCTRL` - missing
- `SDLK_RETURN` - covered by renamed Stark constant or internal native use
- `SDLK_RETURN2` - missing
- `SDLK_RGUI` - missing
- `SDLK_RHYPER` - missing
- `SDLK_RIGHT` - missing
- `SDLK_RIGHTBRACE` - missing
- `SDLK_RIGHTBRACKET` - missing
- `SDLK_RIGHTPAREN` - missing
- `SDLK_RMETA` - missing
- `SDLK_RSHIFT` - missing
- `SDLK_S` - missing
- `SDLK_SCANCODE_MASK` - missing
- `SDLK_SCROLLLOCK` - missing
- `SDLK_SELECT` - missing
- `SDLK_SEMICOLON` - missing
- `SDLK_SEPARATOR` - missing
- `SDLK_SLASH` - missing
- `SDLK_SLEEP` - missing
- `SDLK_SOFTLEFT` - missing
- `SDLK_SOFTRIGHT` - missing
- `SDLK_SPACE` - covered by renamed Stark constant or internal native use
- `SDLK_STOP` - missing
- `SDLK_SYSREQ` - missing
- `SDLK_T` - missing
- `SDLK_TAB` - missing
- `SDLK_THOUSANDSSEPARATOR` - missing
- `SDLK_TILDE` - missing
- `SDLK_U` - missing
- `SDLK_UNDERSCORE` - missing
- `SDLK_UNDO` - missing
- `SDLK_UNKNOWN` - missing
- `SDLK_UP` - missing
- `SDLK_V` - missing
- `SDLK_VOLUMEDOWN` - missing
- `SDLK_VOLUMEUP` - missing
- `SDLK_W` - missing
- `SDLK_WAKE` - missing
- `SDLK_X` - missing
- `SDLK_Y` - missing
- `SDLK_Z` - missing
- `SDLMAIN_DECLSPEC` - missing
- `SDL_ACQUIRE` - missing
- `SDL_ACQUIRED_AFTER` - missing
- `SDL_ACQUIRED_BEFORE` - missing
- `SDL_ACQUIRE_SHARED` - missing
- `SDL_ALIGNED` - missing
- `SDL_ALLOC_SIZE` - missing
- `SDL_ALPHA_OPAQUE` - missing
- `SDL_ALPHA_OPAQUE_FLOAT` - missing
- `SDL_ALPHA_TRANSPARENT` - missing
- `SDL_ALPHA_TRANSPARENT_FLOAT` - missing
- `SDL_ALTIVEC_INTRINSICS` - missing
- `SDL_ANALYZER_NORETURN` - missing
- `SDL_ANDROID_EXTERNAL_STORAGE_READ` - missing
- `SDL_ANDROID_EXTERNAL_STORAGE_WRITE` - missing
- `SDL_ASSERT_CAPABILITY` - missing
- `SDL_ASSERT_FILE` - missing
- `SDL_ASSERT_LEVEL` - missing
- `SDL_ASSERT_SHARED_CAPABILITY` - missing
- `SDL_AUDIO_BITSIZE` - missing
- `SDL_AUDIO_BYTESIZE` - missing
- `SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK` - covered by renamed Stark constant or internal native use
- `SDL_AUDIO_DEVICE_DEFAULT_RECORDING` - covered by renamed Stark constant or internal native use
- `SDL_AUDIO_FRAMESIZE` - missing
- `SDL_AUDIO_ISBIGENDIAN` - missing
- `SDL_AUDIO_ISFLOAT` - missing
- `SDL_AUDIO_ISINT` - missing
- `SDL_AUDIO_ISLITTLEENDIAN` - missing
- `SDL_AUDIO_ISSIGNED` - missing
- `SDL_AUDIO_ISUNSIGNED` - missing
- `SDL_AUDIO_MASK_BIG_ENDIAN` - missing
- `SDL_AUDIO_MASK_BITSIZE` - missing
- `SDL_AUDIO_MASK_FLOAT` - missing
- `SDL_AUDIO_MASK_SIGNED` - missing
- `SDL_AVX2_INTRINSICS` - missing
- `SDL_AVX512F_INTRINSICS` - missing
- `SDL_AVX_INTRINSICS` - missing
- `SDL_AssertBreakpoint` - missing
- `SDL_AtomicDecRef` - missing
- `SDL_AtomicIncRef` - missing
- `SDL_BIG_ENDIAN` - missing
- `SDL_BITSPERPIXEL` - missing
- `SDL_BLENDMODE_ADD` - missing
- `SDL_BLENDMODE_ADD_PREMULTIPLIED` - missing
- `SDL_BLENDMODE_BLEND` - missing
- `SDL_BLENDMODE_BLEND_PREMULTIPLIED` - missing
- `SDL_BLENDMODE_INVALID` - missing
- `SDL_BLENDMODE_MOD` - missing
- `SDL_BLENDMODE_MUL` - missing
- `SDL_BLENDMODE_NONE` - missing
- `SDL_BUTTON_LEFT` - covered by renamed Stark constant or internal native use
- `SDL_BUTTON_LMASK` - missing
- `SDL_BUTTON_MASK` - missing
- `SDL_BUTTON_MIDDLE` - covered by renamed Stark constant or internal native use
- `SDL_BUTTON_MMASK` - missing
- `SDL_BUTTON_RIGHT` - covered by renamed Stark constant or internal native use
- `SDL_BUTTON_RMASK` - missing
- `SDL_BUTTON_X1` - missing
- `SDL_BUTTON_X1MASK` - missing
- `SDL_BUTTON_X2` - missing
- `SDL_BUTTON_X2MASK` - missing
- `SDL_BYTEORDER` - missing
- `SDL_BYTESPERPIXEL` - missing
- `SDL_CACHELINE_SIZE` - missing
- `SDL_CAPABILITY` - missing
- `SDL_COLORSPACECHROMA` - missing
- `SDL_COLORSPACEMATRIX` - missing
- `SDL_COLORSPACEPRIMARIES` - missing
- `SDL_COLORSPACERANGE` - missing
- `SDL_COLORSPACETRANSFER` - missing
- `SDL_COLORSPACETYPE` - missing
- `SDL_COMPILE_TIME_ASSERT` - missing
- `SDL_CPUPauseInstruction` - missing
- `SDL_CompilerBarrier` - missing
- `SDL_DEBUG_TEXT_FONT_CHARACTER_SIZE` - missing
- `SDL_DECLSPEC` - missing
- `SDL_DEFINE_AUDIO_FORMAT` - missing
- `SDL_DEFINE_COLORSPACE` - missing
- `SDL_DEFINE_PIXELFORMAT` - missing
- `SDL_DEFINE_PIXELFOURCC` - missing
- `SDL_DEPRECATED` - missing
- `SDL_ELF_NOTE_DLOPEN` - missing
- `SDL_ELF_NOTE_DLOPEN_PRIORITY_RECOMMENDED` - missing
- `SDL_ELF_NOTE_DLOPEN_PRIORITY_REQUIRED` - missing
- `SDL_ELF_NOTE_DLOPEN_PRIORITY_SUGGESTED` - missing
- `SDL_EXCLUDES` - missing
- `SDL_FALLTHROUGH` - missing
- `SDL_FILE` - missing
- `SDL_FLOATWORDORDER` - missing
- `SDL_FLT_EPSILON` - missing
- `SDL_FORCE_INLINE` - missing
- `SDL_FOURCC` - missing
- `SDL_FUNCTION` - missing
- `SDL_FUNCTION_POINTER_IS_VOID_POINTER` - missing
- `SDL_GLOB_CASEINSENSITIVE` - missing
- `SDL_GL_CONTEXT_DEBUG_FLAG` - missing
- `SDL_GL_CONTEXT_FORWARD_COMPATIBLE_FLAG` - missing
- `SDL_GL_CONTEXT_PROFILE_COMPATIBILITY` - missing
- `SDL_GL_CONTEXT_PROFILE_CORE` - missing
- `SDL_GL_CONTEXT_PROFILE_ES` - missing
- `SDL_GL_CONTEXT_RELEASE_BEHAVIOR_FLUSH` - missing
- `SDL_GL_CONTEXT_RELEASE_BEHAVIOR_NONE` - missing
- `SDL_GL_CONTEXT_RESET_ISOLATION_FLAG` - missing
- `SDL_GL_CONTEXT_RESET_LOSE_CONTEXT` - missing
- `SDL_GL_CONTEXT_RESET_NO_NOTIFICATION` - missing
- `SDL_GL_CONTEXT_ROBUST_ACCESS_FLAG` - missing
- `SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_READ` - missing
- `SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_WRITE` - missing
- `SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ` - missing
- `SDL_GPU_BUFFERUSAGE_INDEX` - missing
- `SDL_GPU_BUFFERUSAGE_INDIRECT` - missing
- `SDL_GPU_BUFFERUSAGE_VERTEX` - missing
- `SDL_GPU_COLORCOMPONENT_A` - missing
- `SDL_GPU_COLORCOMPONENT_B` - missing
- `SDL_GPU_COLORCOMPONENT_G` - missing
- `SDL_GPU_COLORCOMPONENT_R` - missing
- `SDL_GPU_RENDERER` - missing
- `SDL_GPU_SHADERFORMAT_DXBC` - missing
- `SDL_GPU_SHADERFORMAT_DXIL` - missing
- `SDL_GPU_SHADERFORMAT_INVALID` - missing
- `SDL_GPU_SHADERFORMAT_METALLIB` - missing
- `SDL_GPU_SHADERFORMAT_MSL` - missing
- `SDL_GPU_SHADERFORMAT_PRIVATE` - missing
- `SDL_GPU_SHADERFORMAT_SPIRV` - missing
- `SDL_GPU_TEXTUREUSAGE_COLOR_TARGET` - missing
- `SDL_GPU_TEXTUREUSAGE_COMPUTE_STORAGE_READ` - missing
- `SDL_GPU_TEXTUREUSAGE_COMPUTE_STORAGE_SIMULTANEOUS_READ_WRITE` - missing
- `SDL_GPU_TEXTUREUSAGE_COMPUTE_STORAGE_WRITE` - missing
- `SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET` - missing
- `SDL_GPU_TEXTUREUSAGE_GRAPHICS_STORAGE_READ` - missing
- `SDL_GPU_TEXTUREUSAGE_SAMPLER` - missing
- `SDL_GUARDED_BY` - missing
- `SDL_HAPTIC_AUTOCENTER` - missing
- `SDL_HAPTIC_CARTESIAN` - missing
- `SDL_HAPTIC_CONSTANT` - missing
- `SDL_HAPTIC_CUSTOM` - missing
- `SDL_HAPTIC_DAMPER` - missing
- `SDL_HAPTIC_FRICTION` - missing
- `SDL_HAPTIC_GAIN` - missing
- `SDL_HAPTIC_INERTIA` - missing
- `SDL_HAPTIC_INFINITY` - missing
- `SDL_HAPTIC_LEFTRIGHT` - missing
- `SDL_HAPTIC_PAUSE` - missing
- `SDL_HAPTIC_POLAR` - missing
- `SDL_HAPTIC_RAMP` - missing
- `SDL_HAPTIC_RESERVED1` - missing
- `SDL_HAPTIC_RESERVED2` - missing
- `SDL_HAPTIC_RESERVED3` - missing
- `SDL_HAPTIC_SAWTOOTHDOWN` - missing
- `SDL_HAPTIC_SAWTOOTHUP` - missing
- `SDL_HAPTIC_SINE` - missing
- `SDL_HAPTIC_SPHERICAL` - missing
- `SDL_HAPTIC_SPRING` - missing
- `SDL_HAPTIC_SQUARE` - missing
- `SDL_HAPTIC_STATUS` - missing
- `SDL_HAPTIC_STEERING_AXIS` - missing
- `SDL_HAPTIC_TRIANGLE` - missing
- `SDL_HAS_BUILTIN` - missing
- `SDL_HAS_EXTENSION` - missing
- `SDL_HAS_TARGET_ATTRIBS` - missing
- `SDL_HAT_CENTERED` - missing
- `SDL_HAT_DOWN` - missing
- `SDL_HAT_LEFT` - missing
- `SDL_HAT_LEFTDOWN` - missing
- `SDL_HAT_LEFTUP` - missing
- `SDL_HAT_RIGHT` - missing
- `SDL_HAT_RIGHTDOWN` - missing
- `SDL_HAT_RIGHTUP` - missing
- `SDL_HAT_UP` - missing
- `SDL_HINT_ALLOW_ALT_TAB_WHILE_GRABBED` - missing
- `SDL_HINT_ANDROID_ALLOW_PERSISTENT_FOLDER_ACCESS` - missing
- `SDL_HINT_ANDROID_ALLOW_RECREATE_ACTIVITY` - missing
- `SDL_HINT_ANDROID_BLOCK_ON_PAUSE` - missing
- `SDL_HINT_ANDROID_LOW_LATENCY_AUDIO` - missing
- `SDL_HINT_ANDROID_TRAP_BACK_BUTTON` - missing
- `SDL_HINT_APPLE_TV_CONTROLLER_UI_EVENTS` - missing
- `SDL_HINT_APPLE_TV_REMOTE_ALLOW_ROTATION` - missing
- `SDL_HINT_APP_ID` - missing
- `SDL_HINT_APP_NAME` - missing
- `SDL_HINT_ASSERT` - missing
- `SDL_HINT_AUDIO_ALSA_DEFAULT_DEVICE` - missing
- `SDL_HINT_AUDIO_ALSA_DEFAULT_PLAYBACK_DEVICE` - missing
- `SDL_HINT_AUDIO_ALSA_DEFAULT_RECORDING_DEVICE` - missing
- `SDL_HINT_AUDIO_CATEGORY` - missing
- `SDL_HINT_AUDIO_CHANNELS` - missing
- `SDL_HINT_AUDIO_DEVICE_APP_ICON_NAME` - missing
- `SDL_HINT_AUDIO_DEVICE_RAW_STREAM` - missing
- `SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES` - missing
- `SDL_HINT_AUDIO_DEVICE_STREAM_NAME` - missing
- `SDL_HINT_AUDIO_DEVICE_STREAM_ROLE` - missing
- `SDL_HINT_AUDIO_DISK_INPUT_FILE` - missing
- `SDL_HINT_AUDIO_DISK_OUTPUT_FILE` - missing
- `SDL_HINT_AUDIO_DISK_TIMESCALE` - missing
- `SDL_HINT_AUDIO_DRIVER` - missing
- `SDL_HINT_AUDIO_DUCK_OTHERS` - missing
- `SDL_HINT_AUDIO_DUMMY_TIMESCALE` - missing
- `SDL_HINT_AUDIO_FORMAT` - missing
- `SDL_HINT_AUDIO_FREQUENCY` - missing
- `SDL_HINT_AUDIO_INCLUDE_MONITORS` - missing
- `SDL_HINT_AUTO_UPDATE_JOYSTICKS` - missing
- `SDL_HINT_AUTO_UPDATE_SENSORS` - missing
- `SDL_HINT_BMP_SAVE_LEGACY_FORMAT` - missing
- `SDL_HINT_CAMERA_DRIVER` - missing
- `SDL_HINT_CPU_FEATURE_MASK` - missing
- `SDL_HINT_DISPLAY_USABLE_BOUNDS` - missing
- `SDL_HINT_DOS_ALLOW_DIRECT_FRAMEBUFFER` - missing
- `SDL_HINT_EGL_LIBRARY` - missing
- `SDL_HINT_EMSCRIPTEN_ASYNCIFY` - missing
- `SDL_HINT_EMSCRIPTEN_CANVAS_SELECTOR` - missing
- `SDL_HINT_EMSCRIPTEN_KEYBOARD_ELEMENT` - missing
- `SDL_HINT_ENABLE_SCREEN_KEYBOARD` - missing
- `SDL_HINT_ENABLE_STEAM_SCREEN_KEYBOARD` - missing
- `SDL_HINT_EVDEV_DEVICES` - missing
- `SDL_HINT_EVENT_LOGGING` - missing
- `SDL_HINT_FILE_DIALOG_DRIVER` - missing
- `SDL_HINT_FORCE_RAISEWINDOW` - missing
- `SDL_HINT_FRAMEBUFFER_ACCELERATION` - missing
- `SDL_HINT_GAMECONTROLLERCONFIG` - missing
- `SDL_HINT_GAMECONTROLLERCONFIG_FILE` - missing
- `SDL_HINT_GAMECONTROLLERTYPE` - missing
- `SDL_HINT_GAMECONTROLLER_IGNORE_DEVICES` - missing
- `SDL_HINT_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT` - missing
- `SDL_HINT_GAMECONTROLLER_SENSOR_FUSION` - missing
- `SDL_HINT_GDK_TEXTINPUT_DEFAULT_TEXT` - missing
- `SDL_HINT_GDK_TEXTINPUT_DESCRIPTION` - missing
- `SDL_HINT_GDK_TEXTINPUT_MAX_LENGTH` - missing
- `SDL_HINT_GDK_TEXTINPUT_SCOPE` - missing
- `SDL_HINT_GDK_TEXTINPUT_TITLE` - missing
- `SDL_HINT_GPU_DRIVER` - missing
- `SDL_HINT_HIDAPI_ENUMERATE_ONLY_CONTROLLERS` - missing
- `SDL_HINT_HIDAPI_IGNORE_DEVICES` - missing
- `SDL_HINT_HIDAPI_LIBUSB` - missing
- `SDL_HINT_HIDAPI_LIBUSB_GAMECUBE` - missing
- `SDL_HINT_HIDAPI_LIBUSB_WHITELIST` - missing
- `SDL_HINT_HIDAPI_UDEV` - missing
- `SDL_HINT_IME_IMPLEMENTED_UI` - missing
- `SDL_HINT_INVALID_PARAM_CHECKS` - missing
- `SDL_HINT_IOS_HIDE_HOME_INDICATOR` - missing
- `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS` - missing
- `SDL_HINT_JOYSTICK_ARCADESTICK_DEVICES` - missing
- `SDL_HINT_JOYSTICK_ARCADESTICK_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_BLACKLIST_DEVICES` - missing
- `SDL_HINT_JOYSTICK_BLACKLIST_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_DEVICE` - missing
- `SDL_HINT_JOYSTICK_DIRECTINPUT` - missing
- `SDL_HINT_JOYSTICK_DRUM_DEVICES` - missing
- `SDL_HINT_JOYSTICK_ENHANCED_REPORTS` - missing
- `SDL_HINT_JOYSTICK_FLIGHTSTICK_DEVICES` - missing
- `SDL_HINT_JOYSTICK_FLIGHTSTICK_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_GAMECUBE_DEVICES` - missing
- `SDL_HINT_JOYSTICK_GAMECUBE_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_GAMEINPUT` - missing
- `SDL_HINT_JOYSTICK_GAMEINPUT_RAW` - missing
- `SDL_HINT_JOYSTICK_GUITAR_DEVICES` - missing
- `SDL_HINT_JOYSTICK_HAPTIC_AXES` - missing
- `SDL_HINT_JOYSTICK_HIDAPI` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_8BITDO` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_COMBINE_JOY_CONS` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_GAMECUBE` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_GAMECUBE_RUMBLE_BRAKE` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_GAMESIR` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_GIP` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_GIP_RESET_FOR_METADATA` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_JOYCON_HOME_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_JOY_CONS` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_LG4FF` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_LUNA` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_NINTENDO_CLASSIC` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS3` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS4` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS4_REPORT_INTERVAL` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS5` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_PS5_PLAYER_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SHIELD` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SINPUT` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_STADIA` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_STEAM` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_STEAMDECK` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_STEAM_HOME_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_STEAM_HORI` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SWITCH` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SWITCH2` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SWITCH_HOME_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_SWITCH_PLAYER_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_VERTICAL_JOY_CONS` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_WII` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_WII_PLAYER_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX_360` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX_360_PLAYER_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX_360_WIRELESS` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX_ONE` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_XBOX_ONE_HOME_LED` - missing
- `SDL_HINT_JOYSTICK_HIDAPI_ZUIKI` - missing
- `SDL_HINT_JOYSTICK_IOKIT` - missing
- `SDL_HINT_JOYSTICK_LINUX_CLASSIC` - missing
- `SDL_HINT_JOYSTICK_LINUX_DEADZONES` - missing
- `SDL_HINT_JOYSTICK_LINUX_DIGITAL_HATS` - missing
- `SDL_HINT_JOYSTICK_LINUX_HAT_DEADZONES` - missing
- `SDL_HINT_JOYSTICK_MFI` - missing
- `SDL_HINT_JOYSTICK_RAWINPUT` - missing
- `SDL_HINT_JOYSTICK_RAWINPUT_CORRELATE_XINPUT` - missing
- `SDL_HINT_JOYSTICK_ROG_CHAKRAM` - missing
- `SDL_HINT_JOYSTICK_THREAD` - missing
- `SDL_HINT_JOYSTICK_THROTTLE_DEVICES` - missing
- `SDL_HINT_JOYSTICK_THROTTLE_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_WGI` - missing
- `SDL_HINT_JOYSTICK_WHEEL_DEVICES` - missing
- `SDL_HINT_JOYSTICK_WHEEL_DEVICES_EXCLUDED` - missing
- `SDL_HINT_JOYSTICK_ZERO_CENTERED_DEVICES` - missing
- `SDL_HINT_KEYCODE_OPTIONS` - missing
- `SDL_HINT_KMSDRM_ATOMIC` - missing
- `SDL_HINT_KMSDRM_DEVICE_INDEX` - missing
- `SDL_HINT_KMSDRM_REQUIRE_DRM_MASTER` - missing
- `SDL_HINT_LOGGING` - missing
- `SDL_HINT_MAC_BACKGROUND_APP` - missing
- `SDL_HINT_MAC_CTRL_CLICK_EMULATE_RIGHT_CLICK` - missing
- `SDL_HINT_MAC_OPENGL_ASYNC_DISPATCH` - missing
- `SDL_HINT_MAC_OPTION_AS_ALT` - missing
- `SDL_HINT_MAC_PRESS_AND_HOLD` - missing
- `SDL_HINT_MAC_SCROLL_MOMENTUM` - missing
- `SDL_HINT_MAIN_CALLBACK_RATE` - missing
- `SDL_HINT_MOUSE_AUTO_CAPTURE` - missing
- `SDL_HINT_MOUSE_DEFAULT_SYSTEM_CURSOR` - missing
- `SDL_HINT_MOUSE_DOUBLE_CLICK_RADIUS` - missing
- `SDL_HINT_MOUSE_DOUBLE_CLICK_TIME` - missing
- `SDL_HINT_MOUSE_DPI_SCALE_CURSORS` - missing
- `SDL_HINT_MOUSE_EMULATE_WARP_WITH_RELATIVE` - missing
- `SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH` - missing
- `SDL_HINT_MOUSE_NORMAL_SPEED_SCALE` - missing
- `SDL_HINT_MOUSE_RELATIVE_CURSOR_VISIBLE` - missing
- `SDL_HINT_MOUSE_RELATIVE_MODE_CENTER` - missing
- `SDL_HINT_MOUSE_RELATIVE_SPEED_SCALE` - missing
- `SDL_HINT_MOUSE_RELATIVE_SYSTEM_SCALE` - missing
- `SDL_HINT_MOUSE_RELATIVE_WARP_MOTION` - missing
- `SDL_HINT_MOUSE_TOUCH_EVENTS` - missing
- `SDL_HINT_MUTE_CONSOLE_KEYBOARD` - missing
- `SDL_HINT_NO_SIGNAL_HANDLERS` - missing
- `SDL_HINT_OPENGL_ES_DRIVER` - missing
- `SDL_HINT_OPENGL_FORCE_SRGB_CAPABLE` - missing
- `SDL_HINT_OPENGL_FORCE_SRGB_FRAMEBUFFER` - missing
- `SDL_HINT_OPENGL_LIBRARY` - missing
- `SDL_HINT_OPENVR_LIBRARY` - missing
- `SDL_HINT_OPENXR_LIBRARY` - missing
- `SDL_HINT_ORIENTATIONS` - missing
- `SDL_HINT_PEN_MOUSE_EVENTS` - missing
- `SDL_HINT_PEN_TOUCH_EVENTS` - missing
- `SDL_HINT_POLL_SENTINEL` - missing
- `SDL_HINT_PREFERRED_LOCALES` - missing
- `SDL_HINT_PS2_GS_HEIGHT` - missing
- `SDL_HINT_PS2_GS_MODE` - missing
- `SDL_HINT_PS2_GS_PROGRESSIVE` - missing
- `SDL_HINT_PS2_GS_WIDTH` - missing
- `SDL_HINT_QUIT_ON_LAST_WINDOW_CLOSE` - missing
- `SDL_HINT_RENDER_DIRECT3D11_DEBUG` - missing
- `SDL_HINT_RENDER_DIRECT3D11_WARP` - missing
- `SDL_HINT_RENDER_DIRECT3D_THREADSAFE` - missing
- `SDL_HINT_RENDER_DRIVER` - missing
- `SDL_HINT_RENDER_GPU_DEBUG` - missing
- `SDL_HINT_RENDER_GPU_LOW_POWER` - missing
- `SDL_HINT_RENDER_LINE_METHOD` - missing
- `SDL_HINT_RENDER_METAL_PREFER_LOW_POWER_DEVICE` - missing
- `SDL_HINT_RENDER_VSYNC` - missing
- `SDL_HINT_RENDER_VULKAN_DEBUG` - missing
- `SDL_HINT_RETURN_KEY_HIDES_IME` - missing
- `SDL_HINT_ROG_GAMEPAD_MICE` - missing
- `SDL_HINT_ROG_GAMEPAD_MICE_EXCLUDED` - missing
- `SDL_HINT_RPI_VIDEO_LAYER` - missing
- `SDL_HINT_SCREENSAVER_INHIBIT_ACTIVITY_NAME` - missing
- `SDL_HINT_SHUTDOWN_DBUS_ON_QUIT` - missing
- `SDL_HINT_STORAGE_TITLE_DRIVER` - missing
- `SDL_HINT_STORAGE_USER_DRIVER` - missing
- `SDL_HINT_THREAD_FORCE_REALTIME_TIME_CRITICAL` - missing
- `SDL_HINT_THREAD_PRIORITY_POLICY` - missing
- `SDL_HINT_TIMER_RESOLUTION` - missing
- `SDL_HINT_TOUCH_MOUSE_EVENTS` - missing
- `SDL_HINT_TRACKPAD_IS_TOUCH_ONLY` - missing
- `SDL_HINT_TV_REMOTE_AS_JOYSTICK` - missing
- `SDL_HINT_VIDEO_ALLOW_SCREENSAVER` - missing
- `SDL_HINT_VIDEO_DISPLAY_PRIORITY` - missing
- `SDL_HINT_VIDEO_DOUBLE_BUFFER` - missing
- `SDL_HINT_VIDEO_DRIVER` - missing
- `SDL_HINT_VIDEO_DUMMY_SAVE_FRAMES` - missing
- `SDL_HINT_VIDEO_EGL_ALLOW_GETDISPLAY_FALLBACK` - missing
- `SDL_HINT_VIDEO_FORCE_EGL` - missing
- `SDL_HINT_VIDEO_MAC_FULLSCREEN_MENU_VISIBILITY` - missing
- `SDL_HINT_VIDEO_MAC_FULLSCREEN_SPACES` - missing
- `SDL_HINT_VIDEO_MATCH_EXCLUSIVE_MODE_ON_MOVE` - missing
- `SDL_HINT_VIDEO_METAL_AUTO_RESIZE_DRAWABLE` - missing
- `SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS` - missing
- `SDL_HINT_VIDEO_OFFSCREEN_SAVE_FRAMES` - missing
- `SDL_HINT_VIDEO_SYNC_WINDOW_OPERATIONS` - missing
- `SDL_HINT_VIDEO_WAYLAND_ALLOW_LIBDECOR` - missing
- `SDL_HINT_VIDEO_WAYLAND_MODE_EMULATION` - missing
- `SDL_HINT_VIDEO_WAYLAND_MODE_SCALING` - missing
- `SDL_HINT_VIDEO_WAYLAND_PREFER_LIBDECOR` - missing
- `SDL_HINT_VIDEO_WAYLAND_SCALE_TO_DISPLAY` - missing
- `SDL_HINT_VIDEO_WIN_D3DCOMPILER` - missing
- `SDL_HINT_VIDEO_X11_ENABLE_XSYNC_EXT` - missing
- `SDL_HINT_VIDEO_X11_EXTERNAL_WINDOW_INPUT` - missing
- `SDL_HINT_VIDEO_X11_NET_WM_BYPASS_COMPOSITOR` - missing
- `SDL_HINT_VIDEO_X11_NET_WM_PING` - missing
- `SDL_HINT_VIDEO_X11_NODIRECTCOLOR` - missing
- `SDL_HINT_VIDEO_X11_SCALING_FACTOR` - missing
- `SDL_HINT_VIDEO_X11_VISUALID` - missing
- `SDL_HINT_VIDEO_X11_WINDOW_VISUALID` - missing
- `SDL_HINT_VIDEO_X11_XRANDR` - missing
- `SDL_HINT_VITA_ENABLE_BACK_TOUCH` - missing
- `SDL_HINT_VITA_ENABLE_FRONT_TOUCH` - missing
- `SDL_HINT_VITA_MODULE_PATH` - missing
- `SDL_HINT_VITA_PVR_INIT` - missing
- `SDL_HINT_VITA_PVR_OPENGL` - missing
- `SDL_HINT_VITA_RESOLUTION` - missing
- `SDL_HINT_VITA_TOUCH_MOUSE_DEVICE` - missing
- `SDL_HINT_VULKAN_DISPLAY` - missing
- `SDL_HINT_VULKAN_LIBRARY` - missing
- `SDL_HINT_WAVE_CHUNK_LIMIT` - missing
- `SDL_HINT_WAVE_FACT_CHUNK` - missing
- `SDL_HINT_WAVE_RIFF_CHUNK_SIZE` - missing
- `SDL_HINT_WAVE_TRUNCATION` - missing
- `SDL_HINT_WINDOWS_CLOSE_ON_ALT_F4` - missing
- `SDL_HINT_WINDOWS_ENABLE_MENU_MNEMONICS` - missing
- `SDL_HINT_WINDOWS_ENABLE_MESSAGELOOP` - missing
- `SDL_HINT_WINDOWS_ERASE_BACKGROUND_MODE` - missing
- `SDL_HINT_WINDOWS_FORCE_SEMAPHORE_KERNEL` - missing
- `SDL_HINT_WINDOWS_GAMEINPUT` - missing
- `SDL_HINT_WINDOWS_INTRESOURCE_ICON` - missing
- `SDL_HINT_WINDOWS_INTRESOURCE_ICON_SMALL` - missing
- `SDL_HINT_WINDOWS_RAW_KEYBOARD` - missing
- `SDL_HINT_WINDOWS_RAW_KEYBOARD_EXCLUDE_HOTKEYS` - missing
- `SDL_HINT_WINDOWS_RAW_KEYBOARD_INPUTSINK` - missing
- `SDL_HINT_WINDOWS_USE_D3D9EX` - missing
- `SDL_HINT_WINDOW_ACTIVATE_WHEN_RAISED` - missing
- `SDL_HINT_WINDOW_ACTIVATE_WHEN_SHOWN` - missing
- `SDL_HINT_WINDOW_ALLOW_TOPMOST` - missing
- `SDL_HINT_WINDOW_FRAME_USABLE_WHILE_CURSOR_HIDDEN` - missing
- `SDL_HINT_X11_FORCE_OVERRIDE_REDIRECT` - missing
- `SDL_HINT_X11_WINDOW_TYPE` - missing
- `SDL_HINT_X11_XCB_LIBRARY` - missing
- `SDL_HINT_XINPUT_ENABLED` - missing
- `SDL_ICONV_E2BIG` - missing
- `SDL_ICONV_EILSEQ` - missing
- `SDL_ICONV_EINVAL` - missing
- `SDL_ICONV_ERROR` - missing
- `SDL_INIT_AUDIO` - covered by renamed Stark constant or internal native use
- `SDL_INIT_CAMERA` - covered by renamed Stark constant or internal native use
- `SDL_INIT_EVENTS` - covered by renamed Stark constant or internal native use
- `SDL_INIT_GAMEPAD` - covered by renamed Stark constant or internal native use
- `SDL_INIT_HAPTIC` - covered by renamed Stark constant or internal native use
- `SDL_INIT_INTERFACE` - missing
- `SDL_INIT_JOYSTICK` - covered by renamed Stark constant or internal native use
- `SDL_INIT_SENSOR` - covered by renamed Stark constant or internal native use
- `SDL_INIT_VIDEO` - covered by renamed Stark constant or internal native use
- `SDL_INLINE` - missing
- `SDL_INOUT_Z_CAP` - missing
- `SDL_INVALID_UNICODE_CODEPOINT` - missing
- `SDL_IN_BYTECAP` - missing
- `SDL_ISCOLORSPACE_FULL_RANGE` - missing
- `SDL_ISCOLORSPACE_LIMITED_RANGE` - missing
- `SDL_ISCOLORSPACE_MATRIX_BT2020_NCL` - missing
- `SDL_ISCOLORSPACE_MATRIX_BT601` - missing
- `SDL_ISCOLORSPACE_MATRIX_BT709` - missing
- `SDL_ISPIXELFORMAT_10BIT` - missing
- `SDL_ISPIXELFORMAT_ALPHA` - missing
- `SDL_ISPIXELFORMAT_ARRAY` - missing
- `SDL_ISPIXELFORMAT_FLOAT` - missing
- `SDL_ISPIXELFORMAT_FOURCC` - missing
- `SDL_ISPIXELFORMAT_INDEXED` - missing
- `SDL_ISPIXELFORMAT_PACKED` - missing
- `SDL_InvalidParamError` - missing
- `SDL_JOYSTICK_AXIS_MAX` - missing
- `SDL_JOYSTICK_AXIS_MIN` - missing
- `SDL_KMOD_ALT` - missing
- `SDL_KMOD_CAPS` - missing
- `SDL_KMOD_CTRL` - missing
- `SDL_KMOD_GUI` - missing
- `SDL_KMOD_LALT` - missing
- `SDL_KMOD_LCTRL` - missing
- `SDL_KMOD_LEVEL5` - missing
- `SDL_KMOD_LGUI` - missing
- `SDL_KMOD_LSHIFT` - missing
- `SDL_KMOD_MODE` - missing
- `SDL_KMOD_NONE` - missing
- `SDL_KMOD_NUM` - missing
- `SDL_KMOD_RALT` - missing
- `SDL_KMOD_RCTRL` - missing
- `SDL_KMOD_RGUI` - missing
- `SDL_KMOD_RSHIFT` - missing
- `SDL_KMOD_SCROLL` - missing
- `SDL_KMOD_SHIFT` - missing
- `SDL_LASX_INTRINSICS` - missing
- `SDL_LIL_ENDIAN` - missing
- `SDL_LINE` - missing
- `SDL_LSX_INTRINSICS` - missing
- `SDL_MAIN_AVAILABLE` - missing
- `SDL_MAIN_HANDLED` - missing
- `SDL_MAIN_NEEDED` - missing
- `SDL_MAIN_USE_CALLBACKS` - missing
- `SDL_MAJOR_VERSION` - missing
- `SDL_MALLOC` - missing
- `SDL_MAX_SINT16` - missing
- `SDL_MAX_SINT32` - missing
- `SDL_MAX_SINT64` - missing
- `SDL_MAX_SINT8` - missing
- `SDL_MAX_TIME` - missing
- `SDL_MAX_UINT16` - missing
- `SDL_MAX_UINT32` - missing
- `SDL_MAX_UINT64` - missing
- `SDL_MAX_UINT8` - missing
- `SDL_MESSAGEBOX_BUTTONS_LEFT_TO_RIGHT` - missing
- `SDL_MESSAGEBOX_BUTTONS_RIGHT_TO_LEFT` - missing
- `SDL_MESSAGEBOX_BUTTON_ESCAPEKEY_DEFAULT` - missing
- `SDL_MESSAGEBOX_BUTTON_RETURNKEY_DEFAULT` - missing
- `SDL_MESSAGEBOX_ERROR` - missing
- `SDL_MESSAGEBOX_INFORMATION` - missing
- `SDL_MESSAGEBOX_WARNING` - missing
- `SDL_MICRO_VERSION` - missing
- `SDL_MINOR_VERSION` - missing
- `SDL_MIN_SINT16` - missing
- `SDL_MIN_SINT32` - missing
- `SDL_MIN_SINT64` - missing
- `SDL_MIN_SINT8` - missing
- `SDL_MIN_TIME` - missing
- `SDL_MIN_UINT16` - missing
- `SDL_MIN_UINT32` - missing
- `SDL_MIN_UINT64` - missing
- `SDL_MIN_UINT8` - missing
- `SDL_MMX_INTRINSICS` - missing
- `SDL_MOUSE_TOUCHID` - missing
- `SDL_MS_PER_SECOND` - missing
- `SDL_MS_TO_NS` - missing
- `SDL_MUSTLOCK` - missing
- `SDL_MemoryBarrierAcquire` - missing
- `SDL_MemoryBarrierRelease` - missing
- `SDL_NEON_INTRINSICS` - missing
- `SDL_NODISCARD` - missing
- `SDL_NOLONGLONG` - missing
- `SDL_NORETURN` - missing
- `SDL_NO_THREAD_SAFETY_ANALYSIS` - missing
- `SDL_NS_PER_MS` - missing
- `SDL_NS_PER_SECOND` - missing
- `SDL_NS_PER_US` - missing
- `SDL_NS_TO_MS` - missing
- `SDL_NS_TO_SECONDS` - missing
- `SDL_NS_TO_US` - missing
- `SDL_NULL_WHILE_LOOP_CONDITION` - missing
- `SDL_OUT_BYTECAP` - missing
- `SDL_OUT_CAP` - missing
- `SDL_OUT_Z_BYTECAP` - missing
- `SDL_OUT_Z_CAP` - missing
- `SDL_PEN_INPUT_BUTTON_1` - missing
- `SDL_PEN_INPUT_BUTTON_2` - missing
- `SDL_PEN_INPUT_BUTTON_3` - missing
- `SDL_PEN_INPUT_BUTTON_4` - missing
- `SDL_PEN_INPUT_BUTTON_5` - missing
- `SDL_PEN_INPUT_DOWN` - missing
- `SDL_PEN_INPUT_ERASER_TIP` - missing
- `SDL_PEN_INPUT_IN_PROXIMITY` - missing
- `SDL_PEN_MOUSEID` - missing
- `SDL_PEN_TOUCHID` - missing
- `SDL_PIXELFLAG` - missing
- `SDL_PIXELLAYOUT` - missing
- `SDL_PIXELORDER` - missing
- `SDL_PIXELTYPE` - missing
- `SDL_PI_D` - missing
- `SDL_PI_F` - missing
- `SDL_PLATFORM_3DS` - missing
- `SDL_PLATFORM_AIX` - missing
- `SDL_PLATFORM_ANDROID` - missing
- `SDL_PLATFORM_APPLE` - missing
- `SDL_PLATFORM_BSDI` - missing
- `SDL_PLATFORM_CYGWIN` - missing
- `SDL_PLATFORM_DOS` - missing
- `SDL_PLATFORM_EMSCRIPTEN` - missing
- `SDL_PLATFORM_FREEBSD` - missing
- `SDL_PLATFORM_GDK` - missing
- `SDL_PLATFORM_HAIKU` - missing
- `SDL_PLATFORM_HPUX` - missing
- `SDL_PLATFORM_HURD` - missing
- `SDL_PLATFORM_IOS` - missing
- `SDL_PLATFORM_IRIX` - missing
- `SDL_PLATFORM_LINUX` - missing
- `SDL_PLATFORM_MACOS` - missing
- `SDL_PLATFORM_NETBSD` - missing
- `SDL_PLATFORM_NGAGE` - missing
- `SDL_PLATFORM_OPENBSD` - missing
- `SDL_PLATFORM_OS2` - missing
- `SDL_PLATFORM_OSF` - missing
- `SDL_PLATFORM_PS2` - missing
- `SDL_PLATFORM_PSP` - missing
- `SDL_PLATFORM_QNXNTO` - missing
- `SDL_PLATFORM_RISCOS` - missing
- `SDL_PLATFORM_SOLARIS` - missing
- `SDL_PLATFORM_TVOS` - missing
- `SDL_PLATFORM_UNIX` - missing
- `SDL_PLATFORM_VISIONOS` - missing
- `SDL_PLATFORM_VITA` - missing
- `SDL_PLATFORM_WIN32` - missing
- `SDL_PLATFORM_WINDOWS` - missing
- `SDL_PLATFORM_WINGDK` - missing
- `SDL_PLATFORM_XBOXONE` - missing
- `SDL_PLATFORM_XBOXSERIES` - missing
- `SDL_PRILLX` - missing
- `SDL_PRILL_PREFIX` - missing
- `SDL_PRILLd` - missing
- `SDL_PRILLu` - missing
- `SDL_PRILLx` - missing
- `SDL_PRINTF_FORMAT_STRING` - missing
- `SDL_PRINTF_VARARG_FUNC` - missing
- `SDL_PRINTF_VARARG_FUNCV` - missing
- `SDL_PRIX32` - missing
- `SDL_PRIX64` - missing
- `SDL_PRIs32` - missing
- `SDL_PRIs64` - missing
- `SDL_PRIu32` - missing
- `SDL_PRIu64` - missing
- `SDL_PRIx32` - missing
- `SDL_PRIx64` - missing
- `SDL_PROP_APP_METADATA_COPYRIGHT_STRING` - missing
- `SDL_PROP_APP_METADATA_CREATOR_STRING` - missing
- `SDL_PROP_APP_METADATA_IDENTIFIER_STRING` - missing
- `SDL_PROP_APP_METADATA_NAME_STRING` - missing
- `SDL_PROP_APP_METADATA_TYPE_STRING` - missing
- `SDL_PROP_APP_METADATA_URL_STRING` - missing
- `SDL_PROP_APP_METADATA_VERSION_STRING` - missing
- `SDL_PROP_AUDIOSTREAM_AUTO_CLEANUP_BOOLEAN` - missing
- `SDL_PROP_DISPLAY_HDR_ENABLED_BOOLEAN` - missing
- `SDL_PROP_DISPLAY_KMSDRM_PANEL_ORIENTATION_NUMBER` - missing
- `SDL_PROP_DISPLAY_WAYLAND_WL_OUTPUT_POINTER` - missing
- `SDL_PROP_DISPLAY_WINDOWS_HMONITOR_POINTER` - missing
- `SDL_PROP_FILE_DIALOG_ACCEPT_STRING` - missing
- `SDL_PROP_FILE_DIALOG_CANCEL_STRING` - missing
- `SDL_PROP_FILE_DIALOG_FILTERS_POINTER` - missing
- `SDL_PROP_FILE_DIALOG_LOCATION_STRING` - missing
- `SDL_PROP_FILE_DIALOG_MANY_BOOLEAN` - missing
- `SDL_PROP_FILE_DIALOG_NFILTERS_NUMBER` - missing
- `SDL_PROP_FILE_DIALOG_TITLE_STRING` - missing
- `SDL_PROP_FILE_DIALOG_WINDOW_POINTER` - missing
- `SDL_PROP_GAMEPAD_CAP_MONO_LED_BOOLEAN` - missing
- `SDL_PROP_GAMEPAD_CAP_PLAYER_LED_BOOLEAN` - missing
- `SDL_PROP_GAMEPAD_CAP_RGB_LED_BOOLEAN` - missing
- `SDL_PROP_GAMEPAD_CAP_RUMBLE_BOOLEAN` - missing
- `SDL_PROP_GAMEPAD_CAP_TRIGGER_RUMBLE_BOOLEAN` - missing
- `SDL_PROP_GLOBAL_NOTIFICATION_HEADER_ICON_STRING` - missing
- `SDL_PROP_GLOBAL_SYSTEM_UBUNTU_TOUCH_APPID_STRING` - missing
- `SDL_PROP_GLOBAL_SYSTEM_UBUNTU_TOUCH_APP_VERSION_STRING` - missing
- `SDL_PROP_GLOBAL_SYSTEM_UBUNTU_TOUCH_HOOK_STRING` - missing
- `SDL_PROP_GLOBAL_VIDEO_WAYLAND_WL_DISPLAY_POINTER` - missing
- `SDL_PROP_GPU_BUFFER_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_COMPUTEPIPELINE_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_D3D12_AGILITY_SDK_PATH_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_D3D12_AGILITY_SDK_VERSION_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_D3D12_ALLOW_FEWER_RESOURCE_SLOTS_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_D3D12_SEMANTIC_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_DEBUGMODE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_FEATURE_ANISOTROPY_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_FEATURE_CLIP_DISTANCE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_FEATURE_DEPTH_CLAMPING_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_FEATURE_INDIRECT_DRAW_FIRST_INSTANCE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_METAL_ALLOW_MACFAMILY1` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_METAL_ALLOW_MACFAMILY1_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_PREFERLOWPOWER_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_DXBC_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_DXIL_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_METALLIB_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_MSL_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_PRIVATE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_SHADERS_SPIRV_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_VERBOSE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_VULKAN_OPTIONS_POINTER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_VULKAN_REQUIRE_HARDWARE_ACCELERATION` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_VULKAN_REQUIRE_HARDWARE_ACCELERATION_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_APPLICATION_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_APPLICATION_VERSION_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_ENABLE_BOOLEAN` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_ENGINE_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_ENGINE_VERSION_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_EXTENSION_COUNT_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_EXTENSION_NAMES_POINTER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_FORM_FACTOR_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_INSTANCE_POINTER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_LAYER_COUNT_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_LAYER_NAMES_POINTER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_SYSTEM_ID_POINTER` - missing
- `SDL_PROP_GPU_DEVICE_CREATE_XR_VERSION_NUMBER` - missing
- `SDL_PROP_GPU_DEVICE_DRIVER_INFO_STRING` - missing
- `SDL_PROP_GPU_DEVICE_DRIVER_NAME_STRING` - missing
- `SDL_PROP_GPU_DEVICE_DRIVER_VERSION_STRING` - missing
- `SDL_PROP_GPU_DEVICE_NAME_STRING` - missing
- `SDL_PROP_GPU_GRAPHICSPIPELINE_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_SAMPLER_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_SHADER_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_A_FLOAT` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_B_FLOAT` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_DEPTH_FLOAT` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_G_FLOAT` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_R_FLOAT` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_D3D12_CLEAR_STENCIL_NUMBER` - missing
- `SDL_PROP_GPU_TEXTURE_CREATE_NAME_STRING` - missing
- `SDL_PROP_GPU_TRANSFERBUFFER_CREATE_NAME_STRING` - missing
- `SDL_PROP_HIDAPI_LIBUSB_DEVICE_HANDLE_POINTER` - missing
- `SDL_PROP_IOSTREAM_ANDROID_AASSET_POINTER` - missing
- `SDL_PROP_IOSTREAM_DYNAMIC_CHUNKSIZE_NUMBER` - missing
- `SDL_PROP_IOSTREAM_DYNAMIC_MEMORY_POINTER` - missing
- `SDL_PROP_IOSTREAM_FILE_DESCRIPTOR_NUMBER` - missing
- `SDL_PROP_IOSTREAM_MEMORY_FREE_FUNC` - missing
- `SDL_PROP_IOSTREAM_MEMORY_FREE_FUNC_POINTER` - missing
- `SDL_PROP_IOSTREAM_MEMORY_POINTER` - missing
- `SDL_PROP_IOSTREAM_MEMORY_SIZE_NUMBER` - missing
- `SDL_PROP_IOSTREAM_STDIO_FILE_POINTER` - missing
- `SDL_PROP_IOSTREAM_WINDOWS_HANDLE_POINTER` - missing
- `SDL_PROP_JOYSTICK_CAP_MONO_LED_BOOLEAN` - missing
- `SDL_PROP_JOYSTICK_CAP_PLAYER_LED_BOOLEAN` - missing
- `SDL_PROP_JOYSTICK_CAP_RGB_LED_BOOLEAN` - missing
- `SDL_PROP_JOYSTICK_CAP_RUMBLE_BOOLEAN` - missing
- `SDL_PROP_JOYSTICK_CAP_TRIGGER_RUMBLE_BOOLEAN` - missing
- `SDL_PROP_NAME_STRING` - missing
- `SDL_PROP_PROCESS_BACKGROUND_BOOLEAN` - missing
- `SDL_PROP_PROCESS_CREATE_ARGS_POINTER` - missing
- `SDL_PROP_PROCESS_CREATE_BACKGROUND_BOOLEAN` - missing
- `SDL_PROP_PROCESS_CREATE_CMDLINE_STRING` - missing
- `SDL_PROP_PROCESS_CREATE_ENVIRONMENT_POINTER` - missing
- `SDL_PROP_PROCESS_CREATE_STDERR_NUMBER` - missing
- `SDL_PROP_PROCESS_CREATE_STDERR_POINTER` - missing
- `SDL_PROP_PROCESS_CREATE_STDERR_TO_STDOUT_BOOLEAN` - missing
- `SDL_PROP_PROCESS_CREATE_STDIN_NUMBER` - missing
- `SDL_PROP_PROCESS_CREATE_STDIN_POINTER` - missing
- `SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER` - missing
- `SDL_PROP_PROCESS_CREATE_STDOUT_POINTER` - missing
- `SDL_PROP_PROCESS_CREATE_WORKING_DIRECTORY_STRING` - missing
- `SDL_PROP_PROCESS_PID_NUMBER` - missing
- `SDL_PROP_PROCESS_STDERR_POINTER` - missing
- `SDL_PROP_PROCESS_STDIN_POINTER` - missing
- `SDL_PROP_PROCESS_STDOUT_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_GPU_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_GPU_SHADERS_DXIL_BOOLEAN` - missing
- `SDL_PROP_RENDERER_CREATE_GPU_SHADERS_MSL_BOOLEAN` - missing
- `SDL_PROP_RENDERER_CREATE_GPU_SHADERS_SPIRV_BOOLEAN` - missing
- `SDL_PROP_RENDERER_CREATE_NAME_STRING` - missing
- `SDL_PROP_RENDERER_CREATE_OUTPUT_COLORSPACE_NUMBER` - missing
- `SDL_PROP_RENDERER_CREATE_PRESENT_VSYNC_NUMBER` - missing
- `SDL_PROP_RENDERER_CREATE_SURFACE_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_GRAPHICS_QUEUE_FAMILY_INDEX_NUMBER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_INSTANCE_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_PHYSICAL_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_PRESENT_QUEUE_FAMILY_INDEX_NUMBER` - missing
- `SDL_PROP_RENDERER_CREATE_VULKAN_SURFACE_NUMBER` - missing
- `SDL_PROP_RENDERER_CREATE_WINDOW_POINTER` - missing
- `SDL_PROP_RENDERER_D3D11_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_D3D11_SWAPCHAIN_POINTER` - missing
- `SDL_PROP_RENDERER_D3D12_COMMAND_QUEUE_POINTER` - missing
- `SDL_PROP_RENDERER_D3D12_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_D3D12_SWAPCHAIN_POINTER` - missing
- `SDL_PROP_RENDERER_D3D9_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_GPU_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_HDR_ENABLED_BOOLEAN` - missing
- `SDL_PROP_RENDERER_HDR_HEADROOM_FLOAT` - missing
- `SDL_PROP_RENDERER_MAX_TEXTURE_SIZE_NUMBER` - missing
- `SDL_PROP_RENDERER_NAME_STRING` - missing
- `SDL_PROP_RENDERER_OUTPUT_COLORSPACE_NUMBER` - missing
- `SDL_PROP_RENDERER_SDR_WHITE_POINT_FLOAT` - missing
- `SDL_PROP_RENDERER_SURFACE_POINTER` - missing
- `SDL_PROP_RENDERER_TEXTURE_FORMATS_POINTER` - missing
- `SDL_PROP_RENDERER_TEXTURE_WRAPPING_BOOLEAN` - missing
- `SDL_PROP_RENDERER_VSYNC_NUMBER` - missing
- `SDL_PROP_RENDERER_VULKAN_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_VULKAN_GRAPHICS_QUEUE_FAMILY_INDEX_NUMBER` - missing
- `SDL_PROP_RENDERER_VULKAN_INSTANCE_POINTER` - missing
- `SDL_PROP_RENDERER_VULKAN_PHYSICAL_DEVICE_POINTER` - missing
- `SDL_PROP_RENDERER_VULKAN_PRESENT_QUEUE_FAMILY_INDEX_NUMBER` - missing
- `SDL_PROP_RENDERER_VULKAN_SURFACE_NUMBER` - missing
- `SDL_PROP_RENDERER_VULKAN_SWAPCHAIN_IMAGE_COUNT_NUMBER` - missing
- `SDL_PROP_RENDERER_WINDOW_POINTER` - missing
- `SDL_PROP_SURFACE_HDR_HEADROOM_FLOAT` - missing
- `SDL_PROP_SURFACE_HOTSPOT_X_NUMBER` - missing
- `SDL_PROP_SURFACE_HOTSPOT_Y_NUMBER` - missing
- `SDL_PROP_SURFACE_ROTATION_FLOAT` - missing
- `SDL_PROP_SURFACE_SDR_WHITE_POINT_FLOAT` - missing
- `SDL_PROP_SURFACE_TONEMAP_OPERATOR_STRING` - missing
- `SDL_PROP_TEXTINPUT_ANDROID_INPUTTYPE_NUMBER` - missing
- `SDL_PROP_TEXTINPUT_AUTOCORRECT_BOOLEAN` - missing
- `SDL_PROP_TEXTINPUT_CAPITALIZATION_NUMBER` - missing
- `SDL_PROP_TEXTINPUT_DEFAULT_TEXT_STRING` - missing
- `SDL_PROP_TEXTINPUT_MAX_LENGTH_NUMBER` - missing
- `SDL_PROP_TEXTINPUT_MULTILINE_BOOLEAN` - missing
- `SDL_PROP_TEXTINPUT_PLACEHOLDER_STRING` - missing
- `SDL_PROP_TEXTINPUT_TITLE_STRING` - missing
- `SDL_PROP_TEXTINPUT_TYPE_NUMBER` - missing
- `SDL_PROP_TEXTURE_ACCESS_NUMBER` - missing
- `SDL_PROP_TEXTURE_COLORSPACE_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_ACCESS_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_COLORSPACE_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D11_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D11_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D11_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D12_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D12_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_D3D12_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_FORMAT_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_GPU_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_GPU_TEXTURE_UV_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_GPU_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_GPU_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_HDR_HEADROOM_FLOAT` - missing
- `SDL_PROP_TEXTURE_CREATE_HEIGHT_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_METAL_PIXELBUFFER_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGLES2_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGLES2_TEXTURE_UV_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGLES2_TEXTURE_U_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGLES2_TEXTURE_V_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGL_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGL_TEXTURE_UV_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGL_TEXTURE_U_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_OPENGL_TEXTURE_V_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_PALETTE_POINTER` - missing
- `SDL_PROP_TEXTURE_CREATE_SDR_WHITE_POINT_FLOAT` - missing
- `SDL_PROP_TEXTURE_CREATE_VULKAN_LAYOUT_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_VULKAN_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_CREATE_WIDTH_NUMBER` - missing
- `SDL_PROP_TEXTURE_D3D11_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_D3D11_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_D3D11_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_D3D12_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_D3D12_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_D3D12_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_FORMAT_NUMBER` - missing
- `SDL_PROP_TEXTURE_GPU_TEXTURE_POINTER` - missing
- `SDL_PROP_TEXTURE_GPU_TEXTURE_UV_POINTER` - missing
- `SDL_PROP_TEXTURE_GPU_TEXTURE_U_POINTER` - missing
- `SDL_PROP_TEXTURE_GPU_TEXTURE_V_POINTER` - missing
- `SDL_PROP_TEXTURE_HDR_HEADROOM_FLOAT` - missing
- `SDL_PROP_TEXTURE_HEIGHT_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGLES2_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGLES2_TEXTURE_TARGET_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGLES2_TEXTURE_UV_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGLES2_TEXTURE_U_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGLES2_TEXTURE_V_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEXTURE_TARGET_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEXTURE_UV_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEXTURE_U_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEXTURE_V_NUMBER` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEX_H_FLOAT` - missing
- `SDL_PROP_TEXTURE_OPENGL_TEX_W_FLOAT` - missing
- `SDL_PROP_TEXTURE_SDR_WHITE_POINT_FLOAT` - missing
- `SDL_PROP_TEXTURE_VULKAN_TEXTURE_NUMBER` - missing
- `SDL_PROP_TEXTURE_WIDTH_NUMBER` - missing
- `SDL_PROP_THREAD_CREATE_ENTRY_FUNCTION_POINTER` - missing
- `SDL_PROP_THREAD_CREATE_NAME_STRING` - missing
- `SDL_PROP_THREAD_CREATE_STACKSIZE_NUMBER` - missing
- `SDL_PROP_THREAD_CREATE_USERDATA_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_DOUBLECLICK_CALLBACK_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_ICON_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_LEFTCLICK_CALLBACK_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_MIDDLECLICK_CALLBACK_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_RIGHTCLICK_CALLBACK_POINTER` - missing
- `SDL_PROP_TRAY_CREATE_TOOLTIP_STRING` - missing
- `SDL_PROP_TRAY_CREATE_USERDATA_POINTER` - missing
- `SDL_PROP_WINDOW_ANDROID_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_COCOA_METAL_VIEW_TAG_NUMBER` - missing
- `SDL_PROP_WINDOW_COCOA_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_ALWAYS_ON_TOP_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_BORDERLESS_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_COCOA_VIEW_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_COCOA_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_CONSTRAIN_POPUP_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_CURVATURE_FLOAT` - missing
- `SDL_PROP_WINDOW_CREATE_EMSCRIPTEN_CANVAS_ID` - missing
- `SDL_PROP_WINDOW_CREATE_EMSCRIPTEN_CANVAS_ID_STRING` - missing
- `SDL_PROP_WINDOW_CREATE_EMSCRIPTEN_KEYBOARD_ELEMENT` - missing
- `SDL_PROP_WINDOW_CREATE_EMSCRIPTEN_KEYBOARD_ELEMENT_STRING` - missing
- `SDL_PROP_WINDOW_CREATE_EXTERNAL_GRAPHICS_CONTEXT_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_FLAGS_NUMBER` - missing
- `SDL_PROP_WINDOW_CREATE_FOCUSABLE_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_FULLSCREEN_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER` - missing
- `SDL_PROP_WINDOW_CREATE_HIDDEN_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_HIGH_PIXEL_DENSITY_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_MAXIMIZED_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_MENU_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_METAL_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_MINIMIZED_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_MODAL_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_MOUSE_GRABBED_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_OPENGL_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_PARENT_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_RESIZABLE_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_TITLE_STRING` - missing
- `SDL_PROP_WINDOW_CREATE_TOOLTIP_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_TRANSPARENT_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_UTILITY_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_VISIONOS_SETTINGS_STRING` - missing
- `SDL_PROP_WINDOW_CREATE_VULKAN_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_WAYLAND_CREATE_EGL_WINDOW_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_WAYLAND_SURFACE_ROLE_CUSTOM_BOOLEAN` - missing
- `SDL_PROP_WINDOW_CREATE_WAYLAND_WL_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER` - missing
- `SDL_PROP_WINDOW_CREATE_WIN32_HWND_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_WIN32_PIXEL_FORMAT_HWND_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_WINDOWSCENE_POINTER` - missing
- `SDL_PROP_WINDOW_CREATE_X11_WINDOW_NUMBER` - missing
- `SDL_PROP_WINDOW_CREATE_X_NUMBER` - missing
- `SDL_PROP_WINDOW_CREATE_Y_NUMBER` - missing
- `SDL_PROP_WINDOW_CURVATURE_FLOAT` - missing
- `SDL_PROP_WINDOW_EMSCRIPTEN_CANVAS_ID` - missing
- `SDL_PROP_WINDOW_EMSCRIPTEN_CANVAS_ID_STRING` - missing
- `SDL_PROP_WINDOW_EMSCRIPTEN_KEYBOARD_ELEMENT` - missing
- `SDL_PROP_WINDOW_EMSCRIPTEN_KEYBOARD_ELEMENT_STRING` - missing
- `SDL_PROP_WINDOW_HDR_ENABLED_BOOLEAN` - missing
- `SDL_PROP_WINDOW_HDR_HEADROOM_FLOAT` - missing
- `SDL_PROP_WINDOW_KMSDRM_DEVICE_INDEX_NUMBER` - missing
- `SDL_PROP_WINDOW_KMSDRM_DRM_FD_NUMBER` - missing
- `SDL_PROP_WINDOW_KMSDRM_GBM_DEVICE_POINTER` - missing
- `SDL_PROP_WINDOW_OPENVR_OVERLAY_ID_NUMBER` - missing
- `SDL_PROP_WINDOW_QNX_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_QNX_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_SDR_WHITE_LEVEL_FLOAT` - missing
- `SDL_PROP_WINDOW_SHAPE_POINTER` - missing
- `SDL_PROP_WINDOW_UIKIT_METAL_VIEW_TAG_NUMBER` - missing
- `SDL_PROP_WINDOW_UIKIT_OPENGL_FRAMEBUFFER_NUMBER` - missing
- `SDL_PROP_WINDOW_UIKIT_OPENGL_RENDERBUFFER_NUMBER` - missing
- `SDL_PROP_WINDOW_UIKIT_OPENGL_RESOLVE_FRAMEBUFFER_NUMBER` - missing
- `SDL_PROP_WINDOW_UIKIT_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_VISIONOS_SETTINGS_STRING` - missing
- `SDL_PROP_WINDOW_VIVANTE_DISPLAY_POINTER` - missing
- `SDL_PROP_WINDOW_VIVANTE_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_VIVANTE_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_EGL_WINDOW_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_VIEWPORT_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_XDG_POPUP_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_XDG_POSITIONER_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_XDG_SURFACE_POINTER` - missing
- `SDL_PROP_WINDOW_WAYLAND_XDG_TOPLEVEL_EXPORT_HANDLE_STRING` - missing
- `SDL_PROP_WINDOW_WAYLAND_XDG_TOPLEVEL_POINTER` - missing
- `SDL_PROP_WINDOW_WIN32_HDC_POINTER` - missing
- `SDL_PROP_WINDOW_WIN32_HWND_POINTER` - missing
- `SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER` - missing
- `SDL_PROP_WINDOW_X11_DISPLAY_POINTER` - missing
- `SDL_PROP_WINDOW_X11_SCREEN_NUMBER` - missing
- `SDL_PROP_WINDOW_X11_WINDOW_NUMBER` - missing
- `SDL_PT_GUARDED_BY` - missing
- `SDL_RELEASE` - missing
- `SDL_RELEASE_GENERIC` - missing
- `SDL_RELEASE_SHARED` - missing
- `SDL_RENDERER_VSYNC_ADAPTIVE` - missing
- `SDL_RENDERER_VSYNC_DISABLED` - missing
- `SDL_REQUIRES` - missing
- `SDL_REQUIRES_SHARED` - missing
- `SDL_RESTRICT` - missing
- `SDL_RETURN_CAPABILITY` - missing
- `SDL_REVISION` - missing
- `SDL_SCANCODE_TO_KEYCODE` - missing
- `SDL_SCANF_FORMAT_STRING` - missing
- `SDL_SCANF_VARARG_FUNC` - missing
- `SDL_SCANF_VARARG_FUNCV` - missing
- `SDL_SCOPED_CAPABILITY` - missing
- `SDL_SECONDS_TO_NS` - missing
- `SDL_SINT64_C` - missing
- `SDL_SIZE_MAX` - missing
- `SDL_SOFTWARE_RENDERER` - missing
- `SDL_SSE2_INTRINSICS` - missing
- `SDL_SSE3_INTRINSICS` - missing
- `SDL_SSE4_1_INTRINSICS` - missing
- `SDL_SSE4_2_INTRINSICS` - missing
- `SDL_SSE_INTRINSICS` - missing
- `SDL_STANDARD_GRAVITY` - missing
- `SDL_STRINGIFY_ARG` - missing
- `SDL_SURFACE_LOCKED` - missing
- `SDL_SURFACE_LOCK_NEEDED` - missing
- `SDL_SURFACE_PREALLOCATED` - missing
- `SDL_SURFACE_SIMD_ALIGNED` - missing
- `SDL_SVE2_INTRINSICS` - missing
- `SDL_Swap16BE` - missing
- `SDL_Swap16LE` - missing
- `SDL_Swap32BE` - missing
- `SDL_Swap32LE` - missing
- `SDL_Swap64BE` - missing
- `SDL_Swap64LE` - missing
- `SDL_SwapFloatBE` - missing
- `SDL_SwapFloatLE` - missing
- `SDL_TARGETING` - missing
- `SDL_THREAD_ANNOTATION_ATTRIBUTE__` - missing
- `SDL_TOUCH_MOUSEID` - missing
- `SDL_TRAYENTRY_BUTTON` - missing
- `SDL_TRAYENTRY_CHECKBOX` - missing
- `SDL_TRAYENTRY_CHECKED` - missing
- `SDL_TRAYENTRY_DISABLED` - missing
- `SDL_TRAYENTRY_SUBMENU` - missing
- `SDL_TRY_ACQUIRE` - missing
- `SDL_TRY_ACQUIRE_SHARED` - missing
- `SDL_TriggerBreakpoint` - missing
- `SDL_UINT64_C` - missing
- `SDL_US_PER_SECOND` - missing
- `SDL_US_TO_NS` - missing
- `SDL_Unsupported` - missing
- `SDL_VERSION` - missing
- `SDL_VERSIONNUM` - missing
- `SDL_VERSIONNUM_MAJOR` - missing
- `SDL_VERSIONNUM_MICRO` - missing
- `SDL_VERSIONNUM_MINOR` - missing
- `SDL_VERSION_ATLEAST` - missing
- `SDL_WINAPI_FAMILY_PHONE` - missing
- `SDL_WINDOWPOS_CENTERED` - missing
- `SDL_WINDOWPOS_CENTERED_DISPLAY` - missing
- `SDL_WINDOWPOS_CENTERED_MASK` - missing
- `SDL_WINDOWPOS_ISCENTERED` - missing
- `SDL_WINDOWPOS_ISUNDEFINED` - missing
- `SDL_WINDOWPOS_UNDEFINED` - missing
- `SDL_WINDOWPOS_UNDEFINED_DISPLAY` - missing
- `SDL_WINDOWPOS_UNDEFINED_MASK` - missing
- `SDL_WINDOW_ALWAYS_ON_TOP` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_BORDERLESS` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_EXTERNAL` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_FILL_DOCUMENT` - missing
- `SDL_WINDOW_FULLSCREEN` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_HIDDEN` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_HIGH_PIXEL_DENSITY` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_INPUT_FOCUS` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_KEYBOARD_GRABBED` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MAXIMIZED` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_METAL` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MINIMIZED` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MODAL` - missing
- `SDL_WINDOW_MOUSE_CAPTURE` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MOUSE_FOCUS` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MOUSE_GRABBED` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_MOUSE_RELATIVE_MODE` - missing
- `SDL_WINDOW_NOT_FOCUSABLE` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_OCCLUDED` - missing
- `SDL_WINDOW_OPENGL` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_POPUP_MENU` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_RESIZABLE` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_SURFACE_VSYNC_ADAPTIVE` - missing
- `SDL_WINDOW_SURFACE_VSYNC_DISABLED` - missing
- `SDL_WINDOW_TOOLTIP` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_TRANSPARENT` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_UTILITY` - covered by renamed Stark constant or internal native use
- `SDL_WINDOW_VULKAN` - covered by renamed Stark constant or internal native use
- `SDL_WPRINTF_VARARG_FUNC` - missing
- `SDL_WPRINTF_VARARG_FUNCV` - missing
- `SDL_arraysize` - missing
- `SDL_assert` - missing
- `SDL_assert_always` - missing
- `SDL_assert_paranoid` - missing
- `SDL_assert_release` - missing
- `SDL_clamp` - missing
- `SDL_const_cast` - missing
- `SDL_copyp` - missing
- `SDL_disabled_assert` - missing
- `SDL_enabled_assert` - missing
- `SDL_iconv_utf8_locale` - missing
- `SDL_iconv_utf8_ucs2` - missing
- `SDL_iconv_utf8_ucs4` - missing
- `SDL_iconv_wchar_utf8` - missing
- `SDL_max` - missing
- `SDL_min` - missing
- `SDL_reinterpret_cast` - missing
- `SDL_stack_alloc` - missing
- `SDL_stack_free` - missing
- `SDL_static_cast` - missing
- `SDL_zero` - covered by renamed Stark constant or internal native use
- `SDL_zeroa` - missing
- `SDL_zerop` - missing

### Missing API Groups

- Core lifecycle and error handling: additional app-main callbacks, main/runtime entrypoints, assertion handling, clear/set/get error variants, log categories/priorities/output callbacks, hint callbacks, memory allocation hooks, version/revision macros, and platform detection helpers.
- Video/window/display: full display enumeration and modes, window creation properties, window positioning, sizing constraints, fullscreen modes, opacity, parent/modal relationships, hit testing, surfaces, screen saver, text input, clipboard/primary selection, cursor/mouse capture, EGL/OpenGL/Metal/Vulkan bridge APIs, native platform properties, and all related constants/types.
- Rendering/surfaces/textures/pixels: renderer enumeration/properties, texture creation/properties/update/lock, render geometry/lines/points/rects, render viewport/scale/target/logical presentation, surface creation/conversion/blit/fill/palette/color-key/RLE/alternate-image APIs, pixel format and colorspace helpers, blend modes, rectangles, and every carrier struct/enum.
- GPU: device creation, shader/pipeline/sampler/texture/buffer/transfer-buffer creation, command/copy/render/compute passes, swapchains, fences, uploads/downloads, indirect draw/dispatch, render-state APIs, Vulkan interop, and all GPU structs/enums/flags.
- Audio: device enumeration and formats, logical/physical device management, audio stream creation/binding/callbacks/gain/frequency/channel maps/properties, sample conversion, format helpers, driver enumeration, and complete audio constants/enums.
- Events/input: full `SDL_Event` union, all event kinds, event filters/watchers/state/flush/peep APIs, keyboard/scancode/keycode/mod/text-input APIs, mouse/cursor APIs, touch/pen, joystick, gamepad, virtual joystick/gamepad, sensors, haptics, HIDAPI, and all callback/ID/enum carriers.
- Filesystem/storage/IO/process/async I/O: path helpers, directory create/enumerate/copy/remove/rename, storage backends, IOStream construction/read/write/seek/printf helpers, async IO queues, process creation/properties/wait/IO, loadable shared objects, dialogs, and every callback carrier.
- Threading/synchronization/time: threads, TLS, main-thread callbacks, atomic operations, mutexes, RW locks, semaphores, conditions, timers, high-resolution time, date/time conversion, and all related callback/function-pointer types.
- Properties/environment/locale/system/power/misc: property containers, environment variables, locale preferences, power state, CPU/SIMD/system RAM/page-size queries, Android/iOS/GDK/Windows/X11 hooks, tray APIs, message boxes, and platform-specific native handles.
- Standard-library helpers: SDL memory/string/math/CRC/iconv/sort/search/endian/bits helpers that are public through SDL headers and wiki macros/functions.
- Raw ABI surface: most datatypes, structs, enums, and macros are not represented as Stark ABI carriers or named constants, even where the current safe wrapper uses selected hardcoded values.

### Design Notes

- Keep `Vendor.SDL3` as the safe ergonomic layer, but add a low-level raw namespace for complete SDL symbol coverage. This avoids forcing every SDL program through a high-level API while preserving the existing safe wrappers.
- Several SDL APIs require C callbacks or function-pointer tables: app callbacks, event filters/watchers, audio callbacks, timers, log output, hint callbacks, file dialogs, async IO callbacks, thread entrypoints, cleanup callbacks, tray callbacks, property cleanup callbacks, and platform hooks. These need first-class callback lowering or generated non-allocating C trampolines.
- The current flattened event bridge is useful for simple examples but loses most of `SDL_Event`. A complete binding needs either a full ABI carrier for the union or a generated tagged-safe representation covering every event payload.
- Strings are currently `ascii`; SDL uses UTF-8 for user-facing text. Complete bindings need safe UTF-8/byte-string support rather than expanding ASCII-only wrappers.
- Performance-sensitive paths should expose zero-copy views where SDL lifetime rules allow it, avoid per-call allocations in event/audio/render loops, preserve caller-owned buffers, and keep raw SDL calls direct enough for LLVM to inline wrapper logic around FFI boundaries.
- SDL_image, SDL_ttf, and other satellite libraries should be audited as separate vendor packages if they are added. This section is for core SDL3 plus the symbols visible through the SDL3 public symbol index and core headers.

### Tasks

- [ ] Add generated SDL3 upstream inventory tests:
  - record the supported SDL version, local header revision, and wiki index revision/source date
  - extract the public function, datatype, struct, enum, and macro inventories
  - compare each symbol against `Vendor.SDL3` raw bindings, safe wrappers, explicitly unsupported entries, and pending entries
  - fail C# tests when the supported SDL header/wiki inventory changes without updating this audit
  - add Stark self-hosted source tests for every public API name currently exposed by `Vendor.SDL3`

- [ ] Add a complete low-level SDL3 module boundary:
  - introduce `Vendor.SDL3.Raw` or equivalent for ABI-shaped symbols with original SDL names where Stark naming permits it
  - keep the existing `Vendor.SDL3` safe wrappers as an ergonomic layer over raw bindings
  - expose opaque carriers for every SDL object handle and ABI carriers for every public struct that is passed by value or by pointer
  - add C# ABI layout tests and Stark self-hosted parser/type tests for every carrier

- [ ] Complete core lifecycle, error, logging, hints, assertions, and platform/version APIs:
  - bind all init/main/app-callback/version/error/assert/log/hint/platform/memory-function APIs and constants
  - implement callback/trampoline support for assertion handlers, log output, hint callbacks, app callbacks, and main-thread callbacks
  - test callback ordering, error text lifetime, logging output hooks, hint mutation, init/shutdown idempotence, and version/header parity

- [ ] Complete video, display, window, clipboard, text input, mouse, cursor, and graphics-context APIs:
  - bind all display/window/screen-saver/clipboard/text-input/mouse/cursor APIs and every related constant/type
  - bind OpenGL, EGL, Metal, Vulkan, and native platform window/context helpers
  - test hidden/headless dummy-driver paths, display enumeration, window properties, fullscreen mode selection, clipboard text/data, cursor creation, text input state, and graphics context creation where available

- [ ] Complete renderer, texture, surface, pixel, palette, blend, and rectangle APIs:
  - bind renderer enumeration/properties, texture lifecycle/update/lock/readback, render primitives/geometry, surface creation/conversion/blit/fill/palette/color-key/RLE, pixel format/colorspace helpers, blend modes, and rectangle helpers
  - add safe wrappers for hot render loops that avoid allocation and preserve caller-owned buffers
  - test software renderer paths under dummy/software drivers, texture upload/readback, surface blits/conversions, palette/color-key behavior, and rectangle helper parity

- [ ] Complete SDL GPU APIs:
  - bind device, shader, pipeline, sampler, texture, buffer, transfer buffer, command buffer, render pass, compute pass, copy pass, swapchain, fence, upload/download, indirect draw, render-state, and Vulkan interop APIs
  - expose every GPU struct, enum, and flag with exact ABI layout
  - test headless capability queries, shader format discovery, resource lifecycle, command submission, fence waits, swapchain support checks, and at least one triangle-rendering example when the platform supports it

- [ ] Complete audio APIs:
  - bind audio driver/device enumeration, open/close/pause/resume device APIs, logical/physical device helpers, stream create/bind/callback/gain/frequency/channel-map/properties APIs, sample conversion, format helpers, and all audio constants/enums
  - add UTF-8-safe device-name wrappers and zero-copy byte-buffer paths where SDL permits them
  - test dummy audio device paths, stream conversion, callbacks, channel maps, gain/frequency ratio, pause/resume, and no-allocation byte streaming

- [ ] Complete event and input APIs:
  - bind full event state/filter/watch/peep/flush APIs and expose all `SDL_Event` payloads
  - bind keyboard, scancode/keycode/mod/text input, mouse, touch, pen, joystick, gamepad, virtual joystick/gamepad, sensor, haptic, and HIDAPI APIs
  - add safe owner types for joystick/gamepad/haptic/sensor/HID handles
  - test event queue behavior, every currently supported flattened event, keyboard/mouse state, dummy/virtual joystick paths, gamepad mapping, haptic capability checks, sensor enumeration, and HID feature report paths where hardware/platform support exists

- [ ] Complete filesystem, storage, IOStream, async IO, process, dialog, and shared-object APIs:
  - bind path/file/directory/storage/IOStream/async IO/process/dialog/loadso APIs and callback carriers
  - add safe file and stream owners with deterministic close/drop behavior
  - test temp-directory-backed file operations, memory IO streams, async IO queue completion, process launch/wait/IO, dialog callback shape without requiring user interaction where possible, and shared-object symbol loading

- [ ] Complete threading, synchronization, atomics, timers, and time APIs:
  - bind thread creation/properties/wait/detach/naming/priority/TLS, atomics, mutexes, RW locks, semaphores, conditions, timers, performance counters, date/time conversion, and sleep APIs
  - implement non-unwinding Stark callback/trampoline support for thread and timer entrypoints
  - test TLS destructor behavior, synchronization primitives, timer callbacks, atomics, precise delays, and thread lifecycle cleanup

- [ ] Complete properties, environment, locale, power, CPU/system, tray, message box, and platform-specific APIs:
  - bind property containers, cleanup callbacks, environment APIs, locale, power, CPU/SIMD/system queries, tray/menu APIs, message boxes, notifications where available, and Android/iOS/GDK/Windows/X11 platform hooks
  - gate platform-specific APIs behind compile/runtime availability checks
  - test property type round trips, cleanup callbacks, environment mutation, locale/power queries, message-box property construction, and platform-gated symbol availability

- [ ] Complete SDL standard-helper APIs and macros:
  - bind or reimplement public SDL string/memory/math/sort/search/endian/bits/CRC helpers where they are part of the supported public symbol surface
  - expose generated constants/macros with parity tests against headers
  - test endian swaps, string comparisons/copies, checked size arithmetic, CRC helpers, math helpers, and allocation wrappers

- [ ] Update SDL3 examples:
  - keep the existing window/audio example
  - add renderer texture/surface example, keyboard/mouse input example, clipboard/text-input example, storage/IO example, audio callback example, gamepad/virtual joystick example, threading/timer example, Vulkan/OpenGL context example, GPU triangle example where supported, and process/shared-object examples
  - keep console output as simple `WriteLine(...)` calls without checking every status

- [ ] Add C# compiler/integration tests for every new SDL3 surface:
  - package-image native dependency metadata for `sdl3`
  - native symbol availability by subsystem and platform gate
  - ABI layout tests for every public carrier, callback carrier, union/event representation, and function-pointer table
  - runtime tests under dummy video/audio drivers where possible
  - performance regression tests for event polling, audio byte streaming, renderer hot loops, and raw wrapper overhead

- [ ] Add Stark self-hosted tests for every new SDL3 surface that the current Stark test harness can express:
  - source-level API shape tests for constants, enums, structs, functions, and callback carrier declarations
  - example compile tests for safe wrapper usage
  - native/runtime tests gated on SDL availability as the self-hosted compiler gains those test capabilities
