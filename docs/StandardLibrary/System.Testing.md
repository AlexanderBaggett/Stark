# `System.Testing`

`System.Testing` provides the first Stark-native assertion helpers for test
projects run by `stark test`.

It is intentionally imported explicitly:

```stark
import System.Testing
module DemoTests
```

The module is packaged with `System`, but it is not re-exported by the `System`
root so normal programs do not pick up test helper names by default.

## Surface

```stark
public enum TestStatus
{
    Passed, Failed
}

public fn bool True(bool condition);
public fn bool False(bool condition);
public fn bool Fail(ascii message);

public fn bool Equal(bool expected, bool actual);
public fn bool Equal(i32[min max] expected, i32[min max] actual);
public fn bool Equal(i64[min max] expected, i64[min max] actual);
public fn bool Equal(u32[0 max] expected, u32[0 max] actual);
public fn bool Equal(u64[0 max] expected, u64[0 max] actual);
public fn bool Equal(ascii expected, ascii actual);
public fn bool Equal(unicode expected, unicode actual);

public fn TestStatus Status(bool assertion);
public fn u8[0 1] RunFact(ascii name, bool assertion);
public fn u8[0 1] RunFact(unicode name, bool assertion);
public fn i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount);
public fn void Exit(u32[0 2 ** 31 - 1] failureCount);
```

## Behavior

- Assertions return ordinary `bool` values; they do not throw or unwind.
- `RunFact` writes a concise pass/fail line and returns `1` for a failed fact.
- Test executables return `System.Testing.ExitCode(failureCount)` from `main`.
- `[Fact]` attributes are valid source metadata today, but discovery remains
  explicit: the test executable's `main` chooses which facts to run.

## Example

```stark
import System.Testing
module DemoTests

[Fact]
fn bool AddsNumbers()
{
    return System.Testing.Equal(4, 2 + 2);
}

export fn i32[min max] main()
{
    stack mut u8[0 1] failed = 0;
    if (System.Testing.RunFact("AddsNumbers", AddsNumbers()) != 0)
    {
        failed = 1;
    }

    return System.Testing.ExitCode(failed);
}
```
