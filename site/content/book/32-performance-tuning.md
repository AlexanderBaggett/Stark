+++
title = "32. Performance Tuning"
weight = 320
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/31-integers-floats-overflow/"
next = "/book/33-unsafe-stark-raw-pointers/"
aliases = ["/book/28-performance-tuning/", "/book/29-performance-tuning/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"
+++

# Performance Tuning

This chapter turns the performance model into a repeatable tuning loop. The
goal is not to write surprising code. The goal is to make the work, storage,
allocation, and failure behavior visible, then measure the result.

## Step 1: Start From Clear Source Choices

Begin with the smallest kernel that still represents the work.

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

The useful choices are all ordinary Stark choices:

- the loop works over fixed storage
- the helper functions are `finite law`
- the integer ranges are explicit
- no allocation or callable indirection is hidden in the kernel

This is the baseline style. If the kernel cannot be explained this plainly,
separate setup, allocation, IO, and parsing from the hot path before tuning.

That separation should be visible in source:

```stark
finite law i32[min max] SumKernel(i32[min max][4] values) {
    return values[0] + values[1] + values[2] + values[3];
}

fn i32[min max] RunOnce() {
    stack i32[min max][4] values = { 1, 2, 3, 4 };
    return SumKernel(values);
}
```

Keep parsing, file IO, allocation, and logging outside the kernel unless those
operations are the workload you intend to measure.

Write one function for the measured work and another function for setup:

```stark
finite law i32[min max] SumFour(i32[min max][4] values) {
    return values[0] + values[1] + values[2] + values[3];
}

fn i32[min max] BuildAndRun() {
    stack i32[min max][4] values = { 1, 2, 3, 4 };
    return SumFour(values);
}
```

If the measured work is text formatting, allocation, or IO, include it
intentionally and say so in the benchmark name:

```stark
import System.Text

fn i32[min max] FormatOneInteger() {
    stack Ascii label[32] = $"Score: {1234}";
    return (i32[min max])AsciiLength(AsciiView(label));
}
```

Do not compare a Stark kernel against a Rust or C program that performs a
different source task. Source intent comes before the timing number.

## Step 2: Make Non-Overlap Explicit

When a hot path touches multiple memory regions, decide whether overlap is
allowed.

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

Default Stark parameters are non-overlapping when they are memory-backed.
Use `where overlap(...)` only for an API that is intentionally overlap-safe.
Use `where same(...)` when two parameters must name the same region. Use
`if disjoint(...)` when one function supports both a fast disjoint branch and
an overlap-safe branch.

The three useful shapes are different APIs, not interchangeable decoration:

```stark
fn void AddSeparate(
    borrow i32[min max][] left,
    borrow mut i32[min max][] output) {
    return;
}

fn void MoveOverlapSafe(
    borrow i32[min max][] source,
    borrow mut i32[min max][] output)
    where overlap(source, output) {
    return;
}

fn bool TryMoveFast(
    borrow i32[min max][] source,
    borrow mut i32[min max][] output)
    where overlap(source, output) {
    if disjoint(source, output) {
        AddSeparate(source, output);
        return true;
    }

    MoveOverlapSafe(source, output);
    return false;
}
```

Use the default non-overlap shape for the common case. Reach for
`where overlap(...)` only when the same function is meant to accept overlapping
regions.

Use `where same(...)` when the API requires two names for the same region:

```stark
fn bool IsSameBuffer(
    borrow i32[min max][] left,
    borrow i32[min max][] right)
    where same(left, right) {
    return true;
}
```

Use the least permissive contract that matches the source behavior. A stricter
borrow contract is easier for readers to trust and easier to benchmark fairly.

## Step 3: Give Raw Pointer Loops Bounds

Raw pointer code is easier to review when the source names a bounded region.

{{< stark-sample "assets/book/samples/bounded-raw-pointer-regions.stark" >}}

The bound changes the code from "some address" to "this many elements from this
base." That is the shape to use before writing `where disjoint(...)`,
`if disjoint(...)`, or an `independent` loop over raw memory.

For safe slices, the same idea appears as an `independent` loop over indexed
elements:

```stark
fn void AddInto(
    borrow i32[min max][] left,
    borrow i32[min max][] right,
    borrow mut i32[min max][] output,
    u8[0 10] count) {
    for willexit independent (stack mut u8[0 10] index = 0; index < count; index += 1) {
        output[index] = left[index] + right[index];
    }

    return;
}
```

Write `independent` only when each iteration can stand alone. If one iteration
reads data written by another iteration, use an ordinary loop.

Keep loop bounds in small ranged integers when the benchmark domain is small:

```stark
finite law i32[min max] SumTen(i32[min max][10] values) {
    stack mut i32[min max] total = 0;
    for willexit independent (stack mut u8[0 10] index = 0; index < 10; index += 1) {
        total += values[index];
    }

    return total;
}
```

Use `for willexit` when the loop is expected to terminate. Use a plain `loop`
only for code that really has an open-ended control flow shape.

## Step 4: Use `inline` Deliberately

Use `inline` for tiny wrappers whose call overhead is part of the hot path. Use
`inlinehint` for helpers that are usually profitable but should still be a hint.
Use `noinline` for boundaries that protect code size, diagnostics, platform
calls, or benchmarking setup.

The source shape still comes first. Inlining is most useful after ranges,
storage, and memory relations are already visible.

Use attributes to explain the role of the function:

```stark
inline finite law i32[min max] ClampToZero(i32[min max] value) {
    if (value < 0) {
        return 0;
    }

    return value;
}

hot fn i32[min max] ScoreHotPath(i32[min max] value) {
    return ClampToZero(value) + 1;
}

noinline cold fn i32[min max] ReportUnexpected(i32[min max] value) {
    return value;
}
```

Do not put `inline` on large functions just because they are important. Split
the tiny reusable operation first, then mark that operation.

Use `cold` for uncommon failure paths and `hot` for the function that actually
runs in the tight path:

```stark
cold fn i32[min max] BadInput() {
    return -1;
}

hot fn i32[min max] ParseFastPath(bool ok) {
    if (!ok) {
        return BadInput();
    }

    return 0;
}
```

Do not mark every helper `hot`. If everything is hot, the annotation no longer
describes the program.

## Step 5: Keep The Timed Work Honest

Write the timed function so the work is visible:

```stark
finite law i32[min max] Kernel(i32[min max][4] values) {
    return values[0] + values[1] + values[2] + values[3];
}

fn i32[min max] TimedIteration(i32[min max][4] values) {
    return Kernel(values);
}
```

If allocation is part of the benchmark, put it in the timed function. If it is
setup, put it outside:

```stark
fn i32[min max] TimedWithSetup() {
    stack i32[min max][4] values = { 1, 2, 3, 4 };
    return Kernel(values);
}
```

Both versions are valid benchmarks, but they answer different questions. Name
the benchmark so readers know which work is included.

Write benchmark notes in source terms:

```text
Measured: fixed-array initialization and one sum call
Not measured: file IO, command-line parsing, console output
Input: four signed 32-bit values
Result: signed 32-bit sum
```

When comparing languages, make the programmer-facing task match:

```text
Stark: initialize four values and sum them.
Rust: initialize four values and sum them.
C: initialize four values and sum them.
```

If Stark uses a language feature that the other language does not have, keep
the benchmark, but name it after the source task being demonstrated.

## Step 6: Use Release Builds For Numbers

Use the project profile that matches the measurement:

```toml
[profiles.release]
opt = 3
```

Then record the command that produced the result:

```bash
stark run --release
```

Keep the input size, target OS, target CPU, and toolchain versions with the
result. A number without its measurement setup is not very useful later.

## Step 7: Measure, Inspect, And Repeat

Tune in a loop:

1. Run the benchmark in `Release`.
2. Compare against C and Rust using C as `1.0`.
3. Inspect generated output only for the specific question under investigation.
4. Change the source or benchmark setup when required work is hidden or misplaced.
5. Rerun the benchmark and keep the result with the change.

Do not accept a benchmark win that weakens ownership, skips cleanup, changes
the public API shape for convenience, or moves required work out of the timed
region in only one language.

Use a short result note:

```text
Workload: fixed-array sum
Included: four-value setup and one kernel call
Excluded: file IO, formatting, allocation
Stark: 0.98x C baseline
Rust: 1.01x C baseline
C: 1.00x baseline
Next action: none; keep sources comparable.
```
