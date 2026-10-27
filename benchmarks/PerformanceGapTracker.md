# Temporary benchmark gap tracker

This is a working list for investigation, not a performance claim. The initial entries are from the latest complete historical run: `benchmarks/results/results-20260526T012305Z.pew8vs.csv` (100 process-level samples; machine metadata has the same timestamp). Its recorded commit had 73 dirty entries and differs from the current checkout; it used a Ryzen 7 9800X3D, whereas the current machine has a Ryzen 7 5700G with passive cooling and a CPU temperature cap. Results need reproduction on the current compiler. Ratios use recorded average microseconds, not rounded CSV ratio columns; `1.000` can conceal a small difference. Runtime spread and benchmark equivalence must be checked before interpreting a gap.

C criterion: Stark average > C average. Rust criterion: Stark average >= Rust average. The Rust `rust-fixed-batch` variant is not the canonical Rust row.

## Latest affected-benchmark rerun (October 26, 2026)

After adding buffered reads for read-only, fully buffered files, allowing LLVM to recognize the disjoint byte-copy loop, and skipping unnecessary early wide-integer overflow comparisons, these three scenarios were rerun sequentially with one warmup and 100 measured processes per language. The machine and tools are the same as the preceding focused run below; the checkout includes uncommitted changes. Spread is `(max - min) / average`, not a confidence interval. Fixed language order, passive cooling and the temperature cap can affect comparisons, particularly the small gaps.

| Benchmark | Stark µs | C µs | Rust µs | Stark / C | Stark / Rust | Spread % (S / C / R) | Evidence |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| `benchmarks/io/FileBufferedReadWrite` | 9154 | 9369 | 21980 | 0.9771 | 0.4165 | 12.6 / 16.4 / 11.4 | [CSV](results/results-20261027T022415Z.zbSBZj.csv) · [machine](results/machine-20261027T022415Z.UXJZ7a.txt) |
| `benchmarks/runtime/RuntimeBufferDynamic` | 5418 | 5448 | 5869 | 0.9945 | 0.9232 | 6.8 / 8.9 / 10.4 | [CSV](results/results-20261027T022334Z.Tf9eX9.csv) · [machine](results/machine-20261027T022334Z.A8Xq02.txt) |
| `benchmarks/text/TextParsing` | 5782 | 5611 | 5871 | 1.0305 | 0.9848 | 14.0 / 15.8 / 8.5 | [CSV](results/results-20261027T022346Z.mmyJls.csv) · [machine](results/machine-20261027T022346Z.EswYpp.txt) |

For these three, the C criterion now includes only `TextParsing`; the Rust criterion includes none. The small measured differences are below the observed spreads. The four other previously rerun scenarios retain their prior measurements; they were not rerun for these changes. Compared with the preceding run, Stark's raw averages changed from 10723 to 9154 µs for file I/O, 5519 to 5418 µs for dynamic buffers, and 5850 to 5782 µs for text parsing. Those separate, fixed-order samples do not isolate the changes from thermal and other variation.

The file result still compares different workloads: Stark allocates a dynamic buffer for the final write, C writes directly, and Rust uses different buffering and read allocation. Read buffering is enabled for `FileMode.Read` with `FileBuffering.Full`; read/write handles continue to use direct reads. The copy helper now reaches LLVM as a loop that becomes `llvm.memcpy`, and the dynamic benchmark's fixed-size copy emits vector loads/stores. The Unicode 1024-bit parsers retain digit validation and signed/unsigned overflow checks while avoiding wide cutoff comparisons for prefixes that cannot overflow; the remaining wide multiply/add and different C input representation are still possible opportunities for investigation.

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
