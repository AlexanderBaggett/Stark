# Stark Benchmarks

This directory contains small source-level benchmark programs for checking
Stark runtime and codegen changes. The goal is quick feedback, not publication
quality performance numbers.

Formal Stark/C/Rust comparison rules live in
[`Fairness.md`](Fairness.md). A result is publishable only when it follows those
rules, has equivalent C and Rust baselines, and includes recorded machine
metadata.

## Running

```bash
scripts/run-benchmarks.sh
```

The runner compiles executable Stark, C, and Rust benchmarks, performs one
warmup execution per binary, and prints CSV rows:

```text
benchmark,language,runs,compile_us,min_us,avg_us,max_us
```

Each executable Stark benchmark must have same-stem C and Rust counterparts
when those languages are selected. For example:

```text
benchmarks/micro/Calls.stark
benchmarks/micro/Calls.c
benchmarks/micro/Calls.rs
```

Useful environment variables:

- `STARK_BENCH_RUNS`: measured executions per benchmark after one warmup run.
  Defaults to `3`.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
- `STARK_BENCH_LANGUAGES`: comma-separated language list. Defaults to
  `stark,c,rust`.
- `STARK_TARGET`: optional LLVM target triple passed to the compiler.
- `STARK_COMPILER_ARGS`: extra compiler arguments.
- `STARK_BENCH_C_COMPILER`: C compiler command. Defaults to `clang`.
- `STARK_BENCH_RUST_COMPILER`: Rust compiler command. Defaults to `rustc`.
- `STARK_BENCH_OUTPUT_DIR`: directory for timestamped CSV and metadata files.
  Defaults to `benchmarks/results/`.
- `STARK_BENCH_RESULTS_FILE`: explicit CSV output path.
- `STARK_BENCH_MACHINE_FILE`: explicit machine metadata output path.

Rust benchmarks may include additional same-stem variants named
`<benchmark>.rust-<variant>.rs`. When Rust rows are enabled, the runner emits
the ordinary `rust` row plus one row per variant, labeled `rust-<variant>`.

Each Stark benchmark is a standalone `.stark` program with an `export ffi main`.
C and Rust counterparts should perform equivalent source-level work, validate
the same observable result, and use the same benchmark stem. Counterparts should
be natural C/Rust implementations of the same scenario rather than line-by-line
translations of Stark runtime details. Benchmarks should keep their own internal
iteration counts modest so the full harness stays fast enough to run during
normal development.

Benchmarks marked with `// stark-bench: compile-only` are compiler/codegen
regression sources rather than executable timing benchmarks. The executable
runner skips them, while `BenchmarkSourceTests` still verifies that they lower
successfully.

Each run writes:

- `results-<timestamp>.<unique>.csv`: benchmark path, measured runs, compile
  time, and min/average/max runtime in microseconds.
- `machine-<timestamp>.<unique>.txt`: repository, host, CPU, memory, OS, and
  compiler metadata needed to interpret the results.

The locked default flags are:

- Stark: `--emit-exe -O3`
- C: `clang -O3 -DNDEBUG -std=c17`
- Rust: `rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no`

## Current Coverage

- `micro/Arithmetic.stark`, `micro/Branching.stark`, `micro/Calls.stark`, and
  `micro/MemoryAccess.stark` cover tight scalar arithmetic, branch dispatch,
  no-inline call overhead, and stack-array access loops.
- `allocator/HeapLocalBucketReuse.stark` exercises heap-local allocation and
  scope cleanup through the default allocator buckets. It includes an additional
  `rust-fixed-batch` baseline that stores `Box` allocations in a fixed
  `Option<Box<_>>` batch instead of a `Vec`.
- `allocator/SystemMemoryBucketReallocate.stark` exercises bucket-backed
  `System.Memory.Reallocate` in-place reuse.
- `allocator/SystemMemoryFallbackReallocate.stark` exercises the conservative
  allocate-copy-free fallback when a reallocation no longer fits the old bucket.
- `collections/ListGrowth.stark` and `collections/QueueGrowth.stark` are
  executable growth benchmarks for the first owned collections.
- `collections/ListIteration.stark` and `collections/DictionaryLookup.stark`
  exercise indexed list iteration and integer-key hash-table lookup.
- `text/OwnedTextAllocation.stark` is an executable benchmark for allocation-
  visible owned `ToAscii`/`ToUnicode` conversion and literal-prefix
  concatenation through `System.Memory`.
- `text/OwnedPathAllocation.stark` is an executable benchmark for allocation-
  visible owned path joining plus path-view inspection helpers.
- `text/AsciiToUnicodeConversion.stark` is an executable benchmark for the
  caller-buffer ASCII-to-Unicode conversion fast path.
- `text/TextPathCallerBuffer.stark` is a compile-only benchmark for the current
  caller-owned path buffer helpers and low-level text conversion helpers.
- `network/TcpLoopbackThroughput.stark` is an executable loopback benchmark for
  the public `System.Net.Tcp` listener/client write-read path.
