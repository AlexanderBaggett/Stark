# Stark Benchmark Fairness Rules

This file defines the first publication contract for comparing Stark against C
and Rust. It is deliberately conservative: until a benchmark has equivalent
implementations, locked flags, recorded machine metadata, and repeatable result
capture, it is a development benchmark rather than a public performance claim.

## Comparison Scope

- Compare equivalent source-level work, not identical syntax.
- Prefer natural baseline code for each language. C and Rust counterparts are
  best-effort equivalents, not forced transliterations of Stark internals.
- Treat a same-stem Stark/C/Rust source set as one benchmark scenario, such as
  `benchmarks/micro/Calls.stark`, `benchmarks/micro/Calls.c`, and
  `benchmarks/micro/Calls.rs`.
- Use the same algorithm, input sizes, data layout intent, and observable
  result checks for Stark, C, and Rust.
- Use standard library types when they are the normal C or Rust counterpart for
  the scenario. Avoid optimizer barriers, inline assembly, raw allocation APIs,
  or benchmark-lab idioms unless the scenario is explicitly measuring that
  behavior.
- Keep allocation, IO, networking, and synchronization explicit in every
  language. Do not hide setup work in one language and time it in another.
- Do not benchmark debug builds.
- Do not compare Stark source that intentionally relies on a missing optimizer
  against hand-specialized C or Rust unless the result is labeled as a known
  compiler gap.
- Keep benchmark bodies large enough to dominate process startup and timer
  overhead, but small enough to run during normal development.

## Locked Optimization Levels

The default publication profile is:

| Language | Compiler | Flags |
| --- | --- | --- |
| Stark | repository compiler | `--emit-exe -O3` |
| C | `clang` | `-O3 -DNDEBUG -std=c17` |
| Rust | `rustc` | `-C opt-level=3 -C debug-assertions=no -C overflow-checks=no` |

Target selection rules:

- Use the host target unless a benchmark run explicitly pins `STARK_TARGET` and
  records the matching C/Rust target flags.
- If CPU-specific flags are used, record them for all languages. Do not give
  `-march=native` or equivalent to only one language.
- Prefer static benchmark inputs checked into the repository over generated
  inputs. If inputs are generated, record the generator version and seed.

## Timing Rules

- Compile time and runtime are separate metrics.
- Runtime measurements include one untimed warmup run before measured runs.
- Publish `min`, `avg`, and `max` for measured runs, plus the run count.
- Report result CSV timings in microseconds so sub-millisecond benchmark
  differences remain visible.
- Record one row per benchmark scenario and language.
- Record machine and toolchain metadata with every result file.
- Treat the current shell harness as a coarse runner. Publication-quality
  numbers still need stable hardware notes, variance reporting, and repeated
  independent invocations.

## Result Validity

Every benchmark must validate an observable result so optimizing compilers
cannot remove the measured work. Valid checks include status codes, checksums,
file contents, byte counts, or protocol counters. A benchmark that only loops
without checking a result is not valid for comparison.

## Gap Classification

When Stark is materially slower than C or Rust, classify the cause before adding
optimization work:

- frontend semantics or missing source restriction
- MIR/SSA optimization gap
- LLVM IR emission quality
- native linker or target configuration
- runtime or allocator overhead
- standard-library implementation overhead
- benchmark mismatch or unfair baseline

Only create optimizer tasks from benchmark results after the benchmark has
equivalent baselines and enough metadata to reproduce the gap.
