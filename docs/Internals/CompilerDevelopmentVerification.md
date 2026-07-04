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
- Search-dir caveat: search dirs enumerate `*.starkpkg` recursively, and the
  root file's own directory joins the search. A package image under any
  search root poisons the gate — either loudly (STK7312 target
  incompatibility) or silently (a stale image SHADOWS fresh source and
  reports phantom errors against old code, e.g. old function kinds). If a
  gate reports errors that contradict the source you just read, look for a
  stale `*.starkpkg` under the search roots (including the root file's
  project `build/` directory) and delete it — it is build output.

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
   and `build/dev/<triple>/stage0/bin/stark-compiler/libStarkCompiler.a`.
2. Move or copy the `stage0/pkg` + `stage0/bin` pair out of `selfhost/`
   preserving the relative layout (the package records its static library by
   relative path), for example to a scratch directory.
3. Compile the probe against the image:

```
./stark selfhost/probe/MemberFactsProbe.stark --emit-exe \
    -I <scratch>/stage0/pkg --no-stark-path \
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
docs/Self-host-Prep/30-test-progress-streaming.md.

## Diagnostic Probes

For investigations, prefer a compiler-exported probe function that renders an
internal fact table as text over ad-hoc prints: for example
`ProbeSourceMemberPathFacts(source, sink)` renders the typed member rows and
the prevalidated member-path facts one line per row. A field-level rendering
probe is the difference between "the build succeeded" and "the value decoded
correctly" — Ok/Err alone does not show wrong decoded values.

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
| From-source probe | ~12 min | runtime behavior incl. fresh lowering |
| Targeted `stark test --filter` slice | minutes (compile-dominated) | ported-fact behavior |
| Full C# host suites | 15–47 min | host regression surface — run only when asked |
