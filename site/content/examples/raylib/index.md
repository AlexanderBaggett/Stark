+++
title = "Raylib"
weight = 180
+++

The ordinary Raylib project consumes the bundled `Vendor.Raylib`,
`Vendor.Raymath`, and `Vendor.Rlgl` SDK packages. This directory also retains
the contributor-facing standalone bindings, which use direct C ABI aggregate
carriers for small by-value Raylib structs.

## Build And Run

Build the SDK-consumer example without local native setup:

```bash
cd examples
stark build raylib
```

Contributors rebuilding the standalone source package can follow the checked-in
README and run:

```bash
bash examples/raylib/build-package.sh
```

That helper is a binding-author/source-checkout workflow and acquires or locates
Raylib as described in the README. An installed release SDK already owns the
advertised `Vendor.Raylib` package and native payload; applications simply
`import Vendor.Raylib` and do not configure `pkg-config` or native paths.
Graphical execution is intentionally manual.

Status: the SDK-consumer project is built on every release target without
graphical execution; contributor binding modules retain their focused compiler
checks.

## Source Files

- README.md (embedded below)
- [Raylib.stark](samples/Raylib.stark)
- [RaylibHeadlessGeometry.stark](samples/RaylibHeadlessGeometry.stark)
- [Raylib/Types.stark](samples/Raylib/Types.stark)
- [Raylib/Core.stark](samples/Raylib/Core.stark)
- [Raylib/Shapes.stark](samples/Raylib/Shapes.stark)
- [Raylib/Textures.stark](samples/Raylib/Textures.stark)
- [Raylib/Text.stark](samples/Raylib/Text.stark)
- [Raylib/Models.stark](samples/Raylib/Models.stark)
- [Raylib/Audio.stark](samples/Raylib/Audio.stark)
- [Raylib.package.args](samples/Raylib.package.args)
- [Stark.toml](samples/Stark.toml)

### README.md

{{< file-sample "samples/README.md" "markdown" >}}

### Raylib.stark

{{< file-sample "samples/Raylib.stark" "stark" >}}

### RaylibHeadlessGeometry.stark

{{< file-sample "samples/RaylibHeadlessGeometry.stark" "stark" >}}

### Raylib/Types.stark

{{< file-sample "samples/Raylib/Types.stark" "stark" >}}

### Raylib/Core.stark

{{< file-sample "samples/Raylib/Core.stark" "stark" >}}

### Raylib/Shapes.stark

{{< file-sample "samples/Raylib/Shapes.stark" "stark" >}}

### Raylib/Textures.stark

{{< file-sample "samples/Raylib/Textures.stark" "stark" >}}

### Raylib/Text.stark

{{< file-sample "samples/Raylib/Text.stark" "stark" >}}

### Raylib/Models.stark

{{< file-sample "samples/Raylib/Models.stark" "stark" >}}

### Raylib/Audio.stark

{{< file-sample "samples/Raylib/Audio.stark" "stark" >}}

### Raylib.package.args

{{< file-sample "samples/Raylib.package.args" "text" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [FFI, Raw Pointers, and Native Packages](/book/20-ffi-raw-pointers-native-packages/)
- [Native Package Project](/book/39-project-native-package/)
