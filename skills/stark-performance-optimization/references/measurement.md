# Controlled performance measurement

## Before timing

Identify whether the question concerns whole-process time or a steady-state kernel. These branch results used **direct native process launches including startup**. A shell harness, in-process loop, cold file cache, buffered output or changed executable changes the measurement; do not merge their ratios.

Preserve the before executable before changing the compiler. Build real after/C/Rust executables and verify checksums, exact bytes and relevant failure paths outside timing. For diagnostic relinks, use an unchanged control with the same starting IR/object stage and linker flags. A post-optimization patch and an earlier compiler fact fix can produce different code even when the intended optimization is present.

Finish builds, tests, tracing, heavy profiling and compilation first. Record a quiet interval in the shared tracker when coordinating with another performance agent. Document unconfirmed exclusivity, thermal caps, scheduling and startup rather than treating 1,000 samples as a cure for all confounders.

## Reusable timing helper

`scripts/measure_interleaved.py` uses only Python's standard library. It runs **existing native executables**; it does not build, trace or verify output equivalence. Each command's executable must be an absolute file path. Avoid shell/compiler/wrapper commands because the helper hashes the launched executable, not an arbitrary transitive tool/source graph.

Create a JSON configuration outside the source tree or under ignored benchmark artifacts:

```json
{
  "baseline": "c",
  "variants": {
    "c": ["/absolute/artifacts/c"],
    "rust": ["/absolute/artifacts/rust"],
    "before": ["/absolute/artifacts/stark-before"],
    "stark": ["/absolute/artifacts/stark"]
  },
  "cwd": "/absolute/isolated-runtime-directory"
}
```

`cwd` is optional and applies to all commands. Arguments after the executable are supported. For a benchmark that modifies files, arrange equivalent initial state and cleanup; shared cwd alone does not prove isolation. Handle fixtures and expected output explicitly before selecting this helper.

From the skill directory:

```bash
python3 scripts/measure_interleaved.py /absolute/config.json \
  --rounds 1000 --warmups 1 --seed 20261031 \
  --output /absolute/artifacts/interleaved-1000.json
```

On Linux the default pins the driver and inherited children to the first allowed CPU. Use `--cpu N` to select an allowed CPU, or `--no-affinity` deliberately. Platforms without affinity support are recorded as unpinned; requesting an unavailable explicit CPU fails. Affinity does not reserve the CPU or prevent other agents from running.

Every measured round runs every variant once in a shuffled order. Warmups are separate. Stdout goes to `/dev/null`; stderr goes to a separate regular file per variant, truncated/rewound **outside** the timed interval. No sample is excluded. A failed child stops the run, and an executable hash change invalidates it. Use another driver when the workload requires different output, environment, steady-state or cache behavior.

The JSON records commands, cwd, sample count, seed, CPU/affinity state, executable SHA-256 before/after, raw microseconds in round order, arithmetic means, medians, variation and ratios to the baseline. The terminal prints only the summary. Do not modify the binaries while collecting or inspecting a run.

## Interpret the result

For identical sample populations:

```text
ratio_to_C       = mean(Stark) / mean(C)
ratio_to_Rust    = mean(Stark) / mean(Rust)
ratio_to_before  = mean(Stark) / mean(before)
reduction_percent = 100 * (1 - ratio_to_before)
```

C's row is **1.000** by definition. Report one mean and ratio per executable, including the final production row and clear diagnostic labels. Rust's ratio can reflect a different algorithm/library choice, not compiler quality. A faster mean on one host does not guarantee a faster program on all CPUs.

When pooling comparable batches, concatenate raw sample sets (or weight means by their sample counts), then divide pooled means. Do not average rounded ratios, choose the favorable batch, sum individual optimization gains or silently remove outliers. Keep before/after/C/Rust in the same window to distinguish a persistent code difference from day-to-day variation. Report uncertainty in prose while still giving the requested point estimate.

If an unexpectedly large effect remains, inspect native register allocation, branches, code alignment and dynamic dependencies before declaring success or noise. A same-native-object placement control can establish causality; a random padding search alone does not establish a production rule. Test the proposed general policy with other representative workloads, and measure the **ordinary source/package build** again.

Retain the exact final binaries, hash/flag/source provenance, raw JSON, build/test logs, initial and optimized IR, disassembly and any output/fault-injection evidence in ignored artifacts. Record the authoritative summary path in the tracker so similarly named older controls are not mistaken for the final result.
