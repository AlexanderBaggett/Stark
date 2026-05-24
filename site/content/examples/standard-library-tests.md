+++
title = "Standard Library Tests"
weight = 125
+++

This example is a small `kind = "test"` project using `System.Testing`. It
defines ordinary Stark functions for each fact, calls them from `main`, and
returns a process exit code based on the accumulated failures.

## Build And Run

```bash
cd examples
dotnet run --project ../src -- test standard-library-tests
```

Expected test output includes:

```text
ok IntegerAssertionsWork
ok TextAssertionsWork
```

Status: included in the `examples/Stark.solution.toml` default `test` target.

## Source Files

- [StandardLibraryTests.stark](/reference/examples/standard-library-tests/StandardLibraryTests.stark)
- [Stark.toml](/reference/examples/standard-library-tests/Stark.toml)

### StandardLibraryTests.stark

{{< file-sample "static/reference/examples/standard-library-tests/StandardLibraryTests.stark" "stark" >}}

### Stark.toml

{{< file-sample "static/reference/examples/standard-library-tests/Stark.toml" "toml" >}}

## Related

- [Testing Stark Code](/book/28-testing-stark-code/)
- [`System.Testing`](/reference/standard-library/System.Testing/)
