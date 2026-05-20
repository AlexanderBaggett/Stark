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

- [Arithmetic.stark](/reference/examples/arithmetic/Arithmetic.stark)
- [Stark.toml](/reference/examples/arithmetic/Stark.toml)

{{< file-sample "static/reference/examples/arithmetic/Arithmetic.stark" "stark" >}}

## Related

- [Integer, Floating-Point, and Overflow Policy](/book/28-integers-floats-overflow/)
- [Language reference](/reference/language/LanguageReference/)
