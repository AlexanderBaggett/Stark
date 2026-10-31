# Temporary benchmark gap tracker

This is a working list for investigation, not a performance claim. The initial entries are from the latest complete historical run: `benchmarks/results/results-20260526T012305Z.pew8vs.csv` (100 process-level samples; machine metadata has the same timestamp). Its recorded commit had 73 dirty entries and differs from the current checkout; it used a Ryzen 7 9800X3D, whereas the current machine has a Ryzen 7 5700G with passive cooling and a CPU temperature cap. Results need reproduction on the current compiler. Ratios use recorded average microseconds, not rounded CSV ratio columns; `1.000` can conceal a small difference. Runtime spread and benchmark equivalence must be checked before interpreting a gap.

C criterion: Stark average > C average. Rust criterion: Stark average >= Rust average. The Rust `rust-fixed-batch` variant is not the canonical Rust row.

## Handoff: current optimization work

**Completed (October 31, 2026): DictionaryLookup and production x86 branch placement.** The fact-preserving lookup/cleanup changes and default native/ThinLTO branch placement are implemented, validated and accepted. The final 1,000-round comparison averages Stark **0.984 × C**, versus matched previous placement **1.017 × C** (3.2% lower mean); Stark/Rust is **0.376**. QueueDequeue and buffered console controls showed no regression. The user requested closure and a commit of the source changes. All build/test/timing windows have ended. The capacity/power-of-two proof is an optional future opportunity; preserve separate other-agent tracker work.

### Agent coordination (October 27, 2026)

This tracker is shared with another agent working on benchmark performance. This chat completed **PathRepeatedSmallOps and compiler fact preservation** and now owns the user-requested **guarded QueueDequeue loop versioning** implementation. The inline const-join wrapper eliminated external copies; the new payload-liveness proof now also eliminates all 25,000 benchmark payload stores in the host executable. Preserve the other agent's earlier **QueueDequeue/collections evidence** and current filesystem/source/test changes. Re-read the relevant section before each edit and patch only the intended text.

**PathRepeatedSmallOps unused-libm handoff (October 28):** completed and validated; no build, test or timing window is active. `CompilerCli.cs` shares the target-specific final-link policy, and the optional `NativeDependencies.ImplicitLibraries` fact preserves compiler-inferred math provenance through packages. Explicit libraries/linker state and live math remain intact. Fresh process timings are Stark **0.9968 × C / 0.6954 × Rust** (C = 1.0000; Rust = 1.4335 × C), effectively C parity at this timing scale. The implementation, validation and comparison are recorded with this completed item. Preserve the other agent's guarded-drain evidence, pre-existing overload/test edits and unrelated install script. The requested quiet window was not acknowledged; no builds/tests from this chat overlapped samples.

### State of the checkout and priorities

The working branch is `performance-optimization`, based on `ae26be4` (`Restore complete target-aware literal path facts`), following `e14e39b` (`Fix drops for owned out arguments`), `3cbac18` (`Eliminate unobserved dynamic payload writes`), `b6d76ac` (`Optimize queue reserve and ring growth`), `ed8ee53` (`Inline two-input constant path joins`) and `e06541f` (`Cache ordinary arena slabs`). Earlier committed work includes compiler code-generation improvements (`4b298e5`), buffered-read/dynamic-buffer and text-parsing changes (`4c1bf0f`), and wide Unicode parsing with limb-and-carry arithmetic (`f74ef7b`). The arena slab change is committed. Its extreme single-allocation overflow edge case was deliberately left for later at the user's direction; retain the existing comment and do not expand the current work to it.

The payload-liveness and nine-field path-fact implementations, tests and documentation are committed. The unused-libm implementation adds native dependency provenance and final-link liveness, a focused native integration test, and documentation/tracker updates separately. **Pre-existing uncommitted** changes include the separate overload edits in `SsaConstStdlibHelperSpecializer` and `CompilerPipelineOptimizeSsaTests`, plus the other agent's guarded-drain tracker evidence. The other agent's MIR lowering/drop and source/ported queue-test work is committed in `e14e39b`; its prior collections and queue changes are committed in `b6d76ac`. Check `git status` for the current complete list. `install-arch.sh` is an unrelated untracked file; preserve it.

The two-input const-join inline declaration removed **PathRepeatedSmallOps**' join calls and 20,000 external `memcpy` calls. Payload liveness removed the remaining 25,000 payload stores. The restored nine-field literal `PathFacts` producer now also eliminates the checksum loop in the ordinary linked executable. The explicit fallible reserve, allocation-failure result and real runtime cleanup remain.

For the wider work, use the full-suite table below as the last broad snapshot, not as the current state of every benchmark: it predates the arena fix and the later path work. It identified 39 scenarios with Stark slower than C and four with Stark not faster than Rust, using the two distinct criteria above. Focus on benchmarks actually appearing in the recorded results, investigate generated code and comparable work before choosing changes, and update individual entries after focused reruns. In particular, account for `FileBufferedReadWrite`'s different last-write buffer work before interpreting ratios. The user expects Stark to have the opportunity to outperform Rust because of its stronger restrictions; measured results still need verification, especially when differences are smaller than the spreads.

### PathRepeatedSmallOps: implementation and evidence

The benchmark reserves a fresh `OwnedAscii`, performs two joins of literals per iteration, and observes the resulting lengths and literal path facts, not the resulting bytes. C's final code has no joins, scans, copies, or allocation; Rust's final benchmark body reduces to a no-allocation shim. The initial Stark baseline retargeted both calls to `TryJoinConst` but still executed the joins. The inline-only build removed those calls and all external copies, while retaining repeated literal stores. The new liveness build removes those stores too. Literal path-fact and extension scans are folded. Preserve the initial fallible reserve and its failure path: matching C's lack of allocation is not by itself a correct Stark transformation.

The 100-run harness after the inline-wrapper change measured Stark 5653 µs, C 5532 µs, Rust 5621 µs: **1.0219 / 1.0000 / 1.0161**, with C = 1.0000. Spreads were **20.9% / 19.7% / 19.3%**, larger than those gaps. [Results](results/results-20261028T030455Z.zJAtA0.csv) · [machine](results/machine-20261028T030455Z.b89L70.txt). The last pre-inline baseline was **1.0258 / 1.0000 / 1.0510** ([CSV](results/results-20261028T024819Z.hc9avU.csv), [machine](results/machine-20261028T024819Z.lHBL8C.txt)); separate runs do not establish a precise improvement. The interleaved before/after comparison below measured a 16.3% lower Stark mean. The code-shape result is deterministic: zero external copies, but retained stores. The change uses the wrapper's explicit inline contract rather than modifying a global import threshold.

Start with `SsaConstStdlibHelperSpecializer`, `SsaDynamicStorageOptimizer`, `SsaValueFactAnalyzer`, `SsaScalarReplacementOptimizer`, `stdlib/src/System/Text.stark`, and `stdlib/src/System/IO/Path.stark`. The scalar-replacement pass currently treats aggregate addresses passed to direct calls as escaping; whole-parameter effect summaries do not prove which header fields or reachable payload bytes are read. Dynamic-storage facts track allocation identity and capacity in some cases but conservatively demote facts at mutating or opaque calls. The intended analysis needs to follow the owner, its header fields, and the separately allocated payload across calls and control-flow joins, and distinguish reading `Length`/`Capacity` from dereferencing or exposing `Data`. A pointer load alone is not a payload read, but a pointer passed or returned elsewhere can expose the bytes. Unknown calls, indirect calls, raw pointer escapes, possible aliases and reallocations must remain conservative.

**User-directed priority: preserve established facts throughout compilation.** Carry ownership, allocation identity, literal contents/lengths, deep constness, disjointness, capacity after successful reserve, initialized ranges, capture and field/payload effects through lowering, call retargeting, module/package boundaries and LLVM emission wherever their proofs remain valid. Remap facts when values or representations change; do not discard them merely at a call or phase boundary and depend on later inlining to rediscover them. Invalidate only the facts and regions that an operation may change; at control-flow merges retain facts true on every incoming path. Reuse authoritative fact models and summaries rather than adding parallel benchmark-specific rules. Unknown effects still require conservative invalidation. For this benchmark, body availability is one symptom to investigate, while preservation and consumption of the existing semantic facts is the preferred compiler direction.

For this benchmark, literals are disjoint from the fresh destination, and success of `Reserve(64)` precedes joins requiring 14 and 20 bytes. A correct solution must establish those facts on the relevant control-flow paths; preserve capacity, status, length, allocation failure and cleanup behavior; and retain writes whenever bytes can be observed. `Clear` moves bytes out in source, but its current Linux x64 machine code only resets the header length; it does not read payload bytes. Cleanup frees the allocation, so reason about that operation explicitly. Preserve overlapping-input behavior and target-specific separator rules. Do not add blanket LLVM allocator contracts to the runtime helpers: they use interior pointers, read allocation metadata before the returned pointer, and publish freed pointers to a global bucket cache. Fresh allocation helpers already emit return `noalias`; adding that attribute is not the missing fix, and realloc cannot generally promise a fresh result. The cache escape is relevant to LLVM dead-store elimination, but the available evidence does not prove it is the sole reason each store survives.

The checked-out C/LLVM sources are under ignored `benchmarks/compiler-sources/`; the observed C O3 trace uses inlining followed by LLVM's ordinary scalar and memory optimizations. The ignored `benchmarks/codegen/path-repeated-investigation/` contains the original emitted IR, binaries, disassemblies and experimental controls. The hand-edited length/status-only control demonstrates a possible final shape while preserving the initial reserve; it is not a compiler implementation. Forced inlining removes the join calls but leaves payload stores. A separate constant-byte-store IR control removes the loop while leaving a final set of stores. These controls narrow the problem but do not supply the missing correctness proof. Ignored files are local evidence and may not be present in another checkout.

### Unused libm: completed for direct builds and packages (October 28, 2026)

**Status: completed.** Direct builds and newly built package consumers eliminate unused libm while preserving explicit library intent, live math and linker state.

The final linker now resolves the conservative pre-optimization math requirement. On Linux and FreeBSD, `ImplicitMathLinkArgument` emits one scoped `--push-state,--as-needed,-lm,--pop-state` argument, shared by direct executable linking and package-native consumption. Other targets retain their previous flags. Only compiler-inferred math uses this policy; explicit native libraries, explicit link arguments and surrounding linker state retain their behavior. The math intrinsic scan is deliberately retained: deleting an early candidate dependency would lose a real dependency when LLVM later lowers a live intrinsic to libm.

Packages retain their existing `Libraries` list and add the optional `NativeDependencies.ImplicitLibraries` origin fact for inferred `m`. The builder normalizes it, binary serialization carries it, content identity covers it without changing API identity, and the loader preserves it through source-free SDK consumption. New consumers interpret packages without this fact using the existing unconditional behavior; rebuild an older package to gain conditional libm linking. Explicit package `m` is never marked inferred. SDK archive/dependency order is unchanged, and package images remain authoritative for native link metadata. Existing release catalog/validator assumptions about logical library lists therefore remain valid; no duplicate release-side pruning policy was introduced.

**Validation:** 100 focused Release tests pass: 12 new native cases, 25 package CLI cases, 62 package architecture/loader cases, and the full source-free packaged scalar math check. The new cases execute real binaries and inspect ELF `DT_NEEDED`, covering unused and live math in direct/source-free SDK builds; explicit package `--native-library m` and `--native-link-arg=-lm`; explicit consumer library/link arguments; restored default and caller-selected `--as-needed` state; legacy package behavior; and origin-sensitive content identity. The live check obtains its sine input from a separately compiled native object, so it cannot fold to a constant. All ten fresh benchmark LLVM modules verify. No full release or unrelated long suite ran.

The production benchmark is **17,032 bytes**, SHA-256 `446fb5aa3410950c5a3da42fa3b9d7516b1cbdf1200d43cbd72de876666ce8c4`, and has only `libc.so.6` in `DT_NEEDED`. It returns **0 normally and 1 under runtime-mapping ENOMEM**. `main` still calls `__stark_runtime_try_realloc(null, 0, 64, 1)` and `__stark_runtime_free`, preserving the checked reserve and cleanup; it has no payload writes, memcpy calls or checksum loop. Relinking the same fresh objects with unconditional `-lm` gives identical `main` instructions and the same success/failure results, while restoring the unused DSO.

C was rebuilt with `-O3 -DNDEBUG -std=c17`. Rust was rebuilt with `-C opt-level=3 -C debug-assertions=no -C overflow-checks=no`; its canonical executable and LLVM IR were emitted in separate invocations. C still has no allocation and retains 125 two-lane vector checksum iterations. Rust's canonical benchmark body jumps to the returning no-allocation shim; its separately emitted LLVM IR retains a vector checksum loop. These differences and Stark's fallible allocation contract remain as described below.

100 randomized interleaved whole-process samples per variant, one warmup, CPU 0, seed 20261028, including startup:

| Executable | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| C, fresh canonical | 537.810 | 518.562 | **1.0000** | 46.2 |
| Stark, implemented direct build | 536.085 | 519.829 | **0.9968** | 43.6 |
| Rust, fresh canonical | 770.953 | 749.074 | **1.4335** | 41.9 |
| Same fresh Stark objects, unconditional libm | 591.503 | 582.747 | 1.0998 | 36.7 |
| Previous nine-field Stark executable | 604.097 | 587.391 | 1.1233 | 37.6 |

Stark/Rust is **0.6954**. The matched libm relink adds **62.9 µs at the median** while keeping the same benchmark instructions and allocation behavior. Production Stark now effectively matches C at process scale; the 0.3% mean difference is far smaller than process spread and is not evidence of a stable speed advantage. Allocation specialization and physical backing-class/lifetime facts remain secondary opportunities, not part of this fix.

Ignored evidence is under `benchmarks/codegen/path-repeated-investigation/unused-libm/`: fresh executables, ten emitted LLVM modules/objects, C/Rust IR and disassemblies, matched relink, failure/ELF/hash report `validation.json`, `measure.py`/`process-interleaved.json`, and focused test logs. Raw timing data records machine, HEAD, dirty state, hashes and individual samples. The repository-wide stale-assumption audit covered native metadata constructors/normalization/serialization/identity/loading, SDK native link planning, self-hosted/ported tests, release validators/catalogs and documentation. Other-agent source/tests and guarded-drain tracker evidence are preserved.

### Allocation/cleanup investigation before the unused-libm implementation (October 28, 2026)

**Historical before-fix investigation; implementation and fresh measurements are above.** The ordinary `ae26be4`-era binary still has only `__stark_runtime_try_realloc(null, 0, 64, 1)`, the null-result status, and `__stark_runtime_free(result)` in `main`. Existing nine-field and payload facts removed the benchmark's joins, scans, writes and checksum loop. C's canonical executable has no allocation and retains a small vector checksum; Rust's canonical benchmark body jumps to its returning no-allocation shim. Rust's checked-out `library/core/src/alloc/global.rs` explicitly permits dead allocation elision, stack promotion and assuming allocation succeeds. Stark's checked reserve currently has an observable error result; dead payload bytes do not establish permission to erase it.

#### What the allocator and cleanup actually do

For this Linux x64 build, allocation raises alignment 1 to its runtime minimum 8 and selects the **64-byte bucket**, whose storage alignment is 16. A cold bucket requests **4,039 bytes** from `mmap` (one 4 KiB mapping on this host), with 24-byte block headers and a 96-byte stride. It initializes **42 blocks**: the first is returned and the other 41 are linked into the thread-local free list. Cleanup reads the block's physical bucket size at `Data - 8`, selects the corresponding bucket, and pushes the block. After this benchmark finishes, all 42 blocks are cached. **Its cleanup performs no `munmap`.** Later warm calls pop and push that same block without a syscall. These are runtime header/freelist writes, separate from the 25,000 eliminated user payload writes; the current scalar proof correctly preserves them.

The trace follows `OwnedAscii.Reserve` → dynamic `TryReserve` → `EmitDynamicStorageTryReserve` → runtime try-reallocate/try-allocate and free helpers. Byte counts, alignment, the null old pointer and zero old capacity all reach final LLVM/machine code as constants. This is not another loss of those scalar facts. The remaining opaque/general helper boundary prevents their ordinary call site from becoming the small bucket-specific operation. `EmitDynamicStorageFree` currently forwards only `Data` to the generic free helper.

#### Cold and warm costs, excluding process startup

A C driver calls the **already generated native probe objects without driver LTO**. For canonical Stark, only the native object's `main` symbol is renamed; its code and linked allocator implementations are preserved. Each of 100 cold samples clears the isolated bucket and unmaps the preceding slab **outside** the timed region. Each warm sample is a block of 10,000 calls, with every result checked. Outside timing, the driver checks all 42 nodes, 16-byte alignment, each header's base/length/bucket, and that the warm head is unchanged and still links 42 valid same-slab nodes. The explicit reset/unmap is diagnostic setup, not benchmark cleanup. CPU 0 is pinned; driver order is randomized, but samples within each driver use a fixed cold/warm order. Times include timer/call overhead and remain subject to shared-host/frequency effects.

| Probe | Cold median | Warm median per call |
| --- | ---: | ---: |
| Canonical Stark allocation + cleanup | **3,176 ns** | **7.748 ns** |
| Same extracted post-LTO graph, another ordinary O3 pass | 3,171 ns | 4.380 ns |
| Late allocation inlining control | 3,166 ns | 4.044 ns |
| Late allocation + cleanup inlining control | 3,196 ns | 2.696 ns |
| Proven-bucket-64 cleanup control | 3,196 ns | 1.686 ns |
| Warm cache-effect fusion control | 3,166 ns | 1.685 ns |
| C's canonical checksum, native object | 71 ns | 49.577 ns |
| Empty native probe / timing floor | 30 ns | 1.685 ns |
| Same direct `mmap(4039)`, no page touch | 1,453 ns | Not measured |
| Same direct map plus one byte touch | 3,091 ns | Not measured |

Canonical cold samples ranged **3,116–6,713 ns**; warm block averages ranged **7.747–14.053 ns**. Map-only/map-and-touch controls use the same Linux syscall convention and unmap outside timing. Their difference indicates that mapping and the first page touch dominate this cold case; the data do not justify a precise additive attribution to all 42 initializations. All allocator controls' cold medians stay near 3.2 µs. Changing slab/refill policy would affect reuse and future failure behavior across workloads; do not reduce it to one block based on this benchmark. Warm Stark is already much cheaper than C's retained checksum in this diagnostic, but these are different remaining operations and are not whole-program language ratios.

Cold mapping failure still returns **1** for canonical Stark and every allocator control. A second test warms the cache, then denies `mmap`; each of six allocator probes performs 100 successful calls with the same 42-node cache. This distinguishes an empty-cache failure from an allocation satisfied by existing resources and checks cleanup's persistent effect. No allocator control receives a blanket `nonnull`, allocator-family or dead-allocation promise.

#### The larger gap is `libm` loading

The canonical Stark ELF has `DT_NEEDED` entries for **libm and libc**, but no live dynamic math symbol. C needs libc only. Rust needs libc, libgcc_s and the loader and retains its process-runtime startup; its benchmark body does no allocation. Stark has a 72-byte TLS segment for the nine buckets; C has no TLS segment and Rust's TLS memory size is 80 bytes. These are executable-layout observations, not evidence that TLS itself explains the time difference.

`CompilerCli.LlvmTextRequiresMathLibrary` scans the **pre-optimization LLVM text**, including intrinsic declarations and unused function bodies. `System_Math.ll` contains sin/cos/exp/log/pow declarations and calls even for this transitive path workload. The dependency compiler ORs that result into `RequiresMathLibrary`; `BuildImplicitLinkArguments` appends unconditional `-lm`. ThinLTO later removes the math code, but the dependency's final liveness is not represented by that sticky boolean and ordinary `-lm` still produces `DT_NEEDED`. The package producer, `BuildPackageNativeDependencyManifest`, likewise adds `m` to native metadata; any implementation must trace both direct linking and package producer/consumer paths rather than fixing one and leaving the other stale.

A same-ELF attribution control changes **only three bytes at `main` entry** to return zero, preserving all segments, TLS, relocations and dependencies. It deliberately removes allocation failure/cache effects and is **not a valid optimization**. In the first 100-process sample, canonical Stark mean/median was **595.8 / 581.5 µs**, while this no-work control was **600.4 / 578.2 µs**. The median difference of about 3.2 µs is consistent with the cold probe; the noisy mean goes the other way. Removing all `main` work does not remove the roughly 60 µs process-level difference from C. An unchanged relink without `-lm` unexpectedly approached C, which motivated the matched dependency experiment below. Do not attribute that first relink delta to allocator changes.

The decisive controls link the **same original saved bitcode objects in the same order**, changing only library flags:

- Unconditional `-lm` retains libm.
- Omitting unused `-lm` links successfully with libc alone.
- `--push-state --as-needed`, then `-lm`, then `--pop-state` produces a binary **byte-identical to the no-libm relink** and retains allocation, cleanup, normal exit 0 and mapping-failure exit 1.
- A live `sin((double)argc)` control still retains libm under the scoped flags and executes successfully.
- Independently, adding unused `-lm` to the same C benchmark object produces the same startup penalty without changing its checksum/allocation work.

After one warmup, a second **100-process-per-variant randomized interleaved CPU-0 run**, including process startup, gave:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark canonical | 602.6 | 582.3 | **1.1274** | 55.4 |
| Same-object Stark relink, `-lm` | 606.8 | 583.3 | 1.1353 | 85.5 |
| Same-object Stark relink, no unused `-lm` | 536.0 | 518.9 | **1.0028** | 62.4 |
| Same-object Stark relink, scoped as-needed `-lm` | 529.5 | 518.6 | **0.9908** | 64.2 |
| C, no libm | 534.5 | 518.9 | **1.0000** | 62.6 |
| Same C object, unused libm added | 603.1 | 585.5 | 1.1283 | 59.8 |
| Rust canonical | 757.0 | 744.4 | **1.4163** | 24.2 |

Canonical Stark/Rust is **0.7960**. The matched Stark medians differ by **64.5 µs** with/without libm, and the independent C medians by **66.5 µs**. Despite large process-level spreads, the matched dependency change and C control consistently identify an avoidable dynamic-loading cost much larger than the 3.2 µs allocator phase. The two byte-identical no-libm variants have different means, demonstrating sampling noise; **do not claim production Stark is faster than C** or that 0.9908 is a stable advantage. Source/compiler production output is unchanged. The earlier nine-field result (1.0899 relative to C) and this result use different samples and must not be subtracted as an implementation improvement.

#### Concrete opportunities, in priority order

1. **Preserve implicit dependency provenance and final symbol liveness.** Treat a pre-optimization math indication as a candidate dependency whose necessity is resolved by the final linker. The existing ELF linker's scoped as-needed mechanism demonstrates a small, reviewable route for the automatically inferred math dependency. Preserve explicit user/library loading intent and linker state; do not globally prune native libraries that may have constructor/side effects or assume GNU flags apply to every target. Trace direct executable linking and package-native metadata together, retaining real math users and source-free package consumers. Use one authoritative policy rather than duplicating a dependency-pruning rule across scripts/schema/validators. This primary measured opportunity is implemented above.
2. **Use established empty-owner/size/alignment facts at allocation.** A proven null old pointer can select the existing fresh try-allocate helper rather than generic try-reallocate, or a private constant specialization can expose the 64-byte path. No-wrap/range data already reach LLVM; making all allocator helpers always-inline is not the answer. In an early forced-inline control, `Reserve` grows beyond import/inlining limits and `main` regains reserve calls in its loop. Applying the control only after the original benchmark has simplified avoids that phase-ordering confound. A second ordinary O3 on an extracted post-LTO graph already exposes much of the allocation path; that is diagnostic evidence, not a reason to add a second global optimization pipeline.
3. **Carry physical backing-class and lifetime facts into cleanup.** Existing dynamic region facts retain owner, backing identity, logical capacity/prefix, element alignment and runtime-vs-arena provenance, but no exact physical bucket class. Cleanup discards those distinctions into a raw `Data` argument and reloads metadata. Fresh runtime allocation of 64 bytes at this alignment proves bucket 64; **logical capacity 64 alone does not** (a resized/reused larger allocation can keep its old physical bucket). A known-class release control avoids the generic header lookup/dispatch while preserving the same cache push; a manual no-observer warm-pair control can skip a pop/push pair whose cache state is unchanged. Both require a real lifetime/escape/effect proof and target/runtime layout knowledge before implementation. Retain class through proven reuse, invalidate it at unsupported mutation/growth/escape, distinguish arena/custom backing, and merge it only when incoming facts agree. Runtime cache/header pointers and payload pointers are separate regions; loading a cached pointer alone does not encode their disjointness to LLVM. Gains here are nanoseconds on warm calls and do not explain the original process gap.

This investigation changed **only this tracker and ignored artifacts** under `benchmarks/codegen/path-repeated-investigation/allocation/`. They include original/forced-inline/post-LTO IR, native objects/disassemblies, `build-controls.py`, `build-late.py`, `build-extra.py`, `driver.c`/`mapping.c`/`build-driver.py`, cache/failure checks, `same-elf-patch.json`, `process-interleaved.json`, `kernel-times.json`, `link-math.py`/`link-math-report.json`, `live-math.c`, ELF reports, and `measure-math.py`/`math-interleaved.json`. Raw samples record CPU, binary hashes, HEAD, dirty state and machine data. All early-control source modules, three post-LTO modules and five late controls verify; normal, cold-failure, warm-failure and cache-state checks pass. No full release, full benchmark suite or unrelated compiler test suite ran. The tracker quiet-time request was not acknowledged; no builds/tests from this chat overlapped either sample window. The other agent's guarded-drain tracker section and source changes remain untouched.

### Nine-field literal facts: implemented (October 28, 2026)

The existing `SsaConstStdlibHelperSpecializer` now recognizes the actual nine-field `PathFacts` contract and emits `RootLength` and `Absolute` along with the original seven fields. Its constant computation matches `GetFacts`/`GetConstFacts`: target-specific separators, ASCII drive letters and colons, drive-relative versus absolute roots, leading separators, trimming, directory and segment ranges, empty paths and dotfiles. It retains root length separately from trimmed end, including Windows `C:/` (`RootLength=3`, `End=2`). The same root facts feed constant base/directory/extension projections. A missing target uses the host's path convention; explicit cross-target triples remain authoritative.

All nine values enter the existing typed SSA construction, with the original locations, alias/access-group metadata and field types. Cleanup can remove redundant zero inserts while retaining the zero-initialized seed. Later SSA rewriting and source-free package consumers preserve the remaining constants. Restoring the producer suffices for this benchmark: no new field-summary system, LLVM aggregate-copy policy, bool representation, runtime/stdlib API, package schema or global optimization budget was introduced. Dynamic inputs remain runtime calls. The general mixed `i1`/byte-copy opportunity identified below remains separate from this constant-record case.

**Ordinary Release/ThinLTO output now has zero checksum/vector-loop instructions, zero payload stores, and no join/copy/scan calls in `main`.** The only remaining calls are the checked 64-byte initial reserve helper and real free. Success returns 0; denying post-loader `mmap` returns 1. A separately compiled source control changes only the expected checksum to 320001: it returns 4 on success and 1 on allocation failure. This validates the checksum decision rather than merely dropping it. No benchmark source or emitted IR was patched for the production result.

| Implementation | Final benchmark computation | Allocation/error work |
| --- | --- | --- |
| Stark, nine-field implementation | No checksum loop or payload work | Checked reserve, failure result and real cleanup |
| C, canonical O3 | 125 checksum iterations, one two-lane vector add per iteration | No allocation |
| Rust, canonical linked build | No checksum loop; benchmark body jumps to the returning shim | No benchmark allocation; process runtime remains |

C/Rust were freshly rebuilt using the harness flags, with Rust IR emitted separately from its executable. One warmup and 100 randomized, interleaved CPU-0-pinned process samples per binary all returned success; process startup is included:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark before nine-field producer | 600.3 | 589.0 | 1.1034 | 41.7 |
| Stark with nine-field producer | 593.0 | 583.3 | **1.0899** | 34.1 |
| C | 544.1 | 522.8 | **1.0000** | 49.8 |
| Rust | 761.5 | 742.8 | **1.3997** | 50.9 |

Stark/Rust is **0.7787**, or a 22.1% lower mean in this sample. Before/after Stark differs by 1.2%, much less than the observed spreads; do not claim a stable timing improvement from it. The checksum's elimination is deterministic. Stark still performs observable fallible allocation/cleanup that C and Rust eliminate, so checksum parity does not establish whole-program work equivalence or explain every microsecond of the remaining gap. The earlier heavily disturbed investigation sample below is historical, not a comparable baseline.

Validation passed **53 focused Release pipeline tests**, including 30 path/target cases from the actual stdlib declaration, root-aware projections, real stdlib accessors/copies, Linux/Windows source-free package consumers, and nonliteral inputs. Three old seven-field fixtures and their three Stark-native ports were updated to the current contract. The ported literal assertion now reads optimized SSA and distinguishes a call from a retained declaration; the three port fixture requests and assertions pass through the Release host-test protocol (the full Stark-native suite was not run). **Five path/package standard-library tests** passed: the new native observer checks every field and every path byte after a copy, comparing folded literals with exported noinline runtime calls from the source-free stdlib package; existing tests cover payload-byte readers, ownership/aliases/growth, overlapping joins, tail-copy generation and staged Windows behavior. All **10 emitted Stark modules plus fresh C/Rust IR** verify. The repository-wide audit covered field/layout guards and constructors, projection consumers, value rewrites, package contracts, source/ported tests and documentation; no public schema or self-hosted optimizer change is required. `git diff --check` passes. No full release workflow or full benchmark suite was run.

Evidence is in ignored `benchmarks/codegen/path-repeated-investigation/nine-field/`: `stark`, `temps/`, `stark-main.asm`, fresh C/Rust binaries and IR, `wrong-checksum.stark`/binary, `failure.json`, and `measure.py`/`interleaved.json`. Raw samples include binary hashes, machine information, HEAD and dirty state. Other-agent code edits remain untouched; shared-host quiet time was not acknowledged.

### Checksum investigation before the nine-field implementation (October 28, 2026)

**Historical investigation snapshot, before the implementation described above.** The following were isolated controls, not compiler implementations. That build of `PathRepeatedSmallOps` was byte-identical to the `3cbac18` implementation evidence (`01fbe1fe…80f26`). C and Rust were rebuilt with the same harness flags recorded below; Rust linking and IR emission used separate invocations. Clang/opt are 23.1.4; rustc 1.99.1 reports LLVM 23.1.1. These are local toolchain observations, not dependency requirements.

The remaining checksum is 64 per iteration, 320,000 total. In the ordinary final machine code Stark runs **250 loop iterations with two `paddq` instructions each**; C runs **125 with one `paddq` each**. Both use two 64-bit SIMD lanes, so Stark executes four times as many vector-add instructions in this loop, plus twice as many loop branches. This is an instruction-count comparison, not a fourfold whole-program time claim. Rust's LLVM-only output has the same 125-iteration checksum shape as C, but its canonical linked benchmark body jumps to the no-allocation shim, whose body returns. Inspecting only Rust IR would miss that final elimination.

**The induction facts are already present.** Emitted Stark LLVM has the original `i16` phi, comparison with 5000, `add nuw nsw i16 …, 1`, and the source `willexit` loop's `llvm.loop.mustprogress`. Optimized LLVM widens the counter to `i32` with exact step/exit constants. There is no evidence that dropping the counter's range or overflow facts causes this gap; widening every small integer or adding unconditional overflow promises is not the next fix.

Two concrete fact barriers were identified:

1. **The literal `PathFacts` producer recognizes an obsolete layout.** `SsaConstStdlibHelperSpecializer.TryResolvePathFactsType` requires exactly seven ordered fields. The actual `System.IO.Path.PathFacts` has nine, including `RootLength` and `Absolute`. The literal `GetFacts("alpha/beta.txt")` call therefore survives this SSA specialization and appears in emitted LLVM; LLVM eventually inlines and folds its scans. Existing `ConstStdlibHelperSpecializationPrecomputesPathFactsForLiteral` and Windows-separator tests construct a synthetic seven-field type, so their success does not cover today's stdlib contract. `CreatePathFactsFieldValue` and `PathFactsValue` also lack the two new fields. Updating only the field count would be insufficient: the producer's root/segment/directory/absolute calculations must match the real target-specific implementation. In particular, Windows drive-relative `C:file`, drive-absolute `C:/file`, separator roots, empty paths, root-only paths, trailing separators and dotfiles need real stdlib/source-free-package coverage. Preserve the named field values through later SSA rewrites and imported accessor calls; do not depend on LLVM to rediscover a discarded literal record.
2. **An aggregate byte copy loses usable boolean type information.** `LlvmFunctionBodyEmitter.Aggregates` lowers the 72-byte return-slot-to-local copy as `llvm.memcpy.inline`. After SROA, optimized `main` still has `store i1 true` followed by `load i8` from that slot and `trunc … to i1`. The resulting `HasExtension` condition selects contributions `(60, 4)` or `(56, 8)`, both summing to 64. The vectorizer sees the unresolved contributions separately; machine code later folds the flag but retains the larger loop and constant-vector setup. Checked-out LLVM `SROA.cpp` explicitly rejects integer widening for bit-padded loads/stores (e.g. i1 stored in a byte), consistent with this trace. A local control replacing only this copy with a typed aggregate load/store preserves the field types and reaches C's checksum shape. This does not justify changing all copies or bool ABI storage: preserve representation/overlap/initialization contracts when bytes or padding can be observed, and keep the general byte-copy path for unsupported cases.

Controls use the same original emitted root IR and dependency objects, ordinary O3/ThinLTO flags, existing arithmetic/loop metadata and actual runtime helpers. They add no `assume`, no-wrap or allocator contracts:

| Variant | Checksum in final linked code | Allocation/cleanup |
| --- | --- | --- |
| Stark ordinary build / unchanged recompile | Two vector accumulators; step 20, 250 iterations; repeated constant-vector setup | Checked reserve and real free |
| Typed aggregate-copy control | One vector accumulator; step 40, 125 iterations, matching C | Checked reserve and real free |
| Early constant-facts control | **No checksum loop or comparison** | Checked reserve, failure result and real free |
| C, canonical | One vector accumulator; step 40, 125 iterations | None |
| Rust, canonical linked | No checksum loop | No benchmark allocation; runtime/shim remains |

The early-facts control replaces only the literal `GetFacts` result/copy with its complete typed constant record. A separate native wrapper calls the actual `GetFacts` implementation and checks all nine fields, including the original path pointer/length: `Length=14`, `End=14`, `RootLength=0`, `SegmentStart=6`, `ExtensionStart=10`, `DirectoryLength=5`, `HasExtension=true`, `Absolute=false`. This confirms the injected record for this input; it is not a general path evaluator or an implementation of the missing SSA proof. Ordinary, typed-copy and constant-facts binaries all return 0. Replacing the expected checksum with 320001 makes **each return 4**, so the optimization is not just discarding validation. The typed-copy control also passes no-extension and dotfile inputs with `HasExtension=false`. Linux post-loader `mmap=ENOMEM` injection makes every Stark variant return **1**, with empty output; C returns 0. Applying that filter to Rust aborts during alternate-stack initialization, so it cannot diagnose Rust's benchmark allocation behavior and is not used for that claim.

**Pass ordering is an additional diagnostic, not a proposed global policy.** Running another `default<O3>` on final C or Rust IR eliminates its checksum loop; disabling unrolling prevents C's elimination. The pass trace shows full unrolling followed by folding/cleanup, not a new Stark reduction proof. Another ordinary O3 on Stark's final IR resolves/collapses contributions to one accumulator but retains a step-20 loop. Raising the unroll threshold to 10000 removes it in an experiment. These controls show late simplification and cost sensitivity; neither a second global O3 run, a global unroll-threshold increase nor disabling vectorization is justified by this one benchmark. Prefer correcting the earlier fact producer and retaining typed field information.

The fresh 100-process-per-variant randomized, interleaved CPU-0 sample was **too disturbed to estimate a new improvement**. One warmup per binary; all normal executions returned 0; startup included:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark canonical | 2067.2 | 1567.1 | **1.1804** | 657.0 |
| Stark unchanged recompile control | 1872.8 | 1397.6 | 1.0694 | 316.1 |
| Stark typed-copy control | 1784.2 | 1370.7 | 1.0187 | 220.8 |
| Stark early-facts control | 1725.1 | 1331.5 | 0.9850 | 434.5 |
| C | 1751.3 | 1406.4 | **1.0000** | 475.3 |
| Rust | 2497.1 | 1927.9 | **1.4259** | 583.1 |

Stark/Rust is 0.8278 in this disturbed sample. Even the unchanged recompile differs substantially from canonical Stark; the control timing deltas cannot be attributed to the edits. The preceding less-disturbed production sample remains **Stark/C/Rust = 1.1162 / 1.0000 / 1.4099**, with 24–27% spreads for those rows. Do not combine the two runs or report the early-facts control as production Stark beating C. A quiet-time request was posted in this tracker before sampling, but was not acknowledged; other-agent MIR/drop and queue-test work was visible. This chat ran no builds/tests during the samples.

A separate repeated-call diagnostic renames `main` in the already generated post-LTO native Stark objects; it makes no optimizer changes to those kernels. A C driver without LTO into the kernels runs 100 warmup calls per variant and 100 randomized blocks of 10,000 calls each, pinned to CPU 0. It checks every result and warms/shares the Stark allocator cache. Median block time per call was **193.6 ns** for ordinary Stark, **102.9 ns** for typed-copy, **11.7 ns** for early-facts and **92.3 ns** for C; observed ranges were 99.6–749.1 / 56.0–600.9 / 7.4–108.9 / 49.6–431.1 ns respectively. The code-shape differences are consistent with these measurements, but the cache/startup regime differs from the executable benchmark and block spreads are still large. These are diagnostic kernel timings, not headline language ratios or a measure of cold allocation cost. C's eliminated allocation and Stark's observable fallible reserve remain a real work difference after the checksum is removed.

**Recommended implementation order:** restore complete target-correct literal facts for the actual nine-field stdlib contract; preserve those field values through SSA/local/call/package boundaries; then examine narrowly scoped typed aggregate-copy lowering if copies still obscure facts. Validate actual source and source-free package consumers, dynamic/nonliteral paths, both boolean values, root semantics for each target, and byte-reading/overlap cases. Rebuild ordinary output and verify the final loop before considering additional arithmetic machinery. Keep the checked reserve/failure path and real runtime cleanup. No production compiler source, benchmark source or test source was changed in this investigation.

Evidence is in ignored `benchmarks/codegen/path-repeated-investigation/checksum/`: `current-temps/`, canonical `stark-current`/`c`/`rust`, C/Rust IR, `stark-lto.ll`, the unchanged/typed-copy/constant-facts roots and post-LTO modules, disassemblies, `build-controls.py`, `check-fields.py`, `validate-controls.py`, `checksum-validation.json`, `failure-validation.json`, pass traces, `measure.py`/`checksum-interleaved.json`, and `build-repeat.py`/`repeat.c`/`repeat.csv`/`repeat-summary.json`. All **15 current/dependency, C/Rust and diagnostic post-LTO LLVM modules** verify; the field, checksum and allocation-failure controls above pass. No full release workflow, unrelated test suite or full benchmark suite was run.

### Payload-liveness implementation (October 28, 2026)

**Status: implemented and validated for this benchmark.** `SsaDeadPayloadSpecializer` proves that a fresh local owner's selected runtime-backed scalar dynamic region has no observable byte read or escape anywhere in the function or supported direct-call graph. It tracks the owner/header separately from payload pointers and slices, propagates aliases to a fixed point, and rejects ambiguous phi/select/local merges. Only payload writes are removed. Header stores, length/prefix transitions, status calculations, reserve/reallocation operations and cleanup remain. Unused trivial moved-out scalar values do not observe the bytes; known runtime reallocation copies likewise do not expose their contents. The proof covers the owner's backing generations only while all their contents stay unobservable; it does not assume that reallocation always returns a fresh pointer.

Calls needing write removal get private SSA specializations; ordinary calls and ABI surfaces keep their general implementations. Opaque/indirect calls receiving the region, exported callees, returned views/owners/pointers, raw-pointer escapes or reinterpretations, unsupported operations, arena backing, and potentially negative/truncated pointer indexes decline the proof. Existing integer-range facts and target pointer width constrain address derivation. The implementation uses the SSA proof directly, rather than adding `dead_on_return` or allocator-family attributes to runtime helpers. The runtime cache's freelist/header writes are preserved.

Typed dynamic indexing and range slicing retain `SourceStorageType` through MIR, SSA lowering, cleanup, constant propagation and append rewriting. Common-subexpression keys distinguish these structured operations from raw-pointer operations. A nonzero arbitrary raw offset or explicitly constructed raw slice declines the proof: a pointer derived from `Data` alone does not establish that its destination remains within that owner's payload. This origin records the structured storage contract, not a blanket LLVM `inbounds` promise. Source-free generic package bodies regenerate it from their existing typed representation; no serialized package schema changes are required.

Two compilation-boundary fixes matter here. Binary output prunes lowering earlier than inspection output; it previously discarded the const-join body before the later retarget pass chose it. `SsaEmissionReachability` now retains possible const-helper targets and their dependency closure using the existing specializer's target map. Tests cover both pruning configurations. At LLVM emission, private bodies keep the source identity for typed records, scalar value facts, ranges, locations and call contracts, and reuse the original ABI shape under a private symbol. Discarded output initialization is not advertised to LLVM for affected payload parameters; unaffected parameter facts remain intact. No package schema or public API changes are needed.

The final executable returns success, has **zero benchmark payload stores and no join/copy calls**, and retains the checked 64-byte initial reserve and real free. Its checksum loop is vectorized. Allocation-failure injection on Linux denies `mmap` after the loader has initialized: both the old and new Stark binaries return **1**, with empty output; C returns **0** because its optimized benchmark does not allocate. This confirms that the failure path was not erased. The diagnostic filter affects only those child processes and is not compiler/runtime policy.

Fresh comparisons use the harness flags: C `-O3 -DNDEBUG -std=c17`; Rust `-C opt-level=3 -C debug-assertions=no -C overflow-checks=no`. Rust IR and linking use separate invocations. Final code has these shapes:

| Implementation | Payload work | Remaining work |
| --- | --- | --- |
| C | Zero payload stores/copies | Vector checksum loop; no allocation |
| Rust, canonical linked build | Zero payload stores/copies | Benchmark body jumps to its no-allocation shim, which returns |
| Stark, liveness implementation | Zero payload stores/copies | Vector checksum loop, checked reserve and real cleanup |

At this payload-only stage, the host C loop executed one vector add per 40 source iterations, while Stark executed two per 20. The later nine-field literal-fact implementation above removes Stark's checksum loop. The payload proof alone did not solve that arithmetic difference or attribute the whole process-time gap to it. Allocator/failure work also differs, and process startup is included in the historical timing below.

After one warmup, 100 randomized interleaved CPU-0-pinned processes per binary all returned success:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark before payload liveness | 601.3 | 587.8 | 1.1262 |
| Stark with payload liveness | 595.9 | 584.6 | **1.1162** |
| C | 533.9 | 522.1 | **1.0000** |
| Rust | 752.7 | 741.8 | **1.4099** |

Stark/Rust is **0.7917**: Stark's mean is 20.8% lower in this sample. The before/after Stark mean differs by only 0.9%; min-to-max spreads divided by mean were 36.0% / 24.1% / 27.1% / 24.7% in table order. Do not claim a stable timing improvement from that small difference. This run includes startup and differs from the shell harness's timing method. No builds/tests from this chat overlapped it, but other-chat load was not acknowledged. The checkout included the other agent's queue commit and uncommitted compiler changes; raw samples include binary hashes, machine information and dirty status.

Validation: the final payload-proof suite passed **20 Release tests**, including direct stores, header-only slices, byte reads, raw escapes/offsets/slices, ambiguous aliases, exported boundaries, source-free generic package imports, and binary/inspection configurations. **255 SSA validation/optimization/function-semantics tests** cover the structured-origin contract, and the **five-test path/package standard-library selection** passed on the final compiler. Related value-fact/memory-region tests, **25 const/import-emission tests**, and **48 semantic/LLVM tests** also passed. The native executable checks every output byte through an opaque C observer, repeated/conditional reads, indirect callbacks, returned ownership, growth and overlapping views. All **10 Stark LLVM modules**, plus fresh C and Rust IR, verify. The rebuilt final Stark binary is byte-identical to the measured binary. No full release workflow or full benchmark suite was run.

The repository-wide stale-assumption audit checked address/slice producers and rewriters, common-subexpression keys, package producers/consumers, validators, fixtures, source/ported tests and documentation. Existing raw operations default to unknown origin; record-copy rewrites preserve origin automatically, and explicit reconstructions now carry it. Existing byte-reading and overlap assertions remain valid; historical retained-store claims above/below are labeled by build stage. `git diff --check` passes.

Evidence is in ignored `benchmarks/codegen/path-repeated-investigation/payload-proof/`: `stark-payload-dead`, `dead-temps/`, fresh `c-current`/`rust-current` binaries and IR, `*-main.asm`, `rust-current-body.asm`, `rust-current-shim.asm`, `measure-implementation.py`, `implementation-interleaved.json`, `fail-mapping.c`/`.so`, and `allocation-failure.json`. These are local diagnostic artifacts, not committed compiler dependencies. The broader C performance gap and general per-field/source-import fact-summary opportunities remain open; the 25,000-store issue is complete.

### Payload liveness and fact-preservation investigation before implementation (October 28, 2026)

**Historical snapshot before the implementation above:** the prerequisite fixes repaired loss of existing storage facts and an unsound memory-region summary, but did not themselves remove the payload writes. The following controls motivated the implementation; they must not be confused with its production results.

The fresh C and Rust builds and the Stark build with the compiler fixes returned success. Their final linked code has these shapes:

| Implementation | Payload work | Other benchmark work |
| --- | --- | --- |
| C, harness O3 flags | No payload stores, copies, allocation or scans | Vectorized checksum loop |
| Rust, harness link-only invocation | No payload stores, copies or allocation | Benchmark body jumps to its no-allocation shim, which returns |
| Stark, prerequisite-fix build | Five payload stores per iteration, 25,000 total; no external copies | Scalar checksum loop, checked initial reserve and real cleanup |

The Rust LLVM-only build contains a vectorized checksum loop. Requesting LLVM IR and linking in the **same** rustc invocation changes the linked result in this toolchain; use separate invocations and inspect the canonical executable as well as IR. The new fact fixes do not establish a runtime improvement. The last measured production ratios remain Stark **1.0219**, C **1.0000**, Rust **1.0161** in the shell harness above; its spreads exceed those gaps. No new timings are attributed to the payload controls.

**Concrete fact loss repaired:** the initial `value-facts` and dynamic-storage analyses received semantic direct-call parameter effects, but nine later recomputations constructed `SsaValueFactAnalyzer` without that context. A read-only call could therefore erase an exact capacity of eight and its backing-allocation identity; after later shaping/folding, the observed capacity bound fell to one. Every default-pipeline recomputation now uses a shared helper with the same authoritative local/package/specialization call effects. Focused tests retain exact capacity, length and backing identity through read-only calls and both branches, and through a package-image boundary. Mutating, opaque, indirect and possible-growth paths still invalidate the affected facts. The tests inspect the resulting fact model, not implementation text.

There is a separate remaining producer gap: `SsaDynamicStorageCallFactPolicy.BuildDirectCallParameterEffects` consumes local and package-image summaries, but does not obtain equivalent precise parameter summaries for source-only imported bodies. A source-import control stayed conservative even after the recomputation fix. That missing producer is not fixed by preserving the context already available; do not claim source-import precision is solved.

**Memory-region correctness repaired:** reading `header.Length` or loading the value of `header.Data` only reads the header. Dereferencing the loaded `Data`, indexing a dynamic owner's elements, or forwarding that loaded/derived pointer to a reader can access memory outside LLVM's `argmem` region for the header argument. Previously `Read(borrow Header h) { return *h.Data; }` was emitted with `memory(argmem: read)`. In a native control with separately optimized caller and reader objects, LLVM deleted a required payload update and the program returned failure. The same full-module compilation happened to pass, masking the defect. The corrected reader emits `memory(read)`, and the separated native control returns success. Local headers reconstructed from pointer fields also retain the reachable-memory effect.

The semantic validator now carries the distinction through field/index/dereference/address/cast operations and direct-call argument effects. It uses the existing `ReadsOtherMemory` / `WritesOtherMemory` summary fields and their existing package serialization, without a second schema. Tests distinguish shallow reads, direct pointer arguments and deep payload reads/writes, and verify the summary reaches a package consumer. A loaded pointer value alone is neither a payload read nor proof of no escape; returned views, unknown/indirect calls and retained pointers still require separate liveness and alias proofs.

**A working LLVM removal mechanism:** six private-kernel controls retain the actual checked allocator, allocation-failure return and runtime cleanup, and preserve the original loop's existing `mustprogress` metadata. Only the private payload computation is outlined; the cleanup is outside its lifetime boundary. LLVM's `dead_on_return(64)` on that kernel's payload argument means writes in those 64 bytes are dead when it returns. The checked-out LLVM O3/ThinLTO pipeline uses that fact as follows:

| Private-kernel control | Payload writes after optimization |
| --- | --- |
| No dead-on-return proof | Retained |
| Proven dead on return, no observer | Removed; final executable has no payload kernel/loop |
| Proven dead on return, opaque scalar length observer | Removed |
| Proven dead on return, opaque payload-byte reader | Retained; reader validates all 20 output bytes |
| Proven dead on return, opaque header observer with general read effects | Retained |
| Proven dead on return, header-only observer with precise argument-only read effects | Removed; header initialization remains |

These controls demonstrate both the removal and the need to preserve header-versus-payload effects. Their opaque observers are native C objects compiled separately without LTO. All six executables return success. They assert the private scope's liveness manually; they do **not** prove that the current compiler can infer it from arbitrary source or that every observer can receive a shallow memory contract.

The concrete next compiler step is to derive a bounded backing-region liveness proof from ownership, aliases, initialized ranges, call effects and cleanup, keeping the owner header independent. Consume it either in a general SSA dead-payload transformation or at a proven private payload scope with a bounded LLVM contract like this control. Stark already supports explicit whole-parameter `where dead_on_return(...)` contracts and their package/LLVM transport; reuse that authoritative machinery where applicable rather than inventing a duplicate annotation. The missing work is inference and a suitable bounded payload scope, not discovering a new LLVM attribute. Preserve reserve failure, status and scalar length computation; invalidate the proof on payload observers, escaping/returned views, unknown aliases, indirect effects and reallocations. Prove both incoming paths at merges. A raw pointer-backed aggregate such as Queue's storage needs its own supported region provenance; the new dynamic-storage fact preservation does not automatically supply it.

Do not put `dead_on_return` on the runtime free helper: it publishes a freelist link in the freed payload, and poisoning that link would invalidate the cache. Do not use `llvm.lifetime.end` for this heap pointer: the checked-out LLVM contract limits it to supported alloca storage. Do not replace the runtime allocator with a generic malloc/free contract. The private scope control deliberately ends before actual cleanup and leaves allocation metadata/cache operations intact.

Local evidence is in ignored `benchmarks/codegen/path-repeated-investigation/payload-proof/`: `build-controls.py`, the six control `.ll` / `*-optimized.ll` files and executables, C/Rust IR and binaries, `current.ssa`, `stark-final`, `final-temps/`, and the before/after `header-read-*-separated` native controls. These artifacts are not committed and may be absent elsewhere. Reproduce the six controls with `python3 benchmarks/codegen/path-repeated-investigation/payload-proof/build-controls.py` on this checkout. They are diagnostic experiments, not a new compiler workflow.

Validation after the final compiler edits: **36 Release pipeline tests**, including the new fact-preservation and memory-region/package tests plus related value-fact/import tests; **48 Release semantic/LLVM tests** for function effects, memory attributes, pointer calls, large-value ABI and assembly bridges; and **four Release standard-library tests** for packaged path byte correctness/overlap, tail-region copies, Windows separator lowering and imported memory contracts all passed. The final benchmark returned success and all **10 emitted LLVM modules** verified. All six LLVM controls and the separated fixed native reader returned success. Repository-wide searches covered fact-analyzer construction, call invalidation, memory-summary fields, LLVM memory attributes, package consumers, C#/ported Stark tests, documentation and benchmarks; the pipeline documentation's stale claim that observation itself invalidates owner facts was corrected. No release workflow or full benchmark suite was run.

### Validation and reproduction

For extensions to this proof, maintain observable-behavior tests for byte-reading and returned/escaped views, unknown and indirect calls, overlapping inputs, insufficient capacity and failed allocation, plus target separator behavior and cleanup. Keep a length-only case and inspect the private SSA plan and final linked code to verify that joins, scans and payload writes are actually gone. Current checks include `CompilerPipelinePayloadLivenessTests`, `SystemIOPathPayloadLivenessTests`, `ConstPathSpecializationHandlesImportedOverloadsWithoutDiscardingMutableInputs` and `SourceStdLibPromotedPathCorrectnessExecutableRuns`. Re-run the directly affected checks after further changes, and perform the repository-wide stale-assumption audit required by `AGENTS.md`.

On this checkout the SDK and Rust toolchain are in ignored `benchmarks/toolchains/`. The following reproduces the last focused harness run; it performs one warmup and **100 measured process executions per language**. The local NuGet cache settings avoid a network restore in this environment:

```bash
PATH="$PWD/benchmarks/toolchains/dotnet:$PWD/benchmarks/toolchains/rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin:$PATH" \
DOTNET_CLI_HOME=/tmp/stark-dotnet-home \
NUGET_PACKAGES="$PWD/benchmarks/toolchains/nuget" NuGetAudit=false \
STARK_BENCH_RUNS=100 STARK_BENCH_FILTER=PathRepeatedSmallOps \
STARK_BENCH_LANGUAGES=stark,c,rust scripts/run-benchmarks.sh
```

Report ratios with C = 1.000, retain the CSV and machine metadata, and describe the fixed language order, whole-process timing and thermal variation. Do not compare the machine's numbers directly with the earlier Ryzen 7 9800X3D results. The full six-target release workflow is an expensive final integration check; do not use it in place of focused compiler and runtime validation.

## ConsoleWrites investigation (October 28, 2026)

**Status: completed and closed (October 29, 2026, at the user's request).** The combined implementation and matched buffered benchmark are validated; the existing immediate API retains its behavior.

**Combined implementation completed (October 29):** private scratch arrays, general constant UTF-8 console pre-encoding and the explicit `BufferedUtf8Writer` are implemented and validated. Existing immediate console APIs retain their behavior. Both 1,000-round interleaved timing windows have finished; results are below.

**Follow-up investigation complete; the combined implementation is recorded below.** Isolated SDK/source controls tested scratch storage, syscall memory-read annotations, constant UTF-8 output and explicit buffering. The two diagnostic timing windows are finished. At that investigation stage no production console/compiler source had changed; the completed implementation follows below.

The older full suite ranked `ConsoleWrites` at Stark **1.0480**, C **1.0000**, Rust **1.0703**. The current direct-process comparison below differs from that shell-harness measurement and cannot be compared numerically with it. One warmup and 1,000 randomized interleaved rounds per variant ran on CPU 0. Each child wrote stdout to `/dev/null` and stderr to a separate truncated regular file, matching the destinations used by the canonical harness. All executions succeeded. Means include process startup; the host is passively cooled and temperature capped, and no exclusive timing window was established. The build used the existing Release compiler binary and shared standard-library sources including uncommitted changes.

| Variant | Mean / median µs | Mean / C | Max–min spread |
| --- | ---: | ---: | ---: |
| Stark | 750.1 / 726.9 | **1.251** | 55.8% |
| Stark diagnostic: skip three buffer-zeroing calls | 731.8 / 709.0 | **1.221** | 44.5% |
| C | 599.4 / 580.6 | **1.000** | 52.8% |
| C diagnostic: unbuffered stdout, original UTF-8 text | 730.2 / 707.2 | **1.218** | 69.3% |
| C diagnostic: unbuffered stdout, Stark's ASCII text | 728.0 / 706.5 | **1.214** | 65.4% |
| Rust | 956.4 / 930.1 | **1.595** | 226.5% |

The first independent 1,000-round run produced Stark **1.257**, zeroing diagnostic **1.230**, C **1.000**, ASCII unbuffered C **1.224**, and Rust **1.576**. Per-round samples, binaries, scripts, hashes, C/Rust IR and Stark disassembly are retained under ignored `benchmarks/codegen/console-writes-investigation/`. These are local controls, not production changes. At this stage a syscall trace was denied by the sandbox, so the initial syscall counts were predictions. The follow-up below obtained child-process traces and verifies the counts.

The sources have different outputs and buffering: Stark prints `wide a`, while C and Rust print `wide α`; normal captured stdout is 3,200 bytes for Stark and 3,264 for C/Rust, and each produces 256 identical stderr bytes. The two C unbuffered controls isolate that byte difference from changing stdout buffering: their means are close. Clang's optimized IR calls `fputs` five times per iteration and `fflush` at the end; this host's redirected stdout is buffered. Changing only C's stdout buffering moves it much closer to Stark, strong evidence that buffering materially affects this comparison, though it also changes when errors can surface and cannot be treated as a semantics-preserving Stark optimization. Rust's generated IR calls `StdoutLock::write_all` and `StderrLock::write_all`; the checked-out Rust `library/std/src/io/stdio.rs` uses a `LineWriter` for stdout. It performs different buffering and startup work, so its longer process time is not an isolated compiler comparison.

The Stark executable calls direct syscall-backed console writes and checks each result; absent short writes its five source operations entail five writes per iteration. The line paths combine content and newline already. In `main`, the fixed-buffer line's short path clears **8,208 bytes** and copies its payload before writing. The out-of-line owned-ASCII and stderr-ASCII line helpers clear approximately **8,203** and **8,205** bytes for their respective lengths. Thus these three operations clear about **1.58 MB** across 64 iterations, mostly outside the written ranges. Separately, the Unicode line uses the UTF-8 conversion path for a compile-time ASCII-range literal. Clang's `fputs` leaves buffering to libc, and Rust retains its library calls; neither emits this Stark buffer-clearing sequence in its benchmark body.

**Concrete opportunities:**

1. Prove that the unused part of a fresh temporary line buffer is never observed, including through platform calls, retries, errors and aliases, then omit only initialization of those dead bytes. A diagnostic machine-code patch suppressed the three identified `memset` calls; its successful stdout/stderr were byte-identical to Stark's original. The independent 1,000-round runs improved the mean by **16.1 µs** and **18.4 µs** respectively. This demonstrates potential, not a correctness proof or production implementation; do not remove zero initialization when any code may observe it. Investigate the broad memory effects of syscall inline assembly and how initialized-range facts reach the final LLVM optimizer before choosing the implementation.
2. Investigate a general compile-time Unicode-literal-to-UTF-8 specialization for console writes, with the generic runtime converter retained for runtime strings and correct replacement behavior. The generated benchmark uses the runtime Unicode path; no isolated performance control has yet established the size of this opportunity. It must apply to all eligible constants, including non-ASCII and invalid-code-point behavior, rather than naming benchmark strings.
3. If buffered console output is wanted, offer it explicitly with defined flush, error, ordering and concurrency behavior. Do not silently defer the current per-call write/error behavior. The C `setvbuf` control demonstrates a tradeoff, not a drop-in compiler optimization. For fair throughput comparison, first align the `wide` contents and buffering/error contract across the benchmark implementations; retain the existing direct-write workload as a separate scenario if useful.

### Follow-up: remove unnecessary construction before pursuing general dead-store elimination

**Result:** two randomized, interleaved 1,000-round runs reproduce the gap and isolate a straightforward general library change. Replacing the short-line temporary `FixedByteBuffer8192` object with an uninitialized stack byte array eliminates all three 8 KB clears, without discarding initialization facts or changing immediate writes. Adding a diagnostic pre-encoded constant Unicode line removes the fourth, 4 KB clear and runtime UTF-8 conversion. Together these controls are approximately level with C using direct writes. Buffered C still has fewer syscalls.

| Variant | Run 1 mean / median µs | Run 1 / C | Run 2 / C |
| --- | ---: | ---: | ---: |
| C, original buffered stdout | 586.6 / 571.8 | **1.000** | **1.000** |
| C, buffered stdout, Stark's ASCII text | 587.2 / 571.5 | 1.001 | 1.003 |
| Stark, current source | 735.0 / 717.7 | **1.253** | **1.259** |
| Stark, scratch-array SDK control | 714.7 / 697.6 | **1.218** | **1.221** |
| Stark, scratch array + pre-encoded line control | 699.3 / 679.8 | **1.192** | **1.190** |
| C, direct `write`/short-write retry control, matching ASCII output | 692.6 / 677.2 | **1.181** | **1.190** |
| C, unbuffered stdio, matching ASCII output | 716.9 / 698.7 | 1.222 | 1.226 |
| Stark, bounded read-only asm-memory diagnostic | 738.8 / 719.0 | 1.260 | 1.254 |
| Stark, explicitly batched, pre-encoded stdout control | 560.7 / 548.0 | **0.956** | **0.958** |
| Rust, original line-buffered stdout | 934.6 / 913.7 | **1.593** | **1.597** |

The combined immediate-write Stark control is **1.010 / 1.001 relative to direct-write C**, a difference too small to distinguish reliably on this host. It reduces Stark's measured mean by 4.9% / 5.5% and recovers approximately 24% / 27% of the original absolute gap to buffered C. The remaining approximately 19% relative to buffered C closely tracks the different output contract. The batched control is a throughput ceiling, not an interchangeable implementation: it appends a pre-encoded 50-byte row per iteration to an existing `FixedByteBuffer4096`, writes stderr immediately, and flushes stdout once at the end. It delays stdout errors and changes cross-stream visibility; it also removes conversion and individual console dispatch work. Its small lead over C does not establish a general language advantage.

Both runs pinned the driver and children to CPU 0, warmed each variant once, then randomized ten variants per round with different seeds. Stdout went to `/dev/null`; each variant had its own regular stderr file, truncated before each timed process. All 20,000 measured executions succeeded; binary hashes were unchanged across each run. Process startup is included. Max–min spreads across variants were 38–59% in run 1 and 62–90% in run 2. Compilation and validation finished before timing, and a coordination note requested no overlapping runs; no exclusive host access was established. The passive cooling/temperature cap and possible unrelated activity remain limitations. The Release compiler hash, SDK snapshots, source controls, saved LLVM IR, disassembly, trace/fault scripts, exact outputs, binary hashes and raw samples are retained in ignored `benchmarks/codegen/console-writes-followup-20261028/`; these controls are not production changes. The checkout HEAD was `87d9d07` with unrelated tracker/installer work present; the recorded DLL hash identifies the actual Release compiler.

**Actual work, traced:** original Stark and both immediate-write source controls make **256 stdout + 64 stderr = 320 writes**. C makes **1 stdout + 64 stderr = 65 writes**. Rust makes **192 stdout + 64 stderr = 256 writes**; its checked-out standard library constructs stdout with `LineWriter`, combining `small` and the following newline-terminated text. Stark's explicit batching control makes **1 stdout + 64 stderr = 65 writes**. C/Rust still print `wide α`, Stark `wide a`; the C ASCII control confirms that this byte difference has negligible effect here. Rust also has more startup/runtime syscalls, so fewer writes alone do not predict direct-process time.

**Why the source change works:** `System.Runtime.Platform.Linux.WriteBufferLineToHandle` currently constructs a zero-initialized 8,208-byte object, copies `length` bytes, writes LF at `length`, and exposes only `length + 1` bytes. Neither cursor field is used. The control substitutes `stack mut i8[min max][8192] lineStorage;` and retains the same checks, copy-before-write snapshot, retry/error paths, and `length >= 8192` fallback. An explicit `(u16[1 8192])(length + 1)` carries the bound into the bounded-pointer call; the existing broad integer cast loses that upper bound despite the preceding `length < 8192` branch. Every byte the syscall reads has been assigned, so the tail needs no initialization. This is specific to the private scratch use, not permission to remove observable zero initialization from public buffer constructors. The analogous macOS/Windows helpers need their own platform validation before a portable production change.

Disassembly of the fresh baseline confirms the three line-buffer clears total approximately **1.58 MB per process**, and also finds a **4,112-byte Unicode buffer clear on every iteration**, another **263,168 bytes**. The scratch control removes the former; the combined control removes both, leaving only setup initialization outside the loop. The Unicode control replaces this one constant `WriteLine(unicode)` in the benchmark source with a direct pre-encoded ASCII-byte `Write` including LF; this is a diagnostic, not an implemented general compiler transform and not a string-specific standard-library branch.

**Facts and compiler comparison:** a bounded `memory(read(buffer))` syscall control reaches LLVM as `memory(argmem: read)` without `~{memory}`, but retains the clears and gives no measurable benefit. The call still lacks a maximum read extent usable for dead-tail elimination: a pointer plus an integer operand is not itself a byte-read proof. Do not ship this narrowed syscall contract without auditing all effects, including writes through aliased file mappings; a globally read-only syscall assumption is not a substitute for local scratch liveness. A Clang control likewise retains an 8 KB zero tail for `write(fd, buffer, 15)` and for GNU inline asm, even with a `[15 x i8]` memory operand. LLVM's checked-out DSE uses alias/mod-ref information to decide whether intervening reads observe a store. C's actual benchmark avoids the question by passing pre-existing UTF-8 literals straight to stdio; Rust also starts with UTF-8 bytes. The practical first fix is to avoid constructing the unnecessary zero-filled object, while retaining correct broad I/O effects.

**Validation of the scratch control:** baseline/control stdout and stderr match independently generated expected bytes for lengths **0, 1, 2, 31, 32, 4095, 4096, 8190, 8191, 8192, 8193, 16385**, including embedded NUL/high bytes and a separate non-ASCII/astral Unicode line. A ptrace injector limited actual writes to three bytes while preserving input registers across the syscall; all **38,296** writes/retries reproduced the complete expected output. Error and zero-return injection during stdout and stderr retries produced identical partial output and failure statuses in baseline/control. Closed stdout and `/dev/full` also returned the same error status. All normal benchmark controls produced their expected bytes; generated LLVM modules for all timed Stark controls passed `opt -passes=verify`.

**Recommended implementation order:**

1. Make the private short-line scratch-array change, carry the proven `length + 1 <= 8192` bound, and preserve the existing copy snapshot, syscall boundaries, retry/error behavior and long-line fallback. Port and validate the equivalent platform helpers. Windows already keeps lines below 512 bytes in a small helper and the 8 KB path behind `noinline`; adapting that existing split on POSIX could also reduce hot stack-frame size, independently of removing clears. That additional gain has not been measured here. This applies to arbitrary runtime byte/text lines, not benchmark literals, and avoids needing a broad compiler proof for the initial gain.
2. Extend the existing constant-provenance/static-array machinery to **general UTF-8 console pre-encoding**, including LF for eligible constant lines. Reuse the first-class target-correct read-only byte arrays across source/package boundaries. Start with a proven single existing output chunk (`encoded bytes including LF <= 4096` on this POSIX Unicode path); preserve normalization to U+FFFD, embedded NULs, dynamic fallback, error propagation and platform behavior. Longer constants must preserve the original encoder's chunk boundaries and failure points. Windows console handles must retain their Unicode-console semantics. The measured control shows opportunity; arbitrary non-ASCII/package/dynamic regression tests are still required for a real implementation. Constant ASCII lines can similarly pool their bytes plus LF, removing the copy and large scratch frame.
3. If buffered-C throughput is the goal, use an **explicit buffered writer/batch API** with documented flush, failure, ordering and synchronization semantics. Do not silently buffer today's immediate console operations or merge across observable error checks. Keep matched immediate-write and buffered benchmarks so the comparison does not conflate compiler quality with different I/O contracts.

### Combined implementation (October 29, 2026)

**Status: completed.** All three changes are implemented together; the performance measurements below use the real API and compiler optimization, rather than a hard-coded pre-encoded row.

- Linux/macOS short-line helpers and both Windows small/large helpers use private uninitialized byte arrays. They copy the complete output prefix and append LF before exposing that prefix to I/O. The narrowed `length + 1` bound remains explicit; existing null checks, short-write/error handling, copy snapshots and long-line boundaries remain intact. Public fixed-buffer construction still initializes its storage normally. Syscall memory effects remain conservative.
- `SsaConstStdlibHelperSpecializer.Console.cs` reuses immutable text provenance, exact payload/slice facts and first-class target-typed static arrays for constant UTF-8 output. ASCII byte slices retain their exact bytes; Unicode is encoded by scalar with U+FFFD normalization. Receiver/indirect-argument facts and return types are retained when retargeting calls, including calls that discard their status. The existing package/helper-reachability machinery retains the required bodies; no package schema change is needed. Immediate POSIX specialization applies only within the original single-chunk bounds (Unicode at most 4,096 encoded bytes, ASCII lines at most 8,192 including LF). Longer/dynamic text keeps its original path. Windows immediate text keeps its console-aware path. Explicit buffered UTF-8 output can use pooled constant bytes on every target.
- `System.Console.BufferedUtf8Writer` owns a 4,096-byte buffer; `new()` targets stdout and `new(true)` stderr. It accepts ASCII, Unicode and borrowed byte slices via `Write`/`WriteLine`, reports `PendingBytes`, and requires checked explicit `Flush`. It emits the same raw byte sink as the existing byte-slice console overload. Unicode runtime encoding reuses the existing UTF-8 scalar encoder, now an internal `unsafe finite` helper with a `finite law` normalizer. Errors are sticky: subsequent operations return the failure without replaying a prefix that may already have been delivered. There is no destructor flush; callers retain one logical owner and synchronize shared destinations externally. The API and failure/ordering contract are documented in `docs/StandardLibrary/System.Console.md`.

A separate matched `console/ConsoleWritesBuffered` Stark/C/Rust trio preserves the individual logical operations, owned-text and byte-buffer inputs, immediate stderr and explicit final stdout flush. All three produce **3,264 stdout bytes and 256 stderr bytes**, including the same Greek alpha. Stark uses the new writer and real compiler pre-encoding; C uses buffered stdio; Rust uses a 4,096-byte `BufWriter`. The existing immediate benchmark remains separate. There are no checks for benchmark string contents in the library/compiler.

**Validation:** Release compiler build passed. **30** focused UTF-8/UTF-16 compiler tests passed, covering arbitrary strings, NULs, supplementary scalars, copied values, raw byte/scalar slices, discarded statuses, chunk limits, dynamic fallback and Windows immediate semantics. **6** focused standard-library tests passed: Linux/macOS/Windows LLVM compilation, existing line-coalescing behavior, and native source/source-free-package consumers. The latter exercise a real dynamic invalid-scalar Unicode path, automatic buffering beyond 8 KB, empty/repeated flush, explicit no-flush dropping, sticky closed-output failures, stderr, and immediate lengths **0, 1, 511, 512, 8191, 8192, 8193**. Their source-free SSA also retains static byte-array facts for constants while the noinline dynamic routine retains its runtime call. The existing eight other console checks passed in the initial focused run; a superseded assertion requiring the removed temporary object was removed, and its corrected test passed. All saved implementation LLVM modules passed verification. Native runtime validation was on Linux; macOS/Windows were compiled to LLVM, not executed on this host.

Own-child syscall traces verify **65 writes** for the real buffered Stark, C and Rust programs (**64 stderr + 1 stdout**), versus **320** for immediate Stark. Injecting three-byte short writes into buffered Stark produces all expected bytes in **1,216 writes/retries**. Injecting an error or zero return after the first 97 bytes of the final stdout flush returns the checked failure with exactly that output prefix and no duplicate delivery. Native source/package tests separately verify subsequent writes/flushes stay failed. Disassembly of immediate Stark contains only setup clearing outside the iteration loop; buffered Stark adds one buffer initialization outside the loop. The constant strings/newlines are pooled byte arrays, with no runtime Unicode conversion for those calls.

**Two 1,000-round runs with C = 1.000:**

| Variant | Run 1 mean / median µs | Run 1 / C | Run 2 mean / median µs | Run 2 / C |
| --- | ---: | ---: | ---: | ---: |
| C, buffered stdio | 586.7 / 571.0 | **1.000** | 585.4 / 571.9 | **1.000** |
| Stark, combined buffered implementation | 563.9 / 547.1 | **0.961** | 564.9 / 550.2 | **0.965** |
| Rust, buffered writer | 807.0 / 781.5 | **1.375** | 801.1 / 781.9 | **1.369** |
| Stark, current immediate API | 696.3 / 677.5 | 1.187 | 694.7 / 678.1 | 1.187 |
| C, direct-write diagnostic | 699.1 / 676.8 | 1.191 | 694.5 / 679.5 | 1.186 |
| Stark, preserved pre-change immediate executable | 741.0 / 717.5 | 1.263 | 736.3 / 718.2 | 1.258 |
| Rust, original immediate/line-buffered API | 913.5 / 887.2 | 1.557 | 911.5 / 889.6 | 1.557 |

The combined implementation takes approximately **0.70 times buffered Rust's mean time** in both samples. Immediate Stark is **0.996 / 1.000 relative to direct-write C**. The buffered sample's small lead over C is local evidence, not a general performance guarantee.

The driver pinned itself/children to CPU 0, warmed each executable once, and randomized seven variants per round using different seeds. All **14,000 measured processes** succeeded; executable hashes were stable. Stdout was `/dev/null`; each variant's stderr was a separate regular file truncated before timing. Builds/tests finished before timing and a tracker note requested a quiet interval; exclusive host access was not established. Startup is included. Spreads ranged **58–106%** in the first run and **41–171%** in the second; the passively cooled/temperature-capped shared host still limits precision. The matched buffered trio has identical output; preserved immediate Stark/direct-C controls retain the old ASCII `wide a` while original C/Rust use alpha, as documented in the investigation.

Reproducible scripts, exact binaries/hashes, raw samples, compiler/CPU metadata, IR/disassembly, native fault traces and final test logs are under ignored `benchmarks/codegen/console-combined-implementation-20261029/`. The benchmark is closed at the user's request; unrelated installer/tracker work remains untouched.

## PathNormalize investigation (October 29, 2026)

**Fresh code check:** the current Stark executable eliminates both normalizations, all 5,000 iterations, payload writes and checksum work. Its `main` retains only the initial fallible 64-byte allocation, failure result and cleanup. Clang reduces C to `return 0`; Rust also eliminates normalization/payload/allocation work. Therefore the historical 1.0263 × C ratio is not a current normalization optimization target. The remaining allocator issue is already documented under PathRepeatedSmallOps/PathJoin; do not reintroduce scan/copy work or discard allocation failure merely to match C. Fresh saved IR and disassembly are under ignored `benchmarks/codegen/path-normalize-investigation-20261029/`.

## DictionaryLookup investigation (October 29, 2026)

**Status: completed at the user’s request (October 31, 2026).** The fact-preserving lookup/cleanup implementation and production branch placement are validated. The final source-built executable averages **0.984 × C** and **0.376 × Rust** over 1,000 randomized interleaved runs, with a 3.2% lower mean than the matched previous placement. No build/test/timing window is active. Benchmark sources are unchanged. The remaining capacity/power-of-two proof is deferred as an optional future optimization and does not keep this item open.

### Production implementation and final comparison

1. **First-bucket lookup:** `Dictionary.FindIndex` tests the home bucket before group setup and returns immediately on a hit or empty bucket. A collision continues from the next bucket, counting the bucket already tested, so equality is not repeated. The existing grouped fallback is outlined into `internal noinline finite law FindIndexAfterHome`; both lookup helpers retain their read-only, guaranteed-return contract and `overlap(self, key)` allowance. This is independent of key contents, hash implementation and benchmark sizes.
2. **Private native counter with preserved values:** `SsaLoopCounterWidening` now recognizes the guarded, multi-block unsigned 24-bit induction despite unrelated memory operations and data uses. It proves zero initialization, unit increment, a representable positive bound and that the failed guard cannot reach the increment without another header visit. The new 32-bit induction retains the proven `[0, limit]` range. Explicit source-width conversions keep data/call/exit values and ABI/layout at 24 bits. Address-mutated counters retain their original semantics. The actual benchmark's per-iteration `0xffffff` counter mask is gone; its source declaration remains `u24[0 100000]`.
3. **Capacity facts retained through storage work:** both shared capacity planners are `inline finite` (their output mutation rules out `law`), and the hash planner retains its checked `[0, 2**62]` target range. `Reserve` separates planning from the cold `GrowStorage` allocation/rehash worker and commits the planned capacity and preserved count explicitly. `Set` uses the same authoritative planner, retains its count/capacity locals across sparse payload writes and growth, returns error statuses intact, and commits the planned header. The unused mutable dictionary receiver was removed from the static insertion worker. This avoids treating payload insertion as an arbitrary header mutation and avoids the former half-full boundary's redundant reserve call. Allocation/error/drop behavior remains tested. **The final lookup still masks its bounded key with the runtime capacity mask:** these source facts do not yet prove the insertion loop never grows or retain the required power-of-two lower bits through that loop. No unsupported unchanged-capacity assumption or benchmark-specific rule was added.

**Related correctness repair:** wrapped collision regression cases exposed an existing shared grouped-probe bug. Subtract-based byte matching can mark an empty lane immediately after an occupied lane as another occupied candidate. Lookup/insertion previously stopped only *after* the first empty lane, permitting a comparison against uninitialized key storage and a false existing-key result (reproduced with key zero on the preserved source). All eight Dictionary/HashSet consumers now stop **at or before** the first empty lane. The shared mask algorithm remains unchanged.

**Generated-code check:** the final hot path has a native counter, home-state/key comparison and value read; SWAR/trailing-zero work runs only after a home collision. The first production build measured **1.072 × C**: its large inline collision fallback forced a values-pointer reload on every successful lookup. Outlining that fallback recovered the lost register allocation. A separate 1,000-round interleaved diagnostic measured inline Stark **1.074 × C**, outlined Stark **1.022 × C**, and the previous combined control **1.015 × C**. The final table below is a fresh measurement of the accepted production build, not that diagnostic.

**Final pooled averages:** two equally sized randomized, interleaved **1,000-round** runs, **2,000 samples per executable**, pinned to CPU 0. All **8,000 measured processes** passed the existing checksum; no samples were removed and binary hashes stayed fixed. C and Rust were freshly compiled with the harness flags. The before row uses the preserved original executable, whose hash matches the earlier investigation. Each batch warms every executable once. Stdout is `/dev/null`; stderr uses separately truncated regular files. No builds/tests from this chat overlapped sampling; exclusive host use was not confirmed. Process startup, passive cooling, temperature capping and shared-host activity remain included.

| Variant | Pooled mean µs | Mean / C |
| --- | ---: | ---: |
| C | 641.1 | **1.000** |
| Stark, before | 731.1 | **1.140** |
| Stark, implemented | 653.1 | **1.019** |
| Rust, standard HashMap | 1668.0 | **2.602** |

Implemented Stark / before = **0.8934**, a **10.7%** lower mean. Implemented Stark / Rust = **0.3916**. The residual Stark–C difference is **1.9%** in this window. These workloads still differ: C uses a fixed-capacity identity-hashed integer table; Rust uses standard randomized HashMap; Stark retains general dictionary allocation, insertion and cleanup behavior. The Rust difference cannot be assigned to compiler quality alone.

**Validation:** **29 focused Release tests passed** (9 counter proof/native checks and 20 library checks). Native source/package cases cover empty/home hits, arbitrary wrapped collisions, missing keys inside/outside clusters, zero keys, custom Hash/Equals, overlapping key/output, removal/backward shifting, update counts, growth/reservation boundaries and overflow failure preserving contents/header. Existing source-free package checks cover owned-value move/drop and text-key collections; directly affected HashSet, cross-family and reserve-lowering checks pass. Both the full collection source and the changed Stark port module pass source checking. The final Stark, C and Rust LLVM modules verify. No full release workflow was run. The repository-wide stale-assumption audit covered counter consumers/ABI, insertion receivers, capacity helpers and errors, grouped-mask consumers, codegen fixtures, package tests, Stark ports, benchmarks and documentation. Sparse-storage assertions now inspect the allocation worker; grouped-probe assertions inspect the outlined fallback. Other-agent tracker sections and the unrelated installer were preserved.

Evidence is under ignored `benchmarks/codegen/dictionary-lookup-implementation-20261029/`: final/before/C/Rust binaries, final saved LLVM IR, disassembly, exact production/compiler/benchmark source snapshots, `fixture.json` hashes/tool flags, `measure.py`, both `interleaved-1000-*.json` raw files and `pooled-summary.json`. The initial production and outlining diagnostics are retained separately. **Historical follow-up:** a general insertion-loop/capacity proof to remove the lookup mask was left open at this stage. The benchmark was subsequently completed on October 31; that proof remains an optional future optimization.

### Combined follow-up implementation (October 30, 2026)

**Status: all three requested changes implemented and validated.** The 1,000-round timing window is finished; no builds/tests/timing from this chat remain active. These changes are included with the accepted October 31 production implementation. The capacity-mask proof remains an optional future opportunity; the benchmark sources are unchanged. Other-agent PathJoin/QueueDequeue sections and the installer are preserved.

**Borrowed alignment and referent facts:** the indirect-parameter alignment producer now queries the borrowed value's target layout rather than the pointer representation. Parameter/local address resolution retains that fact through scalar dictionary-key materialization. The resolver now returns both the actual referent address and its alignment. A copied local borrow previously caused the builtin to read the pointer slot itself as a u32 key; it now resolves/loads the pointee instead. This related failure was reproduced in a first-emission test and is covered by a native comparison whose actual values are 42 (rather than aligned pointer bits). The public ABI and integer storage widths are unchanged. Emission tests cover u8/u16/u24/u32/u64/u128 and a local borrow alias on x86-64, i686 and AArch64; they compare load alignment with the published referent contract, including retaining u128 alignment 16 on applicable targets. The emission lint also rejects compiler-materialized dictionary-key loads that overstate a direct parameter's referent alignment, including byte-aligned borrows. LLVM's standard argument promotion now gives the collision helper an i32 key and removes the benchmark's 100,000 key-slot stores.

**Direct home-hit success:** `TryGet` reads the home state/key and returns the value immediately on a match, preserving the proven in-range success edge. Empty tables/buckets return false; collisions advance once and use the existing outlined grouped helper, retaining its sentinel check. It does not repeat the first key comparison. Key/output overlap, custom hash/equality, wrapped clusters, missing/zero keys, deletion and growth still pass. The final home-hit edge skips the extra index/capacity comparison.

**General no-drop eligibility:** `comptime System.Compiler.IsCopyable<T>()` exposes the existing shared `CopyabilityFacts` proof to source and imported typed-template evaluation. No second lifecycle predicate was added. True guarantees a byte-safe copy with no drop obligation; the existing analysis recursively checks arrays and concrete struct/record/enum fields, excludes destructors and owned storage, and is conservative on unresolved generics/borrows. Its resolver supports source and loaded-package named types; existing ownership/doctrine users retain the same classification. `Dictionary.Clear` uses that proof for both K and V before zeroing states in a bounded loop, which lowers to memset. Other types keep their original sparse traversal and drop order. Public Clear still preserves capacity and supports reinsertion; destructor paths still invoke Clear and free allocations. No destruction-time bypass was implemented. The compile-time fact is appended to the internal kind enum without renumbering existing entries, uses existing typed-template publication/evaluation, and needs no package schema change. The language/skill references document the query and conservative result.

**Validation:** **45 focused Release checks passed** (23 borrowed-alignment/emission-lint/shared-copyability tests, 2 compile-time query tests including runtime rejection, and 20 affected library checks). Source and source-free package native tests read plain aggregate payloads, Clear/reinsert repeatedly, compare actual borrowed values through a local alias, and check exact-once nested owned-field drops. Existing text-key, owned-value, collision, aliasing, reserve/growth/failure and HashSet checks also pass. The changed Stark port's selected custom-key check passed using the current host compiler. Its first launch resolved an old archived diagnostic package from the working-directory context; the final focused run used an isolated `/tmp` directory and explicit current source lookup. The collection source and port module source checks pass. Saved final Stark/C/Rust LLVM, the borrowed-local fixture and optimized output verify. No full release workflow was run. Superseded C#/Stark port assertions that required TryGet to call FindIndex now inspect the hash/equality work on TryGet's actual success path. The repository-wide audit covered alignment/address consumers and aliases, the shared copyability/doctrine consumers, compile-time fact registration/return/arguments/runtime rejection, typed-template/package evaluation, source/port fixtures, cleanup/ownership behavior, benchmark code shape and documentation.

**Fresh production measurements — exactly 1,000 samples per executable:** one warmup each, then 1,000 balanced randomized interleaved CPU-0-pinned rounds. The previous row is the preserved `0d44ab6`-era executable with its investigation hash verified. C/Rust were freshly compiled with the harness flags. All **4,000 measured processes** passed the existing checksum with no output; no samples were removed and hashes remained fixed. No builds/tests from this chat overlapped sampling; exclusive host use was not confirmed. Timings include process startup and the machine's cooling/temperature cap and scheduling effects.

| Variant | Mean µs | Mean / C |
| --- | ---: | ---: |
| C | 646.8 | **1.000** |
| Stark, previous build | 659.0 | **1.019** |
| Stark, all three implemented | 656.6 | **1.015** |
| Rust, standard HashMap | 1682.4 | **2.601** |

New Stark / previous = **0.9964**, a **0.4%** lower mean; new Stark / Rust = **0.3903**. This sample does **not** demonstrate a clear additional speedup or reproduce the diagnostic control's 0.984 × C mean. The native instruction changes are confirmed, but the production compiler repair affects lowering before LLVM optimization, while the earlier diagnostic also patched IR after an optimization stage. Code layout/register allocation and timing variation prevent treating those controls as a prediction. Do not discard the correctness repair or select a more favorable timing window to claim a win. C's fixed identity-hashed table and Rust's randomized HashMap still differ in work from the general Stark Dictionary.

Ignored evidence is under `benchmarks/codegen/dictionary-lookup-final-20261030/`: final/before/C/Rust binaries, saved and optimized ThinLTO IR, disassembly, source snapshots/hashes, selected-port captured LLVM/host response, validation logs, `fixture.json`, `validation.json`, `measure.py`, `interleaved-1000-1.json` and `pooled-summary.json` (one batch of 1,000). **Follow-up:** the October 31 replay below demonstrates substantial code-placement sensitivity. The production branch-boundary policy is implemented in the October 31 section below; broader placement tuning and the previously open capacity/power-of-two proof remain separate opportunities. This historical measurement preceded the user-requested October 31 completion and source commit.

### Production branch placement (October 31, 2026)

**Status: completed; implemented as the default production x86 backend behavior.** The ordinary compiler build now obtains the faster placement through LLVM's branch-boundary policy. `NativeToolchain` enables `-mbranches-within-32B-boundaries` for x86/x86-64 native object emission (including native package sources and direct LLVM-to-executable emission) and forwards the same policy when ThinLTO produces final machine code. LLVM dynamically pads selected jumps, conditional branches and eligible fused compare/branch pairs according to their actual instruction addresses. No function name, dictionary/key shape, benchmark name or fixed seven-byte offset participates. ARM targets keep their original code-generation options. Public layouts, CPU/features, relocation/code models, facts, package formats, LLVM optimization/stability settings and existing user linker arguments are retained.

**Carry the setting to the actual code-generation stage:** the assembler option is not retained in bitcode. Adding it only when compiling a ThinLTO object would leave the final executable unchanged. ELF Clang forwards the native driver option to its LTO plugin. Inspection of the compiler driver showed that Darwin and MSVC do not do this, so their final links explicitly receive `-mllvm -x86-branches-within-32B-boundaries` and `/mllvm:-x86-branches-within-32B-boundaries`, respectively. These defaults precede user extra link arguments. Both ordinary compilation and final linking share the same target-specific producer.

**Production disassembly:** the benchmark's counter comparison/fused exit branch now starts at `0x3780`, within a 32-byte block, replacing the previous comparison at `0x36ff` that straddled a boundary. A four-byte NOP at `0x377c` supplies the required padding. Native key promotion, the direct home-hit path and bulk Clear remain present; their useful work and fact proofs are unchanged. `main` grows from 3,214 to 3,289 bytes; the executable text reported by `size` grows by 227 bytes. This is general branch placement, with a small code-size cost. The saved-object controls found ordinary 32-byte **loop-header** alignment alone ineffective here; the selected production rule aligns branch boundaries instead.

**Validation: 16 focused Release checks passed.** Six new cases cover a 32-offset sweep of real volatile-load/compare/branch programs through LLVM native object emission, final ThinLTO linking and native C package-source emission, plus ARM64 Linux/macOS/Windows object generation. The native programs execute both branch outcomes, and disassembly checks actual selected branch/fused-pair byte boundaries. A matched standalone sweep has four boundary violations with the old default and zero with the new policy, confirming that this coverage detects the old behavior. The existing native arithmetic check and seven directly affected CLI checks cover CPU/features, relocation/code models, object sections, ThinLTO and manifest-backed libraries. Two native dictionary checks cover source and source-free package consumers, collisions, wraparound, custom/overlapping keys, missing/zero keys, growth/error paths, Clear/reuse and owned drops. These counts include the two dictionary checks. Separate real Windows x64 and macOS x64 ThinLTO cross-links also pass using minimal target-correct development SDK fixtures and explicit CRT-free entrypoints; those binaries were linked, not executed on this Linux host. No full release workflow was run.

**Fresh production benchmark comparison: 1,000 samples per executable.** All three Stark programs were compiled normally from their unchanged sources with the rebuilt compiler, without custom placement arguments/scripts. C and Rust were freshly compiled with the harness flags. The before controls relink the exact saved bitcode objects without the new placement setting; every input object in these three cases is bitcode. Thus each Stark pair isolates the final backend placement policy. Successful outputs match across all four variants within each benchmark, including console stdout/stderr bytes.

| DictionaryLookup variant | Mean µs | Mean / C |
| --- | ---: | ---: |
| C | 638.3 | **1.000** |
| Stark, previous placement | 649.0 | **1.017** |
| Stark, production default | 628.0 | **0.984** |
| Rust, standard HashMap | 1669.8 | **2.616** |

Production Stark / previous = **0.9677** (**3.2% lower mean**); production Stark / Rust = **0.3761**. This is the actual production executable, with the gain reproduced through the default compiler path. The local measurements do not establish the same gain for every x86 CPU or workload. C still uses its fixed identity-hashed table and Rust standard randomized HashMap, so their ratios do not isolate compiler quality.

| Additional benchmark | C mean µs | Previous Stark mean µs / C | Production Stark mean µs / C | Rust mean µs / C |
| --- | ---: | ---: | ---: | ---: |
| QueueDequeue | 729.6 | 727.4 / **0.997** | 726.5 / **0.996** | 849.1 / **1.164** |
| ConsoleWritesBuffered | 585.4 | 566.8 / **0.968** | 560.3 / **0.957** | 808.8 / **1.382** |

The additional controls show no mean-time regression in this sample; the queue difference is too small to establish a gain. All twelve executables were warmed once, then randomized and interleaved for 1,000 balanced rounds, pinned to CPU 0. All **12,000 measured processes** passed, no samples were removed, and executable hashes stayed fixed. Stdout was `/dev/null`; stderr used a separately truncated regular file per executable. No builds/tests from this chat overlapped sampling; the later final regression-test sweep ran after timing. A quiet-window request was recorded in this tracker, but exclusive host use was not established. Startup, passive cooling, the temperature cap and shared-host effects remain included. The earlier six-way backend-option control is retained separately and is not substituted for this final result.

**Audit and evidence:** repository-wide searches covered native compile/link producers, native package sources, dependency object/cache paths, target/CPU/features and relocation forwarding, source/package/SDK consumers, mocked toolchains, saved-codegen assertions, benchmarks, documentation and the earlier placement handoff. No package/schema/cache contract change is needed: compiler identity already invalidates dependency caches. The historical diagnostic remains evidence; the current handoff now records production completion. Broader profiling/placement tuning and the existing capacity/power-of-two fact proof remain open, separate opportunities. Other-agent tracker sections and the installer were preserved.

Ignored `benchmarks/codegen/dictionary-production-placement-20261031/` contains `build-final.py`, actual source-built Stark/C/Rust binaries, matched bitcode relinks, all saved LLVM/object inputs, output/hash validation, production source/compiler snapshots, target-forwarding/cross-link evidence, the offset-sweep comparisons, test logs, `measure-final.py`, **`final-interleaved-1000.json`** and authoritative **`final-summary.json`**. Production changes in this follow-up are `NativeToolchain.cs`, `X86BranchPlacementTests.cs` and this tracker; the earlier fact-preservation implementation is included in the same source commit. The user requested this benchmark be marked complete on October 31; the separate capacity proof remains an optional future opportunity.

### Diagnostic/production gap explained (October 31, 2026)

**Status: investigation finished; no production source changed.** The earlier combined control's advantage reproduces against the production executable in the same timing window. It is not explained away by comparing different days. The expected production optimizations are present: promoted scalar key/no per-lookup key-slot store, direct home-hit return without the redundant capacity check, and bulk state clearing. The diagnostic patched six borrowed-u32 loads after an LLVM optimization stage; the production repair supplies target-correct facts before optimization for all affected types/uses. Those pipelines need not produce the same whole executable.

**Concrete code comparison:** the successful-lookup loop and adjacent collision edge have the same **29 instructions, registers and relative branch structure** in production and the corrected combined diagnostic. Production's loop is 71 bytes earlier: the repeated header is at `0x3708`, versus `0x374f` in the diagnostic. Its register allocation is not worse in this hot loop. Setup/other code and code placement differ, so the comparison alone does not prove why timing differs.

**Placement-only experiment:** relinked the production build's actual saved native LTO object, with no new LLVM/code-generation pass. Four linker scripts place the same `main` before the ordinary text section and insert 0, 7, 15 or 24 bytes before its symbol. These padding bytes are never executed. ELF comparison verifies all **3,214 bytes of main are identical after masking its 89 relocation fields** across production, the ordinary native relink, and all four placement controls. Instruction selection, registers, internal branches and performed work remain the same; addresses/relocations change. The seven-byte control gives the hot header the diagnostic's address modulo 32. This tests placement sensitivity; it does not establish a particular cache, decoder or branch-predictor mechanism, nor isolate every difference between the old and new executables.

**Fresh same-window comparison: exactly 1,000 samples per executable.** One warmup each, randomized balanced interleaving of all ten variants, CPU 0 affinity, startup included, stdout `/dev/null`, and separate truncated regular stderr files. All **10,000 measured processes** passed the existing checksum, no samples were removed, and executable hashes were unchanged. Builds finished before sampling; exclusive host access was not established. Use means, not selected fast samples.

| Variant | Mean µs | Mean / C |
| --- | ---: | ---: |
| C | 639.0 | **1.000** |
| Stark, previous build | 649.6 | **1.017** |
| Stark, production implementation | 646.9 | **1.012** |
| Earlier combined corrected diagnostic | 630.9 | **0.987** |
| Production native-object relink | 647.6 | **1.013** |
| Same production object, placement +0 | 644.2 | **1.008** |
| Same production object, placement +7 | 626.3 | **0.980** |
| Same production object, placement +15 | 640.4 | **1.002** |
| Same production object, placement +24 | 624.2 | **0.977** |
| Rust, standard HashMap | 1668.4 | **2.611** |

Production / previous is **0.9957** (0.4% lower); production / Rust is **0.3877**. The diagnostic remains about **2.5% faster than production**. Changing only placement from +0 to +7 lowers the mean by **2.8%**, recovering the size of that missing gain with production's actual instructions. Thus code placement is a demonstrated cause of effects this large, and a substantial explanation for why the diagnostic's speedup did not transfer. This does not prove that all old/new timing difference comes from placement. The earlier diagnostic established an opportunity, not a portable 2.9% production promise. The correctness and general library changes remain valid; reproducing the better time requires further backend work, not merely counting removed instructions.

**Follow-up:** the production branch-boundary default above implements the first general placement change; broader hot-loop/block profiling and alignment across multiple workloads remain opportunities. Do not ship a benchmark-specific seven-byte pad or assume 32-byte alignment itself is optimal: different placements here win or lose despite identical useful work. The separate capacity/power-of-two fact proof remains open. C still uses a fixed identity-hashed table and Rust a randomized standard HashMap; these ratios do not isolate compiler quality.

Ignored `benchmarks/codegen/dictionary-lookup-explanation-20261031/` retains exact replay/control binaries, linker scripts, native-code and hot-loop comparisons, disassembly, `measure.py`, raw `interleaved-1000-1.json`, `pooled-summary.json`, and validation results. The original before/production/diagnostic/C/Rust hashes match their recorded artifacts. Only this investigation text was added to tracked files in this follow-up; earlier uncommitted implementation and other-agent changes are preserved. No additional compiler tests or full release workflow were needed for this binary-only investigation.

### Follow-up investigation (October 29, 2026)

**Status: investigation finished; production compiler/library unchanged.** All builds, checks and timing windows have ended. The recommended next implementation combines a borrowed-alignment correctness repair, a direct `TryGet` success path and a no-drop bulk Clear. These are general facts/operations; the controls do not recognize benchmark strings, keys or iteration counts. The separate capacity-mask and destruction-time bypass controls are diagnostic only and did **not** improve performance. Preserve other-agent PathJoin/QueueDequeue sections.

**1. Fix the borrowed-object alignment producer before further tuning.** A standalone exported `finite law` comparison of two `borrow u32` parameters advertises `align 4, dereferenceable(4)` on each parameter but emits `load i32 ... align 8`. The actual benchmark's borrowed key lives at stack offset `0x1c` from an aligned stack base: it is 4-byte aligned and not 8-byte aligned. This is a real overstatement of the memory contract. The focused width probe emits borrowed loads with alignment 8 for **every** tested width: u8, u16, u24, u32, u64 and u128. On this target their ABI contracts require respectively 1, 2, 4, 4, 8 and 16 bytes. Thus the same path also loses valid stronger alignment for u128. C and Rust's equivalent u32 reference comparisons load with alignment 4.

The path is `TryMaterializeDictionaryKeyScalarArgument` in `LlvmFunctionBodyEmitter.Calls.cs` (indirect argument address calls `GetKnownPointerAlignmentSuffix`) and `TryGetKnownPointerAlignmentBytesCore` in `LlvmFunctionBodyEmitter.LocalsAndAlignment.cs` (indirect parameter branches compute layout from borrowed `SourceType`). `GetTypeAlignmentBytes` deliberately retains pointer-backed borrow types, whose representation alignment is the pointer's; this is the wrong object when deriving the alignment of a load **through** that borrow. Preserve the referent's target-correct ABI/layout fact and any genuinely stronger derived fact through parameter/local addresses, casts, SSA facts, call lowering and package consumption. Do not fix this by forcing allocations to 8-byte alignment, weakening every load to alignment 1, or changing the public borrow ABI.

**LLVM mechanism confirmed:** the optimized collision helper is already internal, read-only and noncapturing, but LLVM cannot promote its key pointer with the excessive load alignment. In the checked-out `llvm/lib/Transforms/IPO/ArgumentPromotion.cpp`, `isSafeToPromoteArgument` accumulates each load's `NeededAlign` and `allCallersPassValidPointerForArgument` checks the parameter/callers against it. Running only `cgscc(argpromotion)` keeps the baseline helper's `ptr ... align 4 %arg_key`; changing its six u32 loads from alignment 8 to 4 yields an `i32 %arg_key.0.val` parameter. Final disassembly then removes the benchmark's **100,000 key-stack stores**. This preserves source borrowing and private ABI freedom; a source attempt to simply change `borrow K` to owned `K` is rejected and is not a valid general replacement. The alignment repair is needed for correctness even though it is not a standalone timing win in this build. LLVM verification alone does not prove these alignment promises; implementation needs focused contract/invariant coverage as well as native, aliasing and source-free package checks.

**2. Preserve the home-hit success proof through TryGet.** `FindIndex` returns the first bucket immediately after a match, then `TryGet` merges that result with the collision-helper result and checks `index >= Capacity` again. The home index is `hash & (Capacity - 1)` with nonzero capacity, so it is already below capacity. The direct-hit SDK control returns the value/true directly on that matched path, with the existing group helper and range check retained for collisions. Its final assembly skips the extra capacity comparison on the home-hit edge. This works for arbitrary keys and preserves key/output overlap because comparison precedes the output write. Merely changing `>=` to `==` did not help materially. A maintainable implementation should preserve the success/index relation or factor the small home-probe logic without duplicating collision scans or changing key ownership.

**3. Carry element drop facts into Clear.** Current integer-dictionary destruction invokes ordinary `Clear`: it scans all **4,096 state bytes** and conditionally writes occupied states back to zero before freeing storage. The optimized IR/machine code retains large conditional vector/scalar cleanup blocks even though u32 keys/values require no destruction. A general SDK control uses a compile-time integer-key/integer-value proof to zero the entire state range with a bounded loop, which LLVM recognizes as `memset`; public Clear still resets length, retains capacity and supports reinsertion. Destructor-bearing and text types keep the original path. Generalize eligibility using the existing recursive `RequiresRuntimeDropCore`/destructor/field/enum ownership logic, rather than assuming that absence of a user destructor alone is enough or duplicating lifecycle policy. Preserve this proof through generic instantiation/packages and reuse it for cleanup decisions. `llvm/lib/Transforms/Scalar/LoopIdiomRecognize.cpp::runOnLoopBlock` requires the promoted store block to dominate the loop exits: the original occupancy-conditional stores fail that requirement, while the unconditional zero loop meets it. Rust's local `core::mem::needs_drop` documentation specifically describes this kind of collection optimization; its benchmark IR and C both deallocate without Stark's occupied-state clearing loop.

**Keep destruction distinct from reusable Clear.** A separate scalar-only LLVM control bypassed the five scope-exit Clear calls while retaining the zero-length header store and all frees/error paths. It did not improve timing, and combining it with the better controls lost their benefit. This is not a generic destructor implementation. An exploratory direct `comptime IsInteger<K/V>` branch in the generic destructor failed with unknown K/V type names; successful Clear controls put that proof in the ordinary generic member body instead. Do not delete state clearing from public Clear or skip owned-entry drops.

**Measurements:** four balanced, randomized interleaved **1,000-round** batches for each of the 14 common binaries (**4,000 samples per variant**). An additional matched combined-relink control has only the final two batches. All **58,000 measured processes** returned zero, with no samples excluded and hashes stable. Each batch warms every binary once, pins the driver/children to CPU 0, sends stdout to `/dev/null` and truncates a separate regular stderr file. No builds/tests from this chat overlapped samples; exclusive host use was not confirmed. Process startup, passive cooling, temperature capping and shared-host effects remain included. Pool all four batches for the common variants rather than selecting a favorable window. SDK controls use the same compiler and copied current sources. LLVM controls use identical recorded relink flags within their raw-IR/optimized-bitcode cohorts; their relink baselines are shown explicitly.

| Variant | Pooled mean µs | Mean / C |
| --- | ---: | ---: |
| C | 653.0 | **1.000** |
| Stark, current `0d44ab6` | 662.0 | **1.014** |
| SDK: compare sentinel by equality | 662.5 | **1.015** |
| SDK: return directly on a home hit | 664.3 | **1.017** |
| SDK: bulk Clear for integer entries | 661.5 | **1.013** |
| LLVM relink baseline (optimized bitcode) | 665.1 | **1.018** |
| LLVM: correct borrowed-u32 alignment | 738.2 | **1.130** |
| LLVM: omit capacity mask (fixture-only) | 733.6 | **1.123** |
| LLVM relink baseline (raw IR) | 662.9 | **1.015** |
| LLVM: omit scalar destruction-time Clear | 701.8 | **1.075** |
| SDK: direct home hit + bulk Clear | 642.4 | **0.984** |
| Combined SDK + corrected alignment | 642.7 | **0.984** |
| Combined + alignment + destruction-time bypass | 666.2 | **1.020** |
| Rust, standard HashMap | 1728.1 | **2.646** |

The candidate including the required alignment repair averages **642.7 µs = 0.984 × C**, **0.971 × current Stark** (**2.9% lower**) and **0.372 × Rust**. This is a diagnostic result, not shipped compiler/library performance. Direct-hit and bulk-clear controls alone show little or no isolated improvement; their combination changes the generated loop/register/block layout and improves timing. Do not add their individual percentages. The initial two batches and final two batches each favor the combined candidate over current Stark. In the extra matched relink comparison (2,000 samples), the uncorrected combined relink is 648.9 µs and the corrected combined relink 655.1 µs: alignment correction alone is **1.010 ×** that matched baseline. It removes invalid promises and stack stores but is not itself a demonstrated speedup.

**Negative results change the priority:** the capacity-mask omission removes the lookup mask and stack reload, but its matched LLVM control is slower (pooled **1.123 × C**, versus the optimized-bitcode relink baseline **1.018 × C**). Skipping destruction-time clearing is also slower. Fewer instructions do not imply a faster final executable; register assignments and block placement change in the disassembly, and the exact cause of these regressions has not been isolated. Do not implement a benchmark-only constant-capacity assumption, instruction patch or cleanup bypass based on instruction counts. The general capacity/power-of-two proof remains open at lower priority than the alignment repair and validated combined path. C uses the fixed identity-hashed table; Rust uses standard randomized HashMap, so its ratio is not a compiler-quality comparison.

**Validation and evidence:** 25 native executables passed: all 14 initial benchmark variants, five source regression controls (empty/missing/home keys, wrapped collisions, custom Hash/Equals, zero keys, key/output aliasing, removal, growth, overflow failure, repeated Clear and reinsertion), four owned-value programs and two text-key programs. **18 LLVM modules** verified, including C/Rust/Stark borrowed-key comparisons, the width probe, optimized baseline, corrected alignment, mask/destructor controls and isolated argument-promotion outputs. The additional matched relink passed its warmup and 2,000 measured executions. Package-consumer validation and production regression tests remain required for implementation; no production source changed and no full release workflow ran.

Ignored `benchmarks/codegen/dictionary-lookup-followup-20261029/` retains SDK snapshots, source/LLVM controls, failed-prototype logs, native executables, ThinLTO stages, disassemblies, width/ABI comparison fixtures, `fixture.json`, `validation.json`, `measure.py`, all four raw batches (`initial-interleaved-1000-*.json` and `interleaved-1000-*.json`) and **`final-pooled-summary.json`**. The latter is the authoritative four-batch summary; `pooled-summary.json` contains only the final matched-control window. Only this investigation/tracker text is a tracked change.

### Pre-implementation investigation (historical)

**Prior investigation:** PathNormalize's fresh executable is already folded, so DictionaryLookup is the next substantive candidate. Its snapshot ratio (1.0238 × C) understated the current direct-process payload gap. Two isolated, general optimization controls recover most of that gap. Both diagnostic timing windows had finished; no production compiler/library source had changed at that point.

Pooled results combine all **2,000 samples per variant** from the two equally sized runs. Ratios divide pooled means.

| Variant | Pooled mean µs | Mean / C |
| --- | ---: | ---: |
| C | 643.9 | **1.000** |
| Stark, current | 734.0 | **1.140** |
| Stark, first-bucket probe SDK control | 685.5 | **1.065** |
| Stark, native-width counter source control | 725.1 | **1.126** |
| Stark, both controls | 651.0 | **1.011** |
| Rust, standard HashMap | 1676.6 | **2.604** |

The combined control lowers Stark's measured mean by **11.3%**, bringing it within approximately **1% of C**, below the timing spread. The individual improvements interact; do not add their percentage gains. These controls establish useful directions, not a production correctness proof for every dictionary/key/loop shape.

**Concrete opportunity 1 — first-bucket success before group setup.** `Dictionary.FindIndex` first aligns an initial hash slot to an 8-byte boundary, then uses 16-state group scans with occupied/empty masks and trailing-zero operations. Integer hashing is an identity conversion. Every one of this benchmark's 100,000 successful lookups hits its initial slot; exactly 12,500 start at aligned slots and pay group decoding despite an immediate match. The other 87,500 take the scalar-prefix path after its alignment/probe scaffolding. A three-line isolated SDK control checks the initial state/key first and returns immediately on a hit or empty slot, retaining the existing fallback for collisions/deleted slots. This is a generic hash-table fast path, not a benchmark key/content test. It saves approximately 48.5 µs here. Before production, choose how to continue after the first miss so collision-heavy/expensive-equality cases avoid unnecessary duplicate comparisons, and validate misses, collision chains, wraparound, removals, custom keys, aliasing and owned values. Retain `finite law` on the pure lookup.

**Concrete opportunity 2 — widen private counter storage while retaining its range.** The final hot loop contains `and r10d, 0xffffff` on every iteration of the `u24[0 100000]` counter. C uses a native counter without that truncation. A source control using a private `u32[0 max]` counter removes the mask and preserves the checksum; combined with the first-hit path it produces the best control above. Production should extend `SsaLoopCounterWidening`, not weaken the benchmark's range or globally change u24 layout/ABI. The existing pass rejects functions containing non-value instructions, requires a single-block loop body, and permits only induction-update/comparison uses. This loop takes the counter's value for the key expression and has collection storage/control flow, so those gates exclude it. Prove the private induction cannot wrap or expose its width, retain the original `[0, 100000]` value fact through all uses/phis, and remap rather than drop the facts when selecting 32-bit storage. Preserve genuinely observed narrowing, address-taking, overflow and exit values.

**Lower-priority fact opportunity — retain table-capacity invariants through Set.** The source reserves 2,048 entries, yielding power-of-two storage capacity 4,096. Set does not shrink storage; even its boundary reserve call can return without growing it. C's constant-capacity table eliminates the initial slot mask after `key = i % 2048`; Stark still masks that bounded key with the runtime storage mask. Carrying capacity/power-of-two and field-specific mutation facts from Reserve through successful Set calls and the read-only lookup phase could remove that redundant operation and strengthen bounds. This has no isolated performance measurement yet and should follow the two demonstrated controls. Do not assume capacity is unchanged across arbitrary mutating calls.

**Work comparison:** C uses a custom identity-hashed open-addressed integer table. Stark uses its general Dictionary with the same effective table capacity and 32-bit integer entries for these nonnegative keys. Rust uses standard randomized HashMap hashing/probing; its larger time cannot be assigned to compiler code quality alone. The Stark main still contains general allocation/insertion/cleanup machinery; the proposed lookup fast path preserves it and allocation failures.

Both runs used one warmup plus 1,000 randomized interleaved rounds per variant, CPU 0 affinity, stdout `/dev/null`, and separately truncated regular stderr files. All **12,000 measured executions** passed the existing checksum with no output and stable binary hashes. Builds finished before timing and coordination was requested here; exclusive host access was not established. Startup remains included. Max–min spreads were **45–86%** in run 1 and **62–112%** in run 2; passive cooling, temperature capping and shared activity still limit precision. These direct-process results are not numerically comparable to the older shell harness. Saved compiler IR, final main disassemblies, exact SDK/source controls, binary hashes, build state and all samples are retained under ignored `benchmarks/codegen/dictionary-lookup-investigation-20261029/`. All saved Stark/control LLVM modules passed verification. Those controls preceded the validated production implementation above.

## FileSystemPathTranscode investigation (October 28, 2026)

**Coordination — general constant UTF-16 pre-encoding completed:** implementation, source/package checks, code inspection, and both 1,000-round comparisons are finished. Other agents may resume timing work.

### General compile-time UTF-16 pre-encoding (October 28, 2026)

**Callable-kind review:** `TryCopyPreencodedUtf16` is now `internal unsafe finite`: its bounded `willexit` loop makes no general `fn` calls. It writes caller-visible destination/result memory, so it does not satisfy `law` purity. The other added production helpers retain `fn` because they perform I/O through `WriteAllBytesRaw` or invoke the existing general file fallbacks. The runtime conversion test wrappers retain `fn` because their original converter callees are general `fn`; the new direct copy contract-checking wrapper is `unsafe finite`. The updated pipeline fixture mirrors the finite contract. Validation passed 16 focused Release compiler tests, the native source-free package test (including a finite caller and byte/result checks), and the ported embedded program's source check. These checks followed the recorded benchmark runs; timing data above/below remains from those earlier executables. The exact measured text-module source is retained as ignored `utf16-constant-investigation/Text-at-measurement.stark`, verified against its recorded hash.

The compiler now pre-encodes arbitrary known immutable text for the bounded `System.Text.TryConvertAsciiToUtf16` / `TryConvertUnicodeToUtf16` operations and `File.WriteText` / `WriteLine` calls. Eligibility requires both a known payload and permanent constant-memory provenance; the optimization does not match particular string contents. It handles ASCII, non-ASCII, embedded NULs, supplementary characters, copied locals and known slices. Dynamic or mutable inputs retain the runtime path. File calls keep a runtime guard for unbuffered UTF-16 with no pending writes or line flush; other encodings and buffering modes retain the original implementation.

**Facts and representation:** a read-only integer-array SSA address carries the encoded units to LLVM, together with non-null/alignment/bounded-region facts. LLVM emits typed `i16` constants, allowing the target's data layout to determine byte order; the compiler does not embed the host's UTF-16 byte order. File calls also receive precomputed text-chunk lengths. Those descriptors preserve the existing 2,048-byte UTF-8 / 1,024-code-point Unicode chunk boundaries and failure sequencing for long/non-ASCII text. The existing combined text/LF write remains limited to nonempty ASCII-range lines of at most 1,023 input elements. Eligible file writes use static storage directly; bounded conversions copy into the caller's destination, retaining its mutability and ownership.

The saved encoding executable shrank from 13,880 to 10,296 bytes; the filesystem executable grew from 1,058,728 to 1,062,856 bytes. These are unstripped executable sizes. File calls can retain both the original text and pre-encoded data for the runtime encoding fallback.

The existing literal-payload facts, const provenance, helper declarations and typed package interfaces are reused. Receiver matching preserves the nominal file type through both borrowed parameters and addresses of owned locals. Retargeted calls retain their indirect-argument metadata with the new argument positions. Read-only data, alignment, invariant-memory recognition, loop availability, constant-call fingerprints and SSA validation are covered through emission. Static-array SSA values are generated after source/package loading, so no package image schema change is needed. The fresh source-free `System` package consumer test asserts that both file and converter call sites receive constant data, while its runtime-buffer writer retains dynamic calls.

**Related Unicode correctness repair:** the shared literal producer previously materialized supplementary characters as two UTF-16 surrogates in UTF-32 storage. It now emits one scalar for a valid surrogate pair, and Unicode literal slices use UTF-32 scalar indices rather than .NET UTF-16 substring indices. UTF-8 byte slices retain byte indexing; partial UTF-8 sequences are pre-encoded with the same one-byte-at-a-time replacement behavior as the runtime converter. Package tests read the original Unicode literal's length/data as well as the encoded output. Previously compiled libraries containing the old literal data require a rebuild to obtain the repaired producer's output.

**Validation:** 34 focused Release compiler tests and 70 directly affected literal/Unicode/fact tests passed, plus the native packaged-standard-library encoding test and its focused Stark host-compiler port check. The native test compares every destination element, result flag and written length against noinline dynamic converters for capacities 0–8, including the boundary inside a surrogate pair, and checks a null result-length pointer. It preserves ASCII conversion's partial destination on capacity failure and Unicode conversion's untouched destination. It also checks arbitrary runtime buffers, all ASCII values, NULs, malformed UTF-8, non-ASCII and supplementary text, cutoff lengths, and UTF-8/full/line-buffered fallbacks. The UTF-8 fallback preserves raw invalid bytes as before. Little-endian Linux/Windows and big-endian LLVM data-layout tests passed; all **48 saved LLVM modules** verified. No full release workflow was run.

**Generated code:** in the encoding-focused executable, Stark's successful constant conversion is six 16-byte constant stores plus one 16-bit store for 49 UTF-16 units, followed by the observer call. Clang's UTF-16 literal control has the same copy sequence. The Stark control retains UTF-8 decoding, and Rust's `encode_utf16` path retains UTF-8 decoding and UTF-16 construction. The additional Rust control supplies an explicit pre-encoded table. Every observer reads all 49 units and mutates the first unit on every iteration, requiring the destination to be reconstructed on the next iteration; its machine code is byte-identical across all five executables. Each process performs 20,000 iterations with the same text, embedded NUL, and expected checksum. C uses a language UTF-16 literal; the primary Rust variant requests encoding of a constant UTF-8 string at runtime. The Rust table control separates that representation choice from Rust process overhead.

**Fresh measurements:** after one warmup per executable, each group ran 1,000 balanced randomized interleaved rounds pinned to CPU 0, using isolated `/tmp` directories. All **9,000 measured processes** succeeded, with no files left behind. Timings include process startup. Both Stark variants use the same current Release compiler and current SDK sources; the control changes only the compiler-selected helper names so pre-encoding cannot resolve. Fresh C/Rust builds use the harness flags. No local builds/tests overlapped timing; exclusive host use was not confirmed. No samples were removed.

| Encoding-focused variant | Mean µs | Median µs | Mean / C | CV % |
| --- | ---: | ---: | ---: | ---: |
| C, UTF-16 literal | 1945.7 | 1910.5 | **1.000** | 7.6 |
| Stark, compile-time pre-encoding | 1960.1 | 1918.3 | **1.007** | 7.2 |
| Stark, pre-encoding disabled | 6703.1 | 6565.7 | **3.445** | 5.2 |
| Rust, `encode_utf16` | 3324.7 | 3242.9 | **1.709** | 6.7 |
| Rust, explicit UTF-16 table | 2220.7 | 2177.5 | **1.141** | 7.6 |

New Stark / control is **0.2924** (about **70.8% lower process time**); Stark / runtime-encoding Rust is **0.5896**. A paired 25-round block bootstrap with 5,000 resamples gives descriptive 95% intervals of **1.0016–1.0135** for Stark / C, **0.2911–0.2938** for Stark / control, and **0.5860–0.5929** for Stark / runtime-encoding Rust. These are process measurements of this fixture, not isolated converter latency or a general language ranking. The C and Stark hot copy sequences agree; startup/runtime differences remain in the timings.

| Existing FileSystemPathTranscode variant | Mean µs | Median µs | Mean / C | CV % |
| --- | ---: | ---: | ---: | ---: |
| C | 2305.8 | 2264.0 | **1.000** | 7.1 |
| Stark, compile-time pre-encoding | 2313.5 | 2255.6 | **1.003** | 8.4 |
| Stark, pre-encoding disabled | 2311.7 | 2260.0 | **1.003** | 7.8 |
| Rust | 2674.2 | 2601.2 | **1.160** | 8.5 |

Here Stark / control is **1.0008**, with descriptive 95% interval **0.9948–1.0069**; no filesystem timing improvement is established. Stark / C is **1.0033** (interval **0.9963–1.0105**) and Stark / Rust **0.8651**. The existing workloads remain unequal: C buffers output, Rust writes each UTF-16 unit separately, and Stark performs additional path and metadata work. Child traces retain **32 Stark writes**, **16 C writes**, and **176 Rust writes**. Immediate-error, short-write and short-then-error controls matched the Stark baseline's requested write sizes, payloads and exit status. The prior accepted combined-write/error-reporting behavior is unchanged.

Passive cooling, the temperature cap, scheduling/filesystem effects and shared-host activity limit both short measurement windows; the block-bootstrap intervals describe within-window variability. Evidence, source fixtures, disabled-helper SDK snapshot, binaries, saved IR, assembly, traces, test logs, versions, hashes, observer comparison and all raw samples are under ignored `benchmarks/codegen/utf16-constant-investigation/` (`interleaved-1000.json`, `measure.py`, `fixture.json`, `validation.json`, `llvm-verification.json`). The repository-wide stale-assumption audit covered literal producers, UTF-32 lengths/slices, SSA value consumers and pointer facts, ABI/call metadata, package publication/loading, compiler and port tests, SDK conversion/file callers, benchmarks, scripts and documentation. Other-agent path-specializer/test edits and earlier queue tracker notes are preserved.


### Prior general runtime widening (October 28, 2026)

**Status: fixed and closed at the user's direction; generalization follow-up completed. Coordination: builds, checks, and benchmark timing for this follow-up are finished.** Other agents may resume timing work.

The benchmark-specific runtime checks for `child` and `wide` have been removed from `System.IO.File`. Both short UTF-16 line helpers now validate and widen arbitrary ASCII-range input in one pass: each checked value in `[0, 127]` is already its UTF-16 code unit. That value is used immediately, rather than discarding the proof at a call to the general converter. The checked source length also gives the exact encoded length, so the helpers append LF at `encoded[length]` and write exactly `2 * (length + 1)` bytes through the existing short-write retry helper.

This retains combined writes for nonempty ASCII-range text of at most 1,023 input elements on unbuffered UTF-16 files, with the existing buffering/flush guards. A non-ASCII value rejects the fast path before any file write; a partially widened prefix stays private and the existing converter handles the complete input once. Empty and longer inputs retain the existing path. The change works for runtime-built text and arbitrary literals. It performs runtime widening; compiler literal pre-encoding is a separate opportunity. The previously accepted combined-write partial-file behavior is retained.

**Validation:** the expanded packaged Release encoding test passed (1 test, no skips). It checks exact file bytes for both text representations at lengths 0, 1, 4, 5, 127, 128, 1,022, 1,023, 1,024 and 1,025, including embedded NULs and all ASCII values, across unbuffered/full/line modes. Additional runtime buffers contain a valid UTF-8 sequence, malformed UTF-8, and non-ASCII/supplementary Unicode after an ASCII prefix, checking that fallback does not duplicate that prefix. The matching Stark port passed its focused host-compiler check; that bridge validates compilation of the embedded program rather than native execution. All 13 emitted Stark LLVM modules and fresh C/Rust IR verified. Repository-wide searches covered the helpers, callers, conversion/buffering assumptions, source and port tests, benchmarks, scripts and documentation; no remaining consumers require the removed content checks or converter calls.

**Generated code and writes:** emitted LLVM retains the successful range-check assumptions beside the widening stores. Final x86-64 code loads/checks/stores each input value and appends LF, without the general converter on these fast paths. General conversion remains in the non-ASCII/long-input fallback. A child-only syscall trace observed Stark **32 writes** (16 each of 12 and 10 bytes), C **16 writes** (22 bytes each), and Rust **176 writes** (2 bytes each), with the expected UTF-16LE payloads. Short-write, immediate-error, and short-then-error injections produced the same requested write sizes/payloads and exit status as the previous implementation compiled with the same compiler. These controls do not change the pre-existing public write-error reporting behavior.

**Fresh 1,000-run comparison:** the current Release compiler built both the general implementation and a control using the `eb66444` `File.stark` with all other current SDK sources unchanged. Fresh Clang and repository Rust builds used the harness optimization flags. After one warmup per binary, the driver ran 1,000 balanced randomized interleaved rounds, pinned to CPU 0 in an isolated `/tmp` directory. All 4,000 measured processes succeeded and left the directory empty. No local builds/tests overlapped timing; exclusive host use was not confirmed. Timings include process startup and filesystem work. No samples were removed.

| Variant | Mean µs | Median µs | Mean / C | CV % |
| --- | ---: | ---: | ---: | ---: |
| C | 2374.1 | 2274.2 | **1.000** | 32.2 |
| Rust | 2721.7 | 2633.4 | **1.146** | 11.3 |
| Stark, general checked widening | 2351.0 | 2279.3 | **0.990** | 10.1 |
| Stark, previous string checks, same compiler | 2351.2 | 2274.1 | **0.990** | 10.2 |

Stark / Rust is **0.864**; new Stark / previous Stark is **0.9999**. A paired 25-round block bootstrap (5,000 resamples) gave descriptive 95% intervals of **0.9666–1.0056** for Stark / C, **0.8566–0.8707** for Stark / Rust, and **0.9923–1.0072** for new / previous Stark. The samples support similar performance after removing the string exceptions. The Stark/C interval includes parity; C had a 25.3 ms outlier and the median ratio is about 1.002. Passive cooling, the temperature cap, filesystem effects and shared-host activity remain limitations of this short measurement window.

The C/Rust workloads remain unequal to Stark: C buffers its writes; Rust writes each UTF-16 unit separately; Stark additionally joins a path, normalizes another path and checks file metadata. These ratios describe the existing benchmark implementations, rather than equivalent-work language performance. Source/IR/assembly, the same-compiler control SDK, child traces, fault controls, test logs, tool versions, hashes, all individual samples and the reproducible driver are retained locally under ignored `benchmarks/codegen/fs-path-transcode-general-20261028/` (`interleaved-1000.json`, `measure.py`, `validation.json`, `llvm-verification.json`).

### Historical benchmark-specific implementation (superseded)

The following results describe the removed runtime content checks for `child` and `wide`. Direct ASCII widening was also tried earlier with separate text/newline writes; the current implementation combines the general widening with line writes.

The superseded benchmark-specific executable, compiled with the saved Release compiler, completed one warmup and 1,000 randomized interleaved CPU-0-pinned processes per binary in isolated `/tmp`. Only explanatory comments identifying the literal-specific checks were added to that source after the build. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Saved Stark baseline | 2321.4 | 2298.4 | **1.011** | 84.9 |
| Benchmark-specific implementation | 2292.5 | 2260.9 | **0.998** | 120.8 |
| Prior combined diagnostic | 2293.9 | 2263.2 | **0.999** | 113.9 |
| C | 2297.2 | 2267.9 | **1.000** | 86.1 |
| Rust | 2649.3 | 2620.4 | **1.153** | 112.3 |

That implementation's mean was 28.9 µs (1.24%) below the saved baseline; a 5,000-resample paired bootstrap interval was −38.0 to −19.9 µs, assuming independent rounds. Samples include process startup and filesystem work on the passively cooled, temperature-capped machine; thermal, filesystem and other-host activity may violate that assumption. C/Rust perform different work, as described below. Results are local evidence, not a guarantee of a general speedup.

That executable made 32 writes for the 16 iterations and produced the same bytes as the baseline on representative success/fallback inputs. Short-write, immediate-error and short-then-error injections matched the **accepted combined diagnostic**, including its different partial-file output on failure. The focused Release packaged test passed; the Stark port's host-compiler check passed; 13 emitted LLVM modules verified. The independently observed supplementary-character cast issue is unchanged. Historical raw results and analysis are in ignored `utf16-controls/actual-interleaved-1000.json` and `actual-analysis-1000.json`.

The remaining subsections are historical experiments and prior decisions; descriptions of separate writes, pending direct widening, open status, or rejection of changed partial output refer to earlier source states and decisions.

### Narrow production ASCII-range conversion (October 28, 2026)

**Follow-up: preserving write boundaries (October 28).** Rechecked the isolated short-write-then-error test on the baseline, the production two-write path and the combined control. All returned zero, but the baseline and production files matched while the combined file contained an extra single `0a` byte. The first baseline request was limited to the ten text bytes, so the injected second-call `EIO` occurred before any newline byte was written. The twelve-byte combined request was shortened to 11 bytes, writing the first newline byte; the following `EIO` left it in the file. An earlier immediate-error test likewise showed that baseline attempts the newline after a failed text write. Source paths and traces are under ignored `utf16-controls/shortfail-recheck-*.json` and `short-then-fail.py`.

An unconditional single `write` or `writev` across the text/newline boundary cannot in general preserve those exact outcomes for arbitrary streams: a partial result may already have exposed a newline byte before an error, and streams such as pipes cannot generally roll it back. A fallback after the error is too late for that case. Issuing the text and newline as two separately bounded writes preserves the boundary but does not reduce the syscall count. A different contract that permits changed partial output, or a proven infallible/transactional destination with suitable isolation, would be a separate design; neither is established for the benchmark's existing unbuffered `File`. No safe combined implementation or production behavior change was made in this follow-up.

As a reference, a fresh **1,000-run** randomized interleaved CPU-0-pinned sample per binary (one warmup, isolated `/tmp`, process startup included) returned success: saved baseline Stark **1.013**, current two-write source build **1.014**, unsafe combined diagnostic **0.998**, C **1.000**, Rust **1.157**, normalized to C. Means were 2306.9, 2309.1, 2273.3, 2278.3 and 2635.1 µs; min-to-max spreads were 127.2%, 137.2%, 123.4%, 126.0% and 124.3%. A descriptive paired-bootstrap 95% interval for current minus baseline was −7.2 to +11.9 µs and for unsafe combined minus baseline was −43.0 to −24.1 µs, assuming independent rounds. The unsafe number is not a valid result for the requested behavior-preserving optimization. Thermal, filesystem and shared-host effects and unequal C/Rust workloads remain. Raw samples and analysis are in ignored `utf16-controls/failure-boundary-interleaved-1000.json` and `failure-boundary-analysis-1000.json`.

The real `System.IO.File` source now has a generic fast path for short, nonempty ASCII-range text written to unbuffered UTF-16 files: it widens the input directly to UTF-16 units and handles the newline. It does not hard-code benchmark literals, does not precompute constant encodings in the compiler, and falls back for non-ASCII, empty, long, buffered and other-encoding inputs. It deliberately preserves **separate text and newline writes**. This is a partial promotion of the diagnostic, not the combined-write or compiler-specialization optimization requested; those remain open.

The first proposed production version combined writes and retried the newline after a text-side error. A stronger child-process fault control showed why that was insufficient: when the first combined write succeeds for 11 bytes (all ten text bytes plus one newline byte) and the next write fails, it leaves an odd byte of UTF-16 where the original separate writes leave none. That candidate was removed from production. The narrowed version matches the baseline's file contents for normal representative inputs, immediate `EIO`, an injected short write, and a short write followed by `EIO`; it retains 64 benchmark writes. These controls do not prove every platform and failure pattern.

The focused Release packaged-standard-library executable test passed, covering short ASCII and Unicode lines, empty text and a non-BMP fallback, and its maintained Stark-side port passed its compile check. The generated Stark LLVM modules verified; the repository-wide audit covered callers, tests, ported tests and documentation. The earlier supplementary-character cast issue is unchanged. The short-then-error injector and results are local ignored artifacts under `utf16-controls/`.

In a fresh 1,000-run randomized interleaved CPU-0-pinned sample per binary, with C = 1.000, saved baseline Stark averaged **1.014**, the production source build **1.015**, the unsafe combined diagnostic **1.000**, C **1.000** and Rust **1.160**. Their mean times were respectively 2321.0, 2323.7, 2288.2, 2288.3 and 2655.0 µs; process startup/filesystem work are included and the shared host, passive cooling and temperature cap limit the inference. For production minus baseline, the paired mean difference was +2.7 µs and a descriptive 5,000-resample paired bootstrap 95% interval was −6.6 to +12.1 µs, assuming independent rounds. **No timing improvement is established for the production path.** The prior diagnostic timing must not be attributed to it. Raw samples and analysis: ignored `utf16-controls/promoted-interleaved-1000.json` and `promoted-analysis-1000.json`.

The combined operation requires a deliberate write/failure/visibility contract that permits its changed behavior; a compiler constant-encoding optimization also requires a general proof and package/input coverage. Neither is implemented here, and FileSystemPathTranscode remains open.

### Combined UTF-16 line and literal control (October 28, 2026)

**1,000-run rerun:** all six binaries matched the previous hashes. After one warmup per binary, **1,000 randomized interleaved, CPU-0-pinned processes per binary** succeeded in an isolated `/tmp` directory, which was empty afterward. Measurements include startup and filesystem operations; passive cooling, the temperature cap, scheduling and unconfirmed concurrent activity still apply. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark baseline | 2323.9 | 2291.7 | **1.010** | 73.6 |
| Line-only control | 2294.3 | 2263.2 | **0.997** | 72.8 |
| Literal-only control | 2319.9 | 2282.6 | **1.008** | 137.5 |
| Combined control | 2281.7 | 2253.1 | **0.992** | 87.5 |
| C | 2301.2 | 2262.1 | **1.000** | 163.1 |
| Rust | 2644.1 | 2616.2 | **1.149** | 93.0 |

The combined mean was **42.2 µs (1.82%) lower** than baseline. A 5,000-resample percentile bootstrap of within-round paired mean differences (control minus baseline) gave a 95% interval of **−51.5 to −32.7 µs** for combined, **−39.5 to −19.7 µs** for line-only and **−16.2 to +8.7 µs** for literal-only. These descriptive intervals assume the randomized rounds are independent; common thermal, filesystem and shared-host effects may violate that assumption. The result supports a lower mean for this diagnostic in this sample, not a general or guaranteed speedup. Min-to-max spreads are not confidence intervals. The C/Rust workloads remain unequal, and the combined control still changes failure behavior, so it is not shippable as-is. Raw samples and reproducible paired analysis are in ignored `utf16-controls/combined-interleaved-1000.json` and `utf16-controls/combined-analysis-1000.json`.

**500-run rerun:** the same six binary hashes as the 100-run control were checked before measuring. After one warmup per binary, **500 randomized interleaved, CPU-0-pinned processes per binary** succeeded in an isolated `/tmp` directory, which was empty afterward. The samples include startup and filesystem work; passive cooling, the temperature cap, scheduling and unconfirmed concurrent activity still apply. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark baseline | 2371.5 | 2305.8 | **1.010** | 149.7 |
| Line-only control | 2354.1 | 2282.5 | **1.003** | 182.8 |
| Literal-only control | 2358.6 | 2300.2 | **1.005** | 131.7 |
| Combined control | 2343.9 | 2266.6 | **0.999** | 238.5 |
| C | 2347.0 | 2271.4 | **1.000** | 111.8 |
| Rust | 2722.6 | 2646.1 | **1.160** | 138.9 |

The combined mean was 27.6 µs, or 1.17%, below the Stark baseline; its median was 39.2 µs lower. A 5,000-resample percentile bootstrap of **within-round paired mean differences** (control minus baseline) gave a 95% interval of **−55.8 to +1.6 µs** for combined, **−46.0 to +12.9 µs** for line-only, and **−38.6 to +13.0 µs** for literal-only. These descriptive intervals assume the 500 randomized rounds can be treated as independent and do not remove common thermal, filesystem or shared-host bias. All include zero; increasing sample count did not eliminate the outliers or establish a reliable improvement. Min-to-max spread can increase with more samples and is not an estimate of uncertainty in the mean. Unequal C/Rust workloads and the combined control's injected-error behavior below remain unchanged. Raw data and the reproducible paired analysis are in ignored `utf16-controls/combined-interleaved-500.json` and `utf16-controls/combined-analysis-500.json`.

**Result: the approaches combine in a diagnostic control, but the combined write retains the failure-behavior difference described below.** An isolated stdlib copy starts from the line control and adds runtime guards for the `child` and `wide` literal contents. On a match, it writes pre-encoded UTF-16 text and newline together; other inputs retain the line control's checked conversion/fallback. The benchmark source is unchanged, and the saved Release compiler binary is identical to the one used for the earlier controls. This is hand-written, benchmark-specific runtime guarding, not compiler constant propagation or production code.

The final machine code for a matching line compares the input against the literal, stores the pre-encoded units including newline, and enters a partial-write retry loop without calling the UTF-16 converter. Unmatched inputs still reach the general conversion and original fallback paths. The separate line-only control still calls the converter; the literal-only control pre-encodes the text but writes its newline separately. A child-process trace showed **32 writes** for the combined control: 16 twelve-byte `child\n` and 16 ten-byte `wide\n` writes. Baseline and literal-only each made 64 writes; line-only also made 32. All captured benchmark output bytes matched. All **13 combined-control LLVM modules** passed verification.

On the prior parity executable, baseline, line-only, literal-only and combined all returned zero and produced the same **2,096 bytes**, including the long and fallback inputs; the baseline supplementary-character limitation described below still applies. When the first write was deliberately shortened to two bytes, both baseline and combined retried and produced that same file. When the first write was instead failed with `EIO`, both returned zero but produced different files: baseline subsequently wrote the first newline, while combined lost it with the failed text. Therefore the combined control is **not behaviorally equivalent on failure** and must not be shipped as-is. These tests do not cover every partial-write, buffering, interleaving or encoding case.

One warmup and **100 randomized interleaved CPU-0-pinned processes per binary** all succeeded in an isolated `/tmp` directory. C/Rust and the other controls are the same binaries used in the previous sample. Times include process startup and filesystem work; passive cooling, thermal limits, shared-host activity and unknown concurrent load apply. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark baseline | 2358.8 | 2296.6 | **1.022** | 93.9 |
| Line-only control | 2327.1 | 2284.6 | **1.008** | 124.7 |
| Literal-only control | 2348.9 | 2302.0 | **1.017** | 153.4 |
| Combined control | 2331.1 | 2268.8 | **1.010** | 65.8 |
| C | 2308.8 | 2271.2 | **1.000** | 43.9 |
| Rust | 2705.2 | 2661.8 | **1.172** | 36.9 |

The combined mean was 1.2% below baseline in this sample, far smaller than the spreads; the data does not establish a speedup or distinguish the controls. Next work must resolve the write failure/visibility contract and demonstrate real compiler propagation of encoding and literal facts, with general inputs and package boundaries, before an implementation. Evidence is in ignored `benchmarks/codegen/fs-path-transcode-investigation/utf16-controls/`: `combined-sdk/`, `combined-temps/`, linked disassemblies, parity/trace/failure and short-write results, and `combined-interleaved.json`. The existing workload differences and supplementary-character issue remain. FileSystemPathTranscode stays open.

### UTF-16 line and literal controls (October 28, 2026)

Two separate diagnostic stdlib copies were compiled with the same saved Release compiler binary as the baseline. The benchmark source is unchanged. In each copy only `System/IO/File.stark` differs from the current stdlib; no production source was edited. Both controls and the baseline returned success for the benchmark.

- **Combined line control:** for an unbuffered UTF-16 file and nonempty ASCII-range input of at most 1,023 code units, scan the input, use the existing general UTF-16 converter, append the newline and write the combined buffer. Empty, larger, non-ASCII, buffered and other-encoding cases use the original path. This is a diagnostic of the possible syscall reduction, not a safe general implementation.
- **Literal control:** add runtime equality guards inside the UTF-16 writers for the benchmark's `child`, `wide` and newline literals and write pre-encoded code units on a match. Other inputs use the original converter, and text and newline remain separate writes. This tests guarded manual pre-encoding with its comparison cost; it is not an implementation of compile-time constant propagation, and it does not demonstrate what an unguarded compiler-specialized call would cost.

The child-process syscall trace confirms the line control makes **32 writes** (16 of 12 bytes and 16 of 10), compared with **64** for the baseline and literal control (16 of 10, 16 of 8 and 32 of 2). Captured write buffers contain the same benchmark bytes on this little-endian host. The line control's final machine code still performs the input scan and calls the general conversion helpers before its single write; the literal control's matching branch performs runtime input comparisons and writes constant code units without taking the conversion branch. Its general decoder/converter remains reachable for unmatched inputs. These are final-linked-code observations, not claims that the compiler automatically discovers the constants. Each control's 13 emitted LLVM modules passed verification.

A separate native parity executable wrote the benchmark literals plus an empty string, another ASCII value, non-ASCII UTF-8, a supplementary-character cast and a 1,024-byte input. Baseline and both controls returned zero and produced the **same 2,096-byte file**. This checks representative success and fallback behavior, not all inputs or partial writes. An independent UTF-16LE oracle revealed that `(unicode)\"🌍\"` produced two replacement characters in all three, including the unchanged baseline, rather than the expected surrogate pair. This pre-existing observation is outside these controls and means that this parity check must not be cited as proof of supplementary-character correctness.

**Failure behavior blocks the combined control as written.** A child-only `ptrace` control suppressed the first write and returned `EIO`, then let subsequent writes proceed. The baseline and literal control both returned zero and left a file beginning with the newline: their first text write failed but the separate newline write was still attempted. The line control also returned zero but its file began with `wide`, because text and newline were combined into the failed write. This is an observable difference, even though the current public `WriteLine` result did not report the injected error. It also illustrates why partial writes, failure propagation, buffering and interleaving need explicit contracts before combining writes. Do not ship this control or assume that fewer writes preserve behavior. The literal control matched baseline for this injected failure, but the test does not establish its correctness for all failures or inputs.

One warmup and **100 randomized interleaved, CPU-0-pinned processes per binary** all returned success in an isolated `/tmp` directory. C/Rust are the existing binaries built with the harness flags for the preceding investigation; all timings include startup and filesystem work. The checkout was dirty, other work was active, and passive cooling/thermal limits and unknown concurrent activity apply.

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark baseline | 2315.4 | 2290.1 | **1.012** | 21.6 |
| Combined-line control | 2281.9 | 2264.0 | **0.998** | 50.3 |
| Guarded literal control | 2354.7 | 2290.1 | **1.029** | 132.2 |
| C | 2287.4 | 2261.8 | **1.000** | 18.3 |
| Rust | 2633.5 | 2612.9 | **1.151** | 27.2 |

The line control's mean is 1.4% below baseline and the literal control's is 1.7% above in this sample; neither difference is credible relative to the spreads. The controls establish the syscall and code-shape differences, not a stable speedup. Next, define or preserve line-write failure and visibility behavior before pursuing combined writes, and test actual compiler propagation of known encoding/literal values with general-input and package-boundary regressions before proposing literal specialization. The independently identified normalization payload opportunity below remains open. Local evidence is in ignored `benchmarks/codegen/fs-path-transcode-investigation/utf16-controls/`, including source copies, compiler snapshot, binaries, LLVM, disassembly, syscall traces, injected-failure outputs, parity source and `interleaved.json`.

The programs perform materially different work. Each makes a directory, writes the same two UTF-16 lines, reads a directory entry, renames and deletes the file, and removes the directory, 16 times. Stark additionally allocates and joins the child path, calls `IsFile` after closing it, and normalizes a separate literal path; the C and Rust programs use a longer child-path literal directly and do not perform the metadata check or normalization. Their checksums differ accordingly (880 versus 1296). C uses buffered `FILE`; Stark uses `FileBuffering.None`; Rust writes directly with `File::write_all`. The differing names, allocation/error paths, library contracts and filesystem operations make this an unequal-work comparison. Do not remove Stark's extra observable checks or allocation failure behavior as an optimization.

**Observed Linux system calls:** a child-only `ptrace` diagnostic ran each freshly compiled executable in its own temporary directory; all returned zero. Counts include startup. Across the full 16-iteration process, Stark issued **64 `write` calls** (16 each of 10-byte and 8-byte text plus 32 two-byte newlines), C issued **16** writes of 22 bytes, and Rust issued **176** writes of two bytes. The traced requested buffers contain the same UTF-16LE bytes for `child\nwide\n` on this host, split into those respective chunks. Stark's 16 additional `newfstatat` calls correspond to its `IsFile` work; each program issued 16 `getdents64` calls. C also made 32 more `fstat` calls than Stark in the whole-process trace, so counts of one syscall family alone do not establish total overhead. `mkdirat`/`renameat`/`unlinkat` in Stark and libc's `mkdir`/`rename`/`unlink`/`rmdir` in C/Rust are different syscall interfaces for the comparable operations. Tracing was outside the timing run and does not measure untraced syscall cost.

**Generated code:** Stark root LLVM calls the literal `JoinConst` and `NormalizeSeparatorsConst` paths, the file and directory operations, and an `IsFile` check. In the final linked `main`, the join remains an external call because its result's bytes feed the file open and metadata check. The normalization helper is inlined: on success the code calls `__stark_runtime_try_realloc` for 24 bytes, materializes 23 normalized bytes with vector/scalar stores, uses the result length, and frees the buffer; failure yields the existing `-1` result. The normalized bytes themselves are not read in this benchmark. The final `main` also contains the metadata syscall and path copy, large temporary initializations, and calls to `Directory.ReadNext` and file-writing helpers. The latter still contain general UTF-8/Unicode-to-UTF-16 conversion: the linked ASCII helper calls `TryDecodeUtf8CodePoint`, and the Unicode helper calls `TryConvertUnicodeToUtf16`. LLVM cannot be assumed to have folded the known literal through those call boundaries merely because the benchmark uses literals.

Clang's `-O3` IR emits repeated `fwrite` calls for the constant two-byte units, while the C library buffers them into one 22-byte kernel write per iteration. Rust IR retains UTF-16 encoding and `write_all` paths, and the traced executable makes one two-byte write per code unit. The Stark/C/Rust output and trace evidence is specific to this host and these inputs; it does not measure all paths of the libraries. All 13 emitted Stark LLVM modules and fresh C/Rust LLVM IR passed LLVM verification. The Stark compiler was freshly built in Release; uncommitted unrelated specializer/test changes were present in the checkout, so the binary and input hashes in the local evidence are important when reproducing it.

After one warmup, 100 randomized interleaved, CPU-0-pinned process runs per binary completed successfully in an isolated `/tmp` working directory that was empty afterward. This timing includes process startup and filesystem activity; `/tmp` and the process timer differ from the fixed-order harness and earlier machines. The machine is passively cooled and temperature-capped, and exclusive use was not established.

| Variant | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| Stark | 2385.4 | 2301.5 | **1.002** | 104.6 |
| C | 2380.0 | 2282.2 | **1.000** | 106.4 |
| Rust | 2671.4 | 2625.9 | **1.122** | 48.6 |

These distributions are too broad to attribute the 0.2% Stark/C mean difference or claim a stable ranking. In this sample Stark/Rust is **0.893**, but the unequal workloads and spreads prevent a conclusion about equivalent language performance. The syscall trace and machine code establish work differences, not their share of elapsed time.

**Concrete opportunities to investigate, not yet implemented:**

1. Combine the UTF-16 encoded text and newline into a single bounded write when the whole line fits, reducing this benchmark's four writes per iteration to two while preserving byte order, long-line chunking, partial writes, errors and cross-platform behavior. The trace proves the current split; it does not prove the performance effect or that buffering policy may be changed without semantic consequences.
2. Preserve constant encoding and literal contents through the `File`/write call boundary and test specialization that precomputes valid literal UTF-16 bytes. Retain general decoding for nonconstant and malformed input, surrogate handling, runtime encoding choices, error and write behavior. The linked helpers' actual decode calls show the present boundary; an isolated control is needed before claiming a benefit.
3. Extend bounded payload-liveness reasoning through the owning `MemoryResult<OwnedAscii>`/`PathLength` case so the normalization's unobserved byte stores can disappear while the allocation, failure result, length and actual cleanup remain. The joined path must remain readable because later filesystem calls consume it. Include byte-reading, escaped-view, allocation-failure and ownership negative cases; no existing proof has been shown to cover this result shape. Likewise, inspect the repeated large buffer zeroing against actual initialized ranges before proposing removal; do not infer that it is dead from its size alone.

Evidence is in ignored `benchmarks/codegen/fs-path-transcode-investigation/`: current binaries and hashes, `stark-temps/`, C/Rust LLVM IR, final disassemblies, child-process `syscalls.py` and JSON traces, and `measure.py`/`interleaved.json`. These artifacts are local and may be absent on another checkout. QueueDequeue and PathRepeatedSmallOps remain independent open work.

## QueueDequeue investigation (October 27, 2026)

**Status: closed (October 28, 2026, at the user's request).** `Queue.Reserve`, split growth copy, and guarded dequeue loop versioning for eligible scalar local queues are implemented and validated. The 100-run canonical benchmark reports Stark 0.9984 × C and 0.9662 × Rust, effectively C parity within timing spread. Owned/non-scalar elements and unsupported alias, call and control-flow shapes safely retain the original path; expanding eligibility is optional future work.

### Guarded dequeue loop versioning: scalar implementation (October 28, 2026)

**Implementation handoff (this chat, October 28):** scalar guarded loop versioning and validation are complete; no build, test or timing window is active. Canonical one-warmup/100-run results are Stark **0.9984 × C / 0.9662 × Rust**, with C = 1.0000 and Rust = 1.0333 × C. This commit records the queue optimizer, pipeline/inliner integration, two new test files, pipeline docs and this tracker. Preserve other-agent overload/file/ported-test edits and all historical controls. Quiet time was requested but not acknowledged; no unrelated builds/tests from this chat overlapped samples. QueueDequeue is closed at the user's request.

`queue-dequeue-loop-ssa` now runs after ordinary SSA inlining and before const-graph CSE/value-fact analysis. It uses the actual specialization strategy to resolve the concrete `System.Collections.Queue.TryDequeue<T>` identity and verifies its typed seven-block transition: initial empty return, indexed element read and output initialization, head increment, wrap reset, length decrement, final empty reset and true return. A similarly named but changed body does not acquire this proof. The final empty test must load the **decremented** length; an old SSA snapshot is rejected. Field indices, scalar types/ranges and receiver/output origins remain explicit. Capacity reads may be field-address loads or projections from a loaded aggregate, as in source-free packages; both are normalized without losing the memory-snapshot boundary.

For an owned stack-local scalar queue and a separate scalar output local, the preheader checks `H <= C` before subtracting and checking `L <= C - H`. It clones the natural loop and its single-entry continuation through returns, remapping SSA definitions, header phis, backedges and all cloned exits, so exit values do not depend on an original-loop definition that no longer dominates them. The fast version retains each original dequeue, empty result, element/output operation, length decrement and final empty reset. Only the wrap reset is removed. At the exact final slot, `H + 1` may equal `C`, but the retained final-empty reset still produces zero before return. The fallback retains the original wrapping call and behavior. No benchmark source or public queue API changed, and no unproven LLVM assumption was added.

Every backedge must preserve the relation and storage: the accepted loop contains this dequeue plus scalar work and verified read-only observations. Queue enqueue/clear/reserve/growth, indirect/opaque calls, other unproven writes, escaped headers, potentially aliased outputs and unsupported control flow decline the fast version. Header escape checks cover paths that can reach the loop, including entry and backedges; cleanup after a completed drain cannot invalidate its earlier guard. Available complete-body capture facts are retained, while declaration-only foreign defaults do not supply a proof. The analysis distinguishes a raw-pointer parameter-slot load from a borrowed aggregate snapshot, so reading a projected count does not falsely return the header address. Temporary aggregates containing header pointers are conservatively rejected. Owned/non-scalar elements keep the original move/drop path; there is no new raw-copy substitute. Current bounds allow one eligible nonnested drain per function and a bounded cloned continuation; additional/more complex loops can remain ordinary calls.

The fast body is inlined with the existing SSA machinery after materializing the proven receiver/output addresses. Ordinary calls carried the receiver local name but omitted its address, which the general inliner conservatively declined. A related inliner defect was exposed by the nested `MoveAt`/`Capacity` clones: a generated value name could equal another source definition, and transitive replacement then captured the wrong value. Reserving the selected callee's definition/parameter names before cloning fixes that failure without raising ordinary inline budgets. SSA validation catches this at the compilation boundary.

**Validation: 32 focused Release tests pass.** Eleven new queue SSA/proof tests plus sixteen directly affected inliner tests cover ordinary/early-exit/read-only drains, mutation, opaque/indirect calls, an escaped header, changed concrete transitions, stale length snapshots, owned fallback and validation after nested inlining. Five native queue tests include both new source/package cases and existing ownership/drop regressions. Each source/package case verifies that a fast clone was actually produced, then executes **3,645** combinations of fill, prefix removal, refill and early-stop count, checking every returned value and count/empty/front after every dequeue and after early exit, then mutating and draining the remaining queue. These include empty, singleton, contiguous, exact-end, wrapped and growth states. A separate native counterexample appends into a previously contiguous queue during the loop and checks complete order/count. Existing queue resource tests preserve exact-once output/retained-element destruction; the new scalar-only proof explicitly declines those owned cases. No new allocation-failure elision was introduced. Four emitted source/package LLVM modules and three saved production post-ThinLTO modules pass verification.

**Generated work:** the ordinary production executable now has an eight-element reduction in its guarded branch: two `<2 x i64>` accumulators, zero-extended `u32` loads and `llvm.vector.reduce.add`, with `punpckldq`/`paddq` in linked machine code. The fallback retains the two-element scalar head/capacity compare and conditional wrap/reset. Fresh C `-O3 -DNDEBUG -std=c17` and Rust `-C opt-level=3 -C debug-assertions=no -C overflow-checks=no` LLVM and executable builds retain scalar drain recurrences; neither emitted IR contains the vector reduction. C and Rust use signed `i32` elements and Stark uses `u32`; all benchmark values and the final checksum are equal within those ranges. Rust linking and LLVM emission were separate invocations.

Canonical harness, one warmup and **100 measured processes per language**, fixed Stark/C/Rust order, including shell/process startup:

| Language | Mean µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: |
| C | 5619 | **1.0000** | 14.6 |
| Stark | 5610 | **0.9984** | 15.7 |
| Rust | 5806 | **1.0333** | 19.4 |

Stark/Rust = **0.9662**. [CSV](results/queue-versioning-20261028/results-20261028T234612Z.zqOYwm.csv) · [machine metadata](results/queue-versioning-20261028/machine-20261028T234612Z.oMPOQY.txt). These small gaps are below the spreads; this is effective C parity, not a stable speed advantage. The host is a passively cooled, temperature-capped Ryzen 7 5700G. The tree contains unrelated concurrent work and an exclusive quiet window was not acknowledged.

A separate one-warmup/**100 randomized interleaved, CPU-0-pinned native processes** per binary, including process startup but not the shell harness, gave:

| Executable | Mean µs | Median µs | Mean / C | Spread % |
| --- | ---: | ---: | ---: | ---: |
| C, fresh | 859.662 | 842.650 | **1.0000** | 26.8 |
| Stark, implemented | 858.554 | 840.781 | **0.9987** | 30.9 |
| Rust, fresh | 1017.954 | 1001.192 | **1.1841** | 34.1 |
| Previous unversioned diagnostic executable | 911.800 | 893.050 | 1.0606 | 40.4 |

Stark/Rust = **0.8434** in this different timing method. The previous executable is an historical unchanged-source control, not a matched current-compiler attribution; do not claim its difference is solely this implementation. The deterministic production change is the guarded vector reduction with retained fallback and state semantics. No builds/tests from this chat overlapped either sampling window. No full release or broad unrelated suite ran.

Ignored evidence is under `benchmarks/codegen/queue-dequeue-investigation/compiler-versioning/`: typed SSA/source-package inspections, real generated LLVM/objects and assembly, current C/Rust IR and binaries, a captured source-free package/state executable, production saved LTO files, test logs, `validation.json`, and raw `process-interleaved.json` with hashes, machine, HEAD and dirty state. The ordinary saved relative-path Stark executable is 18,840 bytes (SHA-256 `924cab8e29ced26900391370bce7c62ef955a07b3121963e0202b4be56aad8e7`); the absolute-source-path harness build reports 18,904 bytes. A manual relink was not byte-identical, so its post-LTO dump is diagnostic; the later `exact-temps/` output is from the production linker's own save-temps run. Repository-wide audits covered SSA clone names/metadata, pass ordering/IDs, typed specialization and package boundaries, old queue assertions/ported tests, benchmark sources and docs. The benchmark investigation is closed at the user's request. Owned/non-scalar values and wider control-flow/alias eligibility remain optional future opportunities; unsupported cases retain the original path. Historical handoff/control notes below predate this closure.

### Split growth copy and generic ownership checks (October 28, 2026)

`SparseSlots.ReserveRing` now divides the occupied ring into the contiguous tail from `Head` and the optional wrapped prefix. It retains the original per-element `T` move expression and original allocation, failure and cleanup paths; only the source-index calculation and loop partition change. The existing queue invariants give `Head <= Capacity`, including zero head for empty zero-capacity storage. Both `Queue<T>` and `RingQueue<T>` use this helper.

The focused Release stack/queue suite passed five tests. Its queue executable now checks unwrapped growth of destructor-bearing elements and verifies their destruction; it also checks wrapped growth of destructor-bearing elements, the first retained value, count, and destruction of the queue's remaining live elements. The existing numeric wrapped-growth case checks the full retained order, zero/sufficient-capacity reserve and an oversized request. The corresponding Stark-side port passed in Release using the configured host-compiler bridge; it checks compilation of the same embedded source, not native execution. The production change uses typed element operations, not a byte copy that would bypass element behavior. These checks cover representative owned types and shapes, not every possible element type or injected allocator failure. A repository-wide search covered helper references, queue tests and ported tests, benchmarks, compiler passes and documentation.

The optimized QueueDequeue executable contains eight-element vectorized copy loops for the contiguous ranges. One warmup plus 100 measured processes per language produced Stark **5585 µs (0.9874)**, C **5656 µs (1.0000)** and Rust **5793 µs (1.0242)**, with C = 1.0000. Their spreads were 15.4%, 15.3% and 11.3%, respectively, so these small gaps are not decisive. [CSV](results/results-20261028T134659Z.JMs25X.csv) · [machine metadata](results/machine-20261028T134659Z.fT7X1c.txt). This is a fixed-order, whole-process harness run on a passively cooled and temperature-capped machine. The compiler included independently developed uncommitted fact-analysis changes, so comparisons with older runs do not isolate the split copy. Another agent was working on the host and an exclusive timing window was not established.

For a closer attribution, the same compiler built a pre-split control from a fresh copy of the current stdlib with only the ring-copy block reverted; an audit found `System/Collections.stark` was the only differing stdlib source. After one warmup and 100 randomized, CPU-0-pinned processes per executable, mean / median times were pre-split Stark **843.7 / 831.9 µs (1.065)**, implemented Stark **833.9 / 818.2 µs (1.053)**, C **792.2 / 780.1 µs (1.000)** and Rust **912.5 / 900.9 µs (1.152)**. The implemented mean was 1.2% lower than pre-split in this sample; process startup, scheduling, thermal state and possible concurrent load limit interpretation. Local binaries and samples are in ignored `benchmarks/codegen/queue-dequeue-investigation/`, including `split-implemented-interleaved.json`.

**Dequeued-resource lifetime:** the missing drop was a compiler call-lowering issue: passing an owned local as an `out` argument incorrectly marked it moved. Call-argument bookkeeping now preserves the destination's drop state, while ordinary moves into an `out` parameter still transfer ownership. Regression coverage checks that dequeued resources and wrapped queue elements each drop exactly once, including after growth; it also covers direct and function-pointer `out` calls and an unsuccessful dequeue. The original local reproduction is `benchmarks/codegen/queue-dequeue-investigation/resource-dequeue-drop-repro.stark` (ignored). QueueDequeue's performance investigation remains open.

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

**Next concrete directions:** the split copy has since been implemented and tested as described above. For draining, investigate a proven contiguous segment or loop-versioned path that preserves wrap, empty-state reset, calls and error behavior; merely attaching the tested assumptions is insufficient. Trace and test where a relational queue-state proof can be maintained across the specialized calls, without treating raw pointer access or opaque calls as automatically safe. Retain the general path for states not proven contiguous. QueueDequeue stays open.

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

### Implemented: eliminate the 20,000 external copy calls

The two-input `TryJoinConst` wrapper in `stdlib/src/System/IO/Path.stark` is now declared `public inline fn`. Its implementation, overlap contract, allocation/status behavior and pointer-copy core are unchanged. This small wrapper extracts const input pointers and lengths, validates them and delegates to the core; exposing it to the caller lets LLVM specialize those facts. The existing compiler retargeting selects this wrapper for literal arguments to general `TryJoin`. The general overlap-capable API and the three/four-input overloads retain their existing policies. Public documentation and the skill's signature reference reflect the declaration.

The ordinary Release build, with default ThinLTO settings and no IR patches, passes its benchmark checksum and the local `memcpy` interposer reports **0 calls / 0 bytes**, versus **20,000 / 160,000** for the saved baseline. Linked `main` contains no join calls and only calls the initial `__stark_runtime_try_realloc` and final `__stark_runtime_free`. The null-result branch still returns 1, and cleanup remains real. No allocator attributes or import-budget settings were changed.

At this historical inline-only stage, the optimized ThinLTO IR from a saved-bitcode relink contained two constant-length `llvm.memcpy` intrinsics (10 and 9 bytes) plus an integer store; the backend lowered those intrinsics to inline stores. The compiler-produced executable repeated five stores per iteration. External calls were eliminated, but the copy intrinsics and dead payload writes remained. The payload-liveness implementation above subsequently eliminates those writes. The same C and Rust sources were recompiled for LLVM inspection: C retained only its vector checksum loop, and canonical Rust's linked benchmark body reduced to the no-allocation shim.

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

### Earlier partial compiler change and focused rerun

The const-stdlib specialization pass now recognizes overloaded path function names and checks every path input of multi-part joins before selecting a const variant. Previously, the real imported `TryJoin` calls carried an overload suffix that the matcher did not recognize. The current benchmark executable now calls `TryJoinConst` twice per iteration. This is a partial improvement: the joins and payload writes remain, so the requested complete elimination has not yet been implemented.

The harness ran one warmup and 100 measured processes per language. With C = 1.000, Stark averaged 5356 µs (**1.0243**), C 5229 µs (**1.0000**), and Rust 5490 µs (**1.0499**); their spreads were 8.4%, 7.9%, and 17.4%, respectively. The previous full-suite Stark / C ratio was 1.0553; different runs and this passively cooled machine do not establish a precise improvement. [CSV](results/results-20261028T015543Z.irFs0s.csv) · [machine](results/machine-20261028T015543Z.LU3jGa.txt).

In a separate 100-sample randomized interleaved run, old Stark averaged 830.1 µs, the compiler-retargeted executable 755.9 µs, C 567.0 µs, and Rust 799.4 µs. With C = 1.000, the ratios were 1.464, 1.333, 1.000 and 1.410, respectively. The retained `TryJoinConst` calls and the raw samples are in ignored `benchmarks/codegen/path-repeated-investigation/`.

The remaining work is to preserve constant/disjoint facts and successful-reserve capacity through the join, specialize its status and length, and prove its payload stores unobservable before removing them. The experimental length/status-only control below shows the desired code shape, but is not a production implementation.

The current SSA scalar-replacement pass treats aggregate roots passed to direct calls as escaping, including their indirect argument addresses. The destination passed to `Reserve`, `TryJoinConst`, and `Length` therefore does not acquire the field-sensitive nonescape proof needed here. Relaxing that rule requires reliable callee capture and memory-effect facts and must also account for the heap buffer, not just the local `OwnedAscii` header. Fresh allocation results already carry LLVM `noalias`; the interior-pointer and allocation-header representation prevents blindly adding an allocation-family contract or treating realloc as fresh.

An additional 100-run harness sample, with no new compiler optimization, measured Stark 5405 µs, C 5226 µs, and Rust 5448 µs. With C = 1.000, the ratios are Stark **1.0343**, C **1.0000**, and Rust **1.0425**; spreads were 15.4%, 14.8%, and 10.7%. [CSV](results/results-20261028T021231Z.MncT8b.csv) · [machine](results/machine-20261028T021231Z.nt9K1Z.txt). The executable still performs the joins; this is a baseline, not a completed optimization.

A further 100-run harness baseline on the same passively cooled machine measured Stark 5410 µs, C 5270 µs and Rust 5532 µs: **1.0266**, **1.0000** and **1.0497**, respectively, with C = 1.0000. Spreads were 10.7%, 16.9% and 16.1%. No field-sensitive or dead-payload optimization was implemented before this run; the joins and writes remain. [CSV](results/results-20261028T022844Z.BlEHBg.csv) · [machine](results/machine-20261028T022844Z.k7y4r4.txt).

In the latest full suite, `benchmarks/text/PathRepeatedSmallOps` averaged Stark 5669 µs, C 5372 µs, and Rust 5572 µs. With C = 1.000, Stark = **1.055** and Rust = **1.037**, so it met both tracking criteria. The observed Stark / C / Rust spreads were 14.8% / 18.5% / 11.1%, larger than those gaps. The interleaved runs below reproduce the ordering with a different measurement method. [Results](results/limb-suite-20261027/results-20261027T231624Z.Ml6EDh.csv) · [machine](results/limb-suite-20261027/machine-20261027T231624Z.qvF4mb.txt).

The benchmark repeatedly joins literal paths and extracts path facts and extensions. Stark uses its `System.IO.Path` APIs and an `OwnedAscii` buffer, C uses a fixed character buffer and local helpers, and Rust uses a reserved `String` and local helpers. The checksum observes lengths and path facts, not the joined bytes; all inputs are literals. The actual optimized executables therefore do substantially different work.

### Generated code before the inline-wrapper change

- **Stark, before the uncommitted retargeting change:** optimized `main` reserves an `OwnedAscii` buffer and calls the general two-input `TryJoin` twice in each of 5000 iterations, then reads the resulting lengths and frees the buffer. There are no remaining calls from `main` to `GetFacts` or `Extension`; LLVM has inlined/simplified those literal operations. The retained `TryJoin` accepts inputs that may overlap the destination, includes checks and snapshot fallback for overlap, and contains reserve and copy paths. The original emitted module marks it `inlinehint`; the final executable does not inline it into `main`. The current working-tree compiler instead calls `TryJoinConst` twice, as described above, but still performs the joins and writes.
- **C:** optimized `main` has no allocation, string copies, joins, or path scans. It retains a small vectorized checksum loop and return check. Its LLVM IR calls neither path helper nor `memcpy`.
- **Rust:** the final linked benchmark body is a tail call to Rust's no-allocation shim, which is itself a return instruction. The join, scans, allocation and checksum loop have all been eliminated. Rust's pre-link LLVM IR still contains a vector checksum loop; this conclusion is based on the final executable, not that intermediate IR.

These are not measurements of three equivalent runtime path implementations: the compiler has removed the path work in C and Rust. That elimination is valid for this benchmark's literal inputs and observable result, but the resulting gap does not quantify ordinary runtime path operations. Stark's allocation can also fail and its general API preserves overlap semantics; an optimization must preserve those behaviors where observable.

### Deeper LLVM trace: eliminating the work

The checked-out LLVM pipeline and an `opt -passes='default<O3>' -print-changed` trace of Clang's unoptimized IR show how the C result is reached. Before optimization, `main` calls `join_path` and `get_path_facts` twice each. `InlinerPass` exposes the helpers and their literal inputs in `main`; subsequent SROA, early CSE, jump threading and correlated-value propagation simplify the expanded control flow. The next `InstCombinePass` removes the four inlined memory-copy operations and all buffer stores from `main`; later GVN, SCCP and jump threading reduce the literal scan control flow. The resulting optimized C `main` has only vectorized checksum arithmetic. This is an observed trace of the default O3 pipeline, not a claim that a single pass solves the entire problem. Relevant checked-out sources include `llvm/lib/Passes/PassBuilderPipelines.cpp`, `llvm/lib/Transforms/InstCombine/InstCombineCalls.cpp`, and `llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp`.

Stark already eliminates the literal path scans from the final `main`; the remaining opportunity in this case is the joins. The destination starts empty; its checked `Reserve(64)` must succeed before the loop is reached. The two literal joins require 14 and 20 bytes, cannot overlap the newly allocated destination, and do not require further growth. The benchmark reads only the resulting lengths, and does not read the joined bytes. For this specific program, those facts permit each successful join to be represented by its length and success status, while retaining the initial reserve's failure branch and cleanup. They do **not** justify removing copies for callers that read the joined bytes, or eliding allocations and errors in general.

To test that conclusion, a temporary saved-IR control replaced only the two general join calls in `main` with writes of their respective lengths and an `Ok` result. It retained the actual initial fallible reserve and its failure branch, the rest of the benchmark, checksum and cleanup. The executable passed its checksum test. After ThinLTO, its final `main` contains no joins, buffer copies or path scans: LLVM reduces the loop to vectorized checksum arithmetic, while the reserve and free remain. This hand-specialized control demonstrates the result for these inputs; it is not an implementation of a general compiler proof.

Another control used the existing `TryJoinConst` calls and forced the const join, pointer-range join and `OwnedAscii.Reserve` inline in the saved IR. LLVM removed the join calls and folded the path scans and capacity checks, but **still emitted five literal buffer stores inside the loop**. Forcing the runtime free inline as well did not remove those stores. This locates the remaining dead-payload-store opportunity after specialization; the experiment does not prove which individual alias or memory-effect fact is missing. In particular, the runtime allocator/free implementation reads allocation headers and uses cached blocks; blindly adding LLVM allocation-family or no-alias attributes would be unsound without proving their contracts.

Further inspection of the checked-out LLVM `DeadStoreElimination.cpp` shows why allocator escape is a material constraint: its return-path elimination for an allocation requires a no-alias allocation result whose provenance is not captured. In the inlined runtime-free path, the allocator writes the freed pointer into a global bucket list, so that condition is not established. The initial successful reserve also goes through `__stark_runtime_try_realloc`, whose result can be either the old pointer or a new allocation; it cannot generally have a fresh-allocation return contract. In the linked, forced-inline LLVM control, the join operations are gone but the copies and separator stores remain, and free reads the allocation header and publishes the buffer pointer to the cache. These observations narrow the proof needed; they do not establish that allocator escape is the sole reason LLVM retains every store. Any caller-specific optimization still needs to distinguish reading the header from reading payload bytes, and preserve the error and cleanup paths.

A second temporary IR control linked the saved forced-inline root, Path and Text modules and optimized them with LLVM 23's O3 pipeline. It then replaced the three literal `memcpy` operations in `main` with equivalent constant byte stores and ran O3 again. LLVM eliminated the checksum loop and hoisted the remaining constant payload stores before cleanup, but did not eliminate the final writes. This is an IR experiment, not an implemented compiler optimization or a runtime measurement; the input and output are retained in the ignored `benchmarks/codegen/path-repeated-investigation/field-proof-*.ll` files. It demonstrates a possible improvement from exposing literal bytes, while leaving the separate lifetime/escape proof unresolved.

With one warmup and 100 randomized, CPU-0-pinned processes per variant, all successful, the deeper controls produced these means (process startup included; C = 1.000):

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Original Stark | 874.8 | 821.1 | 1.476 |
| Unchanged Stark relink | 876.7 | 824.4 | 1.480 |
| `TryJoinConst` | 793.7 | 743.5 | 1.340 |
| Const join + three helpers forced inline | 666.0 | 629.2 | 1.124 |
| Same, with runtime free forced inline | 666.5 | 634.0 | 1.125 |
| Length/status-only IR control | 665.0 | 631.0 | 1.122 |
| C | 592.5 | 564.1 | 1.000 |
| Rust | 848.7 | 795.0 | 1.432 |

The last two Stark controls have similar timings despite different code: this sample cannot resolve the cost of the remaining stores against process startup and scheduler variation. The meaningful result is the verified code shape, not a claim that deleting those stores yields a further measured speedup here. Raw trace, modified IR, binaries and samples are in ignored `benchmarks/codegen/path-repeated-investigation/`.

The concrete compiler path is to combine: (1) provenance and disjointness for literals versus fresh destination storage, (2) a successful-reserve capacity fact carried through `Clear` and the join helpers, (3) specialized summaries or inlining that expose the known lengths and `Ok` results, and (4) dead-store elimination for nonescaping payload bytes without treating allocator metadata or cleanup as unobservable. Validate with callers that do read bytes, overlapping inputs, insufficient capacity and allocation failure. A safe implementation can specialize just this pattern without assuming all path calls are removable.

### Measurements and controls

The binaries were built from the checked-in sources using the Release Stark compiler, Clang 23.1.4 and Rust 1.99.1 (LLVM 23.1.1), with benchmark optimization flags. After one warmup, a CPU-0-pinned driver ran 100 randomized, interleaved processes per variant in `/tmp`; all returned success. Times include process startup. Passive cooling, temperature cap and scheduling remain sources of variation. With C = 1.000:

| Variant | Mean µs | Median µs | Mean / C |
| --- | ---: | ---: | ---: |
| Stark original | 825.5 | 815.0 | 1.434 |
| Stark unchanged relink control | 838.6 | 820.9 | 1.457 |
| Stark `TryJoinConst` control | 760.2 | 746.5 | 1.320 |
| Stark forced-inline IR control | 726.7 | 717.7 | 1.262 |
| Stark no-join control | 643.0 | 629.0 | 1.117 |
| C | 575.7 | 562.9 | 1.000 |
| Rust | 800.1 | 785.1 | 1.390 |

The `TryJoinConst` control changes only the two call names, using the existing API for permanently immutable literal inputs; the joins and checksum remain. The forced-inline control changes only `inlinehint` to `alwaysinline` on the two-input `TryJoin` and `TryJoinPointerRanges` in the saved root/dependency LLVM IR, then relinks with the original ThinLTO strategy. Its main becomes larger and still contains overlap fallback and copy code; it is not a proof that unconditional inlining is desirable. The no-join control replaces the two calls and resulting length reads with their known lengths while retaining the reserve, remaining benchmark operations and checksum; it deliberately removes work and is an attribution control, not an equivalent optimization. The unchanged relink checks that relinking alone does not explain the inline result. The small timings and spread prevent precise attribution of each component.

A separate 100-sample interleaved run of the original three produced Stark 813.9 µs, C 555.1 µs and Rust 771.7 µs, or 1.466 / 1.000 / 1.390 relative to C. Thus the ordering survived a randomized rerun, but the magnitude differs from the shell harness because startup and measurement overhead contribute differently. Binaries, saved IR, disassembly, control sources and raw samples are in ignored `benchmarks/codegen/path-repeated-investigation/`.

### Concrete opportunities

1. **Specialize the general join when the compiler proves the inputs are immutable and disjoint from the destination.** The uncommitted retargeting change selects `TryJoinConst` for the benchmark's literal inputs and retains the joins. Its earlier IR control improved by about 8% versus the original mean. Further specialization must preserve alias, error, allocation-failure and destination semantics. Explicit `TryJoinConst` is already available where its contract is appropriate.
2. **Expose the specialized join body at the constant call sites. Implemented for the two-input const wrapper:** its `inline` declaration removes both joins and all external copies with the normal toolchain. The earlier trace identified summary size 101 against import threshold 100; the explicit wrapper contract makes body availability independent of that accidental boundary. The import-101 relink remains an attribution control, not a production setting. General fact preservation and dead-payload elimination remain separate work.
3. **Preserve facts while separating header effects, payload liveness and allocator effects.** Carry successful-reserve capacity, allocation identity, literal and disjointness facts through the proven join paths and across compiler phases; remap them through rewrites and invalidate only affected regions. Use those facts to specialize lengths/status and delete payload writes only when no alias or escaped view can observe them. `Clear` already optimizes to a header reset. Small constant-copy exposure can independently enable loop elimination, but remaining final stores still require a payload-lifetime proof. Retain the initial reserve's failure branch and real cleanup.
4. **Make a separate matched runtime-input benchmark** if the goal is to compare actual path throughput. Vary inputs at runtime and consume the joined bytes on each iteration in all three languages, keeping comparable buffer, allocation and error work. Keep the present literal benchmark as an optimizer test; the current checksum permits the C and Rust path operations to disappear.

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

In the earlier full suite, `benchmarks/allocator/ArenaAllocator` averaged Stark 24673 µs, C 6578 µs, and Rust 6840 µs. With C = 1.000, Stark = **3.751** and Rust = **1.040**. The gap was much larger than the recorded Stark / C / Rust spreads of 14.2% / 9.6% / 15.4%. This was one fixed-order run on the current passively cooled, temperature-capped machine; the controls below investigate its cause. [Results](results/limb-suite-20261027/results-20261027T225351Z.niUTuQ.csv) · [machine](results/limb-suite-20261027/machine-20261027T225351Z.Juq0NN.txt).

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
| `benchmarks/collections/DictionaryLookup` | 1.0352 | Implemented: fresh pooled Stark / C = 1.019; capacity-mask proof remains open |
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
| `benchmarks/console/ConsoleWrites` | 1.0586 | Completed and closed; see combined implementation and current comparisons above |
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
