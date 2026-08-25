+++
title = "Benchmarks"
weight = 50
+++

Stark benchmarks live in the repository `benchmarks/` directory. The suite now
contains 74 Stark benchmark sources across allocator, collections, console, IO,
micro, network, runtime-buffer, text, and path workloads.

Executable scenarios use same-stem Stark, C, and Rust sources so local runs can
compare equivalent source-level work across languages:

```text
benchmarks/text/OwnedTextAllocation.stark
benchmarks/text/OwnedTextAllocation.c
benchmarks/text/OwnedTextAllocation.rs
```

The comparison contract lives in
[`benchmarks/Fairness.md`](/reference/benchmarks/Fairness.md). It defines the
fairness rules, default optimization levels, timing rules, and gap
classification vocabulary used when comparing Stark against C and Rust.

## Current Coverage

| Suite | Stark sources | Focus |
| --- | ---: | --- |
| allocator | 5 | heap-local reuse, reserve growth, reallocation, copy/fill/move helpers |
| collections | 16 | list, stack, queue, linked-list, and dictionary workloads |
| console | 2 | console writes plus compile-only read-surface coverage |
| io | 3 | directory enumeration, buffered files, filesystem path transcoding |
| micro | 27 | arithmetic, calls, branch facts, alias facts, scalar forwarding, inlining |
| network | 2 | loopback TCP throughput and scatter/gather-style socket paths |
| runtime | 2 | fixed and dynamic runtime buffers |
| text | 17 | text allocation, formatting, parsing, paths, ASCII/Unicode conversion |

### Micro And Optimizer Probes

The `micro/` suite exercises small code shapes that are useful when changing
MIR, SSA, LLVM emission, and optimization passes:

- Arithmetic and bit facts: `Arithmetic`, `AlgebraicIdentitySimplification`,
  `BitwiseRangePruning`, and `ExplicitArithmeticRangePruning`.
- Branch and switch facts: `Branching`, `BranchSelectPredication`,
  `FactDrivenBranchPruning`, `NullBranchPruning`, and
  `TextLiteralLengthPruning`.
- Calls and abstraction costs: `Calls`, `DirectCallInlining`,
  `FunctionPointerDevirtualization`, `AbstractionHandWritten`,
  `AbstractionLawWrapper`, and `AbstractionGenericWrapper`.
- Load/store and alias facts: `MemoryAccess`, `StackScalarLoadForwarding`,
  `StackFieldLoadForwarding`, `StackNestedFieldForwarding`,
  `StackFieldBranchForwarding`, `AggregatePhiFieldForwarding`,
  `DeadStackFieldStore`, `ReadonlyOtherLocalFieldStore`,
  `GlobalScalarLoadForwarding`, `PureCallGlobalForwarding`,
  `IndependentSliceAdd`, and `IndependentRawPointerRegions`.

### Allocator, Runtime, And Collections

The allocator suite covers `HeapLocalBucketReuse`, `MemoryCopyFill`,
`MemoryDynamicReserveGrowth`, `SystemMemoryBucketReallocate`, and
`SystemMemoryFallbackReallocate`.

The runtime suite covers `RuntimeBufferFixed` and `RuntimeBufferDynamic`, with
write/copy, fill, read/advance, compact, clear, and repeated-growth behavior.

The collections suite covers the canonical `System.Collections` APIs:
`ListGrowth`, `ListIteration`, `StackGrowth`, `QueueGrowth`, `QueueDequeue`,
`QueueChurn`, `LinkedListPush`, `LinkedListReservedPush`,
`LinkedListPopOnly`, `LinkedListBuildClear`, `LinkedListChurn`,
`DictionaryInsert`, `DictionaryLookup`, `DictionaryUpdate`,
`DictionaryRemove`, and `DictionaryMixed`.

### Text, Paths, IO, Console, And Network

Text and path benchmarks include owned text/path allocation, concatenation,
formatting, parsing, path facts, path joins, normalization, repeated small path
operations, ASCII-to-Unicode conversion, and widening kernels:
`OwnedTextAllocation`, `OwnedPathAllocation`, `TextConcatCopy`,
`ConstantIntegerFormatting`, `UnicodeFormatting`, `TextParsing`, `PathFacts`,
`PathJoin`, `PathNormalize`, `PathQueries`, `PathRepeatedSmallOps`,
`AsciiToUnicodeConversion`, `AsciiToUnicodeConversionRuntime`,
`AsciiToUnicodeConversionTinyLiteral`,
`AsciiToUnicodeLargeLiteralSpecialization`, and
`AsciiToUnicodeWideningKernel`.

IO and console coverage includes `DirectoryEnumeration`,
`FileBufferedReadWrite`, `FileSystemPathTranscode`, `ConsoleWrites`, and the
compile-only `ConsoleReadSurface` source. Network coverage includes
`TcpLoopbackThroughput` and `TcpScatterGatherLoopback` through
`System.Net.Tcp`.

Two Stark sources are compile-only regression tests rather than executable
timing benchmarks: `benchmarks/console/ConsoleReadSurface.stark` and
`benchmarks/text/TextPathCallerBuffer.stark`. The executable runner skips them,
while `BenchmarkSourceTests` still checks that they lower successfully.

Rust benchmarks may also include same-stem variants named
`<benchmark>.rust-<variant>.rs`. For example,
`allocator/HeapLocalBucketReuse.rust-fixed-batch.rs` emits an additional
`rust-fixed-batch` row when Rust rows are enabled.

## Running Locally

Use the repository benchmark harness:

```bash
scripts/run-benchmarks.sh
```

On Windows, use:

```powershell
scripts\run-benchmarks.ps1
```

Both runners compile executable Stark, C, and Rust benchmarks, perform one
warmup execution per binary, and write timestamped CSV and machine metadata
under `benchmarks/results/`.

The Bash runner writes rows shaped like this:

```text
benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib,c_avg_ratio
```

The Windows PowerShell runner also records median runtime and C-median ratios:

```text
benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,median_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib,c_median_ratio,c_avg_ratio
```

Useful quick runs:

```bash
STARK_BENCH_FILTER=benchmarks/text scripts/run-benchmarks.sh
STARK_BENCH_LANGUAGES=stark,c STARK_BENCH_RUNS=5 scripts/run-benchmarks.sh
STARK_BENCH_CAPTURE_RSS=1 scripts/run-benchmarks.sh
```

Important controls:

- `STARK_BENCH_RUNS`: measured executions after warmup. The default is `100`.
- `STARK_BENCH_FILTER`: substring filter matched against benchmark file paths.
- `STARK_BENCH_LANGUAGES`: comma-separated language list, default
  `stark,c,rust`.
- `STARK_BENCH_SUBSET`: PowerShell shortcut for targeted Windows runs:
  `allocator`, `console`, `directory`, `file`, `socket`, `network`,
  `windows-io`, or `windows-core`.
- `STARK_BENCH_CAPTURE_RSS`: set to `1` to collect `peak_rss_kib`.
- `STARK_TARGET` and `STARK_COMPILER_ARGS`: optional Stark target and extra
  compiler arguments.
- `STARK_BENCH_C_COMPILER` and `STARK_BENCH_RUST_COMPILER`: C and Rust compiler
  commands.
- `STARK_BENCH_OUTPUT_DIR`, `STARK_BENCH_RESULTS_FILE`, and
  `STARK_BENCH_MACHINE_FILE`: result and metadata output controls.
- `STARK_BENCH_BASELINE_FILE`, `STARK_BENCH_MAX_REGRESSION_PCT`,
  `STARK_BENCH_REGRESSION_METRIC`, and `STARK_BENCH_MIN_REGRESSION_DELTA`:
  same-language baseline gates.
- `STARK_BENCH_MAX_STARK_TO_C_RATIO` and
  `STARK_BENCH_MAX_STARK_TO_RUST_RATIO`: same-run gates against C and Rust rows.

The locked default flags are:

- Stark: `--emit-exe` (the compiler always uses its full optimization pipeline)
- C: `clang -O3 -DNDEBUG -std=c17`
- Rust: `rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no`

More runner details are in
[`benchmarks/README.md`](/reference/benchmarks/README.md).

## Reading Results

Compile time, link time, executable size, runtime, runtime spread, and peak RSS
are separate signals. A fast payload with expensive linking is a different
problem from a slow payload with a small binary.

The C ratio columns divide each row by the same benchmark's C row. Lower than
`1.0` is faster than the C baseline for that run, higher than `1.0` is slower,
and a blank ratio means no same-benchmark C row was available. Treat high
`runtime_spread_pct` as a warning that the run may be too noisy for a
performance conclusion.

For Windows standard-library investigations, prefer `median_us` and
`c_median_ratio`. Use runtime-only mode with preserved binaries when linker or
ThinLTO cost would otherwise hide the payload runtime.

## Publication Bar

These numbers are useful for local compiler and standard-library work, but they
are not automatically publication-quality performance claims. Before publishing
results, a benchmark needs equivalent C and Rust baselines, natural code for
each language, validated observable output, recorded machine/toolchain
metadata, and enough repeated measurement to understand variance.

Remaining suite work is mostly about consolidation and confidence:

- Curate a smaller public result set from the larger development suite.
- Add repeated independent invocations and variance summaries.
- Pin machine-specific baselines and CI policies for release branches.
- Review C and Rust baselines for every published scenario.
- Keep Linux, Windows, and macOS coverage aligned where platform APIs differ.
- Add benchmark narratives for representative workloads so ratios have context.
