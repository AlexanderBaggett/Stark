# Compiler Development Verification

How to verify compiler changes quickly and honestly while the C# host and the
self-hosted Stark compiler coexist. The goal is a small set of repeatable
workflows with known costs, so verification evidence means the same thing
from one change to the next.

## The `--check` Gate

`--check` runs the full front end through `ownership-validate` (see
[CompilerPipeline.md](CompilerPipeline.md), "Check Mode") and also sweeps
every source-backed dependency module with its own front-end run, batching
failures per module. A clean check over a root therefore proves the whole
imported source graph passes every acceptance decision the compiler makes.

The standing selfhost gate:

```
./stark selfhost/Compiler/Mir.stark --check \
    -I selfhost -I stdlib/src --no-stark-path \
    --target arm64-apple-macosx26.0.0 2>&1 \
  | grep -E "error STK|Failure summary"
```

- Zero matches is a pass. The grep keeps multi-minute output readable; the
  slow-pass heartbeat (`Pass 'X' took N.Ns`, see
  [CompilerLoggingDesign.md](CompilerLoggingDesign.md)) shows progress if you
  drop the grep.
- Cost: roughly 2–3 minutes over the selfhost + stdlib graph (parallel
  dependency sweep).
- Scope caveat: `--check` proves acceptance, not behavior. Lowering and
  emission changes need a probe or a ported fact as runtime evidence.
- Search-dir caveat: direct source checks resolve source modules before
  recursively considering `*.starkpkg` images, so stale package artifacts under
  broad roots no longer substitute for source files. Package images still
  participate when no source module exists, and package-backed probe compiles
  are still snapshot-based, so use `--explain-modules` if a diagnostic looks
  like it came from an artifact you did not intend to test.

## Standalone Probes

A probe is a small Stark executable that drives a compiler entry point over
inline sources and checks expectations, printing one line per check:

```stark
import Compiler.Mir
import System.Console
import System.Text
module Compiler.Probe.MemberFacts

fn u8[0 1] Check(ascii name, ascii source, bool expectOk)
{
    stack mut OwnedAscii sink = new();
    stack bool ok = CompileFunctionWithLocalsToLlvm(source, sink);
    if (ok == expectOk) { Write("ok   "); WriteLine(name); return 0; }
    Write("FAIL "); WriteLine(name); return 1;
}
```

Conventions that keep probes honest:

- Known-broken shapes stay in the probe as `expectOk=false` with a comment,
  so a later fix flips them loudly instead of silently.
- Probes live under `selfhost/probe/` and are compiled on demand; they are
  evidence tools, not shipped artifacts.
- A probe run is runtime evidence: it exercises lowering, emission, linking,
  and execution, which `--check` does not.
- An accept BOOLEAN is not lowering evidence. For at least one accepted
  shape per slice, dump the emitted module (`WriteLine(sink.View())`) and
  check it is well-formed — the enum-return slice's "ok" turned out to be
  an accepted `define unknown @main()` / `ret unknown` module (2026-07-04
  ledger entry), and only dumping the emission caught it.

## Package-Backed Probe Compiles

Compiling a probe from selfhost source costs ~12 minutes. Against a
prebuilt package image of the selfhost library the same probe compiles in
~15 seconds (measured 2026-07-01: 14.5 s wall, all 13 member-facts checks
passing). The recipe:

1. `cd selfhost && ../stark build` produces
   `build/dev/<triple>/stage0/pkg/stark-compiler/libStarkCompiler.starkpkg`
   and an adjacent `libStarkCompiler.a` package archive copy. The ordinary
   library output is still also emitted under
   `build/dev/<triple>/stage0/bin/stark-compiler/`.
2. Move or copy the `stage0/pkg/stark-compiler/` package directory out of
   `selfhost/`, for example to `<scratch>/pkg/stark-compiler/`. The package
   image records the adjacent archive file name, so the directory is
   relocatable by itself.
3. Compile the probe against the image:

```
./stark selfhost/probe/MemberFactsProbe.stark --emit-exe \
    -I <scratch>/pkg --no-stark-path \
    --target <triple> \
    --target-data-layout "<layout from ./stark --inspect-pkg <image>>" \
    -o <scratch>/probe
```

Sharp edges (tracked in TASKS.md tooling section):

- Raw `--target` runs derive a different LLVM data layout than project
  builds embed; pass `--target-data-layout` copied from `--inspect-pkg` or
  the image is rejected with STK7312.
- Pass `-I <pkg>` ONLY — do not add `-I stdlib/src`. The selfhost package
  embeds the `System.Process`/Platform modules (via `Compiler.TestDriver`),
  and mixing the package's stdlib subset with stdlib source breaks
  internal-symbol resolution in the swapped platform dispatch template.
  (Before the package embedded those modules, `-I stdlib/src` was needed
  for probe-only imports; the embedded subset now covers them.)
- Rebuild the PACKAGE after changing selfhost sources or the host
  compiler; the image and its static library are snapshots. A probe-only
  rebuild links the old `libStarkCompiler.a`, so it can never observe a
  compiler fix to package-side code — the bundle field-store "remaining
  layer" (TASKS.md §6, closed 2026-07-04) was exactly this ghost: the fix
  was in, the probe was fresh, and the archive was stale.

## Module Resolution Provenance

The classic staleness trap was a package found through the root file's own
directory, `STARK_PATH`, or a broad source root silently winning over fresh
source. Direct resolver selection is now source-first across all search roots:
a `Module.Name.stark` file wins before the resolver builds its recursive
package-image index. Package images are used only when no source module exists.

`--explain-modules` prints one provenance line per resolved module —
`module <name> <- package '<path>' (sha256:...)` or
`module <name> <- source '<file>'`. Use it whenever "which artifact holds the
code under test?" needs a tool answer instead of a guess.

Successful builds keep stderr empty unless `--explain-modules` is passed. JSON
diagnostics mode suppresses provenance output to keep stderr machine-parseable.

## Dependency LLVM Cache

Dependency-module emissions are cached content-addressed under
`~/.stark/dep-llvm-cache`: the key covers the module's source, the sources
(or package-image content hashes) of its transitive import closure, the
codegen-relevant options and target, the inline-clone seed set, and the
compiler binary's own stamp. A hit skips the module's entire pipeline run —
the dominant cost of from-source dependency builds. Wired into both the
executable dependency loop and the library/package dependency loop.

Measured (2026-07-08, M5 Mac): full selfhost package + differential-driver
rebuild from a clean `build/` went from 1709 s (28.5 min) cold to 290 s
(4.8 min) warm at 213/213 hits. The warm remainder is the root-module
pipeline, per-module `clang -c`, and archive/link; caching objects next to
the emissions and root-pipeline incrementality are the follow-on levers
(TASKS.md §3).

Knobs:

- `STARK_DEP_CACHE=0` disables; `STARK_DEP_CACHE=<dir>` relocates.
- `STARK_DEP_CACHE_VERIFY=1` recompiles every hit and fails the compile if
  the cached text differs from the fresh emission — the byte-identical
  gate. Run it when touching anything that feeds the key.
- `STARK_DEP_CACHE_LOG=1` prints per-invocation hit/miss counts to stderr.

Because the compiler binary is part of the key, rebuilding the compiler
rotates every key: the first build after a compiler change is always cold.
That is the safety property, not a defect — a rebuilt compiler must never
serve emissions cached by the previous binary.

## Stage0/Stage1 Differential Harness

Corpus files under `tests-stark/corpus/` are compiled and RUN by the host
compiler (stage0) and lowered by the self-hosted compiler via
`selfhost/tools/DifferentialDriver`; the driver's emitted module is built
with clang and run. Exit-code parity is the gate;
`LlvmTextNormalizer.DiffModules` attaches a normalized per-function skeleton
diff on failure. Run as a slice:

```
dotnet test tests/compiler.IntegrationTests --filter StageParityTests
```

`STARK_STAGE_PARITY=1` turns a missing driver binary into a failure instead
of a skip. Build the driver with `stark build` in
`selfhost/tools/DifferentialDriver` (one-time selfhost package build; warm
rebuilds ride the dependency cache).

Conventions: corpus files use the dialect both stages accept — semicolon
terminators, `export` on `main`, `main` as the file's last function (stage1
names functions ordinally, so the harness wraps the last zero-parameter
define as the entry point), signed ranges that span negative values. A
`STAGE1-REJECT` from the driver is a stage1 coverage gap, not a harness
defect: park the file in `tests-stark/corpus/pending/` with the blocking
family recorded in TASKS.md §1, and move it up when the family lands. Every
new stage1 lowering family should land with corpus files.

## Test Runner Progress

`stark test` streams runner output line-by-line as it happens (it never
buffers until exit), so partial output survives a kill. Two flags sharpen
timeout diagnosis:

- `--test-progress` passes `--progress` to the generated runner, which then
  prints `run <name>` before each fact and `ok|FAILED <name> (k/N)` after it
  (N counts the generated — post-filter — entries). The driver prefixes every
  forwarded line with elapsed wall-clock (`[12.3s]`). A hung or timed-out
  run's last `run <name>` line names the fact in flight, and successive
  prefixes give per-fact durations. Without the flag, output is byte-identical
  to the legacy `ok <name>` format.
- `--test-timeout <seconds>` kills the runner process tree at the deadline
  and reports the timeout explicitly instead of hanging the harness.

The protocol is a stage contract: the runner and `System.Testing`
(`BeginFact`, `RunFactCounted`) are Stark and shared by every stage. The
normative artifacts are the fixtures and byte-exact goldens under
`tests/fixtures/test-progress`, enforced by the
`TestProgressProtocolTests` integration harness. The stage1 components
(`Compiler.TestRunner` for generated-runner emission,
`Compiler.TestDriver` for streaming/prefix/timeout execution) are
golden-parity verified and pinned by `tests-stark/selfhost.TestRunner`;
the full design record is
[Per-Test Progress Streaming Protocol](TestProgressProtocol.md).

## Diagnostic Probes

For investigations, prefer a compiler-exported probe function that renders an
internal fact table as text over ad-hoc prints: for example
`ProbeSourceMemberPathFacts(source, sink)` renders the typed member rows and
the prevalidated member-path facts one line per row. A field-level rendering
probe is the difference between "the build succeeded" and "the value decoded
correctly" — Ok/Err alone does not show wrong decoded values.

## Emitted-Module Lint And Verifier

Every debug-build pipeline run lints the emitted LLVM module text
(`LlvmModuleLint`, reported as STK5004 errors from `emit-llvm`): invalid
attribute extents (`dereferenceable(0)`, empty/inverted `initializes`
ranges) and scoped-noalias self-contradictions (a call's `!noalias` list
containing its own result's fresh scope — the 2026-07 sret heisenbug class).
`STARK_LLVM_LINT=0` disables it, `=1` forces it in release builds. Set
`STARK_LLVM_VERIFY=1` to additionally round-trip every emitted module
through the real LLVM verifier (`opt -passes=verify`; override the binary
with `STARK_LLVM_OPT`) — right for CI, harness runs, and deep
investigations; too slow as a default for the inner test loop.

**Deep-bug closure rule:** a deep bug is not closed until an invariant check
that would have caught it at first emission lands with the fix. If the bug
class is mechanically detectable (invalid IR, self-contradictory metadata,
impossible fact-table states), add it to `LlvmModuleLint` or the relevant
validator in the same change; the regression test pins the instance, the
lint kills the class.

## Classifying Failures Against HEAD

Before attributing a failure to your change, reproduce it on an unmodified
tree. Use a worktree branched from the current HEAD (not from `main`):

```
git worktree add -b verify-head <path> HEAD
```

Run the same gate, suite slice, or probe there. "Fails at HEAD too" turns a
suspected regression into a pre-existing gap to record (TASKS.md or the
relevant ledger) instead of a blocker. Remove the worktree when done.

## Cost Cheat Sheet

| Evidence | Cost | Proves |
| --- | --- | --- |
| Widened `--check` gate | ~2–3 min | whole-graph front-end acceptance |
| Package-backed probe | ~15 s + one-time ~20 min build | entry-point runtime behavior |
| From-source probe | ~12 min cold; dependency sweep collapses on warm dep-cache | runtime behavior incl. fresh lowering |
| Selfhost package/driver rebuild | ~28 min cold, ~5 min warm dep-cache (compiler rebuilds rotate keys → cold) | whole-selfhost emission + link |
| Stage parity slice (`--filter StageParityTests`) | seconds once the driver is built | stage0/stage1 behavioral parity on the corpus |
| Targeted `stark test --filter` slice | minutes (compile-dominated) | ported-fact behavior |
| Full C# host suites | 15–47 min | host regression surface — run only when asked |
