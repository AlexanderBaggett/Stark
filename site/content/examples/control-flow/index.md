+++
title = "Control Flow"
weight = 50
+++

This compact example combines a `while` loop with a `switch` statement and
keeps loop behavior visible in source.

## Build And Run

```bash
dotnet run --project src -- examples/control-flow/ControlFlow.stark --emit-exe -o examples/control-flow/control-flow
./examples/control-flow/control-flow
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.ControlFlowExampleCompilesAndRuns`.

## Source Files

- [ControlFlow.stark](samples/ControlFlow.stark)
- [Stark.toml](samples/Stark.toml)

### ControlFlow.stark

{{< file-sample "samples/ControlFlow.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Bindings, Mutation, and Control Flow](/book/06-bindings-control-flow/)
- [Language reference](/reference/language/LanguageReference/)
