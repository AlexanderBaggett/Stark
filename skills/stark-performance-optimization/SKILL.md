---
name: stark-performance-optimization
description: Investigate and implement Stark compiler or standard-library performance improvements using preserved semantic facts, LLVM IR and machine-code comparisons, controlled experiments, and measured C/Rust baselines. Use for benchmark gaps, missed optimizations, and regressions in generated production code.
---

# Stark Performance Optimization

Produce a general optimization with a visible proof and reproducible production measurements. Treat investigation, diagnostic controls, implementation, and validated production output as different evidence. An investigation request does not by itself request a production patch or commit.

## Establish the workload

Read the repository's `AGENTS.md`, `benchmarks/PerformanceGapTracker.md`, and the relevant benchmark implementations. For Stark source changes, also read `skills/stark-language/SKILL.md`: use the strongest valid function kind, target-correct types, and existing ownership/memory contracts. `finite` guarantees progress and return; it does not imply purity, and `unsafe finite` is valid. Include implicit generic drops when deciding whether the guarantee holds.

Record the actual commit/dirty state, selected SDK and source/package provenance, target, optimization/link flags, tool paths, and executable hashes. Preserve other agents' changes. Historical ratios identify candidates; reproduce the current executable before choosing a gap to fix. Check whether its apparent work already folds away.

Confirm that the launcher executes the compiler you rebuilt. In this checkout `./stark` launches the Release `compiler.dll`; it does not build changed C# sources. Record the compiler binary as well as the benchmark binary so an old compiler cannot invalidate a control.

After a memory incident, start with one compiler worker and monitor the whole process group: one CLI invocation can launch parallel dependency builds, and a managed heap limit does not bound Clang or child processes. Prefer the relevant source-free package chain over rebuilding the umbrella SDK for a focused check. Use a measured memory budget and stop at its limit; diagnose before retrying rather than repeatedly raising it. Exercise allocation failures with small runtime allocations instead of enormous fixed-size aggregate types. Save large IR, disassembly and logs to files and inspect bounded excerpts; do not stream whole artifacts into the conversation. Distinguish measured build memory from an unconfirmed Codex app failure.

Compare **work**, then code: algorithms, hashing, capacity/load factor, inputs and encoded output, allocations, initialization, syscalls, buffering, checks, cleanup and failures. C's custom identity-hashed table and Rust's randomized `HashMap` are different workloads. A change to an API's flush/failure behavior needs its own contract and matched comparison; do not silently replace the canonical benchmark.

## Inspect where the work survives

Use the [LLVM and compiler-source investigation guide](references/llvm-investigation.md). Save Stark SSA/facts and initial LLVM IR, relevant optimized/post-LTO IR, and disassembly of the **actual executable**. Compile C and Rust to LLVM IR and native code with recorded comparable flags. Follow the hot path and cold/cleanup paths separately; quantify dynamic operations, not just textual matches.

When a missed transformation is unexplained, inspect the checked-out LLVM/Clang/Rust implementation of the relevant pass, ABI lowering, library operation, or driver option. Derive a falsifiable condition, then test it with a minimal program or isolated pass. Reading source complements generated-code evidence; it does not prove which pass ran in the timed build.

## Preserve facts before adding machinery

Trace each needed fact from producer to consumer across semantic analysis, MIR, SSA, rewrites/reanalysis, specialization, packages, LLVM emission, and final code generation. Find the earliest incorrect producer or unnecessary loss.

For each proposed change, record the fact, its producer, the first stage that loses it, the consumer that needs it, and the mutation/escape that invalidates it. Explain which executed operations this proof would remove. This makes a fact-preservation patch reviewable before relying on timing.

When an isolated analyzer disagrees with production, compare its inputs first: instantiated generic bodies, materialized effects, target information and package facts. A harness missing these inputs cannot establish that the production optimizer rejected a valid proof.

- Retain exact literals, slices and lengths; source storage types; ranges and relational bounds; target layout/alignment; deep constness; allocation identity; initialized prefixes; field/payload effects; capture and ownership/drop facts.
- Remap facts when cloning, widening private storage, materializing addresses or retargeting calls. Preserve receiver, indirect-argument addresses/locals, source return types, and effect summaries. Reanalysis must use the same authoritative source/package call effects as the original analysis.
- At joins retain facts true on every incoming edge. Invalidate only affected facts/regions after mutation, escape or reallocation. Unknown effects remain conservative.
- Distinguish an owner's header from reachable payload memory, and a borrow pointer slot from its referent. A pointer load is not a payload read; dereferencing that pointer can reach outside the header argument's LLVM `argmem` region. Representation alignment does not establish pointee alignment.
- Reuse shared copyability, lifecycle, layout and call-effect analyses. Keep a producer's complete field/type contract; disabling a mismatched producer or dropping fields can conceal the root cause.
- Combined allocations need checked region extents, padding, alignment and total size. Disjoint regions within one allocation do not imply distinct allocation identities. Preserve physical size/array stride as well as field offsets when lowering explicit alignment.

Read [branch lessons](references/branch-lessons.md) for the concrete commits behind these distinctions and for relevant prior failed controls.

## Turn a control into a general implementation

Choose a small source/SDK control or IR experiment that isolates the hypothesis. Keep an unchanged relink baseline using the same pipeline stage, flags and inputs. Verify outputs and failure semantics before interpreting timings. Label assumptions introduced by an IR patch, including unsupported ones; do not ship those assumptions as proofs.

A successful control establishes an opportunity, not a promised percentage. Implement the semantic rule or guarded fast path for arbitrary eligible inputs, retaining a correct dynamic/unsupported fallback. Never key production behavior on benchmark strings, iteration counts, checksums or benchmark function names. Resolving a real stdlib operation by typed identity still requires checking its signature/body/effects.

Preserve allocation failure, observable state after each operation/early exit, target behavior, aliasing, ownership/drop order and byte-reading cases. Removing payload writes does not authorize removing fallible allocation. Avoid manufacturing stronger `align`, `noalias`, `readonly`, `argmem`, syscall effects or overflow promises to induce LLVM optimizations.

Inspect the rebuilt **production** executable. Check that the intended work disappeared and that register allocation, spills, code placement, cleanup, dynamic dependencies or startup did not erase the gain. Fewer instructions can be slower. If placement is implicated, vary layout while holding native instructions/work constant; then test a general backend policy across additional workloads. Pass backend settings to the stage that actually generates machine code, including target-specific ThinLTO linkers.

## Validate the proof boundary

Run focused checks in the consuming configuration, normally Release for this repository. Cover successful arbitrary inputs, dynamic fallback, boundary/negative proof cases, aliasing/escape/mutation, early exits, allocation failures and exact-once drops as applicable. Exercise source and **source-free package** consumers; separately optimized caller/callee objects expose contracts that whole-module optimization can mask. For I/O, verify exact bytes and accepted short-write, error and flush behavior.

Validate both transformation presence and observable correctness. Add a first-emission invariant/lint when it would catch the repaired compiler contract. LLVM verification checks well-formedness; it does not prove alignment, aliasing, initialized-memory or ownership promises. Compile relevant other targets when layout/ABI/encoding/link forwarding changes; distinguish cross-compilation from native execution.

Audit repository-wide direct references and indirect assumptions: analysis consumers, assertions/codegen shapes, snapshots, fixtures, packages, Stark ports, benchmarks, docs and generated metadata. Update the complete affected contract chain and run the affected checks. Do not use a full release run to discover a deterministic focused failure.

## Measure and report

Finish builds/tests before timing. Preserve before/after/C/Rust executables; verify expected outputs independently. If another performance agent is active, coordinate the timing window in the tracker; lack of acknowledgment is not exclusive host access. Trace syscalls or collect profiles separately from headline timings.

Use [the measurement guide](references/measurement.md) and `scripts/measure_interleaved.py` for randomized, balanced direct-process samples. Respect the requested sample count; 1,000 rounds per executable has been useful for these short programs. Keep the same CPU, output destinations and process-launch method; disclose startup and shared-host/thermal effects. Retain raw samples and all executable hashes. Do not silently drop outliers or select the fastest layout/window.

Report one arithmetic mean and ratio per variant with **C = 1.000**, plus Stark/Rust and Stark/before. Pool equally sized comparable batches by their samples rather than averaging rounded ratios. Give the requested point estimate; explain uncertainty without substituting a range for it. Small differences require restraint; repeat only when a specific uncertainty warrants it. Do not add isolated optimization percentages.

Update the owned tracker section with proof, generated-code/work differences, validation, final production ratios, artifact locations, negative results and remaining opportunities. Preserve other agents' sections. Mark implementation and benchmark status according to the user's requested scope; an optional future optimization need not keep accepted work open. Commit only when requested, and stage only the authorized changes.
