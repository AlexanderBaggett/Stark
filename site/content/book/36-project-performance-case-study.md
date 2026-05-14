+++
title = "36. Project: Performance Case Study"
weight = 360
book_part = "Part VI: Projects"
book_status = "draft"
prev = "/book/35-project-native-package/"
next = "/book/appendix-a-keywords/"
aliases = ["/book/34-project-performance-case-study/"]
+++

# Project: Performance Case Study

This project chapter walks one workload through source, measurement, IR
inspection, and reporting. The goal is to practice the same performance loop
used for the real benchmark suite.

## Step 1: Pick One Small Workload

Choose one small workload and follow it from Stark source to generated output
and benchmark numbers.

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
the kernel small makes it easier to compare source shape, generated output, and
runtime measurements without mixing in unrelated setup costs.

When the case study needs memory traffic, add the memory-contract version as a
second kernel instead of burying aliasing assumptions in prose:

{{< stark-sample "assets/book/samples/memory-separation-contracts.stark" >}}

## Step 2: Write The Stark Version In Normal Stark Style

Write the Stark version in the style the language is designed for:

- explicit storage
- explicit ranges
- no hidden allocation in helper functions
- no hidden dynamic dispatch
- result/status values for recoverable failure

Do not contort the source just to win a benchmark. The goal is to understand
the normal Stark performance model.

## Step 3: Record A Fair Comparison Setup

When comparing to C or Rust, record:

- compiler versions
- optimization flags
- target CPU and OS
- benchmark command
- input size
- whether allocation is included
- whether IO is included

Do not compare a Stark implementation that does extra safety work to a C
implementation that silently omits the same behavior unless the omission is
explicitly part of the experiment.

## Step 4: Inspect Output For One Question

Use generated IR to answer focused questions:

```bash
dotnet run --project src -- benchmarks/collections/ListGrowth.stark --emit-llvm -I stdlib/src
```

Look for behavior, not internal symbol names:

- are calls direct or indirect?
- is the loop still recognizable?
- did range facts survive?
- is allocation where the source said it would be?

For the fixed-array sample, a useful first inspection question is narrower:
does the emitted output show the loop operating over the fixed storage the
source declared, without introducing a collection or owned text allocation?

## Step 5: Report The Result And The Remaining Gap

A useful performance chapter names gaps. If Stark loses a benchmark, document
why:

- missing optimization pass
- current standard-library algorithm
- allocation policy
- target backend limitation
- deliberately stronger safety check

That honesty makes the roadmap sharper and keeps the book from turning into
marketing copy.
