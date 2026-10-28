# Temporary benchmark gap tracker

This is a working list for investigation, not a performance claim. The initial entries are from the latest complete historical run: `benchmarks/results/results-20260526T012305Z.pew8vs.csv` (100 process-level samples; machine metadata has the same timestamp). Its recorded commit had 73 dirty entries and differs from the current checkout; it used a Ryzen 7 9800X3D, whereas the current machine has a Ryzen 7 5700G with passive cooling and a CPU temperature cap. Results need reproduction on the current compiler. Ratios use recorded average microseconds, not rounded CSV ratio columns; `1.000` can conceal a small difference. Runtime spread and benchmark equivalence must be checked before interpreting a gap.

C criterion: Stark average > C average. Rust criterion: Stark average >= Rust average. The Rust `rust-fixed-batch` variant is not the canonical Rust row.

## QueueDequeue investigation (October 27, 2026)

**Status: open.** The `Queue.Reserve` fast path and split growth copy are implemented; the user explicitly wants the benchmark investigation to remain open. The dequeue optimization below is not implemented and needs further investigation.

### Split growth copy and generic ownership checks (October 28, 2026)

`SparseSlots.ReserveRing` now divides the occupied ring into the contiguous tail from `Head` and the optional wrapped prefix. It retains the original per-element `T` move expression and original allocation, failure and cleanup paths; only the source-index calculation and loop partition change. The existing queue invariants give `Head <= Capacity`, including zero head for empty zero-capacity storage. Both `Queue<T>` and `RingQueue<T>` use this helper.

The focused Release stack/queue suite passed five tests. Its queue executable now checks unwrapped growth of destructor-bearing elements and verifies their destruction; it also checks wrapped growth of destructor-bearing elements, the first retained value, count, and destruction of the queue's remaining live elements. The existing numeric wrapped-growth case checks the full retained order, zero/sufficient-capacity reserve and an oversized request. The corresponding Stark-side port passed in Release using the configured host-compiler bridge; it checks compilation of the same embedded source, not native execution. The production change uses typed element operations, not a byte copy that would bypass element behavior. These checks cover representative owned types and shapes, not every possible element type or injected allocator failure. A repository-wide search covered helper references, queue tests and ported tests, benchmarks, compiler passes and documentation.

The optimized QueueDequeue executable contains eight-element vectorized copy loops for the contiguous ranges. One warmup plus 100 measured processes per language produced Stark **5585 µs (0.9874)**, C **5656 µs (1.0000)** and Rust **5793 µs (1.0242)**, with C = 1.0000. Their spreads were 15.4%, 15.3% and 11.3%, respectively, so these small gaps are not decisive. [CSV](results/results-20261028T134659Z.JMs25X.csv) · [machine metadata](results/machine-20261028T134659Z.fT7X1c.txt). This is a fixed-order, whole-process harness run on a passively cooled and temperature-capped machine. The compiler included independently developed uncommitted fact-analysis changes, so comparisons with older runs do not isolate the split copy. Another agent was working on the host and an exclusive timing window was not established.

For a closer attribution, the same compiler built a pre-split control from a fresh copy of the current stdlib with only the ring-copy block reverted; an audit found `System/Collections.stark` was the only differing stdlib source. After one warmup and 100 randomized, CPU-0-pinned processes per executable, mean / median times were pre-split Stark **843.7 / 831.9 µs (1.065)**, implemented Stark **833.9 / 818.2 µs (1.053)**, C **792.2 / 780.1 µs (1.000)** and Rust **912.5 / 900.9 µs (1.152)**. The implemented mean was 1.2% lower than pre-split in this sample; process startup, scheduling, thermal state and possible concurrent load limit interpretation. Local binaries and samples are in ignored `benchmarks/codegen/queue-dequeue-investigation/`, including `split-implemented-interleaved.json`.

**Separate existing issue:** a minimal program enqueues one destructor-bearing `Resource`, dequeues it into a local `out` value, verifies the value, and leaves scope. The observed drop counter is zero. The same program, built with the same compiler against both the pre-split and post-split stdlib, gives the same result. This is not caused by the split copy and is not fixed here; it limits claims about end-to-end ownership correctness of dequeued nontrivial values. The queue-owned-element checks above do not assert a wrong expected drop count for the output locals. The local reproduction is `benchmarks/codegen/queue-dequeue-investigation/resource-dequeue-drop-repro.stark` (ignored). Investigate that output-lifetime behavior separately; keep QueueDequeue open.

### Queue fact trace and LLVM controls (October 28, 2026)

The following trace predates the production split-copy change and uses the then-current source, `--emit-ssa`, saved LLVM modules, the final ThinLTO executable, and the checked-out LLVM implementation. The saved artifacts and controls are in ignored `benchmarks/codegen/queue-dequeue-investigation/`; they are local evidence and may be absent in another checkout.

**What survives and where the trace becomes less precise:**

- The optimized caller SSA initializes `queue`, then calls `Queue.Enqueue` in the fill loop and `Queue.IsEmpty`/`Queue.TryDequeue` in the drain loop. In this SSA module the generic queue methods have bodies but `SupportsDirectCodeGeneration` is false; the concrete `u32` specializations appear later in emitted LLVM. Thus the caller's SSA loop does not expose the field updates performed by those calls for its own fact analysis. This observation does not mean LLVM never inlines them: the final ThinLTO executable does inline the relevant paths.
- Stark's fact model has individual integer ranges and dynamic-storage region facts, but `Queue` stores its raw backing pointer, capacity, head and length in the named `SparseSlots<T>`/`Queue<T>` aggregates. The dynamic-storage optimizer's field-address recognition requires `StarkTypeKind.Dynamic`, so those dynamic owner/prefix facts do not describe this raw-pointer-backed queue. This is a concrete representation boundary, not proof that adding dynamic facts alone would solve the loop.
- Emitted LLVM does preserve useful restrictions: the queue receiver is `nonnull noalias` with a 72-byte dereferenceable header; the dequeue output parameter is separately `noalias` and initialized; head, length and capacity loads carry the individual `[0, 2^63)` range. The reserve fast path emits an explicit `Length <= Capacity` check and a capacity-minus-length comparison. These facts concern individual values and the header; the IR does not expose a persistent relation between head, live length and capacity across the queue calls.
- After ThinLTO, the final benchmark binary **already starts the drain with head zero and count 32,768**, contrary to the earlier hypothesis that these individual initial facts were necessarily lost. It nevertheless retains the head-versus-capacity comparison and conditional reset during the drain. The loop must distinguish an ordinary step, a wrap and the final empty-queue reset, and no production transformation has proved that those states can be simplified together.
- In the concrete `SparseSlots.ReserveRing` LLVM body, `%v16 + %v30_phi` forms `oldHead + offset`, `%v36` compares it with old capacity, and `%v54_phi` selects either that index or index minus capacity. The element load and store use pointers loaded from the old storage and allocation result, without an explicit scoped relationship between those two accesses. `System.Memory.Allocate` returns a structure through an `sret` pointer; `noalias` on that return slot describes the slot, not by itself the buffer stored inside it. The runtime allocation call does already carry `noalias` on its pointer return. Do not infer from this trace alone exactly which allocator-provenance fact LLVM retains after inlining or that aliasing is the sole barrier.

**Isolated tests of the missing information:**

- In saved root LLVM IR, three benchmark-specific entry controls added `llvm.assume` for head zero, then head zero plus `Length <= Capacity`, then those facts plus `Length == 32768` immediately before the drain. All passed the benchmark. ThinLTO retained the same two-at-a-time scalar drain with wrap comparisons in all three. This tests the facts at that location; it does not establish whether another representation or optimization pipeline could use them.
- Two further benchmark-only IR controls added assumptions inside each `TryDequeue`: first `Head + Length <= Capacity`, and then the more directly useful condition `Length == 1 || Head + 1 < Capacity`. Both passed this benchmark but retained the scalar drain and wrap checks. These assumptions are false for some valid wrapped queues, so they are experiments, not valid general contracts. They show that simply inserting these assumptions at these points did not make this LLVM pipeline prove away the recurrence.
- A separate control added a scoped no-alias relationship only to the old-buffer load and new-buffer store in the saved growth-copy loop. The benchmark passed, but the final growth loop remained scalar with its wrapped-index calculations. This tests an asserted relationship for those accesses only; it does not justify a blanket allocator attribute or establish the full language-level proof. By contrast, the two-contiguous-loop source control described below produced vector copy loops. The result points to the wrapped access shape as an actionable issue even when extra alias information is supplied.
- An additional diagnostic run used LLVM 23 `opt -passes='default<O3>'` on the linked saved modules (not the final ThinLTO pipeline). Its vectorizer reported that the drain had a value it could not identify as a reduction, and that vectorizing the growth-copy loop was not profitable. Those remarks explain that particular diagnostic pipeline's decision; they do not prove the exact reason every final ThinLTO pass retained the loops. The C IR instead exposes contiguous source and destination addresses in its growth loop and uses a runtime pointer-distance check before its vector loop.

All benchmark-specific controls below returned success after one warmup and 100 randomized, CPU-0-pinned process runs per variant. They include process startup. The checkout was dirty, and another agent was working on the same host; no exclusive measurement window was established, so concurrent load is unknown. With C = 1.000, the first sample produced current Stark **1.061** (844.9 µs), unchanged relink **1.065** (847.6 µs), entry head/capacity/count assumptions **1.068** (849.9 µs), scoped copy no-alias **1.070** (851.7 µs), the earlier combined reserve/split-copy source control **1.044** (831.2 µs), C **1.000** (796.1 µs), and Rust **1.158** (922.1 µs). These small differences are not a reliable ranking; the observed code shape is the stronger evidence. Raw samples: `facts-interleaved.json`.

In a second 100-run sample, current Stark was **1.061** (844.4 µs), unchanged relink **1.059** (842.9 µs), per-iteration contiguous relation **1.056** (840.1 µs), explicit no-wrap-or-last relation **1.054** (839.0 µs), deliberately incorrect general no-wrap control **1.013** (806.2 µs), combined reserve/split-copy control **1.041** (828.3 µs), C **1.000** (795.7 µs), and Rust **1.159** (922.4 µs). The invalid no-wrap control uses the implemented reserve fast path and vectorizes the drain; it is a code-shape experiment for this benchmark, not a shippable optimization or proof that the difference is statistically stable. Raw samples: `drain-facts-interleaved.json`.

**Next concrete directions:** the split copy has since been implemented and tested as described above. For draining, investigate a proven contiguous segment or loop-versioned path that preserves wrap, empty-state reset, calls and error behavior; merely attaching the tested assumptions is insufficient. Trace and test where a relational queue-state proof can be maintained across the specialized calls, without treating raw pointer access or opaque calls as automatically safe. Retain the general path for states not proven contiguous. Investigate the separate dequeued-output drop issue. QueueDequeue stays open.

### Reserve fast-path implementation and rerun

`Queue.Reserve` now returns `Ok` immediately when the current length is within capacity and the requested additional elements fit in the remaining capacity. The subtraction is guarded against underflow; all other cases continue through the existing capacity, allocation and error path. In the optimized QueueDequeue executable, the enqueue loop now branches around `ComputeContiguousGrowthCapacity` when capacity remains; it still calls the helper when growth is required.

The focused Release `SystemCollectionsStackQueueStandardLibraryTests` suite passed all five tests. The queue runtime test covers zero and no-growth reserves, wrapped growth and order, an oversized reserve while elements remain live, and generic resource destruction. The corresponding ported Stark test passed in Release with the host compiler configured; it checks compilation through the Stark host bridge, not native execution of its embedded program. The same queue program also passed when compiled and executed directly from the standard-library source. Allocation failure was not injected; its original slow path remains in use. A repository-wide search checked queue reserve, growth helpers, tests, ported tests, benchmarks and documentation for assumptions affected by the fast path.

The harness ran one warmup and 100 measured processes per language. With C = 1.0000, Stark averaged 6213 µs (**1.0376**), C 5988 µs (**1.0000**) and Rust 6255 µs (**1.0446**); the observed spreads were 37.3%, 19.8% and 23.2%. Stark meets the slower-than-C criterion but not the not-faster-than-Rust criterion in this sample. The spreads exceed both differences, and fixed language order and passive cooling limit the comparison. [CSV](results/results-20261028T030525Z.0B4Vsc.csv) · [machine](results/machine-20261028T030525Z.nvzb8T.txt).

In a separate CPU-0-pinned, randomized interleaved run, with one warmup and 100 processes per variant, all passed. Mean / median times were old Stark 939.5 / 910.8 µs, implemented Stark 888.3 / 845.4 µs, C 826.1 / 804.2 µs and Rust 965.3 / 940.0 µs; with C = 1.000, the mean ratios were **1.137 / 1.075 / 1.000 / 1.169**. The implemented mean was 5.5% below old Stark in this sample; startup and scheduler variation are included. Samples and binaries are in the ignored `benchmarks/codegen/queue-dequeue-investigation/implemented-interleaved.json` and companion files.

### Investigation before implementation

`PathJoin` was next in the full-suite table but uses the same two literal joins and length-only observation as `PathRepeatedSmallOps`, which is being investigated separately. `QueueDequeue` is a different workload and met both gap criteria in that full run. That run averaged Stark 5763 µs, C 5592 µs and Rust 5760 µs: with C = 1.0000, **1.0306 / 1.0000 / 1.0300**, respectively. Its spreads were 10.4% / 18.3% / 16.4%; the three-microsecond Stark–Rust gap is not distinguishable from noise. [Full-suite CSV](results/limb-suite-20261027/results-20261027T225516Z.WymI4P.csv).

The new harness run used one warmup and 100 measured executions per language. Stark averaged 6375 µs, C 6266 µs and Rust 6102 µs; with C = 1.0000 the ratios are **1.0174 / 1.0000 / 0.9738**. Spreads were 96.4% / 28.8% / 33.2%, so this run does not establish a small gap. [CSV](results/results-20261028T024614Z.nTO4fk.csv) · [machine metadata](results/machine-20261028T024614Z.DIslJC.txt). Results are whole-process times, in fixed language order, on the passively cooled and temperature-capped machine.

All three programs enqueue 32,768 sequential values and then dequeue and checksum them. Stark uses `Queue<u32>`, C uses a custom `int32_t` ring and Rust uses `VecDeque<i32>`; all benchmark values are nonnegative and fit both element types. Stark reports allocation errors through `MemoryStatus`, C returns a failure code, and Rust's ordinary `push_back` allocation failure behavior differs. Stark's queue starts its growth at four slots; C starts at eight, and Rust uses its library's growth policy. They do not have identical allocation or failure paths. `SparseSlots<T>` stores the queue's elements directly; it does not use a per-element bitmap.

The optimized executable before the reserve change exposed two concrete opportunities:

1. **Avoid the general growth calculation on every enqueue when capacity is already sufficient.** Stark's final `main` calls `System_Collections_ComputeContiguousGrowthCapacity` on each of the 32,768 enqueues; the call writes a stack result and the caller checks it. The C and Rust enqueue loops instead test their current length against capacity and enter growth only when full. In an isolated copy of the standard library, a `Queue.Reserve` fast path checks `Length <= Capacity` and `additional <= Capacity - Length` and returns `Ok` when both hold. Its final executable bypasses the helper on that path. The guards avoid unsigned underflow, and the general path remains for growth and errors; this is a local control, not a production change or a complete correctness validation. Adding only `inline` to the shared capacity helper did **not** remove the call in this build.
2. **Expose contiguous segments during ring growth.** Stark's `SparseSlots.ReserveRing` copies individual elements and computes the wrapped index for each element; its generated growth loop retains per-element compare/conditional-move address calculations and scalar copies. In this workload the head is zero while growing. Clang's C executable recognizes the contiguous copy and emits an eight-element vector loop with scalar fallback. Explore proving a nonwrapped segment and copying one or two contiguous ranges, while preserving move/ownership and destructor behavior for nontrivial `T`, wrapped queues, alignment, allocation failure and overlap. This is a code-generation opportunity, not a measured attribution of the whole gap.

After one warmup, a CPU-0-pinned driver ran 100 randomized, interleaved processes for each executable. All returned success; subprocess startup is included. These are separate from the shell harness:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark baseline | 899.8 | 880.0 | 1.159 |
| Stark reserve fast-path control | 821.3 | 813.3 | 1.058 |
| C | 776.1 | 764.4 | 1.000 |
| Rust | 891.5 | 873.5 | 1.149 |

The control's mean is 8.7% below the baseline in this sample; process startup, scheduling and thermal behavior limit the precision of that estimate. Raw samples, binaries, C/Rust LLVM IR, disassemblies and the isolated modified stdlib are local ignored files under `benchmarks/codegen/queue-dequeue-investigation/`. The subsequent reserve and growth-copy implementations and their validation are recorded above; the dequeue opportunity remains open. The current `PathRepeatedSmallOps` work remains untouched.

### Follow-up controls: growth copies and dequeue

An isolated `SparseSlots.ReserveRing` control split the old ring into up to two contiguous element-copy loops. Its optimized QueueDequeue executable contains eight-element vector copy loops, unlike the original scalar wrapped-copy loop. The normal benchmark checksum passed, as did a separate small control that dequeued six of eight elements, wrapped the ring, grew it, and checked the remaining sequence. These checks do not establish correct behavior for every capacity, allocator failure, or nontrivial `T` and destructor; this is not a production implementation.

With one warmup and 100 randomized, interleaved processes per variant, CPU pinned, with C = 1.000:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark baseline | 884.5 | 873.8 | 1.149 |
| Reserve fast path alone | 818.9 | 807.8 | 1.064 |
| Split growth copy alone | 857.6 | 845.8 | 1.114 |
| Both controls | 813.8 | 800.7 | 1.057 |
| C | 769.7 | 760.1 | 1.000 |
| Rust | 887.7 | 876.4 | 1.153 |

In this sample, the split copy alone reduced the baseline mean by 3.0%, but adding it to the reserve fast path reduced the mean by only 0.6%; process startup and scheduling variation limit conclusions about these small differences. The two controls are not evidence of independent additive gains. Raw samples and isolated source are in ignored `benchmarks/codegen/queue-dequeue-investigation/split-growth-interleaved.json` and `split-stdlib/` and `combined-stdlib/`.

A separate **deliberately invalid general-purpose control** removed the head-wrap check from `TryDequeue`. For this benchmark's fill-then-drain sequence, the final empty-queue reset occurs when the head reaches capacity; the checksum passes. Its drain becomes an eight-element vectorized sum. In another 100-run interleaved sample, mean times were Stark baseline 900.5 µs, no-wrap control 882.6 µs, C 784.2 µs and Rust 888.4 µs, or **1.148 / 1.125 / 1.000 / 1.133** with C = 1.000. The 2.0% Stark difference is small relative to process-level variation. This control is incorrect when a live queue wraps and must not be shipped; it shows a possible code shape only. A safe approach would need a compiler proof or a separately validated contiguous-drain operation. Samples and the intentionally modified source are in ignored `nowrap-interleaved.json` and `nowrap-stdlib/`.

## PathRepeatedSmallOps investigation (October 27, 2026)

### Agent coordination (October 27, 2026)

This tracker is shared with another agent working on benchmark performance. This chat's scope is **PathRepeatedSmallOps and compiler fact preservation**. The two-input const-join wrapper is now declared `inline`, eliminating external copy calls in the host benchmark; full dead-payload elimination remains pending. The **QueueDequeue/collections work** is being updated separately; preserve that agent's tracker entries and source/test changes. Re-read the relevant section before each edit and patch only the intended text.

**PathRepeatedSmallOps measurement window finished:** four focused Release checks and both 100-sample comparisons are complete; no builds, tests or timings from this chat are running. New implementation edits are confined to `stdlib/src/System/IO/Path.stark` and its documented signatures; the compiler retargeting change predates this step. QueueDequeue/collections edits belong to the other agent. The measurement-window note was not acknowledged by the other agent, so absence of concurrent machine load is not established. Record shared compiler/stdlib changes that affect another investigation before comparing its old and new measurements.

**User-directed priority: preserve established facts throughout compilation.** Carry ownership, allocation identity, literal contents/lengths, deep constness, disjointness, capacity after successful reserve, initialized ranges, capture and field/payload effects through lowering, call retargeting, module/package boundaries and LLVM emission wherever their proofs remain valid. Remap facts when values or representations change; do not discard them merely at a call or phase boundary and depend on later inlining to rediscover them. Invalidate only the facts and regions that an operation may change; at control-flow merges retain facts true on every incoming path. Reuse authoritative fact models and summaries rather than adding parallel benchmark-specific rules. Unknown effects still require conservative invalidation. For this benchmark, body availability is one symptom to investigate, while preservation and consumption of the existing semantic facts is the preferred compiler direction.

### Implemented: eliminate the 20,000 external copy calls

The two-input `TryJoinConst` wrapper in `stdlib/src/System/IO/Path.stark` is now declared `public inline fn`. Its implementation, overlap contract, allocation/status behavior and pointer-copy core are unchanged. This small wrapper extracts const input pointers and lengths, validates them and delegates to the core; exposing it to the caller lets LLVM specialize those facts. The existing compiler retargeting selects this wrapper for literal arguments to general `TryJoin`. The general overlap-capable API and the three/four-input overloads retain their existing policies. Public documentation and the skill's signature reference reflect the declaration.

The ordinary Release build, with default ThinLTO settings and no IR patches, passes its benchmark checksum and the local `memcpy` interposer reports **0 calls / 0 bytes**, versus **20,000 / 160,000** for the saved baseline. Linked `main` contains no join calls and only calls the initial `__stark_runtime_try_realloc` and final `__stark_runtime_free`. The null-result branch still returns 1, and cleanup remains real. No allocator attributes or import-budget settings were changed.

The optimized ThinLTO IR from a saved-bitcode relink contains two constant-length `llvm.memcpy` intrinsics (10 and 9 bytes) plus an integer store; the backend lowers those intrinsics to inline stores. The compiler-produced executable still repeats five stores per iteration. **External calls are eliminated; the copy intrinsics and dead payload writes are not fully eliminated.** Header-versus-payload liveness remains a separate compiler task. The same C and Rust sources were recompiled for LLVM inspection: C still retains only its vector checksum loop, and canonical Rust's linked benchmark body still reduces to the no-allocation shim.

All 10 emitted Stark LLVM modules pass the verifier. Four directly affected Release tests pass: `SourceStdLibPromotedPathCorrectnessExecutableRuns` (including joined bytes and overlapping self-views through the packaged stdlib), `StdLibSourcePromotedPathTryJoinUsesTailRegionPointerCopies`, `StdLibOverlapSensitiveApisDeclareExplicitMemoryContracts`, and `StagedWindowsStdLibPathHelpersUseWindowsSeparatorsAndNormalizationRules`. The repository-wide stale-assumption audit found no required assertion changes; source and ported tests' relevant shapes/contracts remain valid. Documentation signatures were updated, tracker links resolve, and `git diff --check` passes. No full release workflow was run.

The harness ran one warmup and **100 measured processes per language**, in fixed Stark/C/Rust order:

| Language | Mean µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: |
| Stark, inline wrapper | 5653 | 1.0219 | 20.9 |
| C | 5532 | 1.0000 | 19.7 |
| Rust | 5621 | 1.0161 | 19.3 |

[CSV](results/results-20261028T030455Z.zJAtA0.csv) · [machine](results/machine-20261028T030455Z.b89L70.txt). These small gaps are below the process-level spread. A separate CPU-0-pinned randomized interleaved comparison used one warmup and **100 processes per variant**, all successful:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark, before inline wrapper | 728.1 | 710.2 | 1.3442 |
| Stark, inline wrapper | 609.7 | 593.3 | 1.1255 |
| C | 541.7 | 522.7 | 1.0000 |
| Rust | 763.5 | 751.3 | 1.4097 |

The implemented Stark mean is **16.3% lower** than its saved pre-inline baseline in this sample, and still **12.6% above C**. These timings include process startup, differ from the shell harness's timing overhead, and are subject to passive cooling, scheduling and other-agent activity. The tracker measurement note did not receive an acknowledgment; no idle-machine guarantee is claimed. Reduced call overhead, constant-size copying, layout and retained stores are not independently timed here. The remaining C gap is not resolved by this change.

Local evidence is under ignored `benchmarks/codegen/path-repeated-investigation/`: `stark-implemented`, its disassembly, `implemented-temps/`, `implemented-main-optimized.ll`, `implemented-build.log`, `implemented.metrics`, fresh `c-implemented-comparison.ll` / `rust-implemented-comparison.ll`, and `implemented-interleaved.py` / `.json` (including raw samples and binary hashes).

### Earlier evidence: import threshold and actual copy work

This investigation rebuilt the current Release compiler through the harness, read `skills/stark-language/SKILL.md` and its performance cookbook, and inspected fresh LLVM and linked executables. The existing const-retargeting change remains partial; no new production optimization was implemented in this pass. Module-resolution diagnostics confirm source stdlib inputs, and the fresh root LLVM is byte-identical to the earlier `retarget-temps/root.ll`.

| Current executable | Remaining benchmark work |
| --- | --- |
| Stark | Initial checked reserve; 5000 iterations with two out-of-line `TryJoinConst` calls, length loads, status checks, copies and separator writes; cleanup. Literal facts and extension scans are folded. |
| C, harness flags | Vectorized checksum loop and final return check; no joins, payload writes, scans or allocation. |
| Rust, harness flags | Benchmark body tail-calls the no-allocation shim, whose body returns; path work, allocation and checksum loop are gone. Runtime startup still exists. |

A local `LD_PRELOAD` interposer counted **20,000 `memcpy` calls and 160,000 copied bytes** in Stark; C and Rust each counted zero. Every execution passed its checksum. The sizes are 5, 8, 10 and 9 bytes per iteration. The two additional separator stores per iteration make 170,000 payload bytes written on the normal Stark path (separator count inferred from source and disassembly). This instrumented control counts work, not execution time. Zero `memcpy` calls alone would not demonstrate elimination: the import control below replaces them with inline stores.

#### The immediate barrier is ThinLTO importing

Relinking the unchanged saved bitcode with LLD optimization remarks reports `NoDefinition` for both `TryJoinConst` calls in `main`. With `-Wl,-mllvm,-print-import-failures`, LLVM reports:

```text
System_IO_Path_TryJoinConst__mutborrowSystem_Text_OwnedAscii_ascii_ascii_
Reason = TooLarge, Threshold = 100, Size = 101, MaxHotness = unknown
```

Changing only the relink's import budget to `-Wl,-mllvm,-import-instr-limit=101` makes both calls inline: reported cost 185, threshold 325. No `alwaysinline` edits are necessary for this control. Both joins, their repeated capacity/failure checks and all external `memcpy` calls disappear from `main`; the actual initial fallible reserve and cleanup remain. The 5000-iteration loop still performs eight literal stores per iteration. A larger diagnostic budget of 1000 gives the same main code shape. These budgets are experiments on this build, not proposed global compiler settings or stable size requirements.

This separates body availability from inline profitability. The current join is cheap enough for LLVM's call-site inliner once imported; the default importer rejects it before the inliner sees the body. Inspect `llvm/lib/Transforms/IPO/FunctionImport.cpp` in the checked-out compiler sources and Stark's existing imported-body/inline-clone path when choosing a production change. Prefer preserving and consuming semantic facts across these boundaries so useful optimization does not depend on later body importing. Caller specialization or body exposure can complement that work; a one-instruction threshold adjustment does not address fact loss. Preserve all general-path overlap and failure semantics.

#### Why importing still does not remove the payload writes

There are two separate memory objects: the 24-byte stack owner header (`Data`, `Length`, `Capacity`) and its backing allocation. `Length`/`Capacity` access the header. The current optimized `OwnedAscii.Clear` reads and resets only the header length; its source `MoveLast` loop is already eliminated. Thus that source loop is not evidence that the final program observes the old payload. Reserve/reallocation can read or relocate backing bytes on other paths, and returned views, raw pointer escapes and other aliases can expose them.

`SsaScalarReplacementOptimizer.AddEscapedAggregateCallRoots` rejects owner scalar replacement whenever its address reaches a direct call, even when emitted LLVM already has `captures(none)` for the destination. `SsaDynamicStorageCallFactPolicy` preserves general direct-call facts only for nonwriting, noncapturing arguments (plus its existing initialization-helper cases). `ParameterMemoryEffectSummary.Reads/Writes/CaptureKind` does not distinguish a header-field read from a dereference of a pointer loaded from that field. These are concrete limits of the current analyses, not missing Stark source-language capabilities. A noncapturing call may still read payload bytes, so capture information alone is insufficient.

The earlier forced-inline LLVM control retains `memcpy` lengths **5, 10 and 9**, while the 8-byte copy becomes an integer store. The checked-out `InstCombineCalls.cpp::SimplifyAnyMemTransfer` only expands constant transfers of 1/2/4/8 bytes through this rule. This explains that particular asymmetry; it does not mean LLVM cannot lower the other copies later. The earlier equivalent constant-byte-store control lets O3 remove the repeated loop but leaves final payload writes before cleanup. Exposing small constant copies earlier is therefore a separate opportunity from proving all payload bytes dead, with no independently established timing benefit yet.

Cleanup reads allocator metadata at negative offsets and publishes the freed block to a bucket cache. LLVM's DSE return-path rule requires an uncaptured no-alias allocation result; the cache publication prevents that proof here. A Stark-level proof should describe payload liveness separately from allocator metadata and cleanup effects, and preserve the real free. It must also preserve capacity facts through `Clear` and successful joins, and invalidate them on aliasing writes or potential reallocations. Do not infer that a header read observes the payload, or that loading its pointer proves the payload cannot escape.

Rust has a different allocation contract: the checked-out `library/core/src/alloc/global.rs` explicitly permits unused global allocations to be removed and the optimizer to assume allocation is infallible; `library/alloc/src/alloc.rs` declares special allocator/deallocator entrypoints. That explains why its allocation-free result is not evidence that Stark can discard the observed `Reserve` error branch. C starts with a fixed stack buffer. Stark's achievable target for this source remains removal of joins, scans and dead writes while retaining the checked setup and cleanup.

#### Focused measurements and validation

The harness ran one warmup and 100 measured processes per language, in fixed Stark/C/Rust order, without a concurrent compiler build from this investigation during measurement:

| Language | Mean µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: |
| Stark | 5371 | 1.0258 | 8.4 |
| C | 5236 | 1.0000 | 9.0 |
| Rust | 5503 | 1.0510 | 13.2 |

[CSV](results/results-20261028T024819Z.hc9avU.csv) · [machine](results/machine-20261028T024819Z.lHBL8C.txt). Stark's mean exceeds C and is below Rust in this sample; all gaps are smaller than the spreads. An earlier run in this investigation overlapped another compiler build and had spreads of 127%/186%/69%; retain it as a contaminated diagnostic, not a performance comparison ([CSV](results/results-20261028T024552Z.54nSOh.csv), [machine](results/machine-20261028T024552Z.0zRsEN.txt)).

A separate CPU-0-pinned, randomized interleaved run used one warmup and 100 processes per variant; all returned success. Whole-process means include startup:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Current Stark | 774.1 | 727.4 | 1.3743 |
| Unchanged-bitcode relink | 704.1 | 664.6 | 1.2500 |
| Same relink, import budget 101 | 613.0 | 540.6 | 1.0883 |
| Earlier length/status-only IR control | 648.2 | 607.6 | 1.1509 |
| C | 563.3 | 531.9 | 1.0000 |
| Rust | 851.4 | 789.9 | 1.5115 |

The import control's mean is 12.9% below its matching default-budget relink. The unchanged relink itself differs from the compiler-produced executable (including object ordering/layout), and these small process timings remain sensitive to scheduling and passive cooling. The length-only control has different code and does not establish a further timing benefit from removing the remaining writes. Prefer the deterministic import remarks and final code shapes when deciding the next change.

Reproduction detail: build the Rust executable with the harness's link-only invocation. Requesting `--emit=llvm-ir,link` in one rustc command retained a vector checksum loop in this investigation, unlike the canonical link-only binary, which is byte-identical to the earlier saved executable. Inspect the exact measured executable; request IR separately.

Fresh LLVM modules all pass `opt -passes=verify` (10 modules). The focused Release pipeline test `ConstPathSpecializationHandlesImportedOverloadsWithoutDiscardingMutableInputs` passes (1 test); the .NET runner required local loopback access. Tracker links and `git diff --check` pass. Saved sources, LLVM, executables, copy-count interposer, default/import-101 remarks, import-failure logs, and the interleaved driver/raw samples are local ignored evidence under `benchmarks/codegen/path-repeated-investigation/` (`current-*`, `import101-*`, `count-memcpy.*`, `stark-import101`). The correctness cases listed in the handoff remain prerequisites for a production dead-payload transformation; successful benchmark checksums do not substitute for them.

## ArenaAllocator rerun after slab caching (October 27, 2026)

The arena allocator now subtracts its block link from the largest runtime cache bucket when choosing its minimum slab payload. Both infallible and fallible allocations use the same size selection. The runtime checks passed for repeated and nested frames, fallible growth, allocations around and beyond the slab boundary, alignment up to 4096 bytes, data preservation, and empty-frame overflow. Three directly affected compiler emission checks also passed.

The benchmark harness ran one warmup and 100 measured processes per language on this passively cooled, temperature-capped machine. With C = 1.000:

| Language | Mean µs | Ratio | Spread % |
| --- | ---: | ---: | ---: |
| Stark | 6548 | 0.989 | 14.3 |
| C | 6619 | 1.000 | 13.7 |
| Rust | 6867 | 1.037 | 10.3 |

[CSV](results/results-20261028T001912Z.oMwqXC.csv) · [machine](results/machine-20261028T001912Z.zYl6qD.txt). The prior full-suite sample had Stark 24673 µs, C 6578 µs and Rust 6840 µs. The new raw averages no longer meet either gap criterion, though the small Stark–C difference is far below the spreads. Fixed language order and CPU temperature behavior limit comparisons between separate runs. The full-suite table below is the older snapshot; only ArenaAllocator was rerun here.

A separate CPU-0-pinned, randomized interleaved comparison used 100 processes per variant, after a warmup. Mean / median times were old Stark 20028.3 / 19987.7 µs, implemented Stark 2058.1 / 1985.0 µs, C 2057.4 / 1991.0 µs, and Rust 2304.3 / 2220.5 µs. With C = 1.000, those means are 9.735, 1.000, 1.000, and 1.120 respectively. The new Stark and C means are effectively tied in this sample; process startup and machine conditions remain part of the measurement. Raw samples are retained locally under ignored `benchmarks/codegen/arena-investigation/implemented-interleaved.json`.

## ArenaAllocator investigation before implementation (October 27, 2026)

Validation of the subsequent slab-cache change found a pre-existing edge case: a sufficiently large allocation on an already populated frame can wrap the fast-path cursor arithmetic and return a non-null pointer instead of failing. This is reachable through Stark: `TryReserveCapacity(2 ** 63 - 1)` on an arena-backed `dynamic u16` with an existing allocation returned true in a local executable, although the requested byte size is `2 ** 64 - 2`. The empty-frame overflow path is covered by the new runtime test. This issue is separate from the slab-size change and remains unresolved.

`benchmarks/allocator/ArenaAllocator` is the clearest next candidate in the latest suite: its 100-run process averages were Stark 24673 µs, C 6578 µs, and Rust 6840 µs. With C = 1.000, Stark = **3.751** and Rust = **1.040**. It meets both tracking criteria, and the gap is much larger than the recorded Stark / C / Rust spreads of 14.2% / 9.6% / 15.4%. This is one fixed-order run on the current passively cooled, temperature-capped machine; reproduce it with interleaved samples before attributing a cause. [Results](results/limb-suite-20261027/results-20261027T225351Z.niUTuQ.csv) · [machine](results/limb-suite-20261027/machine-20261027T225351Z.Juq0NN.txt).

The three sources perform the same round count, item count and checksum self-test, but their allocation strategies differ. Stark uses an arena-backed dynamic array and arena-allocated pairs; C allocates one block per round and bumps offsets within it; Rust uses two preallocated `Vec`s. These differences matter when interpreting the ratios.

### Allocation paths and generated code

Before the change, Stark entered a fresh arena for each `RunRound`. Its first allocation reserved 2048 bytes for 256 values, and each of the 256 `Pair`s used a 16-byte arena allocation. The arena helper in [LlvmBuiltinAndHelperEmitter.cs](../src/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.cs) requested at least 4096 bytes of payload plus an 8-byte arena block link: **4104 bytes**. The runtime allocator's largest cached bucket is 4096 bytes, so those ordinary arena blocks bypassed the cache. On Linux, the generated `RunRound` contained an inlined `mmap` path for new blocks and a `munmap` path when the arena left. In this workload, two such blocks were needed per round.

C's optimized LLVM IR instead calls `malloc(8192)` once per round, keeps the arena offset and capacity check in its loop, stores the `Pair` fields and values, and calls `free` once. Rust's optimized IR calls its allocator for a 4096-byte pair vector and a 2048-byte value vector, and deallocates both; capacity/growth paths remain in the generated code. The checked-out Rust Unix allocator source routes normally aligned requests of these sizes to libc `malloc`/`free`. Those are userspace allocator calls; no equivalent OS syscall count was measured for C or Rust. The loops are not identical, so their residual timing differences cannot be assigned solely to compiler quality.

### Controlled measurements

The current Release Stark compiler, Clang 23.1.4 and Rust 1.99.1 built the original sources with the benchmark optimization flags. A local experimental Stark IR variant changes only the minimum arena payload from 4096 to **4088** bytes, so the payload plus 8-byte link requests exactly the existing 4096-byte runtime bucket. Its threshold changes with the payload; larger requests retain the original size-based path. It is an experiment, not a tested general allocator fix.

Instrumenting the generated Stark OS-allocation and OS-free helpers counted **2414 allocations and 2414 frees** in the original executable: two per round for 1200 benchmark rounds plus seven self-test rounds. The experimental cached-slab executable counted **one OS allocation and zero OS frees** across those rounds. These are helper counters inserted into experimental IR, not a syscall trace; all benchmark self-tests passed. The runtime's cached bucket retains its backing allocation for reuse.

A second temporary source control replaces only `arena Pair` with `stack Pair`. This changes the allocation workload: the 2048-byte values buffer still needs an arena block, but the loop no longer allocates pairs in the arena. The normal checksum is retained. The two controls were also combined. Following one warmup, a CPU-0-pinned driver ran 150 randomized, interleaved processes per variant in `/tmp`; every execution returned success. Means and medians include process startup. The passive cooling, temperature cap, scheduling and nonidentical language workloads still apply. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark, original | 19370.6 | 19318.9 | 9.795 |
| Stark, cached-slab IR control | 1959.8 | 1937.9 | 0.991 |
| Stark, stack-pair source control | 9798.0 | 9773.4 | 4.954 |
| Stark, stack-pair plus cached-slab controls | 1964.2 | 1947.5 | 0.993 |
| C | 1977.6 | 1959.3 | 1.000 |
| Rust | 2206.5 | 2197.6 | 1.116 |

The independent 200-sample interleaved run including an unchanged Stark relink gave original Stark 18989.2 µs, unchanged relink 18959.5 µs, cached-slab control 1997.3 µs, C 2000.7 µs, and Rust 2236.0 µs. The much larger direct-process ratios than the shell harness's 3.751 reflect differing timing methods and startup overhead; they are not directly interchangeable. Both runs strongly isolate repeated OS allocation/free as the dominant observed cost here. The stack-pair control roughly halves the original cost by avoiding the second arena block, while changing it after slab caching has no measurable benefit in this sample; it does not establish that scalar replacement would improve the production case.

### Concrete opportunities

1. **Make ordinary arena slabs eligible for the runtime's cached allocation path.** Account for the arena block link when selecting slab payload and bucket size; the 4088-byte IR control demonstrates the specific boundary and its performance effect. A production change must preserve usable capacity, alignment (including over-aligned requests), large-allocation and overflow behavior, nested arena lifetimes, fallible paths, and correct reuse/free behavior. It must also assess the memory retained by cached slabs, including per-thread peak usage. Validate those behaviors on supported targets before claiming a general improvement.
2. **Consider eliminating nonescaping arena objects only when their allocation and lifetime semantics permit it.** The pair is used locally in this benchmark, but changing it to stack storage changes the workload and allocation-failure behavior. The cached-slab comparison shows no measurable further win here; establish benefits on matched cases before prioritizing a compiler transformation.

The binaries, raw samples, generated LLVM and disassemblies, and counting controls are retained locally under ignored `benchmarks/codegen/arena-investigation/`. The investigation itself did not change production code; the slab-cache implementation and rerun are described above.

## Full benchmark suite (October 27, 2026)

The suite ran 74 runnable Stark scenarios with one warmup and 100 process-level samples per language. Two compile-only surfaces were skipped by the harness. Groups ran sequentially on the passively cooled, temperature-capped Ryzen 7 5700G. The network group initially timed out in the restricted sandbox; its loopback cases completed when retried with local socket access. Results are fixed-order averages, and observed spreads are often larger than the differences shown.

Using the latest full-suite averages, 39 scenarios meet the Stark-slower-than-C criterion and 4 meet the Stark-not-faster-than-Rust criterion. The table lists their union; C = 1.000. Rust variants are excluded. A listed difference is a raw average, not a claim of statistical significance.

| Benchmark | Stark / C | Rust / C | Stark slower than C | Stark not faster than Rust |
| --- | ---: | ---: | :---: | :---: |
| `benchmarks/allocator/ArenaAllocator` | 3.7508 | 1.0398 | Yes | Yes |
| `benchmarks/allocator/MemoryCopyFill` | 1.0023 | 1.0387 | Yes | — |
| `benchmarks/collections/DictionaryInsert` | 1.0023 | 1.0715 | Yes | — |
| `benchmarks/collections/DictionaryLookup` | 1.0238 | 1.1931 | Yes | — |
| `benchmarks/collections/DictionaryRemove` | 1.0038 | 1.0697 | Yes | — |
| `benchmarks/collections/LinkedListBuildClear` | 1.0027 | 1.0372 | Yes | — |
| `benchmarks/collections/LinkedListChurn` | 1.0157 | 1.0460 | Yes | — |
| `benchmarks/collections/LinkedListPopOnly` | 1.0130 | 1.0437 | Yes | — |
| `benchmarks/collections/LinkedListPush` | 1.0130 | 1.0388 | Yes | — |
| `benchmarks/collections/ListGrowth` | 1.0115 | 1.0331 | Yes | — |
| `benchmarks/collections/ListIteration` | 1.0109 | 1.0437 | Yes | — |
| `benchmarks/collections/QueueChurn` | 1.0105 | 1.0172 | Yes | — |
| `benchmarks/collections/QueueDequeue` | 1.0306 | 1.0300 | Yes | Yes |
| `benchmarks/collections/QueueGrowth` | 1.0065 | 1.0345 | Yes | — |
| `benchmarks/collections/StackGrowth` | 1.0149 | 1.0389 | Yes | — |
| `benchmarks/console/ConsoleWrites` | 1.0480 | 1.0703 | Yes | — |
| `benchmarks/io/FileSystemPathTranscode` | 1.0055 | 1.0377 | Yes | — |
| `benchmarks/micro/Arithmetic` | 1.0013 | 1.0361 | Yes | — |
| `benchmarks/micro/BranchSelectPredication` | 1.0067 | 1.0259 | Yes | — |
| `benchmarks/micro/Branching` | 1.0127 | 1.0263 | Yes | — |
| `benchmarks/micro/FactDrivenBranchPruning` | 1.0143 | 1.0439 | Yes | — |
| `benchmarks/micro/FunctionPointerDevirtualization` | 1.0200 | 1.0310 | Yes | — |
| `benchmarks/micro/GlobalScalarLoadForwarding` | 1.0017 | 1.0460 | Yes | — |
| `benchmarks/micro/IndependentRawPointerRegions` | 1.0085 | 1.0234 | Yes | — |
| `benchmarks/micro/IndependentSliceAdd` | 1.0129 | 1.0352 | Yes | — |
| `benchmarks/micro/MemoryAccess` | 1.0154 | 1.0549 | Yes | — |
| `benchmarks/micro/PureCallGlobalForwarding` | 1.0075 | 1.0360 | Yes | — |
| `benchmarks/micro/ReadonlyOtherLocalFieldStore` | 1.0098 | 1.0366 | Yes | — |
| `benchmarks/micro/StackFieldLoadForwarding` | 1.0038 | 1.0279 | Yes | — |
| `benchmarks/micro/TailCalls` | 1.0038 | 1.0428 | Yes | — |
| `benchmarks/network/TcpLoopbackThroughput` | 1.0144 | 1.0400 | Yes | — |
| `benchmarks/network/TcpScatterGatherLoopback` | 1.0092 | 1.0327 | Yes | — |
| `benchmarks/text/AsciiToUnicodeConversionRuntime` | 1.0213 | 1.1286 | Yes | — |
| `benchmarks/text/OwnedPathAllocation` | 1.0173 | 1.0413 | Yes | — |
| `benchmarks/text/OwnedTextAllocation` | 1.0005 | 1.0460 | Yes | — |
| `benchmarks/text/PathJoin` | 1.0476 | 1.0384 | Yes | Yes |
| `benchmarks/text/PathNormalize` | 1.0263 | 1.0460 | Yes | — |
| `benchmarks/text/PathRepeatedSmallOps` | 1.0553 | 1.0372 | Yes | Yes |
| `benchmarks/text/TextConcatCopy` | 1.0180 | 1.0540 | Yes | — |

Full-run CSVs:

- [allocator](results/limb-suite-20261027/results-20261027T225351Z.niUTuQ.csv)
- [collections](results/limb-suite-20261027/results-20261027T225516Z.WymI4P.csv)
- [console](results/limb-suite-20261027/results-20261027T225736Z.Y1pe8O.csv)
- [io](results/limb-suite-20261027/results-20261027T225904Z.GsV5NA.csv)
- [micro](results/limb-suite-20261027/results-20261027T230344Z.3xuuk8.csv)
- [runtime](results/limb-suite-20261027/results-20261027T231601Z.KUsciK.csv)
- [text](results/limb-suite-20261027/results-20261027T231624Z.Ml6EDh.csv)
- [network](results/limb-suite-20261027/results-20261027T232330Z.mwD5Y3.csv)

The full-suite TextParsing averages were Stark 5704 µs, C 5728 µs and Rust 5963 µs: Stark / C 0.9958 and Rust / C 1.0410. The separate focused rerun below provides an additional sample.

## Focused TextParsing rerun after limb-and-carry implementation (October 27, 2026)

The Unicode signed and unsigned 1024-bit parsers now use an inlined, explicit 16-limb multiply-and-carry helper. The existing digit and overflow checks remain in place. On this machine, the resulting signed and unsigned parser arithmetic each contains 15 `mul` and 15 `adc` instructions, matching the shape measured in the IR experiment below. Release runtime tests passed for signed and unsigned bounds, error ordering, leading zeros, and deterministic random wide values.

The harness ran one warmup and 100 process-level samples per language in fixed order; this passively cooled, temperature-capped computer and the observed spread limit the interpretation of small differences. The benchmark implementations have the representation and workload differences described below.

| Benchmark | Stark µs | C µs | Rust µs | Stark / C | Rust / C | Spread % (S / C / R) | Evidence |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| `benchmarks/text/TextParsing` | 5576 | 5594 | 5917 | 0.9968 | 1.0577 | 10.5 / 14.1 / 16.7 | [CSV](results/results-20261027T225315Z.z1rwIJ.csv) · [machine](results/machine-20261027T225315Z.0QdgDW.txt) |

The prior same-machine focused sample was Stark 5782 µs and C 5611 µs (Stark / C 1.0305). This latest sample no longer meets either the slower-than-C or not-faster-than-Rust criterion; its differences are smaller than the spreads. The full-suite results are listed above.

To isolate process payloads, the old Stark executable, the implemented Stark executable and C were additionally run in a randomized CPU-0-pinned sequence, 300 times each in `/tmp`. All passed. Mean / median times were 1086.2 / 1071.7 µs before, 930.8 / 919.0 µs after, and 936.6 / 924.6 µs for C. With C = 1.000, the means are 1.160 before and 0.994 after; startup remains included. The implemented mean was 14.3% below the old Stark executable in this sample. Raw local samples are in the ignored `benchmarks/codegen/text-parsing-20261026/ir-controls/implemented-interleaved.json`.

## Previous affected-benchmark rerun (October 26, 2026)

After adding buffered reads for read-only, fully buffered files, allowing LLVM to recognize the disjoint byte-copy loop, and skipping unnecessary early wide-integer overflow comparisons, these three scenarios were rerun sequentially with one warmup and 100 measured processes per language. The machine and tools are the same as the preceding focused run below; the checkout includes uncommitted changes. Spread is `(max - min) / average`, not a confidence interval. Fixed language order, passive cooling and the temperature cap can affect comparisons, particularly the small gaps.

| Benchmark | Stark µs | C µs | Rust µs | Stark / C | Stark / Rust | Spread % (S / C / R) | Evidence |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| `benchmarks/io/FileBufferedReadWrite` | 9154 | 9369 | 21980 | 0.9771 | 0.4165 | 12.6 / 16.4 / 11.4 | [CSV](results/results-20261027T022415Z.zbSBZj.csv) · [machine](results/machine-20261027T022415Z.UXJZ7a.txt) |
| `benchmarks/runtime/RuntimeBufferDynamic` | 5418 | 5448 | 5869 | 0.9945 | 0.9232 | 6.8 / 8.9 / 10.4 | [CSV](results/results-20261027T022334Z.Tf9eX9.csv) · [machine](results/machine-20261027T022334Z.A8Xq02.txt) |
| `benchmarks/text/TextParsing` | 5782 | 5611 | 5871 | 1.0305 | 0.9848 | 14.0 / 15.8 / 8.5 | [CSV](results/results-20261027T022346Z.mmyJls.csv) · [machine](results/machine-20261027T022346Z.EswYpp.txt) |

For these three in that run, the C criterion included only `TextParsing`; the Rust criterion included none. The small measured differences are below the observed spreads. The four other previously rerun scenarios retain their prior measurements; they were not rerun for these changes. Compared with the preceding run, Stark's raw averages changed from 10723 to 9154 µs for file I/O, 5519 to 5418 µs for dynamic buffers, and 5850 to 5782 µs for text parsing. Those separate, fixed-order samples do not isolate the changes from thermal and other variation.

The file result still compares different workloads: Stark allocates a dynamic buffer for the final write, C writes directly, and Rust uses different buffering and read allocation. Read buffering is enabled for `FileMode.Read` with `FileBuffering.Full`; read/write handles continue to use direct reads. The copy helper now reaches LLVM as a loop that becomes `llvm.memcpy`, and the dynamic benchmark's fixed-size copy emits vector loads/stores. The Unicode 1024-bit parsers retain digit validation and signed/unsigned overflow checks while avoiding wide cutoff comparisons for prefixes that cannot overflow; the remaining wide multiply/add and different C input representation are still possible opportunities for investigation.

## TextParsing: isolated LLVM IR difference before implementation

**The measured gap was reproducible by changing only the wide arithmetic representation in Stark's actual LLVM IR.** Before the implementation, Stark's [Text.stark](../stdlib/src/System/Text.stark) Unicode parsers emitted `mul nuw i1024 %value, 10` followed by `add nuw i1024` for each digit. Clang's optimized IR for the C benchmark exposes sixteen 64-bit limbs: each limb is widened to `i128`, multiplied by 10, added to the incoming carry, truncated to 64 bits, and shifted right by 64 to obtain the next carry. Both versions use the same installed Clang/LLVM 23.1.4 backend with `-O3`; Stark's executable path also uses ThinLTO. The source languages did not hand LLVM the same computation graph.

For a controlled test, the actual Stark benchmark was compiled with `--save-temps`. An experimental script changed only the multiply/add in `System_Text_ParseI1024Unicode` and `System_Text_ParseU1024Unicode` in the saved `System_Text.ll`: it substituted an always-inlined, explicit 16-limb `i128` multiply/add-with-carry sequence, leaving all input handling, overflow checks, result construction, the benchmark, and other modules unchanged. The modified module was recompiled and linked with the original saved objects using the original `-O3` ThinLTO strategy. An unchanged relink was included as a control. Every benchmark run returned success, checking the signed minimum and unsigned maximum. The experiment is not a production implementation and does not establish behavior for invalid or overflowing inputs.

In the resulting unsigned parser, the arithmetic sequence changed from **15 `mul` and 47 `adc` instructions** to **15 `mul` and 15 `adc` instructions**; those are static x86-64 counts, not hardware counters. The checked-out LLVM source's `SelectionDAG/LegalizeIntegerTypes.cpp` separately expands wide `MUL` and `ADD` in `ExpandIntRes_MUL` and `ExpandIntRes_ADDSUB`, respectively, and `SelectionDAG/TargetLowering.cpp` contains the multiply expansion machinery. These are the relevant source paths for tracing the lowering; source inspection alone does not prove the exact internal path selected by the installed binary.

One warmup and 300 randomized, interleaved executions per executable were run on CPU 0 in `/tmp`; times include process startup. All returned success. As elsewhere, this passively cooled, temperature-capped machine and scheduler can vary, and the C workload retains its known differences.

| Executable | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Original Stark | 1087.6 | 1075.8 | 1.1583 |
| Stark, unchanged relink control | 1095.1 | 1077.3 | 1.1662 |
| Stark, explicit-limb IR experiment | 935.4 | 921.8 | 0.9962 |
| C | 939.0 | 925.7 | 1.0000 |

The modified Stark mean was 14.0% below the original, and approximately matched C in this run. This isolates the particular emitted arithmetic shape as a substantial cause for this workload; it does not establish that every Stark wide operation should be lowered this way. The compiled outputs and the transformation script are retained locally under ignored `benchmarks/codegen/text-parsing-20261026/ir-controls/`.

A second, smaller control used the same 309 32-bit digits and result for two functions compiled by the same backend: one took the Stark-shaped `i1024` recurrence and one used Clang-shaped limbs. Each was called 1000 times per process. Across 100 randomized, CPU-pinned samples, the wide version with the same `nuw` flags averaged 5150.4 µs and the limb version 3570.8 µs. Removing `nuw` changed the wide mean to 5140.0 µs, so those flags did not explain this difference. The functions also matched on 5000 deterministic, valid random decimal inputs of 1–308 digits. This control omits parser validation and overflow reporting; it demonstrates arithmetic lowering, not full parser equivalence.

**Implemented for these parsers:** an internal standard-library helper exposes the measured limb/carry form to LLVM. General compiler recognition for other wide multiply/add operations remains a separate opportunity; the experiment does not establish its effect on other programs or targets.

## Earlier TextParsing investigation after the prefix optimization

The ordinary harness above measured Stark / C = 1.0305, but both spreads exceed that difference. To look past its shell timing overhead, the current Release compiler and Clang built the same sources with their normal optimization settings. A Python driver pinned itself and its children to CPU 0, warmed each binary, then executed 100 randomized, interleaved samples per variant in a temporary directory. All executions returned success; times include process startup. Passive cooling, scheduling, frequency variation and outliers still apply. Sources, binaries, disassembly and raw samples are in the ignored local `benchmarks/codegen/text-parsing-20261026/` directory.

| Variant | Mean µs | Median µs |
| --- | ---: | ---: |
| Current Stark, full benchmark | 1124.5 | 1073.4 |
| Previous Stark, before skipping safe-prefix cutoff checks | 1256.2 | 1222.2 |
| C, full benchmark | 978.9 | 923.0 |
| C, wide inputs represented as 32-bit code points | 959.3 | 921.6 |
| Stark, signed wide parse only | 855.6 | 819.7 |
| C, signed wide parse only | 759.4 | 730.5 |
| Stark, unsigned wide parse only | 876.2 | 837.2 |
| C, unsigned wide parse only | 757.9 | 732.9 |

The isolated variants remove the other wide parse and adjust the checksum; both retain the small parses and their success checks. They are process timings, not direct per-parser measurements. The interleaved full-result ratio is 1.149 in this sample; the old and new Stark binaries share the same driver conditions, but the samples still do not independently prove a causal improvement. The 32-bit C control changes only the two wide inputs and their reader types; its near-identical median makes input width an unlikely explanation for the gap in this particular workload. It does not make all semantics equivalent: C uses volatile pointers and byte-oriented libc routines for the small inputs, while Stark folds its small literal parses and performs Unicode wide parses with distinct error results.

Before the limb implementation, Stark `main` called each wide parser 50 times even though its inputs were immutable literals. Each parser validated each code point, then performed a 1024-bit multiply by 10 and added the digit. The emitted LLVM had separate `mul nuw i1024` and `add nuw i1024`; the unsigned x86-64 loop contained 15 `mul` and 47 `adc` instructions in its arithmetic region. C's explicit 16-limb `unsigned __int128` multiply/add had 16 `mul` and 16 `adc` instructions in the corresponding region. Counts are from static disassembly, not measured executed instruction counts. The prefix optimization removed most wide cutoff comparisons for these inputs, but did not remove the per-digit wide arithmetic or result construction and comparison.

As an algorithm control, a temporary C version accumulates up to 19 validated decimal digits in a `uint64_t`, then performs one 1024-bit limb multiply by the corresponding power of ten and adds the chunk. It checks carry overflow after each chunk and produces the same successful benchmark results; it is not a complete implementation of Stark's error contract. In a separate 200-sample randomized, interleaved run with the same setup:

| C control | Mean µs | Median µs |
| --- | ---: | ---: |
| Original byte-input parser | 954.3 | 921.3 |
| Original 32-bit-input parser | 947.0 | 923.5 |
| 19-digit chunks, byte input | 598.9 | 582.9 |
| 19-digit chunks, 32-bit input | 605.9 | 581.2 |

Earlier candidate opportunities, before the targeted IR experiment above:

1. **Batch decimal digits in the Stark wide parsers.** Nineteen decimal digits fit in `u64`; the first 304 digits are within both signed and unsigned 1024-bit magnitudes, so sixteen 19-digit chunks can replace 304 wide multiply/add operations on the guaranteed-safe prefix. Keep digit validation and use the existing checked per-digit path for the remaining digits. Preserve empty input, optional minus, signed minimum, leading zeros, overflow and invalid-format ordering, including invalid characters after an already-overflowing prefix. The C control demonstrates potential on this workload, not the performance or correctness of an unimplemented Stark version; measure the Stark change on matched runtime inputs and boundary cases.
2. **Improve lowering of wide multiply/add by a small operand.** The targeted IR experiment above has now isolated and measured this opportunity in the actual benchmark.
3. **Hoist or fold invariant wide literal parses when permitted.** The current code repeats the same pure-looking computations 50 times. Investigate whether effect, termination, immutable-input, error-result and result-identity semantics permit commoning or compile-time evaluation; `finite` alone does not prove absence of side effects. This specifically benefits this literal benchmark and should be assessed separately from runtime-input parsing, since C deliberately uses volatile input pointers.

## Previous focused rerun (October 26, 2026)

These seven scenarios were run sequentially after the process-ID, buffer, and loop-counter changes. Each result is one warmup plus 100 measured executions per language on this machine (Ryzen 7 5700G, passively cooled and temperature-capped), with .NET SDK 10.0.302, Clang 23.1.4, and Rust 1.99.1. CSV and machine metadata are linked for each scenario. The checkout was dirty; the recorded Git commit alone does not identify the tested changes.

Times are process-level averages in microseconds. Ratios below 1 mean Stark is faster. Spread is `(max - min) / average`, reported for Stark / C / Rust; it is not a confidence interval. Language runs occur in a fixed order, so thermal changes and spread can obscure small gaps. Compare these only with the earlier samples on this same machine, not the 9800X3D historical run.

| Benchmark | Stark µs | C µs | Rust µs | Stark / C | Stark / Rust | Spread % (S / C / R) | Evidence |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| `benchmarks/io/FileBufferedReadWrite` | 10723 | 9464 | 21971 | 1.1330 | 0.4881 | 16.5 / 22.5 / 10.3 | [CSV](results/results-20261027T015028Z.lcQCVO.csv) · [machine](results/machine-20261027T015028Z.2vST9d.txt) |
| `benchmarks/micro/AggregatePhiFieldForwarding` | 6643 | 7066 | 6832 | 0.9401 | 0.9723 | 12.1 / 11.6 / 14.3 | [CSV](results/results-20261027T014927Z.1j5Kyn.csv) · [machine](results/machine-20261027T014927Z.m6Kfsf.txt) |
| `benchmarks/micro/BranchSelectPredication` | 8385 | 8310 | 8526 | 1.0090 | 0.9835 | 11.0 / 7.1 / 6.7 | [CSV](results/results-20261027T015158Z.vmieE3.csv) · [machine](results/machine-20261027T015158Z.6BP0Ew.txt) |
| `benchmarks/micro/NullBranchPruning` | 6140 | 6268 | 6334 | 0.9796 | 0.9694 | 14.0 / 14.3 / 15.3 | [CSV](results/results-20261027T014951Z.331EPQ.csv) · [machine](results/machine-20261027T014951Z.5FoXwU.txt) |
| `benchmarks/runtime/RuntimeBufferDynamic` | 5519 | 5498 | 5879 | 1.0038 | 0.9388 | 11.9 / 10.7 / 17.2 | [CSV](results/results-20261027T015015Z.lG9ZUL.csv) · [machine](results/machine-20261027T015015Z.hLk640.txt) |
| `benchmarks/text/AsciiToUnicodeConversion` | 5194 | 5184 | 5445 | 1.0019 | 0.9539 | 17.1 / 9.9 / 11.4 | [CSV](results/results-20261027T015222Z.tQLOX1.csv) · [machine](results/machine-20261027T015222Z.xDCFRA.txt) |
| `benchmarks/text/TextParsing` | 5850 | 5569 | 5917 | 1.0505 | 0.9887 | 9.4 / 11.4 / 16.1 | [CSV](results/results-20261027T015235Z.VApWEj.csv) · [machine](results/machine-20261027T015235Z.Jl6pbM.txt) |

**C criterion in this previous run:** `AsciiToUnicodeConversion`, `BranchSelectPredication`, `FileBufferedReadWrite`, `RuntimeBufferDynamic`, `TextParsing`. These are raw average comparisons, including differences below the observed spread.

**Rust criterion in this previous run:** none of the seven; Stark had a lower recorded average in each. This does not establish a repeatable advantage where the gap is small.

`FileBufferedReadWrite` still has different work across implementations: Stark allocates a dynamic buffer for the final write; C writes directly; Rust uses different buffering and read allocation. Do not attribute the timing gap solely to compiler code generation.

## Historical: Stark slower than C

| Benchmark | Stark / C | Status |
| --- | ---: | --- |
| `benchmarks/collections/DictionaryInsert` | 1.0241 | Historical; needs current reproduction |
| `benchmarks/collections/DictionaryLookup` | 1.0352 | Historical; needs current reproduction |
| `benchmarks/collections/DictionaryRemove` | 1.0297 | Historical; needs current reproduction |
| `benchmarks/collections/DictionaryUpdate` | 1.0419 | Historical; needs current reproduction |
| `benchmarks/collections/LinkedListBuildClear` | 1.0439 | Historical; needs current reproduction |
| `benchmarks/collections/LinkedListChurn` | 1.0274 | Historical; needs current reproduction |
| `benchmarks/collections/LinkedListPopOnly` | 1.0045 | Historical; needs current reproduction |
| `benchmarks/collections/LinkedListPush` | 1.0143 | Historical; needs current reproduction |
| `benchmarks/collections/ListGrowth` | 1.0335 | Historical; needs current reproduction |
| `benchmarks/collections/ListIteration` | 1.0227 | Historical; needs current reproduction |
| `benchmarks/collections/QueueChurn` | 1.0387 | Historical; needs current reproduction |
| `benchmarks/collections/QueueDequeue` | 1.0363 | Historical; needs current reproduction |
| `benchmarks/collections/QueueGrowth` | 1.0085 | Historical; needs current reproduction |
| `benchmarks/collections/StackGrowth` | 1.0369 | Historical; needs current reproduction |
| `benchmarks/console/ConsoleWrites` | 1.0586 | Historical; needs current reproduction |
| `benchmarks/io/FileBufferedReadWrite` | 1.1286 | Historical; needs current reproduction |
| `benchmarks/io/FileSystemPathTranscode` | 1.0817 | Historical; needs current reproduction |
| `benchmarks/micro/AbstractionGenericWrapper` | 1.0150 | Historical; needs current reproduction |
| `benchmarks/micro/AbstractionLawWrapper` | 1.0029 | Historical; needs current reproduction |
| `benchmarks/micro/AggregatePhiFieldForwarding` | 1.0069 | Historical; needs current reproduction |
| `benchmarks/micro/Arithmetic` | 1.0064 | Historical; needs current reproduction |
| `benchmarks/micro/BranchSelectPredication` | 1.0736 | Historical; needs current reproduction |
| `benchmarks/micro/Calls` | 1.0197 | Historical; needs current reproduction |
| `benchmarks/micro/DeadStackFieldStore` | 1.0035 | Historical; needs current reproduction |
| `benchmarks/micro/FactDrivenBranchPruning` | 1.0056 | Historical; needs current reproduction |
| `benchmarks/micro/IndependentSliceAdd` | 1.0073 | Historical; needs current reproduction |
| `benchmarks/micro/NullBranchPruning` | 1.0016 | Historical; needs current reproduction |
| `benchmarks/micro/PureCallGlobalForwarding` | 1.0166 | Historical; needs current reproduction |
| `benchmarks/micro/ReadonlyOtherLocalFieldStore` | 1.0119 | Historical; needs current reproduction |
| `benchmarks/micro/StackFieldBranchForwarding` | 1.0096 | Historical; needs current reproduction |
| `benchmarks/micro/StackFieldLoadForwarding` | 1.0009 | Historical; needs current reproduction |
| `benchmarks/micro/TextLiteralLengthPruning` | 1.0182 | Historical; needs current reproduction |
| `benchmarks/runtime/RuntimeBufferDynamic` | 1.0767 | Historical; needs current reproduction |
| `benchmarks/runtime/RuntimeBufferFixed` | 1.0013 | Historical; needs current reproduction |
| `benchmarks/text/AsciiToUnicodeConversion` | 1.0960 | Historical; needs current reproduction |
| `benchmarks/text/AsciiToUnicodeConversionRuntime` | 1.0789 | Historical; needs current reproduction |
| `benchmarks/text/AsciiToUnicodeWideningKernel` | 1.0510 | Historical; needs current reproduction |
| `benchmarks/text/OwnedPathAllocation` | 1.0306 | Historical; needs current reproduction |
| `benchmarks/text/PathFacts` | 1.0269 | Historical; needs current reproduction |
| `benchmarks/text/PathJoin` | 1.0182 | Historical; needs current reproduction |
| `benchmarks/text/PathNormalize` | 1.0204 | Historical; needs current reproduction |
| `benchmarks/text/PathQueries` | 1.0118 | Historical; needs current reproduction |
| `benchmarks/text/PathRepeatedSmallOps` | 1.0260 | Historical; needs current reproduction |
| `benchmarks/text/TextConcatCopy` | 1.0076 | Historical; needs current reproduction |
| `benchmarks/text/TextParsing` | 1.0469 | Historical; needs current reproduction |

## Historical: Stark not faster than Rust

| Benchmark | Stark / Rust | Status |
| --- | ---: | --- |
| `benchmarks/collections/QueueDequeue` | 1.0007 | Historical; needs current reproduction |
| `benchmarks/io/FileSystemPathTranscode` | 1.0064 | Historical; needs current reproduction |
| `benchmarks/micro/AggregatePhiFieldForwarding` | 1.0274 | Historical; needs current reproduction |
| `benchmarks/micro/ExplicitArithmeticRangePruning` | 1.0053 | Historical; needs current reproduction |
| `benchmarks/micro/FunctionPointerDevirtualization` | 1.0004 | Historical; needs current reproduction |
| `benchmarks/micro/NullBranchPruning` | 1.0106 | Historical; needs current reproduction |
| `benchmarks/text/AsciiToUnicodeConversion` | 1.0535 | Historical; needs current reproduction |
| `benchmarks/text/AsciiToUnicodeConversionRuntime` | 1.0085 | Historical; needs current reproduction |
| `benchmarks/text/TextParsing` | 1.0281 | Historical; needs current reproduction |

## Investigation notes

- `FileBufferedReadWrite`: the Stark implementation creates a dynamic buffer for the last write; the C implementation writes directly from its source array. This is a workload difference to account for when interpreting the gap.
- Current checkout requires .NET SDK 10.0.302. Historical machine used a different compiler checkout and recorded .NET 10.0.108, Clang 22.1.5, and Rust 1.94.0.

## Earlier same-machine focused samples (before changes)

These are individual 100-run samples on the current machine and current checkout, using .NET 10.0.302, Clang 23.1.4 and Rust 1.99.1. They are not directly comparable to the historical machine. The harness measures whole processes in a fixed language order; thermal/frequency behavior and runtime spread can obscure small differences. Repeat and inspect code before treating a gap as established.

| Benchmark | Stark / C | Stark / Rust | Stark spread | Result file |
| --- | ---: | ---: | ---: | --- |
| `benchmarks/io/FileBufferedReadWrite` | 1.1618 | 0.5179 | 11.5% | [`results-20261026T235644Z.wG1nr5.csv`](results/results-20261026T235644Z.wG1nr5.csv) |
| `benchmarks/micro/AggregatePhiFieldForwarding` | 1.0018 | 1.0319 | 7.9% | [`results-20261026T235808Z.u57NVV.csv`](results/results-20261026T235808Z.u57NVV.csv) |
| `benchmarks/micro/BranchSelectPredication` | 0.9945 | 0.9826 | 8.8% | [`results-20261026T235540Z.PZ7r1L.csv`](results/results-20261026T235540Z.PZ7r1L.csv) |
| `benchmarks/micro/NullBranchPruning` | 1.0179 | 1.0146 | 11.0% | [`results-20261026T235857Z.AaZrHj.csv`](results/results-20261026T235857Z.AaZrHj.csv) |
| `benchmarks/runtime/RuntimeBufferDynamic` | 1.0516 | 0.9911 | 27.1% | [`results-20261026T235631Z.nbjSWO.csv`](results/results-20261026T235631Z.nbjSWO.csv) |
| `benchmarks/text/AsciiToUnicodeConversion` | 0.9894 | 0.9521 | 8.2% | [`results-20261026T235945Z.OipqZv.csv`](results/results-20261026T235945Z.OipqZv.csv) |
| `benchmarks/text/TextParsing` | 1.0444 | 0.9981 | 16.3% | [`results-20261026T235959Z.yqhXJR.csv`](results/results-20261026T235959Z.yqhXJR.csv) |

## Code-generation investigation

### AggregatePhiFieldForwarding and NullBranchPruning

The current samples show Rust ahead by 3.2% and 1.5%, respectively, but the process-level spread is larger than each gap. Disassemblies are preserved locally under `benchmarks/codegen/current/` (ignored by Git). At measurement time, both benchmarks initialized a signed `i64` from Stark's `System.Process.CurrentId()`, which then returned `i32[min max]`. Rust's `std::process::id()` returns `u32`, so its conversion to `i64` conveys a nonnegative value. In both hot loops the Stark and C binaries use signed remainder lowering, while Rust uses unsigned lowering. The `NullBranchPruning` score functions already compile to the same `and`/`lea`/`ret` sequence in all three languages; the missing null check is not the source of the difference. The Stark and C hot loops are otherwise very similar.

To test the range hypothesis, a local experimental copy of `AggregatePhiFieldForwarding.stark` casts the process ID through `u32` before converting it to `i64`. This changes the possible input semantics and is not a replacement for the benchmark. Its emitted hot-loop remainder operations use the same unsigned/narrowed pattern as Rust. In an interleaved, randomized 100-pair run using Python `subprocess.run` and `perf_counter_ns`, the original Stark executable averaged 2,397 us (median 2,383 us), while the experimental executable averaged 1,923 us (median 1,915 us); all executions returned success. This is a local experiment, not a robust cross-machine claim, and includes process startup.

Concrete opportunities to investigate:

- Preserve a justified nonnegative process-ID range through the platform boundary and `CurrentId()` into callers, then propagate it through loop-carried arithmetic so LLVM can select unsigned remainder. The earlier API permitted negative `i32` values, so changing its contract requires checking every platform and caller; do not assume the cast is semantics-preserving. The cast experiment also shows that the current compiler/backend can exploit the narrower input when explicitly expressed.
- In the measured `AggregatePhiFieldForwarding` executable, Stark's unrolled loop masked the induction variable with `0xffffff`; C and Rust used a simple countdown. The compiler now widens private bounded counters for the proven loop shape, and the newly compiled executable uses a countdown without the mask. Its runtime impact has not yet been measured.

### FileBufferedReadWrite

The current Stark/C ratio is about 1.16. Stark constructs a dynamic buffer for its final write, while C writes the source array directly; Rust also uses unbuffered file operations and allocates during reads. These differences preclude attributing the timings to generated code without matching the measured work or isolating the operations.

### RuntimeBufferDynamic

The current Stark/C average ratio is 1.052, with 27% Stark runtime spread, so the measured difference needs repeated controlled timing. The disassembly nevertheless exposes a concrete hot-path distinction: Stark unrolls the 32 writes in `main` but retains 32 calls to `DynamicByteBuffer.WriteSlice`; Clang inlines the C write/reserve logic into its loop. Stark's `WriteSlice` permits overlap and includes a snapshot-allocation path in addition to its disjoint path. Investigate call-site specialization or inlining when the caller's stack source is provably disjoint from buffer storage, preserving the overlap behavior for other callers. This could remove call and overlap-path overhead and enable fixed-size copy optimization; its speed impact remains unmeasured and allocator/implementation differences also matter.

## Implementation in progress

- `CurrentId` returns `u32[0 max]` and is declared `finite`; on Linux x64 the aggregate benchmark produces the unsigned remainder pattern. This is a public source API change: negative internal platform results, including the unsupported x86/ARM syscall stubs, are represented as unsigned values.
- `DynamicByteBuffer.WriteSlice` is marked `inline`; the current dynamic-buffer benchmark executable no longer has the 32 out-of-line calls. Overlap behavior still requires verification.
- Private bounded loop counters are widened when the initial value, step, bound, control flow, and uses prove that the original 24-bit value cannot wrap or be observed. The updated aggregate executable no longer contains the loop-counter mask; benchmark measurement remains pending.

## Investigation of the latest C-leading scenarios

The latest harness reports five Stark averages above C, but its shell-based process timing contributes substantial overhead. To inspect the executable payloads, the current Release Stark compiler and Clang 23.1.4 built the five source pairs with the harness optimization flags. A separate local experiment pinned the driver and its children to CPU 0, warmed each binary once, and ran 100 randomized, interleaved Stark/C pairs per scenario using Python `subprocess.run` and `perf_counter_ns`. Timings include process startup. Executables ran in a fresh temporary directory on `/tmp`; in particular, file results are not directly comparable to the earlier repository-directory harness run. The machine's passive cooling, temperature cap, scheduling, differing process IDs, and limited samples remain sources of variation. Binaries, disassembly, and raw samples are in the ignored local `benchmarks/codegen/investigation-20261026/` directory, so the evidence table below is retained here even when those files are absent.

| Scenario | Stark mean / median µs | C mean / median µs | Stark / C mean | Interpretation |
| --- | ---: | ---: | ---: | --- |
| `BranchSelectPredication` | 3769.9 / 3714.2 | 3706.7 / 3654.6 | 1.017 | Small repeatable-sample difference; causation not isolated. |
| `AsciiToUnicodeConversion` | 537.6 / 525.8 | 548.1 / 530.0 | 0.981 | Previous 0.2% C advantage disappeared; process startup dominates. |
| `TextParsing` | 1240.6 / 1224.3 | 937.1 / 925.7 | 1.324 | More substantial difference, but workloads differ. |
| `RuntimeBufferDynamic` | 895.6 / 876.0 | 768.6 / 756.7 | 1.165 | More substantial payload difference; allocation and copy paths differ. |
| `FileBufferedReadWrite` | 3108.4 / 3092.2 | 1933.8 / 1922.0 | 1.607 | Stark performs direct small reads; C uses buffered stdio. |

### `BranchSelectPredication`

The main loops use the same signed-remainder sequence, two induction variables, and a non-inlined select function. Both select functions use a conditional move. The comparison differs: Stark emits `lea`, zeroing, `test`, and `sete` to materialize its boolean; C emits `lea`, `not`, and `and` for the same parity predicate. This is a concrete code-shape difference, not proof that it causes the approximately 1.7% interleaved mean gap. Investigate whether the SSA/LLVM lowering of `(x & 1) == 0` and the boolean argument can preserve the equivalent low-bit expression through the call without the extra materialization; validate the impact across inputs and targets before changing it.

### `AsciiToUnicodeConversion`

Stark's optimized `main` is simply `return 0`: the constant input and final checksum allow the entire measured work to fold. C retains a small vectorized loop. The 0.2% C lead in the original harness is not evidence of a Stark optimization gap, and the interleaved ordering reverses. To measure actual conversion cost, use equivalent runtime inputs and observable output on both sides; avoid creating an optimization task from this constant-folded case.

### `TextParsing`

The following observations describe the code before the latest changes above.

Stark's optimized main folds its literal boolean and 64-bit parses, but calls `ParseI1024Unicode` and `ParseU1024Unicode` on each of 50 iterations. Those parsers process 32-bit Unicode code points and, per decimal digit, compare a 1024-bit accumulator against an overflow cutoff and multiply/add a 1024-bit value. The generated unsigned parser contains many limb multiplications. C instead loads its inputs through volatile pointers, parses byte strings, uses `strtoll`/`strtoull` for 64-bit values, and its 1024-bit routine uses a 16-limb multiply/add with a carry overflow check. These are different representations and optimization barriers; the 32% process-level mean gap cannot be assigned to one instruction without isolation.

Concrete candidates are (1) specialize or hoist parsing of an immutable literal when effect and error-result semantics prove it safe, and (2) investigate lowering wide decimal multiply/add and overflow checks as an efficient limb/carry operation, preserving signed limits, Unicode validation, and all error results. Compare matched runtime inputs and representations, then isolate the 1024-bit parse before attributing the gap to either candidate.

### `RuntimeBufferDynamic`

The following observations describe the code before the latest changes above.

The current Stark executable no longer calls `DynamicByteBuffer.WriteSlice` on its fast path, but its fixed 16-byte copy uses 16 byte loads and 16 byte stores. C loads the constant source as a vector and stores it with one 16-byte `movups`. The Stark `InitializeBytesDisjoint` source is an independent byte-copy loop; the retained disjoint path provides a concrete opportunity to lower or combine a proven 16-byte initialized copy into a bulk copy/vectorizable operation. Preserve initialization, bounds, and the separate overlapping snapshot path. LLVM's checked-out `LoopIdiomRecognize.cpp` contains the loop-to-`memcpy` transformation; determine why the current IR does not expose this case to it rather than assuming a specific backend fix.

The allocation strategies also differ: Stark uses its runtime allocation/reallocation path, while C uses libc `realloc`; in Stark's generated realloc fallback, a new allocation and copy can be required. The interleaved timing does not isolate copy versus allocation, so measure those separately before attributing the full 16.5% gap to scalar copies.

### `FileBufferedReadWrite` controls

These controls describe the unbuffered Stark read path before the latest changes above.

The normal Stark source writes a 32-byte tail through a newly allocated `DynamicByteBuffer`; C writes the original array directly. More significantly, Stark's `File.Read` delegates through `ReadByteRegionFast` to Linux `ReadFileBytes` and its direct read syscall for each 32-byte request. C's `fread` uses libc's buffered stream. There are 33 read requests per iteration, 64 iterations. C uses `fwrite`/`fread` stdio; Stark uses its own write buffer and direct platform reads. No syscall trace was available, so the syscall count is inferred from the normal-success source path rather than measured.

Two temporary controls were compiled without changing the benchmark sources: `stark-direct` writes the original source array for the last write instead of allocating the tail buffer; `c-unbuffered` calls `setvbuf(reader, NULL, _IONBF, 0)` after opening the read stream. In a second CPU-0-pinned, randomized, interleaved run of 100 executions per variant in `/tmp`, all variants returned success:

| Variant | Mean µs | Median µs |
| --- | ---: | ---: |
| Stark as written | 3156.3 | 3135.6 |
| Stark direct tail | 3141.2 | 3127.2 |
| C buffered read | 1950.6 | 1936.3 |
| C unbuffered read | 3272.7 | 3252.8 |

Disabling C's read buffering increased its mean by about 68%; removing Stark's tail allocation changed its mean by about 0.5%, within the run-to-run range. This strongly supports buffering as the main observed workload distinction, although the controls do not make all source operations identical. The concrete standard-library opportunity is a buffered-read path for repeated small reads, with correct seek, close, EOF, error, and read/write-transition semantics. First compare matched buffered and unbuffered scenarios; do not treat this as a proven compiler code-generation gap.
