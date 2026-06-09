# `System.Process`

`System.Process` exposes process id/exit helpers plus the first public process
spawning, environment, argv, working-directory, and output-capture surface.

## Public Surface

```stark
module System.Process

public fn i32[min max] CurrentId();
public fn void Exit(i32[min max] code);

public enum ProcessError;
public enum ProcessStatus;
public enum ProcessResult<T>;
public enum ProcessOption<T>;

public struct ProcessOutput;
public struct ProcessArguments;
public struct ProcessCommand;

public fn ProcessResult<ProcessCommand> Command(ascii executable);
public unsafe fn ProcessResult<ProcessOutput> RunCapture(mut borrow ProcessCommand command);
public unsafe fn ProcessResult<ProcessOutput> RunCapture(ascii executable);
public unsafe fn ProcessResult<ProcessOption<System.Text.OwnedAscii>> GetEnvironment(ascii name);
public unsafe fn ProcessStatus SetEnvironment(ascii name, ascii value);
public unsafe fn ProcessStatus RemoveEnvironment(ascii name);
public fn ProcessResult<System.Text.OwnedAscii> CurrentDirectory();
public unsafe fn ProcessStatus SetCurrentDirectory(ascii path);
public unsafe fn ProcessResult<ProcessArguments> Arguments();
public unsafe fn ProcessResult<u64[0 2 ** 63 - 1]> ArgumentCount();
```

`CurrentId` returns the operating-system process id for the current process.

`Exit` terminates the current process with the provided exit code. It is an
unrecoverable process-termination boundary and does not unwind Stark-owned
values. Code after a direct `System.Process.Exit(...)` call is lowered as
unreachable; if the platform exit boundary unexpectedly returns, the generated
code traps rather than continuing.

The spawning and environment functions are `unsafe` because they cross process
and platform boundaries.

`Command(executable)` validates and stores a null-terminated executable path.
Use `ProcessCommand.AddArgument(argument)` to append argv entries and
`ProcessCommand.SetWorkingDirectory(path)` to set the child working directory.

`RunCapture(command)` waits for the child process, captures stdout and stderr
into owned byte storage, and returns the decoded exit code. `ProcessOutput`
exposes `ExitCode`, `StdoutLength()`, `StderrLength()`, `StdoutSlice()`, and
`StderrSlice()`.

`GetEnvironment(name)` returns `Some(value)` or `None`. On Linux it reads the
inherited environment from `/proc/self/environ`; `SetEnvironment` and
`RemoveEnvironment` update the libc environment used by subsequently spawned
children.

`Arguments()` returns copied argv entries. On Linux this is backed by
`/proc/self/cmdline`.

## Example

```stark
import System.Process
module App

export fn i32[min max] main()
{
    if (System.Process.CurrentId() <= 0)
    {
        return 1;
    }

    unsafe
    {
        stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
            System.Process.Command("/bin/sh");
        switch (commandResult)
        {
            case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                return 2;
            case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                stack mut System.Process.ProcessCommand child = command;
                child.AddArgument("-c");
                child.AddArgument("printf ok; exit 7");
                switch (System.Process.RunCapture(child))
                {
                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var runError):
                        return 3;
                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                        return output.ExitCode;
                }
        }
    }
}
```

## Current Status

- `System.Process` is re-exported by the repository `System` root.
- Linux routes `CurrentId` through the syscall-backed `getpid` path and `Exit`
  through `exit_group`.
- Linux implements process spawn/capture through `pipe`, `fork`, `dup2`,
  `execvp`, `poll`, and `waitpid`.
- Linux implements argv/env reads through `/proc/self/cmdline` and
  `/proc/self/environ`.
- Windows currently routes only `CurrentId` and `Exit` through
  `GetCurrentProcessId` and `ExitProcess`; spawn/capture/env/argv backends still
  need parity work.
