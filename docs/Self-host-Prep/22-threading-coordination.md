# Phase 22 - Threading Coordination

Status: WIP, decision locked.

This document narrows the self-hosting threading scope. Stark already has
`System.Threading.Thread` and atomics. The missing self-hosting-facing work is
not a broad threading framework; it is the small coordination surface needed when
the build/test driver eventually wants parallel workers.

## 1. Locked Decision

The blessed pre-bootstrap threading expansion is:

1. explicit payload thread starts,
2. an easy guard-based shared-state primitive, and
3. channels for progress and result publication.

Do not add `async`/`await`, a thread pool, work stealing, `RwLock`, `Once`,
semaphores, condition variables, or thread locals as part of this decision.
Those may be revisited later, but they are not part of the self-hosting
coordination scope.

## 2. Why This Shape

The current host compiler's `async Task` usage is sequential I/O. A synchronous
Stark port is behaviorally equivalent and remains the default self-hosting path.

If Stark later parallelizes project builds or test execution, the minimum useful
surface is still small:

- start a worker with owned data instead of only a no-argument `fnptr`,
- protect shared mutable compiler/build state behind one easy standard-library
  primitive,
- publish progress, diagnostics, and per-job results without shared mutable
  result arrays or direct worker printing.

## 3. Payload Thread Starts

Today's `ThreadEntry` is:

```stark
public alias ThreadEntry = fnptr<fn i32[min max]()>;
```

That is enough for simple global-state demos, but not for build workers that need
an owned job descriptor, configuration, output channel, or per-worker scratch
state.

The implemented pre-self-host shape is an explicit owning payload entry. Hidden
capturing thread closures are not part of the pre-self-host surface; they may be
added later as sugar over the explicit payload form.

```stark
import System.Threading
module Build

struct WorkerPayload
{
    BuildJob Job;
    System.Threading.Sender<BuildResult> Results;
}

fn i32[min max] Worker(WorkerPayload payload)
{
    stack BuildResult result = CompileJob(payload.Job);
    payload.Results.Send(result);
    return 0;
}

fn System.Threading.ThreadStatus StartWorker(WorkerPayload payload)
{
    stack mut System.Threading.Thread worker =
        System.Threading.Thread.Start<WorkerPayload>(Worker, payload);

    worker.Detach();
    return System.Threading.ThreadStatus.Ok;
}
```

The public API spelling is:

```stark
public alias ThreadPayloadEntry<T> = fnptr<fn i32[min max](T)>;
static fn Thread Start<T>(ThreadPayloadEntry<T> entry, T payload)
    where Transferable(T);
```

The semantic contract is:

- payload values moved into a new thread must satisfy `Transferable`,
- the existing no-payload `Thread(ThreadEntry)` constructor remains valid,
- unsafe raw pointer publication remains explicit and fenced by `unsafe`.

## 4. Easy Shared Mutable State

The blessed shared-state primitive is an ergonomic mutex-style container. The
working name is `System.Threading.Synchronized<T>`.

It should expose a simple mental model:

1. `Synchronized<T>` owns the shared mutable `T`.
2. `Lock()` returns a guard.
3. The guard provides temporary mutable access to `T`.
4. Dropping the guard releases the lock.
5. There is no access to `T` without the guard.

Example:

```stark
import System.Threading
module Build

static System.Threading.Synchronized<BuildCache> Cache =
    new System.Threading.Synchronized<BuildCache>(new BuildCache());

fn void StoreArtifact(ModuleId id, Artifact artifact)
{
    stack mut System.Threading.Locked<BuildCache> cache = Cache.Lock();
    cache.Value().Insert(id, artifact);
    return;
}
```

This remains Stark-shaped: the synchronization point is visible, lock ownership
is represented by an owned guard, and mutation uses an ordinary mutable borrow
derived from that guard. There is no assignment overloading and no hidden lock on
ordinary field/member access.

The implementation should use atomics and the existing internal futex-shaped
platform hooks where useful. The public API should avoid exposing futex details.

## 5. Channels

Channels are the blessed way for workers to publish progress, diagnostics, and
results back to the driver.

Example:

```stark
import System.Threading
module Build

enum BuildEvent
{
    Progress(JobId),
    Result(BuildResult),
    Diagnostic(Diagnostic),
}

fn i32[min max] Worker(BuildJob job, System.Threading.Sender<BuildEvent> events)
{
    events.Send(BuildEvent.Progress(job.Id));
    stack BuildResult result = CompileJob(job);
    events.Send(BuildEvent.Result(result));
    return 0;
}
```

Required first shape:

- multi-producer, single-consumer is enough for the build/test driver,
- `Send(T)` moves a `Transferable(T)` payload into the channel,
- `Receive()` returns an ordinary result/option-shaped value,
- closing/dropping sender handles lets the receiver observe completion,
- workers do not write diagnostics directly to stdout/stderr.

Deterministic final output is still the build driver's responsibility: receive
events, store them by job/order key, and emit in a stable order.

## 6. Relationship To Thread-Safety Laws

This document consumes the `Transferable` and `Shareable` law design from
`14-thread-safety-laws.md` but does not expand it.

Enforcement points required by this scope:

| Operation | Required law |
|---|---|
| Moving a payload into a thread | `Transferable(T)` |
| Sending a value through a channel | `Transferable(T)` |
| Sharing a `Synchronized<T>` handle across threads | `Shareable(Synchronized<T>)`, granted when `Transferable(T)` holds |
| Borrowing through a `Locked<T>` guard | Existing borrow/lifetime rules; borrowed access cannot outlive the guard |

## 7. Explicitly Out Of Scope

These are not part of the locked self-hosting coordination scope:

- `async` / `await`,
- general-purpose thread pools,
- work stealing,
- `RwLock<T>`,
- `Once<T>`,
- semaphores,
- condition variables,
- broad thread-local storage,
- parallel compiler passes,
- hidden synchronized assignment or member access.

## 8. Work Items

- [x] Implement payload thread starts end to end: owned payload handoff through
      `ThreadPayloadEntry<T>` / `Thread.Start<T>(entry, payload)`,
      compatibility with the existing no-payload `ThreadEntry` constructor, and
      `Transferable` enforcement for moved payloads.
- [x] Implement easy guarded shared state: `System.Threading.Synchronized<T>`,
      `Locked<T>` guard lifetime, `drop`-based unlock, borrow rules that prevent
      protected borrows from outliving the guard, and an implementation over
      atomics plus platform wait/wake hooks where useful.
- [x] Implement MPSC channels: `System.Threading.Channel<T>` sender/receiver
      handles, send/receive/close behavior, sender-drop completion,
      receiver-drop close, contention behavior, and `Transferable(T)`
      enforcement for channel payloads.
- [x] Add tests for payload thread starts, `Transferable` failures, guard
      lifetime/release behavior, protected-borrow diagnostics, channel
      send/receive/close behavior, and contention.
      Current coverage includes payload thread-start surface typing, package and
      native payload-thread lifecycle, payload `Transferable` diagnostics,
      channel surface typing, channel handle `Shareable`/`Transferable` facts,
      negative non-transferable channel payload diagnostics, and native
      send/receive/close, receiver-drop behavior, and contended multi-producer
      publication.
- [ ] Add build/test-driver integration tasks only after the synchronous driver
      path works.

## 9. Documentation Work

- [x] Update `docs/StandardLibrary/System.Threading.md` after the public API
      spelling lands.
- [ ] Update the book's threading chapter after the API is implemented and tested.
- [ ] Add examples showing build/test workers publishing events through channels
      and keeping final diagnostic output deterministic.
