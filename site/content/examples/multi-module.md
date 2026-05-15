+++
title = "Multi-Module"
weight = 90
+++

This example shows cross-module imports with a public helper function in a
separate source file.

## Build And Run

```bash
dotnet run --project src -- examples/multi-module/App.stark --emit-exe -o examples/multi-module/app
./examples/multi-module/app
```

Expected behavior: exits with status `7` and no output.

Status: covered by `ExamplesCompileRunTests.MultiModuleExampleCompilesAndRuns`.

## Source Files

- [App.stark](/reference/examples/multi-module/App.stark)
- [Math.stark](/reference/examples/multi-module/Math.stark)
- [Stark.toml](/reference/examples/multi-module/Stark.toml)

{{< file-sample "static/reference/examples/multi-module/App.stark" "stark" >}}

## Related

- [Multi-Module Package Project](/book/34-project-multi-module-package/)
- [Modules and visibility](/reference/language/ModulesAndVisibility/)
