# Lessons from the performance-optimization branch

Reviewed range: `2d8a32e..d84e74a` (22 commits). Inspect a cited change with `git show <commit> -- <path>`. These are examples of proof and investigation techniques, not promises about future timings. The tracker records later corrections and negative results; read the latest status before treating an old handoff as unfinished work.

## Select current work from executable evidence

`fa63579` selected ArenaAllocator after earlier benchmark work; `4b298e5` established compiler/codegen investigations and tracking. A historical ranking can become obsolete when constants, payload writes or loops disappear. Recheck whether work survives before optimizing it. The current PathNormalize executable had already eliminated normalization; its remaining costs belonged to allocation/startup, not normalization.

Inspect both `git log` and diffs/tests. Commit titles do not establish that a diagnostic was generalized, that a package path was tested, or that two languages performed the same work.

`26445c3` and `3bb18ef` closed ArenaAllocator and MemoryCopyFill using fresh 1,000-run evidence without another source patch. Existing optimizations had already met the requested goal. A successful investigation can verify current behavior; it need not manufacture a new optimization.

## Prefer missing facts to larger optimization budgets

`ed8ee53` made the small two-input constant path wrapper inline, eliminating external copies. That exposed remaining payload stores rather than solving the whole benchmark. `ae26be4` restored the **complete nine-field, target-aware PathFacts producer**, retaining root/absolute fields and typed projections through compilation; the checksum loop then disappeared in normal output. Increasing unroll thresholds or replaying O3 had only diagnosed the opportunity.

`3cbac18` repaired repeated fact recomputations that omitted direct-call effects and added payload liveness. Shared analysis inputs matter after **every** rewriting pass. An initially exact capacity/allocation fact can disappear later even though no code mutates it. Track this transition rather than assuming the first analyzer is wrong.

## Separate headers, payloads, escapes and failure

`3cbac18` eliminated unobserved scalar dynamic-payload writes while retaining headers, status, fallible allocation/reallocation and cleanup. The proof followed aliases and call graphs and rejected ambiguous/escaping cases. Regression tests read bytes, expose derived pointers, mutate state and cross packages so the optimization cannot equate unused lengths with unused storage.

The same investigation found a correctness defect: dereferencing a pointer loaded from a header was described as `memory(argmem: read)` on the header argument. A separately optimized caller/reader exposed a deleted required payload update that whole-module testing hid. Model reachable memory honestly; passing LLVM verification is insufficient.

## Expose the right bulk or arithmetic shape

`4c1bf0f` used read buffering for eligible read-only fully buffered files, corrected logical seek offsets for unread buffered bytes, and changed an early-inline byte initializer to `inlinehint` so LLVM could retain and recognize a bulk-copy loop. More inlining is not uniformly better. Preserve logical file position, EOF, short reads and mixed-mode behavior.

`4c1bf0f` and `f74ef7b` reduced wide-integer parser overhead with a mathematically safe decimal prefix and a limb/carry multiply-by-ten-plus-digit form. Generated code showed why two wide arithmetic expressions duplicated carry work. Keep the existing overflow/sign proof, malformed-input behavior and boundary tests; do not treat a native limb formulation as universally faster without checking the target output.

## Check allocation size classes and final dependencies

`e06541f` derived ordinary arena payload from the cached runtime bucket minus block metadata. The previous payload plus header exceeded the cache threshold. Inspect the **actual allocation request including metadata/alignment**, not the named payload size. Preserve alignment, large/over-aligned fallbacks and reset/free semantics.

`296741e` showed that an apparently allocation/cleanup-sized gap could include unnecessary loader work. Compiler-inferred libm requirements were retained until final link, where unused ones became conditional without changing explicit library intent or surrounding linker state. Package provenance distinguished inferred and explicit dependencies. Test actual `DT_NEEDED` and a live dynamic math input in direct and source-free consumers; a folded sine constant is not a live dependency test.

## Preserve observable transitions when versioning loops

`b6d76ac` added Queue.Reserve's no-growth path and split ring growth into two contiguous copies, removing per-element wrapping. `3160d13` then versioned eligible scalar dequeue loops using the concrete specialization and a loop-invariant relational guard. Each dequeue still updated length/head/output, including the empty reset and early exits. The original loop remained the fallback.

The rejected shortcut was a checksum-specific drain API or a bulk final update that changed intermediate queue state. Tests exercised wrapped/contiguous states, mutation and opaque-call exclusions, early stops and package consumers. `e14e39b` repaired owned out-argument/drop semantics exposed in this work: lifecycle correctness is part of the optimization boundary, not an optional follow-up.

## Generalize the proof, never the benchmark's spelling

`eb66444` combined short UTF-16 line writes but also introduced runtime checks for the benchmark's particular strings. **That part was a mistake.** `1292be1` removed those checks, retained general combining, and widened every supported ASCII-range input directly using the range proof instead of a redundant conversion pass.

`075131a` implemented arbitrary constant UTF-16 pre-encoding with immutable provenance, exact slice/byte/scalar payloads, first-class static arrays, target-typed pointers, encoding guards, helper reachability and source-free packages. The runtime path remained for dynamic/unproven input. Supplementary scalar decoding and source indexing also required correction; compare against an independent byte oracle rather than only a baseline sharing the defect. Preserve partial/chunked-write behavior and use the strongest valid `unsafe finite` helper kind.

`87d9d07` tightened overload identity/signature handling in specialization. A matching helper name is insufficient: argument/receiver/return types and indirect-address facts must survive retargeting, with ambiguous shapes left unchanged.

## Distinguish local scratch from an API-level buffering choice

`47eebd9` combined private uninitialized scratch arrays, compile-time UTF-8 output, and an **explicit** buffered writer. Scratch optimizations initialized the complete exposed prefix and did not narrow broad syscall memory effects. Buffered output had checked flushing, sticky failures and no hidden destructor flush. A separately named C/Rust/Stark buffered benchmark preserved matched logical operations and output.

The preceding controls showed that annotating a syscall read pointer/length did not give LLVM sufficient dead-tail read-extent information and did not remove clears. Avoid constructing an unnecessary initialized object before adding a general DSE mechanism. Test arbitrary lengths, NUL/non-ASCII/invalid scalars, short/zero/error writes and flush boundaries.

## Fix ABI facts and preserve private representation freedom

`0d44ab6` added first-bucket success, private counter widening and explicit capacity planning. Widening the **private induction** retained source-width data conversions, ranges, public layout and ABI; changing the benchmark's declared type would not have implemented that compiler proof. Outlining the collision fallback also recovered a values-pointer register lost by excessive inlining. Wrapped/zero-key tests exposed a related grouped-probe correctness defect.

`7ff411a` corrected referent alignment and local borrow aliases, retained direct TryGet success, and used the shared recursive `IsCopyable` proof for bulk Clear. A borrow pointer's alignment did not justify an eight-byte load from a four-byte key. Correct LLVM alignment enabled argument promotion and removed key-slot stores. Copyability included nested ownership/destructors; public reusable Clear still cleared states and owned values still dropped.

## Reproduce a diagnostic advantage in production

The corrected combined DictionaryLookup diagnostic measured better than its first production implementation. Disassembly found the same hot instructions/registers at different addresses. Relinking the exact production native object with unexecuted prefix padding reproduced effects of that size. Layout had become a demonstrated cause, not an excuse based solely on noisy timings.

`7ff411a` shipped a general x86 **branch-boundary** policy after controls found loop-header alignment alone ineffective. The policy was applied at native emission and the actual ThinLTO link; Darwin/MSVC needed explicit forwarding. A complete instruction-offset sweep checked real branch/pair boundaries, with native behavior and other-target compilation checks. Additional queue/console measurements checked for regressions. The final source-built result was 0.984 × C over 1,000 runs; this historical point estimate is evidence for that build/host, not a universal CPU claim.

Other negative controls mattered: deleting the capacity mask or bypassing destructor-time clearing could reduce work yet worsen native timing. Keep the unresolved capacity proof as a separate opportunity; do not manufacture a constant-capacity assumption or fixed benchmark padding to chase a number.

Before attributing a small timing difference to a patch, compare the binaries' allocated code/data sections and addresses, allowing for nonexecuted build-ID/debug differences. Identical executable content provides no evidence of a generated-code improvement. Conversely, matching instruction counts alone do not establish identical placement or register allocation. Preserve the negative result in the tracker instead of selecting a favorable window.

## Reduce allocation overhead without losing region and layout proofs

`d84e74a` replaced Dictionary's three allocations with one allocation containing states, keys and values. The checked layout included independent alignment padding, each region's extent, the combined-size overflow and the maximum required base alignment. Existing entries moved before the old storage was freed; failed planning preserved the old table. An overflow case where each region fits but the sum does not caught a boundary that separate size checks would miss.

This representation change needed a **subregion** disjointness contract. Keys and values share an allocation identity but occupy nonoverlapping byte ranges. Treating them as distinct allocations to obtain LLVM `noalias` would be an invalid substitute. Preserve the checked region proof at calls that move entries into the new storage.

An over-aligned key/value regression also exposed a pre-existing compiler defect: semantic layout included tail padding, but the LLVM struct used its smaller natural size, so indexed addresses had the wrong stride. The shared physical-layout decision was repaired to include explicit alignment, rather than adding a Dictionary-only workaround. First-emission layout checks plus native multi-entry, growth and mutable-borrow tests covered both representation and behavior; cross-target LLVM checks covered the ABI-dependent lowering.

## Express the fast-path proof where the compiler can retain it

`d84e74a` expressed Dictionary insertion's no-growth condition as `length < capacity / 2`. That single relational guard proves nonzero capacity and room for an entry without overflowing an increment; an empty home then proves absence. The hot path needs one hash and three stores, while an outlined worker retains collisions, replacement and growth. Reuse the computed hash in the fallback instead of recomputing it.

The proof does not allow reusing a home index after growth: a key's home changes with the capacity mask. Tests deliberately used a key whose home changes across growth, updates at the load-limit boundary, nested owned values and owned-key replacements. Check those paths even when the benchmark only inserts distinct scalar keys. Outlining is a measured code-generation choice, not a universal rule that every helper should be `noinline`.
