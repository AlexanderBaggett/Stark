+++
title = "9. Stark Borrowing Compared With Rust"
weight = 90
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/08-borrowing/"
next = "/book/10-storage-classes/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[example_refs]]
title = "Ownership Moves"
href = "/reference/examples/borrowing/OwnershipMoves.stark"

[[example_refs]]
title = "Borrowing Examples"
href = "/reference/examples/borrowing/Borrowing.stark"
+++

# Stark Borrowing Compared With Rust

Stark and Rust share a belief: memory safety and performance improve when
ownership and borrowing are checked statically.

They do not expose the same model to the programmer.

Rust asks you to reason mostly in terms of references, lifetimes, traits, and
library types. Stark asks you to put more of the memory contract directly in
the function boundary: escape class, mutability, storage, initialization,
deep-readonly access, and memory separation. That makes Stark feel stricter,
but it also gives the compiler fewer ambiguous cases to lower conservatively.

## Step 1: What Is Similar

Both languages reject use-after-move in safe code. Both distinguish shared
access from mutable access. Both prefer deterministic cleanup over garbage
collection.

The first useful mental mapping is:

```rust
struct Counter {
    value: i32,
}

fn current(counter: &Counter) -> i32 {
    counter.value
}

fn add(counter: &mut Counter, amount: i32) {
    counter.value += amount;
}
```

```stark
struct Counter {
    i32[min max] Value;
}

finite law i32[min max] Current(borrow Counter counter) {
    return counter.Value;
}

finite void Add(mut borrow Counter counter, i32[min max] amount) {
    counter.Value += amount;
    return;
}
```

That mapping is intentionally simple: Rust `&T` is closest to Stark
`borrow T`, and Rust `&mut T` is closest to Stark `mut borrow T`. In both
languages, borrowing does not transfer ownership. The owned value still drops
in the owning scope.

The similarity is strongest for local, non-escaping borrows. If a helper only
looks at a value or mutates it during the call, Rust intuition carries over
well.

## Step 2: Where Stark Is Stricter

Stark makes non-escaping borrows the default. A returned borrow must be written
as `retborrow`.

Stark safe borrows are never `null`. Raw pointers can be null, but raw pointers
are an explicit low-level boundary.

Stark does not currently provide standard safe equivalents of `Rc`, `RefCell`,
dynamic trait objects, or general interior mutability. Those patterns make
aliasing and mutation harder to prove, so Stark keeps them out of the ordinary
safe subset.

Stark is also stricter at function boundaries. Memory-backed parameters are
non-overlapping by default, even when the access is readonly:

```stark
fn void AddSeparate(mut borrow Cell left, mut borrow Cell right) {
    left.Value += 1;
    right.Value += 10;
    return;
}
```

Passing the same `Cell` twice is rejected unless the callee explicitly permits
that relation. Rust programmers are used to `&mut` uniqueness, but Stark pushes
the idea wider: slices, safe borrows, raw pointer regions, `out`, `init`, and
text views all participate in the same memory-region contract.

This is not only a safety rule. It is a performance rule. A function with two
default memory-backed parameters gives lowering a stronger alias fact than a C
or Rust signature that accepts two ordinary raw pointers.

## Step 3: Different Tools For Different Intent

Stark has source forms that do not map one-to-one with Rust syntax:

- `retborrow` marks a returned borrow
- `frozen` marks deeply read-only access
- `out` marks a write destination
- `stack`, `heap`, and `arena` make storage explicit

The point is not to be more ceremonial. The point is to keep escape,
mutability, initialization, and storage visible enough that the compiler can
make strong guarantees.

For a Rust programmer, the important shift is to choose the Stark form that
matches the API's actual promise, not the Rust type you might have reached for:

| Intent | Common Rust shape | Stark shape |
| --- | --- | --- |
| temporary readonly access | `&T` | `borrow T` |
| temporary mutable access | `&mut T` | `mut borrow T` |
| return a borrow tied to an input | `fn f<'a>(&'a T) -> &'a U` | `retborrow U` |
| write a caller-owned result | `Result<T, E>` or tuple return | `bool` plus `out T` |
| fill uninitialized caller storage | `MaybeUninit<T>` or raw pointer | `init T` |
| deeply readonly reachable graph | convention, `&T`, or library wrappers | `frozen T` or `const T` |
| may-overlap parameters | careful implementation, raw pointers, or slices | `where overlap(a, b)` |
| required same-region parameters | ad hoc assertions | `where same(a, b)` |

The table is a translation aid, not a promise that every Rust pattern should
be preserved. Many Rust patterns exist because Rust must support more flexible
library-level aliasing. Stark often wants the API to say the stricter thing
directly.

For example, a returned mutable field borrow is written into the return type:

```stark
struct Counter {
    i32[min max] Value;

    finite retborrow mut i32[min max] Slot(mut borrow Counter self) {
        return self.Value;
    }
}
```

There is no named lifetime in the source. The return escape is the lifetime
fact.

## Step 4: Deep Read-Only Access

`frozen` is stronger than "I do not happen to mutate through this name." It says
the reachable data is deeply read-only for the duration of that access.

{{< stark-sample "assets/book/samples/frozen-read.stark" >}}

The sample reads a value through a frozen view of data reachable from a `const`
object graph. Code that receives `frozen Box` can read `box.Value`, but it
cannot turn that access into mutation.

This rejected sample is checked too:

{{< stark-sample "assets/book/negative-samples/frozen-write.stark" >}}

In Rust, `&T` prevents mutation through that reference, but the type system
also supports safe interior-mutability types such as `Cell<T>`, `RefCell<T>`,
`Mutex<T>`, and atomics. Those are useful tools in Rust, but they mean that an
ordinary shared reference is not automatically a deep-readonly optimizer fact.

Stark keeps that distinction visible. Use ordinary `borrow` when the callee
only needs a readonly view through one path:

```stark
finite law i32[min max] ReadOne(borrow Box box) {
    return box.Value;
}
```

Use `frozen` when the callee needs a stronger promise about everything
reachable through that value:

```stark
finite law i32[min max] ReadFrozenTwice(frozen Box box) {
    return box.Value + box.Value;
}
```

The difference matters to lowering. `borrow` says this access path cannot
write. `frozen` says reachable data cannot be mutated for the lifetime of that
borrow. `const` is stronger again: the reachable object graph has permanent
const provenance.

## Step 5: A Fallible Write Without Exceptions

{{< stark-sample "assets/book/samples/out-parameter.stark" >}}

The `out` destination makes the write-before-read contract explicit. This keeps
fallible code in the value/result world rather than using hidden exceptions or
unwinding.

Rust often models this as an owned return:

```rust
fn try_divide(numerator: i32, denominator: i32) -> Option<i32> {
    if denominator == 0 {
        return None;
    }

    Some(numerator / denominator)
}
```

That is a good Rust API. In Stark, `out` is available when the caller already
owns the destination storage and the callee's job is to write it:

```stark
fn bool TryDivide(i32[min max] numerator, i32[min max] denominator, out i32[min max] result) {
    if (denominator == 0) {
        result = 0;
        return false;
    }

    result = numerator / denominator;
    return true;
}
```

For larger values, this can avoid materializing an owned aggregate just to move
it into caller storage. It also keeps the write-before-read rule explicit:
inside the callee, the old destination contents are not input.

Use `init` when the destination starts uninitialized and the callee is
responsible for initialization rather than replacement:

{{< stark-sample "assets/book/samples/borrowing-write-destinations.stark" >}}

## Step 6: Translating Lifetimes Into Escape Classes

Rust uses lifetime parameters when a borrow crosses an API boundary:

```rust
fn slot<'a>(counter: &'a mut Counter) -> &'a mut i32 {
    &mut counter.value
}
```

Stark makes that escape class part of the returned type:

```stark
fn retborrow mut i32[min max] Slot(mut borrow Counter counter) {
    return counter.Value;
}
```

A plain `borrow` is non-escaping. It may be passed into the function and used
inside that function, but it may not be stored or returned. If the API returns
a borrow, write `retborrow`. If the API stores a borrow into a longer-lived
view or object, write `storeborrow`.

That is why this Rust-style question:

```rust
fn view<'a>(value: &'a Box) -> &'a Box
```

becomes this Stark question:

```stark
fn retborrow Box View(retborrow Box value) {
    return value;
}
```

The important part is not the name of the lifetime. The important part is
whether the borrow is temporary, returned, or stored.

## Step 7: Translating Aliasing Into Memory Contracts

Rust permits many shared `&T` aliases, permits only one active `&mut T` in safe
code, and uses library and unsafe abstractions when a program needs more
complicated aliasing. Stark makes the function contract more explicit.

The default Stark function:

```stark
fn void CopyDisjoint(borrow u8[] source, borrow mut u8[] destination) {
    return;
}
```

requires `source` and `destination` to be non-overlapping at a safe call site.
If the operation is correct when regions overlap, say so:

```stark
fn void MoveOverlapSafe(borrow u8[] source, borrow mut u8[] destination)
    where overlap(source, destination) {
    return;
}
```

If the operation requires two parameters to be views of the same region, say
that instead:

```stark
fn bool SameBytes(borrow u8[] left, borrow u8[] right)
    where same(left, right) {
    return left.Length == right.Length;
}
```

The call site must prove those facts. Distinct local variable names are not
proof. Visible distinct fields, non-overlapping index ranges, declared
contracts, bounded raw pointer regions, and `if disjoint` branches are the
usual proof sources.

{{< stark-sample "assets/book/samples/borrowing-nonoverlap.stark" >}}

And the rejected version is just as important:

{{< stark-sample "assets/book/negative-samples/borrowing-overlap-default.stark" >}}

## Step 8: Replacing Interior Mutability With Explicit Ownership Paths

Rust programmers often reach for `Rc<RefCell<T>>`, `Arc<Mutex<T>>`, `Cell<T>`,
or atomics when ordinary borrowing is too restrictive. Stark does not make
those the default safe vocabulary because they hide mutation behind shared
access.

The usual Stark rewrite is to keep one owner and pass explicit mutable borrows
through the part of the program that performs the update:

```stark
struct Accumulator {
    i32[min max] Total;
}

fn void Add(mut borrow Accumulator accumulator, i32[min max] value) {
    accumulator.Total += value;
    return;
}

fn void AddBoth(mut borrow Accumulator accumulator) {
    Add(accumulator, 10);
    Add(accumulator, 20);
    return;
}
```

When several pieces of code need to identify the same stored value, prefer an
explicit owner plus an index, handle, or returned borrow instead of hidden
shared ownership:

```stark
struct Slot {
    i32[min max] Value;
}

struct Table {
    Slot[4] Slots;
}

fn retborrow mut Slot Get(mut borrow Table table, u64[0 3] index) {
    return table.Slots[index];
}

fn void Increment(mut borrow Table table, u64[0 3] index) {
    table.Slots[index].Value += 1;
    return;
}
```

If the program truly needs shared or concurrent state, make that domain
explicit with `shared`, a standard-library concurrency primitive, or a raw/FFI
boundary designed for that purpose. Ordinary `borrow` should stay ordinary.

## Step 9: Keeping Raw Pointers Out Of The Normal Translation

Rust has references and raw pointers. Stark has safe borrows and raw pointers.
Do not translate Rust `&T` into Stark `rawptr<T>` just because both are pointer
shaped at runtime.

Use safe borrows for normal Stark code:

```stark
fn i32[min max] Read(borrow Counter counter) {
    return counter.Value;
}
```

Use raw pointers when the boundary is actually raw: FFI, platform APIs,
nullable handles, pointer arithmetic, or low-level memory code.

```stark
unsafe fn bool IsNull(rawptr<i32[min max]> pointer) {
    return pointer == null;
}
```

A raw pointer null check does not prove enough to make it a safe borrow. It
does not prove initialization, alignment, lifetime, or aliasing. Converting a
raw pointer into a safe Stark view belongs inside an explicit unsafe proof
boundary.

## Step 10: Why Stark Chooses The Stricter Surface

Many Rust features are designed to express safe flexibility. Stark deliberately
trades some of that flexibility for stronger default facts.

For example:

```stark
finite law i32[min max] ReadFrozenTwice(frozen Box box) {
    return box.Value + box.Value;
}

fn void AddSeparate(mut borrow Cell left, mut borrow Cell right) {
    left.Value += 1;
    right.Value += 10;
    return;
}
```

The first function gives the compiler a deep-readonly access path. The second
function gives it two mutable regions that are non-overlapping by default.
Those are valuable facts for common optimizations: load reuse, hoisting,
dead-store removal, vectorization, and LLVM `noalias`-style lowering.

The practical rule for Rust programmers is simple: start with the Rust
ownership intuition, then make the Stark contract sharper. If a borrow does
not escape, use `borrow`. If it returns, use `retborrow`. If storage is written
by the callee, use `out` or `init`. If aliasing is allowed or required, state
`overlap` or `same`. If data is deeply readonly, use `frozen` or `const`.
