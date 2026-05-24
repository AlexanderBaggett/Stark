+++
title = "Basic Syntax"
weight = 20
+++

This example tours the first C-family surface area: modules, constants,
functions, stack locals, mutation, `if`, `while`, and `switch`.

## Build And Run

```bash
dotnet run --project src -- examples/basic-syntax/BasicSyntax.stark --emit-exe -o examples/basic-syntax/basic-syntax
./examples/basic-syntax/basic-syntax
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.BasicSyntaxExampleCompilesAndRuns`.

## Source Files

- [BasicSyntax.stark](samples/BasicSyntax.stark)
- [Stark.toml](samples/Stark.toml)

### BasicSyntax.stark

{{< file-sample "samples/BasicSyntax.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [A Small Stark Tour](/book/04-small-tour/)
- [Language reference](/reference/language/LanguageReference/)
