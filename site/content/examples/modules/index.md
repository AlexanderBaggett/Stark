+++
title = "Modules"
weight = 80
+++

This three-file example imports public APIs from sibling modules while keeping
helpers internal to their defining modules.

## Build And Run

```bash
dotnet run --project src -- examples/modules/App.stark --emit-exe -o examples/modules/app
./examples/modules/app
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.ModulesExampleCompilesAndRuns`.

## Source Files

- [App.stark](samples/App.stark)
- [Geometry.stark](samples/Geometry.stark)
- [Units.stark](samples/Units.stark)
- [Stark.toml](samples/Stark.toml)

### App.stark

{{< file-sample "samples/App.stark" "stark" >}}

### Geometry.stark

{{< file-sample "samples/Geometry.stark" "stark" >}}

### Units.stark

{{< file-sample "samples/Units.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Modules, Visibility, and Packages](/book/15-modules-visibility-packages/)
- [Modules and visibility](/reference/language/ModulesAndVisibility/)
