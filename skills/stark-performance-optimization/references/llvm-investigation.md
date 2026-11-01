# LLVM, native code, and compiler-source investigation

## Capture the actual pipeline

From the Stark checkout, choose the existing benchmark and tools explicitly. This example is for Linux x86-64/LLD; use the selected SDK's real target and platform-specific link options elsewhere. Keep inspected and timed executables identical, or demonstrate the copy's equivalence.

```bash
case_source=benchmarks/collections/DictionaryLookup
artifact_dir="$(mktemp -d /tmp/stark-perf-XXXXXX)"
./stark "$case_source.stark" --emit-exe \
  --target x86_64-unknown-linux-gnu -I stdlib/src --no-stark-path \
  --save-temps "$artifact_dir/stark-ir" \
  --link-arg=-Wl,--save-temps -o "$artifact_dir/stark"
clang -O3 -DNDEBUG -std=c17 "$case_source.c" -o "$artifact_dir/c"
clang -O3 -DNDEBUG -std=c17 -S -emit-llvm \
  "$case_source.c" -o "$artifact_dir/c.ll"
rustc -C opt-level=3 -C debug-assertions=no -C overflow-checks=no \
  --emit "llvm-ir=$artifact_dir/rust.ll,link=$artifact_dir/rust" "$case_source.rs"
rg --files --no-ignore "$artifact_dir"
```

Record resolved tool paths/versions, SDK identity and all flags rather than assuming PATH selects the copies in this repository. Check `--explain-modules` when source/package resolution is uncertain. A source-free test must actually remove/exclude the source fallback; an archived diagnostic package can shadow the intended library. Cross-target builds need a target-compatible SDK.

Read the launcher before using it to test a compiler edit. The repository's `./stark` is a wrapper around an existing Release `compiler.dll`, not a build command. Rebuild the appropriate compiler project/configuration first, then record the DLL hash with the native executable hash. If output is unexpectedly unchanged, check this provenance before diagnosing a failed optimization.

`-I stdlib/src` alone does not prove that those sources override an active SDK's System modules. For an isolated library control, select its SDK explicitly with `--sdk-root` and verify module provenance. C/Rust IR may come from different LLVM versions or pipeline stages; record that distinction and do not interpret different IR spelling or pass order as a language limitation. Use compatible LLVM tools to read each artifact.

`--save-temps` preserves initial module LLVM and inputs. Linker save-temporaries expose additional stages. Names vary by linker/platform: inspect what was produced. On the branch's ELF ThinLTO builds, `root.o` was bitcode, `.4.opt.bc` and `.5.precodegen.bc` were later LLVM stages, and `*.lto.root.o` was the final native object. Identify file formats before selecting tools; `root.o` is not necessarily native.

```bash
file "$artifact_dir/stark-ir/root.o"
llvm-dis path/to/optimized.bc -o "$artifact_dir/optimized.ll"
opt -passes=verify -disable-output "$artifact_dir/optimized.ll"
objdump -d --no-show-raw-insn --disassemble=main "$artifact_dir/stark"
llvm-objdump -d path/to/native-object-or-executable
readelf -d "$artifact_dir/stark"
sha256sum "$artifact_dir/stark" "$artifact_dir/c" "$artifact_dir/rust"
```

Use `llvm-objdump` where the host `objdump` cannot read a foreign format. `readelf` applies to ELF; use equivalent platform tooling elsewhere. Do not run rewriting/extraction tools in-place on a measured executable: even an apparently dump-only `objcopy` invocation can rewrite its input. Work on a copy and recheck hashes.

For Stark fact tracing, use the structured host-test protocol to stop after a relevant pass and inspect artifacts. Read `skills/stark-language/references/compiler-test-harness-reference.md` and `docs/Internals/CompilerDevelopmentVerification.md` in the repository for the current request schema. Prefer structured diagnostics/artifacts to scraping CLI prose. Locate producers/recomputations with `rg`; do not assume a specialized body is absent just because caller SSA still contains a call.

## Compare the right evidence

| Layer | Questions |
| --- | --- |
| Source/API | Are algorithms, outputs, capacities, buffers, error semantics and ownership comparable? Is work already constant? |
| Semantic/MIR | What is known about lifetime, effects, ranges, target layout and implicit cleanup? Are out values initialized on the right status edges? |
| SSA/facts | Where does a literal, allocation, capacity or relational proof become unknown? Did a rewrite/reanalysis omit effects or source storage type? |
| Initial LLVM | Are facts expressed correctly: widths, alignments, captures, memory effects, attributes, static arrays, branches and call arguments? |
| Optimized/post-LTO LLVM | Did argument promotion, inlining, DSE, loop idioms, vectorization and interprocedural specialization actually occur? |
| Native executable | What executes per iteration? Are there reloads, spills, extra branches, calls, initialization, cleanup or boundary-sensitive instruction placement? |
| Final link/runtime | Which libraries load? How much startup/allocation/I/O remains? Are syscalls buffered or combined? |

Count calls/stores/bytes by their execution frequency and paths. One static `memcpy` in a loop may mean 20,000 calls; four large memset sites may clear megabytes across iterations. A vector instruction in a cold fallback is not evidence that the hot success loop vectorized. Inspect branch targets and loop-carried state.

Keep raw IR alongside normalized comparisons. Ignore incidental value numbering or metadata numbering when comparing shape, but retain attributes, types, ranges, alias scopes, branches and call effects. Document useful work separately from scaffolding and instruction placement. Retain a checksum/output observer so the compared work is real; do not invent a volatile observer that changes the benchmark merely to keep otherwise unobservable work alive.

## Use compiler sources to explain a specific obstacle

Check local `benchmarks/compiler-sources/` before fetching anything. Identify which revision/tool the source describes; if they differ, treat it as a hypothesis until reproduced on the actual binary. Inspect relevant `AGENTS.md` before modifying any third-party checkout. Reading their compiler/library implementation does not require changing it.

Choose the source layer from the first code difference. Compare Stark's lowering with Clang's `clang/lib/CodeGen` or Rust's `compiler/rustc_codegen_llvm` when initial LLVM differs in ABI, pointer representation or attributes. Compare Rust MIR optimizations in `compiler/rustc_mir_transform` when the useful simplification already precedes LLVM. Inspect LLVM passes/backend when comparable initial IR diverges later. Library algorithms belong in the C implementation or Rust `library/` and delegated dependencies. Narrow the search to the operation/pass rather than reading whole compilers; retain source file, revision, relevant condition and the reproducer that tests it.

Useful searches and mechanisms confirmed during this branch:

| Symptom | Source to inspect | Discriminating experiment |
| --- | --- | --- |
| Read-only borrowed key still stored to a stack slot each call | LLVM `lib/Transforms/IPO/ArgumentPromotion.cpp`: `NeededAlign`, `allCallersPassValidPointerForArgument` | Compare load alignment with every caller's real referent contract. Isolate `cgscc(argpromotion)` on a copy. Correct alignment enabled scalar promotion; forcing pointer alignment would be invalid. |
| State-clear loop does not become memset | `lib/Transforms/Scalar/LoopIdiomRecognize.cpp`: store-block dominance of loop exits | Compare occupancy-conditional stores with a proven no-drop unconditional zero loop. Preserve reusable Clear and owned-entry destruction. |
| Dead payload or scratch tail remains written | `lib/Transforms/Scalar/DeadStoreElimination.cpp`, alias/mod-ref analyses | Distinguish actual byte reads/escapes from header access. A pointer plus length operand does not automatically give DSE a bounded read extent. |
| Similar IR but timing changes sharply | `lib/CodeGen/MachineBlockPlacement.cpp`, `lib/Target/X86/MCTargetDesc/X86AsmBackend.cpp` | Relink the same native object with controlled placement and correct relocations. Compare instructions/registers/internal control flow, not just total instruction count. |
| Backend option appears at compilation but final code is unchanged | Clang `lib/Driver/ToolChains/CommonArgs.cpp`, platform driver and LLD option forwarding | Inspect actual driver/link arguments and post-LTO native code. ELF forwarding does not establish Darwin/MSVC forwarding; assembler settings need not survive bitcode. |
| Rust HashMap time differs greatly | Rust `library/std/src/collections/hash/map.rs`, its delegated hash-table implementation and emitted code | Separate hashing/probing/library choices from compiler quality. Compare a matched algorithm only as an explicitly labeled additional control. |
| Expensive wide arithmetic | LLVM target lowering plus the C/Rust output for equivalent checked arithmetic | Test an isolated limb/carry formulation under the existing overflow proof; verify boundaries and native carry chains. |

Pass diagnostics are optional tools, not a new production pass order. On copies, use the selected `opt`'s supported `-print-before`, `-print-after`, `-filter-print-funcs` or `-debug-pass-manager` options to identify the stage. A second global O3, disabled vectorization, or a huge unroll threshold can expose an opportunity without being the right fix. Replaying optimized bitcode through another pipeline changes the experiment; keep its own unchanged relink baseline.

## Proofs that the verifier will not supply

- Header `Data` loaded into a local and then dereferenced can access reachable memory outside the pointer argument's `argmem`. Test caller and reader in separately optimized objects.
- A borrow's pointer representation can be 8-byte aligned while the referent only promises 1/2/4-byte alignment. Materialization must resolve aliases to the referent, not load pointer-slot bits as the key.
- An `IsCopyable`/no-drop result must come from the shared recursive ownership analysis, including nested fields and enum variants. No user destructor alone is insufficient.
- Correct field offsets alone do not establish correct aggregate size or indexed stride. Explicit over-alignment needs tail padding in the physical representation; exercise multiple elements and growth, not only element zero.
- Region disjointness can hold inside one allocation. Derive it from checked byte ranges and alignment, rather than strengthening an allocation-identity or LLVM `noalias` claim.
- Removing a read, branch, mask or write in an LLVM control is not proof it is unobservable or in range. Preserve failure/status, byte observers and unsupported dynamic cases.

These cases require behavior and contract tests in addition to `opt -passes=verify`.
