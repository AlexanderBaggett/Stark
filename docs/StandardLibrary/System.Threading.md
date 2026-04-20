# `System.Threading`

`System.Threading` provides the minimal thread-management surface for Stark.

This module is deliberately not a thread-pool, async, or synchronization
framework. The first version should be enough to create a thread, wait for it,
detach it, and let owned cleanup happen predictably.

## Planned Public Surface

```stark
module System.Threading

public enum ThreadError {
    StartFailed,
    JoinFailed,
    AlreadyJoined,
    AlreadyDetached,
    InvalidState,
}

public enum ThreadStatus {
    Ok,
    Err(ThreadError),
}

public enum ThreadJoinResult {
    Ok(i32),
    Err(ThreadError),
}

public struct Thread {
    finite law bool IsJoinable(self);
    fn ThreadJoinResult Join(mut self);
    fn ThreadStatus Detach(mut self);
    static fn void Yield();
    static fn void SleepMilliseconds(i64[0 max] milliseconds);
}
```

Small enums such as `ThreadError` and `ThreadStatus` should use appropriately
small tags.

## Construction Pattern

Thread creation should use constructors on `Thread`, not a free-standing
`Spawn` function:

```stark
stack mut System.Threading.Thread worker = new(WorkerMain, state);
stack System.Threading.ThreadJoinResult result = worker.Join();
```

The exact callable shape for `WorkerMain` is a language prerequisite. The public
surface should avoid passing raw `void*` state through user code. If the first
compiler implementation needs a raw platform entry internally, that conversion
belongs inside `System.Runtime.Platform`.

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
- Dropping a still-joinable thread should detach or trap according to the final
  safety decision; it must not silently kill the running thread.
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

- This is a planned `v1.2` module.
- It depends on a safe thread-entry callable model.
- Linux and Windows platform thread creation/join/detach implementations remain
  future work.
