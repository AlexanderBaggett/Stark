+++
title = "Breakout"
weight = 170
+++

Breakout has two paths: a deterministic headless game-core slice and an
optional Raylib-backed graphical shell.

## Build And Run

Headless core:

```bash
dotnet run --project src -- examples/breakout/BreakoutCore.stark --emit-exe -o examples/breakout/breakout-core
./examples/breakout/breakout-core
```

Raylib shell:

```bash
bash examples/breakout/run-raylib.sh
./examples/breakout/breakout-raylib
```

Expected behavior: the headless core exits with status `0`. The Raylib shell
requires local native graphics dependencies and opens a graphical window.

Status: the core is covered by `ExamplesCompileRunTests.BreakoutCoreExampleCompilesAndRuns`; the Raylib path is checked or package-built without graphical execution.

## Source Files

- [BreakoutCore.stark](samples/BreakoutCore.stark)
- [BreakoutRaylib.stark](samples/BreakoutRaylib.stark)
- [Stark.toml](samples/Stark.toml)

### BreakoutCore.stark

{{< file-sample "samples/BreakoutCore.stark" "stark" >}}

### BreakoutRaylib.stark

{{< file-sample "samples/BreakoutRaylib.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Native Package Project](/book/39-project-native-package/)
- [Raylib example](/examples/raylib/)
