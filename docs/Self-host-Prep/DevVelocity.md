# Development Velocity Improvements

Short-lived tracker. Delete once all tasks in section 2 are complete and the
mechanisms are documented in `docs/Internals/`.

## 1. Issues And Resolutions

### 1.1 No mechanical stage0-vs-stage1 oracle

**Issue.** Stage1 correctness is verified fact-by-fact against hand-ported
tests. That is O(n) human effort per behavior, and host-vs-selfhost
divergences surface late — the "compare stage outputs" gate is currently
scheduled at cutover, after all porting is done.

**Resolution.** A checked-in differential harness: lower the same source
module with stage0 and with stage1, normalize the emitted text (strip
metadata ids, unify mono prefixes and linkage — the prototype normalizer
from the package-mode debugging already does this), and diff per function.
Corpus starts as the existing selfhost.Ir fixtures and grows toward stdlib
modules. A slice's primary verification becomes "these corpus files diff
clean"; hand-written facts remain as intent pins, not as the coverage
mechanism.

### 1.2 Stale package artifacts silently shadow the source under test

**Issue.** The implicit CWD/root-file-dir recursive `*.starkpkg` search
silently wins over `-I` source paths. A "from-source" probe that links a
stale package looks identical to an honest one except for runtime (~10 s vs
~12 min). This has invalidated whole experiment rounds at least four times,
costing hours each.

**Resolution.** Make artifact identity self-evident instead of
discipline-dependent: (a) `--explain-modules` prints one provenance line per
resolved module — package path plus content hash, or source file (opt-in
because successful compiles keep stderr clean by contract; many tools and
tests assert on it); (b) a warning fires unconditionally whenever an
implicitly discovered package (root-file dir, STARK_PATH) shadows a module
also reachable through an explicit `-I` source path — that warning, which
includes the package content hash, is the load-bearing trap protection.

### 1.3 Invalid or self-contradictory LLVM output is only caught by distant misbehavior

**Issue.** `dereferenceable(0)` receivers and the sret fresh-scope
`!noalias` contradiction were both mechanically detectable classes, but
nothing inspects emitted modules. Each surfaced as far-downstream
misbehavior; the sret bug cost roughly two weeks of archaeology.

**Resolution.** Post-emission validation in debug/test builds: run the LLVM
verifier over every emitted module, plus a custom metadata lint for
invariants the verifier does not know (a call's `!noalias` list must not
contain its own result's fresh scope; `dereferenceable`/`initializes`
extents must be positive and within the object). Standing rule going
forward: every deep bug closes with a retroactive invariant check that
would have caught it at first emission.

### 1.4 Package rebuilds cost 19–30 minutes per selfhost iteration

**Issue.** Any selfhost change that lives in the package costs a full
rebuild to verify honestly (package-backed probes are 15 s but test the
stale package otherwise). Effective iteration rate on stage1 work is 2–4
per hour.

**Resolution.** Content-keyed per-module compile caching. Shipped first cut:
the dependency LLVM cache (`DependencyLlvmCache`) — a dependency module's
emission is keyed by its source, its transitive import closure's sources
(or package-image content hashes), the codegen-relevant options, the
inline-clone seed set, and the compiler binary stamp, and cached under
`~/.stark/dep-llvm-cache`. Changed modules and their dependents recompile
(their closures changed); everything else reuses the cached emission.
`STARK_DEP_CACHE_VERIFY=1` recompiles every hit and fails loudly on any
byte difference. This removes the per-dependency pipeline re-runs that
dominate from-source probe compiles and exe/test builds. Root-module
pipeline incrementality (the remaining cost of single-huge-library package
builds like selfhost's) is the follow-on, tracked in TASKS.md §3.

## 2. Task Tracker

Differential harness (1.1):

- [x] Extract the LLVM normalizer into a checked-in tool — `LlvmTextNormalizer` (attribute/metadata stripping + register/label renumbering + per-function diff), unit-tested
- [x] Add a harness entry point that lowers one module via stage0 and stage1 and diffs normalized output per function — `selfhost/tools/DifferentialDriver` + `StageParityTests`: execution parity is the gate, the normalized skeleton diff is attached on failure; first green run 2026-07-08 (2/2 supported corpus files; the run also surfaced and closed a host emitter invalid-IR bug and filed 4 stage1 whole-module gaps, TASKS.md §1)
- [x] Seed the corpus from the existing selfhost.Ir fixtures — `tests-stark/corpus/` (6 files spanning arith/call, if, unit-enum switch, payload capture, enum-valued return, struct fields; all validated against stage0 with distinct exit codes)
- [x] Wire a "corpus parity" test entry runnable as a targeted slice — `dotnet test tests/compiler.IntegrationTests --filter StageParityTests` (STARK_STAGE_PARITY=1 makes a missing driver a failure instead of a skip); green 2/2. Files stage1 cannot accept yet sit in `tests-stark/corpus/pending/` with the blocking family documented and move up as families land
- [x] Corpus growth mechanism established: every new stage1 lowering family lands with corpus files (documented in the harness class and the Stark skill); growth itself is continuous

Stale-artifact provenance (1.2):

- [x] Print a per-module resolution provenance line (package path + content hash, or source file) — `--explain-modules`
- [x] Warn when an implicit root-dir/STARK_PATH package shadows an explicit `-I` source module (always on, includes package hash)
- [x] Cover both behaviors with CLI tests — `tests/compiler.Tests/ModuleResolutionProvenanceTests.cs` (5 tests)

Post-emission validator (1.3):

- [x] Run the LLVM verifier over emitted modules — `STARK_LLVM_VERIFY=1` runs `opt -passes=verify` per emission (opt-in: subprocess cost is wrong for the default inner loop); covered by an always-on test when `opt` is present
- [x] Lint: a call's `!noalias` list must not contain its own result's fresh scope — `LlvmModuleLint`, on by default in debug builds, STK5004
- [x] Lint: `dereferenceable`/`initializes` extents are positive and well-formed (within-object needs layout knowledge the text lint lacks; the emitter-side zero-size guard covers the known case)
- [x] Adopt the closure rule: every deep bug lands an invariant check with its fix — documented in docs/Internals/CompilerDevelopmentVerification.md

Incremental package builds (1.4):

- [x] Define the per-module cache key — `DependencyLlvmCache.ComputeKey`: module source + transitive import closure sources/package hashes + target/options fingerprint + inline-clone seeds + compiler binary stamp
- [x] Cache and reuse per-module emissions during dependency builds (default on, `~/.stark/dep-llvm-cache`; STARK_DEP_CACHE=0/<dir>, STARK_DEP_CACHE_LOG=1)
- [x] Invalidate dependents on module change; byte-identical gate via STARK_DEP_CACHE_VERIFY=1 (recompiles every hit, throws on any difference) — covered by `DependencyLlvmCacheTests`
- [x] Measure cold vs warm (2026-07-08, M5 Mac): full selfhost package + differential-driver rebuild from a clean `build/` — **1709s (28.5 min) cold → 290s (4.8 min) warm, 213/213 dependency-emission cache hits (5.9×)**. The warm remainder is the root pipeline, per-module `clang -c`, and archive/link; caching the `.o` next to the `.ll` and root-pipeline incrementality (TASKS.md §3) are the follow-on levers. Small stdlib apps see 8s → 3.8s. End-to-end selfhost *probe* measurement stays blocked by the §6 empty-`List<MirEnumLayoutFact>` bug (its root module fails before the dependency sweep)
