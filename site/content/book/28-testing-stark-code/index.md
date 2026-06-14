+++
title = "28. Testing Stark Code"
weight = 280
book_part = "Part IV: The Standard Library"
book_status = "current"
prev = "/book/27-byte-buffers/"
next = "/book/29-performance-model/"
aliases = ["/book/24-testing-stark-code/", "/book/25-testing-stark-code/"]

[[stdlib_refs]]
title = "System.Testing"
href = "/reference/standard-library/System.Testing/"
+++

# Testing Stark Code

Stark test projects are executable projects with an explicit test manifest kind.
They are run through `stark test`.

The tutorial shape deliberately avoids reflection and hidden test discovery. A
test executable owns its `main`, calls the checks it wants to run, and returns a
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

{{< stark-sample "samples/manual-test-executable.stark" >}}

## Step 2: Call Assertions Explicitly

`System.Testing` assertions are ordinary Stark functions. They return `bool`;
they do not throw, allocate hidden exception objects, or unwind the stack.

{{< stark-sample "samples/testing-project-runner.stark" >}}

The `[Fact]` attributes are source metadata. Discovery is explicit in this
tutorial shape: `main` calls the tests and reports each result through
`RunFact`.

Use the small assertion helpers directly inside test functions:

```stark
import System.Testing
module MathTests

[Fact]
fn bool AddsNumbers()
{
    return Equal(4, 2 + 2);
}

[Fact]
fn bool RejectsBadState()
{
    return False(false);
}

[Fact]
fn bool NamesAreStable()
{
    return Equal("Stark", "Stark");
}
```

The first assertion set is intentionally small:

```stark
True(condition);
False(condition);
Equal(expected, actual);
Fail("message");
```

The `Equal` helper covers the first scalar and text cases: `bool`,
`i32[min max]`, `i64[min max]`, `u32[0 max]`, `u64[0 max]`, `ascii`, and
`unicode`.

```stark
fn bool ChecksSeveralKinds()
{
    return Equal(true, true)
        && Equal((i32[min max])42, (i32[min max])42)
        && Equal((i64[min max])42, (i64[min max])42)
        && Equal((u32[0 max])7, (u32[0 max])7)
        && Equal((u64[0 max])7, (u64[0 max])7)
        && Equal("ascii", "ascii")
        && Equal((unicode)"wide", (unicode)"wide");
}
```

Use `Status(assertion)` when a helper should return the `TestStatus` enum
instead of a plain boolean:

```stark
fn TestStatus ParseStatus(bool parsed)
{
    return Status(parsed);
}
```

Switch on `TestStatus` when a caller wants to count or report passed and failed
checks separately:

```stark
fn u32[0 1] FailureCount(TestStatus status)
{
    switch (status)
    {
        case TestStatus.Passed:
            return 0;
        case TestStatus.Failed:
            return 1;
    }
}
```

Use `Fail` when a branch should not be reached:

```stark
fn bool DivideByZeroIsRejected(DivideResult result)
{
    switch (result)
    {
        case DivideResult.DivideByZero:
            return True(true);
        case DivideResult.Ok(var value):
            return Fail("expected divide by zero");
    }
}
```

For result enums, test the branch the caller is supposed to handle:

```stark
enum FirstResult
{
    Ok(i32[min max]),
    Empty,
}

fn bool TryFirstRejectsEmpty(FirstResult result)
{
    switch (result)
    {
        case FirstResult.Empty:
            return True(true);
        case FirstResult.Ok(var value):
            return Fail("expected empty input");
    }
}
```

For `out` APIs, initialize the destination, call the function, then assert both
the status and the written value:

```stark
fn bool TryDivide(i32[min max] numerator, i32[min max] denominator, out i32[min max] result)
{
    if (denominator == 0)
    {
        result = 0;
        return false;
    }

    result = numerator / denominator;
    return true;
}

fn bool TryDivideWritesQuotient()
{
    stack mut i32[min max] quotient = 0;
    if (!TryDivide(21, 3, quotient))
    {
        return Fail("divide should succeed");
    }

    return Equal((i32[min max])7, quotient);
}
```

## Step 3: Collapse Failures To A Process Status

`RunFact` writes a concise pass/fail line and returns `1` for failure. Test
projects collapse their accumulated failure count to a process status with
`ExitCode`.

That keeps failure behavior in the ordinary result/exit-code model. Later test
discovery can become more convenient without changing the basic runtime
contract.

For several tests, accumulate a failure count and return once:

```stark
export fn i32[min max] main()
{
    stack mut u32[0 2 ** 31 - 1] failed = 0;

    failed += RunFact("AddsNumbers", AddsNumbers());
    failed += RunFact("RejectsBadState", RejectsBadState());
    failed += RunFact("NamesAreStable", NamesAreStable());
    failed += RunFact((unicode)"WideName", NamesAreStable());

    return ExitCode(failed);
}
```

Use `Exit(failed)` only when the test runner should terminate
the process immediately instead of returning from `main`.

## Step 4: Keep Test Projects Explicit

Put test-only code in a test project manifest:

```toml
[project]
name = "collections-tests"
version = "0.1.0"
kind = "test"

[test]
root = "CollectionsTests.stark"
output = "collections-tests"
```

Run focused tests from the project directory with:

```bash
stark test
```

From a solution root, add the test project to `[defaults].test` when it should
run with the ordinary solution test set.
