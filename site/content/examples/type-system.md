+++
title = "Type System"
weight = 30
+++

This example focuses on range aliases, constrained fields, records, enums with
payloads, and enum equality.

## Build And Run

```bash
dotnet run --project src -- examples/type-system/TypeSystem.stark --emit-exe -o examples/type-system/type-system
./examples/type-system/type-system
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.TypeSystemExampleCompilesAndRuns`.

## Source Files

- [TypeSystem.stark](/reference/examples/type-system/TypeSystem.stark)
- [Stark.toml](/reference/examples/type-system/Stark.toml)

### TypeSystem.stark

{{< file-sample "static/reference/examples/type-system/TypeSystem.stark" "stark" >}}

### Stark.toml

{{< file-sample "static/reference/examples/type-system/Stark.toml" "toml" >}}

## Related

- [Values, Types, and Ranges](/book/05-values-types-ranges/)
- [Language reference](/reference/language/LanguageReference/)
