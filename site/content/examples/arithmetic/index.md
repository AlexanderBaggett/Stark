+++
title = "Arithmetic"
weight = 40
+++

This example exercises local variables, integer operators, and a simple guard
that returns a non-zero status if arithmetic behaves unexpectedly.

## Build And Run

```bash
dotnet run --project src -- examples/arithmetic/Arithmetic.stark --emit-exe -o examples/arithmetic/arithmetic
./examples/arithmetic/arithmetic
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.ArithmeticExampleCompilesAndRuns`.

## Source Files

- [Arithmetic.stark](samples/Arithmetic.stark)
- [Stark.toml](samples/Stark.toml)

### Arithmetic.stark

{{< file-sample "samples/Arithmetic.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Integer, Floating-Point, and Overflow Policy](/book/31-integers-floats-overflow/)
- [Language reference](/reference/language/LanguageReference/)
