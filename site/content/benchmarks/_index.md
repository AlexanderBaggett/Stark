+++
title = "Benchmarks"
weight = 50
+++

Stark benchmarks currently live in the repository `benchmarks/` directory.
Executable scenarios use same-stem Stark, C, and Rust sources so local runs can
compare equivalent source-level work across languages.

The comparison contract lives in
[`benchmarks/Fairness.md`](/reference/benchmarks/Fairness.md). It defines the
initial fairness rules, default optimization levels, timing rules, and gap
classification vocabulary for future C and Rust baselines.

## Current Coverage

### Microbenchmarks

- `micro/Arithmetic.stark` measures tight integer arithmetic with a runtime
  process-id seed.
- `micro/Branching.stark` measures nested `if` dispatch plus `switch` dispatch.
- `micro/Calls.stark` measures repeated calls through a `noinline` helper.
- `micro/MemoryAccess.stark` measures stack-array indexed load/store traffic.

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
- `collections/ListIteration.stark` exercises indexed list traversal over a
  grown contiguous collection.
- `collections/DictionaryLookup.stark` exercises integer-key dictionary setup
  and repeated lookup.

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

The harness compiles each executable benchmark for Stark, C, and Rust with the
locked flags from `benchmarks/Fairness.md`, runs one warmup execution per
binary, then prints CSV timing rows and writes timestamped result files under
`benchmarks/results/`:

```text
benchmark,language,runs,compile_us,min_us,avg_us,max_us
```

Useful controls:

- `STARK_BENCH_RUNS=10` changes the measured execution count.
- `STARK_BENCH_FILTER=text` limits the run to paths containing a substring.
- `STARK_BENCH_LANGUAGES=stark,c,rust` selects which language rows to run.
- `STARK_TARGET=...` passes an LLVM target triple to the compiler.
- `STARK_COMPILER_ARGS=...` adds compiler arguments for local experiments.
- `STARK_BENCH_C_COMPILER=clang` selects the C compiler command.
- `STARK_BENCH_RUST_COMPILER=rustc` selects the Rust compiler command.
- `STARK_BENCH_OUTPUT_DIR=...` changes where timestamped CSV and machine
  metadata files are written.
- `STARK_BENCH_RESULTS_FILE=...` writes CSV to an explicit file.
- `STARK_BENCH_MACHINE_FILE=...` writes host/toolchain metadata to an explicit
  file.

Benchmarks marked with `// stark-bench: compile-only` are codegen regression
sources. The executable runner skips them, but `BenchmarkSourceTests` still
checks that every benchmark source lowers successfully and does not fall back to
direct `malloc`, `realloc`, or `free` declarations in generated LLVM IR.

## Publication Bar

These numbers are useful for local compiler work, but they are not yet
publication-quality performance claims. The formal benchmark suite still needs:

- IO, networking, and parser/text-processing benchmark families
- variance reporting across repeated independent invocations
- regression thresholds that can fail CI when performance drops materially

Until that suite exists, treat this page as a map of what the repository
exercises today, not as a claim that Stark is faster than C or Rust on these
workloads.
