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

On Windows, use:

```powershell
scripts\run-benchmarks.ps1
```

Both runners compile executable Stark, C, and Rust benchmarks and perform one
warmup execution per binary.

The Bash runner records executable size plus Stark object/link/toolchain timing
and prints CSV rows:

```text
benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us
```

The Windows PowerShell runner currently prints:

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
  Defaults to `50`. Set it lower for quick smoke runs.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
- `STARK_BENCH_LANGUAGES`: comma-separated language list. Defaults to
  `stark,c,rust`.
- `STARK_BENCH_TIMEOUT_SECONDS`: per-executable warmup/run timeout. Defaults
  to `30`; set to `0` to disable when using a platform without `timeout`.
- `STARK_TARGET`: optional LLVM target triple passed to the compiler.
- `STARK_COMPILER_ARGS`: extra compiler arguments.
- `STARK_BENCH_C_COMPILER`: C compiler command. Defaults to `clang`.
- `STARK_BENCH_RUST_COMPILER`: Rust compiler command. Defaults to `rustc`.
- `STARK_BENCH_OUTPUT_DIR`: directory for timestamped CSV and metadata files.
  Defaults to `benchmarks/results/`.
- `STARK_BENCH_RESULTS_FILE`: explicit CSV output path.
- `STARK_BENCH_MACHINE_FILE`: explicit machine metadata output path.
- `STARK_BENCH_BASELINE_FILE`: optional previous CSV to compare against after
  the run.
- `STARK_BENCH_REQUIRE_BASELINE`: set to `1` to fail when a gate is configured
  without a baseline or when current rows are missing from the baseline.
- `STARK_BENCH_MAX_REGRESSION_PCT`: allowed same-language regression against
  the baseline. Defaults to `10` when a baseline is configured.
- `STARK_BENCH_REGRESSION_METRIC`: metric column for baseline checks. Defaults
  to `avg_us`; use `compile_us` for compiler-time gates, `llvm_object_us` for
  Stark LLVM object-generation gates, `link_us` or `toolchain_us` for native
  backend/LTO gates, or `binary_bytes` for code-size gates.
- `STARK_BENCH_MIN_REGRESSION_DELTA`: minimum absolute delta before a
  regression gate can fail. Defaults to `50`.
- `STARK_BENCH_MAX_STARK_TO_C_RATIO` and
  `STARK_BENCH_MAX_STARK_TO_RUST_RATIO`: optional same-run gates for Stark
  relative to the C and Rust rows.

Rust benchmarks may include additional same-stem variants named
`<benchmark>.rust-<variant>.rs`. When Rust rows are enabled, the runner emits
the ordinary `rust` row plus one row per variant, labeled `rust-<variant>`.

To check an existing result file without rerunning benchmarks:

```bash
scripts/check-benchmark-regressions.sh current.csv baseline.csv
```

For a local or CI gate, pin a machine-specific baseline and run:

```bash
STARK_BENCH_BASELINE_FILE=benchmarks/baselines/linux-x64.csv \
STARK_BENCH_MAX_REGRESSION_PCT=10 \
scripts/run-benchmarks.sh
```

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

C counterparts marked with `// stark-bench: skip-c-windows` are skipped by the
Windows PowerShell runner when C rows are enabled. Use this only for C baselines
that are currently POSIX-specific; the Stark and Rust rows for the benchmark
still run normally.

Each run writes:

- `results-<timestamp>.<unique>.csv`: benchmark path, measured runs, compile
  time, Stark LLVM object-generation time, link/toolchain time, binary size,
  and min/average/max runtime in microseconds.
- `machine-<timestamp>.<unique>.txt`: repository, host, CPU, memory, OS, and
  compiler metadata needed to interpret the results.

The locked default flags are:

- Stark: `--emit-exe -O3`
- C: `clang -O3 -DNDEBUG -std=c17`
- Rust: `rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no`

## Current Coverage

- `micro/Arithmetic.stark`, `micro/AbstractionHandWritten.stark`,
  `micro/AbstractionLawWrapper.stark`, `micro/AbstractionGenericWrapper.stark`,
  `micro/Branching.stark`, `micro/Calls.stark`,
  `micro/AlgebraicIdentitySimplification.stark`, `micro/BitwiseRangePruning.stark`,
  `micro/DirectCallInlining.stark`, `micro/ExplicitArithmeticRangePruning.stark`,
  `micro/FactDrivenBranchPruning.stark`, `micro/FunctionPointerDevirtualization.stark`,
  `micro/NullBranchPruning.stark`, `micro/TextLiteralLengthPruning.stark`,
  `micro/StackScalarLoadForwarding.stark`, and `micro/MemoryAccess.stark`
  cover tight scalar arithmetic, abstraction parity
  between hand-written monomorphic code, law wrappers, and generic wrappers,
  branch dispatch, no-inline call overhead, integer identity rewrites,
  bitwise-derived range pruning, Stark-level direct-call inlining through a short wrapper chain,
  explicit wrapping and saturating arithmetic range pruning,
  branch/switch pruning from propagated facts, known-function-pointer
  devirtualization, nullability-derived branch pruning, text-literal length
  pruning, stack-scalar load forwarding, and stack-array access loops.
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
