# Stark Benchmarks

This directory contains small source-level benchmark programs for checking
Stark runtime and codegen changes. The goal is quick feedback, not publication
quality performance numbers.

## Running

```bash
scripts/run-benchmarks.sh
```

Useful environment variables:

- `STARK_BENCH_RUNS`: measured executions per benchmark after one warmup run.
  Defaults to `3`.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
- `STARK_TARGET`: optional LLVM target triple passed to the compiler.
- `STARK_COMPILER_ARGS`: extra compiler arguments.

Each benchmark is a standalone `.stark` program with an `export ffi main`.
Benchmarks should keep their own internal iteration counts modest so the full
harness stays fast enough to run during normal development.

Benchmarks marked with `// stark-bench: compile-only` are compiler/codegen
regression sources rather than executable timing benchmarks. The executable
runner skips them, while `BenchmarkSourceTests` still verifies that they lower
successfully.

## Current Coverage

- `allocator/HeapLocalBucketReuse.stark` exercises heap-local allocation and
  scope cleanup through the default allocator buckets.
- `allocator/SystemMemoryBucketReallocate.stark` exercises bucket-backed
  `System.Memory.Reallocate` in-place reuse.
- `allocator/SystemMemoryFallbackReallocate.stark` exercises the conservative
  allocate-copy-free fallback when a reallocation no longer fits the old bucket.
- `collections/ListGrowth.stark` and `collections/QueueGrowth.stark` are
  executable growth benchmarks for the first owned collections.
- `text/OwnedTextAllocation.stark` is an executable benchmark for allocation-
  visible owned `ToAscii`/`ToUnicode` conversion and literal-prefix
  concatenation through `System.Memory`.
- `text/OwnedPathAllocation.stark` is an executable benchmark for allocation-
  visible owned path joining plus path-view inspection helpers.
- `text/TextPathCallerBuffer.stark` is a compile-only benchmark for the current
  caller-owned path buffer helpers and low-level text conversion helpers.
