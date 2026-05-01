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

The Windows PowerShell runner currently writes:

```text
benchmark,language,runs,compile_us,min_us,avg_us,max_us,peak_rss_kib,c_avg_ratio
```

Set `STARK_BENCH_CAPTURE_RSS=1` to capture peak RSS. On Linux, the Bash runner
samples `/proc/<pid>/status` while each benchmark process runs and records the
largest observed `VmHWM` value in `peak_rss_kib`. On Windows, the PowerShell
runner records `Process.PeakWorkingSet64` after each benchmark process exits. A
`0` value means peak RSS capture was disabled, unavailable on that host, or the
Linux process exited before the sampler observed it.

The `c_avg_ratio` column is calculated after the last benchmark finishes. It
uses the average runtime for the same benchmark: `row avg_us / C avg_us`.
The C row is `1.000000`; faster rows are below `1.0`, slower rows are above
`1.0`. Rows without a same-benchmark C result leave the ratio blank. To add or
refresh this column on an existing Linux/macOS result file without rerunning
benchmarks:

```bash
scripts/add-benchmark-c-ratios.sh benchmarks/results/results-file.csv
```

The Bash runner also records `runtime_spread_pct`, calculated from one run's
samples as `(max_us - min_us) / avg_us * 100`. Treat high spread as a warning
that the average may not be stable enough for a performance conclusion.

Stable and experimental Stark variants share one canonical benchmark name in the
CSV; the variant lives in the `language` column as either `stark` or
`stark-experimental`. The `benchmark` column never includes implementation
prefixes such as `Experimental`; C and Rust baselines are compiled once from the
canonical scenario path. For example:

```text
benchmarks/text/OwnedTextAllocation,stark
benchmarks/text/OwnedTextAllocation,stark-experimental
benchmarks/text/OwnedTextAllocation,c
benchmarks/text/OwnedTextAllocation,rust
```

Useful environment variables:

- `STARK_BENCH_RUNS`: measured executions per benchmark after one warmup run.
  Defaults to `20`. Set it lower for quick smoke runs.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
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
  min/average/max runtime in microseconds, runtime spread percentage, and peak
  RSS in KiB.
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
  `micro/StackScalarLoadForwarding.stark`,
  `micro/ReadonlyOtherLocalFieldStore.stark`, `micro/DeadStackFieldStore.stark`,
  and `micro/MemoryAccess.stark`
  cover tight scalar arithmetic, abstraction parity
  between hand-written monomorphic code, law wrappers, and generic wrappers,
  branch dispatch, no-inline call overhead, integer identity rewrites,
  bitwise-derived range pruning, Stark-level direct-call inlining through a short wrapper chain,
  explicit wrapping and saturating arithmetic range pruning,
  branch/switch pruning from propagated facts, known-function-pointer
  devirtualization, nullability-derived branch pruning, text-literal length
  pruning, stack-scalar load forwarding, readonly-call stack-field dead-store
  narrowing, SROA dead stack-field store removal, and stack-array access loops.
- `allocator/HeapLocalBucketReuse.stark` exercises heap-local allocation and
  scope cleanup through the default allocator buckets. It includes an additional
  `rust-fixed-batch` baseline that stores `Box` allocations in a fixed
  `Option<Box<_>>` batch instead of a `Vec`. A `stark-experimental` row runs
  the same Stark heap-local workload for side-by-side reporting.
- `allocator/SystemMemoryBucketReallocate.stark` exercises bucket-backed
  `System.Memory.Reallocate` in-place reuse. A `stark-experimental` row runs
  the same allocator workload for side-by-side reporting.
- `allocator/SystemMemoryFallbackReallocate.stark` exercises the conservative
  allocate-copy-free fallback when a reallocation no longer fits the old bucket.
  A `stark-experimental` row runs the same allocator workload for side-by-side
  reporting.
- `allocator/MemoryDynamicReserveGrowth` compares regular Stark dynamic reserve,
  append-copy, and append-fill code with the `System.Experimental.Memory`
  helper row against natural C/Rust growable-buffer baselines.
- `allocator/MemoryCopyFill` compares regular Stark byte/codepoint copy, fill,
  and move code with the experimental safe helper row, including disjoint copy
  kernels, initialized-fill kernels, and overlap-safe move work.
- The `runtime/RuntimeBufferFixed` `stark-experimental` row exercises
  `System.Experimental.Runtime.Buffer.FixedByteBuffer512` write/copy, fill,
  read/advance, compact, and clear operations against natural C/Rust
  fixed-capacity byte-buffer baselines.
- The `runtime/RuntimeBufferDynamic` `stark-experimental` row exercises
  `System.Experimental.Runtime.Buffer.DynamicByteBuffer` repeated growth,
  slice writes, read advancement, compaction, and fill appends against natural
  C/Rust growable byte-buffer baselines.
- The `io/FileBufferedReadWrite` `stark-experimental` row exercises
  `System.Experimental.IO.File` safe byte-slice reads/writes, dynamic-buffer
  writes, fixed-buffer writes, seeks, flush/close, and file cleanup against
  natural C/Rust buffered file baselines.
- The `io/FileSystemPathTranscode` `stark-experimental` row exercises
  `System.Experimental.FileSystem` directory create/open/read/delete flows,
  `System.Experimental.IO.Path` join/normalize helpers, path-heavy file moves,
  and UTF-16-style text writes against natural C/Rust filesystem baselines.
- `collections/ListGrowth.stark`, `collections/StackGrowth.stark`, and
  `collections/QueueGrowth.stark` are executable growth benchmarks for the
  contiguous owned collections.
- The `collections/ListGrowth` `stark-experimental` row exercises the
  `System.Experimental.Collections.List<T>` comparison implementation through
  its public API so it can be measured directly against the stable raw-pointer
  list and the natural C/Rust baselines.
- The `collections/ListIteration` `stark-experimental` row runs the same
  push-and-indexed iteration workload through
  `System.Experimental.Collections.List<T>`.
- The `collections/StackGrowth` `stark-experimental` row does the same for
  `System.Experimental.Collections.Stack<T>`, keeping the stack API in the
  measured path instead of benchmarking only the underlying dynamic storage.
- The `collections/QueueGrowth` `stark-experimental` row measures the same
  queue growth workload through `System.Experimental.Collections.Queue<T>` with
  the same natural C/Rust baselines as `QueueGrowth`.
- `collections/ListIteration.stark`, `collections/LinkedListPush.stark`,
  `collections/LinkedListBuildClear.stark`, `collections/LinkedListPopOnly.stark`,
  `collections/LinkedListChurn.stark`, `collections/LinkedListReservedPush.stark`,
  and `collections/DictionaryLookup.stark`
  exercise indexed list iteration, linked-list build-and-drain, linked-list bulk
  clear, linked-list prebuild plus back-pop drain, linked-list add/remove churn,
  explicit Stark node reservation before build-and-drain, and integer-key
  hash-table lookup. `LinkedListPopOnly` still includes prebuild setup in total
  process time until the harness supports in-process measured sections.
  `LinkedListReservedPush` includes the Stark reservation call in total process
  time; it validates the public performance knob against natural C/Rust
  linked-list baselines rather than isolating post-reserve hot-loop cost.
  `DictionaryLookup` pre-reserves the Stark dictionary so setup matches the C
  fixed-capacity table and Rust `HashMap::with_capacity` baseline.
- The `collections/LinkedListPush`, `collections/LinkedListBuildClear`,
  `collections/LinkedListPopOnly`, `collections/LinkedListChurn`, and
  `collections/LinkedListReservedPush` `stark-experimental` rows run the same
  linked-list scenarios through `System.Experimental.Collections.LinkedList<T>`.
  The C and Rust files are intentionally the same natural baselines as the
  stable linked-list benchmarks so the Stark stable-vs-experimental comparison
  changes only the Stark implementation under test.
- `text/OwnedTextAllocation.stark` is an executable benchmark for allocation-
  visible owned `ToAscii`/`ToUnicode` conversion and literal-prefix
  concatenation through `System.Memory`.
- The `text/OwnedTextAllocation` `stark-experimental` row runs the same
  allocation-visible owned conversion and literal-prefix concatenation scenario
  through `System.Experimental.Text`.
- `text/OwnedPathAllocation.stark` is an executable benchmark for allocation-
  visible owned path joining plus path-view inspection helpers.
- The `text/OwnedPathAllocation` `stark-experimental` row runs the same
  allocation-visible path joining and inspection scenario through
  `System.Experimental.IO.Path`.
- `text/TextConcatCopy`, `text/IntegerFormatting`, `text/UnicodeFormatting`,
  and `text/TextParsing` compare `stark` and `stark-experimental` text
  copy/concat, fixed-buffer integer formatting, Unicode formatting, and parsing
  paths with one C/Rust baseline per scenario.
- `text/PathFacts` isolates single-pass path analysis and extension/base/
  directory range reuse for stable and experimental path APIs.
- `text/PathJoin`, `text/PathNormalize`, `text/PathQueries`, and
  `text/PathRepeatedSmallOps` cover owned join, separator normalization,
  extension/base/directory queries, and repeated small path operations across
  the available stable and experimental rows.
- `text/AsciiToUnicodeConversionTinyLiteral.stark` is an executable benchmark
  for tiny known-ASCII literals that should lower to direct scalar widening
  stores.
- `text/AsciiToUnicodeConversion.stark` is an executable benchmark for the
  caller-buffer ASCII-to-Unicode conversion fast path on a medium known-ASCII
  literal.
- The `text/AsciiToUnicodeConversion` `stark-experimental` row runs the same
  medium literal conversion scenario through `System.Experimental.Text`.
- `text/AsciiToUnicodeConversionLargeLiteral.stark` is an executable benchmark
  for larger known-ASCII literals that should lower through the UTF-32
  constant plus `llvm.memcpy` specialization path.
- `text/AsciiToUnicodeConversionRuntime.stark` uses the executable path
  (`argv[0]`) as runtime ASCII input so the Stark, C, and Rust rows must
  convert bytes that are not available as compile-time literals.
- `text/AsciiToUnicodeWideningKernel.stark` is an executable ceiling benchmark
  for raw ASCII byte-to-UTF-32 widening without the public `Unicode` wrapper
  checks.
- `text/TextPathCallerBuffer.stark` is a compile-only benchmark for the current
  caller-owned path buffer helpers and low-level text conversion helpers.
- `console/ConsoleWrites` compares stable and experimental console small writes,
  line writes, Unicode writes, stderr writes, and buffer-shaped output against
  natural C/Rust output baselines.
- `console/ConsoleReadSurface` compile-only sources cover stable and
  experimental console read APIs, including experimental owned text decoding and
  caller-provided fixed/dynamic byte buffers.
- `network/TcpLoopbackThroughput.stark` is an executable loopback benchmark for
  the public `System.Net.Tcp` listener/client write-read path.
- The `network/TcpLoopbackThroughput` `stark-experimental` row runs the same
  loopback throughput scenario through `System.Experimental.Net.Tcp`, including
  fixed runtime-buffer writes and reads, with equivalent C/Rust socket baselines.
