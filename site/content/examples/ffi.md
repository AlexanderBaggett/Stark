+++
title = "FFI"
weight = 100
+++

The FFI example is the second step after hello world. It imports the C ABI
`abs` function and calls it from Stark code. It is intentionally small so the
ABI boundary is visible without making native interop the first language
example.

## Build And Run

```bash
dotnet run --project src -- examples/ffi/Ffi.stark --emit-exe -o examples/ffi/ffi
./examples/ffi/ffi
```

Expected behavior: exits with status `7` and no output.

Status: covered by `ExamplesCompileRunTests.FfiExampleCompilesAndRuns`.

## Source Files

- [Ffi.stark](/reference/examples/ffi/Ffi.stark)
- [Stark.toml](/reference/examples/ffi/Stark.toml)

{{< file-sample "static/reference/examples/ffi/Ffi.stark" "stark" >}}

## Related

- [FFI, Raw Pointers, and Native Packages](/book/20-ffi-raw-pointers-native-packages/)
- [Language reference](/reference/language/LanguageReference/)
