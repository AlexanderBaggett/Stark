+++
title = "25. Testing Stark Code"
weight = 250
book_part = "Part IV: The Standard Library"
book_status = "current"
prev = "/book/24-threading-tcp/"
next = "/book/26-performance-model/"
aliases = ["/book/24-testing-stark-code/"]
+++

# Testing Stark Code

Stark test projects are executable projects with an explicit test manifest kind.
They are run through `stark test`.

The tutorial shape deliberately avoids reflection and hidden test discovery. A
test executable owns its `main`, calls the facts it wants to run, and returns a
process exit code.

## Step 1: Declare A Test Project

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

A manual test executable has the same basic shape: run explicit checks and
return a status code.

{{< stark-sample "assets/book/samples/manual-test-executable.stark" >}}

## Step 2: Call Assertions Explicitly

`System.Testing` assertions are ordinary Stark functions. They return `bool`;
they do not throw, allocate hidden exception objects, or unwind the stack.

{{< stark-sample "assets/book/stdlib-samples/testing-project-runner.stark" >}}

The `[Fact]` attributes are source metadata. Discovery is explicit in this
tutorial shape: `main` calls the facts and reports each result through
`System.Testing.RunFact`.

## Step 3: Collapse Failures To A Process Status

`RunFact` writes a concise pass/fail line and returns `1` for failure. Test
projects collapse their accumulated failure count to a process status with
`System.Testing.ExitCode`.

That keeps failure behavior in the ordinary result/exit-code model. Later test
discovery can become more convenient without changing the basic runtime
contract.
