# Phase 22 - Threading Coordination

Status: WIP, decision locked.

This document narrows the self-hosting threading scope. Stark already has
`System.Threading.Thread` and atomics. The missing self-hosting-facing work is
not a broad threading framework; it is the small coordination surface needed when
the build/test driver eventually wants parallel workers.

## 1. Locked Decision

The blessed pre-bootstrap threading expansion is:

1. captured/payload thread starts,
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

## 3. Captured Thread Payloads

Today's `ThreadEntry` is:

```stark
public alias ThreadEntry = fnptr<fn i32[min max]()>;
```

That is enough for simple global-state demos, but not for build workers that need
an owned job descriptor, configuration, output channel, or per-worker scratch
state.

The intended shape is an owning thread start over a heap closure or equivalent
captured callable:

```stark
import System.Threading
module Build

struct WorkerPayload
{
    Job: BuildJob;
    Results: System.Threading.Sender<BuildResult>;
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
        System.Threading.Thread.Start(capture(move payload) () => Worker(payload));

    worker.Detach();
    return System.Threading.ThreadStatus.Ok;
}
```

Exact API spelling is implementation work, but the semantic contract is locked:

- captured values moved into a new thread must satisfy `Transferable`,
- shared borrows captured by a scoped thread must satisfy `Shareable`,
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
| Moving a captured payload into a thread | `Transferable(T)` |
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

- [ ] Add owned/captured thread start support while preserving the existing
      no-payload `ThreadEntry` constructor.
- [ ] Enforce `Transferable` for moved thread payload captures.
- [ ] Add `System.Threading.Synchronized<T>` and `Locked<T>` guard APIs.
- [ ] Make `Locked<T>` release the lock on `drop` and prevent protected borrows
      from outliving the guard.
- [ ] Implement the primitive on top of atomics plus platform wait/wake hooks
      where useful; avoid busy spinning under normal contention.
- [ ] Add `System.Threading.Channel<T>` with sender/receiver handles and an MPSC
      first implementation.
- [ ] Enforce `Transferable(T)` for channel payloads.
- [ ] Add tests for captured thread payloads, guard lifetime/release behavior,
      channel send/receive/close behavior, and contention.
- [ ] Add build/test-driver integration tasks only after the synchronous driver
      path works.

## 9. Documentation Work

- [ ] Update `docs/StandardLibrary/System.Threading.md` after the public API
      spelling lands.
- [ ] Update the book's threading chapter after the API is implemented and tested.
- [ ] Add examples showing build/test workers publishing events through channels
      and keeping final diagnostic output deterministic.
