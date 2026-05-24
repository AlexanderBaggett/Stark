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
and writes CSV rows:

```text
benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib,c_avg_ratio
```

The Windows PowerShell runner records the same Stark compile/toolchain timing
plus median runtime and writes CSV rows:

```text
benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,median_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib,c_median_ratio,c_avg_ratio
```

Set `STARK_BENCH_CAPTURE_RSS=1` to capture peak RSS. On Linux, the Bash runner
samples `/proc/<pid>/status` while each benchmark process runs and records the
largest observed `VmHWM` value in `peak_rss_kib`. On Windows, the PowerShell
runner records `Process.PeakWorkingSet64` after each benchmark process exits. A
`0` value means peak RSS capture was disabled, unavailable on that host, or the
Linux process exited before the sampler observed it.

Ratio columns are calculated after the last benchmark finishes. The Windows
PowerShell runner makes `c_median_ratio` the primary runtime comparison:
`row median_us / C median_us`. It also keeps `c_avg_ratio` as an outlier
diagnostic. The Bash runner currently records `c_avg_ratio` only:
`row avg_us / C avg_us`. The C row is `1.000000`; faster rows are below `1.0`,
slower rows are above `1.0`. Rows without a same-benchmark C result leave the
ratio blank. To add or refresh the Bash `c_avg_ratio` column on an existing
Linux/macOS result file without rerunning benchmarks:

```bash
scripts/add-benchmark-c-ratios.sh benchmarks/results/results-file.csv
```

The Bash runner also records `runtime_spread_pct`, calculated from one run's
samples as `(max_us - min_us) / avg_us * 100`. Treat high spread as a warning
that the average may not be stable enough for a performance conclusion. On
Windows, prefer `median_us` and `c_median_ratio` for standard-library runtime
comparisons; use `avg_us`, `max_us`, and `runtime_spread_pct` to spot noisy
runs.

Each benchmark scenario has one canonical Stark implementation plus any C and
Rust counterparts. The `benchmark` column never includes implementation
prefixes; the `language` column identifies the compiled source. For example:

```text
benchmarks/text/OwnedTextAllocation,stark
benchmarks/text/OwnedTextAllocation,c
benchmarks/text/OwnedTextAllocation,rust
```

Useful environment variables:

- `STARK_BENCH_RUNS`: measured executions per benchmark after one warmup run.
  Defaults to `100`. Set it lower for quick smoke runs.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
- `STARK_BENCH_SUBSET`: PowerShell runner shortcut for targeted Windows
  investigations. Supported values are `allocator`, `console`, `directory`,
  `file`, `socket`, `network`, `windows-io`, and `windows-core`.
- `STARK_BENCH_LANGUAGES`: comma-separated language list. Defaults to
  `stark,c,rust`.
- `STARK_BENCH_TIMEOUT_SECONDS`: per-executable warmup/run timeout. Defaults
  to `30`; set to `0` to disable when using a platform without `timeout`.
- `STARK_BENCH_CAPTURE_RSS`: set to `1` to collect `peak_rss_kib`. Defaults to
  `0` so ordinary timing runs avoid sampler overhead.
- `STARK_BENCH_RSS_POLL_INTERVAL_SECONDS`: Linux `/proc` peak RSS sampling
  interval for the Bash runner when RSS capture is enabled. Defaults to `0.002`.
- `STARK_TARGET`: optional LLVM target triple passed to the compiler.
- `STARK_COMPILER_ARGS`: extra compiler arguments.
- `STARK_BENCH_C_COMPILER`: C compiler command. Defaults to `clang`.
- `STARK_BENCH_RUST_COMPILER`: Rust compiler command. Defaults to `rustc`.
- `STARK_BENCH_OUTPUT_DIR`: directory for timestamped CSV and metadata files.
  Defaults to `benchmarks/results/`.
- `STARK_BENCH_RESULTS_FILE`: explicit CSV output path.
- `STARK_BENCH_MACHINE_FILE`: explicit machine metadata output path.
- `STARK_BENCH_BINARY_DIR`: PowerShell runner directory for preserved
  benchmark executables. Use it with `STARK_BENCH_KEEP_BINARIES=1` for the
  compile pass, then with `STARK_BENCH_RUNTIME_ONLY=1` for repeated runtime
  measurements that amortize compile, ThinLTO, and `lld-link` cost.
- `STARK_BENCH_KEEP_BINARIES`: PowerShell runner flag that keeps compiled
  executables in `STARK_BENCH_BINARY_DIR`.
- `STARK_BENCH_RUNTIME_ONLY`: PowerShell runner flag that reuses existing
  executables from `STARK_BENCH_BINARY_DIR` and reports compile/toolchain
  fields as `0`.
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

For Windows standard-library investigations, keep linker cost separate from
payload runtime. `compile_us` is total build wall time; Stark rows may also
report `llvm_object_us`, `link_us`, and `toolchain_us` from compiler toolchain
metrics. If `lld-link` or ThinLTO dominates the run, preserve binaries and use
runtime-only mode before drawing conclusions about library code. Windows rows
for benchmarks with `skip-c-windows` C counterparts intentionally have no C
ratio until a Windows C baseline exists.

Each run writes:

- `results-<timestamp>.<unique>.csv`: benchmark path, measured runs, compile
  time, Stark LLVM object-generation time, link/toolchain time, binary size,
  min/average/max runtime in microseconds, runtime spread percentage, and peak
  RSS in KiB.
- `machine-<timestamp>.<unique>.txt`: repository, host, CPU, memory, OS, and
  compiler metadata needed to interpret the results.

The locked default flags are:

- Stark: `--emit-exe -O3`
- C: `clang -O3 -DNDEBUG -std=c17`
- Rust: `rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no`

## Current Coverage

- `micro/*.stark` covers scalar arithmetic, abstraction parity, calls,
  branch/fact pruning, direct-call inlining, range pruning, text-literal facts,
  stack scalarization, dead-store narrowing, and stack-array access loops.
- `allocator/*.stark` covers heap-local bucket reuse, bucket and fallback
  reallocation, dynamic reserve growth, byte/code-point copy, fill, and
  overlap-safe move helpers. Natural C and Rust counterparts are included where
  the scenario is executable.
- `collections/*.stark` covers list, stack, queue, linked-list, and dictionary
  growth, iteration, churn, lookup, insert, update, remove, and mixed workloads
  through the canonical `System.Collections` API.
- `runtime/*.stark` covers fixed and dynamic runtime buffers: write/copy,
  fill, read/advance, compact, clear, and repeated growth behavior.
- `io/*.stark` and `console/*.stark` cover canonical file, filesystem, path,
  buffered read/write, directory enumeration, console write, and console read
  surfaces against C and Rust baselines where applicable.
- `text/*.stark` covers owned text/path allocation, concat/copy, integer and
  Unicode formatting, parsing, path facts, join, normalize, queries, repeated
  small path operations, ASCII-to-Unicode conversion, and caller-buffer helper
  lowering.
- `network/*.stark` covers loopback TCP throughput and scatter/gather-style
  socket paths through the canonical `System.Net.Tcp` API.
