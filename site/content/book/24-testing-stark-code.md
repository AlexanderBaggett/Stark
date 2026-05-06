+++
title = "24. Testing Stark Code"
weight = 240
book_part = "Part IV: The Standard Library"
book_status = "current"
prev = "/book/23-threading-tcp/"
next = "/book/25-performance-model/"
+++

# Testing Stark Code

Stark test projects are executable projects with an explicit test manifest kind.
They are run through `stark test`.

The first implementation deliberately avoids reflection and hidden test
discovery. A test executable owns its `main`, calls the facts it wants to run,
and returns a process exit code.

## Project Shape

Use `kind = "test"` and a `[test]` root:

```toml
[project]
name = "math-tests"
version = "0.1.0"
kind = "test"

[test]
root = "MathTests.stark"
output = "math-tests"
```

From the project directory:

```bash
stark test
```

From a solution directory, `stark test` runs `[defaults].test` when present. If
no default test set is declared, it runs every member whose manifest has
`kind = "test"`.

## Assertion Shape

`System.Testing` assertions are ordinary Stark functions. They return `bool`;
they do not throw, allocate hidden exception objects, or unwind the stack.

{{< stark-sample "assets/book/stdlib-samples/testing-project-runner.stark" >}}

The `[Fact]` attributes are source metadata. In the current implementation,
discovery is still explicit: `main` calls the facts and reports each result
through `System.Testing.RunFact`.

## Result Reporting

`RunFact` writes a concise pass/fail line and returns `1` for failure. Test
projects collapse their accumulated failure count to a process status with
`System.Testing.ExitCode`.

That keeps failure behavior in the ordinary result/exit-code model. Later test
discovery can become more convenient without changing the basic runtime
contract.
