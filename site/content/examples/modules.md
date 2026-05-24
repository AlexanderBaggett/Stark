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

- [App.stark](/reference/examples/modules/App.stark)
- [Geometry.stark](/reference/examples/modules/Geometry.stark)
- [Units.stark](/reference/examples/modules/Units.stark)
- [Stark.toml](/reference/examples/modules/Stark.toml)

### App.stark

{{< file-sample "static/reference/examples/modules/App.stark" "stark" >}}

### Geometry.stark

{{< file-sample "static/reference/examples/modules/Geometry.stark" "stark" >}}

### Units.stark

{{< file-sample "static/reference/examples/modules/Units.stark" "stark" >}}

### Stark.toml

{{< file-sample "static/reference/examples/modules/Stark.toml" "toml" >}}

## Related

- [Modules, Visibility, and Packages](/book/15-modules-visibility-packages/)
- [Modules and visibility](/reference/language/ModulesAndVisibility/)
