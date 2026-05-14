+++
title = "8. Borrowing in Stark"
weight = 80
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/07-ownership-moves-drops/"
next = "/book/09-borrowing-vs-rust/"

[[language_refs]]
title = "Borrower System"
href = "/reference/language/BorrowerSystem/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[example_refs]]
title = "Borrowing Examples"
href = "/reference/examples/borrowing/Borrowing.stark"

[[example_refs]]
title = "Borrow Kinds"
href = "/reference/examples/borrowing/BorrowKinds.stark"

[[example_refs]]
title = "Out Parameters"
href = "/reference/examples/borrowing/OutParameters.stark"
+++

# Borrowing in Stark

Borrowing lets code access a value without taking ownership of it. In Stark it
also carries the facts the optimizer needs: non-null access, escape limits,
mutability, deep-readonly provenance, initialization authority, and
non-overlapping memory regions by default.

{{< stark-sample "assets/book/samples/borrow-counter.stark" >}}

## Step 1: Borrow Without Taking Ownership

An owned value still has exactly one owner while it is borrowed. The borrow can
read or mutate only according to the authority in its type, and it never becomes
responsible for cleanup.

```stark
struct Counter {
    i32[min max] Value;

    finite law i32[min max] Current(borrow Counter self) {
        return self.Value;
    }

    finite void Add(mut borrow Counter self, i32[min max] amount) {
        self.Value += amount;
        return;
    }
}
```

`borrow Counter` is temporary readonly access. `mut borrow Counter` is
temporary mutable access. Neither form owns the `Counter`; the caller's local
still owns the value after the call returns.

## Step 2: Choose The Escape Class From The API Shape

Borrow escape is part of the type. Use the narrowest class that matches what
the API actually does:

```stark
finite law i32[min max] Current(borrow Counter self) {
    return self.Value;
}

finite retborrow mut i32[min max] Slot(mut borrow Counter self) {
    return self.Value;
}
```

The three escape classes are:

- `borrow T`: temporary access; it cannot be stored or returned
- `retborrow T`: may escape only through the return value
- `storeborrow T`: may be stored or otherwise escape through an explicit
  storage-bearing API

A storage-bearing API should say that directly:

```stark
struct CounterView {
    storeborrow Counter Target;
}

fn void Remember(mut borrow CounterView view, storeborrow Counter target) {
    view.Target = target;
    return;
}
```

Use `storeborrow` sparingly. It is the form that says a borrow intentionally
outlives the immediate call. Most ordinary helper functions should use
`borrow` or `retborrow`.

This intentionally invalid sample is also checked by the book:

{{< stark-sample "assets/book/negative-samples/plain-borrow-return-escape.stark" >}}

The fix is not to invent a lifetime parameter. The fix is to choose the source
form that matches the API.

## Step 3: Treat Mutable Borrowing As Authority, Not Ownership

Mutable borrowing gives permission to write through the borrow. It does not
move the value and it does not make the callee responsible for destroying it.

```stark
export fn i32[min max] main() {
    stack mut Counter counter = new Counter() {
        Value = 4
    };

    counter.Add(5);
    counter.Slot() = 12;

    return counter.Current();
}
```

The `Add` call receives `mut borrow Counter self`. The assignment through
`Slot()` receives a returned mutable borrow to one field. In both cases,
`counter` remains the owned local and remains available after the temporary
borrow ends.

## Step 4: Keep Non-Escaping Borrows Local

Stark does not make every borrow potentially escaping and then ask the
programmer to annotate lifetimes. The default safe borrow is local and
non-null. If a borrow needs to escape through a return value, that intent is
written directly with `retborrow`.

```stark
fn void Inspect(borrow Counter counter) {
    counter.Current();
    return;
}

fn retborrow mut i32[min max] Field(mut borrow Counter counter) {
    return counter.Value;
}
```

The first function may inspect the counter during the call only. The second
function returns a borrow tied to the input, so its return type says
`retborrow`.

## Step 5: Leave Null At Raw-Pointer Boundaries

Safe borrows are never nullable. This intentionally invalid sample is part of
the book's negative sample check:

{{< stark-sample "assets/book/negative-samples/null-borrow.stark" >}}

Use raw pointers at FFI boundaries when a foreign API can return or accept
`null`. Convert into safe Stark borrows only after the boundary code has proved
the facts that safe code relies on.

```stark
unsafe fn bool IsMissing(rawptr<i32[min max]> value) {
    return value == null;
}
```

A null check on a raw pointer proves only that the raw pointer is not null. It
does not prove alignment, lifetime, initialization, aliasing, or safe-borrow
validity.

## Step 6: Separate Readonly, Frozen, Const, And Shared Access

Readonly access through one path is not the same thing as deep immutability.
Stark separates the common cases:

- `borrow T`: readonly through this borrow path
- `frozen T`: deeply readonly for this borrow lifetime
- `const T`: permanently deeply immutable provenance
- `shared T`: explicit shared access domain

{{< stark-sample "assets/book/samples/frozen-read.stark" >}}

`frozen` rejects writes through anything reachable from the value:

{{< stark-sample "assets/book/negative-samples/frozen-write.stark" >}}

`const` is stronger than `frozen` because it requires permanent const
provenance:

{{< stark-sample "assets/book/samples/const-parameter-provenance.stark" >}}

Readonly and non-overlap are separate facts. Two immutable views may refer to
the same memory. Function parameters still use the memory-contract rules in the
next steps.

## Step 7: Remember That Borrow Parameters Are Non-Overlapping By Default

For ordinary Stark functions, every pair of memory-backed parameters must be
passed non-overlapping regions unless the callee says otherwise. This default
applies to `borrow`, `mut borrow`, `retborrow`, `storeborrow`, slices, text
views, `out`, `init`, bounded raw pointer regions, `rawptr`, and `rawmutptr`.

{{< stark-sample "assets/book/samples/borrowing-nonoverlap.stark" >}}

The important default is visible in this small function:

```stark
fn void AddSeparate(mut borrow Cell left, mut borrow Cell right) {
    left.Value += 1;
    right.Value += 10;
    return;
}
```

Because both parameters are memory-backed, a safe call must prove `left` and
`right` do not overlap. Distinct fields such as `pair.Left` and `pair.Right`
are visible enough for the compiler to prove that relation.

## Step 8: Opt Into Overlap Or Same-Region Calls Explicitly

The default can be adjusted with relation clauses:

```stark
fn void AddOverlapSafe(mut borrow Cell left, mut borrow Cell right)
    where overlap(left, right) {
    left.Value += 1;
    right.Value += 10;
    return;
}

finite law bool SameCell(borrow Cell left, borrow Cell right)
    where same(left, right) {
    return left.Value == right.Value;
}
```

`where overlap(left, right)` removes the default non-overlap requirement for
that listed pair, so the callee must be correct when both parameters name the
same region. `where same(left, right)` does the opposite: it requires the caller
to prove the listed parameters are the same visible region.

For subregions, use `where disjoint(...)`:

```stark
fn void CopyWindow(
    rawptr<i8[min max]> source,
    rawmutptr<i8[min max]> destination,
    u64[0 max] sourceStart,
    u64[0 max] length)
    where disjoint(source[sourceStart, length], destination[0, length]) {
    return;
}
```

These relations are pairwise. `where overlap(a, b)` says nothing about `a` and
`c`, and `where same(a, b), same(b, c)` does not automatically state
`same(a, c)`.

## Step 9: Make The Call Site Prove Its Memory Facts

Separate variable names are not proof. The compiler needs visible provenance:
distinct fields, non-overlapping indexes, known ranges, declared contracts, or
a branch/scoped proof.

This rejected sample passes the same mutable borrow twice to a function whose
parameters are non-overlapping by default:

{{< stark-sample "assets/book/negative-samples/borrowing-overlap-default.stark" >}}

The valid version either passes distinct visible storage:

```stark
fn void AddPairFields(mut borrow Pair pair) {
    AddSeparate(pair.Left, pair.Right);
    return;
}
```

or calls an overlap-capable API:

```stark
fn void AddMaybeSame(mut borrow Cell first, mut borrow Cell second)
    where overlap(first, second) {
    AddOverlapSafe(first, second);
    return;
}
```

Hidden roots do not count as proof. If a pointer, slice, or borrow is hidden
behind an arbitrary function call, the call site must re-establish the relation
with a visible contract or scoped proof.

## Step 10: Use `if disjoint` For A Checked Fast Path

`if disjoint(...)` performs a runtime region check and gives the true branch a
non-overlap fact:

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

The shape is:

```stark
if disjoint(left, right) {
    AddSeparate(left, right);
    return true;
}

AddOverlapSafe(left, right);
return false;
```

Only the true branch receives the disjoint fact. The false branch must use code
that is correct when the regions may overlap.

## Step 11: Use Unsafe Assertions As Scoped Proofs

An `unsafe` block does not erase borrow or non-overlap rules. If low-level code
knows two regions are separate and the compiler cannot prove it, write the
proof explicitly:

{{< stark-sample "assets/book/samples/borrowing-unsafe-assume.stark" >}}

Inside an `unsafe fn` or an existing `unsafe { ... }` block, the leading
`unsafe` may be omitted:

```stark
assume disjoint(left, right) {
    AddSeparate(left, right);
}
```

The assertion is still narrow. It applies only to the nested statement and must
name visible roots or representable subregions. It does not bless same-root
aliases, integer-laundered pointers, or hidden call results.

## Step 12: Treat `out` And `init` As Borrower Contracts

`out` and `init` are write-destination contracts in the same borrower system.
They do not read old contents, and they participate in default parameter
non-overlap.

{{< stark-sample "assets/book/samples/borrowing-write-destinations.stark" >}}

`out` is for a value the callee must write before the caller observes the
result:

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

`init` is stricter: it is write-only initialization storage. Code writes with
`init destination[index] = value;` and may not inspect the previous contents.

## Step 13: Keep Raw Pointers At The Boundary

Raw pointers are not safe borrows. They may be null, dangling, unaligned,
aliased, or foreign-owned. Bounded raw pointer regions give the borrower and
optimizer a region to reason about without pretending the pointer became safe.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

The bounded forms are:

```stark
rawptr<T>[count]
rawmutptr<T>[count]
```

A positive count requires a non-null pointer valid for the full element range.
A zero count may use `null`. To use ordinary slice rules, convert explicitly
inside an unsafe proof boundary:

```stark
unsafe {
    stack i32[min max][] view = slice(pointer, count);
}
```

The produced slice keeps the raw region's root, length, alignment, mutability,
const provenance, and disjoint facts.

## Step 14: Preserve Borrow Contracts Through Function Pointers

Function-pointer types preserve the callable boundary contract. Because a
`fnptr` type does not name its parameters, relation clauses use synthetic names
such as `arg0` and `arg1`.

{{< stark-sample "assets/book/samples/borrowing-fnptr-contracts.stark" >}}

The key type is:

```stark
fnptr<fn i32[min max](borrow Cell, borrow Cell) where overlap(arg0, arg1)>
```

That callback slot accepts a function that permits overlap between its two
borrow parameters. A callback that requires default non-overlap would not
honestly satisfy that type.

## Step 15: Understand What The Compiler Gets From This

Borrowing is not only a safety feature. It gives lowering and LLVM specific
facts:

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

From safe borrows, Stark gets non-null access and non-owning lifetime facts.
From `frozen` and `const`, it gets readonly facts. From default non-overlap,
`where same`, `where overlap`, `if disjoint`, and `assume disjoint`, it gets
precise alias facts. From `out` and `init`, it gets write-before-read and
initialization facts.

Those facts are the point of making borrowing explicit. They let Stark reject
ambiguous source instead of lowering unclear aliasing into conservative code.
