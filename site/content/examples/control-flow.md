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

- [ControlFlow.stark](/reference/examples/control-flow/ControlFlow.stark)
- [Stark.toml](/reference/examples/control-flow/Stark.toml)

### ControlFlow.stark

{{< file-sample "static/reference/examples/control-flow/ControlFlow.stark" "stark" >}}

### Stark.toml

{{< file-sample "static/reference/examples/control-flow/Stark.toml" "toml" >}}

## Related

- [Bindings, Mutation, and Control Flow](/book/06-bindings-control-flow/)
- [Language reference](/reference/language/LanguageReference/)
