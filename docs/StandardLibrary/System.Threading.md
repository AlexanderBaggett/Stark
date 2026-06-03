# `System.Threading`

`System.Threading` provides the minimal thread-management surface for Stark,
plus the atomic types that make sharing state between those threads safe.

This module is deliberately not a thread-pool, async, or synchronization
framework. The first version should be enough to create a thread, wait for it,
detach it, let owned cleanup happen predictably, and share counters/flags/values
between threads through atomics.

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

## Atomic Types

One atomic type exists for **every** Stark integer width — `AtomicI8` … `AtomicI1024`,
`AtomicU8` … `AtomicU1024` (28 integer types) — plus `AtomicBool`. They are ordinary
structs whose operations are compiler builtins lowered to hardware atomic
instructions; every operation is **sequentially consistent**.

```stark
// The same shape repeats at every width; i64 shown as the representative.
public struct AtomicI64
{
    AtomicI64(i64[min max] initial);

    fn i64[min max] Load(borrow AtomicI64 self);
    fn void Store(mut borrow AtomicI64 self, i64[min max] value);
    fn i64[min max] Add(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Sub(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] And(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Or(mut borrow AtomicI64 self, i64[min max] operand);        // returns previous value
    fn i64[min max] Xor(mut borrow AtomicI64 self, i64[min max] operand);       // returns previous value
    fn i64[min max] Exchange(mut borrow AtomicI64 self, i64[min max] value);    // returns previous value
    fn bool CompareExchange(mut borrow AtomicI64 self, i64[min max] expected, i64[min max] desired);
}

public struct AtomicBool
{
    AtomicBool(bool initial);

    fn bool Load(borrow AtomicBool self);
    fn void Store(mut borrow AtomicBool self, bool value);
    fn bool Exchange(mut borrow AtomicBool self, bool value);                    // returns previous value
    fn bool CompareExchange(mut borrow AtomicBool self, bool expected, bool desired);
}
```

Semantics:

- Every operation is one indivisible seq-cst action. There is no way to express a
  racy read-modify-write through this API: `Add(1)` is one instruction, not a
  `Load`/`Store` pair.
- RMW operations (`Add`/`Sub`/`And`/`Or`/`Xor`/`Exchange`) return the **previous**
  value. Callers that want the new value compute it from the returned one.
- `CompareExchange(expected, desired)` stores `desired` only if the current value
  equals `expected`, and returns whether the swap happened.
- `Add`/`Sub` wrap at the value's width (two's complement), like the `+%`/`-%`
  operators.

### Cost tiers

The API is identical at every width; the lowering strategy differs. Migrating a
counter from `AtomicI64` to `AtomicI256` changes the documented cost, not the code.

| Widths | Lowering | Cost character |
|---|---|---|
| 8, 16, 32, 64 + bool | Single hardware instructions | One locked instruction |
| 24, 48, 96, 128 | Value lives sign/zero-extended in a power-of-2 container; everything except `Add`/`Sub` is still a single lock-free instruction; `Add`/`Sub` retry through a compare-exchange loop | Lock-free; retry only under `Add`/`Sub` contention |
| 192 – 1024 | The struct embeds its own spinlock word next to the value (visible in `sizeof`); operations serialize through it | Serialized; not lock-free — no CPU has lock-free atomics above 128 bits |

128-bit atomics use `cmpxchg16b`-class instructions; Stark's x86-64 baseline
requires that instruction (every x86-64 CPU since ~2006 has it).

### Sharing pattern

Atomics are the safe way for threads to share state through a `static mut`:

```stark
import System.Threading
module Demo

static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

fn i32[min max] Worker()
{
    stack mut i32[min max] i = 0;
    while willexit (i < 100000)
    {
        Counter.Add(1);
        i += 1;
    }

    return 0;
}

export fn i32[min max] main()
{
    stack mut System.Threading.Thread a = new(Worker);
    stack mut System.Threading.Thread b = new(Worker);
    a.Join();
    b.Join();

    // Exactly 200000, every run.
    if (Counter.Load() == 200000)
    {
        return 0;
    }

    return 1;
}
```

`AtomicBool` doubles as a release flag: a producer publishes data through atomics
and then stores `true`; consumers spin (or check periodically) until they observe
the flag.

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

The following are intentionally out of scope for the current `System.Threading`
surface:

- thread pools
- async/await
- mutexes
- semaphores
- condition variables
- channels
- memory orderings weaker than seq-cst on the atomic types

Higher-level synchronization primitives build on the atomic types and the
internal platform futex hooks when they are added.

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
- All 29 atomic types (`AtomicBool`, `AtomicI8`…`AtomicI1024`,
  `AtomicU8`…`AtomicU1024`) are implemented with seq-cst semantics across all
  three lowering tiers, covered by runtime exact-count tests under real thread
  contention, LLVM emission shape tests, and packaged-stdlib consumption tests.
