# `System.Process`

`System.Process` exposes the small public process helpers built on the internal
platform layer.

## Public Surface

```stark
module System.Process

public fn i32[-2147483648 2147483647] CurrentId();
public fn void Exit(i32[-2147483648 2147483647] code);
```

`CurrentId` returns the operating-system process id for the current process.

`Exit` terminates the current process with the provided exit code. It is an
unrecoverable process-termination boundary and does not unwind Stark-owned
values. Code after a direct `System.Process.Exit(...)` call is lowered as
unreachable; if the platform exit boundary unexpectedly returns, the generated
code traps rather than continuing.

Both functions are ordinary `fn` functions because they cross the operating
system boundary.

## Example

```stark
import System.Process
module App

export unsafe ffi fn i32[-2147483648 2147483647] main() {
    if (System.Process.CurrentId() <= 0) {
        return 1;
    }

    System.Process.Exit(7);
    return 0;
}
```

## Current Status

- `System.Process` is re-exported by the repository `System` root.
- Linux routes `CurrentId` through the syscall-backed `getpid` path and `Exit`
  through `exit_group`.
- Windows routes the same operations through `GetCurrentProcessId` and
  `ExitProcess`.
