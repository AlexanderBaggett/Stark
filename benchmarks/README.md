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

## Current Coverage

- `allocator/HeapLocalBucketReuse.stark` exercises heap-local allocation and
  scope cleanup through the default allocator buckets.
- `allocator/SystemMemoryBucketReallocate.stark` exercises bucket-backed
  `System.Memory.Reallocate` in-place reuse.
- `allocator/SystemMemoryFallbackReallocate.stark` exercises the conservative
  allocate-copy-free fallback when a reallocation no longer fits the old bucket.

Future collection and owned-buffer benchmarks should live here once those APIs
allocate through `System.Memory`.
