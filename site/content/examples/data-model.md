+++
title = "Data Model"
weight = 60
+++

This example shows `struct` and `record` declarations plus object-initializer
syntax with `new Type() { ... }`.

## Build And Run

```bash
dotnet run --project src -- examples/data-model/DataModel.stark --emit-exe -o examples/data-model/data-model
./examples/data-model/data-model
```

Expected behavior: exits with status `15` and no output.

Status: covered by `ExamplesCompileRunTests.DataModelExampleCompilesAndRuns`.

## Source Files

- [DataModel.stark](/reference/examples/data-model/DataModel.stark)
- [Stark.toml](/reference/examples/data-model/Stark.toml)

{{< file-sample "static/reference/examples/data-model/DataModel.stark" "stark" >}}

## Related

- [Aggregates and Layout-Aware Design](/book/12-aggregates-layout/)
- [Language reference](/reference/language/LanguageReference/)
