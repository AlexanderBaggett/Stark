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
- [RaylibSmoke.stark](/reference/examples/raylib/RaylibSmoke.stark)
- [Raylib/Types.stark](/reference/examples/raylib/Raylib/Types.stark)
- [Raylib/Core.stark](/reference/examples/raylib/Raylib/Core.stark)
- [Raylib/Shapes.stark](/reference/examples/raylib/Raylib/Shapes.stark)
- [Raylib/Textures.stark](/reference/examples/raylib/Raylib/Textures.stark)
- [Raylib/Text.stark](/reference/examples/raylib/Raylib/Text.stark)
- [Raylib/Models.stark](/reference/examples/raylib/Raylib/Models.stark)
- [Raylib/Audio.stark](/reference/examples/raylib/Raylib/Audio.stark)
- [RaylibNative.c](/reference/examples/raylib/RaylibNative.c)
- [Raylib.package.args](/reference/examples/raylib/Raylib.package.args)
- [Stark.toml](/reference/examples/raylib/Stark.toml)

### README.md

{{< file-sample "static/reference/examples/raylib/README.md" "markdown" >}}

### Raylib.stark

{{< file-sample "static/reference/examples/raylib/Raylib.stark" "stark" >}}

### RaylibSmoke.stark

{{< file-sample "static/reference/examples/raylib/RaylibSmoke.stark" "stark" >}}

### Raylib/Types.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Types.stark" "stark" >}}

### Raylib/Core.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Core.stark" "stark" >}}

### Raylib/Shapes.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Shapes.stark" "stark" >}}

### Raylib/Textures.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Textures.stark" "stark" >}}

### Raylib/Text.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Text.stark" "stark" >}}

### Raylib/Models.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Models.stark" "stark" >}}

### Raylib/Audio.stark

{{< file-sample "static/reference/examples/raylib/Raylib/Audio.stark" "stark" >}}

### RaylibNative.c

{{< file-sample "static/reference/examples/raylib/RaylibNative.c" "c" >}}

### Raylib.package.args

{{< file-sample "static/reference/examples/raylib/Raylib.package.args" "text" >}}

### Stark.toml

{{< file-sample "static/reference/examples/raylib/Stark.toml" "toml" >}}

## Related

- [FFI, Raw Pointers, and Native Packages](/book/20-ffi-raw-pointers-native-packages/)
- [Native Package Project](/book/39-project-native-package/)
