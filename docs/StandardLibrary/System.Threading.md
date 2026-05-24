# `System.Threading`

`System.Threading` provides the minimal thread-management surface for Stark.

This module is deliberately not a thread-pool, async, or synchronization
framework. The first version should be enough to create a thread, wait for it,
detach it, and let owned cleanup happen predictably.

## Public Surface

```stark
module System.Threading

public alias ThreadEntry = fnptr<fn i32[min max]()>;

public enum ThreadError
{
    StartFailed,
    JoinFailed,
    AlreadyJoined,
    AlreadyDetached,
    InvalidState,
}

public enum ThreadStatus
{
    Ok,
    Err(ThreadError),
}

public enum ThreadJoinResult
{
    Ok(i32[min max]),
    Err(ThreadError),
}

public struct Thread
{
    Thread(ThreadEntry entry);
    finite law bool IsJoinable(borrow Thread self);
    fn ThreadJoinResult Join(mut borrow Thread self);
    fn ThreadStatus Detach(mut borrow Thread self);
    static fn void Yield();
    static fn void SleepMilliseconds(i64[0 max] milliseconds);
}
```

Small enums such as `ThreadError`, `ThreadStatus`, and `ThreadJoinResult` use
appropriately small tags.

## Construction Pattern

Thread creation should use constructors on `Thread`, not a free-standing
`Spawn` function:

```stark
stack mut System.Threading.Thread worker = new(WorkerMain);
stack System.Threading.ThreadJoinResult result = worker.Join();
```

The current source surface defines `ThreadEntry` as a no-state function pointer
returning an `i32` thread exit code. Named functions and non-capturing lambdas
can be used as entries, including through packaged `System.Threading`
consumption. Capturing thread entries remain out of scope until captured-lambda
lowering is implemented. Raw platform entry thunks remain inside
`System.Runtime.Platform`.

`Yield` and `SleepMilliseconds` are modeled as static functions on `Thread` so
the public surface stays C#-like.

## Function Kinds

`Thread.IsJoinable` is `finite law` because it only reads local thread-handle
state and always returns.

`Join`, `Detach`, `Yield`, and `SleepMilliseconds` are ordinary `fn` methods.
They affect scheduler-visible state, can block, or depend on platform thread
behavior, so they are not `law` functions.

## Ownership And Drop

`Thread` is an owned handle.

- `Join` waits for completion and consumes the joinable state.
- `Detach` releases the requirement to join.
- Dropping a still-joinable thread performs best-effort detach cleanup. It must
  not silently kill the running thread.
- The standard library should not expose a force-kill or abort-thread API in the
  first version.

## Deferred Work

The following are intentionally out of scope for the first `System.Threading`
slice:

- thread pools
- async/await
- mutexes
- semaphores
- condition variables
- channels
- atomics beyond compiler/runtime primitives needed to make thread start and
  join correct

Synchronization primitives can be added in a later standard-library version
once the memory model and atomic surface are documented.

## Current Status

- `System.Threading` is re-exported by the repository `System` root.
- `ThreadEntry`, `ThreadError`, `ThreadStatus`, `ThreadJoinResult`, `Thread`,
  `Thread` construction, `Thread.Join`, `Thread.Detach`, `Thread.Yield`, and
  `Thread.SleepMilliseconds` are implemented in source.
- Linux `Yield` and `SleepMilliseconds` use internal syscall-backed platform
  hooks. On x86_64 Linux, thread lifecycle uses raw `clone`,
  `mmap`/`munmap`, futex wait/wake, and an internal reference count instead of
  `pthread_create`, `pthread_join`, or `pthread_detach`.
- Windows targets import the corresponding `CreateThread`, `WaitForSingleObject`,
  `GetExitCodeThread`, `CloseHandle`, `SwitchToThread`, and `Sleep` hooks through
  the platform dispatch layer. The internal futex-shaped wait/wake hooks use
  `WaitOnAddress`, `WakeByAddressSingle`, and `WakeByAddressAll`.
- Packaged consumption covers `ThreadEntry`, scheduler helpers, thread
  construction, `Join`, and `Detach`; Linux package archive coverage also
  guards against pthread symbol regressions.
