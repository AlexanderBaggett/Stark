+++
title = "40. Project: Performance Case Study"
weight = 400
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/39-project-native-package/"
next = "/book/appendix-a-keywords/"
aliases = ["/book/34-project-performance-case-study/", "/book/36-project-performance-case-study/", "/book/37-project-performance-case-study/"]
+++

# Project: Performance Case Study

This project chapter walks one workload through source, measurement, optional
output inspection, and reporting. The goal is to practice the same performance
loop used for the real benchmark suite.

## Step 1: Pick One Small Workload

Choose one small workload and follow it from Stark source to benchmark numbers.
Use output inspection only when one benchmark result needs a focused follow-up
question.

Good first candidates:

- arithmetic in a tight loop
- fixed-array indexing
- `List<T>` growth
- `Queue<T>` growth
- owned text/path allocation

Start with the smallest checked version of the workload:

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

This is not a complete benchmark by itself. It is the source kernel that a
benchmark can call repeatedly after the measurement harness is chosen. Keeping
the kernel small makes it easier to compare source shape, measurements, and
runtime measurements without mixing in unrelated setup costs.

When the case study needs memory traffic, add the memory-contract version as a
second kernel instead of burying aliasing assumptions in prose:

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

## Step 2: Write The Stark Version In Normal Stark Style

Write the Stark version in the style the language is designed for:

- explicit storage
- explicit ranges
- no hidden allocation in helper functions
- no hidden callable indirection
- result/status values for recoverable failure

Do not contort the source just to win a benchmark. The goal is to understand
the normal Stark performance model.

Keep the benchmark kernel separate from setup:

```stark
finite law i32[min max] Kernel(i32[min max][4] values) {
    return values[0] + values[1] + values[2] + values[3];
}

export fn i32[min max] main() {
    stack i32[min max][4] values = { 1, 2, 3, 4 };
    if (Kernel(values) != 10) {
        return 1;
    }

    return 0;
}
```

Then decide what the timed harness includes: only repeated `Kernel` calls, or
also allocation, file IO, text formatting, parsing, and cleanup.

Write that decision beside the code:

```stark
finite law i32[min max] Kernel(i32[min max][4] values) {
    return values[0] + values[1] + values[2] + values[3];
}

fn i32[min max] RunMeasuredIteration() {
    stack i32[min max][4] values = { 1, 2, 3, 4 };
    return Kernel(values);
}
```

Here the measured work includes initialization of the four values plus the
kernel call. If setup should be excluded, move setup out of the repeated
function and pass the initialized data in.

For memory-copy or vector-style work, write both the overlap-safe source shape
and the disjoint source shape explicitly:

```stark
fn void AddDisjoint(
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

If the benchmark accepts overlapping regions, name that in the function:

```stark
fn void CopyOverlapSafe(
    borrow i32[min max][] source,
    borrow mut i32[min max][] destination,
    u8[0 10] count)
    where overlap(source, destination) {
    for willexit (stack mut u8[0 10] index = 0; index < count; index += 1) {
        destination[index] = source[index];
    }

    return;
}
```

Those are different source promises and should usually be different benchmark
cases.

## Step 3: Record A Fair Comparison Setup

When comparing to C or Rust, record:

- toolchain versions
- optimization flags
- target CPU and OS
- benchmark command
- input size
- whether allocation is included
- whether IO is included

Do not compare a Stark program that does extra safety work to a C program that
silently omits the same behavior unless the omission is
explicitly part of the experiment.

Write the programmer-facing goal before writing the three versions:

```text
Goal: sum N signed 32-bit values and report the total.
Included: input initialization and repeated summing.
Excluded: file IO and formatting.
Failure behavior: no recoverable failure in the timed loop.
```

If the goal changes in one language, rename the benchmark or split it into two
benchmarks. A fast result is only useful when readers can see what work was
actually measured.

For each language, write the same programmer-facing task:

```text
Stark: create four signed 32-bit values, sum them, return the total.
Rust: create four signed 32-bit values, sum them, return the total.
C: create four signed 32-bit values, sum them, return the total.
```

Then record the language-specific idiom:

```text
Stark idiom: fixed array and ranged i32 values.
Rust idiom: [i32; 4] and ordinary checked source structure for the benchmark.
C idiom: int32_t[4] or local scalar array with equivalent initialization.
```

If one language uses a precomputed constant and another sums at runtime, those
are different benchmarks. Rename the benchmark or add a second one.

## Step 4: Inspect Output For One Question

Use generated output only to answer focused questions:

```bash
dotnet run --project src -- benchmarks/collections/ListGrowth.stark --emit-llvm -I stdlib/src
```

Look for behavior, not generated symbol names:

- are calls direct or indirect?
- is the loop still recognizable?
- do the integer ranges still match the source intent?
- is allocation where the source said it would be?

For the fixed-array sample, a useful first inspection question is narrower:
does the output show the loop operating over the fixed storage the source
declared, without introducing a collection or owned text allocation?

Do not turn output inspection into the report. The report should still be about
the source task and the measured work.

## Step 5: Report The Result And The Remaining Gap

A useful performance chapter names gaps. If Stark loses a benchmark, document
why:

- missing language or library improvement
- current standard-library algorithm
- allocation policy
- target/toolchain limitation
- deliberately stronger safety check

That honesty makes the roadmap sharper and keeps the book from turning into
marketing copy.

Use a small result record:

```text
Workload: fixed-array sum
Input: 4 signed 32-bit values, repeated by the harness
Stark: 0.92x C baseline
Rust: 0.94x C baseline
C: 1.00x baseline
Included work: loop body only
Remaining gap: none observed
```

For a loss, name the next action in source-level terms:

```text
Remaining gap: List<T> growth performs one extra move during reserve.
Next action: inspect collection growth source and add a focused regression.
```

For a win, be just as specific:

```text
Remaining gap: none observed.
Reason to keep benchmark: demonstrates fixed-array source and integer ranges
without allocation, formatting, or IO.
Next action: keep equivalent C and Rust sources in the benchmark directory.
```
