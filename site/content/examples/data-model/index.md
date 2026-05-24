+++
title = "Data Model"
weight = 60
+++

This example shows `struct` and `record` declarations plus field-initializer
syntax with `new Type() { ... }`.

## Build And Run

```bash
dotnet run --project src -- examples/data-model/DataModel.stark --emit-exe -o examples/data-model/data-model
./examples/data-model/data-model
```

Expected behavior: exits with status `15` and no output.

Status: covered by `ExamplesCompileRunTests.DataModelExampleCompilesAndRuns`.

## Source Files

- [DataModel.stark](samples/DataModel.stark)
- [Stark.toml](samples/Stark.toml)

### DataModel.stark

{{< file-sample "samples/DataModel.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Structs, Records, and Layout-Aware Design](/book/12-aggregates-layout/)
- [Language reference](/reference/language/LanguageReference/)
