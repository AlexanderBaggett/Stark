# STB Truetype Example

This example loads the vendored Bitstream Vera TrueType fixture through
`Vendor.STB.Truetype`, reads font and glyph metrics, rasterizes one glyph into
a caller-owned bitmap, and packs a tiny glyph atlas into caller-owned pixels.

Build the vendor package first:

```sh
bash vendor/build-stb-truetype-package.sh
cd examples
stark run stb-truetype
```

Set `STARK_STB_TRUETYPE_FONT_PATH` to point at another `.ttf` file.
