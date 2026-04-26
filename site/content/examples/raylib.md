+++
title = "Raylib"
weight = 180
+++

The Raylib example is a native-backed library package. The binding is split
into modules such as `Raylib.Core`, `Raylib.Shapes`, `Raylib.Textures`,
`Raylib.Text`, `Raylib.Models`, `Raylib.Audio`, and `Raylib.Types`.

## Build And Run

Follow the local setup notes in the checked-in README:

```bash
bash examples/raylib/build-package.sh
```

Raylib must be available through `pkg-config` or configured native paths.
Graphical execution is intentionally manual.

Status: checked by `ExamplesCompileRunTests.RaylibStarkModulesCheckWithoutNativeExecution` and package-linked by `ExamplesCompileRunTests.BreakoutRaylibBuildsThroughPackageOwnedNativeMetadataWithoutGraphicalExecution`.

## Source Files

- [README.md](/reference/examples/raylib/README.md)
- [Raylib.stark](/reference/examples/raylib/Raylib.stark)
- [RaylibNative.c](/reference/examples/raylib/RaylibNative.c)
- [Raylib.package.args](/reference/examples/raylib/Raylib.package.args)
- [Stark.toml](/reference/examples/raylib/Stark.toml)

{{< file-sample "static/reference/examples/raylib/Raylib.stark" "stark" >}}

## Related

- [FFI, Raw Pointers, and Native Packages](/book/19-ffi-raw-pointers-native-packages/)
- [Native Package Project](/book/33-project-native-package/)
