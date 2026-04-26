+++
title = "Benchmarks"
weight = 50
+++

Stark benchmarks currently live in the repository `benchmarks/` directory.
They are source-level programs used for quick performance, runtime, and codegen
feedback while the formal Stark-versus-C-versus-Rust benchmark suite is still
being designed.

## Current Coverage

### Allocator And Memory

- `allocator/HeapLocalBucketReuse.stark` exercises heap-local allocation and
  scope cleanup through the default allocator buckets.
- `allocator/SystemMemoryBucketReallocate.stark` checks bucket-backed
  `System.Memory.Reallocate` behavior when storage can be reused in place.
- `allocator/SystemMemoryFallbackReallocate.stark` checks the conservative
  allocate-copy-free reallocation path when a value no longer fits the old
  bucket.

### Collections

- `collections/ListGrowth.stark` grows and drains `System.Collections.List<T>`,
  checking count, capacity, and push/pop behavior.
- `collections/QueueGrowth.stark` exercises queue growth and FIFO removal over
  the first owned collection implementation.

### Text And Paths

- `text/OwnedTextAllocation.stark` measures allocation-visible owned text
  conversion and concatenation paths.
- `text/OwnedPathAllocation.stark` measures owned path joining and path-view
  inspection helpers.
- `text/TextPathCallerBuffer.stark` is compile-only coverage for caller-owned
  path buffers and low-level text conversion helpers.

## Running Locally

Use the repository benchmark harness:

```bash
scripts/run-benchmarks.sh
```

The harness compiles each executable benchmark, runs one warmup execution, then
prints CSV timing rows:

```text
benchmark,runs,compile_ms,min_ms,avg_ms,max_ms
```

Useful controls:

- `STARK_BENCH_RUNS=10` changes the measured execution count.
- `STARK_BENCH_FILTER=text` limits the run to paths containing a substring.
- `STARK_TARGET=...` passes an LLVM target triple to the compiler.
- `STARK_COMPILER_ARGS=...` adds compiler arguments for local experiments.

Benchmarks marked with `// stark-bench: compile-only` are codegen regression
sources. The executable runner skips them, but `BenchmarkSourceTests` still
checks that every benchmark source lowers successfully and does not fall back to
direct `malloc`, `realloc`, or `free` declarations in generated LLVM IR.

## Publication Bar

These numbers are useful for local compiler work, but they are not yet
publication-quality performance claims. The formal benchmark suite still needs:

- fixed hardware and operating-system notes
- locked compiler versions, optimization levels, and target triples
- C and Rust baseline implementations for each benchmark family
- repeatable result capture with warmup, run count, and variance reporting
- regression thresholds that can fail CI when performance drops materially

Until that suite exists, treat this page as a map of what the repository
exercises today, not as a claim that Stark is faster than C or Rust on these
workloads.
