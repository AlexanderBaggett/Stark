+++
title = "10. Closures and Explicit Capture"
weight = 100
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/09-borrowing-vs-rust/"
next = "/book/11-storage-classes/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"

[[example_refs]]
title = "Closure Examples"
href = "/book/stark-book.md#10-closures-and-explicit-capture"
+++

# Closures and Explicit Capture

Closures let you pass behavior with captured state. Stark keeps that power
explicit: the capture list says what crosses into the closure, and the closure
type says whether the callback is specialized immediately, borrowed for the
current call graph, or owned for later use.

There are two independent decisions in a closure type:

- the closure form: `inline closure`, `borrow closure`, or `heap closure`
- the function kind: `fn`, `finite`, `law`, or `finite law`

The form controls storage and escape. The function kind controls the semantic
promise the callback makes to the caller and to LLVM lowering.

{{< stark-sample "assets/book/samples/closures.stark" >}}

## Step 1: Capture State Explicitly

A Stark lambda can use its parameters, globals, constants, named functions, and
the names listed in its capture clause. It cannot quietly reach out to a local
just because the local is visible in the surrounding block.

```stark
stack i32[min max] offset = 10;

capture(copy offset) (i32[min max] value) => value + offset
```

The word after `capture` is the contract:

- `copy` copies a cheap copyable value into the closure environment
- `move` transfers ownership into the closure environment
- `read` captures readonly access to existing storage
- `mut` captures exclusive mutable access to existing storage
- `out` captures a write-only destination
- `init` captures uninitialized destination storage that must be written
- `unsafe addr` and `unsafe shared` capture lower-level capabilities inside an
  unsafe context

The capture list is part of the performance model. A reader can tell whether a
closure owns data, borrows data, mutates data, or only copies scalars.

## Step 2: Choose The Function Kind Contract

Do not default every closure to `fn`. `fn` is the widest callable contract. A
closure can also promise that it returns, that it is law-style readonly, or
both:

```stark
closure<fn i32[min max](i32[min max])>
closure<finite i32[min max](i32[min max])>
closure<law bool(i32[min max])>
closure<finite law i32[min max](i32[min max])>
```

Use `finite` when the caller is allowed to rely on the callback returning:

```stark
finite i32[min max] ApplyFinite(
    borrow closure<finite i32[min max](i32[min max])> op,
    i32[min max] value) {
    return op(value);
}
```

Use `law` when the callback is readonly/pure in the same sense as a `law`
function:

```stark
law bool ApplyLaw(
    borrow closure<law bool(i32[min max])> predicate,
    i32[min max] value) {
    return predicate(value);
}
```

Use `finite law` for deterministic value-style callback APIs:

```stark
inline finite law i32[min max] ApplyInlineFiniteLaw(
    i32[min max] value,
    inline closure<finite law i32[min max](i32[min max])> op) {
    return op(value);
}
```

The function kind is orthogonal to the closure form. You can have a borrowed
`finite` closure, an inline `finite law` closure, or a heap `fn` closure. Pick
the narrowest kind the callback body can honestly satisfy.

## Step 3: Use Inline Closures For Call-Now Helpers

Use `inline closure<...>` when the receiving function calls the callback during
the current operation. The closure is not a runtime object. It is a
specialization input.

```stark
inline fn i32[min max] ApplyInline(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op) {
    return op(value);
}

fn i32[min max] AddOffsetInline(i32[min max] offset) {
    return ApplyInline(
        32,
        capture(copy offset) (i32[min max] value) => value + offset);
}
```

After specialization, the optimized code has the same shape as a direct block:

```stark
fn i32[min max] AddOffsetInline(i32[min max] offset) {
    return 32 + offset;
}
```

That is the point of an inline closure. It gives you callback syntax without a
fat runtime value, heap allocation, or indirect call. This is the form to reach
for in immediate APIs, layout helpers, small algorithms, and other code where
the callback is used right away.

An inline closure cannot be stored in a local or field, returned, placed in an
array, or converted to a function pointer. If it needs to live as a runtime
value, choose one of the runtime closure forms instead.

## Step 4: Use Borrow Closures For Non-Escaping Runtime Views

Use `borrow closure<...>` when the callback is a runtime value during the
current call graph, but the callee must not retain it.

```stark
fn i32[min max] ApplyBorrow(
    borrow closure<fn i32[min max](i32[min max])> op,
    i32[min max] value) {
    return op(value);
}

fn i32[min max] AddOffsetBorrow(i32[min max] offset) {
    return ApplyBorrow(
        capture(copy offset) (i32[min max] value) => value + offset,
        32);
}
```

A borrowed closure lowers to a pair internally:

```text
{ invoke_pointer, environment_pointer }
```

The environment can live on the caller's stack when the closure is temporary.
The borrowed closure view may be passed through other non-escaping helpers, but
it cannot be stored for later or returned unless the type says that escape is
valid with `retborrow` or `storeborrow`.

Borrow closure arguments are memory-backed values, so the same default
non-overlap rules from borrowing apply. If a closure call receives two mutable
borrow parameters, a safe call must prove those arguments do not overlap unless
the closure type says `where overlap(...)` or `where same(...)`.

## Step 5: Mark Mutable And Once-Only Invocation In The Type

The closure signature also says whether invoking the closure mutates or
consumes its environment.

```stark
fn void PushEvent(
    mut borrow closure<mut fn void(i32[min max])> sink,
    i32[min max] value) {
    sink(value);
    return;
}

fn i32[min max] CountEvents() {
    stack mut i32[min max] total = 0;

    stack mut closure<mut fn void(i32[min max])> add =
        capture(mut total) (i32[min max] value) => {
            total += value;
            return;
        };

    PushEvent(add, 40);
    PushEvent(add, 2);
    return total;
}
```

`closure<mut fn ...>` requires mutable access to the closure value because a
call may mutate captured state. Passing it as an ordinary immutable
`borrow closure<mut ...>` is rejected.

Use `once` when a call consumes the closure:

```stark
fn i32[min max] RunOnce(heap closure<once fn i32[min max]()> producer) {
    return producer();
}
```

After `producer()` is called, the closure value is consumed. Ownership
validation rejects a second call. The initial rule is strict by design:
`mut` and `once` closures may be `fn` or `finite`; `law` and `finite law`
closures use the no-marker call capability so purity is not hiding mutation or
consumption.

## Step 6: Use Heap Closures For Retained Callbacks

Use `heap closure<...>` when the callback must be owned, stored, returned, or
retained past the current call.

```stark
fn heap closure<finite law i32[min max](i32[min max])> MakePureAdder(
    i32[min max] offset) {
    return heap capture(copy offset) (i32[min max] value) => value + offset;
}

fn i32[min max] UseAdder() {
    stack heap closure<finite law i32[min max](i32[min max])> addTwo =
        MakePureAdder(2);

    return addTwo(40);
}
```

The `heap` marker is deliberate. It says the environment is allocation-backed
owned storage. The `finite law` part is still the function kind. Moving the heap
closure moves ownership of that environment. Dropping the heap closure drops the
environment and any owned captured values.

Heap closures cannot retain ordinary stack borrows:

```stark
fn heap closure<fn i32[min max]()> Bad() {
    stack i32[min max] value = 7;

    return heap capture(read value) () => value;
}
```

The valid version copies or moves data into the heap environment:

```stark
fn heap closure<fn i32[min max]()> Good() {
    stack i32[min max] value = 7;

    return heap capture(copy value) () => value;
}
```

That difference is the semantic heart of heap closures: a retained closure
must own or permanently justify everything it keeps.

## Step 7: Choose The Smallest Closure Form And Kind

Start from the API shape and choose the narrowest closure form and function
kind that honestly match it:

| API intent | Closure spelling |
| --- | --- |
| The helper calls the body immediately and returns | `inline closure<finite ...>` or `inline closure<finite law ...>` when possible |
| The helper needs a runtime callback view but cannot retain it | `borrow closure<...>` with the narrowest valid kind |
| The callback mutates its environment | `closure<mut fn ...>` with mutable access |
| The callback is consumed by invocation | `closure<once fn ...>` |
| The callback is stored, returned, or retained | `heap closure<...>` |
| The callback has no captures and only needs a thin target | `fnptr<...>` |
| The callback is deterministic and readonly | `closure<finite law ...>` |

For a call-now helper, write the fast shape directly:

```stark
inline finite law i32[min max] ApplyInlineFiniteLaw(
    i32[min max] value,
    inline closure<finite law i32[min max](i32[min max])> op) {
    return op(value);
}
```

For a stored callback, make ownership visible:

```stark
struct Handler {
    heap closure<finite law i32[min max](i32[min max])> Score;
}
```

This is the same style Stark uses for borrowing and storage generally: the
source says what the compiler is allowed to rely on, and lowering carries those
facts through to direct calls, non-null environment pointers, noalias facts,
and LLVM function attributes where they are sound.
