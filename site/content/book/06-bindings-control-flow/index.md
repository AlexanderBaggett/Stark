+++
title = "6. Bindings, Mutation, and Control Flow"
weight = 60
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/05-values-types-ranges/"
next = "/book/07-ownership-moves-drops/"
+++

# Bindings, Mutation, and Control Flow

Most Stark code reads like a C-family language, but Stark makes local storage,
mutation, and loop intent more explicit.

{{< stark-sample "samples/bindings-control-flow.stark" >}}

## Step 1: Give Locals Storage

A local binding starts with a storage class. The most common one is `stack`:

```stark
stack i32[min max] answer = 42;
stack mut i32[min max] total = 0;
```

The first binding is immutable after initialization. The second one is mutable
because it writes `mut`.

This is the shape to remember:

```stark
stack mut Type name = value;
```

Leave out `mut` when the binding should not be reassigned or updated.

Other storage choices exist, but they should be deliberate:

- `register` is for local scalar values that do not need a stable source-visible
  address
- `heap` is for owned allocation-backed local storage
- top-level `const`, `static`, and `static mut` are global declarations, not
  ordinary function locals
- `arena` is reserved for allocator-backed region storage and is not a valid
  executable local storage class yet

Later storage chapters spend more time on lifetime and allocation policy. In
ordinary code, start with `stack`.

The compiler rejects reassignment through an immutable local:

{{< stark-sample "rejected/immutable-local-assignment.stark" >}}

## Step 2: Mark Mutation Before Assigning

Mutable bindings can be assigned and can use compound assignment:

```stark
stack mut i32[min max] total = 0;
stack mut i32[min max] index = 2;

total += index;
index += 1;
```

That mutation is visible in the source. Stark does not hide mutation through
implicit property setters, reflection, or dynamic dispatch.

## Step 3: Make Branch Outcomes Explicit

`if` branches are ordinary statement blocks:

```stark
if (total == 9)
{
    return 0;
}

if (total < 9)
{
    return 1;
}

return 2;
```

The checked sample uses this shape in `CheckExpectedTotal`: compute one value,
name it, then return a clear status for each case. The value of the example is
not that every branch must return immediately. The point is that every accepted
path through a non-void function must produce a value, and the status choices
are visible in the source.

This matters even more for functions marked `finite`, because `finite` says the
function is expected to make progress and return.

## Step 4: State Loop Intent

The sample uses:

```stark
for willexit (stack mut i32[min max] index = 0; index < count; index += 1)
{
    ...
}
```

Every `while` and `for` loop states its behavior:

- `willexit` means this loop is expected to exit
- `non-deterministic` means it may or may not exit, usually because the outside
  world controls progress
- `infinite` means the loop is intentionally unconditional and does not contain
  a structural exit such as `break` or `return`

`willexit` is the loop form accepted inside `finite` functions. Stark uses
these forms because an event loop, a bounded counted loop, and a deliberate
forever loop are different source contracts.

`continue` and `break` are still explicit branch statements. In the sample,
`continue` skips early indexes and `break` stops before adding `stopAt`; both
choices are visible in the loop body rather than hidden behind an iterator
adapter.

## Step 5: Mark Independent Loops Only With Proof

`independent` is an additional loop contract:

```stark
stack mut i32[min max][4] values =
{
    0, 0, 0, 0
};

for willexit independent (stack mut u8[0 4] index = 0; index < 4; index += 1)
{
    values[index] = (i32[min max])index;
}
```

It means the loop has no loop-carried memory dependency. One iteration may not
read or overwrite memory written by another iteration. In the sample, each
iteration writes only `values[index]`, and `index` is the canonical induction
variable.

Treat `independent` as a proof-carrying statement, not a vague optimization
hint. If a loop writes through hidden roots, uses non-induction indexes, nests
other loops, exits early, or calls functions with unproven memory effects, the
compiler must reject the contract.

## Step 6: Switch On A Closed Shape

`switch` is the multi-way branch form. It is useful when the value being tested
is part of the program's explicit data model:

```stark
switch (value)
{
    case 42:
        return true;
    default:
        return false;
}
```

Every `switch` must account for the whole domain of the value it tests: cover
every possible value with cases, or say what happens to the rest with
`default`. A value that matches no arm is a compile error, not a runtime
surprise.

When only a single case matters, `if` and `while` conditions also accept the
same patterns directly, written `expr is pattern`:

```stark
if (value is 42)
{
    return true;
}
```

Later enum chapters build on `switch` and `is` patterns for variant-shaped
data, including pattern captures that bind new locals on the matching path.
